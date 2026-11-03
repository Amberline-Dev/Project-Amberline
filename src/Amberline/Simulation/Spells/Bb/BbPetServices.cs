using Amberline.Simulation.Pets;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The match's pet and minion services the spawn natives of the BuildingBlocks engine (<c>BBSpawnMinion</c>, <c>BBCloneUnitPet</c>)
/// create their units through: the same instances the shipped Shaco and Yorick scripts use, so a unit spawned by a script is a unit of the
/// same pet system (owner-death cascade, Pet.lua orders, ghoul and clone ticks).
/// </summary>
internal sealed record BbPetServices(SharedCloneService Clones, ShacoBoxService Boxes);
