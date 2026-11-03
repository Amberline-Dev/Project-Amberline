using System.Globalization;
using System.Text.Json;

namespace Amberline.Platform;

/// <summary>One discounted product of a sale: the catalog id, the name its source used, and the prices the source printed.</summary>
public sealed record PlatformSaleItem(string Id, string Name, int? Rp, int? SalePrice);

/// <summary>
/// One sale of the calendar: <see cref="Percent"/> off the RP price of <see cref="Items"/> from <see cref="Start"/> through
/// <see cref="End"/>, both Pacific-time calendar days of one year and both inclusive. <see cref="Source"/> is where the entry was
/// read from and <see cref="Captured"/> its timestamp. <see cref="DatesNote"/> is set (and begins with <c>Reconstruction:</c>)
/// when a date is inferred rather than printed.
/// </summary>
public sealed record PlatformSaleEntry(DateOnly Start, DateOnly End, string Kind, int Percent, DateOnly Announced,
    string Source, DateTimeOffset Captured, string? Event, string? DatesNote, IReadOnlyList<PlatformSaleItem> Items);

/// <summary>An entry of the calendar that is live today, with its dates moved into the current year.</summary>
public sealed record PlatformActiveSale(PlatformSaleEntry Entry, DateOnly Starts, DateOnly Ends);

/// <summary>
/// A limited-edition rune window: the runes in <see cref="RuneIds"/> are store products only from <see cref="Start"/> through
/// <see cref="End"/>, both Pacific-time calendar days of one year and both inclusive. <see cref="Source"/> is where the window was
/// read from and <see cref="Captured"/> its timestamp. <see cref="DatesNote"/> begins with <c>Reconstruction:</c> when a date is
/// inferred rather than printed. <see cref="IpPrices"/> are the IP prices (rune id to price) its source printed, and
/// <see cref="PriceNote"/> the price note of those runes; a rune without an entry sells at its tier's default price.
/// </summary>
public sealed record PlatformRuneWindow(string Event, DateOnly Start, DateOnly End, DateOnly Announced, string Source,
    DateTimeOffset Captured, string? DatesNote, IReadOnlyList<int> RuneIds, IReadOnlyDictionary<int, int> IpPrices,
    string? PriceNote = null);

/// <summary>
/// NOT RETAIL: the sale calendar is operator content (<see cref="PlatformContent"/>, <c>store/sales.json</c>, owner decision of
/// 1 November 2026); without it nothing is ever on sale and no windowed rune is sold. Retail ran each sale once; this server runs
/// for years, so the calendar loops yearly: today's month and day (Pacific time) are looked up in its windows, and outside every
/// window nothing is on sale. The price a sale charges is always computed here from the date, never from a request.
/// </summary>
public sealed class PlatformStoreCalendar
{
    private static readonly Lazy<PlatformStoreCalendar> Loaded = new(() =>
        PlatformContent.Read("store/sales.json") is { } json ? Parse(json) : new PlatformStoreCalendar([]), LazyThreadSafetyMode.PublicationOnly);

