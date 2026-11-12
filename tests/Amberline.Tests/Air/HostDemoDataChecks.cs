using System.Net;
using System.Text.Json;
using Amberline.Data;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// DIAGNOSTICS: the host Dashboard's demo-data switch (<c>AMBERLINE_DEMO_DATA=1</c>, read once when the Platform starts). The start checks set the
/// variable before their starts, run serially, and restore it in <c>finally</c>. The switch changes no account, game or settings data, so the
/// accounts file keeps its bytes. The seats' champions are a pure choice, checked by <c>server.host-demo-data-seat-champions</c>.
/// </summary>
public static class HostDemoDataChecks
{
    private const string Variable = "AMBERLINE_DEMO_DATA";
    private const string Password = "Synthetic-demo-Pw-31";
    // The switch, and the two variables a full start sets for the Platform's own run.
    private static readonly string[] Variables = [Variable, "GAME_DATA_ROOT", "AIR_CLIENT_ROOT"];

    // The feed's eight lines, newest first, as the page shows them.
    private static readonly string[] Feed =
    [
        "Game started · 3v3 map · 4 players", "Flint logged in", "Game started · Capture map · 6 players", "Wren logged in",
        "Game ended · 5v5 map · Blue won", "Sable logged in", "Moss added", "Rook signed up",
    ];

    [Check("server.host-demo-data-off-by-default", Serial = true, TimeoutSeconds = 180)]
    public static void WithoutTheVariableTheDashboardShowsNoDemoData() => OffByDefaultAsync().GetAwaiter().GetResult();

    [Check("server.host-demo-data-fills-dashboard", Serial = true, TimeoutSeconds = 180)]
    public static void WithTheVariableTheDashboardShowsTwoGamesAndTheFeed() => FillsDashboardAsync().GetAwaiter().GetResult();

    [Check("server.host-demo-data-seat-champions", TimeoutSeconds = 60)]
    public static void TheSeatsNameRosterChampionsUntilTheListIsLoaded()
    {
        var roster = ChampionCatalog.PlayableNames;
        var loaded = Enumerable.Range(1, 12).Select(index => $"Loaded {index}").ToArray();
        // Not loaded: the roster's first ten in seat order, whatever else is offered.
        var unloaded = HostDemoData.SeatChampions(false, loaded, roster);
        TestAssert.Equal(string.Join(" | ", roster.Take(10)), string.Join(" | ", unloaded), "not loaded: the roster's first ten, in seat order");
        TestAssert.Equal(10, unloaded.Distinct().Count(), "not loaded: ten different names");
        TestAssert.True(unloaded.All(name => name != "Champion" && roster.Contains(name)), "not loaded: roster names only, never the placeholder");
        // Loaded: the loaded list's first ten, in seat order.
        var picked = HostDemoData.SeatChampions(true, loaded, roster);
        TestAssert.Equal(string.Join(" | ", loaded.Take(10)), string.Join(" | ", picked), "loaded: the loaded list's first ten, in seat order");
        TestAssert.Equal(10, picked.Distinct().Count(), "loaded: ten different names");
        // Loaded but empty: the roster, as when nothing is loaded.
        TestAssert.Equal(string.Join(" | ", roster.Take(10)), string.Join(" | ", HostDemoData.SeatChampions(true, Array.Empty<string>(), roster)),
            "loaded but empty: the roster");
    }

