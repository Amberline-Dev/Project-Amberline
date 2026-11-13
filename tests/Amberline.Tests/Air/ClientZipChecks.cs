using System.IO.Compression;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 765): the client zip as the source of the clients (<see cref="ClientZip"/>). Every zip is a small synthetic one, made in a
/// temporary folder from the synthetic stand-ins of the installer checks (never the real client bundle); the stand-ins' own executables are the
/// pins (<see cref="ClientPins"/>), so the installer's own checks decide after the unpack. Nothing touches a real client folder, the real profile or
/// the real desktop.
/// </summary>
public static class ClientZipChecks
{
    private const string Pin = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
    private const string ReleasePath = "game/projects/lol_game_client/releases/0.0.0.51/";

    [Check("server.client-zip-locate")]
    public static void TheClientsAreFoundByWhatTheyHold() => WithRoot("league-client-zip-locate", root =>
    {
        // By what the folders hold, under any top folder, under none, and in any case; the zip's own name does not matter.
        foreach (var top in new[] { "my copy (2)/", "", "a/b/" })
            TestAssert.Equal(new ClientZip.Layout(top + "game/", top + "air/", top + "launcher/"), Locate(root, Names(top)), $"top folder '{top}'");
        TestAssert.Equal(new ClientZip.Layout("BUNDLE/GAME/", "Bundle/Air/", null),
            Locate(root, ["BUNDLE/GAME/PROJECTS/LOL_GAME_CLIENT/RELEASES/0.0.0.51/league of legends.exe", "Bundle/Air/lolclient.exe"]), "any case, no launcher");

        // No game or AIR client, or one of the three twice: one plain sentence each.
        Refused(() => Locate(root, Names("x/").Where(name => !name.Contains("releases", StringComparison.Ordinal))),
            $"This zip has no game client in it, so it is not the client zip ({ClientZip.BundleName}).");
        Refused(() => Locate(root, Names("x/").Where(name => !name.EndsWith("LolClient.exe", StringComparison.Ordinal))),
            $"This zip has no AIR client in it, so it is not the client zip ({ClientZip.BundleName}).");
        foreach (var (twice, what) in new[] { (ReleasePath + "League of Legends.exe", "game client"), ("air/LolClient.exe", "AIR client"), ("launcher/LoLLauncher.exe", "2011 launcher") })
            Refused(() => Locate(root, [.. Names("x/"), "copy/" + twice]), $"This zip has more than one {what} in it, so it is not known which to use.");

        // An entry that could land outside the staging folder, or a link, refuses the whole zip before anything is written.
        foreach (var unsafeName in new[] { "../escape.txt", "x/../../escape.txt", @"x\..\..\escape.txt", "C:/Windows/escape.txt", "/rooted.txt", "x/file.txt:stream", "x//double.txt", "x/./dot.txt" })
            Refused(() => Locate(root, [.. Names("x/"), unsafeName]), "The zip holds an unsafe entry");
        var linked = Zip(Path.Combine(root, "link.zip"), Names("x/"));
        using (var archive = ZipFile.Open(linked, ZipArchiveMode.Update))
            archive.CreateEntry("x/air/assets/link").ExternalAttributes = unchecked((int)0xA1FF0000);
        Refused(() => { using var archive = ZipFile.OpenRead(linked); ClientZip.Locate(archive); }, "The zip holds an unsafe entry (x/air/assets/link)");
    });

