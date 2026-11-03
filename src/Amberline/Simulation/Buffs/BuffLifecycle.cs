using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Simulation.Buffs;

/// <summary>Authoritative add, remove, stack, expiry, and periodic-buff lifecycle.</summary>
public sealed partial class BuffLifecycle
{
    public const int MaxBuffSlots = 64;
    // Reconstruction: BUFF_STACKS_AND_OVERLAPS scripts declare MaxStack 100, more than the 64-slot table (Jack in the Box's 20 is the
    // largest finite one). A name never holds more than this many live instances, whatever the script asks for, so one overlapping
    // buff cannot take the whole table.
    private const int OverlapNameCap = 20;

    public MinionAiSystem? Minions { get; set; }

    /// <summary>Leashed.luaobj fear and taunt callbacks for jungle camp members.</summary>
    public NeutralAiService? Neutrals { get; set; }

    /// <summary>
    /// The Lua AI host every script-driven unit answers through. The minion and neutral services
    /// above are the hand-written AIs; a unit with a script instance takes its Taunt and
    /// control-release events here instead (Turret.luaobj today).
    /// </summary>
    public IAiHost? AiScripts { get; set; }

    /// <summary>
    /// The pet scripts' own host (<c>Pet.luaobj</c>, <c>UncontrollablePet.luaobj</c> and
    /// <c>YorickPHPet.luaobj</c> all answer <c>OnTauntBegin</c>/<c>OnTauntEnd</c>,
    /// <c>OnFearBegin</c>/<c>OnFearEnd</c> and <c>OnCanMove</c>/<c>OnCanAttack</c>, so unlike the
    /// turret host above this one also takes the fear pair.
    /// </summary>
    public IAiHost? Pets { get; set; }

    /// <summary>
    /// The host running each champion's <c>Hero.luaobj</c>. The chunk answers
    /// <c>OnTauntBegin</c>/<c>OnTauntEnd</c> and <c>OnFearBegin</c>/<c>OnFearEnd</c> (its own state
    /// machine takes the taunt target and runs the fear wander) and defines neither
    /// <c>OnCanMove</c> nor <c>OnCanAttack</c>, so those are not raised for it.
    /// </summary>
    public IAiHost? Heroes { get; set; }

    /// <summary>
    /// The host running <c>Idle.luaobj</c> and <c>Aggro.luaobj</c> for the units a
    /// <c>BBSpawnMinion</c> gave one (a ward, a wall piece, a blood drop, Wukong's Nimbus clones).
    /// Aggro answers all six control hooks (its <c>OnFearBegin</c> arms its own <c>TimerFeared</c>
    /// wander); Idle answers <c>OnTauntBegin</c>/<c>OnTauntEnd</c>, <c>OnCanMove</c> and
    /// <c>OnCanAttack</c> with nothing and defines no fear hook at all, so the shared fear pulse
    /// keeps walking those.
    /// </summary>
    public IAiHost? Spawned { get; set; }

    // Reconstruction: the client buff table holds 64 slots (MaxBuffSlots) and no shipped item, summoner or buff script tests for
    // room; what the original server did when a script's add found the table full is unrecovered. These admission helpers
    // (CanAddNamedBuffs, CanAddScopedBuffs and the failIfFull: false path of AddBuff) make a callback that cannot get a slot
    // decline as a whole instead of throwing, and every call site that guards with them inherits this marker.
    //
    /// <summary>
    /// The self-bound item-slow rows, strongest first — the order the <c>Spells/ItemSlow.luaobj</c>
    /// <c>BuffOnUpdateStats</c> cascade (<c>BBIfHasBuff</c> → <c>BBElse</c>) tests them. The magnitude lives in
    /// the name: the shipped <c>Spells/Internal_*Slow.luaobj</c> scripts are empty. The family is recognized by
    /// name (not by a payload field) so a script row with an empty payload still takes part in the marker
    /// cascade, the marker-removal sweep and death retirement. The producers of the family are shipped scripts
    /// that add the rows by these names (<c>Items/3022</c>, <c>3044</c>, <c>3078</c>, <c>3116</c>, <c>3184</c> and
    /// <c>Spells/OdinEntropicClaymore</c>); no hand-written producer is left (batch 303 comment fix).
    /// </summary>
    public static readonly (string Name, double Percent)[] ItemSlowRows =
    [
        ("Internal_50Slow", 0.50), ("Internal_40Slow", 0.40), ("Internal_35Slow", 0.35),
        ("Internal_30Slow", 0.30), ("Internal_20Slow", 0.20), ("Internal_15Slow", 0.15),
    ];

