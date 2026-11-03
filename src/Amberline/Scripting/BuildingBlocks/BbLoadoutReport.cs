using System.Collections.Concurrent;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// One buff add a loadout script asked the host for: the buff's name, its duration and a copy of the
/// <c>NextBuffVars</c> table the add passes along (the client gives the buff a copy).
/// The add type, the buff type and the mitigate flag travel with it so a live install honours what the script
/// asked for rather than a hard-coded reading of one buff.
/// </summary>
public sealed record BbLoadoutBuffAdd(string Name, double Duration, BbTable Vars,
    double AddType = 0.0, double BuffType = 0.0, bool CanMitigateDuration = false)
{
    /// <summary>One number of the passed-along variables, 0 when the script did not set it.</summary>
    public double Number(string key) => BbRuntime.NumberOrNull(Vars[key]) ?? 0.0;
}

/// <summary>The synthetic hit a damage hook is probed with.</summary>
public enum BbDamageProbe
{
    /// <summary><c>DamageType = TRUE_DAMAGE</c> from the hero: no mastery hook is meant to change it.</summary>
    TrueDamage,
    /// <summary><c>DamageType = PHYSICAL_DAMAGE</c> from the hero.</summary>
    Physical,
    /// <summary><c>DamageType = MAGIC_DAMAGE</c> from the hero.</summary>
    Magic,
    /// <summary>Non-true damage taken from an AI, non-turret, non-hero attacker.</summary>
    AiNonHero,
}

/// <summary>
/// What one damage hook of a talent script does to one synthetic hit of 1024, reported as the two pure shapes a hook
/// can have. The two readings are two views of that one hit, so only the axis of the hook's own shape is its number:
/// a hook that only multiplies leaves <see cref="Multiplier"/> its factor exactly (Havoc's <c>1.04</c>, Tenacity's
/// <c>0.96</c>), and a hook that only subtracts a flat block leaves <see cref="Block"/> that block exactly (Harden
/// Skin's <c>DamageBlock</c>, Defensive's <c>SmallDamageAmount</c>). A hook that does neither is <c>(1, 0)</c>.
/// </summary>
/// <param name="Multiplier">
/// The hit of 1024 the hook leaves, divided by 1024. The probe amount is a power of two, so a hook that only
/// multiplies reports its factor exactly (scaling a double by 2^k and back is exact): Havoc's <c>1.04</c>,
/// Tenacity's <c>0.96</c>.
/// </param>
/// <param name="Block">
/// What the hook takes off a hit of 1024. A hook that only subtracts a flat block reports that block exactly:
/// Harden Skin's <c>DamageBlock</c>, Defensive's <c>SmallDamageAmount</c>. The probe amount is large enough that
/// the shipped <c>DamageAmount &lt; 0</c> clamps never fire, so the block is not swallowed.
/// </param>
public readonly record struct BbDamageEffect(double Multiplier, double Block);

/// <summary>What one talent script asked the probe world to do, at its allocated rank.</summary>
public sealed class BbTalentReport
{
    internal BbTalentReport(BbLoadoutOwner owner, BbLoadoutBinding binding, BbLoadoutEvalHost host)
    {
        _owner = owner;
        Binding = binding;
        _host = host;
    }

    private readonly BbLoadoutOwner _owner;
    private readonly BbLoadoutEvalHost _host;

    public int Id => Binding.Id;
    public int Rank => Binding.Rank;

    /// <summary>The talent's own instance variables (Harden Skin's <c>DamageBlock</c>, Defensive's block).</summary>
    public BbLoadoutBinding Binding { get; }

    /// <summary>Every <c>BBIncStat</c> the talent's own hooks made, by accessor name (<c>IncFlatPhysicalDamageMod</c>).</summary>
    public IReadOnlyDictionary<string, double> Stats => _host.Stats;

    /// <summary>Every <c>BBInc*PAR*</c> the talent's own hooks made, by native name.</summary>
    public IReadOnlyDictionary<string, double> ResourceMods => _host.ResourceMods;

