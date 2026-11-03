using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 215): Master Yi plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the literals of the shipped scripts and spell inibins; the scenario <c>S-masteryi</c> shows the same facts on a
/// headless match. Alpha Strike marks its first target, leaps behind it and hits it for 100 to 300 magic plus the ratio of 1.0; Meditate is a channel of five
/// seconds that heals every half second and closes with a heal on success (its inibin does not block a cancel, so a move ends it); Wuju Style is the passive
/// stance while E is ready and the cast swaps it for the supercharge; Highlander's kill hook readies every spell only while he is active; Double Strike
/// lands its eighth attack twice (one stack per hit, the seventh hit sets the icon).
/// </summary>
public static class BbMasterYiChecks
{
    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static void Refresh(Match match) => match.Runtime.HeroStats.Refresh(match.Hero, push: false);

    /// <summary>Master Yi at rank <paramref name="rank"/> (the three spells of five ranks, the ultimate of three), 80 ability power from a Needlessly Large Rod, the enemy 300 away.</summary>
    private static Match Arena(int rank = 5, int level = 1)
    {
        var match = new Match("MasterYi", foeChampion: "Orianna");
        match.Hero.Level = level;
        match.Learn(rank);
        match.GiveAbilityPower();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("MasterYi");
        foreach (var name in new[]
                 {
                     "CharScriptMasterYi", "AlphaStrike", "AlphaStrikeTarget", "AlphaStrikeTeleport", "AlphaStrikeMarker", "Meditate", "WujuStyle",
                     "WujuStyleSuperCharged", "MasterYiWujuDeactivated", "Highlander", "MasterYiDoubleStrike", "DoubleStrike", "DoubleStrikeIcon",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("MasterYi");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(21, closure.ScriptNames.Count(), "the closure is 21 scripts (bb-coverage: 291 blocks, 46 functions, nothing dangling)");

        var hero = new Entity { ChampionName = "MasterYi" };
        TestAssert.Equal(["AlphaStrike", "Meditate", "WujuStyle", "Highlander"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from MasterYi.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("MasterYi")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 450, RetailBot: true }, "playable as a jungler for 450 IP, one of the retail bots");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "MasterYi"), "listed once");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failed at construction");
    }

