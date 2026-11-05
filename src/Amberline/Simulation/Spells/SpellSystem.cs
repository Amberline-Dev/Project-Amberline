using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Spells;

/// <summary>
/// Per-match shared cast validation, rank gates, cooldowns, pending casts and
/// champion dispatch. Champion effects live in separate partials/files.
/// </summary>
public sealed partial class SpellSystem
{
    public static double ResourceCost(Entity hero, double cost) => hero.UnlimitedResource ? 0.0 : cost;

    /// <summary>
    /// What a champion slot pays: the spell's cost for the rank plus the increment a script set on the slot (<c>BBSetPARCostInc</c>), never below
    /// zero. The same sum is the cost the stat block shows (<c>HeroStatsService.Snapshot</c>) and the gate checks.
    /// </summary>
    public static double SlotResourceCost(Entity hero, int slot, double cost)
    {
        if (hero.SpellParCostIncrements.TryGetValue(slot, out var increment)) cost = Math.Max(0.0, cost + increment);
        // BBSetPARMultiplicativeCostInc (Cassiopeia's Deadly Cadence, Xerath's Arcane Barrage recasts): Reconstruction, cost * (1 + the slot's multiplier).
        if (hero.SpellParCostMultipliers.TryGetValue(slot, out var multiplier)) cost = Math.Max(0.0, cost) * Math.Max(0.0, 1.0 + multiplier);
        return ResourceCost(hero, cost);
    }

    public const int ChampionSlotCount = 4;
    // The stock spellbook init seeds ExtraSpell1..8 at 42..49; the slot map
    // Lua ExtraSlots (2) to 42, while InventorySlots (1) starts at 4.
    public const byte ExtraSpellSlotStart = 42;
    public static readonly string[] SummonerSlots = ["SummonerFlash", "SummonerDot"];

    /// <summary>
    /// The shipped summoner scripts (batch 142 step 2). Null keeps the hand-written summoner path, which the checks use
    /// as the differential oracle while the conversion lands; a bound module runs <c>Summoner*.luaobj</c> instead.
    /// </summary>
    internal Bb.BbSummonerModule? SummonerScripts { get; set; }

    /// <summary>
    /// The shipped item scripts (batch 144 step 1). Null keeps the hand-written item path, which the checks use as the
    /// differential oracle while the groups are converted one at a time; a bound module runs <c>Items/&lt;id&gt;.luaobj</c>
    /// for the converted ids instead.
    /// </summary>
    internal Bb.BbItemModule? ItemScripts { get; set; }

    /// <summary>
    /// The shipped non-champion unit character scripts (batch 159 step 2). Null keeps the hand-written unit rules; a bound
    /// module runs <c>Characters/&lt;unit&gt;/Scripts/CharScript&lt;unit&gt;.luaobj</c> for the units it was given (the
    /// turrets' own buffs, stats and hit hooks), and answers false for a character with no shipped script.
    /// </summary>
    internal Bb.BbUnitCharacterModule? UnitScripts { get; set; }

    /// <summary>The mastery/loadout hooks, so the summoner bindings can reach the hero's <c>BbLoadoutOwner</c>.</summary>
    internal Bb.BbLoadoutHooks? LoadoutHooks { get; set; }

    /// <summary>
    /// Batch 518: a script buff landed on its target (target, caster, name), after the add and before its OnBuffActivate. Map 8's personal
    /// score reads the shipped score markers from it (DominionPlayerScore.OnScriptBuff); null elsewhere.
    /// </summary>
    public Action<Entity, Entity, string>? ScriptBuffAdded { get; set; }

    // Recall's windup, channel length and buff time are the shipped Recall.luaobj's (ChannelDuration 8.0, BBSpellBuffAdd 7.9).
    public const int RecallSlot = 10;
    // Reconstruction: retail walks the caster into range and casts on arrival, but the rule that does it (approach point, re-plan
    // cadence, when it gives up) is server-only native code; no capture or binary here contains it and Hero.lua has no approach
    // rule. The 4 s give-up time and the approach details are this emulator's.
    public const double PendingCastTimeoutSeconds = 4.0;
    public const double FlashRange = 475.0;
    public const double IgniteRange = 600.0;
    public const double ClientTeleportDeadband = 50.0;

    private readonly WorldState _world;
    private readonly MovementSystem _movement;
    private readonly GameData _data;
    private readonly ISpellCombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly ISpellBuffService _buffs;
    private readonly IChampionSpellHooks _championHooks;
    private readonly IChampionSpellDispatcher _championDispatcher;
    private readonly IRandomSource _random;
    private readonly Func<Entity, Entity, bool>? _targetVisible;
    private readonly HeroStatsService? _heroStats;
    private HeroAttackOrderSystem? _heroOrders;
    private double _time;
    // Batch 404: actions due at a later match time (RunAt), run in the order they were queued once the step's clock reaches them.
    private readonly List<(double At, Action Run)> _timedActions = new();
    // Batch 246: each holder's self-buff poll line (Bb.BbSelfUpdate) and whether this step is on it.
    private readonly Dictionary<Entity, double> _selfUpdateNextAt = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Entity> _selfUpdateDue = new(ReferenceEqualityComparer.Instance);

