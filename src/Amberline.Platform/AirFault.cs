using RtmpSharp.IO;

namespace Amberline.Platform;

/// <summary>
/// A refusal the stock AIR client turns into its own localized message. Retail's platform threw Java
/// PlatformException subclasses and the RTMP error carried them as <c>rootCause</c>: the client's
/// <c>LcdsErrorHandler</c> routes on that object's class (every game <c>*Exception</c> alias of
/// <see cref="AirProtocolNames"/> is registered to <c>GameException</c>, which each <c>GameServiceImpl</c> call site listens for) and
/// <c>ServerError.errorCode</c>/<c>messageArguments</c> read its <c>errorCode</c> and
/// <c>substitutionArguments</c>. The error code is the client's resource key (<c>PG-0004</c> and so on, texts in
/// <c>resources-en_US.swf</c>). An error without that object ends in the generic "unknown remote service
/// error" dialog and the call site's handler never runs.
/// It derives from <see cref="ArgumentException"/> so every existing refusal path keeps its catch sites.
/// </summary>
public sealed class AirFault : ArgumentException
{
    public const string GameException = AirProtocolNames.GameException;
    public const string GameFull = AirProtocolNames.GameFullException;
    public const string GameNameNotUnique = AirProtocolNames.GameNameNotUniqueException;
    public const string GameNotFound = AirProtocolNames.GameNotFoundException;
    public const string IncorrectPassword = AirProtocolNames.IncorrectPasswordException;
    public const string InvalidGameState = AirProtocolNames.InvalidGameStateException;
    public const string PlayerAlreadyInGame = AirProtocolNames.PlayerAlreadyInGameException;
    public const string StaleGameVersion = AirProtocolNames.StaleGameVersionException;
    public const string TeamFull = AirProtocolNames.TeamFullException;
    public const string UserBanned = AirProtocolNames.UserBannedException;

    public const string UnexpectedService = AirProtocolNames.UnexpectedServiceException;
    public const string InvalidSessionClass = AirProtocolNames.InvalidSessionException;

    public string Alias { get; }
    public string ErrorCode { get; }
    public object?[] SubstitutionArguments { get; }

    public AirFault(string alias, string errorCode, string message, params object?[] arguments) : base(message)
    {
        Alias = alias;
        ErrorCode = errorCode;
        SubstitutionArguments = arguments;
    }

    /// <summary>The <c>rootCause</c> object: <c>PlatformException</c>'s three fields, typed by the Java class alias.</summary>
    public AsObject RootCause() => Alias == UnexpectedService
        // UnexpectedServiceException wraps any other server exception: message, the wrapped Java class and a stack trace.
        ? AirWire.Object(Alias, ("message", Message), ("rootCauseClassname", ErrorCode), ("stackTraceString", string.Empty))
        : AirWire.Object(Alias,
            ("errorCode", ErrorCode),
            ("substitutionArguments", SubstitutionArguments.ToArray()),
            ("rootCauseClassname", Alias));

    /// <summary>
    /// What retail answered once the session was gone: LcdsErrorHandler sees an UnexpectedServiceException whose
    /// <c>rootCauseClassname</c> is <c>ServerError.INVALID_SESSION_ERROR</c> and raises the client's "session expired,
    /// the application will now logout" dialog instead of the unknown-error window.
    /// </summary>
    public static AirFault InvalidSession() => new(UnexpectedService, InvalidSessionClass,
        "No authenticated platform session.");

    /// <summary>
    /// A refusal for a call whose only registered handler is <c>UnexpectedServiceException</c>
    /// (<c>InventoryServiceImpl.useRuneCombiner</c>). LcdsErrorHandler reaches that handler through
    /// <c>getClassByAlias(rootCauseClassname)</c>, so the wrapped class name must be the exception's own alias; any other
    /// root cause misses the map and becomes the client's generic unknown-error window, and an untyped fault leaves
    /// <c>ServerError.errorCode</c>/<c>exception</c> empty in the handler (TypeError #1009 in the rune combiner).
    /// </summary>
    public static AirFault Unexpected(string message) => new(UnexpectedService, UnexpectedService, message);

    // loginService.login registers handlers for these two (LoginServiceImpl); LoginView.onLoginError shows the
    // resource text of errorCode: LOGIN-0006 'Invalid username/password combination', LOGIN-0001 'Client version {0} is
    // rejected by server'.
    public static AirFault LoginFailed() => new(AirProtocolNames.LoginFailedException, "LOGIN-0006",
        "Login ticket is invalid or expired.");
    // LOGIN-0007 'The server is currently busy. Please try again later.' is the client's answer to a login that skipped the
    // queue (LoginMessageResource maps account_bypassed_login_queue to it); on it LoginView.onLoginError turns its queue-less
    // fallback off, so the next attempt goes through the queue again. Reconstruction: that retail sent it for a missing ticket.
    public static AirFault BypassedQueue() => new(AirProtocolNames.LoginFailedException, "LOGIN-0007",
        "Log in through the login queue.");
    public static AirFault VersionMismatch(string version) => new(
        AirProtocolNames.ClientVersionMismatchException, "LOGIN-0001",
        "This service requires the preserved patch-126 AIR client.", version);

