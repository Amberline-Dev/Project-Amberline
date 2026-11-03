using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The movement family of the BuildingBlocks natives (Phase 3, W22): <c>BBMove</c>, <c>BBMoveAway</c>, <c>BBMoveToUnit</c>,
/// <c>BBStopMove</c>, <c>BBStopMoveBlock</c>, <c>BBStartTrackingCollisions</c>, <c>BBTeleportToPosition</c>,
/// <c>BBTeleportToKeyLocation</c>, <c>BBModifyPosition</c> and the random-point reads the knock-up scripts feed them with.
/// The client registers the four move natives as bare returns, so their server bodies are not in the stock files: every parameter reading below is a
/// Reconstruction from the 56 + 10 + 3 shipped call sites, checked against the hand-written ports of the same scripts (Vayne's Tumble
/// and Condemn, Blitzcrank's pull and Power Fist, Lee Sin's dashes and kick, Janna's Howling Gale, Nidalee's Pounce). The motion itself
/// is the existing movement system's <see cref="ForcedMoveState"/>; <see cref="BbScriptMoves"/> starts, announces and ends it.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> MovementBound = new(StringComparer.Ordinal)
    {
        "BBMove", "BBMoveAway", "BBMoveToUnit", "BBStopMove", "BBStopMoveBlock", "BBStartTrackingCollisions",
        "BBTeleportToPosition", "BBTeleportToKeyLocation", "BBModifyPosition",
        "BBGetRandomPointInAreaUnit", "BBGetRandomPointInAreaPosition", "BBIsPathable", "BBGetGroundHeight", "BBGetNearestPassablePosition",
    };

    private bool ExecuteMovement(BbCall call)
    {
        switch (call.Function)
        {
            case "BBMove": Move(call); return true;
            case "BBMoveAway": MoveAway(call); return true;
            case "BBMoveToUnit": MoveToUnit(call); return true;
            case "BBStopMove" or "BBStopMoveBlock":
                // The stop-move-block native takes Target alone; BBStopMove (a stub) the same. Reconstruction: both end the unit's
                // displacement where it stands.
                if (Unit(call, "TargetVar") is { } stopped) _host.Moves.Stop(stopped);
                return true;
            case "BBStartTrackingCollisions":
                // The native parses Target and Value and nothing else: the body is server-only.
                if (Unit(call, "TargetVar") is { } tracked) _host.Moves.SetCollisionTracking(tracked, Flag(call, "Value", false));
                return true;
            case "BBTeleportToPosition": TeleportToPosition(call); return true;
            case "BBTeleportToKeyLocation": TeleportToKeyLocation(call); return true;
            case "BBModifyPosition": ModifyPosition(call); return true;
            case "BBGetRandomPointInAreaUnit": RandomPointInArea(call, aroundUnit: true); return true;
            case "BBGetRandomPointInAreaPosition": RandomPointInArea(call, aroundUnit: false); return true;
            case "BBIsPathable": IsPathable(call); return true;
            case "BBGetGroundHeight": GetGroundHeight(call); return true;
            case "BBGetNearestPassablePosition": GetNearestPassablePosition(call); return true;
            default: return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Parameter helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The value of a variable parameter: <c>vars[XVarTable][XVar]</c> (the root scope when no table is named).</summary>
    private static object? TargetOf(BbCall call, string key) => call.Table(key + "Table", create: false)[call.Value(key)];

    private static Position2 PositionOf(object? value, string what) => value switch
    {
        Entity unit => unit.Position,
        BbTable point => BbSpellHost.Ground(point),
        _ => throw new BbRuntimeException($"{what} has no position"),
    };

    /// <summary>
    /// <c>MovementType</c>, <c>MovementOrdersType</c> and <c>MovementOrdersFacing</c>: an absent key reads the enum's first value
    /// (FURTHEST_WITHIN_RANGE, CANCEL_ORDER, FACE_MOVEMENT_DIRECTION), which 17 of the 56 <c>BBMove</c> sites rely on.
    /// </summary>
    private static (BbQueryMode Type, bool Postpone, bool Face) MoveOptions(BbCall call) =>
        (call.Value("MovementType") is double type ? (BbQueryMode)(int)type : BbQueryMode.FURTHEST_WITHIN_RANGE,
         call.Value("MovementOrdersType") is double order && (BbOrderMode)(int)order == BbOrderMode.POSTPONE_CURRENT_ORDER,
         !(call.Value("MovementOrdersFacing") is double facing && (BbFacing)(int)facing == BbFacing.KEEP_CURRENT_FACING));

    // ------------------------------------------------------------------------------------------------------------
    // BBMove, BBMoveAway, BBMoveToUnit
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBMove</c>: <c>Unit</c> dashes toward <c>Target</c> (a point, or a unit) at <c>Speed</c> with the arc <c>Gravity</c>.
    /// <c>IdealDistance</c> above 0 caps the way for a point target only: a dash to a unit ends on that unit (the landing fits the body, as an
    /// uncapped point move does). Evidence (batch 369): the shipped unit-target calls are Jarvan IV's Dragon Strike pull (<c>IdealDistance</c> 500
    /// to the standard) and Shen's dash (no cap); the follow form <c>BBMoveToUnit</c> never reads <c>IdealDistance</c>; the League wiki's Dragon
    /// Strike text says Jarvan IV "dashes to its location". Every capped point call passes a fixed length or the point's own distance (Dragon's
    /// Rage 800, Fling 420, Power Fist 80, Riven 275 and 375). Pushes (<c>BBMoveAway</c>) keep their caps.
    /// <c>MoveBackBy</c> stops that far short of the target, <c>MovementType</c> picks the landing rule (see <see cref="BbScriptMoves.Start"/>),
    /// <c>MovementOrdersType</c> cancels or postpones the hero's order and <c>MovementOrdersFacing</c> turns the unit along the move or leaves its facing.
    /// </summary>
    private void Move(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBMove has no unit");
        var targetValue = TargetOf(call, "TargetVar");
        var target = PositionOf(targetValue, "BBMove target");
        var origin = unit.Position;
        var dx = target.X - origin.X;
        var dz = target.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var travel = distance;
        var ideal = Sum(call, "IdealDistance");
        var capped = ideal > 0.0 && targetValue is not Entity;
        if (capped) travel = Math.Min(travel, ideal);
        travel = Math.Max(0.0, travel - Math.Max(0.0, Sum(call, "MoveBackBy")));
        var requested = distance <= 1e-9 ? origin : new Position2(origin.X + dx / distance * travel, origin.Z + dz / distance * travel);
        var (type, postpone, face) = MoveOptions(call);
        _host.Moves.Start(unit, requested, Sum(call, "Speed"), Sum(call, "Gravity"), type, postpone, face, fitBody: !capped);
    }

    /// <summary>
    /// <c>BBMoveAway</c>: the knock-back. <c>Distance</c> is how far from <c>AwayFrom</c> (a point or a unit) the unit ends (the shipped
    /// calls add the current distance in a variable: Headbutt's 700 + distance pushes 700 further), <c>IdealDistance</c> above 0 caps the
    /// way travelled (Maokai's, Riven's and Caitlyn's 225 pushes pass 100 and last the 0.5 s of their root at speed 200).
    /// <para>
    /// Reconstruction (W26d, 19 October 2026): the "current distance" that call subtracts is the one <c>BBDistanceBetweenObjects</c> returns, so
    /// when <c>AwayFrom</c> is a unit it is the gap between the two bodies (centre distance minus both collision radii, never below 0) and
    /// "literal + that variable" pushes exactly the literal; when it is a point (Janna's Monsoon, Orianna's Shockwave, Gragas's barrel, Caitlyn's net)
    /// it is the centre distance. Evidence: the AntiBot build's helper family would measure bodies edge to edge (the helper BBDistanceBetweenObjectsSq,
    /// d^2 - (ra + rb + margin)^2; not valid evidence here, and the client has no BBDistanceBetweenObjects native), the retail Lee Sin bug (Dragon's Rage is "intended to be 800" but shortened by up to the two gameplay radii:
    /// <c>BlindMonkRKick</c> moves the target to a POINT at gap + 800, which is the same mismatch the other way round, with the move native taking
    /// points) and the League wiki's fixed 700 units for Headbutt, the calls that pair the variable with a literal (Alistar, Tristana, Maokai, Riven,
    /// Rammus) and Trundle's pillar (<c>TrundleWallPush</c>: <c>Distance</c> 200 - gap from the pillar unit, which a centre distance would turn into
    /// a push that never fires). Before this the native subtracted the centre distance, so Headbutt pushed 700 minus both radii.
    /// </para>
    /// <para>
    /// The travel is not cut off when the buff that started it ends (W25b checked this for Monsoon: <c>Distance</c> 1000 at speed 1200 is up to
    /// 0.83 s against the 0.75 s of the <c>MoveAway</c> stun). Neither the shipped scripts nor the client say it should be: <c>MoveAway.luaobj</c>
    /// and <c>Move.luaobj</c> have no time or duration parameter, the client's parser of the native is a data stub,
    /// and the one cap the scripts do use for a push shorter than its stun is <c>IdealDistance</c> (the 100 above).
    /// Monsoon passes <c>IdealDistance</c> 1000, equal to its distance, so it caps nothing; a push from point-blank outlasts its stun by about 0.08 s.
    /// </para>
    /// </summary>
    private void MoveAway(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBMoveAway has no unit");
        var center = PositionOf(TargetOf(call, "AwayFromVar"), "BBMoveAway center");
        var dx = unit.Position.X - center.X;
        var dz = unit.Position.Z - center.Z;
        var current = Math.Sqrt(dx * dx + dz * dz);
        // Direction and the "on the centre" test use the centres; the distance the call tops up is the gap when the centre is a unit.
        var away = TargetOf(call, "AwayFromVar") is Entity awayUnit
            ? Math.Max(0.0, current - unit.CombatRadius - awayUnit.CombatRadius)
            : current;
        var travel = Sum(call, "Distance") - away;
        var ideal = Sum(call, "IdealDistance");
        if (ideal > 0.0) travel = Math.Min(travel, ideal);
        if (travel <= 1e-9) return;
        // Reconstruction: a unit standing on the centre is pushed along its own facing.
        var (ux, uz) = current > 1e-6 ? (dx / current, dz / current) : (unit.Facing.X, unit.Facing.Z);
        var requested = new Position2(unit.Position.X + ux * travel, unit.Position.Z + uz * travel);
        var (type, postpone, face) = MoveOptions(call);
        _host.Moves.Start(unit, requested, Sum(call, "Speed"), Sum(call, "Gravity"), type, postpone, face);
    }

    /// <summary>
    /// <c>BBMoveToUnit</c>: a dash that follows <c>Target</c> (Lee Sin's Resonating Strike): <c>MaxTrackDistance</c> is how far the
    /// target may get, <c>MoveBackBy</c> the distance kept from it, <c>TimeOverride</c> a time cap; <c>IdealDistance</c> is the
    /// distance at the call and changes nothing here.
    /// </summary>
    private void MoveToUnit(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBMoveToUnit has no unit");
        if (Unit(call, "TargetVar") is not { } target) return;
        var postpone = call.Value("MovementOrdersType") is double order && (BbOrderMode)(int)order == BbOrderMode.POSTPONE_CURRENT_ORDER;
        _host.Moves.StartFollow(unit, target, Sum(call, "Speed"), Sum(call, "Gravity"), Sum(call, "MaxTrackDistance"),
            Sum(call, "MoveBackBy"), Sum(call, "TimeOverride"), postpone, faceMovement: true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Teleports
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBTeleportToPosition</c> (<c>Owner</c> and <c>CastPositionName</c>, the name of the script variable that
    /// holds the point; the body sets the actor's position through the client's position setter). The server corrects the landing the way every
    /// shipped teleport is corrected (Flash, Arcane Shift, Deceive) and sends the teleport packet.
    /// </summary>
    private void TeleportToPosition(BbCall call)
    {
        var unit = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBTeleportToPosition has no unit");
        // Reconstruction: a corpse is not moved. Death retires a buff whose deactivation teleports its holder (DeceiveFade, the Hallucinate applicator),
        // and the hand-written Shaco stopped there for a dead hero ("without installing stealth or teleporting the dead unit").
        if (!unit.Alive) return;
        var name = call.Value("CastPositionName") as string ?? throw new BbRuntimeException("BBTeleportToPosition names no position variable");
        _host.Moves.Teleport(unit, PositionOf(call.Scope.Vars[name], "BBTeleportToPosition position"), correct: true);
    }

    /// <summary>
    /// <c>BBTeleportToKeyLocation</c> (<c>Owner</c>, <c>Team</c> and <c>Location</c>; the body looks the point up in
    /// the client's locations table and teleports the actor). Only <c>SPAWN_LOCATION</c> is used (Recall's landing, the disconnect
    /// buffs): the team's spawn point, <see cref="MapDefinitions.TeamSpawn"/>. Any other location raises.
    /// </summary>
    private void TeleportToKeyLocation(BbCall call)
    {
        var unit = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBTeleportToKeyLocation has no unit");
        var location = call.Value("Location") is double number ? (BbSpawnLocation)(int)number : BbSpawnLocation.SPAWN_HQ;
        if (location != BbSpawnLocation.SPAWN_LOCATION) throw BbSpellHost.Unbound($"BBTeleportToKeyLocation {location}", "W22");
        if (call.Value("Team") is not double nativeTeam || !TeamMapping.TryFromNative((int)nativeTeam, out var team) || team == Team.Neutral)
            throw new BbRuntimeException("BBTeleportToKeyLocation names no playing team");
        _host.Moves.Teleport(unit, MapDefinitions.TeamSpawn(team), correct: false);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Points
    // ------------------------------------------------------------------------------------------------------------

    /// <summary><c>BBModifyPosition</c>: the point in <c>Position</c> plus <c>(x, y, z)</c>, each read by the shared number getter.</summary>
    private void ModifyPosition(BbCall call)
    {
        var table = call.Table("PositionVarTable", create: false);
        var name = call.Value("PositionVar");
        if (table[name] is not BbTable point) throw new BbRuntimeException("BBModifyPosition names no position");
        table[name] = BbTable.Point(BbRuntimeNames.Number(point["x"]) + Sum(call, "x"),
            BbRuntimeNames.Number(point["y"]) + Sum(call, "y"), BbRuntimeNames.Number(point["z"]) + Sum(call, "z"));
    }

    /// <summary>
    /// <c>BBGetRandomPointInAreaUnit</c> / <c>BBGetRandomPointInAreaPosition</c>: a point of
    /// the ring between <c>InnerRadius</c> and <c>Radius</c> around the unit or the point, into <c>Result</c>. Both go through the shared
    /// <c>GetRandomAreaPoint</c>: it asserts <c>InnerRadius &lt;= Radius</c>, draws
    /// the radius first, the uniform draw = inner + (radius - inner) * U (uniform in the radius, not by area), then the
    /// angle, a uniform draw over 0 to 360 in degrees, and returns x = cx + r cos a, z = cz + r sin a. The knock-up bounces pass equal radii
    /// (a point on the circle); the Worm's geyser (25 to 100) and Shaco's Hallucinate (50 to 250) draw from a ring. The random stream is
    /// the server's (the match random); the angle is drawn here over the same full turn in radians.
    /// </summary>
    private void RandomPointInArea(BbCall call, bool aroundUnit)
    {
        var center = aroundUnit
            ? (Unit(call, "TargetVar") ?? throw new BbRuntimeException("BBGetRandomPointInAreaUnit has no unit")).Position
            : PositionOf(TargetOf(call, "PosVar"), "BBGetRandomPointInAreaPosition center");
        var outer = Math.Max(0.0, Sum(call, "Radius"));
        var inner = Math.Clamp(Sum(call, "InnerRadius"), 0.0, outer);
        var radius = Spells.Random.Uniform(inner, outer);
        var angle = Spells.Random.Uniform(0.0, 2.0 * Math.PI);
        StoreOut(call, "Result", _host.Point(new Position2(center.X + Math.Cos(angle) * radius, center.Z + Math.Sin(angle) * radius)));
    }

    /// <summary>
    /// <c>BBIsPathable</c> (<c>DestPos</c>, the output <c>Result</c>): the client answers from the navigation cell of
    /// the point, true when bit 1 of the cell flags is clear, which is the floor the per-frame movement gate accepts
    /// (<see cref="NavigationGrid.IsMovementWalkable"/>).
    /// </summary>
    private void IsPathable(BbCall call) =>
        StoreOut(call, "Result", Spells.Movement.Navigation.IsMovementWalkable(PositionOf(TargetOf(call, "DestPosVar"), "BBIsPathable")));

    /// <summary>
    /// <c>BBGetGroundHeight</c> (<c>QueryPos</c>, the output <c>GroundPos</c>): the query point with its height
    /// replaced by the ground height of the navigation grid (a point outside the grid keeps its own).
    /// </summary>
    private void GetGroundHeight(BbCall call) =>
        StoreOut(call, "GroundPos", _host.Point(PositionOf(TargetOf(call, "QueryPosVar"), "BBGetGroundHeight")));

    /// <summary>
    /// <c>BBGetNearestPassablePosition</c> (<c>Position</c>, <c>Owner</c>, the output <c>NewPosition</c>): the AI
    /// manager's search for the passable point nearest to the position for the unit's pathing radius. The server uses the placement
    /// search every shipped teleport already corrects its landing with (<see cref="NavigationGrid.ClosestSummonPlacement"/>).
    /// </summary>
    private void GetNearestPassablePosition(BbCall call)
    {
        var owner = Unit(call, "Owner") ?? throw new BbRuntimeException("BBGetNearestPassablePosition has no unit");
        var point = PositionOf(TargetOf(call, "PositionVar"), "BBGetNearestPassablePosition");
        StoreOut(call, "NewPosition", _host.Point(Spells.Movement.Navigation.ClosestSummonPlacement(point, Spells.Movement.MovementRadius(owner))));
    }
}
