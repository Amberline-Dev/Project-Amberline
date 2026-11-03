using System.Text.Json;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

public static class AirRewardCodeChecks
{
    [Check("server.air-reward-code-single-use-and-persistence")]
    public static void RewardCodesWinOnceReplayForTheWinnerAndPersist()
    {
        var root = TemporaryDirectory("league-air-reward-code");
        try
        {
            long winnerId;
            var requestId = Guid.NewGuid();
            PlatformIssuedRewardCode issued;
            using (var accounts = new PlatformAccounts(root))
            {
                var first = accounts.RegisterPublic("rewardfirst", "secret-password", "Reward First");
                var second = accounts.RegisterPublic("rewardsecond", "secret-password", "Reward Second");
                issued = accounts.CreateRewardCode(125, 75, requestId);
                var saved = File.ReadAllText(Path.Combine(root, "accounts.json"));
                TestAssert.False(saved.Contains(issued.Code, StringComparison.Ordinal), "the plaintext code must never be persisted");
                TestAssert.Equal(39, issued.Code.Length);

                var attempts = new[] { first.Id, second.Id }.Select(id => Task.Run(() =>
                {
                    try { return (Id: id, Redemption: accounts.RedeemRewardCode(id, issued.Code.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal)), Error: (Exception?)null); }
                    catch (Exception error) { return (Id: id, Redemption: (PlatformRewardCodeRedemption?)null, Error: error); }
                })).ToArray();
                var outcomes = Task.WhenAll(attempts).GetAwaiter().GetResult();
                TestAssert.Equal(1, outcomes.Count(outcome => outcome.Redemption is not null), "only one account may consume a code");
                TestAssert.Equal(1, outcomes.Count(outcome => outcome.Error is ArgumentException), "the losing account must receive a generic replay failure");
                winnerId = outcomes.Single(outcome => outcome.Redemption is not null).Id;
                var replay = accounts.RedeemRewardCode(winnerId, issued.Code);
                TestAssert.True(replay.Replayed);
                TestAssert.Equal(125L, PlatformEconomy.For(replay.Account).Ip);
                TestAssert.Equal(75L, PlatformEconomy.For(replay.Account).Rp);
                TestAssert.Throws<ArgumentException>(() =>
                    accounts.GrantCurrency(winnerId, 125, 75, requestId),
                    "manual grants must not replay a reward-code grant with the same request ID");

                var persistedJson = File.ReadAllText(Path.Combine(root, "accounts.json"));
                var state = JsonSerializer.Deserialize<PlatformAccounts.State>(persistedJson, MatchRoster.JsonOptions);
                TestAssert.True(state is not null);
                TestAssert.Equal(125L, PlatformEconomy.For(state!.Accounts.Single(account => account.Id == winnerId)).Ip);
                var persisted = accounts.ListRewardCodes().Single(code => code.Id == requestId);
                TestAssert.True(persisted.RedeemedBy == winnerId);
                TestAssert.True(persisted.RedeemedAt is not null);
                var winner = accounts.Get(winnerId);
                TestAssert.Equal(requestId, PlatformEconomy.For(winner).Grants!.Single().RequestId);
            }

            using (var reopened = new PlatformAccounts(root))
            {
                var replay = reopened.RedeemRewardCode(winnerId, issued.Code.Replace("-", "", StringComparison.Ordinal));
                TestAssert.True(replay.Replayed, "a reopened store must preserve code replay state");
                TestAssert.Equal(125L, PlatformEconomy.For(replay.Account).Ip);
                TestAssert.Equal(75L, PlatformEconomy.For(replay.Account).Rp);
            }

            var path = Path.Combine(root, "accounts.json");
            var stored = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
            File.WriteAllText(path, JsonSerializer.Serialize(stored with
            {
                Accounts = stored.Accounts.Select(account => account.Id == winnerId
                    ? account with { Economy = account.Economy! with { Grants = [] } }
                    : account).ToArray(),
            }, MatchRoster.JsonOptions));
            TestAssert.Throws<InvalidDataException>(() => new PlatformAccounts(root),
                "a redeemed marker without its currency grant must fail startup validation");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-reward-code-validation-and-revocation")]
    public static void RewardCodesRejectEmptyAndOverLimitGrantsWithoutConsuming()
    {
        var root = TemporaryDirectory("league-air-reward-code-validation");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("rewardvalidation", "secret-password", "Reward Validation", maxed: false);
            TestAssert.Throws<ArgumentException>(() => accounts.CreateRewardCode(0, 0, Guid.NewGuid()));
            TestAssert.Throws<ArgumentOutOfRangeException>(() => accounts.CreateRewardCode(1_000_001, 0, Guid.NewGuid()));
            var tooMuch = accounts.CreateRewardCode(1, 0, Guid.NewGuid());
            TestAssert.Throws<ArgumentException>(() => accounts.GrantCurrency(account.Id, 1, 0, tooMuch.Id),
                "a code ID cannot be reused as a manual grant ID before redemption");
            TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(account.Id)).Ip);
            var tooMuchRp = accounts.CreateRewardCode(0, 1, Guid.NewGuid());
            accounts.GrantCurrency(account.Id, PlatformEconomy.MaxBalance, PlatformEconomy.MaxBalance, Guid.NewGuid());
            var snapshot = File.ReadAllText(Path.Combine(root, "accounts.json"));
            TestAssert.Throws<InvalidOperationException>(() => accounts.RedeemRewardCode(account.Id, tooMuch.Code));
            TestAssert.Throws<InvalidOperationException>(() => accounts.RedeemRewardCode(account.Id, tooMuchRp.Code));
            TestAssert.Equal(snapshot, File.ReadAllText(Path.Combine(root, "accounts.json")),
                "failed redemptions must not change balances or redemption markers on disk");
            TestAssert.True(accounts.ListRewardCodes().Single(code => code.Id == tooMuch.Id).RedeemedAt is null);
            TestAssert.True(accounts.ListRewardCodes().Single(code => code.Id == tooMuchRp.Id).RedeemedAt is null);

            var revoked = accounts.CreateRewardCode(10, 0, Guid.NewGuid());
            accounts.RevokeRewardCode(revoked.Id);
            TestAssert.Throws<ArgumentException>(() => accounts.RedeemRewardCode(account.Id, revoked.Code));
            TestAssert.True(accounts.ListRewardCodes().Single(code => code.Id == revoked.Id).Revoked);
            TestAssert.Throws<ArgumentException>(() => accounts.CreateRewardCode(10, 0, tooMuch.Id));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-reward-code-legacy-account-grant")]
    public static void RewardCodeGrantPreservesLegacyMaxedAccountPolicy()
    {
        var root = TemporaryDirectory("league-air-reward-code-legacy");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("rewardlegacy", "secret-password", "Reward Legacy");
            var code = accounts.CreateRewardCode(25, 50, Guid.NewGuid());
            var result = accounts.RedeemRewardCode(account.Id, code.Code);
            var economy = PlatformEconomy.For(result.Account);
            TestAssert.True(economy.AllUnlocked);
            TestAssert.Equal(25L, economy.Ip);
            TestAssert.Equal(50L, economy.Rp);
            TestAssert.Equal(code.Id, economy.Grants!.Single().RequestId);
        }
        finally { DeleteDirectory(root); }
    }

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
