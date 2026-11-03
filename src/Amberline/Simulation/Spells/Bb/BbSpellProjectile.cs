using System.Collections.Concurrent;
using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The delayed part of a cast in the shared projectile queue: a targeted missile (homing, arrives after the windup plus its
/// flight) or a windup effect (cone: ends after the windup). Both end by running <c>TargetExecute</c>. The arithmetic mirrors
/// the order the hand-written Annie queue entries step in (windup, then flight at the missile speed toward the live target).
/// </summary>
internal sealed class BbSpellProjectile : ISharedSpellProjectile, ILaunchAnnouncer
{
    // The owning module's flight list: an item's own extra-slot child (BbItemModule.CastChild) flies the same
    // missile, so the flight retires through this callback rather than a champion module.
    private readonly Action<BbSpellProjectile> _forget;
    private readonly BbSpellHost _host;
    private readonly BbScript _script;
    private readonly ChampionCastContext _cast;
    private readonly SpellCastInfo _castInfo;
    private readonly uint _missileNetId;
    private readonly Position2 _origin;
    private readonly Position2 _direction;
    private readonly Position3 _aim;
    private readonly bool _homing;
    // Batch 481b: the height the missile leaves from above its ground point (a unit's bone, see BbSpellProjectile.HeightAt); 0 otherwise.
    private readonly double _launchHeight;
    // The hops of a chain: planned at the cast, replaced in flight by AdvanceChainHop when a planned unit is dead.
    private readonly List<uint>? _chain;
    private int _hop;
    // A chain bounces off a dead unit only once, and only one it was flying to when it died (AdvanceChainHop).
    private bool _corpseBounced;
    // The creation of an Instacast flight with no client-side cast was sent (AnnounceLaunch or the cast frame): once per flight.
    private bool _instantAnnounced;
    private readonly HashSet<string>? _targetFlags;
    private readonly Func<SpellSystem, IReadOnlyList<Entity>>? _windupTargets;
    private readonly Action? _atWindupEnd;
    private readonly bool _child;
    // How the client got this missile: a cast answer creates it and retires it itself on impact (so only a late viewer
    // needs the destroy packet), while a missile packet announces an object the server retires with one. The item
    // actives' sub-missiles are the second kind (BbItemModule.CastChild, matching the old TargetedItemMissiles wire).
    private readonly bool _announcedByMissilePacket;
    private bool _castFrameRun;
    private readonly int _slot;
    private readonly int _rank;
    // The item whose use cast this flight (0 for a champion's own): its hit keeps that item's recap identity.
    private readonly int _itemId;
    private double _castRemaining;
    // The seconds the missile has flown, for a MissileFixedTravelTime flight.
    private double _flown;
    private Position2 _position;
    // The script-visible life of this flight's missile (launch, update and end hooks, BBDestroyMissile); null for a windup effect with no missile.
    private readonly BbMissileFlight? _flight;
    private bool _retired;

    public BbSpellProjectile(Action<BbSpellProjectile> forget, BbSpellHost host, BbScript script, Entity caster, int slot, int rank,
        ChampionCastContext cast, SpellCastInfo castInfo, uint targetNetId, uint missileNetId,
        Position2 origin = default, Position2 direction = default, Position3 aim = default,
        Func<SpellSystem, IReadOnlyList<Entity>>? windupTargets = null, HashSet<string>? targetFlags = null,
        Action? atWindupEnd = null, IReadOnlyList<uint>? chain = null, double? castTime = null, bool child = false,
        Position2? launchFrom = null, bool announcedByMissilePacket = false, int itemId = 0, double launchHeight = 0.0)
    {
        _launchHeight = launchHeight;
        _chain = chain is null ? null : new List<uint>(chain);
        _child = child;
        _announcedByMissilePacket = announcedByMissilePacket;
        _atWindupEnd = atWindupEnd;
        _windupTargets = windupTargets;
        _targetFlags = targetFlags;
        _forget = forget ?? throw new ArgumentNullException(nameof(forget));
        _host = host;
        _script = script;
        Caster = caster;
        _slot = slot;
        _rank = rank;
        _itemId = itemId;
        _cast = cast;
        _castInfo = castInfo;
        TargetNetId = targetNetId;
        _missileNetId = missileNetId;
        _homing = targetNetId != 0;
        _castRemaining = castTime ?? cast.Data.CastTime;
        _origin = _homing ? launchFrom ?? caster.Position : origin;
        _position = _origin;
        _direction = direction;
        _aim = aim;
        if (missileNetId != 0)
            _flight = new BbMissileFlight(host, script, cast.Data.Name, caster, slot, rank, cast.ManaCost, castInfo, cast.Data, targetNetId);
    }

    public Entity Caster { get; }
    public uint TargetNetId { get; }
    public bool Cancelled { get; private set; }
    public double CastRemaining => _castRemaining;
    public int Slot => _slot;
    /// <summary>A champion windup whose cast frame has not spent its mana and cooldown yet (batch 340).</summary>
    public bool ChargePending => _cast.AtCastFrame && !Cancelled && !_castFrameRun;
    /// <summary>The mana the cast frame will spend, held from the press (batch 340, the security review).</summary>
    public double ReservedMana => _cast.ManaCost;

    // A chain missile is tied to its first target only while it is cast: once launched it carries on to its later hops even
    // when the unit it is flying to dies, so the shared queue's target-loss retirement must not end it.
    public SharedProjectileSnapshot Snapshot => new(_cast.Data.Name, Caster.NetId,
        _retired || _flight is { Destroyed: true } || (_chain is not null && _castRemaining <= 1e-9) ? 0u : TargetNetId, _missileNetId,
        _castRemaining, _castRemaining <= 1e-9, _position, _cast.Data.MissileSpeed, Rank: _rank, Slot: _slot,
        ManaCost: _cast.ManaCost, CastWindupRemaining: _castRemaining, Origin: _origin, Direction: _direction, CastInfo: _castInfo);

    public void CancelBeforeLaunch(SpellSystem system)
    {
        if (Cancelled || _castRemaining <= 1e-9) return;
        Cancelled = true;
        if (_missileNetId != 0) RetireMissile(system);
    }

    /// <summary>Ends the client's missile object the way this flight had it announced (see the constructor's flag).</summary>
    private void RetireMissile(SpellSystem system)
    {
        if (_announcedByMissilePacket) system.Effects.SendDestroyMissile(_missileNetId);
        else system.Effects.RetireCastMissile(_missileNetId);
    }

    /// <summary>Target gone during the windup: the cast ends unpaid and the client is told so, once. Returns whether it did.</summary>
    public bool RefundOnTargetLoss(SpellSystem system)
    {
        // A child cast (BBSpellCast) costs nothing and has no cooldown of its own: there is nothing to give back.
        if (Cancelled || _child || _castRemaining <= 1e-9 || !Caster.Alive) return false;
        Cancelled = true;
        system.RefundChampionCast(Caster, _slot, _cast);
        return true;
    }

    public bool Tick(SpellSystem system, double dt)
    {
        if (Cancelled) { _forget(this); return false; }
        // BBDestroyMissile flagged the missile (the client was told then), or the queue took it out with its target: it ends here, as the
        // manager's next update returns 0.
        if (_flight is not null && (_retired || _flight.Destroyed))
        {
            _flight.End(_position, null);
            _forget(this);
            return false;
        }
        return _homing ? TickHoming(system, dt) : TickWindup(system, dt);
    }

    /// <summary>
    /// A launched missile the shared queue took out with its target (<c>BBDestroyMissileForTarget</c>: Zhonya's Hourglass, Hallucinate; the queue
    /// has already dropped the entry and told the client). The missile is over, so its end hooks run (Orianna's Protect missile returns the ball
    /// to her): at the next step of the queue, like a destroyed missile's, because the queue's caller is still walking the world's units.
    /// </summary>
    public void EndRetired(SpellSystem system)
    {
        if (_flight is not { Launched: true, Ended: false } || _retired) return;
        _retired = true;
        system.EnqueueSharedProjectile(this);
    }

    private bool TickHoming(SpellSystem system, double dt)
    {
        var target = system.World.Get(_chain is null ? TargetNetId : _chain[_hop]);
        // Reconstruction (batch 252): a player's cast loses its target in the windup when the target dies or stops being one it may aim at
        // (protected, untargetable, outside the spell's flags). A child cast (BBSpellCast) takes the script's TargetVar as it is, with no
        // admission at the call (CastChild), so its wait keeps only the dead-or-gone end. Maokai's Sapling Toss aims MaokaiSapling2Boom at the
        // invulnerable, untargetable placemarker it has just spawned; the admission refused it, so the burst, sproutling and mine never came.
        if (_castRemaining > 1e-9 && (target is not { Alive: true } ||
                                      (!_child && (target.Protected || !StillAdmitted(target) || !system.Combat.IsTargetable(target, Caster)))))
        {
            RefundOnTargetLoss(system);
            if (_missileNetId != 0) RetireMissile(system);
            _forget(this);
            return false;
        }
        // Retail rule (SpellSystem.DestroyTargetedProjectiles): after launch the missile flies to its target's object, living or not
        // (a corpse stays in the world until it is reaped), and runs its hit there (LandOnArrival); only a unit that is gone is given up.
        if (target is null || (!Caster.Alive && _castRemaining > 1e-9))
        {
            // A chain missile whose next unit is gone carries on to the one after.
            if (_chain is not null && _castRemaining <= 1e-9 && AdvanceChainHop(system, _position, arrivedDead: false)) return true;
            if (_missileNetId != 0 && _chain is not null) RetireMissile(system);
            _flight?.End(_position, null);
            _forget(this);
            return false;
        }
        var windup = _castRemaining;
        _castRemaining = Math.Max(0.0, windup - dt);
        // The cast frame: the spell's SelfExecute runs once, as the windup ends and before the missile leaves or the effect lands.
        if (_castRemaining <= 1e-9 && !_castFrameRun)
        {
            _castFrameRun = true;
            // DoCast_ClientAndServer spends the cast's costs first, then runs the cast (SpellSystem.ChargeChampionCast).
            system.ChargeChampionCast(Caster, _slot, _cast);
            _atWindupEnd?.Invoke();
            if (!_instantAnnounced && _missileNetId != 0 && _cast.Data.IsInstant && !_announcedByMissilePacket)
                _instantAnnounced = SendInstantCreation(system, target);
            // The missile leaves after the spell's SelfExecute (BuffOnLaunchMissile of the caster's script buffs).
            _flight?.Launch(_position, target);
        }
        // A cast without a missile (CastType 0: Imbue lists 1200, Rune Prison 500, Divine Blessing 20) lands when the windup ends.
        var speed = _missileNetId == 0 ? 0.0 : _cast.Data.MissileSpeed;
        if (speed <= 0.0)
        {
            // No missile: the effect lands when the windup ends.
            if (_castRemaining > 1e-9) return true;
            _position = target.Position;
            _host.Router.RunTargetExecute(_script, Caster, _slot, _rank, _cast.ManaCost, Ground(system, target.Position),
                Ground(system, target.Position), target, itemId: _itemId);
            _forget(this);
            return false;
        }
        var flying = Math.Max(0.0, dt - windup);
        var step = flying * speed;
        var dx = target.Position.X - _position.X;
        var dz = target.Position.Z - _position.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        // Batch 331: a homing missile with MissileFixedTravelTime flies the distance left over the time left (the client's update routine), so
        // it lands that long after it left whatever the distance (Maokai's sapling 0.85 s).
        var timed = false;
        if (_cast.Data.MissileFixedTravelTime is > 0.0 and var travel && _chain is null && _castRemaining <= 1e-9)
        {
            var left = travel - _flown;
            _flown += flying;
            timed = flying < left;
            step = timed ? distance * flying / left : double.PositiveInfinity;
        }
        if (_castRemaining > 1e-9 || step < distance || timed)
        {
            if (distance > 1e-9 && step > 0.0)
            {
                var fraction = Math.Min(1.0, step / distance);
                _position = new Position2(_position.X + dx * fraction, _position.Z + dz * fraction);
            }
            if (_castRemaining <= 1e-9) _flight?.Move(_position, target);
            // A chain missile replicates through its own packet (0x71, not the plain 0x41 a late observer would get here).
            if (_castRemaining <= 1e-9 && _chain is null)
                ChampionSpellSupport.RefreshMissile(system, Caster, _castInfo, _origin, _position, target.Position, speed, homing: true,
                    launchHeight: _launchHeight);
            return true;
        }
        _position = target.Position;
        // An item active's sub-missile lands in the phase after the buff lifecycle (SpellSystem.DeferLanding), where the
        // hand-written item missiles of the old code landed (ItemService.Tick runs there): the slow its hit adds then
        // keeps its full shipped duration for the step that delivered it. A champion child lands here with its own kind.
        if (_announcedByMissilePacket)
        {
            var arrived = target;
            system.DeferLanding(sys => LandOnArrival(sys, arrived));
            _forget(this);
            return false;
        }
        return LandOnArrival(system, target);
    }

    /// <summary>
    /// The creation packet of a homing or chain child whose spell is Instacast (<c>SpellData*Flags</c> bit 2). The cast answer alone does not
    /// build its missile: the client's cast routine hands such a cast to the instant-cast routine, which returns before
    /// the cast routine, the only place a missile object is built (the same gap <see cref="BbLineMissile"/> closes for
    /// lines, batch 177 part 1). Brand's E spread (<c>BrandConflagrationMissile</c>) and R bounces (<c>BrandWildfireMissile2..5</c>) are such
    /// children: the client never built their trails (<c>BrandConflagration_mis.troy</c>, <c>BrandWildfire_mis.troy</c>), so the spread showed no
    /// VFX and the Pyroclasm disappeared after its first hit (owner, 24 October 2026). Reconstruction: the payload is the one a homing item child
    /// sends (<c>BbItemModule.SendHomingMissile</c>), launched from where the missile leaves; <c>unitPos</c> is the caster's own ground position
    /// so the 0x41 branch cannot step the caster.
    /// </summary>
    /// <summary>
    /// The launch of an Instacast homing flight with no windup, announced when the flight is queued (the cast request, the moment the cast
    /// answer is queued before it). The creation then reaches exactly the views that the cast answer reached, in the same flush (see
    /// <c>ReplicationDelivery</c>'s <c>NeedsCastAnnouncement</c>): a view that missed the cast is served by the live refresh. The cast frame,
    /// which runs at the first step, then sends nothing more for this flight.
    /// </summary>
    public void AnnounceLaunch(SpellSystem spells)
    {
        if (_castRemaining > 1e-9 || _instantAnnounced || _missileNetId == 0 || !_cast.Data.IsInstant || _announcedByMissilePacket) return;
        if (spells.World.Get(_chain is null ? TargetNetId : _chain[_hop]) is not { } target) return;
        _instantAnnounced = SendInstantCreation(spells, target);
    }

    private bool SendInstantCreation(SpellSystem system, Entity target)
    {
        if (!system.Effects.IsReferenceReady(Caster)) return false;
        var start = Ground(system, _position);
        var end = Ground(system, target.Position);
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var direction = length > 1e-9 ? new Position3(dx / length, 0.0, dz / length) : new Position3(0.0, 0.0, 1.0);
        var speed = _cast.Data.MissileSpeed;
        // Batch 481b: a launch height starts the missile above the ground point and lets it fall at the rate the flight covers ground.
        var raised = _launchHeight == 0.0 ? start : new Position3(start.X, start.Y + HeightAt(_position, target.Position), start.Z);
        var fall = _launchHeight == 0.0 ? 0.0 : -_launchHeight * speed / Math.Max(Distance(_origin, target.Position), 1e-9);
        system.Effects.SendMissile(Caster, _castInfo, raised, direction,
            new Position3(direction.X * speed, fall, direction.Z * speed), raised, end, Ground(system, Caster.Position),
            needsCastAnnouncement: true);
        return true;
    }

    /// <summary>
    /// Batch 481b (Reconstruction): the missile's height above its ground point. A launch height (the guardian's crystal, see
    /// <c>OdinGuardianShot</c>) lies over the origin and falls in proportion to the horizontal distance flown to the target, so the shot
    /// leaves the bone and lands on the target's ground. Zero for every other missile.
    /// </summary>
    private double HeightAt(Position2 position, Position2 end)
    {
        if (_launchHeight == 0.0) return 0.0;
        var total = Distance(_origin, end);
        var flown = total <= 1e-9 ? 1.0 : Math.Clamp(Distance(_origin, position) / total, 0.0, 1.0);
        return _launchHeight * (1.0 - flown);
    }

    private static double Distance(Position2 a, Position2 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The chain moves on to its next hop once the missile has reached the current one (batch 177 part 4, the owner's rule of 24 October
    /// 2026: "E keeps bouncing off a dead target. It should only bounce off a dead target once, and only if the ability is already in flight
    /// to that target. Otherwise it should not bounce off dead targets. If there is no living target nearby, the ability should be deleted").
    /// The planned hop is kept while its unit lives. A unit that died while the missile flew to it is reached as a corpse once: the arrival
    /// itself is that bounce (<paramref name="arrivedDead"/>). A planned hop that is dead when the missile would start toward it is replaced
    /// by the nearest living unit from here (<see cref="BbChainParameters.NextHop"/>), and the client, which follows the list it was sent, is
    /// sent the new list. When no living unit qualifies the chain ends and the missile is retired.
    /// </summary>
    private bool AdvanceChainHop(SpellSystem system, Position2 from, bool arrivedDead)
    {
        if (_chain is null) return false;
        if (arrivedDead)
        {
            if (_corpseBounced) return false;
            _corpseBounced = true;
        }
        // MaximumHits bounds the list: an exhausted chain ends here, as before, and a replacement only takes a planned hop's place.
        if (_hop + 1 >= _chain.Count) return false;
        if (system.World.Get(_chain[_hop + 1]) is { Alive: true })
        {
            _hop++;
            return true;
        }
        var flags = _targetFlags ?? BbSpellTargeting.Names(BbSpellTargeting.Flags(_cast.Data));
        var passed = new List<Entity>();
        for (var i = 0; i <= _hop; i++)
            if (system.World.Get(_chain[i]) is { } unit) passed.Add(unit);
        var replacement = BbChainParameters.Read(_script)?.NextHop(system, Caster, from, passed, _cast.Data, flags);
        if (replacement is null) return false;
        _chain[_hop + 1] = replacement.NetId;
        _hop++;
        if (_missileNetId != 0) system.Effects.SendChainMissileSync(Caster, _missileNetId, _chain);
        return true;
    }

    /// <summary>The arrival itself: the hit, the retirement of the missile and the flight's end hooks.</summary>
    private bool LandOnArrival(SpellSystem system, Entity target)
    {
        _flight?.Move(_position, target);
        // A launched missile survives its caster's death; only a caster dying during the windup cancels it (above). The hit runs on the
        // target dead or alive (batch 339 for a script's placemarker, batch 348 for every unit): the cast routine's hit pass
        // (the shared hit pass, called by the homing missile's arrival) acts on every target object
        // that still exists, with no life check, and the shipped scripts guard that arrival themselves: VeigarBalefulStrike's TargetExecute
        // opens with CO_IS_NOT_DEAD on its target before any damage, so a corpse gives no AP. CH1ConcussionGrenade's burst needs it at the
        // placemarker its ExpirationTimer killed in flight. The corpse takes no damage (CombatService.ApplyHit refuses a dead target) and no
        // hostile control or non-persistent hero buff (BbBuffRuntime); the rest of the hit is the script's (Disintegrate still stacks
        // Pyromania). For a chain the arrival still counts as a hop (the client counts every hop of its list).
        _host.Router.RunTargetExecute(_script, Caster, _slot, _rank, _cast.ManaCost, Ground(system, target.Position),
            Ground(system, target.Position), target, targetsHit: _hop, missileNetId: _missileNetId, itemId: _itemId);
        // A script that destroyed the missile in its hit ends the flight here, a chain's remaining hops included.
        var destroyedByScript = _flight is { Destroyed: true };
        if (_chain is not null && !destroyedByScript && AdvanceChainHop(system, _position, arrivedDead: !target.Alive)) return true;
        if (_missileNetId != 0 && !destroyedByScript) RetireMissile(system);
        _flight?.End(_position, target);
        _forget(this);
        return false;
    }

    private bool TickWindup(SpellSystem system, double dt)
    {
        _castRemaining -= dt;
        if (_castRemaining > 1e-9) return true;
        _castRemaining = 0.0;
        _forget(this);
        // Other delayed Annie effects require their living caster at resolve time.
        if (!Caster.Alive) return false;
        _castFrameRun = true;
        system.ChargeChampionCast(Caster, _slot, _cast);
        _atWindupEnd?.Invoke();
        foreach (var unit in _windupTargets?.Invoke(system) ?? Cone.Units(system, _cast.Data, _origin, _direction, Caster))
            _host.Router.RunTargetExecute(_script, Caster, _slot, _rank, _cast.ManaCost, _aim, _aim, unit, itemId: _itemId);
        return false;
    }

    /// <summary>The target must still be a legal one: an enemy for the missile spells, else what the spell's flags admit.</summary>
    private bool StillAdmitted(Entity target) => _targetFlags is null
        ? WorldState.IsEnemy(Caster, target)
        : BbSpellTargeting.RejectTarget(Caster, target, _targetFlags) is null;

    private static Position3 Ground(SpellSystem system, Position2 point) =>
        new(point.X, system.Movement.Navigation.HeightAt(point), point.Z);
}
