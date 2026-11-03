using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat-live-turret-help-target-change-notifies-both-peers")]
    public static void TurretRetaliationPublishesItsActualTargetChange()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var victim = match.Hero(1);
            var turret = AddTower(match, victim.Team, victim.Position + new Position2(0, 200));
            turret.Created = true;
            turret.AttackRange = 800;
            turret.AttackDamage = 100;
            turret.AttackPeriod = 1;
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team,
                turret.Position + new Position2(100, 0))
            { Alive = true, Created = true, Speed = 0, Radius = 35,
                Health = 500, MaxHealth = 500 });
            runtime.Delivery.Flush(); match.Clear();
            turret.AttackTarget = minion.NetId;
            TestAssert.True(runtime.Combat.QueueBasicAttack(turret, minion),
                $"{team}: turret committed its previous minion shot");
            var committed = runtime.Combat.PendingHits.Single(hit =>
                hit.SourceNetId == turret.NetId && hit.TargetNetId == minion.NetId);
            runtime.Delivery.Flush(); match.Clear();

            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId,
                10, DamageType.True, DamageSourceKind.Basic),
                $"{team}: hero hit triggers turret help");
            TestAssert.Equal((uint?)attacker.NetId, turret.AttackTarget,
                $"{team}: help immediately replaces the turret's selected target");
            TestAssert.True(runtime.Combat.PendingHits.Contains(committed),
                $"{team}: target switch preserves the already launched minion shot");
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var markers = capture.Packets.Where(packet =>
                    (Opcode(packet) is 0x6D or 0xC8) &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == turret.NetId)
                    .ToArray();
                TestAssert.Equal(new byte[] { 0x6D, 0xC8 },
                    markers.Select(Opcode).ToArray(),
                    $"{team}: both peers receive the paired target change once");
                foreach (var marker in markers)
                    TestAssert.Equal(attacker.NetId,
                        BinaryPrimitives.ReadUInt32LittleEndian(marker.Plaintext.Span[5..]),
                        $"{team}: retaliation marker names the new hero target");
            }

            match.Clear();
            victim.HelpCallRemaining = 0;
            runtime.Combat.ApplyHit(attacker.NetId, victim.NetId,
                10, DamageType.True, DamageSourceKind.Basic);
            runtime.Delivery.Flush();
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                (Opcode(packet) is 0x6D or 0xC8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == turret.NetId),
                $"{team}: repeated help for the same target emits no duplicate pair");
        }
    }
}
