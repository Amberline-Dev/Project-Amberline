using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// The host's update-check choice and what the last check found (<c>data\host\updates.json</c>); no file means no choice yet. Batch 666:
/// <see cref="Zip"/>, <see cref="Hash"/> and <see cref="Size"/> are the release's zip and its <c>.sha256</c>, kept only when GitHub named
/// them at the project's own release address (<see cref="HostUpdates.Asset"/>).
/// </summary>
public sealed record HostUpdateState(bool? Check = null, DateTimeOffset? LastCheckUtc = null, string? Latest = null, string? Page = null,
    string? Notes = null, string? Zip = null, string? Hash = null, long? Size = null);

/// <summary>
/// Where Updates asks GitHub and downloads a release (<see cref="Network"/>), and how it starts the downloaded <c>Start Amberline.cmd</c>
/// (<see cref="Start"/>, the start <see cref="HostUpdates.UpdateAsync"/> makes; true once Windows started it). DIAGNOSTICS: a check passes
/// a fake release server and a list; the default, <see cref="Offline"/>, reaches no network and starts nothing. Only
/// <see cref="HostMode.MainAsync"/> passes <see cref="Live"/>.
/// </summary>
public sealed record HostReleases(HttpMessageHandler Network, Func<ProcessStartInfo, bool> Start)
{
    public static HostReleases Offline { get; } = new(new NoNetwork(), _ => false);
    public static HostReleases Live { get; } = new(new SocketsHttpHandler { UseCookies = false }, StartWindow);

    private static bool StartWindow(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception ex) { Console.Error.WriteLine($"Update, start: {ex.Message}"); return false; }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("This Amberline has no network for updates.");
    }
}

/// <summary>
/// NOT RETAIL: the home host's updates (retail had no host program to update). The check runs only with the host's consent, asked at
/// the first run (ticked by default, owner, 2 November 2026) and changed on <c>/host/updates</c>. At most once a day it sends one
/// HTTPS GET for the project's latest GitHub release with <c>User-Agent: Amberline</c> and <c>Accept</c>, and nothing else: no
/// cookie, token, version or name of the PC (GitHub sees the host's address, as the page says). Check now sends the same request
/// once. Batch 666 (owner, 3 November 2026: "auto-updating of the server with one click"): Update now (<see cref="UpdateAsync"/>)
/// downloads the release's zip and its <c>.sha256</c>, only from the release's own addresses, checks the hash, the size and the version
/// inside, and starts the unpacked <c>Start Amberline.cmd</c> as a double-click would, with <see cref="HostInstall.ConfirmedOption"/> (the
/// page asked already), so <see cref="HostInstall"/> updates and rolls back as for a zip from the download page. Nothing downloads or
/// installs by itself.
/// </summary>
public static class HostUpdates
{
    public const string ReleasesPage = "https://github.com/Amberline-Dev/Project-Amberline/releases";
    public const string LatestRelease = "https://api.github.com/repos/Amberline-Dev/Project-Amberline/releases/latest";
    public const string Notice = "Tell me about new versions (Amberline asks GitHub once a day; GitHub sees your address).";
    public const string InstallPath = "/host/updates/install", StatusPath = InstallPath + "/status.json";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private const int NotesLength = 600;
    /// <summary>The largest release zip Update now downloads (a release is about 220 MB).</summary>
    public const long MaxZipBytes = 1L << 30;
    /// <summary>A download that receives nothing for this long has stopped.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    // Update now's answers when nothing was installed: one sentence each (TROUBLESHOOTING.md lists them).
    public const string NothingNewer = "There is no newer Amberline to install, so nothing was installed.";
    public const string NoDownload = "GitHub names no download for this version that Amberline can check, so nothing was installed.";
    public const string StillOpen = "The window of the last update is still open: let it finish or close it, then try again.";
    public const string DownloadFailed = "Amberline could not download the update from GitHub, so nothing was installed.";
    public const string Incomplete = "The download was cut off or is not the size GitHub names, so nothing was installed.";
    public const string WrongHash = "The download does not match its SHA-256, so nothing was installed.";
    public const string WrongVersion = "The download holds another Amberline version than the release names, so nothing was installed.";
    public const string Damaged = "The download is not a whole Amberline release, so nothing was installed.";
    public const string DiskFailed = "Amberline could not save or unpack the download (the disk may be full), so nothing was installed.";
    public const string NotStarted = "Windows did not start the new Amberline's installer, so nothing was installed.";

