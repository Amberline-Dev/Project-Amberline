using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
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
                GameData.ConfigureDefault(paths);
                SessionParticipantDefinition[] roster = [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos)];
                using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(paths), GameData.Default, readySettleSeconds: 0.0,
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
}
