using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Amberline.Network.Protocol;

namespace Amberline.Network.Diagnostics;

/// <summary>
/// Read-only inspection of the maintained plaintext application packets.
/// This mirrors the source wire decoder's important contract: malformed or
/// unknown packets never escape the diagnostic boundary, and undecoded
/// packets retain their complete raw bytes for comparison.
/// </summary>
public static class WirePacketDecoder
{
    private static readonly IReadOnlyDictionary<byte, string> Names =
        new Dictionary<byte, string>
        {
            [0x04] = "SwitchNexusesToIdleParticles", [0x65] = "Ambiguous_0x65",
            [0x4F] = "CreateHero", [0xA5] = "CreateTurret", [0x12] = "EndSpawn",
            [0x5F] = "StartGame", [0xC9] = "GameClock", [0x79] = "SyncSimTimeFinal", [0xCA] = "SyncMissionStartTime", [0x82] = "ToggleFoW",
            [0x7B] = "AddPerceptionBubble", [0x38] = "RemovePerceptionBubble",
            [0x2C] = "AvatarInfo", [0xB5] = "EnterLocalVisibility",
            [0xC2] = "EnterVisibility", [0x54] = "LeaveVisibility",
            [0x3A] = "LeaveLocalVisibility", [0xEE] = "EnterTeamVis",
            [0xEF] = "LeaveTeamVis", [0x8D] = "QueryStatusAns", [0x9D] = "PingLoadInfo",
            [0x10] = "ReconnectAck", [0xA0] = "PlayerExited", [0x67] = "Ambiguous_0x67", [0x57] = "SynchVersionS2C",
            [0x98] = "WorldSendGameNumber", [0x78] = "SetLocalClientID",
            [0x2D] = "DampenerSwitchStates", [0x6D] = "AI_SetTarget", [0xC8] = "AI_SetTargetHero",
            [0x8E] = "BuildingDie", [0x4A] = "OnEventWorld", [0x4D] = "HeroReincarnate", [0x32] = "BuffReplace",
            [0x24] = "NpcDieEventHistory",
            [0xC4] = "DisableHUDForEndOfGame", [0xCD] = "EndOfGameEvent", [0xCE] = "EndGame",
            [0x18] = "UpgradeSpellAns", [0x8C] = "FXCreateGroup", [0x3D] = "FXKill",
            [0x66] = "Ambiguous_0x66", [0xCB] = "NeutralCampEmpty", [0xE0] = "UnitChangeTeam", [0xE1] = "UnitSetMinimapIcon", [0x0D] = "BasicAttack",
            [0x39] = "InstantStopAttack", [0x26] = "AddUnitPerceptionBubble", [0x74] = "PauseAnimation", [0x5E] = "LevelUpSpell", [0x9E] = "ChangeCharacterVoice", [0x99] = "ChangePARColorOverride", [0xE4] = "ColorRemapFX",
            [0x83] = "ToolTipVars", [0x68] = "ApplyDamage", [0x25] = "AddGold", [0x45] = "LevelUp",
            [0x61] = "Die", [0xA6] = "Die", [0x72] = "BuyItemAns", [0x0B] = "RemoveItemAns",
            [0x44] = "SwapItemAns", [0xA7] = "UseItemAns", [0x56] = "SetItem", [0x8A] = "SetCooldown",
            [0x73] = "SetSpellData", [0xBF] = "BuffAdd2", [0x7F] = "BuffRemove2",
            [0x1F] = "BuffUpdateCount", [0x84] = "UnitApplyHeal",
            [0x35] = "ChangeSlotSpellName", [0x53] = "FaceDirection",
            [0x37] = "ChangeSlotSpellOffsetTarget", [0xD7] = "ShowHealthBar",
            [0x9F] = "ChangeCharacterData", [0x6A] = "PopCharacterData",
            [0x6E] = "SetAnimStates",
            [0x22] = "SetAutocast",
            [0x5D] = "DestroyClientMissile", [0x28] = "LineMissileHitList", [0x69] = "ModifyShield", [0xBD] = "CastSpellAns",
            [0x41] = "MissileReplication", [0x80] = "SpawnMinion", [0xCC] = "OnReplication",
            [0xC1] = "WaypointList", [0x64] = "WaypointGroup", [0x48] = "PlayEmote",
            [0xB8] = "PlayAnimation", [0x2B] = "StopAnimation",
            [0xBA] = "PushCharacterFade", [0x34] = "PopCharacterFade",
            [0xAB] = "OnEntityEvent",
            [0xA9] = "Pause", [0x0A] = "Resume",
            [PingPackets.ServerOpcode] = "MapPing",
            [AfkPackets.LocalizedChatTextOpcode] = "DisplayLocalizedTutorialChatText",
            [AfkPackets.OpenWarningMessageOpcode] = "OpenAFKWarningMessage",
        };

    private static readonly HashSet<byte> Decoded =
    [
        0x0D, 0x39, 0x26, 0x74, 0x68, 0x25, 0x45, 0x61, 0xA6,
        0x72, 0x0B, 0x44, 0xA7, 0x56, 0x8A, 0x73, 0x35, 0xBF, 0x7F, 0x1F, 0x84,
        0x53, 0x37, 0x9F, 0x6A, 0x6E, 0x22, 0x5D, 0x28, 0x69, 0xBD, 0x41, 0x80,
        0x8C, 0x66, 0xCB, 0xE0, 0xE1, 0x83, 0x3D, 0xCC, 0xC1, 0xC2, 0x64, 0x48, 0xAB, 0xA9, 0x0A,
        // Batch 360 (the spell sweep): the unit pairs, the death and the presentation packets whose builders the rules read.
        0x6D, 0xC8, 0x8E, 0xD7, 0x5E, 0x2B, 0x9E, 0x32, 0xE4, 0xB8, 0x4A,
        PingPackets.ServerOpcode,
    ];

