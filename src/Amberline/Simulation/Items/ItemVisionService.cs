using System.Linq;
using Amberline.Core;
using Amberline.Network.Replication;

namespace Amberline.Simulation.Items;

/// <summary>
/// Vision providers and stealth detection owned by item effects. Everything an item provides is a bubble its own
/// script adds (<c>BBAddPosPerceptionBubble</c> → <c>WorldState.TimedVisionBubbles</c>) or a ward it spawns, so the
/// service reads the world and not the item runtime (batch 149 step 4).
/// </summary>
public sealed class ItemVisionService
{
    private readonly WorldState _world;

    public ItemVisionService(WorldState world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public IReadOnlyList<VisionProvider> Providers(Team team) => Providers(team, includeTimed: true);

    public IReadOnlyList<VisionProvider> Providers(Team team, bool includeTimed)
    {
        var result = new List<VisionProvider>();
        if (includeTimed)
            foreach (var bubble in _world.TimedVisionBubbles)
                // A bubble that reveals one unit gives no sight of the ground (VisibilityRules.SpecificReveal). A position bubble
                // (0x7B) has no IsUnit flag, so the client's fog lights its whole disc (VisionProvider.IsUnit).
                if (bubble.Team == team && bubble.Remaining > 0.0 && bubble.RevealUnitNetId == 0 &&
                    TimedPosition(bubble) is { } position)
                    result.Add(new VisionProvider(position, bubble.Radius, isUnit: bubble.AnchorNetId != 0));
        foreach (var ward in _world.Entities.Values.Where(entity => entity.Alive && entity.IsWard &&
                     !entity.IsSummonerBeacon && entity.Team == team))
            result.Add(new VisionProvider(ward.Position, VisibilityRules.WardVisionRange));
        // Batch 550: the Lightbringer mark (OdinLightbringer: 3185's hit, 3187's spell damage, HextechSweeperArea) has no rule here. Its sight is
        // its own script's two unit bubbles (OnBuffActivate: 400 without, 50 with RevealSteath; OnBuffDeactivate removes both), read above as
        // timed bubbles; the Python-era buff-name rule that also gave a 400 disc was a duplicate, as Lee Sin's were (6704fa64).
        return result;
    }

    public bool RevealsStealthAt(Team team, Position2 position)
    {
        foreach (var unit in _world.Entities.Values)
        {
            if (!unit.Alive || unit.Team != team) continue;
            if (unit.IsWard && !unit.IsSummonerBeacon && unit.RevealsStealth &&
                DistanceSquared(unit.Position, position) <= VisibilityRules.WardVisionRange * VisibilityRules.WardVisionRange)
                return true;
            if (unit.UnitType == UnitType.Hero &&
                unit.Buffs.Any(buff => buff.Name is "OracleElixirSight" or "OracleExtractSight") &&
                DistanceSquared(unit.Position, position) <= 750.0 * 750.0)
                return true;
        }
        // A turret's own detection is not a rule here any more (batch 159 part 1): every map turret character's
        // Spells/TurretBonusHealth.luaobj adds its own BBAddUnitPerceptionBubble with RevealSteath true, so the bubble
        // below is what sees stealth in its BubbleSize (800 lane, 1000 nexus, 1600 ChaosTurretShrine).
        return _world.TimedVisionBubbles.Any(bubble => bubble.Team == team && bubble.Remaining > 0.0 &&
            bubble.RevealStealth && bubble.RevealUnitNetId == 0 &&
            (bubble.AnchorNetId == 0 ? bubble.Position : LiveAnchor(bubble)?.Position) is { } center &&
            DistanceSquared(center, position) <= bubble.Radius * bubble.Radius);
    }

    /// <summary>
    /// The unit a bubble follows (the timed bubble's unit overload, the IsUnit bit), or null while it is
    /// gone or dead. A turret's own detection is <c>Spells/TurretBonusHealth.luaobj</c>'s <c>BBAddUnitPerceptionBubble</c>
    /// (BubbleSize 800 lane, 1000 nexus, 1600 ChaosTurretShrine), and a dead turret grants no sight
    /// (replication.b9-turret-shaco-true-sight-ranges), so the bubble stops at its anchor like
    /// <see cref="VisibilityRules.SpecificReveal"/> already read it.
    /// </summary>
    private Entity? LiveAnchor(TimedVisionBubble bubble) =>
        bubble.AnchorNetId != 0 && _world.Get(bubble.AnchorNetId) is { Alive: true } anchor ? anchor : null;

    private Position2? TimedPosition(TimedVisionBubble bubble) =>
        // An attached bubble sees from its unit, the shrine included: the client draws the 0x26 there (batch 259).
        bubble.AnchorNetId == 0 ? bubble.Position : LiveAnchor(bubble)?.Position;

    private static double DistanceSquared(Position2 a, Position2 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
}
