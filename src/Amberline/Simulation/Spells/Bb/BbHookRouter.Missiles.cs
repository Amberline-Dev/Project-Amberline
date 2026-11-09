using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Hook dispatch added in Phase 3 (W22): the missile hooks of <see cref="BbHookRouter"/>. Every timing here is a Reconstruction: the stock
/// client has the callbacks as empty stubs (the launch and missile-end handlers) and a spell data key for the update distance, so which hook sees which variable comes
/// from the shipped scripts that read them; see docs/scripting/building-blocks-engine.md, "Missile hooks".
/// </summary>
internal sealed partial class BbHookRouter
{
    /// <summary>The live missiles of this engine by net id (<c>BBDestroyMissile</c>, <c>BBGetMissilePosFromID</c>).</summary>
    internal BbMissileRegistry Missiles { get; } = new();

    /// <summary>The host this router runs on (the checks reach the natives' host through it).</summary>
    internal BbSpellHost SpellHost => _host;

    /// <summary>
    /// <c>BuffOnLaunchMissile</c> of every script buff on the missile's caster, when a missile leaves it: Owner and Attacker are the holder, Target is
    /// the unit the missile flies at (Ashe's FrostShot tests it for a tower: <c>HandleOnLaunchMissile</c> hands the callback the target and the
    /// missile id), <c>missileId</c> is the missile's net id (Orianna's four ball buffs keep it in <c>CharVars</c>, Anivia's Flash Frost and Talon's
    /// Rake in their own tables). Any missile counts, a spell's or an attack's, so Janna's Tailwind buff ends when Zephyr leaves.
    /// </summary>
    internal void RunLaunchMissile(BbMissileFlight flight, Entity? target)
    {
        foreach (var instance in Buffs.WithHook(flight.Caster, "BuffOnLaunchMissile"))
        {
            var scope = NewScope(instance.Owner, instance.Owner, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["missileId"] = (double)flight.MissileNetId;
            Run(instance.Script, "BuffOnLaunchMissile", scope, MissileBuffFrame(instance, "BuffOnLaunchMissile", flight));
        }
    }

    /// <summary>
    /// <c>SpellOnMissileUpdate</c> of the spell script that launched the missile: Owner, Attacker and Caster are the casting hero, Target is the unit
    /// a homing missile flies at (nil for a line), <c>MissilePosition</c> the missile's point; <c>GetCastInfo</c> answers the cast's rank and target
    /// position (Galio's Righteous Gust faces its minion at <c>GetCastSpellTargetPos</c>).
    /// </summary>
    internal void RunMissileUpdate(BbMissileFlight flight, Position2 at, Entity? target)
    {
        if (flight.Script.Event("SpellOnMissileUpdate") is null) return;
        var scope = MissileSpellScope(flight, target);
        scope.Vars["MissilePosition"] = _host.Point(at, flight.Height);
        RunInSpell(flight.Script, "SpellOnMissileUpdate", scope, MissileSpellFrame(flight, "SpellOnMissileUpdate"));
    }

    /// <summary>
    /// <c>SpellOnMissileEnd</c> of the spell script, then <c>BuffOnMissileEnd</c> of every script buff on the caster: <c>MissileEndPosition</c> is where
    /// the missile ended (a line's end or the corpse a homing missile reached), <c>SpellName</c> the spell that launched it, Target the unit a homing
    /// missile reached. The buff hook sees every missile of the holder, which is why the shipped scripts test <c>SpellName</c> (Master Yi's
    /// Alpha Strike, Anivia's Flash Frost, Gragas's Barrel Roll, Talon's Rake). The spell hook comes first.
    /// </summary>
    internal void RunMissileEnd(BbMissileFlight flight, Entity? target) =>
        Buffs.DeferDeactivations(() => RunMissileEndHooks(flight, target));

    private void RunMissileEndHooks(BbMissileFlight flight, Entity? target)
    {
        if (flight.Script.Event("SpellOnMissileEnd") is not null)
        {
            var scope = MissileSpellScope(flight, target);
            scope.Vars["MissileEndPosition"] = _host.Point(flight.Position, flight.Height);
            scope.Vars["SpellName"] = flight.SpellName;
            RunInSpell(flight.Script, "SpellOnMissileEnd", scope, MissileSpellFrame(flight, "SpellOnMissileEnd"));
        }
        foreach (var instance in Buffs.WithHook(flight.Caster, "BuffOnMissileEnd"))
        {
            var scope = NewScope(instance.Owner, instance.Owner, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["MissileEndPosition"] = _host.Point(flight.Position, flight.Height);
            scope.Vars["SpellName"] = flight.SpellName;
            // The buff script deals the damage of an end hook (Flash Frost's burst): its own spell-shield flags are the ambient ones, as for a
            // buff's update ticks (the owner decision of 18 October 2026, evening).
            RunInSpell(instance.Script, "BuffOnMissileEnd", scope, MissileBuffFrame(instance, "BuffOnMissileEnd", flight));
        }
    }

    private BbScope MissileSpellScope(BbMissileFlight flight, Entity? target)
    {
        var scope = NewScope(flight.Caster, flight.Caster, target, flight.Caster, flight.Rank);
        scope.SpellVars = SpellVars(flight.Script);
        return scope;
    }

    private static BbHookFrame MissileSpellFrame(BbMissileFlight flight, string hook) =>
        new(hook, flight.Script.Name, flight.Caster)
        {
            Caster = flight.Caster, SpellSlot = flight.Slot, SpellRank = flight.Rank, ManaCost = flight.ManaCost,
            CastPosition = flight.CastPosition, CastEndPosition = flight.CastEndPosition, SpellName = flight.SpellName,
            MissileNetId = flight.MissileNetId,
        };

    private BbHookFrame MissileBuffFrame(BbBuffInstance instance, string hook, BbMissileFlight flight) =>
        new(hook, instance.Name, instance.Owner)
        {
            Caster = _host.Spells.World.Get(instance.CasterNetId), SpellSlot = instance.SpellSlot, SpellRank = instance.SpellRank,
            ManaCost = instance.ManaCost, Buff = instance, SpellName = flight.SpellName, MissileNetId = flight.MissileNetId,
        };
}
