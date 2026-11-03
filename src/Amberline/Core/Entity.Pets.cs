using Amberline.Simulation.Pets;

namespace Amberline.Core;

public sealed partial class Entity
{
    /// <summary>Typed owner-local AI state for Shaco/Yorick pets.</summary>
    public PetState? PetState { get; set; }
}
