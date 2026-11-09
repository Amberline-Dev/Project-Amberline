using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

internal sealed class ProductionGameplayDispatcher
{
    private readonly Dictionary<uint, (Position2 Position, double At)> _emoteHold = new();
    // Batch 437: an emote whose hero's stop leaves now plays later (PlayDueEmotes); batch 679: at DueAt, simulation time.
    private readonly Dictionary<uint, (ChampionEmote Emote, double DueAt)> _dueEmotes = new();

    /// <summary>
    /// Batch 679 (owner, 4 November 2026: "It should first stop the champ, wait a moment then start emoting"): how long a walking or
    /// attacking hero's emote waits behind its stop. Reconstruction: the retail server's delay is not in the shipped files (no script
    /// handles emotes; the client's 0x4C sender, the client, stops nothing locally and only sets its emote flag), so the value is the
    /// owner's "moment": long enough that the stop reaches the client in an earlier datagram and its one-point seat has stopped the
    /// hero before the 0x48 starts the clip (a stop that lands with or after the clip cancels it), short enough to read as one action.
    /// </summary>
    internal const double EmoteSettleSeconds = 0.25;

    /// <summary>
    /// Batch 691/691b: the input locks the map's level script holds on the clients (<see cref="LevelScriptHost.InputLocks"/>). A client's
    /// command one of them covers is refused: INPUT_ABILITIES a skill-up (0x3E) and a champion-spell cast, INPUT_SUMMONERSPELLS a summoner
    /// cast (0xA2), INPUT_MOVEMENT a move or attack order (0x75 MOVETO/ATTACKTO). Reconstruction: in the client enforces them
    /// (the ability lock, the client, unbinds the cast and level-up keys; the right-click handler, the client, returns while bit 2 is
    /// set, and its minimap branch while bit 64 is), and whether the retail server also checked them is not in the shipped files. The
    /// client's ability lock has a gap: closing the item shop ("closeItemShop") rebinds those keys while the
    /// flag stays set, and neither the level-up nor the key casts read the flag. A W learnt
    /// through it stopped the Basic Tutorial (owner, 4 November 2026), and an early Heal is on cooldown at its lesson. The minimap's own
    /// lock (INPUT_MINIMAPMOVEMENT) sends the same 0x75, so only INPUT_MOVEMENT is read (the TutorialScript always sets the two together).
    /// </summary>
    internal Func<uint>? InputLocks { get; set; }

    // AiGlobals.InputLockMask: INPUT_MOVEMENT, INPUT_ABILITIES, INPUT_SUMMONERSPELLS.
    private const uint MovementLock = 2, AbilitiesLock = 4, SummonerLock = 8;

    private bool Locked(uint flag) => InputLocks is { } locks && (locks() & flag) != 0;
    // Reconstruction: an order received inside an uncancellable spell windup (SpellWindup), applied when the windup is over.
    private readonly Dictionary<uint, SessionGameplayContext> _windupQueued = new();
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly SpellSystem _spells;
    private readonly HeroStatsService _heroStats;
    private readonly ItemService _items;
    private readonly WorldState _world;
    private readonly ISpellEffectSink _effects;
    private readonly PetAiService _pets;
    private readonly HeroAttackOrderSystem _heroOrders;
    private readonly Func<Entity, Entity, bool> _heroOrderVisible;
    private readonly FidelityTrace? _fidelityTrace;
    private readonly CollisionDiagnosticSink? _movementTrace;
    private readonly PlaytestDiagnostics? _playtest;
    // Batch 449: the map decides whether an order on a capture guardian starts the Odin capture channel (map 8 only).
    private readonly int _mapId;
    // Reconstruction: the capture range of a point (BehaviorTreeQuestHost.CaptureRadius, the manager's 600). The shipped channel
    // names no cast range, and the stock client sends no capture order, so the range is the trees' capture radius.
    private const double CaptureRange = BehaviorTreeQuestHost.CaptureRadius;

    private static bool InCaptureRange(Position2 hero, Position2 point) => InRange(hero, point, CaptureRange);

    private static bool InRange(Position2 hero, Position2 point, double range)
    {
        var dx = hero.X - point.X;
        var dz = hero.Z - point.Z;
        return dx * dx + dz * dz <= range * range;
    }

    /// <summary>
    /// Batch 461: the guardian each walking champion was ordered onto from out of range (map 8); a fresh order drops it. Batch 470: also the
    /// useable unit a champion walks to after its use packet (<see cref="UseObject"/>), with the channel it names and that channel's range.
    /// </summary>
    private readonly Dictionary<uint, (Entity Target, string Spell, double Range)> _pendingCaptures = new();

