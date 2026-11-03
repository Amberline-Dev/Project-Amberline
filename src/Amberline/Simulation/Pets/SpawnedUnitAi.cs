namespace Amberline.Simulation.Pets;

/// <summary>
/// The AI scripts a <c>BBSpawnMinion</c> can give a unit the engine runs a controller for (W26c):
/// <c>UncontrollablePet.lua</c> (Malzahar's voidlings: Pet.lua without the owner's orders),
/// <c>Minion.lua</c> (Heimerdinger's turrets: Minion.luaobj without a lane),
/// <c>Aggro.lua</c> (Wukong's Nimbus clones: Aggro.luaobj) and <c>idle.lua</c>
/// (a ward, a wall piece, a blood drop: <c>Idle.luaobj</c>, which stands where it was put). The
/// pet scripts themselves (<see cref="Pet"/>, <see cref="YorickPHPet"/>) reach a unit through the
/// <c>BBSpawnPet</c>/<c>BBCloneUnitPet</c> default and the <c>BBSpawnMinion AiScript</c> field of
/// Yorick's ghouls (<see cref="PetHostScript"/>).
/// </summary>
public static class SpawnedUnitAi
{
    public const string UncontrollablePet = "UncontrollablePet.lua";
    public const string Minion = "Minion.lua";
    public const string Aggro = "Aggro.lua";
    /// <summary><c>Idle.luaobj</c>: <c>OnInit</c>'s <c>SetState(AI_HARDIDLE)</c> and an empty callback for every other hook.</summary>
    public const string Idle = "Idle.lua";
    /// <summary><c>BBSpawnPet</c>/<c>BBCloneUnitPet</c>'s default <c>AiScript</c>: Tibbers and every clone.</summary>
    public const string Pet = "Pet.lua";
    /// <summary>The <c>BBSpawnMinion AiScript</c> of Yorick's three ghouls.</summary>
    public const string YorickPHPet = "YorickPHPet.lua";

    /// <summary>Whether the engine runs a controller for the AI script of a spawned unit.</summary>
    public static bool Runs(string? script) => Fights(script) || IsIdle(script);

    /// <summary>
    /// Whether the unit's script takes a target and swings at it — the three whose hit path runs a
    /// character attack script (<c>BbHookRouter.RunUnitAttackScript</c>) and whose damage line
    /// credits an owner. Idle.luaobj takes no target, so a ward's hit runs nothing.
    /// </summary>
    public static bool Fights(string? script) => Canonical(script) is not null && !IsIdle(script);

    /// <summary>Whether the script is the one that stands where the unit was put.</summary>
    public static bool IsIdle(string? script) => string.Equals(script, Idle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pet family of a unit a <c>BBSpawnMinion</c> gives one of the two pet scripts the engine runs a pet host for
    /// (the reverse of <see cref="PetHostScript"/>): <c>UncontrollablePet.lua</c> is a voidling and <c>YorickPHPet.lua</c>
    /// is one of Yorick's ghouls. <see cref="Minion"/> and <see cref="Aggro"/> are the lane-less controller, not pets, and
    /// a unit of no script stands where it was put.
    /// </summary>
    public static CloneKind? PetFamilyOf(string? script) =>
        string.Equals(script, UncontrollablePet, StringComparison.OrdinalIgnoreCase) ? CloneKind.Voidling :
        string.Equals(script, YorickPHPet, StringComparison.OrdinalIgnoreCase) ? CloneKind.YorickGhoul : null;

    /// <summary>The fighting scripts the spawned-unit Lua host runs without a lane (<see cref="TibbersAiSystem.Host"/>).</summary>
    public static bool IsLaneLess(string? script) => script is Minion or Aggro;

    /// <summary>
    /// The shipped chunk the spawned-unit Lua AI host runs for this <c>AiScript</c>, as the host
    /// loads it (the file stem of <c>DATA/Scripts/&lt;name&gt;.luaobj</c>), or null when that host
    /// does not drive the unit: <c>idle.lua</c> is <c>Idle.luaobj</c>, <c>Aggro.lua</c> is
    /// <c>Aggro.luaobj</c> and <c>Minion.lua</c> is <c>Minion.luaobj</c> with the <c>minionLib.luaobj</c>
    /// it loads (Heimerdinger's turrets, batch 339: the lane minion's chunk, whose forward navigation a
    /// unit with no lane answers by standing). <see cref="UncontrollablePet"/> belongs to the pet host.
    /// </summary>
    public static string? HostScript(string? script) =>
        IsIdle(script) ? "Idle" :
        string.Equals(script, Aggro, StringComparison.OrdinalIgnoreCase) ? "Aggro" :
        string.Equals(script, Minion, StringComparison.OrdinalIgnoreCase) ? "Minion" : null;

    /// <summary>The script's name as the engine keeps it on the unit (the call spells it in any case).</summary>
    public static string? Canonical(string? script) =>
        string.Equals(script, UncontrollablePet, StringComparison.OrdinalIgnoreCase) ? UncontrollablePet :
        string.Equals(script, Minion, StringComparison.OrdinalIgnoreCase) ? Minion :
        string.Equals(script, Aggro, StringComparison.OrdinalIgnoreCase) ? Aggro :
        string.Equals(script, Idle, StringComparison.OrdinalIgnoreCase) ? Idle : null;

    /// <summary>
    /// The shipped pet script of a pet unit, as the AI host loads it (the file stem of
    /// <c>DATA/Scripts/&lt;name&gt;.luaobj</c>). The mapping is the unit's own spawn call:
    /// <c>BBSpawnPet</c> (Tibbers) and <c>BBCloneUnitPet</c> (Shaco's clone, Yorick's revenant, LeBlanc's image, Wukong's
    /// decoy, Mordekaiser's ghost) take the call's default <c>AiScript</c>, <c>Pet.lua</c>; <c>BBSpawnMinion</c> spells
    /// the field out, <c>UncontrollablePet.lua</c> for Malzahar's voidlings and <c>YorickPHPet.lua</c> for Yorick's
    /// ghouls. A unit with no <see cref="PetState"/> is no pet.
    /// </summary>
    public static string? PetHostScript(PetState? state) => state is null ? null : state.Kind switch
    {
        CloneKind.Voidling => "UncontrollablePet",
        CloneKind.YorickGhoul => "YorickPHPet",
        _ => "Pet",
    };
}
