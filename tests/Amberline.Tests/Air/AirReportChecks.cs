using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RtmpSharp.IO;
using RtmpSharp.Net;

namespace Amberline.Tests.Air;

/// <summary>Batch 572: player reports kept for the host's review on /manage. Synthetic accounts and text only.</summary>
public static class AirReportChecks
{
    private const string Version = "1.45.11_10_04_15_28";

    [Check("server.air-reports-are-stored-and-reviewable")]
    public static void ReportsAreStoredFromTheSessionAndReviewedOnTheHostPage() => ReportsAsync().GetAwaiter().GetResult();

    [Check("server.air-report-description-encoded-and-unlogged")]
    public static void ReportDescriptionRoundTripsEncodedAndNeverReachesALog() => DescriptionAsync().GetAwaiter().GetResult();

    private static AsObject Complaint(long reportingSummonerId, long reportedSummonerId, string offense, double gameId, string? comment) =>
        AirWire.Object("com.riotgames.platform.gameclient.domain.PlayerComplaint", ("reportSource", "GAME"),
            ("reportingSummonerId", (double)reportingSummonerId), ("reportedSummonerId", (double)reportedSummonerId),
            ("offense", offense), ("gameId", gameId), ("comment", comment), ("ipAddress", null));

    private static async Task ReportsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-reports-" + Guid.NewGuid().ToString("N"));
        try
        {
            long reporterId, reportedSummonerId;
            Guid first;
            using (var accounts = new PlatformAccounts(root))
            {
                var reporter = accounts.RegisterPublic("report_from", "testing-password", "Report From");
                var reported = accounts.RegisterPublic("report_about", "testing-password", "<i>Report</i> About");
                (reporterId, reportedSummonerId) = (reporter.Id, reported.SummonerId);
                var sessions = new PlatformSessions(accounts);
                var session = sessions.Login("rtmp-report", reporter.Username, sessions.IssueTicket(reporter), Version);
                TestAssert.True(sessions.AuthenticateChannel("rtmp-report", reporter.Username, session.Token));
                await using var manager = PlatformChecks.CreateNonLaunchingManager(root);
                var rpc = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1"));

                var states = (AsObject)((AsObject)rpc.Route("rtmp-report", "clientFacadeService", "getLoginDataPacketForUser", [])!)["clientSystemStates"];
                TestAssert.Equal(true, states["tribunalEnabled"], "the stock report dialog opens only with tribunalEnabled");

                // The client's reportingSummonerId is ignored: the reporter is the session's own account.
                var longText = "Synthetic\u0007 report " + new string('x', 700);
                TestAssert.Equal(null, rpc.Route("rtmp-report", "clientFacadeService", "reportPlayer",
                    [Complaint(reported.SummonerId, reported.SummonerId, "VERBAL_ABUSE", 7, longText)]));
                TestAssert.Throws<ArgumentException>(() => rpc.Route("rtmp-report", "clientFacadeService", "reportPlayer",
                    [Complaint(reporter.SummonerId, reported.SummonerId, "typed free text", 7, "x")]));
                TestAssert.Equal(null, rpc.Route("rtmp-report", "clientFacadeService", "reportPlayer",
                    [Complaint(reporter.SummonerId, reported.SummonerId, "LEAVING_AFK", 8, null)]));
                var reports = accounts.ListReports();
                TestAssert.Equal(2, reports.Length, "a free-text offense is refused and stores nothing");
                var report = reports[0];
                TestAssert.True(report.ReporterAccountId == reporter.Id && report.ReporterSummonerId == reporter.SummonerId &&
                    report.ReportedSummonerId == reported.SummonerId && report.Offense == "VERBAL_ABUSE" && report.GameId == 7 &&
                    report.Outcome is null, "the report keeps the session's reporter, the reported summoner, offense and game");
                TestAssert.Equal(PlatformAccounts.MaxReportComment, report.Comment.Length, "the description is cut at the client's 500 characters");
                TestAssert.True(report.Comment.StartsWith("Synthetic report ", StringComparison.Ordinal), "control characters are dropped");
                TestAssert.Equal("", reports[1].Comment);
                first = report.Id;
            }

            using (var accounts = new PlatformAccounts(root))
            {
                TestAssert.Equal(2, accounts.ListReports().Length, "reports persist in the account store");
                TestAssert.Equal(first, accounts.ListReports()[0].Id);
                var port = AirManagementChecks.FreePort();
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
                builder.Services.AddAntiforgery();
                await using var app = builder.Build();
                PlatformManagement.Map(app, accounts, port, AirManagementChecks.FreePort());
                await app.StartAsync();
                try
                {
                    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false };
                    using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                    var page = await client.GetStringAsync("/manage");
                    TestAssert.True(page.Contains("Player reports (2 open)", StringComparison.Ordinal), "the page counts the open reports");
                    TestAssert.True(page.Contains("<td>Report From</td><td>&lt;i&gt;Report&lt;/i&gt; About</td><td>VERBAL_ABUSE</td><td>7</td>", StringComparison.Ordinal),
                        "names are resolved now and encoded");
                    var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                    var token = new KeyValuePair<string, string>(field.Groups[1].Value, WebUtility.HtmlDecode(field.Groups[2].Value));
                    FormUrlEncodedContent Review(Guid id, string outcome, bool withToken = true)
                    {
                        var fields = new Dictionary<string, string> { ["id"] = id.ToString("D"), ["outcome"] = outcome };
                        if (withToken) fields[token.Key] = token.Value;
                        return new FormUrlEncodedContent(fields);
                    }

                    using var forged = await client.PostAsync("/manage/report", Review(first, "Reviewed", withToken: false));
                    TestAssert.False(forged.IsSuccessStatusCode || forged.StatusCode == HttpStatusCode.Redirect, "the review needs its antiforgery token");
                    using var remote = new HttpRequestMessage(HttpMethod.Post, "/manage/report") { Content = Review(first, "Reviewed") };
                    remote.Headers.Host = "public.example";
                    TestAssert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(remote)).StatusCode, "a rebound host name is refused");
                    using var bogus = await client.PostAsync("/manage/report", Review(first, "Deleted"));
                    TestAssert.False(bogus.IsSuccessStatusCode || bogus.StatusCode == HttpStatusCode.Redirect, "only Reviewed or Dismissed");
                    TestAssert.True(accounts.ListReports().All(r => r.Outcome is null), "no refused post closes a report");

