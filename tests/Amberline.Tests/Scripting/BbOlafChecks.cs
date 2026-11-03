using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, new champions (batch 327, wave 7, 25 October 2026): Olaf plays from his shipped scripts (Undertow, Berserker Rage, Vicious Strikes,
/// Reckless Swing, Ragnarok). The expected numbers are the literals of the shipped scripts at rank one, and the scenario <c>S-olaf</c> shows the same facts
/// on a headless match. He is not a retail bot (no OlafBot files), so the generic bot template applies.
/// </summary>
public static class BbOlafChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static bool Has(Entity unit, string name) => Buff(unit, name) is not null;

    private static string Names(Entity unit) => string.Join(",", unit.Buffs.Select(buff => buff.Name));

    /// <summary>Olaf at rank one (level one), Ezreal 100 in front of him (inside his reach), Sivir 224 away, no armour on the foes.</summary>
    private static Match Arena()
    {
        var match = new Match("Olaf", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.FarFoe.Position = new Position2(7200, 7100);
        match.Foe.Armor = 0.0;
        match.FarFoe.Armor = 0.0;
        match.Refill();
        return match;
    }

    /// <summary>The axe Undertow leaves on the ground: the hidden unit that carries its expiration timer.</summary>
    private static Entity[] Axes(Match match) => match.Runtime.World.Entities.Values
        .Where(entity => entity.Alive && Has(entity, "OlafAxeExpirationTimer")).ToArray();

    [Check("scripting.bb-olaf-module-binds-the-shipped-closure")]
    public static void ModuleRunsItsClosureWithoutAFailure()
    {
        using var match = Arena();
        match.Advance(2.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        TestAssert.True(Has(match.Hero, "OlafBerzerkerRage"), "the passive is on from the start (CharOnActivate adds it for the whole game); buffs: " + Names(match.Hero));
    }

    /// <summary>
    /// Engine rule (batch 327): <c>BBGetCastSpellTargetPos</c> answers in <c>CharOnSpellCast</c> with the point the cast was aimed at. Olaf's character
    /// script stores his axe's point there (<c>OlafAxeThrow</c> in <c>CharOnSpellCast</c>); before the rule the native threw "outside a spell hook", the
    /// axe never spawned and the script failed.
    /// </summary>
    [Check("scripting.bb-char-on-spell-cast-reads-the-cast-point")]
    public static void CharOnSpellCastReadsTheCastPoint()
    {
        using var match = Arena();
        match.Advance(1.0);
        var point = new Position2(7300, 7000);
        TestAssert.True(match.Cast(0, at: point), "Undertow is cast at a point");
        match.Advance(1.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        var axes = Axes(match);
        TestAssert.Equal(1, axes.Length, "the axe is on the ground after it lands");
        var gap = Math.Sqrt(Math.Pow(axes[0].Position.X - point.X, 2) + Math.Pow(axes[0].Position.Z - point.Z, 2));
        TestAssert.True(gap < 5.0, $"the axe lands at the point the cast named (off by {gap:0.##})");
    }

    [Check("scripting.bb-olaf-ultimate-needs-level-six")]
    public static void RagnarokIsLearnedAtLevelSix()
    {
        using var match = new Match("Olaf", foeChampion: "Ezreal");
        match.Hero.Level = 1;
        TestAssert.False(match.Runtime.Spells.RankUp(match.Hero, 3), "Ragnarok is refused at level one");
        match.Hero.Level = 6;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "Ragnarok is taken at level six");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-berserker-rage-adds-attack-speed-for-missing-health")]
    public static void AttackSpeedGrowsWithTheMissingHealth()
    {
        using var match = Arena();
        match.Advance(1.0);
        var full = match.Hero.AttackSpeedMod;
        // IncPercentAttackSpeedMod by 1 minus the health fraction: half his health missing adds 0.5 to the speed modifier.
        match.Hero.Health = match.Hero.MaxHealth * 0.5;
        match.Advance(0.5);
        TestAssert.Near(full + 0.5, match.Hero.AttackSpeedMod, 0.02, "half the health missing adds 0.5 attack speed (full " + full + ")");
        match.Hero.Health = match.Hero.MaxHealth;
        match.Advance(0.5);
        TestAssert.Near(full, match.Hero.AttackSpeedMod, 1e-6, "the speed comes back at full health");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-undertow-slows-and-hurts-what-the-axe-lands-on")]
    public static void AxeHurtsAndSlowsWhatItLandsOn()
    {
        using var match = Arena();
        match.Advance(1.0);
        var landing = new Position2(7300, 7000);
        match.Foe.Position = landing;
        // The harness's 5000 health is clamped to the real maximum at the foe's next stats refresh (the axe's buffs start one): settle it first.
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        match.Foe.Health = match.Foe.MaxHealth;
        var before = match.Foe.Health;
        var attackDamage = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(0, at: landing), "Undertow is cast at the landing point");
        match.Advance(1.0);
        TestAssert.True(Has(match.Foe, "olafSlow"), "a unit under the landing point is slowed (olafSlow); buffs: " + Names(match.Foe));
        // OlafAxeThrowDamage: 50 bonus at rank one plus half of his attack damage, physical, against the foe's armour (the stats refresh restores
        // Ezreal's own). Batch 350: once, although the struck unit takes the axe's TargetExecute and the landing area's BuffOnMissileEnd pass: both
        // add OlafAxeThrowDamage (0.25 s, BUFF_RENEW_EXISTING), the damage is in its OnBuffActivate, and a renew does not run it again (wiki
        // V1.0.0.111: "could hit the same target twice" was fixed).
        TestAssert.Near(Mitigated(50.0 + 0.5 * attackDamage, match.Foe.Armor), before - match.Foe.Health + match.Foe.HealthRegen, 1.5,
            $"the axe hurts once for its 50 bonus and half his attack damage (lost {before - match.Foe.Health:0.##})");
        match.Advance(2.6);
        TestAssert.False(Has(match.Foe, "olafSlow"), "the slow ends after two and a half seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-undertow-pickup-lowers-q-by-six-seconds")]
    public static void PickingTheAxeUpLowersUndertowByAndRemovesIt()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Cast(0, at: new Position2(7300, 7000));
        match.Advance(1.0);
        var axe = Axes(match).FirstOrDefault();
        TestAssert.True(axe is not null, "the axe is on the ground after it lands");
        var before = match.Hero.SpellCooldowns[0];
        // The caster within 250 of the axe takes it up (BuffOnUpdateActions: the Attacker, with the passive): Q's cooldown drops by six seconds.
        match.Hero.Position = axe!.Position;
        match.Advance(0.3);
        TestAssert.Near(Math.Max(0.0, before - 6.0), match.Hero.SpellCooldowns[0], 0.5,
            $"the pick-up takes six seconds off Undertow (before {before:0.##}, after {match.Hero.SpellCooldowns[0]:0.##})");
        TestAssert.False(axe.Alive && Has(axe, "OlafAxeExpirationTimer"), "the axe is gone after the pick-up");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-undertow-axe-expires-after-its-timer")]
    public static void AxeLeftOnTheGroundExpires()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Cast(0, at: new Position2(7300, 7000));
        match.Advance(0.5);
        var axe = Axes(match).FirstOrDefault();
        TestAssert.True(axe is not null, "the axe is on the ground");
        var placed = match.Now;
        // The timer is ten seconds times (1 + the cooldown reduction) less half a second: 9.5 with no reduction.
        while (axe!.Alive && Has(axe, "OlafAxeExpirationTimer") && match.Now - placed < 12.0) match.Advance(0.05);
        var life = match.Now - placed;
        TestAssert.Near(9.5, life, 0.3, $"the axe lasts its timer (lasted {life:0.##} s)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-vicious-strikes-adds-damage-and-life-steal-for-six-seconds")]
    public static void ViciousStrikesLastsSixSecondsAndHitsHarder()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var dummy = Enemy(match, "Dummy", new Position2(7100, 7000));
        dummy.Armor = 0.0;
        dummy.Health = dummy.MaxHealth = 100000.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), "the plain attack");
        match.Advance(1.5);
        var plain = dummy.MaxHealth - dummy.Health;
        TestAssert.True(match.Cast(1), "Vicious Strikes is cast");
        match.Advance(0.2);
        TestAssert.True(Has(hero, "OlafFrenziedStrikes"), "the strikes are on him for six seconds; buffs: " + Names(hero));
        hero.Health = hero.MaxHealth * 0.5;
        var healthBefore = hero.Health;
        var dealtBefore = dummy.MaxHealth - dummy.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), "the strike that adds its damage");
        match.Advance(1.5);
        var strike = dummy.MaxHealth - dummy.Health - dealtBefore;
        // Flat physical damage 7 (rank one) plus one percent of his maximum health (5000 in the match): about 57 more than a plain attack.
        // GetMaxHealth is his stat maximum (534 at level one, read by the script), not the 5000 the harness gives his entity before the stats refresh.
        TestAssert.Near(7.0 + 0.01 * hero.MaxHealth, strike - plain, 2.0,
            $"the strike adds 7 plus 1% of his maximum health ({strike - plain:0.##} over the plain attack {plain:0.##}, maximum {hero.MaxHealth:0.##})");
        // Life steal of nine percent of the damage, on top of his regeneration over the window.
        TestAssert.True(hero.Health - healthBefore > 4.0, $"the strike steals life (healed {hero.Health - healthBefore:0.##})");
        match.Advance(3.5);
        TestAssert.True(Has(hero, "OlafFrenziedStrikes"), "the strikes are still on him five seconds after the cast");
        match.Advance(1.5);
        TestAssert.False(Has(hero, "OlafFrenziedStrikes"), "the strikes end after six seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-reckless-swing-deals-true-damage-and-costs-health")]
    public static void RecklessSwingHitsTrueAndCostsHim()
    {
        using var match = Arena();
        match.Advance(1.0);
        var foeBefore = match.Foe.Health;
        var ownBefore = match.Hero.Health;
        TestAssert.True(match.Cast(2, match.Foe), "Reckless Swing is cast on the foe");
        match.Advance(0.5);
        // BonusDamage 100 at rank one (true damage, armour does not count); the cost is 40 percent of it, as true damage to him.
        TestAssert.Near(100.0, foeBefore - match.Foe.Health, 2.0, $"the foe takes 100 true damage (lost {foeBefore - match.Foe.Health:0.##})");
        TestAssert.Near(40.0, ownBefore - match.Hero.Health, 2.5, $"he pays 40 health (lost {ownBefore - match.Hero.Health:0.##})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-reckless-swing-refused-when-the-cost-would-kill")]
    public static void RecklessSwingIsRefusedAtItsCost()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Hero.Health = 40.0;
        TestAssert.False(match.CanCast(2, match.Foe).Allowed, "at 40 health the 40 cost would kill him: refused");
        match.Hero.Health = 41.0;
        TestAssert.True(match.CanCast(2, match.Foe).Allowed, "at 41 health the cost leaves him one point: allowed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-self-damage-never-kills-him")]
    public static void HeCannotHurtHimselfToDeath()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Hero.Health = 500.0;
        // His own damage (the source is himself): CharOnPreDamage stops it one point short of his health.
        match.Runtime.Combat.ApplyHit(match.Hero.NetId, match.Hero.NetId, 5000.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.1);
        TestAssert.True(match.Hero.Alive, "a self-inflicted hit does not kill him");
        TestAssert.True(match.Hero.Health >= 0.5 && match.Hero.Health < 3.0, $"he is left at one point (health {match.Hero.Health:0.##})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-ragnarok-removes-and-refuses-crowd-control")]
    public static void RagnarokTakesCrowdControlOffAndRefusesIt()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Runtime.Buffs.AddBuff(match.Hero, "ProbeSnare", BuffType.Snare, 4.0, match.Foe.NetId, canMitigateDuration: false);
        TestAssert.True(Has(match.Hero, "ProbeSnare"), "the snare lands before Ragnarok");
        TestAssert.True(match.Cast(3), "Ragnarok is cast");
        match.Advance(0.2);
        TestAssert.False(Has(match.Hero, "ProbeSnare"), "Ragnarok takes the snare off him");
        match.Runtime.Buffs.AddBuff(match.Hero, "ProbeStun", BuffType.Stun, 2.0, match.Foe.NetId, canMitigateDuration: false);
        TestAssert.False(Has(match.Hero, "ProbeStun"), "a stun is refused while Ragnarok lasts; buffs: " + Names(match.Hero));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-ragnarok-absorbs-the-first-physical-damage-and-lasts-five-seconds")]
    public static void RagnarokAbsorbsTwentyAndLastsFive()
    {
        using var match = Arena();
        match.Advance(1.0);
        // A 35 physical hit without Ragnarok: the loss is the hit after his armour.
        var start = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 35.0, DamageType.Physical, DamageSourceKind.Raw);
        match.Advance(0.2);
        var plainLoss = start - match.Hero.Health;
        match.Hero.Health = match.Hero.MaxHealth;
        match.Refill();
        TestAssert.True(match.Cast(3), "Ragnarok is cast");
        match.Advance(0.2);
        TestAssert.True(Has(match.Hero, "OlafRagnarok"), "the Ragnarok buff is on him");
        var shieldedStart = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 35.0, DamageType.Physical, DamageSourceKind.Raw);
        match.Advance(0.2);
        var loss = shieldedStart - match.Hero.Health;
        // The absorb (20 at rank one) comes off the damage after his armour (BuffOnPreDamage, after the resistances): the same hit loses 20 less.
        TestAssert.Near(Math.Max(0.0, plainLoss - 20.0), loss, 1.5,
            $"the absorb takes 20 off the hit (plain loss {plainLoss:0.##}, with Ragnarok {loss:0.##})");
        // The buff's five seconds run from its activation (the cast's windup is inside them): still on at about four and a half, gone at six.
        match.Advance(4.0);
        TestAssert.True(Has(match.Hero, "OlafRagnarok"), "Ragnarok is still on him at four seconds");
        match.Advance(1.4);
        TestAssert.False(Has(match.Hero, "OlafRagnarok"), "Ragnarok ends after five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-olaf-ragnarok-scales-his-model-by-a-tenth")]
    public static void RagnarokGrowsHisModel()
    {
        using var match = Arena();
        match.Advance(1.0);
        var before = ModelScale.Of(match.Hero);
        TestAssert.True(match.Cast(3), "Ragnarok is cast");
        match.Advance(0.5);
        TestAssert.Near(before + 0.1, ModelScale.Of(match.Hero), 1e-6, "Ragnarok adds a tenth to the model scale");
        match.Advance(5.0);
        TestAssert.Near(before, ModelScale.Of(match.Hero), 1e-6, "the model returns to its size when Ragnarok ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
