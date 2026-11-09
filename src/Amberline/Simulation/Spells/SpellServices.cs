using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells;

/// <summary>Required combat boundary for authoritative spell execution.</summary>
public interface ISpellCombatService
{
    /// <summary>The match is over (a Nexus fell or a team surrendered): the end-game ceremony runs and every unit's AI is halted. False for a service with no match.</summary>
    bool GameOver => false;
    bool IsTargetable(Entity target, Entity? observer);
    /// <summary>Returns the live incoming-heal multiplier for a unit.</summary>
    double HealingMultiplier(Entity target);
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale = 1.0) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect: false, sourceIsPeriodic: false, spellOwnerNetId: null,
            recapOrigin: null);
    // Metadata-preserving boundary for item reflections and periodic effects.
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale, bool isReflect,
        bool sourceIsPeriodic, uint? spellOwnerNetId);

    /// <summary>
    /// Optional producer-owned recap metadata. Implementations that do not own
    /// the authoritative combat service retain the old behavior and drop the
    /// metadata through the default overload.
    /// </summary>
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale, bool isReflect,
        bool sourceIsPeriodic, uint? spellOwnerNetId,
        DeathRecapOrigin? recapOrigin) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect, sourceIsPeriodic, spellOwnerNetId);

    /// <summary>
    /// BBApplyDamage with its IgnoreDamageIncreaseMods / IgnoreDamageCrit inputs (Bilgewater Cutlass, Hextech Gunblade and
    /// Tiamat's splash set both). Implementations that do not own the authoritative combat service drop the flags like the
    /// other optional metadata.
    /// </summary>
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale, bool isReflect,
        bool sourceIsPeriodic, uint? spellOwnerNetId, DeathRecapOrigin? recapOrigin,
        bool ignoreDamageIncreaseMods, bool ignoreDamageCrit) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect, sourceIsPeriodic, spellOwnerNetId, recapOrigin);

    /// <summary>
    /// BBApplyDamage with its CallForHelpAttackerVar: the unit that idle neutrals wake on and that the camp's call for help names, when it
    /// is not the credited attacker (ShacoBoxSpell credits Shaco and names the box). Implementations that do not own the authoritative combat
    /// service drop it like the other optional metadata.
    /// </summary>
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale, bool isReflect,
        bool sourceIsPeriodic, uint? spellOwnerNetId, DeathRecapOrigin? recapOrigin,
        bool ignoreDamageIncreaseMods, bool ignoreDamageCrit, Entity? callForHelpSource) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect, sourceIsPeriodic, spellOwnerNetId, recapOrigin,
            ignoreDamageIncreaseMods, ignoreDamageCrit);

    /// <summary>
    /// The same with the hit's critical result (batch 301): a script's attack hit whose <c>HitResult</c> is critical (Parley's own roll). The
    /// amount already carries the critical multiplier; the hit goes out as a critical (0x68 result type 3) and its hit hooks read
    /// <c>HIT_Critical</c>. Implementations that do not own the authoritative combat service drop it like the other optional metadata.
    /// </summary>
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, double vampScale, bool isReflect,
        bool sourceIsPeriodic, uint? spellOwnerNetId, DeathRecapOrigin? recapOrigin,
        bool ignoreDamageIncreaseMods, bool ignoreDamageCrit, Entity? callForHelpSource, bool critical) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect, sourceIsPeriodic, spellOwnerNetId, recapOrigin,
            ignoreDamageIncreaseMods, ignoreDamageCrit, callForHelpSource);

    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin) =>
        ApplyDamage(source, target, amount, type, sourceKind, 1.0,
            isReflect: false, sourceIsPeriodic: false, spellOwnerNetId: null,
            recapOrigin);
    // DoesntBreakShields is independent of the shipped damage-source category.
    bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
        DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield);

    bool ConsumeSpellShield(Entity source, Entity target) =>
        ConsumeSpellShield(source, target, force: false);
    bool ConsumeSpellShield(Entity source, Entity target, bool force);
    bool AwardHeroProgress(Entity hero, double experience) => false;
    /// <summary>
    /// Gold a script gives a hero (<c>BBIncGold</c>): added to its gold and earned total and sent with the gold and experience sync, and the
    /// 0x25 (UnitAddGold) the kill rewards send, from <paramref name="source"/>: the unit the gold came from, when the script names one.
    /// </summary>
    bool AwardHeroGold(Entity hero, double gold, Entity? source) => false;
    /// <summary>
    /// Records the source-side assist marker used by support spells and enemy
    /// debuffs. The combat service owns expiry and team/owner attribution.
    /// </summary>
    void RecordSupportAssist(Entity source, Entity recipient, double durationSeconds = 10.0) { }
    void InterruptBasicAttack(Entity attacker);
    /// <summary>
    /// The reveal routine for a cast whose first unit target is
    /// <paramref name="target"/>: shows the caster to that unit's team at the cast position.
    /// Implementations without a combat service have no awareness state to set.
    /// </summary>
    void RevealAttack(Entity attacker, Entity target) { }
    void CancelPendingWindups(uint sourceNetId) { }
    /// <summary>
    /// BBDestroyMissileForTarget for the basic-attack missiles in flight at a unit (Teleport's completion, Zhonya's
    /// activation); the spell projectiles are destroyed by the spell system.
    /// </summary>
    int CancelPendingTargetMissiles(uint targetNetId) => 0;
    void Kill(Entity unit, uint killerNetId);
    /// <summary>Runs the ordinary hero respawn lifecycle immediately.</summary>
    bool RespawnImmediately(Entity unit) => false;
}

