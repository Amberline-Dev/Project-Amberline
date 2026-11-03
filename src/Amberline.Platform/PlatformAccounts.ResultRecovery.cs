using System.Text.Json;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Server;

namespace Amberline.Platform;

/// <summary>Accounts are the human seats in roster order; Players are every seat, with any bot seats after the humans.</summary>
public sealed record PlatformPendingMatch(int GameId, Guid HostGameId, int QueueId, long[] Accounts, MatchPlayer[] Players);

public sealed partial class PlatformAccounts
{
    private string HandoffDirectory => Path.Combine(Path.GetDirectoryName(_path)!, "match-handoffs");
    private string HandoffPath(long gameId) => Path.Combine(HandoffDirectory, gameId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
    private static PlatformPendingMatch ReadHandoff(string path)
    {
        var match = JsonSerializer.Deserialize<PlatformPendingMatch>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty match handoff.");
        if (match.GameId < 1 || match.HostGameId == Guid.Empty || (match.QueueId != 0 && PlatformGames.QueueOf(match.QueueId) is null) ||
            Path.GetFileNameWithoutExtension(path) != match.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            match.Accounts is null || match.Accounts.Length is < 1 or > MatchRoster.MaxPlayers || match.Players is null ||
            match.Players.Length < match.Accounts.Length || match.Players.Length > MatchRoster.MaxPlayers ||
            match.Accounts.Distinct().Count() != match.Accounts.Length)
            throw new InvalidDataException("Invalid saved match handoff.");
        return match;
    }

    public void PrepareMatch(PlatformPendingMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            if (match.GameId < 1 || match.GameId >= _state.NextGameId || match.HostGameId == Guid.Empty ||
                (match.QueueId != 0 && PlatformGames.QueueOf(match.QueueId) is null) || match.Accounts is null ||
                match.Accounts.Length is < 1 or > MatchRoster.MaxPlayers || match.Players is null ||
                match.Players.Length < match.Accounts.Length || match.Players.Length > MatchRoster.MaxPlayers ||
                match.Accounts.Distinct().Count() != match.Accounts.Length)
                throw new ArgumentException("Invalid platform match handoff.");
            var players = MatchRoster.Validate(match.Players);
            for (var i = 0; i < players.Length; i++)
            {
                // Human seats come first and belong to the accounts; every later seat is a bot.
                if (i < match.Accounts.Length)
                {
                    var account = RequireAccount(match.Accounts[i]);
                    if (players[i].IsBot || players[i].PlayerId != account.SummonerId || players[i].Name != account.Summoner)
                        throw new ArgumentException("Match handoff does not match the account roster.");
                }
                else if (!players[i].IsBot)
                    throw new ArgumentException("Match handoff does not match the account roster.");
            }
            if (File.Exists(HandoffPath(match.GameId)) ||
                _state.Results.Any(r => r.HostGameId == match.HostGameId || r.GameId == match.GameId))
                throw new ArgumentException("This match already has a durable handoff or result.");
            Directory.CreateDirectory(HandoffDirectory);
            var path = HandoffPath(match.GameId);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, match with { Players = players }, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path);
        }
    }

    public int RecoverResults(GameManager manager, TextWriter log)
    {
        if (!Directory.Exists(HandoffDirectory)) return 0;
        var recovered = 0;
        // ponytail: retain immutable handoffs as evidence and scan on startup;
        // add an index if the testing host grows beyond a small match archive.
        foreach (var path in Directory.EnumerateFiles(HandoffDirectory, "*.json")
            .OrderBy(p => long.TryParse(Path.GetFileNameWithoutExtension(p), out var id) ? id : long.MaxValue))
        {
            try
            {
                var match = ReadHandoff(path);
                if (FindResult(match.HostGameId) is not null) continue;
                if (manager.ReadArchivedResult(match.HostGameId) is not { } saved) continue;
                if (RecordResult(new(match.GameId, match.HostGameId, match.Accounts, saved.Result, saved.WrittenAt, match.QueueId)))
                {
                    recovered++;
                    log.WriteLine($"Recovered AIR result {match.GameId} from match {match.HostGameId}.");
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Keep the original journal and result file for repair/retry. One
                // damaged match must not prevent login or other valid recoveries.
                log.WriteLine($"AIR result recovery {Path.GetFileName(path)}: {error.GetType().Name}: {error.Message}");
            }
        }
        return recovered;
    }

    private static void ValidatePendingResult(PlatformPendingMatch pending, PlatformResult receipt, MatchResult result)
    {
        if (receipt.GameId != pending.GameId || receipt.HostGameId != pending.HostGameId || receipt.QueueId != pending.QueueId ||
            pending.Accounts is null || pending.Players is null || pending.Players.Length != result.Players.Count ||
            pending.Accounts.Length != receipt.Accounts.Length ||
            pending.Players.Any(p => p is null) || pending.Players.Select((p, index) => p.PlayerId ?? index + 1).Distinct().Count() != pending.Players.Length ||
            !receipt.Accounts.Order().SequenceEqual(pending.Accounts.Order()) || !Amberline.Core.GameMaps.IsSupported(result.MapId) || !Amberline.Core.GameMaps.IsPlayedIn(result.MapId, result.Mode))
            throw new InvalidDataException("Result does not match the durable platform handoff.");
        foreach (var player in result.Players)
        {
            // Bot seats carry no explicit player ID; the server numbers them by roster index + 1.
            var seat = pending.Players.Where((p, index) => (ulong)(p.PlayerId ?? index + 1) == player.PlayerId).SingleOrDefault();
            if (seat is null || player.Name != seat.Name || player.Team != seat.Team.ToString() ||
                player.Champion != seat.Champion || player.SkinId != seat.SkinId || player.IsBot != seat.IsBot ||
                player.SummonerSpells is null || !player.SummonerSpells.SequenceEqual([seat.SummonerSpell1, seat.SummonerSpell2]))
                throw new InvalidDataException("Result player does not match the frozen match seat.");
        }
    }
}
