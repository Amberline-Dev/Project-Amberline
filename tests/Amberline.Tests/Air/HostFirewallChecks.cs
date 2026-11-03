using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The host's Windows Firewall rules (Amberline Host design, sections 5.2 and 8, batch H4). The elevated command line is compared as
/// text, its refusals and port checks are exercised directly, and the read-back is tested on synthetic samples in netsh's layout as the
/// test machine prints it (English labels and values). Nothing here runs cmd or netsh, changes the firewall or elevates: the read-back
/// and the elevated run are not called by any check.
/// </summary>
public static class HostFirewallChecks
{
    private const string PlatformExe = @"C:\Amberline Host\app\platform\Amberline.Platform.exe";
    private const string ServerExe = @"C:\Amberline Host\app\server\Amberline.Server.exe";
    private const string OtherPlatformExe = @"D:\Old Amberline\app\platform\Amberline.Platform.exe";
    private const string SystemFolder = @"C:\Windows\system32";
    private const string AllNetworks = "Domain,Private,Public";
    private static readonly IReadOnlyList<FirewallRule> Rules = HostFirewall.Rules(PlatformExe, ServerExe, 8391, 5219, 4);
    private static readonly string[] NoNames = [];
    private static readonly string[] BothNames = [HostFirewall.SignInRule, HostFirewall.GamesRule];
    private static readonly string[] GamesOnlyName = [HostFirewall.GamesRule];

    /// <summary>The arguments of a first install, exactly: cmd /d /c, the chain in quotes, each netsh by its full System32 path, joined with &amp;&amp;.</summary>
    private const string FirstInstall =
        @"/d /c """"C:\Windows\system32\netsh.exe"" advfirewall firewall add rule name=""Amberline sign-in"" dir=in action=allow program=""C:\Amberline Host\app\platform\Amberline.Platform.exe"" protocol=TCP localport=8391 profile=any && ""C:\Windows\system32\netsh.exe"" advfirewall firewall add rule name=""Amberline games"" dir=in action=allow program=""C:\Amberline Host\app\server\Amberline.Server.exe"" protocol=UDP localport=5219-5222 profile=any""";
    private const string AddSignIn =
        @"advfirewall firewall add rule name=""Amberline sign-in"" dir=in action=allow program=""C:\Amberline Host\app\platform\Amberline.Platform.exe"" protocol=TCP localport=8391 profile=any";
    private const string AddGames =
        @"advfirewall firewall add rule name=""Amberline games"" dir=in action=allow program=""C:\Amberline Host\app\server\Amberline.Server.exe"" protocol=UDP localport=5219-5222 profile=any";
    private const string DeleteSignIn = @"advfirewall firewall delete rule name=""Amberline sign-in""";
    private const string DeleteGames = @"advfirewall firewall delete rule name=""Amberline games""";

