using Amberline.Core;

namespace Amberline.Network.Protocol;

public readonly record struct LeagueLoadingPlayer(
    ulong PlayerId,
    string Name,
    string ChampionName,
    int TeamId,
    string SummonerSpell1 = SummonerLoadout.DefaultFirst,
    string SummonerSpell2 = SummonerLoadout.DefaultSecond,
    int SkinId = 0);

public static class LoadingPackets
{
    /// <summary>Channel-zero registry entry sent after a valid KeyCheck.</summary>
    public static PlaintextPacket RegistryAdd(uint clientId, ulong playerId) =>
        RegistryEntry(0x00, clientId, playerId);

    /// <summary>
    /// Channel-zero registry removal for a seat that left: the same 16-byte entry as
    /// <see cref="RegistryAdd"/> with opcode 1. The stock client's receive path
    /// (packet byte 0 == 1) reads only the client id at offset 4, raises NetApp status event 5 for it
    /// (the status handler's case 5, then the roster handler's case 5: roster state 2, chat line and world
    /// event 54 OnQuit "has left the game!") and then drops the registry entry. Reconstruction: the
    /// player id bytes are filled as in the add entry; retail's removal body beyond offset 4 is not
    /// read by the client.
    /// </summary>
    public static PlaintextPacket RegistryRemove(uint clientId, ulong playerId) =>
        RegistryEntry(0x01, clientId, playerId);

