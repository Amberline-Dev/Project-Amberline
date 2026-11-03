using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using RtmpSharp.Messaging;
using RtmpSharp.Messaging.Messages;
using RtmpSharp.Net;

namespace Amberline.Tests.Air;

public static class AirTransportChecks
{
    [Check("server.air-amf-dynamic-trait-reference-body")]
    public static void RepeatedAnonymousObjectsKeepTheirBodies()
    {
        // First object: inline dynamic trait, x=1, terminator.
        // Second: trait 0 and string 0 references, x=2, terminator.
        var expected = Convert.FromHexString("0A0B0103780401010A0100040201");
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext()))
        {
            writer.WriteAmf3Item(new AsObject { ["x"] = 1 });
            writer.WriteAmf3Item(new AsObject { ["x"] = 2 });
        }
        TestAssert.Equal(expected, buffer.ToArray(), "trait reuse must not omit object fields");
        using var input = new MemoryStream(expected);
        using var reader = new AmfReader(input, new SerializationContext());
        TestAssert.Equal(1, ((AsObject)reader.ReadAmf3Item())["x"]);
        TestAssert.Equal(2, ((AsObject)reader.ReadAmf3Item())["x"]);
        TestAssert.Equal(input.Length, input.Position);
    }

    [Check("server.air-amf-array-collection-date-reference-wire")]
    public static void ArrayCollectionReferencesFollowInlineDateObjects()
    {
        // AMF3 table: Date=0, collection=1, backing array=2, second
        // collection=3, second backing array=4. Flex mutates each backing
        // array, so distinct empty collections cannot share object reference 2.
        // A repeated collection must still reference object 1.
        var expected = Convert.FromHexString("080100000000000000000A0743")
            .Concat(Encoding.UTF8.GetBytes("flex.messaging.io.ArrayCollection"))
            .Concat(Convert.FromHexString("0901010A07000901010A020800")).ToArray();
        var firstCollection = new ArrayCollection();
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext()))
        {
            writer.WriteAmf3Item(DateTime.UnixEpoch);
            writer.WriteAmf3Item(firstCollection);
            writer.WriteAmf3Item(new ArrayCollection());
            writer.WriteAmf3Item(firstCollection);
            writer.WriteAmf3Item(DateTime.UnixEpoch);
        }
        TestAssert.Equal(expected, buffer.ToArray(), "distinct empty collections need distinct backing arrays");

        // Feed independently specified bytes, never the writer's result.
        using var input = new MemoryStream(expected);
        using var reader = new AmfReader(input, new SerializationContext());
        TestAssert.Equal(DateTime.UnixEpoch, reader.ReadAmf3Item());
        var decodedFirst = reader.ReadAmf3Item();
        TestAssert.True(decodedFirst is ArrayCollection { Count: 0 });
        TestAssert.True(reader.ReadAmf3Item() is ArrayCollection { Count: 0 });
        TestAssert.True(ReferenceEquals(decodedFirst, reader.ReadAmf3Item()),
            "the same collection retains its AMF object reference");
        TestAssert.Equal(DateTime.UnixEpoch, reader.ReadAmf3Item(), "Date reference zero must resolve to the Date");
        TestAssert.Equal(input.Length, input.Position);
    }

    [Check("server.air-amf-nesting-bound")]
    public static void NestedArraysAreBoundedBeforeStackExhaustion()
    {
        // AMF3 array: marker 9, inline one-element header 3, empty associative key 1.
        using var bytes = new MemoryStream(Enumerable.Range(0, 256).SelectMany(_ => new byte[] { 9, 3, 1 }).Append((byte)1).ToArray());
        using var reader = new AmfReader(bytes, new SerializationContext());
        TestAssert.Throws<InvalidDataException>(() => reader.ReadAmf3Item());
    }

    private const string AuthenticationAlias = "com.riotgames.platform.login.AuthenticationCredentials";
    private const string TypedReplyAlias = "com.riotgames.platform.login.UnknownSession";
    private const string ValidToken = "token-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123456789";

    [Check("server.air-rtmp-typed-remoting")]
    public static void RtmpHandshakeRemotingKeepsUnknownTypedObjectsAndCorrelatesFaults()
    {
        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        try
        {
            var credentials = AirWire.Object(AuthenticationAlias,
                ("username", "alice"),
                ("password", ValidToken),
                ("domain", "lolclient.lol.riotgames.com"));
            var result = client.InvokeAsync<object>("endpoint", "typedService", "echo", credentials)
                .GetAwaiter().GetResult();

            TestAssert.True(result is AsObject, "unknown typed AMF3 results must remain AsObject");
            var typedResult = (AsObject)result!;
            TestAssert.Equal(TypedReplyAlias, typedResult.TypeName);
            TestAssert.Equal("ok", typedResult["status"]);

            var arguments = fixture.LastArguments;
            TestAssert.True(arguments is not null && arguments.Length == 1,
                "the route must receive one remoting argument");
            TestAssert.True(arguments![0] is AsObject,
                "unknown typed AuthenticationCredentials must decode as AsObject");
            var typedCredentials = (AsObject)arguments[0]!;
            TestAssert.Equal(AuthenticationAlias, typedCredentials.TypeName);
            TestAssert.Equal("alice", typedCredentials["username"]);
            TestAssert.Equal(ValidToken, typedCredentials["password"]);

            InvocationException? fault = null;
            try
            {
                _ = client.InvokeAsync<object>("endpoint", "typedService", "validate", "bad")
                    .GetAwaiter().GetResult();
            }
            catch (InvocationException exception)
            {
                fault = exception;
            }

            TestAssert.True(fault is not null, "route ArgumentException must be a correlated Flex fault");
            TestAssert.Equal("Server.Error.Validation", fault!.FaultCode);
            TestAssert.True(fault.SourceException is ErrorMessage error &&
                            !string.IsNullOrWhiteSpace(error.CorrelationId),
                "fault correlation id must identify the remoting request");
        }
        finally
        {
            client.Close();
        }
    }

    [Check("server.air-typed-platform-fault")]
    public static void PlatformFaultsCarryTheRetailRootCauseObject()
    {
        // LcdsErrorHandler routes on rootCause (a registered PlatformException alias), and ServerError.errorCode
        // and messageArguments read errorCode and substitutionArguments from it.
        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        try
        {
            InvocationException? fault = null;
            try { _ = client.InvokeAsync<object>("endpoint", "typedService", "gameFault", "x").GetAwaiter().GetResult(); }
            catch (InvocationException exception) { fault = exception; }
            TestAssert.True(fault?.SourceException is ErrorMessage { RootCause: AsObject }, "the fault carries a rootCause object");
            var root = (AsObject)((ErrorMessage)fault!.SourceException!).RootCause!;
            TestAssert.Equal("com.riotgames.platform.game.PlayerAlreadyInGameException", root.TypeName);
            TestAssert.Equal("PG-0008", root["errorCode"]);
            TestAssert.Equal("com.riotgames.platform.game.PlayerAlreadyInGameException", root["rootCauseClassname"]);
            var arguments = root["substitutionArguments"] as System.Collections.IList;
            TestAssert.True(arguments is { Count: 2 }, "the resource text takes the game name and the existing game id");
            TestAssert.Equal("Practice", arguments![0]);

            fault = null;
            try { _ = client.InvokeAsync<object>("endpoint", "typedService", "validate", "bad").GetAwaiter().GetResult(); }
            catch (InvocationException exception) { fault = exception; }
            TestAssert.True(fault?.SourceException is ErrorMessage { RootCause: null }, "an untyped refusal keeps no rootCause");
        }
        finally { client.Close(); }
    }

    [Check("server.air-channel-login")]
    public static void ChannelLoginAcceptsExactAndFlexFoldedCredentialsAndRejectsWrongToken()
    {
        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        try
        {
            InvocationException? wrongToken = null;
            try
            {
                _ = client.LoginAsync("alice", "wrong-token").GetAwaiter().GetResult();
            }
            catch (InvocationException exception)
            {
                wrongToken = exception;
            }

            TestAssert.True(wrongToken is not null, "wrong channel credentials must fault");
            TestAssert.Equal("Server.Error.Authentication", wrongToken!.FaultCode);

            TestAssert.True(client.LoginAsync("alice", ValidToken).GetAwaiter().GetResult(),
                "canonical credentials must authenticate");

            var canonical = Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{ValidToken}"));
            var folded = FoldFlexBase64(canonical);
            TestAssert.True(folded.Contains('\n'), "fixture credential must exercise a folded line");
            TestAssert.True(client.LoginAsync(folded).GetAwaiter().GetResult(),
                "Flex base64 folded at column 76 must authenticate");
        }
        finally
        {
            client.Close();
        }
    }

    [Check("server.air-rtmp-reconnect-resumes-session")]
    public static void ReconnectPresentingTheChannelCredentialsResumesTheSessionOnceAndNothingWeakerDoes()
    {
        using var fixture = HostFixture.Start();
        var first = fixture.Connect();
        TestAssert.True(first.LoginAsync("alice", ValidToken).GetAwaiter().GetResult());
        Subscribe(first, "gn-1", "gn-client");
        var firstId = first.ClientId;
        first.Close();
        TestAssert.True(fixture.WaitParked(1), "the closed connection's session must be parked, not logged out");

        string Credentials(string user, string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));

        // The account name alone, a wrong token, a wrong name and a malformed value prove nothing.
        foreach (var weak in new[] { "", Credentials("alice", ""), Credentials("alice", "wrong-token"),
                     Credentials("mallory", ValidToken), "not base64!", new string('A', 2000) })
        {
            var refused = fixture.Connect(weak, firstId);
            AssertSubscriptionFault(refused, "gn-1", "gn-client");
            refused.Close();
        }
        TestAssert.Equal(1, fixture.ParkedCount, "refused attempts must not spend the parked session");

        // The stock client's reconnect: its channel credentials in the connect command, no login command after it,
        // and the Flex client id of its first connection in every later message.
        var again = fixture.Connect(Credentials("alice", ValidToken), firstId);
        Subscribe(again, "gn-1", "gn-client");
        AssertPush(fixture.Host, again, 1, "gn", "gn-1", "gn-client", "after-resume");
        TestAssert.Equal(0, fixture.ParkedCount, "a resume spends the parked binding");

        // The binding is spent: the same credentials from another connection resume nothing.
        var replay = fixture.Connect(Credentials("alice", ValidToken), firstId);
        AssertSubscriptionFault(replay, "gn-1", "gn-client");
        replay.Close();
        again.Close();
    }

    [Check("server.air-channel-subscriptions")]
    public static void ChannelSubscriptionsAreAccountScopedAndPushPreservesConsumerIds()
    {
        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        try
        {
            TestAssert.True(client.LoginAsync("alice", ValidToken).GetAwaiter().GetResult());
            Subscribe(client, "gn-1", "gn-client");
            Subscribe(client, "cn-1", "cn-client");
            Subscribe(client, "bc", "bc-1");

            AssertSubscriptionFault(client, "gn-2", "gn-other");
            AssertSubscriptionFault(client, "cn-2", "cn-other");
            AssertSubscriptionFault(client, "bc", "bc-2");

            AssertPush(fixture.Host, client, 1, "gn", "gn-1", "gn-client", "gn-body");
            AssertPush(fixture.Host, client, 1, "cn", "cn-1", "cn-client", "cn-body");
            AssertPush(fixture.Host, client, 1, "bc", "bc", "bc-1", "bc-body");
            AssertPush(fixture.Host, client, 1, null, "gn-1", "gn-client",
                AirWire.Object("com.riotgames.platform.game.GameDTO", ("id", 7d)));
            foreach (var alias in new[] { "StoreAccountBalanceNotification", "StoreFulfillmentNotification" })
                AssertPush(fixture.Host, client, 1, null, "cn-1", "cn-client",
                    AirWire.Object("com.riotgames.platform.messaging." + alias, ("ip", 38d)));
            TestAssert.False(fixture.Host.Push(2, "other-account", "gn"),
                "an account must not receive another account's push");
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>
    /// Batch 549: the host's player notice is the stock BroadcastNotification (ticker key "generic", never the shutdown id 11), and
    /// Broadcast reaches every logged-in connection's bc subscription and nothing else.
    /// </summary>
    [Check("server.air-broadcast-notification")]
    public static void BroadcastNotificationReachesEveryBcSubscription()
    {
        var notice = AirDtos.BroadcastNotification("Restart in 2 minutes", "ERROR");
        TestAssert.Equal("com.riotgames.platform.broadcast.BroadcastNotification", notice.TypeName);
        var message = (AsObject)((ArrayCollection)notice["broadcastMessages"]!).Cast<object>().Single();
        TestAssert.Equal("com.riotgames.platform.broadcast.BroadcastMessage", message.TypeName);
        TestAssert.Equal("generic", message["messageKey"]);
        TestAssert.Equal("Restart in 2 minutes", message["content"]);
        TestAssert.Equal("ERROR", message["severity"]);
        TestAssert.False(Equals(message["id"], 11d), "id 11 would start the client's own shutdown countdown");
        TestAssert.Equal(0, ((ArrayCollection)AirDtos.BroadcastNotification(null)["broadcastMessages"]!).Count);

        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        var stranger = fixture.Connect();
        try
        {
            TestAssert.True(client.LoginAsync("alice", ValidToken).GetAwaiter().GetResult());
            Subscribe(client, "gn-1", "gn-client");
            TestAssert.Equal(0, fixture.Host.Broadcast("nothing"), "a connection without a bc subscription gets no broadcast");
            Subscribe(client, "bc", "bc-1");
            var received = new TaskCompletionSource<MessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.MessageReceived += (_, args) => { if (args.Subtopic == "bc") received.TrySetResult(args); };
            var strangerGot = false;
            stranger.MessageReceived += (_, _) => strangerGot = true;
            TestAssert.Equal(1, fixture.Host.Broadcast(notice));
            var got = received.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            TestAssert.Equal("bc-1", got.ClientId);
            TestAssert.Equal(notice.TypeName, ((AsObject)got.Body).TypeName);
            TestAssert.False(strangerGot, "an unauthenticated connection has no subscription to reach");
        }
        finally { client.Close(); stranger.Close(); }
    }

    [Check("server.air-bounded-malformed-frames")]
    public static void HandshakeAndMalformedFramesStayBounded()
    {
        using var fixture = HostFixture.Start();
        using (var oversized = OpenRaw(fixture.Host.BoundEndPoint))
        {
            var stream = oversized.GetStream();
            CompleteHandshake(stream);
            // RTMP message lengths are 24-bit; 0x400001 is one byte above
            // the transport's four-megabyte compatibility bound.
            WriteHeader(stream, 0x400001, 0x11);
            TestAssert.Equal(0, ReadOne(stream),
                "an oversized declared RTMP message must close the connection before allocation");
        }

        using (var truncated = OpenRaw(fixture.Host.BoundEndPoint))
        {
            var stream = truncated.GetStream();
            CompleteHandshake(stream);
            WriteHeader(stream, 8, 0x11);
            stream.WriteByte(0x02);
            truncated.Client.Shutdown(SocketShutdown.Send);
            TestAssert.Equal(0, ReadOne(stream),
                "a truncated RTMP payload must terminate cleanly without an unbounded read");
        }
    }

    [Check("server.air-reply-before-match-push")]
    public static void RemotingAcknowledgementPrecedesImmediateMatchPush()
    {
        using var fixture = HostFixture.Start();
        var client = fixture.Connect();
        try
        {
            TestAssert.True(client.LoginAsync("alice", ValidToken).GetAwaiter().GetResult());
            Subscribe(client, "gn-1", "match-client");
            using var assigned = new ManualResetEventSlim();
            Task<object>? reply = null;
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.MessageReceived += (_, message) =>
            {
                if (message.Body is not string text || text != "match-ready") return;
                // The test's assignment can race the network reader. Waiting
                // for assignment does not change the order of incoming frames.
                var ready = assigned.Wait(TimeSpan.FromSeconds(2));
                observed.TrySetResult(ready && reply!.IsCompletedSuccessfully);
            };
            fixture.ResponseSent = (_, destination, operation) =>
            {
                if (destination == "typedService" && operation == "echo")
                    fixture.Host.Push(1, "match-ready");
            };
            reply = client.InvokeAsync<object>("endpoint", "typedService", "echo", new object[] { "join" });
            assigned.Set();
            TestAssert.True(observed.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(),
                "AIR must receive the remoting result before an immediately eligible match push");
            TestAssert.Equal("ok", ((AsObject)reply.GetAwaiter().GetResult())["status"]);
        }
        finally { client.Close(); }
    }

    private static void Subscribe(RtmpClient client, string subtopic, string clientId)
    {
        TestAssert.True(client.SubscribeAsync(null!, "messagingDestination", subtopic, clientId)
            .GetAwaiter().GetResult(), $"subscription {subtopic} must succeed");
    }

    private static void AssertSubscriptionFault(RtmpClient client, string subtopic, string clientId)
    {
        InvocationException? fault = null;
        try
        {
            _ = client.SubscribeAsync(null!, "messagingDestination", subtopic, clientId)
                .GetAwaiter().GetResult();
        }
        catch (InvocationException exception)
        {
            fault = exception;
        }

        TestAssert.True(fault is not null, $"subscription {subtopic} must be rejected");
        TestAssert.Equal("Server.Error.InvalidSession", fault!.FaultCode);
    }

    private static void AssertPush(
        AirRtmpHost host,
        RtmpClient client,
        long accountId,
        string? channel,
        string subtopic,
        string expectedClientId,
        object body)
    {
        var received = new TaskCompletionSource<MessageReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MessageReceivedEventArgs> handler = (_, message) =>
        {
            if (message.Subtopic == subtopic)
                received.TrySetResult(message);
        };
        client.MessageReceived += handler;
        try
        {
            TestAssert.True(host.Push(accountId, body, channel), $"{channel} push must queue");
            var message = received.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            TestAssert.Equal(expectedClientId, message.ClientId);
            TestAssert.Equal(subtopic, message.Subtopic);
            if (body is AsObject expected)
            {
                var actual = (AsObject)message.Body;
                TestAssert.Equal(expected.TypeName, actual.TypeName);
                foreach (var field in expected) TestAssert.Equal(field.Value, actual[field.Key]);
            }
            else TestAssert.Equal(body, message.Body);
        }
        finally
        {
            client.MessageReceived -= handler;
        }
    }

    private static string FoldFlexBase64(string canonical)
    {
        var builder = new StringBuilder(canonical.Length + canonical.Length / 76);
        for (var offset = 0; offset < canonical.Length; offset += 76)
        {
            var count = Math.Min(76, canonical.Length - offset);
            builder.Append(canonical, offset, count);
            if (count == 76)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static TcpClient OpenRaw(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        client.ConnectAsync(endPoint.Address, endPoint.Port).GetAwaiter().GetResult();
        return client;
    }

    private static void CompleteHandshake(NetworkStream stream)
    {
        var c01 = new byte[1537];
        c01[0] = 3;
        RandomNumberGenerator.Fill(c01.AsSpan(1));
        stream.Write(c01);

        var serverHandshake = new byte[3073];
        ReadExactly(stream, serverHandshake);
        stream.Write(serverHandshake, 1, 1536);
    }

    private static void WriteHeader(NetworkStream stream, int length, byte messageType)
    {
        var header = new byte[12]
        {
            0x03,
            0x00, 0x00, 0x00,
            (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            messageType,
            0x00, 0x00, 0x00, 0x00
        };
        stream.Write(header);
    }

    private static int ReadOne(NetworkStream stream)
    {
        var buffer = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return stream.ReadAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult();
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        stream.ReadExactlyAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult();
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly ConcurrentDictionary<string, long> accounts = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<RtmpClient> clients = [];
        private readonly StringWriter log = new();
        private object?[]? lastArguments;

        private HostFixture(AirRtmpHost host, StringWriter log)
        {
            Host = host;
            this.log = log;
        }

        public AirRtmpHost Host { get; }
        public Action<string, string, string>? ResponseSent { get; set; }
        public object?[]? LastArguments => Volatile.Read(ref lastArguments);

        public static HostFixture Start()
        {
            HostFixture? fixture = null;
            var log = new StringWriter();
            var host = new AirRtmpHost(
                new IPEndPoint(IPAddress.Loopback, 0),
                (connection, destination, operation, arguments) => fixture!.Route(connection, destination, operation, arguments),
                (connection, username, token) => fixture!.Authenticate(connection, username, token),
                connection => fixture!.AccountFor(connection),
                connection => fixture!.Disconnect(connection),
                log, (connection, destination, operation) => fixture!.ResponseSent?.Invoke(connection, destination, operation),
                (connection, username, token) => fixture!.Resume(connection, username, token),
                connection => fixture!.ConnectionClosed(connection));
            fixture = new HostFixture(host, log);
            host.StartAsync().GetAwaiter().GetResult();
            return fixture;
        }

        public RtmpClient Connect(string credentials = "", string flexClientId = "nil")
        {
            var client = new RtmpClient(
                new Uri($"rtmp://{Host.BoundEndPoint.Address}:{Host.BoundEndPoint.Port}"),
                new SerializationContext(),
                ObjectEncoding.Amf3)
            { ConnectCredentials = credentials, ConnectFlexClientId = flexClientId };
            client.ConnectAsync().GetAwaiter().GetResult();
            // The stock client keeps the Flex client id of its first connection (RTMPChannel sets it only while null).
            if (flexClientId != "nil") client.ClientId = flexClientId;
            clients.Add(client);
            return client;
        }

        /// <summary>The sessions whose connection closed without a logout, as the platform parks them.</summary>
        public int ParkedCount => parked.Count;

        public bool WaitParked(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (parked.Count != count && DateTime.UtcNow < deadline) Thread.Sleep(10);
            return parked.Count == count;
        }

        public void Dispose()
        {
            foreach (var client in clients)
                client.Close();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            log.Dispose();
        }

        private object? Route(string connection, string destination, string operation, object?[] arguments)
        {
            if (destination == "typedService" && operation == "echo")
            {
                Volatile.Write(ref lastArguments, arguments);
                return AirWire.Object(TypedReplyAlias, ("status", "ok"));
            }

            if (destination == "typedService" && operation == "validate")
                throw new ArgumentException("The request is invalid.");

            if (destination == "typedService" && operation == "gameFault")
                throw AirFault.AlreadyInGame(joining: true, "Practice", 7);

            throw new ArgumentException($"Unsupported test operation {destination}/{operation}.");
        }

        private bool Authenticate(string connection, string username, string token)
        {
            if (username == "alice" && token == ValidToken)
            {
                accounts[connection] = 1;
                return true;
            }

            return false;
        }

        private long? AccountFor(string connection) =>
            accounts.TryGetValue(connection, out var account) ? account : null;

        private void Disconnect(string connection) => accounts.TryRemove(connection, out _);

        private readonly ConcurrentDictionary<string, long> parked = new(StringComparer.Ordinal);

        private void ConnectionClosed(string connection)
        {
            if (accounts.TryRemove(connection, out var account)) parked[connection] = account;
        }

        // The platform's rule in miniature: the proof is the channel login's own (account name and token), and a parked
        // binding is spent by the first connection that presents it.
        private bool Resume(string connection, string username, string token)
        {
            if (username != "alice" || token != ValidToken) return false;
            var key = parked.Keys.FirstOrDefault();
            if (key is null || !parked.TryRemove(key, out var account)) return false;
            accounts[connection] = account;
            return true;
        }
    }
}
