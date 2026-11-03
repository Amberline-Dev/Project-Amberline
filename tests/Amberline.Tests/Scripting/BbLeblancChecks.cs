using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 337 (26 October 2026, wave 9): LeBlanc plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module).
/// Expected numbers are the literals of the shipped scripts (CharScriptLeblanc, LeblancSlide, LeblancSlideReturn, LeblancSoulShackle,
/// LeblancMIApplicator), read from the bytecode listings, and the rank-one figures of her inibins. The batch 337 record names every difference.
/// </summary>
public static class BbLeblancChecks
{
    /// <summary>LeBlanc at level six with every slot at rank one, the foe 250 away with 30 magic resist.</summary>
    private static Match Arena()
    {
        var match = new Match("Leblanc");
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "LeBlanc reaches level six (the experience of the seat helper)");
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"LeBlanc's slot {slot} is trained at level six");
        }
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        match.Advance(0.5);
        foreach (var unit in new[] { match.Hero, match.Ally, match.Foe, match.FarFoe })
        {
            Refresh(match, unit);
            unit.Health = unit.MaxHealth;
        }
        return match;
    }

    /// <summary>Two stat refreshes: an update hook of a buff adds its stat during the previous refresh.</summary>
    private static void Refresh(Match match, Entity unit)
    {
        match.Runtime.HeroStats.Refresh(unit, push: false);
        match.Runtime.HeroStats.Refresh(unit, push: false);
    }

    private static BbChampionModule Module(Match match) => (BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Leblanc")!;

    private static string Failures(Match match) =>
        Module(match).ScriptFailures.Count == 0 ? "none"
            : string.Join("; ", Module(match).ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}"));

    private static BuffState? Find(Entity unit, string name) => unit.Buffs.FirstOrDefault(buff => buff.Name == name);

    /// <summary>Her clones on the field: the Mirror Image of Pet.lua (EntityKind.ClonePet) keeps her name in <c>Name</c>, not in <c>ChampionName</c>.</summary>
    private static List<Entity> Clones(Match match) =>
        match.Runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Kind == EntityKind.ClonePet).ToList();

    [Check("scripting.bb-leblanc-module-binds-the-shipped-closure")]
    public static void ModuleBindsTheShippedClosure()
    {
        using var match = Arena();
        TestAssert.True(Module(match) is not null, "LeBlanc runs from the production BbChampionModule");
        TestAssert.Equal("Leblanc", match.Hero.ChampionName, "the hero is LeBlanc");
        match.Advance(0.5);
        TestAssert.Equal("none", Failures(match), "no script failure after the arena's first seconds");
    }

    [Check("scripting.bb-leblanc-distortion-dashes-to-its-point-without-a-script-failure")]
    public static void DistortionDashesToItsPointWithoutAScriptFailure()
    {
        // The shipped LeblancSlideMove's BBMove names IdealDistanceVar 'Distance' in InstanceVars, while BBDistanceBetweenObjectAndPoint wrote the
        // root scope's 'Distance' (no DestVarTable): the variable is nil, the client's number getter reads it as 0, and the dash runs uncapped. The
        // old engine raised "attempt to perform arithmetic on a nil value" at the dash, so Distortion moved nothing.
        using var match = Arena();
        var hero = match.Hero;
        TestAssert.True(match.Cast(1, at: new Position2(7500, 7000)), "Distortion is cast toward (7500, 7000)");
        match.Advance(0.8);
        TestAssert.Equal("none", Failures(match), "the dash runs without a script failure");
        TestAssert.True(hero.Position.X - 7000.0 > 300.0, $"the dash carries LeBlanc toward the point (x now {hero.Position.X:F1})");
    }

    [Check("scripting.bb-leblanc-distortion-second-press-returns-her-to-the-start")]
    public static void DistortionSecondPressReturnsHerToTheStart()
    {
        // Batch 337: the return puts her back at the start of the dash, and its cooldown (20 at rank one) starts at the return: 19.4 left 0.6 s after it.
        using var match = Arena();
        var hero = match.Hero;
        var start = hero.Position;
        TestAssert.True(match.Cast(1, at: new Position2(7500, 7000)), "Distortion is cast toward (7500, 7000)");
        match.Advance(0.8);
        TestAssert.True(hero.Position.X - start.X > 300.0, "the dash has carried her away from the start");
        TestAssert.True(match.Cast(1, at: start), "the second press is accepted while the return window runs");
        match.Advance(0.6);
        TestAssert.True(Math.Abs(hero.Position.X - start.X) < 40.0 && Math.Abs(hero.Position.Z - start.Z) < 40.0,
            $"the second press returns LeBlanc to the start (offset {hero.Position.X - start.X:F1}, {hero.Position.Z - start.Z:F1})");
        TestAssert.Near(19.4, hero.SpellCooldowns[1], 0.2, "the cooldown starts at the return: 20 at rank one, 19.4 left 0.6 s after it");
    }

    [Check("scripting.bb-leblanc-sigil-of-silence-marks-the-foe-and-the-next-spell-detonates")]
    public static void SigilOfSilenceMarksTheFoeAndTheNextSpellDetonates()
    {
        // The mark is the buff LeblancChaosOrb (CharScriptLeblanc names it); the bolt lands about 0.7 s after the press. Ethereal Chains' damage on the
        // marked foe detonates the mark: the mark goes and the foe is silenced (LeblancChaosOrb's second effect).
        using var match = Arena();
        var foe = match.Foe;
        foe.Position = new Position2(7600, 7000);
        var before = foe.Health;
        TestAssert.True(match.Cast(0, foe), "Sigil of Silence is cast at the foe 600 away");
        match.Advance(1.5);
        TestAssert.True(Find(foe, "LeblancChaosOrb") is not null, "the bolt marks the foe (buff LeblancChaosOrb)");
        // Batch 350: the bolt's own damage, isolated (the scenario's 52.4 read a foe with other buffs): DamageByLevel 70 at rank one, the
        // wiki's 70 / 110 / 150 / 190 / 230 (LeBlanc patch history, November 4th hotfix), against 30 magic resist; ability power is zero.
        TestAssert.Near(Mitigated(70.0, 30.0), before - foe.Health + foe.HealthRegen * 1.5, 1.0, "the bolt deals 70 magic at rank one");
        // Batch 346 (batch 340's rule): the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        TestAssert.Near(4.5 + match.CastFrameOf(0), match.Hero.SpellCooldowns[0], 0.1,
            "the cooldown is 6 at rank one, started at the cast frame: 4.5 plus the windup left 1.5 s after the press");
        var hp = foe.Health;
        TestAssert.True(match.Cast(2, foe), "Ethereal Chains is cast at the marked foe");
        match.Advance(1.0);
        TestAssert.True(Find(foe, "LeblancChaosOrb") is null, "the next spell's damage uses the mark up");
        TestAssert.True(Find(foe, "Silence") is not null, "the detonation silences the foe");
        TestAssert.True(hp - foe.Health > 0.0, "the detonation and the chains deal damage");
        TestAssert.Equal("none", Failures(match), "no script failure");
    }

    [Check("scripting.bb-leblanc-ethereal-chains-snares-the-foe-that-stays-in-its-line")]
    public static void EtherealChainsSnaresTheFoeThatStaysInItsLine()
    {
        // The line lands about 0.8 s after the press (cast 0.25 s, 900 units at 1600); the foe that stays in range is snared (LeblancSoulShackleNet)
        // and takes the second hit. The second hit comes in the window the net lasts; the first hit alone leaves LeblancSoulShackle and Slow.
        using var match = Arena();
        var foe = match.Foe;
        foe.Position = new Position2(7700, 7000);
        TestAssert.True(match.Cast(2, at: new Position2(8000, 7000)), "Ethereal Chains is cast along the axis through the foe");
        match.Advance(1.6);
        var afterFirst = foe.Health;
        TestAssert.True(Find(foe, "LeblancSoulShackle") is not null, "the first hit leaves the LeblancSoulShackle mark on the foe");
        match.Advance(2.0);
        TestAssert.True(Find(foe, "LeblancSoulShackleNet") is not null, "the foe that stays in range is snared (LeblancSoulShackleNet)");
        TestAssert.True(foe.Health < afterFirst, "the snare hits the foe again");
        TestAssert.Equal("none", Failures(match), "no script failure");
    }

    [Check("scripting.bb-leblanc-mirror-image-clone-appears-under-forty-percent-health")]
    public static void MirrorImageCloneAppearsUnderFortyPercentHealth()
    {
        // The passive fires below 40 percent (LeblancMIApplicator adds the clone after its delay and a cooldown); the clone is a ClonePet with her name.
        using var match = Arena();
        var hero = match.Hero;
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health * 0.7, DamageType.True, DamageSourceKind.Raw),
            "a hit takes her to 30 percent health");
        TestAssert.True(hero.Health / hero.MaxHealth < 0.4, "she is below forty percent");
        match.Advance(4.5);
        var clones = Clones(match);
        TestAssert.Equal(1, clones.Count, "one clone of LeBlanc (EntityKind.ClonePet) is on the field");
        TestAssert.Equal("Leblanc", clones[0].Name, "the clone wears LeBlanc's name");
        TestAssert.Equal("none", Failures(match), "no script failure");
    }

    [Check("scripting.bb-leblanc-mimic-repeats-her-last-basic-spell-as-its-empowered-copy")]
    public static void MimicRepeatsHerLastBasicSpell()
    {
        // CharScriptLeblanc's CharOnSpellCast stores each Q, W or E cast as lastCast; Mimic (slot 3) casts the M variant of that spell. The ultimate's
        // cooldown is 40 at rank one (its inibin); its cost 100 (50 and 0 at the next ranks).
        using var match = Arena();
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7400, 7000);
        TestAssert.True(match.Cast(0, foe), "Sigil of Silence is cast first: the spell Mimic copies");
        match.Advance(1.5);
        var hp = foe.Health;
        var manaBefore = hero.Mana;
        // The copy is validated against its own targeting: LeblancChaosOrbM refuses an ally ("cannot target an ally"), so Mimic names the foe.
        TestAssert.True(match.Cast(3, foe), "Mimic is cast on the foe (its copy is an enemy-targeted spell)");
        match.Advance(0.8);
        TestAssert.True(hp - foe.Health > 0.0, "the empowered copy of Sigil of Silence hits the foe");
        TestAssert.Near(100.0, manaBefore - hero.Mana, 3.0, "Mimic costs 100 mana at rank one");
        TestAssert.Near(40.0, hero.SpellCooldowns[3], 1.0, "Mimic's cooldown is 40 at rank one");
        TestAssert.Equal("none", Failures(match), "no script failure");
    }
}
