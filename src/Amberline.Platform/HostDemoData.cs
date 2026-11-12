using Amberline.Data;

namespace Amberline.Platform;

/// <summary>
/// DIAGNOSTICS: the host Dashboard's made-up games, lobbies line and activity feed for the owner's screenshots (owner, 11 November 2026: "Demo
/// data switch"). It is on only when the Platform starts with <c>AMBERLINE_DEMO_DATA=1</c>: no web route and no setting turns it on, and it
/// changes no account, game or settings data. <see cref="FromEnvironment"/> is read once per start (<see cref="HostMode.RunAsync"/>), and the
/// dashboard and the feed follow that value for the whole run. The made-up games live in memory: End game on one only takes it off the board
/// (<see cref="HostDashboard"/>), and no match server is asked to stop it.
/// </summary>
public static class HostDemoData
{
    /// <summary>The environment variable that switches the demo data on, read once when the Platform starts.</summary>
    public const string Variable = "AMBERLINE_DEMO_DATA";

    /// <summary>The waiting-lobbies line the page shows while the switch is on (a real waiting lobby shows its own line instead).</summary>
    public const string Lobbies = "Lobbies: 1 waiting (Custom 3v3, 2/6)";

    /// <summary>The seats of the two made-up games together: six on the Capture map, four on the 3v3 map.</summary>
    public const int Seats = 10;

    private const string Bot = "Bot";

    // Fixed ids that no match server issues: End game knows a demo game by its id and never sends it to a match server.
    private static readonly Guid CaptureGame = new("5d2e1a7c-3b9f-4c1e-8a6d-0f4b2c9e7a11");
    private static readonly Guid ClassicGame = new("9a4c7e2b-1d6f-4f8a-b3e5-6c0d8a2f4e22");

    // The feed's eight lines, oldest first, each with how many minutes before the start it happened.
    private static readonly (int MinutesAgo, string Text)[] Lines =
    [
        (50, "Rook signed up"),
        (44, "Moss added"),
        (40, "Sable logged in"),
        (33, $"Game ended · {HostActivity.MapName(1)} · Blue won"),
        (20, "Wren logged in"),
        (12, $"Game started · {HostActivity.MapName(8)} · 6 players"),
        (6, "Flint logged in"),
        (3, $"Game started · {HostActivity.MapName(4)} · 4 players"),
    ];

    /// <summary>
    /// Whether the switch is on for a Platform start: the environment variable says 1. <see cref="HostMode.RunAsync"/> calls this once per start,
    /// and nothing else reads the variable, so a change while the Platform runs changes nothing.
    /// </summary>
    public static bool FromEnvironment() => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>Whether <paramref name="id"/> is one of the two made-up games.</summary>
    public static bool IsDemo(Guid id) => id == CaptureGame || id == ClassicGame;

    /// <summary>
    /// The two made-up games: a Capture map 3v3 with three bots on Red, and a 3v3 map 2v2 with two bots, started 12 and 3 minutes before
    /// <paramref name="since"/>. The ten seats take their champions from <see cref="SeatChampions"/>.
    /// </summary>
    public static DashboardGameCard[] Cards(DateTimeOffset since)
    {
        // The loaded list is read only once something has loaded it: the AIR client is never read for the demo.
        var loaded = AirGameDtos.ChampionNamesLoaded;
        var names = SeatChampions(loaded, loaded ? AirGameDtos.ChampionNames.Values.ToArray() : Array.Empty<string>(), ChampionCatalog.PlayableNames);
        return
        [
            new(CaptureGame, HostActivity.MapName(8), "Capture", since.AddMinutes(-12),
                [new("Rook", names[0]), new("Sable", names[1]), new("Moss", names[2])],
                [new(Bot, names[3]), new(Bot, names[4]), new(Bot, names[5])], CanEnd: true),
            new(ClassicGame, HostActivity.MapName(4), "Classic", since.AddMinutes(-3),
                [new("Wren", names[6]), new(Bot, names[7])],
                [new("Flint", names[8]), new(Bot, names[9])], CanEnd: true),
        ];
    }

    /// <summary>
    /// The ten seats' champion names in seat order: seat i takes entry i of the loaded list when it is loaded and not empty, else of the static
    /// roster. A pure function (it reads nothing), so the ten seats name ten different champions whenever the list has ten names.
    /// </summary>
    public static string[] SeatChampions(bool loaded, IReadOnlyList<string> loadedNames, IReadOnlyList<string> roster)
    {
        var source = loaded && loadedNames.Count > 0 ? loadedNames : roster;
        return [.. Enumerable.Range(0, Seats).Select(seat => source[seat % source.Count])];
    }

    /// <summary>
    /// Adds the feed's eight made-up lines, spread over the hour before <paramref name="now"/>. Called only when the switch was read on at the start.
    /// </summary>
    public static void Seed(HostActivity activity, DateTimeOffset now)
    {
        foreach (var (minutesAgo, text) in Lines) activity.Add(text, now.AddMinutes(-minutesAgo));
    }
}
