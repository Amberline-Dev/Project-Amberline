using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Buffs;

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

    /// <summary>
    /// Batch 807 (owner, 15 November 2026: a very long pause before Nunu attacks again after his E): a spell with a cast time takes the hero's
    /// order (<see cref="SpellSystem.BeginChampionCast"/>), and an attack order it takes (an explicit attack or an attack-move) is handed back
    /// once the spell's cast frame has run, instead of leaving the attack to idle acquisition after the cast gesture's hold. Retail keeps the
    /// order: Hero.luaobj's OnOrder answers the engine's AI_TEMP_CASTSPELL (an order it does not handle, proto 0.2 pc 0084-0087) with
    /// TimerCheckAttack and leaves the AI state and target alone, and the engine's order record (IssueOrders: ORDER_STATUS_POSTPONED,
    /// The client and ShouldPostponeCurrentOrderForSpellcast, stubbed in the client) postpones the current order
    /// for the cast and executes it again. Reconstruction: the instant is server code; this takes the spell's cast frame plus the settle margin
    /// of an order held behind a wind-up (<see cref="SpellWindup.SettleSeconds"/>), the first moment a 0x0D cannot replace the spell's
    /// SpellCaster before its cast frame on the client (the cast packet's total time is the cast time, so the client's SpellCaster for it
    /// ends there). A channel the cast opens holds the order until it ends; a newer order, a death or a displacement drops it.
    /// Batch 929 (players, 21 November 2026: "randomly causes him to freeze in place", Graves's Buckshot while kiting): the walk of a move
    /// order is the current order too, and it was dropped, so a hero cast while walking stood after the cast frame (the cast's stop and the
    /// client's StopActor at the cast start had ended the walk) until the idle scan attacked. The engine record postpones the current
    /// order whatever it is (the AI only ever sees AI_TEMP_CASTSPELL), so the walk is handed back too. An instacast whose script casts a
    /// windup child (Sivir's Q, Ezreal's Q and W, Graves's R) takes the order for the child's wind-up: <paramref name="castDelay"/> is that
    /// child's cast time; null (an instacast with no windup child) keeps the order with the hero and drops a hand-back, as before.
    /// Reconstruction: the instant of the hand-back, as above. <paramref name="keepWalk"/> false: the hero's route is a cast approach
    /// (the cast ends a walk into range, or replaces a cast that was walking into range), not a player's walk, and is not handed back.
    /// </summary>
    public static void PostponeForCast(SpellSystem system, Entity hero, double? castDelay, double now, bool keepWalk = true)
    {
        if (castDelay is not { } delay)
        {
            hero.CastPostponedOrder = null;
            return;
        }
        // A second cast inside the first one's wind-up finds the order already taken and moves its hand-back to its own cast frame.
        var captured = Capture(hero);
        var order = (captured is { Kind: VayneTumbleOrderKind.Move } && !keepWalk ? null : captured) ?? hero.CastPostponedOrder?.Order;
        if (order is null) return;
        var at = now + delay + SpellWindup.SettleSeconds;
        hero.CastPostponedOrder = (order, at);
        system.RunAt(at, () => ResumeAfterCast(system, hero, order));
    }

    private static void ResumeAfterCast(SpellSystem system, Entity hero, VayneTumbleOrderIntent order)
    {
        // Dropped by a newer order (SpellSystem.CancelOrder), or a later cast moved the hand-back.
        if (hero.CastPostponedOrder is not { } held || !ReferenceEquals(held.Order, order) || held.At > system.CurrentTime) return;
        if (hero.Alive && hero.ForcedMove is null && hero.ActiveScriptChannels > 0)
        {
            system.RunAt(system.CurrentTime + 1e-6, () => ResumeAfterCast(system, hero, order));
            return;
        }
        hero.CastPostponedOrder = null;
        if (!hero.Alive || HasNewerOrder(hero)) return;
        // Batch 929: a control that stops the hero's walk (a stun, a root) ends a postponed walk, as the dispatcher refuses a click under it
        // (a stationary waypoint, nothing held). An attack order comes back as before: its chase and swing have their own gates.
        if (order.Kind == VayneTumbleOrderKind.Move && !CharacterState.Of(hero).CanMove) return;
        // The hand-back is the first order after the cast frame, so it stops the cast clip a windup child leaves playing, as a player's click
        // does (SpellSystem.CancelOrder; owner playtest 25 September 2026, #7 and #22: the hero slid in it).
        system.StopCastGesture(hero);
        Resume(system, hero, order);
    }

    /// <summary>
    /// A cast queued or an order given during the displacement is the newer order (playtest B3 #10); so is an order the dispatcher holds for the
    /// landing or behind a wind-up (batch 929, <see cref="Entity.HeldPlayerOrder"/>).
    /// </summary>
    public static bool HasNewerOrder(Entity hero) =>
        hero.HeldPlayerOrder || hero.PendingCast is { Request.IsSummoner: false } ||
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
