using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 724 (owner, 9 November 2026: Mordekaiser stuck on the Dominion fountain after he died feared by Nocturne's E, "check all fears -
/// negative effects should disappear on death"). A champion who dies under a control comes back free of it: no control row, no status bit, no
/// fear walk and no feared AI. The duels run on the production composition (<see cref="BbM3.Match"/>), the control comes from the shipped
/// spell scripts, the death is <c>CombatService.Kill</c>, the respawn is the match's own timer, and the walk is a stock client move order
/// through the production dispatch.
/// </summary>
public static class DeathClearsControlChecks
{
    /// <summary>The foe's seat: <see cref="BbM3.Match"/> authenticates its seats 1 to 4 in roster order and the foe is the third.</summary>
    private const long FoeConnection = 3;

    /// <summary>Hero.luaobj's <c>AI_FEARED</c> and <c>AI_TAUNTED</c>.</summary>
    private const double AiFeared = HeroLuaAiEngine.StateFeared, AiTaunted = HeroLuaAiEngine.StateTaunted;

    /// <summary>The client's status word bits for a taunt (5) and a fear (6) (<see cref="CharacterState.ActionWord"/>).</summary>
    private const uint TauntedBit = 1u << 5, FearedBit = 1u << 6;

    /// <summary>The scripted champion at 7000,7000 with every slot at rank one, Mordekaiser 150 in front of him.</summary>
    private static Match Duel(string champion)
    {
        var match = new Match(champion, foeChampion: "Mordekaiser");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Refill();
        match.Advance(1.0);
        return match;
    }

    /// <summary>What the server holds for <paramref name="unit"/>: life, Hero.luaobj's state and live timers, the forced move, the status word, the buffs.</summary>
    private static string State(Match match, Entity unit)
    {
        var orders = match.Runtime.Spells.HeroOrders!;
        var timers = orders.Host.ExistingInstanceFor(unit)?.Timers.Select(timer => timer.Name) ?? [];
        return $"alive={unit.Alive} ai={orders.Engine.AiState(unit)} timers=[{string.Join(",", timers)}] " +
               $"forced={unit.ForcedMove?.Effect ?? "none"} word=0x{CharacterState.ActionWord(unit):X} buffs=[{Names(unit)}]";
    }

    private static bool FearTimerLive(Match match, Entity unit) =>
        match.Runtime.Spells.HeroOrders!.Host.ExistingInstanceFor(unit)?.Timers.Any(timer => timer.Name == "TimerFeared") == true;

    /// <summary>The 0xC1 paths the observer was sent for <paramref name="unit"/> at or after <paramref name="from"/>.</summary>
    private static int PathsFor(WireCapture capture, Entity unit, double from) => capture.Packets.Count(sent =>
        sent.At >= from && sent.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
        BitConverter.ToUInt32(sent.Packet.Plaintext.Span[1..5]) == unit.NetId);

    /// <summary>Kills the foe, lets the match's own respawn timer run out (his AI keeps stepping while he is dead) and steps a little past it.</summary>
    private static void DieAndRespawn(Match match, Entity foe, List<string> timeline)
    {
        TestAssert.True(match.Runtime.Combat.Kill(foe, match.Hero.NetId), "the foe dies");
        timeline.Add("dead: " + State(match, foe));
        match.Runtime.Combat.ScheduleRespawn(foe);
        var waited = 0.0;
        while (!foe.Alive && waited < 120.0)
        {
            match.Advance(0.5);
            waited += 0.5;
        }
        TestAssert.True(foe.Alive, "the foe respawns on his own timer");
        timeline.Add("respawned: " + State(match, foe));
    }

    /// <summary>
    /// A stock move order (wire order 2) through the production dispatch, two seconds of walking, and the distance it closed toward the click.
    /// </summary>
    private static double Walk(Match match, Entity foe, List<string> timeline)
    {
        var goal = foe.Position + new Position2(-350, -350);
        var before = Distance(foe.Position, goal);
        var packet = ClientGameplayPackets.MoveOrder(foe.NetId, 2, goal);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(FoeConnection, 1, packet.Plaintext.Span, match.Runtime.Dependencies.Gameplay),
            "the move order is dispatched");
        match.Advance(2.0);
        timeline.Add("after the walk order: " + State(match, foe));
        return before - Distance(foe.Position, goal);
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// The owner's report. Each shipped fear (Nocturne's <c>NocturneUnspeakableHorror</c> at two seconds of its tether, Fiddlesticks'
    /// <c>Terrify</c>, Shaco's armed <c>JackInTheBox</c>; all call <c>BBApplyFear</c>, whose global <c>Fear.luaobj</c> has no
    /// <c>PersistsThroughDeath</c>) lands on Mordekaiser,
    /// he dies feared and respawns. Hero.luaobj's <c>OnFearBegin</c> armed its one-second <c>TimerFeared</c>, which wanders with
    /// <c>SetStateAndMove(AI_FEARED)</c> and never asks whether a fear is left: only <c>OnFearEnd</c> stops it. After the respawn he holds no
    /// fear, his AI is not feared, no fear walk is sent, and a move order walks him.
    /// </summary>
    [Check("spells-death-ends-every-fear-and-the-respawned-hero-walks")]
    public static void DeathEndsTheFearAndTheRespawnedHeroWalks()
    {
        // Every fear runs, so a red run names each one that fails.
        var failures = new List<string>();
        foreach (var (champion, slot) in new[] { ("Nocturne", 2), ("FiddleSticks", 0), ("Shaco", 1) })
            try { DieFeared(champion, slot); }
            catch (InvalidOperationException failure) { failures.Add(failure.Message); }
        TestAssert.True(failures.Count == 0, string.Join(" || ", failures));
    }

    private static void DieFeared(string champion, int slot)
    {
        using var match = Duel(champion);
        var foe = match.Foe;
        var client = Observe(match, Team.Chaos, foe, 9003);
        var timeline = new List<string>();
        TestAssert.True(match.Cast(slot, foe), $"{champion}: the fear spell casts on Mordekaiser");
        for (var waited = 0.0; Buff(foe, "Fear") is null && waited < 4.0; waited += 0.1) match.Advance(0.1);
        match.Advance(0.2);
        timeline.Add("feared: " + State(match, foe));
        TestAssert.True(Buff(foe, "Fear") is not null && FearTimerLive(match, foe),
            $"{champion}: the fear landed and Hero.luaobj armed TimerFeared: {string.Join(" | ", timeline)}");
        DieAndRespawn(match, foe, timeline);
        match.Advance(0.2);
        var watchFrom = match.Now;
        match.Advance(2.0);
        timeline.Add("two seconds later: " + State(match, foe) + $" paths={PathsFor(client, foe, watchFrom)}");
        var story = $"{champion}: {string.Join(" | ", timeline)}";
        TestAssert.True(Buff(foe, "Fear") is null && (CharacterState.ActionWord(foe) & FearedBit) == 0, "no fear row or bit after the respawn: " + story);
        TestAssert.False(FearTimerLive(match, foe), "the death ended Hero.luaobj's TimerFeared: " + story);
        TestAssert.True(match.Runtime.Spells.HeroOrders!.Engine.AiState(foe) != AiFeared, "the respawned AI is not feared: " + story);
        TestAssert.True(foe.ForcedMove is null, "no fear walk holds the respawned hero: " + story);
        TestAssert.Equal(0, PathsFor(client, foe, watchFrom), "his client is sent no wander path while he stands: " + story);
        var closed = Walk(match, foe, timeline);
        story = $"{champion}: {string.Join(" | ", timeline)}";
        TestAssert.True(closed > 300.0, $"a move order walks the respawned hero ({closed:0} of 495 closed): " + story);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{champion}: no script failed");
    }

    /// <summary>
    /// The same death before the fear: Mordekaiser dies one second into Nocturne's tether. The tether (<c>NocturneUnspeakableHorror</c>, no
    /// <c>PersistsThroughDeath</c>) leaves with the death and its <c>OnBuffDeactivate</c> only plays the break (<c>Feared</c> is still false), so no
    /// fear reaches the corpse or the next life.
    /// </summary>
    [Check("spells-death-in-the-nocturne-tether-leaves-no-fear")]
    public static void DeathInTheTetherLeavesNoFear()
    {
        using var match = Duel("Nocturne");
        var foe = match.Foe;
        var timeline = new List<string>();
        TestAssert.True(match.Cast(2, foe), "E casts on Mordekaiser");
        match.Advance(1.0);
        timeline.Add("tethered: " + State(match, foe));
        TestAssert.True(Buff(foe, "NocturneUnspeakableHorror") is not null && Buff(foe, "Fear") is null,
            "one second in, the tether holds and no fear yet: " + string.Join(" | ", timeline));
        DieAndRespawn(match, foe, timeline);
        match.Advance(2.2);
        timeline.Add("two seconds later: " + State(match, foe));
        var story = string.Join(" | ", timeline);
        TestAssert.True(Buff(foe, "NocturneUnspeakableHorror") is null && Buff(foe, "Fear") is null, "neither the tether nor a fear outlives the death: " + story);
        TestAssert.False(FearTimerLive(match, foe), "no TimerFeared: " + story);
        TestAssert.True(match.Runtime.Spells.HeroOrders!.Engine.AiState(foe) != AiFeared && foe.ForcedMove is null, "no feared AI or fear walk: " + story);
        var closed = Walk(match, foe, timeline);
        TestAssert.True(closed > 300.0, $"a move order walks the respawned hero ({closed:0} closed): " + string.Join(" | ", timeline));
    }

    /// <summary>
    /// The second lock: a taunt. <c>BBApplyTaunt</c> adds the global <c>Taunt.luaobj</c>, which has no <c>PersistsThroughDeath</c>, so a death
    /// removes it like the stun and the fear. Galio's <c>GalioIdolOfDurand</c> taunts from a buff on Galio, so nothing on the victim takes the
    /// taunt away when the victim dies; Rammus' <c>PuncturingTaunt</c> also puts <c>PuncturingTauntArmorDebuff</c> on him, whose
    /// <c>OnBuffDeactivate</c> removes the taunt. Either way the corpse carries no taunt row and no taunt bit, and a revive in the same breath
    /// starts a life with no taunt and an AI that takes orders.
    /// </summary>
    [Check("spells-death-ends-the-taunt-and-the-revived-hero-walks")]
    public static void DeathEndsTheTauntAndTheRevivedHeroWalks()
    {
        foreach (var (champion, slot) in new[] { ("Galio", 3), ("Rammus", 2) })
        {
            using var match = Duel(champion);
            var foe = match.Foe;
            var timeline = new List<string>();
            TestAssert.True(match.Cast(slot, foe), $"{champion}: the taunt casts on Mordekaiser");
            match.Advance(0.3);
            timeline.Add("taunted: " + State(match, foe));
            TestAssert.True(Buff(foe, "Taunt") is not null, $"{champion}: the taunt landed: " + string.Join(" | ", timeline));
            TestAssert.True(match.Runtime.Combat.Kill(foe, match.Hero.NetId), "the foe dies");
            timeline.Add("dead: " + State(match, foe));
            var story = $"{champion}: {string.Join(" | ", timeline)}";
            TestAssert.True(Buff(foe, "Taunt") is null, "the death removes the taunt: " + story);
            TestAssert.Equal(0u, CharacterState.ActionWord(foe) & TauntedBit, "the corpse's status word carries no taunt: " + story);
            match.Runtime.Combat.ScheduleRespawn(foe);
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(foe), "an immediate revive");
            match.Advance(0.2);
            timeline.Add("revived: " + State(match, foe));
            story = $"{champion}: {string.Join(" | ", timeline)}";
            TestAssert.True(Buff(foe, "Taunt") is null && (CharacterState.ActionWord(foe) & TauntedBit) == 0, "the revived hero is not taunted: " + story);
            TestAssert.True(match.Runtime.Spells.HeroOrders!.Engine.AiState(foe) != AiTaunted, "his AI is not taunted: " + story);
            var closed = Walk(match, foe, timeline);
            TestAssert.True(closed > 300.0, $"a move order walks the revived hero ({closed:0} closed): {champion}: " + string.Join(" | ", timeline));
        }
    }
}
