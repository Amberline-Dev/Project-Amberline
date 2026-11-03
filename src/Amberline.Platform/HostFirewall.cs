using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Amberline.Platform;

/// <summary>One allow rule: its name, the program it names, its protocol ("TCP" or "UDP") and its local port or range.</summary>
public sealed record FirewallRule(string Name, string Program, string Protocol, string LocalPort);

/// <summary>One rule block of the read-back: its name and its fields by the label netsh prints.</summary>
public sealed record ShownRule(string Name, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The printed value of a field, or "" when netsh did not print it.</summary>
    public string Field(string label) => Fields.TryGetValue(label, out var value) ? value : "";
}

/// <summary>The elevated command: a program by its full path and its arguments. Nothing is looked up on PATH.</summary>
public sealed record ElevatedCommand(string FileName, string Arguments);

/// <summary>
/// What one press of the allow button did: the state after the run (null when the prompt was declined or nothing ran), whether the Windows
/// prompt was declined, or the card of a refusal (nothing elevated, because a program path has a character the command line cannot carry).
/// </summary>
public sealed record FirewallAttempt(FirewallState? State, bool Declined, FirewallProblem? Refusal);

/// <summary>
/// What the read-back says about the host's two rules: the worst of the two, in this order (a later value is worse).
/// </summary>
public enum FirewallState
{
    /// <summary>Both rules are enabled inbound allows for their program, protocol and ports, on Domain, Private and Public.</summary>
    Allowed,
    /// <summary>A rule allows its program but not every network type.</summary>
    SomeNetworks,
    /// <summary>A rule of the same name names another program file (a moved or updated folder).</summary>
    OtherFolder,
    /// <summary>No enabled allow rule of the name for its program, protocol and ports.</summary>
    Missing,
    /// <summary>An enabled block rule names one of the programs; a block beats any allow rule.</summary>
    Blocked,
    /// <summary>netsh could not be run, or printed no rule.</summary>
    Unreadable,
}

/// <summary>A problem card for the host page: the short code for an issue report, what happened and what to do.</summary>
public sealed record FirewallProblem(string Code, string Message, string WhatToDo);

/// <summary>
/// NOT RETAIL: retail had no firewall step (its servers ran on public addresses); a home host lets its two programs in through
/// Windows Firewall (Amberline Host design, sections 5.2 and 8, batch H4). Two inbound allow rules, each scoped to its program, its
/// protocol and its ports, on every network type. Only <see cref="RunElevatedAsync"/> elevates: it starts System32's cmd.exe with one
/// command line built from code, so Windows asks once and no file is written. Nothing here runs unless the host page asks for it.
/// </summary>
public static class HostFirewall
{
    public const string SignInRule = "Amberline sign-in";
    public const string GamesRule = "Amberline games";

    /// <summary>ERROR_CANCELLED: the user declined the Windows prompt.</summary>
    private const int ErrorCancelled = 1223;

