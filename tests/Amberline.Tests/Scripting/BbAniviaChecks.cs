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
/// Batch 330 (26 October 2026, wave 7): Anivia plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module).
/// Expected numbers are the literals of the shipped scripts (CharScriptAnivia, FlashFrost and FlashFrostSpell, Crystallize and CrystallizePush,
/// Frostbite, GlacialStorm, Rebirth, RebirthReady, RebirthMarker, RebirthCooldown, Chilled), read from the bytecode listings, and the rank-one figures of her
/// inibins and the egg's. Where the tooltip and the script differ, the script is the expected side and the batch 330 record names the difference.
/// </summary>
public static class BbAniviaChecks
{
    private static readonly double[] FlashFrostDamage = [60.0, 90.0, 120.0, 150.0, 180.0];
    private static readonly double[] FrostbiteBase = [55.0, 85.0, 115.0, 145.0, 175.0];
    private static readonly double[] StormDamage = [40.0, 60.0, 80.0];
    private static readonly double[] StormMana = [20.0, 25.0, 30.0];
    private static readonly double[] WallWidth = [400.0, 500.0, 600.0, 700.0, 800.0];
    private static readonly double[] WallCount = [4.0, 5.0, 6.0, 7.0, 8.0];
    private static readonly double[] EggArmour = [-40.0, -40.0, -40.0, -40.0, -25.0, -25.0, -25.0, -10.0, -10.0, -10.0, -10.0,
        5.0, 5.0, 5.0, 20.0, 20.0, 20.0, 20.0];

