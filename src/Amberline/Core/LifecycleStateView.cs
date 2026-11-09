
namespace Amberline.Core;

/// <summary>
/// Typed, read-only projection of champion lifecycle state for the monitor. Every champion runs from its shipped scripts now (19 October 2026),
/// whose state lives in buffs the monitor watches directly, so no champion state is left to project; the seam stays for the replay tooling.
/// </summary>
public sealed record LifecycleStateView
{
    public static LifecycleStateView FromEntity(Entity entity) => new();
}
