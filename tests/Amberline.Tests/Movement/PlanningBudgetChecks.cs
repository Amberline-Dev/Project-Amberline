using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 20: automatic hero route planning is bounded. The owner's 27-minute playtest froze every unit for
/// 0.3 to 1.1 s at a time (client ping 100+ ms, then a burst of corrections) because bot heroes searched sixteen
/// approach angles per 0.2 s scan, all ten at the same instant, and repeated failed searches every scan.
/// </summary>
public static class PlanningBudgetChecks
{
    // Batch 61: the hero scan is budgeted on the iterations of the client planner (MovementSystem.ClientPlannerWork against
    // HeroAttackOrderSystem.ScanWorkBudget) and a hero plans through ClientPathPlanner, not NavigationGrid.FindPath, so the old fixture's
    // path override and PlanningWork meter no longer reach it. The three heroes now start far from the target, where one search costs
    // more than the whole step budget (checked below), and a plan is observed as the replacement of the hero's waypoint list.
    // 19 October 2026 (first run): (11500, 11500) costs 2125 iterations against the budget of 2400, so the third hero starts on the
    // bottom-right lane corner, (12000, 3000), where the search costs 2988 (measured).
    [Check("movement.b20-hero-scan-spreads-heroes-over-steps")]
    public static void HeroScanSpreadsHeroesOverSteps()
    {
        var runtime = StartSolo();
        var first = runtime.Session.Participants.Single().Hero!;
        first.OwnerNetId = 0;
        var heroes = new List<Entity> { first };
        foreach (var (id, x, z) in new (uint, double, double)[] { (0x4F20_0B11, 2500.0, 2500.0), (0x4F20_0B12, 12000.0, 3000.0) })
            heroes.Add(runtime.World.Register(new Entity(id, EntityKind.Hero, UnitType.Hero, Team.Order,
                new Position2(x, z))
            {
                Name = "Annie", ChampionName = "Annie", Created = true, Visible = true,
                Radius = 35.0, Health = 1000.0, MaxHealth = 1000.0, Speed = 325.0,
                HeroAutoAcquire = false,
            }));
        first.Position = new Position2(7000.0, 2000.0);
        // A moving enemy is re-planned at every scan (a static target keeps its live route).
        var target = AddTarget(runtime.World, 0x4F20_0B20, new Position2(7000, 8600.0));
        target.Waypoints.Add(new Position2(7100.0, 8800.0));
        // Batch 163: Hero.luaobj's OnTargetLost (proto 0.3 pc 0009-0020) walks a hard pursuit to
        // the target's last known location the moment sight leaves it — that is the chunk's own
        // decision, taken for all three heroes in the one unbudgeted step, and not the native chase
        // this check measures. So the Order team keeps sight of the target for the run (a scout at
        // the enemy); what spreads over the steps is then the budgeted re-plan of a target the
        // heroes can still see.
        runtime.World.Register(new Entity(0x4F20_0B21, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(7000, 8600.0))
        {
            Name = "SightScout", Created = true, Visible = true, Radius = 0.0,
            Health = 1000.0, MaxHealth = 1000.0, VisibilitySize = 2500.0,
        });
        var orders = runtime.Spells.HeroOrders!;
        foreach (var hero in heroes)
        {
            hero.AttackRange = 100.0;
            var before = runtime.Movement.ClientPlannerWork;
            runtime.Movement.PlanChase(hero, target, _ => true);
            TestAssert.True(runtime.Movement.ClientPlannerWork - before >= HeroAttackOrderSystem.ScanWorkBudget,
                $"one search of hero {hero.NetId:X} costs more than the whole scan step budget");
            // Batch 163: the engage is Hero.luaobj's now (OnOrder's ORDER_ATTACKTO takes AI_HARDATTACK
            // and closes on the target) rather than a hand-written AttackTarget/HeroPursuit pair. The
            // chunk's own per-unit TimerDistanceScan/TimerCheckAttack leave a hero in AI_HARDATTACK
            // alone — TimerDistanceScan only re-decides from AI_STANDING/AI_IDLE/AI_MOVE/
            // AI_ATTACKMOVESTATE/AI_ATTACK_GOING_TO_LAST_KNOWN_LOCATION (proto 0.13 pc 0009-0096) —
            // so the budgeted pass below still spreads the native chase and nothing else.
            orders.StartAttack(hero, target);
        }

        var routes = heroes.ToDictionary(hero => hero.NetId, hero => hero.Waypoints);
        int Planned() => heroes.Count(hero => !ReferenceEquals(routes[hero.NetId], hero.Waypoints));
        orders.Tick(0.2);
        TestAssert.Equal(1, Planned(), "the first scan step plans one hero, not all three");
        orders.Tick(1.0 / 30.0);
        TestAssert.Equal(2, Planned(), "the next step continues with the second hero");
        orders.Tick(1.0 / 30.0);
        TestAssert.Equal(3, Planned(), "and the third");
    }

    private static MatchRuntime StartSolo()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static Entity AddTarget(WorldState world, uint netId, Position2 position)
    {
        var target = new Entity(netId, EntityKind.Minion, UnitType.Minion, Team.Chaos, position)
        {
            Alive = true, Created = true, Visible = true, Radius = 0.0, Health = 1000.0, MaxHealth = 1000.0,
        };
        world.Register(target);
        return target;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));
}
