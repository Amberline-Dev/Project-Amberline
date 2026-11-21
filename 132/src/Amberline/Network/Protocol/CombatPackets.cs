using Amberline.Core;
using Amberline.Simulation.Combat;

namespace Amberline.Network.Protocol;

/// <summary>The command byte of 0xE5 (<see cref="CombatPackets.MusicCueCommand"/>); the AB build's enum MusicCueCommand_e names the same values.</summary>
public enum MusicCueAction : byte
{
    Prepare = 0,
    Begin = 1,
    End = 2,
}

public sealed record HeroStatsSnapshot(
    float AttackDamage = 0.0f,
    float Armor = 0.0f,
    float SpellBlock = 0.0f,
    float HpRegen = 0.0f,
    float MpRegen = 0.0f,
    float AttackRange = 0.0f,
    float AttackSpeedMod = 1.0f,
    float MaxHp = 0.0f,
    float MaxMp = 0.0f,
    float MoveSpeed = 0.0f,
    float? BaseAttackDamage = null,
    float Dodge = 0.0f,
    float CritChance = 0.0f,
    float BonusAttackDamage = 0.0f,
    float AbilityPower = 0.0f,
    float CooldownMod = 0.0f,
    float ArmorPenFlat = 0.0f,
    float ArmorPenPercent = 0.0f,
    float MagicPenFlat = 0.0f,
    float MagicPenPercent = 0.0f,
    float LifeSteal = 0.0f,
    float SpellVamp = 0.0f,
    uint ActionState = 7,
    uint CanCastBits1 = 0xFFFFFFFF,
    uint CanCastBits2 = 0xFFFFFFFF);

public sealed record MinionStatsSnapshot(
    float Hp,
    float AttackDamage,
    float Speed,
    float Armor = 0.0f,
    float SpellBlock = 0.0f,
    float AttackSpeedMod = 1.0f,
    float HpRegen = 0.0f,
    uint ActionState = 7,
    uint MagicImmune = 0,
    uint Invulnerable = 0,
    float CurrentMana = 0.0f,
    float MaxMana = 0.0f,
    float ParRegen = 0.0f,
    // mHP (LOCAL1 bit 0); null writes Hp, the maximum (mMaxHP, bit 2), for a unit at full health. Batch 701: a block for an injured unit
    // that wrote its maximum there drew the client's bar full (Tormented Soil's shred on a minion).
    float? CurrentHp = null);

public sealed record TurretStatsSnapshot(
    float Hp,
    float Damage,
    float? MaxHp = null,
    float Armor = 30.0f,
    float SpellBlock = 30.0f,
    float HpRegen = 0.0f,
    bool Protected = false,
    float AttackSpeedMod = 1.0f);

public readonly record struct BuildingStatsSnapshot(
    float Hp,
    uint SFlags,
    uint Invulnerable = 0,
    uint Targetable = 1);

public static class CombatPackets
{
    public const byte CombatChannel = LeagueProtocolConstants.CombatChannel;
    public const uint SFlagNontargetableEnemy = 0x01000000;
    public const uint SFlagNontargetableAll = 0x01800000;
    public const uint SFlagTargetableToAll = 0x02000000;

