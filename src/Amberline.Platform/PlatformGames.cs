using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Server;
using RtmpSharp.IO;

namespace Amberline.Platform;

public sealed partial class PlatformGames(PlatformAccounts accounts, GameManager manager, string advertisedHost, TimeProvider? clock = null)
{
    /// <summary>
    /// DIAGNOSTICS: whether a human seat (by account id) may use the playtest cheat commands, asked as each game starts; bot seats never
    /// may. The default is the operator's start, every human seat (owner, 26 October 2026: "enable cheats for everyone again"). Home mode
    /// replaces it (owner, 3 November 2026: "Disable cheats"): none, unless Settings allows them, and then the host's own login only
    /// (<see cref="HostSettingsPage.CheatRule"/>). From 24 to 26 October only a seat whose session reached this PC directly could
    /// (<see cref="PlatformSessions.IsLocalAccount"/>).
    /// </summary>
    public Func<long, bool> MayCheat { get; set; } = _ => true;

    public const int NormalQueueId = 2;
    /// <summary>Co-op vs. AI, Summoner's Rift 5v5: the stock play menu's PVE_root leaf for both difficulties (ChooseGameFlowViewLayoutDefinition.as:130-141).</summary>
    public const int CoopVsAiQueueId = 7;
    /// <summary>Twisted Treeline 3v3 normal blind pick: the stock play menu's PVP leaf (ChooseGameFlowViewLayoutDefinition.as:58-63).</summary>
    public const int TwistedTreelineQueueId = 8;
    /// <summary>Summoner's Rift 5v5 normal draft: the stock play menu's PVP leaf (ChooseGameFlowViewLayoutDefinition.as:48-50).</summary>
    public const int NormalDraftQueueId = 14;
    /// <summary>The Crystal Scar 5v5 normal blind pick and normal draft: the PVP odin leaves (ChooseGameFlowViewLayoutDefinition.as:84-90).</summary>
    public const int DominionQueueId = 16;
    public const int DominionDraftQueueId = 17;
    /// <summary>The client's QueueType.DOMINION_UNRANKED, the type of the Dominion queues.</summary>
    public const string DominionQueueType = "ODIN_UNRANKED";

    /// <summary>
    /// A matchmade queue this host runs (batch 571). <c>Type</c> is the client's QueueType: GameQueueManager.getQueues lists NORMAL
    /// queues under the normal games and BOT ones under Co-op vs. AI. <c>GameType</c> is the GameDTO.gameType of its games, <c>Name</c>
    /// the GameQueueConfig.name the "Searching for Match:" ribbon appends (MatchmakingWaitingStatus.as:650, 1062), <c>GameName</c>
    /// the stem of each game's own name and <c>ConfigId</c> the GameTypeConfig its games pick with (blind pick 1, Draft Mode 2).
    /// <c>Ranked</c>, <c>MinParty</c> and <c>MaxParty</c> are GameQueueConfig.ranked, minimumParticipantListSize and
    /// maximumParticipantListSize (batch 586, see <see cref="RankedSoloQueueId"/>); MaxParty null is the team size.
    /// </summary>
    public sealed record QueueRule(int Id, string Type, string GameType, int MapId, int TeamSize, string Name, string GameName,
        int ConfigId = AirGameDtos.BlindPickConfigId, bool Ranked = false, int MinParty = 1, int? MaxParty = null)
    {
        public int PartyLimit => MaxParty ?? TeamSize;
    }

    /// <summary>
    /// Owner decisions, 1 November 2026: queues 2, 7 and 8 (Co-op vs. AI replaces the 29 September "7, 12, 13 off"; 12 and 13 stay off),
    /// then the normal draft queue 14 and the Dominion queues 16 and 17 (batch 579), then the ranked queues 4, 6 and 9 (batch 586, owner
    /// decision 1 November 2026; see <see cref="RankedQueues"/>; 11, 18 and 19 from batch 590). The stock menu has no
    /// normal draft leaf for Twisted Treeline: its draft leaf is ranked (queues 11/9, :64-66).
    /// Reconstruction: the names. No shipped file holds a GameQueueConfig, so these are readable ones ("Searching for Match: Normal 5v5").
    /// Reconstruction: Co-op's GameType. The stock champion select prints "practiceGame_gameMode_" + GameDTO.gameType
    /// (MatchDetailsView.as:257, ChampionSelectionController.as:615), and the bundle's key for Co-op is practiceGame_gameMode_COOP_VS_AI_GAME
    /// ("Co-op vs. AI"); the client's own flow constant GameType.COOP_VS_AI has no such key.
    /// The Dominion queues are the client's QueueType.DOMINION_UNRANKED ("ODIN_UNRANKED"), which PlayerStatSummaryType.convert maps to the
    /// OdinUnranked summary the profile reads for its Dominion wins (ProfileStatsSummaryPresentationModel.as:159).
    /// </summary>
    public static IReadOnlyList<QueueRule> Queues { get; } =
    [
        new(NormalQueueId, "NORMAL", "NORMAL_GAME", PlatformStoreCalendar.NormalRiftMapId, 5, "Normal 5v5", "Normal Game"),
        new(CoopVsAiQueueId, "BOT", "COOP_VS_AI_GAME", PlatformStoreCalendar.NormalRiftMapId, 5, "Co-op vs. AI 5v5", "Co-op vs. AI Game"),
        new(TwistedTreelineQueueId, "NORMAL", "NORMAL_GAME", PlatformStoreCalendar.TwistedTreelineMapId, 3, "Normal 3v3", "Normal Game"),
        new(NormalDraftQueueId, "NORMAL", "NORMAL_GAME", PlatformStoreCalendar.NormalRiftMapId, 5, "Normal 5v5 Draft", "Normal Draft Game",
            AirGameDtos.DraftConfigId),
        new(DominionQueueId, DominionQueueType, "NORMAL_GAME", PlatformStoreCalendar.CrystalScarMapId, 5, "Dominion 5v5", "Dominion Game"),
        new(DominionDraftQueueId, DominionQueueType, "NORMAL_GAME", PlatformStoreCalendar.CrystalScarMapId, 5, "Dominion 5v5 Draft",
            "Dominion Draft Game", AirGameDtos.DraftConfigId),
        .. RankedQueues,
    ];
    public static QueueRule? QueueOf(int queueId) => Queues.FirstOrDefault(queue => queue.Id == queueId);
    /// <summary>
    /// Custom games (no queue) and Co-op vs. AI pay from the daily practice budget the end-of-game screen shows for queue types NONE
    /// and BOT (EndOfGameStatsView.as:1966) and never claim First Win of the Day; the normal queues, Dominion's included, do neither.
    /// Reconstruction: that Co-op claimed no first win.
    /// </summary>
    public static bool PaysFromPracticeBudget(int queueId) => QueueOf(queueId)?.Type is null or "BOT";
    // Reconstruction (owner, 1 November 2026: start Co-op as soon as the humans form a team; retail matched quickly): a Co-op team of
    // fewer than five starts once its first player has waited this long, so friends who queue at the same moment share it.
    private static readonly TimeSpan CoopGatherTime = TimeSpan.FromSeconds(5);
    // The patch-126 AIR client subtracts its three-second server timer buffer
    // from both values before showing the countdown (GameTypeConfig.SERVER_TIMER_BUFFER).
    // Reconstruction (owner decision, 18 October 2026). Retail's pick and post-pick lengths are the server's
    // GameTypeConfigDTO values, which the client does not contain, so nothing recovered says what they were. This host
    // sends 93 and 13 so the stock countdown shows 90 and 10 seconds while the server still accepts a pick or lock-in
    // for three more seconds: latency slack, so that a pick the player made in time is never refused or dropped
    // because it arrived late. The server's own deadline (the AFK drop, the launch) uses the same 93/13.
    public const int MainPickServerSeconds = 93;
    public const int PostPickServerSeconds = 13;
    // Batch 669: Battle Training's champion select (AirDtos.GameTypes, config 11). Reconstruction: the tour says the player "may take as much
    // time as you need" and warns at 60 seconds left; thirty minutes, the custom lobby's own limit, with the same three seconds of slack.
    public const int BattleTrainingPickServerSeconds = 30 * 60 + 3;
    // Draft Mode (GameTypeConfig id 2, batch 543). Reconstruction: no shipped file states retail's draft durations; each ban turn,
    // each pick turn and the closing trade phase last 30 seconds, sent with the same three seconds of slack as blind pick (the
    // client shows 30). The client's own fallback for the post-pick tip says "You now have 30 seconds to make last minute changes"
    // (ChampionSelectionTipsController.showPostPickTip).
    public const int DraftBanServerSeconds = 33;
    public const int DraftPickServerSeconds = 33;
    public const int DraftPostPickServerSeconds = 33;
    // The bundle's draft tips: the captains "ban up to 4 champions", each "a total of 2" (championSelection_newbietip_notBanningTip,
    // potentialBannerTip); the ban progress the client prints is "(pickTurn/maxAllowableBans)" (findBanProgress).
    public const int DraftMaxBans = 4;
    private const int QueueCapacity = 100;