    public sealed record Result(
        byte? Opcode,
        string Name,
        int Length,
        IReadOnlyDictionary<string, object?> Fields,
        bool HasDecoder,
        string Raw);

    /// <summary>Packet-log naming, including the maintained static name table.
    /// Direct <see cref="Decode"/> intentionally follows wire.py and reports
    /// Unknown for static names without a registered field decoder.</summary>
    public static string LogName(byte? opcode, byte channel)
    {
        if (opcode is null) return "Unknown";
        if (opcode == 0x48 && channel == 4) return "UnparsedMovementChannel";
        if (opcode == 0x67 && channel == 4) return "WaypointGroupWithSpeed";
        return Names.TryGetValue(opcode.Value, out var name) ? name : $"Unknown_0x{opcode.Value:X2}";
    }

    public static Result Decode(ReadOnlySpan<byte> payload, byte channel)
    {
        if (payload.Length == 0)
            return new(null, "Unknown", 0, new Dictionary<string, object?>(), false, string.Empty);
        var opcode = payload[0];
        // Channel 4 carries compressed movement data; an incidental 0x48
        // prefix there is not the channel-3 PlayEmote packet.
        if (opcode == 0x48 && channel == 4)
            return new(opcode, "UnparsedMovementChannel", payload.Length,
                new Dictionary<string, object?>(), false,
                Convert.ToHexString(payload).ToLowerInvariant());
        // `wire.decode` deliberately reports Unknown for static-table names
        // that have no registered decoder. Keep the one channel-dependent
        // ambiguity explicit because the source decoder returns it directly.
        var name = "Unknown";
        // 0x67 is a loading-channel roster packet unless it is on the
        // movement channel, where the registered dash decoder applies.
        var movementDash = opcode == 0x67 && channel == 4;
        var hasDecoder = (Decoded.Contains(opcode) || movementDash) &&
            !(opcode == 0x67 && channel != 4);
        if (movementDash) name = "WaypointGroupWithSpeed";
        else if (opcode == 0x67 && channel != 4) name = "Ambiguous_0x67";
        else if (hasDecoder && Names.TryGetValue(opcode, out var knownName)) name = knownName;
        var bytes = payload.ToArray();
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (hasDecoder)
        {
            try { fields = DecodeFields(opcode, bytes); }
            catch (Exception) { fields.Clear(); }
        }
        return new(opcode, name, payload.Length, fields, hasDecoder,
            Convert.ToHexString(payload).ToLowerInvariant());
    }

    public static Result DecodeWire(ReadOnlySpan<byte> ciphertext, byte channel,
        LegacyBlowfishCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        try { return Decode(cipher.Decrypt(ciphertext), channel); }
        catch (Exception)
        {
            return new(null, "Unknown", ciphertext.Length,
                new Dictionary<string, object?>(), false,
                Convert.ToHexString(ciphertext).ToLowerInvariant());
        }
    }

    private static Dictionary<string, object?> DecodeAnimationStates(byte[] payload)
    {
        if (payload.Length < 6) throw new InvalidDataException("Animation header is truncated.");
        var offset = 6;
        string ReadName()
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;
            if (size < 0 || size > payload.Length - offset) throw new InvalidDataException("Animation name is truncated.");
            var name = Encoding.ASCII.GetString(payload, offset, size);
            offset += size;
            return name;
        }
        var states = new List<object>();
        for (var i = 0; i < payload[5]; i++) states.Add(new { original = ReadName(), replacement = ReadName() });
        // Encrypted captures retain up to seven zero padding bytes.
        if (payload.Length - offset > 7 || payload.AsSpan(offset).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Trailing animation bytes.");
        return new() { ["net_id"] = U32(payload, 1), ["states"] = states };
    }

