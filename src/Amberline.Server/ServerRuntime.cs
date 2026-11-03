using System.Diagnostics;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation.Combat;

namespace Amberline.Server;

/// <summary>Required production composition boundary for the executable.</summary>
public interface IServerMatchFactory
{
    MatchRuntimeNetwork Create(ServerOptions options, IServerLogger logger);
}

/// <summary>Built-in composition root used by the supported executable.</summary>
public sealed class BuiltinServerMatchFactory : IServerMatchFactory
{
    private readonly int? _rngSeed;

    /// <param name="rngSeed">
    /// DIAGNOSTICS: tool-only (the scenario director, Amberline.Tools). Retail draws from an unseeded RNG, so the shipped
    /// executable passes nothing and every match draws a fresh logged seed from the OS entropy source.
    /// </param>
    public BuiltinServerMatchFactory(int? rngSeed = null) => _rngSeed = rngSeed;

    /// <summary>The fixed seed this factory was given; null for the shipped server, which draws one per match.</summary>
    public int? RngSeed => _rngSeed;

    private static LegacyBlowfishCipher MatchCipher()
    {
        var key = Environment.GetEnvironmentVariable("AMBERLINE_MATCH_KEY");
        if (key is null) return LegacyBlowfish.CreateDefault();
        if (key.Length != 16 || key.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("AMBERLINE_MATCH_KEY must contain exactly 16 ASCII letters or digits.");
        return new LegacyBlowfishCipher(System.Text.Encoding.ASCII.GetBytes(key));
    }
    /// <summary>Validate the staged production graph without binding a live port.</summary>
    public static void ValidateComposition(ServerOptions options, IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var paths = new GameDataPaths(options.GameDataRoot).ForMap(options.MapId);
        var data = new GameData(paths);
        var navigation = NavigationGrid.Load(paths);
        using var host = new LeagueEnetHost(0, options.PeerLimit,
            options.ChannelLimit, options.BindAddress,
            maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize,
            maximumWaitingData: LeagueEnetHost.ProductionMaximumWaitingData);
        var runtime = MatchRuntime.CreateProduction(
            navigation, data, options.OrderChampion, options.ChaosChampion,
            solo: options.Mode == ServerMatchMode.Solo,
            mapId: options.MapId,
            gameMode: options.GameMode,
            visibilityBaseline: options.VisibilityBaseline,
            minionFidelity: options.MinionFidelity,
            sessionLog: logger,
            cipher: MatchCipher(),
            roster: options.Players is null ? null : MatchRoster.ToParticipants(options.Players));
        runtime.Session.CreateWorld();
        logger.Information("SESSION", "composition.validated", new Dictionary<string, object?>
        {
            ["mode"] = options.Mode.ToString(),
            ["dataRoot"] = paths.Root,
        });
    }

    public MatchRuntimeNetwork Create(ServerOptions options, IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        // NOT RETAIL: the gameplay methods compile on a background thread from server start, before any client connects (JitWarmup).
        JitWarmup.Start();
        var paths = new GameDataPaths(options.GameDataRoot).ForMap(options.MapId);
        var data = new GameData(paths);
        var navigation = NavigationGrid.Load(paths);
        // DIAGNOSTICS: retail draws from an unseeded server RNG. One logged seed per match feeds every
        // roll (combat, Shaco boxes, bots, fear wander), so a recorded live game can be replayed with it.
        var random = new SeededRandomSource(_rngSeed ?? SeededRandomSource.NewSeed());
        var runtime = MatchRuntime.CreateProduction(
            navigation, data, options.OrderChampion, options.ChaosChampion,
            solo: options.Mode == ServerMatchMode.Solo,
            mapId: options.MapId,
            gameMode: options.GameMode,
            visibilityBaseline: options.VisibilityBaseline,
            minionFidelity: options.MinionFidelity,
            sessionLog: logger,
            cipher: MatchCipher(),
            roster: options.Players is null ? null : MatchRoster.ToParticipants(options.Players),
            random: random,
            // Batch 222 (item 7): the shipped server's os.time is the wall clock, as retail's. A seeded tool or check match (a seed given
            // to the factory) keeps its seed as the script clock, the labelled test seam of ProductionMatchComposition.Build.
            scriptClock: _rngSeed is null ? () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null);
        // Batch 617: the movement helper starts now, while the players load, not on the first movement frame of the game.
        runtime.Movement.PrewarmEngine();
        runtime.Lifecycle.AttachReporter(violation =>
        {
            logger.Error("SESSION", "lifecycle violation",
                new InvalidOperationException(violation.Detail),
                new Dictionary<string, object?>
                {
                    ["hero"] = violation.Hero,
                    ["team"] = violation.Team.ToString(),
                    ["netid"] = violation.NetId,
                    ["component"] = violation.Component,
                    ["timer"] = violation.Timer,
                    ["position"] = new[] { violation.Position.X, violation.Position.Z },
                    ["god_mode"] = violation.GodMode,
                    ["cooldowns_disabled"] = violation.CooldownsDisabled,
                    ["detail"] = violation.Detail,
                    ["token"] = violation.Token,
                });
        });
        runtime.SimulationSpeed = options.SimulationSpeed;
        var host = new LeagueEnetHost(options.Port, options.PeerLimit,
            options.ChannelLimit, options.BindAddress,
            maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize,
            maximumWaitingData: LeagueEnetHost.ProductionMaximumWaitingData);
        logger.Information("SESSION", "match.composed", new Dictionary<string, object?>
        {
            ["port"] = host.Port,
            ["mode"] = options.Mode.ToString(),
            ["navigationRoot"] = paths.Root,
            ["rng_seed"] = random.Seed,
        });
        return new MatchRuntimeNetwork(runtime, host, ownsHost: true, log: logger);
    }
}

/// <summary>
/// ENet and fixed-step simulation loop.  The gameplay graph is supplied by
/// <see cref="IServerMatchFactory"/>; this class never substitutes a fake
/// match or swallows an unattached gameplay callback.
/// </summary>
public sealed class ServerRuntimeHost
{
    private const double FixedStepSeconds = 1.0 / 30.0;
    private readonly MatchRuntimeNetwork _network;
    private readonly ServerOptions _options;
    private readonly IServerLogger _logger;
    private double _simulationAccumulator;
    private long _lastTimestamp;
    private bool _started;
    private long _nextStatus;

