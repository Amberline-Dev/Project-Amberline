using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Amberline.AirCompanion.FolderMove;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: the player downloads the game and the AIR client from a host that shares its own copy (batch 681; the host's side is
/// the Platform's HostFileShare). Retail players got both from Riot's patcher. The list comes over the server's pinned HTTPS port; every
/// path in it must lie in the install's own layout (<see cref="Allowed"/>), and the two stock executables in it must have the pinned
/// SHA-256s before a byte is downloaded. Each file goes to <c>&lt;install&gt;\download.partial</c> as <c>.part</c>, continues with a
/// range where it stopped (a Cancel, a dropped connection), and keeps its name only once its SHA-256 matches the list. When everything
/// is there and the client folders pass their checks again, two renames put <c>air</c> and <c>game</c> where a found copy goes
/// (<see cref="FolderMove.GameRelease"/>), and the found copy's install (<see cref="PlayerInstall.Install"/>) does the rest. The host
/// already sends the player tool itself, so the checks guard against a damaged or changed copy, not against the host.
/// </summary>
internal static class PlayerDownload
{
    internal const string Staging = "download.partial";
    /// <summary>Transfers at a time: under the host's four for each address, so a slow answer never turns into a refusal.</summary>
    internal const int Workers = 3;
    internal const long MaxBytes = 20_000_000_000;
    internal const int MaxFiles = 200_000;
    private const int Attempts = 8;
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);
    private const string Game = "game/projects/lol_game_client/releases/0.0.0.51";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Shape = new(@"^(?:game/projects/lol_game_client/releases/0\.0\.0\.51|game/projects/lol_game_client_[a-z]{2}_[a-z]{2}|air)/[^/]",
        RegexOptions.CultureInvariant);
    private static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars(), ':'];

    internal sealed record Entry(string Path, long Size, string Sha256);

    /// <summary>A checked list: the files and their total size.</summary>
    internal sealed record Offer(IReadOnlyList<Entry> Files, long Bytes);

    private sealed record Manifest(int Protocol, long Bytes, Entry[]? Files);

    /// <summary>The pinned client of a connection and its origin, as the shortcut's update check makes them.</summary>
    internal static HttpClient Client(AirRemoteInvitation connection, out Uri origin)
    {
        origin = new UriBuilder(Uri.UriSchemeHttps, connection.Host, connection.Port).Uri;
        return AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(connection.CertificateSha25664hex));
    }

    /// <summary>The server's list, checked; null when the server shares nothing (its answer is 404).</summary>
    internal static async Task<Offer?> OfferAsync(HttpClient client, Uri origin, ClientPins pins, CancellationToken cancel)
    {
        byte[] json;
        try { json = await PlayerInstall.GetAsync(client, new Uri(origin, "/files"), 64 << 20, cancel); }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
        return Read(json, pins);
    }

    /// <summary>The list's checks: its shape, every path (<see cref="Allowed"/>, once each), sizes that add up, and the two pins.</summary>
    internal static Offer Read(byte[] json, ClientPins pins)
    {
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(json, Json); }
        catch (JsonException) { manifest = null; }
        if (manifest is not { Protocol: 1, Files: { Length: > 0 and <= MaxFiles } files }) throw Bad("it is not a list of files");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in files)
        {
            if (entry?.Path is not { } path || !Allowed(path) || !seen.Add(path)) throw Bad($"it names a file outside the game and the AIR client ({entry?.Path})");
            if (entry.Size is < 0 or > MaxBytes || entry.Sha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit)) throw Bad($"its entry for {path} is not valid");
            bytes += entry.Size;
        }
        if (bytes > MaxBytes || bytes != manifest.Bytes) throw Bad("its sizes do not add up");
        bool Pinned(string path, string pin) => files.Any(entry => entry.Path == path && string.Equals(entry.Sha256, pin, StringComparison.OrdinalIgnoreCase));
        if (!Pinned(Game + "/League of Legends.exe", pins.Game) || !Pinned("air/LolClient.exe", pins.Air))
            throw new InstallRefusedException("The server's copy is not the 1.0.0.126 game and AIR client (an executable's SHA-256 differs), so Setup does not download it.");
        return new(files, bytes);
    }

    /// <summary>
    /// A path of the install's layout: under the game's release folder, a language project beside it, or one of the AIR client's own
    /// entries; plain names only (no empty, <c>.</c> or <c>..</c> part, no drive, stream or reserved device name, nothing Windows trims).
    /// </summary>
    internal static bool Allowed(string path)
    {
        if (path.Length > 240 || !Shape.IsMatch(path)) return false;
        var parts = path.Split('/');
        if (parts[0] == "air" && !AirEntries.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) return false;
        return parts.All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
            part.IndexOfAny(Invalid) < 0 && !PlayerInstall.ReservedName.IsMatch(part));
    }

    /// <summary>
    /// The download's install: the folder's rules of a found copy (no drive root, not inside a server installation, no client folders
    /// there yet) and the free space for what is not downloaded yet. Nothing moves, so the plan has no moves.
    /// </summary>
    internal static PlayerInstall.Plan Plan(string installFolder, Offer offer)
    {
        var install = Full(installFolder);
        if (IsDriveRoot(install)) throw new InstallRefusedException("Choose a folder for the install, not a whole drive.");
        if (HostInstallation(install) is not null)
            throw new InstallRefusedException("The install folder is inside an Amberline server installation. Choose a folder of your own.");
        NotThere(Path.Combine(install, "air"), "AIR client");
        NotThere(Path.Combine(install, "game"), "game client");
        var staging = Path.Combine(install, Staging);
        var staged = Directory.Exists(staging) ? new DirectoryInfo(staging).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) : 0;
        var root = Path.GetPathRoot(install)!;
        long free;
        try { free = new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        { throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} is not available. Choose another install folder."); }
        var needed = Math.Max(0, offer.Bytes - staged);
        if (needed > free)
            throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} has {Gigabytes(free)} GB free; the download needs {Gigabytes(needed)} GB. Choose another install folder or free some space.");
        return new(install, Path.Combine(install, "air"), Path.Combine(install, GameRelease), [], null, 0, []);
    }

    /// <summary>
    /// Downloads every file of <paramref name="offer"/> into the staging folder (what is already there and checks out stays; anything
    /// else there goes), checks the two client folders, then renames <c>air</c> and <c>game</c> into <paramref name="plan"/>'s install.
    /// Cancel works until the renames; a second call continues where the first stopped.
    /// </summary>
    internal static async Task DownloadAsync(HttpClient client, Uri origin, Offer offer, PlayerInstall.Plan plan, ClientPins pins, string server,
        IProgress<InstallProgress>? progress, CancellationToken cancel)
    {
        var staging = Path.Combine(plan.Install, Staging);
        Directory.CreateDirectory(staging);
        var files = offer.Files.ToDictionary(entry => Local(staging, entry.Path), StringComparer.OrdinalIgnoreCase);
        // Left from a download of another list: removed, so the install holds exactly this one.
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            if (!files.ContainsKey(file) && !(file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && files.ContainsKey(file[..^5]))) File.Delete(file);
        var meter = new Meter(progress, offer.Bytes, server);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancel },
            (file, token) => FetchAsync(client, origin, file.Key, file.Value, meter, token));
        progress?.Report(new("Checking the downloaded game…"));
        if (RetailClients.GameProblem(Path.Combine(staging, GameRelease), pins.Game) is { } game)
            throw new InstallRefusedException($"The game from {server} is not complete ({game}). Nothing was installed; ask the host.");
        if (RetailClients.AirProblem(Path.Combine(staging, "air"), pins.Air) is { } air)
            throw new InstallRefusedException($"The AIR client from {server} is not complete ({air}). Nothing was installed; ask the host.");
        cancel.ThrowIfCancellationRequested();
        progress?.Report(new("Putting the game in place…"));
        Directory.Move(Path.Combine(staging, "air"), plan.Air);
        try { Directory.Move(Path.Combine(staging, "game"), Path.Combine(plan.Install, "game")); }
        catch
        {
            Directory.Move(plan.Air, Path.Combine(staging, "air"));
            throw;
        }
        TryDeleteTree(staging);
    }

    /// <summary>One file: kept when it is already there and checks out, else continued from its <c>.part</c> (or fetched whole), then checked.</summary>
    private static async ValueTask FetchAsync(HttpClient client, Uri origin, string target, Entry entry, Meter meter, CancellationToken cancel)
    {
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length == entry.Size && Same(target, entry)) { meter.Add(entry.Size); return; }
            File.Delete(target);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        long counted = 0;
        void Count(long now)
        {
            meter.Add(now - counted);
            counted = now;
        }
        var whole = false;
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > entry.Size || whole && have > 0)
            {
                File.Delete(part);
                have = 0;
            }
            var resumed = have > 0;
            Count(have);
            try
            {
                if (have < entry.Size || entry.Size == 0)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Address(origin, entry.Path));
                    if (resumed) request.Headers.Range = new RangeHeaderValue(have, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                    var partial = resumed && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == have;
                    if (!partial && response.StatusCode != HttpStatusCode.OK)
                        throw new HttpRequestException($"The server answered HTTP {(int)response.StatusCode} for {entry.Path}.", null, response.StatusCode);
                    if (!partial) Count(have = 0);
                    await using var file = new FileStream(part, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancel);
                    using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    var buffer = new byte[81920];
                    while (true)
                    {
                        stalled.CancelAfter(Stall);
                        var read = await stream.ReadAsync(buffer, stalled.Token);
                        if (read == 0) break;
                        if (have + read > entry.Size) throw new InvalidDataException($"The server sent more than the list says for {entry.Path}.");
                        await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                        Count(have += read);
                    }
                    if (have != entry.Size) throw new IOException($"The download of {entry.Path} ended early.");
                }
            }
            catch (Exception exception) when (!cancel.IsCancellationRequested && attempt < Attempts && exception is IOException or OperationCanceledException or
                HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError })
            {
                await Task.Delay(exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), cancel);
                continue;
            }
            if (Same(part, entry))
            {
                File.Move(part, target);
                return;
            }
            File.Delete(part);
            Count(0);
            // A continued copy may have started from bytes of another version: once more from the first byte.
            if (resumed && !whole)
            {
                whole = true;
                continue;
            }
            throw new InvalidDataException($"A file from the server does not match its SHA-256 in the list ({entry.Path}). Nothing was installed; ask the host to check their game files.");
        }
    }

    private static bool Same(string path, Entry entry) => string.Equals(RetailClients.Sha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's address: each part of its path escaped, under <c>/files/</c>.</summary>
    private static Uri Address(Uri origin, string path) => new(origin, "/files/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    /// <summary>Where a listed file goes in the staging folder; a path that would leave it (which <see cref="Allowed"/> already refuses) is refused again.</summary>
    private static string Local(string staging, string path)
    {
        var full = Path.GetFullPath(Path.Combine(staging, path.Replace('/', '\\')));
        return Inside(full, staging) && !FolderMove.Same(full, staging) ? full : throw Bad($"it names a file outside the install ({path})");
    }

    private static InvalidDataException Bad(string why) => new($"The server's list of game files cannot be used: {why}.");

    /// <summary>The bar of the download: bytes done of all, reported at most ten times a second and at the end.</summary>
    private sealed class Meter(IProgress<InstallProgress>? progress, long total, string server)
    {
        private readonly Lock gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long done;
        private TimeSpan reported = TimeSpan.FromDays(-1);

        public void Add(long bytes)
        {
            lock (gate)
            {
                done += bytes;
                if (progress is null || (done != total && clock.Elapsed - reported < TimeSpan.FromMilliseconds(100))) return;
                reported = clock.Elapsed;
                progress.Report(new($"Downloading the game from {server}: {Gigabytes(done)} of {Gigabytes(total)} GB",
                    total <= 0 ? 100 : (int)Math.Clamp(done * 100 / total, 0, 100), CanCancel: true));
            }
        }
    }
}