/// <summary>
/// The unbound variant of a bound effect (batch 419, Reconstruction, batch 418 evidence): the point rules a NoRender placemarker's effect is sent
/// with (BbNativeHandlers.SpellEffectCreate), for an effect whose unit is dead when the effect goes on the wire. Position is the unit's point,
/// Target the target point, and TargetNetId a unit target other than the bound unit (0 otherwise).
/// </summary>
public readonly record struct UnboundEffectFallback(Position2 Position, Position2? Target, uint TargetNetId, Position2? OwnerPosition);

/// <summary>
/// Required packet/effect boundary. Implementations may enqueue through the
/// observer delivery service or capture events for offline checks.
/// </summary>
public interface ISpellEffectSink
{
    bool IsReferenceReady(Entity unit);
    void SendCast(Entity caster, SpellCastInfo info, long casterPositionSyncId = 0, bool missileOnlyForCaster = false);
    /// <summary>
    /// The cast of an Instacast child that launches a missile: no view gets its cast answer, every view the answer would reach takes the
    /// missile's creation 0x41 alone (batch 582, <see cref="ProtocolSpellEffectSink.SendInstantMissileCast"/>). Offline sinks record a cast.
    /// </summary>
    void SendInstantMissileCast(Entity caster, SpellCastInfo info) => SendCast(caster, info);
    void SendMissile(Entity caster, SpellCastInfo info, Position3 position,
        Position3 direction, Position3 velocity, Position3 startPoint,
        Position3 endPoint, Position3? unitPosition = null,
        Position3? casterPosition = null, bool needsCastAnnouncement = false);
    void SendProjectedMissile(MissileCasterProjection projection, SpellCastInfo info,
        Position3 position, Position3 direction, Position3 velocity,
        Position3 startPoint, Position3 endPoint, Position3 unitPosition,
        Position3 casterPosition) =>
        throw new NotSupportedException("The spell effect sink does not implement projected missile casters.");
    void RefreshProjectedMissile(MissileCasterProjection projection, SpellCastInfo info,
        Position3 position, Position3 direction, Position3 velocity,
        Position3 startPoint, Position3 endPoint, Position3 unitPosition,
        Position3 casterPosition, float speed, float lifePercentage) =>
        throw new NotSupportedException("The spell effect sink does not implement projected missile casters.");
    void RetireProjectedCaster(uint casterNetId) =>
        throw new NotSupportedException("The spell effect sink does not implement projected missile casters.");
    /// <summary>
    /// Publishes an active-flight missile snapshot for typed observer routing.
    /// Capture sinks intentionally ignore this because it is not an immediate
    /// wire emission and must not become a per-tick duplicate 0x41 packet.
    /// </summary>
    void RefreshMissile(Entity caster, SpellCastInfo info, Position3 position,
        Position3 direction, Position3 velocity, Position3 startPoint,
        Position3 endPoint, Position3 unitPosition, Position3 casterPosition,
        float speed, float lifePercentage) { }
    /// <summary>Retires observer state for a cast-created missile that the
    /// native owner already retires on impact. Capture-only sinks have no views.</summary>
    void RetireCastMissile(uint missileNetId) { }
    /// <summary>
    /// Tells observers the targets a chain missile (CastType 2, <paramref name="missileNetId"/>) flies through. The cast announcement
    /// names only the first target; the list goes out in the missile's own name once the client has the missile, one windup after the
    /// cast (<c>BbChainSync</c>, <c>SpellPackets.ChainMissileSync</c>). <paramref name="attack"/> marks a chain attack, whose missile
    /// the clients got with the basic-attack packet (the first hop is the attacked unit) rather than with a cast. Capture-only sinks
    /// have no wire.
    /// </summary>
    void SendChainMissileSync(Entity caster, uint missileNetId, IReadOnlyList<uint> targets, bool attack = false) { }
    /// <summary>Batch 591: the units a line missile just hit (<see cref="Amberline.Network.Protocol.SpellPackets.LineMissileHitList"/>), before
    /// their hit runs. Capture-only sinks have no wire.</summary>
    void SendLineMissileHitList(uint missileNetId, IReadOnlyList<uint> targets) { }
    /// <summary>Retires a client missile object after impact or expiry.</summary>
    void SendDestroyMissile(uint missileNetId) =>
        throw new NotSupportedException("The spell effect sink does not implement missile retirement.");
    void SendWaypoints(Entity unit, IReadOnlyList<Position2> points, long syncId);
    void SendStopAttack(Entity unit);
    /// <summary>
    /// A stop the unit's client cannot refuse (<c>CombatPackets.ForceStopFlags</c>): ends the channel it is running. A sink without that wire sends
    /// the ordinary stop.
    /// </summary>
    void SendForcedStop(Entity unit) => SendStopAttack(unit);
    void SendFinishCast(Entity unit) =>
        throw new NotSupportedException("The spell effect sink does not implement cast completion.");
    void SendCooldown(Entity unit, int slot, double seconds, bool summoner = false);
    /// <summary>Forces the client back to the ordinary dead state after a temporary revival.</summary>
    void SendForceDead(Entity unit) =>
        throw new NotSupportedException("The spell effect sink does not implement force-dead replication.");
    /// <summary>Replicates native ShowHealthBar (0xD7): hides or shows the unit's health bar on every client.</summary>
    void SendShowHealthBar(Entity unit, bool show) =>
        throw new NotSupportedException("The spell effect sink does not implement health bar replication.");
    /// <summary>Replicates the unit's <c>IsTargetableToTeam</c> flags (LOCAL1): which teams may not target it (<c>BBSetNotTargetableToTeam</c>).</summary>
    void SendTargetableToTeam(Entity unit) =>
        throw new NotSupportedException("The spell effect sink does not implement per-team targetability.");
    /// <summary>Replicates native ColorRemapFX (0xE4): a screen tint fades in or out for one team (0: every team). Global, the packet names no unit.</summary>
    void SendColorRemap(bool fadeIn, float fadeTime, int teamId, uint color, float maxWeight) =>
        throw new NotSupportedException("The spell effect sink does not implement colour-remap fades.");
    /// <summary>Replicates native ChangeCharacterVoice (0x9E): the unit's voice suffix, or null for its own voice again.</summary>
    void SendVoiceOverride(Entity unit, string? suffix) =>
        throw new NotSupportedException("The spell effect sink does not implement voice overrides.");
    /// <summary>Replicates native ChangePARColorOverride (0x99): the unit's resource-bar colours, or null for its own again.</summary>
    void SendParColorOverride(Entity unit, ParColorOverride? value) =>
        throw new NotSupportedException("The spell effect sink does not implement resource-bar colour overrides.");
    /// <summary>Replicates native LevelUpSpell (0x5E): the client's spellbook raises the slot's level by one, with no skill point spent.</summary>
    void SendLevelUpSpell(Entity unit, int slot) =>
        throw new NotSupportedException("The spell effect sink does not implement spell level increments.");
    void SendUpgrade(Entity unit, int slot, int rank, int pointsLeft);
    void SendFaceDirection(Entity unit, Position3 direction);
    void PlayEmote(Entity unit, ChampionEmote emote) =>
        throw new NotSupportedException("The spell effect sink does not implement champion emotes.");
    void PlayAnimation(Entity unit, string name, bool loop = false, bool lockAnimation = false,
        bool blend = true, float duration = 0) =>
        throw new NotSupportedException("The spell effect sink does not implement animation playback.");
    void StopAnimation(Entity unit, bool blend = true) =>
        throw new NotSupportedException("The spell effect sink does not implement animation cleanup.");
    void SetAnimationStates(Entity unit, IReadOnlyList<(string Original, string Replacement)> states) =>
        throw new NotSupportedException("The spell effect sink does not implement animation overrides.");
    void SetAutoAttackSpellSlot(Entity unit, int slot) =>
        throw new NotSupportedException("The spell effect sink does not implement attack overrides.");
    /// <summary>Replicates a forced movement dash to observers.</summary>
    void SendDash(Entity unit, Position2 start, Position2 destination,
        double speed, double gravity, long syncId, uint followNetId = 0,
        double followDistance = 0.0, double followBackDistance = 0.0) =>
        throw new NotSupportedException("The spell effect sink does not implement dash replication.");
    void SendPushCharacterData(Entity unit, uint changeId, string skinName, bool useSpells = false) =>
        throw new NotSupportedException("The spell effect sink does not implement character-data pushes.");
    void SendPopCharacterData(Entity unit, uint changeId) =>
        throw new NotSupportedException("The spell effect sink does not implement character-data pops.");
    /// <summary>The 5-byte 0x6A of <c>BBPopAllCharacterData</c>: the client clears the unit's whole character-data stack.</summary>
    void SendPopAllCharacterData(Entity unit) =>
        throw new NotSupportedException("The spell effect sink does not implement character-data pops.");
    void SendSpellData(Entity unit, string spellName, int slot) =>
        throw new NotSupportedException("The spell effect sink does not implement spell-data replication.");
    /// <summary>
    /// Replicates native SetSpellData (0x73): the spellbook router's set-spell-data (case 0x73)
    /// binds the hashed spell to the unit's slot at rank 1, slots 0 to 0x33.
    /// </summary>
    void SendSetSpellData(Entity unit, uint spellHash, int slot) =>
        throw new NotSupportedException("The spell effect sink does not implement 0x73 slot binding.");
    /// <summary>Replicates the variable length native slot-name update (0x35).</summary>
    void SendSpellName(Entity unit, string spellName, int slot, bool summoner = true) =>
        throw new NotSupportedException("The spell effect sink does not implement slot-name replication.");
    /// <summary>Replicates native ChangeSlotSpellType (0x1A).</summary>
    void SendSpellTargetingType(Entity unit, int slot, byte targetingType,
        bool summoner = false) =>
        throw new NotSupportedException("The spell effect sink does not implement targeting-type replication.");
    void SendSpellOffsetTarget(Entity unit, int slot, uint targetNetId) =>
        throw new NotSupportedException("The spell effect sink does not implement spell offset replication.");
    /// <summary>Replicates the native CharacterFade packet used by Shaco's fade and box effects.</summary>
    void SendCharacterFade(Entity unit, short fadeId, float? amount = null, float duration = 0.0f);
    void SendTeleport(Entity unit, Position2 destination, byte teleportId, long syncId,
        Position2? staging);
    void SendCamera(Entity unit, Position2 position);
    void SendSpawn(Entity unit);
    /// <summary>
    /// Replicates a non-pet minion with the source display name and model
    /// name. Synthetic units such as Shaco's box and Yorick's ghouls have
    /// different values for these fields, so callers must not infer either
    /// one from <see cref="Entity.Name"/>.
    /// </summary>
    void SendMinionSpawn(Entity unit, string displayName, string skinName,
        bool ignoreCollision = true) =>
        throw new NotSupportedException(
            "The spell effect sink does not implement typed minion spawning.");
    /// <summary>Emits the initial C2/B5 reveal after a synthetic minion spawn.</summary>
    void SendInitialVisibility(Entity unit, Position2? wirePosition = null) =>
        throw new NotSupportedException(
            "The spell effect sink does not implement initial minion visibility.");
    /// <summary>Queues the creation template for Nidalee's registered Bushwhack trap.</summary>
    void SendNidaleeTrap(Entity trap, Team ownerTeam) =>
        throw new NotSupportedException("The spell effect sink does not implement Nidalee trap replication.");
    /// <summary>Retires a known Nidalee trap after trigger or expiry, including in fog.</summary>
    void SendNidaleeTrapDeath(uint trapNetId) =>
        throw new NotSupportedException("The spell effect sink does not implement Nidalee trap retirement.");
    /// <summary>Replicates Orianna's deployed ball object lifecycle.</summary>
    void SendOriannaBall(OriannaBallReplication snapshot, bool teleport, byte teleportId) =>
        throw new NotSupportedException("The spell effect sink does not implement Orianna ball replication.");
    /// <summary>Hides Orianna's deployed ball and removes its perception bubble.</summary>
    void RemoveOriannaBall(uint objectNetId) =>
        throw new NotSupportedException("The spell effect sink does not implement Orianna ball removal.");
    /// <summary>Emits the native Pet.lua creation packet with its clone metadata.</summary>
    void SendPetSpawn(Entity unit, SpawnPetSnapshot snapshot);
    void SendStats(Entity unit);
    /// <summary>
    /// Re-sends a unit's combat state (ActionState and the MagicImmune, Invulnerable and Targetable flags) after a script changed one of its
    /// statuses: the narrow block, which writes no HP (batch 359). The default does nothing, as a sink without minion replication has no block.
    /// </summary>
    void SendCombatState(Entity unit) { }
    void SendAttackSpeed(Entity unit);
    /// <summary>Replicates only an NPC's movement speed field.</summary>
    void SendMovementSpeed(Entity unit) =>
        throw new NotSupportedException("The spell effect sink does not implement movement-speed replication.");
    /// <summary>Replicates a clone's initial partial health after its stats burst.</summary>
    void SendHealth(Entity unit, double health);
    /// <summary>Creates one effect identity; production observers outside alliedTeam
    /// receive enemyEffectName when provided. Persistent effects replay on late
    /// sight until KillEffect; callers must own that lifetime explicitly.
    /// livePositions refreshes source, target and target-object anchor at delivery;
    /// the captured effect identity and object references remain unchanged.
    /// unbound (batch 419) is the variant sent instead when the bound unit is dead on the wire (ReplicationDelivery.OnWire).</summary>
    uint CreateEffect(string effectName, Position2 position, Position2? target = null,
        uint targetNetId = 0, uint bindNetId = 0, Position3 direction = default,
        Position2? ownerPosition = null, Team? alliedTeam = null, string? enemyEffectName = null,
        string? boneName = null, bool persistent = false, uint ownerHeroNetId = 0,
        Func<(Position2 Source, Position2 Target)>? livePositions = null,
        ushort flags = 0, string? targetBoneName = null, UnboundEffectFallback? unbound = null, Team? fowTeam = null);
    /// <summary>Batch 582: a default hit pass effect, placed in the world at <paramref name="position"/>, <paramref name="height"/> above the ground,
    /// and not attached to <paramref name="bindNetId"/> (0x8C flags 0x08; <see cref="ProtocolSpellEffectSink.CreateHitEffect"/>).</summary>
    uint CreateHitEffect(string effectName, Position2 position, uint bindNetId, double height) =>
        CreateEffect(effectName, position, bindNetId: bindNetId, flags: 0x08);
    void KillEffect(uint effectNetId);
    /// <summary>Batch 726: a persistent effect whose owner ended without removing it stops replaying on late sight; only a copy a late sight
    /// replayed is killed (<c>BbSpellHost.RetireOwnedEffects</c>). Capture-only sinks have no views.</summary>
    void RetireEffect(uint effectNetId) { }
    void SendBuffDelta(Entity unit, BuffState buff, bool added, int count = 1);
    void SendBuffTooltip(Entity unit, BuffState buff) { }
    void RenewBuff(Entity unit, BuffState buff) => SendBuffDelta(unit, buff, true);
    void SendShieldDelta(Entity unit, double amount);
    /// <summary>The same 0x69 packet with the shield kinds it applies to (BBIncreaseShield and its siblings name them).</summary>
    void SendShieldDelta(Entity unit, double amount, bool physical, bool magic, bool noFade) => SendShieldDelta(unit, amount);
    void PauseAnimation(Entity unit, bool paused);
}

