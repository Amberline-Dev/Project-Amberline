namespace Amberline.Core;

/// <summary>Damage categories accepted by the combat pipeline.</summary>
public enum DamageType : byte
{
    Physical,
    Magic,
    True,
}

/// <summary>
/// Source categories used by spell and item damage. A missing value is
/// meaningful and is represented by a nullable enum at call sites.
/// </summary>
public enum DamageSourceKind : byte
{
    Basic,
    Spell,
    SpellAoe,
    SpellPersist,
    Internal,
    Proc,
    WormAttack,
    Raw,
    InternalRaw,
}

public static class DamageTypeExtensions
{
    public static DamageType Parse(string value) => value switch
    {
        "physical" => DamageType.Physical,
        "magic" => DamageType.Magic,
        "true" => DamageType.True,
        _ => throw new FormatException($"unknown damage type '{value}'"),
    };

    public static DamageSourceKind? ParseSourceKind(string? value) => value switch
    {
        null => null,
        "basic" => DamageSourceKind.Basic,
        "spell" => DamageSourceKind.Spell,
        "spell_aoe" => DamageSourceKind.SpellAoe,
        "spell_persist" => DamageSourceKind.SpellPersist,
        "internal" => DamageSourceKind.Internal,
        "proc" => DamageSourceKind.Proc,
        "worm_attack" => DamageSourceKind.WormAttack,
        "raw" => DamageSourceKind.Raw,
        "internal_raw" => DamageSourceKind.InternalRaw,
        _ => throw new FormatException($"unknown damage source kind '{value}'"),
    };
}
