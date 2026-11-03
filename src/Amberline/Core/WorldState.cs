using System.Collections.Generic;

namespace Amberline.Core;

/// <summary>
/// Timed team-owned vision source, independent of its caster. A bubble with a <see cref="RevealUnitNetId"/> (<c>RevealSpecificUnitOnly</c>,
/// the client's <c>mRevealSpecificNetworkID</c>) gives its team sight of that one unit while it is inside the bubble, and of nothing else.
/// </summary>
public readonly record struct TimedVisionBubble(
    uint BubbleId,
    Team Team,
    Position2 Position,
    double Radius,
    double Remaining,
    bool RevealStealth = false,
    uint EffectNetId = 0,
    uint AnchorNetId = 0,
    uint RevealUnitNetId = 0,
    // Batch 452: SpecificUnitsClientOnly, the unit whose client alone is shown this bubble (0 = the whole team). Sight from it is not team sight.
    uint ClientOnlyNetId = 0);

/// <summary>Per-match entity registry and shared world services.</summary>
public sealed class WorldState
{
    private readonly HashSet<uint> _reportedInvariantNetIds = new();

    /// <summary>
    /// The C runtime's random state of this match's process (<c>CrtState</c>, retail's <c>_holdrand</c>): every Lua state
    /// of the match draws from it.
    /// </summary>
    public Amberline.Scripting.Lua.CrtState LuaRandom { get; } = new();

    public WorldState(
        GameClock? clock = null,
        NetIdAllocator? allocator = null,
        bool strictInvariants = false,
        Action<Entity, IReadOnlyList<string>>? invariantReporter = null)
    {
        Clock = clock ?? new GameClock();
        Allocator = allocator ?? new NetIdAllocator();
        StrictInvariants = strictInvariants;
        InvariantReporter = invariantReporter;
    }

    public GameClock Clock { get; }
    public NetIdAllocator Allocator { get; }
    public OrderedDictionary<uint, Entity> Entities { get; } = new();
    public bool StrictInvariants { get; set; }
    public Action<Entity, IReadOnlyList<string>>? InvariantReporter { get; set; }
    public uint? LocalHeroNetId { get; set; }
    public List<TimedVisionBubble> TimedVisionBubbles { get; } = new();

    /// <summary>
    /// The unit pairs a script linked with <c>BBLinkVisibility</c> (Lux's Final Spark cubes, Xerath's Arcanopulse, Karthus' Wall of Pain): a team that
    /// sees one unit of a link sees the other (<c>VisibilityRules.ComputeRawVisibility</c> closes the visible set over the links). Each pair is
    /// ordered (smaller id first).
    /// </summary>
    public HashSet<(uint, uint)> VisibilityLinks { get; } = new();

    public void LinkVisibility(uint first, uint second)
    {
        if (first == second) return;
        foreach (var pair in VisibilityLinks.Where(pair => !Entities.ContainsKey(pair.Item1) || !Entities.ContainsKey(pair.Item2)).ToArray())
            VisibilityLinks.Remove(pair);
        VisibilityLinks.Add(first < second ? (first, second) : (second, first));
    }

    public void UnlinkVisibility(uint first, uint second) =>
        VisibilityLinks.Remove(first < second ? (first, second) : (second, first));

    public Entity Register(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Entities[entity.NetId] = entity;

        var violations = EntityInvariants.Check(entity);
        if (violations.Count == 0)
        {
            return entity;
        }

        if (StrictInvariants)
        {
            throw new InvalidOperationException(
                $"Invariant violation for net ID 0x{entity.NetId:X8}: {string.Join("; ", violations)}");
        }

        if (InvariantReporter is not null && _reportedInvariantNetIds.Add(entity.NetId))
        {
            try
            {
                InvariantReporter(entity, violations);
            }
            catch
            {
                // A diagnostic sink must never take down the live world.
            }
        }

        return entity;
    }

    public bool Remove(uint netId) => Entities.Remove(netId);

