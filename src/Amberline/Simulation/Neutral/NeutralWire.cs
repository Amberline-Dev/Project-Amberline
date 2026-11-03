using Amberline.Core;
using Amberline.Data;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Neutral;

/// <summary>
/// Required neutral-to-network boundary. The scheduler owns state and packet
/// order; this boundary owns allocation, shared replication IDs, and terrain
/// encoding so a neutral tick cannot silently manufacture a private counter.
/// </summary>
public interface INeutralPacketSink
{
    NavigationBounds FxBounds { get; }
    double TerrainHeight(Position2 position);
    uint Allocate(UnitType unitType);
    uint NextSync(uint unitNetId);
    void Enqueue(PlaintextPacket packet);
    void CreateNeutral(NeutralMember member);
    void AddBuff(Entity target, BuffState buff);
    void MinionStats(Entity entity, NeutralStats stats);
    void ReplicationHp(Entity entity);
    void StopAttack(Entity entity);
    void Waypoints(Entity entity, IReadOnlyList<Position2> points, uint syncId);
}

/// <summary>Protocol implementation used by a match delivery queue or capture.</summary>
public sealed class ProtocolNeutralPacketSink : INeutralPacketSink
{
    private readonly Action<PlaintextPacket> _enqueue;
    private readonly Func<UnitType, uint> _allocate;
    private readonly ReplicationSyncCounter _syncIds;
    private readonly Func<double, double, double> _height;
    private readonly Func<string, string> _clientBuffName;
    private readonly Func<string, bool> _hasClientBuff;

    /// <param name="clientBuffName">
    /// The name a buff's add carries (the binding name): the match passes GameData.ClientBuffName. A bare sink (tests, tools)
    /// keeps the binding name.
    /// </param>
    public ProtocolNeutralPacketSink(
        Action<PlaintextPacket> enqueue,
        ReplicationSyncCounter syncIds,
        Func<UnitType, uint> allocate,
        NavigationBounds fxBounds,
        Func<double, double, double> height,
        Func<string, string>? clientBuffName = null,
        Func<string, bool>? hasClientBuff = null)
    {
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
        _allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));
        _height = height ?? throw new ArgumentNullException(nameof(height));
        _clientBuffName = clientBuffName ?? (bindName => bindName);
        _hasClientBuff = hasClientBuff ?? (_ => true);
        FxBounds = fxBounds;
    }

    public NavigationBounds FxBounds { get; }
    public double TerrainHeight(Position2 position) => _height(position.X, position.Z);
    public uint Allocate(UnitType unitType) => _allocate(unitType);
    public uint NextSync(uint unitNetId) => _syncIds.Next(unitNetId);
    public void Enqueue(PlaintextPacket packet) => _enqueue(packet ?? throw new ArgumentNullException(nameof(packet)));

    public void CreateNeutral(NeutralMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var entity = member.Entity;
        var stats = member.Stats;
        Enqueue(SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(
            entity.NetId, NeutralDefinitions.NeutralNodeId,
            new Position3(entity.Position.X, member.Home.Y, entity.Position.Z),
            member.GroupPosition, member.FacePosition, member.Name, member.Name,
            NeutralDefinitions.UniqueName(member.Name, member.CampId, 1, member.Member),
            NeutralDefinitions.MinimapIcon(member.Name),
            NeutralDefinitions.NeutralWireTeam,
            checked((int)Math.Round(stats.DamageBonus, MidpointRounding.ToEven)),
            checked((int)Math.Round(stats.HealthBonus, MidpointRounding.ToEven)),
            0, member.CampId)));
        Enqueue(CombatPackets.MinionStats(entity.NetId, new MinionStatsSnapshot(
            Float(stats.Hp), Float(stats.AttackDamage), Float(stats.Speed),
            Float(stats.Armor), Float(stats.SpellBlock), Float(stats.AttackSpeedMod),
            Float(stats.HpRegen), ParRegen: Float(stats.MpRegen),
            TargetableToTeam: CombatPackets.MinionTargetableToTeam(entity)), NextSync(entity.NetId)));
    }

    public void AddBuff(Entity target, BuffState buff)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(buff);
        // Batch 212: the gate every other buff wire applies (ProtocolSpellEffectSink.ReachesClient). A server-only marker is a
        // buff the client must never hold, and the client skips an add whose name hash its BuffHashMap lacks. Every buff in the
        // Worm's, the Dragon's and the camps' tables is client-known today, so this drops none of them (batch 212 probe).
        if (buff.Payload.ServerOnlyMarker || !_hasClientBuff(buff.Name)) return;
        Enqueue(SpellPackets.AddBuff(new BuffAdd(target.NetId, checked((byte)buff.Slot),
            (byte)buff.Type, SpellPackets.BuffNameHash(_clientBuffName(buff.Name)), Float(buff.ExpiresAt),
            buff.CasterNetId, IsHidden: buff.HiddenOnClientByte)));

        var effectName = buff.Name switch
        {
            "CrestoftheAncientGolem" => "NeutralMonster_buf_blue_defense.troy",
            "BlessingoftheLizardElder" => "NeutralMonster_buf_red_offense.troy",
            _ => null,
        };
        if (effectName is null) return;

        var effect = Allocate(UnitType.Missile);
        buff.Payload.FxNetId = effect;
        // The client's particle map holds stems, so the name hashes as the spell effects hash theirs (GameData.ParticleStem).
        var hashName = GameData.ParticleStem(effectName);
        Enqueue(FxPackets.CreateGroup(SpellPackets.BuffNameHash(hashName), effect,
            target.Position, FxBounds, _height,
            targetNetId: target.NetId, bindNetId: target.NetId));
    }

    public void MinionStats(Entity entity, NeutralStats stats) =>
        Enqueue(CombatPackets.MinionStats(entity.NetId, new MinionStatsSnapshot(
            Float(stats.Hp), Float(stats.AttackDamage), Float(stats.Speed),
            Float(stats.Armor), Float(stats.SpellBlock), Float(stats.AttackSpeedMod),
            Float(stats.HpRegen), ParRegen: Float(stats.MpRegen),
            TargetableToTeam: CombatPackets.MinionTargetableToTeam(entity)), NextSync(entity.NetId)));

    public void ReplicationHp(Entity entity) =>
        Enqueue(CombatPackets.ReplicationHp(entity.NetId, Float(entity.Health),
            NextSync(entity.NetId), isHero: false));

    // CombatPackets.StopAttack decides: a monster's client gets a stop only while it is in its attack state, with the flags that match
    // what the server did with the hit.
    public void StopAttack(Entity entity)
    {
        if (CombatPackets.StopAttack(entity) is { } packet) Enqueue(packet);
    }

    public void Waypoints(Entity entity, IReadOnlyList<Position2> points, uint syncId) =>
        Enqueue(VisionPackets.WaypointList(entity.NetId, points, syncId));

    private static float Float(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }
}