    // Reconstruction: 0x0D and 0x68 are the only unreliable packets (ENet flags None). The stock client ignores ENet delivery flags,
    // so the choice is not in the client; it follows the reference emulator's notifier (2026-07 channel work, docs/testing.md
    // "Packet-stream rules"). Every other packet of this server is reliable except the unsequenced 0x9D load info.
    public static PlaintextPacket BasicAttack(
        uint attackerNetId, uint targetNetId, uint missileId,
        byte slot = 0, double extraTimeSeconds = 0.0)
    {
        var extra = Math.Clamp((int)Math.Round(extraTimeSeconds * 100.0, MidpointRounding.ToEven) + 0x80, 0, 255);
        using var writer = ProtocolEncoding.Writer(15, out var stream);
        writer.Write((byte)0x0D);
        writer.Write(attackerNetId);
        writer.Write(targetNetId);
        writer.Write((byte)extra);
        writer.Write(missileId);
        writer.Write(slot);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Unreliable, Hero(attackerNetId), Target(targetNetId));
    }

    public const byte BasicAttackOpcode = 0x0D;
    public const byte BasicAttackPositionedOpcode = 0x1D;

    /// <summary>
    /// BasicAttackPos (0x1D on both lines, 23 bytes): the 0x0D's fields, then the attacker's x and z (f32 at 15 and 19). Evidence: the
    /// handler reads the two floats, sets the attacker's path position to that point
    ///, moves the unit there when it is more than 400 off, and then runs the 0x0D's
    /// own routine on the bytes from 5.
    /// </summary>
    public static PlaintextPacket BasicAttackAt(PlaintextPacket attack, Position2 attackerPosition)
    {
        ArgumentNullException.ThrowIfNull(attack);
        var bytes = attack.Plaintext.Span;
        if (bytes.Length != 15 || bytes[0] != BasicAttackOpcode) throw new ArgumentException("not a 0x0D", nameof(attack));
        using var writer = ProtocolEncoding.Writer(23, out var stream);
        writer.Write(BasicAttackPositionedOpcode);
        writer.Write(bytes[1..]);
        writer.Write(Float32(attackerPosition.X, nameof(attackerPosition)));
        writer.Write(Float32(attackerPosition.Z, nameof(attackerPosition)));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Unreliable, attack.EntityReferences.ToArray());
    }

    /// <summary>
    /// NpcInstantStopAttack flags (the stock client's instant stop of an attack): KeepAnimating 0x01, ForceSpellCast 0x02,
    /// ForceStop 0x04, AvatarSpell 0x08, DestroyMissile 0x10. The handler stops the unit's current SpellCaster, then ends the unit's
    /// attack state machine (HSM event 4, whatever the flags).
    /// ForceSpellCast makes the spellbook's StopSpellcastingObject force-stop the CURRENT SpellCaster, whatever it is, when its cast
    /// frame has not run yet (<c>SpellWasCast</c> reads mSpellCasted): the cast frame (the unit faces the target, RevealSpellCaster, then
    /// <c>DoCast_ClientAndServer</c>: hit particles and sound, or the missile's launch) runs once and the same Stop as without the flag
    /// follows. Once the frame has run the flag does nothing. The only other difference is that the forced stop cannot be refused by
    /// IsCancelBlocked, which a plain stop honours. For a spell that frame would show a spell the server never cast.
    /// DestroyMissile marks the SpellCaster's own missile, found by the missile ID of the 0x0D, to be removed, wherever that missile
    /// is: a shot already in flight vanishes without its impact, for as long as the SpellCaster lives (to the end of the attack period);
    /// for a spell the SpellCaster is the spell's and so is the missile.
    /// So ForceSpellCast + DestroyMissile (0x12) is wrong for every stop the server makes: before the hit it draws the hit of an
    /// attack the server dropped, after it it destroys a shot the server still applies (and, within the SpellCaster's life, the
    /// missile of a spell cast since). The builder has no default flags for that reason, and nothing sends 0x12. Use
    /// <see cref="CancelWindupFlags"/> where the server dropped the swing before its hit (no cast frame, no missile),
    /// <see cref="StandingSwingFlags"/> where the hit is the server's and the swing is provably the unit's current SpellCaster (a stop
    /// that reaches the client before the swing's cast frame still shows the hit the server applies), and
    /// <see cref="LandedAttackFlags"/> anywhere a spell may be current (nothing is forced, nothing destroyed);
    /// <see cref="StopAttack"/> picks.
    /// </summary>
    public const byte CancelWindupFlags = 0x10;
    /// <summary>
    /// ForceStop (0x04): the handler skips the spellbook's StopSpellcastingObject and finishes the unit's current SpellCaster directly
    /// (the spell caster's FinishCast), so <c>IsCancelBlocked</c> cannot refuse it. It is the stop a server makes to end a channel the
    /// unit's own client would keep (a stun, a lost target, a script's <c>BBStopChanneling</c>); without ForceSpellCast it runs no cast
    /// frame. Reconstruction: which stop the retail server sent is native and in no binary we hold.
    /// </summary>
    public const byte ForceStopFlags = 0x04;
    public const byte StandingSwingFlags = 0x02;
    public const byte LandedAttackFlags = 0x00;

    public static PlaintextPacket NpcInstantStopAttack(uint attackerNetId, byte flags)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0x39);
        writer.Write(attackerNetId);
        writer.Write(flags);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(attackerNetId));
    }

    /// <summary>
    /// The one place a unit's stop-attack (0x39) is decided: every sender (the effect sink, the neutral wire, the minion AI,
    /// the session detach) asks for it here and enqueues what comes back. Null means the unit's client has no attack to stop.
    /// It reads and clears the unit's client attack state (<see cref="Entity.ClientAttackArmed"/>, <see cref="Entity.ClientSwingDropped"/>,
    /// <see cref="Entity.ClientSwingMissileNetId"/>).
    /// <para>
    /// Whether: the stock client runs every non-hero unit (lane minion, jungle monster, pet, clone, ghoul) as an obj_AI_Minion,
    /// which starts its last attack again every attack period and calls StopActor (erasing the path being followed) from the
    /// unit's 0x0D until a 0x39, not only for the swing. So such a unit gets a stop exactly while it is armed, and never twice. A
    /// hero has no such repeat, but its stop also ends a channel or a cast in progress (recall, Monsoon, a stun), so a hero always
    /// gets the packet it was asked for.
    /// </para>
    /// <para>
    /// Whatever: DestroyMissile (<see cref="CancelWindupFlags"/>) when the server dropped a swing before its hit, so the client
    /// does not play it. When the hit is the server's its shot must fly on (retail missiles land; the combat queue only drops
    /// windups) and nothing is destroyed: ForceSpellCast (<see cref="StandingSwingFlags"/>) while the swing is still the unit's current
    /// SpellCaster (<see cref="Entity.ClientSwingMissileNetId"/>: the 0x0D set it, no cast, stop or cancelled hit has retired it),
    /// because a stop that reaches the client before the swing's cast frame would otherwise cancel the hit the server applies, and
    /// plain <see cref="LandedAttackFlags"/> once a cast has replaced the swing, because a forced cast frame of a spell would show
    /// a spell the server never cast (a channel, the recall, a spell's windup).
    /// </para>
    /// <para>
    /// Reconstruction: the retail server's native stop reasons (TurnOffAutoAttack: STOPREASON_TARGET_LOST, _MOVING, _IMMEDIATELY)
    /// and their flags are unrecovered; this follows what the stock client does with each flag.
    /// </para>
    /// </summary>
    public static PlaintextPacket? StopAttack(Entity unit, bool forceStop = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var send = unit.ClientAttackArmed || unit.UnitType == UnitType.Hero;
        var flags = forceStop ? ForceStopFlags
            : unit.ClientSwingDropped ? CancelWindupFlags
            : unit.ClientSwingMissileNetId != 0 ? StandingSwingFlags : LandedAttackFlags;
        unit.ClientAttackArmed = false;
        unit.ClientSwingDropped = false;
        unit.ClientSwingMissileNetId = 0;
        return send ? NpcInstantStopAttack(unit.NetId, flags) : null;
    }

    public static PlaintextPacket ApplyDamage(
        uint sourceNetId, uint targetNetId, float damage, byte typeByte = 0x84)
    {
        using var writer = ProtocolEncoding.Writer(18, out var stream);
        writer.Write((byte)0x69);
        writer.Write(sourceNetId);
        writer.Write(typeByte);
        writer.Write(targetNetId);
        writer.Write(sourceNetId);
        writer.Write(damage);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Unreliable, Hero(sourceNetId), Target(targetNetId));
    }

    public static PlaintextPacket UnitAddGold(
        uint killedNetId, uint heroNetId, float gold)
    {
        using var writer = ProtocolEncoding.Writer(17, out var stream);
        writer.Write((byte)0x25);
        writer.Write(killedNetId);
        writer.Write(heroNetId);
        writer.Write(killedNetId);
        writer.Write(gold);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Target(heroNetId), Hero(killedNetId));
    }

    public static PlaintextPacket NpcLevelUp(uint heroNetId, byte level, byte abilityPoints)
    {
        using var writer = ProtocolEncoding.Writer(7, out var stream);
        writer.Write((byte)0x46);
        writer.Write(heroNetId);
        writer.Write(level);
        writer.Write(abilityPoints);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public const byte UnitApplyHealOpcode = 0x85;

    /// <summary>
    /// UnitApplyHeal. The unit's heal routine fills a 13-byte packet: opcode, the healed unit's net ID, its maximum health
    /// (float at 5) and the requested heal amount (float at 9), and sends it through the unit's NetVisibilityObject after every heal
    /// that raised health. The send path is <c>TrySendLocalView</c>. The stock client registers the packet on every unit but its
    /// handler only returns true, so the packet has no client effect.
    /// </summary>
    public static PlaintextPacket UnitApplyHeal(uint unitNetId, float maxHealth, float heal)
    {
        if (!float.IsFinite(maxHealth) || !float.IsFinite(heal))
            throw new ArgumentOutOfRangeException(nameof(heal));
        using var writer = ProtocolEncoding.Writer(13, out var stream);
        writer.Write(UnitApplyHealOpcode);
        writer.Write(unitNetId);
        writer.Write(maxHealth);
        writer.Write(heal);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Target(unitNetId));
    }

    public static PlaintextPacket ReplicationHp(
        uint unitNetId, float currentHp, uint syncId = 1,
        bool isHero = false, bool isTurret = false)
    {
        var setMaps = (byte)(isTurret ? 0x02 : isHero ? 0x08 : 0x02);
        var propertyMask = isTurret ? 0x100u : 0x01u;
        using var writer = ReplicationWriter(23, out var stream, syncId, unitNetId, setMaps, propertyMask);
        writer.Write(currentHp);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    /// <summary>
    /// Batch 479 (Reconstruction): a unit's PAR pool, the LOCAL1 fields mMP (bit 1) and mMaxMP (bit 3) of the minion registry,
    /// 27 bytes. The replication tables are in docs/network/patch126-replication-tables.md. MinionStats writes the same two values at creation.
    /// </summary>
    public static PlaintextPacket ReplicationPar(uint unitNetId, float currentMana, float maxMana, uint syncId = 1)
    {
        using var writer = ReplicationWriter(27, out var stream, syncId, unitNetId, 0x02, 0x0Au);
        writer.Write(currentMana);
        writer.Write(maxMana);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    /// <summary>132 UnitTargetableStatusChanged (new in 132, appended to the packet enum).</summary>
    public const byte UnitTargetableStatusOpcode = 0xF4;

    /// <summary>
    /// Patch 132: a unit's targetability, <c>[0xF4][net id u32][IsTargetable u8][IsTargetableToTeam u32]</c>, 10 bytes. The 132 client
    /// replicates IsTargetable and IsTargetableToTeam no more (removed from every class's LOCAL1 table: 132 <c>_AddVar</c> the client, the
    /// shared flag helper the client); its handler (registered on every AttackableUnit) stores +5 as IsTargetable and
    /// +6 as IsTargetableToTeam (<c>sflags_e</c>: <c>SPELL_NonTargetableAlly</c> 0x800000, <c>SPELL_NonTargetableEnemy</c> 0x1000000, both
    /// 0x1800000, <c>SPELL_TargetableToAll</c> 0x2000000; the client compares for exact equality and reads any other value as targetable).
    /// The spawn packets set the state too (0x81 flags bit 3, 0xA6 +0x4A bit 0: the client's SetTargetable the client, which also writes
    /// 0x2000000 or 0x1800000 over the packet's IsTargetableToTeam), so the server sends this packet after a creation and on every change
    /// where it sent the replicated fields before; <c>ClientReplicationStore</c> drops a repeat of the state a client already holds.
    /// </summary>
    public static PlaintextPacket UnitTargetableStatus(uint netId, bool targetable, uint targetableToTeam)
    {
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write(UnitTargetableStatusOpcode);
        writer.Write(netId);
        writer.Write((byte)(targetable ? 1 : 0));
        writer.Write(targetableToTeam);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary><see cref="UnitTargetableStatus(uint, bool, uint)"/> with the not-targetable-to-team flags a client is told (<see cref="WireNotTargetable"/>).</summary>
    public static PlaintextPacket UnitTargetableStatus(Entity unit, bool targetable)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var (toAlly, toEnemy) = WireNotTargetable(unit);
        return UnitTargetableStatus(unit.NetId, targetable, TargetableToTeamFlags(toAlly, toEnemy));
    }

    /// <summary>
    /// The unit's targetability as its own state decides it, for a sender that changes only the team flags (a script's
    /// <c>BBSetNotTargetableToTeam</c>, a capture point's guardian, a late observer), with the IsTargetable its stat blocks gave in 126: a scene
    /// turret is targetable while alive and not a fountain shrine, an inhibitor or nexus always, a champion as its stat snapshot says
    /// (<see cref="Amberline.Simulation.Stats.HeroStatsService.TargetableOf"/>), any other unit unless a buff makes it untargetable
    /// (<see cref="WireTargetable"/>).
    /// </summary>
    public static PlaintextPacket UnitTargetableStatus(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var targetable = unit.IsSceneTurret ? unit.Alive && !TurretRules.IsShrineTurret(unit)
            : unit.Kind is EntityKind.Inhibitor or EntityKind.Nexus || unit.UnitType == UnitType.Structure ||
              (unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0
                  ? Amberline.Simulation.Stats.HeroStatsService.TargetableOf(unit) != 0
                  : WireTargetable(unit, unit.AnyBuffOrStatus(buff => buff.Payload.Untargetable) ? 0u : 1u) != 0);
        return UnitTargetableStatus(unit, targetable);
    }

    /// <summary>
    /// A scene turret's targetability as its 126 stat and protection blocks carried it: untargetable to both teams when it is not targetable
    /// (dead, or a fountain shrine), untargetable to the enemy while protected, else targetable to all.
    /// </summary>
    public static PlaintextPacket TurretTargetableStatus(uint netId, bool targetable, bool isProtected) =>
        UnitTargetableStatus(netId, targetable,
            !targetable ? SFlagNontargetableAll : isProtected ? SFlagNontargetableEnemy : SFlagTargetableToAll);

    /// <summary>The <c>sflags_e</c> value of <c>IsTargetableToTeam</c> for the teams that may not target the unit.</summary>
    public static uint TargetableToTeamFlags(bool notToAlly, bool notToEnemy) =>
        notToAlly || notToEnemy ? (notToAlly ? 0x00800000u : 0u) | (notToEnemy ? 0x01000000u : 0u) : SFlagTargetableToAll;

    /// <summary>
    /// Batch 503 (Reconstruction; the owner, 31 October 2026: "Shrines did not show HP bars to anyone in the original game, but Garrison
    /// worked"): the capture point guardian. The stock client hands a spell click only a unit whose targetable check passes (IsTargetable
    /// and IsTargetableToTeam other than 0x1800000), and the minion bar draw tests that same check. Its other per-unit gate is the current
    /// health: no bar below 0.01. Nothing else on an alive minion reads it: the pick, the spell target check, the use test and the mouse-over
    /// do not, the client's health invariant check only asks 0 &lt;= current &lt;= max, and a death is the server's packet. So a client is told
    /// the guardian's health is 0 (its pool is its PAR; it is invulnerable on every stats update, OdinGuardianBuff), and that it is not
    /// targetable to the enemy of its team (0x1000000) rather than to both: the targetable check holds, so the point can be clicked, and
    /// the enemy's attack order is still refused by the right-click handler. The server keeps the shipped state (encounter Targetable 0,
    /// OdinTowerBehavior's SetUnitTargetableState false): it refuses every attack, and Garrison passes through its AffectUseable (batch 502).
    /// Retail's own mechanism for the hidden bar is not recovered.
    /// Batch 516: the zero health is one rule of the replication layer (<c>ClientReplicationStore.WithoutHealth</c>: every 0xCC and 0xB5 a
    /// client is sent for a unit it created as this character), not of each packet builder.
    /// </summary>
    public static bool HidesBarByHealth(string? character) => character is "OdinNeutralGuardian" or "Urf";

    /// <summary>
    /// Batch 513 (NOT RETAIL; owner finding, 31 October 2026: the Autumn rift's Ghost of Urf shows no tooltip on hover, and it should).
    /// The text is shipped: <c>Urf.inibin</c> <c>Data*EnemyTooltip</c> = <c>game_character_enemyTooltip_Urf</c> ("The Ghost of Urf",
    /// fontconfig_en_US.txt:1946), which the client's tooltip takes for any viewer not on the unit's team, and team 300 passes the minion
    /// gate of the hover tooltip. What stops it is the hover itself: the hovered unit is the world pick, whose candidates are the units the
    /// hero's selected spell, its basic attack by default, may target, and the spell's valid-target check refuses a unit whose targetable
    /// check fails unless the spell has AffectUntargetable 0x200. The shipped CharScriptUrf CharOnActivate sets SetTargetable false and
    /// nothing restores it, so the stock client can never hover the ghost. Each client is therefore told the ghost is targetable
    /// (<see cref="WireTargetable"/>) and, like the guardian above, has 0 health so no bar is drawn over it. The server keeps the shipped
    /// state (untargetable and invulnerable): every attack order and spell on it is refused.
    /// </summary>
    public static bool ShowsOwnTooltip(Entity unit) => unit.Kind == EntityKind.Neutral && unit.Name == "Urf";

    /// <summary>The IsTargetable a client is told: 1 for a unit that <see cref="ShowsOwnTooltip"/>, else the unit's own.</summary>
    public static uint WireTargetable(Entity unit, uint targetable) => ShowsOwnTooltip(unit) ? 1u : targetable;

    /// <inheritdoc cref="HidesBarByHealth(string?)"/>
    public static bool HidesBarByHealth(Entity unit) => HidesBarByHealth(unit.ChampionName);

    /// <summary>The not-targetable flags a client is told (<see cref="HidesBarByHealth(Entity)"/>: untargetable to both becomes untargetable to the enemy).</summary>
    public static (bool ToAlly, bool ToEnemy) WireNotTargetable(Entity unit) =>
        HidesBarByHealth(unit) && unit.NotTargetableToAlly && unit.NotTargetableToEnemy
            ? (false, true)
            : (unit.NotTargetableToAlly, unit.NotTargetableToEnemy);

    /// <summary>
    /// The unit's model scale coefficient (<c>mScaleSkinCoef</c>): MAP bit 8 of a hero (7 in 126; 132 registers mExp at MAP bit 4) and bit 3 of
    /// every other class (registered under the name
    /// "mCrit", the duplicate-name quirk of <c>docs/network/patch126-replication-tables.md</c>), a float the client multiplies into the model's
    /// <c>SkinScale</c>; never 0. 23 bytes.
    /// </summary>
    public static PlaintextPacket UnitModelScale(uint netId, float scale, bool isHero, uint syncId = 1)
    {
        if (!float.IsFinite(scale) || scale <= 0.0f) throw new ArgumentOutOfRangeException(nameof(scale));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x08, isHero ? 0x100u : 0x08u);
        writer.Write(scale);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// The owner's gold alone (CLIENT_ONLY bit 0 in 132: mExp left CLIENT_ONLY for MAP bit 4; bit 1 in 126). Gold is replicated when the whole
    /// value the HUD draws moves, so every source of income sends this delta instead of the full hero stat block.
    /// </summary>
    public static PlaintextPacket HeroGold(
        uint heroNetId, float gold, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(23, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x01, 0x01);
        writer.Write(gold);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    /// <summary>
    /// Gold and experience in one block: 132 gold is CLIENT_ONLY bit 0 and mExp MAP bit 4 (132 hero registration the client adds mExp right
    /// after mMaxMP), so the block sets both registries, 31 bytes (126: CLIENT_ONLY bits 0 and 1, 27 bytes).
    /// </summary>
    public static PlaintextPacket HeroGoldExp(
        uint heroNetId, float gold, float exp, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(31, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x09, 0x01);
        writer.Write(gold);
        writer.Write(0x10u);
        writer.Write(exp);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket HeroNeutralKills(uint heroNetId, int kills, uint syncId = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(kills);
        // Stock hero registration puts mNumNeutralMinionsKilled immediately
        // after mLevelRef in MAP_REPDATA: map 3, bit 10 in 132 (9 in 126, before mExp joined MAP at bit 4), a 32-bit integer.
        using var writer = ReplicationWriter(23, out var stream, syncId, heroNetId, 0x08, 0x0400);
        writer.Write(kills);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket HeroStats(
        uint heroNetId,
        HeroStatsSnapshot stats,
        uint syncId = 1,
        float? currentHp = null,
        float? currentMp = null,
        uint level = 1,
        float gold = 0.0f,
        float exp = 0.0f,
        IReadOnlyList<float>? manaCosts = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var costs = manaCosts?.ToArray() ?? new float[4];
        if (costs.Length != 4) throw new ArgumentException("Exactly four mana costs are required.", nameof(manaCosts));
        if (stats.AttackSpeedMod <= 0.0f || !float.IsFinite(stats.AttackSpeedMod))
            throw new ArgumentOutOfRangeException(nameof(stats), "AttackSpeedMod must be positive and finite.");
        // Patch 132 hero tables: CLIENT_ONLY 0 mGold, 1-2 mCanCastBits, 3-6 the mana costs; LOCAL1 without
        // IsTargetable and IsTargetableToTeam (0xF4 carries them, HeroTargetableStatus), so every field after IsInvulnerable is two bits
        // lower; MAP with mExp at bit 4, after mMaxMP. 163 bytes (126: 171).
        using var writer = ProtocolEncoding.Writer(163, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x0B, 0x7F);
        writer.Write(gold);
        writer.Write(stats.CanCastBits1);
        writer.Write(stats.CanCastBits2);
        foreach (var cost in costs) writer.Write(cost);

        writer.Write(0x07F4FFD1u);
        writer.Write(stats.ActionState);
        writer.Write(stats.BaseAttackDamage ?? stats.AttackDamage);
        writer.Write(stats.Dodge);
        writer.Write(stats.CritChance);
        writer.Write(stats.Armor);
        writer.Write(stats.SpellBlock);
        writer.Write(stats.HpRegen);
        writer.Write(stats.MpRegen);
        writer.Write(stats.AttackRange);
        writer.Write(stats.BonusAttackDamage);
        writer.Write(0.0f);
        writer.Write(stats.AbilityPower);
        writer.Write(stats.AttackSpeedMod);
        writer.Write(stats.CooldownMod);
        writer.Write(stats.ArmorPenFlat);
        writer.Write(stats.ArmorPenPercent);
        writer.Write(stats.MagicPenFlat);
        writer.Write(stats.MagicPenPercent);
        writer.Write(stats.LifeSteal);
        writer.Write(stats.SpellVamp);

        writer.Write(0x029Fu);
        writer.Write(currentHp ?? stats.MaxHp);
        writer.Write(currentMp ?? stats.MaxMp);
        writer.Write(stats.MaxHp);
        writer.Write(stats.MaxMp);
        writer.Write(exp);
        writer.Write(stats.MoveSpeed);
        writer.Write(level);
        writer.Flush();
        if (stream.Length != 163) throw new InvalidOperationException($"Hero stats must be 163 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket HeroStats(
        Amberline.Simulation.Stats.HeroStatsReplicationSnapshot snapshot,
        uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var effective = snapshot.Effective;
        var typed = new HeroStatsSnapshot(
            Float32(effective.AttackDamage, nameof(snapshot)),
            Float32(effective.Armor, nameof(snapshot)),
            Float32(effective.SpellBlock, nameof(snapshot)),
            Float32(effective.HpRegen, nameof(snapshot)),
            Float32(effective.MpRegen, nameof(snapshot)),
            Float32(effective.AttackRange, nameof(snapshot)),
            Float32(effective.AttackSpeedMod, nameof(snapshot)),
            Float32(effective.MaxHp, nameof(snapshot)),
            Float32(effective.MaxMp, nameof(snapshot)),
            Float32(effective.MoveSpeed, nameof(snapshot)),
            Float32(snapshot.BaseAttackDamage, nameof(snapshot)),
            Float32(effective.Dodge, nameof(snapshot)),
            Float32(effective.CritChance, nameof(snapshot)),
            Float32(snapshot.BonusAttackDamage, nameof(snapshot)),
            Float32(effective.AbilityPower, nameof(snapshot)),
            Float32(effective.CooldownMod, nameof(snapshot)),
            Float32(effective.ArmorPenFlat, nameof(snapshot)),
            Float32(1.0 - effective.ArmorPenPercent, nameof(snapshot)),
            Float32(effective.MagicPenFlat, nameof(snapshot)),
            Float32(1.0 - effective.MagicPenPercent, nameof(snapshot)),
            Float32(effective.LifeSteal, nameof(snapshot)),
            Float32(effective.SpellVamp, nameof(snapshot)),
            unchecked((uint)snapshot.ActionState),
            snapshot.CanCastBits1, snapshot.CanCastBits2);
        return HeroStats(snapshot.HeroNetId, typed, syncId,
            Float32(snapshot.CurrentHealth, nameof(snapshot)),
            Float32(snapshot.CurrentMana, nameof(snapshot)), unchecked((uint)snapshot.Level),
            Float32(snapshot.Gold, nameof(snapshot)), Float32(snapshot.Experience, nameof(snapshot)),
            snapshot.ManaCosts.Select(cost => Float32(cost, nameof(snapshot))).ToArray());
    }

    /// <summary>The hero's targetability (132 0xF4) that goes with its stat block: the snapshot's IsTargetable and team flags.</summary>
    public static PlaintextPacket HeroTargetableStatus(Amberline.Simulation.Stats.HeroStatsReplicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return UnitTargetableStatus(snapshot.HeroNetId, snapshot.IsTargetable != 0, snapshot.TargetableToTeam);
    }

    public static PlaintextPacket MinionStats(
        uint netId, MinionStatsSnapshot stats, uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(stats);
        // LOCAL1 of a minion has 19 entries in 132 (bits 0-18; 21 in 126): the client's variable registration drops a repeated name, so the
        // registration's second mPercentPhysicalDamageMod and second mPARRegenRate take no slot (until 18 October 2026 the mask had a value
        // for each duplicate), and 132 registers IsTargetable and IsTargetableToTeam no more (0xF4 carries them, UnitTargetableStatus).
        using var writer = ProtocolEncoding.Writer(103, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x0A, 0x7FFFF);
        writer.Write(stats.CurrentHp ?? stats.Hp);
        writer.Write(stats.CurrentMana);
        writer.Write(stats.Hp);
        writer.Write(stats.MaxMana);
        writer.Write(stats.ActionState);
        writer.Write(stats.MagicImmune);
        writer.Write(stats.Invulnerable);
        writer.Write(0u); // 7 IsPhysicalImmune
        writer.Write(stats.AttackDamage);
        writer.Write(stats.Armor);
        writer.Write(stats.SpellBlock);
        writer.Write(stats.AttackSpeedMod);
        writer.Write(0.0f); // 12 mFlatPhysicalDamageMod
        // 13 mPercentPhysicalDamageMod: retail's neutral value is 0, the client constructor's default. Every client read adds 1 to it, the
        // target bar shows floor((percent + 1) * (mBaseAttackDamage + flat) + 0.5); 1.0 here drew every selected minion
        // and turret at double its attack damage (batch 752). The server's damage comes from its own stats, not this slot.
        writer.Write(0.0f);
        writer.Write(0.0f); // 14 mFlatMagicDamageMod
        writer.Write(stats.HpRegen); // 15 mHPRegenRate
        writer.Write(stats.ParRegen); // 16 mPARRegenRate: the record's BaseStaticMPRegen (batch 329; camp monsters 0.5)
        writer.Write(0.0f); // 17 mFlatMagicReduction
        writer.Write(0.0f); // 18 mPercentMagicReduction
        writer.Write(0x04u);
        writer.Write(stats.Speed);
        writer.Flush();
        if (stream.Length != 103) throw new InvalidOperationException($"Minion stats must be 103 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// Publishes only the native minion CharacterState ActionState field.
    /// The no-render flag (mNoRender) is bit 13 of this value. In the minion
    /// registration order ActionState is map 1/property bit 4, after the
    /// four HP/MP values; selecting bit 0 would decode the value as HP.
    /// </summary>
    public static PlaintextPacket MinionActionState(
        uint netId, uint actionState, uint syncId = 1)
    {
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x02, 0x10);
        writer.Write(actionState);
        writer.Flush();
        if (stream.Length != 23)
            throw new InvalidOperationException($"Minion action state must be 23 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// The narrow combat block of a minion whose script changed a combat status: ActionState (map 1 property bit 4) with MagicImmune and
    /// Invulnerable (bits 5 and 6) in one block. No HP is written. Patch 132: IsTargetable (126 bit 7) is no replicated field; the caller sends
    /// it as 0xF4 (<see cref="UnitTargetableStatus(Entity, bool)"/>).
    /// Reconstruction (batch 359): the retail server's block for these flags is not recovered; the bits follow the minion registration order
    /// (map 1, property bits 4 to 6), the same order the full block uses.
    /// </summary>
    public static PlaintextPacket MinionCombatState(
        uint netId, uint actionState, uint magicImmune, uint invulnerable, uint syncId = 1)
    {
        using var writer = ReplicationWriter(31, out var stream, syncId, netId, 0x02, 0x70);
        writer.Write(actionState);
        writer.Write(magicImmune);
        writer.Write(invulnerable);
        writer.Flush();
        if (stream.Length != 31)
            throw new InvalidOperationException($"Minion combat state must be 31 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Native minion map 1/property 11 (13 in 126, before IsTargetable and IsTargetableToTeam left the table); leaves HP, max HP and action state intact.</summary>
    public static PlaintextPacket MinionAttackSpeed(uint netId, float modifier, uint syncId = 1)
    {
        if (!float.IsFinite(modifier) || modifier <= 0) throw new ArgumentOutOfRangeException(nameof(modifier));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x02, 0x800);
        writer.Write(modifier);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Refreshes current HP and attack speed on NPC re-entry without replacing max HP.</summary>
    public static PlaintextPacket MinionCurrentStats(
        uint netId, float currentHp, float attackSpeed, uint syncId = 1)
    {
        if (!float.IsFinite(attackSpeed) || attackSpeed <= 0) throw new ArgumentOutOfRangeException(nameof(attackSpeed));
        using var writer = ReplicationWriter(27, out var stream, syncId, netId, 0x02, 0x801); // mHP (0) and mAttackSpeedMod (132: 11)
        writer.Write(currentHp);
        writer.Write(attackSpeed);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Publishes only the NPC movement field on the verified movement map.</summary>
    public static PlaintextPacket MinionMovementSpeed(
        uint netId, float speed, uint syncId = 1)
    {
        if (!float.IsFinite(speed) || speed < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(speed));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x08, 0x04);
        writer.Write(speed);
        writer.Flush();
        if (stream.Length != 23)
            throw new InvalidOperationException($"Minion movement speed must be 23 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Minion movement map: additive percent perception radius modifier precedes move speed.</summary>
    public static PlaintextPacket MinionVisionPercentModifier(uint netId, float modifier, uint syncId = 1)
    {
        if (!float.IsFinite(modifier) || modifier < -1) throw new ArgumentOutOfRangeException(nameof(modifier));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x08, 0x02);
        writer.Write(modifier);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// The unit's sight radius modifiers (<c>mFlatBubbleRadiusMod</c> and <c>mPercentBubbleRadiusMod</c>; the client's
    /// <c>(percent + 1) * base + flat</c>): MAP bits 5 and 6 of a hero (4 and 5 in 126, before mExp took MAP bit 4), bits 0 and 1 of every
    /// other unit, 27 bytes with both values.
    /// The tables are in docs/network/patch126-replication-tables.md.
    /// </summary>
    public static PlaintextPacket UnitBubbleRadiusMods(uint netId, float flat, float percent, bool isHero, uint syncId = 1)
    {
        if (!float.IsFinite(flat) || !float.IsFinite(percent)) throw new ArgumentOutOfRangeException(nameof(flat));
        using var writer = ReplicationWriter(27, out var stream, syncId, netId, 0x08, isHero ? 0x60u : 0x03u);
        writer.Write(flat);
        writer.Write(percent);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    public static PlaintextPacket MinionStats(
        uint netId, float hp, float attackDamage, float speed,
        float armor = 0.0f, float spellBlock = 0.0f,
        float attackSpeedMod = 1.0f, float hpRegen = 0.0f,
        uint actionState = 7, uint magicImmune = 0, uint invulnerable = 0,
        uint syncId = 1)
        => MinionStats(netId, new MinionStatsSnapshot(hp, attackDamage, speed, armor,
            spellBlock, attackSpeedMod, hpRegen, actionState, magicImmune,
            invulnerable), syncId);

    public static PlaintextPacket TurretStats(
        uint netId, TurretStatsSnapshot stats, uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(stats);
        // 132 turret LOCAL1: 14 entries, IsTargetable and IsTargetableToTeam gone (126: 16; the callers send 0xF4).
        using var writer = ProtocolEncoding.Writer(83, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x0A, 0x3FFF);
        writer.Write(stats.Damage);
        writer.Write(stats.Armor);
        writer.Write(stats.SpellBlock);
        writer.Write(stats.AttackSpeedMod);
        writer.Write(0.0f); // 4 mFlatPhysicalDamageMod
        writer.Write(0.0f); // 5 mPercentPhysicalDamageMod: neutral 0, see MinionStats (batch 752)
        writer.Write(0.0f); // 6 mFlatMagicDamageMod
        writer.Write(stats.HpRegen);
        writer.Write(stats.Hp);
        writer.Write(stats.MaxHp ?? stats.Hp);
        writer.Write(7u); // 10 ActionState
        writer.Write(0u); // 11 MagicImmune
        writer.Write(stats.Protected ? 1u : 0u); // 12 IsInvulnerable
        writer.Write(0u); // 13 IsPhysicalImmune
        writer.Write(0x04u);
        writer.Write(0.0f);
        writer.Flush();
        if (stream.Length != 83) throw new InvalidOperationException($"Turret stats must be 83 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    public static PlaintextPacket TurretStats(
        uint netId, float hp, float damage, float? maxHp = null,
        float armor = 30.0f, float spellBlock = 30.0f, float hpRegen = 0.0f,
        bool protectedState = false, uint syncId = 1)
        => TurretStats(netId, new TurretStatsSnapshot(hp, damage, maxHp, armor,
            spellBlock, hpRegen, protectedState), syncId);

    /// <summary>
    /// A turret's protection: IsInvulnerable (turret LOCAL1 bit 12, a 23-byte block) and its targetability (0xF4). Patch 132 splits what
    /// was one 126 block (LOCAL1 bits 12-14: invulnerable, IsTargetable, IsTargetableToTeam), because 132 no longer replicates the two
    /// targetable fields.
    /// </summary>
    public static PlaintextPacket[] TurretProtectionFlags(
        uint unitNetId, uint sflagsValue, uint syncId = 1, uint invulnerable = 0, uint targetable = 1)
    {
        using var writer = ProtocolEncoding.Writer(23, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, unitNetId, 0x02, 0x1000);
        writer.Write(invulnerable);
        writer.Flush();
        return [Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId)),
            UnitTargetableStatus(unitNetId, targetable != 0, sflagsValue)];
    }

    /// <summary>
    /// A building's state (nexus, inhibitor): 132 building LOCAL1 is mHP and IsInvulnerable (bits 0 and 1, a 27-byte
    /// block), and the targetability goes as 0xF4 (126: one block of four fields, IsTargetable and IsTargetableToTeam at bits 2 and 3).
    /// </summary>
    public static PlaintextPacket[] BuildingStats(
        uint netId, BuildingStatsSnapshot stats, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(27, out var stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x02, 0x03);
        writer.Write(stats.Hp);
        writer.Write(stats.Invulnerable);
        writer.Flush();
        return [Packet(stream, LeaguePacketReliability.Reliable, Unit(netId)),
            UnitTargetableStatus(netId, stats.Targetable != 0, stats.SFlags)];
    }

    public static PlaintextPacket[] BuildingStats(
        uint netId, float hp, uint sflags, uint invulnerable = 0,
        uint targetable = 1, uint syncId = 1)
        => BuildingStats(netId, new BuildingStatsSnapshot(hp, sflags,
            invulnerable, targetable), syncId);

    public static PlaintextPacket DampenerState(
        uint netId, float respawnSeconds, bool dead)
    {
        var seconds = Math.Clamp((int)Math.Round(respawnSeconds, MidpointRounding.ToEven), 0, 0x7FFF);
        using var writer = ProtocolEncoding.Writer(8, out var stream);
        writer.Write((byte)0x2D);
        writer.Write(netId);
        writer.Write((ushort)seconds);
        writer.Write((byte)(dead ? 0 : 1));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    public static PlaintextPacket AiSetTarget(uint unitNetId, uint targetNetId)
        => SimplePair(0x6E, unitNetId, targetNetId);

    public static PlaintextPacket AiSetTargetHero(uint unitNetId, uint targetNetId)
        => SimplePair(0xC9, unitNetId, targetNetId);

    public static PlaintextPacket NpcDie(
        uint dyingNetId, uint killerNetId, float deathDuration = 8.0f,
        byte becomeZombie = 0, byte deathType = 0, byte damageType = 0, byte opcode = 0xA7)
    {
        using var writer = ProtocolEncoding.Writer(16, out var stream);
        writer.Write(opcode);
        writer.Write(dyingNetId);
        writer.Write(killerNetId == 0 ? dyingNetId : killerNetId);
        writer.Write(deathType);
        writer.Write(damageType);
        writer.Write(deathDuration);
        writer.Write(becomeZombie);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(dyingNetId));
    }

    /// <summary>
    /// NpcDieEventHistory. The fixed header is 0x11 bytes long; each ParamsDamage history record is the six-byte event prefix plus
    /// the 0x29 bytes its event's serializer reads, for 0x2f bytes per record. Batch 859: the event is <see cref="DamageGivenEvent"/>;
    /// the 132 reader (DeathRecap) copies the records and walks them by the size each event's serializer returns.
    /// </summary>
    public static PlaintextPacket DeathRecap(
        DeathRecapSnapshot snapshot,
        IReadOnlyList<DeathRecapRecord> records)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(records);
        if (snapshot.KillerNetId == 0)
            throw new ArgumentException("Death recap requires a resolved killer.", nameof(snapshot));
        if (records.Count == 0 || records.Count > 128)
            throw new ArgumentOutOfRangeException(nameof(records));
        if (snapshot.KillerSourceType > DeathRecapSourceType.Clone)
            throw new ArgumentException("Death recap requires a resolved killer source type.", nameof(snapshot));
        foreach (var record in records)
        {
            if (record.VictimNetId != snapshot.VictimNetId || record.AttackerNetId == 0 ||
                !double.IsFinite(record.Timestamp) || record.Timestamp < 0.0 ||
                !double.IsFinite(record.PhysicalDamage) || record.PhysicalDamage < 0.0 ||
                !double.IsFinite(record.MagicalDamage) || record.MagicalDamage < 0.0 ||
                !double.IsFinite(record.TrueDamage) || record.TrueDamage < 0.0 ||
                record.TotalDamage <= 0.0 || !record.Origin.IsSafe)
                throw new ArgumentException("Death recap contains an unsafe damage record.", nameof(records));
        }

        const int recordBytes = 0x2F;
        var serializedBytes = checked(records.Count * recordBytes);
        using var writer = ProtocolEncoding.Writer(0x19 + serializedBytes, out var stream);
        writer.Write((byte)0x24);
        writer.Write(snapshot.VictimNetId);
        writer.Write(snapshot.KillerNetId);
        writer.Write(ProtocolEncoding.Float32(snapshot.TimeWindow, nameof(snapshot)));
        writer.Write((byte)((byte)snapshot.KillerSourceType & 0x0F));
        writer.Write(new byte[3]);
        writer.Write(serializedBytes);
        writer.Write(records.Count);
        foreach (var record in records)
        {
            writer.Write(ProtocolEncoding.Float32(record.Timestamp, nameof(record)));
            // The stock reader copies this two-byte EventData count before
            // dispatching the event-index byte. One event occupies the row.
            writer.Write((ushort)1);
            writer.Write(DamageGivenEvent);
            writer.Write(record.AttackerNetId); // EventData.mSource
            writer.Write(record.VictimNetId);
            writer.Write(ProtocolEncoding.Float32(record.PhysicalDamage, nameof(record)));
            writer.Write(ProtocolEncoding.Float32(record.MagicalDamage, nameof(record)));
            writer.Write(ProtocolEncoding.Float32(record.TrueDamage, nameof(record)));
            writer.Write((uint)record.Origin.EventSource);
            writer.Write(record.Origin.SourceScriptNameHash);
            writer.Write(record.Origin.ParentScriptNameHash);
            writer.Write(record.Origin.ParentCasterNetId);
            var packed = ((uint)record.Origin.ParentEventSource & 0x0F) |
                (((uint)record.Origin.ParentTeam & 0x0F) << 4) |
                (((uint)record.Origin.ParentSourceType & 0x0F) << 8) |
                (((uint)record.Origin.SourceSpellLevel & 0x07) << 12);
            writer.Write(packed);
        }
        writer.Flush();
        if (stream.Length != 0x19 + serializedBytes)
            throw new InvalidOperationException("Death recap packet size mismatch.");
        var references = records
            .SelectMany(record => new[]
            {
                new LeaguePacketEntityReference(LeaguePacketEntityRole.World, record.AttackerNetId),
                new LeaguePacketEntityReference(LeaguePacketEntityRole.World,
                    record.Origin.ParentCasterNetId),
            })
            .Append(new LeaguePacketEntityReference(
                LeaguePacketEntityRole.World, snapshot.VictimNetId))
            .Append(new LeaguePacketEntityReference(
                LeaguePacketEntityRole.World, snapshot.KillerNetId))
            .Where(reference => reference.NetId != 0)
            .Distinct()
            .ToArray();
        return Packet(stream, LeaguePacketReliability.Reliable, references);
    }

    /// <summary>
    /// NpcForceDead (0x1E), 9 bytes in 132 (126: 5): a new float at byte 5, which the 132 handler stores on the unit before
    /// it calls the death routine; the server-side builder compiled into the client grew from 5 to 9 bytes.
    /// Batch 833: the float is the hero's DeathDuration. lol132.exe's handler writes it at hero +0x2BF8 (the debug dump
    /// The client prints that field as "DeathDuration"; its getter the client is vtable slot 0x21C), then calls slot 0x258, whose hero
    /// override stamps the death time (+0x29C0) with the game time. The HUD (the client "setRespawnTime";
    /// The client "DeathTimer_%d") shows ceil(DeathDuration - (now - stamp)), so the value is the time left to the respawn at the packet
    /// (MatchGameplayHooks.EndZombieLife). The hero death packet (0x62, NpcDie +11) sets the same field.
    /// </summary>
    public static PlaintextPacket ForceDead(uint unitNetId, float value = 0.0f)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x1E);
        writer.Write(unitNetId);
        writer.Write(value);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    /// <summary>
    /// ShowHealthBar (<c>0xD7</c>, six bytes; <c>0xD9</c> in the AntiBot build, whose <c>BBShowHealthBar</c> body builds it): the unit's
    /// network id and <c>show</c> at +5. The unit's receiver stores <c>show == false</c> in its hide-health-bar flag; <c>BBShowHealthBar</c> broadcasts it.
    /// </summary>
    public static PlaintextPacket ShowHealthBar(uint unitNetId, bool show)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0xD8);
        writer.Write(unitNetId);
        writer.Write((byte)(show ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    public static PlaintextPacket BuildingDie(uint buildingNetId, uint killerNetId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x8F);
        writer.Write(buildingNetId);
        writer.Write(killerNetId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(buildingNetId), Target(killerNetId));
    }

    /// <summary>Stock entity BaseParams event; no position or reveal payload.</summary>
    public static PlaintextPacket HeroMultiKill(uint heroNetId, uint victimNetId, int count)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0xAC);
        writer.Write(heroNetId);
        // Stock HeroEventManager registers Double through Unreal; in 132 they are events 7..11 (126: 4..8, ChampionDoubleKillEvent).
        writer.Write((byte)(ChampionDoubleKillEvent + Math.Min(count, 6) - 2));
        writer.Write(victimNetId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId), Target(victimNetId));
    }

    /// <summary>EventDetailsList id 2, <c>OnChampionDie</c>.</summary>
    public const byte ChampionDieEvent = 2;

    /// <summary>
    /// Batch 859 (owner, 17 November 2026: "Getting executed by minions or jungle camps... causes a bugsplat"): EventDetailsList id 14,
    /// <c>OnDamageGiven</c>, the event of every death recap record. The server wrote 126's id 11, which batch 780's insertions (3, 5, 6)
    /// made <c>OnChampionUnrealKill</c> in 132 (initializer: "OnChampionUnrealKill" with EventFunctions&lt;BaseParams&gt;
    /// "OnDamageGiven" with EventFunctions&lt;ParamsDamage&gt;). BaseParams' reader takes 9 bytes where ParamsDamage's
    /// takes 0x29, so the client walked each later record from inside the one before: a lone record showed "amount 0", and
    /// what several did depends on their bytes. A six-record minion recap ran past the copied buffer (the checked index:
    /// _invalid_parameter_noinfo, the process ends with 0xC000000D and no BugSplat; twice on a throwaway client); replaying the walk
    /// over a headless seven-record minion recap reaches event id 194, past the 110-entry table (an access violation, which BugSplat
    /// reports; inferred); two-record camp recaps replay to the end with garbage rows. Executions by a wave or a camp send several
    /// records, one champion hit or one turret shot from low health one. 126 keeps 11, its own OnDamageGiven.
    /// </summary>
    public const byte DamageGivenEvent = 14;

    /// <summary>
    /// Batch 690 (community report, "top right kill counter shows 0 vs 0"): the champion death event, 0xAB on the dying champion with
    /// <c>OnChampionDie</c> (id 2) and its <c>ParamsDie</c>. The HUD's kill counter and
    /// the kill callout run only from the global listener of event 2 (registered beside events 0, 0x38 and 0x22), and no client code
    /// raises event 2 itself (every Notify call site names another id), so only the server's event packet reaches them. The listener reads the
    /// source as the victim and <c>ParamsDie</c>'s first field as the killer: the side whose champion died adds one to
    /// the other side's kills, when the killer is a champion of another team. <c>ParamsDie</c> (AB type) is the other net ID, <c>mGoldGiven</c>,
    /// <c>mAssistCount</c> (the client asserts below 12) and <c>mAssists[12]</c>, which the callout's assist icons read. The hero's own dispatcher
    /// has no event 2 listener (its OnDie, OnChampionKill, multi-kill and spell listeners), so nothing counts twice. Reconstruction: retail's
    /// packet size is not in the client (it hands the raw bytes from offset 6 to the listeners); the whole 60-byte struct is sent so the client
    /// never reads past the packet.
    /// </summary>
    public static PlaintextPacket ChampionDie(uint victimNetId, uint killerNetId, float goldGiven, IReadOnlyList<uint> assists)
    {
        if (assists.Count > 11) throw new ArgumentOutOfRangeException(nameof(assists));
        using var writer = ProtocolEncoding.Writer(66, out var stream);
        writer.Write((byte)0xAC);
        writer.Write(victimNetId);
        writer.Write(ChampionDieEvent);
        writer.Write(killerNetId == 0 ? victimNetId : killerNetId);
        writer.Write(goldGiven);
        writer.Write((uint)assists.Count);
        for (var slot = 0; slot < 12; slot++) writer.Write(slot < assists.Count ? assists[slot] : 0u);
        writer.Flush();
        // The references are the catalog row's (offsets 1 and 6); the assists are heroes, which every client holds.
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(victimNetId), Target(killerNetId == 0 ? victimNetId : killerNetId));
    }

    /// <summary>
    /// Batch 455: the stock client's EventDetailsList ids (one table entry per id, filled by the static initializer); the event byte of
    /// 0x4B and 0xAC indexes it, and the announcer finds the event's Announcements_*.ini section by the entry's name.
    /// Batch 780 (crash of the owner's first native 132 game, 14 November 2026): the 1.0.0.132 list (initializer, table
    ///, 20 bytes per id, 110 ids; 126: 99 ids) inserts OnChampionKillPre (3), OnChampionKillPost (5),
    /// OnChampionSingleKill (6), OnKilledUnitOnKillingSpreeDoubleKill to UnrealKill (56 to 60) and OnStartGameMessage3 to 5 (89 to 91), so
    /// every id from 3 up moved: 126 OnStartGameMessage2 (80) is 132 OnSurrenderFailedVotes, whose chat line "Surrender vote failed for
    /// @Team@..." read a team past the end of the 18-byte packet and crashed the client. The ids below are the
    /// 132 list's: 86 OnMinionsSpawn, 87 OnStartGameMessage1 and 88 OnStartGameMessage2 (Dominion's texts are Map8 keys), 96 + n
    /// OnCapturePointNeutralized_A + n and 101 + n OnCapturePointCaptured_A + n, n = point - 1 (point 1 to 5 is A to E).
    /// </summary>
    public const byte MinionsSpawn = 86;
    public const byte StartGameMessage1 = 87;
    public const byte StartGameMessage2 = 88;

    /// <summary>Batch 780: 132 OnChampionDoubleKill; Triple, Quadra, Penta and Unreal follow it (126: 4 to 8).</summary>
    public const byte ChampionDoubleKillEvent = 7;

    /// <summary>
    /// Batch 471: OnVictoryPointThreshold4 (132: 109, 126: 98). On Dominion it is the quest announcement: Announcements_ODIN.ini:743-754
    /// gives it the text game_announcement_OnVictoryPointThreshold4_@src your team | enemy team@ ("New Capture Point Objective!",
    /// fontconfig_en_US.txt:721-724) and the announcer's Odin "Quest_QuestReceived" line, so it needs a source unit of the team.
    /// </summary>
    public const byte QuestReceived = 109;

    /// <summary>
    /// Batch 505: OnVictoryPointThreshold1..3 (132: 106 to 108, 126: 95 to 97), the nexus health lines: Announcements_ODIN.ini:704-742
    /// (sounds _75StructureDamage, _50SeverelyDamaged, _25Unstable) and fontconfig_en_US.txt:709-720 ("... has 375 / 250 / 125 nexus health
    /// remaining!"). Threshold <c>n</c> is <c>VictoryPointThreshold1 + n - 1</c>.
    /// </summary>
    public const byte VictoryPointThreshold1 = 106;

    public static byte CapturePointEventId(bool captured, int capturePoint)
    {
        if (capturePoint is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(capturePoint));
        return (byte)((captured ? 101 : 96) + capturePoint - 1);
    }

    public static PlaintextPacket OnEventWorld(
        byte eventId, uint sourceNetId, uint otherNetId = 0, int? mapNumber = null)
    {
        using var writer = ProtocolEncoding.Writer(mapNumber.HasValue ? 18 : 14, out var stream);
        writer.Write((byte)0x4B);
        writer.Write(0u);
        writer.Write(eventId);
        writer.Write(sourceNetId);
        writer.Write(otherNetId);
        if (mapNumber.HasValue) writer.Write(mapNumber.Value);
        writer.Flush();
        var refs = new List<LeaguePacketEntityReference>();
        if (sourceNetId != 0) refs.Add(World(sourceNetId));
        if (otherNetId != 0) refs.Add(Target(otherNetId));
        return Packet(stream, LeaguePacketReliability.Reliable, refs.ToArray());
    }

    /// <summary>
    /// HeroReincarnate (0x4E in 132, 0x4D in 126): the hero and its respawn point. The client passes the location on as it is. The duel
    /// implementation notes are in docs/network/duel-implementation.md, so batch 460 sends the ground height there instead of 0 (about -35 in The Crystal Scar's
    /// bases, about 175 in Summoner's Rift's fountains).
    /// Patch 132: 21 bytes, a new float at byte 0x11. The 132 respawn sets the health to its maximum and then hands
    /// this float to a clamp routine, which stores it limited to [0, the field's maximum] in the replicated value the respawn resets; by that
    /// place it is the PAR (126's respawn set the PAR to its maximum itself). Reconstruction (meaning inferred): the server sends
    /// the hero's PAR after its respawn.
    /// </summary>
    public static PlaintextPacket HeroReincarnate(uint heroNetId, Position3 position, float par)
    {
        using var writer = ProtocolEncoding.Writer(21, out var stream);
        writer.Write((byte)0x4E);
        writer.Write(heroNetId);
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
        writer.Write(par);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    /// <summary>
    /// MoveCameraToPoint: <c>bStartAtCurrentCameraPosition</c>, the start, the target and the travel time. Without a
    /// <paramref name="start"/> the pan starts at the current camera (EndOfGame's <c>MoveCameraFromCurrentPositionToPoint</c>); batch
    /// 563's TutorialScript <c>MoveCameraToPoint(hero, from, to, seconds)</c> names its own start.
    /// </summary>
    public static PlaintextPacket MoveCameraToPoint(
        uint heroNetId, Position3 position, float travelTime = 2.0f, Position3? start = null)
    {
        using var writer = ProtocolEncoding.Writer(34, out var stream);
        writer.Write((byte)0x27);
        writer.Write(heroNetId);
        writer.Write((byte)(start is null ? 1 : 0));
        WritePosition(writer, start ?? new Position3(0.0, 0.0, 0.0));
        WritePosition(writer, position);
        writer.Write(travelTime);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket DisableHudForEndOfGame()
        => PacketBytes(new byte[] { 0xC5, 0, 0, 0, 0 });

    public static PlaintextPacket SetGreyscaleEnabledWhenDead(uint heroNetId, bool enabled)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0xB3);
        writer.Write(heroNetId);
        writer.Write((byte)(enabled ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket SetInputLockingFlag(uint flag, bool locked)
    {
        // The native callback switches on an individual flag, not a combined mask.
        if (flag == 0 || flag > 128 || (flag & (flag - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(flag));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x8A);
        writer.Write(0u);
        writer.Write(flag);
        writer.Write(locked);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket FadeOutMainSfx(float duration)
    {
        if (!float.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x63);
        writer.Write(0u);
        writer.Write(duration);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    /// <summary>
    /// MusicCueCommand (<c>0xE5</c>, 10 bytes): <c>[0xE5][hero netid u32][command u8][cue id u32]</c> (batch 747).
    /// Proven in the 126 client: the hero constructor builds the packet's register on the hero (a key helper
    /// writes opcode 0xE5; the id the client routes by is the hero's net id), and the handler (a virtual call)
    /// reads only the command at byte 5 and the cue at byte 6, never the hero: any hero the client holds plays it.
    /// The client's routine maps the command to the audio manager's PrepareMusicCue, BeginMusicCue or EndMusicCue (three virtual calls);
    /// any other value is ignored. The cue id is an FMOD music cue of <c>DATA/Sounds/FMOD/LoL_Audio.fev</c>.
    /// </summary>
    public static PlaintextPacket MusicCueCommand(uint heroNetId, MusicCueAction command, uint cueId)
    {
        if (heroNetId == 0) throw new ArgumentOutOfRangeException(nameof(heroNetId), "the client routes 0xE5 to a hero");
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0xE6);
        writer.Write(heroNetId);
        writer.Write((byte)command);
        writer.Write(cueId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket FadeMinions(Team team, float amount, float duration)
    {
        if (team is not (Team.Order or Team.Chaos)) throw new ArgumentOutOfRangeException(nameof(team));
        if (!float.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!float.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        using var writer = ProtocolEncoding.Writer(14, out var stream);
        writer.Write((byte)0xD5);
        writer.Write(0u);
        writer.Write(team == Team.Order ? (byte)100 : (byte)200);
        writer.Write(amount);
        writer.Write(duration);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket SetNexusSkin(Team team, uint skinId)
    {
        if (team is not (Team.Order or Team.Chaos)) throw new ArgumentOutOfRangeException(nameof(team));
        if (skinId > 1) throw new ArgumentOutOfRangeException(nameof(skinId));
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write((byte)0x78);
        writer.Write(0u);
        writer.Write(team == Team.Order ? (byte)100 : (byte)200);
        writer.Write(skinId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket EndOfGameEvent(bool winnerIsOrder)
        => PacketBytes(new byte[] { 0xCE, 0, 0, 0, 0, (byte)(winnerIsOrder ? 1 : 0) });

    public static PlaintextPacket EndGame(bool winnerIsOrder, bool surrender = false)
        => PacketBytes(new byte[] { 0xCF, 0, 0, 0, 0,
            (byte)((winnerIsOrder ? 1 : 0) | (surrender ? 2 : 0)) });

    private static BinaryWriter ReplicationWriter(
        int capacity, out MemoryStream stream, uint syncId, uint unitNetId,
        byte setMaps, uint propertyMask)
    {
        var writer = ProtocolEncoding.Writer(capacity, out stream);
        writer.Write((byte)0xCD);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, unitNetId, setMaps, propertyMask);
        return writer;
    }

    private static void WriteReplicationPrefix(BinaryWriter writer, uint syncId,
        uint unitNetId, byte setMaps, uint propertyMask)
    {
        writer.Write(syncId);
        writer.Write((byte)1);
        writer.Write(setMaps);
        writer.Write(unitNetId);
        writer.Write(propertyMask);
    }

    /// <summary>
    /// Reads the sync id, unit and per-channel property masks of a 0xCC packet
    /// that replicates exactly one unit (the layout WriteReplicationPrefix
    /// writes). <paramref name="masks"/> holds one mask per replication
    /// channel, zero for channels the packet does not set.
    /// </summary>
    public static bool TryReadReplicationBlock(
        ReadOnlySpan<byte> bytes, out uint syncId, out uint unitNetId, Span<uint> masks)
    {
        syncId = unitNetId = 0;
        masks.Clear();
        if (bytes.Length < 15 || bytes[0] != 0xCD || bytes[9] != 1 ||
            (bytes[10] & 0xE0) != 0 || masks.Length < 5) return false;
        syncId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
        unitNetId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]);
        var offset = 15;
        for (var map = 0; map < 5; map++)
        {
            if ((bytes[10] & (1 << map)) == 0) continue;
            if (offset + 4 > bytes.Length) return false;
            masks[map] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            offset += 4 + 4 * System.Numerics.BitOperations.PopCount(masks[map]);
            if (offset > bytes.Length) return false;
        }
        return true;
    }

    private static PlaintextPacket SimplePair(byte opcode, uint unitNetId, uint targetNetId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write(opcode);
        writer.Write(unitNetId);
        writer.Write(targetNetId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId), Target(targetNetId));
    }

    private static PlaintextPacket PacketBytes(byte[] bytes)
        => ProtocolEncoding.Packet(CombatChannel, bytes, encrypt: true, LeaguePacketReliability.Reliable);

    private static PlaintextPacket Packet(
        MemoryStream stream, LeaguePacketReliability reliability,
        params LeaguePacketEntityReference[] references)
        => ProtocolEncoding.Packet(CombatChannel, ProtocolEncoding.Finish(stream), encrypt: true, reliability,
            references.Where(reference => reference.NetId != 0).ToArray());

    private static void WritePosition(BinaryWriter writer, Position3 position)
    {
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
    }

    private static LeaguePacketEntityReference Hero(uint id) => new(LeaguePacketEntityRole.Hero, id);
    private static LeaguePacketEntityReference Target(uint id) => new(LeaguePacketEntityRole.Target, id);
    private static LeaguePacketEntityReference Unit(uint id) => new(LeaguePacketEntityRole.World, id);
    private static LeaguePacketEntityReference World(uint id) => new(LeaguePacketEntityRole.World, id);

    private static float Float32(double value, string parameterName)
        => ProtocolEncoding.Float32(value, parameterName);
}