    private static readonly Lazy<TimeZoneInfo> Pacific = new(() =>
    {
        // The calendar's days are Pacific Time days. If the host has no tz database, a fixed UTC-8 keeps the day boundary within an hour.
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("PT", TimeSpan.FromHours(-8), "Pacific Time", "Pacific Time");
        }
    });

    public static PlatformStoreCalendar Default => Loaded.Value;

    public PlatformStoreCalendar(IReadOnlyList<PlatformSaleEntry> entries, IReadOnlyList<PlatformRuneWindow>? runeWindows = null)
    {
        foreach (var entry in entries)
        {
            // A window must stay inside one calendar year so that month and day alone place it.
            if (entry.Start.Year != entry.End.Year || entry.End < entry.Start)
                throw new InvalidDataException($"Sale window {entry.Start:O}..{entry.End:O} is not inside one year.");
            if (entry.Percent is < 1 or > 99 || entry.Items.Count == 0)
                throw new InvalidDataException("A sale needs a percentage of 1-99 and at least one item.");
        }
        foreach (var window in runeWindows ?? [])
        {
            if (window.Start.Year != window.End.Year || window.End < window.Start)
                throw new InvalidDataException($"Rune window {window.Start:O}..{window.End:O} is not inside one year.");
            if (window.RuneIds.Count == 0 || window.RuneIds.Any(id => id <= 0))
                throw new InvalidDataException("A rune window needs at least one positive rune id.");
            if (window.IpPrices.Any(price => price.Value <= 0 || !window.RuneIds.Contains(price.Key)))
                throw new InvalidDataException("A rune window price needs a positive price for one of its runes.");
        }
        Entries = entries;
        RuneWindows = runeWindows ?? [];
        WindowRuneIds = RuneWindows.SelectMany(window => window.RuneIds).ToHashSet();
        _windowRuneIp = RuneWindows.SelectMany(window => window.IpPrices).GroupBy(price => price.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);
    }

    private readonly Dictionary<int, int> _windowRuneIp;

    /// <summary>The IP price a window lists for a windowed rune, if any (otherwise the tier's default price applies).</summary>
    public int? WindowRuneIp(int runeId) => _windowRuneIp.TryGetValue(runeId, out var ip) ? ip : null;

    /// <summary>The price note of the window that prices the rune.</summary>
    public string? WindowRuneNote(int runeId) => RuneWindows.FirstOrDefault(window => window.IpPrices.ContainsKey(runeId))?.PriceNote;

    public IReadOnlyList<PlatformSaleEntry> Entries { get; }

    /// <summary>The limited-edition rune windows (empty when the data has none).</summary>
    public IReadOnlyList<PlatformRuneWindow> RuneWindows { get; }

    /// <summary>
    /// Every rune that is sold only inside a window. These are the only event runes the store ever offers; the other event and
    /// promotional runes (8001-8034, 10001-10002) are never products.
    /// </summary>
    public IReadOnlySet<int> WindowRuneIds { get; }

    public static PlatformStoreCalendar Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new List<PlatformSaleEntry>();
        foreach (var sale in document.RootElement.GetProperty("sales").EnumerateArray())
        {
            var items = sale.GetProperty("items").EnumerateArray().Select(item => new PlatformSaleItem(
                item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!,
                item.TryGetProperty("rp", out var rp) ? rp.GetInt32() : null,
                item.TryGetProperty("salePrice", out var price) ? price.GetInt32() : null)).ToArray();
            entries.Add(new PlatformSaleEntry(
                DateOnly.ParseExact(sale.GetProperty("start").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(sale.GetProperty("end").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                sale.GetProperty("kind").GetString()!, sale.GetProperty("percent").GetInt32(),
                DateOnly.ParseExact(sale.GetProperty("announced").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                sale.GetProperty("source").GetString()!,
                DateTimeOffset.Parse(sale.GetProperty("captured").GetString()!, CultureInfo.InvariantCulture),
                sale.TryGetProperty("event", out var eventName) ? eventName.GetString() : null,
                sale.TryGetProperty("datesNote", out var note) ? note.GetString() : null, items));
        }
        var windows = new List<PlatformRuneWindow>();
        if (document.RootElement.TryGetProperty("runeWindows", out var runeWindows))
            foreach (var window in runeWindows.EnumerateArray())
                windows.Add(new PlatformRuneWindow(window.GetProperty("event").GetString()!,
                    DateOnly.ParseExact(window.GetProperty("start").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(window.GetProperty("end").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(window.GetProperty("announced").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    window.GetProperty("source").GetString()!,
                    DateTimeOffset.Parse(window.GetProperty("captured").GetString()!, CultureInfo.InvariantCulture),
                    window.TryGetProperty("datesNote", out var windowNote) ? windowNote.GetString() : null,
                    window.GetProperty("runes").EnumerateArray().Select(rune => rune.GetProperty("id").GetInt32()).ToArray(),
                    window.GetProperty("runes").EnumerateArray().Where(rune => rune.TryGetProperty("ip", out _))
                        .ToDictionary(rune => rune.GetProperty("id").GetInt32(), rune => rune.GetProperty("ip").GetInt32()),
                    window.TryGetProperty("priceNote", out var priceNote) ? priceNote.GetString() : null));
        return new PlatformStoreCalendar(entries, windows);
    }

    /// <summary>The Pacific-time calendar day that <paramref name="now"/> falls on.</summary>
    public static DateOnly PacificDay(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Pacific.Value);
        return new DateOnly(local.Year, local.Month, local.Day);
    }

    /// <summary>The sales whose window contains today's month and day, with their dates in the current year.</summary>
    public IReadOnlyList<PlatformActiveSale> ActiveAt(DateTimeOffset now)
    {
        var today = PacificDay(now);
        var key = today.Month * 100 + today.Day;
        return Entries
            .Where(entry => entry.Start.Month * 100 + entry.Start.Day <= key && key <= entry.End.Month * 100 + entry.End.Day)
            .Select(entry => new PlatformActiveSale(entry,
                new DateOnly(today.Year, entry.Start.Month, entry.Start.Day),
                new DateOnly(today.Year, entry.End.Month, entry.End.Day)))
            .ToArray();
    }

    /// <summary>
    /// The windowed runes that are on sale at <paramref name="now"/>: those of every window whose 2011 month and day range contains
    /// today's Pacific-time month and day (both ends inclusive; the windows never cross a year, so month and day alone place them).
    /// </summary>
    public HashSet<int> OpenRuneIdsAt(DateTimeOffset now)
    {
        var today = PacificDay(now);
        return RuneWindows.Where(window => WindowOpenOn(window, today)).SelectMany(window => window.RuneIds).ToHashSet();
    }

    /// <summary>The Harrowing rune window's Pacific day test; the same window decides the rift below and the runes above.</summary>
    private static bool WindowOpenOn(PlatformRuneWindow window, DateOnly today)
    {
        var key = today.Month * 100 + today.Day;
        return window.Start.Month * 100 + window.Start.Day <= key && key <= window.End.Month * 100 + window.End.Day;
    }

    /// <summary>
    /// The maps a custom game may be created on, at all times: Summoner's Rift and Summoner's Rift (Autumn), LEVELS/Map2 (fontconfig
    /// game_map_displayname_2), the Harrowing rift, Twisted Treeline (LEVELS/Map4) and The Crystal Scar (LEVELS/Map8, Dominion). NOT RETAIL: the Autumn map is always
    /// selectable (owner decision, 28 October 2026: "make the autumn harrowing map always selectible"). NOT RETAIL: Dominion is for custom
    /// games only, with no bots at first (owner decision, 28 October 2026). Reconstruction: retail offered the Autumn map during its
    /// Harrowing event, the window of <see cref="OpenRuneIdsAt"/>. Each queue plays its own map (<see cref="PlatformGames.Queues"/>: Summoner's
    /// Rift, and Twisted Treeline in queue 8 from batch 571), so only custom games pick a map. Twisted Treeline joined custom games in batch 557
    /// (the overnight plan of 1 November 2026, TT-7). Reconstruction: which games retail offered it in this patch is not recovered from the
    /// shipped files; the stock client lists it (MapGeneratedData.as:32) and its play menu has a 3v3 queue (8) for it.
    /// </summary>
    public static IReadOnlyList<int> OpenMapIds { get; } = [NormalRiftMapId, HarrowingRiftMapId, TwistedTreelineMapId, CrystalScarMapId];

    /// <summary>Summoner's Rift (LEVELS/Map1), the only map before the Harrowing rift.</summary>
    public const int NormalRiftMapId = Amberline.Core.GameMaps.Normal;

    /// <summary>Summoner's Rift (Autumn), LEVELS/Map2: the Harrowing rift.</summary>
    public const int HarrowingRiftMapId = Amberline.Core.GameMaps.Harrowing;

    /// <summary>Twisted Treeline, LEVELS/Map4: custom games (batch 557) and the 3v3 queue (batch 571), CLASSIC like the others.</summary>
    public const int TwistedTreelineMapId = Amberline.Core.GameMaps.TwistedTreeline;

    /// <summary>
    /// The most player slots a custom game on <paramref name="mapId"/> holds, bots included: the stock client's own map sizes
    /// (MapGeneratedData.as:30-33, totalPlayers), six on Twisted Treeline and ten on the other maps.
    /// </summary>
    public static int MaxSlotsOf(int mapId) => mapId == TwistedTreelineMapId ? 6 : 10;

    /// <summary>The Crystal Scar, LEVELS/Map8: Dominion, created in custom games only, in the mode <c>ODIN</c> (batch 443).</summary>
    public const int CrystalScarMapId = Amberline.Core.GameMaps.CrystalScar;

    /// <summary>The discount of each product on sale at <paramref name="now"/>; the highest if two sales overlap.</summary>
    public Dictionary<string, int> PercentsAt(DateTimeOffset now)
    {
        var percents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sale in ActiveAt(now))
            foreach (var item in sale.Entry.Items)
                percents[item.Id] = Math.Max(percents.GetValueOrDefault(item.Id), sale.Entry.Percent);
        return percents;
    }

    /// <summary>The regular RP price a sale's source printed for a product, if any printed one.</summary>
    public Dictionary<string, int> PostedRegularPrices() => Entries.SelectMany(entry => entry.Items)
        .Where(item => item.Rp is not null).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().Rp!.Value, StringComparer.OrdinalIgnoreCase);
}
