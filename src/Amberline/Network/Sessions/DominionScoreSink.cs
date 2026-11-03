using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;

namespace Amberline.Network.Sessions;

/// <summary>
/// Batch 447: the match's score sink. A team score goes to every client as 0xDD (the shipped SetGameScore node); a nexus drain
/// goes through the combat service's damage path, so a nexus at zero ends the match through the shipped chain.
/// </summary>
internal sealed class DominionScoreSink(CombatService combat, ProtocolCombatTransport transport) : IDominionScoreSink
{
    public void SetGameScore(Team team, int score) => transport.SendGameScore(team, score);

    public double NexusHealth(Team team) => combat.DominionNexusHealth(team);

    // Batch 531: the quest sap and the drain show no floating text (CombatService.DamageDominionNexus's default).
    public void DamageNexus(Team team, double amount) => combat.DamageDominionNexus(team, amount);

    /// <summary>
    /// The floating text of a Dominion nexus loss (CombatService.DominionNexusLost). The texts are shipped: fontconfig_en_US.txt:3471-3472
    /// game_floating_enemy_kill = "@IntParam1@ Your Nexus Health" and game_floating_friendly_kill = "@IntParam1@ Enemy Nexus Health", the only
    /// two keys of their kind (no capture, neutralise or quest variant). The client never names them itself (they are in neither the client's
    /// routines nor the EXE), and no shipped script or tree names them (LEVELS/Map8/Scripts, DATA/Scripts), so the server sent them, and the one packet
    /// that translates a key and fills @IntParam1@ is 0x1C (<see cref="DominionPackets.FloatingText"/>), type FLOATTEXT_Score (batch 505).
    /// Batch 531 (Reconstruction: the owner's 1.0.0.125-era videos, 31 October 2026, and the shipped key names, an enemy kill and a friendly
    /// kill): a champion death's loss floats over the dying champion, the victim's team reading "-2 Your Nexus Health" and the other team
    /// "-2 Enemy Nexus Health". It goes to every player; the delivery routes it only to a view that sees the champion (a corpse stays seen,
    /// ReplicationDelivery.RefreshObserver), and the client drops it anyway for a unit it does not see (the 0x1C case of the client's packet
    /// handler: the unit's visibility flag). The client's line has the local hero as its source, so the stock FilterLevel 2 draws it over any
    /// unit (the floating-text manager's AddLine: a line passes when its source or target is the local hero).
    /// Batch 531 (Reconstruction, wiki, Nexus, Notes: "Whenever a Nexus lost health from a takedown or point capture, players on that team were
    /// notified via a small text prompt atop their champion. (-2 Your Nexus Health)"): a capture's loss (<paramref name="dying"/> null) floats
    /// over each losing-team player's own champion as "-2 Your Nexus Health" (batch 530's rule).
    /// </summary>
    public static void ShowNexusLoss(ReplicationDelivery delivery, Team team, double wholeLoss, Entity? dying)
    {
        var shown = -(int)wholeLoss;
        if (shown == 0) return;
        foreach (var observer in delivery.Observers)
        {
            if (observer.Team is not (Team.Order or Team.Chaos)) continue;
            var own = observer.Team == team;
            var target = dying?.NetId ?? (own ? observer.HeroNetId : 0u);
            if (target == 0) continue;
            delivery.EnqueueForObserver(observer.ObserverId, DominionPackets.FloatingText(target, DominionPackets.ScoreFloatingText, shown,
                own ? "game_floating_enemy_kill" : "game_floating_friendly_kill"));
        }
    }
}