/// <summary>A non-world native caster needed by a travelling missile.</summary>
public readonly record struct MissileCasterProjection(
    uint NetId, uint OwnerNetId, Team Team, Position3 Origin, Position2 Facing);


/// <summary>Required typed buff lifecycle boundary used by spells.</summary>
public interface ISpellBuffService
{
    BuffState? Add(Entity unit, string name, BuffType type, double duration,
        uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true);
    bool Remove(Entity unit, int slot);
    /// <summary>
    /// A removal the running hook run may take back: its packet waits until that run ends, and an add of the same buff in the run takes
    /// the slot (<c>BuffLifecycle.RemoveBuffForRenewal</c>). The default is an ordinary removal.
    /// </summary>
    bool RemoveForRenewal(Entity unit, int slot, object frame) => Remove(unit, slot);
    /// <summary>Sends the removal packets that <see cref="RemoveForRenewal"/> held for this hook run, the ones no add took.</summary>
    void FlushRenewalRemovals(object frame) { }
    bool Has(Entity unit, string name);
    /// <summary>
    /// Adds or refreshes one stack through the authoritative lifecycle. When
    /// replacement is requested, the live slot and managed state are retained
    /// while the supplied payload replaces the previous payload. <paramref name="canMitigateDuration"/>
    /// is the add's own <c>CanMitigateDuration</c>, carried but not read (tenacity is Hardening.luaobj's hook, batch 296).
    /// </summary>
    BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
        uint casterNetId, int maxStacks, BuffPayload perStackPayload,
        bool replaceExistingPayload = false, bool canMitigateDuration = true);
    /// <summary>
    /// BUFF_STACKS_AND_OVERLAPS: a new buff instance (own slot, own timer) per application; nothing is replaced.
    /// </summary>
    BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration, uint casterNetId,
        BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null) =>
        throw new NotSupportedException("The spell buff service does not implement overlapping buffs.");
    /// <summary>
    /// BUFF_STACKS_AND_CONTINUE: one instance whose count rises to <paramref name="maxStacks"/> while its timer keeps running.
    /// </summary>
    BuffState? AddContinuing(Entity unit, string name, BuffType type, double duration, uint casterNetId, int maxStacks,
        BuffPayload perStackPayload, bool canMitigateDuration = true) =>
        throw new NotSupportedException("The spell buff service does not implement continuing stacks.");
    void Tick(Entity unit, double dt);
    /// <summary>Rebuilds movement from active payloads and emits a speed delta when visible.</summary>
    void RefreshMovementSpeed(Entity unit, bool push = true) =>
        throw new NotSupportedException("The spell buff service does not implement movement-speed refresh.");
    /// <summary>
    /// A script status switched on for <paramref name="unit"/> with no buff to hold it (batch 201): the control effects its add had
    /// (a root stops the walk, a hero's stun cancels its pending cast, a new hard control interrupts the swing). The status is already
    /// in <see cref="Entity.ScriptStatus"/> when this runs.
    /// </summary>
    void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) =>
        throw new NotSupportedException("The spell buff service does not implement script statuses.");
    /// <summary>
    /// A script status switched off for <paramref name="unit"/> (batch 201): the release callbacks its removal ran (the minion, script
    /// AI, spawned and pet callbacks). <paramref name="couldMove"/> and <paramref name="couldAttack"/> are the walk and the swing the unit had
    /// before the release; the status is already gone from <see cref="Entity.ScriptStatus"/>.
    /// </summary>
    void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) =>
        throw new NotSupportedException("The spell buff service does not implement script statuses.");
    /// <summary>
    /// Swaps the stat payload of a live script buff (BuildingBlocks <c>BuffOnUpdateStats</c> polled again) without a wire add;
    /// <paramref name="perStackPayload"/> is scaled by the buff's stacks.
    /// </summary>
    bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload) =>
        throw new NotSupportedException("The spell buff service does not implement script payload replacement.");
    /// <summary>
    /// A script buff loses stacks and stays on its holder (BuildingBlocks <c>BBSpellBuffRemove</c> takes one): the count falls by <paramref name="count"/>,
    /// <paramref name="perStackPayload"/> (sampled for the stacks that remain) replaces the stat payload scaled by the new count, and the client
    /// is told the count. False when the buff is gone or would keep no stack.
    /// </summary>
    bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1) =>
        throw new NotSupportedException("The spell buff service does not implement script stack removal.");
    /// <summary>
    /// Whether the table has room for a new <paramref name="name"/> buff added by <paramref name="caster"/> through <see cref="Add"/>:
    /// a hostile control added to a hero with Boots of Mobility also starts the BootsOfMobilityDebuff, which needs a slot of its own.
    /// <paramref name="freeableSlots"/> is the room the add's own activation frees as it lands (the script engine's
    /// <c>ActivationFreeable</c>), which counts as slots the table already has open.
    /// </summary>
    bool CanAdd(Entity unit, string name, Entity? caster, BuffType type, int freeableSlots = 0) =>
        Buffs.BuffLifecycle.CanAddNamedBuffs(unit, freeableSlots, name);
    /// <summary>
    /// Admission for one group of adds made together (the script engine's per-hook group): <paramref name="applications"/> overlapping
    /// applications, each its own slot, and one new slot per <paramref name="names"/> entry the table does not hold yet. See
    /// <see cref="Buffs.BuffLifecycle.CanAddOverlapping"/>; the default asks the same question without the slots of buffs still being removed.
    /// </summary>
    bool CanAddOverlapping(Entity unit, int applications = 1, params string[] names) =>
        Buffs.BuffLifecycle.CanAddGroup(unit, applications, names);
    /// <summary>
    /// Whether the lifecycle would refuse this add for a reason other than a full table: see <see cref="Buffs.BuffLifecycle.RefusesAdd"/>.
    /// An add this refuses takes no slot, so it stays out of the group above. The default assumes the add would be placed.
    /// </summary>
    bool RefusesAdd(Entity unit, BuffType type, uint casterNetId, int? maxStack = null) => false;
}

