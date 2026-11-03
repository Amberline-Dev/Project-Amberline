using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

public static partial class NeutralAiChecks
{



    private static void FillNeutralSlots(NeutralHarness harness, Entity target, int total)
    {
        for (var index = 0; target.Buffs.Count < total; index++)
            TestAssert.True(harness.AddBuff(target, $"WormCapacityProbe{index}",
                BuffType.Internal, 60.0, target.NetId) is not null);
        TestAssert.Equal(total, target.Buffs.Count);
        harness.Queue.Clear();
    }

    private static void StepWorm(NeutralHarness harness, double dt)
    {
        harness.World.Clock.Advance(dt);
        harness.Movement.Tick(dt);
        harness.Ai.Tick(dt);
        harness.Combat.CombatTick(dt, regenerate: false);
        harness.TickBuffs(dt);
    }
}