    /// <summary>Every buff the talent's own hooks asked for, in the order the hooks ran.</summary>
    public IReadOnlyList<BbLoadoutBuffAdd> Buffs => _host.Buffs;

    /// <summary>
    /// One number the talent's <c>SetVarsByLevel</c> wrote into the hero's <c>AvatarVars</c>, the way the summoner
    /// feeder scripts publish their bonuses (<c>SummonerCooldownBonus</c>, <c>RevivePreservationBonus</c> ...). The
    /// table reads nil for an unwritten key, which this report answers as 0. Each probe of
    /// <see cref="BbLoadoutReport.Talents"/> owns its own loadout, so one feeder's report holds exactly that feeder's
    /// writes and never another's.
    /// </summary>
    public double Avatar(string key) => BbRuntime.NumberOrNull(_owner.AvatarVars[key]) ?? 0.0;

    /// <summary>The script has that character hook (so a probe is worth running).</summary>
    public bool HasHook(string hook) => Binding.Script.Event(hook) is not null;

    /// <summary>
    /// Runs the talent's <paramref name="hook"/> against one synthetic hit of 1024 and reports what the hook did to
    /// it. The scope is fresh for the probe (so <c>DamageAmount</c> starts at the probe amount) but keeps the
    /// binding's <c>InstanceVars</c> and <c>TalentLevel</c>, which is how the live owner runs the same hook.
    /// </summary>
    public BbDamageEffect Damage(string hook, BbDamageProbe probe)
    {
        if (!HasHook(hook)) return new BbDamageEffect(1.0, 0.0);
        const double Hit = 1024.0;
        var attacker = probe == BbDamageProbe.AiNonHero ? BbLoadoutEvalHost.Minion : BbLoadoutEvalHost.Hero;
        var scope = _owner.NewScope(Binding, BbLoadoutEvalHost.Hero, attacker, BbLoadoutEvalHost.Minion);
        scope.Vars["DamageType"] = (double)(probe switch
        {
            BbDamageProbe.TrueDamage => BbDamageType.TRUE_DAMAGE,
            BbDamageProbe.Magic => BbDamageType.MAGIC_DAMAGE,
            _ => BbDamageType.PHYSICAL_DAMAGE,
        });
        scope.Vars["DamageAmount"] = Hit;
        new BbInterpreter(_host).ExecuteHook(Binding.Script, hook, scope);
        var after = BbRuntime.NumberOrNull(scope.Vars["DamageAmount"]) ?? Hit;
        return new BbDamageEffect(after / Hit, Hit - after);
    }

    /// <summary>
    /// The buffs <paramref name="hook"/> asked for, run on its own probe world so they are not mixed into the self-buff
    /// hooks' capture (Nimbleness is added by <c>CharOnDodge</c>, which the self-buff run never touches).
    /// </summary>
    /// <param name="heroBuffCount">The wearer's real buff count by name, when the run polls a live hero: the hook's own
    /// <c>BBIfHasBuff</c>/<c>BBIfNotHasBuff</c> on the Owner then read the hero (Talents/111.luaobj gates Ardor's add on it).</param>
    public IReadOnlyList<BbLoadoutBuffAdd> HookBuffs(string hook, Func<string?, double>? heroBuffCount = null)
    {
        var host = new BbLoadoutEvalHost(_host.Level) { HeroBuffCount = heroBuffCount };
        var scope = _owner.NewScope(Binding, BbLoadoutEvalHost.Hero, BbLoadoutEvalHost.Hero, BbLoadoutEvalHost.Hero);
        new BbInterpreter(host).ExecuteHook(Binding.Script, hook, scope);
        return host.Buffs;
    }

