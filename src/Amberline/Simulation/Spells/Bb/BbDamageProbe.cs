using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// A run of the part of an attack-override spell's <c>TargetExecute</c> that comes before its own <c>DAMAGESOURCE_ATTACK</c> hit
/// (<see cref="BbHookRouter.OnAttackOverrideDamage"/>): the run stops at that hit and keeps the amount the script computed for it. A dry run
/// (the default): anything before the hit that does more than read and compute (a native, a buff or stat change) aborts the run, so the probe
/// never has an effect. A live run (<see cref="Live"/>, made as the swing lands): the effects before the hit happen for real, in the script's order,
/// before the engine deals the hit, and the part after the hit is run later from where this run stopped (<see cref="Path"/>, <see cref="Scope"/>).
/// </summary>
internal sealed class BbDamageProbe
{
    /// <summary>
    /// The prefix of the script may have effects: they run for real (a buff the hit has to see, such as Udyr's <c>GlobalDrainMana</c>, or a particle,
    /// or a fury cost) and the script is resumed after the hit. Only for scripts whose blocks are all conditionals, so that a path can be resumed.
    /// </summary>
    public bool Live { get; init; }
    /// <summary>A live run did something before the hit that must not happen twice.</summary>
    public bool EffectsRan { get; set; }
    /// <summary>The block path of the primary hit in the script, 1-based from the hook's list down (<c>.3.2</c> is the second sub-block of the third block).</summary>
    public IReadOnlyList<int>? Path { get; set; }
    /// <summary>The scope the run ended in, with every variable the prefix wrote (kept for the resumed run).</summary>
    public BbScope? Scope { get; set; }
    /// <summary>The raw amount of the script's primary attack hit, before mitigation; null when the run never reached it.</summary>
    public double? Amount { get; set; }
    /// <summary><c>IgnoreDamageCrit</c> and <c>IgnoreDamageIncreaseMods</c> of that hit's call (Takedown passes both on its empowered hit).</summary>
    public bool IgnoreCrit { get; set; }
    public bool IgnoreIncreaseMods { get; set; }
    /// <summary>
    /// True for the run made as the hit is about to land: a <c>BBBreakSpellShields</c> before the primary hit then pops the shield for real,
    /// so the popped shield zeroes this hit as the script's own order has it (break, then damage). The run made before the swing is rolled
    /// only reads.
    /// </summary>
    public bool PopsShields { get; init; }
}
