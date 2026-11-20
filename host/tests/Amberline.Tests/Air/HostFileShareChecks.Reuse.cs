using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amberline.AirCompanion;
using Amberline.Platform;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 922 (owner, 20 November 2026: "make restarts reuse the last file check - also make sure it also includes the Launcher"): a start with sharing on
/// takes a file's SHA-256 from the last check's record while its path, size and last write time are unchanged, and Settings' Save checks every file again.
/// Synthetic trees only, as the other share checks; the build's DIAGNOSTICS seam (<see cref="HostFileShare.Checked"/>) says which files it read to hash.
/// </summary>
public static partial class HostFileShareChecks
{
    [Check("server.host-file-share-reuse", TimeoutSeconds = 300)]
    public static void ARestartReusesTheLastCheckAndSaveChecksEveryFileAgain() => ReuseAsync().GetAwaiter().GetResult();

    private static async Task ReuseAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Host("league-host-file-share-reuse", big: false);
        var game = Game(home);
        var launcher = Path.Combine(home.Root, "host-launcher");
        var record = Path.Combine(home.Loop.Paths.Data, "host", "file-share.json");
        WriteSettings(home, new { serverName = Server, publicHost = "amberline.example.test", addressSource = HostAddress.Typed, dataRoot = game, shareFiles = true });
        var builds = new Builds(home.Root);
        HostFileShare.Checked += builds.Seen;
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        var starts = 1;
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var player = HostSelfRegistrationChecks.Person(home, IPAddress.Loopback);
            Task RestartAsync(string? folder) => RestartWithLauncherAsync(home, browser, run, folder, ++starts);

            // The first start with the switch on has no record: it reads every listed file to hash it and keeps what it found.
            var expected = Expected(home);
            var first = await ManifestAsync(player);
            TestAssert.Equal(Keys(expected), Keys(first), "the first start lists the shared files");
            TestAssert.Equal(Sorted(expected.Values), builds.Hashed(), "the first start hashes every listed file");

            // A restart with nothing changed (an update that keeps the data folder is one too) reads no file to hash it and lists the same.
            await RestartAsync(null);
            TestAssert.Equal(Text(first), Text(await ManifestAsync(player)), "a restart lists the same files and hashes");
            TestAssert.Equal("", builds.Hashed(), "a restart with nothing changed read files to hash them");
            TestAssert.True(File.Exists(record), "the check is kept in data\\host");
            var settings = await SettingsAsync(browser, "Settings, shared without a launcher folder");
            TestAssert.True(settings.Contains("<p>Shared: the game folder (", StringComparison.Ordinal) && settings.Contains(NoLauncherLine, StringComparison.Ordinal), settings);

            // Settings while such a restart compares the files says nothing about minutes.
            builds.Hold(_ => true);
            await RestartAsync(null);
            var comparing = await builds.WhileHeldAsync(() =>
            {
                // A start cut short (closed, restarted, updated) leaves the record for the next one.
                TestAssert.True(File.Exists(record), "a reusing start keeps the last record while it checks");
                return SettingsAsync(browser, "Settings while a restart compares the files");
            });
            TestAssert.True(comparing.Contains("comparing the files with its last check", StringComparison.Ordinal) && !comparing.Contains("a few minutes", StringComparison.Ordinal), comparing);
            await ManifestAsync(player);
            TestAssert.Equal("", builds.Hashed(), "a restart with nothing changed read files to hash them");

            // A file changed since (same size, other bytes): only it is read again and the list has its new SHA-256; Settings says why it takes longer.
            var changed = expected[Release + "/DATA/file3.bin"];
            File.WriteAllBytes(changed, RandomNumberGenerator.GetBytes((int)new FileInfo(changed).Length));
            builds.Hold(hashed => hashed);
            await RestartAsync(null);
            var hashing = await builds.WhileHeldAsync(() => SettingsAsync(browser, "Settings while a restart hashes a changed file"));
            TestAssert.True(hashing.Contains("the files that changed since its last check", StringComparison.Ordinal) && hashing.Contains("a few minutes", StringComparison.Ordinal), hashing);
            var after = await ManifestAsync(player);
            TestAssert.Equal(changed, builds.Hashed(), "only the changed file is hashed again");
            TestAssert.Equal((new FileInfo(changed).Length, RetailClients.Sha256(changed)), after[Release + "/DATA/file3.bin"], "the changed file's new SHA-256");

            // A record that cannot be read, or one made for other folders, is ignored: that start hashes every file.
            File.WriteAllText(record, "not a record");
            await RestartAsync(null);
            await ManifestAsync(player);
            TestAssert.Equal(Sorted(expected.Values), builds.Hashed(), "an unreadable record is ignored");
            var other = JsonNode.Parse(File.ReadAllText(record))!.AsObject();
            other["game"] = Path.Combine(home.Root, "another-game");
            File.WriteAllText(record, other.ToJsonString());
            await RestartAsync(null);
            await ManifestAsync(player);
            TestAssert.Equal(Sorted(expected.Values), builds.Hashed(), "a record made for another game folder is ignored");