/// <summary>Required champion hook used for cast bookkeeping and channel state.</summary>
public interface IChampionSpellHooks
{
    void OnSpellCast(Entity hero, int slot, double now);
    /// <summary>
    /// The cast hooks of a cast the engine names rather than a champion slot (a summoner slot or <c>Recall</c>):
    /// <c>CharOnSpellCast</c> and <c>BuffOnSpellCast</c> of every module bound to the match, not just the one whose
    /// slot holds the cast. Shaco's <c>Deceive</c> and Vayne's <c>VayneTumbleFade</c> end on it (their own shipped
    /// <c>BuffOnSpellCast</c>: Spells/Deceive.luaobj, Spells/VayneTumbleFade.luaobj), and both buffs sit on the
    /// champion module while a summoner cast runs on the summoner module's own <c>BbBuffRuntime</c>.
    /// </summary>
    void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target);
    void CancelChannel(Entity hero, string reason);
}

/// <summary>
/// Champion-specific cast boundary. Every champion has one explicitly
/// registered module; unregistered champions never receive fallback data.
/// </summary>
public interface IChampionSpellDispatcher
{
    Amberline.Simulation.Spells.Champions.IChampionSpellModule? FindModule(string championName);
    void TickSharedProjectilePhase(SpellSystem system, double dt);
    void OnUnitDeath(SpellSystem system, Entity unit);
    void OnHeroDeath(SpellSystem system, Entity unit, ChampionDeathPhase phase);
    void OnRecipientDeathCleanup(SpellSystem system, Entity unit);
    int RetireTargetedFlights(SpellSystem system, Entity hero, uint targetNetId, TargetedFlightRetirement reason);
    void OnBuffRemoved(SpellSystem system, Entity unit, BuffState buff);
    bool Handles(Entity hero);

