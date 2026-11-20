using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

// Linked as source into the Platform as well (see RetailClients.cs): the host's install, its start and its client moves lock with it.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// NOT RETAIL (batch 857, design <c>2026-11-17-batch-857-lock-install-folders-design.md</c>, approved by the owner 17 November 2026; the platform's
/// own security: retail installed with Riot's patcher, and no server program ran on players' PCs): an Amberline install folder locked to the Windows
/// account that installed it. A folder made at a drive's root inherits the root's "Authenticated Users: Modify", so any other account on the PC could
/// change the programs and data in it. The lock is the one <c>host/tools/air-registration-start.ps1</c> sets on the registration's data: protected,
/// FullControl for this user, SYSTEM and Administrators, passed on to every subfolder and file. The owner stays this user; a folder's owner may change
/// its permissions, so no elevation is needed.
/// <list type="bullet">
/// <item>Locked means every child passes <see cref="Private(FileSystemSecurity)"/>, not only the folder: after the folder, each child is re-stamped to
/// take only what it inherits (its own entries removed, protection off), where it needs it. A link, a child owned by another account, or one that
/// could not be re-stamped leaves the folder not locked, and the reason names the first. A link is never written through and never walked into.</item>
/// <item>A full pass that completed writes the record; <see cref="Check"/> then looks only at the folder and at the record, which must be private
/// itself (anyone could have written one while the folder was open). A full pass and every move into the install remove the record first
/// (<see cref="Restamp"/>), so the next start walks the whole install again.</item>
/// <item>A folder owned by another account, and a link, are refused (<see cref="Refusal"/>); a volume without permissions (FAT32, exFAT) is
/// reported, never a failure; a folder under the user's own profile (<c>%LOCALAPPDATA%</c>, the caller's <c>profile</c>) keeps its own permissions,
/// which the profile makes private, but its children are walked and re-stamped all the same.</item>
/// </list>
/// Nothing here throws: each answer is null (locked, or nothing to do) or one sentence without a final period.
/// </summary>
internal static class InstallLock
{
    /// <summary>The Status line of a locked install.</summary>
    internal const string LockedLine = "Only your Windows account can change this install";

    private const string RecordText = "Amberline locked this install to your Windows account. It checks the whole folder again when this file is missing.\r\n";

    /// <summary>
    /// The full pass: refuses a link or another account's folder, locks <paramref name="folder"/>, then re-stamps and checks every child. Once all
    /// passed, writes <paramref name="record"/> (when given). DIAGNOSTICS: <paramref name="account"/> (a SID) stands in for this Windows user and
    /// <paramref name="keepsPermissions"/> for the volume test, so a check reaches the refusals without another account or a FAT volume.
    /// </summary>
    internal static string? Lock(string folder, string? record = null, string? profile = null, string? account = null, Func<string, bool>? keepsPermissions = null,
        string? top = null) =>
        Locking(folder, record, profile, account, keepsPermissions, top, null).Problem;

