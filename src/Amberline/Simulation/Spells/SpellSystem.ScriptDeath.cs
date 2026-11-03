using Amberline.Core;

namespace Amberline.Simulation.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// What the BuildingBlocks <c>BBForceDead</c> native runs for a living unit: ends the zombie life the unit is in (the emulator's only one is
    /// Yorick's Omen of Death temporary life, <c>MatchGameplayHooks.EndZombieLife</c>) and returns true, or returns false when the unit is in none. Set by
    /// <c>ProductionMatchComposition</c>; null in a graph that has no such service, where the native raises "not bound" instead of
    /// inventing a death.
    /// </summary>
    internal Func<Entity, bool>? EndZombieLife { get; set; }
}
