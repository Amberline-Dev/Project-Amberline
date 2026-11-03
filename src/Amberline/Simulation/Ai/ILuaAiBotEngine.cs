using Amberline.Core;
using Amberline.Scripting.Lua;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The half of an AI script's natives only a bot hero asks for: <c>Bot.luaobj</c> and the eight
/// <c>Task*.luaobj</c> modules its <c>TimerHackDelayedInit</c> loads name these and no other
/// engine family does, so they are bound only for a unit whose engine carries this half and a
/// script that names one is never a silent no-op. <c>TaskWander.luaobj</c> is in the shipped data
/// and is never loaded, so its natives are not here.
/// The shared ownership set (<see cref="ILuaAiOwnedEngine"/>) is deliberately not part of this
/// half: the bot modules name <c>SetStateAndCloseToTarget</c> and <c>SetStateAndMove</c> and none
/// of the owner, wander, fear-leash or cancel-attack natives, and a bot is neither a pet nor a
/// camp monster.
/// </summary>
public interface ILuaAiBotEngine : ILuaAiEngine
{
    // ---- orders: the movement, combat and cast plumbing stays the engine's ----

    /// <summary><c>SetStateAndCloseToTarget(state, object)</c>: take the state and close on the target.</summary>
    void SetStateAndCloseToTarget(Entity unit, double state, Entity? target);

    /// <summary><c>SetStateAndMove(state, point)</c>: take the state and walk to the point.</summary>
    void SetStateAndMove(Entity unit, double state, Position2 point);

    /// <summary><c>StopMove()</c>: drop the current route.</summary>
    void StopMove(Entity unit);

    /// <summary><c>IsMoving()</c>: the hero still follows a move order.</summary>
    bool IsMoving(Entity unit);

    /// <summary><c>BuyItem(itemId)</c>: try the purchase; the scripts ignore the result.</summary>
    void BuyItem(Entity unit, int itemId);

    /// <summary><c>ServerCastSpell(slot)</c>: cast a champion slot the way a player's cast order does.</summary>
    bool ServerCastSpell(Entity unit, double slot);

    /// <summary>
    /// <c>UpgradeSpellsInOrder(4, 2, 3, 1)</c>: spend the hero's skill points in the order the
    /// arguments name. Bot.luaobj's <c>OnLevelUp</c> is the only caller.
    /// </summary>
    void UpgradeSpellsInOrder(Entity unit, IReadOnlyList<double> order);

    /// <summary><c>SetDebugSlotText(slot, text, colour)</c>: one line of the bot's debug HUD.</summary>
    void SetDebugSlotText(Entity unit, double slot, string text, double colour);

    /// <summary><c>Say(text)</c>: the bot's debug line (TaskDefendStructure's BeginTask).</summary>
    void Say(Entity unit, string text);

    // ---- the unit ----

    /// <summary><c>GetAttackRange()</c>. The Task modules read it once, in their main chunk.</summary>
    double AttackRange(Entity unit);

    /// <summary><c>GetMP()</c>: the hero's current mana.</summary>
    double Mana(Entity unit);

    /// <summary><c>GetMaxMP()</c>: the hero's mana pool.</summary>
    double MaxMana(Entity unit);

    /// <summary><c>GetGold()</c>.</summary>
    double Gold(Entity unit);

    /// <summary><c>GetNumInvSlotsAvailable()</c>.</summary>
    double FreeInventorySlots(Entity unit);

    /// <summary><c>GetItemPrice(itemId)</c>: what the shop would charge this inventory right now; 0 when it cannot be quoted.</summary>
    double ItemPrice(Entity unit, int itemId);

    /// <summary><c>CanUseSpell(slot)</c>: 0 when the slot can be cast now (TaskCastSpell's test).</summary>
    double CanUseSpell(Entity unit, double slot);

    // ---- the world ----

    /// <summary><c>GetPos([object])</c>: the hero's own position, or that object's.</summary>
    Position2 PositionOf(Entity unit, Entity? other);

