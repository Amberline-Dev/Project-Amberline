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


    /// <summary>
    /// Batch 933 (owner, 21 November 2026, Fizz capturing a turret: "both windup and loop fail to loop and instead switch to idle after a
    /// little while... in every capture attempt"). A capture held to its end (about 13 s): the capturer's client gets the shipped script's two
    /// looped clips (OdinCaptureChannel: ChannelingStart's Channel_WNDUP and the 1.5 s charge's Channel, both Loop true, Lock false) with the bit
    /// its AnimationComponent wraps a clip on, 0x1 (AtomicClipController's update, the client: at the clip's end it loops on bit 0 and
    /// otherwise ends the clip, and the unit falls back to idle), and from the cast answer to the channel's end nothing else that would cut them:
    /// no other play or stop, no route and no stop order. The 0x10 the server sent only marks the sequencer entry, so each clip
    /// played once, about a second for Fizz, before the idle.
    /// </summary>
    [Check("dominion.capture-channel-loops-run-to-the-channel-end", TimeoutSeconds = 300, Serial = true)]
    public static void TheCaptureChannelLoopsRunToTheChannelEnd()
    {
        using var match = StartMatch();
        var (runtime, guardian, hero) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = match.OrderHome;
        hero.Position = Offset(guardian, -300.0);
        Advance(runtime, 1.0);
        var seat = ObserveTimed(match, Team.Chaos);
        TestAssert.True(Use(runtime, hero, guardian), "the use is accepted");
        TestAssert.True(Until(runtime, () => runtime.Spells.ChargedCaptureTarget(hero) == guardian, 6.0), "the capture channel charges");
        TestAssert.True(Until(runtime, () => runtime.Spells.CaptureChannelTarget(hero) is null, 40.0), "the capture channel ends");
        var end = runtime.Combat.CurrentTime;
        var own = seat.Packets.Where(p => p.Bytes.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(p.Bytes.AsSpan(1)) == hero.NetId).ToList();
        var cast = own.Single(p => p.Bytes[0] == SpellPackets.CastSpellAnswerOpcode).At;
        TestAssert.True(end - cast > 10.0, $"the capture runs long ({end - cast:0.###} s from the cast answer)");
        var inside = own.Where(p => p.At > cast && p.At < end - 1e-6 && p.Bytes[0] is 0xB9 or 0x2B or 0xC2 or 0x65 or 0x39)
            .Select(p => p.Bytes[0] switch
            {
                0xB9 => $"{System.Text.Encoding.ASCII.GetString(p.Bytes, 13, Array.IndexOf(p.Bytes, (byte)0, 13) - 13)}:0x{BinaryPrimitives.ReadUInt32LittleEndian(p.Bytes.AsSpan(5)):X}",
                0x2B => "unlock",
                _ => $"0x{p.Bytes[0]:X2} at +{p.At - cast:0.###}",
            }).ToList();
        TestAssert.True(inside.SequenceEqual(["Channel_WNDUP:0x1", "unlock", "Channel:0x1"]),
            $"the two capture clips are sent looped (0x1) and nothing cuts them before the channel's end ({string.Join(", ", inside)})");
    }
}
