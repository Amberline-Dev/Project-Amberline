using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, batch 222 item 2a: a script's BUFF_Stun label is the shipped type, but a stun is the status the script writes.
/// Teleport_Turret.luaobj is labelled BUFF_Stun and writes no status, so the turret keeps firing (Teleport_Turret.luaobj has no hook and no
/// status write, verified in the batch 222 record).
/// </summary>
public static class BbStunLabelChecks
{
    [Check("scripting.bb-stun-label-without-a-status-disables-nothing")]
    public static void AStunLabelWithoutAStatusStunsNothing()
    {
        var label = BbW3Rig.Buff("W222StunLabel", new BbEventTable("OnBuffActivate", []));
        using var rig = new BbW3Rig(null, label);
        var hero = rig.Match.Hero;
        var state = rig.Module.Buffs!.Add(new BbBuffRequest(hero, hero, label.Name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Stun, 1, 2.0, 0.0, false, new BbTable()));
        TestAssert.True(state is not null && hero.Buffs.Contains(state), "the labelled buff is added");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == label.Name && buff.Type == BuffType.Stun),
            "the buff keeps its shipped Stun label");
        TestAssert.False(hero.AnyBuffOrStatus(buff => buff.DisablesActions),
            "a Stun label with no status write disables nothing (Teleport_Turret's BUFF_Stun)");
        TestAssert.True(CharacterState.Of(hero).CanAttack, "the hero can still attack");
    }
}