    /// <summary>
    /// Batch 461 (Reconstruction): once per step, a champion walking to a guardian it was ordered onto from out of range starts the capture
    /// channel when it comes within <see cref="CaptureRange"/>. The walk is gone, and the order with it, when the champion or the guardian
    /// dies, when crowd control or a displacement takes the champion (<see cref="CaptureStartAllowed"/>: the lock clears the route), or when
    /// the champion's route no longer ends at the guardian (another order replaced it). Batch 470: a use walk the same way, with its own
    /// channel and range.
    /// </summary>
    public void StartDueCaptures()
    {
        if (_pendingCaptures.Count == 0) return;
        foreach (var (netId, (target, spell, range)) in _pendingCaptures.ToArray())
        {
            if (_world.Get(netId) is not { Alive: true } hero || !target.Alive || !CaptureStartAllowed(hero, target))
            {
                _pendingCaptures.Remove(netId);
                continue;
            }
            if (InRange(hero.Position, target.Position, range))
            {
                _pendingCaptures.Remove(netId);
                _spells.BeginUseChannel(hero, target, spell);
            }
            else if (hero.Waypoints.Count == 0 || !InRange(hero.Waypoints[^1], target.Position, range))
                _pendingCaptures.Remove(netId);
        }
    }

    /// <summary>
    /// Batch 470: <c>UseObject</c> (0x3F), the stock client's right-click on a useable unit. The client's right-click handler
    /// tests the unit under the cursor for <c>IsUseable</c> before any attack or move; when the local hero may use it
    /// (<c>GetCanUseObjectReason</c> answers 0) it calls the client's use-object routine, which sends this packet with
    /// no range test, and the click ends there. The unit's character file names what the use does (<c>UseableComponent</c>, read by
    /// the client's use-component reader): <c>Useable*IsUseable</c>, <c>AllyCanUse</c>, <c>EnemyCanUse</c>, <c>MinionUseable</c> and the hero's
    /// <c>HeroUseSpell</c>. Only two shipped units are useable: OdinNeutralGuardian (OdinCaptureChannel) and OdinCenterRelic, the greater relic
    /// (OdinCaptureChannelBomb). The champion channels that spell at the unit: in range at once, from farther away after a walk to it.
    /// Reconstruction: the server's half is not in the client. The range is the spell's <c>CastRange</c> (250 for the relic, 500 for a guardian);
    /// the walk and the gates are the capture order's (batch 461). Batch 494: the use slot's cooldown (<c>BBSetUseSlotSpellCooldownTime</c>, the
    /// client's reason 1) is kept and refuses the use.
    /// </summary>
    private bool UseObject(Entity hero, Entity used)
    {
        if (!hero.Alive || !used.Alive || HeroUseSpell(hero, used) is not { } spell) return false;
        // Batch 494 (Reconstruction: retail's server half of the use is not shipped): a use of the unit the hero already channels this use
        // spell at changes nothing. Each right-click on the point sends a 0x3F, and every one cancelled and restarted the capture (the
        // owner's game e924eadc: 243 uses, 91 restarts, each with a new suppression and beam).
        if (_spells.ChannelsUseAt(hero, used, spell)) return true;
        // Batch 494: a use channel's stop cools the use slot down (BBSetUseSlotSpellCooldownTime: 3 s after a capture, 4 s after a relic). The
        // client refuses its own clicks then (GetCanUseObjectReason, reason 1); the server refuses the use the same way.
        if (_spells.UseSlotCoolingDown(hero)) return false;
        if (!CaptureStartAllowed(hero, used)) return false;
        var range = _spells.Data.Spell(spell).CastRange;
        _windupQueued.Remove(hero.NetId);
        _pendingCaptures.Remove(hero.NetId);
        _spells.CancelOrder(hero);
        if (InRange(hero.Position, used.Position, range)) return _spells.BeginUseChannel(hero, used, spell);
        ForgetEmote(hero.NetId);
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.HeroAutoAcquire = true;
        hero.HeroHold = false;
        _heroOrders.StartMove(hero, used.Position);
        if (hero.Waypoints.Count == 0 || !InRange(hero.Waypoints[^1], used.Position, range)) return false;
        _pendingCaptures[hero.NetId] = (used, spell, range);
        return true;
    }

