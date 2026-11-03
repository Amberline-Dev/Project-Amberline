using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (26 October 2026): Tristana plays from her shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the literals of
/// the shipped scripts and spell inibins (Q 20 s, W 22/20/18/16/14 s, E 16 s, R 60 s; the ranks 5/5/5/3); the scenario <c>S-tristana</c> shows the same facts
/// on a headless match. Resistances are read at the moment of the hit, so the numbers hold whatever the stats refresh did before it.
/// </summary>
public static class BbTristanaChecks
{
    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static void Advance(Match match, double seconds) => match.Advance(seconds);

    // The damage a named unit took from one spell: the committed hits of that raw amount, after mitigation.
    private static double DealtOf(Match match, string victim, double raw) =>
        HitsOn(match, victim).Where(hit => Math.Abs(hit.Raw - raw) < 0.01).Sum(hit => hit.Dealt);

    // The committed hits on a named unit, for a failure message: time, dealt, raw and category.
    private static string Hits(Match match, string victim) =>
        string.Join("; ", HitsOn(match, victim).Select(hit => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hit.At:0.00}s dealt {hit.Dealt:0.0} raw {hit.Raw:0.0} {hit.Kind}")));

    // A point at a distance due east (along +X) of an origin.
    private static Position2 East(Position2 origin, double distance) => new(origin.X + distance, origin.Z);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-tristana-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Tristana");
        TestAssert.True(module.ScriptNames.Count() >= 10, "the module reads its closure");
        foreach (var name in new[] { "CharScriptTristana", "RapidFire", "RocketJump", "DetonatingShot", "BusterShot", "DrawABead", "GrievousWound", "ExplosiveShotDebuff", "RocketJumpSlow", "DetonatingShot_Target" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Tristana");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(20, closure.ScriptNames.Count(), "the closure is 20 scripts");
        // The passive's inibin name has a space ("Draw a Bead (Passive)"); the resolver names it "Draw a Bead". The character script adds the script
        // DrawABead, as it does for Zilean's Heightened Learning.
        var dangling = string.Join(",", closure.Dangling.Select(item => item.Name));
        TestAssert.Equal("Draw a Bead", dangling, "one dangling name, the passive's inibin name");

        var hero = new Entity { ChampionName = "Tristana" };
        TestAssert.Equal(["RapidFire", "RocketJump", "DetonatingShot", "BusterShot"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Tristana.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Tristana")!;
        TestAssert.True(info is { Playable: true, Role: "adc", DefaultIp: 1350, Id: 18, RetailBot: false }, "playable as an adc for 1350 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Tristana"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Draw a Bead, Rapid Fire
    // ------------------------------------------------------------------------------------------------------------

    // CharVars BonusRange by level (SetVarsByLevel): nine for every level above one, through IncFlatAttackRangeMod.
    [Check("scripting.bb-tristana-draw-a-bead-widens-the-attack-range-by-nine-a-level")]
    public static void AttackRangeGrowsNineAPerLevel()
    {
        using var match = new Match("Tristana");
        TestAssert.True(Buff(match.Hero, "DrawABead") is not null, "the passive is on from the start");
        foreach (var level in new[] { 1, 6, 11, 18 })
        {
            // The level goes up the way the scenario's Level does: experience to the threshold of the level.
            var required = ProgressionRules.ExperienceThreshold(level) - match.Hero.Experience;
            if (required > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: required);
            Advance(match, 1.0);
            TestAssert.Near(550.0 + 9.0 * (level - 1), match.Hero.AttackRange, 1e-6, $"the attack range at level {level}");
        }
    }

    [Check("scripting.bb-tristana-rapid-fire-adds-ninety-percent-attack-speed-for-seven-seconds")]
    public static void RapidFireIsAnAttackSpeedBuffOfSevenSeconds()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var before = match.Hero.AttackSpeedMod;
        TestAssert.True(match.Cast(0, match.Hero), "Rapid Fire is accepted on herself");
        Advance(match, 0.5);
        TestAssert.Near(before + 0.9, match.Hero.AttackSpeedMod, 1e-6, "rank five adds 0.9 to the attack speed modifier");
        TestAssert.True(Buff(match.Hero, "RapidFire") is not null, "the buff is on");
        Advance(match, 6.0);
        TestAssert.True(Buff(match.Hero, "RapidFire") is not null, "still on six and a half seconds after the cast");
        Advance(match, 1.0);
        TestAssert.True(Buff(match.Hero, "RapidFire") is null, "gone after its seven seconds");
        TestAssert.Near(before, match.Hero.AttackSpeedMod, 1e-6, "the speed comes back");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Rocket Jump
    // ------------------------------------------------------------------------------------------------------------

    // SelfExecute moves her toward the point: the distance is the aim's, up to 900 (the facing offset clamp in the script).
    [Check("scripting.bb-tristana-rocket-jump-lands-at-the-point-and-stops-at-nine-hundred")]
    public static void JumpLandsOnThePointUpToNineHundred()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var start = match.Hero.Position;
        TestAssert.True(match.Cast(1, at: East(start, 700.0)), "Rocket Jump is accepted toward a point 700 away");
        Advance(match, 1.5);
        TestAssert.Near(700.0, Distance(start, match.Hero.Position), 3.0, "the jump lands on the point");

        // A second hero, so the first jump's landing and its movement anchor cannot cut the second one short.
        using var far = new Match("Tristana");
        far.Learn(5);
        far.Refill();
        Advance(far, 0.5);
        var farStart = far.Hero.Position;
        TestAssert.True(far.Cast(1, at: East(farStart, -1200.0)), "a point 1200 away is accepted too");
        Advance(far, 1.5);
        TestAssert.Near(900.0, Distance(farStart, far.Hero.Position), 3.0, "the jump stops at 900; landed at " + far.Hero.Position.X.ToString("0.0") + ", " + far.Hero.Position.Z.ToString("0.0"));
    }

    // The landing hits what stands within 300 for 70 to 250 magic (rank five: 250, and 0.8 of the ability power, none here) and slows it for 2.5 seconds.
    [Check("scripting.bb-tristana-rocket-jump-hits-and-slows-the-landing-zone")]
    public static void JumpHitsAndSlowsWhereSheLands()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var start = match.Hero.Position;
        var landing = East(start, 700.0);
        match.Foe.Position = new Position2(landing.X, landing.Z + 100.0);
        var resist = match.Foe.SpellBlock;
        TestAssert.True(match.Cast(1, at: landing), "Rocket Jump is accepted");
        Advance(match, 1.5);
        TestAssert.Near(Mitigated(250.0, resist), DealtOf(match, "Foe", 250.0), 1.0, "250 magic at rank five, through the foe's resistance; hits " + Hits(match, "Foe"));
        TestAssert.True(Buff(match.Foe, "RocketJumpSlow") is not null, "the landing slows the foe");
        // The slow lasts 2.5 seconds from the landing (about 0.9 s after the cast): gone 3.5 seconds after the cast.
        Advance(match, 2.0);
        TestAssert.True(Buff(match.Foe, "RocketJumpSlow") is null, "the slow is over 2.5 seconds after the landing");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Explosive Shot
    // ------------------------------------------------------------------------------------------------------------

    // ExplosiveShotDebuff: 46 magic a second at rank five (0.2 of the ability power), five seconds; GrievousWound halves the healing it is given.
    [Check("scripting.bb-tristana-explosive-shot-burns-for-five-seconds-and-halves-healing")]
    public static void ExplosiveShotBurnsAndCutsHealing()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var resist = match.Foe.SpellBlock;
        TestAssert.True(match.Cast(2, match.Foe), "Explosive Shot is accepted on a foe");
        Advance(match, 0.5);
        TestAssert.True(Buff(match.Foe, "ExplosiveShotDebuff") is not null && Buff(match.Foe, "GrievousWound") is not null, "the burn and the wound are on");
        TestAssert.Near(0.5, match.Runtime.Combat.HealingReceivedMultiplier(match.Foe), 1e-6, "healing received is halved while the wound lasts");
        Advance(match, 5.0);
        // One tick may fall on either side of the five-second mark: a tick is 46 times the resistance factor.
        var perTick = Mitigated(46.0, resist);
        TestAssert.Near(5.0 * perTick, DealtOf(match, "Foe", 46.0), perTick + 0.5, "five ticks of 46 magic; hits " + Hits(match, "Foe"));
        TestAssert.True(Buff(match.Foe, "GrievousWound") is null, "the wound ends with the burn");
        TestAssert.Near(1.0, match.Runtime.Combat.HealingReceivedMultiplier(match.Foe), 1e-6, "healing is whole again");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Draw a Bead on kills and assists (the cooldown of Rocket Jump), Detonating Shot on a kill (the passive)
    // ------------------------------------------------------------------------------------------------------------

    // BuffOnKill and BuffOnAssist of Draw a Bead (a hero target) set the Rocket Jump cooldown (slot 1) to zero.
    [Check("scripting.bb-tristana-kill-or-assist-on-a-hero-resets-rocket-jump")]
    public static void KillAndAssistResetRocketJump()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        TestAssert.True(match.Cast(1, at: East(match.Hero.Position, 300.0)), "Rocket Jump starts its cooldown");
        Advance(match, 1.5);
        TestAssert.True(match.Hero.SpellCooldowns[1] > 10.0, "the jump is on cooldown");
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        TestAssert.Equal(0.0, match.Hero.SpellCooldowns[1], "a kill of a hero resets the jump");

        match.Refill();
        Advance(match, 0.5);
        TestAssert.True(match.Cast(1, at: East(match.Hero.Position, 300.0)), "Rocket Jump starts its cooldown again");
        Advance(match, 1.5);
        match.FarFoe.Position = East(match.Hero.Position, 200.0);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.FarFoe), "Tristana hits the far foe first");
        Advance(match, 1.5);
        match.FarFoe.Health = 1.0;
        match.Runtime.Combat.Kill(match.FarFoe, match.Ally.NetId);
        TestAssert.Equal(0.0, match.Hero.SpellCooldowns[1], "an assist on a hero resets the jump");
    }

    // The explosion: a basic attack marks the target (BuffOnHitUnit, DetonatingShot_Target, 0.1 s) and the hit kills it; BuffOnKill of the passive blasts
    // 50 to 150 magic at rank five (0.25 of the ability power) within 300 of the victim.
    [Check("scripting.bb-tristana-detonating-shot-explodes-a-marked-kill")]
    public static void AMarkedKillExplodes()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        TestAssert.True(Buff(match.Hero, "DetonatingShot") is not null, "the passive aura is on once E is learnt");
        var victim = match.Foe;
        var bystander = match.FarFoe;
        bystander.Position = new Position2(victim.Position.X, victim.Position.Z + 150.0);
        var resist = bystander.SpellBlock;
        victim.Health = 1.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, victim), "the basic attack starts");
        Advance(match, 1.5);
        TestAssert.True(!victim.Alive, "the attack kills the marked foe");
        TestAssert.Near(Mitigated(150.0, resist), DealtOf(match, "FarFoe", 150.0), 1.0, "the blast: 150 magic at rank five, on the foe 150 away; hits " + Hits(match, "FarFoe"));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Buster Shot
    // ------------------------------------------------------------------------------------------------------------