    /// <summary>
    /// Runs <paramref name="hook"/> on a real hit and returns the amount the script left. This is the live form of
    /// <see cref="Damage"/>: the same hook, the same binding (its <c>InstanceVars</c> and <c>TalentLevel</c>), but the
    /// hit the simulation is actually resolving, so <c>DamageAmount</c> is written back for the pipeline rather than
    /// read as a factor. <paramref name="attacker"/> carries the attacker's kind flags, which is all
    /// <c>CO_IS_TYPE_*</c> of the shipped hooks reads (Defensive's AI / turret / hero branches).
    /// </summary>
    internal double ModifyHit(string hook, double amount, BbDamageType type, BbProbeUnit attacker)
    {
        if (!HasHook(hook)) return amount;
        var host = new BbLoadoutEvalHost(_host.Level);
        var scope = _owner.NewScope(Binding, BbLoadoutEvalHost.Hero, attacker, BbLoadoutEvalHost.Minion);
        scope.Vars["DamageType"] = (double)type;
        scope.Vars["DamageAmount"] = amount;
        new BbInterpreter(host).ExecuteHook(Binding.Script, hook, scope);
        // The clamp mirrors BbHookRouter.RunPreDamage: the shipped hooks clamp their own DamageAmount, and the combat
        // pipeline refuses a negative hit outright.
        return Math.Max(0.0, BbRuntime.NumberOrNull(scope.Vars["DamageAmount"]) ?? amount);
    }
}

/// <summary>
/// Runs the shipped loadout scripts against a probe world and reports what they asked for. Nothing here simulates
/// anything: the units are named handles with fixed answers (every stat and every resource reads 1, no buff is
/// present), and the only thing that leaves the run is what the scripts wrote or asked for — the same record a
/// live host would act on. Masteries are reported from their <c>DATA/Talents/&lt;id&gt;.luaobj</c> this way; the
/// numbers are the scripts' own, so no mastery formula is reimplemented in C#.
/// </summary>
public static class BbLoadoutReport
{
    private static readonly ConcurrentDictionary<string, BbScript?> Scripts = new(StringComparer.Ordinal);

