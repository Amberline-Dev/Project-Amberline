using Amberline.Core;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Ezreal's casts and bolts as the script engine keeps them (phase 3, 19 October 2026): there is no per-hero Ezreal state any more, the shared
/// projectile queue holds a cast that has not reached its cast frame (a windup child's line, Arcane Shift's windup, the barrage's windup: not
/// <see cref="SharedProjectileSnapshot.Launched"/>) and the missiles in the air (a launched line, the homing bolt). A cast the caster's death
/// cancelled stays in the queue until its next step and does not count.
/// </summary>
internal static class EzrealFlights
{
    /// <summary>Every live queue entry of the hero.</summary>
    public static SharedProjectileSnapshot[] Of(SpellSystem spells, Entity hero) =>
        spells.SharedProjectiles.Where(projectile => projectile is not (BbSpellProjectile { Cancelled: true } or BbLineMissile { Cancelled: true }))
            .Select(projectile => projectile.Snapshot).Where(item => item.CasterNetId == hero.NetId).ToArray();

    /// <summary>A cast whose cast frame (or whose child's) has not been reached: the client is in the uncancellable windup.</summary>
    public static bool WindingUp(SpellSystem spells, Entity hero, int? slot = null) =>
        Of(spells, hero).Any(item => !item.Launched && (slot is null || item.Slot == slot));

    /// <summary>The missile id a cast announced: a ground line names it in its cast before the line exists.</summary>
    public static uint MissileId(SharedProjectileSnapshot item) => item.MissileNetId != 0 ? item.MissileNetId : item.CastInfo?.MissileNetId ?? 0;

    /// <summary>The missiles in the air: launched entries that carry a missile id.</summary>
    public static SharedProjectileSnapshot[] InFlight(SpellSystem spells, Entity hero) =>
        Of(spells, hero).Where(item => item.Launched && item.MissileNetId != 0).ToArray();
}
