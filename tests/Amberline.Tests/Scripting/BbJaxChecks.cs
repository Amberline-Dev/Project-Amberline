using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (batch 273, 26 October 2026): Jax plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped inibins (decoded by the batch's standalone reader, the audit entry) and the scripts; the scenario <c>S-Jax</c> shows the same
/// facts on a headless match. Shipped names: the passive is Equipment Mastery, Relentless Assault is his passive (basic attacks) and his ultimate (the
/// active magic-resist buff, rank three); the brief's "Grandmaster's Might" has no shipped name. Jax is not a retail bot.
/// </summary>
public static class BbJaxChecks
{
    // Rank one of each spell (the inibins): Leap Strike 20, Empower 60, Counter Strike 80 magic, Relentless Assault's third hit 140.
    private static Match Arena(int rank, double foeDistance)
    {
        var match = new Match("Jax", foeChampion: "Ezreal");
        match.Learn(rank);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        match.Foe.Position = new Position2(match.Hero.Position.X + foeDistance, match.Hero.Position.Z);
        match.Refill();
        return match;
    }

    private static double Gap(Entity a, Entity b) => Math.Sqrt(Math.Pow(a.Position.X - b.Position.X, 2) + Math.Pow(a.Position.Z - b.Position.Z, 2));

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Jax");
        foreach (var name in new[]
                 {
                     "CharScriptJax", "LeapStrike", "EmpowerTwo", "CounterStrike", "RelentlessAssault", "EquipmentMastery",
                     "ArmsmasterLeapStrikeAttack", "ArmsmasterEmpowerAttack", "ArmsmasterRelentlessAttack", "CounterStrikeDodgeUp",
                     "CounterStrikeCanCast", "LeapStrikeSpeed", "RelentlessAssaultMarker", "ArmsmasterRelentlessMR",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Jax");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(29, closure.ScriptNames.Count(), "the closure is 29 scripts (bb-coverage, batch 273)");

        var hero = new Entity { ChampionName = "Jax" };
        TestAssert.Equal(["LeapStrike", "EmpowerTwo", "CounterStrike", "RelentlessAssault"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Jax.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");

        var info = ChampionCatalog.ByName("Jax")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 24, RetailBot: false },
            "playable as a top laner for 1350 IP, not a retail bot (no JaxBot inibin, no game_bot_Jax)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Jax"), "listed once");
    }

    [Check("scripting.bb-jax-rank-gates-follow-the-stock-table")]
    public static void RankGatesAreTheStockTable()
    {
        var module = new BbChampionModule("Jax");
        var hero = new Entity { ChampionName = "Jax" };
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(),
            "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(),
            "R is learnt at 6, 11, 16 (the stock table)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Leap Strike (Q): the leap, the magic hit, the ally leap
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-leap-strike-hits-for-effect-plus-total-attack-damage-and-ability-power")]
    public static void LeapStrikeHitsForItsEffectPlusTotalAttackDamageAndAbilityPower()
    {
        var match = Arena(1, 600.0);
        match.GiveAbilityPower();
        match.Refill();
        var attackDamage = match.Hero.AttackDamage;
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(0, match.Foe), "Leap Strike is accepted against an enemy 600 units away");
        match.Advance(0.6);
        var first = HitsOn(match, "Foe").First();
        TestAssert.Equal("Spell", first.Kind, "the leap's hit is a spell hit");
        // SpellDamageRatio 0.7 of ability power (the coefficient), the total attack damage (PhysicalDamageRatio 1.0 of the tooltip) and rank one's 20.
        TestAssert.Near(attackDamage + 20.0 + 0.7 * abilityPower, first.Raw, 0.01, "magic damage = total attack damage + 20 + 0.7 ability power");
        TestAssert.Near(first.Raw, first.Dealt, 0.01, "a zero-resistance victim takes the raw amount");
        TestAssert.True(Gap(match.Hero, match.Foe) < 150.0, "Jax lands beside the enemy");
        // ArmsmasterLeapStrikeAttack's TargetExecute ends with BBIf Target CO_IS_TYPE_HERO, BBIf CO_DIFFERENT_TEAM: BBIssueOrder(Owner, AI_ATTACKTO,
        // Target). The swing after a leap onto an enemy champion is the script's own order (the batch's first version blamed the hero AI).
        TestAssert.Equal((uint?)match.Foe.NetId, match.Hero.AttackTarget, "the leap orders Jax to attack the champion he landed on");
        match.Advance(1.0);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => hit.Kind == "Basic"), "and he swings at it");
    }

    [Check("scripting.bb-jax-leap-strike-to-an-ally-moves-without-damage")]
    public static void LeapStrikeToAnAllyMovesJaxAndHurtsNobody()
    {
        var match = Arena(1, 600.0);
        var ally = match.Ally;
        ally.Position = new Position2(match.Hero.Position.X + 500.0, match.Hero.Position.Z);
        var allyHealth = ally.Health;
        TestAssert.True(match.Cast(0, ally), "Leap Strike is accepted on an allied champion");
        match.Advance(1.5);
        TestAssert.Equal(allyHealth, ally.Health, "the ally takes no damage");
        TestAssert.True(HitsOn(match, "Ally").Count == 0, "no hit is recorded on the ally");
        TestAssert.True(Gap(match.Hero, ally) < 150.0, "Jax lands beside the ally");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Empower (W): the next attack only
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-empower-adds-magic-damage-to-the-next-attack-only")]
    public static void EmpowerAddsMagicDamageToTheNextAttackOnly()
    {
        var match = Arena(1, 110.0);
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(1, match.Hero), "Empower is accepted on Jax");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "EmpowerTwo") is not null, "the Empower buff is on Jax");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Jax's next attack is queued");
        match.Advance(1.2);
        var spells = HitsOn(match, "Foe").Where(hit => hit.Kind == "Spell").ToList();
        TestAssert.Equal(1, spells.Count, "the empowered attack adds one magic hit");
        // EmpowerTwo's rank-one effect 60 and 0.4 of ability power (the W's coefficient).
        TestAssert.Near(60.0 + 0.4 * abilityPower, spells[0].Raw, 0.01, "the bonus is 60 + 0.4 ability power, magic");
        TestAssert.True(Buff(match.Hero, "EmpowerTwo") is null, "that attack spends the buff");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Counter Strike (E): the dodge window, the stun, the damage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-counter-strike-waits-for-a-dodge-then-stuns-for-a-second")]
    public static void CounterStrikeNeedsADodgeThenStunsAndHurts()
    {
        var match = Arena(1, 110.0);
        match.Advance(0.2); // the passive dodge (CounterStrikeDodgeUp) is set on a tick: a dodge roll needs it above zero
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.False(match.CanCast(2, match.Hero).Allowed, "Counter Strike is sealed before any dodge");

        // A forced dodge: the foe's attack is queued with a roll that dodges (Jax's dodge is above zero from rank one).
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero, new AttackRolls(1.0, 0.0, 1.0)), "the foe's attack is queued");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Hero, "CounterStrikeCanCast") is not null, "the dodge opens the spell");
        TestAssert.True(HitsOn(match, "Hero").Count == 0, "the dodged attack deals nothing");

        TestAssert.True(match.Cast(2, match.Hero), "Counter Strike is accepted after the dodge");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "Stun") is not null, "the enemy is stunned at once");
        var hit = HitsOn(match, "Foe").First(item => item.Kind == "SpellAoe");
        // Effect 2 at rank one is 80 magic, plus 0.6 of ability power (the coefficient); the stun's second is the boolean-stored effect 3 (reads 1).
        TestAssert.Near(80.0 + 0.6 * abilityPower, hit.Raw, 0.01, "the damage is 80 + 0.6 ability power, magic");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "Stun") is null, "the stun is over after its one second");
    }

    [Check("scripting.bb-jax-counter-strike-window-closes-after-seven-seconds")]
    public static void CounterStrikeWindowClosesAfterSevenSeconds()
    {
        var match = Arena(1, 110.0);
        match.Advance(0.2);
        match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero, new AttackRolls(1.0, 0.0, 1.0));
        // Poll for the window's appearance (the dodge), then read its length from the moment it opens.
        for (var step = 0; step < 300 && Buff(match.Hero, "CounterStrikeCanCast") is null; step++) match.Advance(1.0 / 30.0);
        var window = Buff(match.Hero, "CounterStrikeCanCast");
        TestAssert.True(window is not null, "the dodge opens the spell");
        // CharOnDodge runs at the impact of the dodged swing and adds the window with Duration 7.0 (CharScriptJax, K92). BuffState.ExpiresAt is the
        // time left, not a clock instant: the batch's first version read ExpiresAt - Now, 6.4 s, and took it for a late timer (restated, batch 273).
        TestAssert.Near(7.0, window!.ExpiresAt, 1.0 / 30.0 + 1e-6, $"seven seconds are left when the window opens (read {window.ExpiresAt:0.###})");
        var opened = match.Now;
        for (var step = 0; step < 300 && Buff(match.Hero, "CounterStrikeCanCast") is not null; step++) match.Advance(1.0 / 30.0);
        TestAssert.Near(7.0, match.Now - opened, 0.1, $"the window closes seven seconds after the dodge (open for {match.Now - opened:0.###} s)");
        TestAssert.False(match.CanCast(2, match.Hero).Allowed, "and Counter Strike is sealed again");
    }

    [Check("scripting.bb-jax-counter-strike-dodge-grows-with-its-rank")]
    public static void CounterStrikeDodgeGrowsWithItsRank()
    {
        // Effect 1 is 10, 12, 14, 16, 18 percent dodge (CounterStrikeDodgeUp adds it with the spell's level).
        var expected = new[] { 0.10, 0.12, 0.14, 0.16, 0.18 };
        for (var rank = 1; rank <= 5; rank++)
        {
            var match = Arena(rank, 110.0);
            match.Advance(0.2);
            TestAssert.Near(expected[rank - 1], match.Hero.Dodge, 0.0001, $"rank {rank} of Counter Strike adds {expected[rank - 1] * 100}% dodge");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Relentless Assault (R, and its passive): the stacks, the third hit, the magic-resist buff
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-relentless-assault-hits-the-third-time-for-bonus-magic-damage")]
    public static void RelentlessAssaultHitsTheThirdTimeForItsBonusMagicDamage()
    {
        var match = Arena(1, 110.0);
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(3, match.Hero), "Relentless Assault is accepted at level six");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "RelentlessAssault") is not null, "the ultimate's buff is on Jax");
        for (var swing = 1; swing <= 3; swing++)
        {
            // An attack is refused while the attack timer runs (QueueBasicAttack): retry in steps until the swing is accepted.
            var queued = false;
            for (var step = 0; step < 40 && !queued; step++)
            {
                queued = match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
                if (!queued) match.Advance(0.1);
            }
            TestAssert.True(queued, $"swing {swing} is queued within four seconds");
            match.Advance(1.0);
        }
        var spells = HitsOn(match, "Foe").Where(hit => hit.Kind == "Spell").ToList();
        TestAssert.Equal(1, spells.Count, "the third consecutive hit adds one magic hit: " + string.Join(" | ", HitsOn(match, "Foe").Select(hit => $"{hit.At:0.00} {hit.Kind} {hit.Raw:0.###}")));
        // Effect 2 at rank one is 140 magic, plus 0.7 of ability power (the spell's coefficient, the tooltip's "(+ ability power)").
        TestAssert.Near(140.0 + 0.7 * abilityPower, spells[0].Raw, 0.01, "the bonus is 140 + 0.7 ability power, magic");
        var stacks = Buff(match.Hero, "ArmsmasterRelentlessCounter")?.Stacks ?? 0;
        TestAssert.True(stacks >= 3, $"the attack-speed stacks grow with each hit (saw {stacks})");
    }

    [Check("scripting.bb-jax-relentless-assault-adds-magic-resist-while-active")]
    public static void RelentlessAssaultAddsMagicResistWhileActive()
    {
        var match = Arena(1, 110.0);
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        match.Advance(0.2);
        var before = match.Hero.SpellBlock;
        TestAssert.True(match.Cast(3, match.Hero), "Relentless Assault is accepted at level six");
        match.Advance(0.4);
        var gained = match.Hero.SpellBlock - before;
        // Effect 3 at rank one is 20 magic resist, plus the dodge percent (the tooltip: "plus his Dodge percent").
        TestAssert.Near(20.0 + 100.0 * match.Hero.Dodge, gained, 0.5, $"the resist bonus is 20 plus the dodge percent (gained {gained})");
        TestAssert.True(Buff(match.Hero, "ArmsmasterRelentlessMR") is not null, "the resist buff is on Jax");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Equipment Mastery (passive): health from items
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-equipment-mastery-gives-health-from-ability-power")]
    public static void EquipmentMasteryGivesTwoHealthPerAbilityPower()
    {
        var match = Arena(1, 300.0);
        var before = match.Hero.MaxHealth;
        match.GiveAbilityPower();
        match.Advance(1.0);
        // The passive: 2 health per point of ability power from items (the tooltip; the script's APHealthAdded).
        TestAssert.Near(before + 2.0 * match.Hero.AbilityPower, match.Hero.MaxHealth, 0.5,
            $"the passive adds 2 health per ability power (ability power {match.Hero.AbilityPower})");
        TestAssert.True(Buff(match.Hero, "EquipmentMastery") is not null, "the passive buff is on Jax");
    }
}
