using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Map1's shipped <c>LevelScript.luaobj</c> on the production host, for fixtures that need the
/// real wave schedule instead of the hand-written one it replaced. Build a host per case: the
/// script keeps wave state in globals (<c>LAST_WAVE</c>, <c>SpawnTable.DidPowerGroup</c>), so
/// two questions about one wave asked of one host are not two independent answers.
/// </summary>
public static class LevelScriptFixtures
{
    /// <summary>
    /// A host whose <c>OnLevelInit</c> has already run. <paramref name="teamMinions"/> is what
    /// the script's <c>GetTotalTeamMinionsSpawned</c> answers; pass <c>null</c> to leave the
    /// policy in place (zero, the client stub's own answer).
    /// </summary>
    public static LevelScriptHost Init(WorldState world, int? teamMinions = null, double now = 0.0)
    {
        var host = new LevelScriptHost(world);
        if (teamMinions is not null) host.TotalTeamMinionsSpawned = _ => teamMinions.Value;
        host.OnLevelInit(now);
        return host;
    }

    /// <summary>
    /// The wave the shipped script composes for one barracks, asked on a host of its own so
    /// probing cannot move a live scheduler's <c>LAST_WAVE</c>/<c>DidPowerGroup</c> state.
    /// </summary>
    public static IReadOnlyList<MinionType> Wave(WorldState world, Team team, string lane,
        int waveIndex, int? teamMinions = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var host = Init(world, teamMinions);
        FellInhibitors(host, world, team);
        return host.GetMinionSpawnInfo(team, lane, waveIndex).Members;
    }

    /// <summary>
    /// Raises the barracks events for every inhibitor of the opposing team the world already
    /// holds dead, the way <c>CombatService</c> does on the lethal edge. That edge is what tells
    /// the script: <c>ApplyBarracksDestructionBonuses</c> sets the beneficiary lane's
    /// <c>WillSpawnSuperMinion</c>, and only a later restore clears it.
    /// </summary>
    public static void FellInhibitors(LevelScriptHost host, WorldState world, Team spawnedTeam)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(world);
        var enemy = MinionDefinitions.Opposing(spawnedTeam);
        foreach (var fallen in world.Entities.Values.Where(entity =>
                     entity.Kind == EntityKind.Inhibitor && entity.Team == enemy && !entity.Alive))
            host.OnBarracksFell(fallen);
    }
}