    [Check("scripting.bb-masteryi-meditate-is-an-ordinary-channel-the-batch-209-gate-does-not-hold")]
    public static void MeditateIsNotCancelBlocked()
    {
        // SpellData*CantCancelWhileChanneling is the string '0' in Meditate.inibin: only Crowstorm, Infinite Duress and Stand United refuse a stop.
        TestAssert.False(GameData.Default.Spell("Meditate").CantCancelWhileChanneling, "Meditate does not carry CantCancelWhileChanneling");
        TestAssert.True(GameData.Default.Spell("Meditate").CantCancelWhileWindingUp, "its windup does (the order window closes at the cast frame)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Alpha Strike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-alpha-strike-hits-the-marked-target-and-leaps-behind-it")]
    public static void AlphaStrikeHitsTheMarkedTargetAndLeapsBehindIt()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var foe = match.Foe;
        var start = hero.Position;
        TestAssert.True(match.Cast(0, foe), "Q casts on the enemy");
        match.Advance(1.5);
        var hits = HitsOn(match, "Foe").Where(hit => hit.Dealt > 0.0).ToArray();
        // The first damaging hit is the Alpha Strike's (AlphaStrikeTarget); the leap's AI_ATTACKTO order then makes Yi swing at the hero, the second hit.
        TestAssert.True(hits.Length >= 1, "the marked target takes the Alpha Strike");
        TestAssert.Near(Mitigated(300.0 + 1.0 * hero.AbilityPower, foe.SpellBlock), hits[0].Dealt, 1.0,
            "300 magic at rank five plus the ability power (ratio 1.0), through 30 magic resist");
        if (hits.Length > 1)
            TestAssert.True(hits[1].At - hits[0].At < 0.5, "the second hit comes from the leap's attack order, a fraction of a second later");
        TestAssert.True(Of(foe, "AlphaStrikeMarker") is null, "the marker is spent by the leap");
        TestAssert.True(Dist(hero.Position, start) > 200.0, "Yi has left his start");
        TestAssert.Near(75.0, Dist(hero.Position, foe.Position), 5.0, "he lands 75 behind the target (AlphaStrikeTeleport)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-masteryi-alpha-strike-costs-120-mana-and-cools-for-ten-seconds-at-rank-five")]
    public static void AlphaStrikeCostAndCooldown()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(0.4);
        TestAssert.Near(120.0, mana - hero.Mana, 1.0, "rank five costs 120 (80 at rank one, the inibin ManaCost ladder)");
        // Batch 340: the cooldown starts at the cast frame (CastFrame), not at the press.
        TestAssert.Near(10.0, hero.SpellCooldowns[0] + 0.4 - CastFrame("AlphaStrike"), 0.2, "rank five cools for 10 (18 at rank one)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Meditate
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-meditate-heals-every-half-second-and-closes-on-success")]
    public static void MeditateHealsEveryHalfSecondAndClosesOnSuccess()
    {
        using var match = Arena(5, level: 18);
        var hero = match.Hero;
        TestAssert.True(hero.MaxHealth > 1600.0, "a level-eighteen health bar holds the whole heal");
        hero.Health = hero.MaxHealth * 0.3;
        var before = hero.Health;
        var armor = hero.Armor;
        TestAssert.True(match.Cast(1, hero), "W casts on himself");
        match.Advance(1.0);
        Refresh(match);
        TestAssert.Near(300.0, hero.Armor - armor, 1.0, "the channel adds 300 armor at rank five (100 to 300 by rank)");
        match.Advance(4.5);
        TestAssert.True(Of(hero, "Meditate") is null, "the buff ends with the channel");
        // Ten ticks (one at the start, then every 0.5 s for the 4.9 s buff) of 58.3 + 0.33 ability power at rank five, then the closing 183.3.
        var tick = 58.3 + 0.33 * hero.AbilityPower;
        var healed = hero.Health - before;
        TestAssert.Near(10.0 * tick + 183.3, healed, 30.0, "ten ticks and the closing heal (a few seconds of regeneration on top)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-masteryi-meditate-move-ends-the-channel-without-the-closing-heal")]
    public static void MeditateMoveEndsTheChannel()
    {
        using var match = Arena(5, level: 18);
        var hero = match.Hero;
        hero.Health = hero.MaxHealth * 0.3;
        var before = hero.Health;
        var tick = 58.3 + 0.33 * hero.AbilityPower;
        TestAssert.True(match.Cast(1, hero), "W casts on himself");
        match.Advance(1.5);
        TestAssert.True(Of(hero, "Meditate") is not null, "the channel runs after 1.5 s");
        match.Runtime.Movement.SetMoveOrder(hero.NetId, new Position2(7600, 7000));
        match.Advance(0.5);
        TestAssert.True(Of(hero, "Meditate") is null, "the move ends the channel at once");
        match.Advance(4.0);
        var healed = hero.Health - before;
        TestAssert.True(healed < 4.5 * tick + 20.0, $"only the ticks before the move (healed {healed:0.0}, the closing heal would add 183.3)");
        TestAssert.True(healed > 2.0 * tick, "and the ticks did land");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Wuju Style
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-wuju-style-is-up-while-e-is-ready-and-casting-swaps-it-for-the-supercharge")]
    public static void WujuStyleSwapsForTheSupercharge()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        match.Advance(0.5);
        TestAssert.True(Of(hero, "WujuStyle") is not null, "the passive stance is up while E is ready");
        Refresh(match);
        var passive = hero.AttackDamage;
        TestAssert.True(match.Cast(2, hero), "E casts on himself");
        match.Advance(0.5);
        TestAssert.True(Of(hero, "WujuStyle") is null && Of(hero, "WujuStyleSuperCharged") is not null, "the cast takes the stance and gives the supercharge");
        Refresh(match);
        TestAssert.Near(35.0, hero.AttackDamage - passive, 0.5, "the supercharge adds 70 (20 + 10 x 5) where the stance added 35 (10 + 5 x 5)");
        match.Advance(10.5);
        TestAssert.True(Of(hero, "WujuStyleSuperCharged") is null, "the supercharge lasts ten seconds");
        TestAssert.True(Of(hero, "MasterYiWujuDeactivated") is not null, "its end puts the deactivated marker back");
        match.Advance(15.0);
        TestAssert.True(Of(hero, "WujuStyle") is not null, "the stance is back when E's 25 seconds are up");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Highlander
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-highlander-kill-readies-every-spell-only-while-he-is-active")]
    public static void HighlanderKillReadiesEverySpell()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        TestAssert.True(match.Cast(3, hero), "R casts on himself");
        match.Advance(0.5);
        TestAssert.True(Of(hero, "Highlander") is not null, "Highlander is up");
        for (var slot = 0; slot < 3; slot++) hero.SpellCooldowns[slot] = 30.0;
        match.Runtime.Combat.Kill(match.Foe, hero.NetId);
        match.Advance(0.2);
        TestAssert.True(hero.SpellCooldowns.Take(4).All(cooldown => cooldown <= 0.0), "a champion kill readies every spell, Highlander included");
        match.Advance(13.0);
        TestAssert.True(Of(hero, "Highlander") is null, "Highlander ran out (12 seconds at rank three)");
        hero.SpellCooldowns[0] = 30.0;
        match.Runtime.Combat.Kill(match.FarFoe, hero.NetId);
        match.Advance(0.2);
        TestAssert.True(hero.SpellCooldowns[0] > 25.0, "after Highlander a kill resets nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Double Strike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-double-strike-lands-the-attack-after-the-seventh-hit-twice")]
    public static void EverySeventhHitSetsTheIconAndTheNextAttackStrikesTwice()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7100, 7000);
        foe.MaxHealth = foe.Health = 50000.0;
        var doubles = new List<int>();
        var previous = HitsOn(match, "Foe").Count;
        for (var attack = 1; attack <= 10; attack++)
        {
            var iconBefore = Of(hero, "DoubleStrikeIcon") is not null;
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "swing");
            match.Advance(1.2);
            var hits = HitsOn(match, "Foe").Count;
            if (hits - previous == 2)
            {
                doubles.Add(attack);
                TestAssert.True(iconBefore, "the double strike is the attack that follows the seventh hit's icon");
            }
            else TestAssert.Equal(1, hits - previous, $"attack {attack} hits once");
            previous = hits;
        }
        TestAssert.Equal(1, doubles.Count, "one double strike in ten attacks");
        TestAssert.True(Of(hero, "DoubleStrikeIcon") is null, "the icon is spent by the double strike");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Batch 381 (finding 64: no Double Strike counter on the HUD). The counter is the DoubleStrike buff: CharScriptMasterYi's CharOnHitUnit adds it on
    // every hit (BUFF stacks, MaxStack 7), and Spells/DoubleStrike.luaobj carries the BuffTextureName the HUD draws. Its first hit's add must reach
    // the holder's client as a 0xBF named DoubleStrike with count 1.
    [Check("scripting.bb-masteryi-double-strike-counter-reaches-the-client")]
    public static void DoubleStrikeCounterReachesTheClient()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7100, 7000);
        foe.MaxHealth = foe.Health = 50000.0;
        var wire = Observe(match, Team.Order, hero);
        hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "swing");
        match.Advance(1.2);
        match.Runtime.Delivery.Flush();
        var hash = SpellPackets.BuffNameHash("DoubleStrike");
        var adds = wire.Packets.Where(entry => entry.Packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[9..]) == hash).ToList();
        TestAssert.Equal(1, adds.Count, "the first hit's DoubleStrike add reaches the holder's client");
        TestAssert.Equal(1, (int)adds[0].Packet.Plaintext.Span[7], "the counter's first stack is count 1");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death and respawn
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-masteryi-death-keeps-the-wuju-marker-and-he-respawns")]
    public static void DeathKeepsTheMarkerAndHeRespawns()
    {
        using var match = Arena(5);
        var hero = match.Hero;
        TestAssert.True(match.Cast(2, hero), "E casts on himself");
        match.Advance(10.5);
        TestAssert.True(Of(hero, "MasterYiWujuDeactivated") is not null, "the marker is up once the supercharge has ended");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Advance(0.2);
        TestAssert.False(hero.Alive, "Yi is dead");
        TestAssert.True(Of(hero, "MasterYiWujuDeactivated") is not null, "the marker persists through death (PersistsThroughDeath)");
        for (var i = 0; i < 600 && !hero.Alive; i++) match.Advance(0.1);
        TestAssert.True(hero.Alive, "he respawns");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
