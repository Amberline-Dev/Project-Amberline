using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Runtime;

/// <summary>
/// Match-owned implementation of the shared gameplay hooks.  Construction is
/// two-phase because CombatService, HeroStatsService, and BuffLifecycle form a
/// callback cycle.  <see cref="Attach"/> is the only place that closes that
/// cycle and it rejects incomplete compositions before the first tick.
/// </summary>
public sealed partial class MatchGameplayHooks : ICombatHooks, IHeroStatsHooks, IChampionSpellHooks
{
    // Audited nonpersistent aliases only; form, pet and revival controllers
    // keep their existing death policies. Removal must run their callbacks.
    private static readonly HashSet<string> TemporaryHeroDeathBuffs =
    [
        "VayneTumbleBonus", "VayneTumbleFade", "VayneInquisition",
        "MoltenShield", "EyeOfTheStorm", "JannaEoTSBuff",
        "BlindMonkWOneShield", "BlindMonkWTwo",
        "Overdrive", "PowerFist", "ManaBarrier",
        "PrimalSurge", "Takedown", "OrianaRedactShield",
        "EzrealEssenceFlux", "EzrealEssenceFluxMissile",
        "Nimbleness", "ExaltedWithBaronNashor",
        // Playtest B4 JNG-01: these shipped scripts also omit persistence.
        "BlindMonkPassive", "ManaBarrierCooldown", "Prowl",
        "Pyromania", "Pyromania_particle", "DeceiveCritBonus", "FromBehind",
        "CastFromBehind", "OrianaPowerDaggerDisplay", "OrianaHaste", "OrianaSlow",
        "RegenerationPotion", "FlaskOfCrystalWater", "CatalystHeal",
        "OracleElixirSight", "OracleExtractSight", "SpectralFury", "Haste",
        "ZhonyasRingShield", "LightstrikerBuff", "OdinEntropicClaymore",
        "Sheen", "LichBane", "SheenDelay", "Rageblade", "WitsEndCounter",
        "OdinBloodbursterBuff", "Hexdrinker", "BansheesVeil", "BansheesVeilTimer",
        "BootsOfMobilityDebuff", "BurningEmbers", "SummonerHealCheck", "FortifyBuff",
        "Tailwind", "AbyssalScepterAuraSelf", "ManaManipulatorAuraSelf",
        "ManaManipulatorAuraFriend", "RallyingBannerAuraSelf", "RallyingBannerAuraFriend",
        "EmblemOfValor", "EmblemOfValorParticle", "AegisoftheLegionAuraSelf",
        "AegisoftheLegionAuraFriend", "WillOfTheAncientsSelf", "WillOfTheAncientsFriendly",
        "TwoShivPoison",
        "SunfireCapeAura", "LightningRodChain",
        "SoulShroudAuraSelf", "SoulShroudAuraFriend", "JackInTheBoxInternal",
        "Root", "Silence", "Fear", "CantAttack",
        "BlindMonkETwoMissile", "BlindMonkRRoot", "BlindMonkRMarker",
        // Audited patch-126 summoner recipients: Lua has no persistence flag;
        // SpellData bit 8 is clear wherever that record exists.
        "SummonerDot", "Internal_50MS", "GrievousWound",
        "ExhaustSlow", "ExhaustDebuff", "SummonerHaste", "SummonerBoost",
        "SummonerReviveSpeedBoost",
    ];

    // Attached marks, item damage/slow/defense debuffs, and hostile auras do
    // not persist into a recipient's next life. Caster death is separate.
    private static readonly HashSet<string> RecipientDeathMarks =
    [
        "BlindMonkQOne", "BlindMonkQOneChaos", "BlindMonkEOne",
        "BushwhackDebuff", "VayneSilveredDebuff", "VayneSilverParticle1",
        "VayneHunted", "OdinLightbringer", "WormAttack", "WrathDamage", "WrathDebuff",
        "Burning", "EntropyBurning", "Mourning", "Slow", "Cripple", "BlackOmen",
        "MaladyCounter", "BlackCleaver", "ItemSlow", "BilgewaterCutlass",
        "AbyssalScepterAura", "RallyingBanner", "FrozenHeartAura",
    ];

    // Shipped nonpersistent displacement/action locks, including the global
    // Stun that Condemn and Pyromania apply.
    // Batch 724: and the global Taunt that BBApplyTaunt adds (Taunt.luaobj has no PersistsThroughDeath). Galio's Idol of Durand taunts from a
    // buff on Galio, so nothing on the victim took the taunt away when the victim died: the corpse kept the row and the status bit, and a revive
    // inside the taunt brought it back taunted.
    private static readonly HashSet<string> RecipientDeathControls =
    [
        "BlindMonkRKick", "BlindMonkRDamage", "OrianaStun",
        "MoveAwayCollision", "RocketGrab2", "PowerFistSlow",
        "VayneCondemnMissile", "Stun", "Taunt",
        "SweepingBlow", "Propel",
    ];

    private readonly WorldState _world;
    private readonly GameData _data;
    private readonly Func<Team, Position2, bool> _revealsStealth;
    private readonly Action<Team>? _matchCompleted;
    private readonly BuffServiceBundle _buffServices;
    private BuffLifecycle? _buffs;
    private IBuffEffectHooks? _buffEffects;
    private ISpellEffectSink? _effects;
    private ItemDamageService? _itemDamage;
    private ItemPassiveService? _itemPassives;
    private JungleScheduler? _jungle;
    private NeutralAiService? _neutral;
    private SpellSystem? _spells;
    private CombatService? _combat;

    public MatchGameplayHooks(WorldState world, GameData data,
        Func<Team, Position2, bool> revealsStealth, Action<Team>? matchCompleted = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _revealsStealth = revealsStealth ?? throw new ArgumentNullException(nameof(revealsStealth));
        _matchCompleted = matchCompleted;
        _buffServices = new BuffServiceBundle(this);
    }

    public WorldState World => _world;
    public GameData GameData => _data;

    /// <summary>
    /// BuildingBlocks engine hooks (M1/P2: buff ticks, pre-damage, deaths, level-ups). Null in production: the ten
    /// hand-written champions run exactly as before. Only the differential checks install a sink. This is the
    /// champion-script slot alone — the summoner spells' own sink has its own slot (see <see cref="SummonerHooks"/>),
    /// so a match that has created no hero still reports no champion sink.
    /// </summary>
    public IBbHookSink? ScriptHooks
    {
        get => _scriptHooks;
        set { _scriptHooks = value; RebuildSinks(); }
    }

    /// <summary>
    /// The summoner spells' own hooks (batch 142 step 2): every shipped <c>Summoner*.luaobj</c> runs its cast and its
    /// buffs here. Like <see cref="LoadoutHooks"/> this one is installed for every match and every hero — a summoner
    /// spell is not a champion script, and the ten hand-written champions cast them too.
    /// </summary>
    public IBbHookSink? SummonerHooks
    {
        get => _summonerHooks;
        set { _summonerHooks = value; RebuildSinks(); }
    }

    private IBbHookSink? _scriptHooks;
    private IBbHookSink? _summonerHooks;
    private IBbHookSink? _loadoutScriptHooks;
    private IBbHookSink? _itemHooks;
    private IBbHookSink? _unitHooks;
    private IBbHookSink? _sinks;