    private readonly WorldState _world;
    private readonly HeroStatsService _heroStats;
    private readonly IBuffServices _services;
    private readonly Func<Position2, Position2> _clientAnchor;

    public BuffLifecycle(WorldState world, HeroStatsService heroStats, IBuffServices services,
        Func<Position2, Position2>? clientAnchor = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _heroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _clientAnchor = clientAnchor ?? (static position => position);
        ArgumentNullException.ThrowIfNull(services.Combat);
        ArgumentNullException.ThrowIfNull(services.Casts);
        ArgumentNullException.ThrowIfNull(services.Champions);
        ArgumentNullException.ThrowIfNull(services.Effects);
        ArgumentNullException.ThrowIfNull(services.Items);
    }

    public BuffState? AddBuff(
        Entity unit,
        string name,
        BuffType type,
        double duration,
        uint casterNetId,
        BuffPayload? payload = null,
        bool canMitigateDuration = true,
        int? maxStack = null,
        bool failIfFull = true,
        bool overlaps = false,
        // True when the caller has asked the script buffs about this add already (AddStackingBuff asks for its first stack): not asked twice.
        bool scriptsAsked = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        payload ??= new BuffPayload();
        if (maxStack is < 0) throw new ArgumentOutOfRangeException(nameof(maxStack));

        if (RefusesAdd(unit, type, casterNetId, maxStack,
                hostileInternal: payload.MoveSpeedMult < 0.0 || payload.ItemSlowPercent > 0.0 ||
                    IsItemSlowRow(name)) ||
            (!scriptsAsked && ScriptBuffRefuses(unit, name, type, ref duration, casterNetId, maxStack))) return null;

        // Batch 296: every crowd-control cut is a held buff's BuffOnAllowAdd, written back above (ScriptBuffRefuses) and multiplied in turn:
        // tenacity is Spells/Hardening.luaobj (Duration x (1 - GetPercentHardnessMod) on an enemy's Snare, Slow, Fear, Net, Sleep, Stun,
        // Taunt, Silence or Blind, with no CanMitigateDuration test; the 1 s aura of the four tenacity items), Insanity Potion's CCreduction,
        // MercuryTreads and Cleanse (SummonerBoost) the same way. GarenBladestorm's own hook divides an enemy slow by 1 - hardness, so the
        // scripts count on exactly one such cut of every enemy slow. The native copy here (Duration x (1 - Hardness) when the add said
        // CanMitigateDuration) was a second cut of the same stat once the tenacity items ran from their scripts (batch 149): a 1 s stun on
        // Mercury's Treads lasted 0.4225 s.
        // Reconstruction: the client only parses CanMitigateDuration (default false) and its server consumer is not recovered;
        // the add still carries it (canMitigateDuration), and nothing here reads it.

        // Batch 147 step 4: the Mobility boots penalty is Items/3117.luaobj's own BuffOnAllowAdd now
        // (same hostile-control test, same BootsOfMobilityDebuff row). The admission helpers above stay:
        // the script's add still needs a free nested slot.

        var wasHardControlled = IsHardControlled(unit);
        var wasRooted = IsRooted(unit);
        var wasStunned = unit.UnitType == UnitType.Hero && IsStunned(unit);
        var existing = overlaps ? null : unit.Buffs.FirstOrDefault(buff => buff.Name == name &&
            (!payload.PerCaster || buff.CasterNetId == casterNetId));
        // Batch 157 part 3: a replacing add of a buff the unit already carries is one event to the client, not a removal
        // and an add. The stock client's packet for it is NpcBuffReplace (0x32): its handler resizes the live copy's
        // timers and restarts its presentation in place. The removal packet before the add dropped that copy instead
        // (the client's buff manager removes whatever occupies the slot), so the periodic self-buff polls' replacing
        // rows made the buff blink once a period — 3110's FrozenHeart (BUFF_REPLACE_EXISTING, Duration 10, re-added every
        // 0.9 s) was removed and re-added on the wire for as long as the item was held. The old instance still leaves and
        // the new one still takes its place here (its deactivation hook, its effects, its stats); only the removal packet
        // is withheld, so the add that follows is delivered as the replace the client's own handler is written for.
        // The client's replace handler (NpcBuffReplace, which asserts the slot is active) writes the slot's timers and
        // restarts its presentation in place, so the replacing add goes out as that packet with the removal withheld; this keeps a buff
        // the script re-adds every period up continuously.
        var replacedSlot = existing?.Slot ?? -1;
        // Batch 478 (Reconstruction): a same-caster renewal of a taunt is not an end and a begin of the taunt. The owner's Dominion log had the
        // guardian's taunt renewed every ~0.24 s by this replacing add (BuffReplace 0x32); each renewal ran the end hook (march, a new route) and
        // the begin hook (taunted, a new route) within 10 ms. The renewed taunt skips both hooks; its end and begin run when the taunt truly ends or begins.
        // Scope: minions only (the owner's finding). A hero's or a monster's taunt renewal keeps its hooks as before (map 1 and 2 packets).
        var renewsTaunt = existing is { Type: BuffType.Taunt } && existing.CasterNetId == casterNetId && unit.UnitType == UnitType.Minion;
        if (existing is not null)
        {
            _renewingTaunt = renewsTaunt;
            try { RemoveBuff(unit, existing.Slot, emitRemovalPacket: false); }
            finally { _renewingTaunt = false; }
            if (renewsTaunt) _tauntRenewals.Add(unit.NetId);
        }
        if (overlaps)
        {
            var cap = Math.Clamp(maxStack ?? OverlapNameCap, 1, OverlapNameCap);
            var live = unit.Buffs.Where(buff => buff.Name == name).ToList();
            if (live.Count >= cap)
            {
                // Reconstruction: the client has no packet for a cap eviction, so it is treated as the replacing add above. The new instance
                // takes the victim's slot and the victim's removal packet is withheld; the add then reaches the client as a renewal of the copy
                // it holds (ReplicationDelivery.AsHeldBuffUpdate). Without this a script's MaxStack-less overlapping add, whose default cap is
                // one (BbNativeHandlers.SpellBuffAdd), removed and re-added the buff in one flush on every add (Cassiopeia's Miasma Slow).
                var victim = live.OrderBy(buff => buff.ExpiresAt).First();
                replacedSlot = victim.Slot;
                RemoveBuff(unit, victim.Slot, emitRemovalPacket: false);
            }
        }
        // The replacement takes the slot of the instance it replaces: its client copy is still standing there (no removal
        // packet was sent for it), and the client removes whatever occupies the slot of a RemoveBuff packet.
        // An add of a buff that the same hook run cleared takes the slot the clear held (see RemoveBuffForRenewal).
        if (replacedSlot < 0 && TakeRenewalSlot(unit, name, out var renewedSlot)) replacedSlot = renewedSlot;
        var slot = replacedSlot >= 0 ? replacedSlot : FirstFreeSlot(unit, name);
        if (slot < 0 && (overlaps || failIfFull))
        {
            // Reconstruction: the original server's reaction to a full 64-slot table is unrecovered, and the client's buff manager has no
            // policy (the server assigns slots). The emulator never throws from gameplay: an overlapping add drops the oldest instance
            // of its own name (or itself when there is none); any other add that must succeed drops the buff with the least time left.
            var victim = overlaps
                ? unit.Buffs.Where(buff => buff.Name == name && buff.Slot is >= 0 and < MaxBuffSlots)
                    .OrderBy(buff => buff.ExpiresAt).FirstOrDefault()
                : unit.Buffs.Where(buff => buff.Slot is >= 0 and < MaxBuffSlots)
                    .OrderBy(buff => buff.ExpiresAt).FirstOrDefault();
            if (victim is not null)
            {
                RemoveBuff(unit, victim.Slot);
                slot = FirstFreeSlot(unit, name);
            }
        }
        if (slot < 0)
        {
            _tauntRenewals.Remove(unit.NetId); // the renewal found no slot: a later taunt is a true begin
            return null;
        }

        var buff = new BuffState(name, payload, type, slot, duration, casterNetId, 1, maxStack);
        if (overlaps)
        {
            // Batch 399 (Reconstruction): the overlapping instances of one name from one caster share the client slot of the first of them
            // (BuffState.WireSlot). Each keeps its own slot here, timer and effects; the wire shows one icon with the live count.
            buff.Overlapping = true;
            var group = unit.Buffs.FirstOrDefault(member => member.Overlapping && member.HasName(name) && member.CasterNetId == casterNetId);
            if (group is not null) buff.WireSlot = group.WireSlot;
        }
        var movementChanged = ChangesMovementSpeed(payload);
        ApplyBuffStats(unit, payload);
        unit.Buffs.Add(buff);
        // The item-slow family selects its magnitude by name (the Spells/ItemSlow.luaobj cascade). A row that
        // lands while its ItemSlow marker is live changes that selection, and the marker's own script is not
        // polled again until its next update: re-run the mirror of that cascade now and rebuild movement when
        // the penalty the marker carries moved. A row with no live marker slows nothing on its own.
        var itemSlowPenaltyChanged = false;
        if (IsItemSlowRow(name))
        {
            var marker = unit.Buffs.FirstOrDefault(candidate => candidate.Name == "ItemSlow");
            var penaltyBefore = marker?.Payload.MoveSpeedMult ?? 0.0;
            _services.Items.SyncItemSlow(unit);
            itemSlowPenaltyChanged = marker is not null && marker.Payload.MoveSpeedMult != penaltyBefore;
            movementChanged |= itemSlowPenaltyChanged;
        }
        // Fear is forced walking, not a knockback. A later movement lock
        // retires only that walking segment; the Fear buff keeps its own
        // remaining lifetime and may choose a new point after the lock ends.
        var cancelledFearWalk = unit.ForcedMove is { Effect: "Fear" } &&
            BlocksFearLocomotion(unit);
        if (cancelledFearWalk)
        {
            unit.RetainLaneRouteForControl();
            unit.ForcedMove = null;
            unit.Waypoints.Clear();
            unit.Target = null;
            if (IsLaneMinion(unit))
                unit.AnchorPath = [_clientAnchor(unit.Position)];
        }
        // A still-attached forced move owns its client route until its spell
        // callback retires it. This also covers an unrelated buff added while
        // Fear is walking; an actual new movement lock cleared Fear above.
        var forcedMoveOwnsWaypoint = unit.ForcedMove is not null;
        if (unit.UnitType != UnitType.Hero && movementChanged)
            RecomputeMovementSpeed(unit);
        if (!wasRooted && IsRooted(unit))
        {
            unit.RetainLaneRouteForControl();
            unit.Waypoints.Clear();
            unit.Target = null;
            if (!forcedMoveOwnsWaypoint) SendControlStop(unit);
        }
        if (name == "ZhonyasRingShield" && _services.Effects.IsWireReady(unit))
            _services.Effects.PauseAnimation(unit, true);
        if (type == BuffType.Stun && unit.UnitType != UnitType.Hero &&
            _services.Effects.IsWireReady(unit))
        {
            unit.RetainLaneRouteForControl();
            unit.Waypoints.Clear();
            unit.Target = null;
            if (!forcedMoveOwnsWaypoint) SendControlStop(unit);
        }
        // Batch 320 item 0: a hero's stun stops the walk and cancels the pending cast when it starts, not on every buff added while it
        // lasts: since batch 314 a CanCastWhileDisabled spell (Remove Scurvy, Quicksilver Sash, Cleanse) can be pending under the stun, and
        // any later add cancelled it. The client's setters tell their listener only when a CanAttack/CanCast/CanMove bit changes
        // (the old low bits are compared and the call skipped when equal).
        var heroStunStarts = unit.UnitType == UnitType.Hero && !wasStunned && IsStunned(unit);
        if (heroStunStarts)
        {
            unit.Waypoints.Clear();
            unit.Target = null;
            unit.AttackTarget = null;
            _services.Casts.CancelPendingCast(unit);
            if (!forcedMoveOwnsWaypoint) SendControlStop(unit);
        }
        // Snare and Stun already sent the stationary waypoint above when they started. Other
        // movement locks (for example suppression) need that correction when
        // they interrupt a previously published Fear segment.
        if (cancelledFearWalk && !(!wasRooted && IsRooted(unit)) &&
            !(type == BuffType.Stun && unit.UnitType != UnitType.Hero) &&
            !heroStunStarts)
            SendControlStop(unit);
        // Like minionLib OnFearBegin's TurnOffAutoAttack(STOPREASON_IMMEDIATELY),
        // every unit, not only heroes, drops a positive wind-up and tells
        // observers to stop the attack (StopAttackForHardControl); launched
        // projectiles continue.
        if (!wasHardControlled && IsHardControlled(unit)) StopAttackForHardControl(unit);
        if (unit.UnitType == UnitType.Hero &&
            (BuffNeedsFullRefresh(payload) || itemSlowPenaltyChanged)) _heroStats.Refresh(unit);

        if (_services.Effects.SendBuffAdd(unit, buff, 1))
        {
            _services.Effects.SendDamageShieldDelta(unit, payload.DamageShield);
            _services.Effects.EnsureBuffFx(unit, buff);
            if (unit.UnitType == UnitType.Hero) _heroStats.Push(unit);
            else if (movementChanged) _services.Effects.SendMovementSpeed(unit);
        }
        // Batch 478: a renewal (the superseded taunt of the same caster, see above) keeps the taunt it had; no begin hook.
        if (type == BuffType.Taunt && !_tauntRenewals.Remove(unit.NetId))
        {
            Minions?.OnTauntBegin(unit);
            Neutrals?.OnTauntBegin(unit);
            AiScripts?.OnTauntBegin(unit);
            Heroes?.OnTauntBegin(unit);
            Spawned?.OnTauntBegin(unit);
            Pets?.OnTauntBegin(unit);
        }
        else if (type == BuffType.Fear || name == "Fear" || payload.FearOrigin is not null)
        {
            Minions?.OnFearBegin(unit, buff);
            Neutrals?.OnFearBegin(unit, buff);
            // The pet scripts take the pair without the buff object (OnFearBegin() only reads
            // GetFearLeashPoint()); Hero.luaobj's OnFearBegin takes the same pair.
            Heroes?.OnFearBegin(unit);
            // Aggro.luaobj's OnFearBegin takes the same empty pair and arms its own TimerFeared;
            // Idle.luaobj defines no fear hook, so a raised one is a no-op for it.
            Spawned?.OnFearBegin(unit);
            Pets?.OnFearBegin(unit);
        }
        return buff;
    }

