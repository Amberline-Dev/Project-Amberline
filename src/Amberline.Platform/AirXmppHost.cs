using System.Net;
using System.Net.Sockets;
using System.Security;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Amberline.Platform;

/// <summary>
/// A deliberately small XMPP 1.0 host for the patch-126 AIR client's social and
/// custom-game paths. It is local-only by default; the remote transport is owned
/// by the launcher layer.
/// NOT RETAIL: retail ran a full XMPP server for pvp.net with a MUC component (rosters, privacy lists, multi-user
/// chat, the custom-game, champion-select and post-game rooms). This host implements only the stanzas the stock client
/// sends and reads: SASL PLAIN against the platform's chat token, bind and session, roster, privacy list 'LOL',
/// presence, direct messages and invitations, and in-memory rooms under conference.pvp.net. It has no TLS, stream
/// management, offline storage, room history or room configuration.
/// </summary>
public sealed class AirXmppHost : IAsyncDisposable
{
    public const string ChatDomain = "pvp.net";
    public const string MucDomain = "conference.pvp.net";
    public const string PrivacyNamespace = "jabber:iq:privacy";
    public const string RosterNamespace = "jabber:iq:roster";
    public const string BindNamespace = "urn:ietf:params:xml:ns:xmpp-bind";
    public const string SessionNamespace = "urn:ietf:params:xml:ns:xmpp-session";
    public const string MucNamespace = "http://jabber.org/protocol/muc";
    public const string MucUserNamespace = "http://jabber.org/protocol/muc#user";
    public const string DiscoInfoNamespace = "http://jabber.org/protocol/disco#info";
    public const string DiscoItemsNamespace = "http://jabber.org/protocol/disco#items";

    private const int MaxStanzaBytes = 64 * 1024;
    private const int MaxDepth = 16;
    private const int MaxConnections = 64;
    private const int MaxRooms = 128;
    private const int MaxOccupants = 32;

    private readonly PlatformAccounts _accounts;
    private readonly PlatformSessions _sessions;
    private readonly PlatformSocialStore _store;
    private readonly int _requestedPort;
    private readonly object _gate = new();
    private readonly HashSet<Connection> _connections = [];
    private readonly Dictionary<long, HashSet<Connection>> _online = [];
    private readonly Dictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, PresenceSnapshot> _presence = [];
    private readonly HashSet<long> _gamePresenceShown = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _expiryTask;
    private CancellationTokenRegistration _externalStopRegistration;
    private int _disposed;

