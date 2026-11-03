using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-healing-modifiers-once")]
    public static void HealingModifiersApplyOnceToEverySource()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var visageCopies in new[] { 0, 1, 2 })
        foreach (var woundSource in new[] { "none", "ignite", "executioner" })
        {
            using var match = new Match("Nidalee", team, ally: true);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var ally = match.Hero(2);
            hero.Inventory = [new(1053), new(3145), null, null, null, null];
            // Batch 144: 3145's 15 percent spell vamp is the HextechRevolver buff its own script keeps alive now, not an
            // inventory modifier, so the purchase poll has to run before the baseline is read — otherwise this fixture
            // silently measures zero vamp and loses the coverage it was written for.
            Advance(match, .12);
            match.Runtime.HeroStats.Refresh(hero);
            var baseRegen = hero.HealthRegen;
            var baseLifesteal = hero.LifeSteal;
            var baseVamp = hero.SpellVamp;
            var slots = hero.Inventory.ToArray();
            for (var i = 0; i < visageCopies; i++) slots[2 + i] = new(3065);
            hero.Inventory = slots;
            // Batch 149 step 2: 3065 is its own shipped script now (Items/3065.luaobj opens the SpiritVisage window from
            // OnActivate and re-adds it every nine seconds), so the copy has to bind before anything is measured.
            Advance(match, .12);
            hero.MaxHealth = 5000;
            hero.Health = 1000;
            if (woundSource == "ignite")
            {
                enemy.SummonerSpells[0] = "SummonerDot";
                match.Cast(1, 0, hero, summoner: true);
                // Isolate the real Ignite debuff from its independent damage. It is Spells/GrievousWound.luaobj: the
                // SummonerDot script adds that buff by name and the engine runs the shipped hooks on it.
                match.Runtime.Buffs.RemoveBuff(hero, hero.Buffs.Single(b => b.Name == "SummonerDot").Slot);
            }
            else if (woundSource == "executioner")
            {
                enemy.Inventory = [new(3123), null, null, null, null, null];
                match.Cast(1, 4, hero);
            }
            // Both scripts re-derive their own sustain share on every buff update (the factors below), so the sample is
            // taken once the pair has settled.
            Advance(match, 2.0);
            match.Runtime.HeroStats.Refresh(hero);

            // Script wins (Spells/SpiritVisage.luaobj and Spells/GrievousWound.luaobj): the window raises the three
            // sustain pools itself and the shipped wound cuts them again, where the hand-written path only ever
            // amplified or reduced the heal and left the stats alone. REPLACE_EXISTING keeps one window however many
            // copies the holder wears, so the raise is the same for one copy and for two.
            var visage = visageCopies != 0;
            // Batch 149 step 4: both wound sources land the shipped Spells/GrievousWound.luaobj now — Ignite's
            // SummonerDot script names it and Executioner's Calling casts its shipped Spells/Mourning.luaobj, whose
            // TargetExecute adds the same buff beside its own Internal_50MS — so one stats hook serves both. The
            // hand-written active's received-heal-only payload is gone with its cast.
            var scriptWound = woundSource != "none";
            // The window's IncPercentHPRegenMod +0.15 and the wound's -0.5 are one accumulator the calculator reads as
            // (1 + sum).
            var regenFactor = 1 + (visage ? .15 : 0) + (scriptWound ? -.5 : 0);
            // The life-steal pool carries the share each update hook re-derives (share = 0.15 * (pool - share) for the
            // window, share = -0.5 * (pool - share) for the wound). Solving the pair together leaves the window 3/43 of
            // the base pool and the wound -23/43 of it, so the two leave 23/43 of the base pool.
            var lifeStealFactor = visage && scriptWound ? 23.0 / 43.0 : visage ? 1.15 : scriptWound ? .5 : 1;
            // The wound's spell-vamp share is the shipped quirk: its seed AND its own re-derivation read
            // GetPercentSpellBlockMod (GrievousWound.luaobj's only stat read), so the share solves to minus the holder's
            // percent spell-block modifier and is zero for a holder with none — the wound never cuts spell vamp here.
            // The window's re-derivation does read GetPercentSpellVampMod (only its seed is the same quirk), so it
            // raises the pool by 15 percent the same way it raises life steal.
            var spellVampFactor = visage ? 1.15 : 1;
            // The one received-heal stage is the scripts' own too: the window's BuffOnHeal returns Health * 1.15 for a
            // heal whose target is the holder, and the wound's own BuffOnHeal halves it. A life-steal or
            // regeneration heal carries this stage as well as the stat change above, so the shipped scripts touch those
            // twice (1.15 raised then 1.15 amplified, 0.5 cut then 0.5 reduced) — kept as the scripts write it and
            // listed as an open question in docs/maintenance/2026-09-21-healing-order.md.
            var stage = (woundSource == "none" ? 1 : .5) * (visage ? 1.15 : 1);
            TestAssert.Near(baseRegen * regenFactor, hero.HealthRegen, 1e-8,
                $"{team}/{visageCopies}/{woundSource}: the window and the shipped wound move regeneration in one accumulator");
            TestAssert.Near(baseLifesteal * lifeStealFactor, hero.LifeSteal, 1e-8,
                $"{team}/{visageCopies}/{woundSource}: the two share pairs re-derive the life-steal pool");
            TestAssert.Near(baseVamp * spellVampFactor, hero.SpellVamp, 1e-8,
                $"{team}/{visageCopies}/{woundSource}: the window's share raises spell vamp, the wound's quirk share does not cut it");
            if (woundSource != "none")
            {
                var payload = hero.Buffs.Single(b => b.Name == "GrievousWound").Payload;
                // The wound writes no multiplicative sustain field. It is the shipped script's own stats hook
                // (GrievousWound.luaobj's BuffOnUpdateStats, gated on the Internal_50MS the same cast adds with the
                // target as its caster) cutting regeneration and carrying the life-steal share; its spell-vamp share
                // is the quirk above and writes nothing.
                TestAssert.False(payload.Has(BuffPayloadField.LifeStealMult) ||
                    payload.Has(BuffPayloadField.SpellVampMult), "no wound multiplies the sustain pools");
                TestAssert.Near(-.5, payload.HpRegenMult, 0,
                    "the shipped wound cuts regeneration from its own stats hook");
                TestAssert.True(payload.Has(BuffPayloadField.LifeSteal),
                    "the shipped wound carries its life-steal share");
                TestAssert.Near(0, payload.SpellVamp, 0,
                    "the shipped wound's spell-vamp share is its spell-block quirk and stays zero");
            }
            hero.MaxHealth = 5000;
            enemy.MaxHealth = enemy.Health = 50000;
            enemy.Armor = enemy.SpellBlock = 0;
            Gain(1000 * baseLifesteal * lifeStealFactor * stage, () => match.Runtime.Combat.ApplyHit(
                hero.NetId, enemy.NetId, 1000, DamageType.True, DamageSourceKind.Basic));
            Gain(1000 * baseVamp * spellVampFactor * stage, () => match.Runtime.Combat.ApplyHit(
                hero.NetId, enemy.NetId, 1000, DamageType.True, DamageSourceKind.Spell));
            // playtest B4 / retail sweep, 27 Sept 2026: combat owns the shipped
            // 0.333 AoE ratio; spell callers no longer pre-scale it.
            Gain(1000 * baseVamp * .333 * spellVampFactor * stage, () => match.Runtime.Combat.ApplyHit(
                hero.NetId, enemy.NetId, 1000, DamageType.True, DamageSourceKind.SpellAoe));
            Gain(baseRegen * regenFactor * stage, () => match.Runtime.Combat.CombatTick(1, regenerate: true));

            // A controlled periodic heal exercises the same path as potions
            // and buff-based item heals, including the user's 200 -> 115 case.
            match.Runtime.Buffs.AddBuff(hero, "HealingModifierProbe", BuffType.CombatEnhancer,
                1, hero.NetId, new BuffPayload { Period = 1, HpPerTick = 200 });
            Gain(200 * stage, () => match.Runtime.Buffs.TickBuffs(hero, 1));

            hero.SummonerSpells[0] = "SummonerHeal";
            ally.Health = 1000;
            Gain(160 * stage, () => match.Cast(0, 0, hero, summoner: true));
            TestAssert.Near(1080, ally.Health, 1e-8, "the caster's modifiers do not change healing received by an ally");

            hero.AbilityPower = 40; // Hextech Revolver; refresh during Primal Surge retains it.
            Gain(78 * stage, () =>
            {
                match.Cast(0, 2, hero);
                match.Runtime.Spells.Tick(.3);
            }, resetMaxHealth: false);

            // Warmog's growth and Force of Nature's percent heal share one second of sustain before the one
            // incoming-heal stage: the growth is already inside itemRegen (the window's and the wound's own
            // regeneration rows are too, and the stage multiplies the lot), while the heal is
            // Spells/ForceofNature.luaobj's own BuffOnUpdateActions — a one-second BBExecutePeriodically of
            // 0.0035 * max health (ExecuteImmediately false, so its first pulse lands a second after the row
            // Items/3109.luaobj opens on activate). It is a heal from that hook and not a regeneration stat, so
            // the second below is the spell clock's and the buff update's the way a match's steps run them, and
            // BBIncHealth carries the one incoming-heal stage itself.
            slots = hero.Inventory.ToArray();
            slots[4] = new(3083);
            slots[5] = new(3109);
            hero.Inventory = slots;
            // Batch 149 step 1: Warmog's growth is Items/3083.luaobj's own ItemOnKill counter (InstanceVars.ExtraRegen,
            // read back by UpdateSelfBuffStats), not a hand-written inventory field any more. The copy binds on the
            // next spell update and the sample runs with the following one.
            Advance(match, .12);
            match.Runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(4)!.InstanceVars["ExtraRegen"] = 1.75;
            Advance(match, .12);
            match.Runtime.HeroStats.Refresh(hero);
            var itemRegen = hero.HealthRegen;
            hero.MaxHealth = 5000;
            hero.Health = 100;
            match.Runtime.Combat.CombatTick(1, regenerate: true);
            var regenerated = hero.Health;
            match.Runtime.Spells.Tick(1);
            // The pulse reads the holder's maximum health at its own run, and the buff tick's own stats sample (the window and
            // the wound re-derive their payload there) refreshes the unit first, restoring the hero's computed maximum over
            // the fixture's 5000. Refresh once here and take the maximum the pulse will read, so the assertion measures the
            // pulse and the regeneration, not the fixture being reset under it.
            match.Runtime.HeroStats.Refresh(hero);
            var pulseHealth = hero.MaxHealth;
            match.Runtime.Buffs.TickBuffs(hero, 1);
            TestAssert.Near(regenerated + pulseHealth * .0035 * stage, hero.Health, 1e-6,
                $"{team}/{visageCopies}/{woundSource}: one incoming-heal modifier (regeneration and the Force of Nature pulse at {pulseHealth:0} maximum health)");
            hero.Inventory = hero.Inventory.Select(slot => slot?.ItemId == 3109 ? null : slot).ToArray();
            hero.HealthRegen = 0;
            hero.Position = team == Team.Order ? new(26, 264) : new(13933.79, 14170.09);
            // playtest B4 / retail sweep, 27 Sept 2026: fountain restores
            // 8.5% maximum HP on each global one-second boundary.
            Gain(425 * stage, () => match.Runtime.Combat.CombatTick(1, regenerate: true));
            hero.Health = hero.MaxHealth - 10;
            match.Runtime.Combat.CombatTick(1, regenerate: true);
            TestAssert.Near(hero.MaxHealth, hero.Health, 0, "clamp after modifiers");

            // Expiry and removal take effect without stale sustain factors. The window is the script's own buff: only the
            // removed copy's UpdateSelfBuffActions re-adds it, so the factor (and the stat raise) stay for the rest of the
            // window's ten seconds and drop when the buff itself runs out — the hand-written path lost both at the sale.
            hero.Inventory = hero.Inventory.Select(slot => slot?.ItemId == 3065 ? null : slot).ToArray();
            Advance(match, .12);
            TestAssert.Near(stage, match.Runtime.Combat.HealingReceivedMultiplier(hero), 0,
                "the window outlives the item that opened it");
            match.Runtime.Buffs.TickBuffs(hero, 12);
            TestAssert.Near(1, match.Runtime.Combat.HealingReceivedMultiplier(hero), 0);

            void Gain(double expected, Action action, bool resetMaxHealth = true)
            {
                if (resetMaxHealth) hero.MaxHealth = 5000;
                hero.Health = 100;
                action();
                TestAssert.Near(100 + expected, hero.Health, 1e-6,
                    $"{team}/{visageCopies}/{woundSource}: one incoming-heal modifier");
            }
        }
    }

    [Check("items-live-healing-recipient-and-blocks")]
    public static void AlliedHealingUsesRecipientModifiersAndPreservesHealingBlocks()
    {
        using var match = new Match("Janna", Team.Order, ally: true);
        var caster = match.Hero(0);
        var ally = match.Hero(2);
        ally.Inventory = [new(3065), null, null, null, null, null];
        // Batch 149 step 2: the Spirit Visage window opens from the copy's own OnActivate on the next spell update.
        Advance(match, .12);
        match.Runtime.HeroStats.Refresh(ally);
        match.Runtime.Buffs.AddBuff(ally, "GrievousWound", BuffType.CombatDehancer, 8,
            match.Hero(1).NetId, new BuffPayload { HealingReceivedMult = -.5 });
        ally.MaxHealth = 5000;
        ally.Health = 100;
        caster.AbilityPower = 0;
        // The first pulse comes when the 0.25 s windup ends (ChannelingStart), the next two every 0.5 s after it; the runtime also regenerates a
        // little, hence the tolerance.
        match.Cast(0, 3, caster);
        // The ally also regenerates continuously under the same recipient multiplier (Visage, the wound), so the expected health carries that
        // regeneration exactly instead of hiding it in a tolerance. The window's own +0.15 to HP regeneration is already in the live stat.
        var regenPerSecond = ally.HealthRegen * match.Runtime.Combat.HealingReceivedMultiplier(ally);
        TestAssert.Near(.5 * 1.15, match.Runtime.Combat.HealingReceivedMultiplier(ally), 0,
            "the recipient's window multiplies the heal after the wound, once");
        Advance(match, .3);
        TestAssert.Near(100 + 35 * .5 * 1.15 + regenPerSecond * .3, ally.Health, .02, "Monsoon immediate heal uses recipient modifiers");
        Advance(match, 1.1);
        TestAssert.Near(100 + 105 * .5 * 1.15 + regenPerSecond * 1.4, ally.Health, .02, "Monsoon later pulses use the same rule");
        match.Runtime.Buffs.AddBuff(ally, "HealingBlock", BuffType.Internal, 5,
            ally.NetId, new BuffPayload { HealingReceivedMult = -1 });
        TestAssert.Near(0, match.Runtime.Combat.HealingReceivedMultiplier(ally), 0,
            "Visage cannot restore a heal blocked entirely");
        var before = ally.Health;
        ally.Position = new(26, 264);
        match.Runtime.Combat.CombatTick(1, regenerate: true);
        TestAssert.Near(before, ally.Health, 0, "blocked regeneration and fountain heal remain zero");
    }
}
