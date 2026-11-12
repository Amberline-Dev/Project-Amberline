using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Server;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the dashboard's cards about games and the disk, and its support log (Amberline Host design, section 2.5, batch 640;
/// retail's servers were run by Riot's staff and had no host page). A watch reads the match servers and the data folder's drive every
/// <see cref="Every"/>:
/// <list type="bullet">
/// <item>A-NET-10: a friend's seat never connected to its game within <see cref="ConnectWait"/>. A friend's seat is one whose login came
/// through the friends' tunnel (<see cref="PlatformSessions.ThroughTunnel"/>), read once when the watch first sees the seat; logging in
/// goes through the sign-in port, the game through the game ports, so the sign-in working says nothing about the game ports.</item>
/// <item>A-RUN-01: a match server of this start stopped unexpectedly or could not start (<see cref="GameManager"/>'s Failed).</item>
/// <item>A-DATA-02: the drive of the data folder has less than <see cref="LowDisk"/> free.</item>
/// </list>
/// Hide ends the first two until the problem changes: the hidden seats and games are saved in host\state.json (batch 751), so a restart
/// keeps them hidden, and a seat that connects (or a game that is gone) is forgotten. The support log
/// (<see cref="PagePath"/>) is a short report with every name and address replaced by a mark (<see cref="Mask"/>).
/// </summary>
public sealed class HostSupport : IAsyncDisposable
{
    public const string PagePath = HostDashboard.PagePath + "/support";
    public const string FilePath = PagePath + "/amberline-support.txt";
    public const string HidePath = PagePath + "/hide";

    /// <summary>Below this much free space the disk card shows: an update copies the program (a few hundred MB), and games write logs.</summary>
    public const long LowDisk = 1_000_000_000;

    /// <summary>Below this much the card says the disk is full: settings, games and the log may not save at all.</summary>
    public const long FullDisk = 100_000_000;

    private const int LogLines = 100;
    private const int LogTail = 64 * 1024;
    private static readonly HostGuest Admin = new("", "", "", Admin: true);

    private sealed class Seat
    {
        public required string Username;
        public required bool Friend;
        // The first and the last look that found its game waiting or playing without it: a game that ends stops the wait.
        public DateTimeOffset? WaitingSince, WaitedUntil;
        public bool Connected;
    }

    private sealed record Failure(DateTimeOffset At, string? Error);