    /// <summary>
    /// Every allocated talent, run at its rank through <see cref="BbLoadoutOwner"/>: <c>SetVarsByLevel</c> first
    /// (it fills the binding's <c>InstanceVars</c> and the hero's <c>AvatarVars</c>), then the two self-buff hooks
    /// that add the stats and the marker buffs. The character hooks are left to
    /// <see cref="BbTalentReport.Damage"/>, which probes each one on demand.
    /// </summary>
    public static IReadOnlyDictionary<int, BbTalentReport> Talents(IReadOnlyDictionary<int, int> allocations,
        int championLevel)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        if (championLevel is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(championLevel), "Champion level must be 1..18.");
        var reports = new Dictionary<int, BbTalentReport>();
        foreach (var (id, rank) in allocations)
        {
            if (rank <= 0) continue; // a rank of 0 allocates nothing: 135's BBElse would add the rank-1 marker buff
            // A report owns its own loadout, so the probe of one talent never sees another's AvatarVars writes.
            if (BindTalent(new BbLoadoutOwner(), id, rank, championLevel) is { } report) reports[id] = report;
        }
        return reports;
    }

    /// <summary>
    /// What the buff's own script adds to the holder's stats with the holder's stats reading 1. The hooks run in one
    /// update's order — <c>OnBuffActivate</c> (which zeroes its own running total and checks its required variable),
    /// <c>BuffOnUpdateActions</c> (which recomputes that total from the live stats) and <c>BuffOnUpdateStats</c>
    /// (which adds it) — so the captured add is the factor the buff applies to a base of 1: Ardor's
    /// <c>PercentMod</c>, Strength of Spirit's <c>multiplier</c>, Nimbleness's <c>0.1</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> BuffStats(BbLoadoutBuffAdd add)
    {
        ArgumentNullException.ThrowIfNull(add);
        var script = BuffScript(add.Name);
        var host = new BbLoadoutEvalHost(1);
        if (script is null) return host.Stats;
        var scope = NewBuffScope(add);
        var interpreter = new BbInterpreter(host);
        foreach (var hook in new[] { "OnBuffActivate", "BuffOnUpdateActions", "BuffOnUpdateStats" })
            interpreter.ExecuteHook(script, hook, scope);
        return host.Stats;
    }

    /// <summary>
    /// What the buff's <c>BuffOnHitUnit</c> adds to a hit of 0 against an AI, non-turret, non-hero: the flat bonus
    /// Offensive Mastery's <c>DamageBonus</c> is (<c>{2, 4}[Level]</c>). A buff with no such hook adds nothing.
    /// </summary>
    public static double BuffOnHitBonus(BbLoadoutBuffAdd add)
    {
        ArgumentNullException.ThrowIfNull(add);
        var script = BuffScript(add.Name);
        if (script is null || script.Event("BuffOnHitUnit") is null) return 0.0;
        var host = new BbLoadoutEvalHost(1);
        var scope = NewBuffScope(add);
        scope.Attacker = BbLoadoutEvalHost.Hero;
        scope.Target = BbLoadoutEvalHost.Minion;
        scope.Vars["DamageAmount"] = 0.0;
        var interpreter = new BbInterpreter(host);
        interpreter.ExecuteHook(script, "OnBuffActivate", scope);
        interpreter.ExecuteHook(script, "BuffOnHitUnit", scope);
        return BbRuntime.NumberOrNull(scope.Vars["DamageAmount"]) ?? 0.0;
    }

    /// <summary>
    /// The live form of <see cref="BuffOnHitBonus"/>: the same buff script's <c>BuffOnHitUnit</c> against the hit the
    /// simulation is resolving, returning the amount it left. <paramref name="attacker"/> and <paramref name="target"/>
    /// carry the two units' kind flags, which is all the hook's <c>CO_IS_TYPE_*</c> branch reads (Offensive Mastery
    /// excludes turrets and heroes). A buff with no such hook leaves the hit alone.
    /// </summary>
    internal static double BuffOnHitModify(BbLoadoutBuffAdd add, double amount, BbDamageType type,
        BbProbeUnit attacker, BbProbeUnit target)
    {
        ArgumentNullException.ThrowIfNull(add);
        var script = BuffScript(add.Name);
        if (script is null || script.Event("BuffOnHitUnit") is null) return amount;
        var host = new BbLoadoutEvalHost(1);
        var scope = NewBuffScope(add);
        scope.Attacker = attacker;
        scope.Target = target;
        scope.Vars["DamageType"] = (double)type;
        scope.Vars["DamageAmount"] = amount;
        var interpreter = new BbInterpreter(host);
        interpreter.ExecuteHook(script, "OnBuffActivate", scope);
        interpreter.ExecuteHook(script, "BuffOnHitUnit", scope);
        return Math.Max(0.0, BbRuntime.NumberOrNull(scope.Vars["DamageAmount"]) ?? amount);
    }

    /// <summary>Whether the buff's own script has that hook (so a live run is worth starting).</summary>
    internal static bool BuffHasHook(string buffName, string hook) =>
        BuffScript(buffName)?.Event(hook) is not null;

    /// <summary>Every native the report's probe host answered with nothing, so a conversion gap is visible.</summary>
    public static IReadOnlyCollection<string> IgnoredNatives => BbLoadoutEvalHost.Ignored.Keys.ToArray();

    private static void Run(BbLoadoutEvalHost host, BbLoadoutBinding binding, BbLoadoutOwner owner, string hook)
    {
        var scope = owner.NewScope(binding, BbLoadoutEvalHost.Hero, BbLoadoutEvalHost.Hero, BbLoadoutEvalHost.Hero);
        new BbInterpreter(host).ExecuteHook(binding.Script, hook, scope);
    }

    /// <summary>
    /// One talent bound into <paramref name="owner"/> at <paramref name="rank"/>, with its <c>SetVarsByLevel</c>
    /// already run (it fills the binding's <c>InstanceVars</c> and the owner's <c>AvatarVars</c>). The report that comes
    /// back keeps the binding live, so its character hooks can be run later against real hits
    /// (<see cref="BbTalentReport.ModifyHit"/>). Null when the talent has no shipped script or allocates nothing.
    /// </summary>
    internal static BbTalentReport? BindTalent(BbLoadoutOwner owner, int id, int rank, int championLevel)
    {
        if (rank <= 0) return null;
        var script = Script($"Talents/{id}.luaobj");
        if (script is null) return null;
        var binding = owner.SetTalent(script, id, rank);
        if (binding is null) return null;
        var host = new BbLoadoutEvalHost(championLevel);
        Run(host, binding, owner, "SetVarsByLevel");
        Run(host, binding, owner, "UpdateSelfBuffStats");
        Run(host, binding, owner, "UpdateSelfBuffActions");
        return new BbTalentReport(owner, binding, host);
    }

    private static BbScope NewBuffScope(BbLoadoutBuffAdd add) =>
        NewBuffScope(add, BbLoadoutEvalHost.Hero, BbLoadoutEvalHost.Hero);

    private static BbScope NewBuffScope(BbLoadoutBuffAdd add, BbProbeUnit attacker, BbProbeUnit target)
    {
        // The buff gets a copy of the add's variables as its InstanceVars; the holder is its Owner and
        // its Target, which is what the shipped buff scripts name both of them.
        var scope = BbScope.ForHook(BbLoadoutEvalHost.Hero, attacker, target, BbLoadoutEvalHost.Hero);
        var vars = new BbTable();
        foreach (var entry in add.Vars.Entries) vars[entry.Key] = entry.Value;
        scope.InstanceVars = vars;
        return scope;
    }

    private static BbScript? Script(string relativePath) =>
        Scripts.GetOrAdd(relativePath, path => BbScriptLoader.Load(GameDataPaths.Default, path).Script);

    /// <summary>The buff's script is named after the add (<c>Ardor</c> → <c>Spells/ardor.luaobj</c>), lower case.</summary>
    private static BbScript? BuffScript(string buffName) =>
        Script($"Spells/{buffName.ToLowerInvariant()}.luaobj");
}