            // Save under Game files checks every file again (TROUBLESHOOTING's repair after a player's download failed its check), with today's line meanwhile.
            builds.Hold(_ => true);
            await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect, ("on", "on"));
            var checking = await builds.WhileHeldAsync(() => SettingsAsync(browser, "Settings while Save checks every file"));
            TestAssert.True(checking.Contains("<p>Amberline is checking the game files before sharing them. This takes a few minutes.", StringComparison.Ordinal), checking);
            await ManifestAsync(player);
            TestAssert.Equal(Sorted(expected.Values), builds.Hashed(), "Save hashes every file");

            // The 2011 launcher: choosing its folder is a start for other folders, so every file is hashed, the launcher's too; the next restart reuses
            // all of it; a changed launcher file is hashed again, and only it; the launcher's pin still decides on its program's bytes.
            await RestartAsync(launcher);
            foreach (var (path, source) in LauncherExpected(launcher)) expected[path] = source;
            var withLauncher = await ManifestAsync(player);
            TestAssert.Equal(Keys(expected), Keys(withLauncher), "the launcher's files are listed beside the game and the AIR client");
            TestAssert.Equal(Sorted(expected.Values), builds.Hashed(), "a record made without the launcher folder is ignored");
            settings = await SettingsAsync(browser, "Settings, the launcher shared");
            TestAssert.True(settings.Contains("and the 2011 launcher (", StringComparison.Ordinal) && !settings.Contains(NoLauncherLine, StringComparison.Ordinal), settings);
            await RestartAsync(launcher);
            TestAssert.Equal(Text(withLauncher), Text(await ManifestAsync(player)), "a restart lists the same launcher files and hashes");
            TestAssert.Equal("", builds.Hashed(), "a restart with the launcher read files to hash them");

            var slideshow = Path.Combine(launcher, "xml", "slideshow.xml");
            File.AppendAllText(slideshow, " ");
            await RestartAsync(launcher);
            var changedLauncher = await ManifestAsync(player);
            TestAssert.Equal(slideshow, builds.Hashed(), "only the changed launcher file is hashed again");
            TestAssert.Equal((new FileInfo(slideshow).Length, RetailClients.Sha256(slideshow)), changedLauncher["launcher/xml/slideshow.xml"], "the launcher file's new SHA-256");

            var program = Path.Combine(launcher, "LoLLauncher.exe");
            var genuineProgram = File.ReadAllBytes(program);
            var programWritten = File.GetLastWriteTimeUtc(program);
            File.WriteAllBytes(program, RandomNumberGenerator.GetBytes(genuineProgram.Length));
            File.SetLastWriteTimeUtc(program, programWritten);
            await RestartAsync(launcher);
            var pinned = await ManifestAsync(player);
            TestAssert.False(pinned.Keys.Any(path => path.StartsWith("launcher/", StringComparison.Ordinal)), "a launcher whose program is not at its pin is left out, although the record has it");
            TestAssert.Equal("", builds.Hashed(), "the game and the AIR client are still reused");
            TestAssert.True((await SettingsAsync(browser, "Settings, a launcher program changed in place")).Contains("<p class=warn>The 2011 launcher is not shared. The classic launcher is not version ",
                StringComparison.Ordinal), "Settings says why the launcher is left out");
            File.WriteAllBytes(program, genuineProgram);
            File.SetLastWriteTimeUtc(program, programWritten);

            // An executable is always checked on its bytes: the game's, changed with its size and time kept, is refused although the record has it.
            var executable = expected[Exe];
            var genuine = File.ReadAllBytes(executable);
            var written = File.GetLastWriteTimeUtc(executable);
            File.WriteAllBytes(executable, RandomNumberGenerator.GetBytes(genuine.Length));
            File.SetLastWriteTimeUtc(executable, written);
            await RestartAsync(launcher);
            await StatusAsync(player, HttpStatusCode.NotFound, "with a game executable changed in place, its size and time kept", "/files");
            TestAssert.True((await SettingsAsync(browser, "Settings, an executable changed in place")).Contains("Your copy cannot be shared: the game folder: The game executable is not the",
                StringComparison.Ordinal), "a record never vouches for an executable");
            File.WriteAllBytes(executable, genuine);
            File.SetLastWriteTimeUtc(executable, written);
        }
        finally
        {
            HostFileShare.Checked -= builds.Seen;
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    /// <summary>The start of Settings' line while sharing is on and no 2011 launcher folder is chosen.</summary>
    private const string NoLauncherLine = "<p>No 2011 launcher folder is chosen, so the 2011 launcher is not shared.";

    private static string Keys<T>(Dictionary<string, T> files) => string.Join("\n", files.Keys.Order(StringComparer.Ordinal));

    private static string Text(Dictionary<string, (long Size, string Sha256)> manifest) =>
        string.Join("\n", manifest.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"{file.Key} {file.Value.Size} {file.Value.Sha256}"));

    private static string Sorted(IEnumerable<string> paths) => string.Join("\n", paths.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>The files the share's builds list under one check's folder, and a hold of the next build on the first such file a test accepts.</summary>
    private sealed class Builds(string root)
    {
        private readonly ConcurrentQueue<(string Source, bool Hashed)> _listed = new();
        private readonly SemaphoreSlim _held = new(0), _go = new(0);
        private Func<bool, bool>? _hold;

        public void Seen(string source, bool hashed)
        {
            if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            _listed.Enqueue((source, hashed));
            if (_hold is { } hold && hold(hashed) && Interlocked.CompareExchange(ref _hold, null, hold) == hold)
            {
                _held.Release();
                _go.Wait(TimeSpan.FromSeconds(60));
            }
        }

        /// <summary>The next build waits on the first file <paramref name="when"/> accepts (by whether it is read to hash it) until <see cref="WhileHeldAsync"/> has read.</summary>
        public void Hold(Func<bool, bool> when) => _hold = when;

        public async Task<string> WhileHeldAsync(Func<Task<string>> read)
        {
            TestAssert.True(await _held.WaitAsync(TimeSpan.FromSeconds(60)), "no build reached a file to hold");
            try { return await read(); }
            finally { _go.Release(); }
        }

        /// <summary>The files the builds since the last call read to hash, sorted.</summary>
        public string Hashed()
        {
            var hashed = new List<string>();
            while (_listed.TryDequeue(out var file)) if (file.Hashed) hashed.Add(file.Source);
            return Sorted(hashed);
        }
    }
}
