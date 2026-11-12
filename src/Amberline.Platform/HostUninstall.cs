using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Amberline.Platform.Folders;

namespace Amberline.Platform;

/// <summary>
/// Where Uninstall Amberline looks outside the install folder: the desktop (the icon), the user's local application data (the
/// pointer) and the temp folder (the cleanup). <see cref="Nowhere"/>, every loop's default, removes nothing outside the install
/// folder and writes no cleanup, so a check never touches this PC's desktop, profile or temp folder.
/// </summary>
public sealed record UninstallPlaces(string? Desktop, string? LocalAppData, string? Temp)
{
    public static UninstallPlaces Nowhere { get; } = new(null, null, null);

    public static UninstallPlaces ThisPc() => new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath());
}

/// <summary>Whose choice deletes a part of the install folder: always, "Also delete the game and AIR client copies", or "Also delete your logins and settings".</summary>
public enum UninstallKind { Program, Clients, Data }

/// <summary>One part of the install folder: its full path, what it is in plain words, whose choice deletes it, and its size in bytes.</summary>
public sealed record UninstallPart(string Path, string What, UninstallKind Kind, long Bytes);

/// <summary>
/// What uninstalling would touch in <see cref="Folder"/> (<see cref="Bytes"/> in all): Amberline's own <see cref="Parts"/>, the
/// <see cref="Others"/> it never deletes (anything else someone put there) and <see cref="Outside"/>, a sentence for each folder
/// Amberline uses outside it, which it never deletes either.
/// </summary>
public sealed record UninstallPlan(string Folder, bool OneFolder, long Bytes, IReadOnlyList<UninstallPart> Parts, IReadOnlyList<string> Others,
    IReadOnlyList<string> Outside)
{
    public bool Has(UninstallKind kind) => Parts.Any(part => part.Kind == kind);
    public long Size(UninstallKind kind) => Parts.Where(part => part.Kind == kind).Sum(part => part.Bytes);
    public static bool Deletes(UninstallPart part, bool clients, bool data) =>
        part.Kind switch { UninstallKind.Clients => clients, UninstallKind.Data => data, _ => true };
}

/// <summary>What one uninstall did, in plain words, and the cleanup that deletes the folder once Amberline has ended (null: none was written).</summary>
public sealed record UninstallResult(IReadOnlyList<string> Done, IReadOnlyList<string> Kept, IReadOnlyList<string> ByHand, ProcessStartInfo? Cleanup);

/// <summary>
/// NOT RETAIL: Uninstall Amberline (batch 641; retail had no server program on players' PCs), from Settings or from
/// <see cref="CommandName"/> in the install folder. It removes the desktop icon (when it starts this install), the pointer (when it
/// names this install), Amberline's two Windows Firewall rules (when they exist, in one elevated command, so Windows asks once) and the
/// router's ports (the Platform closes the ones it opened when it stops), then the install folder's own parts. The game and AIR client
/// copies and the data folder go only with the host's tick. Nothing outside the install folder is ever deleted, and nothing inside it
/// that is not Amberline's. A running program cannot delete its own folder, so a small cmd in the temp folder does that once
/// Amberline has ended.
/// </summary>
public static class HostUninstall
{
    public const string CommandName = "Uninstall Amberline.cmd";
    public const string ConfirmWord = "uninstall";
    private const int Tries = 120;

    private const string FirewallByHand = $"If you let Amberline through Windows Firewall, delete its two rules, \"{HostFirewall.SignInRule}\" and " +
        $"\"{HostFirewall.GamesRule}\", in Windows Defender Firewall, Advanced settings, Inbound Rules.";

    /// <summary>
    /// The install folder's uninstaller: the installed Platform with <c>--uninstall</c>, which asks in this window. One line with
    /// <c>exit /b</c> after it, so the window never reads this file again once the cleanup has deleted it.
    /// </summary>
    public static string Command => $"""
        @echo off
        rem Uninstall Amberline: removes Amberline from this PC. It asks in this window before it deletes anything.
        "%~dp0app\platform\{HostInstall.PlatformFile}" --uninstall & exit /b

        """.ReplaceLineEndings("\r\n");

