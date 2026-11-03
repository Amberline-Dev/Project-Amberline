using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-periodic-procs-end-with-recipient-life")]
    public static void ItemBurnAndBleedDoNotResumeAfterImmediateRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (item, alias) in new[]
                 { (0, "Burning"), (3184, "EntropyBurning"), (3123, "Mourning") })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var recipient = match.Hero(1);
            source.Position = recipient.Position;
            recipient.MaxHealth = recipient.Health = 10000;
            recipient.Armor = recipient.SpellBlock = 0;
            if (item != 0)
                source.Inventory = [new InventorySlot(item), null, null, null, null, null];
            if (item == 0)
                // Batch 159 part 2: the burn and slow are Spells/BlessingoftheLizardElder.luaobj's own
                // BuffOnHitUnit (0598), which only a live script instance runs.
                TestAssert.True(BbM3.ScriptBuff(runtime, source, "BlessingoftheLizardElder", 10) is not null);
            // Batch 149 step 2: 3184's window is Spells/OdinEntropicClaymore.luaobj's own self buff now (the add
            // its SelfExecute makes), so a real item use opens it instead of the hand-written AddBuff this used to
            // make.
            if (item == 3184)
                match.Cast(0, 4, source);
            // The script-scheduled pulses of EntropyBurning (Spells/EntropyBurning.luaobj) and Mourning
            // (Items/3123.luaobj) read GetTime, which SpellSystem.Tick advances before the unit's buffs are ticked
            // (ProductionMatchComposition.Tick). EntropyBurning is added with TickRate 1 and its
            // BBExecutePeriodically runs with ExecuteImmediately false: its first script update (one second after
            // the hit) only seeds LastTimeExecuted and the second one — two seconds after the hit — is the first
            // damage. Three steps cover both, so a burn that survived the death would be caught here.
            void Step(double dt)
            {
                runtime.Spells.Tick(dt);
                runtime.Buffs.TickBuffs(recipient, dt);
            }
            // Batch 147: Mourning is Items/3123.luaobj ItemOnHitUnit's own add now, and a copy binds to its
            // inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does.
            runtime.Tick(0.01);
            TestAssert.True(runtime.Combat.ApplyHit(source.NetId, recipient.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic), $"{team}/{alias}: real hit");
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{team}/{alias}: hit attached periodic item buff");
            if (item == 0)
                TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "Slow"),
                    "red burn also attaches its temporary slow");

            TestAssert.True(runtime.Combat.Kill(recipient, source.NetId));
            runtime.Combat.ScheduleRespawn(recipient);
            if (item == 0)
                TestAssert.True(source.Buffs.Any(buff =>
                    buff.Name == "BlessingoftheLizardElder"),
                    "recipient death keeps the attacker's transferable red buff");
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias ||
                (item == 0 && buff.Name == "Slow")),
                $"{team}/{alias}: death retires attached damage and slow");
            recipient.SummonerSpells[0] = "SummonerRevive";
            TestAssert.True(match.TryCast(1, 0, recipient, summoner: true),
                $"{team}/{alias}: immediate production Revive");
            TestAssert.True(recipient.Alive);
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            Step(0.1);
            Step(0.9);
            Step(1.0);
            TestAssert.Near(damage, recipient.MatchStatistics.TotalDamageTaken, 1e-8,
                $"{team}/{alias}: old-life periodic damage never resumes");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var recipient = match.Hero(1);
            source.Inventory = [new InventorySlot(3184), null, null, null, null, null];
            // Batch 149 step 2: the real active opens the script's own window (see above).
            match.Cast(0, 4, source);
            recipient.MaxHealth = recipient.Health = 10000;
            recipient.Armor = recipient.SpellBlock = 0;
            TestAssert.True(runtime.Combat.ApplyHit(source.NetId, recipient.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(runtime.Combat.Kill(source, recipient.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "EntropyBurning"),
                "caster death does not purge a living recipient's burn");
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            // The burn's first script update (one second after the hit) only seeds its clock and its second one is
            // the first damage (see above): seed it, then give it the full period the pulse needs.
            runtime.Spells.Tick(0.1);
            runtime.Buffs.TickBuffs(recipient, 0.1);
            runtime.Spells.Tick(0.9);
            runtime.Buffs.TickBuffs(recipient, 0.9);
            runtime.Spells.Tick(1.0);
            runtime.Buffs.TickBuffs(recipient, 1.0);
            TestAssert.True(recipient.MatchStatistics.TotalDamageTaken > damage,
                "the living recipient's burn still pulses after caster death");
        }
    }

    [Check("items-live-on-hit-debuffs-end-with-recipient-life")]
    public static void ArmorMrAndItemSlowEndAtDeathAndStayGoneAfterRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (item, alias) in new[]
                 { (3071, "BlackCleaver"), (3114, "MaladyCounter"), (3022, "ItemSlow") })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var recipient = match.Hero(1);
            source.Inventory = [new InventorySlot(item), null, null, null, null, null];
            // Batch 147: 3071's BlackCleaver and 3022's item-slow pair are Items/<id>.luaobj ItemOnHitUnit's
            // own adds now (3114 is still hand-written). A copy binds to its inventory slot on the next spell
            // update (BbItemModule.SyncInventory), the way a real buy does, so give the runtime that update first.
            runtime.Tick(0.01);
            runtime.HeroStats.Refresh(recipient);
            var armor = recipient.Armor;
            var spellBlock = recipient.SpellBlock;
            var speed = recipient.Speed;
            recipient.MaxHealth = recipient.Health = 10000;
            TestAssert.True(runtime.Combat.ApplyHit(source.NetId, recipient.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{team}/{item}: real item hit attaches {alias}");
            if (item == 3071)
                TestAssert.True(recipient.Armor < armor, "Black Cleaver lowers armor in this life");
            if (item == 3114)
                TestAssert.True(recipient.SpellBlock < spellBlock, "Malady lowers MR in this life");
            if (item == 3022)
            {
                // Script wins (batch 147): the shipped Internal_*Slow rows are empty scripts, so the
                // family is identified by name (Spells/ItemSlow.luaobj's cascade does the same).
                TestAssert.True(recipient.Buffs.Any(buff => BuffLifecycle.IsItemSlowRow(buff.Name)),
                    "the item slow carries a self-bound contribution row");
                TestAssert.True(recipient.Speed < speed, "item slow reduces current-life speed");
            }
            TestAssert.True(runtime.Combat.Kill(recipient, source.NetId));
            runtime.Combat.ScheduleRespawn(recipient);
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias ||
                BuffLifecycle.IsItemSlowRow(buff.Name)),
                $"{team}/{item}: recipient death clears the marker and contribution");
            TestAssert.True(runtime.Combat.RespawnImmediately(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias ||
                BuffLifecycle.IsItemSlowRow(buff.Name)),
                $"{team}/{item}: new life carries no old item debuff");
            TestAssert.Near(armor, recipient.Armor, 1e-6, "new life restores baseline armor");
            TestAssert.Near(spellBlock, recipient.SpellBlock, 1e-6, "new life restores baseline MR");
            TestAssert.Near(speed, recipient.Speed, 1e-6, "new life restores baseline move speed");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var recipient = match.Hero(1);
            source.Inventory = [new InventorySlot(3144), null, null, null, null, null];
            match.Cast(0, 4, recipient);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"),
                $"{team}: real targeted Cutlass active attaches its slow");
            TestAssert.True(runtime.Combat.Kill(recipient, source.NetId));
            runtime.Combat.ScheduleRespawn(recipient);
            TestAssert.True(runtime.Combat.RespawnImmediately(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"));
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Annie", team))
            {
                var source = match.Hero(0);
                var recipient = match.Hero(1);
                source.Inventory = [new InventorySlot(3143), null, null, null, null, null];
                match.Cast(0, 4, source);
                TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "BlackOmen") &&
                    recipient.Buffs.Any(buff => buff.Name == "Slow"),
                    "real Randuin active applies both temporary recipient debuffs");
                TestAssert.True(match.Runtime.Combat.Kill(recipient, source.NetId));
                TestAssert.False(recipient.Buffs.Any(buff => buff.Name is "BlackOmen" or "Slow"));
            }
            using (var match = new Match("Annie", team, random: new CountingProcRandom()))
            {
                var recipient = match.Hero(0);
                var defender = match.Hero(1);
                defender.Inventory = [new InventorySlot(3082), null, null, null, null, null];
                // Batch 147: 3082's retaliation is Items/3082.luaobj ItemOnBeingHit now, and a copy of the script binds to
                // its inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does.
                match.Runtime.Tick(0.01);
                // Batch 222: the retaliation's roll is the BuildingBlocks roll on the match's Lua stream; the next Lua draw is 0 (the proc passes).
                LuaRollSeam.Next(match.Runtime.World.LuaRandom, 0.0);
                TestAssert.True(match.Runtime.Combat.ApplyHit(recipient.NetId, defender.NetId,
                    1, DamageType.Physical, DamageSourceKind.Basic));
                TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "Cripple") &&
                    recipient.Buffs.Any(buff => buff.Name == "Slow"),
                    "real Warden retaliation applies both temporary attacker debuffs");
                TestAssert.True(match.Runtime.Combat.Kill(recipient, defender.NetId));
                TestAssert.False(recipient.Buffs.Any(buff => buff.Name is "Cripple" or "Slow"));
            }
        }
    }

    [Check("items-live-hostile-item-auras-end-with-recipient-life")]
    public static void HostileAurasDoNotCrossDeathBeforeFreshRangeEvaluation()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (item, alias) in new[]
                 { (3001, "AbyssalScepterAura"), (3050, "RallyingBanner"),
                   (3110, "FrozenHeartAura") })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var recipient = match.Hero(1);
            source.Inventory = [new InventorySlot(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(source);
            // Batch 145 step 2: Items/<id>.luaobj re-adds its aura from BBExecutePeriodically (0.9 s for 3001 and
            // 3110, 1.0 s for 3050, ExecuteImmediately false), so the copy here is one period old rather than the
            // first sync tick's.
            runtime.Tick(1.15);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{team}/{item}: a real hostile item aura attaches while in range");
            TestAssert.True(runtime.Combat.Kill(recipient, source.NetId));
            runtime.Combat.ScheduleRespawn(recipient);
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{team}/{item}: recipient death retires the old attached aura");
            TestAssert.True(runtime.Combat.RespawnImmediately(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias),
                "fountain respawn outside aura range has no old-life stat penalty");
            runtime.Tick(1.15);
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias),
                "the script's periodic re-add does not restore the out-of-range recipient");
        }
    }

    [Check("items-live-yorick-temporary-life-burn-ends-at-final-death")]
    public static void YorickFinalDeathAlsoRetiresNewlyAttachedItemBurn()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var recipient = match.Hero(0);
            var source = match.Hero(1);
            recipient.Level = 18; // Counted first-death respawn stays after the 11s temporary life.
            // Keep the red-buff attacker outside enemy combat during the temporary life.
            source.Position = new Position2(recipient.Position.X + 3000, recipient.Position.Z);
            // Batch 159 part 2: see the first site — the red burn is the buff script's own BuffOnHitUnit.
            TestAssert.True(BbM3.ScriptBuff(runtime, source, "BlessingoftheLizardElder", 15) is not null);
            match.Cast(0, 3, recipient);
            runtime.Tick(.3);
            TestAssert.True(runtime.Combat.Kill(recipient, source.NetId));
            runtime.Tick(1.1);
            TestAssert.True(recipient.YorickRevival is not null && recipient.Alive &&
                    recipient.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
                "real Yorick R entered its playable temporary life");
            // The token's Deadline is the nominal end (the delay plus the zombie buff); the script ends the life on the buff's own tick.
            var deadline = recipient.YorickRevival!.Deadline;
            runtime.Tick(Math.Max(0, deadline - runtime.Combat.CurrentTime - .15));
            TestAssert.True(recipient.Alive && recipient.YorickRevival is not null,
                "a living zombie can receive a new item proc shortly before final death");
            TestAssert.True(source.Alive && source.Health > 0,
                $"{team}: red-buff attacker remains alive before the late hit");
            var red = source.Buffs.FirstOrDefault(buff =>
                buff.Name == "BlessingoftheLizardElder");
            TestAssert.True(red is not null,
                $"{team}: living attacker still owns red before zombie hit at " +
                $"{runtime.Combat.CurrentTime:R}s (deadline {deadline:R})");
            TestAssert.True(recipient.Health > 1,
                $"{team}: zombie has nonlethal hit room before item proc: " +
                $"health={recipient.Health:R} max={recipient.MaxHealth:R}");
            source.Position = new Position2(recipient.Position.X + 250, recipient.Position.Z);
            var attackDelta = source.Position - recipient.Position;
            var attackDistance = Math.Sqrt(attackDelta.X * attackDelta.X +
                attackDelta.Z * attackDelta.Z);
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            TestAssert.True(attackDistance <= CombatService.AttackReach(source, recipient),
                $"{team}: attacker returns within basic-attack range before the late hit");
            TestAssert.True(runtime.Combat.ApplyHit(source.NetId, recipient.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "Burning"),
                $"{team}: late zombie hit attaches red burn; sourceRed={red!.ExpiresAt:R}, " +
                $"sourceAlive={source.Alive}, sourceHp={source.Health:R}, sourceLevel={source.Level}, " +
                $"targetAlive={recipient.Alive}, targetHp={recipient.Health:R}, " +
                $"targetKind={recipient.Kind}, targetUnitType={recipient.UnitType}, " +
                $"targetBuffs=[{string.Join(',', recipient.Buffs.Select(buff => buff.Name))}], " +
                $"time={runtime.Combat.CurrentTime:R}, deadline={deadline:R}");
            runtime.Tick(.4);
            TestAssert.True(!recipient.Alive && recipient.YorickRevival is null,
                "Yorick final death bypasses the ordinary CombatService.Kill hook");
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name is "Burning" or "Slow"),
                "the shared recipient cleanup also runs at final temporary-life death");
            TestAssert.True(runtime.Combat.RespawnImmediately(recipient));
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            runtime.Buffs.TickBuffs(recipient, 1.1);
            TestAssert.Near(damage, recipient.MatchStatistics.TotalDamageTaken, 1e-8,
                "an old temporary-life item burn cannot pulse in the next real life");
        }
    }
}
