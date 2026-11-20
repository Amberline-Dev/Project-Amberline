using System.Security.AccessControl;
using System.Security.Principal;
using Amberline.Platform;
using AirInstallLock = Amberline.AirCompanion.InstallLock;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 857 (design <c>2026-11-17-batch-857-lock-install-folders-design.md</c>): the install lock on throwaway folders only. The hazard is built
/// here (a parent whose children inherit "Authenticated Users: Modify", as a drive root's do), never read off a real drive. Another Windows
/// account is a stand-in SID (<see cref="Someone"/>): this process may not make a file owned by another account.
/// </summary>
public static class InstallLockChecks
{
    /// <summary>A SID no account on this PC has, standing in for "another Windows account".</summary>
    internal const string Someone = "S-1-5-21-1000000001-1000000002-1000000003-1001";

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    [Check("server.install-lock-helper", TimeoutSeconds = 120)]
    public static void TheLockReachesEveryChildAndRefusesWhatItMayNotChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-install-lock");
        try
        {
            var drive = Hazard(Path.Combine(root, "drive"));
            var profile = Path.Combine(root, "Local");
            var folder = Path.Combine(drive, "Amberline");
            var record = Path.Combine(folder, "data", "host", "install-lock.txt");
            var plain = Write(Path.Combine(folder, "app", "a.txt"));
            Write(Path.Combine(folder, "data", "b.txt"));
            var hidden = Write(Path.Combine(folder, "data", "hidden.txt"));
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
            var granted = Write(Path.Combine(folder, "game", "granted.txt"));
            Grant(new FileInfo(granted), FileSystemRights.Modify);
            var shut = Directory.CreateDirectory(Path.Combine(folder, "air", "shut"));
            var own = PrivateTo<DirectorySecurity>();
            own.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
            shut.SetAccessControl(own);
            Write(Path.Combine(shut.FullName, "c.txt"));
            TestAssert.False(CopyPlaces.Private(folder) || CopyPlaces.Private(plain), "the hazard: the drive's Authenticated Users may change the folder");

            // A link inside: reported, never written through and never walked; the rest is still locked, and no record says it is.
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            Write(Path.Combine(outside, "o.txt"));
            var outsideBefore = Sddl(outside);
            var link = Path.Combine(folder, "data", "link");
            HostLoginCopyChecks.Junction(link, outside);
            TestAssert.Equal($"{link} is a link to another place", AirInstallLock.Lock(folder, record, profile, top: root), "a junction inside");
            TestAssert.Equal(outsideBefore, Sddl(outside), "the junction's target is untouched");
            TestAssert.False(File.Exists(record), "not locked: no record");
            Directory.Delete(link);

            // Locked: the folder protected with the three grants, and every child (hidden, with a grant of its own, protected) takes only those.
            TestAssert.Equal(null, AirInstallLock.Lock(folder, record, profile, top: root), "locked");
            TestAssert.Equal(null, FirstOpen(folder), "every child passes");
            TestAssert.True(File.Exists(record), "the full pass is recorded");
            var top = new DirectoryInfo(folder).GetAccessControl();
            TestAssert.True(top.AreAccessRulesProtected && top.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 3,
                "the folder: protected, three grants: " + top.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", Everything))
            {
                FileSystemSecurity security = Directory.Exists(entry) ? new DirectoryInfo(entry).GetAccessControl() : new FileInfo(entry).GetAccessControl();
                TestAssert.True(!security.AreAccessRulesProtected && security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count == 0,
                    $"{entry} takes only what it inherits: {security.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}");
            }
            var locked = Sddl(folder);
            TestAssert.Equal(null, AirInstallLock.Lock(folder, record, profile, top: root), "again");
            TestAssert.Equal(locked, Sddl(folder), "idempotent");

            // The start's check: the folder alone while the record stands; without it, the whole folder again. A record others may write is none.
            Grant(new FileInfo(plain), FileSystemRights.Write);
            TestAssert.Equal(null, AirInstallLock.Check(folder, record, profile, root), "the record stands: the folder alone");
            TestAssert.False(CopyPlaces.Private(plain), "the folder-only check walked nothing");
            File.Delete(record);
            TestAssert.Equal(null, AirInstallLock.Check(folder, record, profile, root), "no record: the full pass");
            TestAssert.True(CopyPlaces.Private(plain) && File.Exists(record), "the full pass re-stamped the child and recorded it");
            Grant(new FileInfo(record), FileSystemRights.Write);
            Grant(new FileInfo(plain), FileSystemRights.Write);
            TestAssert.Equal(null, AirInstallLock.Check(folder, record, profile, root), "a record others may write: the full pass");
            TestAssert.True(CopyPlaces.Private(plain) && CopyPlaces.Private(record), "the planted record counted for nothing");

            // A move: a tree keeps the drive's permissions when moved in, so it fails until it is re-stamped; the re-stamp drops the record.
            var download = Path.Combine(drive, "download");
            Write(Path.Combine(download, "x", "y.bin"));
            var moved = Path.Combine(folder, "game", "moved");
            Directory.Move(download, moved);
            TestAssert.True(FirstOpen(moved) is not null, "without the re-stamp a tree moved in keeps the drive's permissions");
            TestAssert.Equal(null, AirInstallLock.Restamp(moved, record), "re-stamped");
            TestAssert.True(FirstOpen(folder) is null && !File.Exists(record), "the moved tree is private, and the next start walks it all");

            // Refused, and left as it was: a link, another account's folder (also when it is a tree moved in), a volume without permissions.
            var junction = Path.Combine(drive, "Junction");
            HostLoginCopyChecks.Junction(junction, outside);
            TestAssert.Equal($"{junction} is a link to another place", AirInstallLock.Lock(junction), "a junction");
            TestAssert.Equal($"{junction} is a link to another place", AirInstallLock.Refusal(junction), "a junction: refused");
            TestAssert.Equal(outsideBefore, Sddl(outside), "the junction's target is untouched");
            Directory.Delete(junction);
            var other = Path.Combine(drive, "Other");
            Write(Path.Combine(other, "f.txt"));
            var otherBefore = Sddl(other);
            TestAssert.Equal($"{other} belongs to another Windows account", AirInstallLock.Lock(other, account: Someone), "another account's folder");
            TestAssert.Equal($"{other} belongs to another Windows account", AirInstallLock.Refusal(other, Someone), "another account's folder: refused");
            TestAssert.Equal(null, AirInstallLock.Refusal(other), "this account's own folder is not refused");
            TestAssert.Equal($"{other} belongs to another Windows account", AirInstallLock.Restamp(other, null, Someone), "a child of another account: not locked");
            TestAssert.Equal(otherBefore, Sddl(other), "another account's folder is untouched");
            TestAssert.Equal($"The drive of {other} keeps no Windows permissions (FAT32 or exFAT)", AirInstallLock.Lock(other, keepsPermissions: _ => false),
                "a volume without permissions: reported");
            TestAssert.Equal(otherBefore, Sddl(other), "and untouched");

            // A folder in the user's own profile (private, as a real profile is) keeps its own permissions, but its children are walked (the
            // orchestrator's ruling, 17 November 2026): a grant of a child's own and a tree moved in with the drive's permissions are re-stamped.
            Directory.CreateDirectory(profile).SetAccessControl(PrivateTo<DirectorySecurity>());
            var mine = Path.Combine(profile, "Programs", "Amberline");
            var mineFile = Write(Path.Combine(mine, "m.txt"));
            Grant(new FileInfo(mineFile), FileSystemRights.Modify);
            var old = Path.Combine(drive, "old-download");
            Write(Path.Combine(old, "z.bin"));
            Directory.Move(old, Path.Combine(mine, "game"));
            var mineOwn = new DirectoryInfo(mine).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            TestAssert.True(FirstOpen(mine) is not null, "before: a child grant and a tree moved in with the drive's permissions");
            var mineRecord = Path.Combine(mine, "servers", "install-lock.txt");
            TestAssert.Equal(null, AirInstallLock.Lock(mine, mineRecord, profile, top: root), "the profile folder: locked");
            TestAssert.Equal(mineOwn, new DirectoryInfo(mine).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
                "the profile folder's own permissions are left as they are");
            TestAssert.True(!new DirectoryInfo(mine).GetAccessControl().AreAccessRulesProtected && FirstOpen(mine) is null && File.Exists(mineRecord),
                "every child private, and the full pass recorded");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// Batch 857 security review: the shared first step (made locked; a link, another account's folder, a folder of other files refused), the
    /// record only through its own path, the folders above the install, a re-stamp under a folder that is not private, and the downloaded
    /// files hashed again before they are put in place.
    /// </summary>
    [Check("server.install-lock-review", TimeoutSeconds = 120)]
    public static void TheReviewsRulesHold()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-install-lock-review");
        try
        {
            var drive = Hazard(Path.Combine(root, "drive"));
            static void Refused(Action action, string text, string what)
            {
                try { action(); }
                catch (Amberline.AirCompanion.InstallRefusedException refused)
                {
                    TestAssert.True(refused.Message.Contains(text, StringComparison.Ordinal), $"{what}: refused for another reason: {refused.Message}");
                    return;
                }
                throw new InvalidOperationException($"{what}: not refused (expected: {text})");
            }

            // MEDIUM 4: a new folder is made with the lock already set; a link and another account's folder are refused (HIGH 1: before any write).
            var fresh = Path.Combine(drive, "Fresh");
            AirInstallLock.Prepare(fresh, null, ["marker"]);
            TestAssert.True(new DirectoryInfo(fresh).GetAccessControl().AreAccessRulesProtected && FirstOpen(fresh) is null, "made locked");
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var junction = Path.Combine(drive, "Junction");
            HostLoginCopyChecks.Junction(junction, outside);
            Refused(() => AirInstallLock.Prepare(junction, null, ["marker"]), "is a link to another place", "a junction");
            Directory.Delete(junction);
            var other = Directory.CreateDirectory(Path.Combine(drive, "Other")).FullName;
            Refused(() => AirInstallLock.Prepare(other, null, ["marker"], Someone), "belongs to another Windows account", "another account's folder");

            // LOW 10 (the orchestrator's correction): a folder of other files is refused, and left as it is; one with Amberline's marker is used.
            var theirs = Path.Combine(drive, "Theirs");
            Write(Path.Combine(theirs, "their.txt"));
            var theirsBefore = Sddl(theirs);
            Refused(() => AirInstallLock.Prepare(theirs, null, ["marker"]), "already contains other files. Choose an empty or new folder.", "a folder of other files");
            TestAssert.Equal(theirsBefore, Sddl(theirs), "a refused folder is left as it is");
            Write(Path.Combine(theirs, "marker"));
            AirInstallLock.Prepare(theirs, null, ["marker"]);
            TestAssert.True(FirstOpen(theirs) is null, "a folder with Amberline's marker is locked");

            // MEDIUM 5: the record counts only through its own path: through a junction, the full pass runs (and names the link).
            var folder = Path.Combine(drive, "Amberline");
            var record = Path.Combine(folder, "data", "host", "install-lock.txt");
            Write(Path.Combine(folder, "app", "a.txt"));
            TestAssert.Equal(null, AirInstallLock.Lock(folder, record, top: root), "locked, recorded");
            var elsewhere = Directory.CreateDirectory(Path.Combine(drive, "Amberline-elsewhere")).FullName;
            File.Copy(record, Path.Combine(elsewhere, "install-lock.txt"));
            Directory.Delete(Path.GetDirectoryName(record)!, recursive: true);
            HostLoginCopyChecks.Junction(Path.GetDirectoryName(record)!, elsewhere);
            TestAssert.Equal($"{Path.GetDirectoryName(record)} is a link to another place", AirInstallLock.Check(folder, record, top: root),
                "a record reached through a junction is no record");
            Directory.Delete(Path.GetDirectoryName(record)!);

            // MEDIUM 6 (with the additions): the folders above: one that lets others delete what it holds, or a link, leaves the install not locked.
            var shared = Hazard(Path.Combine(root, "shared"));
            var inShared = Path.Combine(shared, "Amberline");
            Write(Path.Combine(inShared, "a.txt"));
            var open = new DirectoryInfo(shared).GetAccessControl();
            open.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            new DirectoryInfo(shared).SetAccessControl(open);
            TestAssert.Equal($"other Windows accounts can delete, rename or change {shared}, which contains this install", AirInstallLock.Lock(inShared, top: root),
                "a parent that lets others delete its folders");
            var realParent = Directory.CreateDirectory(Path.Combine(drive, "real-parent")).FullName;
            var linkedParent = Path.Combine(drive, "linked-parent");
            HostLoginCopyChecks.Junction(linkedParent, realParent);
            Refused(() => AirInstallLock.Prepare(Path.Combine(linkedParent, "Amberline"), null, ["marker"]), $"{linkedParent} is a link to another place",
                "a folder above that is a junction");
            Directory.Delete(linkedParent);
            // This PC's real folders above a default install and a profile install pass (read only: nothing is made or changed there).
            TestAssert.Equal(null, AirInstallLock.Above(@"C:\Amberline 857 check"), @"C:\ as the folder above an install");
            TestAssert.Equal(null, AirInstallLock.Above(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amberline 857 check")),
                "this user's local application data and the folders above it");

            // LOW 8: a tree moved under a folder that is not private is not locked by a re-stamp.
            var unlocked = Path.Combine(drive, "Unlocked");
            Write(Path.Combine(unlocked, "tree", "t.txt"));
            TestAssert.Equal($"Amberline could not lock {Path.Combine(unlocked, "tree")}", AirInstallLock.Restamp(Path.Combine(unlocked, "tree"), null),
                "a re-stamp under a folder others may change");

            // Review D: a client zip staging folder that is a link is never deleted. A tree's removal never follows a junction (its read-only files
            // elsewhere keep their attributes).
            var kept = Directory.CreateDirectory(Path.Combine(root, "kept")).FullName;
            var keptFile = Write(Path.Combine(kept, "keep.txt"));
            File.SetAttributes(keptFile, FileAttributes.ReadOnly);
            var stagingLink = Path.Combine(drive, "Amberline.client-zip");
            HostLoginCopyChecks.Junction(stagingLink, kept);
            TestAssert.False(Amberline.AirCompanion.ClientZip.Remove(stagingLink), "a staging folder that is a link stays");
            TestAssert.True(Directory.Exists(stagingLink) && File.Exists(keptFile), "and nothing behind it is deleted");
            Directory.Delete(stagingLink);
            var tree = Path.Combine(drive, "Tree");
            Write(Path.Combine(tree, "t.txt"));
            HostLoginCopyChecks.Junction(Path.Combine(tree, "link"), kept);
            Amberline.AirCompanion.FolderMove.TryDeleteTree(tree);
            TestAssert.True(File.Exists(keptFile) && File.GetAttributes(keptFile).HasFlag(FileAttributes.ReadOnly), "what its junction led to is untouched");
            if (Directory.Exists(Path.Combine(tree, "link"))) Directory.Delete(Path.Combine(tree, "link"));
            // Re-review 2 (LOW 3): a junction given as the tree itself is refused; it and what it leads to stay as they are.
            var treeLink = Path.Combine(drive, "TreeLink");
            HostLoginCopyChecks.Junction(treeLink, kept);
            TestAssert.False(Amberline.AirCompanion.FolderMove.TryDeleteTree(treeLink), "a tree that is a link is refused");
            TestAssert.True(Directory.Exists(treeLink) && File.GetAttributes(keptFile).HasFlag(FileAttributes.ReadOnly), "the link and what it leads to are untouched");
            // Re-review 2 (LOW 4): links by their reparse tag: a junction is one; a folder, a file and a path with nothing there are not.
            TestAssert.True(AirInstallLock.IsLink(treeLink) && !AirInstallLock.IsLink(kept) && !AirInstallLock.IsLink(keptFile) &&
                !AirInstallLock.IsLink(Path.Combine(drive, "Missing")) && !AirInstallLock.IsLink(Path.Combine(drive, "Missing", "deeper")), "links by their tag");
            Directory.Delete(treeLink);
            File.SetAttributes(keptFile, FileAttributes.Normal);

            // HIGH 1: every downloaded file is hashed again before it is put in place.
            var install = Path.Combine(drive, "Download");
            var staging = Path.Combine(install, Amberline.AirCompanion.PlayerDownload.Staging);
            var staged = Write(Path.Combine(staging, "air", "LolClient.exe"));
            var entry = new Amberline.AirCompanion.PlayerDownload.Entry("air/LolClient.exe", new FileInfo(staged).Length,
                Amberline.AirCompanion.RetailClients.Sha256(staged));
            File.WriteAllText(staged, "swapped!!");
            var plan = new Amberline.AirCompanion.PlayerInstall.Plan(install, Path.Combine(install, "air"), Path.Combine(install, "game"), [], null, 0, []);
            Refused(() => Amberline.AirCompanion.PlayerDownload.Place(staging, plan, new([entry], entry.Size), new(), "Alpha", null, CancellationToken.None),
                "A downloaded file changed before it was put in place (air/LolClient.exe)", "a file changed after its download");
            TestAssert.False(Directory.Exists(Path.Combine(install, "air")), "nothing was put in place");
        }
        finally { HostModeChecks.Delete(root); }
    }

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };

    /// <summary>A folder whose children inherit "Authenticated Users: Modify", as a drive root's do (this user, Administrators and SYSTEM keep full control).</summary>
    internal static string Hazard(string folder)
    {
        var security = PrivateTo<DirectorySecurity>();
        // Only passed on (as on a drive root, which itself cannot be deleted): the folder itself stays this user's alone, so it may hold an install.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify, Inherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));
        Directory.CreateDirectory(folder).SetAccessControl(security);
        return folder;
    }

    /// <summary>The first of <paramref name="folder"/> and everything in it (hidden and system files too) that fails <see cref="CopyPlaces.Private(string)"/>; null when none.</summary>
    internal static string? FirstOpen(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", Everything).Prepend(folder).FirstOrDefault(path => !CopyPlaces.Private(path));

    /// <summary>The owner and DACL of <paramref name="folder"/> and of everything in it, one line each.</summary>
    internal static string Sddl(string folder) => string.Join("\n", Directory.EnumerateFileSystemEntries(folder, "*", Everything).Prepend(folder).Order(StringComparer.Ordinal)
        .Select(path => path + " " + (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl())
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)));

    private static T PrivateTo<T>() where T : FileSystemSecurity, new()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return HostLoginCopyChecks.PrivateTo<T>(identity.User!);
    }

    /// <summary>An explicit grant for Users on a file, as a program might leave one.</summary>
    private static void Grant(FileInfo file, FileSystemRights rights)
    {
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, rights, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic");
        return path;
    }
}
