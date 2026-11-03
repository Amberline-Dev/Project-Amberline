using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (25 October 2026, batch 257): Dr. Mundo plays from his shipped scripts. The numbers are the constants of the shipped scripts
/// (CharScriptDrMundo, InfectedCleaverMissileCast, InfectedCleaverMissile, BurningAgony, Masochism, Sadism, SadismHeal, Nevershade) and the
/// audit fixture's standalone decode of the inibins. His costs are health (every spell ships ManaCost1..5 as False; BaseMP -100000 is an empty
/// pool), so the checks read health and not mana. The harness starts him at 5000 maximum health, but the first stat refresh (Learn, or any buff
/// added to him) sets his maximum to the level-one value (522), so the checks keep their health at or below that maximum: a heal above the
/// maximum does nothing. Each probe runs at rank one unless it says otherwise. A probe that measures a cost or a refund takes the passive's
/// one-second heal off its window (<see cref="DropPassive"/>); the passive has its own check. Durations are sampled every step, because the
/// buff's own expiry time is not on the same clock as the match's. The scenario <c>S-drmundo</c> shows the same facts on a headless ten-seat
/// match. Written 25 October 2026.
/// </summary>
public static class BbDrMundoChecks
{
    // The shipped rank tables (the scripts' constants, the audit fixture's effects), rank one first.
    private static readonly double[] CleaverCost = [50.0, 60.0, 70.0, 80.0, 90.0];
    private static readonly double[] CleaverMinimum = [80.0, 130.0, 180.0, 230.0, 280.0];
    private static readonly double[] CleaverCap = [300.0, 400.0, 500.0, 600.0, 700.0];
    private static readonly double[] CleaverShare = [0.15, 0.18, 0.21, 0.23, 0.25];
    private static readonly double[] AgonyDrain = [20.0, 25.0, 30.0, 35.0, 40.0];
    private static readonly double[] AgonyDamage = [40.0, 55.0, 70.0, 85.0, 100.0];
    private static readonly double[] MasochismCost = [25.0, 35.0, 45.0, 55.0, 65.0];
    private static readonly double[] MasochismShare = [0.4, 0.55, 0.7, 0.85, 1.0];
    private static readonly double[] MasochismBase = [40.0, 55.0, 70.0, 85.0, 100.0];
    private static readonly double[] SadismHaste = [0.15, 0.25, 0.35];

    private static Match Setup() => new("DrMundo");

    // The passive ticks every second; a window that measures a cost or a refund must not contain one.
    private static void DropPassive(Match match)
    {
        var passive = match.Hero.Buffs.FirstOrDefault(buff => buff.Name == "Nevershade");
        if (passive is not null) match.Runtime.Buffs.RemoveBuff(match.Hero, passive.Slot);
    }

    // Rank one at a settled maximum: the stats are refreshed once, the passive's first tick is past, and the hero sits at a known health.
    private static Match Ready(int rank, double health)
    {
        var match = Setup();
        match.Learn(rank);
        match.Advance(1.1);
        DropPassive(match);
        match.Hero.Health = health;
        return match;
    }

    // Seconds a condition holds, sampled every step from the first step it holds (a buff's own expiry time is on another clock).
    private static double Span(Match match, Func<bool> holds, double timeout)
    {
        var start = -1.0;
        for (var t = 0.0; t < timeout; t += 1.0 / 30.0)
        {
            if (start < 0 && holds()) start = match.Now;
            else if (start >= 0 && !holds()) return match.Now - start;
            match.Advance(1.0 / 30.0);
        }
        return start < 0 ? 0.0 : double.PositiveInfinity;
    }

