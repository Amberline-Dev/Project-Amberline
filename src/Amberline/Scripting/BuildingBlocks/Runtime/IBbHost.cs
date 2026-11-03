namespace Amberline.Scripting.BuildingBlocks.Runtime;

// The host boundary of the BuildingBlocks interpreter. Nothing in this file simulates anything: the interpreter
// (BbInterpreter, BbBaseLibrary) is pure, and everything a script can touch in the world goes through the narrow
// interfaces below.
//
// Two kinds of calls cross the boundary:
//  * Host primitives: the plain Lua globals BuildingBlocksBase.luaobj calls with positional arguments (GetTeamID(unit),
//    SpellBuffCount(unit, name, caster), the stat accessors passed as a parameter, ...). They are typed methods, grouped
//    by the owner the native catalog assigns them to.
//  * Native blocks: the 134 BBxxx functions that are registered by the stock client instead of defined in Lua
//    as docs/scripting/native-catalog.md records. They take (vars, params, subBlocks) in retail and resolve their own
//    parameters, so they arrive as a BbCall routed to one IBbNativeHandler per BbNativeOwner.
//
// Units are opaque host handles (object); null means nil, or "the script's own unit" where retail's Lua function takes
// the unit as an optional argument and the base library omits it. Numbers are doubles, as in Lua.

/// <summary>Unit queries and identity: teams, kinds, skin.</summary>
public interface IBbUnitHost
{
    /// <summary>The unit's team as the TEAM_* number (100, 200, 300).</summary>
    double GetTeamID(object? unit);
    bool IsObjectAI(object? unit);
    bool IsObjectHero(object? unit);
    bool IsMelee(object? unit);
    bool IsTurretAI(object? unit);
    bool IsDead(object? unit);
    string? GetUnitSkinName(object? unit);
    void SetScaleSkinCoef(object? scale, object? unit);
}

/// <summary>Positions, distances and facing tests. Positions are <see cref="BbTable"/>s with x, y and z.</summary>
public interface IBbGeometryHost
{
    object? GetPosition(object? unit);
    /// <summary>Unit vector from attacker to target on the ground plane; <paramref name="flag"/> is the literal true BBKnockback passes.</summary>
    object? GetNormalizedPositionDelta(object? target, object? attacker, bool flag);
    void SetPosition(object? unit, object? position);
    object? Make3DPoint(object? x, object? y, object? z);
    double DistanceBetweenObjects(object? a, object? b);
    double DistanceBetweenObjectAndPoint(object? unit, object? point);
    double DistanceBetweenPoints(object? a, object? b);
    bool IsTargetInFrontOfMe(object? me, object? target);
    bool IsTargetBehindMe(object? me, object? target);
}

/// <summary>Buff queries and the two buff calls the base library makes itself.</summary>
public interface IBbBuffHost
{
    bool HasBuffOfType(object? unit, double? buffType);
    /// <summary>Stacks of <paramref name="buffName"/> on the unit; a null caster counts every caster.</summary>
    double SpellBuffCount(object? unit, string? buffName, object? caster);
    /// <summary>BBBreakSpellShields' positional call: (target, attacker, name, addType, maxStack, duration, vars).</summary>
    void SpellBuffAdd(object? target, object? attacker, string buffName, double addType, double maxStack, double duration, BbTable? vars);
    void SpellBuffRemove(object? target, string? buffName, object? attacker);
    /// <summary>
    /// <c>BBSpellBuffRemoveStacks</c> after the base library resolved the count: up to <paramref name="count"/> stacks of the buff held by
    /// <paramref name="attacker"/> end. The base library's own Lua is a loop of <see cref="SpellBuffRemove"/>, which is the default; a host that
    /// can end them together (one client update for the whole removal) overrides it.
    /// </summary>
    void SpellBuffRemoveStacks(object? target, string? buffName, object? attacker, double count)
    {
        for (var left = count; left > 0.0; left -= 1.0) SpellBuffRemove(target, buffName, attacker);
    }

