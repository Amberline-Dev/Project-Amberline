using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using System.Reflection;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, movement family (Phase 3, W22): <c>BBMove</c>, <c>BBMoveAway</c>, <c>BBMoveToUnit</c>, the teleports and the
/// <c>BuffOnMove*</c> hooks, run on the production simulation (Sivir's script module hosts the probe scripts). The probe scripts are
/// hand-built block lists; one check runs the shipped <c>PowerFistSlow</c> buff, the script that has the largest share of BBMove,
/// <c>GetRandomPointInAreaUnit</c> and status calls in one hook.
/// </summary>
public static class BbMoveChecks
{
    private const string Probe = "W22MoveProbe";

    private static BbScript Script(IReadOnlyList<BbBlock> activate, bool hooks = true)
    {
        var events = new List<BbEventTable> { new("OnBuffActivate", activate) };
        if (hooks)
        {
            events.Add(new BbEventTable("BuffOnMoveSuccess", [BbTest.SetVar("Success", true, "InstanceVars")]));
            events.Add(new BbEventTable("BuffOnMoveFailure", [BbTest.SetVar("Failed", true, "InstanceVars")]));
            events.Add(new BbEventTable("BuffOnCollisionTerrain", [BbTest.SetVar("Terrain", true, "InstanceVars")]));
            events.Add(new BbEventTable("BuffOnMoveEnd", [BbTest.SetVar("Ended", true, "InstanceVars")]));
        }
        return new BbScript(Probe, "Spells/" + Probe + ".luaobj", [], events, []);
    }

    private static BbBuffInstance Activate(BbM3.Match match, Entity holder, Entity caster, BbScript script, double duration = 30.0)
    {
        var state = match.Runtime.Spells.Buffs.Add(holder, script.Name, BuffType.Internal, duration, caster.NetId)
            ?? throw new InvalidOperationException("the probe buff was not added");
        var instance = new BbBuffInstance { Script = script, Name = script.Name, Owner = holder, State = state, CasterNetId = caster.NetId };
        match.Module.Router!.RunBuffHook(instance, "OnBuffActivate", caster);
        return instance;
    }

    private static BbBlock Point(string variable, double distance, double angle = 0.0) =>
        BbTest.Block("BBGetPointByUnitFacingOffset", ["UnitVar", "Owner", "Distance", distance, "OffsetAngle", angle, "PositionVar", variable]);

    private static BbBlock Move(string target, double speed, double gravity, string type = "FURTHEST_WITHIN_RANGE", double ideal = 0.0,
        double back = 0.0, string order = "CANCEL_ORDER") =>
        BbTest.Block("BBMove", ["UnitVar", "Owner", "TargetVar", target, "Speed", speed, "Gravity", gravity, "MoveBackBy", back,
            "MovementType", BbTest.Ref(type), "MovementOrdersType", BbTest.Ref(order), "MovementOrdersFacing", BbTest.Ref("FACE_MOVEMENT_DIRECTION"),
            "IdealDistance", ideal]);

    private static bool Set(BbBuffInstance instance, string name) => instance.InstanceVars[name] is true;

    private static double From(Entity unit, Position2 origin) =>
        Math.Sqrt((unit.Position.X - origin.X) * (unit.Position.X - origin.X) + (unit.Position.Z - origin.Z) * (unit.Position.Z - origin.Z));

    [Check("scripting.bb-move-dash-runs-to-its-point-and-fires-the-success-and-end-hooks")]
    public static void ABbMoveDashesAndTheBuffHooksFollow()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        foe.Facing = new Position2(1.0, 0.0);
        var origin = foe.Position;
        var instance = Activate(match, foe, match.Hero, Script([Point("Dest", 300), Move("Dest", 1000, 0)]));

        TestAssert.True(foe.ForcedMove is { Completed: false } move && move.Effect == Probe && move.Speed == 1000.0,
            "the dash is the unit's forced move, named after the running script");
        TestAssert.True(foe.ForcedMove!.DashPresentation, "the dash packet was announced (0x67)");
        TestAssert.Equal(match.Hero.NetId, foe.ForcedMove.SourceNetId, "the buff's caster started it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        match.Advance(0.7);
        TestAssert.True(foe.ForcedMove is null, "the movement system is released after the landing");
        TestAssert.Near(300.0, From(foe, origin), 4.0, "the unit landed 300 units along its facing");
        TestAssert.True(Set(instance, "Success") && Set(instance, "Ended") && !Set(instance, "Failed"), "BuffOnMoveSuccess then BuffOnMoveEnd ran");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure after the landing");
    }

