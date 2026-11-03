using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Amberline.Data;

public sealed class InibinException : Exception
{
    public InibinException(string message) : base(message) { }
    public InibinException(string message, Exception inner) : base(message, inner) { }
}

public enum InibinValueKind
{
    UInt32,
    Float32,
    Double,
    UInt16,
    Byte,
    Boolean,
    Integer,
    String,
    /// <summary>
    /// 3 or 4 unsigned bytes (segments 6 and 10), stored raw in <see cref="InibinValue.Vector"/>. The stock client
    /// reads them divided by ten (<see cref="InibinValue.AsClientVector"/>).
    /// </summary>
    ByteVector,
    /// <summary>3 or 4 little-endian floats (segments 7 and 11).</summary>
    FloatVector
}

/// <summary>A scalar value from an inibin segment, retaining its wire/coercion type.</summary>
public readonly record struct InibinValue
{
    public InibinValueKind Kind { get; }
    public uint UInt32 { get; }
    /// <summary>
    /// The 16-bit segment's stored bytes, unsigned. The stock client reads this segment sign-extended
    /// (see <see cref="AsDouble"/>); this raw decode exists only for the source-backed corpus digests.
    /// </summary>
    public ushort UInt16 { get; }
    public byte Byte { get; }
    public bool Boolean { get; }
    public BigInteger Integer { get; }
    public double Number { get; }
    public string? Text { get; }
    /// <summary>Components of a <see cref="InibinValueKind.ByteVector"/> or <see cref="InibinValueKind.FloatVector"/>; null otherwise.</summary>
    public IReadOnlyList<double>? Vector { get; }
    /// <summary>
    /// The float32 the stock client reads for a byte/10 value (<see cref="FromByteTenths"/>); null for every other kind.
    /// <see cref="Number"/> keeps the exact decimal b/10 that the source-backed corpus digests recorded.
    /// </summary>
    private readonly double? _clientNumber;

    private InibinValue(InibinValueKind kind, uint uint32 = 0, ushort uint16 = 0,
        byte @byte = 0, bool boolean = false, BigInteger integer = default,
        double number = 0, string? text = null, IReadOnlyList<double>? vector = null, double? clientNumber = null)
    {
        _clientNumber = clientNumber;
        Kind = kind;
        UInt32 = uint32;
        UInt16 = uint16;
        Byte = @byte;
        Boolean = boolean;
        Integer = integer;
        Number = number;
        Text = text;
        Vector = vector;
    }

    public static InibinValue FromUInt32(uint value) => new(InibinValueKind.UInt32, uint32: value);
    public static InibinValue FromFloat32(float value) => new(InibinValueKind.Float32, number: value);
    public static InibinValue FromDouble(double value) => new(InibinValueKind.Double, number: value);

    /// <summary>
    /// A segment-2 byte. The stock client divides it by ten as <c>(float)b * 0.1f</c> (the client's raw float reader,
    /// which uses the float 0.1), which differs from the correctly rounded b/10 by one float32
    /// step for 50 of the 256 byte values (9 reads 0.90000004, 13 reads 1.3000001, ...). <see cref="AsDouble"/> returns the client value.
    /// </summary>
    public static InibinValue FromByteTenths(byte value) =>
        new(InibinValueKind.Double, number: value / 10.0, clientNumber: (double)((float)value * 0.1f));
    public static InibinValue FromUInt16(ushort value) => new(InibinValueKind.UInt16, uint16: value);
    public static InibinValue FromByte(byte value) => new(InibinValueKind.Byte, @byte: value);
    public static InibinValue FromBoolean(bool value) => new(InibinValueKind.Boolean, boolean: value);
    public static InibinValue FromInteger(BigInteger value) => new(InibinValueKind.Integer, integer: value);
    public static InibinValue FromString(string value) => new(InibinValueKind.String, text: value);
    public static InibinValue FromByteVector(IReadOnlyList<double> components) => new(InibinValueKind.ByteVector, vector: components);
    public static InibinValue FromFloatVector(IReadOnlyList<double> components) => new(InibinValueKind.FloatVector, vector: components);

    public bool IsNumeric => Kind is InibinValueKind.UInt32 or InibinValueKind.Float32
        or InibinValueKind.Double or InibinValueKind.UInt16 or InibinValueKind.Byte
        or InibinValueKind.Integer;

    /// <summary>
    /// The number the stock client reads for this value, which is what every gameplay consumer needs.
    /// The client's raw integer lookup
    /// sign-extends the 16-bit integer segment ((int)(short)), so a stored 65535 is -1 and 65401 is -135; the
    /// other segments keep their decode (32-bit as stored - none shipped reaches 0x80000000, where the client's
    /// int would wrap - byte unsigned, byte/10 and float as stored). 56
    /// shipped values sit at 32768 or above, e.g. BaseStaticMPRegen, AttackDelayOffsetPercent and the
    /// HeroSpawnOffsets facing degrees. The raw unsigned decode stays on <see cref="UInt16"/> (and in
    /// <see cref="ToString"/>) only so the source-backed inibin and gamedata corpora, which recorded it before
    /// this was known, stay valid; nothing that models gameplay should read that.
    /// </summary>
    public double AsDouble() => Kind switch
    {
        InibinValueKind.UInt32 => UInt32,
        InibinValueKind.Float32 => Number,
        InibinValueKind.Double => _clientNumber ?? Number,
        InibinValueKind.UInt16 => (short)UInt16,
        InibinValueKind.Byte => Byte,
        InibinValueKind.Integer => (double)Integer,
        _ => throw new InvalidCastException($"Inibin value is {Kind}, not numeric")
    };

    /// <summary>
    /// <see cref="AsDouble"/> plus the booleans the client float getter reads as 0/1
    /// (the client's float getter falls back from float and integer to boolean).
    /// </summary>
    public double AsClientNumber() => Kind == InibinValueKind.Boolean ? (Boolean ? 1.0 : 0.0) : AsDouble();

    /// <summary>
    /// The value of the client's generic float getter: the float and fixed-point segments, then the integer segments, then the boolean segment (true 1.0, false 0.0),
    /// then <c>atof</c> of a string (its numeric prefix, 0 when there is none). A character or spell number the compiler packed
    /// as a boolean or kept as text is therefore a present value, not a missing key. Vectors are not numbers.
    /// </summary>
    public double AsClientFloat() => Kind switch
    {
        InibinValueKind.Boolean => Boolean ? 1.0 : 0.0,
        InibinValueKind.String => AtofPrefix(Text!),
        InibinValueKind.ByteVector or InibinValueKind.FloatVector =>
            throw new InvalidCastException($"Inibin value is {Kind}, not a number"),
        _ => AsDouble()
    };

    /// <summary>The leading decimal number of <paramref name="text"/> the way the CRT <c>atof</c> reads it, 0 when it has none.</summary>
    public static double AtofPrefix(string text) => TryAtofPrefix(text, out var number) ? number : 0.0;

    /// <summary>
    /// Whether <paramref name="text"/> starts with a decimal number and what <c>atof</c> reads from it: optional white space and sign,
    /// digits with an optional fraction, an optional exponent. "4.5.0000" reads 4.5 (a shipped GatlingGun and XenZhaoSweep value).
    /// </summary>
    public static bool TryAtofPrefix(string text, out double number)
    {
        number = 0.0;
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
        var digits = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9') { i++; digits++; }
        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && text[i] is >= '0' and <= '9') { i++; digits++; }
        }
        if (digits == 0) return false;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var j = i + 1;
            if (j < text.Length && (text[j] == '+' || text[j] == '-')) j++;
            if (j < text.Length && text[j] is >= '0' and <= '9')
            {
                while (j < text.Length && text[j] is >= '0' and <= '9') j++;
                i = j;
            }
        }
        return double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// The value of the client's int getter: the integer
    /// segments, then a float truncated toward zero, then a boolean (0 or 1), then <c>atoi</c> of a string. A designer's typo that kept text
    /// after the number therefore reads its leading integer: Twisted Fate's Destiny stores <c>SpellData*TargettingType</c> as
    /// <c>"4CastRangeUseMapScaling=1"</c>, which the client reads as 4 (a self area cast).
    /// </summary>
    public int AsClientInt() => Kind switch
    {
        InibinValueKind.Boolean => Boolean ? 1 : 0,
        InibinValueKind.String => AtoiPrefix(Text!),
        InibinValueKind.ByteVector or InibinValueKind.FloatVector => throw new InvalidCastException($"Inibin value is {Kind}, not a number"),
        _ => (int)Math.Clamp(AsDouble(), int.MinValue, int.MaxValue)
    };

    /// <summary>The integer the CRT <c>atoi</c> reads from <paramref name="text"/>: white space, an optional sign, the leading digits; 0 when there are none.</summary>
    public static int AtoiPrefix(string text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) negative = text[i++] == '-';
        long value = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9' && value < int.MaxValue) value = value * 10 + (text[i++] - '0');
        value = Math.Min(value, int.MaxValue);
        return (int)(negative ? -value : value);
    }

    /// <summary>The integer the client reads (<see cref="AsDouble"/> sign handling for the 16-bit segment).</summary>
    public BigInteger AsInteger() => Kind switch
    {
        InibinValueKind.UInt32 => UInt32,
        InibinValueKind.UInt16 => (short)UInt16,
        InibinValueKind.Byte => Byte,
        InibinValueKind.Integer => Integer,
        _ => throw new InvalidCastException($"Inibin value is {Kind}, not integral")
    };

    public string AsString() => Kind == InibinValueKind.String
        ? Text!
        : throw new InvalidCastException($"Inibin value is {Kind}, not a string");

    public bool AsBoolean() => Kind == InibinValueKind.Boolean
        ? Boolean
        : throw new InvalidCastException($"Inibin value is {Kind}, not boolean");

    public IReadOnlyList<double> AsVector() => Vector
        ?? throw new InvalidCastException($"Inibin value is {Kind}, not a vector");

    /// <summary>
    /// The vector the stock client reads. The client's raw vector readers (LookupRawFloat3/4) multiply by the float 0.1 and take a byte vector as
    /// <c>byte / 10</c> in float32; a float vector is read as stored. <see cref="AsVector"/> keeps the raw bytes
    /// only so the source-backed inibin digests stay valid. Gameplay consumers must use this accessor
    /// (HeroSpawnOffsets <c>Order1*Pos1</c> is the byte vector 100,0,100, which the client reads as 10,0,10).
    /// </summary>
    public IReadOnlyList<double> AsClientVector() =>
        Kind == InibinValueKind.ByteVector ? AsVector().Select(part => (double)((float)part * 0.1f)).ToArray() : AsVector();

    public override string ToString() => Kind switch
    {
        InibinValueKind.String => Text ?? string.Empty,
        InibinValueKind.Boolean => Boolean ? "True" : "False",
        InibinValueKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        InibinValueKind.UInt32 => UInt32.ToString(CultureInfo.InvariantCulture),
        InibinValueKind.UInt16 => UInt16.ToString(CultureInfo.InvariantCulture),
        InibinValueKind.Byte => Byte.ToString(CultureInfo.InvariantCulture),
        InibinValueKind.ByteVector or InibinValueKind.FloatVector =>
            "(" + string.Join(", ", Vector!.Select(part => part.ToString("R", CultureInfo.InvariantCulture))) + ")",
        _ => Number.ToString("R", CultureInfo.InvariantCulture)
    };
}