    /// <summary>
    /// The spell a hero's use of <paramref name="used"/> starts, or null when the hero may not use it: <c>IsAllowedToUse</c> of the useable component
    /// lets a unit of the owner's team use it with <c>AllyCanUse</c> and any other team with <c>EnemyCanUse</c> (a neutral relic
    /// is the other team's); a hero needs no <c>MinionUseable</c>. Batch 516: a centre crystal is on the team that may not use it
    /// (<c>EnemyCanUse</c> only, BehaviorTreeQuestHost.RelicPlacements), so this refuses the wrong team's use.
    /// </summary>
    private string? HeroUseSpell(Entity hero, Entity used)
    {
        if (_spells.Data.OptionalChampion(used.Name) is not { } record ||
            !Flag(record.Raw, "Useable*IsUseable") ||
            !Flag(record.Raw, used.Team == hero.Team ? "Useable*AllyCanUse" : "Useable*EnemyCanUse")) return null;
        return record.Raw.TryGetValue(InibinReader.KeyHash("Useable*HeroUseSpell"), out var name) &&
            name.Kind == InibinValueKind.String && name.AsString() is { Length: > 0 } spell ? spell : null;

        // As SpellData.RawFlag reads a flag: the boolean section, a "0"/"1" string, or a number; absent is false.
        static bool Flag(InibinData raw, string key) => raw.TryGetValue(InibinReader.KeyHash(key), out var value) &&
            (value.Kind == InibinValueKind.Boolean ? value.Boolean
                : value.Kind == InibinValueKind.String ? InibinValue.AtofPrefix(value.AsString()) != 0.0
                : value.IsNumeric && value.AsDouble() != 0.0);
    }

    /// <summary>
    /// Batch 463: a bot's capture order on map 8 (BotController.Dominion.cs), the player's attack order on a guardian without the client's
    /// packet. Within <see cref="CaptureRange"/> it cancels a recall or a deferred cast, as every order does, and starts the channel behind the
    /// same gates; from out of range it marks the bot's own walk for <see cref="StartDueCaptures"/> when that walk ends at the guardian. False
    /// when nothing started or was marked.
    /// </summary>
    public bool OrderCapture(Entity hero, Entity guardian)
    {
        if (_mapId != GameMaps.CrystalScar || guardian is not { ChampionName: "OdinNeutralGuardian", Alive: true } || !hero.Alive) return false;
        if (InCaptureRange(hero.Position, guardian.Position))
        {
            if (!CaptureStartAllowed(hero, guardian)) return false;
            _pendingCaptures.Remove(hero.NetId);
            _spells.CancelOrder(hero);
            return _spells.BeginCaptureChannel(hero, guardian);
        }
        if (hero.Waypoints.Count == 0 || !InCaptureRange(hero.Waypoints[^1], guardian.Position)) return false;
        _pendingCaptures[hero.NetId] = (guardian, BbSummonerModule.CaptureChannelSpellName, CaptureRange);
        return true;
    }

    /// <summary>
    /// Batch 461: the gates a capture start passes, the direct order's and the walk's alike (the order handler refuses an order in these states
    /// before it reaches the capture): no dash or displacement, no uncancellable windup, no action lock (stun, suppression), no root.
    /// Batch 687 (community report, 4 November 2026: a team was let into a capture of its own damaged point, which the channel then rejected):
    /// the hero may use the unit (<see cref="HeroUseSpell"/>, the client's IsAllowedToUse). The client refuses the use of an
    /// allied guardian (its record sets no Useable*AllyCanUse), and its right-click then falls through to ATTACKTO on that ally, because the
    /// guardian's wire health 0 (batch 503) passes the client (the client: an ally at or below 0.0 of its health,
    /// The client). The order and the bots' capture took any guardian, so the shipped channel started and ended itself as a success at its
    /// first update (OdinCaptureChannel ChannelingUpdateStats: MyTeam equals TargetTeam), with the 3 s use cooldown.
    /// </summary>
    private bool CaptureStartAllowed(Entity hero, Entity used) =>
        hero.ForcedMove is null && !SpellWindup.IsWindingUp(hero) && !IsActionLocked(hero) && !IsRooted(hero) &&
        HeroUseSpell(hero, used) is not null;

    public ProductionGameplayDispatcher(MovementSystem movement, CombatService combat,
        SpellSystem spells, HeroStatsService heroStats, ItemService items, WorldState world,
        ISpellEffectSink effects, PetAiService pets,
        HeroAttackOrderSystem heroOrders, Func<Entity, Entity, bool> heroOrderVisible,
        FidelityTrace? fidelityTrace = null, CollisionDiagnosticSink? movementTrace = null,
        PlaytestDiagnostics? playtest = null, int mapId = GameMaps.Normal)
    {
        _mapId = mapId;
        _movement = movement;
        _combat = combat;
        _spells = spells;
        _heroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        _items = items;
        _world = world;
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _pets = pets ?? throw new ArgumentNullException(nameof(pets));
        _heroOrders = heroOrders;
        _heroOrderVisible = heroOrderVisible;
        _fidelityTrace = fidelityTrace;
        _movementTrace = movementTrace;
        _playtest = playtest;
    }