    /// <summary>
    /// The loadout scripts' own hooks (batch 144): the live buffs a mastery page's talents add (Ardor, whose
    /// <c>BuffOnUpdateActions</c> feeds itself). Its own slot for the same reason the summoner spells have one: a
    /// loadout script is not a champion script, and every hero of every champion wears one. A router only acts on the
    /// units and buff instances it owns.
    /// </summary>
    public IBbHookSink? LoadoutScriptHooks
    {
        get => _loadoutScriptHooks;
        set { _loadoutScriptHooks = value; RebuildSinks(); }
    }

    /// <summary>
    /// The item copies' own hooks (batch 144): the buffs an item script adds hear their updates here
    /// (<c>GlacialShroud</c>'s <c>BuffOnUpdateStats</c>, <c>AtmasImpaler</c>'s owner-health re-read), and later groups
    /// add the combat hooks (<c>ItemOnHitUnit</c> and its siblings). A slot of its own, like the summoner spells': an
    /// item is not a champion script and every hero of every champion buys them.
    /// </summary>
    public IBbHookSink? ItemHooks
    {
        get => _itemHooks;
        set { _itemHooks = value; RebuildSinks(); }
    }

    /// <summary>
    /// The non-champion units' own character scripts (batch 159 step 2): every turret's (and later a jungle camp's) shipped
    /// <c>CharScript&lt;Character&gt;.luaobj</c> runs here, bound to one unit at a time by <c>BbUnitCharacterModule</c>. A
    /// slot of its own, like the items': a unit character script is not a champion script and a match binds them for units
    /// no hero owns.
    /// </summary>
    public IBbHookSink? UnitHooks
    {
        get => _unitHooks;
        set { _unitHooks = value; RebuildSinks(); }
    }

    /// <summary>Rebuilds the dispatch view every script call site consults (see <see cref="Sinks"/>).</summary>
    private void RebuildSinks()
    {
        if (_scriptHooks is null && _summonerHooks is null && _loadoutScriptHooks is null && _itemHooks is null && _unitHooks is null)
        {
            _sinks = null;
            return;
        }
        var fanout = new BbHookFanout();
        if (_scriptHooks is not null) fanout.Add(_scriptHooks);
        if (_summonerHooks is not null) fanout.Add(_summonerHooks);
        if (_loadoutScriptHooks is not null) fanout.Add(_loadoutScriptHooks);
        if (_itemHooks is not null) fanout.Add(_itemHooks);
        if (_unitHooks is not null) fanout.Add(_unitHooks);
        _sinks = fanout;
    }

    /// <summary>
    /// Every script sink installed on this match: the champion modules', the summoner spells', the loadout's, the items'
    /// and the unit characters'. A router only acts on the units and buff instances it owns, so the five answering together
    /// is one event each. Null only when no slot is filled.
    /// </summary>
    private IBbHookSink? Sinks => _sinks;

    /// <summary>
    /// Whether the script engine's character hooks run for this unit (batch 159 step 2): every hero (a champion module's
    /// hero or a hand-written one), and a non-champion unit a unit character module bound to a shipped
    /// <c>CharScript</c> (a turret). The call sites that used to ask for <c>UnitType.Hero</c> alone ask this instead.
    /// </summary>
    private bool ScriptsHandle(Entity unit) => unit.UnitType == UnitType.Hero || (Sinks?.OwnsUnit(unit) ?? false);

    /// <summary>
    /// The mastery area's own hooks (batch 140): every hero's talent scripts run their character hooks here. Unlike
    /// <see cref="ScriptHooks"/> this one is installed for every match and every hero — a mastery page is not a
    /// champion script, and the ten hand-written champions wear one too.
    /// </summary>
    public BbLoadoutHooks? LoadoutHooks { get; set; }

    public IBuffServices BuffServices => _buffServices;

    /// <summary>Closes the first half of the buff callback cycle.</summary>
    public void AttachBuffEffectHooks(IBuffEffectHooks buffEffects)
    {
        _buffEffects = buffEffects ?? throw new ArgumentNullException(nameof(buffEffects));
    }

    public void Attach(
        HeroStatsService heroStats,
        CombatService combat,
        BuffLifecycle buffs,
        IBuffEffectHooks buffEffects,
        ISpellEffectSink effects,
        ItemDamageService itemDamage,
        ItemPassiveService itemPassives,
        NeutralAiService neutral,
        JungleScheduler jungle,
        SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(heroStats);
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _buffEffects = buffEffects ?? throw new ArgumentNullException(nameof(buffEffects));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _itemDamage = itemDamage ?? throw new ArgumentNullException(nameof(itemDamage));
        _itemPassives = itemPassives ?? throw new ArgumentNullException(nameof(itemPassives));
        _neutral = neutral ?? throw new ArgumentNullException(nameof(neutral));
        _jungle = jungle ?? throw new ArgumentNullException(nameof(jungle));
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
    }

    private T Required<T>(T? service, string name) where T : class =>
        service ?? throw new InvalidOperationException(
            $"MatchGameplayHooks is not attached to {name}; attach the complete match composition before use.");

    private BuffLifecycle Buffs => Required(_buffs, nameof(BuffLifecycle));
    private IBuffEffectHooks BuffEffects => Required(_buffEffects, nameof(IBuffEffectHooks));
    private ISpellEffectSink Effects => Required(_effects, nameof(ISpellEffectSink));
    private ItemDamageService ItemDamage => Required(_itemDamage, nameof(ItemDamageService));
    private ItemPassiveService ItemPassives => Required(_itemPassives, nameof(ItemPassiveService));
    private JungleScheduler Jungle => Required(_jungle, nameof(JungleScheduler));
    private NeutralAiService Neutral => Required(_neutral, nameof(NeutralAiService));
    private SpellSystem Spells => Required(_spells, nameof(SpellSystem));
    private CombatService Combat => Required(_combat, nameof(CombatService));

    // IHeroStatsHooks ------------------------------------------------------
    public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
        HeroAttackTimings.ApplyRefresh(hero, _data);

