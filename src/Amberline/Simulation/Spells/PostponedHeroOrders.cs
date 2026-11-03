using Amberline.Core;

namespace Amberline.Simulation.Spells;

/// <summary>
/// The player order a scripted displacement postpones (<c>MovementOrdersType = POSTPONE_CURRENT_ORDER</c> of <c>BBMove</c>: Vayne's
/// Tumble and Condemn, Nidalee's Pounce, Renekton's dash ...). The hero's walk, attack-move or explicit attack is remembered as a
/// destination or target, never as a route generated before the dash, and handed back once the move is over unless the player gave a
/// newer order. Moved out of <c>VayneModule</c> (since deleted: Vayne runs from her scripts) so that the hand-written modules and the script engine (<c>BBMove</c>) share one
/// implementation; the order record keeps its historical name <see cref="VayneTumbleOrderIntent"/>.
/// </summary>
internal static class PostponedHeroOrders
{
    /// <summary>The order to hand back after the displacement, or null when there is none (or the hero is already displaced).</summary>
    public static VayneTumbleOrderIntent? Capture(Entity hero)
    {
        // A move replacing another forced move is an interruption, not a
        // postponement of the older displacement's order.
        if (hero.ForcedMove is not null) return null;
        if (hero.HeroPursuit is HeroPursuitState pursuit)
            return new VayneTumbleOrderIntent
            {
                Kind = VayneTumbleOrderKind.Attack,
                TargetNetId = pursuit.TargetNetId,
                LastKnownPosition = pursuit.LastKnownPosition,
                AutoAcquire = hero.HeroAutoAcquire,
            };
        if (hero.AttackMoveGoal is Position2 attackMove)
            return new VayneTumbleOrderIntent
            {
                Kind = VayneTumbleOrderKind.AttackMove,
                Destination = attackMove,
                AutoAcquire = hero.HeroAutoAcquire,
            };
        var destination = hero.Target ??
            (hero.Waypoints.Count > 0 ? hero.Waypoints[^1] : (Position2?)null);
        if (destination is Position2 move)
            return new VayneTumbleOrderIntent
            {
                Kind = VayneTumbleOrderKind.Move,
                Destination = move,
                AutoAcquire = hero.HeroAutoAcquire,
            };
        return null;
    }

    /// <summary>Hands the postponed order back to the hero.</summary>
    public static void Resume(SpellSystem system, Entity hero, VayneTumbleOrderIntent? order)
    {
        if (order is null || hero.ForcedMove is not null) return;
        switch (order.Kind)
        {
            case VayneTumbleOrderKind.Move:
                SendMoveOrder(system, hero, order.Destination);
                break;
            case VayneTumbleOrderKind.AttackMove:
                if (system.HeroOrders is { } heroOrders)
                    heroOrders.StartAttackMove(hero, order.Destination);
                break;
            case VayneTumbleOrderKind.Attack:
                if (system.HeroOrders is { } orders)
                    orders.ResumeExplicitAttack(hero, order.TargetNetId,
                        order.LastKnownPosition, order.AutoAcquire);
                break;
        }
    }

    /// <summary>A cast queued or an order given during the displacement is the newer order (playtest B3 #10).</summary>
    public static bool HasNewerOrder(Entity hero) =>
        hero.PendingCast is { Request.IsSummoner: false } ||
        hero.Target is not null || hero.Waypoints.Count > 0 ||
        hero.AttackTarget is not null || hero.HeroPursuit is not null ||
        hero.AttackMoveGoal is not null;

    public static void SendMoveOrder(SpellSystem system, Entity hero, Position2 destination)
    {
        // Hero.luaobj's OnOrder takes the move (proto 0.2 pc 0074-0083): it drops the fight and the
        // hero never acquires before arrival. A spell that issues the order gets the same walk.
        if (system.HeroOrders is { } heroOrders)
        {
            heroOrders.StartMove(hero, destination);
            return;
        }
        var route = system.Movement.SetMoveOrder(hero.NetId, destination,
            other => system.CanSeeTarget(hero, other));
        if (system.Effects.IsReferenceReady(hero))
            system.Effects.SendWaypoints(hero, route.Count > 0 ? route : system.Movement.InstallStop(hero),
                system.World.Clock.SyncId());
    }
}
