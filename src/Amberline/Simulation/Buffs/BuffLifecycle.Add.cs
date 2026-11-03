using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    public BuffState? AddStackingBuff(
        Entity unit,
        string name,
        BuffType type,
        double duration,
        uint casterNetId,
        int maxStacks,
        BuffPayload perStackPayload,
        // Explicitly updates the supplied buff variables while retaining the
        // live instance's slot, periodic clock, and managed visual handles.
        // This is used for shared client buff names whose
        // BUFF_RENEW_EXISTING payload changes between casters.
        bool replaceExistingPayload = false,
        // BUFF_STACKS_AND_CONTINUE: a further stack raises the count and leaves the running timer alone (the shipped potions: the
        // buff ends once, and RegenerationPotion's OnBuffDeactivate re-adds the stacks still owed). STACKS_AND_RENEWS resets it.
        bool continueTimer = false,
        // The script add's own CanMitigateDuration, carried but not read since batch 296 (see AddBuff): Hardening.luaobj's tenacity
        // tests no such flag, so a held tenacity item cuts the Bilgewater Cutlass and Hextech Gunblade slow too.
        bool canMitigateDuration = true)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(perStackPayload);
        if (maxStacks < 1) throw new ArgumentOutOfRangeException(nameof(maxStacks));
        if (YorickDelayRejects(unit, type, casterNetId)) return null;
        if (WardRejects(unit, type, casterNetId)) return null;
        if (PoppedShieldRejects(unit, casterNetId)) return null;
        if (ScriptBuffRefuses(unit, name, type, ref duration, casterNetId, maxStacks)) return null;

        var existing = unit.Buffs.FirstOrDefault(buff => buff.Name == name &&
            (!perStackPayload.PerCaster || buff.CasterNetId == casterNetId));
        if (existing is null)
        {
            var initialPayload = ScalePayload(perStackPayload, 1);
            var buff = AddBuff(unit, name, type, duration, casterNetId,
                initialPayload, canMitigateDuration, scriptsAsked: true);
            if (buff is not null) buff.Stacks = 1;
            return buff;
        }

        var oldStacks = existing.Stacks;
        var stacks = Math.Min(maxStacks, oldStacks + 1);
        // At the cap a continuing add changes nothing: the count, the timer and the caster stay, and nothing is sent.
        if (continueTimer && stacks == oldStacks && !replaceExistingPayload) return existing;
        var movementChanged = false;
        if (stacks != oldStacks || replaceExistingPayload)
        {
            var previousPayload = existing.Payload;
            var previousPeriodElapsed = replaceExistingPayload
                ? previousPayload.PeriodElapsed : 0.0;
            var replacementPayload = ScalePayload(perStackPayload, stacks);
            // BUFF_RENEW_EXISTING updates the live buff variables. The
            // slot, periodic clock, and server-owned visual handles belong to
            // that live instance and must survive a payload replacement.
            replacementPayload.FxNetId = previousPayload.FxNetId;
            replacementPayload.ExtraFxNetId = previousPayload.ExtraFxNetId;
            replacementPayload.VisionBubbles = previousPayload.VisionBubbles;
            movementChanged = ChangesMovementSpeed(previousPayload) ||
                              ChangesMovementSpeed(replacementPayload);
            ReverseBuffStats(unit, previousPayload);
            existing.Payload = replacementPayload;
            if (replaceExistingPayload && previousPayload.Has(BuffPayloadField.PeriodElapsed))
                existing.Payload.PeriodElapsed = previousPeriodElapsed;
            ApplyBuffStats(unit, existing.Payload);
            if (unit.UnitType != UnitType.Hero && movementChanged)
                RecomputeMovementSpeed(unit);
            if (unit.UnitType == UnitType.Hero &&
                (movementChanged || BuffNeedsFullRefresh(existing.Payload)))
                _heroStats.Refresh(unit);
        }
        existing.Stacks = stacks;
        existing.Payload.Stacks = stacks;
        if (!continueTimer) existing.ExpiresAt = duration;
        existing.CasterNetId = casterNetId;
        if (_services.Effects.SendBuffAdd(unit, existing, stacks))
        {
            if (unit.UnitType == UnitType.Hero) _heroStats.Push(unit);
            else if (movementChanged) _services.Effects.SendMovementSpeed(unit);
        }
        return existing;
    }

    /// <summary>
    /// A BuildingBlocks script buff whose <c>BuffOnUpdateStats</c> result changed (the script engine re-runs the hook, see
    /// <c>BbBuffRuntime</c>): swaps the stat payload of the live buff without a wire add and refreshes what depends on it.
    /// The per-stack payload is scaled by the buff's stacks like <see cref="AddStackingBuff"/> does; the live instance keeps
    /// its slot, timers and visual handles. Returns false when the buff is no longer on the unit.
    /// </summary>
    public bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(buff);
        ArgumentNullException.ThrowIfNull(perStackPayload);
        if (!unit.Buffs.Contains(buff)) return false;
        var previous = buff.Payload;
        var replacement = ScalePayload(perStackPayload, Math.Max(1, buff.Stacks));
        replacement.FxNetId = previous.FxNetId;
        replacement.ExtraFxNetId = previous.ExtraFxNetId;
        replacement.VisionBubbles = previous.VisionBubbles;
        replacement.NonDispellable = previous.NonDispellable;
        // The add's IsHiddenOnClient belongs to the instance too: the observer replay of a late sight and a reconnect sends it again.
        replacement.HiddenOnClient = previous.HiddenOnClient;
        // The script's own tooltip numbers (BBSetBuffToolTipVar) belong to the instance, not to one stack.
        if (previous.Has(BuffPayloadField.TooltipValue1)) replacement.TooltipValue1 = previous.TooltipValue1;
        if (previous.Has(BuffPayloadField.TooltipValue2)) replacement.TooltipValue2 = previous.TooltipValue2;
        if (previous.Has(BuffPayloadField.TooltipValue3)) replacement.TooltipValue3 = previous.TooltipValue3;
        var movementChanged = ChangesMovementSpeed(previous) || ChangesMovementSpeed(replacement);
        var wasRooted = IsRooted(unit);
        var wasHardControlled = IsHardControlled(unit);
        ReverseBuffStats(unit, previous);
        buff.Payload = replacement;
        ApplyBuffStats(unit, replacement);
        if (unit.UnitType != UnitType.Hero && movementChanged) RecomputeMovementSpeed(unit);
        // The shared root of every hold a script status causes: a resample that removes CanMove (a root, a stun, stasis, Teleport's
        // channel) holds the unit in the same tick, whatever wrote the status. Before this the hold fired only on a newly rooted
        // transition, so a status that removed CanMove without rooting left the unit walking and only stopped its swing: the client
        // kept marching the route it had been given and the character walked on a little after the status landed.
        var tookControl = (!wasRooted && IsRooted(unit)) || (!wasHardControlled && IsHardControlled(unit));
        if (tookControl)
        {
            unit.RetainLaneRouteForControl();
            unit.Waypoints.Clear();
            unit.Target = null;
            if (!wasHardControlled && IsHardControlled(unit) && unit.UnitType == UnitType.Hero)
            {
                unit.AttackTarget = null;
                _services.Casts.CancelPendingCast(unit);
            }
            // A still-attached forced move owns its client route (AddBuff does the same): the stop would cancel the dash the 0x67 just started
            // (Vayne's Tumble seals her movement in a buff resample right after its BBMove).
            if (unit.ForcedMove is null) SendControlStop(unit);
        }
        if (!wasHardControlled && IsHardControlled(unit)) StopAttackForHardControl(unit);
        if (unit.UnitType == UnitType.Hero)
        {
            _heroStats.Refresh(unit);
            if (_services.Effects.IsWireReady(unit)) _heroStats.Push(unit);
        }
        else if (movementChanged && _services.Effects.IsWireReady(unit)) _services.Effects.SendMovementSpeed(unit);
        return true;
    }

    /// <summary>
    /// A script buff loses stacks and stays (<c>BBSpellBuffRemove</c> takes one stack of a stacking buff): the count falls by <paramref name="count"/>, the stat
    /// payload is replaced by the one sampled for the stacks that remain (scaled by the new count, like <see cref="ReplaceScriptPayload"/>), and the
    /// client gets the new count the way a raised count reaches it. Returns false when the buff is no longer on the unit or would keep no stack
    /// (that buff is removed with <see cref="RemoveBuff"/>).
    /// </summary>
    public bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(buff);
        ArgumentNullException.ThrowIfNull(perStackPayload);
        if (!unit.Buffs.Contains(buff) || count < 1 || buff.Stacks <= count) return false;
        buff.Stacks -= count;
        ReplaceScriptPayload(unit, buff, perStackPayload);
        buff.Payload.Stacks = buff.Stacks;
        if (_services.Effects.SendBuffAdd(unit, buff, buff.Stacks) && unit.UnitType == UnitType.Hero) _heroStats.Push(unit);
        return true;
    }

    /// <summary>
    /// BUFF_STACKS_AND_OVERLAPS: every application is its own buff instance with its own timer and its own client slot (the stock
    /// client keeps one timer and one stack list per slot: <c>BuffManager_pimpl</c> 0xBF/0x1F/0x32 handlers), so no live instance is
    /// replaced and each expires by itself. Hardness, cleanse and the other add-time rules apply per application.
    /// Reconstruction: at the cap (the script's MaxStack, never above <see cref="OverlapNameCap"/>) the instance of the name with the
    /// least time left makes room for the new one. A full slot table drops the oldest instance of the name, or the new buff when the
    /// unit holds none; the stock client's buff manager has no policy for it (slots are assigned by the server, only an index above 63
    /// is rejected), so this is the emulator's. An overlapping add never throws.
    /// </summary>
    public BuffState? AddOverlappingBuff(Entity unit, string name, BuffType type, double duration, uint casterNetId,
        BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null, bool failIfFull = false) =>
        AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration, maxStack, failIfFull, overlaps: true);
}
