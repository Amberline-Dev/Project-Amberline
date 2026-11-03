namespace Amberline.Core;

// The player order a scripted displacement postpones (BBMove with MovementOrdersType POSTPONE_CURRENT_ORDER: Vayne's Tumble and Condemn,
// Blitzcrank's pull, Lee Sin's dashes ...), kept by Simulation/Spells/PostponedHeroOrders. The types keep the name of the first champion
// that needed them.

public enum VayneTumbleOrderKind
{
    Move,
    AttackMove,
    Attack,
}

/// <summary>
/// The small piece of player order state that VayneTumble postpones.  It
/// stores a destination or the last visible target position, never a route
/// generated before the dash.
/// </summary>
public sealed class VayneTumbleOrderIntent
{
    public required VayneTumbleOrderKind Kind { get; init; }
    public Position2 Destination { get; init; }
    public uint TargetNetId { get; init; }
    public Position2 LastKnownPosition { get; init; }
    public bool AutoAcquire { get; init; }
}
