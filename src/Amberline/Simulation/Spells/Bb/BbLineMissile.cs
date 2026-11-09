using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The line missile (<c>CastType</c> 3) of a spell script's extra-slot child cast (Sivir's Spiral Blade: <c>SpiralBladeMissile</c>), run
/// in the shared projectile queue. It starts when the parent's windup ends, flies straight at the missile speed for the child's cast
/// range and, because the child's <c>LineMissileBounces</c> is set, turns back toward its caster and flies home; every unit the
/// swept line meets takes the child script's <c>TargetExecute</c> once per pass (the hit list is emptied at the turn: the spell
/// text says it damages "each way"). The geometry mirrors the hand-written line missiles (contact radius = LineWidth + the unit's
/// combat radius, <c>Geometry.SweptContact</c>), the turn mirrors the missile's update routine:
/// at the end of its range a missile with a non-zero bounce count sets the bounced flag and aims at its caster's current position.
/// Two more data rules of the same function: <c>LineMissileTrackUnits</c> (the line chases the unit its cast named, see <see cref="Track"/>) and
/// <c>LineMissileCollisionFromStartPoint</c> (the swept contact starts at the launch point, see <see cref="Advance"/>).
/// </summary>
internal sealed class BbLineMissile : ISharedSpellProjectile
{
    // The module that keeps the line in its own flight list. A callback and not a BbChampionModule because an item's
    // ward placement (WriggleLantern's ItemPlacementMissile) casts the same line from BbItemModule.
    private readonly Action<BbLineMissile> _forget;
    private readonly BbSpellHost _host;
    private readonly BbScript _script;
    private readonly string _spellName;
    private readonly int _slot;
    private readonly int _rank;
    // The item whose use cast this line (0 for a champion's own child): its hit keeps that item's recap identity.
    private readonly int _itemId;
    private readonly SpellCastInfo _castInfo;
    private readonly uint _missileNetId;
    private readonly Position2 _aim;
    private double _range;
    private readonly bool _endsAtTargetPoint;
    private readonly bool _fromStart;
    private readonly Entity? _tracked;
    private double _speed;
    private readonly double _fixedTravel;
    private readonly double _width;
    private readonly HashSet<string> _flags;
    private readonly HashSet<uint> _hit = new();
    // The script-visible life of the missile: launch, update and end hooks, BBDestroyMissile.
    private readonly BbMissileFlight _flight;
    // The worldless caster the replication layer builds for a line cast by a unit the clients never saw (Janna's TestCube); null otherwise.
    private readonly MissileCasterProjection? _projection;
    private double _delay;
    private int _bounces;
    private bool _launched;
    private bool _returning;
    private double _traveled;
    private int _hits;
    // OverrideCastPosition of the BBSpellCast that made the line: it leaves from this point instead of from its caster.
    private readonly Position2? _launchFrom;
    private readonly bool _completesCast;
    // SpellData*Flags bit 2 (Instacast): the client runs such a cast through the instant-cast routine, which never
    // reaches the cast routine, so the cast answer creates no missile object (see Launch).
    private readonly bool _instant;
    // Batch 713: how far above the terrain the line flies (LaunchHeight); every point the clients and the scripts get from it carries it.
    private readonly double _height;
    private Position2 _origin;
    private Position2 _position;
    private Position2 _direction;

