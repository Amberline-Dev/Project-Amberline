using Amberline.Core;

namespace Amberline.Platform;

public sealed partial class PlatformGames
{
    /// <summary>
    /// Batch 580: <c>gameService.createTutorialGame(0)</c>, the Basic Tutorial. The stock client asks for it from the play menu and the
    /// first-login wizard (MasterGameController.startTutorialGame) and accepts the answer only in a champion-select state or
    /// <c>START_REQUESTED</c> (MasterGameController.as:1215-1224); a basic tutorial shows no champion choice (ChampionSelectionView's
    /// tutorial canvas covers it) and sends no pick, so the game is created already launching, like a queue game (StartQueueGame), and the
    /// ordinary tick sends the credentials, the IN_PROGRESS state and the results.
    /// <para>
    /// The seats are the map 3 TutorialScript's: the player is the first Order hero (GetTutorialPlayer), Ashe, with Heal on D for its Heal
    /// lesson (Tutorial_Replenish's RegisterOnSpellAvatarCastCallback(0)); the ally and the enemy are found by name, Nunu on Order and
    /// Master Yi on Chaos (OnTutorialInit, TutorialScript.txt proto 0.3 pc 0044-0053). Reconstruction: the player's second spell is a new
    /// account's (Ghost, PlatformAccounts), and the bots play at Basic, the difficulty the script's missing bot scripts are named after
    /// (MasterYi_Basic, Nunu_Basic). The game is not offered as a custom-game map (PlatformStoreCalendar.OpenMapIds).
    /// </para>
    /// </summary>
    public object CreateTutorial(PlatformAccount account)
    {
        const int basic = 0;
        static int Champion(string name) => AirGameDtos.ChampionNames.Single(pair => pair.Value == name).Key;
        lock (_gate)
        {
            RequireFree(account.Id, "Tutorial", joining: false);
            if (_lobbies.Values.Count(g => !IsTerminal(g) && g.Result is null) >= 32) throw AirFault.CreationDisabled();
            var now = _clock.GetUtcNow();
            var id = accounts.AllocateGameId();
            var game = new Lobby(id, $"Tutorial {id}", "", account.Id, 3)
            {
                MapId = GameMaps.Tutorial, State = "CHAMP_SELECT", Created = now, Deadline = now,
            };
            game.Members.Add(new(account, Team.Order)
            {
                Joined = now, Champion = Champion("Ashe"), Locked = true,
                Spell1 = AirGameDtos.SpellId("SummonerHeal"), Spell2 = AirGameDtos.SpellId("SummonerHaste"),
            });
            foreach (var (name, team) in new[] { ("Nunu", Team.Order), ("MasterYi", Team.Chaos) })
                game.Bots.Add(new(Champion(name), team, basic, OfferedBotSeat(PlatformBots.Seat(Champion(name), team, basic), GameMaps.Tutorial)));
            Launch(game);
            _lobbies.Add(game.Id, game);
            return AirGameDtos.Game(game);
        }
    }

    /// <summary>The champions Battle Training's guided grid offers (AdvancedTutorialController.tutorialChampions); see <see cref="Pick"/>.</summary>
    internal static readonly string[] BattleTrainingChampions = ["Garen", "Ryze", "Ashe"];

    /// <summary>
    /// Batch 669: <c>gameService.createTutorialGame(1)</c>, Battle Training, from the play menu's battle_training entry
    /// (ChooseGameFlowViewLayoutDefinition.as:158), the basic tutorial's "yes" (EndOfTutorialGameView.as:354) and the new summoner's tutorial
    /// offer. The client then runs its guided champion select (ChampionSelectionController.as:1286, AdvancedTutorialController): the player
    /// picks one of <see cref="BattleTrainingChampions"/>, the spells, runes and masteries, and locks in; the ordinary lock-in and post-pick
    /// countdown launch the game. The bundle names the map, "Battle Training - Summoner's Rift" (chooseGameType_tutorialGameTypes_advanced),
    /// and the mode is TUTORIAL (<see cref="GameMaps.IsPlayedIn"/>), so the client shows Battle Training's loading screen.
    /// <para>
    /// Reconstruction (the roster is the platform's and no shipped file holds it): the League wiki's Battle Training
    /// ("Old tutorials"): the player with four bots, Master Yi, Soraka, Shen and Sivir,
    /// against Malphite, Sivir, Nasus, Annie and Master Yi; all nine are 126 retail bots (fontconfig game_bot_*). The bots play at the
    /// lowest difficulty. The match is the shipped Summoner's Rift (LevelScript, bots' scripts); the quests the client can show
    /// (game_advanced_tutorial_quest_*, CreateQuest) were set by a server script that did not ship.
    /// </para>
    /// </summary>
    public object CreateBattleTraining(PlatformAccount account)
    {
        const int beginner = 0;
        static int Champion(string name) => AirGameDtos.ChampionNames.Single(pair => pair.Value == name).Key;
        lock (_gate)
        {
            RequireFree(account.Id, "Battle Training", joining: false);
            if (_lobbies.Values.Count(g => !IsTerminal(g) && g.Result is null) >= 32) throw AirFault.CreationDisabled();
            var now = _clock.GetUtcNow();
            var id = accounts.AllocateGameId();
            var game = new Lobby(id, $"Battle Training {id}", "", account.Id, 10)
            {
                MapId = GameMaps.Normal, Mode = GameMaps.TutorialMode, ConfigId = AirGameDtos.BattleTrainingConfigId,
                State = "CHAMP_SELECT", Created = now, Deadline = now.AddSeconds(BattleTrainingPickServerSeconds),
            };
            game.Members.Add(OfferedSeat(new(account, Team.Order) { Joined = now }, GameMaps.Normal));
            foreach (var (name, team) in new[]
                     {
                         ("MasterYi", Team.Order), ("Soraka", Team.Order), ("Shen", Team.Order), ("Sivir", Team.Order),
                         ("Malphite", Team.Chaos), ("Sivir", Team.Chaos), ("Nasus", Team.Chaos), ("Annie", Team.Chaos), ("MasterYi", Team.Chaos),
                     })
                game.Bots.Add(new(Champion(name), team, beginner, OfferedBotSeat(PlatformBots.Seat(Champion(name), team, beginner), GameMaps.Normal)));
            _lobbies.Add(game.Id, game);
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }
}