    private readonly HostLoop _loop;
    private readonly PlatformSettings _settings;
    private readonly Func<IReadOnlyList<RemoteGame>> _games;
    private readonly Func<long, (string Username, bool ThroughTunnel)?> _seat;
    private readonly Func<IReadOnlyList<(string Username, string Summoner)>> _logins;
    private readonly Func<IReadOnlyList<(string Username, string Summoner)>> _deleted;
    private readonly HashSet<Guid> _before;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Game, int Seat), Seat> _seats = [];
    private readonly Dictionary<Guid, Failure> _failed = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _watch = Task.CompletedTask;
    private long? _free;
    private HostDashboard? _dashboard;
    private HostSteps? _steps;
    // The dashboard's store once Map gives it (the same instance the attention cards use); this folder's own store until then.
    private HostHidden _hidden;

    /// <param name="games">Every match the manager lists; the ones listed now are from before this start and never raise a card.</param>
    /// <param name="seat">The login behind a seat's summoner id: its username and whether it came through the friends' tunnel.</param>
    /// <param name="logins">Every login, whose usernames and summoner names the support log hides.</param>
    /// <param name="deleted">Batch 748: every login the host deleted, whose names the support log hides too (they are not counted).</param>
    public HostSupport(HostLoop loop, PlatformSettings settings, Func<IReadOnlyList<RemoteGame>> games,
        Func<long, (string Username, bool ThroughTunnel)?> seat, Func<IReadOnlyList<(string Username, string Summoner)>> logins,
        Func<IReadOnlyList<(string Username, string Summoner)>>? deleted = null)
    {
        _loop = loop;
        _settings = settings;
        _games = games;
        _seat = seat;
        _logins = logins;
        _deleted = deleted ?? (() => []);
        _before = [.. games().Select(game => game.Id)];
        _hidden = new HostHidden(loop.Paths.Data);
    }

    /// <summary>The real Platform's watch: the match manager, the logins and the sessions' tunnel mark.</summary>
    public static HostSupport For(HostLoop loop, PlatformSettings settings, GameManager manager, PlatformAccounts accounts, PlatformSessions sessions) =>
        new(loop, settings, () => manager.List(Admin),
            summoner => accounts.FindSummoner(summoner) is { } account ? (account.Username, sessions.ThroughTunnel(account.Id)) : null,
            () => [.. accounts.ListAccounts().Select(account => (account.Username, account.Summoner))],
            () => [.. accounts.ListDeleted().Select(gone => (gone.Username, gone.Summoner))]);

    public TimeSpan Every { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a friend's seat may wait for its game before A-NET-10. Reconstruction: the stock client connects to the game port as it
    /// starts, before its loading screen; two minutes leave room for a slow PC starting the game.
    /// </summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The free bytes of the drive holding a folder, or null when Windows cannot tell (a network folder, for example).</summary>
    public Func<string, long?> FreeSpace { get; init; } = DriveFree;

    /// <summary>DIAGNOSTICS: test seam. The PC's name and the Windows user's name the support log hides.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    public string UserName { get; init; } = Environment.UserName;

    /// <summary>The watch, once the listeners are up, and the support log's and Hide's routes.</summary>
    public void Map(WebApplication app, HostDashboard dashboard, HostSteps? steps)
    {
        _dashboard = dashboard;
        _steps = steps;
        _hidden = dashboard.Hidden;
        app.Lifetime.ApplicationStarted.Register(() => _watch = WatchAsync());
        app.MapPost(HidePath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Expired;
            var card = (await context.Request.ReadFormAsync(context.RequestAborted))["card"].ToString();
            lock (_gate)
            {
                // NOT RETAIL (batch 751): the hidden seats and games are saved, so they stay hidden across a restart until the problem changes.
                if (card == "A-NET-10") foreach (var pair in _seats) { if (Stuck(pair.Value)) _hidden.Add("A-NET-10", SeatKey(pair.Key)); }
                else if (card == "A-RUN-01") foreach (var game in _failed.Keys) _hidden.Add("A-RUN-01", game.ToString("D"));
                else return Results.BadRequest();
            }
            return Results.Redirect(HostDashboard.PagePath);
        });
        app.MapGet(PagePath, () => HostMode.Page($"""
            <h1>Support log</h1>
            <p>Send this report with your issue report. Names, addresses and passwords are left out; read it first.</p>
            <label for=report>Support log</label>
            <textarea id=report readonly rows=16 spellcheck=false>{Boxed(Report(DateTimeOffset.Now))}</textarea>
            <div class=buttons><button type=button data-copy=report>Copy</button> <span id=copy-status role=status></span>
            <a class="button secondary" href={FilePath} download>Download</a></div>
            """));
        app.MapGet(FilePath, () => Results.File(Encoding.UTF8.GetBytes(Report(DateTimeOffset.Now)), "text/plain; charset=utf-8", "amberline-support.txt"));
    }

    /// <summary>The cards' buttons, shown by the page while a card names their action.</summary>
    public static string Forms(string token) =>
        $"<form method=post action={HidePath} data-action=seat hidden>{token}<input type=hidden name=card value=A-NET-10>" +
        "<button type=submit class=secondary>Hide<span class=vh> the message about the games that could not connect</span></button></form>" +
        $"<form method=get action={PagePath} data-action=run hidden><button type=submit class=secondary>Open the support log</button></form>" +
        $"<form method=post action={HidePath} data-action=run hidden>{token}<input type=hidden name=card value=A-RUN-01>" +
        "<button type=submit class=secondary>Hide<span class=vh> the message about the game that stopped</span></button></form>";

    /// <summary>One look at the games and the disk; the watch calls it every <see cref="Every"/>.</summary>
    public void Observe(DateTimeOffset now)
    {
        var games = _games();
        var free = FreeSpace(_loop.Paths.Data);
        lock (_gate)
        {
            _free = free;
            foreach (var game in games.Where(game => !_before.Contains(game.Id)))
            {
                if (game.State == "Failed")
                {
                    _failed.TryAdd(game.Id, new(now, game.Error));
                    continue;
                }
                var live = game.State is "Waiting for players" or "Playing";
                foreach (var place in game.Seats.Where(place => !place.Player.IsBot && place.Player.PlayerId is not null))
                {
                    if (!_seats.TryGetValue((game.Id, place.Id), out var seat))
                    {
                        // A seat first seen after its game ended tells nothing about where its login came from.
                        if (!live && game.State != "Starting") continue;
                        var login = _seat(place.Player.PlayerId!.Value);
                        _seats[(game.Id, place.Id)] = seat = new() { Username = login?.Username ?? place.Player.Name, Friend = login?.ThroughTunnel == true };
                    }
                    // A friend whose game connects once has working game ports: their earlier card goes too.
                    if (place.Connected) foreach (var same in _seats.Values.Where(other => other.Username == seat.Username)) same.Connected = true;
                    else if (live)
                    {
                        seat.WaitingSince ??= now;
                        seat.WaitedUntil = now;
                    }
                }
            }
            // Seats of games that ended are forgotten, unless they waited long enough to show a card.
            var listed = games.Select(game => game.Id).ToHashSet();
            foreach (var key in _seats.Where(pair => !listed.Contains(pair.Key.Game) && !Stuck(pair.Value)).Select(pair => pair.Key).ToArray())
                _seats.Remove(key);
            // Batch 751: a saved seat is forgotten once its login connected, or once its game is gone; a saved stop once this start no longer
            // knows it as stopped; a saved disk card once the drive has room again. One still waiting or still stopped stays hidden.
            var stopped = games.Where(game => game.State == "Failed").Select(game => game.Id).ToHashSet();
            // A game listed before this start (_before) never raises its cards again, so its saved entries go too (a restart forgets them).
            _hidden.Keep("A-NET-10", subject => ParseSeat(subject) is { } key && !_before.Contains(key.Game) &&
                (_seats.TryGetValue(key, out var known) ? !known.Connected : listed.Contains(key.Game)));
            _hidden.Keep("A-RUN-01", subject => Guid.TryParse(subject, out var game) && !_before.Contains(game) &&
                (stopped.Contains(game) || _failed.ContainsKey(game)));
            if (free is { } bytes) _hidden.Keep("A-DATA-02", subject => bytes < LowDisk && subject == DiskCard(Drive(_loop.Paths.Data), bytes).Subject);
        }
    }

    /// <summary>The cards of the last look; <paramref name="homeOnly"/>: the host chose the home network only (no router).</summary>
    public IReadOnlyList<DashboardCard> Cards(bool homeOnly)
    {
        var cards = new List<DashboardCard>();
        lock (_gate)
        {
            var stuck = _seats.Where(pair => Stuck(pair.Value) && !_hidden.Contains("A-NET-10", SeatKey(pair.Key))).Select(pair => pair.Value.Username)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (stuck.Length > 0) cards.Add(SeatsCard(stuck, homeOnly));
            var failures = _failed.Where(pair => !_hidden.Contains("A-RUN-01", pair.Key.ToString("D"))).Select(pair => pair.Value).ToArray();
            if (failures.Length > 0) cards.Add(StoppedCard(failures.Length, failures.Max(failure => failure.At)));
            if (_free is { } free && free < LowDisk) cards.Add(DiskCard(Drive(_loop.Paths.Data), free));
        }
        return cards;
    }

    /// <summary>A-NET-10 for the friends whose game could not connect.</summary>
    public DashboardCard SeatsCard(IReadOnlyList<string> usernames, bool homeOnly)
    {
        var names = usernames.Count switch
        {
            1 => usernames[0],
            2 => $"{usernames[0]} and {usernames[1]}",
            3 => $"{usernames[0]}, {usernames[1]} and {usernames[2]}",
            _ => $"{usernames[0]}, {usernames[1]} and {usernames.Count - 2} more users",
        };
        var one = usernames.Count == 1;
        // The dashboard's are the ports of the games that may run now (batch 684: Settings changes them while Amberline runs).
        var ports = _dashboard?.GamePorts ?? HostDashboard.GamePortsText(_settings.FirstGamePort, _settings.MaxGames);
        var check = homeOnly
            ? $"Check Windows Firewall (the Network list above) and that {(one ? "their PC is" : "their PCs are")} on your home network."
            : $"Check that your router forwards {ports} to this PC and that Windows Firewall lets Amberline in. A VPN on their side can also block games.";
        return new("A-NET-10", $"{names} logged in, but their {(one ? "game" : "games")} could not connect to Amberline.",
            $"Games use the game ports ({ports}). {check} Then ask {(one ? "them" : "them all")} to start the game again.", "seat");
    }

    /// <summary>A-RUN-01: <paramref name="count"/> match servers of this start stopped unexpectedly, the last at <paramref name="last"/>.</summary>
    public static DashboardCard StoppedCard(int count, DateTimeOffset last)
    {
        var at = last.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return new("A-RUN-01", count == 1 ? $"A game stopped unexpectedly at {at}. The match ended for everyone in it."
                : $"{count} games stopped unexpectedly, the last at {at}. Those matches ended for everyone in them.",
            "Everyone can start a new game. If it happens again, send a support log with an issue report.", "run");
    }

    /// <summary>
    /// A-DATA-02: the data folder's drive has <paramref name="free"/> bytes free, less than <see cref="LowDisk"/>. Its subject is the drive and its
    /// state (batch 751 final review), so hiding "almost full" never hides "full".
    /// </summary>
    public static DashboardCard DiskCard(string drive, long free) =>
        new("A-DATA-02", free < FullDisk
                ? $"The drive Amberline keeps its data on ({drive}) is full: {Size(free)} free. Games, settings and the log may not save."
                : $"The drive Amberline keeps its data on ({drive}) is almost full: {Size(free)} free.",
            $"Free at least {Size(LowDisk)} on {drive}, for example by emptying the Recycle Bin. This message goes by itself.", null,
            drive + (free < FullDisk ? ":full" : ":low"));

    /// <summary>A seat's saved identity: its game's id and its seat number (batch 751).</summary>
    private static string SeatKey((Guid Game, int Seat) key) => $"{key.Game:D}:{key.Seat}";

    private static (Guid Game, int Seat)? ParseSeat(string subject)
    {
        var at = subject.LastIndexOf(':');
        return at > 0 && Guid.TryParse(subject[..at], out var game) && int.TryParse(subject[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var seat)
            ? (game, seat) : null;
    }

    /// <summary>
    /// The support log at <paramref name="now"/>: the version, the status and network checks, the folder checks, the cards, the counts,
    /// the games that stopped and the log's last lines, then <see cref="Mask"/>ed as a whole.
    /// </summary>
    public string Report(DateTimeOffset now)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        var local = now.ToLocalTime();
        var (version, built) = HostVersion.Read(Path.GetFullPath(Path.Combine(_loop.Paths.App, "..")));
        Line("Amberline support log");
        Line($"Made: {local.ToString("d MMMM yyyy, HH:mm (zzz)", CultureInfo.InvariantCulture)}");
        Line($"Amberline: {(version is null ? "(development build)" : version + (built is { } at ? $", built {at.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}" : ""))}");
        Line($"Windows: {Environment.OSVersion.VersionString}{(Environment.Is64BitOperatingSystem ? " (64-bit)" : "")}; .NET {Environment.Version}");
        Line("Names and addresses are replaced by marks such as <login>; logins are only counted; no password is in this report.");
        var logins = _logins();
        if (_dashboard?.Status(now) is { } status)
        {
            Line();
            Line("Status");
            foreach (var line in new[] { status.Headline, status.Address, status.Network, status.Friend }) Line("  " + line);
            Line($"  Logins: {logins.Count}. Online now: {status.Online.Length}. Games running: {status.GamesRunning} of {status.GamesMax}.");
            Line();
            Line("Network checks");
            foreach (var check in status.Checks) Line($"  {check.State,-8} {check.Text}");
            Line();
            Line("Needs your attention");
            if (status.Attention.Length == 0) Line("  Nothing.");
            foreach (var card in status.Attention) Line($"  ({card.Code}) {card.Message} {card.WhatToDo}");
        }
        Line();
        Line("Folders");
        foreach (var (kind, folder) in new[] { (FolderKind.Game, _settings.DataRoot), (FolderKind.Air, _settings.AirRoot) })
        {
            var check = HostFolders.Check(kind, folder, _loop.Folders);
            Line($"  {(check.Ok ? "ok" : "bad"),-8} {(kind == FolderKind.Game ? "Game folder" : "AIR client folder")}{(check.Ok ? "" : ": " + check.Problem)}");
            foreach (var warning in check.Warnings) Line($"           {warning}");
        }
        var free = FreeSpace(_loop.Paths.Data);
        Line($"  {(free is null ? "unknown" : free < LowDisk ? "bad" : "ok"),-8} Data folder: {(free is { } bytes ? $"{Size(bytes)} free on {Drive(_loop.Paths.Data)}" : "free space unknown")}");
        lock (_gate)
        {
            if (_failed.Count > 0)
            {
                Line();
                Line("Games that stopped unexpectedly since the start");
                foreach (var failure in _failed.Values.OrderBy(failure => failure.At))
                    Line($"  {failure.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {failure.Error ?? "(no reason given)"}");
            }
        }
        var (file, lines) = LastLines(_loop.Paths.Logs);
        Line();
        Line(file is null ? "No log yet." : $"The log's last {lines.Count} lines ({Path.GetFileName(file)})");
        foreach (var line in lines) Line("  " + line);
        var names = new List<(string Value, string Mark)> { (UserName, "<Windows user>"), (MachineName, "<PC name>") };
        if (_steps?.ServerName is { } server) names.Add((server, "<server name>"));
        var address = (_steps is null ? _settings.PublicHost : _steps.PublicHost) ?? "";
        names.Add((address, AddressMark(address)));
        // NOT RETAIL (batch 748): a login the host deleted is hidden too; the log, the cards and the games still name it.
        names.AddRange(logins.Concat(_deleted()).SelectMany(login => new[] { (login.Username, "<login>"), (login.Summoner, "<login>") }));
        return Mask(text.ToString(), names);
    }

    /// <summary>
    /// The mark of the address friends use (batch 648): a home-network address (the LAN party's) is a "home address", as the log's other
    /// home addresses are; a name or an internet address is the "public address".
    /// </summary>
    public static string AddressMark(string address) => Address(address) == "<home address>" ? "<home address>" : "<public address>";

    /// <summary>
    /// The support log's privacy pass. Each of <paramref name="names"/> (the Windows user, the PC, the server's name, the public address,
    /// every login's username and summoner name) becomes its mark wherever it stands as a word, in one pass; then every IPv4 and IPv6
    /// address but loopback (an internet address or one of the home network, kept apart), e-mail addresses, the value after a word
    /// "password", secrets in a web address (a launch code, a token) and every long run of letters and digits (tokens, keys).
    /// </summary>
    public static string Mask(string text, IEnumerable<(string Value, string Mark)> names)
    {
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (value, mark) in names)
            if (!string.IsNullOrWhiteSpace(value) && value.Trim() is var name && !IPAddress.IsLoopback(IPAddress.TryParse(name, out var ip) ? ip : IPAddress.None))
                marks.TryAdd(name, mark);
        if (marks.Count > 0)
        {
            var words = new Regex(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", marks.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase);
            text = words.Replace(text, found => marks[found.Value]);
        }
        text = Ipv4.Replace(text, found => Address(found.Value));
        text = Ipv6.Replace(text, found => Address(found.Value));
        text = Email.Replace(text, "<e-mail>");
        text = Password.Replace(text, "$1$2<hidden>");
        text = Query.Replace(text, "$1<secret>");
        return Secret.Replace(text, "<secret>");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _watch;
        _stop.Dispose();
    }

    private static readonly Regex Ipv4 = new(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])");
    private static readonly Regex Ipv6 = new(@"(?<![\w:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![\w:])");
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+");
    private static readonly Regex Password = new(@"(?i)\b(password|passwd|pwd)(""?\s*[:=]\s*""?)[^\s"",;&]+");
    private static readonly Regex Query = new(@"(?i)([?&](?:c|code|token|key|ticket)=)[^&\s""'<>]+");
    private static readonly Regex Secret = new(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{32,}");

    // The game's own version numbers look like addresses and say which client is meant, so they stay.
    private static readonly HashSet<string> Versions = ["1.0.0.126", "0.0.0.51"];

    private static string Address(string text) =>
        Versions.Contains(text) || !IPAddress.TryParse(text, out var address) || IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? text
        : WanAddress.Classify(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) == WanAddressClass.Public ||
          address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            ? "<internet address>" : "<home address>";

    /// <summary>
    /// The report as the box's text. The shell sets every "(A-...)" code of a page's text in a small span; a box shows text, not
    /// markup, so its codes' brackets are character references the shell's pattern does not see.
    /// </summary>
    private static string Boxed(string report) => WebUtility.HtmlEncode(report).Replace("(A-", "&#40;A-", StringComparison.Ordinal);

    private bool Stuck(Seat seat) =>
        seat.Friend && !seat.Connected && seat.WaitingSince is { } since && seat.WaitedUntil is { } until && until - since >= ConnectWait;

    private async Task WatchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(Every);
            do
            {
                // The watch runs on its own: a failed look is logged and the next period looks again.
                try { Observe(DateTimeOffset.Now); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"Amberline's game watch failed: {ex.GetType().Name}"); }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The newest log's last <see cref="LogLines"/> lines, read from its end while Amberline writes it.</summary>
    private static (string? File, IReadOnlyList<string> Lines) LastLines(string folder)
    {
        try
        {
            var file = Directory.Exists(folder) ? Directory.GetFiles(folder, "amberline-*.log").Order(StringComparer.Ordinal).LastOrDefault() : null;
            if (file is null) return (null, []);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skip = Math.Max(0, stream.Length - LogTail);
            stream.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            if (skip > 0) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return (file, lines.TakeLast(LogLines).Select(line => line.Length > 400 ? line[..400] + "..." : line).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, [$"(the log could not be read: {ex.GetType().Name})"]); }
    }

    private static long? DriveFree(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The drive as Windows names it to people ("C:"; a network folder's share).</summary>
    private static string Drive(string folder) => (Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder).TrimEnd('\\', '/');

    private static string Size(long bytes) => bytes < 1_000_000_000
        ? $"{bytes / 1_000_000} MB"
        : $"{(bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture)} GB";

    private static readonly IResult Expired = HostMode.Page(
        $"<h1>The page expired</h1><p><a href={HostDashboard.PagePath}>Open the dashboard</a> and try again.</p>", StatusCodes.Status400BadRequest);
}
