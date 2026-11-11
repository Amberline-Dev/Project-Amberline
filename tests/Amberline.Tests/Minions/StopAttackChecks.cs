using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (17 Oct 2026): a lane minion's stop-attack (0x39) is decided in one place, <c>MinionAiSystem.StopAttack</c>. The
/// stock client keeps a unit in its attack state from the unit's 0x0D until a 0x39, so a stop is sent exactly while the unit is
/// armed (<see cref="Entity.ClientAttackArmed"/>), and its flags follow what the server did with the hit: a swing dropped in its
/// windup sends DestroyMissile alone (0x10, no phantom hit); a hit the server still applies is left alone (0x00, no
/// DestroyMissile on a shot in flight). These run the production composition and read the delivered bytes.
/// </summary>
public static class StopAttackChecks
{
    private const byte BasicAttackOpcode = 0x0D;
    private const byte StopAttackOpcode = 0x39;

    [Check("minions.n2-stop-attack-needs-an-attack-the-client-still-has")]
    public static void AUnitThatNeverAttackedIsNotToldToStop()
    {
        using var rig = new Rig();
        var minion = rig.Minion(new(7000, 7000));
        var chased = rig.Enemy(new(7600, 7000));
        var closer = rig.Enemy(new(7200, 7000));
        minion.AttackTarget = chased.NetId;
        minion.MinionState = MinionAiState.Engage;
        rig.Flush();

        // Chasing a target out of reach and switching to a nearer one: no 0x0D was ever sent, so the client has no attack.
        rig.Runtime.Minions!.FindTargetOrMove(minion);
        rig.Flush();
        TestAssert.Equal(closer.NetId, minion.AttackTarget ?? 0, "the chaser takes the nearer enemy");
        TestAssert.Equal(0, rig.Packets(StopAttackOpcode, minion).Count, "an idle chase retarget sends no stop-attack");
        TestAssert.False(minion.ClientAttackArmed, "it never attacked");

        // Once it has attacked, the same retarget (to a unit attacking an allied champion, which is taken at once) must stop it.
        var helper = rig.Enemy(new(7000, 7150));
        helper.AttackTarget = rig.Hero.NetId;
        TestAssert.True(rig.Runtime.Combat.QueueBasicAttack(minion, closer), "the attack starts");
        TestAssert.True(minion.ClientAttackArmed, "a 0x0D arms the client's attack state");
        rig.Retarget(minion, helper);
        TestAssert.Equal(1, rig.Packets(StopAttackOpcode, minion).Count, "an attacker that retargets is stopped once");
    }

