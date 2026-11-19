using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 913: the platform's side of the end-of-game screen's Play Again. Whether the button is enabled is the client's own test, read once
/// when the screen opens (EndOfGameStatsController.activate, MasterGameController.allowedtoPlayAgain): AIR 1.45 enables it after a custom
/// game (the client's gameType PRACTICE_GAME, which its custom flows set and a GameDTO can overwrite on a reconnect); AIR 1.52 also needs a
/// selected queue, which both custom-game flows clear (gameQueueManager.reset), so 1.52 never enables it after one (retail as shipped). The
/// platform owns the game type its GameDTOs name and where the click leads: the custom game browser (playAnotherGame,
/// startPracticeGameFlowAtJoinGame) or the queue.
/// </summary>
public static class AirPlayAgainChecks
{
    [Check("server.air-play-again-after-a-custom-game")]
    public static void PlayAgainAfterACustomGame() => ScenarioAsync().GetAwaiter().GetResult();

    private static async Task ScenarioAsync()
    {
        var root = TemporaryDirectory("league-air-play-again");
        try
        {
            await using var rig = new AirSpectatorLobbyChecks.Rig(root, grid: false);
            var owner = rig.SignIn("againowner");
            var player = rig.SignIn("againplayer");
            var created = rig.Create(owner, "Again", "", "NONE");
            TestAssert.Equal("PRACTICE_GAME", created["gameType"], "the created custom game");
            var id = Convert.ToInt32(created["id"]);
            rig.Call(player, "joinGame", (double)id, null);
            await rig.LaunchAsync(id, owner, player);
            rig.Games.StartPlayingForTest(id);
            rig.Finish(owner);

            // Every GameDTO of a custom game names PRACTICE_GAME (1.45's test enables Play Again on that game type; a reconnect takes the
            // client's game type from the GameDTO), and so do the results, with no queue.
            foreach (var who in new[] { owner, player })
            {
                var game = (AsObject)AirSpectatorLobbyChecks.Rig.RoundTrip(rig.PushedTo(who).Last(body => body.TypeName == AirProtocolNames.GameDTO));
                TestAssert.Equal("IN_PROGRESS", game["gameState"], who.Summoner);
                TestAssert.Equal("PRACTICE_GAME", game["gameType"], who.Summoner);
                TestAssert.Equal("PRACTICE_GAME", ((AsObject)rig.Games.Latest(who.Id, id)!)["gameType"], who.Summoner);
                var stats = (AsObject)AirSpectatorLobbyChecks.Rig.RoundTrip(rig.PushedTo(who).Last(body => body.TypeName == AirProtocolNames.EndOfGameStats));
                TestAssert.Equal("PRACTICE_GAME", stats["gameType"], who.Summoner);
                TestAssert.Equal("NONE", stats["queueType"], who.Summoner);
            }

            // The click opens the custom game browser: it lists lobbies in team selection, never the finished game.
            TestAssert.False(((ArrayCollection)rig.Call(owner, "listAllPracticeGames")!).Cast<AsObject>().Any(game => Convert.ToInt32(game["id"]) == id),
                "the finished game is not listed");
            // From the results screen the owner makes the same game again (the finished one holds neither the player nor the name, PG-0008/PG-0013)...
            TestAssert.Equal("TEAM_SELECT", rig.Create(owner, "Again", "", "NONE")["gameState"]);
            // ...and the other player can queue (the rule a queue game's Play Again meets).
            var joined = (AsObject)rig.Rpc.Route(player.Username, "matchmakerService", "attachToQueue",
                [AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams", ("queueIds", AirWire.List([2d])),
                    ("team", null), ("invitationId", null), ("botDifficulty", null))])!;
            TestAssert.True(joined["playerJoinFailures"] is null, "a player on the results screen may queue");
            TestAssert.Equal(2, ((AsObject)((ArrayCollection)joined["joinedQueues"])[0])["queueId"]);
        }
        finally { DeleteDirectory(root); }
    }
}