    [Check("scripting.bb-drmundo-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("DrMundo");
        foreach (var name in new[]
                 {
                     "CharScriptDrMundo", "InfectedCleaverMissileCast", "InfectedCleaverMissile", "BurningAgony", "Masochism", "Sadism",
                     "SadismHeal", "Nevershade",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("DrMundo");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(17, closure.ScriptNames.Count(), "the closure is 17 scripts (audit bb-coverage)");

        var hero = new Entity { ChampionName = "DrMundo" };
        TestAssert.Equal(["InfectedCleaverMissileCast", "BurningAgony", "Masochism", "Sadism"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from DrMundo.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(),
            "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("DrMundo")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, RetailBot: false },
            "playable as a top laner for 1350 IP, not a retail bot (the generic bot template)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "DrMundo"), "listed once");
    }

    [Check("scripting.bb-drmundo-pool-is-empty-and-the-spells-cost-no-mana")]
    public static void EmptyPoolAndNoManaCost()
    {
        using var match = Setup();
        TestAssert.Equal(0.0, match.Hero.MaxMana, "a negative BaseMP is an empty pool (the mana init clamps the maximum to zero)");
        TestAssert.Equal(0.0, match.Hero.Mana, "mana stays at zero");
        match.Learn(1);
        TestAssert.True(match.CanCast(0).Allowed, "the cleaver is castable at full health with an empty pool (its cost is health)");
        TestAssert.True(match.CanCast(2).Allowed, "Masochism too: its cost is health, and full health covers it");
    }

    /// <summary>The child cast pays 50 health (CharOnSpellCast on InfectedCleaverMissile); a hit refunds half of it (25).</summary>
    [Check("scripting.bb-drmundo-infected-cleaver-costs-health-on-the-child-cast-and-refunds-half-on-a-hit")]
    public static void CleaverCostAndRefund()
    {
        using var match = Ready(1, 400.0);
        var max = match.Hero.MaxHealth;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "the cleaver casts at rank one");
        match.Advance(0.3);
        TestAssert.Near(400.0 - CleaverCost[0], match.Hero.Health, 3.0, "the child cast pays 50 health");
        match.Advance(1.0);
        TestAssert.Near(400.0 - CleaverCost[0] + CleaverCost[0] / 2.0, match.Hero.Health, 3.0, "a hit refunds half the cost (25)");
        TestAssert.Near(0.0, match.Hero.Mana, 0.01, "no mana is spent");
        TestAssert.True(max > 400.0, "the probe's health is below the maximum, where a heal is not clamped");
    }

    /// <summary>The cleaver deals the larger of its base and a share of the target's current health, capped at its maximum (magic damage).</summary>
    [Check("scripting.bb-drmundo-infected-cleaver-deals-the-larger-of-its-base-and-a-share-of-current-health")]
    public static void CleaverDamageFollowsTheScript()
    {
        foreach (var (foeHealth, expected) in new[] { (200.0, 80.0), (600.0, 90.0), (3000.0, 300.0) })
        {
            using var match = Ready(1, 400.0);
            match.Foe.Health = foeHealth;
            match.Hero.SpellCooldowns[0] = 0.0;
            match.Cast(0, at: match.Foe.Position);
            match.Advance(1.0);
            var hit = HitsOn(match, "Foe").Last();
            // The foe regenerates a little between the probe's set-up and the hit (600 became 600.4), so the band is the regeneration.
            TestAssert.Near(expected, hit.Raw, 0.5, $"at {foeHealth:0} health the cleaver's raw damage is {expected:0}");
            TestAssert.Near(Mitigated(expected, match.Foe.SpellBlock), hit.Dealt, 1.0,
                "the foe takes that magic damage less its magic resistance");
        }
    }

    /// <summary>A hit slows its target by 40 percent for two seconds (BUFF_Slow, MoveMod -0.4, Duration 2.0).</summary>
    [Check("scripting.bb-drmundo-infected-cleaver-slows-by-forty-percent-for-two-seconds")]
    public static void CleaverSlowsForTwoSeconds()
    {
        using var match = Ready(1, 400.0);
        var speed = match.Foe.Speed;
        match.Hero.SpellCooldowns[0] = 0.0;
        match.Cast(0, at: match.Foe.Position);
        var speedWhileSlowed = double.NaN;
        string? slowName = null, wireName = null;
        var slowed = Span(match, () =>
        {
            var slow = match.Foe.Buffs.FirstOrDefault(buff => buff.Type == BuffType.Slow);
            if (slow is not null && double.IsNaN(speedWhileSlowed))
            {
                speedWhileSlowed = match.Foe.Speed;
                slowName = slow.Name;
                wireName = Amberline.Data.GameData.Default.ClientBuffName(slow.Name);
            }
            return slow is not null;
        }, 6.0);
        // Batch 269 goal 3: BBSpellBuffAdd without a BuffName adds the buff of the running script (the native reads the current
        // script), so the slow is the InfectedCleaverMissile buff. The script's BuffName global is that buff's display name
        // (the buff script's accessor: BuffName, else the script name), and the add packet carries it (GameData.ClientBuffName).
        TestAssert.Equal("InfectedCleaverMissile", slowName, "the unnamed add is the buff of the running script");
        TestAssert.Equal("InfectedCleaverMissile", wireName, "the client is sent the binding: the script's BuffName InfectedCleaverDebuff has no script (batch 357)");
        TestAssert.True(speedWhileSlowed < speed * 0.7 + 1.0,
            $"the slow takes the foe below 70 percent of its speed (got {speedWhileSlowed:0.##} of {speed:0.##})");
        TestAssert.Near(2.0, slowed, 0.15, "the slow runs for two seconds from the hit");
        TestAssert.Near(speed, match.Foe.Speed, 0.5, "the foe's speed is back when it ends");
    }

    [Check("scripting.bb-drmundo-infected-cleaver-needs-more-health-than-its-cost")]
    public static void CleaverNeedsMoreHealthThanItsCost()
    {
        using var match = Ready(1, CleaverCost[0]);
        TestAssert.True(!match.CanCast(0).Allowed, "at exactly its cost (50) the cleaver is refused: the gate is health above the cost");
        match.Hero.Health = CleaverCost[0] + 0.5;
        TestAssert.True(match.CanCast(0).Allowed, "50.5 health casts it");
        match.Learn(3);
        match.Hero.Health = CleaverCost[2];
        TestAssert.True(!match.CanCast(0).Allowed, "rank three: 70 health is refused");
    }

    /// <summary>Burning Agony switches on with no cooldown, drains twenty health a second at rank one and hurts the enemies in its area once a second.</summary>
    [Check("scripting.bb-drmundo-burning-agony-toggles-and-drains-twenty-health-a-second")]
    public static void AgonyTogglesAndDrains()
    {
        using var match = Ready(1, 400.0);
        match.Hero.SpellCooldowns[1] = 0.0;
        TestAssert.True(match.Cast(1, match.Hero), "Burning Agony switches on at rank one");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "BurningAgony") is not null, "the aura is on");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[1], 0.01, "the toggle-on press has no cooldown (AdjustCooldown returns 0)");

        // A window of one second holds exactly one tick, whatever the phase of the periodic execution.
        match.Advance(1.05);
        var health = match.Hero.Health;
        match.Advance(1.0);
        TestAssert.Near(-AgonyDrain[0], match.Hero.Health - health, 2.0, "one tick drains 20 health (regeneration aside)");

        // Each tick hits the foe 300 units away (the area is 325) for 40 magic damage, less its magic resistance.
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 1, "the foe inside the area is hit by the aura");
        TestAssert.Near(AgonyDamage[0], hits.Last().Raw, 0.01, "each tick's raw damage is 40 at rank one");
        TestAssert.Near(Mitigated(AgonyDamage[0], match.Foe.SpellBlock), hits.Last().Dealt, 1.0, "the foe takes it less magic resistance");

        match.Cast(1, match.Hero);
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "BurningAgony") is null, "a second press switches it off");
    }

    /// <summary>While the aura is on, a disable on Dr. Mundo lasts 15 percent less at rank one (BuffOnAllowAdd, DurationMod 0.85); the foe's is not changed.</summary>
    [Check("scripting.bb-drmundo-burning-agony-shortens-disables-on-him-by-fifteen-percent-at-rank-one")]
    public static void AgonyShortensDisablesOnHim()
    {
        using var match = Ready(1, 400.0);
        match.Hero.SpellCooldowns[1] = 0.0;
        match.Cast(1, match.Hero);
        match.Advance(0.1);
        match.Runtime.SpellBuffs.Add(match.Hero, "Stun", BuffType.Stun, 2.0, match.Foe.NetId, null, canMitigateDuration: true);
        TestAssert.Near(2.0 * 0.85, Span(match, () => Buff(match.Hero, "Stun") is not null, 4.0), 0.1,
            "a two-second stun on him lasts 1.7 seconds inside his aura at rank one");

        match.Runtime.SpellBuffs.Add(match.Foe, "Stun", BuffType.Stun, 2.0, match.Hero.NetId, null, canMitigateDuration: true);
        TestAssert.Near(2.0, Span(match, () => Buff(match.Foe, "Stun") is not null, 4.0), 0.1,
            "the foe's own stun is not shortened by his aura");
    }

    /// <summary>Batch 125 reported a toggled-on Burning Agony at 25 health paying its drain down to 1. Its guard ends the aura instead.</summary>
    [Check("scripting.bb-drmundo-burning-agony-ends-below-its-drain-without-paying-it")]
    public static void AgonyEndsBelowItsDrain()
    {
        using var match = Ready(3, 400.0);
        match.Hero.SpellCooldowns[1] = 0.0;
        match.Cast(1, match.Hero);
        match.Advance(0.3);
        match.Hero.Health = 25.0;
        match.Advance(1.2);
        TestAssert.True(Buff(match.Hero, "BurningAgony") is null, "the aura ends: 25 health is below the rank-three drain of 30");
        TestAssert.True(match.Hero.Health > 24.0 && match.Hero.Health < 30.0, $"no drain is paid (health {match.Hero.Health:0.##})");
    }

    /// <summary>Masochism costs 25 health at rank one and adds 40 plus 0.4 times the missing health percent to his attack damage.</summary>
    [Check("scripting.bb-drmundo-masochism-costs-health-and-adds-the-missing-health-share")]
    public static void MasochismAddsTheMissingHealthShare()
    {
        using var match = Ready(1, 300.0);
        match.Hero.SpellCooldowns[2] = 0.0;
        var attack = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(2, match.Hero), "Masochism casts at rank one with 300 health");
        match.Advance(0.3);
        TestAssert.Near(300.0 - MasochismCost[0], match.Hero.Health, 3.0, "the cost is 25 health");
        var missing = 100.0 * (1.0 - match.Hero.Health / match.Hero.MaxHealth);
        TestAssert.Near(MasochismBase[0] + MasochismShare[0] * missing, match.Hero.AttackDamage - attack, 1.0,
            "the bonus is 40 plus 0.4 times the missing health percent, read after the cost");
    }

    [Check("scripting.bb-drmundo-masochism-is-refused-below-its-cost")]
    public static void MasochismNeedsItsCost()
    {
        using var match = Ready(1, 24.5);
        match.Hero.SpellCooldowns[2] = 0.0;
        TestAssert.True(!match.CanCast(2).Allowed, "24.5 health is below the cost of 25");
        match.Hero.Health = 25.0;
        TestAssert.True(match.CanCast(2).Allowed, "25 health pays the cost (the gate is health at or above it)");
    }

    [Check("scripting.bb-drmundo-masochism-lasts-five-seconds")]
    public static void MasochismLastsFiveSeconds()
    {
        using var match = Ready(1, 300.0);
        match.Hero.SpellCooldowns[2] = 0.0;
        var attack = match.Hero.AttackDamage;
        match.Cast(2, match.Hero);
        match.Advance(0.3);
        TestAssert.True(match.Hero.AttackDamage - attack > 35.0, "the bonus is on");
        match.Advance(4.6);
        TestAssert.True(match.Hero.AttackDamage - attack > 35.0, "the bonus is still on at about five seconds");
        match.Advance(0.7);
        TestAssert.Near(0.0, match.Hero.AttackDamage - attack, 0.5, "the bonus ends after five seconds");
    }

    /// <summary>Sadism costs a fifth of his current health and hastens him by 15 percent at rank one.</summary>
    [Check("scripting.bb-drmundo-sadism-costs-a-fifth-of-his-health-and-hastens-him")]
    public static void SadismCostsAFifthAndHastens()
    {
        using var match = Ready(1, 300.0);
        match.Hero.SpellCooldowns[3] = 0.0;
        var speed = match.Hero.Speed;
        TestAssert.True(match.Cast(3, match.Hero), "Sadism casts at rank one");

        // The cost lands with the spell's own execution; the first heal tick (0.01667 of the maximum, 8.7) may land in the same update.
        for (var t = 0.0; t < 2.0 && match.Hero.Health >= 300.0; t += 1.0 / 30.0) match.Advance(1.0 / 30.0);
        var paid = match.Hero.Health - 300.0 * 0.8;
        TestAssert.True(paid > -0.5 && paid < 9.5, $"the cost is 20 percent of the current health (60): at most one heal tick above 240 (got {paid + 240.0:0.##})");
        match.Advance(0.3);
        TestAssert.Near(speed * (1.0 + SadismHaste[0]), match.Hero.Speed, 0.5, "the haste is 15 percent at rank one");
    }

    /// <summary>SadismHeal heals 40 percent of his maximum health over twelve seconds (every half second, 0.01667 of the maximum each tick).</summary>
    [Check("scripting.bb-drmundo-sadism-heals-forty-percent-of-max-health-over-twelve-seconds")]
    public static void SadismHealsOverTwelveSeconds()
    {
        using var match = Ready(1, 300.0);
        match.Hero.SpellCooldowns[3] = 0.0;
        var max = match.Hero.MaxHealth;
        match.Cast(3, match.Hero);
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "SadismHeal") is not null, "the heal runs");
        match.Advance(14.0);
        TestAssert.True(Buff(match.Hero, "SadismHeal") is null, "the heal ends after its twelve seconds");

        // 300 less the 60 the cost takes, plus 40 percent of the maximum over the twelve seconds, plus the base regeneration over about 14.5 seconds.
        TestAssert.Near(300.0 - 60.0 + 0.4 * max + 1.45 * 14.5, match.Hero.Health, 12.0,
            "the twelve seconds heal 40 percent of the maximum (regeneration aside)");
    }

    /// <summary>The passive heals 0.3 percent of his maximum health every second while he lives (BBExecutePeriodically, no combat gate).</summary>
    [Check("scripting.bb-drmundo-adrenaline-rush-heals-three-tenths-of-a-percent-a-second")]
    public static void NevershadeHealsEachSecond()
    {
        using var match = Setup();
        match.Advance(1.1);
        var max = match.Hero.MaxHealth;
        var tick = 0.003 * max;

        // Two windows of the same length: with the passive and without it. Their difference is the passive alone (three ticks in 3.5 s).
        match.Hero.Health = max * 0.5;
        var withPassive = match.Hero.Health;
        match.Advance(3.5);
        var gainedWith = match.Hero.Health - withPassive;
        DropPassive(match);
        match.Hero.Health = max * 0.5;
        var without = match.Hero.Health;
        match.Advance(3.5);
        var gainedWithout = match.Hero.Health - without;
        TestAssert.Near(3.0 * tick, gainedWith - gainedWithout, 0.5,
            $"three ticks of 0.3 percent of the maximum ({tick:0.##} each) in 3.5 seconds");
    }

    /// <summary>A death takes the buffs; the respawned Dr. Mundo has the empty pool again.</summary>
    [Check("scripting.bb-drmundo-dies-and-respawns-with-an-empty-pool")]
    public static void DeathAndRespawn()
    {
        using var match = Ready(1, 300.0);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        var waited = 0.0;
        while (!match.Hero.Alive && waited < 90.0)
        {
            match.Advance(0.5);
            waited += 0.5;
        }
        TestAssert.True(match.Hero.Alive, "Dr. Mundo respawns");
        TestAssert.Equal(0.0, match.Hero.MaxMana, "the respawned pool is still empty");
        TestAssert.Equal(0.0, match.Hero.Mana, "and holds no mana");
    }
}