    /// <summary>
    /// The game time the buff whose hooks are running ends, or null outside a buff hook (and for a host that runs no
    /// buffs). <c>BBExecutePeriodically</c> runs its due child only for a due time strictly inside this: a due block at
    /// the instant the buff ends does not run, the buff being gone before another update could catch it (batch 142
    /// part 2 — Tormented Soil's once-a-second tick would otherwise land a sixth time at the end of its 5 s zone).
    /// Reconstruction: the client's update order at the expiry instant is not in the shipped files.
    /// </summary>
    double? CurrentBuffEndsAt => null;
}

/// <summary>The damage context of the running hook.</summary>
public interface IBbCombatHost
{
    /// <summary>The DAMAGESOURCE_* value of the damage the current hook is handling.</summary>
    double GetSourceType();
}

/// <summary>Stat, status and level accessors; the accessor is chosen by name, see the Bb*Stat/Status enums.</summary>
public interface IBbStatHost
{
    double GetLevel(object? unit);
    double GetArmor(object? unit);
    double GetSpellBlock(object? unit);
    double GetTotalAttackDamage(object? unit);
    /// <summary>BBIncStat and BBIncPermanentStat: <c>Stat(delta, unit)</c>; a null unit is the call without the unit argument.</summary>
    void IncStat(BbIncStat stat, double delta, object? unit);
    double GetStat(BbGetStat stat, object? unit);
    bool GetStatus(BbGetStatus status, object? unit);
    void SetStatus(BbSetStatus status, object? unit, object? value);
}

/// <summary>Health, resource (PAR) and gold.</summary>
public interface IBbResourceHost
{
    bool HasPARType(object? unit, double? parType);
    object? GetResource(BbGetResource function, object? unit, double? parType);
    /// <summary><c>BBLuaGetGold(vars, params)</c>: retail's Lua function resolves its own parameters.</summary>
    double GetGold(BbCall call);
    void ReincarnateNonDeadHero(object? unit);
}

/// <summary>Spell slots, cooldowns, cast range and the cast context.</summary>
public interface IBbSpellHost
{
    object? GetSlotSpellInfo(BbSlotSpellInfo function, object? owner, object? slot, double? spellbook, double? slotType);
    void SetSlotSpellCooldownTime(object? owner, object? slot, double? spellbook, double? slotType, object? cooldown);
    void SetSpellCastRange(double? range);
    void SetCanCastWhileDisabled(bool canCast);
    object? GetCastInfo(BbCastInfo info, object? unit);
}

/// <summary>Game clock, randomness and chat output.</summary>
public interface IBbGameHost
{
    double GetTime();
    /// <summary>Lua's <c>math.random()</c> in [0, 1).</summary>
    double Random();
    void Say(object? unit, string? text);
    /// <summary>Lua's <c>_ALERT</c>.</summary>
    void Alert(string? text);
}

/// <summary>Orders and area effects the base library issues directly.</summary>
public interface IBbOrderHost
{
    void IssueOrder(object? whom, double? order, object? position, object? target);
    void DefUpdateAura(object? center, double? range, double? unitScan, string? buffName);
}

/// <summary>Executes the natives of one <see cref="BbNativeOwner"/>.</summary>
public interface IBbNativeHandler
{
    void Execute(BbCall call);
}

/// <summary>Everything the interpreter needs from the world.</summary>
public interface IBbHost : IBbUnitHost, IBbGeometryHost, IBbBuffHost, IBbCombatHost, IBbStatHost, IBbResourceHost,
    IBbSpellHost, IBbGameHost, IBbOrderHost
{
    /// <summary>The handler for the natives of <paramref name="owner"/>, or null when this host binds none.</summary>
    IBbNativeHandler? NativeHandler(BbNativeOwner owner);

    /// <summary>
    /// The body of a <c>BBExecutePeriodically</c> starts (<see cref="BbCall.ExecutePeriodicSubBlocks"/>). A host that
    /// gates the permanent stat and vision mutations of a stats sample on a clock of its own exempts the ones these
    /// brackets cover: their clock is the periodic's. Default: nothing (a host with no such gate).
    /// </summary>
    void EnterPeriodic() { }

    /// <summary>The body of a <c>BBExecutePeriodically</c> ends; the pair with <see cref="EnterPeriodic"/>.</summary>
    void ExitPeriodic() { }
}

