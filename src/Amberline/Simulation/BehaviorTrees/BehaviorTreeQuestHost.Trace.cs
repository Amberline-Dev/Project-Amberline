using System.Globalization;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Minions;

namespace Amberline.Simulation.BehaviorTrees;

// DIAGNOSTICS: batch 478 (map 8 outcome trace). With the environment variable AMBERLINE_CAPTURE_TRACE set to a file, one line is appended
// per second of game time: each capture point's owner and progress variables, the control counts, and every live Odin wave unit (team,
// position, AI state, attack target, route length and last waypoint, taunt buffs). It changes no gameplay: it only reads state.
public sealed partial class BehaviorTreeQuestHost
{
    private static readonly string? CaptureTraceFile = Environment.GetEnvironmentVariable("AMBERLINE_CAPTURE_TRACE");
    private double _nextCaptureTrace;

    private static readonly string[] CaptureTraceVariables =
    [
        "CurrentPointOwner", "NewPointOwner", "AttackingTeam", "CaptureProgress", "CaptureRate", "CaptureTeam",
        "NonWaveMinionsPresent", "OrderMinionCaptureRate", "ChaosMinionCaptureRate",
    ];

    internal void TraceCaptures(double now)
    {
        if (CaptureTraceFile is null || now < _nextCaptureTrace) return;
        _nextCaptureTrace = now + 1.0;
        var line = new StringBuilder();
        line.Append("t=").Append(now.ToString("0.0", CultureInfo.InvariantCulture));
        line.Append(" control O=").Append(ControlCount(Team.Order)).Append(" C=").Append(ControlCount(Team.Chaos));
        for (var index = 0; index < _captures.Count; index++)
        {
            line.Append(" | P").Append(index + 1);
            foreach (var name in CaptureTraceVariables)
                line.Append(' ').Append(name).Append('=').Append(Convert.ToString(_captures[index].Variable(name), CultureInfo.InvariantCulture));
        }
        foreach (var hero in Context.World.Entities.Values)
        {
            if (hero.UnitType != UnitType.Hero) continue;
            line.Append(" | H ").Append(hero.Name).Append(' ').Append(hero.Team)
                .Append(" at=(").Append(hero.Position.X.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(hero.Position.Z.ToString("0", CultureInfo.InvariantCulture)).Append(") alive=").Append(hero.Alive)
                .Append(" hp=").Append(hero.Health.ToString("0", CultureInfo.InvariantCulture))
                .Append(" st=").Append(hero.AiState);
        }
        foreach (var unit in Context.World.Entities.Values)
        {
            if (!unit.Alive || OdinWaves.ScriptOf(unit) is null) continue;
            var route = unit.Waypoints.Count == 0 ? "none" : $"{unit.Waypoints.Count}:({unit.Waypoints[^1].X:0},{unit.Waypoints[^1].Z:0})";
            var taunts = unit.Buffs.Count(buff => buff.Type == BuffType.Taunt);
            line.Append(" | U").Append(unit.NetId).Append(' ').Append(unit.Team).Append(' ').Append(unit.Name)
                .Append(" at=(").Append(unit.Position.X.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(unit.Position.Z.ToString("0", CultureInfo.InvariantCulture)).Append(") st=").Append(unit.MinionState)
                .Append(" tgt=").Append(unit.AttackTarget?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append(" route=").Append(route).Append(" taunts=").Append(taunts);
        }
        File.AppendAllText(CaptureTraceFile, line.Append('\n').ToString());
    }
}
