using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 288: the client's actor update probes the path end's cell of a flagged actor within radius + 75 of it with
/// the client's stuck-actor test (the path end as the reference point) and, when the cell is held, calls the client's
/// <c>ForceStopServer</c> hook, which does nothing there; the server stops the unit and sends the stop.
/// </summary>
public static class B288ForceStopServerChecks
{
    [Check("movement.force-stop-server-on-held-goal")]
    public static void ForceStopServerOnHeldGoal()
    {
        // A hero sent onto a minion that stands on its goal presses it and never gives up (the client's press loop); the minion on the goal point does
        // not hold the goal's cell (the probe's reference point is the goal), so the walk goes on, as in the client.
        {
            var (runtime, hero, goal, _) = HeroFixture(withHolder: false);
            var oneStops = 0;
            runtime.Movement.HeroPathChanged += (_, path) => oneStops += path.Count == 1 ? 1 : 0;
            for (var tick = 0; tick < 30; tick++) runtime.Movement.Tick(.1);
            TestAssert.True(hero.Waypoints.Count > 0 && oneStops == 0,
                $"a goal held only by the unit on its point stops nothing (waypoints {hero.Waypoints.Count}, stops {oneStops})");
            TestAssert.True(Distance(hero.Position, goal) < runtime.Movement.MovementRadius(hero) + 75,
                "fixture: the hero presses within radius + 75 of its goal");
        }

        // A second minion beside the goal's cell centre, beyond the probe's reach of the goal point, holds the cell: the server stops the
        // pressing hero where it stands and publishes the one-point path.
        {
            var (runtime, hero, goal, holder) = HeroFixture(withHolder: true);
            IReadOnlyList<Position2>? stop = null;
            runtime.Movement.HeroPathChanged += (_, path) => { if (path.Count == 1) stop ??= path; };
            var tick = 0;
            for (; tick < 30 && stop is null; tick++) runtime.Movement.Tick(.1);
            TestAssert.True(stop is not null, "the hero pressing a held goal is stopped and the stop is published (ForceStopServer)");
            TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null && hero.FrameVelocity == Position2.Zero,
                "ForceStopServer leaves the hero stopped with no route");
            TestAssert.True(Distance(hero.Position, stop![0]) <= 1e-6 && stop[0] == runtime.Movement.ClientAnchor(stop[0]),
                $"the hero stands on the stop's anchor ({hero.Position} vs {stop[0]})");
            TestAssert.True(runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("force-stop-server") == 1,
                "the stop is counted once as an automatic hero route");
            var stoppedAt = hero.Position;
            for (var more = 0; more < 10; more++) runtime.Movement.Tick(.1);
            TestAssert.True(hero.Position == stoppedAt, "the stopped hero stays where it was stopped");
            TestAssert.True(Distance(holder.Position, goal) > 0, "fixture: the holder stands off the goal point");
        }

