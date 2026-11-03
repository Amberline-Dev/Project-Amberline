using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (26 October 2026, batch 280): Sion plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the literals of the shipped scripts and inibins: Feel No Pain (a 0.4 chance per basic attack to block
/// 30/40/50 by level), Cryptic Gaze (70/125/180/240/300 magic and a 1.5 second stun), Death's Caress (a shield of 100/150/200/250/300 plus the
/// ability power, a 10 second life that explodes for its whole amount when unbroken, a second cast that sets it off, the slot's cooldown of 4 and
/// 8 seconds), Enrage (25/35/45/55/65 bonus damage, 2 x rank + 4 health per attack, off below 15 health, a kill adds rank x 0.5 + 0.5 maximum
/// health), Cannibalism (50 percent lifesteal, 50 percent attack speed, a quarter of the damage healed to hurt allies within 350).
/// The scenario <c>S-Sion</c> shows the same facts, and the death and respawn, on a headless match.
/// </summary>
public static class BbSionChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static bool Near(double expected, double actual, double tolerance) => Math.Abs(expected - actual) <= tolerance;

    /// <summary>Sion at <paramref name="rank"/> on every spell, the foe in front of him. Armour and magic resistance are 0, so the numbers are exact.</summary>
    private static Match Arena(int rank = 1, double gap = 100.0)
    {
        var match = new Match("Sion", foeChampion: "Orianna");
        match.Learn(rank);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Hero.Facing = new Position2(1.0, 0.0);
        // Melee: the foe stands inside his attack range (125), so a swing is a real attack.
        match.Foe.Position = new Position2(7000.0 + gap, 7000);
        match.Foe.Armor = 0.0;
        match.Foe.SpellBlock = 0.0;
        match.Hero.Armor = 0.0;
        match.Hero.SpellBlock = 0.0;
        // A 1000-point hit must not kill him: the shield checks read the whole amount that gets through.
        match.Hero.MaxHealth = 10000.0;
        match.Hero.Health = 10000.0;
        return match;
    }

    private static void Swing(Match match, Entity target, double seconds = 1.2)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target), "swing");
        match.Advance(seconds);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosure()
    {
        var module = new BbChampionModule("Sion");
        foreach (var name in new[] { "CharScriptSion", "CrypticGaze", "DeathsCaressFull", "DeathsCaress", "Enrage", "EnrageMaxHP", "Cannibalism", "FeelNoPain" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Sion");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(16, closure.ScriptNames.Count(), "the closure is 16 scripts (bb-coverage): his spells, passive, buffs and the system buffs");
        var match = Arena();
        TestAssert.True(Buff(match.Hero, "FeelNoPain") is not null, "the passive is added when he is created");
        TestAssert.Equal("", Failures(match), "no script failure at the start");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Feel No Pain
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-feel-no-pain-blocks-thirty-of-about-four-in-ten-attacks")]
    public static void FeelNoPainBlocksPartOfTheAttacks()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        var attack = foe.AttackDamage;
        var blocked = 0;
        var whole = 0;
        // 100 swings (about 100 seconds of match time) end before the first wave reaches the middle of the map.
        const int swings = 100;
        // The match seeds its Lua C-runtime stream from the wall clock (LevelScript's math.randomseed(os.time())); pin it so the share is the
        // same every run (one unpinned run read 0.24). The per-draw rule is pinned by -rolls-the-script-stream-once-per-attack.
        match.Runtime.World.LuaRandom.HoldRand = 2801u;
        var samples = new List<double>();
        for (var index = 0; index < swings; index++)
        {
            hero.Health = hero.MaxHealth;
            var before = hero.Health;
            // The foe is another champion with its own AI: he is kept in reach, alive (the waves reach the middle of the map after a minute and a half)
            // and swinging fast, so 200 swings fit before any wave arrives.
            foe.Position = new Position2(hero.Position.X + 100.0, hero.Position.Z);
            foe.MaxHealth = 10000.0;
            foe.Health = 10000.0;
            foe.AttackSpeedMod = 4.0;
            foe.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(foe, hero),
                $"the foe swings at him (swing {index}, cooldown {foe.AttackCooldown:F2}, can {match.Runtime.Combat.CanAttack(foe, hero)}, alive {foe.Alive})");
            // A swing lasts a fraction of a second at four times the attack speed; the next one waits for it.
            match.Advance(1.2);
            // Each swing deals its whole damage, or 30 less when the passive blocks (30 at rank one), less the regeneration of the window (about 2).
            samples.Add(before - hero.Health);
        }
        // A whole swing is the largest loss (its damage is the foe's, read from the hit itself); a blocked one is 30 less (30 at rank one).
        var full = samples.Max();
        foreach (var lost in samples)
        {
            if (Near(full - 30.0, lost, 3.0)) blocked++;
            else if (Near(full, lost, 3.0)) whole++;
        }
        var share = blocked / (double)swings;
        TestAssert.True(blocked + whole >= swings * 0.9, $"each swing deals its damage, or 30 less when blocked (blocked {blocked}, whole {whole}, damage {attack:F1}, armour {hero.Armor:F1}, losses {string.Join(" ", samples.Take(8).Select(sample => sample.ToString("F1")))})");
        // 100 swings give the share a spread of about 0.05 around 0.4; the band is wide on purpose (a rate of 0 or 1 is a broken chance, not noise).
        // The roll draws the match's Lua C-runtime stream, which this match seeds from the wall clock (LevelScript's math.randomseed(os.time())),
        // so each run is a fresh sample (one read 0.52, 2.4 standard deviations high); the exact roll is the check
        // scripting.bb-sion-feel-no-pain-rolls-the-script-stream-once-per-attack.
        TestAssert.True(share > 0.25 && share < 0.60, $"about 40 percent of the swings are blocked (got {share:F2})");

        hero.Health = hero.MaxHealth;
        var spellBefore = hero.Health;
        match.Runtime.Combat.QueueScriptedHit(match.Foe, hero, 50.0, DamageType.Physical, DamageSourceKind.Spell, 0.0);
        match.Advance(0.3);
        TestAssert.True(Near(50.0, spellBefore - hero.Health, 1.5), "a spell hit is not an attack and is not blocked");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// Batch 280 finishing pass, finding 2: the scenario's blocked swings lost 12.45 where "whole less 30" (4.3) was expected. The block is the
    /// script's <c>DamageAmount - min(BaseBlockAmount, DamageAmount)</c> in <c>CharOnBeingHit</c>, and the engine hands that hook the hit before
    /// armour (the phase Harden Skin's <c>CharOnBeingHit</c> and Thornmail's <c>DamageAmount</c> share), so with armour the 30 comes off the raw
    /// hit: a blocked swing is <c>(damage - 30) x 100 / (100 + armour)</c>, 30 x 100 / (100 + armour) under a whole one (20 at armour 50). The
    /// swings are read from his physical damage taken (the regeneration of the window does not enter).
    /// </summary>
    [Check("scripting.bb-sion-feel-no-pain-takes-the-block-off-before-armour")]
    public static void FeelNoPainBlocksBeforeArmour()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Armor = 50.0;
        var damage = foe.AttackDamage;
        var factor = 100.0 / (100.0 + hero.Armor);
        int whole = 0, blocked = 0;
        for (var swing = 0; swing < 40 && (whole == 0 || blocked == 0); swing++)
        {
            hero.Health = hero.MaxHealth;
            foe.Position = new Position2(hero.Position.X + 100.0, hero.Position.Z);
            foe.AttackCooldown = 0.0;
            var before = hero.MatchStatistics.PhysicalDamageTaken;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(foe, hero), $"the foe swings at him (swing {swing})");
            match.Advance(1.2);
            var lost = hero.MatchStatistics.PhysicalDamageTaken - before;
            if (Near(damage * factor, lost, 0.01)) whole++;
            else if (Near((damage - 30.0) * factor, lost, 0.01)) blocked++;
            else TestAssert.True(false, $"a swing is whole ({damage * factor:F2}) or blocked before armour ({(damage - 30.0) * factor:F2}), got {lost:F2}");
        }
        TestAssert.True(whole > 0 && blocked > 0, $"both kinds of swing were seen (whole {whole}, blocked {blocked})");
        TestAssert.Near(50.0, hero.Armor, 1e-9, "his armour stayed at 50 for the run");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// Batch 280 finishing pass, finding 3: one 100-swing run blocked 52 percent. The roll is the shipped <c>CO_RANDOM_CHANCE_LESS_THAN</c>
    /// (BuildingBlocksBase proto 24: <c>math.random() &lt; BlockChance</c>), one draw per landed attack from the match's C runtime stream (MSVCR80
    /// <c>rand()</c>, 214013 / 2531011, <c>(rand() % RAND_MAX) / RAND_MAX</c>). That stream is shared with every Lua script of the match and is
    /// seeded by LevelScript's <c>math.randomseed(os.time())</c>, the wall clock outside a seeded match, so the 100-swing run is a fresh sample each
    /// time it runs. Here the stream is pinned and 400 attacks land back to back: each one blocks exactly when the predicted draw is under 0.4.
    /// </summary>
    [Check("scripting.bb-sion-feel-no-pain-rolls-the-script-stream-once-per-attack")]
    public static void FeelNoPainRollsOncePerAttack()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        var crt = match.Runtime.World.LuaRandom;
        crt.HoldRand = 280u;
        var state = crt.HoldRand;
        int blocked = 0, mismatched = 0;
        const int attacks = 400;
        for (var attack = 0; attack < attacks; attack++)
        {
            state = unchecked(state * 214013u + 2531011u);
            var draw = (double)(((state >> 16) & 0x7fff) % 0x7fff) / 0x7fff;
            hero.Health = hero.MaxHealth;
            var before = hero.MatchStatistics.PhysicalDamageTaken;
            TestAssert.True(match.Runtime.Combat.ApplyHit(foe.NetId, hero.NetId, 60.0, DamageType.Physical, DamageSourceKind.Basic), $"attack {attack} lands");
            var wasBlocked = Near(30.0, hero.MatchStatistics.PhysicalDamageTaken - before, 1e-6);
            if (wasBlocked) blocked++;
            if (wasBlocked != draw < 0.4) mismatched++;
        }
        TestAssert.Equal(0, mismatched, "each attack blocks exactly when its own draw of the stream is under 0.4");
        TestAssert.Equal(state, crt.HoldRand, "one draw per attack and no other");
        var share = blocked / (double)attacks;
        TestAssert.True(share > 0.33 && share < 0.47, $"about 40 percent of 400 attacks are blocked (got {share:F3})");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Cryptic Gaze (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-cryptic-gaze-hurts-and-stuns-for-a-second-and-a-half")]
    public static void CrypticGazeHurtsAndStuns()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Mana = hero.MaxMana;
        var ability = hero.AbilityPower;
        var before = foe.Health;
        TestAssert.True(match.Cast(0, foe), "Q casts at a unit in range");
        match.Advance(0.6);
        var lost = before - foe.Health;
        TestAssert.True(Near(70.0 + ability, lost, 1.5), $"70 at rank one plus the ability power (lost {lost:F1}, ability power {ability:F1})");
        TestAssert.True(foe.Buffs.Any(buff => buff.Type == BuffType.Stun), "stunned by the cast");
        match.Advance(1.4);
        TestAssert.False(foe.Buffs.Any(buff => buff.Type == BuffType.Stun), "the stun ends a second and a half after the hit");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death's Caress (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-death-caress-absorbs-then-breaks-and-resets-the-slot")]
    public static void DeathsCaressShieldBreaks()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Mana = hero.MaxMana;
        var ability = hero.AbilityPower;
        var foeBefore = foe.Health;
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "DeathsCaress") is not null, "the shield is up");
        TestAssert.Equal("DeathsCaress", hero.ActiveSpellNames[1], "the W slot now holds the second cast");

        // The shield is 100 + ability power at rank one: 50 is absorbed whole, then 1000 breaks what is left.
        var heroBefore = hero.Health;
        match.Runtime.Combat.QueueScriptedHit(foe, hero, 50.0, DamageType.Magic, DamageSourceKind.Spell, 0.0);
        match.Advance(0.3);
        TestAssert.True(Near(heroBefore, hero.Health, 1.5), "a 50 hit is absorbed whole");
        match.Runtime.Combat.QueueScriptedHit(foe, hero, 1000.0, DamageType.Magic, DamageSourceKind.Spell, 0.0);
        match.Advance(0.3);
        var remaining = 100.0 + ability - 50.0;
        TestAssert.True(Near(1000.0 - remaining, heroBefore - hero.Health, 2.0), "the broken shield lets the rest through");
        TestAssert.True(Buff(hero, "DeathsCaress") is null, "a broken shield is gone");
        TestAssert.Equal("DeathsCaressFull", hero.ActiveSpellNames[1], "the slot holds the first cast again");
        TestAssert.True(Near(foeBefore, foe.Health, 0.5), "a broken shield does not explode");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sion-death-caress-explodes-for-its-whole-amount-after-ten-seconds")]
    public static void DeathsCaressExplodesWhenUnbroken()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Mana = hero.MaxMana;
        var ability = hero.AbilityPower;
        var before = foe.Health;
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(10.6);
        var lost = before - foe.Health;
        TestAssert.True(Near(100.0 + ability, lost, 1.5), $"the shield's whole amount hits the foe 525 away (lost {lost:F1})");
        TestAssert.True(Buff(hero, "DeathsCaress") is null, "the shield ends after ten seconds");
        TestAssert.Equal("DeathsCaressFull", hero.ActiveSpellNames[1], "the slot holds the first cast again");
        TestAssert.True(hero.SpellCooldowns[1] > 7.0 && hero.SpellCooldowns[1] <= 8.0, $"the slot's cooldown is 8 seconds from the end (got {hero.SpellCooldowns[1]:F2})");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// Batch 280 finishing pass, finding 1: the scenario's foe (Ezreal: armour 15.5, magic resistance 30) lost his whole 430 health over the
    /// explosion probe. The explosion is the script's <c>MAGIC_DAMAGE</c> of <c>FinalArmorAmount</c> (<c>SpellDamageRatio</c> 0,
    /// <c>PhysicalDamageRatio</c> 1.0), so against that foe it is (100 + ability power) x 100 / 130, one magic hit and nothing physical; the rest of
    /// the scenario's loss was Sion's own auto-attacks (his human seat auto-acquired the foe 300 away for ten seconds).
    /// </summary>
    [Check("scripting.bb-sion-death-caress-explosion-is-one-magic-hit-after-the-foe-resistance")]
    public static void DeathsCaressExplosionIsMitigatedMagic()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Armor = 15.5;
        foe.SpellBlock = 30.0;
        hero.Mana = hero.MaxMana;
        var ability = hero.AbilityPower;
        var stats = foe.MatchStatistics;
        var (physical, magic, total) = (stats.PhysicalDamageTaken, stats.MagicDamageTaken, stats.TotalDamageTaken);
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(10.6);
        var expected = (100.0 + ability) * 100.0 / (100.0 + foe.SpellBlock);
        TestAssert.True(Near(expected, stats.MagicDamageTaken - magic, 0.01),
            $"the explosion is (100 + ability power) after 30 magic resistance (expected {expected:F2}, magic {stats.MagicDamageTaken - magic:F2})");
        TestAssert.Near(0.0, stats.PhysicalDamageTaken - physical, 1e-9, "nothing physical");
        TestAssert.Near(stats.MagicDamageTaken - magic, stats.TotalDamageTaken - total, 1e-9, "and nothing true");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// The <c>BBElse</c> of <c>DeathsCaress.TargetExecute</c>: the second cast without its shield (the slot names <c>DeathsCaress</c>, the buff is
    /// gone) casts extra slot 0 at the variable <c>Level</c> with <c>OverrideCoolDownCheck</c>. The shield's own end always puts the first cast back,
    /// so the state is set by hand here.
    /// </summary>
    [Check("scripting.bb-sion-death-caress-second-cast-without-its-shield-takes-the-else-branch")]
    public static void DeathsCaressSecondCastWithoutShield()
    {
        var match = Arena();
        var hero = match.Hero;
        hero.Mana = hero.MaxMana;
        // The slot's name as BBSetSpell stores it (the list grows to the slot).
        while (hero.ActiveSpellNames.Count <= 1) hero.ActiveSpellNames.Add(string.Empty);
        hero.ActiveSpellNames[1] = "DeathsCaress";
        TestAssert.True(Buff(hero, "DeathsCaress") is null, "no shield");
        // Sion.inibin's Data*ExtraSpell1 is DeathsCaress (no cast time), so the branch casts the second cast again, whose own TargetExecute is in
        // the same branch: before batch 280's nesting cap this overflowed the stack and ended the process.
        var raw = GameData.Default.Champion("Sion").Raw;
        TestAssert.True(raw.TryGetValue(InibinReader.KeyHash("Data*ExtraSpell1"), out var extra) && extra.AsString() == "DeathsCaress",
            "his extra slot 0 is DeathsCaress itself");
        TestAssert.True(match.Cast(1), "the second cast is accepted");
        match.Advance(0.6);
        TestAssert.True(match.Module.ScriptFailures.Count == 1 && match.Module.ScriptFailures[0].Message.Contains("BBSpellCast nested"),
            $"the loop ends as one script failure at the nesting cap ({Failures(match)})");
        TestAssert.True(hero.Alive && Buff(hero, "DeathsCaress") is null, "and nothing else happens: no shield, no explosion");
    }

    [Check("scripting.bb-sion-death-caress-second-cast-sets-the-shield-off-at-once")]
    public static void DeathsCaressSecondCastExplodes()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Mana = hero.MaxMana;
        var ability = hero.AbilityPower;
        var before = foe.Health;
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(4.5);
        TestAssert.True(Buff(hero, "DeathsCaress") is not null, "the shield is still up at 4.5 seconds");
        TestAssert.True(match.Cast(1), "the second W casts once the slot's 4 seconds are over");
        match.Advance(0.5);
        var lost = before - foe.Health;
        TestAssert.True(Near(100.0 + ability, lost, 1.5), $"the second cast explodes the unbroken shield at once (lost {lost:F1})");
        TestAssert.True(Buff(hero, "DeathsCaress") is null, "the shield is gone");
        TestAssert.Equal("DeathsCaressFull", hero.ActiveSpellNames[1], "the slot holds the first cast again");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Enrage (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-enrage-adds-bonus-damage-and-costs-health-per-attack")]
    public static void EnrageAddsDamageAndCostsHealth()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        var damage = hero.AttackDamage;
        TestAssert.True(match.Cast(2), "E casts (no mana)");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "Enrage") is not null, "Enrage is on");
        TestAssert.True(Near(25.0, hero.AttackDamage - damage, 0.5), $"25 bonus damage at rank one (got {hero.AttackDamage - damage:F2})");
        // 6 health per attack at rank one (2 x rank + 4); the regeneration of the swing window (about 2) is added back.
        hero.Health = hero.MaxHealth;
        var before = hero.Health;
        Swing(match, foe);
        var cost = before - hero.Health;
        TestAssert.True(cost > 3.5 && cost < 6.5, $"the swing costs 6 health less the regeneration of its window (lost {cost:F2})");
        TestAssert.True(Buff(hero, "Enrage") is not null, "still on above 15 health");
        TestAssert.True(match.Cast(2), "E casts again to switch it off");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "Enrage") is null, "the second cast switches it off");
        TestAssert.True(Near(0.0, hero.AttackDamage - damage, 0.5), "the bonus damage goes with it");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sion-enrage-switches-off-below-fifteen-health")]
    public static void EnrageSwitchesOffBelowFifteen()
    {
        var match = Arena();
        var hero = match.Hero;
        TestAssert.True(match.Cast(2), "E casts");
        match.Advance(0.5);
        hero.Health = 14.0;
        Swing(match, match.Foe);
        TestAssert.True(Buff(hero, "Enrage") is null, "Enrage ends when the attack would cost health he does not have");
        TestAssert.True(hero.Health >= 14.0, "and the attack costs him nothing (only regeneration moves him)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sion-enrage-kill-adds-maximum-health-and-keeps-it")]
    public static void EnrageKillAddsMaximumHealth()
    {
        var match = Arena();
        var hero = match.Hero;
        TestAssert.True(match.Cast(2), "E casts");
        match.Advance(0.5);
        var maxBefore = hero.MaxHealth;
        match.Foe.Health = 100.0;
        match.Runtime.Combat.QueueScriptedHit(hero, match.Foe, 99999.0, DamageType.True, DamageSourceKind.Spell, 0.0);
        match.Advance(0.5);
        TestAssert.True(Near(1.0, hero.MaxHealth - maxBefore, 0.05), $"the first kill adds rank x 0.5 + 0.5 = 1 maximum health (got {hero.MaxHealth - maxBefore:F2})");
        TestAssert.True(Buff(hero, "EnrageMaxHP") is not null, "the kill buff is on");
        match.FarFoe.Health = 100.0;
        match.Runtime.Combat.QueueScriptedHit(hero, match.FarFoe, 99999.0, DamageType.True, DamageSourceKind.Spell, 0.0);
        match.Advance(0.5);
        TestAssert.True(Near(2.0, hero.MaxHealth - maxBefore, 0.05), $"a second kill adds another 1 (got {hero.MaxHealth - maxBefore:F2})");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Cannibalism (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sion-cannibalism-splashes-a-quarter-of-the-attack-to-hurt-allies")]
    public static void CannibalismSplashesHealing()
    {
        var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        var ally = match.Ally;
        ally.Position = new Position2(7200.0, 7000.0);
        TestAssert.True(match.Cast(3, hero), "R casts on himself");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "Cannibalism") is not null, "Cannibalism is on");
        var attack = hero.AttackDamage;
        // The shipped script reads the health of the unit he hits (OwnerVar Target), not the ally's: the splash runs only while that unit is hurt.
        foe.Health = foe.MaxHealth * 0.5;
        ally.Health = ally.MaxHealth * 0.5;
        var allyBefore = ally.Health;
        var foeBefore = foe.Health;
        foe.Position = new Position2(hero.Position.X + 100.0, hero.Position.Z);
        Swing(match, foe);
        // The foe regenerates a little over the swing's window (about 1.5), so the hit is read with that margin.
        TestAssert.True(Near(attack, foeBefore - foe.Health, 3.0), $"the swing deals his damage (attack {attack:F2}, lost {foeBefore - foe.Health:F2})");
        // A quarter of the damage of the hit (HealPercent 0.25 at rank one), to an injured ally within 350; regeneration of the window is added.
        var healed = ally.Health - allyBefore;
        TestAssert.True(healed > attack * 0.25 - 1.0 && healed < attack * 0.25 + 2.5, $"the ally gains a quarter of the hit (got {healed:F2} of {attack:F1})");
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
