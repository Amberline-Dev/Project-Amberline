using System.Runtime.CompilerServices;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Script buffs on the existing buff system. Creation, stacking, renewal, removal and expiry stay with
/// <see cref="BuffLifecycle"/> (reached through <see cref="ISpellBuffService"/>); this class adds what the shipped scripts
/// carry on top of it: the instance variables and the hooks <c>OnBuffActivate</c>, <c>OnBuffDeactivate</c>,
/// <c>BuffOnUpdateActions</c>, <c>BuffOnUpdateStats</c>, <c>BuffOnPreDamage</c>, <c>BuffOnDeath</c>, <c>BuffOnAllowAdd</c>,
/// <c>BuffOnBeingSpellHit</c>, <c>BuffOnHitUnit</c> and <c>BuffOnSpellCast</c>.
/// Hook timing is a Reconstruction (the stock client has no BuffOn* strings; the server dispatch is not recoverable):
/// see docs/scripting/building-blocks-engine.md.
/// </summary>
internal sealed class BbBuffRuntime
{
    // Batch 203: NativeSystemBuffs is gone. It held APBonusDamageToTowers (the shipped CharOnActivate add, the tower bonus and
    // FortifyCheck, now runs from the script on every hero and the Hallucinate clone) and DisconnectTarget (added by DisconnectTimer's
    // shipped BuffOnUpdateStats to the RunToMe placemarker, now runs too). Nothing refuses a shipped add any more.

    /// <summary>
    /// The buff <c>BBBreakSpellShields</c> adds with the duration 37037 (<c>BuffOnAllowAdd</c> of every spell shield reads that
    /// duration, pops, and refuses the add). It is never a real buff: the add breaks the target's shields instead.
    /// </summary>
    public const string SpellShieldMarker = "SpellShieldMarker";
    internal const double SpellShieldMarkerDuration = 37037.0;

    /// <summary>
    /// How often a live buff's <c>BuffOnUpdateStats</c> is polled: every 0.25 seconds, not every buff update.
    /// <para>
    /// The shipped scripts are written to that grid. <c>OrianaSlow</c> / <c>OrianaHaste</c> set <c>TicksLeft = 8</c> in
    /// <c>OnBuffActivate</c> and take one off per stats call while <c>OrianaDissonanceWave</c> adds them with
    /// <c>Duration 2.0</c> and <c>TickRate 0.0</c>: eight calls across two seconds is 0.25 s each, and the ramp
    /// (<c>ElapsedRatio = TicksLeft / 8</c>, no clamp at zero) only stays a fade at that spacing — polled faster it runs
    /// <c>TicksLeft</c> below zero and the slow becomes a haste. <c>SummonerReviveSpeedBoost</c> steps its
    /// <c>MoveSpeedMod</c> down by 0.045 per call from 2.2 over a <c>Duration 12.0</c>: 12 / 0.25 = 48 calls for
    /// 2.2 - 48 * 0.045 = 0.04 left, and its own clamp takes the rounding below zero back to 0.
    /// </para>
    /// <para>
    /// The interval is therefore script evidence, not an assumption (docs/research/patch126-partial-rules-evidence.md used
    /// to hold that no 0.25 s interval was recovered and that Revive must not rest on one). What is still open is the
    /// boundary count: whether retail's sample at the add and the one on the update that ends the buff are both part of
    /// those 48 (48 vs 49 calls, and the 0.04 the shipped arithmetic leaves over), and whether the whole buff update is
    /// itself 0.25 s — this engine keeps <c>BuffOnUpdateActions</c> on the simulation step with its own TickRate gate,
    /// which is what its two native stamps suggest but the client's tick loop is not in the shipped files.
    /// </para>
    /// <para>
    /// The grid is counted on the game clock from the sample the add or renewal took
    /// (<see cref="BbBuffInstance.StatsNextAt"/>), so a sample lands on that clock + k * 0.25 and a
    /// <c>BBExecutePeriodically</c> inside the stats hook — Janna's <c>ReapTheWhirlwind</c>, <c>SadismHeal</c> and
    /// <c>CatalystHeal</c> at 0.5 s, <c>FeastMarker</c>, <c>PowerBall</c>, <c>KogMawIcathianSurprise</c> and
    /// <c>WallofPainTarget</c> at 0.25 s, <c>Wither</c> on its <c>TickTimeVar</c> — meets its due line on the sample
    /// that reaches it (they all stamp due times on that same clock, <c>IBbHost.GetTime</c>).
    /// </para>
    /// </summary>
    internal const double StatsUpdateInterval = 0.25;

    /// <summary>
    /// How often a live buff's bare <c>BuffOnUpdateActions</c> runs when the add carried no <c>TickRate</c>: every 0.25 seconds,
    /// the same grid as <see cref="StatsUpdateInterval"/>.
    /// <para>
    /// The two hooks are the two stamps the native keeps beside one <c>mTickTimeDelay</c> (<c>mLastTickTimeUpdateActions</c>,
    /// <c>mLastTickTimeUpdateStats</c>), so they share the buff's tick, and the shipped scripts are written to a quarter second of
    /// it:
    /// </para>
    /// <list type="bullet">
    /// <item><c>PickACard.BuffOnUpdateActions</c> walks <c>Counter</c> by 1 a call and changes the slot only on 2, 4 and 6 (red,
    /// gold, blue), so one full shuffle is three calls. Before this the engine ran the hook every 33 ms step: a card every 66 ms
    /// and a full round in 0.2 s, far too fast to lock one by hand (owner finding 20, 20 October 2026). At 0.25 s a card is a
    /// quarter second. Batch 175 added the other half of the same arithmetic: the seed is even and the turns are on 2/4/6, so the
    /// first run belongs one interval after the add or the seeded card is shown for half a card. The owner's "the card changes very
    /// rapidly on first use, then behaves normally" was the due line being repaid a step at a time whenever it had fallen behind
    /// (a stall, a long step); it is now measured from the run that happened, so it cannot fall behind at all.</item>
    /// <item><c>RivenTriCleaveDamage</c> (1 s, added by <c>RivenTriCleave.SelfExecute</c> just before its <c>BBMove</c>) deals its
    /// sweep damage from this hook on every call. Polled every step, the dash's own flight (about 0.28 s) carried several hits per
    /// unit; the shipped <c>RivenTriCleaveDamageDebuff</c> marks each unit for 1 s so a unit is hit once per strike by its own
    /// design, but the sweep itself re-ran whole-step and the per-strike damage window shifted with the frame rate.</item>
    /// <item><c>AbsoluteZero.luaobj</c> reads <c>LifeTime</c> in this hook and divides by it (its slow ramps by the time the
    /// buff has lived); polled 30 times a second the ramp is a staircase of 33 ms steps the client would draw, polled four times a
    /// second it is the ramp the script's own arithmetic states.</item>
    /// </list>
    /// <para>
    /// The quarter second is therefore script evidence for the update-actions hook too, not only for the stats one. The odd one
    /// out is the native's <c>mTickTimeDelay</c>, which the constructor zeroes and which no recovered assignment sets: that the
    /// buff update itself is a quarter second is Reconstruction, the arithmetic above being the evidence. What stays open is the
    /// boundary count against a buff's own end (see <see cref="StatsUpdateInterval"/>) and whether the two hooks share one stamp
    /// or keep their own (the native keeps two, so this engine keeps two clocks: the stats hook on the 0.25 s line the add set, the
    /// update-actions hook, since batch 720, on its holder's 0.25 s pass, the unit's line of <c>SpellSystem.SelfUpdateDue</c>).
    /// A hook that does have a <c>TickRate</c> keeps its own period unchanged: the quarter second replaces only the "every buff
    /// update" default, which was the simulation step.
    /// </para>
    /// <para>
    /// No backlog is replayed. A line the clock has passed is skipped: the next line is the first grid line after the run, not the
    /// old line plus one interval, so the hook runs at most once per interval and never more often than its schedule. The catch-up
    /// burst of a stale line (the first press after a reload, a stalled step, a pause) turned the deck at the step rate until the
    /// debt was gone, which is what the owner saw as "the card changes very rapidly on first use, then behaves normally". A buff
    /// whose whole life is shorter than one interval takes its single run at its first update instead
    /// (<see cref="BbBuffInstance.ActionsFirstLinePending"/>).
    /// </para>
    /// <para>
    /// It is the <em>bare</em> hook that runs on this tick: one that schedules a <c>BBExecutePeriodically</c> of its own is the
    /// periodic's caller, and the due test lives inside the hook on the host clock. Such a hook keeps the every-update poll
    /// (<see cref="BbBuffInstance.ActionsCarriesPeriodic"/>), or the script's period never resolves — Tryndamere's Spinning Slash
    /// scans every 0.1 s, the Bushwhack trap arms after 0.9, Annie's burning pulses every 1.0, Brand's blaze every 1.05, Yorick's
    /// decay every 0.9 and his cloud every 0.5. Batches 143 to 159 shipped those scripts on the every-update cadence and their
    /// checks pin the lines; the shipped period, not the poll, is what those hooks are written to.
    /// </para>
    /// </summary>
    internal const double ActionsUpdateInterval = 0.25;

    /// <summary>
    /// The extra seconds the wire copy of a buff one of the two periodic self-buff polls re-adds is given over the
    /// script's own duration (<see cref="Core.Buffs.BuffPayload.WireDurationMargin"/>). It has to be positive or the
    /// client's copy of 3152's WillOfTheAncients (duration 1.0, period 1.0) runs out at the instant the next re-add is
    /// generated, and it has to stay under half the script's duration or the renewal throttle above holds the re-add
    /// back and the copy lapses for the opposite reason.
    /// </summary>
    internal const double PollWireMargin = 0.25;

    // Reconstruction (wire grace): one 30 Hz server step. A renewal-type add's client copy runs this long past the script's duration, so the
    // periodic re-add that follows the server's expiry by one step still finds the copy it renews (BuffLifecycle.TickBuffs).
    internal const double WireGraceStep = 1.0 / 30.0;

    /// <summary>
    /// Batch 388: the add types whose wire copy carries one step of margin, so the server's expiry of a copy that a re-add follows is held one
    /// step (BuffLifecycle.TickBuffs). BUFF_STACKS_AND_OVERLAPS joins them: a script that re-applies an overlapping buff every period (MissFortune's
    /// Slow, the Rumble carpet bomb missile) let an instance expire in the step of the next application, and the flush carried the remove and the
    /// add of one name (sweep BUF-FLUSH). Reconstruction: how retail's server times an overlapping expiry is not in the client.
    /// </summary>
    internal static bool ReplicatesWithGraceMargin(BbBuffAddType addType) =>
        addType is BbBuffAddType.BUFF_RENEW_EXISTING or BbBuffAddType.BUFF_REPLACE_EXISTING or BbBuffAddType.BUFF_STACKS_AND_RENEWS
            or BbBuffAddType.BUFF_STACKS_AND_OVERLAPS;

    /// <summary>Batch 388: an instant marker, whose duration is no longer than one wire step: its copy would live for a frame at most.</summary>
    internal static bool IsInstantMarker(double duration) => duration <= WireGraceStep + 1e-9;

    private readonly BbSpellHost _host;
    private readonly BbScriptSet _scripts;
    private readonly BbHookRouter _router;
    private readonly Dictionary<BuffState, BbBuffInstance> _instances = new(ReferenceEqualityComparer.Instance);
    // Reconstruction (batch 433): the OnBuffDeactivate hooks of buffs a missile-end dispatch removes wait for that dispatch to end (DeferDeactivations).
    private readonly List<(BbBuffInstance Instance, bool Expired)> _pendingDeactivations = new();
    private int _deferredDeactivations;
    private Entity? _retiring;

    public BbBuffRuntime(BbSpellHost host, BbScriptSet scripts, BbHookRouter router)
    {
        _host = host;
        _scripts = scripts;
        _router = router;
    }

    public IReadOnlyCollection<BbBuffInstance> Instances => _instances.Values;
    public BbBuffInstance? InstanceOf(BuffState state) => _instances.TryGetValue(state, out var instance) ? instance : null;

    /// <summary>
    /// The server's buff type for a script <c>BUFF_*</c> number. The script constants and the 0xBF type byte are one enum (the stock
    /// client registers BUFF_Stun = 4, BUFF_Silence = 6, BUFF_Slow = 9, BUFF_Fear = 0x14 ... and stores the wire byte unmapped), so
    /// the number passes through. (Before 18 October 2026 this added one from 4 up to reach an AntiBot-numbered wire enum.)
    /// </summary>
    public static BuffType ServerBuffType(double scriptType) => (BuffType)(int)scriptType;

    // ------------------------------------------------------------------------------------------------------------
    // Adding and removing
    // ------------------------------------------------------------------------------------------------------------

    private static bool IsNonDispellable(BbScript script) =>
        script.Global("NonDispellable") is BbValue.Bool { Value: true } || script.Global("Nondispellable") is BbValue.Bool { Value: true };

    /// <summary>
    /// The statuses a script set on each unit with no buff to hold them, with the value it wrote (batch 201). A status is a bit of the unit's
    /// state (retail keeps CanAttack, CanMove and the rest as bits), so it is no buff: it takes no slot and reaches
    /// no client. The flags of these statuses are <see cref="Entity.ScriptStatus"/>, which every status reader sees beside the buffs.
    /// </summary>
    private readonly ConditionalWeakTable<Entity, Dictionary<BbSetStatus, bool>> _scriptStatuses = new();

    /// <summary>The name of <see cref="Entity.ScriptStatus"/>. It is never sent or looked up: a status reader sees the payload, not the name.</summary>
    private const string ScriptStatusName = "ScriptStatus";

    /// <summary>
    /// Whether a <c>BBSetStatus</c> value switches the status on. The client keeps the statuses as the bits of one word and a fresh unit holds
    /// CanAttack, CanCast and CanMove, so those three and Targetable act when a script writes <c>false</c>; every other status acts when it
    /// writes <c>true</c> (CharacterState).
    /// </summary>
    internal static bool StatusActs(BbSetStatus status, bool value) =>
        status is BbSetStatus.SetCanMove or BbSetStatus.SetCanAttack or BbSetStatus.SetCanCast or BbSetStatus.SetTargetable ? !value : value;

    /// <summary>
    /// <c>BBSetStatus</c> outside a stats hook (E02). The client keeps one status bit per unit (CharacterState), with no owner and no count,
    /// so the last write sets it. Which writes release it is not in the client (Reconstruction, below). Leona's Zenith Blade shows the
    /// restore in the scripts: <c>SelfExecute</c> clears CanAttack and CanMove (<c>LeonaZenithBlade.luaobj</c>), its <c>OnBuffDeactivate</c>
    /// sets both true again, and <c>LeonaZenithBladeMissile.luaobj</c>'s <c>SpellOnMissileEnd</c> sets both true. Many shipped writers do not
    /// restore their bit (the batch 222 audit), so this engine holds the bit in two places:
    /// <list type="bullet">
    /// <item>a write that leaves the bit at its rest value releases it everywhere on the unit: the unit's script status (<see cref="SetStatusOn"/>)
    /// and the statuses any buff holds (<see cref="BbBuffInstance.HeldStatuses"/>);</item>
    /// <item>a write that switches it on is held by the running buff when the unit is its holder (every hook of the buff but its deactivation,
    /// which leaves nothing to hold it), else by the unit's script status. Reconstruction: the shipped scripts write statuses in buff hooks
    /// without restoring them (Jarvan IV's Cataclysm, Cassiopeia's Miasma and Noxious Blast, Bushwhack; 367 groups in the batch 222 audit,
    /// which refuted a one-bit reading with no holder), so retail released a buff's status writes with the buff. The client's native sets
    /// the unit's bit, but its release rule is not recoverable, so the holder here stands in for it.</item>
    /// </list>
    /// What a buff holds ends with the buff; what the script status holds lasts until a script clears the status or the unit dies.
    /// </summary>
    public void WriteStatus(Entity unit, BbSetStatus status, bool value, BbBuffInstance? running, string hook)
    {
        if (!StatusActs(status, value))
        {
            ReleaseStatus(unit, status, value);
            return;
        }
        if (running is not null && ReferenceEquals(running.Owner, unit) && hook != "OnBuffDeactivate")
        {
            running.HeldStatuses[status] = value;
            // OnBuffActivate is followed by ResampleAfterAdd, which puts these on the unit at once (BbBuffRuntime.Add restarts the stats grid) and a
            // write from an update hook is picked up by the grid's next sample (OnTick); the other hooks are sampled at once.
            if (hook is not ("OnBuffActivate" or "BuffOnUpdateActions")) Resample(running);
            return;
        }
        SetStatusOn(unit, status, value);
    }

    /// <summary>
    /// The statuses a character script's <c>UpdateSelfBuffStats</c> sample asks for (batch 174; CharScriptWorm's
    /// <c>SetCanAttack</c>/<c>SetCanMove</c> false, 0039-0057, is the shipped case). A character script's sample has no buff instance to
    /// hold them — its payload is <see cref="Entity.CharacterScriptStats"/>, which only the stat calculator reads — so a status that acts
    /// is held by the unit's script status (<see cref="SetStatusOn"/>) and one that is back at its rest value is released
    /// (<see cref="ReleaseStatus"/>), exactly the rules <see cref="WriteStatus"/> applies outside a stats hook. Retail keeps one bit per
    /// unit and the poll re-asserts it, which the every-poll call reproduces: another hook's rest write clears the bit and this sample
    /// puts it back at the next poll.
    /// </summary>
    public void ApplyCharacterScriptStatuses(Entity unit, IReadOnlyDictionary<BbSetStatus, bool> statuses)
    {
        if (statuses.Count == 0) return;
        foreach (var (status, value) in statuses)
        {
            if (!CanApplyStatus(status)) continue;
            if (StatusActs(status, value)) SetStatusOn(unit, status, value);
            else ReleaseStatus(unit, status, value);
        }
    }

    /// <summary>
    /// A status switched on for a unit that no running buff holds (a spell's hook, a buff's deactivation, a character's stats sample, a unit the
    /// script has just spawned). It is set on the unit's script status until a script writes its rest value or the unit dies, and it takes no buff
    /// slot. The control effects its add had are replayed by <see cref="BuffLifecycle.ScriptStatusGained"/>.
    /// Reconstruction: retail's death rule for a script-set bit is not in the client; a dead unit keeps none, as the batch 174 carriers ended with it.
    /// </summary>
    public void SetStatusOn(Entity unit, BbSetStatus status, bool value)
    {
        if (!StatusActs(status, value)) { ReleaseStatus(unit, status, value); return; }
        // Batch 476: a dead minion-class unit keeps the NoRender bit its script sets on it. OdinShieldRelicAura's OnBuffDeactivate applies
        // SetNoRender true after the damage that kills the relic (OdinShieldRelicAura.luaobj, after BBApplyDamage); retail keeps the bit
        // on the corpse. Dropping it left the relic drawn on the wire and its Death animation visible on the pickup. Heroes are not exempt
        // from the dead-unit rule: a hero's corpse keeps no script status.
        if (!unit.Alive && !(status == BbSetStatus.SetNoRender && unit.UnitType != UnitType.Hero)) return;
        var held = _scriptStatuses.GetValue(unit, _ => new Dictionary<BbSetStatus, bool>());
        if (held.ContainsKey(status)) return;
        var before = BuffLifecycle.ControlOf(unit);
        held[status] = value;
        RecomposeScriptStatus(unit, held);
        _host.Spells.Buffs.ScriptStatusGained(unit, before.HardControlled, before.Rooted);
        PushMinionStatus(unit, status);
    }

