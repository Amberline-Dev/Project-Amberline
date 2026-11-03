using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 324 (markers #35/#36 of batch 314): a cone fires when the client's targeter would fire it. Incinerate ships CastConeDistance 625 and
/// CastConeAngle 24.76: the chord 2 x 625 x sin(12.38) = 268.0, the reach 625 - 268.0 / 1.5 = 446.3 and L = 446.3 + 75 = 521.3
/// (the cone targeter's range test, its inner radius and its final target point). The numbers are the client's,
/// not the code's.
/// </summary>
public static class ConeTargeterChecks
{
    [Check("spells.cone-fires-by-the-client-targeter-test-and-walks-to-its-start")]
    public static void ConeFiresByTheClientTargeterTest()
    {
        foreach (var (name, startAhead, aimAhead, fires) in new[]
                 {
                     // Beyond L: the old 75-unit test on the start (the hero's own spot) fired it at once.
                     ("aim 600, start at the hero", 0.0, 600.0, false),
                     // A client start 150 ahead (a far cursor), the aim inside L: the old test walked to the start first.
                     ("aim 500, start 150 ahead", 150.0, 500.0, true),
                     // Nearer than the reach: the targeter moves the end out to 446.3, which is inside the annulus.
                     ("aim 200, start at the hero", 0.0, 200.0, true),
                 })
        {
            using var match = new Match("Annie", foeChampion: "Ezreal");
            match.Learn(1);
            var hero = match.Hero;
            hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(9000, 9000);
            match.FarFoe.Position = new Position2(9000, 9500);
            match.Refill();
            match.Advance(0.5);
            var p = hero.Position;
            var request = new SpellCastRequest(1, false, new Position3(p.X + startAhead, 0, p.Z), new Position3(p.X + aimAhead, 0, p.Z), 0);
            var verdict = match.Runtime.Spells.CanCast(hero, 1, 0, request.Position, match.Now, request.EndPosition);
            TestAssert.Equal(fires, verdict.Allowed, $"{name}: the gate ({verdict.Reason}; spell {match.Runtime.Spells.SpellDataOf(hero, 1).Name})");
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(hero, request, match.Now), $"{name}: the request is accepted");
            TestAssert.Equal(fires, hero.PendingCast is null, $"{name}: {(fires ? "fires where he stands" : "walks first")}");
            if (fires) continue;
            match.Advance(2.0);
            TestAssert.True(hero.PendingCast is null && hero.SpellCooldowns[1] > 0.0, $"{name}: the cast fires after the walk");
            // He walks toward the start the targeter computes (the aim minus the reach, 153.7 ahead) and fires as soon as the aim is within L, as the
            // client's Update does each frame: 600 - 521.3 = 78.7 ahead.
            TestAssert.Near(600.0 - 521.3, hero.Position.X - p.X, 25.0, $"{name}: he fires once the aim is within L (moved {hero.Position.X - p.X:0.#})");
        }
    }

    // Batch 324 (marker #36): only a Direction spell has its aim put inside its range by the client (the location targeter:
    // targeting type 7 moves the target to CastRange + FlatCastRangeMod - 1 along the aim), so it fires at once wherever the cursor is. A Location
    // spell, line missile or not, fires within CastRange + FlatCastRangeMod (the location targeter's range test). Caitlyn's Piltover Peacemaker
    // (CaitlynPiltoverPeacemaker: TargettingType 7, CastType 3, CastRange 1300) aimed 1800 away, as shipped and with the slot overridden to Location (6).
    [Check("spells.line-missile-location-spell-tests-its-range-and-direction-spell-does-not")]
    public static void LocationLineMissileTestsItsRange()
    {
        foreach (var (name, location) in new[] { ("Direction", false), ("Location", true) })
        {
            using var match = new Match("Caitlyn", foeChampion: "Ezreal");
            match.Learn(1);
            var hero = match.Hero;
            hero.Position = new Position2(7000, 7000);
            match.Refill();
            if (location) hero.SpellTargetingOverrides[0] = 6;
            var aim = new Position3(hero.Position.X + 1800, 0, hero.Position.Z);
            var verdict = match.Runtime.Spells.CanCast(hero, 0, 0, aim, match.Now, aim);
            TestAssert.Equal(!location, verdict.Allowed, $"{name}: aimed 1800 away with CastRange 1300 ({verdict.Reason})");
            if (location) TestAssert.Equal(SpellCastFailure.Range, verdict.Failure, $"{name}: the refusal is a range one (walk into range)");
        }
    }

    // Batch 340: an OffsetAOE cast (type 9, Orianna's Dissonance) is sent at the press with the owner's position as both points
    // (the client's cast start sends it on as a targeted cast), so it has no reach to test: a request
    // whose point lies beyond the 10000 CastRange is not a range refusal.
    [Check("spells.offset-aoe-cast-has-no-range-test")]
    public static void OffsetAoeCastHasNoRangeTest()
    {
        using var match = new Match("Orianna", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        hero.Position = new Position2(1500, 1500);
        match.Refill();
        var aim = new Position3(12500, 0, 12500);
        var verdict = match.Runtime.Spells.CanCast(hero, 1, 0, aim, match.Now, aim);
        TestAssert.True(verdict.Allowed, $"Dissonance with a point 15556 away is not range-tested ({verdict.Failure}: {verdict.Reason})");
    }

    // Batch 331: every targeter's CanFireSpell adds the caster's FlatCastRangeMod to GetCastRange (the location, area, target
    // and target-or-location tests alike). Piltover Peacemaker as a Location spell (CastRange 1300) with a
    // FlatCastRangeMod of 600 from the permanent modifiers fires at 1800 and is refused at 1950.
    [Check("spells.location-range-test-adds-the-caster-flat-cast-range-mod")]
    public static void LocationRangeAddsFlatCastRangeMod()
    {
        using var match = new Match("Caitlyn", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        hero.Position = new Position2(7000, 7000);
        hero.PermanentStatMods["FlatCastRangeMod"] = 600.0;
        match.Runtime.HeroStats.Refresh(hero);
        match.Refill();
        hero.SpellTargetingOverrides[0] = 6;
        foreach (var (distance, fires) in new[] { (1800.0, true), (1950.0, false) })
        {
            var aim = new Position3(hero.Position.X + distance, 0, hero.Position.Z);
            var verdict = match.Runtime.Spells.CanCast(hero, 0, 0, aim, match.Now, aim);
            TestAssert.Equal(fires, verdict.Allowed, $"aimed {distance} away with CastRange 1300 + FlatCastRangeMod 600 ({verdict.Reason})");
        }
    }

    // Batch 331: the single-target range test checks a spell with CastRangeUseBoundingBoxes set, edge to edge:
    // the client's bounding-box test (d^2 - (rCaster + rTarget + range)^2 < 0; the radii are the bounding radius and
    // its gameplay collision radius). Singed's Fling (CastRange 125, the flag set) reaches a foe whose centre is 125 + both radii away;
    // Annie's Disintegrate (the flag clear) keeps the centre distance.
    [Check("spells.unit-range-test-is-edge-to-edge-for-cast-range-use-bounding-boxes")]
    public static void UnitRangeIsEdgeToEdgeForBoundingBoxSpells()
    {
        foreach (var (champion, slot, range, boxes) in new[] { ("Singed", 2, 125.0, true), ("Annie", 0, 625.0, false) })
        {
            using var match = new Match(champion, foeChampion: "Ezreal");
            match.Learn(1);
            var hero = match.Hero;
            hero.Position = new Position2(7000, 7000);
            match.Refill();
            var edge = range + hero.CombatRadius + match.Foe.CombatRadius;
            foreach (var (gap, fires) in new[] { (edge - 5.0, boxes), (edge + 5.0, false), (range - 5.0, true) })
            {
                match.Foe.Position = new Position2(hero.Position.X + gap, hero.Position.Z);
                var verdict = match.CanCast(slot, match.Foe);
                TestAssert.Equal(fires, verdict.Allowed, $"{champion} slot {slot}: a foe {gap:0.#} away (edge reach {edge:0.#}; {verdict.Reason})");
            }
        }
    }

    // Batch 331: with MissileFixedTravelTime set, the client's launch gives a line missile speed = distance / time.
    // Riven's Wind Slash waves (RivenLightsaberMissile, MissileSpeed 2200, CastRange 1075, MissileFixedTravelTime 1.0) fly 1075 a second, so a foe
    // 900 away along the aim is struck 450 / 1075 = 0.42 s later than one 450 away (at 2200 a second, 0.2 s later).
    [Check("spells.line-missile-with-a-fixed-travel-time-flies-its-range-in-that-time")]
    public static void LineMissileFixedTravelTime()
    {
        double HitAfterCast(double distance)
        {
            using var match = new Match("Riven", foeChampion: "Ezreal");
            match.Learn(1);
            var hero = match.Hero;
            hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(7000 + distance, 7000);
            match.Refill();
            TestAssert.True(match.Cast(3), "R (Blade of the Exile) turns on");
            match.Advance(0.5);
            match.Refill();
            var cast = match.Now;
            TestAssert.True(match.Cast(3, null, null, new Position2(7900, 7000)), "the second R (Wind Slash) fires along the foe's line");
            match.Advance(2.0);
            var hit = HitsOn(match, "Foe").Select(entry => entry.At).DefaultIfEmpty(double.NaN).First();
            TestAssert.True(!double.IsNaN(hit), $"the waves strike the foe {distance} away");
            return hit - cast;
        }
        var far = HitAfterCast(900.0);
        var near = HitAfterCast(450.0);
        TestAssert.Near(450.0 / 1075.0, far - near, 0.05, $"the waves fly 1075 a second (900 away {far:0.000} s, 450 away {near:0.000} s)");
    }

    // Batch 331: the drag-line range test checks the drag's start point (mDragStartPos, the point its Update
    // hands OnTargetInRange first) within CastRange + FlatCastRangeMod of the caster, like a Location. Rumble's The Equalizer
    // (RumbleCarpetBomb: TargettingType 8, CastRange 1700) started 2000 away is a range refusal (walk first); started 1600 away it fires.
    [Check("spells.drag-line-start-point-is-range-tested")]
    public static void DragLineStartIsRangeTested()
    {
        using var match = new Match("Rumble", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        hero.Position = new Position2(7000, 7000);
        match.Refill();
        foreach (var (start, fires) in new[] { (2000.0, false), (1600.0, true) })
        {
            var from = new Position3(hero.Position.X + start, 0, hero.Position.Z);
            var to = new Position3(hero.Position.X + start, 0, hero.Position.Z + 500);
            var verdict = match.Runtime.Spells.CanCast(hero, 3, 0, from, match.Now, to);
            TestAssert.Equal(fires, verdict.Allowed, $"the drag started {start} away with CastRange 1700 ({verdict.Reason})");
            if (!fires) TestAssert.Equal(SpellCastFailure.Range, verdict.Failure, "the refusal is a range one (walk into range)");
        }
    }
}
