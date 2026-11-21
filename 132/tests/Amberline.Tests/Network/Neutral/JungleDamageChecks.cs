using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 739 (owner, 11 November 2026: "jungle is doing more damage than intended; like almost double on certain monsters"), restated by
// batch 758 (owner, 12 November 2026: "Match the footage"). Every camp member of Summoner's Rift (Map1) and the Twisted Treeline (Map4), as
// the shipped level scripts spawn them, swings at a champion in a 3v3 production match (the server's own map composition,
// GameData.ConfigureDefault). Each swing is read off the champion's own wire (0x0D swing, 0x68 damage) and its queued hit
// (CombatService.PendingHits), and the champion's health loss is compared with the wire. A swing on a champion deals the record's AD, times
// the map's dr_UnitToHero (0.7), the record's CritDamageBonus on a critical roll (only wolf, GiantWolf and Worm have a BaseCritChance; V3.14
// "Giant Wolf: No longer critically strikes"), then armour, and nothing more: retail 2011 footage (a ranked game of 1 October 2011, patch
// 1.0.0.125; 99 clean hits of seven monster types on a champion, levels 1-12, docs/maintenance/2026-11-12-batch-758-jungle-champion-damage.md)
// shows no trace of the x1.43 every jungle CharScript's CharOnHitUnit
// writes into DamageAmount, which batch 739 measured on top. A camp the footage shows fights a champion of the armour he had then, and its
// hits must be the footage's (Pins); the other camps fight one of armour 54. The x1.43 stays on a hit that is not on a champion (the footage
// cannot show one): the production hit stage gives a lane minion 1.43 times the hit. Besides the swings only two shipped scripts deal damage:
// the Dragon's CharOnHitUnit adds Burning (15 TRUE_DAMAGE a second, BUFF_RENEW_EXISTING), which still ticks, and the Worm attacks only through
// its scripted spells (WormAttack: its base AD as PHYSICAL_DAMAGE, no ratio, no x1.43; Propel 300 and SweepingBlow 200 MAGIC_DAMAGE).
// Batch 781 (1.0.0.132): the 132 Summoner's Rift character scripts no longer write DamageAmount at all (no x1.43; on the Twisted Treeline
// seven of nine keep it), dr_UnitToHero is 0.6 (Constants.var), the records changed (wolf 14, GiantWolf 22, Golem 51, LesserWraith 17, ...),
// the golem camp is SmallGolem + Golem, the Dragon burns with DragonBurning, and the character scripts move the attack damage
// (CharOnActivate's permanent flat AD and GlobalMonsterBuff's damagePerMinute, see Scripted132). The 2011 footage (patch 1.0.0.125) proved the
// formula, AD x ratio (x crit) x armour on a champion and nothing more; its numbers are 126 data, so the pins below are that formula on the
// 132 data at the footage's armours, and every swing is checked against the AD the 132 scripts give the monster when it swings.
// Batch 932 (players, 21 November 2026: "the jungle feels way too weak"): retail footage of 25 December 2011 (patch 1.0.0.131, the 132 jungle)
// shows a Nocturne of armour 57 (level 1) and 64 (level 3) hit by every Summoner's Rift camp at the target panel's AD (the 132 values) x 1.0,
// then armour, less about 2 (his Tough Skin), not x 0.6: a camp monster's swing on a champion takes no class ratio in 132
// (CombatRules.Line.cs). The footage armours and pins below are that video's (2026-11-21-batch-932-jungle-strength.md); the 1.0.0.125
// footage stays the 126 line's.
// The check is Serial: it swaps GameData's default map, as the server does for a match on another map.
public static class JungleDamageChecks
{
    private const double ChampionArmor = 54.0;
    private const double Step = 0.1;
    // The fight lasts 14 s; the camp is killed at 14 s and the 5 s after it are read for hits that land late (the Dragon's 4 s Burning).
    private const int FightSteps = 140;
    private const int SettleSteps = 50;

