using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Buffs;

public static class BuffLifecycleChecks
{
    [Check("buffs.resistance-refresh-consistency")]
    public static void MixedFlatAndPercentResistanceSurvivesRefresh()
    {
        NpcMixedResistanceRestoresBaseline();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var flat in new[] { 10.0, -15.0 })
        foreach (var percentFirst in new[] { true, false })
        {
            var context = new TestContext();
            var hero = context.Entity(1, EntityKind.Hero, UnitType.Hero, team);
            context.Stats.Refresh(hero);
            var originalArmor = hero.Armor;
            var originalMr = hero.SpellBlock;
            BuffState AddPercent() => context.Lifecycle.AddBuff(hero, "PercentResistance",
                BuffType.CombatDehancer, 10, hero.NetId,
                new BuffPayload { ArmorMult = -0.4, SpellBlockMult = -0.4 })!;
            BuffState AddFlat() => context.Lifecycle.AddStackingBuff(hero, "FlatResistance",
                BuffType.CombatEnhancer, 10, hero.NetId, 2,
                new BuffPayload { Armor = flat, SpellBlock = flat })!;
            var first = percentFirst ? AddPercent() : AddFlat();
            var second = percentFirst ? AddFlat() : AddPercent();
            AssertStable(1, 0.6);
            _ = AddFlat();
            AssertStable(2, 0.6);
            context.Lifecycle.RemoveBuff(hero, percentFirst ? second.Slot : first.Slot);
            AssertStable(0, 0.6);
            context.Lifecycle.RemoveBuff(hero, percentFirst ? first.Slot : second.Slot);
            AssertStable(0, 1.0);

            void AssertStable(int stacks, double multiplier)
            {
                var armor = hero.Armor;
                var mr = hero.SpellBlock;
                context.Stats.Refresh(hero);
                TestAssert.Near(armor, hero.Armor, 1e-9,
                    "an unrelated refresh must not change active armor modifiers");
                TestAssert.Near(mr, hero.SpellBlock, 1e-9,
                    "an unrelated refresh must not change active MR modifiers");
                TestAssert.Near((originalArmor + stacks * flat) * multiplier, hero.Armor, 1e-9);
                TestAssert.Near((originalMr + stacks * flat) * multiplier, hero.SpellBlock, 1e-9);
            }
        }
    }

    private static void NpcMixedResistanceRestoresBaseline()
    {
        foreach (var (kind, type) in new[] { (EntityKind.Minion, UnitType.Minion),
                     (EntityKind.Neutral, UnitType.Monster), (EntityKind.Pet, UnitType.Minion) })
        foreach (var growth in new[] { 0.0, 10.0 })
        foreach (var percentFirst in new[] { true, false })
        foreach (var removePercentFirst in new[] { true, false })
        {
            var context = new TestContext();
            var unit = context.Entity(1, kind, type, Team.Order);
            unit.Armor = 100.0;
            unit.SpellBlock = 80.0;
            BuffState AddPercent(double percent) => context.Lifecycle.AddStackingBuff(unit, "BushwhackDebuff",
                BuffType.CombatDehancer, 10, unit.NetId, 1,
                new BuffPayload { ArmorMult = percent, SpellBlockMult = percent },
                replaceExistingPayload: true)!;
            BuffState AddFlat() => context.Lifecycle.AddStackingBuff(unit, "BlackCleaver",
                BuffType.CombatDehancer, 10, unit.NetId, 3, new BuffPayload { Armor = -15.0 })!;
            var first = percentFirst ? AddPercent(-0.4) : AddFlat();
            var second = percentFirst ? AddFlat() : AddPercent(-0.4);
            TestAssert.Near(51, unit.Armor, 1e-9);
            TestAssert.Near(48, unit.SpellBlock, 1e-9);
            _ = AddFlat();
            TestAssert.Near(42, unit.Armor, 1e-9, "renewal recomputes two Cleaver stacks");
            _ = AddPercent(-0.2);
            TestAssert.Near(56, unit.Armor, 1e-9, "renewed percent replaces its old contribution");
            TestAssert.Near(64, unit.SpellBlock, 1e-9);
            BuffLifecycle.IncreaseNpcBaseResistances(unit, growth, growth / 2);
            TestAssert.Near((70 + growth) * 0.8, unit.Armor, 1e-9, "permanent growth enters the unmodified baseline");
            TestAssert.Near((80 + growth / 2) * 0.8, unit.SpellBlock, 1e-9);
            var percent = percentFirst ? first : second;
            var flat = percentFirst ? second : first;
            context.Lifecycle.RemoveBuff(unit, removePercentFirst ? percent.Slot : flat.Slot);
            TestAssert.Near(removePercentFirst ? 70 + growth : (100 + growth) * 0.8, unit.Armor, 1e-9);
            TestAssert.Near((80 + growth / 2) * (removePercentFirst ? 1 : 0.8), unit.SpellBlock, 1e-9);
            context.Lifecycle.RemoveBuff(unit, removePercentFirst ? flat.Slot : percent.Slot);
            TestAssert.Equal(100 + growth, unit.Armor, "both removal orders restore the exact baseline");
            TestAssert.Equal(80 + growth / 2, unit.SpellBlock);
        }
    }

    [Check("stats.hero.snapshot")]
    public static void HeroSnapshotControlAndCostValidation()
    {
        var context = new TestContext();
        var hero = context.Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order);
        context.Hooks.Pending = true;
        var pending = context.Stats.Snapshot(hero);
        TestAssert.Equal(0, pending.ActionState);
        TestAssert.Equal(uint.MaxValue, pending.CanCastBits1);
        TestAssert.Equal(uint.MaxValue, pending.CanCastBits2,
            "pending action blocks ActionState but leaves cast bits unchanged");

        context.Hooks.Pending = false;
        hero.Buffs.Add(new BuffState("Stun", type: BuffType.Stun));
        var controlled = context.Stats.Snapshot(hero);
        TestAssert.Equal(0, controlled.ActionState);
        TestAssert.Equal(uint.MaxValue & ~0xFu, controlled.CanCastBits1);
        TestAssert.Equal(uint.MaxValue, controlled.CanCastBits2);

        context.Hooks.Costs = new[] { 1.0, 2.0, 3.0 };
        TestAssert.Throws<InvalidDataException>(() => context.Stats.Snapshot(hero));
        context.Hooks.Costs = new[] { 1.0, 2.0, 3.0, double.PositiveInfinity };
        TestAssert.Throws<InvalidDataException>(() => context.Stats.Snapshot(hero));
    }

    [Check("stats.hero.transitions")]
    public static void HeroRefreshTransitionsAndPushOrder()
    {
        var context = new TestContext();
        var hero = context.Entity(5, EntityKind.Hero, UnitType.Hero, Team.Order);
        var withTear = Inventory.Empty().ToArray();
        withTear[0] = new InventorySlot(3003);
        hero.Inventory = withTear;
        context.Stats.Refresh(hero);
        TestAssert.True(hero.EquippedItemIds.Contains(3003));
        // Batch 149 step 5: the Tear family's growth and reservoirs are Spells/TearOfTheGoddessTrack.luaobj's own
        // CharVars.TearBonusMana and InstanceVars.CooldownResevoir, and the reset a buy or sale needs is
        // Items/3003.luaobj's OnActivate (BBIfHasBuff TearOfTheGoddessTrack / BBElse → TearBonusMana = 0); the
        // hand-written hero counters this check used to read are gone. The tooltip rows went the same way: they are
        // every script's own BBSetSpellToolTipVar write (Entity.ScriptSpellTooltips, BuildItemTooltipEntries reads
        // nothing else), so this fixture — which has no script engine — writes one the way a script writes it, and the
        // publish order below is the engine's own (PushItemTooltips before the stats snapshot).
        hero.ScriptSpellTooltips[0] = [30.0, 0, 0];
        context.Stats.Push(hero);
        TestAssert.Equal("tooltips", context.Transport.Events[^2]);
        TestAssert.Equal("stats", context.Transport.Events[^1]);

        hero.Inventory = Inventory.Empty();
        context.Stats.Refresh(hero);
        TestAssert.False(hero.EquippedItemIds.Contains(3003));
    }

    [Check("buffs.lifecycle")]
    public static void LifecycleAndStacking()
    {
        var context = new TestContext();
        var caster = context.Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        var target = context.Entity(3, EntityKind.Hero, UnitType.Hero, Team.Order);
        target.Waypoints.Add(new Position2(30, 30));
        target.Target = new Position2(50, 50);
        target.AttackTarget = caster.NetId;
        var snare = context.Lifecycle.AddBuff(target, "Snare", BuffType.Snare, 2, caster.NetId);
        TestAssert.True(snare is not null);
        TestAssert.Equal(0, target.Waypoints.Count);
        TestAssert.True(target.Target is null);
        TestAssert.Equal(0, context.Casts.Cancellations);
        TestAssert.True(context.Effects.Events.Any(value => value == "add:Snare"));
        TestAssert.True(context.Lifecycle.RemoveBuff(target, snare!.Slot));

        var fearMove = new ForcedMoveState(target.Position, 100, 1, caster.NetId, "Fear");
        target.ForcedMove = fearMove;
        var fear = context.Lifecycle.AddBuff(target, "Fear", BuffType.Fear, 2, caster.NetId);
        TestAssert.True(fear is not null);
        TestAssert.True(context.Lifecycle.RemoveBuff(target, fear!.Slot));
        TestAssert.True(target.ForcedMove is null, "removing Fear must clear its exact move instance");

        var stun = context.Lifecycle.AddBuff(target, "Stun", BuffType.Stun, 2, caster.NetId,
            new BuffPayload { ActionDisabled = true });
        TestAssert.True(stun is not null);
        TestAssert.Equal(1, context.Casts.Cancellations);
        TestAssert.Equal(1, context.Combat.Interruptions);
        TestAssert.True(context.Effects.Events.Any(value => value == "stop:" + target.NetId));
        context.Lifecycle.RemoveBuff(target, stun!.Slot);

        // Playtest B2 OS2, 26 Sept 2026: NPC control stops the swing like hero
        // control (minionLib OnFearBegin TurnOffAutoAttack IMMEDIATELY).
        var laneMinion = context.Entity(6, EntityKind.Minion, UnitType.Minion, Team.Order);
        laneMinion.MinionType = MinionType.Melee;
        laneMinion.AttackTarget = caster.NetId;
        var minionStop = "stop:" + laneMinion.NetId;
        var minionStun = context.Lifecycle.AddBuff(laneMinion, "Stun", BuffType.Stun, 1,
            caster.NetId)!;
        TestAssert.Equal(2, context.Combat.Interruptions, "a minion stun interrupts its wind-up");
        TestAssert.Equal(1, context.Effects.Events.Count(value => value == minionStop),
            "a minion stun sends the instant stop-attack");
        context.Lifecycle.RemoveBuff(laneMinion, minionStun.Slot);
        var minionFear = context.Lifecycle.AddBuff(laneMinion, "Fear", BuffType.Fear, 1,
            caster.NetId, new BuffPayload { ActionDisabled = true, FearOrigin = laneMinion.Position })!;
        TestAssert.Equal(3, context.Combat.Interruptions, "a minion Fear interrupts its wind-up");
        TestAssert.Equal(2, context.Effects.Events.Count(value => value == minionStop),
            "a minion Fear sends the instant stop-attack");
        TestAssert.True(context.Effects.Events.LastIndexOf(minionStop) <
            context.Effects.Events.LastIndexOf("add:Fear"), "the stop precedes the Fear buff add");
        context.Lifecycle.RemoveBuff(laneMinion, minionFear.Slot);

        var periodicPayload = new BuffPayload
        {
            Period = 1.0,
            HpPerTick = 5.0,
            DamagePerTick = 3.0,
            DamageType = DamageType.True,
            SourceKind = DamageSourceKind.SpellPersist,
            SpellOwnerNetId = caster.NetId,
            SourceIsPeriodic = true,
        };
        target.MaxHealth = 100;
        target.Health = 50;
        var periodic = context.Lifecycle.AddBuff(target, "Periodic", BuffType.DamageOverTime, 1.5,
            caster.NetId, periodicPayload);
        TestAssert.True(periodic is not null);
        context.Lifecycle.TickBuffs(target, 3.0);
        TestAssert.Near(52.0, target.Health, 1e-9,
            "one heal tick and one captured damage tick should apply before expiry: the 1.5 s life holds one 1.0 s period");
        TestAssert.Equal(1, context.Combat.Periodic.Count);
        TestAssert.Equal(DamageType.True, context.Combat.Periodic[0].DamageType);
        TestAssert.True(context.Combat.Periodic[0].SourceKind == DamageSourceKind.SpellPersist);
        TestAssert.Equal(caster.NetId, context.Combat.Periodic[0].SourceNetId);
        TestAssert.Equal(caster.NetId, context.Combat.Periodic[0].SpellOwnerNetId);
        TestAssert.True(context.Combat.Periodic[0].SourceIsPeriodic);
        TestAssert.False(context.Lifecycle.HasBuff(target, "Periodic"));

        var minion = context.Entity(4, EntityKind.Minion, UnitType.Minion, Team.Order);
        minion.Armor = 10;
        minion.Speed = 100;
        minion.AttackSpeedMod = 1;
        minion.AttackPeriod = 1;
        var perStack = new BuffPayload { Armor = 2, AttackSpeedMod = 0.2 };
        var first = context.Lifecycle.AddStackingBuff(minion, "Stacks", BuffType.CombatEnhancer,
            10, caster.NetId, 3, perStack);
        var second = context.Lifecycle.AddStackingBuff(minion, "Stacks", BuffType.CombatEnhancer,
            10, caster.NetId, 3, perStack);
        TestAssert.True(first is not null && ReferenceEquals(first, second));
        TestAssert.Equal(2, second!.Stacks);
        TestAssert.True(second.MaxStack is null,
            "stacking metadata is carried by count, not the add-buff max-stack flag");
        TestAssert.Near(14, minion.Armor, 1e-9);
        TestAssert.Near(1.4, minion.AttackSpeedMod, 1e-9);
        TestAssert.Near(1.0 / 1.4, minion.AttackPeriod, 1e-9);
        TestAssert.Near(1.2, second.Payload.SpellbladeDelay, 1e-9);
        TestAssert.False(second.Payload.Has(BuffPayloadField.SpellbladeDelay),
            "an absent default delay must not become a scaled payload field");
    }

    [Check("buffs.minion-forced-move-removal-reanchors")]
    public static void MinionForcedMoveRemovalReanchorsPrediction()
    {
        var context = new TestContext();
        var caster = context.Entity(50, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        var minion = context.Entity(51, EntityKind.Minion, UnitType.Minion, Team.Order);
        minion.MinionType = MinionType.Melee;
        minion.Position = new Position2(7031.25, 7117.75);
        minion.AnchorPath = [new Position2(6900, 7000), new Position2(7100, 7200)];
        minion.ForcedMove = new ForcedMoveState(new Position2(7200, 7300), 325, .75,
            caster.NetId, "Fear", firstCollision: true);

        var fear = context.Lifecycle.AddBuff(minion, "Fear", BuffType.Fear, .75,
            caster.NetId, new BuffPayload { ActionDisabled = true,
                FearOrigin = minion.Position });
        TestAssert.True(fear is not null);
        TestAssert.True(context.Lifecycle.RemoveBuff(minion, fear!.Slot));
        TestAssert.Equal(1, minion.AnchorPath.Count,
            "clearing an owned Fear move must replace the stale minion route");
        TestAssert.Equal(minion.Position, minion.AnchorPath[0],
            "the new anchor must begin at the post-control authoritative body");
    }

    [Check("buffs.npc-attack-timing-baseline-cap-order")]
    public static void NonHeroAttackTimingRebuildsFromUncappedBaseline()
    {
        var context = new TestContext();
        var caster = context.Entity(40, EntityKind.Hero, UnitType.Hero, Team.Order);
        var neutral = context.Entity(41, EntityKind.Neutral, UnitType.Monster, Team.Neutral);
        neutral.AttackSpeedMod = 1.0;
        neutral.AttackPeriod = 4.8;
        neutral.AttackWindup = 0.5;
        neutral.AttackTimingBaseline = new AttackTimingBaseline(1.0, 4.8, 0.5 / 4.8);

        var slow = context.Lifecycle.AddBuff(neutral, "FrozenHeartAura", BuffType.Aura, 10,
            caster.NetId, new BuffPayload { AttackSpeedMult = -0.2 })!;
        TestAssert.Near(0.8, neutral.AttackSpeedMod, 1e-9);
        TestAssert.Near(5.0, neutral.AttackPeriod, 1e-9,
            "a slowed neutral period is capped after rebuilding from its raw baseline");
        TestAssert.Near(5.0 * (0.5 / 4.8), neutral.AttackWindup, 1e-9,
            "windup keeps the native cast fraction when the period is capped");

        var additive = context.Lifecycle.AddBuff(neutral, "Fortify", BuffType.Aura, 10,
            caster.NetId, new BuffPayload { AttackSpeedMod = 1.0 })!;
        TestAssert.Near(2.0 * 0.8, neutral.AttackSpeedMod, 1e-9);
        TestAssert.Near(4.8 / (2.0 * 0.8), neutral.AttackPeriod, 1e-9);
        context.Lifecycle.RemoveBuff(neutral, slow.Slot);
        TestAssert.Near(2.0, neutral.AttackSpeedMod, 1e-9,
            "removing a capped slow retains the additive buff without ratio inversion");
        TestAssert.Near(2.4, neutral.AttackPeriod, 1e-9);
        context.Lifecycle.RemoveBuff(neutral, additive.Slot);
        TestAssert.Near(1.0, neutral.AttackSpeedMod, 1e-9);
        TestAssert.Near(4.8, neutral.AttackPeriod, 1e-9);
        TestAssert.Near(0.5, neutral.AttackWindup, 1e-9);

        var turret = context.Entity(42, EntityKind.Turret, UnitType.Turret, Team.Order);
        // Batch 759: a lane turret's native multiplier is 1 (the assist bonus's +0.3 does not apply), period 1.2 s.
        turret.AttackSpeedMod = 1.0;
        turret.AttackPeriod = 1.2;
        turret.AttackWindup = 0.3;
        turret.AttackTimingBaseline = new AttackTimingBaseline(1.0, 1.2, 0.3 / 1.2);
        var fortify = context.Lifecycle.AddBuff(turret, "SummonerFortify", BuffType.Aura, 10,
            caster.NetId, new BuffPayload { AttackSpeedMod = 1.0 })!;
        TestAssert.Near(2.0, turret.AttackSpeedMod, 1e-9,
            "turret additive haste starts from its native 1.0 base");
        TestAssert.Near(0.6, turret.AttackPeriod, 1e-9);
        context.Lifecycle.RemoveBuff(turret, fortify.Slot);
        TestAssert.Near(1.0, turret.AttackSpeedMod, 1e-9);
        TestAssert.Near(1.2, turret.AttackPeriod, 1e-9);

        var cripple = context.Lifecycle.AddBuff(neutral, "BlindMonkETwoMissile",
            BuffType.CombatDehancer, 5, caster.NetId, new BuffPayload
            {
                AttackSpeedMult = -0.4, MoveSpeedMult = -0.4, CrippleReduction = -0.4
            })!;
        context.Effects.Events.Clear();
        context.Lifecycle.TickBuffs(neutral, 1.0);
        TestAssert.Near(0.7, neutral.AttackSpeedMod, 1e-9,
            "Cripple recomputes the remaining factor while its payload stays in the buff list");
        TestAssert.Equal(1, context.Effects.Events.Count(value => value == "attack-speed:41"),
            "a changing Cripple stage publishes the NPC speed");
        context.Effects.Events.Clear();
        context.Lifecycle.TickBuffs(neutral, 0.1);
        TestAssert.False(context.Effects.Events.Contains("attack-speed:41"),
            "an unchanged Cripple stage does not republish speed");
        context.Lifecycle.RemoveBuff(neutral, cripple.Slot);
        TestAssert.Near(1.0, neutral.AttackSpeedMod, 1e-9);

        context.Lifecycle.AddStackingBuff(neutral, "test-haste", BuffType.CombatEnhancer,
            5, caster.NetId, 2, new BuffPayload { AttackSpeedMod = 0.2 });
        var stacked = context.Lifecycle.AddStackingBuff(neutral, "test-haste", BuffType.CombatEnhancer,
            5, caster.NetId, 2, new BuffPayload { AttackSpeedMod = 0.2 })!;
        TestAssert.Near(1.4, neutral.AttackSpeedMod, 1e-9,
            "stack refresh counts the replacement payload once");
        context.Lifecycle.RemoveBuff(neutral, stacked.Slot);
        TestAssert.Near(4.8, neutral.AttackPeriod, 1e-9,
            "stack removal restores the original timing");
    }

    [Check("buffs.npc-movement-baseline-and-slow-order")]
    public static void NonHeroMovementRebuildsFromBaselineAndStacksSlows()
    {
        var context = new TestContext();
        var caster = context.Entity(50, EntityKind.Hero, UnitType.Hero, Team.Order);
        var npc = context.Entity(51, EntityKind.Minion, UnitType.Minion, Team.Order);
        npc.Speed = 100.0;

        var haste = context.Lifecycle.AddBuff(npc, "Haste", BuffType.Haste, 10.0,
            caster.NetId, new BuffPayload { MoveSpeedPercent = 0.20 })!;
        var multiplier = context.Lifecycle.AddBuff(npc, "Multiplier", BuffType.Haste, 10.0,
            caster.NetId, new BuffPayload { MoveSpeedMult = 0.15 })!;
        var strongest = context.Lifecycle.AddBuff(npc, "StrongSlow", BuffType.Slow,
            10.0, caster.NetId, new BuffPayload { MoveSpeedPercent = -0.40 })!;
        var weaker = context.Lifecycle.AddBuff(npc, "WeakSlow", BuffType.Slow,
            10.0, caster.NetId, new BuffPayload { MoveSpeedMult = -0.20 })!;

        var expectedSlowed = 100.0 * (1.0 + 0.20) * (1.0 + 0.15) *
                             (1.0 - 0.40) * (1.0 - 0.20 * 0.35);
        TestAssert.Near(expectedSlowed, npc.Speed, 1e-9,
            "NPC movement uses additive haste, independent multipliers, and 35 percent slow stacking");
        TestAssert.True(npc.MovementSpeedBaseline is 100.0,
            "the first movement effect captures the unmodified NPC speed");
        TestAssert.Equal(4, context.Effects.Events.Count(value => value == "movement-speed:51"),
            "each live NPC movement transition emits one speed-only update");

        context.Lifecycle.RemoveBuff(npc, strongest.Slot);
        var speedAfterStrongestRemoval = 100.0 * (1.0 + 0.20) * (1.0 + 0.15) *
                                         (1.0 - 0.20);
        TestAssert.Near(speedAfterStrongestRemoval, npc.Speed, 1e-9,
            "removing the strongest slow retains the weaker slow and positive stages");
        context.Lifecycle.RemoveBuff(npc, weaker.Slot);
        context.Lifecycle.RemoveBuff(npc, multiplier.Slot);
        context.Lifecycle.RemoveBuff(npc, haste.Slot);
        TestAssert.Near(100.0, npc.Speed, 1e-9,
            "removing effects restores the captured baseline without ratio inversion");

        var itemSlowEvents = context.Effects.Events.Count(value => value == "movement-speed:51");
        // Script wins (batch 147): the shipped shape is the ItemSlow marker plus self-bound empty
        // Internal_*Slow rows — Spells/ItemSlow.luaobj's cascade applies the selected magnitude from the
        // marker itself. The old fixture baked the penalty into the row and added no marker.
        var itemSlowMarker = context.Lifecycle.AddBuff(npc, "ItemSlow", BuffType.Slow,
            10.0, caster.NetId, canMitigateDuration: false)!;
        var itemSlow = context.Lifecycle.AddBuff(npc, "Internal_40Slow", BuffType.Internal,
            10.0, caster.NetId, canMitigateDuration: false)!;
        TestAssert.Near(60.0, npc.Speed, 1e-9,
            "an item slow contributes its selected movement payload");
        TestAssert.Equal(itemSlowEvents + 1,
            context.Effects.Events.Count(value => value == "movement-speed:51"),
            "adding an item slow publishes its speed-only transition");
        context.Lifecycle.RemoveBuff(npc, itemSlow.Slot);
        TestAssert.Near(100.0, npc.Speed, 1e-9,
            "removing the last item slow restores the NPC baseline");
        TestAssert.Equal(itemSlowEvents + 2,
            context.Effects.Events.Count(value => value == "movement-speed:51"),
            "removing the last item slow publishes the restored speed");

        var silentEvents = context.Effects.Events.Count(value => value == "movement-speed:51");
        var silentSlow = context.Lifecycle.AddBuff(npc, "Internal_20Slow", BuffType.Internal,
            10.0, caster.NetId, canMitigateDuration: false)!;
        TestAssert.Near(80.0, npc.Speed, 1e-9,
            "a second item slow still recomputes from the same baseline");
        context.Lifecycle.RemoveBuff(npc, silentSlow.Slot, emitWire: false);
        TestAssert.Near(100.0, npc.Speed, 1e-9,
            "silent item slow removal still restores authoritative speed");
        TestAssert.Equal(silentEvents + 1,
            context.Effects.Events.Count(value => value == "movement-speed:51"),
            "silent item slow removal emits no packet");
        // The marker sweeps its rows and carries no penalty of its own once they are gone.
        TestAssert.True(context.Lifecycle.RemoveBuff(npc, itemSlowMarker.Slot, emitWire: false),
            "the empty item-slow marker can still be removed");
        TestAssert.Near(100.0, npc.Speed, 1e-9,
            "an empty marker leaves the NPC baseline alone");
    }

    // 19 October 2026: the Orianna half of this check (a payload that decays on its own clock) is gone with the OriannaSpeed payload; OrianaSlow and
    // OrianaHaste decay in their scripts now (scripting.bb-orianna-wave-slow-and-haste-decay). The Cripple half stays.
    [Check("buffs.npc-dynamic-movement-decay")]
    public static void DynamicSlowPayloadsRecomputeAgainstOverlappingSlows()
    {
        var context = new TestContext();
        var caster = context.Entity(60, EntityKind.Hero, UnitType.Hero, Team.Order);
        var crippleTarget = context.Entity(62, EntityKind.Minion, UnitType.Minion, Team.Order);
        var secondSlow = context.Lifecycle.AddBuff(crippleTarget, "SecondSlow",
            BuffType.Slow, 10.0, caster.NetId,
            new BuffPayload { MoveSpeedPercent = -0.20 })!;
        var cripple = context.Lifecycle.AddBuff(crippleTarget, "Cripple",
            BuffType.CombatDehancer, 10.0, caster.NetId, new BuffPayload
            {
                CrippleReduction = -0.40,
                MoveSpeedMult = -0.40,
                AttackSpeedMult = -0.40,
            })!;
        context.Lifecycle.TickBuffs(crippleTarget, 1.0);
        TestAssert.Near(100.0 * (1.0 - 0.30) * (1.0 - 0.20 * 0.35), crippleTarget.Speed,
            1e-9, "Cripple decay recomputes from baseline with the second slow");
        context.Lifecycle.TickBuffs(crippleTarget, 1.0);
        TestAssert.Near(100.0 * (1.0 - 0.20) * (1.0 - 0.20 * 0.35), crippleTarget.Speed,
            1e-9, "Cripple's later decay keeps the strongest slow ordering");
        context.Lifecycle.RemoveBuff(crippleTarget, cripple.Slot);
        context.Lifecycle.RemoveBuff(crippleTarget, secondSlow.Slot);
        TestAssert.Near(100.0, crippleTarget.Speed, 1e-9,
            "removing Cripple and its overlapping slow restores the baseline");
    }

    // BUFF_STACKS_AND_OVERLAPS (Spells/RanduinsOmen.luaobj, Items/3082.luaobj, BlessingoftheLizardElder.luaobj): the stock client keeps
    // one timer per buff slot, so each application is its own instance; nothing is replaced and each ends by itself.
    [Check("buffs.lifecycle.overlapping-applications-keep-their-own-timers")]
    public static void OverlappingApplicationsKeepTheirOwnTimers()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var other = context.Entity(21, EntityKind.Hero, UnitType.Hero, Team.Order);
        var target = context.Entity(63, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        var first = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 3.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 2)!;
        context.Lifecycle.TickBuffs(target, 1.0);
        var second = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 3.0, other.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 2)!;
        TestAssert.Equal(2, target.Buffs.Count(buff => buff.Name == "Slow"), "an application never replaces another");
        TestAssert.True(first.Slot != second.Slot, "every application owns a client slot");
        TestAssert.Near(100.0 * 0.65 * (1.0 - 0.35 * 0.35), target.Speed, 1e-9,
            "two slow instances compose with the shipped 35 percent stacking factor");
        context.Lifecycle.TickBuffs(target, 2.0);
        TestAssert.False(target.Buffs.Contains(first), "the first application expires on its own timer");
        TestAssert.True(target.Buffs.Contains(second) && second.ExpiresAt > 0.9, "the second keeps its remaining time");
        // Reconstruction: at the cap the instance with the least time left makes room.
        var third = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 3.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 2)!;
        var fourth = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 3.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 2)!;
        TestAssert.Equal(2, target.Buffs.Count(buff => buff.Name == "Slow"), "the cap bounds the live instances");
        TestAssert.False(target.Buffs.Contains(second), "the instance with the least time left was dropped");
        TestAssert.True(target.Buffs.Contains(third) && target.Buffs.Contains(fourth));
    }

    // BBSpellBuffAdd's default MaxStack is 1, so a script's overlapping add that names none (Cassiopeia's Miasma Slow, re-added four times a
    // second) evicts the previous instance on every add. The eviction is one event to the client: the new instance takes the victim's slot
    // and the victim's removal packet is withheld, so the wire is one renewal of the held copy and no remove shares a flush with an add
    // (sweep BUF-FLUSH, Slow 38 pairs in the master sweep).
    [Check("buffs.lifecycle.overlap-cap-replaces-in-place")]
    public static void OverlapCapReplacesInPlace()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var target = context.Entity(63, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        var first = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 2.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 1)!;
        context.Lifecycle.TickBuffs(target, 0.25);
        var second = context.Lifecycle.AddOverlappingBuff(target, "Slow", BuffType.Slow, 2.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.35 }, canMitigateDuration: false, maxStack: 1)!;
        TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Slow"), "a cap of one keeps one instance");
        TestAssert.False(target.Buffs.Contains(first), "the previous instance left the unit");
        TestAssert.Equal(first.Slot, second.Slot, "the replacement takes the victim's client slot");
        TestAssert.False(context.Effects.Events.Contains("remove:Slow"),
            "the eviction sends no removal packet, so the add renews the copy the client already holds");
    }

    // CharScriptIrelia clears IreliaIonianDuelist and adds it back in the same hook run every second. The clear's removal packet waits for the end
    // of that run, so the re-add takes the slot (one renewal of the client's copy, no remove in its flush); a clear that no add followed sends its
    // packet when the run ends.
    [Check("buffs.lifecycle.clear-and-readd-in-one-run-renews-the-slot")]
    public static void ClearAndReAddInOneRunRenewsTheSlot()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var target = context.Entity(63, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        var run = new object();
        var held = context.Lifecycle.AddBuff(target, "IreliaIonianDuelist", BuffType.Aura, 1.25, caster.NetId,
            new BuffPayload(), canMitigateDuration: false)!;
        var slot = held.Slot;
        TestAssert.True(context.Lifecycle.RemoveBuffForRenewal(target, slot, run), "the clear removes the buff");
        TestAssert.False(target.Buffs.Contains(held), "the cleared buff left the unit");
        var renewed = context.Lifecycle.AddBuff(target, "IreliaIonianDuelist", BuffType.Aura, 1.25, caster.NetId,
            new BuffPayload(), canMitigateDuration: false)!;
        context.Lifecycle.FlushRenewalRemovals(run);
        TestAssert.Equal(slot, renewed.Slot, "the re-add takes the cleared slot");
        TestAssert.False(context.Effects.Events.Contains("remove:IreliaIonianDuelist"), "no removal packet: the client renews its copy");
        var lone = new object();
        context.Lifecycle.RemoveBuffForRenewal(target, renewed.Slot, lone);
        TestAssert.False(context.Effects.Events.Contains("remove:IreliaIonianDuelist"), "a removal waits for the end of its run");
        context.Lifecycle.FlushRenewalRemovals(lone);
        TestAssert.True(context.Effects.Events.Contains("remove:IreliaIonianDuelist"), "a clear no add followed is sent when its run ends");
    }

    // Sona's Hymn of Valor aura (SonaHymnofValorAuraB) lasts 0.25 s and its periodic re-adds it every 0.25 s, so the server expires it one step
    // before the next add. The client used to get a remove and then a full add one step apart (a flicker). With the wire grace the remove waits
    // one step and the re-add takes the held slot, so no remove crosses the renewal.
    [Check("buffs.lifecycle.sona-aura-renewal-sends-no-remove")]
    public static void SonaAuraRenewalSendsNoRemove()
    {
        var context = new TestContext();
        var sona = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var ally = context.Entity(21, EntityKind.Hero, UnitType.Hero, Team.Order);
        const string aura = "SonaHymnofValorAuraB";
        var first = context.Lifecycle.AddBuff(ally, aura, BuffType.Aura, 0.25, sona.NetId,
            new BuffPayload { WireDurationMargin = 1.0 / 30.0 }, canMitigateDuration: false)!;
        context.Lifecycle.TickBuffs(ally, 0.25);
        TestAssert.False(ally.Buffs.Contains(first), "the server expires the aura on its own timer");
        TestAssert.False(context.Effects.Events.Contains("remove:" + aura), "the remove waits one step for the periodic re-add");
        var renewed = context.Lifecycle.AddBuff(ally, aura, BuffType.Aura, 0.25, sona.NetId,
            new BuffPayload { WireDurationMargin = 1.0 / 30.0 }, canMitigateDuration: false)!;
        TestAssert.Equal(first.Slot, renewed.Slot, "the re-add takes the held slot, so the client renews its copy");
        TestAssert.False(context.Effects.Events.Contains("remove:" + aura), "no remove crosses the renewal");
    }

    // A held remove that no re-add took is sent at the end of the step after its expiry: not at once, and not later.
    [Check("buffs.wire-grace.lone-expiry-is-removed-one-step-late")]
    public static void LoneExpiryIsRemovedOneStepLate()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var ally = context.Entity(21, EntityKind.Hero, UnitType.Hero, Team.Order);
        const string aura = "SonaHymnofValorAuraB";
        context.Lifecycle.AddBuff(ally, aura, BuffType.Aura, 0.25, caster.NetId,
            new BuffPayload { WireDurationMargin = 1.0 / 30.0 }, canMitigateDuration: false);
        context.Lifecycle.TickBuffs(ally, 0.25);
        context.Lifecycle.EndTick();
        TestAssert.False(context.Effects.Events.Contains("remove:" + aura), "the remove is held through the step of its expiry");
        context.Lifecycle.EndTick();
        TestAssert.True(context.Effects.Events.Contains("remove:" + aura), "the remove goes out at the end of the next step");
    }

    // A full 64-slot table never throws from gameplay (a player can spam an overlapping buff): one name is capped below the table, an
    // overlapping add on a full table drops the oldest instance of its own name or itself, and any other add makes room.
    [Check("buffs.lifecycle.full-table-never-throws")]
    public static void FullTableNeverThrows()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var spammed = context.Entity(63, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        for (var application = 0; application < 100; application++)
            context.Lifecycle.AddOverlappingBuff(spammed, "Slow", BuffType.Slow, 3.0 + application, caster.NetId,
                new BuffPayload { MoveSpeedMult = -0.10 }, canMitigateDuration: false, maxStack: 100);
        TestAssert.Equal(20, spammed.Buffs.Count(buff => buff.Name == "Slow"), "one name never takes more than its cap");

        var full = context.Entity(64, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        for (var index = 0; index < 64; index++)
            TestAssert.True(context.Lifecycle.AddBuff(full, "Filler" + index, BuffType.Internal, 10.0 + index, caster.NetId) is not null);
        TestAssert.Equal(64, full.Buffs.Count);
        TestAssert.True(context.Lifecycle.AddOverlappingBuff(full, "Slow", BuffType.Slow, 3.0, caster.NetId,
            new BuffPayload { MoveSpeedMult = -0.10 }, canMitigateDuration: false, maxStack: 100) is null,
            "an overlapping add with no instance of its name to drop declines on a full table");
        var added = context.Lifecycle.AddBuff(full, "Newcomer", BuffType.Internal, 30.0, caster.NetId);
        TestAssert.True(added is not null, "a plain add on a full table makes room instead of throwing");
        TestAssert.False(full.Buffs.Any(buff => buff.Name == "Filler0"), "the buff with the least time left made room");
        TestAssert.Equal(64, full.Buffs.Count);
        TestAssert.False(context.Lifecycle.CanAddOverlapping(full), "admission sees the full table");
    }

    // The whole ActionState word the client replicates (CharacterState.ActionWord): the client's own bit layout, not only the three
    // permission bits. Fear leaves CanAttack and CanMove set and marks bit 6; Stealth sets Ghosted with Stealthed.
    [Check("buffs.character-state.action-word-follows-the-client-bits")]
    public static void ActionWordFollowsTheClientBits()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var unit = context.Entity(21, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        TestAssert.Equal(0x7u, CharacterState.ActionWord(unit), "a fresh unit holds 7");

        void Expect(string name, BuffType type, BuffPayload payload, uint word)
        {
            var buff = context.Lifecycle.AddBuff(unit, name, type, 5.0, caster.NetId, payload, canMitigateDuration: false);
            TestAssert.True(buff is not null, name + " is added");
            TestAssert.Equal(word, CharacterState.ActionWord(unit), name);
            context.Lifecycle.RemoveBuff(unit, buff!.Slot);
        }

        Expect("Stun", BuffType.Stun, new BuffPayload(), 0x000u);
        Expect("Suppression", BuffType.Suppression, new BuffPayload(), 0x080u);
        Expect("Sleep", BuffType.Sleep, new BuffPayload(), 0x100u);
        Expect("Fear", BuffType.Fear, new BuffPayload(), 0x045u);
        Expect("Fear (action-disabled payload, as the server builds it)", BuffType.Fear, new BuffPayload { ActionDisabled = true }, 0x045u);
        Expect("Taunt", BuffType.Taunt, new BuffPayload(), 0x027u);
        Expect("Silence", BuffType.Silence, new BuffPayload { Silence = true }, 0x005u);
        Expect("Disarm", BuffType.Internal, new BuffPayload { Disarmed = true }, 0x006u);
        Expect("Pacified", BuffType.Internal, new BuffPayload { Pacified = true }, 0x004u);
        Expect("Net", BuffType.Net, new BuffPayload(), 0x003u);
        Expect("NearSight", BuffType.NearSight, new BuffPayload(), 0x207u);
        Expect("Stealth", BuffType.Internal, new BuffPayload { Stealthed = true }, 0x40Fu);
        Expect("Ghosted", BuffType.Internal, new BuffPayload { Ghosted = true }, 0x407u);
        Expect("NoRender", BuffType.Internal, new BuffPayload { NoRender = true }, 0x2007u);
        TestAssert.Equal(0x7u, CharacterState.ActionWord(unit), "every bit returns with the last buff");
    }

    // The CanAttack / CanCast / CanMove bits the shipped control scripts drive (Disarm, Pacified, Stun, Root, Taunt ...) and the
    // Taunt.luaobj rule that the buff ends with its attacker; blind keeps every bit.
    [Check("buffs.character-state.follows-the-control-scripts")]
    public static void CharacterStateFollowsTheControlScripts()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var unit = context.Entity(21, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        TestAssert.True(CharacterState.Of(unit) is { CanAttack: true, CanCast: true, CanMove: true, Taunted: false });

        void Expect(string name, BuffType type, BuffPayload payload, bool attack, bool cast, bool move, bool taunted = false)
        {
            var buff = context.Lifecycle.AddBuff(unit, name, type, 5.0, caster.NetId, payload, canMitigateDuration: false);
            TestAssert.True(buff is not null, name + " is added");
            TestAssert.Equal(new CharacterState(attack, cast, move, taunted, false, false, false), CharacterState.Of(unit), name);
            context.Lifecycle.RemoveBuff(unit, buff!.Slot);
        }

        Expect("Disarm", BuffType.Internal, new BuffPayload { Disarmed = true }, attack: false, cast: true, move: true);
        Expect("Pacified", BuffType.Internal, new BuffPayload { Pacified = true }, attack: false, cast: false, move: true);
        Expect("Stun", BuffType.Stun, new BuffPayload(), attack: false, cast: false, move: false);
        Expect("Root", BuffType.Snare, new BuffPayload(), attack: true, cast: true, move: false);
        Expect("Silence", BuffType.Silence, new BuffPayload { Silence = true }, attack: true, cast: false, move: true);
        Expect("Taunt", BuffType.Taunt, new BuffPayload(), attack: true, cast: true, move: true, taunted: true);
        Expect("Blind", BuffType.Blind, new BuffPayload { MissChance = 0.5 }, attack: true, cast: true, move: true);
        Expect("CantAttack", BuffType.Internal, new BuffPayload(), attack: false, cast: true, move: true);
        TestAssert.True(CharacterState.Of(unit) is { CanAttack: true, CanCast: true, CanMove: true }, "every bit returns with the last buff");

        // Taunt.luaobj BuffOnUpdateStats: the buff ends once the attacker is dead.
        context.Lifecycle.AddBuff(unit, "Taunt", BuffType.Taunt, 5.0, caster.NetId, canMitigateDuration: false);
        context.Lifecycle.TickBuffs(unit, 0.1);
        TestAssert.True(unit.Buffs.Any(buff => buff.Type == BuffType.Taunt), "a taunt lives while its attacker does");
        caster.Alive = false;
        context.Lifecycle.TickBuffs(unit, 0.1);
        TestAssert.False(unit.Buffs.Any(buff => buff.Type == BuffType.Taunt), "a taunt ends with its attacker");
        TestAssert.True(BbNativeHandlers.IsBound("BBApplyTaunt"), "the script adapter runs BBApplyTaunt");
    }

    // BUFF_STACKS_AND_CONTINUE (the shipped potions): each add raises the count while the running timer keeps counting down;
    // BUFF_STACKS_AND_RENEWS resets it. At the cap a continuing add changes nothing.
    [Check("buffs.lifecycle.stacks-and-continue-keeps-the-timer")]
    public static void StacksAndContinueKeepsTheRunningTimer()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var target = context.Entity(21, EntityKind.Hero, UnitType.Hero, Team.Order);
        var first = context.Lifecycle.AddStackingBuff(target, "RegenerationPotion", BuffType.Heal, 20.0, caster.NetId, 5,
            new BuffPayload { Period = 1.0 }, continueTimer: true)!;
        context.Lifecycle.TickBuffs(target, 8.0);
        TestAssert.Near(12.0, first.ExpiresAt, 1e-9, "the first potion has run 8 s");
        var second = context.Lifecycle.AddStackingBuff(target, "RegenerationPotion", BuffType.Heal, 20.0, caster.NetId, 5,
            new BuffPayload { Period = 1.0 }, continueTimer: true)!;
        TestAssert.True(ReferenceEquals(first, second), "the second potion is a stack of the same instance");
        TestAssert.Equal(2, second.Stacks);
        TestAssert.Near(12.0, second.ExpiresAt, 1e-9, "a continuing stack leaves the timer running");
        for (var index = 0; index < 5; index++)
            context.Lifecycle.AddStackingBuff(target, "RegenerationPotion", BuffType.Heal, 20.0, caster.NetId, 5,
                new BuffPayload { Period = 1.0 }, continueTimer: true);
        TestAssert.Equal(5, second.Stacks, "the count stops at MaxStack");
        TestAssert.Near(12.0, second.ExpiresAt, 1e-9, "and so does the timer");
        context.Lifecycle.AddStackingBuff(target, "RegenerationPotion", BuffType.Heal, 20.0, caster.NetId, 5,
            new BuffPayload { Period = 1.0 });
        TestAssert.Near(20.0, second.ExpiresAt, 1e-9, "a renewing stack resets the timer");
    }

    [Check("buffs.lifecycle.renewal-preserves-managed-state")]
    public static void RenewalPreservesLiveSlotPeriodAndVisualHandles()
    {
        var context = new TestContext();
        var caster = context.Entity(20, EntityKind.Hero, UnitType.Hero, Team.Order);
        var replacementCaster = context.Entity(22, EntityKind.Hero, UnitType.Hero, Team.Order);
        var target = context.Entity(21, EntityKind.Minion, UnitType.Minion, Team.Order);
        var first = context.Lifecycle.AddStackingBuff(target, "RenewedAura", BuffType.Aura,
            5.0, caster.NetId, 1, new BuffPayload
            {
                MoveSpeedPercent = 0.03,
                Period = 1.0,
            })!;
        first.Payload.PeriodElapsed = 0.4;
        first.Payload.FxNetId = 1001;
        first.Payload.ExtraFxNetId = 1002;
        first.Payload.VisionBubbles = new uint[] { 1003, 1004 };
        var slot = first.Slot;
        var ensuredFx = context.Effects.EnsuredBuffFx.Count;
        context.Effects.Events.Clear();

        var renewed = context.Lifecycle.AddStackingBuff(target, "RenewedAura", BuffType.Aura,
            5.0, replacementCaster.NetId, 1, new BuffPayload
            {
                MoveSpeedPercent = 0.05,
                Period = 1.0,
            }, replaceExistingPayload: true)!;

        TestAssert.True(ReferenceEquals(first, renewed), "renewal retains the live instance");
        TestAssert.Equal(slot, renewed.Slot, "renewal retains the buff slot");
        TestAssert.Near(0.4, renewed.Payload.PeriodElapsed, 1e-9,
            "renewal retains the periodic clock");
        TestAssert.Equal((uint)1001, renewed.Payload.FxNetId);
        TestAssert.Equal((uint)1002, renewed.Payload.ExtraFxNetId);
        TestAssert.True(renewed.Payload.VisionBubbles.SequenceEqual(new uint[] { 1003, 1004 }),
            "renewal retains perception bubble handles");
        TestAssert.Near(105.0, target.Speed, 1e-9,
            "renewal replaces the movement payload without double-applying it");
        TestAssert.Equal(replacementCaster.NetId, renewed.CasterNetId,
            "renewal updates the aura caster");
        TestAssert.Equal(ensuredFx, context.Effects.EnsuredBuffFx.Count,
            "renewal does not create a second managed effect");
        TestAssert.False(context.Effects.Events.Any(value => value == "remove:RenewedAura"),
            "renewal does not emit a remove packet");

        TestAssert.True(context.Lifecycle.RemoveBuff(target, renewed.Slot),
            "final removal succeeds");
        TestAssert.True(context.Effects.KilledEffects.SequenceEqual(new uint[] { 1001, 1002 }),
            "final removal retires the preserved effect handles");
        TestAssert.True(context.Effects.RemovedBubbles.SequenceEqual(new uint[] { 1003, 1004 }),
            "final removal retires the preserved perception bubbles");
    }

    [Check("buffs.lifecycle.callback-removal")]
    public static void RemovedBuffsCannotTickOrExpireReplacementSlots()
    {
        foreach (var phase in new[] { "earlier-buff", "champion-tick", "periodic-hit" })
        {
            var context = new TestContext();
            var target = context.Entity(30, EntityKind.Minion, UnitType.Minion, Team.Order);
            target.Health = 50;
            var first = context.Lifecycle.AddBuff(target, "First", BuffType.Internal, 10, target.NetId)!;
            var removed = context.Lifecycle.AddBuff(target, "Removed", BuffType.DamageOverTime,
                1, target.NetId, new BuffPayload { Period = 0.25, DamagePerTick = 1 })!;
            BuffState? replacement = null;
            void Replace()
            {
                context.Lifecycle.RemoveBuff(target, removed.Slot);
                replacement = context.Lifecycle.AddBuff(target, "Replacement", BuffType.Internal,
                    10, target.NetId)!;
            }
            context.Champions.Tick = (_, buff, _) =>
            {
                if ((phase == "earlier-buff" && ReferenceEquals(buff, first)) ||
                    (phase == "champion-tick" && ReferenceEquals(buff, removed))) Replace();
            };
            context.Combat.PeriodicHit = _ =>
            {
                if (phase == "periodic-hit") Replace();
            };

            context.Lifecycle.TickBuffs(target, 1);
            TestAssert.Equal(phase == "periodic-hit" ? 1 : 0, context.Combat.Periodic.Count, phase);
            TestAssert.True(replacement is not null && target.Buffs.Contains(replacement),
                phase + ": stale expiry must not remove the replacement in the reused slot");
            TestAssert.Equal(removed.Slot, replacement!.Slot, phase);
            TestAssert.Near(10, replacement.ExpiresAt, 1e-9,
                phase + ": a callback's new buff starts ticking next frame");
        }
    }

    [Check("buffs.lifecycle.rejections")]
    public static void RejectionsAndSpecialOrdering()
    {
        var context = new TestContext();
        var caster = context.Entity(12, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        caster.Position = new Position2(2_000, 0);
        var monster = context.Entity(13, EntityKind.Monster, UnitType.Monster, Team.Neutral);
        context.Lifecycle.AddBuff(monster, "ResistantSkin", BuffType.Internal, 20, monster.NetId);
        var rejected = context.Lifecycle.AddBuff(monster, "Control", BuffType.Stun, 2, caster.NetId);
        TestAssert.True(rejected is null, "resistant neutral must reject hostile control");

        var box = context.Entity(14, EntityKind.ShacoBox, UnitType.Minion, Team.Order);
        var rejectedBox = context.Lifecycle.AddBuff(box, "Control", BuffType.Stun, 2, caster.NetId);
        TestAssert.True(rejectedBox is null, "hostile control must not enter a Shaco box");

        var ghoul = context.Entity(17, EntityKind.YorickGhoul, UnitType.Minion, Team.Order);
        TestAssert.True(context.Lifecycle.AddBuff(ghoul, "Slow", BuffType.Slow, 2,
            caster.NetId) is null, "Yorick ghouls ignore the generic slow path");

        var mobileHero = context.Entity(18, EntityKind.Hero, UnitType.Hero, Team.Order);
        var mobileSlots = Inventory.Empty().ToArray();
        mobileSlots[0] = new InventorySlot(3117);
        mobileHero.Inventory = mobileSlots;
        // Script wins (batch 147 step 4): the BootsOfMobilityDebuff that a hostile control used to
        // apply here is Items/3117.luaobj's own BuffOnAllowAdd now, so this engine check only keeps the
        // part that is still its own work — the nested slot the script's add needs. The debuff itself is
        // covered by the item live checks (items.runtime.*, polish.selling-boots-of-mobility-...).
        TestAssert.True(context.Lifecycle.AddBuff(mobileHero, "Snare", BuffType.Snare, 2,
            caster.NetId) is not null);

        var guardian = context.Entity(15, EntityKind.Hero, UnitType.Hero, Team.Order);
        guardian.Health = 20;
        guardian.MaxHealth = 100;
        guardian.Mana = 5;
        guardian.MaxMana = 80;
        var guardianBuff = context.Lifecycle.AddBuff(guardian, "GuardianAngel", BuffType.CombatEnhancer,
            10, guardian.NetId, new BuffPayload { ReviveHp = 30, ReviveMp = 12 });
        TestAssert.True(guardianBuff is not null);
        context.Lifecycle.RemoveBuff(guardian, guardianBuff!.Slot);
        TestAssert.Near(50, guardian.Health, 1e-9);
        TestAssert.Near(17, guardian.Mana, 1e-9);
        var removeIndex = context.Effects.Events.IndexOf("remove:GuardianAngel");
        var reviveIndex = context.Effects.Events.IndexOf("add:HasBeenRevived");
        TestAssert.True(removeIndex >= 0 && reviveIndex > removeIndex,
            "GuardianAngel must remove its old wire slot before adding cooldown");

        var banshee = context.Entity(16, EntityKind.Hero, UnitType.Hero, Team.Order);
        banshee.Inventory = Inventory.Empty().ToArray();
        var slots = banshee.Inventory.ToArray();
        slots[0] = new InventorySlot(3102);
        banshee.Inventory = slots;
        var timer = context.Lifecycle.AddBuff(banshee, "BansheesVeilTimer", BuffType.Aura, 1,
            caster.NetId);
        TestAssert.True(timer is not null);
        context.Lifecycle.RemoveBuff(banshee, timer!.Slot);
        // Batch 183: the shipped item script owns the veil's return (Items/3102 AutoBuffActivateEffect re-adds BansheesVeil on its
        // two-second periodic update), so a timer's end adds nothing by hand. A script-less lifecycle therefore shows no veil here.
        TestAssert.False(context.Lifecycle.HasBuff(banshee, "BansheesVeil"));
    }

    [Check("buffs.payload.presence")]
    public static void PresenceAndTypedDamageBoundaries()
    {
        var zero = new BuffPayload { AbilityPower = 0.0, ItemSlowPercent = 0.0 };
        TestAssert.True(zero.Has(BuffPayloadField.AbilityPower));
        TestAssert.True(zero.Has(BuffPayloadField.ItemSlowPercent));
        TestAssert.True(BuffLifecycle.BuffNeedsFullRefresh(zero));
        TestAssert.Equal(DamageType.Magic, DamageTypeExtensions.Parse("magic"));
        TestAssert.Equal(DamageSourceKind.SpellAoe,
            DamageTypeExtensions.ParseSourceKind("spell_aoe"));
        TestAssert.Throws<FormatException>(() => DamageTypeExtensions.Parse("arcane"));
        TestAssert.Throws<FormatException>(() => DamageTypeExtensions.ParseSourceKind("unknown_source"));
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            World = new WorldState();
            Hooks = new Hooks();
            Transport = new Transport();
            var data = new GameData();
            var catalog = new ItemCatalog(data.Paths);
            Stats = new HeroStatsService(data, catalog, Hooks, Transport);
            Combat = new CombatHooks();
            Casts = new CastHooks();
            Champions = new ChampionHooks();
            Effects = new EffectHooks();
            Items = new ItemHooks();
            Lifecycle = new BuffLifecycle(World, Stats,
                new Services(Combat, Casts, Champions, Effects, Items));
        }

        public WorldState World { get; }
        public Hooks Hooks { get; }
        public Transport Transport { get; }
        public HeroStatsService Stats { get; }
        public CombatHooks Combat { get; }
        public CastHooks Casts { get; }
        public ChampionHooks Champions { get; }
        public EffectHooks Effects { get; }
        public ItemHooks Items { get; }
        public BuffLifecycle Lifecycle { get; }

        public Entity Entity(uint id, EntityKind kind, UnitType type, Team team)
        {
            var entity = new Entity(id, kind, type, team, new Position2(0, 0))
            {
                ChampionName = "Annie",
                Level = 1,
                Health = 100,
                MaxHealth = 100,
                Mana = 100,
                MaxMana = 100,
                Speed = 100,
                AttackSpeedMod = 1,
                AttackPeriod = 1,
                Alive = true,
            };
            World.Register(entity);
            return entity;
        }
    }

    private sealed class Services : IBuffServices
    {
        public Services(CombatHooks combat, CastHooks casts, ChampionHooks champions,
            EffectHooks effects, ItemHooks items)
        {
            Combat = combat;
            Casts = casts;
            Champions = champions;
            Effects = effects;
            Items = items;
        }
        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; }
    }

    private sealed class Hooks : IHeroStatsHooks
    {
        public bool Pending { get; set; }
        public IReadOnlyList<double> Costs { get; set; } = new[] { 60.0, 70.0, 80.0, 90.0 };
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            hero.AttackPeriod = attackSpeedMod <= 0 ? 0 : 1.6 / attackSpeedMod;
        public IReadOnlyList<double> ManaCosts(Entity hero) => Costs;
        public bool IsPendingActionBlocked(Entity hero) => Pending;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire)
        {
            for (var index = hero.Buffs.Count - 1; index >= 0; index--)
                if (hero.Buffs[index].Slot == slot) hero.Buffs.RemoveAt(index);
        }
    }

    private sealed class Transport : IHeroStatsTransport
    {
        public List<string> Events { get; } = new();
        public void Push(HeroStatsReplicationSnapshot snapshot) => Events.Add("stats");
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) => Events.Add("tooltips");
    }

    private sealed class CombatHooks : ICombatBuffHooks
    {
        public Action<PeriodicDamageRequest>? PeriodicHit { get; set; }
        public int Interruptions { get; private set; }
        public List<PeriodicDamageRequest> Periodic { get; } = new();
        public bool IsPendingActionBlocked(Entity unit) => false;
        public double HealingMultiplier(Entity unit) => 1.0;
        public void InterruptBasicAttack(Entity unit) => Interruptions++;
        public void InterruptBasicAttack(Entity unit, out bool cancelledWindup)
        {
            InterruptBasicAttack(unit);
            cancelledWindup = true; // This lifecycle double models an unfinished basic swing.
        }
        public void ApplyPeriodicDamage(PeriodicDamageRequest request)
        {
            Periodic.Add(request);
            request.Target.Health = Math.Max(0.0, request.Target.Health - request.Amount);
            PeriodicHit?.Invoke(request);
        }
        public void Kill(Entity unit, uint killerNetId = 0) => unit.Alive = false;
    }

    private sealed class CastHooks : ICastBuffHooks
    {
        public int Cancellations { get; private set; }
        public void CancelPendingCast(Entity hero) => Cancellations++;
    }

    private sealed class ChampionHooks : IChampionBuffHooks
    {
        public Action<Entity, BuffState, double>? Tick { get; set; }
        public void OnBuffTick(Entity unit, BuffState buff, double dt) => Tick?.Invoke(unit, buff, dt);
        public void OnBuffRemoved(Entity unit, BuffState buff) { }
        public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
    }

    private sealed class EffectHooks : IBuffEffectHooks
    {
        public List<string> Events { get; } = new();
        public List<uint> EnsuredBuffFx { get; } = new();
        public List<uint> KilledEffects { get; } = new();
        public List<uint> RemovedBubbles { get; } = new();
        public bool IsWireReady(Entity unit) => true;
        public bool SendBuffAdd(Entity unit, BuffState buff, int count)
        {
            Events.Add("add:" + buff.Name);
            return true;
        }
        public bool SendBuffRemove(Entity unit, BuffState buff)
        {
            Events.Add("remove:" + buff.Name);
            return true;
        }
        public void SendDamageShieldDelta(Entity unit, double amount) { }
        public void SendAttackSpeed(Entity unit) => Events.Add("attack-speed:" + unit.NetId);
        public void SendMovementSpeed(Entity unit) => Events.Add("movement-speed:" + unit.NetId);
        public void EnsureBuffFx(Entity unit, BuffState buff) => EnsuredBuffFx.Add(buff.Payload.FxNetId);
        public void KillSpellEffect(uint effectNetId) => KilledEffects.Add(effectNetId);
        public void RemovePerceptionBubble(Entity unit, uint bubbleId) => RemovedBubbles.Add(bubbleId);
        public void SendWaypoints(Entity unit) => Events.Add("waypoints:" + unit.NetId);
        public void PauseAnimation(Entity unit, bool paused) { }
        public void SendInstantStopAttack(Entity unit) => Events.Add("stop:" + unit.NetId);
    }

    private sealed class ItemHooks : IItemBuffHooks
    {
        public bool HasItem(Entity unit, int itemId) => unit.Inventory.Any(slot => slot?.ItemId == itemId);
        // The movement fixture runs the real shipped selector (Spells/ItemSlow.luaobj's cascade), not a
        // stub: the penalty lives on the ItemSlow marker and is chosen by row name.
        public void SyncItemSlow(Entity target) => BuffLifecycle.SyncItemSlow(target);
    }
}