    [Check("server.air-player-install-client-zip", TimeoutSeconds = 300)]
    public static void TheClientZipInstallsAsPickedFoldersDo() => WithRoot("league-client-zip-install", root =>
    {
        var source = Source.Make(Path.Combine(root, "source"));
        var zip = source.Zip(Path.Combine(root, "downloads", "not the usual name.zip"), "Some other top folder/");
        var zipSha = RetailClients.Sha256(zip);
        var steps = new Steps();
        var result = PlayerInstall.InstallFromZip(source.Fixture.Profile, zip, source.Fixture.Install, source.Pins, null, Connection, "Alpha", source.Fixture.Tool, steps);
        var install = source.Fixture.Install;

        // The clients are where picked folders go, whole: the game, only the language project the game uses, the AIR client without its logs, and
        // the 2011 launcher's own files only.
        TestAssert.Equal(Snapshot(source.Fixture.Game), Snapshot(Path.Combine(install, PlayerInstall.GameRelease)), "the game");
        TestAssert.Equal(Snapshot(source.Fixture.Language), Snapshot(Path.Combine(install, "game", "projects", "lol_game_client_en_us")), "its language project");
        TestAssert.Equal("lol_game_client, lol_game_client_en_us", Entries(Path.Combine(install, "game", "projects")), "only the language the game uses");
        foreach (var file in Directory.EnumerateFiles(source.Fixture.Air, "*", SearchOption.AllDirectories).Where(file => !FolderMove.Inside(file, Path.Combine(source.Fixture.Air, "logs"))))
            TestAssert.Equal(RetailClients.Sha256(file), RetailClients.Sha256(Path.Combine(install, "air", Path.GetRelativePath(source.Fixture.Air, file))), $"the AIR client's {file}");
        TestAssert.False(Directory.Exists(Path.Combine(install, "air", "logs")), "the AIR client's logs stay in the zip");
        foreach (var file in ClassicLauncherFiles.Required.Append("releasemanifest"))
            TestAssert.True(File.Exists(Path.Combine(install, PlayerInstall.LauncherFolder, file)), $"the launcher's {file}");
        TestAssert.False(File.Exists(Path.Combine(install, PlayerInstall.LauncherFolder, "notes.txt")), "a file that is not the launcher's stays in the zip");

        // The same record and server as an install from picked folders; no staging folder is left, and the zip is byte-identical.
        TestAssert.True(FolderMove.Same(result.Record.Game, Path.Combine(install, PlayerInstall.GameRelease)) && FolderMove.Same(result.Record.Air, Path.Combine(install, "air")),
            $"the record: {result.Record}");
        TestAssert.True(File.Exists(source.Fixture.Profile.RecordPath) && File.Exists(result.Shortcut), "the record and the shortcut");
        TestAssert.False(Directory.Exists(ClientZip.StagingFor(install)), "the staging folder is gone");
        TestAssert.Equal(zipSha, RetailClients.Sha256(zip), "the zip is byte-identical");

        // The unpack's bar: megabytes, Cancel while it runs, 100% at its end.
        var unpacking = steps.All.Where(step => step.Text.StartsWith("Unpacking the client zip: ", StringComparison.Ordinal)).ToList();
        TestAssert.True(unpacking.Count > 0 && unpacking.All(step => step.CanCancel && step.Percent is not null) && unpacking[^1].Percent == 100 &&
            unpacking[^1].Text.EndsWith(" MB", StringComparison.Ordinal), "the unpack's steps: " + string.Join(" | ", unpacking));
    });

