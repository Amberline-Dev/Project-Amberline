using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Ai;

/// <summary>
/// One script timer, the host-side store a shipped script's <c>InitTimer</c> and
/// <c>luaNeutralInitTimer</c> write. Both arm at the call itself: the delay is from that moment,
/// so a callback that arms the next phase (EndOfGame.lua's <c>DestroyNexusPhase</c>) lands it one
/// interval after its own fire, not one interval after the step that happened to run late. A
/// repeat keeps its phase across a stalled step.
/// </summary>
internal sealed class AiTimer
{
    public AiTimer(string name, double interval, bool repeat, LuaValue? closure = null)
    {
        Name = name;
        Interval = interval;
        Repeat = repeat;
        Closure = closure;
    }

    public string Name { get; }
    public double Interval { get; }
    public bool Repeat { get; }
    /// <summary>The closure <c>luaNeutralInitTimer</c> armed; null for a named <c>InitTimer</c>.</summary>
    public LuaValue? Closure { get; }
    public double? Deadline { get; set; }
    /// <summary>
    /// How much of <see cref="Interval"/> this timer has run since <c>ResetAndStartTimer</c> last
    /// started it — the counter the reference keeps per timer and
    /// <c>neutral-ai-corpus</c> records frame by frame (its <c>neutral_regen_elapsed</c> reads 0.1,
    /// 0.2 … 0.9 on the way to a fire and 0.0999999999999999 on the fire step). Null while the
    /// timer is only on the <see cref="Deadline"/> grid <c>Advance</c> advances for a lazy
    /// <c>InitTimer</c> arm.
    /// </summary>
    public double? Elapsed { get; set; }
    public bool Stopped { get; set; }
    /// <summary>Set by <c>RephaseTimers</c>: this step is the fire step, whatever the deadline says.</summary>
    public bool ForceNext { get; set; }
    /// <summary>
    /// Armed by the end-of-game ceremony after it started: the phase clock
    /// <c>IEndOfGameScript.AdvanceEndOfGame</c> owns, kept apart from the map's own economy timers
    /// (which the wave scheduler advances and the ceremony does not).
    /// </summary>
    public bool EndOfGame { get; set; }
}