/// <summary>
/// One executing block: its function, the shipped (immutable) params and sub-blocks, and the scope it runs in.
/// Base functions and native handlers both receive it. The helpers resolve params the way the Lua base library does:
/// <c>Value</c> is <c>params.X</c>, <c>Var</c> is <c>scope[params.X]</c>, <c>Table</c> is <c>GetTable(scope, params.X, create)</c>.
/// </summary>
public sealed class BbCall
{
    internal BbCall(BbInterpreter interpreter, BbScope scope, BbBlock block)
    {
        Interpreter = interpreter;
        Scope = scope;
        Block = block;
    }

    public BbInterpreter Interpreter { get; }
    public BbScope Scope { get; }
    public BbBlock Block { get; }
    public IBbHost Host => Interpreter.Host;
    public string Function => Block.Function;
    public IReadOnlyList<BbField> Params => Block.Params;
    public IReadOnlyList<BbBlock> SubBlocks => Block.SubBlocks;

    /// <summary>The raw shipped value of <c>params.key</c>, or null when absent (an explicit nil counts as absent).</summary>
    public BbValue? Param(string key) => Block.Param(key) is BbValue.Nil ? null : Block.Param(key);

    public bool Has(string key) => Param(key) is not null;

    /// <summary><c>params.key</c> as a runtime value: constants resolve to their number, undefined globals to nil.</summary>
    public object? Value(string key) => BbRuntime.ToRuntime(Block.Param(key));

    /// <summary><c>scope[params.key]</c>: the variable the param names (nil when the param or the variable is absent).</summary>
    public object? Var(string key) => Scope.Vars[Value(key)];

    /// <summary><c>GetTable(scope, params.key, create)</c>: the named variable table, or the root scope when none is named.</summary>
    public BbTable Table(string key, bool create) => BbBaseLibrary.GetTable(Scope, Value(key), create);

    /// <summary>Runs this block's sub-blocks in the same scope (retail's natives call ExecuteBuildingBlocks the same way).</summary>
    public void ExecuteSubBlocks() => Interpreter.Execute(Block.SubBlocks, Scope);

    /// <summary>
    /// Runs this block's sub-blocks as the body of a <c>BBExecutePeriodically</c>: the host is told the body is running
    /// (<see cref="IBbHost.EnterPeriodic"/>) so the mutations inside it follow this periodic's own clock rather than the
    /// clock a stats sample otherwise gates them on (see <c>BbHookFrame.MutationSuppressed</c>). The flag is set for the
    /// body alone and cleared even when the body raises.
    /// </summary>
    public void ExecutePeriodicSubBlocks()
    {
        Host.EnterPeriodic();
        try { ExecuteSubBlocks(); }
        finally { Host.ExitPeriodic(); }
    }

    /// <summary>A param holding the name of a host accessor (<c>Stat = IncFlatArmorMod</c>) as its enum.</summary>
    public T Accessor<T>(string key) where T : struct, Enum =>
        Block.Param(key) is BbValue.Reference reference && BbNames<T>.TryParse(reference.Name, out var value)
            ? value
            : throw new BbRuntimeException($"{Function}: param '{key}' is not a {typeof(T).Name} accessor");
}

/// <summary>
/// Which host area executes a native. The grouping follows the native catalog's best-guess owner labels
/// as docs/scripting/native-catalog.md records, folded to the host areas the simulation has; it is a planning label, not retail evidence.
/// </summary>
public enum BbNativeOwner
{
    /// <summary>Asset preloads (BBPreloadSpell, BBPreloadParticle, BBPreloadCharacter): no runtime effect.</summary>
    Preload,
    /// <summary>Buff lifecycle and buff variables.</summary>
    Buffs,
    /// <summary>Visual output: effects, animation and facing, missiles, character data, presentation.</summary>
    Effects,
    /// <summary>Damage, shields, crowd control, assists, forced death.</summary>
    Combat,
    /// <summary>Health, resource and economy.</summary>
    Resources,
    /// <summary>Dashes, teleports, forced movement.</summary>
    Movement,
    /// <summary>Entity and geometry reads, visibility.</summary>
    Units,
    /// <summary>Area and target iteration (the ForEach family).</summary>
    Queries,
    /// <summary>Spell slots, child casts, auto-attack control.</summary>
    Spells,
    /// <summary>Minion, pet and clone spawns.</summary>
    Spawns,
    /// <summary>Natives that only touch interpreter scope (BBSetUnit, BBSetTriggerUnit, BBInvalidateUnit).</summary>
    Script,
}

