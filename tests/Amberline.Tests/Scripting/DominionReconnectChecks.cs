using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>
/// Batch 472: a client that returns to a running map 8 match gets Dominion's live state, which the server otherwise sends only when it
/// changes: each point's capture records (0xDB value, 0xDC rate), the respawn points (0xDE create and state), both victory meters (0xDD) and
/// its team's open quests (0x90 adds). Two Annie seats, the production composition, one observer per seat; the Chaos seat reconnects through
/// the session's staged path and receives the observer flush and then the burst of <c>MatchRuntimeNetwork.BuildReconnectState</c>, in the
/// order of <c>MatchRuntimeNetwork.Tick</c> (flush, then <c>SendReconnectStates</c>). A point whose guardian the returning client does not
/// hold yet (Order's point A: an owned guardian gives sight to its own team only) gets its records at the client's first sight of it.
/// </summary>
public static class DominionReconnectChecks
{
    private const double Step = 1.0 / 30.0;

    [Check("dominion.reconnecting-client-gets-the-current-dominion-state", Serial = true, TimeoutSeconds = 900)]
    public static void ReconnectingClientGetsTheCurrentDominionState()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            var roster = new[]
            {
                new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
                new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            var captures = new Capture[roster.Length];
            for (var id = 1; id <= roster.Length; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
                var seat = runtime.Session.Participants[id - 1];
                captures[id - 1] = new Capture();
                runtime.Delivery.AddObserver(new ReplicationObserver((ulong)id, seat.Team, seat.Hero!.NetId, captures[id - 1],
                    ready: true, view: seat.View));
            }
            TestAssert.True(runtime.Session.Start(), "the map 8 match starts");
            runtime.Tick(Step);
            var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests
                ?? throw new InvalidOperationException("map 8 runs its quest host");
            // A fresh client at the start is no returning seat: the session hands no reconnect burst out for it.
            TestAssert.True(runtime.Session.ConsumeReconnects().Count == 0, "a client that joined at the start gets no reconnect burst");

            // DIAGNOSTICS: test seams. Chaos holds point E (the tree's owner and full progress, the guardian on Chaos, as a capture leaves
            // them); Order takes point A through the shipped capture tree (neutral with CaptureProgress past 99 is its "Order Capture" branch,
            // OdinScriptBehaviorTrees.xml:1261-1280).
            quests.Captures[4].SetVariable("CurrentPointOwner", Team.Chaos);
            quests.Captures[4].SetVariable("CaptureProgress", -100.0);
            Guardian(runtime, quests, 5).Team = Team.Chaos;
            quests.Captures[0].SetVariable("CurrentPointOwner", Team.Neutral);
            quests.Captures[0].SetVariable("CaptureProgress", 100.0);
            for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
            TestAssert.True(quests.CapturePointOwners[1] == Team.Order, "Order captured point A");
            var guardianA = Guardian(runtime, quests, 1);
            TestAssert.True(guardianA.Team == Team.Order, "point A's guardian took Order's team");
            // Batch 475, decision (a) (Reconstruction): an owned guardian is visible to both teams, and it stays visible. The guardian is the point's
            // structure: in the 126 client structures (turrets, inhibitors, nexuses) stay visible to both teams; the neutral guardian already carries
            // both teams' sight (OdinGuardianBuff blocks 6 and 7); and the ring and the minimap show each owner to both teams, which needs every client
            // to hold and show the bound unit (the 0xDC proof: a client applies a record only to a unit it holds). The live Chaos client (captures[1],
            // connected through the capture) holds the guardian from its creation with its visibility (0xC2 after its 0x66), and keeps it (no 0x54
            // leave). Batch 673: the guardian changes team in place (0xE0), so its creation is the start's, and OdinGuardianBuff gives its 800 sight to
            // Order alone; the guardian stays seen by both teams (Entity.SeenByBothTeams).
            var liveChaos = captures[1].Packets.Select(packet => packet.Plaintext.ToArray()).ToList();
            var liveCreated = liveChaos.FindLastIndex(packet => Creates(packet, guardianA.NetId));
            var liveSeen = liveCreated >= 0 ? liveChaos.FindIndex(liveCreated + 1, packet => packet[0] == VisionPackets.EnterVisibilityOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == guardianA.NetId) : -1;
            TestAssert.True(liveSeen > liveCreated, "the live Chaos client sees the owned guardian after the swap (EnterVisibility, 0xC2, after its 0x66)");
            var leaves = VisionPackets.LeaveVisibility(guardianA.NetId).Select(packet => packet.Plaintext.ToArray()).ToList();
            TestAssert.True(!liveChaos.Skip(liveSeen + 1).Any(packet => leaves.Any(leave => leave.AsSpan().SequenceEqual(packet))),
                "the live Chaos client keeps the owned guardian in sight (no LeaveVisibility after it)");
            TestAssert.True(liveChaos.Any(packet => packet.Length == 13 && packet[0] == 0xE0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == guardianA.NetId &&
                    BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(9)) == TeamMapping.ToNative(Team.Order)),
                "the live Chaos client is told point A's guardian is Order's (UnitChangeTeam, 0xE0)");

