using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 453: Dominion's respawn points (the 0xDE records) and the leaves of the relics and shrines. The respawn leaves are the node
/// names of OdinScriptBehaviorTrees.xml: CreateRespawnPoint (the Initialization tree, :2217-2277), and AllowTeamRespawnPoint and
/// DisallowTeamRespawnPoint (RespawnLogic, :5052). Each leaf's inputs and outputs are the parameter names of its node in the shipped XML.
/// Batch 584: RespawnLogic has no caller in the shipped release (no Procedure node, BTInstance, .ssln item, map or AI Lua names it), so
/// retail 126 ran it nowhere and the two allow leaves stay dormant, as shipped; champions revive at their base. It sets no timer: the
/// death timers are DeathTimes.inibin's, with map 8's respawn waves (MapProgressionRules.DeathTime).
/// </summary>
public static partial class BehaviorLeaves
{
    public static readonly Dictionary<string, BehaviorLeaf> RelicTable = new(StringComparer.Ordinal)
    {
        ["CreateRespawnPoint"] = CreateRespawnPoint,
        ["AllowTeamRespawnPoint"] = call => SetRespawnTeam(call, allow: true),
        ["DisallowTeamRespawnPoint"] = call => SetRespawnTeam(call, allow: false),
    };

    // Reconstruction: GraveyardA to E are the point graveyards (RespawnPointUIID 0 to 4, :2217-2259), so graveyard i is capture point
    // i + 1, kept by GetTurret position i + 2; its record names the point's guardian (batch 466, as the 0xDB and 0xDC records do). The base
    // spawns (5 and 6, :2262-2277) stand at no point, so they send no record.
    private static BehaviorStatus CreateRespawnPoint(BehaviorCall call)
    {
        var index = (int)call.Number("RespawnPointUIID");
        Output(call, "RespawnPointID", index);
        if (index < 0 || index > 4)
            return NoOp(call, "respawn:base", $"respawn point {index} is a base spawn with no point turret: no 0xDE record (Reconstruction)");
        if (call.Context.PointGuardian(index + 2) is not { } point)
        {
            call.Note("respawn:turret", $"no guardian stands at point graveyard {index}: its 0xDE create is not sent");
            return BehaviorStatus.Failure;
        }
        // Reconstruction: the point index is the graveyard's RespawnPointUIID (0 to 4); the net ID is the point guardian's (batch 445 layout, batch 466).
        return Send(call, point.NetId, DominionPackets.CreateRespawnPoint((byte)index, point.NetId));
    }

    // Reconstruction: RespawnLogic (:5052) allows the point owner's team and disallows the other at GraveyardID. The shipped 0xDE state
    // byte carries no team, so the state is a team mask per graveyard (bit 0 Order, bit 1 Chaos), sent as the state byte; a mask that
    // does not change sends nothing (Send). Retail's state meaning is not recovered (batch 445 open).
    private static BehaviorStatus SetRespawnTeam(BehaviorCall call, bool allow)
    {
        var index = (int)call.Number("RespawnPointID");
        var bit = call.TeamOf("Team") switch { Team.Order => 1, Team.Chaos => 2, _ => 0 };
        if (index < 0 || index > 4 || bit == 0)
            return NoOp(call, "respawn-team", $"graveyard {index} for team bit {bit}: no respawn state record (Reconstruction)");
        if (call.Context.PointGuardian(index + 2) is not { } point) return BehaviorStatus.Failure;
        var mask = call.Context.RespawnTeams.GetValueOrDefault(index);
        mask = (byte)(allow ? mask | bit : mask & ~bit);
        call.Context.RespawnTeams[index] = mask;
        return Send(call, point.NetId, DominionPackets.SetRespawnPointState(point.NetId, mask));
    }
}
