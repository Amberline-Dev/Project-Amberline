using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The mastery area's live hooks (batch 140 step 1b): every hero's allocated talent scripts run their own character
/// hooks at the stages the damage pipeline already exposes, the way a champion's character script does. The talents are
/// not champion scripts — every hero wears a page, the ten hand-written champions included — so this dispatcher is per
/// hero rather than per champion module, and it is installed for every match.
/// <para>
/// What runs here is the shipped <c>DATA/Talents/&lt;id&gt;.luaobj</c> through the interpreter
/// (<see cref="BbTalentReport.ModifyHit"/>), not a table of coefficients: <c>CharOnBeingHit</c> (113 Harden Skin),
/// <c>CharOnPreDealDamage</c> (123 Havoc), <c>CharOnPreDamage</c> (125 Tenacity, 144 Defensive), <c>CharOnDodge</c>
/// (127 Nimbleness) and the <c>BuffOnHitUnit</c> of the marker buff 137 Offensive Mastery asked for. The hook a talent
/// does not define leaves the hit alone, so no talent id is special-cased on the damage path.
/// </para>
/// <para>
/// Reconstruction (ordering): the stage each hook lands in is the one the hand-written mastery application used —
/// Harden Skin before the raw-damage snapshot and before the champion's own <c>CharOnBeingHit</c>, Havoc and the
/// Offensive Mastery hit bonus in the pre-mitigation stage after the pre-deal hooks (Havoc's multiplier first, then the
/// flat hit bonus: the difference is 2 * 0.04 per hit), Tenacity and Defensive after resistance and after the target's
/// <c>BuffOnPreDamage</c>/<c>CharOnPreDamage</c>. The stock client has no hook dispatch, so the order of a talent's
/// hook against a champion script's own is not recoverable.
/// </para>
/// </summary>
public sealed class BbLoadoutHooks
{
    private readonly Func<BuffLifecycle?> _buffs;
    private readonly Dictionary<Entity, Page> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly List<BbScriptFailure> _failures = new();

    public BbLoadoutHooks(Func<BuffLifecycle?> buffs, Action<BbScriptFailure>? failureSink = null)
    {
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        FailureSink = failureSink;
    }

    /// <summary>Called for each failure; the production composition writes an ERROR line to the session log.</summary>
    public Action<BbScriptFailure>? FailureSink { get; set; }

    /// <summary>Every hook run that raised, in order (production safety, the same contract as <c>BbHookRouter.Failures</c>).</summary>
    public IReadOnlyList<BbScriptFailure> Failures => _failures;

    // ------------------------------------------------------------------------------------------------------------
    // The live script host (batch 144): a loadout add whose buff keeps its own update loop runs as a real buff
    // ------------------------------------------------------------------------------------------------------------

    private BbSpellHost? _host;
    private BbHookRouter? _router;

    /// <summary>
    /// The loadout's own hook sink (batch 144), installed on <c>MatchGameplayHooks.LoadoutScriptHooks</c>. Null until
    /// <see cref="BindMatch"/> runs: without a live host a loadout add is the static payload
    /// <see cref="Install"/> has always built, which is what the differential checks want while the conversion lands.
    /// </summary>
    public IBbHookSink? Sink => _router;

    /// <summary>
    /// Binds the loadout's live script host to a match (batch 144). The closure is empty on purpose: the scripts this
    /// host runs are the buffs the loadout adds, and <see cref="BbScriptSet.FindOrShipped"/> resolves those from the
    /// shipped <c>Spells/&lt;name&gt;.luaobj</c> the first time one is added. Call once per match, before the first
    /// hero is created.
    /// </summary>
    public IBbHookSink? BindMatch(SpellSystem system, Action<BbScriptFailure>? failureSink = null)
    {
        if (_router is not null) return _router;
        ArgumentNullException.ThrowIfNull(system);
        var scripts = new BbScriptSet(new BbClosure("Loadout", Array.Empty<BbClosureScript>(),
            Array.Empty<BbDanglingReference>(), Array.Empty<string>(), Array.Empty<BbDiagnostic>()), system.Data.Paths);
        var host = new BbSpellHost(system, (_, _) => "");
        var router = new BbHookRouter(host, scripts);
        host.Router = router;
        router.FailureSink = failureSink ?? FailureSink;
        router.AvatarVarsOf = hero => AvatarVars(hero) ?? OwnerFor(hero)?.AvatarVars ?? new BbTable();
        host.Buffs = new BbBuffRuntime(host, scripts, router);
        _host = host;
        _router = router;
        return router;
    }

