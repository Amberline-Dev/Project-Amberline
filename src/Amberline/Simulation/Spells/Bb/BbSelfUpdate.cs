using System.Runtime.CompilerServices;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The cadence of the self-buff polls (batch 246): the character script's <c>UpdateSelfBuffStats</c> and
/// <c>UpdateSelfBuffActions</c> (<see cref="BbHookRouter.RunUpdateSelfBuffStats"/>), the item copies' (<see cref="BbItemModule.Poll"/>)
/// and the talents' (<see cref="BbLoadoutHooks.PollSelfBuffs"/>), and since batch 256 a bound non-champion unit's character script
/// (<see cref="BbUnitCharacterModule.Poll"/>: the turrets, the camps, the Worm, the siege minions). <c>SpellSystem</c> keeps one line per holder.
/// <para>
/// Reconstruction: the shipped client does not run these hooks and holds no period for them. The 1.0.0.126 executable has no hook
/// name strings; the debug symbols name <c>HandleUpdateSelfBuffActions</c> / <c>HandleUpdateSelfBuffStats</c> and
/// <c>UpdateSelfBuffResult</c> (<c>eApplyItemMods</c>, <c>eSkipItemMods</c>) with no client body, and its
/// <c>obj_AI_Base</c> layout has a server-only <c>srvNextUpdate</c> float the client never touches: the server throttled its unit
/// update, by a period the client does not hold. The shipped scripts do not settle it either (no self hook counts its calls, and the
/// periods inside them run from 0.25 s to 60 s). So the polls take the engine's buff update rule
/// (<see cref="BbBuffRuntime.ActionsUpdateInterval"/>, the <c>BuffOnUpdateActions</c> grid the shipped buff scripts are written to):
/// </para>
/// <list type="bullet">
/// <item>a bare hook runs on the holder's 0.25 s line: the first poll at once, then one per interval on that phase, a line the clock
/// passed skipped and never repaid;</item>
/// <item>a hook that owns a <c>BBExecutePeriodically</c> runs every update and its own period decides (the exemption
/// <c>BbBuffRuntime</c> gives <c>ActionsCarriesPeriodic</c>): the item auras' 0.9 s re-add of a 1.0 s buff would otherwise land on the
/// buff's own end once a second.</item>
/// </list>
/// <para>
/// On the step cadence a bare re-add with <c>BUFF_RENEW_EXISTING</c> (the utility mastery's marker, Talents/135.luaobj) sent one in-place
/// renewal per 33 ms step, about 30 a second per observer (batch 241); on this line it is four.
/// </para>
/// <para>
/// Batch 720 (owner decision, 8 November 2026; Reconstruction): the same line is every unit's buff update pass. A buff's bare
/// <c>BuffOnUpdateActions</c> runs on its holder's line (<c>BbBuffRuntime.OnTick</c>), the first time on the holder's next pass after
/// the add, and a unit no hero, pet or character poll steps gets its line from its first spell step (<c>SpellSystem.Tick</c>'s sweep).
/// </para>
/// </summary>
internal static class BbSelfUpdate
{
    public const double Interval = BbBuffRuntime.ActionsUpdateInterval;

    private static readonly ConditionalWeakTable<BbEventTable, StrongBox<bool>> Periodic = new();

    /// <summary>Whether the <paramref name="hook"/> of <paramref name="script"/> runs on this update: on its line, or every update when it owns a periodic.</summary>
    public static bool Runs(BbScript script, string hook, bool due) => due || CarriesPeriodic(script, hook);

    public static bool CarriesPeriodic(BbScript script, string hook) =>
        script.Event(hook) is { } table && Periodic.GetValue(table, t => new StrongBox<bool>(t.Blocks.Any(HasPeriodic))).Value;

    private static bool HasPeriodic(BbBlock block) =>
        block.Function == "BBExecutePeriodically" || block.SubBlocks.Any(HasPeriodic);
}
