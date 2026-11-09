using System.Text.Json;
using Amberline.Data;

namespace Amberline.Platform;

/// <summary>
/// Durable economy settings.  Values are deliberately editable host defaults;
/// they are not presented as recovered patch-126 prices or progression rules.
/// Reconstruction: nothing in the stock client states the server's reward rules, so these defaults rest on the public
/// League of Legends wiki's later-era Classic formula (win 18 IP + 2.312 per minute, loss 16 IP + 1.405 per minute;
/// the wiki also lists a 7 minute minimum, a 25 to 55 minute window for the per-minute part and a 0.75 custom-game
/// modifier, none of which is applied here) and on guesses for XP and the level thresholds. The queue, loyalty and location
/// bonuses that <c>EndOfGameStats</c> has fields for are not awarded; First Win of the Day is (<see cref="PlatformEconomy.FirstWinBonusIp"/>).
/// Daily practice-game quota. Evidence in the stock client: <c>EndOfGameStatsView</c> shows an 'XP/IP reward minutes left
/// today' panel for custom (queue type NONE) and Co-op vs. AI (BOT) games while <c>practiceMinutesLeftToday</c> is not
/// negative, greys it out at 0 and prints 'Resets in' from <c>practiceMsecsUntilReset</c>; the bundle has a 'penalty for
/// playing too many Custom Games in one day' line. So retail budgeted reward minutes per day and reduced the reward past
/// it. Reconstruction: neither the size of the budget (<see cref="PracticeMinutesPerDay"/>), the instant of the reset
/// (<see cref="PracticeResetHourUtc"/>) nor the size of the penalty is in the client. This host pays the minutes inside the
/// budget in full, pays nothing for the minutes past it (a 100% penalty) and resets on a fixed UTC hour.
/// </summary>
public sealed class PlatformEconomyRules
{
    public bool CustomGameRewards { get; set; } = true;
    /// <summary>Reconstruction: reward minutes a custom or Co-op vs. AI player can earn per day (see the class summary).</summary>
    public int PracticeMinutesPerDay { get; set; } = 180;
    /// <summary>Reconstruction: the UTC hour at which the daily practice budget refills.</summary>
    public int PracticeResetHourUtc { get; set; }
    public int[] FreeChampionIds { get; set; } =
        [1, 61, 64, 76, 83, 35, 81, 67, 40, 53];
    public int ExperienceBase { get; set; } = 200;
    public int ExperiencePerLevel { get; set; } = 100;
    public double WinExperiencePerMinute { get; set; } = 6.6;
    public double LossExperiencePerMinute { get; set; } = 5.4;
    public double WinIpPerMinute { get; set; } = 2.312;
    public double LossIpPerMinute { get; set; } = 1.405;
    public double WinIpBase { get; set; } = 18;
    public double LossIpBase { get; set; } = 16;
    public double MinimumRewardSeconds { get; set; } = 60;
    public double MaximumRewardSeconds { get; set; } = 120 * 60;
    // Owner decision, 25 October 2026: 1.0.0.126 allows ten rune pages (twenty came only in 1.0.0.129). A stored value still loads
    // (2 to 20), and accounts above a lowered cap keep their pages.
    public int MaximumRunePages { get; set; } = 10;
    // Boost values are host policy defaults; patch-126 product rules were not
    // recovered. Prices remain editable through Prices below.
    public int IpBoostDurationDays { get; set; } = 1;
    public int XpBoostDurationDays { get; set; } = 1;
    public int IpBoostWinCount { get; set; } = 10;
    public int XpBoostWinCount { get; set; } = 10;
    public long IpBoostPerWinBonus { get; set; } = 150;
    public int XpBoostPerWinBonus { get; set; } = 200;
    public double IpBoostTimedBonusMultiplier { get; set; } = 1;
    public double XpBoostTimedBonusMultiplier { get; set; } = 1;
    public Dictionary<string, PlatformStorePrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ExperienceToNextLevel(int level)
    {
        if (level is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(level));
        if (level >= 30) return 0;
        return checked(ExperienceBase + ExperiencePerLevel * (level - 1));
    }

