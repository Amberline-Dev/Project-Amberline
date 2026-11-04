using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.AirCompanion;
using Amberline.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 681: the host shares its game and AIR client on the friends' port while Settings' switch is on, and the player's installer
/// downloads them. Synthetic trees only (a made-up game release with its language project, the stock-sized AIR fixture), their own pins
/// through the folder setup's seam, temporary folders, a temporary profile and desktop, free loopback ports and the reserved name
/// amberline.example.test; never the real game or AIR client.
/// </summary>
public static class HostFileShareChecks
{
    private const string Switch = "/host/settings/share";
    private const string Release = "game/projects/lol_game_client/releases/0.0.0.51";
    private const string Exe = Release + "/League of Legends.exe";
    private const string Big = Release + "/DATA/big.bin";
    private const string Server = "Test Server";

    [Check("server.host-file-share", TimeoutSeconds = 300)]
    public static void TheHostSharesExactlyItsGameAndAirClientOnlyWhileTheSwitchIsOn() => ShareAsync().GetAwaiter().GetResult();

    private static async Task ShareAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Host("league-host-file-share", big: true);
        var game = Game(home);
        WriteSettings(home, new { serverName = Server, publicHost = "amberline.example.test", addressSource = HostAddress.Typed, dataRoot = game });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var player = HostSelfRegistrationChecks.Person(home, IPAddress.Loopback);

            // Off, as on every install: nothing under /files; Settings has the switch off and the owner's notice.
            await StatusAsync(player, HttpStatusCode.NotFound, "with the switch off", "/files", Url(Exe));
            var settings = await SettingsAsync(browser, "Settings, sharing off");
            TestAssert.True(settings.Contains("value=on>Share the game files with players</label>", StringComparison.Ordinal) &&
                settings.Contains(HostFileShare.Notice, StringComparison.Ordinal) && !settings.Contains("Shared: the game folder", StringComparison.Ordinal), settings);

