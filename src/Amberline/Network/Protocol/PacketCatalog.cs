using System.Buffers.Binary;

namespace Amberline.Network.Protocol;

/// <summary>A run of fixed-size records whose first field is a net ID (spell cast targets, tooltip rows, teleport legs).</summary>
public readonly record struct PacketRepeat(int CountOffset, int CountWidth, int First, int Stride, int FieldOffset);

/// <summary>
/// One server-to-client packet kind: the channel and delivery the server's own builders use,
/// and where the net IDs sit in the plaintext. All offsets are absolute plaintext offsets.
/// </summary>
public sealed class PacketSpec
{
    // Arrays, iterated by index: the checker reads these once per packet and must not allocate.
    private readonly int[] _references;
    private readonly int[] _creations;
    private readonly PacketRepeat[] _repeats;

    public PacketSpec(byte opcode, byte channel, bool reliable, string name,
        int[] references, int[] creations, PacketRepeat[] repeats)
    {
        Opcode = opcode;
        Channel = channel;
        Reliable = reliable;
        Name = name;
        _references = references;
        _creations = creations;
        _repeats = repeats;
    }

    public byte Opcode { get; }
    public byte Channel { get; }
    public bool Reliable { get; }
    public string Name { get; }
    public IReadOnlyList<int> References => _references;
    public IReadOnlyList<int> Creations => _creations;
    public IReadOnlyList<PacketRepeat> Repeats => _repeats;

    /// <summary>
    /// Copies the non-zero net IDs this packet refers to (a unit it must already have created for
    /// the receiver) into <paramref name="destination"/>, in layout order; returns how many were
    /// written. Fields past the end of a short packet are skipped, so a truncated packet never throws.
    /// </summary>
    public int ReadReferences(ReadOnlySpan<byte> payload, Span<uint> destination)
    {
        var count = 0;
        for (var index = 0; index < _references.Length; index++)
            if (TryRead(payload, _references[index], out var id) && count < destination.Length)
                destination[count++] = id;
        for (var index = 0; index < _repeats.Length; index++)
        {
            var repeat = _repeats[index];
            if (!TryCount(payload, repeat, out var rows)) continue;
            for (var row = 0; row < rows; row++)
                if (TryRead(payload, repeat.First + row * repeat.Stride + repeat.FieldOffset, out var id) &&
                    count < destination.Length)
                    destination[count++] = id;
        }
        return count;
    }

    /// <summary>Copies the non-zero net IDs this packet creates for the receiver (spawn packets).</summary>
    public int ReadCreations(ReadOnlySpan<byte> payload, Span<uint> destination)
    {
        var count = 0;
        for (var index = 0; index < _creations.Length; index++)
            if (TryRead(payload, _creations[index], out var id) && count < destination.Length)
                destination[count++] = id;
        return count;
    }

    private static bool TryRead(ReadOnlySpan<byte> payload, int offset, out uint id)
    {
        id = 0;
        if (offset < 0 || offset + 4 > payload.Length) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        return id != 0;
    }

    private static bool TryCount(ReadOnlySpan<byte> payload, PacketRepeat repeat, out int rows)
    {
        rows = 0;
        if (repeat.CountOffset + repeat.CountWidth > payload.Length) return false;
        rows = repeat.CountWidth == 1 ? payload[repeat.CountOffset]
            : BinaryPrimitives.ReadUInt16LittleEndian(payload[repeat.CountOffset..]);
        return true;
    }
}

/// <summary>
/// DIAGNOSTICS: a diagnostics-only description of the server's own S2C packet surface, used by the
/// headless packet-stream checker (<c>Amberline.Tools</c> <c>PacketStreamChecker</c>). The stock
/// client has no such table; the channel map and layouts below are what this server's builders emit
/// (channel numbers: <see cref="LeagueProtocolConstants"/>; chat: <see cref="ChatPackets"/>), and the
/// <c>packet-rules.catalog-matches-builders</c> check pins every row to the real builders, so the
/// table cannot drift from them silently. The channel map is documented in docs/testing.md (Packet-stream rules): 0
/// handshake, 3 gameplay S2C, 4 low-priority movement, 5 chat, 6 loading; 1 and 2 are client to server (2 carries the
/// client's clock reply, DuelSession.cs:710-713). Opcode 0xCC (replication) is parsed by the checker itself.
/// </summary>
public sealed class PacketCatalog
{
    private const byte Handshake = 0;
    private const byte Gameplay = LeagueProtocolConstants.VariantChannel;
    private const byte Movement = LeagueProtocolConstants.LowPriorityChannel;
    private const byte Loading = LeagueProtocolConstants.LoadingChannel;
    private static readonly int[] None = [];
    private static readonly PacketRepeat[] NoRepeats = [];

    private readonly PacketSpec[]?[] _byOpcode = new PacketSpec[256][];
    private readonly List<PacketSpec> _all = [];

