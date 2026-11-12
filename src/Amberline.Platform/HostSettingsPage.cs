using System.Net;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the home host's Settings (batch 641; Amberline Host design, sections 2.3 and 2.6; retail had no host page). It changes,
/// after the first run, what the first run asked, with the first run's own pages and checks: the server name and the address
/// (<see cref="HostSteps.MapSettings"/>), the game and AIR client folders (<see cref="HostFolders.MapSettings"/>, which restarts), the
/// update check (the Updates page) and the Play file (H8's download, and the certificate's Make new Play files). A change friends
/// must hear about says so and links the Play file. Batch 656: Sign-ups holds the switch of <see cref="HostSelfRegistration"/> and, while
/// it is on, the link to send. Batch 681: Game files holds the switch of <see cref="HostFileShare"/> and what it shares. Batch 680: Cheat
/// commands holds the list of <see cref="CheatRule"/>. Batch 684: Games holds <see cref="HostGamesSetting"/>, how many games run at
/// once. Uninstall Amberline
/// (<see cref="HostUninstall"/>) is its last section.
/// </summary>
public static class HostSettingsPage
{
    public const string PagePath = "/host/settings";
    public const string NamePath = PagePath + "/name", AddressPath = PagePath + "/address", FoldersPath = PagePath + "/folders",
        UninstallPath = PagePath + "/uninstall", RegisterPath = PagePath + "/register", CheatsPath = PagePath + "/cheats",
        SharePath = PagePath + "/share";
    // NOT RETAIL (batch 751): retail had no host page for either switch; these two paths are the Settings sections' own POST routes.
    /// <summary>Batch 751: Settings' "Skins and RP" switch and its Champions list (<see cref="PlatformControls"/>).</summary>
    public const string PaidContentPath = PagePath + "/paid-content", ChampionsPath = PagePath + "/champions";

    /// <summary>
    /// DIAGNOSTICS (batch 680; owner, 4 November 2026: "selected person will have cheats in all matches - only toggleable by the host",
    /// replacing batch 666's one switch): the home host's cheat rule for <see cref="PlatformGames.MayCheat"/>. A seat may cheat only when
    /// its login is ticked in Settings' Cheat commands (<see cref="PlatformSettings.CheatLogins"/>; nobody for a new install, the host's
    /// own for one whose batch-666 switch was on). It reads <c>platform.json</c> as each game starts; a file it cannot read allows nobody.
    /// Only this gated page writes the list: no AIR call, chat command or friends' port route does.
    /// </summary>
    public static Func<long, bool> CheatRule(string settingsFile) => account =>
        HostMode.ReadOrDefault(settingsFile).CheatLogins().Contains(account);

    private const string PlayFileLink = $"<a href={HostInvite.PlayFilePath} download>Download the Play file</a>";