    public BbLineMissile(Action<BbLineMissile> forget, BbSpellHost host, BbScript script, string spellName, Entity caster, int slot, int rank,
        SpellCastInfo castInfo, double delay, Position2 aim, SpellData data, HashSet<string> flags, Entity? tracked = null,
        MissileCasterProjection? projection = null, bool completesCast = false, Position2? launchFrom = null, int itemId = 0,
        double launchHeight = 0.0)
    {
        _launchFrom = launchFrom;
        _height = LaunchHeight(caster, launchFrom is not null, launchHeight);
        _completesCast = completesCast;
        _projection = projection;
        _forget = forget;
        _host = host;
        _script = script;
        _spellName = spellName;
        Caster = caster;
        _slot = slot;
        _rank = rank;
        _itemId = itemId;
        _castInfo = castInfo;
        _missileNetId = castInfo.MissileNetId;
        _delay = Math.Max(0.0, delay);
        _aim = aim;
        _range = data.LineRangeAt(rank);
        _endsAtTargetPoint = BbChampionModule.RawCount(data, "SpellData*LineMissileEndsAtTargetPoint") != 0;
        _speed = data.MissileSpeed;
        _fixedTravel = data.MissileFixedTravelTime;
        _width = ChampionSpellSupport.LineWidth(data, 90.0);
        _flags = flags;
        _instant = data.IsInstant;
        _bounces = BbChampionModule.RawCount(data, "SpellData*LineMissileBounces");
        _fromStart = BbChampionModule.RawCount(data, "SpellData*LineMissileCollisionFromStartPoint") != 0;
        _tracked = tracked is not null && Tracks(data) ? tracked : null;
        _position = _origin = launchFrom ?? caster.Position;
        _flight = new BbMissileFlight(host, script, spellName, caster, slot, rank, 0.0, castInfo, data) { Height = _height };
    }

    /// <summary>
    /// Batch 713 (owner, 8 November 2026: Talon's W blade "flies towards his feet when going back... goes underground almost instantly"). How far above
    /// the terrain a line flies. The shared the client (AB 2605987-2606008) launches an <c>obj_SpellLineMissile</c> at
    /// the caster's <c>MissileBoneName</c> joint, which <c>GetJointPositionHelper</c> finds only through the unit's animation component (the client's
    /// actor), else 100 above the caster's position; a cast that overrides its position leaves from <c>CasterPos</c>, the 3D point the script gave.
    /// The line then keeps that height over the terrain (the client: the start and end
    /// heights are the ground's plus <c>mStartHeightFromGround</c>, which the 0x41 handler the client takes from the packet's position).
    /// Reconstruction: the server has no animated skeleton, so its line leaves 100 above its caster; the scripts' points carry the height on
    /// (MissileEndPosition, the spawn there, BBGetUnitPosition, OverrideCastPosVar), so Talon's returns leave from markers raised to the blade's height.
    /// </summary>
    internal static double LaunchHeight(Entity caster, bool overridesCastPosition, double castPositionHeight) =>
        overridesCastPosition ? castPositionHeight : caster.Height + LaunchHeightWithoutJoint;

    /// <summary>The launch height above the caster of a line whose caster has no joint pose (DoCast_ClientAndServer's fallback).</summary>
    internal const double LaunchHeightWithoutJoint = 100.0;

    /// <summary>Whether the spell's line chases the unit its cast named (<c>SpellData*LineMissileTrackUnits</c>).</summary>
    public static bool Tracks(SpellData data) => BbChampionModule.RawCount(data, "SpellData*LineMissileTrackUnits") != 0;

    public Entity Caster { get; }
    public bool Cancelled { get; private set; }

    public SharedProjectileSnapshot Snapshot => new(_spellName, Caster.NetId, 0, _missileNetId, _delay, _launched, _position, _speed,
        Rank: _rank, Slot: _slot, CastWindupRemaining: _delay, Origin: _origin, Direction: _direction, CastInfo: _castInfo);

    /// <summary>The caster died before the line left: it never does.</summary>
    public void CancelBeforeLaunch(SpellSystem system)
    {
        if (Cancelled || _launched) return;
        Cancelled = true;
        if (_missileNetId != 0) system.Effects.RetireCastMissile(_missileNetId);
    }