    public bool Dispatch(SessionGameplayContext context)
    {
        var payload = context.Payload.Span;
        if (payload.Length < 5) return false;
        switch (payload[0])
        {
            case 0x59:
                if (payload.Length != 5) return false;
                _heroStats.Refresh(context.Hero, push: true);
                return true;
            case 0x75:
                if (payload.Length < 22) return false;
                var order = payload[5];
                var x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[6..]));
                var z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[14..]));
                if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
                var destination = new Position2(x, z);
                var actor = context.Actor ?? context.Hero;
                var targetNetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[18..]);
                if (order is 2 or 3 && Locked(MovementLock)) return false;

                // Ordinary hero clicks are also mirrored by the client as
                // plain pet-actor orders. Only the distinct Pet.lua hard
                // orders are explicit pet control; DuelSession enforces the
                // same rule before dispatch.
                if (order is 5 or 6 or 9 or 11)
                    return _pets.Command(context.Hero, order, destination,
                        targetNetId == 0 ? null : targetNetId,
                        actor.NetId == context.Hero.NetId ? null : actor.NetId);
                // A mirrored ordinary order reaches Pet.lua OnOrder, whose own near/far and hard-state
                // rules decide what it does with it.
                if (order is 1 or 2 or 3 or 7 && actor.NetId != context.Hero.NetId)
                    return _pets.Order(context.Hero, actor.NetId, order, destination,
                        targetNetId == 0 ? null : targetNetId);
                if (actor.NetId != context.Hero.NetId || !context.Hero.Alive ||
                    order is not (1 or 2 or 3 or 7 or 10))
                    return false;
                var hasClientTrail = TryReadClientTrail(payload, out var clientTrail);
                if (hasClientTrail)
                {
                    _playtest?.ObserveClientPosition(context.Hero, clientTrail[0]);
                    // DIAGNOSTICS: diagnostics only. The client's own plan for this click (its path encoder's output), to be diffed against the
                    // route this server sends back for it ("hero-order" waypoint intent) in the first live game; no hero is moved from it.
                    _fidelityTrace?.NoteOrder(actor.NetId, "client-trail", $"order-{order} target-{targetNetId:X8}",
                        _world.Clock.SyncId(), clientTrail);
                }
                _movementTrace?.NoteAcceptedHeroOrder(actor, order, targetNetId, destination);
                _fidelityTrace?.NoteOrder(actor.NetId, "input", $"order-{order} target-{targetNetId:X8}",
                    _world.Clock.SyncId(), [actor.Position, destination]);

                // Every fresh client order cancels recall and a deferred
                // walk-then-cast.  Champion channels (notably Janna R) are
                // cancelled by the same typed hook used by spell requests.
                // Do not replace a dash with an ordinary waypoint on the
                // predicting client. Its destination stays server-owned. The
                // newer click still replaces a cast queued during the dash
                // (playtest B3 #10), as any newer order does in retail.
                if (context.Hero.ForcedMove is not null)
                {
                    context.Hero.PendingCast = null;
                    _pendingCaptures.Remove(context.Hero.NetId);
                    return true;
                }
                // An uncancellable cast windup (SpellWindup: any spell the client runs between the cast packet and its cast frame
                // that carries CantCancelWhileWindingUp, Recall's first 0.5 s included) refuses the plain Stop every order ends in
                // (IsCancelBlocked; the client's spell caster stop), and the client has no order queue, so a
                // route sent now would walk the hero in the cast pose until the cast frame. Reconstruction: what the retail server
                // does with the blocked order is native and in no binary we hold; the cast completes and then the order is applied
                // (owner decision, 18 October 2026, for Recall; the same for every spell since the windup gate of 18 October). The latest
                // order is held and ApplyQueuedWindupOrders runs it once the stop is accepted: the stop that cancels the recall or the
                // remaining cast clip, then the route, with no stationary waypoint in between and no cast lost.
                if (SpellWindup.IsWindingUp(context.Hero))
                {
                    _windupQueued[context.Hero.NetId] = context with { Payload = context.Payload.ToArray() };
                    return true;
                }
                // A newer order that is not held replaces a held one (latest order wins).
                _windupQueued.Remove(context.Hero.NetId);
                _pendingCaptures.Remove(context.Hero.NetId);
                _spells.CancelOrder(context.Hero);
                // Reconstruction: how the retail server answers an order it refuses (a stunned or cast-locked hero) is native and in no
                // binary we hold. The client moves only along an echoed path, and the lock already cleared the route and sent the stop
                // (BuffLifecycle), so this stationary waypoint only re-anchors a client that has not yet received that stop. The client
                // installs it on receipt (the client's path-follow routine): the hero is seated on its anchor at once (MovementSystem.InstallStop).
                if (IsActionLocked(context.Hero))
                {
                    if (context.Hero.ForcedMove is null && _effects.IsReferenceReady(context.Hero))
                        SendHeroWaypoints(context.Hero, _movement.InstallStop(context.Hero), "hero-action-locked");
                    return true;
                }

                var attackTarget = order == 3 && targetNetId != 0 ? _world.Get(targetNetId) : null;
                // Root removes movement permission, not attack permission (DarkBindingMissile.luaobj sets only SetCanMove false).
                // Stop/Hold and selecting an enemy already in range remain legal. Reconstruction: the stationary waypoint of a refused
                // order, as above.
                // Reconstruction (owner decision, 18 October 2026): an attack-move (7) with an enemy in attack range is accepted by a rooted
                // hero, which fights that enemy and, as Hero.lua OnOrder has no root gate (a root clears only CanMove), cannot walk on; with
                // no enemy in reach it stays refused with the stationary waypoint, as before (the stock client plans no route of its own
                // for a unit that cannot move, status bit 2, and sends the order without a trail), and is not buffered.
                if (IsRooted(context.Hero) && order is not (1 or 10) &&
                    !(order == 7 && _heroOrders.HasEnemyInReach(context.Hero)) &&
                    (attackTarget is null || !_combat.CanAttack(context.Hero, attackTarget)))
                {
                    if (_effects.IsReferenceReady(context.Hero))
                        SendHeroWaypoints(context.Hero, _movement.InstallStop(context.Hero), "hero-rooted");
                    return true;
                }

                // Batch 449 (map 8 only): an attack order aimed at a capture point guardian within CaptureRange starts the shipped
                // OdinCaptureChannel on the champion, with the guardian as its Target (ChannelingStart: OdinGuardianSuppression on
                // Target). The stock client's pick does not select a Targetable 0 guardian (docs/maintenance/2026-10-28-batch-449-
                // dominion-capture-input.md, part 1), so this is the order the server takes as the capture; a guardian out of range
                // falls through to the ordinary move below (the champion walks first). Batch 461 (Reconstruction): that walk starts the
                // channel when the champion comes within range (StartDueCaptures), as an attack order closes in on its target before it
                // swings (Hero.luaobj OnOrder ATTACKTO); a fresh order drops it.
                if (order == 3 && _mapId == GameMaps.CrystalScar && context.Target is { ChampionName: "OdinNeutralGuardian" } guardian)
                {
                    if (InCaptureRange(context.Hero.Position, guardian.Position))
                    {
                        if (CaptureStartAllowed(context.Hero, guardian) && _spells.BeginCaptureChannel(context.Hero, guardian)) return true;
                    }
                    // The walk below goes to the order's point: only a walk that ends at the guardian closes in on it.
                    else if (InCaptureRange(destination, guardian.Position)) _pendingCaptures[context.Hero.NetId] =
                        (guardian, BbSummonerModule.CaptureChannelSpellName, CaptureRange);
                }
                if (order is 1 or 10)
                {
                    // Reconstruction: Hero.lua OnOrder handles only TAUNT, ATTACKTO, ATTACKMOVE and MOVETO, so the retail server treats
                    // STOP (10) and HOLD (1) natively (Pet.lua, by contrast, sends HOLD to AI_PET_HOLDPOSITION and makes STOP a no-op).
                    // The client sends 10 when the stop key or the hold key goes down and 1 when the hold key is released (StopPosition
                    // the stop handler always sends 10, HoldPosition reads the key state); Stop here clears the order and suppresses idle acquisition (TimerDistanceScan returns
                    // for AI_HARDIDLE and acquires from AI_STANDING and AI_IDLE), Hold clears it and re-enables acquisition for an enemy
                    // already in attack range only (HeroHold: no chase, owner decision 18 October 2026, modelled on Pet.lua's AI_PET_HOLDPOSITION).
                    var nativeEmoteHold = _emoteHold.Remove(context.Hero.NetId, out var hold) &&
                        order == 1 && _world.Clock.Monotonic() - hold.At <= 0.5 &&
                        // The movement model's stop arrival (ArriveOnStop) seats the held hero on its lattice anchor a frame later.
                        (context.Hero.Position == hold.Position ||
                         context.Hero.Position == _movement.ClientAnchor(hold.Position)) &&
                        context.Hero.Waypoints.Count == 0 && context.Hero.Target is null &&
                        context.Hero.AttackTarget is null && context.Hero.AttackMoveGoal is null &&
                        context.Hero.HeroPursuit is null && !context.Hero.HeroAutoAcquire;
                    StopHero(context.Hero, replicate: !nativeEmoteHold);
                    context.Hero.HeroAutoAcquire = order == 1;
                    // Reconstruction (owner decision, 18 October 2026): the hold key's release (order 1) attacks only what is already in
                    // attack range and never chases; the key's press (order 10, the Stop above) has cleared everything first.
                    context.Hero.HeroHold = order == 1;
                    return true;
                }

                ForgetEmote(context.Hero.NetId);
                context.Hero.AttackMoveGoal = null;
                context.Hero.HeroPursuit = null;
                context.Hero.HeroAutoAcquire = true;
                context.Hero.HeroHold = false;
                if (order == 7)
                {
                    _heroOrders.StartAttackMove(context.Hero, destination);
                    return true;
                }

                // Stock IssueOrder uses 3 for ATTACK_TO, 7 for ATTACK_MOVE
                // and 10 for STOP. A stop must never install a chase route.
                // Reconstruction: an ATTACK_TO whose target is not a legal enemy (it died, became untargetable, or is protected) the
                // client still sent is answered like a move to the target's position; the native handling is unrecovered.
                if (order == 3)
                {
                    var target = attackTarget;
                    if (target is not null && target.Alive &&
                        WorldState.IsEnemy(context.Hero, target) &&
                        !target.Protected && _combat.IsTargetable(target, context.Hero))
                    {
                        _heroOrders.StartAttack(context.Hero, target);
                        return true;
                    }
                }

                // Client MovementData contains a dead-reckoned position after
                // the fixed header. It is a prediction report, not an
                // authoritative movement command. Keep it in fidelity output
                // for comparison, but never move the server entity from it.
                if (order != 3 && hasClientTrail)
                    _fidelityTrace?.NoteOrder(context.Hero.NetId, "client-report", "movement-data",
                        _world.Clock.SyncId(), [context.Hero.Position, clientTrail[0]]);

                // The move Hero.luaobj's OnOrder takes for ORDER_MOVETO (proto 0.2 pc 0074-0083):
                // it drops the fight in the same step and walks the click, and an ordinary move
                // never acquires before arrival (TimerDistanceScan only scans an AI_MOVE hero that
                // has stopped). The walk itself is the engine's.
                _heroOrders.StartMove(context.Hero, destination);
                return true;
            case EmotePackets.ClientOpcode:
                if (!EmotePackets.TryReadRequest(payload, out var emote) ||
                    !CanEmote(context.Hero)) return false;
                _pendingCaptures.Remove(context.Hero.NetId);
                _spells.CancelOrder(context.Hero);
                // Batch 420 (owner finding 79, an emote while walking): the stop is replicated in the request's own flush, ahead of the
                // 0x48. The client's 0x48 handler issues AI_HOLD (the order routine, which calls IssueOrder), but
                // that order does not clear the path the client already follows, so the hero walked on (batch 270 relied on AI_HOLD
                // alone). Reconstruction: the stop-then-emote order is inferred from that observation, not read from the server. A
                // channel-4 route after the channel-3 0x48 cancels it (playtest #3, 25 Sept 2026), so the stop is queued first. The
                // client's post-emote hold echo stays unreplicated (see the hold window above). Batch 437 (owner, 28 October 2026:
                // "Dancing emote does not loop"): only a walking or attacking hero has a stop to replicate. A standing hero's emote
                // is the 0x48 alone (as batch 270 had it), because a redundant stop interrupts the dance (overnight polish of 23 September
                // 2026, item 6). Reconstruction: the retail server's choice of stop is inferred from the owner's observation.
                // Batch 437 (owner, 28 October 2026: "Emoting when moving only stops the character, doesnt trigger an emote
                // afterwards"): a walking or attacking hero's stop leaves first and its 0x48 waits (Reconstruction: the retail order
                // is inferred from that observation). The batch 420 order sent both in one burst, and the stop cancelled the emote.
                // Batch 679: one simulation step was still one burst in production, which receives this packet unflushed and flushes
                // once after the step that ran PlayDueEmotes; the 0x48 now waits EmoteSettleSeconds of simulation time.
                var stopping = context.Hero.Waypoints.Count > 0 || context.Hero.AttackTarget is not null;
                StopHero(context.Hero, replicate: stopping);
                if (stopping)
                {
                    _dueEmotes[context.Hero.NetId] = (emote, _spells.CurrentTime + EmoteSettleSeconds);
                    return true;
                }
                if (_effects.IsReferenceReady(context.Hero))
                {
                    _effects.PlayEmote(context.Hero, emote);
                    _emoteHold[context.Hero.NetId] = (context.Hero.Position, _world.Clock.Monotonic());
                }
                return true;
            case DuelSession.UseObjectOpcode:
                return payload.Length >= 9 && context.Target is { } useTarget && UseObject(context.Hero, useTarget);
            case 0x3E:
                if (payload.Length < 6 || Locked(AbilitiesLock) || !_spells.RankUp(context.Hero, payload[5]))
                    return false;
                _heroStats.Refresh(context.Hero, push: true);
                return true;
            case 0xA2:
                var request = SpellSystem.ParseCastRequest(payload);
                if (request is null) return false;
                // Batch 691b: a summoner spell or a champion spell under the script's lock (InputLocks).
                if (request.Value.IsSummoner
                        ? request.Value.Slot < SpellSystem.SummonerSlots.Length && Locked(SummonerLock)
                        : request.Value.Slot < SpellSystem.ChampionSlotCount && Locked(AbilitiesLock))
                    return false;
                // Batch 461: a cast, Recall or an item use replaces a walk toward a capture guardian, as it replaces any order.
                _pendingCaptures.Remove(context.Hero.NetId);
                if (request.Value.Slot >= SpellSystem.ChampionSlotCount &&
                    request.Value.Slot != SpellSystem.RecallSlot)
                {
                    var used = _items.Use(context.Hero, new ItemUseRequest(
                        checked((byte)request.Value.Slot), request.Value.Position,
                        request.Value.EndPosition, request.Value.TargetNetId,
                        request.Value.IsSummoner)).Accepted;
                    if (used) ForgetEmote(context.Hero.NetId);
                    return used;
                }
                // Reconstruction (owner decision, 18 October 2026): a second spell pressed while the hero's client winds up another
                // uncancellable spell (SpellWindup) is held, the way a click is, and cast right after the first spell's cast frame
                // (SettleSeconds later, ApplyQueuedWindupOrders). The client has no order queue and sends the press at once; a
                // cast packet now would start a second SpellCaster beside the one whose stop the client refuses. The newest command
                // wins in both directions: a held cast replaces a held click or an earlier held cast, and a later click, Stop or
                // non-instant cast replaces it (as any newer order replaces a cast queued during a dash, playtest B3 #10). An
                // instacast creates no SpellCaster on the client, so it acts at once and leaves a held cast alone. A champion slot is
                // held only if the spellbook's CanUseSpell (rank, cooldown, the cast bit, then the unit's OnCanUseSpell:
                // control state and mana) passes now: the client sends no press its own gate refuses, so a refused one is refused at once
                // (batch 343: LeBlanc's Mimic pressed during her Q's windup was held whatever its rank, cooldown, mana or stun) and leaves
                // the held order alone. The held cast goes through HandleCastRequest again when it fires, which checks the gates, the
                // target and the range like a fresh press; a failure is dropped silently, as any refused cast is answered (no packet).
                var queues = _spells.CreatesSpellCaster(context.Hero, request.Value);
                if (queues && context.Hero.Alive && SpellWindup.IsWindingUp(context.Hero))
                {
                    if (!request.Value.IsSummoner && request.Value.Slot < SpellSystem.ChampionSlotCount &&
                        !_spells.CanUseSpell(context.Hero, request.Value.Slot, _spells.CurrentTime).Allowed)
                        return false;
                    _windupQueued[context.Hero.NetId] = context with { Payload = context.Payload.ToArray() };
                    return true;
                }
                var cast = _spells.HandleCastRequest(context.Hero, payload, _spells.CurrentTime);
                if (cast)
                {
                    ForgetEmote(context.Hero.NetId);
                    // The newest command wins: an accepted cast supersedes the order held through a windup (a cast refused leaves it).
                    // A held cast survives an instacast, which is not an order of the same queue.
                    if (queues || !_windupQueued.TryGetValue(context.Hero.NetId, out var held) || held.Payload.Span[0] == 0x75)
                        _windupQueued.Remove(context.Hero.NetId);
                }
                return cast;
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies the order held through an uncancellable spell windup once the windup has ended
    /// (<see cref="SpellWindup.SettleSeconds"/> later, so the cancelling stop reaches a client whose own windup is over).
    /// It goes through <see cref="Dispatch"/> again, which cancels the recall or the cast clip (0x39, flags 0) and sends the route.
    /// A hero that died loses its held order.
    /// </summary>
    public void ApplyQueuedWindupOrders()
    {
        if (_windupQueued.Count == 0) return;
        foreach (var (netId, held) in _windupQueued.ToArray())
        {
            var hero = held.Hero;
            if (!hero.Alive || _world.Get(netId) != hero)
            {
                _windupQueued.Remove(netId);
                continue;
            }
            if (SpellWindup.IsWindingUp(hero, settled: true)) continue;
            _windupQueued.Remove(netId);
            Dispatch(held);
        }
    }

    // Batch 689 (community report, 4 November 2026: emotes ended channels): a channelling hero does not emote. The stock client's 0x48
    // handler issues AI_HOLD on the hero (the client(1,...)), an order that ends a cancellable channel on the client and
    // returns to the server as a hold, so an answered emote ends the channel either way; the request
    // is left unanswered. Reconstruction: the retail server's refusal is native; Recall (its windup too) and every script channel count.
    private bool CanEmote(Entity hero) =>
        hero.Alive && hero.ForcedMove is null &&
        !IsActionLocked(hero) && !IsRooted(hero) &&
        hero.ActiveScriptChannels == 0 && _spells.RecallRemaining(hero) <= 0.0;

    private void ForgetEmote(uint netId)
    {
        _emoteHold.Remove(netId);
        _dueEmotes.Remove(netId);
    }

    /// <summary>
    /// Plays the emotes that waited <see cref="EmoteSettleSeconds"/> behind their hero's stop (batches 437 and 679). A hero that has been
    /// given a new order lost its emote in <see cref="ForgetEmote"/>; one that can no longer emote drops it here.
    /// </summary>
    public void PlayDueEmotes()
    {
        if (_dueEmotes.Count == 0) return;
        foreach (var (netId, (emote, dueAt)) in _dueEmotes.ToArray())
        {
            if (_spells.CurrentTime < dueAt - 1e-9) continue;
            _dueEmotes.Remove(netId);
            var hero = _world.Get(netId);
            if (hero is null || !CanEmote(hero) || !_effects.IsReferenceReady(hero)) continue;
            _effects.PlayEmote(hero, emote);
            _emoteHold[netId] = (hero.Position, _world.Clock.Monotonic());
        }
    }

    private void StopHero(Entity hero, bool replicate = true)
    {
        _pendingCaptures.Remove(hero.NetId);
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.HeroAutoAcquire = false;
        hero.HeroHold = false;
        hero.LastAttackStopPoint = null;
        InterruptHeroWindup(hero, "stop-order");
        if (!replicate || !_effects.IsReferenceReady(hero)) return;
        _effects.SendStopAttack(hero);
        // The stop is a one-point path, which the client installs on receipt (the client's path-follow routine): the hero is stopped and
        // seated on its lattice anchor at once (MovementSystem.InstallStop), not at its next frame's stop arrival.
        SendHeroWaypoints(hero, _movement.InstallStop(hero), "hero-stop");
    }

    private int InterruptHeroWindup(Entity hero, string reason)
    {
        var pending = _fidelityTrace is null ? Array.Empty<PendingHit>() : _combat.PendingHits
            .Where(hit => hit.SourceNetId == hero.NetId &&
                hit.SourceKind == DamageSourceKind.Basic &&
                hit.WindupRemaining > 0.0)
            .ToArray();
        var canceled = _combat.InterruptBasicAttack(hero);
        if (_fidelityTrace is null) return canceled;
        foreach (var hit in pending)
        {
            var target = _world.Get(hit.TargetNetId);
            _fidelityTrace.NoteHeroAttack(hero.NetId, hit.TargetNetId,
                hero.Position, target?.Position, reason, 0,
                hero.AttackRange, hit.WindupRemaining,
                target is not null && _combat.CanAttack(hero, target),
                target is not null && _combat.InCommittedAttackRange(hero, target));
        }
        return canceled;
    }

    private void SendHeroWaypoints(Entity hero, IReadOnlyList<Position2> route, string reason)
    {
        var syncId = _world.Clock.SyncId();
        _effects.SendWaypoints(hero, route, syncId);
        _movementTrace?.NoteWaypointIntent(hero.NetId, reason, syncId, route);
    }

    private static bool IsActionLocked(Entity entity) =>
        entity.ChampionState?.BlocksActions == true ||
        entity.AnyBuffOrStatus(buff => buff.DisablesActions);

    private static bool IsRooted(Entity entity) => entity.AnyBuffOrStatus(buff =>
        buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    /// <summary>
    /// The trail a stock client appends to its order (its own plan, a diagnostic report only; its first point is the client's snapped
    /// position): the shared <see cref="MoveOrderTrail"/> decoder, whose lattice origin is the navigation midpoint (6991, 7223 on Map1).
    /// </summary>
    private bool TryReadClientTrail(ReadOnlySpan<byte> payload, out List<Position2> trail)
    {
        var navigation = _movement.Navigation;
        var origin = Geometry.WaypointOrigin(new NavigationBounds(navigation.MinX, navigation.MaxX, navigation.MinZ, navigation.MaxZ));
        if (MoveOrderTrail.TryRead(payload, origin, out trail) &&
            trail.All(point => point.X >= -500.0 && point.X <= 15000.0 && point.Z >= -500.0 && point.Z <= 15000.0))
            return true;
        trail = [];
        return false;
    }
}