    // A missile of 1600 (600 units: 0.375 s) after the 0.25 s cast; the target and everything within 175 of it are thrown 600, 800 or 1000 beyond
    // the distance to Tristana and stunned for half a second; the target takes 300, 400 or 500 magic (1.5 ability power).
    [Check("scripting.bb-tristana-buster-shot-knocks-the-area-back-and-stuns")]
    public static void BusterShotThrowsTheAreaAndStuns()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var start = match.Hero.Position;
        // The knock runs until terrain stops it, so the check takes the first of eight directions with 1700 clear of walls (1600 is the throw), and
        // puts the neighbour 160 to the side of the target: a neighbour in front of it would block the knock (their radii touch at 130).
        var angle = Enumerable.Range(0, 8).Select(index => index * Math.PI / 4.0).First(candidate =>
        {
            var side = Along(start, candidate, 600.0, 160.0, candidate + Math.PI / 2.0);
            return Enumerable.Range(1, 34).All(step => match.Runtime.Navigation.IsMovementWalkable(Along(start, candidate, step * 50.0)))
                && match.Runtime.Navigation.IsMovementWalkable(side);
        });
        var perpendicular = angle + Math.PI / 2.0;
        var target = match.Foe;
        var neighbour = match.FarFoe;
        target.Position = Along(start, angle, 600.0);
        neighbour.Position = Along(start, angle, 600.0, 160.0, perpendicular);
        TestAssert.True(match.Cast(3, target), "Buster Shot is accepted at 600");
        Advance(match, 0.75);
        TestAssert.True(Buff(target, "BusterShot") is not null, "the knock and stun are on the target at impact");
        TestAssert.True(Buff(neighbour, "BusterShot") is not null, "and on the unit 160 to its side");
        // The resistance is read after the knock buff is added: a stat refresh of the add may move it a little.
        var resist = target.SpellBlock;
        Advance(match, 1.5);
        TestAssert.Near(Mitigated(500.0, resist), DealtOf(match, "Foe", 500.0), 1.0, "500 magic at rank three, through the resistance after the knock; hits " + Hits(match, "Foe"));
        TestAssert.Near(600.0 + 1000.0, Distance(start, target.Position), 15.0, "the target is thrown 1000 beyond 600");
        TestAssert.Near(Math.Sqrt(600.0 * 600.0 + 160.0 * 160.0) + 1000.0, Distance(start, neighbour.Position), 15.0, "the neighbour is thrown 1000 beyond its place");
        TestAssert.True(Buff(target, "BusterShot") is null, "the stun is over");
    }

    // A point at distance along a direction, or at a side offset of that point.
    private static Position2 Along(Position2 origin, double angle, double distance, double side = 0.0, double sideAngle = 0.0) =>
        new(origin.X + distance * Math.Cos(angle) + side * Math.Cos(sideAngle), origin.Z + distance * Math.Sin(angle) + side * Math.Sin(sideAngle));

    // AdjustCastInfo: with Draw a Bead, the reach is 600 plus 90 for every level of Rocket Jump (slot 1); without it, the inibin's 600 applies.
    [Check("scripting.bb-tristana-buster-shot-reach-grows-with-rocket-jump-under-draw-a-bead")]
    public static void BusterShotReachFollowsRocketJump()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        match.Refill();
        Advance(match, 0.5);
        var target = match.Foe;
        target.Position = East(match.Hero.Position, 800.0);
        match.Hero.SpellRanks[1] = 0;
        TestAssert.True(match.CanCast(3, target).Failure == SpellCastFailure.Range, "at Rocket Jump level zero the reach is 600");
        TestAssert.Near(600.0, match.Module.CastRange(match.Hero, 3)!.Value, 1e-6, "the adjusted reach at Rocket Jump level zero");
        match.Hero.SpellRanks[1] = 5;
        TestAssert.True(match.CanCast(3, target).Failure == SpellCastFailure.None, "at Rocket Jump level five the target at 800 is reachable");
        // 1050 at rank five. Beyond about 1000 the foe leaves Tristana's sight in this rig, so the number is read from the adjustment itself.
        TestAssert.Near(1050.0, match.Module.CastRange(match.Hero, 3)!.Value, 1e-6, "the adjusted reach at Rocket Jump level five: 600 plus 90 a level");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death and respawn
    // ------------------------------------------------------------------------------------------------------------

    // Draw a Bead and the Detonating Shot aura say PersistsThroughDeath: they stay on her through a death.
    [Check("scripting.bb-tristana-passives-persist-through-death")]
    public static void PassivesSurviveADeath()
    {
        using var match = new Match("Tristana");
        match.Learn(5);
        Advance(match, 0.5);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(!match.Hero.Alive, "Tristana is dead");
        TestAssert.True(Buff(match.Hero, "DrawABead") is not null, "Draw a Bead persists");
        TestAssert.True(Buff(match.Hero, "DetonatingShot") is not null, "the Detonating Shot aura persists");
    }
}
