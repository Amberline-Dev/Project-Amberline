using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests;

/// <summary>
/// The loadout script owner (batch 140 step 0): a script owner that is not a champion, with <c>TalentLevel</c> for a
/// talent, per-copy <c>InstanceVars</c> and sale/move semantics for an inventory copy, a summoner slot, and the avatar
/// variables the talent feeders write — an unset <c>AvatarVars</c> entry reading as 0.
/// </summary>
public static class BbLoadoutOwnerChecks
{
    private static BbScript Shipped(string relativePath) =>
        BbScriptLoader.Load(GameDataPaths.Default, relativePath).Script ??
        throw new InvalidOperationException($"no script at {relativePath}");

    // Batch 303 (was scripting.bb-loadout-avatar-vars-default-zero): AvatarVars reads an unset entry as Lua nil like
    // every other variable table. The client holds no avatar table of its own, BuildingBlocksBase's GetTable hands back
    // nil, and the summoner scripts test their bonuses with CO_NOT_EQUAL 0 / GetMathNumber, which are written for nil.
    [Check("scripting.bb-loadout-avatar-vars-default-nil")]
    public static void AvatarVarsReadsUnsetEntriesAsNil()
    {
        var owner = new BbLoadoutOwner();
        TestAssert.True(owner.AvatarVars["NeverWritten"] is null);
        TestAssert.True(owner.AvatarVars["GhostMovementBonus"] is null);
        owner.AvatarVars["GhostMovementBonus"] = 0.06;
        TestAssert.Near(0.06, (double)owner.AvatarVars["GhostMovementBonus"]!, 1e-12);
        // A written-and-removed entry is unset again, as in every other variable table.
        owner.AvatarVars["GhostMovementBonus"] = null;
        TestAssert.True(owner.AvatarVars["GhostMovementBonus"] is null);
        var scope = BbScope.ForHook();
        TestAssert.True(scope.CharVars is not null);
        TestAssert.True(scope.CharVars!["NeverWritten"] is null);
    }

    [Check("scripting.bb-loadout-set-vars-by-level-writes-avatar-vars")]
    public static void SetVarsByLevelWritesTheHerosAvatarVars()
    {
        // Talents/130.luaobj SetVarsByLevel: GhostMovementBonus = {0.06}, GhostDurationBonus = {1.5, 3.0} by level.
        var owner = new BbLoadoutOwner();
        var binding = owner.SetTalent(Shipped("Talents/130.luaobj"), 130, 1);
        TestAssert.True(binding is not null);
        var scope = owner.NewScope(binding!, owner: OracleCore.Owner);
        TestAssert.Near(1.0, (double)scope.Vars["TalentLevel"]!, 1e-12);
        TestAssert.True(ReferenceEquals(scope.AvatarVars, owner.AvatarVars));
        new BbInterpreter(new OracleHost(new OracleCore())).ExecuteHook(binding!.Script, "SetVarsByLevel", scope);
        TestAssert.Near(0.06, (double)owner.AvatarVars["GhostMovementBonus"]!, 1e-12);
        TestAssert.Near(1.5, (double)owner.AvatarVars["GhostDurationBonus"]!, 1e-12);
    }

    [Check("scripting.bb-loadout-talent-level")]
    public static void TalentLevelIsTheRankAndRankZeroHasNoBinding()
    {
        var owner = new BbLoadoutOwner();
        var script = Shipped("Talents/113.luaobj");
        var first = owner.SetTalent(script, 113, 3);
        TestAssert.True(first is not null);
        TestAssert.Near(3.0, (double)owner.NewScope(first!, OracleCore.Owner).Vars["TalentLevel"]!, 1e-12);
        // Talents/113.luaobj SetVarsByLevel: InstanceVars.DamageBlock = {1, 1.5, 2} by TalentLevel.
        var scope = owner.NewScope(first!, OracleCore.Owner);
        new BbInterpreter(new OracleHost(new OracleCore())).ExecuteHook(first!.Script, "SetVarsByLevel", scope);
        TestAssert.Near(2.0, (double)first.InstanceVars["DamageBlock"]!, 1e-12);
        // Reallocating starts the talent over; rank 0 leaves no binding at all (135's BBElse would fire at rank 0).
        var second = owner.SetTalent(script, 113, 1);
        TestAssert.True(second is not null && !ReferenceEquals(first, second));
        TestAssert.True(second!.InstanceVars["DamageBlock"] is null);
        TestAssert.True(owner.SetTalent(script, 113, 0) is null);
        TestAssert.True(owner.Talent(113) is null);
    }

    [Check("scripting.bb-loadout-item-instance-vars-move-and-sale")]
    public static void ItemInstanceVarsTravelWithTheCopyAndLeaveWithIt()
    {
        var owner = new BbLoadoutOwner();
        var script = Shipped("Items/3001.luaobj"); // any shipped item script serves as the binding's script
        var copy = owner.SetItem(script, 1001, 2);
        copy.InstanceVars["Charges"] = 3.0;
        owner.MoveItem(2, 5);
        var moved = owner.Item(5);
        TestAssert.True(moved is not null);
        TestAssert.True(ReferenceEquals(copy, moved));
        TestAssert.True(owner.Item(2) is null);
        TestAssert.Equal(5, moved!.Slot);
        TestAssert.Near(3.0, (double)moved.InstanceVars["Charges"]!, 1e-12);
        // A swap keeps both copies' InstanceVars on their own copy.
        var other = owner.SetItem(script, 3004, 0);
        other.InstanceVars["Charges"] = 9.0;
        owner.MoveItem(5, 0);
        TestAssert.True(ReferenceEquals(copy, owner.Item(0)));
        TestAssert.True(ReferenceEquals(other, owner.Item(5)));
        TestAssert.Near(3.0, (double)copy.InstanceVars["Charges"]!, 1e-12);
        TestAssert.Near(9.0, (double)other.InstanceVars["Charges"]!, 1e-12);
        // Sale drops the copy and its variables; a later copy of the same item starts clean.
        owner.RemoveItem(0);
        TestAssert.True(owner.Item(0) is null);
        TestAssert.True(!copy.Active);
        var fresh = owner.SetItem(script, 1001, 0);
        TestAssert.True(fresh.InstanceVars["Charges"] is null);
    }

    [Check("scripting.bb-loadout-summoner-slot")]
    public static void SummonerSlotsAreTheirselves()
    {
        var owner = new BbLoadoutOwner();
        var script = Shipped("Spells/SummonerFlash.luaobj");
        var slot = owner.SetSummoner(script, 4, 1);
        TestAssert.True(ReferenceEquals(slot, owner.Summoner(1)));
        TestAssert.True(owner.Summoner(0) is null);
        TestAssert.Equal(BbLoadoutKind.Summoner, slot.Kind);
        TestAssert.Near(1.0, (double)owner.NewScope(slot, OracleCore.Owner).Vars["TalentLevel"]!, 1e-12);
    }
}
