using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    /// <summary>
    /// The end of one simulation tick (ProductionMatchComposition.Tick, after the scripts and the buff hooks have run): the expiries held in an
    /// earlier tick go out now, unless a re-add took their slot (the wire grace of <c>TickBuffs</c>), and the step counter advances.
    /// </summary>
    public void EndTick()
    {
        if (_renewalRemovals.Count > 0)
            foreach (var removal in _renewalRemovals.Where(held => held.Frame is null && held.Tick < _tick).ToArray())
                SendHeld(removal);
        _tick++;
    }

    public void TickBuffs(Entity unit, double dt, bool push = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
        // The popped shield is the shipped script's WillRemove interval (BansheesVeil.luaobj, SpellShield.luaobj): the flag stays until the
        // script's BuffOnUpdateStats swaps the shield out at its 0.25 s poll, so it is re-read from the script instances, not cleared.
        if (unit.ItemState.SpellShieldPopped) unit.ItemState.SpellShieldPopped = _services.Champions.SpellShieldPopped(unit);
        foreach (var buff in unit.Buffs.ToArray())
        {
            // Champion and damage callbacks can remove buffs and reuse their
            // slots. Only the original, still-active instance may tick or expire.
            if (!unit.Buffs.Contains(buff)) continue;
            // Taunt.luaobj BuffOnUpdateStats: SpellBuffRemoveCurrent once the attacker (the buff's caster) is dead.
            if (buff.Type == BuffType.Taunt && _world.Get(buff.CasterNetId) is not { Alive: true })
            {
                RemoveBuff(unit, buff.Slot);
                continue;
            }
            _services.Champions.OnBuffTick(unit, buff, dt);
            if (!unit.Buffs.Contains(buff)) continue;
            var payload = buff.Payload;
            if (payload.Has(BuffPayloadField.CrippleReduction))
            {
                payload.CrippleAge += dt;
                var value = payload.CrippleReduction * Math.Max(0.25, 1.0 -
                    Math.Floor(payload.CrippleAge) * 0.25);
                if (value != payload.MoveSpeedMult)
                {
                    ReverseBuffStats(unit, payload);
                    payload.MoveSpeedMult = value;
                    payload.AttackSpeedMult = value;
                    ApplyBuffStats(unit, payload);
                    RefreshMovementSpeed(unit, push: true);
                    if (unit.UnitType != UnitType.Hero) _services.Effects.SendAttackSpeed(unit);
                }
            }
            // A buff whose own BuffOnUpdateActions schedules its periodics (BBExecutePeriodically, SummonerDot's five
            // true-damage pulses) applies them through the script; the payload only reports them. Running this ticker
            // as well landed every pulse twice (batch 142 part 2).
            if (payload.Period > 0.0 && !payload.ScriptSchedulesPeriodic)
            {
                // Patch-126 BBExecutePeriodically proto 0.70 seeds
                // LastTimeExecuted on the first BuffOnUpdate, executes the
                // child immediately, and runs at most one delayed child per
                // update while retaining overdue time.
                var immediatePeriodic = buff.Name is "Mourning";
                var firstImmediateUpdate = immediatePeriodic &&
                    !payload.Has(BuffPayloadField.PeriodElapsed);
                var activeDt = Math.Min(dt, Math.Max(0.0, buff.ExpiresAt));
                var elapsed = payload.PeriodElapsed + activeDt;
                var ticks = firstImmediateUpdate
                    ? 1
                    : (int)Math.Floor(elapsed / payload.Period);
                if (immediatePeriodic) ticks = Math.Min(1, ticks);
                payload.PeriodElapsed = firstImmediateUpdate
                    ? 0.0
                    : elapsed - ticks * payload.Period;
                // Batch 212: no pulse cap (the shipped periodic is uncapped). This clock is clipped at ExpiresAt, so a pulse
                // due exactly at expiry runs here. Reconstruction: BbBaseLibrary.ExecutePeriodically does not run it; the
                // expiry order is not recovered. Only host-filled payloads reach this ticker; no shipped script does.
                var changed = false;
                if (ticks > 0 && unit.Alive)
                {
                    var oldHealth = unit.Health;
                    var oldMana = unit.Mana;
                    var healingPerTick = payload.HpPerTick + unit.MaxHealth * payload.HpPercentPerTick;
                    var multiplier = _services.Combat.HealingMultiplier(unit);
                    // Keep each tick's native integer heal-stat addition even when
                    // a coarse scheduler step catches up several ticks at once.
                    if (healingPerTick * multiplier > 0.0)
                        for (var tick = 0; tick < ticks && unit.Health < unit.MaxHealth; tick++)
                            Healing.Apply(unit, healingPerTick * multiplier, _world.Get(buff.CasterNetId));
                    if (payload.ResourceDomain != BuffResourceDomain.Mana || UsesMana(unit))
                        unit.Mana = Math.Min(unit.MaxMana, unit.Mana + ticks * payload.MpPerTick);
                    changed = unit.Health != oldHealth || unit.Mana != oldMana;
                    if (payload.DamagePerTick > 0.0)
                    {
                        for (var tick = 0; tick < ticks && unit.Alive && unit.Buffs.Contains(buff); tick++)
                            _services.Combat.ApplyPeriodicDamage(new PeriodicDamageRequest(
                                unit, buff.CasterNetId, unit.NetId, payload.DamagePerTick,
                                payload.DamageType, payload.SourceKind, payload.SpellOwnerNetId,
                                payload.SourceIsPeriodic, payload.DamageOrigin));
                    }
                }
                if (changed && push && unit.UnitType == UnitType.Hero) _heroStats.Push(unit);
            }
            if (!unit.Buffs.Contains(buff)) continue;
            buff.ExpiresAt -= dt;
            if (buff.ExpiresAt <= 0.0 && !ContinueAmmo(unit, buff))
            {
                // Reconstruction (wire grace): a buff whose wire copy carries a margin is one a script or a poll re-adds on a period. Its remove
                // waits one step for that re-add, which takes the held slot and renews the client's copy (TakeRenewalSlot, AsHeldBuffUpdate); a
                // lone expiry is sent one step late (EndTick). The server's expiry is not moved. Retail's order here is not recovered; retail
                // footage shows no flicker.
                if (buff.Payload.WireDurationMargin > 0.0) HoldRemoval(unit, buff.Slot, frame: null);
                else RemoveBuff(unit, buff.Slot);
            }
        }
    }

    /// <summary>
    /// An ammo buff (<c>BUFF_AmmoStack</c>) whose timer ran out does not end: its script's <c>BuffOnUpdateAmmo</c> adds the next charge and renews the
    /// timer (E14). The client shows the stack count minus one for this buff type (the HUD loop),
    /// so N charges are N + 1 stacks and the scripts' own counts are the charge arithmetic. A buff whose hook did not renew it ends as a buff does that
    /// ran out (the script has no hook, or it chose not to recharge). The hook is <c>BuffOnUpdateAmmo</c>, named in the shipped spell scripts (MissileBarrage, KarmaChakraCharge, AkaliShadowDance).
    /// Reconstruction: the moment it runs is not shipped; the client has no
    /// ammo field (nothing in <c>SpellData</c>, <c>SpellDataInst</c> or the replication tables), only this count rule.
    /// </summary>
    private bool ContinueAmmo(Entity unit, BuffState buff)
    {
        if (buff.Type != BuffType.AmmoStack) return false;
        _services.Champions.OnAmmoTimer(unit, buff);
        return unit.Buffs.Contains(buff) && buff.ExpiresAt > 0.0;
    }

    /// <summary>
    /// Replays authoritative buffs after an entity's network object becomes
    /// visible. FX and Zhonya animation state are deliberately sent only when
    /// the add packet was accepted by the effect boundary.
    /// </summary>
    public void SyncUnitBuffs(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_services.Effects.IsWireReady(unit)) return;
        foreach (var buff in unit.Buffs.ToArray())
        {
            var count = buff.Stacks > 0 ? buff.Stacks :
                buff.Payload.Stacks > 0 ? buff.Payload.Stacks : 1;
            if (!_services.Effects.SendBuffAdd(unit, buff, count)) continue;
            _services.Effects.EnsureBuffFx(unit, buff);
            if (buff.Name == "ZhonyasRingShield")
                _services.Effects.PauseAnimation(unit, true);
        }
    }
}
