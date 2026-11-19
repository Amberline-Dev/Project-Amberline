using Amberline.Core;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 896 (owner decision, 18 November 2026: "Fix in its own batch"; batch 892's Burnout finding): a renewal keeps the buff instance's
/// variables. Spells/ShyvanaImmolationAura.luaobj's OnBuffActivate adds 0.2 x bonus AD into <c>InstanceVars.DamagePerTick</c>, its
/// BuffOnUpdateActions burns that every second and multiplies <c>InstanceVars.MovementSpeed</c> (+30 % at rank one) by 0.85, and its
/// BuffOnHitUnit renews itself (BUFF_RENEW_EXISTING, +1 s) with fresh NextBuffVars (25 and 0.3). Before the fix every hit handed those over:
/// the burn fell to 25 and the speed jumped back to +30 %.
/// </summary>
public static class BbB896BurnoutChecks
{
    [Check("scripting.p132-shyvana-burnout-hits-keep-its-bonus-ad-burn-and-decaying-speed")]
    public static void BurnoutRenewalsKeepTheInstanceVariables()
    {
        using var match = new Match("Shyvana", foeChampion: "Orianna");
        var hero = match.Hero;
        var foe = match.Foe;
        // Two Long Swords: +20 bonus AD, so the activation's burn is 25 + 0.2 x 20 = 29 a second at rank one.
        hero.Inventory = [new InventorySlot(1036), new InventorySlot(1036), null, null, null, null];
        match.Learn(1);
        match.Refill();
        foe.Position = hero.Position + new Position2(150, 0);

        // The payload a renewal swaps in and pushes, read where the add lands (before the hook that renewed it ends).
        var atRenewal = new List<double>();
        var spells = match.Runtime.Spells;
        var previous = spells.ScriptBuffAdded;
        spells.ScriptBuffAdded = (target, caster, name) =>
        {
            previous?.Invoke(target, caster, name);
            if (name == "ShyvanaImmolationAura") atRenewal.Add(Buff(target, name)!.Payload.MoveSpeedPercent);
        };

        TestAssert.True(match.Cast(1), "Burnout is cast");
        spells.HeroOrders!.StartAttack(hero, foe);
        var speeds = new List<double>();
        for (var step = 0; step < 150 && Buff(hero, "ShyvanaImmolationAura") is { } burnout; step++)
        {
            speeds.Add(burnout.Payload.MoveSpeedPercent);
            match.Advance(Step);
        }

        var burns = HitsOn(match, "Foe").Where(hit => hit.Kind == "SpellAoe").ToList();
        var attacks = HitsOn(match, "Foe").Count - burns.Count;
        TestAssert.True(attacks >= 2 && atRenewal.Count >= 3, $"at least two hits renewed Burnout (attacks {attacks}, adds {atRenewal.Count})");
        TestAssert.True(burns.Count >= 5, $"Burnout burned at least five times ({burns.Count})");
        foreach (var burn in burns)
            TestAssert.Near(29.0, burn.Raw, 1e-4, $"every burn keeps the bonus AD part (burns: {string.Join(", ", burns.Select(b => R(b.Raw)))})");

        var values = speeds.Distinct().ToList();
        var shown = string.Join(", ", values.Select(R));
        TestAssert.Near(0.3, speeds[0], 1e-6, "+30 % at the cast");
        for (var i = 1; i < speeds.Count; i++)
            TestAssert.True(speeds[i] <= speeds[i - 1] + 1e-12, $"the speed only decays (step {i}: {R(speeds[i - 1])} -> {R(speeds[i])})");
        for (var i = 0; i < values.Count; i++)
            TestAssert.Near(0.3 * Math.Pow(0.85, i), values[i], 1e-6, $"x0.85 a second ({shown})");
        TestAssert.True(values.Count >= 5, $"the speed decayed through the extensions ({shown})");
        // At the add itself (before the hook that renewed it ends) the renewal already carries the live speed, not the add's +30 %.
        var renewals = string.Join(", ", atRenewal.Select(R));
        foreach (var renewal in atRenewal)
            TestAssert.True(values.Any(value => Math.Abs(value - renewal) < 1e-9), $"each add carries a live speed ({renewals}; live {shown})");
        TestAssert.True(atRenewal.Skip(1).Any(renewal => renewal < 0.3 - 1e-6), $"a hit after the first decay renews at the decayed speed ({renewals})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