    public static PlatformEconomyRules Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var rules = JsonSerializer.Deserialize<PlatformEconomyRules>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Empty economy rules.");
            rules.Validate();
            return rules;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw new InvalidDataException($"Cannot read economy rules '{path}': {exception.Message}", exception);
        }
    }

    public static PlatformEconomyRules LoadOrSeed(string path)
    {
        if (File.Exists(path)) return Load(path);
        var rules = new PlatformEconomyRules();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rules, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path))
        {
            return Load(path);
        }
        return rules;
    }

    public void Validate()
    {
        if (FreeChampionIds is null || FreeChampionIds.Length > 100 || FreeChampionIds.Any(id => id <= 0))
            throw new InvalidDataException("Free champion IDs are invalid.");
        if (ExperienceBase <= 0 || ExperiencePerLevel < 0)
            throw new InvalidDataException("Experience thresholds are invalid.");
        if (!FiniteNonNegative(WinExperiencePerMinute) || !FiniteNonNegative(LossExperiencePerMinute) ||
            !FiniteNonNegative(WinIpPerMinute) || !FiniteNonNegative(LossIpPerMinute) ||
            !FiniteNonNegative(WinIpBase) || !FiniteNonNegative(LossIpBase) ||
            !FiniteNonNegative(MinimumRewardSeconds) || !FiniteNonNegative(MaximumRewardSeconds) ||
            MaximumRewardSeconds < MinimumRewardSeconds)
            throw new InvalidDataException("Economy reward rates are invalid.");
        if (PracticeMinutesPerDay is < 1 or > 1440 || PracticeResetHourUtc is < 0 or > 23)
            throw new InvalidDataException("The daily practice quota is outside its bounds.");
        if (MaximumRunePages is < 2 or > 20)
            throw new InvalidDataException("Rune page count must be between 2 and 20.");
        if (IpBoostDurationDays is < 1 or > PlatformEconomy.MaxBoostDurationDays ||
            XpBoostDurationDays is < 1 or > PlatformEconomy.MaxBoostDurationDays ||
            IpBoostWinCount is < 1 or > PlatformEconomy.MaxBoostWinCount ||
            XpBoostWinCount is < 1 or > PlatformEconomy.MaxBoostWinCount ||
            IpBoostPerWinBonus < 0 || IpBoostPerWinBonus > PlatformEconomy.MaxBoostBonus ||
            XpBoostPerWinBonus < 0 || XpBoostPerWinBonus > PlatformEconomy.MaxBoostBonus ||
            !FiniteBoostMultiplier(IpBoostTimedBonusMultiplier) ||
            !FiniteBoostMultiplier(XpBoostTimedBonusMultiplier))
            throw new InvalidDataException("Boost rules are outside their safe bounds.");
        if (Prices is null)
            Prices = new Dictionary<string, PlatformStorePrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, price) in Prices)
        {
            if (string.IsNullOrWhiteSpace(id) || price is null ||
                price.Ip < 0 || price.Ip > PlatformEconomy.MaxBalance ||
                price.Rp < 0 || price.Rp > PlatformEconomy.MaxBalance ||
                (price.Ip is null && price.Rp is null))
                throw new InvalidDataException("Economy prices are invalid.");
        }
    }

    private static bool FiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool FiniteBoostMultiplier(double value) =>
        double.IsFinite(value) && value >= 0 && value <= PlatformEconomy.MaxBoostMultiplier;
}

/// <summary>Persisted optional economy state. Null on an account means legacy maxed state.</summary>
public sealed record PlatformEconomyData(
    bool AllUnlocked,
    long Ip,
    long Rp,
    int Experience,
    int Level = 1,
    int[]? OwnedChampions = null,
    int[]? OwnedSkins = null,
    Dictionary<int, int>? OwnedRunes = null,
    PlatformPurchase[]? Purchases = null,
    PlatformCurrencyGrant[]? Grants = null,
    long IpBoostEndDate = 0,
    long XpBoostEndDate = 0,
    int IpBoostPerWinCount = 0,
    int XpBoostPerWinCount = 0,
    double PracticeSecondsToday = 0,
    long PracticeDay = 0,
    // Unix milliseconds of the last First Win of the Day claimed; 0 for never.
    long FirstWinAt = 0);

public sealed record PlatformEconomyState(
    bool AllUnlocked,
    long Ip,
    long Rp,
    int Experience,
    int[]? OwnedChampions,
    int[]? OwnedSkins,
    Dictionary<int, int>? OwnedRunes,
    PlatformPurchase[]? Purchases,
    int Level = 30,
    PlatformCurrencyGrant[]? Grants = null,
    long IpBoostEndDate = 0,
    long XpBoostEndDate = 0,
    int IpBoostPerWinCount = 0,
    int XpBoostPerWinCount = 0);

public sealed record PlatformPurchase(
    Guid RequestId,
    string ItemId,
    string Currency,
    long Price,
    DateTimeOffset PurchasedAt,
    int Quantity = 1);

public sealed record PlatformCurrencyGrant(
    Guid RequestId,
    long Ip,
    long Rp,
    DateTimeOffset GrantedAt);

public sealed record PlatformReward(
    long IpEarned,
    int XpEarned,
    int PreviousLevel,
    int Level,
    long IpTotal,
    long ExperienceTotal,
    int ExpToNextLevel,
    long BaseIpEarned = 0,
    long TimedIpEarned = 0,
    long WinIpEarned = 0,
    int BaseXpEarned = 0,
    int TimedXpEarned = 0,
    int WinXpEarned = 0,
    long FirstWinIpEarned = 0);

/// <summary>Seconds of custom/Co-op reward time left and spent in the current daily window, and the time to its reset.</summary>
public sealed record PlatformPracticeQuota(double SecondsLeft, double SecondsSpent, long MillisecondsUntilReset, long Day);

public static class PlatformEconomy
{
    public const long MaxBalance = 1_000_000_000;
    public const int MaxBoostDurationDays = 365;
    public const int MaxBoostWinCount = 1_000;
    public const long MaxBoostBonus = MaxBalance;
    public const double MaxBoostMultiplier = 4;

