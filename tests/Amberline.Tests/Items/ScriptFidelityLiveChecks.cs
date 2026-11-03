using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Overnight script-fidelity pass (16 October 2026): item behaviour that must follow the shipped
/// BuildingBlocks scripts. BBApplyDamage's SpellDamageRatio / PhysicalDamageRatio are not a
/// +100% AD/AP scale on item proc or periodic damage; each check names its script.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Mourning.luaobj BuffOnUpdateActions: ApplyDamage(Damage=4, MAGIC, PERIODIC, SDR=0, PDR=1).
    [Check("items-live-mourning-bleed-is-flat-script-damage")]
    public static void ExecutionersBleedIgnoresAttackDamage()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var attackDamage in new[] { 0.0, 120.0 })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var target = match.Hero(1);
            hero.Inventory = [new InventorySlot(3123), null, null, null, null, null];
            hero.AttackDamage = attackDamage;
            target.MaxHealth = target.Health = 10000.0;
            target.Armor = target.SpellBlock = 0.0;
            // Batch 147: Mourning is Items/3123.luaobj ItemOnHitUnit's own add now. A copy is bound to its
            // inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy is, so
            // the runtime gets that one update before the hit.
            runtime.Tick(0.01);
            TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, target.NetId, 1.0,
                DamageType.Physical, DamageSourceKind.Basic), $"{team}: basic hit lands");
            var bleed = target.Buffs.Single(buff => buff.Name == "Mourning");
            TestAssert.Near(4.0, bleed.Payload.DamagePerTick, 0, $"{team} AD={attackDamage}: flat 4 per tick");
            var before = target.Health;
            runtime.Buffs.TickBuffs(target, 1.0);
            TestAssert.Near(before - 4.0, target.Health, 1e-7,
                $"{team} AD={attackDamage}: one tick is 4 raw magic damage");
        }
    }

    // LightstrikerApplicator.luaobj BuffOnHitUnit: ApplyDamage(Damage=100, MAGIC, PROC, SDR=0, PDR=1).
    [Check("items-live-sword-of-divine-fourth-hit-is-flat-100")]
    public static void SwordOfTheDivineBonusIgnoresAttackDamage()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var attackDamage in new[] { 0.0, 250.0 })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var target = match.Hero(1);
            hero.Inventory = [new InventorySlot(3131), null, null, null, null, null];
            // Batch 149 step 5: Items/3131.luaobj opens Spells/LightstrikerApplicator.luaobj on the poll that binds
            // the copy (ExecuteImmediately true), and that window's own InstanceVars.AttackCounter is the arm the
            // hand-written HeroItemState.Item3131Stacks used to be. Four real hits are the script's own count.
            runtime.Tick(0.01);
            hero.AttackDamage = attackDamage;
            target.MaxHealth = target.Health = 10000.0;
            target.Armor = target.SpellBlock = 0.0;
            var deltas = new List<double>();
            for (var hit = 1; hit <= 4; hit++)
            {
                var before = target.Health;
                TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, target.NetId, 1.0,
                    DamageType.Physical, DamageSourceKind.Basic), $"{team}: hit {hit} lands");
                deltas.Add(before - target.Health);
            }
            TestAssert.Near(1.0, deltas[2], 1e-7, $"{team} AD={attackDamage}: third hit has no bonus");
            TestAssert.Near(101.0, deltas[3], 1e-7,
                $"{team} AD={attackDamage}: fourth hit adds exactly 100 magic damage");
        }
    }

    // DeathfireGraspSpell.luaobj: percentBurn = 0.3 + 0.00035 * AP, minimum 200, current health.
    [Check("items-live-deathfire-grasp-uses-script-ap-ratio")]
    public static void DeathfireGraspUsesTheScriptRatio()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3128), null, null, null, null, null];
            // Batch 149 step 2: the AP the burn reads is the hero's own, and the hero's own is sampled now.
            // 3128's shipped window opens on the purchase poll (Items/3128.luaobj, ExecuteImmediately true) and
            // that buff add re-samples the hero's stats the way every buff does (HeroStatsServices), so the item's
            // +60 AP row is in the field and a field written by hand does not survive to the impact. The rest of
            // the 500 is a probe buff's payload row — the channel the calculator keeps across those samples.
            match.Runtime.HeroStats.Refresh(hero);
            match.Runtime.Buffs.AddBuff(hero, "ApRatioProbe", BuffType.Internal, 30.0,
                hero.NetId, new BuffPayload { AbilityPower = 500.0 - hero.AbilityPower });
            match.Runtime.HeroStats.Refresh(hero);
            TestAssert.Near(500.0, hero.AbilityPower, 1e-9, "the probe holds the caster at 500 AP");
            enemy.MaxHealth = enemy.Health = 5000.0;
            enemy.SpellBlock = 0.0;
            match.Cast(0, 4, enemy);
            for (var tick = 0; tick < 30 && enemy.Health >= 5000.0; tick++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.Near(5000.0 * (0.30 + 500.0 * 0.00035), 5000.0 - enemy.Health, 1e-6,
                $"{team}: 30% + 3.5% per 100 AP of current health");
        }
    }

    // Thornmail.luaobj BuffOnBeingHit: ApplyDamage(Owner -> Attacker, DamageAmount * 0.3, MAGIC,
    // REACTIVE); DamageAmount is the hit before armor (wiki V0.9.25.24).
    [Check("items-live-thornmail-reflects-pre-armor-hit")]
    public static void ThornmailReflectsTheRawHit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var holder = match.Hero(1);
            holder.Inventory = [new InventorySlot(3075), null, null, null, null, null];
            // Batch 149 step 5: Items/3075.luaobj's BuffOnBeingHit is the reflect now, so the copy has to be bound to
            // its slot first (BbItemModule.SyncInventory, the same next-spell-update latency the other converted
            // items account for).
            runtime.Tick(0.01);
            holder.MaxHealth = holder.Health = 5000.0;
            holder.Armor = 100.0;
            attacker.MaxHealth = attacker.Health = 5000.0;
            attacker.SpellBlock = 0.0;
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, holder.NetId, 100.0,
                DamageType.Physical, DamageSourceKind.Basic), $"{team}: hit lands");
            TestAssert.Near(50.0, 5000.0 - holder.Health, 1e-7, $"{team}: 100 armor halves the hit");
            TestAssert.Near(30.0, 5000.0 - attacker.Health, 1e-7,
                $"{team}: 30% of the pre-armor 100 returns as magic damage");
        }
    }

    // RegenerationPotion / FlaskOfCrystalWater inibins: SpellData Cooldown = 1 s (the stock loader
    // reads Cooldown1, else Cooldown).
    [Check("items-live-consumables-have-a-spell-cooldown")]
    public static void PotionsCarryTheirInibinCooldown()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (item, spell) in new[] { (2003, "RegenerationPotion"), (2004, "FlaskOfCrystalWater") })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var cooldown = GameData.Default.Spell(spell, 1).Cooldowns[0];
            TestAssert.True(cooldown > 0.0, $"{spell} ships a use cooldown");
            hero.Inventory = [new InventorySlot(item, count: 3), null, null, null, null, null];
            hero.Health = hero.MaxHealth * 0.5;
            hero.Mana = hero.MaxMana * 0.5;
            match.Clear();
            TestAssert.True(match.TryCast(0, 4, hero), $"{team} {spell}: first use accepted");
            TestAssert.Near(cooldown, hero.ItemCooldowns[0], 1e-9, $"{team} {spell}: slot cooldown set");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x8A &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
                packet.Plaintext.Span[5] == 4 &&
                BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..]) == (float)cooldown),
                $"{team} {spell}: cooldown packet for item slot 4");
            TestAssert.False(match.TryCast(0, 4, hero), $"{team} {spell}: second use inside the cooldown");
            TestAssert.Equal(2, hero.Inventory[0]!.Count, $"{team} {spell}: rejected use keeps the charge");
            runtime.Tick(cooldown);
            TestAssert.True(match.TryCast(0, 4, hero), $"{team} {spell}: usable again after the cooldown");
            TestAssert.Equal(1, hero.Inventory[0]!.Count, $"{team} {spell}: second drink spends a charge");

            // The cooldown belongs to the slot's own spell instance: the potion scripts never
            // loop over other slots (Zhonya's and Mourning's do), so a second stack stays ready.
            hero.Inventory = [new InventorySlot(item, count: 2), new InventorySlot(item, count: 2),
                null, null, null, null];
            hero.ItemCooldowns[0] = hero.ItemCooldowns[1] = 0.0;
            hero.Health = hero.MaxHealth * 0.5;
            hero.Mana = hero.MaxMana * 0.5;
            TestAssert.True(match.TryCast(0, 4, hero), $"{team} {spell}: first stack");
            TestAssert.Near(0.0, hero.ItemCooldowns[1], 0, $"{team} {spell}: the other stack keeps no cooldown");
            hero.Health = hero.MaxHealth * 0.5;
            hero.Mana = hero.MaxMana * 0.5;
            TestAssert.True(match.TryCast(0, 5, hero), $"{team} {spell}: second stack drinks at once");
        }
    }

    // ZhonyasHourglass.luaobj TargetExecute: BBStopChanneling(Owner); ZhonyasRingShield.luaobj
    // OnBuffActivate: BBDestroyMissileForTarget(Owner).
    [Check("items-live-zhonya-destroys-missiles-and-stops-channels")]
    public static void ZhonyasActivationDestroysIncomingMissiles()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var annie = match.Hero(0);
            var holder = match.Hero(1);
            holder.Inventory = [new InventorySlot(3157), null, null, null, null, null];
            holder.MaxHealth = holder.Health = 5000.0;
            match.Cast(0, 0, holder);
            SpellStep(match, GameData.Default.Spell("Disintegrate").CastTime + .001);
            TestAssert.True(FlightLaunched(match, "Annie", holder.NetId), $"{team}: Annie Q is in flight");
            var missile = match.Runtime.Spells.SharedProjectiles
                .Single(projectile => projectile.Snapshot.TargetNetId == holder.NetId).Snapshot.MissileNetId;
            TestAssert.True(missile != 0, $"{team}: the flight owns a client missile");
            match.Runtime.Delivery.Flush(); match.Clear();

            TestAssert.True(match.TryCast(1, 4, holder), $"{team}: Zhonya's Hourglass activates");
            TestAssert.False(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.TargetNetId == holder.NetId), $"{team}: the Q flight is retired");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missile),
                $"{team}: the client missile is destroyed at activation");
            SpellStep(match, 1.0);
            TestAssert.Near(5000.0, holder.Health, 0, $"{team}: nothing lands on the holder");
        }
    }

    [Check("items-live-zhonya-cancels-launched-basic-attacks")]
    public static void ZhonyasActivationCancelsLaunchedBasicAttackMissiles()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var holder = match.Hero(1);
            holder.Inventory = [new InventorySlot(3157), null, null, null, null, null];
            holder.MaxHealth = holder.Health = 5000.0;
            match.Attack(0, holder);
            PendingHit? shot = null;
            for (var tick = 0; tick < 90 && shot is null; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                shot = match.Runtime.Combat.PendingHits.Cast<PendingHit?>().FirstOrDefault(hit =>
                    hit!.Value.TargetNetId == holder.NetId && hit.Value.MissileNetId != 0 &&
                    hit.Value.WindupRemaining <= 0.0);
            }
            TestAssert.True(shot is not null, $"{team}: a basic-attack missile is in flight");
            var health = holder.Health;
            match.Runtime.Delivery.Flush(); match.Clear();

            TestAssert.True(match.TryCast(1, 4, holder), $"{team}: Zhonya's Hourglass activates");
            TestAssert.False(match.Runtime.Combat.PendingHits.Any(hit =>
                hit.TargetNetId == holder.NetId && hit.MissileNetId != 0),
                $"{team}: the launched shot is cancelled");
            TestAssert.Near(health, holder.Health, 0, $"{team}: no damage after activation");
        }
    }

    [Check("items-live-zhonya-activation-stops-janna-monsoon")]
    public static void ZhonyasActivationStopsChannelsAtOnce()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team, ally: true);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
            match.Cast(0, 3, hero);
            Advance(match, .3);
            TestAssert.True(JannaChannel(match, hero) is not null, $"{team}: Monsoon is channelling");
            match.Cast(0, 4, hero);
            TestAssert.True(JannaChannel(match, hero) is null, $"{team}: activation ends the channel before any tick");
        }
    }

    // Deathfire/Gunblade missiles belong to the item runtime, which retires its own.
    [Check("items-live-zhonya-destroys-item-missiles")]
    public static void ZhonyasActivationDestroysGunbladeMissile()
    {
        using var match = new Match("Annie", Team.Order);
        var caster = match.Hero(0);
        var holder = match.Hero(1);
        caster.Inventory = [new InventorySlot(3146), null, null, null, null, null];
        holder.Inventory = [new InventorySlot(3157), null, null, null, null, null];
        holder.MaxHealth = holder.Health = 5000.0;
        match.Clear();
        match.Cast(0, 4, holder);
        var missile = Decode(match.Captures[0].Packets.Last(packet => Opcode(packet) == 0x41))
            .GetProperty("cast_info").GetProperty("missile_netid").GetUInt32();
        match.Clear();
        TestAssert.True(match.TryCast(1, 4, holder), "Zhonya's Hourglass activates");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x5D &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missile),
            "the Gunblade missile is destroyed at activation, not at its arrival tick");
        for (var tick = 0; tick < 20; tick++) match.Runtime.Tick(1.0 / 30.0);
        TestAssert.Near(5000.0, holder.Health, 0, "the retired missile deals nothing");
    }
}
