using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 655, the owner's VM test of 0.1.1: the login step answered 500 until a restart. These checks walk the in-browser setup the
/// way a host does, from a data folder with only its ports, in one process (the checks' restart stays in this process): the name,
/// the folders (used where they are, or copied in), then the login step's page and its form, and on to the address step. The last
/// check is the address step's 500 under load: a save of platform.json while the address watch was reading it.
/// </summary>
public static class HostSetupWalkChecks
{
    [Check("server.host-files-write-waits-for-readers", TimeoutSeconds = 60)]
    public static void AHostFileIsReplacedWhileOthersReadOrWriteIt()
    {
        var folder = AirBootstrapChecks.TemporaryDirectory("league-host-write");
        var path = Path.Combine(folder, "platform.json");
        HostFiles.Write(path, new { n = 0 });
        // A reader holds the file as File.ReadAllText does (the address watch, a page, a virus scanner); it lets go after a moment.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var release = Task.Delay(300).ContinueWith(_ => reader.Dispose());
            HostFiles.Write(path, new { n = 1 });
            release.Wait();
        }
        TestAssert.Equal(1, N(path), "the save waited for the reader and replaced the file");
        // Writers at once (the address step and the router's new address, the update choice and the daily check), beside a reader.
        Parallel.For(0, 200, index =>
        {
            if (index % 2 == 0) HostFiles.Write(path, new { n = index });
            else _ = N(path);
        });
        TestAssert.True(N(path) % 2 == 0, "the file is one whole write");
        TestAssert.Equal(0, Directory.GetFiles(folder, "*.tmp").Length, "no temporary file is left");
        HostModeChecks.Delete(folder);
    }

    /// <summary>
    /// Batch 657: the name step saved while <c>platform.json</c> could not be read. A moment's refusal is waited out and keeps the
    /// other fields; one that lasts, or a damaged file, writes nothing and the page says so. Before, the save wrote the defaults
    /// with the new name, and the ports and the address were gone.
    /// </summary>
    [Check("server.host-save-keeps-unread-settings", TimeoutSeconds = 60)]
    public static void ASaveNeverWritesTheDefaultsOverSettingsItCouldNotRead() => UnreadAsync().GetAwaiter().GetResult();

    private static async Task UnreadAsync()
    {
        var home = HostModeChecks.Home.Create("league-save-unread");
        var settings = home.Loop.Paths.Settings;
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await HostModeChecks.WaitForHealthAsync(browser, "setup", run);
            await home.SignInAsync(browser);
            var token = HostModeChecks.Token(await browser.GetStringAsync("/host/setup/name"));
            Task<HttpResponseMessage> Name(string name) => browser.PostAsync("/host/setup/name", Form(token, ("name", name)));
            // Held as a virus scanner holds it, for a moment: the save waits and keeps the rest.
            using (var held = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var release = Task.Delay(300).ContinueWith(_ => held.Dispose());
                using var saved = await Name("Held Name");
                await release;
                TestAssert.Equal(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }
            using (var json = JsonDocument.Parse(File.ReadAllText(settings)))
            {
                var root = json.RootElement;
                TestAssert.Equal("Held Name", root.GetProperty("serverName").GetString(), "the new name is saved");
                TestAssert.Equal(home.Port, root.GetProperty("httpPort").GetInt32(), "the page's port stays: " + root);
                TestAssert.Equal("127.0.0.1", root.GetProperty("publicHost").GetString(), "the address stays: " + root);
            }
            // Held for longer than the save waits, then damaged: nothing is written, and the page says to try again.
            var before = File.ReadAllText(settings);
            using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
                await RefusedAsync(await Name("Lost Name"));
            TestAssert.Equal(before, File.ReadAllText(settings), "a save that could not read the settings wrote nothing");
            File.WriteAllText(settings, "{ \"serverName\": \"Dam");
            await RefusedAsync(await Name("Lost Name"));
            TestAssert.Equal("{ \"serverName\": \"Dam", File.ReadAllText(settings), "a damaged file is never replaced by the defaults");
            File.WriteAllText(settings, before);
        }
        finally { await home.StopAsync(run); }
        HostModeChecks.Delete(home.Root);
    }

    private static async Task RefusedAsync(HttpResponseMessage answer)
    {
        using (answer)
        {
            var page = await answer.Content.ReadAsStringAsync();
            TestAssert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode, page);
            TestAssert.True(page.Contains("Amberline could not read its settings. Try again.", StringComparison.Ordinal), "the page says why: " + page);
        }
    }

    private static int N(string path)
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.GetProperty("n").GetInt32();
            }
            // A read in the moment of a replace is refused too; the host's readers then fall back (HostMode.ReadOrDefault).
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100) { Thread.Sleep(5); }
    }

    [Check("server.host-setup-walk-keep", TimeoutSeconds = 180)]
    public static void TheSetupWalkMakesTheLoginWithTheFoldersWhereTheyAre() => KeepAsync().GetAwaiter().GetResult();

    private static Task KeepAsync(string prefix = "league-walk-keep", bool language = true) => WalkAsync(prefix, async (folder, browser, token) =>
    {
        using var kept = await browser.PostAsync("/setup", Form(token, ("game", folder.Game), ("air", folder.Air), ("keep", "1")));
        return await kept.Content.ReadAsStringAsync();
    }, language);

    /// <summary>
    /// Batch 657: a game folder with no <c>lol_game_client_en_us</c> anywhere beside it (so no fontconfig) still makes the host's
    /// login. Before, the champion list failed on the missing file and every login answered 500 until a restart.
    /// </summary>
    [Check("server.host-setup-walk-no-language", TimeoutSeconds = 180)]
    public static void TheSetupWalkMakesTheLoginWithoutTheLanguageFolder() => KeepAsync("league-walk-language", language: false).GetAwaiter().GetResult();

    [Check("server.host-setup-walk-copy-in", TimeoutSeconds = 180)]
    public static void TheSetupWalkMakesTheLoginAfterTheCopyIn() => CopyInAsync().GetAwaiter().GetResult();

    private static Task CopyInAsync() => WalkAsync("league-walk-copy", async (folder, browser, token) =>
    {
        using (var started = await browser.PostAsync("/setup/copy", Form(token, ("game", folder.Game), ("air", folder.Air), ("delete", "no"))))
            TestAssert.Equal(HttpStatusCode.Redirect, started.StatusCode, await started.Content.ReadAsStringAsync());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        string page;
        while (!(page = await browser.GetStringAsync("/setup/copy")).Contains("<button>Continue</button>", StringComparison.Ordinal))
        {
            TestAssert.True(DateTime.UtcNow < deadline && !page.Contains("were not copied", StringComparison.Ordinal), "the copy ends: " + page);
            await Task.Delay(50);
        }
        string Hidden(string name) => WebUtility.HtmlDecode(Regex.Match(page, $"name={name} value=\"([^\"]+)\"").Groups[1].Value);
        using var folders = await browser.PostAsync("/setup", Form(HostModeChecks.Token(page), ("game", Hidden("game")), ("air", Hidden("air"))));
        return await folders.Content.ReadAsStringAsync();
    });

    /// <summary>
    /// A one-folder install with a fresh download of the clients (its language files only in their own project beside the game, as
    /// the download has them before the client was ever started): the name, the folder offer, <paramref name="folders"/>' answer (it
    /// returns the restarting page), then the login step.
    /// </summary>
    private static async Task WalkAsync(string prefix,
        Func<HostFolderCopyChecks.OneFolder, HttpClient, KeyValuePair<string, string>, Task<string>> folders, bool language = true)
    {
        var saved = Variables();
        var folder = HostFolderCopyChecks.OneFolder.Create(prefix, downloads =>
            HostFolderCopyChecks.Download(Path.Combine(downloads, "League 2011"), Path.Combine(downloads, "League 2011", "lol_air_client")));
        if (!language) HostModeChecks.Delete(folder.Language);
        var home = folder.Home;
        RealChampionList(folder.Air);
        Fresh(home);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await NameAsync(home, browser, run);
            var token = HostModeChecks.Token(await browser.GetStringAsync("/setup"));
            using (var offer = await browser.PostAsync("/setup", Form(token, ("game", folder.Game), ("air", folder.Air))))
                TestAssert.True((await offer.Content.ReadAsStringAsync()).Contains("Use them where they are", StringComparison.Ordinal), "the copy is offered");
            await LoginAsync(home, browser, run, await folders(folder, browser, token), "walk_host");
            // The catalog is the process's own and may have loaded from another check's folder, so the names are asked of this one.
            if (!language)
                TestAssert.Equal("MonkeyKing Bot", new GameData(new GameDataPaths(folder.Game)).BotName("MonkeyKing"), "the internal name stands in");
        }
        finally
        {
            await home.StopAsync(run);
            Restore(saved);
            HostModeChecks.Delete(folder.Downloads);
        }
        HostModeChecks.Delete(home.Root);
    }

    /// <summary>The AIR folder's champion list is the real one (the stand-in's is one byte), as in a host's AIR client.</summary>
    private static void RealChampionList(string air) =>
        File.Copy(AirChampionData.ResolveSwf(), Path.Combine(air, "assets", "swfs", "AirGeneratedContent.swf"), overwrite: true);

    /// <summary>A first start's platform.json: only its ports, no name, folders, address or login.</summary>
    private static void Fresh(HostModeChecks.Home home) => File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
    {
        httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
    }));

    private static async Task NameAsync(HostModeChecks.Home home, HttpClient browser, Task<int> run)
    {
        await HostModeChecks.WaitForHealthAsync(browser, "setup", run);
        await home.SignInAsync(browser);
        await NextAsync(browser, "/host/setup/name");
        // Setup mode has no account store yet: its login step (a tab left open, a start that fell back) leads on, never a bare 404.
        using (var early = await browser.GetAsync("/host/setup/login"))
            TestAssert.Equal(HostSteps.Start, early.Headers.Location?.OriginalString, "setup mode's login step leads on");
        var token = HostModeChecks.Token(await browser.GetStringAsync("/host/setup/name"));
        using (var name = await browser.PostAsync("/host/setup/name", Form(token, ("name", "Walk Test"))))
            TestAssert.Equal(HttpStatusCode.Redirect, name.StatusCode, await name.Content.ReadAsStringAsync());
        await NextAsync(browser, "/setup");
    }

    /// <summary>After the folder step's restarting page: the new start signs the browser in, and the login step makes the host's login.</summary>
    private static async Task LoginAsync(HostModeChecks.Home home, HttpClient browser, Task<int> run, string restarting, string user)
    {
        var refresh = Regex.Match(restarting, "content=\"5;url=([^\"]+)\"");
        TestAssert.True(refresh.Success, "the folder step restarts: " + restarting);
        await HostModeChecks.WaitForStartsAsync(home.Loop, 2, run);
        await HostModeChecks.WaitForHealthAsync(browser, "ready", run);
        using (var open = await browser.GetAsync(WebUtility.HtmlDecode(refresh.Groups[1].Value)))
            TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the restarting page's code signs the browser in again");
        await NextAsync(browser, "/host/setup/login");
        using var page = await browser.GetAsync("/host/setup/login");
        var text = await page.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, page.StatusCode, $"the login step's page: {text}{home.Screen}");
        using (var login = await browser.PostAsync("/host/setup/login", Form(HostModeChecks.Token(text),
                   ("username", user), ("summoner", "Walk Host"), ("password", "Amber line test 655"), ("mode", "maxed"))))
            TestAssert.Equal(HttpStatusCode.Redirect, login.StatusCode, $"the login step's form: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        await NextAsync(browser, "/host/setup/address");
    }

    private static async Task NextAsync(HttpClient browser, string next)
    {
        using var answer = await browser.GetAsync(HostSteps.Start);
        TestAssert.Equal(next, answer.Headers.Location?.OriginalString, "the next first-run step");
    }

    private static FormUrlEncodedContent Form(KeyValuePair<string, string> token, params (string Name, string Value)[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field.Name, field.Value)).Prepend(token));

    private static Dictionary<string, string?> Variables() =>
        new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    private static void Restore(Dictionary<string, string?> saved)
    {
        foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }
}
