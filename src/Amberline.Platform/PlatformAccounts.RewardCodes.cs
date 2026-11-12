using System.Security.Cryptography;

namespace Amberline.Platform;

// NOT RETAIL: the retail store sold Riot Points for money and had promotional codes handled by Riot's own backend. These
// are single-use IP/RP codes the host issues from the management page; the stock client never sees them (the code is
// typed into the reconstructed store page).
public sealed record PlatformRewardCode(
    Guid Id,
    string CodeHash,
    long Ip,
    long Rp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RedeemedAt = null,
    long? RedeemedBy = null,
    bool Revoked = false);

public sealed record PlatformIssuedRewardCode(string Code, Guid Id);

public sealed record PlatformRewardCodeRedemption(
    PlatformAccount Account,
    PlatformRewardCode Reward,
    bool Replayed);

public sealed partial class PlatformAccounts
{
    private const long MaxRewardAmount = 1_000_000;
    /// <summary>NOT RETAIL (batch 751): the refusal of a code of RP alone while the host's "Offer skins and RP" switch is off.</summary>
    public const string RpOnlyCode = "This code gives only RP, and RP is turned off on this server.";

    public PlatformIssuedRewardCode CreateRewardCode(long ip, long rp, Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("A reward-code request ID is required.", nameof(requestId));
        ValidateRewardCurrency(ip, rp);
        lock (_gate)
        {
            var codes = _state.RewardCodes ?? [];
            if (codes.Any(code => code.Id == requestId) ||
                _state.Accounts.Any(account => account.Economy?.Grants?.Any(grant => grant.RequestId == requestId) == true))
                throw new ArgumentException("The request ID was already used.", nameof(requestId));

            string plain;
            string hash;
            do
            {
                var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                plain = string.Join('-', Enumerable.Range(0, 8).Select(index => raw.Substring(index * 4, 4)));
                hash = Hash(raw);
            }
            while (codes.Any(code => code.CodeHash.Equals(hash, StringComparison.OrdinalIgnoreCase)));

            var reward = new PlatformRewardCode(requestId, hash, ip, rp, _time.GetUtcNow());
            Save(_state with { RewardCodes = [.. codes, reward] });
            return new(plain, reward.Id);
        }
    }

    public PlatformRewardCode[] ListRewardCodes()
    {
        lock (_gate) return (_state.RewardCodes ?? []).Select(code => code with { }).ToArray();
    }

    public PlatformRewardCode RevokeRewardCode(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A reward-code ID is required.", nameof(id));
        lock (_gate)
        {
            var codes = _state.RewardCodes ?? [];
            var reward = codes.FirstOrDefault(code => code.Id == id)
                ?? throw new ArgumentException("Reward code does not exist.", nameof(id));
            if (reward.RedeemedBy is not null || reward.RedeemedAt is not null)
                throw new InvalidOperationException("A redeemed reward code cannot be revoked.");
            if (reward.Revoked) return reward;
            var revoked = reward with { Revoked = true };
            Save(_state with { RewardCodes = codes.Select(code => code.Id == id ? revoked : code).ToArray() });
            return revoked;
        }
    }

