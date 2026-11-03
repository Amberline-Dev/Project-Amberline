using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>
/// First bot game (29 September 2026): every bot loading card showed no splash, empty summoner
/// spells and "Not Connected", and in-game names read game_bot_&lt;Champion&gt;. The stock client
/// builds a roster card's splash and spells only from a Ping_Load_Info (0x9D) received after its
/// loading screen exists (the client's player connection update, reached from the 0x9D packet handler)
/// and draws "Not Connected" for a seat without one. Its CreateHero handler (0x4F, byte 16) replaces
/// the name of an isBot hero by the localized "game_bot_" plus the
/// skin name, which only 18 retail bot champions have in fontconfig.
/// </summary>
public static class BotLoadingChecks
{
    private static readonly string[] BotChampions =
        ["Annie", "Shaco", "Janna", "Nidalee", "Blitzcrank", "Orianna", "Yorick", "Ezreal", "LeeSin"];

    // Independent oracle: the game_bot_ keys of the stock DATA/Menu/fontconfig_en_US.txt.
    private static readonly string[] RetailBotKeys =
    [
        "Alistar", "Annie", "Ashe", "Chogath", "Malphite", "Maokai", "MasterYi", "MissFortune", "Nasus",
        "Nunu", "Renekton", "Ryze", "Shen", "Sivir", "Soraka", "Taric", "Trundle", "Warwick",
    ];

    private readonly record struct Row(
        long Connection, uint ClientId, ulong PlayerId, float Percent, float Eta, short Count, ushort Ping, bool Ready);

    /// <summary>One human on Order and nine bots, five per team.</summary>
    private static SessionParticipantDefinition[] OneHumanNineBots(int humans = 1)
    {
        var data = GameData.Default;
        var roster = new List<SessionParticipantDefinition>();
        for (var human = 0; human < humans; human++)
            roster.Add(new SessionParticipantDefinition((ulong)human + 1, (uint)human, $"Human{human + 1}",
                human == 0 ? "Vayne" : "Ezreal", human == 0 ? Team.Order : Team.Chaos, "SummonerHaste", "SummonerFlash"));
        for (var index = 0; roster.Count < 10; index++)
            roster.Add(new SessionParticipantDefinition((ulong)roster.Count + 1, (uint)roster.Count,
                data.BotName(BotChampions[index]), BotChampions[index], roster.Count < 5 ? Team.Order : Team.Chaos,
                "SummonerFlash", "SummonerDot", IsBot: true));
        return roster.ToArray();
    }

