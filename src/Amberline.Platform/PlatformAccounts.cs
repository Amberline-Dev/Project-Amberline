using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Server;
using Amberline.Core;

namespace Amberline.Platform;

public sealed record PlatformAccount(long Id, long SummonerId, string Username, string Summoner,
    string Salt, string PasswordHash, MatchPlayer Loadout, int ProfileIconId = 0,
    bool TutorialFlag = false, bool HelpFlag = false, PlatformRunePage[]? RunePages = null,
    PlatformEconomyData? Economy = null, DateTimeOffset? LeavesClearedAt = null, DateTimeOffset[]? Dodges = null,
    // Batch 646: written only when set, so a store without a removed friend stays as it was (an older build, which refuses an
    // unknown field, still reads it); missing means active.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AccessRemovedAt = null);
public sealed record PlatformResult(long GameId, Guid HostGameId, long[] Accounts, JsonElement Result,
    DateTimeOffset CompletedAt = default, int QueueId = 0,
    Dictionary<long, PlatformReward>? Rewards = null, Dictionary<long, PlatformRating>? Ratings = null);
/// <summary>A player's rating in a ranked game's queue before and after it (batch 586, <see cref="PlatformRatings"/>).</summary>
public sealed record PlatformRating(int Before, int After);

public sealed record PlatformRunePage(long PageId, string Name, bool IsCurrent,
    Dictionary<int, int> Slots, DateTime CreatedAt);

/// <summary>
/// Small invited group: one atomic snapshot owns accounts and result receipts.
/// NOT RETAIL: retail's accounts, purchases, rune pages and results lived in Riot's account databases
/// (accounts were created on the signup website). This is a local JSON snapshot with PBKDF2 hashes, accounts
/// the host creates, a fully unlocked level-30 mode (<c>Economy</c> null) or a fresh level-1 mode, and a file
/// lease. The client only sees the DTOs built from it, whose wire shapes follow the stock AIR classes.
/// </summary>
public sealed partial class PlatformAccounts : IDisposable
{
    // Retired (25 October 2026): no invitation is issued or accepted; the field only lets an older store load.
    public sealed record State(long NextId, PlatformAccount[] Accounts, string[] Invitations, PlatformResult[] Results,
        int NextGameId = 1, PlatformRewardCode[]? RewardCodes = null, PlatformReport[]? Reports = null);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly TimeProvider _time;
    private State _state;
    private readonly string _airRoot;
    private PlatformStoreCatalog? _storeCatalog;
    private static readonly JsonSerializerOptions Json = MatchRoster.JsonOptions;
    private const int Iterations = 310_000;
    private const long FirstRunePageId = 1;
    private const long SecondRunePageId = 2;
    private const string DefaultRunePageName = "Default";
    private const string SecondRunePageName = "Page 2";
    private const string ReservedPageNameToken = "@@!PaG3!@@";
    public const int MaxProfileIconId = 28;
    public PlatformEconomyRules EconomyRules { get; }
    public PlatformStoreCatalog StoreCatalog => _storeCatalog ??= new PlatformStoreCatalog(EconomyRules, _airRoot, _time);

    public PlatformAccounts(string directory, string? airRoot = null, TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "accounts.json");
        _airRoot = Path.GetFullPath(airRoot ?? directory);
        _time = timeProvider ?? TimeProvider.System;
        _lease = new FileStream(Path.Combine(directory, "accounts.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            EconomyRules = PlatformEconomyRules.LoadOrSeed(Path.Combine(directory, "economy.json"));
            _state = File.Exists(_path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("Empty account store.")
                : new State(1, [], [], []);
            if (_state.NextId is < 1 or > int.MaxValue - 1_000_000 || _state.NextGameId < 1 ||
                _state.Accounts.Select(a => a.Id).Distinct().Count() != _state.Accounts.Length)
                throw new InvalidDataException("Invalid account store.");
            foreach (var account in _state.Accounts)
                if (account.Economy is not null) ValidateEconomyData(account.Economy);
            ValidateRewardCodes(_state.RewardCodes);
        }
        catch { _lease.Dispose(); throw; }
    }

    public int AllocateGameId()
    {
        lock (_gate)
        {
            var id = _state.NextGameId;
            Save(_state with { NextGameId = checked(id + 1) });
            return id;
        }
    }

    /// <summary>
    /// The store's refusals of a new login's username, password and summoner name, each on its own (null: it passes), in the texts
    /// <see cref="RegisterPublic"/> throws. Batch 649: the host's login page shows every refusal at once. The username is trimmed and
    /// lower-cased and the summoner name trimmed first, as RegisterPublic stores them.
    /// </summary>
    public static (string? Username, string? Password, string? Summoner) Refusals(string username, string password, string summoner)
    {
        username = username.Trim().ToLowerInvariant();
        summoner = summoner.Trim();
        return (username.Length is < 3 or > 24 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                ? "Username must contain 3–24 ASCII letters, digits or underscores." : null,
            PasswordRefusal(password),
            summoner.Length is < 1 or > MatchRoster.MaxNameLength || summoner.Any(c => c is < ' ' or > '~')
                ? "Summoner name must contain 1–27 printable ASCII characters." : null);
    }

    /// <summary>The store's refusal of a name already in use (RegisterPublic's text), or null when both names are free.</summary>
    public string? TakenRefusal(string username, string summoner)
    {
        username = username.Trim().ToLowerInvariant();
        summoner = summoner.Trim();
        lock (_gate)
            return _state.Accounts.Any(a => a.Username == username || a.Summoner.Equals(summoner, StringComparison.OrdinalIgnoreCase))
                ? "Username or summoner name is already taken." : null;
    }

    /// <summary>Public self-service registration used by a host that is reachable by its configured endpoint.</summary>
    public PlatformAccount RegisterPublic(string username, string password, string summoner, bool maxed = true)
    {
        var refused = Refusals(username, password, summoner);
        if ((refused.Username ?? refused.Password ?? refused.Summoner) is { } message) throw new ArgumentException(message);
        username = username.Trim().ToLowerInvariant();
        summoner = summoner.Trim();
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt);
        lock (_gate)
        {
            if (TakenRefusal(username, summoner) is { } taken) throw new ArgumentException(taken);
            var id = _state.NextId;
            if (id >= int.MaxValue - 1_000_000) throw new ArgumentException("Account capacity reached.");
            var defaultChampion = EconomyRules.FreeChampionIds
                .Select(id => AirGameDtos.ChampionNames.TryGetValue(id, out var name) ? name : null)
                .FirstOrDefault(name => name is not null) ?? "Annie";
            var loadout = new MatchPlayer(summoner, defaultChampion, Team.Order,
                SummonerSpell1: maxed ? SummonerLoadout.DefaultFirst : "SummonerHeal",
                SummonerSpell2: maxed ? SummonerLoadout.DefaultSecond : "SummonerHaste",
                SummonerLevel: maxed ? 30 : 1);
            var account = new PlatformAccount(id, checked(1_000_000 + id), username, summoner,
                Convert.ToHexString(salt), Convert.ToHexString(hash), loadout,
                RunePages: SeedRunePages(loadout), Economy: maxed ? null :
                    new PlatformEconomyData(false, 0, 0, 0, 1, [], [], new Dictionary<int, int>(), [], []));
            Save(_state with { NextId = checked(id + 1), Accounts = [.. _state.Accounts, account] });
            return account;
        }
    }

    /// <summary>The store's refusal of a password (null: it passes): 10 to 128 characters, no control characters.</summary>
    private static string? PasswordRefusal(string password) =>
        password.Length is < 10 or > 128 || password.Any(char.IsControl) ? "Password must contain 10–128 characters without control characters." : null;

    private static void ValidatePassword(string password)
    {
        if (PasswordRefusal(password) is { } refused) throw new ArgumentException(refused);
    }

    /// <summary>
    /// NOT RETAIL: host tool (the /manage password reset; retail's was Riot's account website). A new salt and the same PBKDF2
    /// derivation as registration and login, so the old password stops authenticating at once. The caller ends the account's
    /// tunnel token and sessions. A new password also lets in a login whose access was removed (<see cref="RemoveAccess"/>).
    /// </summary>
    public void SetPassword(long accountId, string password) => SetPassword(accountId, password, removedAt: null);

    /// <summary>
    /// NOT RETAIL: host tool (Friends' Remove access, batch 646 keeps it on record). A password nobody is told (32 random bytes, only
    /// its hash is kept) and the time, so the host's Friends page says "Access removed"; <see cref="SetPassword(long, string)"/> undoes it.
    /// The caller ends the account's tunnel token and sessions; the account and its games stay.
    /// </summary>
    public void RemoveAccess(long accountId) =>
        SetPassword(accountId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), _time.GetUtcNow());

    private void SetPassword(long accountId, string password, DateTimeOffset? removedAt)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt);
        lock (_gate)
        {
            RequireAccount(accountId);
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId
                ? a with { Salt = Convert.ToHexString(salt), PasswordHash = Convert.ToHexString(hash), AccessRemovedAt = removedAt } : a).ToArray() });
        }
    }

    public PlatformAccount? Authenticate(string username, string password)
    {
        if (username.Length > 24 || password.Length > 128) return null;
        PlatformAccount? account;
        lock (_gate) account = _state.Accounts.FirstOrDefault(a => a.Username == username.Trim().ToLowerInvariant());
        // Keep missing-user and wrong-password work comparable.
        var actual = Derive(password, account is null ? new byte[16] : Convert.FromHexString(account.Salt));
        var expected = account is null ? new byte[32] : Convert.FromHexString(account.PasswordHash);
        // A removed login's password is one nobody knows; the marker refuses it as well.
        return CryptographicOperations.FixedTimeEquals(actual, expected) && account is { AccessRemovedAt: null } ? account : null;
    }

    public PlatformAccount Get(long id)
    {
        lock (_gate) return _state.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException("Account does not exist.");
    }

    public PlatformAccount? Find(long id)
    {
        lock (_gate) return _state.Accounts.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>An account with no games, for stats reads of an id that does not exist; never stored.</summary>
    public static PlatformAccount Unknown(long id) => new(id, 0, "", "", "", "", null!);

    public PlatformAccount? FindSummoner(long id)
    {
        lock (_gate) return _state.Accounts.FirstOrDefault(a => a.SummonerId == id);
    }

    public PlatformAccount? FindName(string name)
    {
        lock (_gate) return _state.Accounts.FirstOrDefault(a => a.Summoner.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public PlatformAccount? FindInternalName(string name) => name.StartsWith("sum", StringComparison.Ordinal) &&
        long.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) &&
        name == "sum" + id ? FindSummoner(id) : null;

    public PlatformRunePage[] GetRunePages(long accountId)
    {
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Id == accountId)
                ?? throw new ArgumentException("Account does not exist.", nameof(accountId));
            return RunePagesFor(account);
        }
    }

    public static PlatformRunePage[] RunePagesFor(PlatformAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.RunePages is null
            ? VirtualRunePages(account.Loadout)
            : account.RunePages.OrderBy(page => page.PageId).Select(CopyPage).ToArray();
    }

    public void SaveSummonerPreferences(long accountId, long summonerId, int profileIconId,
        bool? tutorialFlag = null, bool? helpFlag = null)
    {
        if ((uint)profileIconId > MaxProfileIconId)
            throw new ArgumentOutOfRangeException(nameof(profileIconId), "Profile icon must be one of the shipped AIR icons (0–28).");
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Id == accountId)
                ?? throw new ArgumentException("Account does not exist.", nameof(accountId));
            if (account.SummonerId != summonerId)
                throw new ArgumentException("Summoner does not belong to this account.", nameof(summonerId));
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId
                ? a with { ProfileIconId = profileIconId, TutorialFlag = tutorialFlag ?? a.TutorialFlag,
                    HelpFlag = helpFlag ?? a.HelpFlag } : a).ToArray() });
        }
    }

    public void SaveLoadout(long id, MatchPlayer loadout)
    {
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Id == id)
                ?? throw new ArgumentException("Account does not exist.", nameof(id));
            if (loadout.SummonerLevel != account.Loadout.SummonerLevel)
                throw new ArgumentException("The client cannot change the server-owned summoner level.", nameof(loadout));
            var normalized = MatchRoster.Validate([loadout with
            {
                Name = account.Summoner,
                SummonerLevel = account.Loadout.SummonerLevel,
                IsBot = false,
            }])[0];
            ValidateSpellUnlocks(normalized);
            if (!string.Equals(normalized.Champion, account.Loadout.Champion, StringComparison.OrdinalIgnoreCase) ||
                normalized.SkinId != account.Loadout.SkinId)
                ValidateOwnedChampionAndSkin(account, normalized);
            // A loadout save that leaves the runes alone (champion, spells, masteries) must not lock an account out because of an
            // older over-tier or hidden rune, so only a changed rune set is an equip and meets the tier gate and the hidden-rune rule.
            ValidateEquippedRunes(account, !SameRunes(account.Loadout, normalized), normalized.RedRunes, normalized.YellowRunes,
                normalized.BlueRunes, normalized.BlackRunes);
            var pages = RunePagesFor(account);
            var current = pages.Single(page => page.IsCurrent);
            var incomingSlots = SlotsFromLoadout(normalized);
            var runeChanged = !CompactSlotsEqual(current.Slots, incomingSlots);
            var nextPages = account.RunePages;
            if (runeChanged)
            {
                nextPages = pages.Select(page => page.PageId == current.PageId
                    ? page with { Slots = incomingSlots }
                    : page).ToArray();
            }
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == id
                ? account with { Loadout = normalized, RunePages = nextPages } : a).ToArray() });
        }
    }

    public void SaveRunePages(long accountId, long summonerId, IReadOnlyList<PlatformRunePage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        lock (_gate)
        {
            var account = RequireAccount(accountId);
            RequireSummoner(account, summonerId);
            var normalizedPages = NormalizePages(account, pages, gateEveryPage: true);
            var current = normalizedPages.Single(page => page.IsCurrent);
            var loadout = MatchRoster.Validate([ApplyPage(account.Loadout, current, account.Summoner)])[0];
            ValidateSpellUnlocks(loadout);
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId
                ? account with { Loadout = loadout, RunePages = normalizedPages } : a).ToArray() });
        }
    }

    public void SelectRunePage(long accountId, long summonerId, long pageId)
    {
        lock (_gate)
        {
            var account = RequireAccount(accountId);
            RequireSummoner(account, summonerId);
            var pages = RunePagesFor(account);
            if (!pages.Any(page => page.PageId == pageId))
                throw new ArgumentException("Rune page does not belong to this account.", nameof(pageId));
            var selected = pages.Select(page => page with
            {
                IsCurrent = page.PageId == pageId,
                Slots = new Dictionary<int, int>(page.Slots),
            }).ToArray();
            var normalizedPages = NormalizePages(account, selected, gateEveryPage: false);
            var current = normalizedPages.Single(page => page.IsCurrent);
            var loadout = MatchRoster.Validate([ApplyPage(account.Loadout, current, account.Summoner)])[0];
            ValidateSpellUnlocks(loadout);
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId
                ? account with { Loadout = loadout, RunePages = normalizedPages } : a).ToArray() });
        }
    }

    public PlatformAccount[] ListAccounts()
    {
        lock (_gate) return _state.Accounts.Select(CopyAccount).ToArray();
    }

    public PlatformResult? FindResult(Guid hostGameId)
    {
        lock (_gate) return _state.Results.FirstOrDefault(result => result.HostGameId == hostGameId);
    }

    /// <summary>
    /// Buy <paramref name="quantity"/> of a store product. The price is the catalog's price at this instant (a calendar sale
    /// included), read under the account lock; the caller never supplies it. <paramref name="expectedUnitPrice"/> is the price
    /// the buyer's page showed: when given and different from the current price (a sale began or ended since the page
    /// was rendered), the purchase is refused instead of charging another amount. It can only refuse, never set a price.
    /// </summary>
    public PlatformPurchase Purchase(long accountId, string itemId, string currency, Guid requestId, int quantity = 1,
        long? expectedUnitPrice = null)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A purchase request ID is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("A store item is required.", nameof(itemId));
        if (quantity is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(quantity), "Purchase quantity must be between one and nine.");
        currency = currency.Trim().ToUpperInvariant();
        if (currency is not ("IP" or "RP")) throw new ArgumentException("Currency must be IP or RP.", nameof(currency));
        lock (_gate)
        {
            var account = RequireAccount(accountId);
            var data = account.Economy is null ? PlatformEconomy.LegacyData() : PlatformEconomy.Copy(account.Economy);
            var prior = data.Purchases?.FirstOrDefault(purchase => purchase.RequestId == requestId);
            if (prior is not null)
            {
                if (!prior.ItemId.Equals(itemId.Trim(), StringComparison.OrdinalIgnoreCase) || prior.Currency != currency || prior.Quantity != quantity)
                    throw new ArgumentException("The request ID was already used for a different purchase.", nameof(requestId));
                return prior;
            }

            var item = StoreCatalog.Get(itemId.Trim());
            // NOT RETAIL: retail sold the three bundles whose contents this server does not implement; they are the only locked products since
            // 26 October 2026 (batch 341, every champion and skin plays), so they stay unbuyable here. The champion message guards any other lock.
            if (item.Locked)
                throw new ArgumentException(item.Kind == "BUNDLE" ? "That bundle is not available on this server."
                    : "That champion cannot be played on this server yet.", nameof(itemId));
            if (quantity != 1 && item.Kind != "RUNE")
                throw new ArgumentException("Only runes can be purchased in multiple quantities.", nameof(quantity));
            var price = currency == "IP" ? item.IpPrice : item.RpPrice;
            if (price is null or < 0) throw new ArgumentException("That item cannot be bought with the requested currency.", nameof(currency));
            if (expectedUnitPrice is { } shown && shown != price.Value)
                throw new ArgumentException("The price of that item changed since the page was shown. Reload the item and try again.", nameof(expectedUnitPrice));
            var totalPrice = checked((long)price.Value * quantity);
            var level = data.Level is < 1 or > 30 ? throw new InvalidDataException("Account economy level is invalid.") : data.Level;
            if (level < item.MinimumLevel) throw new ArgumentException("The account level is too low for that item.", nameof(itemId));
            var balance = currency == "IP" ? data.Ip : data.Rp;
            if (balance < totalPrice) throw new InvalidOperationException("The account cannot afford that item.");
            var now = _time.GetUtcNow();

            var ownedChampions = data.OwnedChampions?.Distinct().ToList() ?? [];
            var ownedSkins = data.OwnedSkins?.Distinct().ToList() ?? [];
            var ownedRunes = data.OwnedRunes is null ? new Dictionary<int, int>() : new Dictionary<int, int>(data.OwnedRunes);
            var pages = RunePagesFor(account);
            switch (item.Kind.ToUpperInvariant())
            {
                case "CHAMPION":
                    if (data.AllUnlocked || ownedChampions.Contains(item.ContentId))
                        throw new InvalidOperationException("The account already owns that champion.");
                    ownedChampions.Add(item.ContentId);
                    break;
                case "CHAMPION_SKIN":
                    if (!data.AllUnlocked && !ownedChampions.Contains(item.ChampionId))
                        throw new InvalidOperationException("The account must own the champion before buying its skin.");
                    if (data.AllUnlocked || ownedSkins.Contains(item.ContentId))
                        throw new InvalidOperationException("The account already owns that skin.");
                    ownedSkins.Add(item.ContentId);
                    break;
                case "RUNE":
                    // An all-unlocked account owns the 374 regular runes. A limited-edition window rune (Harrowing 2011) is not part of
                    // that grant, so it is bought like any rune of a normal account, and only while its window is open (Get above).
                    if (data.AllUnlocked && !StoreCatalog.IsWindowRune(item.ContentId))
                        throw new InvalidOperationException("The account already owns that rune.");
                    var limit = RuneQuantityLimit(item.ContentId);
                    if (quantity > limit - ownedRunes.GetValueOrDefault(item.ContentId))
                        throw new InvalidOperationException($"The account has reached the quantity limit for rune {item.ContentId}.");
                    ownedRunes[item.ContentId] = ownedRunes.GetValueOrDefault(item.ContentId) + quantity;
                    break;
                case "RUNE_PAGE":
                case "SPELL_BOOK_PAGE":
                    if (pages.Length >= EconomyRules.MaximumRunePages)
                        throw new InvalidOperationException("The account has reached the rune-page limit.");
                    if (item.ContentId > 0 && pages.Any(page => page.PageId == item.ContentId))
                        throw new InvalidOperationException("The account already owns that rune page.");
                    var nextPageId = pages.Length == 0 ? FirstRunePageId : pages.Max(page => page.PageId) + 1;
                    var pageId = item.ContentId > 0 && pages.All(page => page.PageId != item.ContentId)
                        ? item.ContentId : nextPageId;
                    pages = [.. pages, new PlatformRunePage(pageId, $"Page {pageId}", false, [], DateTime.UtcNow)];
                    break;
                case "BUNDLE":
                {
                    if (!item.Id.Equals(PlatformStoreCatalog.RunePageBundleId, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("The store bundle is not supported.", nameof(itemId));
                    if (data.AllUnlocked)
                        throw new InvalidOperationException("The account already owns that bundle.");
                    if ((long)pages.Length + PlatformStoreCatalog.RunePageBundleSize > EconomyRules.MaximumRunePages)
                        throw new InvalidOperationException("The account needs room for all seven rune pages.");
                    var freePageIds = Enumerable.Range(1, EconomyRules.MaximumRunePages)
                        .Where(candidate => pages.All(page => page.PageId != candidate))
                        .Take(PlatformStoreCatalog.RunePageBundleSize)
                        .ToArray();
                    if (freePageIds.Length != PlatformStoreCatalog.RunePageBundleSize)
                        throw new InvalidOperationException("The account needs room for all seven rune pages.");
                    var usedPageNames = pages.Select(page => page.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string NewPageName(int candidate)
                    {
                        var name = $"Page {candidate}";
                        for (var suffix = 2; !usedPageNames.Add(name); suffix++)
                            name = $"Page {candidate} ({suffix})";
                        return name;
                    }
                    pages = [.. pages, .. freePageIds.Select(candidate =>
                        new PlatformRunePage(candidate, NewPageName(candidate), false, [], now.UtcDateTime))];
                    break;
                }
                case "BOOST":
                    data = ApplyBoostPurchase(data, item, level, now);
                    break;
                default:
                    throw new ArgumentException("The store item kind is not supported.", nameof(itemId));
            }

            var purchase = new PlatformPurchase(requestId, item.Id, currency, totalPrice, now, quantity);
            var purchases = data.Purchases?.ToList() ?? [];
            purchases.Add(purchase);
            data = data with
            {
                Ip = currency == "IP" ? checked(data.Ip - totalPrice) : data.Ip,
                Rp = currency == "RP" ? checked(data.Rp - totalPrice) : data.Rp,
                OwnedChampions = ownedChampions.ToArray(),
                OwnedSkins = ownedSkins.ToArray(),
                OwnedRunes = ownedRunes,
                Purchases = purchases.ToArray(),
            };
            var updated = account with { Economy = data, RunePages = pages };
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId ? updated : a).ToArray() });
            return purchase;
        }
    }

    /// <summary>Host-management operation; the HTTP/XMPP caller must enforce host authorization.</summary>
    public PlatformCurrencyGrant GrantCurrency(long accountId, long ip, long rp, Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A grant request ID is required.", nameof(requestId));
        if (ip < 0 || rp < 0 || ip > PlatformEconomy.MaxBalance || rp > PlatformEconomy.MaxBalance)
            throw new ArgumentOutOfRangeException(nameof(ip), "Currency grant is outside the balance limit.");
        lock (_gate)
        {
            if (_state.RewardCodes?.Any(code => code.Id == requestId) == true)
                throw new ArgumentException("The request ID belongs to a reward code.", nameof(requestId));
            var account = RequireAccount(accountId);
            var data = account.Economy is null ? PlatformEconomy.LegacyData() : PlatformEconomy.Copy(account.Economy);
            var prior = data.Grants?.FirstOrDefault(grant => grant.RequestId == requestId);
            if (prior is not null)
            {
                if (prior.Ip != ip || prior.Rp != rp)
                    throw new ArgumentException("The request ID was already used for a different grant.", nameof(requestId));
                return prior;
            }
            if (data.Ip > PlatformEconomy.MaxBalance - ip || data.Rp > PlatformEconomy.MaxBalance - rp)
                throw new InvalidOperationException("The account balance limit would be exceeded.");
            var grant = new PlatformCurrencyGrant(requestId, ip, rp, DateTimeOffset.UtcNow);
            data = data with
            {
                Ip = checked(data.Ip + ip), Rp = checked(data.Rp + rp),
                Grants = [.. data.Grants ?? [], grant],
            };
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId
                ? a with { Economy = data } : a).ToArray() });
            return grant;
        }
    }

    public bool RecordResult(PlatformResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.HostGameId == Guid.Empty) throw new ArgumentException("Result host ID is required.", nameof(result));
        lock (_gate)
        {
            if (_state.Results.Any(r => r.HostGameId == result.HostGameId)) return false;
            if (result.Accounts is null || result.Accounts.Length is < 1 or > MatchRoster.MaxPlayers ||
                result.Accounts.Distinct().Count() != result.Accounts.Length)
                throw new InvalidDataException("Result account IDs are invalid.");
            if (result.Result.ValueKind is not JsonValueKind.Object)
                throw new InvalidDataException("Result payload must be an object.");
            var authoritative = result.Result.Deserialize<MatchResult>(Json)
                ?? throw new InvalidDataException("Result payload is empty.");
            ValidateResult(authoritative, result.Accounts);
            if (File.Exists(HandoffPath(result.GameId)))
                ValidatePendingResult(ReadHandoff(HandoffPath(result.GameId)), result, authoritative);
            var byAccount = result.Accounts.ToDictionary(id => id, RequireAccount);
            var rewards = new Dictionary<long, PlatformReward>();
            var nextAccounts = _state.Accounts.ToArray();
            // IP, XP and receipts belong to the human accounts only; bot seats earn nothing.
            foreach (var player in authoritative.Players.Where(player => !player.IsBot))
            {
                var account = byAccount.Values.Single(a => (ulong)a.SummonerId == player.PlayerId);
                if (TryAward(account, authoritative, player, result.QueueId,
                    result.CompletedAt == default ? _time.GetUtcNow() : result.CompletedAt, out var updated, out var reward))
                {
                    rewards[account.Id] = reward;
                    nextAccounts = nextAccounts.Select(a => a.Id == account.Id ? updated : a).ToArray();
                }
                // Batch 592: a won Basic Tutorial sets the summoner's tutorialFlag, the field the client's first-login flow reads to offer the
                // tutorial (ViewMediatorImpl.as:151 and 429, !session.summoner.tutorialFlag -> showPlayTutorialGame). The client saves it itself
                // only when the offer is declined or Battle Training follows (PlayTutorialGame.as:225, 363); after a basic tutorial it does not,
                // so retail's server did. Reconstruction: that the server set it at the tutorial's win.
                if (authoritative.MapId == GameMaps.Tutorial && player.Team.Equals(authoritative.Winner, StringComparison.Ordinal))
                    nextAccounts = nextAccounts.Select(a => a.Id == account.Id ? a with { TutorialFlag = true } : a).ToArray();
            }
            // A ranked game's rating change is applied once, here, and kept with the receipt (batch 586).
            var ratings = PlatformGames.QueueOf(result.QueueId) is { Ranked: true } ranked
                ? PlatformRatings.Rate(authoritative, byAccount.Values.ToDictionary(a => (ulong)a.SummonerId, a => a.Id), _state.Results, ranked.Type)
                : null;
            var stored = result with { Rewards = rewards.Count == 0 ? null : rewards, Ratings = ratings is { Count: > 0 } ? ratings : null };
            Save(_state with { Accounts = nextAccounts, Results = [.. _state.Results, stored] });
            return true;
        }
    }

    public PlatformResult[] ResultsFor(long id)
    {
        lock (_gate) return _state.Results.Where(r => r.Accounts.Contains(id)).ToArray();
    }

    /// <summary>The account's current rating in a ranked queue type (<see cref="PlatformRatings.Current"/>).</summary>
    public int Rating(long id, string queueType)
    {
        lock (_gate) return PlatformRatings.Current(id, _state.Results, queueType);
    }

    private void ValidateResult(MatchResult result, IReadOnlyList<long> accountIds)
    {
        // Bot seats report beside the humans; the accounts map to the human seats only.
        if (result.SchemaVersion != 8 || result.Players is null || result.Players.Any(player => player is null) ||
            result.Players.Count(player => !player.IsBot) != accountIds.Count)
            throw new InvalidDataException("Result schema or roster count is invalid.");
        if (!double.IsFinite(result.DurationSeconds) || result.DurationSeconds < 0)
            throw new InvalidDataException("Result duration must be finite and non-negative.");
        if (result.Winner is not ("Order" or "Chaos"))
            throw new InvalidDataException("Result winner is invalid.");
        var accounts = accountIds.Select(RequireAccount).ToDictionary(account => (ulong)account.SummonerId);
        if (result.Players.Select(player => player.PlayerId).Distinct().Count() != result.Players.Count ||
            result.Players.Where(player => !player.IsBot).Any(player => !accounts.ContainsKey(player.PlayerId)) ||
            result.Players.Where(player => player.IsBot).Any(player => accounts.ContainsKey(player.PlayerId)))
            throw new InvalidDataException("Result players do not match the account roster.");
        foreach (var player in result.Players)
        {
            if (player.Team is not ("Order" or "Chaos") || !double.IsFinite(player.Experience) ||
                !double.IsFinite(player.GoldOnHand) ||
                (!player.IsBot && !string.Equals(player.Name, accounts[player.PlayerId].Summoner, StringComparison.Ordinal)))
                throw new InvalidDataException("Result player identity or numeric fields are invalid.");
        }
    }

    private bool TryAward(PlatformAccount account, MatchResult result, PlayerMatchResult player, int queueId, DateTimeOffset completedAt,
        out PlatformAccount updated, out PlatformReward reward)
    {
        updated = account;
        reward = null!;
        // Batch 592: the Basic Tutorial pays no IP or XP. The client's end-of-game screen renames a GAME_TYPE points penalty to TUTORIAL for
        // a tutorial game (EndOfGameStatsController.fixPenalties, EndOfGameStatsController.as:629-641), so retail sent tutorial results with a
        // game-type penalty; Reconstruction: a full one (the client never shows the amount), sent with the results (AirGameDtos.Results).
        // Batch 669: the same for Battle Training, a TUTORIAL_GAME too (Reconstruction: the client's rename covers every tutorial game).
        if (result.Mode == GameMaps.TutorialMode) return false;
        // The release gate removes custom-game rewards only; every matchmade queue, Co-op vs. AI included, keeps paying.
        if ((!EconomyRules.CustomGameRewards && PlatformGames.QueueOf(queueId) is null) || player.IsLeaver ||
            result.DurationSeconds < EconomyRules.MinimumRewardSeconds)
            return false;
        var duration = Math.Min(result.DurationSeconds, EconomyRules.MaximumRewardSeconds);
        // Custom and Co-op vs. AI games draw on a daily budget of reward minutes, as the stock end-of-game screen shows
        // ('XP/IP reward minutes left today'). Reconstruction: the minutes inside the budget pay in full and the minutes
        // past it pay nothing (see PlatformEconomyRules for what the client does and does not state).
        var practice = PlatformGames.PaysFromPracticeBudget(queueId);
        var quota = PlatformEconomy.Practice(account, EconomyRules, completedAt);
        var paidSeconds = practice ? Math.Min(duration, quota.SecondsLeft) : duration;
        if (practice && paidSeconds <= 0) return false;
        var share = duration > 0 ? paidSeconds / duration : 1;
        var minutes = duration / 60.0;
        var win = player.Team.Equals(result.Winner, StringComparison.Ordinal);
        var baseIp = checked((long)Math.Round(share * ((win ? EconomyRules.WinIpBase : EconomyRules.LossIpBase) +
            (win ? EconomyRules.WinIpPerMinute : EconomyRules.LossIpPerMinute) * minutes),
            MidpointRounding.AwayFromZero));
        var baseXp = checked((int)Math.Round(share * (win ? EconomyRules.WinExperiencePerMinute : EconomyRules.LossExperiencePerMinute) * minutes,
            MidpointRounding.AwayFromZero));
        var data = account.Economy is null ? PlatformEconomy.LegacyData() : PlatformEconomy.Copy(account.Economy);
        if (data.Level is < 1 or > 30 || data.Experience < 0)
            throw new InvalidDataException("Account economy progression is invalid.");
        var previousLevel = data.Level;
        var nowMilliseconds = completedAt.ToUnixTimeMilliseconds();
        var xpEligible = previousLevel < 30;
        if (!xpEligible) baseXp = 0;
        var ipTimedActive = data.IpBoostEndDate > nowMilliseconds;
        var xpTimedActive = xpEligible && data.XpBoostEndDate > nowMilliseconds;
        var ipWinActive = win && data.IpBoostPerWinCount > 0;
        var xpWinActive = xpEligible && win && data.XpBoostPerWinCount > 0;
        var timedIp = ipTimedActive ? BoostIpAmount(baseIp, EconomyRules.IpBoostTimedBonusMultiplier) : 0;
        var timedXp = xpTimedActive ? BoostXpAmount(baseXp, EconomyRules.XpBoostTimedBonusMultiplier) : 0;
        var winIp = ipWinActive ? EconomyRules.IpBoostPerWinBonus : 0;
        var winXp = xpWinActive ? EconomyRules.XpBoostPerWinBonus : 0;
        // First Win of the Day: a matchmade win, once per 22 hours, a flat amount no boost scales (PlatformEconomy.FirstWinBonusIp).
        var firstWin = !practice && win && nowMilliseconds >= PlatformEconomy.FirstWinAvailableAt(account) ? PlatformEconomy.FirstWinBonusIp : 0;
        var earnedXp = checked(baseXp + timedXp + winXp);
        var rawIp = checked(baseIp + firstWin + timedIp + winIp);
        var creditedIp = Math.Min(rawIp, PlatformEconomy.MaxBalance - data.Ip);
        var remainingIp = creditedIp;
        var creditedBaseIp = Math.Min(baseIp, remainingIp);
        remainingIp -= creditedBaseIp;
        var creditedFirstWin = Math.Min(firstWin, remainingIp);
        remainingIp -= creditedFirstWin;
        var creditedTimedIp = Math.Min(timedIp, remainingIp);
        remainingIp -= creditedTimedIp;
        var creditedWinIp = Math.Min(winIp, remainingIp);
        var experience = data.Experience;
        var level = previousLevel;
        var xp = earnedXp;
        while (xp > 0 && level < 30)
        {
            var needed = EconomyRules.ExperienceToNextLevel(level);
            var available = needed - experience;
            if (xp < available)
            {
                experience += xp;
                xp = 0;
                break;
            }
            xp -= available;
            experience = 0;
            level++;
        }
        if (previousLevel >= 30) earnedXp = 0;
        var totalIp = checked(data.Ip + creditedIp);
        var totalExperience = PlatformEconomy.TotalExperience(level, experience, EconomyRules);
        data = data with
        {
            Ip = totalIp,
            Experience = experience,
            Level = level,
            IpBoostPerWinCount = ipWinActive ? data.IpBoostPerWinCount - 1 : data.IpBoostPerWinCount,
            XpBoostPerWinCount = xpWinActive ? data.XpBoostPerWinCount - 1 : data.XpBoostPerWinCount,
            FirstWinAt = firstWin > 0 ? nowMilliseconds : data.FirstWinAt,
        };
        if (practice) data = data with { PracticeSecondsToday = quota.SecondsSpent + paidSeconds, PracticeDay = quota.Day };
        updated = account with { Economy = data, Loadout = account.Loadout with { SummonerLevel = level } };
        reward = new PlatformReward(creditedIp, earnedXp, previousLevel, level, totalIp, totalExperience,
            EconomyRules.ExperienceToNextLevel(level), creditedBaseIp, creditedTimedIp, creditedWinIp,
            baseXp, timedXp, winXp, creditedFirstWin);
        return true;
    }

    private static long BoostIpAmount(long baseAmount, double multiplier)
    {
        var value = baseAmount * multiplier;
        if (!double.IsFinite(value) || value > long.MaxValue)
            throw new InvalidDataException("The IP boost result is outside the numeric limit.");
        return checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static int BoostXpAmount(int baseAmount, double multiplier)
    {
        var value = baseAmount * multiplier;
        if (!double.IsFinite(value) || value > int.MaxValue)
            throw new InvalidDataException("The XP boost result is outside the numeric limit.");
        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static int RuneQuantityLimit(int runeId)
    {
        try
        {
            var rune = new RuneCatalog(GameData.Default.Paths).Get(runeId);
            return rune is null ? throw new InvalidOperationException("Rune metadata is unavailable.") :
                rune.Color == RuneColor.Black ? 3 : 9;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Rune purchases require the shipped rune metadata.");
        }
    }

    private PlatformEconomyData ApplyBoostPurchase(PlatformEconomyData data, PlatformStoreItem item,
        int level, DateTimeOffset now)
    {
        if (!PlatformStoreCatalog.TryGetBoost(item.Id, out var ip, out var timed))
            throw new ArgumentException("The boost product is not supported.", nameof(item));
        if (!ip && PlatformStoreCatalog.IsXpBoost(item.Id) && level >= 30)
            throw new InvalidOperationException("XP boosts are unavailable at level 30.");

        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        if (nowMilliseconds < 0) throw new InvalidOperationException("The boost clock is before the Unix epoch.");
        if (timed)
        {
            var durationDays = ip ? EconomyRules.IpBoostDurationDays : EconomyRules.XpBoostDurationDays;
            var prior = ip ? data.IpBoostEndDate : data.XpBoostEndDate;
            var start = Math.Max(prior, nowMilliseconds);
            var duration = checked((long)durationDays * 86_400_000L);
            var end = checked(start + duration);
            var maximum = checked(nowMilliseconds + (long)PlatformEconomy.MaxBoostDurationDays * 86_400_000L);
            if (end > maximum)
                throw new InvalidOperationException("The boost duration accumulation limit was reached.");
            return ip ? data with { IpBoostEndDate = end } : data with { XpBoostEndDate = end };
        }

        var quantity = ip ? EconomyRules.IpBoostWinCount : EconomyRules.XpBoostWinCount;
        var priorCount = ip ? data.IpBoostPerWinCount : data.XpBoostPerWinCount;
        var next = checked(priorCount + quantity);
        if (next > PlatformEconomy.MaxBoostWinCount)
            throw new InvalidOperationException("The boost win accumulation limit was reached.");
        return ip ? data with { IpBoostPerWinCount = next } : data with { XpBoostPerWinCount = next };
    }

    private static PlatformAccount CopyAccount(PlatformAccount account) => account with
    {
        Loadout = account.Loadout with
        {
            RedRunes = account.Loadout.RedRunes?.ToArray(),
            BlueRunes = account.Loadout.BlueRunes?.ToArray(),
            YellowRunes = account.Loadout.YellowRunes?.ToArray(),
            BlackRunes = account.Loadout.BlackRunes?.ToArray(),
            Masteries = account.Loadout.Masteries is null ? null : new Dictionary<int, int>(account.Loadout.Masteries),
        },
        RunePages = account.RunePages?.Select(CopyPage).ToArray(),
        Economy = account.Economy is null ? null : PlatformEconomy.Copy(account.Economy),
        Dodges = account.Dodges?.ToArray(),
    };

    private void Save(State state)
    {
        // ponytail: rewrite the small private-group store; use transactional SQLite if its size becomes material.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        using (var stream = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(_path + ".tmp", _path, true);
        _state = state;
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private PlatformAccount RequireAccount(long accountId) =>
        _state.Accounts.FirstOrDefault(account => account.Id == accountId)
        ?? throw new ArgumentException("Account does not exist.", nameof(accountId));

    private void ValidateEconomyData(PlatformEconomyData data)
    {
        if (data.Ip is < 0 or > PlatformEconomy.MaxBalance || data.Rp is < 0 or > PlatformEconomy.MaxBalance ||
            data.Level is < 1 or > 30 || data.Experience < 0 ||
            (data.Level < 30 && data.Experience >= EconomyRules.ExperienceToNextLevel(data.Level)) ||
            data.IpBoostEndDate < 0 || data.XpBoostEndDate < 0 ||
            data.IpBoostPerWinCount is < 0 or > PlatformEconomy.MaxBoostWinCount ||
            data.XpBoostPerWinCount is < 0 or > PlatformEconomy.MaxBoostWinCount)
            throw new InvalidDataException("Account economy balances or progression are invalid.");
        if (data.OwnedChampions?.Any(id => id <= 0) == true || data.OwnedSkins?.Any(id => id <= 0) == true ||
            data.OwnedRunes?.Any(pair => pair.Key <= 0 || pair.Value is < 1 or > 9) == true)
            throw new InvalidDataException("Account economy ownership is invalid.");
        if (data.Purchases?.Any(purchase => purchase is null) == true ||
            data.Purchases?.GroupBy(purchase => purchase.RequestId).Any(group => group.Key == Guid.Empty || group.Count() != 1) == true ||
            data.Purchases?.Any(purchase => string.IsNullOrWhiteSpace(purchase.ItemId) ||
                purchase.Currency is not ("IP" or "RP") || purchase.Price < 0 || purchase.Quantity is < 1 or > 9 ||
                (purchase.Quantity != 1 && !purchase.ItemId.StartsWith("rune:", StringComparison.OrdinalIgnoreCase))) == true ||
            data.Grants?.Any(grant => grant is null) == true ||
            data.Grants?.GroupBy(grant => grant.RequestId).Any(group => group.Key == Guid.Empty || group.Count() != 1) == true ||
            data.Grants?.Any(grant => grant.Ip < 0 || grant.Rp < 0) == true)
            throw new InvalidDataException("Account economy audit data is invalid.");
    }

    private static void RequireSummoner(PlatformAccount account, long summonerId)
    {
        if (account.SummonerId != summonerId)
            throw new ArgumentException("Summoner does not belong to this account.", nameof(summonerId));
    }

    /// <param name="gateEveryPage">A page save equips every page it carries; a page selection equips only the page that
    /// becomes current, so an older over-tier page elsewhere in the book does not stop the player from switching.</param>
    private PlatformRunePage[] NormalizePages(PlatformAccount account, IReadOnlyList<PlatformRunePage> pages, bool gateEveryPage)
    {
        var existing = RunePagesFor(account);
        if (pages.Count != existing.Length)
            throw new ArgumentException("Rune page count cannot change.", nameof(pages));
        var existingById = existing.ToDictionary(page => page.PageId);
        var seen = new HashSet<long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<PlatformRunePage>(pages.Count);
        foreach (var page in pages)
        {
            if (page is null) throw new ArgumentException("Rune page cannot be null.", nameof(pages));
            if (!seen.Add(page.PageId) || !existingById.TryGetValue(page.PageId, out var stored))
                throw new ArgumentException("Rune page IDs must match the stored book exactly.", nameof(pages));
            var name = page.Name?.Trim() ?? throw new ArgumentException("Rune page name is required.", nameof(pages));
            if (name.Length is 0 or > 128 || name.Any(char.IsControl))
                throw new ArgumentException("Rune page names must contain 1–128 non-control characters.", nameof(pages));
            if (name.Contains(ReservedPageNameToken, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Rune page name uses a reserved token.", nameof(pages));
            if (!names.Add(name))
                throw new ArgumentException("Rune page names must be unique.", nameof(pages));
            var slots = CopySlots(page.Slots);
            ValidateSlots(account, slots, gateEveryPage || page.IsCurrent);
            normalized.Add(new PlatformRunePage(page.PageId, name, page.IsCurrent, slots, stored.CreatedAt));
        }
        if (seen.Count != existingById.Count || normalized.Count(page => page.IsCurrent) != 1)
            throw new ArgumentException("A rune book must contain exactly one current page.", nameof(pages));
        return normalized.OrderBy(page => page.PageId).ToArray();
    }

    private void ValidateSlots(PlatformAccount account, IReadOnlyDictionary<int, int> slots, bool equipping)
    {
        if (slots.Keys.Any(slot => slot is < 1 or > RunePage.TotalSlots) || slots.Values.Any(id => id <= 0))
            throw new ArgumentException("Rune slots must be numbered 1–30 and contain positive IDs.", nameof(slots));
        var groups = Groups(slots);
        ValidateEquippedRunes(account, equipping, groups.Red, groups.Yellow, groups.Blue, groups.Black);
        MatchRoster.ValidateLoadout(account.Loadout with
        {
            RedRunes = groups.Red,
            YellowRunes = groups.Yellow,
            BlueRunes = groups.Blue,
            BlackRunes = groups.Black,
        });
    }

    private void ValidateOwnedChampionAndSkin(PlatformAccount account, MatchPlayer loadout)
    {
        var championId = PlatformEconomy.ChampionId(loadout.Champion);
        if (championId <= 0 || !PlatformEconomy.CanPlay(account, championId, EconomyRules))
            throw new ArgumentException("The account does not own that champion.", nameof(loadout));
        if (!PlatformEconomy.OwnsSkin(account, checked(championId * 1000 + loadout.SkinId)) && loadout.SkinId != 0)
            throw new ArgumentException("The account does not own that skin.", nameof(loadout));
    }

    private static void ValidateSpellUnlocks(MatchPlayer loadout)
    {
        foreach (var spell in new[] { loadout.SummonerSpell1, loadout.SummonerSpell2 })
        {
            var minimumLevel = AirDtos.SpellMinimumLevel(spell);
            if (loadout.SummonerLevel < minimumLevel)
                throw new ArgumentException($"Summoner level {loadout.SummonerLevel} cannot select {spell}; level {minimumLevel} is required.", nameof(loadout));
        }
    }

    private static bool SameRunes(MatchPlayer a, MatchPlayer b) =>
        (a.RedRunes ?? []).SequenceEqual(b.RedRunes ?? []) && (a.YellowRunes ?? []).SequenceEqual(b.YellowRunes ?? []) &&
        (a.BlueRunes ?? []).SequenceEqual(b.BlueRunes ?? []) && (a.BlackRunes ?? []).SequenceEqual(b.BlackRunes ?? []);

    /// <summary>
    /// Every rune an account puts on a page or loadout. When <paramref name="equipping"/> (the runes are newly equipped, not merely
    /// carried along unchanged by a save of something else) a rune must be a regular store rune of October 2011 or one of the
    /// limited-edition window runes (the other event, promotional and unknown records are hidden, also for an all-unlocked account)
    /// and its tier must be open at the account's own level (tier 2 from level 10, tier 3 from level 20:
    /// <see cref="RunePage.MinimumLevel"/>), read from the stored economy and never from the request. Unless the account owns
    /// everything it must always own enough copies; a window rune is never part of "everything", so its copies are always counted.
    /// Reconstruction: a window rune that was bought stays equippable after its window closed (retail kept limited-edition
    /// runes in the inventory); only the sale is windowed.
    /// NOT RETAIL: the stock AIR client has no tier or hidden-rune check when a rune is dropped on a slot and ships no message for a
    /// refusal (MessageDictionary has no rune, level or store code; saveSpellBook has no error handler), so a refusal is the
    /// generic service error. Owner decision 18 October 2026 (evening); owned runes above the tier stay owned, only equipping them
    /// is refused until the level is reached.
    /// </summary>
    private static void ValidateEquippedRunes(PlatformAccount account, bool equipping, params int[]?[] groups)
    {
        var runes = RuneCatalogForCombining.Value;
        var level = PlatformEconomy.For(account).Level;
        var owned = account.Economy is null || account.Economy.AllUnlocked ? null : account.Economy.OwnedRunes ?? [];
        var bought = account.Economy?.OwnedRunes ?? [];
        var windowRunes = PlatformStoreCalendar.Default.WindowRuneIds;
        var used = new Dictionary<int, int>();
        foreach (var group in groups)
        foreach (var id in group ?? [])
        {
            if (id <= 0) continue;
            var windowed = windowRunes.Contains(id) && !runes.IsRegular(id);
            if (equipping && !runes.IsRegular(id) && !windowed) throw new ArgumentException($"Rune {id} is not available.", nameof(groups));
            if (equipping && runes.Get(id) is { } rune && level < RunePage.MinimumLevel(rune.Tier))
                throw new ArgumentException($"{rune.DisplayName} is a tier {(int)rune.Tier} rune and needs summoner level {RunePage.MinimumLevel(rune.Tier)}; this account is level {level}.", nameof(groups));
            if (owned is null && !windowed) continue;
            used[id] = used.GetValueOrDefault(id) + 1;
            if ((windowed ? bought : owned!).GetValueOrDefault(id) < used[id])
                throw new ArgumentException($"The account does not own enough copies of rune {id}.", nameof(groups));
        }
    }

    private static MatchPlayer ApplyPage(MatchPlayer loadout, PlatformRunePage page, string summoner)
    {
        var groups = Groups(page.Slots);
        return loadout with
        {
            Name = summoner,
            SummonerLevel = loadout.SummonerLevel,
            IsBot = false,
            RedRunes = groups.Red,
            YellowRunes = groups.Yellow,
            BlueRunes = groups.Blue,
            BlackRunes = groups.Black,
        };
    }

    private static Dictionary<int, int> SlotsFromLoadout(MatchPlayer loadout)
    {
        var slots = new Dictionary<int, int>();
        Add(slots, loadout.RedRunes, 1);
        Add(slots, loadout.YellowRunes, 10);
        Add(slots, loadout.BlueRunes, 19);
        Add(slots, loadout.BlackRunes, 28);
        return slots;

        static void Add(Dictionary<int, int> target, IEnumerable<int>? ids, int start)
        {
            if (ids is null) return;
            var offset = 0;
            foreach (var id in ids)
            {
                if (id > 0) target[start + offset] = id;
                offset++;
            }
        }
    }

    private static bool CompactSlotsEqual(IReadOnlyDictionary<int, int> left, IReadOnlyDictionary<int, int> right)
    {
        var a = Groups(left);
        var b = Groups(right);
        return a.Red.SequenceEqual(b.Red) && a.Yellow.SequenceEqual(b.Yellow) &&
            a.Blue.SequenceEqual(b.Blue) && a.Black.SequenceEqual(b.Black);
    }

    private static (int[] Red, int[] Yellow, int[] Blue, int[] Black) Groups(IReadOnlyDictionary<int, int> slots) =>
        (Group(slots, 1, RunePage.RedSlots), Group(slots, 10, RunePage.YellowSlots),
         Group(slots, 19, RunePage.BlueSlots), Group(slots, 28, RunePage.BlackSlots));

    private static int[] Group(IReadOnlyDictionary<int, int> slots, int first, int count) =>
        Enumerable.Range(first, count).Where(slots.ContainsKey).Select(slot => slots[slot]).ToArray();

    private static Dictionary<int, int> CopySlots(IReadOnlyDictionary<int, int>? slots) =>
        slots is null ? [] : slots.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static PlatformRunePage[] SeedRunePages(MatchPlayer loadout)
    {
        var createdAt = DateTime.UtcNow;
        return
        [
            new PlatformRunePage(FirstRunePageId, DefaultRunePageName, true, SlotsFromLoadout(loadout), createdAt),
            new PlatformRunePage(SecondRunePageId, SecondRunePageName, false, [], createdAt),
        ];
    }

    private static PlatformRunePage[] VirtualRunePages(MatchPlayer loadout) =>
    [
        new PlatformRunePage(FirstRunePageId, DefaultRunePageName, true, SlotsFromLoadout(loadout), DateTime.UnixEpoch),
        new PlatformRunePage(SecondRunePageId, SecondRunePageName, false, [], DateTime.UnixEpoch),
    ];

    private static PlatformRunePage CopyPage(PlatformRunePage page) =>
        new(page.PageId, page.Name, page.IsCurrent, CopySlots(page.Slots), page.CreatedAt);
    public void Dispose() => _lease.Dispose();
}