/// <summary>A probe unit: a named handle whose kind flags are fixed, and nothing else.</summary>
internal sealed record BbProbeUnit(string Name, bool Ai, bool Hero, bool Turret);

/// <summary>
/// The probe world: two units, every stat and resource reading 1 (so a buff's captured add is the factor it applies
/// to a base of 1), and no buffs present (so <c>BBIfNotHasBuff</c> passes and <c>BBIfHasBuff</c> fails, which is the
/// state a mastery's marker buff is added in). IncStats and buff adds are captured; everything else is a no-op that
/// answers a fixed value.
/// </summary>
internal sealed class BbLoadoutEvalHost : IBbHost, IBbNativeHandler
{
    public static readonly BbProbeUnit Hero = new("Hero", Ai: true, Hero: true, Turret: false);
    public static readonly BbProbeUnit Minion = new("Minion", Ai: true, Hero: false, Turret: false);
    public static readonly BbProbeUnit Turret = new("Turret", Ai: true, Hero: false, Turret: true);
    /// <summary>A unit kind the engine does not count as AI (a structure or a missile): <c>CO_IS_TYPE_AI</c> fails.</summary>
    public static readonly BbProbeUnit NotAi = new("NotAi", Ai: false, Hero: false, Turret: false);

    /// <summary>Natives the loadout scripts may call that this host answers with nothing; kept so a gap is visible.</summary>
    public static readonly ConcurrentDictionary<string, byte> Ignored = new(StringComparer.Ordinal);

    private readonly Dictionary<string, double> _stats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _resourceMods = new(StringComparer.Ordinal);
    private readonly List<BbLoadoutBuffAdd> _buffs = new();