    // The install folder's own parts (HostInstall's layout), in the order the pages name them.
    private static readonly (string Name, string What, UninstallKind Kind)[] Known =
    [
        ("app", "the Amberline program", UninstallKind.Program),
        ("app.previous", "the Amberline version before the last update", UninstallKind.Program),
        ("app.new", "an unfinished update", UninstallKind.Program),
        ("game", "your copy of the game", UninstallKind.Clients),
        ("air", "your copy of the AIR client", UninstallKind.Clients),
        ("data", "your logins, your settings, the server's certificate and the logs", UninstallKind.Data),
        (CommandName, "Amberline's uninstaller", UninstallKind.Program),
        (HostInstall.ReadMeName, "Amberline's READ ME", UninstallKind.Program),
        (FolderMove.InstallMarker, "Amberline's install marker", UninstallKind.Program),
    ];

    /// <summary>
    /// The plan for the Amberline running from <paramref name="paths"/>: a one-folder install (batch 637), or an install from before it,
    /// in the Amberline folder of <paramref name="localAppData"/>. Null for anything else (a development build, an unpacked zip): it is
    /// not installed, so there is nothing to uninstall.
    /// </summary>
    public static UninstallPlan? Plan(HostPaths paths, PlatformSettings settings, string? localAppData)
    {
        var folder = FolderMove.Full(Path.Combine(paths.App, "..", ".."));
        var oneFolder = HostPaths.InstallFolder(paths.App) is not null;
        if (!oneFolder && (localAppData is null || !FolderMove.Same(folder, Path.Combine(localAppData, "Amberline")))) return null;
        var parts = Known.Select(known => (Known: known, Path: Path.Combine(folder, known.Name)))
            .Where(item => Directory.Exists(item.Path) || File.Exists(item.Path))
            .Select(item => new UninstallPart(item.Path, item.Known.What, item.Known.Kind, Bytes(item.Path))).ToList();
        var others = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>()
            .Where(name => !Known.Any(known => known.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var outside = new List<string>();
        foreach (var (chosen, label) in new[] { (settings.DataRoot, "game folder"), (settings.AirRoot, "AIR client folder") })
            if (!string.IsNullOrWhiteSpace(chosen) && Path.IsPathFullyQualified(chosen) && !FolderMove.Inside(chosen, folder))
                outside.Add($"The {label} you chose stays where it is: {FolderMove.Full(chosen)}");
        if (!FolderMove.Inside(paths.Data, folder)) outside.Add($"Your logins and settings stay where they are: {paths.Data}");
        return new(folder, oneFolder, parts.Sum(part => part.Bytes) + others.Sum(name => Bytes(Path.Combine(folder, name))), parts, others, outside);
    }

    /// <summary>The page's and the window's answer when <see cref="Plan"/> finds no install.</summary>
    public static string NotInstalled(HostPaths paths) =>
        $"This Amberline is not installed: it runs from {FolderMove.Full(Path.Combine(paths.App, ".."))}. There is nothing to uninstall. " +
        "When you no longer need it, close its window and delete that folder.";

    public static bool Confirmed(string? text) => string.Equals(text?.Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase);

    /// <summary>A size as people read it: GB with one decimal, else MB or KB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e3:0} KB"),
        _ => "less than 1 KB",
    };

    /// <summary>
    /// Uninstalls: the firewall rules, the icon and the pointer at once, the install folder's parts through the cleanup (written to the
    /// temp folder of <paramref name="places"/>; none without one). When a part of Amberline's is kept, the marker stays, so a later
    /// install into the same folder finds the kept data again, and the READ ME says what was kept and where.
    /// </summary>
    public static async Task<UninstallResult> RunAsync(UninstallPlan plan, bool clients, bool data, UninstallPlaces places, HostNetwork? network,
        CancellationToken cancel = default)
    {
        var done = new List<string>();
        var kept = new List<string>();
        var byHand = new List<string>();
        await RemoveFirewallAsync(network, done, byHand, cancel);
        RemoveIcon(plan.Folder, places.Desktop, done, kept, byHand);
        RemovePointer(plan.Folder, places.LocalAppData, done, byHand);

        var keptParts = plan.Parts.Where(part => !UninstallPlan.Deletes(part, clients, data)).ToList();
        kept.AddRange(keptParts.Select(part => $"{char.ToUpperInvariant(part.What[0])}{part.What[1..]}: {part.Path}"));
        if (plan.Others.Count > 0) kept.Add($"Other things in {plan.Folder}, which are not Amberline's: {string.Join(", ", plan.Others)}");
        kept.AddRange(plan.Outside);
        var readMe = Path.Combine(plan.Folder, HostInstall.ReadMeName);
        var marker = Path.Combine(plan.Folder, FolderMove.InstallMarker);
        var targets = plan.Parts.Where(part => UninstallPlan.Deletes(part, clients, data) &&
            !(keptParts.Count > 0 && (FolderMove.Same(part.Path, readMe) || FolderMove.Same(part.Path, marker)))).ToList();
        if (keptParts.Count > 0) File.WriteAllText(readMe, KeptNote(plan.Folder, keptParts, plan.OneFolder));
        var folderGoes = keptParts.Count == 0 && plan.Others.Count == 0;

        // The folders by what they are; Amberline's three small files go with them unnamed.
        var named = string.Join(", ", targets.Where(part => Directory.Exists(part.Path)).Select(part => part.What));
        if (places.Temp is null)
        {
            byHand.Add($"Once Amberline has closed, delete {(folderGoes ? $"its folder {plan.Folder}" : $"from {plan.Folder}: {named}")}.");
            return new(done, kept, byHand, null);
        }
        var cleanup = WriteCleanup(places.Temp, targets.Select(part => part.Path).ToList(), folderGoes ? plan.Folder : null);
        done.Add(folderGoes ? $"Amberline's folder {plan.Folder} is deleted, with everything in it, as soon as Amberline has closed."
            : $"As soon as Amberline has closed, these are deleted from {plan.Folder}: {named}.");
        return new(done, kept, byHand, cleanup);
    }

    /// <summary>The READ ME of a folder that kept parts: what stayed, and what to do with it.</summary>
    public static string KeptNote(string folder, IEnumerable<UninstallPart> kept, bool oneFolder) => $"""
        AMBERLINE (UNINSTALLED)
        =======================

        Amberline was uninstalled from this PC. You chose to keep these, so they stay in this folder:
        {string.Join("\n", kept.Select(part => $"- {Path.GetFileName(part.Path),-15}{part.What}"))}

        Delete this folder when you no longer need them: {folder}
        {(oneFolder ? "To use them again, install Amberline into this same folder." : "")}

        """.ReplaceLineEndings("\r\n");

    /// <summary>
    /// The cleanup: a cmd that deletes each of <paramref name="targets"/> (a folder with everything in it, or a file), then
    /// <paramref name="folder"/> when it is empty (null: the folder stays), then itself. What a program still holds is tried again every
    /// second for two minutes, which also waits for Amberline's window to close. Every path is quoted and a % doubled (cmd expands it
    /// even inside quotes); the file is UTF-8 and switches the window to that code page first, so folder names in any language survive.
    /// </summary>
    public static string Script(IReadOnlyList<string> targets, string? folder)
    {
        static string Quoted(string path) => "\"" + path.Replace("%", "%%", StringComparison.Ordinal) + "\"";
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            "rem Amberline's cleanup after Uninstall Amberline: once Amberline has closed, it deletes what the uninstall named, then itself.",
            "set tries=0",
            ":again",
        };
        lines.AddRange(targets.Select(path => Directory.Exists(path) ? $"rd /s /q {Quoted(path)} 2>nul" : $"del /f /q /a {Quoted(path)} 2>nul"));
        if (folder is not null) lines.Add($"rd {Quoted(folder)} 2>nul");
        lines.AddRange(targets.Append(folder).OfType<string>().Select(path => $"if exist {Quoted(path)} goto wait"));
        lines.AddRange(["goto end", ":wait", "set /a tries+=1", $"if %tries% geq {Tries} goto end",
            "\"%SystemRoot%\\System32\\PING.EXE\" -n 2 127.0.0.1 >nul", "goto again", ":end", "(goto) 2>nul & del \"%~f0\""]);
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Starts a cleanup on its own, without a window; it outlives this process. Null starts nothing.</summary>
    public static void Start(ProcessStartInfo? cleanup)
    {
        if (cleanup is null) return;
        try { Process.Start(cleanup)?.Dispose(); }
        catch (Win32Exception ex) { Console.Error.WriteLine($"Uninstall, cleanup: {ex.Message}"); }
    }

    /// <summary>
    /// Uninstall Amberline.cmd's questions in the window: refused while this Amberline runs (its page has the uninstall then), the
    /// folder and its size, the two choices and the typed confirmation. The exit code, and the cleanup the caller starts last.
    /// </summary>
    public static async Task<(int Exit, ProcessStartInfo? Cleanup)> ConsoleAsync(HostPaths paths, UninstallPlaces places, HostNetwork? network,
        TextWriter screen, Func<string?> read)
    {
        screen.WriteLine("Uninstall Amberline");
        if (Plan(paths, HostMode.ReadOrDefault(paths.Settings), places.LocalAppData) is not { } plan)
        {
            screen.WriteLine(NotInstalled(paths));
            return (2, null);
        }
        if (await HostMode.ProbeAsync(HostMode.ReadPort(paths.Settings)) == true)
        {
            screen.WriteLine("Amberline is running. Close its window first, or uninstall it from its page (Settings).");
            return (1, null);
        }
        screen.WriteLine($"Amberline's folder: {plan.Folder} ({Size(plan.Bytes)}).");
        screen.WriteLine("Uninstalling removes the program, the desktop icon, the install pointer and Amberline's firewall rules (if Windows asks, choose Yes).");
        foreach (var line in plan.Outside) screen.WriteLine(line + ".");
        var clients = plan.Has(UninstallKind.Clients) &&
            Ask(screen, read, $"Also delete the game and AIR client copies in it ({Size(plan.Size(UninstallKind.Clients))})?");
        var data = plan.Has(UninstallKind.Data) && Ask(screen, read, $"Also delete your logins and settings ({Size(plan.Size(UninstallKind.Data))})? " +
            "Your users' logins and Play files then stop working.");
        screen.WriteLine($"Type {ConfirmWord} and press Enter to uninstall Amberline now. To keep it, close this window.");
        if (!Confirmed(read()))
        {
            screen.WriteLine("Nothing was uninstalled.");
            return (1, null);
        }
        screen.WriteLine("Uninstalling. This takes a few seconds.");
        var result = await RunAsync(plan, clients, data, places, network);
        screen.WriteLine("Amberline is uninstalled.");
        foreach (var line in result.Done) screen.WriteLine("- " + line);
        foreach (var line in result.Kept) screen.WriteLine("Kept: " + line);
        foreach (var line in result.ByHand) screen.WriteLine("Still to do: " + line);
        return (0, result.Cleanup);
    }

    /// <summary>A yes or no in the word list's words (batch 653): anything but Y or yes keeps them, as Enter alone did before.</summary>
    public const string YesNoLine = "Type Y for yes or N for no.";

    private static bool Ask(TextWriter screen, Func<string?> read, string question)
    {
        screen.WriteLine(question);
        screen.WriteLine(YesNoLine);
        return read()?.Trim() is { } answer && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RemoveFirewallAsync(HostNetwork? network, List<string> done, List<string> byHand, CancellationToken cancel)
    {
        if (network is null)
        {
            byHand.Add(FirewallByHand);
            return;
        }
        var shown = await ReadRulesAsync(network, cancel);
        if (shown is null) byHand.Add("Amberline could not read the Windows Firewall rules. " + FirewallByHand);
        else if (HostFirewall.RemoveCommand(shown, Environment.SystemDirectory) is not { } command) done.Add("Windows Firewall has no rule of Amberline's to remove.");
        else if (await network.RunElevated(command, cancel) is null) byHand.Add("Windows did not allow removing Amberline's firewall rules. " + FirewallByHand);
        else if (await ReadRulesAsync(network, cancel) is not { } after) byHand.Add("Amberline could not read the Windows Firewall rules afterwards. " + FirewallByHand);
        else if (HostFirewall.RemoveCommand(after, Environment.SystemDirectory) is not null) byHand.Add("Windows Firewall still has Amberline's rules. " + FirewallByHand);
        else done.Add("Amberline's rules in Windows Firewall are removed.");
    }

    private static async Task<IReadOnlyList<ShownRule>?> ReadRulesAsync(HostNetwork network, CancellationToken cancel)
    {
        try { return await network.ReadFirewall(cancel); }
        catch (InvalidOperationException) { return null; } // netsh did not start: the rules are unreadable
    }

    /// <summary>The desktop's Amberline icon, when it starts a program in this install (an icon of another Amberline stays).</summary>
    private static void RemoveIcon(string folder, string? desktop, List<string> done, List<string> kept, List<string> byHand)
    {
        if (desktop is null) return;
        var icon = Path.Combine(desktop, HostInstall.ShortcutName);
        if (!File.Exists(icon)) return;
        if (ShortcutTarget(icon) is not { Length: > 0 } target)
        {
            byHand.Add($"Delete the Amberline icon on your desktop yourself: Amberline could not read where it points ({icon}).");
            return;
        }
        if (!Path.IsPathFullyQualified(target) || !FolderMove.Inside(target, folder))
        {
            kept.Add($"The Amberline icon on your desktop starts another Amberline ({target}), so it stays.");
            return;
        }
        try
        {
            File.Delete(icon);
            done.Add("The Amberline icon on your desktop is removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { byHand.Add($"Delete the Amberline icon on your desktop yourself ({ex.Message})."); }
    }

    /// <summary>The pointer, when it names this install; its folder too when nothing else is in it.</summary>
    private static void RemovePointer(string folder, string? localAppData, List<string> done, List<string> byHand)
    {
        if (localAppData is null || FolderMove.ReadPointer(localAppData) is not { } named || !FolderMove.Same(named, folder)) return;
        var pointer = FolderMove.PointerPath(localAppData);
        try
        {
            File.Delete(pointer);
            var parent = Path.GetDirectoryName(pointer)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            done.Add($"The small file that told a later Amberline where its folder is ({pointer}) is removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { byHand.Add($"Delete the small file {pointer} yourself ({ex.Message})."); }
    }

    /// <summary>Where a shortcut points (WScript.Shell through COM, as <see cref="HostInstall"/> makes it); null when it cannot be read.</summary>
    private static string? ShortcutTarget(string link)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            try
            {
                var shortcut = shell.CreateShortcut(link);
                try { return (string)shortcut.TargetPath; }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Console.Error.WriteLine($"Uninstall, desktop icon: {ex.Message}");
            return null;
        }
    }

    private static ProcessStartInfo WriteCleanup(string temp, IReadOnlyList<string> targets, string? folder)
    {
        Directory.CreateDirectory(temp);
        var script = Path.Combine(temp, $"amberline-uninstall-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, Script(targets, folder), new UTF8Encoding(false));
        // /s takes the outer quotes off, leaving the script's own quoted path; the system folder as the working folder holds no install open.
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{script}\"\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
    }

    private static long Bytes(string path) => File.Exists(path) ? new FileInfo(path).Length
        : new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(file => file.Length);
}
