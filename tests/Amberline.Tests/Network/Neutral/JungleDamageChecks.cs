using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network.Neutral;

// Batch 739 (owner, 11 November 2026: "jungle is doing more damage than intended; like almost double on certain monsters"). Every camp
// member of Summoner's Rift (Map1) and the Twisted Treeline (Map4), as the shipped level scripts spawn them, swings at a champion of
// armour 54 in a 3v3 production match (the server's own map composition, GameData.ConfigureDefault). Each swing is read off the
// champion's own wire (0x0D swing, 0x68 damage) and its queued hit (CombatService.PendingHits), and the champion's health loss is
// compared with the wire. The shipped expectation of a swing on a champion: the record's AD, times the map's dr_UnitToHero (0.7), the
// record's CritDamageBonus on a critical roll (only wolf, GiantWolf and Worm have a BaseCritChance; V3.14 "Giant Wolf: No longer
// critically strikes"), CharOnHitUnit's x1.43 (every jungle CharScript), then armour. Besides the swings only two shipped scripts deal
// damage: the Dragon's CharOnHitUnit adds Burning (15 TRUE_DAMAGE a second, BUFF_RENEW_EXISTING), and the Worm attacks only through its
// scripted spells (WormAttack: its base AD as PHYSICAL_DAMAGE, no ratio, no x1.43; Propel 300 and SweepingBlow 200 MAGIC_DAMAGE). Batch 739
// measured every member at this expectation, so the owner's excess is the scripts' own.
// The check is Serial: it swaps GameData's default map, as the server does for a match on another map.
public static class JungleDamageChecks
{
    private const double ChampionArmor = 54.0;
    private const double Step = 0.1;
    // The fight lasts 14 s; the camp is killed at 14 s and the 5 s after it are read for hits that land late (the Dragon's 4 s Burning).
    private const int FightSteps = 140;
    private const int SettleSteps = 50;

    [Check("neutral.jungle-hits-on-a-champion-match-the-shipped-scripts", Serial = true, TimeoutSeconds = 300)]
    public static void JungleHitsOnAChampionMatchTheShippedScripts()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();
        try
        {
            foreach (var map in new[] { GameMaps.Normal, GameMaps.TwistedTreeline })
                foreach (var row in Measure(map, failures)) Console.WriteLine(row);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
        TestAssert.True(failures.Count == 0, $"{failures.Count} finding(s): " + string.Join("; ", failures));
    }

    private sealed class Sample
    {
        public required string Name { get; init; }
        public double SpawnAd { get; init; }
        public double? Ad600 { get; set; }
        public double? Ad1200 { get; set; }
        public List<double> Swings { get; } = [];
        public List<(double Damage, BasicAttackOutcome Outcome)> Queued { get; } = [];
        public List<(double Damage, byte Type, bool AfterKill)> Events { get; } = [];
    }

    private static List<string> Measure(int map, List<string> failures)
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
        var mitigation = 100.0 / (100.0 + ChampionArmor);
        var lines = new List<string> { $"map {map}: dr_UnitToHero {ratio}, champion armour {hero.Armor}" };
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
            lines.Add($"map {map} camp {camp.Number}: lost {lost:F2} wired {wired:F2}" + (foreign.Count > 0 ? $" foreign {foreign.Count}" : ""));
            foreach (var member in members) lines.Add(Row(map, camp.Number, member, samples[member.NetId], data, ratio, mitigation, failures));
        }
        return lines;
    }

    private static string Row(int map, int camp, Entity member, Sample sample, GameData data, double ratio, double mitigation,
        List<string> failures)
    {
        var label = $"map {map} camp {camp} m{member.NeutralMember} {sample.Name}";
        var record = data.Champion(member.Name).BaseDamage;
        if (sample.SpawnAd != record || sample.Ad600 is { } at600 && at600 != record || sample.Ad1200 is { } at1200 && at1200 != record)
            failures.Add($"{label}: attack damage {sample.SpawnAd}/{sample.Ad600}/{sample.Ad1200} (spawn/10/20 min), the record has {record}");
        var crit = member.CritMultiplier;
        var worm = member.Name == "Worm";
        // A swing's hit as the shipped scripts deal it: the queued amount is the record AD times the ratio (and the crit multiplier),
        // and CharOnHitUnit's x1.43 and the armour come on top. The Worm never swings: WormAttack's TargetExecute deals its base AD.
        double Expected(BasicAttackOutcome outcome) => worm ? member.AttackDamage * mitigation :
            member.AttackDamage * ratio * (outcome == BasicAttackOutcome.Critical ? crit : 1.0) * 1.43 * mitigation;
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
        var period = member.AttackPeriod;
        var rate = swings > 1 ? (swings - 1) / (sample.Swings[^1] - sample.Swings[0]) : double.NaN;
        // The swings are stamped at the end of the 0.1 s read step, so the span of n swings may read one step short of (n - 1) periods.
        if (swings > 1 && sample.Swings[^1] - sample.Swings[0] < (swings - 1) * period - Step - 1e-6)
            failures.Add($"{label}: {rate:F3} swings a second, the record's period gives {1.0 / period:F3}");
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
        var crits = swingHits.Count(item => (item.Type & 0x7f) == 3);
        var mean = swingHits.Length > 0 ? swingHits.Average(item => item.Damage) : double.NaN;
        var perSwing = swings > 0 ? landed.Sum(item => item.Damage) / swings : double.NaN;
        var late = sample.Events.Where(item => item.AfterKill).Select(item => item.Damage.ToString("F2")).ToArray();
        // Batch 742 (owner, 11 November 2026: "Full hit, like the rest"): a shot still in the air when its monster dies lands as the swing
        // would have with the monster alive (blueDragon 178.75, not 125.00 without its CharOnHitUnit); the Dragon's Burning ticks on.
        var lateOff = sample.Events.Where(item => item.AfterKill && !IsHit(item) &&
                                                  !(member.Name == "Dragon" && Math.Abs(item.Damage - 15.0) < 1e-4)).ToArray();
        if (lateOff.Length > 0)
            failures.Add($"{label}: after its death it dealt [{string.Join(", ", lateOff.Select(item => item.Damage.ToString("F2")))}], " +
                         $"its swing deals {Expected(BasicAttackOutcome.Normal):F2}");
        return $"{label,-34} record AD {record,6:F1} AD(spawn/10/20 min) {sample.SpawnAd:F1}/{sample.Ad600:F1}/{sample.Ad1200:F1} " +
               $"period {period:F3} rate {rate:F3} (record {1.0 / period:F3}) crit {member.CritChance:P0} x{crit:F1} | " +
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
