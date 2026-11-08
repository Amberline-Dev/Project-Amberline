using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 660 (community reports of 3 November 2026: "turrets attack too fast", "Dominion minions attack WAY TOO FAST"): the swings a unit
/// makes per second in the production runtime, against the period its own character record gives through the stock client's delay helper
/// (docs/gameplay/patch126-attack-speed.md: clamp(1.6 x (1 + Data*AttackDelayOffsetPercent) / multiplier, 0.4, 5)). The expectations are
/// computed here from the shipped inibins, not from the production tables. A swing is the step its attack timer restarts; the 30 Hz step
/// rounds each interval up to whole steps.
/// </summary>
public static class AttackCadenceChecks
{
    private const double Step = BbM3.Step;

    private static double RecordPeriod(string character, double multiplier = 1.0)
    {
        var raw = GameData.Default.Champion(character).Raw;
        var offset = raw.TryGetValue(InibinReader.KeyHash("Data*AttackDelayOffsetPercent"), out var value) && value.IsNumeric
            ? value.AsClientNumber() : 0.0;
        return Math.Clamp(1.6 * (1.0 + offset) / multiplier, 0.4, 5.0);
    }

    private static double RecordCastFraction(string character)
    {
        var raw = GameData.Default.Champion(character).Raw;
        var offset = raw.TryGetValue(InibinReader.KeyHash("Data*AttackDelayCastOffsetPercent"), out var value) && value.IsNumeric
            ? value.AsClientNumber() : 0.0;
        return Math.Max(0.0, 0.3 + offset);
    }