    public ServerRuntimeHost(MatchRuntimeNetwork network, ServerOptions options,
        IServerLogger logger)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _options = options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public MatchRuntimeNetwork Network => _network;

    /// <summary>
    /// DIAGNOSTICS: tool-only (the scenario director, Amberline.Tools). Retail has no such hook. When set, it is called
    /// once per fixed simulation step, right before the step runs, with the match runtime. The shipped server never
    /// sets it (Program.cs, <see cref="ServerApplication"/>); null leaves the step exactly as before. It is not called
    /// while the match is unstarted or paused, and its time does not count toward <c>tick.slow</c>.
    /// </summary>
    public Action<MatchRuntime>? BeforeTick { get; set; }

    /// <summary>NOT RETAIL: true when a fault ended this match (see Run). The process exit code then reports the fault.</summary>
    public bool Faulted { get; private set; }

    /// <summary>
    /// A simulation step this long (wall time) is logged as <c>tick.slow</c>. A step stops ENet servicing and every
    /// outbound packet for its duration: the stock client sees its RTT jump, then a burst of corrections (batch 20:
    /// 0.4 s hero-planner steps froze all units). The budget is 33.3 ms; checks lower this to force the line.
    /// </summary>
    internal double SlowTickMilliseconds { get; set; } = 100.0;

    /// <summary>Current accumulated wall-to-simulation debt for the owning loop.</summary>
    internal double SimulationAccumulator => _simulationAccumulator;

    /// <summary>Accumulates one wall-clock interval without allowing non-finite debt.</summary>
    private static bool TryAccumulateSimulation(double accumulator, double elapsed,
        double multiplier, out double accumulated)
    {
        accumulated = accumulator + elapsed * multiplier;
        if (double.IsFinite(accumulated)) return true;
        accumulated = 0.0;
        return false;
    }

    /// <summary>Discards simulation debt observed while entering or leaving pause.</summary>
    private static double ClearSimulationDebt(double accumulator, bool wasPaused, bool paused) =>
        wasPaused || paused ? 0.0 : accumulator;

    public void Run(CancellationToken cancellationToken)
    {
        _logger.Information("server.start", new Dictionary<string, object?>
        {
            ["bind"] = _options.BindAddress,
            ["port"] = _network.Port,
            ["mode"] = _options.Mode.ToString(),
            ["orderChampion"] = _options.OrderChampion,
            ["chaosChampion"] = _options.Mode == ServerMatchMode.Duel
                ? _options.ChaosChampion : null,
            ["simulationSpeed"] = _options.SimulationSpeed,
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
                RunIteration(ReadElapsedSeconds());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // NOT RETAIL: server machinery (retail has no per-match containment). A fault ends this match alone: it is logged once
            // with the match's port and stack, and the shutdown below flushes and closes its peers. The simulation is not patched
            // around the fault, so a broken match never keeps running in a corrupt state.
            Faulted = true;
            _logger.Error("match.fault", error, new Dictionary<string, object?> { ["port"] = _network.Port });
        }
        finally
        {
            _network.Host.Flush();
            _logger.Information("server.stop");
            _logger.Flush();
        }
    }

    /// <summary>Runs one production host iteration with an injected elapsed interval for checks.</summary>
    internal void RunIteration(double elapsed)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0.0)
            throw new ArgumentOutOfRangeException(nameof(elapsed));

