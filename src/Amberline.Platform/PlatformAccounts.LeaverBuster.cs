using System.Text.Json;
using Amberline.Network.Sessions;

namespace Amberline.Platform;

/// <summary>
/// An account's LeaverBuster standing (batch 587): <see cref="Leaves"/> among its last <see cref="Games"/> matchmade games, the
/// login packet's <c>leaverPenaltyLevel</c> (<see cref="Level"/>), and the queue-entry delay with the matchmade games it still
/// applies to.
/// </summary>
public sealed record PlatformLeaverStatus(int Leaves, int Games, int Level, TimeSpan QueueDelay, int DelayedGamesLeft);

/// <summary>
/// An account's champion-select dodge timer (batch 589): its <see cref="Dodges"/> in the <see cref="PlatformAccounts.DodgePeriod"/>
/// before the newest one, and the time left before it may join a queue again.
/// </summary>
public sealed record PlatformDodgeStatus(int Dodges, TimeSpan Remaining);

public sealed partial class PlatformAccounts
{
    // What the 126 client has (ApplicationController.checkLeaverPenaltyPopup): every login with leaverPenaltyLevel > 0 shows
    // leaver_penalty_warning_<level> (capped at 4) under "Leaver & AFK Penalty Warning"; level 1 warns, 2 and 3 name the repeat
    // offences, 4 threatens a long ban. Its queue ribbon prints the join reply's QueueInfo.waitTime as "Approx" (GameQueueManager.
    // setQueueInfo, MatchmakingWaitingStatus), which is where the delay shows. The client holds no window, threshold or delay.
    // Reconstruction (owner, 1 November 2026: "LeaverBuster like retail"): the numbers. A level is the leaves among the last
    // 20 matchmade games (custom games never count), at most 4. From level 2 the account waits before it can be matched, 5, 10
    // and 15 minutes at levels 2, 3 and 4: the first three low-priority tiers of retail's later LeaverBuster (League wiki,
    // "LeaverBuster"), which also ran each tier for 5 games; here the wait applies until 5 matchmade games have been completed
    // since the newest leave, and the warning lasts until the leaves fall out of the window. No 2011 source gives retail's own
    // numbers, and nothing here bans an account (the host decides; see ClearLeaves).
    public const int LeaverWindowGames = 20;
    public const int LowPriorityGames = 5;
    private static readonly TimeSpan[] LowPriorityDelays =
        [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];

    /// <summary>
    /// A leave as the game server reported it, without the ones the player made good: latched AFK always counts (owner, 18 October
    /// 2026: an AFK kick is a leaver), the shipped disconnect rule (<see cref="LeaverRules"/>) only when the player was still gone
    /// at the end, so a reconnect that finished the game is forgiven.
    /// </summary>
    public static bool CountsAsLeave(PlayerMatchResult player) => player.WasAfk || (player.Leaver && !player.Connected);

    public PlatformLeaverStatus LeaverStatus(long accountId)
    {
        lock (_gate)
        {
            var account = RequireAccount(accountId);
            var summoner = (ulong)account.SummonerId;
            // Newest first: storage order breaks ties of the completion time.
            var left = _state.Results.Where(r => r.Accounts.Contains(accountId) && PlatformGames.QueueOf(r.QueueId) is not null &&
                    (account.LeavesClearedAt is not { } cleared || r.CompletedAt > cleared))
                .Reverse().OrderByDescending(r => r.CompletedAt).Take(LeaverWindowGames)
                .Select(r => r.Result.Deserialize<MatchResult>(Json)?.Players.FirstOrDefault(p => !p.IsBot && p.PlayerId == summoner) is { } p &&
                    CountsAsLeave(p))
                .ToArray();
            var leaves = left.Count(leave => leave);
            var level = Math.Min(leaves, 4);
            var owed = level < 2 ? 0 : Math.Max(0, LowPriorityGames - Array.IndexOf(left, true));
            return new(leaves, left.Length, level, owed > 0 ? LowPriorityDelays[level] : TimeSpan.Zero, owed);
        }
    }

    // Batch 589, the champion-select dodge timer. What the 126 client has: a queue join is refused with a
    // QueueDodger row (AirProtocolNames.QueueDodger; reasonFailed QUEUE_DODGER, dodgePenaltyRemainingTime in milliseconds) per
    // dodger, which QueueDodgeDialog counts down, "Because you abandoned a recent game during champion selection, you are currently
    // unable to join the queue." (or "{0} abandoned", for a party member). The closing warning logout_confirm_leaver names only "a
    // time penalty", for every game but PRACTICE_GAME and TUTORIAL_GAME (ClientModuleMain.onClosing), so Co-op and ranked dodges
    // count; nothing in the client names a rating loss for a dodge. The dialog formats the time as mm:ss of a Date
    // (QueueDodgeDialog.formatTime), so retail's waits were under an hour.
    // Reconstruction (owner, 1 November 2026: "add the dodge timer like retail"; no 2011 source gives the numbers): 6 minutes for
    // the first dodge in 24 hours, 30 for every later one, the first two tiers of Riot's later dodge timer (its third, 12 hours,
    // could not be shown by this client). No rating loss.
    public static readonly TimeSpan DodgePeriod = TimeSpan.FromHours(24);
    public static readonly TimeSpan FirstDodgeWait = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan RepeatDodgeWait = TimeSpan.FromMinutes(30);

    /// <summary>A champion-select dodge at <paramref name="at"/>; the account keeps only the dodges of the last <see cref="DodgePeriod"/>.</summary>
    public void RecordDodge(long accountId, DateTimeOffset at)
    {
        lock (_gate)
        {
            RequireAccount(accountId);
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id != accountId ? a
                : a with { Dodges = [.. (a.Dodges ?? []).Where(dodge => at - dodge < DodgePeriod), at] }).ToArray() });
        }
    }

    /// <param name="now">The caller's clock (the queue's); the store's own when omitted.</param>
    public PlatformDodgeStatus DodgeStatus(long accountId, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var at = now ?? _time.GetUtcNow();
            var dodges = (RequireAccount(accountId).Dodges ?? []).Where(dodge => dodge <= at).ToArray();
            if (dodges.Length == 0) return new(0, TimeSpan.Zero);
            // The tier is the newest dodge's, so the wait never shrinks while it runs.
            var newest = dodges.Max();
            var count = dodges.Count(dodge => newest - dodge < DodgePeriod);
            var end = newest + (count == 1 ? FirstDodgeWait : RepeatDodgeWait);
            return new(at - newest < DodgePeriod ? count : 0, end > at ? end - at : TimeSpan.Zero);
        }
    }

    /// <summary>
    /// NOT RETAIL: the host's override on <c>/manage</c> (retail's was Riot's player support). The account's leaves so far stop
    /// counting; the games stay in its history and records. Its dodges and any dodge timer are forgiven too.
    /// </summary>
    public void ClearLeaves(long accountId)
    {
        lock (_gate)
        {
            RequireAccount(accountId);
            var now = _time.GetUtcNow();
            Save(_state with { Accounts = _state.Accounts.Select(a => a.Id == accountId ? a with { LeavesClearedAt = now, Dodges = null } : a).ToArray() });
        }
    }
}