    [Check("minions.n2-stop-attack-mid-windup-drops-the-swing-without-a-phantom-hit")]
    public static void AStopInsideTheWindupDestroysOnlyTheMissile()
    {
        foreach (var missileSpeed in new[] { 0.0, 650.0 })
        {
            using var rig = new Rig();
            var minion = rig.Minion(new(7000, 7000));
            minion.MissileSpeed = missileSpeed;
            var target = rig.Enemy(new(7100, 7000));
            var helper = rig.Enemy(new(7000, 7150));
            helper.AttackTarget = rig.Hero.NetId;
            minion.AttackTarget = target.NetId;
            minion.MinionState = MinionAiState.Engage;
            minion.MinionAutoAttackEnabled = true;
            rig.Flush();

            TestAssert.True(rig.Runtime.Combat.QueueBasicAttack(minion, target), "the swing starts");
            rig.Runtime.Combat.CombatTick(0.05, regenerate: false);
            TestAssert.False(rig.Runtime.Combat.LastAutoAttackFinished(minion), "0.05 s is inside the windup");
            rig.Flush();
            TestAssert.Equal(1, rig.Packets(BasicAttackOpcode, minion).Count, "the swing reached the client");

            rig.Retarget(minion, helper);
            var stops = rig.Packets(StopAttackOpcode, minion);
            TestAssert.Equal(1, stops.Count, $"speed {missileSpeed}: one stop-attack");
            TestAssert.Equal(CombatPackets.CancelWindupFlags, stops[0],
                $"speed {missileSpeed}: the dropped swing sends DestroyMissile alone: no ForceSpellCast, which would play the hit it never lands");
            TestAssert.False(rig.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId && hit.TargetNetId == target.NetId),
                $"speed {missileSpeed}: the server dropped the swing too");
            TestAssert.False(minion.ClientAttackArmed, "the client's attack state ended");
            var health = target.Health;
            for (var tick = 0; tick < 20; tick++) rig.Runtime.Combat.CombatTick(0.1, regenerate: false);
            TestAssert.Equal(health, target.Health, $"speed {missileSpeed}: a dropped swing never lands");
        }
    }

    [Check("minions.n2-stop-attack-after-the-hit-leaves-the-shot-and-sends-once")]
    public static void AStopAfterTheWindupLeavesEveryHitTheServerApplies()
    {
        foreach (var missileSpeed in new[] { 0.0, 650.0 })
        {
            using var rig = new Rig();
            var minion = rig.Minion(new(7000, 7000));
            minion.MissileSpeed = missileSpeed;
            var target = rig.Enemy(new(7100, 7000));
            var helper = rig.Enemy(new(7000, 7150));
            helper.AttackTarget = rig.Hero.NetId;
            minion.AttackTarget = target.NetId;
            minion.MinionState = MinionAiState.Engage;
            minion.MinionAutoAttackEnabled = true;
            rig.Flush();

            var start = target.Health;
            TestAssert.True(rig.Runtime.Combat.QueueBasicAttack(minion, target), "the attack starts");
            rig.Runtime.Combat.CombatTick(minion.AttackWindup + 0.01, regenerate: false);
            TestAssert.True(rig.Runtime.Combat.LastAutoAttackFinished(minion), "the windup is over: the hit is launched");
            var launched = rig.Runtime.Combat.PendingHits.Count(hit => hit.SourceNetId == minion.NetId && hit.TargetNetId == target.NetId);
            // Batch 741: a melee hit lands at its cast frame, the windup's end this tick crossed (the client's cast builder runs a CastType 0
            // effect at once); only the missile is still to land. The stop below must leave both.
            TestAssert.Equal(missileSpeed > 0.0 ? 1 : 0, launched, "only the missile is still to land");
            TestAssert.Equal(missileSpeed <= 0.0, target.Health < start, "the melee hit landed at its cast frame");
            rig.Flush();

            rig.Retarget(minion, helper);
            var stops = rig.Packets(StopAttackOpcode, minion);
            TestAssert.Equal(1, stops.Count, $"speed {missileSpeed}: the client's attack state still has to end");
            TestAssert.Equal(CombatPackets.StandingSwingFlags, stops[0],
                $"speed {missileSpeed}: the hit stands: the swing's cast frame is forced if the client has not reached it, and the shot is not destroyed");
            TestAssert.Equal(launched, rig.Runtime.Combat.PendingHits.Count(hit => hit.SourceNetId == minion.NetId && hit.TargetNetId == target.NetId),
                $"speed {missileSpeed}: stopping the attack does not cancel a launched hit (retail missiles land)");

            // A second stop for the same attack finds the client already stopped (HaltAI is TurnOffAutoAttack).
            rig.Runtime.Minions!.HaltAI(minion);
            rig.Flush();
            TestAssert.Equal(1, rig.Packets(StopAttackOpcode, minion).Count, $"speed {missileSpeed}: a stopped unit is not stopped again");

            var health = target.Health;
            for (var tick = 0; tick < 10; tick++) rig.Runtime.Combat.CombatTick(0.1, regenerate: false);
            TestAssert.Equal(missileSpeed > 0.0, target.Health < health, $"speed {missileSpeed}: the launched missile lands after the stop");
            TestAssert.True(target.Health < start, $"speed {missileSpeed}: the hit the server applied stands");
        }
    }

    [Check("minions.n2-stop-attack-range-leash-cancel-is-a-dropped-swing")]
    public static void ATargetLeavingTheCommittedRangeDropsTheSwingWithoutAPhantomHit()
    {
        // The combat tick drops a swing whose target walked out of the committed range (Map1 ar_StopAttackRangeModifier) and
        // tells the client through the shared effect sink: a lane minion's swing is dropped like a champion's, so 0x10.
        using var rig = new Rig();
        var minion = rig.Minion(new(7000, 7000));
        var target = rig.Enemy(new(7100, 7000));
        minion.AttackTarget = target.NetId;
        minion.MinionState = MinionAiState.Engage;
        minion.MinionAutoAttackEnabled = true;
        rig.Flush();

        TestAssert.True(rig.Runtime.Combat.QueueBasicAttack(minion, target), "the swing starts");
        rig.Runtime.Combat.CombatTick(0.05, regenerate: false);
        target.Position = new(7900, 7000);
        rig.Runtime.Combat.CombatTick(0.05, regenerate: false);
        rig.Flush();
        var stops = rig.Packets(StopAttackOpcode, minion);
        TestAssert.Equal(1, stops.Count, "the dropped swing is announced once");
        TestAssert.Equal(CombatPackets.CancelWindupFlags, stops[0], "no cast frame for a swing that never lands");
        TestAssert.False(rig.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId), "the server dropped it");
        TestAssert.False(minion.ClientAttackArmed, "the stop ended the client's attack state, so the AI sends no second one");
    }

    private sealed class Rig : IDisposable
    {
        private readonly Recording _transport = new();
        public MatchRuntime Runtime { get; }
        public Entity Hero { get; }

        public Rig()
        {
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0.0, minionFidelity: true);
            Runtime.World.Clock.SetVirtual(0.0);
            Runtime.Session.CreateWorld();
            TestAssert.True(Runtime.Session.Authenticate(1, Runtime.Session.BuildKeyCheck(1)) is not null, "observer authentication");
            Runtime.Session.Spawn(1);
            TestAssert.True(Runtime.Session.MarkReady(1), "observer ready");
            TestAssert.True(Runtime.Session.Start(), "session start");
            var participant = Runtime.Session.Participants.Single();
            Hero = participant.Hero ?? throw new InvalidOperationException("observer has no hero");
            Runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order, Hero.NetId, _transport, ready: true, view: participant.View));
            Runtime.Waves!.AutomaticSpawningEnabled = false;
            Runtime.Delivery.Flush();
        }

        public Entity Minion(Position2 position) => Place(Runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0), position);

        public Entity Enemy(Position2 position) => Place(
            Runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, Runtime.World.Entities.Count, "C", 0.0, 0.0), position);

        private static Entity Place(Entity minion, Position2 position)
        {
            minion.Position = position;
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.LaneWaypoints.Clear();
            minion.AnchorPath = [position];
            minion.Health = minion.MaxHealth = 1_000_000.0;
            return minion;
        }

        /// <summary>The minion takes up <paramref name="helper"/>, a unit attacking an allied champion, at the next AI step.</summary>
        public void Retarget(Entity minion, Entity helper)
        {
            minion.HelpTarget = helper.NetId;
            minion.HelpRemaining = 1.0;
            minion.HelpPriority = 0;
            Runtime.Minions!.Tick(0.0);
            TestAssert.Equal(helper.NetId, minion.AttackTarget ?? 0, "the minion switched to the helper's target");
            Flush();
        }

        public void Flush() => Runtime.Delivery.Flush();

        /// <summary>The delivered packets with this opcode about <paramref name="unit"/>: the flags of a 0x39, 0 for others.</summary>
        public List<byte> Packets(byte opcode, Entity unit) => _transport.Packets
            .Where(packet => packet.Plaintext.Span[0] == opcode &&
                             BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == unit.NetId)
            .Select(packet => opcode == StopAttackOpcode ? packet.Plaintext.Span[5] : (byte)0)
            .ToList();

        public void Dispose() => Runtime.Dispose();

        private sealed class Recording : IReplicationTransport
        {
            public List<PlaintextPacket> Packets { get; } = [];
            public void Send(PlaintextPacket packet) => Packets.Add(packet);
        }
    }
}
