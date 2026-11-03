using System.Security.Cryptography;
using Amberline.AirCompanion;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

public static class AirPreferenceChecks
{
    [Check("server.air-preferences-leaverdata-repair")]
    public static void LeaverDataRepairCreatesBackupAndPreservesTypedLocale()
    {
        var root = TemporaryDirectory("league-air-preferences");
        try
        {
            var path = WritePreferences(root, StockPreferences(withLeaverData: true));
            var before = File.ReadAllBytes(path);
            var beforeHash = Convert.ToHexString(SHA256.HashData(before));

            var result = LeaverDataRepair.RepairGlobalPreferences(root);
            TestAssert.Equal(LeaverDataRepairStatus.Repaired, result.Status);
            TestAssert.Equal(beforeHash, result.OriginalSha256);
            TestAssert.True(result.BackupPath is not null && File.Exists(result.BackupPath),
                "a repaired preference file must retain an atomic backup");
            TestAssert.Equal(before, File.ReadAllBytes(result.BackupPath!));
            TestAssert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
                "repair must remove the duplicate field from the committed file");

            var repaired = ReadRoot(File.ReadAllBytes(path));
            TestAssert.False(repaired.ContainsKey("leaverData"));
            TestAssert.Equal(LeaverDataRepair.UserLocalePropertiesAlias,
                ((AsObject)repaired["localeData"]).TypeName);
            TestAssert.Equal("Summoner", ((AsObject)repaired["localeData"])["summonerName"]);
            TestAssert.Equal("Login", repaired["loginName"]);

            var secondHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var second = LeaverDataRepair.RepairGlobalPreferences(root);
            TestAssert.Equal(LeaverDataRepairStatus.AlreadyClean, second.Status);
            TestAssert.Equal(secondHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-preferences-leaverdata-repair-guards")]
    public static void LeaverDataRepairSkipsMismatchUnknownAndMalformedFiles()
    {
        var root = TemporaryDirectory("league-air-preferences-guards");
        try
        {
            var mismatch = StockPreferences(withLeaverData: true);
            ((AsObject)mismatch["leaverData"])["gameId"] = 999;
            AssertUnchanged(root, mismatch, "duplicate-mismatch");

            var unknown = StockPreferences(withLeaverData: true);
            unknown["unknownObject"] = new AsObject("unregistered.Type") { ["field"] = "value" };
            AssertUnchanged(root, unknown, "unsupported-root-field");

            var malformed = Path.Combine(root, "preferences", "global", "global.properties");
            File.WriteAllBytes(malformed, [0x0A, 0xFF, 0x01]);
            var malformedBefore = File.ReadAllBytes(malformed);
            var malformedResult = LeaverDataRepair.RepairGlobalPreferences(root);
            TestAssert.Equal(LeaverDataRepairStatus.Skipped, malformedResult.Status);
            TestAssert.Equal(malformedBefore, File.ReadAllBytes(malformed));

            var oversized = new byte[LeaverDataRepair.MaxPreferenceBytes + 1];
            File.WriteAllBytes(malformed, oversized);
            var oversizedResult = LeaverDataRepair.RepairGlobalPreferences(root);
            TestAssert.Equal(LeaverDataRepairStatus.Skipped, oversizedResult.Status);
            TestAssert.Equal("size-limit", oversizedResult.Reason);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-preferences-exclusive-lease")]
    public static void AirCopyLeaseIsExclusiveUntilDisposed()
    {
        var root = TemporaryDirectory("league-air-preferences-lease");
        try
        {
            using var first = LeaverDataRepair.AcquireAirCopyLease(root);
            TestAssert.Throws<IOException>(() => LeaverDataRepair.AcquireAirCopyLease(root));
        }
        finally { DeleteDirectory(root); }
    }

    private static void AssertUnchanged(string root, AsObject value, string reason)
    {
        var path = WritePreferences(root, value);
        var before = File.ReadAllBytes(path);
        var result = LeaverDataRepair.RepairGlobalPreferences(root);
        TestAssert.Equal(LeaverDataRepairStatus.Skipped, result.Status);
        TestAssert.Equal(reason, result.Reason);
        TestAssert.Equal(before, File.ReadAllBytes(path));
    }

    private static AsObject StockPreferences(bool withLeaverData)
    {
        var locale = new AsObject(LeaverDataRepair.UserLocalePropertiesAlias)
        {
            ["languageSaved"] = true,
            ["localId"] = 123,
            ["summonerName"] = "Summoner",
            ["timeZone"] = 456.0,
        };
        var root = new AsObject(LeaverDataRepair.GlobalPrefsAlias)
        {
            ["mute"] = false,
            ["enableAnimations"] = true,
            ["loginMusicMute"] = false,
            ["loginName"] = "Login",
            ["newMusicVolume"] = 0.5,
            ["rememberLogin"] = true,
            ["musicMute"] = false,
            ["localeData"] = locale,
            ["volume"] = 0.8,
        };
        if (withLeaverData)
        {
            root["leaverData"] = new AsObject
            {
                ["gameTime"] = 456,
                ["summonerName"] = "Summoner",
                ["gameId"] = 123.0,
                ["gameInProgress"] = true,
            };
        }
        return root;
    }

    private static string WritePreferences(string root, AsObject value)
    {
        var path = LeaverDataRepair.GlobalPreferencesPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new AmfWriter(stream, new SerializationContext());
        writer.WriteAmf3Item(value);
        return path;
    }

    private static AsObject ReadRoot(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new AmfReader(stream, new SerializationContext());
        var root = (AsObject)reader.ReadAmf3Item();
        TestAssert.False(reader.DataAvailable, "preference fixture must contain one complete AMF3 value");
        return root;
    }

    private static string TemporaryDirectory(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