    private static Dictionary<string, object?> DecodeFields(byte opcode, byte[] p) => opcode switch
    {
        0x6E => DecodeAnimationStates(p),
        0x22 => Fixed(p, 6, (d, o) => new() { ["netid"] = U32(d, 1), ["slot"] = unchecked((sbyte)d[5]) }),
        PingPackets.ServerOpcode => DecodeMapPing(p),
        0xA9 => Fixed(p, 14, (d, o) => new() { ["client_id"] = U32(d, 5),
            ["seconds_remaining"] = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(9)),
            ["tournament"] = d[13] }),
        0x0A => Fixed(p, 10, (d, o) => new() { ["client_id"] = U32(d, 5), ["delayed"] = d[9] }),
        0xAB => Fixed(p, 10, (d, o) => new() { ["source_netid"] = U32(d, 1),
            ["event_id"] = d[5], ["other_netid"] = U32(d, 6) }),
        0x48 => Fixed(p, 9, (d, o) => new() { ["hero_netid"] = U32(d, 1), ["emote_id"] = U32(d, 5) }),
        0x0D => Fixed(p, 15, (d, o) => new()
        {
            ["attacker_netid"] = U32(d, 1), ["target_netid"] = U32(d, 5),
            ["extra_time_secs"] = (d[9] - 0x80) / 100.0, ["missile_id"] = U32(d, 10), ["slot"] = d[14],
        }),
        0x39 => Fixed(p, 6, (d, o) => new() { ["attacker_netid"] = U32(d, 1), ["flags"] = d[5] }),
        0x26 => Fixed(p, 33, (d, o) => new()
        {
            ["bubble_type"] = U32(d, 5), ["client_netid"] = U32(d, 9), ["radius"] = F32(d, 13),
            ["unit_netid"] = U32(d, 17), ["ttl"] = F32(d, 21), ["bubble_id"] = U32(d, 25), ["flags"] = U32(d, 29),
        }),
        0x74 => Fixed(p, 6, (d, o) => new() { ["unit_netid"] = U32(d, 1), ["paused"] = d[5] != 0 }),
        0x68 => Fixed(p, 18, (d, o) => new()
        {
            ["source_netid"] = U32(d, 10), ["target_netid"] = U32(d, 6),
            ["damage"] = F32(d, 14), ["type_byte"] = d[5],
        }),
        0x25 => Fixed(p, 17, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 5), ["killed_netid"] = U32(d, 9), ["gold"] = F32(d, 13),
        }),
        0x45 => Fixed(p, 7, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["level"] = d[5], ["ability_points"] = d[6],
        }),
        0x61 or 0xA6 => Fixed(p, 16, (d, o) => new()
        {
            ["dying_netid"] = U32(d, 1), ["killer_netid"] = U32(d, 5), ["dtype"] = d[9],
            ["stype"] = d[10], ["death_duration"] = F32(d, 11), ["become_zombie"] = d[15],
        }),
        0x72 => Fixed(p, 12, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["slot"] = d[5], ["item_id"] = U32(d, 6),
            ["items_in_slot"] = d[10], ["use_on_bought"] = d[11] != 0,
        }),
        0x0B => Fixed(p, 7, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["slot"] = d[5], ["items_in_slot"] = d[6],
        }),
        0x44 => Fixed(p, 7, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["source_slot"] = d[5], ["destination_slot"] = d[6],
        }),
        0xA7 => Fixed(p, 8, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["slot"] = d[5], ["items_in_slot"] = d[6], ["charges"] = d[7],
        }),
        0x56 => Fixed(p, 12, (d, o) => new()
        {
            ["hero_netid"] = U32(d, 1), ["slot"] = d[5], ["item_id"] = U32(d, 6),
            ["items_in_slot"] = d[10], ["charges"] = d[11],
        }),
        0x8A => Fixed(p, 10, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["slot"] = d[5] & 0x7F, ["is_summoner"] = (d[5] & 0x80) != 0,
            ["cooldown"] = F32(d, 6),
        }),
        0x73 => Fixed(p, 14, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["spell_hash"] = U32(d, 9), ["slot"] = d[13],
        }),
        0x35 => DecodeSlotSpellName(p),
        0xBF => Fixed(p, 25, (d, o) => new()
        {
            ["target_netid"] = U32(d, 1), ["slot"] = d[5], ["buff_type"] = d[6], ["count"] = d[7],
            ["is_hidden"] = d[8], ["buff_name_hash"] = U32(d, 9), ["running_time"] = F32(d, 13),
            ["duration"] = F32(d, 17), ["caster_netid"] = U32(d, 21),
        }),
        0x7F => Fixed(p, 10, (d, o) => new()
        {
            ["target_netid"] = U32(d, 1), ["slot"] = d[5], ["buff_name_hash"] = U32(d, 6),
        }),
        0x1F => Fixed(p, 19, (d, o) => new()
        {
            ["target_netid"] = U32(d, 1), ["slot"] = d[5], ["count"] = d[6],
            ["duration"] = F32(d, 7), ["running_time"] = F32(d, 11), ["caster_netid"] = U32(d, 15),
        }),
        0x84 => Fixed(p, 13, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["max_health"] = F32(d, 5), ["heal"] = F32(d, 9),
        }),
        0x53 => Fixed(p, 17, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["direction"] = Vec3(d, 5),
        }),
        0x37 => Fixed(p, 10, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["slot"] = d[5] & 0x7F, ["summoner"] = (d[5] & 0x80) != 0,
            ["target_netid"] = U32(d, 6),
        }),
        // The pop by id (9 bytes) or BBPopAllCharacterData's pop-all (5 bytes, no change id).
        0x6A => p.Length >= 9 ? Fixed(p, 9, (d, o) => new() { ["netid"] = U32(d, 1), ["change_id"] = U32(d, 5) })
            : Fixed(p, 5, (d, o) => new() { ["netid"] = U32(d, 1), ["pop_all"] = true }),
        0x5D => Fixed(p, 5, (d, o) => new() { ["missile_netid"] = U32(d, 1) }),
        0x28 => Fixed(p, 7, (d, o) => new()
        {
            ["missile_netid"] = U32(d, 1),
            ["targets"] = Enumerable.Range(0, Math.Clamp((int)I16(d, 5), 0, (d.Length - 7) / 4)).Select(i => (object?)U32(d, 7 + 4 * i)).ToArray(),
        }),
        0x69 => Fixed(p, 10, (d, o) => new()
        {
            ["netid"] = U32(d, 1), ["amount"] = F32(d, 6), ["physical"] = (d[5] & 1) != 0,
            ["magic"] = (d[5] & 2) != 0, ["no_fade"] = (d[5] & 4) != 0,
        }),
        0x80 => DecodeSpawnMinion(p),
        0xCC => DecodeReplication(p),
        0xC1 => DecodeWaypoints(p),
        0xC2 => DecodeEnterVisibility(p),
        0x3D => Fixed(p, 9, (d, o) => new()
        {
            ["header_netid"] = U32(d, 1), ["effect_netid"] = U32(d, 5),
        }),
        0x8C => DecodeFxCreateGroup(p),
        // Layouts of the builders (batch 360): SimplePair (0x6D, 0xC8), BuildingDie, ShowHealthBar, LevelUpSpell, StopAnimation,
        // ChangeCharacterVoice, RenewBuff (catalog name BuffReplace), ColorRemapFx.
        0x6D or 0xC8 => Fixed(p, 9, (d, o) => new() { ["unit_netid"] = U32(d, 1), ["target_netid"] = U32(d, 5) }),
        0x8E => Fixed(p, 9, (d, o) => new() { ["building_netid"] = U32(d, 1), ["killer_netid"] = U32(d, 5) }),
        0xD7 => Fixed(p, 6, (d, o) => new() { ["netid"] = U32(d, 1), ["show"] = d[5] != 0 }),
        0x5E => Fixed(p, 9, (d, o) => new() { ["netid"] = U32(d, 1), ["slot"] = I32(d, 5) }),
        0x2B => Fixed(p, 9, (d, o) => new() { ["netid"] = U32(d, 1), ["flags"] = U32(d, 5), ["lock"] = (U32(d, 5) & 0x40) != 0 }),
        0x9E => Fixed(p, 70, (d, o) => new() { ["netid"] = U32(d, 1), ["reset"] = d[5] != 0, ["voice"] = CString(d, 6, 64) }),
        0x32 => Fixed(p, 18, (d, o) => new()
        {
            ["target_netid"] = U32(d, 1), ["slot"] = d[5], ["running_time"] = F32(d, 6), ["duration"] = F32(d, 10),
            ["caster_netid"] = U32(d, 14),
        }),
        0xE4 => Fixed(p, 22, (d, o) => new()
        {
            ["fade_in"] = d[5] != 0, ["fade_time"] = F32(d, 6), ["team"] = I32(d, 10), ["color"] = U32(d, 14), ["max_weight"] = F32(d, 18),
        }),
        0xB8 => DecodePlayAnimation(p),
        0x4A => DecodeOnEventWorld(p),
        0x66 => DecodeAmbiguous66(p),
        0xE0 => Fixed(p, 13, (d, o) => new() { ["target_netid"] = U32(d, 5), ["team"] = I32(d, 9) }),
        0xE1 => Fixed(p, 73, (d, o) => new() { ["target_netid"] = U32(d, 5), ["icon"] = CString(d, 9, 64) }),
        0xCB => Fixed(p, 14, (d, o) => new()
        {
            ["player_netid"] = U32(d, 5), ["camp_index"] = I32(d, 9),
            ["state"] = d[13] != 0,
        }),
        0x83 => DecodeToolTipVars(p),
        0x9F => DecodeChangeCharacterData(p),
        0xBD => DecodeCastSpellAns(p),
        0x41 => DecodeMissileReplication(p),
        0x64 => DecodeWaypointGroup(p),
        0x67 => DecodeWaypointGroupWithSpeed(p),
        _ => new(),
    };

    private static Dictionary<string, object?> DecodeSlotSpellName(byte[] p)
    {
        if (!SpellPackets.TryReadChangeSlotSpellName(p, out var update)) return new();
        return new Dictionary<string, object?>
        {
            ["netid"] = update.NetId, ["slot"] = update.Slot,
            ["is_summoner"] = update.IsSummoner, ["spell_name"] = update.SpellName,
        };
    }

    // PlayAnimation (0xB8, builder SpellPackets.PlayAnimation): [netid u32][flags u32][duration f32][NUL-terminated name at 13].
    // The flags are the playback bits the handler reads: loop 2, replace 8, lock 0x40, no blending 0x200.
    private static Dictionary<string, object?> DecodePlayAnimation(byte[] p) => Fixed(p, 14, (d, o) => new()
    {
        ["netid"] = U32(d, 1), ["flags"] = U32(d, 5), ["loop"] = (U32(d, 5) & 2) != 0, ["replace"] = (U32(d, 5) & 8) != 0,
        ["lock"] = (U32(d, 5) & 0x40) != 0, ["blend"] = (U32(d, 5) & 0x200) == 0, ["duration"] = F32(d, 9), ["name"] = CString(d, 13, 64),
    });

    // OnEventWorld (0x4A, builder CombatPackets.OnEventWorld): [0 u32][event u8][source netid u32][other netid u32], and a map number i32
    // at 14 in the 18-byte form.
    private static Dictionary<string, object?> DecodeOnEventWorld(byte[] p) => Fixed(p, 14, (d, o) => new()
    {
        ["event_id"] = d[5], ["source_netid"] = U32(d, 6), ["other_netid"] = U32(d, 10),
        ["map_number"] = d.Length >= 18 ? (object)I32(d, 14) : null,
    });

    // A NUL-terminated ASCII name starting at offset, at most max bytes (the builders pad nothing after the terminator).
    private static string CString(byte[] p, int offset, int max)
    {
        var end = offset;
        while (end < p.Length && end < offset + max && p[end] != 0) end++;
        return Encoding.ASCII.GetString(p, offset, Math.Max(0, end - offset));
    }

    private static Dictionary<string, object?> DecodeMapPing(byte[] p)
    {
        if (!PingPackets.TryReadNotification(p, out var ping)) return new();
        return new()
        {
            ["sender_netid"] = ping.SenderNetId, ["source_netid"] = ping.SourceNetId,
            ["position"] = new[] { ping.Position.X, ping.Position.Y, ping.Position.Z },
            ["target_netid"] = ping.TargetNetId, ["category"] = (byte)ping.Category,
            ["play_audio"] = ping.PlayAudio, ["show_chat"] = ping.ShowChat,
            ["ping_throttled"] = ping.PingThrottled,
        };
    }

    private static Dictionary<string, object?> DecodeFxCreateGroup(byte[] p)
    {
        if (p.Length < 6) return new();
        var groups = new List<object?>();
        var offset = 6;
        for (var groupIndex = 0; groupIndex < p[5]; groupIndex++)
        {
            if (offset + 15 > p.Length) return new();
            var effectNameHash = U32(p, offset);
            var flags = U16(p, offset + 4);
            var targetBone = U32(p, offset + 6);
            var bone = U32(p, offset + 10);
            var count = p[offset + 14];
            offset += 15;
            var effects = new List<object?>();
            for (var effectIndex = 0; effectIndex < count; effectIndex++)
            {
                if (offset + 52 > p.Length) return new();
                effects.Add(new Dictionary<string, object?>
                {
                    ["target_netid"] = U32(p, offset),
                    ["net_assigned_id"] = U32(p, offset + 4),
                    ["bind_netid"] = U32(p, offset + 8),
                    ["position_compressed"] = CompressedPosition(p, offset + 12),
                    ["target_position_compressed"] = CompressedPosition(p, offset + 20),
                    ["owner_position_compressed"] = CompressedPosition(p, offset + 28),
                    ["direction"] = Vec3(p, offset + 36),
                    ["time_spent"] = F32(p, offset + 48),
                });
                offset += 52;
            }
            groups.Add(new Dictionary<string, object?>
            {
                ["effect_name_hash"] = effectNameHash, ["flags"] = flags,
                ["target_bone_name_hash"] = targetBone, ["bone_name_hash"] = bone,
                ["effects"] = effects.ToArray(),
            });
        }
        return new() { ["header_netid"] = U32(p, 1), ["groups"] = groups.ToArray() };
    }

    private static Dictionary<string, object?> DecodeChangeCharacterData(byte[] p)
    {
        if (p.Length < 10) return new();
        return new()
        {
            ["netid"] = U32(p, 1), ["change_id"] = U32(p, 5),
            ["use_spells"] = p[9] != 0,
            ["skin_name"] = p.Length <= 10 ? string.Empty : CStr(p, 10, Math.Min(64, p.Length - 10)),
        };
    }

    private static Dictionary<string, object?> DecodeAmbiguous66(byte[] p)
    {
        if (p.Length < 323)
            return new() { ["packet_kind"] = "RequestRename" };
        return new()
        {
            ["packet_kind"] = "CreateNeutral", ["net_obj_id"] = U32(p, 5),
            ["net_node_id"] = p[9], ["pos"] = Vec3(p, 10),
            ["group_pos"] = Vec3(p, 22), ["face_pos"] = Vec3(p, 34),
            ["character_name"] = CStr(p, 46, 64), ["skin_name"] = CStr(p, 110, 64),
            ["unique_name"] = CStr(p, 174, 64), ["minimap_icon"] = CStr(p, 238, 64),
            ["team"] = I32(p, 302), ["damage_bonus"] = I32(p, 306),
            ["health_bonus"] = I32(p, 310), ["roam_state"] = I32(p, 314),
            ["group_number"] = I32(p, 318), ["behavior_tree"] = (p[322] & 1) != 0,
        };
    }

    private static Dictionary<string, object?> DecodeToolTipVars(byte[] p)
    {
        if (p.Length < 7) return new();
        var count = U16(p, 5);
        if (p.Length < 7 + 17 * count) return new();
        var entries = new List<object?>((int)count);
        for (var i = 0; i < count; i++)
        {
            var o = 7 + 17 * i;
            var selector = p[o + 4];
            var selectorKind = selector < 0x3C ? "buff" :
                selector < 0x78 ? "spellbook" : "alternate_spellbook";
            var slot = selector < 0x3C ? selector :
                selector < 0x78 ? selector - 0x3C : selector - 0x78;
            entries.Add(new Dictionary<string, object?>
            {
                ["netid"] = U32(p, o), ["selector"] = selector,
                ["selector_kind"] = selectorKind, ["slot"] = slot,
                ["f1"] = F32(p, o + 5), ["f2"] = F32(p, o + 9), ["f3"] = F32(p, o + 13),
            });
        }
        return new() { ["entries"] = entries.ToArray() };
    }

    private static Dictionary<string, object?> DecodeCastSpellAns(byte[] p)
    {
        if (p.Length < 9) return new();
        var castInfo = DecodeCastInfo(p, 9);
        if (castInfo.Count == 0) return new();
        return new()
        {
            ["caster_netid"] = U32(p, 1), ["caster_pos_sync_id"] = I32(p, 5),
            ["cast_info"] = castInfo,
        };
    }

    private static Dictionary<string, object?> DecodeMissileReplication(byte[] p)
    {
        const int prefix = 5 + 7 * 12 + 4 + 4 + 1;
        if (p.Length < prefix) return new();
        var result = new Dictionary<string, object?> { ["caster_netid"] = U32(p, 1) };
        var offset = 5;
        foreach (var name in new[] { "position", "caster_position", "direction", "velocity", "start_point", "end_point", "unit_position" })
        {
            result[name] = Vec3(p, offset);
            offset += 12;
        }
        result["speed"] = F32(p, offset); offset += 4;
        result["life_pct"] = F32(p, offset); offset += 4;
        result["bounced"] = p[offset]; offset++;
        var castInfo = DecodeCastInfo(p, offset);
        if (castInfo.Count == 0) return new();
        result["cast_info"] = castInfo;
        return result;
    }

    private static Dictionary<string, object?> DecodeCastInfo(byte[] p, int baseOffset)
    {
        const int header = 0x30;
        if (baseOffset < 0 || baseOffset + header > p.Length) return new();
        var blobSize = U16(p, baseOffset);
        var spellLevel = p[baseOffset + 0x0A];
        var targetCount = p[baseOffset + 0x2F];
        var offset = baseOffset + header;
        var targets = new List<object?>((int)targetCount);
        for (var i = 0; i < targetCount; i++)
        {
            if (offset + 17 > p.Length) return new();
            targets.Add(new Dictionary<string, object?>
            {
                ["netid"] = U32(p, offset), ["pos"] = Vec3(p, offset + 4), ["hit_result"] = p[offset + 16],
            });
            offset += 17;
        }
        const int tail = 0x26;
        if (offset + tail > p.Length) return new();
        return new()
        {
            ["blob_size"] = blobSize, ["spell_hash"] = U32(p, baseOffset + 2),
            ["spell_netid"] = U32(p, baseOffset + 6), ["spell_level"] = spellLevel,
            ["rank"] = spellLevel + 1, ["attack_speed_modifier"] = F32(p, baseOffset + 0x0B),
            ["caster_netid"] = U32(p, baseOffset + 0x0F), ["missile_netid"] = U32(p, baseOffset + 0x13),
            ["target_pos"] = Vec3(p, baseOffset + 0x17), ["target_pos_end"] = Vec3(p, baseOffset + 0x23),
            ["num_targets"] = targetCount, ["targets"] = targets.ToArray(),
            ["designer_cast_time"] = F32(p, offset), ["extra_cast_time"] = F32(p, offset + 4),
            ["designer_total_time"] = F32(p, offset + 8), ["cooldown"] = F32(p, offset + 12),
            ["start_cast_time"] = F32(p, offset + 16), ["flags"] = p[offset + 20],
            ["slot"] = p[offset + 21], ["mana_cost"] = F32(p, offset + 22),
            ["launch_pos"] = Vec3(p, offset + 26),
        };
    }

    private static Dictionary<string, object?> DecodeWaypointGroupWithSpeed(byte[] p)
    {
        if (p.Length < 11) return new();
        var syncId = U32(p, 5);
        var movements = I16(p, 9);
        if (movements < 1 || movements > 64) return new();
        var offset = 11;
        var entries = new List<object?>();
        for (var i = 0; i < movements; i++)
        {
            if (DecodeDashEntry(p, ref offset) is not { } dash) return new();
            entries.Add(dash);
        }
        if (p.Length - offset > 7 || p.AsSpan(offset).IndexOfAnyExcept((byte)0) >= 0) return new();
        var result = new Dictionary<string, object?> { ["sync_id"] = syncId, ["movements"] = movements, ["entries"] = entries.ToArray() };
        if (entries.Count == 1 && entries[0] is Dictionary<string, object?> entry)
            foreach (var pair in entry) result[pair.Key] = pair.Value;
        return result;
    }

    // One 0x67 entry: [header u32: count in bits 16-22, bit 0 the teleport flag][net id][teleport id u8 when flagged], the
    // 0x21 bytes of speed data, then the points.
    private static Dictionary<string, object?>? DecodeDashEntry(byte[] p, ref int offset)
    {
        if (offset + 8 > p.Length) return null;
        var teleport = (U16(p, offset) & 1) != 0;
        var count = U16(p, offset + 2) & 0x7F;
        var unit = U32(p, offset + 4);
        offset += 8;
        byte? teleportId = null;
        if (teleport)
        {
            if (offset + 1 > p.Length) return null;
            teleportId = p[offset++];
        }
        if (offset + 33 > p.Length) return null;
        var speed = F32(p, offset); var gravity = F32(p, offset + 4);
        var start = new[] { F32(p, offset + 8), F32(p, offset + 12) };
        var facing = p[offset + 16]; var follow = U32(p, offset + 17);
        var followDistance = F32(p, offset + 21); var backDistance = F32(p, offset + 25);
        var travelTime = F32(p, offset + 29);
        offset += 33;
        var waypoints = DecodeCompressedWaypoints(p, ref offset, count);
        if (waypoints is null) return null;
        var entry = new Dictionary<string, object?>
        {
            ["unit_netid"] = unit, ["follow_netid"] = follow, ["speed"] = speed,
            ["gravity"] = gravity, ["start"] = start, ["facing"] = facing,
            ["follow_distance"] = followDistance, ["back_distance"] = backDistance,
            ["travel_time"] = travelTime, ["waypoints"] = waypoints,
        };
        if (teleportId is byte id) entry["teleport_id"] = id;
        return entry;
    }

    private static Dictionary<string, object?> DecodeWaypointGroup(byte[] p)
    {
        if (p.Length < 11) return new();
        var syncId = I32(p, 5); var movements = I16(p, 9);
        if (movements < 1) return new();
        var offset = 11; var waypoints = new List<object?>(); uint? teleportNetId = null; byte? teleportId = null;
        for (var i = 0; i < movements; i++)
        {
            if (offset + 4 > p.Length) return new();
            var header = U32(p, offset); offset += 4;
            if (((header >> 16) & 0x7F) == 0) continue;
            if (DecodeWaypointEntry(p, ref offset, header) is not { } entry) return new();
            (teleportNetId, teleportId) = (entry.NetId, entry.TeleportId);
            waypoints.AddRange(entry.Points);
        }
        return new()
        {
            ["sync_id"] = syncId, ["movements"] = movements, ["teleport_netid"] = teleportNetId,
            ["teleport_id"] = teleportId, ["waypoints"] = waypoints.ToArray(),
        };
    }

    // The rest of one 0x64 entry after its header (the waypoint reader): [net id][teleport id u8 when the header's
    // bit 0 is set], then the header's count (bits 16-22) of points.
    private static (uint NetId, byte? TeleportId, object?[] Points)? DecodeWaypointEntry(byte[] p, ref int offset, uint header)
    {
        var teleport = (header & 1) != 0;
        if (offset + 4 + (teleport ? 1 : 0) > p.Length) return null;
        var netId = U32(p, offset); offset += 4;
        byte? teleportId = teleport ? p[offset++] : null;
        var points = DecodeCompressedWaypoints(p, ref offset, (int)((header >> 16) & 0x7F));
        return points is null ? null : (netId, teleportId, points);
    }

    // 0xC2 (the unit's packet handler): [net id][item count u8][look-at u8, 12 more bytes when set], then the movement data:
    // [type u8][sync id u32] and a 0x67 entry (type 1), a 0x64 entry (type 2) or a stop (type 3: position and facing, which places the unit:
    // decoded as a one-point route).
    private static Dictionary<string, object?> DecodeEnterVisibility(byte[] p)
    {
        if (p.Length < 7) return new();
        var result = new Dictionary<string, object?> { ["net_id"] = U32(p, 1) };
        if (p[5] != 0) return result;
        var offset = 7 + (p[6] != 0 ? 12 : 0);
        if (offset + 5 > p.Length) return result;
        var type = p[offset];
        result["movement_type"] = type;
        result["sync_id"] = U32(p, offset + 1);
        offset += 5;
        switch (type)
        {
            case 1 when DecodeDashEntry(p, ref offset) is { } dash:
                foreach (var pair in dash) result[pair.Key] = pair.Value;
                break;
            case 2 when offset + 4 <= p.Length:
                var header = U32(p, offset);
                offset += 4;
                if (DecodeWaypointEntry(p, ref offset, header) is not { } entry) break;
                if (entry.TeleportId is byte id) result["teleport_id"] = id;
                result["waypoints"] = entry.Points;
                break;
            case 3 when offset + 16 <= p.Length:
                result["waypoints"] = new object?[] { new[] { F32(p, offset), F32(p, offset + 4) } };
                result["facing"] = new[] { F32(p, offset + 8), F32(p, offset + 12) };
                break;
        }
        return result;
    }

    private static (int X, int Z) DecodeMidpoint()
    {
        // Map1's native compression stores points around this midpoint. The
        // production sender derives it from NavigationGrid; the decoder uses
        // the same shipped Map1 bounds without loading the large mesh.
        return (6991, 7223);
    }

    private static object?[]? DecodeCompressedWaypoints(byte[] p, ref int offset, int count)
    {
        if (count < 1 || count > 127) return null;
        var flagBytes = count > 1 ? ((count - 2) / 4 + 1) : 0;
        if (offset + flagBytes + 4 > p.Length) return null;
        var flags = p.AsSpan(offset, flagBytes).ToArray(); offset += flagBytes;
        var x = I16(p, offset); var z = I16(p, offset + 2); offset += 4;
        var bounds = DecodeMidpoint(); var points = new List<object?> { new[] { (float)(bounds.X + x * 2), (float)(bounds.Z + z * 2) } };
        for (var point = 1; point < count; point++)
        {
            var flagIndex = (point - 1) * 2;
            if (flagIndex / 8 >= flags.Length) return null;
            if ((flags[flagIndex / 8] & (1 << (flagIndex % 8))) != 0) { if (offset + 1 > p.Length) return null; x += (sbyte)p[offset++]; }
            else { if (offset + 2 > p.Length) return null; x = I16(p, offset); offset += 2; }
            flagIndex++;
            if ((flags[flagIndex / 8] & (1 << (flagIndex % 8))) != 0) { if (offset + 1 > p.Length) return null; z += (sbyte)p[offset++]; }
            else { if (offset + 2 > p.Length) return null; z = I16(p, offset); offset += 2; }
            points.Add(new[] { (float)(bounds.X + x * 2), (float)(bounds.Z + z * 2) });
        }
        return points.ToArray();
    }

    private static Dictionary<string, object?> DecodeSpawnMinion(byte[] p)
    {
        // The native decoder uses fixed reads for the numeric prefix and
        // bounded C-string slices for the names. Consequently a truncated
        // capture can still carry a complete numeric prefix plus partial
        // names; retain that source behavior instead of requiring 167 bytes.
        if (p.Length < 39) return new();
        return new()
        {
            ["net_id"] = U32(p, 5), ["node_id"] = p[9], ["pos"] = Vec3(p, 10),
            ["skin_id"] = U32(p, 22), ["clone_netid"] = U32(p, 26),
            ["team"] = U32(p, 30) & 0x1FF, ["visibility_size"] = F32(p, 34),
            ["bitfield2"] = p[38], ["unit_name"] = CStr(p, 39, Math.Min(64, p.Length - 39)),
            ["skin_name"] = p.Length <= 103 ? string.Empty : CStr(p, 103, Math.Min(64, p.Length - 103)),
        };
    }

    private static Dictionary<string, object?> DecodeReplication(byte[] p)
    {
        if (p.Length < 15) return new();
        var setMaps = p[10];
        var offset = 15;
        var names = new Dictionary<(int, int), (string Name, bool Float)>
        {
            [(0, 0)] = ("mExp", true), [(0, 1)] = ("mGold", true), [(0, 2)] = ("mCanCastBits1", false),
            [(0, 3)] = ("mCanCastBits2", false), [(0, 4)] = ("mCurManaCost0", true), [(0, 5)] = ("mCurManaCost1", true),
            [(0, 6)] = ("mCurManaCost2", true), [(0, 7)] = ("mCurManaCost3", true), [(1, 0)] = ("ActionState", false),
            [(1, 3)] = ("IsTargetable", false), [(1, 4)] = ("IsTargetableToTeam", false),
            [(1, 6)] = ("mBaseAttackDamage", true), [(1, 10)] = ("mArmor", true), [(1, 11)] = ("mSpellBlock", true),
            [(1, 12)] = ("mHPRegenRate", true), [(1, 13)] = ("mPARRegenRate", true), [(1, 14)] = ("mAttackRange", true),
            [(1, 15)] = ("mFlatPhysicalDamageMod", true), [(1, 16)] = ("mPercentPhysicalDamageMod", true),
            [(1, 17)] = ("mFlatMagicDamageMod", true), [(1, 20)] = ("mAttackSpeedMod", true),
            [(1, 22)] = ("mPercentCooldownMod", true), [(1, 23)] = ("mFlatArmorPenetrationMod", true),
            [(1, 24)] = ("mPercentArmorPenetrationMod", true), [(1, 25)] = ("mFlatMagicPenetrationMod", true),
            [(1, 26)] = ("mPercentMagicPenetrationMod", true), [(1, 27)] = ("mPercentLifeStealMod", true),
            [(1, 28)] = ("mPercentSpellVampMod", true), [(3, 0)] = ("mHP", true), [(3, 1)] = ("mMP", true),
            [(3, 2)] = ("mMaxHP", true), [(3, 3)] = ("mMaxMP", true), [(3, 6)] = ("mMoveSpeed", true),
            [(3, 8)] = ("mLevelRef", false),
            [(3, 9)] = ("mNumNeutralMinionsKilled", false),
        };
        var decoded = new Dictionary<string, object?>
        {
            ["sync_id"] = U32(p, 5), ["unit_netid"] = U32(p, 11),
            ["fields"] = new Dictionary<string, object?>(),
        };
        var output = (Dictionary<string, object?>)decoded["fields"]!;
        for (var map = 0; map < 8; map++)
        {
            if ((setMaps & (1 << map)) == 0) continue;
            // Python's payload[off:off+4] is a bounded slice: a truncated
            // final value remains visible as raw bytes. A named field still
            // requires a complete struct.unpack and therefore degrades the
            // whole decoder result below when it is short.
            if (offset + 4 > p.Length) return new();
            var mask = U32(p, offset); offset += 4;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((mask & (1u << bit)) == 0) continue;
                var available = Math.Min(4, Math.Max(0, p.Length - offset));
                output[$"bit{bit}_setmap{map}"] = PythonBytes(p.AsSpan(offset, available));
                if (names.TryGetValue((map, bit), out var field))
                {
                    if (available < 4) return new();
                    output[field.Name] = field.Float ? F32(p, offset) : U32(p, offset);
                }
                offset += 4;
            }
        }
        return decoded;
    }

    private static Dictionary<string, object?> DecodeWaypoints(byte[] p) => Fixed(p, 9,
        (d, o) => new()
        {
            ["net_id"] = U32(d, 1), ["sync_id"] = U32(d, 5),
            ["waypoints"] = Enumerable.Range(0, Math.Max(0, (d.Length - 9) / 8))
                .Select(i => (object?)new[] { F32(d, 9 + i * 8), F32(d, 13 + i * 8) }).ToArray(),
        });

    private static Dictionary<string, object?> Fixed(byte[] p, int size,
        Func<byte[], int, Dictionary<string, object?>> decode)
        => p.Length < size ? new() : decode(p, size);

    private static ushort U16(byte[] p, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(offset));
    private static short I16(byte[] p, int offset) => BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(offset));
    private static int I32(byte[] p, int offset) => BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(offset));
    private static uint U32(byte[] p, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(offset));
    // Python struct.unpack("<f") returns a Python float (binary64) after
    // widening the wire's binary32. Keep that widened value so JSON and
    // replay comparisons retain the exact decoded representation.
    private static double F32(byte[] p, int offset) => BitConverter.Int32BitsToSingle(unchecked((int)U32(p, offset)));
    private static double[] Vec3(byte[] p, int offset) => [F32(p, offset), F32(p, offset + 4), F32(p, offset + 8)];
    private static object[] CompressedPosition(byte[] p, int offset) =>
        [I16(p, offset), F32(p, offset + 2), I16(p, offset + 6)];
    private static string CStr(byte[] p, int offset, int size)
    {
        var span = p.AsSpan(offset, size);
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(span[..(end < 0 ? size : end)]);
    }

    private static string PythonBytes(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder("b'");
        foreach (var value in bytes)
        {
            switch (value)
            {
                case (byte)'\\': builder.Append("\\\\"); break;
                case (byte)'\'': builder.Append("\\'"); break;
                case (byte)'\n': builder.Append("\\n"); break;
                case (byte)'\r': builder.Append("\\r"); break;
                case (byte)'\t': builder.Append("\\t"); break;
                case >= 0x20 and <= 0x7E: builder.Append((char)value); break;
                default: builder.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture)); break;
            }
        }
        return builder.Append('\'').ToString();
    }
}