    /// <summary>
    /// The status flags of <c>BBSpawnMinion</c> (Stunned, Rooted, Silenced, Invulnerable, MagicImmune, the IgnoreCollision of a marker-sized
    /// unit): retail sets the new unit's status bits from the native's flags, so they are the unit's script status, held until a script writes
    /// the rest value or the unit dies, like any <see cref="SetStatusOn"/>. The minion stat push is left out: the spawn packets that follow
    /// (or the Noxious Trap's template) carry the unit's state, and the trap is created already announced.
    /// </summary>
    public void SetSpawnStatuses(Entity unit, IReadOnlyCollection<BbSetStatus> statuses)
    {
        if (statuses.Count == 0 || !unit.Alive) return;
        var held = _scriptStatuses.GetValue(unit, _ => new Dictionary<BbSetStatus, bool>());
        var before = BuffLifecycle.ControlOf(unit);
        foreach (var status in statuses) held[status] = !StatusActs(status, false);
        RecomposeScriptStatus(unit, held);
        _host.Spells.Buffs.ScriptStatusGained(unit, before.HardControlled, before.Rooted);
    }

    /// <summary>The write that leaves the bit at its rest value: the unit's script status lets go, and every buff's hold of the status with it (the single bit of the client).</summary>
    private void ReleaseStatus(Entity unit, BbSetStatus status, bool rest)
    {
        var carried = ReleaseScriptStatus(unit, status);
        foreach (var holder in _instances.Values.Where(item => ReferenceEquals(item.Owner, unit) && unit.Buffs.Contains(item.State) &&
                     item.HeldStatuses.TryGetValue(status, out var held) && StatusActs(status, held)).ToArray())
        {
            // The entry stays with the rest value: a buff with no stats hook is only re-sampled while it holds something.
            holder.HeldStatuses[status] = rest;
            Resample(holder);
        }
        if (carried) PushMinionStatus(unit, status);
    }

    /// <summary>Lets go of the unit's script status for <paramref name="status"/>; true when it held it.</summary>
    private bool ReleaseScriptStatus(Entity unit, BbSetStatus status)
    {
        if (!_scriptStatuses.TryGetValue(unit, out var scriptHeld) || !scriptHeld.ContainsKey(status)) return false;
        var before = BuffLifecycle.ControlOf(unit);
        scriptHeld.Remove(status);
        RecomposeScriptStatus(unit, scriptHeld);
        _host.Spells.Buffs.ScriptStatusLost(unit, before.CouldMove, before.CouldAttack);
        return true;
    }

    /// <summary>Rebuilds <see cref="Entity.ScriptStatus"/> from the statuses the unit holds with no buff: one payload with their flags, or null when none.</summary>
    private static void RecomposeScriptStatus(Entity unit, Dictionary<BbSetStatus, bool> held)
    {
        if (held.Count == 0)
        {
            unit.ScriptStatus = null;
            return;
        }
        var payload = new BuffPayload();
        foreach (var (status, value) in held) ApplyStatus(payload, status, value);
        unit.ScriptStatus = new BuffState(ScriptStatusName, payload, BuffType.Internal);
    }

    /// <summary>
    /// A script's change to a live minion reaches the client again. The render bits (Ghosted, GhostProof, NoRender, ForceRenderParticles) go out in
    /// the full stat block; the combat statuses (the invulnerable, magic-immune and untargetable flags, the root, disarm, stealth and silence bits) go
    /// out in the narrow combat block, so an injured unit keeps its HP (batch 359).
    /// </summary>
    private void PushMinionStatus(Entity unit, BbSetStatus status)
    {
        if (unit.UnitType == UnitType.Hero || !unit.Created || !_host.Spells.Effects.IsReferenceReady(unit)) return;
        if (status is BbSetStatus.SetGhosted or BbSetStatus.SetGhostProof or BbSetStatus.SetNoRender or BbSetStatus.SetForceRenderParticles)
            _host.Spells.Effects.SendStats(unit);
        else if (status is BbSetStatus.SetInvulnerable or BbSetStatus.SetMagicImmune or BbSetStatus.SetTargetable or BbSetStatus.SetCanMove or
                 BbSetStatus.SetRooted or BbSetStatus.SetCanAttack or BbSetStatus.SetDisarmed or BbSetStatus.SetPacified or
                 BbSetStatus.SetStealthed or BbSetStatus.SetSilenced or BbSetStatus.SetCanCast)
            _host.Spells.Effects.SendCombatState(unit);
    }

    /// <summary>The live script instance of the named buff on <paramref name="unit"/>, or null (hand-written buffs have none).</summary>
    public BbBuffInstance? Find(Entity unit, string name) =>
        _instances.Values.FirstOrDefault(item => ReferenceEquals(item.Owner, unit) &&
                                                 string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                                 unit.Buffs.Contains(item.State));

    /// <summary>
    /// <c>BBSpellBuffRemoveType</c> (the client's dispel-by-type routine): every buff of the script type
    /// on the unit that a dispel may remove. A buff marked NonDispellable stays.
    /// </summary>
    public void RemoveType(Entity unit, double scriptType)
    {
        var type = ServerBuffType(scriptType);
        // Reconstruction: a buff is not part of the sweep its own activation makes (YorickRAPetBuff2 is a BUFF_CombatEnchancer and sweeps that type from its
        // unit when it is added; the sweep takes the buffs the clone copied, not the buff that is being added). Retail's dispel is not in the client.
        var activating = _host.HasFrame && _host.Frame.Hook == "OnBuffActivate" ? _host.Frame.Buff?.State : null;
        foreach (var buff in unit.Buffs.Where(buff => buff.Type == type && !buff.Payload.NonDispellable && !ReferenceEquals(buff, activating)).ToArray())
            if (buff.Slot >= 0) _host.Spells.Buffs.Remove(unit, buff.Slot);
    }

    /// <summary>
    /// <c>BBDispellNegativeBuffs</c> (the client's body): every negative buff the unit holds goes and
    /// a <c>NonDispellable</c> one stays. Quicksilver Sash's <c>TargetExecute</c> is the only shipped call site and it
    /// passes just <c>AttackerVar=Owner</c> (the hero the use dispels).
    /// <para>
    /// Reconstruction: the client body only asserts the dispel-negative call site (<c>DispellNegative</c>),
    /// and the sweep itself is not in the exe, so the negative set is the engine's own vocabulary — the crowd-control and
    /// damage-over-time families plus <c>CombatDehancer</c>, <c>Slow</c> and <c>Poison</c>. That is the list the
    /// hand-written Quicksilver Sash dispel used, which this replaces (batch 149 step 4); it is what
    /// <c>docs/items/item-catalog-sweep.md</c> records of the shipped call, Sleep included. A positive buff and a
    /// hidden internal row stay, and so does the buff the run itself belongs to.
    /// </para>
    /// </summary>
    public void DispellNegatives(Entity unit)
    {
        var activating = _host.HasFrame && _host.Frame.Hook == "OnBuffActivate" ? _host.Frame.Buff?.State : null;
        foreach (var buff in unit.Buffs.Where(buff => NegativeTypes.Contains(buff.Type) && !buff.Payload.NonDispellable &&
                                                     !ReferenceEquals(buff, activating)).ToArray())
            if (buff.Slot >= 0) _host.Spells.Buffs.Remove(unit, buff.Slot);
    }

    private static readonly HashSet<BuffType> NegativeTypes =
    [
        BuffType.CombatDehancer, BuffType.Stun, BuffType.Silence,
        BuffType.Taunt, BuffType.Polymorph, BuffType.Slow, BuffType.Snare,
        BuffType.DamageOverTime, BuffType.Fear, BuffType.Net, BuffType.Poison,
        BuffType.Suppression, BuffType.Blind, BuffType.Sleep,
    ];

    /// <summary>The positional add <c>BBBreakSpellShields</c> makes through the base library.</summary>
    public void AddFromHost(Entity target, Entity caster, string name, double addType, double maxStack, double duration, BbTable? vars,
        BbBuffType type = BbBuffType.BUFF_Internal)
    {
        var table = new BbTable();
        if (vars is not null)
            foreach (var entry in vars.Entries) table[entry.Key] = entry.Value;
        Add(new BbBuffRequest(target, caster, name, (int)addType, (int)type, (int)maxStack, duration, 0.0, false, table));
    }

    /// <summary>Whether the script called <paramref name="name"/> defines a buff hook (a spell script with only <c>SelfExecute</c> or <c>TargetExecute</c> does not).</summary>
    public bool HasBuffHooks(string name) => _scripts.FindOrShipped(name) is { } script &&
        script.Events.Any(table => table.Hook.StartsWith("OnBuff", StringComparison.Ordinal) || table.Hook.StartsWith("BuffOn", StringComparison.Ordinal));

    /// <summary>
    /// The item hooks whose buffs are the proc of one hit given, taken or dealt: their adds are one admission group. The
    /// three damage hooks are batch 147 step 4 (Items/3116.luaobj's pair, Items/3155.luaobj's shield and its cooldown
    /// marker, which its own trigger adds on both paths).
    /// </summary>
    private static bool IsProcHook(string hook) => hook is "ItemOnHitUnit" or "ItemOnBeingHit" or
        "ItemOnPreDamage" or "ItemOnPreDealDamage" or "ItemOnDealDamage";

    /// <summary>
    /// Whether the run of <paramref name="frame"/> holds its adds and its effects until it is over: a proc hook that can
    /// make more than one add. False while the held work is being placed, so the adds and the particles they make are
    /// ordinary ones.
    /// </summary>
    public bool HoldsProcWork(BbHookFrame frame) => !frame.CommittingAdds && ProcAddsAreOneGroup(frame);

    /// <summary>
    /// Whether the run of <paramref name="frame"/> holds its adds until it is over: the proc hooks of <see cref="HoldsProcWork"/>,
    /// and an item active's own cast hook (<see cref="BbHookFrame.ItemActive"/>) when it can make more than one add. The pair one
    /// recipient of Randuin's Omen takes (Slow and BlackOmen) is one admission group this way, the way the hand-written active
    /// reserved every slot its recipient needed or applied none of it. Only the adds are held for an item active: its particles
    /// are the script's own order (the trigger troy first) and are not moved behind the buff wire. The assist markers of such a
    /// run wait with the adds (<see cref="BbHookFrame.HeldMarkers"/>), so a declined group leaves no credit either.
    /// </summary>
    public bool HoldsAdds(BbHookFrame frame) => !frame.CommittingAdds &&
        (ProcAddsAreOneGroup(frame) || (frame.ItemActive && AddSites(frame.ScriptName, frame.Hook) >= 2));

    private readonly Dictionary<(string Script, string Hook), int> _addSites = new();

    private readonly Dictionary<string, bool> _drawsActivationEffect = new();

    private readonly Dictionary<string, bool> _clientTorch = new();

    private readonly Dictionary<string, bool> _carriesForeignTemplate = new();

    private readonly Dictionary<string, bool> _clearsOnActivate = new();

    /// <summary>
    /// Whether the buff template of <paramref name="script"/> is Riot's copy-paste of another spell's, so that a client
    /// which loads the script as a buff draws that other spell's visual for the add. The signature read here is the
    /// Morgana's Q triple the shipped data carries in twelve scripts: <c>BuffName='Dark Binding'</c>,
    /// <c>BuffTextureName='FallenAngel_DarkBinding.dds'</c> and <c>AutoBuffActivateEffect='DarkBinding_tar.troy'</c>
    /// together (<c>DarkBinding</c>, <c>DarkBindingMissile</c>, <c>BrandBlazeMissile</c>, <c>CaitlynEntrapment</c>,
    /// <c>EzrealMysticShotMissile</c>, <c>HowlingGaleSpell</c> and its <c>1</c>..<c>4</c> rows,
    /// <c>ItemPlacementMissile</c>, <c>UrgotSwapMissile2</c>). Only the <c>DarkBinding</c> family is Morgana's own:
    /// its template is its visual (that is what her Q's snare shows), so those names are replicated as written.
    /// The other ten inherited the template with a missile row Riot copy-pasted; their own visuals are the
    /// <c>BBSpellEffectCreate</c> of their <c>TargetExecute</c> (ItemPlacementMissile's <c>ItemPlacement_tar.troy</c>).
    /// The triple is the test rather than a name list (and rather than "the auto effect's name is not this script's
    /// family"): a script may legitimately draw another script's particle — <c>EmblemOfValorParticle</c> draws
    /// <c>RallyingBanner_itm.troy</c> — while the display name, the icon and the effect naming one different spell
    /// together are the copy-paste.
    /// </summary>
    private bool CarriesForeignBuffTemplate(BbScript script)
    {
        if (_carriesForeignTemplate.TryGetValue(script.Name, out var known)) return known;
        var foreign = !script.Name.StartsWith("DarkBinding", StringComparison.OrdinalIgnoreCase) &&
            script.Global("BuffName") is BbValue.Text { Value: var buffName } &&
            string.Equals(buffName, "Dark Binding", StringComparison.Ordinal) &&
            script.Global("BuffTextureName") is BbValue.Text { Value: var texture } &&
            string.Equals(texture, "FallenAngel_DarkBinding.dds", StringComparison.Ordinal) &&
            script.Global("AutoBuffActivateEffect") is BbValue.Text { Value: var effect } &&
            string.Equals(effect, "DarkBinding_tar.troy", StringComparison.Ordinal);
        _carriesForeignTemplate[script.Name] = foreign;
        return foreign;
    }

