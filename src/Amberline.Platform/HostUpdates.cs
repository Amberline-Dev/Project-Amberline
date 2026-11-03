using System.Globalization;
using System.Net;
using System.Text.Json;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>The host's update-check choice and what the last check found (<c>data\host\updates.json</c>); no file means no choice yet.</summary>
public sealed record HostUpdateState(bool? Check = null, DateTimeOffset? LastCheckUtc = null, string? Latest = null, string? Page = null,
    string? Notes = null);

/// <summary>
/// NOT RETAIL: the home host's update check (retail had no host program to update). It runs only with the host's consent, asked at
/// the first run (ticked by default, owner, 2 November 2026) and changed on <c>/host/updates</c>. At most once a day it sends one
/// HTTPS GET for the project's latest GitHub release with <c>User-Agent: Amberline</c> and <c>Accept</c>, and nothing else: no
/// cookie, token, version or name of the PC (GitHub sees the host's address, as the page says). Check now sends the same request
/// once. It never downloads or installs: the page links the download page, and the new zip's Start Amberline does the update.
/// </summary>
public static class HostUpdates
{
    public const string ReleasesPage = "https://github.com/Amberline-Dev/Project-Amberline/releases";
    public const string LatestRelease = "https://api.github.com/repos/Amberline-Dev/Project-Amberline/releases/latest";
    public const string Notice = "Tell me about new versions (Amberline asks GitHub once a day; GitHub sees your address).";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private const int NotesLength = 600;

    public static string StateFile(string data) => Path.Combine(data, "host", "updates.json");

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
            state = state with
            {
                Latest = HostVersion.Parse(Text(json.RootElement, "tag_name"))?.ToString(),
                // The page links only into the project's own releases, whatever the answer names.
                Page = page is not null && page.StartsWith(ReleasesPage + "/", StringComparison.Ordinal) ? page : ReleasesPage,
                Notes = notes is { Length: > NotesLength } ? notes[..NotesLength] + "..." : notes is { Length: > 0 } ? notes : null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Update check: {ex.Message}");
        }
        HostFiles.Write(StateFile(data), state);
        return state;
    }

    /// <summary>The first run's question, on its last step (the address, <see cref="HostSteps"/>); ticked unless the host said no.</summary>
    public static string SetupQuestion(bool check) => TickBox("updates", check);

    /// <summary>The Updates page (GET and POST <c>/host/updates</c>) and the daily check, in the full home Platform.</summary>
    public static void Map(WebApplication app, HostLoop loop)
    {
        var data = loop.Paths.Data;
        var running = Path.GetFullPath(Path.Combine(loop.Paths.App, ".."));
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
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
    }

    /// <summary>
    /// The Updates page: the version, a newer release with its download page and the steps, the tick box and Check now; with
    /// <paramref name="saved"/> (after Save or Check now) the choice is confirmed.
    /// </summary>
    public static string Render(string token, (Version? Version, DateTimeOffset? Built) running, HostUpdateState state, bool saved = false)
    {
        var have = running.Version is null ? "<p>This is a development build without a version number.</p>"
            : $"<p>You have Amberline {running.Version}" +
              (running.Built is { } built ? $" (built {built.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)})" : "") + ".</p>";
        var latest = HostVersion.Parse(state.Latest);
        // Version's operators order null first, so a development build (no version) is never "older" than a release.
        var news = latest is not null && running.Version is not null && latest > running.Version ? $"""
            <h2>Amberline {latest} is out</h2>
            {(state.Notes is null ? "" : $"<p class=notes>What's new: {WebUtility.HtmlEncode(state.Notes)}</p>")}
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
                        screen.WriteLine($"Amberline {state.Latest} is out. Download it from {state.Page}, unzip it and double-click " +
                            "Start Amberline.cmd. Your settings and friends stay.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Update check: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromHours(1), stop);
            }
        }
        catch (OperationCanceledException) { }
    }
}
