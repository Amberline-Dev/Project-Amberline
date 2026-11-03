using Amberline.Core;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells;

/// <summary>
/// The one notion of "the hero's client refuses a plain stop now", for every cast path (hand-written champions, the
/// BuildingBlocks engine, summoners, Recall, item actives): the server holds a hero order for exactly the window the client
/// refuses a stop in.
/// <para>
/// The client rule, the cancel-blocked check, which the plain Stop that every move, attack and cast
/// order ends in asks first (the caster's stop). Let B be "the cast frame has not run" (the
/// caster's cast-frame flag, is 0) and A be "the channel is open" (the channel flag is 0 after the cast frame). A spell with
/// <c>CantCancelWhileWindingUp</c> refuses while B, whatever the caster's status. A spell with
/// <c>CantCancelWhileChanneling</c> refuses while A, unless the caster is suppressed or cannot cast (ActionState bit 7 or
/// bit 1 clear, <see cref="CharacterState"/>) and the spell is not <c>CannotBeSuppressed</c>: the status ends that refusal,
/// and only that one.
/// </para>
/// <para>
/// The windows are read off the cast packet (0xBD) when it arrives. The windup runs from its arrival to the cast frame
/// (the spell-caster init: mTimeCast = arrival + ExtraCastTime + DesignerCastTime). The channel
/// opens at the cast frame: the cast-frame routine clears the channel flag there when the spell has a ChannelDuration
/// (the channel duration), the cast is not an auto attack (the cast info's isAutoAttack flag is 0; batch 246) and
/// the cast routine returned 1. That function returns 0 in two cases only: the gate
/// the gate holds, the CastType (the spell data's cast type) is 1, 2 or 3 and the cast info's missile id already resolves to
/// an object; or the cast info has no SData ("SData not found for caster"). Every CastType case ends in 1. It closes at mTimeSpellEnd
/// (arrival + ExtraCastTime + DesignerTotalTime, where the update step sets the channel flag again and stops
/// the caster). A channel whose stop is accepted ends early
/// (<see cref="End"/>). Reconstruction: what the retail server does with the refused order (the blocked flag is returned to a native
/// caller in no binary we hold) is unrecovered, so an order that arrives inside a window is held, the latest wins, and applied once
/// the stop is accepted (the cast completes, then the order), as Recall's has been since batch 47 (owner decision, 18 October 2026).
/// </para>
/// </summary>
internal static class SpellWindup
{
    /// <summary>
    /// How long after the server's window a held order waits before it is applied, so the stop it sends reaches a client whose own
    /// windup is over whatever the difference between the two one-way trips (the stop is sent when the server's window ends and the
    /// client's ends one trip after its receipt of the cast; symmetric latency makes them meet exactly).
    /// </summary>
    internal const double SettleSeconds = 0.1;

    /// <summary>A cast packet that starts a windup of <paramref name="seconds"/> (the later end wins when windups overlap).</summary>
    internal static void Begin(Entity caster, double seconds) =>
        caster.CastWindupRemaining = Math.Max(caster.CastWindupRemaining, seconds);

    /// <summary>
    /// A cast packet whose spell is a channel: the channel opens <paramref name="opensIn"/> seconds from now (its cast frame) and
    /// closes <paramref name="endsIn"/> seconds from now (the packet's mTimeSpellEnd). A new cast replaces the previous channel window.
    /// </summary>
    internal static void BeginChannel(Entity caster, double opensIn, double endsIn, bool cannotBeSuppressed)
    {
        caster.CastChannelOpensIn = opensIn;
        caster.CastChannelEndsIn = endsIn;
        caster.CastChannelCannotBeSuppressed = cannotBeSuppressed;
    }

    /// <summary>
    /// The channel stopped (a stun, a lost target, a displacement, a stop the player's order was accepted for): its window ends now, so the
    /// stop the client refused is no longer refused and a held order is applied after the settle time. The windup is not touched.
    /// </summary>
    internal static void End(Entity caster)
    {
        if (caster.CastChannelEndsIn is { } ends) caster.CastChannelEndsIn = Math.Min(ends, 0.0);
    }

    /// <summary>The unit died: both windows end and nothing may wait behind them, not even the settle time. A new life starts with no window.</summary>
    internal static void Retire(Entity unit)
    {
        unit.CastWindupRemaining = -SettleSeconds;
        unit.CastChannelEndsIn = null;
    }

    /// <summary>Counts both windows down by one step; each runs on to <c>-SettleSeconds</c> and stays there.</summary>
    internal static void Tick(Entity hero, double dt)
    {
        if (hero.CastWindupRemaining > -SettleSeconds)
            hero.CastWindupRemaining = Math.Max(-SettleSeconds, hero.CastWindupRemaining - dt);
        if (hero.CastChannelEndsIn is { } ends && ends > -SettleSeconds)
        {
            hero.CastChannelOpensIn -= dt;
            hero.CastChannelEndsIn = Math.Max(-SettleSeconds, ends - dt);
        }
    }

    /// <summary>
    /// Whether the hero's client still refuses a stop. With <paramref name="settled"/> the hold of an order already waiting also
    /// covers the <see cref="SettleSeconds"/> after the server's window.
    /// </summary>
    internal static bool IsWindingUp(Entity hero, bool settled = false)
    {
        var floor = settled ? -SettleSeconds : 0.0;
        return hero.CastWindupRemaining > floor || ChannelRefuses(hero, floor);
    }

    /// <summary>
    /// The channel branch of <c>IsCancelBlocked</c>: refused while the channel is open, unless the caster is suppressed or cannot cast and
    /// the spell is not <c>CannotBeSuppressed</c> (the branch A of the listing: only the windup flag refuses then).
    /// </summary>
    private static bool ChannelRefuses(Entity hero, double floor)
    {
        if (hero.CastChannelEndsIn is not { } ends || hero.CastChannelOpensIn > 0.0 || ends <= floor) return false;
        if (hero.CastChannelCannotBeSuppressed) return true;
        var state = CharacterState.Of(hero);
        return state.CanCast && !state.Suppressed;
    }
}