    /// <summary>
    /// A buff this area's live host added left its holder (<c>OnBuffDeactivate</c>). Routed by
    /// <c>MatchGameplayHooks</c> next to the champion modules' and the summoner spells' own removals; a router only
    /// acts on the buff instances it created.
    /// </summary>
    public void OnBuffRemoved(Entity unit, BuffState buff) => _host?.Buffs.OnRemoved(unit, buff);

    // ------------------------------------------------------------------------------------------------------------
    // The stages
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The attacker's side of the pre-mitigation stage, where the hand-written mastery application sat: the hero's
    /// <c>CharOnPreDealDamage</c> (Havoc) first, then the <c>BuffOnHitUnit</c> of the marker buffs its self-buff hooks
    /// asked for (Offensive Mastery). Both are damage-increase mods, so a hit that carries
    /// <c>IgnoreDamageIncreaseMods</c> (an attack override's primary hit) skips them — the target's block of the same
    /// stage does not, and lives in <see cref="OnBeingHit"/>.
    /// </summary>
    public double OnPreDealDamage(Entity? source, Entity target, double amount, DamageType type,
        DamageSourceKind? kind, bool ignoreDamageIncreaseMods)
    {
        if (source is not { UnitType: UnitType.Hero, Kind: EntityKind.Hero }) return amount;
        if (ignoreDamageIncreaseMods) return amount;
        var page = PageFor(source);
        if (page is null) return amount;
        foreach (var talent in page.Talents.Values)
        {
            amount = Run(talent, "CharOnPreDealDamage", amount, type, KindOf(source));
            if (kind != DamageSourceKind.Basic) continue;
            foreach (var add in talent.Buffs.Where(add => BbLoadoutReport.BuffHasHook(add.Name, "BuffOnHitUnit")))
                amount = Guard(talent.Id + "/" + add.Name, "BuffOnHitUnit", amount,
                    () => BbLoadoutReport.BuffOnHitModify(add, amount, TypeOf(type), KindOf(source), KindOf(target)));
        }
        return amount;
    }

    /// <summary>
    /// The target's side of the same stage: a basic attack landed on <paramref name="target"/> and its talents'
    /// <c>CharOnBeingHit</c> (Harden Skin's flat block) change it before the raw damage is kept. The hook only ever
    /// fires for a basic hit — the same invocation rule the champion path gives it — and the hit's own damage type is
    /// what the script tests. Reconstruction: a basic hit whose source entity is already gone still reaches here, which
    /// is where the hand-written block sat (it did not need a live source).
    /// </summary>
    public double OnBeingHit(Entity target, Entity? attacker, double amount, DamageType type)
    {
        var page = PageFor(target);
        if (page is null) return amount;
        foreach (var talent in page.Talents.Values)
            amount = Run(talent, "CharOnBeingHit", amount, type, KindOf(attacker));
        return amount;
    }

    /// <summary>
    /// The damage the hero is about to take after resistance and before the shields: its talents'
    /// <c>CharOnPreDamage</c> (Tenacity's non-true factor, Defensive's block of an AI non-hero hit) run after the
    /// target's script buffs and its champion's own hook, where the hand-written reductions sat.
    /// </summary>
    public double OnPreDamage(Entity target, Entity? source, double amount, DamageType type)
    {
        var page = PageFor(target);
        if (page is null) return amount;
        foreach (var talent in page.Talents.Values)
            amount = Run(talent, "CharOnPreDamage", amount, type, KindOf(source));
        return amount;
    }