/// <summary>The native BBxxx functions the shipped scripts call, and which host area executes them.</summary>
public static class BbNativeCatalog
{
    private static readonly Dictionary<string, BbNativeOwner> Owners = new(StringComparer.Ordinal)
    {
        // Buffs
        ["BBDispellNegativeBuffs"] = BbNativeOwner.Buffs, ["BBGetBuffRemainingDuration"] = BbNativeOwner.Buffs,
        ["BBGetBuffStartTime"] = BbNativeOwner.Buffs, ["BBGetDamagingBuffName"] = BbNativeOwner.Buffs,
        ["BBSetBuffCasterUnit"] = BbNativeOwner.Buffs, ["BBSetBuffToolTipVar"] = BbNativeOwner.Buffs,
        ["BBSpellBuffAdd"] = BbNativeOwner.Buffs, ["BBSpellBuffClear"] = BbNativeOwner.Buffs,
        ["BBSpellBuffRemove"] = BbNativeOwner.Buffs, ["BBSpellBuffRemoveCurrent"] = BbNativeOwner.Buffs,
        ["BBSpellBuffRemoveType"] = BbNativeOwner.Buffs,
        // Preload
        ["BBPreloadCharacter"] = BbNativeOwner.Preload, ["BBPreloadParticle"] = BbNativeOwner.Preload,
        ["BBPreloadSpell"] = BbNativeOwner.Preload,
        // Effects
        ["BBClearOverrideAnimation"] = BbNativeOwner.Effects, ["BBClearPARColorOverride"] = BbNativeOwner.Effects,
        ["BBDestroyMissile"] = BbNativeOwner.Effects, ["BBDestroyMissileForTarget"] = BbNativeOwner.Effects,
        ["BBFaceDirection"] = BbNativeOwner.Effects, ["BBFadeInColorFadeEffect"] = BbNativeOwner.Effects,
        ["BBFadeOutColorFadeEffect"] = BbNativeOwner.Effects, ["BBGetMissilePosFromID"] = BbNativeOwner.Effects,
        ["BBIncScaleSkinCoef"] = BbNativeOwner.Effects, ["BBOverrideAnimation"] = BbNativeOwner.Effects,
        ["BBPauseAnimation"] = BbNativeOwner.Effects, ["BBPlayAnimation"] = BbNativeOwner.Effects,
        ["BBPopAllCharacterData"] = BbNativeOwner.Effects, ["BBPopCharacterData"] = BbNativeOwner.Effects,
        ["BBPopCharacterFade"] = BbNativeOwner.Effects, ["BBPushCharacterData"] = BbNativeOwner.Effects,
        ["BBPushCharacterFade"] = BbNativeOwner.Effects, ["BBResetVoiceOverride"] = BbNativeOwner.Effects,
        ["BBSetCameraPosition"] = BbNativeOwner.Effects, ["BBSetPARColorOverride"] = BbNativeOwner.Effects,
        ["BBSetVoiceOverride"] = BbNativeOwner.Effects, ["BBShowHealthBar"] = BbNativeOwner.Effects,
        ["BBSpellEffectCreate"] = BbNativeOwner.Effects, ["BBSpellEffectRemove"] = BbNativeOwner.Effects,
        ["BBStopCurrentOverrideAnimation"] = BbNativeOwner.Effects, ["BBUnlockAnimation"] = BbNativeOwner.Effects,
        // Combat
        ["BBApplyAssistMarker"] = BbNativeOwner.Combat, ["BBApplyDamage"] = BbNativeOwner.Combat,
        ["BBApplyFear"] = BbNativeOwner.Combat, ["BBApplyNearSight"] = BbNativeOwner.Combat,
        ["BBApplyRoot"] = BbNativeOwner.Combat, ["BBApplySilence"] = BbNativeOwner.Combat,
        ["BBApplyStun"] = BbNativeOwner.Combat, ["BBApplyTaunt"] = BbNativeOwner.Combat,
        ["BBForceDead"] = BbNativeOwner.Combat, ["BBIncreaseShield"] = BbNativeOwner.Combat,
        ["BBModifyShield"] = BbNativeOwner.Combat, ["BBReduceShield"] = BbNativeOwner.Combat,
        ["BBRemoveShield"] = BbNativeOwner.Combat, ["BBSetDodgePiercing"] = BbNativeOwner.Combat,
        // Queries
        ["BBForEachChampion"] = BbNativeOwner.Queries, ["BBForEachPointAroundCircle"] = BbNativeOwner.Queries,
        ["BBForEachPointOnLine"] = BbNativeOwner.Queries, ["BBForEachUnitInTargetArea"] = BbNativeOwner.Queries,
        ["BBForEachUnitInTargetAreaAddBuff"] = BbNativeOwner.Queries,
        ["BBForEachUnitInTargetAreaRandom"] = BbNativeOwner.Queries,
        ["BBForEachUnitInTargetRectangle"] = BbNativeOwner.Queries,
        ["BBForNClosestUnitsInTargetArea"] = BbNativeOwner.Queries,
        ["BBForNClosestVisibleUnitsInTargetArea"] = BbNativeOwner.Queries,
        ["BBGetNumberOfHeroesOnTeam"] = BbNativeOwner.Queries, ["BBStartTrackingCollisions"] = BbNativeOwner.Queries,
        // Spells
        ["BBCancelAutoAttack"] = BbNativeOwner.Spells, ["BBGetCastRange"] = BbNativeOwner.Spells,
        ["BBGetCastSpellDragEndPos"] = BbNativeOwner.Spells, ["BBGetCastSpellTargetPos"] = BbNativeOwner.Spells,
        ["BBGetPARMultiplicativeCostInc"] = BbNativeOwner.Spells, ["BBIncSpellLevel"] = BbNativeOwner.Spells,
        ["BBOverrideAutoAttack"] = BbNativeOwner.Spells, ["BBRemoveOverrideAutoAttack"] = BbNativeOwner.Spells,
        ["BBSealSpellSlot"] = BbNativeOwner.Spells, ["BBSetPARCostInc"] = BbNativeOwner.Spells,
        ["BBSetPARMultiplicativeCostInc"] = BbNativeOwner.Spells,
        ["BBSetSlotSpellCooldownTimeVer2"] = BbNativeOwner.Spells, ["BBSetSpell"] = BbNativeOwner.Spells,
        ["BBSetSpellOffsetTarget"] = BbNativeOwner.Spells, ["BBSetSpellToolTipVar"] = BbNativeOwner.Spells,
        ["BBSetTargetingType"] = BbNativeOwner.Spells, ["BBSetUseSlotSpellCooldownTime"] = BbNativeOwner.Spells,
        ["BBSkipNextAutoAttack"] = BbNativeOwner.Spells, ["BBSpellCast"] = BbNativeOwner.Spells,
        ["BBStopChanneling"] = BbNativeOwner.Spells,
        // Units
        ["BBAddPosPerceptionBubble"] = BbNativeOwner.Units, ["BBAddUnitPerceptionBubble"] = BbNativeOwner.Units,
        ["BBCanSeeTarget"] = BbNativeOwner.Units, ["BBGetChampionBySkinName"] = BbNativeOwner.Units,
        ["BBGetGameTime"] = BbNativeOwner.Units, ["BBGetGroundHeight"] = BbNativeOwner.Units,
        ["BBGetIsZombie"] = BbNativeOwner.Units, ["BBGetNearestPassablePosition"] = BbNativeOwner.Units,
        ["BBGetOffsetAngle"] = BbNativeOwner.Units, ["BBGetPetOwner"] = BbNativeOwner.Units,
        ["BBGetPointByUnitFacingOffset"] = BbNativeOwner.Units,
        ["BBGetRandomPointInAreaPosition"] = BbNativeOwner.Units,
        ["BBGetRandomPointInAreaUnit"] = BbNativeOwner.Units, ["BBGetSkinID"] = BbNativeOwner.Units,
        ["BBGetUnitPosition"] = BbNativeOwner.Units, ["BBIsInBrush"] = BbNativeOwner.Units,
        ["BBIsPathable"] = BbNativeOwner.Units, ["BBLinkVisibility"] = BbNativeOwner.Units,
        ["BBRemoveLinkVisibility"] = BbNativeOwner.Units, ["BBRemovePerceptionBubble"] = BbNativeOwner.Units,
        ["BBSetNotTargetableToTeam"] = BbNativeOwner.Units, ["BBTestUnitAttributeFlag"] = BbNativeOwner.Units,
        // Resources
        ["BBIncExp"] = BbNativeOwner.Resources, ["BBIncFlatPARPoolMod"] = BbNativeOwner.Resources,
        ["BBIncFlatPARRegenMod"] = BbNativeOwner.Resources, ["BBIncGold"] = BbNativeOwner.Resources,
        ["BBIncHealth"] = BbNativeOwner.Resources, ["BBIncMaxHealth"] = BbNativeOwner.Resources,
        ["BBIncPAR"] = BbNativeOwner.Resources, ["BBIncPercentPARPoolMod"] = BbNativeOwner.Resources,
        ["BBIncPercentPARRegenMod"] = BbNativeOwner.Resources,
        ["BBIncPermanentFlatPARPoolMod"] = BbNativeOwner.Resources,
        ["BBIncPermanentFlatPARRegenMod"] = BbNativeOwner.Resources, ["BBRedirectGold"] = BbNativeOwner.Resources,
        // Spawns
        ["BBCloneUnitPet"] = BbNativeOwner.Spawns, ["BBSpawnMinion"] = BbNativeOwner.Spawns,
        ["BBSpawnPet"] = BbNativeOwner.Spawns,
        // Movement
        ["BBForceRefreshPath"] = BbNativeOwner.Movement, ["BBModifyPosition"] = BbNativeOwner.Movement,
        ["BBMove"] = BbNativeOwner.Movement, ["BBMoveAway"] = BbNativeOwner.Movement,
        ["BBMoveToUnit"] = BbNativeOwner.Movement, ["BBStopMove"] = BbNativeOwner.Movement,
        ["BBStopMoveBlock"] = BbNativeOwner.Movement, ["BBTeleportToKeyLocation"] = BbNativeOwner.Movement,
        ["BBTeleportToPosition"] = BbNativeOwner.Movement,
        // Script
        ["BBInvalidateUnit"] = BbNativeOwner.Script, ["BBSetTriggerUnit"] = BbNativeOwner.Script,
        ["BBSetUnit"] = BbNativeOwner.Script,
    };

