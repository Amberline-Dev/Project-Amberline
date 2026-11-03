using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (batch 319): Leona plays from her shipped scripts. The expected values are the literals of the shipped scripts
/// (CharScriptLeona, LeonaShieldOfDaybreak and its attack, LeonaSolarBarrier, LeonaZenithBlade, LeonaSolarFlare, LeonaSunlight), level one and
/// rank one: the checks read raw amounts (before mitigation) and buffs. The scenario <c>S-leona</c> shows the same facts on a headless run.
/// </summary>
public static class BbLeonaChecks
{
    private static bool Has(Entity unit, string name) => unit.Buffs.Any(buff => buff.Name == name);

    private static string Names(Entity unit) => string.Join(",", unit.Buffs.Select(buff => buff.Name));

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    // The hits of one raw amount on a named unit (the trace's committed hits).
    private static int HitsOf(Match match, string victim, double raw) => HitsOn(match, victim).Count(hit => Math.Abs(hit.Raw - raw) < 0.01);

    private static string Hits(Match match, string victim) =>
        string.Join("; ", HitsOn(match, victim).Select(hit => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{hit.At:0.00}s raw {hit.Raw:0.0} {hit.Kind}")));

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Leona at rank one in every slot (level one, no ability power), Ezreal 100 in front of her (inside her attack range).</summary>
    private static Match Arena()
    {
        var match = new Match("Leona", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(6800, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Refill();
        return match;
    }

    // Advances one tick at a time until the condition holds or the time runs out; true when it held.
    private static bool AdvanceUntil(Match match, Func<bool> condition, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds; elapsed += Step)
        {
            if (condition()) return true;
            match.Advance(Step);
        }
        return condition();
    }

    [Check("scripting.bb-leona-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosure()
    {
        var module = new BbChampionModule("Leona");
        foreach (var name in new[] { "CharScriptLeona", "LeonaShieldOfDaybreak", "LeonaShieldOfDaybreakAttack", "LeonaSolarBarrier", "LeonaSolarBarrier2",
                     "LeonaSolarBarrierTracker", "LeonaZenithBlade", "LeonaZenithBladeMissile", "LeonaZenithBladeRoot", "LeonaZenithBladeBuffOrder",
                     "LeonaSolarFlare", "LeonaSolarFlareSlow", "LeonaSolarFlareVision", "LeonaSunlight", "LeonaSunlightPassive" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Leona");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(24, closure.ScriptNames.Count(), "the closure is 24 scripts (coverage audit: bb-coverage.json, Leona)");
    }

    // Shield of Daybreak is an instant self spell (its flags): its SelfExecute arms the buff, seals the slot and holds the cooldown at zero
    // until the strike is spent (the script's own Ver2 cooldown write and the seal of OnBuffActivate).
    [Check("scripting.bb-leona-shield-of-daybreak-arms-on-a-self-cast")]
    public static void ShieldArmsOnItsCaster()
    {
        using var match = new Match("Leona", foeChampion: "Ezreal");
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Hero.SpellRanks = [1, 1, 1, 1];
        TestAssert.True(match.Cast(0, match.Hero), "Shield of Daybreak casts on its caster");
        match.Advance(0.5);
        TestAssert.True(Has(match.Hero, "LeonaShieldOfDaybreak"), "the armed buff stands after the cast");
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "the slot is sealed while the strike waits");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "and its cooldown has not started");
    }

    // The buff's OnBuffActivate runs BBCancelAutoAttack with Reset true: a swing in progress is dropped and the next attack is ready at
    // once, so a Leona who keeps an attack order spends the strike on her very next tick (what the S-leona probes first mistook for a buff
    // that never armed: the buff lived one tick, batch 319).
    [Check("scripting.bb-leona-shield-of-daybreak-resets-the-attack-timer")]
    public static void ShieldResetsTheAttackTimer()
    {
        using var match = Arena();
        match.Advance(0.5);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a plain swing first");
        match.Advance(0.5);
        TestAssert.True(match.Hero.AttackCooldown > 0.5, $"the swing holds the attack timer ({match.Hero.AttackCooldown:0.###} s)");
        TestAssert.True(match.Cast(0, match.Hero), "Shield of Daybreak is cast");
        TestAssert.True(Has(match.Hero, "LeonaShieldOfDaybreak"), "the buff is armed by the cast itself (an instant spell)");
        TestAssert.Near(0.0, match.Hero.AttackCooldown, 1e-9, "OnBuffActivate's BBCancelAutoAttack (Reset true) makes the next attack ready at once");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // BuffOnPreAttack: BBSkipNextAutoAttack and the extra-slot cast of LeonaShieldOfDaybreakAttack, which deals the total attack damage as
    // physical, BonusDamage 40 (rank one) + 0.3 AP as magic, marks the target with LeonaSunlight (3.5 s) and stuns it for 1 s (not a turret).
    // The buff goes, the slot unseals, and OnBuffDeactivate writes SpellCooldown times 1 + the cooldown mod. Batch 335 (owner, 26 October
    // 2026, "Wiki value per spell"): the script's table says 12 at rank one, the inibin and the wiki (V1.0.0.123) 11; BbScriptOverrides
    // loads 11/10/9/8/7 into the script's table, so 11 reaches the slot.
    [Check("scripting.bb-leona-shield-of-daybreak-strike-stuns-and-marks")]
    public static void ShieldStrikeStunsAndMarks()
    {
        using var match = Arena();
        match.Advance(0.5);
        TestAssert.True(match.Cast(0, match.Hero), "Shield of Daybreak is cast");
        match.Advance(0.2);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the empowered attack");
        TestAssert.False(Has(match.Hero, "LeonaShieldOfDaybreak"), "the attack spends the buff (BuffOnPreAttack removes it)");
        TestAssert.False(match.Hero.SpellSeals.IsSealed(false, 0), "the slot is unsealed with it");
        TestAssert.True(match.Hero.SpellCooldowns[0] is > 10.9 and <= 11.0, $"the cooldown is the wiki's 11 at rank one (got {match.Hero.SpellCooldowns[0]:0.###})");
        TestAssert.True(AdvanceUntil(match, () => Has(match.Foe, "Stun"), 1.0), "the strike stuns the foe; foe buffs: " + Names(match.Foe));
        TestAssert.Equal(1, HitsOf(match, "Foe", 40.0), "BonusDamage 40 magic at rank one (no ability power); hits " + Hits(match, "Foe"));
        TestAssert.True(Has(match.Foe, "LeonaSunlight"), "the strike marks the foe with Sunlight");
        match.Advance(0.9);
        TestAssert.True(Has(match.Foe, "Stun"), "the stun holds for its second");
        match.Advance(0.2);
        TestAssert.False(Has(match.Foe, "Stun"), "and ends after it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // LeonaSunlight's BuffOnTakeDamage: a hero attacker that is not the mark's caster and is on the caster's team consumes the mark for
    // SunlightDamage by Leona's level (20 at level one) magic. Her own basic attacks neither mark (the passive's BuffOnHitUnit needs
    // LeonaSolarBarrierTracker, which no shipped script adds; the tooltip: "Leona's damaging spells") nor detonate (Caster ~= Attacker).
    [Check("scripting.bb-leona-sunlight-marks-from-spells-and-detonates-on-an-ally")]
    public static void SunlightMarksFromSpellsAndDetonatesOnAnAlly()
    {
        using var match = Arena();
        match.Advance(0.5);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a plain swing of hers");
        match.Advance(1.0);
        TestAssert.False(Has(match.Foe, "LeonaSunlight"), "her plain attack does not mark");
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Cast(0, match.Hero), "Shield of Daybreak is cast");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the empowered attack");
        TestAssert.True(AdvanceUntil(match, () => Has(match.Foe, "LeonaSunlight"), 1.0), "the strike marks the foe");
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "her own swing on the marked foe");
        match.Advance(0.8);
        TestAssert.True(Has(match.Foe, "LeonaSunlight"), "her own damage does not consume her mark");
        TestAssert.Equal(0, HitsOf(match, "Foe", 20.0), "and deals no Sunlight damage; hits " + Hits(match, "Foe"));
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Ally, match.Foe), "the allied Annie hits the marked foe");
        TestAssert.True(AdvanceUntil(match, () => !Has(match.Foe, "LeonaSunlight"), 1.5), "the ally's hit consumes the mark");
        match.Advance(Step);
        TestAssert.Equal(1, HitsOf(match, "Foe", 20.0), "SunlightDamage 20 magic at level one; hits " + Hits(match, "Foe"));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Eclipse: DefenseBonus 30 armour and magic resistance for 3 s; at the end, units within 450 of her take MagicDamage 60 (+0.4 AP) magic and
    // the mark, and one that was struck gives her LeonaSolarBarrier2 (3 s, the same bonus). Ezreal 300 away is struck; a foe 600 away is not.
    [Check("scripting.bb-leona-eclipse-resists-then-blasts-within-450")]
    public static void EclipseResistsThenBlasts()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        match.FarFoe.Position = new Position2(7600, 7000);
        match.Advance(0.5);
        var armor = match.Hero.Armor;
        var resist = match.Hero.SpellBlock;
        TestAssert.True(match.Cast(1), "Eclipse is cast");
        match.Advance(0.3);
        TestAssert.True(Has(match.Hero, "LeonaSolarBarrier"), "the first buff is on her");
        TestAssert.Near(armor + 30.0, match.Hero.Armor, 0.01, "DefenseBonus 30 armour at rank one");
        TestAssert.Near(resist + 30.0, match.Hero.SpellBlock, 0.01, "and 30 magic resistance");
        match.Advance(2.5);
        TestAssert.Equal(0, HitsOf(match, "Foe", 60.0), "no blast before the buff ends; hits " + Hits(match, "Foe"));
        match.Advance(0.4);
        TestAssert.Equal(1, HitsOf(match, "Foe", 60.0), "MagicDamage 60 magic at rank one on the foe within 450; hits " + Hits(match, "Foe"));
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "nothing on the foe 600 away; hits " + Hits(match, "FarFoe"));
        TestAssert.True(Has(match.Foe, "LeonaSunlight"), "the blast marks the foe");
        TestAssert.True(Has(match.Hero, "LeonaSolarBarrier2"), "a struck foe gives her the second buff");
        TestAssert.Near(armor + 30.0, match.Hero.Armor, 0.01, "which keeps the 30 armour");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Zenith Blade: the missile deals 60 (+0.4 AP) magic and the mark to the champion it hits and orders it (LeonaZenithBladeBuffOrder). At
    // the missile's end she faces the closest ordered unit within 3000 and moves Distance (BBDistanceBetweenObjects: the gap between the
    // bodies) plus 225 along her facing, and the unit is rooted 0.25 s. Restated in batch 659: IdealDistance (the gap) sizes the dash's speed and
    // caps nothing (BbNativeHandlers.Move), so she runs the whole gap + 225 and lands 225 less the two radii past the unit's centre.
    [Check("scripting.bb-leona-zenith-blade-dashes-225-past-the-gap-and-roots")]
    public static void ZenithBladeDashesPastTheGapAndRoots()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7400, 7000);
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Zenith Blade is cast at the foe");
        TestAssert.True(AdvanceUntil(match, () => Has(match.Foe, "LeonaZenithBladeBuffOrder"), 1.0), "the blade orders the champion it hits");
        TestAssert.True(Has(match.Foe, "LeonaSunlight"), "and marks it");
        TestAssert.True(AdvanceUntil(match, () => Has(match.Foe, "LeonaZenithBladeRoot"), 1.5), "the foe is rooted at the blade's end");
        TestAssert.Equal(1, HitsOf(match, "Foe", 60.0), "60 magic at rank one; hits " + Hits(match, "Foe"));
        match.Advance(0.5);
        var contact = match.Hero.CombatRadius + match.Foe.CombatRadius;
        TestAssert.Near(225.0 - contact, Distance(match.Hero.Position, match.Foe.Position), 4.0,
            $"she lands 225 - {contact:0.#} past the foe (hero at {match.Hero.Position.X:0.#},{match.Hero.Position.Z:0.#})");
        TestAssert.True(match.Hero.Position.X > match.Foe.Position.X, "on the far side of the foe");
        TestAssert.False(Has(match.Foe, "LeonaZenithBladeRoot"), "the root is over after its quarter second");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Solar Flare: half a second after the cast, units within 325 of the point take DamageAmount 150 (+0.8 AP) magic, the mark and
    // LeonaSolarFlareSlow (1.5 s, -80% multiplicative), and units within 175 are also stunned for 1.5 s. Ezreal stands on the point,
    // the second foe 250 from it (inside 325, outside 175).
    [Check("scripting.bb-leona-solar-flare-stuns-the-centre-and-slows-the-edge")]
    public static void SolarFlareStunsTheCentreAndSlowsTheEdge()
    {
        using var match = Arena();
        var centre = new Position2(7500, 7000);
        match.Foe.Position = centre;
        match.FarFoe.Position = new Position2(7750, 7000);
        match.Advance(0.5);
        var edgeSpeed = match.FarFoe.Speed;
        TestAssert.True(match.Cast(3, at: centre), "Solar Flare is cast on the point");
        match.Advance(0.3);
        TestAssert.Equal(0, HitsOf(match, "Foe", 150.0), "nothing lands before the half second; hits " + Hits(match, "Foe"));
        TestAssert.True(AdvanceUntil(match, () => HitsOf(match, "Foe", 150.0) > 0, 0.6), "the blast lands; hits " + Hits(match, "Foe"));
        match.Advance(2 * Step);
        TestAssert.Equal(1, HitsOf(match, "FarFoe", 150.0), "150 magic at rank one on the edge foe; hits " + Hits(match, "FarFoe"));
        TestAssert.True(Has(match.Foe, "Stun"), "the centre foe is stunned; buffs: " + Names(match.Foe));
        TestAssert.False(Has(match.FarFoe, "Stun"), "the edge foe is not; buffs: " + Names(match.FarFoe));
        TestAssert.True(Has(match.FarFoe, "LeonaSolarFlareSlow") && Has(match.Foe, "LeonaSolarFlareSlow"), "both are slowed");
        TestAssert.True(Has(match.FarFoe, "LeonaSunlight") && Has(match.Foe, "LeonaSunlight"), "both are marked");
        match.Runtime.HeroStats.Refresh(match.FarFoe, push: false);
        match.Runtime.HeroStats.Refresh(match.FarFoe, push: false);
        TestAssert.True(match.FarFoe.Speed < edgeSpeed * 0.5, $"the slow cuts the edge foe's speed ({edgeSpeed:0.#} -> {match.FarFoe.Speed:0.#})");
        match.Advance(1.6);
        TestAssert.False(Has(match.Foe, "Stun"), "the stun ends after 1.5 s");
        TestAssert.False(Has(match.FarFoe, "LeonaSolarFlareSlow"), "so does the slow");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
