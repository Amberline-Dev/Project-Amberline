using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Buffs;

/// <summary>
/// Typed packet/effect adapter used by <see cref="BuffLifecycle"/>.
/// State mutation and ordering remain in the lifecycle; this class only maps
/// those transitions to the spell effect boundary and the shipped persistent
/// buff visuals.
/// </summary>
public sealed class BuffEffectService : IBuffEffectHooks
{
    /// <summary>
    /// The server-owned persistent visual of a buff: the name drawn for as long as the buff is on its holder and killed
    /// with it. A row is only the fallback for a buff whose shipped script draws nothing of its own — the script's
    /// <c>OnBuffActivate</c> particle is the truth and this copy is not made for it (see <see cref="EnsureBuffFx"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PersistentEffects =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CrestoftheAncientGolem"] = "NeutralMonster_buf_blue_defense.troy",
            ["BlessingoftheLizardElder"] = "NeutralMonster_buf_red_offense.troy",
            ["ExaltedWithBaronNashor"] = "nashor_rune_buf.troy",
            ["Propel"] = "PropelBubbles.troy",
            ["BansheesVeil"] = "bansheesveil_buf.troy",
            ["CatalystHeal"] = "env_manaheal.troy",
            // OdinEntropicClaymore is not here any more (batch 149 step 2): Spells/OdinEntropicClaymore.luaobj's
            // own OnBuffActivate draws NeutralMonster_buf_red_offense.troy (BBSpellEffectCreate) and its
            // OnBuffDeactivate removes it, the way the converted champion buffs draw theirs.
            ["BilgewaterCutlass"] = "Global_Slow.troy",
            ["HextechGunbladeSpell"] = "Global_Slow.troy",
            ["ShurelyasSpell"] = "Haste_proc.troy",
        };

    private readonly ISpellEffectSink _effects;
    private readonly IBuffVisionSink _vision;
    private readonly Func<Entity, long> _nextWaypointSyncId;

    public BuffEffectService(
        ISpellEffectSink effects,
        IBuffVisionSink vision,
        Func<Entity, long> nextWaypointSyncId)
    {
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _vision = vision ?? throw new ArgumentNullException(nameof(vision));
        _nextWaypointSyncId = nextWaypointSyncId ?? throw new ArgumentNullException(nameof(nextWaypointSyncId));
    }

    public bool IsWireReady(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _effects.IsReferenceReady(unit);
    }

    public bool SendBuffAdd(Entity unit, BuffState buff, int count) =>
        SendBuffChange(unit, buff, added: true, count: count);

    public bool SendBuffRemove(Entity unit, BuffState buff) =>
        SendBuffChange(unit, buff, added: false, count: 0);

    private bool SendBuffChange(Entity unit, BuffState buff, bool added, int count)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(buff);
        // Batch 387: the cougar form's buff goes to the wire like any other. Its shipped script sets SpellToggleSlot 4 (R) and its TargetExecute
        // adds it, so the client holds R toggled on for the form (the second cast removes it). The name gate that kept the add off the wire is gone.
        if (!IsWireReady(unit)) return false;
        _effects.SendBuffDelta(unit, buff, added: added, count: count);
        if (unit.UnitType == UnitType.Turret && BuffLifecycle.BuffNeedsFullRefresh(buff.Payload))
            _effects.SendStats(unit);
        else if (ChangesAttackSpeed(buff.Payload)) SendAttackSpeed(unit);
        return true;
    }

    private static bool ChangesAttackSpeed(BuffPayload payload) =>
        payload.Has(BuffPayloadField.AttackSpeedMod) || payload.Has(BuffPayloadField.AttackSpeedMult);

    public void SendAttackSpeed(Entity unit)
    {
        if (unit.UnitType != UnitType.Hero && IsWireReady(unit))
            _effects.SendAttackSpeed(unit);
    }

    public void SendMovementSpeed(Entity unit)
    {
        if (unit.UnitType != UnitType.Hero && IsWireReady(unit))
            _effects.SendMovementSpeed(unit);
    }

    public void SendDamageShieldDelta(Entity unit, double amount)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (amount != 0.0 && IsWireReady(unit)) _effects.SendShieldDelta(unit, amount);
    }

    public void EnsureBuffFx(Entity unit, BuffState buff)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(buff);
        if (!IsWireReady(unit)) return;

        if (!PersistentEffects.TryGetValue(buff.Name, out var effectName))
            return;
        // Batch 157 part 1: the rows below are the hand-written visual of a buff, written before its shipped script ran.
        // Where that script draws the same particle from its own OnBuffActivate (and takes it back in OnBuffDeactivate),
        // the row stacks a second copy for the whole life of the buff — the playtest's "really bright, perhaps a stacked
        // visual effect" was two bansheesveil_buf from one BansheesVeil add. The script is the truth, so the copy is
        // left to it (BbBuffRuntime.DrawsActivationEffect marks the add); OdinEntropicClaymore was taken out of this
        // table for the same reason in batch 149 step 2. What stays is a buff with no activation effect of its own.
        if (buff.Payload.ScriptOwnsActivationFx) return;
        if (buff.Payload.FxNetId == 0)
        {
            buff.Payload.FxNetId = _effects.CreateEffect(
                effectName, unit.Position,
                targetNetId: unit.NetId, bindNetId: unit.NetId,
                boneName: PersistentEffectBone(buff.Name));
        }
    }

    public static IEnumerable<(uint NetId, string Name, string? Bone)> PersistentVisuals(
        Entity unit, BuffState buff, bool enemyObserver = false)
    {
        if (buff.Payload.FxNetId != 0 && PersistentEffects.TryGetValue(buff.Name, out var name))
            yield return (buff.Payload.FxNetId, name, PersistentEffectBone(buff.Name));
    }

    /// <summary>
    /// The shipped buff tables attach watched markers to the victim's head.
    /// Buffs with a native AutoBuffActivateEffect own that visual on the
    /// client; this helper covers only server-owned persistent effects.
    /// </summary>
    public static string? PersistentEffectBone(string buffName) =>
        // No hand-written buff keeps a server-owned persistent effect on a bone any more: Lee Sin's marks and Shaco's clone mark are the effects of their scripts since phase 3.
        null;

    /// <summary>
    /// Returns the existing 0x83 buff-tooltip tuple. The client's tooltip routine
    /// writes the tuple into three buff fields, and the shipped @f1@,
    /// @f2@, and @f3@ formatter reads those fields in order. Keep the old
    /// Eye/Malady fallback for untyped callers.
    /// </summary>
    public static bool TryGetBuffTooltipValues(BuffState buff,
        out double value0, out double value1, out double value2)
    {
        ArgumentNullException.ThrowIfNull(buff);
        value0 = buff.Payload.Has(BuffPayloadField.TooltipValue1)
            ? buff.Payload.TooltipValue1
            : buff.Name == "MaladyCounter" ? -buff.Payload.SpellBlock : 0.0;
        value1 = buff.Payload.Has(BuffPayloadField.TooltipValue2)
            ? buff.Payload.TooltipValue2 : 0.0;
        value2 = buff.Payload.Has(BuffPayloadField.TooltipValue3)
            ? buff.Payload.TooltipValue3 : 0.0;
        return buff.Payload.Has(BuffPayloadField.TooltipValue1) ||
               buff.Payload.Has(BuffPayloadField.TooltipValue2) ||
               buff.Payload.Has(BuffPayloadField.TooltipValue3) ||
               buff.Name is "MaladyCounter";
    }

    public void KillSpellEffect(uint effectNetId)
    {
        if (effectNetId != 0) _effects.KillEffect(effectNetId);
    }

    public void RemovePerceptionBubble(Entity unit, uint bubbleId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (bubbleId != 0) _vision.RemovePerceptionBubble(unit, bubbleId);
    }

    public void SendWaypoints(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!IsWireReady(unit)) return;
        _effects.SendWaypoints(unit,
            new[] { unit.Position }.Concat(unit.Waypoints).ToArray(),
            _nextWaypointSyncId(unit));
    }

    public void PauseAnimation(Entity unit, bool paused)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (IsWireReady(unit)) _effects.PauseAnimation(unit, paused);
    }

    public void CompleteGuardianRevival(Entity unit)
    {
        if (!IsWireReady(unit)) return;
        _effects.PlayAnimation(unit, "Idle1");
        _effects.CreateEffect("GuardianAngel_tar.troy", unit.Position, bindNetId: unit.NetId);
    }

    public void SendInstantStopAttack(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (IsWireReady(unit)) _effects.SendStopAttack(unit);
    }
}

