using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>
/// Reconnect to a running match (owner playtest of 15 October 2026, restart about 25 minutes in): the
/// restarted client's HUD clock restarted at 0:00 and it raised a "turret destroyed" announcement.
/// </summary>
public static partial class P1NetworkChecks
{
    private const long ReturningConnection = 15;

    /// <summary>
    /// Order seat disconnects, a new transport authenticates and finishes loading. Returns the packets
    /// the scheduler sends once native readiness arrives.
    /// </summary>
    private static IReadOnlyList<PlaintextPacket> ReconnectOrderSeat(MatchRuntime runtime)
    {
        var session = runtime.Session;
        TestAssert.True(session.Disconnect(11));
        session.Receive(ReturningConnection, 0, session.BuildKeyCheck(1));
        session.Receive(ReturningConnection, 1, [0xC5]);
        session.Receive(ReturningConnection, 1, [0xC6, 0, 0, 0, 0]);
        runtime.World.Clock.SetVirtual(session.Participant(ReturningConnection)!.SpawnAt!.Value);
        session.RunSchedulers();
        session.Receive(ReturningConnection, 1, [0xA4, 0, 0, 0, 0, 1]);
        session.Receive(ReturningConnection, 1, [0x55]);
        session.Receive(ReturningConnection, 1, [0xC6, 0, 0, 0, 0]);
        return session.RunSchedulers()
            .Where(item => item.ConnectionId == ReturningConnection)
            .Select(item => item.Packet).ToArray();
    }

    [Check("network.reconnect-hud-clock-origin")]
    public static void ReconnectSetsHudOriginBeforeTheClockSync()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        runtime.Tick(37.5);
        var ready = ReconnectOrderSeat(runtime);

        var origin = ready.Select((packet, index) => (packet, index))
            .Where(item => item.packet.Plaintext.Span[0] == 0xCA).ToArray();
        var clock = ready.Select((packet, index) => (packet, index))
            .Where(item => item.packet.Plaintext.Span[0] == 0xC9).ToArray();
        TestAssert.Equal(1, origin.Length, "one SyncMissionStartTime");
        TestAssert.Equal(1, clock.Length, "one SynchSimTime");
        TestAssert.Equal(9, origin[0].packet.Plaintext.Length);
        // The stock client latches the HUD zero to its own clock on its first game-loop iteration unless
        // 0xCA sets it, and this 0xC9 is processed before that iteration (same burst as StartGame).
        TestAssert.True(origin[0].index < clock[0].index, "the origin precedes the clock sync");
        var startTime = BitConverter.ToSingle(origin[0].packet.Plaintext.Span[5..]);
        var simTime = BitConverter.ToSingle(clock[0].packet.Plaintext.Span[5..]);
        var elapsed = session.PlaytestHudTime!.Value;
        TestAssert.True(elapsed > 37.0, "the match really is well into play");
        TestAssert.Equal(0.0f, startTime, "the origin is zero in the clock domain of the 0xC9");

