using System.Globalization;
using System.Net;
using System.Text.Json;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// Loopback-only operator page; remote guests never receive these routes.
/// NOT RETAIL: Riot ran its own account tools, Tribunal and payment backend. This page lets the host grant testing RP,
/// issue reward codes, set a new password for an account, read player reports, clear leaver penalties, download the friend launcher and see who is
/// online (the "Online now" table); none of it is visible to the client protocol.
/// </summary>
public static class PlatformManagement
{
    public static bool IsLocal(HttpContext context, int port) =>
        context.Connection.LocalPort == port && context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
        context.Request.Host.Host is "127.0.0.1" or "localhost" or "[::1]";

    /// <summary>One "Online now" row. Plain text: the page encodes every value, whatever a player typed into it.</summary>
    public sealed record OnlineRow(string Username, string Summoner, string Address, string Doing, string Since, string Chat);

    // A store page request is all the platform sees of the store; the frame closing is invisible. Past this age the player counts as on Home.
    private static readonly TimeSpan StoreRecent = TimeSpan.FromMinutes(3);

    /// <summary>
    /// NOT RETAIL: operator tooling (retail had no such view). Joins what the platform already holds per account: the RTMP session
    /// (login time, peer address, a session parked for a reconnect, the store frame), the lobby and game registry
    /// (<see cref="PlatformGames.Activities"/>) and the XMPP presence. It observes only; a player who is in none of the activity
    /// registries and has loaded no store page lately is on Home or in some other client view, and the row says "Home".
    /// </summary>
    public static OnlineRow[] Online(PlatformAccounts accounts, PlatformSessions sessions, PlatformGames games, AirXmppHost chat, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var activities = games.Activities();
        string Since(DateTimeOffset at) => $"{at.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC ({Age(now - at)})";
        return sessions.Online().Select(session =>
        {
            if (accounts.Find(session.AccountId) is not { } account) return null;
            var (doing, since) = activities.TryGetValue(account.Id, out var activity) ? activity
                : session.Store is { } store && now - store.Seen < StoreRecent ? ($"In the store, last page {Age(now - store.Seen)} ago", store.Opened)
                : ("Home, logged in", session.Since);
            // Home has no start the platform sees (it never learns when a player left a lobby or the store): its time is the login.
            var sinceText = doing == "Home, logged in" ? "logged in " + Since(since) : Since(since);
            if (session.Reconnecting)
                (doing, sinceText) = ("Reconnecting, connection lost" + (activities.ContainsKey(account.Id) ? $" (still: {doing})" : ""), Since(session.Since));
            var address = session.Peer is null ? "unknown" : IPAddress.IsLoopback(session.Peer) ? "this PC" : session.Peer.ToString();
            return new OnlineRow(account.Username, account.Summoner, address, doing, sinceText, chat.ChatState(account.Id) ?? "not connected");
        }).OfType<OnlineRow>().OrderBy(row => row.Summoner, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Age(TimeSpan age) => age < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)age.TotalSeconds)} s"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min" : $"{(int)age.TotalHours} h {age.Minutes} min";

    private static string RenderOnline(OnlineRow[] rows, bool live, bool home)
    {
        static string Cell(string text) => $"<td>{WebUtility.HtmlEncode(text)}</td>";
        var body = string.Concat(rows.Select(r => $"<tr>{Cell(r.Username)}{Cell(r.Summoner)}{Cell(r.Address)}{Cell(r.Doing)}{Cell(r.Since)}{Cell(r.Chat)}</tr>"));
        // Batch 646: the home host's Advanced never reloads. page.js refills this table from OnlineJsonPath while it is data-live and says
        // the count in the live line (the same words as here), so nothing typed in the forms below is lost and the focus stays put.
        if (home)
            return "<h2 id=online-heading>Online now</h2><p>" + (live
                    ? "It updates every 5 seconds. <a href=\"/manage?refresh=off\">Pause updates</a>"
                    : "Updates are paused. <a href=\"/manage\">Resume updates</a>") + "</p>" +
                $"<p id=online-count aria-live=polite>{OnlineCount(rows.Length)}</p>" +
                Wide("online-heading", $"<table id=online-table{(live ? " data-live" : "")}{(rows.Length == 0 ? " hidden" : "")}><thead><tr><th scope=col>Username</th>" +
                    "<th scope=col>Summoner name</th><th scope=col>Address</th><th scope=col>Doing</th><th scope=col>Since</th><th scope=col>Chat</th></tr></thead>" +
                    $"<tbody id=online-rows>{body}</tbody></table>");
        var table = rows.Length == 0 ? "<p>Nobody is logged in.</p>"
            : "<table><thead><tr><th>Username</th><th>Summoner</th><th>Address</th><th>Doing</th><th>Since</th><th>Chat</th></tr></thead><tbody>" +
              body + "</tbody></table>";
        var toggle = live ? "This table refreshes every 5 seconds. <a href=\"/manage?refresh=off\">Pause</a> it to use the forms below."
            : "Refresh is paused. <a href=\"/manage\">Resume</a> it.";
        return $"<h2>Online now ({rows.Length})</h2><p>{toggle}</p>{table}";
    }

    /// <summary>
    /// Batch 648, seen at 400 px: a table of the home host's Advanced (five to seven columns) is wider than a phone, so it scrolls in its
    /// own region, named by its section's heading and reachable by keyboard, and the page never scrolls sideways.
    /// </summary>
    private static string Wide(string heading, string table) => $"<div class=wide role=region tabindex=0 aria-labelledby={heading}>{table}</div>";

    /// <summary>The home host's Advanced: the "Online now" rows as JSON, which <c>page.js</c> reads every 5 seconds.</summary>
    public const string OnlineJsonPath = "/manage/online.json";

    /// <summary>The live line's words; <c>page.js</c> says the same.</summary>
    private static string OnlineCount(int count) =>
        count == 0 ? "Nobody is logged in." : count == 1 ? "1 person is logged in." : $"{count} people are logged in.";

    private const int ShownReports = 100;

    /// <summary>
    /// NOT RETAIL: the host's review of player reports (retail's was the Tribunal). Newest first, the summoner names as they are
    /// now. The comment is a player's untrusted text: HTML-encoded, its line breaks the only markup added.
    /// </summary>
    private static string RenderReports(PlatformAccounts accounts, string field, bool home = false)
    {
        static string Cell(string text) => $"<td>{WebUtility.HtmlEncode(text)}</td>";
        string Name(PlatformAccount? account, long summonerId) => account?.Summoner ?? $"summoner {summonerId}";
        var reports = accounts.ListReports();
        var open = reports.Count(report => report.Outcome is null);
        var intro = $"<h2{(home ? " id=reports-heading" : "")}>Player reports ({open} open)</h2><p>" +
            (home ? "From the Report button at the end of a game." : "Sent with the end-of-game Report button.");
        if (reports.Length == 0) return intro + (home ? " Nobody has been reported yet.</p>" : " No player has been reported.</p>");
        // Batch 648: the home host's buttons name their report for a screen reader, as Friends' buttons name their friend.
        string Button(string outcome, string label, string about) => $"<button name=outcome value={outcome}>{label}" +
            (home ? $"<span class=vh> the report about {WebUtility.HtmlEncode(about)}</span>" : "") + "</button>";
        var rows = reports.Reverse().Take(ShownReports).Select(report =>
        {
            var reported = Name(accounts.FindSummoner(report.ReportedSummonerId), report.ReportedSummonerId);
            return "<tr>" + Cell(report.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) +
                Cell(Name(accounts.Find(report.ReporterAccountId), report.ReporterSummonerId)) + Cell(reported) +
                Cell(report.Offense) + Cell(report.GameId.ToString(CultureInfo.InvariantCulture)) +
                $"<td>{WebUtility.HtmlEncode(report.Comment).Replace("\n", "<br>", StringComparison.Ordinal)}</td>" +
                (report.Outcome is { } outcome ? Cell(outcome)
                    : $"<td>Open<form method=post action=/manage/report>{field}<input type=hidden name=id value=\"{report.Id:D}\">" +
                      Button("Reviewed", "Reviewed", reported) + Button("Dismissed", "Dismiss", reported) + "</form></td>") +
                "</tr>";
        });
        var counted = intro + (home ? $" {reports.Length} are kept" : $" {reports.Length} kept") +
            (reports.Length > ShownReports ? home ? $"; the {ShownReports} newest are shown." : $", the {ShownReports} newest shown." : ".") + "</p>";
        return home
            ? counted + Wide("reports-heading", "<table><thead><tr><th scope=col>Time</th><th scope=col>Reported by</th><th scope=col>Reported</th><th scope=col>Offense</th>" +
                "<th scope=col>Game</th><th scope=col>Description</th><th scope=col>Status</th></tr></thead><tbody>" + string.Concat(rows) + "</tbody></table>")
            : counted + "<table><thead><tr><th>Time</th><th>Reporter</th><th>Reported</th><th>Offense</th><th>Game</th><th>Description</th><th>Status</th></tr></thead><tbody>" +
              string.Concat(rows) + "</tbody></table>";
    }

    /// <summary>
    /// NOT RETAIL: the host's view and override of LeaverBuster (batch 587; retail's was Riot's player support). Each account's
    /// leaves in its recent matchmade games, the warning level its next login shows and the queue wait it serves, with a Clear button.
    /// </summary>
    private static string RenderLeavers(PlatformAccounts accounts, string field, bool home = false)
    {
        static string Cell(string text) => $"<td>{WebUtility.HtmlEncode(text)}</td>";
        var rows = accounts.ListAccounts().OrderBy(a => a.Summoner, StringComparer.OrdinalIgnoreCase).Select(account =>
        {
            var status = accounts.LeaverStatus(account.Id);
            var wait = status.QueueDelay > TimeSpan.Zero
                ? $"{(int)status.QueueDelay.TotalMinutes} min for {status.DelayedGamesLeft} more game{(status.DelayedGamesLeft == 1 ? "" : "s")}" : "none";
            // Batch 589: the champion-select dodge timer.
            var dodge = accounts.DodgeStatus(account.Id);
            var dodged = dodge.Dodges == 0 ? "none" : $"{dodge.Dodges} in 24 h, " + (dodge.Remaining > TimeSpan.Zero
                ? $"{(int)dodge.Remaining.TotalMinutes}:{dodge.Remaining.Seconds:00} left" : "timer over");
            // Batch 648: the home host's Clear names its player for a screen reader.
            var clear = status.Leaves == 0 && dodge.Dodges == 0 ? "<td></td>"
                : $"<td><form method=post action=/manage/leavers/clear>{field}<input type=hidden name=accountId value=\"{account.Id}\"><button>Clear" +
                  (home ? $"<span class=vh> for {WebUtility.HtmlEncode(account.Summoner)}</span>" : "") + "</button></form></td>";
            return "<tr>" + Cell(account.Summoner) + Cell($"{status.Leaves} of {status.Games}" + (home ? " games" : "")) +
                Cell(status.Level == 0 ? "none" : $"level {status.Level}") + Cell(wait) + Cell(dodged) + clear + "</tr>";
        });
        if (home)
            return "<h2 id=leavers-heading>Leavers</h2><p>Leaves (games left or played AFK) in each player's last " +
                $"{PlatformAccounts.LeaverWindowGames} matchmade games. One brings a warning, two a queue wait. A dodge locks the queues for " +
                "6 minutes (30 for a second one in 24 hours). Clear forgives them.</p>" +
                Wide("leavers-heading", "<table><thead><tr><th scope=col>Summoner name</th><th scope=col>Leaves</th><th scope=col>Warning</th><th scope=col>Queue wait</th>" +
                    "<th scope=col>Dodges</th><th scope=col>Change</th></tr></thead><tbody>" + string.Concat(rows) + "</tbody></table>");
        return "<h2>Leavers</h2><p>Leaves (left and did not come back, or AFK) in each account's last " +
            $"{PlatformAccounts.LeaverWindowGames} matchmade games; custom games never count. From one leave the client shows its leaver " +
            "warning at each login; from two the player waits before being matched (5, 10 or 15 minutes, and the whole party with them) " +
            $"until {PlatformAccounts.LowPriorityGames} games are completed. A dodge (leaving a queue game's champion selection) keeps the " +
            "player and any party with them out of every queue for 6 minutes, 30 for a repeat within 24 hours. " +
            "Clear forgives the account's leaves and dodges so far.</p>" +
            "<table><thead><tr><th>Summoner</th><th>Leaves</th><th>Warning</th><th>Queue wait</th><th>Dodges</th><th></th></tr></thead><tbody>" +
            string.Concat(rows) + "</tbody></table>";
    }

    /// <summary>The ticker severities the stock client draws (AnimatedAlertTicker: info, warning and error backgrounds).</summary>
    public static readonly string[] BroadcastSeverities = ["INFO", "WARN", "ERROR"];

    /// <summary>A ticker text the operator may send: up to 200 characters without control characters; blank means clear.</summary>
    public static string? BroadcastText(string? message)
    {
        var text = message?.Trim() ?? "";
        if (text.Length > 200 || text.Any(char.IsControl)) throw new ArgumentException("A player notice is at most 200 characters on one line.");
        return text.Length == 0 ? null : text;
    }

    /// <param name="broadcast">
    /// Sends the stock ticker message (text, or null to clear, and its severity) to every connected client and returns how many
    /// it reached. NOT RETAIL: the operator's form and script endpoint are host tooling; the message is the client's own
    /// <c>BroadcastNotification</c> (see <see cref="AirDtos.BroadcastNotification"/>).
    /// </param>
    /// <param name="invite">
    /// The home host's Invite a friend page (<see cref="HostInvite"/>): the page becomes the dashboard's Advanced, and its friends
    /// part points there instead of the friend launcher and local registration. Null (the operator's start) changes nothing.
    /// </param>
    public static void Map(WebApplication app, PlatformAccounts accounts, int localPort, int remotePort,
        Action<long, object>? notify = null, string? publicRegistrationUrl = null, Func<OnlineRow[]>? online = null,
        AirRemoteAccess? remoteAccess = null, Func<string?, string, int>? broadcast = null, PlatformSessions? sessions = null,
        string? invite = null)
    {
        var gate = new object();
        (string Text, string Severity, int Reached)? notice = null;
        int Notify(string? message, string? severity)
        {
            var text = BroadcastText(message);
            var level = string.IsNullOrEmpty(severity) ? "WARN" : severity;
            if (!BroadcastSeverities.Contains(level)) throw new ArgumentException("Choose INFO, WARN or ERROR.");
            lock (gate)
            {
                var reached = broadcast!(text, level);
                notice = text is null ? null : (text, level, reached);
                return reached;
            }
        }
        // NOT RETAIL (batch 648): the home host's Advanced, shelled like the other host pages (HostPages), in their words (batch 638's
        // word list), each field with its own id, label and hint, the forms novalidate: a refused form answers with this page and its error
        // summary (Answer below), where the operator's page answers with JSON. page.js updates its Online now table in place (batch 646).
        string HomePage(HttpContext context, IAntiforgery antiforgery, Shown? shown)
        {
            var token = antiforgery.GetAndStoreTokens(context);
            var field = $"<input type=hidden name=\"{WebUtility.HtmlEncode(token.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(token.RequestToken)}\">";
            string? At(string id) => shown?.Field == id ? shown.Text : null;
            string? Typed(string form, string name) => shown?.Form == form && shown.Values is { } values ? values[name].ToString() : null;
            string Value(string form, string name, string fallback) => $" value=\"{WebUtility.HtmlEncode(Typed(form, name) ?? fallback)}\"";
            string Logins(string form, string id)
            {
                var chosen = Typed(form, "accountId");
                var options = string.Concat(accounts.ListAccounts().Select(a =>
                    $"<option value=\"{a.Id}\"{(chosen == a.Id.ToString(CultureInfo.InvariantCulture) ? " selected" : "")}>" +
                    $"{WebUtility.HtmlEncode(a.Summoner)} · {PlatformEconomy.For(a).Rp} RP</option>"));
                return $"<label for={id}>Login <span class=req>(required)</span></label>{HostPages.FieldError(id, At(id))}" +
                    $"<select id={id} name=accountId required{HostPages.Described(id, false, At(id))}>{options}</select>";
            }
            string Number(string form, string id, string name, string label, string hint, int min, string fallback) =>
                HostPages.Field(id, label, hint, At(id), $"type=number min={min} max=1000000 required{Value(form, name, fallback)}", name: name);
            static string Send(string label) => $"<div class=actions><button>{label}</button></div></form>";
            var done = context.Request.Query["done"].ToString() switch
            {
                "grant" => "The RP is given.",
                "logout" => "Done. The next time their PC connects, they log in again.",
                "password" => "Saved. Send the new password to the player privately.",
                "code" => "The reward code is cancelled.",
                "leaver" => "Cleared: the player's leaves and dodges so far are forgiven.",
                "report" => "The report is closed.",
                _ => null,
            };
            var current = notice;
            var noticeForm = broadcast is null ? "" :
                "<h2>Send a notice to players</h2><p>It shows on every game client until you clear it or Amberline restarts.</p>" +
                (current is { } now ? $"<p>Showing now ({Kind(now.Severity)}, sent to {now.Reached} {(now.Reached == 1 ? "player" : "players")}): " +
                    $"{WebUtility.HtmlEncode(now.Text)}</p>" : "<p>No notice is showing.</p>") +
                $"<form method=post action=/manage/broadcast novalidate>{field}" +
                HostPages.Field("notice-message", "Message", "Up to 200 characters. Send it empty to clear it.", At("notice-message"),
                    "maxlength=200 autocomplete=off" + Value("notice", "message", ""), required: false, name: "message") +
                $"<label for=notice-kind>Kind of notice <span class=req>(required)</span></label>{HostPages.FieldError("notice-kind", At("notice-kind"))}" +
                $"<select id=notice-kind name=severity{HostPages.Described("notice-kind", false, At("notice-kind"))}>" +
                string.Concat(BroadcastSeverities.Select(severity => $"<option value={severity}" +
                    $"{(severity == (Typed("notice", "severity") ?? "WARN") ? " selected" : "")}>{Kind(severity)}</option>")) + "</select>" +
                Send("Send to players");
            var logout = remoteAccess is null ? "" :
                "<h2>Make a friend log in again</h2><p>They log in again the next time their PC connects.</p>" +
                $"<form method=post action=/manage/remote-revoke novalidate>{field}{Logins("logout", "logout-login")}" +
                Send("Make them log in again");
            var codes = accounts.ListRewardCodes().OrderByDescending(code => code.CreatedAt).ToArray();
            string CodeRow(PlatformRewardCode code)
            {
                var made = code.CreatedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
                var usedBy = code.RedeemedBy is long id ? accounts.Find(id)?.Summoner ?? "a removed login" : "";
                var cancel = !code.Revoked && code.RedeemedAt is null
                    ? $"<form method=post action=/manage/reward-code/revoke>{field}<input type=hidden name=id value=\"{code.Id:D}\">" +
                      $"<button>Cancel code<span class=vh> made {made}</span></button></form>" : "";
                return $"<tr><td>{(code.Revoked ? "Cancelled" : code.RedeemedAt is not null ? "Used" : "Not used yet")}</td><td class=words>{made}</td>" +
                    $"<td>{code.Ip.ToString("N0", CultureInfo.InvariantCulture)} IP, {code.Rp.ToString("N0", CultureInfo.InvariantCulture)} RP</td>" +
                    $"<td>{WebUtility.HtmlEncode(usedBy)}</td><td>{cancel}</td></tr>";
            }
            var body = $$"""
                <h1>Advanced</h1>
                <p>More tools for your server.</p>
                {{(done is null ? "" : $"<p class=notice role=status>{done}</p>")}}
                {{(shown is null ? "" : HostPages.Errors((shown.Text, shown.Field)))}}
                {{(online is null ? "" : RenderOnline(online(), context.Request.Query["refresh"] != "off", home: true))}}
                {{noticeForm}}
                <h2>Give RP</h2><p>Free RP for the store.</p>
                <form method=post action=/manage/grant novalidate>{{field}}<input type=hidden name=requestId value="{{Guid.NewGuid()}}">
                {{Logins("grant", "grant-login")}}{{Number("grant", "grant-rp", "rp", "RP amount", "1 to 1,000,000.", 1, "1000")}}
                {{Send("Give RP")}}
                {{logout}}
                <h2>Set a new password</h2><p>For any login, yours too. The player is logged out at once.</p>
                <form method=post action=/manage/password novalidate>{{field}}{{Logins("password", "password-login")}}
                {{HostPages.Field("password-new", "New password", "At least 10 characters.", At("password-new"), "type=password required minlength=10 maxlength=128 autocomplete=new-password", name: "password")}}
                {{HostPages.Field("password-confirm", "Repeat the new password", null, At("password-confirm"), "type=password required minlength=10 maxlength=128 autocomplete=new-password", name: "confirm")}}
                {{Send("Save")}}
                <h2>Make a reward code</h2><p>A one-time code for Influence Points (IP) and RP, typed in the store's Redeem a code page.</p>
                <form method=post action=/manage/reward-code/create novalidate{{HostPages.Wait("Making the reward code.")}}>{{field}}<input type=hidden name=requestId value="{{Guid.NewGuid()}}">
                {{Number("code", "code-ip", "ip", "IP amount", "0 to 1,000,000.", 0, "0")}}{{Number("code", "code-rp", "rp", "RP amount", "0 to 1,000,000; IP or RP above 0.", 0, "1000")}}
                {{Send("Make a reward code")}}
                <h2 id=codes-heading>Reward codes</h2>{{(codes.Length == 0 ? "<p>You have not made a reward code yet.</p>"
                    : Wide("codes-heading", "<table><thead><tr><th scope=col>Status</th><th scope=col>Made</th><th scope=col>Amount</th><th scope=col>Used by</th><th scope=col>Change</th></tr></thead>" +
                      $"<tbody>{string.Concat(codes.Select(CodeRow))}</tbody></table>"))}}
                {{RenderLeavers(accounts, field, home: true)}}
                {{RenderReports(accounts, field, home: true)}}
                """;
            return body;
        }
        // A refused form: the home host's Advanced again, with the problem in its summary and beside its field, and what was typed (never
        // a password); the operator's page lets the exception reach Program's middleware, which answers with JSON as before.
        async Task<IResult> Answer(HttpContext context, IAntiforgery antiforgery, string name, Func<IFormCollection, IResult> act)
        {
            if (!IsLocal(context, localPort)) return Results.NotFound();
            IFormCollection? form = null;
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                form = await context.Request.ReadFormAsync(context.RequestAborted);
                return act(form);
            }
            catch (Exception ex) when (invite is not null &&
                ex is ArgumentException or AntiforgeryValidationException or (InvalidOperationException and not ObjectDisposedException))
            {
                var (text, at) = HomeProblem(ex, name);
                return HostMode.Page(HomePage(context, antiforgery, new Shown(name, form, text, at)), StatusCodes.Status400BadRequest);
            }
        }
        app.MapGet("/manage", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!IsLocal(context, localPort)) return Results.NotFound();
            if (invite is not null) return HostMode.Page(HomePage(context, antiforgery, null));
            var token = antiforgery.GetAndStoreTokens(context);
            var field = $"<input type=hidden name=\"{WebUtility.HtmlEncode(token.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(token.RequestToken)}\">";
            var accountOptions = string.Join("", accounts.ListAccounts().Select(a =>
                $"<option value=\"{a.Id}\">{WebUtility.HtmlEncode(a.Summoner)} · {PlatformEconomy.For(a).Rp} RP</option>"));
            var revokeForm = remoteAccess is null ? "" :
                $"<h2>Revoke remote access</h2><p>Ends the account's friend sign-in token. Tunnels already open end when the companion reconnects.</p>" +
                $"<form method=post action=/manage/remote-revoke>{field}<label>Account<select name=accountId required>{accountOptions}</select></label><button>Revoke remote access</button></form>";
            // NOT RETAIL: host tool for a friend who forgot the password (retail's was Riot's account website).
            var passwordForm = "<h2>Set a new password</h2><p>The old password stops working at once: the account's remote-access token is " +
                "revoked and its sessions end, so the player logs in again with the new one. Send it to them privately.</p>" +
                (context.Request.Query["password"] == "changed" ? "<p role=status>The password was changed.</p>" : "") +
                $"<form method=post action=/manage/password>{field}<label>Account<select name=accountId required>{accountOptions}</select></label>" +
                "<label>New password<input type=password name=password required minlength=10 maxlength=128 autocomplete=new-password></label>" +
                "<label>Repeat the new password<input type=password name=confirm required minlength=10 maxlength=128 autocomplete=new-password></label>" +
                "<button>Set new password</button></form>";
            var rewardCodes = accounts.ListRewardCodes().OrderByDescending(code => code.CreatedAt)
                .Select(code => RenderRewardCodeRow(code, field)).ToArray();
            var rewardCodeRows = rewardCodes.Length == 0
                ? "<p>No reward codes have been issued.</p>"
                : $"<table><thead><tr><th>Status</th><th>Created (UTC)</th><th>Amount</th><th>Redeemer</th><th></th></tr></thead><tbody>{string.Join("", rewardCodes)}</tbody></table>";
            var registration = publicRegistrationUrl is { Length: > 0 } url
                ? $"<p>Share <a href=\"{WebUtility.HtmlEncode(url)}\"><strong>{WebUtility.HtmlEncode(url)}</strong></a> for account registration. No invitation is needed.</p>"
                : "<p>Public registration is off. Create each friend's account with <a href=\"/register\">local registration</a> on this host, then send that friend the username and password privately.</p>";
            var current = notice;
            var noticeForm = broadcast is null ? "" :
                "<h2>Notify players</h2><p>Shows a line in every connected client's ticker (and in later logins) until you clear it or the platform restarts. Send an empty message to clear it.</p>" +
                (current is { } shown ? $"<p>Showing now ({WebUtility.HtmlEncode(shown.Severity)}, reached {shown.Reached} clients): {WebUtility.HtmlEncode(shown.Text)}</p>" : "<p>No notice is showing.</p>") +
                $"<form method=post action=/manage/broadcast>{field}<label>Message<input name=message maxlength=200></label>" +
                "<label>Severity<select name=severity><option>INFO</option><option selected>WARN</option><option>ERROR</option></select></label><button>Send to players</button></form>";
            var live = online is not null && context.Request.Query["refresh"] != "off";
            var heading = "<h1>Game testing</h1><p>Use the desktop shortcut for solo or multiplayer custom games. Your account and match results are saved.</p>";
            var friends = $$"""
                <h2>Play with friends</h2><p><a href="/bootstrap/Play.cmd">Download Play.cmd</a> and send that single file to your friends. They select their existing patch-126 AIR client and game on first launch.</p>
                {{registration}}<p>The launcher opens only the client.</p>
                <p>Port 8390 is local-only and cannot be used by friends connecting to your public IP.</p>
                <p>Friends connect through HTTPS TCP {{remotePort}}; games use the configured UDP game ports. The raw AIR login and RTMP ports stay local.</p>
                <h2>Create an account</h2><p><a href="/register">Open account registration</a></p>
                """;
            var body = $$"""
                {{heading}}
                {{(online is null ? "" : RenderOnline(online(), live, home: false))}}
                {{noticeForm}}
                {{friends}}
                <h2>Grant testing RP</h2><p>No payment is required. RP grants are saved with the account.</p>
                <form method=post action=/manage/grant>{{field}}<input type=hidden name=requestId value="{{Guid.NewGuid()}}">
                <label>Account<select name=accountId required>{{accountOptions}}</select></label>
                <label>RP amount<input type=number name=rp min=1 max=1000000 value=1000 required></label><button>Grant RP</button></form>
                {{revokeForm}}
                {{passwordForm}}
                <h2>Issue one-time reward code</h2><p>Codes grant testing IP and/or RP. The raw code is shown only in the issuance response.</p>
                <form method=post action=/manage/reward-code/create>{{field}}<input type=hidden name=requestId value="{{Guid.NewGuid()}}">
                <label>IP amount<input type=number name=ip min=0 max=1000000 value=0 required></label>
                <label>RP amount<input type=number name=rp min=0 max=1000000 value=1000 required></label><button>Issue reward code</button></form>
                <h2>Issued reward codes</h2>{{rewardCodeRows}}
                {{RenderLeavers(accounts, field)}}
                {{RenderReports(accounts, field)}}
                """;
            return Results.Content($$"""
                <!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width">{{(live ? "<meta http-equiv=refresh content=5>" : "")}}
                <title>Game testing · Host</title><style>body{font:17px system-ui;background:#10202b;color:#f4eee0;max-width:900px;margin:5vh auto;padding:24px}label{display:block;margin:14px 0}input,button{font:inherit;padding:9px}input{display:block;width:95%}a{color:#ead399}li{margin:12px 0}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:4px 14px 4px 0;vertical-align:top;font-size:15px}</style>
                {{body}}
                </html>
                """, "text/html");
        });
        // NOT RETAIL (batch 646): the home host's Advanced table, read by page.js; the operator's page has no such route.
        if (invite is not null && online is not null)
            app.MapGet(OnlineJsonPath, (HttpContext context) =>
            {
                if (!IsLocal(context, localPort)) return Results.NotFound();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(online());
            });
        // The home host's page comes back with a notice of what was done (Done in HomePage); the operator's page as before.
        string Back(string operatorPath, string done) => invite is null ? operatorPath : "/manage?done=" + done;
        app.MapPost("/manage/grant", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "grant", form =>
        {
            var chosen = long.TryParse(form["accountId"], out var accountId);
            var amount = long.TryParse(form["rp"], out var rp) && rp is >= 1 and <= 1_000_000;
            if (!chosen || !amount || !Guid.TryParse(form["requestId"], out var requestId))
                throw new FormProblem("Choose an account and an RP amount from 1 to 1,000,000.",
                    !chosen ? ("Choose a login.", "grant-login") : !amount ? ("Choose an RP amount from 1 to 1,000,000.", "grant-rp") : (Expired, null));
            accounts.GrantCurrency(accountId, 0, rp, requestId);
            notify?.Invoke(accountId, AirDtos.AccountBalanceNotification(accounts.Get(accountId)));
            return Results.Redirect(Back("/manage", "grant"));
        }));
        app.MapPost("/manage/reward-code/create", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "code", form =>
        {
            var ipOk = long.TryParse(form["ip"], NumberStyles.None, CultureInfo.InvariantCulture, out var ip) && ip <= 1_000_000;
            var rpOk = long.TryParse(form["rp"], NumberStyles.None, CultureInfo.InvariantCulture, out var rp) && rp <= 1_000_000;
            if (!ipOk || !rpOk || ip == 0 && rp == 0 || !Guid.TryParse(form["requestId"], out var requestId) || requestId == Guid.Empty)
                throw new FormProblem("Enter IP and/or RP from 0 to 1,000,000 and a valid request id.",
                    !ipOk ? ("Choose an IP amount from 0 to 1,000,000.", "code-ip") : !rpOk ? ("Choose an RP amount from 0 to 1,000,000.", "code-rp")
                    : ip == 0 && rp == 0 ? ("Choose an IP or RP amount of more than 0.", "code-ip") : (Expired, null));
            PlatformIssuedRewardCode issued;
            lock (gate) issued = accounts.CreateRewardCode(ip, rp, requestId);
            context.Response.Headers.CacheControl = "no-store";
            // The home host's page copies the code with page.js's Copy (no inline handler).
            if (invite is not null)
                return HostMode.Page($"""
                    <h1>Your reward code</h1><p>It gives {ip.ToString("N0", CultureInfo.InvariantCulture)} IP and {rp.ToString("N0", CultureInfo.InvariantCulture)} RP once. Copy it now: Amberline shows it only this once.</p>
                    <p class=message id=reward-code>{WebUtility.HtmlEncode(issued.Code)}</p>
                    <p class=buttons><button type=button data-copy=reward-code>Copy</button> <span id=copy-status role=status></span></p>
                    <p><a href="/manage">Back</a></p>
                    """);
            return Results.Content(RenderRewardCodeIssued(issued), "text/html; charset=utf-8");
        }));
        app.MapPost("/manage/reward-code/revoke", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "cancel-code", form =>
        {
            if (!Guid.TryParse(form["id"], out var id) || id == Guid.Empty)
                throw new FormProblem("The reward code id is invalid.", (Expired, null));
            lock (gate) accounts.RevokeRewardCode(id);
            return Results.Redirect(Back("/manage", "code"));
        }));
        app.MapPost("/manage/password", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "password", form =>
        {
            if (!long.TryParse(form["accountId"], out var accountId) || accountId < 1) throw new FormProblem("Choose an account.", ("Choose a login.", "password-login"));
            // The password is never logged or echoed: the refusals below name no value.
            var password = form["password"].ToString();
            if (password != form["confirm"].ToString())
                throw new FormProblem("The two passwords do not match.", ("The two passwords are not the same. Type the new password twice.", "password-confirm"));
            accounts.SetPassword(accountId, password);
            remoteAccess?.Revoke(accountId);
            sessions?.EndAccount(accountId);
            return Results.Redirect(Back("/manage?refresh=off&password=changed", "password"));
        }));
        app.MapPost("/manage/report", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "report", form =>
        {
            if (!Guid.TryParse(form["id"], out var id)) throw new FormProblem("The report id is invalid.", (Expired, null));
            accounts.CloseReport(id, form["outcome"].ToString());
            return Results.Redirect(Back("/manage?refresh=off", "report"));
        }));
        app.MapPost("/manage/leavers/clear", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "leaver", form =>
        {
            if (!long.TryParse(form["accountId"], out var accountId) || accountId < 1) throw new FormProblem("Choose an account.", (Expired, null));
            accounts.ClearLeaves(accountId);
            return Results.Redirect(Back("/manage?refresh=off", "leaver"));
        }));
        if (broadcast is not null)
        {
            // The notice's own line ("Showing now" or "No notice is showing") says what was sent.
            app.MapPost("/manage/broadcast", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "notice", form =>
            {
                Notify(form["message"], form["severity"]);
                return Results.Redirect(invite is null ? "/manage?refresh=off" : "/manage");
            }));
            // For scripts (the install): loopback only, and JSON only. A browser page cannot send a cross-site JSON POST without a
            // CORS preflight this server never answers, and IsLocal refuses a rebound host name, so no antiforgery token is needed.
            app.MapPost("/manage/broadcast.json", async (HttpContext context) =>
            {
                if (!IsLocal(context, localPort)) return Results.NotFound();
                if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                BroadcastRequest? request;
                try { request = await context.Request.ReadFromJsonAsync<BroadcastRequest>(context.RequestAborted); }
                catch (JsonException) { request = null; }
                if (request is null) throw new ArgumentException("Send {\"message\": \"...\", \"severity\": \"WARN\"}.");
                return Results.Json(new { reached = Notify(request.Message, request.Severity) });
            });
        }
        if (remoteAccess is { } access)
            app.MapPost("/manage/remote-revoke", (HttpContext context, IAntiforgery antiforgery) => Answer(context, antiforgery, "logout", form =>
            {
                if (!long.TryParse(form["accountId"], out var accountId) || accountId < 1)
                    throw new FormProblem("Choose an account.", ("Choose a login.", "logout-login"));
                access.Revoke(accountId);
                return Results.Redirect(Back("/manage", "logout"));
            }));
    }

    private sealed record BroadcastRequest(string? Message, string? Severity);

    /// <summary>The home host's Advanced after a refused form: which form, what was typed, the problem and its field (null: none on the page).</summary>
    private sealed record Shown(string Form, IFormCollection? Values, string Text, string? Field);

    /// <summary>
    /// A refused form's problem: <see cref="Exception.Message"/> in the operator's words (its JSON answer, unchanged), and for the home
    /// host the same problem in the host pages' words with the field it belongs to.
    /// </summary>
    private sealed class FormProblem(string message, (string Text, string? Field) home) : ArgumentException(message)
    {
        public (string Text, string? Field) Home { get; } = home;
    }

    private const string Expired = "The page expired. Please try again.";

    /// <summary>A notice's kind as the home host reads it (the client's ticker severities).</summary>
    private static string Kind(string severity) => severity switch { "INFO" => "Information", "ERROR" => "Error", _ => "Warning" };

    /// <summary>
    /// A refused form of the home host's Advanced in the host pages' words, and its field. The account store's own texts stay as they
    /// are (the AIR client and the operator's page read them); an out-of-date page (a stale code, report or request) gets one sentence.
    /// </summary>
    private static (string Text, string? Field) HomeProblem(Exception ex, string form) => ex switch
    {
        AntiforgeryValidationException => (Expired, null),
        FormProblem problem => problem.Home,
        _ when ex.Message.StartsWith("Password must", StringComparison.Ordinal) => (HostPages.LoginProblem(ex.Message).Text, "password-new"),
        _ when ex.Message.StartsWith("Account does not exist.", StringComparison.Ordinal) =>
            ("Choose a login.", form is "grant" or "logout" or "password" ? form + "-login" : null),
        _ when ex.Message.Contains("balance limit", StringComparison.Ordinal) =>
            ("That is more than a login can hold. Choose a smaller amount.", form == "grant" ? "grant-rp" : null),
        _ when ex.Message.StartsWith("A player notice", StringComparison.Ordinal) => ("A notice has at most 200 characters, on one line.", "notice-message"),
        _ when ex.Message.StartsWith("Choose INFO", StringComparison.Ordinal) => ("Choose the kind of notice.", "notice-kind"),
        _ when ex.Message.StartsWith("A redeemed reward code", StringComparison.Ordinal) => ("That code was used already, so it cannot be cancelled.", null),
        _ => ("The page was out of date, so nothing changed. Here it is again: check it and try once more.", null),
    };

    private static string RenderRewardCodeRow(PlatformRewardCode reward, string field)
    {
        var status = reward.Revoked ? "Revoked" : reward.RedeemedAt is not null ? "Redeemed" : "Unused";
        var date = reward.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var amount = $"{reward.Ip.ToString("N0", CultureInfo.InvariantCulture)} IP / {reward.Rp.ToString("N0", CultureInfo.InvariantCulture)} RP";
        var redeemer = reward.RedeemedBy is long accountId ? accountId.ToString(CultureInfo.InvariantCulture) : "—";
        var revoke = !reward.Revoked && reward.RedeemedAt is null
            ? $"<form method=post action=/manage/reward-code/revoke>{field}<input type=hidden name=id value=\"{reward.Id:D}\"><button>Revoke</button></form>"
            : string.Empty;
        return $"<tr><td>{WebUtility.HtmlEncode(status)}</td><td>{WebUtility.HtmlEncode(date)}</td><td>{WebUtility.HtmlEncode(amount)}</td><td>{WebUtility.HtmlEncode(redeemer)}</td><td>{revoke}</td></tr>";
    }

    private static string RenderRewardCodeIssued(PlatformIssuedRewardCode issued) => $$"""
        <!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width">
        <title>Reward code issued</title><style>body{font:17px system-ui;background:#10202b;color:#f4eee0;max-width:650px;margin:5vh auto;padding:24px}input,button{font:inherit;padding:9px}input{display:block;width:95%}a{color:#ead399}</style>
        <h1>Reward code issued</h1><p>Copy this code now. It is shown once.</p>
        <label>Reward code<input readonly value="{{WebUtility.HtmlEncode(issued.Code)}}" onclick="this.select()"></label>
        <p><a href="/manage">Return to host management</a></p></html>
        """;
}
