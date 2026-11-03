using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-copied-manamune-clone-basic-hit")]
    public static void CopiedManamuneDoesNotSpendCloneHeroCharges()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick" })
        {
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var target = match.Hero(1);
            owner.Inventory = [new InventorySlot(3004), null, null, null, null, null];
            target.MaxHealth = target.Health = 10000.0;
            match.Cast(0, 3, owner);
            runtime.Tick(0.6);
            var kind = champion == "Shaco" ? EntityKind.ShacoClone : EntityKind.YorickRevenant;
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == kind && unit.Alive);
            TestAssert.Equal(UnitType.Monster, clone.UnitType,
                $"{team} {champion}: copied-inventory actor is not a hero");
            TestAssert.True(Inventory.HasItem(clone.Inventory, 3004),
                $"{team} {champion}: clone copied Manamune");
            var previousHealth = target.Health;
            TestAssert.True(runtime.Combat.ApplyHit(clone.NetId, target.NetId, 1.0,
                    DamageType.Physical, DamageSourceKind.Basic),
                $"{team} {champion}: copied-inventory basic hit lands");
            TestAssert.True(target.Health < previousHealth,
                $"{team} {champion}: clone still deals basic damage");
        }
    }

    /// <summary>
    /// Batch 467: a non-hero's buffs give it their penetration (the script helper's armor-penetration setter writes any obj_AI_Base).
    /// A clone runs its own copy of the owner's items (BbItemModule.IsPetHolder), so Items/3035's OnActivate puts its
    /// own LastWhisper on it; its 40% counts once, not on top of the owner's 40% the clone's snapshot copied (SharedCloneService).
    /// </summary>
    [Check("items-live-clone-armour-penetration-counts-its-item-buff-once")]
    public static void CloneArmourPenetrationCountsItsItemBuffOnce()
    {
        foreach (var champion in new[] { "Shaco", "Yorick" })
        {
            using var match = new Match(champion, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            owner.Inventory = [new InventorySlot(3035), null, null, null, null, null];
            runtime.Tick(0.5);
            TestAssert.Near(0.4, owner.ArmorPenetrationPercent, 1e-9, $"{champion}: precondition: Last Whisper's buff gives its holder 40%");
            match.Cast(0, 3, owner);
            runtime.Tick(1.0);
            var kind = champion == "Shaco" ? EntityKind.ShacoClone : EntityKind.YorickRevenant;
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == kind && unit.Alive);
            TestAssert.Near(0.4, clone.ArmorPenetrationPercent, 1e-9,
                $"{champion}: the clone's armour penetration counts Last Whisper once (clone buffs {string.Join(",", clone.Buffs.Select(b => b.Name))})");
        }
    }

    [Check("items-live-red-entropy-renew-periodic-clock")]
    public static void RedAndEntropyBurnRenewWithoutRestartingTick()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var entropy in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var first = match.Hero(0);
            var second = match.Hero(2);
            var target = match.Hero(1);
            var name = entropy ? "EntropyBurning" : "Burning";
            var tickDamage = entropy ? 40.0 : 10.0;
            foreach (var (seat, source) in new[] { (0, first), (2, second) })
            {
                source.Position = target.Position;
                if (entropy)
                {
                    source.Inventory = [new InventorySlot(3184), null, null, null, null, null];
                    // Batch 149 step 2: the window is Spells/OdinEntropicClaymore.luaobj's own self buff (the add
                    // its SelfExecute makes on the owner), so a real item use opens it instead of the hand-written
                    // AddBuff this used to make.
                    match.Cast(seat, 4, source);
                }
                else
                    // Batch 159 part 2: the red buff's burn is Spells/BlessingoftheLizardElder.luaobj's own
                    // BuffOnHitUnit (0598), so the holder has to carry a live script instance; a hand-built row
                    // carries no script and its hit hook never runs.
                    TestAssert.True(BbM3.ScriptBuff(runtime, source, "BlessingoftheLizardElder", 10.0) is not null);
            }
            target.MaxHealth = target.Health = 10000.0;
            target.Armor = target.SpellBlock = 0.0;
            TestAssert.True(runtime.Combat.ApplyHit(first.NetId, target.NetId, 1.0,
                DamageType.Physical, DamageSourceKind.Basic));
            var original = target.Buffs.Single(buff => buff.Name == name);
            var slot = original.Slot;
            // Batch 149 step 2: EntropyBurning's pulses are its own BBExecutePeriodically (Spells/
            // EntropyBurning.luaobj: 1.0 s, ExecuteImmediately false), so its clock is the script's GetTime and not
            // BuffLifecycle's PeriodElapsed bookkeeping (the engine ticker no longer runs a DoT the script schedules
            // itself). GetTime advances in SpellSystem.Tick before the unit's buffs are ticked
            // (ProductionMatchComposition.Tick); a bare TickBuffs leaves it where it was. The add is TickRate 1, so
            // its BuffOnUpdateActions only runs once a second, and that first run only seeds LastTimeExecuted: the
            // first damage is two seconds after the hit (the second run) and every second after that. The red burn
            // keeps the engine clock and its own split ticks.
            void Step(double dt)
            {
                runtime.Spells.Tick(dt);
                runtime.Buffs.TickBuffs(target, dt);
            }
            // Batch 159 part 2: the red buff's Burning is Spells/Burning.luaobj's own periodic too — the add is the
            // shipped BuffOnHitUnit's, so the payload's period and tick amount are the script's and the engine ticker
            // does not run it a second time. Both producers now take the same clock, so both read the script's.
            TestAssert.Near(1.0, original.Payload.Period, 1e-9, $"{team} {name}: script period");
            TestAssert.Near(tickDamage, original.Payload.DamagePerTick, 1e-9,
                $"{team} {name}: the payload describes the script's own amount (DamageVar='TickDamage')");
            TestAssert.True(original.Payload.SourceIsPeriodic && original.Payload.ScriptSchedulesPeriodic,
                $"{team} {name}: the script schedules its own pulses");
            var beforeQuiet = target.Health;
            Step(0.6);
            TestAssert.Near(beforeQuiet, target.Health, 1e-9,
                $"{team} {name}: no update is due before the script's own one-second tick");
            TestAssert.True(runtime.Combat.ApplyHit(second.NetId, target.NetId, 1.0,
                DamageType.Physical, DamageSourceKind.Basic));
            var renewed = target.Buffs.Single(buff => buff.Name == name);
            TestAssert.True(ReferenceEquals(original, renewed),
                $"{team} {name}: second attacker renews the live instance");
            TestAssert.Equal(slot, renewed.Slot,
                $"{team} {name}: renewal retains the wire slot");
            TestAssert.Equal(second.NetId, renewed.CasterNetId,
                $"{team} {name}: renewal records the latest source");
            if (entropy)
            {
                // The renewal hands over fresh NextBuffVars and keeps the instance's own LastTimeExecuted (the two
                // merge by name), so the phase the first script update seeds survives it. One pulse at the boundary
                // that seeding set, and none at the one a restarted clock would have moved it to.
                var beforeSeed = target.Health;
                Step(0.4);
                TestAssert.Near(beforeSeed, target.Health, 1e-9,
                    $"{team} {name}: the first script update only seeds the clock");
                var beforeGap = target.Health;
                Step(0.6);
                TestAssert.Near(beforeGap, target.Health, 1e-9,
                    $"{team} {name}: the script's own update is not due again yet");
                var beforePulse = target.Health;
                Step(0.4);
                TestAssert.Near(beforePulse - tickDamage, target.Health, 1e-7,
                    $"{team} {name}: burn ticks on the boundary its own clock seeded");
            }
            else
            {
                // The red burn keeps the clock the renewal did not move (BUFF_RENEW_EXISTING merges the add's vars
                // by name, so the live instance's LastTimeExecuted survives), the same shape as the entropy branch.
                var beforeSeed = target.Health;
                Step(0.4);
                TestAssert.Near(beforeSeed, target.Health, 1e-9,
                    $"{team} {name}: the first script update only seeds the clock");
                var beforeGap = target.Health;
                Step(0.6);
                TestAssert.Near(beforeGap, target.Health, 1e-9,
                    $"{team} {name}: the script's own update is not due again yet");
                var beforePulse = target.Health;
                Step(0.4);
                TestAssert.Near(beforePulse - tickDamage, target.Health, 1e-7,
                    $"{team} {name}: burn ticks on the boundary its own clock seeded");
            }
        }
    }

    // A full-inventory elixir purchase is a useOnBought item in the temporary slot 7; the client uses it and asks the server to
    // remove it (0x09, slot 7, sell flag clear). Retail accepts that once per temporary purchase and the slot is emptied (0x0B).
    [Check("items-live-temporary-elixir-slot-removal")]
    public static void TemporaryElixirSlotRemovalIsAcknowledged()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.Position = team == Team.Order
                ? new Position2(-97.4378, 1033.8202)
                : new Position2(13360.6055, 14586.5605);
            hero.Inventory = Enumerable.Range(0, Inventory.SlotCount)
                .Select(_ => (InventorySlot?)new InventorySlot(1001)).ToArray();
            hero.Gold = 5000.0;
            bool Dispatch(byte[] request) => runtime.Session.DispatchShop(
                new SessionInput(1, 1, runtime.Cipher.Encrypt(request), request, true));
            byte[] Remove(bool sell, int slot)
            {
                var request = new byte[6];
                request[0] = 0x09;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
                request[5] = (byte)(slot | (sell ? 0x80 : 0));
                return request;
            }
            TestAssert.False(Dispatch(Remove(false, 7)), $"{team}: no removal of a temporary item that was never issued");

            var buy = new byte[9];
            buy[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 2037);
            TestAssert.True(Dispatch(buy), $"{team}: full-inventory elixir purchase is accepted");
            runtime.Delivery.Flush();
            match.Clear();

            TestAssert.False(Dispatch(Remove(true, 7)), $"{team}: the temporary slot cannot be sold");
            TestAssert.True(Dispatch(Remove(false, 7)), $"{team}: the client's removal of the temporary slot is acknowledged");
            runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x0B &&
                packet.Plaintext.Span[5] == 7 && packet.Plaintext.Span[6] == 0),
                $"{team}: the answer empties client slot 7");
            TestAssert.False(Dispatch(Remove(false, 7)), $"{team}: one acknowledgement per temporary purchase");
        }
    }

    [Check("items-live-consumable-buff-capacity-atomic")]
    public static void ConsumablesOnlyCommitAfterBuffAllocation()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.Health = hero.MaxHealth * 0.9;
            hero.Inventory = [new InventorySlot(2003, count: 2), new InventorySlot(2037),
                null, null, null, null];
            var lastFillerSlot = -1;
            for (var index = 0; hero.Buffs.Count < 64; index++)
            {
                var filler = runtime.SpellBuffs.Add(hero, $"ItemCapacity_{index}",
                    BuffType.Internal, 30.0, hero.NetId);
                TestAssert.True(filler is not null, $"{team}: fills buff slot {index}");
                lastFillerSlot = filler!.Slot;
            }
            runtime.Delivery.Flush();
            match.Clear();
            var originalInventory = hero.Inventory;

            var rejectedPotion = match.TryCast(0, 4, hero);
            var rejectedElixir = match.TryCast(0, 5, hero);
            runtime.Delivery.Flush();
            TestAssert.False(rejectedPotion, $"{team}: full buff table rejects potion");
            TestAssert.False(rejectedElixir, $"{team}: full buff table rejects elixir");
            TestAssert.True(ReferenceEquals(originalInventory, hero.Inventory),
                $"{team}: failed allocation does not commit inventory");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name is
                "RegenerationPotion" or "PotionOfGiantStrength"));
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Span[0] == 0xA7),
                $"{team}: rejected uses emit no consume packet");

            TestAssert.True(runtime.Buffs.RemoveBuff(hero, lastFillerSlot));
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}: potion applies after one buff slot opens");
            var activePotion = hero.Buffs.Single(buff => buff.Name == "RegenerationPotion");
            TestAssert.Equal(1, hero.Inventory[0]!.Count,
                $"{team}: successful potion consumes exactly one item");
            TestAssert.Equal(64, hero.Buffs.Count);
            // RegenerationPotion.inibin SpellData Cooldown = 1 s: the second drink waits it out.
            // (The fillers last 30 s, so the table stays full across this tick.)
            match.Runtime.Tick(1.0);
            TestAssert.Equal(64, hero.Buffs.Count);
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}: existing potion renews with all slots occupied");
            TestAssert.True(ReferenceEquals(activePotion,
                hero.Buffs.Single(buff => buff.Name == "RegenerationPotion")),
                $"{team}: renewal retains its allocated slot");
            TestAssert.Equal(2, activePotion.Stacks,
                $"{team}: renewed potion adds a stack");
            TestAssert.True(hero.Inventory[0] is null,
                $"{team}: second successful potion consumes its final item");

            hero.Position = team == Team.Order
                ? new Position2(-97.4378, 1033.8202)
                : new Position2(13360.6055, 14586.5605);
            hero.Inventory = Enumerable.Range(0, Inventory.SlotCount)
                .Select(_ => (InventorySlot?)new InventorySlot(1001)).ToArray();
            hero.Gold = 5000.0;
            var fullInventory = hero.Inventory;
            var gold = hero.Gold;
            runtime.Delivery.Flush();
            match.Clear();
            var buy = new byte[9];
            buy[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 2037);
            TestAssert.False(runtime.Session.DispatchShop(
                    new SessionInput(1, 1, runtime.Cipher.Encrypt(buy), buy, true)),
                $"{team}: full-inventory elixir purchase rejects when no buff slot exists");
            runtime.Delivery.Flush();
            TestAssert.True(ReferenceEquals(fullInventory, hero.Inventory),
                $"{team}: rejected elixir purchase preserves inventory");
            TestAssert.Near(gold, hero.Gold, 0.0,
                $"{team}: rejected elixir purchase preserves gold");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "PotionOfGiantStrength"));
            TestAssert.Equal(0, match.Captures[0].Packets.Count,
                $"{team}: rejected elixir purchase emits no mutation packets");

            TestAssert.True(runtime.Buffs.RemoveBuff(hero, activePotion.Slot));
            hero.Inventory = [new InventorySlot(2037), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero);
            TestAssert.True(hero.EquippedItemIds.Contains(2037),
                $"{team}: refreshed elixir baseline recognizes the held item");
            var healthBeforeElixir = hero.MaxHealth;
            var damageBeforeElixir = hero.AttackDamage;
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}: elixir applies once a buff slot is available");
            TestAssert.True(hero.Inventory[0] is null,
                $"{team}: successful elixir consumes the held item");
            TestAssert.False(hero.EquippedItemIds.Contains(2037),
                $"{team}: successful elixir refresh clears the consumed equipment ID");
            TestAssert.Near(healthBeforeElixir + 134.41 + 5.59 * hero.Level,
                hero.MaxHealth, 1e-7, $"{team}: consumed elixir grants health");
            TestAssert.Near(damageBeforeElixir + 10.0, hero.AttackDamage, 1e-7,
                $"{team}: consumed elixir grants attack damage");
        }
    }
}
