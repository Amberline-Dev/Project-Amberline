using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The circle missile (<c>CastType</c> 4) of a spell script's extra-slot child cast, new in 1.0.0.132 for one spell: Ahri's Fox-Fire
/// (<c>AhriFoxFire</c>'s SelfExecute casts three <c>AhriFoxFireMissile</c> from points 150 around her, <c>TargetVar</c> Attacker). The 132 client
/// builds it in the stock cast routine (its CastType 4 branch: <c>obj_SpellCircleMissile</c> at the launch point, the override
/// cast position when the cast has one) and moves it itself:
/// <list type="bullet">
/// <item>The circle missile's OnCreate (the stock routine): the centre is the cast's one target unit (asserted <c>getNumTargets() &lt;= 1</c>), or the
/// cast's target point without one; the radius is the launch point's ground distance from it and the angle its <c>atan2</c>; the two rates are the
/// spell's <c>CircleMissileRadialVelocity</c> and <c>CircleMissileAngularVelocity</c> (the two rate fields of its SpellData); the missile ends
/// <c>MissileLifetime</c> seconds later (a lifetime of 0 never ends it by time); its height is the centre's plus <c>MissileTargetHeightAugment</c>.</item>
/// <item>The circle missile's UpdateProjectile (the stock routine): each frame the centre is the target unit's position again (the orbit follows
/// it), the missile heads for <c>centre + radius (cos angle, sin angle)</c>, and then the angle grows by angular velocity x dt and the radius by
/// radial velocity x dt.</item>
/// </list>
/// The scripts see it through <see cref="BbMissileFlight"/>: <c>SpellOnMissileUpdate</c> per <c>LuaOnMissileUpdateDistanceInterval</c> flown (Fox-Fire's
/// 75 counts <c>SpellVars.Ready</c> to three, then each fox-fire casts <c>AhriFoxFireMissileTwo</c> at the closest visible enemy within 650 and
/// destroys itself), <c>BBDestroyMissile</c>, and the end hooks. Reconstruction: the update's per-level hit budget
/// (<c>numMaxHitsAtCurrentLevel</c>) and the shared missile collision are server-only work the client does not show; no shipped circle missile has a
/// <c>TargetExecute</c>, so this missile strikes nothing by itself.
/// </summary>
internal sealed class BbCircleMissile : ISharedSpellProjectile
{
    private readonly Action<BbCircleMissile> _forget;
    private readonly string _spellName;
    private readonly int _rank;
    private readonly int _slot;
    private readonly SpellCastInfo _castInfo;
    private readonly uint _missileNetId;
    private readonly Entity? _centerUnit;
    private readonly Position2 _launchFrom;
    private readonly double _radialVelocity;
    private readonly double _angularVelocity;
    private readonly double _lifetime;
    private readonly BbMissileFlight _flight;
    private Position2 _center;
    private Position2 _position;
    private double _radius;
    private double _angle;
    private double _age;
    private bool _launched;

    public BbCircleMissile(Action<BbCircleMissile> forget, BbSpellHost host, BbScript script, string spellName, Entity caster, int slot, int rank,
        SpellCastInfo castInfo, SpellData data, Entity? centerUnit, Position2 center, Position2 launchFrom)
    {
        _forget = forget;
        _spellName = spellName;
        Caster = caster;
        _slot = slot;
        _rank = rank;
        _castInfo = castInfo;
        _missileNetId = castInfo.MissileNetId;
        _centerUnit = centerUnit;
        _center = center;
        _position = _launchFrom = launchFrom;
        _radialVelocity = ChampionSpellSupport.RawNumber(data, "SpellData*CircleMissileRadialVelocity");
        _angularVelocity = ChampionSpellSupport.RawNumber(data, "SpellData*CircleMissileAngularVelocity");
        _lifetime = ChampionSpellSupport.RawNumber(data, "SpellData*MissileLifetime");
        _flight = new BbMissileFlight(host, script, spellName, caster, slot, rank, 0.0, castInfo, data, centerUnit?.NetId ?? 0)
        {
            Height = ChampionSpellSupport.RawNumber(data, "SpellData*MissileTargetHeightAugment"),
        };
    }

    public Entity Caster { get; }
    public bool Cancelled { get; private set; }

    /// <summary>The missile's point now (for the checks that follow it).</summary>
    internal Position2 Position => _position;

