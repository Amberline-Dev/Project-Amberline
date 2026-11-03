using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;

namespace Amberline.Tests.Network;

/// <summary>The client's 0x08 answer to a 0xC9 (SynchSimTime) and its 0xC6, which precedes (and releases) the spawn burst.</summary>
public static class ClockSyncAndEarlyReadyChecks
{
    [Check("network.early-world-ready-is-kept")]
    public static void AWorldReadyReportBeforeTheSpawnBurstIsNotLost()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        session.Receive(1, 1, [0xC5]);
        var seat = session.Participant(1)!;
        TestAssert.False(seat.Spawned, "the spawn burst waits for the client's 0xC6");

        // The client sends 0xC6 once, when it has loaded; the burst follows it at once (the 3 s spacing after the handshake was deleted).
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        TestAssert.True(seat.Loaded, "an early 0xC6 is recorded");
        TestAssert.False(seat.Ready, "the seat is not ready before it has spawned");
        session.RunSchedulers();
        TestAssert.True(seat.Spawned && seat.Loaded && !seat.Ready,
            "the seat spawns on the first pass after its 0xC6 and is made ready on the next one");
        session.RunSchedulers();
        TestAssert.True(seat.Spawned && seat.Loaded && seat.Ready, "the seat is ready on the pass after the one that spawned it");

        // The map load is not over until the client says so: the delay alone never releases the hero burst
        // (a CreateHero that reaches the client mid-load faults it).
        using var silent = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var silentSession = silent.Session;
        silent.World.Clock.SetVirtual(0.0);
        silentSession.Receive(1, 0, silentSession.BuildKeyCheck(1));
        silentSession.Receive(1, 1, [0xC5]);
        var silentSeat = silentSession.Participant(1)!;
        silent.World.Clock.SetVirtual(silentSeat.SpawnAt!.Value);
        var withoutReport = silentSession.RunSchedulers();
        TestAssert.False(silentSeat.Spawned, "no 0xC6 yet: the spawn delay alone does not spawn the seat");
        TestAssert.False(withoutReport.Any(item => item.Packet.Plaintext.Span[0] == 0x4F),
            "no hero is created for a client that has not reported its map loaded");
        silent.World.Clock.SetVirtual(silentSeat.SpawnAt!.Value + 30.0);
        silentSession.RunSchedulers();
        TestAssert.False(silentSeat.Spawned, "waiting longer does not release the burst either");
        silentSession.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        TestAssert.False(silentSeat.Spawned, "the report itself sends nothing; the next scheduler pass does");
        var released = silentSession.RunSchedulers();
        TestAssert.True(silentSeat.Spawned && !silentSeat.Ready &&
            released.Any(item => item.Packet.Plaintext.Span[0] == 0x4F),
            "the burst follows the 0xC6");
    }

    [Check("network.clock-sync-answers-the-client-reply")]
    public static void TheClientClockReplyIsAnsweredWithSyncSimTimeFinal()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        session.Receive(1, 1, [0xC5]);
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        var reply = new byte[49];
        reply[0] = 0x08;
        BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(9), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(13), 1.0f);
        // The stock client encrypts its channel-2 packets like the others (the 49 bytes: whole blocks, then the tail in the clear).
        // LegacyBlowfishCipher.Encrypt pads to whole blocks (56 bytes), so only the first 48 bytes go through it (batch 67 never ran this).
        byte[] Wire() => [.. runtime.Cipher.Encrypt(reply.AsSpan(0, 48)), .. reply.AsSpan(48)];
        TestAssert.Equal(0, session.Receive(1, 2, Wire()).Count, "no answer before the match has started");

        runtime.World.Clock.SetVirtual(session.Participant(1)!.SpawnAt!.Value);
        session.RunSchedulers(); // The spawn burst.
        session.RunSchedulers(); // Ready, then the start (no settle time).
        TestAssert.True(session.Started, "the match has started");

        var answer = session.Receive(1, 2, Wire()).Single().Packet.Plaintext.Span;
        TestAssert.Equal(17, answer.Length);
        TestAssert.Equal((byte)0x79, answer[0]);
        TestAssert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(answer[5..]), "the client time it sent comes back");
        TestAssert.True(BinaryPrimitives.ReadSingleLittleEndian(answer[9..]) >= 0.0f, "a round trip, never negative");

        reply[0] = 0x09;
        TestAssert.Equal(0, session.Receive(1, 2, Wire()).Count, "only the 0x08 reply is answered");
    }

    [Check("network.sync-sim-time-final-layout")]
    public static void SyncSimTimeFinalIsTheClientsSeventeenBytes()
    {
        var bytes = SpawnPackets.SyncSimTimeFinal(2.5f, 0.25f).Plaintext.Span;
        TestAssert.Equal(17, bytes.Length);
        TestAssert.Equal((byte)0x79, bytes[0]);
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
        TestAssert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes[5..]));
        TestAssert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]));
        TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[13..]));
    }
}
