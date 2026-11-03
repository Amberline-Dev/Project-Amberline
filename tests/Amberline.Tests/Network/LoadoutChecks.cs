using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Server;

namespace Amberline.Tests.Network;

public static class LoadoutChecks
{
    [Check("network.loadout-production-pages")]
    public static void ProductionInitialAndReconnectKeepPagesPerSeat()
    {
        var runes = new RuneCatalog();
        var masteries = new MasteryCatalog();
        var first = Page(runes, masteries, new[] { 5001, 5014, 5025 }, new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [136] = 1, [126] = 1, [102] = 4, [107] = 1,
        });
        var second = Page(runes, masteries, new[] { 5002 }, new Dictionary<int, int>
        {
            [133] = 1, [110] = 3, [129] = 1, [109] = 3, [112] = 1,
        });
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "First", "Annie", Team.Order,
                SummonerLevel: 30, Runes: first.Runes, Masteries: first.Masteries),
            new SessionParticipantDefinition(2, 1, "Second", "Orianna", Team.Chaos,
                SummonerLevel: 30, Runes: second.Runes, Masteries: second.Masteries),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        var firstSeat = session.Authenticate(11, session.BuildKeyCheck(1))!;
        var secondSeat = session.Authenticate(12, session.BuildKeyCheck(2))!;
        var firstSpawn = session.Spawn(11);
        var secondSpawn = session.Spawn(12);

        var firstHero = firstSeat.Hero ?? throw new InvalidOperationException("first hero was not created");
        var secondHero = secondSeat.Hero ?? throw new InvalidOperationException("second hero was not created");
        var firstAvatar = Avatar(firstSpawn, firstHero.NetId);
        var secondAvatar = Avatar(firstSpawn, secondHero.NetId);
        var firstAvatarIndex = firstSpawn.ToList().FindIndex(packet =>
            packet.Plaintext.Span[0] == 0x2C &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == firstHero.NetId);
        var firstStatsIndex = firstSpawn.ToList().FindIndex(packet =>
            packet.Plaintext.Length >= 15 && packet.Plaintext.Span[0] == 0xCC &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..15]) == firstHero.NetId);
        TestAssert.True(firstAvatarIndex >= 0 && firstStatsIndex > firstAvatarIndex,
            "initial effective hero stats follow AvatarInfo loadout initialization");
        var firstClassic = ClassicStats(firstSpawn);
        TestAssert.Equal(2, firstClassic.Length,
            "initial owner receives its full row and one public foreign row");
        TestAssert.Equal(firstHero.NetId, firstClassic.Single(packet =>
            packet.EntityReferences.Single().NetId == firstHero.NetId).EntityReferences.Single().NetId,
            "initial Classic snapshot belongs to the requesting owner hero");
        TestAssert.Equal(secondHero.NetId, firstClassic.Single(packet =>
            packet.EntityReferences.Single().NetId == secondHero.NetId).EntityReferences.Single().NetId,
            "the initial foreign Classic projection identifies the other hero");
        var secondClassic = ClassicStats(secondSpawn);
        TestAssert.Equal(2, secondClassic.Length,
            "the second owner receives its full row and one public foreign row");
        TestAssert.Equal(secondHero.NetId, secondClassic.Single(packet =>
            packet.EntityReferences.Single().NetId == secondHero.NetId).EntityReferences.Single().NetId,
            "the second Classic snapshot is seat-local");
        var firstStats = runtime.HeroStats.Calculate(firstHero);
        TestAssert.Near(firstStats.AttackDamage, firstHero.AttackDamage, 1e-9,
            "initial production attack damage includes the selected page");
        TestAssert.Near(firstStats.MaxMp, firstHero.MaxMana, 1e-9,
            "initial production resource pool includes the selected page");
        var baseAnnie = runtime.GameData.StatsAtLevel(runtime.GameData.Champion("Annie"), 1);
        var masteryContribution = MasteryEffects.Calculate(first.Masteries, championLevel: 1);
        TestAssert.Near(Modifier(first.Runes.Modifiers, "FlatPhysicalDamageMod") +
                        Modifier(masteryContribution.Modifiers, "FlatPhysicalDamageMod"),
            firstHero.AttackDamage - baseAnnie.AttackDamage, 1e-9,
            "initial attack damage contains the shipped rune/mastery contribution");
        var expectedMana = baseAnnie.MaxMp + Modifier(first.Runes.Modifiers, "FlatMPPoolMod") +
                           Modifier(first.Runes.Modifiers, "rFlatMPModPerLevel") +
                           Modifier(masteryContribution.Modifiers, "FlatMPPoolMod");
        TestAssert.Near(expectedMana, firstHero.MaxMana, 1e-9,
            "initial mana contains the shipped resource contribution");
        var levelOneArmor = firstHero.Armor;
        firstHero.Level = 6;
        runtime.HeroStats.Refresh(firstHero);
        var levelSixStats = runtime.HeroStats.Calculate(firstHero);
        TestAssert.Near(levelSixStats.Armor, firstHero.Armor, 1e-9,
            "level-up refresh keeps per-level page modifiers in the authoritative armor");
        var levelSixBase = runtime.GameData.StatsAtLevel(runtime.GameData.Champion("Annie"), 6);
        var perLevelArmor = Modifier(first.Runes.Modifiers, "rFlatArmorModPerLevel") +
                            Modifier(masteryContribution.Modifiers, "rFlatArmorModPerLevel");
        TestAssert.True(Math.Abs(perLevelArmor) > 1e-12,
            "the production page includes a shipped per-level armor rune");
        TestAssert.Near((levelSixBase.Armor - baseAnnie.Armor) + perLevelArmor * 5.0,
            firstHero.Armor - levelOneArmor, 1e-9,
            "level-up applies the page's per-level armor contribution once");
        TestAssert.True(Math.Abs(firstHero.Armor - levelOneArmor) > 1e-9,
            "level-up changes the page-aware effective armor");
        firstHero.Level = 1;
        runtime.HeroStats.Refresh(firstHero);
        AssertAvatar(firstAvatar, firstSeat, "initial first seat");
        AssertAvatar(secondAvatar, secondSeat, "initial second seat");
        TestAssert.True(!firstAvatar.AsSpan(5, 120).SequenceEqual(secondAvatar.AsSpan(5, 120)),
            "each seat receives its own rune positions");
        TestAssert.True(!firstAvatar.AsSpan(133, 400).SequenceEqual(secondAvatar.AsSpan(133, 400)),
            "each seat receives its own mastery entries");

        session.MarkReady(11);
        session.MarkReady(12);
        session.BuildMapReadyPackets(11);
        session.BuildMapReadyPackets(12);
        TestAssert.True(session.Start());
        TestAssert.True(session.Disconnect(11));
        var reconnected = session.AuthenticateReconnect(15, session.BuildKeyCheck(1))!;
        var versionOutput = session.Receive(15, 1, [0xC5]);
        TestAssert.True(versionOutput.Any(packet => packet.Packet.Plaintext.Span[0] == 0x57),
            "reconnect handshake arms the production spawn scheduler");
        session.Receive(15, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = reconnected.SpawnAt ??
            throw new InvalidOperationException("reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        var reconnectSpawn = session.RunSchedulers()
            .Where(packet => packet.ConnectionId == 15)
            .Select(packet => packet.Packet)
            .ToArray();
        TestAssert.Equal(0, ClassicStats(reconnectSpawn).Length,
            "reconnect spawn baseline defers Classic stats until ready-stage restoration");
        var reconnectAvatar = Avatar(reconnectSpawn, reconnected.Hero!.NetId);
        AssertAvatar(reconnectAvatar, reconnected, "reconnect first seat");
        TestAssert.Equal(firstAvatar, reconnectAvatar,
            "reconnect avatar metadata is the same typed page, not the other seat's page");
        var reconnectedHero = reconnected.Hero!;
        reconnected.Loaded = true;
        session.MarkReady(15);
        session.BuildReconnectReadyPackets(15);
        var reconnectStats = session.BuildReconnectMatchState(15);
        TestAssert.Equal(2, ClassicStats(reconnectStats).Length,
            "ready-stage reconnect restores the owner and public foreign rows");
        var refreshBurst = session.BuildClassicHeroStatsRefreshBurst(15,
            [0x59, (byte)reconnectedHero.NetId,
                (byte)(reconnectedHero.NetId >> 8), (byte)(reconnectedHero.NetId >> 16),
                (byte)(reconnectedHero.NetId >> 24)]);
        TestAssert.Equal(2, refreshBurst.Count,
            "authenticated actor-owned 0x59 restores every known row");
        TestAssert.Equal(reconnectedHero.NetId,
            BinaryPrimitives.ReadUInt32LittleEndian(refreshBurst[0].Plaintext.Span[1..5]),
            "0x59 restores the requesting owner's full row first");
        TestAssert.Equal(secondHero.NetId,
            BinaryPrimitives.ReadUInt32LittleEndian(refreshBurst[1].Plaintext.Span[1..5]),
            "0x59 restores the other row through the public projection");
        TestAssert.Equal(0, session.BuildClassicHeroStatsRefreshBurst(15,
            [0x59, (byte)secondHero.NetId, (byte)(secondHero.NetId >> 8),
                (byte)(secondHero.NetId >> 16), (byte)(secondHero.NetId >> 24)]).Count,
            "0x59 for another seat cannot restore a foreign row");
    }

    [Check("network.loadout-roster-copy-and-validation")]
    public static void RosterCopiesPagesAndKeepsLegacyEmptyDefaults()
    {
        var red = new[] { 5001 };
        var masteries = new Dictionary<int, int>();
        var source = new MatchPlayer("Copied", "Annie", Team.Order,
            RedRunes: red, Masteries: masteries);
        var validated = MatchRoster.Validate(new[] { source })[0];
        var roundTrip = MatchRoster.Validate(JsonSerializer.Deserialize<MatchPlayer[]>(
            JsonSerializer.Serialize(new[] { source }, MatchRoster.JsonOptions), MatchRoster.JsonOptions)!)[0];
        TestAssert.Equal(validated.RedRunes!, roundTrip.RedRunes!, "saved roster keeps rune slots");
        TestAssert.Equal(validated.Masteries!, roundTrip.Masteries!, "saved roster keeps mastery allocations");
        red[0] = 5002;
        masteries[103] = 3;
        TestAssert.Equal(5001, validated.RedRunes![0], "validated runes own the source array");
        TestAssert.Equal(0, validated.Masteries!.Count, "validated masteries own the source dictionary");

        var legacy = MatchRoster.ToParticipants([
            new MatchPlayer("Legacy", "Annie", Team.Order),
            new MatchPlayer("Legacy2", "Orianna", Team.Chaos),
        ]);
        TestAssert.Equal(30, legacy[0].SummonerLevel, "legacy roster uses the seat account default");
        TestAssert.True(legacy[0].Runes is { IsEmpty: true }, "legacy roster has no rune effects");
        TestAssert.True(legacy[0].Masteries is { TotalPoints: 0 }, "legacy roster has no mastery effects");

        TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([
            new MatchPlayer("BadRune", "Annie", Team.Order, RedRunes: new[] { 5031 }),
        ]));
        TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([
            new MatchPlayer("BadMastery", "Annie", Team.Order,
                Masteries: new Dictionary<int, int> { [999] = 1 }),
        ]));
    }

    private static (RunePage Runes, MasteryPage Masteries) Page(
        RuneCatalog catalog, MasteryCatalog masteryCatalog, int[] red,
        Dictionary<int, int> masteryAllocations)
    {
        return (
            RunePage.Create(catalog, red, summonerLevel: 30),
            MasteryPage.Create(masteryCatalog, masteryAllocations, summonerLevel: 30));
    }

    private static byte[] Avatar(IReadOnlyList<PlaintextPacket> packets, uint netId) =>
        packets.Single(packet => packet.Plaintext.Length == 534 &&
            packet.Plaintext.Span[0] == 0x2C &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == netId)
            .Plaintext.ToArray();

    private static PlaintextPacket[] ClassicStats(
        IReadOnlyList<PlaintextPacket> packets) => packets
        .Where(packet => packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == 0x4B)
        .ToArray();

    private static void AssertAvatar(byte[] payload, SessionParticipant participant, string context)
    {
        TestAssert.Equal((byte)0x2C, payload[0], context + " opcode");
        TestAssert.Equal(participant.Hero!.NetId,
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)), context + " hero");
        TestAssert.Equal(participant.Runes.WireIds.ToArray(),
            ReadUInt32s(payload, 5, RunePage.TotalSlots), context + " runes");
        TestAssert.Equal(ProtocolEncoding.HashString(participant.SummonerSpell1),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(125, 4)), context + " spell 1");
        TestAssert.Equal(ProtocolEncoding.HashString(participant.SummonerSpell2),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(129, 4)), context + " spell 2");
        var masteryBytes = payload.AsSpan(133, 400);
        var entries = new Dictionary<uint, byte>();
        for (var index = 0; index < 80; index++)
        {
            var offset = index * 5;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(masteryBytes[offset..]);
            var rank = masteryBytes[offset + 4];
            if (id != 0) entries.Add(id, rank);
        }
        var expected = participant.Masteries.Allocations.ToDictionary(
            pair => ProtocolEncoding.HashString(pair.Key.ToString()), pair => checked((byte)pair.Value));
        TestAssert.Equal(expected, entries, context + " masteries");
        TestAssert.Equal((byte)participant.SummonerLevel, payload[533], context + " account level");
    }

    private static int[] ReadUInt32s(byte[] payload, int offset, int count)
    {
        var values = new int[count];
        for (var index = 0; index < count; index++)
            values[index] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(offset + index * 4, 4)));
        return values;
    }

    private static double Modifier(IReadOnlyDictionary<string, double> modifiers, string name) =>
        modifiers.TryGetValue(name, out var value) ? value : 0.0;
}