    public PlatformRewardCodeRedemption RedeemRewardCode(long accountId, string code)
    {
        var normalized = NormalizeRewardCode(code);
        var hash = Hash(normalized);
        lock (_gate)
        {
            var reward = (_state.RewardCodes ?? [])
                .FirstOrDefault(candidate => candidate.CodeHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (reward is null || reward.Revoked)
                throw new ArgumentException("Reward code is invalid or already redeemed.", nameof(code));

            var account = RequireAccount(accountId);
            if (reward.RedeemedBy is { } redeemedBy)
            {
                if (redeemedBy != accountId)
                    throw new ArgumentException("Reward code is invalid or already redeemed.", nameof(code));
                if (reward.RedeemedAt is null)
                    throw new InvalidDataException("Reward-code redemption marker is incomplete.");
                return new(CopyAccount(account), reward, true);
            }
            if (reward.RedeemedAt is not null)
                throw new InvalidDataException("Reward-code redemption marker is incomplete.");
            // NOT RETAIL (batch 751): while the host's "Offer skins and RP" switch is off a code grants its IP alone, and a code of RP alone is
            // refused and stays unused, so it can be redeemed once the switch is on again. The grant records what was granted. Retail had no
            // host codes at all.
            var rp = Controls.PaidContentOff ? 0 : reward.Rp;
            if (reward.Ip == 0 && rp == 0) throw new ArgumentException(RpOnlyCode, nameof(code));

            var data = account.Economy is null ? PlatformEconomy.LegacyData() : PlatformEconomy.Copy(account.Economy);
            if (data.Ip > PlatformEconomy.MaxBalance - reward.Ip || data.Rp > PlatformEconomy.MaxBalance - rp)
                throw new InvalidOperationException("The account balance limit would be exceeded.");
            if (data.Grants?.Any(grant => grant.RequestId == reward.Id) == true)
                throw new InvalidDataException("The reward-code grant request ID is already present on the account.");

            var now = _time.GetUtcNow();
            var grant = new PlatformCurrencyGrant(reward.Id, reward.Ip, rp, now);
            var updatedData = data with
            {
                Ip = checked(data.Ip + reward.Ip),
                Rp = checked(data.Rp + rp),
                Grants = [.. data.Grants ?? [], grant],
            };
            var updatedAccount = account with { Economy = updatedData };
            var redeemed = reward with { RedeemedAt = now, RedeemedBy = accountId };
            Save(_state with
            {
                Accounts = _state.Accounts.Select(candidate => candidate.Id == accountId ? updatedAccount : candidate).ToArray(),
                RewardCodes = (_state.RewardCodes ?? []).Select(candidate => candidate.Id == reward.Id ? redeemed : candidate).ToArray(),
            });
            return new(CopyAccount(updatedAccount), redeemed, false);
        }
    }

    private static void ValidateRewardCurrency(long ip, long rp)
    {
        if (ip < 0 || rp < 0 || ip > MaxRewardAmount || rp > MaxRewardAmount)
            throw new ArgumentOutOfRangeException(nameof(ip), "Reward currency must be between zero and 1,000,000.");
        if (ip == 0 && rp == 0)
            throw new ArgumentException("A reward code must grant IP or RP.");
    }

    private static string NormalizeRewardCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A reward code is required.", nameof(code));
        var raw = code.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (raw.Length != 32 || raw.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Reward code is invalid or already redeemed.", nameof(code));
        return raw.ToUpperInvariant();
    }

    private void ValidateRewardCodes(PlatformRewardCode[]? codes)
    {
        if (codes is null) return;
        var ids = new HashSet<Guid>();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (code is null || code.Id == Guid.Empty || !ids.Add(code.Id) || code.CreatedAt == default ||
                string.IsNullOrWhiteSpace(code.CodeHash) || !hashes.Add(code.CodeHash) || code.CodeHash.Length != 64 ||
                code.CodeHash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("Reward-code ledger is invalid.");
            ValidateRewardCurrency(code.Ip, code.Rp);
            if ((code.RedeemedAt is null) != (code.RedeemedBy is null) ||
                (code.Revoked && (code.RedeemedAt is not null || code.RedeemedBy is not null)))
                throw new InvalidDataException("Reward-code redemption marker is invalid.");
            var recipients = _state.Accounts.Where(account =>
                account.Economy?.Grants?.Any(grant => grant.RequestId == code.Id) == true).ToArray();
            if (code.RedeemedBy is { } accountId)
            {
                // NOT RETAIL (batch 748; retail had no host-side delete): a code redeemed by a login the host deleted stays as history; its grant left with the login.
                if (recipients.Length == 0 && _state.Deleted?.Any(deleted => deleted.Id == accountId) == true) continue;
                if (recipients.Length != 1 || recipients[0].Id != accountId)
                    throw new InvalidDataException("Reward-code recipient and grant do not match.");
                var grant = recipients[0].Economy!.Grants!.Single(candidate => candidate.RequestId == code.Id);
                // A grant of RP 0 is a code redeemed while RP was off (batch 751): it granted the IP alone.
                if (grant.Ip != code.Ip || grant.Rp != code.Rp && grant.Rp != 0 || grant.GrantedAt != code.RedeemedAt)
                    throw new InvalidDataException("Reward-code grant does not match its redemption.");
            }
            else if (recipients.Length != 0)
                throw new InvalidDataException("Unused reward code has a currency grant.");
        }
    }
}