    /// <summary>
    /// The torch particles other buffs copy, keyed by particle, with the shipped scripts that own each one: the spell that
    /// creates it or the buff that carries it as its own visual. Every other buff whose torch is one of these particles is
    /// Riot's copy-paste of that template (<see cref="IsCopiedAutoBuffTemplate"/>). Shipped evidence (batch 375): Garen's
    /// GarenJusticePreCast and Rumble's Heating Up family (finding 22, 94), Leona's LeonaZenithBladeBuffChaos (48),
    /// MonkeyKingConsume and MonkeyKingNimbusStrike (the Nunu and Olaf templates), GarenJustice (Ravage's RendingShot).
    /// Riot's icon copies travel with the torch, which is why the icon of an origin counts as a copy too.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> CopiedTorchOrigins =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dr_mundo_burning_agony_cas_02.troy"] = ["BurningAgony"],
            ["leBlanc_displace_AOE_tar.troy"] = ["LeblancChaosOrb"],
            ["Aegis_buf.troy"] = ["AegisoftheLegionAuraSelf", "LeviathanCap", "PendantofZephirisAuraSelf"],
            ["olaf_viciousStrikes_self.troy"] = ["OlafFrenziedStrikes"],
            ["RendingShot_buf.troy"] = ["Ravage"],
            ["Consume_buf.troy"] = ["Consume"],
        };

    /// <summary>
    /// Batch 401 (finding 127): effect creates Riot copied from another champion's torch, keyed by particle with the scripts that
    /// make the copy. Warwick's Hungering Strike sends Master Yi's Meditate_eff.troy on the hero it hits (its TargetExecute hero
    /// branch, bound to Warwick). NOT RETAIL: the shipped script sends this create; owner decision, 27 October 2026: a foreign torch
    /// copied from another champion is a bug, so the
    /// copy is withheld the way an unknown particle is (BbNativeHandlers.SpellEffectCreate). Only Warwick's copy is listed: Consume,
    /// MonkeyKingConsume, TriumphantRoar, InnateSpellHeal and OdinShrineHealBuff create the same particle and stay as shipped until
    /// the owner rules on them. Reconstruction: the shipped files do not show what retail drew for Hungering Strike's heal.
    /// Batch 410 (finding 85): Pantheon's Grand Skyfall inibins (Jump, Fall and the root spell) name Annie's InfernalGuardian torch,
    /// infernalguardian_tar.troy, as their hit effect; the stock default hit pass draws it on each unit the spell hits
    /// (BbHookRouter.DrawDefaultHitEffects, which asks this table too). Pantheon's other hit effects and his R creates are
    /// Pantheon-named, so this one is a copy. NOT RETAIL: the shipped inibins name it; owner decision, 27 October 2026.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> CopiedEffectCreates =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Meditate_eff.troy"] = ["HungeringStrike"],
            ["infernalguardian_tar.troy"] = ["Pantheon_GrandSkyfall_Jump", "Pantheon_GrandSkyfall_Fall", "Pantheon_GrandSkyfall"],
        };

    /// <summary>
    /// Whether <paramref name="script"/>'s BBSpellEffectCreate of <paramref name="effect"/> is one of the copies in
    /// <see cref="CopiedEffectCreates"/>. Such a create is dropped the way an unknown particle is: no packet, the id stays 0.
    /// </summary>
    internal static bool IsCopiedEffectCreate(string? script, string effect) =>
        script is not null && CopiedEffectCreates.TryGetValue(effect, out var copiers) &&
        copiers.Contains(script, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AutoBuffTorchKeys =
        ["AutoBuffActivateEffect", "AutoBuffActivateEffect2", "AutoBuffActivateEffect3", "AutoBuffActivateEffect4"];

    /// <summary>
    /// Whether <paramref name="script"/> is Riot's copy of another spell's template, so a stock client that holds its add
    /// draws the other spell's torch on the holder. Reconstruction: the shipped files show which spell owns each torch and
    /// that the copies exist; they do not show how retail's server treated a copy. The owner's patch-125 observation (no
    /// Dr. Mundo visual on Garen's ultimate) is the evidence that the copy never reached the client, so the add is
    /// server-only. A copy carries a torch from <see cref="CopiedTorchOrigins"/>, is not one of that torch's origins, is not
    /// a toggle (the client draws the toggle state from the add), and has no HUD icon of its own: no icon, or the icon its
    /// origin uses. A buff with its own icon keeps its add (RumbleGrenadeCounter's ammo count), and so does an origin.
    /// </summary>
    internal static bool IsCopiedAutoBuffTemplate(BbScript script, Func<string, BbScript?> findScript)
    {
        if (script.Global("SpellToggleSlot") is not null) return false;
        foreach (var key in AutoBuffTorchKeys)
        {
            if (script.Global(key) is not BbValue.Text { Value: var torch } ||
                !CopiedTorchOrigins.TryGetValue(torch, out var origins)) continue;
            if (origins.Contains(script.Name, StringComparer.OrdinalIgnoreCase)) return false;
            if (script.Global("BuffTextureName") is not BbValue.Text { Value: var icon } || icon.Length == 0) return true;
            return origins.Any(origin => findScript(origin)?.Global("BuffTextureName") is BbValue.Text { Value: var originIcon } &&
                string.Equals(originIcon, icon, StringComparison.Ordinal));
        }
        return false;
    }

    private readonly Dictionary<string, bool> _copiedTorch = new();

    /// <summary>
    /// The per-script answer to <see cref="IsCopiedAutoBuffTemplate"/>, with the shipped scripts looked up through the router.
    /// </summary>
    private bool CarriesCopiedTorch(BbScript script)
    {
        if (_copiedTorch.TryGetValue(script.Name, out var known)) return known;
        var copied = IsCopiedAutoBuffTemplate(script, _scripts.FindOrShipped);
        _copiedTorch[script.Name] = copied;
        return copied;
    }

    private readonly Dictionary<string, bool> _unappliedSilenceTorch = new();

    /// <summary>
    /// Batch 696 (owner, 5 November 2026: "throwing twisted fate's Q produces a silence effect under him"): whether the template hands the
    /// client the Silence buff's torch (<c>Global_Silence.troy</c>, Silence.luaobj's AutoBuffActivateEffect) while the script never silences
    /// its holder (no <c>BBSetStatus SetSilenced</c> true). Silence and Fiddlesticks' DarkWind set the status, so they keep their adds.
    /// WildCards is the shipped case: its template (BuffName 'Wild Cards', Q's icon, SealFate_tar.prt and GLOBAL_SILENCE.TROY at 'head',
    /// the target debuff of the old Seal Fate, copied into InfectedCleaver too) rides only the add SelfExecute puts on the TestCubeRender
    /// cube the cards leave from, and ExpirationTimer sets that cube's ForceRenderParticles, so the stock client drew the silence at the
    /// cube for its 5 s. Reconstruction: retail's wire for this add is not in the shipped files; the add stays on the server, where its
    /// BuffOnSpellHit is the cards' damage. Batch 695 did not cause it (the cube kept the add before), but ended the line's early 0xA6
    /// that had cut the torch off when the cards landed.
    /// </summary>
    private bool DrawsUnappliedSilence(BbScript script)
    {
        if (_unappliedSilenceTorch.TryGetValue(script.Name, out var known)) return known;
        var unapplied = AutoBuffTorchKeys.Any(key => script.Global(key) is BbValue.Text { Value: var torch } &&
                            string.Equals(torch, "Global_Silence.troy", StringComparison.OrdinalIgnoreCase)) &&
                        !script.Walk().Any(step => step.Block.Function == "BBSetStatus" &&
                            step.Block.Param("Status") is BbValue.Reference { Name: "SetSilenced" } &&
                            step.Block.Param("SrcValue") is BbValue.Bool { Value: true });
        _unappliedSilenceTorch[script.Name] = unapplied;
        return unapplied;
    }

    /// <summary>
    /// Batch 380 (finding 48): the two buffs Zenith Blade adds to its own caster, Leona's in-flight lock (LeonaZenithBlade) and
    /// the lunge (LeonaZenithBladeMissile, named after the running script). Reconstruction: the wire is withheld by name, because
    /// the shipped files do not say what retail sent; see the note at the ServerOnlyMarker assignment in the add path.
    /// </summary>
    private static bool IsZenithBladeOwnAdd(BbScript script) =>
        string.Equals(script.Name, "LeonaZenithBlade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(script.Name, "LeonaZenithBladeMissile", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the buff's own shipped script draws its activation effect: a <c>BBSpellEffectCreate</c> anywhere in its
    /// <c>OnBuffActivate</c> (BansheesVeil's bansheesveil_buf.troy, RallyingBannerAuraFriend's RallyingBanner_itm.troy,
    /// the seven rows of BuffEffectService's <c>PersistentEffects</c> that name what their script creates). Such a
    /// script takes the effect back in its own <c>OnBuffDeactivate</c>, so the particle's lifetime is the script's and
    /// the server-owned persistent copy must not be drawn beside it — batch 157 part 1: one BansheesVeil add drew two
    /// bansheesveil_buf (the hand-written row and the script's own) and the playtest called the shield "really bright,
    /// perhaps a stacked visual effect".
    /// </summary>
    private bool DrawsActivationEffect(BbScript script)
    {
        if (_drawsActivationEffect.TryGetValue(script.Name, out var known)) return known;
        var draws = false;
        void Count(IReadOnlyList<BbBlock> blocks)
        {
            foreach (var block in blocks)
            {
                if (string.Equals(block.Function, "BBSpellEffectCreate", StringComparison.Ordinal)) draws = true;
                Count(block.SubBlocks);
            }
        }
        if (script.Event("OnBuffActivate")?.Blocks is { } blocks) Count(blocks);
        _drawsActivationEffect[script.Name] = draws;
        return draws;
    }

    /// <summary>
    /// Whether the buff's own shipped template hands the stock client an activation particle to paint for the life of
    /// the add: any of the four <c>AutoBuffActivateEffect</c> slots the buff instance's torch restart walks (its loop is
    /// <c>index &lt; 4</c>). Those particles belong to the client's buff instance, so the renewal packet must not
    /// restart them — see <see cref="Core.Buffs.BuffPayload.ClientActivationFx"/>. The other half of the family is
    /// <see cref="DrawsActivationEffect"/>: a script that creates its particle itself is never on this list, and its
    /// particle is a 0x8C/0x3D pair the renewals do not touch at all.
    /// </summary>
    private bool ClientTorch(BbScript script)
    {
        if (_clientTorch.TryGetValue(script.Name, out var known)) return known;
        var torch = false;
        for (var slot = 1; slot <= 4 && !torch; slot++)
        {
            var key = slot == 1 ? "AutoBuffActivateEffect" : $"AutoBuffActivateEffect{slot}";
            torch = script.Global(key) is BbValue.Text { Value.Length: > 0 };
        }
        _clientTorch[script.Name] = torch;
        return torch;
    }

    /// <summary>
    /// Whether one run of <paramref name="frame"/>'s hook can make more than one add: the count of <c>BBSpellBuffAdd</c> blocks in the
    /// hook, cached per script. Two adds in different branches of one hook still count as one group, so a run that takes only one of
    /// them holds it until the hook is over.
    /// </summary>
    private bool ProcAddsAreOneGroup(BbHookFrame frame) =>
        IsProcHook(frame.Hook) && AddSites(frame.ScriptName, frame.Hook) >= 2;

    /// <summary>
    /// Whether the buff's own <c>OnBuffActivate</c> clears the buff it belongs to before the hook is over: a top-level
    /// <c>BBSpellBuffClear</c> that names the running script itself, on the holder (<c>TargetVar=Owner</c>, the shape both
    /// shipped users have). Such a buff never reaches the client in retail — it exists only for the length of its own
    /// activation — so its add is server-only (<see cref="Core.Buffs.BuffPayload.ServerOnlyMarker"/>).
    /// <para>
    /// The two shipped users are <c>OrianaGlobalCooldown</c> and <c>OdinShieldRelicBuffHeal</c>. The first is the one the
    /// owner saw: the four commands add it, its activation lowers the other slots' cooldowns and then clears itself, and
    /// its template carries <c>PopupMessage1 = game_floatingtext_Snared</c>, so a client holding the add drew "Snared"
    /// over Orianna on every Q, W, E and R. The server keeps the add on the unit for the 0.25 s it lasts, so the cooldown
    /// write the activation makes is unaffected.
    /// </para>
    /// <para>
    /// Only one client path paints a popup at all: the buff add packet handler (NpcBuffAdd2)
    /// walks the resolved script's three <c>popupMessage</c> slots through
    /// the script's message getter, and shows each through the local player's 0x10 text slot, for an add
    /// whose holder is that player and whose slot holds nothing yet. the buff instance's torch restart draws
    /// only the auto-activate particles, so a template's popup reaches the screen only on the add itself, never on a
    /// renewal. That is why the fix is "send no add", not "send a hidden one": the client would still paint the text.
    /// </para>
    /// <para>
    /// Reconstruction: retail's server is not in the client binary, so the mechanism is inferred. The evidence is the
    /// effect: the 2011 client shows no text for her casts, and a client only draws that text from the buff template's
    /// popup slot on the add, so the add cannot be reaching it. The test is read off the script's own blocks rather than a
    /// name list, so a buff added by another champion's script with the same shape is covered too.
    /// </para>
    /// </summary>
    private bool ClearsItselfOnActivate(BbScript script)
    {
        if (_clearsOnActivate.TryGetValue(script.Name, out var known)) return known;
        var clears = false;
        if (script.Event("OnBuffActivate")?.Blocks is { } blocks)
            foreach (var block in blocks)
                if (string.Equals(block.Function, "BBSpellBuffClear", StringComparison.Ordinal) &&
                    string.Equals(block.Text("BuffName"), script.Name, StringComparison.OrdinalIgnoreCase) &&
                    (block.Text("TargetVar") is not { } target || string.Equals(target, "Owner", StringComparison.Ordinal)))
                    clears = true;
        _clearsOnActivate[script.Name] = clears;
        return clears;
    }

    private int AddSites(string scriptName, string hook)
    {
        if (_addSites.TryGetValue((scriptName, hook), out var known)) return known;
        var sites = 0;
        void Count(IReadOnlyList<BbBlock> blocks)
        {
            foreach (var block in blocks)
            {
                if (string.Equals(block.Function, "BBSpellBuffAdd", StringComparison.Ordinal)) sites++;
                Count(block.SubBlocks);
            }
        }
        if (_scripts.FindOrShipped(scriptName)?.Event(hook)?.Blocks is { } blocks) Count(blocks);
        _addSites[(scriptName, hook)] = sites;
        return sites;
    }

    /// <summary>
    /// Places the adds one run held (<see cref="BbHookFrame.HeldAdds"/>: a proc hook, or an item active's own cast hook). The unit's
    /// table is asked for the whole group of
    /// adds that unit would take at once: an add <see cref="ISpellBuffService.RefusesAdd"/> refuses is out of the count (it never takes
    /// a slot), and a group that cannot get every slot it needs places none of its adds. The run is over when this is called, so the
    /// adds land in the order the script made them, after the rest of the hook. The particles the run held with them
    /// (<see cref="BbHookFrame.HeldEffects"/>) are created only for a group that landed, and a refused one raises
    /// <see cref="BbHookFrame.GroupDenied"/>: the whole proc is declined, its damage change included (BbItemModule.RunDamageChain).
    /// The assist markers the run held (<see cref="BbHookFrame.HeldMarkers"/>) follow their own recipient: a recipient whose group
    /// is declined is left with no debuff and no credit.
    /// </summary>
    public void CommitProcAdds(BbHookFrame frame)
    {
        var held = frame.HeldAdds;
        var effects = frame.HeldEffects;
        var markers = frame.HeldMarkers;
        if (held is not { Count: > 0 } && effects is not { Count: > 0 } && markers is not { Count: > 0 }) return;
        // A run can place its held work in parts (LandHeldAdds): a part that was declined keeps the run declined.
        var deniedEarlier = frame.GroupDenied;
        frame.HeldAdds = null;
        frame.HeldEffects = null;
        frame.HeldMarkers = null;
        frame.CommittingAdds = true;
        try
        {
            var denied = false;
            var skipped = new HashSet<Entity>();
            var landed = new List<BuffState>();
            var services = _host.Spells.Buffs;
            if (held is { Count: > 0 })
                foreach (var group in held.GroupBy(add => add.Target))
                {
                    // Reconstruction (the capacity case only; retail's outcome on a full table is not recovered): an item active's cast hook carries marker rows its script adds beside the
                    // effect (RegenerationPotion's Potion_Internal, Mourning's Internal_50MS). They are best-effort rows that follow
                    // the effect rows and decline silently on a full table — the use gate's own policy (ItemService.UseConsumable),
                    // pinned by items-live-consumable-buff-capacity-atomic and the renewal case of
                    // items-live-targeted-capacity-commit-and-slow: one opened slot is spent on the effect, never on its marker.
                    // The effect rows stay one admission group (Randuin's Slow/BlackOmen pair). A proc hook's marker is part of its
                    // package instead (Items/3155.luaobj's HexdrinkerTimer with its shield: items-live-hexdrinker-capacity-proc).
                    var package = frame.ItemActive
                        ? group.Where(add => ServerBuffType(add.ScriptBuffType) != BuffType.Internal).ToList()
                        : group.ToList();
                    var markerRows = frame.ItemActive
                        ? group.Where(add => ServerBuffType(add.ScriptBuffType) == BuffType.Internal).ToList()
                        : [];
                    var applications = 0;
                    var names = new List<string>();
                    foreach (var add in package)
                    {
                        if (services.RefusesAdd(group.Key, ServerBuffType(add.ScriptBuffType), add.Caster.NetId,
                                add.MaxStack > 0 ? add.MaxStack : null)) continue;
                        if ((BbBuffAddType)add.AddType == BbBuffAddType.BUFF_STACKS_AND_OVERLAPS)
                            applications += Math.Min(Math.Max(1, add.Stacks), Math.Max(1, add.MaxStack));
                        else names.Add(add.Name);
                    }
                    if (package.Count > 0 && !services.CanAddOverlapping(group.Key, applications, names.ToArray()))
                    {
                        denied = true;
                        skipped.Add(group.Key);
                        continue;
                    }
                    foreach (var add in package) Add(add, landed);
                    foreach (var add in markerRows) Add(add, landed);
                }
            // The group's rows land effects before their markers (the capacity policy above), so the sample a row's own add takes
            // after its OnBuffActivate can run before a sibling row of the same cast is on the unit: Spells/GrievousWound.luaobj's
            // BuffOnUpdateStats is gated on the Internal_50MS Mourning's TargetExecute adds beside it, and with the marker landing
            // after the wound its activation sample found no marker and wrote no stats at all (the wound cut nothing and carried no
            // life-steal share until the next 0.25 s grid line, by which its own BuffOnUpdateActions had re-derived the share against
            // the pool that never carried it). Every row of the landed group is sampled again now the group is complete — the sample
            // a single add takes after its own activation (Add below), so a hook gated on a sibling of the same cast sees it.
            foreach (var state in landed)
                if (_instances.TryGetValue(state, out var placed)) Resample(placed);
            frame.GroupDenied = deniedEarlier || denied;
            if (markers is not null)
                foreach (var marker in markers)
                    if (!skipped.Contains(marker.Target))
                        _host.Spells.Combat.RecordSupportAssist(marker.Source, marker.Target, marker.Duration);
            if (denied || effects is null) return;
            foreach (var create in effects) create();
        }
        finally { frame.CommittingAdds = false; }
    }

    /// <summary>
    /// Batch 703: the running hook's held adds land before a block of the same run reads or changes the buff table (a buff count,
    /// a has-buff test, a removal, a duration read; see <see cref="BbSpellHost.SpellBuffCount"/> and <c>BbNativeHandlers.Execute</c>).
    /// <c>BBSpellBuffAdd</c> places its buff when the block runs, so the script's next block sees it: Spells/RegenerationPotion.luaobj's
    /// TargetExecute adds its stack and Potion_Internal and then stores <c>BBGetBuffCountFromAll</c> in CharVars.countHealthPotion, the count
    /// its OnBuffDeactivate re-adds the queued potions from (>= 2). With the adds held to the end of the run the count was the one before the
    /// drink, so a potion drunk less than a second (Potion_Internal's recount period) before the running one ended was used up and lost
    /// (community report, 7 November 2026). The group admission stays: what the run adds before the read is admitted as one group,
    /// what it adds after it as the next. Reconstruction: only the run's own adds are placed, not those an outer run still holds.
    /// ponytail: the buff natives only; a stat read (BBGetStat) after a held add still sees the stats without it.
    /// </summary>
    internal void LandHeldAdds()
    {
        if (_host.HasFrame) CommitProcAdds(_host.Frame);
    }

    /// <summary>
    /// The slots the added buff's own <c>OnBuffActivate</c> frees on <paramref name="target"/> as it lands: the
    /// <c>BBSpellBuffRemoveType</c> sweeps (which leave the rows a dispel leaves, see <see cref="RemoveType"/>) and the
    /// <c>BBSpellBuffRemove</c> / <c>BBSpellBuffClear</c> removals by name. Counted as room for the add, which is what the
    /// hand-written Guardian Angel revive counted as "the source-cleared statuses or the ready marker" (see the capacity
    /// note in <see cref="Add"/>).
    /// <para>
    /// Reconstruction: the walk reads the hook's blocks as written, so a removal under a condition counts even when the
    /// condition will not hold, and a removal whose <c>AttackerVar</c> narrows its match counts the name alone. The shipped
    /// activation this exists for (GuardianAngel.luaobj's) clears unconditionally, and an over-count only makes the table
    /// briefly fuller than the limit it is already allowed to exceed.
    /// </para>
    /// </summary>
    private int ActivationFreeable(BbScript? script, Entity target)
    {
        if (script?.Event("OnBuffActivate") is not { } activate) return 0;
        var slots = new HashSet<int>();
        void Count(IReadOnlyList<BbBlock> blocks)
        {
            foreach (var block in blocks)
            {
                if (block.Function == "BBSpellBuffRemoveType" && block.Number("Type") is { } scriptType)
                {
                    var type = ServerBuffType(scriptType);
                    foreach (var buff in target.Buffs)
                        if (buff.Type == type && !buff.Payload.NonDispellable && buff.Slot is >= 0 and < BuffLifecycle.MaxBuffSlots)
                            slots.Add(buff.Slot);
                }
                else if (block.Function is "BBSpellBuffRemove" or "BBSpellBuffClear")
                {
                    // BBSpellBuffRemove takes one stack (only its last frees the slot); BBSpellBuffClear takes every buff of
                    // the name whole. A remove without BuffName is the running script's own name, as in the native. The remove takes
                    // the oldest application (see Remove).
                    var name = block.Text("BuffName") ?? script.Name;
                    var matching = target.Buffs.Where(buff => string.Equals(buff.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                                              buff.Slot is >= 0 and < BuffLifecycle.MaxBuffSlots);
                    foreach (var buff in block.Function == "BBSpellBuffClear" ? matching.ToArray()
                                 : matching.Take(1).Where(buff => buff.Stacks <= 1).ToArray())
                        slots.Add(buff.Slot);
                }
                Count(block.SubBlocks);
            }
        }
        Count(activate.Blocks);
        return slots.Count;
    }

    // landed collects the rows this call places (one per application of an overlapping add) for a held group's own resample
    // once the whole group is on the unit (CommitProcAdds).
    public BuffState? Add(BbBuffRequest request, ICollection<BuffState>? landed = null)
    {
        // BBBreakSpellShields: the marker add pops every shield the unit holds, those of other champions' scripts and Banshee's Veil
        // included, so it goes to the one shared break (ISpellCombatService.ConsumeSpellShield) instead of this runtime's own buffs.
        if (request.Name == SpellShieldMarker)
        {
            if (_host.HasFrame && (_host.Frame.Caster ?? _host.Frame.Owner) is { } breaker)
                _host.Spells.Combat.ConsumeSpellShield(breaker, request.Target, force: true);
            return null;
        }
        // Reconstruction: the 64-slot buff table is a client limit the original scripts never guard, and a full table has no recovered
        // retail outcome. The hand-written item ports declined the whole proc rather than half-apply it (ItemDamageService's
        // Hexdrinker, ItemPassiveService's Slow/Cripple and ItemSlow pairs, and the actives' own recipient pairs), and
        // BuffLifecycle's admission helpers mark every call site that guards with them the same way. An item proc's or item active's
        // adds are therefore held until its hook run is over and admitted as one group (CommitProcAdds): the adds the lifecycle
        // itself refuses take no slot and stay out of the count (3082's Slow is refused by ResistantSkin while its Cripple still
        // takes the one free slot), and a group that cannot get every slot it needs places none of it, so a denial leaves no partial
        // wire — no debuff and no assist credit (the marker the recipient's script made is dropped with its group). A hook that can
        // only add one buff places it at once, as it always did.
        if (_host.HasFrame && HoldsAdds(_host.Frame))
        {
            (_host.Frame.HeldAdds ??= new List<BbBuffRequest>()).Add(request);
            return null;
        }
        var services = _host.Spells.Buffs;
        // The buff's own script: the closure's, else the shipped Spells/<name>.luaobj. The summoner spells name buffs
        // that are not members of their closure (GrievousWound, FortifyBuff, Teleport_Target ...), and their hooks have
        // to run the same way a champion's closure buffs' do.
        var script = _scripts.FindOrShipped(request.Name);
        var type = ServerBuffType(request.ScriptBuffType);
        // The periodic re-add hooks are one root for the whole family: the character script's UpdateSelfBuffStats and
        // UpdateSelfBuffActions (Vayne's Night Hunter, Nidalee's Prowl in brush, and the eight aura items' one-second
        // BBExecutePeriodically), and an ammo buff's own BuffOnUpdateAmmo (E14: the timer of Corki's Missile Barrage,
        // Akali's Shadow Dance and Karma's Mantra is the recharge timer, and the hook re-adds the buff with exactly that
        // duration — zero slack over the interval to the next charge). Their re-adds are what the client's copy of the
        // buff has to bridge.
        var fromPoll = _host.HasFrame &&
            _host.Frame.Hook is "UpdateSelfBuffStats" or "UpdateSelfBuffActions" or "BuffOnUpdateAmmo";
        // Reconstruction: a renewal of a live buff by that poll only refreshes the server's timer; the wire add (a 0xBF with the
        // buff's activation sound) would go out 30 times a second per target. Retail's packet for a renewal is not in the client,
        // so the add is held back to one wire renewal per client copy: once the copy is past the middle of its duration. Both
        // hooks take that rule. The stats poll used to swallow the wire renewal outright, on the reading that its buffs are
        // invisible; they are not (3001's AbyssalScepterAuraSelf and 3110's FrozenHeartAura are re-added from it and both are
        // on the HUD), and with no renewal the client's copy ran out at the script's duration and never came back —
        // batch 157 part 3, the second half of the aura blink.
        // Reconstruction (batch 174b): the self-update poll runs every spell tick, and CharScriptWorm's UpdateSelfBuffStats re-adds
        // WormRecouperateOn with BUFF_REPLACE_EXISTING on each run (its add, 0026-0037). A replace that builds a new instance each tick
        // never reaches the buff's own BuffOnUpdateActions line (0.25 s after the add), so the recuperation never started. A poll re-add of
        // a live buff of the same name and caster keeps the live instance, as a poll renewal does; the wire rule below is unchanged.
        if (((BbBuffAddType)request.AddType is BbBuffAddType.BUFF_RENEW_EXISTING or BbBuffAddType.BUFF_REPLACE_EXISTING) && fromPoll &&
            request.Target.Buffs.FirstOrDefault(buff => string.Equals(buff.Name, request.Name, StringComparison.Ordinal) &&
                                                       buff.CasterNetId == request.Caster.NetId) is { } live &&
            _instances.TryGetValue(live, out var renewed) &&
            _host.Spells.CurrentTime + request.Duration * 0.5 < renewed.WireEndsAt)
        {
            live.ExpiresAt = request.Duration;
            var pendingPop = PendingVeilPop(renewed);
            foreach (var entry in request.Vars.Entries) renewed.InstanceVars[entry.Key] = entry.Value;
            if (pendingPop) renewed.InstanceVars["WillRemove"] = true;
            return live;
        }
        // Reconstruction (batch 326): a BUFF_RENEW_EXISTING add of a buff from that same buff's own OnBuffDeactivate renews the instance whose
        // removal is under way, so the removal goes on and nothing is added. KogMawIcathianSurprise.luaobj's OnBuffDeactivate renews itself
        // (pc 0765-0783: Owner, 4 s) just before its BBForceDead (pc 0785); the lifecycle takes the buff off the unit before the hook runs
        // (BuffLifecycle.RemoveBuffNow), so the renewal found nothing and made a new PersistsThroughDeath instance: the corpse exploded again
        // four seconds later, and after the respawn its BBForceDead hit a living unit. Retail's buff manager is server code the client does
        // not hold; no Kog'Maw patch note from V1.0.0.94 to V1.0.0.138 mentions a second explosion. The deactivation re-adds that do make
        // the next instance are other add types and keep doing so: KarmaChakraTimer's recharge (BUFF_REPLACE_EXISTING) and the queued
        // RegenerationPotion and FlaskOfCrystalWater (BUFF_STACKS_AND_RENEWS), the only other shipped buffs that re-add themselves there.
        if ((BbBuffAddType)request.AddType == BbBuffAddType.BUFF_RENEW_EXISTING && _host.HasFrame &&
            _host.Frame is { Hook: "OnBuffDeactivate", Buff: { } deactivating } && ReferenceEquals(deactivating.Owner, request.Target) &&
            string.Equals(deactivating.State.Name, request.Name, StringComparison.Ordinal))
            return null;
        // Reconstruction: while death retires a unit's buffs, the deactivation hooks may not add a new buff that does not persist
        // through death to that unit. Retail's death clears them; the emulator's buff lifecycle does not guard dead targets, so
        // StarcallDamage's OnBuffDeactivate would hang a Starcall shred on the corpse. (An add from an ordinary hook, such as
        // the hand-written Annie's Pyromania stack of a missile that outlives its caster, is not touched.)
        if (ReferenceEquals(request.Target, _retiring) && (script is null || !PersistsThroughDeath(script)))
            return null;
        // Reconstruction: retail's add of a hostile control to a corpse is not recovered; a hostile control is not added to a corpse. The hand-written control spells all tested Alive before the stun, root or
        // silence; the scripts do not (Rocket Grab and Static Field call BBApplyStun / BBApplySilence and add RocketGrab2 right after the
        // damage that killed the target), and the buff lifecycle does not guard dead targets. Nothing is shown for a unit that is gone.
        if (!request.Target.Alive && request.Caster.Team != request.Target.Team && type is BuffType.Stun or BuffType.Silence or BuffType.Snare
                or BuffType.Fear or BuffType.Taunt or BuffType.Sleep or BuffType.Net or BuffType.Polymorph or BuffType.Suppression or BuffType.Blind)
            return null;
        // Reconstruction: a dead hero is given no new script buff that does not persist through death. Death runs the non-persistent removal
        // (RemoveNonPersistent below), so after it the buff table of a corpse holds only what the scripts mark PersistsThroughDeath; a buff added
        // later by someone else's hook would outlive the respawn (the shipped scripts guard their own adds with IS_NOT_DEAD, but Kassadin's Force Pulse
        // names him, not the caster, as the target of its charge: six casts in the five seconds after his death sealed nothing and respawned him
        // with E ready). No retail source says the add is refused; a zombie life (Entity.IsZombie) is a live unit and is not touched.
        // Reconstruction (batch 317): the hero's own character script may add a buff to its corpse from its UpdateSelfBuffActions poll. CharScriptCassiopeia
        // adds her death particle (CassiopeiaDeathParticle, not persistent) on the corpse, every poll while dead, and that particle turns her to
        // stone (BBPushCharacterData); the shipped script names her as both Owner and Attacker. Any other add to a corpse is still refused
        // (Kassadin's charge, a spell's hook, another unit's add).
        var ownPoll = _host.HasFrame && _host.Frame.Hook == "UpdateSelfBuffActions" && ReferenceEquals(_host.Frame.Caster, request.Target) &&
            ReferenceEquals(request.Caster, request.Target);
        // Reconstruction (batch 683, the owner's game a26761c7 of 4 November 2026): neither is any other corpse given another unit's new
        // non-persistent script buff. OdinCaptureChannelBomb's channel outlives the capture (batch 665) and its ChannelingUpdateStats adds
        // OdinBombSuppression to its Target whenever the Target holds none; the capture kills the relic and the death clears it, so the next
        // update put it on the corpse, whose 0.5 s drain re-ran the capture (OdinBombSuccessParticle's lightning, the Storm Shield's renewal)
        // until the corpse was reaped: 16 lightnings from one pickup. A unit's adds to its own corpse (a placemarker's own scripts) are kept.
        // Batch 685 narrows it to the per-tick update hooks (the channel's update above): an event-driven add (a hit, a cast, a missile) to a
        // non-hero corpse lands as the script asks. VeigarBalefulStrike's TargetExecute guards its add to the target with CO_IS_NOT_DEAD (its
        // OnBuffDeactivate grants AP when that unit is dead), a guard that matters only if retail's add to a corpse succeeds; TwoShivPoison
        // adds its slow after the damage that may kill, unguarded (batch 348 recorded that slow on the corpse).
        var periodicAdd = _host.HasFrame && _host.Frame.Hook is "BuffOnUpdateActions" or "BuffOnUpdateStats" or "ChannelingUpdateActions"
            or "ChannelingUpdateStats" or "UpdateSelfBuffActions" or "UpdateSelfBuffStats";
        if (!request.Target.Alive && !request.Target.IsZombie && !ownPoll &&
            (request.Target.UnitType == UnitType.Hero || periodicAdd && !ReferenceEquals(request.Caster, request.Target)) &&
            (script is null || !PersistsThroughDeath(script)))
            return null;
        // BuffOnAllowAdd of the target's script buffs (a spell shield refuses what a hit spell adds after it popped) is asked by BuffLifecycle for
        // every add, a script's or not, of every module's buffs: see IBbHookSink.AllowBuffAdd.
        // Reconstruction: the 64-slot buff table is a client limit the original scripts never guard (retail's outcome on a full table is not recovered); BuffLifecycle throws on a
        // full table, so the emulator declines the buff (and with it the activation hook) as the hand-written ports do.
        var addType = (BbBuffAddType)request.AddType;
        var overlapping = addType == BbBuffAddType.BUFF_STACKS_AND_OVERLAPS;
        // The stacks of a BUFF_STACKS_AND_RENEWS / BUFF_STACKS_AND_CONTINUE buff belong to the unit that added them. Retail's buff list holds an
        // instance per application with its caster: the client's removal core erases the first buff whose caster field matches the
        // Attacker key, and BuildingBlocksBase.luaobj's SpellBuffCount takes the caster. The shipped scripts count and remove them by caster
        // (BBGetBuffCountFromCaster, BBSpellBuffRemove with an Attacker: Vayne's Silver Bolts, Pyromania), so two casters on one target are
        // two buffs, each with its own count. The hand-written buffs did this with BuffPayload.PerCaster; the script path did not (batch 98, row 19).
        // Batch 407 (Reconstruction): an overlapping add with StacksExclusive (the client's default, Gangplank's R Slow) that its own periodic hook
        // re-applies (BuffOnUpdateActions or ChannelingUpdateStats: the same caster adds it every tick) renews the caster's live instance instead of
        // adding one. Event-driven adds (a cast, a hit, an activation, a missile) keep one instance per application, as before batch 407.
        // Pantheon's counter (StacksExclusive false) stacks either way.
        var periodicReapply = addType == BbBuffAddType.BUFF_STACKS_AND_OVERLAPS && request.StacksExclusive && _host.HasFrame &&
                              _host.Frame.Hook is "BuffOnUpdateActions" or "ChannelingUpdateStats";
        var perCaster = addType is BbBuffAddType.BUFF_STACKS_AND_RENEWS or BbBuffAddType.BUFF_STACKS_AND_CONTINUE || periodicReapply;
        // An overlapping add never throws and declines itself on a full table (AddOverlappingBuff), so only the other add types preflight.
        // The adds that go through BuffLifecycle.AddBuff (a replacing add, a renewing add of a buff that is not there yet) also reserve the
        // slot of the Boots of Mobility penalty a hostile control starts; the stacking adds do not call that hook.
        var throughAddBuff = addType == BbBuffAddType.BUFF_REPLACE_EXISTING ||
            (addType == BbBuffAddType.BUFF_RENEW_EXISTING &&
             !request.Target.Buffs.Any(buff => string.Equals(buff.Name, request.Name, StringComparison.Ordinal)));
        // Reconstruction (the capacity case only): the slots the added buff's own activation frees count as room for it (retail's capacity rule is native and not recovered) — the rule
        // the hand-written Guardian Angel revive was written to ("with a full table, only the source-cleared statuses or the
        // ready marker can make room"). Spells/GuardianAngel.luaobj's OnBuffActivate is exactly that room: it sweeps the
        // cleared-by-type set (BBSpellBuffRemoveType) and removes the Spells/WillRevive.luaobj marker by name, so the revival's
        // own add has to be admitted where a plain full-table question would refuse it.
        var freeable = ActivationFreeable(script, request.Target);
        if (!overlapping && !(perCaster
                ? BuffLifecycle.CanAddScopedBuffs(request.Target, freeable, (request.Name, request.Caster.NetId))
                : throughAddBuff ? services.CanAdd(request.Target, request.Name, request.Caster, type, freeable)
                                 : BuffLifecycle.CanAddNamedBuffs(request.Target, freeable, request.Name)))
        {
            // A refused add does not half-apply (owner decision, batch 147): a hook that could not place its buff keeps none of
            // its other application work, its DamageAmount write included. GroupDenied is the flag a held proc group raises for
            // that already (BbItemModule.RunDamageChain and the router's damage readers keep the hit the hook was given); a hook
            // with one add site places its add at once and takes the same rule here. Without it Spells/WillRevive.luaobj's
            // BuffOnPreDamage left the hero at one health with no Guardian Angel and no death, and its next lethal hit was
            // CurHealth - 1 = 0 damage: unkillable.
            if (_host.HasFrame) _host.Frame.GroupDenied = true;
            return null;
        }

        var frame = _host.HasFrame ? _host.Frame : null;
        // Batch 252: the sample taken before the buff exists runs on a copy of its variables. Retail runs no hook of a buff before its
        // OnBuffActivate (BbHookFrame.PreActivation), so what this run writes must not reach the instance: Wither's BuffOnUpdateStats ticked
        // its BBExecutePeriodically here with TimeBetweenTicks still unset (period 0), one step of the slow before the activation sized it,
        // and the slow reached -0.50 instead of the script's -0.35 - 4 x 0.03 = -0.47 (the tooltip's 47 percent).
        var sampled = script is null ? new StatsSample(new BuffPayload(), "") : TrySample(script, request.Target, request.Caster,
            Copy(request.Vars), frame, instance: null);
        var payload = sampled.Payload;
        // The particle of a buff whose own script draws it in OnBuffActivate is the script's, not the server-owned
        // persistent one (see DrawsActivationEffect): the add carries the claim with it, so the buff lifecycle leaves
        // the visual to the activation hook the add is about to run.
        if (script is not null) payload.ScriptOwnsActivationFx = DrawsActivationEffect(script);
        // Batch 157 part 3: the half of the family the client paints itself. A template that hands the stock client an
        // AutoBuffActivateEffect makes the buff's renewal packet matter: NpcBuffReplace re-presents that particle
        // (the buff instance's torch restart), and the aura items re-add their aura every second. The claim rides the add so the
        // held-buff rewrite can pick the silent renewal for it.
        if (script is not null) payload.ClientActivationFx = ClientTorch(script);
        // The copy of a buff the polls re-add every period must outlive the interval to the next re-add.
        if (fromPoll) payload.WireDurationMargin = PollWireMargin;
        if (ReplicatesWithGraceMargin(addType))
            payload.WireDurationMargin = Math.Max(payload.WireDurationMargin, WireGraceStep);
        // A buff whose shipped template is a copy-paste of another spell's stays on the server (see
        // CarriesForeignBuffTemplate and BuffPayload.ServerOnlyMarker). The script keeps the add it asked for — the
        // ward's placement marker is what ItemPlacementMissile's TargetExecute tests with BBIfHasBuff before it
        // destroys itself — but the wire carries nothing for it: the client would draw the copied DarkBinding_tar.troy
        // on the holder and show Morgana's icon and tooltip for a marker. Every script-driven add goes through here,
        // so an item active's own marker is covered with a ward's.
        // Batch 375: the same wire rule for the torch copies Riot pasted from another spell's template (CarriesCopiedTorch):
        // Garen's R pre-cast, Leona's E chaos buff and the Rumble Heating Up family draw Dr. Mundo's, LeBlanc's or Aegis's
        // torch on the holder for the length of the add, so their adds stay server-side too.
        // Batch 380 (finding 48): Zenith Blade's two adds on Leona stay server-side too. LeonaZenithBlade is her in-flight lock: a visible
        // 0.5 s add whose template carries PopupMessage1 'game_floatingtext_Snared', which the stock client shows over the local hero for a
        // new add (the popup branch). The wiki's Zenith Blade entry roots only the enemy and says Leona is "unable
        // to move or attack while Zenith Blade is in flight", never that she is snared. LeonaZenithBladeMissile is the lunge: a 1.25 s
        // Internal add named after the running script (no BuffName key, see SpellBuffAdd), whose template copies Teemo's Blind (BuffName
        // 'Blind', BuffTextureName Teemo_TranquilizingShot.dds, BlindingDart's icon). Reconstruction: retail's wire for these two adds is not
        // in the shipped files. The lock and the lunge are the script's and stay on the server (BBSetStatus, BBMove and the move hooks run as
        // before); only the client's add is withheld.
        // Batch 696: so does a template that carries the Silence buff's torch for an add that silences nothing (DrawsUnappliedSilence: Wild Cards' cube).
        if (script is not null && (CarriesForeignBuffTemplate(script) || CarriesCopiedTorch(script) || IsZenithBladeOwnAdd(script) ||
                                   DrawsUnappliedSilence(script)))
            payload.ServerOnlyMarker = true;
        // Batch 169 part 2: a buff whose own OnBuffActivate clears it again never exists on the client in retail, so its add is
        // server-only too. OrianaGlobalCooldown is the shipped case: the add the four commands make is followed, inside the same
        // OnBuffActivate, by BBSpellBuffClear of its own name (the hook lowers the other slots' cooldowns and then removes
        // itself); its template carries PopupMessage1 = game_floatingtext_Snared, and a client that held the add drew "Snared"
        // over Orianna on every Q, W, E and R. The buff itself stays on the server for the 0.25 s it lasts, so the resample and
        // the cooldown write the hook makes happen as before. The test is read off the script's own blocks (an unconditional
        // BBSpellBuffClear of the running script's name under OnBuffActivate), not off a name list, so the other shipped users
        // of the same pattern (OdinShieldRelicBuffHeal) are covered too.
        if (script is not null && ClearsItselfOnActivate(script)) payload.ServerOnlyMarker = true;
        // Batch 388: an instant marker never reaches the client: its duration ends in the step of its add, so the copy would live a frame at most
        // and draws nothing (Parley, Renekton's unlock, Mordekaiser's syphon damage and Nasus's Spirit Fire: each has no texture). Reconstruction:
        // retail's wire for these adds is not in the shipped files; the marker's own hooks run as before.
        if (script is not null && IsInstantMarker(request.Duration)) payload.ServerOnlyMarker = true;
        // Batch 405 (Reconstruction): the slot the script toggles on (SpellToggleSlot, from 1). The client reads that slot's IsToggleSpell when
        // this add activates (BuffPayload.ToggleSlot), so the add's place on the wire depends on the slot's swap; nothing else changes.
        if (script?.Global("SpellToggleSlot") is BbValue.Number { Value: var toggled }) payload.ToggleSlot = (int)toggled - 1;
        // Batch 142 part 2: the damage-over-time a buff's own BuffOnUpdateActions schedules is described on the payload
        // the way a hand-written one is (ItemPassiveService.AddMourning reads the same two blocks of Mourning.luaobj).
        // SummonerDot.luaobj is the shipped case: before this, its Period / DamagePerTick / MaxTicks read 0 and the DoT
        // existed only as running script, which the record, the tick numbers and the checks all disagree with.
        FillScriptPeriodic(script, payload, request.Vars, request.Caster);
        // Reconstruction: BUFF_STACKS_AND_RENEWS with StacksExclusive (the default; the client's add reader defaults it to true) keeps its
        // stacks per caster. The shipped Silver Bolts mark counts and removes its stacks by caster (BBGetBuffCountFromCaster, BBSpellBuffRemove with
        // the caster as Attacker) and its first stack clears only that caster's other marks, so two Vaynes on one target each own a mark with its own
        // stack count. The stock client keeps one instance per caster (its removal core matches entries by caster), and the previous
        // hand-written marks used one per caster. The meaning of StacksExclusive = false is not in the client (the add reader only parses
        // it); the 31 shipped adds that say false (potions, two self stacks) are left on one shared stack count.
        if ((BbBuffAddType)request.AddType == BbBuffAddType.BUFF_STACKS_AND_RENEWS && request.StacksExclusive) payload.PerCaster = true;
        // A script's own invulnerability only protects its holder; the emulator's stasis reading of that buff type is Zhonya's.
        if (type == BuffType.Invulnerability) payload.ActionsAllowed = true;
        // Batch 222: a stun label with no status write in its script stuns nothing (Teleport_Turret's BUFF_Stun sets none and the turret
        // keeps firing). The label stays; the stun rule reads ActionsAllowed (BuffState.DisablesActions, CharacterState).
        if (type == BuffType.Stun && script is not null && !ScriptWritesStatus(script)) payload.ActionsAllowed = true;
        // The script's own NonDispellable global (278 scripts, 40 more spell it Nondispellable) is what a dispel reads: Cleanse, Quicksilver
        // Sash and BBSpellBuffRemoveType (the dispel-by-type routine) leave such a buff on its holder.
        if (script is not null && IsNonDispellable(script)) payload.NonDispellable = true;
        // The add's IsHiddenOnClient is the isHidden byte of its 0xBF: the client keeps the buff and its sound and never draws it on the HUD buff bar.
        // Brand's CharScript adds BrandPassiveSound (an empty script: no BuffTextureName) with it; without the byte the bar drew a blank square for it.
        if (request.HiddenOnClient) payload.HiddenOnClient = true;
        if (perCaster) payload.PerCaster = true;
        // BUFF_STACKS_AND_OVERLAPS: every application is its own instance with its own timer (Reckoning's slow); see
        // BuffLifecycle.AddOverlappingBuff. Each application runs OnBuffActivate and OnBuffDeactivate on its own.
        // NumberOfStacks (E29): that many applications of the add, each a stack of its own (an instance in retail) that sees the count the
        // ones before it made. A renewal adds none of its own: it creates the buff or renews it, and the others raise the count to NumberOfStacks.
        var maxStack = Math.Max(1, request.MaxStack);
        var wanted = Math.Min(Math.Max(1, request.Stacks), maxStack);
        var applications = addType switch
        {
            BbBuffAddType.BUFF_STACKS_AND_OVERLAPS or BbBuffAddType.BUFF_RENEW_EXISTING => wanted,
            BbBuffAddType.BUFF_STACKS_AND_RENEWS or BbBuffAddType.BUFF_STACKS_AND_CONTINUE => Math.Max(1, request.Stacks),
            _ => 1,
        };
        BuffState? state = null;
        for (var index = 0; index < applications; index++)
        {
            var present = request.Target.Buffs.FirstOrDefault(buff => string.Equals(buff.Name, request.Name, StringComparison.Ordinal) &&
                                                                  (!perCaster || buff.CasterNetId == request.Caster.NetId));
            var before = present?.Stacks ?? 0;
            if (index > 0 && addType == BbBuffAddType.BUFF_RENEW_EXISTING && before >= wanted) break;
            switch (addType)
            {
                case BbBuffAddType.BUFF_REPLACE_EXISTING:
                    // Reconstruction (batch 551): the replacement is by name whatever the caster, StacksExclusive or not; what that flag means for a
                    // replacing add is not in the client (the add reader only parses it). Ignite is the case that shows: SummonerDot is a
                    // REPLACE_EXISTING add with StacksExclusive, and the wiki's Ignite page says a later Ignite on the same target overrides the
                    // earlier debuff and refreshes it, never stacks (the wiki's Ignite page, Notes), naming no caster rule.
                    // Batch 583: Zilean's two-bomb case agrees. TimeBomb.TargetExecute finds the other team's Zilean (BBGetChampionBySkinName), blasts
                    // with his name when his bomb is on the target, then adds its own bomb REPLACE_EXISTING; the bomb blasts again at its end only
                    // when it expires (OnBuffDeactivate, Expired), so a per-caster add would let the other bomb go off a second time. The wiki's
                    // V1.0.0.96 "Bug Fix: Not detonating an enemy Zilean's bomb when landing on the same unit" shows the detonation is retail.
                    state = services.Add(request.Target, request.Name, type, request.Duration, request.Caster.NetId, payload,
                        request.CanMitigateDuration);
                    break;
                case BbBuffAddType.BUFF_RENEW_EXISTING:
                    // The live instance keeps its slot, start time and visuals; duration and variables are refreshed. The first application
                    // keeps the count (the cap is the live count); the later ones raise it.
                    state = present is null
                        ? services.Add(request.Target, request.Name, type, request.Duration, request.Caster.NetId, payload,
                            request.CanMitigateDuration)
                        : services.AddStacking(request.Target, request.Name, type, request.Duration, request.Caster.NetId,
                            index == 0 ? Math.Max(1, present.Stacks) : maxStack, payload, replaceExistingPayload: index == 0,
                            canMitigateDuration: request.CanMitigateDuration);
                    break;
                case BbBuffAddType.BUFF_STACKS_AND_OVERLAPS when periodicReapply && present is not null:
                    // Batch 407 (Reconstruction): the live instance of this caster keeps its slot, start and visuals; its duration and variables are
                    // refreshed and no stack is added, so the count stays and the wire sends the renewal of the copy the client holds (no 0x1F).
                    state = services.AddStacking(request.Target, request.Name, type, request.Duration, request.Caster.NetId,
                        Math.Max(1, present.Stacks), payload, replaceExistingPayload: true, canMitigateDuration: request.CanMitigateDuration);
                    break;
                case BbBuffAddType.BUFF_STACKS_AND_OVERLAPS:
                    state = services.AddOverlapping(request.Target, request.Name, type, request.Duration, request.Caster.NetId, payload,
                        request.CanMitigateDuration, request.MaxStack > 0 ? request.MaxStack : null);
                    break;
                case BbBuffAddType.BUFF_STACKS_AND_RENEWS:
                    state = services.AddStacking(request.Target, request.Name, type, request.Duration, request.Caster.NetId,
                        maxStack, payload, canMitigateDuration: request.CanMitigateDuration);
                    break;
                case BbBuffAddType.BUFF_STACKS_AND_CONTINUE:
                    // The count rises and the running timer continues (RegenerationPotion and FlaskOfCrystalWater are the shipped users).
                    state = services.AddContinuing(request.Target, request.Name, type, request.Duration, request.Caster.NetId,
                        maxStack, payload, canMitigateDuration: request.CanMitigateDuration);
                    break;
                default:
                    throw BbSpellHost.Unbound($"{addType} for buff {request.Name}", "M2");
            }
            if (state is null) break;
            landed?.Add(state);
            if (index == 0) _host.Spells.ScriptBuffAdded?.Invoke(request.Target, request.Caster, request.Name);
            // A further stack of a buff that is full adds nothing: the first application already renewed it.
            if (index > 0 && addType is BbBuffAddType.BUFF_STACKS_AND_RENEWS or BbBuffAddType.BUFF_STACKS_AND_CONTINUE && state.Stacks <= before) break;
            if (script is null) continue;

            var known = _instances.TryGetValue(state, out var instance);
            // Another stack of a live buff: the count rose. A renewal or a capped add leaves the stacks as they were.
            var raised = known && state.Stacks > before;
            if (!known)
            {
                instance = new BbBuffInstance { Script = script, Name = request.Name, Owner = request.Target, State = state };
                _instances[state] = instance;
                instance.StartTime = _host.Spells.CurrentTime;
                instance.Shortened = state.ExpiresAt < request.Duration - 1e-9;
                // The mutation clock of this life (BbBuffInstance.MutateNextAt): an add that carries a TickRate accumulates
                // its bare permanents once per tick from the first tick on, one that carries none on every sample from the
                // one this add takes (batch 673). A renewal above does not re-stamp it.
                instance.MutateNextAt = request.TickRate > 0.0
                    ? _host.Spells.CurrentTime + request.TickRate
                    : _host.Spells.CurrentTime;
                // The update-actions hook of this life (see ActionsUpdateInterval) runs on its holder's 0.25 s pass, the first time on
                // the holder's next pass after this add (batch 720, owner decision of 8 November 2026: 0 to 0.25 s later; OnTick). This
                // stamp is only the not-before of the next run. Batch 175 ran the first one a whole interval after the add, reading
                // PickACard's even seed (Counter 0/2/4, turns on 2/4/6) as the script's own phase; on the holder's pass the seeded card
                // shows for one to two passes. A buff whose whole life ends before that pass (the shipped "run once and remove myself"
                // applicators: FrostArrowApplicator and ToxicShotApplicator 0.1 s) still gets one run — its last update is its only
                // opportunity — which OnTick notices by the buff's own end. A renewal (below) keeps the stamp, like StartTime and
                // MutateNextAt: the holder's line goes on.
                instance.ActionsNextAt = _host.Spells.CurrentTime;
                instance.ActionsFirstLinePending = true;
                // A hook that schedules its own BBExecutePeriodically has to be polled every update: the period is tested inside the
                // hook on the host clock, so a hook polled four times a second never admits Tryndamere's 0.1 s scan, Annie's 1.0 s
                // pulse, Brand's 1.05 s tick or the Bushwhack trap's 0.9 s arming on its own lines (see ActionsCarriesPeriodic).
                instance.ActionsCarriesPeriodic = script.Walk().Any(entry =>
                    string.Equals(entry.Hook, "BuffOnUpdateActions", StringComparison.Ordinal) &&
                    string.Equals(entry.Block.Function, "BBExecutePeriodically", StringComparison.Ordinal));
            }
            instance!.WireEndsAt = _host.Spells.CurrentTime + request.Duration;
            instance.LifeEndsAt = instance.WireEndsAt;
            instance.CasterNetId = request.Caster.NetId;
            // The damage identity the chain keeps (see BbBuffInstance.RecapOrigin): the add's own, else the item whose own
            // run made it (Items/3131's window, the active's SelfExecute of Spells/OdinEntropicClaymore), else the one the
            // buff that added it carries, and an application that states none of the three leaves the live instance's as it
            // was (a renewal from a poll). A champion spell's chain has none: its frame answers with the slot and rank it
            // inherited instead, which stay live.
            if (request.RecapOrigin is { } origin) instance.RecapOrigin = origin;
            else if (frame?.ItemOrigin is { } item) instance.RecapOrigin = item;
            else if (frame?.Buff?.RecapOrigin is { } carried) instance.RecapOrigin = carried;
            // Every application is handed its own copy of NextBuffVars.
            var vars = index == 0 ? request.Vars : Copy(request.Vars);
            if (!known || raised)
            {
                // A new stack is a record of its own (see BbBuffInstance.Stacks): its OnBuffActivate writes there, and the hooks that run
                // once per buff read the newest stack's table.
                instance.Stacks.Add(new BbBuffStack
                {
                    Vars = vars, SpellSlot = frame?.SpellSlot ?? -1, SpellRank = frame?.SpellRank ?? 0, ManaCost = frame?.ManaCost ?? 0.0,
                });
                instance.InstanceVars = vars;
            }
            else
            {
                // A renewal hands over fresh NextBuffVars; the live instance keeps what its own hooks stored since (the effect ids
                // OnBuffActivate wrote for OnBuffDeactivate to remove) and takes the new values over by name. Replacing the table lost
                // those ids: Radiance's aura on allies and Taric's Shatter-ready sparks were never removed.
                var pendingPop = PendingVeilPop(instance);
                foreach (var entry in vars.Entries) instance.InstanceVars[entry.Key] = entry.Value;
                if (pendingPop) instance.InstanceVars["WillRemove"] = true;
            }
            instance.TickRate = request.TickRate;
            instance.StatsSignature = sampled.Signature;
            ApplyForeign(instance, sampled.Foreign); // null removes the carriers of a hook that no longer asks for another unit (batch 318)
            if (frame is not null)
            {
                // A buff added from a hook inherits the spell chain that started it: its hooks get the same Level and recap origin.
                instance.SpellSlot = frame.SpellSlot;
                instance.SpellRank = frame.SpellRank;
                instance.ManaCost = frame.ManaCost;
            }
            // BUFF_RENEW_EXISTING refreshes the live instance and does not run OnBuffActivate again (batch 314: settled by the shipped
            // scripts). CharScriptSoraka's UpdateSelfBuffActions (BBExecutePeriodically 1.0, pc 0002-0005) renews
            // ConsecrationAuraNoParticle with BUFF_RENEW_EXISTING (pc 0038-0039) every second, and that buff's OnBuffActivate is
            // BBIncPermanentStat IncPermanentFlatSpellBlockMod +16 (pc 0006-0012), its OnBuffDeactivate -16 (pc 0035-0041): a rerun
            // would pile up 16 spell block a second, where Consecration gives a flat 16. A stacking add is a new instance in retail and
            // keeps running the hook (Pyromania counts its stacks there). Reconstruction: that the renewal also hands over the add's
            // variables (NextBuffVars) is not shown by a script.
            // Batch 490: a BUFF_STACKS_AND_RENEWS add at MaxStack raises nothing either, so it is the same renewal. Running OnBuffActivate
            // there gave each capped add an activation no stack deactivates: Rageblade's BBIncPermanentStat pair (+0.04 attack speed,
            // +6 AP per stack, MaxStack 8) kept a stack per hit past the cap for good (owner: 2078 AP that never came down).
            if (known && !raised && addType is BbBuffAddType.BUFF_RENEW_EXISTING or BbBuffAddType.BUFF_STACKS_AND_RENEWS)
            {
                // The renewal replaced the payload with the sample taken before the variables were handed over; take it again (when the
                // hook that renewed it ends, batch 467).
                ResampleAfterAdd(instance, frame, sampled);
                instance.StatsNextAt = _host.Spells.CurrentTime + StatsUpdateInterval;
                continue;
            }
            _router.RunBuffHook(instance, "OnBuffActivate", request.Caster);
            // Reconstruction (M3): the stats hook runs on its own 0.25 s grid (see StatsUpdateInterval), so a stats hook that reads
            // what OnBuffActivate just wrote (Overload's CooldownBonus, RadianceAura's halved minion bonus) would wait out the first
            // interval. The sample taken before the buff existed could not see it at all, so the hook is run again in this step (when the
            // hook that made the add ends, batch 467: ResampleAfterAdd) — that add or renewal sample is the grid's zero (the next line is
            // this sample's clock plus one interval, see OnTick).
            ResampleAfterAdd(instance, frame, sampled);
            instance.StatsNextAt = _host.Spells.CurrentTime + StatsUpdateInterval;
        }
        return state;
    }

    // Reconstruction (batch 188): a BansheesVeil that has popped (WillRemove) keeps the pop through a renewal. The item's periodic
    // re-add (Items/3102, every 2 s while no BansheesVeilTimer is on the unit) hands over WillRemove = false in NextBuffVars, and
    // the veil's swap (BuffOnUpdateStats, the next 0.25 s sample) has not run yet when the re-add lands inside that window: the
    // renewal would undo the pop and the veil would stay up with no timer (spell-shields-eat-the-first-ability...). The item's
    // no-timer test cannot see a pending swap, so the renewal keeps the pop. Retail's renewal of a popped veil is not recovered.
    private static bool PendingVeilPop(BbBuffInstance instance) =>
        instance.Name == "BansheesVeil" && instance.InstanceVars["WillRemove"] is true;

    private static BbTable Copy(BbTable source)
    {
        var copy = new BbTable();
        foreach (var entry in source.Entries) copy[entry.Key] = entry.Value;
        return copy;
    }

    /// <summary>
    /// <c>BuffOnAllowAdd</c> of every script buff of this module on the target: the hook sets <c>BBSetReturnValue false</c> to refuse a new buff.
    /// Scope: Owner is the holder, Attacker the unit adding, <c>Type</c>, <c>MaxStack</c>, <c>Duration</c> and <c>BuffName</c> describe the add.
    /// </summary>
    public bool AllowAdd(Entity target, Entity adder, string name, ref double duration, double scriptBuffType, double maxStack)
    {
        foreach (var instance in WithHook(target, "BuffOnAllowAdd"))
        {
            if (instance.Name == name || !target.Buffs.Contains(instance.State)) continue;
            if (!_router.RunAllowAdd(instance, adder, name, ref duration, scriptBuffType, maxStack)) return false;
        }
        // Retail: the shipped character script's BuffOnAllowAdd is asked for every add onto its champion too (CharScriptIrelia cuts an enemy's
        // disables by her enemy count). A character is no buff instance, so the loop above never reaches it.
        return _router.RunCharacterAllowAdd(target, adder, name, ref duration, scriptBuffType, maxStack);
    }

    private static BuffState[] Matching(Entity target, string? name, Entity? caster) =>
        target.Buffs.Where(buff => string.Equals(buff.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                   (caster is null || buff.CasterNetId == 0 || buff.CasterNetId == caster.NetId)).ToArray();

    /// <summary>
    /// <c>BBSpellBuffRemove</c>: the oldest application of the named buff that the Attacker made loses one stack (its oldest); the last stack
    /// takes it. This is the shared removal core. The body and the Lua helper's SpellBuffRemove both find the target's buff with the
    /// buff manager's script lookup, the one buff instance of the script. They then erase the first entry of that instance's list whose
    /// caster is the Attacker. The entries of one instance belong to several casters: the shared count <c>SpellBuffCount</c>
    /// is the list size, or with a caster the entries of that caster, from the same single instance. So the
    /// emulator's buffs of one name stand for that list. The target's list keeps them in the order they were added, and a buff's stacks are
    /// oldest first. The first match is therefore the first entry of that caster. (Batch 292 replaced the least-time-left choice.)
    /// Reconstruction: that the retail server appended each application at the end of the list. The client's own fill builds
    /// the entries in index order, but the server's add is not in the client.
    /// One stack per call is retail: <c>BuildingBlocksBase.luaobj</c>'s <c>BBSpellBuffRemoveStacks</c> is a loop of <c>SpellBuffRemove</c>
    /// calls, one per stack (instr 0000-0022). Shipped uses: Irelia's Transcendent Blades spends one of four stacks of
    /// <c>IreliaTranscendentBladesSpell</c> per cast with a single call and reads the count after it, Vayne's Silver Bolts calls it twice per
    /// mark on the others, and Teemo's mushroom charges and Karma's Mantra are counted down the same way. A buff that removes itself by name
    /// from its own hook is no exception: the core erases the first entry of the <c>Attacker</c> per call, whichever stack's hook calls it,
    /// and a hook that removes its own buff runs once per stack (<see cref="RunsPerStack"/>), as retail ran each stack's hook on its own
    /// entry (batch 241). <paramref name="resetDuration"/> above zero restarts the stacks that stay on that instance with that many seconds.
    /// </summary>
    public void Remove(Entity target, string? name, Entity? caster, double resetDuration = 0.0)
    {
        var first = Matching(target, name, caster).FirstOrDefault();
        RemoveStacks(target, name, caster, 1);
        if (resetDuration <= 0.0 || first is null || !target.Buffs.Contains(first)) return;
        // ResetDuration: what stays is restarted with that many seconds, through the stacking add so that the client is told the new timer
        // (count, caster and stats stay). The client has the body: BBSpellBuffRemove reads ResetDuration (default 0) and calls
        // the buff instance's reset on the instance it removed from, which sets start = now and
        // end = now + the reset. The shipped uses are the recharge timers of ammo-like buffs (Akali, Corki) and Riven's passive, which all want
        // the remaining stacks to run for the given time from now.
        _host.Spells.Buffs.AddStacking(target, first.Name, first.Type, resetDuration, first.CasterNetId, Math.Max(1, first.Stacks), first.Payload);
    }

    /// <summary>
    /// <c>BBSpellBuffRemoveStacks</c>: up to <paramref name="count"/> stacks of the named buff end, the oldest application's stacks first and
    /// then the next (see <see cref="Remove"/>). The stacks of one buff end together: one client update (or the removal), however many go.
    /// </summary>
    public void RemoveStacks(Entity target, string? name, Entity? caster, int count)
    {
        while (count > 0)
        {
            var matches = Matching(target, name, caster);
            if (matches.Length == 0) return;
            var buff = matches[0];
            var taken = Math.Min(count, Math.Max(1, buff.Stacks));
            RemoveStack(target, buff, taken);
            count -= taken;
        }
    }

    /// <summary>
    /// Every buff of the name, whole (<c>BBSpellBuffClear</c>: "every buff of that name from the unit's buff manager", and the override buffs the attack
    /// code retires); each buff's stacks end with their own deactivation hooks.
    /// </summary>
    public void RemoveAll(Entity target, string? name, Entity? caster)
    {
        foreach (var buff in Matching(target, name, caster)) RemoveBuff(target, buff);
    }

    /// <summary>
    /// <c>BBSpellBuffClear</c> inside a hook run: the buffs of the name leave now, and their removal packets wait for the end of the run, so a re-add
    /// of the name in the same run takes the slot (<c>BuffLifecycle.RemoveBuffForRenewal</c>). Outside a run it is <see cref="RemoveAll"/>.
    /// </summary>
    public void ClearDeferred(Entity target, string name)
    {
        if (!_host.HasFrame)
        {
            RemoveAll(target, name, null);
            return;
        }
        var frame = _host.Frame;
        foreach (var buff in Matching(target, name, null))
            if (buff.Slot >= 0 && target.Buffs.Contains(buff)) _host.Spells.Buffs.RemoveForRenewal(target, buff.Slot, frame);
    }

    /// <summary>
    /// The end of a hook run: the stats samples its adds deferred (<see cref="ResampleAfterAdd"/>) run now, then the removal packets it held
    /// for a clear that no add of the name took go out.
    /// </summary>
    public void EndRun(BbHookFrame frame)
    {
        while (frame.DeferredResamples is { Count: > 0 } pending)
        {
            frame.DeferredResamples = null;
            foreach (var instance in pending.Distinct()) Resample(instance);
        }
        _host.Spells.Buffs.FlushRenewalRemovals(frame);
    }

    /// <summary>
    /// Batch 467: the stats sample an add takes after its activation (or a renewal after it takes the new variables) runs when the hook
    /// that made the add ends, not inside it. Retail polls <c>BuffOnUpdateStats</c> when the holder's stats are rebuilt, which no running
    /// script can interleave with, and the shipped scripts are written to that order: OdinCaptureChannel's ChannelingStart adds
    /// OdinGuardianSuppression (block 2) before the channeller's team stack (blocks 8 and 9), and the suppression's stats hook clears it
    /// when it counts no team stack (BuffOnUpdateStats block 4). Sampled inside block 2 it counted none and cleared itself in the step
    /// the channel started, so its stun, its meter drain and the hold it gives the guardian barely ran. An add outside any hook run
    /// (the engine's own) samples at once, as before, and so does a buff with no stats hook: its sample runs no script, it only carries
    /// the statuses its <c>OnBuffActivate</c> wrote (<see cref="BbBuffInstance.HeldStatuses"/>). Those are the unit's at once whatever
    /// the buff (the client's native sets the bit at the call, see <see cref="WriteStatus"/>), so a deferred buff takes them now on top of
    /// its add's sample (<paramref name="sampled"/>, per stack; merged as <see cref="Sample"/> merges them, the hook's own write first):
    /// PropelTimer spawns its geyser, adds PropelSpellCaster (NoRender in OnBuffActivate) and binds PropelBubbles.troy to the geyser in the
    /// same hook, and the bind reads NoRender (BbNativeHandlers.IsPlacemarkerCube). When the hook reads nothing the activation changed, that
    /// is the sample the immediate resample used to take, so the deferred one finds the same signature and changes nothing.
    /// </summary>
    private void ResampleAfterAdd(BbBuffInstance instance, BbHookFrame? frame, StatsSample sampled)
    {
        if (frame is null || instance.Script.Event("BuffOnUpdateStats") is null || sampled.Collector is not { } collector)
        {
            Resample(instance);
            return;
        }
        var merged = new BbStatCollector();
        merged.Merge(collector);
        var payload = sampled.Payload.Clone();
        foreach (var (status, value) in instance.HeldStatuses)
        {
            if (merged.Statuses.ContainsKey(status)) continue;
            merged.Set(status, value);
            ApplyStatus(payload, status, value);
        }
        var signature = merged.Signature();
        if (signature != instance.StatsSignature && instance.Owner.Buffs.Contains(instance.State))
        {
            instance.StatsSignature = signature;
            _host.Spells.Buffs.ReplaceScriptPayload(instance.Owner, instance.State, payload);
        }
        (frame.DeferredResamples ??= []).Add(instance);
    }

    private void RemoveBuff(Entity target, BuffState buff)
    {
        if (buff.Slot >= 0 && target.Buffs.Contains(buff)) _host.Spells.Buffs.Remove(target, buff.Slot);
    }

    /// <summary>
    /// The <paramref name="count"/> oldest stacks of a script buff end: each one's own <c>OnBuffDeactivate</c> runs on its own variables, the count falls,
    /// the stats follow and the client is told the new count once. A buff that loses its last stack (or one the runtime does not track) is removed whole.
    /// </summary>
    private void RemoveStack(Entity target, BuffState buff, int count)
    {
        if (!_instances.TryGetValue(buff, out var instance) || count >= buff.Stacks || instance.Stacks.Count <= count)
        {
            RemoveBuff(target, buff);
            return;
        }
        var ended = instance.Stacks.Take(count).ToArray();
        instance.Stacks.RemoveRange(0, count);
        instance.InstanceVars = instance.Stacks[^1].Vars;
        var sample = Sampled(instance);
        if (sample is { } taken)
        {
            instance.StatsSignature = taken.Signature;
            ApplyForeign(instance, taken.Foreign);
        }
        if (!_host.Spells.Buffs.DropScriptStack(target, buff, sample?.Payload ?? new BuffPayload(), count)) return;
        foreach (var stack in ended)
        {
            RunStackHook(instance, stack, "OnBuffDeactivate", scope => scope.Vars["Expired"] = false);
            _host.RetireOwnedEffects(stack.Vars);
        }
    }

    private readonly Dictionary<(BbScript Script, string Hook), bool> _removesItself = new();

    /// <summary>
    /// Whether <paramref name="hook"/> of <paramref name="instance"/> runs once per stack: a hook the engine runs once per buff (every hook but
    /// the activation and the deactivation, which already run per stack) whose blocks remove the buff itself by name (<c>BBSpellBuffRemove</c>
    /// or <c>BBSpellBuffRemoveStacks</c> of its own name on the Owner), on a buff that holds more than one stack. Retail ran each stack's hook on
    /// its own entry, so each run removes what its own table decides (batch 241). Reconstruction: the other once-per-buff hooks still run once on
    /// the newest stack's table, as recorded in docs/scripting/building-blocks-engine.md, E29; no shipped stacking buff removes itself from such a hook (the
    /// batch 241 audit), so the per-stack run only changes a hook that does.
    /// </summary>
    internal bool RunsPerStack(BbBuffInstance instance, string hook)
    {
        if (instance.Stacks.Count < 2 || hook is "OnBuffActivate" or "OnBuffDeactivate") return false;
        if (!_removesItself.TryGetValue((instance.Script, hook), out var removes))
        {
            var name = instance.Script.Name;
            removes = instance.Script.Walk().Any(entry => entry.Hook == hook &&
                entry.Block.Function is "BBSpellBuffRemove" or "BBSpellBuffRemoveStacks" &&
                string.Equals(entry.Block.Text("BuffName"), name, StringComparison.OrdinalIgnoreCase) &&
                entry.Block.Text("TargetVar") is null or "Owner");
            _removesItself[(instance.Script, hook)] = removes;
        }
        return removes;
    }

    /// <summary>
    /// Runs <paramref name="run"/> once for each stack of <paramref name="instance"/> still held (oldest first), with the stack's own variables
    /// and spell chain as the buff's while it runs (see <see cref="RunsPerStack"/>). A stack a run before it removed is skipped.
    /// </summary>
    internal void ForEachStack(BbBuffInstance instance, Action<BbTable> run)
    {
        var (vars, slot, rank, cost) = (instance.InstanceVars, instance.SpellSlot, instance.SpellRank, instance.ManaCost);
        try
        {
            foreach (var stack in instance.Stacks.ToArray())
            {
                if (!instance.Stacks.Contains(stack) || !instance.Owner.Buffs.Contains(instance.State)) continue;
                (instance.InstanceVars, instance.SpellSlot, instance.SpellRank, instance.ManaCost) = (stack.Vars, stack.SpellSlot, stack.SpellRank, stack.ManaCost);
                run(stack.Vars);
            }
        }
        finally
        {
            (instance.SpellSlot, instance.SpellRank, instance.ManaCost) = (slot, rank, cost);
            // The newest stack's table is the buff's again (RemoveStack's rule), or the one it had when no stack is left.
            instance.InstanceVars = instance.Stacks.Count > 0 ? instance.Stacks[^1].Vars : vars;
        }
    }

    /// <summary>A hook of one stack of a buff: the stack's own variables and spell chain are the buff's while it runs.</summary>
    private void RunStackHook(BbBuffInstance instance, BbBuffStack stack, string hook, Action<BbScope>? prepare = null)
    {
        var (vars, slot, rank, cost) = (instance.InstanceVars, instance.SpellSlot, instance.SpellRank, instance.ManaCost);
        (instance.InstanceVars, instance.SpellSlot, instance.SpellRank, instance.ManaCost) = (stack.Vars, stack.SpellSlot, stack.SpellRank, stack.ManaCost);
        try { _router.RunBuffHook(instance, hook, _host.Spells.World.Get(instance.CasterNetId), prepare: prepare); }
        finally { (instance.InstanceVars, instance.SpellSlot, instance.SpellRank, instance.ManaCost) = (vars, slot, rank, cost); }
    }

    public void RemoveCurrent(Entity target)
    {
        // The sample taken before the buff exists (BbHookFrame.PreActivation) has no current buff to end: retail never runs a
        // buff's hooks before its activation, and the run that is about to be that buff's own sample ends it properly
        // (TurretBonusHealth's one-shot initializer removes itself in the run that applies its health).
        if (_host.HasFrame && _host.Frame.PreActivation) return;
        var instance = _host.Frame.Buff ?? throw new BbRuntimeException("BBSpellBuffRemoveCurrent runs outside a buff hook");
        if (instance.State.Slot >= 0 && target.Buffs.Contains(instance.State))
        {
            // NOT RETAIL, batch 729 follow-up: a hidden trap's own script removing its buff is its spring (Bushwhack's trigger), which the flush shows
            // (Entity.SpringRevealRadius). The removal is the spring only for that trap: its expiry ends the buff on the lifecycle's timer instead.
            if (target.SpringRevealRadius > 0.0) target.SprungThisFlush = true;
            _host.Spells.Buffs.Remove(target, instance.State.Slot);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // BuffOnUpdateStats
    // ------------------------------------------------------------------------------------------------------------

    private readonly record struct StatsSample(BuffPayload Payload, string Signature, Dictionary<Entity, BuffPayload>? Foreign = null,
        BbStatCollector? Collector = null);

    /// <summary>The internal buff that carries the stats a buff's stats hook asked for on another unit (<see cref="ApplyForeign"/>).</summary>
    internal const string ForeignStatsPrefix = "BBStats_";

    /// <summary>
    /// The stats a buff's stats hook asked for on units other than its holder (YorickSummonSpectral, held by Yorick, gives its ghoul attack
    /// damage and speed): each such unit holds an internal buff of its own that carries them, replaced when the hook asks for something else and
    /// removed with the buff. Batch 222 (item 2c): retail rebuilds the other unit's accumulators from the hook on every update; this engine replaces the
    /// payload whenever the sample changes (the signature covers the foreign totals), which gives the same values.
    /// </summary>
    private void ApplyForeign(BbBuffInstance instance, Dictionary<Entity, BuffPayload>? foreign)
    {
        foreach (var (netId, state) in instance.ForeignStats.ToArray())
        {
            if (foreign is not null && foreign.Keys.Any(unit => unit.NetId == netId)) continue;
            instance.ForeignStats.Remove(netId);
            if (_host.Spells.World.Get(netId) is { } gone && state.Slot >= 0 && gone.Buffs.Contains(state))
                _host.Spells.Buffs.Remove(gone, state.Slot);
        }
        if (foreign is null) return;
        var name = ForeignStatsPrefix + instance.Name;
        foreach (var (unit, payload) in foreign)
        {
            if (!unit.Alive) continue;
            if (instance.ForeignStats.TryGetValue(unit.NetId, out var existing) && unit.Buffs.Contains(existing))
                _host.Spells.Buffs.ReplaceScriptPayload(unit, existing, payload);
            else if (_host.Spells.Buffs.Add(unit, name, BuffType.Internal, 25000.0, instance.Owner.NetId, payload, canMitigateDuration: false) is { } added)
                instance.ForeignStats[unit.NetId] = added;
        }
    }

    /// <summary>
    /// Runs <c>BuffOnUpdateStats</c> once against the buff's variables and turns the stat deltas and statuses into a payload the
    /// existing stat pipeline (ApplyBuffStats, HeroStatsService) already understands. The engine samples it when the buff is added
    /// or renewed, again after OnBuffActivate, after an update hook changed a variable it carries (<see cref="SampledVars"/>),
    /// and then on the 0.25 s stats grid of every update (<see cref="OnTick"/>, <see cref="StatsUpdateInterval"/>), replacing
    /// the payload when the hook asked for something different.
    /// </summary>
    private StatsSample Sample(BbScript script, Entity owner, Entity caster, BbTable vars, BbHookFrame? parent, BbBuffInstance? instance)
    {
        var payload = new BuffPayload();
        if (script.Event("BuffOnUpdateStats") is null)
        {
            // No stats hook: only the statuses the buff holds (HeldStatuses) can be in the sample.
            if (instance is null || instance.HeldStatuses.Count == 0) return new StatsSample(payload, "");
            var held = new BbStatCollector();
            foreach (var (status, value) in instance.HeldStatuses) held.Set(status, value);
            foreach (var (status, value) in held.Statuses) ApplyStatus(payload, status, value);
            return new StatsSample(payload, held.Signature());
        }
        var collector = new BbStatCollector();
        // Target is the holder in every buff hook (shipped scripts read the buff owner as "Target": GarenRecouperate's stats hook,
        // CardmasterSlow's activation, ChampionChampionDelta).
        var scope = _router.NewScope(owner, caster, owner, caster,
            parent is { SpellRank: >= 1 } ? parent.SpellRank : instance is { SpellRank: >= 1 } ? instance.SpellRank : null, vars);
        if (instance is not null) scope.Vars["LifeTime"] = instance.LifeTime;
        var frame = new BbHookFrame("BuffOnUpdateStats", script.Name, owner)
        {
            Caster = caster, SpellSlot = parent?.SpellSlot ?? instance?.SpellSlot ?? -1,
            SpellRank = parent?.SpellRank ?? instance?.SpellRank ?? 0,
            ManaCost = parent?.ManaCost ?? instance?.ManaCost ?? 0.0, Stats = collector, Buff = instance,
            IsResample = instance is not null, PreActivation = instance is null,
            MutationDue = instance is not null && _host.Spells.CurrentTime + 1e-9 >= instance.MutateNextAt,
        };
        _router.Run(script, "BuffOnUpdateStats", scope, frame, rethrow: true);
        // Batch 667: a live buff's stats hook that writes a status's rest value clears the unit's one bit of it, as a write from any other
        // hook does (WriteStatus), so it lets go of the unit's script status every sample. HalloweenUrfWarwick shows the Ghost of Urf only
        // this way (BuffOnUpdateStats SetNoRender false, pc 0250-0260, over CharScriptUrf's CharOnActivate SetNoRender true; OnBuffDeactivate
        // hides him again); before, the sample dropped the rest write and the ghost slapped Urf-skin Warwick unseen. Of the shipped stats
        // hooks only six writes are rest writes (b667 audit): this one, BlindMonkRNewMinion SetNoRender, Destealth SetStealthed and SpellImmunity
        // SetCanMove/SetCanAttack/SetCanCast, whose OnBuffActivate already writes the same rest value. Reconstruction: other buffs' holds
        // stay (the holder model, batch 222); only the bit no buff holds is released.
        if (instance is not null)
            foreach (var (status, value) in collector.Statuses)
                if (!StatusActs(status, value) && ReleaseScriptStatus(owner, status)) PushMinionStatus(owner, status);
        // Reconstruction M4: the bare mutations of a sample that was due have accumulated (BbHookFrame.MutationSuppressed);
        // the clock moves one tick on, overdue time kept the way StatsNextAt's is. Batch 673 (Reconstruction): a buff the add gave no
        // TickRate has no tick, so every sample after its activation is due. Its "once and never again" dropped the guarded one-shots of
        // the only shipped TickRate-0 stats hooks with mutations: OdinGuardianBuff's team-change branch (BuffOnUpdateStats 4: it removed
        // the old 800 bubbles and its add of the new owner's was held back, so a captured point gave nobody its sight). The other one,
        // TurretBonusHealth, ends itself in that first sample (BBSpellBuffRemoveCurrent), so it applies once either way.
        if (instance is not null && frame.MutationDue && instance.TickRate > 0.0)
            instance.MutateNextAt += instance.TickRate;
        foreach (var colours in collector.ParColorCalls) _host.ApplyParColorOverride(owner, colours);
        if (instance is not null)
            foreach (var (status, value) in instance.HeldStatuses)
                if (!collector.Statuses.ContainsKey(status)) collector.Set(status, value);
        foreach (var (stat, value) in collector.Totals) ApplyStat(payload, stat, value);
        foreach (var (status, value) in collector.Statuses) ApplyStatus(payload, status, value);
        ApplyMaxHealth(payload, collector);
        Dictionary<Entity, BuffPayload>? foreign = null;
        if (collector.Foreign.Count > 0 || collector.ForeignStatuses.Count > 0)
        {
            foreign = new Dictionary<Entity, BuffPayload>(ReferenceEqualityComparer.Instance);
            foreach (var (other, totals) in collector.Foreign)
            {
                var other2 = new BuffPayload();
                foreach (var (stat, value) in totals) ApplyStat(other2, stat, value);
                foreign[other] = other2;
            }
            // The statuses another unit holds through this buff (batch 318): they ride on that unit's carrier payload, with its stats.
            foreach (var (other, statuses) in collector.ForeignStatuses)
            {
                if (!foreign.TryGetValue(other, out var carried)) foreign[other] = carried = new BuffPayload();
                foreach (var (status, value) in statuses) ApplyStatus(carried, status, value);
            }
        }
        return new StatsSample(payload, collector.Signature(), foreign, collector);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The periodic a buff's own update hook schedules
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The damage-over-time a buff's own <c>BuffOnUpdateActions</c> schedules, read from its blocks:
    /// <c>BBExecutePeriodically</c>'s <c>TimeBetweenExecutions</c> and the flat per-tick amount of its
    /// <c>BBApplyDamage</c> child. SummonerDot.luaobj is the shipped case — five true-damage pulses of
    /// <c>DamageByLevel</c> one second apart over a five second buff — and it is the one place the numbers come from,
    /// so nothing here is a summoner table.
    /// <para>
    /// The amount is the call's own flat sum, <c>(Damage + DamageByLevel[Level] + DamageVar[...]) * PercentOfAttack</c>. The
    /// <c>SpellDamageRatio</c> / <c>PhysicalDamageRatio</c> coefficients do not enter it: a periodic is flat
    /// and, as docs/scripting/building-blocks-engine.md "ApplyDamage ratio fields" records, which is also how
    /// <c>ItemPassiveService.AddMourning</c> reads <c>Mourning.luaobj</c>'s identical call. The level is the one the
    /// add's <c>BuffVarsTable</c> carried (<c>TargetExecute</c>'s <c>BBGetLevel</c> of the caster), which is what the
    /// hook's own <c>BBSetVarInTable</c> copies into <c>Level</c> before the call runs.
    /// </para>
    /// <para>
    /// No pulse cap is recorded (batch 212). <c>BBExecutePeriodically</c> runs every due time inside the buff's life: the
    /// engine's <c>ExecutePeriodically</c> applies it (<c>ScriptSchedulesPeriodic</c> keeps the engine ticker off), so the
    /// count is the shipped rule's. A due time that lands exactly on the expiry does not run there (that end test is the
    /// engine's). Reconstruction: the expiry order is not recovered, so today's order is kept.
    /// </para>
    /// </summary>
    private static void FillScriptPeriodic(BbScript? script, BuffPayload payload, BbTable vars, Entity caster)
    {
        if (script is null || payload.Period > 0.0) return;
        foreach (var (hook, block) in script.Walk())
        {
            if (hook != "BuffOnUpdateActions" ||
                !string.Equals(block.Function, "BBExecutePeriodically", StringComparison.Ordinal)) continue;
            var period = block.Number("TimeBetweenExecutions") ?? 0.0;
            if (period <= 0.0) continue;
            var damage = block.SubBlocks.FirstOrDefault(child =>
                string.Equals(child.Function, "BBApplyDamage", StringComparison.Ordinal));
            if (damage is null) continue;
            payload.Period = period;
            payload.DamagePerTick = TickDamage(damage, vars);
            payload.DamageType = TickDamageType(damage);
            // Batch 159 part 2: the tick's own category is the call's SourceDamageType (Burning.luaobj's
            // BBApplyDamage names DAMAGESOURCE_PROC), so the payload a death recap and a check read carries it too.
            if (TickDamageSource(damage) is { } kind) payload.SourceKind = kind;
            // And the tick's own recap identity: a periodic whose BBApplyDamage states no recap of its own is credited
            // at the pulse by BbNativeHandlers.BBApplyDamage to the world-script entry of the running script, so the
            // payload carries that same origin (Spells/Burning.luaobj's burn is WorldScript(<caster>, "Burning"), the
            // twin of what the hand-written Dragon and item burns wrote). A script the catalog does not name keeps no
            // origin — the same refusal BbNativeHandlers.WorldScriptOrigin gives, so the hit falls through to whatever
            // else its chain carries.
            var origin = DeathRecapOrigin.WorldScript(caster, script.Name);
            if (origin != DeathRecapOrigin.Unclassified) payload.DamageOrigin = origin;
            payload.SourceIsPeriodic = true;
            // The numbers above describe the DoT the shipped blocks run; the engine's own ticker must not run it again.
            payload.ScriptSchedulesPeriodic = true;
            return;
        }
    }

    /// <summary>The flat per-tick amount of a periodic's <c>BBApplyDamage</c> child (see <see cref="FillScriptPeriodic"/>).</summary>
    private static double TickDamage(BbBlock damage, BbTable vars)
    {
        var level = BbRuntime.NumberOrNull(vars["Level"]) ?? 1.0;
        var total = damage.Number("Damage") ?? 0.0;
        if (damage.Param("DamageByLevel") is BbValue.Table byLevel &&
            BbRuntime.Index(byLevel, level) is { } scaled)
            total += BbRuntime.NumberOrNull(scaled) ?? 0.0;
        // The variable part of the same sum (BbNativeHandlers.Sum): EntropyBurning's tick is a Damage=0 with
        // DamageVar='TickDamage' read from InstanceVars — the 40 its window's hit add hands over in NextBuffVars,
        // which is this table. Reconstruction: only the add's own vars are read here (the table InstanceVars
        // becomes); a name it does not hold counts 0, like the client's number getter.
        if (damage.Param("DamageVar") is BbValue.Text { Value: var variable })
            total += BbRuntime.NumberOrNull(vars[variable]) ?? 0.0;
        return total * (damage.Number("PercentOfAttack") ?? 1.0);
    }

    /// <summary>The <c>DamageType</c> global a periodic's <c>BBApplyDamage</c> child names (default physical, as the call itself).</summary>
    private static global::Amberline.Core.DamageType TickDamageType(BbBlock damage) =>
        damage.Param("DamageType") is BbValue.Reference { Name: var name } ? name switch
        {
            "TRUE_DAMAGE" => global::Amberline.Core.DamageType.True,
            "MAGIC_DAMAGE" => global::Amberline.Core.DamageType.Magic,
            _ => global::Amberline.Core.DamageType.Physical,
        } : global::Amberline.Core.DamageType.Physical;

    /// <summary>The <c>SourceDamageType</c> global a periodic's <c>BBApplyDamage</c> child names, through the one map the
    /// live call goes through (<see cref="BbNativeHandlers.SourceKind"/>); a call that names none keeps the payload's own.</summary>
    private static global::Amberline.Core.DamageSourceKind? TickDamageSource(BbBlock damage) =>
        damage.Param("SourceDamageType") is BbValue.Reference { Name: var name } &&
        Enum.TryParse<BbDamageSource>(name, out var source)
            ? BbNativeHandlers.SourceKind((int)source).Kind
            : null;

    /// <summary>The sample taken while the buff does not exist yet: a hook that cannot run before activation yields nothing.</summary>
    private StatsSample TrySample(BbScript script, Entity owner, Entity caster, BbTable vars, BbHookFrame? parent, BbBuffInstance? instance)
    {
        try { return Sample(script, owner, caster, vars, parent, instance); }
        catch (Exception ex) when (ex is BbRuntimeException) { return new StatsSample(new BuffPayload(), ""); }
    }

    /// <summary>
    /// The <c>InstanceVars</c> names a buff's own <c>BuffOnUpdateStats</c> reads or writes through one of its
    /// <c>*VarTable InstanceVars</c> fields: <c>BBIncStat</c>'s <c>DeltaVar</c> (the share the payload carries), the
    /// <c>BBMath</c> sources one is computed from. They are what the sample is taken from, so one of them moving leaves the
    /// payload this buff holds stale (see <see cref="OnTick"/>).
    /// </summary>
    private static readonly Dictionary<BbScript, IReadOnlyList<string>> SampledVarsCache = new(ReferenceEqualityComparer.Instance);

    private static IReadOnlyList<string> SampledVars(BbScript script)
    {
        if (SampledVarsCache.TryGetValue(script, out var names)) return names;
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (hook, block) in script.Walk())
        {
            if (hook != "BuffOnUpdateStats") continue;
            foreach (var field in block.Params)
            {
                // "DeltaVar" -> "DeltaVarTable", "Src2Var" -> "Src2VarTable"; a field with no table names the run's own root
                // variable, which no other hook reads.
                if (!field.Key.EndsWith("Var", StringComparison.Ordinal) ||
                    block.Text(field.Key + "Table") is not { } table ||
                    !string.Equals(table, "InstanceVars", StringComparison.Ordinal)) continue;
                if (field.Value is BbValue.Text { Value: var name }) set.Add(name);
            }
        }
        names = set.ToArray();
        SampledVarsCache[script] = names;
        return names;
    }

    /// <summary>Re-runs the stats hook of a live buff and swaps its payload when the sample changed.</summary>
    private void Resample(BbBuffInstance instance)
    {
        if ((instance.Script.Event("BuffOnUpdateStats") is null && instance.HeldStatuses.Count == 0) ||
            !instance.Owner.Buffs.Contains(instance.State)) return;
        if (Sampled(instance) is not { } sample) return;
        // The hook may have removed its own buff (a shield that popped).
        if (!instance.Owner.Buffs.Contains(instance.State) || sample.Signature == instance.StatsSignature) return;
        instance.StatsSignature = sample.Signature;
        ApplyForeign(instance, sample.Foreign);
        _host.Spells.Buffs.ReplaceScriptPayload(instance.Owner, instance.State, sample.Payload);
    }

    /// <summary>
    /// The stats hook of a live buff run now: the per-stack payload the lifecycle scales by the stack count, carrying the flags the live payload
    /// has outside the stats hook, or null when the hook raised (reported).
    /// </summary>
    private StatsSample? Sampled(BbBuffInstance instance)
    {
        var caster = _host.Spells.World.Get(instance.CasterNetId) ?? instance.Owner;
        StatsSample sample;
        try { sample = Sample(instance.Script, instance.Owner, caster, instance.InstanceVars, parent: null, instance); }
        catch (Exception ex) when (ex is BbRuntimeException)
        {
            _router.Fail(instance.Name, "BuffOnUpdateStats", ex);
            return null;
        }
        if (instance.State.Payload.PerCaster) sample.Payload.PerCaster = true;
        if (instance.State.Payload.ActionsAllowed) sample.Payload.ActionsAllowed = true;
        return sample;
    }

    /// <summary>The payload a stats hook's collected deltas and statuses make (the character script's <c>UpdateSelfBuffStats</c>).</summary>
    internal static BuffPayload PayloadOf(BbStatCollector collector, bool statuses = true)
    {
        var payload = new BuffPayload();
        foreach (var (stat, value) in collector.Totals) ApplyStat(payload, stat, value);
        // Batch 440: a permanent carrier's status is a fixed flag no later write can release. A bound unit's CharOnActivate statuses are
        // held by the unit's script status instead (BbHookRouter.RunCharOnActivate), so the carrier takes stats only when asked to.
        if (statuses) foreach (var (status, value) in collector.Statuses) ApplyStatus(payload, status, value);
        ApplyMaxHealth(payload, collector);
        return payload;
    }

    /// <summary>The <c>BBIncMaxHealth</c> deltas of a hero's stats hook: the maximum health, and the part of it that heals nobody.</summary>
    private static void ApplyMaxHealth(BuffPayload payload, BbStatCollector collector)
    {
        if (collector.ParRegenFlat != 0.0) payload.MpRegen += collector.ParRegenFlat;
        if (collector.ParRegenPercent != 0.0) payload.MpRegenMult += collector.ParRegenPercent;
        if (collector.ParPoolFlat != 0.0) payload.MaxMp += collector.ParPoolFlat;
        if (collector.ParPoolPercent != 0.0) payload.MaxMpMult += collector.ParPoolPercent;
        if (collector.ScaleSkinCoef != 0.0) payload.ScaleSkinCoef += collector.ScaleSkinCoef;
        if (collector.MaxHealth == 0.0) return;
        payload.MaxHp += collector.MaxHealth;
        payload.NonHealingMaxHp += collector.NonHealingMaxHealth;
    }

    private static void ApplyStat(BuffPayload payload, BbIncStat stat, double value)
    {
        switch (stat)
        {
            case BbIncStat.IncFlatArmorMod: payload.Armor += value; break;
            case BbIncStat.IncFlatSpellBlockMod: payload.SpellBlock += value; break;
            case BbIncStat.IncFlatMagicDamageMod: payload.AbilityPower += value; break;
            case BbIncStat.IncFlatPhysicalDamageMod: payload.AttackDamage += value; break;
            case BbIncStat.IncFlatHPPoolMod: payload.MaxHp += value; break;
            // The experience bonus (Zilean's Heightened Learning) joins the loadout's in HeroStats; the acquisition range (Sona's Power Chord) is
            // read when an idle hero scans for a target.
            case BbIncStat.IncPercentEXPBonus: payload.ExperiencePercent += value; break;
            // PositiveChampionDelta.luaobj's other row (batch 159 part 3): 0.1 x T of the team-size catch-up ramp, which the
            // hero's respawn timer multiplies as 1 - the sum (HeroStats.TimeDeadPercent, the same field the rune and mastery
            // rows feed). The hand-written TeamSizeCatchUp reader stood in for this pair; both are the script's now.
            case BbIncStat.IncPercentRespawnTimeMod: payload.RespawnTimePercent += value; break;
            // No shipped item record carries FlatGoldPer10Mod (only the rune records carry rFlatGoldPer10Mod), so a
            // gold item's income is this row alone — the window buff of Items/3093, 3096, 3098 and 3132 writes +5.
            case BbIncStat.IncFlatGoldPer10Mod: payload.GoldPer10 += value; break;
            case BbIncStat.IncAcquisitionRangeMod: payload.AcquisitionRange += value; break;
            case BbIncStat.IncFlatMovementSpeedMod: payload.MoveSpeedFlat += value; break;
            case BbIncStat.IncPercentMovementSpeedMod: payload.MoveSpeedPercent += value; break;
            // The multiplicative speed accessor is a slow when negative and a haste when positive; the speed calculator
            // composes the payload's multiplier and its slows the way the shared stages do.
            case BbIncStat.IncPercentMultiplicativeMovementSpeedMod: payload.MoveSpeedMult += value; break;
            case BbIncStat.IncPercentArmorMod: payload.ArmorMult += value; break;
            case BbIncStat.IncPercentSpellBlockMod: payload.SpellBlockMult += value; break;
            case BbIncStat.IncPercentAttackSpeedMod: payload.AttackSpeedMod += value; break;
            case BbIncStat.IncPercentCooldownMod: payload.CooldownMod += value; break;
            case BbIncStat.IncPercentSpellVampMod: payload.SpellVamp += value; break;
            case BbIncStat.IncFlatAttackRangeMod: payload.AttackRange += value; break;
            case BbIncStat.IncFlatDodgeMod: payload.Dodge += value; break;
            // The four damage reduction stats (CharacterIntermediate mFlat/PercentPhysical/MagicReduction): read when the holder takes a hit
            // (CombatService.ApplyReduction), so a buff needs no stat refresh to take effect.
            case BbIncStat.IncFlatPhysicalReduction: payload.FlatPhysicalReduction += value; break;
            case BbIncStat.IncFlatMagicReduction: payload.FlatMagicReduction += value; break;
            case BbIncStat.IncPercentPhysicalReduction: payload.PercentPhysicalReduction += value; break;
            case BbIncStat.IncPercentMagicReduction: payload.PercentMagicReduction += value; break;
            // The sight radius of the holder: the client's (percent + 1) * base + flat, see VisibilityRules.BubbleRadiusMods.
            case BbIncStat.IncFlatBubbleRadiusMod: payload.BubbleRadiusFlat += value; break;
            case BbIncStat.IncPercentBubbleRadiusMod: payload.BubbleRadiusPercent += value; break;
            // More accessors with a payload field the stat calculator already reads (HeroStats): the multiplicative attack speed factor is
            // taken once per payload (21 stats hooks), life steal and regeneration are fractions added to the item totals, the penetrations
            // are the same flat and percent fields the rune and item code fill.
            case BbIncStat.IncPercentMultiplicativeAttackSpeedMod: payload.AttackSpeedMult += value; break;
            case BbIncStat.IncPercentLifeStealMod: payload.LifeSteal += value; break;
            case BbIncStat.IncFlatHPRegenMod: payload.HpRegen += value; break;
            case BbIncStat.IncPercentHPRegenMod: payload.HpRegenMult += value; break;
            case BbIncStat.IncFlatMissChanceMod: payload.MissChance += value; break;
            case BbIncStat.IncFlatCritChanceMod: payload.CritChance += value; break;
            // The crit damage accessor adds to the 2.0 base multiplier (HeroStats: 2 + items + active payloads): Shaco's DeceiveCritBonus is
            // -0.6 .. +0.2 by rank, Infinity Edge +0.5.
            case BbIncStat.IncFlatCritDamageMod: payload.CritDamage += value; break;
            case BbIncStat.IncFlatArmorPenetrationMod: payload.ArmorPenFlat += value; break;
            case BbIncStat.IncPercentArmorPenetrationMod: payload.ArmorPenPercent += value; break;
            case BbIncStat.IncFlatMagicPenetrationMod: payload.MagicPenFlat += value; break;
            case BbIncStat.IncPercentMagicPenetrationMod: payload.MagicPenPercent += value; break;
            default: throw BbSpellHost.Unbound($"{stat} (no BuffPayload field)", "M3");
        }
    }

    /// <summary>The statuses <see cref="ApplyStatus"/> turns into a payload flag.</summary>
    internal static bool CanApplyStatus(BbSetStatus status) => status is BbSetStatus.SetCanMove or BbSetStatus.SetRooted or
        BbSetStatus.SetInvulnerable or BbSetStatus.SetDisarmed or BbSetStatus.SetCanAttack or BbSetStatus.SetPacified or
        BbSetStatus.SetStealthed or BbSetStatus.SetTargetable or BbSetStatus.SetMagicImmune or BbSetStatus.SetPhysicalImmune or
        BbSetStatus.SetGhosted or BbSetStatus.SetNoRender or BbSetStatus.SetSilenced or BbSetStatus.SetCanCast or
        BbSetStatus.SetForceRenderParticles or BbSetStatus.SetSuppressCallForHelp or BbSetStatus.SetCallForHelpSuppresser or BbSetStatus.SetIgnoreCallForHelp or
        BbSetStatus.SetStunned or BbSetStatus.SetSuppressed or BbSetStatus.SetGhostProof;

    /// <summary>Whether any block of the script (its pre-load and its event tables, sub-blocks included) is a <c>BBSetStatus</c> (batch 222).</summary>
    private static bool ScriptWritesStatus(BbScript script)
    {
        static bool Writes(BbBlock block) => block.Function == "BBSetStatus" || block.SubBlocks.Any(Writes);
        return script.PreLoad.Any(Writes) || script.Events.Any(table => table.Blocks.Any(Writes));
    }

    private static void ApplyStatus(BuffPayload payload, BbSetStatus status, bool value)
    {
        switch (status)
        {
            // SetCanMove(false) and SetRooted(true) both hold the unit in place; the lifecycle already treats the payload flag
            // as a root (IsRooted), clears the route and tells the client. SetRooted also clears CanAttack: the client's SetRooted
            // calls SetCanMove (bit 2) and SetCanAttack (bit 0) with !rooted, where SetNetted
            // calls only SetCanMove. Root.luaobj (the buff BBApplyRoot adds) writes SetRooted in OnBuffActivate and each
            // stats update, so a rooted unit cannot attack either (batch 320).
            case BbSetStatus.SetCanMove: if (!value) payload.MovementDisabled = true; break;
            case BbSetStatus.SetRooted: if (value) payload.MovementDisabled = payload.Disarmed = true; break;
            case BbSetStatus.SetInvulnerable: if (value) payload.Invulnerable = true; break;
            // The CanAttack status bit (CharacterState): SetDisarmed(true) and SetCanAttack(false) clear it, SetPacified(true)
            // clears it with CanCast. Pets, minions, the swing loop and the cast gate all read the payload through CharacterState.
            case BbSetStatus.SetDisarmed: if (value) payload.Disarmed = true; break;
            case BbSetStatus.SetCanAttack: if (!value) payload.Disarmed = true; break;
            case BbSetStatus.SetPacified: if (value) payload.Pacified = true; break;
            // The other status bits the unit-state readers (visibility, targeting, damage, cast gate, render state) already take from a
            // buff payload. A bit whose default is "on" (SetTargetable, SetCanCast) acts when the hook turns it off.
            case BbSetStatus.SetStealthed: if (value) payload.Stealthed = true; break;
            case BbSetStatus.SetTargetable: if (!value) payload.Untargetable = true; break;
            case BbSetStatus.SetMagicImmune: if (value) payload.MagicImmune = true; break;
            case BbSetStatus.SetPhysicalImmune: if (value) payload.PhysicalImmune = true; break;
            case BbSetStatus.SetGhosted: if (value) payload.Ghosted = true; break;
            // The ghost-proof bit (CharacterState 11): the client's collision lets a ghosted unit pass every body but a ghost-proof one
            // (ClientActorGrid: a ghosted mover skips an actor unless it is ghost-proof), which the movement rules already read from the entity.
            case BbSetStatus.SetGhostProof: if (value) payload.GhostProof = true; break;
            case BbSetStatus.SetNoRender: if (value) payload.NoRender = true; break;
            case BbSetStatus.SetSilenced: if (value) payload.Silenced = true; break;
            case BbSetStatus.SetCanCast: if (!value) payload.Silenced = true; break;
            // ForceRenderParticles is bit 14 of the CharacterState word (CharacterState.ActionWord); the two call-for-help flags are the
            // victim's and the responder's side of the same rule (CombatService.CallForHelp / CallLaneHelp).
            // SetStunned (OrianaStun, the stun of Orianna's Command: Shockwave): the unit's CanAttack, CanCast and CanMove bits are clear, the same
            // hard control the Stun buff type and the ActionDisabled payload stand for (CharacterState.ActionWord).
            case BbSetStatus.SetStunned: if (value) payload.ActionDisabled = true; break;
            // ForceRenderParticles is bit 14 of the CharacterState word (CharacterState.ActionWord): the expiring Orianna ball renders its particles although it is NoRender.
            case BbSetStatus.SetForceRenderParticles: if (value) payload.ForceRenderParticles = true; break;
            // The call-for-help bits (YorickRADelay, the Orianna ball's ExpirationTimer): CombatService.CallForHelp reads them.
            case BbSetStatus.SetSuppressCallForHelp: if (value) payload.SuppressCallForHelp = true; break;
            case BbSetStatus.SetCallForHelpSuppresser: if (value) payload.CallForHelpSuppresser = true; break;
            case BbSetStatus.SetIgnoreCallForHelp: if (value) payload.IgnoreCallForHelp = true; break;
            // The Suppressed bit (Suppression.luaobj and Skarner's Impale set it next to SetStunned, which is the one that clears the actions): bit 7 of the
            // status word. The BUFF_Suppression type of the callers already gives the same bit; the payload gives it to a script buff of any other type.
            case BbSetStatus.SetSuppressed: if (value) payload.Suppressed = true; break;
            default: throw BbSpellHost.Unbound($"{status} (no BuffPayload flag)", "M3");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Lifecycle callbacks (BuffLifecycle -> module / hook sink)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BuffOnUpdateActions</c>: once per <see cref="ActionsUpdateInterval"/> when TickRate is 0, else once per TickRate with
    /// overdue time kept. <c>BuffOnUpdateStats</c> is not polled here on every update: it runs on its own
    /// <see cref="StatsUpdateInterval"/> grid (at most one sample per update, overdue time kept), the way the native keeps a
    /// <c>mLastTickTimeUpdateStats</c> stamp beside the update-actions one, and once more whenever this hook moves a variable
    /// that sample carries (<see cref="SampledVars"/>). Both grids are instants of the game clock the hooks themselves read,
    /// counted from the sample the add or renewal took (<see cref="BbBuffInstance.StatsNextAt"/>,
    /// <see cref="BbBuffInstance.ActionsNextAt"/>).
    /// </summary>
    public void OnTick(Entity unit, BuffState buff, double dt)
    {
        if (!_instances.TryGetValue(buff, out var instance)) return;
        // Reconstruction: LifeTime is a variable the engine hands to every hook of a buff (18 shipped scripts read it: the three ward buffs wake
        // at 2, Hexdrinker and Gragas' barrel subtract it from their durations, Janna's Howling Gale picks its wave tier from it). It is the
        // time the buff has been on its holder when the update runs. The update that finds the buff run out has seen the whole duration and the
        // step that ended it, so a buff of 3 seconds reads more than 3 there: the full charge of Howling Gale is the branch no recast reaches.
        instance.Age += dt;
        instance.LifeTime = instance.Age + (buff.ExpiresAt - dt <= 1e-9 ? dt : 0.0);
        // The instant this life runs out: the countdown is the time left at the start of the step, and the spell clock
        // already counts the step (SpellSystem.Tick runs before the lifecycle's tick), so the step is taken back off.
        instance.LifeEndsAt = _host.Spells.CurrentTime + buff.ExpiresAt - dt;
        // EndOfGame.luaobj's HaltAllAI clears every order before the ceremony (the services of the pets and the
        // spawned AI families stop their scans). The update hook of a script buff is the AI of a unit that has no other: Jack in the Box's wake scan and
        // fear, the scans of a clone's or a spawned unit's buff. After the match is over it does not run on a unit that is not a hero; the buff's clock
        // (duration, deactivation, the pet's lifetime) goes on like every lifetime, and a hero's buffs are no AI.
        var aiHalted = unit.UnitType != UnitType.Hero && _host.Spells.Combat.GameOver;
        if (!aiHalted && instance.Script.Event("BuffOnUpdateActions") is not null)
        {
            // No TickRate: the buff's own actions clock (ActionsUpdateInterval, see there: both hooks keep a stamp beside one
            // buff tick, and the shipped scripts are written to a quarter second of it). It replaced "every buff update", which
            // was the simulation step: Twisted Fate's shuffle turned a card every other step (66 ms, owner finding 20).
            var due = true;
            if (instance.TickRate > 0.0)
            {
                instance.TickElapsed += dt;
                due = instance.TickElapsed + 1e-9 >= instance.TickRate;
                if (due) instance.TickElapsed -= instance.TickRate;
            }
            else if (!instance.ActionsCarriesPeriodic)
            {
                // Batch 720 (owner decision, 8 November 2026). Reconstruction: no shipped file or source gives the phase (batch 718's
                // research leans weakly this way). A bare hook runs on its holder's 0.25 s pass, the unit's one update line
                // (SpellSystem.SelfUpdateDue, the line the self-buff polls run on: retail's per-unit obj_AI_Base update throttle,
                // srvNextUpdate, under which UpdateBuffActions runs). The first run is the holder's next pass after the add: the line
                // is stepped in SpellSystem.Tick and this hook runs in TickBuffs after it, so an add earlier in a pass step (a dash's
                // landing hit, a cast, an attack's hit) runs in that step, and one after the holder's buff update (between two steps,
                // or inside TickBuffs) waits for the next pass. Batch 175 ran it one interval after the add. A hook that owns a
                // BBExecutePeriodically runs every update and its own period decides (see ActionsCarriesPeriodic). One run per update
                // at most: ActionsNextAt is the next run's not-before, the holder's next line after each run. A line the clock has
                // passed is skipped, never repaid (the holder's line advances by whole intervals): repaying it ran the hook once per
                // simulation step until the debt was gone, which turned Twisted Fate's deck at the step rate (owner finding, 24 October 2026).
                var now = _host.Spells.CurrentTime;
                var nextPass = _host.Spells.SelfUpdateNextAt(unit);
                due = _host.Spells.SelfUpdateDue(unit) && now + 1e-9 >= instance.ActionsNextAt;
                // A buff that ends before the holder's next pass takes that one run at its first update instead (see
                // ActionsFirstLinePending): the shipped applicators live a tenth of a second. The run stands for that pass, so the next
                // one waits a whole line more: the step a buff outlives its computed end by (ExpiresAt <= 0 has no tolerance) never
                // meets the pass for a second run, and a short-lived buff cannot spin.
                if (!due && instance.ActionsFirstLinePending && instance.LifeEndsAt <= nextPass - 1e-9)
                {
                    due = true;
                    nextPass += ActionsUpdateInterval;
                }
                if (due)
                {
                    instance.ActionsNextAt = nextPass;
                    instance.ActionsFirstLinePending = false;
                }
            }
            if (due)
            {
                // The payload carries the instance variables the stats hook sampled (SampledVars); when this hook moves one
                // of them that sample is already stale, and the next grid line is a quarter second away. Spells/GrievousWound.luaobj
                // and Spells/SpiritVisage.luaobj re-derive their life-steal and spell-vamp shares on this hook and carry them
                // into IncPercentLifeStealMod / IncPercentSpellVampMod from the stats hook, so a share that only reaches the
                // pool a grid line later makes the pair (the window's 0.15 * (pool - share) and the wound's -0.5 * (pool -
                // share) feed each other) chase the fixed point their own arithmetic states for whole seconds — longer than
                // the eight-second wound lives. The sample is taken again here, the way the add takes it after OnBuffActivate,
                // so the pool carries the share each update hook re-derives. A variable that did not move is no change (value
                // comparison), so a hook rewriting the same number costs nothing, and a stats hook nothing feeds keeps its
                // own grid: OrianaSlow's and SummonerReviveSpeedBoost's counters run on it and neither script has an update
                // hook at all.
                var sampled = SampledVars(instance.Script);
                Dictionary<string, object?>? before = sampled.Count == 0 ? null : new();
                if (before is not null)
                    foreach (var name in sampled) before[name] = instance.InstanceVars[name];
                _router.RunBuffHook(instance, "BuffOnUpdateActions", _host.Spells.World.Get(instance.CasterNetId));
                if (before is not null)
                    foreach (var (name, value) in before)
                        if (!Equals(instance.InstanceVars[name], value)) { Resample(instance); break; }
            }
        }
        // The stats hook has its own clock (see StatsUpdateInterval): one sample per update at most, overdue time kept
        // (the line moves one interval per sample, so a long step drains its backlog one sample per update), after the
        // update-actions hook of this update the way the add runs it after OnBuffActivate. The line is an instant of the
        // clock the hooks read (GetTime — the one BBExecutePeriodically stamps its due times with), not a sum of update
        // steps: a step-summed grid also counted the update of the add step and so ran every line one step early of the
        // add sample's clock + k * 0.25, which put a BBExecutePeriodically of the stats hook between two samples every
        // time. Janna's ReapTheWhirlwind (TimeBetweenExecutions 0.5, seeded at the add sample) then fired its heal one
        // grid interval late — 0.25 / 0.99 / 1.49 where the script's own timing is 0.25 / 0.75 / 1.25, two pulses by
        // 1.4 s where the check counts three. On this clock the sample that reaches a due line is the one that runs it.
        if (_host.Spells.CurrentTime + 1e-9 >= instance.StatsNextAt)
        {
            instance.StatsNextAt += StatsUpdateInterval;
            Resample(instance);
        }
    }

    /// <summary>
    /// The timer of an ammo buff (<c>BUFF_AmmoStack</c>) ran out (E14). The script's <c>BuffOnUpdateAmmo</c> adds the next charge with
    /// <c>BBSpellBuffAdd</c> (<c>BUFF_STACKS_AND_RENEWS</c>, one stack, the recharge time as the duration, or 25000 when that charge fills the
    /// buff), which renews the timer; the lifecycle keeps the buff when the hook did and ends it as any buff that ran out when it did not.
    /// Akali's Shadow Dance, Corki's Missile Barrage and Karma's Mantra are the shipped users.
    /// </summary>
    public void OnAmmoTimer(Entity unit, BuffState buff)
    {
        if (!_instances.TryGetValue(buff, out var instance) || instance.Script.Event("BuffOnUpdateAmmo") is null) return;
        _router.RunBuffHook(instance, "BuffOnUpdateAmmo", _host.Spells.World.Get(instance.CasterNetId));
    }

    /// <summary><c>OnBuffDeactivate</c>: the lifecycle has already taken the buff off the unit and reversed its stats.</summary>
    public void OnRemoved(Entity unit, BuffState buff)
    {
        if (!_instances.Remove(buff, out var instance)) return;
        if (instance.ForeignStats.Count > 0) ApplyForeign(instance, null);
        // The hook reads Expired: true when the buff ran out (the lifecycle removes a buff whose remaining time reached zero), false
        // when something removed it (a script, a dispel, a replacement, the holder's death). Leblanc, Pantheon, Xin Zhao, Vladimir,
        // Orianna's ball and Nether Grasp branch on it (12 shipped tables).
        // Every stack ends with its own deactivation (E29, retail keeps an instance per stack): oldest first, on its own variables.
        var expired = buff.ExpiresAt <= 0.0;
        if (_deferredDeactivations > 0)
        {
            _pendingDeactivations.Add((instance, expired));
            return;
        }
        RunDeactivation(instance, expired);
    }

    private void RunDeactivation(BbBuffInstance instance, bool expired)
    {
        if (instance.Stacks.Count == 0)
            _router.RunBuffHook(instance, "OnBuffDeactivate", _host.Spells.World.Get(instance.CasterNetId),
                prepare: scope => scope.Vars["Expired"] = expired);
        foreach (var stack in instance.Stacks.ToArray())
            RunStackHook(instance, stack, "OnBuffDeactivate", scope => scope.Vars["Expired"] = expired);
        foreach (var id in instance.SupersededEffects)
            if (_host.KilledEffects.Add(id)) _host.Spells.Effects.KillEffect(id);
        // Batch 726: what the buff's tables still own outlived it (BbSpellHost.RetireOwnedEffects).
        _host.RetireOwnedEffects(instance.InstanceVars);
        foreach (var stack in instance.Stacks) _host.RetireOwnedEffects(stack.Vars);
    }

    /// <summary>
    /// Runs <paramref name="dispatch"/> (a missile-end dispatch), then the <c>OnBuffDeactivate</c> hooks of the buffs it removed. Talon's Rake: the
    /// third blade's <c>BuffOnMissileEnd</c> takes <c>TalonRakeMissileOne</c> off Talon before it puts <c>TalonRakeMarker</c> on its placemarker, and
    /// that buff's deactivation is the return loop, which then passed the third placemarker over. Reconstruction: retail's order is taken as the removed
    /// buff's deactivation running after the dispatch that removed it (the shipped order needs the later marker to be in place when the loop runs).
    /// </summary>
    internal void DeferDeactivations(Action dispatch)
    {
        _deferredDeactivations++;
        try
        {
            dispatch();
        }
        finally
        {
            if (--_deferredDeactivations == 0)
                while (_pendingDeactivations.Count > 0)
                {
                    var (instance, expired) = _pendingDeactivations[0];
                    _pendingDeactivations.RemoveAt(0);
                    RunDeactivation(instance, expired);
                }
        }
    }

    /// <summary>
    /// <c>BuffOnPreDamage</c> for the damage the unit is about to take; returns the (possibly script-modified)
    /// <c>DamageAmount</c>. Runs for every script buff of the unit that has the hook.
    /// </summary>
    public double OnPreDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind,
        string hook = "BuffOnPreDamage")
    {
        // The order the scripts ask for in their globals. The client's buff loader reads OnPreDamagePriority as a number into
        // the buff object (its priority field). OnPreDamagePriority (28 scripts: Black Shield and Hexdrinker 2 (BlackShield.luaobj instr 0019), most shields 3,
        // Chrono Shift 4, Will Revive 6, Rebirth 8, Children of the Grave 10) sorts the death-preventing effects after the shields, so a lower
        // number runs first (no global: 0). DoOnPreDamageInExpirationOrder (12 shields, true in BlackShield and Hexdrinker) runs equal priorities by
        // the time they have left, the soonest first, so that the shield about to expire absorbs first. Reconstruction: the expiration-order flag is
        // only in the shipped scripts; the client does not read it.
        // Batch 583 (Kassadin's Void Stone): equal priorities run in the unit's buff order (WithHook; the sorts are stable), the older buff first.
        // The instance table reuses a removed buff's entry, so walking it ran a newer buff first whenever an older one had been removed:
        // VoidStoneAttackSpeedBoost then read the hit before VoidStone's MagicAbsorb (0.85) and grew by 1 / 0.85 too much.
        // Reconstruction: the walk is a snapshot taken before the first hook, so a buff a hook adds (Void Stone's boost on the first magic
        // hit) does not see the hit that added it; whether retail's walk reached it is not in the client (its BuffOnPreDamage dispatch,
        // is an empty stub there). The scripts lean that way: ManaBarrierIcon
        // hands the triggering hit to the shield it adds by hand (amountToSubtract, batch 119), which a walk that reached the new buff would not need.
        foreach (var instance in WithHook(target, hook)
                     .OrderBy(item => item.Script.Global("OnPreDamagePriority") is BbValue.Number priority ? priority.Value : 0.0)
                     .ThenBy(item => item.Script.Global("DoOnPreDamageInExpirationOrder") is BbValue.Bool { Value: true } ? item.State.ExpiresAt : 0.0)
                     .ToArray())
        {
            if (!target.Buffs.Contains(instance.State)) continue;
            amount = _router.RunPreDamage(instance, source, amount, type, kind, hook);
        }
        return amount;
    }

    /// <summary>
    /// <c>BuffOnTakeDamage</c> of the unit's script buffs after a hit reached its health: Owner and Target are the holder, Attacker the
    /// damage source, <c>DamageAmount</c> the damage taken, <c>GetSourceType</c> its category (Sleep ends on anything but DAMAGESOURCE_PERIODIC).
    /// </summary>
    public void OnTakeDamage(Entity unit, Entity? source, double amount, DamageSourceKind? kind, bool periodic)
    {
        foreach (var instance in WithHook(unit, "BuffOnTakeDamage"))
            _router.RunBuffHook(instance, "BuffOnTakeDamage", source, kind, inDamage: true,
                prepare: scope => scope.Vars["DamageAmount"] = amount, periodicDamage: periodic);
    }

    /// <summary>
    /// Script buffs of a unit that have <paramref name="hook"/>, a snapshot safe against the hooks changing the unit's buffs, in the order of the
    /// unit's buff list (the older buff first, as the stock buff manager walks it): YorickSpectral's hit reads the <c>DamageAmount</c> the passive
    /// <c>YorickUnholySymbiosis</c> left, and the instance table reuses the slot of a removed buff, so its own order is not the buffs'.
    /// </summary>
    public BbBuffInstance[] WithHook(Entity unit, string hook)
    {
        var found = _instances.Values.Where(item => ReferenceEquals(item.Owner, unit) && item.Script.Event(hook) is not null &&
                                                    unit.Buffs.Contains(item.State)).ToArray();
        if (found.Length > 1) Array.Sort(found, (first, second) => unit.Buffs.IndexOf(first.State).CompareTo(unit.Buffs.IndexOf(second.State)));
        return found;
    }

    /// <summary>Every script buff the unit holds now (a snapshot, in the order of the unit's buff list).</summary>
    public BbBuffInstance[] OfUnit(Entity unit)
    {
        var found = _instances.Values.Where(item => ReferenceEquals(item.Owner, unit) && unit.Buffs.Contains(item.State)).ToArray();
        if (found.Length > 1) Array.Sort(found, (first, second) => unit.Buffs.IndexOf(first.State).CompareTo(unit.Buffs.IndexOf(second.State)));
        return found;
    }

    /// <summary>
    /// <c>BuffOnDeath</c> for every script buff on the dying unit. The removal of the ones that do not persist through death
    /// (their <c>OnBuffDeactivate</c> runs from it) is <see cref="RemoveNonPersistent(Entity)"/>, called once the kill credit has run.
    /// Attacker is the unit that dealt the killing damage.
    /// </summary>
    public void OnUnitDeath(Entity unit, uint killerNetId)
    {
        // Batch 398 (Reconstruction): the unbound placemarker effects a script never removes end with their cube (BbNativeHandlers.CreateNow registers
        // them; the client's bound emitter ends with its unit). Batch 404 (Reconstruction): they end with the cube's object,
        // which the client keeps for its corpse (the 0xA6 NpcDie duration, CombatService.CorpseReapSeconds, after which the server removes the unit), not
        // with the 0xA6 itself. Leona's Solar Flare creates its ground effect in the step the cube dies (the landing buff's OnBuffDeactivate, then 1000 true
        // damage to the cube), so a kill at the death meant it never played. KilledEffects keeps a script's own remove from a second kill.
        if (_host.PlacemarkerEffects.Remove(unit.NetId, out var placed))
            _host.Spells.RunAt(_host.Spells.CurrentTime + Amberline.Simulation.Combat.CombatService.CorpseReapSeconds, () =>
            {
                foreach (var id in placed)
                    if (_host.KilledEffects.Add(id)) _host.Spells.Effects.KillEffect(id);
            });
        var instances = _instances.Values.Where(item => ReferenceEquals(item.Owner, unit)).ToArray();
        var killer = _host.Spells.World.Get(killerNetId);
        foreach (var instance in instances)
        {
            var alreadyRan = instance.DeathHookRan;
            instance.DeathHookRan = false;
            if (!alreadyRan && unit.Buffs.Contains(instance.State) && instance.Script.Event("BuffOnDeath") is not null)
                _router.RunBuffHook(instance, "BuffOnDeath", killer);
        }
        // The non-persistent buffs go later, after the kill credit (OnUnitDeathReleased, from CombatService.Kill): the credit reads them.
    }

    /// <summary>
    /// Whether a buff survives a death: its script says <c>PersistsThroughDeath</c>, or its spell data carries <c>SPELL_PersistThroughDeath</c>
    /// (<c>SpellData*Flags</c> bit 8). The client has one persistence predicate, and it is that OR:
    /// the script override byte (read by the buff loader) or the data record's bit 8. Batch 203 follow-up: only the script was read.
    /// </summary>
    internal bool PersistsThroughDeath(BbScript script) =>
        script.Global("PersistsThroughDeath") is BbValue.Bool { Value: true } || _host.Spells.Data.SpellPersistsThroughDeath(script.Name);

    /// <summary>
    /// The server's non-persistent buff removal for the script buffs of a unit (a death, <c>BBForceDead</c>): every buff
    /// whose script does not say <c>PersistsThroughDeath</c> is removed and its <c>OnBuffDeactivate</c> runs.
    /// </summary>
    public void RemoveNonPersistent(Entity unit) =>
        RemoveNonPersistent(unit, _instances.Values.Where(item => ReferenceEquals(item.Owner, unit)).ToArray());

    private void RemoveNonPersistent(Entity unit, BbBuffInstance[] instances)
    {
        _retiring = unit;
        try
        {
            foreach (var instance in instances)
                if (!PersistsThroughDeath(instance.Script) && unit.Buffs.Contains(instance.State) && instance.State.Slot >= 0)
                    _host.Spells.Buffs.Remove(unit, instance.State.Slot);
        }
        finally { _retiring = null; }
        // The statuses a script set on the unit with no buff to hold them end with it (Reconstruction, see SetStatusOn).
        if (_scriptStatuses.TryGetValue(unit, out var held) && held.Count > 0)
        {
            var before = BuffLifecycle.ControlOf(unit);
            held.Clear();
            RecomposeScriptStatus(unit, held);
            _host.Spells.Buffs.ScriptStatusLost(unit, before.CouldMove, before.CouldAttack);
        }
    }
}
