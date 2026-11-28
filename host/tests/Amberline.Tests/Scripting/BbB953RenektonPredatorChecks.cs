using System.Globalization;
using Amberline.Core;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 953 (community report, 28 November 2026: "Renekton W does less damage than intended and apparently doesn't scale with AD"). Both lines
/// ship the same <c>RenektonExecute</c> and <c>RenektonSuperExecute</c>: each strike is <c>BaseDamage</c> = <c>GetBaseAttackDamage</c> x
/// <c>BonusPercent</c> (0.75) + <c>BonusDamage</c> (5 / 15 / 25 / 35 / 45), dealt as a physical <c>DAMAGESOURCE_ATTACK</c> hit with
/// <c>PhysicalDamageRatioVar</c> = <c>BonusPercent</c>, so the bonus attack damage counts at the same 0.75: a strike is
/// <c>BonusDamage + 0.75 x total attack damage</c>, and the tooltip's two strikes are "10 + 150% of his Attack Damage" (three at fifty fury,
/// 15 + 225%). The hero carries a B. F. Sword, so his bonus attack damage is not zero (the batch 250 checks have none and cannot tell).
/// </summary>
public static class BbB953RenektonPredatorChecks
{
    [Check("scripting.bb-renekton-ruthless-predator-scales-with-total-attack-damage")]
    public static void RuthlessPredatorScalesWithTotalAttackDamage()
    {
        var bonusDamage = ByLevel("RenektonExecute", "TargetExecute", "BonusDamage");
        var bonusPercent = ByLevel("RenektonExecute", "TargetExecute", "BonusPercent");
        TestAssert.Equal([5.0, 15.0, 25.0, 35.0, 45.0], bonusDamage, "BonusDamage by rank (RenektonExecute)");
        TestAssert.Equal(0.75, bonusPercent[0], "BonusPercent 0.75 (RenektonExecute)");

        var rows = new List<string>();
        var wrong = new List<string>();
        foreach (var level in new[] { 1, 18 })
        foreach (var rank in new[] { 1, 5 })
        foreach (var fifty in new[] { false, true })
        {
            using var match = new Match("Renekton");
            var hero = match.Hero;
            match.Learn(rank);
            hero.Level = level;
            match.Runtime.HeroStats.Refresh(hero, push: false);
            var baseAd = hero.AttackDamage;
            hero.Inventory = [new InventorySlot(1038), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(hero, push: false);
            var totalAd = hero.AttackDamage;
            TestAssert.True(totalAd - baseAd > 40.0, $"the B. F. Sword gives bonus attack damage (base {baseAd:0.##}, total {totalAd:0.##})");
            hero.Position = new Position2(7000, 7000);
            hero.Facing = new Position2(1.0, 0.0);
            match.Foe.Position = new Position2(7150, 7000);
            match.Refill();
            SetFury(match, 0.0);
            TestAssert.True(match.Cast(1, hero), $"Ruthless Predator casts (level {level}, rank {rank})");
            match.Advance(0.3);
            // RenektonPreExecute's BuffOnPreAttack reads the fury at the swing: at half the bar it casts RenektonSuperExecute (three strikes).
            SetFury(match, fifty ? 0.5 * hero.MaxMana : 0.0);
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "the swing is queued");
            match.Advance(3.0);

            var perHit = bonusDamage[rank - 1] + bonusPercent[rank - 1] * totalAd;
            var strikes = HitsOn(match, "Foe");
            var count = fifty ? 3 : 2;
            var raws = string.Join("+", strikes.Select(hit => hit.Raw.ToString("0.##", CultureInfo.InvariantCulture)));
            rows.Add(FormattableString.Invariant($"L{level} R{rank} fury{(fifty ? 50 : 0)}: base {baseAd:0.##} total {totalAd:0.##} -> strikes {raws} ") +
                     FormattableString.Invariant($"(shipped {count} x {perHit:0.##} = {count * perHit:0.##}; base only {count * (bonusDamage[rank - 1] + bonusPercent[rank - 1] * baseAd):0.##})"));
            if (strikes.Count != count || strikes.Any(hit => Math.Abs(hit.Raw - perHit) > 0.01)) wrong.Add($"L{level} R{rank} fury{(fifty ? 50 : 0)}");
        }
        TestAssert.Equal(0, wrong.Count, $"each strike is BonusDamage + 0.75 x total attack damage; wrong: {string.Join(", ", wrong)}\n  " +
                                          string.Join("\n  ", rows));
    }

    private static void SetFury(Match match, double fury)
    {
        match.Hero.Mana = fury;
        match.Runtime.Effects.SendStats(match.Hero);
    }
}
