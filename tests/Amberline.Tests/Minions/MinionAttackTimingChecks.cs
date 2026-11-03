using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static class MinionAttackTimingChecks
{
    // Independent patch-126 expectations from character offsets and the client's own
    // stat helpers, not the production stat table.
    private static readonly (MinionType Type, double Period, double Order, double Chaos)[] Cases =
    [
        (MinionType.Melee, 0.8, 0.33336, 0.345336),
        (MinionType.Caster, 1.4928, 0.46082736, 0.46082736),
        (MinionType.Siege, 1.0, 0.345, 0.55),
        (MinionType.Super, 1.44, 0.554976, 0.560016),
    ];

    internal static double ExpectedWindup(MinionType type, Team team)
    {
        var row = Cases.Single(value => value.Type == type);
        return team == Team.Order ? row.Order : row.Chaos;
    }

    [Check("minions-native-attack-timing")]
    public static void SpawnAndDamageTiming()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        foreach (var row in Cases)
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var elapsed in new[] { 0.0, 540.0 })
        {
            var expected = ExpectedWindup(row.Type, team);
            var source = runtime.Waves!.SpawnFixture(team, row.Type, 0, "C", 0.0, elapsed);
            source.Position = new(7000.0, 7000.0);
            source.Waypoints.Clear();
            source.Target = null;
            var label = $"{source.Name} at {elapsed}";
            var data = InibinReader.LoadData($"Characters/{source.Name}/{source.Name}.inibin");
            var castOffset = data[InibinReader.KeyHash("Data*AttackDelayCastOffsetPercent")].AsDouble();
            TestAssert.Near(expected, row.Period * (0.3 + castOffset), 1e-8,
                label + " matches shipped cast offset");
            foreach (var slot in new[] { "ExtraAttack1", "ExtraAttack2", "ExtraAttack3", "CritAttack", "MissAttack", "DodgeAttack" })
            foreach (var field in new[] { "AttackDelayOffsetPercent", "AttackDelayCastOffsetPercent" })
                TestAssert.True(!data.ContainsKey(InibinReader.KeyHash($"Data*{slot}_{field}")),
                    label + $" inherits {slot} {field}");
            TestAssert.Near(row.Period, source.AttackPeriod, 1e-8, label + " period");
            TestAssert.Near(expected, source.AttackWindup, 1e-8, label + " windup");
            var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team == Team.Order ? Team.Chaos : Team.Order,
                new(7060.0, 7000.0)) { Health = 10000.0, MaxHealth = 10000.0 });
            TestAssert.True(runtime.Combat.QueueBasicAttack(source, target,
                new AttackRolls(1.0, 1.0, 1.0)), label + " starts an attack");
            TestAssert.Near(expected, runtime.Combat.PendingHits.Single().WindupRemaining,
                1e-8, label + " queued windup");
            runtime.Combat.CombatTick(expected - 0.000001, regenerate: false);
            TestAssert.Near(10000.0, target.Health, 1e-8, label + " no early hit");
            runtime.Combat.CombatTick(0.000002, regenerate: false);
            TestAssert.Near(10000.0, target.Health, 1e-8, label + " crossing tick keeps windup");
            var flight = source.MissileSpeed > 0.0 ? 60.0 / source.MissileSpeed : 0.0;
            if (flight > 0.0)
            {
                runtime.Combat.CombatTick(flight - 0.000001, regenerate: false);
                TestAssert.Near(10000.0, target.Health, 1e-8, label + " no hit before missile arrival");
            }
            runtime.Combat.CombatTick(0.000002, regenerate: false);
            TestAssert.Near(10000.0 - source.AttackDamage, target.Health, 1e-7,
                label + " one hit after windup and flight");
            TestAssert.Equal(0, runtime.Combat.PendingHits.Count, label + " consumes one attack");
        }
    }

    [Check("minions-attack-speed-windup")]
    public static void AttackSpeedChangesPreserveCastFraction()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        foreach (var row in Cases)
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var source = runtime.Waves!.SpawnFixture(team, row.Type, 0, "C", 0.0, 0.0);
            var period = source.AttackPeriod;
            var windup = source.AttackWindup;
            var slow = runtime.Buffs.AddBuff(source, "TimingSlow", BuffType.Slow, 10.0, 0,
                new BuffPayload { AttackSpeedMult = -0.5 })!;
            var haste = runtime.Buffs.AddBuff(source, "TimingHaste", BuffType.Aura, 10.0, 0,
                new BuffPayload { AttackSpeedMod = 0.25 })!;
            TestAssert.Near(period * 1.6, source.AttackPeriod, 1e-8, source.Name + " combined period");
            TestAssert.Near(windup * 1.6, source.AttackWindup, 1e-8, source.Name + " combined windup");
            runtime.Buffs.RemoveBuff(source, slow.Slot);
            TestAssert.Near(windup / 1.25, source.AttackWindup, 1e-8, source.Name + " haste remains");
            runtime.Buffs.RemoveBuff(source, haste.Slot);
            TestAssert.Near(period, source.AttackPeriod, 1e-8, source.Name + " restored period");
            TestAssert.Near(windup, source.AttackWindup, 1e-8, source.Name + " restored windup");
        }
    }
}