        // The stock client: the origin starts unset, 0xCA sets it, 0xC9 seeds
        // the slave clock's t, the first game-loop iteration latches an unset origin to t,
        // and the HUD shows t - origin.
        double? hudOrigin = null;
        var t = 0.0;
        foreach (var packet in ready)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes[0] == 0xCA) hudOrigin = BitConverter.ToSingle(bytes[5..]);
            else if (bytes[0] == 0xC9) t = BitConverter.ToSingle(bytes[5..]);
        }
        hudOrigin ??= t;
        TestAssert.Near(elapsed, t - hudOrigin.Value, 1e-3, "a reconnect at game time T shows HUD T, not 0:00");
        TestAssert.Near(elapsed, simTime - startTime, 1e-3);
    }

    [Check("network.reconnect-destroyed-turret-silent")]
    public static void ReconnectKillsDestroyedTurretsWithoutAnAnnouncementEvent()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        runtime.Tick(4.25);
        var lane = session.MapEntities.Where(entity => entity.UnitType == UnitType.Turret &&
            !TurretRules.IsFountainTurret(entity)).ToArray();
        var dead = new[] { lane.First(turret => turret.Team == Team.Order), lane.Last(turret => turret.Team == Team.Chaos) };
        foreach (var turret in dead)
        {
            turret.Alive = false;
            turret.Health = 0.0;
        }
        var deadInhibitor = session.MapEntities.First(entity => entity.Kind == EntityKind.Inhibitor);
        deadInhibitor.Alive = false;
        deadInhibitor.Health = 0.0;
        deadInhibitor.RespawnAt = runtime.Combat.CurrentTime + 12.0;

        var ready = ReconnectOrderSeat(runtime);

        // Every lane tower is a client-built obj_AI_Turret that 0xA5 only binds a net ID to, so a destroyed
        // one must still be bound (or the client keeps it standing) and then killed. A native NPC death
        // notifies EVENT_ON_DIE and the client answers with the OnTurretDie announcement, replayed or
        // live (the turret event manager's death handler), so a state restore must kill it with ForceDead (0x1E).
        TestAssert.False(ready.Any(packet => packet.Plaintext.Span[0] is 0xA6 or 0x8E or 0x4A),
            "no NPC death, building death or world event is replayed");
        int IndexOf(byte opcode, uint netId, int idOffset) => Array.FindIndex(ready.ToArray(), packet =>
            packet.Plaintext.Span[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[idOffset..]) == netId);
        foreach (var turret in dead)
        {
            var bind = IndexOf(0xA5, turret.NetId, 5);
            var kill = IndexOf(0x1E, turret.NetId, 1);
            TestAssert.True(bind >= 0, $"destroyed {turret.Name} is still bound");
            TestAssert.True(kill > bind, $"destroyed {turret.Name} is force-killed after its bind");
            TestAssert.Equal(1, ready.Count(packet => packet.Plaintext.Span[0] == 0x1E &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == turret.NetId));
        }
        foreach (var turret in lane.Where(turret => turret.Alive))
            TestAssert.False(ready.Any(packet => packet.Plaintext.Span[0] == 0x1E &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == turret.NetId),
                $"living {turret.Name} is not killed");
        TestAssert.True(ready.Any(packet => packet.Plaintext.Span[0] == 0x2D &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == deadInhibitor.NetId),
            "a dead inhibitor keeps its silent state packet");
    }

    [Check("network.reconnect-turret-protection-resend")]
    public static void ReconnectResendsTurretProtectionAfterTheMapScriptRuns()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        runtime.Tick(4.25);
        var lane = session.MapEntities.Where(entity => entity.UnitType == UnitType.Turret &&
            !TurretRules.IsFountainTurret(entity)).ToArray();
        var gone = lane.First(turret => turret.Team == Team.Chaos);
        gone.Alive = false;
        gone.Health = 0.0;
        foreach (var (turret, index) in lane.Where(turret => turret.Alive).Select((turret, index) => (turret, index)))
            turret.Protected = index % 2 == 1;
        var attackable = lane.Where(turret => turret.Alive && !turret.Protected).ToArray();
        TestAssert.True(attackable.Length > 0 && lane.Any(turret => turret.Alive && turret.Protected),
            "the fixture mixes attackable and protected turrets");

        ReconnectOrderSeat(runtime);
        var readyAt = runtime.World.Clock.Monotonic();
        bool IsFlags(SessionOutboundPacket item) =>
            item.Packet.Plaintext.Length == 31 && item.Packet.Plaintext.Span[0] == 0xCC;

        runtime.World.Clock.SetVirtual(readyAt + 4.0);
        TestAssert.False(session.RunSchedulers().Any(IsFlags),
            "the resend waits until the client's map script has certainly run");
        runtime.World.Clock.SetVirtual(readyAt + 5.5);
        var resent = session.RunSchedulers().Where(IsFlags).ToArray();
        TestAssert.True(resent.All(item => item.ConnectionId == ReturningConnection),
            "only the returning seat is addressed");
        foreach (var turret in lane)
        {
            var packets = resent.Where(item =>
                item.Packet.EntityReferences.Any(reference => reference.NetId == turret.NetId)).ToArray();
            if (!turret.Alive)
            {
                TestAssert.Equal(0, packets.Length, $"destroyed {turret.Name} is not addressed");
                continue;
            }
            TestAssert.Equal(1, packets.Length, $"{turret.Name} is resent once");
            var flags = packets[0].Packet.Plaintext.Span;
            TestAssert.Equal(turret.Protected ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(flags[19..]),
                $"{turret.Name} invulnerable");
            TestAssert.Equal(turret.Protected ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll,
                BinaryPrimitives.ReadUInt32LittleEndian(flags[27..]), $"{turret.Name} targetability");
        }
        runtime.World.Clock.SetVirtual(readyAt + 30.0);
        TestAssert.False(session.RunSchedulers().Any(IsFlags), "the resend happens once");
    }
}
