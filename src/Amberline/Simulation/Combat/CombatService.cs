using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

/// <summary>
/// Authoritative per-match combat state. Pending attacks live here so shots in
/// flight never become shared or global state on an Entity.
/// </summary>
public sealed partial class CombatService : IEndOfGameEngine
{
    // Reconstruction: the server removes a dead non-hero unit after this long and sends it as the NpcDie duration; retail's
    // corpse lifetime is not recovered (the client plays the death animation and drops the unit itself).
    public const double CorpseReapSeconds = 8.0;
    // Map1/LevelScript.luaobj EXP_GIVEN_RADIUS: ordinary lane-unit XP uses
    // 1250 units. Champion-kill XP keeps its separate 1600-unit candidate
    // radius because the shipped client exposes that input independently.
    public const double ExperienceShareRadius = 1250.0;
    // Map1 Constants.var ai_ExpRadius2 ("Radius for Experience sharing").
    public static readonly double ChampionExperienceShareRadius = Map1Constant("ai_ExpRadius2");
    // NeutralMinionSpawn.luaobj HERO_EXP_RADIUS: every GiveExpToNearHeroesFromNeutral call the
    // script makes is made with it, so the engine's own neutral-XP share uses the same distance.
    public static readonly double NeutralExperienceShareRadius = MapScriptData.HeroExperienceRadius;
    // Shipped Map1 Constants.var ambient gold and spawn regeneration values, read from the file.
    public static readonly double PassiveGoldDelay = Map1Constant("ai_AmbientGoldDelay");
    public static readonly double PassiveGoldInterval = Map1Constant("ai_AmbientGoldInterval");
    public static readonly double PassiveGoldAmount = Map1Constant("ai_AmbientGoldAmount");
    // NOT RETAIL: the shipped Constants.var says ai_AmbientGoldAmount 6.5 "granted on ambient gold tick" every ai_AmbientGoldInterval 5
    // seconds. The owner keeps the continuous rate from period footage (batch 161; owner decision, 25 October 2026): 6.5 / 5 = 1.3 gold
    // per second, paid continuously. See the income grant in CombatTick.
    public static readonly double PassiveGoldPerSecond = PassiveGoldAmount / PassiveGoldInterval;
    // Map1 Constants.var ai_DisableAmbientGoldWhileDead ("Disable ambient gold while dead"). Map1 ships 0, so a dead
    // hero keeps its ambient income; the gate is read here so another map's value is honoured.
    public static readonly bool DisableAmbientGoldWhileDead = Map1Constant("ai_DisableAmbientGoldWhileDead") != 0.0;
    public const double GoldSendInterval = 1.0;
    public static readonly double FountainHealRadius = Map1Constant("sp_RegenRadius");
    public static readonly double FountainHealFractionPerTick = Map1Constant("sp_HealthRegenPercent");
    public static readonly double FountainManaFractionPerTick = Map1Constant("sp_ManaRegenPercent");
    public static readonly double FountainTickSeconds = Map1Constant("sp_RegenTickInterval");
    // The GlobalGoldGivenOnDeath of all eight lane turret characters (the shipped inibins agree at 150).
    public static readonly double TurretGold = Amberline.Data.GameData.Default.Champion("OrderTurretNormal").GlobalGoldGivenOnDeath;
    // bounty.ini Global*AssistDurationOverride is 15 seconds in this build.
    // BBApplyAssistMarker commonly requests 10 seconds, but the native event
    // manager replaces that marker duration when this override is nonzero.
    public const double AssistCreditSeconds = 15.0;
    // Installed Map1/Constants.var events_TimeForMultiKill.
    public static readonly double MultiKillSeconds = Map1Constant("events_TimeForMultiKill");

    private static double Map1Constant(string name) => Amberline.Data.GameData.Default.LevelConstants.Values[name];

    // An ambient tick amount over its interval, per second; an interval the map leaves at 0 pays nothing rather than dividing by it.
    private static double AmbientRate(IReadOnlyDictionary<string, double> constants, string amount, string interval) =>
        constants[interval] > 0.0 ? constants[amount] / constants[interval] : 0.0;

    // Each turret whose script has called TurnOnAutoAttack and not TurnOffAutoAttack since (Turret.luaobj TimerFindEnemies), with the
    // object that call gave its attack (batch 693: a SetTarget to another object does not move the attack; see SetTurretAutoAttack).
    private readonly Dictionary<uint, uint> _turretAutoAttack = new();
    // What each turret's client was last told it targets (0x6D/0xC8), which is not the same as the
    // target id the script keeps: see PublishTurretDisplay.
    private readonly Dictionary<uint, uint?> _turretDisplay = new();
    // The terrain that turret sight reads (TurretSeesTarget), loaded on first use from the same data root as the match's grid.
    private NavigationGrid? _sightGrid;
    private NavigationGrid SightGrid => _sightGrid ??= NavigationGrid.Load(_gameData.Paths);

    private readonly WorldState _world;
    private readonly ICombatHooks _hooks;

    /// <summary>A hero's player dropped out of (false) or came back to (true) a live match: forwarded to the script engine's disconnect hooks.</summary>
    public void NotifyHeroConnection(Entity hero, bool connected) => _hooks.OnHeroConnection(hero, connected);
    private readonly ICombatTransport _transport;
    private readonly IRandomSource _random;
    private readonly Amberline.Data.GameData _gameData;
    private readonly MapProgressionRules _progression;
    // Batch 454: this match's map's ambient gold, ambient XP and fountain values (the statics above read the process default).
    private readonly double _passiveGoldDelay;
    private readonly double _passiveGoldPerSecond;
    private readonly bool _disableAmbientGoldWhileDead;
    // Batch 522: the step the income is paid in (0 = every combat step, as on Summoner's Rift) and the game time it is paid up to.
    private readonly double _incomeTickSeconds;
    private double _incomePaidUntil;
    private readonly double _ambientXpDelay;
    private readonly double _ambientXpPerSecond;
    // The game time the ambient XP has been paid up to (starts at ai_AmbientXPDelay).
    private double _ambientXpPaidUntil;
    private readonly bool _disableAmbientXpWhileDead;
    private readonly double _fountainHealRadius;
    private readonly double _fountainHealFraction;
    private readonly double _fountainManaFraction;
    private readonly double _fountainTickSeconds;
    private readonly CombatKarma _karma;
    private readonly HeroStatsService? _heroStats;
    private readonly List<PendingHit> _pendingHits = new();
    private readonly Dictionary<uint, double> _lastMinionRegenSend = new();
    private readonly Dictionary<uint, uint> _lastAggroTarget = new();
    private readonly Dictionary<uint, Dictionary<uint, AssistCredit>> _assistCredits = new();
    private readonly Dictionary<uint, Dictionary<uint, double>> _supportCredits = new();
    private readonly Dictionary<uint, DeathRecapHistory> _deathRecapHistories = new();
    private readonly Dictionary<uint, DeathRecapSnapshot> _lastDeathRecaps = new();
    // Heroes that still had a route at the end of their previous combat step.
    private readonly HashSet<uint> _routedHeroes = new();
    // Hero -> target of a swing planted in range for the next combat step.
    private readonly Dictionary<uint, uint> _plantedHeroSwings = new();
    private long _deathRecapGeneration;
    private double _lastHeroStatsSend;
    private double _time;
    private bool _protectionEngaged;
    private bool _gameOver;
    private bool _endGameSent;
    private bool _shopClosed;
    private bool _barracksSpawnEnabled = true;
    /// <summary>The losing team <c>SetMinionsNoRender</c> switched off, kept as manager state.</summary>
    private Team? _endGameNoRenderTeam;
    private Team _winner;
    private double _gameOverAt;

    public CombatService(
        WorldState world,
        ICombatHooks hooks,
        ICombatTransport transport,
        IRandomSource random,
        Amberline.Data.GameData? gameData = null)
        : this(world, hooks, transport, random, gameData, null)
    {
    }

    /// <summary>
    /// Creates combat with the match's real hero-stat refresh service. Level-up
    /// and respawn transitions require this dependency so they cannot silently
    /// leave derived equipment or buff stats stale.
    /// </summary>
    public CombatService(
        WorldState world,
        ICombatHooks hooks,
        ICombatTransport transport,
        IRandomSource random,
        HeroStatsService heroStats,
        Amberline.Data.GameData? gameData = null)
        : this(world, hooks, transport, random, gameData, heroStats)
    {
    }

