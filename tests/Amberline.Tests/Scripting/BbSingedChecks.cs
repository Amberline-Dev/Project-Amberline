using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 2 (batch 289, 26 October 2026): Singed plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped scripts and spell inibins (rank one unless stated): Empowered Bulwark adds a quarter of his maximum mana to his health pool;
/// Poison Trail costs 13 mana and drains 13 a second while it is on, and its cloud poisons the units beside it one tick a second (22 per tick at rank one,
/// plus 0.3 of ability power); Mega Adhesive's pool lasts 5.5 seconds and slows the units in 265 by 35 percent (75 at rank five), half a second more after
/// they leave; Fling throws the target 420 along its path at 100 magic damage and stuns it for the flight; Insanity Potion adds 35 armour, magic resist,
/// movement speed and ability power for 25 seconds. The scenario <c>S-Singed</c> shows
/// the same facts on a headless match.
/// </summary>
public static class BbSingedChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Singed at the given level with every spell learnt at rank one, the enemy 100 in front of him, the second enemy far off.</summary>
    private static Match Arena(int level = 1, string foe = "Ezreal")
    {
        var match = new Match("Singed", foeChampion: foe);
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.FarFoe.Position = new Position2(7500, 7300);
        match.Ally.Position = new Position2(6500, 6500);
        foreach (var unit in new[] { match.Foe, match.FarFoe, match.Ally }) unit.SpellBlock = 0.0;
        match.Hero.Level = level;
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Refill();
        return match;
    }

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>The 220 soft floor of movement speed: a slow that would go under it loses half of the difference (the rule the Zilean record measured).</summary>
    private static double SoftFloor(double speed) => speed < 220.0 ? 220.0 - (220.0 - speed) / 2.0 : speed;

    /// <summary>Damage dealt to a named unit in a window, every source (the arena has no other damage).</summary>
    private static double Dealt(Match match, string victim, double from, double to) =>
        HitsOn(match, victim).Where(hit => hit.At >= from && hit.At <= to).Sum(hit => hit.Dealt);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Singed");
        foreach (var name in new[] { "CharScriptSinged", "PoisonTrail", "PoisonTrailApplicator", "PoisonTrailMarker", "PoisonTrailTarget",
                     "MegaAdhesive", "MegaAdhesiveApplicator", "MegaAdhesiveTarget", "Fling", "InsanityPotion", "EmpoweredBulwark", "ExpirationTimer" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Singed");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(21, closure.ScriptNames.Count(), "the closure is 21 scripts (audit bb-coverage)");

        var hero = new Entity { ChampionName = "Singed" };
        TestAssert.Equal(["PoisonTrail", "MegaAdhesive", "Fling", "InsanityPotion"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Singed.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Singed")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 27, RetailBot: false },
            "playable as a top laner for 1350 IP, not a retail bot (the generic template)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Singed"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Empowered Bulwark (passive)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-empowered-bulwark-adds-a-quarter-of-max-mana-to-health")]
    public static void EmpoweredBulwarkAddsAQuarterOfMaxManaToHealth()
    {
        using var match = Arena();
        TestAssert.True(Buff(match.Hero, "EmpoweredBulwark") is not null, "he carries the passive (his character script adds it on activation)");
        // The base 448 at level one (his stats) plus 0.25 of his maximum mana (260): the script's BBGetPAROrHealth GetMaxPAR and HealthMod.
        TestAssert.Near(448.0 + 0.25 * match.Hero.MaxMana, match.Hero.MaxHealth, 0.5, "health is 448 plus a quarter of the 260 mana");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(Buff(match.Hero, "EmpoweredBulwark") is not null, "the passive persists through death (PersistsThroughDeath)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Poison Trail (Q, a toggle)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-poison-trail-costs-thirteen-mana-and-drains-thirteen-a-second")]
    public static void PoisonTrailDrainsThirteenManaASecond()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "Poison Trail is switched on");
        TestAssert.Near(13.0, mana - match.Hero.Mana, 0.01, "the switch-on costs 13 mana at rank one");
        TestAssert.True(Buff(match.Hero, "PoisonTrail") is not null, "he carries the toggle buff");
        var before = match.Hero.Mana;
        match.Advance(3.2);
        // Three drains (one a second) less the regeneration of the 3.2 seconds.
        TestAssert.Near(39.0 - 3.2 * match.Hero.ManaRegen, before - match.Hero.Mana, 0.8, "13 mana a second while it is on");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-poison-trail-switches-off-with-a-one-second-cooldown")]
    public static void PoisonTrailSwitchesOffWithOneSecondCooldown()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "Poison Trail is switched on");
        match.Advance(0.5);
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "the second cast switches it off");
        TestAssert.True(Buff(match.Hero, "PoisonTrail") is null, "the toggle buff is gone");
        // The off branch of SelfExecute sets the spell's cooldown to one second (SpellSlotValue 0, SrcValue 1).
        TestAssert.Near(1.0, match.Hero.SpellCooldowns[0], 0.05, "and the toggle comes back after one second");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Batch 331: the shared post-cast adjustment (ApplyCostsOfSpell,
    // called by the cast-and-serve routine) spends the cast's PAR only when the slot's toggle state is not eToggleOn (2), which
    // the spellbook's buff-activate hook sets for an IsToggleSpell slot that a live buff names in SpellToggleSlot. Poison Trail keeps its
    // targeting, so the old rule (a targeting switch) charged its switch-off 13 mana and refused it below 13.
    [Check("scripting.bb-singed-poison-trail-switch-off-is-free-by-the-toggle-state")]
    public static void PoisonTrailSwitchOffIsFree()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "Poison Trail is switched on");
        match.Advance(0.5);
        match.Hero.Mana = 5.0;
        var verdict = match.CanCast(0);
        TestAssert.True(verdict.Allowed, $"the switch-off is not refused for mana with 5 left ({verdict.Reason})");
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "the second cast switches it off");
        TestAssert.True(Buff(match.Hero, "PoisonTrail") is null, "the toggle buff is gone");
        TestAssert.True(match.Hero.Mana >= 5.0 - 1e-6, $"the switch-off spends no mana ({match.Hero.Mana:0.##} left)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-poison-trail-poisons-the-unit-beside-it-one-tick-a-second")]
    public static void PoisonedUnitTakesOneRankTickEverySecond()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(6960, 7000);
        TestAssert.True(match.Cast(0, at: match.Hero.Position), "Poison Trail is switched on beside the enemy");
        // The first cloud comes from the toggle's one-second execution (ExecuteImmediately is false there), so the poison starts near one second.
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "PoisonTrailTarget") is not null, "the enemy beside the trail is poisoned");
        var from = match.Now;
        match.Advance(2.5);
        var hits = HitsOn(match, "Foe").Where(hit => hit.At >= from && hit.Dealt > 0.0).ToList();
        // 22 magic damage a tick at rank one (the arena's resistance is zero, ability power is zero): one tick a second, the first at once.
        TestAssert.True(hits.Count is >= 2 and <= 4, $"one tick a second over 2.5 seconds ({hits.Count})");
        TestAssert.True(hits.All(hit => Math.Abs(hit.Dealt - 22.0) < 0.05), "each tick is 22 (rank one, no ability power)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Mega Adhesive (W, a pool)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-mega-adhesive-slows-the-units-in-its-pool-by-thirty-five-percent")]
    public static void MegaAdhesiveSlowsTheUnitsInItsPool()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7200, 7000);
        var speed = match.Foe.Speed;
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "Mega Adhesive is cast at his feet");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "MegaAdhesiveTarget") is not null, "the enemy 200 from the centre carries the slow");
        TestAssert.Near(SoftFloor(speed * 0.65), match.Foe.Speed, 0.5, "35 percent off his speed, through the 220 soft floor");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-mega-adhesive-pool-lasts-five-and-a-half-seconds-and-the-slow-half-a-second-more")]
    public static void MegaAdhesivePoolEndsAboutSixSecondsAfterTheCast()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7200, 7000);
        var speed = match.Foe.Speed;
        var cast = match.Now;
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "Mega Adhesive is cast at his feet");
        match.Advance(5.4);
        TestAssert.True(Buff(match.Foe, "MegaAdhesiveTarget") is not null, "the enemy is still slowed at 5.4 seconds");
        // The pool's 5.5 seconds start with the applicator (a quarter second after the cast resolves); the slow leaves half a second after that.
        var ended = -1.0;
        for (var step = 0; step < 120 && ended < 0.0; step++)
        {
            match.Advance(0.05);
            if (Buff(match.Foe, "MegaAdhesiveTarget") is null) ended = match.Now - cast;
        }
        TestAssert.True(ended is >= 5.9 and <= 6.6, $"the slow ends about six seconds after the cast ({Math.Round(ended, 2)})");
        TestAssert.Near(speed, match.Foe.Speed, 0.5, "and his speed returns");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-mega-adhesive-slows-by-seventy-five-percent-at-rank-five")]
    public static void MegaAdhesiveSlowGrowsWithItsRank()
    {
        using var match = Arena();
        match.Learn(5);
        match.Refill();
        match.Foe.Position = new Position2(7200, 7000);
        var speed = match.Foe.Speed;
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "Mega Adhesive at rank five is cast at his feet");
        match.Advance(1.0);
        TestAssert.Near(SoftFloor(speed * 0.25), match.Foe.Speed, 0.5, "75 percent off his speed, through the 220 soft floor");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Fling (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-fling-throws-the-target-behind-him-and-stuns-it-for-the-flight")]
    public static void FlingThrowsTheTargetBehindHimAndStunsItForTheFlight()
    {
        using var match = Arena();
        var start = match.Foe.Position;
        var cast = match.Now;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Foe), "Fling is cast on the enemy 100 in front of him");
        // The cast takes a quarter second (the inibin's cast time); the throw resolves then.
        match.Advance(0.35);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(100.0, mana - match.Hero.Mana, 0.5, "it costs 100 mana at rank one");
        TestAssert.Near(10.0, match.Hero.SpellCooldowns[2] + 0.35 - match.CastFrameOf(2), 0.05, "and its cooldown is ten seconds");
        TestAssert.True(Buff(match.Foe, "Fling") is not null, "the enemy is stunned for the flight");
        TestAssert.Near(100.0, Dealt(match, "Foe", cast, match.Now), 0.01, "100 magic damage at rank one (no ability power, no resistance)");
        match.Advance(1.5);
        // Restated in batch 659: the target is thrown to LandPos, 420 behind him; the script's IdealDistance of 420 sizes the throw's speed and
        // caps nothing (BbNativeHandlers.Move), so the flight is 520 here (the target starts 100 in front of him) and lasts 420 / 1000 s.
        TestAssert.Near(420.0, Dist(match.Foe.Position, match.Hero.Position), 6.0, "it lands 420 behind him");
        TestAssert.True(match.Foe.Position.X < match.Hero.Position.X, "behind him, on the far side");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Insanity Potion (R, a self buff)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-singed-insanity-potion-gives-thirty-five-stats-for-twenty-five-seconds")]
    public static void InsanityPotionGivesThirtyFiveStatsForTwentyFiveSeconds()
    {
        using var match = Arena(level: 6);
        var armour = match.Hero.Armor;
        var magicResist = match.Hero.SpellBlock;
        var speed = match.Hero.Speed;
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "Insanity Potion is cast on himself");
        match.Advance(0.2);
        TestAssert.Near(armour + 35.0, match.Hero.Armor, 0.05, "armour plus 35 at rank one");
        TestAssert.Near(magicResist + 35.0, match.Hero.SpellBlock, 0.05, "magic resist plus 35");
        TestAssert.Near(speed + 35.0, match.Hero.Speed, 0.05, "movement speed plus 35 (a flat bonus)");
        TestAssert.Near(abilityPower + 35.0, match.Hero.AbilityPower, 0.05, "ability power plus 35");
        match.Advance(24.4);
        TestAssert.True(Buff(match.Hero, "InsanityPotion") is not null, "the potion lasts 25 seconds");
        match.Advance(0.8);
        TestAssert.True(Buff(match.Hero, "InsanityPotion") is null, "and then ends");
        TestAssert.Near(armour, match.Hero.Armor, 0.05, "and the armour comes back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-insanity-potion-regenerates-seven-health-and-mana-a-second")]
    public static void InsanityPotionRegeneratesSevenASecond()
    {
        using var match = Arena(level: 6);
        var healthRegen = match.Hero.HealthRegen;
        var manaRegen = match.Hero.ManaRegen;
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "Insanity Potion is cast on himself");
        match.Advance(0.2);
        // The script's StatsPer5 (35 / 5) feeds IncFlatHPRegenMod and BBIncFlatPARRegenMod.
        TestAssert.Near(healthRegen + 7.0, match.Hero.HealthRegen, 0.05, "health regeneration plus 7 a second");
        TestAssert.Near(manaRegen + 7.0, match.Hero.ManaRegen, 0.05, "mana regeneration plus 7 a second");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Crowd-control reduction on him (batch 296): every held buff's BuffOnAllowAdd multiplies the incoming Duration in turn
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Singed at level six (the potion learnt) with a second Singed as the enemy 100 in front of him, Fling learnt.</summary>
    private static Match Duel()
    {
        var match = Arena(level: 6, foe: "Singed");
        match.Foe.SpellRanks[2] = 1;
        match.Foe.Mana = match.Foe.MaxMana;
        return match;
    }

    /// <summary>
    /// Ticks one millisecond at a time until <paramref name="buff"/> is on <paramref name="holder"/>: the time left it landed with, and the
    /// Duration its add asked for (the trace's <c>dur=</c>, before any BuffOnAllowAdd).
    /// </summary>
    private static (double Left, double Asked) Landed(Match match, Entity holder, string holderName, string buff)
    {
        for (var tick = 0; tick < 2000 && Buff(holder, buff) is null; tick++) match.Runtime.Tick(0.001);
        var state = Buff(holder, buff);
        TestAssert.True(state is not null, $"{buff} lands on {holderName}");
        var line = match.Log.Buffs.Last(entry => entry.Contains($" add {holderName} {buff} ", StringComparison.Ordinal));
        var asked = double.Parse(line.Split(' ').First(part => part.StartsWith("dur=", StringComparison.Ordinal))[4..],
            System.Globalization.CultureInfo.InvariantCulture);
        return (state!.ExpiresAt, asked);
    }

    [Check("scripting.bb-singed-insanity-potion-shortens-an-enemy-fling-by-ten-percent")]
    public static void InsanityPotionShortensAnEnemyFlingByTenPercent()
    {
        using var match = Duel();
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "Insanity Potion is cast on himself");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "InsanityPotion") is not null, "the potion is on him");
        TestAssert.True(match.Cast(2, match.Hero, caster: match.Foe), "the enemy Singed flings him");
        var (left, asked) = Landed(match, match.Hero, "Singed", "Fling");
        // InsanityPotion.luaobj BuffOnAllowAdd: an enemy's Snare, Slow, Fear, Net, Sleep, Stun or Taunt is multiplied by CCreduction (0.9 at rank one).
        // The flight's stun (BUFF_Stun, distance to LandPos over 1160) is one: it lasts 0.9 of what Fling asked, not the flight's length.
        TestAssert.Near(0.9 * asked, left, 0.003, $"the stun lasts 0.9 x {R(asked)} (it lasted {R(left)})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-singed-mercurys-treads-shorten-an-enemy-stun-by-thirty-five-percent")]
    public static void MercurysTreadsShortenAnEnemyStunByThirtyFivePercent()
    {
        using var match = Duel();
        match.Hero.Inventory = [new InventorySlot(3111), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        TestAssert.Near(0.35, match.Hero.Hardness, 1e-6, "Mercury's Treads: PercentHardnessMod 0.35 (Items/3111.inibin)");
        // Items/3111.luaobj: OnActivate (the buy) adds MercuryTreads for 10 s; UpdateSelfBuffActions adds Hardening (2 s) every second from one
        // second on. Hardening.luaobj BuffOnAllowAdd multiplies an enemy's Snare, Slow, Fear, Net, Sleep, Stun, Taunt, Silence or Blind by
        // 1 - GetPercentHardnessMod; MercuryTreads.luaobj multiplies the first seven by 0.65. Both run as shipped.
        match.Advance(1.5);
        TestAssert.True(Buff(match.Hero, "Hardening") is not null && Buff(match.Hero, "MercuryTreads") is not null, "both buffs are on him");
        var both = match.Runtime.Spells.Buffs.Add(match.Hero, "Stun", BuffType.Stun, 1.0, match.Foe.NetId)!;
        TestAssert.Near(0.65 * 0.65, both.ExpiresAt, 1e-6, "inside the buy's ten seconds both scripts cut a stun: 0.65 x 0.65");
        match.Runtime.Spells.Buffs.Remove(match.Hero, both.Slot);
        match.Advance(9.5);
        TestAssert.True(Buff(match.Hero, "MercuryTreads") is null && Buff(match.Hero, "Hardening") is not null, "then Hardening alone");
        // The native control (BBApplyStun adds the global Stun with its duration) and a script's own stun (Fling's) are cut once, by 0.35.
        var stun = match.Runtime.Spells.Buffs.Add(match.Hero, "Stun", BuffType.Stun, 1.0, match.Foe.NetId)!;
        TestAssert.Near(0.65, stun.ExpiresAt, 1e-6, "a one-second stun lasts 0.65 s (one tenacity copy, not 0.65 x 0.65)");
        match.Runtime.Spells.Buffs.Remove(match.Hero, stun.Slot);
        TestAssert.True(match.Cast(2, match.Hero, caster: match.Foe), "the enemy Singed flings him");
        var (left, asked) = Landed(match, match.Hero, "Singed", "Fling");
        TestAssert.Near(0.65 * asked, left, 0.003, $"Fling's stun lasts 0.65 x {R(asked)} (it lasted {R(left)})");
        // Several reductions multiply: the potion's 0.9 and the treads' 0.65 make a stun 0.585 of its length.
        match.Advance(1.0);
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "Insanity Potion is cast on himself");
        match.Advance(0.4);
        var stacked = match.Runtime.Spells.Buffs.Add(match.Hero, "Stun", BuffType.Stun, 1.0, match.Foe.NetId)!;
        TestAssert.Near(0.9 * 0.65, stacked.ExpiresAt, 1e-6, "potion and treads together: 0.9 x 0.65");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
