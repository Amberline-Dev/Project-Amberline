using System.Text.Json;
using Amberline.Network.Protocol;

namespace Amberline.Tests.Network;

public static class ProtocolBlowfishChecks
{
    [Check("protocol.blowfish")]
    public static void LegacyVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Fixture("blowfish.json")));
        var root = document.RootElement;
        var key = System.Text.Encoding.ASCII.GetBytes(root.GetProperty("keyAscii").GetString()!);
        var cipher = new LegacyBlowfishCipher(key);
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            var plaintext = Hex(vector, "plaintext");
            var encrypted = Hex(vector, "encrypted");
            var tail = Hex(vector, "tail");
            var expectedDecrypted = Hex(vector, "decryptedWithTail");
            TestAssert.Equal(encrypted, cipher.Encrypt(plaintext));
            var ciphertextWithTail = encrypted.Concat(tail).ToArray();
            TestAssert.Equal(expectedDecrypted, cipher.Decrypt(ciphertextWithTail));
        }

        foreach (var check in root.GetProperty("keyChecks").EnumerateArray())
            TestAssert.Equal(Hex(check, "signature"), cipher.Encrypt(Hex(check, "block")));

        TestAssert.Equal(
            new byte[] { 1, 2, 3 },
            cipher.Decrypt(new byte[] { 1, 2, 3 }));
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static byte[] Hex(JsonElement value, string property) =>
        Convert.FromHexString(value.GetProperty(property).GetString()!);
}
