using System.Text.Json;
using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Stats;
using Amberline.Simulation;

namespace Amberline.Tests.Network;

public static class SessionChecks
{
    [Check("session.playtest-b4-team-spawn-slots")]
    public static void TeamSpawnSlots()
    {
        var navigation = NavigationGrid.LoadDefault();
        foreach (var withBots in new[] { false, true })
        {
            var roster = Enumerable.Range(0, 4).Select(index => new SessionParticipantDefinition(
                (ulong)index + 1, (uint)index, $"Seat{index}", "Annie",
                index % 2 == 0 ? Team.Order : Team.Chaos, IsBot: withBots && index >= 2)).ToArray();
            var session = NewSession(roster: roster);
            session.World.Clock.SetVirtual();
            session.CreateWorld();
            // Order seats: the exact __Spawn_T1.SCB point plus the HeroSpawnOffsets Order2 slots (-100,120) and (120,-100)
            // (owner decision, 18 October 2026: the anchor is no longer the rounded (26, 264)).
            Position2[] positions = [new(25.902751922607422 - 100, 263.5519104003906 + 120), new(13933.7890625 - 120, 14170.09375 + 110),
                new(25.902751922607422 + 120, 263.5519104003906 - 100), new(13933.7890625 + 100, 14170.09375 - 110)];
            byte[] slots = [0, 0, 1, 1];
            var production = new ProductionServices(session.World, GameData.Default,
                navigation, new ReplicationSyncCounter()) { Session = session };
            TestAssert.Equal(4, session.Participants.Select(member => member.Hero!.Position).Distinct().Count());
            for (var index = 0; index < 4; index++)
            {
                var member = session.Participants[index];
                TestAssert.Equal(slots[index], member.SpawnPositionIndex);
                TestAssert.Equal(positions[index], member.Hero!.Position);
                var creation = production.BuildCreation(member.Hero).Single().Plaintext.Span;
                TestAssert.Equal(slots[index], creation[18], "replication uses the same retained slot");
                TestAssert.Equal((byte)(member.IsBot ? 1 : 0), creation[16]);
                var direction = index < 2 ? 1.0 : -1.0;
                TestAssert.Near(direction / Math.Sqrt(2), member.Hero.Facing.X, 1e-12);
                TestAssert.Near(-direction / Math.Sqrt(2), member.Hero.Facing.Z, 1e-12);
                if (member.IsBot) continue;
                var connection = 11L + index;
                TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(member.PlayerId)) is not null);
                AssertBurst(session.Spawn(connection), index, reconnect: false);
                session.MarkReady(connection);
            }
            TestAssert.True(session.Start());
            for (var index = 0; index < 4; index++)
            {
                var member = session.Participants[index];
                if (member.IsBot) continue;
                var hero = member.Hero!;
                TestAssert.True(session.Disconnect(11L + index));
                var connection = 21L + index;
                TestAssert.True(session.AuthenticateReconnect(connection, session.BuildKeyCheck(member.PlayerId)) is not null);
                session.Receive(connection, 1, [0xC5]);
                session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                session.World.Clock.SetVirtual(member.SpawnAt!.Value);
                var burst = session.RunSchedulers().Where(output => output.ConnectionId == connection)
                    .Select(output => output.Packet).ToArray();
                AssertBurst(burst, index, reconnect: true);
                TestAssert.True(ReferenceEquals(hero, member.Hero));
                TestAssert.Equal(positions[index], hero.Position, "reconnect preserves the authoritative location");
            }

            // A later reconnect must retain the slot without returning a moved hero to spawn.
            var moved = session.Participants[0];
            positions[0] = new Position2(5000, 6000);
            moved.Hero!.Position = positions[0];
            TestAssert.True(session.Disconnect(21));
            TestAssert.True(session.AuthenticateReconnect(31, session.BuildKeyCheck(moved.PlayerId)) is not null);
            session.Receive(31, 1, [0xC5]);
            session.Receive(31, 1, [0xC6, 0, 0, 0, 0]);
            session.World.Clock.SetVirtual(moved.SpawnAt!.Value);
            AssertBurst(session.RunSchedulers().Where(output => output.ConnectionId == 31)
                .Select(output => output.Packet).ToArray(), 0, reconnect: true);
            TestAssert.Equal(positions[0], moved.Hero.Position);

