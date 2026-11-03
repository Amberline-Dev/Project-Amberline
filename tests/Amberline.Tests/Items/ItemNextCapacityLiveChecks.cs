using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-onhit-buff-capacity-preserves-damage")]
    public static void BasicHitItemBuffOverflowDoesNotAbortDamage()
    {
        // Rows is the number of buffs one hit's own proc adds — the admission group one free slot has to make room
        // for, since a proc's BBSpellBuffAdds land all-or-none (BbBuffRuntime.CommitProcAdds). Script = retail:
        // Spells/WitsEnd.luaobj's BuffOnHitUnit adds WitsEndBuff and WitsEndCounter together and Spells/Malady.luaobj's
        // MaladyCounter and MaladySpell together, while the window and item hit hooks of the other four add one row
        // each (Spells/OdinBloodburster.luaobj's OdinBloodbursterBuff, Items/3071, 3185 and 3123's ItemOnHitUnit).
        var cases = new[]
        {
            (Item: 3181, Name: "OdinBloodbursterBuff", OnAttacker: true, Rows: 1),
            (Item: 3091, Name: "WitsEndCounter", OnAttacker: true, Rows: 2),
            (Item: 3114, Name: "MaladyCounter", OnAttacker: false, Rows: 2),
            (Item: 3071, Name: "BlackCleaver", OnAttacker: false, Rows: 1),
            (Item: 3185, Name: "OdinLightbringer", OnAttacker: false, Rows: 1),
            (Item: 3123, Name: "Mourning", OnAttacker: false, Rows: 1),
        };
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var item in cases)
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Inventory = [new InventorySlot(item.Item), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(attacker);
            // Batch 147: 3071, 3185 and 3123 add their on-hit buff from Items/<id>.luaobj ItemOnHitUnit now. A
            // copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory), the way a
            // real buy does, so the runtime gets that one update before the capacity is filled and the hit lands.
            match.Runtime.Tick(0.01);
            var recipient = item.OnAttacker ? attacker : target;
            FillItemBuffSlots(match.Runtime, recipient, 64);
            var before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                40.0, DamageType.Physical, DamageSourceKind.Basic),
                $"{team}/{item.Item}: real basic hit completes at full buff capacity");
            TestAssert.True(target.Health < before);
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == item.Name),
                $"{team}/{item.Item}: missing optional item buff is skipped at full capacity");

            // Freeing fewer slots than the script's own set needs still declines the whole group (see Rows above).
            for (var freed = 0; freed < item.Rows; freed++)
            {
                var filler = recipient.Buffs.First(buff =>
                    buff.Name.StartsWith("ItemCapacityPassive_", StringComparison.Ordinal));
                TestAssert.True(match.Runtime.Buffs.RemoveBuff(recipient, filler.Slot));
            }
            before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                40.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before);
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == item.Name));
            TestAssert.Equal(64, recipient.Buffs.Count);

            before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                40.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before);
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == item.Name),
                "same-name item renewal keeps a single live row at 64 slots");
            TestAssert.Equal(64, recipient.Buffs.Count);
        }

        // Pet.lua copies the owner's inventory to these Monster actors.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick" })
        foreach (var (itemId, buffName) in new[]
                 { (3181, "OdinBloodbursterBuff"), (3091, "WitsEndCounter") })
        {
            using var match = new Match(champion, team);
            var owner = match.Hero(0);
            var target = match.Hero(1);
            owner.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
            target.MaxHealth = target.Health = 10000.0;
            match.Cast(0, 3, owner);
            match.Runtime.Tick(0.6);
            var kind = champion == "Shaco" ? EntityKind.ShacoClone : EntityKind.YorickRevenant;
            var clone = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == kind && unit.Alive);
            TestAssert.True(Inventory.HasItem(clone.Inventory, itemId));
            FillItemBuffSlots(match.Runtime, clone, 64);
            var before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(clone.NetId, target.NetId,
                1.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before);
            TestAssert.False(clone.Buffs.Any(buff => buff.Name == buffName));
        }
    }

    [Check("items-live-red-buff-boots-capacity-pair")]
    public static void RedBuffKeepsBurnAndSlowPairBehindBootsBudget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var used in new[] { 64, 62, 61 })
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            // Batch 159 part 2: the burn and slow are Spells/BlessingoftheLizardElder.luaobj's own BuffOnHitUnit
            // (0598), which only a live script instance runs; a hand-built row carries no script.
            TestAssert.True(BbM3.ScriptBuff(match.Runtime, attacker,
                "BlessingoftheLizardElder", 10.0) is not null);
            target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            // Batch 147 step 4: Items/3117.luaobj starts its Mobility penalty from its own BuffOnAllowAdd now, so the
            // copy has to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update latency
            // the 3022 cases already account for).
            match.Runtime.Tick(0.01);
            match.Runtime.HeroStats.Refresh(target);
            FillItemBuffSlots(match.Runtime, target, used);
            var before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                40.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before);
            // Script wins (batch 159 part 2): Spells/BlessingoftheLizardElder.luaobj's BuffOnHitUnit is two
            // independent adds — Burning (0394-0411, BUFF_RENEW_EXISTING) and Slow (0568-0586,
            // BUFF_STACKS_AND_OVERLAPS) — not the one grouped callback the hand-written twin ran. Each is admitted
            // on its own slot budget, so a table with room for only one of them burns without slowing; the old model
            // refused both. The Boots row is the lifecycle's own nested hostile-control add, which runs inside the
            // Burning add (BuffLifecycle.AddBuff) before that add takes its slot.
            var pair = used == 61;
            var burning = target.Buffs.Any(buff => buff.Name == "Burning");
            var slow = target.Buffs.Any(buff => buff.Name == "Slow");
            var boots = target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff");
            TestAssert.Equal(used < 64, burning,
                $"used={used} burning names={string.Join(",", target.Buffs.Select(b => b.Name))}");
            TestAssert.Equal(pair, slow, $"used={used} slow (" + "the second add needs a slot of its own)");
            TestAssert.Equal(used < 64, boots, $"used={used} boots");
            if (!pair) continue;
            TestAssert.Equal(64, target.Buffs.Count);
            before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                40.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before);
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Burning"));
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Slow"));
            TestAssert.Equal(64, target.Buffs.Count);
        }
    }

    [Check("items-live-ionic-chain-full-buffs-no-repeat")]
    public static void IonicChainWithoutMarkerStillVisitsEachUnitOnce()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var primary = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3178), null, null, null, null, null];
            // Batch 149 step 5: the copy binds on the next spell update (BbItemModule.SyncInventory) and Items/3178.luaobj
            // opens its LightningRodApplicator window there; the fourth-hit arm is that window's own
            // InstanceVars.AttackCounter, not the deleted HeroItemState.Item3178Stacks. The whole match tick is what
            // runs the item module's poll — Spells.Tick alone leaves the copy unbound.
            match.Runtime.Tick(0.01);
            primary.SpellBlock = 0.0;
            var second = AddMinion(20.0);
            var third = AddMinion(40.0);
            var targets = new[] { primary, second, third };
            foreach (var target in targets)
            {
                target.MaxHealth = target.Health = 1000.0;
                FillItemBuffSlots(match.Runtime, target, 64);
            }
            ItemScriptState.ArmWindowCounter(match.Runtime, attacker, "LightningRodApplicator", "AttackCounter", 3.0);
            // Script wins (batch 149 step 5): the shipped chain is Spells/LightningRodChain.luaobj — the marker add is
            // the chain itself (its own BBApplyDamage, and each bounce re-adds it to the next target with a raised
            // BounceCounter). A full buff table on the primary therefore swallows the proc whole. The hand-written
            // chain's damage was independent of the marker and kept visiting; that difference is the script's.
            // What the check keeps is the capacity half of the property: a refused add does not abort the basic hit.
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, primary.NetId,
                1.0, DamageType.True, DamageSourceKind.Basic));
            TestAssert.Near(999.0, primary.Health, 1e-6,
                "the refused chain add loses the impact, not the basic hit");
            TestAssert.Near(1000.0, second.Health, 1e-6);
            TestAssert.Near(1000.0, third.Health, 1e-6,
                "a chain that never started cannot visit a unit once");
            TestAssert.True(targets.All(target =>
                !target.Buffs.Any(buff => buff.Name == "LightningRodChain")));

            Entity AddMinion(double x)
            {
                return match.Runtime.World.Register(new Entity(
                    match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, primary.Team,
                    new Position2(primary.Position.X + x, primary.Position.Z))
                {
                    Name = "Minion_T200Basic", Health = 1000.0, MaxHealth = 1000.0,
                    Speed = 325.0, Radius = 0.0,
                });
            }
        }
    }

    [Check("items-live-sunfire-snowball-capacity-retry")]
    public static void PeriodicAuraAndSnowballMarkersRetryBehindFullCapacity()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Annie", team))
            {
                var owner = match.Hero(0);
                var target = match.Hero(1);
                target.Position = owner.Position + new Position2(50.0, 0.0);
                owner.Inventory = [new InventorySlot(3068), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(owner);
                FillItemBuffSlots(match.Runtime, owner, 64);
                FillItemBuffSlots(match.Runtime, target, 64);
                var before = target.MatchStatistics.TotalDamageTaken;
                match.Runtime.Tick(.05);
                // Script wins (batch 149 step 1): the 400-range burn is Spells/SunfireCloakParticle.luaobj's own
                // BuffOnUpdateActions, and that buff is a row on the wearer — Items/3068.luaobj re-adds it every
                // second. A saturated wearer table declines the row, so there is nothing to tick that poll.
                TestAssert.Near(before, target.MatchStatistics.TotalDamageTaken, 1e-9,
                    "a saturated wearer table has no Sunfire row to tick");
                TestAssert.False(owner.Buffs.Any(buff => buff.Name == "SunfireCloakParticle"));
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "SunfireCapeAura"));
                var ownerFiller = owner.Buffs.First(buff =>
                    buff.Name.StartsWith("ItemCapacityPassive_", StringComparison.Ordinal));
                var targetFiller = target.Buffs.First(buff =>
                    buff.Name.StartsWith("ItemCapacityPassive_", StringComparison.Ordinal));
                TestAssert.True(match.Runtime.Buffs.RemoveBuff(owner, ownerFiller.Slot));
                TestAssert.True(match.Runtime.Buffs.RemoveBuff(target, targetFiller.Slot));
                match.Runtime.Tick(1.0);
                TestAssert.True(owner.Buffs.Any(buff => buff.Name == "SunfireCloakParticle"));
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "SunfireCapeAura"));
                TestAssert.Equal(64, owner.Buffs.Count);
                TestAssert.Equal(64, target.Buffs.Count);
            }

            // Batch 149 step 1: the snowball markers are the shipped scripts' own now (Spells/MejaisCheck.luaobj
            // and its two siblings, driven from Items/3041, 3138 and 3141's UpdateSelfBuffActions). The copy re-adds
            // its check buff — and at twenty stacks its cap — on its own one-second BBExecutePeriodically, so those two
            // are the retries this check is named for. What is gone is the hand-written counter that used to bank a
            // stack behind a full table: the stack is the *Stats buff itself (BUFF_STACKS_AND_RENEWS), so a saturated
            // table loses that kill's stack (script = retail) while the stacks already on the buff survive the retry.
            foreach (var (itemId, checkName, statsName, capName) in new[]
                     {
                         (3041, "MejaisCheck", "MejaisStats", "MejaisCap"),
                         (3138, "LeviathanCheck", "LeviathanStats", "LeviathanCap"),
                         (3141, "MuramasaCheck", "MuramasaStats", "MuramasaCap"),
                     })
            {
                using var match = new Match("Annie", team);
                var hero = match.Hero(0);
                var victim = match.Hero(1);
                hero.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                // The copy binds to its slot on the next spell update and only then seeds the one-second clock its
                // check buff is renewed from.
                match.Runtime.Tick(.01);
                FillItemBuffSlots(match.Runtime, hero, 64);
                match.Runtime.Tick(1.1);
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == checkName),
                    $"{team}/{itemId}: check marker cannot land on a full table");
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName),
                    $"{team}/{itemId}: no stats buff while the table is full");
                TestAssert.Equal(64, hero.Buffs.Count);

                RemoveOneFiller();
                match.Runtime.Tick(1.1);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == checkName),
                    $"{team}/{itemId}: the copy retries its check marker");
                TestAssert.Equal(64, hero.Buffs.Count);

                KillVictim();
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName),
                    $"{team}/{itemId}: a kill's stack needs a row of its own");
                TestAssert.Equal(64, hero.Buffs.Count);

                RemoveOneFiller();
                KillVictim();
                TestAssert.Equal(2, hero.Buffs.Single(buff => buff.Name == statsName).Stacks,
                    $"{team}/{itemId}: two adds a hero kill");
                for (var kill = 1; kill < 10; kill++) KillVictim();
                TestAssert.Equal(20, hero.Buffs.Single(buff => buff.Name == statsName).Stacks,
                    $"{team}/{itemId}: the stacks already on the buff survive every later retry");
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == capName),
                    $"{team}/{itemId}: cap marker cannot land on a full table either");
                TestAssert.Equal(64, hero.Buffs.Count);

                RemoveOneFiller();
                match.Runtime.Tick(1.1);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == capName),
                    $"{team}/{itemId}: the copy retries its cap marker at twenty stacks");
                TestAssert.Equal(64, hero.Buffs.Count);

                void RemoveOneFiller()
                {
                    var filler = hero.Buffs.First(buff =>
                        buff.Name.StartsWith("ItemCapacityPassive_", StringComparison.Ordinal));
                    TestAssert.True(match.Runtime.Buffs.RemoveBuff(hero, filler.Slot));
                }

                void KillVictim()
                {
                    if (!victim.Alive)
                    {
                        match.Runtime.Combat.ScheduleRespawn(victim);
                        TestAssert.True(match.Runtime.Combat.RespawnImmediately(victim),
                            $"{team}/{itemId}: victim respawns for the next stack");
                    }
                    TestAssert.True(match.Runtime.Combat.Kill(victim, hero.NetId),
                        $"{team}/{itemId}: production kill grants the script's stacks");
                }
            }
        }
    }
}
