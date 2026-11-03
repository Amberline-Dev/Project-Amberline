using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Shaco runs from his shipped scripts (phase 3, W33), so a check that needs a Jack in the Box builds the one
/// <c>JackInTheBoxInternal.OnBuffDeactivate</c> builds: the unit of <c>ShacoBoxService.SpawnForScript</c> plus the <c>JackInTheBox</c> buff the
/// script adds to it (90 s, BUFF_CombatEnchancer, the fear time of the W rank, the placement point). The buff script is the box's AI.
/// </summary>
internal static class ShacoScriptSupport
{
    private static readonly double[] FearByRank = [0.5, 0.75, 1.0, 1.25, 1.5];
    private static readonly double[] BonusHealthByRank = [0, 100, 200, 300, 400];

    /// <summary>A box of <paramref name="owner"/> (a Shaco of the match, so his module is bound) at <paramref name="position"/>, asleep for its first two seconds.</summary>
    public static Entity SpawnBox(MatchRuntime runtime, Entity owner, Position2 position, int rank = 1)
    {
        var module = runtime.Dependencies.ChampionDispatcher.FindModule("Shaco") as BbChampionModule
            ?? throw new InvalidOperationException("the match has no script-driven Shaco");
        var box = runtime.Spells.ScriptPets!.Boxes.SpawnForScript(owner, position, runtime.Combat.CurrentTime);
        var vars = new BbTable
        {
            ["TargetPos"] = BbTable.Point(box.Position.X, runtime.Navigation.HeightAt(box.Position), box.Position.Z),
            ["BonusHealth"] = BonusHealthByRank[rank - 1],
            ["FearDuration"] = FearByRank[rank - 1],
        };
        module.Buffs!.Add(new BbBuffRequest(box, owner, "JackInTheBox", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_CombatEnchancer, 1, 90.0, 0.0, false, vars));
        return box;
    }

    private static readonly double[] ExplosionByRank = [300.0, 450.0, 600.0];

    /// <summary>
    /// A Shaco clone of <paramref name="owner"/> at <paramref name="position"/> with the <c>HallucinateFull</c> buff the applicator gives it
    /// (18 s; <c>DamageAmount</c> 300 / 450 / 600 of the R rank, <c>DamageDealt</c> 0.75, <c>DamageTaken</c> 1.5): its deactivation is the explosion.
    /// </summary>
    public static Entity SpawnClone(MatchRuntime runtime, Entity owner, Position2 position, int rank = 1)
    {
        var module = runtime.Dependencies.ChampionDispatcher.FindModule("Shaco") as BbChampionModule
            ?? throw new InvalidOperationException("the match has no script-driven Shaco");
        var clone = runtime.Clones!.Spawn(owner, owner, position, CloneKind.ShacoClone, 500.0, "HallucinateFull", 18.0, "Shaco");
        var vars = new BbTable { ["DamageAmount"] = ExplosionByRank[rank - 1], ["DamageDealt"] = 0.75, ["DamageTaken"] = 1.5 };
        module.Buffs!.Add(new BbBuffRequest(clone, owner, "HallucinateFull", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 18.0, 0.0, false, vars));
        return clone;
    }

    /// <summary>A box is awake while it holds <c>EndKill</c>: <c>JackInTheBox.BuffOnUpdateActions</c> adds it (5 s) when it wakes and its deactivation kills the box.</summary>
    public static bool IsAwake(Entity box) => box.Buffs.Any(buff => buff.Name == "EndKill");

    /// <summary>The game time the box's <c>JackInTheBox</c> buff started (<c>BBGetBuffStartTime</c>): its 2 s arming and its 0.5 s shot grid count from here.</summary>
    public static double Born(MatchRuntime runtime, Entity box)
    {
        var module = runtime.Dependencies.ChampionDispatcher.FindModule("Shaco") as BbChampionModule
            ?? throw new InvalidOperationException("the match has no script-driven Shaco");
        return module.Buffs!.Find(box, "JackInTheBox")?.StartTime ?? throw new InvalidOperationException("the box has no JackInTheBox buff");
    }
}