    // The codes below are the client's own MessageDictionary keys; the game ones read in the resource bundle as:
    // PG-0002 "The game has been changed by another player", PG-0003/PG-0018 teams locked, PG-0004 team full,
    // PG-0005/PG-0015 cannot summon, PG-0006/PG-0017 cannot unsummon, PG-0007/PG-0016 cannot set spells,
    // PG-0008/PG-0012 already in a game, PG-0009/PG-0011 game full, PG-0010 password, PG-0013 name not unique,
    // PG-0014 game not found, PG-0020 not allowed to join, PG-0021 banned, PG-0022 champion not owned.
    public static AirFault Stale() => new(StaleGameVersion, "PG-0002",
        "The game has been changed by another player. Please retry.");
    public static AirFault TeamsLocked() => new(InvalidGameState, "PG-0018",
        "Teams are locked: the game is already selecting champions.");
    public static AirFault TeamIsFull() => new(TeamFull, "PG-0004", "That team is full.");
    public static AirFault SelectionClosed() => new(InvalidGameState, "PG-0015",
        "Unable to select a champion: the game is already starting.");
    public static AirFault UnselectClosed() => new(InvalidGameState, "PG-0017",
        "Unable to deselect a champion: the game is already starting.");
    public static AirFault SpellsClosed() => new(InvalidGameState, "PG-0016",
        "Unable to select spells: the game is already starting.");
    // The texts read "You cannot join/create game {0} because you are already in game {0} [{1}]".
    public static AirFault AlreadyInGame(bool joining, string gameName, long existingId,
        string message = "Leave your current game first.") => new(PlayerAlreadyInGame,
        joining ? "PG-0008" : "PG-0012", message, gameName, existingId);
    public static AirFault IsFull(string gameName) => new(GameFull, "PG-0009",
        "This game is full.", gameName);
    public static AirFault GameIsFull() => new(GameFull, "PG-0011", "The game is full.");
    public static AirFault WrongPassword() => new(IncorrectPassword, "PG-0010", "Incorrect game password.");
    public static AirFault NameNotUnique(string gameName) => new(GameNameNotUnique, "PG-0013",
        "That game name is already in use.", gameName);
    public static AirFault NoGame() => new(GameNotFound, "PG-0014", "Game does not exist.");
    // PG-0039 'This Game Mode is not supported on {0}'.
    public static AirFault ModeNotSupported(string platform, string message = "That game mode is not available on this server.") =>
        new(AirProtocolNames.GameModeNotSupportedException, "PG-0039", message, platform);
    // createPracticeGame refusals (GameServiceImpl.createGame registers GameException; CreatePracticeGameView.onCreateGameError
    // shows errorCode with messageArguments). PG-0037 'Unable to create game.  Custom game has a maximum of {0} players.',
    // PG-0027 'Due to high server demand, custom games temporarily require a minimum of {0} players to start.',
    // PG-0036 'Spectating is disabled for this game.'
    public static AirFault TooManySlots(int maximum) => new(AirProtocolNames.TooManyPlayersException, "PG-0037",
        "Too many player slots for a custom game.", maximum);
    public static AirFault TooFewSlots(int minimum) => new(AirProtocolNames.NotEnoughPlayersException, "PG-0027",
        "Too few player slots for a custom game.", minimum);
    public static AirFault SpectatorsDisabled() => new(InvalidGameState, "PG-0036", "Spectator setting refused: spectating is disabled.");
    // Reconstruction: no PG text names these; the keys are the client's own resource texts. GEN-0004 'An unexpected error has
    // occurred' (a name or password the stock form cannot produce, such as a control character) and the create view's
    // 'Custom game creation is temporarily disabled. Please try again later.' (the lobby cap).
    public static AirFault InvalidGameText(string message) => new(GameException, "GEN-0004", message);
    // matchmakerService.acceptInviteForMatchmakingGame registers only MatchmakingException (MatchMakerServiceImpl); its handler,
    // InviteController.handleSendMessageIfValidLCDS_failure, ignores the code and shows the client's own connection-failure
    // notice (GEN-0001), and the accept never reaches the inviter. The code is the queue-disabled text, for the log. The stock client
    // reaches it only by accepting while its own game runs (isInviteeValidToAcceptInvite blocks team select and the party lobby).
    public static AirFault InviteRefused(string message) => new(AirProtocolNames.MatchmakingException,
        "matchmaking_joinqueue_queue_disabled", message);
    public static AirFault CreationDisabled() => new(GameException, "practiceGame_createPracticeGame_disabled_popupText",
        "Lobby capacity reached.");
    public static AirFault NotJoinable() => new(InvalidGameState, "PG-0020", "This game cannot be joined.");
    public static AirFault Banned() => new(UserBanned, "PG-0021", "The owner has banned you from this game.");
    public static AirFault NotOwned() => new(GameException, "PG-0022",
        "This champion is not owned or in the free rotation.");
    // Draft Mode (batch 543). PG-0030 'Could not choose Champion, because it is no longer your turn.' is the client's
    // MessageDictionary.DRAFT_MODE_GAME_NOT_TURN / CANNOT_SUMMON_NOT_YOUR_TURN, and GameNotDraftModeTurnException is one of the
    // aliases GameException.as registers. selectChampion's GameException handler shows it (onChampionSelectError); banChampion
    // is sent without a handler, and an unhandled PlatformException with an errorCode shows that code's text in a general alert
    // (ViewMediatorImpl/LolClient.handleUnhandledRemoteError).
    public static AirFault NotYourTurn() => new(AirProtocolNames.GameNotDraftModeTurnException, "PG-0030",
        "It is not your turn in the draft.");
    // PG-0031 is MessageDictionary.GAME_CHAMPION_BANNED; the shipped en_US bundle has no text for it. The stock grids never offer a
    // banned champion (isChampionAvailableForChoosing) or a champion a bot holds for a ban (ChampionsForBan), so only a
    // non-stock request meets it.
    public static AirFault ChampionBanned() => new(GameException, "PG-0031", "That champion is banned or cannot be banned.");
    // tradeChampion registers GameException; ChampionTradeController.onTradeError ignores the code and shows
    // champion_trading_trade_error ('There was an error with the trade...'). Reconstruction: that key is the code, for the log.
    public static AirFault TradeRefused(string message) => new(GameException, "champion_trading_trade_error", message);
}