    /// <summary>Anivia at level six with every slot at <paramref name="rank"/> (the ultimate at three at most), the foe 250 away with 30 magic resist.</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("Anivia");
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "Anivia reaches level six (the experience of the seat helper)");
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"Anivia's slot {slot} is trained at level six");
        }
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        match.Advance(0.5);
        // The harness sets 5000 health; the first stat refresh clamps it to the real maximum. Settle that here.
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

    private static BuffState? Find(Entity unit, string name) => unit.Buffs.FirstOrDefault(buff => buff.Name == name);

    /// <summary>Seconds left on a buff (negative when it is not on the unit). <see cref="BuffState.ExpiresAt"/> is the countdown the buff runtime ticks down.</summary>
    private static double Left(Match match, Entity unit, string name) => Find(unit, name) is { } buff ? buff.ExpiresAt : -1.0;

    /// <summary>The pieces of the wall: the ice blocks of the match, alive, in net id order.</summary>
    private static List<Entity> Pieces(Match match) =>
        match.Runtime.World.Entities.Values.Where(unit => unit.ChampionName == "AniviaIceblock" && unit.Alive).OrderBy(unit => unit.NetId).ToList();

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, the rank tables
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Anivia");
        foreach (var name in new[] { "CharScriptAnivia", "FlashFrost", "FlashFrostSpell", "Crystallize", "CrystallizePush", "Frostbite", "GlacialStorm",
                     "GlacialStormSpell", "Chilled", "Rebirth", "RebirthReady", "RebirthMarker", "RebirthCooldown", "HasBeenRevived" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Anivia");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(57, closure.ScriptNames.Count(), "the closure is 57 scripts (audit bb-coverage: 1370 blocks, 82 functions, one dangling passive name)");

        var hero = new Entity { ChampionName = "Anivia" };
        TestAssert.Equal(["FlashFrost", "Crystallize", "Frostbite", "GlacialStorm"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Anivia.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "the ultimate is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Anivia")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 34, RetailBot: false },
            "playable as a mid laner for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Anivia"), "listed once");

        // The tables the figures rest on, a second time as literals.
        var bolt = Blocks(Script("FlashFrost"), "OnBuffDeactivate").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal(FlashFrostDamage, Numbers(bolt.Param("DamageByLevel")!), "Flash Frost's damage by rank (the second press and the flight's end)");
        var frostbite = Blocks(Script("Frostbite"), "TargetExecute").First(item => item.Text("DestVar") == "BaseDamage");
        TestAssert.Equal(FrostbiteBase, Numbers(frostbite.Param("SrcValueByLevel")!), "Frostbite's base damage by rank (TargetExecute)");
        var length = Blocks(Script("Crystallize"), "SelfExecute").First(item => item.Text("DestVar") == "LineWidth");
        TestAssert.Equal(WallWidth, Numbers(length.Param("SrcValueByLevel")!), "Crystallize's wall width by rank (SelfExecute)");
        var count = Blocks(Script("Crystallize"), "SelfExecute").First(item => item.Text("DestVar") == "Iter");
        TestAssert.Equal(WallCount, Numbers(count.Param("SrcValueByLevel")!), "Crystallize's pieces by rank (SelfExecute)");
        var storm = Blocks(Script("GlacialStorm"), "SelfExecute").First(item => item.Text("DestVar") == "DamagePerLevel");
        TestAssert.Equal(StormDamage, Numbers(storm.Param("SrcValueByLevel")!), "Glacial Storm's damage per tick by rank (SelfExecute, every half second)");
        var drain = Blocks(Script("GlacialStorm"), "SelfExecute").First(item => item.Text("DestVar") == "ManaCost");
        TestAssert.Equal(StormMana, Numbers(drain.Param("SrcValueByLevel")!), "Glacial Storm's mana per tick by rank (SelfExecute, every half second)");
        var armour = Blocks(Script("RebirthReady"), "OnBuffActivate").First(item => item.Text("DestVar") == "RebirthArmorMod");
        TestAssert.Equal(EggArmour, Numbers(armour.Param("SrcValueByLevel")!), "the egg's armour and magic resist by level (18 levels)");
    }

    [Check("scripting.bb-anivia-rebirth-markers-are-on-from-activation")]
    public static void RebirthMarkersAreOnFromActivation()
    {
        using var match = Arena(1);
        TestAssert.True(Find(match.Hero, "RebirthReady") is not null, "RebirthReady (the armed passive) is on from activation (CharOnActivate)");
        TestAssert.True(Find(match.Hero, "RebirthMarker") is not null, "RebirthMarker is on from activation (CharOnActivate)");
        TestAssert.True(Find(match.Hero, "Rebirth") is null, "no egg while she lives");
        TestAssert.True(Find(match.Hero, "RebirthCooldown") is null, "no four-minute cooldown at the start");
        TestAssert.True(Find(match.Hero, "GlacialStormSpell") is null, "the storm's child spell is not on her (nothing in her closure casts it)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Flash Frost
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-flash-frost-second-press-detonates-where-the-missile-is")]
    public static void FlashFrostSecondPressDetonatesWhereTheMissileIs()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // 150 off the flight line: inside the detonation's 210 units, outside the missile's own path (no missile hit).
        foe.Position = new Position2(7300, 7150);
        TestAssert.True(match.Cast(0, at: new Position2(8000, 7000)), "Flash Frost is cast along the axis");
        match.Advance(0.6);
        TestAssert.True(Find(hero, "FlashFrost") is not null, "the toggle buff is on while the missile flies (SpellToggleSlot 1)");
        var hp = foe.Health;
        TestAssert.True(match.Cast(0), "the second press is accepted (the toggle ignores the 0.285 s cooldown of the cast)");
        match.Advance(0.05);
        TestAssert.True(Find(hero, "FlashFrost") is null, "the second press ends the toggle");
        TestAssert.Near(Mitigated(60.0, 30.0), hp - foe.Health, 0.5, "the detonation deals rank one's 60 magic against 30 magic resist");
        // Batch 350 (owner, 26 October 2026, "Wiki value per spell"): the script's second-press literal is 0.75, the tooltip and inibin effect 4 say
        // one second, and the wiki (V1.0.0.111) fixed the 0.75 s stun to the tooltip's one second; BbScriptOverrides loads 1.0 into the literal.
        TestAssert.Near(1.0, Left(match, foe, "Stun"), 0.08, "the second press stuns for one second (wiki V1.0.0.111; the script's literal 0.75 is stale)");
        TestAssert.Near(12.0, hero.SpellCooldowns[0], 0.1, "the cooldown starts at the detonation: 12 at rank one (CooldownPerLevel)");
        TestAssert.True(Find(foe, "Chilled") is not null, "the detonation chills what it hits");
    }

    [Check("scripting.bb-anivia-flash-frost-flight-end-detonates-at-its-range-with-a-one-second-stun")]
    public static void FlashFrostFlightEndDetonatesAtItsRange()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // The missile flies to its range (the cast range, 1100) and detonates there, 150 off the axis.
        foe.Position = new Position2(8100, 7150);
        TestAssert.True(match.Cast(0, at: new Position2(8000, 7000)), "Flash Frost is cast along the axis");
        // The cast takes a quarter second, then the missile flies 1100 units at 850 a second: it detonates about 1.55 seconds after the cast.
        match.Advance(1.7);
        TestAssert.True(Find(hero, "FlashFrost") is null, "the toggle ends with the flight (BuffOnMissileEnd removes it)");
        var stun = Left(match, foe, "Stun");
        TestAssert.True(stun > 0.5 && stun < 1.0, $"the flight's end stuns for one second (the BuffOnMissileEnd literal; {stun:0.00} s left)");
        TestAssert.True(Find(foe, "Chilled") is not null, "the flight's end chills what it hits");
        TestAssert.Near(12.0, hero.SpellCooldowns[0], 0.5, "the cooldown starts at the detonation (12 at rank one)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Crystallize
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-crystallize-wall-pieces-follow-the-rank")]
    public static void CrystallizeLaysTheBlocksAcrossTheRank()
    {
        for (var rank = 1; rank <= 5; rank++)
        {
            using var match = Arena(rank);
            var hero = match.Hero;
            hero.Mana = hero.MaxMana;
            hero.SpellCooldowns[1] = 0.0;
            TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), $"rank {rank}: Crystallize is cast 600 out");
            match.Advance(0.5);
            var pieces = Pieces(match);
            TestAssert.Equal((int)WallCount[rank - 1], pieces.Count, $"rank {rank}: {WallCount[rank - 1]} ice blocks");
            // The line runs across the aim (along z), 100 apart, and spans the width less one step (the W27c line rule).
            var zs = pieces.Select(piece => piece.Position.Z).OrderBy(z => z).ToArray();
            TestAssert.True(zs.Zip(zs.Skip(1)).All(pair => Math.Abs(pair.Second - pair.First - 100.0) < 1e-3), $"rank {rank}: a block every 100 units");
            TestAssert.Near(WallWidth[rank - 1] - 100.0, zs[^1] - zs[0], 1e-3, $"rank {rank}: the line is {WallWidth[rank - 1]} wide");
            // The bodies are solid, so they settle a little apart in x (under a unit's width) as they are laid.
            TestAssert.Near(7600.0, pieces.Average(piece => piece.Position.X), 1.0, $"rank {rank}: the line lies across the aim point");
        }
    }

    [Check("scripting.bb-anivia-crystallize-pieces-are-solid-untargetable-and-melt-after-five-seconds")]
    public static void PiecesAreSolidAndMelt()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        hero.Mana = hero.MaxMana;
        hero.SpellCooldowns[1] = 0.0;
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Crystallize is cast");
        match.Advance(0.5);
        var pieces = Pieces(match);
        TestAssert.True(pieces.Count > 0, "the pieces stand");
        foreach (var piece in pieces)
        {
            TestAssert.True(piece.AnyBuffOrStatus(buff => buff.Payload.Invulnerable), "a piece is invulnerable (SetInvulnerable)");
            TestAssert.True(piece.AnyBuffOrStatus(buff => buff.Payload.GhostProof), "a piece is ghost-proof (SetGhostProof, E02)");
            TestAssert.True(!piece.AnyBuffOrStatus(buff => buff.Payload.Ghosted), "a piece is a solid body (IgnoreCollision is no ghost at radius 100)");
            TestAssert.True(piece.AnyBuffOrStatus(buff => !buff.Payload.Targetable), "a piece cannot be targeted (SetTargetable false)");
            TestAssert.True(piece.AnyBuffOrStatus(buff => !buff.Payload.CanAttack), "a piece cannot attack (SetCanAttack false)");
        }
        // Seen a quarter second after the cast: the piece's own buff has about 4.75 seconds left (its five-second duration, Effect1 of the tooltip).
        var left = Left(match, pieces[0], "Crystallize");
        TestAssert.True(left > 4.3 && left <= 5.0, $"the piece's own buff runs five seconds ({left:0.00} s left after a quarter second)");
        match.Advance(5.5);
        TestAssert.True(Pieces(match).Count == 0, "the pieces have melted (their buff's 10000 damage at its end)");
    }

    [Check("scripting.bb-anivia-crystallize-blocks-the-route-through-the-wall")]
    public static void TheRouteGoesAroundTheWall()
    {
        // A wall across the foe's way, the foe 300 units before it; each part has its own (the pieces melt after five seconds).
        static Match Wall()
        {
            var match = Arena(1);
            match.Hero.Mana = match.Hero.MaxMana;
            match.Hero.SpellCooldowns[1] = 0.0;
            TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Crystallize is cast across the foe's way");
            match.Advance(0.5);
            match.Foe.Position = new Position2(7300, 7000);
            return match;
        }
        // The bodies are circles of radius 100, so the way through is the middle band of the line: a foe must never cross the aim line inside that
        // band. Going round the ends crosses it outside the band, and standing at the edge stops it before it.
        static bool InTheMiddle(Entity unit) =>
            unit.Position.X > 7550.0 && unit.Position.X < 7650.0 && unit.Position.Z > 6800.0 && unit.Position.Z < 7200.0;
        var goal = new Position2(7900, 7000);

        Planned();
        Blind();

        // The planner: the foe's client sees the pieces 300 units away and plans round them (the client's planner takes a unit its client cannot
        // see for no actor). This harness attaches no viewer, so no unit is marked seen; the order is planned with the foe's sight.
        void Planned()
        {
            using var match = Wall();
            var foe = match.Foe;
            var route = match.Runtime.Movement.SetMoveOrder(foe.NetId, goal, visibleTo: _ => true);
            TestAssert.True(route.Count > 2, $"the foe's route bends round the wall ({route.Count} points)");
            var through = 0;
            var furthest = foe.Position.X;
            for (var step = 0; step < 120; step++)
            {
                match.Advance(1.0 / 30.0);
                if (InTheMiddle(foe)) through++;
                furthest = Math.Max(furthest, foe.Position.X);
            }
            TestAssert.Equal(0, through, $"the planned route never goes through the wall's middle (furthest x {furthest:0.#})");
            TestAssert.True(furthest > 7700.0, $"the foe gets past the wall round its end (furthest x {furthest:0.#})");
        }

        // The solver: a route laid straight through the pieces (planned blind) walks the foe into them, and the movement holds it at their edge.
        // Batch 617: the client's own collision test lets a unit through once its give-up count (one per blocked frame) passes its limit, 15 for a
        // hero, even past a ghost-proof body: ghost-proofing only stops ghosted units. The port held the foe for good (a server-only ghost-proof
        // exception the client lacks); production runs the client's own routines, so the foe is held until it gives up.
        void Blind()
        {
            using var match = Wall();
            var foe = match.Foe;
            TestAssert.Equal(2, match.Runtime.Movement.SetMoveOrder(foe.NetId, goal).Count, "the blind route is the straight line");
            var held = 0;
            for (var step = 0; step < 120 && foe.RepathCount <= 15; step++)
            {
                match.Advance(1.0 / 30.0);
                TestAssert.False(InTheMiddle(foe) && foe.RepathCount <= 15,
                    $"the foe does not get through the wall's middle before it gives up (x {foe.Position.X:0.#}, give-up count {foe.RepathCount})");
                if (foe.Position.X > 7400.0 && foe.Position.X < 7550.0) held++;
            }
            TestAssert.True(foe.RepathCount > 15 && held > 10,
                $"the pieces hold the foe at their edge until it gives up ({held} steps at the edge, give-up count {foe.RepathCount})");
        }
    }

    [Check("scripting.bb-anivia-crystallize-pushes-the-units-that-touch-it")]
    public static void TheWallPushesTheUnitsNearIt()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // The foe stands 50 west of the wall's centre line: within 150 of the piece beside it, so the piece's OnBuffActivate pushes it (a hero, 110).
        foe.Position = new Position2(7550, 7000);
        hero.Mana = hero.MaxMana;
        hero.SpellCooldowns[1] = 0.0;
        var before = foe.Position;
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Crystallize is cast beside the foe");
        match.Advance(0.1);
        // The cast takes a quarter second; the pieces are laid and their activation pushes the foe while the push buff runs (0.25 s).
        match.Advance(0.3);
        TestAssert.True(Find(foe, "CrystalizePush") is not null || Find(foe, "CrystallizePush") is not null,
            "the foe gets the push buff (CrystallizePush, 0.25 s)");
        match.Advance(0.6);
        var moved = Math.Sqrt(Math.Pow(foe.Position.X - before.X, 2) + Math.Pow(foe.Position.Z - before.Z, 2));
        TestAssert.True(moved > 40.0, $"the foe is pushed off the wall (moved {moved:0.#} units)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Frostbite
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-frostbite-doubles-on-a-chilled-target")]
    public static void FrostbiteDoublesOnAChilledTarget()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // Clean foe: rank one's 55 magic (SpellDamageRatio 0.5 x ability power 0) against 30 magic resist.
        hero.SpellCooldowns[2] = 0.0;
        hero.Mana = hero.MaxMana;
        var hp = foe.Health;
        TestAssert.True(match.Cast(2, foe), "Frostbite is cast on the clean foe");
        match.Advance(0.6);
        TestAssert.Near(Mitigated(55.0, 30.0), hp - foe.Health, 0.5, "a clean foe takes the base damage");
        // The cooldown starts when the cast completes (a quarter second in), so about 4.65 seconds are left after 0.6 seconds.
        TestAssert.True(hero.SpellCooldowns[2] > 4.4 && hero.SpellCooldowns[2] <= 5.0, "Frostbite's cooldown at rank one is five seconds");
        // Chilled foe: the storm chills what stands within 400 each half second (Glacial Storm's buff is on from the ultimate's first tick).
        foe.Health = foe.MaxHealth;
        hero.Mana = hero.MaxMana;
        hero.SpellCooldowns[3] = 0.0;
        TestAssert.True(match.Cast(3, at: hero.Position), "Glacial Storm is turned on");
        match.Advance(0.6);
        TestAssert.True(Find(foe, "Chilled") is not null, "the storm chills the foe");
        hero.SpellCooldowns[2] = 0.0;
        hero.Mana = hero.MaxMana;
        TestAssert.True(match.Cast(2, foe), "Frostbite is cast on the chilled foe");
        match.Advance(0.6);
        // The storm's own ticks are 40 raw; Frostbite's hit on a chilled target is 110 raw (base doubled, ratio 1.0).
        var frostbiteHits = HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Raw - 110.0) < 1e-6).ToList();
        TestAssert.Equal(1, frostbiteHits.Count, "one 110 raw hit on the chilled foe (55 doubled)");
        TestAssert.Near(Mitigated(110.0, 30.0), frostbiteHits[0].Dealt, 0.5, "the chilled foe takes Frostbite doubled, against 30 magic resist");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Glacial Storm
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-glacial-storm-drains-and-hits-twice-a-second-while-toggled")]
    public static void GlacialStormDrainsAndHitsEachHalfSecond()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        hero.SpellCooldowns[3] = 0.0;
        hero.Mana = hero.MaxMana;
        TestAssert.True(match.Cast(3, at: hero.Position), "Glacial Storm is turned on");
        match.Advance(0.3);
        TestAssert.True(Find(hero, "GlacialStorm") is not null, "the toggle is on");
        // The cooldown is set to one second when the storm turns on, so 0.7 is left after 0.3 seconds.
        TestAssert.Near(0.7, hero.SpellCooldowns[3], 0.1, "the slot's cooldown is one second while the storm is on (set when it turns on)");
        // Two seconds of storm: four half-second ticks, each draining 20 mana (rank one) and hitting the foe for 40 raw.
        var mana = hero.Mana;
        var hits = HitsOn(match, "Foe").Count;
        match.Advance(2.0);
        TestAssert.Near(4 * StormMana[0], mana - hero.Mana, 6.0, "two seconds drain 80 mana: 20 every half second (the tooltip's 40 a second, regeneration aside)");
        var stormHits = HitsOn(match, "Foe").Skip(hits).Where(hit => Math.Abs(hit.Raw - StormDamage[0]) < 1e-6).ToList();
        TestAssert.Equal(4, stormHits.Count, "four 40 raw ticks in two seconds");
        TestAssert.True(Find(foe, "Chilled") is not null, "the foe in the storm is chilled");
        TestAssert.True(Find(foe, "GlacialStormSpell") is null, "nothing applies GlacialStormSpell (the storm's own buff is the only one)");
        // Toggle off: the storm ends, the cooldown starts at six seconds (the script's baseCooldown), and the drain stops.
        TestAssert.True(match.Cast(3, at: hero.Position), "a second press turns the storm off");
        match.Advance(0.3);
        TestAssert.True(Find(hero, "GlacialStorm") is null, "the second press ends the storm");
        // Six seconds from the press that ends it (set then), so 5.7 is left 0.3 seconds later.
        TestAssert.Near(5.7, hero.SpellCooldowns[3], 0.1, "six seconds after it ends (baseCooldown 6 times the cooldown modifier)");
        var idle = hero.Mana;
        match.Advance(1.0);
        TestAssert.True(hero.Mana >= idle - 0.5, "no drain once the storm is off (only regeneration)");
    }

    /// <summary>
    /// Batch 583 (the checklist's question 3, the tooltip's 20 percent): GlacialStorm adds Chilled with MovementSpeedMod and AttackSpeedMod -0.2 at
    /// every rank (OnBuffActivate and both periodic clocks; 1 s, BUFF_STACKS_AND_OVERLAPS), the tooltip's
    /// @Effect4Amount@ is 20 at ranks 1 to 3, and the wiki has 20 percent since V0.9.25.21 (from 25), unchanged up to V1.0.0.126. The storm's re-adds
    /// renew this caster's one chill (batch 407), so the foe is 20 percent slower, not more.
    /// </summary>
    [Check("scripting.bb-anivia-glacial-storm-slows-twenty-percent-with-one-chill")]
    public static void GlacialStormSlowsTwentyPercent()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        var baseSpeed = foe.Speed;
        TestAssert.True(baseSpeed * 0.8 > 220.0, $"the slowed foe stays above the 220 soft cap, so the slow reads directly ({baseSpeed:0.0})");
        hero.SpellCooldowns[3] = 0.0;
        hero.Mana = hero.MaxMana;
        TestAssert.True(match.Cast(3, at: hero.Position), "Glacial Storm is turned on");
        match.Advance(2.0);
        TestAssert.Equal(1, foe.Buffs.Count(buff => buff.Name == "Chilled" && buff.CasterNetId == hero.NetId), "one chill from the storm: " + Names(foe));
        TestAssert.Near(baseSpeed * 0.8, foe.Speed, 1.0, "20 percent slower");
        TestAssert.True(match.Cast(3, at: hero.Position), "a second press turns the storm off");
        match.Advance(1.5);
        TestAssert.True(Find(foe, "Chilled") is null, "the last chill ends a second after the storm");
        TestAssert.Near(baseSpeed, foe.Speed, 1.0, "and the speed is back");
    }

    [Check("scripting.bb-anivia-glacial-storm-ends-when-the-mana-runs-out-or-she-goes-far")]
    public static void GlacialStormEndsOnManaOrDistance()
    {
        using (var match = Arena(1))
        {
            var hero = match.Hero;
            hero.SpellCooldowns[3] = 0.0;
            hero.Mana = hero.MaxMana;
            TestAssert.True(match.Cast(3, at: hero.Position), "Glacial Storm is turned on");
            match.Advance(0.3);
            hero.Mana = 10.0; // below the 20 a half second that the next tick costs
            match.Advance(0.6);
            TestAssert.True(Find(hero, "GlacialStorm") is null, "the storm ends when the mana cannot pay its next tick");
        }
        using (var match = Arena(1))
        {
            var hero = match.Hero;
            hero.SpellCooldowns[3] = 0.0;
            hero.Mana = hero.MaxMana;
            TestAssert.True(match.Cast(3, at: hero.Position), "Glacial Storm is turned on");
            match.Advance(0.3);
            // The storm is centred on the cast point; Anivia walks 1300 away from it (the 1200-unit leash of the second periodic).
            hero.Position = new Position2(hero.Position.X + 1300.0, hero.Position.Z);
            match.Advance(0.6);
            TestAssert.True(Find(hero, "GlacialStorm") is null, "the storm ends when she is 1200 or more from its centre");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Rebirth
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-anivia-rebirth-egg-hatches-at-its-health-after-six-seconds")]
    public static void TheEggHatchesAfterSixSeconds()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        // The egg keeps her stat block: a push that carries no spells keeps the champion's numbers (Entity.StatCharacterName, the model-only rule).
        // AniviaEgg's own record (415 at level one, 740 at six) is the open question of the batch 330 record, not asserted here.
        var annieMax = StatsAt("Anivia", 6).MaxHp;
        var eggMax = annieMax;
        // A lethal hit (true damage, more than her health): the egg takes its place, healed to full (BBIncHealth 10000).
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw),
            "the lethal hit lands");
        Refresh(match, hero);
        TestAssert.True(hero.Alive, "the fatal damage is absorbed: Anivia lives as an egg (BuffOnPreDamage sets DamageAmount to 0)");
        TestAssert.True(Find(hero, "Rebirth") is not null, "the egg buff is on for six seconds");
        // The armed passive removes itself on its next update (WillRemove, BuffOnUpdateActions): a quarter second later.
        match.Advance(0.3);
        TestAssert.True(Find(hero, "RebirthReady") is null, "the armed passive is used up");
        TestAssert.Near(eggMax, hero.MaxHealth, 1e-6, "the egg's maximum health is her own stat block's (the model-only push keeps it)");
        TestAssert.Near(eggMax, hero.Health, 1e-6, "the egg is healed to full");
        // The egg takes 100 damage: at its hatching she has that health, not full.
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 100.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(5.5);
        TestAssert.True(Find(hero, "Rebirth") is not null, "still an egg at 5.5 seconds");
        match.Advance(1.0);
        TestAssert.True(hero.Alive, "the egg survives its six seconds and hatches");
        TestAssert.True(Find(hero, "Rebirth") is null, "the egg buff has ended");
        Refresh(match, hero);
        TestAssert.Near(annieMax, hero.MaxHealth, 1e-6, "Anivia's own maximum health is back");
        TestAssert.Near(eggMax - 100.0, hero.Health, 12.0, "she hatches at the egg's health (the 100 the egg took stays off, regeneration aside)");
        TestAssert.True(Find(hero, "RebirthCooldown") is not null, "the four-minute cooldown starts when the egg ends");
        TestAssert.True(Find(hero, "RebirthReady") is null, "no second rebirth during the cooldown");
    }

    [Check("scripting.bb-anivia-rebirth-egg-killed-within-six-seconds-stays-dead")]
    public static void TheEggKilledInItsSixSecondsStaysDead()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw);
        Refresh(match, hero);
        TestAssert.True(hero.Alive && Find(hero, "Rebirth") is not null, "the egg is up");
        match.Advance(2.0);
        // The egg has no fatal-damage protection of its own: a second lethal hit kills her for real.
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw),
            "the egg is hit for lethal damage");
        Refresh(match, hero);
        TestAssert.True(!hero.Alive, "the egg's death is her death: no hatching");
        TestAssert.True(Find(hero, "Rebirth") is null, "the egg buff ends with the death");
        TestAssert.True(Find(hero, "RebirthCooldown") is not null, "the cooldown starts with the death");
        TestAssert.True(Find(hero, "RebirthReady") is null, "no armed passive while she is in the cooldown");
    }

    [Check("scripting.bb-anivia-rebirth-egg-resistances-follow-the-level-table")]
    public static void TheEggResistancesFollowTheLevel()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var before = hero.Armor;
        Refresh(match, hero);
        var ownArmour = hero.Armor;
        TestAssert.Near(StatsAt("Anivia", 6).Armor, ownArmour, 1e-6, "awake, her armour is her own record's");
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw);
        Refresh(match, hero);
        // Level six sits in the table's -25 band (levels 5 to 7). The egg's record carries her own armour, so the mod is the whole difference.
        TestAssert.Near(StatsAt("AniviaEgg", 6).Armor + EggArmour[5], hero.Armor, 1e-6, "as an egg, her armour is the egg's record plus the level's mod (-25 at six)");
        TestAssert.True(before >= 0.0, "the awake armour is read before the egg");
    }

    [Check("scripting.bb-anivia-rebirth-comes-back-after-the-four-minute-cooldown")]
    public static void TheArmedPassiveReturnsAfterTheCooldown()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(6.5);
        TestAssert.True(Find(hero, "RebirthCooldown") is not null && Find(hero, "RebirthReady") is null, "the cooldown runs after the egg");
        match.Advance(241.0);
        TestAssert.True(Find(hero, "RebirthCooldown") is null, "the four-minute cooldown has ended");
        TestAssert.True(Find(hero, "RebirthReady") is not null, "the armed passive is back (RebirthCooldown's OnBuffDeactivate)");
    }

    [Check("scripting.bb-anivia-rebirth-egg-puts-a-ready-summoner-spell-on-six-seconds")]
    public static void TheEggPutsAReadySummonerSpellOnSixSeconds()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        hero.SummonerCooldowns[0] = 0.0;  // ready: Rebirth's OnBuffActivate sets a summoner cooldown at or under six seconds to six
        hero.SummonerCooldowns[1] = 30.0; // longer than six: the egg leaves it alone
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 500.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.3);
        TestAssert.True(Find(hero, "Rebirth") is not null, "the egg is up");
        TestAssert.True(hero.SummonerCooldowns[0] > 5.5 && hero.SummonerCooldowns[0] <= 6.0,
            $"the ready summoner spell is on six seconds ({hero.SummonerCooldowns[0]:0.00} s left)");
        TestAssert.True(hero.SummonerCooldowns[1] > 29.0, "a summoner spell on a longer cooldown is left alone");
    }

    /// <summary>The level stats of a champion record (the same formula the audit uses).</summary>
    private static ChampionLevelStats StatsAt(string champion, int level) =>
        GameData.Default.StatsAtLevel(GameData.Default.Champion(champion), level);
}
