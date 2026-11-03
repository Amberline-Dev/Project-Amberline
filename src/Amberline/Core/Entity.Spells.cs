namespace Amberline.Core;

/// <summary>Decoded 0xA2 cast request. Wire coordinates use X/Y/Z.</summary>
public readonly record struct SpellCastRequest(
    int Slot,
    bool IsSummoner,
    Position3 Position,
    Position3 EndPosition,
    uint TargetNetId);

/// <summary>
/// The auto-attack override a BuildingBlocks script installed with <c>BBOverrideAutoAttack</c>: the spellbook slot the client
/// fires basic attacks from (extra slots start at 42), the spell in it and the level the attack spell runs at.
/// <paramref name="OriginSlot"/> and <paramref name="OriginRank"/> are the champion spell (0-3) and rank whose chain armed the override
/// (Vayne's Tumble, Kayle's Righteous Fury), or -1 and 0 when the override came from nowhere in a spell chain or from an attack spell with
/// no script of its own (Vayne's Final Hour attack): the death recap names the armed spell for the damage of the empowered swing.
/// </summary>
public sealed record ScriptAttackOverride(int Slot, string SpellName, int Level, int OriginSlot = -1, int OriginRank = 0);

/// <summary>Typed failure categories used by walk-then-cast.</summary>
public enum SpellCastFailure
{
    None,
    Dead,
    Slot,
    Rank,
    Cooldown,
    Mana,
    Target,
    Range,
    Disabled,
}

public readonly record struct SpellValidation(
    bool Allowed,
    string Reason,
    SpellCastFailure Failure)
{
    public static SpellValidation Success => new(true, string.Empty, SpellCastFailure.None);
}

public readonly record struct SpellRankValidation(
    bool Allowed,
    string Reason,
    int RequiredLevel = 0);

/// <summary>
/// Per-entity deferred walk-then-cast state. The pending request is cleared
/// before execution so a cast effect cannot accidentally re-enter the order.
/// </summary>
public sealed class PendingCastState
{
    public PendingCastState(SpellCastRequest request, double deadline)
    {
        if (!double.IsFinite(deadline)) throw new ArgumentOutOfRangeException(nameof(deadline));
        Request = request;
        Deadline = deadline;
    }

    public SpellCastRequest Request { get; }
    /// <summary>When the give-up clock runs out; it restarts each time the hero closes in on the cast's point (SpellSystem.TickPendingCast).</summary>
    public double Deadline { get; set; }
    /// <summary>The closest the hero has been to the cast's point during this approach.</summary>
    public double BestGap { get; set; } = double.PositiveInfinity;
    public Position2? LastMovePoint { get; set; }
    public double NextMoveRetryAt { get; set; }
}

public sealed partial class Entity
{
    /// <summary>Two canonical patch-126 summoner names in client slot order.</summary>
    public List<string> SummonerSpells { get; set; } =
        [SummonerLoadout.DefaultFirst, SummonerLoadout.DefaultSecond];

    public PendingCastState? PendingCast { get; set; }
}