    public bool Tick(SpellSystem system, double dt)
    {
        if (Cancelled)
        {
            _forget(this);
            return false;
        }
        var left = dt;
        if (!_launched)
        {
            var wait = Math.Min(left, _delay);
            _delay -= wait;
            left -= wait;
            if (_delay > 1e-9) return true;
            Launch();
            // Reconstruction (owner playtest 28 September 2026, "Ezreal Q kill leaves the missile"; kept from the hand-written Ezreal and Lee Sin Q):
            // a windup child's cast frame is completed on the client before the missile can be retired. The client runs its own cast frame
            // DesignerCastTime after the cast packet reaches it and creates the missile there (the cast routine creates it when its network id is
            // unknown); a DestroyMissile that arrives first (a unit overlapping the caster is hit on the line's first step) finds nothing to
            // destroy and the missile then flies on. PKT 0x39 with the flags 0x07 (KeepAnimating, ForceSpellCast, ForceStop)
            // executes an outstanding cast frame once and ends the cast controller, so the ordered stream is cast, finish,
            // missile created, missile retired.
            if (_completesCast && Caster.UnitType == UnitType.Hero && system.Effects.IsReferenceReady(Caster)) system.Effects.SendFinishCast(Caster);
            var announced = false;
            if (_projection is { } projected)
            {
                SendProjected(system, projected);
                announced = true;
            }
            else if (_instant) announced = SendInstantCreation(system);
            // Batch 713 (owner, 8 November 2026: Talon's R blades "sometimes" end early). A line announced by its own creation packet (an Instacast
            // line, a worldless caster's) is at its start on the client when that packet arrives: the 0x41 handler puts the
            // object at the packet's position and the line update moves it from there. The packet leaves with this step's flush, so the line
            // flies from the next step: the rest of this one flown here put the server one step (1850 x 1/30 = 62 units) ahead of every client,
            // and the 0x5D of a returning blade that reached Talon cut the client's blade that far short of him. Reconstruction: when retail's
            // server first moved a missile it created is server-only; the client's start is the packet's.
            if (announced) left = 0.0;
            // The line leaves its caster: BuffOnLaunchMissile of the caster's script buffs.
            _flight.Launch(_position, null);
        }
        // BBDestroyMissile flagged the line (the client was told then): it ends at this step.
        if (_flight.Destroyed) return Finish(system);
        // A launched missile survives its caster's death (stock missile updates need the caster object, not a living caster).
        var remainingDistance = left * _speed;
        if (_tracked is not null) return Track(system, remainingDistance);
        // Reconstruction: a pass can turn and meet the first units of the next in one step; the loop keeps the step's leftover.
        for (var pass = 0; pass < 3 && remainingDistance > 1e-9; pass++)
        {
            if (!_returning)
            {
                var step = Math.Min(remainingDistance, _range - _traveled);
                Advance(system, _position + new Position2(_direction.X * step, _direction.Z * step), step);
                remainingDistance -= step;
                if (_flight.Destroyed) return Finish(system);
                if (_range - _traveled > 1e-9) break;
                if (_bounces <= 0) return Finish(system);
                // The turn: hits are forgotten, the missile heads for where its caster stands now.
                _bounces--;
                _returning = true;
                _hit.Clear();
            }
            else
            {
                var home = Caster.Position;
                var distance = ChampionSpellSupport.Distance(_position, home);
                var step = Math.Min(remainingDistance, distance);
                if (distance > 1e-9)
                {
                    var toward = ChampionSpellSupport.Direction(_position, home);
                    _direction = new Position2(toward.X, toward.Z);
                }
                Advance(system, distance <= 1e-9 ? _position : _position + new Position2(_direction.X * step, _direction.Z * step), step);
                if (_flight.Destroyed || distance - step <= 1e-9) return Finish(system);
                break;
            }
        }
        var end = _returning ? Caster.Position : _origin + new Position2(_direction.X * _range, _direction.Z * _range);
        ChampionSpellSupport.RefreshMissile(system, Caster, _castInfo, _origin, _position, end, _speed, casterProjection: _projection, lineHeight: _height);
        return true;
    }

    /// <summary>The first packet of a line cast by a worldless caster: the missile at its start, flying along the line at its speed.</summary>
    private void SendProjected(SpellSystem system, MissileCasterProjection projection)
    {
        var ground = ChampionSpellSupport.Ground(system, _origin);
        var start = ChampionSpellSupport.Raised(ground, _height);
        var end = ChampionSpellSupport.Raised(ChampionSpellSupport.Ground(system, _origin + new Position2(_direction.X * _range, _direction.Z * _range)), _height);
        system.Effects.SendProjectedMissile(projection, _castInfo, start, new Position3(_direction.X, 0.0, _direction.Z),
            new Position3(_direction.X * _speed, 0.0, _direction.Z * _speed), start, end, ground, ground);
    }

