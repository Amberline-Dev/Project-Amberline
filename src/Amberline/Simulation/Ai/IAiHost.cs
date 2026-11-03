using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// One match's AI host: the engine-facing side of every unit's script instance. The engine
/// raises events here and the host dispatches them into the unit's callbacks; a unit with no
/// instance is not driven at all.
/// This is the seam a differential oracle plugs into: the hand-written turret machine kept in
/// the checks implements the same surface so two host implementations can be run side by side.
/// </summary>
public interface IAiHost
{
    /// <summary>Whether this host drives the unit (it has a script instance for it).</summary>
    bool Drives(Entity unit);

    /// <summary>The per-tick pass: the engine's target-lost test, then the unit's due timers.</summary>
    void StepUnit(Entity unit, double now, double dt);

    /// <summary><c>OnTargetLost</c>: the retained target died, became untargetable or changed side.</summary>
    void OnTargetLost(Entity unit);

    /// <summary>
    /// <c>OnReachedDestinationForGoingToLastLocation</c>: the walk to a lost target's last known
    /// position arrived and the target never came back (minionLib proto 0.12, Hero.luaobj's
    /// TimerDistanceScan).
    /// </summary>
    void OnReachedDestinationForGoingToLastLocation(Entity unit);

    /// <summary><c>OnCallForHelp</c>: an allied unit was attacked near this unit (a camp-mate, for a jungle camp).</summary>
    void OnCallForHelp(Entity unit, Entity aggressor);

    /// <summary><c>OnTakeDamage</c>: this unit was hit by <paramref name="attacker"/> (Leashed.luaobj).</summary>
    void OnTakeDamage(Entity unit, Entity attacker);

    /// <summary><c>OnTauntBegin</c> / <c>OnTauntEnd</c>.</summary>
    void OnTauntBegin(Entity unit);
    void OnTauntEnd(Entity unit);

    /// <summary><c>OnFearBegin</c> / <c>OnFearEnd</c> (Leashed.luaobj).</summary>
    void OnFearBegin(Entity unit);
    void OnFearEnd(Entity unit);

    /// <summary><c>OnCanMove</c> / <c>OnCanAttack</c>: control released.</summary>
    void OnCanMove(Entity unit);
    void OnCanAttack(Entity unit);

    /// <summary>
    /// <c>HaltAI</c>: stop every timer and the auto-attack and enter <c>AI_HALTED</c>. The
    /// shipped scripts export it and nothing recovered calls it, so this is an API, not a hook
    /// (the same call <c>MinionAiSystem.HaltAI</c> exposes for lane minions).
    /// </summary>
    void HaltAI(Entity unit);
}
