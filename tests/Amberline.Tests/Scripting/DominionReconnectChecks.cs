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

    /// <summary>
    /// Batch 725 (owner, 9 November 2026: "after reconnecting the stairs vanished [...] Stairs vanishing possibly not sending packet on
    /// reconnect"): a client that returns holds the Dominion props as a client that stayed. Three seats, the production composition: Order, a
    /// Chaos seat that leaves and returns, and a Chaos seat that stays (the same team, so the same laser and core colours). The returning seat
    /// leaves and comes back (a) before the stairs close, (b) during a closing stage, (c) after the release (the stairs raised and held) and
    /// (d) after the Chaos nexus falls. A client's prop state is what its stream leaves it holding (<see cref="PropState"/>): the props it
    /// created, each prop's last clip (the client plays the last 0xDA it got, seeking to now minus the clip's mission start) and the live
    /// effects on the props (the lasers, the lit cores and the gems). The returning client's stream starts at its reconnect, as a restarted
    /// client holds nothing: the session's reconnect bursts, the observer flush, then the reconnect burst, in production's order.
    /// </summary>
    [Check("dominion.reconnecting-client-sees-the-stairs-crystals-and-lasers-as-a-client-that-stayed", Serial = true, TimeoutSeconds = 900)]
    public static void ReconnectingClientSeesTheDominionPropsAsAClientThatStayed()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            var roster = new[]
            {
                new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
                new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
                new SessionParticipantDefinition(3, 2, "Chaos2", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            for (var id = 1; id <= roster.Length; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
            }
            // The staying Chaos client: its map-ready burst (the props' creates, the crystals' holds and gems), built before the start as
            // the session's scheduler builds it, then everything its observer gets.
            var leaving = runtime.Session.Participants[1];
            var staying = runtime.Session.Participants[2];
            var stayed = new Capture();
            foreach (var entity in runtime.Session.MapEntities) staying.View.Known.Add(entity.NetId);
            stayed.Packets.AddRange(runtime.Session.BuildMapReadyPackets(3));
            foreach (var seat in runtime.Session.Participants.Where(seat => seat != staying))
                runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId, new Capture(), ready: true,
                    view: seat.View));
            runtime.Delivery.AddObserver(new ReplicationObserver(staying.PlayerId, staying.Team, staying.Hero!.NetId, stayed, ready: true,
                view: staying.View));
            TestAssert.True(runtime.Session.Start(), "the map 8 match starts");

            var phases = new (string Name, double Leave, double Back, Action? Away)[]
            {
                ("(a) before the stairs close", 1.0, 3.0, null),
                ("(b) during a closing stage", 25.0, 30.0, null),
                ("(c) after the release, the stairs raised and held", 50.0, 90.0, null),
                // DIAGNOSTICS: the Chaos nexus falls while the client is away (the nexus damage seam), which ends the match.
                ("(d) after the Chaos nexus fell", 95.0, 105.0, () => runtime.Combat.DamageDominionNexus(Team.Chaos, 1000.0)),
            };
            // Every phase's differences are gathered first, so a failure lists what the returning client lacked in each phase.
            var differences = new List<string>();
            var connection = 2L;
            Capture? back = null;
            foreach (var (name, leave, backAt, away) in phases)
            {
                RunTo(runtime, leave);
                if (back is not null) differences.AddRange(Differences($"{name}, before leaving", stayed, back));
                TestAssert.True(runtime.Session.Disconnect(connection), $"{name}: the Chaos client disconnects");
                TestAssert.True(runtime.Delivery.RemoveObserver(leaving.PlayerId), $"{name}: its observer is removed");
                away?.Invoke();
                RunTo(runtime, backAt);
                connection = 900 + (long)backAt;
                back = Reconnect(runtime, leaving, connection);
                differences.AddRange(Differences(name, stayed, back));
            }
            RunTo(runtime, 112.0);
            differences.AddRange(Differences("(d), later", stayed, back!));
            TestAssert.True(differences.Count == 0, "the returning Chaos client holds the props as the Chaos client that stayed:\n"
                + string.Join("\n", differences));

            // The phases reached the states they name, on the client that stayed.
            var final = PropState(stayed.Packets.Select(packet => packet.Plaintext.ToArray()));
            TestAssert.True(final[Clip(DominionProps.OrderStairsNetId)].StartsWith("Raised_Idle ", StringComparison.Ordinal),
                $"the Order stairs hold Raised_Idle ({final[Clip(DominionProps.OrderStairsNetId)]})");
            TestAssert.True(final[Clip(DominionProps.ChaosStairsNetId)].StartsWith("Death ", StringComparison.Ordinal),
                $"the fallen Chaos stairs hold their Death ({final[Clip(DominionProps.ChaosStairsNetId)]})");
            TestAssert.True(final[Clip(DominionProps.ChaosCrystalNetId)].StartsWith("Explode ", StringComparison.Ordinal),
                $"the fallen Chaos crystal holds its Explode ({final[Clip(DominionProps.ChaosCrystalNetId)]})");
            TestAssert.Equal(13, final.Keys.Count(key => key.StartsWith("effect ", StringComparison.Ordinal)),
                "the Order base's 8 lasers, 4 lit cores and gem stay lit; the fallen Chaos base's are out");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void RunTo(MatchRuntime runtime, double seconds)
    {
        while (runtime.Combat.CurrentTime + Step / 2 < seconds) runtime.Tick(Step);
    }

    // The returning client, as the session and MatchRuntimeNetwork.Tick serve it: the staged handshake (its spawn burst, then the map-ready
    // burst on the scheduler pass after it is loaded), a new observer, one step (the flush) and the reconnect burst.
    private static Capture Reconnect(MatchRuntime runtime, SessionParticipant seat, long connection)
    {
        TestAssert.True(runtime.Session.AuthenticateReconnect(connection, runtime.Session.BuildKeyCheck(seat.PlayerId)) == seat, "the seat reconnects");
        var output = new List<SessionOutboundPacket>();
        output.AddRange(runtime.Session.Receive(connection, 1, [0xC5]));
        output.AddRange(runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]));
        runtime.World.Clock.SetVirtual(seat.SpawnAt ?? throw new InvalidOperationException("the reconnect armed no spawn"));
        output.AddRange(runtime.Session.RunSchedulers());
        output.AddRange(runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]));
        output.AddRange(runtime.Session.RunSchedulers());
        TestAssert.True(seat is { Spawned: true, Loaded: true, Ready: true }, "the returning seat reaches ready");
        var back = new Capture();
        back.Packets.AddRange(output.Where(item => item.ConnectionId == connection).Select(item => item.Packet));
        runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId, back, ready: true, view: seat.View,
            clientId: seat.ClientId));
        runtime.Tick(Step);
        foreach (var participant in runtime.Session.ConsumeReconnects())
            back.Packets.AddRange(MatchRuntimeNetwork.BuildReconnectState(runtime, participant));
        return back;
    }

    private static readonly uint[] PropNetIds =
    [
        DominionProps.OrderStairsNetId, DominionProps.ChaosStairsNetId, DominionProps.OrderCrystalNetId, DominionProps.ChaosCrystalNetId,
    ];

    private static string Clip(uint prop) => $"clip {prop:X8}";

    // What a stream leaves a client holding of the Dominion props: each prop it created (0xD9, the netid at 5), each prop's last clip (0xDA:
    // the name at 5, the mission start at 69, the duration at 73, the netid at 77, the flags at 81) and the live effects bound to or aimed at
    // a prop (0x8C: the effect at 6, the bind bone at 16, the target at 21, the effect's own netid at 25, the bind at 29), less those killed
    // (0x3D, the effect's netid at 5).
    private static SortedDictionary<string, string> PropState(IEnumerable<byte[]> stream)
    {
        var state = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var packet in stream)
        {
            if (packet.Length < 9) continue;
            uint At(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));
            if (packet[0] == LevelPropPackets.CreateOpcode && PropNetIds.Contains(At(5)))
                state[$"prop {At(5):X8}"] = Convert.ToHexString(packet);
            else if (packet[0] == LevelPropPackets.AnimationOpcode && packet.Length == LevelPropPackets.AnimationLength && PropNetIds.Contains(At(77)))
                state[Clip(At(77))] = $"{System.Text.Encoding.ASCII.GetString(packet, 5, 64).Split('\0')[0]} start "
                    + $"{BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(69)):0.000} duration {BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(73)):0.000} flags 0x{At(81):X}";
            else if (packet[0] == FxPackets.CreateGroupOpcode && packet.Length >= 33 && (PropNetIds.Contains(At(21)) || PropNetIds.Contains(At(29))))
                state[$"effect {At(25):X8}"] = $"0x{At(6):X8} from bone 0x{At(16):X8} of {At(29):X8} to {At(21):X8}";
            else if (packet[0] == FxPackets.KillOpcode && packet.Length == 9)
                state.Remove($"effect {At(5):X8}");
        }
        return state;
    }

    private static IEnumerable<string> Differences(string phase, Capture stayed, Capture back)
    {
        var expected = PropState(stayed.Packets.Select(packet => packet.Plaintext.ToArray()));
        var actual = PropState(back.Packets.Select(packet => packet.Plaintext.ToArray()));
        return expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)
            .Where(key => expected.GetValueOrDefault(key) != actual.GetValueOrDefault(key))
            .Select(key => $"{phase}: {key}: stayed {expected.GetValueOrDefault(key, "(none)")}, returned {actual.GetValueOrDefault(key, "(none)")}");
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