    /// <summary>
    /// The creation packet of a line whose spell is Instacast (<c>SpellData*Flags</c> bit 2). The cast answer alone does not build the
    /// missile for one: the client's cast routine (the typed client's version; the matching build's
    /// the same routine) hands a cast whose <c>CasterPos</c> z is non-zero to the instant-cast routine
    /// (the instant-cast routine), which plays the sound, applies the costs and runs the cast routine but returns before
    /// the cast routine, the only place an <c>obj_SpellLineMissile</c> is created. Only the missile packet
    /// (<c>Game_ProcessWorldEvent2</c> case 0x41) reaches that function directly, so an instant line must send it: without it the client has
    /// no missile object and never starts the spell's <c>MissileEffectName</c> trail (Orianna's <c>Oriana_Ghost_mis.troy</c> /
    /// <c>Oriana_Ghost_mis_protect.troy</c>: the owner's "ball invisible while travelling via Q or E", 24 October 2026).
    /// <para>
    /// The payload is the live one <see cref="ChampionSpellSupport.RefreshMissile"/> sends, at the launch point, so a later refresh only
    /// moves the same object. <c>unitPosition</c> is the caster's own ground position: the client's 0x41 branch applies the packet's
    /// <c>unitPos</c> to the caster's actor (the cast routine's cases 1, 2 and 3, the packet's
    /// <c>unitPos</c> field), and naming the launch point there would step Orianna to her ball (the 15.4-unit Q backstep of the
    /// 24 September native retest).
    /// </para>
    /// </summary>
    private bool SendInstantCreation(SpellSystem system)
    {
        if (_missileNetId == 0 || !system.Effects.IsReferenceReady(Caster)) return false;
        var start = ChampionSpellSupport.Raised(ChampionSpellSupport.Ground(system, _origin), _height);
        var end = ChampionSpellSupport.Raised(ChampionSpellSupport.Ground(system,
            _tracked is { } tracked ? tracked.Position : _origin + new Position2(_direction.X * _range, _direction.Z * _range)), _height);
        system.Effects.SendMissile(Caster, _castInfo, start, new Position3(_direction.X, 0.0, _direction.Z),
            new Position3(_direction.X * _speed, 0.0, _direction.Z * _speed), start, end,
            ChampionSpellSupport.Ground(system, Caster.Position), needsCastAnnouncement: true);
        return true;
    }

