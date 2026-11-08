using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-geometry-line-gameplay-radius-boundaries")]
    public static void LineSpellsUseFullWidthAndResolvedGameplayRadius()
    {
        foreach (var (champion, slot, missile, width, range) in new[]
                 {
                     ("Nidalee", 0, "JavelinToss", 60.0, 1500.0),
                     ("Blitzcrank", 0, "RocketGrabMissile", 70.0, 1050.0),
                     ("Ezreal", 0, "EzrealMysticShotMissile", 60.0, 1200.0), // CastRange 1100, CastRange1..5 1200
                     ("Ezreal", 1, "EzrealEssenceFluxMissile", 120.0, 1050.0),
                     ("Ezreal", 3, "EzrealTrueshotBarrage", 160.0, 20000.0),
                 })
        foreach (var recipient in new[] { "Vayne", "Janna" })
        foreach (var outside in new[] { false, true })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: recipient);
            var hero = match.Hero(0); var target = match.Hero(1);
            TestAssert.False(GameData.Default.Champion(recipient).Raw.TryGetValue(
                InibinReader.KeyHash("Data*GameplayCollisionRadius"), out _));
            TestAssert.Near(65, target.CombatRadius, 0);
            var data = GameData.Default.Spell(missile, slot == 3 ? 3 : 5);
            TestAssert.Near(width, ChampionSpellSupport.LineWidth(data, -1), .001);
            TestAssert.Near(range, data.CastRangeAt(hero.SpellRanks[slot]), .001);
            // Deliberately separate the movement body from the gameplay radius.
            target.Radius = 1;
            target.Position = hero.Position + new Position2(500, width + 65 + (outside ? 1 : -1));
            SpellStep(match, 0);
            var health = target.Health;
            match.CastGround(0, hero.Position + new Position2(900, 0), slot);
            SpellStep(match, 2); SpellStep(match, 1);
            TestAssert.Equal(!outside, target.Health < health, missile + " / " + recipient);
            health = target.Health;
            SpellStep(match, 1);
            TestAssert.Near(health, target.Health, 0, "a penetrating line still hits a recipient only once");
        }
    }

    [Check("spells-geometry-orianna-qe-gameplay-radius-boundaries")]
    public static void OriannaTravelIncludesGameplayRadiusWithoutExpandingLandingBurst()
    {
        foreach (var slot in new[] { 0, 2 })
        foreach (var outside in new[] { false, true })
        {
            using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Vayne");
            var hero = match.Hero(0); var target = match.Hero(1); var ally = match.Hero(2);
            target.Radius = 1;
            target.Position = hero.Position + new Position2(400, outside ? 146 : 144);
            ally.Position = hero.Position + new Position2(800, 0);
            SpellStep(match, 0);
            var health = target.Health;
            if (slot == 0) match.CastGround(0, ally.Position);
            else match.Cast(0, 2, ally);
            // Batch 713: an Instacast line flies from the step after its creation 0x41 (BbLineMissile.Tick), so the launch step is its own step here.
            SpellStep(match, 0);
            SpellStep(match, .7);
            TestAssert.Equal(!outside, target.Health < health, "Orianna slot " + slot);
            health = target.Health;
            SpellStep(match, .1);
            TestAssert.Near(health, target.Health, 0, "one hit per command");
        }
        foreach (var outside in new[] { false, true })
        {
            using var match = new Match("Orianna", Team.Order, enemyChampion: "Janna");
            var hero = match.Hero(0); var target = match.Hero(1);
            var landing = hero.Position + new Position2(600, 0);
            target.Position = landing + new Position2(0, outside ? 176 : 174);
            SpellStep(match, 0);
            var health = target.Health;
            // Batch 713: an Instacast line flies from the step after its creation 0x41 (BbLineMissile.Tick), so the launch step is its own step here.
            match.CastGround(0, landing); SpellStep(match, 0); SpellStep(match, .6);
            TestAssert.Equal(!outside, target.Health < health, "175-unit script burst stays separate from line width");
        }
    }

    [Check("spells-geometry-homing-centre-contact-boundaries")]
    public static void TargetedMissilesReachTheCentreWithoutBorrowingLineOrMovementRadii()
    {
        foreach (var (champion, slot, spell, missile) in new[]
                 {
                     ("Ezreal", 2, "EzrealArcaneShift", "EzrealArcaneShiftMissile"),
                     ("Janna", 1, "SowTheWind", "SowTheWind"),
                     ("Vayne", 2, "VayneCondemn", "VayneCondemnMissile"),
                 })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Janna");
            var hero = match.Hero(0); var target = match.Hero(1);
            target.Position = hero.Position + new Position2(400, 0);
            target.Radius = 100;
            SpellStep(match, 0);
            var health = target.Health;
            if (champion == "Ezreal") match.CastGround(0, hero.Position + new Position2(100, 0), slot);
            else match.Cast(0, slot, target);
            SpellStep(match, GameData.Default.Spell(spell, 5).CastTime);
            TestAssert.Near(health, target.Health, 0, "windup does not hit");
            var distance = Distance(hero.Position, target.Position);
            var speed = GameData.Default.Spell(missile, 5).MissileSpeed;
            SpellStep(match, (distance - 1) / speed);
            TestAssert.Near(health, target.Health, 0, champion + " is one unit short of the centre");
            SpellStep(match, 2 / speed);
            TestAssert.True(target.Health < health, champion + " crosses the target plane");
            health = target.Health;
            SpellStep(match, .01);
            TestAssert.Near(health, target.Health, 0, "retired missile cannot hit again");
        }
    }

    [Check("spells-geometry-annie-cone-radial-boundary")]
    public static void AnnieConeDoesNotAddMovementRadiusToItsRange()
    {
        foreach (var recipient in new[] { "Vayne", "Janna" })
        foreach (var outside in new[] { false, true })
        {
            using var match = new Match("Annie", Team.Order, enemyChampion: recipient);
            var hero = match.Hero(0); var target = match.Hero(1);
            target.Position = hero.Position + new Position2(outside ? 626 : 624, 0);
            target.Radius = 100;
            SpellStep(match, 0);
            var health = target.Health;
            // Native W submits a nearby origin and a separate direction endpoint. Batch 336 (restated after batch 324): the client sends the
            // cast only once the cone targeter's range test passes, the end within L = reach + 75 (521.3 for Incinerate) of
            // Annie, so the aim is 500 (an aim at 625 walks first). The cone's units are still those within its 625 CastConeDistance.
            match.CastDirected(0, 1, hero.Position, hero.Position + new Position2(500, 0));
            SpellStep(match, .5);
            TestAssert.Equal(!outside, target.Health < health, "625-unit cone broad phase / " + recipient);
        }
    }

    [Check("spells-geometry-targeted-cast-centre-boundaries")]
    public static void TargetedAdmissionKeepsTheShippedCentreRanges()
    {
        foreach (var (champion, slot, spell, range, friendly) in new[]
                 {
                     ("Annie", 0, "Disintegrate", 625.0, false),
                     ("Orianna", 2, "OrianaRedactCommand", 1020.0, true),
                     ("LeeSin", 1, "BlindMonkWOne", 700.0, true),
                     ("LeeSin", 3, "BlindMonkRKick", 375.0, false),
                     ("Nidalee", 2, "PrimalSurge", 600.0, true),
                     // YorickRavenous.inibin: CastRange 650, CastRange1..5 550. The cast-range getter reads
                     // the entry of the spell's level, so the client reaches 550 at every learned rank (the base 650 is the level-0 entry).
                     ("Yorick", 2, "YorickRavenous", 550.0, false),
                     ("Yorick", 3, "YorickReviveAlly", 850.0, true),
                     ("Shaco", 2, "TwoShivPoison", 625.0, false),
                     ("Vayne", 2, "VayneCondemn", 575.0, false),
                     ("Janna", 1, "SowTheWind", 600.0, false),
                     ("Janna", 2, "EyeOfTheStorm", 800.0, true),
                 })
        {
            using var match = new Match(champion, Team.Order, ally: true,
                enemyChampion: "Vayne", allyChampion: "Janna");
            var hero = match.Hero(0); var target = match.Hero(friendly ? 2 : 1);
            TestAssert.Near(range, GameData.Default.Spell(spell, slot == 3 ? 3 : 5).CastRangeAt(hero.SpellRanks[slot]), .001);
            foreach (var outside in new[] { false, true })
            {
                target.Position = hero.Position + new Position2(range + (outside ? 1 : -1), 0);
                var result = match.Runtime.Spells.CanCast(hero, slot, target.NetId,
                    new Position3(target.Position.X, 0, target.Position.Z), 0);
                TestAssert.Equal(!outside, result.Allowed, champion + " slot " + slot);
                if (outside) TestAssert.Equal(SpellCastFailure.Range, result.Failure);
            }
        }
    }
}