            // On: saved, and the manifest is exactly the release folder, its language project and the AIR client's own entries, less
            // what the clients wrote there as they ran; each entry has the file's size and SHA-256.
            await PostAsync(browser, Switch, Token(settings), HttpStatusCode.Redirect, ("on", "on"));
            using (var file = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
                TestAssert.True(file.RootElement.GetProperty("shareFiles").GetBoolean(), "the switch is saved in platform.json");
            var manifest = await ManifestAsync(player);
            var expected = Expected(home);
            TestAssert.Equal(string.Join("\n", expected.Keys.Order(StringComparer.Ordinal)), string.Join("\n", manifest.Keys.Order(StringComparer.Ordinal)),
                "the manifest lists exactly the shared files");
            foreach (var (path, source) in expected)
                TestAssert.Equal((new FileInfo(source).Length, RetailClients.Sha256(source)), manifest[path], path);
            settings = await SettingsAsync(browser, "Settings, sharing on");
            TestAssert.True(settings.Contains("value=on checked>Share the game files with players</label>", StringComparison.Ordinal) &&
                settings.Contains("<p>Shared: the game folder (", StringComparison.Ordinal) && settings.Contains("through your internet upload", StringComparison.Ordinal), settings);

            // A file by its manifest path, whole and as a range (a resumed download), and a name with a space.
            using (var whole = await player.GetAsync(Url(Exe)))
            {
                TestAssert.Equal(HttpStatusCode.OK, whole.StatusCode, Exe);
                TestAssert.Equal("bytes", string.Join(",", whole.Headers.AcceptRanges), "the file says it takes ranges");
                TestAssert.True((await whole.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(File.ReadAllBytes(expected[Exe])), "the whole file");
            }
            using (var range = new HttpRequestMessage(HttpMethod.Get, Url(Exe)))
            {
                range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
                using var answer = await player.SendAsync(range);
                TestAssert.Equal(HttpStatusCode.PartialContent, answer.StatusCode, "a range");
                TestAssert.True((await answer.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(File.ReadAllBytes(expected[Exe]).AsSpan(1000, 1000)), "the range's bytes");
            }
            await StatusAsync(player, HttpStatusCode.OK, "a manifest path with a space", Url(Release + "/DATA/Spells file.inibin"));

            // Never anything but a manifest entry: traversal as it reaches the wire, the files the clients wrote, the AIR folder's other
            // things, the host's data folder and certificate, a folder, another case, an absolute path; nor a POST or the host's own port.
            var data = home.Loop.Paths.Data;
            var secret = File.ReadAllText(home.Loop.Paths.Settings);
            foreach (var target in new[]
                     {
                         "/files/../data/platform.json", "/files/game/../../data/platform.json", "/files/%2e%2e/%2e%2e/data/platform.json",
                         "/files/%2E%2E%2F%2E%2E%2Fdata%2Fplatform.json", "/files/..%5C..%5Cdata%5Cplatform.json", $"/files/{Release}/../../../../../../data/platform.json",
                         $"/files/{Release}/lastCmd.log", $"/files/{Release}/Localization_Errors.txt", $"/files/{Release}/2026-11-04_10-00-00_r3dlog.txt",
                         $"/files/{Release}/DATA/crash.dmp", "/files/air/logs/host.log", "/files/air/notes.txt", "/files/data/platform.json",
                         "/files/remote/host.pfx", "/files/host/page.key", "/files/" + Uri.EscapeDataString(home.Loop.Paths.Settings),
                         "/files/" + Uri.EscapeDataString(expected[Exe]), "/files/" + expected[Exe].Replace('\\', '/'), $"/files/{Release}",
                         $"/files/{Release.ToUpperInvariant()}/League%20of%20Legends.exe", "/files/", "/files//" + Release + "/League%20of%20Legends.exe",
                     })
            {
                // An absolute path names the temporary folder: never in a message.
                var what = target.Contains("%3A", StringComparison.Ordinal) || target.Contains(":/", StringComparison.Ordinal) ? "an absolute path" : target;
                var (status, body) = await RawAsync(home, "GET", target);
                TestAssert.True(status is 404 or 400, $"GET {what} on the friends' port: {status}");
                TestAssert.False(body.Contains("remotePort", StringComparison.Ordinal) || body.Contains(secret, StringComparison.Ordinal), $"GET {what} answered the host's data");
            }
            TestAssert.Equal(404, (await RawAsync(home, "POST", "/files")).Status, "POST /files");
            using (var local = await browser.GetAsync("/files")) TestAssert.Equal(HttpStatusCode.NotFound, local.StatusCode, "/files on the host's own page port");

            // With the switch on, the friends' port still has nothing else of the host: every route but the public list and /files is 404.
            var routes = home.Loop.Routes().OfType<RouteEndpoint>().Select(endpoint => (
                Path: Regex.Replace(endpoint.RoutePattern.RawText ?? "", @"\{[^}]*\}", "x"),
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])).ToArray();
            TestAssert.True(routes.Any(route => route.Path == Switch) && routes.Any(route => route.Path == "/files/x"), "the routes walked include the switch and the files");
            foreach (var (path, methods) in routes)
            {
                if (AirRemoteTunnel.IsPublicPath(path) || path == "/files" || path.StartsWith("/files/", StringComparison.Ordinal)) continue;
                foreach (var method in methods)
                {
                    using var remote = await HostSessionChecks.PacedAsync(browser, () => HostSessionChecks.Request(method, $"https://127.0.0.1:{home.RemotePort}{path}"));
                    TestAssert.Equal(HttpStatusCode.NotFound, remote.StatusCode, $"{method} {path} on the friends' port with sharing on");
                }
            }

            // Four transfers at a time from one address, sixteen from everyone: transfers of a large file, held unread.
            var held = new List<HttpResponseMessage>();
            var people = new List<HttpClient>();
            try
            {
                HttpClient From(int last)
                {
                    var client = HostSelfRegistrationChecks.Person(home, IPAddress.Parse($"127.0.0.{last}"));
                    people.Add(client);
                    return client;
                }
                async Task<HttpResponseMessage> StartAsync(HttpClient client) => await client.GetAsync(Url(Big), HttpCompletionOption.ResponseHeadersRead);
                var first = From(2);
                for (var i = 0; i < HostFileShare.PerAddress; i++) held.Add(await StartAsync(first));
                TestAssert.True(held.All(answer => answer.StatusCode == HttpStatusCode.OK), "four transfers from one address");
                using (var fifth = await StartAsync(first)) TestAssert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode, "a fifth transfer from the same address");
                for (var last = 3; held.Count < HostFileShare.InAll; last++) held.Add(await StartAsync(From(last)));
                TestAssert.True(held.All(answer => answer.StatusCode == HttpStatusCode.OK), "sixteen transfers in all");
                using (var more = await StartAsync(From(40))) TestAssert.Equal(HttpStatusCode.TooManyRequests, more.StatusCode, "a seventeenth transfer, from a new address");
            }
            finally
            {
                foreach (var answer in held) answer.Dispose();
                foreach (var client in people) client.Dispose();
            }
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            HttpStatusCode again;
            do
            {
                using var next = HostSelfRegistrationChecks.Person(home, IPAddress.Parse("127.0.0.2"));
                using var answer = await next.GetAsync(Url(Exe));
                again = answer.StatusCode;
                if (again != HttpStatusCode.OK) await Task.Delay(200);
            } while (again != HttpStatusCode.OK && DateTime.UtcNow < deadline);
            TestAssert.Equal(HttpStatusCode.OK, again, "the transfers' places come back once they end");

            // A copy whose game executable is not the pinned one is not shared: nothing is served, and Settings says why.
            var executable = Path.Combine(game, "League of Legends.exe");
            var genuine = File.ReadAllBytes(executable);
            File.WriteAllBytes(executable, RandomNumberGenerator.GetBytes(genuine.Length));
            await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect, ("on", "on"));
            await StatusAsync(player, HttpStatusCode.NotFound, "with an executable that is not the pinned one", "/files", Url(Exe), Url(Release + "/DATA/file1.bin"));
            settings = await SettingsAsync(browser, "Settings, a copy that cannot be shared");
            TestAssert.True(settings.Contains("<p class=problem role=alert>Your copy cannot be shared: the game folder: The game executable is not the 1.0.0.126 game", StringComparison.Ordinal) &&
                settings.Contains("Nothing is shared.", StringComparison.Ordinal), settings);
            File.WriteAllBytes(executable, genuine);

