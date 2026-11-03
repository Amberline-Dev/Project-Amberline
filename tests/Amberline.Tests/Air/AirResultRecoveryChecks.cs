using System.Text.Json;
using Amberline.Core;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

public static class AirResultRecoveryChecks
{
    [Check("server.air-result-recovery-after-restart")]
    public static void CompletedMatchRecoversHistoryAndRewardsExactlyOnce() => RecoveryAsync().GetAwaiter().GetResult();

    // Batch 443: a Dominion result (The Crystal Scar, map 8, mode ODIN) is recovered like a classic one.
    [Check("server.air-result-recovery-accepts-the-dominion-map")]
    public static void CompletedCrystalScarMatchRecoversAsAnOdinResult() =>
        RecoveryAsync(Amberline.Core.GameMaps.CrystalScar, Amberline.Core.GameMaps.DominionMode).GetAwaiter().GetResult();

    private static async Task RecoveryAsync(int mapId = 1, string mode = "CLASSIC")
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-result-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var games = Path.Combine(root, "games");
        var clock = new RecoveryClock(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        PlatformAccount alice;
        PlatformPendingMatch pending;
        var writtenAt = clock.Current.AddMinutes(10);
        using (var accounts = new PlatformAccounts(root, timeProvider: clock))
        {
            alice = accounts.RegisterPublic("recovery", "testing-password", "Recovery", maxed: false);
            accounts.GrantCurrency(alice.Id, 0, 1_000, Guid.NewGuid());
            accounts.Purchase(alice.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(alice.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
            pending = Pending(accounts, alice);
            accounts.PrepareMatch(pending);
            WriteResult(games, pending, Result(alice, mapId, mode), writtenAt);
            TestAssert.Equal(0, accounts.ResultsFor(alice.Id).Length, "simulate exit before the platform receipt");
        }
        // Only disk reading is exercised. The manager never starts this executable.
        await using var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), games);
        clock.Current = clock.Current.AddDays(2);
        using (var accounts = new PlatformAccounts(root, timeProvider: clock))
        {
            TestAssert.Equal(1, accounts.RecoverResults(manager, TextWriter.Null));
            var receipt = accounts.ResultsFor(alice.Id).Single();
            TestAssert.Equal(pending.GameId, (int)receipt.GameId);
            TestAssert.Equal(pending.HostGameId, receipt.HostGameId);
            TestAssert.Equal(writtenAt, receipt.CompletedAt);
            var reward = receipt.Rewards![alice.Id];
            TestAssert.True(reward.TimedIpEarned > 0, "boost was active at victory even though recovery occurs after expiry");
            TestAssert.True(reward.WinIpEarned > 0);
            TestAssert.Equal(9, PlatformEconomy.For(accounts.Get(alice.Id)).IpBoostPerWinCount);
            var balance = PlatformEconomy.For(accounts.Get(alice.Id)).Ip;
            TestAssert.Equal(0, accounts.RecoverResults(manager, TextWriter.Null));
            TestAssert.Equal(balance, PlatformEconomy.For(accounts.Get(alice.Id)).Ip);
            TestAssert.False(accounts.RecordResult(receipt), "duplicate delivery cannot consume a second boost win");
        }
        using var reopened = new PlatformAccounts(root, timeProvider: clock);
        TestAssert.Equal(0, reopened.RecoverResults(manager, TextWriter.Null));
        TestAssert.Equal(1, reopened.ResultsFor(alice.Id).Length);
        TestAssert.True(File.Exists(Path.Combine(root, "match-handoffs", pending.GameId + ".json")), "immutable handoff evidence is retained");
    }

    [Check("server.air-result-recovery-rejects-unrelated-or-ambiguous-output")]
    public static void RecoveryValidatesTheFrozenRosterAndContinuesPastBadFiles() => RejectionAsync().GetAwaiter().GetResult();

