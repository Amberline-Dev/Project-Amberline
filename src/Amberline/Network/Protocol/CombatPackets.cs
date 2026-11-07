using Amberline.Core;
using Amberline.Simulation.Combat;

namespace Amberline.Network.Protocol;

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
    uint IsTargetable = 1,
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
    uint Targetable = 1,
    float CurrentMana = 0.0f,
    float MaxMana = 0.0f,
    float ParRegen = 0.0f,
    uint TargetableToTeam = 1,
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
    float AttackSpeedMod = 1.0f,
    bool Targetable = true);

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
        writer.Write((byte)0x68);
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
        writer.Write((byte)0x45);
        writer.Write(heroNetId);
        writer.Write(level);
        writer.Write(abilityPoints);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public const byte UnitApplyHealOpcode = 0x84;

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

    /// <summary>
    /// The unit's <c>IsTargetableToTeam</c> flags (<c>sflags_e</c>: <c>SPELL_NonTargetableAlly</c> 0x800000, <c>SPELL_NonTargetableEnemy</c> 0x1000000,
    /// <c>SPELL_TargetableToAll</c> 0x2000000): LOCAL1 bit 4 of a hero, bit 8 of a minion, 23 bytes. The tables are in docs/network/patch126-replication-tables.md.
    /// </summary>
    public static PlaintextPacket UnitTargetableToTeam(uint netId, bool notToAlly, bool notToEnemy, bool isHero, uint syncId = 1) =>
        TargetableToTeamBlock(netId, TargetableToTeamFlags(notToAlly, notToEnemy), isHero ? 0x10u : 0x100u, syncId);

    private static PlaintextPacket TargetableToTeamBlock(uint netId, uint flags, uint mask, uint syncId)
    {
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x02, mask);
        writer.Write(flags);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// Batch 671: the LOCAL1 field of <c>IsTargetableToTeam</c> in the unit's own class table:
    /// bit 4 of a hero, 14 of a scene turret, 3 of a building (inhibitor, nexus), 8 of a minion (and of a point turret, a minion on the client).
    /// Every non-hero took bit 8, which is a turret's mHP: the tutorial's SetTargetable false turret was sent 0x1800000 as its health on each
    /// reveal (an empty bar), and Nunu's first hit sent the real health back (the owner's finding of 3 November 2026, "no HP and full heals").
    /// </summary>
    private static uint TargetableToTeamMask(Entity unit, bool isHero) =>
        isHero ? 0x10u :
        unit.IsSceneTurret ? 0x4000u :
        unit.Kind is EntityKind.Inhibitor or EntityKind.Nexus || unit.UnitType == UnitType.Structure ? 0x08u : 0x100u;

    /// <summary>The <c>sflags_e</c> value of <c>IsTargetableToTeam</c> for the teams that may not target the unit.</summary>
    public static uint TargetableToTeamFlags(bool notToAlly, bool notToEnemy) =>
        notToAlly || notToEnemy ? (notToAlly ? 0x00800000u : 0u) | (notToEnemy ? 0x01000000u : 0u) : SFlagTargetableToAll;

    /// <summary>
    /// Batch 495: the IsTargetableToTeam value of a minion's full stat block (LOCAL1 bit 8, <see cref="MinionStats(uint, MinionStatsSnapshot, uint)"/>).
    /// The block wrote a raw 1 there for every unit, so each full block sent after a <c>BBSetNotTargetableToTeam</c> or an untargetable encounter
    /// unit's 0x1800000 (<see cref="UnitTargetableToTeam"/>) made the unit targetable to both teams again on the client. A unit with no flag keeps
    /// the raw 1. Any value other than the four sflags constants reads as targetable; the tables are in docs/network/patch126-replication-tables.md.
    /// </summary>
    public static uint MinionTargetableToTeam(Entity unit)
    {
        var (toAlly, toEnemy) = WireNotTargetable(unit);
        return toAlly || toEnemy ? TargetableToTeamFlags(toAlly, toEnemy) : 1u;
    }

    /// <summary><see cref="UnitTargetableToTeam(uint, bool, bool, bool, uint)"/> with the flags a client is told (<see cref="WireNotTargetable"/>).</summary>
    public static PlaintextPacket UnitTargetableToTeam(Entity unit, bool isHero, uint syncId)
    {
        var (toAlly, toEnemy) = WireNotTargetable(unit);
        return TargetableToTeamBlock(unit.NetId, TargetableToTeamFlags(toAlly, toEnemy), TargetableToTeamMask(unit, isHero), syncId);
    }

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
    /// The unit's model scale coefficient (<c>mScaleSkinCoef</c>): MAP bit 7 of a hero and bit 3 of every other class (registered under the name
    /// "mCrit", the duplicate-name quirk of <c>docs/network/patch126-replication-tables.md</c>), a float the client multiplies into the model's
    /// <c>SkinScale</c>; never 0. 23 bytes.
    /// </summary>
    public static PlaintextPacket UnitModelScale(uint netId, float scale, bool isHero, uint syncId = 1)
    {
        if (!float.IsFinite(scale) || scale <= 0.0f) throw new ArgumentOutOfRangeException(nameof(scale));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x08, isHero ? 0x80u : 0x08u);
        writer.Write(scale);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>
    /// The owner's gold alone (CLIENT_ONLY bit 1). Gold is replicated when the whole value the HUD draws moves, so
    /// every source of income sends this delta instead of the full hero stat block.
    /// </summary>
    public static PlaintextPacket HeroGold(
        uint heroNetId, float gold, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(23, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x01, 0x02);
        writer.Write(gold);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket HeroGoldExp(
        uint heroNetId, float gold, float exp, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(27, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x01, 0x03);
        writer.Write(exp);
        writer.Write(gold);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket HeroNeutralKills(uint heroNetId, int kills, uint syncId = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(kills);
        // Stock hero registration puts mNumNeutralMinionsKilled immediately
        // after mLevelRef in MAP_REPDATA: map 3, bit 9, a 32-bit integer.
        using var writer = ReplicationWriter(23, out var stream, syncId, heroNetId, 0x08, 0x0200);
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
        using var writer = ProtocolEncoding.Writer(171, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, heroNetId, 0x0B, 0xFF);
        writer.Write(exp);
        writer.Write(gold);
        writer.Write(stats.CanCastBits1);
        writer.Write(stats.CanCastBits2);
        foreach (var cost in costs) writer.Write(cost);

        writer.Write(0x1FD3FF59u);
        writer.Write(stats.ActionState);
        writer.Write(stats.IsTargetable);
        writer.Write(1u);
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

        writer.Write(0x014Fu);
        writer.Write(currentHp ?? stats.MaxHp);
        writer.Write(currentMp ?? stats.MaxMp);
        writer.Write(stats.MaxHp);
        writer.Write(stats.MaxMp);
        writer.Write(stats.MoveSpeed);
        writer.Write(level);
        writer.Flush();
        if (stream.Length != 171) throw new InvalidOperationException($"Hero stats must be 171 bytes, got {stream.Length}.");
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
            unchecked((uint)snapshot.ActionState), unchecked((uint)snapshot.IsTargetable),
            snapshot.CanCastBits1, snapshot.CanCastBits2);
        return HeroStats(snapshot.HeroNetId, typed, syncId,
            Float32(snapshot.CurrentHealth, nameof(snapshot)),
            Float32(snapshot.CurrentMana, nameof(snapshot)), unchecked((uint)snapshot.Level),
            Float32(snapshot.Gold, nameof(snapshot)), Float32(snapshot.Experience, nameof(snapshot)),
            snapshot.ManaCosts.Select(cost => Float32(cost, nameof(snapshot))).ToArray());
    }

    public static PlaintextPacket MinionStats(
        uint netId, MinionStatsSnapshot stats, uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(stats);
        // LOCAL1 of a minion has 21 entries (bits 0-20): the client's variable registration drops a repeated name, so the
        // registration's second mPercentPhysicalDamageMod and second mPARRegenRate take no slot. Until 18 October 2026 the mask had
        // 23 bits with a value for each duplicate, which put 1.0 into mHPRegenRate and the HP regen into mPARRegenRate.
        using var writer = ProtocolEncoding.Writer(111, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x0A, 0x1FFFFF);
        writer.Write(stats.CurrentHp ?? stats.Hp);
        writer.Write(stats.CurrentMana);
        writer.Write(stats.Hp);
        writer.Write(stats.MaxMana);
        writer.Write(stats.ActionState);
        writer.Write(stats.MagicImmune);
        writer.Write(stats.Invulnerable);
        writer.Write(stats.Targetable);
        writer.Write(stats.TargetableToTeam); // 8 IsTargetableToTeam (MinionTargetableToTeam)
        writer.Write(0u);
        writer.Write(stats.AttackDamage);
        writer.Write(stats.Armor);
        writer.Write(stats.SpellBlock);
        writer.Write(stats.AttackSpeedMod);
        writer.Write(0.0f); // 14 mFlatPhysicalDamageMod
        writer.Write(1.0f); // 15 mPercentPhysicalDamageMod
        writer.Write(0.0f); // 16 mFlatMagicDamageMod
        writer.Write(stats.HpRegen); // 17 mHPRegenRate
        writer.Write(stats.ParRegen); // 18 mPARRegenRate: the record's BaseStaticMPRegen (batch 329; camp monsters 0.5)
        writer.Write(0.0f); // 19 mFlatMagicReduction
        writer.Write(0.0f); // 20 mPercentMagicReduction
        writer.Write(0x04u);
        writer.Write(stats.Speed);
        writer.Flush();
        if (stream.Length != 111) throw new InvalidOperationException($"Minion stats must be 111 bytes, got {stream.Length}.");
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
    /// The narrow combat block of a minion whose script changed a combat status: ActionState (map 1 property bit 4) with MagicImmune,
    /// Invulnerable and Targetable (bits 5 to 7) in one block. No HP is written.
    /// Reconstruction (batch 359): the retail server's block for these flags is not recovered; the bits follow the minion registration order
    /// (map 1, property bits 4 to 7), the same order the full block uses.
    /// </summary>
    public static PlaintextPacket MinionCombatState(
        uint netId, uint actionState, uint magicImmune, uint invulnerable, uint targetable, uint syncId = 1)
    {
        using var writer = ReplicationWriter(35, out var stream, syncId, netId, 0x02, 0xF0);
        writer.Write(actionState);
        writer.Write(magicImmune);
        writer.Write(invulnerable);
        writer.Write(targetable);
        writer.Flush();
        if (stream.Length != 35)
            throw new InvalidOperationException($"Minion combat state must be 35 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Native minion map 1/property 13; leaves HP, max HP and action state intact.</summary>
    public static PlaintextPacket MinionAttackSpeed(uint netId, float modifier, uint syncId = 1)
    {
        if (!float.IsFinite(modifier) || modifier <= 0) throw new ArgumentOutOfRangeException(nameof(modifier));
        using var writer = ReplicationWriter(23, out var stream, syncId, netId, 0x02, 0x2000);
        writer.Write(modifier);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    /// <summary>Refreshes current HP and attack speed on NPC re-entry without replacing max HP.</summary>
    public static PlaintextPacket MinionCurrentStats(
        uint netId, float currentHp, float attackSpeed, uint syncId = 1)
    {
        if (!float.IsFinite(attackSpeed) || attackSpeed <= 0) throw new ArgumentOutOfRangeException(nameof(attackSpeed));
        using var writer = ReplicationWriter(27, out var stream, syncId, netId, 0x02, 0x2001);
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
    /// <c>(percent + 1) * base + flat</c>): MAP bits 4 and 5 of a hero, bits 0 and 1 of every other unit, 27 bytes with both values.
    /// The tables are in docs/network/patch126-replication-tables.md.
    /// </summary>
    public static PlaintextPacket UnitBubbleRadiusMods(uint netId, float flat, float percent, bool isHero, uint syncId = 1)
    {
        if (!float.IsFinite(flat) || !float.IsFinite(percent)) throw new ArgumentOutOfRangeException(nameof(flat));
        using var writer = ReplicationWriter(27, out var stream, syncId, netId, 0x08, isHero ? 0x30u : 0x03u);
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
        uint targetable = 1, uint syncId = 1)
        => MinionStats(netId, new MinionStatsSnapshot(hp, attackDamage, speed, armor,
            spellBlock, attackSpeedMod, hpRegen, actionState, magicImmune,
            invulnerable, targetable), syncId);

    public static PlaintextPacket TurretStats(
        uint netId, TurretStatsSnapshot stats, uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(stats);
        using var writer = ProtocolEncoding.Writer(91, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x0A, 0xFFFF);
        writer.Write(stats.Damage);
        writer.Write(stats.Armor);
        writer.Write(stats.SpellBlock);
        writer.Write(stats.AttackSpeedMod);
        writer.Write(0.0f);
        writer.Write(1.0f);
        writer.Write(0.0f);
        writer.Write(stats.HpRegen);
        writer.Write(stats.Hp);
        writer.Write(stats.MaxHp ?? stats.Hp);
        writer.Write(7u);
        writer.Write(0u);
        writer.Write(stats.Protected ? 1u : 0u);
        writer.Write(stats.Targetable ? 1u : 0u);
        writer.Write(!stats.Targetable ? SFlagNontargetableAll :
            stats.Protected ? SFlagNontargetableEnemy : SFlagTargetableToAll);
        writer.Write(0u);
        writer.Write(0x04u);
        writer.Write(0.0f);
        writer.Flush();
        if (stream.Length != 91) throw new InvalidOperationException($"Turret stats must be 91 bytes, got {stream.Length}.");
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    public static PlaintextPacket TurretStats(
        uint netId, float hp, float damage, float? maxHp = null,
        float armor = 30.0f, float spellBlock = 30.0f, float hpRegen = 0.0f,
        bool protectedState = false, uint syncId = 1)
        => TurretStats(netId, new TurretStatsSnapshot(hp, damage, maxHp, armor,
            spellBlock, hpRegen, protectedState), syncId);

    public static PlaintextPacket TurretProtectionFlags(
        uint unitNetId, uint sflagsValue, uint syncId = 1, uint invulnerable = 0, uint targetable = 1)
    {
        using var writer = ProtocolEncoding.Writer(31, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, unitNetId, 0x02, 0x7000);
        writer.Write(invulnerable);
        writer.Write(targetable);
        writer.Write(sflagsValue);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    public static PlaintextPacket BuildingStats(
        uint netId, BuildingStatsSnapshot stats, uint syncId = 1)
    {
        using var writer = ProtocolEncoding.Writer(35, out var stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        WriteReplicationPrefix(writer, syncId, netId, 0x02, 0x0F);
        writer.Write(stats.Hp);
        writer.Write(stats.Invulnerable);
        writer.Write(stats.Targetable);
        writer.Write(stats.SFlags);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(netId));
    }

    public static PlaintextPacket BuildingStats(
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
        => SimplePair(0x6D, unitNetId, targetNetId);

    public static PlaintextPacket AiSetTargetHero(uint unitNetId, uint targetNetId)
        => SimplePair(0xC8, unitNetId, targetNetId);

    public static PlaintextPacket NpcDie(
        uint dyingNetId, uint killerNetId, float deathDuration = 8.0f,
        byte becomeZombie = 0, byte deathType = 0, byte damageType = 0, byte opcode = 0xA6)
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
    /// Patch-126 NpcDieEventHistory. The fixed header is 0x11 bytes long;
    /// each ParamsDamage history record is the six-byte event prefix plus its
    /// 0x29-byte event-11 serializer payload, for 0x2f bytes per record.
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
            writer.Write((byte)11); // common event table: OnDamageGiven
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

    public static PlaintextPacket ForceDead(uint unitNetId)
    {
        using var writer = ProtocolEncoding.Writer(5, out var stream);
        writer.Write((byte)0x1E);
        writer.Write(unitNetId);
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
        writer.Write((byte)0xD7);
        writer.Write(unitNetId);
        writer.Write((byte)(show ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Unit(unitNetId));
    }

    public static PlaintextPacket BuildingDie(uint buildingNetId, uint killerNetId)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write((byte)0x8E);
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
        writer.Write((byte)0xAB);
        writer.Write(heroNetId);
        // Stock HeroEventManager registers Double through Unreal as events 4..8.
        writer.Write((byte)(Math.Min(count, 6) + 2));
        writer.Write(victimNetId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId), Target(victimNetId));
    }

    /// <summary>EventDetailsList id 2, <c>OnChampionDie</c>.</summary>
    public const byte ChampionDieEvent = 2;

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
        writer.Write((byte)0xAB);
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
    /// Batch 455: the stock client's EventDetailsList ids (one table entry per id, filled by the static initializer):
    /// 79 OnStartGameMessage1 and 80 OnStartGameMessage2 (Dominion's texts are Map8 keys), 85 + n OnCapturePointNeutralized_A + n and
    /// 90 + n OnCapturePointCaptured_A + n, n = point - 1 (point 1 to 5 is A to E).
    /// </summary>
    public const byte StartGameMessage1 = 79;
    public const byte StartGameMessage2 = 80;

    /// <summary>
    /// Batch 471: 98 OnVictoryPointThreshold4. On Dominion it is the quest announcement: Announcements_ODIN.ini:743-754 gives it the text
    /// game_announcement_OnVictoryPointThreshold4_@src your team | enemy team@ ("New Capture Point Objective!", fontconfig_en_US.txt:721-724)
    /// and the announcer's Odin "Quest_QuestReceived" line, so it needs a source unit of the team.
    /// </summary>
    public const byte QuestReceived = 98;

    /// <summary>
    /// Batch 505: 95 to 97 OnVictoryPointThreshold1..3 (batch 455's id table), the nexus health lines: Announcements_ODIN.ini:704-742
    /// (sounds _75StructureDamage, _50SeverelyDamaged, _25Unstable) and fontconfig_en_US.txt:709-720 ("... has 375 / 250 / 125 nexus health
    /// remaining!"). Threshold <c>n</c> is <c>VictoryPointThreshold1 + n - 1</c>.
    /// </summary>
    public const byte VictoryPointThreshold1 = 95;

    public static byte CapturePointEventId(bool captured, int capturePoint)
    {
        if (capturePoint is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(capturePoint));
        return (byte)((captured ? 90 : 85) + capturePoint - 1);
    }

    public static PlaintextPacket OnEventWorld(
        byte eventId, uint sourceNetId, uint otherNetId = 0, int? mapNumber = null)
    {
        using var writer = ProtocolEncoding.Writer(mapNumber.HasValue ? 18 : 14, out var stream);
        writer.Write((byte)0x4A);
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
    /// HeroReincarnate (0x4D): the hero and its respawn point. The client passes the location on as it is. The duel
    /// implementation notes are in docs/network/duel-implementation.md, so batch 460 sends the ground height there instead of 0 (about -35 in The Crystal Scar's
    /// bases, about 175 in Summoner's Rift's fountains).
    /// </summary>
    public static PlaintextPacket HeroReincarnate(uint heroNetId, Position3 position)
    {
        using var writer = ProtocolEncoding.Writer(17, out var stream);
        writer.Write((byte)0x4D);
        writer.Write(heroNetId);
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
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
        => PacketBytes(new byte[] { 0xC4, 0, 0, 0, 0 });

    public static PlaintextPacket SetGreyscaleEnabledWhenDead(uint heroNetId, bool enabled)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0xB2);
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
        writer.Write((byte)0x89);
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
        writer.Write((byte)0x62);
        writer.Write(0u);
        writer.Write(duration);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket FadeMinions(Team team, float amount, float duration)
    {
        if (team is not (Team.Order or Team.Chaos)) throw new ArgumentOutOfRangeException(nameof(team));
        if (!float.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!float.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        using var writer = ProtocolEncoding.Writer(14, out var stream);
        writer.Write((byte)0xD4);
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
        writer.Write((byte)0x77);
        writer.Write(0u);
        writer.Write(team == Team.Order ? (byte)100 : (byte)200);
        writer.Write(skinId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable);
    }

    public static PlaintextPacket EndOfGameEvent(bool winnerIsOrder)
        => PacketBytes(new byte[] { 0xCD, 0, 0, 0, 0, (byte)(winnerIsOrder ? 1 : 0) });

    public static PlaintextPacket EndGame(bool winnerIsOrder, bool surrender = false)
        => PacketBytes(new byte[] { 0xCE, 0, 0, 0, 0,
            (byte)((winnerIsOrder ? 1 : 0) | (surrender ? 2 : 0)) });

    private static BinaryWriter ReplicationWriter(
        int capacity, out MemoryStream stream, uint syncId, uint unitNetId,
        byte setMaps, uint propertyMask)
    {
        var writer = ProtocolEncoding.Writer(capacity, out stream);
        writer.Write((byte)0xCC);
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
        if (bytes.Length < 15 || bytes[0] != 0xCC || bytes[9] != 1 ||
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
