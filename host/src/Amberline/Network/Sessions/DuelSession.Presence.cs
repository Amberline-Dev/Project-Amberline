using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

/// <summary>
/// The leave and return lines the stock client prints and raises itself, and the packets that raise them. The server never
/// sends a 0x4A for events 54-56. Event 54 OnQuit ("has left the game!") comes from NetApp status event 5, which the client's
/// channel-zero receive path raises for a registry-removal entry (opcode 1, client id at offset 4;
/// <see cref="LoadingPackets.RegistryRemove"/>) just before it drops the entry from its roster. Every departure sends that
/// removal to the other ready human seats, so each leave shows one line with the name. Event 56 OnReconnect ("has reconnected!")
/// comes from Reconnect (0x10), and a returning seat gets its registry entry back before that notice. Event 55 OnLeave
/// ("has quit!") comes from Exit, which the players no longer get (see <see cref="BuildExitNotice"/>).
/// </summary>
public sealed partial class DuelSession
{
    /// <summary>
    /// Reconstruction: no Exit goes to the other players when a human seat sends C2S_Exit (0x94 on 126, 0x95 on 132).
    /// The leave is announced once, by the registry removal from <see cref="BuildDisconnectNotice"/>, with the name. The exit
    /// looks its name up by client id (the 0xa0 case, which walks the same object list as the 0x10 case). In the
    /// owner's report the exit arrived after the removal, on another channel (3 and 0), and showed no name; why the lookup
    /// failed is inferred, not traced. The spectator stream has no removal, so it still gets the exit. Retail's trigger is not
    /// recoverable from the shipped files; a lost connection (ENet disconnect, AFK kick) is not an Exit and sends nothing. Call
    /// before detaching the seat.
    /// </summary>
    internal IReadOnlyList<SessionOutboundPacket> BuildExitNotice(long connectionId)
    {
        if (Participant(connectionId) is { } seat)
            PresenceNotice(seat, LoadingPackets.PlayerExited(seat.ClientId), toPlayers: false);
        return [];
    }

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
    /// Channel-zero registry removal to every other in-game client when a human seat leaves, by a voluntary exit or a lost
    /// transport. The stock client turns it into "@srctr name@ has left the game!" (world event 54). Call before
    /// <see cref="DisconnectPackets"/> detaches the seat. Reconstruction: retail's NetApp server removes the registry entry when
    /// a peer drops and sends nothing else; the shipped files show only the client side. A voluntary exit sends nothing else to
    /// the players (see <see cref="BuildExitNotice"/>).
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
        SessionParticipant who, PlaintextPacket packet, bool toPlayers = true)
    {
        // Bots have no transport and never leave; nothing is announced before the start barrier or
        // after game over, when exits are the normal end of the match.
        if (who.IsBot || !Started || _combatGameOver()) return [];
        var output = new List<SessionOutboundPacket>();
        if (toPlayers)
            foreach (var seat in PresenceRecipients(who))
                output.AddRange(ToParticipant(seat, [packet]));
        // Batch 847: the spectator's client prints the same "has quit!" and "has reconnected!" lines. The channel-zero registry entries
        // (BuildDisconnectNotice) are a connected client's roster and stay out of the stream.
        Spectator?.Record(packet);
        return output;
    }

    private IEnumerable<SessionParticipant> PresenceRecipients(SessionParticipant who) =>
        _participants.Where(seat => seat != who && !seat.IsBot && seat.Spawned && seat.Ready);
}