    public IReadOnlyList<double> ManaCosts(Entity hero) =>
        _data.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName, hero.ActiveSpellNames);

    public bool IsPendingActionBlocked(Entity hero) =>
        hero.ChampionState?.BlocksActions == true ||
        hero.AnyBuffOrStatus(buff => buff.DisablesActions || buff.Payload.AbilityDisabled);

    // A slot a script buff toggled on (SpellToggleSlot) casts for free; the stats snapshot replicates the zero cost.
    public bool IsSpellToggledOn(Entity hero, int slot) => Sinks?.IsSpellToggledOn(hero, slot) == true;

    public void RemoveBuff(Entity hero, int slot, bool emitWire) =>
        Buffs.RemoveBuff(hero, slot, emitWire);

    // IChampionSpellHooks -----------------------------------------------
    public void OnSpellCast(Entity hero, int slot, double now)
    {
        // CharOnSpellCast and BuffOnSpellCast of the script engine, right after cost and cooldown (Reconstruction).
        var spellName = slot is >= 0 and < SpellSystem.ChampionSlotCount ? Spells.ChampionSpellName(hero, slot) : null;
        if (Sinks is not null && spellName is not null) Sinks.OnSpellCast(hero, slot, spellName);
        // ItemOnSpellCast of the copies the caster wears (batch 147): the same moment as the script cast hooks,
        // before the hand-written items below. The four converted spell-cast items test
        // SpellVars.DoesntTriggerSpellCasts themselves (Sivir's Q and W, Ashe's Frost Shot ...).
        if (spellName is not null) Spells.ItemScripts?.OnSpellCast(hero, slot, spellName);
        // Batch 149 step 5: the Tear family's cast charge is Spells/TearOfTheGoddessTrack.luaobj's own
        // BuffOnSpellCast (BBIf SpellVars.DoesntTriggerSpellCasts … / BBElse if InstanceVars.CooldownResevoir > 0),
        // so nothing is left for a hand-written cast reaction.
    }

    // The same cast hooks for a summoner slot or Recall (batch 164): a cast that is not a champion slot still crosses
    // every module's CharOnSpellCast and BuffOnSpellCast, which is what ends Shaco's and Vayne's stealth on it.
    public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target)
    {
        if (Sinks is not null) Sinks.OnNamedSpellCast(hero, slot, spellName, target);
    }

    // A running script channel (BbScriptChannels) ends with the stop source the reason names; any other champion has none.
    public void CancelChannel(Entity hero, string reason) => Sinks?.OnCancelChannel(hero, reason);
    // ICombatHooks --------------------------------------------------------
    public bool IsInvulnerable(Entity unit) =>
        unit.AnyBuffOrStatus(buff =>
            buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable);

    public bool IsGodMode(Entity unit) => unit.GodMode;

    public bool IsTargetable(Entity unit, Entity? observer)
    {
        if (!unit.Alive || TurretRules.IsFountainTurret(unit) ||
            unit.AnyBuffOrStatus(buff => buff.Payload.Untargetable) || unit.NotTargetableTo(observer)) return false;
        if (observer is not null && WorldState.IsEnemy(observer, unit) &&
            unit.AnyBuffOrStatus(buff => buff.Payload.Stealthed) &&
            !_revealsStealth(observer.Team, unit.Position))
            return false;
        if (observer is not null && observer.UnitType != UnitType.Turret &&
            unit.Kind == EntityKind.ShacoBox && unit.AnyBuffOrStatus(buff => buff.Payload.Stealthed) &&
            !Spells.CanSeeTarget(observer, unit)) return false;
        return true;
    }

    // The spellblade's SheenDelay is a script buff's own CharOnLaunchAttack now (batch 147): nothing is left of
    // the hand-written item callback at the launch of a swing.
    public void OnBasicAttackLaunch(Entity attacker) { }

    // CharOnLaunchAttack / BuffOnLaunchAttack / BuffOnLaunchMissile of the script engine (E12).
    public void OnBasicAttackLaunch(Entity attacker, Entity target, uint missileNetId)
    {
        OnBasicAttackLaunch(attacker);
        if (Sinks is not null && ScriptsHandle(attacker)) Sinks.OnBasicAttackLaunched(attacker, target, missileNetId);
    }

    // CharOnMiss / CharOnDodge of the script engine (E07), then ItemOnBeingDodged / ItemOnMiss of the copies the
    // attacker wears (batch 147): the same impact of the avoided swing, with the outcome the roll kept.
    public void OnBasicAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome)
    {
        Sinks?.OnBasicAttackAvoided(attacker, target, outcome);
        Spells.ItemScripts?.OnAttackAvoided(attacker, target, outcome);
    }

    /// <summary>
    /// The masteries' own changes to a hit, in the stage the hand-written mastery application used: the attacker's
    /// CharOnPreDealDamage and Offensive Mastery's BuffOnHitUnit (both skipped by IgnoreDamageIncreaseMods), then the
    /// target's CharOnBeingHit (Harden Skin), which is not an increase mod and stays. The hook's own conditions live in
    /// the shipped scripts; what is here is only when each hook name is invoked.
    /// </summary>
    public double ModifyDamageByLoadoutBeforeMitigation(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind, bool ignoreDamageIncreaseMods)
    {
        if (LoadoutHooks is null || amount <= 0.0) return Math.Max(0.0, amount);
        amount = LoadoutHooks.OnPreDealDamage(source, target, amount, damageType, sourceKind, ignoreDamageIncreaseMods);
        // CharOnBeingHit is the hook of a landed basic attack, the same invocation rule the champion path gives it.
        if (sourceKind == DamageSourceKind.Basic)
            amount = LoadoutHooks.OnBeingHit(target, source, amount, damageType);
        return amount;
    }

    // CharOnBeingHit / BuffOnBeingHit of the script engine (E07), then ItemOnBeingHit of the copies the target wears
    // (batch 147): the same moment and the same DamageAmount, with the inventory copy as Owner.
    public double ModifyDamageOnBeingHit(Entity target, Entity attacker, double amount, DamageType damageType, BasicAttackOutcome outcome)
    {
        var result = Sinks is null ? amount : Sinks.OnBeingHit(target, attacker, amount, damageType, outcome);
        return Spells.ItemScripts?.OnBeingHit(target, attacker, result, damageType, outcome) ?? result;
    }

    public void OnBasicAttackStart(Entity attacker, Entity target)
    {
        // The script engine's attack override (Sivir's Ricochet) pays for the swing before it starts, and BuffOnPreAttack of the unit's script
        // buffs runs (Backstab's FromBehind, Deceive's crit bonus): a hero's, and an attacking pet's (the Hallucinate clone carries Backstab),
        // and a unit character module's own (batch 159 step 2).
        if (Sinks is not null && (ScriptsHandle(attacker) || attacker.Kind.IsAttackingPet()))
            Sinks.OnBasicAttackStart(attacker, target);
    }

    public bool SendAttackAsCast(Entity attacker, Entity target, uint missileNetId) =>
        Sinks is not null && ScriptsHandle(attacker) && Sinks.SendAttackAsCast(attacker, target, missileNetId);

    public void OnBasicAttackSent(Entity attacker, Entity target, uint missileNetId)
    {
        if (Sinks is not null && ScriptsHandle(attacker))
            Sinks.OnBasicAttackSent(attacker, target, missileNetId);
    }

    public void OnBasicAttackDodge(Entity attacker, Entity target) =>
        // Talents/127.luaobj CharOnDodge: BBSpellBuffAdd(Nimbleness, BUFF_REPLACE_EXISTING, BUFF_Haste). The add is
        // what the script asked for, with the payload the Nimbleness buff's own script collects (batch 140).
        LoadoutHooks?.OnBasicAttackDodge(attacker, target);

    /// <summary>
    /// A swing that is not an attack override ends the attacker's invisibility. Reconstruction: no stealth script ends on an attack by itself
    /// (<c>Deceive</c>, <c>VayneTumbleFade</c> and <c>HideInShadows</c> hold only a <c>BuffOnSpellCast</c>; Deceive's <c>BuffOnPreAttack</c> arms its
    /// crit bonus and nothing else, which one empowered swing per stealth presupposes), so retail's server ends them natively. Every shipped
    /// stealth buff the server adds with <c>BUFF_Invisibility</c> is one of those: Shaco's Deceive, Evelynn's and Twitch's and Wukong's hides.
    /// The buff goes through the lifecycle, so its <c>OnBuffDeactivate</c> (Deceive's cooldown, unseal and fade) runs.
    /// </summary>
    public void BreakAttackStealth(Entity attacker)
    {
        foreach (var buff in attacker.Buffs.Where(buff => buff.Type == BuffType.Invisibility && buff.Slot >= 0).ToArray())
            Buffs.RemoveBuff(attacker, buff.Slot);
    }

    /// <summary>
    /// The one place a spell shield is hit or broken: the damage gate of <c>ApplyHit</c>, the module ports of
    /// <c>BBBreakSpellShields</c>, the script engine's <c>TargetExecute</c> and its marker add, the items and the summoner spells all
    /// come here. Every shield the unit holds runs its own shipped <c>BuffOnBeingSpellHit</c> (a hero with Banshee's Veil and Sivir's
    /// Spell Shield loses both to one spell), and a shield pops only when its script says so: <c>BansheesVeil.luaobj</c> sets
    /// <c>WillRemove</c> from the running spell's <c>DoesntBreakShields</c> / <c>DoesntTriggerSpellCasts</c> and never for an attack
    /// override, then takes the shield away at its next 0.25 s <c>BuffOnUpdateStats</c> poll. <paramref name="force"/> is
    /// <c>BBBreakSpellShields</c>, whose marker add (<c>BuffOnAllowAdd</c>, duration 37037) pops whatever spell is running. The popped
    /// interval is <see cref="HeroItemState.SpellShieldPopped"/>.
    /// </summary>
    public bool ConsumeSpellShield(Entity? source, Entity target,
        DamageSourceKind? sourceKind, bool force)
    {
        if (!force && sourceKind is not (DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist))
            return false;
        var popped = false;
        // NOT RETAIL: a BansheesVeil that no item script raised has no script instance to pop it (only a test harness adds one directly),
        // so the gate takes it away at once, as the hand-written port always did. The shipped veil pops itself through its BuffOn hooks.
        if (target.Buffs.FirstOrDefault(buff => buff.Name == "BansheesVeil") is { } bare && Sinks?.IsScripted(target, bare) != true)
            popped = (force || BbSpellContext.BreaksShields) && ItemDamage.ConsumeSpellShield(source, target, sourceKind, force);
        if (Sinks is not null)
        {
            if (force) popped |= Sinks.OnBreakSpellShields(target);
            else if (source is not null && source.Team != target.Team) popped |= Sinks.OnBeingSpellHit(source, target, sourceKind);
        }
        if (popped) target.ItemState.SpellShieldPopped = true;
        return popped;
    }

    public PreDamageResult PreDamage(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind)
    {
        if (source is not null && sourceKind == DamageSourceKind.Basic)
        {
            // Yorick's ghouls and his revenant: the swing is the hit hook of the script buff the unit carries (YorickXLogic, YorickRAPetBuff2), which
            // deals the hit as Yorick (BuffOnHitUnit, DAMAGESOURCE_PROC) and takes the swing away; the swing itself deals nothing.
            if (source.Kind is EntityKind.YorickRevenant or EntityKind.YorickGhoul)
            {
                var landed = ScriptPetHit(source, target, amount);
                return new PreDamageResult(true, landed, true, amount, source.NetId,
                    damageType, sourceKind);
            }
            // AnnieTibbersBasicAttack.luaobj TargetExecute: BBApplyDamage from Annie (BBGetChampionBySkinName "Annie") to the target,
            // MAGIC_DAMAGE, DAMAGESOURCE_PROC, base 80/105/130 by her R level, no AP ratio. Tibbers' swing is the carrier; the
            // damage is Annie's proc, so her magic penetration applies, she gets the credit and no sustain applies.
            if (source.PetState is { Kind: Pets.CloneKind.Tibbers } && _world.Get(source.OwnerNetId) is { } annie)
                return new PreDamageResult(true, true, false, Pets.TibbersAiSystem.AttackDamage(annie), annie.NetId,
                    DamageType.Magic, DamageSourceKind.Proc);
            // A unit a script spawned with an AI script of its own (Malzahar's voidlings, Heimerdinger's turrets): its attack spell's script is the hit,
            // dealt as its owner's proc (MalzaharVoidlingBasicAttack, HeimerTBlueBasicAttack), and the swing deals nothing itself.
            // Batch 163: the gate is the three fighting scripts, not "has an AiScript" — a unit a call gave idle.lua carries one now
            // (SpawnedUnitAi.Idle) and Idle.luaobj takes no target and runs no attack spell.
            if (SpawnedUnitAi.Fights(source.AiScript) && source.UnitType != UnitType.Hero && Sinks is not null && target.Alive &&
                Sinks.OnUnitAttackHit(source, target))
                return new PreDamageResult(true, true, true, amount, source.NetId, damageType, sourceKind);
        }
        return PreDamageResult.Unhandled;
    }

    public double ModifyDamageBeforeMitigation(Entity? source, Entity target,
        double amount, DamageType damageType, DamageSourceKind? sourceKind)
    {
        // CharOnPreDealDamage of a script-driven hero (Kayle's Reckoning mark) or of a unit character module's own (batch 159
        // step 2): source-side, before the raw-damage snapshot.
        if (Sinks is not null && source is not null && ScriptsHandle(source))
            amount = Sinks.OnPreDealDamage(source, target, amount, damageType, sourceKind);
        // BuffOnDealDamage of the script buffs of the unit that deals the damage (any unit: a debuffed minion too).
        if (Sinks is not null && source is not null)
            amount = Sinks.OnDealDamage(source, target, amount, damageType, sourceKind);
        // ItemOnPreDealDamage and ItemOnDealDamage of the copies the hitter wears (batch 147 step 4), the item
        // callbacks beside those two. Items/3116.luaobj registers its slow body under both names, so one Rylai hit
        // runs it twice and the second run only renews the pair the first added.
        if (source is not null)
        {
            var periodic = sourceKind is null;
            amount = Spells.ItemScripts?.OnPreDealDamage(source, target, amount, damageType, sourceKind, periodic) ?? amount;
            amount = Spells.ItemScripts?.OnDealDamage(source, target, amount, damageType, sourceKind, periodic) ?? amount;
        }
        return amount;
    }

    public double ModifyDamageAfterRawBeforeMitigation(Entity target, Entity? source,
        double amount, DamageType damageType, DamageSourceKind? sourceKind)
    {
        // BansheesVeil.luaobj BuffOnPreDamage: WillRemove (a spell popped the shield, see HeroItemState.SpellShieldPopped) sets
        // DamageAmount to 0 whatever the source, type or kind of the hit.
        if (target.ItemState.SpellShieldPopped) return 0.0;
        // BuffOnPreMitigationDamage of the target's script buffs (BuildingBlocks engine): the target-side slot after the raw-damage
        // snapshot and before resistance. (BuffOnPreDamage is the slot after resistance, ModifyDamageAfterMitigationBeforeShields.)
        if (Sinks is not null) amount = Sinks.OnPreMitigationDamage(target, source, amount, damageType, sourceKind);
        // These target-side scripts run after the raw-damage snapshot but
        // before resistance.  Keeping this boundary explicit preserves item
        // splash/on-hit raw damage.
        // Batch 159 part 2: the hand-written turret x0.5 against siege and super minions is gone. The shipped
        // character scripts own it — CharOnBeingHit of CharScriptBlue_Minion_MechCannon / _MechMelee (and the Red
        // pair) tests Value1 DAMAGESOURCE_ATTACK with CO_DAMAGE_SOURCETYPE_IS and Attacker with CO_IS_TYPE_TURRET,
        // then BBMath Src1Value 0.5 MO_MULTIPLY DamageAmount and creates FeelNoPain_eff.troy on the minion. It runs
        // from ModifyDamageOnBeingHit, where CharOnBeingHit lives, so the half is in the raw snapshot rather than
        // after it (the script's own hook is the retail boundary).
        return amount;
    }

    // Batch 203: the hand-written AP / 2.5 tower bonus (heroes and the Shaco clone) is gone. The shipped APBonusDamageToTowers add
    // carries it: its BuffOnHitUnit runs from ModifyBasicAttackDamage below, for the hero and for the clone, which holds the buff
    // from Hallucinate's own TargetExecute.

    public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) =>
        ModifyBasicAttackDamage(source, target, damage, attackOverride: null);

    public double ModifyBasicAttackDamage(Entity source, Entity target, double damage,
        bool? attackOverride) =>
        ModifyBasicAttackDamage(source, target, damage, attackOverride, BasicAttackOutcome.Normal);

    public double ModifyBasicAttackDamage(Entity source, Entity target, double damage,
        bool? attackOverride, BasicAttackOutcome outcome)
    {
        // Batch 159 part 2: the hand-written siege-vs-turret and super-vs-structure scalars are gone. The shipped
        // scripts own them and run from the sink below — CharOnHitUnit of CharScriptBlue/Red_Minion_MechCannon
        // (BBIf Target CO_IS_TYPE_TURRET, BBMath DamageAmount x 2.0) and Spells/StructureDamageReduction.luaobj's
        // BuffOnHitUnitBuildingBlocks (BBIf Target CO_IS_TYPE_AI, BBElse BBMath DamageAmount x 0.25), the buff
        // CharScriptBlue/Red_Minion_MechMelee's CharOnActivate puts on the super minion. Turrets are AI units, so
        // only inhibitors and nexuses take the 0.25.
        _procReapplication = default;
        _scriptedHitResult = null;
        if (source.UnitType != UnitType.Hero)
        {
            // BuffOnHitUnit of a non-hero attacker's own script buffs. An attacking pet's have always been run (the Hallucinate clone's
            // HallucinateFull turns its swing into 75 percent of it, dealt as Shaco's proc, and Backstab multiplies it from behind); a
            // fortified turret's now run too, because the shipped SummonerFortify puts its splash there. The hook is not restricted to
            // heroes, so every attacker's buffs reach it.
            var swung = Sinks is not null
                ? Sinks.ModifyBasicAttackDamage(source, target, damage, attackOverride ?? false, outcome) : damage;
            // ItemOnHitUnit of the copy the pet wears (Pet.lua copies the owner's inventory onto it): Items/3123.luaobj
            // is written for exactly this holder, its BBGetPetOwner rewrites Attacker to the pet's owner first.
            return Spells.ItemScripts?.OnHitUnit(source, target, swung, outcome) ?? swung;
        }
        // A script's attack override whose own primary hit is more than a plain swing (VayneTumbleAttack's ratio of the total attack damage) is
        // part of the swing the hit hooks read as DamageAmount, so it comes first: an empowered third Silver Bolts hit re-applies the whole amount.
        if (Sinks is not null)
        {
            damage = Sinks.OnAttackOverrideDamage(source, target, damage, attackOverride ?? source.ScriptAttackOverride is not null, outcome);
            // The override's own hit passes IgnoreDamageIncreaseMods (Takedown): the swing is dealt without the attacker's damage-increase modifiers.
            if (Sinks.TakeAttackOverrideIgnoresIncreaseMods()) _procReapplication = (_procReapplication.Proc, true);
            // The attack script left another HitResult at its hit than the swing rolled (Certain Death): the hit hooks below read that one, and so
            // does the rest of the hit (CombatService.ApplyHit takes it for the 0x68 result type).
            if (Sinks.TakeScriptedHitResult() is { } scripted) outcome = (_scriptedHitResult = scripted).Value;
        }
        // CharOnHitUnit and BuffOnHitUnit of the script engine resolve the hit here, after the hand-written champion branches and before
        // mitigation: they read the swing as DamageAmount and may change it (Backstab x1.2). Reconstruction: retail's order between the
        // champion's hooks and the item callbacks is not in the client; the scripts' writes to DamageAmount (49 of the 146 shipped on-hit scripts) only make
        // sense before the damage is taken.
        if (Sinks is not null)
            damage = Sinks.ModifyBasicAttackDamage(source, target, damage,
                attackOverride ?? source.ScriptAttackOverride is not null, outcome);
        // ItemOnHitUnit of the copies the attacker wears (batch 147), the item callback beside the champion's
        // CharOnHitUnit/BuffOnHitUnit: it reads the same DamageAmount they did. The spellblade bonus is not folded
        // in here any more (batch 147): Spells/Sheen.luaobj and Spells/LichBane.luaobj add it from their own
        // BuffOnHitUnit, which the sink's ModifyBasicAttackDamage above already ran.
        damage = Spells.ItemScripts?.OnHitUnit(source, target, damage, outcome) ?? damage;
        return damage;
    }

    private (bool Proc, bool IgnoreIncreaseMods) _procReapplication;
    private BasicAttackOutcome? _scriptedHitResult;

    public BasicAttackOutcome? TakeScriptedHitResult()
    {
        var answer = _scriptedHitResult;
        _scriptedHitResult = null;
        return answer;
    }

    public bool AttackIgnoresCrit(Entity attacker, Entity target) =>
        Sinks is not null && ScriptsHandle(attacker) && Sinks.AttackOverrideIgnoresCrit(attacker, target);

    public (bool Proc, bool IgnoreIncreaseMods) TakeProcReapplication()
    {
        var answer = _procReapplication;
        _procReapplication = default;
        return answer;
    }

    public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source,
        double amount, DamageType damageType, DamageSourceKind? sourceKind)
    {
        // BuffOnPreDamage of the target's script buffs (BuildingBlocks engine): the damage that will reach the health bar. Reconstruction
        // from the shipped scripts (the hook name is server-only): Black Shield, Hexdrinker and Mana Barrier subtract their hit points from
        // DamageAmount, YorickRAZombie compares it with the current health, while the separate BuffOnPreMitigationDamage hook exists for
        // the pre-resistance reductions.
        if (Sinks is not null) amount = Sinks.OnPreDamage(target, source, amount, damageType, sourceKind);
        // ItemOnPreDamage of the copies the target wears (batch 147 step 4), the same stage: Items/3155.luaobj arms
        // the Hexdrinker shield here and Items/3117.luaobj starts its mobility penalty.
        amount = Spells.ItemScripts?.OnPreDamage(target, source, amount, damageType, sourceKind,
            sourceKind is null) ?? amount;
        // The masteries' own CharOnPreDamage (Talents/125 Tenacity, Talents/144 Defensive) in the same stage, after the
        // target's script buffs and its champion's own hook — where the hand-written reductions sat.
        return LoadoutHooks?.OnPreDamage(target, source, amount, damageType) ?? amount;
    }

    public double ModifyDamageDealtAfterMitigation(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind) =>
        Sinks is not null && source is not null && ScriptsHandle(source)
            ? Sinks.OnBuffPreDealDamage(source, target, amount, damageType, sourceKind) : amount;

    public double ModifyOutgoingDamage(Entity? source, Entity target, double damage,
        DamageType type, DamageSourceKind? sourceKind)
    {
        // Batch 142 part 2: the hand-written Exhaust modifiers are gone. ExhaustDebuff.luaobj's own
        // BuffOnPreDealDamage is the shipped decision (x0.30 on DAMAGESOURCE_ATTACK whatever its type; else
        // x0.65 unless the source is INTERNALRAW or the type is true damage) and it runs once, through
        // ModifyDamageDealtAfterMitigation. Keeping this body as well applied the same multipliers twice
        // (100 basic became 9, not 30).
        return damage;
    }

    public double ModifyIncomingDamage(Entity target, Entity? source, double damage,
        DamageType type, DamageSourceKind? sourceKind)
    {
        // Hallucinate's incoming modifier is HallucinateFull's BuffOnPreDamage, so it runs before mitigation
        // (ModifyDamageAfterRawBeforeMitigation), not here.
        return damage;
    }

    // Mana Barrier was the only champion absorber; it is Blitzcrank's ManaBarrierIcon.BuffOnPreDamage now (BuildingBlocks engine).
    public double AbsorbDamage(Entity target, Entity? source, double damage,
        DamageType type, DamageSourceKind? sourceKind) => damage;

    public double PreprocessItemDamage(Entity? source, Entity target, double damage,
        DamageType type, DamageSourceKind? sourceKind) =>
        ItemDamage.PreprocessItemDamage(source, target, damage, type, sourceKind);

    public double ModifyHealthRegeneration(Entity hero, double amount, double dt) =>
        amount;

    public double ModifyManaRegeneration(Entity hero, double amount, double dt) =>
        amount;

    public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage,
        DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic)
    {
        ItemDamage.OnUnitDamaged(source, target, damage, rawDamage, sourceKind, reflected, sourceIsPeriodic);
        // BuffOnTakeDamage of the target's script buffs (BuildingBlocks engine), once the health was taken.
        if (Sinks is not null && damage > 0.0) Sinks.OnTakeDamage(target, source, damage, sourceKind, sourceIsPeriodic);
    }

    public void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted)
    {
        BuffEffects.SendDamageShieldDelta(target, delta);
    }

    public void RemoveBuff(Entity target, int slot) => Buffs.RemoveBuff(target, slot);

    public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) =>
        OnBasicAttackHit(source, target, rawDamage, type, attackOverride: null);

    public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type,
        bool? attackOverride) =>
        OnBasicAttackHit(source, target, rawDamage, type, attackOverride, BasicAttackOutcome.Normal);

    public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type,
        bool? attackOverride, BasicAttackOutcome outcome)
    {
        // Batch 159 part 2: the hand-written Wraith EternalThirst_buf.troy is gone. CharOnHitUnit of
        // CharScriptWraith and CharScriptLesserWraith creates it itself (BBSpellEffectCreate BindObjectVar=Attacker
        // EffectName=EternalThirst_buf.troy TargetObjectVar=Target), for every landed swing — this hook's own rule
        // (it also runs on lethal hits, never on miss/dodge).
        // Champion on-hit scripts run before the item on-hit dispatcher in
        // CharOnHitUnit and BuffOnHitUnit of the script engine, after the hand-written champion branches (Reconstruction).
        // A unit character module's own reaches the same hook (batch 159 step 2): its attack spell's TargetExecute is the hit.
        if (Sinks is not null && ScriptsHandle(source))
            Sinks.OnBasicAttackHit(source, target, rawDamage, attackOverride ?? source.ScriptAttackOverride is not null, outcome);
        ItemDamage.OnBasicAttackHit(source, target, rawDamage, type);
    }

    // The roll of an avoided swing has no outcome and no target, so the item callbacks for one
    // (ItemOnBeingDodged / ItemOnMiss) run at its impact instead (OnBasicAttackAvoided above).
    public void OnBasicAttackMiss(Entity source) { }

    public void OnPendingHitCanceled(PendingHit hit)
    {
        // The combat queue owns cancellation, while the spell boundary owns
        // retiring any client missile that was created for that hit.
        if (hit.MissileNetId != 0) Effects.SendDestroyMissile(hit.MissileNetId);
    }

    public void OnAttackMissileOnCorpse(PendingHit hit, Entity corpse)
    {
        if (hit.SourceKind != DamageSourceKind.Basic || Sinks is null ||
            World.Get(hit.SourceNetId) is not { } source || !ScriptsHandle(source)) return;
        Sinks.OnBasicAttackOnCorpse(source, corpse, hit.AttackOverride);
    }

    public void OnPendingBasicAttackCanceled(PendingHit hit)
    {
        // Ordinary cancellation callers already send their stop packet. This
        // callback is reserved for the combat tick's automatic positive-
        // windup range/invalid-target cancellation, avoiding duplicate stops.
        if (hit.SourceKind != DamageSourceKind.Basic) return;
        var source = World.Get(hit.SourceNetId);
        if (source is not null && Effects.IsReferenceReady(source))
            Effects.SendStopAttack(source);
    }

    // The client's answer to a 0x0D that arrives while its last swing has not reached its cast frame (batch 246, plan b242 C4):
    // ProcessCastSpell (the 0x0D handler) reads the current SpellCaster's mSpellCasted (its slot 5)
    // and, while it is 0, calls StopSpellcastingObject (slot 4) with forceCast = 1 and destroyMissile = the old
    // cast's isAutoAttack (slot 6). The stop runs the old cast's frame (ExecuteCastFrame), for an auto attack flags its
    // missile (the id at the caster's missile field) destroyed, and stops the caster: the old swing plays its cast frame and
    // its missile is removed, so the two swings are never drawn side by side.
    // Reconstruction: whether the retail server sent a stop first is server-side and not in the client; the stop goes out first here.
    public void OnDroppedSwingReplaced(Entity attacker)
    {
        if (Effects.IsReferenceReady(attacker)) Effects.SendStopAttack(attacker);
    }

    public void OnPendingCastCanceled(Entity unit)
    {
        unit.PendingCast = null;
    }

    // The swing gate of CombatService's auto-attack loop: a running script channel (any spell host's) holds the unit's own swings for its whole length
    // (Entity.ActiveScriptChannels; Reconstruction, owner hand-test 26 October 2026). The stun and stats readers keep IsPendingActionBlocked alone.
    public bool IsActionBlocked(Entity unit) =>
        IsPendingActionBlocked(unit) || unit.ActiveScriptChannels > 0;

    // The death's whole buff clear, after the kill credit (CombatService.Kill calls this once OnKill has run; batch 287): the script buffs
    // without PersistsThroughDeath (the non-persistent buff removal, Sinks), then the rows the audited lists retire, so no clear runs
    // before the credit. Reconstruction: the server's clear is not in the client (the buff manager stubs it). The shared
    // the base unit's death routine notifies EVENT_ON_DIE, whose listeners give the kill credit, before the rest of the death, and the
    // shipped DetonatingShot BuffOnKill reads the victim's mark, so the victim still carries its buffs while the credit runs. A death into a
    // zombie life skips it (batch 352): the life's end, EndZombieLife (BBForceDead), runs the same clear.
    public void OnDeathBuffsReleased(Entity unit)
    {
        Sinks?.OnUnitDeathReleased(unit);
        RetireNonPersistentRecipientBuffs(unit);
        // kill_unit removes every buff of a dead pet (OnPetDeath, the Pet.lua clone families; CombatService.Kill calls it for EntityKind.Pet
        // right after this): a Yorick ghoul's Yorick*Logic buffs go the same way, and the YorickSummon* marker on Yorick ends from its own script.
        if (unit.Kind == EntityKind.YorickGhoul) OnPetDeath(unit);
    }

    public void OnDeath(Entity unit, uint killerNetId)
    {
        // BuffOnDeath runs while the dying unit still carries its buffs (BuildingBlocks engine).
        Sinks?.OnUnitDeath(unit, killerNetId);
        // A unit character module's binding goes with the unit (batch 159 step 2), after its BuffOnDeath has run.
        Spells.UnitScripts?.Unbind(unit);
        Spells.DestroyTargetedProjectiles(unit.NetId);
        // Death ends the cast the hero was winding up (SpellWindup): a new life starts with no uncancellable window.
        if (unit.UnitType == UnitType.Hero) SpellWindup.Retire(unit);
        // Death retires the old client route.  Keeping a path/anchor through
        // the dead interval lets the first post-respawn order reuse a base
        // route from the pre-death position.
        ResetMovementAnchor(unit, emit: false);
        // Batch 174 part 2: the Baron's team grant is Spells/ResistantSkin.luaobj's own BuffOnDeath now (it runs in
        // Sinks?.OnUnitDeath above, while the corpse still holds the buff), so no hand-written death call remains here.
        RetireChampionDeathWork(unit);
        // The buff clears (the script buffs, the audited lists, a pet's whole table) go after the kill credit: OnDeathBuffsReleased (batch 287).
        // A Pet.lua clone family (Shaco's clone, Yorick's revenant, and the ClonePet kind: LeBlanc's image,
        // Wukong's decoy, Mordekaiser's ghost) leaves no bookkeeping of its own behind: the pet AI host drops
        // the state of a dead pet on its next step. Its item-script copies go with it (batch 145 step 2): the
        // clone is never a holder again, and its aura is left to lapse on the recipients the way a living clone's
        // out-of-range aura does.
        if (unit.Kind is EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet)
            Spells.ItemScripts?.Clear(unit);
    }

    private void RetireChampionDeathWork(Entity unit)
    {
        Spells.OnUnitDeath(unit);
        Spells.OnHeroDeath(unit, ChampionDeathPhase.BeforeControllerBuffs);
        Spells.OnHeroDeath(unit, ChampionDeathPhase.AfterControllerBuffs);
    }

    private void RetireNonPersistentRecipientBuffs(Entity unit)
    {
        // The item-slow rows are named, not payload-tagged: the shipped Spells/Internal_*Slow.luaobj scripts
        // are empty and a script row carries nothing (batch 147, the ItemSlow marker chain).
        foreach (var buff in unit.Buffs.Where(buff =>
                     RecipientDeathMarks.Contains(buff.Name) ||
                     BuffLifecycle.IsItemSlowRow(buff.Name) ||
                     RecipientDeathControls.Contains(buff.Name)).ToArray())
            if (unit.Buffs.Any(active => ReferenceEquals(active, buff)))
                Buffs.RemoveBuff(unit, buff.Slot);
        if (unit.Kind == EntityKind.Hero && unit.UnitType == UnitType.Hero)
        {
            foreach (var buff in unit.Buffs.Where(buff => TemporaryHeroDeathBuffs.Contains(buff.Name)).ToArray())
                if (unit.Buffs.Any(active => ReferenceEquals(active, buff)))
                    Buffs.RemoveBuff(unit, buff.Slot);
            Spells.OnRecipientDeathCleanup(unit);
        }
    }

    public void OnPetDeath(Entity pet)
    {
        // kill_unit removes every buff from ordinary pets, after the kill credit
        // like every death's clear (batch 287).  This also clears Tibbers' owner timer;
        // the owner may be dead by this point, but the buff still owns the
        // client HUD state and must be retired through the lifecycle.
        foreach (var buff in pet.Buffs.ToArray()) Buffs.RemoveBuff(pet, buff.Slot);
        var owner = _world.Get(pet.OwnerNetId);
        if (owner is null) return;
        foreach (var buff in owner.Buffs.Where(buff => buff.Name == "InfernalGuardianTimer").ToArray())
            Buffs.RemoveBuff(owner, buff.Slot);
    }

    public void OnNeutralDeath(Entity neutral, uint killerNetId)
    {
        // NeutralMinionDeath's third argument is the team the script pays the death reward to,
        // so the report carries the killer's team (Team.Neutral when the kill has no owner).
        Jungle.OnDeath(neutral, Combat.CurrentTime, _world.Get(killerNetId)?.Team ?? Team.Neutral);
        // The Baron's ExaltedWithBaronNashor is Spells/ResistantSkin.luaobj's BuffOnDeath, run by the script engine's
        // death hook (MatchGameplayHooks.OnDeath -> Sinks.OnUnitDeath) before this report.
        // Jungle and neutral services own all current neutral death effects.
    }

    // ItemOnKill / ItemOnDeath / ItemOnAssist of the inventory copies' own scripts (batch 149 step 1). The hand-written
    // snowball, Sunfire and Bloodthirster counters these used to drive are gone; batch 149 step 5 took Ionic Spark's
    // 3178 stack counter with it (LightningRodApplicator.luaobj counts its own hits), so nothing is left in ItemPassives.
    public void OnItemKill(Entity? killer, Entity victim) => Spells.ItemScripts?.OnItemKill(killer, victim);

    public void OnItemDeath(Entity hero) => Spells.ItemScripts?.OnItemDeath(hero);

    public void OnItemAssist(Entity hero, Entity victim) => Spells.ItemScripts?.OnItemAssist(hero, victim);

    // BuffOnKill / CharOnKillUnit and BuffOnAssist / CharOnAssistUnit of the script engine (E06), after the rewards and the item callbacks.
    public void OnKill(Entity? killer, Entity victim)
    {
        if (killer is not null) Sinks?.OnUnitKilled(killer, victim);
    }

    public void OnAssist(Entity assistant, Entity victim) => Sinks?.OnUnitAssisted(assistant, victim);

    public void OnRespawn(Entity unit)
    {
        // A missile that was still flying to the corpse (SpellSystem.DestroyTargetedProjectiles keeps it past the death) belongs to
        // the old life: the same NetId lives again, so it must not follow or strike the new one. Reconstruction: the retail rule for a
        // unit that returns while a missile is aimed at it is not in the client.
        Spells.DestroyTargetedProjectiles(unit.NetId);
        ResetMovementAnchor(unit, emit: true);
        // CharOnResurrect / BuffOnResurrect of the script engine: a hero is alive again (Inhibitors also come through here).
        if (unit.UnitType == UnitType.Hero) Sinks?.OnHeroRespawn(unit);
        // Batch 204: the living hero's item copies start their ExecuteImmediately periodics again (BbItemModule.OnRespawn).
        if (unit.UnitType == UnitType.Hero) Spells.ItemScripts?.OnRespawn(unit);
    }

    /// <summary>A hero's player dropped out of a live match (<paramref name="connected"/> false) or came back (true): the script engine's disconnect hooks.</summary>
    public void OnHeroConnection(Entity hero, bool connected)
    {
        if (hero.UnitType != UnitType.Hero) return;
        if (connected) Sinks?.OnHeroReconnect(hero);
        else Sinks?.OnHeroDisconnect(hero);
    }

    private void ResetMovementAnchor(Entity unit, bool emit)
    {
        unit.Waypoints.Clear();
        unit.Target = null;
        unit.AttackTarget = null;
        unit.AttackMoveGoal = null;
        unit.HeroPursuit = null;
        unit.LastAttackStopPoint = null;
        unit.ForcedMove = null;
        unit.ForcedMoveGrace = 0.0;
        // Inhibitors also use OnRespawn; their building lifecycle must not
        // receive mobile-actor teleport or waypoint packets.
        var respawn = emit && unit.UnitType == UnitType.Hero;
        // A respawned hero lands with a teleport and a one-point path, whose install stops the hero and
        // seats it on the path's point, its lattice anchor, at once.
        IReadOnlyList<Position2> stop = respawn ? Spells.Movement.InstallStop(unit) : [unit.Position];
        unit.AnchorPath = [unit.Position];
        unit.LastAnchorTime = Combat.CurrentTime;
        if (!respawn) return;
        Spells.Movement.Teleported(unit);
        unit.TeleportId++;
        if (!Effects.IsReferenceReady(unit)) return;
        Effects.SendStopAttack(unit);
        Effects.SendTeleport(unit, unit.Position, unit.TeleportId,
            World.Clock.SyncId(), staging: null);
        Effects.SendWaypoints(unit, stop, World.Clock.SyncId());
    }

    public void OnLevelChanged(Entity hero)
    {
        // Batch 149 step 5: the Catalyst's (3010) and Rod of Ages' (3027) level-up heal is
        // Items/3010.luaobj's own UpdateSelfBuffActions (BBGetLevel against InstanceVars.OwnerLevel), so the
        // hand-written JungleBuffs.OnHeroLevelChanged that stood in for it is gone.
        Sinks?.OnLevelChanged(hero);
    }

    public void OnNeutralDamaged(Entity target, Entity? source)
    {
        if (source is not null) Neutral.OnTakeDamage(target, source);
    }

    // Batch 149 step 5: the item periodics this used to drive are the copies' own UpdateSelfBuffActions /
    // UpdateSelfBuffStats clocks now (BbItemModule.Poll). The sink entry stays for the combat tick's shape.
    public void OnItemPassiveTick(double now, double dt)
    {
    }

    public void OnPeriodicDamage(Entity target, Entity? source, double amount,
        DamageType type, DamageSourceKind? sourceKind)
    {
        // CombatService already called OnUnitDamaged with sourceIsPeriodic
        // while applying the hit.  This callback exists for the lifecycle
        // boundary, but invoking ItemDamage here would process Thornmail,
        // slow rolls, and recall cancellation a second time.
    }

    public void OnEndGame(Team winner) => _matchCompleted?.Invoke(winner);

    public void HaltAllAi()
    {
        // EndOfGame.luaobj's HaltAllAI, called from its ceremony before the camera pan starts
        // (EndOfGame.txt proto 0.0 pc 0080-0081). Clear ordinary orders, while scripted
        // displacement and effect lifetimes remain owned by their existing update phases.
        foreach (var unit in World.Entities.Values.Where(unit => unit.Alive).ToArray())
        {
            var moving = unit.Waypoints.Count > 0 || unit.Target is not null;
            var attacking = unit.AttackTarget is not null;
            var canceled = Combat.CancelPendingWindups(unit.NetId);
            unit.AttackTarget = null;
            unit.PendingCast = null;
            unit.AttackMoveGoal = null;
            unit.HeroPursuit = null;
            unit.HeroAutoAcquire = false;
            unit.Target = null;
            unit.Waypoints.Clear();
            if (unit.ForcedMove is not null || !Effects.IsReferenceReady(unit)) continue;
            if (attacking || canceled != 0) Effects.SendStopAttack(unit);
            // The stop's one-point path is installed on receipt: seated on its anchor at once.
            if (moving) Effects.SendWaypoints(unit, Spells.Movement.InstallStop(unit), World.Clock.SyncId());
        }
    }

    // ICombatBuffHooks and the remaining buff service views ---------------
    private sealed class BuffServiceBundle : IBuffServices
    {
        private readonly MatchGameplayHooks _owner;
        public BuffServiceBundle(MatchGameplayHooks owner)
        {
            _owner = owner;
            Combat = new CombatBuffView(owner);
            Casts = new CastBuffView();
            Champions = new ChampionBuffView(owner);
            Items = new ItemBuffView(owner);
        }
        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects => _owner.BuffEffects;
        public IItemBuffHooks Items { get; }
    }

    private sealed class CombatBuffView : ICombatBuffHooks
    {
        private readonly MatchGameplayHooks _owner;
        public CombatBuffView(MatchGameplayHooks owner) => _owner = owner;
        public bool IsPendingActionBlocked(Entity unit) => _owner.IsPendingActionBlocked(unit);
        public double HealingMultiplier(Entity unit) => _owner.Combat.HealingReceivedMultiplier(unit);
        public void InterruptBasicAttack(Entity unit) => _owner.Combat.InterruptBasicAttack(unit);
        public void InterruptBasicAttack(Entity unit, out bool cancelledWindup) =>
            cancelledWindup = _owner.Combat.InterruptBasicAttack(unit) > 0;
        public void ApplyPeriodicDamage(PeriodicDamageRequest request)
        {
            // Periodic damage follows the same authoritative hit pipeline as
            // every other hit.  In particular, retain the original source ID,
            // spell owner, source-kind, and periodic marker for mitigation,
            // sustain, replication, and item callbacks.
            _owner.Combat.ApplyHit(request.SourceNetId, request.TargetNetId,
                request.Amount, request.DamageType, request.SourceKind,
                sourceIsPeriodic: request.SourceIsPeriodic,
                spellOwnerNetId: request.SpellOwnerNetId == 0
                    ? null : request.SpellOwnerNetId, recapOrigin: request.RecapOrigin);
        }
        public void Kill(Entity unit, uint killerNetId = 0) => _owner.Combat.Kill(unit, killerNetId);
    }

    private sealed class CastBuffView : ICastBuffHooks
    {
        public void CancelPendingCast(Entity hero) => hero.PendingCast = null;
    }

    private sealed class ChampionBuffView : IChampionBuffHooks
    {
        private readonly MatchGameplayHooks _owner;
        public ChampionBuffView(MatchGameplayHooks owner) => _owner = owner;
        public bool SpellShieldPopped(Entity unit) => _owner.Sinks?.ShieldPopped(unit) ?? false;
        public void OnBuffTick(Entity unit, BuffState buff, double dt) =>
            // Batch 159 part 3: the team-size catch-up's timer is Spells/ChampionChampionDelta.luaobj's own periodic on its
            // live script instance (BbBuffRuntime.OnTick), so the hand-written tick that stood in for it is gone.
            _owner.Sinks?.OnBuffTick(unit, buff, dt);
        public void OnBuffRemoved(Entity unit, BuffState buff)
        {
            _owner.Spells.OnChampionBuffRemoved(unit, buff);
            _owner.Spells.OnSummonerBuffRemoved(unit, buff);
            // Batch 144: the item scripts' own buffs (GlacialShroud, ZhonyasRing, DoranT2Health ...) and the live ones
            // a mastery page added (Ardor) hear their removal. A router only acts on the buff instances it created.
            _owner.Spells.OnItemBuffRemoved(unit, buff);
            // Batch 159 part 1: a non-champion unit's character script hears its own buff's removal too (a turret's
            // Spells/TurretPreBonus.luaobj hands over to TurretBonus when its idle window runs out).
            _owner.Spells.OnUnitBuffRemoved(unit, buff);
            _owner.LoadoutHooks?.OnBuffRemoved(unit, buff);
        }
        public bool AllowBuffAdd(Entity unit, string name, BuffType type, ref double duration, Entity adder, int? maxStack) =>
            _owner.Sinks?.AllowBuffAdd(unit, adder, name, ref duration, (double)type, maxStack ?? 1) ?? true;
        public void OnAmmoTimer(Entity unit, BuffState buff) => _owner.Sinks?.OnAmmoTimer(unit, buff);
    }

    private sealed class ItemBuffView : IItemBuffHooks
    {
        private readonly MatchGameplayHooks _owner;
        public ItemBuffView(MatchGameplayHooks owner) => _owner = owner;
        public bool HasItem(Entity unit, int itemId) => Inventory.HasItem(unit.Inventory, itemId);
        public void SyncItemSlow(Entity target) => _owner.ItemPassives.SyncItemSlow(target);
    }
}
