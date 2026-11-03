using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-targeted-capacity-commit-and-slow")]
    public static void TargetedItemCapacityKeepsCommitBoundary()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var denied = new Match("Annie", team))
            {
                var hero = denied.Hero(0);
                var target = denied.Hero(1);
                hero.Inventory = [new InventorySlot(3123), null, null, null, null, null];
                FillChampionCapacity(denied.Runtime.Buffs, target, 64);
                var hp = target.Health;
                TestAssert.False(denied.TryCast(0, 4, target),
                    "unshielded Executioner active rejects before cooldown when its only result cannot fit");
                TestAssert.Near(0, hero.ItemCooldowns[0], 0);
                TestAssert.Near(hp, target.Health, 0);
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "GrievousWound"));
            }

            using (var renewed = new Match("Annie", team))
            {
                var hero = renewed.Hero(0);
                var target = renewed.Hero(1);
                hero.Inventory = [new InventorySlot(3123), null, null, null, null, null];
                var existing = renewed.Runtime.Buffs.AddBuff(target, "GrievousWound",
                    BuffType.CombatDehancer, 1.0, hero.NetId);
                TestAssert.True(existing is not null);
                FillChampionCapacity(renewed.Runtime.Buffs, target, 64);
                renewed.Cast(0, 4, target);
                TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "GrievousWound"));
                TestAssert.Near(8.0,
                    target.Buffs.Single(buff => buff.Name == "GrievousWound").ExpiresAt, 0);
                TestAssert.Near(20.0, hero.ItemCooldowns[0], 0);
            }

            using (var shielded = new Match("Annie", team))
            {
                var hero = shielded.Hero(0);
                var target = shielded.Hero(1);
                hero.Inventory = [new InventorySlot(3123), null, null, null, null, null];
                TestAssert.True(shielded.Runtime.Buffs.AddBuff(target, "BansheesVeil",
                    BuffType.Aura, 10.0, target.NetId) is not null);
                FillChampionCapacity(shielded.Runtime.Buffs, target, 64);
                shielded.Cast(0, 4, target);
                TestAssert.False(target.Buffs.Any(buff => buff.Name is "BansheesVeil" or
                    "GrievousWound"));
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"),
                    "a full-table shield still consumes the accepted active and reuses its freed slot");
                TestAssert.Near(20.0, hero.ItemCooldowns[0], 0);
            }

            foreach (var item in new[] { 3144, 3146 })
            foreach (var used in new[] { 64, 63 })
            {
                using var damaged = new Match("Annie", team);
                var hero = damaged.Hero(0);
                var target = damaged.Hero(1);
                hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
                FillChampionCapacity(damaged.Runtime.Buffs, target, used);
                var hp = target.Health;
                damaged.Cast(0, 4, target);
                if (item == 3146) AdvanceGunbladeToRetirement(damaged);
                TestAssert.True(target.Health < hp,
                    $"{item}: committed hit remains when the optional slow cannot fit");
                TestAssert.Equal(used == 63,
                    target.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"));
                TestAssert.True(hero.ItemCooldowns[0] > 0.0);
                if (item == 3146)
                    TestAssert.Equal(1, damaged.Captures[0].Packets.Count(packet =>
                        Opcode(packet) == 0x5D), "the missile retires once after impact");
            }

            foreach (var used in new[] { 63, 62 })
            {
                using var boots = new Match("Annie", team);
                var hero = boots.Hero(0);
                var target = boots.Hero(1);
                hero.Inventory = [new InventorySlot(3144), null, null, null, null, null];
                target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
                // Batch 147 step 4: Items/3117.luaobj starts its Mobility penalty from its own BuffOnAllowAdd now, so
                // the copy has to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update
                // latency the 3022 cases already account for).
                boots.Runtime.Tick(0.01);
                boots.Runtime.HeroStats.Refresh(target);
                FillChampionCapacity(boots.Runtime.Buffs, target, used);
                var hp = target.Health;
                boots.Cast(0, 4, target);
                TestAssert.True(target.Health < hp);
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"));
                TestAssert.Equal(used == 62,
                    target.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"),
                    "the nested Boots marker consumes one slot before the optional slow");
            }
        }
    }

    // Batch 149: the revival is Spells/WillRevive.luaobj's own BuffOnPreDamage and its trigger is the ready marker
    // Items/3026.luaobj's UpdateSelfBuffActions keeps alive, not the inventory item the hand-written revive read; the
    // room a full table grants it is what Spells/GuardianAngel.luaobj's OnBuffActivate frees (the cleared-by-type sweep
    // of BBSpellBuffRemoveType and the BBSpellBuffRemove of WillRevive). The three arms are the same three outcomes the
    // hand-written revive had — a source-cleared status makes room, the ready marker makes room, or nothing does and
    // the hit stays lethal — and the invariant the check is named for is unchanged: the hit leaves either the whole
    // revival state or the whole lethal one, never half of each. "none" is now the arm where the copy's own marker add
    // cannot fit, since without that marker the script has no revive to open at all.
    [Check("items-live-guardian-capacity-lethal-or-revival")]
    public static void GuardianAngelCapacityKeepsLethalOrRevivalState()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var clearance in new[] { "none", "status", "ready" })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            var mana = hero.Mana;
            if (clearance == "status")
                TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "CapacityReviveHaste",
                    BuffType.Haste, 10.0, hero.NetId) is not null);
            // "status"/"ready": the copy binds on the next spell update (BbItemModule.SyncInventory) and its marker is up
            // before the fill takes the table to 64 — the status the revival sweeps ("status") or the marker its own
            // activation drops by name ("ready") is what makes room for GuardianAngel. "none": the table is filled before
            // that update, so the marker's add finds no slot and the revival never opens.
            if (clearance != "none")
                match.Runtime.Tick(0.01);
            FillChampionCapacity(match.Runtime.Buffs, hero, 64);
            // The timers are set after that update for the reason the cooldown-floor check gives: the bind's own clock
            // must not walk them into the revival's floor range.
            hero.SummonerCooldowns = [0.0, 2.0];
            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                hero.Health + 1.0, DamageType.True));
            var revives = clearance != "none";
            TestAssert.Equal(revives, hero.Alive);
            TestAssert.Equal(revives,
                hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
            if (revives)
            {
                TestAssert.Near(1.0, hero.Health, 0);
                TestAssert.Near(0.0, hero.Mana, 0);
                TestAssert.Near(4.0, hero.SummonerCooldowns[0], 0);
                TestAssert.Near(4.0, hero.SummonerCooldowns[1], 0);
                TestAssert.False(hero.Buffs.Any(buff => buff.Name is
                    "CapacityReviveHaste" or "WillRevive"));
            }
            else
            {
                TestAssert.Near(0.0, hero.Health, 0);
                TestAssert.Near(mana, hero.Mana, 0,
                    "rejected full-table revive does not zero mana");
                TestAssert.Near(0.0, hero.SummonerCooldowns[0], 0);
                TestAssert.Near(2.0, hero.SummonerCooldowns[1], 0);
            }
        }
    }

    [Check("items-live-targeted-slow-does-not-cross-yorick-first-death")]
    public static void TargetedSlowDoesNotCrossYorickFirstDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var item in new[] { 3144, 3146 })
        {
            using var match = new Match("Annie", team, enemyChampion: "Yorick");
            var caster = match.Hero(0);
            var yorick = match.Hero(1);
            caster.Inventory = [new InventorySlot(item), null, null, null, null, null];
            ArmYorickSelfRevival(match);
            yorick.Health = 1.0;
            match.Cast(0, 4, yorick);
            if (item == 3146) AdvanceGunbladeToRetirement(match);
            TestAssert.True(yorick.YorickRevival is not null && yorick.Alive,
                $"{item}: the real first death entered Yorick's temporary life");
            TestAssert.False(yorick.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"),
                $"{item}: old-life impact does not slow the revived actor");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, enemyChampion: "LeeSin");
            var caster = match.Hero(0);
            var target = match.Hero(1);
            caster.Inventory = [new InventorySlot(3144), null, null, null, null, null];
            target.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            // Batch 149: the copy binds on the next spell update (BbItemModule.SyncInventory); Items/3026.luaobj keeps
            // WillRevive alive and its BuffOnPreDamage preserves the same life at one health.
            match.Runtime.Tick(0.01);
            target.Health = 1.0;
            match.Cast(0, 4, target);
            TestAssert.True(target.Alive && target.YorickRevival is null &&
                target.Buffs.Any(buff => buff.Name == "GuardianAngel"),
                "Guardian Angel preserves the same life at one health");
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"),
                "the accepted same-life Cutlass slow remains eligible after Guardian Angel");
        }
    }
}
