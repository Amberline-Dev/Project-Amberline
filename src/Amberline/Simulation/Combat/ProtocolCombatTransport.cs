using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

/// <summary>
/// Production packet adapter for <see cref="CombatService"/>. Combat owns
/// ordering and state; this class owns the verified protocol builders and
/// queues messages through the per-match replication delivery.
/// </summary>
public sealed class ProtocolCombatTransport : ICombatTransport
{
    private const uint SFlagNontargetableAll = 0x0180_0000u;

    private readonly ICombatPacketQueue _delivery;
    private readonly HeroStatsService _heroStats;
    private readonly Func<Entity, bool> _wireReady;
    private readonly ReplicationSyncCounter _syncIds;

    public ProtocolCombatTransport(
        ICombatPacketQueue delivery,
        HeroStatsService heroStats,
        Func<Entity, bool> wireReady,
        ReplicationSyncCounter syncIds)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _heroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        _wireReady = wireReady ?? throw new ArgumentNullException(nameof(wireReady));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
    }

    /// <summary>Batch 460: the ground height under a point (the match's navigation grid), the Y of the respawn packet; 0 without a grid.</summary>
    public Func<Position2, double> GroundHeight { get; init; } = static _ => 0.0;

    public bool IsWireReady(Entity unit) => _wireReady(unit);

    public void SendBasicAttack(Entity source, Entity target, uint missileNetId, double extraTimeSeconds,
        BasicAttackOutcome outcome, int animationIndex)
        => Enqueue(CombatPackets.BasicAttack(source.NetId, target.NetId, missileNetId,
            slot: outcome switch {
                BasicAttackOutcome.Critical => 0x44,
                BasicAttackOutcome.Miss => 0x45,
                BasicAttackOutcome.Dodge => 0x46,
                _ => animationIndex == 0 ? (byte)0 : checked((byte)(0x40 + animationIndex)),
            }, extraTimeSeconds: extraTimeSeconds));

    public void SendTurretTarget(Entity turret, Entity? target)
        => Enqueue(CombatPackets.AiSetTarget(turret.NetId, target?.NetId ?? 0));

    public void SendTurretTargetHero(Entity turret, Entity? target)
        => Enqueue(CombatPackets.AiSetTargetHero(turret.NetId, target?.NetId ?? 0));

    public void SendAggroOutline(Entity unit, uint targetNetId)
        // A target-zero transition is cleanup for a known actor. Route it
        // through the fog-safe lifecycle path so a client cannot retain the
        // previous aggro outline after the neutral leaves sight.
        => Enqueue(CombatPackets.AiSetTargetHero(unit.NetId, targetNetId),
            global: targetNetId == 0);

    public void RevealShrine(Entity shrine, Entity target)
    {
        Enqueue(VisionPackets.EnterVisibilityClient(shrine.NetId, shrine.Name,
            maxHealth: Float(shrine.MaxHealth), health: Float(shrine.Health),
            position: shrine.Position, facing: shrine.Facing));
        Enqueue(VisionPackets.EnterTeamVisibility(shrine.NetId, (byte)target.Team));
        SendStructureProtection(shrine);
    }

    public void SendApplyDamage(uint sourceNetId, Entity? source, Entity target,
        double damage, DamageType type, byte typeByte)
        => Enqueue(CombatPackets.ApplyDamage(sourceNetId, target.NetId, Float(damage), typeByte));

    public void SendLineMissileHit(Entity attacker, uint attackTargetNetId, uint missileNetId, uint hitNetId)
    {
        if (missileNetId == 0 || hitNetId == 0) return;
        if (_delivery is IReplicationIntentQueue typed)
            typed.EnqueueLineMissileHitList(new LineMissileHitListReplicationMessage(missileNetId, [hitNetId], attacker.NetId, attackTargetNetId));
        else Enqueue(SpellPackets.LineMissileHitList(missileNetId, [hitNetId]));
    }

    public void SendHealth(Entity target, double health)
    {
        Enqueue(CombatPackets.ReplicationHp(target.NetId, Float(health),
            syncId: _syncIds.Next(target.NetId),
            isHero: target.UnitType == UnitType.Hero,
            isTurret: target.IsSceneTurret));
    }

    public void SendTurretStats(Entity turret)
    {
        // Batch 457: a turret the clients hold as a minion (Entity.ReplicatedAsMinion) has no turret block; a buff that changes its stats sends
        // its minion block (BuffEffectService, SendStats), and its health goes in SendHealth.
        if (turret.ReplicatedAsMinion) return;
        Enqueue(CombatPackets.TurretStats(turret.NetId,
            new TurretStatsSnapshot(Float(turret.Health), Float(turret.AttackDamage),
                Float(turret.MaxHealth), Float(turret.Armor), Float(turret.SpellBlock),
                Float(turret.HealthRegen), turret.Protected, Float(turret.AttackSpeedMod),
                Targetable: turret.Alive && !TurretRules.IsFountainTurret(turret)),
            syncId: _syncIds.Next(turret.NetId)));
    }

    public void SendHeroStats(Entity hero)
    {
        hero.MarkReplicatedGold();
        var tooltipEntries = _heroStats.BuildItemTooltipEntries(hero)
            .Select(entry => new SpellTooltipEntry(
                checked((byte)entry.Slot), Float(entry.First),
                Float(entry.Second), Float(entry.Third)))
            .ToArray();
        if (tooltipEntries.Length > 0)
            Enqueue(SpellPackets.SpellTooltipVariables(tooltipEntries, hero.NetId));
        Enqueue(CombatPackets.HeroStats(_heroStats.Snapshot(hero),
            syncId: _syncIds.Next(hero.NetId)));
    }

    public void SendDeath(Entity target, uint killerNetId, double durationSeconds, bool becomeZombie = false)
    {
        var opcode = target.UnitType == UnitType.Hero ? (byte)0x61 : (byte)0xA6;
        EnqueueLifecycle(target.NetId, killerNetId,
            resolvedKiller => CombatPackets.NpcDie(target.NetId, resolvedKiller,
                Float(durationSeconds), becomeZombie: (byte)(becomeZombie ? 1 : 0), opcode: opcode));
    }

    public void SendTurretDestroyed(Entity turret, uint killerNetId)
    {
        // The client's turret death handler emits event 29 from this death.
        EnqueueLifecycle(turret.NetId, killerNetId,
            resolvedKiller => CombatPackets.NpcDie(turret.NetId, resolvedKiller,
                Float(CombatService.CorpseReapSeconds)));
    }

    public void SendStructureDestroyed(Entity structure, uint killerNetId)
    {
        EnqueueLifecycle(structure.NetId, killerNetId,
            resolvedKiller => CombatPackets.BuildingDie(structure.NetId, resolvedKiller));
        Enqueue(CombatPackets.BuildingStats(structure.NetId, 0.0f, SFlagNontargetableAll,
            syncId: _syncIds.Next(structure.NetId),
            invulnerable: 1u, targetable: 0u));
    }

    public void SendInputLockingFlag(uint flag, bool locked)
        => Enqueue(CombatPackets.SetInputLockingFlag(flag, locked), global: true);

    public void SendDisableHudForEndOfGame()
        => Enqueue(CombatPackets.DisableHudForEndOfGame(), global: true);

    public void SendMoveCameraToPoint(Entity hero, Position3 point, double travelSeconds)
        => Enqueue(CombatPackets.MoveCameraToPoint(hero.NetId, point, Float(travelSeconds)), global: true);

    public void SendGreyscaleEnabledWhenDead(Entity hero, bool enabled)
        => Enqueue(CombatPackets.SetGreyscaleEnabledWhenDead(hero.NetId, enabled), global: true);

    public void SendMainSfxFade(double seconds)
        => Enqueue(CombatPackets.FadeOutMainSfx(Float(seconds)), global: true);

    public void SendNexusSkin(Team team, uint skinId)
        => Enqueue(CombatPackets.SetNexusSkin(team, skinId), global: true);

    public void SendMinionFade(Team team, double amount, double seconds)
        => Enqueue(CombatPackets.FadeMinions(team, Float(amount), Float(seconds)), global: true);

    public void SendEndOfGameEvent(Team winner)
        => Enqueue(CombatPackets.EndOfGameEvent(winner == Team.Order), global: true);

    public void SendInhibitorState(Entity inhibitor, bool dead, double respawnSeconds, uint killerNetId = 0)
    {
        if (dead)
        {
            Enqueue(CombatPackets.DampenerState(inhibitor.NetId, Float(respawnSeconds), dead: true), global: true);
            // The building death handler already invokes the dampener's death handler
            // which emits event 25.
        }
        else
        {
            // The stock respawn step emits respawn event 27 from the alive state below.
            Enqueue(CombatPackets.BuildingStats(inhibitor.NetId, Float(inhibitor.Health),
                CombatPackets.SFlagTargetableToAll, syncId: _syncIds.Next(inhibitor.NetId)));
            Enqueue(CombatPackets.DampenerState(inhibitor.NetId, 0.0f, dead: false), global: true);
        }
    }

    public void SendGold(Entity killed, Entity hero, double gold)
        => Enqueue(CombatPackets.UnitAddGold(killed.NetId, hero.NetId, Float(gold)));

    public void SendLevelUp(Entity hero)
        => Enqueue(CombatPackets.NpcLevelUp(hero.NetId, checked((byte)hero.Level),
            checked((byte)hero.SkillPoints)));

    public void SendHeroGold(Entity hero)
        => Enqueue(CombatPackets.HeroGold(hero.NetId, Float(hero.Gold), syncId: _syncIds.Next(hero.NetId)));

    public void SendHeroGoldExp(Entity hero)
    {
        hero.MarkReplicatedGold();
        Enqueue(CombatPackets.HeroGoldExp(hero.NetId, Float(hero.Gold), Float(hero.Experience),
            syncId: _syncIds.Next(hero.NetId)));
    }

    public void SendHeroNeutralKills(Entity hero)
        => Enqueue(CombatPackets.HeroNeutralKills(hero.NetId, hero.NeutralMinionsKilled,
            _syncIds.Next(hero.NetId)), global: true);

    public void SendHeroMultiKill(Entity hero, Entity victim, int count)
        => Enqueue(CombatPackets.HeroMultiKill(hero.NetId, victim.NetId, count), global: true);

    public void SendRespawn(Entity hero)
        => EnqueueLifecycle(hero.NetId, 0,
            _ => CombatPackets.HeroReincarnate(hero.NetId,
                new Position3(hero.Position.X, GroundHeight(hero.Position), hero.Position.Z)));

    public void SendMinionNoRender(Entity minion)
    {
        ArgumentNullException.ThrowIfNull(minion);
        var actionState = minion.Kind is EntityKind.ShacoBox or EntityKind.NidaleeTrap ? 0u : 7u;
        if (minion.AnyBuffOrStatus(buff => buff.Payload.Ghosted)) actionState |= 1u << 10;
        if (minion.AnyBuffOrStatus(buff => buff.Payload.GhostProof)) actionState |= 1u << 11;
        Enqueue(CombatPackets.MinionActionState(minion.NetId,
            actionState | (1u << 13), _syncIds.Next(minion.NetId)));
    }

    /// <summary>Batch 447: 0xDD, a team's whole score, to every client (the shipped SetGameScore node; DominionPackets.SetGameScore).</summary>
    public void SendGameScore(Team team, int score)
        => Enqueue(DominionPackets.SetGameScore(team, score), global: true);

    public void SendEndGame(Team winner, bool surrendered)
        // The chat lock is ScoreboardPhase's own SetInputLockingFlag(INPUT_CHAT, true), one call
        // before this (EndOfGame.txt proto 0.5 pc 0000-0003).
        => Enqueue(CombatPackets.EndGame(winner == Team.Order, surrendered), global: true);

    // Structure protection is sent global (SendStructureProtection), so every connected seat gets it; every seat knows the
    // unit from the map burst.
    public void SendStructureProtection(Entity structure)
    {
        // Batch 457: a turret the clients hold as a minion is no map structure (not in the map burst, no turret block); no map rule protects it.
        if (structure.ReplicatedAsMinion) return;
        Enqueue(StructureProtectionPacket(structure, _syncIds.Next(structure.NetId)), global: true);
    }

    /// <summary>The replicated protection/targetability state of one structure, as the client's store holds it.</summary>
    internal static PlaintextPacket StructureProtectionPacket(Entity structure, uint syncId)
    {
        if (structure.UnitType == UnitType.Turret)
        {
            var targetable = structure.Alive && !TurretRules.IsFountainTurret(structure);
            return CombatPackets.TurretProtectionFlags(structure.NetId,
                !targetable ? CombatPackets.SFlagNontargetableAll :
                    structure.Protected ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll,
                syncId: syncId,
                invulnerable: structure.Protected ? 1u : 0u,
                targetable: targetable ? 1u : 0u);
        }
        return CombatPackets.BuildingStats(structure.NetId, Float(structure.Health),
            structure.Protected ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll,
            syncId: syncId,
            invulnerable: structure.Protected ? 1u : 0u,
            targetable: 1u);
    }

    private void Enqueue(PlaintextPacket packet, bool global = false)
        => _delivery.Enqueue(packet, global);

    /// <summary>Batch 448: a Dominion capture packet (0xDB, 0xDC), sent to every client on the combat channel.</summary>
    public void SendDominion(PlaintextPacket packet) => Enqueue(packet, global: true);

    private void EnqueueLifecycle(uint victimNetId, uint killerNetId,
        Func<uint, PlaintextPacket> builder)
        => _delivery.EnqueueLifecycle(victimNetId, killerNetId, builder);

    private static float Float(double value)
        => ProtocolEncoding.Float32(value, nameof(value));
}
