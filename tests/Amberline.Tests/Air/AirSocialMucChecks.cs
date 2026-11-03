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