    // Batch 390: the slots buffs left in the current tick (RemoveBuffNow). A same-name add in that tick may take one (FirstFreeSlot).
    // Entries of earlier ticks are pruned when the next one is recorded.
    private readonly List<LeftSlot> _leftThisStep = new();
    private sealed record LeftSlot(int Tick, uint Unit, string Name, int Slot);

    // Batch 478: units whose taunt was renewed by a same-caster replacing add; the renewing add raises no begin hook (see AddBuff).
    private readonly HashSet<uint> _tauntRenewals = new();

    // Batch 478: set while AddBuff withdraws a taunt that the same-caster add replaces; RemoveBuffNow then raises no end hook.
    private bool _renewingTaunt;

    // A script clear whose removal packet waits for the end of the hook run that made it (RemoveBuffForRenewal). The slot of each one stays
    // reserved until its packet goes out or a re-add takes the slot.
    private readonly List<DeferredRemoval> _renewalRemovals = new();

    // Frame: the hook run of a script clear, or null for an expiry held by the wire grace (Tick: the step it expired in).
    private sealed record DeferredRemoval(Entity Unit, BuffState Buff, int Slot, object? Frame, int Tick, bool Reserved);

    // The step counter of the wire grace, advanced by EndTick.
    private int _tick;

    // Slots of buffs that have left the unit but whose removal packet has not been sent yet. The deactivation hook of a script buff
    // may add another buff to the same unit (StarcallDamage adds Starcall); that add must not take the slot of the buff that is
    // still being removed: the client removes whatever occupies the slot of a RemoveBuff packet (the client's buff manager
    // looks the slot up and never compares the name hash), so Starcall in slot 0 followed by "remove slot 0" erased it.
    private readonly HashSet<(uint Unit, int Slot)> _retiringSlots = new();