    /// <summary>
    /// The hero dodged a basic attack: its talents' <c>CharOnDodge</c> (127 Nimbleness) ask for their buff, and the
    /// engine installs what the script asked for with the payload the buff's own script collects.
    /// </summary>
    public void OnBasicAttackDodge(Entity attacker, Entity target)
    {
        var page = PageFor(target);
        if (page is null) return;
        foreach (var talent in page.Talents.Values)
        {
            foreach (var add in RunAdd(talent, "CharOnDodge"))
                Guard(talent.Id + "/" + add.Name, "CharOnDodge", 0.0, () =>
                {
                    Install(target, add);
                    return 0.0;
                });
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Running one talent hook
    // ------------------------------------------------------------------------------------------------------------

    private double Run(BbTalentReport talent, string hook, double amount, DamageType type, BbProbeUnit attacker)
    {
        if (!talent.HasHook(hook)) return amount;
        return Guard(talent.Id + "/" + hook, hook, amount,
            () => talent.ModifyHit(hook, amount, TypeOf(type), attacker));
    }

    private IReadOnlyList<BbLoadoutBuffAdd> RunAdd(BbTalentReport talent, string hook, Func<string?, double>? heroBuffCount = null)
    {
        if (!talent.HasHook(hook)) return Array.Empty<BbLoadoutBuffAdd>();
        try { return talent.HookBuffs(hook, heroBuffCount); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            Fail(talent.Id + "/" + hook, hook, ex);
            return Array.Empty<BbLoadoutBuffAdd>();
        }
    }

    private double Guard(string script, string hook, double fallback, Func<double> run)
    {
        try { return run(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            Fail(script, hook, ex);
            return fallback;
        }
    }

    /// <summary>Reconstruction, production safety (the same contract as <c>BbHookRouter.Fail</c>): a raising script loses that one hook run.</summary>
    private void Fail(string script, string hook, Exception ex)
    {
        var failure = new BbScriptFailure(script, hook, (ex as BbRuntimeException)?.BlockPath ?? "", ex.Message);
        if (_failures.Count < 256) _failures.Add(failure);
        try { FailureSink?.Invoke(failure); }
        catch (Exception) { /* a broken log sink must not take the match down */ }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Installing a buff a loadout script asked for
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The talents' periodic self-buff adds, polled on the same self-buff line as the items' copies (SpellSystem's hero loop). A talent's
    /// <c>UpdateSelfBuffActions</c> runs on every poll, as retail runs it, against the hero's real buffs: Talents/135.luaobj (the
    /// utility marker) adds <c>MonsterBuffs</c>/<c>MonsterBuffs2</c> with <c>BUFF_RENEW_EXISTING</c> each time, which renews the live
    /// marker (the client gets an in-place renewal of the slot it holds, 0x32 or 0x1F, never a second 0xBF: batch 157's rule in
    /// <c>ReplicationDelivery.AsHeldBuffUpdate</c>), and Talents/111.luaobj (Ardor) guards its <c>BUFF_REPLACE_EXISTING</c> add with its own
    /// <c>BBIfNotHasBuff</c>, so a live Ardor is left alone. A buff a death retired comes back on the living hero's first poll.
    /// <para>
    /// Batch 246: <paramref name="due"/> is the hero's self-buff line (<see cref="BbSelfUpdate"/>, Reconstruction: the engine's 0.25 s
    /// <c>BuffOnUpdateActions</c> grid, the client holding no period for these hooks). Every shipped talent's hook is bare, so the
    /// utility marker is renewed four times a second, not once per simulation step (about 30, batch 241's measurement).
    /// </para>
    /// </summary>
    public void PollSelfBuffs(Entity hero, bool due)
    {
        if (!hero.Alive || !_pages.TryGetValue(hero, out var page)) return;
        // A page a rebind has not caught up with waits for the next bind (PageFor), as every other hook does.
        if (!ReferenceEquals(page.Masteries, hero.Masteries) || page.Level != hero.Level) return;
        foreach (var talent in page.Talents.Values)
        {
            if (!talent.HasHook("UpdateSelfBuffActions") || !BbSelfUpdate.Runs(talent.Binding.Script, "UpdateSelfBuffActions", due)) continue;
            foreach (var add in RunAdd(talent, "UpdateSelfBuffActions", name => hero.Buffs.Count(buff => buff.Name == name)))
            {
                if (!IsLiveAdd(add)) continue;
                Guard(talent.Id + "/" + add.Name, "UpdateSelfBuffActions", 0.0, () =>
                {
                    Install(hero, add);
                    return 0.0;
                });
            }
        }
    }

    /// <summary>
    /// The adds that live on the hero as buffs: those whose buff keeps its own update loop (Ardor), and the utility's neutral
    /// markers (batch 196), which the shipped neutral death scripts read from the killer.
    /// </summary>
    private static bool IsLiveAdd(BbLoadoutBuffAdd add) =>
        BbLoadoutReport.BuffHasHook(add.Name, "BuffOnUpdateActions") || MasteryEffects.NeutralBuffMarkers.Contains(add.Name);

    /// <summary>
    /// The add goes through the ordinary buff lifecycle with the payload the buff's own script collects
    /// (<c>OnBuffActivate</c> / <c>BuffOnUpdateActions</c> / <c>BuffOnUpdateStats</c> of <c>Spells/&lt;name&gt;.luaobj</c>),
    /// so the number is the script's own (Nimbleness's <c>IncPercentMovementSpeedMod</c>) and not a table entry. The
    /// script's own <c>BuffType</c> and <c>CanMitigateDuration</c> are honoured as asked.
    /// <para>
    /// Batch 144: a buff that keeps its own update loop (<c>BuffOnUpdateActions</c> — Ardor scales the AP and
    /// attack-speed pools it just wrote, Zhonya's Ring the flat AP beside its own) is added as a live instance instead,
    /// so the loop runs against the hero's real stats every buff tick. Its frozen payload would pin the first frame
    /// forever, which is exactly the frozen reading batch 140 recorded as a deviation from retail. Everything else stays on the static path:
    /// a marker buff such as Offensive Mastery's is read twice by <see cref="OnPreDealDamage"/>, once from here and once
    /// from its own <c>BuffOnHitUnit</c> in the live router, and must not be applied twice. Batch 196: the utility's neutral
    /// markers (<see cref="MasteryEffects.NeutralBuffMarkers"/>) are live as well; their scripts have no hooks, so nothing applies
    /// them twice, and the neutral death scripts read them from the killer.
    /// </para>
    /// </summary>
    private void Install(Entity target, BbLoadoutBuffAdd add)
    {
        if (_host?.Buffs is { } live && IsLiveAdd(add))
        {
            live.Add(new BbBuffRequest(target, target, add.Name, (int)add.AddType, (int)add.BuffType, 1, add.Duration,
                0.0, add.CanMitigateDuration, add.Vars));
            return;
        }
        var buffs = _buffs();
        if (buffs is null || !BuffLifecycle.CanAddNamedBuffs(target, add.Name)) return;
        var collector = new BbStatCollector();
        foreach (var (accessor, delta) in BbLoadoutReport.BuffStats(add))
            if (Enum.TryParse<BbIncStat>(accessor, out var stat)) collector.Add(stat, delta);
        buffs.AddBuff(target, add.Name, BbBuffRuntime.ServerBuffType(add.BuffType), add.Duration, target.NetId,
            BbBuffRuntime.PayloadOf(collector), canMitigateDuration: add.CanMitigateDuration);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The per-hero page
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>One hero's live loadout: its talent bindings (rebuilt when its page or its level changes) and its two summoner slots.</summary>
    private sealed class Page
    {
        public MasteryPage Masteries = MasteryPage.Empty;
        public int Level;
        public BbLoadoutOwner Owner = new();
        public IReadOnlyDictionary<int, BbTalentReport> Talents = new Dictionary<int, BbTalentReport>();
    }

    private Page? PageFor(Entity hero)
    {
        if (hero.UnitType != UnitType.Hero || hero.Kind != EntityKind.Hero) return null;
        if (!_pages.TryGetValue(hero, out var page)) _pages[hero] = page = new Page();
        // Reconstruction: retail runs SetVarsByLevel when the character is created and on every level-up. Here the
        // bindings are (re)made the first time a hook asks for them after the page or the level changed.
        if (!ReferenceEquals(page.Masteries, hero.Masteries) || page.Level != hero.Level) Bind(page, hero);
        return page;
    }

    /// <summary>
    /// The hero's whole loadout — its allocated talents and its two summoner spell slots. The owner is the hero's and
    /// outlives a rebind: its <c>AvatarVars</c> is the one table the mastery feeders write and the summoner scripts read,
    /// and its summoner bindings are the spellbook's two slots (batch 142 step 2).
    /// </summary>
    public BbLoadoutOwner? OwnerFor(Entity hero) => PageFor(hero)?.Owner;

    /// <summary>The hero's avatar variables (an unwritten entry reads nil), the same table <see cref="BbLoadoutOwner.AvatarVars"/> hands its bindings.</summary>
    public BbTable? AvatarVars(Entity hero) => PageFor(hero)?.Owner.AvatarVars;

    private void Bind(Page page, Entity hero)
    {
        page.Masteries = hero.Masteries;
        page.Level = hero.Level;
        // The owner survives the rebind (its summoner bindings and its AvatarVars are the hero's), so only the talents
        // start over — fresh InstanceVars, the way a page change or a level-up does.
        page.Owner.ClearTalents();
        var talents = new Dictionary<int, BbTalentReport>();
        foreach (var (id, rank) in hero.Masteries.Allocations)
            if (BbLoadoutReport.BindTalent(page.Owner, id, rank, hero.Level) is { } talent) talents[id] = talent;
        page.Talents = talents;
        // Batch 144: the self-buff adds whose buff keeps its own update loop (Ardor) are installed live, on the bind
        // that captured them, so a page change or a level-up replaces them with that rank's own numbers the way retail's
        // SetVarsByLevel re-runs the add. The other adds stay captured and are read from the report. Batch 196: the
        // utility's neutral markers are live too (IsLiveAdd), so the shipped neutral death scripts see them on the killer.
        foreach (var talent in talents.Values)
            foreach (var add in talent.Buffs)
                if (IsLiveAdd(add)) Install(hero, add);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Unit kind flags
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>CO_IS_TYPE_AI</c>/<c>_TURRET</c>/<c>_HERO</c> reading of a unit, the same one <c>BbSpellHost</c> gives
    /// (hero, minion, monster and turret are AI; a structure or a missile is not). A missing source reads as a hero,
    /// which takes the same branch of Defensive's hook as a real one: neither reduces the hit.
    /// </summary>
    private static BbProbeUnit KindOf(Entity? unit) => unit switch
    {
        null => BbLoadoutEvalHost.Hero,
        { UnitType: UnitType.Hero } => BbLoadoutEvalHost.Hero,
        { UnitType: UnitType.Turret } => BbLoadoutEvalHost.Turret,
        { UnitType: UnitType.Minion or UnitType.Monster } => BbLoadoutEvalHost.Minion,
        _ => BbLoadoutEvalHost.NotAi,
    };

    /// <summary>The <c>DamageType</c> number the shipped hooks read (<c>PHYSICAL_DAMAGE</c> 0, <c>MAGIC_DAMAGE</c> 1, <c>TRUE_DAMAGE</c> 2).</summary>
    private static BbDamageType TypeOf(DamageType type) => type switch
    {
        DamageType.Magic => BbDamageType.MAGIC_DAMAGE,
        DamageType.True => BbDamageType.TRUE_DAMAGE,
        _ => BbDamageType.PHYSICAL_DAMAGE,
    };
}
