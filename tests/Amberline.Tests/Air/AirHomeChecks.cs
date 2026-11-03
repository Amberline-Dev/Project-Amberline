using System.Text.Json;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

public static class AirHomeChecks
{
    /// <summary>
    /// Batch 549: the profile's Ladder Rankings button opens riotApplication.ladderURL, which the overlay points at the
    /// Platform's /ladder; the page ranks summoners by recorded wins, encodes names and needs a tunnel token on the public port.
    /// </summary>
    [Check("server.air-ladder-page")]
    public static void LadderUrlOpensTheLocalRankingOfRecordedWins() => LadderAsync().GetAwaiter().GetResult();

    private static async Task LadderAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-ladder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            AirStockLayoutFixture.Write(source);
            Amberline.AirCompanion.AirOverlay.Prepare(new(source, Path.Combine(root, "copy"), "http://127.0.0.1:8390"));
            foreach (var name in new[] { "lol.properties", "locale.properties" })
                TestAssert.True(File.ReadAllText(Path.Combine(root, "copy", name)).Contains("ladderURL=http://127.0.0.1:8390/ladder\r\n", StringComparison.Ordinal), name);
            TestAssert.True(AirRemoteTunnel.IsPublicPath("/ladder") && Amberline.AirCompanion.AirRemoteProxy.IsAllowedPath("/ladder"),
                "a friend's browser reaches it through the companion");

            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            var alice = accounts.RegisterPublic("ladderalice", "testing-password", "Alice");
            var bob = accounts.RegisterPublic("ladderbob", "testing-password", "<b>Bob</b>");
            var game = 0;
            void Play(int queue, string winner) => TestAssert.True(accounts.RecordResult(new PlatformResult(++game, Guid.NewGuid(), [alice.Id, bob.Id],
                JsonSerializer.SerializeToElement(new Amberline.Network.Sessions.MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", winner, 600,
                    [Player(alice, "Order"), Player(bob, "Chaos")], false), Amberline.Server.MatchRoster.JsonOptions), QueueId: queue)));
            Play(2, "Order");
            Play(0, "Order");
            var html = PlatformHome.RenderLadder(accounts);
            TestAssert.True(html.Contains("<td>1</td><td>Alice</td><td>2</td><td>0</td>", StringComparison.Ordinal), "normal and custom wins count");
            TestAssert.True(html.Contains("<td>2</td><td>&lt;b&gt;Bob&lt;/b&gt;</td><td>0</td><td>2</td>", StringComparison.Ordinal), "names are encoded");

            int Port() { using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; }
            var (local, remote) = (Port(), Port());
            var access = new AirRemoteAccess(Path.Combine(root, "remote"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(o => { o.Listen(System.Net.IPAddress.Loopback, local); o.Listen(System.Net.IPAddress.Loopback, remote); });
            await using var app = builder.Build();
            // The public listener is HTTPS; this one is plain loopback, so the test marks its requests as HTTPS.
            app.Use((context, next) => { if (context.Connection.LocalPort == remote) context.Request.Scheme = "https"; return next(context); });
            PlatformHome.MapLadder(app, accounts, remote, access);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                TestAssert.True((await client.GetStringAsync($"http://127.0.0.1:{local}/ladder")).Contains("Ladder Rankings", StringComparison.Ordinal));
                using var anonymous = await client.GetAsync($"http://127.0.0.1:{remote}/ladder");
                TestAssert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode, "no tunnel token, no summoner list");
                using var friend = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{remote}/ladder");
                friend.Headers.Authorization = new("Bearer", access.Issue(bob.Id).Token);
                using var allowed = await client.SendAsync(friend);
                TestAssert.Equal(System.Net.HttpStatusCode.OK, allowed.StatusCode, "a friend's companion carries the token");
            }
            finally { await app.StopAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static Amberline.Network.Sessions.PlayerMatchResult Player(PlatformAccount account, string team) => new(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner,
        Champion: "Annie", Team: team, SkinId: 0, Connected: true, Level: 1,
        Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0,
        OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
        Statistics: new Amberline.Core.HeroMatchStatistics().Snapshot(0),
        SummonerSpells: [account.Loadout.SummonerSpell1, account.Loadout.SummonerSpell2], SpellRanks: [0, 0, 0, 0],
        Inventory: []);


    private sealed class TemporaryCatalog : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "league-air-home-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly PlatformAccounts _accounts;
        public TemporaryCatalog() { Directory.CreateDirectory(_root); _accounts = new PlatformAccounts(_root, _root); }
        public PlatformStoreCatalog Catalog => _accounts.StoreCatalog;
        public void Dispose() { _accounts.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }


    [Check("server.air-home-rss-and-status-shapes")]
    public static void RssAndStatusMatchRecoveredClientReaders()
    {
        var model = PlatformHomeModel.Testing() with
        {
            Notices = [new PlatformHomeNotice("Local notice", "Ready", new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero))]
        };
        var rss = PlatformHome.RenderRss("joj", model);
        TestAssert.True(rss.Contains("<rss version=\"2.0\">", StringComparison.Ordinal));
        TestAssert.True(rss.Contains("<pubDate>Tue, 22 Sep 2026 12:00:00 GMT</pubDate>", StringComparison.Ordinal));
        TestAssert.True(rss.Contains("<item>", StringComparison.Ordinal));

        var status = JsonSerializer.Serialize(PlatformHome.StatusPayload(["Ready"]));
        using var document = JsonDocument.Parse(status);
        var text = document.RootElement.GetProperty("messagesContainer")[0].GetProperty("messages")[0].GetProperty("text").GetString();
        TestAssert.Equal("Ready", text);

        var journal = System.Xml.Linq.XDocument.Parse(PlatformHome.RenderRss("joj"));
        if (!PlatformContent.Has("home/home.json"))
        {
            // Batch 597: without the operator's home content the feeds carry no items, so the client shows no notice bubble.
            TestAssert.Equal("Journal", journal.Descendants("title").First().Value);
            TestAssert.False(journal.Descendants("item").Any() ||
                System.Xml.Linq.XDocument.Parse(PlatformHome.RenderRss("store")).Descendants("item").Any(), "no feed items");
            return;
        }
        var archived = journal.Descendants("item").Single();
        TestAssert.Equal("Volume 1, Issue 29", archived.Element("title")!.Value);
        TestAssert.Equal("30 September, 21 CLE", archived.Element("description")!.Value);
        TestAssert.Equal(2011, DateTimeOffset.Parse(archived.Element("pubDate")!.Value,
            System.Globalization.CultureInfo.InvariantCulture).Year);
        TestAssert.True(archived.Element("link") is null, "the notice bubble ignores the link and the feed names no third-party address");
        TestAssert.True(archived.Element("expireDate") is null, "do not expire archived content using today's date");
        var store = System.Xml.Linq.XDocument.Parse(PlatformHome.RenderRss("store")).Descendants("item").Single();
        TestAssert.True(store.Element("title")!.Value.Length <= 24 && store.Element("description")!.Value.Length <= 30,
            "the client's notice bubble is narrow: a short title and one short line");
    }

    [Check("server.air-home-shipped-image-routing")]
    public static void HomeImagesUseKnownArtWithoutExposingOtherFiles() => ImagesAsync().GetAwaiter().GetResult();

    private static async Task ImagesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-home-images-" + Guid.NewGuid().ToString("N"));
        var backgrounds = Path.Combine(root, "assets", "images", "backgrounds");
        Directory.CreateDirectory(backgrounds);
        var champions = Path.Combine(root, "assets", "images", "champions");
        Directory.CreateDirectory(champions);
        byte[] content = [0xff, 0xd8, 0xff, 0xd9];
        File.WriteAllBytes(Path.Combine(backgrounds, "pvp_landingBackground.jpg"), content);
        File.WriteAllBytes(Path.Combine(backgrounds, "parchment.png"), content);
        File.WriteAllBytes(Path.Combine(champions, "Xerath_Splash_0.jpg"), content);
        File.WriteAllText(Path.Combine(root, "accounts.json"), "private account snapshot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var app = builder.Build();
        PlatformHome.MapImages(app, root);
        app.MapGet("/img/{**path}", () => Results.NotFound());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/img/home/background");
            TestAssert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            TestAssert.Equal(content, await response.Content.ReadAsByteArrayAsync());
            using var xerath = await client.GetAsync("/img/home/feature-101");
            TestAssert.Equal("image/jpeg", xerath.Content.Headers.ContentType?.MediaType);
            TestAssert.Equal(content, await xerath.Content.ReadAsByteArrayAsync());
            using var parchment = await client.GetAsync("/img/home/parchment");
            TestAssert.Equal("image/png", parchment.Content.Headers.ContentType?.MediaType);
            TestAssert.Equal(content, await parchment.Content.ReadAsByteArrayAsync());
            foreach (var key in new[] { "accounts.json", "background.json", "portrait-999", "feature-999", "other-61", "link-getting-started", "link-unknown", "link-accounts.json", "archive-sources.json", "thumb-accounts.json", "archive-home.json", "landing.html" })
            {
                using var rejected = await client.GetAsync("/img/home/" + key);
                TestAssert.Equal(System.Net.HttpStatusCode.NotFound, rejected.StatusCode);
            }
            // Batch 597: the page images are the operator's (content/home); without them only the client's own art above is served.
            if (!PlatformContent.Has("home/sources.json"))
            {
                using var absent = await client.GetAsync("/img/home/archive-menu_icon_1257.png");
                TestAssert.Equal(System.Net.HttpStatusCode.NotFound, absent.StatusCode);
                return;
            }
            // The website's link buttons left the Home with the 1.43 in-client page (batch 83), and their `link-*` keys with them; the
            // operator's icon file itself is still served as an archive original (the loop below checks its bytes against sources.json).
            using var icon = await client.GetAsync("/img/home/archive-menu_icon_1257.png");
            TestAssert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
            TestAssert.Equal(File.ReadAllBytes(Path.Combine(PlatformContent.Home, "menu_icon_1257.png")),
                await icon.Content.ReadAsByteArrayAsync());
            using var archived = JsonDocument.Parse(File.ReadAllText(Path.Combine(PlatformContent.Home, "sources.json")));
            foreach (var entry in archived.RootElement.EnumerateArray())
            {
                var file = entry.GetProperty("file").GetString()!;
                var key = file.StartsWith("thumb-", StringComparison.Ordinal) ? file : "archive-" + file;
                using var image = await client.GetAsync("/img/home/" + key);
                TestAssert.Equal(System.Net.HttpStatusCode.OK, image.StatusCode);
                var bytes = await image.Content.ReadAsByteArrayAsync();
                TestAssert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                    "the operator's original archive bytes must survive the export and HTTP routing");
            }
        }
        finally { await app.StopAsync(); }
    }
}
