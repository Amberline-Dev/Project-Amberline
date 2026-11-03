using Amberline.Core;

namespace Amberline.Network.Protocol;

public readonly record struct SpawnMinionSnapshot(
    uint NetId,
    byte NodeId,
    Position2 Position,
    int Team,
    string Name,
    string SkinName,
    bool IsWard = false,
    bool IgnoreCollision = false,
    int SkinId = 0,
    float VisibilitySize = 0.0f);

public sealed record SpawnPetSnapshot(
    uint OwnerNetId,
    uint NetId,
    byte NodeId,
    Position2 Position,
    string Name,
    string SkinName,
    uint CloneNetId = 0,
    int SkinId = 0,
    int Rank = 1,
    float Duration = 0.0f,
    int DamageBonus = 0,
    int HealthBonus = 0,
    string BuffName = "",
    bool CopyInventory = true,
    bool ClearFocusTarget = true,
    string AiScript = "Pet.lua",
    bool ShowMinimapIcon = false);

public readonly record struct NeutralSpawnSnapshot(
    uint NetObjId,
    byte NetNodeId,
    Position3 Position,
    Position3 GroupPosition,
    Position3 Facing,
    string Name,
    string SkinName,
    string UniqueName,
    string MinimapIcon,
    int Team,
    int DamageBonus,
    int HealthBonus,
    int RoamState,
    int GroupNumber,
    bool BehaviorTree = false);

public readonly record struct PerceptionBubble(
    uint BubbleType,
    Position3 Position,
    float Radius,
    float Ttl,
    uint BubbleId,
    uint ClientNetId = 0,
    uint Flags = 0,
    uint RevealSpecificNetworkId = 0);

public static class SpawnPackets
{
    public const byte VariantChannel = LeagueProtocolConstants.VariantChannel;

    public static PlaintextPacket StartSpawn(int orderBots = 0, int chaosBots = 0)
    {
        if (orderBots is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(orderBots));
        if (chaosBots is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(chaosBots));
        return Global(new byte[] { 0x65, 0, 0, 0, 0, (byte)orderBots, (byte)chaosBots }, encrypt: false);
    }

