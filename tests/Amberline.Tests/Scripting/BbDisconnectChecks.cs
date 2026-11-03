using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// The disconnect path of the script engine (batch 184): a dropped seat runs the shipped disconnect hooks and the reconnect undoes them.
/// Every check here drives the real session (<c>Session.Disconnect</c> and <c>AuthenticateReconnect</c>) on a live match.
/// </summary>
public static class BbDisconnectChecks
{
    [Check("scripting.bb-disconnect-timer-applies-on-drop-and-reconnect-removes-it")]
    public static void DisconnectTimerAppliesOnDropAndTheReconnectRemovesIt()
    {
        using var match = new Match("Annie");
        var runtime = match.Runtime;
        var seat = runtime.Session.Participants[0];
        var playerId = seat.PlayerId;
        TestAssert.False(match.Hero.Buffs.Any(buff => buff.Name == "DisconnectTimer"), "no disconnect timer while the player is connected");
        TestAssert.True(runtime.Session.Disconnect(seat.ConnectionId!.Value), "the player disconnects");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "DisconnectTimer"),
            "the disconnect applies DisconnectTimer (APBonusDamageToTowers' BuffOnDisconnect, 25000 s)");
        var participant = runtime.Session.AuthenticateReconnect(77, runtime.Session.BuildKeyCheck(playerId));
        TestAssert.True(participant is not null, "the player reconnects");
        TestAssert.False(match.Hero.Buffs.Any(buff => buff.Name == "DisconnectTimer"),
            "the reconnect runs DisconnectTimer's BuffOnReconnect, which removes it");
    }
}
