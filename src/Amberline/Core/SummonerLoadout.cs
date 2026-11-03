namespace Amberline.Core;

/// <summary>
/// Patch-126 canonical summoner names shared by roster validation, entity
/// state, loading packets, and the runtime spell dispatcher.
/// </summary>
public static class SummonerLoadout
{
    public const string DefaultFirst = "SummonerFlash";
    public const string DefaultSecond = "SummonerDot";

    public static readonly IReadOnlyList<string> CanonicalNames =
    [
        "SummonerFlash", "SummonerDot", "SummonerHeal", "SummonerHaste",
        "SummonerExhaust", "SummonerSmite", "SummonerBoost",
        "SummonerClairvoyance", "SummonerMana", "SummonerTeleport",
        "SummonerRevive", "SummonerFortify", "SummonerRally",
    ];

    /// <summary>
    /// Batch 455: the Dominion-only summoners (Crystal Scar, mode ODIN; their inibins and luaobj ship under DATA/Spells): Garrison, and
    /// Batch 500: Promote. NOT RETAIL: the Summoner's Rift lists never offer them, so the Platform lets them onto map 8 only
    /// (AirGameDtos.SpellNamesFor).
    /// </summary>
    public static readonly IReadOnlyList<string> DominionNames = ["SummonerOdinGarrison", "SummonerOdinPromote"];

    public static readonly IReadOnlyList<string> DominionLauncherNames = ["Garrison", "Promote"];

    public static readonly IReadOnlyList<string> LauncherNames =
    [
        "Flash", "Ignite", "Heal", "Ghost", "Exhaust", "Smite", "Cleanse",
        "Clairvoyance", "Clarity", "Teleport", "Revive", "Fortify", "Rally",
    ];

    /// <summary>
    /// The script name of a summoner spell this loadout knows ("Fortify" and "SummonerFortify" both answer
    /// "SummonerFortify"), or null when the name is not a summoner spell at all.
    /// </summary>
    public static string? TryCanonicalFromDisplay(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        for (var index = 0; index < LauncherNames.Count; index++)
            if (string.Equals(value.Trim(), LauncherNames[index], StringComparison.OrdinalIgnoreCase))
                return CanonicalNames[index];
        for (var index = 0; index < CanonicalNames.Count; index++)
            if (string.Equals(value.Trim(), CanonicalNames[index], StringComparison.OrdinalIgnoreCase))
                return CanonicalNames[index];
        for (var index = 0; index < DominionNames.Count; index++)
            if (string.Equals(value.Trim(), DominionNames[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Trim(), DominionLauncherNames[index], StringComparison.OrdinalIgnoreCase))
                return DominionNames[index];
        return null;
    }

    public static string CanonicalFromDisplay(string value) =>
        TryCanonicalFromDisplay(value) ?? throw new ArgumentException($"Unknown summoner spell '{value}'.", nameof(value));

    public static string DisplayName(string value)
    {
        var canonical = CanonicalFromDisplay(value);
        var index = Array.IndexOf(CanonicalNames.ToArray(), canonical);
        if (index < 0) return DominionLauncherNames[Array.IndexOf(DominionNames.ToArray(), canonical)];
        return LauncherNames[index];
    }

    public static (string First, string Second) ValidatePair(string first, string second)
    {
        var canonicalFirst = CanonicalFromDisplay(first);
        var canonicalSecond = CanonicalFromDisplay(second);
        if (string.Equals(canonicalFirst, canonicalSecond, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A player must choose two different summoner spells.");
        return (canonicalFirst, canonicalSecond);
    }
}
