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

    /// <summary>
    /// Batch 887 (player report, 18 November 2026: "Orianna Q hitbox is smaller than intended?"): the reach of Command: Attack on the production
    /// step (MatchRuntime's 1/30 s, heroes moving every 0.05 s). The flight meets a unit within OrianaIzuna's LineWidth 80 plus the unit's gameplay
    /// radius (BbLineMissile's swept contact); the landing meets a unit whose centre is within 175 of the ball (OrianaIzuna.SpellOnMissileEnd's
    /// BBForEachUnitInTargetArea, the client's centre test in BbSpellTargeting.InCircle). The enemy hero stands in for every unit type; only its
    /// gameplay radius changes. A unit walking across is judged by where it stands when the ball passes it.
    /// </summary>
    [Check("spells-geometry-orianna-q-hitbox-case-table")]
    public static void OriannaQHitboxCaseTable()
    {
        const double width = 80.0, burst = 175.0, ambiguity = 20.0;
        TestAssert.Near(width, ChampionSpellSupport.LineWidth(GameData.Default.Spell("OrianaIzuna", 5), -1), 0);
        // Standing beside the middle of the flight (the landing is 400 further on, out of the burst).
        foreach (var (unit, radius) in new[] { ("Red_Minion_Basic", 48.0), ("Annie", 55.0), ("Vayne", 65.0), ("Golem", 80.0), ("AncientGolem", 100.0) })
        foreach (var outside in new[] { false, true })
        {
            TestAssert.Near(radius, GameData.Default.Champion(unit).CombatRadius, 0, unit);
            var hit = OriannaQ(new Position2(400, width + radius + (outside ? 1 : -1)), radius, new Position2(800, 0), null, out _);
            TestAssert.Equal(!outside, hit, $"standing beside the flight, {unit} radius {radius}, {(outside ? "1 outside" : "1 inside")}");
        }
        // Walking away from the flight (some across it first), at the hero's own speed: hit exactly when the unit is inside the edge as the ball
        // passes it, within one step of either.
        var inside = 0; var beyond = 0;
        foreach (var start in new[] { -90.0, -60.0, -30.0, 0.0, 30.0, 60.0, 90.0, 120.0 })
        {
            var hit = OriannaQ(new Position2(400, start), 65, new Position2(800, 0), new Position2(400, 900), out var lateral);
            TestAssert.False(double.IsNaN(lateral), $"walking from {start}: the ball passed the unit");
            Console.WriteLine($"B887 Orianna Q, walking from {start}: {lateral:0.0} from the flight as the ball passes, {(hit ? "hit" : "missed")}");
            if (Math.Abs(lateral - (width + 65)) < ambiguity) continue;
            if (lateral < width + 65) inside++; else beyond++;
            TestAssert.Equal(lateral < width + 65, hit, $"walking from {start}: {lateral:0.0} from the flight as the ball passes");
        }
        TestAssert.True(inside > 0 && beyond > 0, $"walking cases on both sides of the edge ({inside} inside, {beyond} beyond)");
        // Walking across the flight as the ball comes.
        TestAssert.True(OriannaQ(new Position2(400, -150), 65, new Position2(800, 0), new Position2(400, 600), out var crossing), "walking across");
        TestAssert.True(crossing < width + 65 - ambiguity, $"walking across: {crossing:0.0} from the flight as the ball passes");
        Console.WriteLine($"B887 Orianna Q, walking across: {crossing:0.0} from the flight as the ball passes, hit");
        // Walking into the flight after the ball has passed: the line has no collision from its start (LineMissileCollisionFromStartPoint 0).
        TestAssert.False(OriannaQ(new Position2(150, 320), 65, new Position2(800, 0), new Position2(150, -600), out var behind), "walking in behind the ball");
        TestAssert.True(behind > width + 65, $"walking in behind the ball: {behind:0.0} from the flight as the ball passes");
        Console.WriteLine($"B887 Orianna Q, walking in behind the ball: {behind:0.0} from the flight as the ball passes, missed");
        // At the stop: the burst reaches a centre within 175 (radius 65: the flight's own edge, 145, is nearer); a unit of radius 100 is met by the
        // flight's edge, 180, there too (the burst adds nothing for it); along the line past the stop only the burst reaches.
        foreach (var (radius, edge) in new[] { (65.0, burst), (100.0, width + 100) })
        foreach (var outside in new[] { false, true })
            TestAssert.Equal(!outside, OriannaQ(new Position2(600, edge + (outside ? 1 : -1)), radius, new Position2(600, 0), null, out _),
                $"beside the stop, radius {radius}, {(outside ? "1 outside" : "1 inside")} {edge}");
        TestAssert.True(OriannaQ(new Position2(750, 0), 65, new Position2(600, 0), null, out _), "150 past the stop, inside the burst");
        TestAssert.False(OriannaQ(new Position2(780, 0), 65, new Position2(600, 0), null, out _), "180 past the stop, outside the burst");
    }

    /// <summary>
    /// One Command: Attack from Orianna (holding the ball) toward <paramref name="aim"/>, stepped as production steps. The enemy hero stands at
    /// <paramref name="offset"/> with the gameplay <paramref name="radius"/>; with <paramref name="walkTo"/> it is ordered there 0.1 s before the cast.
    /// <paramref name="lateral"/> is its distance from the flight at the first step that finds the ball level with it or past it.
    /// </summary>
    private static bool OriannaQ(Position2 offset, double radius, Position2 aim, Position2? walkTo, out double lateral)
    {
        const double step = 1.0 / 30.0;
        using var match = new Match("Orianna", Team.Order, enemyChampion: "Vayne");
        var hero = match.Hero(0); var target = match.Hero(1);
        var origin = hero.Position;
        target.CombatRadius = radius;
        target.Position = origin + offset;
        if (walkTo is { } to)
        {
            match.Move(1, origin + to);
            for (var elapsed = 0.0; elapsed < .1 - 1e-9; elapsed += step) match.Runtime.Tick(step);
        }
        match.CastGround(0, origin + aim);
        lateral = double.NaN;
        // The hit is the script's own mark: TargetExecute and the landing burst both add the 1 s OrianaIzunaDamage to the unit they strike (the
        // runtime's stat refresh moves the test's 5000 health, so health is no signal here).
        var hit = false;
        for (var elapsed = 0.0; elapsed < 1.2; elapsed += step)
        {
            match.Runtime.Tick(step);
            hit |= target.Buffs.Any(buff => string.Equals(buff.Name, "OrianaIzunaDamage", StringComparison.OrdinalIgnoreCase));
            var ball = match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
                .Where(snapshot => snapshot.Launched && string.Equals(snapshot.SpellName, "OrianaIzuna", StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => (Position2?)snapshot.Position).FirstOrDefault();
            if (double.IsNaN(lateral) && ball is { } at && at.X - origin.X >= target.Position.X - origin.X)
                lateral = Math.Abs(target.Position.Z - origin.Z);
        }
        if (walkTo is null) TestAssert.Near(0, Distance(target.Position, origin + offset), .5, "the unit stands still");
        return hit;
    }
}