    // Batch 932: the champion's armour in the December 2011 footage while he cleared each camp (HUD stats panel 57 at level 1, + 3.5 a
    // level): wolves and blue at level 1, wraiths at 2, golems and red at 3. The camp's other members share it.
    private static readonly Dictionary<string, double> FootageArmor = new()
    {
        ["GiantWolf"] = 57.0, ["AncientGolem"] = 57.0, ["Wraith"] = 60.5, ["LesserWraith"] = 60.5, ["Golem"] = 64.0, ["SmallGolem"] = 64.0,
        ["LizardElder"] = 64.0,
    };

    // Batch 932: the December 2011 footage's hits (HUD health at 10 frames a second, each reading +/- 1, the champion's regeneration and
    // heals in between) were the target panel's AD x 1.0 (x crit) x 100/(100 + armour), less about 2 (his Tough Skin; this check's champion
    // has no masteries, so the pins are the formula itself). Every landed swing of the monster on a champion of that armour must be this one.
    // The Ancient Golem (footage 39-40 at armour 57, 10 hits), the Lizard Elder (38-39 at armour 64) and the young lizards (4-5) grow every
    // minute in 132 (GlobalMonsterBuff's upgradeTimer), so after this check's 20 minutes they have no footage number; their swings are held to
    // the AD the scripts give them when they swing (Row).
    private static readonly (string Monster, bool Critical, double Armor, double Hit, string Video)[] Pins =
    [
        ("Golem", false, 64.0, 32.93, "54 x 100/164; footage level 3: 5 hits 31, 28, 31, 31, 27 (ratio 0.6 gives 19.76)"),
        ("SmallGolem", false, 64.0, 18.29, "30 x 100/164; footage level 3: 5 hits 15, 16, 13, 15, 17 (ratio 0.6 gives 10.98)"),
        ("GiantWolf", false, 57.0, 15.29, "24 x 100/157; footage level 1: 13 to 16 (ratio 0.6 gives 9.17)"),
        ("GiantWolf", true, 57.0, 45.86, "24 x 3 x 100/157; footage level 1: critical x3, 1 hit, 43 (ratio 0.6 gives 27.52)"),
        ("wolf", false, 57.0, 8.92, "14 x 100/157; footage level 1: 6 to 7 (ratio 0.6 gives 5.35)"),
        ("wolf", true, 57.0, 17.83, "14 x 2 x 100/157; no critical wolf hit read in the footage"),
        ("Wraith", false, 60.5, 15.58, "25 x 100/160.5; footage level 2: lands with a Lesser Wraith's, 22 together (ratio 0.6 gives 9.35)"),
        ("LesserWraith", false, 60.5, 10.59, "17 x 100/160.5; footage level 2: 7 to 8 (ratio 0.6 gives 6.36)"),
    ];

    /// <summary>
    /// Batch 781: what the 1.0.0.132 character scripts do to a camp monster's attack damage (CharOnActivate, DATA/Characters/&lt;name&gt;/Scripts):
    /// a permanent flat AD (BBIncPermanentStat IncPermanentFlatPhysicalDamageMod: Golem +3, SmallGolem -3, GiantWolf +2, Wraith +1) and
    /// GlobalMonsterBuff's NextBuffVars (spawnTime 101 for all, damagePerMinute, upgradeTimer). Spells/GlobalMonsterBuff.luaobj's
    /// OnBuffActivate adds damagePerMinute x max(0, game time - spawnTime) / 60 once; its BuffOnUpdateStats adds damagePerMinute every 60 s
    /// (BBExecutePeriodically, not immediately) while the monster has 99.5 % of its health or more, only when upgradeTimer is true.
    /// </summary>
    private static readonly Dictionary<string, (double FlatAd, double PerMinute, bool Grows)> Scripted132 = new(StringComparer.Ordinal)
    {
        ["AncientGolem"] = (0.0, 1.825, true), ["LizardElder"] = (0.0, 1.825, true), ["YoungLizard"] = (0.0, 0.22, true),
        ["Golem"] = (3.0, 1.05, false), ["SmallGolem"] = (-3.0, 0.84, false), ["GiantWolf"] = (2.0, 0.44, false),
        ["wolf"] = (0.0, 0.286, false), ["Wraith"] = (1.0, 0.5, false), ["LesserWraith"] = (0.0, 0.2195, false),
        ["TwistedTinyWraith"] = (0.0, 0.2195, false),
    };

