using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using System.IO;

namespace Amberline.Simulation.Bots;

// Server-side equivalents of the natives the tasks call (SetStateAndMove,
// SetStateAndCloseToTarget, TurnOffAutoAttack, StopMove, ServerCastSpell,
// BuyItem, GetTarget, IsMoving ...) plus the robustness guards.
public sealed partial class BotController
{
    /// <summary>Reconstruction: IsMoving() is a server native; here the unit has an unfinished route.</summary>
    private static bool IsMoving(Entity hero) =>
        hero.Waypoints.Count > 0 &&
            (hero.Waypoints.Count > 1 || DistanceSquared(hero.Position, hero.Waypoints[0]) > 9.0) ||
        hero.Target is Position2 target && DistanceSquared(hero.Position, target) > 9.0;

    /// <summary>Reconstruction: GetTarget() is a server native; here the unit the bot is currently attacking, if it still lives.</summary>
    private Entity? CurrentTarget(Entity hero) =>
        hero.AttackTarget is uint id && _world.Get(id) is { Alive: true } target ? target : null;

    /// <summary>Reconstruction: GetTargetHealth() is a server native; 0 without a target.</summary>
    private double CurrentTargetHealth(Entity hero) => CurrentTarget(hero)?.Health ?? 0.0;

    /// <summary>
    /// SetStateAndCloseToTarget(AI_ATTACK...): close in on the target and keep attacking it.
    /// Reconstruction: a live attack on another target is only replaced after a second (the
    /// script retargets every tick, which in a busy wave would re-order almost every
    /// decision).
    /// </summary>
    private void EnsureAttack(Entity hero, BotState state, Entity target, double now)
    {
        if (hero.AttackTarget == target.NetId && hero.HeroPursuit is { } pursuit &&
            pursuit.TargetNetId == target.NetId)
            return;
        if (hero.AttackTarget is not null && now - state.LastAttackOrderAt < AttackRetargetSeconds)
            return;
        _heroOrders.StartAttack(hero, target);
        // The task list, not Hero.lua's idle acquisition, chooses the next target.
        hero.HeroAutoAcquire = false;
        // The attack replaced any earlier move order, so that goal is no longer in flight.
        state.LastMoveGoal = null;
        state.LastAttackOrderAt = now;
        state.OrdersIssued++;
    }

    /// <summary>
    /// Reconstruction: TurnOffAutoAttack(reason) is a server native. The scripts pass STOPREASON_TARGET_LOST, _MOVING or
    /// _IMMEDIATELY; what each reason does differently is unrecovered, so all stop the attack the same way (the wind-up
    /// is interrupted) without touching the movement order.
    /// </summary>
    private void TurnOffAutoAttack(Entity hero)
    {
        if (hero.AttackTarget is null && hero.HeroPursuit is null) return;
        if (_combat.InterruptBasicAttack(hero) > 0 && _effects.IsReferenceReady(hero))
            _effects.SendStopAttack(hero);
        hero.AttackTarget = null;
        hero.HeroPursuit = null;
        hero.HeroAutoAcquire = false;
    }

    /// <summary>
    /// Reconstruction of the engine's lost-target handling. Bot.luaobj OnLostTarget forwards to the active
    /// task's OnTargetLost, and all eight shipped handlers are a single RETURN, so the scripts themselves do
    /// nothing; the engine ends the attack when the target dies or leaves sight. Here an attack target that died
    /// or left the team's sight is dropped together with the pursuit route, so the bot never chases the last
    /// known position of a unit it can no longer see. A8 audit, seeded 60-minute matches (seeds 21 to 24, ten
    /// bots): keeping the last-known-position chase raised deaths by 37 % and cost 5 % of the last hits.
    /// </summary>
    private void DropLostTarget(Entity hero, BotState state)
    {
        if (hero.HeroPursuit is not { } pursuit) return;
        if (_world.Get(pursuit.TargetNetId) is { Alive: true } target && IsVisibleTarget(hero, target))
            return;
        TurnOffAutoAttack(hero);
        StopMove(hero, state);
    }

    /// <summary>SetStateAndMove(state, position).</summary>
    private void IssueMove(Entity hero, BotState state, Position2 goal, double now)
    {
        if (MoveHeldBack(hero)) return;
        // Spells and ordinary attack orders can replace the route after this
        // controller's last decision, so compare the actual endpoint.
        var routeEndpoint = hero.Waypoints.Count > 0 ? hero.Waypoints[^1] : hero.Target;
        var toleranceSquared = SameGoalTolerance * SameGoalTolerance;
        if (routeEndpoint is Position2 current && DistanceSquared(current, goal) <= toleranceSquared)
            return;
        // No order spam: an identical destination is not ordered again within a
        // second, even if the previous order produced no route.
        if (state.LastMoveGoal is Position2 last && DistanceSquared(last, goal) <= toleranceSquared &&
            now - state.LastMoveAt < MoveRepeatSeconds)
            return;

        ClearOrders(hero);
        var route = _movement.SetMoveOrder(hero.NetId, goal, other => _visible(hero, other));
        state.LastMoveGoal = goal;
        state.LastMoveAt = now;
        state.OrdersIssued++;
        if (_effects.IsReferenceReady(hero))
            _effects.SendWaypoints(hero, route.Count > 0 ? route : _movement.InstallStop(hero),
                _world.Clock.SyncId());
    }

    /// <summary>StopMove().</summary>
    private void StopMove(Entity hero, BotState state)
    {
        var moving = hero.Waypoints.Count > 0 || hero.Target is not null;
        hero.Waypoints.Clear();
        hero.Target = null;
        if (!moving) return;
        state.OrdersIssued++;
        // The stop's one-point path is installed on receipt: seated on its anchor at once.
        if (_effects.IsReferenceReady(hero))
            _effects.SendWaypoints(hero, _movement.InstallStop(hero), _world.Clock.SyncId());
    }

