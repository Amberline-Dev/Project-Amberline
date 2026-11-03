using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// What the dashboard's network panel asks of the world outside the Platform: the router (UPnP), the Windows Firewall rules, the one
/// elevated start, and the default route for the router guide. <see cref="Windows"/> is the real one, which only the <c>--home</c>
/// start passes; a loop without one tests nothing, so a check that starts the Platform never reaches a real router or the firewall.
/// Checks pass a fake router on loopback and an elevation that only records the command.
/// </summary>
public sealed record HostNetwork(
    Func<CancellationToken, Task<UpnpGateway?>> FindRouter,
    Func<CancellationToken, Task<IReadOnlyList<ShownRule>?>> ReadFirewall,
    Func<ElevatedCommand, CancellationToken, Task<int?>> RunElevated,
    Func<(IPAddress? Router, IPAddress? Computer)> Route,
    Func<IPAddress?>? Outbound = null)
{
    // Outbound: the address this PC sends internet traffic from (HostChecks.VpnCard); null looks for no VPN.
    public static HostNetwork Windows { get; } = new(cancel => UpnpGateway.DiscoverAsync(cancellationToken: cancel),
        HostFirewall.ReadRulesAsync, HostFirewall.RunElevatedAsync, DefaultRoute, HostChecks.OutboundAddress);

    /// <summary>The first IPv4 default gateway of an interface that is up, and this PC's IPv4 address on that interface.</summary>
    internal static (IPAddress? Router, IPAddress? Computer) DefaultRoute()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Select(route => route.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
                if (gateway is null) continue;
                return (gateway, properties.UnicastAddresses.Select(unicast => unicast.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork));
            }
        }
        catch (NetworkInformationException) { }
        return (null, null);
    }
}

