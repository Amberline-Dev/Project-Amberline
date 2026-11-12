using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

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
// The check is Serial: it swaps GameData's default map, as the server does for a match on another map.
public static class JungleDamageChecks
{
    private const double ChampionArmor = 54.0;
    private const double Step = 0.1;
    // The fight lasts 14 s; the camp is killed at 14 s and the 5 s after it are read for hits that land late (the Dragon's 4 s Burning).
    private const int FightSteps = 140;
    private const int SettleSteps = 50;

    // The champion's armour in the footage while he cleared each camp (HUD stats panel, 11.5 + 3.5 x level): the camp's other members share it.
    private static readonly Dictionary<string, double> FootageArmor = new()
    {
        ["Golem"] = 15.0, ["GiantWolf"] = 18.5, ["AncientGolem"] = 22.0, ["LizardElder"] = 29.0, ["LesserWraith"] = 32.5,
    };

    // The footage's hits (JUNGLE-REPORT.md, per-monster averages; the HUD shows integers, so each reading is +/- 1): AD x 0.7 (x crit) x
    // 100/(100 + armour) at the armour he had. Every landed swing of the monster and result on a champion of that armour must be this one.
    private static readonly (string Monster, bool Critical, double Armor, double Hit, string Video)[] Pins =
    [
        ("Golem", false, 15.0, 39.57, "level 1, 18 hits, mean 39.56"),
        ("GiantWolf", false, 18.5, 15.36, "level 2, 1 hit, 16"),
        ("GiantWolf", true, 18.5, 46.08, "level 2, critical x3, 2 hits, 47 and 46"),
        ("wolf", false, 18.5, 10.63, "level 2, 3 hits, mean 10.33"),
        ("wolf", true, 18.5, 21.27, "level 2, critical x2, 2 hits, 21 and 22"),
        ("AncientGolem", false, 22.0, 63.11, "level 3, 5 hits, mean 63.20"),
        ("YoungLizard", false, 22.0, 10.33, "level 3, 14 hits, mean 10.21"),
        ("LizardElder", false, 29.0, 59.69, "level 5, 19 hits, mean 59.68"),
        ("YoungLizard", false, 29.0, 9.77, "level 5, 2 hits, mean 9.50"),
        ("LesserWraith", false, 32.5, 18.49, "level 6, 9 hits, mean 18.33"),
    ];

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
        public double SpawnAd { get; init; }
        public double? Ad600 { get; set; }
        public double? Ad1200 { get; set; }
        // The period it fights with: the record's, or faster under its own script buff (RabidWolf's CrestOfNaturesFury, +0.2 attack speed).
        public double? Period { get; set; }
        public List<double> Swings { get; } = [];
        public List<(double Damage, BasicAttackOutcome Outcome)> Queued { get; } = [];
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
        var samples = new Dictionary<uint, Sample>();
        var clock = 0.0;
        while (clock < 1200.0 - 1e-9)
        {
            runtime.Tick(0.25);
            clock += 0.25;
            foreach (var unit in runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive))
            {
                if (!samples.TryGetValue(unit.NetId, out var sample))
                    samples[unit.NetId] = sample = new Sample { Name = unit.Name, SpawnAd = unit.AttackDamage };
                if (clock >= 600.0) sample.Ad600 ??= unit.AttackDamage;
                if (clock >= 1200.0 - 1e-9) sample.Ad1200 ??= unit.AttackDamage;
            }
        }

        TestAssert.Near(ChampionArmor, hero.Armor, 1e-9, $"map {map}: the measured champion's armour");
        var ratio = data.LevelConstants.DamageRatios.UnitToHero;
        var lines = new List<string> { $"map {map}: dr_UnitToHero {ratio}, champion armour {hero.Armor}" };

        // Batch 758: the x1.43 stays on a hit that is not on a champion. Every living camp member's landed hit of 100 on a lane minion, through
        // the stage CombatService runs as a swing lands (ICombatHooks.ModifyBasicAttackDamage: the character script's CharOnHitUnit, then its
        // buffs' BuffOnHitUnit), comes out at 143; the Worm's script has no CharOnHitUnit. The minion dies right after, before any camp fights.
        var minion = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, runtime.Waves.ActiveLanes[0], clock, 0);
        foreach (var member in runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive)
                     .GroupBy(unit => unit.Name).Select(group => group.First()).OrderBy(unit => unit.Name, StringComparer.Ordinal))
        {
            var onMinion = runtime.Dependencies.CombatHooks.ModifyBasicAttackDamage(member, minion, 100.0, false, BasicAttackOutcome.Normal);
            var scripted = member.Name == "Worm" ? 100.0 : 143.0;
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
                        samples[hit.SourceNetId].Queued.Add((hit.Damage, hit.Outcome));
                foreach (var packet in capture.Packets)
                {
                    var span = packet.Plaintext.Span;
                    if (span.Length >= 9 && span[0] == 0x0D && ids.Contains(BitConverter.ToUInt32(span[1..5])))
                        samples[BitConverter.ToUInt32(span[1..5])].Swings.Add(clock);
                    else if (span.Length >= 18 && span[0] == 0x68 && BitConverter.ToUInt32(span[6..10]) == hero.NetId)
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
            foreach (var member in members)
                lines.Add(Row(map, camp.Number, member, samples[member.NetId], data, ratio, armor, mitigation, failures, pinned));
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
        if (sample.SpawnAd != record || sample.Ad600 is { } at600 && at600 != record || sample.Ad1200 is { } at1200 && at1200 != record)
            failures.Add($"{label}: attack damage {sample.SpawnAd}/{sample.Ad600}/{sample.Ad1200} (spawn/10/20 min), the record has {record}");
        var crit = member.CritMultiplier;
        var worm = member.Name == "Worm";
        // A swing's hit on a champion as the 2011 footage shows it: the queued amount is the record AD times the ratio (and the crit
        // multiplier), and only the armour comes on top (CharOnHitUnit's x1.43 does not reach a champion, batch 758). The Worm never swings:
        // WormAttack's TargetExecute deals its base AD.
        double Expected(BasicAttackOutcome outcome) => worm ? member.AttackDamage * mitigation :
            member.AttackDamage * ratio * (outcome == BasicAttackOutcome.Critical ? crit : 1.0) * mitigation;
        bool IsHit((double Damage, byte Type, bool AfterKill) item) => (item.Type & 0x7f) is 3 or 4 &&
            (Math.Abs(item.Damage - Expected(BasicAttackOutcome.Normal)) < 0.01 || Math.Abs(item.Damage - Expected(BasicAttackOutcome.Critical)) < 0.01);
        var landed = sample.Events.Where(item => !item.AfterKill).ToArray();
        var swingHits = landed.Where(IsHit).ToArray();
        var others = landed.Where(item => !IsHit(item)).ToArray();
        var queued = sample.Queued.Count;
        foreach (var (damage, outcome) in sample.Queued)
        {
            var raw = member.AttackDamage * ratio * (outcome == BasicAttackOutcome.Critical ? crit : 1.0);
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