        var wasPaused = _network.Runtime.Session.Paused;
        if (!TryAccumulateSimulation(_simulationAccumulator, elapsed,
                _network.Runtime.SimulationSpeed, out var accumulated))
            _logger.Warning("server.simulation-accumulator-reset");
        _simulationAccumulator = accumulated;
        var timeout = _simulationAccumulator >= FixedStepSeconds ? 0 : 16;
        _network.Pump(timeout, _options.MaxEvents, flushDelivery: false,
            drainBudgetMilliseconds: MatchRuntimeNetwork.DefaultDrainBudgetMilliseconds);
        if (_options.ManagedStatus && Stopwatch.GetTimestamp() >= _nextStatus)
        {
            var session = _network.Runtime.Session;
            Console.WriteLine("host.status " + System.Text.Json.JsonSerializer.Serialize(new ManagedMatchStatus(
                session.Started, session.Result is not null,
                session.Participants.Where(p => p.ConnectionId is not null).Select(p => (int)p.PlayerId).ToArray(),
                session.Participants.Where(p => p.Loaded).Select(p => (int)p.PlayerId).ToArray())));
            Console.Out.Flush();
            _nextStatus = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }
        _simulationAccumulator = ClearSimulationDebt(
            _simulationAccumulator, wasPaused, _network.Runtime.Session.Paused);
        if (_network.Runtime.Session.Paused)
        {
            // Keep reconnect snapshots moving, without accumulating a
            // burst of simulation to replay when the pause ends.
            _network.FlushDelivery();
            return;
        }
        if (_network.Runtime.ConsumeClockResyncPending())
        {
            _network.Runtime.Delivery.Enqueue(
                SpawnPackets.GameClock((float)(_network.Runtime.Session.HudTime ?? 0.0)),
                global: true);
            _network.FlushDelivery();
        }

        var started = _network.Runtime.Session.Started;
        if (!started)
        {
            // Time before the start barrier must not become a burst of
            // simulation after the second seat finishes loading.
            _simulationAccumulator = 0.0;
        }
        else if (!_started)
        {
            _started = true;
            // NOT RETAIL: the first step waits for the JIT warm-up only while it still runs (JitWarmup). It starts with the server and
            // normally finishes within seconds, before the first client connects.
            if (JitWarmup.AwaitFirstStep() is { } warm)
                _logger.Information("GAMEPLAY", "jit.warmup", new Dictionary<string, object?>
                {
                    ["methods"] = warm.Result.Methods,
                    ["failed"] = warm.Result.Failed,
                    ["prepare_ms"] = Math.Round(warm.Result.Milliseconds, 1),
                    ["waited_ms"] = Math.Round(warm.WaitedMilliseconds, 1),
                });
            _logger.Information("GAMEPLAY", "match.started", new Dictionary<string, object?>
            {
                // DIAGNOSTICS: the seed of every random roll in this match (see BuiltinServerMatchFactory).
                ["rng_seed"] = (_network.Runtime.Dependencies.Random as SeededRandomSource)?.Seed,
            });
            _network.FlushDelivery();
        }

        if (_started && _simulationAccumulator >= FixedStepSeconds)
        {
            BeforeTick?.Invoke(_network.Runtime);
            var tickStarted = Stopwatch.GetTimestamp();
            _network.Tick(FixedStepSeconds);
            _simulationAccumulator -= FixedStepSeconds;
            var tickMilliseconds = Stopwatch.GetElapsedTime(tickStarted).TotalMilliseconds;
            // DIAGNOSTICS: owner playtest diagnostic. Retail logs no server step times.
            if (tickMilliseconds > SlowTickMilliseconds)
                _logger.Warning("GAMEPLAY", "tick.slow", new Dictionary<string, object?>
                {
                    ["ms"] = Math.Round(tickMilliseconds, 1),
                    ["debt_ms"] = Math.Round(_simulationAccumulator * 1000.0, 1),
                    ["hud"] = _network.Runtime.Session.HudTime,
                });
        }
    }

    private double ReadElapsedSeconds()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        if (!double.IsFinite(elapsed) || elapsed < 0.0)
            throw new InvalidOperationException("The monotonic server clock returned an invalid interval.");
        return elapsed;
    }
}

public static class ServerApplication
{
    public static async Task<int> RunAsync(
        ServerOptions options,
        IServerMatchFactory factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        options = options.Validate();
        using var logger = ServerLogger.Open(options.LogDirectory,
            clientLogDirectory: options.GameDataRoot);
        MatchRuntimeNetwork? network = null;
        try
        {
            network = factory.Create(options, logger) ??
                throw new InvalidOperationException("The match factory returned no runtime.");
            if (options.LauncherControl)
            {
                Console.WriteLine("launcher.ready");
                Console.Out.Flush();
            }
            var host = new ServerRuntimeHost(network, options, logger);
            host.Run(cancellationToken);
            return host.Faulted ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            logger.Error("server.crash", exception);
            return 1;
        }
        finally
        {
            network?.Dispose();
            logger.Flush();
        }
    }
}