    /// <summary>The owner of a native block function, or null when the name is not a native (or base) function of the shipped corpus.</summary>
    public static BbNativeOwner? OwnerOf(string function) => Owners.TryGetValue(function, out var owner) ? owner : null;

    public static IReadOnlyCollection<string> Names => Owners.Keys;
}

// Host accessors. The shipped scripts pass these as a parameter value (Stat = IncFlatArmorMod) and the base library
// calls them with positional arguments; the interpreter hands the host the enum instead of a function. The lists are the
// functions the stock client registers with the shared handlers named in each summary
// so they hold more names than the shipped scripts use.

/// <summary>Stat accessors BBIncStat and BBIncPermanentStat call as <c>Stat(delta, unit)</c>. One shared handler serves all of them in the stock client.</summary>
public enum BbIncStat
{
    IncMoveSpeedFloorMod,
    IncFlatDodgeMod,
    IncFlatMissChanceMod,
    IncPercentArmorMod,
    IncPercentArmorPenetrationMod,
    IncPercentMagicPenetrationMod,
    IncPercentMultiplicativeAttackSpeedMod,
    IncPercentMultiplicativeMovementSpeedMod,
    IncFlatGoldPer10Mod,
    IncPermanentFlatGoldPer10Mod,
    IncBaseAttackDamage,
    IncPermanentBaseAttackDamage,
    IncFlatArmorMod,
    IncPermanentFlatArmorMod,
    IncFlatArmorPenetrationMod,
    IncPermanentFlatArmorPenetrationMod,
    IncFlatMagicPenetrationMod,
    IncPermanentFlatMagicPenetrationMod,
    IncFlatSpellBlockMod,
    IncPermanentFlatSpellBlockMod,
    IncFlatAttackRangeMod,
    IncPermanentFlatAttackRangeMod,
    IncFlatCastRangeMod,
    IncPermanentFlatCastRangeMod,
    IncFlatCooldownMod,
    IncPermanentFlatCooldownMod,
    IncFlatCritChanceMod,
    IncPermanentFlatCritChanceMod,
    IncFlatCritDamageMod,
    IncPermanentFlatCritDamageMod,
    IncFlatHPPoolMod,
    IncPermanentFlatHPPoolMod,
    IncFlatHPRegenMod,
    IncPermanentFlatHPRegenMod,
    IncFlatMagicDamageMod,
    IncPermanentFlatMagicDamageMod,
    IncFlatMagicReduction,
    IncPermanentFlatMagicReduction,
    IncFlatMovementSpeedMod,
    IncPermanentFlatMovementSpeedMod,
    IncFlatPhysicalDamageMod,
    IncPermanentFlatPhysicalDamageMod,
    IncFlatPhysicalReduction,
    IncPermanentFlatPhysicalReduction,
    IncFlatBubbleRadiusMod,
    IncPermanentFlatBubbleRadiusMod,
    IncPercentHardnessMod,
    IncPermanentPercentHardnessMod,
    IncPercentSpellBlockMod,
    IncPermanentPercentSpellBlockMod,
    IncPercentAttackRangeMod,
    IncPermanentPercentAttackRangeMod,
    IncPercentAttackSpeedMod,
    IncPermanentPercentAttackSpeedMod,
    IncPercentCastRangeMod,
    IncPermanentPercentCastRangeMod,
    IncPercentCooldownMod,
    IncPermanentPercentCooldownMod,
    IncPercentCritDamageMod,
    IncPermanentPercentCritDamageMod,
    IncPercentEXPBonus,
    IncPermanentPercentEXPBonus,
    IncPercentHPPoolMod,
    IncPermanentPercentHPPoolMod,
    IncPercentHPRegenMod,
    IncPermanentPercentHPRegenMod,
    IncPercentHealingAmountMod,
    IncPermanentPercentHealingAmountMod,
    IncPercentLifeStealMod,
    IncPermanentPercentLifeStealMod,
    IncPercentSpellVampMod,
    IncPermanentPercentSpellVampMod,
    IncPercentMagicDamageMod,
    IncPermanentPercentMagicDamageMod,
    IncPercentMagicReduction,
    IncPermanentPercentMagicReduction,
    IncPercentMovementSpeedMod,
    IncPermanentPercentMovementSpeedMod,
    IncPercentPhysicalDamageMod,
    IncPermanentPercentPhysicalDamageMod,
    IncPercentPhysicalReduction,
    IncPermanentPercentPhysicalReduction,
    IncPercentRespawnTimeMod,
    IncPermanentPercentRespawnTimeMod,
    IncPercentGoldLostOnDeathMod,
    IncPermanentPercentGoldLostOnDeathMod,
    IncPercentBubbleRadiusMod,
    IncPermanentPercentBubbleRadiusMod,
    IncAcquisitionRangeMod,
    IncPermanentAcquisitionRangeMod,
}