    private void ClearOrders(Entity hero)
    {
        _spells.CancelOrder(hero);
        _combat.InterruptBasicAttack(hero);
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.HeroAutoAcquire = false;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.LastAttackStopPoint = null;
    }

    /// <summary>What makes <see cref="IssueMove"/> give no route now: a displacement, a recall, a windup, a cast lock or a root.</summary>
    private bool MoveHeldBack(Entity hero) =>
        hero.ForcedMove is not null || _spells.RecallRemaining(hero) > 0.0 || SpellWindup.IsWindingUp(hero) ||
        hero.ChampionState?.BlocksActions == true || MovementBlocked(hero);

    private static bool MovementBlocked(Entity hero)
    {
        foreach (var buff in hero.BuffsAndScriptStatus())
            if (buff.Payload.ActionDisabled || buff.Payload.MovementDisabled) return true;
        return false;
    }

    /// <summary>
    /// Whether a hero is stunned, casting a summon, recalling or winding up an uncancellable
    /// spell (<see cref="SpellWindup"/>, the window a player's order is held in). Retail runs the
    /// arbiter regardless and the natives refuse the order of a disabled unit, so a BeginTask
    /// issued under a stun is lost (except a walk, which batch 581 holds: <see cref="NativeSetStateAndMove"/>)
    /// — that is where this test sits now, on the order
    /// natives (BotController.Natives.cs). An earlier revision held the whole 250 ms update back
    /// instead (a retired deviation, no longer in the code: it kept the active task and delayed the switch, so its BeginTask was
    /// not wasted; the A8 audit, seeded 60-minute matches, seeds 21 to 24, ten bots, found that
    /// left the bots 26 % fewer seconds with a route and no progress). The arbiter is the
    /// script's own <c>TimerUpdate</c> and is no longer the engine's to hold back.
    /// Reconstruction (batch 568): a running channel (<see cref="Entity.ActiveScriptChannels"/>) holds the order natives too. Their bodies
    /// end in the player's order path (<see cref="ClearOrders"/>: <c>SpellSystem.CancelOrder</c>, a fresh order's channel stop, and
    /// <c>HandleCastRequest</c>, an accepted cast's), so a task's 250 ms re-order ended the bot's own Drain, Death Lotus or Absolute Zero
    /// a step after it opened. No shipped Bot or Task script reads a channel or cast state (the shipped Bot and Task listings), and the
    /// same natives from Hero.luaobj's timers never end a channel (<c>HeroAttackOrderSystem.Blocked</c> and <c>Chase</c>), so a
    /// channel runs to its own end and the task's next order after it is the one that counts.
    /// </summary>
    private bool IsActionLocked(Entity hero)
    {
        if (hero.ChampionState?.BlocksActions == true || _spells.RecallRemaining(hero) > 0.0 ||
            SpellWindup.IsWindingUp(hero) || hero.ActiveScriptChannels > 0)
            return true;
        foreach (var buff in hero.BuffsAndScriptStatus())
            if (buff.DisablesActions)
                return true;
        return false;
    }

    /// <summary>
    /// ServerCastSpell(slot). Reconstruction: the native lives in the game server and its body is
    /// not in the stock client. The cast is issued through the same SpellSystem path as a
    /// player's cast order, aimed at the AI's target (at the bot itself when the spell only
    /// affects allies). A target outside the spell's range therefore makes the hero walk into
    /// range and cast, as an out-of-range player click does, instead of skipping the slot.
    /// </summary>
    private bool ServerCastSpell(Entity hero, Entity target, int slot, double now)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var subject = attempt == 0 ? target : hero;
            var position = new Position3(subject.Position.X, 0.0, subject.Position.Z);
            var validation = _spells.CanCast(hero, slot, subject.NetId, position, now);
            if (validation.Failure == SpellCastFailure.Target)
            {
                // An enemy-target rejection can mean an ally or self spell.
                if (attempt == 0) continue;
                return false;
            }
            // CanUseSpell already passed, so only the geometry gates are left. A range failure is
            // queued as an approach-and-cast by the request handler; anything else is a refusal.
            if (!validation.Allowed && validation.Failure != SpellCastFailure.Range) return false;
            return _spells.HandleCastRequest(hero,
                new SpellCastRequest(slot, false, position, position, subject.NetId), now);
        }
        return false;
    }

    private static int FreeInventorySlots(Entity hero)
    {
        var free = 0;
        for (var index = 0; index < hero.Inventory.Count; index++)
            if (hero.Inventory[index] is null) free++;
        return free;
    }

    /// <summary>GetItemPrice(item): what the shop would charge this inventory right now.</summary>
    private bool TryItemPrice(Entity hero, int itemId, out double price)
    {
        price = 0.0;
        if (itemId <= 0 || _catalog.Get(itemId) is null) return false;
        try { price = Inventory.CurrentCost(_catalog, itemId, hero.Inventory); }
        catch (InvalidDataException) { return false; }
        return double.IsFinite(price);
    }

    /// <summary>BuyItem(item): the result is ignored, as in the script.</summary>
    private void BuyItem(Entity hero, BotState state, int itemId)
    {
        if (!_shop.CanShop(hero)) return;
        if (!_shop.IsOpen(hero)) _ = _shop.Open(hero, new ShopOpenRequest(0));
        state.Measure.PurchaseAttempts++;
        if (_shop.Buy(hero, new BuyItemRequest(hero.NetId, checked((uint)itemId))).Accepted) state.Measure.Purchases++;
        state.OrdersIssued++;
    }
}