    private static string NetshPath => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    /// <summary>
    /// The two rules for these program files: the sign-in rule on TCP at the public port, and the games rule on UDP at the game ports
    /// that the match servers take from <paramref name="firstGamePort"/> (<paramref name="games"/> of them). Ports must be 1 to 65535.
    /// </summary>
    public static IReadOnlyList<FirewallRule> Rules(string platformProgram, string serverProgram, int remotePort, int firstGamePort, int games)
    {
        if (remotePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort), "The sign-in port is 1 to 65535.");
        if (games < 1) throw new ArgumentOutOfRangeException(nameof(games), "A host runs at least one game.");
        if (firstGamePort < 1 || firstGamePort + (long)games - 1 > 65535)
            throw new ArgumentOutOfRangeException(nameof(firstGamePort), "The game ports must stay within 1 to 65535.");
        var gamePorts = games == 1 ? $"{firstGamePort}" : $"{firstGamePort}-{firstGamePort + games - 1}";
        return
        [
            new FirewallRule(SignInRule, FullPath(platformProgram), "TCP", $"{remotePort}"),
            new FirewallRule(GamesRule, FullPath(serverProgram), "UDP", gamePorts),
        ];
    }

    /// <summary>
    /// The refusal card when a program path has a character that cmd or netsh could read as something else, or null when every path is
    /// safe for the command line. Refused: any character outside plain ASCII, a control character, %, !, ^ and the double quote. A
    /// refusal elevates nothing; the card gives the manual steps.
    /// </summary>
    public static FirewallProblem? Refusal(IReadOnlyList<FirewallRule> rules)
    {
        if (!rules.Any(rule => Unsafe(rule.Program))) return null;
        return new FirewallProblem("A-NET-02",
            "Amberline cannot ask Windows Firewall to allow it from this folder: its path has a character Windows cannot pass safely.",
            "Move Amberline to a folder whose path has only plain letters, digits, spaces, dots and dashes, then try again. " +
            "Or allow its two programs by hand in Windows Defender Firewall (TCP 8391 for sign-in, UDP 5219-5222 for games, every network type).");
    }

    /// <summary>
    /// The one elevated command line: <c>cmd.exe /d /c</c> from the system folder (a full path, never a PATH lookup; <c>/d</c> skips AutoRun
    /// commands), with each netsh call by its full System32 path, chained with <c>&amp;&amp;</c>. Every value comes from code: names and
    /// protocols are fixed, ports are checked numbers, and program paths are checked the way <see cref="Refusal"/> checks them. A delete is
    /// written only for a rule that exists, because a delete that matches nothing would end the chain. Reconstruction: netsh's exit code for
    /// a missing rule is not verified; the rule is therefore never deleted when it is absent. No file is written.
    /// </summary>
    public static ElevatedCommand Command(IReadOnlyList<FirewallRule> rules, IReadOnlyCollection<string> existingNames, string systemDirectory)
    {
        var netsh = Netsh(systemDirectory);
        var steps = new List<string>();
        foreach (var rule in rules)
        {
            CheckRule(rule);
            if (existingNames.Any(name => Same(name, rule.Name)))
                steps.Add($"\"{netsh}\" advfirewall firewall delete rule name=\"{rule.Name}\"");
            steps.Add($"\"{netsh}\" advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow program=\"{rule.Program}\" protocol={rule.Protocol} localport={rule.LocalPort} profile=any");
        }
        var chain = string.Join(" && ", steps);
        return new ElevatedCommand(Path.Combine(systemDirectory, "cmd.exe"), $"/d /c \"{chain}\"");
    }

    /// <summary>
    /// Uninstall's one elevated command (batch 641): deletes those of the two rules that the read-back <paramref name="shown"/> holds, by
    /// their fixed names, with the same cmd and netsh as <see cref="Command"/>. Null when neither rule exists: nothing to ask Windows.
    /// </summary>
    public static ElevatedCommand? RemoveCommand(IReadOnlyList<ShownRule> shown, string systemDirectory)
    {
        var netsh = Netsh(systemDirectory);
        var steps = new[] { SignInRule, GamesRule }.Where(name => shown.Any(block => Same(block.Name, name)))
            .Select(name => $"\"{netsh}\" advfirewall firewall delete rule name=\"{name}\"").ToArray();
        return steps.Length == 0 ? null : new ElevatedCommand(Path.Combine(systemDirectory, "cmd.exe"), $"/d /c \"{string.Join(" && ", steps)}\"");
    }

    private static string Netsh(string systemDirectory) => Path.IsPathFullyQualified(systemDirectory) && !Unsafe(systemDirectory)
        ? Path.Combine(systemDirectory, "netsh.exe")
        : throw new ArgumentException("The system folder must be a full path of plain characters.", nameof(systemDirectory));

    /// <summary>
    /// The blocks of <c>netsh advfirewall firewall show rule name=all verbose</c>: a "Rule Name:" line starts a block, and each "label: value"
    /// line after it is one of its fields. The first colon splits, so a drive colon stays in the value. The test machine prints English
    /// labels and key values; the dashed lines, the localized grouping names and "No rules match" carry no rule and are skipped.
    /// </summary>
    public static IReadOnlyList<ShownRule> ParseShowRules(string text)
    {
        var rules = new List<ShownRule>();
        string? name = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null) rules.Add(new ShownRule(name, fields));
                name = line["Rule Name:".Length..].Trim();
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var colon = line.IndexOf(':');
            if (name is not null && colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (name is not null) rules.Add(new ShownRule(name, fields));
        return rules;
    }

    /// <summary>
    /// The names of our rules that the read-back found, in any state. After a failed read-back, none: a delete of a rule that is not there
    /// would end the command's chain before its add, so the add runs alone and the read-back after the prompt decides.
    /// </summary>
    public static IReadOnlyList<string> ExistingNames(IReadOnlyList<FirewallRule> rules, IReadOnlyList<ShownRule>? shown)
    {
        if (shown is null) return [];
        return rules.Where(ours => shown.Any(block => Same(block.Name, ours.Name))).Select(ours => ours.Name).ToArray();
    }

    /// <summary>
    /// The state from one read-back (null: netsh could not be read). A block rule for either program wins over everything. Each of our
    /// rules must be an enabled inbound allow with its program, protocol and ports, and must cover Domain, Private and Public; the
    /// result is the worst state of the two rules.
    /// </summary>
    public static FirewallState Evaluate(IReadOnlyList<FirewallRule> rules, IReadOnlyList<ShownRule>? shown)
    {
        if (shown is null) return FirewallState.Unreadable;
        if (shown.Any(block => IsOn(block) && Same(block.Field("Action"), "Block") && rules.Any(ours => Same(block.Field("Program"), ours.Program))))
            return FirewallState.Blocked;
        var worst = FirewallState.Allowed;
        foreach (var ours in rules)
        {
            var named = shown.Where(block => Same(block.Name, ours.Name)).ToArray();
            var state = named.Any(block => Fits(block, ours) && EveryNetwork(block)) ? FirewallState.Allowed
                : named.Any(block => Fits(block, ours)) ? FirewallState.SomeNetworks
                : named.Any(block => !Same(block.Field("Program"), ours.Program)) ? FirewallState.OtherFolder
                : FirewallState.Missing;
            if (state > worst) worst = state;
        }
        return worst;
    }

    /// <summary>The host page's card for a state, in plain words; null when everything is allowed.</summary>
    public static FirewallProblem? Problem(FirewallState state) => state switch
    {
        FirewallState.Allowed => null,
        FirewallState.SomeNetworks => new("A-NET-02", "Windows lets friends in on some network types only.",
            "Choose Allow through Windows Firewall again: friends need every network type."),
        FirewallState.OtherFolder => new("A-NET-02", "Windows Firewall has Amberline's rule for another folder.",
            "Choose Allow through Windows Firewall again to point the rule at this folder."),
        FirewallState.Blocked => new("A-NET-02", "Windows Firewall blocks Amberline.",
            "Remove the block rule in Windows Defender Firewall (Advanced settings), then try again."),
        // Reconstruction: the design names no card for an unreadable firewall; it says only what could not be seen.
        FirewallState.Unreadable => new("A-NET-02", "Amberline could not read the Windows Firewall rules on this PC.",
            "Check that Windows Firewall is running, then try again."),
        // The tip under the button says what Windows then asks (batch 643: the card said it a second time).
        _ => new("A-NET-02", "Windows Firewall blocks Amberline.", "Choose Allow through Windows Firewall."),
    };

    /// <summary>
    /// The read-back: <c>netsh advfirewall firewall show rule name=all verbose</c>, which needs no elevation. Null when netsh cannot be
    /// started or prints no rule block: Windows always has built-in rules, so none means the output was not read (fails closed, never
    /// a false "missing"). No check calls this.
    /// </summary>
    public static async Task<IReadOnlyList<ShownRule>?> ReadRulesAsync(CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var start = new ProcessStartInfo(NetshPath, "advfirewall firewall show rule name=all verbose")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The labels and key values are ASCII; a localized grouping name may not decode, and nothing reads it.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var netsh = Process.Start(start) ?? throw new InvalidOperationException("netsh did not start.");
            var output = netsh.StandardOutput.ReadToEndAsync();
            var errors = netsh.StandardError.ReadToEndAsync();
            await netsh.WaitForExitAsync(cancel);
            var rules = ParseShowRules(await output);
            await errors;
            return rules.Count > 0 ? rules : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The one elevated step: the command's program starts with the runas verb, so Windows asks once; the prompt names Windows Command
    /// Processor, Microsoft's cmd.exe. Returns the exit code, or null when the prompt was declined. No check calls this.
    /// </summary>
    public static async Task<int?> RunElevatedAsync(ElevatedCommand command, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall rules exist on Windows only.");
        try
        {
            using var shell = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("The command processor did not start.");
            await shell.WaitForExitAsync(cancel);
            return shell.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    /// <summary>
    /// The page's flow (design 5.2): read the rules; return at once when they are allowed, or when a block rule exists (the command cannot
    /// remove it); refuse without a prompt when a program path has a character the command line cannot carry; otherwise run the one
    /// elevated command and read the rules back. <paramref name="read"/> and <paramref name="run"/> replace the real read-back and the
    /// elevated start (the dashboard's checks pass ones that never run netsh or elevate); no check calls this with the real ones.
    /// </summary>
    public static async Task<FirewallAttempt> AllowAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancel = default,
        Func<CancellationToken, Task<IReadOnlyList<ShownRule>?>>? read = null, Func<ElevatedCommand, CancellationToken, Task<int?>>? run = null)
    {
        read ??= ReadRulesAsync;
        run ??= RunElevatedAsync;
        var before = await read(cancel);
        var state = Evaluate(rules, before);
        if (state is FirewallState.Allowed or FirewallState.Blocked) return new FirewallAttempt(state, false, null);
        if (Refusal(rules) is { } refusal) return new FirewallAttempt(null, false, refusal);
        if (await run(Command(rules, ExistingNames(rules, before), Environment.SystemDirectory), cancel) is null)
            return new FirewallAttempt(null, true, null);
        return new FirewallAttempt(Evaluate(rules, await read(cancel)), false, null);
    }

    /// <summary>A program path must be a full path; characters that cmd would misread are not an error here, <see cref="Refusal"/> refuses them.</summary>
    private static string FullPath(string path) =>
        Path.IsPathFullyQualified(path) ? path : throw new ArgumentException("The program path must be a full path.", nameof(path));

    /// <summary>Every value on the command line: a plain name, TCP or UDP, ports that are numbers in range, and a plain full program path.</summary>
    private static void CheckRule(FirewallRule rule)
    {
        if (Unsafe(rule.Name) || rule.Protocol is not ("TCP" or "UDP") || !PortsValid(rule.LocalPort) || Unsafe(rule.Program) ||
            !Path.IsPathFullyQualified(rule.Program))
            throw new ArgumentException($"The firewall rule {rule.Name} has a value that the command line cannot carry.", nameof(rule));
    }

    /// <summary>A port or a range "first-last": one to five digits each, within 1 to 65535, the first not above the last.</summary>
    private static bool PortsValid(string text)
    {
        var ends = text.Split('-');
        if (ends.Length > 2 || ends.Any(end => end.Length is < 1 or > 5 || !end.All(char.IsAsciiDigit))) return false;
        var numbers = ends.Select(end => int.Parse(end, CultureInfo.InvariantCulture)).ToArray();
        return numbers.All(number => number is >= 1 and <= 65535) && (numbers.Length == 1 || numbers[0] <= numbers[1]);
    }

    /// <summary>
    /// Characters cmd or netsh could read as something else, even inside double quotes: control characters, anything outside plain ASCII,
    /// and %, !, ^ and the double quote itself.
    /// </summary>
    private static bool Unsafe(string text) => text.Any(c => c is < ' ' or > '~' or '%' or '!' or '^' or '"');

    /// <summary>A block of our name counts when it is an enabled inbound allow for our program, protocol and port.</summary>
    private static bool Fits(ShownRule block, FirewallRule ours) =>
        IsOn(block) && Same(block.Field("Direction"), "In") && Same(block.Field("Action"), "Allow") &&
        Same(block.Field("Program"), ours.Program) && Same(block.Field("Protocol"), ours.Protocol) && Same(block.Field("LocalPort"), ours.LocalPort);

    private static bool EveryNetwork(ShownRule block)
    {
        var profiles = block.Field("Profiles").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new[] { "Domain", "Private", "Public" }.All(wanted => profiles.Any(profile => Same(profile, wanted)));
    }

    private static bool IsOn(ShownRule block) => Same(block.Field("Enabled"), "Yes");

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
