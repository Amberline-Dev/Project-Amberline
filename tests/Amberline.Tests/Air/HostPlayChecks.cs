using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL: Play on this PC (Amberline Host H12). The full home Platform runs on free loopback ports with a recorder in place of
/// the companion's start, so no companion, AIR or game is started; the stage is written under the check's temporary data folder and
/// read back by the companion's own stage loader.
/// </summary>
public static class HostPlayChecks
{
    [Check("server.host-local-stage-written", TimeoutSeconds = 240)]
    public static void PlayOnThisPcWritesTheLocalStageAndStartsTheCompanion() => LocalStageAsync().GetAwaiter().GetResult();

    private static async Task LocalStageAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        var home = Home.Create("league-host-play");
        var paths = home.Loop.Paths;
        File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", publicHost = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air,
        }));
        var airBefore = Listing(home.Air);
        var opened = new List<string>();
        var started = new List<ProcessStartInfo>();
        // The recorder hands back this check's own process as the "running" companion: nothing is started, nothing is ever stopped.
        var loop = new HostLoop(paths, url => { lock (opened) opened.Add(url); }, TextWriter.Synchronized(new StringWriter()))
        {
            Folders = home.Loop.Folders,
            Play = new HostPlay(info => { lock (started) started.Add(info); return Process.GetCurrentProcess(); }),
        };
        var run = Task.Run(() => HostMode.RunAsync(loop));
        var stage = HostPlay.StageRoot(paths);
        var companion = HostPlay.CompanionExecutable(paths);
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (opened.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            using (var open = await browser.GetAsync(opened[0])) TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "signed in");

            var page = await browser.GetStringAsync("/host/play");
            TestAssert.True(page.Contains(HostPlay.Button("").Split('>')[0], StringComparison.Ordinal), "the page shows the Play on this PC button");
            var token = Token(page);
            async Task<(HttpStatusCode Status, string Text)> PressAsync(bool withToken = true)
            {
                var form = withToken ? new Dictionary<string, string> { [token.Key] = token.Value } : new Dictionary<string, string>();
                using var answer = await browser.PostAsync("/host/play", new FormUrlEncodedContent(form));
                return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
            }

            // The release's companion is missing (Windows Security may remove an unsigned exe): named, nothing written or started.
            var missing = await PressAsync();
            TestAssert.Equal(HttpStatusCode.Conflict, missing.Status, missing.Text);
            TestAssert.True(missing.Text.Contains(@"companion\Amberline.AirCompanion.exe", StringComparison.Ordinal) &&
                missing.Text.Contains("(A-FILES-06)", StringComparison.Ordinal), missing.Text);
            TestAssert.False(File.Exists(Path.Combine(stage, "stage.json")), "no stage without a companion");
            TestAssert.Equal(0, started.Count);

            Directory.CreateDirectory(Path.GetDirectoryName(companion)!);
            File.WriteAllText(companion, "stand-in companion, never started");
            var expired = await PressAsync(withToken: false);
            TestAssert.Equal(HttpStatusCode.BadRequest, expired.Status, "the form needs its antiforgery token");
            TestAssert.Equal(0, started.Count, "a form without its token starts nothing");

            var first = await PressAsync();
            TestAssert.Equal(HttpStatusCode.OK, first.Status, first.Text);
            TestAssert.True(first.Text.Contains("Your game client is opening", StringComparison.Ordinal) &&
                first.Text.Contains("The first time takes a few minutes", StringComparison.Ordinal) &&
                first.Text.Contains("url=/open", StringComparison.Ordinal), first.Text);
            TestAssert.Equal(1, started.Count, "one press starts one companion");
            var info = started[0];
            TestAssert.Equal(companion, info.FileName, "the companion beside the Platform's folder (app\\companion)");
            TestAssert.Equal(Path.GetDirectoryName(companion), info.WorkingDirectory);
            TestAssert.Equal("--desktop|--stage-root|" + stage, string.Join('|', info.ArgumentList), "--desktop --stage-root <data>\\stage");
            TestAssert.True(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError,
                "no window of its own; its output goes to Amberline's log");
            TestAssert.True(stage.StartsWith(paths.Data + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the stage lives in the data folder");

            var again = await PressAsync();
            TestAssert.Equal(HttpStatusCode.Conflict, again.Status, again.Text);
            TestAssert.True(again.Text.Contains("already open", StringComparison.Ordinal), again.Text);
            TestAssert.Equal(1, started.Count, "a second press while the companion runs starts nothing");
        }
        finally
        {
            loop.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(60));
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the full Platform's start leaves the champion list unloaded");

        // The stage as written, then as the companion reads it: the port from the stage (no platform-data), the host's folders in place.
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "stage.json"))))
        {
            var root = json.RootElement;
            TestAssert.Equal(Path.Combine(home.Game, "League of Legends.exe"), root.GetProperty("stockExecutable").GetString());
            TestAssert.Equal(home.Air, root.GetProperty("airSource").GetString());
            TestAssert.Equal(home.Port, root.GetProperty("httpPort").GetInt32());
            TestAssert.Equal(8393, root.GetProperty("maestroPort").GetInt32());
            TestAssert.Equal("127.0.0.1", root.GetProperty("gameHost").GetString());
        }
        TestAssert.False(Directory.Exists(Path.Combine(stage, "platform-data")), "the stage needs no platform-data");
        var desktop = Amberline.AirCompanion.Program.LoadDesktopStage(stage);
        TestAssert.Equal(home.Port, desktop.HttpPort, "the companion reads the port from the stage");
        TestAssert.Equal(Path.Combine(home.Game, "League of Legends.exe"), desktop.StockExecutable, "the host's game is used in place");
        TestAssert.Equal(Path.Combine(stage, "air"), desktop.AirRoot, "the AIR copy lives in the stage");
        TestAssert.Equal(8393, desktop.MaestroPort);
        TestAssert.Equal(null, desktop.RemoteInvitation, "the host's own play is the local path, never the friends' proxy");
        TestAssert.Equal(home.Air, desktop.AirSource);
        TestAssert.Equal("127.0.0.1", desktop.GameHost, "the host's game reaches the match server on this PC");

        // The companion's AIR copy: made once from the host's folder and pointed at the stage's port, kept while it is complete,
        // made again when the host chooses another AIR folder. The host's folder is only read.
        TestAssert.True(Amberline.AirCompanion.Program.PrepareHostAir(desktop), "the first start makes the AIR copy");
        var properties = File.ReadAllText(Path.Combine(desktop.AirRoot, "lol.properties"));
        TestAssert.True(properties.Contains($"lq_uri=http://127.0.0.1:{home.Port}/login-queue/rest/queue", StringComparison.Ordinal) &&
            properties.Contains("maestro_port=8393", StringComparison.Ordinal), properties);
        TestAssert.False(Amberline.AirCompanion.Program.PrepareHostAir(desktop), "a complete copy of the same folder is kept");
        TestAssert.Equal(airBefore, Listing(home.Air), "the host's AIR folder is unchanged");
        var other = HostFolderChecks.Air(Path.Combine(home.Root, "air-other"));
        HostPlay.WriteStage(paths, new PlatformSettings(home.Game, null!, HttpPort: home.Port, AirRoot: other));
        TestAssert.True(Amberline.AirCompanion.Program.PrepareHostAir(Amberline.AirCompanion.Program.LoadDesktopStage(stage)),
            "another AIR folder makes a new copy");
        TestAssert.Equal(other, File.ReadAllText(Path.Combine(stage, "air-source.txt")));
        Delete(home.Root);
    }

    /// <summary>Every file under a folder with its size, to show a folder was only read.</summary>
    private static string Listing(string folder) => string.Join('\n', Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(folder, file)} {new FileInfo(file).Length}"));
}
