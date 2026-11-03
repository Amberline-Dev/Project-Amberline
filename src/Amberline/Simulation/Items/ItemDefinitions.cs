using Amberline.Core;

namespace Amberline.Simulation.Items;

/// <summary>Decoded item-use request from an inventory spell slot.</summary>
public readonly record struct ItemUseRequest(
    byte Slot,
    Position3 Position,
    Position3 EndPosition,
    uint TargetNetId,
    bool IsSummoner = false);

public readonly record struct ItemUseResult(bool Accepted, string Reason)
{
    public static ItemUseResult Success => new(true, string.Empty);
    public static ItemUseResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Packet boundary for item inventory mutations and ward creation. Cast,
/// cooldown, stat, and particle packets use the existing spell/effect sink.
/// Implementations must preserve call order; item scripts intentionally emit
/// reconciliation packets around their effects.
/// </summary>
public interface IItemRuntimeTransport
{
    bool IsWireReady(Entity unit);
    void SendUseItem(Entity hero, int inventorySlot, int remainingCount, int charges);
}
