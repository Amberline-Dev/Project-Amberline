using System.Globalization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;

namespace Amberline.Server;

public enum ServerMatchMode
{
    Solo,
    Duel,
    Custom,
}

/// <summary>
/// Validated process configuration.  Environment values provide the same
/// development defaults as the old entry point; command-line values win.
/// </summary>
public sealed record ServerOptions(
    string BindAddress,
    ushort Port,
    int PeerLimit,
    int ChannelLimit,
    ServerMatchMode Mode,
    string OrderChampion,
    string ChaosChampion,
    double SimulationSpeed,
    string? GameDataRoot,
    string LogDirectory,
    int MaxEvents,
    bool VisibilityBaseline = false,
    bool MinionFidelity = false,
    bool ValidateOnly = false,
    IReadOnlyList<MatchPlayer>? Players = null,
    bool LauncherControl = false,
    bool ManagedStatus = false,
    int MapId = 1)
{
    public const ushort DefaultPort = 5119;
    // Ten roster seats plus a new transport while a crashed seat is replaced.
    public const int DefaultPeerLimit = 11;
    public const int DefaultChannelLimit = 255;
    public const double DefaultSimulationSpeed = 1.0;
    public const int DefaultMaxEvents = 32;

    /// <summary>The playable champions of <see cref="ChampionCatalog"/>, in roster order.</summary>
    public static readonly IReadOnlySet<string> SupportedChampions =
        new HashSet<string>(ChampionCatalog.PlayableNames, StringComparer.OrdinalIgnoreCase);

    public bool IsEphemeral => Port == 0;

    public static ServerOptions FromEnvironment()
    {
        return FromEnvironmentRaw().Validate();
    }

    private static ServerOptions FromEnvironmentRaw(IReadOnlyList<string>? commandLine = null)
    {
        // Select the raw mode override before parsing DEV_DUEL.  This keeps a
        // valid --solo/--duel invocation usable even when a stale inherited
        // DEV_DUEL value is malformed; the final Parse loop still resolves
        // the order of explicit mode flags.
        var explicitDuel = commandLine?.Any(arg => arg.Equals("--duel", StringComparison.OrdinalIgnoreCase)) == true;
        var explicitSolo = commandLine?.Any(arg => arg.Equals("--solo", StringComparison.OrdinalIgnoreCase)) == true;
        var mode = explicitDuel ? ServerMatchMode.Duel :
            explicitSolo ? ServerMatchMode.Solo :
            ParseMode(Environment.GetEnvironmentVariable("DEV_DUEL"));
        var order = HasOption(commandLine, "roster") || HasOption(commandLine, "order-champion") ? "Annie" : Environment.GetEnvironmentVariable("DEV_CHAMPION") ?? "Annie";
        var chaos = HasOption(commandLine, "roster") || HasOption(commandLine, "chaos-champion") ? "Orianna" : Environment.GetEnvironmentVariable("DEV_DUEL_CHAMPION") ?? "Orianna";
        var port = HasOption(commandLine, "port") || commandLine?.Any(x => x.Equals("--ephemeral", StringComparison.OrdinalIgnoreCase)) == true
            ? DefaultPort
            : ParseUShort(Environment.GetEnvironmentVariable("PORT"), DefaultPort, "PORT");
        var speed = HasOption(commandLine, "speed")
            ? DefaultSimulationSpeed
            : ParseDouble(Environment.GetEnvironmentVariable("SIMULATION_SPEED"), DefaultSimulationSpeed, "SIMULATION_SPEED");
        var bind = HasOption(commandLine, "bind") ? "0.0.0.0" : Environment.GetEnvironmentVariable("BIND_ADDRESS") ?? "0.0.0.0";
        var dataRoot = HasOption(commandLine, "data-root") ? null : Environment.GetEnvironmentVariable("GAME_DATA_ROOT");
        var logDirectory = HasOption(commandLine, "log-dir") ? "logs" : Environment.GetEnvironmentVariable("AMBERLINE_LOG_DIR") ?? "logs";
        var visibilityBaseline = HasOption(commandLine, "visibility-baseline") ? false :
            ParseFlag(Environment.GetEnvironmentVariable("DEV_VISIBILITY_BASELINE"), false,
                "DEV_VISIBILITY_BASELINE");
        var minionFidelity = HasOption(commandLine, "minion-fidelity") ? false :
            ParseFlag(Environment.GetEnvironmentVariable("MINION_FIDELITY_MODE"), false,
                "MINION_FIDELITY_MODE");
        return new ServerOptions(bind, port, DefaultPeerLimit, DefaultChannelLimit, mode,
            order, chaos, speed, dataRoot, logDirectory, DefaultMaxEvents,
            visibilityBaseline, minionFidelity);
    }

    public ServerOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
            throw new ArgumentException("BindAddress is required.", nameof(BindAddress));
        if (PeerLimit is < 1 or > 0xFFF)
            throw new ArgumentOutOfRangeException(nameof(PeerLimit), "Peer limit must be 1..4095.");
        if (ChannelLimit is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ChannelLimit), "Channel limit must be 1..255.");
        if (!ProductionSimulationSpeed.IsValid(SimulationSpeed))
            throw new ArgumentOutOfRangeException(nameof(SimulationSpeed),
                "Simulation speed must be finite and between 0.1 and 10.");
        if (!GameMaps.IsSupported(MapId))
            throw new ArgumentOutOfRangeException(nameof(MapId), $"Map id must be {GameMaps.SupportedText}.");
        if (MaxEvents is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxEvents), "Max events must be 1..1024.");
        if (VisibilityBaseline && MinionFidelity)
            throw new ArgumentException("DEV_VISIBILITY_BASELINE and MINION_FIDELITY_MODE are mutually exclusive.");
        var players = Players is null ? null : MatchRoster.Validate(Players);
        if (players is not null && PeerLimit < players.Length)
            throw new ArgumentException("Peer limit must cover every configured player.");
        if (Mode == ServerMatchMode.Custom && players is null)
            throw new ArgumentException("Custom matches require a roster.");
        if ((players is not null ? players.Length != 1 : Mode != ServerMatchMode.Solo) && (VisibilityBaseline || MinionFidelity))
            throw new ArgumentException("Visibility baseline and minion fidelity modes require --solo.");
        ValidateChampion(OrderChampion, nameof(OrderChampion));
        if (Mode == ServerMatchMode.Duel) ValidateChampion(ChaosChampion, nameof(ChaosChampion));
        return this with
        {
            OrderChampion = CanonicalChampion(OrderChampion),
            ChaosChampion = Mode == ServerMatchMode.Duel ? CanonicalChampion(ChaosChampion) : "Orianna",
            Players = players,
            Mode = players is null ? Mode : players.Length == 1 ? ServerMatchMode.Solo : ServerMatchMode.Custom,
        };
    }

    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(arg => arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg == "-h"))
            throw new ServerHelpRequestedException();
        var result = FromEnvironmentRaw(args);
        var mode = result.Mode;
        var bind = result.BindAddress;
        var port = result.Port;
        var peerLimit = result.PeerLimit;
        var channelLimit = result.ChannelLimit;
        var order = result.OrderChampion;
        var chaos = result.ChaosChampion;
        var speed = result.SimulationSpeed;
        var dataRoot = result.GameDataRoot;
        var logDirectory = result.LogDirectory;
        var maxEvents = result.MaxEvents;
        var visibilityBaseline = result.VisibilityBaseline;
        var minionFidelity = result.MinionFidelity;
        var validateOnly = result.ValidateOnly;
        var launcherControl = result.LauncherControl;
        var managedStatus = false;
        var mapId = result.MapId;
        MatchPlayer[]? players = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--managed-status", StringComparison.OrdinalIgnoreCase))
            {
                managedStatus = true;
                continue;
            }
            if (arg.Equals("--launcher-control", StringComparison.OrdinalIgnoreCase))
            {
                launcherControl = true;
                continue;
            }
            if (arg.Equals("--solo", StringComparison.OrdinalIgnoreCase))
            {
                mode = ServerMatchMode.Solo;
                continue;
            }
            if (arg.Equals("--duel", StringComparison.OrdinalIgnoreCase))
            {
                mode = ServerMatchMode.Duel;
                continue;
            }
            if (arg.Equals("--ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                port = 0;
                continue;
            }
            if (arg.Equals("--visibility-baseline", StringComparison.OrdinalIgnoreCase))
            {
                visibilityBaseline = true;
                continue;
            }
            if (arg.Equals("--minion-fidelity", StringComparison.OrdinalIgnoreCase))
            {
                minionFidelity = true;
                continue;
            }
            if (arg.Equals("--validate-only", StringComparison.OrdinalIgnoreCase))
            {
                validateOnly = true;
                continue;
            }
            var (name, value) = SplitOption(args, ref index, arg);
            switch (name)
            {
                case "bind": bind = value; break;
                case "port": port = ParseUShort(value, 0, "--port"); break;
                case "peer-limit": peerLimit = ParseInt(value, "--peer-limit"); break;
                case "channel-limit": channelLimit = ParseInt(value, "--channel-limit"); break;
                case "order-champion": order = value; break;
                case "chaos-champion": chaos = value; mode = ServerMatchMode.Duel; break;
                case "speed": speed = ParseDouble(value, 0.0, "--speed"); break;
                case "data-root": dataRoot = value; break;
                case "log-dir": logDirectory = value; break;
                case "max-events": maxEvents = ParseInt(value, "--max-events"); break;
                case "roster": players = MatchRoster.Read(value); break;
                case "map-id": mapId = ParseInt(value, "--map-id"); break;
                default: throw new ArgumentException($"Unknown option '--{name}'.");
            }
        }

        return new ServerOptions(bind, port, peerLimit, channelLimit, mode, order, chaos,
            speed, dataRoot, logDirectory, maxEvents,
            visibilityBaseline, minionFidelity, validateOnly, players, launcherControl, managedStatus, mapId).Validate();
    }

    public static string Usage() => """
        Amberline.Server

        Usage: Amberline.Server [options]

          --solo                         Start one-seat mode.
          --duel                         Start two-seat mode (default from DEV_DUEL).
          --bind <address>               ENet bind address (default 0.0.0.0).
          --port <0..65535>              ENet port; 0 selects an ephemeral port.
          --ephemeral                    Shorthand for --port 0.
          --peer-limit <1..4095>         Native ENet peer limit.
          --channel-limit <1..255>       Native ENet channel limit.
          --order-champion <name>        Order development champion.
          --chaos-champion <name>        Chaos champion and duel mode.
          --roster <path.json>           1–10 players: Name, Champion, Team (Order/Chaos).
          --launcher-control             Signal readiness; stop on stdin 'stop' or EOF.
          --managed-status               Emit structured match status for the host manager.
          --map-id <1|2|3|4|8>          1 Summoner's Rift; 2 the Harrowing rift (Autumn, LEVELS/Map2); 3 the Basic Tutorial (LEVELS/Map3); 4 Twisted Treeline (LEVELS/Map4); 8 The Crystal Scar (Dominion, LEVELS/Map8).
          --speed <0.1..10>              Simulation speed multiplier.
          --visibility-baseline           Solo vision probe mode (DEV_VISIBILITY_BASELINE).
          --minion-fidelity               Solo mid-lane fidelity mode (MINION_FIDELITY_MODE).
          --data-root <path>             Shipped game-data root.
          --log-dir <path>               Structured session log directory.
          --max-events <1..1024>         Maximum native events per pump.
          --validate-only                 Build the production graph and load ENet, then exit.
          --help                         Show this help.

        The executable builds the maintained gameplay composition in-process.
        """;

    private static (string Name, string Value) SplitOption(
        IReadOnlyList<string> args, ref int index, string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            throw new ArgumentException($"Unknown argument '{arg}'.");
        var text = arg[2..];
        var equals = text.IndexOf('=');
        if (equals >= 0)
            return (text[..equals], text[(equals + 1)..]);
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option '--{text}' requires a value.");
        index++;
        return (text, args[index]);
    }

    private static bool HasOption(IReadOnlyList<string>? args, string name) =>
        args is not null && args.Any(arg => arg.Equals($"--{name}", StringComparison.OrdinalIgnoreCase) ||
                                            arg.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase));

    private static ServerMatchMode ParseMode(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            ? ServerMatchMode.Duel : ServerMatchMode.Solo;

    private static ushort ParseUShort(string? value, ushort fallback, string name) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
            ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new ArgumentException($"{name} must be an unsigned 16-bit integer.");

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"{name} must be an integer.");

    private static double ParseDouble(string? value, double fallback, string name) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new ArgumentException($"{name} must be a number.");

    private static bool ParseFlag(string? value, bool fallback, string name) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value switch
        {
            "0" => false,
            "1" => true,
            _ => throw new ArgumentException($"{name} must be 0 or 1.")
        };

    private static void ValidateChampion(string champion, string parameter)
    {
        if (string.IsNullOrWhiteSpace(champion) || !SupportedChampions.Contains(champion))
            throw new ArgumentException($"Unsupported development champion '{champion}'.", parameter);
    }

    private static string CanonicalChampion(string champion) =>
        SupportedChampions.First(name => string.Equals(name, champion, StringComparison.OrdinalIgnoreCase));
}

public sealed class ServerHelpRequestedException : Exception;
