using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>Queues a typed delayed hit for server scripted effects.</summary>
    public void QueueScriptedHit(Entity source, Entity target, double damage,
        DamageType damageType, DamageSourceKind sourceKind, double windup,
        double missileSpeed = 0.0, uint missileNetId = 0,
        DeathRecapOrigin? recapOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(damage) || damage < 0.0 || !double.IsFinite(windup) || windup < 0.0 ||
            !double.IsFinite(missileSpeed) || missileSpeed < 0.0)
            throw new ArgumentOutOfRangeException(nameof(damage));
        _pendingHits.Add(new PendingHit(source.NetId, target.NetId, damage, damageType,
            sourceKind, windup, source.Position, missileSpeed, missileNetId,
            RecapOrigin: recapOrigin)
        {
            TargetChampionDeaths = target.ChampionDeaths,
            TargetYorickRevival = target.YorickRevival,
        });
    }

    /// <summary>
    /// Cancels only attacks that are still in their positive wind-up phase.
    /// Launched projectiles remain in the queue and keep their missile ID and
    /// cancellation/impact callbacks, matching cast interruption semantics.
    /// </summary>
    public int InterruptBasicAttack(Entity attacker)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        // A planted swing has sent nothing yet; dropping it needs no stop.
        _plantedHeroSwings.Remove(attacker.NetId);
        return CancelPendingBasicWindups(attacker.NetId, requireMissile: false);
    }

    // A queued basic attack dropped before its hit is a swing the attacker's client still plays (its 0x0D told it, and no cast has
    // replaced it since); the next stop-attack (CombatPackets.StopAttack) says so and the client does not play it. A hit that
    // already launched is not a swing: its shot is retired by the hook, and since the server no longer keeps that hit no stop may
    // force the swing's cast frame (which would launch the retired shot after the hook destroyed it).
    private void NotifyHitCanceled(PendingHit hit)
    {
        if (hit.SourceKind == DamageSourceKind.Basic && hit.MissileNetId != 0 &&
            _world.Get(hit.SourceNetId) is { } source && source.ClientSwingMissileNetId == hit.MissileNetId)
        {
            if (hit.WindupRemaining > 0.0) source.ClientSwingDropped = true;
            else source.ClientSwingMissileNetId = 0;
        }
        _hooks.OnPendingHitCanceled(hit);
    }

    private int CancelPendingBasicWindups(uint sourceNetId, bool requireMissile,
        bool notifyClient = false)
    {
        var canceled = 0;
        for (var index = _pendingHits.Count - 1; index >= 0; index--)
        {
            var hit = _pendingHits[index];
            if (hit.SourceNetId != sourceNetId || hit.SourceKind != DamageSourceKind.Basic ||
                hit.WindupRemaining <= 0.0 || (requireMissile && hit.MissileNetId == 0)) continue;
            _pendingHits.RemoveAt(index);
            // Reconstruction: the retail server timer is unrecovered; canceling
            // an unlaunched basic swing permits a fresh windup immediately.
            if (hit.MissileNetId != 0 && _world.Get(sourceNetId) is { } attacker)
                attacker.AttackCooldown = 0.0;
            NotifyHitCanceled(hit);
            if (notifyClient) _hooks.OnPendingBasicAttackCanceled(hit);
            canceled++;
        }
        return canceled;
    }

    /// <summary>Cancels every pending hit owned by one scripted attacker.</summary>
    public int CancelPendingHits(uint sourceNetId)
    {
        var canceled = 0;
        for (var index = _pendingHits.Count - 1; index >= 0; index--)
        {
            if (_pendingHits[index].SourceNetId != sourceNetId) continue;
            var hit = _pendingHits[index];
            _pendingHits.RemoveAt(index);
            NotifyHitCanceled(hit);
            canceled++;
        }
        return canceled;
    }

    /// <summary>Cancels only positive-windup hits, preserving launched projectiles.</summary>
    public int CancelPendingWindups(uint sourceNetId)
    {
        var canceled = 0;
        for (var index = _pendingHits.Count - 1; index >= 0; index--)
        {
            var hit = _pendingHits[index];
            if (hit.SourceNetId != sourceNetId || hit.WindupRemaining <= 0.0) continue;
            _pendingHits.RemoveAt(index);
            NotifyHitCanceled(hit);
            canceled++;
        }
        return canceled;
    }

    /// <summary>Cancels launched missile hits aimed at a unit, preserving melee windups.</summary>
    public int CancelPendingTargetMissiles(uint targetNetId)
    {
        var canceled = 0;
        for (var index = _pendingHits.Count - 1; index >= 0; index--)
        {
            var hit = _pendingHits[index];
            if (hit.TargetNetId != targetNetId || hit.WindupRemaining > 0.0 ||
                hit.MissileNetId == 0) continue;
            _pendingHits.RemoveAt(index);
            NotifyHitCanceled(hit);
            canceled++;
        }
        return canceled;
    }

    private int ProcessPendingHits(double dt)
    {
        var resolved = 0;
        // Hit callbacks can cancel another windup (Power Fist, revival) or
        // queue new work. Keep the live queue authoritative and process only
        // the starting snapshot; never restore a canceled hit afterwards.
        // ponytail: linear lookup per hit; index by hit identity if measured
        // projectile counts make this small per-tick queue a bottleneck.
        foreach (var hit in _pendingHits.ToArray())
        {
            if (_gameOver) break;
            var index = _pendingHits.IndexOf(hit);
            if (index < 0) continue;
            var target = _world.Get(hit.TargetNetId);
            // Retail rule (SpellSystem.DestroyTargetedProjectiles): a missile in the air whose target died is not destroyed; the
            // client's homing missile follows the target object, alive or not, and ends at the corpse. The hit
            // therefore flies on and lands without effect (ResolvePendingHit). A target that is gone, or one that lives again as
            // another life (it respawned), still cancels the hit and takes the missile out.
            var inFlight = hit.WindupRemaining <= 0.0 && hit.MissileNetId != 0 && hit.MissileSpeed > 0.0;
            if (target is null || (!target.Alive && !inFlight) ||
                (target.Alive && (target.ChampionDeaths != hit.TargetChampionDeaths ||
                                  !ReferenceEquals(target.YorickRevival, hit.TargetYorickRevival))))
            {
                _pendingHits.RemoveAt(index);
                NotifyHitCanceled(hit);
                continue;
            }
            var source = _world.Get(hit.SourceNetId);
            // Map1's ar_StopAttackRangeModifier gives a legal basic attack a
            // 100-unit live-range leash during its positive windup, melee or
            // ranged, for every mobile attacker (pets, clones, minions,
            // neutrals). Ranged heroes keep the leash in
            // HeroAttackOrderSystem.Chase (HasCommittedWindup); turrets never
            // leash. Launched shots and non-basic scripted hits keep their
            // own lifetimes.
            if (hit.WindupRemaining > 0.0 &&
                hit.SourceKind == DamageSourceKind.Basic && hit.MissileNetId != 0 &&
                source is not null && source.Alive &&
                (hit.MissileSpeed <= 0.0 ||
                 source.UnitType is not (UnitType.Hero or UnitType.Turret)) &&
                !InCommittedAttackRange(source, target))
            {
                _pendingHits.RemoveAt(index);
                NotifyHitCanceled(hit);
                _hooks.OnPendingBasicAttackCanceled(hit);
                continue;
            }
            // The source keeps the shot in windup for the whole tick that
            // crosses zero; travel starts on the following tick.
            if (hit.WindupRemaining > 0.0)
            {
                // Preserve the signed remainder for the crossing tick.  The
                // client/source keeps a negative wind-up until the next tick
                // begins projectile travel; clamping here loses that state
                // and makes replayed pending-hit snapshots diverge.
                _pendingHits[index] = hit with { WindupRemaining = hit.WindupRemaining - dt };
                if (hit.WindupRemaining <= dt && hit.SourceKind == DamageSourceKind.Basic &&
                    hit.MissileNetId != 0 && source is not null)
                {
                    RevealAtCastFrame(source, target);
                    // Before the launch hooks: Spray and Pray's BuffOnLaunchMissile spends the attack and may end the override.
                    StartPierceLine(source, target, hit);
                    _hooks.OnBasicAttackLaunch(source, target, hit.MissileNetId);
                }
                continue;
            }
            if (hit.MissileSpeed <= 0.0)
            {
                _pendingHits.RemoveAt(index);
                if (ResolvePendingHit(hit, target)) resolved++;
                continue;
            }
            var position = hit.ProjectilePosition;
            var distance = Math.Sqrt(DistanceSquared(position, target.Position));
            var travel = hit.MissileSpeed * dt;
            if (distance <= travel || distance == 0.0)
            {
                _pendingHits.RemoveAt(index);
                if (ResolvePendingHit(hit, target)) resolved++;
            }
            else
            {
                var scale = travel / distance;
                // The source retains the signed wind-up value throughout
                // homing; only the positive branch decrements it. Keeping
                // the negative remainder is observable in pending snapshots.
                _pendingHits[index] = hit with {
                    ProjectilePosition = new Position2(position.X + (target.Position.X - position.X) * scale,
                        position.Z + (target.Position.Z - position.Z) * scale) };
            }
        }
        return resolved;
    }

    /// <summary>
    /// Batch 575 (owner question 3): the line of a swing whose attack-override spell is a line missile (<c>CastType</c> 3). The one
    /// shipped spell is Spray and Pray's <c>SprayAndPrayAttack</c> (inibin: CastType 3, LineWidth 60, CastRange 1100, MissileSpeed 3000,
    /// flags enemies, neutral, minions, heroes, buildings, turrets, wards; script: <c>NotSingleTargetSpell</c>, a <c>TargetExecute</c> per unit
    /// hit), which the client flies as an <c>obj_SpellLineMissile</c> (DoCast_ClientAndServer creates one for CastType 1 to 3). Every unit
    /// other than the attacked one that the line meets on its 1100 takes the same swing: its rolled damage and result, through the attack
    /// callbacks (on-hit, the spell's <c>TargetExecute</c>), the way the attacked unit takes it. The wiki's history agrees for 1.0.0.126:
    /// projectile width 60 (V0.9.25.24), range 1100 (V1.0.0.72), "100% to all targets hit" (V0.9.22.15), 5 / 6 / 7 shots (V1.0.0.107).
    /// Reconstruction: the retail server's contact filter is not shipped; the line takes the units this server's line missiles take
    /// (<c>BbLineMissile</c>: the area gate with the spell's flags, which picks no ward or building, contact radius LineWidth plus the unit's
    /// radius, a stealthed unit other than a hero only when the attacker sees it).
    /// </summary>
    private void StartPierceLine(Entity source, Entity target, PendingHit hit)
    {
        if (hit.Outcome is not (BasicAttackOutcome.Normal or BasicAttackOutcome.Critical) ||
            source.ScriptAttackOverride is not { } attack || !_gameData.HasSpell(attack.SpellName)) return;
        var spell = _gameData.Spell(attack.SpellName);
        if (Amberline.Simulation.Spells.Bb.BbChampionModule.CastType(spell) != 3 || spell.MissileSpeed <= 0.0) return;
        var dx = target.Position.X - source.Position.X;
        var dz = target.Position.Z - source.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var direction = length > 1e-9 ? new Position2(dx / length, dz / length) : new Position2(source.Facing.X, source.Facing.Z);
        _pierceLines.Add(new PierceLine(hit, source.Position, direction, spell.LineRangeAt(attack.Level), spell.MissileSpeed,
            Amberline.Simulation.Spells.Champions.ChampionSpellSupport.LineWidth(spell, 0.0),
            Amberline.Simulation.Spells.Bb.BbSpellTargeting.Names(Amberline.Simulation.Spells.Bb.BbSpellTargeting.Flags(spell))));
    }

    private void ProcessPierceLines(double dt)
    {
        foreach (var line in _pierceLines.ToArray())
        {
            var source = _world.Get(line.Hit.SourceNetId);
            var from = line.At(line.Traveled);
            line.Traveled = Math.Min(line.Range, line.Traveled + line.Speed * dt);
            var to = line.At(line.Traveled);
            if (source is not null && !_gameOver)
            {
                var met = _world.Entities.Values
                    .Where(unit => unit.NetId != source.NetId && unit.NetId != line.Hit.TargetNetId && !line.Struck.Contains(unit.NetId) &&
                                   Amberline.Simulation.Spells.Bb.BbSpellTargeting.Admits(source, unit, line.Flags) && IsTargetable(unit, null) &&
                                   (unit.Kind == EntityKind.Hero || !unit.AnyBuffOrStatus(buff => buff.Payload.Stealthed) ||
                                    _hooks.IsTargetable(unit, source)))
                    .Select(unit => (Unit: unit, Contact: Geometry.SweptContact(from, to, unit.Position, line.Width + unit.CombatRadius)))
                    .Where(item => item.Contact is not null)
                    .OrderBy(item => item.Contact!.Value).ThenBy(item => item.Unit.NetId)
                    .Select(item => item.Unit).ToArray();
                foreach (var unit in met)
                {
                    line.Struck.Add(unit.NetId);
                    if (!unit.Alive) continue;
                    // Batch 591: the client's line attack has no collision of its own; LineMissileHitList runs its hit pass (an attack's
                    // HitEffectName and AfterEffectName, its hit sounds) on the unit (SpellPackets.LineMissileHitList).
                    if (_transport.IsWireReady(source) && _transport.IsWireReady(unit))
                        _transport.SendLineMissileHit(source, line.Hit.TargetNetId, line.Hit.MissileNetId, unit.NetId);
                    ApplyHit(source.NetId, unit.NetId, line.Hit.Damage, line.Hit.DamageType, DamageSourceKind.Basic,
                        typeByte: line.Hit.Outcome == BasicAttackOutcome.Critical ? (byte)0x83 : (byte)0x84, vampScale: line.Hit.VampScale,
                        recapOrigin: line.Hit.RecapOrigin, attackOverride: line.Hit.AttackOverride);
                }
            }
            if (source is null || line.Traveled >= line.Range - 1e-9) _pierceLines.Remove(line);
        }
    }

    private sealed class PierceLine(PendingHit hit, Position2 origin, Position2 direction, double range, double speed, double width,
        HashSet<string> flags)
    {
        public PendingHit Hit { get; } = hit;
        public Position2 Origin { get; } = origin;
        public Position2 Direction { get; } = direction;
        public double Range { get; } = range;
        public double Speed { get; } = speed;
        public double Width { get; } = width;
        public HashSet<string> Flags { get; } = flags;
        public double Traveled { get; set; }
        public HashSet<uint> Struck { get; } = new();
        public Position2 At(double distance) => new(Origin.X + Direction.X * distance, Origin.Z + Direction.Z * distance);
    }

    private readonly List<PierceLine> _pierceLines = new();

    private bool ResolvePendingHit(PendingHit hit, Entity target)
    {
        // A missile that reached its target's corpse hits nothing (the client's missile ends there on its own); a chain attack
        // still goes on from the corpse, as the client's chain missile does.
        if (!target.Alive)
        {
            _hooks.OnAttackMissileOnCorpse(hit, target);
            return false;
        }
        // The shrine's swing is its shipped TargetExecute (OrderTurretShrineBasicAttack.luaobj, ChaosTurretShrineBasicAttack.luaobj, identical):
        // BBIf Target CO_IS_TYPE_HERO -> BBApplyDamage TurretRules.ShrineDamage TRUE_DAMAGE DAMAGESOURCE_INTERNALRAW, BBElse the same with
        // DAMAGESOURCE_RAW. It replaces the attack's AD payload at impact. The call is native, not run through BuildingBlocks: the basic-attack
        // route is hero-only (BbHookRouter.RunBasicAttackScript), RunUnitAttackScript needs a champion-owned unit, and a routed hit would change
        // the death-recap origin from the tower's basic attack. Batch 227 (owner request, 25 October 2026).
        if (hit.SourceKind == DamageSourceKind.Basic &&
            _world.Get(hit.SourceNetId) is { } shrine && TurretRules.IsShrineTurret(shrine))
            return ApplyHit(hit.SourceNetId, hit.TargetNetId, TurretRules.ShrineDamage, DamageType.True,
                target.UnitType == UnitType.Hero ? DamageSourceKind.InternalRaw : DamageSourceKind.Raw,
                recapOrigin: DeathRecapOrigin.BasicAttack(CompactTeam(shrine.Team), DeathRecapSourceType.Tower));
        if (hit.Outcome is BasicAttackOutcome.Miss or BasicAttackOutcome.Dodge)
        {
            // An avoided attack only displays its result. Passing zero damage
            // through ApplyHit would still invoke damage and on-hit callbacks.
            var source = _world.Get(hit.SourceNetId);
            if (_transport.IsWireReady(target) && (source is null || _transport.IsWireReady(source)))
                _transport.SendApplyDamage(hit.SourceNetId, source, target, 0, hit.DamageType,
                    hit.Outcome == BasicAttackOutcome.Dodge ? (byte)2 : (byte)5);
            if (source is not null && hit.SourceKind == DamageSourceKind.Basic) _hooks.OnBasicAttackAvoided(source, target, hit.Outcome);
            return false;
        }
        // Batch 591: the attacked unit of a line attack (Spray and Pray) is a unit its line hits too, named before its hit like the others.
        if (hit.MissileNetId != 0 && _world.Get(hit.SourceNetId) is { } liner && _pierceLines.Any(line => line.Hit.MissileNetId == hit.MissileNetId) &&
            _transport.IsWireReady(liner) && _transport.IsWireReady(target))
            _transport.SendLineMissileHit(liner, hit.TargetNetId, hit.MissileNetId, target.NetId);
        return ApplyHit(hit.SourceNetId, hit.TargetNetId, hit.Damage, hit.DamageType, hit.SourceKind,
            hit.IsReflect, hit.SourceIsPeriodic, hit.SpellOwnerNetId,
            hit.Outcome == BasicAttackOutcome.Critical ? (byte)0x83 : (byte)0x84,
            hit.VampScale, hit.RecapOrigin, attackOverride: hit.AttackOverride);
    }
}
