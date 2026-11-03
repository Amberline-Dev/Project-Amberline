using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Neutral;
using Amberline.Network.Sessions;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

// Batch 174 part 2: the Baron's rotation is CharScriptWorm's. Its action timers (ActionTimer, ActionTimer2, SweepTimer, WrathTimer,
// PropelTimer), WrathCooldown, WormRecoupDebuff, the WrathDamage markers and the PropelSpellCaster placemarkers are buffs the shipped
// scripts add and remove. These types are the test-side view the Worm checks read. They are not production API.

/// <summary>One of the Baron's shipped action timers a check starts (in a match the script's own roll picks it).</summary>
public enum NeutralWormAction
{
    Sweep,
    Wrath,
    Propel,
}

/// <summary>A pending wrath hit: the marked unit and the seconds left on its WrathDamage marker.</summary>
public readonly record struct NeutralWormPendingHit(uint TargetNetId, double Remaining);

/// <summary>A propel geyser: the placemarker's position and the seconds left on its PropelSpellCaster.</summary>
public readonly record struct NeutralWormPendingGeyser(double Remaining, Position2 Position);

/// <summary>What the Baron's script is doing now, read from its buffs. GrowthElapsed, RecoupElapsed and the wrath selection
/// counters have no buff of their own in the shipped scripts and read as zero.</summary>
public readonly record struct NeutralWormStateSnapshot(
    double GrowthElapsed,
    double RecoupElapsed,
    double RecoupBlock,
    string? Action,
    double ActionRemaining,
    double WrathCooldown,
    int WrathSelections,
    double WrathSelectRemaining,
    IReadOnlyList<NeutralWormPendingHit> PendingWrath,
    IReadOnlyList<NeutralWormPendingGeyser> Geysers);

public static class NeutralWormScriptView
{
    private static readonly string[] ActionTimers = { "ActionTimer", "ActionTimer2", "SweepTimer", "WrathTimer", "PropelTimer" };

    /// <summary>The shipped timer buff a check starts for an action.</summary>
    public static string TimerName(NeutralWormAction action) => action switch
    {
        NeutralWormAction.Sweep => "SweepTimer",
        NeutralWormAction.Wrath => "WrathTimer",
        _ => "PropelTimer",
    };

    /// <summary>
    /// Starts one of the Baron's shipped action timers: the timer buff is added and removed again, so its OnBuffDeactivate runs
    /// the action (the cast, the geysers, the wrath marks). The roll that picks the timer in a match is the script's own.
    /// </summary>
    public static bool Trigger(BbUnitCharacterModule scripts, BuffLifecycle buffs, Entity worm, NeutralWormAction action)
    {
        // AddShippedBuff makes the timer a live script buff (its hooks run); a plain AddBuff would have no script behind it.
        var timer = scripts.AddShippedBuff(worm, TimerName(action), BuffType.Internal, 2.0);
        return timer is not null && buffs.RemoveBuff(worm, timer.Slot);
    }

    /// <summary>
    /// One world step of a production runtime, in the order the match runs it: the clock, the spell system (the scripts'
    /// periodic actions and projectiles), each unit's buffs, then the neutral AI.
    /// </summary>
    public static void StepRuntime(MatchRuntime runtime, NeutralAiService ai, double dt)
    {
        runtime.World.Clock.Advance(dt);
        runtime.Spells.Tick(dt);
        foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, dt);
        ai.Tick(dt);
    }

    /// <summary>Queues one of the Baron's shipped action timers without running it: it acts when it expires after two seconds.</summary>
    public static BuffState? Queue(BbUnitCharacterModule scripts, Entity worm, NeutralWormAction action) =>
        scripts.AddShippedBuff(worm, TimerName(action), BuffType.Internal, 2.0);

    /// <summary>
    /// The snapshot now. A buff's <c>ExpiresAt</c> is the seconds it has left (BuffLifecycle.TickBuffs subtracts the step from it), so
    /// the remaining time is that value, not a difference with a clock.
    /// </summary>
    public static NeutralWormStateSnapshot Snapshot(WorldState world, Entity worm)
    {
        static double Remaining(Entity unit, string name) =>
            unit.Buffs.FirstOrDefault(buff => buff.Name == name) is { } buff ? Math.Max(0.0, buff.ExpiresAt) : 0.0;
        var timer = worm.Buffs.FirstOrDefault(buff => ActionTimers.Contains(buff.Name));
        var pendingWrath = world.Entities.Values
            .Where(unit => unit.Buffs.Any(buff => buff.Name == "WrathDamage"))
            .Select(unit => new NeutralWormPendingHit(unit.NetId, Remaining(unit, "WrathDamage")))
            .ToArray();
        var geysers = world.Entities.Values
            .Where(unit => unit.Buffs.Any(buff => buff.Name == "PropelSpellCaster"))
            .Select(unit => new NeutralWormPendingGeyser(Remaining(unit, "PropelSpellCaster"), unit.Position))
            .ToArray();
        return new NeutralWormStateSnapshot(
            GrowthElapsed: 0.0,
            RecoupElapsed: 0.0,
            RecoupBlock: Remaining(worm, "WormRecoupDebuff"),
            Action: timer?.Name,
            ActionRemaining: timer is null ? 0.0 : Math.Max(0.0, timer.ExpiresAt),
            WrathCooldown: Remaining(worm, "WrathCooldown"),
            WrathSelections: 0,
            WrathSelectRemaining: 0.0,
            PendingWrath: pendingWrath,
            Geysers: geysers);
    }
}