    /// <summary>
    /// Batch 857 review (MEDIUM 4): <see cref="Lock"/> with whether its problem refuses an install: a link, another account's folder or child, or one
    /// it could not lock (<c>Refuses</c>); a volume without permissions is only reported.
    /// </summary>
    private static (string? Problem, bool Refuses) Locking(string folder, string? record, string? profile, string? account, Func<string, bool>? keepsPermissions,
        string? top, Func<FileSystemInfo, bool>? links)
    {
        Forget(record);
        if (!OperatingSystem.IsWindows()) return (null, false);
        try
        {
            folder = FolderMove.Full(folder);
            if (!(keepsPermissions ?? KeepsPermissions)(folder)) return (NoPermissions(folder), false);
            if (Refusal(folder, account) is { } refused) return (refused, true);
            var trusted = Trusted(account);
            var root = new DirectoryInfo(folder);
            // Orchestrator's ruling (17 November 2026): a folder in the user's own profile keeps the profile's private permissions, so its own are
            // left as they are; its children are still walked, so a tree moved in before batch 857 with another drive's permissions is fixed.
            if (profile is null || !FolderMove.Inside(folder, profile))
            {
                // ponytail: Refusal and this write both go by path, so a folder swapped for a junction between the two is written through (only to
                // make its target private to this user); a handle opened without following links closes that, through a P/Invoke write.
                root.SetAccessControl(Locked(trusted));
                // Batch 857 review (MEDIUM 4): a folder that became a link meanwhile is refused all the same.
                root.Refresh();
                if (Linked(root)) return (Link(folder), true);
            }
            var problem = Private(root.GetAccessControl(), trusted) ? Walk(root, trusted, links) : Unreached(folder);
            if (problem is not null) return (problem, true);
            Record(record);
            return Ancestors(folder, account, top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return (Unreached(folder), true); }
    }

    /// <summary>
    /// Batch 857 review (HIGH 1, MEDIUM 4): the one step every install route runs before its first write into <paramref name="folder"/>: a folder that
    /// is not there is made with the lock already set (in the user's own profile, as the profile makes it), then the folder itself is checked again
    /// (another account may have made it meanwhile) and locked (<see cref="Lock"/>). A link, another account's folder or one that could not be locked
    /// refuses the install (<see cref="InstallRefusedException"/>); anything else that is not locked (a volume without permissions) is returned to be
    /// reported, and the install goes on. Review LOW 10 (with the orchestrator's correction): only a folder Amberline made is used: a new or empty one,
    /// one that holds one of its <paramref name="markers"/>, or one already locked; any other folder that holds files refuses the install, and its
    /// permissions stay as they are. Batch 919: <paramref name="made"/> is the caller's own test of a folder Amberline made that holds no marker (a
    /// player install's download of an older tool); it is asked only of a folder that is not a link and is this user's own.
    /// </summary>
    internal static string? Prepare(string folder, string? profile, IReadOnlyList<string> markers, string? account = null, Func<string, bool>? made = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        folder = FolderMove.Full(folder);
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                if ((profile is not null && FolderMove.Inside(folder, profile)) || !KeepsPermissions(folder)) Directory.CreateDirectory(folder);
                else new DirectoryInfo(folder).Create(Locked(Trusted(account)));
            }
            else if (Refusal(folder, account) is { } refused) throw Refused(refused);
            // Orchestrator's ruling on review LOW 10 (17 November 2026): a folder that holds other files is refused, never filled unlocked.
            else if (!Ours(folder, markers) && made?.Invoke(folder) != true) throw new InstallRefusedException($"{folder} already contains other files. Choose an empty or new folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { throw Refused(Unreached(folder)); }
        var (problem, refuses) = Locking(folder, null, profile, account, null, null, null);
        return refuses ? throw Refused(problem!) : problem;
    }

    private static InstallRefusedException Refused(string problem) => new($"{problem}. Nothing was installed there. Choose another folder.");

    /// <summary>Review LOW 10: a folder Amberline made: empty, holding one of its <paramref name="markers"/>, or locked already (protected and private).</summary>
    [SupportedOSPlatform("windows")]
    private static bool Ours(string folder, IReadOnlyList<string> markers) =>
        !Directory.EnumerateFileSystemEntries(folder).Any() || markers.Any(marker => Path.Exists(Path.Combine(folder, marker))) ||
        new DirectoryInfo(folder).GetAccessControl() is { AreAccessRulesProtected: true } security && Private(security);

    /// <summary>The lock, a new object as the registration's recipe: protected, FullControl for the three, inherited by everything; only a DACL, never an owner.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity Locked(SecurityIdentifier[] trusted)
    {
        var locked = new DirectorySecurity();
        locked.SetAccessRuleProtection(true, false);
        foreach (var sid in trusted)
            locked.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return locked;
    }

    /// <summary>
    /// A start's check: only the folder (and the record) while <paramref name="record"/> says a full pass completed and both are private; else the
    /// full pass (<see cref="Lock"/>).
    /// </summary>
    internal static string? Check(string folder, string record, string? profile = null, string? top = null) => Checking(folder, record, profile, top, null).Problem;

    /// <summary>
    /// <see cref="Check"/> with whether its problem refuses (a link, another account's folder or child, one it could not lock). Batch 857 review:
    /// <paramref name="links"/> names the links that may stand in the folder (the 2011 launcher's RADS root: only the junctions its layout made); each is
    /// passed over, never written, while a trusted account owns it. Any other link is a problem.
    /// </summary>
    internal static (string? Problem, bool Refuses) Checking(string folder, string record, string? profile, string? top, Func<FileSystemInfo, bool>? links)
    {
        if (!OperatingSystem.IsWindows()) return (null, false);
        try
        {
            var root = new DirectoryInfo(FolderMove.Full(folder));
            if (root.Exists && !Linked(root) && Private(root.GetAccessControl()) && Holds(root.FullName, record)) return Ancestors(root.FullName, null, top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { }
        return Locking(folder, record, profile, null, null, top, links);
    }

    /// <summary>Whether <paramref name="folder"/> is owned by this Windows user itself (not only by a trusted group).</summary>
    internal static bool Mine(string folder)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
                owner == identity.User;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return false; }
    }

    /// <summary>
    /// Batch 857 review (MEDIUM 5): whether the record counts: it lies inside <paramref name="root"/>, no folder between the two is a link, and the
    /// handle it is opened through leads to it (<see cref="FinalPath"/>) and is private.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool Holds(string root, string record)
    {
        var full = FolderMove.Full(record);
        if (!FolderMove.Inside(full, root) || FolderMove.Same(full, root) || !File.Exists(full)) return false;
        for (var folder = Path.GetDirectoryName(full)!; !FolderMove.Same(folder, root); folder = Path.GetDirectoryName(folder)!)
            if (Linked(new DirectoryInfo(folder))) return false;
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(FinalPath(stream.SafeFileHandle), full, StringComparison.OrdinalIgnoreCase) && Private(stream.GetAccessControl());
    }

    /// <summary>
    /// Windows' final name of the file <paramref name="handle"/> is open on, without the <c>\\?\</c> prefix: a junction or symbolic link anywhere
    /// on the way shows as a different path. Null when Windows cannot say.
    /// </summary>
    internal static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length > buffer.Length) return null;
        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>The number of names the file <paramref name="handle"/> is open on has (more than one: a hard link); 0 when Windows cannot say.</summary>
    internal static uint Links(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var information) ? information.Links : 0;