            // A link in a shared folder could lead anywhere on the host's PC: such a copy is not shared either (where Windows lets this user make one).
            var link = Path.Combine(home.Air, "assets", "data", "link");
            var linked = false;
            try { Directory.CreateSymbolicLink(link, data); linked = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (linked)
            {
                await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect, ("on", "on"));
                await StatusAsync(player, HttpStatusCode.NotFound, "with a link in the AIR client folder", "/files", Url(Exe));
                TestAssert.True((await browser.GetStringAsync(HostSettingsPage.PagePath)).Contains("is a link, which is never shared.", StringComparison.Ordinal), "Settings names the link");
                Directory.Delete(link);
            }
            else Console.WriteLine("server.host-file-share: this Windows user cannot make a link, so the link refusal is not proven on this PC.");

            // Saved again with the copy fixed: shared again. Off: 404 at once, without a restart.
            await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect, ("on", "on"));
            TestAssert.Equal(expected.Count, (await ManifestAsync(player)).Count, "shared again once the copy is the pinned one");
            await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect);
            await StatusAsync(player, HttpStatusCode.NotFound, "with the switch off again", "/files", Url(Exe));
            using (var file = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
                TestAssert.False(file.RootElement.GetProperty("shareFiles").GetBoolean(), "off is saved");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.air-player-download", TimeoutSeconds = 300)]
    public static void APlayerDownloadsTheSharedCopyChecksEveryFileAndInstallsItAsAFoundCopy() => DownloadAsync().GetAwaiter().GetResult();

    private static async Task DownloadAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        ListRefusals();
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Host("league-air-player-download", big: false);
        var game = Game(home);
        WriteSettings(home, new { serverName = Server, publicHost = "amberline.example.test", addressSource = HostAddress.Typed, dataRoot = game, shareFiles = true });
        var root = Path.Combine(home.Root, "player");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using (var browser = HostSessionChecks.Browser(home)) await WaitForHealthAsync(browser, "ready", run);
            var pin = Pin(home);
            var pins = new ClientPins(home.Loop.Folders.GameSha256, home.Loop.Folders.AirSha256);
            var connection = new AirRemoteInvitation("127.0.0.1", home.RemotePort, CertificateSha25664hex: pin);
            var origin = new Uri($"https://127.0.0.1:{home.RemotePort}");
            var recorder = new Recorder(pin);
            using var client = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(30) };
            var offer = await PlayerDownload.OfferAsync(client, origin, pins, CancellationToken.None) ?? throw new InvalidOperationException("the server offers nothing");
            var expected = Expected(home);
            TestAssert.Equal(expected.Count, offer.Files.Count, "the offer is the host's manifest");
            TestAssert.Equal(expected.Values.Sum(path => new FileInfo(path).Length), offer.Bytes, "the offer's size");
            var profile = new PlayerProfile(Path.Combine(root, "profile", "Local"), Path.Combine(root, "profile", "Desktop"));
            Directory.CreateDirectory(profile.Desktop);
            var tool = Directory.CreateDirectory(Path.Combine(root, "bootstrap", "companion")).FullName;
            AirPlayerInstallChecks.WriteTool(tool, "one");

            // A file the host changed after the list was made (same size, other bytes) fails the player's check, and nothing is installed.
            var changed = expected[Release + "/DATA/file3.bin"];
            var genuine = File.ReadAllBytes(changed);
            var bent = genuine.ToArray();
            bent[0] ^= 0x5A;
            File.WriteAllBytes(changed, bent);
            var refused = Path.Combine(root, "Refused");
            await ThrowsAsync<InvalidDataException>(() => PlayerDownload.DownloadAsync(client, origin, offer, PlayerDownload.Plan(refused, offer), pins, Server, null,
                CancellationToken.None), "does not match its SHA-256");
            TestAssert.False(Directory.Exists(Path.Combine(refused, "air")) || Directory.Exists(Path.Combine(refused, "game")) || File.Exists(profile.RecordPath),
                "a refused download installs nothing");
            File.WriteAllBytes(changed, genuine);

            // A resume: a cut file left from before (half the game executable) continues with a range, a stray file goes, and a Cancel
            // after ten files keeps what came; the second run never fetches a finished file again.
            var install = Path.Combine(root, "Install");
            var plan = PlayerDownload.Plan(install, offer);
            var staging = Path.Combine(install, PlayerDownload.Staging);
            var cut = Path.Combine(staging, Exe.Replace('/', '\\')) + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(cut)!);
            var executable = File.ReadAllBytes(expected[Exe]);
            File.WriteAllBytes(cut, executable[..(executable.Length / 2)]);
            var stray = Path.Combine(staging, "air", "stray.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
            File.WriteAllText(stray, "left from another list");
            recorder.Requests.Clear();
            using (var stop = new CancellationTokenSource())
            {
                recorder.After = count => { if (count == 10) stop.Cancel(); };
                await ThrowsAsync<OperationCanceledException>(() => PlayerDownload.DownloadAsync(client, origin, offer, plan, pins, Server, null, stop.Token), "");
                recorder.After = null;
            }
            var firstRun = recorder.Requests.ToList();
            TestAssert.False(File.Exists(stray), "a file the list does not name is removed");
            TestAssert.False(Directory.Exists(plan.Air) || Directory.Exists(plan.Game), "a canceled download installs nothing");
            var finished = offer.Files.Where(entry => File.Exists(Path.Combine(staging, entry.Path.Replace('/', '\\')))).Select(entry => "/files/" + entry.Path).ToHashSet();
            TestAssert.True(finished.Count is > 0 && finished.Count < offer.Files.Count, $"the Cancel came halfway: {finished.Count} of {offer.Files.Count}");
            recorder.Requests.Clear();
            var steps = new Steps();
            await PlayerDownload.DownloadAsync(client, origin, offer, plan, pins, Server, steps, CancellationToken.None);
            var secondRun = recorder.Requests.ToList();
            TestAssert.True(firstRun.Concat(secondRun).Any(request => request.Path == "/files/" + Exe && request.From == executable.Length / 2 &&
                request.Status == HttpStatusCode.PartialContent), "the cut executable continues with a range: " + string.Join(", ", firstRun.Concat(secondRun)));
            TestAssert.False(secondRun.Any(request => finished.Contains(request.Path)), "the second run fetched a finished file again: " + string.Join(", ", secondRun));
            TestAssert.Equal(offer.Files.Count - finished.Count, secondRun.Select(request => request.Path).Distinct().Count(), "the second run fetches the rest");
            TestAssert.True(steps.All.Any(step => step.Text.StartsWith($"Downloading the game from {Server}: ", StringComparison.Ordinal) && step.CanCancel) &&
                steps.All.Last(step => step.Percent is not null).Percent == 100, "the bar's steps: " + string.Join(" | ", steps.All.Select(step => step.Text)));
            TestAssert.False(Directory.Exists(staging), "the staging folder is gone");
            var result = PlayerInstall.Install(profile, plan, connection, Server, tool);
            TestAssert.True(PlayerInstall.ExistingInstall(profile, pins) is not null, "the downloaded install passes the client checks");

            // The same layout as a found copy of the same files: the found copy's own install, from folders where a player keeps them.
            var found = new AirPlayerInstallChecks.Fixture(new PlayerProfile(Path.Combine(root, "found", "Local"), Path.Combine(root, "found", "Desktop")),
                Path.Combine(root, "found", "downloads", "lol_air_client"), Path.Combine(root, "found", "downloads", "live", "projects", "lol_game_client", "releases", "0.0.0.51"),
                Path.Combine(root, "found", "downloads", "live", "projects", "lol_game_client_en_us"), Path.Combine(root, "found", "Install"), tool, pins);
            Directory.CreateDirectory(found.Profile.Desktop);
            foreach (var (path, source) in expected)
            {
                var copy = path.StartsWith("air/", StringComparison.Ordinal) ? Path.Combine(found.Air, path[4..])
                    : Path.Combine(root, "found", "downloads", "live", "projects", path["game/projects/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(source, copy);
            }
            var foundResult = PlayerInstall.Install(found.Profile, found.Plan(), connection, Server, tool);
            TestAssert.Equal(Tree(found.Install), Tree(install), "the download's install and the found copy's differ");
            TestAssert.Equal((Path.GetRelativePath(found.Install, foundResult.Record.Air), Path.GetRelativePath(found.Install, foundResult.Record.Game)),
                (Path.GetRelativePath(install, result.Record.Air), Path.GetRelativePath(install, result.Record.Game)), "the record's folders");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    /// <summary>The player's checks of a list before a byte is downloaded: every path inside the layout, once, sizes that add up, and the pins.</summary>
    private static void ListRefusals()
    {
        var pins = new ClientPins(new string('A', 64), new string('B', 64));
        var good = new List<object> { new { path = Exe, size = 1, sha256 = pins.Game }, new { path = "air/LolClient.exe", size = 2, sha256 = pins.Air } };
        byte[] List(long bytes, params object[] more) => JsonSerializer.SerializeToUtf8Bytes(new { protocol = 1, bytes, files = good.Concat(more) });
        TestAssert.Equal(2, PlayerDownload.Read(List(3), pins).Files.Count, "a good list");
        foreach (var path in new[]
                 {
                     "../evil.dll", Release + "/../../../../evil.dll", "air/../evil.dll", "air/notes.txt", "air/logs2/x", "game/projects/other/x",
                     "C:/Windows/evil.dll", "/air/LolClient.exe", Release + "/DATA\\..\\x", Release + "/CON", Release + "/nul.txt", Release + "/a:stream",
                     Release + "/dots.", Release + "/space ", Release + "//x", Release + "/", "air/logs/../../x", "game/projects/lol_game_client_EN/x",
                     "\\\\server\\share\\x", "air", Release + "/" + new string('x', 240),
                 })
            Throws<InvalidDataException>(() => PlayerDownload.Read(List(3, new { path, size = 0, sha256 = new string('C', 64) }), pins), "outside the game", path);
        Throws<InvalidDataException>(() => PlayerDownload.Read(List(3, new { path = "AIR/lolclient.exe".ToLowerInvariant(), size = 0, sha256 = new string('C', 64) }), pins), "outside the game", "a path twice");
        Throws<InvalidDataException>(() => PlayerDownload.Read(List(4), pins), "do not add up", "a total that is not the sum");
        Throws<InvalidDataException>(() => PlayerDownload.Read(List(3, new { path = Release + "/x", size = 0, sha256 = "not-a-hash" }), pins), "is not valid", "a bad hash");
        Throws<InvalidDataException>(() => PlayerDownload.Read("[]"u8.ToArray(), pins), "not a list of files", "not a list");
        Throws<InstallRefusedException>(() => PlayerDownload.Read(List(3), pins with { Game = new string('D', 64) }), "is not the 1.0.0.126", "another game executable");
    }

    /// <summary>
    /// A home host whose shared folders are synthetic: a game release (the folder step's checks pass) with a name that has a space, its
    /// language project beside it, and the stock-sized AIR fixture; plus what the clients write there as they run, which is never shared.
    /// Their pins are these executables' own.
    /// </summary>
    private static Home Host(string prefix, bool big) => Home.Create(prefix, root =>
    {
        var game = HostFolderChecks.Game(Path.Combine(root, "host-game", "projects", "lol_game_client", "releases", "0.0.0.51"));
        Directory.CreateDirectory(Path.Combine(game, "DATA", "CFG"));
        File.WriteAllText(Path.Combine(game, "DATA", "CFG", "Locale.cfg"), "[General]\r\nLanguageLocaleRegion=en_US\r\n");
        for (var index = 0; index < 12; index++) File.WriteAllBytes(Path.Combine(game, "DATA", $"file{index}.bin"), RandomNumberGenerator.GetBytes(4096 + index));
        File.WriteAllBytes(Path.Combine(game, "DATA", "Spells file.inibin"), RandomNumberGenerator.GetBytes(300));
        File.WriteAllBytes(Path.Combine(game, "LEVELS", "Map1", "empty.bin"), []);
        File.WriteAllText(Path.Combine(game, "client.ver"), "stand-in");
        if (big) using (var file = File.Create(Path.Combine(game, "DATA", "big.bin"))) file.SetLength(64L << 20);
        var language = Path.Combine(root, "host-game", "projects", "lol_game_client_en_us", "releases", "0.0.0.20", "DATA", "Menu");
        Directory.CreateDirectory(language);
        File.WriteAllText(Path.Combine(language, "fontconfig_en_US.txt"), "stand-in");
        foreach (var name in new[] { "lastCmd.log", "maestro-game_client.log", "Localization_Errors.txt", "2026-11-04_10-00-00_r3dlog.txt", "2026-11-04_10-00-00_netlog.txt", @"DATA\crash.dmp" })
            File.WriteAllText(Path.Combine(game, name), "the host's own data");
        var air = Path.Combine(root, "air");
        Directory.CreateDirectory(Path.Combine(air, "logs"));
        File.WriteAllText(Path.Combine(air, "logs", "host.log"), "the host's own login");
        File.WriteAllText(Path.Combine(air, "notes.txt"), "the host's own file beside the AIR client");
        return HostFolderChecks.Pins(game, air);
    });

    private static string Game(Home home) => Path.Combine(home.Root, "host-game", "projects", "lol_game_client", "releases", "0.0.0.51");

    /// <summary>What the manifest must list, by its path, with the host's file: everything but what the clients wrote and the AIR folder's other things.</summary>
    private static Dictionary<string, string> Expected(Home home)
    {
        var projects = Path.Combine(home.Root, "host-game", "projects");
        string[] written = ["lastCmd.log", "maestro-game_client.log", "Localization_Errors.txt", "2026-11-04_10-00-00_r3dlog.txt", "2026-11-04_10-00-00_netlog.txt", "DATA/crash.dmp"];
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(projects, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projects, file).Replace('\\', '/');
            if (!written.Any(name => relative == "lol_game_client/releases/0.0.0.51/" + name)) files["game/projects/" + relative] = file;
        }
        foreach (var file in Directory.EnumerateFiles(home.Air, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(home.Air, file).Replace('\\', '/');
            if (!relative.StartsWith("logs/", StringComparison.Ordinal) && relative != "notes.txt") files["air/" + relative] = file;
        }
        return files;
    }

    private static string Url(string path) => "/files/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static async Task<Dictionary<string, (long Size, string Sha256)>> ManifestAsync(HttpClient player)
    {
        using var answer = await player.GetAsync("/files");
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, "GET /files with sharing on");
        TestAssert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        TestAssert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
        var files = json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(entry => entry.GetProperty("path").GetString()!,
            entry => (entry.GetProperty("size").GetInt64(), entry.GetProperty("sha256").GetString()!), StringComparer.Ordinal);
        TestAssert.Equal(files.Values.Sum(file => file.Item1), json.RootElement.GetProperty("bytes").GetInt64(), "the manifest's total");
        return files;
    }

    /// <summary>Settings as the host's browser gets it, held to the host pages' standard.</summary>
    private static async Task<string> SettingsAsync(HttpClient browser, string what)
    {
        using var answer = await browser.GetAsync(HostSettingsPage.PagePath);
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, page);
        HostPagesChecks.Accessible(page, what, string.Join(";", answer.Headers.GetValues("Content-Security-Policy")));
        return page;
    }

    private static async Task StatusAsync(HttpClient player, HttpStatusCode expected, string when, params string[] paths)
    {
        foreach (var path in paths)
        {
            using var answer = await player.GetAsync(path);
            TestAssert.Equal(expected, answer.StatusCode, $"GET {path} on the friends' port {when}");
        }
    }

    /// <summary>A request exactly as written, past HttpClient's own clean-up of the path, over the pinned TLS of the friends' port.</summary>
    private static async Task<(int Status, string Body)> RawAsync(Home home, string method, string target)
    {
        var pin = Pin(home);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, home.RemotePort);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == pin);
        await tls.AuthenticateAsClientAsync("127.0.0.1");
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(tls, Encoding.UTF8);
        var answer = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var status = Regex.Match(answer, @"^HTTP/1\.1 (\d{3})");
        TestAssert.True(status.Success, $"{method} {target}: no HTTP answer: {answer}");
        return (int.Parse(status.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), answer);
    }

    /// <summary>Every file of an install's two client folders with its SHA-256, by its path in the install.</summary>
    private static string Tree(string install) => string.Join("\n", new[] { "air", "game" }
        .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(install, folder), "*", SearchOption.AllDirectories))
        .Select(file => $"{Path.GetRelativePath(install, file)} {RetailClients.Sha256(file)}").Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>The player's pinned client with a record of each request: its path, the range it asked for and the answer.</summary>
    private sealed class Recorder(string pin) : DelegatingHandler(new SocketsHttpHandler
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
            { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == pin },
    })
    {
        public ConcurrentQueue<(string Path, long? From, HttpStatusCode Status)> Requests { get; } = new();
        public Action<int>? After { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Requests.Enqueue((Uri.UnescapeDataString(request.RequestUri!.AbsolutePath), request.Headers.Range?.Ranges.First().From, response.StatusCode));
            After?.Invoke(Requests.Count);
            return response;
        }
    }

    private sealed class Steps : IProgress<InstallProgress>
    {
        public ConcurrentQueue<InstallProgress> All { get; } = new();
        public void Report(InstallProgress value) => All.Enqueue(value);
    }

    private static void Throws<T>(Action action, string text, string what) where T : Exception
    {
        try { action(); }
        catch (T exception)
        {
            TestAssert.True(exception.Message.Contains(text, StringComparison.Ordinal), $"{what}: refused for another reason: {exception.Message}");
            return;
        }
        throw new InvalidOperationException($"{what}: not refused (expected: {text})");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string text) where T : Exception
    {
        try { await action(); }
        catch (T exception)
        {
            TestAssert.True(exception.Message.Contains(text, StringComparison.Ordinal), $"failed for another reason: {exception.Message}");
            return;
        }
        throw new InvalidOperationException($"did not fail (expected {typeof(T).Name}: {text})");
    }
}
