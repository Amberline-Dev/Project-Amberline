using System.Text.Json;
using System.Text.Json.Serialization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;

namespace Amberline.Server;

public sealed record MatchPlayer(
    string Name,
    string Champion,
    Team Team,
    string SummonerSpell1 = SummonerLoadout.DefaultFirst,
    string SummonerSpell2 = SummonerLoadout.DefaultSecond,
    int SkinId = 0,
    int SummonerLevel = 30,
    int[]? RedRunes = null,
    int[]? BlueRunes = null,
    int[]? YellowRunes = null,
    int[]? BlackRunes = null,
    Dictionary<int, int>? Masteries = null,
    bool IsBot = false,
    int? PlayerId = null,
    // DIAGNOSTICS: the playtest cheat commands and the cheat packet answer only seats that may cheat. AIR matches allow a seat only
    // when its session reached the host PC directly; the Host API allows a seat only in a game created on the host PC; other rosters keep every seat (tools, developer matches).
    bool CanCheat = true);

public sealed record MatchPlayerLoadout(
    int SummonerLevel,
    RunePage Runes,
    MasteryPage Masteries);

/// <summary>The launcher and CLI share the stock client's roster limits.</summary>
public static class MatchRoster
{
    public const int MaxPlayers = 10;
    public const int MaxTeamPlayers = 5;
    public const int MaxNameLength = 27;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<Team>(allowIntegerValues: false) },
    };

    private static readonly Lazy<RuneCatalog> DefaultRunes = new(() =>
        new RuneCatalog(GameData.Default.Paths));
    private static readonly Lazy<MasteryCatalog> DefaultMasteries = new(() =>
        new MasteryCatalog(GameData.Default.Paths));

    public static MatchPlayer[] Read(string path)
    {
        try
        {
            return Validate(JsonSerializer.Deserialize<MatchPlayer[]>(File.ReadAllText(path), JsonOptions)
                ?? throw new ArgumentException("Roster must be a JSON array of players."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ArgumentException($"Cannot read roster '{path}': {exception.Message}", exception);
        }
    }

    public static MatchPlayer[] Validate(IReadOnlyList<MatchPlayer> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count is < 1 or > MaxPlayers)
            throw new ArgumentException($"Choose between 1 and {MaxPlayers} players.");
        var result = new MatchPlayer[players.Count];
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index] ?? throw new ArgumentException($"Player {index + 1} is missing.");
            if (player.PlayerId is <= 0) throw new ArgumentException("Explicit player IDs must be positive.");
            var name = player.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(c => c is < ' ' or > '~'))
                throw new ArgumentException($"Player {index + 1}: use a nickname of 1–{MaxNameLength} printable ASCII characters.");
            var champion = ServerOptions.SupportedChampions.FirstOrDefault(candidate =>
                string.Equals(candidate, player.Champion, StringComparison.OrdinalIgnoreCase));
            if (champion is null)
                throw new ArgumentException($"Player {index + 1}: select a supported champion.");
            if (player.Team is not (Team.Order or Team.Chaos))
                throw new ArgumentException($"Player {index + 1}: select Order or Chaos.");
            var loadout = SummonerLoadout.ValidatePair(player.SummonerSpell1, player.SummonerSpell2);
            GameData.Default.ValidateSkin(champion, player.SkinId);
            ValidateLoadout(player);
            result[index] = player with
            {
                Name = name,
                Champion = champion,
                SummonerSpell1 = loadout.First,
                SummonerSpell2 = loadout.Second,
                RedRunes = Copy(player.RedRunes),
                BlueRunes = Copy(player.BlueRunes),
                YellowRunes = Copy(player.YellowRunes),
                BlackRunes = Copy(player.BlackRunes),
                Masteries = player.Masteries is null
                    ? null
                    : new Dictionary<int, int>(player.Masteries),
            };
        }
        if (result.GroupBy(player => player.Team).Any(team => team.Count() > MaxTeamPlayers))
            throw new ArgumentException($"Each team can have at most {MaxTeamPlayers} players.");
        var duplicateChampion = result.GroupBy(player => (player.Team, player.Champion))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChampion is not null)
            throw new ArgumentException($"{duplicateChampion.Key.Team} already has {duplicateChampion.Key.Champion}. Each team can select a champion only once.");
        if (result.Select(player => player.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new ArgumentException("Each player needs a different nickname.");
        if (result.Select((player, index) => player.PlayerId ?? index + 1).Distinct().Count() != result.Length)
            throw new ArgumentException("Each player needs a different player ID.");
        if (!result.Any(player => !player.IsBot))
            throw new ArgumentException("A production roster needs at least one human player.");
        return result;
    }

    public static SessionParticipantDefinition[] ToParticipants(IReadOnlyList<MatchPlayer> players) =>
        Validate(players).Select((player, index) =>
        {
            var loadout = ValidateLoadout(player);
            return new SessionParticipantDefinition(
                (ulong)(player.PlayerId ?? index + 1), (uint)index, player.Name, player.Champion, player.Team,
                player.SummonerSpell1, player.SummonerSpell2, player.SkinId,
                loadout.SummonerLevel, loadout.Runes, loadout.Masteries, player.IsBot, CanCheat: player.CanCheat);
        }).ToArray();

    public static MatchPlayerLoadout ValidateLoadout(MatchPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        // A bot list is the shipped Bot*Runes.inibin and bypasses the lobby page rules (RunePage.CreateBot).
        var runes = player.IsBot
            ? RunePage.CreateBot(DefaultRunes.Value, (player.RedRunes ?? []).Concat(player.YellowRunes ?? [])
                .Concat(player.BlueRunes ?? []).Concat(player.BlackRunes ?? []))
            : RunePage.Create(DefaultRunes.Value,
                player.RedRunes, player.BlueRunes, player.YellowRunes, player.BlackRunes,
                player.SummonerLevel);
        var masteries = MasteryPage.Create(DefaultMasteries.Value, player.Masteries,
            player.SummonerLevel);
        return new MatchPlayerLoadout(player.SummonerLevel, runes, masteries);
    }

    private static int[]? Copy(IEnumerable<int>? values) =>
        values is null ? null : values.ToArray();
}