    /// <summary>The mean interval between the swings <paramref name="attacker"/> starts while <paramref name="swing"/> is asked every step.</summary>
    private static double MeanInterval(BbM3.Match match, Entity attacker, double seconds, Func<bool>? swing = null)
    {
        var starts = new List<double>();
        var cooldown = attacker.AttackCooldown;
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++)
        {
            swing?.Invoke();
            match.Advance(Step);
            if (attacker.AttackCooldown > cooldown + 1e-9) starts.Add(match.Now);
            cooldown = attacker.AttackCooldown;
        }
        TestAssert.True(starts.Count >= 4, $"{attacker.Name} swings at least four times in {seconds} s (it swung {starts.Count} times)");
        // Batch 670: from the second swing. The first can be this loop's own call, made between steps, whose cooldown the next step already
        // counts down; a unit with its own AI (the lane minion) then swings inside the step, so that first interval was one step short.
        return (starts[^1] - starts[1]) / (starts.Count - 2);
    }

    private static void AssertCadence(string label, double period, double interval)
    {
        // The step rounds a period up to whole steps: the measured interval is the first whole step at or after the period. Batch 670: a period of
        // whole steps (the shrine's 0.4 s, 12 steps; the melee minion's 0.8 s; the tutorial turret's 1.0 s) takes exactly that many (batch 660
        // allowed one step more, which the countdown's float residue added).
        var steps = Math.Ceiling(period / Step - 1e-6) * Step;
        TestAssert.Near(steps, interval, 1e-6,
            $"{label}: one swing every {interval:0.0000} s, the record's period {period:0.0000} s ({1.0 / period:0.000} attacks per second) is {steps:0.0000} s in whole steps");
    }

    /// <summary>
    /// The Crystal Scar's wave units (SmallWave.json: Odin_*_Minion_Caster and Odin*Superminion) spawn through the encounter path
    /// (EncounterService, BbNativeHandlers.SpawnEncounterUnit), which gave them no attack timing: period 0 and windup 0, so they swung on
    /// every 30 Hz step (the report's "insta kill"). The stock CharacterData loader gives every character its record's timing, so a wave
    /// unit swings at its record's period and lands at its cast fraction of it.
    /// </summary>
    [Check("combat.dominion-wave-units-swing-at-their-record-period")]
    public static void DominionWaveUnitsSwingAtTheirRecordPeriod()
    {
        using var match = new BbM3.Match("Ryze");
        var unitScripts = new BbUnitCharacterModule(match.Runtime.Spells.Data);
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        ((BbHookFanout)hooks.ScriptHooks!).Add(unitScripts.Bind(match.Runtime.Spells, _ => { }));
        foreach (var (skin, team) in new[]
                 {
                     ("Odin_Blue_Minion_Caster", Team.Order), ("Odin_Red_Minion_Caster", Team.Chaos),
                     ("OdinBlueSuperminion", Team.Order), ("OdinRedSuperminion", Team.Chaos),
                 })
        {
            var minion = unitScripts.SpawnEncounterUnit(skin, new Position2(7000, 7300), team, UnitType.Minion)!;
            var period = RecordPeriod(skin);
            TestAssert.Near(period, minion.AttackPeriod, 1e-9, skin + " period from its record");
            TestAssert.Near(period * RecordCastFraction(skin), minion.AttackWindup, 1e-9, skin + " windup from its record");
            var target = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
                team == Team.Order ? Team.Chaos : Team.Order, new Position2(7060, 7300)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
            AssertCadence(skin, period, MeanInterval(match, minion, 10.0, () => match.Runtime.Combat.QueueBasicAttack(minion, target)));
            minion.Alive = false;
            target.Alive = false;
        }
    }

    /// <summary>
    /// A Summoner's Rift outer turret, run by its shipped Turret.luaobj on a champion in its range: one swing per its record's 1.2 s
    /// (AttackDelayOffsetPercent -0.25) over 1.3, the +0.3 IncPermanentPercentAttackSpeedMod of PersonalTurretAssistBonus (added once,
    /// by TurretAssistManager from the turret's CharOnActivate): 0.923 s, 1.083 attacks per second. A lane melee minion swings at its
    /// record's 0.8 s. Both are what the shipped data gives; the check pins that the live cadence adds nothing to it.
    /// </summary>
    [Check("combat.summoners-rift-turret-and-minion-swing-at-their-record-period")]
    public static void SummonersRiftTurretAndMinionSwingAtTheirRecordPeriod()
    {
        using var match = new BbM3.Match("Ryze");
        var turret = match.Runtime.World.Entities.Values
            .Where(unit => unit.Team == Team.Chaos && unit.UnitType == UnitType.Turret &&
                           MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Outer)
            .OrderBy(unit => unit.Name, StringComparer.Ordinal).First();
        var character = MapDefinitions.TurretCharacter(TurretTier.Outer, Team.Chaos);
        var hero = match.Hero;
        hero.MaxHealth = hero.Health = 1e7;
        // Toward the map's centre, inside the 750 attack range; the other heroes stay out of the way.
        var dx = 7000.0 - turret.Position.X;
        var dz = 7000.0 - turret.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        hero.Position = new Position2(turret.Position.X + dx / length * 450.0, turret.Position.Z + dz / length * 450.0);
        foreach (var other in new[] { match.Ally, match.Foe, match.FarFoe })
            other.Position = other.Team == Team.Order ? new Position2(400, 400) : new Position2(13900, 14000);
        AssertCadence(turret.Name, RecordPeriod(character, 1.3), MeanInterval(match, turret, 12.0));

        var minion = match.Runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        minion.Position = new Position2(7000, 7000);
        minion.Waypoints.Clear();
        var dummy = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(7060, 7000)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
        AssertCadence(minion.Name, RecordPeriod(minion.Name),
            MeanInterval(match, minion, 8.0, () => match.Runtime.Combat.QueueBasicAttack(minion, dummy)));
    }

    /// <summary>
    /// Batch 670 (owner, 3 November 2026: "Check turret attack speed on all maps"): every turret CreateLevelProps makes on every shipped map
    /// (Summoner's Rift, the Harrowing rift, the tutorial, Twisted Treeline, the Crystal Scar), run by the production composition (its character
    /// script and Turret.luaobj), swings at its record's period over the multiplier its shipped character script gives it. The multiplier is
    /// 1.3 when the character's CharOnActivate adds TurretAssistManager, which adds PersonalTurretAssistBonus to the turret itself
    /// (IncPermanentPercentAttackSpeedMod +0.3, batch 660); 1 otherwise (the shrines, the Crystal Scar's shrines, the tutorial's two turrets,
    /// whose CharOnActivate adds only TurretDamageManager). The expectation reads the shipped inibin and the shipped character script, not the
    /// production tables. Each turret has a 1e7-health enemy minion 450 units toward the map's centre; the window ends before any wave arrives.
    /// The Crystal Scar's capture point guardians are DominionGuardianChecks' (they need a captured point).
    /// </summary>
    [Check("combat.turrets-on-every-map-swing-at-their-shipped-rate", Serial = true, TimeoutSeconds = 600)]
    public static void TurretsOnEveryMapSwingAtTheirShippedRate()
    {
        const double Window = 12.0;
        try
        {
            foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.Tutorial, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
            {
                var paths = GameDataPaths.Default.ForMap(mapId);
                using var runtime = StartTwoSeatMatch(mapId);

                var layout = MapDefinitions.Layout;
                var centre = new Position2((layout.OrderSpawn.X + layout.ChaosSpawn.X) / 2, (layout.OrderSpawn.Z + layout.ChaosSpawn.Z) / 2);
                var turrets = runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret && layout.TurretTiers.ContainsKey(unit.Name))
                    .OrderBy(unit => unit.Name, StringComparer.Ordinal).ToArray();
                TestAssert.Equal(layout.Turrets.Count, turrets.Length, $"map {mapId}: every CreateLevelProps turret stands");
                foreach (var turret in turrets)
                {
                    var (dx, dz) = (centre.X - turret.Position.X, centre.Z - turret.Position.Z);
                    var length = Math.Max(1.0, Math.Sqrt(dx * dx + dz * dz));
                    runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
                        turret.Team == Team.Order ? Team.Chaos : Team.Order,
                        new Position2(turret.Position.X + dx / length * 450.0, turret.Position.Z + dz / length * 450.0))
                        { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
                }

                var starts = turrets.ToDictionary(turret => turret, _ => new List<double>());
                var cooldowns = turrets.ToDictionary(turret => turret, turret => turret.AttackCooldown);
                for (var tick = 0; tick < (int)Math.Round(Window / Step); tick++)
                {
                    runtime.Tick(Step);
                    var now = runtime.World.Clock.Now();
                    foreach (var turret in turrets)
                    {
                        if (turret.AttackCooldown > cooldowns[turret] + 1e-9) starts[turret].Add(now);
                        cooldowns[turret] = turret.AttackCooldown;
                    }
                }

                foreach (var turret in turrets)
                {
                    var tier = layout.TurretTiers[turret.Name];
                    var character = layout.TurretCharacter(tier, turret.Team);
                    // OrderTurretShrine ships no character script.
                    var script = CaseInsensitivePaths.Resolve(paths.Under("DATA", "Characters", character, "Scripts", "CharScript" + character + ".luaobj"));
                    var assisted = File.Exists(script) &&
                                   System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(script)).Contains("TurretAssistManager", StringComparison.Ordinal);
                    var period = RecordPeriod(character, assisted ? 1.3 : 1.0);
                    var swings = starts[turret];
                    var interval = swings.Count >= 3 ? (swings[^1] - swings[0]) / (swings.Count - 1) : double.NaN;
                    Console.WriteLine($"map {mapId} {turret.Name} {character} {tier} assist={assisted} shipped {period:0.0000} s " +
                                      $"({1.0 / period:0.000}/s) ours {interval:0.0000} s over {swings.Count} swings, AttackPeriod {turret.AttackPeriod:0.0000}");
                    TestAssert.True(swings.Count >= 3, $"map {mapId} {turret.Name} ({character}) swings at least three times in {Window} s ({swings.Count})");
                    AssertCadence($"map {mapId} {turret.Name} ({character}, {tier})", period, interval);
                }
            }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>The production composition of <paramref name="mapId"/> with an Order and a Chaos Annie, started and stepped once.</summary>
    private static MatchRuntime StartTwoSeatMatch(int mapId)
    {
        var paths = GameDataPaths.Default.ForMap(mapId);
        GameData.ConfigureDefault(paths);
        SessionParticipantDefinition[] roster = [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos)];
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(paths), GameData.Default, readySettleSeconds: 0.0,
            roster: roster, mapId: mapId);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            TestAssert.True(runtime.Session.Authenticate(100L + index, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"map {mapId} seat {index} authenticates");
            runtime.Session.Spawn(100L + index);
            TestAssert.True(runtime.Session.MarkReady(100L + index), $"map {mapId} seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, $"map {mapId} starts");
        runtime.Tick(Step);
        return runtime;
    }

    /// <summary>
    /// Batch 693 (owner, 4 November 2026: turrets "shoot way too fast" on Summoner's Rift and Twisted Treeline; "we lack a small delay before
    /// the tower shoots when it targets a new target", and the original turret wind-up). What the shipped files give a turret's first shot on
    /// a new target:
    /// <list type="bullet">
    /// <item>DATA/Scripts/Turret.luaobj's TimerFindEnemies (InitTimer 0.15, repeating; proto 0.7) takes a target only from HARDIDLE
    /// (NetSetState ATTACKING, SetTarget: the laser) and its trailing gate reads the state it entered with, so the attack is turned on
    /// (TurnOnAutoAttack(GetTarget)) from the next scan. OnTargetLost (proto 0.1) and OnCallForHelp (proto 0.2) only SetTarget: the attack
    /// TurnOnAutoAttack gave the old object reaches the new one at the next scan too.</item>
    /// <item>The stock client times the 0x0D's cast frame at receipt + extra time + its own attack cast delay, period x (0.3 +
    /// AttackDelayCastOffsetPercent). With extra time 0 the shot
    /// leaves at the cast frame the server's hit is timed from (the turret's AttackWindup), after a drawn wind-up.</item>
    /// </list>
    /// A dive (an Order champion steps into the front Chaos lane turret's range) and two switches (the minion it shoots dies; the champion
    /// hits the turret's ally), on maps 1 and 4. The switches land right after a scan with the swing timer spent, so only the switch rule
    /// can hold the shot. Each line it prints is a row of the batch record's table.
    /// </summary>
    [Check("combat.turret-first-shot-on-a-new-target-waits-for-its-scan-and-windup", Serial = true, TimeoutSeconds = 300)]
    public static void TurretFirstShotOnANewTargetWaitsForItsScanAndWindup()
    {
        // Every scenario runs and prints its row before any failure is reported, so one run gives the whole table.
        var failures = new List<string>();
        try
        {
            foreach (var mapId in new[] { GameMaps.Normal, GameMaps.TwistedTreeline })
                foreach (var scenario in new Action[] { () => TurretDive(mapId), () => TurretSwitch(mapId, callForHelp: false),
                             () => TurretSwitch(mapId, callForHelp: true) })
                {
                    try { scenario(); }
                    catch (Exception failure) { failures.Add(failure.Message); }
                }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    private static void TurretDive(int mapId)
    {
        using var scene = new TurretScene(mapId);
        scene.Diver.Position = scene.Toward(1400.0);
        scene.Advance(1.0);
        TestAssert.True(scene.Turret.AttackTarget is null, $"map {mapId}: the turret holds no target before the dive");
        scene.Diver.Position = scene.Toward(450.0);
        var entered = scene.Now;
        var hits = new List<double>();
        var health = scene.Diver.Health;
        for (var tick = 0; tick < (int)Math.Round(3.0 / Step); tick++)
        {
            scene.Tick();
            if (scene.Diver.Health < health - 1e-6) hits.Add(scene.Now - entered);
            health = scene.Diver.Health;
        }
        TestAssert.True(scene.Sent(0x6D, scene.Diver, entered).Any(),
            $"map {mapId}: {scene.Turret.Name}'s laser reaches the diver's client (target {scene.Turret.AttackTarget}, diver {scene.Diver.NetId} at " +
            $"{scene.Diver.Position}, turret at {scene.Turret.Position}, {scene.PacketCount} packets seen)");
        var laser = scene.Sent(0x6D, scene.Diver, entered).First();
        var shots = scene.Sent(0x0D, scene.Diver, entered).ToList();
        TestAssert.True(shots.Count > 0 && hits.Count > 0, $"map {mapId}: the turret shoots and hits the diver");
        var shot = shots[0];
        var extra = (shot.Bytes[9] - 0x80) / 100.0;
        var clientLaunch = scene.ClientLaunch(shot);
        var serverLaunch = shot.At + scene.Turret.AttackWindup;
        int Within(IEnumerable<double> times, double seconds) => times.Count(time => time <= seconds + 1e-9);
        var launches = shots.Select(item => scene.ClientLaunch(item) - entered).ToList();
        Console.WriteLine($"map {mapId} dive {scene.Turret.Name} ({scene.Character}): laser +{laser.At - entered:0.000} s, first 0x0D " +
                          $"+{shot.At - entered:0.000} s (extra {extra:0.00} s), the client's shot leaves +{clientLaunch - entered:0.000} s, the " +
                          $"server launches +{serverLaunch - entered:0.000} s, first hit +{hits[0]:0.000} s; 0x0D in 1/2/3 s " +
                          $"{Within(shots.Select(item => item.At - entered), 1)}/{Within(shots.Select(item => item.At - entered), 2)}/" +
                          $"{Within(shots.Select(item => item.At - entered), 3)}, shots leaving {Within(launches, 1)}/{Within(launches, 2)}/" +
                          $"{Within(launches, 3)}, hits {Within(hits, 1)}/{Within(hits, 2)}/{Within(hits, 3)}");

        // Turret.luaobj: the scan that sees the diver sets the laser, the next one turns the attack on (script = retail, unchanged).
        TestAssert.True(scene.ScanTimes.Contains(laser.At), $"map {mapId}: the laser goes out on a TimerFindEnemies scan");
        TestAssert.Near(scene.ScanTimes.First(time => time > laser.At + 1e-9), shot.At, 1e-6,
            $"map {mapId}: the first 0x0D is the next scan's TurnOnAutoAttack, not the acquiring scan's");
        // The client's wind-up: no extra time, so the shot leaves at the cast frame the server's hit is timed from.
        TestAssert.Equal(0x80, (int)shot.Bytes[9], $"map {mapId}: the turret's 0x0D announces no extra time (0x80), so its client draws the wind-up");
        TestAssert.Near(serverLaunch, clientLaunch, 1e-6,
            $"map {mapId}: the client's cast frame (receipt + extra + {scene.ClientCastDelay:0.0000} s, the record's cast delay) is the server's launch");
    }

    private static void TurretSwitch(int mapId, bool callForHelp)
    {
        using var scene = new TurretScene(mapId);
        var minion = scene.Runtime.World.Register(new Entity(scene.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Order, scene.Toward(450.0)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
        scene.Diver.Position = scene.Toward(450.0);
        // The ladder takes the lane minion over the champion; two swings at it settle the attack.
        for (var tick = 0; tick < (int)Math.Round(6.0 / Step) && scene.Sent(0x0D, minion, 0.0).Count() < 2; tick++) scene.Tick();
        TestAssert.True(scene.Sent(0x0D, minion, 0.0).Count() >= 2, $"map {mapId}: the turret shoots the minion first");
        while (scene.ScanTimes[^1] < scene.Now - 1e-9) scene.Tick();
        // Right after a scan, with the swing timer spent: only the switch rule can hold the next shot back.
        scene.Turret.AttackCooldown = 0.0;
        var switched = scene.Now;
        if (callForHelp)
            TestAssert.True(scene.Runtime.Combat.ApplyHit(scene.Diver.NetId, scene.Ally.NetId, 1.0, DamageType.True, DamageSourceKind.Basic),
                $"map {mapId}: the champion hits the turret's ally");
        else minion.Alive = false;
        scene.Advance(1.0);
        var how = callForHelp ? "call for help" : "target death";
        TestAssert.True(scene.Sent(0x6D, scene.Diver, switched).Any(), $"map {mapId} {how}: the turret's laser moves to the champion");
        var laser = scene.Sent(0x6D, scene.Diver, switched).First();
        var shot = scene.Sent(0x0D, scene.Diver, switched).First();
        var nextScan = scene.ScanTimes.First(time => time > switched + 1e-9);
        Console.WriteLine($"map {mapId} {how} {scene.Turret.Name}: laser +{laser.At - switched:0.000} s, first 0x0D +{shot.At - switched:0.000} s, " +
                          $"next scan +{nextScan - switched:0.000} s, the client's shot leaves " +
                          $"+{scene.ClientLaunch(shot) - switched:0.000} s");
        TestAssert.Near(nextScan, shot.At, 1e-6,
            $"map {mapId} {how}: the first shot at the new target waits for the next scan's TurnOnAutoAttack(GetTarget()) " +
            $"(laser +{laser.At - switched:0.000} s, shot +{shot.At - switched:0.000} s, scan +{nextScan - switched:0.000} s)");
    }

    /// <summary>
    /// Batch 712 (owner, 7 November 2026: "towers still shoot too fast, when it switches from one target to another the delay isn't always
    /// applied"). Every way the front Chaos lane turret's target changes, on maps 1 and 4, against Turret.luaobj's rule: a new target is shot
    /// only after the scan that calls TurnOnAutoAttack(GetTarget()) on it (TimerFindEnemies, 0.15 s; the HARDIDLE scan that takes a target
    /// does not; OnTargetLost, OnCallForHelp and OnTauntBegin only SetTarget), and never before the turret's attack timer has run out.
    /// Most switches are made right after a scan, so the rule's scan is a whole scan away; the call for help is also made one to three steps
    /// later, which shows the script's own spread (a switch on the step before a scan is shot on the laser's step). A row per switch: from the
    /// switch to the laser, the first 0x0D at the new target, and the client's shot leaving; "expected" is the rule's step. Every row held on
    /// the engine of master 3d24accdc: no path shoots a new target before its scan or before its attack timer runs out (batch 712 record).
    /// </summary>
    [Check("combat.turret-every-target-switch-waits-for-the-scan-that-turns-its-attack-on", Serial = true, TimeoutSeconds = 600)]
    public static void TurretEveryTargetSwitchWaitsForTheScanThatTurnsItsAttackOn()
    {
        var failures = new List<string>();
        try
        {
            foreach (var mapId in new[] { GameMaps.Normal, GameMaps.TwistedTreeline })
                foreach (var (label, scenario) in SwitchScenarios)
                {
                    try
                    {
                        using var scene = new TurretScene(mapId);
                        MeasureSwitch(scene, $"map {mapId} {label}", scenario(scene));
                    }
                    catch (Exception failure) { failures.Add(failure.Message); }
                }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// One switch: when it was made, the unit the turret must shoot next (null: it must keep its target and shoot no <paramref name="Watched"/>),
    /// which scan after the switch turns the attack on (1: the next), the attack timer left at the switch, and a unit it must not shoot after it.
    /// </summary>
    private sealed record TurretSwitchCase(double Switched, Entity? NewTarget, int Scan, double Cooldown, Entity? Watched = null);

    private static readonly (string Label, Func<TurretScene, TurretSwitchCase> Make)[] SwitchScenarios =
    [
        ("acquisition from idle (a champion walks in)", scene =>
        {
            scene.Diver.Position = scene.Toward(1400.0);
            scene.Advance(1.0);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            scene.Diver.Position = scene.Toward(450.0);
            // The scan that sees him takes him (laser), the next one turns the attack on.
            return new(switched, scene.Diver, 2, 0.0);
        }),
        ("its target dies (dead, not yet removed)", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            minion.Alive = false;
            return new(switched, scene.Diver, 1, 0.0);
        }),
        ("its target leaves attack range (walks out, recalls, teleports)", scene =>
        {
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(scene.Diver);
            var minion = scene.Minion(450.0);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            scene.Diver.Position = scene.Toward(3000.0);
            // Scan 1: out of range, HARDIDLE and TurnOffAutoAttack(MOVING); scan 2 takes the minion; scan 3 turns the attack on.
            return new(switched, minion, 3, 0.0);
        }),
        ("its target becomes untargetable", scene =>
        {
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(scene.Diver);
            var minion = scene.Minion(450.0);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            scene.Runtime.Buffs.AddBuff(scene.Diver, "B712Untargetable", BuffType.Internal, 5.0, scene.Diver.NetId,
                new BuffPayload { Untargetable = true }, canMitigateDuration: false);
            return new(switched, minion, 1, 0.0);
        }),
        ("its target stealths in its 800-unit true sight (no switch)", scene =>
        {
            // Every lane turret's TurretBonusHealth adds a perception bubble with RevealSteath (800 units): the turret keeps seeing him.
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(scene.Diver);
            var minion = scene.Minion(450.0);
            scene.AfterScan();
            var switched = scene.Now;
            scene.Runtime.Buffs.AddBuff(scene.Diver, "B712Stealth", BuffType.Invisibility, 5.0, scene.Diver.NetId,
                new BuffPayload { Stealthed = true }, canMitigateDuration: false);
            return new(switched, null, 0, 0.0, minion);
        }),
        ("its own shot kills its target", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            minion.Health = 1.0;
            for (var tick = 0; tick < (int)Math.Round(3.0 / Step) && minion.Alive; tick++) scene.Tick();
            TestAssert.True(!minion.Alive, $"{scene.Turret.Name}'s shot kills {minion.Name}");
            // The kill landed inside the last step: the switch is that step's start, and a scan on that step is the next one.
            var switched = scene.Now - Step;
            return new(switched, scene.Diver, 1, scene.Turret.AttackCooldown + Step);
        }),
        ("a champion hits a champion (call for help) right after a scan", CallForHelpAfterScan(0)),
        // The same switch one, two and three steps later in the scan's 0.15 s: the wait for the scan shrinks with it (the script's own
        // spread, 0 to 0.15 s), and a switch on the step before the scan is shot on the laser's own step.
        ("a champion hits a champion, 1 step after a scan", CallForHelpAfterScan(1)),
        ("a champion hits a champion, 2 steps after a scan", CallForHelpAfterScan(2)),
        ("a champion hits a champion, 3 steps after a scan", CallForHelpAfterScan(3)),
        ("a champion hits the turret's minion (no call to a turret)", scene =>
        {
            var minion = scene.Minion(450.0);
            var ally = scene.Minion(-100.0, Team.Chaos);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.AfterScan();
            var switched = scene.Now;
            scene.Runtime.Combat.ApplyHit(scene.Diver.NetId, ally.NetId, 1.0, DamageType.True, DamageSourceKind.Basic);
            return new(switched, null, 0, 0.0, scene.Diver);
        }),
        ("a champion taunts it", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            scene.Runtime.Buffs.AddBuff(scene.Turret, "B712Taunt", BuffType.Taunt, 1.5, scene.Diver.NetId, canMitigateDuration: false);
            return new(switched, scene.Diver, 1, 0.0);
        }),
        ("a champion walks in while it shoots a minion (no switch)", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Engage(minion);
            scene.AfterScan();
            var switched = scene.Now;
            scene.Diver.Position = scene.Toward(450.0);
            return new(switched, null, 0, 0.0, scene.Diver);
        }),
        ("two switches inside one scan (its target dies, then a call for help)", scene =>
        {
            var first = scene.Minion(400.0);
            var second = scene.Minion(500.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(first);
            scene.AfterScan();
            scene.Turret.AttackCooldown = 0.0;
            var switched = scene.Now;
            first.Alive = false;
            scene.Tick();
            TestAssert.Equal(second.NetId, scene.Turret.AttackTarget ?? 0u, $"{scene.Turret.Name}: OnTargetLost takes the second minion");
            scene.CallForHelp();
            return new(switched, scene.Diver, 1, 0.0, second);
        }),
        ("a call for help during its wind-up at a minion", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.NextSwing(minion);
            var switched = scene.Now;
            var cooldown = scene.Turret.AttackCooldown;
            scene.CallForHelp();
            return new(switched, scene.Diver, 1, cooldown);
        }),
        ("its target dies during its wind-up", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.NextSwing(minion);
            var switched = scene.Now;
            var cooldown = scene.Turret.AttackCooldown;
            // The champion beside the turret last-hits it.
            scene.Runtime.Combat.ApplyHit(scene.Ally.NetId, minion.NetId, 2e7, DamageType.True, DamageSourceKind.Basic);
            TestAssert.True(!minion.Alive, $"{scene.Ally.Name} kills {minion.Name}");
            return new(switched, scene.Diver, 1, cooldown);
        }),
        ("a call for help right after a shot (its missile in flight)", scene =>
        {
            var minion = scene.Minion(450.0);
            scene.Diver.Position = scene.Toward(450.0);
            scene.Engage(minion);
            scene.NextSwing(minion);
            scene.Advance(5 * Step);
            var switched = scene.Now;
            var cooldown = scene.Turret.AttackCooldown;
            scene.CallForHelp();
            return new(switched, scene.Diver, 1, cooldown);
        }),
    ];

    private static Func<TurretScene, TurretSwitchCase> CallForHelpAfterScan(int steps) => scene =>
    {
        var minion = scene.Minion(450.0);
        scene.Diver.Position = scene.Toward(450.0);
        scene.Engage(minion);
        scene.AfterScan();
        scene.Advance(steps * Step);
        scene.Turret.AttackCooldown = 0.0;
        var switched = scene.Now;
        scene.CallForHelp();
        return new(switched, scene.Diver, 1, 0.0);
    };

    private static void MeasureSwitch(TurretScene scene, string label, TurretSwitchCase switchCase)
    {
        scene.Advance(2.0);
        var switched = switchCase.Switched;
        if (switchCase.NewTarget is not { } target)
        {
            var stray = scene.Sent(0x0D, switchCase.Watched!, switched).Select(shot => shot.At - switched).ToList();
            Console.WriteLine($"{label} {scene.Turret.Name}: 0x0D at the champion in 2 s: {stray.Count}");
            TestAssert.True(stray.Count == 0, $"{label}: the turret keeps its target and shoots no {switchCase.Watched!.Name} " +
                                              $"(0x0D at +{string.Join(", +", stray.Select(at => at.ToString("0.000")))} s)");
            return;
        }
        var scans = scene.ScanTimes.Where(time => time > switched + 1e-9).ToList();
        var armed = scans[switchCase.Scan - 1];
        // The attack timer counts down by whole steps and fires on the step it reaches zero (batch 670).
        var timerOut = switched + Math.Ceiling(switchCase.Cooldown / Step - 1e-6) * Step;
        var expected = Math.Max(armed, timerOut);
        var shots = scene.Sent(0x0D, target, switched).ToList();
        var laser = scene.Sent(0x6D, target, switched).Select(item => item.At - switched).DefaultIfEmpty(double.NaN).First();
        var first = shots.Count > 0 ? shots[0].At - switched : double.NaN;
        var leaves = shots.Count > 0 ? scene.ClientLaunch(shots[0]) - switched : double.NaN;
        var stolen = switchCase.Watched is { } watched ? scene.Sent(0x0D, watched, switched).Count() : 0;
        Console.WriteLine($"{label} {scene.Turret.Name}: laser +{laser:0.000} s, first 0x0D +{first:0.000} s, expected +{expected - switched:0.000} s " +
                          $"(scan {switchCase.Scan} +{armed - switched:0.000} s, attack timer +{timerOut - switched:0.000} s), the client's shot leaves " +
                          $"+{leaves:0.000} s");
        TestAssert.True(shots.Count > 0, $"{label}: the turret shoots its new target");
        TestAssert.Near(expected, shots[0].At, 1e-6,
            $"{label}: the first shot at the new target waits for scan {switchCase.Scan}'s TurnOnAutoAttack and the attack timer " +
            $"(first 0x0D +{first:0.000} s, expected +{expected - switched:0.000} s)");
        TestAssert.Equal(0, stolen, $"{label}: no shot at {switchCase.Watched?.Name} after the switch");
    }

    /// <summary>
    /// A two-seat production match around the front Chaos lane turret (the nearest to the map's centre): the Order champion is the diver, the
    /// Chaos champion stands beside the turret. Both are 1e7-health and acquire nothing; the client of the Order seat records the wire.
    /// </summary>
    private sealed class TurretScene : IDisposable
    {
        private readonly BbM3.WireCapture _wire;

        public TurretScene(int mapId)
        {
            Runtime = StartTwoSeatMatch(mapId);
            var layout = MapDefinitions.Layout;
            var centre = new Position2((layout.OrderSpawn.X + layout.ChaosSpawn.X) / 2, (layout.OrderSpawn.Z + layout.ChaosSpawn.Z) / 2);
            double Distance(Position2 at) => Math.Sqrt(Math.Pow(at.X - centre.X, 2) + Math.Pow(at.Z - centre.Z, 2));
            Turret = Runtime.World.Entities.Values.Where(unit => unit.Team == Team.Chaos && TurretRules.IsLaneTurret(unit))
                .OrderBy(unit => Distance(unit.Position)).First();
            Character = layout.TurretCharacter(layout.TurretTiers[Turret.Name], Team.Chaos);
            // Every front lane character adds TurretAssistManager in its CharOnActivate (batch 670), the 1.3 of the client's delay helper.
            ClientCastDelay = RecordPeriod(Character, 1.3) * RecordCastFraction(Character);
            var length = Math.Max(1.0, Distance(Turret.Position));
            _direction = ((centre.X - Turret.Position.X) / length, (centre.Z - Turret.Position.Z) / length);
            Diver = Runtime.Session.Participants[0].Hero!;
            Ally = Runtime.Session.Participants[1].Hero!;
            foreach (var hero in new[] { Diver, Ally })
            {
                hero.HeroAutoAcquire = false;
                hero.MaxHealth = hero.Health = 1e7;
            }
            Ally.Position = Toward(-150.0);
            _wire = new BbM3.WireCapture(() => Runtime.World.Clock.Now());
            var seat = Runtime.Session.Participants[0];
            Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(seat.PlayerId, seat.Team, Diver.NetId, _wire,
                ready: true, view: seat.View));
        }

        private readonly (double X, double Z) _direction;
        public MatchRuntime Runtime { get; }
        public Entity Turret { get; }
        public string Character { get; }
        public double ClientCastDelay { get; }
        public Entity Diver { get; }
        public Entity Ally { get; }
        /// <summary>The steps on which the turret's TimerFindEnemies ran.</summary>
        public List<double> ScanTimes { get; } = [];
        public double Now => Runtime.World.Clock.Now();
        public int PacketCount => _wire.Packets.Count;

        /// <summary>When the client's shot of a 0x0D leaves: its cast frame, receipt + extra time + the cast delay, and never before receipt.</summary>
        public double ClientLaunch((double At, byte[] Bytes) shot) => shot.At + Math.Max(0.0, (shot.Bytes[9] - 0x80) / 100.0 + ClientCastDelay);

        public Position2 Toward(double distance) =>
            new(Turret.Position.X + _direction.X * distance, Turret.Position.Z + _direction.Z * distance);

        public void Tick()
        {
            Runtime.Tick(Step);
            if (((Amberline.Simulation.Ai.LuaAiService)Runtime.Combat.AiHost).TimerFired(Turret)) ScanTimes.Add(Now);
        }

        public void Advance(double seconds)
        {
            for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) Tick();
        }

        /// <summary>A 1e7-health dummy minion <paramref name="distance"/> units from the turret toward the centre (an Order one by default).</summary>
        public Entity Minion(double distance, Team team = Team.Order) => Runtime.World.Register(new Entity(
            Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, team, Toward(distance))
            { Health = 1e7, MaxHealth = 1e7, Name = $"Dummy{distance:0}" });

        /// <summary>Steps until the turret has swung at <paramref name="target"/> twice, which settles its attack on it.</summary>
        public void Engage(Entity target)
        {
            for (var tick = 0; tick < (int)Math.Round(6.0 / Step) && Sent(0x0D, target, 0.0).Count() < 2; tick++) Tick();
            TestAssert.True(Sent(0x0D, target, 0.0).Count() >= 2, $"{Turret.Name} shoots {target.Name} first");
        }

        /// <summary>Steps until the step that ran a TimerFindEnemies scan.</summary>
        public void AfterScan()
        {
            Tick();
            while (ScanTimes.Count == 0 || ScanTimes[^1] < Now - 1e-9) Tick();
        }

        /// <summary>Steps until the turret's next 0x0D at <paramref name="target"/>: its swing's wind-up has just begun.</summary>
        public void NextSwing(Entity target)
        {
            var before = Sent(0x0D, target, 0.0).Count();
            for (var tick = 0; tick < (int)Math.Round(3.0 / Step) && Sent(0x0D, target, 0.0).Count() == before; tick++) Tick();
            TestAssert.True(Sent(0x0D, target, 0.0).Count() > before, $"{Turret.Name} swings at {target.Name} again");
        }

        /// <summary>The diver hits the champion beside the turret: Turret.luaobj's OnCallForHelp.</summary>
        public void CallForHelp() => TestAssert.True(
            Runtime.Combat.ApplyHit(Diver.NetId, Ally.NetId, 1.0, DamageType.True, DamageSourceKind.Basic), $"{Diver.Name} hits {Ally.Name}");

        /// <summary>The turret's packets of <paramref name="opcode"/> naming <paramref name="target"/> (at byte 5), delivered after <paramref name="after"/>.</summary>
        public IEnumerable<(double At, byte[] Bytes)> Sent(byte opcode, Entity target, double after) => _wire.Packets
            .Where(entry => entry.At > after + 1e-9)
            .Select(entry => (entry.At, Bytes: entry.Packet.Plaintext.ToArray()))
            .Where(entry => entry.Bytes.Length >= 9 && entry.Bytes[0] == opcode &&
                            BitConverter.ToUInt32(entry.Bytes, 1) == Turret.NetId && BitConverter.ToUInt32(entry.Bytes, 5) == target.NetId);

        public void Dispose() => Runtime.Dispose();
    }
}