    public BbLoadoutEvalHost(double championLevel) => Level = championLevel;

    /// <summary>The hero's level, the <c>BBGetLevel</c> answer for the wearer (106's Sorcerer's Insight scales with it).</summary>
    public double Level { get; }

    public IReadOnlyDictionary<string, double> Stats => _stats;
    public IReadOnlyDictionary<string, double> ResourceMods => _resourceMods;
    public IReadOnlyList<BbLoadoutBuffAdd> Buffs => _buffs;

    public IBbNativeHandler? NativeHandler(BbNativeOwner owner) => this;

    // ------------------------------------------------------------------------------------------------------------
    // Natives
    // ------------------------------------------------------------------------------------------------------------

    public void Execute(BbCall call)
    {
        switch (call.Function)
        {
            case "BBSpellBuffAdd":
                AddBuff(call);
                return;
            case "BBIncPercentPARPoolMod":
            case "BBIncFlatPARPoolMod":
            case "BBIncPercentPARRegenMod":
            case "BBIncFlatPARRegenMod":
            case "BBIncPermanentPercentPARPoolMod":
            case "BBIncPermanentFlatPARPoolMod":
            case "BBIncPermanentPercentPARRegenMod":
            case "BBIncPermanentFlatPARRegenMod":
                Add(_resourceMods, call.Function, Sum(call, "Delta"));
                return;
            case "BBPreloadSpell":
            case "BBPreloadParticle":
            case "BBPreloadCharacter":
                return; // an asset preload: no runtime effect
        }
        Ignored.TryAdd(call.Function, 0);
    }

    private void AddBuff(BbCall call)
    {
        // The buff gets a copy of BuffVarsTable (default NextBuffVars); no BuffName names the script.
        var vars = new BbTable();
        var source = call.Table("BuffVarsTable", create: false);
        foreach (var entry in source.Entries) vars[entry.Key] = entry.Value;
        _buffs.Add(new BbLoadoutBuffAdd(call.Value("BuffName") as string ?? "", Sum(call, "Duration"), vars,
            call.Value("BuffAddType") as double? ?? 0.0, call.Value("BuffType") as double? ?? 0.0,
            call.Value("CanMitigateDuration") as bool? ?? false));
    }

    /// <summary>The number getter the natives share, in Lua doubles like <c>BbBaseLibrary.IncStat</c>.</summary>
    private static double Sum(BbCall call, string key)
    {
        var total = 0.0;
        if (call.Value(key) is double literal) total = literal;
        if (call.Param(key + "ByLevel") is { } byLevel && call.Scope.Level is { } level)
            total += BbRuntime.NumberOrNull(BbRuntime.Index(byLevel, level)) ?? 0.0;
        if (call.Value(key + "Var") is { } name)
            total += BbRuntime.NumberOrNull(call.Table(key + "VarTable", true)[name]) ?? 0.0;
        return total;
    }

    private static void Add(Dictionary<string, double> totals, string key, double delta) =>
        totals[key] = (totals.TryGetValue(key, out var current) ? current : 0.0) + delta;

    // ------------------------------------------------------------------------------------------------------------
    // Host primitives
    // ------------------------------------------------------------------------------------------------------------

    private static BbProbeUnit Unit(object? unit) => unit as BbProbeUnit ?? Hero;

    // IBbUnitHost
    public double GetTeamID(object? unit) => 100.0;
    // The type natives answer false for nil; see BbSpellHost.
    public bool IsObjectAI(object? unit) => unit is BbProbeUnit { Ai: true };
    public bool IsObjectHero(object? unit) => unit is BbProbeUnit { Hero: true };
    public bool IsMelee(object? unit) => true;
    public bool IsTurretAI(object? unit) => unit is BbProbeUnit { Turret: true };
    public bool IsDead(object? unit) => false;
    public string? GetUnitSkinName(object? unit) => "Probe";
    public void SetScaleSkinCoef(object? scale, object? unit) { }

