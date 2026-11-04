using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Platform.Folders;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the host shares their own game and AIR client with players who have none (batch 681; the owner, 4 November 2026: "Where
/// the person hosting the server can decide if they want to also host the game and air client files for the players"; anyone with the
/// Play file may download, off by default). Retail players got both from Riot's patcher. While Settings' switch is on
/// (<see cref="PlatformSettings.ShareFiles"/>), the friends' port (HTTPS with the certificate the Play file pins) serves two things more:
/// <see cref="Prefix"/>, the manifest (each file's path in the player's install, size and SHA-256), and <c>/files/&lt;path&gt;</c>, one
/// file of that manifest with ranges, so a download resumes. Nothing else of the host reaches that port (<see cref="Serves"/>).
/// <list type="bullet">
/// <item>The manifest is exactly the game's release folder, its language project beside it (as <see cref="FolderMove.LanguageProject"/>
/// takes it) and the AIR client's own entries (<see cref="FolderMove.AirEntries"/>), less what the clients write as they run (their logs,
/// crash dumps and the game's last command line, which names a match's server and key: <see cref="Private"/>).</item>
/// <item>A request names a manifest entry, looked up as it is: never a path on disk, so no traversal reaches anything else.</item>
/// <item>The two stock executables in it must have the pinned SHA-256s (<see cref="RetailClients"/>), the folders must hold no link and
/// must not hold Amberline's own data folder; otherwise Settings says why and nothing is served.</item>
/// <item>Each address gets <see cref="PerAddress"/> transfers at a time and all players together <see cref="InAll"/>
/// (<see cref="Program.RequestPartition"/>, <see cref="Program.FilesPartition"/>), so the tunnels of a running game keep their share
/// of the port.</item>
/// </list>
/// </summary>
public sealed class HostFileShare(HostLoop loop, string game, string air, int remotePort, bool on)
{
    public const string Prefix = "/files";
    public const int PerAddress = 4, InAll = 16;
    /// <summary>The owner's notice beside the switch, in one sentence.</summary>
    public const string Notice = "You share your own copy of the game, so make sure you may share it: Amberline itself never ships these files.";
    /// <summary>Where each folder goes in the player's install (<see cref="FolderMove.GameRelease"/>, <c>air</c>).</summary>
    public const string GameRoot = "game/projects/lol_game_client/releases/0.0.0.51", AirRoot = "air";

    public sealed record Entry(string Path, long Size, string Sha256);

    /// <summary>What a build found: the files by their manifest path, the manifest's bytes and the two folders' sizes, or why nothing is shared.</summary>
    internal sealed record Shared(IReadOnlyDictionary<string, (string Source, Entry Entry)> Files, byte[] Manifest, long GameBytes, long AirBytes, string? Problem);

    private volatile bool _on = on;
    private volatile Task<Shared>? _built = on ? Start(loop, game, air) : null;

    public bool On => _on;

    /// <summary>A request the friends' port serves from the share: on that port, over HTTPS, a GET of <see cref="Prefix"/> or below, while the switch is on.</summary>
    public bool Serves(HttpContext context) => _on && context.Connection.LocalPort == remotePort && context.Request.IsHttps &&
        HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments(Prefix);

    /// <summary>Settings' switch: saved in platform.json and served at once; turning it on checks the folders again.</summary>
    internal void Set(bool value)
    {
        HostMode.Change(loop.Paths.Settings, saved => saved with { ShareFiles = value });
        if (value) _built = Start(loop, game, air);
        _on = value;
    }

    public void Map(WebApplication app)
    {
        app.MapGet(Prefix, (HttpContext context) => ServeAsync(context));
        app.MapGet(Prefix + "/{**path}", (HttpContext context) => ServeAsync(context));
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (!Serves(context) || _built is not { } built) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var shared = await built.WaitAsync(context.RequestAborted);
        var path = context.Request.Path.Value!;
        // The rest of the address after /files/ is compared with the manifest's paths as it is; a file changed since the manifest is not served.
        var entry = path.Length > Prefix.Length ? path[(Prefix.Length + 1)..] : null;
        if (shared.Problem is not null) context.Response.StatusCode = StatusCodes.Status404NotFound;
        else if (entry is null) await Results.Bytes(shared.Manifest, "application/json").ExecuteAsync(context);
        else if (shared.Files.TryGetValue(entry, out var file) && new FileInfo(file.Source) is { Exists: true } info &&
                 info.Length == file.Entry.Size)
            await Results.File(file.Source, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
        else context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>Settings' lines under the switch: what is shared and its size, that it checks the files, or why it cannot be shared.</summary>
    internal string Status()
    {
        if (!_on || _built is not { } built) return "";
        if (!built.IsCompleted) return "<p>Amberline is checking every file once before it shares them. This takes a few minutes; open this page again to see when it is done.</p>";
        var shared = built.Result;
        return shared.Problem is { } problem
            ? $"<p class=problem role=alert>Your copy cannot be shared: {WebUtility.HtmlEncode(problem)} Nothing is shared.</p>"
            : $"<p>Shared: the game folder ({HostUninstall.Size(shared.GameBytes)}) and the AIR client folder ({HostUninstall.Size(shared.AirBytes)}). " +
              "A player who has no copy of their own downloads them with their Play file, through your internet upload.</p>";
    }

    private static Task<Shared> Start(HostLoop loop, string game, string air) =>
        Task.Run(() => Build(game, air, loop.Paths.Data, (loop.Folders.GameSha256, loop.Folders.AirSha256)));

    /// <summary>
    /// The client's own run-time files, which hold the host's data, never the game's: its logs (<c>*.log</c>, the release folder's
    /// <c>*_r3dlog.txt</c>, <c>*_netlog.txt</c> and <c>Localization_Errors.txt</c>), crash dumps and <c>lastCmd.log</c>.
    /// </summary>
    internal static bool Private(string relative, bool release) =>
        relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
        release && !relative.Contains('/') && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The manifest of the two folders, or why they cannot be shared. Reads every file once.</summary>
    internal static Shared Build(string game, string air, string data, (string Game, string Air) pins)
    {
        static Shared Refused(string problem) => new(new Dictionary<string, (string, Entry)>(), [], 0, 0, problem);
        if (RetailClients.GameProblem(game, pins.Game) is { } gameProblem) return Refused($"the game folder: {gameProblem}.");
        if (RetailClients.AirProblem(air, pins.Air) is { } airProblem) return Refused($"the AIR client folder: {airProblem}.");
        var roots = new List<(string Source, string Path, bool Release)> { (game, GameRoot, true) };
        if (FolderMove.LanguageProject(game) is { } language) roots.Add((language, "game/projects/" + Path.GetFileName(language), false));
        // The AIR client's own entries only; its logs hold the host's own logins.
        foreach (var entry in FolderMove.AirEntries.Where(entry => entry != "logs"))
            if (File.Exists(Path.Combine(air, entry)) || Directory.Exists(Path.Combine(air, entry))) roots.Add((Path.Combine(air, entry), AirRoot + "/" + entry, false));
        if (roots.FirstOrDefault(root => FolderMove.Overlaps(root.Source, data)) is { Source: not null } holds)
            return Refused($"{holds.Source} holds Amberline's own data folder.");
        var files = new Dictionary<string, (string Source, Entry Entry)>(StringComparer.Ordinal);
        long gameBytes = 0, airBytes = 0;
        try
        {
            foreach (var (source, prefix, release) in roots)
            {
                IEnumerable<FileSystemInfo> items = File.Exists(source) ? [new FileInfo(source)]
                    : new DirectoryInfo(source).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });
                foreach (var item in items)
                {
                    // A link could lead out of the folder, to anything on the host's PC.
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return Refused($"{item.FullName} is a link, which is never shared.");
                    if (item is not FileInfo file) continue;
                    var relative = File.Exists(source) ? "" : Path.GetRelativePath(source, file.FullName).Replace('\\', '/');
                    if (Private(relative, release)) continue;
                    var path = relative.Length == 0 ? prefix : prefix + "/" + relative;
                    using (var stream = file.OpenRead()) files[path] = (file.FullName, new(path, file.Length, Convert.ToHexString(SHA256.HashData(stream))));
                    if (prefix.StartsWith(AirRoot + "/", StringComparison.Ordinal)) airBytes += file.Length;
                    else gameBytes += file.Length;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Refused($"a file cannot be read ({exception.Message})."); }
        // The pins again, on the bytes just read: a file replaced since the first check is not shared either.
        if (files.GetValueOrDefault(GameRoot + "/" + HostFolders.GameExecutable).Entry?.Sha256 != pins.Game ||
            files.GetValueOrDefault(AirRoot + "/LolClient.exe").Entry?.Sha256 != pins.Air)
            return Refused("an executable changed while Amberline checked it.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { protocol = 1, bytes = gameBytes + airBytes,
            files = files.Values.Select(file => file.Entry).OrderBy(entry => entry.Path, StringComparer.Ordinal) }, Json);
        return new(files, manifest, gameBytes, airBytes, null);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
