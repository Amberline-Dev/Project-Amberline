using System.Net;
using System.Text.RegularExpressions;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Codec = Amberline.AirCompanion.InviteLink;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 917 follow-up): the operator's /manage (the dev previews' INVITE-FRIEND.cmd opens it) keeps its "Play with friends"
/// part, with the server's invite link in place of the friend launcher's Play.cmd: the host pages' InviteFor and InviteBox, and a Copy that
/// runs from the page's own script (its policy allows that one and nothing inline). Without a public address it says so, as the host pages do.
/// </summary>
public static class AirManagementInviteChecks
{
    private const string Pin = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";

    [Check("server.air-management-invite-link", TimeoutSeconds = 120)]
    public static void TheOperatorPageGivesTheInviteLink() => OperatorPageAsync().GetAwaiter().GetResult();

    private static async Task OperatorPageAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-management-invite-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "data"));
            // A development build's Platform folder (no BUILD.json version beside it), as in the dev previews.
            var platform = Directory.CreateDirectory(Path.Combine(root, "app", "platform")).FullName;
            string? host = null;
            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, 8391, inviteLink: () => HostInvite.InviteFor(platform, host, 8391, Pin, "Test Server"));
            await app.StartAsync();
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };

                // No public address yet: the part says so, as the host pages do, and offers no Copy.
                var page = await client.GetStringAsync("/manage?refresh=off");
                TestAssert.True(page.Contains("<h2>Play with friends</h2><p>Send this invite link to your friends.", StringComparison.Ordinal) &&
                    page.Contains("<p class=tip>The invite link appears here once Amberline knows your address.</p>", StringComparison.Ordinal) &&
                    !page.Contains("data-copy=invite-link", StringComparison.Ordinal) && !page.Contains("Play.cmd", StringComparison.Ordinal), page);

                // The address known: the link (a development build names the line's first player-app release), its box, Copy and the script.
                host = "203.0.113.7";
                using var answer = await client.GetAsync("/manage?refresh=off");
                page = await answer.Content.ReadAsStringAsync();
                var link = WebUtility.HtmlDecode(Regex.Match(page, "<pre id=invite-link class=message>([^<]+)</pre>").Groups[1].Value);
                TestAssert.Equal(Codec.Link(new(Codec.TagPrefix + Codec.FirstPlayerRelease, host, 8391, Pin, "Test Server")), link, "the operator page's link: " + page);
                TestAssert.True(page.Contains("<button type=button data-copy=invite-link>Copy</button>", StringComparison.Ordinal) &&
                    page.Contains($"<script src={PlatformManagement.CopyScriptPath} defer></script>", StringComparison.Ordinal) &&
                    !page.Contains("Play.cmd", StringComparison.Ordinal) && !page.Contains("/bootstrap/", StringComparison.Ordinal), page);
                var policy = string.Join(";", answer.Headers.GetValues("Content-Security-Policy"));
                TestAssert.Equal("default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; form-action 'self'; frame-ancestors 'none'", policy,
                    "the page's policy: its own script, nothing inline");
                using var script = await client.GetAsync(PlatformManagement.CopyScriptPath);
                TestAssert.Equal(HttpStatusCode.OK, script.StatusCode, "the Copy script");
                TestAssert.True((await script.Content.ReadAsStringAsync()).Contains("navigator.clipboard.writeText(source.textContent)", StringComparison.Ordinal), "it copies the box's text");

                // A release beside the Platform names its own tag.
                File.WriteAllText(Path.Combine(root, "app", "BUILD.json"), "{\"version\":\"1.4.0\"}");
                page = await client.GetStringAsync("/manage?refresh=off");
                TestAssert.Equal(PlatformLine.ReleaseTagPrefix + "1.4.0", Codec.Decode(WebUtility.HtmlDecode(
                    Regex.Match(page, "<pre id=invite-link class=message>([^<]+)</pre>").Groups[1].Value)).Invite?.Tag, "a release's own tag");
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
