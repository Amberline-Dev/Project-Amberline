using Amberline.Core;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 159 step 1: the missing half of the client's minion target filter. <c>AffectMinions</c> alone admits every
/// minion-shaped unit; <c>SPELL_AffectBarrackOnly</c> (0x200000) keeps only the ones a barracks spawned, which is
/// what <c>Spells/TurretAssistManager.luaobj</c> asks for before it renews a turret's backdoor window. The shipped
/// minion query reads the candidate's <c>barrackSpawn</c> pointer; ours is the lane
/// minion the wave scheduler creates with a <c>MinionType</c>.
/// </summary>
public static class BbBarrackOnlyChecks
{
    private static readonly HashSet<string> MinionScan = new(StringComparer.Ordinal) { "AffectEnemies", "AffectMinions" };
    private static readonly HashSet<string> BarrackScan = new(StringComparer.Ordinal)
        { "AffectEnemies", "AffectMinions", "AffectBarrackOnly" };

    private static Entity Unit(uint netId, EntityKind kind, UnitType type, Team team, MinionType? minionType = null) =>
        new(netId, kind, type, team, new Position2(1000, 1000))
        {
            Name = kind.ToString(), Alive = true, Visible = true, MinionType = minionType,
        };

    [Check("scripting.bb-affect-barrack-only-is-the-barracks-own-minion-filter")]
    public static void AffectBarrackOnlyAdmitsOnlyTheBarracksOwnMinions()
    {
        var turret = Unit(1, EntityKind.Turret, UnitType.Turret, Team.Order);
        var lane = Unit(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, MinionType.Melee);
        var siege = Unit(3, EntityKind.Minion, UnitType.Minion, Team.Chaos, MinionType.Siege);
        var pet = Unit(4, EntityKind.Pet, UnitType.Minion, Team.Chaos);
        var clone = Unit(5, EntityKind.ShacoClone, UnitType.Minion, Team.Chaos);
        var monster = Unit(6, EntityKind.Neutral, UnitType.Monster, Team.Neutral);
        var hero = Unit(7, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        // Without the flag every minion-shaped enemy passes (AffectMinions), which is why the scan that
        // guards a turret's backdoor carries the flag as well.
        TestAssert.True(BbSpellTargeting.Admits(turret, lane, MinionScan), "AffectMinions alone: a lane minion passes");
        TestAssert.True(BbSpellTargeting.Admits(turret, pet, MinionScan), "AffectMinions alone: a pet passes");
        // Batch 286: a hero clone answers the minion type test with AffectHeroes, so it is no
        // minion to the scan at all (it passed AffectMinions before).
        TestAssert.False(BbSpellTargeting.Admits(turret, clone, MinionScan), "AffectMinions alone: a hero clone is a champion");
        TestAssert.False(BbSpellTargeting.Admits(turret, hero, MinionScan), "AffectMinions alone: a hero is not a minion");
        TestAssert.False(BbSpellTargeting.Admits(turret, monster, BarrackScan), "the flag keeps the jungle out too");
        // With it, only the two the lanes spawn.
        TestAssert.True(BbSpellTargeting.Admits(turret, lane, BarrackScan), "the barracks' own melee minion passes");
        TestAssert.True(BbSpellTargeting.Admits(turret, siege, BarrackScan), "and its siege minion");
        TestAssert.False(BbSpellTargeting.Admits(turret, pet, BarrackScan), "no barracks spawned the pet");
        TestAssert.False(BbSpellTargeting.Admits(turret, clone, BarrackScan), "nor the clone");
        TestAssert.False(BbSpellTargeting.Admits(turret, hero, BarrackScan), "nor the hero");
        // The unit-target form is the same filter (the minion target query is shared).
        TestAssert.Equal(null, BbSpellTargeting.RejectTarget(turret, lane, BarrackScan), "the lane minion is a legal target");
        TestAssert.Equal("cannot target that unit", BbSpellTargeting.RejectTarget(turret, pet, BarrackScan),
            "the pet is refused by the same rule");
    }
}
