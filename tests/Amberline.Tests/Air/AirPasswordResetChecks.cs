using System.Net;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Amberline.Tests.Air;

/// <summary>Batch 572: the host's password reset on /manage. Synthetic accounts and passwords only.</summary>
public static class AirPasswordResetChecks
{
    private const string Version = "1.45.11_10_04_15_28";

    [Check("server.air-manage-password-reset-boundary")]
    public static void PasswordResetIsLoopbackOnlyAndNeedsItsFormToken() => PasswordBoundaryAsync().GetAwaiter().GetResult();

    [Check("server.air-manage-password-reset-replaces-old-password")]
    public static void PasswordResetRehashesRevokesTheTunnelTokenAndEndsSessions() => PasswordResetAsync().GetAwaiter().GetResult();

    [Check("server.air-manage-password-reset-never-logged")]
    public static void PasswordResetNeverLogsOrStoresThePassword() => PasswordResetAsync(checkLogs: true).GetAwaiter().GetResult();

    private sealed record ResetHost(WebApplication App, HttpClient Client, KeyValuePair<string, string> Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); }

        public Task<HttpResponseMessage> Reset(long accountId, string password, string confirm, bool withToken = true, string? host = null)
        {
            var fields = new Dictionary<string, string> { ["accountId"] = accountId.ToString(), ["password"] = password, ["confirm"] = confirm };
            if (withToken) fields[Token.Key] = Token.Value;
            var request = new HttpRequestMessage(HttpMethod.Post, "/manage/password") { Content = new FormUrlEncodedContent(fields) };
            if (host is not null) request.Headers.Host = host;
            return Client.SendAsync(request);
        }

        public async Task<HttpStatusCode> LogIn(string username, string password) =>
            (await Client.PostAsync(Amberline.Platform.Program.LoginQueuePath, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["payload"] = $"user={username},password={password}" }))).StatusCode;
    }

    // The /manage page and the login queue on one loopback listener, as the platform serves them.
    private static async Task<ResetHost> StartResetHost(PlatformAccounts accounts, PlatformSessions sessions, AirRemoteAccess access,
        ILoggerProvider? log = null)
    {
        var port = AirManagementChecks.FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        if (log is not null) builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(log);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddAntiforgery();
        var app = builder.Build();
        PlatformManagement.Map(app, accounts, port, AirManagementChecks.FreePort(), remoteAccess: access, sessions: sessions);
        app.MapPost(Amberline.Platform.Program.LoginQueuePath, (Delegate)((HttpContext context) =>
            Amberline.Platform.Program.AuthenticateAsync(context, accounts, sessions, access)));
        await app.StartAsync();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false };
        var client = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        var page = await client.GetStringAsync("/manage");
        TestAssert.True(page.Contains("action=/manage/password", StringComparison.Ordinal) && page.Contains("type=password", StringComparison.Ordinal),
            "the page offers the password form");
        var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
        TestAssert.True(field.Success, "the page carries its antiforgery field");
        return new ResetHost(app, client, new(field.Groups[1].Value, WebUtility.HtmlDecode(field.Groups[2].Value)));
    }

    private static async Task PasswordBoundaryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-password-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            var friend = accounts.RegisterPublic("reset_friend", "old-password-1", "Reset Friend");
            var sessions = new PlatformSessions(accounts);
            var access = new AirRemoteAccess(Path.Combine(root, "remote"));
            await using var host = await StartResetHost(accounts, sessions, access);
            using var forged = await host.Reset(friend.Id, "new-password-2", "new-password-2", withToken: false);
            TestAssert.False(forged.IsSuccessStatusCode || forged.StatusCode == HttpStatusCode.Redirect, "the form needs its antiforgery token");
            using var remote = await host.Reset(friend.Id, "new-password-2", "new-password-2", host: "public.example");
            TestAssert.Equal(HttpStatusCode.NotFound, remote.StatusCode, "a rebound host name never reaches the form");
            using var mismatch = await host.Reset(friend.Id, "new-password-2", "new-password-3");
            TestAssert.False(mismatch.IsSuccessStatusCode || mismatch.StatusCode == HttpStatusCode.Redirect, "both entries must match");
            using var tooShort = await host.Reset(friend.Id, "short", "short");
            TestAssert.False(tooShort.IsSuccessStatusCode || tooShort.StatusCode == HttpStatusCode.Redirect, "the registration rule applies");
            TestAssert.True(accounts.Authenticate("reset_friend", "old-password-1") is not null, "no refused post changes the password");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task PasswordResetAsync(bool checkLogs = false)
    {
        const string oldPassword = "Synthetic-old-Pw-41";
        const string newPassword = "Synthetic-new-Pw-82";
        const string newerPassword = "Synthetic-newer-Pw-93";
        var root = Path.Combine(Path.GetTempPath(), "league-air-password-reset-" + Guid.NewGuid().ToString("N"));
        var log = new AirReportChecks.CapturedLog();
        try
        {
            using var accounts = new PlatformAccounts(root);
            var friend = accounts.RegisterPublic("reset_target", oldPassword, "Reset Target");
            var bystander = accounts.RegisterPublic("reset_other", oldPassword, "Reset Other");
            var sessions = new PlatformSessions(accounts);
            var access = new AirRemoteAccess(Path.Combine(root, "remote"));
            var tunnelToken = access.Issue(friend.Id).Token;
            var otherToken = access.Issue(bystander.Id).Token;
            await using var host = await StartResetHost(accounts, sessions, access, log);
            TestAssert.Equal(HttpStatusCode.OK, await host.LogIn("reset_target", oldPassword));

            // A live session and an unused login ticket, both from the old password.
            var live = sessions.Login("rtmp-live", friend.Username, sessions.IssueTicket(friend), Version);
            TestAssert.True(sessions.AuthenticateChannel("rtmp-live", friend.Username, live.Token));
            var unused = sessions.IssueTicket(friend);

            using var changed = await host.Reset(friend.Id, newPassword, newPassword);
            TestAssert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
            TestAssert.Equal("/manage?refresh=off&password=changed", changed.Headers.Location?.OriginalString);
            TestAssert.True((await host.Client.GetStringAsync("/manage?refresh=off&password=changed")).Contains("The password was changed.", StringComparison.Ordinal));
            TestAssert.Equal(HttpStatusCode.Forbidden, await host.LogIn("reset_target", oldPassword), "the old password is refused by the login queue");
            TestAssert.Equal(HttpStatusCode.OK, await host.LogIn("reset_target", newPassword), "the new password logs in");
            TestAssert.True(accounts.Authenticate("reset_target", oldPassword) is null && accounts.Authenticate("reset_target", newPassword) is not null,
                "the tunnel sign-in uses the same check");
            TestAssert.True(access.AccountFor(tunnelToken) is null, "the account's tunnel token is revoked");
            TestAssert.Equal(bystander.Id, access.AccountFor(otherToken), "another account's token stays");
            TestAssert.True(sessions.AccountForConnection("rtmp-live") is null, "the live session ended");
            TestAssert.Throws<ArgumentException>(() => sessions.Login("rtmp-late", friend.Username, unused, Version));
            TestAssert.Equal(HttpStatusCode.OK, await host.LogIn("reset_other", oldPassword), "another account keeps its password");

            // A session parked for a reconnect cannot be resumed after a reset either.
            var parked = sessions.Login("rtmp-park", friend.Username, sessions.IssueTicket(friend), Version);
            TestAssert.True(sessions.AuthenticateChannel("rtmp-park", friend.Username, parked.Token));
            sessions.Suspend("rtmp-park");
            using var again = await host.Reset(friend.Id, newerPassword, newerPassword);
            TestAssert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            TestAssert.False(sessions.Resume("rtmp-back", friend.Username, parked.Token), "the parked session is gone");
            TestAssert.Equal(HttpStatusCode.OK, await host.LogIn("reset_target", newerPassword));

            if (!checkLogs) return;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
                .Select(path => (path, File.ReadAllText(path))).ToArray();
            TestAssert.True(files.Any(file => file.path.EndsWith("accounts.json", StringComparison.Ordinal)), "the account store was written");
            TestAssert.True(log.Text.Contains("/manage/password", StringComparison.Ordinal), "the web host log saw the requests");
            foreach (var secret in new[] { newPassword, newerPassword })
            {
                TestAssert.False(log.Text.Contains(secret, StringComparison.Ordinal), "the new password never reaches the web host log");
                foreach (var (path, text) in files)
                    TestAssert.False(text.Contains(secret, StringComparison.Ordinal), $"the new password is never stored in plain text ({Path.GetFileName(path)})");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