    /// <summary>No variable: a full start shows no game card, no lobbies line, counts no game and seeds no feed; switching it on afterwards shows nothing.</summary>
    private static async Task OffByDefaultAsync()
    {
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);
            var home = Home.Create("league-host-demo-data-off");
            home.ChooseFolders();
            var run = Task.Run(() => HostMode.RunAsync(home.Loop));
            try
            {
                using var client = home.Client();
                await WaitForHealthAsync(client, "ready", run);
                await home.SignInAsync(client);
                AssertOff(await StatusAsync(client));
                // The switch is read once, at the start: switching it on now shows nothing.
                Environment.SetEnvironmentVariable(Variable, "1");
                AssertOff(await StatusAsync(client));
            }
            finally { await home.StopAsync(run); }
            Delete(home.Root);
        }
        finally { Restore(saved); }
    }

    private static void AssertOff(JsonElement status)
    {
        TestAssert.Equal(0, status.GetProperty("cards").GetArrayLength(), "no made-up game: " + status.GetRawText());
        TestAssert.True(!status.TryGetProperty("lobbies", out var lobbies) || lobbies.ValueKind == JsonValueKind.Null, "no made-up lobbies line");
        TestAssert.Equal(0, status.GetProperty("gamesRunning").GetInt32(), "no made-up game counted");
        TestAssert.Equal(0, status.GetProperty("activity").GetArrayLength(), "no seeded feed");
    }

    /// <summary>
    /// The switch on, in two starts. The first has no account, so the champion list is loaded only if an earlier check loaded it; the second has
    /// an account, which loads it. Each start's seats name exactly what its champion-list state allows, and the accounts file keeps its bytes
    /// through the second start. The switch is read at each start only: turning it off during the first start changes nothing.
    /// </summary>
    private static async Task FillsDashboardAsync()
    {
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, "1");
            var home = Home.Create("league-host-demo-data-on");
            home.ChooseFolders();
            var firstState = ChampionState();
            var first = Task.Run(() => HostMode.RunAsync(home.Loop));
            try
            {
                using var client = home.Client();
                await WaitForHealthAsync(client, "ready", first);
                await home.SignInAsync(client);
                var status = await StatusAsync(client);
                AssertFilled(status, firstState);
                // The switch is read once, at the start: switching it off now changes nothing on the Dashboard.
                Environment.SetEnvironmentVariable(Variable, null);
                AssertFilled(await StatusAsync(client), firstState);

                // End game on a demo game only takes it off the board: no match server is asked to stop anything.
                var cards = status.GetProperty("cards").EnumerateArray().ToArray();
                var end = $"{HostDashboard.PagePath}/games/end";
                var id = cards[0].GetProperty("id").GetString()!;
                string confirm;
                using (var question = await client.GetAsync($"{end}?id={id}"))
                {
                    confirm = await question.Content.ReadAsStringAsync();
                    TestAssert.Equal(HttpStatusCode.OK, question.StatusCode, confirm);
                }
                using (var answer = await client.PostAsync(end, new FormUrlEncodedContent([Token(confirm), new KeyValuePair<string, string>("id", id)])))
                {
                    TestAssert.Equal(HttpStatusCode.SeeOther, answer.StatusCode, await answer.Content.ReadAsStringAsync());
                    TestAssert.Equal(HostDashboard.PagePath, answer.Headers.Location?.OriginalString, "back to the dashboard");
                }
                var left = await StatusAsync(client);
                var rest = left.GetProperty("cards").EnumerateArray().ToArray();
                TestAssert.Equal(1, rest.Length, "End game takes its demo game off the board");
                TestAssert.Equal(cards[1].GetProperty("id").GetString(), rest[0].GetProperty("id").GetString(), "the other demo game stays");
                TestAssert.Equal(1, left.GetProperty("gamesRunning").GetInt32(), "the count follows the cards");
            }
            finally { await home.StopAsync(first); }

            // The second start: an account loads the champion list in this process. The Platform's own variables go back first, and the switch is on again.
            RestorePlatformVariables(saved);
            Environment.SetEnvironmentVariable(Variable, "1");
            using (var accounts = new PlatformAccounts(home.Loop.Paths.Data)) accounts.RegisterPublic("demo_rook", Password, "Rook");
            var secondState = ChampionState();
            var accountsFile = Path.Combine(home.Loop.Paths.Data, "accounts.json");
            var before = await File.ReadAllBytesAsync(accountsFile);
            var again = home.Again();
            var second = Task.Run(() => HostMode.RunAsync(again.Loop));
            try
            {
                using var client = again.Client();
                await WaitForHealthAsync(client, "ready", second);
                await again.SignInAsync(client);
                AssertFilled(await StatusAsync(client), secondState);
            }
            finally { await again.StopAsync(second); }
            var after = await File.ReadAllBytesAsync(accountsFile);
            TestAssert.True(before.SequenceEqual(after), "the accounts file keeps its bytes");
            Delete(home.Root);
        }
        finally { Restore(saved); }
    }

    /// <summary>
    /// What one start with the switch on shows: two games with their teams, the seats' champions (exactly the choice for the champion-list state
    /// that start saw), the lobbies line, the games count and the eight feed lines.
    /// </summary>
    private static void AssertFilled(JsonElement status, (bool Loaded, string[] Names) state)
    {
        var cards = status.GetProperty("cards").EnumerateArray().ToArray();
        TestAssert.Equal(2, cards.Length, status.GetRawText());
        TestAssert.Equal("Capture map · Capture: Rook, Sable, Moss against Bot, Bot, Bot", Describe(cards[0]), "the Capture map 3v3");
        TestAssert.Equal("3v3 map · Classic: Wren, Bot against Flint, Bot", Describe(cards[1]), "the 3v3 map 2v2 with bots");
        TestAssert.True(cards.All(card => card.GetProperty("canEnd").GetBoolean()), "End game is offered on each demo game");
        TestAssert.True(cards[0].GetProperty("elapsedSeconds").GetInt32() >= 12 * 60, "the Capture game has run for 12 minutes");
        TestAssert.True(cards[1].GetProperty("elapsedSeconds").GetInt32() >= 3 * 60, "the Classic game has run for 3 minutes");
        var seats = cards.SelectMany(card => card.GetProperty("blue").EnumerateArray().Concat(card.GetProperty("red").EnumerateArray()))
            .Select(seat => seat.GetProperty("champion").GetString()!).ToArray();
        var expected = HostDemoData.SeatChampions(state.Loaded, state.Names, ChampionCatalog.PlayableNames);
        TestAssert.Equal(string.Join(" | ", expected), string.Join(" | ", seats), $"the seats for a list loaded: {state.Loaded}");
        TestAssert.True(seats.All(name => name != "Champion" && ChampionCatalog.PlayableNames.Contains(name)), "no placeholder: every seat a roster champion");
        TestAssert.Equal(10, seats.Distinct().Count(), "ten seats, ten different champions");
        TestAssert.Equal("Lobbies: 1 waiting (Custom 3v3, 2/6)", status.GetProperty("lobbies").GetString(), "the waiting-lobbies line");
        TestAssert.Equal(2, status.GetProperty("gamesRunning").GetInt32(), "both demo games count as running");
        var feed = status.GetProperty("activity").EnumerateArray().Select(line => line.GetProperty("text").GetString());
        TestAssert.Equal(string.Join(" | ", Feed), string.Join(" | ", feed), "eight feed lines, newest first");
    }

    /// <summary>The champion list's state as the check sees it now: loaded or not, and its names when loaded (the list is read only once it is loaded).</summary>
    private static (bool Loaded, string[] Names) ChampionState() =>
        AirGameDtos.ChampionNamesLoaded ? (true, AirGameDtos.ChampionNames.Values.ToArray()) : (false, Array.Empty<string>());

    /// <summary>A card as one line: its map and mode, then each team's players (a bot says "Bot").</summary>
    private static string Describe(JsonElement card) =>
        $"{card.GetProperty("map").GetString()} · {card.GetProperty("mode").GetString()}: {Side(card, "blue")} against {Side(card, "red")}";

    private static string Side(JsonElement card, string team) =>
        string.Join(", ", card.GetProperty(team).EnumerateArray().Select(seat => seat.GetProperty("player").GetString()));

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var answer = await client.GetAsync("/host/status.json");
        var text = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    /// <summary>The Platform's own start variables back to their values before the check; the switch keeps the value the check set.</summary>
    private static void RestorePlatformVariables(Dictionary<string, string?> saved)
    {
        foreach (var name in Variables.Skip(1)) Environment.SetEnvironmentVariable(name, saved[name]);
    }

    private static void Restore(Dictionary<string, string?> saved)
    {
        foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
    }
}
