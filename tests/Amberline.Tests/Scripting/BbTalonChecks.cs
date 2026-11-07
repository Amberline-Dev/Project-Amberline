using System;
using System.Linq;
using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, new champions (batch 321, wave 6, 25 October 2026): Talon plays from his shipped scripts. The expected numbers are the literals of the
/// shipped scripts (level one, rank one), and the scenario <c>S-talon</c> shows the same facts on a headless match. He is not a retail bot.
/// The Cutthroat's landing distance follows the engine's reading of <c>BBDistanceBetweenObjects</c> (the gap between the bodies, see the batch 321 record).
/// </summary>
public static class BbTalonChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static bool Has(Entity unit, string name) => Buff(unit, name) is not null;

    private static string Names(Entity unit) => string.Join(",", unit.Buffs.Select(buff => buff.Name));

    /// <summary>Talon at rank one (level one), Ezreal 100 in front of him (inside his attack range), Sivir 224 away, no armour on either foe.</summary>
    private static Match Arena()
    {
        var match = new Match("Talon", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.FarFoe.Position = new Position2(7200, 7100);
        match.Foe.Armor = 0.0;
        match.FarFoe.Armor = 0.0;
        match.Refill();
        return match;
    }

    [Check("scripting.bb-talon-module-binds-the-shipped-closure")]
    public static void ModuleRunsItsClosureWithoutAFailure()
    {
        using var match = Arena();
        match.Advance(2.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        TestAssert.True(Has(match.Hero, "TalonMercy"), "the passive is on from the start (CharOnActivate adds it for the whole game)");
    }

    [Check("scripting.bb-talon-noxian-diplomacy-arms-the-next-attack")]
    public static void CastArmsTheAttackAndSealsTheSpell()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(0), "Noxian Diplomacy is cast");
        match.Advance(0.5);
        TestAssert.True(Has(match.Hero, "TalonNoxianDiplomacyBuff"), "the ready buff is on him");
        TestAssert.True(Has(match.Hero, "TalonNoxianDiplomacy"), "the attack override is on him");
        TestAssert.False(match.CanCast(0).Allowed, "Q is sealed until its attack lands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-talon-noxian-attack-adds-the-core-damage")]
    public static void TheNextAttackAddsTheLevelsCoreDamage()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        // An inert dummy, not a hero: the bleed (heroes only) would add its own ticks to the window of the measurement.
        var dummy = Enemy(match, "Dummy", new Position2(7100, 7000));
        dummy.Armor = 0.0;
        // A plain attack first, then the attack after Noxian Diplomacy: the difference is the core damage of the rank (30 at rank one).
        var before = dummy.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), "the plain attack");
        match.Advance(1.5);
        var plain = before - dummy.Health + dummy.HealthRegen * 1.5;
        match.Cast(0);
        match.Advance(0.3);
        before = dummy.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), "the attack that procs");
        match.Advance(1.5);
        var proc = before - dummy.Health + dummy.HealthRegen * 1.5;
        // SrcValueByLevel 30 / 60 / 90 / 120 / 150 of BuffOnHitUnit (rank one: 30) is added to the hit; the bonus term is 0.3 of the bonus attack damage,
        // which is zero at level one with no items.
        TestAssert.Near(plain + 30.0, proc, 2.5, $"the hit is the plain attack ({plain:0.##}) plus the rank-one core damage of 30 (got {proc:0.##})");
        TestAssert.False(Has(hero, "TalonNoxianDiplomacy"), "the hit uses the attack override up");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-talon-noxian-bleeds-a-champion-for-five-seconds")]
    public static void TheHitBleedsAChampionForFiveSeconds()
    {
        using var match = Arena();
        match.Advance(1.0);
        match.Cast(0);
        match.Advance(0.3);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack that procs");
        match.Advance(0.5);
        TestAssert.True(Has(match.Foe, "TalonBleedDebuff"), "a champion the hit lands on bleeds (TalonBleedDebuff); buffs: " + Names(match.Foe) + "; failures: " + Failures(match));
        match.Advance(5.5);
        TestAssert.False(Has(match.Foe, "TalonBleedDebuff"), "the bleed ends after five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 336: the bleed's vision cube (BBSpawnMinion TestCube, VisibilitySize 450, not a placemarker; the record's ExpGivenOnDeath 77) expires
    /// beside the bleeding champion and pays nobody: retail's death experience is a barracks minion's (obj_AI_Base ScriptExpGiven/ExpGiveRadius).
    /// </summary>
    [Check("scripting.bb-talon-bleed-cube-pays-no-experience-when-it-expires")]
    public static void TheBleedCubePaysNoExperience()
    {
        using var match = Arena();
        match.Advance(1.0);
        var experience = match.Foe.Experience;
        match.Cast(0);
        match.Advance(0.3);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack that procs");
        match.Advance(0.5);
        var cubes = match.Runtime.World.Entities.Values.Where(unit => unit.Name == "TestCube" && unit.Team == match.Hero.Team).ToArray();
        TestAssert.True(cubes.Length > 0, "the bleed spawns its vision cube; failures: " + Failures(match));
        match.Advance(6.0);
        TestAssert.True(cubes.All(cube => !cube.Alive), "the cube expires with the bleed");
        TestAssert.Near(experience, match.Foe.Experience, 1e-9, "its death pays the bleeding champion beside it no experience");
    }

    [Check("scripting.bb-talon-rake-blades-slow-the-foe-for-two-seconds")]
    public static void RakeSlowsTheFoeForTwoSeconds()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(1), "Rake is cast");
        match.Advance(1.0);
        TestAssert.True(Has(match.Foe, "TalonSlow"), "a blade that hits slows its foe (TalonSlow); buffs: " + Names(match.Foe));
        match.Advance(2.5);
        TestAssert.False(Has(match.Foe, "TalonSlow"), "the slow ends after two seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>Batch 362 (owner finding 1, Talon W, "doesn't cast where the cursor is pointing"): Rake's blades fly along the cast point. Talon starts
    /// facing south-east, the foe stands 300 units north and the cast is aimed at it: he turns to the point and a blade slows it.</summary>
    [Check("scripting.bb-talon-rake-cone-turns-to-its-cast-point")]
    public static void RakeConeFollowsTheCastPoint()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7000, 7300);
        match.Advance(1.0);
        TestAssert.True(match.Cast(1, null, null, match.Foe.Position), "Rake is cast at the foe, 300 units north");
        match.Advance(match.CastFrameOf(1));
        TestAssert.True(match.Hero.Facing.Z > 0.99, $"Talon turns to the cast point at its cast frame (facing {match.Hero.Facing.X:0.00}, {match.Hero.Facing.Z:0.00})");
        match.Advance(1.0);
        TestAssert.True(Has(match.Foe, "TalonSlow"), "a blade of the cone hits the foe at the cast point (TalonSlow); buffs: " + Names(match.Foe));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 433 (owner native 28 October, Talon W: "disappear when coming back"): the third blade's missile end takes <c>TalonRakeMissileOne</c> off
    /// Talon before it puts <c>TalonRakeMarker</c> on its placemarker, so the buff's deactivation (the return loop) passed that placemarker over and
    /// its blade never came back. Every placemarker's marker must be taken back, so every blade returns.
    /// Batch 709: the loop ends each placemarker it visits with the cube's own 50000 hit (after the marker is off), which the engine refused until
    /// then, so the three cubes stayed for the rest of the match. A cube the loop skipped would keep its marker and its life: all three must die.
    /// </summary>
    [Check("scripting.bb-talon-rake-every-blade-returns")]
    public static void RakeEveryBladeReturns()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(1), "Rake is cast");
        var cubes = new System.Collections.Generic.HashSet<Entity>();
        for (var step = 0; step < 50; step++)
        {
            match.Advance(0.05);
            foreach (var unit in match.Runtime.World.Entities.Values)
                if (unit.Name == "TestCube" && unit.Placemarker && unit.Team == match.Hero.Team) cubes.Add(unit);
        }
        TestAssert.Equal(3, cubes.Count, "the three blades end as three placemarkers");
        TestAssert.True(cubes.All(cube => !Has(cube, "TalonRakeMarker")),
            "every placemarker's marker is taken back, so every blade returns; still marked: " +
            string.Join(",", cubes.Where(cube => Has(cube, "TalonRakeMarker")).Select(cube => cube.NetId)));
        TestAssert.True(cubes.All(cube => !cube.Alive),
            "the loop ends every placemarker with its own 50000 hit; still alive: " +
            string.Join(",", cubes.Where(cube => cube.Alive).Select(cube => cube.NetId)));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 349 (batch 321's open 5): Mercy (<c>TalonMercy</c> <c>BuffOnHitUnit</c>) multiplies the hit by 1.1 on an AI unit that holds a buff of
    /// type <c>BUFF_Slow</c>, <c>BUFF_Stun</c>, <c>BUFF_Net</c> or <c>BUFF_Suppression</c>. A typed slow is put on the foe directly.
    /// </summary>
    [Check("scripting.bb-talon-mercy-adds-a-tenth-against-a-slowed-foe")]
    public static void MercyAddsATenthAgainstASlowedFoe()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var foe = match.Foe;
        var before = foe.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the plain attack");
        match.Advance(1.5);
        var plain = before - foe.Health + foe.HealthRegen * 1.5;
        match.Runtime.Buffs.AddBuff(foe, "AcceptanceSlow", Amberline.Core.Buffs.BuffType.Slow, 3.0, hero.NetId, canMitigateDuration: false);
        match.Advance(0.3);
        before = foe.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the attack on the slowed foe");
        match.Advance(1.5);
        var slowed = before - foe.Health + foe.HealthRegen * 1.5;
        TestAssert.Near(1.1 * plain, slowed, 1.0, $"a tenth more on a slowed foe ({plain:0.##} plain, {slowed:0.##} slowed)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-talon-cutthroat-silences-and-amplifies-for-three-seconds")]
    public static void CutthroatSilencesAndAmplifies()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(2, match.Foe), "Cutthroat is cast on the foe");
        match.Advance(0.5);
        // The blink (BBTeleportToPosition to the far side of the foe) is in the scenario S-talon: its landing follows the engine's reading of
        // BBDistanceBetweenObjects (the gap between the bodies, Reconstruction), so the check does not pin the distance.
        TestAssert.True(Has(match.Foe, "Silence"), "the foe is silenced (one second); buffs: " + Names(match.Foe));
        TestAssert.True(Has(match.Foe, "TalonDamageAmp"), "the foe carries the damage amplifier; buffs: " + Names(match.Foe));
        match.Advance(3.0);
        TestAssert.False(Has(match.Foe, "TalonDamageAmp"), "the amplifier ends after three seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Batch 324 (batch 321's open 1): TalonShadowAssault adds the stealth, whose BuffOnSpellCast removes it on any cast, then casts its ring of eight
    // TalonShadowAssaultMisOne children (DoesntTriggerSpellCasts = true) in the same SelfExecute. A flagged child runs no BuffOnSpellCast.
    [Check("scripting.bb-talon-shadow-assault-stealth-survives-its-own-ring-and-the-second-press-casts")]
    public static void ShadowAssaultStealthSurvivesItsRing()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(3), "Shadow Assault is cast");
        match.Advance(0.5);
        TestAssert.True(Has(match.Hero, "TalonShadowAssaultBuff"), "the stealth outlives the ring casts; buffs: " + Names(match.Hero));
        // The slot stays sealed until a ring blade ends (TalonShadowAssaultMisOne's BuffOnMissileEnd unseals it), inside the 2.5 s stealth.
        for (var waited = 0.0; waited < 1.9 && !match.CanCast(3).Allowed; waited += 0.1) match.Advance(0.1);
        TestAssert.True(Has(match.Hero, "TalonShadowAssaultBuff"), "the stealth is still on when the blades have flown out");
        TestAssert.True(match.Cast(3), "the second press (TalonShadowAssaultToggle) is accepted: " + match.CanCast(3).Reason);
        match.Advance(0.5);
        TestAssert.False(Has(match.Hero, "TalonShadowAssaultBuff"), "the second press ends the stealth");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // The shared dispatch (BbHookRouter.RunChildSpellCast): the flag of the child's own script decides; an unflagged child still runs the buff hook.
    [Check("scripting.bb-child-cast-runs-buff-on-spell-cast-unless-the-child-says-doesnt-trigger-spell-casts")]
    public static void FlaggedChildSkipsTheBuffHookOnly()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.True(match.Cast(3), "Shadow Assault is cast");
        match.Advance(0.5);
        var router = match.Module.Router!;
        TestAssert.True(Script("TalonShadowAssaultMisOne").Global("DoesntTriggerSpellCasts") is Amberline.Scripting.BuildingBlocks.BbValue.Bool { Value: true },
            "the ring child says DoesntTriggerSpellCasts = true");
        router.RunChildSpellCast(match.Hero, 45, "TalonShadowAssaultMisOne", Script("TalonShadowAssaultMisOne"), 1, null);
        TestAssert.True(Has(match.Hero, "TalonShadowAssaultBuff"), "a flagged child cast leaves the stealth on");
        TestAssert.True(Script("TalonShadowAssaultMisTwo").Global("DoesntTriggerSpellCasts") is Amberline.Scripting.BuildingBlocks.BbValue.Bool { Value: false },
            "the return child says DoesntTriggerSpellCasts = false");
        router.RunChildSpellCast(match.Hero, 46, "TalonShadowAssaultMisTwo", Script("TalonShadowAssaultMisTwo"), 1, null);
        TestAssert.False(Has(match.Hero, "TalonShadowAssaultBuff"), "an unflagged child cast runs BuffOnSpellCast, which ends the stealth");
    }
}
