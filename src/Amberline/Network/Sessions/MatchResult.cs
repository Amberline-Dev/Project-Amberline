using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

/// <summary>Detached final state; untracked aggregate statistics are deliberately absent.</summary>
public sealed record MatchResult(
    int SchemaVersion, string ClientVersion, int MapId, string Mode,
    string Winner, double DurationSeconds, IReadOnlyList<PlayerMatchResult> Players, bool Surrendered)
{
    internal static MatchResult Capture(IReadOnlyList<SessionParticipant> participants,
        Team winner, double durationSeconds, bool surrendered, double now, LeaverRules? leaverRules = null,
        int mapId = GameMaps.Normal, WorldState? world = null, string? mode = null)
    {
        var players = participants.Select(seat =>
        {
            var hero = seat.Hero ?? throw new InvalidOperationException("A final roster seat has no hero.");
            var statistics = hero.MatchStatistics.Snapshot(now);
            // Batch 518: map 8's personal score block (.artifacts/deep-dive/dominion-score-contract.md), every seat; the team's final nexus
            // health is the crystal's whole health (0 once it fell).
            var odin = mapId != GameMaps.CrystalScar ? null : new OdinPlayerScore(
                TotalScore: hero.OdinCombatScore + hero.OdinObjectiveScore, CombatScore: hero.OdinCombatScore,
                ObjectiveScore: hero.OdinObjectiveScore, NodeCaptures: hero.NodeCaptures, NodeCaptureAssists: hero.NodeCaptureAssists,
                NodeNeutralizes: hero.NodeNeutralizes, NodeNeutralizeAssists: hero.NodeNeutralizeAssists,
                NodeKillsOffense: hero.NodeKillsOffense, NodeKillsDefense: hero.NodeKillsDefense, TeamObjectives: hero.TeamObjectives,
                VictoryPoints: world is null ? 0 : (int)Math.Floor(Amberline.Simulation.Combat.CombatService.DominionNexusHealth(world, seat.Team)));
            return new PlayerMatchResult(seat.PlayerId, hero.NetId, seat.Name, seat.ChampionName,
                seat.Team.ToString(), hero.SkinId, seat.ConnectionId is not null,
                hero.Level, hero.ChampionKills, hero.ChampionDeaths, hero.ChampionAssists,
                hero.KillingSprees, hero.LargestKillingSpree,
                hero.TurretsKilled, hero.InhibitorsKilled, hero.NexusesKilled,
                hero.LaneMinionsKilled, hero.OtherMinionsKilled, hero.NeutralMinionsKilled, hero.Gold, hero.Experience,
                statistics,
                Array.AsReadOnly(hero.SummonerSpells.ToArray()),
                Array.AsReadOnly(hero.SpellRanks.ToArray()),
                Array.AsReadOnly(hero.Inventory.Select(item => item is null ? null :
                    new MatchResultItem(item.ItemId, item.Count, item.Charges)).ToArray()),
                IsBot: seat.IsBot, WasAfk: statistics.WasAfk,
                Leaver: !seat.IsBot && leaverRules is not null &&
                    leaverRules.IsLeaver(seat.Team == winner, statistics.TimePlayed, statistics.TimeSpentDisconnected,
                        seat.ConnectionId is null, statistics.TimeSinceLastDisconnect),
                Odin: odin);
        }).ToArray();
        return new(8, LeagueProtocolConstants.VersionString, mapId, mode ?? GameMaps.ModeOf(mapId), winner.ToString(),
            durationSeconds, Array.AsReadOnly(players), surrendered);
    }
}

public sealed record PlayerMatchResult(
    ulong PlayerId, uint NetId, string Name, string Champion, string Team, int SkinId,
    bool Connected, int Level, int Kills, int Deaths, int Assists,
    int KillingSprees, int LargestKillingSpree, int TurretsKilled, int InhibitorsKilled, int NexusesKilled,
    int LaneMinionsKilled, int OtherMinionsKilled, int NeutralMinionsKilled,
    double GoldOnHand, double Experience, HeroMatchStatisticsSnapshot Statistics,
    IReadOnlyList<string> SummonerSpells,
    IReadOnlyList<int> SpellRanks, IReadOnlyList<MatchResultItem?> Inventory,
    bool IsBot = false, bool WasAfk = false, bool Leaver = false, OdinPlayerScore? Odin = null)
{
    /// <summary>
    /// The seat counts as having left: the shipped disconnect rule (<see cref="LeaverRules"/>) or the latched AFK
    /// classification (Reconstruction: an emulator inactivity policy, kept so an idle player is never rewarded).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLeaver => Leaver || WasAfk;
}

public sealed record MatchResultItem(int ItemId, int Count, int Charges);

/// <summary>
/// Dominion (map 8) personal score totals at the end of the match; null on the other maps. The Platform turns each field into the
/// stock AIR stat of the same meaning (.artifacts/deep-dive/dominion-score-contract.md).
/// </summary>
public sealed record OdinPlayerScore(
    int TotalScore = 0, int CombatScore = 0, int ObjectiveScore = 0,
    int NodeCaptures = 0, int NodeCaptureAssists = 0, int NodeNeutralizes = 0, int NodeNeutralizeAssists = 0,
    int NodeKillsOffense = 0, int NodeKillsDefense = 0, int TeamObjectives = 0, int VictoryPoints = 0);
