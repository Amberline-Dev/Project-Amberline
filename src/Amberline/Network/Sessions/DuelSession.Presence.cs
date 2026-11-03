using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

/// <summary>
/// World events 55 OnLeave and 56 OnReconnect. The stock client prints and raises both itself when it
/// receives Exit (0xA0) or Reconnect (0x10) naming a seat's client id (the same way it
/// raises 29/25/35 from the death packets), so the server sends those carriers and never a 0x4A for
/// events 54-56. Event 54 OnQuit ("has left the game!") comes from NetApp status event 5, which the
/// client's channel-zero receive path raises for a registry-removal entry (opcode 1, client id at
/// offset 4; <see cref="LoadingPackets.RegistryRemove"/>) just before it drops the entry from its
/// roster. A lost connection (ENet disconnect, AFK kick, replaced transport) therefore announces
/// "has left the game!" and a returning seat gets its registry entry back before the reconnect notice.
/// </summary>
public sealed partial class DuelSession
{
    /// <summary>
    /// Reconstruction: Exit to every other in-game client when a human seat sends C2S_Exit
    /// (0x94). Retail's trigger is not recoverable from the shipped files; a lost connection (ENet
    /// disconnect, AFK kick) is not an Exit and sends nothing. Call before detaching the seat.
    /// </summary>
    internal IReadOnlyList<SessionOutboundPacket> BuildExitNotice(long connectionId) =>
        Participant(connectionId) is { } seat
            ? PresenceNotice(seat, LoadingPackets.PlayerExited(seat.ClientId)) : [];

    /// <summary>
    /// Reconnect to every other in-game client once a returning seat is ready. NOT RETAIL:
    /// the stock handler's local-client branch (reconnect mode until 0x49) shows retail also sent it
    /// to the reconnecting client. That handler dereferences the HUD, which a restarted client has
    /// not built yet, and this emulator's reconnect uses the ordinary loading path, so that client
    /// is left out and shows no banner.
    /// </summary>
    internal IReadOnlyList<SessionOutboundPacket> BuildReconnectNotice(SessionParticipant seat)
    {
        var output = new List<SessionOutboundPacket>();
        // Reconstruction: a clean roster needs the entry back on exactly the clients that dropped it
        // (see BuildDisconnectNotice); the client adds an entry it still has again, so only those
        // recipients get it, and only while they are still in game. Retail's reconnect traffic to the
        // other clients is not recoverable from the shipped files.
        if (_registryDroppedBy.Remove(seat.PlayerId, out var droppedFor))
            foreach (var other in PresenceRecipients(seat))
                if (droppedFor.Contains(other.PlayerId))
                    output.AddRange(ToParticipant(other,
                        [LoadingPackets.RegistryAdd(seat.ClientId, seat.PlayerId)]));
        output.AddRange(PresenceNotice(seat, LoadingPackets.PlayerReconnected(seat.ClientId)));
        return output;
    }

    /// <summary>
    /// Channel-zero registry removal to every other in-game client when a human seat's transport is
    /// lost, which the stock client turns into "@srctr name@ has left the game!" (world event 54).
    /// Call before <see cref="DisconnectPackets"/> detaches the seat. Reconstruction: retail's
    /// NetApp server removes the registry entry when a peer drops and sends nothing else; the shipped
    /// files show only the client side. A voluntary exit sends this after the 0xA0 notice, so the
    /// other clients see both "has quit!" and "has left the game!" (each layer announces on its own).
    /// </summary>
    internal IReadOnlyList<SessionOutboundPacket> BuildDisconnectNotice(long connectionId)
    {
        // A seat that is already dropped (a returning client that crashed again while loading) has no
        // entry left to remove; announcing again would repeat the banner for nobody who is playing.
        if (Participant(connectionId) is not { } seat || seat.IsBot || !Started || _combatGameOver() ||
            _registryDroppedBy.ContainsKey(seat.PlayerId))
            return [];
        var output = new List<SessionOutboundPacket>();
        var droppedFor = new HashSet<ulong>();
        foreach (var other in PresenceRecipients(seat))
        {
            droppedFor.Add(other.PlayerId);
            output.AddRange(ToParticipant(other, [LoadingPackets.RegistryRemove(seat.ClientId, seat.PlayerId)]));
        }
        _registryDroppedBy[seat.PlayerId] = droppedFor;
        return output;
    }

    private IReadOnlyList<SessionOutboundPacket> PresenceNotice(
        SessionParticipant who, PlaintextPacket packet)
    {
        // Bots have no transport and never leave; nothing is announced before the start barrier or
        // after game over, when exits are the normal end of the match.
        if (who.IsBot || !Started || _combatGameOver()) return [];
        var output = new List<SessionOutboundPacket>();
        foreach (var seat in PresenceRecipients(who))
            output.AddRange(ToParticipant(seat, [packet]));
        return output;
    }

    private IEnumerable<SessionParticipant> PresenceRecipients(SessionParticipant who) =>
        _participants.Where(seat => seat != who && !seat.IsBot && seat.Spawned && seat.Ready);
}