    public SpellSystem(
        WorldState world,
        MovementSystem movement,
        ISpellCombatService combat,
        ISpellEffectSink effects,
        ISpellBuffService buffs,
        IChampionSpellHooks championHooks,
        IChampionSpellDispatcher championDispatcher,
        GameData? data = null,
        IRandomSource? random = null,
        Func<Entity, Entity, bool>? targetVisible = null,
        HeroStatsService? heroStats = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        if (!ReferenceEquals(_movement.World, _world))
            throw new ArgumentException("MovementSystem must belong to the spell world", nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _championHooks = championHooks ?? throw new ArgumentNullException(nameof(championHooks));
        _championDispatcher = championDispatcher ?? throw new ArgumentNullException(nameof(championDispatcher));
        _data = data ?? GameData.Default;
        _random = random ?? new SystemRandomSource();
        _targetVisible = targetVisible;
        _heroStats = heroStats;
    }

    public WorldState World => _world;
    public GameData Data => _data;
    /// <summary>Injected movement and spell boundaries for champion dispatchers.</summary>
    public MovementSystem Movement => _movement;
    public ISpellCombatService Combat => _combat;
    public ISpellEffectSink Effects => _effects;
    public ISpellBuffService Buffs => _buffs;
    /// <summary>Uses the owning match's team sight; standalone services retain their visibility flag.</summary>
    public bool CanSeeTarget(Entity viewer, Entity target) =>
        _targetVisible?.Invoke(viewer, target) ?? (viewer.Team == target.Team || target.Visible);
    public double CurrentTime => _time;

    /// <summary>
    /// Runs <paramref name="action"/> on the first step whose clock is at or past <paramref name="at"/> (batch 404: an effect that ends with its
    /// cube's corpse is killed then, BbBuffRuntime.OnUnitDeath).
    /// </summary>
    public void RunAt(double at, Action action) => _timedActions.Add((at, action));

    /// <summary>
    /// Reconstruction (batch 419, batch 418 evidence): an effect the wire sent unbound because its unit was dead (ReplicationDelivery.OnWire) is killed
    /// at that unit's corpse end, CombatService.CorpseReapSeconds after the death, as a placemarker's effect is (BbBuffRuntime.OnUnitDeath).
    /// </summary>
    public void EndEffectWithUnitCorpse(uint effectNetId, uint unitNetId)
    {
        // A corpse the world has already removed ended its corpse time: the effect ends on the next step.
        var at = _world.Get(unitNetId) is { } unit ? (unit.DeathTime ?? _time) + CombatService.CorpseReapSeconds : _time;
        RunAt(at, () => _effects.KillEffect(effectNetId));
    }

    /// <summary>Whether this step is the unit's self-buff poll line (<see cref="Bb.BbSelfUpdate"/>), as the hero loop set it.</summary>
    public bool SelfUpdateDue(Entity unit) => _selfUpdateDue.Contains(unit);

    // The line advances by whole intervals from the last one, so it keeps the first poll's phase; a passed line is skipped, never
    // repaid (the BuffOnUpdateActions rule, BbBuffRuntime.ActionsUpdateInterval). The holder's first poll runs at once.
    // Batch 256: a bound non-champion unit (Bb.BbUnitCharacterModule) steps its own line here too, and forgets it when unbound.
    internal bool StepSelfUpdate(Entity unit)
    {
        if (!_selfUpdateNextAt.TryGetValue(unit, out var next)) next = _time;
        var due = _time + 1e-9 >= next;
        if (due)
        {
            var missed = Math.Floor((_time - next) / Bb.BbSelfUpdate.Interval + 1e-9);
            _selfUpdateNextAt[unit] = next + Math.Max(1.0, missed + 1.0) * Bb.BbSelfUpdate.Interval;
            _selfUpdateDue.Add(unit);
        }
        else _selfUpdateDue.Remove(unit);
        return due;
    }

    internal void ForgetSelfUpdate(Entity unit)
    {
        _selfUpdateNextAt.Remove(unit);
        _selfUpdateDue.Remove(unit);
    }

    /// <summary>The match's hero stat service (null in the stand-alone check systems); the script engine refreshes through it.</summary>
    internal HeroStatsService? HeroStats => _heroStats;
    public IRandomSource Random => _random;
    public HeroAttackOrderSystem? HeroOrders => _heroOrders;

    /// <summary>
    /// Binds the owning match's existing hero-order service. Champion modules
    /// use this only to replan a postponed order from a displacement landing.
    /// </summary>
    public void AttachHeroOrders(HeroAttackOrderSystem heroOrders)
    {
        ArgumentNullException.ThrowIfNull(heroOrders);
        if (_heroOrders is not null && !ReferenceEquals(_heroOrders, heroOrders))
            throw new InvalidOperationException("Hero-order service is already attached.");
        _heroOrders = heroOrders;
    }

    public static SpellCastRequest? ParseCastRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 34) return null;
        try
        {
            var reader = new PacketBuffer(payload);
            _ = reader.ReadBytes(5);
            var bitfield = reader.ReadByte();
            var position = new Position3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var endPosition = new Position3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var target = reader.ReadUInt32();
            if (!Finite(position) || !Finite(endPosition)) return null;
            return new SpellCastRequest(bitfield >> 1, (bitfield & 1) != 0,
                position, endPosition, target);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public SpellRankValidation CanRankUp(Entity hero, int slot)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if ((uint)slot >= ChampionSlotCount)
            return new(false, $"slot {slot} out of range");
        if (hero.SkillPoints <= 0) return new(false, "no skill points available");
        EnsureSpellLists(hero);
        var current = hero.SpellRanks[slot];
        var handled = _championDispatcher.Handles(hero);
        if (!handled)
            return new(false, $"no spell dispatcher for champion {hero.ChampionName}");
        var max = _championDispatcher.MaxRank(hero, slot);
        var spellName = _championDispatcher.SpellName(hero, slot);
        if (current >= max)
            return new(false, $"{spellName} already at max rank {current}");
        var required = _championDispatcher.RankGate(hero, slot, current + 1);
        return hero.Level < required
            ? new(false, $"{spellName} rank {current + 1} needs champion level {required}, hero is {hero.Level}", required)
            : new(true, string.Empty, required);
    }

    public bool RankUp(Entity hero, int slot)
    {
        var result = CanRankUp(hero, slot);
        if (!result.Allowed) return false;
        hero.SpellRanks[slot]++;
        hero.SkillPoints--;
        _championDispatcher.OnRankUp(this, hero, slot, hero.SpellRanks[slot]);
        if (_effects.IsReferenceReady(hero))
            _effects.SendUpgrade(hero, slot, hero.SpellRanks[slot], hero.SkillPoints);
        return true;
    }

