using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Server;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;

namespace Amberline.Tests.Network;

public static class ServerRuntimeChecks
{
    [Check("server.jit-warmup-prepares-gameplay-methods")]
    public static void JitWarmupPreparesGameplayMethods()
    {
        // The warm-up compiles the gameplay assembly without running it: methods are prepared, and none fails to compile.
        var result = JitWarmup.Prepare(typeof(MatchRuntime).Assembly);
        TestAssert.True(result.Methods > 0, "the warm-up prepares the gameplay assembly's methods");
        TestAssert.True(result.Failed == 0, "no gameplay method fails to compile: " + string.Join("; ", result.Failures.Take(5)));
    }

    [Check("server.jit-warmup-compiles-virtual-methods")]
    public static void JitWarmupCompilesVirtualMethods()
    {
        // Batch 554: RuntimeHelpers.PrepareMethod alone compiled no virtual method that was never called (.NET 10), so every interface
        // implementation and override (the damage pipeline, the transports, the hook fan-out) was compiled at its first call on the simulation
        // thread. After the warm-up of the probe's assembly, the first call through the interface compiles nothing on this thread.
        JitWarmup.Prepare(typeof(WarmupProbe).Assembly);
        IWarmupProbe probe = new WarmupProbe();
        var before = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        var answer = probe.Answer();
        var compiled = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - before;
        TestAssert.True(answer == 42 && compiled == 0,
            $"the warm-up compiles an interface implementation before its first call (that call compiled {compiled} methods)");
    }

