using System.Text;

namespace Amberline.Network.Protocol;

public enum LeaguePacketReliability : byte
{
    Unreliable = 0,
    Reliable = 1,
    Unsequenced = 2
}

public enum LeaguePacketEntityRole : byte
{
    Hero,
    Target,
    Effect,
    Binding,
    Create,
    World,
    /// <summary>
    /// A unit the client resolves by net ID and tolerates missing: the caster field of the buff packets (0xBF, 0x32, 0x1F; the
    /// client's lookup of an unknown ID leaves the buff without a caster). Delivery needs the holder known, not this unit;
    /// <c>ReplicationView.ForClient</c> sends 0 in its place when the client was not told about it.
    /// </summary>
    Caster
}

public readonly record struct LeaguePacketEntityReference(
    LeaguePacketEntityRole Role,
    uint NetId);

public sealed class PlaintextPacket
{
    private readonly byte[] _plaintext;
    private readonly IReadOnlyList<LeaguePacketEntityReference> _entityReferences;

    internal PlaintextPacket(
        byte channel,
        byte[] plaintext,
        bool encrypt,
        LeaguePacketReliability reliability,
        IReadOnlyList<LeaguePacketEntityReference>? entityReferences = null)
    {
        _plaintext = plaintext?.ToArray() ?? throw new ArgumentNullException(nameof(plaintext));
        _entityReferences = entityReferences is null
            ? Array.Empty<LeaguePacketEntityReference>()
            : Array.AsReadOnly(entityReferences.ToArray());
        Channel = channel;
        Encrypt = encrypt;
        Reliability = reliability;
    }

    public byte Channel { get; }

    public ReadOnlyMemory<byte> Plaintext => _plaintext;

    public bool Encrypt { get; }

    public LeaguePacketReliability Reliability { get; }

    public IReadOnlyList<LeaguePacketEntityReference> EntityReferences => _entityReferences;

    /// <summary>
    /// Applies the ordinary transport rule: encrypted packets shorter than one
    /// Blowfish block remain plaintext. Use the retained cipher context directly
    /// when raw padded encryption is required.
    /// </summary>
    public byte[] ToWireBytes(LegacyBlowfishCipher cipher)
    {
        if (!Encrypt || _plaintext.Length < LegacyBlowfish.BlockSize)
            return _plaintext.ToArray();
        ArgumentNullException.ThrowIfNull(cipher);
        return cipher.Encrypt(_plaintext);
    }
}

public static class LeagueProtocolConstants
{
    public const byte VariantChannel = 3;
    public const byte CombatChannel = 3;
    public const byte LowPriorityChannel = 4;
    public const byte LoadingChannel = 6;
    public const uint VariantNetId = 0;
    public const ulong DefaultPlayerId = 1;
    public const string VersionString = "Version 1.0.0.126 [PUBLIC]";
}

internal static class ProtocolEncoding
{
    internal static readonly Encoding Ascii = Encoding.GetEncoding(
        20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    internal static PlaintextPacket Packet(
        byte channel,
        byte[] plaintext,
        bool encrypt,
        LeaguePacketReliability reliability,
        params LeaguePacketEntityReference[] references)
        => new(channel, plaintext, encrypt, reliability, references);

    internal static uint HashString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        uint hash = 0;
        foreach (var character in value)
        {
            var lower = char.ToLowerInvariant(character);
            hash = unchecked(hash * 0x10u + lower);
            var high = hash & 0xF0000000u;
            if (high != 0)
                hash = unchecked((hash ^ (high >> 24) ^ high));
        }

        return hash;
    }

    internal static void WriteFixedAscii(BinaryWriter writer, string value, int width, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (width < 0 || maxBytes < 0 || maxBytes > width)
            throw new ArgumentOutOfRangeException(nameof(width));

        var bytes = Ascii.GetBytes(value);
        if (bytes.Length > maxBytes)
            bytes = bytes[..maxBytes];
        writer.Write(bytes);
        writer.Write(new byte[width - bytes.Length]);
    }

    internal static void WriteSizedAscii(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Ascii.GetBytes(value);
        if (bytes.Length >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    internal static byte[] Finish(MemoryStream stream)
        => stream.ToArray();

    internal static BinaryWriter Writer(int capacity, out MemoryStream stream)
    {
        stream = new MemoryStream(capacity);
        return new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    }

    internal static void RequireAsciiLength(string value, int maxBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Ascii.GetByteCount(value) > maxBytes)
            throw new ArgumentOutOfRangeException(parameterName, $"ASCII value exceeds {maxBytes} bytes.");
    }

    internal static void RequirePlayerId(ulong playerId, string parameterName = "playerId")
    {
        if (playerId == 0 || playerId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Player IDs must be nonzero and not the empty-slot marker.");
    }

    internal static float Float32(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Value must be a finite float32.");
        return (float)value;
    }
}
