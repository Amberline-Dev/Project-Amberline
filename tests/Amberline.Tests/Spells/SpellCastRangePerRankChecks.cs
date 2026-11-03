using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Cast range per spell level. The stock client reads <c>CastRange</c> and <c>CastRange1..6</c> of every SpellData
/// and <c>GetCastRange</c> returns the entry of the spell's level, so the
/// server's hand-written champions go through <see cref="SpellData.CastRangeAt"/> instead of the base <c>CastRange</c>.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// The spell manager's add step runs a clamp on every spell: each entry of the seven (CastRange and
    /// CastRange1..6) at or below 1.0 becomes 1.0, so no spell reads a range under 1 (Molten Shield 0 and Overdrive 1 are the ledger's cases).
    /// </summary>
    [Check("spells-cast-range-floor-lifts-entries-of-one-or-less")]
    public static void CastRangeEntriesOfOneOrLessReadAsOne()
    {
        var offenders = GameData.Default.SpellInibinNames.Where(GameData.Default.HasSpell).Where(name =>
        {
            var spell = GameData.Default.Spell(name);
            return spell.CastRange < 1.0 || Enumerable.Range(0, 7).Any(rank => spell.CastRangeAt(rank) < 1.0);
        }).ToArray();
        TestAssert.True(offenders.Length == 0, "spells reading a cast range under 1: " + string.Join(", ", offenders));
        TestAssert.Near(600, GameData.Default.Spell("YorickDecayed", 5).CastRangeAt(1), 0, "an ordinary entry is unchanged");
    }

    [Check("spells-cast-range-accessor-reads-level-entry-and-falls-back")]
    public static void CastRangeAtReadsTheLevelEntryAndFallsBackToTheBase()
    {
        // YorickDecayed.inibin: CastRange 550, CastRange1..5 600, CastRange6 550.
        var decayed = GameData.Default.Spell("YorickDecayed", 5);
        TestAssert.Near(550, decayed.CastRange, 0, "the base entry");
        TestAssert.Near(550, decayed.CastRangeAt(0), 0, "level 0 (unlearned) reads the base entry");
        for (var rank = 1; rank <= 5; rank++) TestAssert.Near(600, decayed.CastRangeAt(rank), 0, $"rank {rank}");
        TestAssert.Near(550, decayed.CastRangeAt(6), 0, "CastRange6 is read too");
        TestAssert.Near(550, decayed.CastRangeAt(7), 0, "a level past the maximum spell level falls back");
        TestAssert.Near(550, decayed.CastRangeAt(-1), 0, "a negative level falls back");
        // VayneCondemn.inibin: CastRange 575, CastRange1..5 575, CastRange6 10000 (unreachable: Condemn stops at rank 5).
        TestAssert.Near(10000, GameData.Default.Spell("VayneCondemn", 5).CastRangeAt(6), 0, "CastRange6 of Condemn");
        // A spell that ships no level entry at all reads its base entry at every rank (HowlingGaleSpell: CastRange 1100 only).
        var gale = GameData.Default.Spell("HowlingGaleSpell", 5);
        for (var rank = 0; rank <= 6; rank++) TestAssert.Near(1100, gale.CastRangeAt(rank), 0, $"no entries: rank {rank}");

        // A rank whose entry is missing reads the base entry, as the spell data loader defaults CastRangeN to CastRange.
        var raw = decayed.Raw.Keys.ToDictionary(key => key, key => decayed.Raw[key]);
        foreach (var rank in new[] { 3, 4, 5 }) raw.Remove(InibinReader.KeyHash($"SpellData*CastRange{rank}"));
        var partial = decayed with { Raw = new InibinData(raw) };
        TestAssert.Near(600, partial.CastRangeAt(1), 0, "rank 1 keeps its entry");
        TestAssert.Near(600, partial.CastRangeAt(2), 0, "rank 2 keeps its entry");
        foreach (var rank in new[] { 3, 4, 5 })
            TestAssert.Near(550, partial.CastRangeAt(rank), 0, $"rank {rank} has no entry and falls back to the base");
    }

    [Check("spells-cast-range-hand-written-ledger")]
    public static void HandWrittenKitsKeepTheShippedPerRankCastRanges()
    {
        // The ledger of docs/champions/spell-geometry.md ("Cast range per rank"): every spell the ten hand-written modules load
        // (slot spells and the children they cast) with the ranks a champion can learn (1..5, R 1..3). A spell whose level
        // entries all equal its base entry is absent from `expected`; a new difference in the data or a changed accessor fails here.
        var spells = new (string Name, int MaxRank)[]
        {
            ("Disintegrate", 5), ("Incinerate", 5), ("MoltenShield", 5), ("InfernalGuardian", 3),
            ("OrianaIzunaCommand", 5), ("OrianaDissonanceCommand", 5), ("OrianaRedactCommand", 5), ("OrianaDetonateCommand", 3),
            ("OrianaIzuna", 5), ("OrianaRedact", 5),
            ("BlindMonkQOne", 5), ("BlindMonkQTwo", 5), ("BlindMonkQTwoDash", 5), ("BlindMonkWOne", 5), ("BlindMonkWTwo", 5),
            ("BlindMonkEOne", 5), ("BlindMonkETwo", 5), ("BlindMonkETwoMissile", 5), ("BlindMonkRKick", 3),
            ("JavelinToss", 5), ("Bushwhack", 5), ("PrimalSurge", 5), ("AspectOfTheCougar", 3),
            ("Takedown", 5), ("Pounce", 5), ("Swipe", 5),
            ("YorickSpectral", 5), ("YorickDecayed", 5), ("YorickRavenous", 5), ("YorickReviveAlly", 3),
            ("YorickSummonDecayed", 5), ("YorickSummonRavenous", 5), ("YorickSummonSpectral", 5),
            ("Deceive", 5), ("JackInTheBox", 5), ("TwoShivPoison", 5), ("HallucinateFull", 3), ("ShacoBoxSpell", 5),
            ("EzrealMysticShot", 5), ("EzrealMysticShotMissile", 5), ("EzrealEssenceFlux", 5), ("EzrealEssenceFluxMissile", 5),
            ("EzrealArcaneShift", 5), ("EzrealArcaneShiftMissile", 5), ("EzrealTrueshotBarrage", 3),
            ("VayneTumble", 5), ("VayneSilveredBolts", 5), ("VayneCondemn", 5), ("VayneCondemnMissile", 5), ("VayneInquisition", 3),
            ("HowlingGale", 5), ("HowlingGaleSpell", 5), ("HowlingGaleSpell1", 5), ("HowlingGaleSpell2", 5),
            ("HowlingGaleSpell3", 5), ("SowTheWind", 5), ("EyeOfTheStorm", 5), ("ReapTheWhirlwind", 3),
            ("RocketGrab", 5), ("RocketGrabMissile", 5), ("Overdrive", 5), ("PowerFist", 5), ("StaticField", 3),
        };
        var expected = new Dictionary<string, (double Base, double PerRank)>
        {
            ["EzrealMysticShotMissile"] = (1100, 1200),
            ["OrianaDissonanceCommand"] = (255, 10000),
            ["OrianaDetonateCommand"] = (410, 10000),
            ["OrianaRedact"] = (1500, 2500),
            ["YorickDecayed"] = (550, 600),
            ["YorickRavenous"] = (650, 550),
            ["YorickSummonDecayed"] = (25000, 650),
            ["YorickSummonRavenous"] = (25000, 900),
            ["YorickSummonSpectral"] = (25000, 900),
        };
        var differing = new Dictionary<string, (double Base, double PerRank)>();
        foreach (var (name, maxRank) in spells)
        {
            var data = GameData.Default.Spell(name, maxRank);
            var perRank = Enumerable.Range(1, maxRank).Select(data.CastRangeAt).Distinct().ToArray();
            if (perRank.Length > 1)
                throw new InvalidOperationException($"{name}: the learnable ranks no longer share one range ({string.Join(", ", perRank)}); update the ledger");
            if (Math.Abs(perRank[0] - data.CastRange) > 1e-9) differing[name] = (data.CastRange, perRank[0]);
        }
        TestAssert.Equal(expected.Keys.Order().ToArray(), differing.Keys.Order().ToArray(),
            "spells whose level entries differ from the base: " + string.Join(", ", differing.Keys.Order()));
        foreach (var (name, (baseRange, perRank)) in expected)
        {
            TestAssert.Near(baseRange, differing[name].Base, 1e-9, name + " base entry");
            TestAssert.Near(perRank, differing[name].PerRank, 1e-9, name + " level entries");
        }
    }

    [Check("spells-cast-range-yorick-admission-uses-the-rank-entry")]
    public static void YorickAdmissionFollowsTheRangeOfTheLearnedRank()
    {
        // YorickDecayed (W, point): base 550, ranks 1-5 600. YorickRavenous (E, unit): base 650, ranks 1-5 550.
        foreach (var (slot, spell, rankRange, baseRange) in new[] { (1, "YorickDecayed", 600.0, 550.0), (2, "YorickRavenous", 550.0, 650.0) })
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = new Match("Yorick", Team.Order);
            var hero = match.Hero(0); var target = match.Hero(1);
            hero.SpellRanks[slot] = rank;
            var data = GameData.Default.Spell(spell, 5);
            TestAssert.Near(rankRange, data.CastRangeAt(rank), 0, $"{spell} rank {rank} entry");
            TestAssert.True(Math.Abs(baseRange - rankRange) > 1, "the scenario separates the base and the level entry");
            // The client's edge: one unit inside is accepted, one unit outside is a range failure.
            foreach (var outside in new[] { false, true })
            {
                target.Position = hero.Position + new Position2(rankRange + (outside ? 1 : -1), 0);
                var result = match.Runtime.Spells.CanCast(hero, slot, target.NetId,
                    new Position3(target.Position.X, 0, target.Position.Z), 0);
                TestAssert.Equal(!outside, result.Allowed, $"{spell} rank {rank} outside={outside}: {result}");
                if (outside) TestAssert.Equal(SpellCastFailure.Range, result.Failure);
            }
            // Between the two entries the old base-entry read decided the other way.
            var between = (rankRange + baseRange) / 2.0;
            target.Position = hero.Position + new Position2(between, 0);
            var inBetween = match.Runtime.Spells.CanCast(hero, slot, target.NetId,
                new Position3(target.Position.X, 0, target.Position.Z), 0);
            TestAssert.Equal(rankRange > baseRange, inBetween.Allowed, $"{spell} rank {rank} at {between}: the level entry decides");
        }
    }

    [Check("spells-cast-range-approach-walks-to-the-rank-entry")]
    public static void CastApproachStopsInsideTheRangeOfTheLearnedRank()
    {
        // Yorick E reaches 550 (base entry 650). An approach that stopped at 650 would be rejected again by CanCast and walk forever.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = new Match("Yorick", team);
            var hero = match.Hero(0); var target = match.Hero(1);
            hero.SpellRanks[2] = rank;
            hero.Position = new Position2(6100, 6100);
            target.Position = new Position2(7000, 7000);
            match.Runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                match.Runtime.World.Allocate(UnitType.Missile), team, target.Position, 600, 10));
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 2, target);
            TestAssert.True(hero.PendingCast is not null && hero.Waypoints.Count > 0, $"{team}/{rank}: E out of range queues an approach");
            var stop = Distance(hero.Waypoints[^1], target.Position);
            TestAssert.True(stop <= 550.0 + 1e-6, $"{team}/{rank}: the route ends inside the rank's 550, not the base 650 ({stop:0.0})");
            // Outside the entry the queued cast keeps waiting, inside it fires.
            hero.Position = target.Position + new Position2(-560, 0);
            match.Runtime.Spells.Tick(.01);
            TestAssert.True(hero.PendingCast is not null, $"{team}/{rank}: 560 units is outside the rank entry");
            match.Clear();
            hero.Position = target.Position + new Position2(-540, 0);
            match.Runtime.Spells.Tick(.01);
            match.Runtime.Delivery.Flush();
            TestAssert.True(hero.PendingCast is null, $"{team}/{rank}: 540 units is inside the rank entry");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD), $"{team}/{rank}: the cast was acknowledged");
        }
    }

    [Check("spells-cast-range-ezreal-q-missile-flies-the-rank-entry")]
    public static void EzrealMysticShotFliesItsChildRangeOfTheCastLevel()
    {
        // EzrealMysticShotMissile.inibin: CastRange 1100, CastRange1..5 1200. The client flies the line missile CastRange[level]
        // from a base-first table (index 0 is CastRange),
        // and the cast packet carries the level as rank - 1, so rank 1 flies the base 1100 and rank 5 flies CastRange4, 1200.
        // Batch 433 restated this check: it expected 1200 at rank 1, the rank read as the table index.
        foreach (var (rank, reach) in new[] { (1, 1100.0), (5, 1200.0) })
        foreach (var distance in new[] { reach + 100.0, reach + 131.0 })
        {
            using var match = new Match("Ezreal", Team.Order, enemyChampion: "Vayne");
            var hero = match.Hero(0); var target = match.Hero(1);
            hero.SpellRanks[0] = rank;
            TestAssert.Near(65, target.CombatRadius, 0);
            // Width 60 + combat radius 65 = 125: a target 100 past the reach is touched, one 131 past it is not.
            target.Position = hero.Position + new Position2(distance, 0);
            SpellStep(match, 0);
            var health = target.Health;
            match.CastGround(0, hero.Position + new Position2(900, 0), 0);
            SpellStep(match, GameData.Default.Spell("EzrealMysticShot", 5).CastTime + .01);
            TestAssert.Equal(1, EzrealFlights.InFlight(match.Runtime.Spells, hero).Length, $"rank {rank}: one missile in the air");
            SpellStep(match, 2);
            TestAssert.Equal(distance < reach + 125.0, target.Health < health, $"rank {rank}, target at {distance}");
        }
    }
}
