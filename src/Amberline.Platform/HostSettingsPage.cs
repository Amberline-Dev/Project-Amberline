using System.Net;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the home host's Settings (batch 641; Amberline Host design, sections 2.3 and 2.6; retail had no host page). It changes,
/// after the first run, what the first run asked, with the first run's own pages and checks: the server name and the address
/// (<see cref="HostSteps.MapSettings"/>), the game and AIR client folders (<see cref="HostFolders.MapSettings"/>, which restarts), the
/// update check (the Updates page) and the Play file (H8's download, and the certificate's Make new Play files). A change friends
/// must hear about says so and links the Play file. Batch 656: Sign-ups holds the switch of <see cref="HostSelfRegistration"/> and, while
/// it is on, the link to send. Batch 666: Cheat commands holds the switch of <see cref="CheatRule"/>. Uninstall Amberline
/// (<see cref="HostUninstall"/>) is its last section.
/// </summary>
public static class HostSettingsPage
{
    public const string PagePath = "/host/settings";
    public const string NamePath = PagePath + "/name", AddressPath = PagePath + "/address", FoldersPath = PagePath + "/folders",
        UninstallPath = PagePath + "/uninstall", RegisterPath = PagePath + "/register", CheatsPath = PagePath + "/cheats";

    /// <summary>
    /// DIAGNOSTICS (batch 666; owner, 3 November 2026: "Disable cheats", off for everyone, the host included, with a Settings switch):
    /// the home host's cheat rule for <see cref="PlatformGames.MayCheat"/>. A seat may cheat only while Settings' "Allow cheat commands in
    /// my games" is on (<see cref="PlatformSettings.AllowCheats"/>, off for new and existing installs) and only when it is the host's own
    /// login (<c>hostAccount</c>). It reads <c>platform.json</c> as each game starts; a file it cannot read allows nobody.
    /// </summary>
    public static Func<long, bool> CheatRule(string settingsFile) => account =>
        HostMode.ReadOrDefault(settingsFile) is { AllowCheats: true, HostAccount: { } own } && own == account;

    private const string PlayFileLink = $"<a href={HostInvite.PlayFilePath} download>Download the Play file</a>";