    private CombatService(
        WorldState world,
        ICombatHooks hooks,
        ICombatTransport transport,
        IRandomSource random,
        Amberline.Data.GameData? gameData,
        HeroStatsService? heroStats)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _gameData = gameData ?? Amberline.Data.GameData.Default;
        // Batch 454: the death timers, the XP curve and the shared-unit shares are this match's map's own files.
        _progression = MapProgressionRules.Load(_gameData.Paths);
        var constants = _gameData.LevelConstants.Values;
        _passiveGoldDelay = constants["ai_AmbientGoldDelay"];
        _passiveGoldPerSecond = AmbientRate(constants, "ai_AmbientGoldAmount", "ai_AmbientGoldInterval");
        _disableAmbientGoldWhileDead = constants["ai_DisableAmbientGoldWhileDead"] != 0.0;
        // Reconstruction (batch 522): on The Crystal Scar the income is paid every 0.5 s. A 1.0.0.125 Dominion video (owner,
        // 31 October 2026) shows the HUD gold rising 3 gold once every 0.5 s (6 a second); the wiki's V1.0.0.126 notes say
        // "Ambient gold gain reduced by 7%" for Dominion, and 6 x 0.93 = 5.58 is the shipped Map8 Constants.var rate
        // (ai_AmbientGoldAmount 28 every ai_AmbientGoldInterval 5 = 5.6 a second), so the rate stays the shipped one and only
        // the payment step is the video's: 2.8 gold a step, the fraction kept, so the whole-gold HUD moves +3 (now and then +2).
        // The start stays the shipped ai_AmbientGoldDelay 90 s (after the owner's 80 s release, so no conflict).
        _incomeTickSeconds = _gameData.Paths.MapName == GameDataPaths.MapFolder(GameMaps.CrystalScar) ? 0.5 : 0.0;
        _incomePaidUntil = _passiveGoldDelay;
        _ambientXpDelay = constants["ai_AmbientXPDelay"];
        _ambientXpPaidUntil = _ambientXpDelay;
        _ambientXpPerSecond = AmbientRate(constants, "ai_AmbientXPAmount", "ai_AmbientXPInterval");
        _disableAmbientXpWhileDead = constants["ai_DisableAmbientXPWhileDead"] != 0.0;
        _fountainHealRadius = constants["sp_RegenRadius"];
        _fountainHealFraction = constants["sp_HealthRegenPercent"];
        _fountainManaFraction = constants["sp_ManaRegenPercent"];
        _fountainTickSeconds = constants["sp_RegenTickInterval"];
        _karma = new CombatKarma(_gameData, _random);
        _heroStats = heroStats;
        // The source combat module owns a simulated match clock independent of
        // Unix time; the orchestrator advances it with CombatTick(dt).
        _time = 0.0;
        // Every AI unit runs its shipped script (Turret.luaobj today); the host is part of the
        // match because the target selection it replaces was.
        AiHost = new LuaAiService(_world, new CombatLuaAiEngine(this, _world))
        {
            OnError = failure => AiScriptError?.Invoke(failure),
        };
    }

    /// <summary>Where a shipped AI script's failure goes; the composition root points this at the session log.</summary>
    public Action<AiScriptFailure>? AiScriptError { get; set; }

    public bool ProtectionEngaged => _protectionEngaged;
    public double CurrentTime => _time;
    public IReadOnlyList<PendingHit> PendingHits => _pendingHits;
    /// <summary>
    /// Called at the lethal transition, before the ordinary combat pass
    /// clears attackers' retained target IDs. Minion AI uses this edge to
    /// restore its saved lane route while it still owns the target-loss
    /// context.
    /// </summary>
    public Action<uint>? TargetLost { get; set; }
    /// <summary>Batch 674: every death, with its credited killer (a pet's kill is its owner's), for a map's own script (BattleTrainingScript).</summary>
    public Action<Entity, Entity?>? UnitDied { get; set; }
    /// <summary>
    /// The Lua AI host every AI unit runs its shipped script through. Built with the match
    /// because the turret target selection it owns used to live here; a test may replace it with
    /// a second implementation of <see cref="IAiHost"/> to run one side of a differential.
    /// </summary>
    public IAiHost AiHost { get; set; }

    /// <summary>
    /// Restores the authoritative combat clock when a match snapshot or a
    /// deterministic replay is attached after the world clock has advanced.
    /// The next ordinary stat interval is still measured from the service's
    /// initial send marker, matching a newly composed match.
    /// </summary>
    public void RestoreClock(double time)
    {
        if (!double.IsFinite(time) || time < 0.0)
            throw new ArgumentOutOfRangeException(nameof(time));
        _time = time;
    }

    public BasicAttackResult BasicAttackDamage(Entity attacker, Entity target, AttackRolls? rolls = null)
    {
        _hooks.OnBasicAttackStart(attacker, target);
        var selected = rolls ?? LiveAttackRolls(attacker, target);
        if (_hooks.AttackIgnoresCrit(attacker, target)) selected = selected with { Crit = 1.0 };
        var result = CombatRules.BasicAttackDamage(attacker, target, selected,
            _gameData.LevelConstants.DamageRatios);
        if (result.Dodged) _hooks.OnBasicAttackDodge(attacker, target);
        if (result.Missed || result.Dodged)
        {
            _hooks.BreakAttackStealth(attacker);
            _hooks.OnBasicAttackMiss(attacker);
        }
        else
        {
            _hooks.BreakAttackStealth(attacker);
        }
        return result;
    }

    private AttackRolls LiveAttackRolls(Entity attacker, Entity target)
    {
        var missChance = attacker.Buffs
            .Where(buff => buff.Payload.Has(BuffPayloadField.MissChance))
            .Sum(buff => buff.Payload.MissChance);
        var miss = Math.Clamp(missChance, 0.0, 1.0) > 0.0 ? _random.NextDouble() : 1.0;
        if (miss < Math.Clamp(missChance, 0.0, 1.0)) return new AttackRolls(1.0, 1.0, miss);
        var dodge = target.UnitType == UnitType.Hero && !attacker.DodgePiercing
            ? (_karma.Roll(target.NetId, attacker.UnitType == UnitType.Hero ? 1 : 3, target.Dodge) ? 0.0 : 1.0)
            : _random.NextDouble();
        if (!attacker.DodgePiercing && dodge < Math.Clamp(target.Dodge, 0.0, 1.0))
            return new AttackRolls(1.0, dodge, miss);
        var critical = attacker.UnitType == UnitType.Hero
            ? (CombatRules.CanCriticallyStrike(attacker, target) &&
               _karma.Roll(attacker.NetId, target.UnitType == UnitType.Hero ? 0 : 2, attacker.CritChance) ? 0.0 : 1.0)
            : _random.NextDouble();
        return new AttackRolls(critical, dodge, miss);
    }

    public bool QueueBasicAttack(Entity attacker, Entity target, AttackRolls? rolls = null) =>
        QueueBasicAttack(attacker, target, rolls, committed: false);

    private bool QueueBasicAttack(Entity attacker, Entity target, AttackRolls? rolls, bool committed)
    {
        if (!CanAttack(attacker, target, committed) || attacker.AttackCooldown > 0.0) return false;
        Geometry.FaceTowards(attacker, target.Position);
        var missileNetId = _world.Allocate(UnitType.Missile);
        var result = TurretRules.IsShrineTurret(attacker)
            ? BasicAttackResult.Hit(TurretRules.ShrineDamage, false)
            : BasicAttackDamage(attacker, target, rolls);
        // BBSkipNextAutoAttack (set by a BuffOnPreAttack hook, which BasicAttackDamage ran): the script cast a spell in the swing's place, so no attack
        // is made: no 0x0D, no hit, no hit hooks. The swing still uses the attack timer (the spell was the attack). Reconstruction: the flag is
        // server-only in the stock client (it only stores it), so what a skipped attack keeps is read from the shipped scripts, which all follow the
        // flag with the replacing BBSpellCast.
        if (attacker.SkipNextAutoAttack)
        {
            attacker.SkipNextAutoAttack = false;
            attacker.AttackCooldown = attacker.AttackPeriod;
            // The replacing spell lands after its cast time (Three Talon Strike: 0.175 s) and reads this swing's roll as its HitResult.
            attacker.ReplacedSwing = result.Outcome;
            return true;
        }
        attacker.ReplacedSwing = null;
        var animation = result.Outcome == BasicAttackOutcome.Normal ? SelectAttackAnimation(attacker) : 0;
        if (_transport.IsWireReady(attacker) && _transport.IsWireReady(target))
        {
            // A swing the server dropped in its windup is still the client's current SpellCaster until a 0x39 retires it
            // (Entity.ClientSwingDropped): the client plays it to its cast frame. The 0x0D below would clear that flag without a stop,
            // so the dropped swing is retired first (a ghost whose windup was cancelled by a script swung again at once: finding 36).
            if (attacker.ClientSwingDropped) _hooks.OnDroppedSwingReplaced(attacker);
            // A script's chain attack (Sivir's Ricochet) leaves as a cast instead (ICombatHooks.SendAttackAsCast); a miss or dodge keeps
            // the 0x0D, whose slot byte picks the character's own miss and dodge attacks.
            if (attacker.UnitType != UnitType.Hero || result.Outcome is not (BasicAttackOutcome.Normal or BasicAttackOutcome.Critical) ||
                !_hooks.SendAttackAsCast(attacker, target, missileNetId))
                _transport.SendBasicAttack(attacker, target, missileNetId,
                    // Batch 693: no extra time for any attacker, turrets included. The stock 0x0D handler times the cast frame
                    // at receipt + extra + its own attack cast delay, period x (0.3 + AttackDelayCastOffsetPercent), the AttackWindup the
                    // pending hit below waits for; so with 0 the client draws the turret's wind-up (0.128 s for a lane turret) and its shot
                    // leaves when the server's does. The inherited -0.17 (no derivation) put the cast frame in the past: no wind-up, and the
                    // shot left 0.128 s before the server launched it. Reconstruction: retail's byte is not in the shipped files; 0 is the
                    // value whose cast frame is the server's.
                    0.0, result.Outcome, animation);
            attacker.ClientAttackArmed = true;
            attacker.ClientSwingDropped = false;
            attacker.ClientSwingMissileNetId = missileNetId;
        }
        if (attacker.UnitType == UnitType.Hero) _hooks.OnBasicAttackSent(attacker, target, missileNetId);
        attacker.AttackCooldown = attacker.AttackPeriod;
        if (result.Damage <= 0.0 && !result.Missed && !result.Dodged)
        {
            // No pending hit carries a cast frame for a zero-damage swing, so it reveals at once.
            RevealAtCastFrame(attacker, target);
            return true;
        }
        var queued = new PendingHit(attacker.NetId, target.NetId, result.Damage,
            DamageType.Physical, DamageSourceKind.Basic, attacker.AttackWindup,
            // A CastType 0 attack (both shrine spells, on either map) carries no flight (TurretRules.StatsFor): it lands at the windup's end.
            attacker.Position, attacker.MissileSpeed, missileNetId, Outcome: result.Outcome)
        {
            TargetChampionDeaths = target.ChampionDeaths,
            TargetYorickRevival = target.YorickRevival,
            AttackOverride = CombatRules.HasAttackOverride(attacker),
        };
        _pendingHits.Add(queued);
        if (attacker.AttackWindup <= 0.0)
        {
            RevealAtCastFrame(attacker, target);
            StartPierceLine(attacker, target, queued);
            _hooks.OnBasicAttackLaunch(attacker, target, missileNetId);
        }
        return true;
    }

    // The attack goes through the same reveal routine as a spell, which runs when the cast executes (the
    // cast frame, the end of the windup), not when the swing starts: a swing dropped in its windup reveals nothing. It
    // honours the attack spell's SpellRevealsChampion flag (default true; MalzaharVoidling attacks ship False).
    private void RevealAtCastFrame(Entity attacker, Entity target)
    {
        if (_gameData.BasicAttackRevealsChampion(
                attacker.UnitType == UnitType.Hero ? attacker.ChampionName : attacker.Name))
            RevealAttack(attacker, target);
    }

    private int SelectAttackAnimation(Entity attacker)
    {
        var probabilities = attacker.BasicAttackAnimationProbabilities;
        if (probabilities.Count <= 1) return 0;
        var roll = (float)_random.NextDouble();
        float cumulative = 0;
        for (int i = 0; i < probabilities.Count; i++)
        {
            cumulative += probabilities[i];
            if (roll < cumulative) return i;
        }
        return 0; // The stock code falls back to the base attack without normalization.
    }

    public bool ApplyHit(
        uint sourceNetId,
        uint targetNetId,
        double amount,
        DamageType damageType = DamageType.Physical,
        DamageSourceKind? sourceKind = null,
        bool isReflect = false,
        bool sourceIsPeriodic = false,
        uint? spellOwnerNetId = null,
        byte typeByte = 0x84,
        double vampScale = 1.0,
        DeathRecapOrigin? recapOrigin = null,
        Entity? callForHelpSource = null,
        bool? attackOverride = null,
        bool bypassSpellShield = false,
        bool ignoreDamageIncreaseMods = false,
        bool ignoreDamageCrit = false,
        bool sendDamagePacket = true)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount));
        // Reconstruction: a hit below zero deals nothing. RapidReload's true damage is health minus one, which is -1 on a unit at zero health (the
        // script caps the hit at the health and does not floor it), and a script hit must not raise (batch 255: script.exception in the seed-61 match).
        if (amount < 0.0) amount = 0.0;
        var target = _world.Get(targetNetId);
        // A unit's own InternalRaw hit is its own end (ExpirationTimer's 9999 on the helper a script spawned, a ward's expiry). Batch 246: it
        // passes the engine's Protected flag (a placemarker is Protected to keep it out of target selection) as it passes invulnerability
        // below, so the helper dies by the ordinary kill, the retail route of BBApplyDamage. Every other hit on a Protected unit is refused.
        // Batch 709: a placemarker's own hit of any source passes the flag too. The flag is the engine's stand-in for keeping it out of target
        // selection, and a self hit selects nobody; Talon's return loops (TalonRakeMissileOne, TalonShadowAssaultMisOne) end each blade's cube
        // with 50000 DAMAGESOURCE_DEFAULT physical from the cube to itself, after SetInvulnerable false and SetTargetable true, and every W left
        // three cubes and every R eight. Invulnerability below still refuses such a hit (TrundleDesecrate's 9999 on its still invulnerable
        // cube). Reconstruction: retail's damage gate is server-only; every shipped end of a placemarker by damage is the unit's own hit.
        var ownKill = sourceKind == DamageSourceKind.InternalRaw && sourceNetId == targetNetId;
        var placemarkerOwnHit = sourceNetId == targetNetId && target is { Placemarker: true };
        if (target is null || !target.Alive || target.Health <= 0.0 || target.Protected && !ownKill && !placemarkerOwnHit) return false;
        // Batch 227, narrowed in batch 230 (owner rule: the evidence is the shrine's, so the rule is the shrine's). Only the fountain shrine's own
        // DAMAGESOURCE_INTERNALRAW hit ignores resistances, immunities, pre-damage hooks and shields (patch V1.0.0.108: "Shrine (spawn) turrets now
        // ignore all resistances and immunities"; OrderTurretShrineBasicAttack and ChaosTurretShrineBasicAttack deal it that way). It reaches a
        // champion under Kayle's Intervention. Reconstruction: the shipped files show the immunity for the shrine alone, so every other InternalRaw
        // use keeps its pre-227 handling, and a unit's own self kill (Bushwhack's trap, a ward's expiry) still passes its own invulnerability.
        var shrineHit = sourceKind == DamageSourceKind.InternalRaw && _world.Get(sourceNetId) is { } shrine && TurretRules.IsShrineTurret(shrine);
        if (IsInvulnerable(target) && !shrineHit && !ownKill || _hooks.IsGodMode(target))
        {
            // The client shows "Invulnerable" for a 0x68 whose result type is DAMAGEFLOW_INVULNERABLE (0; 1 is the same
            // without the message). Reconstruction: the server sends it for a hit that invulnerability negates (stasis,
            // Intervention), but not for periodic ticks, reflections or structures, which would only spam it.
            if (amount > 0.0 && !sourceIsPeriodic && !isReflect && IsInvulnerable(target) &&
                target.UnitType is not (UnitType.Turret or UnitType.Structure) &&
                _transport.IsWireReady(target) && _world.Get(sourceNetId) is { } hitSource && _transport.IsWireReady(hitSource))
                _transport.SendApplyDamage(sourceNetId, hitSource, target, 0.0, damageType, 0);
            return false;
        }
        var source = _world.Get(sourceNetId);
        // Empowered autos are one authoritative hit.  Capture their named
        // source before champion on-hit hooks consume the arm buff; the
        // resulting recap row carries the complete post-mitigation damage
        // once, for both lethal and nonlethal impacts. A queued shot passes
        // its launch-time attack override; direct callers read live buffs.
        if (recapOrigin is null && sourceKind == DamageSourceKind.Basic && source is not null)
            recapOrigin = EmpoweredBasicAttackRecapOrigin(source, attackOverride);
        if (target.Kind == EntityKind.ShacoBox && source is not null && source.UnitType != UnitType.Turret &&
            !_hooks.IsTargetable(target, source)) return false;
        var preDamage = _hooks.PreDamage(source, target, amount, damageType, sourceKind);
        if (preDamage.Handled)
        {
            if (!preDamage.Landed) return false;
            if (preDamage.AlreadyApplied) return true;
            amount = preDamage.Damage;
            damageType = preDamage.DamageType;
            sourceKind = preDamage.SourceKind;
            if (preDamage.SourceNetId != 0)
            {
                sourceNetId = preDamage.SourceNetId;
                source = _world.Get(sourceNetId);
            }
        }
        // The swing's own roll (or a scripted attack hit's result, BbNativeHandlers.ApplyDamage): the attack script that runs after the hit
        // (OnBasicAttackHit) is chosen by it and starts from it, even when the script's HitResult changes the hit below.
        var swingOutcome = (typeByte & 0x7f) == 3 ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal;
        if (!bypassSpellShield && _hooks.ConsumeSpellShield(source, target, sourceKind)) return false;
        if (!shrineHit && damageType == DamageType.Magic && target.AnyBuffOrStatus(buff => buff.Payload.MagicImmune)) return false;
        if (!shrineHit && damageType == DamageType.Physical && target.AnyBuffOrStatus(buff => buff.Payload.PhysicalImmune)) return false;
        // A script that re-applies the attack as DAMAGESOURCE_PROC (VayneSilveredBolts third hit, YorickSpectral) deals that hit itself (a nested
        // ApplyHit of kind Proc: Exhaust scales it as PROC, it gives no life steal) and takes the swing away (DamageAmount 0), so this one
        // deals nothing and the callbacks of the attack itself (OnBasicAttackHit, recap, call for help) still run for the Basic kind.
        if (sourceKind == DamageSourceKind.Basic && source is not null)
        {
            // The turret ramp is not a rule here any more (batch 159 part 1): Spells/TurretDamageManager.luaobj's own
            // BuffOnHitUnit multiplies DamageAmount by 0.9 + 0.2 * (its TurretDamageMarker stacks on the turret and on
            // this victim) and stacks the markers through BBSpellBuffAdd, and it runs from this same hook for every
            // turret whose character is bound (all eight lane ones). What follows is only the on-hit scalars.
            amount = _hooks.ModifyBasicAttackDamage(source, target, amount, attackOverride, swingOutcome);
            // An attack override's own primary hit passes IgnoreDamageIncreaseMods (Nidalee's Takedown).
            if (_hooks.TakeProcReapplication().IgnoreIncreaseMods) ignoreDamageIncreaseMods = true;
            // Batch 301: the attack script's HitResult at its hit decides the hit's result (Pantheon's Certain Death makes a normal swing critical; the
            // multiplier is already in the amount). The 0x68 result type follows it (DAMAGEFLOW_CRITICAL 3, DAMAGEFLOW_NORMAL 4); the sound bit stays.
            if (_hooks.TakeScriptedHitResult() is { } scripted)
                typeByte = (byte)((typeByte & 0x80) | (scripted == BasicAttackOutcome.Critical ? 3 : 4));
        }
        amount = _hooks.ModifyDamageBeforeMitigation(source, target, amount, damageType, sourceKind);
        amount = _hooks.ModifyOutgoingDamage(source, target, amount, damageType, sourceKind);
        // BBApplyDamage IgnoreDamageIncreaseMods / IgnoreDamageCrit (DamageEffect.IgnoreModifiers / IgnoreCrit in the client's
        // shared struct; Bilgewater Cutlass, Hextech Gunblade and Tiamat's splash set both). Crit is rolled when a basic attack is
        // queued, and a scripted attack hit's critical HitResult is applied before this call (batch 301: BbNativeHandlers.ApplyDamage
        // multiplies the amount and sends 0x83, BbHookRouter.OnAttackOverrideDamage changes the swing), both only without IgnoreDamageCrit,
        // so ignoreDamageCrit has no stage to skip here; it is accepted so the script input is not dropped silently.
        // The mastery area's own scripts (batch 140): Havoc's CharOnPreDealDamage and Offensive Mastery's BuffOnHitUnit
        // for the attacker, Harden Skin's CharOnBeingHit for the target, all before the raw damage is kept.
        amount = _hooks.ModifyDamageByLoadoutBeforeMitigation(source, target, amount, damageType, sourceKind,
            ignoreDamageIncreaseMods);
        // CharOnBeingHit / BuffOnBeingHit of the script engine: the target's scripts see (and may change) a landed basic attack before the raw damage is kept.
        if (sourceKind == DamageSourceKind.Basic && source is not null)
            amount = _hooks.ModifyDamageOnBeingHit(target, source, amount, damageType,
                (typeByte & 0x7f) == 3 ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal);
        var rawDamage = amount;
        // Batch 227 (narrowed in batch 230): the fountain shrine's hit skips the target's prevention stages (this pre-mitigation hook, armor and
        // resistance, the pre-damage hooks and the shields below), so Undying Rage and Chrono Shift neither clamp nor revive the unit it kills.
        if (!shrineHit) amount = _hooks.ModifyDamageAfterRawBeforeMitigation(
            target, source, amount, damageType, sourceKind);
        // YorickRAZombie can force the temporary life dead before resistance.
        if (!target.Alive) return true;
        // The ward damage rule is the shipped ward script's own BuffOnPreDamage now (batch 149 step 3): Spells/SightWard,
        // VisionWard and WriggleLanternWard cap a basic attack at 1 true damage, zero anything that is not one, and let a
        // turret through. The hand-written rule that stood in for it is gone; it also refused the ward's own
        // DAMAGESOURCE_INTERNALRAW expiry self-kill, so it kept every placed ward alive for ever.
        var spellOwner = spellOwnerNetId.HasValue ? _world.Get(spellOwnerNetId.Value) : source;
        var mitigationSource = sourceKind is DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist
            ? spellOwner : source;
        // Odyn's Veil is the shipped Spells/OdynsVeil.luaobj BuffOnPreDamage now (batch 149 step 4): it takes its
        // tenth off every magic hit and adds what it prevented to CharVars.StoredDamage for the active's own
        // detonation. The hand-written block that stood in for it is gone with the hand-written active that spent
        // that counter; leaving both would have taken two tenths off every hit.
        // Batch 227 (narrowed in batch 230): armor, magic resistance, the damage reductions (the turret deltas among them), the BuffOnPreDamage stage
        // and the shields are all skipped for the fountain shrine's hit (see the pre-mitigation stage above). The source's own BuffOnPreDealDamage still runs.
        if (!shrineHit) amount = ApplyMitigation(amount, target, mitigationSource, damageType);
        // Positive/NegativeTurretDelta write IncPercentPhysicalReduction (+/-5 %, ramped over 90 s) through the
        // turret's own stat pipeline (Spells/PositiveTurretDelta.luaobj and Spells/NegativeTurretDelta.luaobj), so
        // the delta is one of the payloads ApplyReduction below sums and no separate damage stage stands in for it.
        if (!shrineHit) amount = ApplyReduction(target, amount, damageType);
        amount = _hooks.ModifyDamageDealtAfterMitigation(source, target, amount, damageType, sourceKind);
        if (!shrineHit) amount = _hooks.ModifyDamageAfterMitigationBeforeShields(target, source, amount, damageType, sourceKind);
        amount = _hooks.ModifyIncomingDamage(target, source, amount, damageType, sourceKind);
        if (!shrineHit) amount = ConsumeShields(target, amount);
        amount = _hooks.AbsorbDamage(target, source, amount, damageType, sourceKind);
        amount = _hooks.PreprocessItemDamage(source, target, amount, damageType, sourceKind);
        if (amount < 0.0 || !double.IsFinite(amount)) throw new InvalidDataException("combat hook returned invalid damage");
        // This is the authoritative post-mitigation, post-shield amount. It
        // precedes Health/Kill so the lethal hit is part of the victim's
        // immutable last-death snapshot without changing stat or overkill
        // accounting below.
        RecordDeathRecapDamage(source, target, amount, damageType, sourceKind, recapOrigin, spellOwner);
        if (amount > 0.0 && source is not null && !isReflect)
            CallForHelp(target, source, sourceKind);

        // Reconstruction (owner decision, batch 81): a spell that a popped spell shield (Sivir's Spell Shield, Banshee's Veil) zeroed sends no 0x68. Retail's server routine is not
        // in the client, so whether it sent one is unknown; the stock client (its damage-text routine, result type 4) draws the 0 as ordinary damage text
        // above the unit, and the shield's own pop effect already says the spell was eaten (W23 throwaway-client run, 19 October 2026: the 0
        // appears next to the pop with the packet and is gone without it). Owner decision (batch 81 question 2): show only the pop.
        var shieldEaten = amount <= 0.0 && target.ItemState.SpellShieldPopped &&
                          sourceKind is DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist;
        // Batch 505: a hit the client cannot draw sends no 0x68 (the Dominion nexus, see DamageDominionNexus).
        if (sendDamagePacket && !shieldEaten && _transport.IsWireReady(target) && (source is null || _transport.IsWireReady(source)))
            _transport.SendApplyDamage(sourceNetId, source, target, amount, damageType, typeByte);
        if (amount > 0.0) target.LastDamageAt = _time;

        // Health is replicated again by buff/stat refreshes after the hit.
        // Keep the authoritative value valid, including overkill damage.
        // Batch 583 (Cho'Gath's Feast on a regenerating hero): health is a float in the shared code (the script helper's health getter returns a
        // float), and the scripts' numbers are float32 (the sum native, the
        // getter). A hit as large as the float32 health empties it: Feast's execute deals the TargetHealth it read, and against this
        // double the float32 rounding left about 4e-6, so the hero lived on it and regenerated.
        target.Health = (float)amount >= (float)target.Health ? 0.0 : Math.Max(0.0, target.Health - amount);
        var statisticsOwner = ResolveHeroOwner(spellOwner ?? source);
        // Reconstruction: DAMAGESOURCE_INTERNALRAW is the scripts' own bookkeeping (a pet ending itself with 9999 true damage: the ghouls' Summon buffs, the
        // revenant, Tibbers), not damage its hero dealt, so it stays out of the damage dealt statistic.
        // A hit on a unit of the dealer's own team is no damage dealt either: Bushwhack's trap ends with a 4000-damage hit on itself, which would
        // otherwise be counted as its owner's damage (Nidalee's scoreboard read 38 943 after seven traps).
        if (statisticsOwner is not null && sourceKind != DamageSourceKind.InternalRaw && statisticsOwner.Team != target.Team)
            statisticsOwner.MatchStatistics.RecordDamageDealt(amount, damageType, (typeByte & 0x7f) == 3);
        if (target.Kind == EntityKind.Hero && target.UnitType == UnitType.Hero)
            target.MatchStatistics.RecordDamageTaken(amount, damageType);
        RecordDamageCredit(source, spellOwner, target, amount);
        if (_transport.IsWireReady(target)) _transport.SendHealth(target, Math.Max(0.0, target.Health));
        _hooks.OnUnitDamaged(source, target, amount, rawDamage, sourceKind, isReflect, sourceIsPeriodic);
        // A script may name a call-for-help attacker other than the credited
        // source (ShacoBoxSpell: the box wakes the camp, Shaco takes credit).
        if (amount > 0.0 && target.Kind is (EntityKind.Neutral or EntityKind.Monster))
            _hooks.OnNeutralDamaged(target, callForHelpSource ?? source);
        // The attack callback is tied to the landed hit, not to the victim's
        // post-damage life flag.  Empowered autos must consume their arm and
        // start cooldowns on lethal impacts too; proc callbacks retain their
        // own target-alive gates and therefore do not add a second damage hit.
        if (sourceKind == DamageSourceKind.Basic && source is not null)
            _hooks.OnBasicAttackHit(source, target, rawDamage, damageType, attackOverride, swingOutcome);
        if (sourceIsPeriodic) _hooks.OnPeriodicDamage(target, source, amount, damageType, sourceKind);

        // Map1 Constants.var sv_*Ratio (shipped): spell 1, AoE 0.333, persist 1, periodic/proc/reactive/on-death 0.
        // Reconstruction: no sustain from a structure hit (the server routine is not in the client and no
        // period statement about structures was found).
        var sustainSource = sourceKind is DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist
            ? spellOwner : source;
        if (sustainSource is not null && sustainSource.Alive &&
            (sustainSource.UnitType == UnitType.Hero ||
             sourceKind == DamageSourceKind.Basic && sustainSource.LifeSteal > 0.0) &&
            target.UnitType is not (UnitType.Turret or UnitType.Structure))
        {
            // A PROC hit gives no life steal and sv_ProcRatio is 0, so it heals nothing.
            var sustain = sourceKind == DamageSourceKind.Basic ? sustainSource.LifeSteal :
                sourceKind is DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist ? sustainSource.SpellVamp : 0.0;
            if (sustain > 0.0 && amount > 0.0)
            {
                var oldHealth = sustainSource.Health;
                // Map1 sv_SpellAoERatio belongs to the category, not each spell caller.
                // Reconstruction: life steal and spell vamp are engine sustain, not a script IncHealth, so they
                // send no UnitApplyHeal (retail's server-only caller is not in the client binary).
                Healing.Apply(sustainSource,
                    amount * sustain * Math.Max(0.0, vampScale) *
                    (sourceKind == DamageSourceKind.SpellAoe ? 0.333 : 1.0) * HealingMultiplier(sustainSource),
                    announce: false);
                if (sustainSource.Health > oldHealth && _transport.IsWireReady(sustainSource))
                    _transport.SendHealth(sustainSource, sustainSource.Health);
            }
        }

        if (target.Health <= 0.0 && target.Alive)
        {
            Kill(target, sourceNetId);
            // The source schedules an ordinary hero respawn from the lethal
            // hit caller, after the complete death/reward path.  Kill itself
            // also serves direct non-hit lifecycle callers and therefore must
            // not invent this timer before revival markers are processed.
            if (target.UnitType == UnitType.Hero && target.RespawnAt <= 0.0)
                ScheduleRespawn(target);
        }
        return true;
    }

    public bool Kill(Entity unit, uint killerNetId = 0)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.Alive) return false;
        // A second Nexus impact in the same simulation update cannot change
        // the winner or restart the already-issued end-of-game sequence.
        if (unit.Kind == EntityKind.Nexus && _gameOver) return false;
        if (_hooks.TryFinishExistingRevival(unit, killerNetId)) return false;
        // The script buffs that revive their holder as a zombie (BuffOnDeath sets BecomeZombie) are asked once, before the death packet carries the flag.
        var becomesZombie = unit.UnitType == UnitType.Hero && _hooks.DeathBecomesZombie(unit, killerNetId);
        var creditedKiller = ResolveKillCredit(unit, killerNetId);
        var championAssistants = unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0 &&
            creditedKiller is not null
            ? ResolveAssistants(unit, creditedKiller).ToArray()
            : Array.Empty<Entity>();
        var championExperienceRecipients = unit.UnitType == UnitType.Hero &&
            unit.OwnerNetId == 0 && creditedKiller is not null
            ? ResolveChampionExperienceRecipients(unit, creditedKiller, championAssistants)
            : Array.Empty<Entity>();
        var championExperienceVictimLevel = unit.Level;
        // Batch 688: each recipient's level at the death, for its share's level-delta curve (V1.0.0.75, ProgressionRules).
        var championExperienceRecipientLevels = championExperienceRecipients.Select(recipient => recipient.Level).ToArray();
        var championExperienceBase = championExperienceRecipients.Length == 0
            ? 0.0
            : _progression.ChampionKillExperienceBase(championExperienceVictimLevel);
        var deathSeconds = unit.UnitType == UnitType.Hero
            ? RespawnDuration(unit)
            : CorpseReapSeconds;
        var firstBlood = false;
        var bounty = 0.0;
        if (unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0 && creditedKiller is not null)
        {
            firstBlood = _world.Heroes().All(hero => hero.ChampionKills == 0);
            // Batch 498: the map's bounty.inibin values (the [ODIN] section on map 8, Summoner's Rift [Global] elsewhere).
            bounty = _progression.HeroBounty(
                unit.KillStreak, unit.DeathStreak, firstBlood);
        }
        unit.Health = 0.0;
        if (unit.PendingCast is not null) _hooks.OnPendingCastCanceled(unit);
        unit.PendingCast = null;
        unit.Alive = false;
        unit.DeathTime = _time;
        // Batch 447: Dominion's champion death takes 2 from the victim team's nexus (wiki, Dominion, Gameplay: "Killing an enemy
        // champion reduces their nexus' HP by 2"). Reconstruction: every champion death counts; the killer is not traced here.
        // Batch 478b: a kill stops taking nexus health at the floor (wiki, Dominion, Patch History V1.0.0.127: the floor "to 100 Nexus health from
        // 125" for Champion Kills, so 125 in 1.0.0.126). A nexus at the floor takes nothing; one above it takes 2, or down to the floor.
        // Batch 531: the loss floats over the dying champion (DominionScoreSink.ShowNexusLoss).
        if (unit.UnitType == UnitType.Hero && MapDefinitions.OnCrystalScar)
            DamageDominionNexusAboveFloor(unit.Team, 2.0, showLoss: true, dying: unit);
        // Death commits the source lifecycle before callbacks run. Cancel an
        // unfinished queued basic attack here, while leaving launched shots
        // and scripted delayed hits in the queue for their own lifetimes.
        CancelPendingBasicWindups(unit.NetId, requireMissile: true);
        TargetLost?.Invoke(unit.NetId);
        // A zombie life (Yorick's Omen of Death) keeps the original counted death and its
        // eventual deadline. Schedule it here, before BeginZombie exposes
        // the temporary one-health life, so loadout respawn modifiers use the
        // same shared path as ordinary hero deaths.
        if (unit.UnitType == UnitType.Hero && becomesZombie && unit.RespawnAt <= 0.0)
            ScheduleRespawn(unit);
        RecordStructureLoss(unit);
        // A turret's damage markers go with the death of whoever held them (batch 159 part 1): they are
        // Spells/TurretDamageManager.luaobj's own TurretDamageMarker buffs now, and the script buff runtime's
        // RemoveNonPersistantBuffs (OnDeathBuffsReleased, after the kill credit below; at the end of a zombie life, EndZombieLife) drops
        // them like every buff that does not say PersistsThroughDeath.
        unit.AttackTarget = null;
        unit.AttackMoveGoal = null;
        unit.HeroPursuit = null;
        unit.HeroAutoAcquire = true;
        unit.HeroHold = false;
        unit.ForcedMove = null;
        if (_transport.IsWireReady(unit))
        {
            if (unit.UnitType == UnitType.Turret)
                _transport.SendTurretDestroyed(unit, killerNetId);
            else if (unit.UnitType != UnitType.Structure)
                _transport.SendDeath(unit,
                    // Native OnDie dispatches score events on this ID's own
                    // dispatcher; a pet ID does not redirect them to its hero.
                    creditedKiller?.NetId ?? killerNetId,
                    deathSeconds,
                    becomesZombie);
            // Batch 690: the HUD's kill counter and kill callout count a champion's death only from OnChampionDie (CombatPackets.ChampionDie).
            if (unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0)
                _transport.SendChampionDie(unit, creditedKiller?.NetId ?? killerNetId, bounty, championAssistants);
        }
        if (unit.UnitType == UnitType.Hero && unit.Kind == EntityKind.Hero)
        {
            _lastDeathRecaps.Remove(unit.NetId);
            var recap = CaptureDeathRecap(unit, killerNetId);
            if (recap is not null)
                _lastDeathRecaps[unit.NetId] = recap;
        }
        _hooks.OnDeath(unit, killerNetId);
        UnitDied?.Invoke(unit, creditedKiller);

        var killer = creditedKiller;
        if (unit.Kind == EntityKind.Neutral && unit.Name is "Dragon" or "Worm" &&
            killer is { UnitType: UnitType.Hero, Kind: EntityKind.Hero } &&
            WorldState.IsEnemy(killer, unit))
            killer.MatchStatistics.RecordSuperMonsterKill();
        if (unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0)
        {
            unit.ChampionDeaths++;
            // An execution (turret, minion or monster with no credited
            // champion) keeps the victim's kill streak and bounty and does
            // not extend its death streak (Oct 2011 Kill page).
            if (creditedKiller is not null)
            {
                unit.DeathStreak++;
                unit.KillStreak = 0;
            }
            unit.MatchStatistics.RecordDeath(_time);
        }
        if (unit.Kind == EntityKind.Neutral)
            _hooks.OnNeutralDeath(unit, killerNetId);
        _hooks.OnItemKill(killer, unit);
        if (unit.UnitType == UnitType.Hero) _hooks.OnItemDeath(unit);

        if (killer is not null && killer.UnitType == UnitType.Hero && WorldState.IsEnemy(killer, unit))
        {
            switch (unit.Kind)
            {
                case EntityKind.Turret: killer.TurretsKilled++; break;
                case EntityKind.Inhibitor: killer.InhibitorsKilled++; break;
                case EntityKind.Nexus: killer.NexusesKilled++; break;
            }
            if (unit.Kind == EntityKind.Minion) killer.LaneMinionsKilled++;
            else if (unit.Team != Team.Neutral && unit.UnitType is UnitType.Minion or UnitType.Monster)
                killer.OtherMinionsKilled++;
            if (unit.Kind == EntityKind.Neutral)
            {
                // The 2011 counter counts individual jungle monsters. Pet
                // last hits use the same owner credit as their gold reward.
                killer.NeutralMinionsKilled++;
                if (_transport.IsWireReady(killer)) _transport.SendHeroNeutralKills(killer);
            }
            if (unit.UnitType == UnitType.Hero && unit.OwnerNetId == 0)
            {
                killer.ChampionKills++;
                var multiKillCount = killer.MatchStatistics.RecordChampionKill(_time, MultiKillSeconds);
                if (multiKillCount >= 2 && _transport.IsWireReady(killer) && _transport.IsWireReady(unit))
                    _transport.SendHeroMultiKill(killer, unit, multiKillCount);
                killer.KillStreak++;
                if (killer.KillStreak >= 2)
                    killer.LargestKillingSpree = Math.Max(killer.LargestKillingSpree, killer.KillStreak);
                if (killer.KillStreak == 2) killer.KillingSprees++;
                killer.DeathStreak = 0;
                AwardHeroProgress(killer, bounty);
                if (_transport.IsWireReady(unit))
                    _transport.SendGold(unit, killer, bounty);
                var assistants = championAssistants;
                foreach (var assistant in assistants)
                {
                    assistant.ChampionAssists++;
                    // The native assist handler reduces a
                    // qualifying assistant's death streak by one. This is a
                    // counter update at reward time, so a source who died
                    // earlier in the assist window still receives it.
                    assistant.DeathStreak = Math.Max(0, assistant.DeathStreak - 1);
                    // Reconstruction: GoldPoolForAssist (bounty.inibin .5834) is shipped, but the server routine that
                    // pays it is not in the 126 client (the assist-processing handler is a stub; the client
                    // only loads the constant in its bounty load). Used as the pool allocated across all qualifying
                    // assistants of the final bounty (first blood included); the killer keeps the whole bounty.
                    var assistGold = bounty * ProgressionRules.GoldPoolForAssist /
                        Math.Max(1, assistants.Length);
                    AwardHeroProgress(assistant, assistGold);
                    if (_transport.IsWireReady(unit))
                        _transport.SendGold(unit, assistant, assistGold);
                    _hooks.OnItemAssist(assistant, unit);
                    _hooks.OnAssist(assistant, unit);
                }
                DominionPlayerScore?.OnChampionKill(killer, unit, assistants, multiKillCount, firstBlood);
            }
            else if (unit.Kind == EntityKind.Minion) DominionPlayerScore?.OnMinionKill(killer);
            for (var index = 0; index < championExperienceRecipients.Length; index++)
            {
                var recipient = championExperienceRecipients[index];
                // Batch 688: the recipient's share of the pool on its own level delta to the victim (V1.0.0.75's S curve; batch 552 paid
                // every recipient the bare share, so a fed champion took the full reward for kills far below its level).
                var experience = ProgressionRules.SharedChampionKillExperience(
                    championExperienceBase, championExperienceRecipients.Length) *
                    ProgressionRules.ChampionKillLevelDeltaFactor(championExperienceVictimLevel,
                        championExperienceRecipientLevels[index]);
                var earnedCredit = recipient.NetId == killer?.NetId ||
                    championAssistants.Any(assistant => assistant.NetId == recipient.NetId);
                AwardHeroProgress(recipient, experience: experience,
                    // The killer and qualifying assistants earned the XP at
                    // the death event even if they died earlier in the same
                    // assist window. Nearby nonparticipants remain live-only.
                    allowExperienceWhileDead: earnedCredit);
            }
            _hooks.OnKill(killer, unit);
        }
        // The death's order (batch 287): the death hooks (BuffOnDeath, above), the kill credit, then the clear of every buff without
        // PersistsThroughDeath, the script buffs and the audited lists together (ICombatHooks.OnDeathBuffsReleased). BuffOnKill of a
        // Detonating Shot reads the victim's mark. Reconstruction: the server's clear is not in the client; its death routine
        // gives the credit (EVENT_ON_DIE listeners) before the rest of the death. A pet's whole table goes with it (OnPetDeath).
        // Batch 352: a death into a zombie life keeps every buff until the life ends. DoDeath sets bZombie instead of bDead when bBecomeZombie
        // is set and runs no clear; the life's end, BBForceDead, runs RemoveNonPersistantBuffs itself (EndZombieLife).
        // The shipped scripts read the buffs in the life: Caitlyn's mark (not persistent) cancels her channel from BuffOnZombie and from its
        // update's BBGetIsZombie, and YorickReviveAllySelf's BuffOnZombie removes the unit's stuns, poisons, silences and the like by type.
        if (!becomesZombie) _hooks.OnDeathBuffsReleased(unit);
        if (unit.Kind == EntityKind.Pet) _hooks.OnPetDeath(unit);

        // The owner's death ends Annie's Tibbers and Yorick's ghouls (DespawnPetsOf). The cascade stays in the shared death choke
        // point so the pet's wire cleanup and kill attribution have the order of an explicit pet kill.
        if (unit.UnitType == UnitType.Hero) DespawnPetsOf(unit.NetId);

        if (unit.UnitType == UnitType.Turret)
        {
            foreach (var hero in _world.Heroes().Where(hero => WorldState.IsEnemy(hero, unit)))
            {
                GrantGold(hero, TurretGold);
                if (_transport.IsWireReady(unit))
                    _transport.SendGold(unit, hero, TurretGold);
            }
        }

        // Building death is emitted after item/pet lifecycle callbacks. The
        // client needs the death visual before the subsequent protection scan.
        if (unit.UnitType == UnitType.Structure && _transport.IsWireReady(unit))
            _transport.SendStructureDestroyed(unit, killerNetId);

        // The map script's HandleDestroyedObject (BarracksChanged) arms the inhibitor's return, ArmInhibitorReturn, so the death packet
        // carries the script's countdown.
        if (unit.Kind == EntityKind.Inhibitor)
            BarracksChanged?.Invoke(unit, true);
        if (unit.Kind == EntityKind.Nexus)
        {
            FinishMatch(unit);
        }
        else if (unit.Kind == EntityKind.Inhibitor)
        {
            _transport.SendInhibitorState(unit, true, Math.Max(0.0, unit.RespawnAt - _time), killerNetId);
        }
        RecomputeProtection();

        // Reconstruction (batch 277): a script's placemarker (ExpirationTimer's or PropelSpellCaster's own 9999 InternalRaw end, batch 246)
        // pays nobody. Its character data names a reward (TestCube and SpellBook1: ExpGivenOnDeath 77), no shipped script pays for a helper,
        // and the engine retired helpers unpaid before batch 246; how retail's death reward leaves them out is not in the shipped files.
        if (unit.UnitType != UnitType.Hero && !unit.Placemarker)
        {
            // Neutral camps pay their experience through the script's GiveExpToNearHeroesFromNeutral (batch 219: the level host's
            // awarder, wired by ProductionMatchComposition). Batch 336: of the rest, only a barracks minion shares its death experience. Retail's
            // per-unit reward is obj_AI_Base's ScriptExpGiven within ExpGiveRadius, both zeroed by the constructor and filled only by obj_Barracks
            // from the level script's spawn info (the barracks update reads MeleeExpGiven ... SuperExpGiven and ExperienceRadius; the debug layout,
            // the client never reads them). A script-spawned unit, pet, clone, box or ward keeps zero whatever its record's ExpGivenOnDeath says
            // (the test cubes copy the melee minion's 77): Xerath's Arcanopulse cubes paid the enemy beside them two levels a cast. Reconstruction:
            // the server's award itself is not in the client; this is its data's reading.
            var sharesUnitExperience = unit.Kind == EntityKind.Minion;
            var experienceRadius = unit.Kind == EntityKind.Neutral
                ? NeutralExperienceShareRadius : ExperienceShareRadius;
            var neutralRewardTeam = unit.Kind == EntityKind.Neutral
                ? killer?.Team ?? _world.Get(killerNetId)?.Team
                : null;
            var experienceRecipients = sharesUnitExperience
                ? _world.Heroes()
                    .Where(hero => hero.Alive &&
                        (unit.Kind == EntityKind.Neutral
                            ? neutralRewardTeam is Team team && hero.Team == team
                            : WorldState.IsEnemy(hero, unit)) &&
                        DistanceSquared(hero.Position, unit.Position) <= experienceRadius * experienceRadius)
                    .ToArray()
                : Array.Empty<Entity>();
            // A single lane-unit recipient gets 92% (V1.0.0.104); neutral
            // monsters keep their full solo reward.
            var sharedExperience = _progression.SharedUnitExperience(
                unit.ExperienceReward, experienceRecipients.Length,
                unit.Kind == EntityKind.Neutral ? 1.0 : _progression.SoloLaneUnitExperience);
            // Batch 500 (Reconstruction, BBRedirectGold): a killer whose gold is redirected (Promote's super minion) pays its last hit to the redirect
            // target, whoever the hit credit names. Without a redirect this is the killer, as before.
            var goldRecipient = _world.Get(killerNetId)?.GoldRedirectTarget ?? killer;
            foreach (var hero in _world.Heroes().Where(hero => WorldState.IsEnemy(hero, unit)))
            {
                var lastHitGold = goldRecipient is not null && goldRecipient.UnitType == UnitType.Hero &&
                    WorldState.IsEnemy(goldRecipient, unit) && ReferenceEquals(goldRecipient, hero)
                    ? unit.GoldReward : 0.0;
                // Neutral global gold follows the source unit's team even
                // when a minion/monster delivered the hit and has no hero
                // kill credit.  The old corpus encodes this as owner-team
                // reward rather than last-hit gold.
                var rewardTeam = neutralRewardTeam ?? killer?.Team ?? _world.Get(killerNetId)?.Team;
                var globalGold = rewardTeam is Team sourceTeam &&
                    unit.GlobalGoldReward != 0.0 && sourceTeam == hero.Team
                    ? unit.GlobalGoldReward : 0.0;
                // CharacterData GlobalExpGivenOnDeath (turret tiers 30/50/70/
                // 120, Worm 900) reaches every hero on the destroying team,
                // dead included. A turret's destroying team is every enemy in
                // this loop; a neutral pays its reward team. Dead heroes never
                // enter the local sharing set above.
                var globalExperience = (unit.Kind != EntityKind.Neutral ||
                    (neutralRewardTeam is Team xpTeam && xpTeam == hero.Team))
                    ? unit.GlobalExperienceReward : 0.0;
                var experience = (experienceRecipients.Contains(hero) ? sharedExperience : 0.0) +
                    globalExperience;
                var gold = lastHitGold + globalGold;
                if (gold != 0.0 && _transport.IsWireReady(unit))
                    _transport.SendGold(unit, hero, gold);
                if (gold != 0.0 || experience != 0.0)
                    AwardHeroProgress(hero, gold, experience,
                        allowExperienceWhileDead: globalExperience != 0.0);
            }
        }

        if (becomesZombie) _hooks.BeginZombie(unit, killerNetId);
        _lastMinionRegenSend.Remove(unit.NetId);
        ClearCreditHistory(unit.NetId);
        return true;
    }

    public CombatTickResult CombatTick(double dt, bool regenerate = true)
    {
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
        var previousTime = _time;
        _time += dt;
        TickCombatAwareness(dt);
        if (_gameOver)
        {
            // EndOfGame.luaobj's phase clock: the sound fade, the destruction and the scoreboard
            // are its timers (1s / 3.5s / 8.5s), not this method's schedule. The no-render state
            // is manager state the phase switched on, so the tick keeps it true for late units.
            EndOfGameScript?.AdvanceEndOfGame(_time);
            ApplyEndOfGameNoRender();
            return new CombatTickResult(0, 0, _endGameSent);
        }

        // NOT RETAIL: the Constants.var comment speaks of ambient gold "ticks" (ai_AmbientGoldAmount every ai_AmbientGoldInterval),
        // which retail would pay in 6.5-gold steps. The owner keeps the continuous rate from period footage (batch 161): the Xerath
        // champion spotlight (October 2011) shows the HUD gold rising one gold at a time about once a second (estimated ~15 gold in 10 s;
        // the shipped 6.5 per 5 s is 13 in 10 s). Owner decision, 25 October 2026: keep paying continuously, so the rate is prorated:
        // ai_AmbientGoldAmount / ai_AmbientGoldInterval per second. Item and loadout gold-per-10 share the ambient
        // 90-second start gate and this continuous clock. Batch 522: on map 8 the same prorated income is paid in whole
        // _incomeTickSeconds steps instead of every combat step (see the constructor), so the HUD moves every 0.5 s.
        var incomeSeconds = previousTime >= _passiveGoldDelay
            ? dt : Math.Max(0.0, _time - _passiveGoldDelay);
        if (_incomeTickSeconds > 0.0)
        {
            incomeSeconds = _incomeTickSeconds * Math.Floor((_time - _incomePaidUntil) / _incomeTickSeconds);
            if (incomeSeconds > 0.0) _incomePaidUntil += incomeSeconds; else incomeSeconds = 0.0;
        }
        foreach (var hero in _world.Heroes())
        {
            // Ambient income continues while dead (Map1's ai_DisableAmbientGoldWhileDead is 0) and is independent
            // of HP/MP regeneration. Item income keeps its separate continuous clock.
            // Items 3093/3096/3098/3132 each add their own buff (AvariceBlade,
            // PhilosophersStone, KagesLuckyPick, HeartOfGold; MaxStack 1,
            // BUFF_REPLACE_EXISTING), +5 IncFlatGoldPer10Mod apiece. Their
            // scripts share only the tooltip global BuffName =
            // 'WillOfTheAncientsFriendly', which is not the buff identity, so
            // different items stack and copies of one item do not.
            // Batch 149 step 5: that row is read from the live window buffs now — no shipped item record carries
            // FlatGoldPer10Mod, so a gold item's own BBIncStat IncFlatGoldPer10Mod is its whole income — and the
            // hand-written distinct-item count that stood in for it is gone.
            var itemGoldPer10 = hero.Buffs.Sum(buff => buff.Payload.GoldPer10);
            var loadoutGoldPer10 = GoldPer10Bonus(hero);
            var ambientPerSecond = _disableAmbientGoldWhileDead && !hero.Alive ? 0.0 : _passiveGoldPerSecond;
            GrantGold(hero, (ambientPerSecond + (itemGoldPer10 + loadoutGoldPer10) * 0.1) * incomeSeconds);
            if (regenerate)
            {
                if (hero.Alive) Regenerate(hero, dt);
            }
        }
        // Batch 454: ai_AmbientXPDelay, ai_AmbientXPInterval and ai_AmbientXPAmount are the map's own (Map8: 8 XP every 1 s from 0 s; Map1
        // and Map2 ship Amount 0, so nothing is paid there). Paid continuously at amount / interval, as the ambient gold is (owner decision,
        // 25 October 2026). Dead heroes keep it unless ai_DisableAmbientXPWhileDead is set (Map8 ships 0).
        // Reconstruction: the XP goes through AwardHeroProgress, so the hero's experience bonus applies to it, as it does to the kill XP.
        // The XP accrues on the server every step, but is awarded once per GoldSendInterval (one gold/exp block a second per hero, not
        // one per step: a block per step flooded 0xCC, 111,483 packets in the map 8 five-minute run). The total is the same.
        if (_ambientXpPerSecond > 0.0 && _time - _ambientXpPaidUntil >= GoldSendInterval)
        {
            var owedSeconds = _time - _ambientXpPaidUntil;
            foreach (var hero in _world.Heroes().ToArray())
            {
                if (_disableAmbientXpWhileDead && !hero.Alive) continue;
                AwardHeroProgress(hero, experience: _ambientXpPerSecond * owedSeconds, allowExperienceWhileDead: true);
            }
            _ambientXpPaidUntil = _time;
        }
        // Reconstruction (narrowed to what it still is): retail replicates a hero stat when it changes (not shown by the shipped files), and gold and
        // experience now reach the owner that way (ReplicateGold and the block AwardHeroProgress sends with them).
        // This once-per-second block is what keeps the buff-derived stats of every ready client current — the place
        // replication still waits for a timer. Its own delta filter drops the fields that did not move.
        if (_time - _lastHeroStatsSend >= GoldSendInterval)
        {
            foreach (var hero in _world.Heroes().Where(hero => _transport.IsWireReady(hero)))
                _transport.SendHeroStats(hero);
            _lastHeroStatsSend = _time;
        }
        if (regenerate)
        {
            RegenerateMinionsAndStructures(dt);
            RegenerateFountainAllies(dt);
        }
        foreach (var turret in _world.Entities.Values.Where(entity => entity.UnitType == UnitType.Turret))
        {
            // The stat block SendTurretStats carries, less the health (a regen has its own SendHealth below). A bound
            // turret's own buffs change these (TurretBonus's growth, TurretAssistManager's backdoor permanents,
            // PersonalTurretAssistBonus) instead of the counters of the hand-written model, so the block itself is
            // what tells the clients to refresh.
            var before = (turret.AttackDamage, turret.MaxHealth, turret.Armor,
                turret.SpellBlock, turret.HealthRegen, turret.AttackSpeedMod, turret.Protected);
            var oldHealth = turret.Health;
            TurretRules.Tick(turret, _time, dt, applyRegen: regenerate);
            if (!_transport.IsWireReady(turret)) continue;
            if ((turret.AttackDamage, turret.MaxHealth, turret.Armor,
                    turret.SpellBlock, turret.HealthRegen, turret.AttackSpeedMod, turret.Protected) != before)
            {
                _transport.SendTurretStats(turret);
                turret.TurretLastRegenSend = _time;
            }
            else if (turret.Health > oldHealth &&
                     (turret.Health >= turret.MaxHealth || turret.TurretLastRegenSend < 0.0 ||
                      _time - turret.TurretLastRegenSend >= GoldSendInterval))
            {
                _transport.SendHealth(turret, turret.Health);
                turret.TurretLastRegenSend = _time;
            }
        }
        _hooks.OnItemPassiveTick(_time, dt);

        foreach (var dead in _world.Entities.Values.Where(entity => !entity.Alive &&
                     (Minions.MinionAiSystem.IsMinionUnit(entity) || entity.Kind == EntityKind.Neutral ||
                      entity.Kind.IsAttackingPet())))
        {
            _lastAggroTarget.Remove(dead.NetId);
            _lastMinionRegenSend.Remove(dead.NetId);
        }

        // A pierce line started at this tick's launch moves from the next tick, as its swing's own missile does.
        ProcessPierceLines(dt);
        var resolved = ProcessPendingHits(dt);
        var deathsBefore = _world.Entities.Values.Count(entity => !entity.Alive);
        foreach (var attacker in _world.Entities.Values.Where(entity => entity.Alive).ToArray())
        {
            if (_gameOver) break;
            // Attack cooldowns belong to every living unit, including units
            // whose ordinary basic-attack loop is not owned by this service.
            // Batch 670: a residue under 1e-9 s is the float error of summing the steps, not time left, so a period of whole steps ends on its
            // last step (the shrine's 0.4 s is 12 steps at 30 Hz; the residue made it 13, 2.31 attacks a second for its record's 2.5). The
            // spell slots test their ready time the same way (BbUnitCharacterModule.CastSpellSlot, BbChampionModule).
            var cooldown = attacker.AttackCooldown - dt;
            attacker.AttackCooldown = cooldown > 1e-9 ? cooldown : 0.0;
            var isHero = attacker.UnitType == UnitType.Hero && attacker.Kind == EntityKind.Hero;
            var heroRouteEnded = false;
            if (isHero && (attacker.Waypoints.Count > 0 || attacker.Target is not null))
                _routedHeroes.Add(attacker.NetId);
            else if (isHero) heroRouteEnded = _routedHeroes.Remove(attacker.NetId);
            // A planted swing is valid for exactly the next step.
            var plantedTarget = _plantedHeroSwings.Remove(attacker.NetId, out var planted) ? planted : 0u;
            if (attacker.Kind.IsAttackingPet())
            {
                // The pet services own target choice and swings (QueueBasicAttack); only the aggro outline is
                // published here, once per engage and once on target loss.
                UpdateAggroOutline(attacker);
                continue;
            }
            // Batch 664: the same for a scripted minion of BBSpawnMinion (Minion.lua, Aggro.lua): the spawned-unit AI host owns its target
            // and swings (TibbersAiSystem), and its aggro outline is published here as a lane minion's is.
            if (attacker.UnitType != UnitType.Minion && Pets.SpawnedUnitAi.IsLaneLess(attacker.AiScript))
            {
                UpdateAggroOutline(attacker);
                continue;
            }
            if (attacker.UnitType != UnitType.Turret && attacker.UnitType != UnitType.Hero &&
                attacker.UnitType != UnitType.Minion && attacker.Kind != EntityKind.Neutral)
                continue;
            // The Worm's attack cadence is owned by the neutral spell script;
            // it never fires through the ordinary basic-attack loop.
            if (attacker.Kind == EntityKind.Neutral && attacker.Name == "Worm")
            {
                // Target acquisition still owns the neutral aggro outline.  The
                // scripted Worm attack is suppressed here, but its engaged
                // target must remain visible to observers just like any other
                // engaged neutral.
                UpdateAggroOutline(attacker);
                continue;
            }
            if (attacker.UnitType == UnitType.Turret)
            {
                // Turret.luaobj is the whole retail rule, and it runs: the AI host dispatches the
                // engine's events into the unit's copy of DATA/Scripts/Turret.luaobj and the script
                // answers with the natives bound to the services below. State HARDIDLE has no
                // target, HARDIDLE_ATTACKING has one.
                //  - OnTargetLost (the engine raises it when the target died, became untargetable or
                //    protected, or changed side; leaving attack range is not a loss): FindTargetInAcR
                //    at once; a result is taken only while the state is ATTACKING or TAUNTED, and any
                //    other path sets HARDIDLE.
                //  - OnCallForHelp (CombatAwareness) assigns a target the same way; OnTauntBegin and
                //    OnTauntEnd switch to and from TAUNTED; OnCanMove/OnCanAttack return to HARDIDLE.
                //  - TimerFindEnemies, every 0.15 s: in HARDIDLE it searches; a hit sets ATTACKING and
                //    the target but calls nothing else, no hit calls TurnOffAutoAttack(TARGET_LOST). In
                //    ATTACKING or TAUNTED it calls TurnOnAutoAttack when the target is in attack range,
                //    else it sets HARDIDLE and calls TurnOffAutoAttack(MOVING) — the target id stays.
                //  So a turret swings at a new target only from the timer tick after it got the target, whichever of the scan,
                //  OnTargetLost, OnCallForHelp or OnTauntBegin gave it: the swing gate below fires only on the object the last
                //  TurnOnAutoAttack named (batch 693, SetTurretAutoAttack).
                AiHost.StepUnit(attacker, _time, dt);
                // SyncShrineLaser: a Crystal Scar shrine's laser enters and leaves range with the swing gate, at the same tick.
                if (TurretRules.IsCrystalScarShrine(attacker)) PublishTurretDisplay(attacker, attacker.AttackTarget);
            }
            var selectedTarget = attacker.AttackTarget is uint retainedTargetId ? _world.Get(retainedTargetId) : null;
            if (selectedTarget is null)
            {
                if (attacker.UnitType == UnitType.Minion || attacker.Kind == EntityKind.Neutral)
                    UpdateAggroOutline(attacker);
                continue;
            }
            var target = selectedTarget;
            var committed = plantedTarget == target.NetId;
            if (target is null || !CanAttack(attacker, target, committed))
            {
                // A hidden/untargetable target must leave the authoritative
                // aggro edge immediately.  Retaining its id leaves the
                // client-side yellow outline alive and makes the next
                // visible transition look like a duplicate toggle.  Keep an
                // out-of-range live target for ordinary pursuit; the AI can
                // approach it on the next frame.
                if (target is null || !target.Alive || target.Protected ||
                    !_hooks.IsTargetable(target, attacker) ||
                    !WorldState.IsEnemy(attacker, target))
                {
                    CancelPendingBasicWindups(attacker.NetId, requireMissile: false,
                        notifyClient: true);
                    // Through the setter for a turret, so its client's target display retires with
                    // the id (the AI host's own clear already does this for a scripted loss).
                    if (attacker.UnitType == UnitType.Turret) SetTurretTarget(attacker, null);
                    else attacker.AttackTarget = null;
                }
                if (attacker.UnitType == UnitType.Minion || attacker.Kind == EntityKind.Neutral)
                    UpdateAggroOutline(attacker);
                continue;
            }
            if (attacker.UnitType == UnitType.Minion || attacker.Kind == EntityKind.Neutral)
                UpdateAggroOutline(attacker);
            if (attacker.AttackCooldown <= 0.0 && !AttackDisabled(attacker) &&
                !_hooks.IsActionBlocked(attacker) &&
                (attacker.UnitType != UnitType.Turret || _turretAutoAttack.TryGetValue(attacker.NetId, out var armed) && armed == target.NetId) &&
                (attacker.UnitType != UnitType.Minion ||
                 attacker.MinionAutoAttackEnabled &&
                 attacker.MinionState is MinionAiState.Engage or MinionAiState.Taunted) &&
                (attacker.Kind != EntityKind.Neutral || attacker.NeutralState == "engage"))
            {
                // Playtest B2 AE7: the stock client dispatches 0x0D (ch3)
                // before 0xC1 (ch4), so a route or stop in the swing's flush
                // or windup cuts it. A hero still routed here is planted
                // silently (the 0x0D stops the client); one whose route ended
                // during this step (a pursuit-scan stop) swings next step.
                // Playtest B3 #4, 26 Sept 2026: that deferred swing is already
                // committed, so it starts within range + 100 (Hero.lua's
                // ar_StopAttackRangeModifier) instead of re-checking the strict
                // start range against a target that walked on for one step.
                if (isHero && (heroRouteEnded || attacker.Waypoints.Count > 0 || attacker.Target is not null))
                {
                    attacker.Waypoints.Clear();
                    attacker.Target = null;
                    _routedHeroes.Remove(attacker.NetId);
                    _plantedHeroSwings[attacker.NetId] = target.NetId;
                }
                else QueueBasicAttack(attacker, target, null, committed);
            }
        }
        var deathsAfter = _world.Entities.Values.Count(entity => !entity.Alive);
        var sent = _endGameSent;
        return new CombatTickResult(resolved, Math.Max(0, deathsAfter - deathsBefore), sent);
    }

    private sealed class AssistCredit
    {
        public double ExpiresAt { get; set; }
        public double LastDamageAt { get; set; } = double.NegativeInfinity;
    }
}