    /// <summary>
    /// Batch 117 (62fa55bb), kept narrow by the Power Fist regression: a buff sized to the trip (Malphite's Unstoppable Force: Duration = distance / speed) lives
    /// through the emulator's own slack until its BuffOnMoveSuccess has run; a buff the script made shorter than the trip (Power Fist's 0.5 s lock over a
    /// 0.9 s knock-up) is not stretched.
    /// </summary>
    [Check("scripting.bb-move-buff-sized-to-the-trip-reaches-its-success-hook-and-a-shorter-one-is-not-stretched")]
    public static void ATripSizedBuffOutlivesTheSlackAndAShorterOneDoesNot()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        foe.Facing = new Position2(1.0, 0.0);
        // 300 units at 1000: the buff lasts the trip (0.3 s). Restated in batch 252: since batch 241 the move has no trip-sized timer (it ends on
        // its destination), and a trip-sized buff lives one 30 Hz step past the trip so the move's hooks run (BbScriptMoves.Track).
        var sized = Activate(match, foe, match.Hero, Script([Point("Dest", 300), Move("Dest", 1000, 0)]), 0.3);
        var trip = BbSpellHost.Distance(foe.Position, foe.ForcedMove!.Destination) / 1000.0;
        TestAssert.Near(trip + 1.0 / 30.0, sized.State.ExpiresAt, 1e-9, "the trip-sized buff lives one step past the trip");
        match.Advance(0.7);
        TestAssert.True(Set(sized, "Success") && Set(sized, "Ended"), "the trip-sized buff was still there when the move landed");

