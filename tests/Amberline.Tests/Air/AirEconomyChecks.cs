using System.Text.Json;
using System.Reflection;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

public static class AirEconomyChecks
{
    [Check("server.air-store-http-quantity-fulfillment")]
    public static void HttpPurchasesValidateFormsAndPushOnlyPurchasedQuantity() =>
        HttpPurchaseAsync().GetAwaiter().GetResult();

    private static async Task HttpPurchaseAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-store-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("httpbuyer", "testing-password", "HTTP Buyer", maxed: false);
            var rune = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE" && item.MinimumLevel == 1 &&
                accounts.StoreCatalog.GetRuneQuantityLimit(item.ContentId) == 9 && item.IpPrice > 0);
            accounts.GrantCurrency(account.Id, rune.IpPrice!.Value * 9L, 0, Guid.NewGuid());
            accounts.Purchase(account.Id, rune.Id, "IP", Guid.NewGuid());
            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("buyer", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
            TestAssert.True(sessions.AuthenticateChannel("buyer", account.Username, session.Token));
            var access = sessions.RedeemStoreTicket(sessions.IssueStoreTicket("buyer"));
            var pushes = new List<(long AccountId, AsObject Data)>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { context.Response.StatusCode = 400; }
            });
            PlatformStore.Map(app, accounts, sessions, root, (id, value) => pushes.Add((id, (AsObject)value)));
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Add("Cookie", PlatformStore.StoreCookie + "=" + access.Token);
                var request = Guid.NewGuid();
                async Task<System.Net.HttpStatusCode> Buy(string quantity, Guid identity, string? formToken = null)
                {
                    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["itemId"] = rune.Id, ["currency"] = "IP", ["quantity"] = quantity,
                        ["requestId"] = identity.ToString(), ["formToken"] = formToken ?? access.FormToken,
                    });
                    using var response = await client.PostAsync("/store/purchase", form);
                    return response.StatusCode;
                }
                TestAssert.Equal(System.Net.HttpStatusCode.OK, await Buy("3", request));
                TestAssert.Equal(System.Net.HttpStatusCode.OK, await Buy("3", request));
                TestAssert.Equal(2, pushes.Count, "one fulfillment and balance push, none on replay");
                TestAssert.True(pushes.All(push => push.AccountId == account.Id));
                var fulfillment = pushes.Single(push => push.Data.TypeName.EndsWith(".StoreFulfillmentNotification", StringComparison.Ordinal)).Data;
                var delta = (AsObject)fulfillment["data"];
                TestAssert.Equal(3, delta["quantity"], "the stock inventory adds a delta, not total ownership");
                TestAssert.Equal(4, PlatformEconomy.For(accounts.Get(account.Id)).OwnedRunes![rune.ContentId]);
                var snapshot = File.ReadAllText(Path.Combine(root, "accounts.json"));
                foreach (var invalid in new[] { "0", "10", "1.5", "garbage" })
                    TestAssert.Equal(System.Net.HttpStatusCode.BadRequest, await Buy(invalid, Guid.NewGuid()));
                TestAssert.Equal(System.Net.HttpStatusCode.BadRequest, await Buy("2", request));
                TestAssert.Equal(System.Net.HttpStatusCode.BadRequest, await Buy("1", Guid.NewGuid(), "wrong-form-token"));
                sessions.Logout("buyer");
                TestAssert.Equal(System.Net.HttpStatusCode.BadRequest, await Buy("1", Guid.NewGuid()));
                TestAssert.Equal(snapshot, File.ReadAllText(Path.Combine(root, "accounts.json")));
                TestAssert.Equal(2, pushes.Count);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-rune-quantity-atomic-replay")]
    public static void RuneQuantitiesDebitOnceAndRespectRemainingCapacity()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-rune-purchase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long accountId;
            int runeId;
            long remainingIp;
            var request = Guid.NewGuid();
            using (var accounts = new PlatformAccounts(root))
            {
                var account = accounts.RegisterPublic("runebuyer", "testing-password", "Rune Buyer", maxed: false);
                accountId = account.Id;
                var rune = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE" && item.MinimumLevel == 1 &&
                    accounts.StoreCatalog.GetRuneQuantityLimit(item.ContentId) == 9 && item.IpPrice > 0);
                runeId = rune.ContentId;
                var price = rune.IpPrice!.Value;
                accounts.GrantCurrency(accountId, price * 9L, 0, Guid.NewGuid());
                var receipt = accounts.Purchase(accountId, rune.Id, "IP", request, 6);
                TestAssert.Equal(6, receipt.Quantity);
                TestAssert.Equal(price * 6L, receipt.Price);
                TestAssert.Equal(receipt, accounts.Purchase(accountId, rune.Id, "IP", request, 6));
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(accountId, rune.Id, "IP", request, 5));
                TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(accountId, rune.Id, "IP", Guid.NewGuid(), 4));
                TestAssert.Throws<ArgumentOutOfRangeException>(() => accounts.Purchase(accountId, rune.Id, "IP", Guid.NewGuid(), 0));
                TestAssert.Throws<ArgumentOutOfRangeException>(() => accounts.Purchase(accountId, rune.Id, "IP", Guid.NewGuid(), 10));
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(accountId, "champion:1", "IP", Guid.NewGuid(), 2));
                var state = PlatformEconomy.For(accounts.Get(accountId));
                TestAssert.Equal(6, state.OwnedRunes![runeId]);
                TestAssert.Equal(price * 3L, state.Ip);
                TestAssert.Equal(1, state.Purchases!.Length);
                accounts.Purchase(accountId, rune.Id, "IP", Guid.NewGuid(), 3);
                TestAssert.Equal(9, PlatformEconomy.For(accounts.Get(accountId)).OwnedRunes![runeId]);
                accounts.GrantCurrency(accountId, 100_000, 0, Guid.NewGuid());
                TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(accountId, rune.Id, "IP", Guid.NewGuid()));
                var quint = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE" && item.MinimumLevel == 1 &&
                    accounts.StoreCatalog.GetRuneQuantityLimit(item.ContentId) == 3);
                TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(accountId, quint.Id, "IP", Guid.NewGuid(), 4));
                accounts.Purchase(accountId, quint.Id, "IP", Guid.NewGuid(), 3);
                remainingIp = PlatformEconomy.For(accounts.Get(accountId)).Ip;
            }
            using (var reopened = new PlatformAccounts(root))
            {
                var state = PlatformEconomy.For(reopened.Get(accountId));
                TestAssert.Equal(9, state.OwnedRunes![runeId]);
                TestAssert.Equal(remainingIp, state.Ip);
                TestAssert.Equal(6, state.Purchases!.Single(purchase => purchase.RequestId == request).Quantity);
            }
            // Existing account files predate the optional Quantity field.
            var legacy = JsonSerializer.Deserialize<PlatformPurchase>("""
                {"RequestId":"12345678-1234-1234-1234-123456789abc","ItemId":"rune:5001","Currency":"IP","Price":30,"PurchasedAt":"2011-10-05T00:00:00Z"}
                """);
            TestAssert.Equal(1, legacy!.Quantity);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-rune-page-cap-is-ten")]
    public static void RunePageCapIsTenAndAnAccountAboveANewCapKeepsItsPages()
    {
        // Owner decision, 25 October 2026: 1.0.0.126 allows ten rune pages; the cap rose to twenty only in 1.0.0.129.
        var root = Path.Combine(Path.GetTempPath(), "amberline-rune-page-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"MaximumRunePages\":14}");
            long accountId;
            using (var accounts = new PlatformAccounts(root))
            {
                accountId = accounts.RegisterPublic("pagecap", "testing-password", "Page Cap", maxed: false).Id;
                accounts.GrantCurrency(accountId, 0, 100_000, Guid.NewGuid());
                foreach (var page in accounts.StoreCatalog.Items.Where(item => item.Kind == "SPELL_BOOK_PAGE").OrderBy(item => item.ContentId))
                {
                    if (accounts.GetRunePages(accountId).Length == 14) break;
                    if (accounts.GetRunePages(accountId).Any(owned => owned.PageId == page.ContentId)) continue;
                    accounts.Purchase(accountId, page.Id, "RP", Guid.NewGuid());
                }
                TestAssert.Equal(14, accounts.GetRunePages(accountId).Length, "a host configured for fourteen pages sells them");
            }

            // Retiring the configured cap: the default is the rule for 1.0.0.126 and the stored pages stay.
            File.Delete(Path.Combine(root, "economy.json"));
            using (var accounts = new PlatformAccounts(root))
            {
                TestAssert.Equal(10, accounts.EconomyRules.MaximumRunePages, "the default cap is ten rune pages");
                TestAssert.Equal(10L, accounts.StoreCatalog.Items.Where(item => item.Kind == "SPELL_BOOK_PAGE").Max(item => (long)item.ContentId),
                    "the store sells rune pages up to the cap");
                TestAssert.Equal(14, accounts.GetRunePages(accountId).Length, "an account above the cap keeps every page it owns");
                accounts.GrantCurrency(accountId, 0, 10_000, Guid.NewGuid());
                var refusal = "";
                try { accounts.Purchase(accountId, PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid()); }
                catch (InvalidOperationException error) { refusal = error.Message; }
                TestAssert.True(refusal.Contains("room for all seven", StringComparison.Ordinal), "an account above the cap cannot add pages: " + refusal);
                TestAssert.Equal(14, accounts.GetRunePages(accountId).Length, "a refused purchase adds nothing");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-rune-page-bundle-atomic-replay")]
    public static void RunePageBundleAddsSevenPagesOnceAndUsesFullSpellBookFulfillment()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "amberline-rune-page-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"MaximumRunePages\":12}");
            long accountId;
            PlatformPurchase receipt;
            int[] beforeIds;
            using (var accounts = new PlatformAccounts(root))
            {
                var account = accounts.RegisterPublic("bundlebuyer", "testing-password", "Bundle Buyer", maxed: false);
                accountId = account.Id;
                beforeIds = PlatformAccounts.RunePagesFor(account).Select(page => (int)page.PageId).ToArray();
                TestAssert.Equal(2, beforeIds.Length);
                TestAssert.Equal(1L, PlatformAccounts.RunePagesFor(account).Single(page => page.IsCurrent).PageId);
                accounts.GrantCurrency(accountId, 0, 2_600, Guid.NewGuid());
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(accountId,
                    PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid(), 2));

                var request = Guid.NewGuid();
                var concurrentReceipts = Enumerable.Range(0, 4).AsParallel()
                    .Select(_ => accounts.Purchase(accountId, PlatformStoreCatalog.RunePageBundleId, "RP", request))
                    .ToArray();
                receipt = concurrentReceipts[0];
                TestAssert.True(concurrentReceipts.All(value => value == receipt), "concurrent replay returns one receipt");
                TestAssert.Equal(1, receipt.Quantity);
                TestAssert.Equal(2_600L, receipt.Price);
                TestAssert.Equal(receipt, accounts.Purchase(accountId, PlatformStoreCatalog.RunePageBundleId, "rp", request));

                var updated = accounts.Get(accountId);
                var pages = PlatformAccounts.RunePagesFor(updated);
                TestAssert.Equal(9, pages.Length);
                TestAssert.Equal(1, pages.Count(page => page.IsCurrent));
                TestAssert.Equal(1L, pages.Single(page => page.IsCurrent).PageId);
                TestAssert.Equal(7, pages.Select(page => page.PageId).Except(beforeIds.Select(id => (long)id)).Count());
                TestAssert.Equal(0L, PlatformEconomy.For(updated).Rp);
                TestAssert.Equal(1, PlatformEconomy.For(updated).Purchases!.Count(purchase => purchase.ItemId == PlatformStoreCatalog.RunePageBundleId));

                var fulfillment = Fulfillment(accounts.StoreCatalog.Get(PlatformStoreCatalog.RunePageBundleId), updated,
                    PlatformEconomy.For(updated));
                TestAssert.Equal("SPELL_BOOK_PAGE", fulfillment["inventoryType"]);
                var spellBook = (AsObject)fulfillment["data"];
                TestAssert.Equal("com.riotgames.platform.summoner.spellbook.SpellBook", spellBook.TypeName);
                TestAssert.Equal(9, ((ArrayCollection)spellBook["spellBookPages"]).Count);

                accounts.GrantCurrency(accountId, 0, 2_600, Guid.NewGuid());
                var pagesBeforeCapacityFailure = PlatformAccounts.RunePagesFor(accounts.Get(accountId));
                TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(accountId,
                    PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid()));
                TestAssert.Equal(2_600L, PlatformEconomy.For(accounts.Get(accountId)).Rp);
                TestAssert.True(pagesBeforeCapacityFailure.Select(page => page.PageId)
                    .SequenceEqual(PlatformAccounts.RunePagesFor(accounts.Get(accountId)).Select(page => page.PageId)));
            }

            using (var reopened = new PlatformAccounts(root))
            {
                var account = reopened.Get(accountId);
                TestAssert.Equal(9, PlatformAccounts.RunePagesFor(account).Length);
                TestAssert.Equal(1, PlatformEconomy.For(account).Purchases!.Count(purchase => purchase.ItemId == PlatformStoreCatalog.RunePageBundleId));
                var maxed = reopened.RegisterPublic("bundlemaxed", "testing-password", "Bundle Maxed", maxed: true);
                reopened.GrantCurrency(maxed.Id, 0, 2_600, Guid.NewGuid());
                TestAssert.Throws<InvalidOperationException>(() => reopened.Purchase(maxed.Id,
                    PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid()));
                // Batch 706: a full account carries every rune page up to the host's cap (twelve in this root), not two.
                TestAssert.Equal(12, PlatformAccounts.RunePagesFor(reopened.Get(maxed.Id)).Length);
            }

            // A sparse legacy account proves that allocation uses the first holes, while the
            // lowered-cap pass proves the count guard is checked before free-ID allocation.
            var sparseRoot = Path.Combine(Path.GetTempPath(), "amberline-rune-page-bundle-sparse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sparseRoot);
            try
            {
                File.WriteAllText(Path.Combine(sparseRoot, "economy.json"), "{\"MaximumRunePages\":12}");
                long sparseAccountId;
                using (var seeded = new PlatformAccounts(sparseRoot))
                    sparseAccountId = seeded.RegisterPublic("sparsebundle", "testing-password", "Sparse Bundle", maxed: false).Id;

                var sparseStatePath = Path.Combine(sparseRoot, "accounts.json");
                var sparseState = JsonSerializer.Deserialize<PlatformAccounts.State>(
                    File.ReadAllText(sparseStatePath), MatchRoster.JsonOptions)!;
                var sparseAccount = sparseState.Accounts.Single(account => account.Id == sparseAccountId);
                var sparsePages = new[]
                {
                    new PlatformRunePage(1, "Default", true, [], DateTime.UtcNow),
                    new PlatformRunePage(11, "Page 2", false, [], DateTime.UtcNow),
                };
                File.WriteAllText(sparseStatePath, JsonSerializer.Serialize(sparseState with
                {
                    Accounts = sparseState.Accounts.Select(account => account.Id == sparseAccountId
                        ? sparseAccount with { RunePages = sparsePages } : account).ToArray(),
                }, MatchRoster.JsonOptions));

                using (var sparse = new PlatformAccounts(sparseRoot))
                {
                    sparse.GrantCurrency(sparseAccountId, 0, 2_600, Guid.NewGuid());
                    sparse.Purchase(sparseAccountId, PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid());
                    var pages = PlatformAccounts.RunePagesFor(sparse.Get(sparseAccountId));
                    TestAssert.Equal(9, pages.Length);
                    TestAssert.Equal(1L, pages.Single(page => page.IsCurrent).PageId);
                    TestAssert.True(pages.Any(page => page.PageId == 11 && page.Name == "Page 2"),
                        "the existing high-ID page remains intact");
                    foreach (var pageId in Enumerable.Range(2, 7))
                        TestAssert.True(pages.Any(page => page.PageId == pageId), $"bundle fills hole {pageId}");
                    TestAssert.Equal("Page 2 (2)", pages.Single(page => page.PageId == 2).Name,
                        "bundle avoids an existing page-name collision");
                }

                // Reset to the two sparse pages and lower the configured cap to eight. Seven
                // free IDs remain, but the account still cannot fit seven additional pages.
                sparseState = JsonSerializer.Deserialize<PlatformAccounts.State>(
                    File.ReadAllText(sparseStatePath), MatchRoster.JsonOptions)!;
                sparseAccount = sparseState.Accounts.Single(account => account.Id == sparseAccountId);
                var loweredEconomy = sparseAccount.Economy! with { Rp = 2_600 };
                File.WriteAllText(sparseStatePath, JsonSerializer.Serialize(sparseState with
                {
                    Accounts = sparseState.Accounts.Select(account => account.Id == sparseAccountId
                        ? sparseAccount with { RunePages = sparsePages, Economy = loweredEconomy } : account).ToArray(),
                }, MatchRoster.JsonOptions));
                File.WriteAllText(Path.Combine(sparseRoot, "economy.json"), "{\"MaximumRunePages\":8}");

                using (var lowered = new PlatformAccounts(sparseRoot))
                {
                    TestAssert.Throws<InvalidOperationException>(() => lowered.Purchase(sparseAccountId,
                        PlatformStoreCatalog.RunePageBundleId, "RP", Guid.NewGuid()));
                    var unchanged = lowered.Get(sparseAccountId);
                    TestAssert.Equal(2_600L, PlatformEconomy.For(unchanged).Rp,
                        "capacity failure does not debit RP");
                    TestAssert.True(PlatformAccounts.RunePagesFor(unchanged).Select(page => page.PageId)
                        .SequenceEqual(new long[] { 1, 11 }), "capacity failure does not grant partial pages");
                }
            }
            finally
            {
                if (Directory.Exists(sparseRoot)) Directory.Delete(sparseRoot, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AsObject Fulfillment(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState state)
    {
        var method = typeof(PlatformStore).GetMethod("Fulfillment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(PlatformStore), "Fulfillment");
        return (AsObject)(method.Invoke(null, [item, account, state, 1])
            ?? throw new InvalidOperationException("Fulfillment returned null."));
    }

    [Check("server.air-economy-purchase-and-reward-reopen")]
    public static void FreshAccountPurchaseAndResultRewardAreAtomicAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-economy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long accountId;
            Guid purchaseRequest;
            PlatformResult receipt;
            using (var accounts = new PlatformAccounts(root))
            {
                var account = accounts.RegisterPublic("fresh", "testing-password", "Fresh", maxed: false);
                accountId = account.Id;
                var state = PlatformEconomy.For(account);
                TestAssert.False(state.AllUnlocked);
                TestAssert.Equal(1, state.Level);
                TestAssert.Equal(0L, state.Ip);
                TestAssert.Equal(0, state.OwnedChampions!.Length);

                var championPrice = accounts.StoreCatalog.Get("champion:61").IpPrice!.Value;
                var grant = accounts.GrantCurrency(account.Id, championPrice + 1_000, 0, Guid.NewGuid());
                TestAssert.Equal(championPrice + 1_000L, grant.Ip);
                purchaseRequest = Guid.NewGuid();
                var purchase = accounts.Purchase(account.Id, "champion:61", "IP", purchaseRequest);
                TestAssert.Equal("champion:61", purchase.ItemId);
                TestAssert.Equal(purchase, accounts.Purchase(account.Id, "champion:61", "ip", purchaseRequest));
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(account.Id, "champion:1", "IP", purchaseRequest));

                var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 90,
                    [Player(account)], false);
                receipt = new PlatformResult(1, Guid.NewGuid(), [account.Id],
                    JsonSerializer.SerializeToElement(result, MatchRoster.JsonOptions));
                TestAssert.True(accounts.RecordResult(receipt));
                TestAssert.False(accounts.RecordResult(receipt with { Result = receipt.Result.Clone() }));
                var rewarded = PlatformEconomy.For(accounts.Get(account.Id));
                TestAssert.Equal(1_021L, rewarded.Ip);
                TestAssert.Equal(10, rewarded.Experience);
                TestAssert.Equal(1, rewarded.Level);
            }

            using (var reopened = new PlatformAccounts(root))
            {
                var account = reopened.Get(accountId);
                var state = PlatformEconomy.For(account);
                TestAssert.Equal(1_021L, state.Ip);
                TestAssert.Equal(new[] { 61 }, state.OwnedChampions);
                TestAssert.Equal(1, reopened.ResultsFor(accountId).Length);
                TestAssert.Equal(10, reopened.FindResult(receipt.HostGameId)!.Rewards![accountId].XpEarned);
                TestAssert.Throws<InvalidOperationException>(() => reopened.Purchase(accountId, "champion:61", "IP", Guid.NewGuid()));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-economy-custom-reward-release-gate")]
    public static void CustomRewardSwitchLeavesNormalQueueRewardsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-economy-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"CustomGameRewards\":false}");
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("gate", "testing-password", "Gate", maxed: false);
            var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 90,
                [Player(account)], false);
            TestAssert.True(accounts.RecordResult(new(1, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(result, MatchRoster.JsonOptions), QueueId: 1)));
            TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(account.Id)).Ip,
                "custom-game rewards are disabled by the release gate");
            TestAssert.True(accounts.RecordResult(new(2, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(result, MatchRoster.JsonOptions), QueueId: 2)));
            TestAssert.True(PlatformEconomy.For(accounts.Get(account.Id)).Ip > 0,
                "normal matchmaking rewards remain available");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-practice-quota-budget-and-reset")]
    public static void PracticeGamesDrawOnAFixedDailyBudgetThatRefillsAtTheResetHour()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-practice-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"PracticeMinutesPerDay\":10,\"PracticeResetHourUtc\":3}");
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("quota", "testing-password", "Quota", maxed: false);
            var noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
            MatchResult Match(double seconds) => new(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", seconds,
                [Player(account) with { SummonerSpells = ["SummonerFlash", "SummonerDot"] }], false);
            PlatformResult Game(long id, double seconds, int queue, DateTimeOffset at) => new(id, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(Match(seconds), MatchRoster.JsonOptions), at, queue);
            long Ip() => PlatformEconomy.For(accounts.Get(account.Id)).Ip;
            PlatformPracticeQuota Quota(DateTimeOffset at) => PlatformEconomy.Practice(accounts.Get(account.Id), accounts.EconomyRules, at);

            var fresh = Quota(noon);
            TestAssert.Equal(600d, fresh.SecondsLeft);
            TestAssert.Equal(15 * 3_600_000L, fresh.MillisecondsUntilReset, "the budget refills at 03:00 UTC");

            // A win of 6 minutes pays 18 + 2.312 * 6 = 31.87 IP; the 6 minutes fit the 10 minute budget.
            TestAssert.True(accounts.RecordResult(Game(1, 360, 1, noon)));
            TestAssert.Equal(32L, Ip());
            // 4 of the next 6 minutes fit: two thirds of the full reward (21.25), and the budget is gone.
            TestAssert.True(accounts.RecordResult(Game(2, 360, 1, noon.AddMinutes(30))));
            TestAssert.Equal(53L, Ip());
            TestAssert.Equal(0d, Quota(noon.AddMinutes(60)).SecondsLeft);
            TestAssert.Equal(600d, Quota(noon.AddMinutes(60)).SecondsSpent);
            // Past the budget the game is still recorded but pays nothing and spends nothing.
            TestAssert.True(accounts.RecordResult(Game(3, 360, 1, noon.AddMinutes(60))));
            TestAssert.Equal(53L, Ip());
            // Queue games are not on the practice budget. This first queue win also claims First Win of the Day (150 IP, batch 549).
            TestAssert.True(accounts.RecordResult(Game(4, 360, 2, noon.AddMinutes(90))));
            TestAssert.Equal(85L + PlatformEconomy.FirstWinBonusIp, Ip());
            TestAssert.Equal(0d, Quota(noon.AddMinutes(90)).SecondsLeft);

            // The end-of-game screen shows the real remaining amount; queue games keep the panel hidden.
            var member = new PlatformGames.Member(accounts.Get(account.Id), Team.Order);
            var lobby = new PlatformGames.Lobby(1, "Quota", "", account.Id, 1) { Members = [member], Result = Match(360) };
            var dto = AirGameDtos.Results(lobby, member, null, accounts.EconomyRules, null, noon.AddMinutes(60));
            TestAssert.Equal(0d, dto["practiceMinutesLeftToday"]);
            TestAssert.Equal(10d, dto["practiceMinutesPlayedToday"]);
            TestAssert.Equal(14 * 3_600_000d, dto["practiceMsecsUntilReset"]);
            lobby.QueueId = 2;
            TestAssert.Equal(-1d, AirGameDtos.Results(lobby, member, null, accounts.EconomyRules, null, noon)["practiceMinutesLeftToday"]);

            // After the reset hour the budget is whole again and the next game pays in full.
            var refilled = noon.AddHours(16);
            TestAssert.Equal(600d, Quota(refilled).SecondsLeft);
            TestAssert.True(accounts.RecordResult(Game(5, 360, 1, refilled)));
            TestAssert.Equal(117L + PlatformEconomy.FirstWinBonusIp, Ip());
            TestAssert.Equal(240d, Quota(refilled).SecondsLeft);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static PlayerMatchResult Player(PlatformAccount account) => new(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner,
        Champion: "Annie", Team: "Order", SkinId: 0, Connected: true, Level: 1,
        Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0,
        OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
        Statistics: new HeroMatchStatistics().Snapshot(0), SummonerSpells: [], SpellRanks: [0, 0, 0, 0],
        Inventory: []);
}