    /// <summary>The catalog of every packet kind the Network/Protocol builders produce.</summary>
    public static PacketCatalog Default { get; } = Build();

    public IReadOnlyList<PacketSpec> Specs => _all;

    /// <summary>True for the chat channel, whose plaintext is a bare envelope with no opcode byte.</summary>
    public static bool IsChatChannel(byte channel) => channel == ChatPackets.Channel;

    /// <summary>The chat envelope's delivery: reliable (<see cref="ChatPackets.Envelope"/>).</summary>
    public static bool ChatReliable => true;

    public bool KnowsOpcode(byte opcode) => _byOpcode[opcode] is not null;

    public bool TryGet(byte opcode, byte channel, out PacketSpec spec)
    {
        if (_byOpcode[opcode] is { } specs)
            foreach (var candidate in specs)
                if (candidate.Channel == channel) { spec = candidate; return true; }
        spec = null!;
        return false;
    }

    /// <summary>The channels the catalog lists for an opcode, for diagnostics.</summary>
    public string ChannelsOf(byte opcode) => _byOpcode[opcode] is { } specs
        ? string.Join(",", specs.Select(spec => spec.Channel)) : "none";

    private void Add(PacketSpec spec)
    {
        var existing = _byOpcode[spec.Opcode] ?? [];
        if (existing.Any(other => other.Channel == spec.Channel))
            throw new InvalidOperationException($"duplicate catalog row 0x{spec.Opcode:X2} on channel {spec.Channel}");
        _byOpcode[spec.Opcode] = [.. existing, spec];
        _all.Add(spec);
    }

    private void Row(byte opcode, string name, byte channel = Gameplay, bool reliable = true,
        int[]? refs = null, int[]? creates = null, PacketRepeat[]? repeats = null) =>
        Add(new PacketSpec(opcode, channel, reliable, name, refs ?? None, creates ?? None, repeats ?? NoRepeats));

