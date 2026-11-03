using System.Collections.ObjectModel;
using Amberline.Data;

namespace Amberline.Core;

/// <summary>Validated immutable pre-match rune selection.</summary>
public sealed class RunePage
{
    public const int RedSlots = 9;
    public const int BlueSlots = 9;
    public const int YellowSlots = 9;
    public const int BlackSlots = 3;
    public const int TotalSlots = RedSlots + BlueSlots + YellowSlots + BlackSlots;

    public static RunePage Empty { get; } = new([], [], [], [],
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)),
        new int[TotalSlots]);

    private RunePage(
        IReadOnlyList<int> red,
        IReadOnlyList<int> blue,
        IReadOnlyList<int> yellow,
        IReadOnlyList<int> black,
        IReadOnlyDictionary<string, double> modifiers,
        int[] wireIds,
        bool botList = false)
    {
        IsBotList = botList;
        Red = red;
        Blue = blue;
        Yellow = yellow;
        Black = black;
        Modifiers = modifiers;
        WireIds = Array.AsReadOnly(wireIds);
    }

    public IReadOnlyList<int> Red { get; }
    public IReadOnlyList<int> Blue { get; }
    public IReadOnlyList<int> Yellow { get; }
    public IReadOnlyList<int> Black { get; }
    public IReadOnlyDictionary<string, double> Modifiers { get; }
    /// <summary>
    /// All 30 avatar positions, with zero for an unused slot. Slot order is the one the shipped
    /// Bot*Runes.inibin lists use for Rune1..Rune30 and the AIR rune-slot ids: marks (red) 1-9,
    /// seals (yellow) 10-18, glyphs (blue) 19-27, quintessences (black) 28-30.
    /// </summary>
    public IReadOnlyList<int> WireIds { get; }
    /// <summary>
    /// Built by <see cref="CreateBot"/> from a shipped <c>Bot*Runes.inibin</c> list, so it is not bound by the lobby page rules and
    /// must never be re-checked against them, whichever seat flag carries it (the headless match seats its bots as ordinary
    /// participants, see <c>SessionParticipantDefinition.Validate</c>).
    /// </summary>
    public bool IsBotList { get; }
    public bool IsEmpty => Red.Count == 0 && Blue.Count == 0 && Yellow.Count == 0 && Black.Count == 0;
    public IEnumerable<int> AllIds => Red.Concat(Yellow).Concat(Blue).Concat(Black);

    /// <summary>
    /// Returns the historical 1.0.0.126 account slot count for one rune
    /// color. One ordinary slot opens per account level in Mark/Seal/Glyph
    /// order; every tenth level opens a Quintessence slot instead.
    /// Reconstruction: the slot table (id, rune type, minLevel) is the retail platform's, sent to the stock AIR
    /// client as the summoner catalog's spellBookConfig, so no shipped file holds it. The client fixes only
    /// 30 slots (AvatarInfo carries 30 rune ids), the four types red 1, yellow 3,
    /// blue 5, black 7 (RuneType.as) and locked-slot art for minLevel 10, 20 and 30 alone
    /// (RuneTarget.as e_runeTargetDisabledLarge10/20/30), which is the quintessence schedule. The
    /// Mark, Seal, Glyph order per level rests on the historical guides cited in
    /// docs/items/runes-catalog-audit.md.
    /// </summary>
    public static int UnlockedSlots(int summonerLevel, RuneColor color)
    {
        ValidateSummonerLevel(summonerLevel);
        if (!Enum.IsDefined(typeof(RuneColor), color))
            throw new ArgumentOutOfRangeException(nameof(color), "unknown rune color");
        var count = 0;
        for (var level = 1; level <= summonerLevel; level++)
        {
            var unlockedColor = level % 10 == 0
                ? RuneColor.Black
                : (((level - 1) % 10) % 3) switch
                {
                    0 => RuneColor.Red,
                    1 => RuneColor.Yellow,
                    _ => RuneColor.Blue,
                };
            if (unlockedColor == color) count++;
        }
        return count;
    }

    /// <summary>
    /// The summoner level a rune tier opens at: tier 1 from level 1, tier 2 from level 10, tier 3 from level 20 (the January
    /// 2012 Learn 2 LoL guide and the LoL Manual v1.3, see docs/items/runes-catalog-audit.md). The store's <c>minimumLevel</c>
    /// and the platform's rune-page saves both read it, so buying and equipping can not drift apart.
    /// </summary>
    public static int MinimumLevel(RuneTier tier) => tier switch { RuneTier.Three => 20, RuneTier.Two => 10, _ => 1 };

    /// <summary>
    /// The highest rune tier an account level can use (see <see cref="MinimumLevel"/>). Reconstruction (owner decision, 18 October 2026, evening):
    /// retail's own tier gate is not recovered. The stock AIR client does not
    /// gate tiers when a rune is dropped on a slot (RuneBookController.canAddToSlot compares the rune type and the slot's minLevel
    /// only; the tier checkboxes only filter the inventory view), so <see cref="Create"/> accepts any tier and the gate is the
    /// platform's: owner decision 18 October 2026 (evening), <c>PlatformAccounts</c> refuses a rune purchase and a rune-page
    /// save above the account's own level. The launcher's editor uses it to offer the runes a level may use.
    /// </summary>
    public static RuneTier MaxUsableTier(int summonerLevel)
    {
        ValidateSummonerLevel(summonerLevel);
        return summonerLevel >= 20 ? RuneTier.Three
            : summonerLevel >= 10 ? RuneTier.Two
            : RuneTier.One;
    }

    public static RunePage Create(RuneCatalog catalog,
        IEnumerable<int>? red = null, IEnumerable<int>? blue = null,
        IEnumerable<int>? yellow = null, IEnumerable<int>? black = null,
        int summonerLevel = 30)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ValidateSummonerLevel(summonerLevel);
        var redIds = Copy(red);
        var blueIds = Copy(blue);
        var yellowIds = Copy(yellow);
        var blackIds = Copy(black);
        ValidateSlots(catalog, redIds, UnlockedSlots(summonerLevel, RuneColor.Red), RuneColor.Red, nameof(red));
        ValidateSlots(catalog, blueIds, UnlockedSlots(summonerLevel, RuneColor.Blue), RuneColor.Blue, nameof(blue));
        ValidateSlots(catalog, yellowIds, UnlockedSlots(summonerLevel, RuneColor.Yellow), RuneColor.Yellow, nameof(yellow));
        ValidateSlots(catalog, blackIds, UnlockedSlots(summonerLevel, RuneColor.Black), RuneColor.Black, nameof(black));
        var ids = redIds.Concat(blueIds).Concat(yellowIds).Concat(blackIds).ToArray();
        if (ids.Length == 0) return Empty;
        var modifiers = catalog.AggregateModifiers(ids);
        return new RunePage(redIds, blueIds, yellowIds, blackIds, modifiers,
            ToWireIds(redIds, yellowIds, blueIds, blackIds));
    }

    /// <summary>
    /// The page of a bot seat: every rune of its shipped <c>Bot*Runes.inibin</c> list, in slot order
    /// (zero marks an empty slot). The bot lists reach the game without the lobby: they fill
    /// Rune1..Rune30 with no colour, capacity or tier rule, and the game applies each listed rune.
    /// The shipped eINTERMEDIATE lists hold ten yellow runes for nine seal slots, and Shen's
    /// eADVANCED and eUBER lists put nine red marks into the seal positions, so no lobby page can
    /// hold them. A bot page never throws for its content (a crash at the start of a game is the worst outcome for a bot
    /// seat): Reconstruction, no shipped file or client code says what the retail server did with a bot id that has no rune
    /// record or with more than the 30 slots the client's AvatarInfo holds (the shipped lists have neither, 17 champions x 4
    /// difficulties checked), so such an id is read as an empty slot and the entries after the 30th are ignored.
    /// </summary>
    public static RunePage CreateBot(RuneCatalog catalog, IEnumerable<int> slots)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(slots);
        var wire = slots.Take(TotalSlots).Select(id => catalog.Get(id) is null ? 0 : id).ToArray();
        var runes = wire.Where(id => id != 0).Select(id => catalog.Get(id)!).ToArray();
        if (runes.Length == 0) return Empty;
        int[] OfColor(RuneColor color) => runes.Where(rune => rune.Color == color).Select(rune => rune.Id).ToArray();
        return new RunePage(OfColor(RuneColor.Red), OfColor(RuneColor.Blue), OfColor(RuneColor.Yellow),
            OfColor(RuneColor.Black), catalog.AggregateModifiers(runes.Select(rune => rune.Id)),
            wire.Concat(new int[TotalSlots - wire.Length]).ToArray(), botList: true);
    }

    private static IReadOnlyList<int> Copy(IEnumerable<int>? ids) =>
        Array.AsReadOnly((ids ?? Array.Empty<int>()).ToArray());

    private static void ValidateSlots(RuneCatalog catalog, IReadOnlyList<int> ids,
        int expected, RuneColor color, string argument)
    {
        if (ids.Count > expected)
            throw new ArgumentException(
                $"{color} rune slots allow at most {expected} entries at this summoner level.", argument);
        foreach (var id in ids)
        {
            var rune = catalog.Get(id) ?? throw new ArgumentException($"Unknown rune ID {id}.", argument);
            if (rune.Color != color)
                throw new ArgumentException($"Rune {id} is {rune.Color}, not {color}.", argument);
        }
    }

    private static void ValidateSummonerLevel(int summonerLevel)
    {
        if (summonerLevel is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(summonerLevel), "summoner level must be 1..30");
    }

    private static int[] ToWireIds(
        IReadOnlyList<int> red,
        IReadOnlyList<int> yellow,
        IReadOnlyList<int> blue,
        IReadOnlyList<int> black)
    {
        var result = new int[TotalSlots];
        var offset = 0;
        foreach (var (group, capacity) in new[]
                 { (red, RedSlots), (yellow, YellowSlots), (blue, BlueSlots), (black, BlackSlots) })
        {
            for (var index = 0; index < group.Count; index++)
                result[offset + index] = group[index];
            offset += capacity;
        }
        return result;
    }
}
