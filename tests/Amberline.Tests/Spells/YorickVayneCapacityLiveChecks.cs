using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-scoped-buff-capacity-caster-isolation")]
    public static void ScopedBuffCapacityKeepsCasterOwnership()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Vayne", team, enemyChampion: "Vayne");
            var first = match.Hero(0);
            var second = match.Hero(1);
            var target = match.Runtime.World.Register(new Entity(
                match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Monster,
                UnitType.Monster, Team.Neutral, first.Position + new Position2(100, 100))
            { Name = "ScopedCapacityNeutral", MaxHealth = 5000, Health = 5000, Radius = 30 });
            FillChampionCapacity(match.Runtime.Buffs, target, 63);
            match.Runtime.Buffs.AddBuff(target, "ScopedCapacityMark", BuffType.Internal,
                60.0, second.NetId, new BuffPayload { PerCaster = true });
            TestAssert.False(BuffLifecycle.CanAddScopedBuffs(target,
                ("ScopedCapacityMark", first.NetId)),
                "another caster's same-name mark does not supply a reusable slot");
            TestAssert.True(BuffLifecycle.CanAddScopedBuffs(target,
                ("ScopedCapacityMark", second.NetId)),
                "same-caster renewal fits at 64 occupied slots");
            TestAssert.True(BuffLifecycle.CanAddScopedBuffs(target,
                ("ScopedCapacityMark", null)),
                "ordinary same-name replacement retains the existing helper's behavior");
            var invalidShadow = new BuffState("ScopedCapacityMark",
                new BuffPayload { PerCaster = true }, BuffType.Internal, 64, 60.0,
                second.NetId);
            target.Buffs.Insert(0, invalidShadow);
            TestAssert.False(BuffLifecycle.CanAddScopedBuffs(target,
                ("ScopedCapacityMark", second.NetId)),
                "admission follows AddBuff's first matching row, not a later valid duplicate");
            target.Buffs.Remove(invalidShadow);
        }
    }
}