    /// <summary>The camp monsters whose 1.0.0.132 CharOnHitUnit still multiplies DamageAmount by 1.43 (the Twisted Treeline's).</summary>
    private static readonly HashSet<string> HitMultiplier132 = new(StringComparer.Ordinal)
    {
        "Ghast", "RabidWolf", "TwistedBlueWraith", "TwistedGiantWolf", "TwistedGolem", "TwistedLizardElder", "TwistedSmallWolf",
    };

    /// <summary>The attack damage the 1.0.0.132 scripts give a monster of record AD <paramref name="record"/> spawned at
    /// <paramref name="spawnAt"/>, at <paramref name="time"/> (it has kept its health above 99.5 % since).</summary>
    private static double ScriptedAd(string name, double record, double spawnAt, double time)
    {
        if (!Scripted132.TryGetValue(name, out var script)) return record;
        var ad = record + script.FlatAd + script.PerMinute * Math.Max(0.0, spawnAt - 101.0) / 60.0;
        return script.Grows ? ad + script.PerMinute * Math.Floor((time - spawnAt) / 60.0 + 1e-9) : ad;
    }

    [Check("neutral.jungle-hits-on-a-champion-match-2011-footage", Serial = true, TimeoutSeconds = 300)]
    public static void JungleHitsOnAChampionMatch2011Footage()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();
        var pinned = new int[Pins.Length];
        try
        {
            foreach (var map in new[] { GameMaps.Normal, GameMaps.TwistedTreeline })
                foreach (var row in Measure(map, failures, pinned)) Console.WriteLine(row);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
        for (var index = 0; index < Pins.Length; index++)
        {
            var pin = Pins[index];
            Console.WriteLine($"footage {pin.Monster}{(pin.Critical ? " critical" : "")} on armour {pin.Armor}: {pin.Hit:F2} ({pin.Video}), " +
                              $"{pinned[index]} landed hit(s) measured");
            if (pinned[index] == 0) failures.Add($"no {(pin.Critical ? "critical " : "")}{pin.Monster} hit on armour {pin.Armor} was measured");
        }
        TestAssert.True(failures.Count == 0, $"{failures.Count} finding(s): " + string.Join("; ", failures));
    }

    private sealed class Sample
    {
        public required string Name { get; init; }
        public double SpawnAt { get; init; }
        public double SpawnAd { get; init; }
        public double? Ad600 { get; set; }
        public double? Ad1200 { get; set; }
        // The period it fights with: the record's, or faster under its own script buff (RabidWolf's CrestOfNaturesFury, +0.2 attack speed).
        public double? Period { get; set; }
        public List<double> Swings { get; } = [];
        // Each queued swing with the attack damage the monster had and the clock when it was queued.
        public List<(double Damage, BasicAttackOutcome Outcome, double Ad, double At)> Queued { get; } = [];
        public List<(double Damage, byte Type, bool AfterKill)> Events { get; } = [];
    }

    private static List<string> Measure(int map, List<string> failures, int[] pinned)
    {
        var paths = GameDataPaths.Default.ForMap(map);
        GameData.ConfigureDefault(paths);
        var data = GameData.Default;
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, data, readySettleSeconds: 0.0, mapId: map,
            roster: [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Order2", "Tryndamere", Team.Order),
                new(3, 2, "Order3", "Warwick", Team.Order), new(4, 3, "Chaos", "Orianna", Team.Chaos),
                new(5, 4, "Chaos2", "Garen", Team.Chaos), new(6, 5, "Chaos3", "MasterYi", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.AfkTrackingDisabled = true;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), $"map {map} production match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var seat = session.Participants.First(participant => participant.Team == Team.Order);
        var hero = seat.Hero!;
        var killer = session.Participants.First(participant => participant.Team == Team.Chaos).Hero!;
        // The champion's armour and a pool no camp can empty, as a stat buff the hero's stat refreshes keep (a debuff's refresh, Burning's
        // or WormAttack's, rebuilds Armor from the stats).
        runtime.Buffs.AddBuff(hero, "B739Probe", BuffType.CombatEnhancer, 1e6, hero.NetId,
            new BuffPayload { Armor = ChampionArmor - hero.Armor, MaxHp = 50000 });
        var capture = new Capture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, seat, capture));

