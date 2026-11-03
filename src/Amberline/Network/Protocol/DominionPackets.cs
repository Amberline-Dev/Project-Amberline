using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Network.Protocol;

/// <summary>
/// Dominion capture and score packets, decoded from the client's packet dispatcher (batch 445). Offsets count from the opcode byte at 0.
/// Each packet is as long as the last byte the client reads. Bytes the client never reads are written as zero. Reconstruction: which unit
/// a netId names, what the mode bytes select beyond the client's lookups, and the packet lengths (inferred from the reads) are not checked against a capture.
/// </summary>
public static class DominionPackets
{
    public const byte CapturePropOpcode = 0xDA;
    public const byte CapturePointEventOpcode = 0xDB;
    public const byte CapturePointValueOpcode = 0xDC;
    public const byte SetGameScoreOpcode = 0xDD;
    public const byte RespawnPointOpcode = 0xDE;

    /// <summary>
    /// 0xDA, 89 bytes. Netid at offset 0x4D (u32; the client's managed level-prop lookup), mode at offset 0x55 (1 = value; 0 means
    /// no packet field is read, other values are a client assert), byte at offset 0x56 must be 0, value at offset 0x58 (u8) becomes
    /// the prop's constant CapturePointFlexValue.
    /// </summary>
    public static PlaintextPacket CapturePropFlexValue(uint netId, byte value)
    {
        var bytes = new byte[0x59];
        bytes[0] = CapturePropOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4D), netId);
        bytes[0x55] = 1;
        bytes[0x58] = value;
        return Packet(bytes);
    }

    /// <summary>
    /// 0xDB, 12 bytes. Netid at 5 (u32; the client's hashed object lookup; an absent id is a no-op). Byte 9 is an event
    /// code that the client passes to every listener of the capture object. Mode byte 11 = 0 binds a CapturePointFlexValue whose
    /// byte is byte 10, the capture index (batch 475; its value is the point record's client-smoothed +0x1C, batch 493e).
    /// Mode 1 binds a PAR-driven flex value instead (no value byte is read).
    /// Reconstruction: the meaning of the event code is not decoded.
    /// </summary>
    public static PlaintextPacket CapturePointEvent(uint netId, byte eventCode, byte value, bool parFlex)
    {
        var bytes = new byte[12];
        bytes[0] = CapturePointEventOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), netId);
        bytes[9] = eventCode;
        bytes[10] = value;
        bytes[11] = parFlex ? (byte)1 : (byte)0;
        return Packet(bytes);
    }

    /// <summary>
    /// 0xDC, 16 bytes. Byte 5 is the capture index: the client's capture point list (GetCapturePoint(index)), a list of 0x24-byte
    /// records. Netid at offset 6 (u32) must name a live managed object for the write to happen. Byte 10 then goes to the
    /// record's member at offset 0x10. Byte 15 is the mode: 0 applies, any other value only logs.
    /// Batch 494 (proven): the packet is the client's attach of a capture point to a unit, with the unit's primary ability resource type, so byte 10 is the PAR type the point's value reads: GetPARPercent(type) of the unit.
    /// </summary>
    public static PlaintextPacket CapturePointValue(byte index, uint netId, byte parType, bool apply)
    {
        var bytes = new byte[16];
        bytes[0] = CapturePointValueOpcode;
        bytes[5] = index;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), netId);
        bytes[10] = parType;
        bytes[15] = apply ? (byte)0 : (byte)1;
        return Packet(bytes);
    }

    /// <summary>
    /// 0xDD, 13 bytes. Team at 5 (u32: 100 Order, 200 Chaos) and score at 9 (u32). The client's score controller stores it in the team-score
    /// pair that feeds the Dominion victory meter (VictoryPointDisplay.gfx SetScore "0,own" and "1,enemy", the pair ordered by the local
    /// hero's team). The TitanBar does not read it (its ODIN setScore is the player's own score).
    /// Other team values assert in the client and store nothing. This is the shipped SetGameScore(Team, Score) node's packet. Batch 496: the
    /// meter is a team's nexus health out of 500 (DominionScore.SendNexusScores). The client draws no floating text from it.
    /// </summary>
    public static PlaintextPacket SetGameScore(Team team, int score)
    {
        var bytes = new byte[13];
        bytes[0] = SetGameScoreOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), team == Team.Order ? 100u : 200u);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), score);
        return Packet(bytes);
    }

    /// <summary>
    /// 0xDE command 0, 11 bytes: byte 5 = 0, point index at 6 (u8), net ID at 7 (u32). The client records the net ID under the
    /// index and shows the respawn point. Reconstruction: the index-to-point meaning follows the client's map insert only.
    /// </summary>
    public static PlaintextPacket CreateRespawnPoint(byte point, uint netId)
    {
        var bytes = new byte[11];
        bytes[0] = RespawnPointOpcode;
        bytes[6] = point;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(7), netId);
        return Packet(bytes);
    }

    /// <summary>
    /// 0xDE command 1, 15 bytes: byte 5 = 1, state byte at 6, net ID at 11 (u32). The client writes the state byte into the
    /// respawn point with that net ID (nothing is written when it is unknown).
    /// </summary>
    public static PlaintextPacket SetRespawnPointState(uint netId, byte state)
    {
        var bytes = new byte[15];
        bytes[0] = RespawnPointOpcode;
        bytes[5] = 1;
        bytes[6] = state;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11), netId);
        return Packet(bytes);
    }

    public const byte IncrementPlayerStatOpcode = 0xE3;
    // The client's stat names by index (its table): 0 NODE_CAPTURE, 1 NODE_NEUTRALIZE, 2 NODE_KILL_OFFENSE, 3 TEAM_OBJECTIVE,
    // 4 DEFEND_POINT_NEUTRALIZE, 5 NODE_KILL_DEFENSE, 6 NODE_TIME_DEFENSE, 7 LAST_STAND, 8 NODE_CAPTURE_ASSIST, 9 NODE_NEUTRALIZE_ASSIST.
    public const byte NodeCaptureStat = 0;
    public const byte NodeNeutralizeStat = 1;
    public const byte TeamObjectiveStat = 3;
    public const byte NodeCaptureAssistStat = 8;
    public const byte NodeNeutralizeAssistStat = 9;

    /// <summary>
    /// 0xE3, 10 bytes: hero net id at 5 (u32), stat index at 9 (u8). The client finds the object, then the hero, and adds one to the stat
    /// the index names (its table) in the hero's stat collection, which ignores a name the collection lacks; an unknown object or a non-hero
    /// only logs a "Tried to increment player stat" message.
    /// </summary>
    public static PlaintextPacket IncrementPlayerStat(uint heroNetId, byte stat)
    {
        var bytes = new byte[10];
        bytes[0] = IncrementPlayerStatOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), heroNetId);
        bytes[9] = stat;
        return Packet(bytes);
    }

    public const byte IncrementPlayerScoreOpcode = 0xE2;
    // Batch 518: the client's score categories (ScoreCategory_e; the names are filled in by its initialisers).
    // ODIN_Stats.inibin has COMBAT_PLAYER_SCORE and OBJECTIVE_PLAYER_SCORE only, so the other two change the total alone.
    public const byte OffenseScore = 0;
    public const byte DefenseScore = 1;
    public const byte CombatScore = 2;
    public const byte ObjectiveScore = 3;

    /// <summary>
    /// Batch 518: IncrementPlayerScore (0xE2), 16 bytes. The client's packet switch finds the hero by the net id at 5 (an unknown
    /// object or a non-hero only logs) and calls the score controller's AddScoreToPlayer with the category byte at offset 9, the float at
    /// offset 0xC, the score event byte at offset 0xA and bit 0 of byte 0xB. AddScoreToPlayer adds the float to the hero's PlayerScore: to
    /// the category's slot and stat (<see cref="CombatScore"/>, <see cref="ObjectiveScore"/>; category 5 skips both) and to the total (stat
    /// TOTAL_PLAYER_SCORE), then re-ranks the hero's team. The TitanBar polls the local hero's total and the controller's rank for the player
    /// and calls the movie's setScore("total,rank") on a change. When the flag is set, the hero is the local hero and the category is not 5,
    /// the ScoreCallOut movie shows "score_event_callout" ("@ScoreLabel@: +@ScoreValue@", fontconfig_en_US.txt:7200) in #FFFFFF, the label being
    /// the event's key in the table (scoreboard_stat_label_*) and the value the float cast to int.
    /// </summary>
    public static PlaintextPacket IncrementPlayerScore(uint heroNetId, byte category, byte scoreEvent, bool callout, float points)
    {
        var bytes = new byte[16];
        bytes[0] = IncrementPlayerScoreOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), heroNetId);
        bytes[9] = category;
        bytes[10] = scoreEvent;
        bytes[11] = callout ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), points);
        return Packet(bytes);
    }

    public const byte QuestUpdateOpcode = 0x90;
    // The client's quest types (its table: "PrimaryQuests", "SecondaryQuests") and commands.
    public const byte PrimaryQuest = 0;
    public const byte SecondaryQuest = 1;
    public const byte QuestAdd = 0;
    public const byte QuestComplete = 1;
    public const byte QuestHide = 2;

    /// <summary>
    /// Batch 471: HandleQuestUpdate (0x90), 396 bytes. The client's world dispatcher passes: objective at offset 5, tooltip at
    /// offset 0x85 and reward at offset 0x105 (char[128] each), the type at offset 0x185, the command at offset 0x186, the flag at offset
    /// 0x187 (bit 0) and the quest id at offset 0x188 (u32) to its quest handler. Command 0 adds the quest to its type's tracker
    /// (QueueNewQuest, the "Quest Received!" text and sound; the flag is HandleRollovers); 1 completes it (CompleteQuest; flag 1 plays
    /// Quests.ini CompletedText and CompletedSound, flag 0 FailedText and FailedSound; a 5 s timer then retires it); 2 hides it.
    /// The texts go to QuestTracker.gfx unchanged, and the client's GFx FontTranslator turns a fontconfig key into its text.
    /// </summary>
    public static PlaintextPacket QuestUpdate(uint questId, byte questType, byte command, bool flag, string objective = "",
        string tooltip = "", string reward = "")
    {
        var bytes = new byte[0x18C];
        bytes[0] = QuestUpdateOpcode;
        Ascii(bytes.AsSpan(0x05, 128), objective);
        Ascii(bytes.AsSpan(0x85, 128), tooltip);
        Ascii(bytes.AsSpan(0x105, 128), reward);
        bytes[0x185] = questType;
        bytes[0x186] = command;
        bytes[0x187] = flag ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x188), questId);
        return Packet(bytes);
    }

    /// <summary>
    /// Batch 674: OnQuestEvent, 10 bytes: the quest event at +5 (u8; eQuestEvent 0 roll over, 1 roll out, 2 mouse down, 3 mouse up)
    /// and the quest id at +6 (i32) (the AntiBot build's struct and enum, where it is 216 next to OnTutorialPopupClosed 215; the 126 build
    /// sends OnTutorialPopupClosed as 0xD5 and this as 0xD6, the client). The tracker's roll-over sends it only for a quest added
    /// with the flag (handleRolloverCallbacks); its click sends a mouse down once per quest.
    /// </summary>
    public const byte QuestEventClientOpcode = 0xD6;
    public const byte QuestRollOver = 0;
    public const byte QuestMouseDown = 2;

    /// <summary>The event and quest id of a <see cref="QuestEventClientOpcode"/> payload, or null for any other.</summary>
    public static (byte Event, uint QuestId)? QuestEvent(ReadOnlySpan<byte> payload) =>
        payload.Length >= 10 && payload[0] == QuestEventClientOpcode
            ? (payload[5], BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(6, 4)))
            : null;

    public const byte FloatingTextOpcode = 0x1C;

    /// <summary>FLOATTEXT_Score (DATA/CFG/defaults/GamePermanent.cfg:382-394: Arial 14, white, decay 3.5 s).</summary>
    public const byte ScoreFloatingText = 24;

    /// <summary>
    /// Batch 505: DisplayFloatingText (0x1C), 14 bytes plus the text. The client's packet switch reads the target net id at 5 (nothing
    /// is drawn for an unknown or not net-visible unit), the type at 9 (u8), an i32 at offset 0xA and a NUL-terminated text at offset 0xE.
    /// The text is translated (so a fontconfig key shows its text), then every "@IntParam1@" in it is replaced by the i32 in decimal, and the
    /// line goes to the floating-text manager with the local hero as its source, so the stock FilterLevel 2 draws it on every client that sees the target.
    /// </summary>
    public static PlaintextPacket FloatingText(uint targetNetId, byte type, int intParam, string text)
    {
        var bytes = new byte[0xE + text.Length + 1];
        bytes[0] = FloatingTextOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), targetNetId);
        bytes[9] = type;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xA), intParam);
        Ascii(bytes.AsSpan(0xE), text);
        return ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, bytes, encrypt: true, LeaguePacketReliability.Reliable,
            new LeaguePacketEntityReference(LeaguePacketEntityRole.World, targetNetId));
    }

    // A NUL-terminated ASCII field: the text may use all but the last byte.
    private static void Ascii(Span<byte> field, string text)
    {
        if (text.Length >= field.Length || !System.Text.Ascii.IsValid(text)) throw new ArgumentOutOfRangeException(nameof(text));
        System.Text.Encoding.ASCII.GetBytes(text, field);
    }

    private static PlaintextPacket Packet(byte[] bytes)
        => ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, bytes, encrypt: true, LeaguePacketReliability.Reliable);
}
