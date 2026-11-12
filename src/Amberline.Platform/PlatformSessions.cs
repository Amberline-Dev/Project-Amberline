using System.Net;
using System.Security.Cryptography;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: retail admitted a player through the login queue (<c>RestAuthServiceImpl</c>: a form POST that
/// returns a token) and kept a Spring-security session on the LCDS channel. Here that token is a single-use
/// two-minute ticket, the session is rolling 30 minutes (renewed by the heartbeat), the XMPP password is a
/// separate token bound to the session, and the store frame gets its own short-lived tickets. The values and
/// lifetimes are this host's choice; the client only needs the Session DTO and the channel login to succeed.
/// Session resume. The stock client reconnects its RTMP channel on its next send after a drop and presents the
/// channel credentials it logged in with (base64 of 'username:session token') in the <c>connect</c> command, without a
/// separate login command, then treats itself as authenticated (RTMPChannel.internalConnect, setUpMainNC). Owner
/// decision 18 October 2026: that reconnect carries the platform session over. Security (the platform is reachable from
/// the internet): a session is parked only when its channel-authenticated connection closes, is resumed only by a
/// fresh connection that presents the session's own token (constant-time compare, the same proof the channel login
/// takes), only within the resume window (60 minutes after the close, see <see cref="ResumeWindow"/>), and only once; the
/// parked binding is then consumed, so the old connection can never use it again. A newer login, a logout and an expired
/// session all discard it. Batch 436: a stock client that drops during a game sends nothing until the game ends, so the
/// former 120 s window refused its reconnect and the client logged out.
/// </summary>
public sealed class PlatformSessions(PlatformAccounts accounts, TimeProvider? clock = null, HostActivity? activity = null)
{
    // LoggedIn, Peer (the RTMP connection's address), Opened and Seen (the store frame's first and last page) only feed the Online table.
    public sealed record Session(long AccountId, string Username, string Token, DateTimeOffset Expires, bool ChannelAuthenticated, string ChatToken,
        DateTimeOffset LoggedIn = default, IPAddress? Peer = null);
    public sealed record StoreSession(string Token, long AccountId, string FormToken, DateTimeOffset Expires,
        DateTimeOffset Opened = default, DateTimeOffset Seen = default);
    /// <summary>One row of <see cref="Online"/>: a live channel-authenticated session, or one parked for a reconnect.</summary>
    public sealed record OnlineSession(long AccountId, IPAddress? Peer, DateTimeOffset Since, bool Reconnecting,
        (DateTimeOffset Opened, DateTimeOffset Seen)? Store);
    private sealed record Ticket(long AccountId, DateTimeOffset Expires);
    private sealed record Parked(Session Session, DateTimeOffset ClosedAt);
    private sealed record StoreGrant(string Connection, string PlatformToken, DateTimeOffset Expires);
    private sealed record StoreAccess(StoreSession Session, string Connection, string PlatformToken);
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Ticket> _tickets = [];
    /// <summary>
    /// NOT RETAIL: how long after its connection closed a parked login session can still be resumed; then it is discarded.
    /// Retail kept the Spring session for its whole idle timeout. The host bounds it to an hour, enough to cover a game: the
    /// stock client sends nothing while its game runs and reconnects at game end, and the former 120 s window refused that
    /// reconnect. The value is this host's choice (retail's timeout is not recovered from the shipped files); the single-use
    /// parked binding bounds the replay risk.
    /// </summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(60);
    private const int MaxParked = 256;
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, Parked> _parked = [];
    private readonly Dictionary<string, IPAddress> _peers = [];
    // NOT RETAIL: whether each connection reached this PC through the friends' tunnel (true) or directly (false), set at its first
    // use: the dashboard's mark for a friend's seat (ThroughTunnel), and the old host-only cheat rule (IsLocalAccount).
    private readonly Dictionary<string, bool> _throughTunnel = [];
    private readonly Dictionary<string, StoreGrant> _storeTickets = [];
    private readonly Dictionary<string, StoreAccess> _storeSessions = [];

    public string IssueTicket(PlatformAccount account)
    {
        lock (_gate)
        {
            Expire();
            foreach (var key in _tickets.Where(p => p.Value.AccountId == account.Id).Select(p => p.Key).ToArray()) _tickets.Remove(key);
            if (_tickets.Count >= 256) throw new ArgumentException("Login capacity reached. Try again shortly.");
            var token = Token();
            _tickets.Add(token, new(account.Id, _clock.GetUtcNow().AddMinutes(2)));
            return token;
        }
    }

    public Session Login(string connection, string username, string ticket, string version)
    {
        Session session;
        lock (_gate)
        {
            Expire();
            if (version != "1.45.11_10_04_15_28") throw AirFault.VersionMismatch(version);
            if (!_tickets.TryGetValue(ticket, out var entry)) throw AirFault.LoginFailed();
            var account = accounts.Get(entry.AccountId);
            if (!account.Username.Equals(username, StringComparison.OrdinalIgnoreCase)) throw AirFault.LoginFailed();
            if (_sessions.TryGetValue(connection, out var current) && current.AccountId != account.Id)
                throw new ArgumentException("Log out before changing account.");
            _tickets.Remove(ticket);
            // A newer login of the account ends every older binding, a parked one included (the second login wins, as it
            // does for chat, where the older stream gets <conflict/>). Each binding that ends is named as a logout.
            foreach (var (key, superseded) in _sessions.Where(p => p.Value.AccountId == account.Id).ToArray())
            {
                _sessions.Remove(key);
                LoggedOut(superseded);
            }
            foreach (var (key, superseded) in _parked.Where(p => p.Value.Session.AccountId == account.Id).ToArray())
            {
                _parked.Remove(key);
                LoggedOut(superseded.Session, superseded.ClosedAt);
            }
            session = new Session(account.Id, account.Username, Token(), _clock.GetUtcNow().AddMinutes(30), false, Token(),
                _clock.GetUtcNow(), _peers.GetValueOrDefault(connection));
            _sessions[connection] = session;
            // NOT RETAIL: the host page's activity feed names a login.
            activity?.Add($"{account.Username} logged in");
        }
        // NOT RETAIL: the Users page's Last seen keeps the login's time (batch 748). Touch never waits for the disk (its file is written in the
        // background, at most once an hour) and never fails the login.
        accounts.Touch(session.AccountId, session.LoggedIn);
        return session;
    }

    public bool AuthenticateChannel(string connection, string username, string token)
    {
        lock (_gate)
        {
            Expire();
            if (!_sessions.TryGetValue(connection, out var session) ||
                (session.Username != username && AirGameDtos.InternalName(accounts.Get(session.AccountId)) != username) || !SecretEquals(session.Token, token)) return false;
            _sessions[connection] = session with { ChannelAuthenticated = true, Expires = _clock.GetUtcNow().AddMinutes(30) };
            return true;
        }
    }

    /// <summary>
    /// The connection of a channel-authenticated session closed without a logout. The session leaves its connection (so
    /// nothing can use it there any more) and waits, for the resume window, for a reconnect that proves it holds
    /// the session token.
    /// </summary>
    public void Suspend(string connection)
    {
        lock (_gate)
        {
            Expire();
            _peers.Remove(connection);
            _throughTunnel.Remove(connection);
            if (!_sessions.Remove(connection, out var session)) return;
            // A login whose channel never authenticated is not parked: it ends here.
            if (!session.ChannelAuthenticated)
            {
                LoggedOut(session);
                return;
            }
            if (_parked.Count >= MaxParked)
            {
                // The oldest parked login is evicted to make room, so it ends here.
                var evicted = _parked.MinBy(p => p.Value.ClosedAt);
                _parked.Remove(evicted.Key);
                LoggedOut(evicted.Value.Session, evicted.Value.ClosedAt);
            }
            _parked[connection] = new(session, _clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Binds a parked session to a fresh connection when it presents that session's own channel credentials. Every
    /// condition is required: the connection has no session yet, the close is within the resume window, the
    /// account still exists and has no newer session, and the token matches (constant-time). A successful resume consumes
    /// the parked binding.
    /// </summary>
    public bool Resume(string connection, string username, string token)
    {
        lock (_gate)
        {
            Expire();
            if (_sessions.ContainsKey(connection)) return false;
            var now = _clock.GetUtcNow();
            foreach (var (previous, parked) in _parked.ToArray())
            {
                var session = parked.Session;
                var account = accounts.Find(session.AccountId);
                if (account is null || session.Username != username && AirGameDtos.InternalName(account) != username ||
                    !SecretEquals(session.Token, token)) continue;
                _parked.Remove(previous);
                if (now - parked.ClosedAt > ResumeWindow || _sessions.Values.Any(s => s.AccountId == session.AccountId))
                {
                    // The parked login is discarded here, so it ends here.
                    LoggedOut(session, parked.ClosedAt);
                    return false;
                }
                _sessions[connection] = session with { ChannelAuthenticated = true, Expires = now.AddMinutes(30),
                    Peer = _peers.GetValueOrDefault(connection) ?? session.Peer };
                return true;
            }
            return false;
        }
    }

    public Session Require(string connection)
    {
        lock (_gate)
        {
            Expire();
            if (!_sessions.TryGetValue(connection, out var session) || !session.ChannelAuthenticated)
                throw AirFault.InvalidSession();
            return session;
        }
    }

    public long? AccountForConnection(string connection)
    {
        lock (_gate)
        {
            Expire();
            return _sessions.TryGetValue(connection, out var session) && session.ChannelAuthenticated ? session.AccountId : null;
        }
    }

    public void Heartbeat(string connection, long accountId, string token)
    {
        lock (_gate)
        {
            var session = Require(connection);
            if (session.AccountId != accountId || !SecretEquals(session.Token, token)) throw new ArgumentException("Invalid heartbeat identity.");
            _sessions[connection] = session with { Expires = _clock.GetUtcNow().AddMinutes(30) };
        }
    }

    public void Logout(string connection)
    {
        lock (_gate)
        {
            // NOT RETAIL: the host page's activity feed names the logout an explicit logout ends (a parked login ends at its own expiry, see Expire).
            if (_sessions.Remove(connection, out var session)) LoggedOut(session);
        }
    }

    /// <summary>
    /// NOT RETAIL: host tool (the /manage password reset). Ends every login of the account: its unused login tickets and its live
    /// and parked sessions, so nothing signed in with the old password can resume. The store tickets and pages hanging on those
    /// sessions lapse in the same expiry pass, the chat stream at the XMPP host's next 30-second check, and the client's next
    /// call (at the latest its 60-second heartbeat) is answered InvalidSession, the client's "session expired".
    /// </summary>
    public void EndAccount(long accountId)
    {
        lock (_gate)
        {
            foreach (var key in _tickets.Where(p => p.Value.AccountId == accountId).Select(p => p.Key).ToArray()) _tickets.Remove(key);
            // The host's end of an open or parked login is a logout, named once per login (the host page's activity feed).
            foreach (var (key, ended) in _sessions.Where(p => p.Value.AccountId == accountId).ToArray())
            {
                _sessions.Remove(key);
                LoggedOut(ended);
            }
            foreach (var (key, parked) in _parked.Where(p => p.Value.Session.AccountId == accountId).ToArray())
            {
                _parked.Remove(key);
                LoggedOut(parked.Session, parked.ClosedAt);
            }
            Expire();
        }
    }

    /// <summary>
    /// NOT RETAIL: operator tooling for the host page (retail had none). The RTMP host reports the address a new connection came
    /// from (for a friend the tunnel's TLS peer, see AirTunnelPeers); a login or resume on that connection takes it over, and a
    /// closed connection forgets it (<see cref="Suspend"/>). Held in memory only, never logged, never sent to a client.
    /// </summary>
    public void ObservePeer(string connection, IPAddress peer) { lock (_gate) _peers[connection] = peer; }

    /// <summary>NOT RETAIL: records whether a connection reached the platform through the friends' tunnel, at its first use.</summary>
    public void ObserveOrigin(string connection, bool throughTunnel) { lock (_gate) _throughTunnel[connection] = throughTunnel; }

    /// <summary>
    /// NOT RETAIL: true when the account is logged in on a connection that came through the friends' tunnel (retail had no tunnel). The
    /// dashboard reads it for a friend's seat (A-NET-10, <see cref="HostSupport"/>): the login went through the sign-in port, the game
    /// connects to the game ports outside the tunnel. A closed connection forgets its mark (<see cref="Suspend"/>).
    /// </summary>
    public bool ThroughTunnel(long accountId)
    {
        lock (_gate)
            return _sessions.Any(pair => pair.Value.AccountId == accountId && _throughTunnel.GetValueOrDefault(pair.Key));
    }

    /// <summary>
    /// DIAGNOSTICS: true when the account is logged in on a connection that reached this PC directly. From 24 to 26 October 2026 only
    /// that seat could use the playtest cheat commands in an AIR match. Since 26 October every human seat may (owner), so no rule reads
    /// it; ponytail: kept with its origin tracking so the host-only rule is a one-line change in PlatformGames.
    /// </summary>
    public bool IsLocalAccount(long accountId)
    {
        lock (_gate)
            return _sessions.Any(pair => pair.Value.AccountId == accountId &&
                _throughTunnel.TryGetValue(pair.Key, out var throughTunnel) && !throughTunnel);
    }

    /// <summary>NOT RETAIL: operator tooling (the /manage "Online now" table). Reads state only; nothing here authenticates.</summary>
    public IReadOnlyList<OnlineSession> Online()
    {
        lock (_gate)
        {
            Expire();
            (DateTimeOffset, DateTimeOffset)? Store(long account) => _storeSessions.Values.Where(s => s.Session.AccountId == account)
                .Select(s => ((DateTimeOffset, DateTimeOffset)?)(s.Session.Opened, s.Session.Seen)).FirstOrDefault();
            return _sessions.Values.Where(s => s.ChannelAuthenticated)
                .Select(s => new OnlineSession(s.AccountId, s.Peer, s.LoggedIn, false, Store(s.AccountId)))
                .Concat(_parked.Values.Select(p => new OnlineSession(p.Session.AccountId, p.Session.Peer, p.ClosedAt, true, null))).ToArray();
        }
    }

    public string IssueStoreTicket(string connection)
    {
        lock (_gate)
        {
            var session = Require(connection);
            foreach (var key in _storeTickets.Where(p => p.Value.Connection == connection).Select(p => p.Key).ToArray())
                _storeTickets.Remove(key);
            if (_storeTickets.Count >= 256) throw new ArgumentException("Store capacity reached. Try again shortly.");
            var token = Token();
            _storeTickets[token] = new(connection, session.Token, _clock.GetUtcNow().AddMinutes(2));
            return token;
        }
    }

    public StoreSession RedeemStoreTicket(string ticket)
    {
        lock (_gate)
        {
            Expire();
            if (!_storeTickets.Remove(ticket, out var grant) || !ValidStoreParent(grant.Connection, grant.PlatformToken))
                throw new ArgumentException("Store link expired. Reopen the store from the client.");
            var parent = _sessions[grant.Connection];
            foreach (var key in _storeSessions.Where(p => p.Value.Session.AccountId == parent.AccountId).Select(p => p.Key).ToArray())
                _storeSessions.Remove(key);
            if (_storeSessions.Count >= 256) throw new ArgumentException("Store capacity reached. Try again shortly.");
            var access = new StoreSession(Token(), parent.AccountId, Token(), _clock.GetUtcNow().AddMinutes(30), _clock.GetUtcNow(), _clock.GetUtcNow());
            _storeSessions[access.Token] = new(access, grant.Connection, parent.Token);
            return access;
        }
    }

    public StoreSession RequireStoreSession(string token)
    {
        lock (_gate)
        {
            Expire();
            if (!_storeSessions.TryGetValue(token, out var access))
                throw new ArgumentException("Store session expired. Reopen the store from the client.");
            var seen = access.Session with { Seen = _clock.GetUtcNow() };
            _storeSessions[token] = access with { Session = seen };
            return seen;
        }
    }

    private bool ValidStoreParent(string connection, string platformToken) =>
        _sessions.TryGetValue(connection, out var parent) && parent.ChannelAuthenticated &&
        parent.Expires > _clock.GetUtcNow() && SecretEquals(parent.Token, platformToken);

    // New SASL logins: only a live, channel-authenticated session, exactly as on master. A parked session never opens a new stream.
    public long? AuthenticateChat(string username, string password)
    {
        lock (_gate)
        {
            Expire();
            return _sessions.Values.FirstOrDefault(s => s.ChannelAuthenticated && ChatMatches(s, username, password))?.AccountId;
        }
    }

    /// <summary>
    /// Whether an already-authenticated chat stream may stay open. A brief RTMP drop parks the login session and the stock
    /// client reconnects its RTMP at once, so the stream it already had stays up while the parked session is inside its idle
    /// timeout (Reconstruction: retail's chat while its session is parked is not recovered). New SASL logins use
    /// <see cref="AuthenticateChat"/>, which never accepts a parked session.
    /// </summary>
    public bool IsChatSessionAlive(string username, string password)
    {
        lock (_gate)
        {
            Expire();
            return _sessions.Values.Any(s => s.ChannelAuthenticated && ChatMatches(s, username, password)) ||
                _parked.Values.Any(p => ChatMatches(p.Session, username, password));
        }
    }

    // NOT RETAIL (batch 748; retail had no host-side delete): a session of a login the host has just deleted (its sessions end right after) matches nothing, so the other logins' chat
    // never fails on it.
    private bool ChatMatches(Session session, string username, string password) =>
        accounts.Find(session.AccountId) is { } account && AirGameDtos.InternalName(account) == username && SecretEquals("AIR_" + session.ChatToken, password);

    private void Expire()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _tickets.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _tickets.Remove(key);
        // An open login past its expiry, or a parked login past the resume window, ends here: each is named as a logout once.
        foreach (var (key, expired) in _sessions.Where(p => p.Value.Expires <= now).ToArray())
        {
            _sessions.Remove(key);
            // It was last there 30 minutes before its expiry: its login, its channel and each heartbeat move the expiry 30 minutes on.
            LoggedOut(expired, expired.Expires.AddMinutes(-30));
        }
        foreach (var (key, parked) in _parked.Where(p => now - p.Value.ClosedAt > ResumeWindow).ToArray())
        {
            _parked.Remove(key);
            LoggedOut(parked.Session, parked.ClosedAt);
        }
        foreach (var key in _storeTickets.Where(p => p.Value.Expires <= now ||
            !ValidStoreParent(p.Value.Connection, p.Value.PlatformToken)).Select(p => p.Key).ToArray()) _storeTickets.Remove(key);
        foreach (var key in _storeSessions.Where(p => p.Value.Session.Expires <= now ||
            !ValidStoreParent(p.Value.Connection, p.Value.PlatformToken)).Select(p => p.Key).ToArray()) _storeSessions.Remove(key);
    }
    // NOT RETAIL: the host page's activity feed ends each login it named "logged in" with exactly one "logged out", at the one
    // place the login ends (each removal above and in Expire calls this). The Users page's Last seen (batch 748, final fix 7) keeps the time
    // the login was last there: now, or for a parked login when its connection closed. Touch only stamps memory (its file is written in the
    // background), so it is safe under this lock (sessions, then accounts).
    private void LoggedOut(Session session, DateTimeOffset? lastThere = null)
    {
        activity?.Add($"{session.Username} logged out");
        accounts.Touch(session.AccountId, lastThere ?? _clock.GetUtcNow());
    }
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool SecretEquals(string expected, string actual) => actual.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
}
