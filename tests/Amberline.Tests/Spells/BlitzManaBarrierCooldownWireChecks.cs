using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-blitz-mana-barrier-cooldown-wire-lifecycle")]
    public static void BlitzManaBarrierCooldownWireLifecycle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Blitzcrank", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            // Batch 357 (restates batch 181): the cooldown's add, re-entry and removal carry its binding name,
            // (batch 357: BlitzcrankManaBarrierCD has no script, so it must stay off the wire).
            var wireHash = SpellPackets.BuffNameHash("ManaBarrierCooldown");
            var bindingHash = SpellPackets.BuffNameHash("BlitzcrankManaBarrierCD");
            var shieldHash = SpellPackets.BuffNameHash("ManaBarrier");
            bool HasBuffPacket(PlaintextPacket packet, byte opcode, uint hash)
            {
                var raw = packet.Plaintext.Span;
                return raw.Length >= (opcode == 0xBF ? 13 : 10) && raw[0] == opcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(raw[1..]) == hero.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(raw[(opcode == 0xBF ? 9 : 6)..]) == hash;
            }

            hero.Health = hero.MaxHealth * .25;
            hero.Mana = 200;
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 300,
                DamageType.True, DamageSourceKind.Spell), $"{team}: real damage triggers the passive");
            runtime.Delivery.Flush();
            var cooldown = hero.Buffs.Single(buff => buff.Name == "ManaBarrierCooldown");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ManaBarrier"),
                $"{team}: the shield remains as a known snapshot control");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                HasBuffPacket(packet, 0xBF, wireHash)),
                $"{team}: initial add carries the binding name");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                HasBuffPacket(packet, 0xBF, bindingHash)),
                $"{team}: the BuffName BlitzcrankManaBarrierCD is not on the wire");

            match.Clear();
            runtime.Session.Participants[0].View.Reset();
            runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                HasBuffPacket(packet, 0xBF, wireHash)),
                $"{team}: observer re-entry restores the live cooldown buff");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                HasBuffPacket(packet, 0xBF, shieldHash)),
                $"{team}: re-entry also includes the known live shield buff");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                HasBuffPacket(packet, 0xBF, bindingHash)),
                $"{team}: re-entry never emits the binding name");

            match.Clear();
            runtime.Buffs.TickBuffs(hero, cooldown.ExpiresAt + .01);
            // Batch 365 (wire grace): an expiry's remove is held through one step and sent by the second EndTick after it (BuffLifecycle.EndTick).
            runtime.Buffs.EndTick();
            runtime.Buffs.EndTick();
            runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ManaBarrierCooldown"),
                $"{team}: sixty-second cooldown expires authoritatively");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                HasBuffPacket(packet, 0x7F, wireHash)),
                $"{team}: expiry removes the shipped BuffName once");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                HasBuffPacket(packet, 0x7F, bindingHash)),
                $"{team}: removal never uses the binding name");
        }
    }
}
