using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // 19 October 2026: "spells-live-annie-pyromania-capacity-counter-and-stun" pinned the hand-written Annie's authoritative Pyromania
    // counter and its display catch-up when the buff table was full; the script engine's passive is the buffs themselves
    // (a stack that cannot be added is lost, like any other buff on a full table), so the check went with the module.

    [Check("spells-live-annie-molten-shield-and-timer-capacity")]
    public static void AnnieSelfBuffsSkipMissingSlotsAndRenewExisting()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            FillAnnieShacoCapacity(hero);
            var mana = hero.Mana;
            var beforePackets = match.Captures[0].Packets.Count;
            match.Cast(0, 2, hero);
            TestAssert.True(hero.Mana < mana && hero.SpellCooldowns[2] > 0,
                "an accepted E still spends its normal cast resources");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "MoltenShield"));
            match.Runtime.Delivery.Flush();
            var spentManaStats = match.Captures[0].Packets.Skip(beforePackets)
                .Where(packet => Opcode(packet) == 0xCC && packet.Plaintext.Length == 171 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == hero.NetId)
                .ToArray();
            TestAssert.True(spentManaStats.Length > 0,
                "omitting a full-table Molten Shield still publishes the committed resource snapshot");
            TestAssert.Near(hero.Mana,
                BinaryPrimitives.ReadSingleLittleEndian(spentManaStats[^1].Plaintext.Span[151..]), 1e-3,
                "the omitted shield's hero stats packet carries the spent mana");
            FreeAnnieShacoCapacity(hero);
            FreeAnnieShacoCapacity(hero);
            hero.SpellCooldowns[2] = 0;
            hero.Mana = hero.MaxMana;
            match.Cast(0, 2, hero);
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "MoltenShield"));
            FillAnnieShacoCapacity(hero);
            hero.SpellCooldowns[2] = 0;
            hero.Mana = hero.MaxMana;
            match.Cast(0, 2, hero);
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "MoltenShield"),
                "same-name renewal remains legal with all 64 slots occupied");

            hero.SpellCooldowns[3] = 0;
            hero.Mana = hero.MaxMana;
            match.CastGround(0, hero.Position + new Position2(100, 0), 3);
            Advance(match, .6);
            TestAssert.True(match.Runtime.World.Entities.Values.Any(unit =>
                unit.Name == "AnnieTibbers" && unit.Alive),
                "missing owner timer cannot abort the already accepted summon");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "InfernalGuardianTimer"));
        }
    }

    [Check("spells-live-shaco-capacity-cast-and-poison-fallback")]
    public static void ShacoCastAdmissionAndPostHitPoisonCapacity()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            foreach (var slot in new[] { 0, 1, 3 })
            {
                using var blocked = new Match("Shaco", team);
                var hero = blocked.Hero(0);
                FillAnnieShacoCapacity(hero);
                var mana = hero.Mana;
                var cooldown = hero.SpellCooldowns[slot];
                // W33: Shaco is a script champion, so a cast at a full 64-slot table follows the rule every script champion follows (the Annie
                // E above): the cast is accepted and spends its resources, and the buff the script cannot place is omitted (NOT RETAIL, the
                // generic decline of BbBuffRuntime.Add). The hand-written module refused the cast before it spent anything.
                TestAssert.True(blocked.TryCastGround(0, slot, hero.Position + new Position2(100, 0)),
                    $"{team}/slot{slot}: a script cast is accepted at full capacity");
                Advance(blocked, .6);
                // Batch 340: a windup spends its mana at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not at the press.
                TestAssert.True(hero.Mana < mana, $"{team}/slot{slot}: and spends its mana");
                var controllers = blocked.Runtime.World.Entities.Values;
                TestAssert.False(hero.Buffs.Any(buff => buff.Name is "Deceive" or "DeceiveFade") ||
                                 controllers.Any(unit => unit.Kind is EntityKind.ShacoBox or EntityKind.ShacoClone),
                    $"{team}/slot{slot}: the owner controller the script could not place is omitted, so no stealth, box or clone");
                if (slot == 0) continue; // Q's seal is only opened by the Deceive buff that was omitted: the slot stays closed until Shaco dies.
                FreeAnnieShacoCapacity(hero);
                FreeAnnieShacoCapacity(hero);
                hero.SpellCooldowns[slot] = 0;
                hero.Mana = hero.MaxMana;
                TestAssert.True(blocked.TryCastGround(0, slot, hero.Position + new Position2(100, 0)),
                    $"{team}/slot{slot}: a freed controller slot lets the recast through");
                Advance(blocked, .6);
                TestAssert.True(blocked.Runtime.World.Entities.Values.Any(unit =>
                        unit.Kind == (slot == 1 ? EntityKind.ShacoBox : EntityKind.ShacoClone)),
                    $"{team}/slot{slot}: the recast places its box or clone");
            }

            using (var match = new Match("Shaco", team))
            {
                var hero = match.Hero(0);
                var target = match.Hero(1);
                target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
                FillAnnieShacoCapacity(target);
                var before = target.MatchStatistics.TotalDamageTaken;
                match.Cast(0, 2, target);
                Advance(match, 1.0);
                TestAssert.True(target.MatchStatistics.TotalDamageTaken > before,
                    "launched Two-Shiv still lands when its later slow cannot fit");
                TestAssert.False(target.Buffs.Any(buff => buff.Name is
                    "TwoShivPoison" or "BootsOfMobilityDebuff"));

                FreeAnnieShacoCapacity(target);
                FreeAnnieShacoCapacity(target);
                hero.SpellCooldowns[2] = 0;
                hero.Mana = hero.MaxMana;
                match.Cast(0, 2, target);
                Advance(match, 1.0);
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "TwoShivPoison") &&
                                target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"));
                TestAssert.Equal(SpellPackets.MaxBuffSlots, target.Buffs.Count);
                hero.SpellCooldowns[2] = 0;
                hero.Mana = hero.MaxMana;
                match.Cast(0, 2, target);
                Advance(match, 1.0);
                TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "TwoShivPoison"),
                    "active poison renews its existing slot at full capacity");
            }

            using (var attack = new Match("Shaco", team))
            {
                var hero = attack.Hero(0);
                var target = attack.Hero(1);
                // Champion sweep SHA-08, 27 Sept 2026: count the baseline aura before filling slots,
                // so it cannot consume the slot Deceive later releases for FromBehind.
                attack.Runtime.Spells.TickLateChampions(0);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Backstab"));
                hero.Facing = new Position2(1, 0);
                target.Facing = new Position2(1, 0);
                attack.Runtime.Buffs.AddBuff(hero, "Deceive", BuffType.Invisibility,
                    3.5, hero.NetId, new BuffPayload { DeceiveCritBonus = .2,
                        CritChance = 1.0, DodgePiercing = 1.0, Stealthed = true });
                FillAnnieShacoCapacity(hero);
                FillAnnieShacoCapacity(target);
                var before = target.MatchStatistics.TotalDamageTaken;
                attack.Attack(0, target);
                for (var step = 0; step < 200 &&
                     target.MatchStatistics.TotalDamageTaken <= before; step++)
                    attack.Runtime.Tick(.01);
                TestAssert.True(target.MatchStatistics.TotalDamageTaken > before,
                    "missing passive owner and victim rows cannot abort a basic hit");
                TestAssert.False(hero.Buffs.Any(buff => buff.Name is
                    "DeceiveCritBonus" or "FromBehind"),
                    "the first attack prepares its passives while Deceive still fills slot 64");
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "TwoShivPoison"));
                for (var step = 0; step < 200 &&
                     !hero.Buffs.Any(buff => buff.Name == "FromBehind"); step++)
                    attack.Runtime.Tick(.01);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FromBehind"),
                    "a later attack may admit Backstab after breaking Deceive frees its slot");
            }
        }
    }

    [Check("spells-live-shaco-box-fear-and-lock-capacity")]
    public static void ShacoBoxAndHardLockSkipOnlyUnavailableBuffs()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var owner = match.Hero(0);
            var victim = match.Hero(1);
            match.CastGround(0, victim.Position, 1);
            for (var step = 0; step < 30 && !match.Runtime.World.Entities.Values.Any(unit =>
                     unit.Kind == EntityKind.ShacoBox); step++)
                match.Runtime.Tick(.02);
            var box = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            FillAnnieShacoCapacity(owner);
            FillAnnieShacoCapacity(victim);
            var before = victim.MatchStatistics.TotalDamageTaken;
            Advance(match, 2.7);
            TestAssert.True(box.Alive && box.Buffs.Any(buff => buff.Name == "JackInTheBox"),
                "saturated optional rows cannot abort box activation");
            TestAssert.False(owner.Buffs.Any(buff => buff.Name == "JackInTheBoxDamageSensor"));
            TestAssert.False(victim.Buffs.Any(buff => buff.Name is
                "Fear" or "JackInTheBoxSoftLock"));
            TestAssert.True(victim.MatchStatistics.TotalDamageTaken > before,
                "the activated box still completes its independent projectile hit");
        }
    }

    private static void FillAnnieShacoCapacity(Entity unit)
    {
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots; slot++)
        {
            if (unit.Buffs.Count == SpellPackets.MaxBuffSlots) break;
            if (unit.Buffs.Any(buff => buff.Slot == slot)) continue;
            unit.Buffs.Add(new BuffState($"AnnieShacoCapacity{slot}", slot: slot, expiresAt: 60));
        }
        TestAssert.Equal(SpellPackets.MaxBuffSlots, unit.Buffs.Count);
    }

    private static void FreeAnnieShacoCapacity(Entity unit) =>
        unit.Buffs.Remove(unit.Buffs.First(buff =>
            buff.Name.StartsWith("AnnieShacoCapacity", StringComparison.Ordinal)));
}
