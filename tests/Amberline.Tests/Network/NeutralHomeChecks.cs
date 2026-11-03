using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>Production-graph checks for complete Map1 neutral leash returns.</summary>
public static class NeutralHomeChecks
{
    private const double FrameDt = 0.1;
    private const double HomeEpsilon = 1e-6;
    private static readonly int[] MobileCamps = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    [Check("neutral.production-home-return")]
    public static void MobileCampsReturnEveryMemberToItsResolvedHome()
    {
        using var runtime = StartRuntime(180.0);
        var decisions = new List<MovementDecisionObservation>();
        runtime.Movement.DecisionObserver = decisions.Add;

        foreach (var pull in new[] { new Position2(1.0, 0.0), new Position2(0.0, 1.0) })
        foreach (var campId in MobileCamps)
        {
            var members = runtime.World.Entities.Values
                .Where(entity => entity.Kind == EntityKind.Neutral &&
                    entity.CampId == campId && entity.Alive)
                .OrderBy(entity => entity.NeutralMember).ToArray();
            TestAssert.Equal(NeutralDefinitions.Camps[campId].Roster.Count, members.Length,
                $"camp {campId} has every ordinary member");
            if (campId == 3)
                TestAssert.True(members.Any(member => member.Name == "LesserWraith"),
                    "the wraith return includes a small wraith member");

            var homes = members.ToDictionary(member => member.NetId,
                member => member.NeutralHome!.Value);
            var starts = PullPositions(runtime.Navigation, members, pull);
            foreach (var index in Enumerable.Range(0, members.Length))
                PrepareReturn(members[index], starts[index]);

            decisions.Clear();
            DriveReturn(runtime, members, homes, decisions, campId, pull);
        }
    }