    private static PacketCatalog Build()
    {
        var c = new PacketCatalog();
        // Offsets: 1 is the usual unit header netid, 5 the second field of the two-id packets.
        int[] one = [1], two = [1, 5];

        // --- Channel 0, handshake (LoadingPackets.RegistryAdd / RegistryRemove, SpawnPackets.SetLocalClientId) ---
        c.Row(0x00, "RegistryAdd", Handshake);
        c.Row(0x01, "RegistryRemove", Handshake);
        c.Row(0x78, "SetLocalClientId", Handshake);

        // --- Channel 6, loading (LoadingPackets.TeamRosterUpdate / RequestReskin / RequestRename) ---
        c.Row(0x65, "RequestReskin", Loading);
        c.Row(0x66, "RequestRename", Loading);
        c.Row(0x67, "TeamRosterUpdate", Loading);

        // --- Channel 4, low priority movement (VisionPackets.WaypointList / Dash / Teleport, PingLoadInfo) ---
        c.Row(0xC1, "WaypointList", Movement, refs: one);
        c.Row(0x67, "WaypointGroupWithSpeed", Movement, refs: [15, 36]);
        c.Row(0x64, "WaypointGroup", Movement, repeats: [new(9, 2, 11, 13, 4)]);
        c.Row(0x9D, "PingLoadInfo", Movement, reliable: false); // LeaguePacketReliability.Unsequenced

        // --- Channel 3, creation: the receiver learns the unit here ---
        c.Row(0x4F, "CreateHero", creates: [5]);
        c.Row(0xA5, "CreateTurret", creates: [5]);
        c.Row(0x80, "SpawnMinion", creates: [5]);
        c.Row(0x3C, "SpawnPet", refs: one, creates: [5]);
        c.Row(0x66, "CreateNeutral", creates: [5]);
        c.Row(0xE1, "UnitSetMinimapIcon", refs: [5]); // batch 511: SpawnPackets.UnitSetMinimapIcon, the unit at 5

        // --- Channel 3, visibility and perception ---
        c.Row(0xC2, "EnterVisibility", refs: one);
        c.Row(0xB5, "EnterLocalVisibility", refs: one);
        c.Row(0x54, "LeaveVisibility", refs: one);
        c.Row(0x3A, "LeaveLocalVisibility", refs: one);
        c.Row(0xEE, "EnterTeamVisibility", refs: one);
        c.Row(0xEF, "LeaveTeamVisibility", refs: one);
        c.Row(0x7B, "AddPerceptionBubble");
        c.Row(0x26, "AddUnitPerceptionBubble", refs: [17]);
        c.Row(0x38, "RemovePerceptionBubble");

        // --- Channel 3, combat. 0x0D and 0x68 are the only unreliable rows (CombatPackets) ---
        c.Row(0x0D, "BasicAttack", reliable: false, refs: two);
        c.Row(0x68, "ApplyDamage", reliable: false, refs: [1, 6, 10]);
        c.Row(0x39, "InstantStopAttack", refs: one);
        c.Row(0x25, "UnitAddGold", refs: [1, 5, 9]);
        c.Row(0x45, "LevelUp", refs: one);
        c.Row(0x84, "UnitApplyHeal", refs: one);
        c.Row(0x61, "NpcDie", refs: two);
        c.Row(0xA6, "NpcDie", refs: two);
        c.Row(0x24, "DeathRecap", refs: two);
        c.Row(0x1E, "ForceDead", refs: one);
        c.Row(0xD7, "ShowHealthBar", refs: one);
        c.Row(0x8E, "BuildingDie", refs: two);
        c.Row(0xAB, "OnEntityEvent", refs: [1, 6]);
        c.Row(0x4A, "OnEventWorld", refs: [6, 10]);
        c.Row(0x4D, "HeroReincarnate", refs: one);
        c.Row(0x6D, "AiSetTarget", refs: two);
        c.Row(0xC8, "AiSetTargetHero", refs: two);
        c.Row(0x2D, "DampenerState", refs: one);

        // --- Channel 3, replication (parsed by the checker) ---
        c.Row(0xCC, "OnReplication");

        // --- Channel 3, spells, buffs, effects, animation (SpellPackets, FxPackets) ---
        // 0xBD carries the cast info at 9, 0x41 at 98; in it the caster is at offset 0x0F, the target count
        // at offset 0x2F and 17-byte target rows from offset 0x30.
        c.Row(0xBD, "CastSpellAns", refs: [1, 9 + 0x0F], repeats: [new(9 + 0x2F, 1, 9 + 0x30, 17, 0)]);
        c.Row(0x41, "MissileReplication", refs: [1, 98 + 0x0F], repeats: [new(98 + 0x2F, 1, 98 + 0x30, 17, 0)]);
        c.Row(0x5D, "DestroyClientMissile");
        // FromID (1) is the missile's own ID, which the checker does not track; the owner (9) and every hop must be known units.
        c.Row(0x6F, "ChainMissileSync", refs: [9], repeats: [new(5, 4, 13, 4, 0)]);
        // FromID (1) is the line missile's own ID, as for 0x6F; every listed unit must be known (batch 591).
        c.Row(0x28, "LineMissileHitList", repeats: [new(5, 2, 7, 4, 0)]);
        c.Row(0x18, "UpgradeSpellAns", refs: one);
        c.Row(0x8A, "SetCooldown", refs: one);
        c.Row(0x73, "SetSpellData", refs: two);
        c.Row(0x35, "ChangeSlotSpellName", refs: one);
        c.Row(0x1A, "ChangeSlotSpellType", refs: one);
        c.Row(0x37, "ChangeSlotSpellOffsetTarget", refs: [1, 6]);
        c.Row(0x22, "SetAutocast", refs: one);
        c.Row(0x83, "ToolTipVars", repeats: [new(5, 2, 7, 17, 0)]);
        c.Row(0x32, "BuffReplace", refs: [1, 14]);
        c.Row(0x1F, "BuffUpdateCount", refs: [1, 15]);
        c.Row(0xBF, "BuffAdd2", refs: [1, 21]);
        c.Row(0x7F, "BuffRemove2", refs: one);
        c.Row(0x69, "ModifyShield", refs: one);
        c.Row(0x8C, "FXCreateGroup", refs: [1, 21, 29]);
        c.Row(0x3D, "FXKill", refs: one);
        c.Row(0x9F, "ChangeCharacterData", refs: one);
        c.Row(0x6A, "PopCharacterData", refs: one);
        c.Row(0xBA, "PushCharacterFade", refs: one);
        c.Row(0x34, "PopCharacterFade", refs: one);
        c.Row(0x6E, "SetAnimStates", refs: one);
        c.Row(0x53, "FaceDirection", refs: one);
        c.Row(0x74, "PauseAnimation", refs: one);
        c.Row(0x5E, "LevelUpSpell", refs: one);
        c.Row(0x9E, "ChangeCharacterVoice", refs: one);
        c.Row(0x99, "ChangePARColorOverride", refs: two);
        c.Row(0xB8, "PlayAnimation", refs: one);
        c.Row(0x2B, "StopAnimation", refs: one);

        // --- Channel 3, hero state, items, camera, social (ItemsPackets and friends) ---
        c.Row(0x2C, "AvatarInfo", refs: one);
        c.Row(0x4B, "ClassicHeroStats", refs: one);
        c.Row(0x72, "BuyItemAns", refs: one);
        c.Row(0x0B, "RemoveItemAns", refs: one);
        c.Row(0x44, "SwapItemAns", refs: one);
        c.Row(0xA7, "UseItemAns", refs: one);
        c.Row(0x56, "SetItem", refs: one);
        c.Row(0x76, "SetCameraPosition", refs: one);
        c.Row(0x7C, "LockCamera", refs: one);
        c.Row(0x27, "MoveCameraToPoint", refs: one);
        c.Row(0xB2, "SetGreyscaleEnabledWhenDead", refs: one);
        c.Row(0x48, "PlayEmote", refs: one);
        c.Row(0x46, "MapPing", refs: [1, 17, 21]);
        c.Row(0xD2, "SurrenderVote", refs: [6]);
        c.Row(0xAD, "SurrenderStatus");
        c.Row(0xA9, "Pause");
        c.Row(0x0A, "Resume");
        c.Row(0x02, "DisplayLocalizedTutorialChatText");
        c.Row(0xC0, "OpenAFKWarningMessage");
        // Batch 563: the Basic Tutorial's client packets (TutorialPackets; cases of the 126 packet switch).
        c.Row(TutorialPackets.ShowObjectiveTextOpcode, "ShowObjectiveText");
        c.Row(TutorialPackets.RefreshObjectiveTextOpcode, "RefreshObjectiveText");
        c.Row(TutorialPackets.HideObjectiveTextOpcode, "HideObjectiveText");
        c.Row(TutorialPackets.ShowAuxiliaryTextOpcode, "ShowAuxiliaryText");
        c.Row(TutorialPackets.RefreshAuxiliaryTextOpcode, "RefreshAuxiliaryText");
        c.Row(TutorialPackets.HideAuxiliaryTextOpcode, "HideAuxiliaryText");
        c.Row(TutorialPackets.OpenTutorialPopupOpcode, "OpenTutorialPopup");
        c.Row(TutorialPackets.PlayVoAudioEventOpcode, "PlayVOAudioEvent");
        c.Row(TutorialPackets.ToggleInputLockingFlagOpcode, "ToggleInputLockingFlag");
        c.Row(TutorialPackets.MuteVolumeCategoryOpcode, "MuteVolumeCategory");
        // Batch 580: the tutorial's highlights (the unit ones name the unit at 5).
        c.Row(TutorialPackets.ToggleUiHighlightOpcode, "ToggleUIHighlight");
        c.Row(TutorialPackets.HighlightHudElementOpcode, "HighlightHUDElement");
        c.Row(TutorialPackets.CreateUnitHighlightOpcode, "CreateUnitHighlight", refs: [5]);
        c.Row(TutorialPackets.RemoveUnitHighlightOpcode, "RemoveUnitHighlight", refs: [5]);
        c.Row(TutorialPackets.CloseShopOpcode, "CloseShop");

        // --- Channel 3, match lifecycle and world (SpawnPackets, CombatPackets, LoadingPackets) ---
        c.Row(0x65, "StartSpawn");
        c.Row(0x12, "EndSpawn");
        c.Row(0x5F, "StartGame");
        c.Row(0xC9, "GameClock");
        c.Row(0x79, "SyncSimTimeFinal");
        c.Row(0xCA, "SyncMissionStartTime");
        c.Row(0x82, "ToggleFoW");
        c.Row(0x04, "SwitchNexusesToIdleParticles");
        c.Row(0xCB, "NeutralCampEmpty");
        c.Row(0x77, "SetNexusSkin");
        c.Row(0x89, "SetInputLockingFlag");
        c.Row(0x62, "FadeOutMainSfx");
        c.Row(0xE4, "ColorRemapFX");
        c.Row(0xD4, "FadeMinions");
        c.Row(0xC4, "DisableHUDForEndOfGame");
        c.Row(0xCD, "EndOfGameEvent");
        c.Row(0xCE, "EndGame");
        // Dominion capture and score (DominionPackets, batch 445). No references: the keys are Reconstruction.
        c.Row(0xD9, "SpawnLevelProp"); // batch 477: case 0xd9 of the 126 packet switch (LevelPropPackets)
        c.Row(0xDA, "CapturePropFlexValue");
        c.Row(0xDB, "CapturePointEvent");
        c.Row(0xDC, "CapturePointValue");
        c.Row(0xDD, "SetGameScore");
        c.Row(0xDE, "RespawnPointCommand");
        c.Row(0xE2, "IncrementPlayerScore"); // batch 518: DominionPackets.IncrementPlayerScore
        c.Row(0xE3, "IncrementPlayerStat");
        c.Row(0x90, "HandleQuestUpdate");
        c.Row(0x1C, "DisplayFloatingText", refs: [5]); // batch 505: DominionPackets.FloatingText, the target at 5
        c.Row(0x8D, "QueryStatusAns");
        c.Row(0x10, "ReconnectAck");
        c.Row(0xA0, "PlayerExited");
        c.Row(0x57, "SynchVersionS2C");
        c.Row(0x98, "WorldSendGameNumber");
        return c;
    }
}