/// <summary>The fully parsed key/value map from one version-2 inibin file.</summary>
public sealed class InibinData
{
    private readonly Dictionary<uint, InibinValue> _values;

    internal InibinData(Dictionary<uint, InibinValue> values) => _values = values;

    public int Count => _values.Count;
    public IEnumerable<uint> Keys => _values.Keys;
    public IEnumerable<InibinValue> Values => _values.Values;
    public InibinValue this[uint key] => _values[key];
    public bool TryGetValue(uint key, out InibinValue value) => _values.TryGetValue(key, out value);
    public bool ContainsKey(uint key) => _values.ContainsKey(key);
}

/// <summary>Strict reader for Riot's version-2 .inibin format.</summary>
public static class InibinReader
{
    private const int StringBit = 12;
    private static readonly Regex Numeric = new(
        "^[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][+-]?\\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static uint KeyHash(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint hash = 0;
        foreach (var c in name)
            hash = unchecked((uint)(char.ToLowerInvariant(c) + 65599UL * hash));
        return hash;
    }

    public static InibinData Load(string path, bool raw = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InibinException($"cannot read {path}: {ex.Message}", ex);
        }
        try
        {
            return Parse(data, raw);
        }
        catch (InibinException ex)
        {
            throw new InibinException($"{path}: {ex.Message}", ex);
        }
    }

    public static InibinData LoadData(string relativePath, bool raw = false) => Load(GameDataPaths.Default.Data(relativePath), raw);

    public static InibinData Load(GameDataPaths paths, string relativePath, bool raw = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Load(paths.Data(relativePath), raw);
    }

    public static InibinData Parse(ReadOnlySpan<byte> data, bool raw = false)
    {
        if (data.Length < 5)
            throw new InibinException($"file too short: {data.Length} bytes");
        if (data[0] != 2)
            throw new InibinException($"unsupported inibin version {data[0]}, expected 2");

        var stringLength = ReadUInt16(data, 1);
        var bitMask = ReadUInt16(data, 3);
        var stringStart = data.Length - stringLength;
        if (stringStart < 5)
            throw new InibinException($"string block length {stringLength} exceeds file size {data.Length}");

        var cursor = 5;
        var values = new Dictionary<uint, InibinValue>();
        for (var bit = 0; bit < 16; bit++)
        {
            if ((bitMask & (1 << bit)) == 0)
                continue;
            var count = ReadUInt16(data, ref cursor, stringStart);
            var keys = new uint[count];
            for (var i = 0; i < count; i++)
                keys[i] = ReadUInt32(data, ref cursor, stringStart);

            if (bit == StringBit)
            {
                for (var i = 0; i < count; i++)
                {
                    var relative = ReadUInt16(data, ref cursor, stringStart);
                    if (relative >= stringLength)
                        throw new InibinException($"string offset {relative} is outside string block length {stringLength}");
                    var start = stringStart + relative;
                    var end = data[start..].IndexOf((byte)0);
                    if (end < 0)
                        throw new InibinException("unterminated string in blob");
                    var text = Encoding.Latin1.GetString(data.Slice(start, end));
                    values[keys[i]] = raw ? InibinValue.FromString(text) : CoerceString(text);
                }
                continue;
            }

            if (bit is >= 0 and <= 5)
            {
                if (bit == 5)
                {
                    var byteCount = (count + 7) / 8;
                    Ensure(data, cursor, byteCount, stringStart);
                    for (var i = 0; i < count; i++)
                        values[keys[i]] = InibinValue.FromBoolean((data[cursor + i / 8] & (1 << (i & 7))) != 0);
                    cursor += byteCount;
                }
                else
                {
                    for (var i = 0; i < count; i++)
                        values[keys[i]] = ReadSegmentValue(bit, data, ref cursor, stringStart);
                }
                continue;
            }

            // Vector segments (see ReadVector). Bits 8 and 9 (2-component vectors) occur in none of the 3140 shipped inibin
            // files; the client reads them (its list reader, LookupRawFloat2), so they are decoded the same way.
            if (bit is >= 6 and <= 11)
            {
                for (var i = 0; i < count; i++)
                    values[keys[i]] = ReadVector(bit, data, ref cursor, stringStart);
                continue;
            }

            throw new InibinException($"unhandled segment bit {bit} at offset {cursor}");
        }

        if (cursor != stringStart)
            throw new InibinException($"parse cursor landed at {cursor}, expected {stringStart} (file {data.Length} bytes, string block {stringLength})");
        return new InibinData(values);
    }

    public static InibinValue CoerceString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (!Numeric.IsMatch(trimmed))
            return InibinValue.FromString(value);
        if (!trimmed.Contains('.') && !trimmed.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            if (BigInteger.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                return InibinValue.FromInteger(integer);
            return InibinValue.FromString(value);
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return InibinValue.FromDouble(number);
        return InibinValue.FromString(value);
    }

    private static InibinValue ReadSegmentValue(int bit, ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        return bit switch
        {
            0 => InibinValue.FromUInt32(ReadUInt32(data, ref cursor, limit)),
            1 => InibinValue.FromFloat32(ReadSingle(data, ref cursor, limit)),
            2 => InibinValue.FromByteTenths(ReadByte(data, ref cursor, limit)),
            3 => InibinValue.FromUInt16(ReadUInt16(data, ref cursor, limit)),
            4 => InibinValue.FromByte(ReadByte(data, ref cursor, limit)),
            _ => throw new InibinException($"unhandled segment bit {bit} at offset {cursor}")
        };
    }

    /// <summary>
    /// Segment 6 = 3 bytes, 7 = 3 floats, 8 = 2 bytes, 9 = 2 floats (client ReadList element sizes, unused by the shipped files),
    /// 10 = 4 bytes, 11 = 4 floats. Evidence: every one of the 24
    /// shipped inibin files that use these segments (Karma, Renekton, Rumble, Tryndamere, Udyr and its
    /// forms, HeroSpawnOffsets, GeneralCharacterData, Odin skeletons, LoadingScreen settings) ends its
    /// last segment exactly at the string block with these sizes, and the sizes are jointly determined
    /// by files that mix segments (for example Karma holds one bit-10 entry only). The byte components
    /// are returned as stored; the stock client divides them by ten like segment 2
    /// (<see cref="InibinValue.AsClientVector"/>, which the 16 October 2026 spawn fix added).
    /// </summary>
    private static InibinValue ReadVector(int bit, ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        var components = bit is 6 or 7 ? 3 : bit is 8 or 9 ? 2 : 4;
        var parts = new double[components];
        var isFloat = bit is 7 or 9 or 11;
        for (var i = 0; i < components; i++)
            parts[i] = isFloat ? ReadSingle(data, ref cursor, limit) : ReadByte(data, ref cursor, limit);
        return isFloat ? InibinValue.FromFloatVector(parts) : InibinValue.FromByteVector(parts);
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        Ensure(data, cursor, 1, limit);
        return data[cursor++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        Ensure(data, cursor, 2, limit);
        var value = (ushort)(data[cursor] | data[cursor + 1] << 8);
        cursor += 2;
        return value;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2, data.Length);
        return (ushort)(data[offset] | data[offset + 1] << 8);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        Ensure(data, cursor, 4, limit);
        var value = (uint)(data[cursor] | data[cursor + 1] << 8 | data[cursor + 2] << 16 | data[cursor + 3] << 24);
        cursor += 4;
        return value;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, ref int cursor, int limit)
    {
        return BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32(data, ref cursor, limit)));
    }

    private static void Ensure(ReadOnlySpan<byte> data, int offset, int count, int limit)
    {
        if (offset < 0 || count < 0 || offset > limit - count || offset > data.Length - count)
            throw new InibinException("truncated inibin");
    }
}
