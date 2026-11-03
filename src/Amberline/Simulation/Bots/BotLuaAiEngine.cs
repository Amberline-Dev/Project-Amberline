using Amberline.Core;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;

namespace Amberline.Simulation.Bots;

/// <summary>
/// The world side of a bot's shipped AI: <c>Bot.luaobj</c> and the eight <c>Task*.luaobj</c>
/// modules it loads decide what a bot does (which task is active, where it walks, what it swings
/// at, when it shops), and this engine answers their natives from the services that already own
/// movement, combat and the cast path (<see cref="BotController"/>). It is the batch-152 shape
/// again: every member is a thin projection onto that half, so the script runs the decision and
/// the engine still decides how the unit moves, swings and casts. The natives are
/// <see cref="BotController.Natives"/> — see <c>BotController.Natives.cs</c>.
/// </summary>
public sealed class BotLuaAiEngine : ILuaAiBotEngine
{
    private readonly BotController _bots;

    public BotLuaAiEngine(BotController bots) =>
        _bots = bots ?? throw new ArgumentNullException(nameof(bots));

    /// <summary>The match clock the bot's timers run on.</summary>
    public double Now => _bots.Now;

    /// <summary>
    /// Bot.luaobj names the callback <c>OnLostTarget</c>, not the shared <c>OnTargetLost</c>:
    /// its handler forwards to the active task's <c>OnTargetLost</c> method.
    /// </summary>
    public string LostTargetHook => "OnLostTarget";

    // ---- ILuaAiEngine: the shared natives ----

    public Entity? FindTargetInAcquisitionRange(Entity unit) => _bots.NativeFindTarget(unit);

    public void SetTarget(Entity unit, Entity? target) => _bots.NativeSetTarget(unit, target);

    public Entity? CurrentTarget(Entity unit) => _bots.NativeCurrentTarget(unit);

    public uint? HeldTargetId(Entity unit) => _bots.NativeHeldTargetId(unit);

    public Entity? TauntTarget(Entity unit) => _bots.NativeTauntTarget(unit);

    public bool TargetStillValid(Entity unit, Entity target) => _bots.NativeTargetStillValid(unit, target);

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && _bots.NativeTargetInAttackRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target) => _bots.NativeTurnOnAutoAttack(unit, target);

    public void TurnOffAutoAttack(Entity unit, double stopReason) => _bots.NativeTurnOffAutoAttack(unit);

    public double AiState(Entity unit) => _bots.NativeAiState(unit);

    public void SetAiState(Entity unit, double state) => _bots.NativeSetAiState(unit, state);

    // ---- ILuaAiBotEngine: the bot natives ----

    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target) =>
        _bots.NativeSetStateAndCloseToTarget(unit, state, target);

    public void SetStateAndMove(Entity unit, double state, Position2 point) =>
        _bots.NativeSetStateAndMove(unit, state, point);

    public void StopMove(Entity unit) => _bots.NativeStopMove(unit);

    public bool IsMoving(Entity unit) => _bots.NativeIsMoving(unit);

    public void BuyItem(Entity unit, int itemId) => _bots.NativeBuyItem(unit, itemId);

    public bool ServerCastSpell(Entity unit, double slot) => _bots.NativeServerCastSpell(unit, slot);

    public void UpgradeSpellsInOrder(Entity unit, IReadOnlyList<double> order) =>
        _bots.NativeUpgradeSpellsInOrder(unit, order);

    public void SetDebugSlotText(Entity unit, double slot, string text, double colour) =>
        _bots.NativeSetDebugSlotText(unit, slot, text, colour);

    public void Say(Entity unit, string text) => _bots.NativeSay(unit, text);

    public double AttackRange(Entity unit) => _bots.NativeAttackRange(unit);

    public double Mana(Entity unit) => _bots.NativeMana(unit);

    public double MaxMana(Entity unit) => _bots.NativeMaxMana(unit);

    public double Gold(Entity unit) => _bots.NativeGold(unit);

    public double FreeInventorySlots(Entity unit) => _bots.NativeFreeInventorySlots(unit);

    public double ItemPrice(Entity unit, int itemId) => _bots.NativeItemPrice(unit, itemId);

    public double CanUseSpell(Entity unit, double slot) => _bots.NativeCanUseSpell(unit, slot);

    public Position2 PositionOf(Entity unit, Entity? other) => _bots.NativePositionOf(unit, other);

    public double Health(Entity unit, Entity? other) => _bots.NativeHealth(unit, other);

    public double MaxHealth(Entity unit, Entity? other) => _bots.NativeMaxHealth(unit, other);

    public bool IsDead(Entity unit, Entity? other) => _bots.NativeIsDead(unit, other);

    public double LastTookDamageTime(Entity unit, Entity? other) =>
        _bots.NativeLastTookDamageTime(unit, other);

    public IReadOnlyList<Entity> MinionsOf(Entity unit, double team, double lane) =>
        _bots.NativeMinionsOf(unit, team, lane);

    public IReadOnlyList<Entity> HeroesOf(Entity unit, double team) => _bots.NativeHeroesOf(unit, team);

    public IReadOnlyList<uint> StructuresOf(Entity unit, double team) =>
        _bots.NativeStructuresOf(unit, team);

    public Entity? ByNetId(uint netId) => _bots.NativeByNetId(netId);

    public string NameOf(Entity? unit) => _bots.NativeNameOf(unit);

    public double TeamId(Entity unit) => _bots.NativeTeamId(unit);

    public double OtherTeamId(Entity unit) => _bots.NativeOtherTeamId(unit);

    public Position2 RegroupPos(Entity unit) => _bots.NativeRegroupPos(unit);

    public double MaxTravelDistSquared(Entity unit) => _bots.NativeMaxTravelDistSquared(unit);

    public double MinionDistanceToLane(Entity unit, Entity minion) =>
        _bots.NativeMinionDistanceToLane(unit, minion);

    public string ActiveTaskName(Entity unit, Entity? hero) => _bots.NativeActiveTaskName(unit, hero);

    public bool AreAllBarracksStarted(Entity unit, double team) =>
        _bots.NativeAreAllBarracksStarted(unit, team);

    public double TargetHealth(Entity unit) => _bots.NativeTargetHealth(unit);

    public void ReconstructModuleData(Entity unit, string name, LuaTable module) =>
        _bots.NativeReconstructModuleData(unit, name, module);
}
