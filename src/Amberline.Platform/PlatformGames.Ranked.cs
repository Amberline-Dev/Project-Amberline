namespace Amberline.Platform;

/// <summary>
/// Batch 586: the ranked queues of 1.0.0.126 (owner decision, 1 November 2026). What the stock client states:
/// <list type="bullet">
/// <item>The play menu (ChooseGameFlowViewLayoutDefinition.as) has a Ranked draft leaf on Summoner's Rift 5v5 (soloid 4, partyid 6),
/// Twisted Treeline 3v3 (soloid 11, partyid 9) and The Crystal Scar 5v5 (18, 19). QueueType names them RANKED_SOLO_5x5,
/// RANKED_PREMADE_5x5, RANKED_SOLO_3x3, RANKED_PREMADE_3x3 and ODIN_RANKED_*; PlayerStatSummaryType.convert gives each its own
/// summary, so each queue has its own rating. The profile's ranked view (ProfileStatsSummaryPresentationModel, ProfileStatsRankingView)
/// shows three of them, RANKED_PREMADE_3x3, RANKED_SOLO_5x5 and RANKED_PREMADE_5x5, the three Season One launched with ("3v3 Premade,
/// 5v5 Premade, or 5v5 Solo", wiki Season_One). Batch 586 ran 4, 6 and 9. NOT RETAIL (owner decision, 1 November 2026, batch 590): 11
/// and the Dominion pair 18/19 run too, although retail 126 offered no ranked solo 3v3 row and Dominion launched in September 2011 without
/// ranked; the menu leaves exist, so the client drives them like the others. Their ratings have no profile row (the ranked view and
/// PresenceController.findHighestRankedQueue read only the three above): they show on the end-of-game rating line, in recent games
/// ("(ranked game)") and on the ladder page.</item>
/// <item>There are no named ranked teams in 126: the client's TeamService is FakeTeamService (ClientModuleServices.as:88, a local stub
/// with only createNewTeam, answered after 200 ms with no remote call), the RANKED_TEAMS module mod_rt.dat does not ship and
/// RiotApplication.loadModuleRT defaults to false. The "team" queues are the arranged-team premade queues: the bundle calls their games
/// "Ranked Team Game" (practiceGame_gameMode_RANKED_GAME_PREMADE) and the buddy tooltip "Ranked 5x5 Team:" / "Ranked 3x3 Team:". Each
/// player has a premade rating; a party queues with its members' ratings (batch 579's party flow).</item>
/// <item>Party sizes: CreateNormalArrangedGame.as:1201 enables Start Game for a ranked party of minimumParticipantListSize players, or of
/// two when maximumParticipantListSize is 5, with the tooltips "You must have either 2 or {0} players" (5v5) and "You must have {0}
/// players" (3v3); MasterGameViewController.joinQueue_attach moves a party smaller than the premade minimum to the RANKED_SOLO_5x5 queue
/// (GameQueueManager.joinEquivalentPremadeSoloQueue). So 4 takes one or two (solo/duo), 6 exactly five, 9 exactly three.</item>
/// <item>Eligibility: the menu enables a ranked leaf from the queue's minLevel ("You must be at least level {0} before you can join ranked
/// games"); the server answers FailedJoinPlayer RANKED_MIN_LEVEL / RANKED_NUM_CHAMPS / RANKED_MAX_LEVEL, which RankedQueueFailuresDialog
/// shows per summoner: "Minimum level", "must have at least 14 champions available." (bundle matchmaking_join_rankedqueue_reason_*).</item>
/// </list>
/// </summary>
public sealed partial class PlatformGames
{
    public const int RankedSoloQueueId = 4;
    public const int RankedTeamQueueId = 6;
    public const int RankedTreelineTeamQueueId = 9;
    public const int RankedTreelineSoloQueueId = 11;
    public const int DominionRankedSoloQueueId = 18;
    public const int DominionRankedTeamQueueId = 19;
    /// <summary>Season One: "summoners of Level 30 will be able to indulge in the ranked game system" (wiki Season_One); the queue's minLevel.</summary>
    public const int RankedMinLevel = 30;
    /// <summary>The bundle's matchmaking_join_rankedqueue_reason_champs: 14 in 126 (the four 2011 draft bans and ten picks; 16 came with six bans).</summary>
    public const int RankedMinChampions = 14;