    /// <summary>The champion script's CanCast gate for a slot (finding 32). A dispatcher without scripts keeps every slot open.</summary>
    bool ScriptCastGateOpen(Entity hero, int slot) => true;
    /// <summary>Returns the current source spell name for a slot.</summary>
    string SpellName(Entity hero, int slot);
    /// <summary>The reach the champion's spell script asks for this cast (<c>AdjustCastInfo</c>), or null for the spell's own.</summary>
    double? CastRange(Entity hero, int slot) => null;
    int MaxRank(Entity hero, int slot);
    int RankGate(Entity hero, int slot, int rank);
    SpellValidation Validate(SpellSystem system, Entity hero, int slot,
        uint targetNetId, Position3 position, double now, Position3? end = null);
    bool Execute(SpellSystem system, Entity hero, SpellCastRequest request, double now);
    /// <summary>Runs the champion source lifecycle phase once per match tick.</summary>
    void Tick(SpellSystem system, double dt);
    /// <summary>Runs one hero's source lifecycle at its documented server phase.</summary>
    void TickHero(SpellSystem system, Entity hero, double dt);
    /// <summary>Runs late champion work after buff lifecycle processing.</summary>
    void TickLate(SpellSystem system, double dt);
    ChampionTickPhase TickPhase(Entity hero);
    bool OwnsCooldown(Entity hero, int slot);
    /// <summary>Applies rank-dependent state changes after a shared rank-up.</summary>
    void OnRankUp(SpellSystem system, Entity hero, int slot, int rank);
}