    public static PlatformEconomyState For(PlatformAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Economy is null)
            return new(true, 0, 0, 0, null, null, null, [], 30, null, 0, 0, 0, 0);
        var economy = account.Economy;
        return new(economy.AllUnlocked, economy.Ip, economy.Rp, economy.Experience,
            economy.OwnedChampions?.Distinct().Order().ToArray(),
            economy.OwnedSkins?.Distinct().Order().ToArray(),
            economy.OwnedRunes is null ? null : new Dictionary<int, int>(economy.OwnedRunes),
            economy.Purchases?.ToArray(), economy.Level, economy.Grants?.ToArray(), economy.IpBoostEndDate,
            economy.XpBoostEndDate, economy.IpBoostPerWinCount, economy.XpBoostPerWinCount);
    }

    public static bool CanPlay(PlatformAccount account, int championId, PlatformEconomyRules rules)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(rules);
        return account.Economy is null || account.Economy.AllUnlocked ||
            rules.FreeChampionIds.Contains(championId) ||
            (account.Economy.OwnedChampions?.Contains(championId) == true);
    }

    public static bool OwnsSkin(PlatformAccount account, int globalSkinId)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Economy is null || account.Economy.AllUnlocked ||
            (account.Economy.OwnedSkins?.Contains(globalSkinId) == true);
    }

    internal static PlatformEconomyData LegacyData() =>
        new(true, 0, 0, 0, 30, null, null, null, [], [], 0, 0, 0, 0);

    internal static PlatformEconomyData Copy(PlatformEconomyData data) => new(
        data.AllUnlocked, data.Ip, data.Rp, data.Experience, data.Level,
        data.OwnedChampions?.Distinct().Order().ToArray(), data.OwnedSkins?.Distinct().Order().ToArray(),
        data.OwnedRunes is null ? null : new Dictionary<int, int>(data.OwnedRunes),
        data.Purchases?.ToArray(), data.Grants?.ToArray(), data.IpBoostEndDate, data.XpBoostEndDate,
        data.IpBoostPerWinCount, data.XpBoostPerWinCount, data.PracticeSecondsToday, data.PracticeDay, data.FirstWinAt);

    /// <summary>
    /// First Win of the Day as retail had it in 1.0.0.126 (batch 549): winning a matchmade game grants a flat 150 IP that boosts
    /// do not change, and claiming it makes it unavailable for 22 hours (the League wiki's First Win of the Day page, Release
    /// and history: 150 IP and 22 hours until V7.22/V7.23; the V1.0.0.111 notes of February 2011 say "First Win of the Day
    /// remains unchanged - it is still a flat bonus"). Custom games are not matchmade and never earn it; the stock end-of-game
    /// screen hides the first-win timer for them (EndOfGameStatsView.initializeView: queueType NONE).
    /// </summary>
    public const long FirstWinBonusIp = 150;
    public static readonly TimeSpan FirstWinCooldown = TimeSpan.FromHours(22);

    /// <summary>Unix milliseconds from which the account can claim First Win of the Day again; 0 when it is available now or never claimed.</summary>
    public static long FirstWinAvailableAt(PlatformAccount account) =>
        account.Economy is { FirstWinAt: > 0 } economy ? economy.FirstWinAt + (long)FirstWinCooldown.TotalMilliseconds : 0;

    /// <summary>
    /// The account's daily custom/Co-op reward budget at <paramref name="now"/>. The budget refills when the fixed daily
    /// reset hour passes (Reconstruction, see <see cref="PlatformEconomyRules"/>); a stored day other than today's means
    /// nothing has been spent yet today.
    /// </summary>
    public static PlatformPracticeQuota Practice(PlatformAccount account, PlatformEconomyRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(rules);
        var day = DayOf(rules, now);
        var spent = account.Economy is { } economy && economy.PracticeDay == day ? economy.PracticeSecondsToday : 0;
        var budget = rules.PracticeMinutesPerDay * 60.0;
        var resets = (day + 1) * DayMilliseconds + rules.PracticeResetHourUtc * 3_600_000L;
        return new(Math.Clamp(budget - spent, 0, budget), Math.Clamp(spent, 0, budget), Math.Max(0, resets - now.ToUnixTimeMilliseconds()), day);
    }

    internal static long DayOf(PlatformEconomyRules rules, DateTimeOffset now) =>
        Math.Max(0, now.ToUnixTimeMilliseconds() - rules.PracticeResetHourUtc * 3_600_000L) / DayMilliseconds;

    private const long DayMilliseconds = 86_400_000L;

    /// <summary>The AIR id of a playable champion (exact name); 0 for anything the server cannot play.</summary>
    public static int ChampionId(string champion) =>
        ChampionCatalog.ByName(champion) is { Playable: true } info && info.Name == champion ? info.Id : 0;

    internal static long TotalExperience(int level, int currentExperience, PlatformEconomyRules rules)
    {
        var total = (long)Math.Max(0, currentExperience);
        for (var current = 1; current < Math.Min(level, 30); current++)
            total = checked(total + rules.ExperienceToNextLevel(current));
        return total;
    }
}
