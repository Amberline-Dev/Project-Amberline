using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// The clean control: a real production match (ten champions, eight of them server bots, two connected seats)
/// is run on the virtual clock, every packet each connected peer would receive goes through the checker, and
/// the checker must report nothing. A violation here is either a real protocol bug in the emulator or a rule
/// that is too strict; it is never silenced to make the check pass.
/// </summary>
public static class PacketRuleControlChecks
{
    // The ten hand-written champions (the first ten playable; Soraka is covered by the headless Soraka match) in alphabetical seat order (five per side).
    private static readonly string[] Champions = ChampionCatalog.PlayableNames.Take(10).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Virtual match length: long enough for the first waves to reach the towers and for bot fights.</summary>
    private const double Seconds = 210.0;

    private sealed class Capture : IReplicationTransport
    {
        public readonly List<PlaintextPacket> Packets = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    internal sealed record Run(IReadOnlyList<PacketRuleViolation> Violations, IReadOnlyDictionary<string, long> Totals,
        long Packets, SortedDictionary<byte, long> Opcodes, double Elapsed, long StaleReplication);

    /// <param name="seconds">Virtual match length.</param>
    /// <param name="tamper">Optional fault injection: sees each observation just before the checker and returns the
    /// observation to feed instead, or null to drop it.</param>
    internal static Run RunProductionMatch(double seconds, Func<PacketObservation, PacketObservation?>? tamper = null)
    {
        // Seats 0 and 5 are connected (one per team); the other eight are bots with the production controller.
        var roster = Champions.Select((champion, index) => new SessionParticipantDefinition(
            (ulong)index + 1, (uint)index, $"Rules {index + 1}", champion, index < 5 ? Team.Order : Team.Chaos,
            IsBot: index is not (0 or 5))).ToArray();
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();

        var checker = new PacketStreamChecker(2);
        // Batch 67: the connection's replication store is the last filter before the wire (MatchRuntimeNetwork.Send) and drops a block the
        // client would discard as stale; the main checker still models the client from the full stream the delivery produced (as
        // HeadlessMatch does), this one sees what the store lets through and is read only for its stale count.
        var wire = new PacketStreamChecker(2);
        var opcodes = new SortedDictionary<byte, long>();
        var captures = new[] { new Capture(), new Capture() };
        var connections = new long[2];
        var seats = new[] { 0, 5 };

        void Feed(int peer, PlaintextPacket packet)
        {
            var observation = PacketRuleChecks.Observation(packet, runtime.World.Clock.Now(), peer);
            if (tamper is not null)
            {
                var changed = tamper(observation);
                if (changed is null) return;
                observation = changed;
            }
            checker.Observe(observation);
            if (session.Participants[seats[peer]].View.Store.Process(packet) is { } sent)
                wire.Observe(observation with { Plaintext = sent.Plaintext });
            if (!PacketCatalog.IsChatChannel(packet.Channel) && packet.Plaintext.Length > 0)
                opcodes[packet.Plaintext.Span[0]] = opcodes.GetValueOrDefault(packet.Plaintext.Span[0]) + 1;
        }

        for (var peer = 0; peer < 2; peer++)
        {
            var participant = session.Participants[seats[peer]];
            connections[peer] = 100 + peer;
            TestAssert.True(session.Authenticate(connections[peer], session.BuildKeyCheck(participant.PlayerId)) is not null,
                $"seat {peer} authenticates");
            foreach (var packet in session.BuildLoadingBurst(connections[peer])) Feed(peer, packet);
            foreach (var packet in session.Spawn(connections[peer])) Feed(peer, packet);
            TestAssert.True(session.MarkReady(connections[peer]), $"seat {peer} reaches ready");
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team,
                participant.Hero!.NetId, captures[peer], ready: true, view: participant.View, clientId: participant.ClientId));
        }

        void Pump()
        {
            foreach (var output in session.RunSchedulers())
                Feed(Array.IndexOf(connections, output.ConnectionId), output.Packet);
        }

        const double dt = 1.0 / 30.0;
        var steps = (int)Math.Round(seconds / dt);
        for (var step = 0; step < steps; step++)
        {
            Pump();
            runtime.Tick(dt);
            for (var peer = 0; peer < 2; peer++)
            {
                foreach (var packet in captures[peer].Packets) Feed(peer, packet);
                captures[peer].Packets.Clear();
            }
        }

