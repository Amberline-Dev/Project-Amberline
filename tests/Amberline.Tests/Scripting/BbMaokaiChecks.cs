using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (25 October 2026, batch 249): Maokai plays from his shipped scripts, a retail bot. Expected numbers are the literals of the
/// shipped scripts and the spell inibins (the audit fixture's standalone decode; the tooltips of fontconfig_en_US.txt are the second witness:
/// Sap Magic's 7 percent, Vengeful Maelstrom's 30 mana a second). The scenario <c>S-Maokai</c> shows the same facts on a headless match.
/// The harness carries no masteries and no ability power, so the damage of a hit is its literal base (the Shen checks' convention).
/// Written 25 October 2026.
/// </summary>
public static class BbMaokaiChecks
{
    [Check("scripting.bb-maokai-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Maokai");
        foreach (var name in new[]
                 {
                     "CharScriptMaokai", "MaokaiTrunkLine", "MaokaiTrunkLineStun", "MaokaiUnstableGrowth", "MaokaiUnstableGrowthRoot",
                     "MaokaiSapling2", "MaokaiSapling2Boom", "MaokaiSaplingMine", "MaokaiDrain3", "MaokaiDrain3Toggle", "MaokaiDrain3Defense",
                     "MaokaiSapMagicPass", "MaokaiSapMagicMelee", "MaokaiSapMagicHot",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Maokai");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(28, closure.ScriptNames.Count(), "the closure is 28 scripts (his four spells and their helpers, the passive and its variants, the extra spells)");

        var hero = new Entity { ChampionName = "Maokai" };
        TestAssert.Equal(["MaokaiTrunkLine", "MaokaiUnstableGrowth", "MaokaiSapling2", "MaokaiDrain3"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Maokai.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Maokai")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 6300, RetailBot: true }, "playable as a top laner for 6300 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Maokai"), "listed once");
    }

    /// <summary>Arcane Smash (Q): magic damage 70 at rank one in a 275 area and along his trunk, a two-second slow and a half-second stun that knocks the unit away.</summary>
    [Check("scripting.bb-maokai-arcane-smash-hits-stuns-and-slows-the-area")]
    public static void ArcaneSmashHitsStunsAndSlows()
    {
        var match = new BbM3.Match("Maokai");
        // Ezreal, not Orianna: her ghost buff (the ball's own state) refuses the stun, which the check is not about.
        var foe = match.FarFoe;
        foe.Position = new Position2(7150, 7000);
        foe.SpellBlock = 0.0;
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var mana = match.Hero.Mana;
        var start = Distance(match.Hero, foe);
        TestAssert.True(match.Cast(0, null, null, foe.Position), "Q casts at the foe 150 units away");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(6.0, match.Hero.SpellCooldowns[0], 0.05, "rank one's six-second cooldown starts at the cast frame");
        TestAssert.Near(55.0, mana - match.Hero.Mana, 1.5, "rank one costs 55 mana");

        var stunned = Appears(match, foe, "MaokaiTrunkLineStun", 1.0);
        TestAssert.True(!double.IsNaN(stunned), $"the knock-away stun lands (foe buffs {Names(foe)}, foe hits {Hits(match)})");
        TestAssert.True(!double.IsNaN(Appears(match, foe, "Slow", 1.0)), "the slow lands");
        TestAssert.Near(0.5, Vanishes(match, foe, "MaokaiTrunkLineStun", 2.0) - stunned, 0.15, "the stun lasts half a second");
        match.Advance(1.0);
        // Batch 252 (open item c of batch 249): MaokaiTrunkLineStun's BBMoveAway asks for the body gap + 225 at speed 200 with IdealDistance 100, and the
        // engine reads IdealDistance as the cap of the way (BbNativeHandlers.MoveAway, W26): 100 units in the 0.5 s of the stun. The same call shape is
        // Riven's RivenKnockback and Rammus's PowerballStun; the League wiki gives Riven's third Broken Wings "75 units over 0.5 seconds" and
        // Powerball 125 units (today's values), the order of the cap and not of 225, so the 99 units batch 249 measured is the script as read.
        TestAssert.Near(start + 100.0, Distance(match.Hero, foe), 6.0, $"the stun knocks the foe 100 units away from him (moved {Distance(match.Hero, foe) - start:0.#})");
        var hits = HitsOn(match, "FarFoe");
        TestAssert.True(hits.Count >= 1 && hits[0].Dealt >= 69.5 && hits[0].Dealt <= 70.0 + match.Hero.AttackDamage + 1.0,
            $"the first hit is the 70 magic base at rank one (dealt {(hits.Count > 0 ? hits[0].Dealt : -1)})");
    }

    /// <summary>Twisted Advance (W): a dash to the target that hits for 80 magic at rank one and roots it for one second.</summary>
    [Check("scripting.bb-maokai-twisted-advance-dashes-to-the-target-and-roots-it")]
    public static void TwistedAdvanceDashesAndRoots()
    {
        var match = new BbM3.Match("Maokai");
        var foe = match.Foe;
        foe.SpellBlock = 0.0;
        foe.Position = new Position2(7300, 7000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var mana = match.Hero.Mana;
        var pressed = match.Now;
        TestAssert.True(match.Cast(1, foe), "W casts at the foe 300 units away");
        TestAssert.True(!double.IsNaN(Appears(match, match.Hero, "MaokaiUnstableGrowth", 0.5)), "the dash buff holds him while he travels");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(13.0, match.Hero.SpellCooldowns[1] + (match.Now - pressed) - match.CastFrameOf(1), 0.05,
            "rank one's 13-second cooldown starts at the cast frame");
        TestAssert.Near(75.0, mana - match.Hero.Mana, 1.5, "rank one costs 75 mana");

        var deadline = match.Now + 2.0;
        // The dash ends where the two bodies touch (their radii add up to about 113 units), which is the arrival of MoveToUnit.
        while (match.Now < deadline && Distance(match.Hero, foe) > 130.0) match.Advance(0.05);
        TestAssert.True(Distance(match.Hero, foe) <= 130.0,
            $"the dash reaches the foe (distance {Distance(match.Hero, foe):0.#}, hero buffs {Names(match.Hero)}, hero {match.Hero.Position.X:0},{match.Hero.Position.Z:0})");
        var root = Appears(match, foe, "MaokaiUnstableGrowthRoot", 0.5);
        TestAssert.True(!double.IsNaN(root), "the foe is rooted on arrival");
        TestAssert.Near(1.0, Vanishes(match, foe, "MaokaiUnstableGrowthRoot", 3.0) - root, 0.2, "rank one roots for one second");
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - 80.0) < 0.5), "the dash hits for 80 magic at rank one");
    }

    /// <summary>Sapling Toss (E): a placemarker that bursts for 40 magic in 240 at rank one and leaves a sapling that springs at the foe for 80 magic.</summary>
    [Check("scripting.bb-maokai-sapling-toss-spawns-a-sapling-that-jumps-and-detonates")]
    public static void SaplingTossPlantsAndDetonates()
    {
        var match = new BbM3.Match("Maokai");
        var foe = match.Foe;
        foe.SpellBlock = 0.0;
        foe.Position = new Position2(7790, 7000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, null, null, new Position2(7600, 7000)), "E casts at the point 600 units away");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(2));
        TestAssert.Near(12.0, match.Hero.SpellCooldowns[2], 0.05, "rank one's 12-second cooldown starts at the cast frame");
        TestAssert.Near(70.0, mana - match.Hero.Mana, 1.5, "rank one costs 70 mana");
        // The placemarker carries the sapling's buff for 1.5 seconds from the spawn (about 0.4 seconds after the cast) and then expires.
        match.Advance(0.6 - match.CastFrameOf(2));
        var placemarker = match.Runtime.World.Entities.Values.FirstOrDefault(unit => Buff(unit, "MaokaiSapling2") is not null);
        TestAssert.True(placemarker is not null, "a placemarker carries the sapling's buff after the cast");
        var seen = match.Now;
        var gone = Vanishes(match, placemarker!, "MaokaiSapling2", 3.0);
        TestAssert.True(gone - seen > 0.9 && gone - seen <= 1.55, $"the placemarker expires with its 1.5-second buff (lived {gone - seen:0.00} more seconds)");
        // Batch 252 (open item a of batch 249): the Boom child (BBSpellCast, homing on the invulnerable placemarker) lands. Its TargetExecute
        // bursts DamageAmount (40 at rank one) in 240 around the placemarker and spawns a MaokaiSproutling with MaokaiSaplingMine (35 s); the
        // mine springs at the foe within 550 and detonates MineDamageAmount (80 at rank one) in 275.
        var burst = HitsOn(match, "Foe");
        TestAssert.True(burst.Count >= 1 && Math.Abs(burst[0].Dealt - 40.0) < 0.5, $"the burst hits the foe for 40 magic at rank one (foe hits {Hits(match, "Foe")})");
        TestAssert.True(match.Runtime.World.Entities.Values.Any(unit => unit.Name == "MaokaiSproutling"), "the burst leaves a sproutling");
        match.Advance(2.0);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - 80.0) < 0.5), $"the sapling springs and detonates for 80 magic at rank one (foe hits {Hits(match, "Foe")})");
    }

    /// <summary>
    /// Batch 331: <c>MissileFixedTravelTime</c>. The client's homing missile flies the
    /// distance left over the time left, so MaokaiSapling2Boom (MissileSpeed 1500, MissileFixedTravelTime 0.85) lands 0.85 s after it leaves
    /// wherever the placemarker is: a toss 600 away and one 300 away burst at the same time after the cast (at 1500 a second, 0.2 s apart).
    /// </summary>
    [Check("scripting.bb-maokai-sapling-boom-lands-after-its-fixed-travel-time")]
    public static void SaplingBoomLandsAfterItsFixedTravelTime()
    {
        double BurstAfterCast(double distance)
        {
            using var match = new BbM3.Match("Maokai");
            match.Foe.SpellBlock = 0.0;
            match.Foe.Position = new Position2(7000 + distance, 7000);
            match.Learn(1);
            match.Refill();
            match.Advance(0.5);
            var cast = match.Now;
            TestAssert.True(match.Cast(2, null, null, new Position2(7000 + distance, 7000)), $"E casts {distance} away");
            match.Advance(2.5);
            var burst = HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Dealt - 40.0) < 0.5).Select(hit => hit.At).DefaultIfEmpty(double.NaN).First();
            TestAssert.True(!double.IsNaN(burst), $"the burst of a toss {distance} away hits the foe (foe hits {Hits(match)})");
            return burst - cast;
        }
        var far = BurstAfterCast(600.0);
        var near = BurstAfterCast(300.0);
        TestAssert.Near(far, near, 0.05, $"the Boom lands its fixed 0.85 s after it leaves at any distance (600 away {far:0.000} s, 300 away {near:0.000} s)");
        TestAssert.True(far >= 0.85, $"the burst comes at least 0.85 s after the cast ({far:0.000} s)");
    }

    /// <summary>
    /// Batch 428 (the owner, native, 28 October 2026: "when it detects target nearby it jumps in place a few times and explodes"). The sproutling
    /// springs at a unit that can see it within 550 and, from its second spring, issues AI_MOVETO to that unit every quarter second; it detonates
    /// MineDamageAmount (80 at rank one) in 275 once a unit that can see it stands within 200 (MaokaiSaplingMine, BuffOnUpdateActions). The foe here
    /// stands 300 units from the landed sapling: inside the 550 detection, outside the 275 blast (and beyond its 200 proximity stop). The sproutling must walk to him, not stand still.
    /// </summary>
    [Check("scripting.bb-maokai-sapling-walks-to-the-foe-it-detects-and-detonates")]
    public static void SaplingWalksToTheFoeItDetects()
    {
        var match = new BbM3.Match("Maokai");
        var foe = match.Foe;
        foe.SpellBlock = 0.0;
        foe.Position = new Position2(7900, 7000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, null, null, new Position2(7600, 7000)), "E casts at the point 600 units away");
        var deadline = match.Now + 4.0;
        Entity? found = null;
        while (match.Now < deadline && found is null)
        {
            match.Advance(0.05);
            found = match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "MaokaiSproutling");
        }
        var sproutling = found ?? throw new InvalidOperationException("the flight lands no sproutling within 4 seconds");
        var landing = new Position2(sproutling.Position.X, sproutling.Position.Z);
        var gap = Distance(sproutling, foe);
        var blast = deadline + 4.0;
        while (match.Now < blast && !HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - 80.0) < 0.5)) match.Advance(0.05);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - 80.0) < 0.5),
            $"the sproutling detonates for 80 magic on the foe it detected 300 units away (foe hits {Hits(match, "Foe")}; sproutling at {sproutling.Position.X:0} {sproutling.Position.Z:0}, waypoints {sproutling.Waypoints.Count}, buffs {string.Join(",", sproutling.Buffs.Select(buff => buff.Name))}, foe at {foe.Position.X:0} {foe.Position.Z:0}, foe alive {foe.Alive}, now {match.Now:0.00})");
        TestAssert.True(Distance(sproutling, foe) <= 275.0,
            $"it walks within its 275 blast of him (landed {gap:0} units away, now {Distance(sproutling, foe):0} units away)");
        TestAssert.True(Math.Sqrt(Math.Pow(sproutling.Position.X - landing.X, 2) + Math.Pow(sproutling.Position.Z - landing.Z, 2)) > 100.0,
            "the sproutling moved toward the foe it detected");
    }

    /// <summary>
    /// Batch 428 (the owner: "a disconnect between the animation of throwing and the plant actually starting flying"). E's cast frame is its windup,
    /// 0.375 s (MaokaiSapling2: DelayCastOffsetPercent -0.25 on the 0.5 s delay), and the placemarker and the child cast come at that frame. The child
    /// MaokaiSapling2Boom is a homing cast with no ForceCastingOrChannelling, so the flight leaves at the cast frame (as a line child of a non-instant
    /// parent does) and lands its fixed 0.85 s later. The child's own 0.25 s cast time is not a second windup; the sproutling lands at cast frame + 0.85.
    /// </summary>
    [Check("scripting.bb-maokai-sapling-flight-leaves-at-the-cast-frame")]
    public static void SaplingFlightLeavesAtTheCastFrame()
    {
        using var match = new BbM3.Match("Maokai");
        match.Foe.SpellBlock = 0.0;
        match.Foe.Position = new Position2(7000, 9000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var cast = match.Now;
        TestAssert.True(match.Cast(2, null, null, new Position2(7600, 7000)), "E casts at the point 600 units away");
        var deadline = cast + 4.0;
        var landed = double.NaN;
        while (match.Now < deadline && double.IsNaN(landed))
        {
            match.Advance(0.05);
            if (match.Runtime.World.Entities.Values.Any(unit => unit.Name == "MaokaiSproutling")) landed = match.Now;
        }
        TestAssert.True(!double.IsNaN(landed), "the flight lands a sproutling");
        var expected = match.CastFrameOf(2) + 0.85;
        TestAssert.Near(expected, landed - cast, 0.08,
            $"the flight leaves at the cast frame and lands its fixed 0.85 s later (cast frame {match.CastFrameOf(2):0.000} s, landed {landed - cast:0.000} s after the cast)");
    }

    /// <summary>
    /// Vengeful Maelstrom (R): a toggled zone that takes 20 percent of the damage an ally receives inside it and stores that 20 percent; the
    /// release deals 100 magic plus twice the stored damage (at most 200), 40-second cooldown from the release, 30 mana a second while it holds.
    /// </summary>
    [Check("scripting.bb-maokai-vengeful-maelstrom-absorbs-ally-damage-and-detonates-on-toggle-off")]
    public static void VengefulMaelstromAbsorbsAndDetonates()
    {
        var match = new BbM3.Match("Maokai");
        var ally = match.Ally;
        var foe = match.Foe;
        ally.Armor = 0.0;
        foe.SpellBlock = 0.0;
        foe.Position = new Position2(7300, 7000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, null, null, new Position2(7000, 7000)), "R switches the zone on at his feet");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(75.0, mana - match.Hero.Mana, 2.0, "rank one costs 75 mana to switch on");
        TestAssert.True(!double.IsNaN(Appears(match, match.Hero, "MaokaiDrain3", 0.5)), "the zone is up on him");

        var before = ally.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(foe, ally), "the foe swings at the ally inside the zone");
        match.Advance(1.5);
        // The ally loses 80 percent of the hit; the zone keeps the other 20 percent, so the stored amount is a quarter of the loss.
        var loss = before - ally.Health;
        TestAssert.True(loss > 0.0, $"the swing lands on the ally (ally hits {Hits(match, "Ally")})");
        TestAssert.True(Buff(match.Hero, "MaokaiDrain3Tally") is not null,
            $"the absorbed damage is stored on him (hero buffs {Names(match.Hero)}, ally buffs {Names(ally)})");

        TestAssert.True(match.Cast(3, null, null, new Position2(7000, 7000)), "the second press releases the zone");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "MaokaiDrain3") is null, "the zone ends with the second press");
        TestAssert.Near(40.0, match.Hero.SpellCooldowns[3], 1.0, "the release starts the 40-second cooldown");
        var released = HitsOn(match, "Foe").LastOrDefault();
        // Batch 252 (open item b of batch 249): the stored 20 percent reaches the release. MaokaiDrain3Defense stores DamageAmount x (1 - 0.8) per hit
        // in MaokaiDrain3Tally, whose OnBuffActivate sums it into Maokai's CharVars.Tally; MaokaiDrain3's deactivation deals BaseDamage (100 at rank
        // one) + min(BonusCap 200, 2 x Tally). The ally kept 80 percent of each hit, so the stored amount is a quarter of his loss.
        var stored = loss / 4.0;
        TestAssert.Near(100.0 + Math.Min(200.0, 2.0 * stored), released.Dealt, 1.0,
            $"the release deals 100 plus twice the stored damage (stored {stored:0.##}, dealt {released.Dealt:0.##}, foe hits {Hits(match)})");
    }

    /// <summary>
    /// Sap Magic (passive): every spell cast near him adds a charge; the fifth arms his next basic attack, which heals him for 7 percent of his
    /// maximum health (5000 in the harness) and spends the charge.
    /// </summary>
    [Check("scripting.bb-maokai-sap-magic-heals-after-five-spells-near-him")]
    public static void SapMagicHealsAfterFiveSpellsNearHim()
    {
        var match = new BbM3.Match("Maokai");
        var ally = match.Ally;
        var foe = match.Foe;
        foe.Position = new Position2(7000, 7100);
        match.Advance(1.0);
        for (var slot = 0; slot < 4; slot++) ally.SpellRanks[slot] = 1;
        match.Hero.Health = 4000.0;
        for (var cast = 1; cast <= 4; cast++)
        {
            ally.SpellCooldowns[0] = 0.0;
            ally.Mana = ally.MaxMana;
            TestAssert.True(match.Cast(0, foe, ally), $"the ally's spell {cast} casts near him");
            match.Advance(0.3);
        }
        TestAssert.True(Buff(match.Hero, "MaokaiSapMagicMelee") is null, "four spells near him leave the basic attack unarmed");
        ally.SpellCooldowns[0] = 0.0;
        ally.Mana = ally.MaxMana;
        TestAssert.True(match.Cast(0, foe, ally), "the fifth spell casts");
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "MaokaiSapMagicMelee") is not null, "five spells near him arm his next basic attack");

        var before = match.Hero.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, foe), "Maokai swings at the foe");
        match.Advance(1.0);
        TestAssert.Near(350.0, match.Hero.Health - before, 3.0, "the armed swing heals 7 percent of his 5000 maximum health");
        TestAssert.True(Buff(match.Hero, "MaokaiSapMagicMelee") is null, "the swing spends the charge");
    }

    /// <summary>Sap Magic's passive has PersistsThroughDeath: a death keeps it on him.</summary>
    [Check("scripting.bb-maokai-sap-magic-persists-through-death")]
    public static void SapMagicPassiveSurvivesADeath()
    {
        var match = new BbM3.Match("Maokai");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "MaokaiSapMagicPass") is not null, "the passive is on him from the start");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(!match.Hero.Alive, "the kill lands");
        TestAssert.True(Buff(match.Hero, "MaokaiSapMagicPass") is not null, "PersistsThroughDeath keeps the passive on him");
    }

    private static string Hits(BbM3.Match match, string victim = "Foe") =>
        string.Join(";", HitsOn(match, victim).Select(hit => $"{hit.At:0.00}s {hit.Dealt:0.##} dealt, raw {hit.Raw:0.##}, {hit.Kind}"));

    private static double Distance(Entity a, Entity b)
    {
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double Appears(BbM3.Match match, Entity unit, string name, double window)
    {
        var deadline = match.Now + window;
        while (match.Now < deadline && BbM3.Buff(unit, name) is null) match.Advance(0.05);
        return BbM3.Buff(unit, name) is null ? double.NaN : match.Now;
    }

    // The match clock when a buff is first seen gone (NaN if it stays past the window).
    private static double Vanishes(BbM3.Match match, Entity unit, string name, double window)
    {
        var deadline = match.Now + window;
        while (match.Now < deadline && BbM3.Buff(unit, name) is not null) match.Advance(0.05);
        return BbM3.Buff(unit, name) is null ? match.Now : double.NaN;
    }
}
