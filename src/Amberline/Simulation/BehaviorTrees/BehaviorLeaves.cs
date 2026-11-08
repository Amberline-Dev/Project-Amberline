using Amberline.Core;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>A colour built by <c>MakeColor</c> (the shipped R, G, B and A inputs, unscaled).</summary>
public readonly record struct BehaviorColor(double R, double G, double B, double A);

/// <summary>
/// The generic leaves of the shipped trees: variables, arithmetic, comparisons, constants and the world getters that need no
/// game action. Game actions (kills, gold, score, announcements, bubbles, respawn points, encounters, debug circles) are not
/// here: they fail their node and are reported once per type as unimplemented. Each leaf's inputs and outputs are the
/// parameter names of its node in the shipped XML.
/// </summary>
public static partial class BehaviorLeaves
{
    /// <summary>Reconstruction (batch 450): the tree tick that NormalizeFloatToTickRate's per-tick values are normalised to (0.1 s).</summary>
    public const double ReferenceTickSeconds = 0.1;

    public static readonly Dictionary<string, BehaviorLeaf> Generic = new(StringComparer.Ordinal)
    {
        // Variables: Input is written to Output. Reconstruction: an unset number reads as 0, an unset flag as false, an unset team as neutral.
        ["SetVarFloat"] = call => Set(call, call.Number("Input")),
        ["SetVarInt"] = call => Set(call, call.Number("Input")),
        ["SetVarBool"] = call => Set(call, call.Flag("Input")),
        ["SetVarUnitTeam"] = call => Set(call, call.TeamOf("Input")),

        // Arithmetic on numbers (Int and Float share one representation).
        ["AddFloat"] = call => Set(call, call.Number("LeftHandSide") + call.Number("RightHandSide")),
        ["AddInt"] = call => Set(call, call.Number("LeftHandSide") + call.Number("RightHandSide")),
        ["SubtractInt"] = call => Set(call, call.Number("LeftHandSide") - call.Number("RightHandSide")),
        ["MultiplyFloat"] = call => Set(call, call.Number("LeftHandSide") * call.Number("RightHandSide")),
        // Reconstruction: a division by zero fails its node (the shipped uses divide by counts that are checked first).
        ["DivideFloat"] = call => call.Number("RightHandSide") == 0.0
            ? BehaviorStatus.Failure
            : Set(call, call.Number("LeftHandSide") / call.Number("RightHandSide")),
        ["AbsFloat"] = call => Set(call, Math.Abs(call.Number("LeftHandSide"))),
        ["SubtractFloat"] = call => Set(call, call.Number("LeftHandSide") - call.Number("RightHandSide")),
        ["MinFloat"] = call => Set(call, Math.Min(call.Number("LeftHandSide"), call.Number("RightHandSide"))),
        ["MaxFloat"] = call => Set(call, Math.Max(call.Number("LeftHandSide"), call.Number("RightHandSide"))),
        // The shipped rates are per tick (CapturePointMath: "Set The rate of capture per tick", OdinScriptBehaviorTrees.xml:1580) and no tree tick
        // period is shipped. Reconstruction (batch 450): the reference tick is ReferenceTickSeconds; the node gives the step its share of a
        // per-tick value. One champion's 0.85 then fills 100 in 100 / 0.85 * 0.1 = 11.8 s, inside the wiki's 10 s band (Capture point page).
        // Batch 550: inside CapturePointMath the champions' rate also takes Priscilla's Blessing (CaptureRateScale, BehaviorLeaves.Capture).
        // Batch 711: inside EvaluateCapturePoint the regen pauses while the guardian holds OdinGuardianSuppression or OdinMinionSpellAttack.
        ["NormalizeFloatToTickRate"] = call =>
            Set(call, call.Number("LeftHandSide") * CaptureRateScale(call) * call.TickSeconds / ReferenceTickSeconds),

        // Comparisons: Success when the relation holds.
        ["EqualInt"] = call => Test(call.Number("LeftHandSide") == call.Number("RightHandSide")),
        ["NotEqualInt"] = call => Test(call.Number("LeftHandSide") != call.Number("RightHandSide")),
        ["GreaterInt"] = call => Test(call.Number("LeftHandSide") > call.Number("RightHandSide")),
        ["GreaterEqualInt"] = call => Test(call.Number("LeftHandSide") >= call.Number("RightHandSide")),
        ["GreaterFloat"] = call => Test(call.Number("LeftHandSide") > call.Number("RightHandSide")),
        ["LessFloat"] = call => Test(call.Number("LeftHandSide") < call.Number("RightHandSide")),
        ["EqualBool"] = call => Test(call.Flag("LeftHandSide") == call.Flag("RightHandSide")),
        ["EqualUnitTeam"] = call => Test(call.TeamOf("LeftHandSide") == call.TeamOf("RightHandSide")),
        ["NotEqualUnitTeam"] = call => Test(call.TeamOf("LeftHandSide") != call.TeamOf("RightHandSide")),
        ["NotEqualString"] = call => Test(call.Text("LeftHandSide") != call.Text("RightHandSide")),

        // Text and constants.
        ["AddString"] = call => Set(call, call.Text("LeftHandSide") + call.Text("RightHandSide")),
        ["MakeColor"] = call => Set(call, new BehaviorColor(call.Number("R"), call.Number("G"), call.Number("B"), call.Number("A")), "Color"),
        ["MakeVector"] = call => Set(call, new Position3(call.Number("X"), call.Number("Y"), call.Number("Z")), "Vector"),

        // World getters.
        // Reconstruction: a name the map does not place succeeds with its output unset (a Lua nil); each name is reported once.
        ["GetWorldLocationByName"] = GetWorldLocationByName,
        // Reconstruction: a missing unit fails its node (the Lua native would error on a nil unit).
        ["GetUnitTeam"] = call => call.Unit("Unit") is { } unit ? Set(call, unit.Team) : BehaviorStatus.Failure,
        ["GetUnitPosition"] = GetUnitPosition,
        ["GetChampionCollection"] = call => Set(call, call.Context.World.Heroes()),
        ["GetCollectionCount"] = call => Set(call, (double)(call.Units("Collection")?.Count ?? 0)),
        ["GetGameTime"] = call => Set(call, call.Now),
        ["GetTurret"] = GetTurret,
    };

    private static BehaviorStatus Set(BehaviorCall call, object? value, string output = "Output")
    {
        call.Out(output, value);
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus Test(bool holds) => holds ? BehaviorStatus.Success : BehaviorStatus.Failure;

    private static BehaviorStatus GetWorldLocationByName(BehaviorCall call)
    {
        var name = call.Text("Input");
        if (call.Context.NamedLocation(name) is not { } point)
        {
            call.Note("location:" + name, $"the map places no location named {name}");
            return BehaviorStatus.Success;
        }
        return Set(call, point);
    }

    private static BehaviorStatus GetUnitPosition(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        var at = unit.Position;
        return Set(call, new Position3(at.X, call.Context.Height(at), at.Z));
    }

    // Reconstruction: GetTurret(team, lane, position) answers nil when the map names no turret for that slot, as the Lua native
    // does; each unnamed slot is reported once.
    private static BehaviorStatus GetTurret(BehaviorCall call)
    {
        var team = call.TeamOf("Team");
        var lane = (int)call.Number("Lane");
        var position = (int)call.Number("Position");
        var turret = call.Context.Turret(team, lane, position);
        if (turret is null) call.Note($"turret:{team}:{lane}:{position}", $"the map names no {team} turret for lane {lane}, slot {position}");
        return Set(call, turret, "Turret");
    }
}
