using Amberline.Core;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The vision family of the BuildingBlocks natives (Phase 3, W28): the timed perception bubbles (<c>BBAddUnitPerceptionBubble</c>,
/// <c>BBAddPosPerceptionBubble</c>, <c>BBRemovePerceptionBubble</c>) and the visible nearest-N iteration. All of them drive the one existing
/// timed team vision service: <see cref="WorldState.TimedVisionBubbles"/>, aged by <c>SpellSystem.Tick</c>, read as sight and stealth
/// reveal by <c>ItemVisionService</c> and sent to the clients by the replication layer (<c>0x7B</c> / <c>0x26</c> / <c>0x38</c>).
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> VisionBound = new(StringComparer.Ordinal)
    {
        "BBAddUnitPerceptionBubble", "BBAddPosPerceptionBubble", "BBRemovePerceptionBubble",
    };

    private bool ExecuteVision(BbCall call)
    {
        switch (call.Function)
        {
            case "BBAddUnitPerceptionBubble": AddUnitPerceptionBubble(call); return true;
            case "BBAddPosPerceptionBubble": AddPosPerceptionBubble(call); return true;
            case "BBRemovePerceptionBubble": RemovePerceptionBubble(call); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The unit a bubble is shown to one client of (<c>SpecificUnitsClientOnlyVar</c>, the client body's unit getter; the Odin
    /// shrines pass <c>Owner</c>, which is the turret itself in its CharOnActivate). Batch 452: the bubble carries its netid
    /// (<see cref="TimedVisionBubble.ClientOnlyNetId"/>) and the replication layer shows it to that unit's client only. Zero when the variable
    /// holds no unit (the ordinary team bubble, as the client body reads it).
    /// </summary>
    private uint ClientOnlyNetId(BbCall call) => Unit(call, "SpecificUnitsClientOnlyVar") is { } client ? client.NetId : 0u;

    /// <summary>
    /// The team of a bubble: <c>TeamVar</c> names a variable that holds a team number (<c>BBGetTeamID</c>) and wins over the literal
    /// <c>Team</c> (the client's team getter, the team parameter's conversion; all 98 shipped bubbles pass the variable). The client maps it
    /// with <c>ToPerceptionBubbleType</c>: 100 Order, 200 Chaos, 300 Neutral, anything else is a soft assert and a bubble nobody draws, here
    /// null (no bubble, id 0).
    /// </summary>
    private static Team? BubbleTeam(BbCall call)
    {
        return NativeTeam(call) is { } value && TeamMapping.TryFromNative((int)value, out var team) ? team : null;
    }

    /// <summary>
    /// <c>BBAddUnitPerceptionBubble</c> : <c>Radius</c> and
    /// <c>Duration</c> (the shared number getter), <c>Target</c> (required: without a unit the client does nothing and pushes no id), the
    /// team, <c>RevealSteath</c> (sic), <c>RevealSpecificUnitOnly</c> (a unit variable: when it holds a unit the bubble reveals that unit
    /// only, see <see cref="TimedVisionBubble.RevealUnitNetId"/>; an empty variable makes an ordinary bubble, as in the client) and the
    /// client-only variant. It spawns a timed bubble that follows the unit
    /// (the timed bubble's spawn, the unit overload: the flag word gets the IsUnit bit) and writes its id to
    /// <c>BubbleID</c>. A radius of 0 is the unit's own perception radius (the client's bubble code, see the replication tables).
    /// </summary>
    private void AddUnitPerceptionBubble(BbCall call)
    {
        // A perception bubble is a mutation, not a contribution: in a stats sample it accumulates on the buff's mutation
        // clock (BbHookFrame.MutationSuppressed). Without that gate OdinGuardianBuff's and TurretBonusHealth's bare adds
        // would spawn a 25 s bubble on every 0.25 s sample of the hook.
        if (_host.HasFrame && _host.Frame.MutationSuppressed)
        {
            StoreOut(call, "BubbleID", 0.0);
            return;
        }
        var radius = Sum(call, "Radius");
        if (Unit(call, "TargetVar") is not { } target) return;
        var duration = Sum(call, "Duration");
        var team = BubbleTeam(call);
        var reveal = Unit(call, "RevealSpecificUnitOnlyVar");
        var id = team is { } bubbleTeam
            ? Spells.World.AddTimedVisionBubble(bubbleTeam, target.Position, radius > 0.0 ? radius : VisibilityRules.TerrainVisionRange(target),
                duration, Flag(call, "RevealSteath", false), target.NetId, reveal?.NetId ?? 0u, ClientOnlyNetId(call))
            : 0u;
        StoreOut(call, "BubbleID", (double)id);
    }

    /// <summary>
    /// <c>BBAddPosPerceptionBubble</c> : the same parse with a
    /// point instead of a unit: <c>Radius</c>, <c>Pos</c> (required; a variable that holds a point, or a unit whose position is meant, as
    /// the shipped <c>PosVar="Owner"</c> calls pass), <c>Duration</c>, <c>Team</c>, the client-only unit and <c>RevealSteath</c>. The overload
    /// the timed bubble's point overload clears the IsUnit flag, so the bubble stays where it was put. The id is pushed
    /// whether or not a bubble was spawned.
    /// </summary>
    private void AddPosPerceptionBubble(BbCall call)
    {
        // The same mutation gate as the unit overload above (one native family, one clock).
        if (_host.HasFrame && _host.Frame.MutationSuppressed)
        {
            StoreOut(call, "BubbleID", 0.0);
            return;
        }
        var radius = Sum(call, "Radius");
        var position = SpawnPosition(call, "PosVar");
        var duration = Sum(call, "Duration");
        var team = BubbleTeam(call);
        var reveal = Unit(call, "RevealSpecificUnitOnlyVar");
        var id = team is { } bubbleTeam
            ? Spells.World.AddTimedVisionBubble(bubbleTeam, position, radius, duration, Flag(call, "RevealSteath", false),
                revealUnitNetId: reveal?.NetId ?? 0u, clientOnlyNetId: ClientOnlyNetId(call))
            : 0u;
        StoreOut(call, "BubbleID", (double)id);
    }

    /// <summary>
    /// <c>BBRemovePerceptionBubble</c> : <c>BubbleIDVar</c> and
    /// <c>BubbleIDVarTable</c> name the variable that holds the id an add native wrote; the client reads it as an integer (an unset or non-numeric
    /// variable is a Lua error, "must be present as a Integer, but it is not") and, when it is not 0, has the timed manager remove the bubble with
    /// that id (the timed manager's remove). An id that names no bubble (a refreshed duplicate, an expired bubble) removes nothing.
    /// </summary>
    private void RemovePerceptionBubble(BbCall call)
    {
        if (call.Value("BubbleIDVar") is not string name) return;
        if (call.Table("BubbleIDVarTable", create: false)[name] is not double number)
            throw new BbRuntimeException($"{name} must be present as a Integer, but it is not.");
        var id = number >= 1.0 && number <= uint.MaxValue ? (uint)number : 0u;
        if (Spells.World.TryRemoveTimedVisionBubble(id, out var removed) && removed.EffectNetId != 0)
            Spells.Effects.KillEffect(removed.EffectNetId);
    }
}