    public AirXmppHost(PlatformAccounts accounts, PlatformSessions sessions, string socialStoreDirectory, int port = 5222)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _store = new PlatformSocialStore(socialStoreDirectory);
    }

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _requestedPort;
    public Func<long, string?>? ActiveChampion { get; set; }
    /// <summary>Whether an account may enter a room, by the room's name before the '@' (<see cref="PlatformGames.MayEnterRoom"/>); null admits all.</summary>
    public Func<long, string, bool>? RoomAccess { get; set; }

    /// <summary>
    /// NOT RETAIL: operator tooling for the host page's Online table. Null when the account has no chat stream; otherwise the
    /// availability its client last announced (<c>show</c>: away, dnd, xa) or "online". Whatever the client sent, not verified.
    /// </summary>
    public string? ChatState(long accountId)
    {
        lock (_gate)
        {
            if (!_online.ContainsKey(accountId)) return null;
            return _presence.TryGetValue(accountId, out var saved) && saved.Show is "away" or "dnd" or "xa" ? saved.Show : "online";
        }
    }

    public async Task RefreshGamePresenceAsync(long accountId)
    {
        Connection? source;
        lock (_gate) source = _online.TryGetValue(accountId, out var clients) ? clients.FirstOrDefault() : null;
        if (source is null) return;
        try { await BroadcastPresenceAsync(accountId, source, false).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listener is not null) return Task.CompletedTask;
        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        _listener.Start(MaxConnections);
        _acceptTask = AcceptLoopAsync(_stop.Token);
        _expiryTask = ExpiryLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var task = StartAsync();
        if (cancellationToken.CanBeCanceled)
            _externalStopRegistration = cancellationToken.Register(static state => ((AirXmppHost)state!)._stop.Cancel(), this);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener?.Stop();
        await AwaitQuietly(_acceptTask).ConfigureAwait(false);
        Connection[] connections;
        lock (_gate) connections = _connections.ToArray();
        foreach (var connection in connections) connection.Abort();
        await AwaitQuietly(_expiryTask).ConfigureAwait(false);
        foreach (var connection in connections) await AwaitQuietly(connection.RunTask).ConfigureAwait(false);
        _externalStopRegistration.Dispose();
        _store.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { continue; }

            client.NoDelay = true;
            Connection connection;
            lock (_gate)
            {
                if (_connections.Count >= MaxConnections)
                {
                    client.Dispose();
                    continue;
                }
                connection = new Connection(this, client, cancellationToken);
                _connections.Add(connection);
            }
            connection.Start();
        }
    }

    private async Task ExpiryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Connection[] ready;
                lock (_gate) ready = _connections.Where(x => x.Ready).ToArray();
                foreach (var connection in ready)
                    if (!connection.SessionIsValid()) connection.Abort();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DetachAsync(Connection connection)
    {
        long? accountId = connection.AccountId;
        var wasOnline = false;
        var departedRooms = new List<(string Key, string Nickname, Connection[] Recipients)>();
        lock (_gate)
        {
            _connections.Remove(connection);
            if (accountId is { } id && _online.TryGetValue(id, out var clients))
            {
                clients.Remove(connection);
                wasOnline = clients.Count == 0;
                if (clients.Count == 0)
                {
                    _online.Remove(id);
                    _presence.Remove(id);
                    _gamePresenceShown.Remove(id);
                }
            }
            foreach (var room in _rooms.Values.ToArray())
            {
                var departed = room.Members.FirstOrDefault(x => x.Connection == connection);
                room.Members.RemoveWhere(x => x.Connection == connection);
                if (departed is not null && room.Members.Count > 0)
                    departedRooms.Add((room.Key, departed.Nickname, room.Members.Select(x => x.Connection).Distinct().ToArray()));
                if (room.Members.Count == 0) _rooms.Remove(room.Key);
            }
        }
        foreach (var departedRoom in departedRooms)
            foreach (var recipient in departedRoom.Recipients)
                await recipient.SendAsync($"<presence from='{XmlEscape(RoomJid(departedRoom.Key, departedRoom.Nickname))}' to='{XmlEscape(recipient.FullJid)}' type='unavailable'><x xmlns='{MucUserNamespace}'><item affiliation='none' role='none'/></x></presence>").ConfigureAwait(false);
        if (accountId is { } offlineId && wasOnline) await BroadcastPresenceAsync(offlineId, connection, true).ConfigureAwait(false);
        connection.Dispose();
    }

    private void RegisterReady(Connection connection)
    {
        if (connection.AccountId is not { } accountId) return;
        Connection[] previous;
        lock (_gate)
        {
            if (!_online.TryGetValue(accountId, out var clients)) _online[accountId] = clients = [];
            previous = clients.ToArray();
            clients.Add(connection);
        }
        // Every stock connection binds the resource 'xiff', so a second login of the same account is a resource conflict:
        // an XMPP server ends the older stream with <conflict/>, which ChatServiceImpl answers by not reconnecting
        // (shouldAttemptReconnectImmediately = false). Without it the older client kept a half-working stream until the
        // session sweep and then retried with a stale password.
        foreach (var old in previous) _ = old.ConflictAsync();
        _ = SendInitialPresenceAsync(connection);
    }

    private async Task SendInitialPresenceAsync(Connection connection)
    {
        if (connection.AccountId is not { } id) return;
        foreach (var contact in _store.Contacts(id).Where(x => (x.Subscription is "from" or "both") && x.Ask))
            await connection.SendAsync(PresenceXml(JidFor(contact.ContactId), "subscribe", null, connection.FullJid)).ConfigureAwait(false);
        await BroadcastPresenceAsync(id, connection, false).ConfigureAwait(false);

        foreach (var contact in _store.Contacts(id).Where(x => (x.Subscription is "to" or "both") && !x.Ask))
        {
            if (_store.IsBlocked(id, contact.ContactId) || _store.IsBlocked(contact.ContactId, id)) continue;
            Connection? source;
            PresenceSnapshot? snapshot;
            lock (_gate)
            {
                source = _online.TryGetValue(contact.ContactId, out var clients) ? clients.FirstOrDefault() : null;
                snapshot = _presence.TryGetValue(contact.ContactId, out var saved) ? saved : null;
            }
            if (source is not null && EffectivePresence(contact.ContactId, snapshot) is { } current)
                await connection.SendAsync(PresenceXml(JidFor(contact.ContactId, source.Resource), null, null, connection.FullJid, current)).ConfigureAwait(false);
        }
    }

    private async Task BroadcastPresenceAsync(long accountId, Connection? source, bool unavailable, PresenceSnapshot? snapshot = null)
    {
        var recipients = new List<Connection>();
        foreach (var contact in _store.AllContacts().Where(x => x.ContactId == accountId && (x.Subscription is "to" or "both") && !x.Ask))
        {
            if (_store.IsBlocked(accountId, contact.OwnerId) || _store.IsBlocked(contact.OwnerId, accountId)) continue;
            lock (_gate)
            {
                if (_online.TryGetValue(contact.OwnerId, out var clients)) recipients.AddRange(clients);
            }
        }
        PresenceSnapshot? current;
        lock (_gate) current = snapshot ?? (_presence.TryGetValue(accountId, out var saved) ? saved : null);
        if (!unavailable) current = EffectivePresence(accountId, current);
        if (!unavailable && current is null) return;
        var from = JidFor(accountId, source?.Resource ?? "xiff");
        foreach (var recipient in recipients.Distinct())
        {
            var xml = unavailable
                ? PresenceXml(from, "unavailable", "Offline", recipient.FullJid, null)
                : PresenceXml(from, null, current is null ? "Online" : null, recipient.FullJid, current);
            await recipient.SendAsync(xml).ConfigureAwait(false);
        }
    }

    private async Task SendRosterAsync(Connection connection, string? responseId)
    {
        if (connection.AccountId is not { } accountId) return;
        var items = new StringBuilder();
        // Ask marks an internal pending request, not a granted subscription.
        // Stock ChatController repairs roster to/from entries automatically and
        // treats a listed requester as an existing buddy. Only publish approved
        // contacts; pending inbound requests are delivered as subscribe presence.
        foreach (var contact in _store.Contacts(accountId).Where(x => !x.Ask && x.Subscription != "none"))
        {
            var account = TryGetAccount(contact.ContactId);
            if (account is null) continue;
            items.Append("<item jid='").Append(XmlEscape(JidFor(account.Id))).Append("' name='")
                .Append(XmlEscape(account.Summoner)).Append("' subscription='").Append(contact.Subscription).Append("'");
            items.Append("><group>").Append(XmlEscape(contact.Group)).Append("</group></item>");
        }
        var id = responseId ?? $"air-roster-{Guid.NewGuid():N}";
        var type = responseId is null ? "set" : "result";
        if (responseId is null && items.Length == 0) return;
        await connection.SendAsync($"<iq type='{type}' id='{XmlEscape(id)}'><query xmlns='{RosterNamespace}'>{items}</query></iq>").ConfigureAwait(false);
    }

    private async Task PushRosterTo(long ownerId)
    {
        Connection[] clients;
        lock (_gate) clients = _online.TryGetValue(ownerId, out var set) ? set.ToArray() : [];
        foreach (var connection in clients) await SendRosterAsync(connection, null).ConfigureAwait(false);
    }

    private async Task PushRosterRemoval(long ownerId, long contactId)
    {
        Connection[] clients;
        lock (_gate) clients = _online.TryGetValue(ownerId, out var set) ? set.ToArray() : [];
        var jid = JidFor(contactId);
        foreach (var connection in clients)
            await connection.SendAsync($"<iq type='set' id='air-roster-remove-{Guid.NewGuid():N}'><query xmlns='{RosterNamespace}'><item jid='{XmlEscape(jid)}' subscription='remove'/></query></iq>").ConfigureAwait(false);
    }

    private async Task DeliverPresence(long targetId, string from, string type)
    {
        Connection[] clients;
        lock (_gate) clients = _online.TryGetValue(targetId, out var set) ? set.ToArray() : [];
        foreach (var connection in clients)
            await connection.SendAsync(PresenceXml(from, type, null, connection.FullJid)).ConfigureAwait(false);
    }

    private async Task HandlePresenceAsync(Connection connection, XElement stanza)
    {
        if (connection.AccountId is not { } senderId) return;
        var to = stanza.Attribute("to")?.Value;
        var type = stanza.Attribute("type")?.Value;
        if (TryParseMucTarget(to, out var roomKey, out var nickname))
        {
            await HandleMucPresenceAsync(connection, stanza, roomKey, nickname, type).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrEmpty(to))
        {
            if (type == "unavailable")
            {
                lock (_gate)
                {
                    _presence.Remove(senderId);
                    _gamePresenceShown.Remove(senderId);
                }
                await BroadcastPresenceAsync(senderId, connection, true).ConfigureAwait(false);
            }
            else
            {
                var snapshot = SnapshotPresence(connection, stanza);
                lock (_gate)
                {
                    _presence[senderId] = snapshot;
                    _gamePresenceShown.Remove(senderId);
                }
                await BroadcastPresenceAsync(senderId, connection, false, snapshot).ConfigureAwait(false);
            }
            return;
        }
        if (!TryResolveJid(to, out var targetId, out _)) return;
        if (_store.IsBlocked(senderId, targetId) || _store.IsBlocked(targetId, senderId)) return;
        switch (type)
        {
            case "subscribe":
                if (_store.TryGetContact(senderId, targetId, out var approved) &&
                    (approved.Subscription == "both" && !approved.Ask || approved.Subscription == "from" && approved.Ask))
                    return;
                // A full roster on either side refuses the request without a disconnect (the store would otherwise throw
                // half-way through and take the sender's stream down).
                if (!_store.HasRoom(senderId, targetId)) return;
                if (!_store.HasRoom(targetId, senderId))
                {
                    await DeliverPresence(senderId, JidFor(targetId), "unsubscribed").ConfigureAwait(false);
                    return;
                }
                SetRelationship(senderId, targetId, "to", true);
                SetRelationship(targetId, senderId, "from", true);
                await DeliverPresence(targetId, JidFor(senderId), "subscribe").ConfigureAwait(false);
                await PushRosterTo(senderId).ConfigureAwait(false);
                await PushRosterTo(targetId).ConfigureAwait(false);
                break;
            case "subscribed":
                if (!_store.TryGetContact(senderId, targetId, out var pending) || pending.Subscription != "from" || !pending.Ask)
                    return;
                SetRelationship(senderId, targetId, "both", false);
                SetRelationship(targetId, senderId, "both", false);
                await DeliverPresence(targetId, JidFor(senderId), "subscribed").ConfigureAwait(false);
                await PushRosterTo(senderId).ConfigureAwait(false);
                await PushRosterTo(targetId).ConfigureAwait(false);
                await SendAvailableAsync(connection, targetId).ConfigureAwait(false);
                await BroadcastPresenceAsync(senderId, connection, false).ConfigureAwait(false);
                break;
            case "unsubscribe":
            case "unsubscribed":
                _store.RemoveContact(senderId, targetId);
                _store.RemoveContact(targetId, senderId);
                await DeliverPresence(targetId, JidFor(senderId), type).ConfigureAwait(false);
                await PushRosterRemoval(senderId, targetId).ConfigureAwait(false);
                await PushRosterRemoval(targetId, senderId).ConfigureAwait(false);
                await PushRosterTo(senderId).ConfigureAwait(false);
                await PushRosterTo(targetId).ConfigureAwait(false);
                break;
            case "probe":
                if (_store.TryGetContact(senderId, targetId, out var subscription) && (subscription.Subscription is "to" or "both") && !subscription.Ask)
                    await SendAvailableAsync(connection, targetId).ConfigureAwait(false);
                break;
            default:
                await BroadcastPresenceAsync(senderId, connection, false).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleRosterIqAsync(Connection connection, XElement iq, XElement query)
    {
        if (connection.AccountId is not { } ownerId) return;
        var type = iq.Attribute("type")?.Value;
        if (type == "get")
        {
            await SendRosterAsync(connection, iq.Attribute("id")?.Value).ConfigureAwait(false);
            return;
        }
        if (type == "set")
        {
            foreach (var item in query.Elements().Where(x => x.Name.LocalName == "item"))
            {
                if (!TryResolveJid(item.Attribute("jid")?.Value, out var contactId, out _)) continue;
                if (string.Equals(item.Attribute("subscription")?.Value, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    _store.RemoveContact(ownerId, contactId);
                    _store.RemoveContact(contactId, ownerId);
                    await PushRosterRemoval(ownerId, contactId).ConfigureAwait(false);
                    await PushRosterRemoval(contactId, ownerId).ConfigureAwait(false);
                    continue;
                }
                var group = item.Elements().FirstOrDefault(x => x.Name.LocalName == "group")?.Value ?? "General";
                var existing = _store.TryGetContact(ownerId, contactId, out var current) ? current : null;
                SetRelationship(ownerId, contactId, existing?.Subscription ?? "none", existing?.Ask ?? false, group);
            }
            await SendIqResultAsync(connection, iq).ConfigureAwait(false);
            await PushRosterTo(ownerId).ConfigureAwait(false);
            return;
        }
        await SendIqErrorAsync(connection, iq, "bad-request").ConfigureAwait(false);
    }

    private async Task HandlePrivacyIqAsync(Connection connection, XElement iq, XElement query)
    {
        if (connection.AccountId is not { } ownerId) return;
        var type = iq.Attribute("type")?.Value;
        if (type == "get")
        {
            var list = BuildPrivacyList(ownerId);
            await connection.SendAsync($"<iq type='result' id='{XmlEscape(iq.Attribute("id")?.Value ?? "")}'><query xmlns='{PrivacyNamespace}'><active name='LOL'/><default name='LOL'/>{list}</query></iq>").ConfigureAwait(false);
            return;
        }
        if (type == "set")
        {
            var list = query.Elements().FirstOrDefault(x => x.Name.LocalName == "list");
            if (list is not null)
            {
                var blockedIds = new List<long>();
                foreach (var item in list.Elements().Where(x => x.Name.LocalName == "item" && x.Attribute("type")?.Value == "jid"))
                {
                    if (!TryResolveJid(item.Attribute("value")?.Value, out var blockedId, out _)) continue;
                    if (item.Attribute("action")?.Value == "deny") blockedIds.Add(blockedId);
                }
                _store.ReplaceBlocks(ownerId, blockedIds);
                foreach (var blockedId in blockedIds)
                {
                    _store.RemoveContact(ownerId, blockedId);
                    _store.RemoveContact(blockedId, ownerId);
                    await PushRosterRemoval(ownerId, blockedId).ConfigureAwait(false);
                    await PushRosterRemoval(blockedId, ownerId).ConfigureAwait(false);
                }
            }
            await SendIqResultAsync(connection, iq).ConfigureAwait(false);
            await PushRosterTo(ownerId).ConfigureAwait(false);
            return;
        }
        await SendIqErrorAsync(connection, iq, "bad-request").ConfigureAwait(false);
    }

    private string BuildPrivacyList(long ownerId)
    {
        var builder = new StringBuilder("<list name='LOL'>");
        var order = 1;
        foreach (var block in _store.Blocks(ownerId))
            builder.Append("<item type='jid' value='").Append(XmlEscape(JidFor(block.BlockedId))).Append("' action='deny' order='").Append(order++).Append("'/>");
        return builder.Append("</list>").ToString();
    }

    private async Task HandleMessageAsync(Connection connection, XElement stanza)
    {
        if (connection.AccountId is not { } senderId) return;
        var to = stanza.Attribute("to")?.Value;
        if (TryParseMucTarget(to, out var roomKey, out _))
        {
            await HandleMucMessageAsync(connection, stanza, roomKey).ConfigureAwait(false);
            return;
        }
        if (!TryResolveJid(to, out var targetId, out var targetResource) || targetId == senderId) return;
        if (_store.IsBlocked(senderId, targetId) || _store.IsBlocked(targetId, senderId)) return;
        var messageType = stanza.Attribute("type")?.Value ?? "chat";
        var body = stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "body")?.Value;
        var subject = stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "subject")?.Value;
        var thread = stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "thread")?.Value;
        if (body is null && subject is null) return;
        var from = JidFor(senderId, connection.Resource);
        var target = JidFor(targetId, targetResource);
        var xml = $"<message from='{XmlEscape(from)}' to='{XmlEscape(target)}' type='{XmlEscape(messageType)}' id='{XmlEscape(stanza.Attribute("id")?.Value ?? "air-message")}'>";
        if (body is not null) xml += "<body>" + XmlEscape(body) + "</body>";
        if (subject is not null) xml += "<subject>" + XmlEscape(subject) + "</subject>";
        if (thread is not null) xml += "<thread>" + XmlEscape(thread) + "</thread>";
        xml += "</message>";
        Connection[] recipients;
        lock (_gate) recipients = _online.TryGetValue(targetId, out var set) ? set.ToArray() : [];
        foreach (var recipient in recipients) await recipient.SendAsync(xml).ConfigureAwait(false);
    }

    private async Task HandleIqAsync(Connection connection, XElement iq)
    {
        if (iq.Attribute("type")?.Value is "result" or "error") return;
        var id = iq.Attribute("id")?.Value ?? "";
        var child = iq.Elements().FirstOrDefault();
        if (child is null) { await SendIqErrorAsync(connection, iq, "bad-request").ConfigureAwait(false); return; }
        var ns = child.Name.NamespaceName;
        if (child.Name.LocalName == "bind" && ns == BindNamespace && iq.Attribute("type")?.Value == "set")
        {
            if (!connection.Authenticated) { await SendIqErrorAsync(connection, iq, "not-authorized").ConfigureAwait(false); return; }
            var resource = child.Elements().FirstOrDefault(x => x.Name.LocalName == "resource")?.Value;
            connection.Bind(resource);
            await connection.SendAsync($"<iq type='result' id='{XmlEscape(id)}'><bind xmlns='{BindNamespace}'><jid>{XmlEscape(connection.FullJid)}</jid></bind></iq>").ConfigureAwait(false);
            return;
        }
        if (child.Name.LocalName == "session" && ns == SessionNamespace && iq.Attribute("type")?.Value == "set")
        {
            if (!connection.Authenticated) { await SendIqErrorAsync(connection, iq, "not-authorized").ConfigureAwait(false); return; }
            connection.MarkReady();
            if (!connection.Ready) { await SendIqErrorAsync(connection, iq, "not-authorized").ConfigureAwait(false); return; }
            await SendIqResultAsync(connection, iq).ConfigureAwait(false);
            RegisterReady(connection);
            return;
        }
        if (!connection.Ready) { await SendIqErrorAsync(connection, iq, "not-authorized").ConfigureAwait(false); return; }
        if (child.Name.LocalName == "query" && ns == RosterNamespace)
        {
            await HandleRosterIqAsync(connection, iq, child).ConfigureAwait(false);
            return;
        }
        if (child.Name.LocalName == "query" && ns == PrivacyNamespace)
        {
            await HandlePrivacyIqAsync(connection, iq, child).ConfigureAwait(false);
            return;
        }
        if (child.Name.LocalName == "query" && (ns is DiscoInfoNamespace or DiscoItemsNamespace))
        {
            var requestedTarget = iq.Attribute("to")?.Value;
            var from = TryParseMucTarget(requestedTarget, out var roomKey, out _)
                ? roomKey
                : MucDomain;
            var occupants = 0;
            if (ns == DiscoInfoNamespace && roomKey.Length > 0)
            {
                lock (_gate)
                    if (_rooms.TryGetValue(roomKey, out var room)) occupants = room.Members.Count;
            }
            var payload = ns == DiscoInfoNamespace
                ? $"<query xmlns='{DiscoInfoNamespace}'><identity category='conference' type='text' name='Amberline'/><feature var='{MucNamespace}'/><x xmlns='jabber:x:data' type='result'><field var='muc#roominfo_occupants' type='text-single'><value>{occupants}</value></field></x></query>"
                : $"<query xmlns='{DiscoItemsNamespace}'></query>";
            await connection.SendAsync($"<iq type='result' id='{XmlEscape(id)}' from='{XmlEscape(from)}' to='{XmlEscape(connection.FullJid)}'>{payload}</iq>").ConfigureAwait(false);
            return;
        }
        if (connection.Ready) await SendIqErrorAsync(connection, iq, "feature-not-implemented").ConfigureAwait(false);
    }

    private async Task HandleMucPresenceAsync(Connection connection, XElement stanza, string roomKey, string nickname, string? type)
    {
        if (connection.AccountId is not { } accountId || roomKey.Length > 128 || nickname.Length > 64) return;
        if (!roomKey.EndsWith("@" + MucDomain, StringComparison.OrdinalIgnoreCase)) return;
        if (type == "unavailable")
        {
            LeaveRoom(connection, roomKey);
            return;
        }
        // The stock client sends its presence payload (<show> and the profile/level/wins <status>) to each room
        // it is in, on joining and again whenever it changes. Room occupants must carry it to every member:
        // TeamSelectionController.room_userJoinHandler reads RoomOccupant.status for the lobby tiles ("Failed
        // presenceUpdate ... status null" without it) and the group-chat and invite user lists draw their
        // icons from it (PresenceController.getProfileIcon).
        var payload = SnapshotPresence(connection, stanza);
        // The stock client joins every room as its summoner name (ChatRoom: room.nickname = currentUserDisplayName =
        // session.summoner.name), and lobby tiles, invite rows and chat lines find occupants by that name alone. A room
        // nickname that is not the account's own summoner name would let one account speak as another, so it is refused
        // (XEP-0045 'not-acceptable') and a nickname another account already holds in the room is a conflict.
        if (!nickname.Equals(TryGetAccount(accountId)?.Summoner, StringComparison.OrdinalIgnoreCase))
        {
            await SendMucErrorAsync(connection, roomKey, nickname, "406", "not-acceptable").ConfigureAwait(false);
            return;
        }
        // A lobby's rooms admit only its own players, whose names anyone could derive from the lobby name (batch 549). The refusal
        // is XEP-0045's members-only answer. Nothing of the room is sent, and no chat text is read or logged here.
        if (RoomAccess is { } access && !access(accountId, roomKey[..roomKey.IndexOf('@')]))
        {
            await SendMucErrorAsync(connection, roomKey, nickname, "407", "registration-required").ConfigureAwait(false);
            return;
        }
        RoomState? room;
        var nicknameTaken = false;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomKey, out room))
            {
                if (_rooms.Count >= MaxRooms) return;
                room = new RoomState(roomKey);
                _rooms[roomKey] = room;
            }
            nicknameTaken = room.Members.Any(x => x.AccountId != accountId &&
                x.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));
            if (!nicknameTaken)
            {
                if (room.Members.Count >= MaxOccupants && !room.Members.Any(x => x.Connection == connection)) return;
                room.Members.RemoveWhere(x => x.Connection == connection);
                room.Members.Add(new RoomMember(connection, nickname, accountId, payload.Show, payload.Status));
            }
        }
        if (nicknameTaken)
        {
            await SendMucErrorAsync(connection, roomKey, nickname, "409", "conflict").ConfigureAwait(false);
            return;
        }
        if (room is null) return;
        RoomMember[] occupants;
        Connection[] members;
        lock (_gate)
        {
            occupants = room.Members.ToArray();
            members = occupants.Select(x => x.Connection).Distinct().ToArray();
        }
        foreach (var member in members)
        {
            foreach (var occupant in occupants)
            {
                var occupantJid = RoomJid(roomKey, occupant.Nickname);
                var status = occupant.Connection == member && occupant.Connection == connection ? "<status code='110'/>" : "";
                // Stock Room creates RoomOccupant.jid from this item, then
                // ChatRoom dereferences it when constructing join messages.
                var show = occupant.Show is { Length: > 0 } ? $"<show>{XmlEscape(occupant.Show)}</show>" : "";
                var text = occupant.Status is { Length: > 0 } ? $"<status>{XmlEscape(occupant.Status)}</status>" : "";
                await member.SendAsync($"<presence from='{XmlEscape(occupantJid)}' to='{XmlEscape(member.FullJid)}'>{show}{text}<x xmlns='{MucUserNamespace}'><item affiliation='member' role='participant' jid='{XmlEscape(occupant.Connection.FullJid)}'/>{status}</x></presence>").ConfigureAwait(false);
            }
        }
        string subject;
        lock (_gate) subject = room.Subject;
        if (!string.IsNullOrEmpty(subject)) await connection.SendAsync($"<message from='{XmlEscape(roomKey)}' to='{XmlEscape(connection.FullJid)}' type='groupchat'><subject>{XmlEscape(subject)}</subject></message>").ConfigureAwait(false);
    }

    private async Task HandleMucMessageAsync(Connection connection, XElement stanza, string roomKey)
    {
        RoomState? room;
        RoomMember? sender;
        RoomMember[] occupants;
        lock (_gate) _rooms.TryGetValue(roomKey, out room);
        if (room is null) return;
        lock (_gate)
        {
            sender = room.Members.FirstOrDefault(x => x.Connection == connection);
            occupants = room.Members.ToArray();
        }
        if (sender is null) return;
        var body = stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "body")?.Value;
        var subject = stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "subject")?.Value;
        var extension = stanza.Descendants().FirstOrDefault(x => x.Name.LocalName == "x" && x.Name.NamespaceName == MucUserNamespace);
        var invite = extension?.Elements().FirstOrDefault(x => x.Name.LocalName == "invite");
        if (invite is not null)
        {
            if (TryResolveJid(invite.Attribute("to")?.Value, out var targetId, out _) && !_store.IsBlocked(sender.AccountId, targetId) && !_store.IsBlocked(targetId, sender.AccountId))
            {
                var reason = invite.Elements().FirstOrDefault(x => x.Name.LocalName == "reason")?.Value;
                // XEP-0045: a mediated invitation comes from the room; the inviter is in the <invite from=...> child.
                var inviteXml = $"<message from='{XmlEscape(roomKey)}' to='{XmlEscape(JidFor(targetId))}' type='normal'><x xmlns='{MucUserNamespace}'><invite from='{XmlEscape(JidFor(sender.AccountId))}' to='{XmlEscape(JidFor(targetId))}'>" +
                    (reason is null ? "" : "<reason>" + XmlEscape(reason) + "</reason>") + "</invite></x></message>";
                Connection[] recipients;
                lock (_gate) recipients = _online.TryGetValue(targetId, out var set) ? set.ToArray() : [];
                foreach (var recipient in recipients) await recipient.SendAsync(inviteXml).ConfigureAwait(false);
            }
        }
        if (body is null && subject is null) return;
        if (subject is not null)
        {
            room.Subject = subject;
            foreach (var member in occupants.Select(x => x.Connection).Distinct())
                await member.SendAsync($"<message from='{XmlEscape(roomKey)}' to='{XmlEscape(member.FullJid)}' type='groupchat'><subject>{XmlEscape(subject)}</subject></message>").ConfigureAwait(false);
            return;
        }
        var from = RoomJid(roomKey, sender.Nickname);
        foreach (var member in occupants.Select(x => x.Connection).Distinct())
            await member.SendAsync($"<message from='{XmlEscape(from)}' to='{XmlEscape(member.FullJid)}' type='groupchat'><body>{XmlEscape(body!)}</body></message>").ConfigureAwait(false);
    }

    private static Task SendMucErrorAsync(Connection connection, string roomKey, string nickname, string code, string condition) =>
        connection.SendAsync($"<presence from='{XmlEscape(RoomJid(roomKey, nickname))}' to='{XmlEscape(connection.FullJid)}' type='error'><x xmlns='{MucNamespace}'/><error code='{code}' type='cancel'><{condition} xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></presence>");

    private void LeaveRoom(Connection connection, string roomKey)
    {
        RoomState? room;
        string departedNickname = "";
        Connection[] recipients = [];
        var wasMember = false;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomKey, out room)) return;
            var departedMember = room.Members.FirstOrDefault(x => x.Connection == connection);
            wasMember = departedMember is not null;
            departedNickname = departedMember?.Nickname ?? connection.Nickname ?? "";
            room.Members.RemoveWhere(x => x.Connection == connection);
            recipients = room.Members.Select(x => x.Connection).Distinct().ToArray();
            if (room.Members.Count == 0) _rooms.Remove(roomKey);
        }
        var departed = RoomJid(roomKey, departedNickname);
        foreach (var recipient in recipients)
            _ = recipient.SendAsync($"<presence from='{XmlEscape(departed)}' to='{XmlEscape(recipient.FullJid)}' type='unavailable'><x xmlns='{MucUserNamespace}'><item affiliation='none' role='none' jid='{XmlEscape(connection.FullJid)}'/></x></presence>");
        // XEP-0045 sends the leaver its own unavailable presence (status 110). XIFF's Room only goes inactive, fires
        // ROOM_LEAVE and drops its presence listener on that echo (Room.as: presence handler, isThisUser), so without
        // it the Room object stays 'active' and cannot join again.
        if (wasMember)
            _ = connection.SendAsync($"<presence from='{XmlEscape(departed)}' to='{XmlEscape(connection.FullJid)}' type='unavailable'><x xmlns='{MucUserNamespace}'><item affiliation='none' role='none' jid='{XmlEscape(connection.FullJid)}'/><status code='110'/></x></presence>");
    }

    // The group the client chose (its localized default, ChatController.internalAddBuddy) is kept when a subscription
    // stanza only changes the relationship; a new contact starts in "General".
    private void SetRelationship(long ownerId, long contactId, string subscription, bool ask, string? group = null) =>
        _store.SetContact(ownerId, contactId,
            group ?? (_store.TryGetContact(ownerId, contactId, out var existing) ? existing.Group : "General"), subscription, ask);

    private PresenceSnapshot SnapshotPresence(Connection connection, XElement stanza)
    {
        string? Bounded(XElement? node)
        {
            if (node is null) return null;
            if (node.Value.Length > 8192) LogProtocolError(connection, "presence-size");
            return node.Value.Length <= 8192 ? node.Value : null;
        }
        return new PresenceSnapshot(Bounded(stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "show")),
            Bounded(stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "status")),
            Bounded(stanza.Elements().FirstOrDefault(x => x.Name.LocalName == "priority")));
    }

    // NOT RETAIL: retail left game presence to the client (MasterGameController.changePresence at IN_PROGRESS and
    // GAME_OVER, PresenceController.setSkinnane at the champion pick) and the server relayed it verbatim. A stale or
    // incomplete client update (no skinname, a missing outOfGame after a hidden-AIR match) left friends with a wrong
    // tooltip, so the platform overlays gameStatus and skinname from the authoritative roster while a match runs.
    // Because the overlay owns gameStatus, it owns <show> too: the stock PresenceController pairs gameStatus=inGame with
    // show=dnd and gameStatus=outOfGame with show=chat (PresenceController.setGameStatus). Publishing one half of that
    // pair from the server and the other half from a stale client stanza is what the friend list rendered wrong: an
    // outOfGame with a leftover dnd is "Do Not Disturb" for a player sitting in the AIR client, and the client's own next
    // correction never arrives while AIR is hidden or gone. PresenceController's own lobby/selection dnd (a client state
    // the platform does not track) is still relayed untouched — that is retail's pairing, not a stale one.
    private PresenceSnapshot? EffectivePresence(long accountId, PresenceSnapshot? snapshot)
    {
        if (ActiveChampion is null) return snapshot;
        var champion = ActiveChampion(accountId);
        bool wasGame;
        lock (_gate)
        {
            wasGame = _gamePresenceShown.Contains(accountId);
            if (champion is not null) _gamePresenceShown.Add(accountId);
        }
        if (champion is null && snapshot is null && !wasGame) return null;
        XElement body;
        try
        {
            using var reader = XmlReader.Create(new StringReader(snapshot?.Status ?? "<body/>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            body = XElement.Load(reader);
            if (body.Name.LocalName != "body") throw new XmlException("Presence body expected.");
        }
        catch (XmlException)
        {
            if (champion is null && !wasGame) return snapshot;
            body = new XElement("body", new XElement("statusMsg", snapshot?.Status ?? ""));
        }
        var status = body.Elements().FirstOrDefault(x => x.Name.LocalName == "gameStatus");
        var skin = body.Elements().FirstOrDefault(x => x.Name.LocalName == "skinname");
        string show;
        if (champion is not null)
        {
            // Presence.SHOW_DND: the stock client's own availability for a non-outOfGame status.
            show = "dnd";
            if (status?.Value == "inGame" && skin?.Value == champion && snapshot?.Show == show) return snapshot;
            if (status is null) body.Add(status = new XElement(body.Name.Namespace + "gameStatus"));
            if (skin is null) body.Add(skin = new XElement(body.Name.Namespace + "skinname"));
            status.Value = "inGame";
            skin.Value = champion;
        }
        else if (wasGame)
        {
            // Presence.SHOW_CHAT: back in the client. A leftover dnd here is the reported Do Not Disturb.
            show = "chat";
            if (status?.Value == "outOfGame" && skin is null && snapshot?.Show == show) return snapshot;
            if (status is null) body.Add(status = new XElement(body.Name.Namespace + "gameStatus"));
            status.Value = "outOfGame";
            skin?.Remove();
        }
        else return snapshot;
        return (snapshot ?? new PresenceSnapshot(null, null, null)) with
        { Show = show, Status = body.ToString(SaveOptions.DisableFormatting) };
    }

    private async Task SendAvailableAsync(Connection connection, long targetId)
    {
        Connection? source;
        PresenceSnapshot? snapshot;
        lock (_gate)
        {
            source = _online.TryGetValue(targetId, out var clients) ? clients.FirstOrDefault() : null;
            snapshot = _presence.TryGetValue(targetId, out var saved) ? saved : null;
        }
        if (source is not null && EffectivePresence(targetId, snapshot) is { } current)
            await connection.SendAsync(PresenceXml(JidFor(targetId, source.Resource), null, null, connection.FullJid,
                current)).ConfigureAwait(false);
    }

    private async Task ProcessAsync(Connection connection, XElement stanza)
    {
        if (connection.Authenticated && !connection.SessionIsValid()) { connection.Abort(); return; }
        var ns = stanza.Name.NamespaceName;
        if (stanza.Name.LocalName == "auth")
        {
            if (ns != "urn:ietf:params:xml:ns:xmpp-sasl") { connection.Abort(); return; }
            await connection.AuthenticateAsync(stanza).ConfigureAwait(false);
            return;
        }
        // The framer extracts individual stanzas, so an inherited stream namespace
        // appears empty; an explicit unrelated namespace must never be accepted.
        if (ns is not ("" or "jabber:client")) { connection.Abort(); return; }
        switch (stanza.Name.LocalName)
        {
            case "iq": await HandleIqAsync(connection, stanza).ConfigureAwait(false); break;
            case "presence" when connection.Ready: await HandlePresenceAsync(connection, stanza).ConfigureAwait(false); break;
            case "message" when connection.Ready: await HandleMessageAsync(connection, stanza).ConfigureAwait(false); break;
            default: break;
        }
    }

    private bool TryResolveJid(string? jid, out long accountId, out string resource)
    {
        accountId = 0;
        resource = "";
        if (string.IsNullOrWhiteSpace(jid)) return false;
        var bare = jid.Trim();
        var slash = bare.IndexOf('/');
        if (slash >= 0) { resource = bare[(slash + 1)..]; bare = bare[..slash]; }
        var at = bare.IndexOf('@');
        if (at <= 0 || !bare[(at + 1)..].Equals(ChatDomain, StringComparison.OrdinalIgnoreCase)) return false;
        var account = _accounts.FindInternalName(bare[..at]);
        if (account is null) return false;
        accountId = account.Id;
        return true;
    }

    private bool TryParseMucTarget(string? jid, out string roomKey, out string nickname)
    {
        roomKey = "";
        nickname = "";
        if (string.IsNullOrWhiteSpace(jid)) return false;
        var slash = jid.IndexOf('/');
        var bare = slash >= 0 ? jid[..slash] : jid;
        var at = bare.IndexOf('@');
        if (at <= 0 || !bare[(at + 1)..].Equals(MucDomain, StringComparison.OrdinalIgnoreCase)) return false;
        roomKey = bare;
        nickname = slash >= 0 ? jid[(slash + 1)..] : "";
        return true;
    }

    private PlatformAccount? TryGetAccount(long id)
    {
        try { return _accounts.Get(id); } catch (ArgumentException) { return null; }
    }

    private string JidFor(long accountId, string? resource = null)
    {
        var account = TryGetAccount(accountId) ?? throw new ArgumentException("Account does not exist.");
        return "sum" + account.SummonerId + "@" + ChatDomain + (string.IsNullOrEmpty(resource) ? "" : "/" + resource);
    }

    private static string RoomJid(string roomKey, string nickname) => roomKey + "/" + nickname;
    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? "";
    private static void LogProtocolError(Connection connection, string kind) =>
        Console.Error.WriteLine($"AIR XMPP protocol error kind={kind} remote={connection.RemoteEndpoint} account={connection.AccountId?.ToString() ?? "anonymous"}");
    private static string PresenceXml(string from, string? type, string? status, string? to, PresenceSnapshot? snapshot = null)
    {
        var builder = new StringBuilder("<presence from='").Append(XmlEscape(from)).Append("'");
        if (!string.IsNullOrEmpty(to)) builder.Append(" to='").Append(XmlEscape(to)).Append("'");
        if (!string.IsNullOrEmpty(type)) builder.Append(" type='").Append(XmlEscape(type)).Append("'");
        builder.Append(">");
        if (snapshot?.Show is { Length: > 0 } show) builder.Append("<show>").Append(XmlEscape(show)).Append("</show>");
        if (snapshot?.Priority is { Length: > 0 } priority) builder.Append("<priority>").Append(XmlEscape(priority)).Append("</priority>");
        if (snapshot?.Status is { Length: > 0 } savedStatus) status = savedStatus;
        if (status is not null) builder.Append("<status>").Append(XmlEscape(status)).Append("</status>");
        return builder.Append("</presence>").ToString();
    }

    private sealed record PresenceSnapshot(string? Show, string? Status, string? Priority);

    private static Task SendIqResultAsync(Connection connection, XElement iq) =>
        connection.SendAsync($"<iq type='result' id='{XmlEscape(iq.Attribute("id")?.Value ?? "")}'/>");

    private static Task SendIqErrorAsync(Connection connection, XElement iq, string condition) =>
        connection.SendAsync($"<iq type='error' id='{XmlEscape(iq.Attribute("id")?.Value ?? "")}'><error type='cancel'><{condition} xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></iq>");

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch { }
    }

    private sealed class RoomState(string key)
    {
        public string Key { get; } = key;
        public string Subject { get; set; } = "";
        public HashSet<RoomMember> Members { get; } = [];
    }

    /// <summary>A room occupant. Show and Status are the occupant's last presence payload, relayed to every member.</summary>
    private sealed record RoomMember(Connection Connection, string Nickname, long AccountId, string? Show = null, string? Status = null);

    private sealed class Connection : IDisposable
    {
        private readonly AirXmppHost _host;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationToken _hostToken;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly XmlFramer _framer = new();
        private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly char[] _decodedChars = new char[8192];
        private int _closed;
        private Task _runTask = Task.CompletedTask;
        private bool _streamStarted;
        private bool _authenticated;
        private string _domain = ChatDomain;
        private long? _accountId;
        private string _username = "";
        private string _chatPassword = "";
        private string _resource = "xiff";
        private bool _bound;

        public Connection(AirXmppHost host, TcpClient client, CancellationToken hostToken)
        {
            _host = host;
            _client = client;
            _stream = client.GetStream();
            _hostToken = hostToken;
        }

        public long? AccountId => _accountId;
        public bool Authenticated => _authenticated;
        public string RemoteEndpoint => _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        public string Resource => _resource;
        public string? Nickname => _accountId is { } id ? _host.TryGetAccount(id)?.Summoner : null;
        public bool Ready { get; private set; }
        public string FullJid => _accountId is { } id ? _host.JidFor(id, _resource) : "";
        public Task RunTask => _runTask;

        public void Start() => _runTask = RunAsync();

        public async Task RunAsync()
        {
            var bytes = new byte[8192];
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
            try
            {
                while (!_hostToken.IsCancellationRequested && Volatile.Read(ref _closed) == 0)
                {
                    idle.CancelAfter(_streamStarted ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(20));
                    var read = await _stream.ReadAsync(bytes.AsMemory(), idle.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    try
                    {
                        _decoder.Convert(bytes, 0, read, _decodedChars, 0, _decodedChars.Length, false,
                            out _, out var charsUsed, out _);
                        _framer.Append(_decodedChars.AsSpan(0, charsUsed));
                    }
                    catch (DecoderFallbackException)
                    {
                        LogProtocolError(this, "utf8");
                        break;
                    }
                    while (_framer.TryTake(out var xml, out var streamOpen, out var streamClose))
                    {
                        if (streamClose) { Abort(); break; }
                        if (streamOpen) { await HandleStreamOpenAsync(xml).ConfigureAwait(false); continue; }
                        var stanza = Parse(xml);
                        if (stanza is not null) await _host.ProcessAsync(this, stanza).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_hostToken.IsCancellationRequested || Volatile.Read(ref _closed) != 0) { }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (XmlException) { LogProtocolError(this, "xml"); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
            { LogProtocolError(this, exception.GetType().Name); }
            finally { await _host.DetachAsync(this).ConfigureAwait(false); }
        }

        private async Task HandleStreamOpenAsync(string raw)
        {
            if (!_streamStarted)
            {
                var requested = ParseStreamDomain(raw);
                if (requested != ChatDomain && requested != MucDomain) { Abort(); return; }
                _domain = requested;
                _streamStarted = true;
            }
            await SendAsync(StreamHeader(_domain)).ConfigureAwait(false);
            await SendAsync(StreamFeatures(_authenticated)).ConfigureAwait(false);
        }

        public async Task AuthenticateAsync(XElement auth)
        {
            async Task RejectAuthenticationAsync()
            {
                LogProtocolError(this, "authentication");
                await SendAsync("<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><not-authorized/></failure>").ConfigureAwait(false);
                Abort();
            }
            if (_authenticated || !_streamStarted || auth.Attribute("mechanism")?.Value != "PLAIN") { Abort(); return; }
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Value)); }
            catch (FormatException) { await RejectAuthenticationAsync().ConfigureAwait(false); return; }
            var parts = decoded.Split('\0');
            if (parts.Length != 3 || parts[1].Length is < 1 or > 64 || parts[2].Length > 256)
            {
                await RejectAuthenticationAsync().ConfigureAwait(false); return;
            }
            var username = parts[1];
            if (parts[0].Length > 0 && !parts[0].Equals(username + "@" + ChatDomain, StringComparison.OrdinalIgnoreCase))
            {
                await RejectAuthenticationAsync().ConfigureAwait(false); return;
            }
            var account = _host._accounts.FindInternalName(username);
            var authenticatedId = _host._sessions.AuthenticateChat(username, parts[2]);
            if (account is null || authenticatedId is null || authenticatedId.Value != account.Id)
            {
                await RejectAuthenticationAsync().ConfigureAwait(false); return;
            }
            _accountId = account.Id;
            _username = username;
            _chatPassword = parts[2];
            _authenticated = true;
            await SendAsync("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>").ConfigureAwait(false);
        }

        public void Bind(string? resource)
        {
            if (!_authenticated || _bound) return;
            _resource = string.IsNullOrWhiteSpace(resource) ? "xiff" : resource.Length > 64 ? "xiff" : resource;
            _bound = true;
        }

        public void MarkReady() => Ready = _authenticated && _bound;
        public bool SessionIsValid() => _username.Length > 0 && _host._sessions.IsChatSessionAlive(_username, _chatPassword);

        public Task SendAsync(string xml)
        {
            if (Volatile.Read(ref _closed) != 0) return Task.CompletedTask;
            return SendCoreAsync(xml);
        }

        private async Task SendCoreAsync(string xml)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Abort(); return; }
            try
            {
                if (Volatile.Read(ref _closed) == 0)
                    await _stream.WriteAsync(Encoding.UTF8.GetBytes(xml), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { Abort(); }
            catch { Abort(); }
            finally { _sendGate.Release(); }
        }

        public async Task ConflictAsync()
        {
            await SendAsync("<stream:error><conflict xmlns='urn:ietf:params:xml:ns:xmpp-streams'/></stream:error></stream:stream>").ConfigureAwait(false);
            Abort();
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _client.Close(); } catch { }
        }

        public void Dispose()
        {
            Abort();
            // Broadcast writes can still be unwinding after socket close. The
            // semaphore owns no OS handle; let those writers release it safely.
        }

        private static XElement? Parse(string xml)
        {
            var settings = new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaxStanzaBytes,
                MaxCharactersFromEntities = 0,
                XmlResolver = null,
                ConformanceLevel = ConformanceLevel.Document
            };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                while (reader.Read())
                    if (reader.Depth > MaxDepth) throw new XmlException("XMPP stanza nesting limit exceeded.");
            }
            using var finalReader = XmlReader.Create(new StringReader(xml), settings);
            return XElement.Load(finalReader, LoadOptions.PreserveWhitespace);
        }

        private static string ParseStreamDomain(string raw)
        {
            var synthetic = raw.EndsWith("/>", StringComparison.Ordinal) ? raw : raw[..^1] + "/>";
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 };
            using var reader = XmlReader.Create(new StringReader(synthetic), settings);
            reader.MoveToContent();
            return reader.GetAttribute("to")?.Trim().ToLowerInvariant() ?? ChatDomain;
        }

        private static string StreamHeader(string domain) => $"<stream:stream from='{ChatDomain}' id='{Guid.NewGuid():N}' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>";
        private static string StreamFeatures(bool authenticated) => authenticated
            ? $"<stream:features><bind xmlns='{BindNamespace}'/><session xmlns='{SessionNamespace}'/></stream:features>"
            : "<stream:features><mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><mechanism>PLAIN</mechanism></mechanisms></stream:features>";
    }

    private sealed class XmlFramer
    {
        private readonly StringBuilder _buffer = new();

        public void Append(string text)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxStanzaBytes * 2) throw new XmlException("XMPP input limit exceeded.");
        }

        public void Append(ReadOnlySpan<char> text)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxStanzaBytes * 2) throw new XmlException("XMPP input limit exceeded.");
        }

        public bool TryTake(out string xml, out bool streamOpen, out bool streamClose)
        {
            xml = "";
            streamOpen = false;
            streamClose = false;
            TrimLeadingWhitespace();
            if (_buffer.Length == 0) return false;
            if (_buffer[0] != '<') throw new XmlException("XMPP stanza must start with XML.");
            if (_buffer.ToString().StartsWith("<?xml", StringComparison.Ordinal))
            {
                var declarationEnd = _buffer.ToString().IndexOf("?>", StringComparison.Ordinal);
                if (declarationEnd < 0) return false;
                _buffer.Remove(0, declarationEnd + 2);
                return TryTake(out xml, out streamOpen, out streamClose);
            }
            if (_buffer.ToString().StartsWith("<stream:stream", StringComparison.Ordinal))
            {
                var end = FindTagEnd(0);
                if (end < 0) return false;
                xml = _buffer.ToString(0, end + 1);
                _buffer.Remove(0, end + 1);
                streamOpen = true;
                return true;
            }
            if (_buffer.ToString().StartsWith("</stream:stream", StringComparison.Ordinal))
            {
                var end = FindTagEnd(0);
                if (end < 0) return false;
                xml = _buffer.ToString(0, end + 1);
                _buffer.Remove(0, end + 1);
                streamClose = true;
                return true;
            }
            var depth = 0;
            for (var i = 0; i < _buffer.Length; i++)
            {
                var c = _buffer[i];
                if (c != '<') continue;
                if (i + 4 < _buffer.Length && _buffer.ToString(i, 4) == "<!--")
                {
                    var commentEnd = _buffer.ToString().IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (commentEnd < 0) return false;
                    i = commentEnd + 2;
                    continue;
                }
                if (i + 9 < _buffer.Length && _buffer.ToString(i, 9) == "<![CDATA[")
                {
                    var cdataEnd = _buffer.ToString().IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    if (cdataEnd < 0) return false;
                    i = cdataEnd + 2;
                    continue;
                }
                if (i + 1 < _buffer.Length && _buffer[i + 1] == '?')
                {
                    var piEnd = _buffer.ToString().IndexOf("?>", i + 2, StringComparison.Ordinal);
                    if (piEnd < 0) return false;
                    i = piEnd + 1;
                    continue;
                }
                if (i + 1 < _buffer.Length && _buffer[i + 1] == '/')
                {
                    var end = FindTagEnd(i);
                    if (end < 0) return false;
                    if (--depth < 0) throw new XmlException("Unexpected XML close.");
                    if (depth == 0)
                    {
                        xml = _buffer.ToString(0, end + 1);
                        _buffer.Remove(0, end + 1);
                        return true;
                    }
                    i = end;
                    continue;
                }
                var openEnd = FindTagEnd(i);
                if (openEnd < 0) return false;
                var selfClosing = openEnd > i && _buffer[openEnd - 1] == '/';
                if (!selfClosing) depth++;
                if (depth > MaxDepth) throw new XmlException("XMPP stanza nesting limit exceeded.");
                if (selfClosing && depth == 0)
                {
                    xml = _buffer.ToString(0, openEnd + 1);
                    _buffer.Remove(0, openEnd + 1);
                    return true;
                }
                i = openEnd;
            }
            if (_buffer.Length > MaxStanzaBytes) throw new XmlException("XMPP stanza limit exceeded.");
            return false;
        }

        private int FindTagEnd(int start)
        {
            var quote = '\0';
            for (var i = start; i < _buffer.Length; i++)
            {
                var c = _buffer[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') quote = c;
                else if (c == '>') return i;
            }
            return -1;
        }

        private void TrimLeadingWhitespace()
        {
            var count = 0;
            while (count < _buffer.Length && char.IsWhiteSpace(_buffer[count])) count++;
            if (count > 0) _buffer.Remove(0, count);
        }
    }
}
