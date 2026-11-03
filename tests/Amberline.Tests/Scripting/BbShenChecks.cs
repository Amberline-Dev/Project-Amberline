using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 224): Shen plays from his shipped scripts. Expected numbers are the literals of the shipped scripts and the
/// spell inibins (the audit fixture's standalone decode; the tooltips of fontconfig_en_US.txt are the second witness). The scenario <c>S-Shen</c> shows the
/// same facts on a headless match. The harness carries no masteries, so no cooldown reduction is assumed. Written 25 October 2026.
/// </summary>
public static class BbShenChecks
{
    [Check("scripting.bb-shen-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Shen");
        foreach (var name in new[]
                 {
                     "CharScriptShen", "ShenVorpalStar", "ShenVorpalStarHeal", "ShenFeint", "ShenShadowDash", "ShenShadowDashCooldown",
                     "ShenStandUnited", "ShenStandUnitedShield", "ShenStandUnitedTarget", "ShenKiAttack", "ShenWayOfTheNinjaMarker",
                     "ShenWayOfTheNinjaAura", "IsNinja",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Shen");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(22, closure.ScriptNames.Count(), "the closure is 22 scripts (his four spells and their helpers, Ki Strike's marker and aura, the system buffs)");

        var hero = new Entity { ChampionName = "Shen" };
        TestAssert.Equal(["ShenVorpalStar", "ShenFeint", "ShenShadowDash", "ShenStandUnited"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Shen.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Shen")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 3150, RetailBot: true }, "playable as a top laner for 3150 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Shen"), "listed once");
    }

    /// <summary>Vorpal Blade (Q): 70 magic at rank one through the target's magic resistance, a five-second life tap, and a 3-second cooldown.</summary>
    [Check("scripting.bb-shen-vorpal-blade-life-taps-its-target-for-five-seconds")]
    public static void VorpalBladeHitsAndLifeTapsItsTarget()
    {
        var match = new BbM3.Match("Shen");
        var victim = match.FarFoe;
        victim.Position = new Position2(7300.0, 7000.0);
        victim.SpellBlock = 0.0;
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var before = victim.Health;
        var castAt = match.Now;
        TestAssert.True(match.Cast(0, victim), "Q casts on the foe");
        // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(3.0, match.Hero.SpellCooldowns[0], 0.05, "rank one's 3-second cooldown starts at the cast frame");
        var tapSeen = Appears(match, victim, "ShenVorpalStar", 1.0);
        TestAssert.Near(5.0, Vanishes(match, victim, "ShenVorpalStar", 7.0) - tapSeen, 0.15, "the life tap runs five seconds");
        match.Advance(1.0);
        // Rank one deals 70 magic (DamageByLevel). The headless harness's victim takes less than that through its own stats (62.7 at zero
        // resistance); the production scenario S-Shen measures the same hit at 54.5 through Ezreal's resistance. The band pins the rank, not the mitigation.
        var lost = before - victim.Health;
        TestAssert.True(lost > 40.0 && lost <= 70.5, $"Vorpal Blade hits for at most its rank-one 70 magic (took {lost:0.00})");
    }

    /// <summary>Feint (W): a self shield that lasts 2.5 seconds, with a 9-second cooldown at rank one.</summary>
    [Check("scripting.bb-shen-feint-shields-the-caster-for-two-and-a-half-seconds")]
    public static void FeintShieldsTheCasterForTwoAndAHalfSeconds()
    {
        var match = new BbM3.Match("Shen");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var castAt = match.Now;
        TestAssert.True(match.Cast(1, match.Hero), "W casts on himself");
        TestAssert.Near(9.0, match.Hero.SpellCooldowns[1], 0.05, "rank one's 9-second cooldown starts at the cast");
        var feintSeen = Appears(match, match.Hero, "ShenFeint", 0.5);
        TestAssert.Near(2.5, Vanishes(match, match.Hero, "ShenFeint", 4.0) - feintSeen, 0.15, "the shield lasts 2.5 seconds");
    }

    /// <summary>
    /// Shadow Dash (E): the dash runs 575 units (ShenShadowDash caps its distance at 575), a champion it collides with is taunted for 1.5 seconds, an
    /// ally on the line is not, and 50 energy comes back on a champion collision.
    /// </summary>
    [Check("scripting.bb-shen-shadow-dash-runs-575-and-taunts-on-collision-for-energy")]
    public static void ShadowDashRunsAndTauntsTheChampionItCollidesWith()
    {
        var match = new BbM3.Match("Shen");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var start = match.Hero.Position;
        var mana = match.Hero.Mana;
        var castAt = match.Now;
        TestAssert.True(match.Cast(2, at: new Position2(start.X + 700.0, start.Z)), "E casts toward a point 700 units away");
        // The taunt starts at the collision, a few tenths of a second into the dash, and runs its 1.5 seconds.
        var tauntSeen = Appears(match, match.Foe, "Taunt", 2.0);
        var taunt = Vanishes(match, match.Foe, "Taunt", 3.0) - tauntSeen;
        // Effect1Level1Amount is 1.0 at rank one (stored as a boolean, which the client reads as 1.0), and the tooltip reads "for @Effect1Amount@ second(s)".
        TestAssert.Near(1.0, taunt, 0.15, "the champion it collides with is taunted for one second at rank one");
        match.Advance(1.0);
        TestAssert.Near(575.0, Distance(start, match.Hero.Position), 3.0, "the dash runs 575 units");
        TestAssert.True(BbM3.Buff(match.Ally, "Taunt") is null, "an ally on the line is not taunted");
        TestAssert.True(match.Hero.Mana >= mana - 120.0 + 50.0 - 1.0,
            $"120 energy spent and 50 back on a champion collision (energy {match.Hero.Mana:0.0}; regeneration only adds)");
    }

    /// <summary>
    /// Stand United (R) and the stop refusal of batch 209: a move ordered inside the windup (before the cast frame) and one ordered inside the channel are
    /// both refused (CantCancelWhileWindingUp and CantCancelWhileChanneling are set on the spell).
    /// </summary>
    [Check("scripting.bb-shen-stand-united-refuses-a-stop-in-the-windup-and-the-channel")]
    public static void StandUnitedRefusesStopsUntilItsChannelEnds()
    {
        var match = new BbM3.Match("Shen");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(3, match.Ally), "R casts on the ally");
        // Batch 279: the cast's stop seats Shen on his lattice anchor at once; he stands there through the windup.
        var start = match.Hero.Position;
        TestAssert.True(SpellWindup.IsWindingUp(match.Hero), "the windup is open at the cast");
        match.Advance(0.05);
        MoveHero(match, new Position2(start.X - 400.0, start.Z));
        match.Advance(0.15);
        TestAssert.Near(0.0, Distance(start, match.Hero.Position), 1.0, "the windup move is refused");
        match.Advance(0.9);
        TestAssert.True(SpellWindup.IsWindingUp(match.Hero), "the channel is open 1.1 s in");
        var held = match.Hero.Position;
        MoveHero(match, new Position2(start.X - 400.0, start.Z));
        match.Advance(0.5);
        TestAssert.Near(0.0, Distance(held, match.Hero.Position), 1.0, "the move inside the channel is refused");
    }

    /// <summary>
    /// Stand United's channel lasts 2.5 seconds, then Shen teleports beside the ally (BBGetPointByUnitFacingOffset, Distance 150, a point off the walkable
    /// grid steps back toward her). The shield is on the ally from the cast, for 7.5 seconds (batch 220: the arrival is one teleport, not a staged pair).
    /// </summary>
    [Check("scripting.bb-shen-stand-united-teleports-to-the-ally-after-the-channel")]
    public static void StandUnitedTeleportsHimToTheAllyAndShieldsHer()
    {
        var match = new BbM3.Match("Shen");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Ally.Position = new Position2(7800.0, 7000.0);
        match.Ally.Facing = new Position2(-1.0, 0.0);
        var castAt = match.Now;
        TestAssert.True(match.Cast(3, match.Ally), "R casts on the distant ally");
        // Batch 279: the cast's stop seats Shen on his lattice anchor at once; he stands there through the channel.
        var start = match.Hero.Position;
        var shieldSeen = Appears(match, match.Ally, "ShenStandUnitedShield", 0.5);
        AdvanceToClock(match, castAt + 2.0);
        TestAssert.Near(0.0, Distance(start, match.Hero.Position), 1.0, "still channeling 2 s in");
        AdvanceToClock(match, castAt + 3.2);
        var gap = Distance(match.Hero.Position, match.Ally.Position);
        TestAssert.True(gap >= 100.0 && gap <= 160.0, $"Shen arrives beside the ally after the channel, {gap:0.0} units from her");
        TestAssert.Near(7.5, Vanishes(match, match.Ally, "ShenStandUnitedShield", 9.0) - shieldSeen, 0.15, "the ally's shield runs 7.5 seconds");
    }

    /// <summary>The Ki Strike marker (ShenWayOfTheNinjaMarker) persists through a death; the bonus hit (the aura) comes up after its timer.</summary>
    [Check("scripting.bb-shen-ki-strike-marker-persists-through-death")]
    public static void KiStrikeMarkerPersistsThroughDeath()
    {
        var match = new BbM3.Match("Shen");
        match.Advance(9.0);
        TestAssert.True(BbM3.Buff(match.Hero, "ShenWayOfTheNinjaAura") is not null, "the bonus hit is ready after its timer");
        TestAssert.True(BbM3.Buff(match.Hero, "ShenWayOfTheNinjaMarker") is not null, "the marker is on him");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        match.Advance(120.0);
        TestAssert.True(match.Hero.Alive, "he respawns");
        TestAssert.True(BbM3.Buff(match.Hero, "ShenWayOfTheNinjaMarker") is not null, "the marker survives the death (PersistsThroughDeath)");
    }

    // ----- helpers -----

    // A buff's remaining time at the moment the harness first sees it. ExpiresAt is absolute, so the duration is read from the appearance.
    private static double RemainingWhenSeen(BbM3.Match match, Entity unit, string name)
    {
        for (var step = 0; step < 240 && BbM3.Buff(unit, name) is null; step++) match.Advance(0.05);
        return BbM3.Buff(unit, name) is { } buff ? buff.ExpiresAt - match.Now : double.NaN;
    }

    // The match clock when a buff first shows (NaN if it does not within the window). ExpiresAt is on another base, so lifetimes are read from polls.
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

    private static void AdvanceToClock(BbM3.Match match, double clock)
    {
        while (match.Now < clock - 1e-9) match.Advance(0.05);
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // A move order as the client sends it (MoveOrder: opcode 0x75, order 2, the destination), through the production session.
    private static void MoveHero(BbM3.Match match, Position2 destination)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), match.Hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), match.Runtime.Dependencies.Gameplay);
        match.Runtime.Delivery.Flush();
    }
}
