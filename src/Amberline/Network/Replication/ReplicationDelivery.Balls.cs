using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    /// <summary>
    /// Recomputes the authoritative Orianna ball projection before each
    /// visibility pass.  Spell effects mark object IDs as managed when the
    /// native ball is created; this keeps manually supplied test projections
    /// independent while production projections follow the live champion
    /// state.
    /// </summary>
    public void SetManagedBallProjection(BallProjection projection)
    {
        if (projection.NetId == 0 || projection.OwnerNetId == 0)
            throw new ArgumentOutOfRangeException(nameof(projection));
        _managedBallIds.Add(projection.NetId);
        var previous = Balls.FirstOrDefault(ball => ball.NetId == projection.NetId);
        var hadPrevious = previous.NetId != 0;
        var applied = projection with
        {
            NoRender = projection.NoRender || _noRenderBallTeams.Contains(projection.Team),
        };
        UpsertBall(applied);
        if (applied.NoRender && (!hadPrevious || !previous.NoRender))
            QueueBallNoRender(applied);
    }

    public void RemoveManagedBallProjection(uint netId)
    {
        if (netId == 0) return;
        _managedBallIds.Remove(netId);
        Balls.RemoveAll(ball => ball.NetId == netId);
    }

    /// <summary>
    /// Mirrors EndOfGame.lua's SetMinionsNoRender for the managed Orianna
    /// projection. The team latch survives ball destruction/recreation; only
    /// observers that already know and see a ball receive the narrow action
    /// state delta here. A later first sight uses the latched full template.
    /// </summary>
    public void SetTeamBallNoRender(Team team)
    {
        _noRenderBallTeams.Add(team);
        foreach (var ball in Balls.Where(ball => ball.Team == team).ToArray())
        {
            // A script-spawned ball is also a minion of the world, which the ceremony's walk over the losing team (CombatService) would
            // send the generic minion word for a second time. Latching the unit here, before that walk, leaves the observers the one delta
            // below with the ball's own word (ghosted, particles).
            if (World.Get(ball.NetId) is { } unit) unit.NoRender = true;
            if (ball.NoRender) continue;
            var updated = ball with { NoRender = true };
            UpsertBall(updated);
            QueueBallNoRender(updated);
        }
    }

    public bool IsBallNoRender(uint netId) =>
        Balls.Any(ball => ball.NetId == netId && ball.NoRender);

    private void QueueBallNoRender(BallProjection ball)
    {
        if (_observers.Any(observer => observer.Ready &&
                observer.View.Known.Contains(ball.NetId) &&
                observer.View.Visible.Contains(ball.NetId)))
            Enqueue(CombatPackets.MinionActionState(
                ball.NetId, (1u << 10) | (1u << 13) | (1u << 14), NextSyncId(ball.NetId)));
    }

    /// <summary>
    /// Follows the ball units a script spawned (<c>BBSpawnMinion</c> of <c>OriannaBall</c>, announced by <see cref="SetManagedBallProjection"/>):
    /// the projection takes the unit's position, owner, skin and no-render state (its <c>SetNoRender</c> status or the end-game latch), and
    /// goes when the unit ends, which sends the observers that know it the leave. The ball does not move: a new Q is a new ball.
    /// </summary>
    private void RefreshManagedBallProjections()
    {
        if (_managedBallIds.Count == 0) return;
        foreach (var netId in _managedBallIds.ToArray())
        {
            if (World.Get(netId) is not { Alive: true } ball)
            {
                Balls.RemoveAll(existing => existing.NetId == netId);
                _managedBallIds.Remove(netId);
                continue;
            }
            var previous = Balls.FirstOrDefault(existing => existing.NetId == netId);
            var projection = new BallProjection(netId, ball.OwnerNetId, ball.Team, ball.Position, Rendered: true, SkinId: ball.SkinId,
                NoRender: previous.NoRender || _noRenderBallTeams.Contains(ball.Team) ||
                          (CharacterState.ActionWord(ball) & CharacterState.NoRenderBit) != 0);
            UpsertBall(projection);
            if (projection.NoRender && !previous.NoRender) QueueBallNoRender(projection);
        }
    }

    private void UpsertBall(BallProjection projection)
    {
        var index = Balls.FindIndex(ball => ball.NetId == projection.NetId);
        if (index >= 0) Balls[index] = projection;
        else Balls.Add(projection);
    }
}
