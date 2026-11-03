using Amberline.Core;

using System.Text;

namespace Amberline.Network.Protocol;

public readonly record struct SpellCastTarget(uint NetId, Position3 Position, byte HitResult);

public sealed record SpellCastInfo(
    uint SpellHash,
    uint SpellNetId,
    int Rank,
    uint CasterNetId,
    uint MissileNetId,
    Position3 TargetPosition,
    Position3 TargetPositionEnd,
    IReadOnlyList<SpellCastTarget> Targets,
    float Cooldown,
    float ManaCost,
    byte Slot,
    Position3 LaunchPosition,
    float AttackSpeedModifier = 1.0f,
    float DesignerCastTime = 0.0f,
    float ExtraCastTime = 0.0f,
    float DesignerTotalTime = 0.0f,
    float StartCastTime = 0.0f,
    byte Flags = 0);

public readonly record struct SpellTooltipEntry(byte Slot, float Value1, float Value2, float Value3);

public readonly record struct BuffAdd(
    uint TargetNetId,
    byte Slot,
    byte BuffType,
    uint BuffNameHash,
    float Duration,
    uint CasterNetId,
    byte Count = 1,
    byte IsHidden = 0,
    float RunningTime = 0.0f);

public readonly record struct SlotSpellNameUpdate(
    uint NetId, byte Slot, bool IsSummoner, string SpellName);

public static class SpellPackets
{
    public const byte CastSpellAnswerOpcode = 0xBD;
    public const byte BuffAddOpcode = 0xBF;
    public const byte BuffUpdateCountOpcode = 0x1F;
    public const byte BuffReplaceOpcode = 0x32;
    public const int MaxBuffSlots = 64;
    // Native 0x83 routes selectors below 0x3C through BuffManager.  The
    // remaining selector ranges belong to spellbooks, so only these slots
    // are safe for a buff tooltip update.
    public const int MaxBuffTooltipSlots = 60;

    public const byte BuffInternal = 0;
    public const byte BuffAura = 1;
    public const byte BuffCombatEnhancer = 2;
    public const byte BuffCombatDehancer = 3;
    // The retail numbering (see Core/Buffs/BuffType.cs).
    public const byte BuffStun = 4;
    public const byte BuffInvisibility = 5;
    public const byte BuffSilence = 6;
    public const byte BuffTaunt = 7;
    public const byte BuffSlow = 9;
    public const byte BuffSnare = 10;
    public const byte BuffDamageOverTime = 11;
    public const byte BuffHeal = 12;
    public const byte BuffHaste = 13;
    public const byte BuffInvulnerability = 16;
    public const byte BuffSleep = 17;
    public const byte BuffFear = 20;
    public const byte BuffNet = 21;
    public const byte BuffBlind = 24;

    /// <summary>Protocol name hash used by buff add/remove packets.</summary>
    public static uint BuffNameHash(string name) => ProtocolEncoding.HashString(name);

    /// <summary>
    /// The client's SpellCastInfo holds 32 target slots (the count byte is stored unchecked into Targets[32] and
    /// hitResult[32]), so a longer list would overwrite client memory. The wire carries the first 32; the server keeps the full list.
    /// </summary>
    public const int MaxCastTargets = 32;

    public static byte[] BuildCastInfo(SpellCastInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(info.Targets);
        if (info.Rank < 1 || info.Rank > 256)
            throw new ArgumentOutOfRangeException(nameof(info), "Spell rank must fit the one-byte zero-based wire field.");
        var targets = info.Targets.Count > MaxCastTargets ? info.Targets.Take(MaxCastTargets).ToArray() : info.Targets;

        using var writer = ProtocolEncoding.Writer(0x30 + 17 * targets.Count + 0x26, out var stream);
        writer.Write((ushort)0);
        writer.Write(info.SpellHash);
        writer.Write(info.SpellNetId);
        writer.Write((byte)(info.Rank - 1));
        writer.Write(info.AttackSpeedModifier);
        writer.Write(info.CasterNetId);
        writer.Write(info.MissileNetId);
        WritePosition(writer, info.TargetPosition);
        WritePosition(writer, info.TargetPositionEnd);
        writer.Write((byte)targets.Count);
        foreach (var target in targets)
        {
            writer.Write(target.NetId);
            WritePosition(writer, target.Position);
            writer.Write(target.HitResult);
        }

        writer.Write(info.DesignerCastTime);
        writer.Write(info.ExtraCastTime);
        writer.Write(info.DesignerTotalTime);
        writer.Write(info.Cooldown);
        writer.Write(info.StartCastTime);
        writer.Write(info.Flags);
        writer.Write(info.Slot);
        writer.Write(info.ManaCost);
        WritePosition(writer, info.LaunchPosition);
        writer.Flush();
        if (stream.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(info), "SpellCastInfo is too large for its length field.");
        var result = ProtocolEncoding.Finish(stream);
        BitConverter.TryWriteBytes(result.AsSpan(0, 2), (ushort)result.Length);
        return result;
    }

