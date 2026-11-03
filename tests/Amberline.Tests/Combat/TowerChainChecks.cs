using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Ai;

namespace Amberline.Tests;

/// <summary>
/// Batch 180 (owner hand test, 24 October 2026): after the first tower of a lane dies, the next tower must become
/// attackable on every client. These checks run the live start of the production match, its 10 s protection timer and
/// real damage, and read what each team's client receives.
/// </summary>
public static class TowerChainChecks
{
    private sealed class WireCapture(long connectionId, List<(long Connection, PlaintextPacket Packet)> sink)
        : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) => sink.Add((connectionId, packet));
    }

    /// <summary>Starts a production match the way the live server does: the client's handshake and loaded packets, then the scheduler.</summary>
    private static (MatchRuntime Runtime, List<(long Connection, PlaintextPacket Packet)> Wire) StartLiveMatch()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault());
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        foreach (var (connectionId, playerId) in new[] { (11L, 1UL), (12L, 2UL) })
        {
            session.Receive(connectionId, 0, session.BuildKeyCheck(playerId));
            session.Receive(connectionId, 1, [0xC5, 0, 0, 0, 0]);
            session.Receive(connectionId, 1, [0xC6, 0, 0, 0, 0]);
        }
        for (var guard = 0; !session.Started && guard < 6000; guard++)
        {
            runtime.World.Clock.Advance(1.0 / 30.0);
            session.RunSchedulers();
        }
        TestAssert.True(session.Started, "the scheduler starts the match");
        var wire = new List<(long Connection, PlaintextPacket Packet)>();
        foreach (var connectionId in new long[] { 11, 12 })
        {
            var participant = session.Participant(connectionId)!;
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team,
                participant.Hero!.NetId, new WireCapture(connectionId, wire), ready: true,
                view: participant.View, clientId: participant.ClientId));
        }
        return (runtime, wire);
    }

    /// <summary>Runs the production loop (Tick, then the scheduler pass) until the protection timer has engaged and its packets have left.</summary>
    private static void EngageProtection(MatchRuntime runtime)
    {
        for (var guard = 0; !runtime.Combat.ProtectionEngaged && guard < 2000; guard++) Advance(runtime, 1.0 / 30.0);
        TestAssert.True(runtime.Combat.ProtectionEngaged, "the scheduler engages the protection ladder at its 10 s timer");
        // The engage packets are queued by the scheduler and leave with the next Delivery.Flush, which runs inside Tick.
        Advance(runtime, 0.2);
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        const double step = 1.0 / 30.0;
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += step)
        {
            runtime.Tick(step);
            runtime.Session.RunSchedulers();
        }
    }

    private static Entity Named(MatchRuntime runtime, string name) =>
        runtime.World.Entities.Values.Single(entity => entity.Name == name);

    /// <summary>
    /// Every replication 0xCC names its unit at byte 11 and carries the unit's sync id at byte 5 (CombatPackets.WriteReplicationPrefix).
    /// The client keeps the highest sync id it accepted per unit, so a state change must carry a higher one than any earlier packet.
    /// </summary>
    private static bool IsReplicationFor(PlaintextPacket packet, uint netId) =>
        packet.Plaintext.Length >= 19 && packet.Plaintext.Span[0] == 0xCC &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == netId;

    private static uint SyncIdOf(PlaintextPacket packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]);

    /// <summary>The protection fields of a turret's 31-byte flags packet or a building's 35-byte stats packet, or null for other packets.</summary>
    private static (uint Invulnerable, uint Targetable, uint SFlags)? ProtectionOf(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span.Length == 31)
            return (BinaryPrimitives.ReadUInt32LittleEndian(span[19..]), BinaryPrimitives.ReadUInt32LittleEndian(span[23..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[27..]));
        if (span.Length == 35)
            return (BinaryPrimitives.ReadUInt32LittleEndian(span[23..]), BinaryPrimitives.ReadUInt32LittleEndian(span[27..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[31..]));
        return null;
    }

    /// <summary>The protection state a seat's client holds for one structure: its last protection packet, or null when none arrived.</summary>
    private static (uint Invulnerable, uint Targetable, uint SFlags)? LastProtection(
        IEnumerable<(long Connection, PlaintextPacket Packet)> wire, long seat, uint netId)
    {
        (uint, uint, uint)? last = null;
        foreach (var (connection, packet) in wire)
            if (connection == seat && IsReplicationFor(packet, netId) && ProtectionOf(packet) is { } state)
                last = state;
        return last;
    }

    /// <summary>
    /// The seat's release of one structure in the wire from <paramref name="from"/> on: its last protection packet must be the
    /// release (invulnerable 0, targetable 1, targetable-to-all flags) and carry a sync id above every earlier replication packet
    /// the seat received for that structure. Returns the failure, or null.
    /// </summary>
    private static string? ReleaseFailure(IReadOnlyList<(long Connection, PlaintextPacket Packet)> wire,
        int from, long seat, Entity structure)
    {
        var index = -1;
        for (var i = from; i < wire.Count; i++)
            if (wire[i].Connection == seat && ProtectionOf(wire[i].Packet) is not null &&
                IsReplicationFor(wire[i].Packet, structure.NetId))
                index = i;
        if (index < 0) return $"seat {seat} received no protection packet for {structure.Name} after its guard died";
        var state = ProtectionOf(wire[index].Packet)!.Value;
        if (state.Invulnerable != 0u || state.Targetable != 1u || state.SFlags != CombatPackets.SFlagTargetableToAll)
            return $"seat {seat}: the last protection of {structure.Name} is invulnerable {state.Invulnerable}, " +
                   $"targetable {state.Targetable}, sflags 0x{state.SFlags:X}";
        var syncId = SyncIdOf(wire[index].Packet);
        var earlier = 0u;
        for (var i = 0; i < index; i++)
            if (wire[i].Connection == seat && IsReplicationFor(wire[i].Packet, structure.NetId))
                earlier = Math.Max(earlier, SyncIdOf(wire[i].Packet));
        return syncId > earlier ? null :
            $"seat {seat}: the release of {structure.Name} has syncId {syncId}, not above the earlier {earlier}";
    }

    [Check("combat.tower-chain-protection-reaches-both-clients")]
    public static void TowerChainProtectionReachesBothClients()
    {
        var (runtime, wire) = StartLiveMatch();
        EngageProtection(runtime);
        var chaos = runtime.World.Heroes().Single(hero => hero.Team == Team.Chaos);
        var attackerView = runtime.Session.Participant(12)!.View;

        // The Order seat always sees its own towers, so the engage protection of every Order inner tower reaches it.
        foreach (var name in new[] { "Turret_T1_L_02", "Turret_T1_C_04", "Turret_T1_R_02" })
        {
            var inner = Named(runtime, name);
            TestAssert.True(inner.Protected, $"{name} is protected by the ladder at engage");
            var held = LastProtection(wire, 11, inner.NetId);
            TestAssert.True(held is { } state && state.Invulnerable == 1u &&
                state.SFlags == CombatPackets.SFlagNontargetableEnemy,
                $"{name}: the Order client holds the engage protection (last: {held})");
        }

        // The attacking hero stands at the outer tower. The inner tower is outside its vision when the outer tower dies.
        var outer = Named(runtime, "Turret_T1_L_03");
        var inner2 = Named(runtime, "Turret_T1_L_02");
        chaos.Position = new Position2(outer.Position.X + 300.0, outer.Position.Z);
        Advance(runtime, 0.5);
        TestAssert.False(attackerView.Visible.Contains(inner2.NetId),
            "the inner tower is outside the attacking hero's vision when the outer tower dies");
        var mark = wire.Count;
        TestAssert.True(runtime.Combat.ApplyHit(chaos.NetId, outer.NetId, 100000.0) && !outer.Alive,
            "a lethal hit kills the outer tower through the damage path");
        // The release is due in the first flush after the death, before the hero moves.
        Advance(runtime, 1.0 / 30.0);
        TestAssert.False(inner2.Protected, "the server releases the inner tower when the outer tower dies");
        foreach (var seat in new long[] { 11, 12 })
        {
            var failure = ReleaseFailure(wire, mark, seat, inner2);
            TestAssert.True(failure is null, failure);
        }

        // The attacker walks up to the released tower: the server accepts the attack order and the damage lands.
        chaos.Position = new Position2(inner2.Position.X + 300.0, inner2.Position.Z);
        Advance(runtime, 0.5);
        TestAssert.True(runtime.Combat.CanAttack(chaos, inner2), "the server accepts the attack order on the inner tower");
        TestAssert.True(runtime.Combat.ApplyHit(chaos.NetId, inner2.NetId, 5.0), "the damage lands on the inner tower");
    }

    [Check("combat.tower-chain-structure-releases-reach-every-client")]
    public static void TowerChainStructureReleasesReachEveryClient()
    {
        var (runtime, wire) = StartLiveMatch();
        EngageProtection(runtime);
        var chaos = runtime.World.Heroes().Single(hero => hero.Team == Team.Chaos);
        var attackerView = runtime.Session.Participant(12)!.View;
        var outer = Named(runtime, "Turret_T1_L_03");
        // The attacking hero stays at the outer tower for the whole chain, so no structure of the chain is in its vision.
        chaos.Position = new Position2(outer.Position.X + 300.0, outer.Position.Z);
        Advance(runtime, 0.5);

        // Each death releases the structures listed after it: the lane chain, the dampener's HQ towers, then the HQ.
        var steps = new (string Kill, string[] Released)[]
        {
            ("Turret_T1_L_03", new[] { "Turret_T1_L_02" }),
            ("Turret_T1_L_02", new[] { "Turret_T1_C_06" }),
            ("Turret_T1_C_06", new[] { "Barracks_T1_L1" }),
            ("Barracks_T1_L1", new[] { "Turret_T1_C_01", "Turret_T1_C_02" }),
            ("Turret_T1_C_01", Array.Empty<string>()),
            ("Turret_T1_C_02", new[] { "HQ_T1" }),
        };
        var failures = new List<string>();
        foreach (var (killName, released) in steps)
        {
            var target = Named(runtime, killName);
            foreach (var name in released)
                if (attackerView.Visible.Contains(Named(runtime, name).NetId))
                    failures.Add($"{name} is in the attacking seat's vision, so the scenario does not hold");
            var mark = wire.Count;
            if (!runtime.Combat.ApplyHit(chaos.NetId, target.NetId, 100000.0) || target.Alive)
            {
                failures.Add($"the lethal hit on {killName} was refused (protected: {target.Protected})");
                break;
            }
            Advance(runtime, 1.0 / 30.0);
            foreach (var name in released)
            {
                var structure = Named(runtime, name);
                if (structure.Protected) failures.Add($"{name} is still protected after {killName} died");
                foreach (var seat in new long[] { 11, 12 })
                    if (ReleaseFailure(wire, mark, seat, structure) is { } failure) failures.Add(failure);
            }
        }
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    [Check("scripting.lane-tower-names-follow-the-tower-chain")]
    public static void LaneTowerNamesFollowTheTowerChain()
    {
        // LevelScriptHost.TurretName answers the shipped script's GetTurret(team, lane, tower); the chain tables
        // are the engine's port of the same rows, so each lane must read FRONT <- MIDDLE <- BACK from them. Batch 545: the
        // answer comes from the map's CreateLevelProps rows, here Summoner's Rift's.
        var rows = MapScriptData.TurretsFor(GameDataPaths.Default.ForMap(GameMaps.Normal));
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var side = team == Team.Order ? "T1" : "T2";
            foreach (var (laneId, lane) in new[] { (0, "R"), (1, "C"), (2, "L") })
            {
                string Name(int tower) => LevelScriptHost.TurretName(rows, team, laneId, tower) ?? "none";
                var front = Name(3);
                var middle = Name(2);
                var back = Name(1);
                var label = $"{team} {lane} lane";
                TestAssert.True(MapDefinitions.TurretTiers.GetValueOrDefault(front) == TurretTier.Outer,
                    $"{label}: FRONT_TOWER names {front}, not an outer tower");
                TestAssert.True(MapDefinitions.TurretTiers.GetValueOrDefault(middle) == TurretTier.Inner,
                    $"{label}: MIDDLE_TOWER names {middle}, not an inner tower");
                TestAssert.Equal(front, MapDefinitions.TurretFront.GetValueOrDefault(middle) ?? "none",
                    $"{label}: the middle tower is in front of the outer tower");
                TestAssert.Equal(middle, MapDefinitions.TurretFront.GetValueOrDefault(back) ?? "none",
                    $"{label}: the back tower is in front of the middle tower");
                TestAssert.Equal($"Barracks_{side}_{lane}1", MapDefinitions.TurretGuardsInhibitor.GetValueOrDefault(back) ?? "none",
                    $"{label}: the back tower guards the inhibitor");
            }
        }
    }
}
