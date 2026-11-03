using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Update now (batch 666). GitHub is a fake release server (<see cref="FakeReleases"/>, an <see cref="HttpMessageHandler"/>): no check
/// reaches the network. Its releases are small synthetic zips with their <c>.sha256</c>; the downloaded installer is never started (a
/// list stands in), and the data folders are temporary.
/// </summary>
public static class HostUpdateNowChecks
{
    private static readonly Version Running = new(1, 10, 0), Newer = new(1, 11, 0);
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T12:00:00Z");

    [Check("server.host-update-now-installs-only-a-checked-release", TimeoutSeconds = 120)]
    public static void UpdateNowStartsOnlyTheProjectsOwnReleaseWhoseHashSizeAndVersionAreRight() => ReleaseAsync().GetAwaiter().GetResult();

    private static async Task ReleaseAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-update-now");
        try
        {
            var data = Path.Combine(root, "data");
            var name = HostUpdates.ZipName(Newer);
            var (zipUrl, hashUrl) = (HostUpdates.Asset(Newer, name), HostUpdates.Asset(Newer, name + ".sha256"));
            TestAssert.Equal("https://github.com/Amberline-Dev/Project-Amberline/releases/download/v1.11.0/Amberline-1.11.0-win-x64.zip", zipUrl);
            var github = FakeReleases.For(root, Newer);
            var zip = github.Files[zipUrl].Body;
            using var client = new HttpClient(github);
            var started = new List<string>();
            // One click (owner): the downloaded Start Amberline.cmd starts as a double-click would, with the page's confirmation.
            bool Start(System.Diagnostics.ProcessStartInfo start)
            {
                TestAssert.True(start is { UseShellExecute: true, Arguments: HostInstall.ConfirmedOption } &&
                    start.WorkingDirectory == Path.GetDirectoryName(start.FileName), $"the start: {start.FileName} {start.Arguments} in {start.WorkingDirectory}");
                started.Add(start.FileName);
                return true;
            }

            // The check keeps the release's own two files and the zip's size, as GitHub's API names them.
            var state = await HostUpdates.CheckAsync(data, client, At, force: true);
            TestAssert.Equal((zipUrl, hashUrl, (long?)zip.Length), (state.Zip, state.Hash, state.Size), "the release's assets");
            TestAssert.Equal(null, HostUpdates.Refusal(state, Running), "a newer release with its files can be installed");

            async Task Refused(HostUpdateState release, Version? running, string expected, string why)
            {
                var before = started.Count;
                TestAssert.Equal(expected, await HostUpdates.UpdateAsync(data, client, release, running, Start), why);
                TestAssert.Equal(before, started.Count, why + ": nothing was started");
                TestAssert.False(Directory.Exists(HostUpdates.UpdatesFolder(data)), why + ": nothing is left in the data folder");
            }

            // Only the release's own addresses. An answer naming any other gives no download; a changed updates.json is refused unasked.
            foreach (var other in new[] { "https://example.test/" + name, "https://github.com/someone/Project-Amberline/releases/download/v1.11.0/" + name,
                         "http://github.com/Amberline-Dev/Project-Amberline/releases/download/v1.11.0/" + name, HostUpdates.Asset(new(1, 11, 1), name) })
            {
                github.Files[HostUpdates.LatestRelease] = (HttpStatusCode.OK, FakeReleases.Api("v1.11.0", other, hashUrl, zip.Length));
                var elsewhere = await HostUpdates.CheckAsync(data, client, At, force: true);
                TestAssert.Equal((null, null, (long?)null), (elsewhere.Zip, elsewhere.Hash, elsewhere.Size), $"a zip at {other} is not kept");
                TestAssert.Equal(HostUpdates.NoDownload, HostUpdates.Refusal(elsewhere, Running));
                TestAssert.False(HostUpdates.Render("", (Running, null), elsewhere).Contains("Update now", StringComparison.Ordinal), "and no Update now");
                var asked = github.Requests.Count;
                await Refused(state with { Zip = other }, Running, HostUpdates.NoDownload, $"an updates.json naming {other}");
                await Refused(state with { Hash = other + ".sha256" }, Running, HostUpdates.NoDownload, $"an updates.json naming {other}.sha256");
                TestAssert.Equal(asked, github.Requests.Count, "an address that is not the release's is never asked");
            }
            github.Files[HostUpdates.LatestRelease] = (HttpStatusCode.OK, FakeReleases.Api("v1.11.0", zipUrl, hashUrl, zip.Length));
            await Refused(state with { Size = HostUpdates.MaxZipBytes + 1 }, Running, HostUpdates.NoDownload, "a zip larger than 1 GB");
            await Refused(state with { Size = null }, Running, HostUpdates.NoDownload, "a zip without a size");

            // Only a newer version: not the same, not an older one, never over a development build.
            await Refused(state, Newer, HostUpdates.NothingNewer, "the version installed already");
            await Refused(state, new(1, 12, 0), HostUpdates.NothingNewer, "a release older than the installed version");
            await Refused(state, null, HostUpdates.NothingNewer, "a development build");
            github.Files[HostUpdates.LatestRelease] = (HttpStatusCode.OK, FakeReleases.Api("v1.9.0", HostUpdates.Asset(new(1, 9, 0), HostUpdates.ZipName(new(1, 9, 0))),
                HostUpdates.Asset(new(1, 9, 0), HostUpdates.ZipName(new(1, 9, 0)) + ".sha256"), zip.Length));
            var older = await HostUpdates.CheckAsync(data, client, At, force: true);
            await Refused(older, Running, HostUpdates.NothingNewer, "GitHub's latest release older than the installed one");
            TestAssert.False(HostUpdates.Render("", (Running, null), older).Contains("Update now", StringComparison.Ordinal), "an older release offers no Update now");
            github.Files[HostUpdates.LatestRelease] = (HttpStatusCode.OK, FakeReleases.Api("v1.11.0", zipUrl, hashUrl, zip.Length));

            // The SHA-256: another hash, the hash of another file, a file that is no hash.
            var good = github.Files[hashUrl];
            foreach (var (text, why) in new[] { ($"{new string('0', 64)}  {name}\n", "a zip whose SHA-256 is not the one its .sha256 names"),
                         ($"{Encoding.ASCII.GetString(good.Body).Split(' ')[0]}  Amberline-1.10.0-win-x64.zip\n", "a .sha256 of another zip"), ("not a hash\n", "a .sha256 that is no hash") })
            {
                github.Files[hashUrl] = (HttpStatusCode.OK, Encoding.ASCII.GetBytes(text));
                await Refused(state, Running, HostUpdates.WrongHash, why);
            }
            github.Files[hashUrl] = good;

            // The size: a download cut off, one longer than GitHub names, and a failed one; and one that left HTTPS on its way.
            github.Files[zipUrl] = (HttpStatusCode.OK, zip[..(zip.Length / 2)]);
            await Refused(state, Running, HostUpdates.Incomplete, "a download cut off half way");
            github.Files[zipUrl] = (HttpStatusCode.OK, [.. zip, 0]);
            await Refused(state, Running, HostUpdates.Incomplete, "a download longer than the release's zip");
            github.Files[zipUrl] = (HttpStatusCode.NotFound, []);
            await Refused(state, Running, HostUpdates.DownloadFailed, "a download GitHub does not have");
            github.Files[zipUrl] = (HttpStatusCode.OK, zip);
            github.EndsAt = "http://example.test/" + name;
            await Refused(state, Running, HostUpdates.DownloadFailed, "a download redirected away from HTTPS");
            github.EndsAt = null;

            // What the zip holds: another version than its tag (newer and older), no Start Amberline.cmd, no zip at all.
            foreach (var (inside, broken, expected) in new[] { ("1.11.1", false, HostUpdates.WrongVersion), ("1.9.0", false, HostUpdates.WrongVersion),
                         ("1.11.0", true, HostUpdates.Damaged) })
            {
                var (bytes, sha) = FakeReleases.Zip(root, inside, broken);
                github.Files[zipUrl] = (HttpStatusCode.OK, bytes);
                github.Files[hashUrl] = (HttpStatusCode.OK, Encoding.ASCII.GetBytes($"{sha}  {name}\n"));
                await Refused(state with { Size = bytes.Length }, Running, expected, $"a zip holding {inside}{(broken ? " without Start Amberline.cmd" : "")}");
            }
            var junk = RandomNumberGenerator.GetBytes(4096);
            github.Files[zipUrl] = (HttpStatusCode.OK, junk);
            github.Files[hashUrl] = (HttpStatusCode.OK, Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(junk)) + "\n"));
            await Refused(state with { Size = junk.Length }, Running, HostUpdates.Damaged, "a download that is no zip");
            github.Files[zipUrl] = (HttpStatusCode.OK, zip);
            github.Files[hashUrl] = good;

            // The release: downloaded with its progress, unpacked in the data folder, the zip deleted, its Start Amberline.cmd started.
            var progress = new Seen();
            TestAssert.Equal(null, await HostUpdates.UpdateAsync(data, client, state, Running, Start, progress));
            var unpacked = Path.Combine(HostUpdates.UpdatesFolder(data), "Amberline-1.11.0");
            TestAssert.Equal(Path.Combine(unpacked, "Start Amberline.cmd"), started.Single(), "the unpacked release's Start Amberline.cmd starts");
            TestAssert.Equal(Newer, HostVersion.Read(Path.Combine(unpacked, "app")).Version);
            TestAssert.False(File.Exists(Path.Combine(HostUpdates.UpdatesFolder(data), name)), "the zip is deleted once unpacked");
            TestAssert.Equal((long)zip.Length, progress.Values.Last(), "the progress reaches the zip's size");
            TestAssert.Equal(null, HostPaths.InstallFolder(Path.Combine(unpacked, "app", "platform")),
                "the unpacked release is no install itself, so its start updates the install (HostInstall.ForRelease)");

            // While that window runs its installer from the folder, Update now changes nothing; afterwards it replaces the folder.
            using (new FileStream(Path.Combine(unpacked, "app", "platform", HostInstall.PlatformFile), FileMode.Open, FileAccess.Read, FileShare.Read))
                TestAssert.Equal(HostUpdates.StillOpen, await HostUpdates.UpdateAsync(data, client, state, Running, Start), "the last update's window is open");
            TestAssert.True(File.Exists(started.Single()) && started.Count == 1, "the open window's release is untouched");
            TestAssert.Equal(null, await HostUpdates.UpdateAsync(data, client, state, Running, Start));
            TestAssert.Equal(2, started.Count);
            TestAssert.Equal("Amberline-1.11.0", string.Join(",", Directory.EnumerateFileSystemEntries(HostUpdates.UpdatesFolder(data)).Select(Path.GetFileName)),
                "one release in the updates folder");
            TestAssert.False(Directory.Exists(HostUpdates.UpdatesFolder(data) + ".old"), "the replaced folder is gone");
            TestAssert.Equal(HostUpdates.NotStarted, await HostUpdates.UpdateAsync(data, client, state, Running, _ => false), "a start Windows refuses");
            TestAssert.False(Directory.Exists(HostUpdates.UpdatesFolder(data)), "a refused start leaves nothing");

            // Every request went to the API or to the release's two files.
            TestAssert.True(github.Requests.All(url => url == HostUpdates.LatestRelease || url == zipUrl || url == hashUrl),
                "requests: " + string.Join(", ", github.Requests.Distinct()));
        }
        finally { Delete(root); }
    }

    [Check("server.host-update-now-page", TimeoutSeconds = 120)]
    public static void UpdatesOffersUpdateNowWarnsAboutGamesAndShowsTheDownload() => PageAsync().GetAwaiter().GetResult();

    private static async Task PageAsync()
    {
        var home = Home.Create("league-host-update-now-page");
        File.WriteAllText(Path.Combine(home.Root, "app", "BUILD.json"), JsonSerializer.Serialize(new { version = Running.ToString(), component = "amberline-host" }));
        var github = FakeReleases.For(home.Root, Newer);
        var started = new List<string>();
        home.Loop.Releases = new HostReleases(github, start => { lock (started) started.Add(start.FileName + " " + start.Arguments); return true; });
        var games = 0;
        var run = Task.Run(() => RunUpdatesAsync(home, () => Volatile.Read(ref games)));
        async Task<int> Watched() => await run ? 1 : 0;
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", Watched());
            using (var stranger = await browser.PostAsync(HostUpdates.InstallPath, null))
                TestAssert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode, "Update now needs the host's session");
            await home.SignInAsync(browser);

            // Check now asks the loop's release server; the page then offers Update now with the version and the size.
            using (var checkNow = await PostAsync(browser, "/host/updates", "/host/updates", ("check", "on"), ("do", "now")))
                TestAssert.Equal(HttpStatusCode.Redirect, checkNow.StatusCode);
            var size = HostUpdates.Read(home.Loop.Paths.Data).Size!.Value;
            var updates = await Page(browser, "/host/updates", HttpStatusCode.OK, "Updates with a newer release");
            Has(updates, "Amberline 1.11.0 is out", $"<form method=post action={HostUpdates.InstallPath} data-wait=\"{WebUtility.HtmlEncode(HostUpdates.Downloading("1.11.0", size))}\">",
                ">Update now</button>", "Open the download page");
            TestAssert.Equal(1, github.Requests.Count, "the page asked only for the latest release");

            // A game in progress: the warning first and a confirm; nothing is downloaded until then.
            Volatile.Write(ref games, 1);
            var confirm = await Form(browser, "/host/updates", HttpStatusCode.OK, "Update now while a game runs");
            Has(confirm, "<h1>Update Amberline now?</h1>", "<p class=warn>Updating ends the game in progress.</p>", "<input type=hidden name=games value=1>",
                ">Update now</button>", ">Cancel</a>");
            Volatile.Write(ref games, 2);
            Has(await Form(browser, confirm, HttpStatusCode.Conflict, "a confirm after another game started", ("games", "1")),
                "A game started since this page was opened.", "Updating ends the games in progress.");
            TestAssert.Equal(1, github.Requests.Count, "nothing was downloaded before the confirm");

            // Confirmed: the download's page and its progress, while the release server holds the zip back.
            github.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var go = await PostAsync(browser, HostUpdates.InstallPath, confirm, ("games", "2")))
                TestAssert.Equal(HostUpdates.InstallPath, go.Headers.Location?.OriginalString, "the confirm starts the update");
            var status = await StatusAsync(browser, state => state == "downloading");
            TestAssert.Equal(HostUpdates.Downloading("1.11.0", size), status.GetProperty("phase").GetString());
            Has(await Page(browser, HostUpdates.InstallPath, HttpStatusCode.OK, "the download's page"), "<h1>Updating Amberline</h1>",
                "<progress id=update-bar max=100", "id=update-phase", "id=update-detail", $"<script src={HostPages.ScriptPath} defer></script>");
            var script = await browser.GetStringAsync(HostPages.ScriptPath);
            TestAssert.True(script.Contains("fetch('/host/updates/install/status.json', { cache: 'no-store' })", StringComparison.Ordinal) &&
                script.Contains("if (byId('update-bar')) setTimeout(update, 1000);", StringComparison.Ordinal), "page.js follows the download: " + script);
            github.Hold.SetResult();
            await StatusAsync(browser, state => state == "started");
            var installing = await Page(browser, HostUpdates.InstallPath, HttpStatusCode.OK, "the started update");
            Has(installing, "<h1>Installing Amberline 1.11.0</h1>", "It installs Amberline 1.11.0 now.", "the one you have comes back.");
            TestAssert.False(installing.Contains("Enter", StringComparison.Ordinal), "one click: the page asks for nothing more: " + installing);
            lock (started)
                TestAssert.Equal(Path.Combine(HostUpdates.UpdatesFolder(home.Loop.Paths.Data), "Amberline-1.11.0", "Start Amberline.cmd") + " " +
                    HostInstall.ConfirmedOption, started.Single());

            // A failed update says why in one sentence; the Amberline running keeps running and nothing was started.
            Volatile.Write(ref games, 0);
            var hashUrl = HostUpdates.Asset(Newer, HostUpdates.ZipName(Newer) + ".sha256");
            github.Files[hashUrl] = (HttpStatusCode.OK, Encoding.ASCII.GetBytes(new string('0', 64) + "\n"));
            using (var again = await PostAsync(browser, HostUpdates.InstallPath, "/host/updates"))
                TestAssert.Equal(HostUpdates.InstallPath, again.Headers.Location?.OriginalString, "no game: Update now starts at once");
            await StatusAsync(browser, state => state == "failed");
            Has(await Page(browser, HostUpdates.InstallPath, HttpStatusCode.OK, "the failed update"), "<h1>Amberline was not updated</h1>",
                WebUtility.HtmlEncode(HostUpdates.WrongHash), "The Amberline you have keeps running.", "<a href=/host/updates>Back</a>");
            lock (started) TestAssert.Equal(1, started.Count, "a failed update starts nothing");
            TestAssert.False(Directory.Exists(HostUpdates.UpdatesFolder(home.Loop.Paths.Data)), "and leaves nothing in the data folder");
        }
        finally
        {
            home.Loop.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(60));
            Delete(home.Root);
        }
    }

    /// <summary>A page-only Updates on the home's page port (the gate, the shell, Updates), as the full Platform maps it, until Stop.</summary>
    private static async Task<bool> RunUpdatesAsync(Home home, Func<int> games)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(HostDashboard.PagePath)) context.Response.Headers.ContentSecurityPolicy = HostDashboard.Policy;
            return next(context);
        });
        app.MapGet("/health", () => Results.Json(new { status = "ready", service = "Amberline.AirPlatform", protocol = 1, mode = "home" }));
        HostGate.Use(app, home.Port, home.Loop.Sessions, "/host/updates");
        HostPages.Use(app, home.Loop, () => "Friday League", nav: true);
        HostUpdates.Map(app, home.Loop, games);
        return await home.Loop.RunAsync(app, home.Port);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient browser, Func<string?, bool> until)
    {
        for (var tries = 0; tries < 200; tries++)
        {
            using var answer = await browser.GetAsync(HostUpdates.StatusPath);
            var status = JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone();
            if (until(status.GetProperty("state").GetString())) return status;
            await Task.Delay(50);
        }
        throw new TimeoutException("Update now's status never reached the state the check waits for");
    }

    private static async Task<string> Page(HttpClient browser, string path, HttpStatusCode status, string what)
    {
        using var answer = await browser.GetAsync(path);
        return await Checked(answer, status, what);
    }

    /// <summary>Update now's form posted with the token of <paramref name="tokenPage"/> (a path to fetch, or a page already fetched).</summary>
    private static async Task<string> Form(HttpClient browser, string tokenPage, HttpStatusCode status, string what, params (string Name, string Value)[] fields)
    {
        using var answer = await PostAsync(browser, HostUpdates.InstallPath, tokenPage, fields);
        return await Checked(answer, status, what);
    }

    private static async Task<string> Checked(HttpResponseMessage answer, HttpStatusCode status, string what)
    {
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(status, answer.StatusCode, $"{what}: {page}");
        HostPagesChecks.Accessible(page, what, string.Join(";", answer.Headers.GetValues("Content-Security-Policy")));
        return page;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string tokenPage, params (string Name, string Value)[] fields)
    {
        var token = Token(tokenPage.StartsWith('/') ? await client.GetStringAsync(tokenPage) : tokenPage);
        var form = new Dictionary<string, string> { [token.Key] = token.Value };
        foreach (var (name, value) in fields) form[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static void Has(string page, params string[] phrases)
    {
        foreach (var phrase in phrases) TestAssert.True(page.Contains(phrase, StringComparison.Ordinal), $"the page does not say <{phrase}>:\n{page}");
    }

    /// <summary>Every value reported, at once (Progress&lt;T&gt; would post them later).</summary>
    private sealed class Seen : IProgress<long>
    {
        public List<long> Values { get; } = [];
        public void Report(long value) => Values.Add(value);
    }

    /// <summary>
    /// GitHub as Update now sees it: its releases API and the release download host, answering each address from <see cref="Files"/> (404
    /// otherwise) and recording every request. <see cref="EndsAt"/> stands for a redirect's final address; <see cref="Hold"/> holds the zip back.
    /// </summary>
    private sealed class FakeReleases : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, byte[] Body)> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public string? EndsAt { get; set; }
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>A release of <paramref name="version"/> with its zip, its .sha256 and the API's answer naming both.</summary>
        public static FakeReleases For(string root, Version version)
        {
            var name = HostUpdates.ZipName(version);
            var (zipUrl, hashUrl) = (HostUpdates.Asset(version, name), HostUpdates.Asset(version, name + ".sha256"));
            var (zip, sha) = Zip(root, version.ToString(), broken: false);
            var github = new FakeReleases();
            github.Files[HostUpdates.LatestRelease] = (HttpStatusCode.OK, Api("v" + version, zipUrl, hashUrl, zip.Length));
            github.Files[zipUrl] = (HttpStatusCode.OK, zip);
            // As the release build writes it (build-server.ps1): the hash, two spaces, the zip's name.
            github.Files[hashUrl] = (HttpStatusCode.OK, Encoding.ASCII.GetBytes($"{sha}  {name}\n"));
            return github;
        }

        /// <summary>The releases API's answer: the tag, the page, the notes and the two assets with their addresses and sizes.</summary>
        public static byte[] Api(string tag, string zipUrl, string hashUrl, long size) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = tag, html_url = $"{HostUpdates.ReleasesPage}/tag/{tag}", body = "Faster games",
            assets = new object[]
            {
                new { name = Path.GetFileName(zipUrl), size, browser_download_url = zipUrl },
                new { name = Path.GetFileName(hashUrl), size = 96, browser_download_url = hashUrl },
            },
        });

        /// <summary>A release zip as the build makes it (Start Amberline.cmd and app\ at its root) whose BUILD.json names <paramref name="inside"/>.</summary>
        public static (byte[] Zip, string Sha256) Zip(string root, string inside, bool broken)
        {
            var folder = Path.Combine(root, "built", Guid.NewGuid().ToString("N"));
            HostInstallChecks.Release(folder, inside);
            if (!broken) File.WriteAllText(Path.Combine(folder, "Start Amberline.cmd"), "@echo off\r\n");
            var zip = folder + ".zip";
            ZipFile.CreateFromDirectory(folder, zip);
            var bytes = File.ReadAllBytes(zip);
            return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (url.EndsWith(".zip", StringComparison.Ordinal) && Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
            var (status, body) = Files.TryGetValue(url, out var file) ? file : (HttpStatusCode.NotFound, []);
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = EndsAt is { } end ? new HttpRequestMessage(HttpMethod.Get, end) : request,
            };
        }
    }
}
