using System.Globalization;
using System.Text.Json;

namespace Amberline.Platform;

/// <summary>
/// The source a section of the price list names: its address, the UTC time it was captured, a note and the short <see cref="Label"/>
/// that the store keeps as the price note of the products the section prices.
/// </summary>
public sealed record PlatformPriceSource(string Id, string Url, DateTimeOffset Captured, string Note, string? Label = null);

/// <summary>
/// The IP price of a regular rune. <see cref="Basis"/> is null when the source prints the price of this very rune; otherwise it is a
/// <c>Reconstruction:</c> text saying which printed price it was inferred from.
/// </summary>
public sealed record PlatformRunePrice(int Ip, string? Basis);

/// <summary>One retail boost pack: <see cref="Kind"/> "ip" or "xp", <see cref="Unit"/> "days" or "wins", and its RP price.</summary>
public sealed record PlatformBoostPack(string Kind, string Unit, int Amount, int Rp);

/// <summary>
/// The store's price list: champions (IP and RP), skins (RP; a skin that is not listed is not for sale unless the list has a default),
/// regular runes (IP), boost packs (RP), the extra rune page and the bundles. NOT RETAIL: the stock client carries no prices, so the
/// list is operator content (<see cref="PlatformContent"/>, <c>store/prices.json</c>, owner decision of 1 November 2026); without it
/// the Platform prices everything by the neutral <see cref="NeutralJson"/>, one price per kind. The catalog derives every price from
/// here, so the server alone decides what a purchase charges.
/// </summary>
public sealed class PlatformStorePrices
{
    private static readonly Lazy<PlatformStorePrices> Loaded = new(() => Parse(PlatformContent.Read("store/prices.json") ?? NeutralJson), LazyThreadSafetyMode.PublicationOnly);

    public static PlatformStorePrices Default => Loaded.Value;

    /// <summary>
    /// The neutral built-in list: every champion 1000 IP or 500 RP, every skin 500 RP, every bundle 2000 RP, a rune page 1000 IP or
    /// 500 RP, boosts 100 RP a day and 300 RP for ten wins; runes fall to their tier's default (see PlatformStoreCatalog.RuneProduct).
    /// </summary>
    public const string NeutralJson = """
        {"sources":[],"champions":[],"skins":[],"runes":[],"bundles":[],"runePage":{"ip":1000,"rp":500},
         "boosts":[{"kind":"ip","unit":"days","amount":1,"rp":100},{"kind":"xp","unit":"days","amount":1,"rp":100},
                   {"kind":"ip","unit":"wins","amount":10,"rp":300},{"kind":"xp","unit":"wins","amount":10,"rp":300}],
         "defaults":{"championIp":1000,"championRp":500,"skinRp":500,"bundleRp":2000}}
        """;

    private (int ChampionIp, int ChampionRp, int SkinRp, int BundleRp)? _defaults;
    private readonly Dictionary<int, (int Ip, int Rp)> _champions = [];
    private readonly Dictionary<int, int> _skins = [];
    private readonly Dictionary<int, PlatformRunePrice> _runes = [];
    private readonly Dictionary<string, int> _bundles = new(StringComparer.OrdinalIgnoreCase);

    private PlatformStorePrices(IReadOnlyList<PlatformPriceSource> sources, IReadOnlyList<PlatformBoostPack> boosts, int runePageIp, int runePageRp)
    {
        Sources = sources;
        Boosts = boosts;
        RunePageIp = runePageIp;
        RunePageRp = runePageRp;
    }

    public IReadOnlyList<PlatformPriceSource> Sources { get; }

    public IReadOnlyList<PlatformBoostPack> Boosts { get; }

    /// <summary>The additional rune page's IP and RP price.</summary>
    public int RunePageIp { get; }

    public int RunePageRp { get; }

    public IReadOnlyCollection<int> ChampionIds => _champions.Keys;

    /// <summary>Global skin ids (champion id x 1000 + index) the list prices.</summary>
    public IReadOnlyCollection<int> SkinIds => _skins.Keys;

    public IReadOnlyCollection<int> RuneIds => _runes.Keys;

    /// <summary>The label of a section's source (<c>sources[].label</c>), the price note of what it prices; null in a list without one.</summary>
    public string? Label(string sourceId) => Sources.FirstOrDefault(source => source.Id == sourceId)?.Label;

    public int? ChampionIp(int championId) => _champions.TryGetValue(championId, out var price) ? price.Ip : _defaults?.ChampionIp;

    public int? ChampionRp(int championId) => _champions.TryGetValue(championId, out var price) ? price.Rp : _defaults?.ChampionRp;

