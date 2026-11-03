using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    public bool CanAttack(Entity attacker, Entity target) => CanAttack(attacker, target, committed: false);

    // A committed swing (one planted for the next step) is admitted within
    // the ar_StopAttackRangeModifier leash instead of the strict start range.
    private bool CanAttack(Entity attacker, Entity target, bool committed)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        return CanAttackIgnoringRange(attacker, target) &&
            (committed ? InCommittedAttackRange(attacker, target) : InRange(attacker, target));
    }

    /// <summary>
    /// Reconstruction (owner decision, 18 October 2026; retail unknown, see below): automatic acquisition (a hero's idle, attack-move and hold scans and every pet's
    /// scans: Tibbers, Shaco's clone, Yorick's revenant and ghouls) never takes a ward. The shipped scripts carry no such filter (each
    /// calls the native FindTargetInAcR with no unit-type test), so the rule is the owner's; an explicit attack order on a ward is not
    /// an acquisition and is not subject to it. Rally's beacon rides the ward flag but is a unit with real health, so it stays
    /// acquirable. The one rule every acquiring system asks, instead of a copy per system.
    /// </summary>
    public static bool IsAutoAcquirable(Entity target) => !target.IsWard || target.IsSummonerBeacon;

    /// <summary>
    /// Everything <see cref="CanAttack"/> requires except the range: the engine's "target lost"
    /// test (the one <c>OnTargetLost</c> is raised for) for a turret.
    /// </summary>
    public bool CanAttackIgnoringRange(Entity attacker, Entity target) =>
        attacker.Alive && target.Alive && !IsStasis(attacker) &&
        // Batch 227 (owner request, 25 October 2026; patch V1.0.0.108: "Shrine (spawn) turrets now ignore all resistances and immunities"):
        // the fountain's swing is the shipped OrderTurretShrineBasicAttack's DAMAGESOURCE_INTERNALRAW hit, which ignores invulnerability, so
        // an invulnerable champion (Kayle's Intervention) is a legal target for it. Untargetable units stay refused by IsTargetable below.
        (TurretRules.IsShrineTurret(attacker) || !_hooks.IsInvulnerable(target)) && _hooks.IsTargetable(target, attacker) &&
        WorldState.IsEnemy(attacker, target) && !target.Protected &&
        (attacker.UnitType != UnitType.Turret ||
            !target.IsWard || target.IsSummonerBeacon);

    // Provisional reconstruction: Map1 ar_StopAttackRangeModifier permits
    // 100 extra units beyond the shared start reach while an
    // admitted attack winds up. Large escapes and explicit Move/Hold/Stop
    // can still interrupt it; launched attacks have their own lifetime.
    public bool InCommittedAttackRange(Entity attacker, Entity target)
    {
        var reach = AttackReach(attacker, target) + 100.0;
        return DistanceSquared(attacker.Position, target.Position) <= reach * reach;
    }

    // A swing planted for the next combat step counts as committed, so
    // pursuit neither re-routes nor cancels it in between.
    public bool HasCommittedWindup(Entity attacker, Entity target) =>
        (_plantedHeroSwings.TryGetValue(attacker.NetId, out var planted) &&
         planted == target.NetId && InCommittedAttackRange(attacker, target)) ||
        _pendingHits.Any(hit => hit.SourceNetId == attacker.NetId &&
            hit.TargetNetId == target.NetId && hit.WindupRemaining > 0.0 &&
            hit.SourceKind == DamageSourceKind.Basic && hit.MissileNetId != 0 &&
            InCommittedAttackRange(attacker, target));

    /// <summary>
    /// A basic attack of <paramref name="attacker"/>'s is wound up on <paramref name="target"/> and
    /// has not landed yet — regardless of missile or reach, unlike
    /// <see cref="HasCommittedWindup"/>, which is the pursuit rule.
    /// </summary>
    public bool IsWindingUpOn(Entity attacker, Entity target) =>
        _pendingHits.Any(hit => hit.SourceNetId == attacker.NetId &&
            hit.TargetNetId == target.NetId && hit.SourceKind == DamageSourceKind.Basic &&
            hit.WindupRemaining > 0.0);

    // Reconstruction: the native LastAutoAttackFinished binding is unrecovered.
    // The attack finishes at release; its launched missile and cooldown continue independently.
    public bool LastAutoAttackFinished(Entity attacker) =>
        !_plantedHeroSwings.ContainsKey(attacker.NetId) &&
        !_pendingHits.Any(hit => hit.SourceNetId == attacker.NetId &&
            hit.SourceKind == DamageSourceKind.Basic && hit.WindupRemaining > 0.0);

    /// <summary>Returns the live targetability gate used by scripted pets.</summary>
    public bool IsTargetable(Entity target, Entity? observer = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Alive && !target.Protected && !IsInvulnerable(target) &&
            _hooks.IsTargetable(target, observer);
    }

    // Owner decision 27 Sept 2026: edge-to-edge gameplay radii, no start slack.
    // Turret.lua delegates to unrecovered native gates; its indicator/data do
    // not establish edge geometry. Preserve turret-as-attacker admission.
    public static double AttackReach(Entity attacker, Entity target) =>
        attacker.UnitType == UnitType.Turret
            ? FountainSightCap(attacker, attacker.AttackRange + target.Radius +
                (target.UnitType is UnitType.Turret or UnitType.Structure ? attacker.Radius : 3.0))
            : attacker.AttackRange + attacker.CombatRadius + target.CombatRadius;

    // Reconstruction (owner decision, 26 October 2026: "the laser range limited by the perception bubble radius"): the fountain shrine
    // reaches only what its own perception bubble covers, centre to centre: the shrine character's PerceptionBubbleRadius (absent from
    // both shrine inibins, so the CharacterData default 1350, VisibilityRules.TerrainVisionRange). Its shipped AttackRange and
    // AcquisitionRange (2250) stay. This reach feeds acquisition (FindTargetInAcR), retention (TargetInAttackRange), the swing and
    // the call for help, so the laser never shows past the bubble. FindTargetInAcR and TargetInAttackRange are native and unrecovered,
    // and no dated source lowers the 2250 before 1.0.0.126 (four research passes, batch 354).
    private static double FountainSightCap(Entity turret, double reach) =>
        TurretRules.IsFountainTurret(turret)
            ? Math.Min(reach, Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(turret))
            : reach;

    public static bool InRange(Entity attacker, Entity target)
    {
        var reach = AttackReach(attacker, target);
        return DistanceSquared(attacker.Position, target.Position) <= reach * reach;
    }

    // Turret.luaobj asks FindTargetInAcR (the acquisition range) for a new target and TargetInAttackRange (the attack
    // reach) for the current one, so acquisition uses AcquisitionRange with the same radii rule as AttackReach.
    public static bool InAcquisitionRange(Entity attacker, Entity target)
    {
        var reach = AttackReach(attacker, target) - attacker.AttackRange + attacker.AcquisitionRange;
        return DistanceSquared(attacker.Position, target.Position) <= reach * reach;
    }

    // The CanAttack status bit (stun, suppression, sleep, fear, disarm, pacify, CantAttack): see CharacterState.
    private static bool AttackDisabled(Entity entity) => !CharacterState.Of(entity).CanAttack;

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private void UpdateAggroOutline(Entity unit)
    {
        // Reconstruction: the client's CollectAggroUnits has no unit-type filter, so any AI unit that targets
        // the local hero gets the yellow outline from 0xC8. Pet.lua / YorickPHPet.lua have no lane-minion
        // state; a pet is engaged while it holds an attack target, like a minion in Engage. Batch 664: a scripted minion
        // (BBSpawnMinion with Minion.lua or Aggro.lua, MinionAiSystem.IsMinionUnit) is engaged as a lane minion is: the Basic
        // Tutorial's minions sent none (owner's hand test, 3 November 2026).
        var engaged = (Minions.MinionAiSystem.IsMinionUnit(unit) &&
                       unit.MinionState is MinionAiState.Engage or MinionAiState.Taunted) ||
            (unit.Kind == EntityKind.Neutral && unit.NeutralState == "engage") ||
            (unit.Kind.IsAttackingPet() && unit.AttackTarget is not null);
        if (!engaged && !_lastAggroTarget.ContainsKey(unit.NetId)) return;
        var targetId = engaged && unit.AttackTarget is uint id ? id : 0U;
        if (_lastAggroTarget.TryGetValue(unit.NetId, out var previous) && previous == targetId)
            return;
        if (!_transport.IsWireReady(unit)) return;
        _transport.SendAggroOutline(unit, targetId);
        _lastAggroTarget[unit.NetId] = targetId;
    }

    // An invulnerable unit does not attack unless the invulnerability is a script's own protection (Kayle's Intervention), which
    // restricts nothing else: every other invulnerability here is a stasis (Zhonya's Hourglass, Guardian Angel, Yorick's revival).
    private static bool IsStasis(Entity unit) => unit.AnyBuffOrStatus(buff =>
        (buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable) && !buff.Payload.ActionsAllowed);

    private bool IsInvulnerable(Entity unit) => _hooks.IsInvulnerable(unit) ||
        unit.AnyBuffOrStatus(buff => buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable);
}
