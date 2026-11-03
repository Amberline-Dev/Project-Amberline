using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

/// <summary>
/// The Platform's <c>--home</c> start. Every check runs in a temporary folder on free loopback ports; the browser opener is a list,
/// so no browser starts, and nothing is written to the user's profile or desktop.
/// </summary>
public static class HostModeChecks
{
    [Check("server.host-derived-paths")]
    public static void HomePathsDeriveTheDataFolderServerAndBootstrap()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-paths");
        try
        {
            var local = Path.Combine(root, "Local");
            var app = Path.Combine(root, "app", "platform");
            var paths = HostPaths.Resolve(null, local, app);
            TestAssert.Equal(Path.Combine(local, "Amberline", "data"), paths.Data, "the data folder is in the user's local application data");
            TestAssert.Equal(paths.Data, HostPaths.Resolve("  ", local, app).Data, "a blank AMBERLINE_PLATFORM_HOME is unset");
            TestAssert.Equal(Path.Combine(root, "elsewhere"), HostPaths.Resolve(Path.Combine(root, "elsewhere"), local, app).Data,
                "AMBERLINE_PLATFORM_HOME wins");
            TestAssert.Equal(Path.Combine(paths.Data, "platform.json"), paths.Settings);
            TestAssert.Equal(Path.Combine(paths.Data, "logs"), paths.Logs);
            TestAssert.Equal(Path.Combine(root, "app", "server", OperatingSystem.IsWindows() ? "Amberline.Server.exe" : "Amberline.Server"),
                paths.ServerExecutable, "the match server is beside the Platform's folder");
            TestAssert.Equal(Path.Combine(root, "app", "bootstrap"), paths.Bootstrap, "the friends' archive is beside the Platform's folder");

            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var air = Directory.CreateDirectory(Path.Combine(root, "air")).FullName;
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ServerExecutable)!);
            File.WriteAllText(paths.ServerExecutable, "server");
            Directory.CreateDirectory(paths.Data);
            void Save(object value) => File.WriteAllText(paths.Settings, JsonSerializer.Serialize(value));

            Save(new { dataRoot = game, airRoot = air });
            var (settings, host) = Amberline.Platform.Program.LoadSettings(paths.Settings, paths);
            TestAssert.Equal(paths.ServerExecutable, settings.ServerExecutable, "no serverExecutable means the one beside the program");
            TestAssert.Equal(paths.ServerExecutable, host.ServerExecutable);
            Save(new { dataRoot = game, airRoot = air, serverExecutable = (string?)null });
            TestAssert.Equal(paths.ServerExecutable, Amberline.Platform.Program.LoadSettings(paths.Settings, paths).Settings.ServerExecutable);

            var other = Path.Combine(root, "other-server.exe");
            File.WriteAllText(other, "server");
            Save(new { dataRoot = game, airRoot = air, serverExecutable = other });
            TestAssert.Equal(other, Amberline.Platform.Program.LoadSettings(paths.Settings, paths).Settings.ServerExecutable,
                "a named server is kept");

            Save(new { dataRoot = game });
            TestAssert.Throws<ArgumentException>(() => Amberline.Platform.Program.LoadSettings(paths.Settings, paths),
                "home settings need the AIR folder");
            Save(new { dataRoot = game, airRoot = Path.Combine(root, "gone") });
            TestAssert.Throws<ArgumentException>(() => Amberline.Platform.Program.LoadSettings(paths.Settings, paths),
                "a moved AIR folder is setup again");
            Save(new { dataRoot = game, airRoot = air });
            File.Delete(paths.ServerExecutable);
            TestAssert.Throws<ArgumentException>(() => Amberline.Platform.Program.LoadSettings(paths.Settings, paths),
                "the derived server must still exist");

            // Without --home nothing is derived: the operator's platform.json names the server, as before.
            Save(new { dataRoot = game, serverExecutable = other });
            TestAssert.Equal(other, Amberline.Platform.Program.LoadSettings(paths.Settings).Settings.ServerExecutable);
            Save(new { dataRoot = game, airRoot = air });
            TestAssert.Throws<ArgumentException>(() => Amberline.Platform.Program.LoadSettings(paths.Settings),
                "the operator's start never falls back to a derived server");
        }
        finally { Delete(root); }
    }

    [Check("server.host-setup-mode-loopback-only", TimeoutSeconds = 120)]
    public static void SetupModeListensOnLoopbackAndServesOnlyTheSetupRoutes() => SetupModeAsync().GetAwaiter().GetResult();

    private static async Task SetupModeAsync()
    {
        var home = Home.Create("league-host-setup");
        var saved = File.ReadAllText(home.Loop.Paths.Settings);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var client = home.Client();
            await WaitForHealthAsync(client, "setup", run);
            TestAssert.Equal(1, home.Opened.Count, "the first start opens the page once");
            TestAssert.True(home.Opened[0].StartsWith(home.OpenPrefix, StringComparison.Ordinal), home.Opened[0]);
            TestAssert.True(home.Screen.ToString().Contains("Amberline is running. Closing this window stops it and any game in progress.", StringComparison.Ordinal),
                "the window shows the banner");

            // Since H2 the opened address carries the launch code that signs this client in (HostSessionChecks).
            using (var open = await client.GetAsync(home.Opened[0]))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode);
                // Since H6 /open leads to the first-run steps (HostSteps), which send setup mode on to /setup once the server is named.
                TestAssert.Equal(HostSteps.Start, open.Headers.Location?.OriginalString);
            }
            var page = await client.GetStringAsync("/setup");
            TestAssert.True(page.Contains("<label for=paste>", StringComparison.Ordinal) &&
                page.Contains("Choose the game folder and the AIR client folder.", StringComparison.Ordinal), page);
            foreach (var path in new[] { "/", "/manage", "/register", "/bootstrap/client.json", "/landing", "/store/entry" })
            {
                using var response = await client.GetAsync(path);
                TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, path + " is not a setup route");
            }
            using (var login = await client.PostAsync(Amberline.Platform.Program.LoginQueuePath, new FormUrlEncodedContent([])))
                TestAssert.Equal(HttpStatusCode.NotFound, login.StatusCode, "no sign-in before setup");
            using (var rebound = new HttpRequestMessage(HttpMethod.Get, "/setup"))
            {
                rebound.Headers.Host = "attacker.example";
                TestAssert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(rebound)).StatusCode, "a rebound host name is refused");
            }

            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var pagePort = listeners.Where(endpoint => endpoint.Port == home.Port).ToArray();
            TestAssert.True(pagePort.Length != 0 && pagePort.All(endpoint => IPAddress.IsLoopback(endpoint.Address)),
                "the page's port listens on loopback only");
            foreach (var port in new[] { home.RtmpPort, home.XmppPort, home.RemotePort })
                TestAssert.False(listeners.Any(endpoint => endpoint.Port == port), $"port {port} (RTMP, XMPP or public HTTPS) waits for setup");

            using (var forged = await client.PostAsync("/setup", new FormUrlEncodedContent(
                       new Dictionary<string, string> { ["game"] = home.Game, ["air"] = home.Air })))
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "the form needs its antiforgery token");
            var token = Token(page);
            using (var missing = await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
                       { [token.Key] = token.Value, ["game"] = Path.Combine(home.Root, "nowhere"), ["air"] = home.Air })))
            {
                TestAssert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                TestAssert.True((await missing.Content.ReadAsStringAsync()).Contains("Amberline cannot find that game folder.", StringComparison.Ordinal),
                    "a folder that is not there is named in plain words");
            }
            TestAssert.Equal(saved, File.ReadAllText(home.Loop.Paths.Settings), "a refused form saves nothing");
            TestAssert.Equal(1, home.Loop.Starts, "a refused form does not restart");
        }
        finally { await home.StopAsync(run); }
        TestAssert.False(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == home.Port),
            "Stop releases the page's port");
        Delete(home.Root);
    }

    [Check("server.host-restart-loop-rebinds", TimeoutSeconds = 180)]
    public static void FinishingSetupAndRestartRebindTheFullPlatformInTheSameProcess() => RestartLoopAsync().GetAwaiter().GetResult();

    private static async Task RestartLoopAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        var home = Home.Create("league-host-restart");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var client = home.Client();
            await WaitForHealthAsync(client, "setup", run);
            await home.SignInAsync(client);
            var token = Token(await client.GetStringAsync("/setup"));
            using (var finished = await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
                       { [token.Key] = token.Value, ["game"] = "\"" + home.Game + "\"", ["air"] = " " + home.Air + " " })))
            {
                TestAssert.Equal(HttpStatusCode.OK, finished.StatusCode, await finished.Content.ReadAsStringAsync());
                TestAssert.True((await finished.Content.ReadAsStringAsync()).Contains("http-equiv=refresh", StringComparison.Ordinal),
                    "the page comes back by itself");
            }
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(client, "ready", run);
            using (var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
            {
                TestAssert.Equal(home.Game, json.RootElement.GetProperty("dataRoot").GetString(), "the quoted address is saved unquoted");
                TestAssert.Equal(home.Air, json.RootElement.GetProperty("airRoot").GetString());
                TestAssert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("serverExecutable").ValueKind,
                    "platform.json names no program path");
                TestAssert.Equal(home.Port, json.RootElement.GetProperty("httpPort").GetInt32(), "setup keeps the saved ports");
            }
            await FullModeAnswersAsync(home, client);
            TestAssert.Equal(1, home.Opened.Count, "setup finishing does not open a second browser page");

            home.Loop.Restart();
            await WaitForStartsAsync(home.Loop, 3, run);
            await WaitForHealthAsync(client, "ready", run);
            await FullModeAnswersAsync(home, client);
            TestAssert.Equal(1, home.Opened.Count, "Restart does not open a second browser page");
            TestAssert.Equal(1, Regex.Count(home.Screen.ToString(), "Amberline is running"), "the banner shows once per window");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var port in new[] { home.Port, home.RtmpPort, home.XmppPort, home.RemotePort })
            TestAssert.False(listeners.Any(endpoint => endpoint.Port == port), $"Stop releases port {port}");
        // The run points AIR_CLIENT_ROOT at a synthetic AIR folder (sized stand-ins) for a moment; the champion list must not be cached from it.
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the full Platform's start leaves the champion list unloaded");
        Delete(home.Root);
    }

    internal static bool ChampionCatalogLoaded() =>
        typeof(ChampionCatalog).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is { } lazy &&
        lazy.GetType().GetProperty("IsValueCreated")?.GetValue(lazy) is true;

    private static async Task FullModeAnswersAsync(Home home, HttpClient client)
    {
        using (var open = await client.GetAsync("/open"))
        {
            TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode);
            TestAssert.Equal(HostSteps.Start, open.Headers.Location?.OriginalString,
                "the full Platform's entry is the first-run steps (H6), which end at the dashboard (H7)");
        }
        using (var bootstrap = await client.GetAsync("/bootstrap/client.json"))
        {
            TestAssert.Equal(HttpStatusCode.OK, bootstrap.StatusCode, "the friends' archive is served from the folder beside the program");
            using var json = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
            TestAssert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(home.Loop.Paths.Bootstrap, "companion.zip")))),
                json.RootElement.GetProperty("companionSha256").GetString());
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var port in new[] { home.Port, home.RtmpPort, home.XmppPort, home.RemotePort })
            TestAssert.True(listeners.Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address)),
                $"port {port} is bound again (loopback in this check)");
    }

    [Check("server.host-restart-fresh-process", TimeoutSeconds = 240)]
    public static void ARestartIsANewProcessThatReadsTheSavedFolders() => FreshProcessAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 636, the owner's VM test: the champion list was read while no AIR client was known, the restart after the folder step
    /// stayed in that process, and creating the host's login was a broken page. This check runs setup mode in its own process (server
    /// checks run one per process), reads the list the way that start did, and saves the folders; the restart then starts the real
    /// Platform program, copied into a release layout, which takes the host's login. The dashboard's Restart is another new process,
    /// and its Stop ends every one with exit code 0. The address is home-network only, so no router is asked.
    /// </summary>
    private static async Task FreshProcessAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var first = Home.Create("league-host-fresh");
        var app = first.Loop.Paths.App;
        HostInstall.CopyTree(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Amberline.Platform", "release")), app);
        var program = Path.Combine(app, HostInstall.PlatformFile);
        // The next process reads its champion list from this AIR folder: the real AirGeneratedContent.swf every check reads, from the
        // configured AIR_CLIENT_ROOT (batch 647: the public build has no fallback search for the AIR folder).
        File.Copy(AirChampionData.ResolveSwf(), Path.Combine(first.Air, "assets", "swfs", "AirGeneratedContent.swf"), overwrite: true);
        File.WriteAllText(first.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = first.Port, rtmpPort = first.RtmpPort, xmppPort = first.XmppPort, remotePort = first.RemotePort,
            bindAddress = "127.0.0.1", serverName = "Fresh Test", publicHost = "192.168.1.20", addressSource = HostAddress.Lan,
        }));
        // Stdin is a pipe, so a fatal start never waits for Enter; stdout stays this check's.
        var next = new ProcessStartInfo(program, "--home") { UseShellExecute = false, RedirectStandardInput = true };
        next.Environment["AMBERLINE_PLATFORM_HOME"] = first.Loop.Paths.Data;
        // The next process knows only the saved folders (platform.json's airRoot), as after the VM's folder step; this one keeps its own.
        next.Environment.Remove("AIR_CLIENT_ROOT");
        var home = first.Again(next);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "setup", run);
            await home.SignInAsync(browser);
            // What the VM's start did before its folders were saved: the list read with no AIR client to find, and the failure kept.
            Environment.SetEnvironmentVariable("AIR_CLIENT_ROOT", Path.Combine(home.Root, "no-air"));
            try { _ = ChampionCatalog.All.Count; } catch (IOException) { }
            Environment.SetEnvironmentVariable("AIR_CLIENT_ROOT", saved["AIR_CLIENT_ROOT"]);

            var token = Token(await browser.GetStringAsync("/setup"));
            string reopen;
            using (var folders = await browser.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
                       { [token.Key] = token.Value, ["game"] = home.Game, ["air"] = home.Air })))
                reopen = Refresh(await folders.Content.ReadAsStringAsync());
            TestAssert.True(reopen.StartsWith("/open?c=", StringComparison.Ordinal), "the restarting page carries a fresh launch code: " + reopen);
            await WaitForHealthAsync(browser, "ready", run);
            await SignedInAgainAsync(browser, reopen);
            await StepAsync(browser, HostSteps.Start, "/host/setup/login");
            token = Token(await browser.GetStringAsync("/host/setup/login"));
            using (var login = await browser.PostAsync("/host/setup/login", new FormUrlEncodedContent(new Dictionary<string, string>
                   {
                       [token.Key] = token.Value, ["username"] = "fresh_host", ["summoner"] = "Fresh Host", ["password"] = "Amber line test 636", ["mode"] = "maxed",
                   })))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, login.StatusCode,
                    $"creating the host's login: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}{Log(home)}");
                TestAssert.Equal(HostSteps.Start, login.Headers.Location?.OriginalString);
            }
            await StepAsync(browser, HostSteps.Start, HostDashboard.PagePath);
            using (var dashboard = await browser.GetAsync(HostDashboard.PagePath))
                TestAssert.Equal(HttpStatusCode.OK, dashboard.StatusCode, "the redirect after the login lands on the dashboard");
            using (var signIn = await browser.PostAsync(Amberline.Platform.Program.LoginQueuePath, new FormUrlEncodedContent(
                       new Dictionary<string, string> { ["payload"] = "user=fresh_host,password=Amber line test 636" })))
                TestAssert.Equal(HttpStatusCode.OK, signIn.StatusCode, "the League client's sign-in takes the new login");
            TestAssert.True(home.Loop.HandedOver && home.Loop.Starts == 1, "the full Platform runs in the new process, not in setup mode's");

            // The dashboard's Restart (H7): the next process again, and the page comes back through its fresh code.
            reopen = Refresh(await EndAsync(browser, "restart"));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (await HostMode.ProbeAsync(home.Port) is not null && DateTime.UtcNow < deadline) await Task.Delay(20);
            await WaitForHealthAsync(browser, "ready", run);
            await SignedInAgainAsync(browser, reopen);
            await StepAsync(browser, HostSteps.Start, HostDashboard.PagePath);
            TestAssert.True((await EndAsync(browser, "stop")).Contains("Amberline has stopped", StringComparison.Ordinal), "Stop answers");
            TestAssert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(60)), "Stop ends every process, and the window's exit code is 0");
            TestAssert.Equal(1, home.Opened.Count, "the new processes open no browser page");
            TestAssert.False(home.Screen.ToString().Contains("Amberline could not start again", StringComparison.Ordinal), home.Screen.ToString());
        }
        finally
        {
            home.Loop.Stop();
            // Never left running: a process of this check's own copy only.
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
                using (process)
                    try { if (string.Equals(process.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase)) process.Kill(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            try { await run.WaitAsync(TimeSpan.FromSeconds(60)); } catch (Exception ex) when (ex is TimeoutException) { }
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Delete(home.Root);
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var port in new[] { home.Port, home.RtmpPort, home.XmppPort, home.RemotePort })
            TestAssert.False(listeners.Any(endpoint => endpoint.Port == port), $"Stop releases port {port}");
    }

    /// <summary>A setup page's own refresh address (<c>http-equiv=refresh</c>).</summary>
    private static string Refresh(string page)
    {
        var refresh = Regex.Match(page, "content=\"5;url=([^\"]+)\"");
        TestAssert.True(refresh.Success, "the page comes back by itself: " + page);
        return WebUtility.HtmlDecode(refresh.Groups[1].Value);
    }

    /// <summary>The restarting page's address, as the browser follows it, once the new process answers: it signs the browser in again.</summary>
    private static async Task SignedInAgainAsync(HttpClient browser, string reopen)
    {
        using var open = await browser.GetAsync(reopen);
        TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the fresh code signs the browser in to the new process");
        TestAssert.Equal(HostSteps.Start, open.Headers.Location?.OriginalString);
    }

    private static async Task StepAsync(HttpClient browser, string path, string next)
    {
        using var answer = await browser.GetAsync(path);
        TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, path);
        TestAssert.Equal(next, answer.Headers.Location?.OriginalString, "after " + path);
    }

    /// <summary>The dashboard's Stop or Restart, confirmed with the page's own form (no game runs).</summary>
    private static async Task<string> EndAsync(HttpClient browser, string verb)
    {
        var token = Token(await browser.GetStringAsync($"{HostDashboard.PagePath}/{verb}"));
        using var end = await browser.PostAsync($"{HostDashboard.PagePath}/{verb}", new FormUrlEncodedContent(
            new Dictionary<string, string> { [token.Key] = token.Value, ["games"] = "0" }));
        var page = await end.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, end.StatusCode, page);
        return page;
    }

    /// <summary>The end of the newest log of the data folder, for a failure's message.</summary>
    private static string Log(Home home) =>
        Directory.Exists(home.Loop.Paths.Logs) && Directory.GetFiles(home.Loop.Paths.Logs, "amberline-*.log").Order().LastOrDefault() is { } file
            ? "\nlog: " + string.Concat(new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)).ReadToEnd().TakeLast(4000))
            : "";

    [Check("server.host-single-instance-opens-existing", TimeoutSeconds = 120)]
    public static void ASecondStartOpensTheRunningPageAndExits() => SingleInstanceAsync().GetAwaiter().GetResult();

    private static async Task SingleInstanceAsync()
    {
        var first = Home.Create("league-host-single");
        var run = Task.Run(() => HostMode.RunAsync(first.Loop));
        try
        {
            using var client = first.Client();
            await WaitForHealthAsync(client, "setup", run);
            var opened = new List<string>();
            var screen = new StringWriter();
            var second = new HostLoop(first.Loop.Paths, opened.Add, screen);
            var exit = await HostMode.RunAsync(second).WaitAsync(TimeSpan.FromSeconds(30));
            TestAssert.Equal(0, exit, screen.ToString());
            TestAssert.True(opened.Count == 1 && opened[0].StartsWith(first.OpenPrefix, StringComparison.Ordinal),
                "the second start opens the running Amberline's page");
            // Its launch code is signed with the same key file, so the running page signs that browser in.
            using (var browser = first.Client())
            using (var signedIn = await browser.GetAsync(opened[0]))
                TestAssert.Equal(HostSteps.Start, signedIn.Headers.Location?.OriginalString, "the second start's code opens the running page");
            TestAssert.True(screen.ToString().Contains("already running", StringComparison.Ordinal), screen.ToString());
            TestAssert.Equal(0, second.Starts, "the second start never listens");
            TestAssert.Equal(1, first.Opened.Count, "the first Amberline is untouched");
            await WaitForHealthAsync(client, "setup", run);
        }
        finally { await first.StopAsync(run); }

        // Something else on the port, here an operator's Platform without --home: no page opens, A-NET-01 is shown.
        var other = Home.Create("league-host-foreign");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, other.Port));
        await using var foreign = builder.Build();
        foreign.MapGet("/health", () => Results.Json(new { status = "ready", service = "Amberline.AirPlatform", protocol = 1 }));
        await foreign.StartAsync();
        try
        {
            TestAssert.Equal(2, await HostMode.RunAsync(other.Loop).WaitAsync(TimeSpan.FromSeconds(30)));
            TestAssert.Equal(0, other.Opened.Count, "nothing is opened for a foreign program");
            TestAssert.True(other.Screen.ToString().Contains("(A-NET-01)", StringComparison.Ordinal), other.Screen.ToString());
            TestAssert.Equal(0, other.Loop.Starts);
        }
        finally { await foreign.StopAsync(); }
        TestAssert.Equal(null, await HostMode.ProbeAsync(other.Port), "a free port means no Amberline runs");
        Delete(first.Root);
        Delete(other.Root);
    }

    /// <summary>
    /// A temporary home: data, app folder with a stand-in server and archive, incomplete platform.json on free ports, and a synthetic
    /// AIR folder whose own LolClient.exe is the pin (H3 checks folders before saving them). <c>folders</c>, given the root,
    /// replaces the folder setup.
    /// </summary>
    /// <summary>
    /// DIAGNOSTICS: with <c>AMBERLINE_HOST_PAGES_DUMP</c> set to a folder, every host page a check's browser receives is written there
    /// (one file per method, path, status and content), so a batch can count the pages' words and list their buttons (batch 652).
    /// </summary>
    internal sealed class PageDump : DelegatingHandler
    {
        private static readonly string? Folder = Environment.GetEnvironmentVariable("AMBERLINE_HOST_PAGES_DUMP");

        public static HttpMessageHandler Wrap(HttpMessageHandler inner) => Folder is null ? inner : new PageDump { InnerHandler = inner };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var answer = await base.SendAsync(request, cancellationToken);
            if (answer.Content.Headers.ContentType?.MediaType != "text/html") return answer;
            var page = await answer.Content.ReadAsStringAsync(cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(page)))[..8];
            var path = Regex.Replace(request.RequestUri?.AbsolutePath ?? "", "[^A-Za-z0-9]+", "_");
            Directory.CreateDirectory(Folder!);
            await File.WriteAllTextAsync(Path.Combine(Folder!, $"{request.Method}{path} {(int)answer.StatusCode} {hash}.html"), page, cancellationToken);
            return answer;
        }
    }

    internal sealed class Home
    {
        public required string Root { get; init; }
        public required HostLoop Loop { get; init; }
        public required List<string> Opened { get; init; }
        public required StringWriter Screen { get; init; }
        public required string Game { get; init; }
        public required string Air { get; init; }
        public required int Port { get; init; }
        public required int RtmpPort { get; init; }
        public required int XmppPort { get; init; }
        public required int RemotePort { get; init; }

        /// <param name="network">The dashboard's router and firewall; null tests nothing (a fake router and firewall in the dashboard checks).</param>
        public static Home Create(string prefix, Func<string, FolderSetup>? folders = null, HostNetwork? network = null)
        {
            var root = AirBootstrapChecks.TemporaryDirectory(prefix);
            var paths = HostPaths.Resolve(Path.Combine(root, "data"), Path.Combine(root, "Local"), Path.Combine(root, "app", "platform"));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ServerExecutable)!);
            File.WriteAllText(paths.ServerExecutable, "stand-in server");
            Directory.CreateDirectory(paths.Bootstrap);
            File.WriteAllBytes(Path.Combine(paths.Bootstrap, "companion.zip"), RandomNumberGenerator.GetBytes(64));
            // The game folder is the shipped release the other checks read too, so GAME_DATA_ROOT does not move under them.
            var game = GameDataPaths.ResolveRoot() is var shipped && Directory.Exists(shipped)
                ? shipped : Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var air = HostFolderChecks.Air(Path.Combine(root, "air"));
            var ports = FreePorts(4);
            Directory.CreateDirectory(paths.Data);
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new
            {
                httpPort = ports[0], rtmpPort = ports[1], xmppPort = ports[2], remotePort = ports[3],
                bindAddress = "127.0.0.1", publicHost = "127.0.0.1",
            }));
            return New(root, paths, game, air, ports, folders?.Invoke(root) ?? HostFolderChecks.Pins(game, air), network);
        }

        /// <summary>The same folders and ports in a new loop, as the next process would start; <paramref name="next"/> is what its Restart starts.</summary>
        public Home Again(ProcessStartInfo? next = null) =>
            New(Root, Loop.Paths, Game, Air, [Port, RtmpPort, XmppPort, RemotePort], Loop.Folders, Loop.Network, next);

        /// <summary>Both folders chosen, so the loop starts the full Platform at once (its public HTTPS port on loopback in the checks).</summary>
        public void ChooseFolders() => File.WriteAllText(Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = Port, rtmpPort = RtmpPort, xmppPort = XmppPort, remotePort = RemotePort,
            bindAddress = "127.0.0.1", publicHost = "127.0.0.1", dataRoot = Game, airRoot = Air,
        }));

        private static Home New(string root, HostPaths paths, string game, string air, int[] ports, FolderSetup folders, HostNetwork? network,
            ProcessStartInfo? next = null)
        {
            var opened = new List<string>();
            var screen = new StringWriter();
            return new Home
            {
                Root = root,
                Loop = new HostLoop(paths, url => { lock (opened) opened.Add(url); }, TextWriter.Synchronized(screen))
                    { Folders = folders, Network = network, NextProcess = next },
                Opened = opened, Screen = screen, Game = game, Air = air,
                Port = ports[0], RtmpPort = ports[1], XmppPort = ports[2], RemotePort = ports[3],
            };
        }

        public HttpClient Client() => new(PageDump.Wrap(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false }))
            { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>The start of every address the loop opens: the entry with its launch code after it.</summary>
        public string OpenPrefix => $"http://127.0.0.1:{Port}/open?c=";

        /// <summary>Opens the address the loop gave the "browser", as the browser would, so the client holds the host's session.</summary>
        public async Task SignInAsync(HttpClient client)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Opened.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            TestAssert.True(Opened.Count != 0, "the loop opened the page");
            using var open = await client.GetAsync(Opened[0]);
            TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the launch code signs the browser in");
        }

        public async Task StopAsync(Task<int> run)
        {
            Loop.Stop();
            TestAssert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(60)), Screen.ToString());
        }
    }

    internal static async Task WaitForHealthAsync(HttpClient client, string status, Task<int> run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            TestAssert.False(run.IsCompleted, $"the home loop ended early with {(run.IsCompleted ? run.Result : 0)}");
            try
            {
                last = await client.GetStringAsync("/health");
                using var json = JsonDocument.Parse(last);
                if (json.RootElement.GetProperty("status").GetString() == status)
                {
                    TestAssert.Equal("home", json.RootElement.GetProperty("mode").GetString(), last);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex.Message; }
            await Task.Delay(100);
        }
        throw new TimeoutException($"/health never said {status}: {last}");
    }

    internal static async Task WaitForStartsAsync(HostLoop loop, int starts, Task<int> run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (loop.Starts < starts && DateTime.UtcNow < deadline && !run.IsCompleted) await Task.Delay(50);
        TestAssert.Equal(starts, loop.Starts, "the loop built the next start");
    }

    internal static KeyValuePair<string, string> Token(string page)
    {
        var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
        TestAssert.True(field.Success, "the form carries its antiforgery token");
        return new(field.Groups[1].Value, WebUtility.HtmlDecode(field.Groups[2].Value));
    }

    private static int[] FreePorts(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        var ports = listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        foreach (var listener in listeners) listener.Stop();
        return ports;
    }

    internal static void Delete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
