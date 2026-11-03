using System.Buffers.Binary;
using System.Security.Cryptography;
using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

/// <summary>Patch-126 channel-zero key check used before a seat is reserved.</summary>
public static class SessionKeyCheck
{
    public const int Length = sizeof(ulong) * 3;

    public static byte[] Build(ulong playerId, LegacyBlowfishCipher cipher)
    {
        if (playerId == 0 || playerId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        ArgumentNullException.ThrowIfNull(cipher);
        Span<byte> plain = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(plain, playerId);
        var encrypted = cipher.Encrypt(plain);
        var result = new byte[Length];
        BinaryPrimitives.WriteUInt64LittleEndian(result, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(sizeof(ulong)), playerId);
        encrypted.CopyTo(result.AsSpan(sizeof(ulong) * 2));
        return result;
    }

    public static bool TryReadPlayerId(
        ReadOnlySpan<byte> keyCheck,
        LegacyBlowfishCipher cipher,
        out ulong playerId)
    {
        playerId = 0;
        ArgumentNullException.ThrowIfNull(cipher);
        // The leading eight bytes are an opaque native token.  The stock
        // server checks only the 24-byte shape, player-id field, and the
        // Blowfish block; captures contain non-zero values here.
        if (keyCheck.Length != Length)
            return false;

        playerId = BinaryPrimitives.ReadUInt64LittleEndian(keyCheck[sizeof(ulong)..]);
        if (playerId == 0 || playerId == ulong.MaxValue)
        {
            playerId = 0;
            return false;
        }

        Span<byte> plain = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(plain, playerId);
        var expected = cipher.Encrypt(plain);
        var valid = CryptographicOperations.FixedTimeEquals(
            keyCheck[(sizeof(ulong) * 2)..], expected);
        if (!valid) playerId = 0;
        return valid;
    }
}