            // A quest set goes out at 5:00 (each team holds a point); then both nexuses lose health (DIAGNOSTICS: the damage seam the quest sap uses).
            // Batch 711: the waves neutralise an undefended point before 5:00 (Chaos's E1 waves take Order's A at about 3:50, DominionQuestChecks.Retake),
            // so each team takes its point back through the same capture-tree seam just before (DIAGNOSTICS).
            while (runtime.Combat.CurrentTime + Step / 2 < 299.0) runtime.Tick(Step);
            foreach (var (point, team) in new[] { (1, Team.Order), (5, Team.Chaos) })
            {
                if (quests.CapturePointOwners.GetValueOrDefault(point) == team) continue;
                quests.Captures[point - 1].SetVariable("CurrentPointOwner", Team.Neutral);
                quests.Captures[point - 1].SetVariable("CaptureProgress", team == Team.Order ? 100.0 : -100.0);
                for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
                TestAssert.True(quests.CapturePointOwners[point] == team, $"{team} takes point {point} back before 5:00");
            }
            while (runtime.Combat.CurrentTime + Step / 2 < 300.5) runtime.Tick(Step);
            var issued = captures[1].Of(DominionPackets.QuestUpdateOpcode).Where(packet => packet[0x186] == DominionPackets.QuestAdd).ToList();
            TestAssert.True(issued.Count == 2, $"the Chaos client got its two quest adds at 5:00, got {issued.Count}");
            runtime.Combat.DamageDominionNexus(Team.Order, 40.0);
            runtime.Combat.DamageDominionNexus(Team.Chaos, 75.0);
            runtime.Tick(Step);
            foreach (var team in new[] { Team.Order, Team.Chaos })
                TestAssert.True(runtime.Combat.DominionNexusHealth(team) < 500.0, $"the {team} nexus drained");

