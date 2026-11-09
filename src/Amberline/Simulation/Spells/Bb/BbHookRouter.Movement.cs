using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>Hook dispatch added in Phase 3 (W22): the movement hooks of <see cref="BbHookRouter"/>.</summary>
internal sealed partial class BbHookRouter
{
    /// <summary>
    /// The hooks of the script buff whose displacement (<c>BBMove</c> and its siblings) is over: <c>BuffOnCollisionTerrain</c> when the
    /// move stopped at a wall the unit's script tracks (<c>BBStartTrackingCollisions</c>), <c>BuffOnMoveSuccess</c> when it arrived or
    /// <c>BuffOnMoveFailure</c> when it did not, then <c>BuffOnMoveEnd</c>. Owner is the moved unit (the holder of the buff), Attacker and
    /// Caster the buff's caster. Reconstruction: the stock client has none of these hook names; the order follows the shipped scripts
    /// (<c>BlindMonkQTwoDash</c> deals its landing damage in MoveSuccess and removes itself in MoveEnd, <c>VayneCondemnMissile</c> removes
    /// itself in CollisionTerrain and stuns in its deactivation). A hook that removed the buff ends the sequence for it.
    /// </summary>
    public void RunMoveHooks(BbBuffInstance instance, bool success, bool terrainCollision)
    {
        var caster = _host.Spells.World.Get(instance.CasterNetId);
        void RunLive(string hook)
        {
            if (instance.Owner.Buffs.Contains(instance.State)) RunBuffHook(instance, hook, caster);
        }
        if (terrainCollision) RunLive("BuffOnCollisionTerrain");
        RunLive(success ? "BuffOnMoveSuccess" : "BuffOnMoveFailure");
        RunLive("BuffOnMoveEnd");
    }

    /// <summary>
    /// <c>BuffOnCollision</c>: the moving unit (Owner, the holder of the buff) touches <paramref name="other"/> (Target) during a move whose
    /// script tracks collisions. Attacker and Caster are the buff's caster.
    /// </summary>
    public void RunCollision(BbBuffInstance instance, Entity other)
    {
        if (!instance.Owner.Buffs.Contains(instance.State)) return;
        var caster = _host.Spells.World.Get(instance.CasterNetId);
        var scope = NewScope(instance.Owner, caster, other, caster, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
        Run(instance.Script, "BuffOnCollision", scope, BuffFrame(instance, "BuffOnCollision", null, inDamage: false));
    }
}
