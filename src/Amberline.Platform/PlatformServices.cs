using System.Collections;
using System.Globalization;
using Amberline.Host;
using RtmpSharp.IO;
using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>Patch-126 RPC boundary. No fallback success for unimplemented mutations.</summary>
public sealed class PlatformServices
{
    private readonly PlatformAccounts accounts;
    private readonly PlatformSessions sessions;
    private readonly PlatformGames games;
    private readonly string storeOrigin;

    public PlatformServices(PlatformAccounts accounts, PlatformSessions sessions, PlatformGames games,
        int httpPort = 8390)
    {
        this.accounts = accounts;
        this.sessions = sessions;
        this.games = games;
        if (httpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpPort));
        // StoreController appends /store/... to UrlVo.baseUrl, rather than
        // resolving it against an entry-page URI. The canonical URL is origin-only.
        storeOrigin = $"http://127.0.0.1:{httpPort}";
        games.AccountForConnection = sessions.AccountForConnection;
    }

    /// <summary>The host's current ticker message (<see cref="AirDtos.BroadcastNotification"/>), handed to every later login too; null when cleared.</summary>
    public AsObject? Broadcast { get; set; }

    public void Disconnect(string connection)
    {
        var account = sessions.AccountForConnection(connection);
        sessions.Logout(connection);
        if (account is long id) games.CancelQueue(id, connection);
    }

    /// <summary>
    /// The RTMP connection closed without a logout. The login session is parked for a brief reconnect
    /// (<see cref="PlatformSessions.Suspend"/>); the queue entry is not, because it is bound to the connection.
    /// </summary>
    public void ConnectionClosed(string connection)
    {
        var account = sessions.AccountForConnection(connection);
        sessions.Suspend(connection);
        if (account is long id) games.CancelQueue(id, connection);
    }

    // Matching becomes eligible only after the RTMP acknowledgement has been
    // queued on this connection, so AIR enters its waiting state before a push.
    public void ResponseSent(string connection, string destination, string operation)
    {
        if (destination == "matchmakerService" && operation is
            "attachToQueue" or "attachToQueues" or "attachTeamToQueue" or "attachTeamToQueues")
            games.ConfirmQueueReply(connection);
    }

    public object? Route(string connection, string destination, string operation, object?[] args)
    {
        object? A(int index) => index < args.Length ? args[index] : throw new ArgumentException("Missing RPC argument.");
        int N(int index) => Integer(A(index));
        string S(int index) => Text(A(index));
        if (destination == "loginService" && operation == "login")
        {
            var credentials = Fields(A(0));
            // LoginView falls back to a login without a queue ticket when the queue answers anything but 403; a plain
            // validation fault there never reaches its handler and leaves the login screen busy.
            if (Field(credentials, "authToken") is not string { Length: > 0 } ticket) throw AirFault.BypassedQueue();
            var session = sessions.Login(connection, Text(Field(credentials, "username")),
                ticket, Text(Field(credentials, "clientVersion")));
            return AirDtos.Login(accounts.Get(session.AccountId), session);
        }
        var current = sessions.Require(connection);
        var account = accounts.Get(current.AccountId);
        // Reconstruction: the stock profile issues its stats reads with profileAcctId, which is NaN before
        // the summoner data arrives and 0 once SummonerProfileController.deactivate() has run. Changing
        // tabs quickly leaves such reads in flight, and every one became a Validation fault (a service
        // error on the guest's client, 17 Oct 2026 platform log). Retail's answer for an account that
        // does not exist is not recorded here (the native answer is unknown): answer with the empty record a new account would get.
        PlatformAccount StatsAccount()
        {
            long id;
            try { id = N(0); } catch (ArgumentException) { id = 0; }
            return accounts.Find(id) ?? PlatformAccounts.Unknown(id);
        }
        void OwnSummoner(int index = 0)
        {
            if (N(index) != account.SummonerId) throw new ArgumentException("This summoner does not belong to your session.");
        }
        // NOT RETAIL (batch 748; retail had no host-side delete): a player the host deleted (a match history still names them) is read as an empty record under the names they had, as
        // StatsAccount answers the stats reads; an id no login ever had fails as before.
        PlatformAccount ByAccount(int id) => accounts.Find(id) ?? accounts.FormerAccount(id) ?? throw new ArgumentException("Account does not exist.");
        PlatformAccount BySummoner(int id) => accounts.FindSummoner(id) ?? accounts.FormerSummoner(id) ?? throw new ArgumentException("Summoner not found.");
        try
        {
            switch (destination, operation)
            {
                case ("loginService", "performLCDSHeartBeat"):
                    // This build sends a fixed validation marker rather than
                    // the session token. It still renews the authenticated socket.
                    if (N(0) == 0 && S(1) == "validate-lcds-connection-4-invite")
                        sessions.Heartbeat(connection, account.Id, current.Token);
                    else sessions.Heartbeat(connection, N(0), S(1));
                    return true;
                case ("loginService", "logout"):
                    if (!PlatformSessions.SecretEquals(current.Token, S(0))) throw new ArgumentException("Invalid session token.");
                    Disconnect(connection);
                    return null;
                case ("loginService", "getLoggedInAccountView"):
                    if (!PlatformSessions.SecretEquals(current.Token, S(0))) throw new ArgumentException("Invalid session token.");
                    return AirDtos.Login(account, current)["accountSummary"];
                case ("loginService", "getStoreUrl"):
                    return storeOrigin + "?sessionId=" + sessions.IssueStoreTicket(connection);
                case ("clientFacadeService", "reportPlayer"):
                    // NOT RETAIL: retail sent the PlayerComplaint (reporter, reported summoner, offense, comment, game) to the
                    // Tribunal; here the host reviews it on /manage (owner, 1 November 2026). The reporter is the session's own
                    // account and summoner, not the client's reportingSummonerId. The comment is the player's untrusted text:
                    // stored capped and plain, never logged (the operation line names no argument), HTML-encoded on the page.
                    // ReportPlayerView.sendReport registers no handler and shows its own confirmation; ClientSystemStates sends
                    // tribunalEnabled=true so the stock DialogController opens the dialog.
                    var complaint = Fields(A(0));
                    accounts.RecordReport(account.Id, Integer(Field(complaint, "reportedSummonerId")), Text(Field(complaint, "offense")),
                        Integer(Field(complaint, "gameId")), complaint.TryGetValue("comment", out var comment) ? comment as string : null);
                    return null;
                case ("clientFacadeService", "getLoginDataPacketForUser"):
                    var loginData = AirDtos.LoginData(account, games.Reconnect(account.Id), accounts.EconomyRules);
                    loginData["playerStatSummaries"] = AirProfileDtos.PlayerStatSummaries(account, accounts.ResultsFor(account.Id));
                    // Batch 587: a level above 0 makes the stock client show its leaver warning at this login (checkLeaverPenaltyPopup).
                    loginData["leaverPenaltyLevel"] = accounts.LeaverStatus(account.Id).Level;
                    loginData["broadcastNotification"] = Broadcast;
                    return loginData;
                case ("gameMapService", "getGameMapList"):
                    return AirDtos.Maps();
                case ("gameMapService", "getGameMapSet"):
                    return List(PlatformStoreCalendar.OpenMapIds
                        .Select(id => Object(AirProtocolNames.GameMapEnabledDTO, ("gameMapId", id), ("minPlayers", 1))).ToArray());
                case ("inventoryService", "getAvailableChampions"):
                    return AirDtos.Champions(account, accounts.EconomyRules);
                case ("inventoryService", "getSumonerActiveBoosts"):
                    return AirDtos.ActiveBoosts(account);
                case ("inventoryService", "isStoreEnabled"):
                    return true;
                case ("matchmakerService", "isMatchmakingEnabled"):
                    return true;
                case ("matchmakerService", "getAvailableQueues"):
                    return AirDtos.QueueCatalog();
                case ("matchmakerService", "attachToQueue"):
                case ("matchmakerService", "attachToQueues"):
                case ("matchmakerService", "attachTeamToQueue"):
                case ("matchmakerService", "attachTeamToQueues"):
                    var parameters = Fields(A(0));
                    var queueIds = Items(Field(parameters, "queueIds")).Select(Integer).ToArray();
                    // A party leader sends attachTeamToQueue with the team's summoner ids and the invitation id (joinQueue_attach).
                    var team = parameters.TryGetValue("team", out var members) && members is not null
                        ? Items(members).Select(member => (long)Integer(member)).ToArray() : null;
                    var invitationId = parameters.TryGetValue("invitationId", out var invitation) && invitation is not null ? Text(invitation) : null;
                    var difficulty = parameters.TryGetValue("botDifficulty", out var botDifficulty) && botDifficulty is not null
                        ? Text(botDifficulty) : null;
                    return games.AttachQueue(account, connection, queueIds, difficulty, team, invitationId);
                case ("matchmakerService", "acceptInviteForMatchmakingGame"):
                    // The invitee's accept of a party invitation (batch 579); its success handler reads only the call's token.
                    games.AcceptInvite(account, connection, S(0));
                    return null;
                case ("matchmakerService", "cancelFromQueueIfPossible"):
                    if (PlatformGames.QueueOf(N(0)) is null) throw new ArgumentException("This queue is not enabled.");
                    return games.CancelQueue(account.Id, connection);
                case ("matchmakerService", "purgeFromQueues"):
                    games.CancelQueue(account.Id, connection);
                    return null;
                case ("matchmakerService", "getQueueInfo"):
                    if (PlatformGames.QueueOf(N(0)) is null) throw new ArgumentException("This queue is not enabled.");
                    return games.QueueInfo(N(0));
                case ("inventoryService", "getAllRuneCombiners"):
                    // Retail answers the recipe list to every account; the client's combiner view is built from it, so an
                    // empty list leaves its Combine button doing nothing at all. A refusal on a combine is answered in the
                    // shape InventoryServiceImpl.useRuneCombiner registers a handler for (see useRuneCombiner below).
                    return List(PlatformRuneCombiner.All.Select(PlatformRuneCombiner.ToAir));
                case ("inventoryService", "useGrabBag"):
                    throw new ArgumentException("Free rune gifts are disabled. Purchase runes in the Store or combine spare runes.");
                case ("inventoryService", "useRuneCombiner"):
                    // The client's only handler for this call is registered for UnexpectedServiceException
                    // (InventoryServiceImpl.useRuneCombiner); RuneCombinerController.onRuneCombineError reads
                    // ServerError.errorCode and .exception off the rootCause, and a plain fault leaves both null, so the
                    // handler ended in TypeError #1009 with its alert half drawn. Every refusal below, the malformed
                    // input included, answers the shape the client handles.
                    int awardedRune;
                    try
                    {
                        var runeIds = Items(A(1)).Select(item => Integer(Field(Fields(item), "itemId"))).ToArray();
                        awardedRune = games.UseRuneCombiner(account.Id, N(0), runeIds);
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                    { throw AirFault.Unexpected(exception.Message); }
                    // RuneBookController applies this delta and removes its input
                    // copies. An additional store push would award it twice.
                    return List([Object(AirProtocolNames.RuneQuantity,
                        ("runeId", awardedRune), ("quantity", 1))]);
                case ("summonerService", "getSocialNetworkFriends"):
                    return List([]);
                case ("summonerService", "getSummonerCatalog"):
                    return AirDtos.SummonerCatalog();
                case ("summonerService", "saveSummoner"):
                    var summoner = Fields(A(0));
                    if (Integer(Field(summoner, "sumId")) != account.SummonerId ||
                        Integer(Field(summoner, "summonerId")) != account.SummonerId ||
                        (summoner.TryGetValue("acctId", out var owner) && Integer(owner) != account.Id))
                        throw new ArgumentException("This summoner does not belong to your session.");
                    if (Field(summoner, "tutorialFlag") is not bool tutorial || Field(summoner, "helpFlag") is not bool help)
                        throw new ArgumentException("Invalid summoner preference flags.");
                    // Identity, permissions and progression remain server-owned. The
                    // stock sealed Summoner class sends no acctId on this path.
                    games.SaveProfileIcon(account.Id, Integer(Field(summoner, "profileIconId")), tutorial, help);
                    return AirDtos.Summoner(accounts.Get(account.Id));
                case ("summonerService", "updateProfileIconId"):
                    games.SaveProfileIcon(account.Id, N(0));
                    return null;
                case ("summonerService", "getSummoner"):
                    var bySummoner = BySummoner(N(0));
                    return AirDtos.Summoner(bySummoner, isMe: bySummoner.Id == account.Id);
                case ("summonerService", "getSummonerByAccountId"):
                    var byAccount = ByAccount(N(0));
                    return AirDtos.Summoner(byAccount, isMe: byAccount.Id == account.Id);
                case ("summonerService", "getSummonerByName"):
                    return accounts.FindName(S(0)) is { } found ? AirDtos.PublicSummoner(found) : null;
                case ("summonerService", "getSummonerInternalNameByName"):
                    return accounts.FindName(S(0)) is { } named ? AirGameDtos.InternalName(named) : null;
                case ("statisticsService", "getSummonerSummaryByInternalName"):
                    if (accounts.FindInternalName(S(0)) is not { } friend) return null;
                    var summary = AirDtos.SummonerSummary(friend);
                    var stats = AirProfileDtos.PlayerStatSummaries(friend, accounts.ResultsFor(friend.Id));
                    summary["playerStatSummaries"] = stats;
                    var unranked = Items(stats["playerStatSummarySet"]).Select(Fields)
                        .Single(row => Text(row["playerStatSummaryType"]) == "Unranked");
                    foreach (var field in new[] { "wins", "losses", "leaves" }) summary[field] = unranked[field];
                    return summary;
                case ("summonerService", "checkSummonerName"):
                    return accounts.FindName(S(0)) is null;
                case ("summonerService", "getAllSummonerData"):
                    OwnSummoner();
                    return AirDtos.AllSummonerData(account, accounts.EconomyRules);
                case ("summonerService", "getAllSummonerDataByAccount"):
                    if (N(0) != account.Id) throw new ArgumentException("This account does not belong to your session.");
                    return AirDtos.AllSummonerData(account, accounts.EconomyRules);
                case ("summonerService", "getAllPublicSummonerDataByAccount"):
                    return AirDtos.PublicSummonerData(ByAccount(N(0)), accounts.EconomyRules);
                case ("summonerService", "getSummonerNames"):
                    // NOT RETAIL (batch 748; retail had no host-side delete): a match history's fellow player whom the host deleted keeps the name they had.
                    return List(Items(A(0)).Select(x => accounts.FindSummoner(Integer(x))?.Summoner ?? accounts.FindDeletedSummoner(Integer(x))?.Summoner ?? ""));
                case ("summonerService", "getPublicSummonersByAccountIds"):
                    return List(Items(A(0)).Select(x => AirDtos.PublicSummoner(ByAccount(Integer(x)))));
                case ("summonerService", "changeTalentRankings"):
                    OwnSummoner();
                    var ranks = Fields(A(1)).ToDictionary(p => ParseId(p.Key), p => Integer(p.Value));
                    games.SaveMasteries(account.Id, ranks);
                    return AirDtos.Talents(accounts.Get(account.Id));
                case ("summonerService", "resetTalents"):
                    OwnSummoner();
                    games.SaveMasteries(account.Id, []);
                    return AirDtos.Talents(accounts.Get(account.Id));
                case ("summonerRuneService", "getSummonerRuneInventory"):
                case ("summonerRuneService", "getSummonerRunes"):
                    OwnSummoner();
                    return AirDtos.RuneInventory(account);
                case ("spellBookService", "getSpellBook"):
                    return AirDtos.SpellBook(BySummoner(N(0)));
                case ("spellBookService", "saveSpellBook"):
                    return SaveRunes(account, A(0));
                case ("spellBookService", "selectDefaultSpellBookPage"):
                    var page = Fields(A(0));
                    var pageId = Integer(Field(page, "pageId"));
                    games.SelectRunePage(account.Id, Integer(Field(page, "summonerId")), pageId);
                    // RuneBookController consumes ResultEvent.result as a page.
                    // Champion selection receives the ResultEvent wrapper and
                    // coerces it to Boolean, so the same typed result serves both.
                    return AirDtos.SpellBookPage(accounts.Get(account.Id),
                        accounts.GetRunePages(account.Id).Single(p => p.PageId == pageId));
                case ("playerStatsService", "retrievePlayerStatsByAccountId"):
                    var lifetimeOwner = StatsAccount();
                    return AirProfileDtos.LifetimeStats(lifetimeOwner, accounts.ResultsFor(lifetimeOwner.Id));
                case ("playerStatsService", "retrieveTopPlayedChampions"):
                    return AirProfileDtos.TopPlayedChampions(StatsAccount(), S(1), accounts.ResultsFor(StatsAccount().Id));
                case ("playerStatsService", "getAggregatedStats"):
                    return AirProfileDtos.AggregatedStats(StatsAccount(), S(1), accounts.ResultsFor(StatsAccount().Id), S(2));
                case ("playerStatsService", "getRecentGames"):
                    return AirGameDtos.RecentGames(StatsAccount(), accounts.ResultsFor(StatsAccount().Id));
                case ("gameService", "listAllPracticeGames"):
                    return games.List();
                case ("gameService", "createTutorialGame"):
                    // Batch 580: 0 is the Basic Tutorial (CreateTutorialGameController.as:75), a one-player map-3 game. Batch 669: 1 is
                    // Battle Training, Summoner's Rift in TUTORIAL with bots (PlatformGames.CreateBattleTraining).
                    return N(0) switch
                    {
                        0 => games.CreateTutorial(account),
                        1 => games.CreateBattleTraining(account),
                        _ => throw AirFault.ModeNotSupported("this server"),
                    };
                case ("gameService", "createPracticeGame"):
                    return games.Create(account, S(0), A(5) is null ? "" : S(5), Integer(Field(Fields(A(2)), "mapId")), N(3), N(4), S(1), S(6));
                case ("gameService", "joinGame"):
                    return games.Join(account, N(0), A(1) is null ? "" : S(1));
                case ("gameService", "getGameForUser"):
                    return games.Current(account.Id);
                case ("gameService", "getGame"):
                    return games.Get(account.Id, N(0));
                case ("gameService", "getLatestGameTimerState"):
                    // The champion-select tickle sends it with no error handler after its timer ran out; a game the player
                    // is no longer in is a null answer (the handler checks 'result as GameDTO'), not an error window.
                    return games.Latest(account.Id, N(0));
                case ("gameService", "switchTeams"):
                    return games.SwitchTeam(account.Id, N(0)); // Boolean, see PlatformGames.SwitchTeam
                case ("gameService", "startChampionSelection"):
                    games.Start(account.Id, N(0), N(1));
                    return Object(AirProtocolNames.StartChampSelectDTO, ("invalidPlayers", List([])));
                case ("gameService", "selectChampion"):
                    return games.Pick(account.Id, N(0));
                case ("gameService", "cancelSelectChampion"):
                    return games.ClearPick(account.Id);
                case ("gameService", "selectChampionSkin"):
                    return games.SelectSkin(account.Id, N(0), N(1));
                case ("gameService", "selectSpells"):
                    games.SelectSpells(account.Id, N(0), N(1));
                    return true;
                case ("gameService", "championSelectCompleted"):
                    games.LockIn(account.Id);
                    return null;
                // Draft Mode (batch 543): ChampionSelectionController and ChampionTradeController; the stock result handlers
                // (onGetBannableChampionsSuccess, onBanSucccess, onBanCompleteSuccess, onTradeSuccess) read only the ban list.
                case ("gameService", "getChampionsForBan"):
                    return games.ChampionsForBan(account.Id);
                case ("gameService", "banChampion"):
                    games.Ban(account.Id, N(0));
                    return null;
                case ("gameService", "banChampionCompleted"):
                    // Reconstruction: the client sends it right after each banChampion answer (onBanSucccess); the ban already
                    // ended the turn (PlatformGames.Ban), so this only acknowledges. It has no error handler.
                    return null;
                case ("gameService", "tradeChampion"):
                    games.Trade(account.Id, N(0));
                    return null;
                case ("gameService", "selectBotChampion"):
                    var botToAdd = Fields(A(1));
                    RequireBotChampion(botToAdd, N(0));
                    // The client localizes the difficulty name itself; keep its text for the lobby row.
                    games.AddBot(account.Id, N(0), BotTeam(botToAdd), Integer(Field(botToAdd, "botSkillLevel")),
                        botToAdd.TryGetValue("botSkillLevelName", out var difficultyName) && difficultyName is string text &&
                            text.Length is > 0 and <= 32 && !text.Any(char.IsControl) ? text : null);
                    return null;
                case ("gameService", "removeBotChampion"):
                    var botToRemove = Fields(A(1));
                    RequireBotChampion(botToRemove, N(0));
                    games.RemoveBot(account.Id, N(0), BotTeam(botToRemove));
                    return null;
                case ("gameService", "quitGame"):
                    games.Quit(account.Id);
                    return null;
                case ("gameService", "banUserFromGame"):
                    games.Kick(account.Id, N(0), N(1));
                    return null;
                case ("gameService", "retrieveInProgressGameInfo"):
                    return games.Reconnect(account.Id);
                case ("gameService", "setClientReceivedGameMessage"):
                case ("gameService", "setClientReceivedMaestroMessage"):
                    // Fire-and-forget audit pings: every stock call site passes no error handler, so a refusal here
                    // would only raise an error window, so any game id is acknowledged. Only the champion grid's CHAMP_SELECT_CLIENT
                    // changes state: it is what spares a leaver whose grid never loaded from the dodge timer (batch 589).
                    if (S(1).Length > 64) throw new ArgumentException("Invalid acknowledgement.");
                    if (operation == "setClientReceivedGameMessage" && S(1) == "CHAMP_SELECT_CLIENT") games.AcknowledgeChampionSelect(account.Id, N(0));
                    return null;
                default:
                    throw new ArgumentException($"{destination}.{operation} is not implemented in this testing platform.");
            }
        }
        catch (RemoteFault fault) { throw new ArgumentException(fault.Message, fault); }
    }

    // BotParticipant.teamId is the String form of Team.TEAM_ID_BLUE (100) or TEAM_ID_PURPLE (200).
    private static Amberline.Core.Team BotTeam(IDictionary<string, object> bot) => Text(Field(bot, "teamId")) switch
    {
        "100" => Amberline.Core.Team.Order,
        "200" => Amberline.Core.Team.Chaos,
        _ => throw new ArgumentException("Invalid bot team.")
    };

    // The stock client sends the champion id and the BotParticipant it built for that champion.
    private static void RequireBotChampion(IDictionary<string, object> bot, int championId)
    {
        if (bot.TryGetValue("champion", out var champion) && champion is IDictionary<string, object> fields &&
            fields.TryGetValue("championId", out var id) && Integer(id) != championId)
            throw new ArgumentException("The bot does not match its champion.");
    }

    private object SaveRunes(PlatformAccount account, object? value)
    {
        var book = Fields(value);
        if (Integer(Field(book, "summonerId")) != account.SummonerId) throw new ArgumentException("This rune book is not yours.");
        var pages = Items(Field(book, "spellBookPages")).Select(item =>
        {
            var page = Fields(item);
            if (Integer(Field(page, "summonerId")) != account.SummonerId)
                throw new ArgumentException("This rune page is not yours.");
            if (Field(page, "isCurrent") is not bool isCurrent)
                throw new ArgumentException("Invalid current rune page flag.");
            var slots = new Dictionary<int, int>();
            foreach (var slotItem in Items(Field(page, "slotEntries")))
            {
                var entry = Fields(slotItem);
                var slot = Integer(Field(entry, "runeSlotId"));
                if (slot is < 1 or > 30 || !slots.TryAdd(slot, Integer(Field(entry, "runeId"))))
                    throw new ArgumentException("Invalid or duplicate rune slot.");
            }
            return new PlatformRunePage(Integer(Field(page, "pageId")), Text(Field(page, "name")),
                isCurrent, slots, DateTime.UnixEpoch);
        }).ToArray();
        games.SaveRunePages(account.Id, account.SummonerId, pages);
        return AirDtos.SpellBook(accounts.Get(account.Id));
    }

    public static IDictionary<string, object> Fields(object? value) => value as IDictionary<string, object>
        ?? throw new ArgumentException("Expected an AMF object.");
    public static object? Field(IDictionary<string, object> value, string key) => value.TryGetValue(key, out var result)
        ? result : throw new ArgumentException($"Missing field {key}.");
    public static string Text(object? value) => value as string ?? throw new ArgumentException("Expected a string.");
    public static int Integer(object? value)
    {
        if (value is not (int or uint or long or ulong or double or float)) throw new ArgumentException("Expected an integer.");
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new ArgumentException("Integer outside the supported range.");
        return (int)number;
    }
    private static int ParseId(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id : throw new ArgumentException("Invalid mastery ID.");
    public static object?[] Items(object? value) => value is IList list && list.Count <= 512
        ? list.Cast<object?>().ToArray() : throw new ArgumentException("Expected a bounded AMF collection.");
}
