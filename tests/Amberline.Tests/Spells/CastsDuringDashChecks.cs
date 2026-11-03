using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 663 (owner decision, 3 November 2026, "Cast at once"): a cast pressed during the hero's dash goes off at once when the dash's clip
/// is not locked, as in the original game; a dash whose script plays its clip with <c>Lock=true</c> (or leaves Lock out: true) still holds the
/// cast until the landing (playtest B3 #10, batch 659's in-order queue). The stock client refuses every animation play while the lock bit is
/// set and only the 0x2B unlock clears it (batch 520), which is why a locked dash's casts wait: their gesture would be lost.
/// </summary>
public static class CastsDuringDashChecks
{
    private static bool Dashing(Entity hero) => hero.ForcedMove is { Completed: false };

    /// <summary>The slot's cast went out after the first <paramref name="before"/> logged casts: its cast packet, or the instacast's cooldown.</summary>
    private static bool CastSince(Match match, int slot, int before) =>
        match.Log.Casts.Skip(before).Any(cast => cast.Slot == slot) || match.Hero.SpellCooldowns[slot] > 0.0;

    private static void StepUntilDashing(Match match, int steps = 30)
    {
        for (var step = 0; step < steps && !Dashing(match.Hero); step++) match.Advance(Step);
        TestAssert.True(Dashing(match.Hero), "the dash is running");
    }

