using System.Net.Sockets;
using System.Text;
using Amberline.Platform;

namespace Amberline.Tests.Air;

public static partial class AirSocialChecks
{
    [Check("server.air-xmpp-friend-request-requires-acceptance")]
    public static void PendingRequestsDoNotBecomeRosterFriends() => PendingRequestsAsync().GetAwaiter().GetResult();

    private static async Task PendingRequestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-pending-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var alice = accounts.RegisterPublic("pendingalice", "testing-password", "Alice");
        var bob = accounts.RegisterPublic("pendingbob", "testing-password", "Bob");
        Authenticate(accounts, sessions, alice, "alice");
        Authenticate(accounts, sessions, bob, "bob");
        var social = Path.Combine(directory, "social");
        await using (var host = new AirXmppHost(accounts, sessions, social, 0))
        {
            await host.StartAsync();
            await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
            await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
            // Stock addContact writes a roster item before requesting a subscription.
            await a.SendAsync($"<iq type='set' id='add'><query xmlns='jabber:iq:roster'><item jid='{Jid(bob)}' name='Bob'/></query></iq>");
            await a.ReadUntilAsync("id='add'");
            await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribe'/>");
            await b.ReadUntilAsync("type='subscribe'");
            TestAssert.False((await a.RosterAsync("outgoing")).Contains("<item", StringComparison.Ordinal),
                "pending outgoing subscriptions must not trigger stock roster auto-grants");
            TestAssert.False((await b.RosterAsync("incoming")).Contains("<item", StringComparison.Ordinal),
                "the requester must not already be a buddy when AIR opens the acceptance prompt");
            await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribed'/>");
            TestAssert.False((await a.RosterAsync("self-approval")).Contains("<item", StringComparison.Ordinal),
                "a sender cannot approve their own request");
            await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribe'/>");
            await b.RosterAsync("reciprocal-pending");
            await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribed'/>");
            TestAssert.False((await a.RosterAsync("still-pending")).Contains("<item", StringComparison.Ordinal),
                "a reciprocal subscribe must not reverse the original recipient's approval responsibility");
            await b.SendAsync($"<presence to='{Jid(alice)}' type='unsubscribed'/>");
            TestAssert.False((await b.RosterAsync("declined")).Contains("<item", StringComparison.Ordinal));
            TestAssert.False((await a.RosterAsync("declined-sender")).Contains("<item", StringComparison.Ordinal));
            await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribe'/>");
            await a.RosterAsync("pending-before-restart");
        }
        await using (var host = new AirXmppHost(accounts, sessions, social, 0))
        {
            await host.StartAsync();
            await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
            await b.ReadUntilAsync("type='subscribe'");
            TestAssert.False((await b.RosterAsync("pending-after-restart")).Contains("<item", StringComparison.Ordinal),
                "reconnecting replays the request without accepting it");
            await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribed'/>");
            TestAssert.True((await b.RosterAsync("accepted")).Contains("subscription='both'", StringComparison.Ordinal));
            await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
            TestAssert.True((await a.RosterAsync("accepted-sender")).Contains("subscription='both'", StringComparison.Ordinal),
                "explicit recipient acceptance persists as a mutual friendship");
        }
    }

    // Batch 436 (friend accept): a friend's RTMP connection drops around the accept and the stock client reconnects at once.
    // While that session was parked, the next stanza on the friend's XMPP stream aborted it (SessionIsValid), the chat
    // reconnect failed with kind=authentication, and the stock client showed "disconnected from PvP.net".
    [Check("server.air-friend-chat-survives-brief-rtmp-drop")]
    public static void FriendChatSurvivesABriefRtmpDrop() => FriendChatAsync().GetAwaiter().GetResult();

    private static async Task FriendChatAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-parked-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var alice = accounts.RegisterPublic("parkedchatalice", "testing-password", "Alice");
        var bob = accounts.RegisterPublic("parkedchatbob", "testing-password", "Bob");
        Authenticate(accounts, sessions, alice, "alice");
        Authenticate(accounts, sessions, bob, "bob");
        var bobToken = sessions.Require("bob").Token;
        await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0);
        await host.StartAsync();
        await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
        await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
        await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribe'/>");
        await b.ReadUntilAsync("type='subscribe'");
        await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribed'/>");

        // The RTMP connection drops; the session is parked until the stock client's reconnect resumes it.
        sessions.Suspend("bob");
        TestAssert.True((await b.RosterAsync("parked-roster")).Contains("subscription='both'", StringComparison.Ordinal),
            "a parked session keeps the friend's chat stream authenticated");
        TestAssert.True(sessions.Resume("bob-resumed", bob.Username, bobToken),
            "the parked session resumes on the stock client's reconnect");
    }

    // Batch 436 (security review of the friend-chat fix): a parked session's chat token must not open a NEW stream. Only a
    // stream that was already authenticated before the park keeps working through a brief RTMP drop.
    [Check("server.air-xmpp-parked-chat-token-refused-for-new-stream")]
    public static void ParkedChatTokenRefusedForNewStream() => ParkedChatTokenAsync().GetAwaiter().GetResult();

    private static async Task ParkedChatTokenAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-parked-token-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var bob = accounts.RegisterPublic("parkedtokenbob", "testing-password", "Bob");
        Authenticate(accounts, sessions, bob, "bob");
        var parkedChat = sessions.Require("bob").ChatToken;
        await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0);
        await host.StartAsync();
        await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
        sessions.Suspend("bob");

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Jid(bob)}\0sum{bob.SummonerId}\0AIR_{parkedChat}"));
        await using var fresh = await SocialClient.OpenAsync(host.Port);
        await fresh.SendAsync($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{auth}</auth>");
        await fresh.ReadUntilAsync("not-authorized");

        TestAssert.True((await b.RosterAsync("parked-stream")).Contains("jabber:iq:roster", StringComparison.Ordinal),
            "the already-authenticated stream still answers through the park");
    }

    [Check("server.air-xmpp-social-contract")]
    public static void SaslRosterPresenceChatPrivacyAndMuc() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var alice = accounts.RegisterPublic("socialalice", "testing-password", "Alice");
        var bob = accounts.RegisterPublic("socialbob", "testing-password", "Bob");
        Authenticate(accounts, sessions, alice, "alice");
        Authenticate(accounts, sessions, bob, "bob");

        await using (var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0))
        {
            await host.StartAsync();
            await using (var unauthenticated = await SocialClient.OpenAsync(host.Port))
            {
                await unauthenticated.SendAsync($"<iq type='set' id='unauthenticated'><bind xmlns='{AirXmppHost.BindNamespace}'/></iq>");
                await unauthenticated.ReadUntilAsync("not-authorized");
            }
            await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
            await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");

            var bobJid = Jid(bob);
            await a.SendAsync($"<presence to='{bobJid}' type='subscribe'/>", split: true);
            var request = await b.ReadUntilAsync("type='subscribe'");
            TestAssert.True(request.Contains(Jid(alice), StringComparison.Ordinal), "friend requests use canonical sender JIDs");
            TestAssert.True(request.Contains("to='" + Jid(bob) + "/xiff'", StringComparison.Ordinal), "server presence requests target the bound recipient");

            await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribed'/>");
            var reciprocal = await b.ReadUntilAsync("subscription='both'");
            TestAssert.True(reciprocal.Contains(Jid(alice), StringComparison.Ordinal), "accepted friendship is reciprocal in the roster");
            await a.SendAsync("<presence><show>chat</show><status>I'm here</status><priority>5</priority></presence>", split: true);
            var presence = await b.ReadUntilAsync("here");
            TestAssert.True(presence.Contains(Jid(alice) + "/xiff", StringComparison.Ordinal), "presence sender cannot be spoofed");
            TestAssert.True(presence.Contains("to='" + Jid(bob) + "/xiff'", StringComparison.Ordinal), "broadcast presence addresses each recipient");

            // The stock roster requests the reciprocal subscription after approval.
            await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribe'/>");
            await b.SendAsync("<iq type='get' id='approved'><query xmlns='jabber:iq:roster'/></iq>");
            await b.ReadUntilAsync("id='approved'");
            TestAssert.True((await b.ReadUntilAsync("</iq>")).Contains("subscription='both'", StringComparison.Ordinal),
                "reciprocal request cannot downgrade an accepted friendship");

            await a.SendAsync($"<message from='{Jid(bob)}' to='{Jid(bob)}' type='chat'><body>héllo</body></message>", split: true);
            var relayed = await b.ReadUntilAsync("héllo");
            TestAssert.True(relayed.Contains(Jid(alice) + "/xiff", StringComparison.Ordinal), "sender identity is canonicalized from the authenticated socket");
            await a.SendAsync($"<message from='{Jid(bob)}' to='{Jid(bob)}' type='chat'><body>I'm not a spoof</body></message>");
            TestAssert.True(await b.TryReadAsync("not a spoof"), "mutual friends can exchange direct chat");

            await a.SendAsync($"<message to='{Jid(bob)}' type='chat'><subject>PRACTICE_GAME_INVITE</subject><body>&lt;body&gt;&lt;gameId&gt;7&lt;/gameId&gt;&lt;/body&gt;</body></message>");
            var invite = await b.ReadUntilAsync("PRACTICE_GAME_INVITE");
            TestAssert.True(invite.Contains("gameId", StringComparison.Ordinal), "stock invite subject and XML body relay unchanged");

            var room = "air-social-check@" + AirXmppHost.MucDomain;
            await a.SendAsync($"<presence to='{room}/Alice'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            var selfJoin = await a.ReadUntilAsync("code='110'");
            TestAssert.True(selfJoin.Contains("jid='" + Jid(alice) + "/xiff'", StringComparison.Ordinal),
                "room self-presence provides the real occupant JID needed by stock join-message rendering");
            await b.SendAsync($"<presence to='{room}/Bob'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            var occupants = await b.ReadUntilAsync("code='110'");
            TestAssert.True(occupants.Contains("jid='" + Jid(alice) + "/xiff'", StringComparison.Ordinal) &&
                occupants.Contains("jid='" + Jid(bob) + "/xiff'", StringComparison.Ordinal),
                "every room occupant is associated with the authenticated account JID");
            await a.SendAsync($"<iq type='get' id='room-info' to='{room}'><query xmlns='{AirXmppHost.DiscoInfoNamespace}'/></iq>");
            var roomInfo = await a.ReadUntilAsync("id='room-info'") + await a.ReadUntilAsync("</iq>");
            TestAssert.True(roomInfo.Contains("from='" + room + "'", StringComparison.Ordinal), "MUC disco replies identify the queried room");
            TestAssert.True(roomInfo.Contains("to='" + Jid(alice) + "/xiff'", StringComparison.Ordinal), "MUC disco replies target the requesting resource");
            TestAssert.True(roomInfo.Contains("muc#roominfo_occupants", StringComparison.Ordinal) && roomInfo.Contains("><value>2</value>", StringComparison.Ordinal), "MUC disco reports the bounded occupant count");
            await a.SendAsync($"<message to='{room}' type='groupchat'><body>room hello</body></message>", split: true);
            TestAssert.True((await b.ReadUntilAsync("room hello")).Contains("groupchat", StringComparison.Ordinal), "MUC relays bounded group messages");

            await a.SendAsync($"<iq type='set' id='block'><query xmlns='{AirXmppHost.PrivacyNamespace}'><list name='LOL'><item type='jid' value='{bobJid}' action='deny' order='1'/></list></query></iq>");
            await a.ReadUntilAsync("id='block'");
            await a.SendAsync($"<message to='{bobJid}' type='chat'><body>blocked</body></message>");
            var removal = await b.ReadUntilAsync("subscription='remove'");
            TestAssert.True(removal.Contains("air-roster-remove", StringComparison.Ordinal), "roster removal is pushed explicitly");
            TestAssert.True(!await b.TryReadAsync("blocked"), "blocked contacts receive no direct chat");

            sessions.Logout("alice");
            await a.SendAsync("<iq type='get' id='after-logout'><query xmlns='jabber:iq:roster'/></iq>");
            TestAssert.True(!await a.TryReadAsync("id='after-logout'"), "logged-out sockets cannot use the roster");
        }

        using var persisted = new PlatformSocialStore(Path.Combine(directory, "social"));
        TestAssert.True(!persisted.TryGetContact(alice.Id, bob.Id, out _),
            "privacy blocking removes the roster relationship");
        TestAssert.True(persisted.IsBlocked(alice.Id, bob.Id), "privacy deny state survives host restart");
    }

    private static void Authenticate(PlatformAccounts accounts, PlatformSessions sessions, PlatformAccount account, string connection)
    {
        var session = sessions.Login(connection, account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel(connection, account.Username, session.Token));
    }

    private static string Jid(PlatformAccount account) => $"sum{account.SummonerId}@{AirXmppHost.ChatDomain}";

    private sealed class SocialClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StringBuilder _buffer = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

        private SocialClient(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<SocialClient> ConnectAsync(int port, PlatformAccount account, PlatformSessions sessions, string connection)
        {
            var result = await OpenAsync(port);
            var session = sessions.Require(connection);
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Jid(account)}\0sum{account.SummonerId}\0AIR_{session.ChatToken}"));
            await result.SendAsync($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{auth}</auth>");
            await result.ReadUntilAsync("<success");
            await result.SendAsync("<stream:stream to='pvp.net' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>");
            await result.ReadUntilAsync("urn:ietf:params:xml:ns:xmpp-bind");
            await result.SendAsync("<iq type='set' id='bind'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><resource>xiff</resource></bind></iq>");
            await result.ReadUntilAsync("id='bind'");
            await result.SendAsync("<iq type='set' id='session'><session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></iq>");
            await result.ReadUntilAsync("id='session'");
            return result;
        }

        public static async Task<SocialClient> OpenAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            var result = new SocialClient(client);
            await result.SendAsync("<stream:stream to='pvp.net' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>");
            await result.ReadUntilAsync("mechanism>PLAIN");
            return result;
        }

        public async Task SendAsync(string xml, bool split = false)
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            if (!split) { await _stream.WriteAsync(bytes); return; }
            for (var i = 0; i < bytes.Length; i += 3)
            {
                var length = Math.Min(3, bytes.Length - i);
                await _stream.WriteAsync(bytes.AsMemory(i, length));
                await Task.Yield();
            }
        }

        public async Task<string> RosterAsync(string id)
        {
            await SendAsync($"<iq type='get' id='{id}'><query xmlns='jabber:iq:roster'/></iq>");
            await ReadUntilAsync($"id='{id}'");
            return await ReadUntilAsync("</iq>");
        }

        public async Task<string> ReadUntilAsync(string marker)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var current = _buffer.ToString();
                var index = current.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    var result = current[..(index + marker.Length)];
                    _buffer.Remove(0, index + marker.Length);
                    return result;
                }
                var bytes = new byte[4096];
                var count = await _stream.ReadAsync(bytes, timeout.Token);
                if (count == 0) throw new EndOfStreamException();
                Append(bytes, count);
            }
        }

        public async Task<bool> TryReadAsync(string marker)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { await ReadUntilWithTokenAsync(marker, timeout.Token); return true; }
            catch (OperationCanceledException) { return false; }
            catch (EndOfStreamException) { return false; }
        }

        private async Task ReadUntilWithTokenAsync(string marker, CancellationToken token)
        {
            while (true)
            {
                var current = _buffer.ToString();
                var index = current.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) { _buffer.Remove(0, index + marker.Length); return; }
                var bytes = new byte[4096];
                var count = await _stream.ReadAsync(bytes, token);
                if (count == 0) throw new EndOfStreamException();
                Append(bytes, count);
            }
        }

        private void Append(byte[] bytes, int count)
        {
            var chars = new char[count + 1];
            var length = _decoder.GetChars(bytes, 0, count, chars, 0);
            _buffer.Append(chars, 0, length);
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