    // IBbGeometryHost
    public object? GetPosition(object? unit) => BbTable.Point(0.0, 0.0, 0.0);
    public object? GetNormalizedPositionDelta(object? target, object? attacker, bool flag) => BbTable.Point(1.0, 0.0, 0.0);
    public void SetPosition(object? unit, object? position) { }
    public object? Make3DPoint(object? x, object? y, object? z) =>
        BbTable.Point(BbRuntime.NumberOrNull(x) ?? 0.0, BbRuntime.NumberOrNull(y) ?? 0.0, BbRuntime.NumberOrNull(z) ?? 0.0);
    public double DistanceBetweenObjects(object? a, object? b) => 0.0;
    public double DistanceBetweenObjectAndPoint(object? unit, object? point) => 0.0;
    public double DistanceBetweenPoints(object? a, object? b) => 0.0;
    public bool IsTargetInFrontOfMe(object? me, object? target) => true;
    public bool IsTargetBehindMe(object? me, object? target) => false;

    // IBbBuffHost — no buff is present in the probe world
    public bool HasBuffOfType(object? unit, double? buffType) => false;
    public double SpellBuffCount(object? unit, string? buffName, object? caster) =>
        ReferenceEquals(unit, Hero) && HeroBuffCount is { } count ? count(buffName) : 0.0;

    /// <summary>The wearer's real buff count by name when a live hero is polled (<see cref="BbTalentReport.HookBuffs"/>); else no buffs.</summary>
    public Func<string?, double>? HeroBuffCount { get; init; }
    public void SpellBuffAdd(object? target, object? attacker, string buffName, double addType, double maxStack,
        double duration, BbTable? vars) =>
        _buffs.Add(new BbLoadoutBuffAdd(buffName, duration, vars ?? new BbTable()));
    public void SpellBuffRemove(object? target, string? buffName, object? attacker) { }

    // IBbCombatHost
    public double GetSourceType() => 0.0;

    // IBbStatHost — every stat reads 1, so a buff's captured add is the factor it applies to a base of 1
    public double GetLevel(object? unit) => Unit(unit) == Hero ? Level : 1.0;
    public double GetArmor(object? unit) => 0.0;
    public double GetSpellBlock(object? unit) => 0.0;
    public double GetTotalAttackDamage(object? unit) => 0.0;
    public void IncStat(BbIncStat stat, double delta, object? unit) => Add(_stats, stat.ToString(), delta);
    public double GetStat(BbGetStat stat, object? unit) => 1.0;
    public bool GetStatus(BbGetStatus status, object? unit) => false;
    public void SetStatus(BbSetStatus status, object? unit, object? value) { }

    // IBbResourceHost
    public bool HasPARType(object? unit, double? parType) => true;
    public object? GetResource(BbGetResource function, object? unit, double? parType) => 1.0;
    public double GetGold(BbCall call) => 0.0;
    public void ReincarnateNonDeadHero(object? unit) { }

    // IBbSpellHost
    public object? GetSlotSpellInfo(BbSlotSpellInfo function, object? owner, object? slot, double? spellbook,
        double? slotType) => function == BbSlotSpellInfo.GetSlotSpellName ? "" : 0.0;
    public void SetSlotSpellCooldownTime(object? owner, object? slot, double? spellbook, double? slotType,
        object? cooldown) { }
    public void SetSpellCastRange(double? range) { }
    public void SetCanCastWhileDisabled(bool canCast) { }
    public object? GetCastInfo(BbCastInfo info, object? unit) => info == BbCastInfo.GetSpellName ? "" : 0.0;

    // IBbGameHost
    public double GetTime() => 0.0;
    public double Random() => 0.0;
    public void Say(object? unit, string? text) { }
    public void Alert(string? text) { }

    // IBbOrderHost
    public void IssueOrder(object? whom, double? order, object? position, object? target) { }
    public void DefUpdateAura(object? center, double? range, double? unitScan, string? buffName) { }
}