/// <summary>Status setters BBSetStatus calls as <c>Status(unit, value)</c>.</summary>
public enum BbSetStatus
{
    SetDisableAmbientGold,
    SetInvulnerable,
    SetTargetable,
    SetPhysicalImmune,
    SetMagicImmune,
    SetRooted,
    SetSilenced,
    SetStealthed,
    SetRevealSpecificUnit,
    SetStunned,
    SetPacified,
    SetNetted,
    SetDisarmed,
    SetTaunted,
    SetCharmed,
    SetFeared,
    SetSleep,
    SetNearSight,
    SetNoRender,
    SetForceRenderParticles,
    SetGhosted,
    SetSuppressed,
    SetGhostProof,
    SetSuppressCallForHelp,
    SetCallForHelpSuppresser,
    SetBrushVisibilityFake,
    SetIgnoreCallForHelp,
    SetCanAttack,
    SetCanCast,
    SetCanMove,
    SetBarracksEnabled,
}

/// <summary>Boolean unit getters BBGetStatus calls as <c>Status(unit)</c>.</summary>
public enum BbGetStatus
{
    GetInvulnerable,
    GetPhysicalImmune,
    GetTargetable,
    GetMagicImmune,
    GetNoRender,
    GetBrushVisibilityFake,
    GetForceRenderParticles,
    GetStealthed,
    GetRevealSpecificUnit,
    GetTaunted,
    GetCharmed,
    GetSuppressed,
    GetFeared,
    GetDisableAmbientGold,
    GetSleep,
    GetNearSight,
    GetGhosted,
    GetGhostProof,
    GetCanAttack,
    GetCanCast,
    IsAutoCastOn,
    GetCanMove,
    IsBaseAI,
    IsHeroAI,
    IsMelee,
    IsRanged,
    IsMoving,
    IsObjectHero,
    IsObjectAI,
    IsTurretAI,
    IsDampener,
}