        // A lane minion sent onto a planted minion, with a second one holding the goal's cell: it is stopped where it presses, and its
        // stop goes out on the wire as the one-point path every observer receives.
        {
            var runtime = Start();
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.Position = new Position2(6000, 7000);
            var (centre, half) = Cell(runtime, new Position2(7500, 7000));
            var goal = runtime.Movement.ClientAnchor(new Position2(centre.X - 0.8 * half, centre.Z));
            var walker = Body(runtime, new Position2(goal.X - 400, goal.Z));
            Body(runtime, goal);
            var holder = Holder(runtime, walker, centre, half, goal);
            var capture = new Capture();
            runtime.Delivery.AddObserver(new ReplicationObserver(99, hero.Team, hero.NetId, capture, ready: true));
            runtime.Delivery.Flush(); capture.Packets.Clear();
            runtime.Movement.InstallPath(walker, [walker.Position, goal]);
            for (var tick = 0; tick < 40 && walker.Waypoints.Count > 0; tick++) runtime.Movement.Tick(.1);
            runtime.Delivery.Flush();
            var stops = capture.Packets.Where(packet => packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == walker.NetId).ToArray();
            TestAssert.True(stops.Length == 1 && stops[0].Plaintext.Length == 17,
                $"the minion's ForceStopServer stop is published once as a one-point path ({stops.Length} packets; waypoints " +
                $"{walker.Waypoints.Count}, {Distance(walker.Position, goal):F1} from the goal)");
            var x = BinaryPrimitives.ReadSingleLittleEndian(stops[0].Plaintext.Span[9..]);
            var z = BinaryPrimitives.ReadSingleLittleEndian(stops[0].Plaintext.Span[13..]);
            TestAssert.True(walker.Waypoints.Count == 0 && Distance(walker.Position, new Position2(x, z)) <= 1e-3,
                $"the minion stands on the published stop ({walker.Position} vs ({x}, {z}))");
            TestAssert.True(Distance(walker.Position, goal) > 1, "fixture: the minion was stopped short of its goal, against the holder");
            TestAssert.True(Distance(holder.Position, goal) > 0, "fixture: the holder stands off the goal point");
        }
    }

    private static (MatchRuntime Runtime, Entity Hero, Position2 Goal, Entity Holder) HeroFixture(bool withHolder)
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var (centre, half) = Cell(runtime, new Position2(7500, 7000));
        var goal = runtime.Movement.ClientAnchor(new Position2(centre.X - 0.8 * half, centre.Z));
        hero.Position = new Position2(goal.X - 400, goal.Z);
        var occupant = Body(runtime, goal);
        var holder = withHolder ? Holder(runtime, hero, centre, half, goal) : occupant;
        // The route the server would have published before the minion stepped onto the point.
        runtime.Movement.InstallPath(hero, [hero.Position, goal]);
        // Fixture: the hero presses the occupant short of the probe's own-body reach, so only a holder can stop it.
        var contact = runtime.Movement.MoverPairRadius(hero, occupant) + runtime.Movement.PairMovementRadius(occupant, hero);
        var rs = ProbeShrink(runtime, hero, half);
        TestAssert.True(contact < rs + 0.95 * PathRadius(runtime, hero), $"fixture: contact distance {contact:F1}");
        return (runtime, hero, goal, holder);
    }

    // A minion beside the goal cell's centre, on the side away from the goal, inside the probe's disc around the centre and outside
    // its reach of the goal point (the client's reach test with the stationary disc, k = 1 for a hero and 2 for a minion).
    private static Entity Holder(MatchRuntime runtime, Entity mover, Position2 centre, double half, Position2 goal)
    {
        var holder = Body(runtime, centre);
        var rs = ProbeShrink(runtime, mover, half);
        var rb = PathRadius(runtime, holder);
        holder.Position = new Position2(centre.X + 0.9 * (rb + rs), centre.Z);
        TestAssert.True(Distance(holder.Position, goal) > rs + 0.95 * rb,
            "fixture: the holder is outside the probe's reach of the goal point");
        return holder;
    }

    private static double ProbeShrink(MatchRuntime runtime, Entity mover, double half) =>
        Math.Max(PathRadius(runtime, mover) - 1.8 * half, 15.0);

    private static double PathRadius(MatchRuntime runtime, Entity unit) =>
        Math.Clamp(runtime.Movement.MovementRadius(unit), 0.0, 250.0);

    private static (Position2 Centre, double Half) Cell(MatchRuntime runtime, Position2 point)
    {
        var nav = runtime.Navigation;
        var centre = nav.CellToWorld(nav.WorldToCell(point));
        TestAssert.True(nav.IsMovementWalkable(centre), "fixture: the goal cell is walkable");
        return (centre, nav.CellSize / 2.0);
    }

    private static MatchRuntime Start()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Yorick", solo: true, originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0); runtime.Session.CreateWorld();
        runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1));
        runtime.Session.Spawn(1); runtime.Session.MarkReady(1); runtime.Session.Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        hero.Waypoints.Clear(); hero.Target = null;
        return runtime;
    }

    private static Entity Body(MatchRuntime runtime, Position2 point) => runtime.World.Register(
        new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Order, point)
        { Name = "Minion_Basic", Created = true, Visible = true, Health = 5000, MaxHealth = 5000, Radius = 35.744, Speed = 325 });

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