    /// <summary>The RP price of a skin, or null when the list neither prices it nor has a default: then the skin is not for sale.</summary>
    public int? SkinRp(int globalSkinId) => _skins.TryGetValue(globalSkinId, out var rp) ? rp : _defaults?.SkinRp;

    public PlatformRunePrice? Rune(int runeId) => _runes.GetValueOrDefault(runeId);

    public int? BundleRp(string bundleId) => _bundles.TryGetValue(bundleId, out var rp) ? rp : _defaults?.BundleRp;

    /// <summary>
    /// The RP price of an IP or XP boost of <paramref name="amount"/> days or wins. A pack of the list costs its price
    /// (<paramref name="retailPack"/>). NOT RETAIL: any other amount a host configures costs the per-unit price of the largest pack
    /// not above it (the smallest pack for a smaller amount) times the amount.
    /// </summary>
    public int? BoostRp(bool ip, bool timed, int amount, out bool retailPack)
    {
        retailPack = false;
        var kind = ip ? "ip" : "xp";
        var unit = timed ? "days" : "wins";
        var packs = Boosts.Where(pack => pack.Kind == kind && pack.Unit == unit).OrderBy(pack => pack.Amount).ToArray();
        if (packs.Length == 0 || amount < 1) return null;
        if (packs.FirstOrDefault(pack => pack.Amount == amount) is { } exact)
        {
            retailPack = true;
            return exact.Rp;
        }
        var basis = packs.LastOrDefault(pack => pack.Amount <= amount) ?? packs[0];
        return (int)Math.Round((double)basis.Rp * amount / basis.Amount, MidpointRounding.AwayFromZero);
    }

    public static PlatformStorePrices Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var sources = root.GetProperty("sources").EnumerateArray().Select(source => new PlatformPriceSource(
            source.GetProperty("id").GetString()!, source.GetProperty("url").GetString()!,
            DateTimeOffset.Parse(source.GetProperty("captured").GetString()!, CultureInfo.InvariantCulture),
            source.GetProperty("note").GetString()!,
            source.TryGetProperty("label", out var label) ? label.GetString() : null)).ToArray();
        var boosts = root.GetProperty("boosts").EnumerateArray().Select(pack => new PlatformBoostPack(
            pack.GetProperty("kind").GetString()!, pack.GetProperty("unit").GetString()!,
            pack.GetProperty("amount").GetInt32(), pack.GetProperty("rp").GetInt32())).ToArray();
        var page = root.GetProperty("runePage");
        var prices = new PlatformStorePrices(sources, boosts, page.GetProperty("ip").GetInt32(), page.GetProperty("rp").GetInt32());
        foreach (var champion in root.GetProperty("champions").EnumerateArray())
            prices._champions.Add(champion.GetProperty("id").GetInt32(), (champion.GetProperty("ip").GetInt32(), champion.GetProperty("rp").GetInt32()));
        foreach (var skin in root.GetProperty("skins").EnumerateArray())
            prices._skins.Add(skin.GetProperty("id").GetInt32(), skin.GetProperty("rp").GetInt32());
        foreach (var rune in root.GetProperty("runes").EnumerateArray())
        {
            var basis = rune.GetProperty("basis").GetString()!;
            prices._runes.Add(rune.GetProperty("id").GetInt32(),
                new PlatformRunePrice(rune.GetProperty("ip").GetInt32(), basis == "wiki" ? null : basis));
        }
        foreach (var bundle in root.GetProperty("bundles").EnumerateArray())
            prices._bundles.Add(bundle.GetProperty("id").GetString()!, bundle.GetProperty("rp").GetInt32());
        // Optional: the price of anything a section does not list.
        if (root.TryGetProperty("defaults", out var defaults))
            prices._defaults = (defaults.GetProperty("championIp").GetInt32(), defaults.GetProperty("championRp").GetInt32(),
                defaults.GetProperty("skinRp").GetInt32(), defaults.GetProperty("bundleRp").GetInt32());

        var fallback = prices._defaults is { } d ? new[] { d.ChampionIp, d.ChampionRp, d.SkinRp, d.BundleRp } : [];
        var all = fallback.Concat(prices._champions.Values.SelectMany(price => new[] { price.Ip, price.Rp }))
            .Concat(prices._skins.Values).Concat(prices._runes.Values.Select(price => price.Ip))
            .Concat(prices._bundles.Values).Concat(boosts.Select(pack => pack.Rp)).Append(prices.RunePageIp).Append(prices.RunePageRp);
        if (all.Any(price => price <= 0) || boosts.Any(pack => pack.Amount < 1))
            throw new InvalidDataException("A store price must be positive.");
        return prices;
    }
}