/// <summary>
/// NOT RETAIL: the friend light of the Amberline Host design, section 5.4 (retail's servers needed no home reachability test). Only a
/// friend outside can show that friends reach this PC, so the first friend whose Play file or companion reaches the public port turns
/// "Waiting for a friend" green. Kept in <c>host\state.json</c>: a time, and whether it came from the internet or from the home
/// network; never a friend's address. The file's other entries are left as they are.
/// </summary>
public sealed class HostFriendLight
{
    private const string InternetKey = "friendPublicAt";
    private const string HomeKey = "friendLocalAt";
    // Each request of a friend's tools would otherwise write the file; the saved time is at most this much behind.
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);
    private readonly string _file;
    private readonly Lock _gate = new();
    private DateTimeOffset? _internet, _home, _savedInternet, _savedHome;

    public HostFriendLight(string dataFolder)
    {
        _file = StateFile(dataFolder);
        var state = Read(_file);
        _internet = _savedInternet = Time(state, InternetKey);
        _home = _savedHome = Time(state, HomeKey);
    }

    public static string StateFile(string dataFolder) => Path.Combine(dataFolder, "host", "state.json");

    /// <summary>When a friend last reached the public port from the internet, or null.</summary>
    public DateTimeOffset? FromInternet { get { lock (_gate) return _internet; } }

    /// <summary>When a friend last reached it from the home network, which does not show that the router lets friends in; or null.</summary>
    public DateTimeOffset? FromHomeNetwork { get { lock (_gate) return _home; } }

    /// <summary>The friends' tools: the Play file's manifest and archive, the companion's sign-in and its two tunnels.</summary>
    public static bool FriendPath(string? path) => path is not null && (path.StartsWith("/bootstrap/", StringComparison.Ordinal) ||
        path is AirRemoteTunnel.SignInPath or AirRemoteTunnel.Path or AirRemoteTunnel.ChatPath);

    /// <summary>
    /// One request that reached the public port over HTTPS on an allowed path. A request from this PC itself (loopback) comes from the
    /// host's own tools, not from a friend. The address decides only "internet or home network" and is not kept.
    /// </summary>
    public void Observe(IPAddress? from, string? path, DateTimeOffset now)
    {
        if (from is null || !FriendPath(path)) return;
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        if (IPAddress.IsLoopback(from)) return;
        var internet = from.AddressFamily == AddressFamily.InterNetwork
            ? WanAddress.Classify(from) == WanAddressClass.Public
            : !(from.IsIPv6LinkLocal || from.IsIPv6SiteLocal || from.IsIPv6UniqueLocal);
        lock (_gate)
        {
            if (internet) _internet = now;
            else _home = now;
            if (now - (internet ? _savedInternet : _savedHome) < SaveEvery) return;
            try
            {
                lock (HostAttention.StateGate)
                {
                    var state = Read(_file) ?? new JsonObject();
                    if (_internet is { } seen) state[InternetKey] = seen.ToString("O", CultureInfo.InvariantCulture);
                    if (_home is { } near) state[HomeKey] = near.ToString("O", CultureInfo.InvariantCulture);
                    HostFiles.Write(_file, state);
                }
                (_savedInternet, _savedHome) = (_internet, _home);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The light stays in memory; a full disk must not stop a friend's sign-in.
                Console.Error.WriteLine($"Amberline could not save the friend light: {ex.Message}");
            }
        }
    }

    private static JsonObject? Read(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static DateTimeOffset? Time(JsonObject? state, string key) =>
        state?[key] is JsonValue value && value.TryGetValue<string>(out var text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
}

/// <summary>
/// The network panel's last test. Null fields were not reached yet; <see cref="RouterDone"/> says whether the router part finished;
/// <see cref="HomeOnly"/>, that it was left out because the host chose the home network only.
/// </summary>
public sealed record NetworkResult(FirewallState? Firewall, FirewallProblem? FirewallCard, bool RouterDone, UpnpGateway? Router,
    UpnpProblem? RouterProblem, IPAddress? External, IPAddress? RouterAddress, IPAddress? Computer, bool HomeOnly = false)
{
    public static NetworkResult Pending { get; } = new(null, null, false, null, null, null, null, null);
}

/// <summary>
/// NOT RETAIL: the dashboard's network panel (Amberline Host design, sections 2.2 "Ready", 5.1 to 5.3; retail's servers had public
/// addresses). One test reads the Windows Firewall rules back, finds the router, asks it to forward the sign-in and game ports to this
/// PC and reads the router's own internet address. The router keeps the ports only while Amberline runs: <see cref="DisposeAsync"/>
/// removes them. A panel without a <see cref="HostNetwork"/> tests nothing. While <paramref name="homeOnly"/> says the host chose the
/// home network only, a test reads the firewall rules but never asks the router, and removes the ports an earlier test opened.
/// </summary>
public sealed class HostNetworkPanel(HostNetwork? network, IReadOnlyList<FirewallRule> rules, IReadOnlyList<UpnpPort> ports,
    Func<bool>? homeOnly = null) : IAsyncDisposable
{
    /// <summary>The card after the host declined the Windows prompt (design 2.2, step 3).</summary>
    public static readonly FirewallProblem Declined = new("A-NET-02", "Windows did not allow it, so friends cannot join yet.",
        "Choose Allow through Windows Firewall and press Yes.");

    private readonly SemaphoreSlim _busy = new(1, 1);
    private UpnpGateway? _router;

    public bool Off => network is null;
    public IReadOnlyList<FirewallRule> Rules => rules;
    public NetworkResult Last { get; private set; } = NetworkResult.Pending;

    /// <summary>One whole test; the page shows each part as it finishes. A router that stopped answering is searched for again.</summary>
    public async Task TestAsync(CancellationToken cancel)
    {
        if (network is null) return;
        await _busy.WaitAsync(cancel);
        try
        {
            Last = NetworkResult.Pending;
            IReadOnlyList<ShownRule>? shown = null;
            try { shown = await network.ReadFirewall(cancel); }
            catch (InvalidOperationException) { } // netsh did not start: the rules are unreadable
            Last = Last with { Firewall = HostFirewall.Evaluate(rules, shown) };
            if (homeOnly?.Invoke() == true)
            {
                // Friends on the home network reach this PC directly: the router keeps no port for Amberline.
                if (_router is not null) await _router.DisposeAsync();
                _router = null;
                var (gateway, own) = network.Route();
                Last = Last with { RouterDone = true, HomeOnly = true, RouterAddress = gateway, Computer = own };
                return;
            }
            UpnpProblem? problem = UpnpGateway.NotFound;
            IPAddress? external = null;
            try
            {
                if (_router is null || _router.RenewalProblem == UpnpGateway.NotFound)
                {
                    if (_router is not null) await _router.DisposeAsync();
                    _router = await network.FindRouter(cancel);
                }
                if (_router is not null)
                {
                    problem = await _router.MapAsync(ports, cancel);
                    external = await _router.ExternalAddressAsync(cancel);
                    if (problem == UpnpGateway.NotFound)
                    {
                        await _router.DisposeAsync();
                        _router = null;
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException)
            {
                // No network at all, for example: the router guide covers it.
                Console.Error.WriteLine($"Amberline could not reach the router: {ex.Message}");
            }
            var (routerAddress, computer) = _router is { } found
                ? (IPAddress.TryParse(found.Location.Host, out var address) ? address : null, found.LocalAddress)
                : network.Route();
            Last = Last with
            {
                RouterDone = true, Router = _router, RouterProblem = problem, External = external, RouterAddress = routerAddress, Computer = computer,
            };
        }
        finally { _busy.Release(); }
    }

    /// <summary>The one Windows prompt (H4's flow), with the read-back after it.</summary>
    public async Task AllowAsync(CancellationToken cancel)
    {
        if (network is null) return;
        await _busy.WaitAsync(cancel);
        try
        {
            var attempt = await HostFirewall.AllowAsync(rules, cancel, network.ReadFirewall, network.RunElevated);
            Last = Last with { Firewall = attempt.State ?? Last.Firewall, FirewallCard = attempt.Refusal ?? (attempt.Declined ? Declined : null) };
        }
        finally { _busy.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _busy.WaitAsync();
        try
        {
            if (_router is not null) await _router.DisposeAsync();
            _router = null;
        }
        finally { _busy.Release(); }
    }
}

/// <summary>One running match as the dashboard counts it: its human players, its bots and the match server's state.</summary>
public sealed record DashboardGame(int Players, int Bots, string State);

/// <summary>One line of the network panel: <see cref="State"/> is ok, bad, unknown, waiting or checking.</summary>
public sealed record DashboardCheck(string Id, string State, string Text);

/// <summary>A "Needs your attention" card; <see cref="Action"/> names the page's button for it (firewall or network), or null.</summary>
public sealed record DashboardCard(string Code, string Message, string WhatToDo, string? Action);

public sealed record DashboardPerson(string Username, string Summoner, string Doing, string Since);

/// <summary>The status card, as <c>/host/status.json</c> sends it. Every text is plain words the page shows as they are.</summary>
public sealed record DashboardStatus(string State, DateTimeOffset Since, string Headline, string Address, string Network, string Friend,
    DashboardCheck[] Checks, DashboardPerson[] Online, int GamesRunning, int GamesMax, string[] Games, DashboardCard[] Attention,
    string[]? Guide);

/// <summary>
/// NOT RETAIL: the home host's dashboard (Amberline Host design, section 2.3; retail had no host page). Under <c>/host</c>, behind
/// <see cref="HostGate"/>: the page, its status as JSON (which the page's one script reads every few seconds, so nothing on the page
/// reloads), Stop and Restart, each naming the running games first, and the network panel with the friend light and the router guide.
/// </summary>
public sealed class HostDashboard : IAsyncDisposable
{
    public const string PagePath = "/host";

    /// <summary>The host pages' policy: the stylesheet and one script file from this origin, the status it reads, nothing inline.</summary>
    public const string Policy =
        "default-src 'none'; style-src 'self'; script-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'";

    private static readonly string[] RunningStates = ["Starting", "Waiting for players", "Playing"];
    private readonly HostLoop _loop;
    private readonly PlatformSettings _settings;
    private readonly Func<PlatformManagement.OnlineRow[]> _online;
    private readonly Func<IReadOnlyList<DashboardGame>> _games;
    private readonly DateTimeOffset _since = DateTimeOffset.Now;
    private readonly CancellationTokenSource _stop = new();
    private Task _testing = Task.CompletedTask;
    private HostSteps? _steps;

    /// <param name="platformProgram">The program the sign-in rule names; the running one by default.</param>
    public HostDashboard(HostLoop loop, PlatformSettings settings, Func<PlatformManagement.OnlineRow[]> online,
        Func<IReadOnlyList<DashboardGame>> games, string? platformProgram = null)
    {
        _loop = loop;
        _settings = settings;
        _online = online;
        _games = games;
        Network = new HostNetworkPanel(loop.Network,
            HostFirewall.Rules(platformProgram ?? Environment.ProcessPath!, settings.ServerExecutable, settings.RemotePort, settings.FirstGamePort, settings.MaxGames),
            UpnpGateway.HostPorts(settings.RemotePort, settings.FirstGamePort, settings.MaxGames), () => HomeOnly);
        Attention = new HostAttention(loop, games, Network.TestAsync);
    }

    public HostNetworkPanel Network { get; }

    /// <summary>The start checks' cards (H9: the app's files, the certificate, a VPN, the address change).</summary>
    public HostAttention Attention { get; }

    /// <summary>The cards about games and the disk and the support log (batch 640); null shows neither.</summary>
    public HostSupport? Support { get; init; }

    /// <summary>The matches still to end: starting, waiting for players or being played.</summary>
    public static IReadOnlyList<DashboardGame> RunningGames(GameManager manager) =>
        manager.List(new HostGuest("", "", "", Admin: true)).Where(game => RunningStates.Contains(game.State))
            .Select(game => new DashboardGame(game.Seats.Count(seat => !seat.Player.IsBot), game.Seats.Count(seat => seat.Player.IsBot), game.State))
            .ToArray();

    /// <summary>
    /// The dashboard's routes; the network test and the start checks (<see cref="HostAttention"/>, given the address
    /// <paramref name="steps"/> and the served <paramref name="certificate"/>) start once the listeners are up.
    /// </summary>
    public void Map(WebApplication app, HostSteps? steps = null, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
    {
        _steps = steps;
        // Turning the home-network choice on removes the router's ports, turning it off asks the router again.
        if (steps is not null) steps.HomeNetworkChanged = () => _testing = Task.WhenAll(_testing, TestInBackgroundAsync());
        Attention.Map(app, steps, certificate);
        Support?.Map(app, this, steps);
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(PagePath)) context.Response.Headers.ContentSecurityPolicy = Policy;
            return next(context);
        });
        app.Lifetime.ApplicationStarted.Register(() => _testing = TestInBackgroundAsync());
        app.MapGet(PagePath, (HttpContext context, IAntiforgery antiforgery) => HostMode.Page(Body(Program.RegistrationToken(context, antiforgery))));
        app.MapGet(PagePath + "/status.json", () => Results.Json(Status(DateTimeOffset.Now)));
        app.MapGet(PagePath + "/stop", (HttpContext context, IAntiforgery antiforgery) => Confirm(context, antiforgery, restart: false, null));
        app.MapGet(PagePath + "/restart", (HttpContext context, IAntiforgery antiforgery) => Confirm(context, antiforgery, restart: true, null));
        app.MapPost(PagePath + "/stop", (HttpContext context, IAntiforgery antiforgery) => EndAsync(context, antiforgery, restart: false));
        app.MapPost(PagePath + "/restart", (HttpContext context, IAntiforgery antiforgery) => EndAsync(context, antiforgery, restart: true));
        app.MapPost(PagePath + "/firewall", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Expired;
            await Network.AllowAsync(context.RequestAborted);
            return Results.Redirect(PagePath);
        });
        app.MapPost(PagePath + "/network", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Expired;
            await Network.TestAsync(context.RequestAborted);
            return Results.Redirect(PagePath);
        });
    }

    /// <summary>The host chose Only people on my home network (H6's address step, <see cref="HostAddress.Lan"/>).</summary>
    private bool HomeOnly => _steps?.HomeNetworkOnly == true;

    /// <summary>
    /// The status card at <paramref name="now"/>. With the home network only, the router is not needed, its address classes (a second
    /// router, a shared provider address) do not matter, and a friend on the home network is a friend reached.
    /// </summary>
    public DashboardStatus Status(DateTimeOffset now)
    {
        var net = Network.Last;
        var homeOnly = HomeOnly;
        var checks = new List<DashboardCheck>();
        var cards = new List<DashboardCard>();
        void Add(string id, string state, string text, string? code = null, string? whatToDo = null, string? action = null)
        {
            checks.Add(new(id, state, text));
            if (code is not null) cards.Add(new(code, text, whatToDo!, action));
        }

        Add("listening", "ok", "Amberline is listening on this PC");
        var firewallOk = false;
        if (Network.Off) Add("firewall", "unknown", "Amberline did not check Windows Firewall");
        else if (net.FirewallCard is { } card) Add("firewall", "bad", card.Message, card.Code, card.WhatToDo, card == HostNetworkPanel.Declined ? "firewall" : null);
        else if (net.Firewall is not { } firewall) Add("firewall", "checking", "Checking Windows Firewall");
        else if (firewall == FirewallState.Allowed) { firewallOk = true; Add("firewall", "ok", "Windows Firewall lets friends in"); }
        else
        {
            var problem = HostFirewall.Problem(firewall)!;
            Add("firewall", firewall == FirewallState.Unreadable ? "unknown" : "bad", problem.Message, problem.Code, problem.WhatToDo,
                firewall switch { FirewallState.Blocked => null, FirewallState.Unreadable => "network", _ => "firewall" });
        }

        var routerOk = false;
        string[]? guide = null;
        if (homeOnly) { routerOk = true; Add("router", "ok", "Home network only: the router is not needed"); }
        else if (Network.Off) Add("router", "unknown", "Amberline did not check your router");
        // A test from while the home network only was chosen says nothing about the router; the next test asks it.
        else if (!net.RouterDone || net.HomeOnly) Add("router", "checking", "Looking for your router");
        else if ((net.RouterProblem ?? net.Router?.RenewalProblem) is { } refused)
        {
            Add("router", "bad", refused.Message, refused.Code, refused.WhatToDo, "network");
            guide = Guide(net);
        }
        else
        {
            routerOk = true;
            Add("router", "ok", $"Found your router{Model(net.Router?.ModelName, " (\"{0}\")")}; it opened the ports for this PC");
        }

        var addressOk = false;
        // No line on the home network only: a second router or a shared provider address does not stop friends at home (no A-NET-06/07).
        if (homeOnly) { }
        else if (net.External is not null || net.Router is not null && net.RouterDone)
        {
            var kind = WanAddress.Classify(net.External);
            if (kind == WanAddressClass.Public)
            {
                addressOk = true;
                Add("address", "ok", "Your router has a public internet address");
            }
            else
            {
                var problem = WanAddress.Problem(kind)!;
                Add("address", "bad", problem.Message, problem.Code, problem.WhatToDo, kind == WanAddressClass.None ? "network" : null);
            }
        }
        else if (Network.Off) Add("address", "unknown", "Amberline did not ask your router for its internet address");
        else if (!net.RouterDone || net.HomeOnly) Add("address", "checking", "Asking your router for its internet address");
        else Add("address", "unknown", "The router did not say its internet address");

        var fromInternet = _loop.Friend.FromInternet;
        var fromHome = _loop.Friend.FromHomeNetwork;
        if (fromInternet is { } seen) Add("friend", "ok", $"A friend reached Amberline from the internet {Ago(now - seen)}");
        else if (fromHome is { } local && homeOnly) Add("friend", "ok", $"A friend on your home network reached Amberline {Ago(now - local)}");
        else if (fromHome is { } near)
            Add("friend", "waiting", $"A friend on your home network reached Amberline {Ago(now - near)}. Waiting for a friend from the internet.");
        else Add("friend", "waiting", "Waiting for the first friend to start the Play file.");

        var checking = checks.Any(check => check.State == "checking");
        var bad = checks.Count(check => check.State == "bad");
        var canJoin = firewallOk && (homeOnly || routerOk && addressOk || fromInternet is not null);
        var headline = $"Running since {Clock(_since, now)}." + (canJoin ? " Friends can join."
            : checking ? " Checking whether friends can reach you." : " Friends may not be able to join: see Needs your attention.");
        var network = checking ? "Network: checking"
            : bad > 0 ? $"Network: {Count(bad, "thing")} {(bad == 1 ? "needs" : "need")} your attention"
            : checks.Any(check => check.State == "unknown") ? "Network: some checks could not run"
            : "Network: all checks passed";
        // The address step changes the saved address without a restart, so the steps' value is the one friends are given.
        var saved = _steps is null ? _settings.PublicHost : _steps.PublicHost;
        var address = homeOnly ? $"Address: {saved} (home network only). {HostInvite.HomeNetworkNote}"
            : net.External is { } shown && WanAddress.Classify(shown) == WanAddressClass.Public ? $"Address: {shown} (from your router)"
            : !string.IsNullOrWhiteSpace(saved) ? $"Address: {saved} (from your settings)"
            : "Address: not known yet";
        var last = fromInternet > fromHome || fromHome is null ? fromInternet : fromHome;
        var friend = last is { } at ? $"Last friend connected {Ago(now - at)}" : "No friend has connected yet";
        cards.AddRange(Attention.Cards(now, net));
        if (Support is not null) cards.AddRange(Support.Cards(homeOnly));
        var games = _games();
        return new DashboardStatus("running", _since, headline, address, network, friend, [.. checks],
            [.. _online().Select(row => new DashboardPerson(row.Username, row.Summoner, row.Doing, row.Since))],
            games.Count, _settings.MaxGames,
            [.. games.Select((game, index) => $"Game {index + 1} · {Count(game.Players, "player")}" +
                (game.Bots > 0 ? $" and {Count(game.Bots, "bot")}" : "") + " · " + StateWords(game.State))],
            [.. cards], guide);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _testing;
        await Attention.DisposeAsync();
        if (Support is not null) await Support.DisposeAsync();
        await Network.DisposeAsync();
        _stop.Dispose();
    }

    private async Task TestInBackgroundAsync()
    {
        try { await Network.TestAsync(_stop.Token); }
        catch (OperationCanceledException) { }
        // The start's test runs on its own: a failure shows on the page as unfinished checks and in the log, never as a failed stop.
        catch (Exception ex) { Console.Error.WriteLine($"Amberline's network test failed: {ex.Message}"); }
    }

    /// <summary>The router guide of design section 5.5, with the values Amberline knows.</summary>
    private string[] Guide(NetworkResult net)
    {
        var lastGame = _settings.FirstGamePort + _settings.MaxGames - 1;
        var games = lastGame == _settings.FirstGamePort ? $"UDP {_settings.FirstGamePort}" : $"UDP {_settings.FirstGamePort} to {lastGame}";
        var model = Model(net.Router?.ModelName, "{0}");
        return
        [
            net.RouterAddress is { } router
                ? $"Open your router's page, http://{router}/. Its password is often on a sticker on the router."
                : "Open your router's page. Its address and password are often on a sticker on the router.",
            "Find \"Port forwarding\" (or \"Virtual server\" or \"NAT\").",
            $"Forward these to this PC{(net.Computer is { } computer ? $", {computer}" : "")}: TCP {_settings.RemotePort} (sign-in) and {games} (games).",
            "Give this PC a fixed address on the router (\"DHCP reservation\")." +
                (model.Length == 0 ? "" : $" Search the web for \"{model} port forwarding\" to find its steps."),
            "Press Test again. Or switch on UPnP in the router, then press Test again.",
        ];
    }

    /// <summary>The router's own model name, cut to plain printable text (any device on the network can answer the search).</summary>
    private static string Model(string? name, string format)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
        return text.Length == 0 ? "" : string.Format(CultureInfo.InvariantCulture, format, text);
    }

    private IResult Confirm(HttpContext context, IAntiforgery antiforgery, bool restart, string? note, int status = StatusCodes.Status200OK)
    {
        var games = _games();
        var verb = restart ? "Restart" : "Stop";
        var warning = games.Count == 0
            ? restart ? "Amberline is back in a few seconds." : "Friends cannot log in until you start Amberline again."
            : $"{Count(games.Count, "game")} {(games.Count == 1 ? "is" : "are")} running ({Count(games.Sum(game => game.Players), "player")}). " +
              $"{(restart ? "Restarting" : "Stopping")} ends {(games.Count == 1 ? "it" : "them")} for everyone.";
        var wait = HostPages.Wait(restart ? "Restarting Amberline. This takes a few seconds." : "Stopping Amberline.");
        return HostMode.Page($"<h1>{verb} Amberline?</h1>" + (note is null ? "" : $"<p class=problem role=alert>{note}</p>") + $"<p>{warning}</p>" +
            $"<form method=post action={PagePath}/{verb.ToLowerInvariant()}{wait}>{Program.RegistrationToken(context, antiforgery)}" +
            $"<input type=hidden name=games value={games.Count}><div class=buttons><button type=submit>{verb} Amberline</button> " +
            $"<a class=\"button secondary\" href={PagePath}>Cancel</a></div></form>", status);
    }

    /// <summary>
    /// Stops or restarts once the answer is sent, but only when no more games run than the page warned about: a game that started
    /// after the page was opened gets the warning first.
    /// </summary>
    private async Task<IResult> EndAsync(HttpContext context, IAntiforgery antiforgery, bool restart)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
            return Confirm(context, antiforgery, restart, "The page expired. Please choose again.", StatusCodes.Status400BadRequest);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        // ponytail: compares counts, not the games themselves; one game ending while another starts goes unasked. Send the game ids if it matters.
        if (!int.TryParse(form["games"], NumberStyles.None, CultureInfo.InvariantCulture, out var warned) || _games().Count > warned)
            return Confirm(context, antiforgery, restart, "A game started since this page was opened.", StatusCodes.Status409Conflict);
        context.Response.OnCompleted(() =>
        {
            if (restart) _loop.Restart();
            else _loop.Stop();
            return Task.CompletedTask;
        });
        return restart
            ? HostMode.Page("<h1>Amberline is restarting</h1><p>This page comes back by itself.</p>", refresh: _loop.ReopenPath, nav: false)
            : HostMode.Page("<h1>Amberline has stopped</h1><p>To start it again, double-click Amberline on your desktop.</p>", nav: false);
    }

    private static readonly IResult Expired = HostMode.Page(
        $"<h1>The page expired</h1><p><a href={PagePath}>Open the dashboard</a> and try again.</p>", StatusCodes.Status400BadRequest);

    private static string StateWords(string state) => state switch
    {
        "Playing" => "in progress",
        "Waiting for players" => "waiting for players",
        _ => "starting",
    };

    private static string Ago(TimeSpan age) => age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? Count((int)age.TotalMinutes, "minute") + " ago"
        : age < TimeSpan.FromDays(1) ? Count((int)age.TotalHours, "hour") + " ago"
        : Count((int)age.TotalDays, "day") + " ago";

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Clock(DateTimeOffset since, DateTimeOffset now) =>
        since.ToLocalTime().ToString(since.ToLocalTime().Date == now.ToLocalTime().Date ? "HH:mm" : "d MMMM, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The page; the script fills it from the status and shows each button only when a card needs it. Only the headline and the
    /// network line are a live region, and the script changes a text only when it changed, so a screen reader hears what changed,
    /// once, and the focus stays where it is.
    /// </summary>
    private static string Body(string token) => $$"""
        <h1>Dashboard</h1>
        <section id=status class=status aria-labelledby=status-title><h2 id=status-title>Status</h2>
        <div aria-live=polite><p class=headline><span id=status-mark class=mark aria-hidden=true>…</span> <strong id=headline>Checking…</strong></p>
        <p id=network-line></p></div>
        <p id=address></p><p id=friend></p>
        <p class=buttons><a class="button secondary" href={{PagePath}}/stop>Stop Amberline</a> <a class="button secondary" href={{PagePath}}/restart>Restart Amberline</a></p></section>
        {{HostPlay.Button(token)}}
        <noscript><p class=problem>This page needs JavaScript to show the status.</p></noscript>
        <h2 id=online-title>Online now</h2><p id=online-empty>Checking…</p>
        <table id=online-table hidden><thead><tr><th scope=col>Username</th><th scope=col>Doing</th><th scope=col>Since</th></tr></thead><tbody id=online></tbody></table>
        <h2 id=games-title>Games</h2><p id=games-empty>Checking…</p><ul id=games></ul>
        <h2>Network</h2><ul id=checks class=checks><li>Checking…</li></ul>
        <h2>Needs your attention</h2><p id=attention-none>Checking…</p><ul id=attention class=cards></ul>
        <form method=post action={{PagePath}}/firewall data-action=firewall hidden{{HostPages.Wait("Waiting for Windows. Choose Yes when Windows asks.")}}>{{token}}
        <button type=submit class=secondary>Allow through Windows Firewall</button>
        <p class=tip>Windows asks once, for Windows Command Processor: choose Yes.</p></form>
        <form method=post action={{PagePath}}/network data-action=network hidden{{HostPages.Wait("Testing your network. This takes up to a minute.")}}>{{token}}<button type=submit class=secondary>Test again</button></form>
        {{HostAttention.Forms(token)}}{{HostSupport.Forms(token)}}
        <p class=tip>Something not working? Send a <a href={{HostSupport.PagePath}}>support log</a> with your issue report.</p>
        <section id=guide hidden aria-labelledby=guide-title><h2 id=guide-title>Open the ports on your router by hand</h2><ol id=guide-steps></ol></section>
        """;
}
