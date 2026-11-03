using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The pet services the BuildingBlocks spawn natives use (set by <c>ProductionMatchComposition</c> after the spell system is built);
    /// null in a graph that has none, where those natives raise an "unbound" error instead of spawning a parallel copy of a pet.
    /// </summary>
    internal BbPetServices? ScriptPets { get; set; }
}
