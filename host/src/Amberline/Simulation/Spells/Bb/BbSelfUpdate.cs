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
/// update, by a period the client does not hold. The polls take the engine's buff update rule
/// (<see cref="BbBuffRuntime.ActionsUpdateInterval"/>, the <c>BuffOnUpdateActions</c> grid the shipped buff scripts are written to):
/// every self hook runs on its holder's 0.25 s line, the first poll at once, then one per interval on that phase, a line the clock
/// passed skipped and never repaid.
/// </para>
/// <para>
/// Batch 897 (owner decision, 18 November 2026): that includes a hook that owns a <c>BBExecutePeriodically</c>. Batch 246 polled such a
/// hook on every 33 ms step so its period was tested on the step clock; a self hook's bare blocks then ran 30 times a second
/// (Shyvana's <c>BBSetPARColorOverride</c>/<c>BBClearPARColorOverride</c>, one ChangePARColorOverride each, 30 to 60 a second to
/// every client in view). The shipped self hooks are written to the quarter second: of the 125 (1.0.0.132) and 112 (1.0.0.126)
/// character, item and talent self hooks that own a periodic, the shortest period asked is 0.25 s (CharScriptCassiopeia's
/// death-particle re-add), apart from CharScriptKarthus' period 0 (its <c>TickTimeVar</c> names a variable nothing sets), which is
/// "every run"; and all but 11 periods are whole quarter seconds. The 11 are the death-particle
/// re-adds at 0.4 s (25000 s buffs), the Tear family's 0.9/0.95 s stack timers (Items/3003, 3004, 3070), the 0.9 s aura re-adds of
/// Items/3001 and 3110 (1.0 s buffs), Items/3124's 2.85 s and the unused Talents/TestScript. The periodic's own clock accumulates
/// (<c>BbBaseLibrary.ExecutePeriodically</c>: due = last due + period), so on this line those keep their mean period and land on
/// the first line at or after each due time; a 0.9 s aura re-add comes 1.0 or 0.75 s after the last, which a renewal of its 1.0 s
/// buff on the line still meets (the 1.0 s periods of Items/3105 and 3152 already did).
/// </para>
/// <para>
/// On the step cadence a bare re-add with <c>BUFF_RENEW_EXISTING</c> (the utility mastery's marker, Talents/135.luaobj) sent one in-place
/// renewal per 33 ms step, about 30 a second per observer (batch 241); on this line it is four.
/// </para>
/// <para>
/// Batch 720 (owner decision, 8 November 2026; Reconstruction): the same line is every unit's buff update pass. A buff's bare
/// <c>BuffOnUpdateActions</c> runs on its holder's line (<c>BbBuffRuntime.OnTick</c>), the first time on the holder's next pass after
/// the add, and a unit no hero, pet or character poll steps gets its line from its first spell step (<c>SpellSystem.Tick</c>'s sweep).
/// A buff hook that owns a periodic still runs on every update (<c>BbBuffInstance.ActionsCarriesPeriodic</c>): batch 897 changed the
/// self polls only.
/// </para>
/// </summary>
internal static class BbSelfUpdate
{
    public const double Interval = BbBuffRuntime.ActionsUpdateInterval;
}