        foe.Facing = new Position2(1.0, 0.0);
        var brief = Activate(match, foe, match.Hero, Script([Point("Dest", 300), Move("Dest", 1000, 0)]), 0.1);
        TestAssert.Near(0.1, brief.State.ExpiresAt, 1e-9, "the shorter buff keeps the life the script gave it");
        match.Advance(0.2);
        TestAssert.True(foe.ForcedMove is { Completed: false }, "the move is still under way");
        TestAssert.False(foe.Buffs.Any(buff => buff.Name == Probe), "the buff the script made shorter than the trip ended at its own time");
        TestAssert.False(Set(brief, "Success"), "and its move had not landed yet");
    }

    [Check("scripting.bb-move-ideal-distance-and-move-back-by-shorten-the-way")]
    public static void IdealDistanceCapsAndMoveBackByStopsShort()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        foe.Facing = new Position2(1.0, 0.0);
        var origin = foe.Position;
        Activate(match, foe, match.Hero, Script([Point("Dest", 400), Move("Dest", 1500, 0, ideal: 120)], hooks: false));
        TestAssert.Near(120.0, BbSpellHostDistance(foe.ForcedMove!.Destination, origin), 4.0, "IdealDistance caps the way at 120");
        match.Advance(0.5);
        foe.Position = origin;
        Activate(match, foe, match.Hero, Script([Point("Dest", 400), Move("Dest", 1500, 0, back: 100)], hooks: false));
        TestAssert.Near(300.0, BbSpellHostDistance(foe.ForcedMove!.Destination, origin), 4.0, "MoveBackBy 100 stops 100 short of the point");
    }

    [Check("scripting.bb-move-ideal-distance-does-not-cap-a-dash-to-a-unit")]
    public static void IdealDistanceDoesNotCapADashToAUnit()
    {
        // IdealDistance caps a dash toward a point (the check above). A dash whose TargetVar is a unit ends on that unit, the way Jarvan IV's
        // Dragon Strike pulls him to a Demacian Standard (TargetVar Attacker, IdealDistance 500). The hero is the buff's Caster, 400 units
        // ahead of the foe on clear floor, and the cap is 120: the foe must reach the hero, not stop 120 along.
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        foe.Facing = new Position2(1.0, 0.0);
        var origin = foe.Position;
        var along = ClearDirection(match, foe, origin, 400.0);
        match.Hero.Position = new Position2(origin.X + along.X * 400.0, origin.Z + along.Z * 400.0);
        Activate(match, foe, match.Hero, Script([Move("Caster", 1500, 0, ideal: 120)], hooks: false));
        TestAssert.Near(400.0, BbSpellHostDistance(foe.ForcedMove!.Destination, origin), 4.0, "a dash to a unit ends on it; IdealDistance 120 does not cap it");
    }

    [Check("scripting.bb-move-away-from-a-unit-keeps-the-ideal-distance-cap")]
    public static void MoveAwayFromAUnitKeepsItsIdealDistanceCap()
    {
        // A push is not a dash: IdealDistance still caps a BBMoveAway whose centre is a unit (Maokai's trunk line, Riven's knockback and the
        // Powerball stun pass 100, which holds the root for 0.5 s). The dash rule above does not reach pushes.
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var centre = match.Hero.Position;
        var along = ClearDirection(match, foe, centre, 300.0);
        foe.Position = new Position2(centre.X + along.X * 100.0, centre.Z + along.Z * 100.0);
        var origin = foe.Position;
        var away = BbTest.Block("BBMoveAway", ["UnitVar", "Owner", "AwayFromVar", "Attacker", "Speed", 200, "Gravity", 10, "Distance", 225,
            "DistanceInner", 0, "MovementType", BbTest.Ref("FURTHEST_WITHIN_RANGE"), "MovementOrdersType", BbTest.Ref("CANCEL_ORDER"),
            "IdealDistance", 100]);
        Activate(match, foe, match.Hero, Script([away], hooks: false));
        TestAssert.Near(100.0, BbSpellHostDistance(foe.ForcedMove!.Destination, origin), 4.0, "the push from a unit is capped at its IdealDistance 100");
    }

    private static double BbSpellHostDistance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// A direction from <paramref name="from"/> along which <paramref name="unit"/> has body-clear floor for <paramref name="length"/> units
    /// (the test the shared landing applies to its endpoint, here at every 25 units). The open floor of the map is diagonal lanes and
    /// jungle wall, so a fixed east ray clips short.
    /// </summary>
    private static Position2 ClearDirection(BbM3.Match match, Entity unit, Position2 from, double length)
    {
        var movement = match.Runtime.Spells.Movement;
        var grid = movement.Navigation;
        var clearance = movement.MovementRadius(unit) + 4.0;
        foreach (var step in Enumerable.Range(0, 16))
        {
            var angle = step * Math.PI / 8.0;
            var dir = new Position2(Math.Cos(angle), Math.Sin(angle));
            var clear = Enumerable.Range(1, (int)(length / 25.0)).All(k =>
            {
                var point = new Position2(from.X + dir.X * 25.0 * k, from.Z + dir.Z * 25.0 * k);
                return grid.IsMovementWalkable(point) && grid.PointClearance(point, clearance) >= clearance;
            });
            if (clear) return dir;
        }
        throw new InvalidOperationException($"no direction from {from} is clear for {length} units");
    }

    [Check("scripting.bb-move-away-pushes-to-the-distance-from-the-centre")]
    public static void MoveAwayEndsAtTheScriptsDistanceFromTheCentre()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var centre = match.Hero.Position;
        var along = ClearDirection(match, foe, centre, 500.0);
        foe.Position = new Position2(centre.X + along.X * 300.0, centre.Z + along.Z * 300.0);
        // Headbutt's form: Distance = a literal plus the current distance in a variable, so the push is the literal. The variable is what
        // BBDistanceBetweenObjects returns, the gap between the two bodies (since batch 112), and since W26d BBMoveAway tops up that same gap when
        // its centre is a unit, so the push is the literal and the unit ends 200 further from the centre than it stood.
        var away = BbTest.Block("BBMoveAway", ["UnitVar", "Owner", "AwayFromVar", "Attacker", "Speed", 1200, "Gravity", 20, "Distance", 200,
            "DistanceVar", "Current", "DistanceInner", 0, "MovementType", BbTest.Ref("FURTHEST_WITHIN_RANGE"),
            "MovementOrdersType", BbTest.Ref("CANCEL_ORDER"), "IdealDistance", 0]);
        var gap = BbTest.Block("BBDistanceBetweenObjects", ["DestVar", "Current", "ObjectVar1", "Owner", "ObjectVar2", "Attacker"]);
        var instance = Activate(match, foe, match.Hero, Script([gap, away]));
        var current = From(foe, centre);
        TestAssert.Near(current + 200.0, BbSpellHostDistance(foe.ForcedMove!.Destination, centre), 4.0, "the unit ends 200 further from the centre");
        match.Advance(0.8);
        TestAssert.True(foe.ForcedMove is null && Set(instance, "Success"), "it arrived and the success hook ran");
    }

    [Check("scripting.bb-move-away-from-a-point-subtracts-the-centre-distance")]
    public static void MoveAwayFromAPointTopsUpTheCentreDistance()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var centre = match.Hero.Position;
        var along = ClearDirection(match, foe, centre, 500.0);
        foe.Position = new Position2(centre.X + along.X * 300.0, centre.Z + along.Z * 300.0);
        // Janna's Monsoon and Orianna's Shockwave name a point (the caster's position, the ball): there is no body to take a radius from, so
        // the distance the call tops up stays the centre distance, and a script that adds BBDistanceBetweenObjectAndPoint pushes the literal.
        var away = BbTest.Block("BBMoveAway", ["UnitVar", "Owner", "AwayFromVar", "Centre", "Speed", 1200, "Gravity", 20, "Distance", 200,
            "DistanceVar", "Current", "DistanceInner", 0, "MovementType", BbTest.Ref("FURTHEST_WITHIN_RANGE"),
            "MovementOrdersType", BbTest.Ref("CANCEL_ORDER"), "IdealDistance", 0]);
        var heart = BbTest.Block("BBGetPointByUnitFacingOffset", ["UnitVar", "Attacker", "Distance", 0, "OffsetAngle", 0, "PositionVar", "Centre"]);
        var distance = BbTest.Block("BBDistanceBetweenObjectAndPoint", ["DestVar", "Current", "ObjectVar", "Owner", "PointVar", "Centre"]);
        Activate(match, foe, match.Hero, Script([heart, distance, away]));
        var current = From(foe, centre);
        TestAssert.Near(current + 200.0, BbSpellHostDistance(foe.ForcedMove!.Destination, centre), 4.0, "the unit ends 200 further from the point");
    }

    [Check("scripting.bb-move-hooks-report-a-replaced-move-as-a-failure")]
    public static void AMoveThatIsReplacedRunsTheFailureAndEndHooks()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        foe.Facing = new Position2(1.0, 0.0);
        var instance = Activate(match, foe, match.Hero, Script([Point("Dest", 600), Move("Dest", 300, 0)]));
        TestAssert.True(foe.ForcedMove is not null, "the move started");
        match.Advance(0.2);
        foe.ForcedMove = null; // another displacement or a death took the unit
        match.Advance(0.1);
        TestAssert.True(Set(instance, "Failed") && Set(instance, "Ended") && !Set(instance, "Success"), "BuffOnMoveFailure and BuffOnMoveEnd ran, not Success");
    }

    [Check("scripting.bb-move-terrain-collision-is-reported-to-a-tracking-script")]
    public static void AWallStopsAFirstWallHitMoveAndTheTrackedUnitHearsOfIt()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var grid = match.Runtime.Spells.Movement.Navigation;
        // The first wall on a ray from the mid lane: a ray that crosses 1,600 units of floor before it meets one.
        Position2? wall = null;
        Position2 direction = default;
        foreach (var angle in Enumerable.Range(0, 16).Select(step => step * Math.PI / 8.0))
        {
            var dir = new Position2(Math.Cos(angle), Math.Sin(angle));
            for (var travel = 100.0; travel <= 1500.0; travel += 50.0)
            {
                var point = new Position2(foe.Position.X + dir.X * travel, foe.Position.Z + dir.Z * travel);
                if (grid.IsMovementWalkable(point)) continue;
                if (travel > 400.0) { wall = point; direction = dir; }
                break;
            }
            if (wall is not null) break;
        }
        TestAssert.True(wall is not null, "the mid lane has a wall within 1,500 units");
        foe.Facing = direction;
        var track = BbTest.Block("BBStartTrackingCollisions", ["TargetVar", "Owner", "Value", true]);
        var instance = Activate(match, foe, match.Hero, Script([track, Point("Dest", 1800), Move("Dest", 1500, 0, "FIRST_COLLISION_HIT")]));
        match.Advance(2.0);
        TestAssert.True(foe.ForcedMove is null, "the move is over");
        TestAssert.True(Set(instance, "Terrain") && Set(instance, "Failed") && Set(instance, "Ended") && !Set(instance, "Success"),
            "BuffOnCollisionTerrain, BuffOnMoveFailure and BuffOnMoveEnd ran");
        var stopped = From(foe, new Position2(wall!.Value.X - direction.X * 1500, wall.Value.Z - direction.Z * 1500));
        TestAssert.True(grid.IsMovementWalkable(foe.Position) && stopped > 0.0, "the unit stands on floor, short of the wall");
    }

    [Check("scripting.bb-move-tracked-dash-reports-the-units-it-passes-once")]
    public static void ATrackingDashStrikesWhatItPassesOnce()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var along = ClearDirection(match, foe, foe.Position, 400.0);
        foe.Facing = along;
        var bystander = BbM3.Enemy(match, "Bystander", new Position2(foe.Position.X + along.X * 250.0, foe.Position.Z + along.Z * 250.0), UnitType.Minion, Team.Order);
        // BbM3.Enemy builds a bare unit; the contact test is the sum of the two movement radii, so the bystander needs a body.
        bystander.Radius = 40.0;
        var script = new BbScript(Probe, "Spells/" + Probe + ".luaobj", [],
            [
                new BbEventTable("OnBuffActivate", [BbTest.Block("BBStartTrackingCollisions", ["TargetVar", "Owner", "Value", true]),
                    Point("Dest", 600), Move("Dest", 600, 0)]),
                new BbEventTable("BuffOnCollision", [BbTest.Block("BBSetVarInTable", ["DestVar", "Hit", "DestVarTable", "InstanceVars", "SrcVar", "Target"])]),
            ], []);
        var instance = Activate(match, foe, match.Hero, script);
        match.Advance(1.4);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        TestAssert.True(ReferenceEquals(instance.InstanceVars["Hit"], bystander), "BuffOnCollision saw the unit in the way as Target");
    }

    [Check("scripting.bb-move-teleports-land-and-the-key-location-is-the-team-spawn")]
    public static void TeleportsMoveTheUnitAndSendTheTeleportId()
    {
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        var router = match.Module.Router!;
        var before = hero.TeleportId;
        var positional = new BbScript("W22Teleport2", "Spells/W22Teleport2.luaobj", [],
            [new BbEventTable("TargetExecute", [Point("There", 0.0),
                BbTest.Block("BBModifyPosition", ["PositionVar", "There", "x", -300, "y", 0, "z", 0]),
                BbTest.Block("BBTeleportToPosition", ["OwnerVar", "Owner", "CastPositionName", "There"])])], []);
        var start = hero.Position;
        router.RunTargetExecute(positional, hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), hero);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure after the positional teleport: " +
            string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        TestAssert.Near(300.0, BbSpellHostDistance(start, hero.Position), 60.0, "BBModifyPosition moved the point 300 west and the teleport corrected only slightly");
        TestAssert.Equal((byte)(before + 1), hero.TeleportId, "the teleport advanced the id");

        var keyed = new BbScript("W22Teleport", "Spells/W22Teleport.luaobj", [],
            [new BbEventTable("TargetExecute", [BbTest.Block("BBTeleportToKeyLocation",
                ["OwnerVar", "Owner", "Location", BbTest.Ref("SPAWN_LOCATION"), "Team", BbTest.Ref("TEAM_CHAOS")])])], []);
        router.RunTargetExecute(keyed, hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), hero);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        TestAssert.Near(13933.79, hero.Position.X, 3.0, "Team=TEAM_CHAOS teleports to the Chaos spawn (x)");
        TestAssert.Near(14170.09, hero.Position.Z, 3.0, "Team=TEAM_CHAOS teleports to the Chaos spawn (z)");
        TestAssert.Equal((byte)(before + 2), hero.TeleportId, "the second teleport advanced the id again");
    }

    [Check("scripting.bb-move-random-point-is-on-the-ring-the-script-names")]
    public static void TheRandomPointLiesOnTheRing()
    {
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        var script = new BbScript("W22Random", "Spells/W22Random.luaobj", [],
            [new BbEventTable("TargetExecute", [
                BbTest.Block("BBGetRandomPointInAreaUnit", ["TargetVar", "Owner", "Radius", 80, "InnerRadius", 80, "ResultVar", "Bounce"]),
                BbTest.Block("BBDistanceBetweenObjectAndPoint", ["DestVar", "Dist", "ObjectVar", "Owner", "PointVar", "Bounce"]),
                BbTest.Block("BBSetVarInTable", ["DestVar", "Dist", "DestVarTable", "CharVars", "SrcVar", "Dist"]),
            ])], []);
        for (var i = 0; i < 5; i++)
        {
            match.Module.Router!.RunTargetExecute(script, hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), hero);
            TestAssert.Near(80.0, (double)match.Module.CharVars(hero)["Dist"]!, 0.01, "equal radii give a point exactly 80 away");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-move-random-ring-point-is-uniform-in-radius")]
    public static void TheRandomRingRadiusIsUniformInRadius()
    {
        // Batch 284 (N8): the client's random-area draw takes r = inner + (outer - inner) * U, uniform in the radius,
        // so the mean distance of the 0..100 ring is 50; a draw uniform by area would give 66.7.
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        var script = new BbScript("W284Ring", "Spells/W284Ring.luaobj", [],
            [new BbEventTable("TargetExecute", [
                BbTest.Block("BBGetRandomPointInAreaUnit", ["TargetVar", "Owner", "Radius", 100, "InnerRadius", 0, "ResultVar", "Point"]),
                BbTest.Block("BBDistanceBetweenObjectAndPoint", ["DestVar", "Dist", "ObjectVar", "Owner", "PointVar", "Point"]),
                BbTest.Block("BBSetVarInTable", ["DestVar", "Dist", "DestVarTable", "CharVars", "SrcVar", "Dist"]),
            ])], []);
        const int draws = 1000;
        var total = 0.0;
        for (var i = 0; i < draws; i++)
        {
            match.Module.Router!.RunTargetExecute(script, hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), hero);
            var distance = (double)match.Module.CharVars(hero)["Dist"]!;
            TestAssert.True(distance <= 100.01, "the point lies inside the ring");
            total += distance;
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
        TestAssert.Near(50.0, total / draws, 5.0, "the radius is uniform in [inner, outer]: mean 50, not the 66.7 of a draw by area");
    }

    [Check("scripting.bb-move-power-fist-slow-runs-its-shipped-knock-up")]
    public static void TheShippedPowerFistSlowKnocksTheUnitUp()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        // The ring point is drawn at random and the landing is quantized to the client's 2-unit lattice: a point that rounds outward past the
        // 80-unit cap lands the 10-unit backoff short. A zero draw puts the point due east, and the foe on a lattice point keeps it on one.
        typeof(SpellSystem).GetField("_random", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(match.Runtime.Spells, new ZeroRandom());
        foe.Position = new Position2(7301.0, 7001.0);
        var origin = foe.Position;
        var script = BbM3.Script("PowerFistSlow");
        var instance = Activate(match, foe, match.Hero, script);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        var move = foe.ForcedMove ?? throw new InvalidOperationException("the shipped OnBuffActivate started no move");
        TestAssert.Equal(90.0, move.Speed, "the shipped Speed");
        TestAssert.Near(80.0, BbSpellHostDistance(move.Destination, origin), 4.0, "the shipped IdealDistance 80 on a ring of radius 80");
        TestAssert.Equal(20.0, move.DashGravity, "the shipped Gravity went to the dash packet");
        match.Advance(1.2);
        TestAssert.True(foe.ForcedMove is null, "the knock-up ended");
        TestAssert.True(instance.Script == script, "the instance runs the shipped buff");
    }

    private sealed class ZeroRandom : IRandomSource
    {
        public double NextDouble() => 0.0;
    }

    [Check("scripting.bb-move-natives-are-bound")]
    public static void TheMovementNativesAreBound()
    {
        foreach (var native in new[]
                 {
                     "BBMove", "BBMoveAway", "BBMoveToUnit", "BBStopMove", "BBStopMoveBlock", "BBStartTrackingCollisions",
                     "BBTeleportToPosition", "BBTeleportToKeyLocation", "BBModifyPosition", "BBGetRandomPointInAreaUnit",
                     "BBGetRandomPointInAreaPosition",
                 })
            TestAssert.True(BbNativeHandlers.IsBound(native), native + " is bound");
    }
}