    /// <summary>
    /// Settings and its pages in the full home Platform; <paramref name="games"/> counts the matches a restart or an uninstall would end.
    /// Batch 656: <see cref="RegisterPath"/> saves the switch of <paramref name="signUp"/> (people register themselves).
    /// </summary>
    public static void Map(WebApplication app, HostLoop loop, HostSteps steps, Func<int> games, HostSelfRegistration signUp)
    {
        steps.MapSettings(app);
        HostFolders.MapSettings(app, loop, games);
        app.MapGet(PagePath, (HttpContext context, IAntiforgery antiforgery) =>
            HostMode.Page(Render(loop, steps, signUp, Program.RegistrationToken(context, antiforgery), context.Request.Query["saved"].ToString())));
        app.MapPost(RegisterPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null)
                return HostMode.Page(Render(loop, steps, signUp, Program.RegistrationToken(context, antiforgery), "", HostSteps.Expired),
                    StatusCodes.Status400BadRequest);
            signUp.Set(form["on"] == "on");
            return Results.Redirect(PagePath + "?saved=register");
        });
        app.MapPost(CheatsPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null)
                return HostMode.Page(Render(loop, steps, signUp, Program.RegistrationToken(context, antiforgery), "", HostSteps.Expired),
                    StatusCodes.Status400BadRequest);
            // Batch 657's one way to save: a failed read writes nothing, and the page says so.
            HostMode.Change(loop.Paths.Settings, saved => saved with { AllowCheats = form["on"] == "on" });
            return Results.Redirect(PagePath + "?saved=cheats");
        });
        app.MapGet(UninstallPath, (HttpContext context, IAntiforgery antiforgery) =>
            UninstallPage(loop, Program.RegistrationToken(context, antiforgery), games(), null, false, false));
        app.MapPost(UninstallPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            var token = Program.RegistrationToken(context, antiforgery);
            var (clients, data) = (form?["clients"].ToString() == "on", form?["data"].ToString() == "on");
            if (form is null) return UninstallPage(loop, token, games(), (HostSteps.Expired, null), clients, data, StatusCodes.Status400BadRequest);
            // A game that started since the page was opened ends the uninstall here: the host decides again on the page.
            if (games() > 0) return UninstallPage(loop, token, games(), null, clients, data, StatusCodes.Status409Conflict);
            if (!HostUninstall.Confirmed(form["confirm"]))
                return UninstallPage(loop, token, 0, ($"Type {HostUninstall.ConfirmWord} in the box to confirm.", "confirm"), clients, data, StatusCodes.Status400BadRequest);
            if (Plan(loop) is not { } plan) return UninstallPage(loop, token, 0, null, clients, data);
            var result = await HostUninstall.RunAsync(plan, clients, data, loop.Uninstall, loop.Network, context.RequestAborted);
            loop.Cleanup = result.Cleanup;
            loop.Screen.WriteLine("Amberline is uninstalled. This window closes by itself.");
            context.Response.OnCompleted(() => { loop.Stop(); return Task.CompletedTask; });
            return HostMode.Page(Done(result), nav: false);
        });
    }

    private static UninstallPlan? Plan(HostLoop loop) =>
        HostUninstall.Plan(loop.Paths, HostMode.ReadOrDefault(loop.Paths.Settings), loop.Uninstall.LocalAppData);

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Render(HostLoop loop, HostSteps steps, HostSelfRegistration signUp, string token, string saved, string? problem = null)
    {
        var settings = HostMode.ReadOrDefault(loop.Paths.Settings);
        var notice = problem is not null ? HostPages.Errors((problem, null)) : saved switch
        {
            "name" => $"<p class=notice role=status>Saved. Send your friends the new Play file to show the new name. {PlayFileLink}</p>",
            "address" => $"<p class=notice role=status>Saved. Send your friends the new Play file: their old one looks at the old address. {PlayFileLink}</p>",
            "folders" => "<p class=notice role=status>Saved. Amberline restarted to use the new folders.</p>",
            "register" => "<p class=notice role=status>Saved.</p>",
            "cheats" => "<p class=notice role=status>Saved. It counts for games that start from now on.</p>",
            _ => "",
        };
        var address = string.IsNullOrWhiteSpace(steps.PublicHost) ? "Not chosen yet."
            : $"<b>{Encode(steps.PublicHost)}</b> ({HostSteps.Source(settings.AddressSource)})";
        var updates = HostUpdates.Read(loop.Paths.Data).Check == true ? "On: Amberline asks GitHub once a day." : "Off.";
        return $$"""
            <h1>Settings</h1>
            {{notice}}
            <section class=folder aria-labelledby=name-title><h2 id=name-title>Server name</h2><p><b>{{Encode(steps.ServerName)}}</b></p>
            <p><a href={{NamePath}}>Change the server name</a></p></section>
            <section class=folder aria-labelledby=address-title><h2 id=address-title>Your address</h2><p>{{address}}</p>
            <p><a href={{AddressPath}}{{HostSteps.AddressWait}}>Change your address</a></p></section>
            <section class=folder aria-labelledby=folders-title><h2 id=folders-title>Your game folders</h2>
            <p>Game folder: {{Encode(settings.DataRoot)}}</p><p>AIR client folder: {{Encode(settings.AirRoot)}}</p>
            <p><a href={{FoldersPath}}{{HostPages.Wait("Looking at your folders. This takes a few seconds.")}}>Change the folders</a></p></section>
            <section class=folder aria-labelledby=updates-title><h2 id=updates-title>Updates</h2><p>{{updates}}</p>
            <p><a href=/host/updates>Change the update check</a></p></section>
            <section class=folder aria-labelledby=play-title><h2 id=play-title>Play file</h2>
            <p>{{PlayFileLink}}</p>
            <p><a href="{{HostAttention.CertificatePath}}?from=settings">Make new Play files</a>: every friend's old Play file stops working.</p></section>
            <section class=folder aria-labelledby=register-title><h2 id=register-title>Sign-ups</h2>
            <form method=post action={{RegisterPath}}>{{token}}
            <label class=choice><input type=checkbox name=on value=on{{(signUp.On ? " checked" : "")}}>Let people register themselves</label>
            <div class=buttons><button type=submit>Save</button></div></form>
            {{signUp.Link(steps.PublicHost)}}</section>
            <section class=folder aria-labelledby=cheats-title><h2 id=cheats-title>Cheat commands</h2>
            <form method=post action={{CheatsPath}}>{{token}}
            <label class=choice><input type=checkbox name=on value=on aria-describedby=cheats-hint{{(settings.AllowCheats ? " checked" : "")}}>Allow cheat commands in my games</label>
            <p class=hint id=cheats-hint>Only your own login can use them. Your friends never can.</p>
            <div class=buttons><button type=submit>Save</button></div></form></section>
            <section class=folder aria-labelledby=uninstall-title><h2 id=uninstall-title>Uninstall Amberline</h2>
            <p><a href={{UninstallPath}}>Uninstall Amberline</a></p></section>
            """;
    }

    /// <summary>
    /// The uninstall page: the install folder and its size, what goes, the two choices and the typed confirmation. A running game or a
    /// Platform that is not installed gets the reason instead of the form.
    /// </summary>
    private static IResult UninstallPage(HostLoop loop, string token, int games, (string? Text, string? Field)? problem, bool clients, bool data,
        int status = StatusCodes.Status200OK)
    {
        const string Back = $"<p><a href={PagePath}>Back</a></p>";
        if (Plan(loop) is not { } plan)
            return HostMode.Page($"<h1>Uninstall Amberline</h1><p>{Encode(HostUninstall.NotInstalled(loop.Paths))}</p>{Back}", status);
        if (games > 0)
            return HostMode.Page("<h1>Uninstall Amberline</h1><p class=problem role=alert>A game is running. Uninstall Amberline when it has ended.</p>" +
                Back, status);
        var shown = problem ?? default;
        string Tick(string name, bool on, string text) =>
            $"<label class=choice><input type=checkbox name={name} value=on{(on ? " checked" : "")}>{text}</label>";
        var choices = (plan.Has(UninstallKind.Clients)
                ? Tick("clients", clients, $"Also delete the game and AIR client copies ({HostUninstall.Size(plan.Size(UninstallKind.Clients))})") : "") +
            (plan.Has(UninstallKind.Data)
                ? Tick("data", data, $"Also delete your logins and settings ({HostUninstall.Size(plan.Size(UninstallKind.Data))}). " +
                    "Friends' logins and Play files stop working for good.") : "");
        return HostMode.Page($$"""
            <h1>Uninstall Amberline</h1>
            {{HostPages.Errors(shown)}}
            <p>Amberline's folder: <b>{{Encode(plan.Folder)}}</b> ({{HostUninstall.Size(plan.Bytes)}}). Uninstalling stops Amberline and removes:</p>
            <ul><li>the Amberline program ({{HostUninstall.Size(plan.Size(UninstallKind.Program))}}) and its desktop icon</li>
            <li>the note of where its folder is</li>
            <li>its two Windows Firewall rules (Windows asks once, for Windows Command Processor: choose Yes)</li>
            <li>the ports it opened on your router</li></ul>
            {{string.Concat(plan.Outside.Select(line => $"<p>{Encode(line)}.</p>"))}}
            <form method=post action={{UninstallPath}} novalidate{{HostPages.Wait("Uninstalling Amberline. If Windows asks, choose Yes.")}}>{{token}}
            {{(choices.Length == 0 ? "" : $"<fieldset><legend>What else to delete</legend>{choices}</fieldset>")}}
            {{HostPages.Field("confirm", $"Type {HostUninstall.ConfirmWord} to confirm", "This cannot be undone.", shown.Field == "confirm" ? shown.Text : null,
                "required autocomplete=off spellcheck=false")}}
            <div class=actions><a href={{PagePath}}>Cancel</a><button type=submit>Uninstall Amberline</button></div></form>
            """, status);
    }

    /// <summary>The page after an uninstall: what was removed, what was kept and where, and what is left for the host to do.</summary>
    private static string Done(UninstallResult result)
    {
        static string List(string title, IReadOnlyList<string> lines) =>
            lines.Count == 0 ? "" : $"<h2>{title}</h2><ul>{string.Concat(lines.Select(line => $"<li>{Encode(line)}</li>"))}</ul>";
        return $"""
            <h1>Amberline is uninstalled</h1>
            <p>Amberline has stopped; its window closes by itself.</p>
            {List("Removed", result.Done)}
            {List("Kept", result.Kept)}
            {List("Still to do", result.ByHand)}
            <p>You can close this browser tab.</p>
            """;
    }
}
