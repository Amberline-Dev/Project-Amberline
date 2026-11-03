using System.Text.Json;
using Amberline.Network.Replication;
using Amberline.Server;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 20: a simulation step that overruns its budget is logged. The owner's playtest froze every unit for
/// 0.3 to 1.1 s at a time and nothing in the session log said so; the cause had to be rebuilt from
/// <c>observed_mono_ns</c> against game time.
/// </summary>
public static class SlowTickLogChecks
{
    [Check("server.slow-simulation-step-is-logged", Serial = true)]
    public static void SlowSimulationStepIsLogged()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-slowtick-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            using var logger = ServerLogger.Open(root, new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(options, logger);
            var runtime = network.Runtime;
            runtime.World.Clock.SetVirtual(0.0);
            var session = runtime.Session;
            var participant = session.Authenticate(1, session.BuildKeyCheck(1))
                ?? throw new InvalidOperationException("the solo seat did not authenticate");
            session.Spawn(1);
            session.MarkReady(1);
            runtime.World.Clock.Advance(2.5); // the production ready-settle delay
            TestAssert.True(session.Start(), "the solo session starts");
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team,
                participant.Hero!.NetId, new NullTransport(), ready: true, view: participant.View));
            var host = new ServerRuntimeHost(network, options, logger);
            host.RunIteration(0.01); // logs match.started

            // The default threshold is a bit over three step budgets; a step under the threshold logs nothing
            // (the first step of a cold process includes JIT time, so the quiet case uses an unreachable limit).
            TestAssert.Near(100.0, host.SlowTickMilliseconds, 1e-9, "the default threshold is 100 ms");
            host.SlowTickMilliseconds = double.MaxValue;
            host.RunIteration(0.05);
            logger.Flush();
            TestAssert.Equal(0, SlowTickRecords(root).Length, "a step under the threshold logs nothing");

            // With the threshold at zero the same step is "slow", so the line and its fields can be checked.
            host.SlowTickMilliseconds = 0.0;
            host.RunIteration(0.05);
            logger.Flush();
            var records = SlowTickRecords(root);
            TestAssert.True(records.Length >= 1, "an over-budget step logs tick.slow");
            var record = records[0];
            TestAssert.Equal("WARN", record.GetProperty("lvl").GetString());
            TestAssert.Equal("GAMEPLAY", record.GetProperty("cat").GetString());
            TestAssert.True(record.GetProperty("ms").GetDouble() > 0.0, "the line carries the step's wall time");
            TestAssert.True(record.TryGetProperty("debt_ms", out _), "and the simulation debt left behind");
            TestAssert.True(record.TryGetProperty("hud", out _), "and the HUD time");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement[] SlowTickRecords(string root)
    {
        // The logger holds the file open for append; read it with shared access.
        var path = Path.Combine(Directory.GetDirectories(root).Single(), "session.jsonl");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(row => row.TryGetProperty("msg", out var msg) && msg.GetString() == "tick.slow").ToArray();
    }

    private sealed class NullTransport : IReplicationTransport
    {
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) { }
    }
}