    /// <summary>
    /// The client's can-use check, the call the shipped bot scripts
    /// make as <c>CanUseSpell(slot) == 0</c>: no spell or rank 0 (1/2), cooldown (4), the cast-bit
    /// mask (3), then the owner's can-use callback (dead, hard control, silence,
    /// mana: 3/5). Unlike <see cref="CanCast"/> there is NO range and NO target test, so a
    /// <see cref="SpellCastFailure.Range"/> or <see cref="SpellCastFailure.Target"/> verdict of the
    /// champion module counts as usable. Every module checks rank, cooldown and mana before it
    /// looks at the target, so those state gates are never masked by a geometry failure.
    /// </summary>
    public SpellValidation CanUseSpell(Entity hero, int slot, double now)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (CasterGate(hero, slot) is { } blocked) return blocked;
        var probe = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        var verdict = _championDispatcher.Validate(this, hero, slot, 0u, probe, now);
        return verdict.Failure is SpellCastFailure.Range or SpellCastFailure.Target
            ? SpellValidation.Success : verdict;
    }

    /// <summary>The caster-state gates shared by <see cref="CanCast"/> and <see cref="CanUseSpell"/>.</summary>
    private SpellValidation? CasterGate(Entity hero, int slot)
    {
        if (!hero.Alive)
            return Failure("caster is dead", SpellCastFailure.Dead);
        // Batch 314: no control test of the server's own here. The owner's can-use callback (of
        // HeroWars_clientServer, the code the client and the server share) is the whole control rule, SpellOwnerGate below: a
        // CanCastWhileDisabled spell passes a stun, a fear and a stasis alike (the shipped Stun and Fear buffs disable actions through
        // their payload, which the old gate here refused even for Remove Scurvy).
        if ((uint)slot >= ChampionSlotCount)
            return Failure($"slot {slot} out of range", SpellCastFailure.Slot);
        EnsureSpellLists(hero);

        if (!_championDispatcher.Handles(hero))
            return Failure($"no spell dispatcher for champion {hero.ChampionName}",
                SpellCastFailure.Disabled);
        // The can-use check tests the slot's cast bit before it asks the owner (USE_Disabled); a slot a script sealed
        // with BBSealSpellSlot has its bit cleared (SpellSlotSeals), so this is the one place a sealed champion slot is refused.
        if (hero.SpellSeals.IsSealed(false, slot))
            return Failure("spell slot is sealed (BBSealSpellSlot)", SpellCastFailure.Disabled);
        // the owner's can-use callback with the spell's own flags: silence, fear, sleep,
        // taunt, suppression and the other cannot-cast states, and CantCastWhileRooted (Deceive, EzrealArcaneShift, BlindMonkQTwo,
        // BlindMonkWOne and Pounce) for a unit that cannot move. A cast requested during a dash whose clip is locked is queued until
        // the landing, so that forced move does not count as a root here (owner decision B3 #10, HandleCastRequest); during an unlocked
        // dash the cast goes at once (owner, 3 November 2026), so the dash's CanMove-off counts, as it does for Flash and as the client
        // greys the slot.
        SpellData? spellData = null;
        try { spellData = CastSpellData(hero, slot); }
        catch (InibinException) { /* no spell record: every flag takes the client default (false) */ }
        if (!SpellOwnerGate.CanUse(hero, spellData, forcedMoveClearsCanMove: !hero.AnimationLocked))
            return Failure("caster cannot cast in its control state", SpellCastFailure.Disabled);
        return null;
    }

    /// <summary>The champion script's own CanCast gate for a slot (finding 32): whether the HUD keeps the slot's castable bit.</summary>
    public bool ScriptCastGateOpen(Entity hero, int slot) => _championDispatcher.ScriptCastGateOpen(hero, slot);

    public SpellValidation CanCast(Entity hero, int slot, uint targetNetId,
        Position3 position, double now, Position3? end = null)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (!double.IsFinite(now) || !Finite(position))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (CasterGate(hero, slot) is { } blocked) return blocked;
        var resolved = _world.Get(targetNetId);
        if (resolved is not null && resolved.Alive &&
            !CastTargetAvailable(hero, resolved, _championDispatcher.SpellName(hero, slot), SlotTargetsUnit(hero, slot)))
            return Failure("target is not targetable", SpellCastFailure.Target);
        return _championDispatcher.Validate(this, hero, slot, targetNetId, position, now, end);
    }

    public bool HandleCastRequest(Entity hero, ReadOnlySpan<byte> payload, double now)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var request = ParseCastRequest(payload);
        if (request is null) return false;
        return HandleCastRequest(hero, request.Value, now);
    }

    /// <summary>
    /// Handles an already-decoded cast order through the same authoritative
    /// validation and champion dispatch path as the native wire request.
    /// Server-owned actors use this boundary instead of manufacturing a
    /// client packet.
    /// </summary>
    public bool HandleCastRequest(Entity hero, SpellCastRequest request, double now)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        // ParseCastRequest applies this check to both positions. Keep the
        // typed server boundary equally strict before it clears any existing
        // pending order or champion channel.
        if (!Finite(request.Position) || !Finite(request.EndPosition)) return false;
        var queued = hero.PendingCast;
        hero.PendingCast = null;
        var req = request;
        // Owner decision, 3 November 2026 ("Cast at once", batch 663; replaces playtest B3 #10's "every cast requested mid-dash queues"):
        // the original game casts a spell pressed during a dash at once (Riven's Blade of the Exile and Ki Burst during Valor: the shipped
        // Broken Wings seals only Valor for its own dash, a block a retail queue would not need). Only a dash whose clip is locked holds it:
        // its script's BBPlayAnimation Lock=true (Lee Sin's Q2 and W, Tumble, Pounce, Bladesurge, Broken Wings' strikes ...), whose lock the
        // stock client keeps until the landing's 0x2B unlock and while which it refuses every other play, so the cast's gesture would be
        // lost (B3 #10: Lee Sin's Q2 -> R showed no kick). Such a cast queues without an approach, in press order (batch 659), and Tick
        // fires it on the first spell tick after the landing. Valor plays Spell3 with Lock=false, Rocket Jump, Cataclysm and the net's flip
        // play no clip. What a script blocks still blocks: the spell's own flags, SetCanCast, a seal, a silence (CanCast refuses at once).
        // Foreign displacements carry a Stun that already rejects spells; their summoners (Cleanse, Ignite) keep the immediate path.
        var held = hero.AnimationLocked;
        var dashing = hero.Alive && hero.ForcedMove is { Completed: false } move &&
            move.SourceNetId == hero.NetId && held;
        // A script dash records the unit that ADDED its buff as the move's source (BbScriptMoves.Origin: Rocket Grab's pull is Blitzcrank's), and
        // Lee Sin's Q2 buff is added with the marked enemy as its attacker, so his own dash is not "his own" by that id. For a champion spell the
        // source does not matter: a foreign displacement stuns, and CanCast refuses the stunned hero before this flag is read.
        // A fear wander is a walk, not a dash with a locked clip (the client plays the run, as every other reader of a "Fear" move treats it),
        // so a cast that passes OnCanUseSpell while feared (CanCastWhileDisabled: Remove Scurvy, batch 314) is not queued behind it.
        var moving = hero.Alive && hero.ForcedMove is { Completed: false, Effect: not "Fear" } && held;
        if (req.IsSummoner)
        {
            // A summoner slot a script sealed (Zhonya's stasis, Yorick's ghouls, Gate, Xerath's Locus, Skarner's Impale ...) is disabled in the
            // second spellbook's cast bits (SpellSlotSeals); the refusal comes before any queueing or approach.
            if (hero.SpellSeals.IsSealed(true, req.Slot)) return false;
            // Summoners share the queue behind a locked dash; HandleSummonerCast validates at landing. During an unlocked dash
            // they cast at once. Flash and Revive fall through and are refused now (retail greys them).
            if (dashing && (uint)req.Slot < SummonerSlots.Length && !SummonerRefusesDash(hero, req.Slot))
            {
                OnSpellRequestAccepted(hero);
                hero.PendingCast = QueueBehindDash(queued, new PendingCastState(req, now + PendingCastTimeoutSeconds));
                return true;
            }
            // Reconstruction: a unit-targeted summoner (Ignite, Exhaust, Smite) requested out of range walks into range like any
            // other unit-targeted cast instead of being refused; the stock server's approach is native code.
            if (SummonerOutOfRangeTarget(hero, req) is { } farTarget)
            {
                OnSpellRequestAccepted(hero);
                hero.PendingCast = new PendingCastState(req, now + PendingCastTimeoutSeconds)
                    { LastMovePoint = farTarget.Position, NextMoveRetryAt = now + HeroAttackOrderSystem.ScanSeconds };
                ApproachCastPoint(hero, req, farTarget.Position);
                return true;
            }
            if (!HandleSummonerCast(hero, req, now)) return false;
            // A refused cast leaves a running recall alone; the accepted one ends it (the cast replaces the client's channel).
            OnSpellRequestAccepted(hero);
            hero.MatchStatistics.RecordCast(req.Slot, summoner: true);
            return true;
        }
        if (req.Slot == RecallSlot) return HandleRecall(hero, req, now);
        if ((uint)req.Slot >= ChampionSlotCount) return false;
        var validation = CanCast(hero, req.Slot, req.TargetNetId, req.Position, now, req.EndPosition);
        if (!validation.Allowed || moving)
        {
            if (!validation.Allowed && validation.Failure != SpellCastFailure.Range) return false;
            var point = PendingCastPoint(hero, req);
            if (point is null) return false;
            OnSpellRequestAccepted(hero);
            StopCastGesture(hero);
            var pending = new PendingCastState(req, now + PendingCastTimeoutSeconds)
                { LastMovePoint = point, NextMoveRetryAt = now + HeroAttackOrderSystem.ScanSeconds };
            hero.PendingCast = moving ? QueueBehindDash(queued, pending) : pending;
            ApproachCastPoint(hero, req, point.Value);
            return true;
        }
        OnSpellRequestAccepted(hero);
        StopCastGesture(hero);
        ExecuteCast(hero, req, now);
        return true;
    }

    /// <summary>
    /// Whether the client would run this cast through a SpellCaster, the path that waits for the cast frame: a spell that is not an
    /// instacast (<c>SpellData*Flags</c> bit 4; the instant-cast routine casts an instacast at once and
    /// creates no SpellCaster). Every non-instant slot spell, Recall and a non-instant summoner spell (none ships: all fifteen summoner
    /// inibins carry the instacast bit) do; item actives never do. An instacast slot spell whose script casts a normal child spell
    /// does too, through the child (Ezreal Q and W, Blitzcrank Q, Yorick W: the parent is an instacast, the child's cast packet is
    /// not). This is what the dispatcher holds while the hero's client winds up another spell (<see cref="SpellWindup"/>).
    /// </summary>
    internal bool CreatesSpellCaster(Entity hero, SpellCastRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        try
        {
            if (request.IsSummoner) return SummonerSpellData(hero, request.Slot) is { IsInstant: false };
            if (request.Slot == RecallSlot) return !_data.Spell(_data.RecallSpellName).IsInstant;
            if ((uint)request.Slot >= ChampionSlotCount || !_championDispatcher.Handles(hero)) return false;
            if (!CastSpellData(hero, request.Slot).IsInstant) return true;
            return _championDispatcher.FindModule(hero.ChampionName)?.CasterChildSpell(hero, request.Slot) is { } child &&
                !_data.Spell(child).IsInstant;
        }
        catch (InibinException)
        {
            // No spell record: the cast is refused whenever it is handled, so there is nothing to hold.
            return false;
        }
    }

    /// <summary>
    /// A spell request that passed validation (or was queued to walk into range) ends a running script channel: Janna's Monsoon, and
    /// Recall, which is the summoner module's channel (a cast that happens stops a channel, ChannelingStopSource_Casting, unless the
    /// shipped spell carries CantCancelWhileChanneling). A refused one leaves it running. Recall's own request (slot 10) never reaches this.
    /// </summary>
    private void OnSpellRequestAccepted(Entity hero) => _championHooks.CancelChannel(hero, "spell request");

    /// <summary>
    /// <c>BBStopChanneling</c>: the unit stops whatever it is channelling. The channel the unit runs ends through the hooks
    /// (<see cref="IChampionSpellHooks.CancelChannel"/>): Recall's and Janna's Monsoon's channels are the script channels the sinks own,
    /// the same call Zhonya's Hourglass makes. Only heroes channel here.
    /// </summary>
    public void StopChanneling(Entity unit, string reason)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType != UnitType.Hero) return;
        _championHooks.CancelChannel(unit, reason);
    }

    /// <summary>Applies the source order interruption rules to a fresh player order.</summary>
    /// <summary>
    /// Batch 449: starts the shipped OdinCaptureChannel on a champion with a capture point guardian as its Target (the BuildingBlocks channel
    /// engine, <see cref="BbScriptChannels.Begin"/>). False when the module or the script is missing.
    /// </summary>
    public bool BeginCaptureChannel(Entity hero, Entity guardian) =>
        BeginUseChannel(hero, guardian, Bb.BbSummonerModule.CaptureChannelSpellName);

    /// <summary>
    /// Batch 470: starts the shipped use channel <paramref name="spellName"/> (a useable unit's <c>HeroUseSpell</c>) on a champion aimed at that unit.
    /// Batch 687 (community report, 4 November 2026: a stealthed Evelynn's capture moved nothing): the use is a cast of the slot-0x33 spell, so it
    /// crosses the cast hooks every other cast does (Recall's <c>OnNamedSpellCast</c>). OdinCaptureChannel and OdinCaptureChannelBomb set
    /// <c>CastingBreaksStealth</c> true, and the stealth buffs end themselves on such a cast (the BuffOnSpellCast of ShadowWalk, Twitch's HideInShadows,
    /// Deceive, VayneTumbleFade, AkaliSBStealth, CamouflageStealth); the point's tree counts no stealthed champion ("Doesn't count if stealthed", OdinScriptBehaviorTrees.xml:148).
    /// </summary>
    public bool BeginUseChannel(Entity hero, Entity used, string spellName)
    {
        if (SummonerScripts?.BeginUseChannel(hero, used, spellName) != true) return false;
        _championHooks.OnNamedSpellCast(hero, Bb.BbSummonerModule.UseChannelSlot, spellName, used);
        return true;
    }

    /// <summary>Batch 494: whether <paramref name="hero"/> channels the use spell <paramref name="spellName"/> at <paramref name="used"/> now.</summary>
    public bool ChannelsUseAt(Entity hero, Entity used, string spellName) =>
        SummonerScripts?.ChannelsUseAt(hero, used, spellName) == true;

    /// <summary>Batch 494: whether <paramref name="hero"/>'s use slot still cools down after its last use channel stopped.</summary>
    public bool UseSlotCoolingDown(Entity hero) => SummonerScripts?.UseSlotCoolingDown(hero) == true;

    /// <summary>Batch 452: the guardian <paramref name="hero"/> channels OdinCaptureChannel on (its Target), or null.</summary>
    public Entity? CaptureChannelTarget(Entity hero) => SummonerScripts?.CaptureChannelTarget(hero);

    /// <summary>Batch 517: the same guardian, but only once the capture channel has charged (its 1.5 s wind-up is over), or null.</summary>
    public Entity? ChargedCaptureTarget(Entity hero) => SummonerScripts?.ChargedCaptureTarget(hero);

    /// <summary>Batch 480b: the hero whose capture channel is aimed at <paramref name="guardian"/> on <paramref name="team"/>'s side, or null.</summary>
    public Entity? CapturerOf(Entity guardian, Team team) => SummonerScripts?.CapturerOf(guardian, team);

    public void CancelOrder(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        // Any player order lifts the post-cast idle-attack hold (playtest B2 AE6).
        hero.AutoAttackResumeAt = 0.0;
        StopCastGesture(hero);
        if (hero.PendingCast is not null) hero.PendingCast = null;
        // Every fresh order is the stop source Move/Attack of a running script channel (Janna's Monsoon).
        _championHooks.CancelChannel(hero, "order");
    }

    // The cast clip of a windup child cast (Ezreal Q/W, Sivir Q, Blitzcrank Q) outlasts its 0.25 s
    // uncancellable windup. The stock client refuses Stop during that windup
    // (the cancel-blocked check), so only a later order cuts the clip;
    // that keeps the hero from sliding in it (playtest #7/#22).
    private void StopCastGesture(Entity hero)
    {
        if (!hero.CastClipActive || SpellWindup.IsWindingUp(hero)) return;
        hero.CastClipActive = false;
        if (_effects.IsReferenceReady(hero)) _effects.StopAnimation(hero);
    }

    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
        _time += dt;
        // Missiles and delayed impacts share one insertion-ordered phase. This
        // keeps Annie, Shaco, and pet projectiles in the same source order.
        TickSharedProjectiles(dt);
        _championDispatcher.TickSharedProjectilePhase(this, dt);
        // A pending Infernal Guardian may register Tibbers while the hero
        // loop is running. Snapshot the ordered collection before ticking so
        // registration cannot invalidate its enumerator.
        foreach (var hero in _world.Entities.Values
                     .Where(entity => entity.UnitType == UnitType.Hero).ToArray())
        {
            // Playtest B2 NL2: a displacement owns movement (retail
            // BlindMonkQTwoDash cancels orders), so a queued cast waits for
            // the landing. Snapshot before the champion tick lands the dash;
            // the cast then fires on the first spell tick after landing.
            var displaced = hero.ForcedMove is not null;
            // Batch 246: the self-buff polls below (items, talents, the character script) run on the hero's 0.25 s line.
            var selfUpdate = StepSelfUpdate(hero);
            // First, so a cast that starts later in this step keeps its whole window for the next one, as the modules' own pending
            // states do.
            SpellWindup.Tick(hero, dt);
            if (_championDispatcher.Handles(hero) &&
                _championDispatcher.TickPhase(hero) == ChampionTickPhase.BeforeCooldowns)
                _championDispatcher.TickHero(this, hero, dt);
            EnsureSpellLists(hero);
            for (var i = 0; i < hero.SpellCooldowns.Count; i++)
            {
                if (_championDispatcher.Handles(hero) && _championDispatcher.OwnsCooldown(hero, i)) continue;
                hero.SpellCooldowns[i] = Math.Max(0.0, hero.SpellCooldowns[i] - dt);
            }
            for (var i = 0; i < hero.SummonerCooldowns.Count; i++)
                hero.SummonerCooldowns[i] = Math.Max(0.0, hero.SummonerCooldowns[i] - dt);
            UpdateBurningEmbers(hero);
            for (var i = 0; i < hero.ItemCooldowns.Count; i++)
                hero.ItemCooldowns[i] = Math.Max(0.0, hero.ItemCooldowns[i] - dt);
            // Batch 144 step 1: the item copies' own update, the server's obj_AI_Base item-buff poll. Dead heroes are
            // polled too (the scripts guard on CO_IS_NOT_DEAD themselves, and DoranT2Health persists through death).
            // Batch 246: the copies' hooks run on the hero's self-buff line (Bb.BbSelfUpdate); the inventory sync runs every step.
            ItemScripts?.Poll(hero, selfUpdate);
            // Batch 196 follow-up: the talents' periodic live adds (the utility marker, Ardor) come back on this poll after a death.
            LoadoutHooks?.PollSelfBuffs(hero, selfUpdate);
            // The character script's own polls (BbChampionModule.Tick) read the same line through SelfUpdateDue.
            if (_championDispatcher.Handles(hero) &&
                _championDispatcher.TickPhase(hero) == ChampionTickPhase.AfterCooldowns)
                _championDispatcher.TickHero(this, hero, dt);
            if (!displaced) TickPendingCast(hero);
        }
        // Recall (Spells/Recall.luaobj, batch 179): its windup and channel run in the summoner module, once per step.
        SummonerScripts?.Tick(dt);
        // Batch 145 step 2: a clone family member (ShacoClone / YorickRevenant / ClonePet) wears a copy of its owner's
        // inventory and the aura scripts' pet branch is written for it, so its copies poll too. Dead clones are not
        // polled — unlike a hero, a clone never returns from death, so its copies die with it (BbItemModule.Clear runs
        // from MatchGameplayHooks.OnDeath) and nothing renews the aura it was re-adding.
        foreach (var pet in _world.Entities.Values
                     .Where(entity => entity.Alive && entity.UnitType != UnitType.Hero &&
                                      Bb.BbItemModule.IsPetHolder(entity)).ToArray())
            ItemScripts?.Poll(pet, StepSelfUpdate(pet));
        // Batch 159 step 2: the bound non-champion units' own character updates (a turret's UpdateSelfBuffStats sample),
        // the same spell update the item copies get. Dead units are polled too: their stats hook is dropped inside the
        // router and their actions hook guards on IS_NOT_DEAD itself. Batch 256: each unit on its own self-buff line, as a hero.
        UnitScripts?.Poll(StepSelfUpdate);
        // Batch 148/150: the advanced summoners' own ticking is gone. Fortify's buffs run their scripts' hooks (BuffOnUpdate*, and
        // FortifyCheck is added by APBonusDamageToTowers' OnBuffActivate, batch 203), and Revive's speed decay is
        // SummonerReviveSpeedBoost's BuffOnUpdateStats on the 0.25 s stats grid (BbBuffRuntime). No engine add is left here.
        for (var index = _world.TimedVisionBubbles.Count - 1; index >= 0; index--)
        {
            var bubble = _world.TimedVisionBubbles[index] with
            { Remaining = _world.TimedVisionBubbles[index].Remaining - dt };
            if (bubble.Remaining <= 0.0)
            {
                if (bubble.EffectNetId != 0) _effects.KillEffect(bubble.EffectNetId);
                _world.TimedVisionBubbles.RemoveAt(index);
            }
            else _world.TimedVisionBubbles[index] = bubble;
        }
        // Batch 404: the timed actions due by this step (an effect that ends with its cube's corpse), in the order they were queued.
        for (var index = 0; index < _timedActions.Count;)
        {
            if (_timedActions[index].At > _time) { index++; continue; }
            var run = _timedActions[index].Run;
            _timedActions.RemoveAt(index);
            run();
        }
    }

    /// <summary>
    /// Runs champion work scheduled after buff lifecycle processing. The match
    /// orchestrator calls this after ticking buffs, wards, and item auras. The
    /// item actives' sub-missile arrivals run first (see <see cref="DeferLanding"/>).
    /// </summary>
    public void TickLateChampions(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
        RunLateLandings();
        _championDispatcher.TickLate(this, dt);
    }

    /// <summary>
    /// Batch 659 (community report of 3 November 2026, "E > R > W cancel not possible?"): a cast pressed during the hero's own locked dash
    /// joins the casts already queued behind that dash instead of replacing them (owner decision B3 #10 queues them until the landing), so
    /// three presses during Lee Sin's Q2 give all three, in press order. A second press of a slot already queued replaces it and goes last.
    /// Batch 663 (owner, 3 November 2026): only a dash whose clip is locked queues; Valor's (Lock=false) casts Blade of the Exile and Ki
    /// Burst at once, as retail does.
    /// </summary>
    private static PendingCastState QueueBehindDash(PendingCastState? queued, PendingCastState entry)
    {
        PendingCastState? head = null, tail = null;
        for (var item = queued; item is not null; item = item.Next)
        {
            if (item.Request.IsSummoner == entry.Request.IsSummoner && item.Request.Slot == entry.Request.Slot) continue;
            if (tail is null) head = item;
            else tail.Next = item;
            tail = item;
        }
        entry.Next = null;
        if (tail is null) return entry;
        tail.Next = entry;
        return head!;
    }

    private void TickPendingCast(Entity hero)
    {
        var pending = hero.PendingCast;
        if (pending is null) return;
        // A cast queued after another waits for that one's cast frame, as a press inside a windup is held (SpellWindup, owner decision of
        // 18 October 2026): Ki Burst queued after Blade of the Exile fires once the blade is out, so it takes the blade's 360 range.
        if (pending.AfterPrevious && SpellWindup.IsWindingUp(hero, settled: true)) return;
        TickPendingCastHead(hero, pending);
        // The next cast queued behind the same dash takes the head once this one has fired or been dropped (and nothing replaced the queue).
        if (pending.Next is { } next && hero.PendingCast is null && hero.Alive)
        {
            next.AfterPrevious = true;
            hero.PendingCast = next;
        }
    }

    private void TickPendingCastHead(Entity hero, PendingCastState pending)
    {
        // Batch 362 (owner finding 3, Ashe E: "does not cast when Ashe arrives the required spot when it's really far away"): the give-up clock
        // restarts each time a champion's walk closes in on its point, so a walk that makes progress fires on arrival however long it takes
        // (Hawkshot from 5000 units walks about eleven seconds, and the clock used to drop the cast on the way). A hero that does not get closer
        // for PendingCastTimeoutSeconds still gives up. Reconstruction: the give-up time is this emulator's, not retail's; summoner approaches keep
        // the plain clock (their ranges are short). The gap is measured to the cast's point (PendingCastPoint); the firing point lies on the line
        // to it, so closing in on the point is closing in on the range.
        if (!pending.Request.IsSummoner && PendingCastPoint(hero, pending.Request) is { } aimedAt)
        {
            var gap = Distance(hero.Position, aimedAt);
            if (gap < pending.BestGap - 1.0)
            {
                pending.BestGap = gap;
                pending.Deadline = _time + PendingCastTimeoutSeconds;
            }
        }
        if (_time >= pending.Deadline)
        {
            hero.PendingCast = null;
            return;
        }
        if (pending.Request.IsSummoner)
        {
            // Queued behind a dash (playtest B3 #10) or walking into range (Ignite, Exhaust, Smite); fire when it can.
            if (SummonerOutOfRangeTarget(hero, pending.Request) is { } farTarget)
            {
                ContinueApproach(hero, pending, farTarget.Position);
                return;
            }
            hero.PendingCast = null;
            if (HandleSummonerCast(hero, pending.Request, _time))
                hero.MatchStatistics.RecordCast(pending.Request.Slot, summoner: true);
            return;
        }
        var check = CanCast(hero, pending.Request.Slot, pending.Request.TargetNetId,
            pending.Request.Position, _time, pending.Request.EndPosition);
        if (!check.Allowed && check.Failure == SpellCastFailure.Range) check = CanCastWithinLeash(hero, pending.Request, check);
        if (check.Allowed)
        {
            hero.PendingCast = null;
            ExecuteCast(hero, pending.Request, _time);
            return;
        }
        if (check.Failure != SpellCastFailure.Range)
        {
            hero.PendingCast = null;
            return;
        }
        var point = PendingCastPoint(hero, pending.Request);
        if (point is null)
        {
            hero.PendingCast = null;
            return;
        }
        ContinueApproach(hero, pending, point.Value);
    }

    /// <summary>The re-plan cadence of a queued cast that walks into range (champion spells and summoners alike).</summary>
    private void ContinueApproach(Entity hero, PendingCastState pending, Position2 point)
    {
        var previous = pending.LastMovePoint;
        if (hero.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled)) return;
        if (previous is Position2 old && DistanceSquared(old, point) <= 120.0 * 120.0 &&
            (hero.Waypoints.Count > 0 || hero.Target is not null || _time < pending.NextMoveRetryAt)) return;
        pending.LastMovePoint = point;
        pending.NextMoveRetryAt = _time + HeroAttackOrderSystem.ScanSeconds;
        ApproachCastPoint(hero, pending.Request, point);
    }

    /// <summary>
    /// Reconstruction (owner decision, 18 October 2026): a unit-targeted cast gets the slack an attack gets. The attack path admits a
    /// planted swing within <c>reach + 100</c> (Map1 <c>ar_StopAttackRangeModifier</c>, <c>CombatService.InCommittedAttackRange</c>), so a
    /// target that steps away while the hero finishes its last step does not send the hero back into a new chase. Retail's spell approach
    /// is native code (Hero.lua has none), so the slack is this server's, not a retail value. It applies once the hero has ended its
    /// approach (no route left, free to move) and only to the centre-to-centre range every champion validator tests; point-targeted and
    /// self casts are unchanged. While the hero is still walking, the strict range keeps deciding.
    /// </summary>
    public const double PendingCastLeash = 100.0;

    // ponytail: the hero is moved toward the target for the length of one validation, so every champion's own range formula (hand-written
    // or script) is reused unchanged; a validator that needs more than the centre distance would have to take a leash argument instead.
    private SpellValidation CanCastWithinLeash(Entity hero, SpellCastRequest request, SpellValidation strict)
    {
        if (!SlotTargetsUnit(hero, request.Slot) || request.TargetNetId == 0 || hero.Waypoints.Count > 0 ||
            hero.Target is not null || hero.ForcedMove is not null ||
            hero.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled) ||
            _world.Get(request.TargetNetId) is not { Alive: true } target) return strict;
        var gap = Distance(hero.Position, target.Position);
        if (gap <= PendingCastLeash) return strict;
        var home = hero.Position;
        hero.Position = new Position2(home.X + (target.Position.X - home.X) / gap * PendingCastLeash,
            home.Z + (target.Position.Z - home.Z) / gap * PendingCastLeash);
        try { return CanCast(hero, request.Slot, request.TargetNetId, request.Position, _time, request.EndPosition); }
        finally { hero.Position = home; }
    }

    private void ApproachCastPoint(Entity hero, SpellCastRequest request, Position2 point)
    {
        // Playtest B2 NL2: keep the queued cast but send nothing mid-dash; a
        // waypoint here overwrites the client's dash (SetMoveOrder already
        // refuses to move a displaced hero). The same inside another spell's uncancellable windup (SpellWindup): the queued cast
        // stays and the next approach retry (HeroAttackOrderSystem.ScanSeconds) walks once the cast frame has run.
        if (hero.ForcedMove is not null || SpellWindup.IsWindingUp(hero)) return;
        var attacking = hero.AttackTarget is not null;
        _combat.InterruptBasicAttack(hero);
        hero.AttackTarget = null;
        hero.LastAttackStopPoint = null;
        if (attacking && _effects.IsReferenceReady(hero)) _effects.SendStopAttack(hero);
        Func<Entity, bool>? visible = _targetVisible is null ? null : other => _targetVisible(hero, other);
        var spell = request.IsSummoner ? SummonerSpellData(hero, request.Slot)! : CastSpellData(hero, request.Slot);
        // Playtest B2 AE4: walk to the nearest firing point inside the
        // centre-to-centre cast range, as attack pursuit does. Routing to the
        // unit centre ends in a wait anchor when a wave surrounds the target.
        var unitCast = request.IsSummoner ? spell.TargetsUnit : SlotTargetsUnit(hero, request.Slot);
        var target = unitCast && _heroOrders is not null ? _world.Get(request.TargetNetId) : null;
        // The firing point follows the range the champion's own validation applies: the one for the current rank.
        // Batch 331: plus the hero's FlatCastRangeMod, as the champion validation adds it (BbChampionModule.Validate).
        var range = request.IsSummoner ? SummonerCastRange(spell.Name)
            : (_championDispatcher.CastRange(hero, request.Slot)
               ?? spell.CastRangeAt((uint)request.Slot < hero.SpellRanks.Count ? hero.SpellRanks[request.Slot] : 0)) + hero.FlatCastRangeMod;
        // Batch 175 (owner finding 21, 24 October 2026: "casting R out of range creates odd minimap pathing: he heads to the teleport target and
        // then back"): a point cast out of range walks to the nearest firing point inside its range, as a unit cast does (playtest B2 AE4), not to
        // the point itself. Gate's cast from the minimap walked 1023 units toward its 6499-unit target before the cast fired at 5497 units.
        // Reconstruction: retail's point approach is native code; the firing point is the one the unit approach uses.
        var firing = unitCast ? point : FiringPoint(hero.Position, point, range);
        var reach = target is null ? range : UnitCastReach(spell, range, hero, target);
        var route = target is { Alive: true }
            ? _movement.CommitMoveOrder(hero, _heroOrders!.ApproachWithin(hero, target,
                Math.Max(0.0, reach - 3.0), reach))
            : _movement.SetMoveOrder(hero.NetId, firing, visible);
        if (_effects.IsReferenceReady(hero))
            _effects.SendWaypoints(hero, route.Count > 0 ? route : _movement.InstallStop(hero), _world.Clock.SyncId());
    }

    /// <summary>
    /// The centre distance within which a unit-targeted spell fires. Batch 331: the unit targeter's can-fire test and
    /// the unit-or-location targeter tests a spell with <see cref="SpellData.CastRangeUseBoundingBoxes"/> edge to edge,
    /// the within-distance test (d^2 - (rCaster + rTarget + range)^2 &lt; 0, each radius
    /// the bounding radius, the GameplayCollisionRadius times the skin scale), and every other one by the centre distance
    /// (the 2D distance test). <paramref name="range"/> already holds the FlatCastRangeMod.
    /// </summary>
    internal static double UnitCastReach(SpellData spell, double range, Entity caster, Entity target) =>
        spell.CastRangeUseBoundingBoxes ? range + caster.CombatRadius + target.CombatRadius : range;

    /// <summary>
    /// The point the approach walks to for a point cast: <paramref name="point"/> itself when <paramref name="from"/> is in range of it, else the
    /// nearest point inside the range on the way from <paramref name="from"/> (3 units short, as the unit approach keeps).
    /// </summary>
    private static Position2 FiringPoint(Position2 from, Position2 point, double range)
    {
        var gap = Distance(from, point);
        if (gap <= range) return point;
        var reach = Math.Max(0.0, range - 3.0);
        return new Position2(point.X + (from.X - point.X) / gap * reach, point.Z + (from.Z - point.Z) / gap * reach);
    }

    private SpellData CastSpellData(Entity hero, int slot) =>
        _data.Spell(_championDispatcher.SpellName(hero, slot));

    internal bool TargetsUnit(Entity hero, int slot) => SlotTargetsUnit(hero, slot);

    /// <summary>
    /// Whether the champion slot is a unit-target cast now: the spell instance's targeting-type getter answers the type a script set with
    /// <c>BBSetTargetingType</c> (<see cref="Entity.SpellTargetingOverrides"/>, TTYPE_Target is 1) before the spell's own.
    /// </summary>
    private bool SlotTargetsUnit(Entity hero, int slot) =>
        hero.SpellTargetingOverrides.TryGetValue(slot, out var overridden) ? overridden == 1 : CastSpellData(hero, slot).TargetsUnit;

    /// <summary>The slot's targeting type: a script's override (BBSetTargetingType), else the spell's <c>TargettingType</c>.</summary>
    private int SlotTargetingType(Entity hero, int slot) =>
        hero.SpellTargetingOverrides.TryGetValue(slot, out var overridden) ? overridden
            : (int)ChampionSpellSupport.RawNumber(CastSpellData(hero, slot), "SpellData*TargettingType", -1.0);

    /// <summary>The data of the spell in a champion slot (the bots read how to aim it from it).</summary>
    internal SpellData SpellDataOf(Entity hero, int slot) => CastSpellData(hero, slot);

    internal bool SummonerTargetsUnit(Entity hero, int slot)
    {
        if ((uint)slot >= SummonerSlots.Length || slot >= hero.SummonerSpells.Count)
            return true;
        var name = hero.SummonerSpells[slot];
        return name != "TeleportCancel" &&
            _data.Spell(SummonerLoadout.CanonicalFromDisplay(name)).TargetsUnit;
    }

    /// <summary>The SpellData*Flags bit the native unit predicate requires before a spell may pick a ward.</summary>
    private const long WardOptInFlag = 0x04000000;

    private bool CastTargetAvailable(Entity hero, Entity target, string spellName, bool targetsUnit)
    {
        var data = _data.Spell(spellName);
        if (!targetsUnit) return true;
        // A ward is a legal unit only for a spell whose flags carry the opt-in bit (of the ten champions only Lee Sin's
        // Safeguard, BlindMonkWOne, and Vayne's Tumble attacks): the native rejects it for every other unit-target spell.
        if (target.IsWard && !(data.Raw.TryGetValue(InibinReader.KeyHash("SpellData*Flags"), out var flags) &&
                               flags.IsNumeric && ((long)flags.AsDouble() & WardOptInFlag) != 0))
            return false;
        // A unit-target cast must still see its enemy when an approach ends.
        // Ground/self casts ignore the request's incidental unit reference.
        return _combat.IsTargetable(target, hero) &&
               (!WorldState.IsEnemy(hero, target) || _targetVisible?.Invoke(hero, target) != false);
    }

    private Position2? PendingCastPoint(Entity hero, SpellCastRequest request)
    {
        // A cone walks to the start the client's targeter puts in front of the aim (the cone targeter's final position, Cone.Fire).
        if (!SlotTargetsUnit(hero, request.Slot))
            return SlotTargetingType(hero, request.Slot) == 3 && Bb.Cone.Fire(CastSpellData(hero, request.Slot), hero.Position,
                       new Position2(request.EndPosition.X, request.EndPosition.Z)) is { } cone
                ? cone.Start
                : new Position2(request.Position.X, request.Position.Z);
        var target = _world.Get(request.TargetNetId);
        // Champion validation has already checked target allegiance and type.
        // Both friendly shields and enemy spells approach the unit's current position.
        return target is { Alive: true } ? target.Position : null;
    }

    private void ExecuteCast(Entity hero, SpellCastRequest request, double now)
    {
        var slot = request.Slot;
        // Accepted out-of-range requests arrive here only when they execute.
        // Count before effects: an immediate lethal cast can freeze the final result.
        hero.MatchStatistics.RecordCast(slot);
        // the reveal routine runs at the cast frame, at the caster's
        // position before the spell moves it, for the first unit of the cast's target list. That list is the
        // clicked unit of a unit-target spell; ground and self casts ignore the request's incidental unit.
        // Every other cast reveals through the list its own cast packet carries: BuildChampionCastInfo
        // (area and cone casts list the enemies their effect selects, see SpellCastTargets.cs) and, for
        // a line missile, RevealMissileHit at the hit.
        if (request.TargetNetId != 0 && _world.Get(request.TargetNetId) is { } castTarget &&
            CastSpellData(hero, slot) is { TargetsUnit: true, RevealsChampion: true })
            _combat.RevealAttack(hero, castTarget);
        if (!_championDispatcher.Handles(hero) ||
            !_championDispatcher.Execute(this, hero, request, now))
            throw new InvalidOperationException(
                $"Spell dispatcher rejected an already validated {hero.ChampionName} cast");
    }

    /// <summary>
    /// Applies the shared resource, cooldown, attack-order and passive-cast
    /// bookkeeping for a validated champion spell. The champion module then
    /// owns its cast packet, delayed effects and state transitions.
    /// <paramref name="atCastFrame"/>: a windup cast spends its cost and starts its cooldown at its cast frame, not here
    /// (<see cref="ChargeChampionCast"/>); an instacast pays here, as the instant-cast routine does.
    /// </summary>
    public ChampionCastContext BeginChampionCast(
        Entity hero, int slot, string spellName, int rank, double now,
        bool interruptOrder = true, bool invokeChampionHook = true, bool free = false, bool atCastFrame = false)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentException.ThrowIfNullOrWhiteSpace(spellName);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (rank < 1) throw new ArgumentOutOfRangeException(nameof(rank));
        EnsureSpellLists(hero);
        var data = _data.Spell(spellName, _data.SpellMaxLevel(hero.ChampionName, slot));
        if (rank > data.MaxRank) throw new ArgumentOutOfRangeException(nameof(rank));
        // free: the cast of a slot a script buff toggled on (Janna's Howling Gale release) costs nothing.
        var manaCost = free ? 0.0 : SlotResourceCost(hero, slot, data.ManaCosts[rank - 1]);
        var cooldown = hero.CooldownsDisabled ? 0.0 :
            Math.Max(0.0, data.Cooldowns[rank - 1] * (1.0 + hero.CooldownMod));
        if (!atCastFrame)
        {
            hero.Mana -= manaCost;
            hero.SpellCooldowns[slot] = cooldown;
        }
        if (interruptOrder)
        {
            _combat.InterruptBasicAttack(hero);
            hero.Waypoints.Clear();
            hero.Target = null;
            hero.AttackTarget = null;
            hero.HeroPursuit = null;
            hero.AttackMoveGoal = null;
            hero.LastAttackStopPoint = null;
            HoldIdleAttacks(hero, data);
        }
        // Reconstruction (27 October 2026, owner finding 78): a windup is priced now and paid at its cast frame (ChargeChampionCast), so the stat
        // block keeps this price until then. The hook below is where a cast's own buffs reset their cost changes (Visionary's BuffOnSpellCast).
        if (atCastFrame && _heroStats is not null)
        {
            hero.HeldManaCosts = null;
            hero.HeldManaCosts = _heroStats.Snapshot(hero).ManaCosts;
        }
        if (invokeChampionHook) _championHooks.OnSpellCast(hero, slot, now);
        return new ChampionCastContext(data, rank, manaCost, cooldown, atCastFrame);
    }

    /// <summary>
    /// The cast frame of a windup cast spends what <see cref="BeginChampionCast"/> deferred. Batch 340: the cast routine
    /// (one cast routine for the client and the server, in the shared spell helper)
    /// calls the cost routine first, before its CastType switch, and the windup reaches it only from its cast frame
    /// (the cast-frame routine). The cost adjustment then raises the slot's cooldown to the cast's
    /// only when it is shorter and only while the slot still holds the cast's spell, and spends the PAR (the toggle's free cast is already in
    /// the cost, 0). A windup that never reaches its cast frame (its caster died, its target was lost) therefore costs nothing.
    /// Reconstruction (batch 378, owner finding 40): the charge is replicated in this same step (the push below). The client's own deduction
    /// (0xBD) only makes the replication store forget the mana (<c>patch126-replication-tables.md</c>); the stat block that carries the new
    /// value used to wait for the once-per-second hero block (<c>CombatService.GoldSendInterval</c>, NOT RETAIL), so Kennen's Thundering
    /// Shuriken showed its energy about a second late (batch 378 record: the charge at 0.200 s, the next 0xCC with mMP at 1.033 s).
    /// </summary>
    internal void ChargeChampionCast(Entity hero, int slot, ChampionCastContext cast)
    {
        if (!cast.AtCastFrame) return;
        EnsureSpellLists(hero);
        // Validation holds a pending windup's mana from the press (BbChampionModule.Validate), so no later cast spends it; the PAR's own
        // clamp (IncPAR, BbNativeHandlers.IncPAR) only meets a drain from outside (a foe's mana burn) inside the windup.
        hero.Mana = Math.Max(0.0, hero.Mana - cast.ManaCost);
        if ((uint)slot < (uint)hero.SpellCooldowns.Count && hero.SpellCooldowns[slot] < cast.Cooldown &&
            string.Equals(ChampionSpellName(hero, slot), cast.Data.Name, StringComparison.OrdinalIgnoreCase))
            hero.SpellCooldowns[slot] = cast.Cooldown;
        // The cast is paid, so the stat block carries the live costs again (Entity.HeldManaCosts, batch 376).
        hero.HeldManaCosts = null;
        // Reconstruction (batch 378): the same push a resource change of a spell uses everywhere else (BbSpellHost, BbNativeHandlers); the
        // store's delta sends only the fields that changed: the mana, and the costs a hook changed while the windup held them.
        if (_effects.IsReferenceReady(hero)) _heroStats?.Push(hero);
    }

    // The caller retires its pending state before refunding. Annie's shared
    // queue carries its own cost, including overlapping cooldown-disabled Qs.
    internal void RefundChampionCast(Entity hero, int slot, ChampionCastContext cast)
    {
        hero.HeldManaCosts = null;
        // Batch 340: a windup cast pays at its cast frame (ChargeChampionCast), so one whose target is lost before it has spent nothing and
        // keeps the slot's own cooldown; an instacast's cost is given back as before.
        if (!cast.AtCastFrame)
        {
            hero.Mana = Math.Min(hero.MaxMana, hero.Mana + cast.ManaCost);
            hero.SpellCooldowns[slot] = 0.0;
        }
        // Reconstruction (owner decision, 29 September 2026): the client is told the slot's cooldown and the mana. the caster's force-stop
        // (the force-stop routine, which the 0x39 handler reaches through the spellbook's stop, with the packet's flag bits) can reset a
        // stopped cast's cooldown (the cooldown refund, only while the slot still holds that spell); which stop and
        // which packets the retail server sent on a lost target is server-only.
        if (_effects.IsReferenceReady(hero))
        {
            _effects.SendCooldown(hero, slot, hero.SpellCooldowns[slot]);
            _effects.SendStats(hero);
        }
    }

    // Reconstruction: scripts define their own order/timer changes; the retail
    // server's own hold after an order-interrupting cast is not recovered. This
    // emulator policy holds idle attacks for the designer duration to protect
    // gestures. CancelOrder lifts the hold.
    private void HoldIdleAttacks(Entity hero, SpellData spell) =>
        _heroOrders?.HoldIdleAttacks(hero, Math.Max(spell.CastTime, _data.DesignerTotalTime(spell)));

    public void OnUnitDeath(Entity unit) => _championDispatcher.OnUnitDeath(this, unit);
    public void OnHeroDeath(Entity unit, ChampionDeathPhase phase)
    {
        // A windup its hero dies in is cancelled unpaid (BbChampionModule.OnHeroDeath), so the costs it held end with it.
        unit.HeldManaCosts = null;
        _championDispatcher.OnHeroDeath(this, unit, phase);
    }
    public void OnRecipientDeathCleanup(Entity unit) => _championDispatcher.OnRecipientDeathCleanup(this, unit);
    public void OnChampionBuffRemoved(Entity unit, BuffState buff) =>
        _championDispatcher.OnBuffRemoved(this, unit, buff);

    /// <summary>
    /// A buff left the unit and the summoner spells' own scripts hear it (batch 142 step 2): the same removal the
    /// champion modules get through <see cref="OnChampionBuffRemoved"/>, for the buffs the <c>Summoner*.luaobj</c>
    /// added. A router only acts on the buff instances it created.
    /// </summary>
    public void OnSummonerBuffRemoved(Entity unit, BuffState buff) =>
        SummonerScripts?.OnBuffRemoved(unit, buff);

    /// <summary>
    /// A buff left the unit and the item scripts' own scripts hear it (batch 144 step 1): the same removal the
    /// champion modules get through <see cref="OnChampionBuffRemoved"/>, for the buffs the <c>Items/&lt;id&gt;.luaobj</c>
    /// scripts added. A router only acts on the buff instances it created.
    /// </summary>
    public void OnItemBuffRemoved(Entity unit, BuffState buff) =>
        ItemScripts?.OnBuffRemoved(unit, buff);

    /// <summary>
    /// A buff left the unit and the units' own character scripts hear it (batch 159 part 1): the same removal the
    /// champion modules get through <see cref="OnChampionBuffRemoved"/>, for the buffs a
    /// <c>CharScript&lt;unit&gt;.luaobj</c> added. Spells/TurretPreBonus.luaobj's <c>OnBuffDeactivate</c> hands over
    /// to TurretBonus the moment its idle window runs out. A router only acts on the buff instances it created.
    /// </summary>
    public void OnUnitBuffRemoved(Entity unit, BuffState buff) =>
        UnitScripts?.OnBuffRemoved(unit, buff);

    public void SendChampionStats(Entity hero)
    {
        if (_effects.IsReferenceReady(hero)) _effects.SendStats(hero);
    }

    public SpellCastInfo BuildChampionCastInfo(
        Entity hero, ChampionCastContext cast, int slot, Position3 target,
        IReadOnlyList<SpellCastTarget>? targets = null, uint missileNetId = 0,
        Position3? end = null, Position3? launch = null)
    {
        var launchPoint = launch ?? new Position3(hero.Position.X,
            _movement.Navigation.HeightAt(hero.Position), hero.Position.Z);
        // the reveal routine runs at the cast frame on the list this packet carries. A
        // unit-target spell already revealed its clicked unit in ExecuteCast; repeating it here at the
        // same moment only restates the same bubble.
        if (targets is { Count: > 0 }) RevealCaster(hero, cast.Data, targets);
        return new SpellCastInfo(
            ProtocolHash(cast.Data.Name), hero.NetId, cast.Rank, hero.NetId,
            missileNetId, target, end ?? target, targets ?? Array.Empty<SpellCastTarget>(),
            (float)cast.Cooldown, (float)cast.ManaCost, checked((byte)slot), launchPoint,
            1.0f, (float)cast.Data.CastDelay, 0.0f, (float)cast.Data.TotalTime, 0.0f);
    }

    public static uint ProtocolHash(string value) => ProtocolEncoding.HashString(value);

    /// <summary>The source name of the spell in a champion slot (the dispatcher's, so forms and swaps are honoured).</summary>
    internal string ChampionSpellName(Entity hero, int slot) => _championDispatcher.SpellName(hero, slot);

    /// <summary>
    /// Whether the champion slot's spell ships <c>SpellData*CanCastWhileDisabled</c> (Alistar's Ferocious Howl, Tryndamere's Undying Rage, Gangplank's
    /// Remove Scurvy; not Olaf's Ragnarok in 1.0.0.126): the owner's can-use callback skips the cannot-cast states for it
    /// (<see cref="SpellOwnerGate"/>), so a controlled caster may press it. Read by the stat block for the slot's castable bit.
    /// </summary>
    public bool CastsWhileDisabled(Entity hero, int slot)
    {
        if ((uint)slot >= ChampionSlotCount || !_championDispatcher.Handles(hero)) return false;
        try { return CastSpellData(hero, slot).RawFlag("SpellData*CanCastWhileDisabled"); }
        catch (InibinException) { return false; }
    }

    private static Position3 Direction(Position3 from, Position3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return length <= 1e-9 ? new Position3(1.0, 0.0, 0.0) :
            new Position3(dx / length, 0.0, dz / length);
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private static bool Finite(Position3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z);

    private static SpellValidation Failure(string reason, SpellCastFailure failure) =>
        new(false, reason, failure);

    private static void EnsureSpellLists(Entity hero)
    {
        while (hero.SpellRanks.Count < ChampionSlotCount) hero.SpellRanks.Add(0);
        while (hero.SpellCooldowns.Count < ChampionSlotCount) hero.SpellCooldowns.Add(0.0);
        while (hero.SummonerCooldowns.Count < SummonerSlots.Length) hero.SummonerCooldowns.Add(0.0);
    }

}
