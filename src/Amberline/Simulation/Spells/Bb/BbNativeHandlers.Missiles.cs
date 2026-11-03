using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>The missile family (BBDestroyMissile, BBDestroyMissileForTarget, BBGetMissilePosFromID) of the BuildingBlocks natives (Phase 3, W22).</summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>The natives of this family <see cref="ExecuteMissile"/> runs.</summary>
    private static readonly HashSet<string> MissileBound = new(StringComparer.Ordinal)
    {
        "BBDestroyMissile", "BBDestroyMissileForTarget", "BBGetMissilePosFromID",
    };

    /// <summary>Runs <paramref name="call"/> when it belongs to this family; false lets the main switch go on.</summary>
    private bool ExecuteMissile(BbCall call)
    {
        switch (call.Function)
        {
            case "BBDestroyMissile": DestroyMissile(call); return true;
            case "BBDestroyMissileForTarget": DestroyMissileForTarget(call); return true;
            case "BBGetMissilePosFromID": GetMissilePosFromId(call); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The id the client's integer getter (the one <c>BBDestroyMissile</c> and <c>BBGetMissilePosFromID</c> call) returns:
    /// the literal <c>X</c> plus the variable <c>XVar</c> of <c>XVarTable</c>, as an exact integer. It is not <see cref="Sum"/>: that getter keeps
    /// float32 partial sums, which would round a net id (0x40000000 and up have 31 significant bits) to a neighbour. An unset or non-numeric variable
    /// reads as 0, the id of no missile.
    /// </summary>
    private static uint IdParam(BbCall call, string key)
    {
        var total = call.Value(key) is double literal ? literal : 0.0;
        if (call.Value(key + "Var") is { } name && call.Table(key + "VarTable", create: false)[name] is double value) total += value;
        return total >= 1.0 && total <= uint.MaxValue ? (uint)total : 0u;
    }

    /// <summary>
    /// <c>BBDestroyMissile</c>: reads <c>MissileID</c>, looks the missile up in the missile manager and flags it destroyed.
    /// 62 shipped calls pass <c>MissileNetworkID</c>, the id <c>TargetExecute</c> of a missile spell is given; the rest pass an id a script stored itself
    /// (Orianna's <c>CharVars.MissileID</c>, Anivia's <c>FlashMissileId</c>, Talon's <c>MISSILEID2</c>). A missile that is gone, never existed or
    /// was destroyed before makes the call a no-op (the client tests the lookup for null).
    /// </summary>
    private void DestroyMissile(BbCall call)
    {
        var id = IdParam(call, "MissileID");
        if (id != 0 && _host.Router.Missiles.Find(id) is { } missile) missile.Destroy();
    }

    /// <summary>
    /// <c>BBDestroyMissileForTarget</c>: flags every missile in the manager whose target is the unit — both halves of
    /// that manager here, the basic-attack missiles in flight at it (<see cref="ISpellCombatService.CancelPendingTargetMissiles"/>, the pair
    /// Teleport's completion makes by hand) and the shared queue's homing entries (<see cref="SpellSystem.DestroyTargetedProjectiles"/>). A
    /// script missile the queue takes out ends through <c>BbChampionModule.RetireTargetedFlights</c>, which runs its end hooks.
    /// <para>
    /// Batch 149 step 4: Zhonya's Hourglass reaches this native from its shipped <c>Spells/ZhonyasRingShield.luaobj</c> now
    /// (<c>OnBuffActivate: BBDestroyMissileForTarget(Owner)</c>), so the basic-attack half belongs to the native itself. The hand-written
    /// active used to call both halves through <c>ItemService.DestroyMissilesForTarget</c>, which is gone with it.
    /// </para>
    /// </summary>
    private void DestroyMissileForTarget(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        Spells.Combat.CancelPendingTargetMissiles(unit.NetId);
        Spells.DestroyTargetedProjectiles(unit.NetId);
    }

    /// <summary>
    /// <c>BBGetMissilePosFromID</c>: reads <c>TargetID</c>, looks the missile up and writes its position (the position vector of the
    /// object) to <c>Result</c>. A missile that is gone leaves the result variable as it was (the client writes only for a found object), which is why
    /// the shipped Flash Frost guards the call with its own <c>MissileAlive</c> flag.
    /// </summary>
    private void GetMissilePosFromId(BbCall call)
    {
        var id = IdParam(call, "TargetID");
        if (id != 0 && _host.Router.Missiles.Find(id) is { } missile) StoreOut(call, "Result", _host.Point(missile.Position));
    }
}
