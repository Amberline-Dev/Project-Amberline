using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The spell script whose hook is running on this thread. A buff hook that fires while a spell resolves
/// (<c>BuffOnBeingSpellHit</c> of a shield the spell hits) reads that spell's flags as <c>SpellVars</c> and whether it is an attack
/// override as <c>GetIsAttackOverride</c>; the simulation is single-threaded, so one slot per thread is exact.
/// Reconstruction: retail's <c>SpellVars</c> is the table of the spell script.
/// </summary>
internal static class BbSpellContext
{
    [ThreadStatic] private static BbScript? _current;
    [ThreadStatic] private static bool _isAttack;

    public static BbScript? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary><c>GetIsAttackOverride</c> of the running spell: an auto-attack override fired it (Ricochet, Righteous Fury).</summary>
    public static bool IsAttack
    {
        get => _isAttack;
        set => _isAttack = value;
    }

    /// <summary>
    /// What <c>BuffOnBeingSpellHit</c> of <c>SpellShield.luaobj</c> and <c>BansheesVeil.luaobj</c> decide for the running spell, for a hostile
    /// unit with a shield: no pop for an attack override; <c>DoesntBreakShields</c> false pops and true does not; without that flag the spell
    /// pops unless it sets <c>DoesntTriggerSpellCasts</c>. A hand-written module has no running script, so its hit pops (both flags unset).
    /// Only the gate's bare-veil fallback reads it (MatchGameplayHooks.ConsumeSpellShield); a scripted shield's own hook decides.
    /// </summary>
    public static bool BreaksShields =>
        !_isAttack && (_current?.Global("DoesntBreakShields") is BbValue.Bool breaks
            ? !breaks.Value
            : _current?.Global("DoesntTriggerSpellCasts") is not BbValue.Bool { Value: true });

    /// <summary>Copies the plain (boolean, number and text) globals of a spell script into a <c>SpellVars</c> table.</summary>
    public static void Fill(BbTable table, BbScript spell)
    {
        foreach (var global in spell.Globals)
            if (global.Value is BbValue.Bool or BbValue.Number or BbValue.Text)
                table[global.Key] = BbRuntime.ToRuntime(global.Value);
    }
}
