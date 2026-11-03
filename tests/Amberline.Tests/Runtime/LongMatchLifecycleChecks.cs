using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

public static class LongMatchLifecycleChecks
{
    [Check("runtime.long-match-natural-dragon-worm-camp-and-waves")]
    public static void NaturalObjectivesRespawnsAndContinuingLaneWaves()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, solo: true, originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0, minionFidelity: true);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        // Keep this scheduler fixture alive if unattended lane combat reaches a Nexus.
        foreach (var nexus in runtime.World.Entities.Values.Where(entity =>
                     entity.Kind == EntityKind.Nexus)) nexus.GodMode = true;
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "authenticate one human seat");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "ready one human seat");
        TestAssert.True(runtime.Session.Start(), "start natural production schedules");
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(26, 264);
        hero.HeroAutoAcquire = false;
        hero.Waypoints.Clear();
        hero.Target = null;
        runtime.Delivery.Flush();

        var camps = runtime.Delivery.NeutralCamps ??
            throw new InvalidOperationException("production jungle state is unavailable");
        var waves = runtime.Waves ??
            throw new InvalidOperationException("production wave scheduler is unavailable");
        var steps = 0;
        void AdvanceTo(double target)
        {
            while (runtime.Combat.CurrentTime + 1e-7 < target)
            {
                TestAssert.True(steps++ < 1900, "bounded 905-second production simulation");
                runtime.Tick(Math.Min(.5, target - runtime.Combat.CurrentTime));
            }
            TestAssert.Near(target, runtime.Combat.CurrentTime, 1e-6,
                $"natural simulation reached {target:R}s without a clock jump");
        }

        AdvanceTo(89.5);
        TestAssert.Equal(0, waves.WaveIndex, "lane waves have not started early");
        AdvanceTo(90.1);
        TestAssert.Equal(1, waves.WaveIndex, "first lane wave starts naturally");
        AdvanceTo(99.5);
        TestAssert.False(camps[2].Active, "wolf camp has not spawned before its deadline");
        AdvanceTo(100.1);
        TestAssert.True(camps[2].Active && camps[2].SpawnCycles == 1,
            "multi-member wolf camp spawned through the production phase");
        var wolfIds = camps[2].MemberNetIds.ToArray();
        TestAssert.Equal(3, wolfIds.Length, "wolf camp has its shipped three members");
        foreach (var id in wolfIds[..^1])
        {
            TestAssert.True(runtime.Combat.Kill(runtime.World.Get(id)!, hero.NetId),
                "real combat death retires a wolf member");
            TestAssert.True(camps[2].Active,
                "a multi-member camp remains active until its final member dies");
        }
        TestAssert.True(runtime.Combat.Kill(runtime.World.Get(wolfIds[^1])!, hero.NetId),
            "real combat death retires the last wolf member");
        TestAssert.False(camps[2].Active, "empty camp schedules its respawn");
        var wolfRespawnAt = camps[2].NextSpawnAt ??
            throw new InvalidOperationException("wolf respawn was not scheduled");
        TestAssert.Near(runtime.Combat.CurrentTime + NeutralDefinitions.Camps[2].RespawnDelay,
            wolfRespawnAt, 1e-6, "wolf respawn uses the source camp delay");

        AdvanceTo(149.5);
        TestAssert.False(camps[6].Active, "Dragon has not spawned before 150 seconds");
        AdvanceTo(150.1);
        TestAssert.True(camps[6].Active && camps[6].SpawnCycles == 1,
            "Dragon spawns through the natural production phase");
        var firstDragonId = camps[6].MemberNetIds.Single();
        TestAssert.True(runtime.Combat.Kill(runtime.World.Get(firstDragonId)!, hero.NetId),
            "Dragon death enters production combat and neutral hooks");
        TestAssert.False(camps[6].Active, "Dragon death schedules an empty camp");
        var dragonRespawnAt = camps[6].NextSpawnAt ??
            throw new InvalidOperationException("Dragon respawn was not scheduled");
        TestAssert.Near(runtime.Combat.CurrentTime + NeutralDefinitions.Camps[6].RespawnDelay,
            dragonRespawnAt, 1e-6, "Dragon respawn uses the source epic delay");
        TestAssert.Equal(1, hero.MatchStatistics.SuperMonsterKills,
            "one real Dragon kill credits one epic objective");

        AdvanceTo(wolfRespawnAt - .1);
        TestAssert.False(camps[2].Active, "wolf camp does not respawn early");
        AdvanceTo(wolfRespawnAt + .1);
        TestAssert.True(camps[2].Active && camps[2].SpawnCycles == 2 &&
            camps[2].MemberNetIds.Count == 3 &&
            !camps[2].MemberNetIds.Intersect(wolfIds).Any(),
            "wolf camp respawns one fresh full generation");

        AdvanceTo(dragonRespawnAt - .1);
        TestAssert.False(camps[6].Active, "Dragon does not respawn early");
        AdvanceTo(dragonRespawnAt + .1);
        TestAssert.True(camps[6].Active && camps[6].SpawnCycles == 2 &&
            camps[6].MemberNetIds.Single() != firstDragonId,
            "Dragon returns once with a new authoritative actor");

        AdvanceTo(899.5);
        TestAssert.False(camps[12].Active, "Worm has not spawned before 900 seconds");
        AdvanceTo(900.1);
        TestAssert.True(camps[12].Active && camps[12].SpawnCycles == 1 &&
            runtime.World.Get(camps[12].MemberNetIds.Single()) is { Alive: true, Name: "Worm" },
            "Worm naturally spawns after the full long-match simulation");
        TestAssert.Equal(28, waves.WaveIndex,
            "mid-lane waves continue through the Worm spawn rather than ending at the soak boundary");
        AdvanceTo(905.0);
        TestAssert.Equal(0, waves.Pending.Count,
            "the 900-second lane wave drains its natural staggered spawns");
        TestAssert.True(runtime.World.Entities.Values.Any(entity =>
            entity.Kind == EntityKind.Minion && entity.SpawnScheduledAt >= 900.0 - 1e-6 &&
            entity.SpawnScheduledAt <= 905.0),
            "late lane-wave actors were actually registered in the production world");
        TestAssert.False(runtime.Combat.GameOver,
            "the small-roster fixture remains live through objective and wave deadlines");
    }
}
