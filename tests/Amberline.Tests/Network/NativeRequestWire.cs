using Amberline.Network.Protocol;

namespace Amberline.Tests;

internal static class NativeRequestWire
{
    /// <summary>Native C2S encryption leaves the short tail raw; notifications remain padded.</summary>
    internal static byte[] Encrypt(LegacyBlowfishCipher cipher, ReadOnlySpan<byte> payload)
    {
        var aligned = payload.Length / LegacyBlowfishCipher.BlockSize *
            LegacyBlowfishCipher.BlockSize;
        return [.. cipher.Encrypt(payload[..aligned]), .. payload[aligned..]];
    }
}