    /// <summary>
    /// The hero's cast request as the client sends it (0xA2), through the session's gameplay dispatcher: unlike <c>Match.Cast</c> it meets the
    /// dispatcher's windup hold (a press during an uncancellable windup is held to that cast's frame, owner decision of 18 October 2026).
    /// </summary>
    internal static bool WireCast(Match match, int slot, Position2 at, Entity? target = null)
    {
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = (byte)(slot << 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), match.Hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)at.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)at.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target?.NetId ?? 0);
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context => accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        return accepted;
    }

    private static void NoScriptFailure(Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));

    [Check("spells-live-riven-blade-and-ki-burst-cast-at-once-during-valors-unlocked-dash")]
    public static void BladeAndKiBurstDuringValor()
    {
        using var match = new Match("Riven");
        var hero = match.Hero;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        hero.Facing = new Position2(1, 0);
        match.Foe.Position = new Position2(9000, 9000);
        // RivenFeint.SelfExecute plays Spell3 with Lock=false before its BBMove: the dash's clip is not locked.
        TestAssert.True(Blocks(Script("RivenFeint"), "SelfExecute").Any(block => block.Function == "BBPlayAnimation" &&
            block.Param("Lock") is BbValue.Bool { Value: false }), "Valor plays Spell3 unlocked");
        TestAssert.True(match.Cast(2, at: new Position2(7800, 7000)), "Valor is accepted");
        StepUntilDashing(match);
        TestAssert.False(hero.AnimationLocked, "Valor's dash leaves the clip unlocked");
        // What the scripts and the spell flags block still blocks: the dash replicates CanMove off, so Flash (its CanCast needs CanMove) and
        // Broken Wings (CantCastWhileRooted) are refused, as the client greys them, and neither queues.
        var here = new Position3(hero.Position.X + 300, 0, hero.Position.Z);
        TestAssert.False(match.Runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(0, true, here, here, 0), match.Now),
            "Flash is refused during the dash");
        TestAssert.False(match.Cast(0, at: new Position2(hero.Position.X + 300, hero.Position.Z)),
            "Broken Wings (CantCastWhileRooted) is refused during the dash");
        TestAssert.True(hero.PendingCast is null && hero.SummonerCooldowns[0] == 0.0, "neither queues nor spends a cooldown");
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(3), "Blade of the Exile is accepted during the dash");
        TestAssert.True(hero.PendingCast is null && CastSince(match, 3, before), "Blade of the Exile is cast at once, mid-dash");
        match.Advance(Step);
        TestAssert.True(Dashing(hero), "the dash still runs");
        TestAssert.True(match.Cast(1), "Ki Burst is accepted during the dash");
        TestAssert.True(hero.PendingCast is null && CastSince(match, 1, before), "Ki Burst is cast at once, mid-dash");
        TestAssert.True(Dashing(hero) && match.Log.Effects.Any(line => line.Contains("exile_W_cast", StringComparison.Ordinal)),
            "Ki Burst goes off before Valor lands: " + string.Join(" | ", match.Log.Effects));
        // Blade of the Exile takes effect at its own cast frame (0.25 s windup), whether the dash has landed by then or not.
        match.Advance(CastFrame("RivenFengShuiEngine") + 2 * Step);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "Blade of the Exile takes effect at its cast frame");
        TestAssert.False(Dashing(hero), "Valor has landed");
        TestAssert.Near(7250.0, hero.Position.X, 15.0, "and still ran its 250 units");
        NoScriptFailure(match);
    }

    [Check("spells-live-riven-blade-pressed-during-broken-wings-locked-dash-waits-for-the-landing")]
    public static void BladeDuringBrokenWingsWaits()
    {
        using var match = new Match("Riven");
        var hero = match.Hero;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        hero.Facing = new Position2(1, 0);
        match.Foe.Position = new Position2(9000, 9000);
        // RivenTriCleave.SelfExecute plays Spell1a/b/c with Lock=true before each strike's BBMove: the dash's clip is locked.
        TestAssert.True(Blocks(Script("RivenTriCleave"), "SelfExecute").Where(block => block.Function == "BBPlayAnimation")
            .All(block => block.Param("Lock") is BbValue.Bool { Value: true }), "Broken Wings plays its strikes locked");
        TestAssert.True(match.Cast(0, at: new Position2(7600, 7000)), "Broken Wings is accepted");
        StepUntilDashing(match);
        TestAssert.True(hero.AnimationLocked, "the strike's clip is locked");
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(3), "Blade of the Exile is accepted during the strike");
        TestAssert.True(hero.PendingCast is not null, "it queues behind the locked dash");
        for (var step = 0; step < 60 && Dashing(hero); step++)
        {
            TestAssert.False(CastSince(match, 3, before), "Blade of the Exile is not cast during the locked dash");
            match.Advance(Step);
        }
        TestAssert.False(Dashing(hero), "the strike lands");
        match.Advance(2 * Step);
        TestAssert.True(CastSince(match, 3, before) && hero.PendingCast is null, "Blade of the Exile is cast at the landing");
        match.Advance(0.5);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "and takes effect");
        NoScriptFailure(match);
    }

    [Check("spells-live-tristana-rapid-fire-casts-at-once-during-rocket-jump")]
    public static void RapidFireDuringRocketJump()
    {
        using var match = new Match("Tristana");
        var hero = match.Hero;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(9000, 9000);
        // RocketJump plays no clip of its own (no BBPlayAnimation): nothing locks the jump.
        TestAssert.False(Script("RocketJump").Events.SelectMany(hook => hook.Blocks).SelectMany(Flatten)
            .Any(block => block.Function == "BBPlayAnimation"), "Rocket Jump plays no clip");
        TestAssert.True(match.Cast(1, at: new Position2(7700, 7000)), "Rocket Jump is accepted");
        StepUntilDashing(match);
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(0), "Rapid Fire is accepted during the jump");
        TestAssert.True(hero.PendingCast is null && CastSince(match, 0, before), "Rapid Fire is cast at once, mid-jump");
        match.Advance(Step);
        TestAssert.True(Dashing(hero) && hero.Buffs.Any(buff => buff.Name == "RapidFire"), "and takes effect before she lands");
        match.Advance(1.5);
        TestAssert.Near(7700.0, hero.Position.X, 20.0, "the jump still lands on its point");
        NoScriptFailure(match);
    }

    [Check("spells-live-irelia-hiten-style-pressed-during-bladesurges-locked-dash-waits-for-the-landing")]
    public static void HitenStyleDuringBladesurgeWaits()
    {
        using var match = new Match("Irelia");
        var hero = match.Hero;
        var foe = match.Foe;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        foe.Position = new Position2(7550, 7000);
        // IreliaGatotsuDash.OnBuffActivate plays spell1 with Lock=true; its OnBuffDeactivate unlocks it.
        TestAssert.True(Blocks(Script("IreliaGatotsuDash"), "OnBuffActivate").Any(block => block.Function == "BBPlayAnimation" &&
            block.Param("Lock") is BbValue.Bool { Value: true }), "Bladesurge's dash plays spell1 locked");
        TestAssert.True(match.Cast(0, foe), "Bladesurge is accepted");
        StepUntilDashing(match);
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(1), "Hiten Style is accepted during the dash");
        TestAssert.True(hero.PendingCast is not null, "it queues behind the locked dash");
        for (var step = 0; step < 60 && Dashing(hero); step++)
        {
            TestAssert.False(CastSince(match, 1, before), "Hiten Style is not cast during the locked dash");
            match.Advance(Step);
        }
        TestAssert.False(Dashing(hero), "the dash lands");
        match.Advance(0.5);
        TestAssert.True(CastSince(match, 1, before) && hero.PendingCast is null, "Hiten Style is cast after the landing");
        NoScriptFailure(match);
    }

    [Check("spells-live-caitlyn-piltover-peacemaker-casts-at-once-during-the-net-flip-and-reaches-its-cast-frame")]
    public static void PeacemakerDuringNetFlip()
    {
        using var match = new Match("Caitlyn");
        var hero = match.Hero;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(9000, 9000);
        // CaitlynEntrapment.SelfExecute flips her with BBMoveAway (POSTPONE_CURRENT_ORDER) and plays no clip of its own.
        TestAssert.False(Script("CaitlynEntrapment").Events.SelectMany(hook => hook.Blocks).SelectMany(Flatten)
            .Any(block => block.Function == "BBPlayAnimation"), "90 Caliber Net plays no clip");
        // A walk for the flip to postpone.
        match.Runtime.Spells.HeroOrders!.StartMove(hero, new Position2(7000, 8000));
        match.Advance(Step);
        TestAssert.True(match.Cast(2, at: new Position2(7400, 7000)), "90 Caliber Net is accepted");
        StepUntilDashing(match);
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(0, at: new Position2(6000, 7000)), "Piltover Peacemaker is accepted mid-flip");
        TestAssert.True(hero.PendingCast is null && CastSince(match, 0, before), "it is cast at once");
        match.Advance(1.0);
        TestAssert.True(hero.SpellCooldowns[0] > 0.0, "its cast frame comes: the flip's postponed walk does not cut the windup");
        NoScriptFailure(match);
    }
}
