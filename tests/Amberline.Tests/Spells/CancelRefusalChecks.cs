using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 209 (C06): the client refuses a plain stop (a move, an attack or a cast order) inside a windup or a channel, by
    /// the cancel-blocked test (<c>IsCancelBlocked</c>). A windup flag
    /// refuses before the cast frame whatever the caster's status. A channel flag refuses from the cast frame (the client's cast-frame step,
    /// which opens it there) to <c>mTimeSpellEnd</c> (ExtraCastTime + DesignerTotalTime), and not while the caster is
    /// suppressed or unable to cast unless the spell is CannotBeSuppressed. Shen's Stand United carries CantCancelWhileChanneling and
    /// CantCancelWhileWindingUp (its windup covers the time before the cast frame, so no shipped channel is refused early); its channel
    /// length is the shipped script's ChannelDuration.
    /// </summary>
    [Check("spells-cancel-refusal-windup-refuses-under-any-status-until-its-cast-frame")]
    public static void WindupRefusesUnderAnyStatusUntilItsCastFrame()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        match.Cast(0, 0, match.Hero(1));
        var windup = hero.CastWindupRemaining;
        TestAssert.True(windup > 0.2, $"Q's cast packet opens its windup ({windup:0.000} s)");
        TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "Silence", BuffType.Silence, 2.0, hero.NetId) is not null, "silence applied");
        TestAssert.True(SpellWindup.IsWindingUp(hero),
            "a silenced caster's flagged windup still refuses before its cast frame (IsCancelBlocked, status-independent)");
        match.Runtime.Tick(windup + 0.01);
        TestAssert.False(SpellWindup.IsWindingUp(hero), "the windup is over at its cast frame");
        match.Clear();
        match.Move(0, hero.Position + new Position2(0, 300));
        TestAssert.True(CancelRefusalRouted(hero), "a move after the cast frame is routed at once");
    }

    [Check("spells-cancel-refusal-flagged-channel-holds-inside-its-window-and-applies-after-it")]
    public static void FlaggedChannelHoldsInsideItsWindowAndAppliesAfterIt()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        CancelRefusalTimes(out var castFrame, out var channel);
        CancelRefusalCast(match, hero);
        var inside = Math.Min(0.5, channel / 2.0);
        match.Runtime.Tick(castFrame + inside);
        TestAssert.True(SpellWindup.IsWindingUp(hero), "inside the channel's window it refuses the stop");
        match.Clear();
        match.Move(0, hero.Position + new Position2(0, 300));
        TestAssert.False(CancelRefusalRouted(hero), "and holds the move: no route inside the window");
        match.Runtime.Tick(channel - inside + 0.25);
        TestAssert.False(SpellWindup.IsWindingUp(hero), "the window is over at the end of the channel");
        TestAssert.True(CancelRefusalRouted(hero), "the held move is applied after the window");
    }

    [Check("spells-cancel-refusal-unable-to-cast-channel-is-not-refused")]
    public static void UnableToCastChannelIsNotRefused()
    {
        // Silence, stun, suppression and fear all clear CanCast (the stop is not refused, IsCancelBlocked branch A): the flagged channel
        // carries no CannotBeSuppressed, so the status ends the refusal and the held move is not kept back by it.
        foreach (var (name, type) in new[] { ("Silence", BuffType.Silence), ("Stun", BuffType.Stun), ("Suppression", BuffType.Suppression), ("Fear", BuffType.Fear) })
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            CancelRefusalTimes(out var castFrame, out var channel);
            CancelRefusalCast(match, hero);
            match.Runtime.Tick(castFrame + Math.Min(0.5, channel / 2.0));
            TestAssert.True(SpellWindup.IsWindingUp(hero), $"{name}: the channel refuses the stop before the status");
            TestAssert.True(match.Runtime.Buffs.AddBuff(hero, name, type, 2.0, hero.NetId) is not null, $"{name} applied");
            TestAssert.False(SpellWindup.IsWindingUp(hero), $"{name}: a caster that cannot cast is not refused its channel stop");
        }
    }

    /// <summary>The cast frame (the packet's DesignerCastTime is the spell's CastDelay) and the channel length of Stand United, from the shipped data.</summary>
    private static void CancelRefusalTimes(out double castFrame, out double channel)
    {
        TestAssert.True(GameData.Default.SpellHasUncancellableChannel("ShenStandUnited"),
            "precondition: Stand United carries CantCancelWhileChanneling");
        castFrame = GameData.Default.Spell("ShenStandUnited").CastDelay;
        channel = GameData.Default.SpellChannelDuration("ShenStandUnited");
        TestAssert.True(castFrame > 0.05 && channel > 1.0, $"precondition: cast frame {castFrame:0.00} s and channel {channel:0.00} s");
    }

    /// <summary>
    /// Stand United's cast packet (a Shen cast: Shen has no champion module in this build), sent through the production sink, whose
    /// <c>NoteCastWindup</c> reads the spell's windup and channel flags and its ChannelDuration from the shipped data. The hero is a
    /// playable one, so the gate reads its own status (<see cref="CharacterState"/>) and nothing else is simulated.
    /// </summary>
    private static void CancelRefusalCast(Match match, Entity hero, byte flags = 0, float totalTime = 2.75f)
    {
        var at = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        var cast = new SpellCastInfo(SpellSystem.ProtocolHash("ShenStandUnited"), hero.NetId, 1, hero.NetId, 0, at, at,
            Array.Empty<SpellCastTarget>(), 0f, 0f, 3, at, 1f, 0.25f, 0f, totalTime, 0f, flags);
        match.Runtime.Spells.Effects.SendCast(hero, cast);
    }

    /// <summary>
    /// Batch 246 (replaces batch 220's <c>spells-autoattack-cast-opens-its-channel</c>): <c>ExecuteCastFrame</c>, the client's cast-frame step,
    /// clears the refusal byte and stamps the channel end only for the local hero's caster whose
    /// auto-attack flag is 0. That flag is the cast info's isAutoAttack (copied 8 bytes up; bit 0 of the packet's flags; the
    /// SpellCaster_Client accessor returns it), so a cast with the auto-attack bit never opens the channel refusal. Batch
    /// 220 read the function without that test. No shipped swing has a ChannelDuration, so only a BuildingBlocks child attack that
    /// carries one is affected; this pins the rule with Stand United's shipped channel and the bit set.
    /// </summary>
    [Check("spells-autoattack-cast-opens-no-channel")]
    public static void AnAutoAttackCastOpensNoChannel()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        CancelRefusalTimes(out var castFrame, out _);
        CancelRefusalCast(match, hero, flags: 0x01);
        TestAssert.True(hero.CastChannelEndsIn is null,
            "a cast with the auto-attack bit (0x01) opens no channel: ExecuteCastFrame arms the refusal only when +0x285 is clear");
        match.Runtime.Tick(castFrame + 0.25);
        TestAssert.False(SpellWindup.IsWindingUp(hero), "past its cast frame the auto-attack cast refuses no stop");
        match.Clear();
        match.Move(0, hero.Position + new Position2(0, 300));
        TestAssert.True(CancelRefusalRouted(hero), "a move after the auto-attack cast's frame is routed at once");

        using var control = new Match("Annie", Team.Order);
        CancelRefusalCast(control, control.Hero(0));
        TestAssert.True(control.Hero(0).CastChannelEndsIn is not null, "the same cast without the bit opens its channel");
    }

    /// <summary>
    /// Batch 246: the channel refusal closes at the packet's mTimeSpellEnd, <c>ExtraCastTime + DesignerTotalTime</c>: the client's
    /// the spell-caster update sets the refusal byte again and stops the caster once the clock passes
    /// the channel end <c>InitSpell</c> stamped from the packet (batch 207). The old floor of cast + ChannelDuration (a port guard) is
    /// gone: a packet whose total is shorter than the cast frame plus the channel ends the refusal at its total. Every sender's channel
    /// packet carries cast + ChannelDuration (BbChampionModule.Channelled, SummonersRecall), so the shipped casts are unchanged.
    /// </summary>
    [Check("spells-cancel-refusal-channel-window-ends-at-the-packets-total-time")]
    public static void ChannelWindowEndsAtThePacketsTotalTime()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        CancelRefusalTimes(out var castFrame, out var channel);
        var total = (float)(castFrame + channel / 2.0);
        CancelRefusalCast(match, hero, totalTime: total);
        TestAssert.True(hero.CastChannelEndsIn is { } ends && Math.Abs(ends - total) < 1e-6,
            $"the channel window ends at ExtraCastTime + DesignerTotalTime = {total:0.000} s, not at the cast frame plus the channel " +
            $"({castFrame + channel:0.000} s): got {hero.CastChannelEndsIn}");
    }

    private static bool CancelRefusalRouted(Entity hero) => hero.Waypoints.Count > 0 || hero.Target is not null;
}
