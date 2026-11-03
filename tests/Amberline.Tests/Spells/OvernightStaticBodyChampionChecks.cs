using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-leesin-kick-tower-safe-endpoint")]
    public static void DragonRageKeepsItsKickEndpointOutsideTower()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var target = match.Hero(1);
            lee.Position = new Position2(7000, 7000);
            target.Position = new Position2(7200, 7000);
            var tower = AddTower(match, team, new Position2(8000, 7000));
            tower.Alive = false;
            tower.Position = runtime.Movement.FurthestWalkableLanding(target.Position,
                new Position2(8000, 7000), target);
            tower.Alive = true;
            runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 3, target);
            for (var tick = 0; tick < 40 && target.ForcedMove is null; tick++) runtime.Tick(.01);
            var move = target.ForcedMove ?? throw new InvalidOperationException("Dragon's Rage did not kick");
            TestAssert.True(StaticDistance(move.Destination, tower.Position) + 1e-6 >=
                runtime.Movement.BodyClearance(target, tower) + 4,
                "Lee R's non-colliding forced move ends outside the tower body");
            TestAssert.Equal(move.Destination, StaticBodyDashEnd(match.Captures[0].Packets),
                "Lee R announces the same tower-safe authoritative endpoint");
            for (var tick = 0; tick < 100 && !move.Completed; tick++) runtime.Tick(.05);
            TestAssert.True(move.Completed && move.Succeeded,
                "the clear far-side kick reaches its safe landing");
            TestAssert.Equal(move.Destination, target.Position,
                "the completed Lee R kick does not re-enter the tower");
        }
    }

    [Check("spells-live-vayne-tumble-tower-collision-wire-landing")]
    public static void TumbleAnnouncesItsTowerLimitedLanding()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cadence in new[] { .05, .1 })
        foreach (var blocked in new[] { false, true })
        {
            using var match = new Match("Vayne", team);
            var runtime = match.Runtime;
            var vayne = match.Hero(0);
            vayne.Position = new Position2(7000, 7000);
            match.Hero(1).Position = new Position2(8200, 7000);
            var tower = AddTower(match, team, new Position2(7250, 7000));
            tower.Alive = blocked;
            runtime.Delivery.Flush(); match.Clear();
            var rawEnd = new Position2(7300, 7000);
            var projected = runtime.Movement.FirstCollisionDashLanding(
                vayne.Position, rawEnd, 900, 300.0 / 900.0 + .05, vayne);
            match.CastGround(0, rawEnd);
            for (var tick = 0; tick < 30 && vayne.ForcedMove is null; tick++) runtime.Tick(.01);
            var move = vayne.ForcedMove ?? throw new InvalidOperationException("Tumble did not begin");
            TestAssert.Equal(rawEnd, move.Destination,
                "Tumble retains its original collision request for the server");
            TestAssert.Equal(runtime.Movement.ClientAnchor(projected),
                StaticBodyDashEnd(match.Captures[0].Packets),
                "0x67 announces the quantized tower-limited Tumble endpoint");
            for (var tick = 0; tick < 20 && !move.Completed; tick++) runtime.Tick(cadence);
            TestAssert.True(move.Completed, "Tumble completes");
            TestAssert.Equal(!blocked, move.Succeeded,
                "clear Tumble completes while tower collision stops it");
            if (blocked)
                TestAssert.True(StaticDistance(vayne.Position, tower.Position) + 1e-6 >=
                    runtime.Movement.BodyClearance(vayne, tower) + 4,
                    "Tumble never embeds Vayne in the tower body");
            else
            {
                TestAssert.Equal(rawEnd, projected, "clear Tumble keeps its raw dash endpoint");
                // Batch 279: the dash's stand is a one-point path, installed on receipt: seated on the wire endpoint, its anchor.
                TestAssert.Equal(runtime.Movement.ClientAnchor(rawEnd), vayne.Position,
                    "clear dash wire endpoint agrees with authoritative full travel");
            }
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cadence in new[] { .05, .1 })
        {
            using var match = new Match("Vayne", team);
            var runtime = match.Runtime;
            var vayne = match.Hero(0);
            vayne.Position = new Position2(7700, 7000);
            match.Hero(1).Position = new Position2(9000, 7000);
            TestAssert.True(runtime.Navigation.IsWalkable(new Position2(7900, 7000)) &&
                !runtime.Navigation.IsWalkable(new Position2(7925, 7000)),
                "shipped Map1 wall bounds the Tumble ray");
            runtime.Delivery.Flush(); match.Clear();
            match.CastGround(0, new Position2(8000, 7000));
            var move = vayne.ForcedMove ?? throw new InvalidOperationException("wall Tumble did not begin");
            var wire = StaticBodyDashEnd(match.Captures[0].Packets);
            TestAssert.Equal(runtime.Movement.ClientAnchor(new Position2(7900, 7000)), wire,
                "wall Tumble previews the runtime raw-terrain collision");
            for (var tick = 0; tick < 20 && !move.Completed; tick++) runtime.Tick(cadence);
            TestAssert.True(move.Completed && !move.Succeeded, "wall Tumble retains generic collision");
            TestAssert.True(StaticDistance(wire, vayne.Position) <= runtime.Navigation.CellSize / 2 + 2,
                "wall Tumble wire and tick-sampled landing stay within one collision sample");
        }
    }

    [Check("spells-live-vayne-condemn-tower-collision-wire-landing")]
    public static void CondemnAnnouncesItsTowerLimitedLandingAndRetainsCollide()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cadence in new[] { .05, .1 })
        foreach (var blocked in new[] { false, true })
        {
            using var match = new Match("Vayne", team);
            var runtime = match.Runtime;
            var vayne = match.Hero(0);
            var target = match.Hero(1);
            vayne.Position = new Position2(7000, 7000);
            target.Position = new Position2(7200, 7000);
            var tower = AddTower(match, team, new Position2(7440, 7000));
            tower.Alive = blocked;
            runtime.Delivery.Flush(); match.Clear();
            // Batch 279: Condemn's windup stop seats Vayne on her lattice anchor at once, and the push runs 475 units away
            // from her there.
            var seat = runtime.Movement.ClientAnchor(vayne.Position);
            var away = Math.Sqrt(Math.Pow(target.Position.X - seat.X, 2) + Math.Pow(target.Position.Z - seat.Z, 2));
            var rawEnd = new Position2(target.Position.X + (target.Position.X - seat.X) / away * 475.0,
                target.Position.Z + (target.Position.Z - seat.Z) / away * 475.0);
            var projected = runtime.Movement.FirstCollisionDashLanding(
                target.Position, rawEnd, 2000, 475.0 / 2000.0 + .05, target);
            match.Cast(0, 2, target);
            for (var tick = 0; tick < 100 && target.ForcedMove is null; tick++) runtime.Tick(.01);
            var move = target.ForcedMove ?? throw new InvalidOperationException("Condemn did not push");
            TestAssert.True(Math.Abs(rawEnd.X - move.Destination.X) < 1e-3 && Math.Abs(rawEnd.Z - move.Destination.Z) < 1e-3,
                $"Condemn retains its original request so BBMove Collide can fire ({move.Destination})");
            TestAssert.Equal(runtime.Movement.ClientAnchor(projected),
                StaticBodyDashEnd(match.Captures[0].Packets),
                "0x67 announces the quantized tower-limited Condemn endpoint");
            for (var tick = 0; tick < 20 && !move.Completed; tick++) runtime.Tick(cadence);
            runtime.Tick(.05);
            TestAssert.True(move.Completed, "Condemn completes");
            TestAssert.Equal(!blocked, move.Succeeded,
                "clear Condemn completes while tower collision stops it");
            if (blocked)
                TestAssert.True(StaticDistance(target.Position, tower.Position) + 1e-6 >=
                    runtime.Movement.BodyClearance(target, tower) + 4,
                    "Condemn never embeds its target in the tower body");
            else
            {
                TestAssert.Equal(rawEnd, projected, "clear Condemn keeps its raw dash endpoint");
                // Batch 279: the push's stand is a one-point path, installed on receipt: seated on the endpoint's anchor.
                TestAssert.Equal(runtime.Movement.ClientAnchor(rawEnd), target.Position,
                    "clear dash wire endpoint agrees with authoritative full travel");
            }
            TestAssert.True(StaticDistance(runtime.Movement.ClientAnchor(projected), target.Position) <= 2,
                "tower and clear Condemn wire landings agree with completed movement");
            // Shipped VayneCondemnMissile.luaobj tests generic BBMove Collide.
            // The available native stub does not settle whether a tower body
            // counts as that collision; retain the existing callback policy.
            TestAssert.Equal(blocked,
                target.Buffs.Any(buff => buff.Name == "Stun"),
                "the generic collision callback only runs for an actual hit");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cadence in new[] { .05, .1 })
        {
            using var match = new Match("Vayne", team);
            var runtime = match.Runtime;
            var vayne = match.Hero(0);
            var target = match.Hero(1);
            vayne.Position = new Position2(7300, 7000);
            target.Position = new Position2(7600, 7000);
            TestAssert.True(runtime.Navigation.IsWalkable(new Position2(7900, 7000)) &&
                !runtime.Navigation.IsWalkable(new Position2(7925, 7000)),
                "shipped Map1 wall bounds the archived Condemn ray");
            runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 2, target);
            for (var tick = 0; tick < 100 && target.ForcedMove is null; tick++) runtime.Tick(.01);
            var move = target.ForcedMove ?? throw new InvalidOperationException("wall Condemn did not push");
            var wire = StaticBodyDashEnd(match.Captures[0].Packets);
            // Batch 279: Condemn's windup stop seats Vayne on her lattice anchor (7299, 7001) at once, so the push's line can
            // run a unit off z = 7000 and meet the wall's raw-terrain stop a few units further on ((7907, 6999) against (7899, 7001)).
            TestAssert.True(Math.Abs(wire.Z - 7000) <= 2 && wire.X >= 7890 && wire.X < 7925 && runtime.Navigation.IsWalkable(wire),
                $"{team}/{cadence}: archived Condemn wall uses the runtime raw-terrain stop on 0x67 ({wire})");

            runtime.Tick(.05);
            TestAssert.False(move.Completed, "fog re-entry observes a partially advanced push");
            var observer = runtime.Session.Participants[0].View;
            observer.Visible.Remove(target.NetId);
            match.Clear();
            runtime.Delivery.Flush();
            // Batch 291: since batch 276 the re-entry's dash is the reveal's 0xC2 movement data type 1.
            TestAssert.Equal(wire, AnnouncedDashEnd(match.Captures[0].Packets, target.NetId),
                "Condemn vision reentry retains the projected active dash limit");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1 &&
                packet.EntityReferences.Any(reference => reference.NetId == target.NetId)),
                "Condemn reentry does not resume a raw-destination waypoint walk");

            for (var tick = 0; tick < 20 && !move.Completed; tick++) runtime.Tick(cadence);
            runtime.Tick(.05);
            TestAssert.True(move.Completed && !move.Succeeded,
                "archived wall still invokes Condemn's generic collision result");
            TestAssert.True(StaticDistance(wire, target.Position) <= 2,
                "Condemn wire and server differ by at most client-grid rounding");
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "Stun"),
                "the wall-hit callback still stuns");

            runtime.Tick(2);
            TestAssert.True(target.ForcedMove is null &&
                !target.Buffs.Any(buff => buff.Name == "Stun"),
                "Condemn and its wall stun retire before an ordinary escape order");
            var open = new Position2(7600, 7000);
            var route = runtime.Movement.SetMoveOrder(target.NetId, open);
            TestAssert.True(route.Count > 1, "one post-wall order obtains an escape route");
            var before = StaticDistance(target.Position, open);
            for (var tick = 0; tick < 10; tick++) runtime.Movement.Tick(.05);
            TestAssert.True(StaticDistance(target.Position, open) < before - 1,
                "the post-wall route makes progress without a second order");
        }
    }

    private static Entity AddTower(Match match, Team team, Position2 point) =>
        match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, team, point)
        {
            Alive = true, Speed = 0, Radius = MapDefinitions.TurretRadius,
            Health = 5000, MaxHealth = 5000, AttackRange = 0, AttackDamage = 0,
        });

    private static Position2 StaticBodyDashEnd(
        IEnumerable<Amberline.Network.Protocol.PlaintextPacket> packets)
    {
        var dash = Decode(packets.Single(packet => Opcode(packet) == 0x67 && packet.Channel == 4));
        var points = dash.GetProperty("waypoints");
        var end = points[points.GetArrayLength() - 1];
        return new Position2(end[0].GetDouble(), end[1].GetDouble());
    }

    private static double StaticDistance(Position2 a, Position2 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