    /// <summary>
    /// <c>LineMissileTrackUnits</c>: the missile's update routine looks the missile's first target up every
    /// update and, when the spell sets the flag (its flag) and the object still exists, makes the unit's current position the
    /// destination (its position plus the spell's missile height) and aims at it again; a missing caster destroys the missile. The
    /// arrival test is the usual one against that destination, so the line has no range of its own: it ends when its step reaches
    /// the unit, however far the unit went (Caitlyn's Ace in the Hole and Talon's second wave list 20000). A dead unit that is still in the
    /// object manager (a hero's corpse) is tracked all the same; one that is gone ends the line. The hits along the way are the ordinary
    /// swept contacts, and the tracked unit is met on the last step. The client has no cap on the flight: the tracking branch has no time or
    /// distance test (its timer is written only once the line is done). A line that cannot arrive stays
    /// until its caster dies or its hit budget ends it, as in the client.
    /// A line that tracks does not bounce (no shipped spell sets both).
    /// </summary>
    private bool Track(SpellSystem system, double remainingDistance)
    {
        if (system.World.Get(_tracked!.NetId) is null || system.World.Get(Caster.NetId) is null)
            return Finish(system);
        var goal = _tracked.Position;
        var distance = ChampionSpellSupport.Distance(_position, goal);
        var step = Math.Min(remainingDistance, distance);
        if (distance > 1e-9)
        {
            var toward = ChampionSpellSupport.Direction(_position, goal);
            _direction = new Position2(toward.X, toward.Z);
        }
        Advance(system, distance <= 1e-9 ? _position : _position + new Position2(_direction.X * step, _direction.Z * step), step);
        // A script that destroyed the line ends it here; reaching the unit ends it with the unit as the end hook's target, as a homing missile's.
        if (_flight.Destroyed) return Finish(system);
        if (distance - step <= 1e-9)
        {
            // Batch 707 (owner, 7 November 2026, Talon: "W VFX disappears when coming back; R VFX disappears too quickly when coming back (should
            // fly into talon)"): the tracked unit is met here, on arrival, and not by the sweep (Advance). The shared update
            // aims a tracking line at its first target's position every update and,
            // when the line reaches that point with no bounce or destroy delay left, hands the target alone to OnMissileCollide and ends. The
            // return blades (TalonRakeMissileTwo, TalonShadowAssaultMisTwo: AffectFriends, no NotAffectSelf) track Talon himself and their
            // TargetExecute destroys them on him; the sweep met him at LineWidth plus his radius (140 and 315 units out), so the 0x5D removed
            // each blade that far from him (obj_SpellMissile's DestroyClientMissile handler flags it at once). Reconstruction: whether retail's
            // server-only sweep also skipped the tracked unit is not in the client; the arrival collide and the scripts' Target == Owner branch are.
            if (_tracked.Alive && !_hit.Contains(_tracked.NetId) && Admissible(system, _tracked)) Strike(system, _tracked);
            return Finish(system, _tracked);
        }
        ChampionSpellSupport.RefreshMissile(system, Caster, _castInfo, _origin, _position, goal, _speed, casterProjection: _projection, lineHeight: _height);
        return true;
    }


    private void Launch()
    {
        _launched = true;
        _origin = _position = _launchFrom ?? Caster.Position;
        var toward = ChampionSpellSupport.Direction(_origin, _aim);
        // A cast at the caster's own feet flies where he faces.
        _direction = ChampionSpellSupport.Distance(_origin, _aim) <= 1e-9
            ? new Position2(Caster.Facing.X, Caster.Facing.Z) : new Position2(toward.X, toward.Z);
        // SpellData*LineMissileEndsAtTargetPoint (the flag the location targeter reads when it places a direction
        // cast's point): the line ends at the point it was aimed at when that is inside the cast range, instead of flying the whole range.
        // Orianna's Izuna, Rumble's carpet bomb, Gragas' barrel, Lux's Lucent Singularity and Olaf's axe set it; a point beyond the range
        // still gets the full range (the client clamps the aim to it). Whether a first hit ends a line is the script's: 28 of the 68 shipped
        // line missiles call BBDestroyMissile from their TargetExecute, and no spell data key says so.
        var aimDistance = ChampionSpellSupport.Distance(_origin, _aim);
        if (_endsAtTargetPoint && aimDistance > 1e-9 && aimDistance < _range) _range = aimDistance;
        // Batch 331: MissileFixedTravelTime: the client's launch gives the missile the speed distance / time instead of
        // MissileSpeed, so the line reaches its end that long after it left (Riven's Wind Slash waves: 1.0 s).
        if (_fixedTravel > 0.0) _speed = _range / _fixedTravel;
    }