    [Check("server.host-firewall-script-exact")]
    public static void ScriptExact()
    {
        var first = HostFirewall.Command(Rules, NoNames, SystemFolder);
        TestAssert.Equal(@"C:\Windows\system32\cmd.exe", first.FileName, "cmd.exe by its full System32 path, never a PATH lookup");
        TestAssert.Equal(FirstInstall, first.Arguments, "a first install: both adds, chained with &&, each netsh by its full path");
        TestAssert.True(first.Arguments.StartsWith("/d /c ", StringComparison.Ordinal), "/d skips AutoRun commands from the registry");
        TestAssert.False(first.Arguments.Contains(" -f", StringComparison.Ordinal) || first.Arguments.Contains(".txt", StringComparison.Ordinal),
            "no script file: netsh is never given -f and no file is named");
        TestAssert.Equal(Chain(DeleteSignIn, AddSignIn, DeleteGames, AddGames), HostFirewall.Command(Rules, BothNames, SystemFolder).Arguments,
            "both rules exist: each delete comes right before its add");
        TestAssert.Equal(Chain(AddSignIn, DeleteGames, AddGames), HostFirewall.Command(Rules, GamesOnlyName, SystemFolder).Arguments,
            "only the games rule exists: only it is deleted");

        TestAssert.True(HostFirewall.Refusal(Rules) is null, "plain program paths are not refused");
        foreach (var (label, folder) in new[]
                 {
                     ("a percent sign", @"C:\Amberline 100%"),
                     ("an exclamation mark", @"C:\Amberline wow!"),
                     ("a caret", @"C:\Amberline a^b"),
                     ("a double quote", "C:\\Amberline \"x\""),
                     ("a control character", "C:\\Amberline\tx"),
                     ("a letter outside plain ASCII", "C:\\Amberline \u00D3"),
                 })
        {
            var refused = HostFirewall.Rules(folder + @"\app\platform\Amberline.Platform.exe", ServerExe, 8391, 5219, 4);
            TestAssert.True(HostFirewall.Refusal(refused) is not null, $"a program path with {label} gets the refusal card, with no prompt");
            TestAssert.Throws<ArgumentException>(() => HostFirewall.Command(refused, NoNames, SystemFolder),
                $"a program path with {label} never reaches the command line");
        }
        TestAssert.Throws<ArgumentException>(() => HostFirewall.Command(
                [new FirewallRule(HostFirewall.SignInRule, PlatformExe, "TCP", "8391 & calc")], NoNames, SystemFolder),
            "a port that is not a number or a range never reaches the command line");
        TestAssert.Throws<ArgumentException>(() => HostFirewall.Command(
                [new FirewallRule(HostFirewall.SignInRule, PlatformExe, "TCP && calc", "8391")], NoNames, SystemFolder),
            "a protocol other than TCP or UDP never reaches the command line");
        TestAssert.Throws<ArgumentException>(() => HostFirewall.Command(Rules, NoNames, "system32"), "the system folder must be a full path");

        TestAssert.Equal("8391", Rules[0].LocalPort, "the sign-in rule covers the public port");
        TestAssert.Equal("TCP", Rules[0].Protocol);
        TestAssert.Equal("5219-5222", Rules[1].LocalPort, "the games rule covers the four game ports");
        TestAssert.Equal("5219", HostFirewall.Rules(PlatformExe, ServerExe, 8391, 5219, 1)[1].LocalPort, "one game port is a single port");
        TestAssert.Throws<ArgumentException>(() => HostFirewall.Rules(@"relative\Amberline.Platform.exe", ServerExe, 8391, 5219, 4),
            "a relative program path is refused");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => HostFirewall.Rules(PlatformExe, ServerExe, 70000, 5219, 4),
            "a sign-in port above 65535 is refused");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => HostFirewall.Rules(PlatformExe, ServerExe, 8391, 65535, 2),
            "game ports past 65535 are refused");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => HostFirewall.Rules(PlatformExe, ServerExe, 8391, 5219, 0),
            "a host with no game is refused");
    }

    [Check("server.host-firewall-rules-parse")]
    public static void RulesParse()
    {
        var all = HostFirewall.ParseShowRules(Shown(SignIn(), Games(), CoreDns()));
        TestAssert.Equal(3, all.Count, "one block per Rule Name line");
        TestAssert.Equal(HostFirewall.SignInRule, all[0].Name);
        TestAssert.Equal(PlatformExe, all[0].Field("Program"), "a program path with spaces and a colon is read whole");
        TestAssert.Equal("TCP", all[0].Field("Protocol"));
        TestAssert.Equal(BothNames, HostFirewall.ExistingNames(Rules, all).ToArray(), "both rules exist");
        TestAssert.Equal(NoNames, HostFirewall.ExistingNames(Rules, HostFirewall.ParseShowRules("No rules match the specified criteria.\r\n")).ToArray(),
            "no rule printed: none exists");
        TestAssert.Equal(NoNames, HostFirewall.ExistingNames(Rules, null).ToArray(),
            "unreadable: no delete, so the && chain cannot end early on a missing rule");

        TestAssert.Equal(FirewallState.Allowed, State(Shown(SignIn(), Games(), CoreDns())), "both rules on every network type");
        TestAssert.Equal(FirewallState.Allowed, State(Shown(SignIn(), Games(), CoreDns()).Replace("\r\n", "\n")), "LF line endings read the same");
        TestAssert.Equal(FirewallState.OtherFolder, State(Shown(SignIn(program: OtherPlatformExe), Games())), "the sign-in rule names another folder");
        TestAssert.Equal(FirewallState.Blocked, State(Shown(SignIn(), Games(), Block("Old block", "Yes", "In", "Block", AllNetworks, "UDP", "Any", ServerExe))),
            "a block rule for the server beats both allow rules");
        TestAssert.Equal(FirewallState.Allowed, State(Shown(SignIn(), Games(), Block("Switched off", "No", "In", "Block", AllNetworks, "UDP", "Any", ServerExe))),
            "a switched-off block rule does not count");
        TestAssert.Equal(FirewallState.SomeNetworks, State(Shown(SignIn("Domain,Private"), Games())), "no Public profile: not every network type");
        TestAssert.Equal(FirewallState.SomeNetworks, State(Shown(SignIn("Private,Public"), Games())), "no Domain profile: not every network type");
        TestAssert.Equal(FirewallState.Missing, State(Shown(SignIn(enabled: "No"), Games())), "a switched-off allow rule does not allow");
        TestAssert.Equal(FirewallState.Missing, State(Shown(SignIn(), Block(HostFirewall.GamesRule, "Yes", "In", "Allow", AllNetworks, "UDP", "5219", ServerExe))),
            "a games rule on another port is not ours");
        TestAssert.Equal(FirewallState.Missing, State("No rules match the specified criteria.\r\n"), "no rule printed");
        TestAssert.Equal(FirewallState.Unreadable, State(null), "netsh could not be read");

        TestAssert.True(HostFirewall.Problem(FirewallState.Allowed) is null, "no card when everything is allowed");
        TestAssert.Equal("A-NET-02", HostFirewall.Problem(FirewallState.Blocked)?.Code ?? "", "a block rule is the firewall problem code");
    }

    private static FirewallState State(string? text) =>
        HostFirewall.Evaluate(Rules, text is null ? null : HostFirewall.ParseShowRules(text));

    /// <summary>The arguments the checks expect for these steps: cmd /d /c, the chain in quotes, each netsh by its full path, joined with &amp;&amp;.</summary>
    private static string Chain(params string[] steps) =>
        "/d /c \"" + string.Join(" && ", steps.Select(step => "\"C:\\Windows\\system32\\netsh.exe\" " + step)) + "\"";

    /// <summary>The output as netsh prints it: the blocks, each followed by a blank line.</summary>
    private static string Shown(params string[] blocks) => string.Concat(blocks);

    /// <summary>One rule block in netsh's layout: a Rule Name line, a dashed line, then the labelled fields (labels padded to 38 columns).</summary>
    private static string Block(string name, string enabled, string direction, string action, string profiles, string protocol, string localPort,
        string program, string grouping = "") =>
        Lines(Field("Rule Name", name), new string('-', 70), Field("Enabled", enabled), Field("Direction", direction),
            Field("Profiles", profiles), Field("Grouping", grouping), Field("LocalIP", "Any"), Field("RemoteIP", "Any"),
            Field("Protocol", protocol), Field("LocalPort", localPort), Field("RemotePort", "Any"), Field("Edge traversal", "No"),
            Field("Action", action), Field("Program", program)) + "\r\n";

    /// <summary>One line per string, each ending in CRLF as netsh prints it.</summary>
    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\r\n"));

    private static string Field(string label, string value) => $"{label + ":",-38}{value}";

    private static string SignIn(string profiles = AllNetworks, string enabled = "Yes", string program = PlatformExe) =>
        Block(HostFirewall.SignInRule, enabled, "In", "Allow", profiles, "TCP", "8391", program);

    private static string Games(string program = ServerExe) =>
        Block(HostFirewall.GamesRule, "Yes", "In", "Allow", AllNetworks, "UDP", "5219-5222", program);

    /// <summary>A built-in rule with a localized grouping name: the parser keeps unknown fields and ignores their text.</summary>
    private static string CoreDns() =>
        Block("Core Networking - DNS (UDP-Out)", "Yes", "Out", "Allow", AllNetworks, "UDP", "Any", "Any", "Podstawowe operacje sieciowe");
}
