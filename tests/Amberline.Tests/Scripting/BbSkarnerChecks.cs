using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (batch 318): Skarner plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the literals of the
/// shipped scripts and inibins. Impale's stats hook sets <c>SetCanAttack</c> on its Attacker (Skarner) while the victim's buff is up: a status on another
/// unit inside a stats hook is held by an internal buff of that unit (the engine rule of this batch).
/// </summary>
public static class BbSkarnerChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Skarner at rank <paramref name="rank"/> (the ultimate is capped at three), the enemy 300 away (Impale reaches 350).</summary>
    private static Match Arena(int rank = 5, string foe = "Orianna")
    {
        var match = new Match("Skarner", foeChampion: foe);
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-skarner-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosure()
    {
        var module = new BbChampionModule("Skarner");
        foreach (var name in new[] { "CharScriptSkarner", "SkarnerVirulentSlash", "SkarnerExoskeleton", "SkarnerFracture", "SkarnerImpale",
                     "SkarnerImpaleBuff", "SkarnerVirulentSlashEnergy1", "SkarnerVirulentSlashSlow", "SkarnerFractureMissile" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Skarner");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(22, closure.ScriptNames.Count(), "the closure is 22 scripts (two names it dangles: SkarnerLurk, SkarnerVirulentSlashEnergy2)");

        var hero = new Entity { ChampionName = "Skarner" };
        TestAssert.Equal(["SkarnerVirulentSlash", "SkarnerExoskeleton", "SkarnerFracture", "SkarnerImpale"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Skarner.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "Impale is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Skarner")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 6300, Id: 72, RetailBot: false },
            "playable as a jungler for 6300 IP, not a retail bot");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine rule: BBIssueOrder AI_HOLD on a hero (SkarnerImpale's TargetExecute orders its own caster)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-issue-order-hold-stands-a-hero-on-the-cast")]
    public static void ImpaleStandsTheCasterOnHold()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(3, match.Foe), "Impale is cast on the victim");
        match.Advance(0.5); // the cast takes 0.25 s; TargetExecute (the order) runs when it lands
        TestAssert.True(match.Hero.HeroHold, "the caster is on hold (BBIssueOrder AI_HOLD): he attacks only what is in range and never chases");
        TestAssert.True(match.Hero.Target is null && match.Hero.Waypoints.Count <= 1, "and he stands: no route is left");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine rule: a stats hook's status on another unit (SkarnerImpale, BuffOnUpdateStats)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-stats-hook-status-on-another-unit-is-held-by-a-carrier")]
    public static void ImpaleHoldsHisAttackThroughACarrier()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(3, match.Foe), "Impale is cast on the victim");
        match.Advance(0.5);
        TestAssert.Equal("", Failures(match), "no script failure while the victim is impaled (the stats hook may write Skarner's CanAttack)");
        TestAssert.True(Buff(match.Foe, "SkarnerImpale") is not null, "the victim carries the impale");
        TestAssert.False(CharacterState.Of(match.Hero).CanAttack, "Skarner cannot attack while he holds the victim");
        TestAssert.True(Buff(match.Hero, "BBStats_SkarnerImpale") is not null, "the status on Skarner is carried by an internal buff of his");

        // The impale's end is taken off directly (its 1.5 s timer is the batch 349 check below). Removing the buff runs the same release.
        match.Runtime.Spells.Buffs.Remove(match.Foe, Buff(match.Foe, "SkarnerImpale")!.Slot);
        match.Advance(0.1);
        TestAssert.True(CharacterState.Of(match.Hero).CanAttack, "Skarner attacks again once the impale has ended");
        TestAssert.True(Buff(match.Hero, "BBStats_SkarnerImpale") is null, "the carrier goes with the impale");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 349: the batch 318 record's open questions 1 to 3
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>SkarnerImpale</c> lasts its <c>SuppressionDuration</c> of 1.5 s. Its <c>BuffOnUpdateActions</c> drags the victim every update with a
    /// <c>BBMove</c> at 2.6 times the distance, a trip of 1 / 2.6 s whatever the distance; the engine's trip-sized buff rule (<c>BbScriptMoves.Track</c>)
    /// took that for a buff sized to its dash and stretched the impale on every update, so it never ended (9.8 s and on in the batch 318 run).
    /// The expiry (remaining duration 0) deals <c>DamagePerTick</c> again. While it holds, <c>OnBuffActivate</c> seals Fracture (slot 2, learnt) on
    /// Skarner; <c>OnBuffDeactivate</c> unseals it (the record's open 2: script = retail).
    /// </summary>
    [Check("scripting.bb-skarner-impale-ends-at-one-and-a-half-seconds-and-hits-again")]
    public static void ImpaleEndsOnItsTimerAndHitsAgain()
    {
        using var match = Arena();
        var foe = match.Foe;
        var before = foe.Health;
        TestAssert.True(match.Cast(3, foe), "Impale is cast on the victim");
        var waited = 0.0;
        while (Buff(foe, "SkarnerImpale") is null && waited < 1.0) { match.Advance(0.05); waited += 0.05; }
        TestAssert.True(Buff(foe, "SkarnerImpale") is not null, "the victim is impaled");
        var first = before - foe.Health;
        TestAssert.False(match.CanCast(2, foe).Allowed, "Fracture is sealed while he holds the victim");
        // The clock, not a sum of requested steps: Advance runs whole 30 Hz steps.
        var start = match.Now;
        while (Buff(foe, "SkarnerImpale") is not null && match.Now - start < 5.0) match.Advance(1.0 / 30.0);
        var held = match.Now - start;
        TestAssert.Near(1.5, held, 0.07, "the impale ends after its 1.5 s");
        TestAssert.True(first > 50.0, $"the cast hits ({first:0.00})");
        TestAssert.Near(2.0 * first, before - foe.Health + foe.HealthRegen * held, 3.0, "the expiry hits for the cast's amount again");
        match.Advance(0.1);
        TestAssert.True(match.CanCast(2, foe).Allowed, "Fracture is unsealed when the impale ends");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// Batch 371 (owner finding 107, "Skarner R really stuttery when moving the enemy"): Impale's update hook re-issues its <c>BBMove</c> every
    /// update at 2.6 times the distance. Each re-issue used to restart the victim's dash on the wire, 45 dash packets (opcode 0x67, one per 1/30 s
    /// update) in one 1.5 s impale, which the client showed as a stutter. The drag is one dash to the caster's side: the victim's Order view gets
    /// a single dash packet, and the victim is dragged up to him.
    /// </summary>
    [Check("scripting.bb-skarner-impale-drags-with-one-dash")]
    public static void ImpaleDragSendsOneDash()
    {
        using var match = Arena();
        var foe = match.Foe;
        var capture = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(3, foe), "Impale is cast on the victim");
        for (var step = 0; step < 90; step++) match.Advance(1.0 / 30.0);
        var sent = ForcedMoveAiChecks.Movement(capture.Packets.Select(item => item.Packet), foe.NetId, 0);
        TestAssert.Equal(1, sent.Count(item => item.Opcode == VisionPackets.DashOpcode),
            "the drag is one dash packet, not one per update");
        var gap = Math.Sqrt(Math.Pow(foe.Position.X - match.Hero.Position.X, 2) + Math.Pow(foe.Position.Z - match.Hero.Position.Z, 2));
        TestAssert.True(gap < 100.0, $"the victim is dragged up to Skarner ({gap:0} apart, 300 at the cast)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>Energize (<c>CharScriptSkarner</c> <c>CharOnHitUnit</c>): a basic attack that lands on a champion takes one second off each of his
    /// spell cooldowns (half a second on another AI unit).</summary>
    [Check("scripting.bb-skarner-energize-takes-a-second-off-his-cooldowns-per-hit-on-a-champion")]
    public static void EnergizeTakesASecondOffPerHitOnAChampion()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Foe.Position = new Position2(7100, 7000);
        TestAssert.True(match.Cast(1, hero), "Crystalline Exoskeleton starts its cooldown");
        match.Advance(0.5);
        var cooldown = hero.SpellCooldowns[1];
        TestAssert.True(cooldown > 10.0, $"the W is cooling down ({cooldown:0.00})");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "a basic attack on the champion");
        match.Advance(1.0);
        TestAssert.Near(cooldown - 1.0 - 1.0, hero.SpellCooldowns[1], 0.1, "one second of time and one of Energize");
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