    [Check("server.jit-release-server-is-ready-to-run")]
    public static void ReleaseServerIsReadyToRun()
    {
        // Batch 573: the Release build precompiles the server's assemblies (ReadyToRun, Amberline.Server.csproj), so the simulation
        // thread loads precompiled code at a path's first use instead of compiling it. A ReadyToRun image has a managed native header.
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Amberline.Server", "release"));
        foreach (var name in new[] { "Amberline.Server.dll", "Amberline.dll", "BouncyCastle.Cryptography.dll" })
        {
            using var reader = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(Path.Combine(directory, name)));
            TestAssert.True(reader.PEHeaders.CorHeader is { ManagedNativeHeaderDirectory.Size: > 0 },
                $"{name} in the server's output folder is a ReadyToRun image ({directory})");
        }
    }

    private interface IWarmupProbe { int Answer(); }

    private sealed class WarmupProbe : IWarmupProbe
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int Answer() => 42;
    }

    [Check("server.logger-metadata-capture")]
    public static void LoggerMetadataAndClientCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-logger-" + Guid.NewGuid().ToString("N"));
        var client = Path.Combine(root, "client");
        Directory.CreateDirectory(client);
        File.WriteAllText(Path.Combine(client, "1_r3dlog.txt"),
            "--- Log started at Wed Jan 01 00:00:00 2020\nclient\n");
        File.SetCreationTimeUtc(Path.Combine(client, "1_r3dlog.txt"), DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(Path.Combine(client, "1_r3dlog.txt"), DateTime.UtcNow.AddDays(-1));

        var console = new StringWriter();
        using (var logger = ServerLogger.Open(root, console, client))
        {
            TestAssert.True(logger.CaptureClientLog() is null, "Do not attach a previous game's log");
            File.WriteAllText(Path.Combine(client, "2_r3dlog.txt"),
                "--- Log started at Wed Jan 01 00:00:00 2020\nCommand Line: secret-host 5119 secret-key 2\nclient\n");
            logger.Write("PKT", "DEBUG", "->", new Dictionary<string, object?>
            {
                ["op"] = "caller-op",
                ["n"] = 3,
                ["payload"] = new[] { 1, 2, 3 },
            });
            logger.Information("SESSION", "ready", new Dictionary<string, object?>
            {
                ["cat"] = "caller-category",
                ["lvl"] = "caller-level",
                ["msg"] = "caller-message",
                ["t"] = -1,
            });
        }

        var session = Directory.GetDirectories(root)
            .Single(path => !path.EndsWith("client", StringComparison.OrdinalIgnoreCase));
        var metadataPath = Path.Combine(session, "meta.json");
        var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath)).RootElement;
        var commit = metadata.GetProperty("commit").GetString();
        TestAssert.True(commit == "unknown" || commit?.Length == 40,
            "logger metadata must contain a git commit or explicit unknown");
        TestAssert.Equal("2_r3dlog.txt", metadata.GetProperty("client_r3dlog_source").GetString());
        TestAssert.Equal("2020-01-01T00:00:00.0000000", metadata.GetProperty("client_r3dlog_start").GetString());
        TestAssert.True(File.Exists(Path.Combine(session, "client_r3dlog.txt")),
            "production shutdown must preserve the newest client log");
        TestAssert.False(File.ReadAllText(Path.Combine(session, "client_r3dlog.txt")).Contains("secret-"),
            "Archived client commands must redact connection credentials");

        var records = File.ReadAllLines(Path.Combine(session, "session.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        TestAssert.Equal(2, records.Length);
        var packet = records[0].RootElement;
        TestAssert.Equal("PKT", packet.GetProperty("cat").GetString());
        TestAssert.Equal("DEBUG", packet.GetProperty("lvl").GetString());
        TestAssert.Equal("->", packet.GetProperty("msg").GetString());
        TestAssert.False(packet.TryGetProperty("caller-op", out _));
        TestAssert.Equal(3, packet.GetProperty("n").GetInt32());
        foreach (var record in records) record.Dispose();
        Directory.Delete(root, recursive: true);
    }

    [Check("server.options-cli-overrides-invalid-environment")]
    public static void CliOverridesInvalidEnvironment()
    {
        var oldPort = Environment.GetEnvironmentVariable("PORT");
        var oldSpeed = Environment.GetEnvironmentVariable("SIMULATION_SPEED");
        var oldMode = Environment.GetEnvironmentVariable("DEV_DUEL");
        try
        {
            Environment.SetEnvironmentVariable("PORT", "not-a-port");
            Environment.SetEnvironmentVariable("SIMULATION_SPEED", "not-a-speed");
            Environment.SetEnvironmentVariable("DEV_DUEL", "not-a-mode");
            var options = ServerOptions.Parse(["--solo", "--port", "0", "--speed", "1"]);
            TestAssert.Equal((ushort)0, options.Port);
            TestAssert.Equal(1.0, options.SimulationSpeed);
            TestAssert.Equal(ServerMatchMode.Solo, options.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORT", oldPort);
            Environment.SetEnvironmentVariable("SIMULATION_SPEED", oldSpeed);
            Environment.SetEnvironmentVariable("DEV_DUEL", oldMode);
        }
    }

    // Batch 540 (TT-1): Twisted Treeline is map 4 at every gate; its level folder is the shipped LEVELS/Map4; 3 stays refused.
    [Check("server.options-map-id-4-twisted-treeline-is-accepted")]
    public static void MapIdFourIsAcceptedAtTheGates()
    {
        TestAssert.Equal(GameMaps.TwistedTreeline, ServerOptions.Parse(["--map-id", "4"]).MapId, "--map-id 4 names Twisted Treeline");
        TestAssert.Equal("Map4", GameDataPaths.MapFolder(GameMaps.TwistedTreeline), "map 4 reads LEVELS/Map4");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => GameDataPaths.MapFolder(5), "map 5 is refused (batch 563 accepts 3)");
        var synch = LoadingPackets.SynchVersionS2C(mapId: GameMaps.TwistedTreeline).Plaintext.Span;
        TestAssert.Equal(GameMaps.TwistedTreeline, BitConverter.ToInt32(synch.Slice(6, 4)), "SynchVersion carries map 4");
    }

    // Batch 563: the Basic Tutorial (LEVELS/Map3) at the server's gates, played in AIR's GameMode.TUTORIAL.
    [Check("server.options-map-id-3-tutorial-is-accepted")]
    public static void MapIdThreeIsAcceptedAtTheGates()
    {
        TestAssert.Equal(GameMaps.Tutorial, ServerOptions.Parse(["--map-id", "3"]).MapId, "--map-id 3 names the Basic Tutorial");
        TestAssert.Equal("Map3", GameDataPaths.MapFolder(GameMaps.Tutorial), "map 3 reads LEVELS/Map3");
        TestAssert.Equal("TUTORIAL", GameMaps.ModeOf(GameMaps.Tutorial), "the Platform's mode (AIR GameMode.TUTORIAL)");
        TestAssert.Equal("CLASSIC", GameMaps.ModeOf(GameMaps.TwistedTreeline), "Twisted Treeline stays CLASSIC");
        var synch = LoadingPackets.SynchVersionS2C(mapId: GameMaps.Tutorial).Plaintext.Span;
        TestAssert.Equal(GameMaps.Tutorial, BitConverter.ToInt32(synch.Slice(6, 4)), "SynchVersion carries map 3");
        // Batch 674 (restated): the client is told CLASSIC, not TUTORIAL. A mode equal to "Tutorial" loads Battle Training's
        // TutorialModeSettings.ini; any other reads Map3's Mission.ini LoadScreen "Tutorial", the Basic Tutorial's own screen
        // (GameMaps.ClientModeOf).
        TestAssert.Equal("CLASSIC", System.Text.Encoding.ASCII.GetString(synch.Slice(1438 - 128, 128)).TrimEnd('\0'),
            "the Basic Tutorial's SynchVersion mode");
    }

    [Check("server.simulation-speed-policy")]
    public static void SimulationSpeedRejectsInvalidValuesWithoutMutation()
    {
        var speed = new ProductionSimulationSpeed { ClockResyncPending = true };
        foreach (var invalid in new[] { 0.0, 0.099, 10.001, double.NaN,
                     double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        {
            TestAssert.False(speed.TrySet(invalid), $"speed {invalid} must be rejected");
            TestAssert.Equal(1.0, speed.Multiplier, "rejected speed must not mutate state");
            TestAssert.True(speed.ClockResyncPending, "rejected speed must not mutate resync state");
        }
        foreach (var valid in new[] { 0.1, 1.0, 10.0 })
            TestAssert.True(speed.TrySet(valid), $"speed {valid} must be accepted");

        foreach (var invalid in new[] { "0", "0.09", "10.01", "NaN", "Infinity", "1e309" })
            TestAssert.Throws<ArgumentException>(() =>
                ServerOptions.Parse(["--solo", "--speed", invalid]),
                $"CLI speed {invalid} must be rejected");

        var oldEnvironmentSpeed = Environment.GetEnvironmentVariable("SIMULATION_SPEED");
        try
        {
            Environment.SetEnvironmentVariable("SIMULATION_SPEED", "0.09");
            TestAssert.Throws<ArgumentOutOfRangeException>(() => ServerOptions.FromEnvironment(),
                "environment speed policy must match CLI policy");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMULATION_SPEED", oldEnvironmentSpeed);
        }

        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true,
            readySettleSeconds: 0.0);
        runtime.SimulationSpeed = 2.0;
        foreach (var invalid in new[] { 0.0, 11.0, double.NaN, double.PositiveInfinity })
        {
            TestAssert.Throws<ArgumentOutOfRangeException>(() => runtime.SimulationSpeed = invalid);
            TestAssert.Equal(2.0, runtime.SimulationSpeed,
                "rejected runtime speed must not mutate state");
        }

        var root = Path.Combine(Path.GetTempPath(), "amberline-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logger = ServerLogger.Open(root, new StringWriter()))
            using (var network = new MatchRuntimeNetwork(runtime,
                new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true))
            {
                var host = new ServerRuntimeHost(network,
                    new ServerOptions("127.0.0.1", 0, 1, 8, ServerMatchMode.Solo,
                        "Annie", "Orianna", 1.0, null, root, 32), logger);
                runtime.World.Clock.SetVirtual(0.0);
                var participant = runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1))
                    ?? throw new InvalidOperationException("solo participant did not authenticate");
                runtime.Session.Spawn(1);
                runtime.Session.MarkReady(1);
                TestAssert.True(runtime.Session.Start(), "speed chat test must start the session");
                var capture = new CaptureTransport();
                runtime.Delivery.AddObserver(new ReplicationObserver(
                    participant.PlayerId, participant.Team, participant.Hero!.NetId,
                    capture, ready: true, view: participant.View));

                // Both operands and the multiplier are finite; elapsed * 2
                // genuinely overflows, while the finite control remains debt.
                host.RunIteration(double.MaxValue);
                TestAssert.Equal(0.0, host.SimulationAccumulator,
                    "a non-finite accumulator resets before it can create simulation debt");
                host.RunIteration(0.01);
                TestAssert.Near(0.02, host.SimulationAccumulator, 1e-12,
                    "a finite elapsed interval remains usable after overflow recovery");
                capture.Packets.Clear();

                var packet = ChatPackets.Envelope(participant.ClientId, false,
                    Encoding.ASCII.GetBytes("/speed NaN"));
                runtime.Session.Receive(1, ChatPackets.Channel,
                    packet.ToWireBytes(runtime.Cipher));
                TestAssert.Equal(2.0, runtime.SimulationSpeed,
                    "rejected chat speed must not mutate runtime state");

                packet = ChatPackets.Envelope(participant.ClientId, false,
                    Encoding.ASCII.GetBytes("/speed 4"));
                runtime.Session.Receive(1, ChatPackets.Channel,
                    packet.ToWireBytes(runtime.Cipher));
                TestAssert.Equal(4.0, runtime.SimulationSpeed,
                    "accepted chat speed changes the runtime multiplier");
                var combatTime = runtime.Combat.CurrentTime;
                runtime.Session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher,
                    PausePackets.Pause(uint.MaxValue, 1).Plaintext.Span));
                TestAssert.True(runtime.Session.Paused, "the host iteration test must enter pause");

                host.RunIteration(0.5);
                TestAssert.Equal(0.0, host.SimulationAccumulator,
                    "pause entry discards accumulated simulation debt");
                TestAssert.Equal(combatTime, runtime.Combat.CurrentTime,
                    "paused host iterations must not advance combat");
                TestAssert.False(capture.Packets.Any(item => item.Plaintext.Length > 0 &&
                    item.Plaintext.Span[0] == 0xC9),
                    "paused host iterations defer pending speed resynchronization");

                runtime.Session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher,
                    PausePackets.Resume(uint.MaxValue, delayed: false).Plaintext.Span));
                runtime.World.Clock.SetVirtual(5.0);
                TestAssert.True(runtime.Session.Paused,
                    "resume remains pending until the production pump runs its scheduler");
                host.RunIteration(0.25);
                TestAssert.False(runtime.Session.Paused, "the production scheduler must finish resume");
                TestAssert.Equal(0.0, host.SimulationAccumulator,
                    "unpause must discard the elapsed interval observed while paused");
                TestAssert.Equal(combatTime, runtime.Combat.CurrentTime,
                    "elapsed paused time must not tick simulation when the pump resumes play");
                TestAssert.Equal(1, capture.Packets.Count(item => item.Plaintext.Length > 0 &&
                    item.Plaintext.Span[0] == 0xC9),
                    "the production host branch consumes and delivers one speed resync");
            }
        }
        finally
        {
            runtime.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Check("server.production-clock-domain")]
    public static void ProductionUsesSimulationClockForRuntimeTimers()
    {
        var navigation = NavigationGrid.LoadDefault(GameDataPaths.Default);
        var runtime = MatchRuntime.CreateProduction(navigation, solo: true);
        TestAssert.True(runtime.World.Clock.Now() > 1_000_000_000.0,
            "real-mode world wall clock should remain a Unix timestamp");
        TestAssert.Equal(runtime.Combat.CurrentTime, runtime.Delivery.SimulationTime(),
            "replication timers must use the combat simulation domain");
    }

    [Check("session.start-clock-matches-simulation")]
    public static void DelayedHudSynchronizationPreservesMatchTime()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            readySettleSeconds: 0.0);
        // Loading has a nonzero process-clock origin; match time still starts
        // at zero and advances before the delayed client clock packet.
        runtime.World.Clock.SetVirtual(1234.0);
        foreach (var id in new[] { 1L, 2L })
        {
            TestAssert.True(runtime.Session.Authenticate(id,
                runtime.Session.BuildKeyCheck((ulong)id)) is not null);
            runtime.Session.Spawn(id);
            runtime.Session.MarkReady(id);
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started);
        runtime.Tick(1.25);
        var clocks = runtime.Session.RunSchedulers()
            .Where(packet => packet.Packet.Plaintext.Span[0] == 0xC9).ToArray();
        TestAssert.Equal(2, clocks.Length, "one initial clock for each player");
        foreach (var clock in clocks)
            TestAssert.Near(1.25, BitConverter.ToSingle(clock.Packet.Plaintext.Span[5..]), 1e-6,
                "delayed HUD packet includes time already simulated");
        TestAssert.Near(0.0, runtime.Session.HudOrigin!.Value, 1e-8,
            "HUD shares the match origin");
        TestAssert.Near(runtime.Waves!.StartClock!.Value, runtime.Session.HudOrigin.Value, 1e-8,
            "HUD shares the wave origin");
        runtime.Tick(2.0);
        TestAssert.Near(runtime.Combat.CurrentTime, runtime.Session.HudTime!.Value, 1e-8,
            "HUD and authoritative combat retain one timeline");
        TestAssert.True(!runtime.Session.RunSchedulers()
            .Any(packet => packet.Packet.Plaintext.Span[0] == 0xC9), "no repeated clock reset");
    }

    [Check("server.production-step-cadence")]
    public static void ProductionOversizedStepUsesSourceSubsteps()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(GameDataPaths.Default), solo: true,
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        var participant = runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1))
            ?? throw new InvalidOperationException("solo participant did not authenticate");
        runtime.Session.Spawn(1);
        runtime.Session.MarkReady(1);
        runtime.Session.BuildMapReadyPackets(1);
        TestAssert.True(runtime.Session.Start(), "solo production session must start");
        var observer = new ReplicationObserver(
            participant.PlayerId, participant.Team, participant.Hero!.NetId,
            new CaptureTransport(), ready: true, view: participant.View);
        runtime.Delivery.AddObserver(observer);

        runtime.Tick(0.1);

        // 0.1 is three source steps (1/30, 1/30, remainder), with no tiny
        // fourth step. Instrumentation is reset at each Delivery.Flush, so
        // the final frame also proves raw visibility ran once for the team.
        TestAssert.Equal(3, runtime.Delivery.Instrumentation.Frame,
            "oversized simulation time must split into three source frames");
        TestAssert.Equal(1,
            runtime.Delivery.Instrumentation.RawCalculationsByTeam[participant.Team],
            "raw visibility must be calculated once per team in the final frame");
    }

    [Check("network.pump-drain-budget-preserves-input-and-delivery", Serial = true)]
    public static void PumpDrainBudgetLeavesLaterInputForNextPump()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(GameDataPaths.Default), solo: true,
            readySettleSeconds: 0.0);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true);
        using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var peer = client.Connect("127.0.0.1", network.Port, channelCount: 8);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        var clientConnected = false;
        while (!clientConnected || network.ConnectionIds.Count != 1)
        {
            network.Pump(1, 32, flushDelivery: false, drainBudgetMilliseconds: 0);
            clientConnected |= client.Service(1).Type == LeagueEnetEventType.Connect;
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new InvalidOperationException("native pump budget peer did not connect");
        }

        var keyCheck = runtime.Session.BuildKeyCheck(1);
        peer.Send(0, keyCheck, LeagueEnetPacketFlags.Reliable);
        peer.Send(0, keyCheck, LeagueEnetPacketFlags.Reliable);
        client.Flush();

        PumpUntilOneEvent(network, "a zero budget still handles the first complete receive event");
        var connectionId = network.ConnectionIds.Single();
        TestAssert.True(runtime.Session.Participant(connectionId)?.ConnectionId == connectionId,
            "the first queued key check must authenticate its connection");
        TestAssert.True(DrainClientReceives(client) > 0,
            "the first event must retain its loading delivery");

        PumpUntilOneEvent(network, "the later queued receive must remain for the next pump");
        TestAssert.True(DrainClientReceives(client) > 0,
            "the later receive must retain its loading delivery");
    }

    private static void PumpUntilOneEvent(MatchRuntimeNetwork network, string message)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var handled = network.Pump(0, 32, flushDelivery: false,
                drainBudgetMilliseconds: 0);
            if (handled == 0) continue;
            TestAssert.Equal(1, handled, message);
            return;
        }
        throw new InvalidOperationException("native pump budget receive did not arrive");
    }

    private static int DrainClientReceives(LeagueEnetHost client)
    {
        var received = 0;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (received == 0 && Stopwatch.GetTimestamp() < deadline)
        {
            var @event = client.Service(1);
            if (@event.Type == LeagueEnetEventType.Receive) received++;
        }
        while (true)
        {
            var @event = client.CheckEvents();
            if (@event.Type == LeagueEnetEventType.None) break;
            if (@event.Type == LeagueEnetEventType.Receive) received++;
        }
        return received;
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