    [Check("neutral.production-home-waits-while-disabled")]
    public static void ReturnWaitsWhileDisabledAndBaronAtHomeCompletes()
    {
        using var runtime = StartRuntime(900.0);
        // Batch 51 (7077fb3): TimerRetreat is a free-running 0.5 s timer and the arrival check falls on its edge, so the
        // single steps below are one full period (it always contains exactly one edge).
        const double RetreatPeriod = NeutralDefinitions.RetreatCheckSeconds;
        var mobile = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Neutral && entity.CampId == 1 &&
            entity.Name == "AncientGolem" && entity.Alive);
        var baron = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Neutral && entity.CampId == 12 &&
            entity.Name == "Worm" && entity.Alive);
        var mobileHome = mobile.NeutralHome!.Value;
        var mobileStart = PullPositions(runtime.Navigation, [mobile],
            new Position2(1.0, 0.0))[0];
        var speed = mobile.Speed;
        TestAssert.True(speed > 0.0, "the temporary return blocker uses a mobile neutral");
        PrepareReturn(mobile, mobileStart);

        mobile.Speed = 0.0;
        runtime.Tick(RetreatPeriod);
        TestAssert.Equal("retreat", mobile.NeutralState,
            "a speed-zero ordinary neutral outside home remains in retreat");
        TestAssert.Equal(mobileStart, mobile.Position,
            "a speed-zero ordinary neutral does not receive an authoritative snap");
        TestAssert.Equal(0, mobile.Waypoints.Count,
            "a speed-zero ordinary neutral has no false arrival route");

        mobile.Speed = speed;
        var stun = runtime.Buffs.AddBuff(mobile, "TemporaryReturnStun", BuffType.Stun,
            2.0, mobile.NetId);
        runtime.Tick(RetreatPeriod);
        TestAssert.Equal("retreat", mobile.NeutralState,
            "a temporarily stunned ordinary neutral outside home remains in retreat");
        TestAssert.Equal(mobileStart, mobile.Position,
            "a temporary stun does not move the ordinary neutral");
        if (stun is not null) runtime.Buffs.RemoveBuff(mobile, stun.Slot, emitWire: false);

        var decisions = new List<MovementDecisionObservation>();
        runtime.Movement.DecisionObserver = decisions.Add;
        DriveReturn(runtime, [mobile], new Dictionary<uint, Position3>
        {
            [mobile.NetId] = mobileHome,
        }, decisions, 1, new Position2(1.0, 0.0));

        var home = baron.NeutralHome!.Value;
        var homePoint = new Position2(home.X, home.Z);
        PrepareReturn(baron, homePoint);
        baron.Speed = 0.0;
        runtime.Tick(RetreatPeriod);
        TestAssert.Equal("idle", baron.NeutralState,
            "a stationary Baron already at its resolved home completes normally");
        TestAssert.Equal(homePoint, baron.Position,
            "home completion does not move a stationary Baron");

        var clientHome = runtime.Movement.ClientAnchor(homePoint);
        var tinyOffset = new Position2(clientHome.X - 0.25, clientHome.Z + 0.25);
        TestAssert.Equal(clientHome, runtime.Movement.ClientAnchor(tinyOffset),
            "the subcell fixture uses the same signed packet-grid cell as Baron home");
        PrepareReturn(baron, tinyOffset);
        runtime.Tick(RetreatPeriod);
        TestAssert.Equal("idle", baron.NeutralState,
            "a stopped neutral in the same client cell completes its return");
        TestAssert.Equal(tinyOffset, baron.Position,
            "same-client-cell completion preserves the subcell position");
    }

    private static MatchRuntime StartRuntime(double combatTime)
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null,
                $"participant {participant.PlayerId} authenticates");
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection),
                $"participant {participant.PlayerId} becomes ready");
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "production neutral-home match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        runtime.Combat.RestoreClock(combatTime);
        runtime.Tick(FrameDt);
        return runtime;
    }

    private static void PrepareReturn(Entity member, Position2 position)
    {
        member.Position = position;
        member.NeutralState = "retreat";
        member.NeutralLeashCounter = 0;
        member.NeutralRetreatElapsed = 0.0;
        member.NeutralRegenElapsed = 0.0;
        member.NeutralLastMove = null;
        member.AttackTarget = null;
        member.Target = null;
        member.Waypoints.Clear();
        member.ForcedMove = null;
        member.Health = member.MaxHealth;
    }

    private static void DriveReturn(MatchRuntime runtime, IReadOnlyList<Entity> members,
        IReadOnlyDictionary<uint, Position3> homes,
        IReadOnlyList<MovementDecisionObservation> decisions, int campId, Position2 pull)
    {
        for (var frame = 0; frame < 600 && members.Any(member => member.NeutralState != "idle"); frame++)
        {
            var previous = members.ToDictionary(member => member.NetId, member => member.Position);
            runtime.Tick(FrameDt);
            foreach (var member in members)
            {
                var distance = Distance(previous[member.NetId], member.Position);
                var stepBound = Math.Max(0.0, member.Speed) * FrameDt *
                    Math.Pow(Math.Sqrt(1.0 + runtime.Movement.Settings.Collision.SpeedDeadband), 3) + 3.0;
                TestAssert.True(distance <= stepBound,
                    $"camp {campId} pull {pull} member {member.NeutralMember} stays within the existing collision-step bound");
                TestAssert.True(member.ForcedMove is null,
                    $"camp {campId} pull {pull} member {member.NeutralMember} has no forced return snap");
            }
        }

        foreach (var member in members)
        {
            TestAssert.Equal("idle", member.NeutralState,
                $"camp {campId} pull {pull} member {member.NeutralMember} completes return");
            TestAssert.Equal(0, member.Waypoints.Count,
                $"camp {campId} pull {pull} member {member.NeutralMember} has one final stop");
            var home = homes[member.NetId];
            var raw = new Position2(home.X, home.Z);
            TestAssert.Equal(home, member.NeutralHome!.Value,
                $"camp {campId} pull {pull} member {member.NeutralMember} keeps saved home");
            var own = decisions.Where(decision => decision.NetId == member.NetId &&
                decision.Kind == "neutral-planner").ToArray();
            // Batch 248 (restated): the return is planned by the engine planner (the client's path builder), whose end-cell snap
            // moves an end whose cell a standing sibling covers to the nearest cell left free, so a member
            // lands on its route's end, at its home or a cell beside it, not always on the home point (the old NPC planner's).
            var end = own.Last(decision => decision.Reason == "neutral-retreat" && decision.Phase == "accepted").Route![^1];
            TestAssert.True(Math.Min(Distance(member.Position, end), Distance(member.Position, runtime.Movement.ClientAnchor(end))) <= HomeEpsilon,
                $"camp {campId} pull {pull} member {member.NeutralMember} lands on its route's end: {member.Position} against {end}");
            TestAssert.True(Distance(end, raw) <= 2.0 * runtime.Navigation.CellSize,
                $"camp {campId} pull {pull} member {member.NeutralMember} returns to its home or a cell beside it: {end} against {raw}");
            TestAssert.Equal(1, own.Count(decision => decision.Reason == "neutral-retreat" &&
                decision.Phase == "accepted"),
                $"camp {campId} pull {pull} member {member.NeutralMember} accepts one return route");
            // script = retail: Leashed' OnStoppedMoving is state-only (SetLeashCounter 0, SetState
            // AI_ATTACK, SetRoamState of the original roam), so the arrival at home is the route
            // being consumed — the old machine's extra "neutral-home" stop order is gone.
            TestAssert.Equal(0, own.Count(decision => decision.Reason == "neutral-home" &&
                decision.Phase == "stopped"),
                $"camp {campId} pull {pull} member {member.NeutralMember} emits no home stop order");
        }
    }

    private static Position2[] PullPositions(NavigationGrid grid,
        IReadOnlyList<Entity> members, Position2 heading)
    {
        var angle = Math.Atan2(heading.Z, heading.X);
        foreach (var distance in new[] { 180.0, 220.0, 260.0, 300.0, 360.0 })
        for (var sample = 0; sample < 16; sample++)
        {
            var direction = angle + sample * Math.PI / 8.0;
            var offset = new Position2(distance * Math.Cos(direction),
                distance * Math.Sin(direction));
            var starts = members.Select(member =>
            {
                var home = member.NeutralHome!.Value;
                return new Position2(home.X + offset.X, home.Z + offset.Z);
            }).ToArray();
            if (starts.Select((start, index) => (start, index)).Any(item =>
                    !grid.IsMovementWalkable(item.start) ||
                    grid.FindPath(item.start, new Position2(
                        members[item.index].NeutralHome!.Value.X,
                        members[item.index].NeutralHome!.Value.Z)).Count < 2)) continue;
            if (Enumerable.Range(0, starts.Length).Any(index => Enumerable.Range(index + 1,
                    starts.Length - index - 1).Any(other =>
                Distance(starts[index], starts[other]) <
                    members[index].Radius + members[other].Radius + 20.0))) continue;
            return starts;
        }
        throw new InvalidOperationException("no walkable neutral pull offset was found");
    }

    private static double Distance(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