    /// <summary><c>GetHP([object])</c>, <c>GetMaxHP([object])</c> and the object form of each.</summary>
    double Health(Entity unit, Entity? other);

    /// <summary><c>GetMaxHP([object])</c>.</summary>
    double MaxHealth(Entity unit, Entity? other);

    /// <summary><c>IsDead(object)</c>: the object's unit is gone or not alive.</summary>
    bool IsDead(Entity unit, Entity? other);

    /// <summary><c>GetLastTookDamageTime(object)</c>: the match time of its last damage.</summary>
    double LastTookDamageTime(Entity unit, Entity? other);

    /// <summary>
    /// <c>GetMinions(team, lane)</c>: the side's lane minions, filtered to one lane
    /// (<c>-1</c> is every lane). The list is the objects the scripts pass back in.
    /// </summary>
    IReadOnlyList<Entity> MinionsOf(Entity unit, double team, double lane);

    /// <summary><c>GetHeroes(team)</c>: the side's heroes, as objects.</summary>
    IReadOnlyList<Entity> HeroesOf(Entity unit, double team);

    /// <summary>
    /// <c>GetStructures(team)</c>: the side's structures as <b>handles</b> (net ids), the one
    /// list the scripts do not use as objects — every entry goes through <c>GetObject</c>.
    /// </summary>
    IReadOnlyList<uint> StructuresOf(Entity unit, double team);

    /// <summary><c>GetObject(handle)</c>: the object behind a handle, or nil once its unit is gone.</summary>
    Entity? ByNetId(uint netId);

    /// <summary><c>objGetName(object)</c>: the name the bot prints for it.</summary>
    string NameOf(Entity? unit);

    /// <summary><c>GetTeam()</c>, as the host's <c>TEAM_*</c> id.</summary>
    double TeamId(Entity unit);

    /// <summary><c>GetOtherTeam()</c>, as the host's <c>TEAM_*</c> id.</summary>
    double OtherTeamId(Entity unit);

    // ---- the bot's own book ----

    /// <summary><c>GetRegroupPos()</c>: where healing and the shop coincide — the team's fountain.</summary>
    Position2 RegroupPos(Entity unit);

    /// <summary><c>GetMaxTravelDistSquared()</c>: the scale of the tasks' distance terms.</summary>
    double MaxTravelDistSquared(Entity unit);

    /// <summary><c>GetMinionDistanceToLane(object)</c>: the minion's distance to its lane's route.</summary>
    double MinionDistanceToLane(Entity unit, Entity minion);

    /// <summary><c>GetActiveTaskName([hero])</c>: the name of the active task, its own or another bot's.</summary>
    string ActiveTaskName(Entity unit, Entity? hero);

    /// <summary><c>GetAreAllBarracksStarted(team)</c>.</summary>
    bool AreAllBarracksStarted(Entity unit, double team);

    /// <summary><c>GetTargetHealth()</c>: the held target's health, 0 without one.</summary>
    double TargetHealth(Entity unit);

    // ---- the per-champion data the shipped files do not carry ----

    /// <summary>
    /// NOT RETAIL: reconstructs the per-champion bot data this unit's shipped files do not carry,
    /// on the module table <c>LoadScript</c> has just produced. Retail: a champion with no own
    /// <c>Characters/&lt;Name&gt;/Scripts/TaskCastSpell.luaobj</c> runs the shared
    /// <c>DATA/Scripts/TaskCastSpell</c> for <c>LoadTask('TaskCastSpell.lua')</c>, whose
    /// <c>BeginTask</c> is a bare RETURN (see the shipped listing of TaskCastSpell), so
    /// that bot casts nothing and is not a bot at all; owner decision 22 October 2026 makes every
    /// roster champion a bot and reconstructs the missing per-champion data instead of falling
    /// back to a C# port. The shipped half of the module is left as it is.
    /// </summary>
    void ReconstructModuleData(Entity unit, string name, LuaTable module);
}
