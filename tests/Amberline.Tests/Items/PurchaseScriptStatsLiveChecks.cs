using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// The batch-144 purchase group at the live loop: the stat or the named stat buff each converted item's own shipped
    /// script keeps alive, measured on a hero that bought it. This is the coverage that left
    /// <c>items.inventory.modifiers</c> and <c>stats.hero.corpus</c>: the inventory aggregation carries the inibin rows
    /// only, and the script's own numbers reach the hero through <c>BbItemModule.Poll</c> and its buffs. Each case is a
    /// match of its own so a sold copy's deactivation cannot leave a permanent modifier behind.
    /// </summary>
    [Check("items.live-purchase-script-stats")]
    public static void PurchaseScriptsOwnTheirStats()
    {
        // (bare max HP, and each field as the change the purchase made) of a hero that bought the ids.
        (double bareHp, double hp, double ap, double ad, double crit, double cd, double penFlat, double penPct,
            double vamp, double armorPen, double armorPenFlat) Measure(double seconds, params int?[] ids)
        {
            using var match = new Match("Annie", Team.Order, ally: true);
            var hero = match.Hero(0);
            match.Runtime.HeroStats.Refresh(hero);
            var bareHp = hero.MaxHealth;
            var hp = hero.MaxHealth; var ap = hero.AbilityPower; var ad = hero.AttackDamage;
            var crit = hero.CritMultiplier; var cd = hero.CooldownMod;
            var penFlat = hero.MagicPenetrationFlat; var penPct = hero.MagicPenetrationPercent;
            var vamp = hero.SpellVamp; var armorPen = hero.ArmorPenetrationPercent;
            var armorPenFlat = hero.ArmorPenetrationFlat;
            var slots = new InventorySlot?[Amberline.Simulation.Inventory.SlotCount];
            for (var i = 0; i < ids.Length && i < slots.Length; i++)
                slots[i] = ids[i] is { } id ? new InventorySlot(id) : null;
            hero.Inventory = slots;
            Advance(match, seconds);
            match.Runtime.HeroStats.Refresh(hero);
            return (bareHp, hero.MaxHealth - hp, hero.AbilityPower - ap, hero.AttackDamage - ad,
                hero.CritMultiplier - crit, hero.CooldownMod - cd, hero.MagicPenetrationFlat - penFlat,
                hero.MagicPenetrationPercent - penPct, hero.SpellVamp - vamp,
                hero.ArmorPenetrationPercent - armorPen, hero.ArmorPenetrationFlat - armorPenFlat);
        }

        // DoranT2Health is one buff (BUFF_RENEW_EXISTING, MaxStack 1) and its OnBuffActivate takes the HealthVar the
        // poll wrote (200) through IncPermanentFlatHPPoolMod, so two copies land the pool once. That is the sharing
        // the old Inventory.ScriptedStaticModifiers row had with its passiveKey rule, now the script's own.
        var oneDoran = Measure(.12, 1062);
        TestAssert.Near(200.0, oneDoran.hp, 1e-9, "one Doran copy is +200 max HP");
        var twoDorans = Measure(.12, 1062, 1063);
        TestAssert.Near(200.0, twoDorans.hp, 1e-9, "two Doran copies share one DoranT2Health");
        TestAssert.Near(30.0, twoDorans.ap, 1e-9, "Doran's Ring's flat AP is its inibin row and stays data");

        TestAssert.Near(0.5, Measure(.12, 3031).crit, 1e-9, "InfinityEdge is +0.5 crit damage");

        TestAssert.Near(20.0, Measure(.12, 3020).penFlat, 1e-9,
            "Sorcerer's Shoes' UpdateSelfBuffStats is 20 flat magic pen per copy");
        TestAssert.Near(40.0, Measure(.12, 3020, 3020).penFlat, 1e-9,
            "two Sorcerer's Shoes sample twice");

        TestAssert.Near(-0.25, Measure(.12, 3115, 3115, 3115).cd, 1e-9,
            "three Nashor's Teeth share one NashorsToothCD");

        // ZhonyasRing's BuffOnUpdateActions reads the flat pool, takes its own prior share off it and keeps 30 percent
        // of what is left; the settled pool is 1.30 times the flat AP the item and the buffs contribute. Deathcap's
        // inibin row is 140, so the hero holds 182. The share lands through BuffOnUpdateStats, which batch 150 part 2a
        // samples on the 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval): one sample per 0.25 s, the share
        // geometric (about 0.23 of the remaining gap each), so 0.12 s only catches the inibin 140 and the loop needs
        // about twenty samples - the five seconds measured here - to reach 182 within the 1e-9 below. Re-measured after
        // ten seconds: the feedback loop settles instead of compounding.
        var deathcap = Measure(5.0, 3089);
        TestAssert.Near(182.0, deathcap.ap, 1e-9, "Deathcap's shipped multiplier settles at 1.30 over its 140 flat AP");
        var deathcapLate = Measure(10.0, 3089);
        TestAssert.Near(deathcap.ap, deathcapLate.ap, 1e-9, "the ZhonyasRing feedback loop is settled, not compounding");

        TestAssert.Near(0.40, Measure(.12, 3135).penPct, 1e-9, "VoidStaff is 40 percent magic penetration");
        TestAssert.Near(0.40, Measure(.12, 3035).armorPen, 1e-9, "LastWhisper is 40 percent armor penetration");
        TestAssert.Near(0.15, Measure(.12, 3145).vamp, 1e-9, "HextechRevolver is 15 percent spell vamp");

        var atmas = Measure(.12, 3005);
        TestAssert.Near(atmas.bareHp * 0.02, atmas.ad, 1e-9,
            "AtmasImpaler turns 2 percent of max health into attack damage");

        TestAssert.Near(-0.15, Measure(.12, 3024).cd, 1e-9, "GlacialShroud is -0.15 cooldown");
        TestAssert.Near(-0.15, Measure(.12, 3158).cd, 1e-9, "IonianBootsofLucidity is -0.15 cooldown");
        var kindle = Measure(.12, 3067);
        TestAssert.Near(-0.10, kindle.cd, 1e-9, "Kindlegem is -0.10 cooldown");
        TestAssert.Near(200.0, kindle.hp, 1e-9, "Kindlegem's 200 health is its inibin row");
        TestAssert.Near(-0.10, Measure(.12, 3108).cd, 1e-9, "FiendishCodex is -0.10 cooldown");
        TestAssert.Near(-0.10, Measure(.12, 3101).cd, 1e-9, "Sting is -0.10 cooldown");
        var brutalizer = Measure(.12, 3134);
        TestAssert.Near(-0.10, brutalizer.cd, 1e-9, "Brutalizer is -0.10 cooldown");
        TestAssert.Near(15.0, brutalizer.armorPenFlat, 1e-9, "Brutalizer is 15 flat armor penetration");

        // HauntingGuise and RitualStaff are the two items whose poll block is a BBExecutePeriodically with
        // ExecuteImmediately false: the shipped script's own stat is not there at the purchase and lands when that
        // block first runs, nine seconds in. Reconstruction: the first-fire rule of ExecuteImmediately false is the
        // engine's (the client's is not in the shipped files); the parameter itself is the script's.
        var guiseEarly = Measure(3.0, 3136);
        TestAssert.Near(0.0, guiseEarly.penFlat, 1e-9,
            "HauntingGuise's poll block has ExecuteImmediately false, so its pen is not there at the purchase");
        TestAssert.Near(20.0, Measure(12.0, 3136).penFlat, 1e-9,
            "HauntingGuise is 20 flat magic pen from the moment its poll block first runs");

        // Ritual Staff's OnActivate adds the FiendishCodex buff it upgrades from (only when the owner does not hold it
        // already) with a 10 second life, and its poll block installs RitualStaff every nine seconds. The two are
        // separate names and the buy's buff lapses, so the steady state is RitualStaff's alone — the -0.20 the old
        // hand-written row had. The first ten seconds carry the Codex's -0.10 instead.
        var ritualEarly = Measure(.12, 3165);
        TestAssert.Near(-0.10, ritualEarly.cd, 1e-9, "the buy's FiendishCodex is -0.10 for its own ten seconds");
        var ritualLate = Measure(12.0, 3165);
        TestAssert.Near(-0.20, ritualLate.cd, 1e-9, "RitualStaff settles at -0.20 cooldown");
        TestAssert.Near(Measure(12.0, 3108).cd + ritualLate.cd, Measure(12.0, 3108, 3165).cd, 1e-9,
            "3165's OnActivate leaves a held Fiendish Codex's own buff alone (BBIfHasBuff), so the pair is not doubled");

        // Batch 149 step 2, the active-window group. Each of the five keeps its named internal buff alive from
        // UpdateSelfBuffActions (a BBExecutePeriodically every nine seconds re-adding a ten-second window) and the
        // window itself owns the static row: OnBuffActivate's BBIncPermanentStat IncPermanentPercentCooldownMod taken
        // back in OnBuffDeactivate (Youmuu's blade adds IncPermanentFlatArmorPenetrationMod +20 the same way), except
        // SpiritVisage, whose window carries IncPercentCooldownMod -0.10 on its own BuffOnUpdateStats payload. 3065
        // also opens that window from OnActivate; the other four wait for the poll block, which is immediate for
        // 3128/3142/3143 (ExecuteImmediately true) and nine seconds away for 3069's (ExecuteImmediately false).
        TestAssert.Near(-0.10, Measure(.12, 3065).cd, 1e-9,
            "SpiritVisage opens its window from OnActivate and that window is -0.10 cooldown");
        TestAssert.Near(0.0, Measure(.12, 3069).cd, 1e-9,
            "Shurelya's poll block is ExecuteImmediately false, so its window is not open at the purchase");
        TestAssert.Near(-0.15, Measure(12.0, 3069).cd, 1e-9,
            "ShurelyasCrest's window is -0.15 cooldown from its own permanent row once the poll block first runs");
        TestAssert.Near(-0.15, Measure(.12, 3128).cd, 1e-9, "DeathfireGrasp's window is -0.15 cooldown");
        var youmuu = Measure(.12, 3142);
        TestAssert.Near(-0.15, youmuu.cd, 1e-9, "YoumusBlade's window is -0.15 cooldown");
        TestAssert.Near(20.0, youmuu.armorPenFlat, 1e-9, "YoumusBlade's window is 20 flat armor penetration");
        TestAssert.Near(-0.05, Measure(.12, 3143).cd, 1e-9, "RanduinsOmen's window is -0.05 cooldown");

        // 3187 Hextech Sweeper opens the same kind of window (its Items/3187.luaobj poll block is a
        // BBExecutePeriodically every nine seconds re-adding HextechSweeper, whose OnBuffActivate is
        // IncPermanentPercentCooldownMod -0.1) and until batch 149 step 4 an Inventory.ScriptedStaticModifiers row put
        // the same -0.10 beside it, so the hero held -0.20. Script wins: the row is gone and the window's own number is
        // the whole of it, at the purchase and once the ten-second renewal has cycled.
        TestAssert.Near(-0.10, Measure(.12, 3187).cd, 1e-9, "HextechSweeper's window is -0.10 cooldown");
        TestAssert.Near(-0.10, Measure(12.0, 3187).cd, 1e-9, "HextechSweeper's renewal window settles at -0.10, not -0.20");
    }
}
