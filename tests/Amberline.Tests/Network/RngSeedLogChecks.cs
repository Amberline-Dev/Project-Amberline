using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Replication;
using Amberline.Server;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>Batch 18: a live game logs the seed of every random roll so it can be replayed.</summary>
public static class RngSeedLogChecks
{
    [Check("server.rng-seed-logged-at-compose-and-start", Serial = true)]
    public static void LiveMatchLogsItsRandomSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seeds = new List<int>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var options = ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
                var directory = Path.Combine(root, attempt.ToString());
                using var logger = ServerLogger.Open(directory, new StringWriter());
                using var network = new BuiltinServerMatchFactory().Create(options, logger);
                var random = network.Runtime.Dependencies.Random as SeededRandomSource
                    ?? throw new InvalidOperationException("the live match must draw from a seeded source");
                seeds.Add(random.Seed);
                TestAssert.True(random.Seed >= 0, "a live seed is a non-negative int");

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
                new ServerRuntimeHost(network, options, logger).RunIteration(0.01);
                logger.Flush();

                // The logger still holds the file open for append; read it with shared access.
                using var stream = new FileStream(Path.Combine(Directory.GetDirectories(directory).Single(), "session.jsonl"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var records = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonDocument.Parse(line).RootElement).ToArray();
                foreach (var message in new[] { "match.composed", "match.started" })
                {
                    var record = records.Single(row => row.GetProperty("msg").GetString() == message);
                    TestAssert.Equal(random.Seed, record.GetProperty("rng_seed").GetInt32(), message + " logs the seed in use");
                }
            }
            TestAssert.True(seeds.Distinct().Count() > 1, "each live match draws its own seed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NullTransport : IReplicationTransport
    {
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) { }
    }
}
