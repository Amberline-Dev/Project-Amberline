using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amberline.Host;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: per-account bearer tokens for the remote AIR tunnels (retail has no tunnel). A friend signs in once with the
/// login the host created (<see cref="AirRemoteTunnel.MapSignIn"/>) and the companion sends the 64-hex token as
/// <c>Authorization: Bearer</c> before any tunnel opens. Only SHA-256 digests are stored. A token expires after
/// <see cref="TokenLifetime"/>, a new sign-in replaces the account's token, and the host can revoke it. Failed attempts
/// are throttled per address.
/// </summary>
public sealed class AirRemoteAccess
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private const int MaxFailures = 10;
    private const int PruneAbove = 1024;

    public sealed record Entry(long AccountId, string TokenHash, DateTimeOffset Expires);

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<Entry> _entries;
    private readonly Dictionary<IPAddress, (int Count, DateTimeOffset Since)> _failures = new();

    public AirRemoteAccess(string directory, TimeProvider? clock = null)
    {
        _path = Path.Combine(directory, "remote-access.json");
        _clock = clock ?? TimeProvider.System;
        _entries = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), HostFiles.Json) ?? []
            : [];
    }

    /// <summary>Issues the account's token, replacing any earlier one. The raw token leaves the store only here.</summary>
    public (string Token, DateTimeOffset Expires) Issue(long accountId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var expires = now + TokenLifetime;
            _entries.RemoveAll(e => e.AccountId == accountId || e.Expires <= now);
            _entries.Add(new Entry(accountId, HostFiles.Hash(token), expires));
            Save();
            return (token, expires);
        }
    }

    /// <summary>True when the token is a live token of some account. Digests are compared in constant time.</summary>
    public bool Authorize(string token) => AccountFor(token) is not null;

    /// <summary>The account whose live token this is, or null. Digests are compared in constant time.</summary>
    public long? AccountFor(string token)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) return null;
        var digest = Encoding.ASCII.GetBytes(HostFiles.Hash(token));
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            long? account = null;
            foreach (var entry in _entries)
                if (CryptographicOperations.FixedTimeEquals(digest, Encoding.ASCII.GetBytes(entry.TokenHash)) && entry.Expires > now)
                    account = entry.AccountId;
            return account;
        }
    }

    /// <summary>Ends the account's token. Tunnels already open stay up until their companion disconnects.</summary>
    public void Revoke(long accountId)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.AccountId == accountId) > 0) Save();
        }
    }

    /// <summary>True while the address has used up its failed attempts in the current window.</summary>
    public bool Throttled(IPAddress? address) => RetryAfter(address) > TimeSpan.Zero;

    /// <summary>How long the address stays throttled; zero when it is not.</summary>
    public TimeSpan RetryAfter(IPAddress? address)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(KeyOf(address), out var failures) || failures.Count < MaxFailures) return TimeSpan.Zero;
            var left = FailureWindow - (_clock.GetUtcNow() - failures.Since);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Counts one failed sign-in or tunnel attempt from the address.</summary>
    public void Failed(IPAddress? address)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var key = KeyOf(address);
            var current = _failures.GetValueOrDefault(key);
            _failures[key] = now - current.Since >= FailureWindow ? (1, now) : (current.Count + 1, current.Since);
            if (_failures.Count <= PruneAbove) return;
            foreach (var stale in _failures.Where(pair => now - pair.Value.Since >= FailureWindow).Select(pair => pair.Key).ToArray())
                _failures.Remove(stale);
        }
    }

    private static IPAddress KeyOf(IPAddress? address) =>
        address is null ? IPAddress.None : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private void Save() => HostFiles.Write(_path, _entries);
}
