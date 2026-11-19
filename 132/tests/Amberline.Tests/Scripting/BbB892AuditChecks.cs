using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 892 (18 November 2026, deep audit of Fizz, Volibear and Shyvana on 1.0.0.132): the server bugs the audit found under real play
/// conditions. docs/maintenance/2026-11-18-batch-892-audit-fizz-volibear-shyvana.md.
/// </summary>
public static class BbB892AuditChecks
{
    // Ranks spent through the level-up hook, as a player spends them (CharOnLevelUpSpell: Shyvana's first R rank gives 100 Fury).
    private static void Learn(Match match, int rank)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = rank;
            match.Module.OnRankUp(match.Runtime.Spells, match.Hero, slot, rank);
        }
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
    }

    // ShyvanaDoubleAttackDragon's BuffOnHitUnit cleaves every other unit within 250 in front of her with DAMAGESOURCE_ATTACK hits, then the
    // swing's own spell, ShyvanaDoubleAttackHitDragon, strikes the target a second time (TargetExecute adds its buff, whose OnBuffActivate
    // deals 80-100% of her attack damage). A cleave hit is a swing of its own for the hit hooks; it must not take the target's second strike away.
    [Check("scripting.b892-shyvana-dragon-twin-bite-strikes-its-target-twice-when-it-cleaves")]
    public static void DragonTwinBiteStrikesTwiceWhenItCleaves()
    {
        using var match = new Match("Shyvana", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Foe.Position = match.Hero.Position + new Position2(2000, 0);
        match.Advance(0.2);
        Learn(match, 1);
        TestAssert.True(match.Cast(3, at: match.Hero.Position + new Position2(400, 0)), "Dragon's Descent is cast at 100 Fury");
        match.Advance(1.5);
        TestAssert.Equal("ShyvanaDoubleAttackDragon", match.Module.SpellName(match.Hero, 0), "fixture: dragon form");
        match.Foe.Position = match.Hero.Position + new Position2(150, 0);
        var minion = LaneMinion(match, match.Hero.Position + new Position2(200, 60));
        match.Runtime.Minions!.HaltAI(minion);
        minion.Waypoints.Clear();
        match.Advance(0.2);
        var from = match.Log.Buffs.Count;
        var hits = match.Log.Hits.Count;
        TestAssert.True(match.Cast(0), "the dragon's Twin Bite is cast");
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
        for (var step = 0; step < 90 && !match.Log.Buffs.Skip(from).Any(line => line.Contains(" remove Shyvana ShyvanaDoubleAttackDragon", StringComparison.Ordinal)); step++)
            match.Advance(Step);
        match.Advance(Step * 3);
        var story = string.Join(" | ", match.Log.Hits.Skip(hits)) + " || " + string.Join(" | ", match.Log.Buffs.Skip(from));
        TestAssert.True(match.Log.Hits.Skip(hits).Any(line => line.Contains("->Minion ", StringComparison.Ordinal)), "fixture: the swing cleaves the minion: " + story);
        TestAssert.True(match.Log.Buffs.Skip(from).Any(line => line.Contains(" add Foe ShyvanaDoubleAttackHitDragon", StringComparison.Ordinal)),
            "the swing's own spell strikes the target a second time (ShyvanaDoubleAttackHitDragon on it): " + story);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // FizzJump puts FizzJumpBuffer in the E slot for the hop; the hop's end restores FizzJump (BuffOnMoveFailure sets the slot back with its
    // cooldown; a landing does it through FizzJumpDelay). A death in the hop ends the hop: its move hooks run on the live buff before the death
    // clears the buffs, so Fizz respawns with Playful / Trickster, not the buffer spell.
    [Check("scripting.b892-fizz-playful-trickster-is-back-after-a-death-in-the-hop")]
    public static void PlayfulTricksterIsBackAfterADeathInTheHop()
    {
        using var match = new Match("Fizz", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Foe.Position = match.Hero.Position + new Position2(900, 0);
        match.Advance(0.2);
        Learn(match, 1);
        TestAssert.True(match.Cast(2, at: match.Hero.Position + new Position2(300, 0)), "E is cast");
        match.Advance(0.2);
        TestAssert.Equal("FizzJumpBuffer", match.Module.SpellName(match.Hero, 2), "fixture: Fizz is in the hop");
        TestAssert.True(match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId), "Fizz dies in the hop");
        match.Advance(1.0);
        match.Hero.RespawnAt = match.Now + 0.1;
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(match.Hero), "Fizz respawns");
        match.Advance(0.5);
        TestAssert.Equal("FizzJump", match.Module.SpellName(match.Hero, 2), "the E slot holds Playful / Trickster again after the respawn");
        match.Refill();
        var start = match.Hero.Position;
        TestAssert.True(match.Cast(2, at: start + new Position2(300, 0)), "E is cast after the respawn");
        match.Advance(0.6);
        TestAssert.True(Distance(start, match.Hero.Position) > 200, $"the hop moves Fizz (moved {R(Distance(start, match.Hero.Position))})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