    public static string StateFile(string data) => Path.Combine(data, "host", "updates.json");

    /// <summary>Where Update now downloads and unpacks a release: in the data folder, which every install keeps across updates.</summary>
    public static string UpdatesFolder(string data) => Path.Combine(data, "updates");

    public static string ZipName(Version version) => $"Amberline-{version}-win-x64.zip";

    /// <summary>A release file's only accepted address: the project's own release of that version (tags are v1.2.3).</summary>
    public static string Asset(Version version, string name) => $"{ReleasesPage}/download/v{version}/{name}";

    public static HostUpdateState Read(string data)
    {
        try { return JsonSerializer.Deserialize<HostUpdateState>(File.ReadAllText(StateFile(data)), HostFiles.Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static void SaveChoice(string data, bool check) => HostFiles.Write(StateFile(data), Read(data) with { Check = check });

    /// <summary>
    /// One check when it is due (consent, and a day since the last one), or at once when <paramref name="force"/> (Check now). A
    /// failed request counts as the day's check too, so an outage is not asked again every hour.
    /// </summary>
    public static async Task<HostUpdateState> CheckAsync(string data, HttpClient client, DateTimeOffset now, bool force = false,
        CancellationToken cancel = default)
    {
        var state = Read(data);
        if (!force && (state.Check != true || now - state.LastCheckUtc < Interval)) return state;
        state = state with { LastCheckUtc = now };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.TryAddWithoutValidation("User-Agent", "Amberline");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await client.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var page = Text(json.RootElement, "html_url");
            var notes = Text(json.RootElement, "body")?.Trim();
            var latest = HostVersion.Parse(Text(json.RootElement, "tag_name"));
            var (zip, hash, size) = latest is null ? default : Assets(json.RootElement, latest);
            state = state with
            {
                Latest = latest?.ToString(),
                // The page links only into the project's own releases, whatever the answer names.
                Page = page is not null && page.StartsWith(ReleasesPage + "/", StringComparison.Ordinal) ? page : ReleasesPage,
                Notes = notes is { Length: > NotesLength } ? notes[..NotesLength] + "..." : notes is { Length: > 0 } ? notes : null,
                Zip = zip, Hash = hash, Size = size,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Update check: {ex.Message}");
        }
        HostFiles.Write(StateFile(data), state);
        return state;
    }

    /// <summary>The release's zip (with its size) and <c>.sha256</c>, only when both are at <see cref="Asset"/>; else none.</summary>
    private static (string? Zip, string? Hash, long? Size) Assets(JsonElement root, Version version)
    {
        string? zip = null, hash = null;
        long? size = null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return default;
        foreach (var asset in assets.EnumerateArray())
        {
            var url = Text(asset, "browser_download_url");
            if (url == Asset(version, ZipName(version)) && asset.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number &&
                bytes.TryGetInt64(out var length)) (zip, size) = (url, length);
            else if (url == Asset(version, ZipName(version) + ".sha256")) hash = url;
        }
        return zip is null || hash is null ? default : (zip, hash, size);
    }

    /// <summary>
    /// Update now: downloads the release <paramref name="state"/> names into <see cref="UpdatesFolder"/>, checks it and starts its
    /// <c>Start Amberline.cmd</c> with <paramref name="start"/> (<see cref="StartOf"/>). Null once started; else why nothing was installed, in one sentence, and
    /// the folder is gone. The rules, each a refusal: the release is newer than <paramref name="running"/>; both files are at
    /// <see cref="Asset"/> (a changed <c>updates.json</c> names nothing else) and end on HTTPS; the zip has the size GitHub names (at most
    /// <see cref="MaxZipBytes"/>) and the SHA-256 its <c>.sha256</c> names; its <c>app\BUILD.json</c> names the release's version.
    /// <paramref name="progress"/> hears the bytes downloaded.
    /// </summary>
    public static async Task<string?> UpdateAsync(string data, HttpClient client, HostUpdateState state, Version? running,
        Func<ProcessStartInfo, bool> start, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        if (Refusal(state, running) is { } refused) return refused;
        var latest = HostVersion.Parse(state.Latest)!;
        var name = ZipName(latest);
        var folder = UpdatesFolder(data);
        // The last update's folder goes first. A window that still runs its installer holds it (its own folder is that window's), and
        // then nothing changes: a folder is renamed whole or not at all.
        if (Directory.Exists(folder))
        {
            var old = folder + ".old";
            Remove(old);
            try { Directory.Move(folder, old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Update, last folder: {ex.Message}"); return StillOpen; }
            Remove(old);
        }
        var started = false;
        try
        {
            Directory.CreateDirectory(folder);
            byte[]? expected;
            using (var answer = await GetAsync(client, state.Hash!, HttpCompletionOption.ResponseContentRead, cancel))
                expected = ParseHash(await answer.Content.ReadAsStringAsync(cancel), name);
            if (expected is null) return WrongHash;
            var zip = Path.Combine(folder, name);
            var (bytes, actual) = await DownloadAsync(client, state.Zip!, zip, state.Size!.Value, progress, cancel);
            if (bytes != state.Size) return Incomplete;
            if (!actual.AsSpan().SequenceEqual(expected)) return WrongHash;
            // .NET's extraction refuses an entry that would land outside the folder.
            var unpacked = Path.Combine(folder, "Amberline-" + latest);
            ZipFile.ExtractToDirectory(zip, unpacked);
            File.Delete(zip);
            if (HostVersion.Read(Path.Combine(unpacked, "app")).Version != latest) return WrongVersion;
            var command = Path.Combine(unpacked, "Start Amberline.cmd");
            if (!File.Exists(command) || !File.Exists(Path.Combine(unpacked, "app", "platform", HostInstall.PlatformFile))) return Damaged;
            if (!start(StartOf(command))) return NotStarted;
            started = true;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Update, download: {ex.Message}");
            return DownloadFailed;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Update, unpack: {ex.Message}");
            return Damaged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Update, files: {ex.Message}");
            return DiskFailed;
        }
        finally
        {
            // The started installer runs from the folder; anything else leaves nothing behind.
            if (!started) Remove(folder);
        }
    }

    /// <summary>
    /// The unpacked <c>Start Amberline.cmd</c> as a double-click starts it (a new window, its own folder), with
    /// <see cref="HostInstall.ConfirmedOption"/>: the host confirmed on the page, so the window does not ask again (owner: one click).
    /// </summary>
    public static ProcessStartInfo StartOf(string command) =>
        new(command, HostInstall.ConfirmedOption) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(command) };

    /// <summary>
    /// Why the release <paramref name="state"/> names cannot be installed over <paramref name="running"/>, before anything is downloaded:
    /// it is not newer (a development build, without a version, never updates from here), or its files are not at <see cref="Asset"/> or
    /// have no size Amberline downloads. Null when it can.
    /// </summary>
    public static string? Refusal(HostUpdateState state, Version? running)
    {
        // Version's operators order null first.
        if (HostVersion.Parse(state.Latest) is not { } latest || running is null || latest <= running) return NothingNewer;
        var name = ZipName(latest);
        return state.Zip != Asset(latest, name) || state.Hash != Asset(latest, name + ".sha256") || state.Size is not (> 0 and <= MaxZipBytes) ? NoDownload : null;
    }

    /// <summary>One GET of a release file; the final address (after GitHub's redirect to its download host) must be HTTPS.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, HttpCompletionOption completion, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Amberline");
        var response = await client.SendAsync(request, completion, cancel);
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" } || !response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The release file answered {(int)status} or left HTTPS.");
        }
        return response;
    }

    /// <summary>The zip into <paramref name="path"/> and its SHA-256 as it comes; it stops one byte past <paramref name="size"/>.</summary>
    private static async Task<(long Bytes, byte[] Sha256)> DownloadAsync(HttpClient client, string url, string path, long size, IProgress<long>? progress,
        CancellationToken cancel)
    {
        using var answer = await GetAsync(client, url, HttpCompletionOption.ResponseHeadersRead, cancel);
        await using var input = await answer.Content.ReadAsStreamAsync(cancel);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var buffer = new byte[1 << 16];
        long bytes = 0;
        while (bytes <= size)
        {
            stall.CancelAfter(Stall);
            var read = await input.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            bytes += read;
            progress?.Report(Math.Min(bytes, size));
        }
        return (bytes, sha.GetHashAndReset());
    }

    /// <summary>The SHA-256 of a <c>.sha256</c> file as the release build writes it ("hex  name"), or the hex alone; null for anything else.</summary>
    private static byte[]? ParseHash(string text, string name)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (1 or 2) || parts[0].Length != 64 || parts.Length == 2 && parts[1].TrimStart('*') != name) return null;
        try { return Convert.FromHexString(parts[0]); }
        catch (FormatException) { return null; }
    }

    private static void Remove(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Update, clean-up: {ex.Message}"); }
    }

    /// <summary>The first run's question, on its last step (the address, <see cref="HostSteps"/>); ticked unless the host said no.</summary>
    public static string SetupQuestion(bool check) => TickBox("updates", check);

    /// <summary>One Update now: its release, the bytes downloaded, then how it ended.</summary>
    private sealed class Job(string version, long size) : IProgress<long>
    {
        private long _done;
        public string Version { get; } = version;
        public long Size { get; } = size;
        public long Done => Interlocked.Read(ref _done);
        public volatile string? Problem;
        public volatile bool Started;
        public bool Running => Problem is null && !Started;
        public void Report(long value) => Interlocked.Exchange(ref _done, value);
    }

    /// <summary>
    /// The Updates page (GET and POST <c>/host/updates</c>), the daily check and Update now (<see cref="InstallPath"/>: POST starts it, one
    /// at a time, GET shows it, <see cref="StatusPath"/> is what page.js reads every second), in the full home Platform. They are host
    /// routes behind <see cref="HostGate"/>. <paramref name="games"/> counts the matches an update would end: with any, Update now asks first.
    /// </summary>
    public static void Map(WebApplication app, HostLoop loop, Func<int> games)
    {
        var data = loop.Paths.Data;
        var running = Path.GetFullPath(Path.Combine(loop.Paths.App, ".."));
        // The timeout and the buffer cap cover the check and the small .sha256; the zip is read as a stream (DownloadAsync).
        var client = new HttpClient(loop.Releases.Network, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        app.Lifetime.ApplicationStopped.Register(client.Dispose);
        app.Lifetime.ApplicationStarted.Register(() =>
            _ = DailyAsync(data, client, loop.Screen, HostVersion.Read(running).Version, app.Lifetime.ApplicationStopping));
        app.MapGet("/host/updates", (HttpContext context, IAntiforgery antiforgery) =>
            HostMode.Page(Render(Program.RegistrationToken(context, antiforgery), HostVersion.Read(running), Read(data), context.Request.Query["saved"] == "1")));
        app.MapPost("/host/updates", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            SaveChoice(data, form["check"] == "on");
            if (form["do"] == "now") await CheckAsync(data, client, DateTimeOffset.UtcNow, force: true, context.RequestAborted);
            return Results.Redirect("/host/updates?saved=1");
        });

        var gate = new Lock();
        Job? job = null;
        app.MapPost(InstallPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return HostMode.Page("<h1>The page expired</h1><p><a href=/host/updates>Open Updates</a> and try again.</p>", StatusCodes.Status400BadRequest);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var state = Read(data);
            // A game in progress: the warning first, and the host's confirm, which names the games it warned about.
            if (games() is var count and > 0 && !(int.TryParse(form["games"], NumberStyles.None, CultureInfo.InvariantCulture, out var warned) && warned >= count))
                return Confirm(Program.RegistrationToken(context, antiforgery), state, count, form.ContainsKey("games"));
            var version = HostVersion.Read(running).Version;
            lock (gate)
            {
                // One at a time; a release that cannot be installed is refused at once, before anything is downloaded.
                if (job is { Running: true }) { }
                else if (Refusal(state, version) is { } refused) job = new(state.Latest ?? "", 0) { Problem = refused };
                else
                {
                    var next = job = new(state.Latest!, state.Size!.Value);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (await UpdateAsync(data, client, state, version, loop.Releases.Start, next, app.Lifetime.ApplicationStopping) is { } problem) next.Problem = problem;
                            else next.Started = true;
                        }
                        // A job that ends otherwise must still end, or its page would wait for ever.
                        catch (Exception ex) { Console.Error.WriteLine($"Update: {ex}"); next.Problem = DiskFailed; }
                    });
                }
            }
            return Results.Redirect(InstallPath);
        });
        app.MapGet(InstallPath, () => job is { } shown ? Show(shown) : Results.Redirect("/host/updates"));
        app.MapGet(StatusPath, () => job is { } shown ? Results.Json(new
        {
            state = shown.Problem is not null ? "failed" : shown.Started ? "started" : "downloading",
            phase = Phase(shown), detail = Detail(shown), percent = Percent(shown),
        }) : Results.NotFound());
    }

    /// <summary>The page's and the button's words while the zip comes in.</summary>
    public static string Downloading(string version, long size) =>
        $"Downloading Amberline {version} ({HostUninstall.Size(size)}). This takes a few minutes.";

    private static string Phase(Job job) => job.Done < job.Size ? Downloading(job.Version, job.Size) : $"Installing Amberline {job.Version}…";
    private static string Detail(Job job) => $"{HostUninstall.Size(job.Done)} of {HostUninstall.Size(job.Size)}";
    private static int Percent(Job job) => job.Size > 0 ? (int)(job.Done * 100 / job.Size) : 0;

    private static string UpdateForm(string token, HostUpdateState state, int games, string button) =>
        $"<form method=post action={InstallPath}{HostPages.Wait(Downloading(state.Latest ?? "", state.Size ?? 0))}>{token}" +
        (games > 0 ? $"<input type=hidden name=games value={games}>" : "") + $"<div class=buttons><button type=submit>{button}</button>";

    /// <summary>Update now while games run: the warning, and Update now again to confirm (Cancel goes back to Updates).</summary>
    private static IResult Confirm(string token, HostUpdateState state, int games, bool again) => HostMode.Page(
        "<h1>Update Amberline now?</h1>" + (again ? "<p class=problem role=alert>A game started since this page was opened.</p>" : "") +
        $"<p class=warn>Updating ends the {(games == 1 ? "game" : "games")} in progress.</p>" +
        UpdateForm(token, state, games, "Update now") + " <a class=\"button secondary\" href=/host/updates>Cancel</a></div></form>",
        again ? StatusCodes.Status409Conflict : StatusCodes.Status200OK);

    /// <summary>Update now's page: the download's progress (page.js follows <see cref="StatusPath"/>), then how it ended.</summary>
    private static IResult Show(Job job)
    {
        var version = WebUtility.HtmlEncode(job.Version);
        if (job.Problem is { } problem)
            return HostMode.Page($"<h1>Amberline was not updated</h1>{HostPages.Errors((problem, null))}<p>The Amberline you have keeps running.</p>" +
                "<div class=actions><a href=/host/updates>Back</a></div>");
        if (job.Started)
            return HostMode.Page($"""
                <h1>Installing Amberline {version}</h1>
                <p class=notice role=status>A new Amberline window opened (look on the taskbar). It installs Amberline {version} now.</p>
                <p>That window stops this Amberline, installs the new one and starts it, and its page opens in your browser. Your settings and
                friends stay. If the new version does not start, the one you have comes back.</p>
                """);
        var percent = Percent(job);
        return HostMode.Page($"""
            <h1>Updating Amberline</h1>
            <p>Keep this page and the Amberline window open.</p>
            <div aria-live=polite><p id=update-phase class=headline>{WebUtility.HtmlEncode(Phase(job))}</p></div>
            <label for=update-bar>Progress</label><progress id=update-bar max=100 value={percent}>{percent}%</progress>
            <p id=update-detail>{WebUtility.HtmlEncode(Detail(job))}</p>
            <noscript><p class=warn>Reload the page to see how far the download is.</p></noscript>
            """);
    }

    /// <summary>
    /// The Updates page: the version, a newer release with Update now (when GitHub named its download), its download page and the
    /// steps by hand, the tick box and Check now; with <paramref name="saved"/> (after Save or Check now) the choice is confirmed.
    /// </summary>
    public static string Render(string token, (Version? Version, DateTimeOffset? Built) running, HostUpdateState state, bool saved = false)
    {
        var have = running.Version is null ? "<p>This is a development build without a version number.</p>"
            : $"<p>You have Amberline {running.Version}" +
              (running.Built is { } built ? $" (built {built.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)})" : "") + ".</p>";
        var latest = HostVersion.Parse(state.Latest);
        var update = state is { Zip: not null, Size: { } size }
            ? UpdateForm(token, state, 0, "Update now") + "</div></form>" +
              $"<p class=tip>Update now downloads Amberline {latest} ({HostUninstall.Size(size)}) from GitHub, checks it and installs it. " +
              "Your settings and friends stay.</p><h3>Or update by hand</h3>"
            : "";
        // Version's operators order null first, so a development build (no version) is never "older" than a release.
        var news = latest is not null && running.Version is not null && latest > running.Version ? $"""
            <h2>Amberline {latest} is out</h2>
            {(state.Notes is null ? "" : $"<p class=notes>What's new: {WebUtility.HtmlEncode(state.Notes)}</p>")}
            {update}
            <p><a href="{WebUtility.HtmlEncode(state.Page ?? ReleasesPage)}" target=_blank rel="noopener noreferrer">Open the download page</a></p>
            <ol><li>Download the zip and open it.</li>
            <li>Double-click Start Amberline. Your settings and friends stay.</li></ol>
            <p>Your friends' games update by themselves.</p>
            """
            : latest is not null && running.Version is not null && state.LastCheckUtc is { } last
                ? $"<p>This is the newest Amberline (checked on {last.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}).</p>"
                : "";
        return $"""
            <h1>Updates</h1>
            {(saved ? "<p class=notice role=status>Saved.</p>" : "")}
            {have}
            {news}
            <form method=post action=/host/updates>{token}
            {TickBox("check", state.Check == true)}
            <div class=buttons><button type=submit name=do value=save>Save</button>
            <button type=submit name=do value=now class=secondary{HostPages.Wait("Asking GitHub for a new version. This takes up to 20 seconds.")}>Check now</button></div>
            <p class=tip>Check now asks GitHub once. Amberline never installs anything by itself.</p>
            </form>
            """;
    }

    private static string TickBox(string name, bool ticked) =>
        $"<label class=choice><input type=checkbox name={name} value=on{(ticked ? " checked" : "")}>{Notice}</label>";

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>Every hour, the check if it is due; a newer version found is also named in the window once.</summary>
    private static async Task DailyAsync(string data, HttpClient client, TextWriter screen, Version? running, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                try
                {
                    var before = Read(data).Latest;
                    var state = await CheckAsync(data, client, DateTimeOffset.UtcNow, cancel: stop);
                    if (state.Latest != before && running is not null && HostVersion.Parse(state.Latest) is { } latest && latest > running)
                        screen.WriteLine($"Amberline {state.Latest} is out. To update, choose Updates on Amberline's page, then Update now. " +
                            "Your settings and friends stay.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Update check: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromHours(1), stop);
            }
        }
        catch (OperationCanceledException) { }
    }
}