    public sealed class Member(PlatformAccount account, Team team)
    {
        public PlatformAccount Account = account;
        public Team Team = team;
        public int Champion;
        public int Skin;
        /// <summary>When the player took this seat; only feeds the host page's Online table.</summary>
        public DateTimeOffset Joined;
        public int Spell1 = AirGameDtos.SpellId(account.Loadout.SummonerSpell1);
        public int Spell2 = AirGameDtos.SpellId(account.Loadout.SummonerSpell2);
        public bool Locked;
        /// <summary>The client's CHAMP_SELECT_CLIENT acknowledgement that its champion grid loaded (<see cref="AcknowledgeChampionSelect"/>).</summary>
        public bool GridLoaded;
        /// <summary>Draft Mode: the pick turn this player picks in (1-based, see <see cref="AssignPickTurns"/>); 0 outside a draft.</summary>
        public int PickTurn;
    }
    /// <summary>
    /// A server-owned lobby seat added with the stock add-bot controls. Bots are never
    /// lobby members: they hold no account, so every member-based consumer (credentials,
    /// receipts, rewards, presence) stays human-only. The match seats them after the humans.
    /// </summary>
    public sealed class BotSeat(int champion, Team team, int skillLevel, MatchPlayer loadout, string? skillLevelName = null)
    {
        public int Champion = champion;
        public Team Team = team;
        public int SkillLevel = skillLevel;
        /// <summary>The client's own localized difficulty text, echoed back so its lobby row matches.</summary>
        public string SkillLevelName = skillLevelName ?? PlatformBots.SkillName(skillLevel);
        /// <summary>Retail or default spells and pages; the match name is fixed at launch.</summary>
        public MatchPlayer Loadout = loadout;
    }
    public sealed class Lobby(int id, string name, string password, long owner, int capacity)
    {
        public int Id = id;
        public string Name = name;
        public string Password = password;
        public long Owner = owner;
        public int Capacity = capacity;
        /// <summary>The map the game is played on, fixed at creation: a queue game its queue's map, a custom game the map it chose.</summary>
        public int MapId = PlatformStoreCalendar.NormalRiftMapId;
        /// <summary>Batch 669: the mode, when it is not the map's own (Battle Training: map 1 in TUTORIAL, <see cref="GameMaps.IsPlayedIn"/>).</summary>
        public string? Mode;
        /// <summary>The mode the game is played in: the GameDTO's gameMode and the server's <c>--game-mode</c>.</summary>
        public string GameMode => Mode ?? GameMaps.ModeOf(MapId);
        public string State = "TEAM_SELECT";
        public string TerminatedCondition = "";
        public long Version = 1;
        public DateTimeOffset Created;
        public DateTimeOffset Deadline;
        /// <summary>When <c>State</c> last changed; only feeds the host page's Online table.</summary>
        public DateTimeOffset StateSince;
        internal string StateSeen = "";
        public List<Member> Members = [];
        public List<BotSeat> Bots = [];
        public HashSet<long> Banned = [];
        public Guid? HostId;
        public Guid RequestId = Guid.NewGuid();
        public MatchResult? Result;
        public bool CredentialsSent;
        public HashSet<long> Returned = [];
        public int QueueId;
        public int QueuePosition;
        /// <summary>The queue entries a matchmade game was built from, so a champion-select leaver can put the rest back.</summary>
        internal List<QueueEntry> QueueEntries = [];
        /// <summary>The queue a matchmade game came from; null for a custom game.</summary>
        public QueueRule? Queue => PlatformGames.QueueOf(QueueId);
        public bool IsMatchmade => Queue is not null;
        /// <summary>The GameTypeConfigDTO id the game was created with: blind pick (1) or Draft Mode (2).</summary>
        public int ConfigId = AirGameDtos.BlindPickConfigId;
        public bool IsDraft => ConfigId == AirGameDtos.DraftConfigId;
        /// <summary>GameDTO.pickTurn: always 1 in blind pick; in a draft the ban turn (1-4) in PRE_CHAMP_SELECT, then the pick turn.</summary>
        public int PickTurn = 1;
        /// <summary>GameDTO.bannedChampionIds, in ban order (draft only). Not <see cref="Banned"/>, which holds kicked accounts.</summary>
        public List<int> BannedChampions = [];
    }
    internal sealed class QueueEntry(long accountId, string connection, DateTimeOffset joined, long sequence, int queueId, int botSkill)
    {
        public long AccountId = accountId;
        public string Connection = connection;
        public DateTimeOffset Joined = joined;
        public long Sequence = sequence;
        public int QueueId = queueId;
        /// <summary>Co-op vs. AI: the bot level the player chose (EASY 0, MEDIUM 1); -1 in the other queues.</summary>
        public int BotSkill = botSkill;
        /// <summary>The unit this entry is matched in: the leader's sequence for a party (batch 579), the entry's own for a solo player.</summary>
        public long Party = sequence;
        public bool ReplySent;
        /// <summary>When the unit may be matched: its join, or later for a LeaverBuster low-priority wait (batch 587).</summary>
        public DateTimeOffset MatchableAt = joined;
    }
    private readonly object _gate = new();
    private readonly Dictionary<int, Lobby> _lobbies = [];
    private readonly List<QueueEntry> _queue = [];
    /// <summary>Each account's last accepted party invitation and the connection it came on (<see cref="AcceptInvite"/>).</summary>
    private readonly Dictionary<long, (string InviteId, string Connection)> _invites = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _queueSequence;
    public Action<long, object> Push { get; set; } = (_, _) => { };
    public Action<long> PresenceChanged { get; set; } = _ => { };
    public Func<string, long?>? AccountForConnection { get; set; }
    public string? ActivePresenceChampion(long account)
    {
        lock (_gate)
        {
            var game = FindCurrent(account);
            if (game is null || game.Result is not null ||
                game.State is not ("START_REQUESTED" or "IN_PROGRESS")) return null;
            var champion = game.Members.First(m => m.Account.Id == account).Champion;
            return AirGameDtos.ChampionNames.GetValueOrDefault(champion);
        }
    }
    /// <summary>
    /// NOT RETAIL: operator tooling for the host page's Online table (retail had none). What each player in the queue or in a
    /// lobby, champion select or match is doing, and since when (the phase began, or the player joined, whichever is later).
    /// Everything is read from the lobby registry and the game host's seat flags; an account in none of them is not listed.
    /// </summary>
    public Dictionary<long, (string Doing, DateTimeOffset Since)> Activities()
    {
        lock (_gate)
        {
            var result = new Dictionary<long, (string, DateTimeOffset)>();
            foreach (var entry in _queue) result[entry.AccountId] = ($"In the {QueueOf(entry.QueueId)!.Name} queue", entry.Joined);
            foreach (var game in _lobbies.Values.Where(g => !IsTerminal(g)))
            {
                var remote = game.HostId is Guid id ? manager.List(new HostGuest("", "", "")).FirstOrDefault(g => g.Id == id) : null;
                var phase = game.StateSince == default ? game.Created : game.StateSince;
                foreach (var member in game.Members.Where(m => !game.Returned.Contains(m.Account.Id)))
                    result[member.Account.Id] = (Doing(game, member, remote), member.Joined > phase ? member.Joined : phase);
            }
            return result;
        }
    }

    /// <summary>
    /// Whether the account may enter a lobby chat room (batch 549). The stock client names its rooms after its own game,
    /// <c>prefix~sha1(lower(name))</c> (<see cref="ChatRoomName"/>): team select "ap" from the game name as TeamSelectionController
    /// shows it (cut to practiceGame_new_maxTeamDisplayLength, 50 characters, plus "..."), champion select "c1"/"c2" for the
    /// player's own team (team one, Order) from name_c1/name_c2 (ChampionSelectionController) or name_ta/name_tb
    /// (QueuedGameController), and results "pg" from "endGame" + gameId (EndOfGameStatsController). So a room name is predictable
    /// from a lobby's name, and only the rooms of the account's own current game admit it. Every other prefix stays open: the
    /// public rooms "pu", the private "pr" and the invitation room "ag" (a random client-made id). Reconstruction: retail's
    /// MUC rules are not in the shipped files.
    /// </summary>
    public bool MayEnterRoom(long account, string room)
    {
        // The XMPP host keys its rooms ignoring case, so the prefix test must too ("AP~…" is the same room as "ap~…").
        room = room.ToLowerInvariant();
        if (room.Split('~', 2)[0] is not ("ap" or "c1" or "c2" or "pg")) return true;
        lock (_gate)
        {
            if (FindCurrent(account) is not { } game) return false;
            var (prefix, pick, queued) = game.Members.First(m => m.Account.Id == account).Team == Team.Order
                ? ("c1", "_c1", "_ta") : ("c2", "_c2", "_tb");
            var shown = game.Name.Length > 50 ? game.Name[..50] + "..." : game.Name;
            return new[] { ChatRoomName("ap", shown), ChatRoomName(prefix, game.Name + pick), ChatRoomName(prefix, game.Name + queued),
                ChatRoomName("pg", "endGame" + game.Id) }.Contains(room, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>ChatController.obfuscateChatRoom: the prefix, "~" and the lower-case hex SHA-1 of the lower-cased UTF-8 name.</summary>
    public static string ChatRoomName(string prefix, string name) => prefix + "~" +
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())));

    private static string Doing(Lobby game, Member member, RemoteGame? remote)
    {
        var what = game.Queue is { } queue ? $"{queue.Name} queue match \"{game.Name}\"" : $"custom game \"{game.Name}\" (#{game.Id})";
        var champion = AirGameDtos.ChampionNames.GetValueOrDefault(member.Champion);
        var seat = remote?.Seats.ElementAtOrDefault(game.Members.IndexOf(member));
        if (game.Result is not null) return $"Post-game results, {what}";
        return game.State switch
        {
            "TEAM_SELECT" => $"In lobby of {what}, {game.Members.Count + game.Bots.Count}/{game.Capacity} players" + (game.Owner == member.Account.Id ? ", host" : ""),
            "PRE_CHAMP_SELECT" => $"Champion select (bans), {what}",
            "CHAMP_SELECT" or "POST_CHAMP_SELECT" => $"Champion select, {what}: " +
                (champion is null ? "no pick yet" : champion + (member.Locked ? " locked in" : " picked")),
            "START_REQUESTED" when !game.CredentialsSent => $"Launching the game server for {what} as {champion}",
            "START_REQUESTED" => (seat is not { Connected: true } ? "Starting: not connected to the game server yet"
                : seat.Loaded ? "Loaded, waiting for the other players" : "Loading into the game") + $" as {champion}, {what}",
            "IN_PROGRESS" => (seat is { Connected: false } ? "In game, game connection lost" : "In game") + $" as {champion}, {what}",
            _ => $"{game.State}, {what}",
        };
    }

    public static HostGuest Guest(PlatformAccount a) => new($"air:{a.Id}", a.Summoner, "");

    public object List()
    {
        lock (_gate) return AirWire.List(_lobbies.Values.Where(g => g.State == "TEAM_SELECT").Select(AirGameDtos.Search));
    }

