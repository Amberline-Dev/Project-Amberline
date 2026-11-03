using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

public static class MovementPolishChecks
{
    [Check("movement.polish-order-is-not-a-teleport")]
    public static void OrdersPreservePosition()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var minion = runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0, 0);
        foreach (var entity in new[] { hero, minion })
        {
            entity.Position = new Position2(7000.37, 7000.61);
            var before = entity.Position;
            var route = runtime.Movement.SetMoveOrder(entity.NetId, new Position2(7600, 7000));
            TestAssert.True(route.Count > 1);
            // The stock client seats its actor on the first waypoint's two-unit lattice point (its server-path follower, for every unit),
            // so an order seats the server's unit on that same point. Restated in batch 254: a minion is
            // seated too (batch 248 seated heroes only); nothing else is moved by an order.
            var expected = runtime.Movement.ClientAnchor(before);
            TestAssert.True(Math.Abs(expected.X - before.X) < 2.0 && Math.Abs(expected.Z - before.Z) < 2.0,
                "the seat is the unit's own lattice cell, never farther than one two-unit step per axis");
            TestAssert.Equal(expected, entity.Position, "assigning a route moves a unit only onto its wire anchor");
            runtime.Minions!.Tick(.01);
            TestAssert.Equal(expected, entity.Position, "AI packet anchoring cannot move an actor without a movement tick");
            entity.Position = new Position2(5000, 5000);
        }
    }

    [Check("movement.polish-rotated-subcell-gaps")]
    public static void RotatedGaps()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var angle in new[] { 0.0, .25, .5, .75 })
        foreach (var offset in new[] { 0.0, 13.0, 27.0 })
        {
            Position2 Point(double x, double z) => new(7100 + offset + x * Math.Cos(angle) - z * Math.Sin(angle),
                7000 + x * Math.Sin(angle) + z * Math.Cos(angle));
            var obstacles = new[] { new NavigationObstacle(Point(0, 70), 96), new NavigationObstacle(Point(0, -140), 96) };
            var route = grid.FindPathAroundUnits(Point(-300, 0), Point(300, 0), obstacles, preservePhysicalGaps: true);
            TestAssert.True(route.Count > 1, $"gap lost at angle {angle}, grid offset {offset}");
            TestAssert.True(Length(route) < 750, $"unnecessary gap detour: {Length(route)}");
            for (var index = 1; index < route.Count; index++)
                TestAssert.True(grid.SegmentClearOfUnits(route[index - 1], route[index], obstacles), "wire segment crosses a body");
        }
    }

    [Check("minions.polish-initial-wave-facing")]
    public static void InitialWaveFacingFollowsLaneRoute()
    {
        var runtime = Start();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var minion = runtime.Waves!.SpawnFixture(team, MinionType.Melee,
                team == Team.Order ? 0 : 1, "C", 0.0, 0.0);
            var route = runtime.Waves.LaneRoute(team, "C");
            TestAssert.True(route.Count > 1, $"{team}: lane route has an initial leg");
            var delta = route[1] - route[0];
            var length = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            TestAssert.Equal(new Position2(delta.X / length, delta.Z / length),
                minion.Facing, $"{team}: wave spawn facing follows its first lane leg");
        }
    }

    [Check("movement.polish-melee-wave-progress")]
    public static void MeleeApproach()
    {
        foreach (var champion in ChampionCatalog.PlayableNames)
            Approach(champion);
    }

    private static void Approach(string champion)
    {
        var runtime = Start(champion);
        var hero = runtime.Session.Participants.Single().Hero!;
        var enemy = Body(runtime, Team.Chaos, new Position2(7500, 7000));
        enemy.Health = enemy.MaxHealth = 10000;
        foreach (var z in new[] { 6830.0, 7070.0, 7270.0 }) Body(runtime, Team.Order, new Position2(7250, z));
        runtime.Spells.HeroOrders!.StartAttack(hero, enemy);
        var traveled = 0.0;
        var stationary = 0;
        for (var tick = 0; tick < 100 && enemy.Health == 10000; tick++)
        {
            var old = hero.Position;
            runtime.Tick(.1);
            var step = Distance(old, hero.Position);
            traveled += step;
            stationary = step < .5 ? stationary + 1 : 0;
            TestAssert.True(stationary < 15, "reachable target stalled for 1.5 seconds");
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0, $"{champion} lost a visible reachable attack target at tick {tick}");
        }
        TestAssert.True(enemy.Health < 10000, "single right-click must approach and hit");
        TestAssert.True(traveled < 1000, $"excessive route/oscillation distance: {traveled}");
    }

    [Check("minions.polish-proximity-target-stability")]
    public static void NearbyChampionDoesNotCauseRepeatedTargetLoss()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Health = hero.MaxHealth = 100000;
        var minion = runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0, 0);
        minion.Position = new Position2(7280, 7000);
        minion.Waypoints.Clear(); minion.Target = null; minion.AttackTarget = null;
        minion.MinionState = MinionAiState.March;
        runtime.Tick(.3);
        TestAssert.Equal(hero.NetId, minion.AttackTarget ?? 0, "proximity acquires the champion without a call for help");
        for (var tick = 0; tick < 160; tick++)
        {
            runtime.Tick(.1);
            TestAssert.Equal(hero.NetId, minion.AttackTarget ?? 0, "an eligible nearby champion must not flicker in and out of aggro");
        }
        TestAssert.True(hero.Health < hero.MaxHealth, "retained aggro must deliver attacks");
    }

    [Check("minions.polish-windup-survives-overlap")]
    public static void CommittedMinionAttack()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var minion = runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0, 0);
        var other = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 1, "C", 0, 0);
        foreach (var unit in new[] { minion, other })
        {
            unit.Position = new Position2(7130.37, 7000.61);
            unit.Waypoints.Clear(); unit.Target = null;
            unit.AttackTarget = hero.NetId; unit.MinionState = MinionAiState.Engage;
            unit.AttackCooldown = 100;
        }
        minion.AttackCooldown = 0; minion.AttackWindup = .8; minion.AttackPeriod = 2;
        TestAssert.True(runtime.Combat.QueueBasicAttack(minion, hero));
        for (var tick = 0; tick < 7; tick++)
        {
            runtime.Minions!.Tick(.1);
            TestAssert.True(runtime.Combat.HasCommittedWindup(minion, hero), "overlap maintenance canceled an attack");
            runtime.Combat.CombatTick(.1, regenerate: false);
            TestAssert.Equal(hero.NetId, minion.AttackTarget!.Value);
        }
        var hp = hero.Health;
        runtime.Combat.CombatTick(.2, regenerate: false);
        var crossing = runtime.Combat.PendingHits.Single(hit =>
            hit.SourceNetId == minion.NetId && hit.TargetNetId == hero.NetId);
        TestAssert.True(crossing.WindupRemaining <= 0.0,
            "the crossing tick must retain a released, nonpositive windup");
        TestAssert.Equal(hp, hero.Health,
            "the crossing tick must not resolve the released melee hit");
        runtime.Combat.CombatTick(.1, regenerate: false); // The native crossing tick completes windup before damage.
        TestAssert.True(hero.Health < hp, "the retained windup must deliver its damage");
        TestAssert.False(runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == minion.NetId && hit.TargetNetId == hero.NetId),
            "the released melee hit must leave the pending queue after impact");

        // Combat runs after minion AI in production. Advance the AI by one
        // target-scan interval so the first post-windup decision can issue the
        // real production separation route for the still-overlapping pair.
        var radii = runtime.Movement.PairMovementRadius(minion, other) +
                    runtime.Movement.PairMovementRadius(other, minion);
        runtime.Minions!.Tick(.25);
        TestAssert.True(minion.Waypoints.Count > 1 || other.Waypoints.Count > 1,
            "the next eligible minion decision must issue a separating route");
        for (var step = 0; step < 20 && Distance(minion.Position, other.Position) < 0.5 * radii; step++)
            runtime.Movement.Tick(MovementSystem.FixedTickSeconds);
        TestAssert.True(Distance(minion.Position, other.Position) >= 0.5 * radii,
            "the post-windup production route must recover the physical gap");
    }

    [Check("combat.polish-stationary-tower-attacks")]
    public static void SustainedTowerAttack()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var tower = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret), EntityKind.Turret,
            UnitType.Turret, Team.Chaos, new Position2(7200, 7000))
            { Name = "test-tower", Created = true, Health = 10000, MaxHealth = 10000, Radius = 88 });
        hero.AttackPeriod = 1; hero.AttackWindup = .3;
        runtime.Spells.HeroOrders!.StartAttack(hero, tower);
        var hits = 0;
        var health = tower.Health;
        for (var tick = 0; tick < 60; tick++)
        {
            runtime.Tick(.1);
            TestAssert.Equal(tower.NetId, hero.AttackTarget!.Value);
            if (tower.Health < health) hits++;
            health = tower.Health;
        }
        TestAssert.True(hits >= 5, $"stationary tower attacks interrupted: {hits} hits in six seconds");
    }

    [Check("neutral.polish-initial-facing")]
    public static void NeutralFacing()
    {
        var runtime = Start();
        var scheduler = new JungleScheduler(runtime.World, GameData.Default, navigation: runtime.Navigation);
        scheduler.Start(); scheduler.Tick(1000);
        foreach (var member in scheduler.Members.Values)
        {
            var expected = VisionPackets.FacingTowards(member.Entity.Position, new Position2(member.FacePosition.X, member.FacePosition.Z));
            TestAssert.Equal(expected, member.Entity.Facing, $"camp {member.CampId} member {member.Member} initial facing");
        }
    }

    private static MatchRuntime Start(string champion = "Yorick")
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: champion, solo: true, originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0); runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null);
        runtime.Session.Spawn(1); runtime.Session.MarkReady(1); runtime.Session.Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(7000, 7000); hero.HeroAutoAcquire = false;
        hero.Waypoints.Clear(); hero.Target = null;
        return runtime;
    }

    private static Entity Body(MatchRuntime runtime, Team team, Position2 point) => runtime.World.Register(
        new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, team, point)
        { Name = "Minion_Basic", Created = true, Visible = true, Health = 5000, MaxHealth = 5000, Radius = 35.744, Speed = 325 });
    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
    private static double Length(IReadOnlyList<Position2> path) => path.Zip(path.Skip(1), Distance).Sum();
}