    private void Advance(SpellSystem system, Position2 next, double step)
    {
        // Reconstruction: LineMissileCollisionFromStartPoint (Nocturne's Duskbringer) is only parsed and copied by the client,
        // which never reads it back (the contact test is the server's), so the rule is read from its name: the swept contact starts at the
        // line's launch point instead of where the head was a tick ago. The whole line behind the head is tested on every step, so a unit that
        // walks into the path after the head has gone by is still struck (once: the hit set), and a unit standing behind the head at the cast is
        // met by the first step. The bounce pass keeps its own sweep.
        var old = _fromStart && !_returning ? _origin : _position;
        _traveled += _returning ? 0.0 : step;
        // Batch 582 (owner finding 121, Urgot R's arrows; Reconstruction): a line at least as wide as it is long meets a unit when its head reaches
        // the unit's body, not when the unit enters the round end of the width around the head. Urgot's swap arrows (UrgotSwapMissile and
        // UrgotSwapMissile2: LineWidth 1200, CastRange 800, the only shipped lines this wide) are cast at the other champion about 800 away, and their
        // TargetExecute destroys them on the unit that holds UrgotSwapMarker; with the round end (1200 + 65) every arrow met that champion on its
        // first step and its 0x5D left in the flush of its 0x41, so no arrow flew. The owner's retail look: the arrows fly from one champion to the
        // other. Retail's contact geometry is server-only; narrower lines keep the round end.
        var square = _width >= _range;
        // A tracking line meets the unit it tracks on arrival only (Track, batch 707).
        var hits = system.World.Entities.Values
            .Where(unit => !_hit.Contains(unit.NetId) && !ReferenceEquals(unit, _tracked) && Admissible(system, unit))
            .Select(unit => (Unit: unit, Contact: square
                ? Geometry.SweptHeadContact(old, next, unit.Position, _width + unit.CombatRadius, unit.CombatRadius)
                : Geometry.SweptContact(old, next, unit.Position, _width + unit.CombatRadius)))
            .Where(item => item.Contact is not null)
            .OrderBy(item => item.Contact!.Value).ThenBy(item => item.Unit.NetId)
            .ToArray();
        _position = next;
        _flight.Move(next);
        foreach (var (unit, _) in hits)
        {
            // A script that destroyed the missile (its update hook, or the hit before: Rocket Grab, Mystic Shot) takes no further unit.
            if (!unit.Alive || _flight.Destroyed) continue;
            Strike(system, unit);
        }
    }

    private bool Admissible(SpellSystem system, Entity unit) =>
        BbSpellTargeting.Admits(Caster, unit, _flags) && system.Combat.IsTargetable(unit, null) &&
        ChampionSpellSupport.ScriptCollisionAllows(system, Caster, unit);

    private void Strike(SpellSystem system, Entity unit)
    {
        _hit.Add(unit.NetId);
        // A line cast by a placemarker (Janna's wave from the cube) reveals the hero that owns it, not the cube.
        var revealed = _projection is { } owner ? system.World.Get(owner.OwnerNetId) ?? Caster : Caster;
        system.RevealMissileHit(revealed, _spellName, unit);
        // Batch 591 (owner decision, 1 November 2026): LineMissileHitList names the unit before its hit runs, so it reaches the client ahead
        // of the hit's 0x5D (the handler skips a missile that is over; SpellPackets.LineMissileHitList). One packet per unit: a script that destroys
        // the line on this hit (Mystic Shot, Thundering Shuriken) keeps the next unit of the step unhit. Reconstruction: retail's grouping is not in
        // the client, which takes any count.
        system.Effects.SendLineMissileHitList(_missileNetId, [unit.NetId]);
        var at = new Position3(unit.Position.X, system.Movement.Navigation.HeightAt(unit.Position), unit.Position.Z);
        _host.Router.RunTargetExecute(_script, Caster, _slot, _rank, 0.0, at, at, unit, targetsHit: _hits++, missileNetId: _missileNetId,
            itemId: _itemId);
    }

    private bool Finish(SpellSystem system, Entity? reached = null)
    {
        // A destroyed missile's client object went at the call; the natural end tells the client now.
        if (_missileNetId != 0 && !_flight.Destroyed) system.Effects.SendDestroyMissile(_missileNetId);
        // Batch 695: only a projection that stands for no unit of the entity paths is retired with its line. A world unit they serve (any but an
        // un-announced placemarker: Janna's TestCube is announced, Entity.Announced) stays on the clients until its own death, which they send;
        // retiring it put an 0xA6 on the wire while it lived, and the next visibility refresh created it a second time.
        if (_projection is { } projected && system.World.Get(projected.NetId) is null or { Placemarker: true, Announced: false })
            system.Effects.RetireProjectedCaster(projected.NetId);
        _flight.End(_position, reached);
        _forget(this);
        return false;
    }
}