    /// <param name="botDifficulty">MatchMakerParams.botDifficulty: the stock flow sends gameModel.difficulty, the PVE_root leaf's EASY
    /// or MEDIUM (MasterGameViewController.joinQueue_attach); the other queues ignore it.</param>
    /// <param name="team">MatchMakerParams.team of a party's attachTeamToQueue: the summoner ids of the leader's arranged team, the leader
    /// included (InviteController.getParticipantIDCollection reads them from the members' chat JIDs, "sum" + id). Null or one id: solo.</param>
    /// <param name="invitationId">MatchMakerParams.invitationId: the invitation every member accepted (<see cref="AcceptInvite"/>).</param>
    public object AttachQueue(PlatformAccount account, string connection, int[] queueIds, string? botDifficulty = null,
        long[]? team = null, string? invitationId = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException("An authenticated connection is required.");
            var resolvedAccount = AccountForConnection?.Invoke(connection);
            if (AccountForConnection is not null && resolvedAccount != account.Id)
                throw new ArgumentException("The queue connection is not authenticated for this account.");
            var rule = queueIds is { Length: 1 } ? QueueOf(queueIds[0]) : null;
            if (rule is null)
                return AirGameDtos.QueueFailure(queueIds is { Length: > 0 } ? queueIds[0] : NormalQueueId, "This queue is not enabled.");
            // Owner decision, 1 November 2026: EASY (Beginner) plays bot level 0, MEDIUM (Intermediate) level 1, the retail bot
            // scripts' eNEWBIE and eINTERMEDIATE arms (BotLoadoutReader.Difficulties), as custom games seat them. A party plays its leader's.
            var botSkill = rule.Type != "BOT" ? -1 : botDifficulty switch { "EASY" => 0, "MEDIUM" => 1, _ => -2 };
            if (botSkill == -2) return AirGameDtos.QueueFailure(rule.Id, "Choose Beginner or Intermediate bots.");
            // Batch 579: a party queues as one unit. Reconstruction: retail's checks on a team are not recovered; here the team fits one
            // side (the stock joinQueue refuses more itself, invite_join_game_with_2_many_participants) and every member accepted this
            // invitation on a connection still signed in to their account.
            var members = new List<(PlatformAccount Account, string Connection)>();
            // A ranked party queue takes only its own size (queue 6 five, queue 9 three; the duo goes to queue 4, batch 586).
            if ((team is { Length: > 1 } ? team.Length : 1) < rule.MinParty)
                return AirGameDtos.QueueFailure(rule.Id, "This team cannot join this queue.");
            if (team is { Length: > 1 })
            {
                if (team.Distinct().Count() != team.Length || !team.Contains(account.SummonerId) || team.Length > rule.PartyLimit)
                    return AirGameDtos.QueueFailure(rule.Id, "This team cannot join this queue.");
                foreach (var summonerId in team.Where(id => id != account.SummonerId))
                {
                    if (accounts.FindSummoner(summonerId) is not { } member || !_invites.TryGetValue(member.Id, out var accepted) ||
                        accepted.InviteId != invitationId || (AccountForConnection is not null && AccountForConnection(accepted.Connection) != member.Id))
                        return AirGameDtos.QueueFailure(rule.Id, "A team member has not accepted the invitation.");
                    members.Add((member, accepted.Connection));
                }
            }
            var everyone = members.Select(member => member.Account).Prepend(account).ToArray();
            var playing = everyone.Where(player => FindCurrent(player.Id) is { Result: null }).ToArray();
            if (playing.Length != 0) return AirGameDtos.QueueAlreadyInGame(playing);
            var now = _clock.GetUtcNow();
            // Batch 589: a player with a dodge timer keeps the whole unit out of every queue, with the client's own QueueDodger rows
            // ("Because {0} abandoned a recent game during champion selection, your team is unable to join the queue").
            var dodgers = everyone.Select(player => (player, accounts.DodgeStatus(player.Id, now).Remaining))
                .Where(dodger => dodger.Remaining > TimeSpan.Zero).ToArray();
            if (dodgers.Length != 0) return AirGameDtos.QueueDodgers(dodgers);
            if (rule.Ranked && RankedFailures(everyone) is { Length: > 0 } unqualified) return AirGameDtos.QueueJoinFailures(unqualified);
            foreach (var player in everyone)
                if (FindCurrent(player.Id) is { } finished) ReturnFromResults(finished, player.Id);

            var existing = _queue.FirstOrDefault(entry => entry.AccountId == account.Id);
            if (members.Count == 0 && existing is not null && existing.Connection == connection && existing.QueueId == rule.Id &&
                existing.BotSkill == botSkill && _queue.Count(entry => entry.Party == existing.Party) == 1)
                return AirGameDtos.SearchingForMatch(QueueInfoObject(rule.Id, existing.MatchableAt - now));
            // A replaced AIR connection is removed by the next authenticated
            // tick. Reattaching on the new connection is safe immediately and
            // cannot create a second membership for the same account. An earlier entry leaves with its whole party.
            foreach (var player in everyone)
                if (_queue.FirstOrDefault(entry => entry.AccountId == player.Id) is { } old) LeaveQueue(old);
            if (_queue.Count + everyone.Length > QueueCapacity)
                return AirGameDtos.QueueThrottled(rule.Id, "Queue capacity reached.");
            // Batch 587, LeaverBuster: the unit waits out the longest low-priority delay among its players (PlatformAccounts.LeaverStatus),
            // so one penalised member delays the whole party. The client shows it in the ribbon's "Approx" wait, the join reply's
            // QueueInfo.waitTime; the matcher takes the unit only once it is matchable, behind everyone matchable before it.
            var delay = everyone.Max(player => accounts.LeaverStatus(player.Id).QueueDelay);
            var leader = new QueueEntry(account.Id, connection, now, ++_queueSequence, rule.Id, botSkill) { MatchableAt = now + delay };
            _queue.Add(leader);
            // The leader's reply gates the party (ConfirmQueueReply); the members are told by push, so theirs is already sent.
            foreach (var (member, memberConnection) in members)
                _queue.Add(new(member.Id, memberConnection, now, ++_queueSequence, rule.Id, botSkill)
                    { Party = leader.Party, ReplySent = true, MatchableAt = leader.MatchableAt });
            var joined = AirGameDtos.SearchingForMatch(QueueInfoObject(rule.Id, delay));
            // The members wait in their arranged-team view (JOIN_QUEUE) and enter the queue on this notification: a pushed
            // SearchingForMatchNotification goes to handleJoinQueue (MasterGameController.onServerGameMessageReceived), which starts their wait.
            foreach (var (member, _) in members) Push(member.Id, joined);
            return joined;
        }
    }

    /// <summary>
    /// matchmakerService.acceptInviteForMatchmakingGame(inviteId): an invitee accepts a party invitation to a queue. The stock client
    /// sends its GAME_INVITE_ACCEPT chat message to the inviter only once this succeeds (InviteController.sendMessageIfValidLCDS), and
    /// sends it again with the next invite id when its team is put back after a failed start (MasterGameViewController.inviteeWaitForJoinQueue).
    /// The accept is remembered with its connection, so the leader's attachTeamToQueue can queue only players who accepted that
    /// invitation. The client then leaves any queue view for the inviter's arranged team, so an earlier queue entry ends here.
    /// Reconstruction: retail's server side of the call is not recovered.
    /// </summary>
    public void AcceptInvite(PlatformAccount account, string connection, string inviteId)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(connection) || (AccountForConnection is not null && AccountForConnection(connection) != account.Id))
                throw new ArgumentException("The queue connection is not authenticated for this account.");
            if (inviteId.Length is < 1 or > 32 || inviteId.Any(char.IsControl)) throw AirFault.InviteRefused("Invalid invitation.");
            if (FindCurrent(account.Id) is { } current)
            {
                if (current.Result is null) throw AirFault.InviteRefused("Leave your current game first.");
                ReturnFromResults(current, account.Id);
            }
            if (_queue.FirstOrDefault(entry => entry.AccountId == account.Id) is { } queued) LeaveQueue(queued);
            _invites[account.Id] = (inviteId, connection);
        }
    }

    public void ConfirmQueueReply(string connection)
    {
        lock (_gate)
        {
            var entry = _queue.FirstOrDefault(item => item.Connection == connection);
            if (entry is not null) entry.ReplySent = true;
        }
    }

    public bool CancelQueue(long accountId, string connection)
    {
        lock (_gate)
        {
            var queued = _queue.FirstOrDefault(item => item.AccountId == accountId && item.Connection == connection);
            if (queued is not null)
            {
                LeaveQueue(queued);
                return true;
            }
            if (FindCurrent(accountId) is { IsMatchmade: true }) return false;
            return true;
        }
    }

    public object QueueInfo(int queueId = NormalQueueId)
    {
        lock (_gate)
        {
            PurgeQueue();
            return QueueInfoObject(queueId);
        }
    }

    public object Create(PlatformAccount account, string name, string password, int mapId, int capacity, int configId, string mode, string spectators)
    {
        // Every refusal below is a typed fault the create view's GameException handler shows (see AirFault), each with its own
        // message, which the platform log names as the reason.
        if (name.Trim().Length is < 1 or > 60 || name.Any(char.IsControl)) throw AirFault.InvalidGameText("Invalid game name.");
        if (password.Length > 64 || password.Any(char.IsControl)) throw AirFault.InvalidGameText("Invalid game password.");
        // NOT RETAIL: Dominion (The Crystal Scar, map 8) is created in its own mode, ODIN, and only there; the other maps are CLASSIC.
        if (!PlatformStoreCalendar.OpenMapIds.Contains(mapId)) throw AirFault.ModeNotSupported("this server",
            $"Map not offered for custom games; the offered maps are {string.Join(", ", PlatformStoreCalendar.OpenMapIds)}.");
        if (mode != Amberline.Core.GameMaps.ModeOf(mapId)) throw AirFault.ModeNotSupported("this server", "Game mode does not match the map.");
        if (configId is not (AirGameDtos.BlindPickConfigId or AirGameDtos.DraftConfigId))
            throw AirFault.ModeNotSupported("this server", "Pick type not offered, blind pick or Draft Mode only.");
        // The slot maximum is the map's (six on Twisted Treeline, batch 557); bots take slots inside it, as in the lobby's own count.
        var maxSlots = PlatformStoreCalendar.MaxSlotsOf(mapId);
        if (capacity > maxSlots) throw AirFault.TooManySlots(maxSlots);
        if (capacity < 2) throw AirFault.TooFewSlots(2);
        // Stock AIR hides the spectator checkbox when observerModeEnabled=false,
        // but its untouched default still sends ALL. Both inputs create a
        // player-only lobby; the returned DTO always disables spectators.
        if (spectators is not ("NONE" or "ALL")) throw AirFault.SpectatorsDisabled();
        lock (_gate)
        {
            RequireFree(account.Id, name.Trim(), joining: false);
            if (_lobbies.Values.Count(g => !IsTerminal(g) && g.Result is null) >= 32) throw AirFault.CreationDisabled();
            // Reconstruction: the client ships GameNameNotUniqueException and PG-0013 ("Game name is not unique"), so retail
            // refused a duplicate name; which games it compared against is unknown, so open custom lobbies are compared here.
            if (_lobbies.Values.Any(g => !IsTerminal(g) && g.Result is null && !g.IsMatchmade &&
                g.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw AirFault.NameNotUnique(name.Trim());
            var game = new Lobby(accounts.AllocateGameId(), name.Trim(), password, account.Id, capacity)
            // Reconstruction: GameDTO.expiryTime is the milliseconds a lobby has left (PracticeGameManager counts it down and
            // the client has a 'Lobby Expired' dialog), but retail's lobby lifetime is not recoverable; 30 minutes is a guess.
            { Created = _clock.GetUtcNow(), Deadline = _clock.GetUtcNow().AddMinutes(30),
              MapId = mapId, ConfigId = configId };
            game.Members.Add(OfferedSeat(new(account, Team.Order) { Joined = _clock.GetUtcNow() }, mapId));
            _lobbies.Add(game.Id, game);
            return AirGameDtos.Game(game);
        }
    }

    public object Join(PlatformAccount account, int id, string password)
    {
        lock (_gate)
        {
            var game = Find(id);
            if (game.State == "TEAM_SELECT" && game.Members.Any(m => m.Account.Id == account.Id)) return AirGameDtos.Game(game);
            RequireFree(account.Id, game.Name, joining: true);
            if (game.State != "TEAM_SELECT") throw AirFault.NotJoinable();
            if (game.Banned.Contains(account.Id)) throw AirFault.Banned();
            // Bots occupy lobby slots, as in the stock team lists.
            if (game.Members.Count + game.Bots.Count >= game.Capacity) throw AirFault.IsFull(game.Name);
            if (!PlatformSessions.SecretEquals(game.Password, password)) throw AirFault.WrongPassword();
            var team = TeamCount(game, Team.Order) <= TeamCount(game, Team.Chaos) ? Team.Order : Team.Chaos;
            game.Members.Add(OfferedSeat(new(account, team) { Joined = _clock.GetUtcNow() }, game.MapId));
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    public object? Current(long account)
    {
        lock (_gate) return FindCurrent(account) is { } game ? AirGameDtos.Game(game) : null;
    }

    public object? Latest(long account, int id)
    {
        lock (_gate)
            return _lobbies.GetValueOrDefault(id) is { } game && game.Members.Any(m => m.Account.Id == account)
                ? AirGameDtos.Game(game) : null;
    }

    public object Get(long account, int id)
    {
        lock (_gate)
        {
            var game = Find(id);
            if (!game.Members.Any(m => m.Account.Id == account) && game.State != "TEAM_SELECT") throw AirFault.NoGame();
            return AirGameDtos.Game(game);
        }
    }

    /// <summary>
    /// gameService.switchTeams answers a Boolean: TeamSelectionController.onSwitchTeamsSuccess runs its team-full
    /// handler (PG-0004) when the result is not true, so a DTO result would raise that popup after every switch.
    /// </summary>
    public bool SwitchTeam(long account, int id)
    {
        lock (_gate)
        {
            var game = RequireMember(account, id);
            if (game.IsMatchmade) throw new ArgumentException("Queue game teams are assigned by matchmaking.");
            if (game.State != "TEAM_SELECT") throw AirFault.TeamsLocked();
            var member = MemberFor(game, account);
            var next = member.Team == Team.Order ? Team.Chaos : Team.Order;
            if (TeamCount(game, next) >= TeamLimit(game)) return false;
            member.Team = next;
            Changed(game);
            return true;
        }
    }

    public object Start(long account, int id, long revision)
    {
        lock (_gate)
        {
            var game = RequireMember(account, id);
            if (game.IsMatchmade) throw new ArgumentException("Queue games start through matchmaking.");
            if (game.Owner != account) throw new ArgumentException("Only the lobby owner can start champion selection.");
            if (game.State != "TEAM_SELECT" || revision != game.Version) throw AirFault.Stale();
            // Owner-approved testing rule: one human may start without an opponent.
            // Stock AIR already confirms uneven teams before sending this request.
            BeginSelection(game);
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    /// <summary>Champion selection begins, in a custom game's start or a queue game's match: blind pick goes straight to the picks.</summary>
    private void BeginSelection(Lobby game)
    {
        if (!game.IsDraft)
        {
            game.State = "CHAMP_SELECT";
            game.Deadline = _clock.GetUtcNow().AddSeconds(MainPickServerSeconds);
            return;
        }
        AssignPickTurns(game);
        // Reconstruction: the stock ban grid of the banning captain lists only champions the other team owns
        // (ChampionSelectionController.updateChampionList: ownedByEnemyTeam), so without a human on both teams there is
        // nothing to ban and the phase is skipped. Team one (Order, blue) bans first, as in the 2011 draft.
        if (Captain(game, Team.Order) is not null && Captain(game, Team.Chaos) is not null)
        {
            game.State = "PRE_CHAMP_SELECT";
            game.PickTurn = 1;
            game.Deadline = _clock.GetUtcNow().AddSeconds(DraftBanServerSeconds);
        }
        else BeginDraftPicks(game);
    }

    /// <summary>
    /// The pick mode the stock champion select reads per participant (GameParticipant.PICK_MODE_NOT_PICKING 0, ACTIVE 1, DONE 2).
    /// ChampionSelectionController.selectChampion only sends a pick while its own participant is ACTIVE; findBanTeam takes the
    /// banning team from the ACTIVE participant; the turn texts list the ACTIVE players.
    /// </summary>
    internal static int PickMode(Lobby game, Member member, string state)
    {
        if (!game.IsDraft) return member.Locked ? 2 : state == "CHAMP_SELECT" ? 1 : 0;
        return state switch
        {
            "PRE_CHAMP_SELECT" => Captain(game, game.PickTurn % 2 == 1 ? Team.Order : Team.Chaos) == member ? 1 : 0,
            "CHAMP_SELECT" => member.Locked ? 2 : member.PickTurn == game.PickTurn ? 1 : 0,
            _ => member.Locked ? 2 : 0,
        };
    }

    /// <summary>
    /// A team's draft captain is the first participant of its team list: the stock tips controller's isPotentialBanner reads
    /// getCurrentTeam().getItemAt(0). The DTO lists the humans before the bots, so a team of bots alone has no captain.
    /// </summary>
    private static Member? Captain(Lobby game, Team team) => game.Members.FirstOrDefault(m => m.Team == team);

    /// <summary>
    /// Reconstruction (the 2011 draft rules; the client only numbers the turns): team one's first player picks alone, then the
    /// teams alternate two players at a time, so five a side is 1-2-2-2-2-1. Within a team the order is its team list. A team
    /// with nobody left hands the turn on. Bots hold their lobby champion and have no turn.
    /// </summary>
    private static void AssignPickTurns(Lobby game)
    {
        var waiting = new Dictionary<Team, Queue<Member>>
        {
            [Team.Order] = new(game.Members.Where(m => m.Team == Team.Order)),
            [Team.Chaos] = new(game.Members.Where(m => m.Team == Team.Chaos)),
        };
        var (team, size, turn) = (Team.Order, 1, 1);
        while (waiting.Values.Any(q => q.Count != 0))
        {
            if (waiting[team].Count == 0) team = team == Team.Order ? Team.Chaos : Team.Order;
            for (var taken = 0; taken < size && waiting[team].Count != 0; taken++) waiting[team].Dequeue().PickTurn = turn;
            (team, size, turn) = (team == Team.Order ? Team.Chaos : Team.Order, 2, turn + 1);
        }
    }

    private void BeginDraftPicks(Lobby game)
    {
        game.State = "CHAMP_SELECT";
        game.PickTurn = 1;
        game.Deadline = _clock.GetUtcNow().AddSeconds(DraftPickServerSeconds);
    }

    /// <summary>The ban turn ends (a ban, or the captain's time ran out): the other captain's turn, or the picks after the fourth.</summary>
    private void NextBanTurn(Lobby game)
    {
        if (game.PickTurn >= DraftMaxBans) { BeginDraftPicks(game); return; }
        game.PickTurn++;
        game.Deadline = _clock.GetUtcNow().AddSeconds(DraftBanServerSeconds);
    }

    /// <summary>Every player of the pick turn has locked in: the next turn, or the closing (trade) phase after the last.</summary>
    private void NextPickTurn(Lobby game)
    {
        game.PickTurn++;
        if (game.Members.Any(m => m.PickTurn == game.PickTurn))
            game.Deadline = _clock.GetUtcNow().AddSeconds(DraftPickServerSeconds);
        else
        {
            game.State = "POST_CHAMP_SELECT";
            game.Deadline = _clock.GetUtcNow().AddSeconds(DraftPostPickServerSeconds);
        }
    }

    /// <summary>
    /// gameService.getChampionsForBan: one ChampionBanInfoDTO per champion. initializeBannableChampions copies <c>owned</c> to
    /// Champion.ownedByYourTeam and <c>enemyOwned</c> to ownedByEnemyTeam; the banning captain's grid shows the champions the
    /// other team owns and the other players' grid the ones their own team owns. A champion a bot holds is owned by nobody
    /// here, so it is never offered for a ban (the bot keeps it).
    /// </summary>
    public object ChampionsForBan(long account)
    {
        lock (_gate)
        {
            var game = FindCurrent(account) ?? throw AirFault.NoGame();
            var member = MemberFor(game, account);
            var enemy = member.Team == Team.Order ? Team.Chaos : Team.Order;
            bool Owned(Team team, int champion) => AirGameDtos.ChampionNames.ContainsKey(champion) &&
                !game.Bots.Any(b => b.Champion == champion) &&
                game.Members.Any(m => m.Team == team && PlatformEconomy.CanPlay(accounts.Get(m.Account.Id), champion, accounts.EconomyRules));
            return AirWire.List(AirGameDtos.AllChampionNames.Keys.Order().Select(id =>
                AirWire.Object(AirProtocolNames.ChampionBanInfoDTO,
                    ("championId", id), ("owned", Owned(member.Team, id)), ("enemyOwned", Owned(enemy, id)))));
        }
    }

    /// <summary>
    /// gameService.banChampion: the captain whose turn it is bans one champion, and the turn passes. The stock client sends
    /// banChampionCompleted right after the answer (onBanSucccess), which only acknowledges. Each ban is its own turn because
    /// the client announces new bans and restarts its ban timer only when pickTurn changes (pickTurnChanged).
    /// </summary>
    public void Ban(long account, int champion)
    {
        lock (_gate)
        {
            var game = FindCurrent(account) ?? throw AirFault.NoGame();
            var member = MemberFor(game, account);
            if (game.State != "PRE_CHAMP_SELECT" || PickMode(game, member, game.State) != 1) throw AirFault.NotYourTurn();
            if (!AirGameDtos.AllChampionNames.ContainsKey(champion)) throw new ArgumentException("Unsupported champion.");
            if (game.BannedChampions.Contains(champion) || game.Bots.Any(b => b.Champion == champion)) throw AirFault.ChampionBanned();
            game.BannedChampions.Add(champion);
            NextBanTurn(game);
            Changed(game);
        }
    }

    /// <summary>
    /// gameService.tradeChampion(summonerId), sent by the player who asked once the other accepted. The request, the accept and
    /// the decline travel between the two clients as CHAMPION_SELECTION_TRADE chat messages (ChampionTradeController), which
    /// the XMPP host relays like any other message; the server only swaps. The stock client trades in POST_CHAMP_SELECT of a
    /// game whose config allows trades, with a teammate, champions only (requestChampionTrade passes tradeSpells=false).
    /// Reconstruction: the swapped champions return to their default skin, since a skin belongs to its champion.
    /// </summary>
    public void Trade(long account, long summonerId)
    {
        lock (_gate)
        {
            var game = FindCurrent(account) ?? throw AirFault.NoGame();
            var member = MemberFor(game, account);
            var other = game.Members.FirstOrDefault(m => m.Account.SummonerId == summonerId);
            if (!game.IsDraft || game.State != "POST_CHAMP_SELECT" || _clock.GetUtcNow() >= game.Deadline)
                throw AirFault.TradeRefused("Trades are closed.");
            if (other is null || other == member || other.Team != member.Team || member.Champion == 0 || other.Champion == 0)
                throw AirFault.TradeRefused("That player cannot trade with you.");
            if (!PlatformEconomy.CanPlay(accounts.Get(member.Account.Id), other.Champion, accounts.EconomyRules) ||
                !PlatformEconomy.CanPlay(accounts.Get(other.Account.Id), member.Champion, accounts.EconomyRules))
                throw AirFault.TradeRefused("A traded champion is not owned.");
            (member.Champion, other.Champion) = (other.Champion, member.Champion);
            member.Skin = other.Skin = 0;
            Changed(game);
        }
    }

    public object Pick(long account, int champion)
    {
        lock (_gate)
        {
            var game = RequirePicking(account);
            var member = MemberFor(game, account);
            RequireTurn(game, member);
            if (member.Locked) throw AirFault.SelectionClosed();
            // A champion the server cannot play is listed in the AIR grid as locked; naming it anyway is 'not owned', whatever
            // the account holds (an unlocked account owns everything the server plays, not what it lacks a module for).
            if (!AirGameDtos.ChampionNames.ContainsKey(champion))
                throw AirGameDtos.AllChampionNames.ContainsKey(champion) ? AirFault.NotOwned() : new ArgumentException("Unsupported champion.");
            // Batch 669: Battle Training's grid lists Garen, Ryze and Ashe, owned or not (AdvancedTutorialController.tutorialChampions,
            // ChampionSelectionController.setHardcodedChampionsToChoose clears restrictChampionOwnership), and nothing else.
            if (game.ConfigId == AirGameDtos.BattleTrainingConfigId ? !BattleTrainingChampions.Contains(AirGameDtos.ChampionNames[champion])
                    : !PlatformEconomy.CanPlay(accounts.Get(account), champion, accounts.EconomyRules))
                throw AirFault.NotOwned();
            // The stock grid never offers a banned champion (isChampionAvailableForChoosing); only a draft bans.
            if (game.BannedChampions.Contains(champion)) throw AirFault.ChampionBanned();
            // A teammate holding the champion is not an error in retail: ChampionSelectionController.onSummonChampionSuccess
            // compares the champion the returned game gives this player with the one requested and, when they differ,
            // shows "This Champion has already been taken" itself. So answer with the unchanged game. A draft's config is
            // exclusivePick, so there both teams count (MasterGameController.updatePlayerRoster marks both teams' picks taken).
            if (game.Members.Any(m => m != member && (m.Team == member.Team || game.IsDraft) && m.Champion == champion) ||
                game.Bots.Any(b => (b.Team == member.Team || game.IsDraft) && b.Champion == champion))
                return AirGameDtos.Game(game);
            member.Champion = champion;
            member.Skin = 0;
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    public object ClearPick(long account)
    {
        lock (_gate)
        {
            var game = RequirePicking(account);
            var member = MemberFor(game, account);
            RequireTurn(game, member);
            if (member.Locked) throw AirFault.UnselectClosed();
            member.Champion = 0;
            member.Skin = 0;
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    public object SelectSkin(long account, int champion, int skin)
    {
        lock (_gate)
        {
            var game = FindCurrent(account) ?? throw AirFault.NoGame();
            // ChampionSkinSelecter remains enabled during the post-pick countdown.
            // Launch takes the same gate, so no published match roster can change.
            if (game.State != "CHAMP_SELECT" &&
                !(game.State == "POST_CHAMP_SELECT" && _clock.GetUtcNow() < game.Deadline))
                throw AirFault.SelectionClosed();
            var member = MemberFor(game, account);
            if (member.Champion != champion) throw new ArgumentException("Select this champion first.");
            if (skin != 0 && !AirGameDtos.SkinIds(champion).Contains(skin)) throw new ArgumentException("Unsupported champion skin.");
            if (skin != 0 && !PlatformEconomy.OwnsSkin(accounts.Get(account), skin))
                throw new ArgumentException("This champion skin is not owned.");
            member.Skin = skin == 0 ? 0 : skin - champion * 1000;
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    /// <summary>
    /// Batch 500 (NOT RETAIL, see <see cref="AirGameDtos.SpellNamesFor"/>): a seat's saved spells must be ones the game's map offers. A saved pair
    /// the map does not offer (Teleport, Fortify or Rally on Dominion) gives the seat the platform default pair instead, so the match never names
    /// a spell its map refuses. The saved loadout itself is not changed.
    /// </summary>
    private static Member OfferedSeat(Member member, int mapId)
    {
        var offered = AirGameDtos.SpellNamesFor(mapId);
        if (!offered.ContainsKey(member.Spell1) || !offered.ContainsKey(member.Spell2))
        {
            member.Spell1 = AirGameDtos.SpellId(SummonerLoadout.DefaultFirst);
            member.Spell2 = AirGameDtos.SpellId(SummonerLoadout.DefaultSecond);
        }
        return member;
    }

    /// <summary>
    /// Batch 500 (NOT RETAIL: retail 1.0.0.126 has no Dominion bots, so the bot pair on map 8 is this server's): the same rule for a bot seat,
    /// whose spells are the shipped bot pair's names (<see cref="PlatformBots.Seat"/>).
    /// </summary>
    private static MatchPlayer OfferedBotSeat(MatchPlayer seat, int mapId)
    {
        var offered = AirGameDtos.SpellNamesFor(mapId).Values;
        return offered.Contains(seat.SummonerSpell1) && offered.Contains(seat.SummonerSpell2) ? seat
            : seat with { SummonerSpell1 = SummonerLoadout.DefaultFirst, SummonerSpell2 = SummonerLoadout.DefaultSecond };
    }

    public object SelectSpells(long account, int first, int second)
    {
        // Batch 455: every spell id the platform knows; the game's own list is checked under the lock, per game (Dominion's pair is map 8 only).
        var known = AirGameDtos.AllSpellNames;
        if (!known.ContainsKey(first) || !known.ContainsKey(second) || first == second)
            throw new ArgumentException("Select two distinct supported summoner spells.");
        lock (_gate)
        {
            var game = FindCurrent(account) ?? throw AirFault.NoGame();
            var offered = AirGameDtos.SpellNamesFor(game.MapId);
            if (!offered.ContainsKey(first) || !offered.ContainsKey(second) || first == second)
                throw new ArgumentException("Select two distinct supported summoner spells.");
            // A draft's ban phase is champion selection too (GameState.isInChampionSelectionState); blind pick never has it.
            if (game.State is not ("PRE_CHAMP_SELECT" or "CHAMP_SELECT") &&
                !(game.State == "POST_CHAMP_SELECT" && _clock.GetUtcNow() < game.Deadline))
                throw AirFault.SpellsClosed();
            var member = MemberFor(game, account);
            // The saved default loadout keeps the CLASSIC spells: a Dominion pick is for this game only.
            if (!AirGameDtos.DominionSpellNames.ContainsKey(first) && !AirGameDtos.DominionSpellNames.ContainsKey(second))
                accounts.SaveLoadout(account, accounts.Get(account).Loadout with
                { SummonerSpell1 = offered[first], SummonerSpell2 = offered[second] });
            member.Spell1 = first;
            member.Spell2 = second;
            Changed(game);
            return AirGameDtos.Game(game);
        }
    }

    public void LockIn(long account)
    {
        lock (_gate)
        {
            var game = RequirePicking(account);
            var member = MemberFor(game, account);
            RequireTurn(game, member);
            if (member.Champion == 0) throw new ArgumentException("Select a champion before locking in.");
            member.Locked = true;
            if (game.IsDraft)
            {
                if (game.Members.Where(m => m.PickTurn == game.PickTurn).All(m => m.Locked)) NextPickTurn(game);
            }
            else if (game.Members.All(m => m.Locked))
            { game.State = "POST_CHAMP_SELECT"; game.Deadline = _clock.GetUtcNow().AddSeconds(PostPickServerSeconds); }
            Changed(game);
        }
    }

    /// <summary>
    /// gameService.setClientReceivedGameMessage(gameId, "CHAMP_SELECT_CLIENT"): ChampionSelectionController.tryToAck sends it once its
    /// champion grid has loaded (batch 589, the dodge exemption in <see cref="DropFromSelection"/>). The call has no error handler, so a
    /// game the player is not in changes nothing.
    /// </summary>
    public void AcknowledgeChampionSelect(long account, int gameId)
    {
        lock (_gate)
            if (FindCurrent(account) is { } game && game.Id == gameId && game.Members.FirstOrDefault(m => m.Account.Id == account) is { } member)
                member.GridLoaded = true;
    }

    public void Quit(long account)
    {
        lock (_gate)
        {
            var game = FindCurrent(account);
            if (game is null) return;
            if (game.Result is not null) { ReturnFromResults(game, account); return; }
            if (game.HostId is not null && game.Result is null) throw new ArgumentException("This match is in progress; reconnect to return.");
            if (game.State != "TEAM_SELECT")
            {
                DropFromSelection(game, [MemberFor(game, account)], notifyLeavers: false);
                return;
            }
            game.Members.RemoveAll(m => m.Account.Id == account);
            if (game.Members.Count == 0) game.State = "TERMINATED";
            else if (game.Owner == account) game.Owner = game.Members[0].Account.Id;
            Changed(game);
        }
    }

    public void Kick(long owner, int id, long targetAccount)
    {
        lock (_gate)
        {
            var game = RequireMember(owner, id);
            if (game.IsMatchmade) throw new ArgumentException("Queue games do not have a lobby owner.");
            if (game.Owner != owner || targetAccount == owner || game.State != "TEAM_SELECT") throw new ArgumentException("Only the owner can remove another lobby member.");
            var target = MemberFor(game, targetAccount);
            game.Members.Remove(target);
            game.Banned.Add(targetAccount);
            Changed(game);
            Push(targetAccount, AirWire.Object(AirProtocolNames.GameNotification,
                ("type", "PLAYER_BANNED_FROM_GAME"), ("messageCode", ""), ("messageArgument", "")));
        }
    }

    /// <summary>gameService.selectBotChampion: the owner adds one bot to a team in the team-select lobby.</summary>
    public void AddBot(long owner, int champion, Team team, int skillLevel, string? skillLevelName = null)
    {
        lock (_gate)
        {
            var game = RequireBotEditor(owner);
            if (!PlatformBots.IsBotEnabled(champion)) throw new ArgumentException("This champion is not available as a bot.");
            if (skillLevel is < 0 or > PlatformBots.MaxSkillLevel) throw new ArgumentException("Unsupported bot difficulty.");
            if (game.Members.Count + game.Bots.Count >= game.Capacity) throw AirFault.GameIsFull();
            if (TeamCount(game, team) >= TeamLimit(game)) throw AirFault.TeamIsFull();
            // The stock picker offers a champion once per team; humans and bots share that rule.
            if (game.Bots.Any(b => b.Team == team && b.Champion == champion) ||
                game.Members.Any(m => m.Team == team && m.Champion == champion))
                throw new ArgumentException("That team already has this champion.");
            game.Bots.Add(new(champion, team, skillLevel,
                OfferedBotSeat(PlatformBots.Seat(champion, team, skillLevel), game.MapId), skillLevelName));
            Changed(game);
        }
    }

    /// <summary>gameService.removeBotChampion: the owner removes the bot holding a champion on a team.</summary>
    public void RemoveBot(long owner, int champion, Team team)
    {
        lock (_gate)
        {
            var game = RequireBotEditor(owner);
            var bot = game.Bots.FirstOrDefault(b => b.Team == team && b.Champion == champion)
                ?? throw new ArgumentException("That bot is not in the game.");
            game.Bots.Remove(bot);
            Changed(game);
        }
    }

    public object? Reconnect(long account)
    {
        lock (_gate)
        {
            var game = FindCurrent(account);
            if (game is null || game.HostId is null || game.Result is not null || !game.CredentialsSent) return null;
            return AirWire.Object(AirProtocolNames.GameReconnectInfoDTO, ("game", AirGameDtos.Game(game)),
                ("playerCredentials", Credentials(game, MemberFor(game, account))));
        }
    }

    public void SaveLoadout(long account, Func<PlatformAccount, MatchPlayer> update)
    {
        lock (_gate)
        {
            RequireEditableLoadout(account);
            accounts.SaveLoadout(account, update(accounts.Get(account)));
        }
    }

    // Mastery and rune pages are account data, not match fields: the roster handed to the host is a snapshot taken in
    // Launch, so a page saved at any time (including after the post-pick deadline, when the stock client still lets the
    // player press Save while "starting game" is shown) reaches the next match only. They must never be refused: the
    // stock call sites carry no error handler (SummonerServiceImpl.changeTalentRankings, SpellBookServiceImpl.saveSpellBook
    // and selectDefaultSpellBookPage pass none), and LcdsErrorHandler turns any fault without a handler into the generic
    // "unknown remote service error" window, which is the PvP.net error the owner saw after the countdown.
    public void SaveMasteries(long account, Dictionary<int, int> ranks)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        lock (_gate)
        {
            var current = accounts.Get(account);
            accounts.SaveLoadout(account, current.Loadout with { Masteries = ranks });
        }
    }

    // Lobby members hold an account snapshot. Refresh it so a lobby DTO shows
    // the new icon; the stock TEAM_SELECT view re-renders on a newer revision.
    public void SaveProfileIcon(long account, int icon, bool? tutorial = null, bool? help = null)
    {
        lock (_gate)
        {
            var current = accounts.Get(account);
            accounts.SaveSummonerPreferences(account, current.SummonerId, icon, tutorial, help);
            if (FindCurrent(account) is not { } game) return;
            MemberFor(game, account).Account = accounts.Get(account);
            if (game.HostId is null && current.ProfileIconId != icon) Changed(game);
        }
    }

    // Rune pages are account data too; see SaveMasteries for why these are never refused once a match is starting.
    public void SaveRunePages(long account, long summoner, IReadOnlyList<PlatformRunePage> pages)
    {
        lock (_gate)
        {
            accounts.SaveRunePages(account, summoner, pages);
        }
    }

    public void SelectRunePage(long account, long summoner, long page)
    {
        lock (_gate)
        {
            accounts.SelectRunePage(account, summoner, page);
        }
    }

    public int UseRuneCombiner(long account, int combiner, IReadOnlyList<int> runeIds)
    {
        lock (_gate)
        {
            RequireEditableLoadout(account);
            return accounts.UseRuneCombiner(account, combiner, runeIds);
        }
    }

    private void RequireEditableLoadout(long account)
    {
        var game = FindCurrent(account);
        // The stock controls remain active through the post-pick countdown.
        // Launch takes this same gate and freezes the roster at the deadline.
        if (game is not null && game.Result is null && game.State is not ("TEAM_SELECT" or "PRE_CHAMP_SELECT" or "CHAMP_SELECT") &&
            !(game.State == "POST_CHAMP_SELECT" && _clock.GetUtcNow() < game.Deadline))
            throw new ArgumentException("Your match loadout is locked.");
    }

    /// <summary>
    /// NOT RETAIL: server machinery. Runs one timer callback per period. A fault in a callback is logged with its stack, and the
    /// next period still runs, so one bad game cannot silence every launch, deadline and result.
    /// </summary>
    public static async Task RunTimerAsync(Action tick, TimeSpan period, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(tick);
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try { tick(); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"AIR platform timer fault; the timers keep running.{Environment.NewLine}{error}");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public void Tick()
    {
        lock (_gate)
        {
            PurgeQueue();
            MatchQueues();
            foreach (var game in _lobbies.Values.Where(g => !IsTerminal(g)).ToArray())
            {
                try
                {
                    // Reconstruction: a captain who does not ban in time gives up that ban, and the turn passes.
                    if (game.State == "PRE_CHAMP_SELECT" && _clock.GetUtcNow() >= game.Deadline) { NextBanTurn(game); Changed(game); }
                    if (game.State == "CHAMP_SELECT" && _clock.GetUtcNow() >= game.Deadline)
                    {
                        // Reconstruction: the client ships 'You were removed from champion selection for being AFK', so retail
                        // dropped a player who had made no pick by the deadline; the exact timing and any random pick are
                        // unknown. The drop is the same one a quitting player gets (see DropFromSelection). In a draft the
                        // deadline is the pick turn's, so only that turn's players are checked, and a pick not yet locked in
                        // is locked in for them.
                        var turn = game.Members.Where(m => !game.IsDraft || m.PickTurn == game.PickTurn).ToArray();
                        var afk = turn.Where(m => m.Champion == 0).ToArray();
                        if (afk.Length != 0) { DropFromSelection(game, afk, notifyLeavers: true); continue; }
                        foreach (var member in turn) member.Locked = true;
                        if (game.IsDraft) NextPickTurn(game);
                        else
                        {
                            game.State = "POST_CHAMP_SELECT";
                            game.Deadline = _clock.GetUtcNow().AddSeconds(PostPickServerSeconds);
                        }
                        Changed(game);
                    }
                    if (game.State == "TEAM_SELECT" && _clock.GetUtcNow() >= game.Deadline)
                    {
                        game.State = "TERMINATED";
                        game.TerminatedCondition = "LOBBY_EXPIRED";
                        Changed(game);
                    }
                    if (game.State == "POST_CHAMP_SELECT" && _clock.GetUtcNow() >= game.Deadline) Launch(game);
                    if (game.HostId is not Guid hostId) continue;
                    var owner = Guest(accounts.Get(game.Owner));
                    var remote = manager.List(owner).FirstOrDefault(g => g.Id == hostId);
                    if (game.Result is null && manager.ReadCompletedResult(owner, hostId) is { } saved)
                    {
                        var json = saved.Result;
                        var result = json.Deserialize<MatchResult>() ?? throw new InvalidDataException("Empty match result.");
                        // Bot seats report too; only the human seats map to lobby members.
                        if (result.SchemaVersion != 8 || result.Players.Count != game.Members.Count + game.Bots.Count ||
                            result.Players.Count(p => p.IsBot) != game.Bots.Count ||
                            !result.Players.Where(p => !p.IsBot).Select(p => p.PlayerId).Order()
                                .SequenceEqual(game.Members.Select(m => (ulong)m.Account.SummonerId).Order()))
                            throw new InvalidDataException("Match result does not match the frozen roster.");
                        accounts.RecordResult(new(game.Id, hostId, game.Members.Select(m => m.Account.Id).ToArray(), json,
                            saved.WrittenAt, game.QueueId));
                        game.Result = result;
                        game.StateSince = _clock.GetUtcNow();
                        foreach (var member in game.Members) PresenceChanged(member.Account.Id);
                        var receipt = accounts.FindResult(hostId);
                        // Every member is refreshed before the first push: each screen lists the others' levels and records.
                        foreach (var member in game.Members) member.Account = accounts.Get(member.Account.Id);
                        foreach (var member in game.Members)
                        {
                            // The record counters are the game's own summary (a ranked game's queue record, batch 586).
                            Push(member.Account.Id, AirGameDtos.Results(game, member,
                                receipt?.Rewards?.GetValueOrDefault(member.Account.Id), accounts.EconomyRules,
                                account => AirProfileDtos.Record(account, accounts.ResultsFor(account.Id), game.Queue?.Type), _clock.GetUtcNow(),
                                receipt?.Ratings?.GetValueOrDefault(member.Account.Id)));
                            Push(member.Account.Id, AirDtos.AccountBalanceNotification(member.Account));
                            // The stock post-game refresh reloads summoner data,
                            // not boost inventory. Replace remaining win counts.
                            Push(member.Account.Id, AirDtos.ActiveBoostsNotification(member.Account));
                        }
                    }
                    if (game.Result is not null) continue;
                    if (remote is null || remote.State is "Failed" or "Interrupted" or "Stopped")
                    { AbortMatch(game); continue; }
                    if (!game.CredentialsSent && remote.State == "Waiting for players")
                    {
                        game.CredentialsSent = true;
                        foreach (var member in game.Members) Push(member.Account.Id, Credentials(game, member));
                    }
                    if (remote.State == "Playing" && game.State != "IN_PROGRESS") { game.State = "IN_PROGRESS"; Changed(game); }
                }
                catch (Exception error) when (error is ArgumentException or RemoteFault or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    Console.Error.WriteLine($"AIR game {game.Id}: {error.GetType().Name}: {error.Message}; game aborted. See match logs.");
                    AbortMatch(game);
                }
                catch (Exception error)
                {
                    // NOT RETAIL: server machinery. An unexpected fault in this game is logged with its stack; the other games' timers keep running.
                    Console.Error.WriteLine($"AIR game {game.Id}: unexpected {error.GetType().Name} in its timer; other games keep running.{Environment.NewLine}{error}");
                }
            }
            // ponytail: bounded recent lobby cache; durable results remain in the account store.
            foreach (var old in _lobbies.Values.Where(g => IsTerminal(g) || g.Result is not null).OrderByDescending(g => g.Id).Skip(100).ToArray()) _lobbies.Remove(old.Id);
        }
    }

    /// <summary>
    /// A player leaves a game that has not launched (quit, or no pick by the deadline). MasterGameController reads the
    /// next DTO as the leaver's departure: a TEAM_SELECT game (custom) takes the others back to team selection with the
    /// 'X has left during champion selection and you have moved back to Team Selection' notice, and a TERMINATED
    /// matchmade game ('...placed back in the matchmaking queue') expects the server to have put them back in the queue
    /// already, since rejoinQueue(false) sends no new join. The leaver's own client is told only when it did not ask.
    /// </summary>
    private void DropFromSelection(Lobby game, IReadOnlyList<Member> leavers, bool notifyLeavers)
    {
        foreach (var leaver in leavers) game.Members.Remove(leaver);
        // Batch 589: leaving a matchmade game's champion selection, by quitting or by the AFK drop, is a dodge (PlatformAccounts.
        // DodgeStatus). The client's championSelection_championsNotLoaded, shown when its grid never loaded and so never acknowledged,
        // promises "You will not get a penalty for leaving during this champion selection, IF you do not choose a champion": that
        // leaver is spared.
        if (game.IsMatchmade)
            foreach (var leaver in leavers.Where(leaver => leaver.GridLoaded || leaver.Champion != 0))
                accounts.RecordDodge(leaver.Account.Id, _clock.GetUtcNow());
        var remaining = game.Members.ToArray();
        if (remaining.Length == 0) game.State = "TERMINATED";
        else if (game.IsMatchmade)
        {
            game.State = "TERMINATED";
            // The old sequence numbers put them back ahead of everyone who joined later, in their own queue (and Co-op difficulty), each
            // party whole. A leaver's party mates are not put back: their client sees a teammate among the leavers and goes home instead
            // (the MasterGameController.currentGame setter rejoins only when isSolo || !inviteController.arePlayersOnTeam(leavers)).
            var leaverParties = game.QueueEntries.Where(e => leavers.Any(l => l.Account.Id == e.AccountId)).Select(e => e.Party).ToHashSet();
            foreach (var entry in game.QueueEntries.Where(e => !leaverParties.Contains(e.Party) && remaining.Any(m => m.Account.Id == e.AccountId)))
            {
                entry.ReplySent = true;
                _queue.Add(entry);
            }
        }
        else
        {
            game.State = "TEAM_SELECT";
            game.PickTurn = 1;
            game.BannedChampions.Clear();
            foreach (var member in remaining) { member.Champion = 0; member.Skin = 0; member.Locked = false; member.PickTurn = 0; }
            if (leavers.Any(l => l.Account.Id == game.Owner)) game.Owner = remaining[0].Account.Id;
            game.Deadline = _clock.GetUtcNow().AddMinutes(30);
        }
        Changed(game);
        foreach (var leaver in leavers)
        {
            PresenceChanged(leaver.Account.Id);
            if (notifyLeavers) Push(leaver.Account.Id, AirGameDtos.Game(game, "TERMINATED"));
        }
    }

    /// <param name="delay">A LeaverBuster low-priority wait still ahead of the player, added to the queue's own estimate.</param>
    private AsObject QueueInfoObject(int queueId, TimeSpan delay = default)
    {
        var now = _clock.GetUtcNow();
        var entries = _queue.Where(entry => entry.QueueId == queueId).ToArray();
        var first = entries.MinBy(entry => entry.Sequence);
        var wait = first is null ? 0 : Math.Max(0, (int)Math.Min(int.MaxValue,
            (now - first.Joined).TotalSeconds));
        return AirGameDtos.QueueInfo(queueId, entries.Length, wait + Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds)));
    }

    // An entry whose connection no longer signs in its account, or whose player is in a game, leaves with its whole party.
    private void PurgeQueue()
    {
        foreach (var entry in _queue.Where(entry => (AccountForConnection is not null && AccountForConnection(entry.Connection) != entry.AccountId) ||
                     FindCurrent(entry.AccountId) is not null).ToArray())
            if (_queue.Contains(entry)) LeaveQueue(entry);
    }

    /// <summary>
    /// A queue entry leaves: a cancel, a dropped connection, a newer attach or an accepted invitation. A party leaves whole. Its other
    /// members get a GameNotification PLAYER_QUIT naming the leaver's summoner id: the stock client prefixes "sum" to look the name up and
    /// shows invite_participant_teammate_quit_while_in_queue, "{0} left the game. Your team has been disbanded.", then goes home
    /// (MasterGameController.handleMatchedGamePlayerQuitWhileInQueue). Reconstruction: that retail disbanded the team for any member.
    /// </summary>
    private void LeaveQueue(QueueEntry leaver)
    {
        var unit = _queue.Where(entry => entry.Party == leaver.Party).ToArray();
        _queue.RemoveAll(entry => entry.Party == leaver.Party);
        var argument = accounts.Find(leaver.AccountId)?.SummonerId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        foreach (var other in unit.Where(entry => entry != leaver))
            Push(other.AccountId, AirWire.Object(AirProtocolNames.GameNotification,
                ("type", "PLAYER_QUIT"), ("messageCode", ""), ("messageArgument", argument)));
    }

    /// <summary>
    /// One game per queue (and Co-op difficulty) per tick. The units, a solo player or a whole party whose leader's reply was sent, are
    /// taken in arrival order, each onto the first team it fits (Reconstruction: retail matched normal games by a hidden rating, which this
    /// host has none of). A ranked queue matches by its rating instead (<see cref="RankedTeams"/>, batch 586).
    /// A normal queue waits for two full teams of humans. A Co-op game takes up to five humans of one difficulty, all on team one
    /// (Reconstruction: the side), and the bots fill every other seat.
    /// </summary>
    private void MatchQueues()
    {
        foreach (var rule in Queues)
            foreach (var group in _queue.Where(entry => entry.QueueId == rule.Id).GroupBy(entry => entry.BotSkill).ToArray())
            {
                if (_lobbies.Values.Count(g => !IsTerminal(g) && g.Result is null) >= 32) return;
                // ponytail: first fit in arrival order; a big party can wait while solos fill both sides. Pack smarter if parties starve.
                var teams = Enumerable.Range(0, rule.Type == "BOT" ? 1 : 2).Select(_ => new List<QueueEntry>()).ToArray();
                // A unit in a LeaverBuster wait is skipped until it is matchable, then queues behind every unit matchable before it
                // (batch 587); MayMatch is batch 586's hook for any further per-account gate.
                var now = _clock.GetUtcNow();
                var units = group.GroupBy(entry => entry.Party)
                    .Where(unit => unit.All(entry => entry.ReplySent && entry.MatchableAt <= now && MayMatch(entry.AccountId, entry.Joined)))
                    .OrderBy(unit => unit.Max(entry => entry.MatchableAt)).ThenBy(unit => unit.Key)
                    .Select(unit => unit.OrderBy(entry => entry.Sequence).ToArray()).ToArray();
                if (rule.Ranked)
                {
                    if (RankedTeams(rule, units) is { } ranked) StartQueueGame(rule, ranked);
                    continue;
                }
                foreach (var unit in units)
                    teams.FirstOrDefault(side => side.Count + unit.Length <= rule.TeamSize)?.AddRange(unit);
                if (teams[0].Count == 0) continue;
                if (teams.Any(side => side.Count < rule.TeamSize) &&
                    (rule.Type != "BOT" || now - teams[0][0].MatchableAt < CoopGatherTime)) continue;
                StartQueueGame(rule, teams);
            }
    }

    private void StartQueueGame(QueueRule rule, List<QueueEntry>[] teams)
    {
        var now = _clock.GetUtcNow();
        var id = accounts.AllocateGameId();
        var picked = teams.SelectMany(side => side).ToArray();
        // Batch 571: each queue game has its own name. The stock client names its champion-select rooms after it (name + "_ta"/"_tb",
        // name + "_c1"/"_c2", see MayEnterRoom), so two games of one shared name shared their rooms. The id makes it unique, and the client
        // shows the name only in its "Game {0} either failed to start" notice (PG-0019, NewGameViews.as:730).
        var game = new Lobby(id, $"{rule.GameName} {id}", "", picked[0].AccountId, 2 * rule.TeamSize)
        {
            QueueId = rule.Id,
            MapId = rule.MapId,
            ConfigId = rule.ConfigId,
            Created = now,
        };
        var coop = rule.Type == "BOT";
        for (var side = 0; side < teams.Length; side++)
            foreach (var entry in teams[side])
                game.Members.Add(OfferedSeat(new(accounts.Get(entry.AccountId), side == 0 ? Team.Order : Team.Chaos) { Joined = now }, rule.MapId));
        if (coop)
        {
            // The Co-op bots are retail 126's: the 18 champions whose game_bot_ name the client ships (ChampionCatalog.RetailBot), as far
            // as the server runs them. Reconstruction: which of them retail chose; here a random one per seat, no champion twice.
            var pool = ChampionCatalog.All.Where(c => c.RetailBot && PlatformBots.IsBotEnabled(c.Id)).Select(c => c.Id).ToArray();
            Random.Shared.Shuffle(pool);
            var seats = Enumerable.Repeat(Team.Order, rule.TeamSize - picked.Length).Concat(Enumerable.Repeat(Team.Chaos, rule.TeamSize)).ToArray();
            if (pool.Length < seats.Length) throw new InvalidOperationException("Too few bot champions for a Co-op vs. AI game.");
            var skill = picked[0].BotSkill;
            for (var seat = 0; seat < seats.Length; seat++)
                game.Bots.Add(new(pool[seat], seats[seat], skill, OfferedBotSeat(PlatformBots.Seat(pool[seat], seats[seat], skill), rule.MapId)));
        }
        // A draft queue (14, 17) runs batch 543's draft: two humans' teams always have captains, so it opens with the bans.
        BeginSelection(game);
        game.QueueEntries.AddRange(picked);
        _lobbies.Add(game.Id, game);
        _queue.RemoveAll(entry => picked.Contains(entry));
        Changed(game);
    }

    private void Launch(Lobby game)
    {
        foreach (var member in game.Members)
        {
            // Batch 580 (Reconstruction): the Basic Tutorial lends its champion, owned or not (PlatformGames.Tutorial); batch 669: Battle
            // Training its three (Pick).
            if (game.GameMode == GameMaps.TutorialMode) continue;
            var account = accounts.Get(member.Account.Id);
            if (!PlatformEconomy.CanPlay(account, member.Champion, accounts.EconomyRules) ||
                (member.Skin != 0 && !PlatformEconomy.OwnsSkin(account, member.Champion * 1000 + member.Skin)))
                throw new ArgumentException("A selected champion or skin is no longer available.");
        }
        // The spells this game's map offers (Garrison on The Crystal Scar, batch 455): the pick was checked against the same list.
        var offered = AirGameDtos.SpellNamesFor(game.MapId);
        var humans = game.Members.Select(m => accounts.Get(m.Account.Id).Loadout with
        {
            Champion = AirGameDtos.ChampionNames[m.Champion], Team = m.Team, SkinId = m.Skin,
            SummonerSpell1 = offered[m.Spell1], SummonerSpell2 = offered[m.Spell2],
            PlayerId = checked((int)m.Account.SummonerId),
            // DIAGNOSTICS: who may use the cheat commands (MayCheat), decided as the game starts.
            CanCheat = MayCheat(m.Account.Id)
        }).ToArray();
        // Bot seats follow the humans, so member index + 1 stays each human's seat number.
        // NOT RETAIL: roster names must be unique here, so the same champion on both teams gets a
        // numeric suffix; the client still names a champion with a game_bot_ key itself.
        var taken = new HashSet<string>(humans.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var bots = game.Bots.Select(bot =>
        {
            var name = PlatformBots.MatchName(bot.Champion);
            for (var copy = 2; !taken.Add(name); copy++) name = $"{PlatformBots.MatchName(bot.Champion)} {copy}";
            return bot.Loadout with { Name = name, CanCheat = false };
        }).ToArray();
        var players = humans.Concat(bots).ToArray();
        var host = manager.CreateReserved(Guest(accounts.Get(game.Owner)), new(game.RequestId, players, game.MapId, game.Mode),
            game.Members.Select(m => Guest(m.Account)).ToArray(),
            id => accounts.PrepareMatch(new(game.Id, id, game.QueueId, game.Members.Select(m => m.Account.Id).ToArray(), players)));
        game.HostId = host.Id;
        game.State = "START_REQUESTED";
        Changed(game);
    }

    private AsObject Credentials(Lobby game, Member member)
    {
        var seat = game.Members.IndexOf(member) + 1;
        // The platform is the authority for its own seats: their cheat rights were set from the session origin at launch.
        var join = manager.Join(Guest(member.Account), game.HostId!.Value, seat, local: true);
        return AirWire.Object(AirProtocolNames.PlayerCredentialsDto, ("gameId", (double)game.Id),
            ("playerId", (double)join.PlayerId), ("serverIp", advertisedHost), ("serverPort", join.Port),
            ("encryptionKey", join.Key), ("observer", false), ("handshakeToken", ""));
    }

    private void RequireFree(long account, string targetName, bool joining)
    {
        if (_queue.FirstOrDefault(entry => entry.AccountId == account) is { } queued)
            throw AirFault.AlreadyInGame(joining, targetName, queued.QueueId, "Leave the queue first.");
        var current = FindCurrent(account);
        if (current?.Result is not null) { ReturnFromResults(current, account); return; }
        if (current is not null) throw AirFault.AlreadyInGame(joining, targetName, current.Id);
    }
    private static int TeamLimit(Lobby game) => Math.Min(MatchRoster.MaxTeamPlayers, (game.Capacity + 1) / 2);
    private static int TeamCount(Lobby game, Team team) =>
        game.Members.Count(m => m.Team == team) + game.Bots.Count(b => b.Team == team);
    // computeCanAddBotsToGame: the owner of a Map1 team-select lobby; never a queue game (Co-op seats its bots itself).
    private Lobby RequireBotEditor(long account)
    {
        var game = FindCurrent(account) ?? throw AirFault.NoGame();
        if (game.IsMatchmade) throw new ArgumentException("Queue games take no lobby bots.");
        if (game.Owner != account || game.State != "TEAM_SELECT" || game.HostId is not null)
            throw new ArgumentException("Only the lobby owner can change bots before champion selection.");
        return game;
    }
    private Lobby Find(int id) => _lobbies.GetValueOrDefault(id) ?? throw AirFault.NoGame();
    private void ReturnFromResults(Lobby game, long account)
    {
        game.Returned.Add(account);
        if (game.Returned.Count == game.Members.Count) game.State = "TERMINATED";
    }
    private static bool IsTerminal(Lobby game) => game.State is "TERMINATED" or "FAILED_TO_START" or "TERMINATED_IN_ERROR";
    private Lobby? FindCurrent(long account) => _lobbies.Values.FirstOrDefault(g => !IsTerminal(g) && !g.Returned.Contains(account) && g.Members.Any(m => m.Account.Id == account));
    private Lobby RequireMember(long account, int id)
    {
        var game = Find(id); _ = MemberFor(game, account); return game;
    }
    private Lobby RequirePicking(long account)
    {
        var game = FindCurrent(account) ?? throw AirFault.NoGame();
        if (game.State != "CHAMP_SELECT") throw AirFault.SelectionClosed();
        return game;
    }
    // A draft player acts only in their own pick turn; PG-0030 is the client's "no longer your turn" (a pick racing the turn's end).
    private static void RequireTurn(Lobby game, Member member)
    {
        if (game.IsDraft && member.PickTurn != game.PickTurn) throw AirFault.NotYourTurn();
    }
    private static Member MemberFor(Lobby game, long account) => game.Members.FirstOrDefault(m => m.Account.Id == account) ?? throw AirFault.NoGame();
    private void AbortMatch(Lobby game)
    {
        // Once launch begins, stock AIR treats plain TERMINATED as a completed
        // game and opens results. An aborted server has no authoritative results.
        game.State = game.HostId is null ? "TERMINATED" :
            game.CredentialsSent ? "TERMINATED_IN_ERROR" : "FAILED_TO_START";
        Changed(game);
    }

    private void Changed(Lobby game)
    {
        game.Version++;
        if (game.StateSeen != game.State) { game.StateSeen = game.State; game.StateSince = _clock.GetUtcNow(); }
        Console.WriteLine($"AIR game={game.Id} state={game.State} revision={game.Version} players={game.Members.Count}");
        if (game.State is "START_REQUESTED" or "IN_PROGRESS" or "TERMINATED" or
            "FAILED_TO_START" or "TERMINATED_IN_ERROR")
            foreach (var member in game.Members) PresenceChanged(member.Account.Id);
        var dto = AirGameDtos.Game(game);
        foreach (var member in game.Members) Push(member.Account.Id, dto);
    }
}