/// <summary>Numeric unit getters BBGetStat calls as <c>Stat(unit)</c>.</summary>
public enum BbGetStat
{
    GetArmor,
    GetSpellBlock,
    GetDodge,
    GetMissChance,
    GetAttackSpeedMod,
    GetMovementSpeed,
    GetLevel,
    GetTotalAttackDamage,
    GetFlatGoldPer10Mod,
    GetBaseAttackDamage,
    GetFlatArmorMod,
    GetFlatArmorPenetrationMod,
    GetFlatMagicPenetrationMod,
    GetFlatSpellBlockMod,
    GetFlatAttackRangeMod,
    GetFlatCastRangeMod,
    GetFlatCooldownMod,
    GetFlatCritChanceMod,
    GetFlatCritDamageMod,
    GetFlatHPPoolMod,
    GetFlatHPRegenMod,
    GetFlatMagicDamageMod,
    GetFlatMagicReduction,
    GetFlatMovementSpeedMod,
    GetFlatPhysicalDamageMod,
    GetFlatPhysicalReduction,
    GetFlatBubbleRadiusMod,
    GetPercentHardnessMod,
    GetPercentSpellBlockMod,
    GetPercentAttackRangeMod,
    GetPercentAttackSpeedMod,
    GetPercentCastRangeMod,
    GetPercentCooldownMod,
    GetPercentCritDamageMod,
    GetPercentEXPBonus,
    GetPercentHPPoolMod,
    GetPercentHPRegenMod,
    GetPercentHealingAmountMod,
    GetPercentLifeStealMod,
    GetPercentSpellVampMod,
    GetPercentMagicDamageMod,
    GetPercentMagicReduction,
    GetPercentMovementSpeedMod,
    GetPercentPhysicalDamageMod,
    GetPercentPhysicalReduction,
    GetPercentRespawnTimeMod,
    GetPercentGoldLostOnDeathMod,
    GetPercentBubbleRadiusMod,
    GetAcquisitionRangeMod,
}

