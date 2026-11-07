using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static partial class MinionChecks
{
    /// <summary>
    /// Batch 705: the members of a wave spawn in the order the stock 1.0.0.126 barracks update picks them, one per
    /// stagger step (the client, lines 876306-876323: melee first, then the archer,
    /// which is the ranged minion (our <see cref="MinionType.Caster"/>), then the caster, which is the cannon (our
    /// <see cref="MinionType.Siege"/>), then super). A cannon wave is three melee, three ranged and the cannon last; an
    /// affected lane's super wave has no cannon and ends in its super. Owner decision (7 November 2026): keep the
    /// October 2011 order. The wiki's V1.0.0.126 notes (dated 5 October 2011) change no wave order.
    /// </summary>
    [Check("minions.wave-spawn-order-2011")]
    public static void WaveMembersSpawnInThe2011Order()
    {
        var lane = MapDefinitions.Lanes[0];
        var plainWave = new[]
        {
            MinionType.Melee, MinionType.Melee, MinionType.Melee,
            MinionType.Caster, MinionType.Caster, MinionType.Caster,
        };
        var cannonWave = plainWave.Append(MinionType.Siege).ToArray();

        // The scheduler's own queue at the nominal wave times (90, 120 and 150 s; the script's 30 s interval). Each tick
        // returns the members that are due, in queue order, and the 0.8 s stagger puts all of a wave within 4.8 s.
        var world = new WorldState();
        var scheduler = new WaveScheduler(world, NavigationGrid.LoadDefault(), new FixturePackets(),
            LevelScriptFixtures.Init(world), firstSpawnSeconds: 90.0);
        scheduler.Start(0, new[] { lane });
        TestAssert.Equal(plainWave, OrderSpawns(scheduler, lane, 90.0, 96.0), "the first wave has no cannon");
        TestAssert.Equal(plainWave, OrderSpawns(scheduler, lane, 120.0, 126.0), "the second wave has no cannon");
        TestAssert.Equal(cannonWave, OrderSpawns(scheduler, lane, 150.0, 156.0), "the cannon is the last member of the third wave");

        // One enemy inhibitor down (the Chaos one on lane 0): that lane's wave swaps its cannon for a super, which comes last.
        var fallen = InhibitorWorld(1, Team.Order);
        TestAssert.Equal(new[]
        {
            MinionType.Melee, MinionType.Melee, MinionType.Melee,
            MinionType.Caster, MinionType.Caster, MinionType.Caster, MinionType.Super,
        }, LevelScriptFixtures.Wave(fallen, Team.Order, lane, 2).ToArray(),
            "the affected lane's super wave ends in its super");
        TestAssert.Equal(cannonWave, LevelScriptFixtures.Wave(fallen, Team.Order, MapDefinitions.Lanes[1], 2).ToArray(),
            "an unaffected lane keeps its cannon as the last member");
    }

    private static MinionType[] OrderSpawns(WaveScheduler scheduler, string lane, params double[] ticks)
    {
        var spawned = new List<MinionType>();
        foreach (var now in ticks)
            foreach (var entity in scheduler.Tick(now))
                if (entity.Team == Team.Order && entity.MinionLane == lane) spawned.Add(entity.MinionType!.Value);
        return spawned.ToArray();
    }
}