    public static PlaintextPacket CharacterFade(
        uint netId, short fadeId, float? amount = null, float duration = 0.0f)
    {
        using var writer = ProtocolEncoding.Writer(amount.HasValue ? 15 : 7, out var stream);
        writer.Write(amount.HasValue ? (byte)0xBA : (byte)0x34);
        writer.Write(netId);
        writer.Write(fadeId);
        if (amount.HasValue)
        {
            writer.Write(duration);
            writer.Write(amount.Value);
        }
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>Stock spellbook's SetAutocastSpellSlot: -1 restores
    /// the default attack; ExtraSlots start at absolute slot 42.</summary>
    public static PlaintextPacket SetAutoAttackSpellSlot(uint netId, int slot)
    {
        if (slot is < -1 or >= 52) throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0x22);
        writer.Write(netId);
        writer.Write(unchecked((byte)slot));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>0x6E replaces the complete animation override map. The stock loader reads a byte count, then two int32-length byte strings per entry.
    /// It stores keys verbatim, but every lookup lowercases the requested name first, as the client's own script setter does before storing,
    /// so the original name is sent lowercase (playtest B3 #1); the replacement keeps its data casing.</summary>
    public static PlaintextPacket SetAnimationStates(uint netId,
        IReadOnlyList<(string Original, string Replacement)> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(states));
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0x6E);
        writer.Write(netId);
        writer.Write((byte)states.Count);
        foreach (var (original, replacement) in states)
            foreach (var name in new[] { original?.ToLowerInvariant(), replacement })
            {
                if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(c => c > 127 || c == 0))
                    throw new ArgumentException("Animation names must be 1-255 ASCII characters.", nameof(states));
                var bytes = System.Text.Encoding.ASCII.GetBytes(name);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket FaceDirection(uint netId, Position3 direction)
    {
        using var writer = ProtocolEncoding.Writer(17, out var stream);
        writer.Write((byte)0x53);
        writer.Write(netId);
        WritePosition(writer, direction);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket PushCharacterData(
        uint netId, uint changeId, string skinName, bool useSpells = false)
    {
        ProtocolEncoding.RequireAsciiLength(skinName, 63, nameof(skinName));
        if (skinName.Length == 0 || skinName.Contains('\0'))
            throw new ArgumentException("Character name must be 1..63 ASCII bytes without NUL.", nameof(skinName));
        using var writer = ProtocolEncoding.Writer(74, out var stream);
        writer.Write((byte)0x9F);
        writer.Write(netId);
        writer.Write(changeId);
        writer.Write((byte)(useSpells ? 1 : 0));
        ProtocolEncoding.WriteFixedAscii(writer, skinName, 64, 63);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket PopCharacterData(uint netId, uint changeId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x6A);
        writer.Write(netId);
        writer.Write(changeId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// PopAllCharacterData (5 bytes: 0x6A, the unit), the body of <c>BBPopAllCharacterData</c> (the script's function sends 0x6A with size 5). The client
    /// runs both of its 0x6A handlers for this and for the 9-byte pop (one clears the stack, the other erases by the id at +5).
    /// </summary>
    public static PlaintextPacket PopAllCharacterData(uint netId)
    {
        using var writer = ProtocolEncoding.Writer(5, out var stream);
        writer.Write((byte)0x6A);
        writer.Write(netId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket SpellOffsetTarget(
        uint netId, byte slot, uint targetNetId, bool summoner = false)
    {
        if (slot >= 128)
            throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x37);
        writer.Write(netId);
        writer.Write((byte)(slot | (summoner ? 0x80 : 0)));
        writer.Write(targetNetId);
        writer.Flush();
        var references = targetNetId == 0
            ? new[] { Hero(netId) }
            : new[] { Hero(netId), Target(targetNetId) };
        return Packet(stream, LeaguePacketReliability.Reliable, references);
    }

    public static PlaintextPacket PauseAnimation(uint netId, bool paused)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0x74);
        writer.Write(netId);
        writer.Write((byte)(paused ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// ColorRemapFX (<c>0xE4</c>, 22 bytes): <c>[0xE4][0 u32][fading in u8][fade time f32][team i32][color b,g,r,a][max weight f32]</c>.
    /// The stock client's handler (the 0xE4 case) reads the flag at 5 and the team at 10 only: a team other than 0 must be the local
    /// player's (100 with no player), otherwise the packet is dropped; then the flag picks the colour-remap manager's fade in or fade out. The
    /// header carries no unit, so the packet is global. Fade-outs carry no colour (<c>BBFadeOutColorFadeEffect</c> has only the time and the team).
    /// </summary>
    public static PlaintextPacket ColorRemapFx(bool fadeIn, float fadeTime, int teamId, uint color, float maxWeight)
    {
        if (!float.IsFinite(fadeTime) || fadeTime < 0 || !float.IsFinite(maxWeight)) throw new ArgumentOutOfRangeException(nameof(fadeTime));
        using var writer = ProtocolEncoding.Writer(22, out var stream);
        writer.Write((byte)0xE4);
        writer.Write(0u);
        writer.Write((byte)(fadeIn ? 1 : 0));
        writer.Write(fadeTime);
        writer.Write(teamId);
        writer.Write(color);
        writer.Write(maxWeight);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// ChangeCharacterVoice (<c>0x9E</c>, 70 bytes): <c>[0x9E][netid u32][reset u8][voice override char[64]]</c>. The unit's receiver
    /// clears its voice string on <c>reset</c> and otherwise stores the NUL-terminated suffix (Riven's ultimate sends "Ult").
    /// </summary>
    public static PlaintextPacket ChangeCharacterVoice(uint netId, string? suffix)
    {
        if (suffix is not null)
        {
            ProtocolEncoding.RequireAsciiLength(suffix, 63, nameof(suffix));
            if (suffix.Contains('\0')) throw new ArgumentException("The voice suffix contains NUL.", nameof(suffix));
        }
        using var writer = ProtocolEncoding.Writer(70, out var stream);
        writer.Write((byte)0x9E);
        writer.Write(netId);
        writer.Write((byte)(suffix is null ? 1 : 0));
        var text = new byte[64];
        if (suffix is not null) Encoding.ASCII.GetBytes(suffix, text);
        writer.Write(text);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// ChangePARColorOverride (<c>0x99</c>, 18 bytes): <c>[0x99][netid u32][unit u32][enabling u8][bar color][fade color]</c>. The unit's
    /// receiver stores both colours on its resource bar when enabling and drops the override otherwise (the shipped disable sends no colours).
    /// The header id and the unit field are the same unit (the native writes the slot owner's id into the body).
    /// </summary>
    public static PlaintextPacket ChangeParColorOverride(uint netId, ParColorOverride? value)
    {
        using var writer = ProtocolEncoding.Writer(18, out var stream);
        writer.Write((byte)0x99);
        writer.Write(netId);
        writer.Write(netId);
        writer.Write((byte)(value is null ? 0 : 1));
        writer.Write(value?.Bar ?? 0u);
        writer.Write(value?.Fade ?? 0u);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    // Patch 126: the registration binds opcode 0xB8 (0xBA is fade).
    // The handler reads flags at +5, duration at +9 and a
    // NUL-terminated animation name at +13. Playback flags are consumed by
    // The play routine's flags: loop=2, replace=8, lock=0x40, disable blending=0x200.
    public static PlaintextPacket PlayAnimation(uint netId, string name,
        bool loop = false, bool lockAnimation = false, bool blend = true, float duration = 0)
    {
        ProtocolEncoding.RequireAsciiLength(name, 63, nameof(name));
        if (name.Length == 0 || name.Contains('\0')) throw new ArgumentException("Animation name is empty or contains NUL.", nameof(name));
        if (!float.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        using var writer = ProtocolEncoding.Writer(14 + name.Length, out var stream);
        writer.Write((byte)0xB8);
        writer.Write(netId);
        writer.Write(8u | (loop ? 2u : 0u) | (lockAnimation ? 0x40u : 0u) | (blend ? 0u : 0x200u));
        writer.Write(duration);
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write((byte)0);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    // The lock-clear routine clears the animation lock with bit 0x40 even outside a dash.
    public static PlaintextPacket StopAnimation(uint netId, bool blend = true)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x2B);
        writer.Write(netId);
        writer.Write(0x40u | (blend ? 0u : 0x200u));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket SpellTooltipVariables(
        IReadOnlyList<SpellTooltipEntry> entries, uint netId = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return TooltipVariables(entries, netId, selectorBase: 60);
    }

    public static PlaintextPacket BuffTooltipVariables(
        uint netId, int buffSlot, float value0, float value1, float value2)
    {
        if (buffSlot is < 0 or >= MaxBuffTooltipSlots)
            throw new ArgumentOutOfRangeException(nameof(buffSlot),
                "Buff tooltip selectors must address an allocated native buff slot below 60.");
        return TooltipVariables(
            new[] { new SpellTooltipEntry(checked((byte)buffSlot), value0, value1, value2) },
            netId, selectorBase: 0);
    }

    private static PlaintextPacket TooltipVariables(
        IReadOnlyList<SpellTooltipEntry> entries, uint netId, byte selectorBase)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entries));
        using var writer = ProtocolEncoding.Writer(7 + entries.Count * 17, out var stream);
        writer.Write((byte)0x83);
        writer.Write(0u);
        writer.Write((ushort)entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(netId);
            writer.Write(entry.Slot > byte.MaxValue - selectorBase
                ? throw new ArgumentOutOfRangeException(nameof(entries))
                : (byte)(entry.Slot + selectorBase));
            writer.Write(entry.Value1);
            writer.Write(entry.Value2);
            writer.Write(entry.Value3);
        }
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable,
            netId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { World(netId) });
    }

    public static PlaintextPacket CastSpellAnswer(
        uint casterNetId, SpellCastInfo info, int casterPositionSyncId = 0)
    {
        var cast = BuildCastInfo(info);
        using var writer = ProtocolEncoding.Writer(9 + cast.Length, out var stream);
        writer.Write(CastSpellAnswerOpcode);
        writer.Write(casterNetId);
        writer.Write(casterPositionSyncId);
        writer.Write(cast);
        writer.Flush();
        var references = new List<LeaguePacketEntityReference> { Hero(casterNetId) };
        foreach (var target in info.Targets)
            if (target.NetId != 0) references.Add(Target(target.NetId));
        return Packet(stream, LeaguePacketReliability.Reliable, references.ToArray());
    }

    public static PlaintextPacket CastSpellAnswer(
        uint casterNetId, ReadOnlySpan<byte> castInfo, int casterPositionSyncId = 0)
    {
        using var writer = ProtocolEncoding.Writer(9 + castInfo.Length, out var stream);
        writer.Write(CastSpellAnswerOpcode);
        writer.Write(casterNetId);
        writer.Write(casterPositionSyncId);
        writer.Write(castInfo);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(casterNetId));
    }

    /// <summary>
    /// LevelUpSpell (<c>0x5E</c>, 9 bytes): <c>[0x5E][netid u32][slot i32]</c>. The client's spellbook raises that slot's level by one
    /// (the slot's IncLevel: level + 1, never above 6) and touches nothing else: no skill point is spent and no script hook runs.
    /// </summary>
    public static PlaintextPacket LevelUpSpell(uint netId, int slot)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x5E);
        writer.Write(netId);
        writer.Write(slot);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket UpgradeSpellAnswer(
        uint heroNetId, byte slot, byte spellLevel, byte pointsLeft)
    {
        using var writer = ProtocolEncoding.Writer(8, out var stream);
        writer.Write((byte)0x18);
        writer.Write(heroNetId);
        writer.Write(slot);
        writer.Write(spellLevel);
        writer.Write(pointsLeft);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket SetCooldown(
        uint netId, byte slot, float cooldown, bool isSummoner = false)
    {
        if (slot >= 128) throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x8A);
        writer.Write(netId);
        writer.Write((byte)(slot | (isSummoner ? 0x80 : 0)));
        writer.Write(cooldown);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket SetSpellData(uint netId, uint spellHash, byte slot)
    {
        using var writer = ProtocolEncoding.Writer(14, out var stream);
        writer.Write((byte)0x73);
        writer.Write(netId);
        writer.Write(netId);
        writer.Write(spellHash);
        writer.Write(slot);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// Native ChangeSlotSpellName (0x35): five-byte unit header, a slot
    /// byte with the summoner bit, then a zero-terminated source spell name.
    /// </summary>
    public static PlaintextPacket ChangeSlotSpellName(
        uint netId, byte slot, string spellName, bool isSummoner = true)
    {
        if (slot >= 128) throw new ArgumentOutOfRangeException(nameof(slot));
        ProtocolEncoding.RequireAsciiLength(spellName, 64, nameof(spellName));
        if (spellName.Length == 0 || spellName.Contains('\0'))
            throw new ArgumentException("Spell name must be 1..64 ASCII bytes without NUL.", nameof(spellName));
        using var writer = ProtocolEncoding.Writer(7 + spellName.Length, out var stream);
        writer.Write((byte)0x35);
        writer.Write(netId);
        writer.Write((byte)(slot | (isSummoner ? 0x80 : 0)));
        writer.Write(Encoding.ASCII.GetBytes(spellName));
        writer.Write((byte)0);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// Native ChangeSlotSpellType (0x1A). The source BBSetTargetingType
    /// handler writes the five-byte unit header, then a slot byte (with the
    /// summoner flag in bit 7) and the native ttype_e value.
    /// </summary>
    public static PlaintextPacket ChangeSlotSpellType(
        uint netId, byte slot, byte targetingType, bool isSummoner = false)
    {
        if (slot >= 128) throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(7, out var stream);
        writer.Write((byte)0x1A);
        writer.Write(netId);
        writer.Write((byte)(slot | (isSummoner ? 0x80 : 0)));
        writer.Write(targetingType);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static bool TryReadChangeSlotSpellName(
        ReadOnlySpan<byte> payload, out SlotSpellNameUpdate update)
    {
        update = default;
        if (payload.Length < 7 || payload[0] != 0x35) return false;
        var terminator = payload[6..].IndexOf((byte)0);
        if (terminator < 1 || terminator > 64 || 7 + terminator != payload.Length)
            return false;
        var name = Encoding.ASCII.GetString(payload.Slice(6, terminator));
        update = new SlotSpellNameUpdate(BitConverter.ToUInt32(payload[1..5]),
            (byte)(payload[5] & 0x7F), (payload[5] & 0x80) != 0, name);
        return true;
    }

    public static PlaintextPacket MissileReplication(
        uint casterNetId,
        Position3 position,
        Position3 casterPosition,
        Position3 direction,
        Position3 velocity,
        Position3 startPoint,
        Position3 endPoint,
        Position3 unitPosition,
        float speed,
        float lifePercentage,
        byte bounced,
        SpellCastInfo castInfo)
    {
        var blob = BuildCastInfo(castInfo);
        using var writer = ProtocolEncoding.Writer(0x62 + blob.Length, out var stream);
        writer.Write((byte)0x41);
        writer.Write(casterNetId);
        WritePosition(writer, position);
        WritePosition(writer, casterPosition);
        WritePosition(writer, direction);
        WritePosition(writer, velocity);
        WritePosition(writer, startPoint);
        WritePosition(writer, endPoint);
        WritePosition(writer, unitPosition);
        writer.Write(speed);
        writer.Write(lifePercentage);
        writer.Write(bounced);
        writer.Write(blob);
        writer.Flush();
        var references = new List<LeaguePacketEntityReference> { Hero(casterNetId) };
        if (castInfo.MissileNetId != 0) references.Add(Create(castInfo.MissileNetId));
        foreach (var target in castInfo.Targets)
            if (target.NetId != 0) references.Add(Target(target.NetId));
        return Packet(stream, LeaguePacketReliability.Reliable, references.ToArray());
    }

    public static PlaintextPacket MissileReplication(
        uint casterNetId,
        Position3 position,
        Position3 casterPosition,
        Position3 direction,
        Position3 velocity,
        Position3 startPoint,
        Position3 endPoint,
        Position3 unitPosition,
        float speed,
        float lifePercentage,
        byte bounced,
        ReadOnlySpan<byte> castInfo)
    {
        using var writer = ProtocolEncoding.Writer(0x62 + castInfo.Length, out var stream);
        writer.Write((byte)0x41);
        writer.Write(casterNetId);
        WritePosition(writer, position);
        WritePosition(writer, casterPosition);
        WritePosition(writer, direction);
        WritePosition(writer, velocity);
        WritePosition(writer, startPoint);
        WritePosition(writer, endPoint);
        WritePosition(writer, unitPosition);
        writer.Write(speed);
        writer.Write(lifePercentage);
        writer.Write(bounced);
        writer.Write(castInfo);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(casterNetId));
    }

    /// <summary>ChainMissileSync (patch-126 enum, 0x6F): the targets a chain missile flies through.</summary>
    public const byte ChainMissileSyncOpcode = 0x6F;
    /// <summary>The client struct holds 32 target slots.</summary>
    public const int ChainMissileTargetSlots = 32;

    /// <summary>
    /// ChainMissileSync (141 bytes): command, <b>FromID</b>, target count, owner, 32 target net IDs. The sender is the
    /// chain missile's own net ID (the <c>MissileNetId</c> of its cast), not the caster, and the packet reaches the client only
    /// after that client's missile exists.
    /// <para>
    /// Evidence: the missile registers an <c>OnNetworkRegister&lt;ChainMissileSync&gt;</c> sub-object whose ID is the missile's net ID
    /// with event 0x6F. The client's packet router runs its waiting-list pass before every lookup, so a missile whose ID was just assigned is
    /// registered, and it looks a packet up by {FromID, event} and hands it to that object's handler, which turns each net ID into the unit's
    /// object id and fills the missile's hop table (32 slots) and its "list received" flag. Only a packet nobody registered for reaches the
    /// world-event dispatcher, whose message names FromID as a missile id; its fallback stores the packet on the owner's spell caster but looks
    /// the owner up in the object-id keyed manager, so a net ID never resolves there. Sent right after the cast with the caster as FromID
    /// (batch 26), the packet fell through that fallback and the missile, whose table held only the cast's one target, ended at its first hit
    /// (the projectile update returns 0 when the next table entry is empty): the owner's playtest of 16 October ("Spell Flux / Ricochet disappear after the first hit").
    /// </para>
    /// <para>
    /// The cast packet may name only one target (the missile's OnCreate asserts <c>getNumTargets() &lt;= 1</c>); the missile
    /// is created at the client's cast frame, so this packet follows the cast by its windup (<c>BbChainSync</c> sends it).
    /// Re-sending is harmless: the handler rewrites the same entries.
    /// </para>
    /// </summary>
    public static PlaintextPacket ChainMissileSync(uint missileNetId, uint ownerNetId, IReadOnlyList<uint> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > ChainMissileTargetSlots)
            throw new ArgumentOutOfRangeException(nameof(targets), "a chain missile holds at most 32 targets");
        using var writer = ProtocolEncoding.Writer(13 + 4 * ChainMissileTargetSlots, out var stream);
        writer.Write(ChainMissileSyncOpcode);
        writer.Write(missileNetId);
        writer.Write(targets.Count);
        writer.Write(ownerNetId);
        for (var index = 0; index < ChainMissileTargetSlots; index++)
            writer.Write(index < targets.Count ? targets[index] : 0u);
        writer.Flush();
        // The missile is not a unit the receiver tracks: only the owner is a reference. The typed route
        // (ChainMissileSyncReplicationMessage) decides who holds the missile.
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(ownerNetId));
    }

    public const byte LineMissileHitListOpcode = 0x28;

    /// <summary>
    /// LineMissileHitList (0x28, 7 + 4n bytes): <c>[0x28][FromID = the line missile u32][count i16][hit unit net ID u32 x count]</c>.
    /// Evidence: the line missile registers an <c>OnNetworkRegister&lt;LineMissileHitList&gt;</c> sub-object whose id is event 0x28, so the
    /// packet reaches only a client that holds the missile under that net ID (the world-event switch has no 0x28 case). Its handler
    /// reads the count at +5 and the IDs from +7; for each one, unless the missile is over (set by 0x5D), it copies the
    /// missile's cast info, makes that unit its target and runs the spell's hit pass: the spell's hit
    /// sound events 10 and 9, and for an attack its HitEffectName and AfterEffectName. So it must reach the client before the missile's 0x5D.
    /// </summary>
    public static PlaintextPacket LineMissileHitList(uint missileNetId, IReadOnlyList<uint> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count is 0 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(targets), "a hit list names 1 to 32767 units");
        using var writer = ProtocolEncoding.Writer(7 + 4 * targets.Count, out var stream);
        writer.Write(LineMissileHitListOpcode);
        writer.Write(missileNetId);
        writer.Write((short)targets.Count);
        foreach (var target in targets) writer.Write(target);
        writer.Flush();
        // Encrypted like every packet of eight bytes or more (the 5-byte 0x5D is the plaintext one).
        return Packet(stream, LeaguePacketReliability.Reliable, targets.Select(Target).ToArray());
    }

    public static PlaintextPacket DestroyMissile(uint missileNetId)
    {
        using var writer = ProtocolEncoding.Writer(5, out var stream);
        writer.Write((byte)0x5D);
        writer.Write(missileNetId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, false, Effect(missileNetId));
    }

    public static PlaintextPacket FinishCast(uint casterNetId)
    {
        // The stop handler selects the active champion cast with ForceStop; the finish routine
        // executes any outstanding cast frame once, then ends its controller.
        // KeepAnimating | ForceSpellCast | ForceStop, without DestroyMissile.
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0x39);
        writer.Write(casterNetId);
        writer.Write((byte)0x07);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(casterNetId));
    }

    public static PlaintextPacket ModifyShield(
        uint netId, float amount, bool physical = true, bool magic = true, bool noFade = false)
    {
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x69);
        writer.Write(netId);
        writer.Write((byte)((physical ? 1 : 0) | (magic ? 2 : 0) | (noFade ? 4 : 0)));
        writer.Write(amount);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    // NpcBuffReplace; retail's buff replace handler reads slot/+5, running/+6,
    // duration/+10, caster/+14 (looked up; none found leaves the instance's caster as it is). It renews an existing native instance: the handler restarts the slot's timers (start = now - running,
    // end = start + duration) and calls the script instance's Renew, with no activation sound and no change of the stack count.
    public static PlaintextPacket RenewBuff(uint unitNetId, byte slot, float duration, uint casterNetId, float runningTime = 0.0f)
    {
        if (slot >= 64 || !float.IsFinite(duration) || duration < 0 || !float.IsFinite(runningTime))
            throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(18, out var stream);
        writer.Write((byte)0x32); writer.Write(unitNetId); writer.Write(slot);
        writer.Write(runningTime); writer.Write(duration); writer.Write(casterNetId);
        writer.Flush();
        var references = new List<LeaguePacketEntityReference> { Target(unitNetId) };
        if (casterNetId != 0) references.Add(Caster(casterNetId));
        return Packet(stream, LeaguePacketReliability.Reliable, references.ToArray());
    }

    /// <summary>
    /// NpcBuffUpdateCount (0x1F), for a buff instance the client already holds: slot (5), new stack
    /// count (6), duration (7), running time (11), caster (15). The 126 handler (the buff manager's packet
    /// handler) resizes the instance's stack to <c>count</c> and restarts its timers like a 0xBF add does, but plays no activation sound or voice and
    /// ignores the packet when the slot is empty. A count of zero would clear the instance (that is
    /// <see cref="RemoveBuff"/>'s job), so it is rejected.
    /// </summary>
    public static PlaintextPacket UpdateBuffCount(
        uint unitNetId, byte slot, byte count, float duration, uint casterNetId, float runningTime = 0.0f)
    {
        if (slot >= MaxBuffSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (count == 0) throw new ArgumentOutOfRangeException(nameof(count), "A zero count clears the buff.");
        if (!float.IsFinite(duration) || duration < 0 || !float.IsFinite(runningTime))
            throw new ArgumentOutOfRangeException(nameof(duration));
        using var writer = ProtocolEncoding.Writer(19, out var stream);
        writer.Write(BuffUpdateCountOpcode);
        writer.Write(unitNetId);
        writer.Write(slot);
        writer.Write(count);
        writer.Write(duration);
        writer.Write(runningTime);
        writer.Write(casterNetId);
        writer.Flush();
        var references = new List<LeaguePacketEntityReference> { Target(unitNetId) };
        if (casterNetId != 0) references.Add(Caster(casterNetId));
        return Packet(stream, LeaguePacketReliability.Reliable, references.ToArray());
    }

    /// <summary>
    /// Where the caster's net ID sits in the three buff packets that carry one, or -1. The stock client resolves it by net ID in each and
    /// carries on with no caster when the lookup fails, so 0 is a valid value in all three: 0xBF reads it at offset 0x15 and looks it up in the
    /// object map (a miss leaves the instance's caster set to none); 0x32 at offset 0xE and 0x1F at offset 0xF search the object table and skip updating
    /// the caster when nothing is found. The handlers read no other field that names a unit (the holder at +1 addresses the packet and is not read by them).
    /// </summary>
    internal static int BuffCasterOffset(byte opcode) => opcode switch
    {
        BuffAddOpcode => 21,
        BuffUpdateCountOpcode => 15,
        BuffReplaceOpcode => 14,
        _ => -1,
    };

    /// <summary>
    /// The same buff packet with no caster: the caster field zeroed and the caster reference dropped. A packet that is not a buff packet,
    /// or has no caster reference, is returned as it is.
    /// </summary>
    internal static PlaintextPacket WithoutCaster(PlaintextPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var bytes = packet.Plaintext.Span;
        var offset = bytes.IsEmpty ? -1 : BuffCasterOffset(bytes[0]);
        if (offset < 0 || bytes.Length < offset + 4 ||
            !packet.EntityReferences.Any(reference => reference.Role == LeaguePacketEntityRole.Caster))
            return packet;
        var copy = bytes.ToArray();
        Array.Clear(copy, offset, 4);
        return ProtocolEncoding.Packet(packet.Channel, copy, packet.Encrypt, packet.Reliability,
            packet.EntityReferences.Where(reference => reference.Role != LeaguePacketEntityRole.Caster).ToArray());
    }

    /// <summary>Reads back a 0xBF built by <see cref="AddBuff"/>.</summary>
    public static bool TryReadBuffAdd(ReadOnlySpan<byte> payload, out BuffAdd buff)
    {
        buff = default;
        if (payload.Length < 25 || payload[0] != BuffAddOpcode) return false;
        buff = new BuffAdd(BitConverter.ToUInt32(payload[1..5]), payload[5], payload[6],
            BitConverter.ToUInt32(payload[9..13]), BitConverter.ToSingle(payload[17..21]),
            BitConverter.ToUInt32(payload[21..25]), payload[7], payload[8],
            BitConverter.ToSingle(payload[13..17]));
        return true;
    }

    public static PlaintextPacket AddBuff(BuffAdd buff)
    {
        if (buff.BuffNameHash == 0)
            throw new ArgumentException("The client rejects a nameless buff.", nameof(buff));
        if (buff.Slot >= MaxBuffSlots)
            throw new ArgumentOutOfRangeException(nameof(buff), "Buff slot must be below 64.");
        using var writer = ProtocolEncoding.Writer(25, out var stream);
        writer.Write(BuffAddOpcode);
        writer.Write(buff.TargetNetId);
        writer.Write(buff.Slot);
        writer.Write(buff.BuffType);
        writer.Write(buff.Count);
        writer.Write(buff.IsHidden);
        writer.Write(buff.BuffNameHash);
        writer.Write(buff.RunningTime);
        writer.Write(buff.Duration);
        writer.Write(buff.CasterNetId);
        writer.Flush();
        var references = new List<LeaguePacketEntityReference> { Target(buff.TargetNetId) };
        if (buff.CasterNetId != 0) references.Add(Caster(buff.CasterNetId));
        return Packet(stream, LeaguePacketReliability.Reliable, references.ToArray());
    }

    public static PlaintextPacket RemoveBuff(uint targetNetId, byte slot, uint buffNameHash)
    {
        if (slot >= MaxBuffSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x7F);
        writer.Write(targetNetId);
        writer.Write(slot);
        writer.Write(buffNameHash);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Target(targetNetId));
    }

    private static void WritePosition(BinaryWriter writer, Position3 position)
    {
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
    }

    private static PlaintextPacket Packet(
        MemoryStream stream,
        LeaguePacketReliability reliability,
        bool encrypt,
        params LeaguePacketEntityReference[] references)
        => ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel,
            ProtocolEncoding.Finish(stream), encrypt, reliability, references);

    private static PlaintextPacket Packet(
        MemoryStream stream,
        LeaguePacketReliability reliability,
        params LeaguePacketEntityReference[] references)
        => Packet(stream, reliability, encrypt: true, references);

    private static LeaguePacketEntityReference Hero(uint id) => new(LeaguePacketEntityRole.Hero, id);
    private static LeaguePacketEntityReference Caster(uint id) => new(LeaguePacketEntityRole.Caster, id);
    private static LeaguePacketEntityReference Target(uint id) => new(LeaguePacketEntityRole.Target, id);
    private static LeaguePacketEntityReference Effect(uint id) => new(LeaguePacketEntityRole.Effect, id);
    private static LeaguePacketEntityReference Create(uint id) => new(LeaguePacketEntityRole.Create, id);
    private static LeaguePacketEntityReference World(uint id) => new(LeaguePacketEntityRole.World, id);
}