    private static PlaintextPacket RegistryEntry(byte opcode, uint clientId, ulong playerId)
    {
        ProtocolEncoding.RequirePlayerId(playerId);
        using var writer = ProtocolEncoding.Writer(16, out var stream);
        writer.Write(opcode);
        writer.Write(new byte[3]);
        writer.Write(clientId);
        writer.Write(playerId);
        writer.Flush();
        RequireLength(stream, 16, nameof(RegistryEntry));
        return ProtocolEncoding.Packet(0, ProtocolEncoding.Finish(stream), encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket QueryStatusAns()
    {
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.VariantChannel,
            new byte[] { 0x8D, 0, 0, 0, 0, 1 },
            encrypt: false,
            LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket PingLoadInfo(
        uint clientId,
        ulong playerId,
        float percentage = 100.0f,
        float eta = 0.0f,
        short count = 0,
        ushort ping = 0,
        bool ready = true)
    {
        ProtocolEncoding.RequirePlayerId(playerId);
        if (!float.IsFinite(percentage) || !float.IsFinite(eta))
            throw new ArgumentOutOfRangeException(nameof(percentage), "Load values must be finite.");

        using var writer = ProtocolEncoding.Writer(30, out var stream);
        writer.Write((byte)0x9D);
        writer.Write(LeagueProtocolConstants.VariantNetId);
        writer.Write(clientId);
        writer.Write(playerId);
        writer.Write(percentage);
        writer.Write(eta);
        writer.Write(count);
        writer.Write((ushort)(ping & 0x7FFF));
        writer.Write((byte)(ready ? 1 : 0));
        writer.Flush();
        RequireLength(stream, 30, nameof(PingLoadInfo));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.LowPriorityChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Unsequenced);
    }

    // DIAGNOSTICS, unused by production: the Python server's 13-byte reconnect answer with a 64-bit player id. The stock handler of 0x10
    // (the client's packet switch case for it) reads only the 32-bit client id at +5, so this packet would name the low half of the player id as
    // a client; the match sends PlayerReconnected (9 bytes) instead. It stays for the source-backed protocol-basics fixture.
    public static PlaintextPacket ReconnectAck(ulong playerId)
    {
        ProtocolEncoding.RequirePlayerId(playerId);
        using var writer = ProtocolEncoding.Writer(13, out var stream);
        writer.Write((byte)0x10);
        writer.Write(LeagueProtocolConstants.VariantNetId);
        writer.Write(playerId);
        writer.Flush();
        RequireLength(stream, 13, nameof(ReconnectAck));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.VariantChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// Reconnect (0x10) naming a seat that came back. The stock handler
    /// (case 0x10) finds the hero whose owner client id matches, prints
    /// "@srctr name@ has reconnected!" and raises native world event 56 OnReconnect itself,
    /// so the server must NOT also send a 0x4A for it. Send it only to already-loaded
    /// clients: the handler dereferences the HUD, and a matching local client id enters the
    /// client's reconnect mode until 0x49 (Reconnect_Done) arrives.
    /// </summary>
    public static PlaintextPacket PlayerReconnected(uint clientId) =>
        ClientIdNotice(0x10, clientId);

    /// <summary>
    /// Exit (0xA0) naming a seat that quit. The stock handler (case 0xA0)
    /// prints "@srctr name@ has quit!" and raises native world event 55
    /// OnLeave itself, so the server must NOT also send a 0x4A for it.
    /// </summary>
    public static PlaintextPacket PlayerExited(uint clientId) =>
        ClientIdNotice(0xA0, clientId);

    /// <summary>Five-byte header plus the owner client id the stock handlers compare to CreateHero's.</summary>
    private static PlaintextPacket ClientIdNotice(byte opcode, uint clientId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write(opcode);
        writer.Write(LeagueProtocolConstants.VariantNetId);
        writer.Write(clientId);
        writer.Flush();
        RequireLength(stream, 9, nameof(ClientIdNotice));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.VariantChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket TeamRosterUpdate(
        IReadOnlyList<ulong> orderPlayerIds,
        IReadOnlyList<ulong> chaosPlayerIds,
        uint teamSizeOrder = 6,
        uint teamSizeChaos = 6)
    {
        ArgumentNullException.ThrowIfNull(orderPlayerIds);
        ArgumentNullException.ThrowIfNull(chaosPlayerIds);
        ValidateRosterIds(orderPlayerIds, nameof(orderPlayerIds));
        ValidateRosterIds(chaosPlayerIds, nameof(chaosPlayerIds));

        using var writer = ProtocolEncoding.Writer(408, out var stream);
        writer.Write((byte)0x67);
        writer.Write(new byte[3]);
        writer.Write(teamSizeOrder);
        writer.Write(teamSizeChaos);
        writer.Write(new byte[4]);
        WriteRosterSlots(writer, orderPlayerIds);
        WriteRosterSlots(writer, chaosPlayerIds);
        writer.Write((uint)orderPlayerIds.Count);
        writer.Write((uint)chaosPlayerIds.Count);
        writer.Flush();
        RequireLength(stream, 408, nameof(TeamRosterUpdate));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.LoadingChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket RequestReskin(
        ulong playerId,
        string skinName,
        uint skinId = 0)
        => SizedStringPacket(0x65, playerId, skinId, skinName);

    public static PlaintextPacket RequestRename(
        ulong playerId,
        string playerName)
        => SizedStringPacket(0x66, playerId, 0, playerName);

    public static IReadOnlyList<PlaintextPacket> LoadingBurst(
        ulong playerId,
        string playerName = "Player1",
        string championName = "Annie",
        IReadOnlyList<LeagueLoadingPlayer>? players = null)
    {
        var roster = NormalizePlayers(players, playerId, playerName, championName);
        var order = roster.Where(player => player.TeamId == 100).Select(player => player.PlayerId).ToArray();
        var chaos = roster.Where(player => player.TeamId == 200).Select(player => player.PlayerId).ToArray();
        var packets = new List<PlaintextPacket>(1 + roster.Length * 2)
        {
            TeamRosterUpdate(order, chaos)
        };
        foreach (var player in roster)
        {
            packets.Add(RequestReskin(player.PlayerId, player.ChampionName, checked((uint)player.SkinId)));
            packets.Add(RequestRename(player.PlayerId, player.Name));
        }

        return packets;
    }

    public static PlaintextPacket SynchVersionS2C(
        string championName = "Annie",
        IReadOnlyList<LeagueLoadingPlayer>? players = null,
        string version = LeagueProtocolConstants.VersionString,
        int mapId = GameMaps.Normal)
    {
        var roster = NormalizePlayers(players, LeagueProtocolConstants.DefaultPlayerId, "Player1", championName);
        ProtocolEncoding.RequireAsciiLength(version, 255, nameof(version));
        // The client loads LEVELS\Map<mapId> from this int: 1 Summoner's Rift, 2 the Harrowing rift, 3 the Basic Tutorial, 4 Twisted Treeline, 8 The Crystal Scar.
        if (!GameMaps.IsSupported(mapId)) throw new ArgumentOutOfRangeException(nameof(mapId));

        using var writer = ProtocolEncoding.Writer(1438, out var stream);
        writer.Write((byte)0x57);
        writer.Write(LeagueProtocolConstants.VariantNetId);
        writer.Write((byte)1);
        writer.Write(mapId);
        foreach (var player in roster)
            WritePlayerLiteInfo(writer, player);
        for (var index = roster.Length; index < 12; index++)
            WriteEmptyPlayerLiteInfo(writer);
        ProtocolEncoding.WriteFixedAscii(writer, version, 256, 255);
        ProtocolEncoding.WriteFixedAscii(writer, GameMaps.ModeOf(mapId), 128, 127);
        writer.Flush();
        RequireLength(stream, 1438, nameof(SynchVersionS2C));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.VariantChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket WorldSendGameNumber(ulong gameNumber = 1)
    {
        using var writer = ProtocolEncoding.Writer(13, out var stream);
        writer.Write((byte)0x98);
        writer.Write(LeagueProtocolConstants.VariantNetId);
        writer.Write(gameNumber);
        writer.Flush();
        RequireLength(stream, 13, nameof(WorldSendGameNumber));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.VariantChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    private static PlaintextPacket SizedStringPacket(
        byte opcode,
        ulong playerId,
        uint skinId,
        string value)
    {
        ProtocolEncoding.RequirePlayerId(playerId);
        ArgumentNullException.ThrowIfNull(value);
        using var writer = ProtocolEncoding.Writer(32, out var stream);
        writer.Write(opcode);
        writer.Write(new byte[7]);
        writer.Write(playerId);
        writer.Write(skinId);
        ProtocolEncoding.WriteSizedAscii(writer, value);
        writer.Write((byte)0);
        writer.Flush();
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.LoadingChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable);
    }

    private static LeagueLoadingPlayer[] NormalizePlayers(
        IReadOnlyList<LeagueLoadingPlayer>? players,
        ulong defaultPlayerId,
        string defaultName,
        string defaultChampion)
    {
        var normalized = players?.ToArray() ?? new[]
        {
            new LeagueLoadingPlayer(defaultPlayerId, defaultName, defaultChampion, 100)
        };
        if (normalized.Length is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(players), "A loading roster must contain 1 to 12 players.");

        var ids = new HashSet<ulong>();
        for (var index = 0; index < normalized.Length; index++)
        {
            var player = normalized[index];
            ProtocolEncoding.RequirePlayerId(player.PlayerId);
            if (!ids.Add(player.PlayerId))
                throw new ArgumentException("A loading roster cannot repeat a player ID.", nameof(players));
            if (player.TeamId is not (100 or 200))
                throw new ArgumentOutOfRangeException(nameof(players), "Loading teams must be 100 or 200.");
            ArgumentNullException.ThrowIfNull(player.Name);
            ArgumentNullException.ThrowIfNull(player.ChampionName);
            if (player.SkinId < 0) throw new ArgumentOutOfRangeException(nameof(players), "Skin IDs must be nonnegative.");
            var loadout = SummonerLoadout.ValidatePair(player.SummonerSpell1, player.SummonerSpell2);
            normalized[index] = player with
            {
                SummonerSpell1 = loadout.First,
                SummonerSpell2 = loadout.Second,
            };
        }

        return normalized;
    }

    private static void ValidateRosterIds(IReadOnlyList<ulong> ids, string parameterName)
    {
        if (ids.Count > 24)
            throw new ArgumentOutOfRangeException(parameterName, "A roster section has at most 24 slots.");
        foreach (var id in ids)
        {
            if (id != 0)
                ProtocolEncoding.RequirePlayerId(id, parameterName);
        }
    }

    private static void WriteRosterSlots(BinaryWriter writer, IReadOnlyList<ulong> ids)
    {
        for (var index = 0; index < 24; index++)
            writer.Write(index < ids.Count ? ids[index] : 0UL);
    }

    private static void WritePlayerLiteInfo(BinaryWriter writer, LeagueLoadingPlayer player)
    {
        ProtocolEncoding.RequirePlayerId(player.PlayerId);
        writer.Write(player.PlayerId);
        writer.Write((ushort)30);
        writer.Write(ProtocolEncoding.HashString(player.SummonerSpell1));
        writer.Write(ProtocolEncoding.HashString(player.SummonerSpell2));
        writer.Write((byte)0);
        writer.Write(player.TeamId);
        ProtocolEncoding.WriteFixedAscii(writer, player.Name, 28, 27);
        ProtocolEncoding.WriteFixedAscii(writer, player.ChampionName, 28, 27);
        writer.Write(0);
        writer.Write(0);
    }

    private static void WriteEmptyPlayerLiteInfo(BinaryWriter writer)
    {
        writer.Write(ulong.MaxValue);
        writer.Write(new byte[79]);
    }

    private static void RequireLength(MemoryStream stream, int expected, string name)
    {
        if (stream.Length != expected)
            throw new InvalidOperationException($"{name} must be {expected} bytes, got {stream.Length}.");
    }
}