    /// <summary>
    /// Batch 857 review: which file <paramref name="handle"/> is open on (its volume and file index) and when it was last written, as one text; null
    /// when Windows cannot say. A file swapped or rewritten since gives another.
    /// </summary>
    internal static string? Identity(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var information)
        ? $"{information.Volume:X8}-{information.IndexHigh:X8}{information.IndexLow:X8}-{information.WrittenHigh:X8}{information.WrittenLow:X8}" : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    /// <summary>BY_HANDLE_FILE_INFORMATION; each FILETIME as its two halves, so the layout has no padding.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>
    /// Batch 857 re-review 2 (LOW 4): whether <paramref name="path"/> is a link, by its reparse tag: a name surrogate (a junction, a symbolic link
    /// and the like) is one, and so is an entry whose tag cannot be read. Nothing there, a drive's root, an ordinary file or folder and other
    /// reparse points (OneDrive files on demand, deduplicated files) are not. Never follows the link.
    /// </summary>
    internal static bool IsLink(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase)) return false;
        var find = FindFirstFile(full.Length >= 260 && !full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + full : full, out var data);
        if (find == -1) return Marshal.GetLastPInvokeError() is not (2 or 3);   // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND: nothing there
        FindClose(find);
        const uint NameSurrogate = 0x20000000;
        return (data.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && (data.ReparseTag & NameSurrogate) != 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindFirstFileW", SetLastError = true)]
    private static extern nint FindFirstFile(string name, out FindData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint find);

    /// <summary>WIN32_FIND_DATAW: for a reparse point, <c>dwReserved0</c> is its tag.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, SizeHigh, SizeLow, ReparseTag, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
    }

    /// <summary>
    /// After a move into an install: <paramref name="tree"/> (a same-volume move keeps its old permissions) takes only what it inherits there, with
    /// every child; the install's record goes, so its next start makes a full pass. DIAGNOSTICS: <paramref name="account"/> as in <see cref="Lock"/>.
    /// </summary>
    internal static string? Restamp(string tree, string? record, string? account = null)
    {
        Forget(record);
        if (!OperatingSystem.IsWindows() || !Directory.Exists(tree) || !KeepsPermissions(tree)) return null;
        try
        {
            var root = new DirectoryInfo(FolderMove.Full(tree));
            var trusted = Trusted(account);
            // Batch 857 review (LOW 8 and its follow-up): read only first: the tree's own refusal (a link, another account's), then the folder it was
            // moved into must pass the check itself (what the tree inherits is only the lock then); only then is anything rewritten.
            if (Linked(root)) return Link(root.FullName);
            if (Read(root).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return Foreign(root.FullName);
            if (root.Parent is not { } parent || !Private(parent.GetAccessControl(), trusted)) return Unreached(tree);
            return Stamp(root, trusted) ?? Walk(root, trusted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return Unreached(tree); }
    }

    /// <summary>Why an existing <paramref name="folder"/> may not become (or hold) an install: a link, or another account's; null when it may or is not there.</summary>
    internal static string? Refusal(string folder, string? account = null)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder) || !KeepsPermissions(folder)) return null;
        try
        {
            var info = new DirectoryInfo(folder);
            if (Linked(info)) return Link(folder);
            return info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && Trusted(account).Contains(owner)
                ? null : Foreign(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            return $"Amberline cannot read who owns {folder}";
        }
    }

    /// <summary>
    /// Batch 830 review (as <c>CopyPlaces.Private</c>, which asks this): whether <paramref name="security"/> has this Windows user, the Administrators
    /// group or SYSTEM as its owner, and no allow entry lets anyone else write, delete or change its permissions (entries that only pass on to
    /// children are left out: each child is asked itself). Anything else, or not Windows, is not trusted.
    /// </summary>
    internal static bool Private(FileSystemSecurity security)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Private(security, Trusted(null)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool Private(FileSystemSecurity security, SecurityIdentifier[] trusted)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        // Write is WriteData, AppendData and the attribute writes; the two generic bits are GENERIC_WRITE and GENERIC_ALL.
        const int Changes = (int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x40000000 | 0x10000000;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                !(rule.IdentityReference is SecurityIdentifier who && trusted.Contains(who)) && ((int)rule.FileSystemRights & Changes) != 0)
                return false;
        return true;
    }

    /// <summary>
    /// Batch 857 review (MEDIUM 6, with the orchestrator's additions of 17 November 2026): the folders above the install, up to the volume root, must
    /// not let anyone else delete, rename or swap it. Trusted there: this user, Administrators, SYSTEM and TrustedInstaller (CREATOR OWNER entries
    /// only pass on). Each folder above must not be a link (a refusal), must have a trusted owner, and must give nobody else Delete subfolders and
    /// files, ChangePermissions, TakeOwnership or GENERIC_ALL, nor DELETE on itself (a volume root cannot be deleted); the folder right above the
    /// install not GENERIC_WRITE either. Entries that only pass on to children are left out. Anything else reports the install not locked.
    /// DIAGNOSTICS: <paramref name="top"/>, a check's own throwaway folder, ends the walk below it (the folders above a check's root are not its own).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string? Problem, bool Refuses) Ancestors(string folder, string? account, string? top)
    {
        // DIAGNOSTICS: with the runner's seam (no top of the caller's own), a check's throwaway folders under it count for their links only.
        var throwaway = top is null ? Top : null;
        top ??= Top;
        const int GenericWrite = 0x40000000, GenericAll = 0x10000000;
        SecurityIdentifier[] trusted = [.. Trusted(account), new(TrustedInstaller), new(WellKnownSidType.CreatorOwnerSid, null)];
        var first = true;
        for (var parent = Path.GetDirectoryName(FolderMove.Full(folder)); parent is not null && (top is null || !FolderMove.Inside(top, parent));
             parent = Path.GetDirectoryName(parent))
        {
            try
            {
                var info = new DirectoryInfo(parent);
                if (Linked(info)) return (Link(parent), true);
                if (throwaway is not null && FolderMove.Inside(parent, throwaway))
                {
                    first = false;
                    continue;
                }
                var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return (OpenAbove(parent), false);
                var volume = string.Equals(Path.GetPathRoot(parent), parent, StringComparison.OrdinalIgnoreCase);
                var risky = (int)(FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | GenericAll |
                    (volume ? 0 : (int)FileSystemRights.Delete) | (first ? GenericWrite : 0);
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                        !(rule.IdentityReference is SecurityIdentifier who && trusted.Contains(who)) && ((int)rule.FileSystemRights & risky) != 0)
                        return (OpenAbove(parent), false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
            {
                return ($"Amberline could not check {parent}", false);
            }
            first = false;
        }
        return (null, false);
    }

    /// <summary>NT SERVICE\TrustedInstaller, the owner of Windows' own folders (a trusted owner and grantee of the folders above an install).</summary>
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// DIAGNOSTICS (batch 857): the check runner's throwaway root (its temp folder), set once by the runner: a walk of the folders above an install
    /// counts only links inside it and stops at it (the folders there are a check's throwaway ones, those above it the PC's). Never set outside
    /// the checks; a caller's own top replaces it.
    /// </summary>
    internal static string? Top { get; set; }

    /// <summary>
    /// Batch 857 review: a read-only look at <paramref name="folder"/> and everything in it, hidden and system files too: the first that another
    /// Windows account may change (<see cref="Private(FileSystemSecurity)"/>), or a link that <paramref name="links"/> does not name (each named one
    /// is passed over, never walked into, while a trusted account owns it). Null when there is none. Nothing is changed.
    /// </summary>
    internal static string? Inspect(string folder, Func<FileSystemInfo, bool>? links)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var trusted = Trusted(null);
        [SupportedOSPlatform("windows")]
        string? Look(FileSystemInfo entry)
        {
            try
            {
                if (Linked(entry))
                    return links is not null && links(entry) && Read(entry).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && trusted.Contains(owner)
                        ? null : Link(entry.FullName);
                if (!Private(Read(entry), trusted)) return $"other Windows accounts can change {entry.FullName}";
                if (entry is DirectoryInfo directory)
                    foreach (var child in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                        if (Look(child) is { } problem) return problem;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
            {
                return $"Amberline could not check {entry.FullName}";
            }
        }
        return Look(new DirectoryInfo(FolderMove.Full(folder)));
    }

    private static string OpenAbove(string folder) => $"other Windows accounts can delete, rename or change {folder}, which contains this install";

    /// <summary>DIAGNOSTICS: <see cref="Ancestors"/> alone, read only, so a check can ask it about this PC's real folders without changing anything.</summary>
    internal static string? Above(string folder, string? top = null) => OperatingSystem.IsWindows() ? Ancestors(folder, null, top).Problem : null;

    /// <summary>This user (or the check's stand-in), Administrators and SYSTEM: the owners and the grants a locked install has.</summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] Trusted(string? account)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [account is null ? identity.User! : new SecurityIdentifier(account), new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null)];
    }

    /// <summary>
    /// Every child of <paramref name="folder"/>, hidden and system ones too, folder by folder; a link or a problem is not walked into. With
    /// <paramref name="links"/>, the links the caller names (the 2011 launcher's RADS root: only the junctions its layout made) are passed over, never
    /// written, while a trusted account owns them; any other link is a problem.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? Walk(DirectoryInfo folder, SecurityIdentifier[] trusted, Func<FileSystemInfo, bool>? links = null)
    {
        List<FileSystemInfo> entries;
        try { entries = [.. folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return Unreached(folder.FullName); }
        string? first = null;
        foreach (var entry in entries)
        {
            // A link the caller names (links) is passed over while a trusted account owns it; any other one is a problem (Stamp).
            if (links is not null && Linked(entry) && links(entry))
            {
                first ??= Read(entry).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && trusted.Contains(owner) ? null : Foreign(entry.FullName);
                continue;
            }
            var problem = Stamp(entry, trusted);
            if (problem is null && entry is DirectoryInfo directory) problem = Walk(directory, trusted, links);
            first ??= problem;
        }
        return first;
    }

    /// <summary>
    /// One child: a link is reported and never written (a write by path would follow it), another account's is reported, and one that is
    /// protected, has entries of its own or fails the check is re-stamped to take only what it inherits; then it must pass.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? Stamp(FileSystemInfo entry, SecurityIdentifier[] trusted)
    {
        try
        {
            if (Linked(entry)) return Link(entry.FullName);
            var security = Read(entry);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return Foreign(entry.FullName);
            var own = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
            if (security.AreAccessRulesProtected || own.Count > 0 || !Private(security, trusted))
            {
                // The child's own security, read, with only its DACL changed (its own entries removed, protection off); the owner is never written.
                foreach (FileSystemAccessRule rule in own) security.RemoveAccessRuleSpecific(rule);
                security.SetAccessRuleProtection(false, false);
                if (entry is DirectoryInfo directory) directory.SetAccessControl((DirectorySecurity)security);
                else ((FileInfo)entry).SetAccessControl((FileSecurity)security);
                security = Read(entry);
            }
            return Private(security, trusted) ? null : Unreached(entry.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return Unreached(entry.FullName); }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity Read(FileSystemInfo entry) =>
        entry is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();

    /// <summary>
    /// Batch 857 review (G) and re-review 2 (LOW 4): a link by its reparse tag (<see cref="IsLink"/>); other reparse points (OneDrive files on
    /// demand, deduplicated files) are not links. The tag is read only for a reparse point, so an ordinary file costs no call here.
    /// </summary>
    private static bool Linked(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsLink(entry.FullName);

    /// <summary>FAT32 and exFAT keep no permissions; a drive that cannot be asked is tried, and its own answer counts.</summary>
    private static bool KeepsPermissions(string folder)
    {
        try { return !new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).DriveFormat.Contains("FAT", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
    }

    private static void Record(string? record)
    {
        if (record is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, RecordText);
    }

    private static void Forget(string? record)
    {
        try { if (record is not null && File.Exists(record)) File.Delete(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Link(string path) => $"{path} is a link to another place";
    private static string Foreign(string path) => $"{path} belongs to another Windows account";
    private static string Unreached(string path) => $"Amberline could not lock {path}";
    private static string NoPermissions(string folder) => $"The drive of {folder} keeps no Windows permissions (FAT32 or exFAT)";
}
