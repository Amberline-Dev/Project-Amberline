namespace Amberline.Tests.Air;

/// <summary>
/// Batch 921: every box that shows the invite link or the sign-up link wraps inside its panel (white-space pre-wrap and overflow-wrap
/// anywhere, not break-all). The sign-up sheet and the host sheet both take the notice rule from the shared parts of HostPages.cs; the
/// operator page (/manage) has its own inline sheet, which carries the same rule and lets its long lines (the sign-up link) wrap.
/// Source checks: no browser is started.
/// </summary>
public static class HostBoxWrapChecks
{
    [Check("server.host-box-wrap")]
    public static void InviteAndSignUpBoxesWrapOnEverySheet()
    {
        var sheet = File.ReadAllText(WorkspaceFiles.Find("src/Amberline.Platform/HostPages.cs"));
        TestAssert.True(SheetLiteral(sheet, "Parts").Contains(
            ".message{padding:12px 16px;border:1px solid var(--line);border-radius:2px;background:var(--field);white-space:pre-wrap;overflow-wrap:anywhere;",
            StringComparison.Ordinal), "the shared parts wrap a message box (pre-wrap, anywhere)");
        TestAssert.True(sheet.Contains("PublicCss = Look + \"\\r\\n\" + SignUpPanel + \"\\r\\n\" + Parts + ", StringComparison.Ordinal),
            "the sign-up sheet takes the shared parts");
        TestAssert.True(sheet.Contains("Css = Look + \"\\r\\n\" + Parts + ", StringComparison.Ordinal), "the host sheet takes the shared parts");
        TestAssert.True(SheetLiteral(sheet, "SignUpPanel").Contains(".message{text-align:left}", StringComparison.Ordinal),
            "the sign-up sheet's message boxes read from the left");
        var manage = File.ReadAllText(WorkspaceFiles.Find("src/Amberline.Platform/PlatformManagement.cs"));
        TestAssert.True(manage.Contains(".message{white-space:pre-wrap;overflow-wrap:anywhere}</style>", StringComparison.Ordinal),
            "the operator page's invite box wraps");
        TestAssert.True(manage.Contains("max-width:900px;margin:5vh auto;padding:24px;overflow-wrap:anywhere}", StringComparison.Ordinal),
            "the operator page's text wraps (its sign-up link)");
    }

    /// <summary>The text of one raw string constant of HostPages.cs, from its opening quotes to its closing quotes.</summary>
    internal static string SheetLiteral(string source, string name)
    {
        var opening = $"private const string {name} = \"\"\"";
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        TestAssert.True(start >= 0, $"HostPages.cs has no {name} literal");
        var end = source.IndexOf("\"\"\";", start + opening.Length, StringComparison.Ordinal);
        TestAssert.True(end > start, $"HostPages.cs's {name} literal does not close");
        return source[start..end];
    }
}
