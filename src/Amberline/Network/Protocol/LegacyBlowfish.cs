using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Amberline.Network.Protocol;

/// <summary>A single-owner legacy Blowfish ECB context with a reused key schedule.</summary>
public sealed class LegacyBlowfishCipher
{
    public const int BlockSize = 8;

    private readonly BlowfishEngine _encryptor;
    private readonly BlowfishEngine _decryptor;

    public LegacyBlowfishCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 4 or > 56)
            throw new ArgumentException("Blowfish keys must be 4 to 56 bytes.", nameof(key));

        var keyBytes = key.ToArray();
        _encryptor = new BlowfishEngine();
        _decryptor = new BlowfishEngine();
        _encryptor.Init(true, new KeyParameter(keyBytes));
        _decryptor.Init(false, new KeyParameter(keyBytes));
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length == 0)
            return Array.Empty<byte>();

        var paddedLength = checked((plaintext.Length + (BlockSize - 1)) / BlockSize * BlockSize);
        var padded = new byte[paddedLength];
        plaintext.CopyTo(padded);
        return Process(_encryptor, padded);
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        var alignedLength = ciphertext.Length - ciphertext.Length % BlockSize;
        if (alignedLength == 0)
            return ciphertext.ToArray();

        var result = new byte[ciphertext.Length];
        for (var offset = 0; offset < alignedLength; offset += BlockSize)
            _decryptor.ProcessBlock(ciphertext.Slice(offset, BlockSize), result.AsSpan(offset, BlockSize));
        ciphertext[alignedLength..].CopyTo(result.AsSpan(alignedLength));
        return result;
    }

    private static byte[] Process(BlowfishEngine engine, byte[] input)
    {
        var output = new byte[input.Length];
        for (var offset = 0; offset < input.Length; offset += BlockSize)
            engine.ProcessBlock(input.AsSpan(offset, BlockSize), output.AsSpan(offset, BlockSize));
        return output;
    }
}

/// <summary>Factory for the fixed legacy key used by patch 126.</summary>
public static class LegacyBlowfish
{
    public const int BlockSize = LegacyBlowfishCipher.BlockSize;
    public const string DefaultKeyAscii = "1234567890123456";

    public static LegacyBlowfishCipher CreateDefault() =>
        new(Encoding.ASCII.GetBytes(DefaultKeyAscii));
}