public enum ChampionDeathPhase
{
    BeforeControllerBuffs,
    AfterControllerBuffs,
}

public enum TargetedFlightRetirement
{
    Untargetable,
    TargetDeath,
}

public enum ChampionTickPhase
{
    BeforeCooldowns,
    AfterCooldowns,
    Late,
}

/// <summary>
/// Common result of the shared cost/cooldown portion of a champion cast.
/// Champion modules use this after validation and own their effect timing and
/// cast packet details. <see cref="AtCastFrame"/>: the cost and the cooldown are not spent yet; the windup's cast frame spends them
/// (<see cref="SpellSystem.ChargeChampionCast"/>).
/// </summary>
public readonly record struct ChampionCastContext(
    SpellData Data,
    int Rank,
    double ManaCost,
    double Cooldown,
    bool AtCastFrame = false);

/// <summary>
/// Complete source snapshot needed by the deployed Orianna ball wire path.
/// It is deliberately separate from <see cref="Entity"/> because the ball is
/// a client placemarker and is not registered as a combat entity.
/// </summary>
public readonly record struct OriannaBallReplication(
    uint NetId,
    Position2 Position,
    int Team,
    float Hp,
    float AttackDamage,
    float Speed,
    float Armor,
    float SpellBlock,
    uint OwnerNetId = 0,
    int SkinId = 0);