    /// <summary>
    /// Settings and its pages in the full home Platform; <paramref name="games"/> counts the matches a restart or an uninstall would end.
    /// Batch 656: <see cref="RegisterPath"/> saves the switch of <paramref name="signUp"/> (people register themselves). Batch 680:
    /// Cheat commands lists the logins of <paramref name="accounts"/> (none for a page-only Settings without a store). Batch 681: Game files
    /// holds the switch of <paramref name="share"/> (the host shares the game with players; none for a page-only Settings). Batch 684:
    /// <paramref name="gamesChanged"/> hears a saved Games at the same time.
    /// </summary>
    public static void Map(WebApplication app, HostLoop loop, HostSteps steps, Func<int> games, HostSelfRegistration signUp,
        PlatformAccounts? accounts = null, HostFileShare? share = null, Action<int>? gamesChanged = null)
    {
        steps.MapSettings(app);
        HostFolders.MapSettings(app, loop, games);
        PlatformAccount[] Logins() => [.. (accounts?.ListAccounts() ?? []).OrderBy(account => account.Username, StringComparer.Ordinal)];
        IResult Page(HttpContext context, IAntiforgery antiforgery, string saved, string? problem = null, string? typed = null) =>
            HostMode.Page(Render(loop, steps, signUp, share, accounts?.Controls, Logins(), Program.RegistrationToken(context, antiforgery), saved, problem, typed),
                problem is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        if (share is not null)
            app.MapPost(SharePath, async (HttpContext context, IAntiforgery antiforgery) =>
            {
                var form = await HostSteps.FormAsync(context, antiforgery);
                if (form is null) return Page(context, antiforgery, "", HostSteps.Expired);
                share.Set(form["on"] == "on");
                return Results.Redirect(PagePath + "?saved=share");
            });
        HostGamesSetting.Map(app, loop, (context, antiforgery, problem, typed) => Page(context, antiforgery, "", problem, typed), gamesChanged);
        app.MapGet(PagePath, (HttpContext context, IAntiforgery antiforgery) => Page(context, antiforgery, context.Request.Query["saved"].ToString()));
        app.MapPost(RegisterPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null) return Page(context, antiforgery, "", HostSteps.Expired);
            signUp.Set(form["on"] == "on");
            return Results.Redirect(PagePath + "?saved=register");
        });
        app.MapPost(CheatsPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null) return Page(context, antiforgery, "", HostSteps.Expired);
            // One field per ticked box; only ids of existing logins are kept.
            var logins = Logins().Select(account => account.Id).ToHashSet();
            long[] ticked = [.. form["cheat"].Select(value => long.TryParse(value, out var id) ? id : 0).Where(logins.Contains).Distinct().Order()];
            // Batch 657's one way to save: a failed read writes nothing, and the page says so. The list replaces batch 666's switch.
            HostMode.Change(loop.Paths.Settings, saved => saved with { CheatAccounts = ticked, AllowCheats = false });
            return Results.Redirect(PagePath + "?saved=cheats");
        });
        // NOT RETAIL (batch 751): the switch saves first and then sets the live value, so a failed save (an unreadable platform.json throws) changes nothing live.
        app.MapPost(PaidContentPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null) return Page(context, antiforgery, "", HostSteps.Expired);
            var off = form["on"] != "on";
            HostMode.Change(loop.Paths.Settings, saved => saved with { PaidContentOff = off });
            if (accounts is not null) accounts.Controls.PaidContentOff = off;
            return Results.Redirect(PagePath + "?saved=paid");
        });
        // NOT RETAIL (batch 751): one field per ticked box; the saved list is the playable ids that are not ticked, so an id that is not playable is ignored.
        app.MapPost(ChampionsPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null) return Page(context, antiforgery, "", HostSteps.Expired);
            // Without the roster, a save would wipe the stored list (every playable id would look unticked): nothing is saved.
            if (Roster() is not { } roster) return Page(context, antiforgery, "", ChampionsUnreadable);
            var ticked = form["champion"].Select(value => int.TryParse(value, out var id) ? (int?)id : null).OfType<int>().ToHashSet();
            int[] disabled = [.. roster.Keys.Where(id => !ticked.Contains(id))];
            HostMode.Change(loop.Paths.Settings, saved => saved with { DisabledChampions = disabled });
            if (accounts is not null) accounts.Controls.DisabledChampions = disabled.ToHashSet();
            return Results.Redirect(PagePath + "?saved=champions");
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

    /// <summary>What the Champions section says while Amberline cannot read the AIR client's files (no AIR folder yet, or unreadable).</summary>
    private const string ChampionsUnreadable = "The champion list appears once Amberline can read the AIR client's files.";

    /// <summary>
    /// DIAGNOSTICS (batch 751 fix; a test seam): the roster Settings lists, which a check replaces to show a Settings page whose files cannot be read.
    /// The real roster is <see cref="AirGameDtos.ChampionNames"/>, read from the AIR client's data.
    /// </summary>
    public static Func<IReadOnlyDictionary<int, string>> ChampionRoster { get; set; } = () => AirGameDtos.ChampionNames;

    private static int _rosterLogged;

    /// <summary>
    /// The roster, or null while the AIR client's data cannot be read (missing, locked or malformed: batch 751 final review, any non-fatal
    /// error): Settings still renders, and a Champions save changes nothing. The first failure writes one console line, its type only.
    /// </summary>
    private static IReadOnlyDictionary<int, string>? Roster()
    {
        try { return ChampionRoster(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            if (Interlocked.Exchange(ref _rosterLogged, 1) == 0) Console.Error.WriteLine($"Amberline could not read the AIR client's champion list: {ex.GetType().Name}");
            return null;
        }
    }

    private static UninstallPlan? Plan(HostLoop loop) =>
        HostUninstall.Plan(loop.Paths, HostMode.ReadOrDefault(loop.Paths.Settings), loop.Uninstall.LocalAppData);

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>The page; a <paramref name="typed"/> value makes <paramref name="problem"/> the Games field's (<see cref="HostGamesSetting"/>).</summary>
    private static string Render(HostLoop loop, HostSteps steps, HostSelfRegistration signUp, HostFileShare? share, PlatformControls? controls, PlatformAccount[] logins,
        string token, string saved, string? problem, string? typed = null)
    {
        var settings = HostMode.ReadOrDefault(loop.Paths.Settings);
        var cheaters = settings.CheatLogins();
        var cheats = logins.Length == 0 ? "<p>Nobody has a login yet.</p>" : $"""
            <form method=post action={CheatsPath}>{token}
            {string.Concat(logins.Select(login => $"<label class=choice><input type=checkbox name=cheat value={login.Id}{(cheaters.Contains(login.Id) ? " checked" : "")}>" +
                $"<span><b>{Encode(login.Username)}</b> · {Encode(login.Summoner)}{(login.Id == settings.HostAccount ? " (you)" : "")}</span></label>\n"))}<div class=buttons><button type=submit>Save</button></div></form>
            """;
        var notice = problem is not null ? HostPages.Errors((problem, typed is null ? null : "games")) : saved switch
        {
            "name" => $"<p class=notice role=status>Saved. Send your users the new Play file to show the new name. {PlayFileLink}</p>",
            "address" => $"<p class=notice role=status>Saved. Send your users the new Play file: their old one looks at the old address. {PlayFileLink}</p>",
            "folders" => "<p class=notice role=status>Saved. Amberline restarted to use the new folders.</p>",
            "register" or "share" or "paid" or "champions" => "<p class=notice role=status>Saved.</p>",
            "cheats" => "<p class=notice role=status>Saved. It counts for games that start from now on.</p>",
            HostGamesSetting.Saved => $"<p class=notice role=status>{HostGamesSetting.SavedNotice}</p>",
            _ => "",
        };
        var address = string.IsNullOrWhiteSpace(steps.PublicHost) ? "Not chosen yet."
            : $"<b>{Encode(steps.PublicHost)}</b> ({HostSteps.Source(settings.AddressSource)})";
        var updates = HostUpdates.Read(loop.Paths.Data).Check == true ? "On: Amberline asks GitHub once a day." : "Off.";
        var files = share is null ? "" : $"""
            <section class=folder aria-labelledby=share-title><h2 id=share-title>Game files</h2>
            <p>{HostFileShare.Notice}</p>
            <form method=post action={SharePath}>{token}
            <label class=choice><input type=checkbox name=on value=on{(share.On ? " checked" : "")}>Share the game files with players</label>
            <div class=buttons><button type=submit>Save</button></div></form>
            {share.Status()}</section>
            """;
        // NOT RETAIL (batch 751): the Skins and RP switch, and the Champions list (only with the store, whose live controls it sets).
        var paid = $"""
            <section class=folder aria-labelledby=paid-title><h2 id=paid-title>Skins and RP</h2>
            <form method=post action={PaidContentPath}>{token}
            <label class=choice><input type=checkbox name=on value=on{(settings.PaidContentOff ? "" : " checked")}>Offer skins and RP</label>
            <p class=tip>Off: everyone plays the default looks, and the store shows no skins and no RP. Nothing anyone owns is removed.</p>
            <div class=buttons><button type=submit>Save</button></div></form></section>
            """;
        var roster = controls is null ? null : Roster();
        var champions = controls is null ? "" : roster is null
            ? $"""<section class=folder aria-labelledby=champions-title><h2 id=champions-title>Champions</h2><p class=tip>{ChampionsUnreadable}</p></section>"""
            : $"""
            <section class=folder aria-labelledby=champions-title><h2 id=champions-title>Champions</h2>
            <p class=tip>Tick the champions players may pick. Tutorials keep their own champions.</p>
            <form method=post action={ChampionsPath}>{token}
            <p class=table-search data-needs-script hidden><label for=champion-search>Search champions</label><input type=search id=champion-search autocomplete=off></p>
            <div id=champions class=champion-list>{string.Concat(roster.Select(champion =>
                $"<label class=choice><input type=checkbox name=champion value={champion.Key}{(controls!.IsEnabled(champion.Key) ? " checked" : "")}>{Encode(champion.Value)}{(PlatformGames.KeptByBattleTraining(champion.Key) ? " (Battle Training keeps it)" : "")}</label>"))}</div>
            <p id=champions-none class=empty hidden>No champions match your search.</p>
            <div class=buttons><button type=submit>Save</button></div></form></section>
            """;
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
            {{HostGamesSetting.Section(settings, token, typed is null ? null : problem, typed)}}
            <section class=folder aria-labelledby=updates-title><h2 id=updates-title>Updates</h2><p>{{updates}}</p>
            <p><a href=/host/updates>Change the update check</a></p></section>
            <section class=folder aria-labelledby=play-title><h2 id=play-title>Play file</h2>
            <p>{{PlayFileLink}}</p>
            <p><a href="{{HostAttention.CertificatePath}}?from=settings">Make new Play files</a>: every user's old Play file stops working.</p></section>
            <section class=folder aria-labelledby=register-title><h2 id=register-title>Sign-ups</h2>
            <form method=post action={{RegisterPath}}>{{token}}
            <label class=choice><input type=checkbox name=on value=on{{(signUp.On ? " checked" : "")}}>Let people register themselves</label>
            <div class=buttons><button type=submit>Save</button></div></form>
            {{signUp.Link(steps.PublicHost)}}</section>
            {{paid}}
            {{champions}}
            {{files}}
            <section class=folder aria-labelledby=cheats-title><h2 id=cheats-title>Cheat commands</h2>
            <p>A ticked login can use them in every game it plays here. Only you can change this list.</p>
            {{cheats}}</section>
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
                    "Users' logins and Play files stop working for good.") : "");
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
