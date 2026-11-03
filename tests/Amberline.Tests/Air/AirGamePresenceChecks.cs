using Amberline.Platform;

namespace Amberline.Tests.Air;

public static partial class AirSocialChecks
{
    [Check("server.air-xmpp-game-champion-presence")]
    public static void GameChampionPresenceFollowsAuthoritativeMatch()
        => GameChampionPresenceAsync().GetAwaiter().GetResult();

    private static async Task GameChampionPresenceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "league-air-presence-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var sessions = new PlatformSessions(accounts);
        var alice = accounts.RegisterPublic("presencealice", "testing-password", "Alice");
        var bob = accounts.RegisterPublic("presencebob", "testing-password", "Bob");
        Authenticate(accounts, sessions, alice, "alice");
        Authenticate(accounts, sessions, bob, "bob");
        string? activeChampion = null;
        await using var host = new AirXmppHost(accounts, sessions, Path.Combine(directory, "social"), 0)
        { ActiveChampion = id => id == alice.Id ? activeChampion : null };
        await host.StartAsync();
        await using var a = await SocialClient.ConnectAsync(host.Port, alice, sessions, "alice");
        await using var b = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
        await a.SendAsync($"<presence to='{Jid(bob)}' type='subscribe'/>");
        await b.ReadUntilAsync("type='subscribe'");
        await b.SendAsync($"<presence to='{Jid(alice)}' type='subscribed'/>");
        await b.ReadUntilAsync("subscription='both'");

        // No tracked platform game is not proof that a fresh client-reported
        // game ended (for example, a match started through the fallback).
        var external = "<body><gameStatus>inGame</gameStatus><skinname>Ezreal</skinname></body>";
        await a.SendAsync("<presence><status>" +
            System.Security.SecurityElement.Escape(external) + "</status></presence>");
        var untracked = await b.ReadUntilAsync("Ezreal") + await b.ReadUntilAsync("</presence>");
        TestAssert.True(untracked.Contains("&lt;gameStatus&gt;inGame&lt;/gameStatus&gt;", StringComparison.Ordinal),
            "an unknown game does not erase the client's current champion presence");

        var body = "<body><gameStatus>championSelect</gameStatus><statusMsg>My status</statusMsg></body>";
        await a.SendAsync("<presence><show>dnd</show><status>" +
            System.Security.SecurityElement.Escape(body) + "</status><priority>5</priority></presence>");
        await b.ReadUntilAsync("championSelect");
        await b.ReadUntilAsync("</presence>");
        activeChampion = "Annie";
        await host.RefreshGamePresenceAsync(alice.Id);
        var live = await b.ReadUntilAsync("Annie") + await b.ReadUntilAsync("</presence>");
        TestAssert.True(live.Contains("&lt;gameStatus&gt;inGame&lt;/gameStatus&gt;", StringComparison.Ordinal) &&
            live.Contains("&lt;skinname&gt;Annie&lt;/skinname&gt;", StringComparison.Ordinal),
            "friends receive the authoritative champion with in-game status");
        // PresenceController.setGameStatus pairs a non-outOfGame status with Presence.SHOW_DND and outOfGame with
        // SHOW_CHAT; the overlay publishes the status, so it publishes that half of the pair too (retail's client did
        // both). The user's text and priority are still theirs.
        TestAssert.True(live.Contains("My status", StringComparison.Ordinal) &&
            live.Contains("<show>dnd</show>", StringComparison.Ordinal) &&
            live.Contains("<priority>5</priority>", StringComparison.Ordinal),
            "in game presence carries the client's dnd/inGame pair, the status text and the priority");

        await using var reconnect = await SocialClient.ConnectAsync(host.Port, bob, sessions, "bob");
        var replay = await reconnect.ReadUntilAsync("Annie") + await reconnect.ReadUntilAsync("</presence>");
        TestAssert.True(replay.Contains("&lt;skinname&gt;Annie&lt;/skinname&gt;", StringComparison.Ordinal),
            "a reconnecting friend receives the current champion");

        // The second login of the same account ended the first stream (resource conflict); the friend continues on it.
        await b.ReadUntilAsync("<conflict");
        activeChampion = null;
        await host.RefreshGamePresenceAsync(alice.Id);
        var ended = await reconnect.ReadUntilAsync("outOfGame") + await reconnect.ReadUntilAsync("</presence>");
        // ReadUntilAsync returns every buffered byte up to the marker, so a
        // preceding queued presence must not be attributed to this update.
        var endedStart = ended.LastIndexOf("<presence from='", StringComparison.Ordinal);
        TestAssert.True(endedStart >= 0, "the game-end update is a complete presence stanza");
        ended = ended[endedStart..];
        TestAssert.False(ended.Contains("skinname", StringComparison.Ordinal),
            "the champion is removed when the authoritative game ends");
        TestAssert.True(ended.Contains("My status", StringComparison.Ordinal),
            "ending the game keeps the custom status message");
        // The stock friend list renders show=dnd as Do Not Disturb (BuddyRenderer.updateStatusLabel), so an outOfGame
        // correction that kept the client's game-time dnd is exactly the reported "everyone shows DND".
        TestAssert.True(ended.Contains("<show>chat</show>", StringComparison.Ordinal) &&
            !ended.Contains("<show>dnd</show>", StringComparison.Ordinal),
            "a player back in the client is available again, not left announcing dnd");
        await a.SendAsync("<presence type='unavailable'/>");
        var offline = await reconnect.ReadUntilAsync("unavailable") + await reconnect.ReadUntilAsync("</presence>");
        TestAssert.True(offline.Contains("Offline", StringComparison.Ordinal) &&
            !offline.Contains("skinname", StringComparison.Ordinal),
            "logout sends offline presence without a stale champion");
    }
}