        return new Run(checker.Finish(), checker.TotalsByKind, checker.PacketsObserved, opcodes, runtime.World.Clock.Now(),
            wire.StaleReplicationDropped);
    }

    /// <summary>
    /// The same real stream with the historic bugs put back, one fault family per peer, so a zero in the clean
    /// control means "the rules ran and found nothing" and not "the rules cannot see this stream".
    /// </summary>
    [Check("packet-rules.control-detects-injected-faults")]
    public static void ControlDetectsInjectedFaults()
    {
        PacketObservation? Tamper(PacketObservation observation)
        {
            var bytes = observation.Plaintext.Span;
            if (bytes.IsEmpty || PacketCatalog.IsChatChannel(observation.Channel)) return observation;
            var opcode = bytes[0];
            if (observation.Peer == 0)
            {
                // Peer 0: minion spawns never arrive (units the client was never told about), waypoints go out on
                // the gameplay channel, and from 100 s every hero 0xCC carries syncID 0 (never applied).
                if (opcode == 0x80) return null;
                if (opcode == 0xC1) return observation with { Channel = 3 };
                if (opcode == 0xCC && observation.T > 100.0 && bytes.Length >= 15 && bytes[11] <= 0x3F && bytes[12] == 0 &&
                    bytes[13] == 0 && bytes[14] == 0x40)
                {
                    var copy = bytes.ToArray();
                    copy.AsSpan(5, 4).Clear();
                    return observation with { Plaintext = copy };
                }
            }
            else
            {
                // Peer 1: no minion ever gets a move speed or stat template (the freeze: shown minions with no replicated
                // stat block, zeroed by the next 0xCC), and attacks go out reliably.
                if (opcode == 0xCC && bytes.Length is 111 or 23 && bytes[10] is 0x0A or 0x08 && bytes[13] == 0x01 &&
                    bytes[14] == 0x40)
                    return null;
                if (opcode == 0x0D) return observation with { Reliable = true };
            }
            return observation;
        }

        var run = RunProductionMatch(130.0, Tamper);
        string Detail(string kind, int peer) => string.Join("\n", run.Violations
            .Where(violation => violation.Kind == kind && violation.Peer == peer).Take(3)
            .Select(violation => $"t={violation.T:0.###} 0x{violation.Opcode:X2} {violation.Detail}"));
        foreach (var (kind, peer) in new[]
                 {
                     (PacketRuleKinds.UnknownUnit, 0), (PacketRuleKinds.ChannelOrReliability, 0),
                     (PacketRuleKinds.SyncIdRegression, 0), (PacketRuleKinds.ReplicatedStatZeroed, 1),
                     (PacketRuleKinds.ChannelOrReliability, 1),
                 })
            TestAssert.True(Detail(kind, peer).Length > 0, $"injected fault not reported: {kind} on peer {peer}");
        // And only where injected: peer 1 still knows every unit and peer 0 still gets its stats.
        TestAssert.Equal("", Detail(PacketRuleKinds.UnknownUnit, 1), "peer 1 creation was not tampered with");
        TestAssert.Equal("", Detail(PacketRuleKinds.ReplicatedStatZeroed, 0), "peer 0 stats were not tampered with");
        TestAssert.True(run.Violations.Any(v => v is { Kind: PacketRuleKinds.ChannelOrReliability, Peer: 0, Opcode: 0xC1 }), "waypoint channel");
        TestAssert.True(run.Violations.Any(v => v is { Kind: PacketRuleKinds.ChannelOrReliability, Peer: 1, Opcode: 0x0D }), "attack delivery");
        TestAssert.True(run.Violations.Any(v => v is { Kind: PacketRuleKinds.ReplicatedStatZeroed, Peer: 1 } &&
            v.Detail.Contains("flushes all characters", StringComparison.Ordinal)), "the freeze: shown but never replicated");
    }

    /// <summary>
    /// Production queues a unit's stats at creation and the observer's first sight rebuilds them with newer sync
    /// ids in the same flush. The older copy used to follow (the client drops it): heroes in the first flush and
    /// every friendly wave at 1:30. The checker counts such a packet as StaleReplicationDropped. Since batch 67 the count is taken
    /// on the stream the connection's replication store lets through; the delivery itself still queues one stale copy of the connected
    /// hero's creation stats (its spawn burst already carried newer ones), which the store removes before the wire.
    /// </summary>
    [Check("packet-rules.production-sends-no-stale-replication")]
    public static void ProductionSendsNoStaleReplication()
    {
        var run = RunProductionMatch(95.0);
        TestAssert.True(run.Elapsed >= 94.0, $"the match ran {run.Elapsed:0.0} s");
        TestAssert.True(run.Opcodes.GetValueOrDefault((byte)0x80) > 0, "the 1:30 wave spawned");
        TestAssert.Equal(0L, run.StaleReplication, "stale queued 0xCC templates delivered behind first-sight ones");
        TestAssert.Equal(0, run.Violations.Count, "short production match stays clean");
    }

    [Check("packet-rules.clean-production-control")]
    public static void CleanProductionControl()
    {
        var run = RunProductionMatch(Seconds);
        TestAssert.True(run.Elapsed >= Seconds - 1.0, $"the match ran {run.Elapsed:0.0} s");
        TestAssert.Equal(0L, run.StaleReplication, "no stale queued 0xCC is delivered");
        // The control is not vacuous: waves spawned and fought, towers and heroes replicated stats, attacks flew.
        foreach (var (opcode, name) in new (byte, string)[] { (0x4F, "CreateHero"), (0xA5, "CreateTurret"), (0x80, "SpawnMinion"),
                     (0xCC, "OnReplication"), (0xC1, "WaypointList"), (0x0D, "BasicAttack"), (0xC2, "EnterVisibility"),
                     (0x68, "ApplyDamage"), (0xA6, "NpcDie") })
            TestAssert.True(run.Opcodes.GetValueOrDefault(opcode) > 0, $"the match produced no {name} (0x{opcode:X2})");
        TestAssert.True(run.Packets > 5000, $"{run.Packets} packets observed");
        var report = string.Join("\n", run.Violations.Select(violation =>
            $"{violation.Kind} peer={violation.Peer} t={violation.T:0.###} op=0x{violation.Opcode:X2} " +
            $"net={(violation.NetId is { } id ? $"0x{id:X8}" : "-")} {violation.Detail}"));
        TestAssert.True(run.Violations.Count == 0,
            $"the production stream breaks {run.Violations.Count} packet rules over {run.Packets} packets:\n{report}");
    }
}
