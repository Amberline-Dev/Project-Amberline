using Amberline.Core;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Pets;

/// <summary>Scripted pet families that share the native Pet.lua controller.</summary>
public enum CloneKind
{
    ShacoClone,
    YorickRevenant,
    YorickGhoul,
    Tibbers,
    /// <summary>LeBlanc's Mirror Image (<c>BBCloneUnitPet Buff="LeblancMI"</c>, controller buff <c>LeblancMIFull</c>).</summary>
    LeblancImage,
    /// <summary>Wukong's decoy (<c>Buff="MonkeyKingDecoyDummy"</c>): a clone with no controller buff; its scripts stop it from moving or attacking.</summary>
    MonkeyKingDecoy,
    /// <summary>Mordekaiser's ghost (<c>Buff="MordekaiserCotgPetSlow"</c>, controller buff <c>MordekaiserCOTGPetBuff2</c>): a clone of a fallen enemy.</summary>
    MordekaiserGhost,
    /// <summary>A <c>UncontrollablePet.lua</c> unit (Malzahar's voidling): Pet.lua without the owner's orders, a minion on the wire.</summary>
    Voidling,
}

/// <summary>
/// The Pet.lua clones <see cref="SharedCloneService"/> builds: the controller buff each family's script adds right after
/// <c>BBCloneUnitPet</c> (its life is the clone's life); a family without one never acts.
/// </summary>
public static class CloneFamilies
{
    /// <summary>The controller buff of a family, or null for a clone that has none and never acts (the decoy).</summary>
    public static string? ControllerBuff(CloneKind kind) => kind switch
    {
        CloneKind.ShacoClone => "HallucinateFull",
        CloneKind.YorickRevenant => "YorickRAPetBuff2",
        CloneKind.LeblancImage => "LeblancMIFull",
        CloneKind.MordekaiserGhost => "MordekaiserCOTGPetBuff2",
        _ => null,
    };
}

/// <summary>
/// What a pet's unit carries of its own: which family it belongs to, who owns it and what it was
/// copied from, and the spawn metadata of the call that made it. Everything the pet decides is the
/// shipped script's (<see cref="PetAiService"/>), so there is no order or route state here any more.
/// </summary>
public sealed class PetState
{
    public PetState(CloneKind kind, uint ownerNetId)
    {
        Kind = kind;
        OwnerNetId = ownerNetId;
    }

    public CloneKind Kind { get; }
    public uint OwnerNetId { get; }
    public uint CloneSourceNetId { get; set; }
    public string SpawnBuffName { get; set; } = string.Empty;
    public double SpawnDuration { get; set; }
    public bool ShowMinimapIcon { get; set; }
    public double BornAt { get; set; }
    public int Rank { get; set; }
}

/// <summary>
/// Pet.lua's shared rules and the wire ids the 0x75 pet orders carry. The rule set itself is the
/// script's; what lives here is what the natives and the wire need: the owner-order ids. The return
/// radius behind <c>GetPetReturnRadius</c> is Map1's <c>ai_DefaultPetReturnRadius</c> (<c>PetLuaAiEngine</c>).
/// </summary>
public static class PetOrders
{
    /// <summary>ORDER_HOLD: AI_PET_HOLDPOSITION, attacks in range without moving.</summary>
    public const int OwnerHold = 1;
    /// <summary>ORDER_MOVETO: AI_PET_MOVE, follows the walking owner without acquiring.</summary>
    public const int OwnerMove = 2;
    /// <summary>ORDER_ATTACKTO: AI_PET_ATTACK on the owner's target, at any distance.</summary>
    public const int OwnerAttack = 3;
    /// <summary>ORDER_ATTACKMOVE: close to the owner while acquiring enemies.</summary>
    public const int OwnerAttackMove = 7;
    /// <summary>The longest a pet stays planted for a target that is just out of reach.</summary>
    public const double PlantHoldSeconds = 0.5;

    /// <summary>One client step of this pet: the 100 ms the stock client moves an AI unit by, at its speed (32.5 units at 325).</summary>
    public static double ClientHop(Entity pet) => Math.Max(0.0, pet.Speed) * MovementSystem.FixedTickSeconds;
}
