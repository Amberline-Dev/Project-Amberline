using Amberline.Platform;

namespace Amberline.Tests.Air;

public static partial class AirSocialChecks
{
    [Check("server.air-xmpp-muc-leave-nickname-and-resource-conflict")]
    public static void MucLeaveIsAcknowledgedNicknamesAreBoundAndASecondLoginEndsTheFirst() =>
        MucAsync().GetAwaiter().GetResult();

    private static async Task MucAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-muc-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var alice = accounts.RegisterPublic("mucalice", "testing-password", "Alice");
        var bob = accounts.RegisterPublic("mucbob", "testing-password", "Bob");
        Authenticate(accounts, sessions, alice, "alice");
        Authenticate(accounts, sessions, bob, "bob");
        await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0);
        await host.StartAsync();
        await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
        await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
        var room = "air-muc-check@" + AirXmppHost.MucDomain;

        await a.SendAsync($"<presence to='{room}/Alice'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
        await a.ReadUntilAsync("</presence>");
        // A room nickname is the account's own summoner name: speaking as someone else is refused.
        await b.SendAsync($"<presence to='{room}/Alice'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
        await b.ReadUntilAsync("not-acceptable");
        await b.SendAsync($"<presence to='{room}/Bob'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
        await b.ReadUntilAsync("code='110'");
        await b.ReadUntilAsync("</presence>");

        // XIFF's Room only goes inactive on the echo of its own unavailable presence.
        await a.SendAsync($"<presence to='{room}/Alice' type='unavailable'/>");
        var echo = await a.ReadUntilAsync("type='unavailable'");
        TestAssert.True(echo.Contains("/Alice", StringComparison.Ordinal), "the leaver receives its own unavailable presence");
        await a.ReadUntilAsync("code='110'");
        TestAssert.True((await b.ReadUntilAsync("type='unavailable'")).Contains("/Alice", StringComparison.Ordinal),
            "the other occupant is told");

        // A second login of the account ends the first stream with <conflict/>.
        await using var again = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
        await a.ReadUntilAsync("<conflict");
    }

    /// <summary>
    /// Batch 748: a user the host deletes can still have a chat stream in a room for a moment (the account goes first). A message to the room
    /// then still reaches every member and never takes the sender's stream down, and a privacy list that still names the deleted user is
    /// answered. Forget closes the deleted user's stream, the room hears it, and the other member keeps chatting.
    /// </summary>
    [Check("server.air-xmpp-deleted-user-leaves-rooms")]
    public static void ADeletedUsersStreamNeverTakesTheOthersDown() => DeletedInRoomAsync().GetAwaiter().GetResult();

    private static async Task DeletedInRoomAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-deleted-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
            var sessions = new PlatformSessions(accounts);
            var alice = accounts.RegisterPublic("delalice", "testing-password", "Alice");
            var bob = accounts.RegisterPublic("delbob", "testing-password", "Bob");
            Authenticate(accounts, sessions, alice, "alice");
            Authenticate(accounts, sessions, bob, "bob");
            // Alice blocked Bob earlier: a row a cut-short cleanup could leave behind.
            using (var social = new PlatformSocialStore(Path.Combine(directory, "social"))) social.SetBlocked(alice.Id, bob.Id, true);
            await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0);
            await host.StartAsync();
            await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
            await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
            var room = "air-deleted-check@" + AirXmppHost.MucDomain;
            await a.SendAsync($"<presence to='{room}/Alice'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            await a.ReadUntilAsync("code='110'");
            await b.SendAsync($"<presence to='{room}/Bob'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            await b.ReadUntilAsync("code='110'");
            await a.ReadUntilAsync($"from='{room}/Bob'");

            TestAssert.Equal(DeleteOutcome.Deleted, accounts.Delete(bob.Id), "the host deletes Bob; his stream is still in the room");
            await a.SendAsync($"<message to='{room}' type='groupchat'><body>still here</body></message>");
            TestAssert.True((await a.ReadUntilAsync("</message>")).Contains("<body>still here</body>", StringComparison.Ordinal),
                "Alice's own message comes back: her stream stayed up");
            TestAssert.True((await b.ReadUntilAsync("</message>")).Contains("<body>still here</body>", StringComparison.Ordinal), "the room reaches every member");
            await a.SendAsync($"<iq type='get' id='privacy-1'><query xmlns='{AirXmppHost.PrivacyNamespace}'/></iq>");
            await a.ReadUntilAsync("id='privacy-1'");
            TestAssert.True((await a.ReadUntilAsync("</iq>")).Contains($"value='{Jid(bob)}'", StringComparison.Ordinal),
                "a privacy list that still names the deleted user is answered");

            // The host's cleanup: the sessions end and Forget closes Bob's stream at once; the room hears it and Alice keeps chatting.
            sessions.EndAccount(bob.Id);
            host.Forget(bob.Id);
            TestAssert.True((await a.ReadUntilAsync("type='unavailable'")).Contains($"from='{room}/Bob'", StringComparison.Ordinal), "the room hears that Bob left");
            var closed = false;
            try { await b.ReadUntilAsync("never sent"); }
            catch (Exception ex) when (ex is EndOfStreamException or IOException) { closed = true; }
            TestAssert.True(closed, "Bob's stream is closed at once, not at the next session sweep");
            await a.SendAsync($"<message to='{room}' type='groupchat'><body>after</body></message>");
            TestAssert.True((await a.ReadUntilAsync("</message>")).Contains("<body>after</body>", StringComparison.Ordinal), "Alice's stream survives the delete");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// Batch 549: a lobby's chat rooms, whose names the stock client derives from the lobby name, admit only that lobby's players
    /// (champion select only the player's own team); public rooms stay open.
    /// </summary>
    [Check("server.air-xmpp-lobby-rooms-members-only")]
    public static void LobbyRoomsAdmitOnlyTheirOwnPlayers() => LobbyRoomsAsync().GetAwaiter().GetResult();

    private static async Task LobbyRoomsAsync()
    {
        // ChatController.obfuscateChatRoom on a known SHA-1 vector: sha1("abc").
        TestAssert.Equal("pu~a9993e364706816aba3e25717850c26c9cd0d89d", PlatformGames.ChatRoomName("pu", "ABC"));
        var directory = Path.Combine(Path.GetTempPath(), "league-air-social-rooms-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
            var sessions = new PlatformSessions(accounts);
            var alice = accounts.RegisterPublic("roomalice", "testing-password", "Alice");
            var bob = accounts.RegisterPublic("roombob", "testing-password", "Bob");
            Authenticate(accounts, sessions, alice, "alice");
            Authenticate(accounts, sessions, bob, "bob");
            Directory.CreateDirectory(Path.Combine(directory, "game-data"));
            var executable = Path.Combine(directory, "server-placeholder.exe");
            File.WriteAllBytes(executable, []);
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var gamePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            await using var manager = new Amberline.Host.GameManager(new Amberline.Host.HostSettings(Path.Combine(directory, "game-data"), executable,
                FirstGamePort: gamePort, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(directory, "games"));
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            var name = "A Rather Long Lobby Name That The Header Cuts Short"; // 51 characters: the header shows 50 and "..."
            var id = Convert.ToInt32(((RtmpSharp.IO.AsObject)games.Create(alice, name, "", 1, 10, 1, "CLASSIC", "NONE"))["id"]);
            var teamSelect = PlatformGames.ChatRoomName("ap", name[..50] + "...");
            TestAssert.True(games.MayEnterRoom(alice.Id, teamSelect), "the owner enters the team-select room the client names");
            TestAssert.False(games.MayEnterRoom(alice.Id, PlatformGames.ChatRoomName("ap", name)), "the client hashes the cut name");
            TestAssert.True(games.MayEnterRoom(alice.Id, PlatformGames.ChatRoomName("c1", name + "_c1")), "own team's champion select");
            TestAssert.False(games.MayEnterRoom(alice.Id, PlatformGames.ChatRoomName("c2", name + "_c2")), "never the other team's");
            TestAssert.True(games.MayEnterRoom(alice.Id, PlatformGames.ChatRoomName("pg", "endGame" + id)));
            TestAssert.False(games.MayEnterRoom(bob.Id, teamSelect), "an outsider cannot enter by the predictable name");
            TestAssert.False(games.MayEnterRoom(bob.Id, teamSelect.ToUpperInvariant()), "nor by its upper-case spelling (the room table ignores case)");
            TestAssert.True(games.MayEnterRoom(bob.Id, PlatformGames.ChatRoomName("pu", "general")), "public rooms stay open");

            await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0) { RoomAccess = games.MayEnterRoom };
            await host.StartAsync();
            await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
            await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
            var room = teamSelect + "@" + AirXmppHost.MucDomain;
            await a.SendAsync($"<presence to='{room}/Alice'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            await a.ReadUntilAsync("code='110'");
            await b.SendAsync($"<presence to='{room}/Bob'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            await b.ReadUntilAsync("registration-required");
            games.Join(bob, id, "");
            await b.SendAsync($"<presence to='{room}/Bob'><x xmlns='{AirXmppHost.MucNamespace}'/></presence>");
            await b.ReadUntilAsync("code='110'");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
