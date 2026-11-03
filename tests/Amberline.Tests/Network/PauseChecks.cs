using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Diagnostics;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("protocol.pause-stock-layouts")]
    public static void PausePacketsMatchStockLayouts()
    {
        var pause = PausePackets.Pause(0x11223344, 1800);
        var resume = PausePackets.Resume(0x11223344, delayed: true);
        TestAssert.Equal("A900000000443322110807000001", Convert.ToHexString(pause.Plaintext.Span));
        TestAssert.Equal("0A000000004433221101", Convert.ToHexString(resume.Plaintext.Span));
        TestAssert.Equal("0A00000000FFFFFFFF00", Convert.ToHexString(
            PausePackets.Resume(uint.MaxValue, delayed: false).Plaintext.Span));
        var cipher = LegacyBlowfish.CreateDefault();
        var decodedPause = WirePacketDecoder.Decode(pause.Plaintext.Span, pause.Channel);
        TestAssert.Equal("Pause", decodedPause.Name);
        TestAssert.Equal(1800, (int)decodedPause.Fields["seconds_remaining"]!);
        TestAssert.Equal(0x11223344u, (uint)decodedPause.Fields["client_id"]!);
        TestAssert.Equal((byte)1, (byte)decodedPause.Fields["tournament"]!);
        var decodedResume = WirePacketDecoder.Decode(resume.Plaintext.Span, resume.Channel);
        TestAssert.Equal("Resume", decodedResume.Name);
        TestAssert.Equal((byte)1, (byte)decodedResume.Fields["delayed"]!);
        foreach (var packet in new[] { pause, resume })
        {
            TestAssert.Equal((byte)3, packet.Channel);
            TestAssert.True(packet.Encrypt);
            TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
            var padded = cipher.Decrypt(packet.ToWireBytes(cipher));
            TestAssert.Equal(packet.Plaintext.ToArray(), padded[..packet.Plaintext.Length]);
            TestAssert.True(padded.AsSpan(packet.Plaintext.Length).IndexOfAnyExcept((byte)0) < 0);
            TestAssert.Equal(packet.Plaintext.ToArray(),
                cipher.Decrypt(NativeRequestWire.Encrypt(cipher, packet.Plaintext.Span)));
            TestAssert.True(PausePackets.IsRequest(packet.Plaintext.Span));
            TestAssert.False(PausePackets.IsRequest(packet.Plaintext.Span[..^1]));
            TestAssert.False(PausePackets.IsRequest([.. packet.Plaintext.ToArray(), 0]));
            TestAssert.Equal(0, WirePacketDecoder.Decode(packet.Plaintext.Span[..^1], packet.Channel).Fields.Count);
        }
        TestAssert.False(PausePackets.IsRequest([]));
        TestAssert.False(PausePackets.IsRequest(new byte[14]));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => PausePackets.Pause(0, 0));
    }

    [Check("session.pause-freezes-live-match")]
    public static void PauseFreezesMovementDamageBuffsCooldownsAndRespawn()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            var dead = match.Hero(2);
            runtime.Combat.Kill(dead);
            runtime.Combat.ScheduleRespawn(dead);
            hero.SpellCooldowns[0] = 20;
            hero.SummonerCooldowns[0] = 40;
            hero.ItemCooldowns[0] = 30;
            var buff = runtime.Buffs.AddBuff(hero, "PauseProbe", BuffType.Internal, 10, hero.NetId)!;
            match.Move(0, hero.Position + new Position2(0, 300));
            TestAssert.True(hero.Waypoints.Count > 0);
            TestAssert.True(runtime.Combat.QueueBasicAttack(enemy, hero, new AttackRolls(.5, .5, .5)));
            var hits = runtime.Combat.PendingHits.ToArray();
            var position = hero.Position; var route = hero.Waypoints.ToArray();
            var hp = hero.Health; var mana = hero.Mana; var gold = hero.Gold;
            var time = runtime.Combat.CurrentTime; var respawn = dead.RespawnAt;
            var count = runtime.World.Entities.Count;
            var outputs = SendPause(runtime, 1, pause: true);
            TestAssert.Equal(3, outputs.Count);
            TestAssert.True(runtime.Session.Paused);
            runtime.Tick(60);
            runtime.Session.RunSchedulers();
            TestAssert.Equal(time, runtime.Combat.CurrentTime);
            TestAssert.Equal(position, hero.Position);
            TestAssert.Equal(route, hero.Waypoints.ToArray());
            TestAssert.Equal(hits, runtime.Combat.PendingHits.ToArray());
            TestAssert.Equal(hp, hero.Health); TestAssert.Equal(mana, hero.Mana); TestAssert.Equal(gold, hero.Gold);
            TestAssert.Equal(20.0, hero.SpellCooldowns[0]);
            TestAssert.Equal(40.0, hero.SummonerCooldowns[0]);
            TestAssert.Equal(30.0, hero.ItemCooldowns[0]);
            TestAssert.True(hero.Buffs.Contains(buff));
            TestAssert.False(dead.Alive); TestAssert.Equal(respawn, dead.RespawnAt);
            TestAssert.Equal(count, runtime.World.Entities.Count, "pause cannot spawn waves or camps");
            TestAssert.Near(1740, runtime.Session.PauseSecondsRemaining, 1e-9);

            var countdown = SendPause(runtime, 2, pause: false);
            TestAssert.Equal(3, countdown.Count);
            TestAssert.True(countdown.All(output => output.Packet.Plaintext.Span[9] == 1));
            runtime.Tick(4.99); runtime.Session.RunSchedulers();
            TestAssert.True(runtime.Session.Paused);
            TestAssert.Equal(time, runtime.Combat.CurrentTime);
            runtime.Tick(.02);
            var resumed = runtime.Session.RunSchedulers();
            TestAssert.False(runtime.Session.Paused);
            TestAssert.Equal(3, resumed.Count(output => output.Packet.Plaintext.Span[0] == 0x0A &&
                output.Packet.Plaintext.Span[9] == 0));
            runtime.Tick(2);
            TestAssert.True(runtime.Combat.CurrentTime > time && hero.Position != position);
            TestAssert.True(hero.SpellCooldowns[0] < 20 && hero.SummonerCooldowns[0] < 40 && hero.ItemCooldowns[0] < 30);
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.MissileNetId == hits[0].MissileNetId));
            TestAssert.False(dead.Alive, "paused wall time cannot consume the respawn timer");
            runtime.Tick(respawn - runtime.Combat.CurrentTime + .1);
            TestAssert.True(dead.Alive);
            TestAssert.False(hero.Buffs.Contains(buff));
        }
    }

    [Check("session.pause-input-and-budget")]
    public static void PauseAuthenticatesInputPreservesChatAndConsumesOneBudget()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime; var session = runtime.Session; var hero = match.Hero(0);
        var request = PausePackets.Pause(uint.MaxValue, 1).Plaintext.ToArray();
        request.AsSpan(1).Fill(0xFF);
        TestAssert.Equal(0, session.Receive(999, 1, NativeRequestWire.Encrypt(runtime.Cipher, request)).Count);
        TestAssert.Equal(0, session.Receive(1, 2, request).Count);
        TestAssert.Equal(0, session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher, request[..^1])).Count);
        TestAssert.False(session.Paused);
        session.Participants[0].Ready = false;
        TestAssert.Equal(0, session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher, request)).Count);
        session.Participants[0].Ready = true;
        var accepted = session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher, request));
        TestAssert.Equal(2, accepted.Count);
        foreach (var output in accepted)
        {
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(output.Packet.Plaintext.Span[5..]));
            TestAssert.Equal(1800, BinaryPrimitives.ReadInt32LittleEndian(output.Packet.Plaintext.Span[9..]));
        }
        var mana = hero.Mana; var gold = hero.Gold; var destination = hero.Position;
        match.Move(0, destination + new Position2(200, 200));
        TestAssert.True(hero.Target is null && hero.Waypoints.Count == 0);
        var invoked = false;
        var cast = new byte[34]; cast[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(1), hero.NetId);
        session.Receive(1, 1, runtime.Cipher.Encrypt(cast), _ => invoked = true);
        TestAssert.False(invoked); TestAssert.Equal(mana, hero.Mana);
        var purchase = new byte[9]; purchase[0] = 0x87;
        BinaryPrimitives.WriteUInt32LittleEndian(purchase.AsSpan(5), 1001);
        session.Receive(1, 1, runtime.Cipher.Encrypt(purchase));
        session.Receive(1, 1, new byte[] { 0xAC, 0, 0, 0, 0, 1 });
        TestAssert.Equal(gold, hero.Gold); TestAssert.False(runtime.Combat.GameOver);
        var chat = ChatPackets.Envelope(999, false, "hello"u8);
        var relayed = session.Receive(1, 5, runtime.Cipher.Encrypt(chat.Plaintext.Span));
        TestAssert.Equal(2, relayed.Count, "ordinary chat remains available");
        var cheat = ChatPackets.Envelope(999, false, "/gold"u8);
        TestAssert.Equal(1, session.Receive(1, 5, runtime.Cipher.Encrypt(cheat.Plaintext.Span)).Count);
        TestAssert.Equal(gold, hero.Gold, "chat cheats cannot mutate a paused match");
        runtime.Tick(10);
        TestAssert.Equal(0, SendPause(runtime, 2, pause: true).Count, "duplicate pause cannot refill the budget");
        SendPause(runtime, 2, pause: false);
        runtime.Tick(3);
        TestAssert.Equal(0, SendPause(runtime, 1, pause: false).Count, "duplicate resume cannot restart countdown");
        runtime.Tick(2); session.RunSchedulers();
        TestAssert.False(session.Paused);
        TestAssert.Near(1785, session.PauseSecondsRemaining, 1e-9);
        SendPause(runtime, 1, pause: true);
        runtime.Tick(1785); var expiry = session.RunSchedulers();
        TestAssert.False(session.Paused); TestAssert.Equal(0.0, session.PauseSecondsRemaining);
        TestAssert.Equal(2, expiry.Count(output => output.Packet.Plaintext.Span[0] == 0x0A));
        TestAssert.Equal(0, SendPause(runtime, 1, pause: true).Count);
        TestAssert.False(session.RunSchedulers().Any(output => output.Packet.Plaintext.Span[0] == 0x0A));
    }

    [Check("network.pause-reconnect", Serial = true)]
    public static void PausedReconnectLoadsBeforePauseUiAndReceivesFinalResume()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var alreadyResuming in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime; var session = runtime.Session;
            TestAssert.Equal(2, SendPause(runtime, 1, pause: true).Count);
            if (alreadyResuming) SendPause(runtime, 2, pause: false);
            session.Disconnect(1); session.Disconnect(2);
            foreach (var observer in runtime.Delivery.Observers.ToArray())
                runtime.Delivery.RemoveObserver(observer.ObserverId);
            using var network = new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true);
            using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var packets = new List<byte[]>();
            var seat = session.Participants[0];
            var peer = client.Connect("127.0.0.1", network.Port, 8);
            Wait(() => network.ConnectionIds.Count == 1);
            peer.Send(0, session.BuildKeyCheck(seat.PlayerId), LeagueEnetPacketFlags.Reliable);
            client.Flush(); Wait(() => seat.ConnectionId is not null);
            TestAssert.False(packets.Any(packet => packet[0] == 0xA9));
            peer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            client.Flush(); Wait(() => seat.Loaded);
            Wait(() => seat.Spawned);
            // The check used to set the clock to the seat's SpawnAt, three seconds after its 0xC6, which spent three seconds of the pause budget
            // before the returning client read it. The spawn delay is deleted (batch 84, commit 7dda5681), so the three seconds are advanced here.
            runtime.World.Clock.Advance(3);
            TestAssert.False(packets.Any(packet => packet[0] == 0xA9));
            peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            client.Flush(); Wait(() => packets.Any(packet => packet[0] == 0xA9));
            var pauseIndex = packets.FindIndex(packet => packet[0] == 0xA9);
            var rankIndex = packets.FindIndex(packet => packet[0] == 0x18 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == seat.Hero!.NetId);
            TestAssert.True(rankIndex >= 0 && pauseIndex > rankIndex, "pause UI follows hero/spell snapshots");
            TestAssert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packets[pauseIndex].AsSpan(5)));
            TestAssert.Equal(1797, BinaryPrimitives.ReadInt32LittleEndian(packets[pauseIndex].AsSpan(9)));
            var time = runtime.Combat.CurrentTime;
            if (!alreadyResuming)
            {
                peer.Send(1, NativeRequestWire.Encrypt(runtime.Cipher, PausePackets.Resume(uint.MaxValue, false).Plaintext.Span),
                    LeagueEnetPacketFlags.Reliable);
                client.Flush(); Wait(() => packets.Any(packet => packet[0] == 0x0A && packet[9] == 1));
            }
            else TestAssert.False(packets.Any(packet => packet[0] == 0x0A), "reconnect cannot restart a pending countdown");
            runtime.Tick(alreadyResuming ? 2 : 5);
            Wait(() => packets.Any(packet => packet[0] == 0x0A && packet[9] == 0));
            TestAssert.False(session.Paused);
            TestAssert.Equal(time, runtime.Combat.CurrentTime);
            TestAssert.Equal(1, packets.Count(packet => packet[0] == 0xA9));

            void Wait(Func<bool> done)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (!done())
                {
                    network.Pump(1, 32);
                    while (true)
                    {
                        var received = client.Service(0);
                        if (received.Type == LeagueEnetEventType.None) break;
                        if (received.Type == LeagueEnetEventType.Receive && received.Channel == 3)
                            packets.Add(received.Payload.Length >= 8 ? runtime.Cipher.Decrypt(received.Payload) : received.Payload);
                    }
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("paused reconnect timeout");
                }
            }
        }
    }

    [Check("session.pause-start-and-terminal-schedulers")]
    public static void PausePreservesInitialProtectionDeadlineAndCannotTrapEndedMatches()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0);
        foreach (var seat in session.Participants)
        {
            var connection = (long)seat.PlayerId;
            session.Authenticate(connection, session.BuildKeyCheck(seat.PlayerId));
            session.Spawn(connection); session.MarkReady(connection);
            seat.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.Equal(0, SendPause(runtime, 1, pause: true).Count, "pause cannot bypass the start barrier");
        session.RunSchedulers();
        TestAssert.True(session.Started);
        TestAssert.Equal(10.0, session.ProtectAt!.Value);
        runtime.Tick(2);
        SendPause(runtime, 1, pause: true);
        runtime.Tick(60); session.RunSchedulers();
        TestAssert.Equal(10.0, session.ProtectAt!.Value, "initial protection cannot engage during a pause");
        SendPause(runtime, 2, pause: false);
        runtime.Tick(5); session.RunSchedulers();
        TestAssert.Equal(75.0, session.ProtectAt!.Value, "protection retains eight unpaused seconds");
        runtime.Tick(7); session.RunSchedulers();
        TestAssert.True(session.ProtectAt.HasValue);
        runtime.Tick(1); session.RunSchedulers();
        TestAssert.False(session.ProtectAt.HasValue);
        SendPause(runtime, 2, pause: true);
        var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == Team.Order);
        runtime.Combat.Kill(nexus);
        TestAssert.True(runtime.Combat.GameOver);
        var ended = session.RunSchedulers();
        TestAssert.False(session.Paused);
        TestAssert.Equal(2, ended.Count(output => output.Packet.Plaintext.Span[0] == 0x0A));
        TestAssert.Equal(0, SendPause(runtime, 1, pause: true).Count);
        runtime.World.Clock.UseReal();
    }

    private static IReadOnlyList<SessionOutboundPacket> SendPause(MatchRuntime runtime, long connection, bool pause)
    {
        var packet = pause ? PausePackets.Pause(uint.MaxValue, 1) : PausePackets.Resume(uint.MaxValue, false);
        return runtime.Session.Receive(connection, 1, NativeRequestWire.Encrypt(runtime.Cipher, packet.Plaintext.Span));
    }

}