            void AssertBurst(IReadOnlyList<PlaintextPacket> packets, int recipient, bool reconnect)
            {
                var start = packets.Single(packet => packet.Plaintext.Span[0] == 0x65).Plaintext.Span;
                TestAssert.Equal((byte)0, start[5]);
                TestAssert.Equal((byte)0, start[6]);
                var creates = packets.Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
                TestAssert.Equal(4, creates.Length);
                for (var seat = 0; seat < 4; seat++)
                {
                    var member = session.Participants[seat];
                    var create = creates[seat].Plaintext.Span;
                    TestAssert.Equal(member.Hero!.NetId, BinaryPrimitives.ReadUInt32LittleEndian(create[5..]));
                    TestAssert.Equal(slots[seat], create[18]);
                    TestAssert.Equal((byte)(member.IsBot ? 1 : 0), create[16]);
                    var reveals = packets.Where(packet => packet.Plaintext.Span[0] == 0xC2 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == member.Hero!.NetId).ToArray();
                    TestAssert.Equal(reconnect && seat != recipient ? 0 : 1, reveals.Length,
                        "reconnect baseline preserves hidden-seat privacy");
                    if (reveals.Length == 0) continue;
                    var reveal = reveals[0].Plaintext.Span;
                    TestAssert.Equal((float)positions[seat].X, BinaryPrimitives.ReadSingleLittleEndian(reveal[12..]));
                    TestAssert.Equal((float)positions[seat].Z, BinaryPrimitives.ReadSingleLittleEndian(reveal[16..]));
                    TestAssert.Equal((float)member.Hero.Facing.X, BinaryPrimitives.ReadSingleLittleEndian(reveal[20..]));
                    TestAssert.Equal((float)member.Hero.Facing.Z, BinaryPrimitives.ReadSingleLittleEndian(reveal[24..]));
                }
            }
        }
    }

    [Check("session.bot-roster-spawn-offsets-counted-once")]
    public static void BotRosterSpawnOffsetsCountedOnce()
    {
        var offsets = InibinReader.LoadData("Characters/HeroSpawnOffsets.inibin");
        foreach (var (orderSize, chaosSize, chaosHuman) in new[] { (1, 1, true), (1, 4, false), (2, 3, true), (5, 5, true), (5, 5, false) })
        {
            var roster = Enumerable.Range(0, orderSize + chaosSize).Select(index =>
                new SessionParticipantDefinition((ulong)index + 1, (uint)index,
                    index < orderSize ? "Annie" : "Orianna", index < orderSize ? "Annie" : "Orianna",
                    index < orderSize ? Team.Order : Team.Chaos,
                    IsBot: index != 0 && (!chaosHuman || index != orderSize))).ToArray();
            var session = NewSession(roster: roster);
            session.World.Clock.SetVirtual();
            session.CreateWorld();
            var humans = session.Participants.Where(member => !member.IsBot).ToArray();
            foreach (var member in humans)
            {
                var connection = 11L + member.ClientId;
                TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(member.PlayerId)) is not null);
                AssertOffsets(connection, session.Spawn(connection));
                TestAssert.True(session.MarkReady(connection));
            }
            TestAssert.True(session.Start());
            foreach (var member in humans)
            {
                TestAssert.True(session.Disconnect(11L + member.ClientId));
                var connection = 31L + member.ClientId;
                TestAssert.True(session.AuthenticateReconnect(connection, session.BuildKeyCheck(member.PlayerId)) is not null);
                session.Receive(connection, 1, [0xC5]);
                session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                session.World.Clock.SetVirtual(member.SpawnAt!.Value);
                AssertOffsets(connection, session.RunSchedulers().Where(output => output.ConnectionId == connection)
                    .Select(output => output.Packet).ToArray());
            }

            void AssertOffsets(long connection, IReadOnlyList<PlaintextPacket> packets)
            {
                var loading = session.BuildLoadingBurst(connection)
                    .Single(packet => packet.Plaintext.Span[0] == 0x67).Plaintext.Span;
                var start = packets.Single(packet => packet.Plaintext.Span[0] == 0x65).Plaintext.Span;
                var creates = packets.Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
                TestAssert.Equal(roster.Length, creates.Length);
                foreach (var team in new[] { Team.Order, Team.Chaos })
                {
                    var order = team == Team.Order;
                    var teamSize = session.Participants.Count(member => member.Team == team);
                    var rosterCount = BinaryPrimitives.ReadUInt32LittleEndian(loading[(order ? 400 : 404)..]);
                    TestAssert.Equal((uint)teamSize, rosterCount, "loading roster includes every bot");
                    var nativeSize = rosterCount + start[order ? 5 : 6];
                    TestAssert.Equal((uint)teamSize, nativeSize, "native roster plus additional seats counts each hero once");
                    var teamCreates = creates.Where(packet => packet.Plaintext.Span[15] == (order ? 1 : 0)).ToArray();
                    TestAssert.Equal(teamSize, teamCreates.Length);
                    TestAssert.Equal(Enumerable.Range(0, teamSize).Select(index => (byte)index).ToArray(),
                        teamCreates.Select(packet => packet.Plaintext.Span[18]).OrderBy(index => index).ToArray());
                    var anchor = order ? new Position2(25.902751922607422, 263.5519104003906) : new Position2(13933.7890625, 14170.09375);
                    foreach (var packet in teamCreates)
                    {
                        var create = packet.Plaintext.Span;
                        var hero = session.Participants.Single(member => member.Hero!.NetId ==
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..])).Hero!;
                        TestAssert.Equal(team, hero.Team);
                        // The client's spawn-table lookup selects Order/Chaos<size>, then Pos<index + 1>.
                        var key = InibinReader.KeyHash($"{team}{nativeSize}*Pos{create[18] + 1}");
                        TestAssert.True(offsets.TryGetValue(key, out var offset), "native-selected spawn section exists");
                        var position = offset.AsClientVector();
                        TestAssert.Equal(3, position.Count);
                        TestAssert.Near(anchor.X + position[0], hero.Position.X, 1e-6);
                        TestAssert.Near(anchor.Z + position[2], hero.Position.Z, 1e-6);
                    }
                }
            }
        }
    }

    [Check("session.playtest-b4-shipped-spawn-table")]
    public static void ShippedSpawnTable()
    {
        // Independent extraction of HeroSpawnOffsets.inibin, all Pos/Facing rows.
        // Order1*Pos1 is stored as the byte vector (100,0,100); the stock client reads byte vectors divided by
        // ten (the client's byte-vector reader), so a lone Order hero stands 10 units from the fountain centre.
        (float X, float Z, int Degrees)[][] expected =
        [
            [(10, 10, -40)],
            [(-100, 120, 45), (120, -100, -135)],
            [(-96.1f, -96.1f, -45), (154.9f, -28.8f, -165), (-28.8f, 154.9f, 60)],
            [(120, 120, 135), (120, -100, -135), (-100, -100, -45), (-100, 120, 45)],
            [(166.4f, 120, 144), (166.4f, -100, -144), (-42.8f, -168, -72), (-172.2f, 10, 0), (-82.8f, 188, 72)],
            [(-10, 0, 135)],
            [(-120, 110, 45), (100, -110, -135)],
            [(-116.1f, -106.1f, -45), (134.9f, -38.8f, -165), (-48.8f, 144.9f, 80)],
            [(100, 110, 135), (100, -110, -135), (-120, -110, -45), (-120, 110, 45)],
            [(146.4f, 120, 144), (146.4f, -100, -144), (-62.8f, -168, -72), (-192.2f, 10, 0), (-102.8f, 188, 72)],
        ];
        for (var row = 0; row < expected.Length; row++)
        {
            var team = row < 5 ? Team.Order : Team.Chaos;
            var anchor = row < 5 ? new Position2(25.902751922607422, 263.5519104003906) : new Position2(13933.7890625, 14170.09375);
            var size = row % 5 + 1;
            var participants = Enumerable.Range(0, size).Select(index => new SessionParticipant(
                new SessionParticipantDefinition((ulong)index + 1, (uint)index, $"Seat{index}", "Annie", team))).ToArray();
            var service = new SessionSpawnService(new WorldState(), GameData.Default,
                CreateHeroStats(GameData.Default), new ReplicationSyncCounter());
            var heroes = service.CreateHeroes(participants);
            for (var slot = 0; slot < size; slot++)
            {
                var entry = expected[row][slot];
                TestAssert.Equal(new Position2(anchor.X + entry.X, anchor.Z + entry.Z), heroes[slot].Position);
                TestAssert.Near(entry.Degrees, Math.Atan2(-heroes[slot].Facing.Z, heroes[slot].Facing.X) * 180 / Math.PI, 1e-10);
                TestAssert.Equal((byte)slot, participants[slot].SpawnPositionIndex);
            }
        }
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MapDefinitions.InitialHeroSpawn(Team.Order, 0, 0));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MapDefinitions.InitialHeroSpawn(Team.Order, 6, 0));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MapDefinitions.InitialHeroSpawn(Team.Order, 2, 2));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MapDefinitions.InitialHeroSpawn(Team.Order, 2, -1));
    }

    /// <summary>
    /// Batch 462: the client builds each hero's stats collection from <c>Data\Globals\&lt;mode&gt;_Stats.ini</c>, the mode string the
    /// synch version carried (the client copies the mode from the synch version's field), and reads a 0x4B through that collection
    /// in name order. Map 8 sends "ODIN": the CLASSIC-shaped 0x4B made the owner's client log "malformed buffer, 240 vs 244" and assert
    /// in the stats buffer's string reader (28 October 2026).
    /// </summary>
    [Check("session.dominion-hero-stats-follow-the-mode-collection")]
    public static void HeroStatsFollowTheModeCollection()
    {
        foreach (var (mapId, mode, fields) in new[] { (GameMaps.Normal, "CLASSIC", 58), (GameMaps.CrystalScar, "ODIN", 61) })
        {
            var session = NewSession(mapId: mapId);
            session.Authenticate(11, session.BuildKeyCheck(1));
            session.Authenticate(12, session.BuildKeyCheck(2));
            var burst = session.Spawn(11);
            var hero = session.Participants.Single(participant => participant.ConnectionId == 11).Hero!;
            var packet = burst.Single(item => item.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Plaintext.Span[1..5]) == hero.NetId);
            var values = ClassicHeroStatsChecks.DecodeClient(packet.Plaintext.Span, mode);
            TestAssert.Equal(fields, values.Count, $"map {mapId}: the {mode} collection");
            TestAssert.Equal(hero.Name, (string)values["NAME"], $"map {mapId}: NAME");
            TestAssert.Equal(TeamMapping.ToNative(hero.Team), (int)values["TEAM"], $"map {mapId}: TEAM");
            TestAssert.Equal(mode == "ODIN", values.ContainsKey("NODE_CAPTURE"), $"map {mapId}: NODE_CAPTURE");
            TestAssert.Equal(mode == "CLASSIC", values.ContainsKey("TURRETS_KILLED"), $"map {mapId}: TURRETS_KILLED");
        }
    }

    [Check("session.solo-hero-spawns-on-the-client-beam")]
    public static void SoloHeroSpawnsOnTheClientBeam()
    {
        // Owner playtest 16 October 2026 (Sivir, one player, HUD 55 s): "with only one player on the team the
        // spawnpoint should be in middle of fountain as the light beams indicate". The beam (the spawn point object)
        // and the client's own CreateHero position (0x4F handler) both stand on the team's spawn
        // point plus HeroSpawnOffsets "Order<size>*Pos<slot+1>", size = roster count + StartSpawn
        // bots. Order1*Pos1 is a byte vector (100,0,100) that the client reads divided by ten; the server
        // used the raw bytes and put the lone hero 127 units from the beam.
        var offsets = InibinReader.LoadData("Characters/HeroSpawnOffsets.inibin");
        var stored = offsets[InibinReader.KeyHash("Order1*Pos1")];
        TestAssert.Equal(InibinValueKind.ByteVector, stored.Kind, "Order1*Pos1 is the one byte-vector position");
        TestAssert.Equal(new[] { 100.0, 0.0, 100.0 }, stored.AsVector().ToArray(), "raw bytes");
        var beam = stored.AsClientVector();
        TestAssert.Near(10.0, beam[0], 1e-6, "client x offset");
        TestAssert.Near(10.0, beam[2], 1e-6, "client z offset");

        var roster = new[] { new SessionParticipantDefinition(1, 0, "Solo", "Sivir", Team.Order) };
        var session = NewSession(roster: roster);
        session.World.Clock.SetVirtual();
        session.CreateWorld();
        var hero = session.Participants.Single().Hero!;
        TestAssert.Near(25.902751922607422 + beam[0], hero.Position.X, 1e-6, "hero x is the beam x");
        TestAssert.Near(263.5519104003906 + beam[2], hero.Position.Z, 1e-6, "hero z is the beam z");
        // The Order spawn point (__Spawn_T1.SCB central point) is (25.9, 263.55): the beam is on the fountain middle.
        var spawnPoint = new Position2(25.902751922607422, 263.5519104003906);
        TestAssert.True(Math.Sqrt(Math.Pow(hero.Position.X - spawnPoint.X, 2) + Math.Pow(hero.Position.Z - spawnPoint.Z, 2)) < 15.0,
            $"the lone Order hero stands in the middle of the fountain ({hero.Position})");
    }

    [Check("session.keycheck")]
    public static void KeyCheck()
    {
        var cipher = LegacyBlowfish.CreateDefault();
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SessionKeyCheck.Build(0, cipher));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SessionKeyCheck.Build(ulong.MaxValue, cipher));
        var keyCheck = SessionKeyCheck.Build(0x0102_0304_0506_0708UL, cipher);
        TestAssert.Equal(SessionKeyCheck.Length, keyCheck.Length);
        TestAssert.True(SessionKeyCheck.TryReadPlayerId(keyCheck, cipher, out var playerId));
        TestAssert.Equal(0x0102_0304_0506_0708UL, playerId);

        keyCheck[0] = 1;
        TestAssert.True(SessionKeyCheck.TryReadPlayerId(keyCheck, cipher, out _),
            "the first key-check word is an opaque native token");
        keyCheck[^1] ^= 0x80;
        TestAssert.False(SessionKeyCheck.TryReadPlayerId(keyCheck, cipher, out _));

        var session = NewSession(orderChampion: "Annie", chaosChampion: "Orianna");
        TestAssert.True(session.Authenticate(1, session.BuildKeyCheck(1)) is not null);
        TestAssert.True(session.Authenticate(1, session.BuildKeyCheck(2)) is null,
            "one native connection cannot claim another seat");
        TestAssert.True(session.Authenticate(2, session.BuildKeyCheck(2)) is not null);
        TestAssert.True(session.Authenticate(3, session.BuildKeyCheck(3)) is null,
            "unsupported player IDs cannot reserve a seat");
    }


    [Check("session.readiness-and-spawn")]
    public static void ReadinessAndSpawn()
    {
        var session = NewSession(orderChampion: "Annie", chaosChampion: "Orianna");
        var order = session.Authenticate(11, session.BuildKeyCheck(1))!;
        var chaos = session.Authenticate(12, session.BuildKeyCheck(2))!;
        TestAssert.False(session.Start());
        var orderPackets = session.Spawn(11);
        TestAssert.True(order.Spawned);
        TestAssert.True(orderPackets.Any(packet => packet.Plaintext.Span[0] == 0x4F));
        TestAssert.True(session.MarkReady(11));
        TestAssert.False(session.Start());
        session.Spawn(12);
        session.MarkReady(12);
        TestAssert.True(session.CanStart);
        var mapPackets = session.BuildMapReadyPackets(11);
        session.BuildMapReadyPackets(12);
        TestAssert.True(mapPackets.Any(packet => packet.Plaintext.Span[0] == 0x12));
        TestAssert.True(session.Start());
        TestAssert.True(session.Authenticate(13, session.BuildKeyCheck(1)) is null,
            "another connection cannot claim a seat that is still connected");

        var hero = chaos.Hero!;
        hero.Position = new Position2(5000.0, 5000.0);
        var disconnectPackets = session.DisconnectPackets(12);
        TestAssert.Equal(2, disconnectPackets.Count);
        TestAssert.True(hero.Alive);
        TestAssert.Equal(0, hero.Waypoints.Count);
        session.RunSchedulers();
        TestAssert.Equal(0, hero.Waypoints.Count, "a detached hero stays where it stands: no automatic fountain walk");
        TestAssert.False(session.Disconnect(12));
    }

    [Check("session.initial-spell-rank-hydration")]
    public static void InitialSpellRankHydration()
    {
        var world = new WorldState();
        var participant = new SessionParticipant(new SessionParticipantDefinition(
            1, 0, "Vayne", "Vayne", Team.Order));
        var service = new SessionSpawnService(world, GameData.Default,
            CreateHeroStats(GameData.Default), new ReplicationSyncCounter());
        service.CreateHeroes(new[] { participant });

        var initial = service.BuildHeroSpawnBurst(participant, new[] { participant },
            includeStartSpawn: false);
        var initialRanks = initial.Where(packet => packet.Plaintext.Span[0] == 0x18).ToArray();
        TestAssert.Equal(4, initialRanks.Length, "local champion slots receive rank state");
        var initialR = initialRanks.Single(packet => packet.Plaintext.Span[5] == 3).Plaintext.Span;
        TestAssert.Equal((byte)0, initialR[6], "fresh Vayne R remains unlearned in native book");
        TestAssert.Equal((byte)1, initialR[7], "fresh Vayne keeps its available skill point");

        participant.Hero!.Level = 6;
        participant.Hero.SpellRanks[3] = 1;
        participant.Hero.SkillPoints = 0;
        var trained = service.BuildHeroSpawnBurst(participant, new[] { participant },
            includeStartSpawn: false);
        var trainedR = trained.Where(packet => packet.Plaintext.Span[0] == 0x18)
            .Single(packet => packet.Plaintext.Span[5] == 3).Plaintext.Span;
        TestAssert.Equal((byte)1, trainedR[6], "trained Vayne R rank reaches native book");
        TestAssert.Equal((byte)0, trainedR[7], "training consumes the available point");
    }


    private static IReadOnlyList<PlaintextPacket> NormalizeArchivedAvatarOrder(
        IReadOnlyList<PlaintextPacket> actual, IReadOnlyList<JsonElement> expected)
    {
        var expectedOps = expected.Select(packet =>
            Convert.FromHexString(packet.GetProperty("plaintext").GetString()!)[0]).ToArray();
        var expectedAvatar = Array.IndexOf(expectedOps, (byte)0x2C);
        var expectedStats = Array.IndexOf(expectedOps, (byte)0xCC);
        var actualAvatar = actual.ToList().FindIndex(packet => packet.Plaintext.Span[0] == 0x2C);
        var actualStats = actual.ToList().FindIndex(packet => packet.Plaintext.Span[0] == 0xCC);
        if (expectedAvatar < 0 || expectedStats < 0 || actualAvatar < 0 || actualStats < 0 ||
            expectedStats >= expectedAvatar || actualAvatar >= actualStats)
            return actual;

        // The production burst now initializes AvatarInfo before the first
        // effective-stat 0xCC.  Keep the historical wire corpus useful by
        // moving only this known adjacent semantic pair into its archived
        // order; every packet byte/channel/flag remains asserted below.
        var normalized = actual.ToList();
        var avatar = normalized[actualAvatar];
        normalized.RemoveAt(actualAvatar);
        normalized.Insert(expectedAvatar, avatar);
        return normalized;
    }

    [Check("session.start-camera-per-player")]
    public static void StartCameraPerPlayer()
    {
        var session = NewSession();
        foreach (var id in new[] { 11L, 12L })
        {
            session.Authenticate(id, session.BuildKeyCheck((ulong)(id - 10)));
            session.Spawn(id); session.MarkReady(id);
        }
        foreach (var participant in session.Participants)
        {
            var hero = participant.Hero!;
            var packets = session.BuildMapReadyPackets(participant.ConnectionId!.Value).ToList();
            var focus = packets.Single(packet => packet.Plaintext.Span[0] == 0x27);
            var start = packets.Single(packet => packet.Plaintext.Span[0] == 0x5F);
            TestAssert.True(packets.IndexOf(start) < packets.IndexOf(focus), "focus follows StartGame");
            var bytes = focus.Plaintext.Span;
            TestAssert.Equal(34, bytes.Length, "native MoveCameraToPoint layout");
            TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "recipient's own hero");
            TestAssert.Equal((byte)1, bytes[5], "start at current camera");
            TestAssert.Near(hero.Position.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]), .001, "camera destination X");
            TestAssert.Near(hero.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[26..]), .001, "camera destination Z");
            TestAssert.Near(.001, BinaryPrimitives.ReadSingleLittleEndian(bytes[30..]), 1e-9,
                "one-frame focus uses positive duration: native divides distance by travel time");
            TestAssert.Equal((byte)3, focus.Channel);
            TestAssert.Equal(LeaguePacketReliability.Reliable, focus.Reliability);
        }
    }


    private static void ReplayDriverCase(JsonElement expectedCase)
    {
        var driver = CreateDriverSession();
        var session = driver.Session;
        var nativeReadySeats = new HashSet<int>();
        session.World.Clock.SetVirtual();
        var cipher = LegacyBlowfish.CreateDefault();
        foreach (var expectedEvent in expectedCase.GetProperty("events").EnumerateArray())
        {
            var step = expectedEvent.GetProperty("step");
            session.World.Clock.SetVirtual(step.GetProperty("time").GetDouble());
            // The preserved Python corpus predates the stock native C6-ready
            // boundary. The stock client sends that frame after its map load and
            // the hero burst waits for it, so add it when a recorded spawn delay
            // falls due: the burst then lands on its recorded step and every
            // original event and packet comparison stays unchanged.
            if (step.GetProperty("op").GetString() == "schedule")
                for (var seat = 0; seat < session.Participants.Count; seat++)
                    if (session.Participants[seat] is { Loaded: false, SpawnAt: double due } &&
                        session.World.Clock.Monotonic() + 1e-9 >= due)
                        TestAssert.Equal(0, session.Receive(11L + seat, 1, [0xC6, 0, 0, 0, 0]).Count);
            var actualPackets = driver.Step(step);
            // A seat is made ready on the scheduler pass after its burst; run it
            // (and the duplicate frame, which does nothing) at each recorded first spawn.
            var nativeReadyInjected = false;
            for (var seat = 0; seat < session.Participants.Count; seat++)
            {
                if (!session.Participants[seat].Spawned || nativeReadySeats.Contains(seat)) continue;
                TestAssert.Equal(0, session.Receive(11L + seat, 1, [0xC6, 0, 0, 0, 0]).Count);
                nativeReadySeats.Add(seat);
                nativeReadyInjected = true;
            }
            if (nativeReadyInjected)
                TestAssert.Equal(0, session.RunSchedulers().Count,
                    "the recorded two-second settle still controls map output");
            AssertDriverState(expectedEvent.GetProperty("state"), session,
                expectedCase.GetProperty("name").GetString()! + " t=" + step.GetProperty("time").GetDouble());
            AssertDriverPackets(expectedEvent.GetProperty("packets"), actualPackets,
                cipher, expectedCase.GetProperty("name").GetString()! + " t=" + step.GetProperty("time").GetDouble(), session);
        }
    }

    private static DriverSession CreateDriverSession()
    {
        var data = GameData.Default;
        var world = new WorldState();
        var navigation = NavigationGrid.LoadDefault();
        var syncIds = new ReplicationSyncCounter();
        var stats = CreateHeroStats(data);
        var replication = new ProtocolReplicationServices(
            (entity, observer) => DriverEntityCreation(entity),
            (entity, observer) => DriverEntityStats(entity, stats, syncIds),
            static (entity, observer) => Array.Empty<PlaintextPacket>(),
            static (ball, observer) => Array.Empty<PlaintextPacket>(),
            syncIds.Next,
            new NavigationBounds(navigation.MinX, navigation.MaxX, navigation.MinZ, navigation.MaxZ));
        var delivery = new ReplicationDelivery(
            world,
            replication,
            new NavigationGridVisionEnvironment(navigation),
            static team => Array.Empty<VisionProvider>(),
            static (team, point) => false);
        var movement = new MovementSystem(world, navigation);
        var session = new DuelSession(
            new SessionDependencies(
                stats,
                syncIds,
                new MovementSessionAdapter(movement),
                new ProtocolProtectionSessionAdapter(world, delivery, syncIds),
                new EffectSessionAdapter()),
            world: world,
            orderChampion: "Annie",
            chaosChampion: "Orianna",
            gameData: data,
            readySettleSeconds: 2.0);
        return new DriverSession(session, delivery);
    }

    private static IReadOnlyList<PlaintextPacket> DriverEntityCreation(Entity entity)
    {
        if (entity.UnitType == UnitType.Turret)
            return new[] { SpawnPackets.CreateTurret(entity.NetId, SessionSpawnService.MapNetNodeId,
                entity.Name + "_A") };
        if (entity.UnitType == UnitType.Hero)
            return new[] { SpawnPackets.CreateHero(entity.NetId, 0, entity.Name,
                entity.ChampionName, entity.Team == Team.Order, SessionSpawnService.MapNetNodeId) };
        return Array.Empty<PlaintextPacket>();
    }

    private static IReadOnlyList<PlaintextPacket> DriverEntityStats(
        Entity entity, HeroStatsService stats, ReplicationSyncCounter syncIds)
    {
        if (entity.UnitType == UnitType.Turret)
        {
            var packets = new List<PlaintextPacket>
            {
                CombatPackets.TurretStats(entity.NetId,
                    new TurretStatsSnapshot(ToFloat(entity.Health), ToFloat(entity.AttackDamage),
                        ToFloat(entity.MaxHealth), ToFloat(entity.Armor), ToFloat(entity.SpellBlock),
                        ToFloat(entity.HealthRegen), entity.Protected, ToFloat(entity.AttackSpeedMod)), syncIds.Next(entity.NetId)),
            };
            if (!entity.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase))
                packets.Add(VisionPackets.EnterLocalVisibility(entity.NetId,
                    ToFloat(entity.MaxHealth), ToFloat(entity.Health)));
            return packets;
        }
        if (entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus)
            return new[] { CombatPackets.BuildingStats(entity.NetId,
                new BuildingStatsSnapshot(ToFloat(entity.Health), CombatPackets.SFlagTargetableToAll),
                syncIds.Next(entity.NetId)) };
        if (entity.UnitType == UnitType.Hero)
            return new[] { CombatPackets.HeroStats(stats.Snapshot(entity), syncIds.Next(entity.NetId)) };
        return Array.Empty<PlaintextPacket>();
    }

    private static float ToFloat(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }

    private sealed class DriverSession
    {
        private readonly ReplicationDelivery _delivery;
        private readonly Dictionary<long, DriverCaptureTransport> _captures = new();
        private bool _observersAttached;

        public DriverSession(DuelSession session, ReplicationDelivery delivery)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        }

        public DuelSession Session { get; }

        public IReadOnlyList<SessionOutboundPacket> Step(JsonElement step)
        {
            var wasAttached = _observersAttached;
            var op = step.GetProperty("op").GetString();
            var direct = op switch
            {
                "receive" => Receive(step),
                "schedule" => Session.RunSchedulers(),
                _ => throw new InvalidOperationException("Unknown session driver op: " + op),
            };

            if (!_observersAttached && Session.Participants.All(participant => participant.Spawned))
                AttachObservers();
            if (wasAttached && op == "schedule" && Session.Started)
                _delivery.Flush();
            return Merge(direct);
        }

        private IReadOnlyList<SessionOutboundPacket> Receive(JsonElement step)
        {
            var seat = step.GetProperty("seat").GetInt32();
            var connectionId = 11L + seat;
            var data = Convert.FromHexString(step.GetProperty("data").GetString()!);
            return Session.Receive(connectionId, step.GetProperty("channel").GetByte(), data);
        }

        private void AttachObservers()
        {
            foreach (var participant in Session.Participants)
            {
                if (participant.ConnectionId is not long connectionId || participant.Hero is not { } hero)
                    throw new InvalidOperationException("Cannot attach a driver observer before authentication and spawn.");
                var capture = new DriverCaptureTransport();
                _captures.Add(connectionId, capture);
                _delivery.AddObserver(new ReplicationObserver(
                    (ulong)connectionId, participant.Team, hero.NetId, capture,
                    ready: true, view: participant.View));
            }
            _observersAttached = true;
        }

        private IReadOnlyList<SessionOutboundPacket> Merge(
            IReadOnlyList<SessionOutboundPacket> direct)
        {
            var result = new List<SessionOutboundPacket>();
            foreach (var participant in Session.Participants)
            {
                if (participant.ConnectionId is not long connectionId) continue;
                result.AddRange(direct.Where(packet => packet.ConnectionId == connectionId));
                if (_captures.TryGetValue(connectionId, out var capture))
                {
                    result.AddRange(capture.Packets.Select(packet =>
                        new SessionOutboundPacket(connectionId, packet)));
                    capture.Clear();
                }
            }
            return result;
        }
    }

    private sealed class DriverCaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();
        public IReadOnlyList<PlaintextPacket> Packets => _packets;
        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
    }

    private static void AssertDriverState(JsonElement expected, DuelSession session, string name)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, new
            {
                started = session.Started, worldCreated = session.WorldCreated,
                clockAt = session.ClockAt, protectAt = session.ProtectAt,
                hudOrigin = session.HudOrigin, entityCount = session.World.Entities.Count,
                turretRebindDone = session.TurretRebindDone,
                announcements = session.Announcements.Select(item => new object[] { item.EventId, item.HudTime }).ToArray(),
                players = session.Participants.Select(player => new
                {
                    playerId = player.PlayerId, clientId = player.ClientId, connected = player.ConnectionId.HasValue,
                    spawned = player.Spawned, ready = player.Ready, loaded = player.Loaded, shopOpen = player.ShopOpen,
                    spawnAt = player.SpawnAt, readyAt = player.ReadyAt, hero = player.Hero?.NetId ?? 0u,
                    known = player.View.Known.OrderBy(id => id).ToArray(), visible = player.View.Visible.OrderBy(id => id).ToArray(),
                }).ToArray(),
            });
            return;
        }
        TestAssert.Equal(expected.GetProperty("started").GetBoolean(), session.Started, name + " started");
        TestAssert.Equal(expected.GetProperty("worldCreated").GetBoolean(), session.WorldCreated,
            name + " worldCreated");
        TestOptionalNear(expected.GetProperty("clockAt"), session.ClockAt, name + " clockAt");
        TestOptionalNear(expected.GetProperty("protectAt"), session.ProtectAt, name + " protectAt");
        TestOptionalNear(expected.GetProperty("hudOrigin"), session.HudOrigin, name + " hudOrigin");
        TestAssert.Equal(expected.GetProperty("entityCount").GetInt32(), session.World.Entities.Count,
            name + " entity count");
        TestAssert.Equal(expected.GetProperty("turretRebindDone").GetBoolean(), session.TurretRebindDone,
            name + " turret rebind");

        var expectedAnnouncements = expected.GetProperty("announcements").EnumerateArray()
            .Select(item => (EventId: item[0].GetByte(), HudTime: item[1].GetDouble())).ToArray();
        TestAssert.Equal(expectedAnnouncements.Length, session.Announcements.Count,
            name + " announcement count");
        for (var i = 0; i < expectedAnnouncements.Length; i++)
        {
            TestAssert.Equal(expectedAnnouncements[i].EventId, session.Announcements[i].EventId,
                name + " announcement id");
            TestAssert.Near(expectedAnnouncements[i].HudTime, session.Announcements[i].HudTime,
                1e-9, name + " announcement time");
        }

        var players = expected.GetProperty("players").EnumerateArray().ToArray();
        TestAssert.Equal(players.Length, session.Participants.Count, name + " player count");
        for (var i = 0; i < players.Length; i++)
        {
            var row = players[i];
            var actual = session.Participants[i];
            TestAssert.Equal(row.GetProperty("playerId").GetUInt64(), actual.PlayerId, name + " player id");
            TestAssert.Equal(row.GetProperty("clientId").GetUInt32(), actual.ClientId, name + " client id");
            TestAssert.Equal(row.GetProperty("connected").GetBoolean(), actual.ConnectionId.HasValue,
                name + " connected");
            TestAssert.Equal(row.GetProperty("spawned").GetBoolean(), actual.Spawned, name + " spawned");
            TestAssert.Equal(row.GetProperty("ready").GetBoolean(), actual.Ready, name + " ready");
            TestAssert.Equal(row.GetProperty("loaded").GetBoolean(),
                actual.Loaded, name + " native loaded");
            TestAssert.Equal(row.GetProperty("shopOpen").GetBoolean(), actual.ShopOpen, name + " shop");
            TestOptionalNear(row.GetProperty("spawnAt"), actual.SpawnAt, name + " spawnAt");
            TestOptionalNear(row.GetProperty("readyAt"), actual.ReadyAt, name + " readyAt");
            var heroId = actual.Hero?.NetId ?? 0u;
            TestAssert.Equal(row.GetProperty("hero").GetUInt32(), heroId, name + " hero");
            var known = row.GetProperty("known").EnumerateArray().Select(item => item.GetUInt32()).ToArray();
            var visible = row.GetProperty("visible").EnumerateArray().Select(item => item.GetUInt32()).ToArray();
            var actualKnown = actual.View.Known.OrderBy(id => id).ToArray();
            var actualVisible = actual.View.Visible.OrderBy(id => id).ToArray();
            TestAssert.Equal(known, actualKnown, name + " known expected=" + string.Join(',', known) +
                " actual=" + string.Join(',', actualKnown));
            TestAssert.Equal(visible, actualVisible, name + " visible expected=" + string.Join(',', visible) +
                " actual=" + string.Join(',', actualVisible));
        }
    }

    private static void TestOptionalNear(JsonElement expected, double? actual, string name)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual is null, name + " expected null");
        else
            TestAssert.Near(expected.GetDouble(), actual ?? double.NaN, 1e-9, name);
    }

    private static void AssertDriverPackets(JsonElement expected,
        IReadOnlyList<SessionOutboundPacket> actual, LegacyBlowfishCipher cipher, string name, DuelSession session)
    {
        var spawnConnections = actual.Where(item => item.Packet.Plaintext.Span[0] == 0x4F)
            .Select(item => item.ConnectionId).Distinct().ToArray();
        var classic = actual.Where(item => item.Packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode).ToArray();
        TestAssert.Equal(spawnConnections.Length * session.Participants.Count(p => p.Hero is not null),
            classic.Length, name + " owner/public Classic snapshot count");
        foreach (var connection in spawnConnections)
        {
            var recipient = session.Participants.Single(p => p.ConnectionId == connection);
            TestAssert.Equal(1, classic.Count(item => item.ConnectionId == connection &&
                item.Packet.EntityReferences.Single().NetId == recipient.Hero!.NetId), name + " own Classic snapshot");
        }
        var starts = actual.Count(item => item.Packet.Plaintext.Span[0] == 0x5F);
        if (starts > 0)
            AssertDriverFountainProtection(actual, session, name,
                starts * (session.MapEntities.Count(e => e.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase)) + 1));
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.GroupBy(item => item.ConnectionId).Select(group => new
            {
                seat = group.Key - 11L,
                packets = group.Select(item => PacketSnapshot(item.Packet, cipher)).ToArray(),
            }).ToArray());
            return;
        }
        var expectedGroups = expected.EnumerateArray().ToArray();
        TestAssert.Equal(expectedGroups.Sum(group => group.GetProperty("packets").GetArrayLength()), actual.Count, name + " packet count");
        var index = 0;
        foreach (var group in expectedGroups)
        foreach (var row in group.GetProperty("packets").EnumerateArray())
        {
            var output = actual[index++];
            TestAssert.Equal(11L + group.GetProperty("seat").GetInt32(), output.ConnectionId, name + " connection");
            TestAssert.Equal(row.GetProperty("channel").GetByte(), output.Packet.Channel, name + " channel");
            TestAssert.Equal(row.GetProperty("flags").GetByte(), (byte)output.Packet.Reliability, name + " flags");
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("plaintext").GetString()!), output.Packet.Plaintext.ToArray(), name + " plaintext " + index);
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("wire").GetString()!), output.Packet.ToWireBytes(cipher), name + " wire " + index);
        }
    }

    private static object PacketSnapshot(PlaintextPacket packet, LegacyBlowfishCipher cipher) => new
    {
        channel = packet.Channel, flags = (byte)packet.Reliability,
        plaintext = Convert.ToHexString(packet.Plaintext.Span).ToLowerInvariant(),
        wire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant(),
    };

    private static void AssertMapFountainProtection(
        IReadOnlyList<PlaintextPacket> actual, IReadOnlyList<Entity> mapEntities,
        uint expectedSyncId, string name)
    {
        var fountainIds = mapEntities
            .Where(entity => entity.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase))
            .Select(entity => entity.NetId)
            .ToArray();
        var candidates = actual.Select((packet, index) => (packet, index))
            .Where(item => IsFountainProtection(item.packet, fountainIds))
            .ToArray();
        TestAssert.Equal(fountainIds.Length, candidates.Length,
            name + " fountain protection packet count");
        TestAssert.Equal(fountainIds,
            candidates.Select(item => FountainId(item.packet)).ToArray(),
            name + " fountain protection entity order");
        var mapTurrets = mapEntities.Where(entity => entity.UnitType == UnitType.Turret).ToArray();
        var expectedIndexes = new List<int>(fountainIds.Length);
        var cursor = mapTurrets.Length;
        foreach (var turret in mapTurrets)
        {
            if (turret.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase))
                expectedIndexes.Add(cursor++);
            else
                cursor += 2;
        }
        TestAssert.Equal(expectedIndexes.ToArray(), candidates.Select(item => item.index).ToArray(),
            name + " fountain protection position");
        foreach (var candidate in candidates)
            AssertFountainProtection(candidate.packet, FountainId(candidate.packet), expectedSyncId,
                name + " fountain protection", statBlock: true);


    }

    private static void AssertDriverFountainProtection(
        IReadOnlyList<SessionOutboundPacket> actual, DuelSession session, string name,
        int expectedCount)
    {
        var fountainIds = session.MapEntities
            .Where(entity => entity.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase))
            .Select(entity => entity.NetId)
            .ToArray();
        var candidates = actual.Select((output, index) => (output, index))
            .Where(item => IsFountainProtection(item.output.Packet, fountainIds))
            .ToArray();
        if (expectedCount == 0)
        {
            // Later archived events retain their pre-existing shrine rows. They
            // are part of the strict corpus comparison and are adapted below;
            // only the newly inserted startup rows are checked here.
            return;
        }
        TestAssert.Equal(expectedCount, candidates.Length,
            name + $" fountain protection packet count: expected={expectedCount}, actual={candidates.Length}; " +
            string.Join("; ", candidates.Select(item => $"connection={item.output.ConnectionId}, index={item.index}, " +
                $"entity={FountainId(item.output.Packet):X8}, bytes={Convert.ToHexString(item.output.Packet.Plaintext.Span)}")));

        var participants = session.Participants
            .Where(participant => participant.ConnectionId is not null)
            .OrderBy(participant => participant.ConnectionId)
            .ToArray();
        TestAssert.Equal(2, participants.Length, name + " fountain protection observers");
        TestAssert.Equal(expectedCount, participants.Length * (fountainIds.Length + 1),
            name + " fountain protection expected sequence");
        var mapTurrets = session.MapEntities.Where(entity => entity.UnitType == UnitType.Turret).ToArray();
        var fountainOffsets = new List<int>(fountainIds.Length);
        var mapCursor = mapTurrets.Length;
        foreach (var turret in mapTurrets)
        {
            if (turret.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase))
                fountainOffsets.Add(mapCursor++);
            else
                mapCursor += 2;
        }
        var expected = new List<(long ConnectionId, uint FountainId, uint SyncId, bool Direct)>(expectedCount);
        for (var participantIndex = 0; participantIndex < participants.Length; participantIndex++)
        {
            var participant = participants[participantIndex];
            // DriverSession.Merge emits each participant's direct packets and
            // then that participant's observer capture before moving to the
            // next participant. The startup rows therefore interleave per
            // connection: direct fountain rows, followed by that observer's
            // own-shrine reveal. The fixed map burst seeds both shrine
            // protections; the first visibility flush then reveals the
            // observer's allied shrine through the ordinary C2/EE path.
            foreach (var fountainId in fountainIds)
                expected.Add((participant.ConnectionId!.Value, fountainId,
                    (uint)(participantIndex + 1), true));
            var ownFountain = session.MapEntities.Single(entity =>
                entity.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase) &&
                entity.Team == participant.Team);
            // ProtocolReplicationServices deliberately suppresses shrine
            // stats before BuildReveal, so after both direct map passes the
            // own-shrine protection is the next per-unit sync (3 here).
            expected.Add((participant.ConnectionId!.Value, ownFountain.NetId,
                checked((uint)(participants.Length + 1)), false));
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var expectedPacket = expected[index];
            var candidate = candidates[index];
            TestAssert.Equal(expectedPacket.ConnectionId, candidate.output.ConnectionId,
                name + " fountain protection connection " + index);
            TestAssert.Equal(expectedPacket.FountainId, FountainId(candidate.output.Packet),
                name + " fountain protection entity " + index);
            if (expectedPacket.Direct)
            {
                var mapStart = actual.Select((output, packetIndex) => (output, packetIndex))
                    .First(item => item.output.ConnectionId == expectedPacket.ConnectionId &&
                        item.output.Packet.Plaintext.Span[0] == 0xA5).packetIndex;
                TestAssert.Equal(mapStart + fountainOffsets[index % (fountainIds.Length + 1)], candidate.index,
                    name + " direct fountain protection position " + index);
                TestAssert.Equal((byte)0xB5, OpcodeAt(actual, candidate.index - 1),
                    name + " direct fountain protection predecessor " + index);
                var isLastFountain = expectedPacket.FountainId == fountainIds[^1];
                TestAssert.Equal(isLastFountain ? (byte)0x12 : (byte)0xCC,
                    OpcodeAt(actual, candidate.index + 1),
                    name + " direct fountain protection successor " + index);
            }
            else
            {
                TestAssert.Equal((byte)0xC2, OpcodeAt(actual, candidate.index - 2),
                    name + " reveal fountain protection C2 predecessor " + index);
                TestAssert.Equal((byte)0xEE, OpcodeAt(actual, candidate.index - 1),
                    name + " reveal fountain protection EE predecessor " + index);
            }
            AssertFountainProtection(candidate.output.Packet, expectedPacket.FountainId,
                expectedPacket.SyncId,
                name + $" fountain protection {index} connection={expectedPacket.ConnectionId} " +
                $"entity=0x{expectedPacket.FountainId:X8} direct={expectedPacket.Direct}",
                statBlock: expectedPacket.Direct);
        }


    }

    private static bool TryAdaptArchivedFountainProtection(
        byte[] expectedPlain, IReadOnlyCollection<uint> fountainIds, int connectedCount,
        out byte[] adapted)
    {
        adapted = expectedPlain;
        if (expectedPlain.Length != 32 || expectedPlain[0] != 0xCC || expectedPlain[^1] != 0 ||
            !fountainIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(11, 4))) ||
            BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(5, 4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(15, 4)) != 0x7000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(19, 4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(23, 4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(expectedPlain.AsSpan(27, 4)) != 0x01000000)
            return false;

        // The archived row has one legacy trailer byte. The current protection
        // writer emits the 31-byte contract; retain every preceding field while
        // applying the current shared fountain state and sync progression.
        adapted = expectedPlain[..^1].ToArray();
        var nextSync = checked(1u + (uint)(connectedCount + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(adapted.AsSpan(5, 4), nextSync);
        BinaryPrimitives.WriteUInt32LittleEndian(adapted.AsSpan(23, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(adapted.AsSpan(27, 4), CombatPackets.SFlagNontargetableAll);
        return true;
    }

    private static byte OpcodeAt(IReadOnlyList<SessionOutboundPacket> packets, int index)
    {
        TestAssert.True(index >= 0 && index < packets.Count, "fountain protection neighbor index");
        return packets[index].Packet.Plaintext.Span[0];
    }

    private static bool IsFountainProtection(PlaintextPacket packet,
        IReadOnlyCollection<uint> fountainIds)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 15 && bytes[0] == 0xCC &&
            fountainIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..15]));
    }

    private static uint FountainId(PlaintextPacket packet)
    {
        var bytes = packet.Plaintext.Span;
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..15]);
    }

    /// <summary>
    /// A shrine's protection state as the client receives it. The map burst gives it the lane towers' 91-byte <c>TurretStats</c> block
    /// (<paramref name="statBlock"/>: Damage first, then Protected/Targetable/flags at 67/71/75), a reveal the 31-byte flags-only packet.
    /// </summary>
    private static void AssertFountainProtection(PlaintextPacket packet,
        uint fountainId, uint expectedSyncId, string name, bool statBlock = false)
    {
        var bytes = packet.Plaintext.Span;
        if (statBlock)
        {
            TestAssert.Equal(CombatPackets.CombatChannel, packet.Channel, name + " channel");
            TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability, name + " reliability");
            TestAssert.Equal(91, bytes.Length, name + " length");
            TestAssert.Equal((byte)0xCC, bytes[0], name + " opcode");
            TestAssert.Equal(expectedSyncId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..9]), name + " sync");
            TestAssert.Equal((byte)0x0A, bytes[10], name + " registries");
            TestAssert.Equal(fountainId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..15]), name + " entity");
            TestAssert.Equal(0xFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..19]), name + " mask");
            // The two fields the client kept at its constructor defaults (25 and 0) before the shrine was given this block.
            TestAssert.Equal(999.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[19..23]), name + " mBaseAttackDamage");
            // Batch 752: mPercentPhysicalDamageMod is the constructor's 0 too; the client adds 1 to it wherever it reads it.
            TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[39..43]), name + " mPercentPhysicalDamageMod");
            TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[67..71]), name + " invulnerable");
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[71..75]), name + " targetable");
            TestAssert.Equal(CombatPackets.SFlagNontargetableAll,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[75..79]), name + " flags");
            TestAssert.Equal(fountainId, packet.EntityReferences.Single().NetId, name + " reference");
            return;
        }
        TestAssert.Equal(CombatPackets.CombatChannel, packet.Channel, name + " channel");
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability, name + " reliability");
        TestAssert.True(packet.Encrypt, name + " encryption");
        TestAssert.Equal(31, bytes.Length, name + " length");
        TestAssert.Equal((byte)0xCC, bytes[0], name + " opcode");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]), name + " header");
        TestAssert.Equal(expectedSyncId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..9]), name + " sync");
        TestAssert.Equal((byte)1, bytes[9], name + " map count");
        TestAssert.Equal((byte)2, bytes[10], name + " map");
        TestAssert.Equal(fountainId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..15]), name + " entity");
        TestAssert.Equal(0x7000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..19]), name + " mask");
        TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[19..23]), name + " invulnerable");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[23..27]), name + " targetable");
        TestAssert.Equal(CombatPackets.SFlagNontargetableAll,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[27..31]), name + " flags");
        TestAssert.Equal(1, packet.EntityReferences.Count, name + " reference count");
        TestAssert.Equal(LeaguePacketEntityRole.World, packet.EntityReferences[0].Role,
            name + " reference role");
        TestAssert.Equal(fountainId, packet.EntityReferences[0].NetId, name + " reference");
    }

    private static IReadOnlyList<SessionOutboundPacket> NormalizeDriverAvatarOrder(
        IReadOnlyList<JsonElement> expected, IReadOnlyList<SessionOutboundPacket> actual)
    {
        var expectedOpcodes = expected.Select(packet =>
            Convert.FromHexString(packet.GetProperty("plaintext").GetString()!)[0]).ToArray();
        if (expected.Count != actual.Count || !expectedOpcodes.Contains((byte)0x2C) ||
            !expectedOpcodes.Contains((byte)0xCC))
            return actual;

        var ordered = actual.Where(item => item.Packet.Plaintext.Span[0] != 0x2C).ToList();
        var avatars = actual.Where(item => item.Packet.Plaintext.Span[0] == 0x2C).ToArray();
        var avatarIndex = 0;
        for (var index = 0; index < expectedOpcodes.Length; index++)
        {
            if (expectedOpcodes[index] != 0x2C) continue;
            if (avatarIndex >= avatars.Length || index > ordered.Count) return actual;
            // Preserve the actual seat/connection pairing while relocating
            // only the AvatarInfo rows to the archived packet positions.
            ordered.Insert(index, avatars[avatarIndex++]);
        }
        if (avatarIndex != avatars.Length) return actual;
        return ordered;
    }

    private static DuelSession NewSession(
        string orderChampion = "Annie",
        string chaosChampion = "Orianna",
        SessionRosterCounts? originalRosterCounts = null,
        WorldState? world = null,
        double readySettleSeconds = 0.0,
        IReadOnlyList<SessionParticipantDefinition>? roster = null,
        int mapId = GameMaps.Normal)
    {
        var data = GameData.Default;
        var syncIds = new ReplicationSyncCounter();
        var services = new SessionTestGameplayServices(syncIds);
        return new DuelSession(
            new SessionDependencies(CreateHeroStats(data), syncIds,
                services, services, services) { MapId = mapId },
            world: world,
            orderChampion: orderChampion, chaosChampion: chaosChampion,
            originalRosterCounts: originalRosterCounts, gameData: data,
            readySettleSeconds: readySettleSeconds, roster: roster);
    }

    private static HeroStatsService CreateHeroStats(GameData data) =>
        new(data, new ItemCatalog(data.Paths), new SessionStatsHooks(data), new SessionStatsTransport());

    private sealed class SessionStatsHooks(GameData data) : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) => HeroAttackTimings.ApplyRefresh(hero, data);
        public IReadOnlyList<double> ManaCosts(Entity hero) => data.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire)
        {
            for (var index = hero.Buffs.Count - 1; index >= 0; index--)
                if (hero.Buffs[index].Slot == slot) hero.Buffs.RemoveAt(index);
        }
    }

    private sealed class SessionStatsTransport : IHeroStatsTransport
    {
        public void Push(HeroStatsReplicationSnapshot snapshot) { }
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) { }
    }

    private sealed class SessionTestGameplayServices :
        ISessionMovementService, ISessionCombatService, ISessionEffectService
    {
        public bool Surrender(Team team) => false;

        private readonly ReplicationSyncCounter _syncIds;

        public SessionTestGameplayServices(ReplicationSyncCounter syncIds)
        {
            _syncIds = syncIds;
        }

        public IReadOnlyList<Position2> SetMoveOrder(Entity unit, Position2 destination)
        {
            var route = new[] { unit.Position, destination };
            unit.Waypoints = new[] { destination }.ToList();
            unit.Target = null;
            return route;
        }

        public IReadOnlyList<Position2> InstallStop(Entity unit) => [unit.Position];

        public void CancelPendingActions(Entity unit)
        {
            unit.AttackTarget = null;
            unit.PendingCast = null;
        }

        public IReadOnlyList<PlaintextPacket> EngageProtection(WorldState world)
        {
            var packets = new List<PlaintextPacket>();
            ProtectionRules.Engage(world, entity =>
            {
                if (entity.UnitType == UnitType.Turret)
                {
                    packets.Add(CombatPackets.TurretProtectionFlags(entity.NetId,
                        entity.Protected ? CombatPackets.SFlagNontargetableEnemy :
                            CombatPackets.SFlagTargetableToAll,
                        _syncIds.Next(entity.NetId), entity.Protected ? 1u : 0u, 1u));
                }
                else if (entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus)
                {
                    packets.Add(CombatPackets.BuildingStats(entity.NetId,
                        new BuildingStatsSnapshot(Float(entity.Health), entity.Protected
                            ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll,
                            entity.Protected ? 1u : 0u, 1u), _syncIds.Next(entity.NetId)));
                }
            });
            return packets;
        }

        public PlaintextPacket BuildStopAttack(Entity unit) => CombatPackets.StopAttack(unit)!;

        public PlaintextPacket BuildWaypoints(Entity unit, IReadOnlyList<Position2> points, uint syncId) =>
            VisionPackets.WaypointList(unit.NetId, points, syncId);

        private static float Float(double value)
        {
            if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be a finite float32.");
            return (float)value;
        }
    }

    private static void TestHero(JsonElement expected, Entity actual)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, expected.EnumerateObject().ToDictionary(field => field.Name, field => field.Name switch
            {
                "netid" => (object?)(actual.NetId),
                "champion_name" => (object?)(actual.ChampionName),
                "gold" => (object?)(actual.Gold),
                "armor" => (object?)(actual.Armor),
                "spell_block" => (object?)(actual.SpellBlock),
                "speed" => (object?)(actual.Speed),
                "hp" => (object?)(actual.Health),
                "max_hp" => (object?)(actual.MaxHealth),
                "mana" => (object?)(actual.Mana),
                "max_mana" => (object?)(actual.MaxMana),
                "radius" => (object?)(actual.Radius),
                "atk_range" => (object?)(actual.AttackRange),
                "atk_damage" => (object?)(actual.AttackDamage),
                "atk_period" => (object?)(actual.AttackPeriod),
                "missile_speed" => (object?)(actual.MissileSpeed),
                "windup" => (object?)(actual.AttackWindup),
                "pos" => (object?)(new[] { actual.Position.X, actual.Position.Z }),
                "unit_type" => (object?)((int)actual.UnitType),
                "team" => (object?)((int)actual.Team),
                "name" => (object?)(actual.Name),
                "etype" => (object?)actual.EntityType,
                "turret_scaling_duration" => (object?)actual.TurretScalingDuration,
                "turret_base_armor" => (object?)actual.TurretBaseArmor,
                "hp_regen" => (object?)actual.HealthRegen,
                "attack_speed_mod" => (object?)actual.AttackSpeedMod,
                "turret_scaling_start" => (object?)actual.TurretScalingStart,
                "turret_base_damage" => (object?)actual.TurretBaseDamage,
                "mp_regen" => (object?)actual.ManaRegen,
                "turret_base_spell_block" => (object?)actual.TurretBaseSpellBlock,
                "protected" => (object?)actual.Protected,
                "turret_scaling_stacks" => (object?)actual.TurretScalingStacks,
                _ => throw new InvalidOperationException("Unknown spawn output: " + field.Name),
            }));
            return;
        }
        TestAssert.Equal(expected.GetProperty("netid").GetUInt32(), actual.NetId);
        TestAssert.Equal(expected.GetProperty("unit_type").GetInt32(), (int)actual.UnitType);
        TestAssert.Equal(expected.GetProperty("team").GetInt32(), (int)actual.Team);
        TestAssert.Equal(expected.GetProperty("champion_name").GetString(), actual.ChampionName);
        var position = expected.GetProperty("pos");
        TestAssert.Near(position[0].GetDouble(), actual.Position.X, 1e-9);
        TestAssert.Near(position[1].GetDouble(), actual.Position.Z, 1e-9);
        TestAssert.Near(expected.GetProperty("gold").GetDouble(), actual.Gold, 1e-6);
        TestAssert.Near(expected.GetProperty("armor").GetDouble(), actual.Armor, 1e-6);
        TestAssert.Near(expected.GetProperty("spell_block").GetDouble(), actual.SpellBlock, 1e-6);
        TestAssert.Near(expected.GetProperty("speed").GetDouble(), actual.Speed, 1e-6);
        TestAssert.Near(expected.GetProperty("hp").GetDouble(), actual.Health, 1e-6);
        TestAssert.Near(expected.GetProperty("max_hp").GetDouble(), actual.MaxHealth, 1e-6);
        TestAssert.Near(expected.GetProperty("mana").GetDouble(), actual.Mana, 1e-6);
        TestAssert.Near(expected.GetProperty("max_mana").GetDouble(), actual.MaxMana, 1e-6);
        TestAssert.Near(expected.GetProperty("radius").GetDouble(), actual.Radius, 1e-6);
        TestAssert.Near(expected.GetProperty("atk_range").GetDouble(), actual.AttackRange, 1e-6);
        TestAssert.Near(expected.GetProperty("atk_damage").GetDouble(), actual.AttackDamage, 1e-6);
        TestAssert.Near(expected.GetProperty("atk_period").GetDouble(), actual.AttackPeriod, 1e-6);
        TestAssert.Near(expected.GetProperty("missile_speed").GetDouble(), actual.MissileSpeed, 1e-6);
        TestAssert.Near(expected.GetProperty("windup").GetDouble(), actual.AttackWindup, 1e-6);
    }

    private static void TestStructure(JsonElement expected, Entity actual)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, expected.EnumerateObject().ToDictionary(field => field.Name, field => field.Name switch
            {
                "netid" => (object?)(actual.NetId),
                "hp" => (object?)(actual.Health),
                "max_hp" => (object?)(actual.MaxHealth),
                "radius" => (object?)(actual.Radius),
                "armor" => (object?)(actual.Armor),
                "spell_block" => (object?)(actual.SpellBlock),
                "atk_damage" => (object?)(actual.AttackDamage),
                "atk_period" => (object?)(actual.AttackPeriod),
                "missile_speed" => (object?)(actual.MissileSpeed),
                "windup" => (object?)(actual.AttackWindup),
                "turret_scaling_stacks" => (object?)(actual.TurretScalingStacks),
                "pos" => (object?)(new[] { actual.Position.X, actual.Position.Z }),
                "unit_type" => (object?)((int)actual.UnitType),
                "team" => (object?)((int)actual.Team),
                "champion_name" => (object?)(actual.ChampionName),
                "name" => (object?)(actual.Name),
                "etype" => (object?)actual.EntityType,
                "turret_scaling_duration" => (object?)actual.TurretScalingDuration,
                "turret_base_armor" => (object?)actual.TurretBaseArmor,
                "hp_regen" => (object?)actual.HealthRegen,
                "attack_speed_mod" => (object?)actual.AttackSpeedMod,
                "turret_scaling_start" => (object?)actual.TurretScalingStart,
                "turret_base_damage" => (object?)actual.TurretBaseDamage,
                "mp_regen" => (object?)actual.ManaRegen,
                "turret_base_spell_block" => (object?)actual.TurretBaseSpellBlock,
                "protected" => (object?)actual.Protected,
                "gold" => (object?)actual.Gold,
                "speed" => (object?)actual.Speed,
                "max_mana" => (object?)actual.MaxMana,
                "mana" => (object?)actual.Mana,
                "atk_range" => (object?)actual.AttackRange,
                _ => throw new InvalidOperationException("Unknown spawn output: " + field.Name),
            }));
            AssertStructureGeometry(actual);
            return;
        }
        TestAssert.Equal(expected.GetProperty("netid").GetUInt32(), actual.NetId, actual.Name + " netid");
        TestAssert.Equal(expected.GetProperty("team").GetInt32(), (int)actual.Team, actual.Name + " team");
        TestAssert.Near(expected.GetProperty("pos")[0].GetDouble(), actual.Position.X, 1e-6);
        TestAssert.Near(expected.GetProperty("pos")[1].GetDouble(), actual.Position.Z, 1e-6);
        TestAssert.Near(expected.GetProperty("hp").GetDouble(), actual.Health, 1e-6, actual.Name + " hp");
        TestAssert.Near(expected.GetProperty("max_hp").GetDouble(), actual.MaxHealth, 1e-6, actual.Name + " max hp");
        TestAssert.Near(expected.GetProperty("radius").GetDouble(), actual.Radius, 1e-6);
        AssertStructureGeometry(actual);
        TestAssert.Near(expected.GetProperty("armor").GetDouble(), actual.Armor, 1e-6, actual.Name + " armor");
        TestAssert.Near(expected.GetProperty("spell_block").GetDouble(), actual.SpellBlock, 1e-6, actual.Name + " MR");
        TestAssert.Near(expected.GetProperty("atk_damage").GetDouble(), actual.AttackDamage, 1e-6, actual.Name + " damage");
        TestAssert.Near(expected.GetProperty("atk_period").GetDouble(), actual.AttackPeriod, 1e-6, actual.Name + " period");
        TestAssert.Near(expected.GetProperty("missile_speed").GetDouble(), actual.MissileSpeed, 1e-6, actual.Name + " missile");
        TestAssert.Near(expected.GetProperty("windup").GetDouble(), actual.AttackWindup, 1e-6, actual.Name + " windup");
        TestAssert.Equal(expected.GetProperty("turret_scaling_stacks").GetInt32(), actual.TurretScalingStacks);
    }

    [Check("session.structure-retail-geometry")]
    public static void StructureRetailGeometry()
    {
        foreach (var entity in NewSession().CreateWorld().MapEntities) AssertStructureGeometry(entity);
    }

    private static void AssertStructureGeometry(Entity actual)
    {
        var name = actual.Name;
        double attack;
        double pathing;
        if (name.StartsWith("Turret_", StringComparison.Ordinal))
        {
            attack = pathing = 88.4000015258789;
        }
        else if (name is "HQ_T1" or "HQ_T2")
        {
            attack = 279.43333435058594;
            pathing = name == "HQ_T1" ? 352.7778 : 240.7846;
        }
        else
        {
            pathing = name switch
            {
                "Barracks_T1_L1" => 187.15,
                "Barracks_T1_C1" => 186.7707,
                "Barracks_T1_R1" => 185.4683,
                "Barracks_T2_L1" or "Barracks_T2_C1" or "Barracks_T2_R1" => 213.75,
                _ => throw new InvalidOperationException("Unreviewed structure geometry: " + name),
            };
            attack = 204.4998550415039;
        }
        TestAssert.Near(attack, actual.Radius, 1e-6, name + " attack radius");
        TestAssert.Near(pathing, actual.PathfindingRadius ?? actual.Radius, 1e-6, name + " pathing radius");
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