    public SharedProjectileSnapshot Snapshot => new(_spellName, Caster.NetId, _centerUnit?.NetId ?? 0, _missileNetId, 0.0, _launched, _position,
        Math.Abs(_angularVelocity) * _radius, Rank: _rank, Slot: _slot, Origin: _center, CastInfo: _castInfo);

    /// <summary>The caster died before the missile left: it never does.</summary>
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
        if (!_launched)
        {
            _launched = true;
            _center = CenterNow(system);
            _radius = ChampionSpellSupport.Distance(_center, _launchFrom);
            _angle = Math.Atan2(_launchFrom.Z - _center.Z, _launchFrom.X - _center.X);
            _flight.Launch(_position, _centerUnit);
            AnnounceLaunch(system);
        }
        if (_flight.Destroyed) return Finish(system);
        _age += dt;
        if (_lifetime > 0.0 && _age >= _lifetime - 1e-9) return Finish(system);
        _center = CenterNow(system);
        // The client's order: head for the point of the current angle and radius, then advance both.
        var next = new Position2(_center.X + _radius * Math.Cos(_angle), _center.Z + _radius * Math.Sin(_angle));
        _angle += _angularVelocity * dt;
        _radius += _radialVelocity * dt;
        _position = next;
        _flight.Move(next, _centerUnit);
        // Batch 901: the live missile for a view that holds no copy of it (one that first sees Ahri meanwhile), as the homing children refresh theirs
        // (BbSpellProjectile); a view that holds it is skipped (ReplicationDelivery's RefreshOnly rule).
        if (!_flight.Destroyed)
            ChampionSpellSupport.RefreshMissile(system, Caster, _castInfo, _launchFrom, _position, _center, Math.Abs(_angularVelocity) * _radius);
        return !_flight.Destroyed || Finish(system);
    }

    /// <summary>
    /// Batch 901 (owner finding, 19 November 2026: "Ahri W doesn't show the VFX around her"): the circle missile's creation 0x41. AhriFoxFireMissile
    /// is Instacast (SpellData Flags bit 2), so its child cast reaches every view as its missile's creation alone (batch 582, SendChildCast's
    /// instantMissile: the views are tracked, no cast answer goes out), as the homing and line children announce theirs (BbSpellProjectile
    /// SendInstantCreation, BbLineMissile). The circle missile sent none, so no client built the three fox-fires that orbit Ahri: they appeared only
    /// as the second missiles leaving for a target. The 0x41 handler runs the stock cast routine, whose CastType 4 branch builds the orbit from the
    /// cast info (its target unit the centre, the launch point the radius and angle). Reconstruction: retail's wire for this child is not in the client.
    /// </summary>
    private void AnnounceLaunch(SpellSystem system)
    {
        if (_missileNetId == 0 || !system.Effects.IsReferenceReady(Caster)) return;
        var start = ChampionSpellSupport.Ground(system, _launchFrom);
        // The orbit's tangent at the launch point, the way the angle turns (the sign of the angular velocity).
        var radial = new Position2(_launchFrom.X - _center.X, _launchFrom.Z - _center.Z);
        var length = Math.Sqrt(radial.X * radial.X + radial.Z * radial.Z);
        var sign = _angularVelocity < 0.0 ? -1.0 : 1.0;
        var direction = length > 1e-9 ? new Position3(-radial.Z / length * sign, 0.0, radial.X / length * sign) : new Position3(0.0, 0.0, 1.0);
        var speed = Math.Abs(_angularVelocity) * _radius;
        system.Effects.SendMissile(Caster, _castInfo, start, direction, new Position3(direction.X * speed, 0.0, direction.Z * speed), start,
            ChampionSpellSupport.Ground(system, _center), ChampionSpellSupport.Ground(system, Caster.Position), needsCastAnnouncement: true);
    }

    // The target unit's position while the object manager still holds it (a hero's corpse included), else the last centre.
    private Position2 CenterNow(SpellSystem system) =>
        _centerUnit is { } unit && system.World.Get(unit.NetId) is not null ? unit.Position : _center;

    private bool Finish(SpellSystem system)
    {
        // A destroyed missile's client object went at the call; one that outlived MissileLifetime ends on the client by itself, and is told too.
        if (_missileNetId != 0 && !_flight.Destroyed) system.Effects.SendDestroyMissile(_missileNetId);
        _flight.End(_position, _centerUnit);
        _forget(this);
        return false;
    }
}