    public Entity? Get(uint netId) => Entities.TryGetValue(netId, out var entity) ? entity : null;

    public bool IsAlive(uint netId) => IsAlive(Get(netId));

    public static bool IsAlive(Entity? entity) => entity is not null && entity.Alive;

    public static bool IsEnemy(Entity? first, Entity? second)
    {
        if (first is null || second is null)
        {
            return false;
        }

        var firstNeutral = first.Team == Team.Neutral;
        var secondNeutral = second.Team == Team.Neutral;
        return firstNeutral || secondNeutral
            ? firstNeutral != secondNeutral
            : first.Team != second.Team;
    }

    public IReadOnlyList<Entity> Heroes()
    {
        return Entities.Values
            .Where(entity => entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0)
            .ToArray();
    }

    public Entity? LocalHero() => LocalHeroNetId is uint netId ? Get(netId) : null;

    public uint Allocate(UnitType unitType) => Allocator.Alloc(unitType);

    /// <summary>
    /// The client's timed bubble manager adds a timed team bubble
    /// and returns its id. The id is new on every call, but an identical live bubble (same team, radius, stealth flag, unit or position)
    /// only has its lifetime set to <paramref name="duration"/> by <c>FindUpdateBubble</c>, so the returned id then names no bubble and
    /// <see cref="TryRemoveTimedVisionBubble"/> of it does nothing, exactly as in the client's manager. A bubble that follows a unit
    /// passes <paramref name="anchorNetId"/>; the list is aged by <c>SpellSystem.Tick</c> and delivered by the replication layer.
    /// </summary>
    public uint AddTimedVisionBubble(Team team, Position2 position, double radius, double duration, bool revealStealth = false,
        uint anchorNetId = 0, uint revealUnitNetId = 0, uint clientOnlyNetId = 0)
    {
        var bubbleId = Allocate(UnitType.Missile);
        for (var index = 0; index < TimedVisionBubbles.Count; index++)
        {
            var live = TimedVisionBubbles[index];
            if (live.Team != team || (float)live.Radius != (float)radius || live.RevealStealth != revealStealth ||
                live.AnchorNetId != anchorNetId || live.RevealUnitNetId != revealUnitNetId || live.ClientOnlyNetId != clientOnlyNetId) continue;
            if (anchorNetId == 0 && ((float)live.Position.X != (float)position.X || (float)live.Position.Z != (float)position.Z)) continue;
            TimedVisionBubbles[index] = live with { Remaining = duration };
            return bubbleId;
        }
        TimedVisionBubbles.Add(new TimedVisionBubble(bubbleId, team, position, radius, duration, revealStealth, 0, anchorNetId, revealUnitNetId,
            clientOnlyNetId));
        return bubbleId;
    }

    /// <summary>The client's timed bubble manager removes the bubble with that id; false when none has it (0 never does).</summary>
    public bool TryRemoveTimedVisionBubble(uint bubbleId, out TimedVisionBubble removed)
    {
        removed = default;
        var index = bubbleId == 0 ? -1 : TimedVisionBubbles.FindIndex(bubble => bubble.BubbleId == bubbleId);
        if (index < 0) return false;
        removed = TimedVisionBubbles[index];
        TimedVisionBubbles.RemoveAt(index);
        return true;
    }

    public SpatialIndex<Entity> IndexUnits(IEnumerable<Entity>? entities = null)
    {
        var source = entities ?? Entities.Values;
        return new SpatialIndex<Entity>(source.Select(entity =>
            (entity.Position.X, entity.Position.Z, entity)));
    }

    public IReadOnlyList<Entity> Near(SpatialIndex<Entity> index, Position2 center, double radius)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.Query(center.X, center.Z, radius);
    }

    public void Clear()
    {
        Entities.Clear();
        _reportedInvariantNetIds.Clear();
        LocalHeroNetId = null;
        TimedVisionBubbles.Clear();
    }
}