            // The Chaos client leaves; Dominion changes while it is away (Order's nexus loses more, which only the staying client hears).
            var chaos = runtime.Session.Participants[1];
            TestAssert.True(runtime.Session.Disconnect(2), "the Chaos client disconnects");
            TestAssert.True(runtime.Delivery.RemoveObserver(2), "the Chaos observer is removed");
            runtime.Combat.DamageDominionNexus(Team.Order, 13.0);
            for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);

            // The staying Order client's view of the state: its last record of each kind.
            var staying = captures[0];
            // The owner rule (batch 475, second follow-up): the owning Order client sees its own guardian. After the guardian's creation (0x66, the
            // net id at 5), the client gets its visibility (EnterVisibility, 0xC2, the net id at 1). Checked on the staying client, before the reconnect.
            var stayingPackets = staying.Packets.Select(packet => packet.Plaintext.ToArray()).ToList();
            var ownerCreated = stayingPackets.FindLastIndex(packet => Creates(packet, guardianA.NetId));
            TestAssert.True(ownerCreated >= 0 && stayingPackets.Skip(ownerCreated + 1).Any(packet => packet[0] == VisionPackets.EnterVisibilityOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == guardianA.NetId),
                "the owning Order client sees point A's guardian after its creation (EnterVisibility, 0xC2)");
            var expected = new Dictionary<(byte Opcode, uint Key), byte[]>();
            foreach (var packet in staying.Packets.Select(packet => packet.Plaintext.ToArray()))
                if (RecordKey(packet) is { } key) expected[key] = packet;

            // The reconnect: the staged handshake, the baseline, the ready boundary, the observer, then one step (flush + burst).
            const long connection = 902;
            var seatBack = runtime.Session.AuthenticateReconnect(connection, runtime.Session.BuildKeyCheck(2));
            TestAssert.True(seatBack == chaos, "the Chaos seat reconnects");
            runtime.Session.Receive(connection, 1, [0xC5]);
            runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
            runtime.World.Clock.SetVirtual(chaos.SpawnAt ?? throw new InvalidOperationException("the reconnect armed no spawn"));
            runtime.Session.RunSchedulers();
            runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
            runtime.Session.RunSchedulers();
            TestAssert.True(chaos is { Spawned: true, Loaded: true, Ready: true }, "the returning seat reaches ready");
            var back = new Capture();
            runtime.Delivery.AddObserver(new ReplicationObserver(chaos.PlayerId, chaos.Team, chaos.Hero!.NetId, back,
                ready: true, view: chaos.View, clientId: chaos.ClientId));
            staying.Clear();
            runtime.Tick(Step);
            var flushed = back.Packets.Count;
            foreach (var participant in runtime.Session.ConsumeReconnects())
                back.Packets.AddRange(MatchRuntimeNetwork.BuildReconnectState(runtime, participant));
            var stream = back.Packets.Select(packet => packet.Plaintext.ToArray()).ToList();
            TestAssert.True(!staying.Packets.Any(packet => RecordKey(packet.Plaintext.ToArray()) is not null),
                "the staying client receives no Dominion record at the other seat's reconnect");

            // Existence (batch 475, second follow-up): every client holds every point's current guardian, whichever team owns it. The 0xDC proof
            // (the client's capture-record handlers): a client applies a capture record only to a unit it holds, and the ring and the minimap
            // show each point's owner (the bound unit's team) to both teams. So the reconnect flush creates point A's guardian on Order's team as
            // well, before any record names it. Sight is separate and is asserted at the sight bubble below.
            Created(stream, guardianA, 'A', flushed);
            for (var point = 2; point <= 5; point++)
                Created(stream, Guardian(runtime, quests, point), Letter(point), flushed);

            // The burst carries the state the staying client holds: each held point's 0xDB and 0xDC, its respawn point's 0xDE create (and its
            // state, which no shipped flow sends yet: nothing calls RespawnLogic, batch 453), both 0xDD meters, each the last one the staying
            // client got.
            var received = Last(stream);
            // The records of a point name its current guardian (batch 475's re-created guardians left old ones' records behind; batch 673 keeps one
            // unit); the team meters and the quests have no unit.
            var current = Enumerable.Range(2, 5).Select(point => Guardian(runtime, quests, point - 1).NetId).ToHashSet();
            foreach (var (key, packet) in expected.Where(pair => pair.Key.Opcode != DominionPackets.QuestUpdateOpcode &&
                (pair.Key.Opcode == DominionPackets.SetGameScoreOpcode || current.Contains(pair.Key.Key))))
                TestAssert.True(received.TryGetValue(key, out var got) && got.AsSpan().SequenceEqual(packet),
                    $"the returning client holds the staying client's last 0x{key.Opcode:X2} record for {key.Key}");
            for (var point = 2; point <= 5; point++)
                RespawnCreated(stream, Guardian(runtime, quests, point), point - 1);
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                var meter = received[(DominionPackets.SetGameScoreOpcode, (uint)TeamMapping.ToNative(team))];
                var score = BinaryPrimitives.ReadInt32LittleEndian(meter.AsSpan(9));
                TestAssert.True(score == (int)Math.Floor(runtime.Combat.DominionNexusHealth(team)) && score < 500,
                    $"the {team} meter shows its drained nexus ({score})");
            }

            // Its team's open quests, as issued (ids, types and keys), and no repeated announcement; none of Order's quests.
            var replayed = stream.Where(packet => packet[0] == DominionPackets.QuestUpdateOpcode).ToList();
            TestAssert.True(replayed.Count == 2 && issued.All(add => replayed.Any(packet => packet.AsSpan().SequenceEqual(add))),
                $"the returning client gets its team's two open quests as issued, got {replayed.Count} quest packets");
            TestAssert.True(!stream.Any(packet => packet[0] == 0x4A && packet.Length > 5 && packet[5] == 98), "the quest announcement is not repeated");

            // Sight rule changed (batch 475, decision a; Reconstruction): the owned guardian is visible to both teams, so the returning Chaos client sees
            // it in the reconnect flush, at its creation (EnterVisibility, 0xC2, the net id at 1, after the 0x66). Batch 472's rule (a Chaos sight
            // bubble reveals it) is replaced: the guardian is the point's structure (turrets, inhibitors and nexuses stay visible to both teams in the
            // 126 client), and the ring and the minimap need every client to hold and show the bound unit (the 0xDC proof).
            var createdA = stream.FindIndex(packet => Creates(packet, guardianA.NetId));
            TestAssert.True(createdA >= 0 && stream.FindIndex(createdA + 1, packet => packet[0] == VisionPackets.EnterVisibilityOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == guardianA.NetId) > createdA,
                "the returning Chaos client sees point A's guardian at its creation (EnterVisibility, 0xC2, after its 0x66)");
            // The sight bubble no longer reveals the guardian; the flush after it re-creates nothing (the guardian is held once).
            var before = back.Packets.Count;
            runtime.World.AddTimedVisionBubble(Team.Chaos, guardianA.Position, 500.0, 5.0);
            runtime.Tick(Step);
            var sight = back.Packets.Skip(before).Select(packet => packet.Plaintext.ToArray()).ToList();
            TestAssert.True(!sight.Any(packet => Creates(packet, guardianA.NetId)),
                "the sight flush does not create point A's guardian again");
            // The burst's last records of point A: the 0xDB carries the capture index (0), the 0xDC the guardian's PAR type (0, MP). The
            // batch 472 expectation (255 on 0xDB) encoded the old layout (batch 475); batch 494
            // read the 0xDC byte as AttachCapturePointToObject's PrimaryAbilityResourceType, not a progress.
            TestAssert.True(received[(DominionPackets.CapturePointEventOpcode, guardianA.NetId)][10] == 0,
                "point A's 0xDB carries its capture index 0");
            TestAssert.True(received[(DominionPackets.CapturePointValueOpcode, guardianA.NetId)][10] == 0,
                "point A's 0xDC names the guardian's PAR type (0, MP)");
            RespawnCreated(stream, guardianA, 0);
            before = back.Packets.Count;
            runtime.Tick(Step);
            TestAssert.True(!back.Packets.Skip(before).Any(packet => Names(packet.Plaintext.ToArray(), guardianA.NetId)),
                "point A's records are not repeated on the flushes after the sight");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // A Dominion record's identity: the opcode and the unit it names (0xDB, 0xDC, 0xDE), the native team (0xDD) or the quest id (0x90).
    private static (byte Opcode, uint Key)? RecordKey(byte[] packet) => packet.Length == 0 ? null : packet[0] switch
    {
        DominionPackets.CapturePointEventOpcode or DominionPackets.CapturePointValueOpcode or DominionPackets.RespawnPointOpcode
            => (packet[0], NamedNetId(packet)),
        DominionPackets.SetGameScoreOpcode => (packet[0], BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5))),
        DominionPackets.QuestUpdateOpcode => (packet[0], BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0x188))),
        _ => null,
    };

    // The unit a capture record names (DominionPackets): 0xDB at 5, 0xDC at 6, 0xDE create at 7 and state at 11.
    private static uint NamedNetId(byte[] packet) => packet[0] switch
    {
        DominionPackets.CapturePointEventOpcode => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)),
        DominionPackets.CapturePointValueOpcode => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packet[5] == 0 ? 7 : 11)),
    };

    private static bool Names(byte[] packet, uint netId) =>
        packet.Length != 0 && packet[0] is DominionPackets.CapturePointEventOpcode or DominionPackets.CapturePointValueOpcode
            or DominionPackets.RespawnPointOpcode && NamedNetId(packet) == netId;

    // The last record of each key in a stream, as a client ends up holding it.
    private static Dictionary<(byte Opcode, uint Key), byte[]> Last(IEnumerable<byte[]> stream)
    {
        var last = new Dictionary<(byte Opcode, uint Key), byte[]>();
        foreach (var packet in stream)
            if (RecordKey(packet) is { } key) last[key] = packet;
        return last;
    }

    // Batch 482: the guardian is created through the neutral creation (0x66 CreateNeutral, SpawnPackets.CreateNeutral: net id at 5, native team
    // at 302; ProductionMatchComposition.BuildCreation), not SpawnMinion (0x80, net id at 5, team at 30), which this check was written for.
    private static bool Creates(byte[] packet, uint netId) =>
        packet.Length == CreateNeutralLength && packet[0] == 0x66 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == netId;

    private const int CreateNeutralLength = 323;

    // The guardian's creation is among the first `within` packets, on its current team, and comes before every record that names it.
    private static void Created(List<byte[]> stream, Entity guardian, char point, int within)
    {
        var created = stream.FindIndex(packet => Creates(packet, guardian.NetId));
        TestAssert.True(created >= 0 && created < within, $"point {point}'s guardian is created on the returning client by the flush");
        TestAssert.True(BinaryPrimitives.ReadInt32LittleEndian(stream[created].AsSpan(302)) == TeamMapping.ToNative(guardian.Team),
            $"point {point}'s guardian is created on its current team ({guardian.Team})");
        var named = stream.FindIndex(packet => Names(packet, guardian.NetId));
        TestAssert.True(named > created, $"point {point}'s records follow its guardian's creation (first record {named}, creation {created})");
    }

    // The 0xDE create of a graveyard (11 bytes: command 0, the index at 6, the guardian's net id at 7).
    private static void RespawnCreated(List<byte[]> stream, Entity guardian, int graveyard) =>
        TestAssert.True(stream.Any(packet => packet[0] == DominionPackets.RespawnPointOpcode && packet.Length == 11 && packet[5] == 0 &&
                packet[6] == graveyard && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7)) == guardian.NetId),
            $"respawn point {graveyard} is created on the returning client");

    private static char Letter(int point) => (char)('A' + point - 1);

    private static Entity Guardian(MatchRuntime runtime, BehaviorTreeQuestHost quests, int point)
    {
        var at = (Position3)quests.Captures[point - 1].Variable("CapturePosition")!;
        return runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
            .OrderBy(unit => Math.Abs(unit.Position.X - at.X) + Math.Abs(unit.Position.Z - at.Z)).First();
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);

        public void Clear() => Packets.Clear();

        public IEnumerable<byte[]> Of(byte opcode) =>
            Packets.Select(packet => packet.Plaintext.ToArray()).Where(packet => packet.Length != 0 && packet[0] == opcode);
    }
}
