using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    public static bool IsItemSlowRow(string? name) =>
        ItemSlowRows.Any(row => string.Equals(row.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The mirror of <c>Spells/ItemSlow.luaobj</c>'s <c>BuffOnUpdateStats</c> cascade: the strongest
    /// self-bound <c>Internal_*Slow</c> row its holder carries, in the order that hook's
    /// <c>BBIfHasBuff</c> → <c>BBElse</c> chain tests them, applied as
    /// <c>IncPercentMultiplicativeMovementSpeedMod</c> from the <c>ItemSlow</c> marker itself and leaving
    /// the rows empty. The marker's own script runs the same cascade when its instance is polled; this
    /// mirror is for the producers that add through the lifecycle with no script instance
    /// (3116, 3078, 3144, 3143) and for the add of a row while the marker is already live. Both paths
    /// write the same field with the same number, so they are idempotent together.
    /// <para>
    /// The caller owns the movement rebuild. Removal defers that rebuild until this selection completes so
    /// it can emit one final speed delta; live applications refresh immediately.
    /// </para>
    /// </summary>
    public static void SyncItemSlow(Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // A rejected marker must never leave a speed penalty active by itself.
        var marker = target.Buffs.FirstOrDefault(buff => buff.Name == "ItemSlow");
        var percent = 0.0;
        if (marker is not null)
            foreach (var row in ItemSlowRows)
                if (target.Buffs.Any(buff => buff.Name == row.Name))
                {
                    percent = row.Percent;
                    break;
                }
        foreach (var buff in target.Buffs)
        {
            if (ReferenceEquals(buff, marker))
            {
                if (buff.Payload.MoveSpeedMult != -percent) buff.Payload.MoveSpeedMult = -percent;
                continue;
            }
            // Shipped rows are empty (Spells/Internal_*Slow.luaobj has no hooks): only a row that still
            // carries a legacy penalty is cleared, so an empty row keeps its absent movement field.
            if (IsItemSlowRow(buff.Name) && buff.Payload.MoveSpeedMult != 0.0)
                buff.Payload.MoveSpeedMult = 0.0;
        }
    }

    /// <summary>
    /// A script status switched on for <paramref name="unit"/> with no buff to hold it (batch 201). The carrier buff that held these
    /// before ran the same effects in <see cref="AddBuff"/> once its payload was in the table: a movement lock cancels a Fear walk, a root
    /// stops the walk (the stationary waypoint, unless a forced move owns it), a hero's stun stops its walk and cancels its pending cast,
    /// and a new hard control interrupts the swing. A status is no buff, so no slot and no add refusal apply to it.
    /// </summary>
    public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted)
    {
        ArgumentNullException.ThrowIfNull(unit);
        // A status write changes no pending action, so the stun the hero had before is its hard control then or that block now.
        var heroStunStarts = unit.UnitType == UnitType.Hero && !wasHardControlled && !_services.Combat.IsPendingActionBlocked(unit) &&
                             IsStunned(unit);
        var cancelledFearWalk = unit.ForcedMove is { Effect: "Fear" } && BlocksFearLocomotion(unit);
        if (cancelledFearWalk)
        {
            unit.RetainLaneRouteForControl();
            unit.ForcedMove = null;
            unit.Waypoints.Clear();
            unit.Target = null;
            if (IsLaneMinion(unit))
                unit.AnchorPath = [_clientAnchor(unit.Position)];
        }
        var forcedMoveOwnsWaypoint = unit.ForcedMove is not null;
        // Batch 320 item 0: the root's stop and the hero stun's stop and cast cancel run when the state starts, not on every later
        // status write while it lasts (the client's own setters tell their listener only on a change of the CanAttack/CanCast/CanMove
        // bits: the setters compare the old low bits and skip the call when they are equal).
        var rootStarts = !wasRooted && IsRooted(unit);
        if (rootStarts)
        {
            unit.RetainLaneRouteForControl();
            unit.Waypoints.Clear();
            unit.Target = null;
            if (!forcedMoveOwnsWaypoint) SendControlStop(unit);
        }
        if (heroStunStarts)
        {
            unit.Waypoints.Clear();
            unit.Target = null;
            unit.AttackTarget = null;
            _services.Casts.CancelPendingCast(unit);
            if (!forcedMoveOwnsWaypoint) SendControlStop(unit);
        }
        // The stationary waypoint of a root or stun that starts is sent above; another movement lock still corrects a Fear segment it interrupts.
        if (cancelledFearWalk && !rootStarts && !heroStunStarts)
            SendControlStop(unit);
        if (!wasHardControlled && IsHardControlled(unit)) StopAttackForHardControl(unit);
    }

    /// <summary>
    /// A script status switched off for <paramref name="unit"/> (batch 201): the release callbacks <see cref="RemoveBuffNow"/> runs for a
    /// buff that held a movement or attack lock. A Fear still on the unit keeps them quiet, as it does there. <paramref name="couldMove"/>
    /// and <paramref name="couldAttack"/> are the walk and the swing the unit had before the release.
    /// </summary>
    public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.Alive || unit.Buffs.Any(active => active.Type == BuffType.Fear || active.Name == "Fear" || active.Payload.FearOrigin is not null))
            return;
        if (IsLaneMinion(unit))
        {
            if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) Minions?.OnCanMove(unit);
            if (!couldAttack && CharacterState.Of(unit).CanAttack) Minions?.OnCanAttack(unit);
        }
        if (AiScripts is { } ai && ai.Drives(unit))
        {
            if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) ai.OnCanMove(unit);
            if (!couldAttack && CharacterState.Of(unit).CanAttack) ai.OnCanAttack(unit);
        }
        if (Spawned is { } spawned && spawned.Drives(unit))
        {
            if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) spawned.OnCanMove(unit);
            if (!couldAttack && CharacterState.Of(unit).CanAttack) spawned.OnCanAttack(unit);
        }
        if (Pets is { } pets && pets.Drives(unit))
        {
            if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) pets.OnCanMove(unit);
            if (!couldAttack && CharacterState.Of(unit).CanAttack) pets.OnCanAttack(unit);
        }
    }

    /// <summary>
    /// The hard control, walk and swing of a unit before a script status changes (batch 201). <see cref="ScriptStatusGained"/> takes the
    /// hard control, <see cref="ScriptStatusLost"/> the walk and the swing.
    /// </summary>
    internal static (bool HardControlled, bool CouldMove, bool CouldAttack, bool Rooted) ControlOf(Entity unit)
    {
        var hard = IsHardControlled(unit);
        var rooted = Rooted(unit);
        return (hard, !rooted && !hard, CharacterState.Of(unit).CanAttack, rooted);
    }

    internal static bool Rooted(Entity unit) => unit.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    // Hard control (minionLib OnFearBegin's TurnOffAutoAttack(STOPREASON_IMMEDIATELY)) drops a swing still in its windup and tells the
    // client. A hero's client is told for that alone. Reconstruction: the retail packet for this reason is unrecovered; any other unit's
    // client stays in its attack state, which starts the same attack again and erases the unit's path (CombatPackets.StopAttack), until
    // it is stopped, even when the hit already launched and continues, so the sink is asked for every such unit and sends exactly
    // while it has attacked.
    private void StopAttackForHardControl(Entity unit)
    {
        _services.Combat.InterruptBasicAttack(unit, out var cancelledWindup);
        if ((cancelledWindup || unit.UnitType != UnitType.Hero) && _services.Effects.IsWireReady(unit))
            _services.Effects.SendInstantStopAttack(unit);
    }

    /// <summary>
    /// The wire half of every hold a CanMove-removing status causes (a stun, stasis, Teleport's channel, a root): the stationary
    /// <c>WaypointList</c> that makes the client stand at once. It carries the position of the tick the status landed in, never a stale
    /// or predicted one, so the point the client seats its actor on (<c>ClientFollowServerPath</c> snaps to the two-unit wire anchor of the
    /// first point) is the anchor of where the unit already stood and the hold moves nothing the client draws.
    /// The unit's own position is deliberately left where it stands: it is the geometry a displacement of the same script step reads
    /// (<c>BBMoveAway</c> lands from the exact position its control buff's add found), and seating the body on the lattice first moved
    /// Monsoon's wall-crossing landing by 28 units (batch 158).
    /// Batch 710 (Reconstruction, owner, 7 November 2026: the capture bar flashed and vanished, the pose ended and the champion stood until
    /// the charge): a unit that runs a script channel gets none. It stands: the client's cast stopped its actor (Client_DoCastSpell's StopActor),
    /// and the channel's Move rule ends the channel on any step (BbScriptChannels.TickChannels); a stun that ends the channel publishes the
    /// channel's own seat with its stop (BbScriptChannels.Stop). The one shipped channel that roots its own caster, OdinCaptureChannel
    /// (ChannelingStart: OdinCaptureImmobile, SetCanMove false), began at the cast frame from batch 678, so this waypoint reached the
    /// capturer's client inside its SpellCaster (0xC1: ClientFollowServerPath and a new-path event on the unit's state machine), the only
    /// packet of that window the scripts do not send. Retail's server half of a root is not shipped.
    /// </summary>
    private void SendControlStop(Entity unit)
    {
        if (!_services.Effects.IsWireReady(unit) || unit.ActiveScriptChannels > 0) return;
        _services.Effects.SendWaypoints(unit);
    }

    public bool IsStunned(Entity unit) =>
        _services.Combat.IsPendingActionBlocked(unit) || IsHardControlled(unit);

    public bool IsRooted(Entity unit) => Rooted(unit);

    // The Fear buff itself disables voluntary actions but permits its own
    // wander. A second movement lock cannot be bypassed by that wander.
    internal static bool BlocksFearLocomotion(Entity unit) =>
        unit.AnyBuffOrStatus(buff => buff.Type != BuffType.Fear && buff.Name != "Fear" &&
            buff.Payload.FearOrigin is null &&
            (buff.Type is BuffType.Snare or BuffType.Stun or BuffType.Suppression || buff.DisablesActions ||
                buff.Payload.MovementDisabled));

    private static bool IsHardControlled(Entity unit) =>
        unit.AnyBuffOrStatus(buff => buff.DisablesActions);

    private static bool IsHardnessType(BuffType type) => type is
        BuffType.Snare or BuffType.Slow or BuffType.Fear or BuffType.Net or BuffType.Sleep or
        BuffType.Stun or BuffType.Taunt or BuffType.Silence or BuffType.Blind;

    private static bool IsEpicControl(BuffType type) => IsHardnessType(type) || type == BuffType.Suppression;

    // Items/3117.luaobj BuffOnAllowAdd: BUFF_Damage, Fear, Net, Polymorph, Silence, Sleep, Snare, Stun and Slow
    // from an enemy start the five-second BootsOfMobilityDebuff.
    private static bool IsMobilityControl(BuffType type) => type is
        BuffType.DamageOverTime or BuffType.Fear or BuffType.Net or BuffType.Polymorph or BuffType.Silence or
        BuffType.Sleep or BuffType.Snare or BuffType.Stun or BuffType.Slow;
}