        // Every camp of the map spawns as its level script says, and is left alone until 20 minutes: the attack damage at spawn, at 10 and at 20.
        // A camp's first generation lands at 100 s + its GroupDelaySpawnTime (JungleScheduler.Start).
        var spawnAt = runtime.Waves!.LevelScript.NeutralCamps.ToDictionary(camp => camp.Number,
            camp => NeutralDefinitions.InitialSpawnSeconds + camp.GroupDelaySpawnTime);
        var samples = new Dictionary<uint, Sample>();
        var clock = 0.0;
        while (clock < 1200.0 - 1e-9)
        {
            runtime.Tick(0.25);
            clock += 0.25;
            foreach (var unit in runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive))
            {
                if (!samples.TryGetValue(unit.NetId, out var sample))
                    samples[unit.NetId] = sample = new Sample { Name = unit.Name, SpawnAt = spawnAt[unit.CampId], SpawnAd = unit.AttackDamage };
                if (clock >= 600.0) sample.Ad600 ??= unit.AttackDamage;
                if (clock >= 1200.0 - 1e-9) sample.Ad1200 ??= unit.AttackDamage;
            }
        }

        TestAssert.Near(ChampionArmor, hero.Armor, 1e-9, $"map {map}: the measured champion's armour");
        var ratio = data.LevelConstants.DamageRatios.UnitToHero;
        var lines = new List<string> { $"map {map}: dr_UnitToHero {ratio}, champion armour {hero.Armor}" };

        // Batch 758: the x1.43 stays on a hit that is not on a champion. Every living camp member's landed hit of 100 on a lane minion, through
        // the stage CombatService runs as a swing lands (ICombatHooks.ModifyBasicAttackDamage: the character script's CharOnHitUnit, then its
        // buffs' BuffOnHitUnit), comes out at 143 where its 1.0.0.132 script still multiplies (HitMultiplier132) and at 100 elsewhere (126: every
        // camp but the Worm). The minion dies right after, before any camp fights.
        var minion = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, runtime.Waves.ActiveLanes[0], clock, 0);
        foreach (var member in runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive)
                     .GroupBy(unit => unit.Name).Select(group => group.First()).OrderBy(unit => unit.Name, StringComparer.Ordinal))
        {
            var onMinion = runtime.Dependencies.CombatHooks.ModifyBasicAttackDamage(member, minion, 100.0, false, BasicAttackOutcome.Normal);
            var scripted = HitMultiplier132.Contains(member.Name) ? 143.0 : 100.0;
            lines.Add($"map {map} {member.Name,-18} landed hit of 100 on a lane minion: {onMinion:F2}");
            if (Math.Abs(onMinion - scripted) > 1e-9)
                failures.Add($"map {map} {member.Name}: a landed hit of 100 on a lane minion deals {onMinion:F2}, its script gives {scripted:F2}");
        }
        runtime.Combat.Kill(minion);
        foreach (var camp in runtime.Waves.LevelScript.NeutralCamps.OrderBy(camp => camp.Number))
        {
            var members = runtime.World.Entities.Values
                .Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive && unit.CampId == camp.Number)
                .OrderBy(unit => unit.NeutralMember).ToArray();
            // A camp spawns one of its groups (Map4's GroupsChance picks one); every member of it is alive at 20 minutes.
            if (!camp.Groups.Any(group => group.Count == members.Length))
            {
                failures.Add($"map {map} camp {camp.Number}: {members.Length} living members at 20 minutes");
                if (members.Length == 0) continue;
            }
            var first = members[0];
            var spot = Spot(grid, first, hero);
            if (spot is null)
            {
                failures.Add($"map {map} camp {camp.Number}: no walkable spot near {first.Name} that it sees");
                continue;
            }
            // A camp the footage shows fights the champion of the armour he had then (a stat buff on top of the probe's 54, removed after it).
            var armor = members.Select(member => FootageArmor.GetValueOrDefault(member.Name)).FirstOrDefault(value => value > 0.0, ChampionArmor);
            var footage = runtime.Buffs.AddBuff(hero, "B758FootageArmor", BuffType.CombatEnhancer, 1e6, hero.NetId,
                new BuffPayload { Armor = armor - hero.Armor });
            TestAssert.Near(armor, hero.Armor, 1e-9, $"map {map} camp {camp.Number}: the champion's armour");
            var mitigation = 100.0 / (100.0 + armor);
            hero.Position = spot.Value;
            hero.Waypoints.Clear();
            hero.Health = hero.MaxHealth;
            runtime.Delivery.Flush();
            capture.Packets.Clear();
            var ids = members.Select(unit => unit.NetId).ToHashSet();
            var seen = new HashSet<uint>();
            var foreign = new List<string>();
            var (lost, wired) = (0.0, 0.0);
            TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, first.NetId, 1.0, DamageType.True, DamageSourceKind.Spell),
                $"map {map} camp {camp.Number}: the champion's poke lands");
            foreach (var member in members) samples[member.NetId].Period = member.AttackPeriod;
            for (var step = 0; step < FightSteps + SettleSteps; step++)
            {
                if (step == FightSteps)
                    foreach (var member in members.Where(member => member.Alive))
                        runtime.Combat.ApplyHit(killer.NetId, member.NetId, 1e7, DamageType.True, DamageSourceKind.Spell);
                hero.Health = hero.MaxHealth;
                runtime.Tick(Step);
                clock += Step;
                lost += hero.MaxHealth - hero.Health;
                foreach (var hit in runtime.Combat.PendingHits)
                    if (ids.Contains(hit.SourceNetId) && hit.SourceKind == DamageSourceKind.Basic && seen.Add(hit.MissileNetId))
                        samples[hit.SourceNetId].Queued.Add((hit.Damage, hit.Outcome, runtime.World.Get(hit.SourceNetId)!.AttackDamage, clock));
                foreach (var packet in capture.Packets)
                {
                    var span = packet.Plaintext.Span;
                    if (span.Length >= 9 && span[0] == 0x0D && ids.Contains(BitConverter.ToUInt32(span[1..5])))
                        samples[BitConverter.ToUInt32(span[1..5])].Swings.Add(clock);
                    else if (span.Length >= 18 && span[0] == 0x69 && BitConverter.ToUInt32(span[6..10]) == hero.NetId)
                    {
                        var source = BitConverter.ToUInt32(span[1..5]);
                        var amount = BitConverter.ToSingle(span[14..18]);
                        wired += amount;
                        if (ids.Contains(source)) samples[source].Events.Add((amount, span[5], step >= FightSteps));
                        else foreign.Add($"{runtime.World.Get(source)?.Name ?? source.ToString()} {amount:F2}");
                    }
                }
                capture.Packets.Clear();
            }
            if (foreign.Count > 0) failures.Add($"map {map} camp {camp.Number}: hits from outside the camp [{string.Join(", ", foreign)}]");
            // Health the champion lost that no damage packet reported (a hit applied twice, or applied without its 0x68). The champion's own
            // regeneration in a step that hit it is the only way to lose less than the wire says.
            var fight = (FightSteps + SettleSteps) * Step;
            if (lost > wired + 0.5 || lost < wired - 0.5 - hero.HealthRegen * fight)
                failures.Add($"map {map} camp {camp.Number}: the champion lost {lost:F2} health, its wire reported {wired:F2}");
            lines.Add($"map {map} camp {camp.Number}: armour {armor} lost {lost:F2} wired {wired:F2}" +
                      (foreign.Count > 0 ? $" foreign {foreign.Count}" : ""));
            // Batch 932: a camp monster's swing on a champion takes the 132 line's ratio (1.0), not dr_UnitToHero.
            foreach (var member in members)
                lines.Add(Row(map, camp.Number, member, samples[member.NetId], data, CombatRules.AttackRatio(member, hero), armor, mitigation,
                    failures, pinned));
            if (footage is not null) runtime.Buffs.RemoveBuff(hero, footage.Slot);
            TestAssert.Near(ChampionArmor, hero.Armor, 1e-9, $"map {map} camp {camp.Number}: the champion's armour after the camp");
        }
        return lines;
    }

    private static string Row(int map, int camp, Entity member, Sample sample, GameData data, double ratio, double armor, double mitigation,
        List<string> failures, int[] pinned)
    {
        var label = $"map {map} camp {camp} m{member.NeutralMember} {sample.Name}";
        var record = data.Champion(member.Name).BaseDamage;
        // Batch 781: the AD the 1.0.0.132 scripts give it at spawn, at 10 and at 20 minutes (126: the record's, unchanged).
        double Scripted(double time) => ScriptedAd(member.Name, record, sample.SpawnAt, time);
        if (Math.Abs(sample.SpawnAd - Scripted(sample.SpawnAt)) > 1e-6 || sample.Ad600 is { } at600 && Math.Abs(at600 - Scripted(600.0)) > 1e-6 ||
            sample.Ad1200 is { } at1200 && Math.Abs(at1200 - Scripted(1200.0)) > 1e-6)
            failures.Add($"{label}: attack damage {sample.SpawnAd}/{sample.Ad600}/{sample.Ad1200} (spawn/10/20 min), its 132 scripts give " +
                         $"{Scripted(sample.SpawnAt)}/{Scripted(600.0)}/{Scripted(1200.0)} (record {record})");
        var crit = member.CritMultiplier;
        var worm = member.Name == "Worm";
        // A swing's hit on a champion as the 2011 footage shows it: the queued amount is the AD it swings with times the ratio (and the crit
        // multiplier), and only the armour comes on top (CharOnHitUnit's x1.43 does not reach a champion, batch 758). The Worm never swings:
        // WormAttack's TargetExecute deals its base AD. Batch 781: a 132 monster's AD can move during the fight (GlobalMonsterBuff), so a
        // landed hit is one of its queued swings after armour, and each queued swing is held to the AD the scripts give it then (below).
        double Expected(BasicAttackOutcome outcome) => worm ? member.AttackDamage * mitigation :
            member.AttackDamage * ratio * (outcome == BasicAttackOutcome.Critical ? crit : 1.0) * mitigation;
        bool IsHit((double Damage, byte Type, bool AfterKill) item) => (item.Type & 0x7f) is 3 or 4 && (worm
            ? Math.Abs(item.Damage - Expected(BasicAttackOutcome.Normal)) < 0.01
            : sample.Queued.Any(queued => Math.Abs(item.Damage - queued.Damage * mitigation) < 0.01));
        var landed = sample.Events.Where(item => !item.AfterKill).ToArray();
        var swingHits = landed.Where(IsHit).ToArray();
        var others = landed.Where(item => !IsHit(item)).ToArray();
        var queued = sample.Queued.Count;
        foreach (var (damage, outcome, ad, at) in sample.Queued)
        {
            if (Math.Abs(ad - Scripted(at)) > 1e-6)
                failures.Add($"{label}: swung at {at:F1} s with attack damage {ad:F3}, its 132 scripts give {Scripted(at):F3}");
            var raw = ad * ratio * (outcome == BasicAttackOutcome.Critical ? crit : 1.0);
            if (Math.Abs(damage - raw) > 1e-6) failures.Add($"{label}: queued {damage:F3} for a {outcome} swing, the ratio gives {raw:F3}");
        }
        var swings = sample.Swings.Count;
        var period = sample.Period ?? member.AttackPeriod;
        var rate = swings > 1 ? (swings - 1) / (sample.Swings[^1] - sample.Swings[0]) : double.NaN;
        // The swings are stamped at the end of the 0.1 s read step, so the span of n swings may read one step short of (n - 1) periods.
        if (swings > 1 && sample.Swings[^1] - sample.Swings[0] < (swings - 1) * period - Step - 1e-6)
            failures.Add($"{label}: {rate:F3} swings a second, the period it fights with gives {1.0 / period:F3}");
        if (!worm && swingHits.Length > swings) failures.Add($"{label}: {swingHits.Length} swing hits from {swings} swings");
        if (!worm && swings < 8) failures.Add($"{label}: only {swings} swings in the fight");
        // Damage events that are not one of the swings' own hits: only the Dragon's Burning and the Worm's scripted spells deal any.
        var extra = others.Select(item => item.Damage).Distinct().ToArray();
        var allowed = member.Name switch
        {
            "Dragon" => others.All(item => Math.Abs(item.Damage - 15.0) < 1e-4 && (item.Type & 0x7f) == 4),
            "Worm" => true,
            _ => others.Length == 0,
        };
        if (!allowed) failures.Add($"{label}: other damage events [{string.Join(", ", others.Select(item => $"{item.Damage:F2}/{item.Type:X2}"))}]");
        // The rest of the Dragon's CharOnHitUnit still runs on a champion: its Burning ticks.
        if (member.Name == "Dragon" && others.Length == 0) failures.Add($"{label}: no Burning tick from its swings");
        // The footage's own numbers, read off the wire: every landed swing of a pinned monster and result on a champion of the pinned armour.
        for (var index = 0; index < Pins.Length; index++)
        {
            var pin = Pins[index];
            if (pin.Monster != member.Name || Math.Abs(pin.Armor - armor) > 1e-9) continue;
            var hits = landed.Where(item => (item.Type & 0x7f) == (pin.Critical ? 3 : 4)).Select(item => item.Damage).ToArray();
            pinned[index] += hits.Length;
            var off = hits.Where(hit => Math.Abs(hit - pin.Hit) > 0.01).ToArray();
            if (off.Length > 0)
                failures.Add($"{label}: {off.Length} of {hits.Length} {(pin.Critical ? "critical " : "")}hit(s) on armour {armor} deal " +
                             $"[{string.Join(", ", off.Distinct().Select(hit => hit.ToString("F2")))}], the footage shows {pin.Hit:F2} ({pin.Video})");
        }
        var crits = swingHits.Count(item => (item.Type & 0x7f) == 3);
        var mean = swingHits.Length > 0 ? swingHits.Average(item => item.Damage) : double.NaN;
        var perSwing = swings > 0 ? landed.Sum(item => item.Damage) / swings : double.NaN;
        var late = sample.Events.Where(item => item.AfterKill).Select(item => item.Damage.ToString("F2")).ToArray();
        // Batch 742 (owner, 11 November 2026: "Full hit, like the rest"): a shot still in the air when its monster dies lands as the swing
        // would have with the monster alive (on a champion that is its plain hit since batch 758); the Dragon's Burning ticks on, and so
        // does a debuff the Worm's scripted spells left on the champion (WrathDebuff's ramping ticks; the batch 758 name's draws show one).
        var lateOff = sample.Events.Where(item => item.AfterKill && !IsHit(item) && !worm &&
                                                  !(member.Name == "Dragon" && Math.Abs(item.Damage - 15.0) < 1e-4)).ToArray();
        if (lateOff.Length > 0)
            failures.Add($"{label}: after its death it dealt [{string.Join(", ", lateOff.Select(item => item.Damage.ToString("F2")))}], " +
                         $"its swing deals {Expected(BasicAttackOutcome.Normal):F2}");
        return $"{label,-34} record AD {record,6:F1} AD(spawn/10/20 min) {sample.SpawnAd:F1}/{sample.Ad600:F1}/{sample.Ad1200:F1} " +
               $"period {period:F3} rate {rate:F3} (its period {1.0 / period:F3}) crit {member.CritChance:P0} x{crit:F1} | " +
               $"swings {swings} queued {queued} hits {swingHits.Length} (crit {crits}) mean {mean:F2} expected {Expected(BasicAttackOutcome.Normal):F2} " +
               $"ratio {mean / Expected(BasicAttackOutcome.Normal):F3} | other {others.Length} [{string.Join(",", extra.Select(value => value.ToString("F2")))}] " +
               $"per swing {perSwing:F2} ({perSwing / Expected(BasicAttackOutcome.Normal):F3})" + (late.Length > 0 ? $" | after kill [{string.Join(",", late)}]" : "");
    }

    // The first walkable spot 200 to 400 units from the member, in eight directions, that the member sees.
    private static Position2? Spot(NavigationGrid grid, Entity member, Entity hero)
    {
        foreach (var reach in new[] { 200.0, 300.0, 400.0 })
            foreach (var (dx, dz) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0),
                         (0.7071, 0.7071), (-0.7071, 0.7071), (0.7071, -0.7071), (-0.7071, -0.7071) })
            {
                var candidate = new Position2(member.Position.X + dx * reach, member.Position.Z + dz * reach);
                if (grid.IsMovementWalkable(candidate) &&
                    grid.HasUnitVision(member.Position, member.SelectionRadius, candidate, hero.SelectionRadius)) return candidate;
            }
        return null;
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
