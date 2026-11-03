using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Spells;

/// <summary>
/// Protocol adapter for the shared spell/effect boundary. Spell code owns
/// ordering and state; this adapter only builds the verified packets and puts
/// them on the match queue. The callbacks are required so a production match
/// cannot silently drop stat or allocation work.
/// </summary>
public sealed class ProtocolSpellEffectSink : ISpellEffectSink
{
    private readonly ICombatPacketQueue _queue;
    private readonly ReplicationSyncCounter _syncIds;
    private readonly NavigationBounds _bounds;
    private readonly Func<double, double, double> _height;
    private readonly Func<UnitType, uint> _allocate;
    private readonly Action<Entity> _sendStats;
    private readonly Action<Entity> _sendCombatState;
    private readonly Action<OriannaBallReplication, bool>? _updateBall;
    private readonly Action<uint>? _removeBall;
    private readonly Func<uint, bool>? _isBallNoRender;
    private readonly GameData _data;
    private Dictionary<uint, string>? _spellNamesByHash;

    public ProtocolSpellEffectSink(
        ICombatPacketQueue queue,
        ReplicationSyncCounter syncIds,
        NavigationBounds bounds,
        Func<double, double, double> height,
        Func<UnitType, uint> allocate,
        Action<Entity> sendStats,
        Action<OriannaBallReplication, bool>? updateBall = null,
        Action<uint>? removeBall = null, GameData? data = null,
        Func<uint, bool>? isBallNoRender = null,
        Action<Entity>? sendCombatState = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
        _bounds = bounds;
        _height = height ?? throw new ArgumentNullException(nameof(height));
        _allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));
        _sendStats = sendStats ?? throw new ArgumentNullException(nameof(sendStats));
        _sendCombatState = sendCombatState ?? _sendStats;
        _updateBall = updateBall;
        _removeBall = removeBall;
        _isBallNoRender = isBallNoRender;
        _data = data ?? GameData.Default;
    }

    public bool IsReferenceReady(Entity unit) => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created;

    public void SendCast(Entity caster, SpellCastInfo info, long casterPositionSyncId = 0, bool missileOnlyForCaster = false)
    {
        // A cast replaces the client's current SpellCaster and force-stops the attack's, which plays the cast frame of a swing
        // the server dropped. Tell the client first. Whatever swing is left is no longer the current SpellCaster, so a later drop
        // of it sends nothing and no stop can find the spell's own missile.
        if (caster.ClientSwingDropped) SendStopAttack(caster);
        caster.ClientSwingMissileNetId = 0;
        // Batch 400 (owner finding, 27 October 2026: Fiddlesticks cannot move while Dark Wind's crows bounce). A missile-only child (batch 393)
        // reaches its caster's own client as the 0x41 alone, which builds no SpellCaster (the 0x41 handler runs
        // the cast routine, and that builds none). Only a SpellCaster's Stop asks IsCancelBlocked
        // (the caster's stop routine), so that send opens no windup and no channel on the server
        // either. Reconstruction (batch 400): follows the batch 393 choice that the caster's client gets the missile alone.
        if (!missileOnlyForCaster) NoteCastWindup(caster, info);
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueCast(new SpellCastReplicationMessage(
                caster.NetId, info, checked((int)casterPositionSyncId), missileOnlyForCaster));
            return;
        }
        Enqueue(SpellPackets.CastSpellAnswer(caster.NetId, info,
            checked((int)casterPositionSyncId)));
    }

    /// <summary>
    /// Batch 582 (owner finding 25, "Gragas R has two barrels, only sometimes"): an Instacast child that launches a missile reaches every view as
    /// its missile's creation 0x41 alone, never as a cast answer too. Evidence: the client's cast routine hands
    /// a cast answer straight to the cast routine (which builds the missile) when the spellbook's current
    /// SpellCaster is not an attack (the caster's isAutoAttack byte), the new cast is neither an
    /// attack nor channelled (the cast info's flags) and its slot is an extra slot above 41 other than 51. Only with no such caster does the answer
    /// go to the instant-cast routine, which builds no missile. The 0x41 handler always builds one, and DoCast's check for a missile id
    /// that already exists runs only when the facade's flag is set, the value <c>SetSpectatorMode</c> takes, so a player client keeps both.
    /// Gragas's <c>GragasExplosiveCaskBoom</c> (Instacast, homing) is cast at R's cast frame, while R's SpellCaster may still be current on the client:
    /// two barrels whenever the answer arrives before that caster ends. Reconstruction: what retail's server sent for these children is not in the
    /// client; the missile packet alone builds exactly one object in both client states. The child's own cast sound (sound event 8 in
    /// the instant-cast routine) no longer plays when no caster is current; DoCast plays none in either path.
    /// </summary>
    public void SendInstantMissileCast(Entity caster, SpellCastInfo info)
    {
        // No cast answer replaces the client's current SpellCaster (the 0x41 builds none), so a dropped swing needs no stop here (SendCast).
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueCast(new SpellCastReplicationMessage(caster.NetId, info, MissileOnly: true));
            return;
        }
        // Plain replay has no per-view tracking: the creation 0x41 that follows is sent as it is.
    }

    /// <summary>
    /// The cast packet starts the client's <c>SpellCaster</c>, whose cast frame comes <c>DesignerCastTime + ExtraCastTime</c> after it
    /// arrives; until then a spell with <c>CantCancelWhileWindingUp</c> refuses a plain Stop, and from that cast frame to its
    /// mTimeSpellEnd a spell with <c>CantCancelWhileChanneling</c> does (<see cref="SpellWindup"/>). Every spell path ends in this
    /// method, so the hero's order gate is read off the wire and not off each module's own state.
    /// </summary>
    private void NoteCastWindup(Entity caster, SpellCastInfo info)
    {
        if (caster.UnitType != UnitType.Hero) return;
        var castFrame = (double)info.DesignerCastTime + info.ExtraCastTime;
        if (!(castFrame > 0.0)) return;
        _spellNamesByHash ??= SpellNamesByHash();
        if (!_spellNamesByHash.TryGetValue(info.SpellHash, out var name)) return;
        if (_data.SpellHasUncancellableWindup(name)) SpellWindup.Begin(caster, castFrame);
        // The channel opens at the cast frame and closes at the packet's mTimeSpellEnd, ExtraCastTime + DesignerTotalTime (batch 246).
        // - The cast frame opens it only when the cast routine returned 1, the spell's
        //   ChannelDuration (the channel duration) is above zero and the caster's auto-attack byte is 0. That byte is the cast info's isAutoAttack (copied
        //   8 bytes up; bit 0 of the packet's flags), so an auto-attack cast never opens the channel refusal (batch 220 read the
        //   function without that test). No shipped swing has a ChannelDuration (GoldCardPreAttack's is 0): only a BuildingBlocks child attack
        //   that carries one changes.
        // - The caster's update sets the channel flag again and stops the caster once the clock passes its end time,
        //   the mTimeSpellEnd InitSpell stamped from the packet. Every channel sender's total is cast + ChannelDuration
        //   (BbChampionModule.Channelled, SummonersRecall), so the old floor of the channel length (a port guard) is gone; a sender with a
        //   short total is fixed at the sender, not here.
        // A script with no ChannelDuration (Nidalee's NidaleeTakedownAttack carries CantCancelWhileChanneling but no global) never channels,
        // so its total time is no hold (batch 178).
        var channel = _data.SpellChannelDuration(name);
        if (channel > 0.0 && (info.Flags & 0x01) == 0 && _data.SpellHasUncancellableChannel(name))
            SpellWindup.BeginChannel(caster, castFrame, info.ExtraCastTime + info.DesignerTotalTime,
                _data.Spell(name).RawFlag("SpellData*CannotBeSuppressed"));
    }

    private Dictionary<uint, string> SpellNamesByHash()
    {
        var names = new Dictionary<uint, string>();
        foreach (var name in _data.SpellInibinNames) names.TryAdd(SpellSystem.ProtocolHash(name), name);
        return names;
    }

    public void SendMissile(Entity caster, SpellCastInfo info, Position3 position,
        Position3 direction, Position3 velocity, Position3 startPoint, Position3 endPoint,
        Position3? unitPosition = null, Position3? casterPosition = null, bool needsCastAnnouncement = false)
    {
        var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z);
        var wireSpeed = Float(speed);
        var wireCasterPosition = casterPosition ?? position;
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueMissile(new MissileReplicationMessage(
                caster.NetId, position, wireCasterPosition, direction, velocity,
                startPoint, endPoint, unitPosition ?? startPoint,
                wireSpeed, 0.0f, 0, info, NeedsCastAnnouncement: needsCastAnnouncement));
            return;
        }
        Enqueue(SpellPackets.MissileReplication(caster.NetId, position, wireCasterPosition,
            direction, velocity, startPoint, endPoint, unitPosition ?? startPoint,
            wireSpeed, 0.0f, 0, info));
    }

    public void RefreshMissile(Entity caster, SpellCastInfo info, Position3 position,
        Position3 direction, Position3 velocity, Position3 startPoint, Position3 endPoint,
        Position3 unitPosition, Position3 casterPosition, float speed, float lifePercentage)
    {
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueMissile(new MissileReplicationMessage(
                caster.NetId, position, casterPosition, direction, velocity,
                startPoint, endPoint, unitPosition, speed, lifePercentage, 0, info,
                RefreshOnly: true));
        }
    }

    public void SendChainMissileSync(Entity caster, uint missileNetId, IReadOnlyList<uint> targets, bool attack = false)
    {
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueChainMissileSync(new ChainMissileSyncReplicationMessage(missileNetId, caster.NetId, targets.ToArray(), attack));
        else Enqueue(SpellPackets.ChainMissileSync(missileNetId, caster.NetId, targets));
    }

    public void SendLineMissileHitList(uint missileNetId, IReadOnlyList<uint> targets)
    {
        if (missileNetId == 0 || targets.Count == 0) return;
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueLineMissileHitList(new LineMissileHitListReplicationMessage(missileNetId, targets.ToArray()));
        else Enqueue(SpellPackets.LineMissileHitList(missileNetId, targets));
    }

    public void RetireCastMissile(uint missileNetId)
    {
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueEffectDestroy(new EffectDestroyReplicationMessage(
                SpellPackets.DestroyMissile(missileNetId), missileNetId, LateMissileOnly: true));
    }

    public void SendDestroyMissile(uint missileNetId)
    {
        var packet = SpellPackets.DestroyMissile(missileNetId);
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueEffectDestroy(new EffectDestroyReplicationMessage(packet, missileNetId));
            return;
        }
        Enqueue(packet);
    }

    /// <summary>
    /// Every path and stop a unit is handed goes out as one <c>WaypointList</c>, pets, boxes and heroes alike. Retail re-sends
    /// the path of an AI unit whenever its script or native state asks for one: the shipped <c>Pet.luaobj</c> calls the state natives
    /// on each order (<c>OnOrder</c>) and on each acquisition or state change (<c>TimerFindEnemies</c>, <c>TimerScanDistance</c>),
    /// and each ask is one packet. A repeated stop is therefore sent, not deduplicated. Owner decision,
    /// 25 October 2026: retail even where it measures worse; batch 229 records the match numbers.
    /// </summary>
    public void SendWaypoints(Entity unit, IReadOnlyList<Position2> points, long syncId) =>
        Enqueue(VisionPackets.WaypointList(unit.NetId, points, checked((uint)syncId)));

    // Whether a unit gets a stop-attack (0x39) and with which flags is CombatPackets.StopAttack's decision, the same one the minion
    // AI, the neutral wire and the session detach use: a swing the server dropped is not played, a hit that stands keeps its shot.
    public void SendStopAttack(Entity unit)
    {
        if (CombatPackets.StopAttack(unit) is { } packet) Enqueue(packet);
    }

    public void SendForcedStop(Entity unit)
    {
        if (CombatPackets.StopAttack(unit, forceStop: true) is { } packet) Enqueue(packet);
    }

    public void SendFinishCast(Entity unit) => Enqueue(SpellPackets.FinishCast(unit.NetId));

    public void SendCooldown(Entity unit, int slot, double seconds, bool summoner = false) =>
        Enqueue(SpellPackets.SetCooldown(unit.NetId, checked((byte)slot), Float(seconds), summoner));

    // Temporary life can expire in the same step as ordinary respawn. Both
    // must retain lifecycle order when delivery prioritizes lifecycle packets.
    public void SendForceDead(Entity unit) =>
        _queue.EnqueueLifecycle(unit.NetId, 0, _ => CombatPackets.ForceDead(unit.NetId));

    public void SendShowHealthBar(Entity unit, bool show) =>
        Enqueue(CombatPackets.ShowHealthBar(unit.NetId, show));

    public void SendUpgrade(Entity unit, int slot, int rank, int pointsLeft) =>
        Enqueue(SpellPackets.UpgradeSpellAnswer(unit.NetId, checked((byte)slot),
            checked((byte)rank), checked((byte)pointsLeft)));

    public void SendFaceDirection(Entity unit, Position3 direction) =>
        Enqueue(SpellPackets.FaceDirection(unit.NetId, direction));

    public void PlayAnimation(Entity unit, string name, bool loop = false, bool lockAnimation = false,
        bool blend = true, float duration = 0)
    {
        unit.AnimationSerial++;
        // A play the client refuses while locked leaves the lock as it was; an accepted one with the lock bit sets it.
        unit.AnimationLocked |= lockAnimation;
        Enqueue(SpellPackets.PlayAnimation(unit.NetId, name, loop, lockAnimation, blend, duration));
    }

    public void PlayEmote(Entity unit, ChampionEmote emote) =>
        Enqueue(EmotePackets.Play(unit.NetId, emote));

    public void StopAnimation(Entity unit, bool blend = true)
    {
        unit.AnimationSerial++;
        unit.AnimationLocked = false;
        Enqueue(SpellPackets.StopAnimation(unit.NetId, blend));
    }

    public void SetAnimationStates(Entity unit, IReadOnlyList<(string Original, string Replacement)> states) =>
        Enqueue(SpellPackets.SetAnimationStates(unit.NetId, states));

    public void SetAutoAttackSpellSlot(Entity unit, int slot) =>
        Enqueue(SpellPackets.SetAutoAttackSpellSlot(unit.NetId, slot));

    public void SendDash(Entity unit, Position2 start, Position2 destination,
        double speed, double gravity, long syncId, uint followNetId = 0,
        double followDistance = 0.0, double followBackDistance = 0.0)
    {
        if (unit.ForcedMove is { Completed: false } move &&
            (move.Destination == destination ||
             move.FirstCollision && start == unit.Position && speed == move.Speed))
        {
            move.DashPresentation = true;
            move.DashGravity = gravity;
            move.DashFollowDistance = followDistance;
            move.DashFollowBackDistance = followBackDistance;
            move.AnnouncedEnd = followNetId == 0 ? destination : null;
        }
        Enqueue(VisionPackets.Dash(unit.NetId, start, destination, checked((float)speed),
            _bounds, checked((uint)syncId), followNetId, checked((float)followDistance),
            checked((float)gravity), checked((float)followBackDistance)));
    }

    public void SendPushCharacterData(Entity unit, uint changeId, string skinName, bool useSpells = false) =>
        SendCharacterData(new(unit.NetId, changeId, skinName, useSpells));

    public void SendPopCharacterData(Entity unit, uint changeId) =>
        SendCharacterData(new(unit.NetId, changeId, null));

    public void SendPopAllCharacterData(Entity unit) =>
        SendCharacterData(CharacterDataReplicationMessage.PopAll(unit.NetId));

    private void SendCharacterData(CharacterDataReplicationMessage message)
    {
        if (_queue is IReplicationIntentQueue typed) typed.EnqueueCharacterData(message);
        else Enqueue(message.Packet);
    }

    public void SendProjectedMissile(MissileCasterProjection projection, SpellCastInfo info,
        Position3 position, Position3 direction, Position3 velocity,
        Position3 startPoint, Position3 endPoint, Position3 unitPosition,
        Position3 casterPosition)
    {
        var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z);
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueMissile(new MissileReplicationMessage(projection.NetId,
                position, casterPosition, direction, velocity, startPoint, endPoint,
                unitPosition, Float(speed), 0.0f, 0, info,
                CasterProjection: projection));
        else
        {
            // Direct capture sinks have no observer views. Retain the native
            // creation-before-reference order rather than emitting a helper
            // ID that the capture's client could not resolve.
            foreach (var packet in TemporaryCasterPackets.Create(projection, info, _syncIds.Next))
                Enqueue(packet);
            Enqueue(SpellPackets.MissileReplication(projection.NetId,
                position, casterPosition, direction, velocity, startPoint, endPoint,
                unitPosition, Float(speed), 0.0f, 0, info));
        }
    }

    public void RefreshProjectedMissile(MissileCasterProjection projection, SpellCastInfo info,
        Position3 position, Position3 direction, Position3 velocity,
        Position3 startPoint, Position3 endPoint, Position3 unitPosition,
        Position3 casterPosition, float speed, float lifePercentage)
    {
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueMissile(new MissileReplicationMessage(projection.NetId,
                position, casterPosition, direction, velocity, startPoint, endPoint,
                unitPosition, speed, lifePercentage, 0, info,
                RefreshOnly: true, CasterProjection: projection));
    }

    public void RetireProjectedCaster(uint casterNetId)
    {
        if (_queue is IReplicationIntentQueue typed)
            typed.EnqueueTemporaryCasterRetirement(new TemporaryCasterRetirementMessage(casterNetId));
        else
            _queue.Enqueue(CombatPackets.NpcDie(casterNetId, casterNetId), global: true);
    }

    public void SendSpellData(Entity unit, string spellName, int slot)
    {
        while (unit.ActiveSpellNames.Count <= slot) unit.ActiveSpellNames.Add(string.Empty);
        unit.ActiveSpellNames[slot] = spellName;
        // BBSetSpell uses 0x35: 0x73 resets the native rank to one on a name change.
        Enqueue(SpellPackets.ChangeSlotSpellName(unit.NetId,
            checked((byte)slot), spellName, isSummoner: false));
    }

    public void SendSetSpellData(Entity unit, uint spellHash, int slot) =>
        Enqueue(SpellPackets.SetSpellData(unit.NetId, spellHash, checked((byte)slot)));

    public void SendSpellName(Entity unit, string spellName, int slot, bool summoner = true) =>
        Enqueue(SpellPackets.ChangeSlotSpellName(unit.NetId, checked((byte)slot),
            spellName, summoner));

    public void SendSpellTargetingType(Entity unit, int slot, byte targetingType,
        bool summoner = false) =>
        Enqueue(SpellPackets.ChangeSlotSpellType(unit.NetId, checked((byte)slot),
            targetingType, summoner));

    public void SendSpellOffsetTarget(Entity unit, int slot, uint targetNetId) =>
        Enqueue(SpellPackets.SpellOffsetTarget(unit.NetId, checked((byte)slot), targetNetId));

    public void SendCharacterFade(Entity unit, short fadeId, float? amount = null,
        float duration = 0.0f) =>
        Enqueue(SpellPackets.CharacterFade(unit.NetId, fadeId, amount, duration));

    public void SendTeleport(Entity unit, Position2 destination, byte teleportId,
        long syncId, Position2? staging)
    {
        Enqueue(VisionPackets.Teleport(unit.NetId, destination, teleportId, _bounds,
            checked((int)syncId), staging));
    }

    public void SendCamera(Entity unit, Position2 position) =>
        // Batch 460: the ground height under the point, the Y of the position variable the script passes (BBGetUnitPosition, BBSetCameraPosition).
        Enqueue(VisionPackets.SetCameraPosition(unit.NetId, new Position3(position.X, _height(position.X, position.Z), position.Z)));

    public void SendSpawn(Entity unit)
    {
        SendMinionSpawn(unit, unit.Name, unit.Name, unit.IsWard);
    }

    public void SendMinionSpawn(Entity unit, string displayName, string skinName,
        bool ignoreCollision = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(skinName);
        var team = TeamMapping.ToNative(unit.Team);
        Enqueue(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
            unit.NetId, checked((byte)unit.NodeId), unit.Position, team,
            displayName, skinName, unit.IsWard, ignoreCollision, unit.SkinId, (float)unit.VisibilitySize), _height));
    }

    public void SendInitialVisibility(Entity unit, Position2? wirePosition = null)
    {
        Enqueue(VisionPackets.EnterVisibilityClient(unit.NetId, unit.Name, 0,
            Float(unit.MaxHealth), Float(unit.Health), wirePosition ?? unit.Position,
            unit.Facing));
        Enqueue(VisionPackets.EnterLocalVisibility(unit.NetId,
            Float(unit.MaxHealth), Float(unit.Health)));
    }

    public void SendNidaleeTrap(Entity trap, Team ownerTeam)
    {
        ArgumentNullException.ThrowIfNull(trap);
        if (trap.Kind != EntityKind.NidaleeTrap)
            throw new ArgumentException("Nidalee trap replication requires a trap entity.", nameof(trap));
        // Production delivery rebuilds registered traps per observer and
        // assigns their replication counters during the reveal. A second
        // queued template would arrive afterward with stale stat counters.
        // Plain packet replay has no visibility refresh and still needs it.
        if (_queue is IReplicationIntentQueue) return;
        var team = TeamMapping.ToNative(ownerTeam);
        Enqueue(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
            trap.NetId, 0x40, trap.Position, team,
            "Noxious Trap", "Nidalee_Spear", IgnoreCollision: true), _height));
        Enqueue(CombatPackets.MinionStats(trap.NetId, new MinionStatsSnapshot(
            Hp: 1.0f, AttackDamage: 0.0f, Speed: 0.0f, Armor: 0.0f,
            SpellBlock: 0.0f, AttackSpeedMod: 1.0f,
            ActionState: trap.NoRender ? 1u << 13 : 0u,
            MagicImmune: 1, Invulnerable: 1, Targetable: 0), _syncIds.Next(trap.NetId)));
        // Serialize Bushwhack's additive -1 modifier before native visibility entry.
        Enqueue(CombatPackets.MinionVisionPercentModifier(trap.NetId, -1.0f,
            _syncIds.Next(trap.NetId)));
        Enqueue(VisionPackets.EnterVisibilityClient(trap.NetId, "Nidalee_Spear", 0,
            maxHealth: 1.0f, health: 1.0f,
            position: trap.Position, facing: trap.Facing));
        Enqueue(VisionPackets.EnterLocalVisibility(trap.NetId, 1.0f, 1.0f));
        // The setup animation is Bushwhack's own BBPlayAnimation (the script plays it once); the template does not repeat it.
    }

    public void SendNidaleeTrapDeath(uint trapNetId) =>
        // Preserve effect teardown before native object death. Global routing
        // reaches known traps in fog while still requiring both object references.
        _queue.Enqueue(CombatPackets.NpcDie(trapNetId, trapNetId), global: true);

    public void SendOriannaBall(OriannaBallReplication snapshot, bool teleport, byte teleportId)
    {
        var managedProjection = _updateBall is not null;
        if (_updateBall is not null)
            _updateBall(snapshot, teleport);

        if (teleport)
        {
            Enqueue(VisionPackets.Teleport(snapshot.NetId, snapshot.Position, teleportId,
                _bounds, syncId: 1));
        }
        else if (!managedProjection)
        {
            Enqueue(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
                snapshot.NetId, 0x40, snapshot.Position, snapshot.Team,
                "TheDoomBall", "OriannaBall", IgnoreCollision: true, SkinId: snapshot.SkinId), _height));
            Enqueue(CombatPackets.MinionStats(snapshot.NetId, new MinionStatsSnapshot(
                snapshot.Hp, snapshot.AttackDamage, snapshot.Speed, snapshot.Armor,
                snapshot.SpellBlock, AttackSpeedMod: 1.0f,
                ActionState: (1u << 10) | (1u << 14),
                MagicImmune: 1, Invulnerable: 1, Targetable: 0)));
        }
        if (managedProjection)
        {
            // ReplicationDelivery rebuilds the creation/visibility burst for
            // each observer. Keep the authoritative action-time stat update
            // and movement order in the queue; C2/B5/26 are reconstructed by
            // that flush and must not be emitted directly here.
            Enqueue(CombatPackets.MinionStats(snapshot.NetId,
                new MinionStatsSnapshot(snapshot.Hp, snapshot.AttackDamage,
                    snapshot.Speed, snapshot.Armor, snapshot.SpellBlock,
                    AttackSpeedMod: 1.0f,
                    ActionState: (1u << 10) | (1u << 14) |
                        (_isBallNoRender?.Invoke(snapshot.NetId) == true
                        ? 1u << 13 : 0u),
                    MagicImmune: 1, Invulnerable: 1, Targetable: 0),
                _syncIds.Next(snapshot.NetId)));
        }
        Enqueue(VisionPackets.WaypointList(snapshot.NetId, [snapshot.Position], syncId: 1));
        if (!managedProjection)
        {
            Enqueue(VisionPackets.EnterVisibilityClient(snapshot.NetId, "OriannaBall", 0,
                maxHealth: 0.0f, health: 0.0f,
                position: new Position2(snapshot.Position.X, _height(snapshot.Position.X, snapshot.Position.Z)),
                facing: new Position2(0.0, 1.0)));
            Enqueue(VisionPackets.EnterLocalVisibility(snapshot.NetId, 0.0f, 0.0f));
            Enqueue(SpawnPackets.AddPerceptionBubble(new PerceptionBubble(
                BubbleType: 0,
                Position: new Position3(snapshot.Position.X,
                    _height(snapshot.Position.X, snapshot.Position.Z), snapshot.Position.Z),
                Radius: 225.0f,
                Ttl: 25000.0f,
                BubbleId: snapshot.NetId,
                ClientNetId: 0)));
        }
    }

    public void RemoveOriannaBall(uint objectNetId)
    {
        if (_removeBall is not null)
        {
            _removeBall(objectNetId);
            return;
        }
        Enqueue(VisionPackets.LeaveVisibility(objectNetId)[0]);
        Enqueue(VisionPackets.LeaveLocalVisibility(objectNetId));
        Enqueue(SpawnPackets.RemovePerceptionBubble(objectNetId));
    }

    public void SendPetSpawn(Entity unit, SpawnPetSnapshot snapshot)
    {
        if (snapshot.NetId != unit.NetId || snapshot.OwnerNetId != unit.OwnerNetId)
            throw new ArgumentException("Pet snapshot does not describe the supplied entity", nameof(snapshot));
        Enqueue(SpawnPackets.SpawnPet(snapshot, _height));
    }

    public void SendStats(Entity unit)
    {
        _sendStats(unit);
    }

    public void SendCombatState(Entity unit) => _sendCombatState(unit);

    public void SendAttackSpeed(Entity unit)
    {
        if (unit.UnitType is UnitType.Hero or UnitType.Turret)
            _sendStats(unit);
        else
            Enqueue(CombatPackets.MinionAttackSpeed(unit.NetId, Float(unit.AttackSpeedMod),
                _syncIds.Next(unit.NetId)));
    }

    public void SendMovementSpeed(Entity unit)
    {
        if (unit.UnitType is not (UnitType.Minion or UnitType.Monster) ||
            !IsReferenceReady(unit)) return;
        Enqueue(CombatPackets.MinionMovementSpeed(unit.NetId, Float(unit.Speed),
            _syncIds.Next(unit.NetId)));
    }

    public void SendHealth(Entity unit, double health)
    {
        Enqueue(CombatPackets.ReplicationHp(unit.NetId, Float(health),
            syncId: _syncIds.Next(unit.NetId),
            isHero: unit.UnitType == UnitType.Hero,
            isTurret: unit.IsSceneTurret));
    }

    public uint CreateEffect(string effectName, Position2 position, Position2? target = null,
        uint targetNetId = 0, uint bindNetId = 0, Position3 direction = default,
        Position2? ownerPosition = null, Team? alliedTeam = null, string? enemyEffectName = null,
        string? boneName = null, bool persistent = false, uint ownerHeroNetId = 0,
        Func<(Position2 Source, Position2 Target)>? livePositions = null,
        ushort flags = 0, string? targetBoneName = null, UnboundEffectFallback? unbound = null, Team? fowTeam = null) =>
        CreateEffectAt(effectName, position, target, targetNetId, bindNetId, direction, ownerPosition, alliedTeam, enemyEffectName, boneName,
            persistent, ownerHeroNetId, livePositions, flags, targetBoneName, unbound, fowTeam, _height);

    /// <summary>
    /// Batch 582: the default hit pass's effect as the stock client draws a hit effect itself (the cast routine, for an
    /// attack: the effect routine with flags 8, the unit as bind, no bone, no target, at the point). Flag 8 skips the attachment to the bind
    /// unit, so the emitter keeps the world's orientation where it is put; the client reads the packet's height as is
    /// (it takes the float between the two grid shorts).
    /// </summary>
    public uint CreateHitEffect(string effectName, Position2 position, uint bindNetId, double height) =>
        CreateEffectAt(effectName, position, bindNetId: bindNetId, flags: 0x08, heightProvider: (x, z) => _height(x, z) + height);

    private uint CreateEffectAt(string effectName, Position2 position, Position2? target = null,
        uint targetNetId = 0, uint bindNetId = 0, Position3 direction = default,
        Position2? ownerPosition = null, Team? alliedTeam = null, string? enemyEffectName = null,
        string? boneName = null, bool persistent = false, uint ownerHeroNetId = 0,
        Func<(Position2 Source, Position2 Target)>? livePositions = null,
        ushort flags = 0, string? targetBoneName = null, UnboundEffectFallback? unbound = null, Team? fowTeam = null,
        Func<double, double, double>? heightProvider = null)
    {
        var height = heightProvider ?? _height;
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        if (enemyEffectName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enemyEffectName);
            if (alliedTeam is null) throw new ArgumentException("An enemy effect variant requires its allied team.", nameof(alliedTeam));
        }
        var effect = _allocate(UnitType.Missile);
        // Native 0x8C uses its third position to refresh targetNetId. Keep an
        // explicit ownerPosition, and leave unbound effects' legacy field alone.
        var targetObjectPosition = ownerPosition ??
            (targetNetId != 0 ? target : null) ?? position;
        var packet = CreatePacket(effectName, position, target, targetObjectPosition);
        if (_queue is IReplicationIntentQueue typed)
        {
            // Only the moving source/target coordinates vary on replay; effect
            // identity, object references, name and routing stay captured here.
            Func<bool, PlaintextPacket>? livePacket = livePositions is null ? null : enemy =>
            {
                var (source, currentTarget) = livePositions();
                return CreatePacket(enemy ? enemyEffectName ?? effectName : effectName,
                    source, currentTarget, currentTarget);
            };
            // Batch 419 (Reconstruction): the unbound variant of a bound effect, for the delivery to send when the bound unit is dead on the wire.
            PlaintextPacket? unboundPacket = null, unboundEnemyPacket = null;
            if (bindNetId != 0 && unbound is { } fallback)
            {
                unboundPacket = UnboundPacket(effectName, fallback);
                if (enemyEffectName is not null) unboundEnemyPacket = UnboundPacket(enemyEffectName, fallback);
            }
            typed.EnqueueEffectCreate(new EffectCreateReplicationMessage(packet, effect, position,
                alliedTeam, enemyEffectName is null ? null : CreatePacket(enemyEffectName,
                    position, target, targetObjectPosition),
                persistent, ownerHeroNetId, livePacket, BindNetId: bindNetId,
                UnboundPacket: unboundPacket, UnboundEnemyPacket: unboundEnemyPacket, FowTeam: fowTeam));
            return effect;
        }
        Enqueue(packet);
        return effect;

        PlaintextPacket UnboundPacket(string name, UnboundEffectFallback fallback)
        {
            // The placemarker rule (BbNativeHandlers.SpellEffectCreate): the unit's own point, no bind, and a unit target only when it is not the bound
            // unit. Reconstruction: the wire of an unbound effect is the placemarker's (batch 397-398), not a retail capture.
            var anchor = fallback.OwnerPosition ?? (fallback.TargetNetId != 0 ? fallback.Target : null) ?? fallback.Position;
            return FxPackets.CreateGroup(SpellPackets.BuffNameHash(GameData.ParticleStem(name)), effect,
                position: fallback.Position, bounds: _bounds, heightProvider: height,
                targetPosition: fallback.Target, ownerPosition: anchor,
                targetNetId: fallback.TargetNetId, bindNetId: 0, flags: flags,
                direction: direction,
                boneHash: boneName is null ? 0 : SpellPackets.BuffNameHash(boneName),
                targetBoneHash: targetBoneName is null ? 0 : SpellPackets.BuffNameHash(targetBoneName));
        }

        PlaintextPacket CreatePacket(string name, Position2 source,
            Position2? targetPoint, Position2 targetAnchor)
        {
            // Native particle lookups use the asset's stem (GameData.ParticleStem): the name without its extension, and without the spaces a
            // script leaves after it (StaticField's shipped script uses .prt instead of .troy; RivenFeint's E pads its name with two spaces).
            // Reconstruction: retail's hash of a padded or mistyped name is not recovered; the client's particle map is keyed by stem, so the
            // stem is the name the create can resolve to.
            var hashName = GameData.ParticleStem(name);
            return FxPackets.CreateGroup(SpellPackets.BuffNameHash(hashName), effect,
                position: source, bounds: _bounds, heightProvider: height,
                targetPosition: targetPoint, ownerPosition: targetAnchor,
                targetNetId: targetNetId, bindNetId: bindNetId, flags: flags,
                direction: direction,
                boneHash: boneName is null ? 0 : SpellPackets.BuffNameHash(boneName),
                targetBoneHash: targetBoneName is null ? 0 : SpellPackets.BuffNameHash(targetBoneName));
        }
    }

    public void KillEffect(uint effectNetId)
    {
        if (effectNetId == 0) return;
        var packet = FxPackets.Kill(effectNetId);
        if (_queue is IReplicationIntentQueue typed)
        {
            typed.EnqueueEffectDestroy(new EffectDestroyReplicationMessage(packet, effectNetId));
            return;
        }
        Enqueue(packet);
    }

    /// <summary>
    /// Whether a buff of this name may appear on a client's wire at all. Unknown names make the stock client dump its
    /// entire buff hash map and assert, and a server-only marker (<see cref="BuffPayload.ServerOnlyMarker"/>) would be
    /// loaded as the buff script its name hash resolves to and drawn from that script's template: one ward placement
    /// painted Morgana's Q's DarkBinding_tar.troy on the ward that way (batch 157 part 2). Both kinds still apply
    /// their authoritative stats server-side; they simply send no add, renewal, removal or tooltip for themselves.
    /// </summary>
    private bool ReachesClient(BuffState buff) => !buff.Payload.ServerOnlyMarker && _data.HasClientBuff(buff.Name);

    /// <summary>
    /// The duration every wire packet for <paramref name="buff"/> carries: the server's own remaining time plus the
    /// margin a periodically re-added buff is given so its client copy cannot run out before the next re-add reaches it
    /// (<see cref="BuffPayload.WireDurationMargin"/>). The server's timer still ends where the script's Duration says.
    /// </summary>
    private static float WireDuration(BuffState buff) =>
        Float(buff.ExpiresAt + buff.Payload.WireDurationMargin);

    public void RenewBuff(Entity unit, BuffState buff)
    {
        if (IsReferenceReady(unit) && ReachesClient(buff))
            Enqueue(SpellPackets.RenewBuff(unit.NetId, checked((byte)buff.WireSlot), WireDuration(buff), buff.CasterNetId));
    }

    /// <summary>
    /// Batch 399 (Reconstruction): the other live instances of the overlapping group <paramref name="buff"/> belongs to and that reach the client
    /// (same name, same caster, same wire slot).
    /// </summary>
    private List<BuffState> OverlapGroup(Entity unit, BuffState buff) =>
        unit.Buffs.Where(member => !ReferenceEquals(member, buff) && member.Overlapping && member.WireSlot == buff.WireSlot &&
            member.HasName(buff.Name) && member.CasterNetId == buff.CasterNetId && ReachesClient(member)).ToList();

    public void SendBuffDelta(Entity unit, BuffState buff, bool added, int count = 1)
    {
        if (!ReachesClient(buff)) return;
        // Batch 181: the add and the remove carry the name the client holds data for (GameData.ClientBuffName), never two hashes.
        var nameHash = SpellPackets.BuffNameHash(_data.ClientBuffName(buff.Name));
        if (buff.Overlapping)
        {
            // Batch 399 (Reconstruction): one client icon per overlapping group, whose count is the number of live instances. Each change
            // sends the group's state on the group's wire slot: an add to a copy the client holds becomes a count update there
            // (ReplicationDelivery.AsHeldBuffUpdate, 0x1F), and the group's last removal is the only 0x7F. The duration is the longest-lived
            // instance's, which is the time the copy has left.
            var members = OverlapGroup(unit, buff);
            if (added) members.Add(buff);
            if (members.Count == 0)
            {
                Enqueue(SpellPackets.RemoveBuff(unit.NetId, checked((byte)buff.WireSlot), nameHash));
                return;
            }
            var lead = members.OrderByDescending(member => member.ExpiresAt).First();
            Enqueue(SpellPackets.AddBuff(new BuffAdd(unit.NetId, checked((byte)buff.WireSlot),
                (byte)lead.Type, nameHash,
                WireDuration(lead), lead.CasterNetId, checked((byte)members.Count),
                IsHidden: lead.HiddenOnClientByte)));
            if (added) SendBuffTooltip(unit, buff);
            return;
        }
        var packet = added
            ? SpellPackets.AddBuff(new BuffAdd(unit.NetId, checked((byte)buff.Slot),
                (byte)buff.Type, nameHash,
                WireDuration(buff), buff.CasterNetId, checked((byte)Math.Max(0, count)),
                IsHidden: buff.HiddenOnClientByte))
            : SpellPackets.RemoveBuff(unit.NetId, checked((byte)buff.Slot), nameHash);
        // Batch 405: a toggle carrier's add names the slot it toggles, so the delivery can send it behind that slot's swap.
        if (added && buff.Payload.ToggleSlot is { } toggleSlot) _queue.EnqueueToggleCarrier(packet, unit.NetId, toggleSlot);
        else Enqueue(packet);
        if (added) SendBuffTooltip(unit, buff);
    }

    public void SendBuffTooltip(Entity unit, BuffState buff)
    {
        if (ReachesClient(buff) &&
            buff.WireSlot is >= 0 and < SpellPackets.MaxBuffTooltipSlots &&
            BuffEffectService.TryGetBuffTooltipValues(buff,
                out var value0, out var value1, out var value2))
        {
            // Buff selectors are raw slots; unlike spellbook selectors they
            // are not offset by 60. An overlapping instance names its group's slot (batch 399).
            Enqueue(SpellPackets.BuffTooltipVariables(unit.NetId, buff.WireSlot,
                Float(value0), Float(value1), Float(value2)));
        }
    }

    public void SendShieldDelta(Entity unit, double amount) =>
        Enqueue(SpellPackets.ModifyShield(unit.NetId, Float(amount)));

    public void SendShieldDelta(Entity unit, double amount, bool physical, bool magic, bool noFade) =>
        Enqueue(SpellPackets.ModifyShield(unit.NetId, Float(amount), physical, magic, noFade));

    public void PauseAnimation(Entity unit, bool paused) =>
        Enqueue(SpellPackets.PauseAnimation(unit.NetId, paused));

    public void SendLevelUpSpell(Entity unit, int slot) =>
        Enqueue(SpellPackets.LevelUpSpell(unit.NetId, slot));

    public void SendTargetableToTeam(Entity unit) =>
        Enqueue(CombatPackets.UnitTargetableToTeam(unit,
            unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0, _syncIds.Next(unit.NetId)));

    public void SendColorRemap(bool fadeIn, float fadeTime, int teamId, uint color, float maxWeight) =>
        _queue.Enqueue(SpellPackets.ColorRemapFx(fadeIn, fadeTime, teamId, color, maxWeight), global: true);

    public void SendVoiceOverride(Entity unit, string? suffix) =>
        Enqueue(SpellPackets.ChangeCharacterVoice(unit.NetId, suffix));

    public void SendParColorOverride(Entity unit, ParColorOverride? value) =>
        Enqueue(SpellPackets.ChangeParColorOverride(unit.NetId, value));

    private void Enqueue(PlaintextPacket packet) => _queue.Enqueue(packet);

    private static float Float(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }
}
