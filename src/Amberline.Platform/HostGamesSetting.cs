using System.Globalization;
using System.Net;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: Settings' Games section (batch 684; owner, 4 November 2026: the host chooses how many games run at once, 1 to 32, default
/// 4; retail ran its match servers in its own data centres). One number and Save, saved as <c>platform.json</c> <c>maxGames</c> through
/// <see cref="HostMode.Change"/>. It counts for games that start from now on: <see cref="GameManager.MaxGames"/> changes at once (a lower
/// value ends no running game; a new game waits until fewer run) and the network panel tests again, so the router's ports follow at
/// once. A Windows Firewall rule for more game ports needs the host's Yes, which Save never asks: the dashboard's card offers Allow
/// through Windows Firewall (<see cref="FirewallState.OtherPorts"/>).
/// </summary>
public static class HostGamesSetting
{
    public const string Path = HostSettingsPage.PagePath + "/games";
    public const string Saved = "games";
    private const string Field = "games";

    /// <summary>The saved notice: Save never asks Windows, so it says where the firewall's question comes from.</summary>
    internal const string SavedNotice = "Saved. It counts for games that start from now on; a running game goes on. " +
        "If the dashboard asks, choose Allow through Windows Firewall.";

    /// <summary>The typed number of games, or the problem with it (<see cref="HostSettings.GamePortsFit"/>, with the saved first game port).</summary>
    public static (int Games, string? Problem) Read(string typed, int firstGamePort)
    {
        if (!int.TryParse(typed.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var games) || games is < 1 or > HostSettings.MostGames)
            return (0, $"Choose a number from 1 to {HostSettings.MostGames}.");
        return HostSettings.GamePortsFit(firstGamePort, games) ? (games, null)
            : (0, $"The game ports would go past 65535. Choose up to {65536 - firstGamePort} games.");
    }

    /// <summary>The section; <paramref name="typed"/> and <paramref name="problem"/> are a refused Save's.</summary>
    internal static string Section(PlatformSettings settings, string token, string? problem, string? typed)
    {
        var value = WebUtility.HtmlEncode(typed ?? settings.MaxGames.ToString(CultureInfo.InvariantCulture));
        return $"""
            <section class=folder aria-labelledby=games-title><h2 id=games-title>Games</h2>
            <form method=post action={Path} novalidate>{token}
            {HostPages.Field(Field, "Games at the same time",
                $"1 to {HostSettings.MostGames}. Each game uses about one CPU core, some memory and its own game port: now " +
                $"{HostDashboard.GamePortsText(settings.FirstGamePort, settings.MaxGames)}.",
                problem, $"type=number min=1 max={HostSettings.MostGames} inputmode=numeric value=\"{value}\"")}
            <div class=buttons><button type=submit>Save</button></div></form></section>
            """;
    }

    /// <summary>
    /// <c>POST</c> <see cref="Path"/>: a valid number is saved and handed to <paramref name="changed"/> (the manager and the network
    /// panel); a refused one comes back through <paramref name="refused"/> (the Settings page with the problem and what was typed).
    /// </summary>
    internal static void Map(WebApplication app, HostLoop loop, Func<HttpContext, IAntiforgery, string, string?, IResult> refused, Action<int>? changed)
    {
        app.MapPost(Path, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var form = await HostSteps.FormAsync(context, antiforgery);
            if (form is null) return refused(context, antiforgery, HostSteps.Expired, null);
            var typed = form[Field].ToString();
            (int Games, string? Problem) read = default;
            var first = 0;
            HostMode.Change(loop.Paths.Settings, saved =>
            {
                (read, first) = (Read(typed, saved.FirstGamePort), saved.FirstGamePort);
                return read.Problem is not null || saved.MaxGames == read.Games ? saved : saved with { MaxGames = read.Games };
            });
            if (read.Problem is { } problem) return refused(context, antiforgery, problem, typed);
            Console.WriteLine($"Settings: {read.Games} games at the same time ({HostDashboard.GamePortsText(first, read.Games)}).");
            changed?.Invoke(read.Games);
            return Results.Redirect(HostSettingsPage.PagePath + "?saved=" + Saved);
        });
    }
}