    private bool RemoveBuffNow(Entity unit, BuffState buff, int slot, bool emitWire, bool emitMovementDelta,
        bool emitRemovalPacket = true)
    {

        var couldMove = !IsRooted(unit) && !IsHardControlled(unit);
        var couldAttack = CharacterState.Of(unit).CanAttack;
        var guardianRevival = buff.Name == "GuardianAngel" && unit.Alive;
        var movementSpeedBefore = unit.Speed;
        var movementChanged = ChangesMovementSpeed(buff.Payload);
        // The item-slow family is recognized by name (Spells/ItemSlow.luaobj's cascade): the shipped rows
        // carry no payload at all, and the marker itself holds the penalty rather than a magnitude field.
        var itemSlowContribution = IsItemSlowRow(buff.Name) || buff.Name == "ItemSlow";
        ReverseBuffStats(unit, buff.Payload);
        unit.Buffs.Remove(buff);
        // Batch 390: the slot this buff leaves is the one a same-name add in this tick may take (FirstFreeSlot).
        _leftThisStep.RemoveAll(entry => entry.Tick != _tick);
        _leftThisStep.Add(new LeftSlot(_tick, unit.NetId, buff.Name, slot));
        // Item slow payloads are selected by SyncItemSlow below. Defer their
        // recomputation until that selection so one final delta is emitted.
        if (unit.UnitType != UnitType.Hero && movementChanged && !itemSlowContribution)
            RecomputeMovementSpeed(unit);

        _services.Champions.OnBuffRemoved(unit, buff);
        if (buff.Type == BuffType.Fear || buff.Payload.FearOrigin is not null ||
            buff.Name is "Fear" or "MoveAwayCollision")
        {
            var expectedEffect = buff.Name == "MoveAwayCollision" ? buff.Name : "Fear";
            // A knock-back is not cut at its buff's end: the shipped MoveAwayCollision's OnBuffDeactivate only gives CanMove, CanAttack
            // and CanCast back, and the client walks its dash to the end, so a move still running (Explosive Cask: 0.5 s of a 1 s dash)
            // goes on to its wire end, where its owner retires it and the displaced minion's held order goes out (batch 263).
            if (unit.ForcedMove?.Effect == expectedEffect &&
                (expectedEffect == "Fear" || unit.ForcedMove.Completed))
            {
                unit.ForcedMove = null;
                // Fear/MoveAwayCollision can expire before the movement tick
                // that would normally observe a completed forced move.  Give
                // minion prediction the actual post-control body as its new
                // fixed origin instead of retaining the pre-fear route.
                if (IsLaneMinion(unit))
                    unit.AnchorPath = [_clientAnchor(unit.Position)];
                if (emitWire && _services.Effects.IsWireReady(unit)) _services.Effects.SendWaypoints(unit);
            }
        }
        if (emitWire) _services.Effects.SendDamageShieldDelta(unit, -buff.Payload.DamageShield);
        if (emitWire && buff.Name == "ZhonyasRingShield" && _services.Effects.IsWireReady(unit))
            _services.Effects.PauseAnimation(unit, false);
        if (buff.Name == "ItemSlow")
        {
            // The marker is the family's only speed contribution; its self-bound rows leave with it.
            foreach (var internalBuff in unit.Buffs.Where(candidate => IsItemSlowRow(candidate.Name)).ToArray())
                RemoveBuffCore(unit, internalBuff.Slot, emitWire, emitMovementDelta: false);
        }
        if (itemSlowContribution)
        {
            _services.Items.SyncItemSlow(unit);
            // SyncItemSlow may clear a selected contribution even when the
            // removed payload had no movement field of its own. Rebuild every
            // non-hero item-family transition from the captured baseline.
            if (unit.UnitType != UnitType.Hero)
                RecomputeMovementSpeed(unit);
        }
        if (emitWire)
        {
            _services.Effects.KillSpellEffect(buff.Payload.FxNetId);
            _services.Effects.KillSpellEffect(buff.Payload.ExtraFxNetId);
            foreach (var bubbleId in buff.Payload.VisionBubbles)
                _services.Effects.RemovePerceptionBubble(unit, bubbleId);
        }
        // An item-family transition rewrites the penalty the ItemSlow marker carries (SyncItemSlow above)
        // without an incremental Apply/Reverse pair, so it needs the same full rebuild a stat payload does.
        if (unit.UnitType == UnitType.Hero &&
            (BuffNeedsFullRefresh(buff.Payload) || itemSlowContribution)) _heroStats.Refresh(unit);

        var sent = emitWire && emitRemovalPacket && _services.Effects.SendBuffRemove(unit, buff);
        if (sent)
        {
            if (unit.UnitType == UnitType.Hero && !guardianRevival) _heroStats.Push(unit);
        }
        // Some item slow contributions are selected by SyncItemSlow after the
        // buff has been removed. Emit one final NPC speed delta after that
        // selection so removing the last slow cannot lose its restoration.
        if (emitMovementDelta && emitWire && IsMovementUnit(unit) && movementSpeedBefore != unit.Speed &&
            _services.Effects.IsWireReady(unit))
            _services.Effects.SendMovementSpeed(unit);
        if (guardianRevival)
        {
            // GuardianAngel.luaobj OnBuffDeactivate opens with BBIncHealth(Owner, 750, HealerVar = Owner),
            // then BBIncPAR(+375 mana). A script heal: IncHealth announces it (UnitApplyHeal) and the
            // heal event credits TOTAL_HEAL / TOTAL_UNITS_HEALED to the healer, here the revived hero.
            Healing.Apply(unit, buff.Payload.ReviveHp, unit);
            if (UsesMana(unit))
                unit.Mana = Math.Min(unit.MaxMana, unit.Mana + buff.Payload.ReviveMp);
            if (emitWire) _services.Effects.CompleteGuardianRevival(unit);
            AddBuff(unit, "HasBeenRevived", BuffType.CombatEnhancer, 300.0,
                unit.NetId, canMitigateDuration: false);
        }
        if (unit.Alive && IsLaneMinion(unit))
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) Minions?.OnTauntEnd(unit);
            else if (buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
            {
                var remainingFear = unit.Buffs.LastOrDefault(active => active.Type == BuffType.Fear ||
                    active.Name == "Fear" || active.Payload.FearOrigin is not null);
                if (remainingFear is null) Minions?.OnFearEnd(unit);
                else Minions?.OnFearBegin(unit, remainingFear);
            }
            else if (!unit.Buffs.Any(active => active.Type == BuffType.Fear ||
                         active.Name == "Fear" || active.Payload.FearOrigin is not null))
            {
                if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) Minions?.OnCanMove(unit);
                if (!couldAttack && CharacterState.Of(unit).CanAttack)
                    Minions?.OnCanAttack(unit);
            }
        }
        if (unit.Alive && unit.Kind == EntityKind.Neutral)
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) Neutrals?.OnTauntEnd(unit);
            else if (buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
            {
                var remainingFear = unit.Buffs.LastOrDefault(active => active.Type == BuffType.Fear ||
                    active.Name == "Fear" || active.Payload.FearOrigin is not null);
                if (remainingFear is null) Neutrals?.OnFearEnd(unit);
                else Neutrals?.OnFearBegin(unit, remainingFear);
            }
        }
        // The same release path for a unit whose AI is a script (Turret.luaobj answers OnTauntEnd,
        // OnCanMove and OnCanAttack). Fear has its own script callbacks and no turret script defines
        // them, so a fear removal is left to the unit's own handlers.
        if (unit.Alive && AiScripts is { } ai && ai.Drives(unit))
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) ai.OnTauntEnd(unit);
            else if (buff.Type != BuffType.Taunt && buff.Type != BuffType.Fear && buff.Name != "Fear" && buff.Payload.FearOrigin is null &&
                     !unit.Buffs.Any(active => active.Type == BuffType.Fear ||
                          active.Name == "Fear" || active.Payload.FearOrigin is not null))
            {
                if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) ai.OnCanMove(unit);
                if (!couldAttack && CharacterState.Of(unit).CanAttack) ai.OnCanAttack(unit);
            }
        }
        // The same release path for a champion's Hero.luaobj (batch 163): it answers OnTauntEnd
        // (taking the taunt target again, or standing down when there is none) and OnFearEnd (its
        // own TimerFeared wander stops with it), and defines neither OnCanMove nor OnCanAttack, so
        // a plain release raises nothing for it. A fear that leaves another fear restarts the
        // wander instead of ending it, as the pet path does.
        if (unit.Alive && Heroes is { } heroes && heroes.Drives(unit))
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) heroes.OnTauntEnd(unit);
            else if (buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
            {
                var remainingFear = unit.Buffs.LastOrDefault(active => active.Type == BuffType.Fear ||
                    active.Name == "Fear" || active.Payload.FearOrigin is not null);
                if (remainingFear is null) heroes.OnFearEnd(unit);
                else heroes.OnFearBegin(unit);
            }
        }
        // The same release path for the units a BBSpawnMinion gave idle.lua or Aggro.lua (batch 163).
        // Aggro.luaobj answers all four (OnTauntEnd re-takes the taunt target or runs FindTargetOrMove,
        // OnFearEnd stops its own TimerFeared and runs FindTargetOrMove, OnCanMove/OnCanAttack reset to
        // AI_IDLE and run it again); Idle.luaobj answers OnTauntEnd, OnCanMove and OnCanAttack with
        // nothing and defines no fear hook, so a raised one is a no-op for it. A fear that leaves
        // another fear restarts the wander instead of ending it, as the pet path does.
        if (unit.Alive && Spawned is { } spawned && spawned.Drives(unit))
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) spawned.OnTauntEnd(unit);
            else if (buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
            {
                var remainingFear = unit.Buffs.LastOrDefault(active => active.Type == BuffType.Fear ||
                    active.Name == "Fear" || active.Payload.FearOrigin is not null);
                if (remainingFear is null) spawned.OnFearEnd(unit);
                else spawned.OnFearBegin(unit);
            }
            else if (!unit.Buffs.Any(active => active.Type == BuffType.Fear ||
                         active.Name == "Fear" || active.Payload.FearOrigin is not null))
            {
                if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) spawned.OnCanMove(unit);
                if (!couldAttack && CharacterState.Of(unit).CanAttack) spawned.OnCanAttack(unit);
            }
        }
        // The same release path for a pet's shipped script (Pet.luaobj answers OnTauntEnd, OnFearEnd,
        // OnCanMove and OnCanAttack; UncontrollablePet.luaobj and YorickPHPet.luaobj define the same
        // pair). A fear that leaves another fear on the unit restarts the wander instead of ending it,
        // as the minion path does.
        if (unit.Alive && Pets is { } pets && pets.Drives(unit))
        {
            if (buff.Type == BuffType.Taunt && !_renewingTaunt) pets.OnTauntEnd(unit);
            else if (buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
            {
                var remainingFear = unit.Buffs.LastOrDefault(active => active.Type == BuffType.Fear ||
                    active.Name == "Fear" || active.Payload.FearOrigin is not null);
                if (remainingFear is null) pets.OnFearEnd(unit);
                else pets.OnFearBegin(unit);
            }
            else if (!unit.Buffs.Any(active => active.Type == BuffType.Fear ||
                         active.Name == "Fear" || active.Payload.FearOrigin is not null))
            {
                if (!couldMove && !IsRooted(unit) && !IsHardControlled(unit)) pets.OnCanMove(unit);
                if (!couldAttack && CharacterState.Of(unit).CanAttack) pets.OnCanAttack(unit);
            }
        }
        return true;
    }

    // gcd_PercentAttackSpeedModMinimum (-0.95) as a multiplier, the one floor every unit class shares (MapConstants).
    private static double AttackSpeedFloor => GameData.Default.LevelConstants.AttackSpeedMultiplierFloor;
}