    public static PlaintextPacket CreateHero(
        uint netObjId,
        ulong playerUid,
        string heroName,
        string skinName,
        bool teamIsOrder,
        int skinId = 0,
        byte skillLevel = 1,
        byte spawnPositionIndex = 0,
        byte netNodeId = 1,
        bool isBot = false,
        byte botRank = 0)
    {
        using var writer = ProtocolEncoding.Writer(103, out var stream);
        writer.Write((byte)0x4F);
        writer.Write(0u);
        writer.Write(netObjId);
        writer.Write(unchecked((uint)playerUid));
        writer.Write(netNodeId);
        writer.Write(skillLevel);
        writer.Write((byte)(teamIsOrder ? 1 : 0));
        writer.Write((byte)(isBot ? 1 : 0));
        writer.Write(botRank);
        writer.Write(spawnPositionIndex);
        writer.Write(skinId);
        ProtocolEncoding.WriteFixedAscii(writer, heroName, 40, 39);
        ProtocolEncoding.WriteFixedAscii(writer, skinName, 40, 39);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable,
            netObjId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { Create(netObjId) });
    }

    public static PlaintextPacket CreateTurret(
        uint netObjId, byte netNodeId, string name)
    {
        using var writer = ProtocolEncoding.Writer(74, out var stream);
        writer.Write((byte)0xA5);
        writer.Write(0u);
        writer.Write(netObjId);
        writer.Write(netNodeId);
        ProtocolEncoding.WriteFixedAscii(writer, name, 64, 63);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable,
            netObjId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { Create(netObjId) });
    }

    public static PlaintextPacket SpawnMinion(
        SpawnMinionSnapshot minion,
        Func<double, double, double> heightProvider)
    {
        ArgumentNullException.ThrowIfNull(heightProvider);
        return SpawnMinion(minion, heightProvider(minion.Position.X, minion.Position.Z));
    }

    public static PlaintextPacket SpawnMinion(
        SpawnMinionSnapshot minion, double terrainHeight)
    {
        if (!double.IsFinite(terrainHeight) || terrainHeight < float.MinValue || terrainHeight > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(terrainHeight));
        using var writer = ProtocolEncoding.Writer(167, out var stream);
        writer.Write((byte)0x80);
        writer.Write(0u);
        writer.Write(minion.NetId);
        writer.Write(minion.NodeId);
        writer.Write(ProtocolEncoding.Float32(minion.Position.X, nameof(minion)));
        writer.Write(ProtocolEncoding.Float32(terrainHeight, nameof(terrainHeight)));
        writer.Write(ProtocolEncoding.Float32(minion.Position.Z, nameof(minion)));
        writer.Write(minion.SkinId);
        writer.Write(0u);
        writer.Write(unchecked((uint)minion.Team) & 0x1FFu);
        writer.Write(minion.VisibilitySize);
        writer.Write((byte)((minion.IsWard ? 2 : 0) | (minion.IgnoreCollision ? 1 : 0)));
        ProtocolEncoding.WriteFixedAscii(writer, minion.Name, 64, 63);
        ProtocolEncoding.WriteFixedAscii(writer, minion.SkinName, 64, 63);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Create(minion.NetId));
    }

    public static PlaintextPacket SpawnPet(
        SpawnPetSnapshot pet,
        Func<double, double, double> heightProvider)
    {
        ArgumentNullException.ThrowIfNull(heightProvider);
        return SpawnPet(pet, heightProvider(pet.Position.X, pet.Position.Z));
    }

    public static PlaintextPacket SpawnPet(SpawnPetSnapshot pet, double terrainHeight)
    {
        if (!double.IsFinite(terrainHeight) || terrainHeight < float.MinValue || terrainHeight > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(terrainHeight));
        using var writer = ProtocolEncoding.Writer(208, out var stream);
        writer.Write((byte)0x3C);
        writer.Write(pet.OwnerNetId);
        writer.Write(pet.NetId);
        writer.Write(pet.NodeId);
        writer.Write(ProtocolEncoding.Float32(pet.Position.X, nameof(pet)));
        writer.Write(ProtocolEncoding.Float32(terrainHeight, nameof(terrainHeight)));
        writer.Write(ProtocolEncoding.Float32(pet.Position.Z, nameof(pet)));
        writer.Write(pet.Rank);
        writer.Write(pet.Duration);
        writer.Write(pet.DamageBonus);
        writer.Write(pet.HealthBonus);
        ProtocolEncoding.WriteFixedAscii(writer, pet.Name, 32, 31);
        ProtocolEncoding.WriteFixedAscii(writer, pet.SkinName, 32, 31);
        writer.Write(pet.SkinId);
        ProtocolEncoding.WriteFixedAscii(writer, pet.BuffName, 64, 63);
        writer.Write(pet.CloneNetId);
        writer.Write((byte)((pet.CopyInventory ? 1 : 0) | (pet.ClearFocusTarget ? 2 : 0)));
        ProtocolEncoding.WriteFixedAscii(writer, pet.AiScript, 32, 31);
        writer.Write((byte)(pet.ShowMinimapIcon ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable,
            Create(pet.NetId), pet.OwnerNetId == 0 ? default : Hero(pet.OwnerNetId));
    }

    public static PlaintextPacket CreateNeutral(NeutralSpawnSnapshot neutral)
    {
        using var writer = ProtocolEncoding.Writer(323, out var stream);
        writer.Write((byte)0x66);
        writer.Write(0u);
        writer.Write(neutral.NetObjId);
        writer.Write(neutral.NetNodeId);
        WritePosition(writer, neutral.Position);
        WritePosition(writer, neutral.GroupPosition);
        WritePosition(writer, neutral.Facing);
        ProtocolEncoding.WriteFixedAscii(writer, neutral.Name, 64, 63);
        ProtocolEncoding.WriteFixedAscii(writer, neutral.SkinName, 64, 63);
        ProtocolEncoding.WriteFixedAscii(writer, neutral.UniqueName, 64, 63);
        ProtocolEncoding.WriteFixedAscii(writer, neutral.MinimapIcon, 64, 63);
        writer.Write(neutral.Team);
        writer.Write(neutral.DamageBonus);
        writer.Write(neutral.HealthBonus);
        writer.Write(neutral.RoamState);
        writer.Write(neutral.GroupNumber);
        writer.Write((byte)(neutral.BehaviorTree ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Create(neutral.NetObjId));
    }

    /// <summary>
    /// Batch 511: UnitSetMinimapIcon (0xE1 in the 126 packet switch; 73 bytes): <c>targetNetID</c> at +5, <c>iconName</c> char[64] at +9.
    /// The client finds the unit by +5 and keeps the name as the unit's own minimap icon. The minimap draws that minimap.ini section for the unit
    /// with its team's texture, only while the unit is seen and drawn (visible and NoRender clear).
    /// </summary>
    public static PlaintextPacket UnitSetMinimapIcon(uint unitNetId, string iconName)
    {
        using var writer = ProtocolEncoding.Writer(73, out var stream);
        writer.Write((byte)0xE1);
        writer.Write(0u);
        writer.Write(unitNetId);
        ProtocolEncoding.WriteFixedAscii(writer, iconName, 64, 63);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Target(unitNetId));
    }

    /// <summary>
    /// Batch 673: UnitChangeTeam (0xE0 in the 126 packet switch; 13 bytes): <c>targetNetID</c> at +5, the native <c>team</c> (int) at +9.
    /// Proven in the stock client: the switch sends 0xE0 to, which finds the unit by +5 (the AttackableUnit manager's find,
    ///; an unknown id is a no-op) and calls its SetTeam (vtable +0x98) with +9. SetTeam stores the team at +0x1C and moves the
    /// unit between the teams' lists. The client layout: the same fields; the client numbers it 226 (the range shifted by 2, as 0xE1).
    /// </summary>
    public static PlaintextPacket UnitChangeTeam(uint unitNetId, Team team)
    {
        using var writer = ProtocolEncoding.Writer(13, out var stream);
        writer.Write((byte)0xE0);
        writer.Write(0u);
        writer.Write(unitNetId);
        writer.Write(TeamMapping.ToNative(team));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Target(unitNetId));
    }

    public static PlaintextPacket NeutralCampEmpty(
        uint playerClientId, int campIndex, bool state = false)
    {
        using var writer = ProtocolEncoding.Writer(14, out var stream);
        writer.Write((byte)0xCB);
        writer.Write(0u);
        writer.Write(playerClientId);
        writer.Write(campIndex);
        writer.Write((byte)(state ? 1 : 0));
        writer.Flush();
        // +5 is the authenticated player/client identity consumed by the
        // native camp handler. It is not an entity reference, so attaching a
        // Hero(playerClientId) dependency makes normal observer routing reject
        // a valid packet (and confuses ClientId with a NetId).
        return Packet(stream, LeaguePacketReliability.Reliable,
            Array.Empty<LeaguePacketEntityReference>());
    }

    // DIAGNOSTICS, unused by production: AllTurrets (with the position table at the end of the class) is the Python server's one-shot
    // turret burst. The match sends turrets through SessionSpawnService.BuildMapReadyBurst; it stays only because the source-backed
    // fixture of protocol.gameplay replays its bytes. (The map-bubble bursts, including the fountain lighting workaround retired on
    // 14 September 2026, were deleted on 18 October 2026.)
    public static IReadOnlyList<PlaintextPacket> AllTurrets(
        IReadOnlyList<(uint NetId, string Placement, byte NodeId)> turrets)
    {
        ArgumentNullException.ThrowIfNull(turrets);
        return turrets.Select(turret => CreateTurret(turret.NetId, turret.NodeId, turret.Placement + "_A")).ToArray();
    }

    public static IReadOnlyList<PlaintextPacket> AllTurrets(
        IReadOnlyList<uint> netObjIds)
    {
        ArgumentNullException.ThrowIfNull(netObjIds);
        if (netObjIds.Count != TurretNames.Length)
            throw new ArgumentException("Exactly 24 turret IDs are required.", nameof(netObjIds));
        return netObjIds.Select((id, index) =>
            CreateTurret(id, 0x40, TurretNames[index] + "_A")).ToArray();
    }

    public static PlaintextPacket EndSpawn()
        => Global(new byte[] { 0x12, 0, 0, 0, 0 }, encrypt: false);

    public static PlaintextPacket StartGame()
        => Global(new byte[] { 0x5F, 0, 0, 0, 0, 0 }, encrypt: false);

    public static PlaintextPacket GameClock(float gameTime = 0.0f)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0xC9);
        writer.Write(0u);
        writer.Write(gameTime);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// SyncMissionStartTime (0xCA): sets the HUD clock origin. The stock HUD
    /// reads <c>SlaveClock.t - origin</c>; the origin starts at -1 and the client latches it to its own <c>t</c> on
    /// the first game-loop iteration. A 0xC9 that lands before that iteration, as on a
    /// reconnect, would therefore become the HUD zero. This packet sets it explicitly, in the clock domain of
    /// <see cref="GameClock"/>.
    /// </summary>
    public static PlaintextPacket SyncMissionStartTime(float startTime = 0.0f)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0xCA);
        writer.Write(0u);
        writer.Write(startTime);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket SwitchNexusesToIdleParticles()
        => Global(new byte[] { 0x04, 0, 0, 0, 0 }, encrypt: false);

    public static PlaintextPacket ToggleFow()
        => Global(new byte[] { 0x82, 0, 0, 0, 0 }, encrypt: false);

    /// <summary>
    /// AddPerceptionBubble (0x7B, 49 bytes): the client's packet switch reads the type at +5 (a 0-based
    /// team index, <see cref="PerceptionBubble.BubbleType"/>), the radius at +13, the position at +17, the lifetime in seconds at +29, the
    /// bubble id at +33 and a 12-byte flags block at +37 (flag bits, revealSpecificNetworkID, unitThatCanSeeNetworkID). The client never
    /// reads +9. Until 18 October 2026 the packet was 41 bytes and the two ids were read past its end.
    /// </summary>
    public static PlaintextPacket AddPerceptionBubble(PerceptionBubble bubble)
    {
        using var writer = ProtocolEncoding.Writer(49, out var stream);
        writer.Write((byte)0x7B);
        writer.Write(0u);
        writer.Write(bubble.BubbleType);
        writer.Write(bubble.ClientNetId);
        writer.Write(bubble.Radius);
        WritePosition(writer, bubble.Position);
        writer.Write(bubble.Ttl);
        writer.Write(bubble.BubbleId);
        writer.Write(bubble.Flags);
        writer.Write(bubble.RevealSpecificNetworkId); // revealSpecificNetworkID: the one unit the bubble reveals (RevealSpecificUnitOnly), else 0
        writer.Write(0u); // unitThatCanSeeNetworkID
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// AddUnitPerceptionBubble (0x26, 41 bytes): type at +5, radius at +13, the unit at +17 (an unknown unit drops the packet),
    /// lifetime at +21, bubble id at +25 and the same 12-byte flags block at +29 (the wrapper ORs in IsUnit, bit 2). +9 is never read.
    /// Until 18 October 2026 the packet was 33 bytes.
    /// </summary>
    public static PlaintextPacket AddUnitPerceptionBubble(
        uint unitNetId, float radius, float ttl, uint bubbleId,
        uint bubbleType = 0, uint clientNetId = 0, uint flags = 0, uint revealSpecificNetworkId = 0)
    {
        using var writer = ProtocolEncoding.Writer(41, out var stream);
        writer.Write((byte)0x26);
        writer.Write(0u);
        writer.Write(bubbleType);
        writer.Write(clientNetId);
        writer.Write(radius);
        writer.Write(unitNetId);
        writer.Write(ttl);
        writer.Write(bubbleId);
        writer.Write(flags);
        writer.Write(revealSpecificNetworkId); // the one unit the bubble reveals (RevealSpecificUnitOnly), else 0
        writer.Write(0u); // unitThatCanSeeNetworkID
        writer.Flush();
        var refs = unitNetId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { Target(unitNetId) };
        return Packet(stream, LeaguePacketReliability.Reliable, refs);
    }

    public static PlaintextPacket RemovePerceptionBubble(uint bubbleId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x38);
        writer.Write(0u);
        writer.Write(bubbleId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// SyncSimTimeFinal (0x79): the server's answer to the client's 0x08 reply to a 0xC9. The client reads
    /// two floats: <paramref name="timeLastClient"/> (+5, the <c>time_LastClient</c> of its 0x08) and <paramref name="roundTrip"/>
    /// (+9, the full round trip in the 0xC9 clock domain). It folds them into its latency average
    /// (<c>average = 0.8 * average + 0.1 * roundTrip</c>), which the next 0xC9 adds to the server time it sets the clock to. +13
    /// (<c>time_convergence</c>) is never read.
    /// </summary>
    public static PlaintextPacket SyncSimTimeFinal(float timeLastClient, float roundTrip)
    {
        using var writer = ProtocolEncoding.Writer(17, out var stream);
        writer.Write((byte)0x79);
        writer.Write(0u);
        writer.Write(timeLastClient);
        writer.Write(roundTrip);
        writer.Write(0.0f);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// 0x78 on channel 0. NOT sent by the session any more: the stock client's live receive path never reads it (its only
    /// <c>'x'</c> check is the replay sink); the local client id comes from the channel-0 registry add
    /// (<see cref="LoadingPackets.RegistryAdd"/>). Kept for the source-backed fixtures.
    /// </summary>
    public static PlaintextPacket SetLocalClientId(uint clientId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)'x');
        writer.Write(0u);
        writer.Write(clientId);
        writer.Flush();
        return ProtocolEncoding.Packet(0, ProtocolEncoding.Finish(stream), encrypt: true, LeaguePacketReliability.Reliable);
    }

    private static PlaintextPacket Global(byte[] payload, bool encrypt)
        => ProtocolEncoding.Packet(VariantChannel, payload, encrypt, LeaguePacketReliability.Reliable);

    private static PlaintextPacket Packet(
        MemoryStream stream,
        LeaguePacketReliability reliability,
        params LeaguePacketEntityReference[] references)
        => ProtocolEncoding.Packet(VariantChannel, ProtocolEncoding.Finish(stream), encrypt: true, reliability, references);

    private static void WritePosition(BinaryWriter writer, Position3 position)
    {
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
    }

    private static LeaguePacketEntityReference Hero(uint id) => new(LeaguePacketEntityRole.Hero, id);
    private static LeaguePacketEntityReference Target(uint id) => new(LeaguePacketEntityRole.Target, id);
    private static LeaguePacketEntityReference Create(uint id) => new(LeaguePacketEntityRole.Create, id);

    // The turret spawn packet names the scene turret and carries no position; the client places it at its mesh (MapDefinitions.TurretPositions
    // holds the server's copy of those positions). Order of the twenty-four names is the wire order.
    private static readonly string[] TurretNames =
    {
        "Turret_T1_C_01", "Turret_T1_C_02", "Turret_T1_C_03", "Turret_T1_C_04", "Turret_T1_C_05", "Turret_T1_C_06",
        "Turret_T1_C_07", "Turret_T1_L_02", "Turret_T1_L_03", "Turret_T1_R_02", "Turret_T1_R_03", "Turret_OrderTurretShrine",
        "Turret_T2_C_01", "Turret_T2_C_02", "Turret_T2_C_03", "Turret_T2_C_04", "Turret_T2_C_05", "Turret_T2_L_01",
        "Turret_T2_L_02", "Turret_T2_L_03", "Turret_T2_R_01", "Turret_T2_R_02", "Turret_T2_R_03", "Turret_ChaosTurretShrine"
    };
}
