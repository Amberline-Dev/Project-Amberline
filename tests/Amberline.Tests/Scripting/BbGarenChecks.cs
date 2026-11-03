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
/// Phase 3, wave 3 (19 October 2026): Garen plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). Expected
/// numbers are the literals of the shipped scripts and spell inibins (the second rank where a check learns it); the scenarios <c>S-garen</c> and
/// <c>S-garen-reconnect</c> show the same facts, and more, on a headless match. The engine piece Garen needed (the buff manager asks every buff the unit holds
/// whether a new buff may be added, whoever adds it, and the hook may rewrite the duration) is checked at the end.
/// </summary>
public static class BbGarenChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Garen next to the enemy hero (his attack range is 125); every spell learnt at <paramref name="rank"/> (R at most 3).</summary>
    private static Match Melee(int rank = 1, string foe = "Orianna")
    {
        var match = new Match("Garen", foeChampion: foe);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7080, 7000);
        match.Foe.Armor = 40.0;
        match.Foe.SpellBlock = 30.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        // W is ranked through the spellbook, as a player does: that is what adds GarenCommandKill (CharOnLevelUpSpell of slot 1).
        match.Hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 1), "W learnt");
        match.Learn(rank);
        match.Refill();
        return match;
    }

    private static void Swing(Match match, Entity target, double seconds = 1.0)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target), "swing");
        match.Advance(seconds);
    }

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, the missing resource pool
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-garen-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndHasNoPool()
    {
        var module = new BbChampionModule("Garen");
        foreach (var name in new[] { "CharScriptGaren", "GarenSlash3", "GarenSlash2", "GarenFastMove", "GarenCommand", "GarenCommandKill", "GarenKillBuff",
                     "GarenBladestorm", "GarenBladestormLeave", "GarenJustice", "GarenJusticePreCast", "GarenRecouperate", "GarenRecouperateOn",
                     "GarenRecouperate1", "GarenRecoupDebuff" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Garen");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");

        var hero = new Entity { ChampionName = "Garen" };
        TestAssert.Equal(["GarenSlash3", "GarenCommand", "GarenBladestorm", "GarenJustice"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Garen.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "the stock ranks");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 like any ultimate");

        var info = ChampionCatalog.ByName("Garen")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 86, RetailBot: false }, "playable as a top laner for 1350 IP, not a retail bot");
        var stats = GameData.Default.StatsAtLevel(GameData.Default.Champion("Garen"), 1);
        TestAssert.Equal(0.0, stats.MaxMp, "his pool is nothing (PARType Shield, MaxMp 0)");
    }

    [Check("scripting.bb-garen-has-no-resource-pool-and-every-spell-casts-free")]
    public static void AllFourSpellsCastWithNoMana()
    {
        using var match = Melee(2);
        var hero = match.Hero;
        TestAssert.Equal(0.0, hero.MaxMana, "no maximum");
        TestAssert.Equal(0.0, hero.Mana, "and none held");
        foreach (var slot in new[] { 0, 1, 2 })
        {
            match.Refill();
            TestAssert.True(match.Cast(slot, hero), $"slot {slot} casts with no mana");
            match.Advance(0.3);
            TestAssert.Equal(0.0, hero.Mana, "and costs none");
            foreach (var buff in hero.Buffs.Where(buff => buff.Name is "GarenSlash3" or "GarenFastMove" or "GarenCommand" or "GarenBladestorm").ToArray())
                match.Runtime.Buffs.RemoveBuff(hero, buff.Slot);
            match.Advance(0.2);
        }
        match.Refill();
        TestAssert.True(match.Cast(3, match.Foe), "Demacian Justice casts with no mana");
        match.Advance(0.6);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Perseverance
    // ------------------------------------------------------------------------------------------------------------

    private static string Regen(Entity hero) => string.Join("+", hero.Buffs.Where(buff => buff.Name.StartsWith("GarenRecoup", StringComparison.Ordinal) ||
                                                                              buff.Name.StartsWith("GarenRecouperate", StringComparison.Ordinal)).Select(buff => buff.Name));

    [Check("scripting.bb-garen-perseverance-waits-seven-seconds-for-damage-and-crowd-control")]
    public static void RegenerationRestartsOnDamageAndOnEnemyControl()
    {
        using var match = Melee();
        var hero = match.Hero;
        match.Advance(2.5);
        TestAssert.Equal("GarenRecouperate1", Regen(hero), "an undamaged Garen holds the healing buff");
        // Half a percent of the maximum health a second, on top of the stat regeneration.
        hero.Health = hero.MaxHealth * 0.5;
        var rest = hero.Health;
        match.Advance(3.0);
        var gain = hero.Health - rest;
        var stat = hero.HealthRegen * 3.0;
        TestAssert.Near(0.005 * hero.MaxHealth * 3.0, gain - stat, 0.005 * hero.MaxHealth * 1.2, $"half a percent a second more than the stat (gained {gain:0.0}, stat {stat:0.0})");

        // Damage from the enemy: the debuff comes on, the healing buff goes, and Perseverance heals nothing for seven seconds.
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 40.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.2);
        TestAssert.Equal("GarenRecoupDebuff+GarenRecouperateOn", Regen(hero), "debuff and waiting buff after a hit");
        TestAssert.Near(7.0, Of(hero, "GarenRecoupDebuff")!.ExpiresAt, 0.3, "seven seconds");
        hero.Health = hero.MaxHealth * 0.5;
        var before = hero.Health;
        match.Advance(5.0);
        TestAssert.Near(hero.HealthRegen * 5.0, hero.Health - before, 1.0, "only the stat regeneration inside the seven seconds");

        // A slow from an enemy (no damage) renews the seven seconds: the hook asks for every add.
        match.Runtime.Buffs.AddBuff(hero, "ProbeSlow", BuffType.Slow, 1.0, match.Foe.NetId, canMitigateDuration: false);
        match.Advance(0.1);
        TestAssert.Near(7.0, Of(hero, "GarenRecoupDebuff")!.ExpiresAt, 0.3, "restarted by the slow");
        match.Advance(6.0);
        TestAssert.True(Of(hero, "GarenRecoupDebuff") is not null, "still counting after 6 s of the new seven");
        match.Advance(2.5);
        TestAssert.Equal("GarenRecouperate1", Regen(hero), "the healing buff is back once the debuff has run out");
        // A slow from a friend does not count.
        match.Advance(1.5);
        match.Runtime.Buffs.AddBuff(hero, "ProbeFriendSlow", BuffType.Slow, 1.0, match.Ally.NetId, canMitigateDuration: false);
        match.Advance(0.1);
        TestAssert.Equal("GarenRecouperate1", Regen(hero), "friendly control restarts nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Decisive Strike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-garen-decisive-strike-replaces-the-next-attack-and-silences")]
    public static void TheStrikeIsOneHitWithTheSilence()
    {
        using var match = Melee(2);
        var hero = match.Hero;
        var speed = hero.Speed;
        TestAssert.True(match.Cast(0, hero), "Q casts");
        match.Advance(0.5);
        TestAssert.Near(speed * 1.2, hero.Speed, speed * 0.01, "20 percent movement speed at rank 2");
        TestAssert.True(Of(hero, "GarenSlash3") is not null && Of(hero, "GarenFastMove") is not null, "the armed buff and the speed buff");
        TestAssert.Near(4.0, Of(hero, "GarenFastMove")!.ExpiresAt + 0.5, 0.1, "four seconds of speed");
        TestAssert.True(hero.SpellSeals.IsSealed(false, 0), "the slot is sealed while the strike waits");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "and its cooldown has not started");
        TestAssert.False(match.CanCast(0).Allowed, "it cannot be pressed again");

        Swing(match, match.Foe, 1.2);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the one attack is the one strike (no plain swing besides)");
        var expected = 1.4 * hero.AttackDamage + 45.0;
        TestAssert.Near(expected, hits[0].Raw, 0.05, "1.4 x total attack damage + 45 at rank 2");
        TestAssert.Near(Mitigated(expected, match.Foe.Armor), hits[0].Dealt, 0.05, "physical, after the armor");
        var silence = Of(match.Foe, "Silence");
        TestAssert.True(silence is not null, "silenced");
        TestAssert.Near(2.5, silence!.ExpiresAt + (match.Now - hits[0].At), 0.15, "for 2.5 seconds, at every rank (GarenSlash2's own table)");
        TestAssert.True(Of(hero, "GarenSlash3") is null, "the armed buff is spent");
        TestAssert.False(hero.SpellSeals.IsSealed(false, 0), "the slot opens");
        TestAssert.Near(11.0 - (match.Now - hits[0].At), hero.SpellCooldowns[0], 0.4, "and the cooldown (11 s at rank 2) runs from the strike");
        Swing(match, match.Foe, 1.2);
        TestAssert.Near(Mitigated(hero.AttackDamage, match.Foe.Armor), HitsOn(match, "Foe").Last().Dealt, 0.05, "the next attack is an ordinary one");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-garen-decisive-strike-runs-out-after-six-seconds-and-the-cooldown-starts-then")]
    public static void AnUnusedStrikeEndsAtSixSeconds()
    {
        using var match = Melee();
        var hero = match.Hero;
        match.Cast(0, hero);
        match.Advance(5.8);
        TestAssert.True(Of(hero, "GarenSlash3") is not null, "still armed at 5.8 s");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "no cooldown yet");
        TestAssert.True(Of(hero, "GarenFastMove") is null, "the speed ended at 4 s");
        match.Advance(0.4);
        TestAssert.True(Of(hero, "GarenSlash3") is null, "gone after six seconds");
        TestAssert.Near(12.0 - 0.2, hero.SpellCooldowns[0], 0.3, "the 12 s cooldown of rank 1 starts then");
        TestAssert.False(hero.SpellSeals.IsSealed(false, 0), "the slot is open");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Courage
    // ------------------------------------------------------------------------------------------------------------

    private static double Lost(Match match, Entity hero, DamageType type)
    {
        hero.Health = hero.MaxHealth;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 100.0, type, DamageSourceKind.Raw);
        return hero.MaxHealth - hero.Health;
    }

    [Check("scripting.bb-garen-courage-reduces-damage-for-three-seconds")]
    public static void CourageTakesAQuarterOffForThreeSeconds()
    {
        using var match = Melee(2);
        var hero = match.Hero;
        var physical = Lost(match, hero, DamageType.Physical);
        var magic = Lost(match, hero, DamageType.Magic);
        TestAssert.True(match.Cast(1, hero), "W casts");
        match.Advance(0.3);
        TestAssert.Near(3.0, Of(hero, "GarenCommand")!.ExpiresAt + 0.3, 0.1, "three seconds");
        TestAssert.Near(physical * 0.76, Lost(match, hero, DamageType.Physical), 0.05, "24 percent less physical damage at rank 2");
        TestAssert.Near(magic * 0.76, Lost(match, hero, DamageType.Magic), 0.05, "and magic");
        TestAssert.Near(100.0, Lost(match, hero, DamageType.True), 0.05, "true damage is left alone");
        TestAssert.Near(27.0 - 0.3, hero.SpellCooldowns[1], 0.4, "the 27 s cooldown of rank 2");
        match.Advance(2.9);
        TestAssert.True(Of(hero, "GarenCommand") is null, "over after three seconds");
        TestAssert.Near(physical, Lost(match, hero, DamageType.Physical), 0.05, "the plain figure again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-garen-courage-passive-adds-half-armor-and-resist-per-kill-up-to-25")]
    public static void EveryKillAddsHalfAPoint()
    {
        using var match = Melee();
        var hero = match.Hero;
        TestAssert.True(Of(hero, "GarenCommandKill") is not null, "the passive buff comes with the first rank of W");
        var armor = hero.Armor;
        var resist = hero.SpellBlock;
        for (var kill = 1; kill <= 3; kill++)
        {
            var minion = Enemy(match, "Victim" + kill, new Position2(7200, 7000));
            match.Runtime.Combat.Kill(minion, hero.NetId);
            match.Advance(0.2);
            TestAssert.Near(armor + 0.5 * kill, hero.Armor, 1e-6, $"kill {kill}: half a point of armor");
            TestAssert.Near(resist + 0.5 * kill, hero.SpellBlock, 1e-6, $"kill {kill}: half a point of magic resist");
        }
        var other = Enemy(match, "Victim4", new Position2(7200, 7000));
        match.Runtime.Combat.Kill(other, match.Foe.NetId);
        match.Advance(0.2);
        TestAssert.Near(armor + 1.5, hero.Armor, 1e-6, "a kill by someone else counts for nothing");
        match.Runtime.Combat.Kill(match.Foe, hero.NetId);
        match.Advance(0.2);
        TestAssert.Near(armor + 2.0, hero.Armor, 1e-6, "an enemy hero counts like any unit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Judgment
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-garen-judgment-spins-three-seconds-and-leaves-on-the-second-press")]
    public static void TheSpinHitsEveryHalfSecondAndLocksTheAttack()
    {
        using var match = Melee(2);
        var hero = match.Hero;
        var far = Enemy(match, "Far", new Position2(7450, 7000));
        var minion = Enemy(match, "Minion", new Position2(6880, 7000));
        match.Runtime.Buffs.AddBuff(hero, "ProbeSlow", BuffType.Slow, 5.0, match.Foe.NetId, canMitigateDuration: false);
        match.Advance(0.1);
        TestAssert.True(match.Cast(2, hero), "E casts");
        match.Advance(0.3);
        TestAssert.Equal("GarenBladestormLeave", hero.ActiveSpellNames[2], "the slot is the leave spell");
        TestAssert.True(Of(hero, "ProbeSlow") is null, "a slow he had is removed at the cast");
        var spin = Of(hero, "GarenBladestorm")!;
        TestAssert.True(spin.Payload.Ghosted && spin.Payload.Disarmed, "ghosted, and no attacks");
        TestAssert.Equal("Spell3", hero.ScriptAnimationOverrides.GetValueOrDefault("run"), "the run is the spin");
        match.Advance(2.8);
        var hero1 = HitsOn(match, "Foe");
        TestAssert.Equal(6, hero1.Count, "a tick at the cast and every half second for three seconds");
        foreach (var hit in hero1) NearF32(45.0, hit.Raw, "45 at rank 2 (+0.7 of the bonus attack damage, none here)");
        TestAssert.Equal(0, HitsOn(match, "Far").Count, "nothing 450 away (the range is 325)");
        var minions = HitsOn(match, "Minion");
        TestAssert.Equal(6, minions.Count, "the minion is hit as often");
        foreach (var hit in minions) NearF32(22.5, hit.Raw, "for half of it");
        TestAssert.Equal("GarenBladestorm", hero.ActiveSpellNames[2], "the slot is E again after the spin");
        TestAssert.False(spin.Payload.Ghosted && Of(hero, "GarenBladestorm") is not null, "the buff is over");
        TestAssert.Near(12.0 - 0.3, hero.SpellCooldowns[2], 0.5, "12 s of cooldown run from the end");

        // The second press ends it at once.
        match.Refill();
        match.Cast(2, hero);
        match.Advance(0.6);
        TestAssert.False(match.Cast(2, hero), "the leave spell holds a one second cooldown from the cast (BBSetSlotSpellCooldownTimeVer2)");
        match.Advance(0.5);
        var ticks = HitsOn(match, "Foe").Count;
        TestAssert.True(match.Cast(2, hero), "the second press is accepted (the leave spell)");
        match.Advance(0.2);
        TestAssert.True(Of(hero, "GarenBladestorm") is null, "the spin is over");
        TestAssert.Equal("GarenBladestorm", hero.ActiveSpellNames[2], "the slot is E again");
        match.Advance(1.5);
        TestAssert.Equal(ticks, HitsOn(match, "Foe").Count, "no more ticks");
        TestAssert.Near(12.0 - 1.7, hero.SpellCooldowns[2], 0.8, "and the cooldown runs");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-garen-judgment-tick-reads-bonus-attack-damage-and-its-own-critical-rule")]
    public static void TheTickScalesWithBonusAttackDamage()
    {
        foreach (var critical in new[] { false, true })
        {
            using var match = Melee(2);
            var hero = match.Hero;
            var payload = new BuffPayload { AttackDamage = 40.0, CritChance = critical ? 1.0 : 0.0 };
            match.Runtime.Buffs.AddBuff(hero, "ProbeBonus", BuffType.CombatEnhancer, 20.0, hero.NetId, payload, canMitigateDuration: false);
            match.Advance(1.3);
            var minion = Enemy(match, "Minion", new Position2(6880, 7000));
            match.Cast(2, hero);
            match.Advance(1.2);
            var bonus = 40.0;
            var expected = 45.0 + 0.7 * bonus * (critical ? 2.0 : 1.0);
            var foe = HitsOn(match, "Foe");
            TestAssert.True(foe.Count >= 2, "ticks");
            foreach (var hit in foe) NearF32(expected, hit.Raw, critical ? "a critical tick doubles the bonus part" : "45 + 0.7 x the bonus attack damage");
            foreach (var hit in HitsOn(match, "Minion")) NearF32(expected / 2.0, hit.Raw, "half of it to a minion");
            TestAssert.Near(1.4 * bonus, hero.ScriptSpellTooltips[2][0], 1e-6, "the tooltip of E shows 1.4 x the bonus attack damage");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Demacian Justice
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-garen-demacian-justice-finishes-by-missing-health")]
    public static void JusticeAddsAShareOfTheMissingHealth()
    {
        using var match = Melee(2);
        var hero = match.Hero;
        match.Foe.MaxHealth = 5000.0;
        match.Foe.Health = 2000.0;
        TestAssert.True(match.Cast(3, match.Foe), "R casts on an enemy hero");
        match.Advance(0.1);
        TestAssert.True(Of(hero, "GarenJusticePreCast") is not null, "the cast sword buff is up (the name compare ignores case)");
        match.Advance(0.6);
        var hit = HitsOn(match, "Foe").Single();
        TestAssert.Near(350.0 + 3000.0 / 3.0, hit.Raw, 0.5, "350 + the missing health divided by 3 at rank 2 (the target regenerates a little before the hit)");
        TestAssert.Near(Mitigated(hit.Raw, match.Foe.SpellBlock), hit.Dealt, 0.05, "magic damage");
        TestAssert.Near(120.0 - 0.7, hero.SpellCooldowns[3], 0.5, "120 s of cooldown at rank 2");
        // A minion is no target (the spell is flagged AffectHeroes).
        var minion = Enemy(match, "Minion", new Position2(7100, 7000));
        match.Hero.SpellCooldowns[3] = 0.0;
        TestAssert.False(match.CanCast(3, minion).Allowed, "R refuses a minion");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The engine piece: BuffOnAllowAdd for every add
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-buff-on-allow-add-reaches-every-adder-and-rewrites-the-duration")]
    public static void TheBuffManagerAsksTheTargetsScriptBuffsOnceForEveryAdd()
    {
        // 1. An add that runs no script (an item, a summoner spell, a minion, a turret: AddBuff) is asked about, and the hook's duration is used.
        using (var match = Melee(2))
        {
            var hero = match.Hero;
            match.Cast(2, hero);
            match.Advance(0.3);
            match.Runtime.Buffs.AddBuff(hero, "ProbeSlow", BuffType.Slow, 2.0, match.Foe.NetId, canMitigateDuration: false);
            TestAssert.Near(1.0, Of(hero, "ProbeSlow")!.ExpiresAt, 0.01, "a slow of an enemy lasts half as long while Judgment spins");
            match.Runtime.Buffs.AddBuff(hero, "ProbeStun", BuffType.Stun, 2.0, match.Foe.NetId, canMitigateDuration: false);
            TestAssert.Near(2.0, Of(hero, "ProbeStun")!.ExpiresAt, 0.01, "other control is left alone");
            match.Runtime.Buffs.AddBuff(hero, "ProbeFriend", BuffType.Slow, 2.0, match.Ally.NetId, canMitigateDuration: false);
            TestAssert.Near(2.0, Of(hero, "ProbeFriend")!.ExpiresAt, 0.01, "and so is a slow of a friend");
        }

        // 2. An add another champion's script makes is asked about once: Janna's Zephyr slows Garen for the same time with and without the spin, halved by it
        //    (a second ask would quarter it).
        double SlowOf(bool spinning)
        {
            using var match = Melee(2, foe: "Janna");
            var hero = match.Hero;
            match.Foe.SpellRanks[1] = 1;
            match.Foe.SpellCooldowns[1] = 0.0;
            if (spinning)
            {
                match.Cast(2, hero);
                match.Advance(0.3);
            }
            TestAssert.True(match.Cast(1, hero, match.Foe), "Janna's W casts at Garen");
            match.Advance(0.5);
            var slow = hero.Buffs.FirstOrDefault(buff => buff.Type == BuffType.Slow && buff.Name != "ProbeSlow");
            TestAssert.True(slow is not null, "Zephyr's slow is on Garen");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
            return slow!.ExpiresAt;
        }
        var plain = SlowOf(false);
        var halved = SlowOf(true);
        TestAssert.True(plain > 1.0, $"Zephyr's slow lasts a while ({plain:0.00})");
        TestAssert.Near(plain / 2.0, halved, 0.15, $"half as long while Judgment spins ({halved:0.00} against {plain:0.00}), asked once");
    }

    [Check("scripting.bb-buff-on-allow-add-asks-a-stacking-add-once")]
    public static void AStackingAddIsAskedOnce()
    {
        // AddStackingBuff asks the script buffs for its first stack itself and must not ask again inside AddBuff: a second ask would halve
        // the slow again (Judgment) and leave a quarter of its duration.
        using var match = Melee(2);
        var hero = match.Hero;
        match.Cast(2, hero);
        match.Advance(0.3);
        var stacked = match.Runtime.Buffs.AddStackingBuff(hero, "ProbeStackSlow", BuffType.Slow, 2.0, match.Foe.NetId, 2, new BuffPayload());
        TestAssert.True(stacked is not null, "the stacking slow is added");
        TestAssert.Near(1.0, stacked!.ExpiresAt, 0.01, "a stacking slow of an enemy lasts half as long while Judgment spins, asked once");
    }
}