    private static Row[] Rows(IEnumerable<SessionOutboundPacket> packets) => packets
        .Where(item => item.Packet.Plaintext.Span[0] == 0x9D)
        .Select(item =>
        {
            var packet = item.Packet.Plaintext.Span;
            TestAssert.Equal(30, packet.Length, "Ping_Load_Info size");
            return new Row(item.ConnectionId, BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]),
                BinaryPrimitives.ReadUInt64LittleEndian(packet[9..]),
                BinaryPrimitives.ReadSingleLittleEndian(packet[17..]),
                BinaryPrimitives.ReadSingleLittleEndian(packet[21..]),
                BinaryPrimitives.ReadInt16LittleEndian(packet[25..]),
                BinaryPrimitives.ReadUInt16LittleEndian(packet[27..]), packet[29] == 1);
        }).ToArray();

    private static string Ascii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>A stock client's C2S loading report: the first 24 bytes Blowfish-encrypted.</summary>
    private static byte[] LoadingReport(MatchRuntime runtime, float percent, bool ready)
    {
        var report = new byte[30];
        report[0] = 0x19;
        report[29] = (byte)(ready ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(report.AsSpan(17), BitConverter.SingleToInt32Bits(percent));
        return runtime.Cipher.Encrypt(report.AsSpan(0, 24)).Concat(report.AsSpan(24).ToArray()).ToArray();
    }

    [Check("network.bot-loading-cards-follow-client-reports")]
    public static void BotCardsAreFedByTheReportingClient()
    {
        var roster = OneHumanNineBots();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);

        // Authentication: rows sent here reach the client before its loading screen exists, and are
        // dropped, so only the human's own (also dropped, then re-sent by its reports) row is kept.
        var auth = session.Receive(1, 0, session.BuildKeyCheck(1));
        TestAssert.Equal(new[] { 0u }, Rows(auth).Select(row => row.ClientId).ToArray(),
            "no bot row is sent before the client's loading screen exists");
        var registry = auth.Select(item => item.Packet).Where(packet =>
            packet.Channel == 0 && packet.Plaintext.Length == 16 && packet.Plaintext.Span[0] == 0).ToArray();
        TestAssert.Equal(10, registry.Length, "every seat, bots included, is a registered client");
        TestAssert.Equal(roster.Select(seat => seat.ClientId).ToArray(),
            registry.Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[4..])).ToArray());
        TestAssert.Equal(roster.Select(seat => seat.PlayerId).ToArray(),
            registry.Select(packet => BinaryPrimitives.ReadUInt64LittleEndian(packet.Plaintext.Span[8..])).ToArray());

        // Roster (0x67) and per-player skin (0x65) and name (0x66): the card's champion and name.
        var burst = auth.Select(item => item.Packet).Where(packet => packet.Channel == LeagueProtocolConstants.LoadingChannel).ToArray();
        var names = burst.Where(packet => packet.Plaintext.Span[0] == 0x66).ToDictionary(
            packet => BinaryPrimitives.ReadUInt64LittleEndian(packet.Plaintext.Span[8..]),
            packet => Ascii(packet.Plaintext.Span[24..]));
        var skins = burst.Where(packet => packet.Plaintext.Span[0] == 0x65).ToDictionary(
            packet => BinaryPrimitives.ReadUInt64LittleEndian(packet.Plaintext.Span[8..]),
            packet => Ascii(packet.Plaintext.Span[24..]));
        foreach (var seat in roster)
        {
            TestAssert.Equal(seat.Name, names[seat.PlayerId], $"RequestRename for {seat.ChampionName}");
            TestAssert.Equal(seat.ChampionName, skins[seat.PlayerId], $"RequestReskin for {seat.ChampionName}");
        }
        var rosterPacket = burst.Single(packet => packet.Plaintext.Span[0] == 0x67).Plaintext.ToArray();
        var listed = Enumerable.Range(0, 24).Select(slot => BinaryPrimitives.ReadUInt64LittleEndian(rosterPacket.AsSpan(16 + 8 * slot)))
            .Concat(Enumerable.Range(0, 24).Select(slot => BinaryPrimitives.ReadUInt64LittleEndian(rosterPacket.AsSpan(16 + 192 + 8 * slot))))
            .Where(id => id != 0).Order().ToArray();
        TestAssert.Equal(roster.Select(seat => seat.PlayerId).Order().ToArray(), listed, "all ten seats are in the team roster");

        // SynchVersion: the summoner-spell hashes, level, team, name and champion of each seat.
        var version = session.Receive(1, 1, [0xC5]).Select(item => item.Packet)
            .Single(packet => packet.Plaintext.Span[0] == 0x57).Plaintext.Span;
        TestAssert.Equal(1438, version.Length);
        for (var index = 0; index < 12; index++)
        {
            var entry = version.Slice(10 + 87 * index, 87);
            if (index >= roster.Length)
            {
                TestAssert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(entry), "empty slot sentinel");
                continue;
            }
            var seat = roster[index];
            TestAssert.Equal(seat.PlayerId, BinaryPrimitives.ReadUInt64LittleEndian(entry));
            TestAssert.Equal((ushort)30, BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]));
            TestAssert.Equal(ProtocolEncoding.HashString(seat.SummonerSpell1), BinaryPrimitives.ReadUInt32LittleEndian(entry[10..]));
            TestAssert.Equal(ProtocolEncoding.HashString(seat.SummonerSpell2), BinaryPrimitives.ReadUInt32LittleEndian(entry[14..]));
            // Deliberate: bots are announced as roster players. The client builds a card for an entry
            // with this byte set from its own data, without spells and outside the roster.
            TestAssert.Equal((byte)0, entry[18], $"{seat.ChampionName} is a roster card, not a client-built bot card");
            TestAssert.Equal(seat.Team == Team.Order ? 100 : 200, BinaryPrimitives.ReadInt32LittleEndian(entry[19..]));
            TestAssert.Equal(seat.Name, Ascii(entry.Slice(23, 28)));
            TestAssert.Equal(seat.ChampionName, Ascii(entry.Slice(51, 28)));
        }

        // A loading report: the echo of the human's own row, then every bot's completed row, all for
        // the reporting client.
        var report = LoadingReport(runtime, 40.0f, ready: false);
        var rows = Rows(session.Receive(1, 1, report));
        TestAssert.Equal(10, rows.Length, "the human's echo and nine bot rows");
        TestAssert.Equal(new Row(1, 0, 1, 40.0f, 0.0f, 0, 0, false), rows[0], "the human row echoes its own report");
        for (var index = 1; index < 10; index++)
            TestAssert.Equal(new Row(1, roster[index].ClientId, roster[index].PlayerId, 100.0f, 0.0f, 0, 0, true), rows[index],
                $"{roster[index].ChampionName} is fully loaded and ready");
        TestAssert.Equal(10, Rows(session.Receive(1, 1, LoadingReport(runtime, 100.0f, ready: true))).Length,
            "the rows follow every report while the client loads");

        // Once the client's world is up its loading screen is gone and the rows stop.
        session.Receive(1, 1, [0xA4, 0, 0, 0, 0, 1]);
        session.Receive(1, 1, [0x55]);
        runtime.World.Clock.SetVirtual(session.Participant(1)!.SpawnAt!.Value);
        session.RunSchedulers();
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        TestAssert.True(session.Participant(1)!.Loaded, "the native world-ready frame arrived");
        TestAssert.Equal(1, Rows(session.Receive(1, 1, report)).Length, "a loaded client only gets its own echo");
    }

    [Check("network.bot-loading-rows-reach-only-the-reporting-client")]
    public static void BotRowsAreNotRelayedToOtherHumans()
    {
        var roster = OneHumanNineBots(humans: 2);
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        session.Receive(2, 0, session.BuildKeyCheck(2));

        var rows = Rows(session.Receive(1, 1, LoadingReport(runtime, 55.0f, ready: false)));
        TestAssert.Equal(10, rows.Length, "the echo to both humans plus eight bot rows");
        TestAssert.Equal(new[] { 1L, 2L }, rows.Where(row => row.ClientId == 0).Select(row => row.Connection).Order().ToArray(),
            "the human's own row is relayed to every connected client");
        var botRows = rows.Where(row => row.ClientId >= 2).ToArray();
        TestAssert.Equal(8, botRows.Length);
        TestAssert.True(botRows.All(row => row.Connection == 1 && row.Percent == 100.0f && row.Ready),
            "bot rows go to the reporting client only");
    }

    [Check("network.bot-wire-names-and-creation-flag")]
    public static void BotNamesFollowTheClientLocalizationKeys()
    {
        var data = GameData.Default;
        var shippedKeys = Regex.Matches(File.ReadAllText(data.Paths.Data("Menu/fontconfig_en_US.txt")),
                "(?m)^tr\\s+\"game_bot_([^\"]+)\"").Select(match => match.Groups[1].Value).Order().ToArray();
        TestAssert.Equal(RetailBotKeys.Order().ToArray(), shippedKeys, "the stock client ships the 18 retail bot names");
        foreach (var champion in RetailBotKeys)
            TestAssert.True(data.ClientNamesBot(champion), $"{champion} has a client-side bot name");
        foreach (var champion in BotChampions.Where(champion => champion != "Annie"))
            TestAssert.False(data.ClientNamesBot(champion), $"{champion} has none, the raw key would show");
        TestAssert.Equal("Annie Bot", data.BotName("Annie"));
        TestAssert.Equal("Cho'Gath Bot", data.BotName("Chogath"), "the client's own text");
        TestAssert.Equal("Master Yi Bot", data.BotName("MasterYi"));
        TestAssert.Equal("Lee Sin Bot", data.BotName("LeeSin"), "the retail format from the display name");
        TestAssert.Equal("Nidalee Bot", data.BotName("Nidalee"));
        TestAssert.Equal("Nobody Bot", data.BotName("Nobody"), "an unknown champion falls back to its own name");

        var roster = OneHumanNineBots();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        session.Receive(1, 1, [0xC5]);
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        runtime.World.Clock.SetVirtual(session.Participant(1)!.SpawnAt!.Value);
        var heroes = session.RunSchedulers().Where(item => item.Packet.Plaintext.Span[0] == 0x4F)
            .Select(item => item.Packet.Plaintext).ToArray();
        TestAssert.Equal(10, heroes.Length);
        var production = new ProductionServices(session.World, data, NavigationGrid.LoadDefault(), new ReplicationSyncCounter())
            { Session = session };
        for (var index = 0; index < roster.Length; index++)
        {
            var seat = roster[index];
            var create = heroes[index].Span;
            var flag = (byte)(seat.ChampionName == "Annie" ? 1 : 0);
            TestAssert.Equal(flag, create[16], $"{seat.ChampionName} CreateHero isBot: only a client-named bot carries it");
            TestAssert.Equal(seat.Name, Ascii(create.Slice(23, 40)), $"{seat.ChampionName} CreateHero name");
            TestAssert.Equal(flag, production.BuildCreation(session.Participants[index].Hero!).Single().Plaintext.Span[16],
                $"{seat.ChampionName} replication creation uses the same flag");
            if (seat.IsBot)
                TestAssert.Equal(data.BotName(seat.ChampionName), seat.Name);
        }
        TestAssert.Equal("Annie Bot", Ascii(heroes[1].Span.Slice(23, 40)),
            "the server text equals the client's game_bot_Annie, so both routes read alike");
        TestAssert.Equal("Shaco Bot", Ascii(heroes[2].Span.Slice(23, 40)), "a no-key bot shows the server's retail-format name");
    }
}