    [Check("server.air-player-install-client-zip-staging", TimeoutSeconds = 300)]
    public static void TheStagingFolderIsRedoneAndAlwaysRemoved() => WithRoot("league-client-zip-staging", root =>
    {
        // A zip without the 2011 launcher installs without it. A leftover of an interrupted run (its marker, a half file) is removed and redone.
        var source = Source.Make(Path.Combine(root, "plain"));
        var zip = source.Zip(Path.Combine(root, "plain.zip"), "", launcher: false);
        var zipSha = RetailClients.Sha256(zip);
        var staging = ClientZip.StagingFor(source.Fixture.Install);
        Write(Path.Combine(staging, "amberline-client-zip.txt"), "left by an interrupted run");
        Write(Path.Combine(staging, "game", "half.bin"), "half");
        PlayerInstall.InstallFromZip(source.Fixture.Profile, zip, source.Fixture.Install, source.Pins, null, Connection, "Alpha", source.Fixture.Tool);
        TestAssert.False(Directory.Exists(Path.Combine(source.Fixture.Install, PlayerInstall.LauncherFolder)), "no launcher in the zip, none in the install");
        TestAssert.False(File.Exists(Path.Combine(source.Fixture.Install, "game", "half.bin")), "the leftover did not come in");
        TestAssert.Equal(Snapshot(source.Fixture.Game), Snapshot(Path.Combine(source.Fixture.Install, PlayerInstall.GameRelease)), "the game");
        TestAssert.False(Directory.Exists(staging), "the staging folder is gone");
        TestAssert.Equal(zipSha, RetailClients.Sha256(zip), "the zip is byte-identical");

        // A folder of that name that is not a leftover is never removed: the install is refused and changes nothing.
        var foreign = Source.Make(Path.Combine(root, "foreign"));
        var inWay = ClientZip.StagingFor(foreign.Fixture.Install);
        Write(Path.Combine(inWay, "mine.txt"), "the player's own file");
        Refused(() => PlayerInstall.InstallFromZip(foreign.Fixture.Profile, zip, foreign.Fixture.Install, foreign.Pins, null, Connection, "Alpha", foreign.Fixture.Tool),
            $"A folder named {inWay} is in the way");
        TestAssert.Equal("the player's own file", File.ReadAllText(Path.Combine(inWay, "mine.txt")), "the folder in the way is untouched");
        TestAssert.False(Directory.Exists(foreign.Fixture.Install), "nothing was installed");

        // The installer's own checks decide after the unpack: a game that is not the pinned one is refused, and the staging folder goes.
        var wrong = Source.Make(Path.Combine(root, "wrong"));
        Refused(() => PlayerInstall.InstallFromZip(wrong.Fixture.Profile, zip, wrong.Fixture.Install, wrong.Pins with { Game = Pin }, null, Connection, "Alpha", wrong.Fixture.Tool),
            "This is not the 1.0.0.126 game client");
        TestAssert.False(Directory.Exists(ClientZip.StagingFor(wrong.Fixture.Install)) || Directory.Exists(wrong.Fixture.Install), "nothing is left and nothing came in");

        // A damaged entry (one byte of a stored file flipped) fails its CRC-32 check; a cancel stops the unpack. Neither leaves the staging folder.
        var probe = new byte[4096];
        for (var index = 0; index < probe.Length; index++) probe[index] = (byte)"AMBERLINE-CRC-PROBE "[index % 20];
        Write(Path.Combine(source.Fixture.Game, "DATA", "probe.bin"), probe);
        var damaged = source.Zip(Path.Combine(root, "damaged.zip"), "", launcher: false, CompressionLevel.NoCompression);
        var bytes = File.ReadAllBytes(damaged);
        bytes[bytes.AsSpan().IndexOf(probe.AsSpan(0, 64)) + 100] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);
        var unpackTo = Path.Combine(root, "unpack.client-zip");
        Refused(() => ClientZip.Unpack(damaged, unpackTo, launcher: false), "The client zip is damaged (");
        TestAssert.False(Directory.Exists(unpackTo), "a damaged zip leaves no staging folder");
        using var cancel = new CancellationTokenSource();
        try
        {
            ClientZip.Unpack(zip, unpackTo, launcher: false, new Cancels(cancel), cancel.Token);
            throw new InvalidOperationException("the canceled unpack went on");
        }
        catch (OperationCanceledException) { }
        TestAssert.False(Directory.Exists(unpackTo), "a canceled unpack leaves no staging folder");
        TestAssert.Equal(zipSha, RetailClients.Sha256(zip), "the zip is still byte-identical");
    });

    /// <summary>
    /// A synthetic client zip at <paramref name="zip"/>: under <paramref name="top"/>, the game release at <c>game\projects\lol_game_client\releases\0.0.0.51</c>,
    /// each language project beside it, the AIR client at <c>air</c> and the 2011 launcher at <c>launcher</c> (each left out when null), with every file
    /// and every empty folder.
    /// </summary>
    internal static string Bundle(string zip, string top, string? game, IEnumerable<string> languages, string? air, string? launcher,
        CompressionLevel level = CompressionLevel.Fastest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string? folder, string prefix)
        {
            if (folder is null) return;
            string Relative(string path) => top + prefix + Path.GetRelativePath(folder, path).Replace('\\', '/');
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Where(directory => !Directory.EnumerateFileSystemEntries(directory).Any()))
                archive.CreateEntry(Relative(directory) + "/");
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) archive.CreateEntryFromFile(file, Relative(file), level);
        }
        Add(game, ReleasePath);
        foreach (var language in languages) Add(language, "game/projects/" + Path.GetFileName(language) + "/");
        Add(air, "air/");
        Add(launcher, "launcher/");
        return zip;
    }

    /// <summary>
    /// The installer checks' synthetic clients (<see cref="AirPlayerInstallChecks.Fixture"/>), with what a zip may hold beside them: a second language
    /// project, the AIR client's logs, and a 2011 launcher folder with its file list and a file of its own.
    /// </summary>
    private sealed record Source(AirPlayerInstallChecks.Fixture Fixture, string German, string Launcher, ClientPins Pins)
    {
        public static Source Make(string root)
        {
            var fixture = AirPlayerInstallChecks.Fixture.Make(root);
            var german = Path.Combine(Path.GetDirectoryName(fixture.Language)!, "lol_game_client_de_de");
            Write(Path.Combine(german, "releases", "0.0.0.20", "DATA", "Menu", "fontconfig_de_DE.txt"), "stand-in");
            Write(Path.Combine(fixture.Air, "logs", "session.txt"), "the AIR client's own record");
            var launcher = Path.Combine(root, "downloads", "launcher");
            var sha = ClassicLauncherChecks.WriteLauncherFiles(launcher);
            Write(Path.Combine(launcher, "releasemanifest"), "file list");
            Write(Path.Combine(launcher, "notes.txt"), "not the launcher's");
            return new(fixture, german, launcher, fixture.Pins with { Launcher = sha });
        }

        public string Zip(string zip, string top, bool launcher = true, CompressionLevel level = CompressionLevel.Fastest) =>
            Bundle(zip, top, Fixture.Game, [Fixture.Language, German], Fixture.Air, launcher ? Launcher : null, level);
    }

    /// <summary>The names of a zip that holds the three under <paramref name="top"/>, and a file of the zip's own.</summary>
    private static string[] Names(string top) =>
        [top + ReleasePath + "League of Legends.exe", top + ReleasePath + "DATA/data.bin", top + "air/LolClient.exe", top + "launcher/LoLLauncher.exe", top + "READ-ME.txt"];

    private static ClientZip.Layout Locate(string root, IEnumerable<string> names)
    {
        using var archive = ZipFile.OpenRead(Zip(Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"), names));
        return ClientZip.Locate(archive);
    }

    /// <summary>A zip of empty entries with these names, as written (no check of its own).</summary>
    private static string Zip(string path, IEnumerable<string> names)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names) archive.CreateEntry(name);
        return path;
    }

    private static readonly AirRemoteInvitation Connection = new("one.example.test", 8391, CertificateSha25664hex: Pin);

    /// <summary>A synchronous progress sink: every step, in order.</summary>
    private sealed class Steps : IProgress<InstallProgress>
    {
        public List<InstallProgress> All { get; } = [];

        public void Report(InstallProgress value) => All.Add(value);
    }

    /// <summary>Cancels at the first step it hears of.</summary>
    private sealed class Cancels(CancellationTokenSource cancel) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => cancel.Cancel();
    }

    private static string Snapshot(string folder) => Directory.Exists(folder) ? HostInstallChecks.Snapshot(folder) : "";

    private static string Entries(string folder) => string.Join(", ", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    private static void Write(string path, string text) => Write(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void Refused(Action action, string text)
    {
        try { action(); }
        catch (InstallRefusedException refused)
        {
            TestAssert.True(refused.Message.Contains(text, StringComparison.Ordinal), $"refused for another reason: {refused.Message}");
            return;
        }
        throw new InvalidOperationException($"not refused (expected: {text})");
    }

    private static void WithRoot(string prefix, Action<string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory(prefix);
        try { check(root); }
        finally { HostModeChecks.Delete(root); }
    }
}
