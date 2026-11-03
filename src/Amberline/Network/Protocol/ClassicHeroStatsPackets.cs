using System;
using System.Collections.Generic;
using System.IO;
using Amberline.Data;

namespace Amberline.Network.Protocol;

public readonly record struct ClassicHeroStatValue
{
    private ClassicHeroStatValue(ClassicHeroStatKind kind, int int32, float float32,
        string? text, long longLong)
    {
        Kind = kind;
        Int32 = int32;
        Float32 = float32;
        Text = text;
        LongLong = longLong;
    }

    public ClassicHeroStatKind Kind { get; }
    public int Int32 { get; }
    public float Float32 { get; }
    public string? Text { get; }
    public long LongLong { get; }

    public static ClassicHeroStatValue FromInt(int value) =>
        new(ClassicHeroStatKind.Int, value, 0.0f, null, 0L);

    public static ClassicHeroStatValue FromFloat(float value) =>
        new(ClassicHeroStatKind.Float, 0, value, null, 0L);

    public static ClassicHeroStatValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ClassicHeroStatKind.String, 0, 0.0f, value, 0L);
    }

    public static ClassicHeroStatValue FromLongLong(long value) =>
        new(ClassicHeroStatKind.LongLong, 0, 0.0f, null, value);
}

/// <summary>Strict encoder for native Classic 0x4B snapshots.</summary>
public static class ClassicHeroStatsPackets
{
    public const byte Opcode = 0x4B;

    public static PlaintextPacket HeroStats(
        uint heroNetId,
        Amberline.Data.ClassicHeroStatsSchema schema,
        IReadOnlyDictionary<string, ClassicHeroStatValue> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        if (heroNetId == 0) throw new ArgumentOutOfRangeException(nameof(heroNetId));
        if (values.Count != schema.Definitions.Count)
            throw new ArgumentException("Classic HeroStats values must contain exactly the schema fields.", nameof(values));

        foreach (var definition in schema.Definitions)
        {
            if (!values.TryGetValue(definition.Name, out var value))
                throw new ArgumentException($"Missing Classic HeroStats value {definition.Name}.", nameof(values));
            if (value.Kind != definition.Kind)
                throw new ArgumentException($"Classic HeroStats {definition.Name} is {definition.Kind}, got {value.Kind}.", nameof(values));
        }
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, ProtocolEncoding.Ascii, leaveOpen: true))
        {
            foreach (var definition in schema.Definitions)
                WriteValue(writer, definition, values[definition.Name]);
            writer.Flush();
        }

        var count = checked(4L + payload.Length);
        if (count > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(values), "Classic HeroStats payload is too large.");
        using var packet = new MemoryStream(checked(9 + (int)payload.Length));
        using (var writer = new BinaryWriter(packet, ProtocolEncoding.Ascii, leaveOpen: true))
        {
            writer.Write(Opcode);
            writer.Write(heroNetId);
            writer.Write((uint)count);
            writer.Write(payload.ToArray());
            writer.Flush();
        }

        // The packet is a complete collection snapshot. Carry the hero
        // reference as well as the native header so delivery readiness follows
        // the same known-entity guard as other hero packets.
        return ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel,
            packet.ToArray(), encrypt: true, LeaguePacketReliability.Reliable,
            new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    private static void WriteValue(BinaryWriter writer,
        Amberline.Data.ClassicHeroStatDefinition definition, ClassicHeroStatValue value)
    {
        switch (definition.Kind)
        {
            case ClassicHeroStatKind.Int:
                writer.Write(value.Int32);
                break;
            case ClassicHeroStatKind.Float:
                if (!float.IsFinite(value.Float32))
                    throw new ArgumentOutOfRangeException(nameof(value), $"{definition.Name} must be finite.");
                writer.Write(value.Float32);
                break;
            case ClassicHeroStatKind.String:
                var text = value.Text ?? throw new ArgumentException($"{definition.Name} cannot be null.", nameof(value));
                if (text.IndexOf('\0') >= 0)
                    throw new ArgumentException($"{definition.Name} cannot contain NUL.", nameof(value));
                var bytes = ProtocolEncoding.Ascii.GetBytes(text);
                if (bytes.Length >= 255)
                    throw new ArgumentOutOfRangeException(nameof(value), $"{definition.Name} must be shorter than 255 bytes.");
                writer.Write((short)bytes.Length);
                writer.Write(bytes);
                break;
            case ClassicHeroStatKind.LongLong:
                // Patch 1.0.0.126 initializes LONGLONG HeroStat values with
                // the INT holder, so StatsDataFromNetwork consumes four bytes
                // for this field. Keep the logical schema kind while matching
                // the native holder and reject values the stock client cannot
                // represent in that holder.
                writer.Write(checked((int)value.LongLong));
                break;
            default:
                throw new InvalidDataException($"Unsupported Classic HeroStats kind {definition.Kind}");
        }
    }

}
