using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, batch 159 step 1 (Reconstruction M4): contributions are re-sampled; mutations are ticked. A
/// buff's <c>BuffOnUpdateStats</c> is a stats hook, so its payload contributions (<c>BBIncStat</c>) are re-sampled on the
/// 0.25 s grid, but its bare mutations (<c>BBIncPermanentStat</c>, a perception bubble) accumulate on the buff's own
/// clock: once per the add's <c>TickRate</c> when the add carried one, once for the add's sample when it carried none,
/// and once per a <c>BBExecutePeriodically</c> that wraps them (that periodic's own clock is what governs there).
/// <para>
/// The shipped scripts that state it are the turret spells this batch binds:
/// <c>Spells/TurretBonus.luaobj</c>'s three bare <c>BBIncPermanentStat</c> under an add with <c>TickRate 60</c> (the
/// line <c>TurretRules.BonusStacks</c> used to stack on), <c>Spells/TurretBonusHealth.luaobj</c>'s one-shot health and
/// bubble under a <c>TickRate 0</c> add that removes itself in the same run, and <c>Spells/TurretDecay.luaobj</c> /
/// <c>Spells/ResistantSkin.luaobj</c>, whose permanents sit inside a 60 s periodic. Without the gate the sample grid
/// piles TurretBonus's +6 damage up four times a second and spawns a 25 s bubble per sample of OdinGuardianBuff's hook.
/// </para>
/// Written and run by the worker that made them.
/// </summary>
public static class BbBuffMutationChecks
{
    private const string Key = "FlatPhysicalDamageMod";

    private static BbBlock Permanent(double delta) =>
        BbTest.Block("BBIncPermanentStat", ["Stat", BbTest.Ref("IncPermanentFlatPhysicalDamageMod"), "TargetVar", "Owner", "Delta", delta]);

    /// <summary>The shape of TurretBonus: a bare permanent in the stats hook, no periodic around it.</summary>
    private static BbScript Bare(string name, double delta) =>
        BbW3Rig.Buff(name, new BbEventTable("BuffOnUpdateStats", [Permanent(delta)]));

    /// <summary>The shape of TurretDecay and ResistantSkin: the permanents sit inside a 60 s periodic.</summary>
    private static BbScript InsidePeriodic(string name, double delta) =>
        BbW3Rig.Buff(name, new BbEventTable("BuffOnUpdateStats",
        [
            BbTest.Block("BBExecutePeriodically",
                ["TimeBetweenExecutions", 60.0, "TrackTimeVar", "LastTimeExecuted", "TrackTimeVarTable", "InstanceVars", "ExecuteImmediately", false],
                Permanent(delta)),
        ]));

    private static BbBuffInstance Give(BbW3Rig rig, BbScript buff, double tickRate, BbBuffAddType addType = BbBuffAddType.BUFF_REPLACE_EXISTING)
    {
        var state = rig.Module.Buffs!.Add(new BbBuffRequest(rig.Match.Hero, rig.Match.Hero, buff.Name, (int)addType,
            (int)BbBuffType.BUFF_Internal, 1, 25000.0, tickRate, false, new BbTable()));
        return rig.Module.Buffs.InstanceOf(state ?? throw new InvalidOperationException($"{buff.Name} was not added"))
               ?? throw new InvalidOperationException($"{buff.Name} has no script instance");
    }

    private static double Damage(Entity hero) =>
        hero.PermanentStatMods.TryGetValue(Key, out var value) ? value : 0.0;

    [Check("scripting.bb-buff-stats-a-bare-permanent-in-a-stats-hook-accumulates-once-per-the-adds-tick-rate")]
    public static void APermanentTicksWithTheAddsOwnClock()
    {
        var ticking = Bare("W159TickRate", 6.0);
        using var rig = new BbW3Rig(null, ticking);
        var hero = rig.Match.Hero;
        var before = Damage(hero);
        Give(rig, ticking, 60.0);
        TestAssert.Near(before, Damage(hero), 1e-9,
            "the add's own sample is before the first tick (TurretBonus stacks from its TickRate, BonusInterval's line)");
        rig.Match.Advance(59.0);
        TestAssert.Near(before, Damage(hero), 1e-9, "the 0.25 s samples between two ticks accumulate nothing");
        rig.Match.Advance(2.0);
        TestAssert.Near(before + 6.0, Damage(hero), 1e-9, "one accumulation on the first tick");
        rig.Match.Advance(60.0);
        TestAssert.Near(before + 12.0, Damage(hero), 1e-9, "and one per tick after it");
        rig.NoFailures();
    }

    [Check("scripting.bb-buff-stats-a-bare-permanent-with-no-tick-rate-accumulates-once-for-the-add-and-never-again")]
    public static void APermanentWithoutATickRateAccumulatesOnce()
    {
        var oneShot = Bare("W159OneShot", 6.0);
        using var rig = new BbW3Rig(null, oneShot);
        var hero = rig.Match.Hero;
        var before = Damage(hero);
        Give(rig, oneShot, 0.0);
        TestAssert.Near(before + 6.0, Damage(hero), 1e-9,
            "the add's own sample accumulates once (TurretBonusHealth applies its health and bubble there); twice would mean the pre-activation sample ran it too");
        rig.Match.Advance(2.0);
        TestAssert.Near(before + 6.0, Damage(hero), 1e-9, "and never again: a buff with no tick rate accumulates once and the grid piles nothing up");
        Give(rig, oneShot, 0.0, BbBuffAddType.BUFF_RENEW_EXISTING);
        TestAssert.Near(before + 6.0, Damage(hero), 1e-9,
            "a renewal keeps the mutation clock (the way it keeps StartTime), so it cannot re-accumulate");
        rig.NoFailures();
    }

    [Check("scripting.bb-buff-stats-a-permanent-inside-a-bb-execute-periodically-follows-that-periodic")]
    public static void APeriodicOwnsItsMutations()
    {
        var periodic = InsidePeriodic("W159Periodic", 6.0);
        using var rig = new BbW3Rig(null, periodic);
        var hero = rig.Match.Hero;
        var before = Damage(hero);
        Give(rig, periodic, 0.0);
        TestAssert.Near(before, Damage(hero), 1e-9,
            "the add's own sample only seeds the periodic (ExecuteImmediately false): the buff's mutation clock would have applied it here, the periodic's does not");
        rig.Match.Advance(61.0);
        TestAssert.Near(before + 6.0, Damage(hero), 1e-9, "the periodic's own 60 s line is what accumulates (TurretDecay, ResistantSkin)");
        rig.Match.Advance(60.0);
        TestAssert.Near(before + 12.0, Damage(hero), 1e-9, "and its next line");
        rig.NoFailures();
    }
}