    private static async Task RejectionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-result-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var games = Path.Combine(root, "games");
        using var accounts = new PlatformAccounts(root);
        var alice = accounts.RegisterPublic("rejectresult", "testing-password", "Reject Result");
        var pending = Enumerable.Range(0, 6).Select(_ => Pending(accounts, alice)).ToArray();
        foreach (var match in pending) accounts.PrepareMatch(match);
        var wrongChampion = Result(alice) with { Players = [Player(alice) with { Champion = "Orianna" }] };
        WriteResult(games, pending[0], wrongChampion, DateTimeOffset.UtcNow);
        var unrelated = WriteResult(games, pending[1], Result(alice), DateTimeOffset.UtcNow);
        File.WriteAllText(unrelated, JsonSerializer.Serialize(new { session_id = Guid.NewGuid(), result = Result(alice) }));
        var ambiguous = WriteResult(games, pending[2], Result(alice), DateTimeOffset.UtcNow);
        var duplicate = Path.Combine(Path.GetDirectoryName(ambiguous)!, "duplicate");
        Directory.CreateDirectory(duplicate);
        File.Copy(ambiguous, Path.Combine(duplicate, "match-result.json"));
        // pending[3] represents a stopped/unfinished match without a final result.
        var badEnvelope = WriteResult(games, pending[4], Result(alice), DateTimeOffset.UtcNow);
        File.WriteAllText(badEnvelope, "[]");
        WriteResult(games, pending[5], Result(alice), DateTimeOffset.UtcNow);
        await using var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), games);
        using var log = new StringWriter();
        TestAssert.Equal(1, accounts.RecoverResults(manager, log));
        TestAssert.Equal(pending[5].HostGameId, accounts.ResultsFor(alice.Id).Single().HostGameId);
        TestAssert.True(log.ToString().Contains("frozen match seat", StringComparison.Ordinal));
        TestAssert.True(log.ToString().Contains("Multiple final results", StringComparison.Ordinal));
        TestAssert.True(log.ToString().Contains("envelope must be an object", StringComparison.Ordinal));
        TestAssert.True(File.Exists(ambiguous) && File.Exists(unrelated), "evidence survives rejection");
        TestAssert.Equal(0, accounts.RecoverResults(manager, TextWriter.Null));
        var forged = new PlatformResult(pending[3].GameId, pending[3].HostGameId, [alice.Id],
            JsonSerializer.SerializeToElement(Result(alice)), QueueId: 2);
        TestAssert.Throws<InvalidDataException>(() => accounts.RecordResult(forged), "queue identity cannot change during result import");
        TestAssert.Throws<ArgumentException>(() => accounts.PrepareMatch(pending[3]), "a match cannot receive a second handoff");
    }

    private static PlatformPendingMatch Pending(PlatformAccounts accounts, PlatformAccount account) => new(
        accounts.AllocateGameId(), Guid.NewGuid(), 0, [account.Id],
        [account.Loadout with { PlayerId = (int)account.SummonerId }]);

    private static string WriteResult(string games, PlatformPendingMatch pending, MatchResult result, DateTimeOffset writtenAt)
    {
        var folder = Path.Combine(games, pending.HostGameId.ToString("N"), "logs", "session");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "match-result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { session_id = pending.HostGameId.ToString(), result }));
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
        return path;
    }

    private static MatchResult Result(PlatformAccount account, int mapId = 1, string mode = "CLASSIC") =>
        new(8, "Version 1.0.0.126", mapId, mode, "Order", 600, [Player(account)], false);

    private static PlayerMatchResult Player(PlatformAccount account) => new(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner, Champion: account.Loadout.Champion,
        Team: account.Loadout.Team.ToString(), SkinId: account.Loadout.SkinId, Connected: true, Level: 1,
        Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0, OtherMinionsKilled: 0,
        NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0, Statistics: new HeroMatchStatistics().Snapshot(600),
        SummonerSpells: [account.Loadout.SummonerSpell1, account.Loadout.SummonerSpell2], SpellRanks: [0, 0, 0, 0], Inventory: []);

    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