/// <summary>Required visibility boundary for buff-owned unit bubbles.</summary>
public interface IBuffVisionSink
{
    Entity? ResolveCaster(uint netId);
    uint AddUnitPerceptionBubble(Entity unit, double radius, double ttl,
        uint bubbleType, uint flags);
    void RemovePerceptionBubble(Entity unit, uint bubbleId);
}

/// <summary>
/// Protocol implementation of the buff-owned vision boundary. Allocation and
/// packet delivery are injected so IDs remain match-owned and the lifecycle
/// cannot accidentally allocate from a process-global counter.
/// </summary>
public sealed class ProtocolBuffVisionSink : IBuffVisionSink
{
    private readonly Func<uint, Entity?> _resolve;
    private readonly Func<UnitType, uint> _allocate;
    private readonly ICombatPacketQueue _queue;

    public ProtocolBuffVisionSink(
        Func<uint, Entity?> resolve,
        Func<UnitType, uint> allocate,
        ICombatPacketQueue queue)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public Entity? ResolveCaster(uint netId) => netId == 0 ? null : _resolve(netId);

    public uint AddUnitPerceptionBubble(Entity unit, double radius, double ttl,
        uint bubbleType, uint flags)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var bubbleId = _allocate(UnitType.Missile);
        // DIAGNOSTICS (batch 476): the gate withholds only this packet; the bubble id and the server's sight stand.
        if (!Amberline.Network.Diagnostics.OdinRelicBubbleGate.Withholds(unit.ChampionName))
            _queue.Enqueue(SpawnPackets.AddUnitPerceptionBubble(
                unit.NetId, Float(radius), Float(ttl), bubbleId, bubbleType, flags: flags));
        return bubbleId;
    }

    public void RemovePerceptionBubble(Entity unit, uint bubbleId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (bubbleId != 0) _queue.Enqueue(SpawnPackets.RemovePerceptionBubble(bubbleId));
    }

    private static float Float(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }
}
