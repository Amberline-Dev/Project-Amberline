using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-targeted-active-unit-filters")]
    public static void TargetedActivesRejectNonChampionsWithoutSideEffects()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            foreach (var item in new[] { 3123, 3128, 3144, 3146 })
            {
                hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
                foreach (var (kind, type) in new[] { (EntityKind.Minion, UnitType.Minion),
                             (EntityKind.Pet, UnitType.Minion), (EntityKind.Neutral, UnitType.Monster),
                             (EntityKind.Ward, UnitType.Minion), (EntityKind.Turret, UnitType.Turret),
                             (EntityKind.Inhibitor, UnitType.Structure), (EntityKind.Nexus, UnitType.Structure) })
                {
                    var target = new Entity(match.Runtime.World.Allocate(type), kind, type,
                        enemy.Team, enemy.Position) { Health = 1000, MaxHealth = 1000 };
                    match.Runtime.World.Register(target);
                    Reject(target);
                    match.Runtime.World.Remove(target.NetId);
                }
                Reject(match.Hero(2));
                Reject(hero);
                enemy.Alive = false;
                Reject(enemy);
                enemy.Alive = true;

                void Reject(Entity target)
                {
                    match.Clear();
                    var oldHp = target.Health;
                    var packet = new byte[34]; packet[0] = 0xA2; packet[5] = 8;
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(30), target.NetId);
                    foreach (var offset in new[] { 6, 18 })
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), (float)target.Position.X);
                        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset + 8), (float)target.Position.Z);
                    }
                    TestAssert.False(match.Runtime.Spells.HandleCastRequest(hero, packet, 0),
                        $"{item} rejects {target.Kind}/{target.UnitType}");
                    TestAssert.Near(0, hero.ItemCooldowns[0], 0);
                    TestAssert.Near(oldHp, target.Health, 0);
                    match.Runtime.Delivery.Flush();
                    TestAssert.False(match.Captures.SelectMany(c => c.Packets).Any(p =>
                        Opcode(p) is 0xBD or 0x8A or 0x68 or 0xBF), "rejected casts have no gameplay packets");
                }
            }
        }
    }

    [Check("items-live-cutlass-gunblade-shared-slow")]
    public static void CutlassAndGunbladeRefreshTheSameUnmitigatedSlow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var first in new[] { 3144, 3146 })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(first), new InventorySlot(first == 3144 ? 3146 : 3144), null, null, null, null];
            enemy.Hardness = .75;
            enemy.MaxHealth = enemy.Health = 10000;
            match.Cast(0, 4, enemy);
            var firstFlight = first == 3146 ? AdvanceGunbladeToRetirement(match) : 0.0;
            TestAssert.Near(3, enemy.Buffs.Single(b => b.Name == "BilgewaterCutlass").ExpiresAt, 0);
            match.Runtime.Buffs.TickBuffs(enemy, 1);
            enemy.Hardness = .75;
            enemy.MaxHealth = enemy.Health = 10000;
            match.Cast(0, 5, enemy);
            var secondFlight = first == 3144 ? AdvanceGunbladeToRetirement(match) : 0.0;
            var slow = enemy.Buffs.Single(b => b.Type == BuffType.Slow);
            TestAssert.Equal("BilgewaterCutlass", slow.Name);
            TestAssert.Near(3, slow.ExpiresAt, 0);
            TestAssert.Near(-.5, slow.Payload.MoveSpeedMult, 0);
            TestAssert.Near(60 - firstFlight - secondFlight, hero.ItemCooldowns[0], 1e-9);
            TestAssert.Near(60 - secondFlight, hero.ItemCooldowns[1], 1e-9);
            match.Runtime.Buffs.TickBuffs(enemy, 2.99);
            TestAssert.True(enemy.Buffs.Contains(slow));
            match.Runtime.Buffs.TickBuffs(enemy, .02);
            TestAssert.False(enemy.Buffs.Contains(slow));
        }
    }

    [Check("items-live-cutlass-gunblade-flat-damage")]
    public static void CutlassAndGunbladeDamageDoesNotScaleWithCasterStats()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (item, expectedDamage) in new[] { (3144, 150.0), (3146, 300.0) })
        foreach (var (attackDamage, abilityPower) in new[] { (0.0, 0.0), (250.0, 900.0) })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
            hero.AttackDamage = attackDamage;
            hero.AbilityPower = abilityPower;
            // Keep the target at its authoritative spawned HP/MR. A targeted
            // active's shared slow refreshes hero stats, so synthetic max-HP
            // values would be clamped back to the shipped champion maximum.
            match.Runtime.HeroStats.Refresh(enemy, push: false);
            enemy.Health = enemy.MaxHealth;
            var initialHealth = enemy.Health;
            var expectedPacketDamage = expectedDamage *
                CombatRules.ResistanceMultiplier(enemy.SpellBlock);
            match.Clear();

            match.Cast(0, 4, enemy);
            var flightTime = item == 3146 ? AdvanceGunbladeToRetirement(match) : 0.0;
            match.Runtime.Delivery.Flush();
            TestAssert.Near(initialHealth - expectedPacketDamage, enemy.Health, 1e-6,
                $"{item} damage is flat at AD={attackDamage}, AP={abilityPower}");
            TestAssert.Near(60.0 - flightTime, hero.ItemCooldowns[0], 1e-9,
                $"{item} keeps its sixty-second cooldown, less simulated flight time");

            var damagePackets = match.Captures.SelectMany(capture => capture.Packets)
                .Where(packet => Opcode(packet) == 0x68)
                .Select(Decode)
                .Where(fields => fields.GetProperty("target_netid").GetUInt32() == enemy.NetId)
                .ToArray();
            TestAssert.True(damagePackets.Length > 0, $"{item} emits a damage packet");
            foreach (var fields in damagePackets)
                TestAssert.Near((float)expectedPacketDamage, fields.GetProperty("damage").GetDouble(), 0,
                    $"{item} wire damage is flat at AD={attackDamage}, AP={abilityPower}");

            var slow = enemy.Buffs.Single(buff => buff.Type == BuffType.Slow);
            TestAssert.Equal("BilgewaterCutlass", slow.Name, $"{item} keeps shared slow name");
            TestAssert.Near(-0.5, slow.Payload.MoveSpeedMult, 0,
                $"{item} keeps shared slow strength");
        }
    }

    [Check("items-live-executioner-active-is-separate-from-bleed")]
    public static void ExecutionerActiveDoesNotStartOrRefreshItsOnHitBleed()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var existingBleed in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3123), new InventorySlot(3123), null, null, null, null];
            BuffState? bleed = null;
            if (existingBleed)
                bleed = match.Runtime.Buffs.AddBuff(enemy, "Mourning", BuffType.DamageOverTime, 2,
                    hero.NetId, new BuffPayload { Period = 1, PeriodElapsed = .25, DamagePerTick = 4 });
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.Runtime.HeroStats.Refresh(enemy);
            var oldHp = enemy.Health;
            match.Cast(0, 4, enemy);
            TestAssert.Near(oldHp, enemy.Health, 0);
            TestAssert.Equal(existingBleed ? 1 : 0, enemy.Buffs.Count(b => b.Name == "Mourning"));
            if (bleed is not null)
            {
                TestAssert.True(enemy.Buffs.Contains(bleed), "active leaves the existing bleed instance intact");
                TestAssert.Near(2, bleed.ExpiresAt, 0);
                TestAssert.Near(.25, bleed.Payload.PeriodElapsed, 0);
            }
            TestAssert.Near(8, enemy.Buffs.Single(b => b.Name == "GrievousWound").ExpiresAt, 0);
            TestAssert.Near(.5, match.Runtime.Combat.HealingReceivedMultiplier(enemy), 0);
            TestAssert.Near(20, hero.ItemCooldowns[0], 0);
            TestAssert.Near(20, hero.ItemCooldowns[1], 0);
            foreach (var name in new[] { "executionersCalling_cas.troy", "executionersCalling_tar.troy" })
                foreach (var capture in match.Captures)
                    TestAssert.True(capture.Packets.Any(p => Opcode(p) == 0x8C &&
                        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name[..^5])));
        }
    }
}
