using System.Buffers.Binary;
using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Stats;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 240): Nunu plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the literals of the shipped scripts and spell inibins (rank five: Consume 900 true damage and a heal of 345 plus
/// the ability power, Ice Blast 275 magic plus the ratio of 1.0, Absolute Zero 1125 magic plus 2.5 times the ability power at rank three); the scenario
/// <c>S-nunu</c> shows the same facts on a headless match. Consume takes a minion or a monster, never a hero; Blood Boil adds movement and attack speed to its
/// target and to himself for fifteen seconds; Ice Blast chills for four; Absolute Zero is a three-second channel that slows every enemy in 575 each quarter
/// second and deals its nova in 650 when it ends, or a time-scaled share of it when a move ends it early; Visionary makes the next spell free after
/// seven basic attacks, and its marker survives a death.
/// </summary>
public static class BbNunuChecks
{
    private static double Dist(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static void Refresh(Match match) => match.Runtime.HeroStats.Refresh(match.Hero, push: false);

    /// <summary>Nunu at rank <paramref name="rank"/> (Consume, Blood Boil and Ice Blast of five ranks, Absolute Zero of three), 80 ability power from a Needlessly Large Rod.</summary>
    private static Match Arena(int rank = 5, int level = 1)
    {
        var match = new Match("Nunu", foeChampion: "Orianna");
        match.Hero.Level = level;
        match.Learn(rank);
        match.GiveAbilityPower();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 0.0;
        match.Ally.SpellBlock = 0.0;
        match.FarFoe.SpellBlock = 0.0;
        match.Refill();
        return match;
    }

    /// <summary>A neutral monster beside Nunu that no hit can kill in the check: 200 armour and 200 magic resist, so true damage reads apart.</summary>
    private static Entity Monster(Match match, Position2 at) =>
        Harden(Enemy(match, "Monster", at, UnitType.Monster, Team.Neutral));

    private static Position2 At(Entity unit, double dx) => new(unit.Position.X + dx, unit.Position.Z);

    private static Entity Harden(Entity unit)
    {
        unit.Health = unit.MaxHealth = 20000.0;
        unit.Armor = 200.0;
        unit.SpellBlock = 200.0;
        return unit;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks, costs
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Nunu");
        foreach (var name in new[]
                 {
                     "CharScriptNunu", "Consume", "BloodBoil", "IceBlast", "AbsoluteZero", "AbsoluteZeroSlow", "Visionary", "Visionary_Counter",
                     "Visionary_marker", "ChampionChampionDelta", "APBonusDamageToTowers", "ResistantSkin",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Nunu");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(0, closure.Dangling.Count, "no reference dangles");
        TestAssert.Equal(27, closure.ScriptNames.Count(), "the closure is 27 scripts (bb-coverage: nothing dangling, no failure)");

        var hero = new Entity { ChampionName = "Nunu" };
        TestAssert.Equal(["Consume", "BloodBoil", "IceBlast", "AbsoluteZero"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Nunu.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Nunu")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 450, RetailBot: true }, "playable as a jungler for 450 IP, one of the retail bots");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Nunu"), "listed once");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failed at construction");
    }

    [Check("scripting.bb-nunu-spells-cost-and-cool-down-as-the-inibins-say")]
    public static void CostsAndCooldownsAtRankFive()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var costs = new[] { 60.0, 50.0, 115.0, 150.0 }; // rank five: Consume 60, Blood Boil 50, Ice Blast 115 (75 at rank one), Absolute Zero 150 (rank three)
        var cooldowns = new[] { 10.0, 15.0, 6.0, 90.0 };
        foreach (var slot in new[] { 0, 1, 2 })
        {
            var mana = hero.Mana;
            if (slot == 0) TestAssert.True(match.Cast(0, Monster(match, At(hero, 100.0))), "Consume casts on a monster");
            else if (slot == 1) TestAssert.True(match.Cast(1, match.Ally), "Blood Boil casts on the ally");
            else TestAssert.True(match.Cast(2, match.Foe), "Ice Blast casts on the enemy");
            match.Advance(0.4);
            TestAssert.Near(costs[slot], mana - hero.Mana, 2.0, $"slot {slot} costs its rank-five mana (regeneration allowed)");
            // Batch 340: the cooldown starts at the cast frame (CastFrame), not at the press.
            TestAssert.Near(cooldowns[slot], hero.SpellCooldowns[slot] + 0.4 - CastFrame(match.Module.SpellName(hero, slot)), 0.3,
                $"slot {slot} cools for its rank-five cooldown");
            match.Refill();
            match.Advance(0.2);
        }
        match.Foe.Position = new Position2(7300, 7000);
        TestAssert.True(match.Cast(3, hero), "Absolute Zero casts on himself");
        match.Advance(0.4);
        TestAssert.Near(150.0, hero.MaxMana - hero.Mana, 2.0, "rank three costs 150 mana");
        TestAssert.Near(90.0, hero.SpellCooldowns[3] + 0.4, 0.3, "rank three cools for 90 seconds (150 at rank one, 120 at rank two)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Consume
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-consume-deals-true-damage-and-heals")]
    public static void ConsumeDealsTrueDamageAndHeals()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var monster = Monster(match, At(hero, 100.0));
        hero.Health = 50.0; // room for the whole heal (the harness hero has 545 health at level one)
        var health = hero.Health;
        var regen = hero.HealthRegen;
        TestAssert.True(match.Cast(0, monster), "Consume casts on the monster");
        match.Advance(1.0);
        var hits = HitsOn(match, "Monster").Where(hit => hit.Dealt > 0.0).ToArray();
        TestAssert.Equal(1, hits.Length, "one bite");
        TestAssert.Near(900.0, hits[0].Raw, 0.5, "rank five is 900 true damage (the monster's 200 armour and 200 magic resist do not reduce it)");
        TestAssert.Near(900.0, hits[0].Dealt, 0.5, "true damage is dealt whole");
        // The heal is the rank-five value plus the ability power, and regeneration over the second.
        var healed = hero.Health - health;
        TestAssert.Near(345.0 + hero.AbilityPower, healed - regen * (1.0 - 0.4), 3.0, "the heal is 345 plus the ability power (ratio 1.0)");
        TestAssert.True(match.Module.ScriptFailures.Count == 0, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nunu-consume-deals-450-to-a-resistant-skin-target")]
    public static void ConsumeDealsTheLowerFigureToAResistantSkinTarget()
    {
        // Consume's TargetExecute: a target that carries ResistantSkin (Baron Nashor's buff) takes 200 to 450 true damage, anything else 400 to 900.
        using var match = Arena(5);
        var hero = match.Hero;
        var monster = Monster(match, At(hero, 100.0));
        TestAssert.True(ScriptBuff(match.Runtime, monster, "ResistantSkin", 25000.0) is not null, "the monster carries ResistantSkin");
        TestAssert.True(match.Cast(0, monster), "Consume casts on it");
        match.Advance(1.0);
        var hits = HitsOn(match, "Monster").Where(hit => hit.Dealt > 0.0).ToArray();
        TestAssert.Equal(1, hits.Length, "one bite");
        TestAssert.Near(450.0, hits[0].Raw, 0.5, "rank five is 450 true damage against ResistantSkin (200 at rank one)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nunu-consume-takes-a-minion-or-a-monster-never-a-hero")]
    public static void ConsumeRefusesAHero()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var minion = Enemy(match, "Minion", At(hero, 100.0), UnitType.Minion, Team.Chaos);
        TestAssert.True(match.CanCast(0, minion).Allowed, "a minion within its range is a legal target (Consume flags AffectMinions and AffectEnemies)");
        TestAssert.True(match.CanCast(0, Monster(match, At(hero, -100.0))).Allowed, "a neutral monster is a legal target (AffectNeutral)");
        TestAssert.False(match.CanCast(0, match.Foe).Allowed, "an enemy hero is refused: Consume's shipped flags name no heroes");
        TestAssert.False(match.CanCast(0, match.Ally).Allowed, "an allied hero is refused too");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Blood Boil
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-blood-boil-hastes-the-ally-and-himself-for-fifteen-seconds")]
    public static void BloodBoilHastesTheAllyAndHimself()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var ally = match.Ally;
        Refresh(match);
        var speed = hero.Speed;
        var attack = hero.AttackSpeedMod;
        TestAssert.True(match.Cast(1, ally), "Blood Boil casts on the ally");
        match.Advance(0.6);
        Refresh(match);
        var selfBuff = Buff(hero, "BloodBoil");
        var allyBuff = Buff(ally, "BloodBoil");
        TestAssert.True(selfBuff is not null && allyBuff is not null, "both carry the buff");
        TestAssert.Near(0.15, selfBuff!.Payload.MoveSpeedPercent, 1e-6, "movement speed +15 percent at rank five (0.11 to 0.15)");
        TestAssert.Near(0.65, selfBuff.Payload.AttackSpeedMod, 1e-6, "attack speed +65 percent at rank five (0.25 to 0.65)");
        TestAssert.Near(0.15, allyBuff!.Payload.MoveSpeedPercent, 1e-6, "the ally gets the same movement speed");
        TestAssert.Near(0.65, allyBuff.Payload.AttackSpeedMod, 1e-6, "the ally gets the same attack speed");
        TestAssert.True(hero.Speed > speed * 1.1, "Nunu runs faster");
        TestAssert.True(attack > 0.0 && hero.AttackSpeedMod > attack, "Nunu attacks faster");
        var removed = double.NaN;
        var waited = 0.6;
        while (double.IsNaN(removed) && waited < 20.0)
        {
            match.Advance(0.1);
            waited += 0.1;
            if (Buff(hero, "BloodBoil") is null && Buff(ally, "BloodBoil") is null) removed = waited;
        }
        TestAssert.Near(15.0 + 0.25, removed, 0.3, "both lose it fifteen seconds after the cast frame (the cast takes a quarter second)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ice Blast
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-ice-blast-hits-for-magic-damage-and-chills-for-four-seconds")]
    public static void IceBlastHitsAndChills()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var foe = match.Foe;
        TestAssert.True(match.Cast(2, foe), "Ice Blast casts on the enemy");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe").Where(hit => hit.Dealt > 0.0).ToArray();
        TestAssert.True(hits.Length >= 1, "the ice ball hits");
        TestAssert.Near(275.0 + 1.0 * hero.AbilityPower, hits[0].Raw, 1.0, "rank five is 275 magic plus the ability power (ratio 1.0)");
        var chill = Buff(foe, "IceBlast");
        TestAssert.True(chill is not null, "the chill is on the enemy");
        TestAssert.Near(-0.6, chill!.Payload.MoveSpeedMult, 1e-6, "movement speed reduced by 60 percent at rank five (20 to 60)");
        TestAssert.Near(-0.25, chill.Payload.AttackSpeedMult, 1e-6, "attack speed reduced by 25 percent");
        var removed = double.NaN;
        var waited = 1.0;
        while (double.IsNaN(removed) && waited < 10.0)
        {
            match.Advance(0.1);
            waited += 0.1;
            if (Buff(foe, "IceBlast") is null) removed = waited;
        }
        TestAssert.Near(4.0 + 0.7, removed, 0.3, "the chill lasts four seconds from the hit (the hit lands about 0.7 seconds after the press)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Absolute Zero
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-absolute-zero-slows-enemies-in-its-area-every-quarter-second")]
    public static void AbsoluteZeroSlowsTheAreaEveryQuarterSecond()
    {
        using var match = Arena(3);
        var hero = match.Hero;
        var foe = match.Foe;
        var speed = hero.Speed;
        TestAssert.True(match.Cast(3, hero), "Absolute Zero starts on himself");
        match.Advance(0.6);
        var slows = foe.Buffs.Where(buff => buff.Name == "AbsoluteZeroSlow").ToArray();
        // Batch 407: AbsoluteZeroSlow's add is StacksExclusive (the client default, as Gangplank's R Slow), so one caster's re-applications renew
        // one instance instead of adding one per quarter second (Reconstruction: the owner's one-slow observation of the R, batch 399/407).
        TestAssert.True(slows.Length == 1, "the enemy in 575 holds one exclusive slow, renewed each quarter second (batch 407)");
        TestAssert.True(slows.All(buff => Math.Abs(buff.Payload.MoveSpeedMult + 0.5) < 1e-6 && Math.Abs(buff.Payload.AttackSpeedMult + 0.25) < 1e-6),
            "each instance takes 50 percent of movement speed and 25 percent of attack speed (rank three ChannelingStart values)");
        TestAssert.True(Buff(foe, "AbsoluteZeroSlow") is not null && slows.All(buff => buff.ExpiresAt - match.Now <= 3.0 + 1e-6),
            "each instance lasts three seconds");
        TestAssert.True(Buff(match.FarFoe, "AbsoluteZeroSlow") is null, "a unit beyond 575 is not slowed");
        Refresh(match);
        TestAssert.Near(speed, hero.Speed, 0.5, "Nunu himself is not slowed (no stat change on the channel's own buff)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nunu-absolute-zero-completes-with-a-nova-when-the-channel-ends")]
    public static void AbsoluteZeroNovaWhenTheChannelEnds()
    {
        using var match = Arena(3);
        var hero = match.Hero;
        var foe = match.Foe;
        var ally = match.Ally;
        ally.Position = new Position2(7000 + 700, 7000); // beyond the 650 nova
        TestAssert.True(match.Cast(3, hero), "Absolute Zero starts");
        var start = match.Now;
        var onAt = double.NaN;
        var offAt = double.NaN;
        for (var step = 0; step < 240 && double.IsNaN(offAt); step++)
        {
            match.Advance(1.0 / 30.0);
            if (double.IsNaN(onAt) && Buff(hero, "AbsoluteZero") is not null) onAt = match.Now - start;
            if (!double.IsNaN(onAt) && Buff(hero, "AbsoluteZero") is null) offAt = match.Now - start;
        }
        TestAssert.Near(3.0, offAt - onAt, 0.25, "the channel runs three seconds (ChannelDuration)");
        match.Advance(0.5);
        var hits = HitsOn(match, "Foe").Where(hit => hit.Dealt > 0.0).ToArray();
        TestAssert.Equal(1, hits.Length, "the nova is one hit on the enemy in 650");
        TestAssert.Near(1125.0 + 2.5 * hero.AbilityPower, hits[0].Raw, 1.0, "rank three is 1125 magic plus 2.5 times the ability power");
        TestAssert.True(HitsOn(match, "Ally").All(hit => hit.Dealt <= 0.0), "an ally 700 away (beyond the 650 nova) takes nothing");
        TestAssert.True(Buff(foe, "AbsoluteZeroSlow") is null || foe.Buffs.All(buff => buff.Name != "AbsoluteZero"), "the channel's buff is spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nunu-absolute-zero-moved-scales-the-nova-with-the-time-channelled")]
    public static void AbsoluteZeroMovedScalesTheNova()
    {
        using var match = Arena(3);
        var hero = match.Hero;
        var foe = match.Foe;
        TestAssert.True(match.Cast(3, hero), "Absolute Zero starts");
        var start = match.Now;
        match.Advance(1.5);
        TestAssert.True(Buff(hero, "AbsoluteZero") is not null, "still channelling at 1.5 seconds");
        match.Runtime.Movement.SetMoveOrder(hero.NetId, new Position2(7600, 7000));
        var endAt = double.NaN;
        for (var step = 0; step < 60 && double.IsNaN(endAt); step++)
        {
            match.Advance(1.0 / 30.0);
            if (Buff(hero, "AbsoluteZero") is null) endAt = match.Now - start;
        }
        TestAssert.True(!double.IsNaN(endAt) && endAt < 2.0, "a move ends the channel at once (CantCancelWhileChanneling is 0)");
        match.Advance(0.5);
        var hits = HitsOn(match, "Foe").Where(hit => hit.Dealt > 0.0).ToArray();
        TestAssert.Equal(1, hits.Length, "the cancel deals one hit");
        var full = 1125.0 + 2.5 * hero.AbilityPower;
        var share = hits[0].Raw / full;
        // The ratio is 0.25 a second of the channel's own clock (LifeTime, the per-step time since it began), so it reads 0.25 x LifeTime.
        TestAssert.True(share > 0.25 * 1.0 && share < 0.25 * 2.0, $"the share is 0.25 a second of the channel ({share:0.000})");
        TestAssert.Near(0.25 * (endAt - 0.25), share, 0.12, "the share is the time channelled (about 0.25 a second, the cast frame aside)");
        TestAssert.True(hits[0].Raw < full, "an early cancel does less than the complete nova");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Visionary
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nunu-visionary-makes-the-next-spell-free-after-seven-attacks")]
    public static void VisionaryMakesTheNextSpellFree()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var dummy = match.Foe;
        dummy.Position = new Position2(7100, 7000); // inside Nunu's attack range
        dummy.MaxHealth = dummy.Health = 50000.0;
        TestAssert.True(Buff(hero, "Visionary_marker") is not null, "the marker is up from the start");
        for (var attack = 1; attack <= 6; attack++)
        {
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), $"attack {attack}");
            match.Advance(1.2);
            TestAssert.True(Buff(hero, "Visionary") is null, $"no free spell after attack {attack}");
        }
        TestAssert.Near(6.0, Buff(hero, "Visionary_Counter")?.Stacks ?? 0, 0.0, "six hits are six stacks of the counter");
        hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), "the seventh attack");
        match.Advance(1.2);
        TestAssert.True(Buff(hero, "Visionary") is not null, "the seventh hit makes the next spell free");
        TestAssert.True(Buff(hero, "Visionary_Counter") is null, "the counter is spent");
        var monster = Monster(match, At(hero, 80.0));
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, monster), "Consume casts for free");
        match.Advance(0.2);
        TestAssert.True(mana - hero.Mana <= 0.5, $"no mana is spent (regeneration aside): {mana - hero.Mana:0.00}");
        TestAssert.True(Buff(hero, "Visionary") is null, "the free spell is used up by the cast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nunu-visionary-marker-persists-through-a-death")]
    public static void VisionaryMarkerPersistsThroughDeath()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        TestAssert.True(Buff(hero, "Visionary_marker") is not null, "the marker is up");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Advance(0.2);
        TestAssert.False(hero.Alive, "Nunu is dead");
        TestAssert.True(Buff(hero, "Visionary_marker") is not null, "the marker persists through death (PersistsThroughDeath)");
        for (var i = 0; i < 900 && !hero.Alive; i++) match.Advance(0.1);
        TestAssert.True(hero.Alive, "he respawns");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Visionary's free Consume, pressed through the production cast request (the 0xA2 payload, parsed and dispatched by <c>SpellSystem.HandleCastRequest</c>
    /// as the composition does). The cast answer carries no mana cost and no mana leaves the hero. The cast's own hook resets the four cost increments at the
    /// press; every stat block the client gets before the cast frame must still carry the price the cast was paid at (zero while the marker is up), and the
    /// live costs come back once the cast is paid. Reconstruction (27 October 2026, owner finding 78): the shipped scripts do not say when retail sends the reset.
    /// </summary>
    [Check("scripting.bb-nunu-visionary-free-cast-keeps-its-costs-until-the-cast-frame")]
    public static void VisionaryFreeCastKeepsItsCostsUntilTheCastFrame()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var dummy = match.Foe;
        dummy.Position = new Position2(7100, 7000);
        dummy.MaxHealth = dummy.Health = 50000.0;
        var stats = AttachStatRecorder(match);
        for (var attack = 1; attack <= 7; attack++)
        {
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), $"attack {attack}");
            match.Advance(1.2);
        }
        TestAssert.True(Buff(hero, "Visionary") is not null, "the seventh hit makes the next spell free");
        var monster = Monster(match, At(hero, 80.0));
        var mana = hero.Mana;
        var castFrame = match.CastFrameOf(0);
        var pressedAt = match.Now;
        var pushesBefore = stats.Pushes.Count;
        var castsBefore = match.Log.Casts.Count;
        TestAssert.True(match.Runtime.Spells.HandleCastRequest(hero, EncodeCast(hero.NetId, 0, monster.Position, monster.NetId), match.Now),
            "the production cast request is accepted");
        TestAssert.Equal(1, match.Log.Casts.Count - castsBefore, "one cast answer (0xBD) goes out");
        TestAssert.Near(0.0, match.Log.Casts[^1].ManaCost, 1e-6, "the cast answer carries no mana cost");
        match.Advance(castFrame + 0.3);
        TestAssert.Near(mana, hero.Mana, 0.5, "no mana leaves the hero at the cast frame");
        var sent = stats.Pushes.Skip(pushesBefore).ToList();
        TestAssert.True(sent.Count > 0, "the client gets stat blocks around the cast");
        var beforeCastFrame = sent.Where(push => push.At < pressedAt + castFrame - 1e-9).ToList();
        TestAssert.True(beforeCastFrame.All(push => push.Snapshot.ManaCosts[0] == 0.0),
            "every stat block sent before the cast frame keeps the price the cast was paid at: " +
            string.Join(" ", beforeCastFrame.Select(push => $"+{push.At - pressedAt:0.00}s[{string.Join(",", push.Snapshot.ManaCosts)}]")));
        TestAssert.Near(mana, sent[^1].Snapshot.CurrentMana, 0.5, "the replicated mana after the cast equals the mana before");
        TestAssert.True(sent[^1].Snapshot.ManaCosts[0] > 0.0, "the live costs return once the cast is paid");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>A windup that its hero dies in releases the costs it held: the hero's stat block carries the live costs again.</summary>
    [Check("scripting.bb-nunu-visionary-held-costs-end-with-a-windup-that-dies")]
    public static void VisionaryHeldCostsEndWithAWindupThatDies()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var dummy = match.Foe;
        dummy.Position = new Position2(7100, 7000);
        dummy.MaxHealth = dummy.Health = 50000.0;
        for (var attack = 1; attack <= 7; attack++)
        {
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, dummy), $"attack {attack}");
            match.Advance(1.2);
        }
        var monster = Monster(match, At(hero, 80.0));
        TestAssert.True(match.Runtime.Spells.HandleCastRequest(hero, EncodeCast(hero.NetId, 0, monster.Position, monster.NetId), match.Now),
            "the production cast request is accepted");
        TestAssert.Near(0.0, match.Runtime.HeroStats.Snapshot(hero).ManaCosts[0], 1e-6, "while the windup is pending the stat block holds its price");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        match.Advance(0.2);
        TestAssert.False(hero.Alive, "Nunu dies in his windup");
        TestAssert.True(match.Runtime.HeroStats.Snapshot(hero).ManaCosts[0] > 0.0, "the held costs end with the windup that died");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>Attaches a recorder to the hero-stats transport: every stat block pushed to the clients, with the time of the push.</summary>
    private static StatPushRecorder AttachStatRecorder(Match match)
    {
        var field = typeof(HeroStatsService).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var recorder = (StatPushRecorder)(object)DispatchProxy.Create<IHeroStatsTransport, StatPushRecorder>();
        recorder.Target = (IHeroStatsTransport)field.GetValue(match.Runtime.HeroStats)!;
        recorder.Clock = () => match.Now;
        field.SetValue(match.Runtime.HeroStats, recorder);
        return recorder;
    }

    /// <summary>The 0xA2 cast request the client sends (<c>SpellSystem.ParseCastRequest</c> reads it): actor, slot, point, end point, target.</summary>
    private static byte[] EncodeCast(uint actorNetId, int slot, Position2 point, uint targetNetId)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), actorNetId);
        payload[5] = checked((byte)(slot << 1));
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(10, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14, 4), (float)point.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(22, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(26, 4), (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), targetNetId);
        return payload;
    }

    /// <summary>Records every stat block the hero-stats service pushes to its transport, with the time of the push.</summary>
    public class StatPushRecorder : DispatchProxy
    {
        public IHeroStatsTransport Target { get; set; } = null!;
        public Func<double> Clock { get; set; } = () => 0.0;
        public List<(double At, HeroStatsReplicationSnapshot Snapshot)> Pushes { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Push" && args?[0] is HeroStatsReplicationSnapshot snapshot) Pushes.Add((Clock(), snapshot));
            try { return targetMethod!.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
