using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    /// <summary>
    /// Reconstruction ("the source" in these comments is the Python server this one replaced): the pre-combat shrine-vision
    /// phase is that server's design for revealing an enemy fountain turret. The stock client has no such phase and the retail
    /// rule that reveals the enemy fountain turret (and when it is hidden again) is not in the client or the shipped data.
    /// Commits the source shrine-vision phase before combat target selection.
    /// Provider visibility and an existing can-attack-valid target both keep
    /// the shrine visible.  A falling edge clears the target packets and the
    /// exact observer team's team-visibility bit; the source does not send a
    /// generic local leave for this pre-placed object.
    /// </summary>
    public void PrepareShrineVisibility(Func<Entity, Entity, bool> canAttack)
    {
        ArgumentNullException.ThrowIfNull(canAttack);
        RefreshManagedBallProjections();
        _preparedShrines.Clear();

        var active = _observers.Where(observer => observer.Ready).ToArray();
        foreach (var observer in active)
        {
            var providers = VisibilityRules.BuildTeamProviders(
                World, observer.Team, Balls, AdditionalProviders);
            foreach (var shrine in World.Entities.Values.Where(IsEnemyShrine(observer.Team)))
            {
                // Who sees the shrine is the client's turret line-of-sight test, as for any turret (batch 244).
                var providerVisible = VisibilityRules.BuildingRevealed(Environment, providers, shrine);
                var targetVisible = shrine.AttackTarget is uint targetId &&
                    World.Get(targetId) is { } target && canAttack(shrine, target);
                var rawVisible = shrine.Alive && (providerVisible || targetVisible);
                var shouldBeVisible = DebounceShrineVisibility(observer, shrine, rawVisible);
                if (shouldBeVisible)
                    _preparedShrines.Add((observer.ObserverId, shrine.NetId));

                var wasVisible = observer.View.Visible.Contains(shrine.NetId);
                if (shouldBeVisible == wasVisible) continue;

                if (shouldBeVisible)
                {
                    // Batch 452: a shrine the observer has never been told about (a point turret spawned mid-match) gets its CreateTurret
                    // first, as the first-sight path sends it, so its reveal never precedes its creation.
                    var firstSight = !observer.View.Known.Contains(shrine.NetId);
                    if (firstSight)
                    {
                        SendFreshReplication(observer, Services.BuildEntityCreation(shrine, observer));
                        observer.View.Known.Add(shrine.NetId);
                    }
                    // Dynamic shrine reveals are C2 + EE.  They do not use the
                    // ordinary 0x80/CC creation burst for a pre-placed turret.
                    // Batch 452: a first sight leaves out the protection block (0xCC, its own sync id), which the shipped tree's init does not
                    // set (the point turret stays targetable until its owner stat block says otherwise).
                    var reveal = Services.BuildReveal(shrine, observer);
                    SendAll(observer, firstSight ? reveal.Where(packet => packet.Plaintext.Span[0] != 0xCC).ToArray() : reveal);
                    observer.View.Known.Add(shrine.NetId);
                    observer.View.Visible.Add(shrine.NetId);
                    // Combat's transport readiness is match-global (true when
                    // any ready observer has the object).  The source reveal is
                    // committed before CombatTick, so publish that creation
                    // state before target packets are considered.
                    shrine.Created = true;
                    observer.View.LostAt.Remove(shrine.NetId);
                    continue;
                }

                var hadTarget = shrine.AttackTarget is not null;
                if (hadTarget)
                {
                    Send(observer, CombatPackets.AiSetTarget(shrine.NetId, 0));
                    Send(observer, CombatPackets.AiSetTargetHero(shrine.NetId, 0));
                    shrine.AttackTarget = null;
                }
                Send(observer, VisionPackets.LeaveTeamVisibility(
                    shrine.NetId, (byte)observer.Team));
                observer.View.Visible.Remove(shrine.NetId);
                observer.View.LostAt[shrine.NetId] = SimulationTime();
            }
        }
    }

    private static Func<Entity, bool> IsEnemyShrine(Team team) => entity =>
        entity.UnitType == UnitType.Turret && entity.Team != team && entity.Alive &&
        VisibilityRules.IsShrine(entity);

    private bool DebounceShrineVisibility(
        ReplicationObserver observer, Entity shrine, bool rawVisible)
    {
        if (rawVisible)
        {
            observer.View.LostAt.Remove(shrine.NetId);
            return true;
        }
        if (!observer.View.Visible.Contains(shrine.NetId))
        {
            observer.View.LostAt.Remove(shrine.NetId);
            return false;
        }
        var now = SimulationTime();
        var lostAt = observer.View.LostAt.GetValueOrDefault(shrine.NetId, now);
        observer.View.LostAt[shrine.NetId] = lostAt;
        return now - lostAt < VisibilityRules.ShrineHideDebounceSeconds;
    }
}
