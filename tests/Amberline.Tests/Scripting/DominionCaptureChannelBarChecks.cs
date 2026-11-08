using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Batch 710 (owner, 7 November 2026: "the channeling bar briefly appears before disappearing, the capturing animation ends early before it
/// actually ends and after the channeling bar disappears the character just stands in place until they actually start capturing"). The stock
/// client's use packet (0x3F) on point C's guardian, from in range and from out of range, through the production dispatcher.
/// </summary>
public static partial class DominionGuardianChecks
{
    /// <summary>A seat's packets with the game time they were delivered at.</summary>
    private sealed class TimedSeat(MatchRuntime runtime) : IReplicationTransport
    {
        public List<(double At, byte[] Bytes)> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add((runtime.Combat.CurrentTime, packet.Plaintext.ToArray()));
    }

    private static TimedSeat ObserveTimed(Match match, Team team)
    {
        var participant = match.Runtime.Session.Participants.Single(seat => seat.Team == team);
        var capture = new TimedSeat(match.Runtime);
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team, participant.Hero!.NetId, capture,
            ready: true, view: participant.View, clientId: participant.ClientId));
        return capture;
    }

    // DIAGNOSTICS: test seam, the stock client's UseObject (0x3F: the user at 1, the used unit at 5) through the production dispatcher.
    private static bool Use(MatchRuntime runtime, Entity hero, Entity used)
    {
        var payload = new byte[9];
        payload[0] = 0x3F;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), used.NetId);
        var participant = runtime.Session.Participants.Single(seat => seat.Hero == hero);
        return runtime.Dependencies.Gameplay(new SessionGameplayContext(participant, hero, 1, payload, used.NetId, used, hero));
    }

}
