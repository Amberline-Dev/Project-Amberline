namespace Amberline.Data;

/// <summary>
/// The client's <c>PrimaryAbilityResourceType</c>, the kind of the one resource pool (<c>mMP</c> / <c>mMaxMP</c>) a character carries. The character
/// record names it in <c>Data*PARType</c>; the client's loader builds the string map "MP" 0, "Energy" 1, "Souls" 2, "Shield" 3,
/// "Other" 4 and the loader reads it with a map lookup, so a string that is not in the map (the shipped "Mana" among them, and an
/// absent key whose default is "MP") is 0. A character has exactly one pool: every script accessor that names another type finds nothing and does
/// nothing. See the "Resource pools" section of docs/scripting/building-blocks-engine.md.
/// </summary>
public enum PrimaryResourceType
{
    Mana = 0,
    Energy = 1,
    Souls = 2,
    Shield = 3,
    Other = 4,
}

/// <summary>What the pool kinds mean to the server beyond their number.</summary>
public static class PrimaryResourceTypes
{
    /// <summary>The client's string map; any other string is the default, 0 (<see cref="PrimaryResourceType.Mana"/>).</summary>
    public static PrimaryResourceType Parse(string? name) => name switch
    {
        "Energy" => PrimaryResourceType.Energy,
        "Souls" => PrimaryResourceType.Souls,
        "Shield" => PrimaryResourceType.Shield,
        "Other" => PrimaryResourceType.Other,
        _ => PrimaryResourceType.Mana,
    };

    /// <summary>
    /// The pools that start a life empty: the hero's reincarnation handler sets the pool to 0
    /// when the type is 3 or 4, so a Shield or Other pool (rage, heat, flow, Mordekaiser's shield) does not come back full; mana and energy keep the
    /// full refill of a respawn.
    /// </summary>
    public static bool StartsLifeEmpty(this PrimaryResourceType type) => type is PrimaryResourceType.Shield or PrimaryResourceType.Other;

    /// <summary>
    /// The pools the fountain and the passive regeneration refill. Reconstruction: the client's temple tick adds to the pool of any type (the amount is kept in a temporary),
    /// and the server's routine is not in the client; a rage, heat or shield pool that filled in the fountain
    /// would overheat Rumble and arm Tryndamere's fury, so only the pools that behave like a resource are refilled.
    /// </summary>
    public static bool RefillsAtFountain(this PrimaryResourceType type) =>
        type is PrimaryResourceType.Mana or PrimaryResourceType.Energy or PrimaryResourceType.Souls;

    /// <summary>
    /// The name the client's item and character stat keys put between "Flat" / "Percent" and "PoolMod" / "RegenMod":
    /// "MP", "Energy", "Souls", "Shield" and "Other". The per-level keys read <c>rFlat{name}ModPerLevel</c> and <c>rFlat{name}RegenModPerLevel</c>.
    /// </summary>
    public static string StatKeyName(this PrimaryResourceType type) => type switch
    {
        PrimaryResourceType.Energy => "Energy",
        PrimaryResourceType.Souls => "Souls",
        PrimaryResourceType.Shield => "Shield",
        PrimaryResourceType.Other => "Other",
        _ => "MP",
    };
}
