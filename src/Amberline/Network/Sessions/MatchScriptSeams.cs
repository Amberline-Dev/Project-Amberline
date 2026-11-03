using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Network.Sessions;

/// <summary>
/// The shipped-script seams a match installs, in one place so every composition that builds a match world runs the same
/// scripts the game runs: the production root and the replay harnesses that stand in for it (batch 173). A harness that
/// listed the seams itself went stale the moment a new one was added and recorded a world the game does not have.
/// <para>
/// <see cref="InstallUnitCharacters"/> is the non-champion units' own character scripts (batch 159 part 2: the turrets',
/// the lane minions' and the camp monsters' <c>CharScript&lt;character&gt;.luaobj</c>), bound one unit at a time through
/// <see cref="BindUnitCharacter"/> as the map's spawn services create them. <see cref="AttachHeroOrders"/> is a
/// champion's <c>Hero.luaobj</c> (batch 163): the order system is its engine half, and the buff lifecycle raises its
/// taunt and fear callbacks through <see cref="BuffLifecycle.Heroes"/>.
/// </para>
/// </summary>
public static class MatchScriptSeams
{
    /// <summary>
    /// Installs the non-champion units' character scripts and returns the module plus the sink the match's gameplay hooks
    /// must fan out through (production's <c>hooks.UnitHooks</c>). A character with no shipped script is never bound by
    /// <see cref="BindUnitCharacter"/> and keeps the hand-written rules.
    /// </summary>
    public static (BbUnitCharacterModule Module, IBbHookSink Sink) InstallUnitCharacters(
        SpellSystem spells, GameData? data = null, Action<BbScriptFailure>? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var module = new BbUnitCharacterModule(data);
        spells.UnitScripts = module;
        return (module, module.Bind(spells, failureSink));
    }

    /// <summary>Production's <c>BindUnitCharacter</c> seam: binds one created unit to its shipped character script.</summary>
    public static bool BindUnitCharacter(BbUnitCharacterModule module, Entity unit, string character) =>
        module.BindUnit(unit, character);

    /// <summary>Binds a map's camp monsters as they spawn.</summary>
    public static void BindUnitCharacters(JungleScheduler scheduler, BbUnitCharacterModule module)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(module);
        scheduler.BindUnitCharacter = (unit, character) => BindUnitCharacter(module, unit, character);
    }

    /// <summary>Binds a lane's minions as they spawn (the siege and super minion characters ship scripts).</summary>
    public static void BindUnitCharacters(WaveScheduler waves, BbUnitCharacterModule module)
    {
        ArgumentNullException.ThrowIfNull(waves);
        ArgumentNullException.ThrowIfNull(module);
        waves.BindUnitCharacter = (unit, character) => BindUnitCharacter(module, unit, character);
    }

    /// <summary>Binds the map's own units (the turrets) as the session creates them.</summary>
    public static void BindUnitCharacters(DuelSession session, BbUnitCharacterModule module)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(module);
        session.BindUnitCharacter = (unit, character) => BindUnitCharacter(module, unit, character);
    }

    /// <summary>
    /// Attaches a champion's <c>Hero.luaobj</c> (batch 163): the order system is the script's engine half, installed on the
    /// spell system's hero-order seam, and the buff lifecycle raises the script's taunt and fear callbacks on its host.
    /// </summary>
    public static HeroAttackOrderSystem AttachHeroOrders(SpellSystem spells, BuffLifecycle buffs,
        WorldState world, MovementSystem movement, CombatService combat, ISpellEffectSink effects,
        Func<Entity, Entity, bool> visible, ISpellBuffService spellBuffs, GameData? data = null,
        IRandomSource? random = null, FidelityTrace? fidelityTrace = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(buffs);
        var orders = new HeroAttackOrderSystem(world, movement, combat, effects, data ?? GameData.Default,
            visible, spellBuffs, random, fidelityTrace) { RecallRemainingOf = spells.RecallRemaining };
        spells.AttachHeroOrders(orders);
        buffs.Heroes = orders.Host;
        return orders;
    }
}