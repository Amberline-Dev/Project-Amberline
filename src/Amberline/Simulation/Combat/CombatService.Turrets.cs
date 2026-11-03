using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>
    /// <c>TurnOnAutoAttack</c>/<c>TurnOffAutoAttack</c>: the flag the turret's swing gate reads.
    /// TurnOn also re-affirms its swing target (the script always passes <c>GetTarget()</c>);
    /// TurnOff retires the client's target display without touching the id the script kept.
    /// </summary>
    public void SetTurretAutoAttack(Entity turret, Entity? target, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(turret);
        // The flag first: a Crystal Scar shrine's laser follows it (PublishTurretDisplay).
        if (enabled) _turretAutoAttack.Add(turret.NetId);
        // TurnOnAutoAttack's argument is the swing target; Turret.luaobj always passes GetTarget(),
        // so this only ever re-affirms what SetTarget already set.
        if (enabled && target is not null) SetTurretTarget(turret, target);
        if (!enabled)
        {
            _turretAutoAttack.Remove(turret.NetId);
            PublishTurretDisplay(turret, null);
        }
    }

    // Reconstruction: Turret.luaobj picks its target with the native FindTargetInAcR (no Lua-visible
    // rule; DATA/Scripts/Turret.luaobj only runs the 0.15 s TimerFindEnemies and re-asks it on
    // OnTargetLost). The 126 client keeps none of the server's selector (the base unit's better-target tests
    // are asserting stubs, obj_AI_Hero's only say "a hero other than the local player is better").
    // The order used here is the period behaviour: summoned units first, then lane minions, then
    // anything else (heroes), the nearest of each class. A champion that hit an allied champion
    // takes the turret over through OnCallForHelp (CallForHelp in CombatAwareness).
    public void SetTurretTarget(Entity turret, Entity? target)
    {
        // Eligibility and idle state must not depend on packet readiness.
        turret.AttackTarget = target?.NetId;
        PublishTurretDisplay(turret, target?.NetId);
    }

    /// <summary>
    /// Batch 481 (Reconstruction): the target line (0x6D, with the 0xC8 outline) of a unit that is not a turret script's, the capture point
    /// guardian. Its shipped record names <c>LaserSight_beam.troy crystal</c>, so the client draws the beam from the crystal bone to the
    /// target the 0x6D names. Only the display is published: <c>AttackTarget</c> stays with the caller, so a unit that swings
    /// no basic attack keeps no attack state. The dedupe in <c>PublishTurretDisplay</c> sends a packet only on a change.
    /// </summary>
    public void PublishTargetLine(Entity unit, Entity? target) => PublishTurretDisplay(unit, target?.NetId);

    /// <summary>
    /// The turret-target display (0x6D/0xC8) follows the script's calls, not the id it leaves behind:
    /// <c>SetTarget</c> and <c>TurnOnAutoAttack</c> publish the target, <c>TurnOffAutoAttack</c>
    /// publishes the clear, and each goes out only when the display actually changes (an idle
    /// 0.15 s scan calls TurnOff every tick and must not resend). Nothing is shown until something
    /// is, so a turret that never took a target sends no clear for the beam it never gave out.
    /// Turret.luaobj never calls <c>SetTarget(nil)</c> — out of attack range it only stops the
    /// swing and keeps the id (classified difference 2) — so without the TurnOff clear a hero
    /// would keep the turret's targeting line after leaving its range. Reconstruction: which of
    /// retail's two target packets each stop reason retires is unrecovered; this clears both, as
    /// the visibility path already does for a shrine that leaves sight (ReplicationDelivery).
    /// </summary>
    private void PublishTurretDisplay(Entity turret, uint? targetNetId)
    {
        // Batch 529 (owner, map 8: "the laser should start targeting exactly where it starts dealing damage"). A Crystal Scar
        // shrine's acquisition range (2250) exceeds its attack range (1600). Turret.luaobj takes, and keeps, a target in that band
        // that it does not shoot. So its laser shows a target only while the swing gate would fire on it: the auto-attack is on,
        // and CanAttack holds, with the same attack reach as the shot. The combat step re-syncs it every tick (SyncShrineLaser).
        // The band's SetTarget and MOVING-stop cycle therefore sends nothing, and the laser starts and ends with the shots.
        // Summoner's Rift turrets have equal reaches and keep the script-call display.
        if (targetNetId is uint candidateId && TurretRules.IsCrystalScarShrine(turret) &&
            (!_turretAutoAttack.Contains(turret.NetId) || _world.Get(candidateId) is not { } candidate || !CanAttack(turret, candidate)))
            targetNetId = null;
        var known = _turretDisplay.TryGetValue(turret.NetId, out var shown);
        if (known ? shown == targetNetId : targetNetId is null) return;
        _turretDisplay[turret.NetId] = targetNetId;
        if (!_transport.IsWireReady(turret)) return;
        var target = targetNetId is uint id ? _world.Get(id) : null;
        if (target is not null && !_transport.IsWireReady(target)) return;
        _transport.SendTurretTarget(turret, target);
        _transport.SendTurretTargetHero(turret, target);
    }

    public Entity? AcquireTurretTarget(Entity turret) => AcquireTurretTarget(turret, candidate => CanAttackIgnoringRange(turret, candidate));

    /// <summary>
    /// The same ladder over the candidates <paramref name="eligible"/> admits. Batch 466: a capture point guardian is invulnerable (OdinGuardianBuff
    /// sets SetInvulnerable on every stats update), which <see cref="CanAttackIgnoringRange"/> reads as stasis, yet it fires (batch 466; batch 501 moved it to its tower tree).
    /// </summary>
    public Entity? AcquireTurretTarget(Entity turret, Func<Entity, bool> eligible)
    {
        ArgumentNullException.ThrowIfNull(turret);
        var candidates = _world.Entities.Values
            .Where(candidate => eligible(candidate) && InAcquisitionRange(turret, candidate) &&
                TurretSeesTarget(turret, candidate))
            .Select(candidate => (Entity: candidate,
                Distance: DistanceSquared(turret.Position, candidate.Position)))
            .ToArray();
        return candidates.Where(item => item.Entity.OwnerNetId != 0 && !item.Entity.IsWard &&
                item.Entity.UnitType is UnitType.Minion or UnitType.Monster)
            .OrderBy(item => item.Distance).Select(item => item.Entity).FirstOrDefault() ??
            candidates.Where(item => item.Entity.UnitType == UnitType.Minion)
            .OrderBy(item => item.Distance).Select(item => item.Entity).FirstOrDefault() ??
            candidates.OrderBy(item => item.Distance).Select(item => item.Entity).FirstOrDefault();
    }

    // Reconstruction: FindTargetInAcR is native and its sight test is not in the client, so a turret targets what the client's own
    // turret sight test sees, the sight test in NavigationGrid.TurretLineOfSightTest with the candidate
    // as the viewer: the turret's two columns, the walk from the candidate starting 226.8 units out, the brush rule (a candidate in
    // brush looks out of it). The range is the turret's acquisition range, not the candidate's perception bubble (null: the client's
    // 3000 without a bubble). ser_ClosenessLineOfSightThresholdTurret (200) is not a targeting rule: it is a "Server Culling" constant
    // of obj_AI_Turret that the client never reads (batch 242), and the client test passes every viewer within about 357 anyway.
    // The fountain is exempt (owner, 25 October 2026); its reach is capped by its perception bubble instead (FountainSightCap, owner,
    // 26 October 2026). Batch 244 measured the client test
    // for it: the shrine mesh is opaque past the 140-unit turret stamp, so on a 100-unit lattice of movement-walkable points within
    // 2250 an enemy hero sees the Order shrine from 122 of 254 (48%) and the Chaos shrine from 203 of 280 (72%), and batch 211's three
    // Order base positions (spawn edge, entrance, inhibitor side) all fail it. The test does not cover the base floor, so the
    // exemption stays. Batch 528 (owner request): The Crystal Scar's shrines are exempt too (TurretRules.IsShrineTurret), so they lock on
    // anywhere in their 2250 acquisition range. In the owner's game the test hid an approaching hero from the Chaos shrine from 2635 units
    // to 1431 and first passed at 1399, the tick of its laser.
    // Reconstruction: another unit that shares this ladder (a neutral script other than Leashed) has no turret test; it keeps the
    // fog's unit-vision test from its own position (batch 211).
    private bool TurretSeesTarget(Entity turret, Entity candidate)
    {
        if (turret.UnitType != UnitType.Turret)
            return SightGrid.HasUnitVision(turret.Position, turret.SelectionRadius, candidate.Position, candidate.SelectionRadius);
        if (TurretRules.IsShrineTurret(turret)) return true;
        var (selection, gameplay) = Amberline.Network.Replication.VisibilityRules.TurretBody(turret);
        return SightGrid.TurretLineOfSightTest(candidate.Position, candidate.SelectionRadius, null, turret.Position, selection,
            gameplay);
    }

    public void EngageProtection()
    {
        _protectionEngaged = true;
        RecomputeProtection();
    }

    /// <summary>
    /// The map's protection ladder, re-derived from turret state. Once the ceremony has locked both
    /// HQs (EndOfGame.txt proto 0.0 pc 0018-0045) that re-derivation would release them again, so
    /// the end-of-game path only reports the state the script left behind.
    /// </summary>
    public void RecomputeProtection()
    {
        if (_gameOver)
        {
            foreach (var structure in _world.Entities.Values.Where(unit =>
                         unit.UnitType is UnitType.Turret or UnitType.Structure).ToArray())
                _transport.SendStructureProtection(structure);
            return;
        }
        ProtectionRules.Recompute(_world, _protectionEngaged,
            structure => _transport.SendStructureProtection(structure));
    }
}