/// <summary>Health and resource getters BBGetPAROrHealth calls as <c>Function(unit, parType)</c>.</summary>
public enum BbGetResource
{
    GetHealth,
    GetHealthPercent,
    GetPAR,
    GetPARPercent,
    GetMaxHealth,
    GetMaxPAR,
}

/// <summary>Slot getters BBGetSlotSpellInfo calls as <c>Function(owner, slot, spellbook, slotType)</c>.</summary>
public enum BbSlotSpellInfo
{
    GetSlotSpellLevel,
    GetSlotSpellName,
    GetSlotSpellCooldownTime,
}

/// <summary>Spell and damage context getters BBGetCastInfo calls as <c>Info(unit)</c>.</summary>
public enum BbCastInfo
{
    GetCastSpellLevelPlusOne,
    GetCastSpellTargetsHitPlusOne,
    GetCasterID,
    GetSpellSlot,
    GetTotalTeamMinionsSpawned,
    GetDamageType,
    GetSourceType,
    GetHitResult,
    GetPARCost,
    GetTime,
    GetDamageAmount,
    GetPercentageOfAttack,
    GetCastSpellTargetPos,
    GetCastSpellLuaInfo,
    GetTutorialPlayer,
    GetAttacker,
    GetIsAttackOverride,
    GetSpellName,
}
