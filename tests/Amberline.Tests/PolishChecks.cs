using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static class PolishChecks
{
    [Check("polish.b4-clone-item-auras-owner-range-death")]
    public static void CopiedCloneItemsSupplyRetailAuras()
    {
        foreach (var (item, aura, enemyAura) in new[]
        {
            (3105, "AegisoftheLegionAuraFriend", false),
            (3037, "ManaManipulatorAuraFriend", false),
            (3050, "RallyingBannerAuraFriend", false),
            (3050, "RallyingBanner", true),
            (3097, "EmblemOfValor", false),
            (3099, "SoulShroudAuraFriend", false),
            (3152, "WillOfTheAncientsFriendly", false),
            (3001, "AbyssalScepterAura", true),
            (3110, "FrozenHeartAura", true),
        })
        {
            var m = new Match("Shaco", secondTeam: enemyAura ? Team.Chaos : Team.Order);
            using var runtime = m.Runtime;
            var owner = m.Hero(0);
            var recipient = m.Hero(1);
            owner.Position = new Position2(9000, 7000);
            recipient.Position = new Position2(7000, 7000);
            owner.Inventory = [new(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            runtime.HeroStats.Refresh(recipient);
            var armor = recipient.Armor;
            var mr = recipient.SpellBlock;
            var ad = recipient.AttackDamage;
            var clone = runtime.Clones.Spawn(owner, owner, new Position2(7100, 7000),
                CloneKind.ShacoClone, owner.Health, "HallucinateFull", 18, "Shaco");
            // Batch 146: the clone runs Pet.lua, whose TimerScanDistance sends a pet standing beyond
            // GetPetReturnRadius back to its owner (AI_PET_RETURN) - left alone, it walks home from
            // 9000 through the recipient and renews the aura for the whole walk. This scene is about
            // the item auras, so the clone is parked on the script's own hard stop (ORDER_PETHARDSTOP
            // -> AI_PET_HARDSTOP), the state the pet timers leave alone.
            runtime.Pets.Command(owner, 11, clone.Position, actorNetId: clone.NetId);
            TestAssert.True(Inventory.HasItem(clone.Inventory, item), "SharedCloneService copies the aura item");
            // Batch 145 step 2: the aura is no longer the first sync tick's work. The shipped Items/<id>.luaobj
            // re-adds it from BBExecutePeriodically with ExecuteImmediately false, so the first copy lands one period
            // after the clone's own inventory binds (1.0 s for these, 0.9 s for 3001 and 3110) and each renewal buys
            // the next period again.
            runtime.Tick(1.15);
            var buff = recipient.Buffs.Single(b => b.Name == aura);
            TestAssert.Equal(clone.NetId, buff.CasterNetId, "the out-of-range owner cannot supply this aura");
            if (item == 3037)
                // The pet arm of 3037's loop is its own 4.1 s (the hero arm re-adds 1.1 s): the sample is one frame or
                // so after the add, so the number is the script's duration and not an exact countdown.
                TestAssert.Near(4.1, buff.ExpiresAt, 0.2, "pet Mana Manipulator duration");
            if (item == 3105)
            {
                TestAssert.Near(armor + 12, recipient.Armor, 1e-9);
                TestAssert.Near(mr + 15, recipient.SpellBlock, 1e-9);
                TestAssert.Near(ad + 8, recipient.AttackDamage, 1e-9);
            }
            owner.Inventory = Inventory.Empty();
            runtime.HeroStats.Refresh(owner);
            owner.Position = new Position2(7200, 7000);
            runtime.Tick(.05);
            TestAssert.False(owner.Buffs.Any(b => b.Name == aura), "pet ally branches exclude their owner");
            clone.Position = new Position2(9000, 7000);
            // Out of range, the periodic block still runs and simply finds nobody: the held copy lapses on its own
            // duration (4.1 s for 3037's pet arm, 1.0-1.2 s for the rest) with nothing renewing it.
            runtime.Tick(5.0);
            TestAssert.False(recipient.Buffs.Any(b => b.Name == aura), "aura expires after clone leaves range");
            if (item == 3105)
            {
                TestAssert.Near(armor, recipient.Armor, 1e-9);
                TestAssert.Near(mr, recipient.SpellBlock, 1e-9);
                TestAssert.Near(ad, recipient.AttackDamage, 1e-9);
            }
            clone.Position = new Position2(7100, 7000);
            // Back in range it is the next periodic fire that finds the recipient again, not the walk itself.
            runtime.Tick(1.15);
            TestAssert.True(recipient.Buffs.Any(b => b.Name == aura));
            TestAssert.True(runtime.Combat.Kill(clone, 0));
            // A clone never returns from death, so its copies die with it (BbItemModule.Clear from the death hook)
            // and the last renewal lapses the same way an out-of-range one does.
            runtime.Tick(5.0);
            TestAssert.False(recipient.Buffs.Any(b => b.Name == aura), "dead clone stops renewing the aura");
        }
    }

    [Check("polish.heal-clarity-recipient-effects")]
    public static void RecipientEffects()
    {
        foreach (var (summoner, effect) in new[] { ("SummonerHeal", "Meditate_eff.troy"),
                     ("SummonerMana", "Summoner_Mana.troy") })
        {
            var m = new Match(summoner: summoner, secondTeam: Team.Order);
            TestAssert.True(m.Cast(0, m.Hero(0), summoner: true));
            foreach (var hero in new[] { m.Hero(0), m.Hero(1) })
                TestAssert.Equal(1, m.Captures[0].Packets.Count(p => IsEffect(p, effect, hero.NetId)));
            TestAssert.Equal(1, m.Captures[0].Packets.Count(p => IsEffect(p, "Summoner_Cast.troy", m.Hero(0).NetId)));
        }
    }

    private static bool IsEffect(PlaintextPacket packet, string effect, uint bind) =>
        packet.Plaintext.Span[0] == 0x8C &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
            SpellPackets.BuffNameHash(effect.EndsWith(".troy", StringComparison.OrdinalIgnoreCase) ? effect[..^5] : effect) &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == bind;

    [Check("polish.yorick-aura-renew-packet")]
    public static void AuraRenewal()
    {
        var m = new Match("Yorick"); var enemy = m.Hero(1);
        TestAssert.True(m.Cast(1, enemy));
        for (var tick = 0; tick < 30; tick++) m.Runtime.Tick(.1);
        // Batch 357: the add and remove carry the binding name (GameData.ClientBuffName), not the script's BuffName "Slow" that
        // batch 181 sent; the client builds the aura from Data\Spells\YorickDecayedAuraSlow.luaobj.
        var auraHash = SpellPackets.BuffNameHash("YorickDecayedAuraSlow");
        // 19 October 2026 (batch 109, row 21) recorded the wire as one 0x7F and one 0xBF per pulse: the shipped
        // YorickDecayedDiseaseCloud adds YorickDecayedAuraSlow every 0.5 s with BUFF_REPLACE_EXISTING, and a
        // replacement removed the held instance and added a new one (BuffLifecycle.AddBuff), six pulses in three
        // seconds. Batch 157 changed both halves of that wire and this expectation with them. A BUFF_REPLACE_EXISTING
        // add of a live buff is a change of that live instance, not a new one: the removal is withheld (the client has
        // a replacement packet for exactly this) and the add goes out as the renewal the client's HUD needs. That
        // renewal is the silent NpcBuffUpdateCount (0x1F: slot @5, count @6, duration @7, running @11, caster
        // @15) for the same reason the aura items' is: YorickDecayedAuraSlow's own template hands the client a torch
        // (AutoBuffActivateEffect='GLOBAL_SLOW.TROY'), and NpcBuffReplace (0x32) ends in
        // the client's torch restart, which deactivates the instance's auto-effect particles and
        // re-creates them — one re-light per half-second pulse is the aura's "disappears, appears" shape. The 0x1F's
        // handler feeds the count and the duration to the client's resize routine, which
        // restarts the slot's timers and leaves live particles alone. The pulses still carry the script's one-second
        // duration on the wire: a renewal that changes what the client shows must still refresh the HUD timer, and a
        // renewal that left it running out would be a regression even with no torch blink.
        // Reconstruction: retail's server buff manager is not in the client binary; the first pulse places the buff
        // and the rest renew it in place, which is what the shipped BUFF_REPLACE_EXISTING ask means over the wire.
        // Batch 181: the cloud also adds YorickDecayedSlow (a 1.5 s slow, slot 2 in this run, expired before the end),
        // and batch 357 sends each under its own binding name, so the aura is matched by its name and its live slot.
        var auraSlot = enemy.Buffs.Single(b => b.Name == "YorickDecayedAuraSlow").Slot;
        var adds = m.Captures[1].Packets.Where(p => p.Plaintext.Span[0] == 0xBF &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[9..]) == auraHash && p.Plaintext.Span[5] == auraSlot).ToArray();
        TestAssert.Equal(1, adds.Length, "the cloud's first pulse places the aura slow and the rest renew it in place");
        var slot = adds[0].Plaintext.Span[5];
        var renewals = m.Captures[1].Packets.Where(p => p.Plaintext.Length >= 19 && p.Plaintext.Span[0] == 0x1F &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == enemy.NetId &&
            p.Plaintext.Span[5] == slot).ToArray();
        TestAssert.True(adds.Length + renewals.Length >= 5,
            "the cloud pulses every half second: " + (adds.Length + renewals.Length));
        TestAssert.True(renewals.All(p =>
                Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(p.Plaintext.Span[7..]) - 1.0f) < 0.05f),
            "every pulse carries the script's one-second duration (the HUD timer must not run out)");
        TestAssert.Equal(1, enemy.Buffs.Count(b => b.Name == "YorickDecayedAuraSlow"), "one live instance");
        TestAssert.Equal(0, m.Captures[1].Packets.Count(p => p.Plaintext.Span[0] == 0x7F &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == enemy.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == auraHash && p.Plaintext.Span[5] == auraSlot),
            "a replacement of a live buff withholds the removal (batch 157 part 3)");
        TestAssert.Equal(0, m.Captures[1].Packets.Count(p => p.Plaintext.Span[0] == 0x32 &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == enemy.NetId),
            "a keep-alive pulse must not re-light the client's torch (the torch restart)");
        enemy.Position = new(10000, 10000);
        for (var tick = 0; tick < 12; tick++) m.Runtime.Tick(.1);
        TestAssert.False(enemy.Buffs.Any(b => b.Name == "YorickDecayedAuraSlow"));
        TestAssert.Equal(1, m.Captures[1].Packets.Count(p => p.Plaintext.Span[0] == 0x7F &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == enemy.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == auraHash && p.Plaintext.Span[5] == auraSlot),
            "the expiry of the one live instance emits one removal");

        // Separate owners retain independently expiring instances and slots.
        var first = m.Runtime.Buffs.AddBuff(enemy, "YorickDecayedAuraSlow", BuffType.Slow, 1,
            m.Hero(0).NetId, new BuffPayload { PerCaster = true });
        var second = m.Runtime.Buffs.AddBuff(enemy, "YorickDecayedAuraSlow", BuffType.Slow, 2,
            enemy.NetId, new BuffPayload { PerCaster = true });
        TestAssert.True(first is not null && second is not null && first.Slot != second.Slot);
        m.Runtime.Buffs.TickBuffs(enemy, 1.1);
        TestAssert.True(enemy.Buffs.Contains(second!));
        TestAssert.False(enemy.Buffs.Contains(first!));
    }

    [Check("polish.item-and-annie-recap-origins")]
    public static void RecapOrigins()
    {
        foreach (var item in new[] { false, true })
        {
            var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
            if (item) { hero.Inventory = [new(3128), null, null, null, null, null]; m.Runtime.HeroStats.Refresh(hero); }
            enemy.Health = 1;
            TestAssert.True(m.Cast(item ? 4 : 0, enemy));
            hero.SpellRanks[0] = 5;
            for (var tick = 0; tick < 15; tick++) m.Runtime.Tick(.1);
            TestAssert.False(enemy.Alive);
            var snapshot = m.Runtime.Combat.GetLastDeathRecap(enemy.NetId)!;
            var row = snapshot.Records.Single();
            TestAssert.Equal(item ? DeathRecapEventSource.Item : DeathRecapEventSource.Spell, row.Origin.EventSource);
            TestAssert.Equal(hero.NetId, row.Origin.ParentCasterNetId);
            TestAssert.True(row.TotalDamage > 0 && row.Origin.IsSafe);
            TestAssert.Equal((byte)(item ? 0 : 1), row.Origin.SourceSpellLevel);
            TestAssert.True(m.Captures[1].Packets.Any(p => p.Plaintext.Span[0] == 0x24));
        }
    }

    [Check("polish.malady-tooltip-stacks")]
    public static void MaladyTooltip()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3114), null, null, null, null, null];
        // Batch 149 step 5: Items/3114.luaobj's own ItemOnHitUnit stacks the counter now, so the copy has to be bound
        // to its slot first (BbItemModule.SyncInventory).
        m.Runtime.Tick(0.01);
        m.Runtime.HeroStats.Refresh(hero);
        for (var stack = 1; stack <= 4; stack++)
        {
            m.Clear();
            m.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, 10, DamageType.Physical, DamageSourceKind.Basic);
            m.Runtime.Delivery.Flush();
            var buff = enemy.Buffs.Single(b => b.Name == "MaladyCounter");
            var tooltip = m.Captures[1].Packets.Last(p => p.Plaintext.Span[0] == 0x83 &&
                p.Plaintext.Span[11] == buff.Slot);
            // Script wins (Spells/MaladyCounter.luaobj's OnBuffActivate): the counter carries no stat row at all — it
            // takes Count = BBGetBuffCountFromAll(MaladyCounter) into ResistanceShred = Count * 6 and writes that with
            // BBSetBuffToolTipVar Index 1, which is the first float below. The shred the tooltip describes is the
            // sibling's row: Spells/MaladySpell.luaobj's BuffOnUpdateStats / OnBuffActivate carry the same count as
            // ResistanceShred = -6 * Count through BBIncStat IncFlatSpellBlockMod, so it is MaladySpell's payload on
            // the holder that cuts the resistance (and the live spell block), not MaladyCounter's.
            var shred = enemy.Buffs.Single(b => b.Name == "MaladySpell");
            TestAssert.Near(stack * 6, -shred.Payload.SpellBlock, 0);
            TestAssert.Near(stack * 6, BitConverter.ToSingle(tooltip.Plaintext.Span[12..]), 0);
        }
    }

    [Check("polish.summoner-presentation")]
    public static void SummonerPresentation()
    {
        foreach (var (spell, minimum) in new[] { ("SummonerHeal", 2), ("SummonerMana", 2),
                     ("SummonerBoost", 2), ("SummonerHaste", 1), ("SummonerExhaust", 1) })
        {
            var m = new Match(summoner: spell);
            m.Hero(0).Health -= 100; m.Hero(0).Mana -= 100;
            TestAssert.True(m.Cast(0, m.Hero(1), summoner: true), spell);
            TestAssert.True(m.Captures[0].Packets.Count(p => p.Plaintext.Span[0] == 0x8C) >= minimum, spell);
            m.Clear();
            TestAssert.False(m.Cast(0, m.Hero(1), summoner: true), "Cooldown must reject duplicate activation");
            TestAssert.False(m.Captures[0].Packets.Any(p => p.Plaintext.Span[0] == 0x8C));
        }
    }

    [Check("polish.champion-buff-transfer-and-revive")]
    public static void DeathBuffs()
    {
        var m = new Match(summoner: "SummonerRevive"); var hero = m.Hero(0); var enemy = m.Hero(1);
        // Batch 159 part 2: both transfers are the shipped buff scripts' own BuffOnDeath (Spells/
        // CrestoftheAncientGolem.luaobj 0231-0462 and Spells/BlessingoftheLizardElder.luaobj 0231-0272), which run for
        // the script instances the camp's own character scripts place (CharScriptAncientGolem and
        // CharScriptLizardElder's UpdateSelfBuffActions, 0000-0075). So the killing hero holds them the way a match
        // makes it: through a real camp kill. A hand-built row carries no script instance and runs no death hook at all.
        foreach (var (name, character) in new[]
        {
            ("CrestoftheAncientGolem", "AncientGolem"), ("BlessingoftheLizardElder", "LizardElder"),
        })
        {
            var holder = m.Runtime.World.Register(new Entity(m.Runtime.World.Allocate(UnitType.Monster),
                EntityKind.Neutral, UnitType.Monster, Team.Neutral, hero.Position)
            {
                Name = character, Alive = true, Created = true, Visible = true,
                Health = 1000.0, MaxHealth = 1000.0,
            });
            TestAssert.True(m.Runtime.Spells.UnitScripts!.BindUnit(holder, character),
                $"{character} binds its shipped character script");
            TestAssert.True(m.Runtime.Combat.Kill(holder, hero.NetId),
                $"{character} death reaches its buff's own BuffOnDeath");
            TestAssert.True(hero.Buffs.Any(b => b.Name == name),
                $"the killing hero holds {name}");
        }
        TestAssert.True(m.Runtime.Combat.Kill(hero, enemy.NetId));
        foreach (var name in new[] { "CrestoftheAncientGolem", "BlessingoftheLizardElder" })
        {
            TestAssert.False(hero.Buffs.Any(b => b.Name == name));
            TestAssert.Near(150, enemy.Buffs.Single(b => b.Name == name).ExpiresAt, 1e-6);
        }
        m.Runtime.Combat.ScheduleRespawn(hero);
        m.Runtime.Buffs.AddBuff(hero, "HasBeenRevived", BuffType.CombatEnhancer, 300, hero.NetId);
        TestAssert.True(m.Cast(0, hero, summoner: true));
        TestAssert.True(hero.Alive);
    }

    [Check("polish.yorick-spellblade-and-aura")]
    public static void YorickHitAndAura()
    {
        var m = new Match("Yorick"); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3057), null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        // Batch 147: Sheen's arm is Items/3057.luaobj's own ItemOnSpellCast now, and its copy binds to the
        // inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does.
        m.Runtime.Tick(.01);
        TestAssert.True(m.Cast(0, hero)); m.Runtime.Tick(.4);
        TestAssert.True(hero.Buffs.Any(b => b.Name == "Sheen"));
        m.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic);
        TestAssert.False(hero.Buffs.Any(b => b.Name is "Sheen" or "YorickSpectral"));
    }

    [Check("polish.full-inventory-elixir-purchase")]
    public static void FullInventoryElixirs()
    {
        foreach (var (id, buff) in new[] { (2037, "PotionOfGiantStrength"), (2038, "PotionOfElusiveness"),
                     (2039, "PotionOfBrilliance"), (2042, "OracleElixirSight") })
        {
            var m = new Match(); var hero = m.Hero(0);
            hero.Position = new(0, 1000); hero.Gold = 10000;
            hero.Inventory = Enumerable.Repeat<InventorySlot?>(new(1036), 6).ToArray();
            var cost = Inventory.TotalCost(new ItemCatalog(), id);
            TestAssert.Equal("inventory_full", Inventory.Buy(new ItemCatalog(), hero.Inventory,
                hero.Gold, id, MapItemRules.LoadMap1(GameData.Default.Paths)).Reason, $"elixir {id}");
            var request = new byte[9]; request[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(5), (uint)id);
            m.Send(request);
            TestAssert.Near(10000 - cost, hero.Gold, 1e-6, $"elixir {id}");
            TestAssert.True(hero.Inventory.All(s => s?.ItemId == 1036));
            TestAssert.True(hero.Buffs.Any(b => b.Name == buff));
            var purchase = m.Captures[0].Packets.Single(p => p.Plaintext.Span[0] == 0x72);
            TestAssert.Equal((byte)7, purchase.Plaintext.Span[5], "Use the native temporary slot, preserving visible client equipment");
            TestAssert.Equal((byte)1, purchase.Plaintext.Span[11]);
            hero.Gold = 0; m.Clear(); var previous = hero.Buffs.Single(b => b.Name == buff);
            m.Send(request);
            TestAssert.Near(0, hero.Gold, 0);
            TestAssert.True(ReferenceEquals(previous, hero.Buffs.Single(b => b.Name == buff)));
        }
    }

    [Check("polish.enemy-equipment-reveal")]
    public static void EnemyEquipment()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        enemy.Position = new(13000, 13000); m.Runtime.Delivery.Flush(); m.Clear();
        enemy.Inventory = [new(3057), null, null, null, null, null];
        m.Runtime.Delivery.Enqueue(ItemsPackets.BuyItem(enemy.NetId, 0, 3057, 1, false));
        m.Runtime.Delivery.Flush();
        TestAssert.False(m.Captures[0].Packets.Any(p => p.Plaintext.Span[0] == 0x72));
        enemy.Position = hero.Position + new Position2(100, 0); m.Clear(); m.Runtime.Delivery.Flush();
        var equipment = m.Captures[0].Packets.Where(p => p.Plaintext.Span[0] is 0x56 or 0x0B &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == enemy.NetId).ToArray();
        TestAssert.Equal(6, equipment.Length);
        TestAssert.Equal(1, equipment.Count(p => p.Plaintext.Span[0] == 0x56));
    }

    [Check("polish.deathfire-missile-impact")]
    public static void Deathfire()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3128), null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero); var hp = enemy.Health;
        TestAssert.True(m.Cast(4, enemy));
        TestAssert.Near(hp, enemy.Health, 1e-6, "Damage waits for missile arrival");
        m.Runtime.Tick(.25);
        TestAssert.True(enemy.Health < hp);
        TestAssert.True(m.Captures[0].Packets.Any(p => p.Plaintext.Span[0] == 0x8C));
        var after = enemy.Health; m.Runtime.Tick(.25);
        TestAssert.True(enemy.Health >= after, "One missile must apply damage once");
    }

    [Check("polish.moving-melee-target")]
    public static void Chase()
    {
        foreach (var speed in new[] { 100.0, 200.0 })
        {
            var m = new Match("Yorick"); var hero = m.Hero(0); var enemy = m.Hero(1);
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            enemy.Position = hero.Position + new Position2(CombatService.AttackReach(hero, enemy), 0);
            enemy.Speed = speed; m.Runtime.Movement.SetMoveOrder(enemy.NetId, new(10500, 7000));
            m.Runtime.Spells.HeroOrders!.StartAttack(hero, enemy);
            var hp = enemy.Health;
            for (var tick = 0; tick < 240; tick++) m.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(enemy.Health < hp, $"Chase must land an attack against speed {speed}");
        }
    }

    [Check("polish.guardian-revival-lifecycle")]
    public static void GuardianRevival()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3026), null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        // Batch 149: the copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory), and only
        // then does Items/3026.luaobj's UpdateSelfBuffActions keep WillRevive alive.
        m.Runtime.Tick(0.01);
        m.Runtime.Buffs.AddBuff(hero, "CrestoftheAncientGolem", BuffType.CombatEnhancer, 70, hero.NetId);
        m.Clear();
        m.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True);
        TestAssert.True(hero.Alive && hero.Buffs.Any(b => b.Name == "GuardianAngel"));
        TestAssert.False(enemy.Buffs.Any(b => b.Name == "CrestoftheAncientGolem"), "GA is not a completed death");
        m.Runtime.Delivery.Flush();
        TestAssert.True(m.Captures[0].Packets.Any(p => System.Text.Encoding.ASCII.GetString(p.Plaintext.Span).Contains("Death")));
        m.Runtime.Tick(4.1);
        TestAssert.True(hero.Alive && hero.Health >= Math.Min(750, hero.MaxHealth) && hero.Buffs.Any(b => b.Name == "HasBeenRevived"));
        TestAssert.False(hero.Buffs.Any(b => b.Name == "GuardianAngel"));
        TestAssert.True(m.Captures[0].Packets.Any(p => System.Text.Encoding.ASCII.GetString(p.Plaintext.Span).Contains("Idle1")));
    }

    // Audit of 19 October 2026 (W19d): Items/3057 and the Tear scripts do nothing for a spell whose script says DoesntTriggerSpellCasts.
    [Check("polish.spells-that-do-not-trigger-spell-casts-leave-sheen-and-tear-alone")]
    public static void FlaggedSpellsDoNotArmSheenOrSpendTearCharges()
    {
        // Sivir's Spiral Blade (Q) is flagged; Annie's Disintegrate is not.
        // Batch 147: the arm is Items/3057.luaobj's own ItemOnSpellCast, so the copy has to reach the
        // inventory slot first (BbItemModule.SyncInventory, one spell update) before either cast.
        var sivir = new Match("Sivir"); var hero = sivir.Hero(0);
        hero.Inventory = [new(3057), new(3070), null, null, null, null];
        sivir.Runtime.HeroStats.Refresh(hero);
        sivir.Runtime.Tick(0.01);
        TestAssert.True(sivir.Cast(0, sivir.Hero(1)), "Spiral Blade is cast");
        TestAssert.False(hero.Buffs.Any(b => b.Name == "Sheen"), "Spiral Blade does not arm Sheen");
        TestAssert.False(hero.Buffs.Any(b => b.Name == "SheenDelay"), "nor start its delay");

        var annie = new Match(); var caster = annie.Hero(0);
        caster.Inventory = [new(3057), null, null, null, null, null];
        annie.Runtime.HeroStats.Refresh(caster);
        annie.Runtime.Tick(0.01);
        TestAssert.True(annie.Cast(0, annie.Hero(1)), "Disintegrate is cast");
        TestAssert.True(caster.Buffs.Any(b => b.Name == "Sheen"), "an unflagged spell arms Sheen as before");
    }

    // Batch 708 (owner, 7 November 2026: "Tear stacking ... not working on Yorick"): a Health Potion drunk from inventory slot 0 is
    // flagged DoesntTriggerSpellCasts (Spells/RegenerationPotion.luaobj). The item module's router kept one SpellVars table per
    // (hero, slot number) and only copied a spell's globals over it, so the potion's true stayed in slot 0's table; Yorick's Omen of
    // War (YorickSpectral.luaobj, no such global) then read it, and TearOfTheGoddessTrack's BuffOnSpellCast and Items/3057's
    // ItemOnSpellCast ignored the cast. Annie's and Ryze's spells set the global to false themselves, which hid it there.
    [Check("polish.tear-and-sheen-hear-a-spell-cast-after-a-potion-from-its-slot")]
    public static void TearAndSheenHearASpellAfterAPotionFromItsSlot()
    {
        var m = new Match("Yorick"); var hero = m.Hero(0);
        hero.Inventory = [new(2003), new(3070), new(3057), null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        // Items/3070 adds TearOfTheGoddessTrack after 0.95 s; its reservoir then fills to two charges (one per 3 s).
        m.Runtime.Tick(5);
        hero.Health = hero.MaxHealth * 0.5;
        TestAssert.True(m.Cast(4, hero), "the Health Potion in inventory slot 0 is drunk");
        TestAssert.True(hero.Buffs.Any(b => b.Name == "RegenerationPotion"), "the potion's buff is on");
        m.Runtime.Tick(1);
        var before = hero.MaxMana;
        hero.Mana = hero.MaxMana;
        TestAssert.True(m.Cast(0, m.Hero(1)), "Omen of War (slot 0) is cast");
        m.Runtime.Tick(0.5);
        TestAssert.Near(before + 4.0, hero.MaxMana, 1e-6, "Tear adds 4 maximum mana for Omen of War");
        TestAssert.True(hero.Buffs.Any(b => b.Name == "Sheen"), "Omen of War arms Sheen");
    }

    // Batch 708: the same leak through the summoner slots. A summoner cast crosses every router's cast hooks with its own slot number
    // (0 for D, 1 for F), so Flash (SummonerFlash.luaobj, DoesntTriggerSpellCasts = true) left its true in slot 0's table of the item
    // module's router and Heal in slot 1's: Yorick's Q and W stopped feeding Tear for the rest of the game.
    [Check("polish.tear-hears-a-spell-cast-after-a-summoner-spell-of-the-same-slot-number")]
    public static void TearHearsASpellAfterASummonerOfTheSameSlotNumber()
    {
        var m = new Match("Yorick"); var hero = m.Hero(0);
        hero.Inventory = [new(3070), null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        m.Runtime.Tick(5);
        TestAssert.True(m.Cast(0, m.Hero(1), summoner: true), "Flash (summoner slot 0) is cast");
        hero.Health = hero.MaxHealth * 0.5;
        TestAssert.True(m.Cast(1, hero, summoner: true), "Heal (summoner slot 1) is cast");
        m.Runtime.Tick(1);
        var before = hero.MaxMana;
        hero.Mana = hero.MaxMana;
        TestAssert.True(m.Cast(0, m.Hero(1)), "Omen of War (slot 0) is cast");
        TestAssert.True(m.Cast(1, m.Hero(1)), "Omen of Pestilence (slot 1) is cast");
        m.Runtime.Tick(1);
        TestAssert.Near(before + 8.0, hero.MaxMana, 1e-6, "Tear adds 4 maximum mana for each of the two casts");
    }

    [Check("polish.guardian-angel-leaves-non-dispellable-elixirs-and-oracle")]
    public static void GuardianAngelKeepsElixirsAndOracleBuffs()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Position = new(0, 1000); hero.Gold = 20000;
        // A bought elixir is drunk at once only when the inventory has no free slot (ShopService: the useOnBought purchase of a full
        // inventory); otherwise it waits in the inventory to be used.
        hero.Inventory = [new(3026), new(1001), new(1001), new(1001), new(1001), new(1001)];
        foreach (var id in new[] { 2037, 2038, 2039, 2042 })
        {
            var request = new byte[9]; request[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(5), (uint)id);
            m.Send(request);
        }
        string[] names = ["PotionOfGiantStrength", "PotionOfElusiveness", "PotionOfBrilliance", "OracleElixirSight"];
        TestAssert.True(names.All(name => hero.Buffs.Any(b => b.Name == name && b.Payload.NonDispellable)), "the elixirs are non-dispellable");
        // Batch 149: the copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory); only then does
        // Items/3026.luaobj keep WillRevive alive, whose BuffOnPreDamage is the revive.
        m.Runtime.Tick(0.01);
        m.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True);
        TestAssert.True(hero.Alive && hero.Buffs.Any(b => b.Name == "GuardianAngel"), "Guardian Angel revives");
        TestAssert.True(names.All(name => hero.Buffs.Any(b => b.Name == name)), "its clear by type leaves the non-dispellable buffs");
    }

    [Check("polish.elixir-redrink-renews-the-timer-and-keeps-the-first-level")]
    public static void ElixirRedrinkKeepsTheAmountOfTheFirstDrink()
    {
        var m = new Match(); var hero = m.Hero(0);
        hero.Position = new(0, 1000); hero.Gold = 20000; hero.Level = 1;
        // A full inventory, so that the purchase is drunk at once (see the Guardian Angel check above).
        hero.Inventory = [new(1001), new(1001), new(1001), new(1001), new(1001), new(1001)];
        void Buy()
        {
            var request = new byte[9]; request[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(5), 2037u);
            m.Send(request);
        }
        // Batch 149 step 3: the elixir is Spells/PotionOfGiantStrength.luaobj's own TargetExecute (one
        // BUFF_RENEW_EXISTING row of 240 s) and its own OnBuffActivate, which writes InstanceVars.BonusHealth
        // (BBGetLevel(Owner) * 5.59 + 134.41) and grants it as IncPermanentFlatHPPoolMod plus 10 attack damage.
        // The amount is a MaxHealth delta now, not a buff payload.
        m.Runtime.HeroStats.Refresh(hero);
        var bare = hero.MaxHealth;
        Buy();
        m.Runtime.HeroStats.Refresh(hero);
        var first = hero.Buffs.Single(b => b.Name == "PotionOfGiantStrength");
        var amount = hero.MaxHealth - bare;
        TestAssert.Near(134.41 + 5.59, amount, 1e-9, "level 1 amount");
        hero.Level = 10; m.Runtime.Tick(100.0);
        var remaining = first.ExpiresAt;
        Buy();
        m.Runtime.HeroStats.Refresh(hero);
        var again = hero.Buffs.Single(b => b.Name == "PotionOfGiantStrength");
        // The permanent accumulator is keyed without the IncPermanent prefix (BbSpellHost.PermanentModifier): FlatHPPoolMod.
        TestAssert.Near(amount,
            hero.PermanentStatMods.TryGetValue("FlatHPPoolMod", out var kept) ? kept : 0.0, 1e-9,
            "BUFF_RENEW_EXISTING does not run OnBuffActivate again: the level-1 amount stays");
        TestAssert.True(again.ExpiresAt > remaining, "the 240 s restart");
    }

    [Check("polish.wits-end-stacks-on-structure-hits")]
    public static void WitsEndMagicResistStacksOnAnyLandedHit()
    {
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [new(3091), null, null, null, null, null];
        // Batch 149 step 5: the counter is Spells/WitsEnd.luaobj's own BuffOnHitUnit now, carried by the WitsEnd
        // window Items/3091.luaobj opens on its OnActivate, so the copy has to be bound to its slot first
        // (BbItemModule.SyncInventory). Items/3091.luaobj has no ItemOnHitUnit hook at all.
        m.Runtime.Tick(0.01);
        m.Runtime.HeroStats.Refresh(hero);
        var inhibitor = m.Runtime.World.Register(new Entity(m.Runtime.World.Allocate(UnitType.Structure), EntityKind.Inhibitor,
            UnitType.Structure, Team.Chaos, new(7100, 7000)) { Health = 10000, MaxHealth = 10000 });
        TestAssert.True(m.Runtime.Combat.ApplyHit(hero.NetId, inhibitor.NetId, 100, DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Equal(1, hero.Buffs.Single(b => b.Name == "WitsEndCounter").Stacks, "a hit on an inhibitor stacks Wit's End");
        TestAssert.Near(10000 - 100, inhibitor.Health, 1e-8, "and adds no 42 magic damage there");
    }

    [Check("polish.force-of-nature-heals-are-script-heals")]
    public static void ForceOfNatureHealIsAnnounced()
    {
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [new(3109), null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        hero.MaxHealth = 4000; hero.Health = 1000; hero.HealthRegen = 0;
        m.Clear();
        m.Runtime.Tick(1.1);
        m.Runtime.Delivery.Flush();
        // One tick of 0.35 % of 4000 = 14 health, announced with UnitApplyHeal (0x84) like BBIncHealth's heals.
        TestAssert.True(hero.Health >= 1000 + 14.0 - 1e-6, "the item restored health");
        TestAssert.True(m.Captures[0].Packets.Any(p => p.Plaintext.Span[0] == 0x84), "and announced it");
    }

    [Check("polish.selling-boots-of-mobility-removes-their-debuff")]
    public static void SellingBootsOfMobilityRemovesTheMobilityDebuff()
    {
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [new(3117), null, null, null, null, null];
        // Batch 147 step 4: Items/3117.luaobj removes the debuff from its own OnDeactivate (BBIfHasBuff then
        // BBSpellBuffRemove on the Owner), so the copy that leaves has to have been bound to the slot first
        // (BbItemModule.SyncInventory, the same next-spell-update latency the other converted item checks account for).
        m.Runtime.Tick(0.01);
        m.Runtime.HeroStats.Refresh(hero);
        m.Runtime.Buffs.AddBuff(hero, "BootsOfMobilityDebuff", BuffType.Internal, 5.0, hero.NetId);
        TestAssert.True(hero.Buffs.Any(b => b.Name == "BootsOfMobilityDebuff"));
        hero.Inventory = [null, null, null, null, null, null];
        m.Runtime.Tick(0.01);
        m.Runtime.HeroStats.Refresh(hero);
        TestAssert.False(hero.Buffs.Any(b => b.Name == "BootsOfMobilityDebuff"), "OnDeactivate of the item removes the debuff");
    }

    [Check("polish.leviathan-cap-is-a-percent-reduction-buff-not-a-hardcoded-multiplier")]
    public static void LeviathanReductionIsThePercentReductionOfItsCapBuff()
    {
        // Batch 149 step 1: the twenty stacks are Spells/LeviathanCheck.luaobj's own BuffOnKill now (two stacks of
        // LeviathanStats per hero kill) and the cap is Items/3138.luaobj's UpdateSelfBuffActions reacting to the
        // count. The hand-written Snowball3138 counter is gone, so the check pays for the stacks with real kills.
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3138), null, null, null, null, null];
        m.Runtime.Tick(1.1);
        m.Runtime.HeroStats.Refresh(hero);
        for (var i = 0; i < 10; i++)
        {
            TestAssert.True(m.Runtime.Combat.Kill(enemy, hero.NetId), "leviathan stack kill " + i);
            // A direct Kill leaves the hero's own respawn timer to the lethal-hit caller (CombatService keeps
            // revival markers ahead of it), so the loop pays for the next stack's victim the way a match's
            // ProcessRespawns would before it can be killed again.
            m.Runtime.Combat.ScheduleRespawn(enemy);
            TestAssert.True(m.Runtime.Combat.RespawnImmediately(enemy), "leviathan stack kill " + i + " respawn");
        }
        m.Runtime.Tick(1.1);
        var stacks = hero.Buffs.FirstOrDefault(b => b.Name == "LeviathanStats");
        TestAssert.True(stacks is not null && stacks.Stacks == 20, "twenty LeviathanStats stacks");
        var cap = hero.Buffs.FirstOrDefault(b => b.Name == "LeviathanCap");
        TestAssert.True(cap is not null, "twenty stacks add LeviathanCap");
        TestAssert.Near(0.15, cap!.Payload.PercentPhysicalReduction, 1e-12);
        TestAssert.Near(0.15, cap.Payload.PercentMagicReduction, 1e-12);
        hero.Health = hero.MaxHealth = 10000;
        var before = hero.Health;
        TestAssert.True(m.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 100, DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near(100.0 * CombatRules.ResistanceMultiplier(hero.Armor) * 0.85, before - hero.Health, 1e-6, "15 % less physical damage");
    }

    [Check("polish.warmog-regeneration-is-flat-regeneration-under-the-percent-multipliers")]
    public static void WarmogRegenerationIsMultipliedByPercentRegeneration()
    {
        // Batch 149 step 1: Warmog's growth is Items/3083.luaobj's own ItemOnKill writing InstanceVars.ExtraRegen,
        // which UpdateSelfBuffStats turns into IncFlatHPRegenMod. The check sets that counter the way the script's
        // kill hook does (the same 2.0 the hand-written WarmogRegen field held).
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [null, null, null, null, null, null];
        m.Runtime.Tick(0.01);
        hero.Inventory = [new(3083), null, null, null, null, null];
        m.Runtime.Tick(0.01);
        m.Runtime.Buffs.AddBuff(hero, "TestRegen", BuffType.CombatEnhancer, 60.0, hero.NetId,
            new BuffPayload { HpRegenMult = 0.5 }, canMitigateDuration: false);
        m.Runtime.HeroStats.Refresh(hero);
        var without = hero.HealthRegen;
        m.Runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(0)!.InstanceVars["ExtraRegen"] = 2.0;
        // Batch 252 (restated from 0.01 s): batch 246 put the item's bare UpdateSelfBuffStats on the holder's 0.25 s poll line (BbSelfUpdate),
        // so the counter reaches the stats at the next line, not on the next step.
        m.Runtime.Tick(0.25);
        m.Runtime.HeroStats.Refresh(hero);
        // Items/3083.luaobj: IncStat IncFlatHPRegenMod, so the +2 joins the flat sum that the +50 % then multiplies.
        TestAssert.Near(2.0 * 1.5, hero.HealthRegen - without, 1e-9, "Warmog's regeneration is scaled by percent regeneration");
    }

    [Check("polish.guinsoos-owner-keeps-the-item-scripts-permanent-bonus-per-copy")]
    public static void GuinsoosPermanentBonusFollowsTheCopies()
    {
        // Batch 147: the bonus is Items/3124.luaobj's own OnBuffActivate/OnBuffDeactivate permanent IncStat pair,
        // and the copy only runs them once its inventory slot is bound (one spell update per change).
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [null, null, null, null, null, null];
        m.Runtime.HeroStats.Refresh(hero);
        var bareAs = hero.AttackSpeedMod;
        hero.Inventory = [new(3124), null, null, null, null, null];
        m.Runtime.Tick(0.01); m.Runtime.HeroStats.Refresh(hero);
        TestAssert.Near(45.0 + 7.0, hero.AbilityPower, 1e-9, "inibin +45 AP plus the script's +7 permanent AP");
        TestAssert.Near(bareAs + 0.04, hero.AttackSpeedMod, 1e-9, "and +4 % attack speed");
        hero.Inventory = [new(3124), new(3124), null, null, null, null];
        m.Runtime.Tick(0.01); m.Runtime.HeroStats.Refresh(hero);
        TestAssert.Near(2 * (45.0 + 7.0), hero.AbilityPower, 1e-9, "every copy runs its own OnBuffActivate");
        TestAssert.Near(bareAs + 0.08, hero.AttackSpeedMod, 1e-9);
        hero.Inventory = [null, null, null, null, null, null];
        m.Runtime.Tick(0.01); m.Runtime.HeroStats.Refresh(hero);
        TestAssert.Near(0.0, hero.AbilityPower, 1e-9, "OnDeactivate takes the bonus back on sale");
        TestAssert.Near(bareAs, hero.AttackSpeedMod, 1e-9);
    }

    [Check("polish.potions-stack-and-continue-then-run-one-after-the-other")]
    public static void PotionsAreStacksAndContinueWithAQueue()
    {
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [new(2003, count: 9), null, null, null, null, null];
        hero.MaxHealth = 5000; hero.Health = 500;
        void Drink() { hero.ItemCooldowns[0] = 0; TestAssert.True(m.Cast(4, hero), "the potion is drunk"); }
        Drink(); m.Runtime.Tick(1.0); Drink();
        var buff = hero.Buffs.Single(b => b.Name == "RegenerationPotion");
        TestAssert.Equal(2, buff.Stacks, "second potion stacks");
        TestAssert.True(buff.ExpiresAt < 19.5, "BUFF_STACKS_AND_CONTINUE leaves the running 20 s timer alone");
        m.Runtime.Tick(19.5);
        // Batch 149 step 3: Spells/RegenerationPotion.luaobj's own OnBuffDeactivate re-adds the queue as a fresh
        // row (BUFF_STACKS_AND_RENEWS, CharVars.countHealthPotion - 1 stacks, 20 s) after the window's row has
        // left, so each window is a new RegenerationPotion instance and not the same buff continued.
        var next = hero.Buffs.Single(b => b.Name == "RegenerationPotion");
        TestAssert.False(ReferenceEquals(next, buff), "the next window is a fresh RegenerationPotion row");
        TestAssert.True(next.Stacks == 1 && next.ExpiresAt > 19.0,
            "OnBuffDeactivate re-adds count - 1 stacks for a fresh 20 s");
        m.Runtime.Tick(20.5);
        TestAssert.False(hero.Buffs.Any(b => b.Name == "RegenerationPotion"), "the last window ends the buff");

        // MaxStack 5: a sixth potion changes nothing but is still used up.
        for (var i = 0; i < 5; i++) Drink();
        buff = hero.Buffs.Single(b => b.Name == "RegenerationPotion");
        var remaining = buff.ExpiresAt;
        var count = hero.Inventory[0]!.Count;
        Drink();
        TestAssert.Equal(5, buff.Stacks, "five stacks at most");
        TestAssert.Near(remaining, buff.ExpiresAt, 1e-9, "and the cap leaves the timer alone");
        TestAssert.Equal(count - 1, hero.Inventory[0]!.Count, "the potion is still consumed");
    }

    [Check("items-live-three-potions-run-three-windows-one-after-the-other")]
    public static void ThreePotionsRunThreeWindows()
    {
        // Batch 555 (batch 107 Q3, three or more potions): Spells/RegenerationPotion.luaobj's OnBuffDeactivate re-adds
        // CharVars.countHealthPotion - 1 stacks as a fresh 20 s row whenever the count was 2 or more, and
        // Potion_Internal's one-second periodic re-reads that count from the live stacks (BBGetBuffCountFromAll), so three
        // potions drunk in the first window are three 20 s windows, the stack count falling 3, 2, 1, the healing running
        // through all three.
        var m = new Match(); var hero = m.Hero(0);
        hero.Inventory = [new(2003, count: 9), null, null, null, null, null];
        hero.MaxHealth = 5000; hero.Health = 500;
        for (var i = 0; i < 3; i++) { hero.ItemCooldowns[0] = 0; TestAssert.True(m.Cast(4, hero), "the potion is drunk"); }
        int Stacks() => hero.Buffs.Where(b => b.Name == "RegenerationPotion").Sum(b => b.Stacks);
        void Run(double seconds) { for (var t = 0.0; t < seconds - 1e-9; t += 0.25) m.Runtime.Tick(0.25); }
        TestAssert.Equal(3, Stacks(), "three potions are three stacks of one row");
        Run(10.0);
        TestAssert.Equal(3, Stacks(), "first window");
        Run(20.0);
        TestAssert.Equal(2, Stacks(), "second window: count - 1 stacks");
        Run(20.0);
        TestAssert.Equal(1, Stacks(), "third window");
        var before = hero.Health;
        Run(5.0);
        TestAssert.True(hero.Health - before >= 45.0, $"Potion_Internal still heals 10 a second in the third window ({hero.Health - before:0.0})");
        Run(7.0);
        TestAssert.Equal(0, Stacks(), "the third window is the last");
        TestAssert.False(hero.Buffs.Any(b => b.Name == "Potion_Internal"), "and the healing buff has left");
    }

    [Check("items-live-potion-drunk-as-the-last-one-expires-runs-its-full-window")]
    public static void PotionDrunkAsTheLastOneExpiresRunsItsFullWindow()
    {
        // Batch 703 (community report, 7 November 2026: "Consuming a pot right as one expires just makes it disapear"). The second
        // potion is drunk one step before the first one's 20 s window ends, in the step it ends, and one step after. Either way
        // the potion is used up and its window runs its whole 20 s: Spells/RegenerationPotion.luaobj stacks it onto the live row
        // (BUFF_STACKS_AND_CONTINUE) and the row's OnBuffDeactivate re-adds CharVars.countHealthPotion - 1 stacks for 20 s, or
        // TargetExecute adds a fresh row when none is left; Potion_Internal heals 10 a second while a RegenerationPotion row is up
        // (5 mana for FlaskOfCrystalWater). The lost potion was TargetExecute's count read before its own adds had landed (batch 703).
        foreach (var (item, name) in new[] { (2003, "RegenerationPotion"), (2004, "FlaskOfCrystalWater") })
        foreach (var stepsLeft in new[] { 2, 1, 0 })
        {
            var m = new Match(); var hero = m.Hero(0);
            hero.Inventory = [new(item, count: 5), null, null, null, null, null];
            hero.MaxHealth = 5000; hero.Health = 500; hero.Mana = 10;
            double Restored() => item == 2003 ? hero.Health : hero.Mana;
            var label = $"{name}, {stepsLeft} steps left";
            hero.ItemCooldowns[0] = 0; TestAssert.True(m.Cast(4, hero), $"{label}: the first potion is drunk");
            BuffState? Row() => hero.Buffs.FirstOrDefault(b => b.Name == name);
            while (Row() is { } row && row.ExpiresAt > stepsLeft * 0.25 + 1e-9) m.Runtime.Tick(0.25);
            TestAssert.True(stepsLeft == 0 ? Row() is null : Row() is not null, $"{label}: the first window is where it should be");
            var count = hero.Inventory[0]!.Count;
            hero.ItemCooldowns[0] = 0; TestAssert.True(m.Cast(4, hero), $"{label}: the second potion is drunk");
            TestAssert.Equal(count - 1, hero.Inventory[0]!.Count, $"{label}: the second potion is used up");
            var before = Restored();
            for (var i = 0; i < 78; i++) m.Runtime.Tick(0.25); // 19.5 s
            TestAssert.True(Row() is not null, $"{label}: the second potion's window is still up 19.5 s later");
            TestAssert.True(hero.Buffs.Any(b => b.Name == "Potion_Internal"), $"{label}: and so is Potion_Internal");
            var perSecond = item == 2003 ? 10.0 : 5.0;
            TestAssert.True(Restored() - before >= 18 * perSecond, $"{label}: Potion_Internal restored {perSecond} a second ({Restored() - before:0.0})");
            for (var i = 0; i < 8; i++) m.Runtime.Tick(0.25); // 21.5 s
            TestAssert.True(Row() is null, $"{label}: one potion is one window");
        }
    }

    [Check("polish.ionic-spark-counter-counts-every-hit-and-overshoots-on-structures")]
    public static void IonicSparkCounterOvershootsOnAStructureHit()
    {
        var m = new Match(); var hero = m.Hero(0); var enemy = m.Hero(1);
        hero.Inventory = [new(3178), null, null, null, null, null];
        // Batch 149 step 5: Items/3178.luaobj's own UpdateSelfBuffActions opens Spells/LightningRodApplicator.luaobj on
        // the poll that binds the copy (ExecuteImmediately true), and the counter this check follows is that window's
        // InstanceVars.AttackCounter — the hand-written HeroItemState.Item3178Stacks is gone.
        m.Runtime.Tick(0.01);
        m.Runtime.HeroStats.Refresh(hero);
        enemy.MaxHealth = enemy.Health = 100000;
        var inhibitor = m.Runtime.World.Register(new Entity(m.Runtime.World.Allocate(UnitType.Structure), EntityKind.Inhibitor,
            UnitType.Structure, Team.Chaos, new(7100, 7000)) { Health = 10000, MaxHealth = 10000 });
        void Hit(Entity target) => TestAssert.True(m.Runtime.Combat.ApplyHit(hero.NetId, target.NetId, 100, DamageType.Physical, DamageSourceKind.Basic));
        double Counter() => ItemScriptState.WindowCounter(m.Runtime, hero, "LightningRodApplicator", "AttackCounter");
        for (var i = 0; i < 3; i++) Hit(enemy);
        TestAssert.Near(3.0, Counter(), 1e-9, "three hits");
        Hit(inhibitor);
        TestAssert.Near(4.0, Counter(), 1e-9, "the structure hit is counted and starts nothing (AttackCounter == 4, not an AI)");
        TestAssert.False(enemy.Buffs.Any(b => b.Name == "LightningRodChain"));
        Hit(enemy);
        TestAssert.Near(5.0, Counter(), 1e-9, "the counter overshoots");
        for (var i = 0; i < 4; i++) Hit(enemy);
        TestAssert.False(enemy.Buffs.Any(b => b.Name == "LightningRodChain"), "and never equals 4 again: the item is dead");
        TestAssert.True(m.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 100000, DamageType.True));
        TestAssert.False(hero.Alive, "the hero dies");
        // The window's own OnBuffActivate zeroes AttackCounter when the copy re-opens it after the death.
        TestAssert.Near(0.0, Counter(), 1e-9, "the applicator buff is rebuilt after a death with AttackCounter 0");
    }

    [Check("combat-turret-acquisition-uses-acquisition-range")]
    public static void TurretAcquisitionUsesAcquisitionRange()
    {
        var m = new Match();
        var turret = m.Runtime.World.Entities.Values.First(e => e.Kind == EntityKind.Turret && e.Team == Team.Chaos);
        // Clear of the Order base's own structures, which are enemies of a Chaos turret too.
        turret.Position = new Position2(11000, 11000); turret.Protected = false;
        turret.AcquisitionRange = 500;
        var minion = m.Runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0, 0);
        minion.Position = turret.Position + new Position2(600, 0);
        TestAssert.True(m.Runtime.Combat.AcquireTurretTarget(turret) is null,
            "a minion 600 units away is outside the turret's 500-unit acquisition range (its attack reach is 750)");
        minion.Position = turret.Position + new Position2(400, 0);
        TestAssert.Equal(minion, m.Runtime.Combat.AcquireTurretTarget(turret),
            "inside the acquisition range the minion is acquired");
    }

    [Check("polish.retaliation-and-pet-priority")]
    public static void Retaliation()
    {
        var m = new Match(); var attacker = m.Hero(0); var victim = m.Hero(1);
        var minion = m.Runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0, 0);
        minion.Position = victim.Position + new Position2(0, 100);
        var turret = m.Runtime.World.Entities.Values.First(e => e.Kind == EntityKind.Turret && e.Team == Team.Chaos);
        turret.Position = victim.Position + new Position2(0, 200); turret.Protected = false;
        var enemyMinion = m.Runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0, 0);
        enemyMinion.Position = turret.Position + new Position2(100, 0);
        var pet = m.Runtime.World.Register(new Entity(m.Runtime.World.Allocate(UnitType.Monster),
            EntityKind.Pet, UnitType.Monster, Team.Order, turret.Position + new Position2(150, 0))
            { OwnerNetId = attacker.NetId, Health = 500, MaxHealth = 500, Speed = 300, Radius = 35 });
        TestAssert.Equal(pet, m.Runtime.Combat.AcquireTurretTarget(turret));
        turret.AttackTarget = enemyMinion.NetId;
        m.Runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.Equal(enemyMinion.NetId, turret.AttackTarget!.Value, "pet arrival does not preempt a valid target");
        // minionLib.OnCallForHelp only answers in ATTACKMOVE/ATTACKING; let the
        // zero-delay TimerMoveForward take the new minion out of its spawn IDLE
        // state first, so the help call competes with its live minion target.
        m.Runtime.Minions!.Tick(.1);
        m.Runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 10, DamageType.True, DamageSourceKind.Basic);
        TestAssert.Equal(attacker.NetId, turret.AttackTarget!.Value);
        m.Runtime.Minions!.Tick(.1);
        TestAssert.Equal(attacker.NetId, minion.AttackTarget!.Value);
    }

    [Check("polish.attack-reveal-timeout")]
    public static void AttackReveal()
    {
        var m = new Match(); var attacker = m.Hero(0); var victim = m.Hero(1);
        TestAssert.True(m.Runtime.Combat.QueueBasicAttack(attacker, victim));
        m.Runtime.Delivery.Flush();
        // The stock client runs its spell-caster reveal itself in its cast frame (the perception-bubble routine that the 0x7B handler
        // also uses), so the server sends no
        // bubble packet for it (A4 audit, 18 Oct 2026); the 400-unit radius and 4.5-second timeout stay server sight.
        TestAssert.False(m.Captures[1].Packets.Any(p => p.Plaintext.Span[0] is 0x7B or 0x38),
            "an attack reveal sends no perception bubble packet");
        // Batch 49 (1ccf9f7): RevealSpellCaster runs at the cast frame, the end of the windup, not when the swing starts.
        TestAssert.True(attacker.AttackWindup > 0.0, "Annie's swing has a windup");
        TestAssert.Near(0, attacker.AttackRevealRemaining[(int)victim.Team], 0, "a swing still in its windup reveals nothing");
        m.Runtime.Combat.CombatTick(attacker.AttackWindup, regenerate: false);
        TestAssert.Near(4.5, attacker.AttackRevealRemaining[(int)victim.Team], 0);
        attacker.AttackTarget = null;

        // playtest B2 OS3, 26 Sept 2026: RevealSpellCaster spawns a fixed
        // 400-unit bubble at the attack point; it does not follow the attacker.
        var view = m.Runtime.Session.Participants[1].View;
        var attackPoint = attacker.Position;
        victim.Position = new Position2(12000, 12000);
        attacker.Position = new Position2(1000, 14000);
        m.Runtime.Delivery.Flush();
        TestAssert.False(view.Visible.Contains(attacker.NetId),
            "an attacker that leaves the attack point is not revealed while the timer runs");
        attacker.Position = attackPoint + new Position2(300, 0);
        m.Runtime.Delivery.Flush();
        TestAssert.True(view.Visible.Contains(attacker.NetId),
            "the attack-point bubble still reveals an attacker within 400 units");

        m.Runtime.Combat.CombatTick(4.49, regenerate: false);
        TestAssert.True(attacker.AttackRevealRemaining[(int)victim.Team] > 0);
        m.Runtime.Delivery.Flush();
        m.Clear();
        m.Runtime.Combat.CombatTick(.02, regenerate: false);
        TestAssert.Near(0, attacker.AttackRevealRemaining[(int)victim.Team], 0);
        m.Runtime.Delivery.Flush();
        TestAssert.False(m.Captures[1].Packets.Any(p => p.Plaintext.Span[0] is 0x7B or 0x38),
            "the timeout removes no bubble: none was sent");
    }

    [Check("polish.dragon-reset-at-home")]
    public static void DragonReset()
    {
        var m = new Match(); var r = m.Runtime; var nav = r.Navigation;
        var wire = new ProtocolNeutralPacketSink(p => r.Delivery.Enqueue(p), r.SyncIds,
            r.World.Allocate, new(nav.MinX, nav.MaxX, nav.MinZ, nav.MaxZ), (x, z) => nav.HeightAt(new(x, z)));
        var scheduler = new JungleScheduler(r.World, r.GameData, wire, navigation: nav);
        scheduler.Start(); scheduler.Tick(1000);
        var ai = new NeutralAiService(r.World, scheduler, r.Movement, r.Combat, wire, r.Effects, r.SpellBuffs);
        var dragon = scheduler.Members.Values.Single(member => member.Entity.Name == "Dragon").Entity;
        var home = dragon.NeutralHome!.Value;
        StartReset(ai, dragon, home, m.Hero(0));
        ArriveHome(ai, dragon, home);
        // playtest #18, 25 Sept 2026: Leashed.luaobj OnStoppedMoving goes idle on
        // arrival (this used to assert a retreat until healed); TimerRegen keeps
        // healing the idle camp until it is re-engaged.
        TestAssert.Equal("idle", dragon.NeutralState, "a returned Dragon goes idle before healing");
        TestAssert.True(dragon.Health < dragon.MaxHealth, "arrival does not wait for healing");
        for (var tick = 0; tick < 12; tick++) ai.Tick(.1);
        TestAssert.Near(dragon.MaxHealth, dragon.Health, 1e-6);
        TestAssert.Equal("idle", dragon.NeutralState);

        StartReset(ai, dragon, home, m.Hero(0));
        ArriveHome(ai, dragon, home);
        TestAssert.Equal("idle", dragon.NeutralState);
        var hero = m.Hero(0);
        ai.OnTakeDamage(dragon, hero);
        TestAssert.Equal("engage", dragon.NeutralState, "an idle camp below max HP re-engages on damage");
        TestAssert.Equal((uint?)hero.NetId, dragon.AttackTarget);
        var health = dragon.Health;
        ai.Tick(1.0);
        // playtest B4 / retail sweep, 27 Sept 2026: reset healing stops on
        // re-engagement, but the shipped 0.5 HP/s baseline keeps running.
        TestAssert.Near(health + .5, dragon.Health, 1e-8,
            "re-engaging leaves only baseline regeneration");
    }

    /// <summary>
    /// Batch 166: the reset heal is Leashed' own <c>TimerRegen</c>, and the only two script paths into
    /// <c>AI_RETREAT</c> arm it first (TimerRetreat proto 0.13 pc 0060-0066 and FindNewTarget proto 0.16
    /// pc 0045-0049: <c>ResetAndStartTimer('TimerRegen'); Retreat()</c>). A fixture that wants "a returned
    /// Dragon that is still healing" has to take one of those branches — a bare <c>NeutralState</c> write
    /// carries no host timer, and since batch 143 the reset is that named timer rather than the retired
    /// machine's regen accumulator. This is the same vocabulary <c>neutral.ai-reset-facing-wire</c> uses
    /// to start a reset: engage the camp, walk it past the leash radius, one TimerRetreat edge.
    /// </summary>
    private static void StartReset(NeutralAiService ai, Entity dragon, Position3 home, Entity hero)
    {
        dragon.Health = dragon.MaxHealth - 100;
        dragon.Position = new Position2(home.X + NeutralDefinitions.LeashRadius + 100.0, home.Z);
        dragon.NeutralState = "engage";
        dragon.AttackTarget = hero.NetId;
        dragon.Waypoints.Clear();
        dragon.Target = null;
        dragon.NeutralLastMove = null;
        // Batch 51 (7077fb3): TimerRetreat is a free-running 0.5 s timer and every check falls on its
        // edge, so each step below is one full period (it always contains exactly one edge).
        ai.Tick(NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.Equal("retreat", dragon.NeutralState, "leaving the leash radius starts the reset");
    }

    /// <summary>
    /// Home with the walk consumed. <c>OnStoppedMoving</c> is TimerRetreat's own arrival test
    /// (proto 0.13 pc 0067-0082: a stopped retreating member within 100 units of its destination),
    /// so the next edge is what settles the state.
    /// </summary>
    private static void ArriveHome(NeutralAiService ai, Entity dragon, Position3 home)
    {
        dragon.Position = new Position2(home.X, home.Z);
        dragon.Waypoints.Clear();
        dragon.Target = null;
        dragon.NeutralLastMove = null;
        ai.Tick(NeutralDefinitions.RetreatCheckSeconds);
    }

    [Check("polish.box-shot-calls-camp-for-help")]
    public static void BoxShotCallsCampForHelp()
    {
        // playtest #15: ShacoBoxSpell credits Shaco with the damage but names
        // the box as CallForHelpAttacker, so an idle camp wakes on the box.
        var m = new Match("Shaco"); var r = m.Runtime; var nav = r.Navigation; var owner = m.Hero(0);
        m.Hero(1).Position = new(12000, 12000);
        var wire = new ProtocolNeutralPacketSink(p => r.Delivery.Enqueue(p), r.SyncIds,
            r.World.Allocate, new(nav.MinX, nav.MaxX, nav.MinZ, nav.MaxZ), (x, z) => nav.HeightAt(new(x, z)));
        var scheduler = new JungleScheduler(r.World, r.GameData, wire, navigation: nav);
        // Batch 159 part 2: the camp's own character script is what puts ResistantSkinDragon on the Dragon
        // (CharScriptDragon's CharOnActivate, 0109-0155) — the buff that refuses the box's hostile fear, so the
        // woken camp engages the box instead of being feared by it. The hand-written inherent add is gone, so the
        // check binds the character the way the match's own jungle does.
        scheduler.BindUnitCharacter = (unit, character) => r.Spells.UnitScripts!.BindUnit(unit, character);
        scheduler.Start(); scheduler.Tick(1000);
        var dragon = scheduler.Members.Values.Single(member => member.Entity.Name == "Dragon").Entity;
        // The shipped scripts' box (19 October 2026): asleep for 2 s, then it wakes on the camp and shoots it through ShacoBoxSpell.
        var box = ShacoScriptSupport.SpawnBox(r, owner, dragon.Position + new Position2(150, 0));
        const double dt = 1.0 / 30.0;
        for (var tick = 1; tick <= 240 && dragon.Health >= dragon.MaxHealth; tick++) r.Tick(dt);
        TestAssert.True(dragon.Health < dragon.MaxHealth, "the armed box shoots the idle camp");
        TestAssert.Equal("engage", dragon.NeutralState, "the box shot wakes the camp");
        TestAssert.Equal((uint?)box.NetId, dragon.AttackTarget,
            "the box, not its credited owner, is the camp's call-for-help attacker");
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private sealed class Match
    {
        public MatchRuntime Runtime { get; }
        public List<Capture> Captures { get; } = [];
        public Entity Hero(int seat) => Runtime.Session.Participants[seat].Hero!;
        public Match(string champion = "Annie", string summoner = "SummonerFlash", Team secondTeam = Team.Chaos)
        {
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                readySettleSeconds: 0, roster: [new(1, 0, "Caster", champion, Team.Order, summoner,
                    summoner == "SummonerHeal" ? "SummonerFlash" : "SummonerHeal"),
                    new(2, 1, "Enemy", "Annie", secondTeam)]);
            Runtime.World.Clock.SetVirtual(0); Runtime.Session.CreateWorld();
            for (var seat = 0; seat < 2; seat++)
            {
                var id = seat + 1;
                TestAssert.True(Runtime.Session.Authenticate(id, Runtime.Session.BuildKeyCheck((ulong)id)) is not null);
                Runtime.Session.Spawn(id); Runtime.Session.MarkReady(id);
                var hero = Hero(seat); hero.HeroAutoAcquire = false;
                hero.Position = new(7000 + seat * 200, 7000);
                for (var slot = 0; slot < 4; slot++) hero.SpellRanks[slot] = 1;
                hero.MaxHealth = hero.Health = 5000; hero.Mana = hero.MaxMana;
                var capture = new Capture(); Captures.Add(capture);
                Runtime.Delivery.AddObserver(new((ulong)id, hero.Team, hero.NetId, capture,
                    ready: true, view: Runtime.Session.Participants[seat].View));
            }
            TestAssert.True(Runtime.Session.Start()); Runtime.Delivery.Flush(); Clear();
        }
        public void Clear() { foreach (var capture in Captures) capture.Packets.Clear(); }
        public bool Send(byte[] request)
        {
            var accepted = false;
            var wire = request.ToArray();
            var aligned = request.Length / 8 * 8;
            Runtime.Cipher.Encrypt(request.AsSpan(0, aligned)).CopyTo(wire, 0);
            Runtime.Session.Receive(1, 1, wire,
                context => accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush(); return accepted;
        }
        public bool Cast(int slot, Entity target, bool summoner = false)
        {
            var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(0).NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
            return Send(request);
        }
    }
}