    // Every ranked queue picks in Draft Mode (the menu's Ranked leaves are data="true;draft_mode"). Reconstruction: the names; the game name
    // stems are the bundle's practiceGame_gameMode_RANKED_GAME_SOLO "Ranked Solo Game" and _PREMADE "Ranked Team Game".
    private static QueueRule[] RankedQueues =>
    [
        new(RankedSoloQueueId, "RANKED_SOLO_5x5", "RANKED_GAME_SOLO", PlatformStoreCalendar.NormalRiftMapId, 5, "Ranked Solo/Duo 5v5",
            "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 2),
        new(RankedTeamQueueId, "RANKED_PREMADE_5x5", "RANKED_GAME_PREMADE", PlatformStoreCalendar.NormalRiftMapId, 5, "Ranked Team 5v5",
            "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 5),
        new(RankedTreelineTeamQueueId, "RANKED_PREMADE_3x3", "RANKED_GAME_PREMADE", PlatformStoreCalendar.TwistedTreelineMapId, 3,
            "Ranked Team 3v3", "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 3),
        // Batch 590. Solo only: joinEquivalentPremadeSoloQueue moves a party only from RANKED_PREMADE_5x5 to RANKED_SOLO_5x5, so a TT or a
        // Dominion party below the premade size never reaches 11 or 18. 19 takes a full team, like 6. Dominion is map 8 in mode ODIN
        // (the catalog's and GameDTO's gameMode follow the map), with map 8's summoner spells (OfferedSeat); results keep the map 8 score
        // block (batch 519). Reconstruction: the names and 19's sizes (Dominion ranked never ran in retail 126).
        new(RankedTreelineSoloQueueId, "RANKED_SOLO_3x3", "RANKED_GAME_SOLO", PlatformStoreCalendar.TwistedTreelineMapId, 3,
            "Ranked Solo 3v3", "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 1),
        new(DominionRankedSoloQueueId, "ODIN_RANKED_SOLO", "RANKED_GAME_SOLO", PlatformStoreCalendar.CrystalScarMapId, 5,
            "Dominion Ranked Solo 5v5", "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 1),
        new(DominionRankedTeamQueueId, "ODIN_RANKED_PREMADE", "RANKED_GAME_PREMADE", PlatformStoreCalendar.CrystalScarMapId, 5,
            "Dominion Ranked Team 5v5", "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 5),
    ];

    /// <summary>
    /// The hook for LeaverBuster (batch 587 owns leaver tracking and the low-priority delay): whether a queue entry of this account, which
    /// joined at this time, may be matched now. Every queue's matcher asks it for every entry; a party waits for its slowest member.
    /// </summary>
    public Func<long, DateTimeOffset, bool> MayMatch { get; set; } = (_, _) => true;

    /// <summary>The failures RankedQueueFailuresDialog lists, one per summoner and reason. Reconstruction: "available" champions are the owned
    /// ones; whether retail counted the free rotation is not in the client (Champion.isAvailable counts it for picking).</summary>
    private (PlatformAccount Player, string Reason)[] RankedFailures(IEnumerable<PlatformAccount> players) =>
        players.SelectMany(player => new[]
        {
            player.Loadout.SummonerLevel < RankedMinLevel ? "RANKED_MIN_LEVEL" : null,
            OwnedChampions(player) < RankedMinChampions ? "RANKED_NUM_CHAMPS" : null,
        }.OfType<string>().Select(reason => (player, reason))).ToArray();

    private static int OwnedChampions(PlatformAccount player) => player.Economy is null || player.Economy.AllUnlocked
        ? AirGameDtos.ChampionNames.Count : player.Economy.OwnedChampions?.Distinct().Count(AirGameDtos.ChampionNames.ContainsKey) ?? 0;

    /// <summary>
    /// Reconstruction (retail's matchmaker is not in the shipped files, which only show an Elo rating per ranked queue): each unit in
    /// arrival order may anchor a game. Its candidates are the units whose rating (a party's mean) is within <see cref="RatingWindow"/> of
    /// the anchor's, closest first, and each joins the side it fits with the lower rating total, so the two teams' means stay close. The
    /// first anchor whose two sides fill starts the game. The window widens while the anchor waits, so a small group still plays.
    /// ponytail: every anchor is tried every tick, O(units²) on at most the queue's 100 entries.
    /// </summary>
    private List<QueueEntry>[]? RankedTeams(QueueRule rule, QueueEntry[][] units)
    {
        var rated = units.Select(unit => (Unit: unit, Rating: unit.Average(entry => (double)accounts.Rating(entry.AccountId, rule.Type)))).ToArray();
        var now = _clock.GetUtcNow();
        foreach (var anchor in rated)
        {
            var window = RatingWindow(now - anchor.Unit[0].Joined);
            var teams = new[] { new List<QueueEntry>(), new List<QueueEntry>() };
            var totals = new double[2];
            foreach (var (unit, rating) in rated.Where(unit => Math.Abs(unit.Rating - anchor.Rating) <= window)
                         .OrderBy(unit => unit.Unit == anchor.Unit ? -1 : Math.Abs(unit.Rating - anchor.Rating)))
            {
                var side = teams[0].Count + unit.Length > rule.TeamSize ? 1
                    : teams[1].Count + unit.Length > rule.TeamSize || totals[0] <= totals[1] ? 0 : 1;
                if (teams[side].Count + unit.Length > rule.TeamSize) continue;
                teams[side].AddRange(unit);
                totals[side] += rating * unit.Length;
            }
            if (teams.All(side => side.Count == rule.TeamSize)) return teams;
        }
        return null;
    }

    /// <summary>Reconstruction: 150 rating points, plus 10 for every second the anchor has waited.</summary>
    internal static double RatingWindow(TimeSpan waited) => 150 + 10 * Math.Max(0, waited.TotalSeconds);
}
