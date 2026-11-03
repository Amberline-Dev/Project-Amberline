namespace Amberline.Platform;

/// <summary>
/// The AMF class aliases of the stock AIR client's remoting classes (the <c>[RemoteClass(alias=...)]</c> names of its domain and
/// fault classes). Every object and fault the Platform sends carries one, or the client cannot decode it. These are protocol
/// identifiers required by the stock client, kept for interoperability only; their use implies no affiliation with, or endorsement
/// by, the owner of the names. Every <c>com.riotgames.platform.*</c> name of the Platform is defined here and only here.
/// </summary>
public static class AirProtocolNames
{
    public const string AccountSummary = "com.riotgames.platform.account.AccountSummary";
    public const string BroadcastMessage = "com.riotgames.platform.broadcast.BroadcastMessage";
    public const string BroadcastNotification = "com.riotgames.platform.broadcast.BroadcastNotification";
    public const string ChampionDTO = "com.riotgames.platform.catalog.champion.ChampionDTO";
    public const string ChampionSkinDTO = "com.riotgames.platform.catalog.champion.ChampionSkinDTO";
    public const string RuneQuantity = "com.riotgames.platform.catalog.runes.RuneQuantity";
    public const string RuneType = "com.riotgames.platform.catalog.runes.RuneType";
    public const string Spell = "com.riotgames.platform.catalog.spell.Spell";
    public const string LoginDataPacket = "com.riotgames.platform.clientfacade.domain.LoginDataPacket";
    public const string BotParticipant = "com.riotgames.platform.game.BotParticipant";
    public const string ChampionBanInfoDTO = "com.riotgames.platform.game.ChampionBanInfoDTO";
    public const string FailedJoinPlayer = "com.riotgames.platform.game.FailedJoinPlayer";
    public const string GameDTO = "com.riotgames.platform.game.GameDTO";
    public const string GameException = "com.riotgames.platform.game.GameException";
    public const string GameFullException = "com.riotgames.platform.game.GameFullException";
    public const string GameModeNotSupportedException = "com.riotgames.platform.game.GameModeNotSupportedException";
    public const string GameNameNotUniqueException = "com.riotgames.platform.game.GameNameNotUniqueException";
    public const string GameNotDraftModeTurnException = "com.riotgames.platform.game.GameNotDraftModeTurnException";
    public const string GameNotFoundException = "com.riotgames.platform.game.GameNotFoundException";
    public const string GameReconnectInfoDTO = "com.riotgames.platform.game.GameReconnectInfoDTO";
    public const string GameTypeConfigDTO = "com.riotgames.platform.game.GameTypeConfigDTO";
    public const string IncorrectPasswordException = "com.riotgames.platform.game.IncorrectPasswordException";
    public const string InvalidGameStateException = "com.riotgames.platform.game.InvalidGameStateException";
    public const string NotEnoughPlayersException = "com.riotgames.platform.game.NotEnoughPlayersException";
    public const string PlayerAlreadyInGameException = "com.riotgames.platform.game.PlayerAlreadyInGameException";
    public const string PlayerChampionSelectionDTO = "com.riotgames.platform.game.PlayerChampionSelectionDTO";
    public const string PlayerCredentialsDto = "com.riotgames.platform.game.PlayerCredentialsDto";
    public const string PlayerParticipant = "com.riotgames.platform.game.PlayerParticipant";
    public const string StaleGameVersionException = "com.riotgames.platform.game.StaleGameVersionException";
    public const string StartChampSelectDTO = "com.riotgames.platform.game.StartChampSelectDTO";
    public const string TeamFullException = "com.riotgames.platform.game.TeamFullException";
    public const string TooManyPlayersException = "com.riotgames.platform.game.TooManyPlayersException";
    public const string UserBannedException = "com.riotgames.platform.game.UserBannedException";
    public const string GameMap = "com.riotgames.platform.game.map.GameMap";
    public const string GameMapEnabledDTO = "com.riotgames.platform.game.map.GameMapEnabledDTO";
    public const string GameNotification = "com.riotgames.platform.game.message.GameNotification";
    public const string PracticeGameSearchResult = "com.riotgames.platform.game.practice.PracticeGameSearchResult";
    public const string RuneCombiner = "com.riotgames.platform.inventory.loot.RuneCombiner";
    public const string InvalidSessionException = "com.riotgames.platform.login.InvalidSessionException";
    public const string LoginFailedException = "com.riotgames.platform.login.LoginFailedException";
    public const string Session = "com.riotgames.platform.login.Session";
    public const string ClientVersionMismatchException = "com.riotgames.platform.login.impl.ClientVersionMismatchException";
    public const string GameQueueConfig = "com.riotgames.platform.matchmaking.GameQueueConfig";
    public const string MatchmakingException = "com.riotgames.platform.matchmaking.MatchmakingException";
    public const string QueueDisabled = "com.riotgames.platform.matchmaking.QueueDisabled";
    public const string QueueDodger = "com.riotgames.platform.matchmaking.QueueDodger";
    public const string QueueInfo = "com.riotgames.platform.matchmaking.QueueInfo";
    public const string QueueThrottled = "com.riotgames.platform.matchmaking.QueueThrottled";
    public const string SearchingForMatchNotification = "com.riotgames.platform.matchmaking.SearchingForMatchNotification";
    public const string MessagingPrefix = "com.riotgames.platform.messaging.";
    public const string StoreAccountBalanceNotification = "com.riotgames.platform.messaging.StoreAccountBalanceNotification";
    public const string StoreFulfillmentNotification = "com.riotgames.platform.messaging.StoreFulfillmentNotification";
    public const string UnexpectedServiceException = "com.riotgames.platform.messaging.UnexpectedServiceException";
    public const string AggregatedStat = "com.riotgames.platform.statistics.AggregatedStat";
    public const string AggregatedStats = "com.riotgames.platform.statistics.AggregatedStats";
    public const string AggregatedStatsKey = "com.riotgames.platform.statistics.AggregatedStatsKey";
    public const string ChampionStatInfo = "com.riotgames.platform.statistics.ChampionStatInfo";
    public const string EndOfGameStats = "com.riotgames.platform.statistics.EndOfGameStats";
    public const string FellowPlayerInfo = "com.riotgames.platform.statistics.FellowPlayerInfo";
    public const string PlayerGameStats = "com.riotgames.platform.statistics.PlayerGameStats";
    public const string PlayerLifetimeStats = "com.riotgames.platform.statistics.PlayerLifetimeStats";
    public const string PlayerParticipantStatsSummary = "com.riotgames.platform.statistics.PlayerParticipantStatsSummary";
    public const string PlayerStatSummaries = "com.riotgames.platform.statistics.PlayerStatSummaries";
    public const string PlayerStatSummary = "com.riotgames.platform.statistics.PlayerStatSummary";
    public const string PointsPenalty = "com.riotgames.platform.statistics.PointsPenalty";
    public const string RawStat = "com.riotgames.platform.statistics.RawStat";
    public const string RawStatDTO = "com.riotgames.platform.statistics.RawStatDTO";
    public const string RecentGames = "com.riotgames.platform.statistics.RecentGames";
    public const string SummaryAggStat = "com.riotgames.platform.statistics.SummaryAggStat";
    public const string SummaryAggStats = "com.riotgames.platform.statistics.SummaryAggStats";
    public const string AllPublicSummonerDataDTO = "com.riotgames.platform.summoner.AllPublicSummonerDataDTO";
    public const string AllSummonerData = "com.riotgames.platform.summoner.AllSummonerData";
    public const string BasePublicSummonerDTO = "com.riotgames.platform.summoner.BasePublicSummonerDTO";
    public const string PublicSummoner = "com.riotgames.platform.summoner.PublicSummoner";
    public const string RuneSlot = "com.riotgames.platform.summoner.RuneSlot";
    public const string Summoner = "com.riotgames.platform.summoner.Summoner";
    public const string SummonerAssociatedTalent = "com.riotgames.platform.summoner.SummonerAssociatedTalent";
    public const string SummonerAssociatedTalentPK = "com.riotgames.platform.summoner.SummonerAssociatedTalentPK";
    public const string SummonerCatalog = "com.riotgames.platform.summoner.SummonerCatalog";
    public const string SummonerDefaultSpells = "com.riotgames.platform.summoner.SummonerDefaultSpells";
    public const string SummonerLevel = "com.riotgames.platform.summoner.SummonerLevel";
    public const string SummonerLevelAndPoints = "com.riotgames.platform.summoner.SummonerLevelAndPoints";
    public const string SummonerSummary = "com.riotgames.platform.summoner.SummonerSummary";
    public const string SummonerTalentsAndPoints = "com.riotgames.platform.summoner.SummonerTalentsAndPoints";
    public const string Talent = "com.riotgames.platform.summoner.Talent";
    public const string TalentGroup = "com.riotgames.platform.summoner.TalentGroup";
    public const string TalentRow = "com.riotgames.platform.summoner.TalentRow";
    public const string SummonerActiveBoostsDTO = "com.riotgames.platform.summoner.boost.SummonerActiveBoostsDTO";
    public const string SummonerRune = "com.riotgames.platform.summoner.runes.SummonerRune";
    public const string SummonerRuneInventory = "com.riotgames.platform.summoner.runes.SummonerRuneInventory";
    public const string SlotEntry = "com.riotgames.platform.summoner.spellbook.SlotEntry";
    public const string SpellBook = "com.riotgames.platform.summoner.spellbook.SpellBook";
    public const string SpellBookPage = "com.riotgames.platform.summoner.spellbook.SpellBookPage";
    public const string ClientSystemStatesNotification = "com.riotgames.platform.systemstate.ClientSystemStatesNotification";
}
