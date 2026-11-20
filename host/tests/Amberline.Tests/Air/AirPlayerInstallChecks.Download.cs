using System.Net;
using System.Text.Json;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static partial class AirPlayerInstallChecks
{
    /// <summary>
    /// Batch 919 (owner finding 4, 20 November 2026): the setup window's download into its default folder inside the player's own profile
    /// (<c>&lt;LocalAppData&gt;\Programs\&lt;the line's name&gt;</c>, as <c>DefaultInstall</c> picks it), chained as the window chains it: the offer from a
    /// fake host's <c>/files</c>, the plan, the download (canceled once and continued), then the install. Before batch 919 the install refused the folder
    /// the download had just filled ("holds other files"). Then the retries: the folder a finished download of an older tool left (its parts and
    /// nothing else) installs without fetching a file again; the same beside a file of the player's own, or outside the profile, is refused before
    /// anything changes; a found copy's install that stopped after its moves (the record written) is finished by the next install.
    /// </summary>
    [Check("server.air-player-download-into-profile", TimeoutSeconds = 300)]
    public static void ADownloadIntoTheProfileInstallsAndARetryFinishesIt() => DownloadIntoProfileAsync().GetAwaiter().GetResult();

    private static async Task DownloadIntoProfileAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-download-profile");
        try
        {
            // The host shares the game, the AIR client and the 2011 launcher, so an older tool's download put all three in place.
            var shared = Fixture.Make(Path.Combine(root, "host"));
            var launcher = Path.Combine(root, "host", "launcher");
            var pins = shared.Pins with { Launcher = ClassicLauncherChecks.WriteLauncherFiles(launcher) };
            var connection = Connection("friends.example.test", PinA);
            using var host = new FakeFileHost(("air", shared.Air), ("game/projects/lol_game_client/releases/0.0.0.51", shared.Game),
                ("game/projects/lol_game_client_en_us", shared.Language), (PlayerInstall.LauncherFolder, launcher));
            using var client = new HttpClient(host);
            var origin = new Uri("https://friends.example.test:8391/");
            var offer = await PlayerDownload.OfferAsync(client, origin, pins, CancellationToken.None) ?? throw new InvalidOperationException("the fake host offers nothing");

            // The friend's default: the window's plan, the download (canceled after five files, then continued), then the install.
            var friend = Profile(root, "friend");
            var install = DefaultFolder(friend);
            var plan = PlayerDownload.Plan(install, offer, null, friend, null, pins);
            using (var stop = new CancellationTokenSource())
            {
                host.After = count => { if (count == 5) stop.Cancel(); };
                try
                {
                    await PlayerDownload.DownloadAsync(client, origin, offer, plan, pins, "Alpha", friend.LocalAppData, null, stop.Token);
                    throw new InvalidOperationException("the canceled download went on");
                }
                catch (OperationCanceledException) { }
                host.After = null;
            }
            await PlayerDownload.DownloadAsync(client, origin, offer, plan, pins, "Alpha", friend.LocalAppData, null, CancellationToken.None);
            PlayerInstall.Install(friend, plan, connection, "Alpha", shared.Tool);
            TestAssert.True(PlayerInstall.ExistingInstall(friend, pins) is { } done && FolderMove.Same(done.Install, install), "the download into the profile is installed");
            TestAssert.Equal(null, ClassicLauncherFiles.Problem(Path.Combine(install, PlayerInstall.LauncherFolder), pins.Launcher), "the launcher is in place");
            TestAssert.False(Directory.Exists(Path.Combine(install, PlayerDownload.Staging)), "the staging folder is gone");

            // A finished download of an older tool (0.1.x since batch 857): the clients and the launcher in place, nothing that marks the folder.
            // Download again takes them as they are, fetches nothing, and installs.
            var older = Profile(root, "older");
            var leftover = DefaultFolder(older);
            Leave(shared, launcher, leftover);
            host.Fetched = 0;
            var retry = PlayerDownload.Plan(leftover, offer, null, older, null, pins);
            await PlayerDownload.DownloadAsync(client, origin, offer, retry, pins, "Alpha", older.LocalAppData, null, CancellationToken.None);
            PlayerInstall.Install(older, retry, connection, "Alpha", shared.Tool);
            TestAssert.True(PlayerInstall.ExistingInstall(older, pins) is { } kept && FolderMove.Same(kept.Install, leftover), "the older tool's download is installed");
            TestAssert.Equal(0, host.Fetched, "a file in place was fetched again");

            // Not Amberline's: the same clients beside a file of the player's own, and the same clients outside the profile. Refused before anything is
            // written, the permissions as they were.
            var mine = Profile(root, "mine");
            var notes = DefaultFolder(mine);
            Leave(shared, launcher, notes);
            File.WriteAllText(Path.Combine(notes, "my-notes.txt"), "the player's own");
            var outside = Profile(root, "outside");
            var elsewhere = Path.Combine(root, "outside", "Games", FolderMove.HostName);
            Leave(shared, launcher, elsewhere);
            foreach (var (profile, folder) in new[] { (mine, notes), (outside, elsewhere) })
            {
                var before = Permissions(folder);
                var refusedPlan = PlayerDownload.Plan(folder, offer, null, profile, null, pins);
                try
                {
                    await PlayerDownload.DownloadAsync(client, origin, offer, refusedPlan, pins, "Alpha", profile.LocalAppData, null, CancellationToken.None);
                    throw new InvalidOperationException($"{folder}: not refused");
                }
                catch (InstallRefusedException refused)
                {
                    TestAssert.True(refused.Message.Contains("already contains other files. Choose an empty or new folder.", StringComparison.Ordinal), $"{folder}: refused for another reason: {refused.Message}");
                }
                TestAssert.Equal(before, Permissions(folder), $"{folder}: a refused folder's permissions changed");
                TestAssert.Equal(new[] { "air", "game", "launcher" }, Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Where(name => name != "my-notes.txt")
                    .Order(StringComparer.Ordinal).ToArray(), $"{folder}: a refused download wrote into it");
                TestAssert.False(File.Exists(profile.RecordPath), $"{folder}: a refused download installed");
            }
            TestAssert.Equal(0, host.Fetched, "a refused download fetched a file");

            // A found copy's install into the profile that stopped after its moves (the player tool missing; the record is written): the next install,
            // from the record's folders as the window offers them, finishes it.
            var found = Fixture.Make(Path.Combine(root, "found"));
            found = found with { Install = DefaultFolder(found.Profile) };
            var broken = Directory.CreateDirectory(Path.Combine(root, "found", "no-tool")).FullName;
            try
            {
                PlayerInstall.Install(found.Profile, found.Plan(), connection, "Alpha", broken);
                throw new InvalidOperationException("the install without its player tool went on");
            }
            catch (IOException) { }
            var record = PlayerInstall.ExistingInstall(found.Profile, found.Pins) ?? throw new InvalidOperationException("the stopped install wrote no record");
            PlayerInstall.Install(found.Profile, PlayerInstall.MakePlan(found.Profile, record.Air, record.Game, record.Install, found.Pins), connection, "Alpha", found.Tool);
            TestAssert.True(File.Exists(Path.Combine(found.Install, PlayerInstall.ToolFolder, "Amberline.AirCompanion.exe")), "the next install finished the stopped one");
        }
        finally { Cleanup(root); }
    }

    private static PlayerProfile Profile(string root, string name)
    {
        var profile = new PlayerProfile(Path.Combine(root, name, "Local"), Path.Combine(root, name, "Desktop"));
        Directory.CreateDirectory(profile.Desktop);
        return profile;
    }

    /// <summary>The setup window's default install folder for a check's profile (<c>DefaultInstall</c>), as for a friend whose game is on the system drive.</summary>
    private static string DefaultFolder(PlayerProfile profile) => Path.Combine(profile.LocalAppData, "Programs", FolderMove.HostName);

    /// <summary>What a finished download leaves in its install folder: <c>air</c>, <c>game</c> (with the language project) and <c>launcher</c>, and nothing else.</summary>
    private static void Leave(Fixture shared, string launcher, string install)
    {
        Fixture.CopyTree(shared.Air, Path.Combine(install, "air"));
        Fixture.CopyTree(shared.Game, Path.Combine(install, PlayerInstall.GameRelease));
        Fixture.CopyTree(shared.Language, Path.Combine(install, "game", "projects", "lol_game_client_en_us"));
        Fixture.CopyTree(launcher, Path.Combine(install, PlayerInstall.LauncherFolder));
    }

    /// <summary>The permissions of a folder and of each file and folder in it, as one text.</summary>
    private static string Permissions(string folder) => OperatingSystem.IsWindows()
        ? string.Join("\n", new[] { folder }.Concat(Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
            .Select(path => path + " " + (Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All)
                : new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All))))
        : "";

    /// <summary>
    /// A host that shares its game as HostFileShare does, in this process: <c>/files</c> lists every file of the given folders under its path in the
    /// install's layout, with its size and SHA-256, and <c>/files/&lt;path&gt;</c> sends it whole. <see cref="Fetched"/> counts the files sent.
    /// </summary>
    private sealed class FakeFileHost : HttpMessageHandler
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        private readonly byte[] list;
        private int fetched;

        public FakeFileHost(params (string Prefix, string Folder)[] parts)
        {
            foreach (var (prefix, folder) in parts)
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    files[prefix + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/')] = file;
            var entries = files.Select(pair => new { path = pair.Key, size = new FileInfo(pair.Value).Length, sha256 = RetailClients.Sha256(pair.Value) }).ToList();
            list = JsonSerializer.SerializeToUtf8Bytes(new { protocol = 1, bytes = entries.Sum(entry => entry.size), files = entries });
        }

        public int Fetched { get => fetched; set => fetched = value; }
        public Action<int>? After { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == "/files") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(list) });
            if (!path.StartsWith("/files/", StringComparison.Ordinal) || !files.TryGetValue(path["/files/".Length..], out var file))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            After?.Invoke(Interlocked.Increment(ref fetched));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) });
        }
    }
}
