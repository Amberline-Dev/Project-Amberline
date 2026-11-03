using System.Net;
using System.Text.Json;
using Amberline.Network.Sessions;

namespace Amberline.Platform;

public static partial class PlatformHome
{
    /// <summary>
    /// <c>GET /ladder</c>. The page names every summoner, so on the public port it needs a friend's live tunnel token, which the
    /// companion's proxy adds to every request it forwards; the host's loopback port serves it as is.
    /// </summary>
    public static void MapLadder(WebApplication app, PlatformAccounts accounts, int remotePort, AirRemoteAccess remoteAccess) =>
        app.MapGet("/ladder", (HttpContext context) => context.Connection.LocalPort == remotePort &&
            !AirRemoteTunnel.AuthorizeBearer(context, remoteAccess.Authorize) ? Results.Unauthorized()
            : Results.Content(RenderLadder(accounts), "text/html; charset=utf-8"));

    /// <summary>
    /// The profile's "Ladder Rankings" button (SummonerSummaryView: <c>riotApplication.ladderURL</c> when set, else the ladder
    /// site the client's bundle names) opens this page in the system browser; the companion sets <c>ladderURL</c> in lol.properties
    /// (RiotApplication.readProperties sets any public property it names). Riot's ladder was its website's ranked Elo ladder, one per
    /// ranked queue; batch 586 puts this host's ranked queues first (the profile's three rows, named as the stock buddy tooltip names
    /// them, then batch 590's 11, 18 and 19, the ladder being the only place besides the end-of-game line that shows those ratings),
    /// each summoner's rating once wins + losses reach 10 and the placement count before, as the stock profile hides a rating
    /// until then (getEloDisplayType). NOT RETAIL: the page below them ranks every summoner by all the games this host recorded (normal,
    /// ranked and custom, bots excluded): most wins first, then fewer losses; retail's ladder was ranked only.
    /// </summary>
    public static string RenderLadder(PlatformAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var everyone = accounts.ListAccounts();
        // Batch 590: the queues the profile has no row for (11, 18, 19) follow; Reconstruction: their names, after the tooltip's pattern.
        var ranked = string.Concat(new[] { ("RANKED_SOLO_5x5", "Ranked 5x5 Solo"), ("RANKED_PREMADE_5x5", "Ranked 5x5 Team"),
            ("RANKED_PREMADE_3x3", "Ranked 3x3 Team"), ("RANKED_SOLO_3x3", "Ranked 3x3 Solo"), ("ODIN_RANKED_SOLO", "Dominion Ranked 5x5 Solo"),
            ("ODIN_RANKED_PREMADE", "Dominion Ranked 5x5 Team") }.Select(queue =>
        {
            var rated = everyone.Select(account =>
            {
                var receipts = accounts.ResultsFor(account.Id);
                var games = PlatformRatings.History(account.Id, receipts, queue.Item1).ToArray();
                var (wins, losses, _) = AirProfileDtos.Record(account, receipts, queue.Item1);
                return (account.Summoner, Rating: games.Length == 0 ? 0 : games[^1].After, Wins: wins, Losses: losses,
                    Placed: wins + losses >= PlatformRatings.PlacementGames);
            }).Where(row => row.Wins + row.Losses > 0).OrderByDescending(row => row.Placed)
            .ThenByDescending(row => row.Placed ? row.Rating : row.Wins + row.Losses).ThenBy(row => row.Summoner, StringComparer.OrdinalIgnoreCase)
            .Select((row, index) => $"<tr><td>{index + 1}</td><td>{WebUtility.HtmlEncode(row.Summoner)}</td><td>" +
                (row.Placed ? row.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"placement {row.Wins + row.Losses}/{PlatformRatings.PlacementGames}") + $"</td><td>{row.Wins}</td><td>{row.Losses}</td></tr>");
            return $"<h2>{queue.Item2}</h2><table><thead><tr><th>#</th><th>Summoner</th><th>Rating</th><th>Wins</th><th>Losses</th></tr></thead>" +
                $"<tbody>{string.Concat(rated)}</tbody></table>";
        }));
        var rows = everyone.Select(account =>
        {
            var (wins, losses) = (0, 0);
            foreach (var receipt in accounts.ResultsFor(account.Id))
                if (receipt.Result.Deserialize<MatchResult>() is { } result &&
                    result.Players.FirstOrDefault(player => player.PlayerId == (ulong)account.SummonerId) is { } player)
                {
                    if (player.Team == result.Winner) wins++;
                    else losses++;
                }
            return (account.Summoner, Wins: wins, Losses: losses);
        }).OrderByDescending(row => row.Wins).ThenBy(row => row.Losses).ThenBy(row => row.Summoner, StringComparer.OrdinalIgnoreCase)
        .Select((row, index) => $"<tr><td>{index + 1}</td><td>{WebUtility.HtmlEncode(row.Summoner)}</td><td>{row.Wins}</td><td>{row.Losses}</td></tr>");
        return $$"""
            <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width">
            <title>Amberline · Ladder Rankings</title>
            <style>body{margin:0;padding:5vh 18px;background:#101b29;color:#d5dce2;font:14px Arial,sans-serif}main{max-width:620px;margin:auto}h1{color:#e8c684;font:normal 23px Georgia,serif}h2{color:#d7c7a6;font:normal 17px Georgia,serif;margin-top:28px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 12px;border-bottom:1px solid #2c3a4c}th{color:#d7c7a6}</style>
            </head><body><main><h1>Ladder Rankings</h1>{{ranked}}<h2>All games</h2><p>Every game played on this server, normal, ranked and custom.</p>
            <table><thead><tr><th>#</th><th>Summoner</th><th>Wins</th><th>Losses</th></tr></thead><tbody>{{string.Concat(rows)}}</tbody></table>
            </main></body></html>
            """;
    }
}