                    using var reviewed = await client.PostAsync("/manage/report", Review(first, "Reviewed"));
                    TestAssert.Equal(HttpStatusCode.Redirect, reviewed.StatusCode);
                    using var dismissed = await client.PostAsync("/manage/report", Review(accounts.ListReports()[1].Id, "Dismissed"));
                    TestAssert.Equal(HttpStatusCode.Redirect, dismissed.StatusCode);
                    TestAssert.True(accounts.ListReports().Select(r => r.Outcome).SequenceEqual(["Reviewed", "Dismissed"]));
                    page = await client.GetStringAsync("/manage");
                    TestAssert.True(page.Contains("Player reports (0 open)", StringComparison.Ordinal) && page.Contains("<td>Dismissed</td>", StringComparison.Ordinal) &&
                        !page.Contains("action=/manage/report", StringComparison.Ordinal), "closed reports stay listed without their buttons");
                }
                finally { await app.StopAsync(); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // The whole platform path a stock client takes: remoting login, channel login, reportPlayer over RTMP; then the host page.
    private static async Task DescriptionAsync()
    {
        const string marker = "Synthetic-Desc-7f3a";
        const string description = "<script>alert('x')</script>\r\n" + marker + " & more";
        var root = Path.Combine(Path.GetTempPath(), "league-air-report-text-" + Guid.NewGuid().ToString("N"));
        var rtmpLog = new StringWriter();
        var webLog = new CapturedLog();
        try
        {
            using var accounts = new PlatformAccounts(root);
            var reporter = accounts.RegisterPublic("text_from", "testing-password", "Text From");
            var reported = accounts.RegisterPublic("text_about", "testing-password", "Text About");
            var sessions = new PlatformSessions(accounts);
            await using var manager = PlatformChecks.CreateNonLaunchingManager(root);
            var services = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1"));
            var rtmp = new AirRtmpHost(new IPEndPoint(IPAddress.Loopback, 0), services.Route, sessions.AuthenticateChannel,
                sessions.AccountForConnection, services.Disconnect, rtmpLog);
            await rtmp.StartAsync();
            var client = new RtmpClient(new Uri($"rtmp://127.0.0.1:{rtmp.BoundEndPoint.Port}"), new SerializationContext(), ObjectEncoding.Amf3);
            try
            {
                await client.ConnectAsync();
                var login = (AsObject)await client.InvokeAsync<object>("my-rtmps", "loginService", "login",
                    AirWire.Object("com.riotgames.platform.login.AuthenticationCredentials",
                        ("username", reporter.Username), ("authToken", sessions.IssueTicket(reporter)), ("clientVersion", Version)));
                TestAssert.True(await client.LoginAsync(reporter.Username, (string)login["token"]), "the channel login succeeds");
                await client.InvokeAsync<object>("my-rtmps", "clientFacadeService", "reportPlayer",
                    Complaint(reporter.SummonerId, reported.SummonerId, "OFFENSIVE_LANGUAGE", 12, description));
            }
            finally { client.Close(); await rtmp.DisposeAsync(); }

            var stored = accounts.ListReports().Single();
            TestAssert.Equal("<script>alert('x')</script>\n" + marker + " & more", stored.Comment, "the description is kept as plain text");
            TestAssert.True(rtmpLog.ToString().Contains("operation=reportPlayer", StringComparison.Ordinal), "the operation itself is logged");

            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(webLog);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, AirManagementChecks.FreePort());
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var page = await http.GetStringAsync("/manage");
                TestAssert.True(page.Contains("<td>&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;<br>" + marker + " &amp; more</td>", StringComparison.Ordinal),
                    "the description is HTML-encoded, its line break the only markup");
                TestAssert.False(page.Contains("<script", StringComparison.OrdinalIgnoreCase), "no player text reaches the page as markup");
            }
            finally { await app.StopAsync(); }
            foreach (var (name, text) in new[] { ("RTMP operation log", rtmpLog.ToString()), ("web host log", webLog.Text) })
                TestAssert.False(text.Contains(marker, StringComparison.Ordinal) || text.Contains("alert(", StringComparison.Ordinal),
                    $"the description never reaches the {name}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>Every message the web host logs, at every level.</summary>
    internal sealed class CapturedLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public string Text => string.Join('\n', _lines);
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(CapturedLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._lines.Enqueue($"{category} {formatter(state, exception)} {exception}");
        }
    }
}
