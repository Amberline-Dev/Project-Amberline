using System.Security.Cryptography;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

public static partial class AirPlayerInstallChecks
{
    /// <summary>
    /// Batch 857 (design step 4): the player install is locked to the player's Windows account. A link is refused as the install folder, and so is a
    /// folder of other files (review LOW 10); the install is locked when it is made and the clients moved in from a folder with the drive's
    /// permissions take its own; the shortcut's start makes a full pass and records it; the tool's update switches in a new tool that takes the lock
    /// and drops the record, and the next start walks it all. The folders above the throwaway root are this PC's: the walk stops at it.
    /// </summary>
    [Check("server.air-player-install-locked", TimeoutSeconds = 300)]
    public static void ThePlayerInstallIsLockedWhenMadeAtStartAndAfterAnUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-install-lock");
        try
        {
            var player = InstallLockChecks.Hazard(Path.Combine(root, "player"));
            var fixture = Fixture.Make(player);
            HostLoginCopyChecks.Junction(fixture.Install, Path.Combine(player, "bootstrap"));
            Refused(() => fixture.Plan(), $"{fixture.Install} is a link to another place. Choose another install folder.");
            Directory.Delete(fixture.Install);
            Directory.CreateDirectory(fixture.Install);
            File.WriteAllText(Path.Combine(fixture.Install, "their.txt"), "the player's own");
            var plan = fixture.Plan();
            Refused(() => PlayerInstall.Install(fixture.Profile, plan, Connection("127.0.0.1", new string('A', 64)), "Alpha", fixture.Tool),
                "already contains other files. Choose an empty or new folder.");
            TestAssert.True(Directory.Exists(fixture.Air) && File.Exists(fixture.GameExecutable), "a refused install moves nothing");
            File.Delete(Path.Combine(fixture.Install, "their.txt"));

            PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", new string('A', 64)), "Alpha", fixture.Tool);
            var install = fixture.Install;
            TestAssert.True(new DirectoryInfo(install).GetAccessControl().AreAccessRulesProtected, "the install folder is locked");
            TestAssert.Equal(null, InstallLockChecks.FirstOpen(install), "every file of the install, the clients moved in too");
            var record = PlayerInstall.LockRecord(install);
            TestAssert.False(File.Exists(record), "the installer records no full pass: the start makes one");

            TestAssert.Equal(null, InstallLock.Check(install, record, fixture.Profile.LocalAppData, root), "the start: locked");
            TestAssert.True(File.Exists(record), "the start's full pass is recorded");

            // The tool's update, from a folder with the drive's permissions: switched in, private, and the record went.
            var next = Directory.CreateDirectory(Path.Combine(player, "next-tool")).FullName;
            WriteTool(next, "two");
            PlayerInstall.InstallTool(next, install);
            TestAssert.Equal("two", Version(Path.Combine(install, PlayerInstall.ToolFolder)));
            TestAssert.True(InstallLockChecks.FirstOpen(install) is null && !File.Exists(record), "the update keeps the install locked and drops the record");
            TestAssert.Equal(null, InstallLock.Check(install, record, fixture.Profile.LocalAppData, root), "the next start: locked");
            TestAssert.True(File.Exists(record), "the next start walked the whole install again");
        }
        finally { Cleanup(root); }
    }

    /// <summary>Batch 857 review (MEDIUM 7): a staged player tool is never reused on its .sha256 mark: it is downloaded and checked again.</summary>
    [Check("server.air-player-staged-tool-checked", TimeoutSeconds = 300)]
    public static void AStagedToolIsDownloadedAgainNotReusedOnItsMark() => StagedToolAsync().GetAwaiter().GetResult();

    private static async Task StagedToolAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-staged-tool");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, port), "Alpha", fixture.Tool);
            var served = ServedTool(root, "two");
            await using var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate);
            var staged = await PlayerInstall.StageUpdateAsync(fixture.Install, result.Server, CancellationToken.None);
            TestAssert.Equal("two", Version(staged!), "the newer tool is staged");
            // Changed after staging while its mark stays: the next check does not take it as it is.
            File.WriteAllText(Path.Combine(staged!, "version.txt"), "changed");
            var again = await PlayerInstall.StageUpdateAsync(fixture.Install, result.Server, CancellationToken.None);
            TestAssert.Equal("two", Version(again!), "the staged tool was downloaded and checked again, not reused on its mark");
        }
        finally { Cleanup(root); }
    }
}
