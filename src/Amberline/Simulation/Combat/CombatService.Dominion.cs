using Amberline.Core;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>
    /// Batch 505: the whole points a Dominion nexus just lost on its 0xDD meter (its team, the points), for the floating text
    /// (DominionScoreSink.ShowNexusLoss). Whole points, because the text's number is an integer and a 1.5 drain moves the meter by 1 or 2.
    /// Batch 531: raised only for a loss that shows a text (a champion death and a capture); the third argument is the dying champion, null
    /// for a capture.
    /// </summary>
    public Action<Team, double, Entity?>? DominionNexusLost { get; set; }

    /// <summary>Batch 518: map 8's personal score, told of each credited champion and lane-minion kill; null on the other maps.</summary>
    public BehaviorTrees.DominionPlayerScore? DominionPlayerScore { get; set; }

    /// <summary>
    /// Batch 478b: the nexus health a champion kill stops at (wiki, Dominion, Patch History V1.0.0.127: "Champion Kills or Neutralizes" stop
    /// affecting the nexus at 100 "from 125", so 125 in 1.0.0.126).
    /// </summary>
    public const double DominionKillFloor = 125.0;

    /// <summary>
    /// Batch 505: the nexus damage of a champion kill, a capture or a neutralise, which stops at <see cref="DominionKillFloor"/> (the
    /// V1.0.0.127 note above; forum post, 15 February 2012: kills, neutralises and captures count "while the nexus is above 100 health", the
    /// later floor). A nexus at the floor takes nothing; one above it takes the amount, or down to the floor (batch 478b's kill rule).
    /// </summary>
    public void DamageDominionNexusAboveFloor(Team team, double amount, bool showLoss = false, Entity? dying = null) =>
        DamageDominionNexus(team, Math.Min(amount, Math.Max(0.0, DominionNexusHealth(team) - DominionKillFloor)), showLoss, dying);

    /// <summary>Batch 450: the team's Dominion nexus health, the 0xDD meter's value (0 once the nexus is down or absent).</summary>
    public double DominionNexusHealth(Team team) => DominionNexusHealth(_world, team);

    /// <summary>Batch 518: the same for any world (the match result's VictoryPoints).</summary>
    public static double DominionNexusHealth(WorldState world, Team team) =>
        world.Entities.Values.SingleOrDefault(unit => unit.Kind == EntityKind.Nexus && unit.Team == team && unit.Alive)?.Health ?? 0.0;

    /// <summary>
    /// Batch 447: the Dominion score takes the team's nexus health through the damage path (ApplyHit, true damage). A nexus at zero
    /// reaches the building-death path, so FinishMatch ends the game through the shipped chain.
    /// Batch 505: no 0x68 goes out. Map 8 has no nexus object on the client (batch 496: the text routine returns before any text when the target
    /// is not a client object, and the server nexus id is never created), so batch 480b's display-source 0x68 drew nothing. The loss is shown
    /// as floating text instead (<see cref="DominionNexusLost"/>).
    /// Batch 531: only a loss whose caller asks for it shows the text (a champion death, over <paramref name="dying"/>, and a capture); the
    /// drain, a neutralise and the quest sap show none.
    /// </summary>
    public void DamageDominionNexus(Team team, double amount, bool showLoss = false, Entity? dying = null)
    {
        if (_gameOver || amount <= 0.0) return;
        if (_world.Entities.Values.SingleOrDefault(unit => unit.Kind == EntityKind.Nexus && unit.Team == team && unit.Alive) is not { } nexus)
            return;
        var before = nexus.Health;
        ApplyHit(0, nexus.NetId, amount, DamageType.True, sendDamagePacket: false);
        var lost = Math.Floor(before) - Math.Floor(nexus.Health);
        if (lost > 0.0 && showLoss) DominionNexusLost?.Invoke(team, lost, dying);
    }
}
