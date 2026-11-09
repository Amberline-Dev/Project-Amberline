using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Decides which hook runs when, builds the <c>vars</c> scope each hook sees and keeps the per-hero variable tables.
/// Every timing here is a Reconstruction: the stock client has no <c>TargetExecute</c>/<c>SelfExecute</c>/<c>BuffOn*</c>
/// strings (hook dispatch is server-only), so the table in docs/scripting/building-blocks-engine.md derives each rule from
/// the hand-written Annie module's verified timing and the existing buff tick cadence.
/// </summary>
internal sealed partial class BbHookRouter : IBbHookSink
{
    private readonly BbSpellHost _host;
    private readonly BbScriptSet _scripts;

    /// <summary>The shipped character script a spawned skin runs (<see cref="BbScriptSet.SpawnedCharacterPath"/>), or null. Batch 431: the placemarker test reads it.</summary>
    internal string? CharacterScriptPath(string skin) => _scripts.SpawnedCharacterPath(skin);
    private readonly Dictionary<Entity, BbTable> _charVars = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, BbTable> _avatarVars = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, BbTable> _charInstanceVars = new(ReferenceEqualityComparer.Instance);
    // Batch 159 step 2: the non-champion units this router runs a character script for (a turret's CharScript), bound one
    // unit at a time by BbUnitCharacterModule. A champion module's router holds none of these.
    private readonly Dictionary<Entity, BbScript> _unitCharacters = new(ReferenceEqualityComparer.Instance);
    private readonly List<BbScriptFailure> _failures = new();

    public BbHookRouter(BbSpellHost host, BbScriptSet scripts)
    {
        _host = host;
        _scripts = scripts;
    }

    internal BbBuffRuntime Buffs => _host.Buffs;

    /// <summary>Every hook run that raised, in order (the match log receives the same records through <see cref="FailureSink"/>).</summary>
    public IReadOnlyList<BbScriptFailure> Failures => _failures;

    /// <summary>Called for each failure; the production composition writes an ERROR line to the session log.</summary>
    public Action<BbScriptFailure>? FailureSink { get; set; }

    /// <summary>
    /// Reconstruction, production safety (M2): a script that raises (an unbound native, a nil in a script table, ...) loses that one hook
    /// run and is reported; it never reaches the simulation tick. Retail's reaction to a script error is not recovered. The list keeps
    /// the first 256 failures; later ones still reach <see cref="FailureSink"/>.
    /// </summary>
    internal void Fail(string script, string hook, Exception ex)
    {
        var failure = new BbScriptFailure(script, hook, (ex as BbRuntimeException)?.BlockPath ?? "", ex.Message);
        if (_failures.Count < 256) _failures.Add(failure);
        try { FailureSink?.Invoke(failure); }
        catch (Exception) { /* a broken log sink must not take the match down either */ }
    }

    public BbTable CharVars(Entity hero) => _charVars.TryGetValue(hero, out var table) ? table : _charVars[hero] = new BbTable();

    /// <summary>
    /// Where this router takes a hero's <c>AvatarVars</c> from. Production points every router at the hero's
    /// <see cref="BbLoadoutOwner.AvatarVars"/>, so the mastery feeders' <c>SetVarsByLevel</c> writes and the summoner
    /// scripts' reads are the same table (batch 142 step 2). Null keeps the router's own per-hero table.
    /// </summary>
    public Func<Entity, BbTable>? AvatarVarsOf { get; set; }

    /// <summary>
    /// The inventory copies' own <c>BuffOnAllowAdd</c>, asked before a buff is placed on a unit that wears them
    /// (<c>BbItemModule.AllowBuffAdd</c>; batch 147 step 4). Set only on the item module's router — the copies are
    /// bindings, not script buffs, so <see cref="BbBuffRuntime.AllowAdd"/> cannot see them. Null asks nothing.
    /// </summary>
    public ItemCopyAllowAddHook? ItemCopyAllowAdd { get; set; }

    /// <summary>The inventory copies' <c>BuffOnAllowAdd</c> signature: the hook may rewrite <paramref name="duration"/>, and the add goes on with it.</summary>
    public delegate bool ItemCopyAllowAddHook(Entity target, Entity adder, string name, ref double duration, double type, double maxStack);

    /// <summary>The hero's avatar variables; an entry that was never written reads nil, as in every Lua table (batch 303).</summary>
    private BbTable AvatarVars(Entity hero) => AvatarVarsOf is { } shared ? shared(hero)
        : _avatarVars.TryGetValue(hero, out var table) ? table : _avatarVars[hero] = new BbTable();

    /// <summary>
    /// The <c>InstanceVars</c> of a hero's character script: one table for the script instance, kept between hook runs. The shipped
    /// <c>UpdateSelfBuffActions</c> of Taric, Kayle, Sivir and Ryze track their <c>BBExecutePeriodically</c> clock in it
    /// (<c>TrackTimeVarTable="InstanceVars"</c>, <c>ExecuteImmediately</c>): with a fresh table per run the clock never persisted and the
    /// hook body ran every tick (Taric re-added his two buffs 25 times a second).
    /// </summary>
    private BbTable CharInstanceVars(Entity hero) =>
        _charInstanceVars.TryGetValue(hero, out var table) ? table : _charInstanceVars[hero] = new BbTable();
    /// <summary>
    /// The <c>SpellVars</c> of one hook run: a fresh table with the casting spell script's plain globals (<c>DoesntTriggerSpellCasts</c>,
    /// <c>CastingBreaksStealth</c>, <c>DoesntBreakShields</c> ...) and nothing else; a spell with no script gives an empty one. The shipped
    /// scripts read exactly those three names from <c>SpellVars</c> (38, 19 and 8 reads in 38 files) and write none, so the table holds no
    /// state between casts. Not every spell script defines them: <c>SpellShield.luaobj</c> sets <c>DoesntTriggerSpellCasts</c>, Yorick's
    /// four slot spells set none, and a global a spell does not define reads nil.
    /// <para>
    /// Batch 708: the table used to be one per (hero, slot number) per router, refilled over the last spell's globals. A Health Potion
    /// drunk from inventory slot 0 (<c>RegenerationPotion.luaobj</c>, <c>DoesntTriggerSpellCasts = true</c>) left its <c>true</c> in the
    /// item module's slot-0 table, and Yorick's Omen of War read it there: Tear of the Goddess (<c>TearOfTheGoddessTrack</c>'s
    /// <c>BuffOnSpellCast</c>) and Sheen's <c>ItemOnSpellCast</c> ignored his Q for the rest of the game. Annie's and Ryze's spells set
    /// the global to <c>false</c> themselves, which overwrote it and hid the leak.
    /// </para>
    /// </summary>
    private static BbTable SpellVars(BbScript? spell)
    {
        var table = new BbTable();
        if (spell is not null) BbSpellContext.Fill(table, spell);
        return table;
    }

    /// <summary>
    /// The same <c>SpellVars</c> for a cast hook this router does not run itself: the item module's
    /// <c>ItemOnSpellCast</c> builds its own scope beside the cast hooks and reads the cast spell's flags
    /// (<c>DoesntTriggerSpellCasts</c> ...) from it. The cast spell is looked up the way
    /// <see cref="RunSpellCast"/>'s buff hooks resolve a spell outside the closure
    /// (<see cref="BbScriptSet.FindOrShipped"/>); a name no shipped script carries gives an empty table.
    /// </summary>
    internal BbTable CastSpellVars(string? spellName) =>
        SpellVars(spellName is null ? null : _scripts.FindOrShipped(spellName));

    // ------------------------------------------------------------------------------------------------------------
    // Scopes and execution
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The scope of a hook: unit slots, the persistent CharVars/AvatarVars of the casting hero, a fresh NextBuffVars, and the
    /// spell rank as <c>Level</c>. A loadout binding (<paramref name="binding"/>, the summoner spellbook's
    /// <c>(hero, summoner slot)</c> owner) contributes its own <c>InstanceVars</c> and its <c>TalentLevel</c> instead of
    /// the character script's.
    /// </summary>
    public BbScope NewScope(Entity owner, Entity? attacker, Entity? target, Entity? caster, double? level,
        BbTable? instanceVars = null, BbLoadoutBinding? binding = null)
    {
        var scope = BbScope.ForHook(owner, attacker, target, caster, level);
        // Reconstruction (batch 252): CharVars are the character's whose script runs, the buff's caster for a buff it put on another unit (Kassadin's
        // Force Pulse marks, Orianna's ball buffs). A buff of this closure on one of its own heroes whose caster is a unit outside it keeps the
        // owner's: Maokai's Vengeful Maelstrom adds MaokaiDrain3Tally to him with the hitting enemy as AttackerVar (MaokaiDrain3Defense,
        // BuffOnPreDamage), and its OnBuffActivate sums CharVars.Tally that MaokaiDrain3's deactivation releases; the enemy's table lost it.
        var vars = caster is not null && (Owns(caster) || !Owns(owner)) ? caster : owner;
        scope.CharVars = CharVars(vars);
        scope.AvatarVars = AvatarVars(vars);
        if (binding is not null)
        {
            scope.InstanceVars = binding.InstanceVars;
            scope.Vars["TalentLevel"] = (double)binding.Rank;
        }
        else if (instanceVars is not null) scope.InstanceVars = instanceVars;
        return scope;
    }

    /// <summary>
    /// The scope of an attack override's <c>TargetExecute</c>: its <c>InstanceVars</c> is the hero's <c>CharVars</c>. The shipped Udyr scripts
    /// need that: <c>UdyrPhoenixAttack.luaobj</c> counts its swings in <c>InstanceVars.Count</c> (instr 0054-0062), tests <c>CharVars.Count</c>
    /// against 3 (0076-0078) and resets it (0135-0136), and <c>UdyrPhoenixStance.luaobj</c> zeroes <c>CharVars.Count</c> (0053-0054): only one variable makes the flame of "every third attack" (the stance's tooltip and
    /// V1.0.0.108, "Flame attack timer reduced to every 3 attacks from 4") work, and a fresh table for every swing never reaches 3. The other attack
    /// scripts of the playable champions use no <c>InstanceVars</c> in it, so nothing else sees the difference.
    /// </summary>
    private BbScope AttackScope(Entity attacker, Entity target, double? level) =>
        NewScope(attacker, attacker, target, attacker, level, CharVars(attacker));

    // Scripts that read the variable Rnd1 (only PickACard.TargetExecute does, of all 2,300 shipped scripts).
    private readonly Dictionary<BbScript, bool> _readsRnd1 = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <c>PickACard.TargetExecute</c> (Twisted Fate) picks the first card with <c>BBIf Src1Var="Rnd1"</c> against 0.34 and
    /// 0.67 (blue, red, gold: a third each). Neither the script nor <c>BuildingBlocksBase.luaobj</c> sets the variable (the base file has
    /// <c>math.random</c> only inside <c>CO_RANDOM_CHANCE</c>), and the string <c>Rnd1</c> is absent from the stock exe's code (no matches), so the server
    /// supplies it like <c>Owner</c> and <c>Target</c>. The generator and seed are the engine's design: a fresh uniform number in [0, 1) per run
    /// of a script that names it, drawn from the match's seeded random source only for such a script, so no other script's stream moves.
    /// </summary>
    private bool ReadsRnd1(BbScript script)
    {
        if (_readsRnd1.TryGetValue(script, out var reads)) return reads;
        reads = script.Walk().Any(item => item.Block.Params.Any(field => field.Value is BbValue.Text { Value: "Rnd1" }));
        _readsRnd1[script] = reads;
        return reads;
    }

    public void Run(BbScript script, string hook, BbScope scope, BbHookFrame frame, bool rethrow = false, IReadOnlyList<int>? resumeAfter = null)
    {
        if (script.Event(hook) is null) return;
        // A buff's hook that removes the buff itself runs once per stack, each on its own table (BbBuffRuntime.RunsPerStack, batch 241).
        if (resumeAfter is null && frame.Buff is { } buff && ReferenceEquals(buff.Script, script) && Buffs.RunsPerStack(buff, hook))
        {
            Buffs.ForEachStack(buff, vars =>
            {
                scope.InstanceVars = vars;
                RunOnce(script, hook, scope, frame, rethrow, null);
            });
            return;
        }
        RunOnce(script, hook, scope, frame, rethrow, resumeAfter);
    }

    private void RunOnce(BbScript script, string hook, BbScope scope, BbHookFrame frame, bool rethrow, IReadOnlyList<int>? resumeAfter)
    {
        _host.Push(frame);
        try
        {
            if (resumeAfter is null && ReadsRnd1(script) && scope.Vars["Rnd1"] is null) scope.Vars["Rnd1"] = _host.Random();
            if (resumeAfter is null) new BbInterpreter(_host).ExecuteHook(script, hook, scope);
            else new BbInterpreter(_host).ExecuteAfter(script.Event(hook)?.Blocks, resumeAfter, scope);
        }
        catch (Exception ex) when (!rethrow && ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The frame stack is balanced by the finally below; the hook run is skipped (see Fail).
            Fail(script.Name, hook, ex);
        }
        finally
        {
            // The held adds of an item proc hook are placed when the run is over (BbBuffRuntime.CommitProcAdds), before the frame
            // goes: the run is what admits them as one group, and the frame still names its script.
            _host.Buffs.CommitProcAdds(frame);
            // The removal packets a clear of this run held go out now, unless an add of the same buff took the slot (BbBuffRuntime.ClearDeferred).
            _host.Buffs.EndRun(frame);
            _host.Pop();
        }
    }

    /// <summary>A buff hook: Owner is the holder, Attacker the unit the hook names (the buff caster, or the killer/damager).</summary>
    /// <param name="prepare">Writes the variables the host provides to this hook beyond the unit slots (<c>Expired</c> ...).</param>
    public void RunBuffHook(BbBuffInstance instance, string hook, Entity? attacker, DamageSourceKind? damage = null,
        bool inDamage = false, Action<BbScope>? prepare = null, bool periodicDamage = false)
    {
        var caster = _host.Spells.World.Get(instance.CasterNetId);
        // Target is the holder in every buff hook: the shipped scripts name the unit that carries the buff "Target" (a buff's
        // effects take it as TargetObjectVar, GarenRecouperate's stats hook reads its max health, CardmasterSlow marks it).
        var scope = NewScope(instance.Owner, attacker, instance.Owner, caster, instance.SpellRank >= 1 ? instance.SpellRank : null,
            instance.InstanceVars);
        scope.Vars["LifeTime"] = instance.LifeTime; // how long the buff has been on its holder (see BbBuffRuntime.OnTick)
        prepare?.Invoke(scope);
        var frame = BuffFrame(instance, hook, damage, inDamage, periodicDamage);
        // A buff's update is where its damage-over-time ticks are dealt, and the buff script is the one that deals them: its own
        // DoesntBreakShields / DoesntTriggerSpellCasts decide whether a tick pops a spell shield, as the running spell's decide for a
        // direct hit (owner decision, 18 October 2026, evening: ticks follow the same flag rule; before, a tick found no running script and
        // always popped). Reconstruction: when the native raises BuffOnBeingSpellHit for a buff's damage is not in the client.
        if (hook == "BuffOnUpdateActions") RunInSpell(instance.Script, hook, scope, frame);
        else Run(instance.Script, hook, scope, frame);
    }

    /// <summary>The <c>PHYSICAL_DAMAGE</c>/<c>MAGIC_DAMAGE</c>/<c>TRUE_DAMAGE</c> number the damage hooks read as <c>DamageType</c>.</summary>
    internal static double DamageTypeNumber(DamageType type) => type switch
    {
        DamageType.Magic => (double)BbDamageType.MAGIC_DAMAGE,
        DamageType.True => (double)BbDamageType.TRUE_DAMAGE,
        _ => (double)BbDamageType.PHYSICAL_DAMAGE,
    };

    /// <summary>
    /// <c>BuffOnPreDamage</c> of one buff on the unit about to take damage. The hook reads <c>DamageAmount</c> and <c>DamageType</c>
    /// (the shipped shields and reductions compare it with <c>MAGIC_DAMAGE</c>/<c>TRUE_DAMAGE</c>: 16 tables) and may write <c>DamageAmount</c> back.
    /// </summary>
    public double RunPreDamage(BbBuffInstance instance, Entity? source, double amount, DamageType type, DamageSourceKind? kind,
        string hook = "BuffOnPreDamage")
    {
        var caster = _host.Spells.World.Get(instance.CasterNetId);
        var scope = NewScope(instance.Owner, source, instance.Owner, caster, instance.SpellRank >= 1 ? instance.SpellRank : null,
            instance.InstanceVars);
        scope.Vars["DamageAmount"] = amount;
        scope.Vars["DamageType"] = DamageTypeNumber(type);
        var frame = BuffFrame(instance, hook, kind, inDamage: true);
        Run(instance.Script, hook, scope, frame);
        // Reconstruction: what the stock server does with a negative DamageAmount is not recovered. Blitzcrank's ManaBarrierIcon computes one
        // when his health is already below 20 percent (damageToLetThrough = health - 0.2 * max is negative, so the amount it subtracts
        // exceeds the hit); the combat pipeline refuses a negative amount, so the hit is blocked in full instead (the hand-written
        // Mana Barrier made the same choice, "Reconstruction: both quantities are clamped at zero").
        // A hook whose add the table refused keeps neither its buff nor its write (BbHookFrame.GroupDenied, the rule
        // BbItemModule.RunDamageChain applies to a held proc group): Spells/WillRevive.luaobj's revival writes
        // CurHealth - 1 here and would otherwise leave the hero at one health with no Guardian Angel.
        return frame.GroupDenied ? amount : scope.Vars["DamageAmount"] is double changed ? Math.Max(0.0, changed) : amount;
    }

    /// <summary>
    /// <c>BuffOnPreDealDamage</c> of the script buffs on the unit that deals damage: Owner and Attacker are the buff's holder, who is the
    /// damage source (ExhaustDebuff scales <c>DamageAmount</c> by its holder's outgoing hits, GlobalDrain heals <c>Attacker</c>), Target the
    /// victim; the hook reads <c>DamageAmount</c> and <c>DamageType</c> and may write <c>DamageAmount</c> back.
    /// </summary>
    public double RunBuffPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var instance in Buffs.WithHook(source, "BuffOnPreDealDamage"))
        {
            var caster = _host.Spells.World.Get(instance.CasterNetId);
            var scope = NewScope(source, source, target, caster, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["DamageAmount"] = amount;
            scope.Vars["DamageType"] = DamageTypeNumber(type);
            var frame = BuffFrame(instance, "BuffOnPreDealDamage", kind, inDamage: true);
            Run(instance.Script, "BuffOnPreDealDamage", scope, frame);
            if (!frame.GroupDenied && scope.Vars["DamageAmount"] is double changed) amount = changed;
        }
        return amount;
    }

    private BbHookFrame BuffFrame(BbBuffInstance instance, string hook, DamageSourceKind? damage, bool inDamage, bool periodicDamage = false,
        bool attackOverride = false) =>
        new(hook, instance.Name, instance.Owner)
        {
            Caster = _host.Spells.World.Get(instance.CasterNetId), SpellSlot = instance.SpellSlot,
            SpellRank = instance.SpellRank, ManaCost = instance.ManaCost, Buff = instance, DamageSource = damage,
            InDamageHook = inDamage, DamageIsPeriodic = periodicDamage, IsAttackOverride = attackOverride,
        };

    // ------------------------------------------------------------------------------------------------------------
    // Character hooks
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Brings a hero under the engine: <c>SetVarsByLevel</c> at the hero's level, then <c>CharOnActivate</c>. Reconstruction:
    /// retail runs both when the character is created; here the composition (or a check) calls it once per hero.
    /// </summary>
    public void ActivateHero(Entity hero)
    {
        if (_scripts.Character is not { } script) return;
        RunSetVarsByLevel(hero);
        // Target is the hero too, like Owner, Attacker and Caster. Character scripts address the hero as "Target" in CharOnActivate:
        // Blitzcrank's ManaBarrierIcon (CharScriptBlitzcrank.luaobj instr 0185) and Talon's TalonMercy (CharScriptTalon.luaobj instr 0453)
        // are read; Dragon's ResistantSkinDragon and Warwick's EternalThirstIcon are from the earlier marker, not re-read. A nil target
        // leaves the passive unadded, so retail binds Target to the hero.
        var scope = NewScope(hero, hero, hero, hero, null, CharInstanceVars(hero));
        Run(script, "CharOnActivate", scope, new BbHookFrame("CharOnActivate", script.Name, hero) { Caster = hero });
    }

    /// <summary>
    /// <c>CharOnActivate</c> of a unit a script spawned with <c>BBSpawnMinion</c> (the character script of its <paramref name="skin"/>, when it has one):
    /// retail runs it when the character is created, as for a hero. The unit is Owner, Attacker, Target and Caster. Reconstruction: the stat deltas and
    /// statuses the hook asks for (<c>TestCube</c> hides itself, <c>Nidalee_Spear</c> cannot be targeted) are collected and kept on the unit by one
    /// permanent internal buff, the way <see cref="BbBuffRuntime"/> keeps a buff's; the buffs it adds (the Orianna ball's <c>OrianaGhost</c>,
    /// <c>OrianaGhostMinion</c> and the tracker) are ordinary script buffs.
    /// </summary>
    public void ActivateSpawned(Entity unit, string skin)
    {
        if (_scripts.SpawnedCharacter(skin) is not { } script) return;
        RunCharOnActivate(unit, script);
    }

    /// <summary>The permanent internal buff that carries the statuses and stat deltas of a spawned unit's <c>CharOnActivate</c>.</summary>
    internal const string SpawnedCharacterBuff = "BBCharacterScript";

    private void RunSetVarsByLevel(Entity hero)
    {
        if (_scripts.Character is not { } script) return;
        // The champion level indexes the *ByLevel tables of the character script (CharScriptAnnie: 18 entries).
        var scope = NewScope(hero, hero, null, hero, hero.Level, CharInstanceVars(hero));
        Run(script, "SetVarsByLevel", scope, new BbHookFrame("SetVarsByLevel", script.Name, hero) { Caster = hero });
    }

    public void OnLevelChanged(Entity hero) =>
        Guard("sink", "OnLevelChanged", () =>
        {
            if (string.Equals(hero.ChampionName, _scripts.Champion, StringComparison.OrdinalIgnoreCase)) RunSetVarsByLevel(hero);
            RunLevelUp(hero);
        });

    // ------------------------------------------------------------------------------------------------------------
    // Spell hooks
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>SelfExecute</c>: once per accepted cast, at the cast frame: when the spell's windup ends (an instacast: at the cast), after the
    /// cost and cooldown were spent and the cast was announced, and before the spell's missile leaves or its <c>TargetExecute</c> runs.
    /// A cast cancelled in its windup (the caster dies, the target is lost) never reaches it.
    /// Reconstruction (18 October 2026, decided from the scripts): the hook name is server-only, but every script whose SelfExecute does
    /// the visible work of a windup spell needs the cast frame: EzrealArcaneShift (CastTime 0.25) faces the point and teleports,
    /// InfernalGuardian (0.25) draws the landing effect and spawns Tibbers, TaricHammerSmash scans and damages from it with no
    /// TargetExecute, and 62 spells cast their child missile with BBSpellCast from it; no shipped script needs it before the windup.
    /// The client has one shared function for the cast frame, the client's cast-frame function, which creates the
    /// missile or runs the effect for the ends alike; the script's SelfExecute is its server side. The earlier rule (windup spells with a
    /// TargetExecute ran it at the cast) matched the hand-written Annie, whose W and R follow this rule next.
    /// </summary>
    public void RunSelfExecute(BbScript script, Entity hero, int slot, int rank, double manaCost, Position3 position, Position3 end,
        Entity? target = null, Entity? channelTarget = null, BbLoadoutBinding? binding = null, bool itemActive = false, int itemId = 0)
    {
        // Target is the unit the cast was aimed at, the caster itself for a self cast: the shipped self spells add their buffs to
        // "Target" (BurningAgony, Defile, FlashFrost, LuxPrismaticWave) and stop its channel (SummonerFlash).
        var scope = NewScope(hero, hero, target ?? hero, hero, rank, binding: binding);
        scope.SpellVars = SpellVars(script);
        // The client's windup cast stays active until its FinishCast: a BBSetSpell on this slot from the cast frame retires it first (WindupCastFrame).
        var windup = hero.UnitType == UnitType.Hero && _host.Spells.Data.HasSpell(script.Name) && !_host.Spells.Data.Spell(script.Name, 5).IsInstant;
        var outerFrame = WindupCastFrame;
        if (windup) WindupCastFrame = (hero.NetId, slot);
        try
        {
            RunInSpell(script, "SelfExecute", scope,
                SpellFrame("SelfExecute", script, hero, slot, rank, manaCost, position, end, itemActive: itemActive, itemId: itemId));
        }
        finally { WindupCastFrame = outerFrame; }
        // A spell with a ChannelDuration channels from its cast frame on (Janna's Monsoon): ChannelingStart runs after SelfExecute.
        if (hero.UnitType == UnitType.Hero && BbScriptChannels.DurationOf(script) > 0.0)
            _host.Channels.Begin(script, hero, slot, rank, manaCost, position, end, target ?? channelTarget);
    }

    /// <summary>
    /// Runs a hook of a spell script (or, for a buff's update, of the buff script that deals its ticks) with that script as the ambient
    /// one (what <c>BuffOnBeingSpellHit</c> reads as <c>SpellVars</c> and <c>GetIsAttackOverride</c>). <paramref name="hit"/> is the unit the spell reaches (<c>TargetExecute</c>): its spell shields take
    /// the hit first, as one native step before the script runs (the one place every script spell goes through).
    /// </summary>
    private bool RunInSpell(BbScript script, string hook, BbScope scope, BbHookFrame frame, Entity? hit = null, IReadOnlyList<int>? resumeAfter = null)
    {
        var outer = BbSpellContext.Current;
        var outerAttack = BbSpellContext.IsAttack;
        BbSpellContext.Current = script;
        BbSpellContext.IsAttack = frame.IsAttackOverride;
        try
        {
            // Reconstruction (18 October 2026): the stock client holds no BuffOnBeingSpellHit string, so when the native raises it is
            // not recoverable. The shipped shields (SpellShield, BansheesVeil) pop on a hostile spell that is not an attack override,
            // refuse every hostile buff add afterwards and zero all damage until their next update; a stun that comes before the
            // damage (Dazzle, Rune Prison) or a spell with no damage (Starcall's shred, Infuse's silence) can only be refused if the
            // pop comes first, so the hit is raised where the engine hands the unit to TargetExecute. ConsumeSpellShield reads the
            // running spell's DoesntBreakShields and DoesntTriggerSpellCasts from the context set above.
            var absorbed = hit is not null && frame.Caster is { } caster && caster.Team != hit.Team && !frame.IsAttackOverride &&
                _host.Spells.Combat.ConsumeSpellShield(caster, hit, force: false);
            Run(script, hook, scope, frame, resumeAfter: resumeAfter);
            return absorbed;
        }
        finally
        {
            BbSpellContext.Current = outer;
            BbSpellContext.IsAttack = outerAttack;
        }
    }

    /// <summary>
    /// <c>TargetExecute</c>: once per affected unit at the spell's effect time (instant: at the cast; windup: when it
    /// ends; missile: on arrival). Owner, Attacker and Caster are the casting hero, Target the unit.
    /// </summary>
    public void RunTargetExecute(BbScript script, Entity hero, int slot, int rank, double manaCost, Position3 position,
        Position3 end, Entity target, int targetsHit = 0, bool attackOverride = false, uint missileNetId = 0,
        BbLoadoutBinding? binding = null, bool itemActive = false, int itemId = 0)
    {
        var scope = NewScope(hero, hero, target, hero, rank, binding: binding);
        scope.SpellVars = SpellVars(script);
        // The spell a BuffOnPreAttack cast in place of a swing (Xin Zhao's Three Talon Strike) reads that swing's roll as HitResult, once (Entity.ReplacedSwing).
        if (hero.ReplacedSwing is { } replaced && string.Equals(hero.SwingReplacedBy, script.Name, StringComparison.Ordinal))
        {
            scope.Vars["HitResult"] = HitResultOf(replaced);
            hero.ReplacedSwing = null;
            hero.SwingReplacedBy = null;
        }
        // A missile spell's hit hook reads the missile it rides as MissileNetworkID (62 shipped BBDestroyMissile calls name it).
        if (missileNetId != 0) scope.Vars["MissileNetworkID"] = (double)missileNetId;
        var frame = SpellFrame("TargetExecute", script, hero, slot, rank, manaCost, position, end, targetsHit, attackOverride,
            missileNetId, itemActive, itemId);
        var absorbed = RunInSpell(script, "TargetExecute", scope, frame, hit: target);
        // A spell shield that popped on this hit swallowed it: the unit was not hit, so the default hit pass draws nothing
        // (the hand-written item missiles of the old code drew their impact effect only inside the same gate as their damage
        // and their slow; the script is silent about the inibin's effects, so the old gate is kept). The hook body above has
        // run anyway and its damage and buff adds are refused by the shield window.
        if (!absorbed) DrawDefaultHitEffects(script, target, frame);
        RunSpellHit(script, hero, position, end, target, targetsHit, missileNetId);
    }

    /// <summary>
    /// <c>BuffOnSpellHit</c>: a spell cast by the holder of a script buff hit <paramref name="target"/>. Owner is the holder (the unit that cast the
    /// spell: Janna's wave is cast by the TestCube she spawned, whose <c>HowlingGaleSpell</c> buff carries the whole effect), Target the unit hit,
    /// Attacker and Caster the buff's caster (the hook says so itself with <c>BBSetBuffCasterUnit</c>: the damage is the hero's, with the hero's
    /// ability power and recap). The spell that hit is the ambient one (<c>SpellVars</c>, shield flags); the shield pop was raised with the hit
    /// (<see cref="RunInSpell"/>), before this hook runs. Reconstruction: the stock client has no BuffOnSpellHit string (10 shipped scripts:
    /// Janna's four wave tiers and three of Yorick's borrowed scripts); the hook runs after the spell's own <c>TargetExecute</c>, once per unit hit.
    /// </summary>
    private void RunSpellHit(BbScript spell, Entity holder, Position3 position, Position3 end, Entity target, int targetsHit, uint missileNetId)
    {
        foreach (var instance in Buffs.WithHook(holder, "BuffOnSpellHit"))
        {
            var buffCaster = _host.Spells.World.Get(instance.CasterNetId) ?? holder;
            var scope = NewScope(holder, buffCaster, target, buffCaster, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["LifeTime"] = instance.LifeTime;
            scope.SpellVars = new BbTable();
            BbSpellContext.Fill(scope.SpellVars, spell);
            var frame = new BbHookFrame("BuffOnSpellHit", instance.Name, holder)
            {
                Caster = buffCaster, SpellSlot = instance.SpellSlot, SpellRank = instance.SpellRank, ManaCost = instance.ManaCost,
                CastPosition = position, CastEndPosition = end, SpellName = spell.Name, Buff = instance, TargetsHit = targetsHit,
                MissileNetId = missileNetId,
            };
            var outer = BbSpellContext.Current;
            var outerAttack = BbSpellContext.IsAttack;
            BbSpellContext.Current = spell;
            BbSpellContext.IsAttack = false;
            try { Run(instance.Script, "BuffOnSpellHit", scope, frame); }
            finally
            {
                BbSpellContext.Current = outer;
                BbSpellContext.IsAttack = outerAttack;
            }
        }
    }

    /// <summary>
    /// The default hit pass of the stock server (its hit and after entries), which the
    /// shipped scripts never call themselves (no script holds a <c>BBDrawDefaultHitEffects</c> block): the spell inibin's
    /// <c>HitEffectName</c> (<c>HaveHitEffect</c>, on <c>HitBoneName</c> when <c>HaveHitBone</c>) and <c>AfterEffectName</c>
    /// (<c>HaveAfterEffect</c>) are drawn on each unit the spell hits. Evidence: across the shipped spells a script contains the
    /// effect named by an enabled field only 10 times out of 205 (Starcall's falling star, Imbue's heal flash on the ally, Astral
    /// Blessing, Dazzle, Reckoning, Divine Blessing have no effect block at all), and the client's own copy of the pass
    /// is an "InvalidClientCall" assertion: the client never draws them for a script cast.
    /// Reconstruction: the effect is bound to the unit (the stock pass places it at the hit bone or at the side facing the caster),
    /// the hit pass runs after the script and neither repeats an effect the script drew on the same unit.
    /// </summary>
    private void DrawDefaultHitEffects(BbScript script, Entity target, BbHookFrame frame)
    {
        var data = DefaultEffects(script.Name);
        if (data is null) return;
        // Batch 410 (finding 85): Annie's InfernalGuardian torch in Pantheon's Grand Skyfall inibins is a copy from another champion, so the
        // pass withholds it the way Warwick's Meditate copy is withheld (BbBuffRuntime.CopiedEffectCreates). NOT RETAIL: the shipped inibins
        // name it as the hit effect; owner decision, 27 October 2026: a foreign torch copied from another champion is a bug.
        if (data.Hit is { } hit && !BbBuffRuntime.IsCopiedEffectCreate(script.Name, hit.Effect))
            DrawDefaultEffect(hit.Effect, hit.Bone, target, frame, world: true);
        if (data.After is { } after && !BbBuffRuntime.IsCopiedEffectCreate(script.Name, after)) DrawDefaultEffect(after, null, target, frame);
    }

    /// <param name="world">
    /// Batch 582 (owner finding 124, "Veigar R post-hit VFX weird placement"): the hit effect is placed the way the stock client places one in its
    /// own hit pass (the cast routine, for an attack): the effect routine with flags 8 (no attachment, the world's orientation), at
    /// the point, which is the hit bone's world position when the spell has HaveHitBone, else the unit's own position (a hero's or a
    /// minion's offset is 0). Sent bound to the bone, the effect took the joint's orientation (the attachment's
    /// orientation, which uses the bone's matrix): Ezreal's <c>root</c> stands 95 above his feet (86.3 times SkinScale 1.1) with its local up nearly level
    /// (Ezreal.skl), so Primordial Burst's upward flash (permission_mana_flare_tar, post-offsets and velocities along +y) went sideways from his hip.
    /// Reconstruction: the server has no animated skeleton, so the bone's point is its bind-pose height above the unit (GameData.JointHeight) times
    /// the unit's model scale; the after effect stays attached to the unit, as the client's second create (flags 0) is.
    /// </param>
    private void DrawDefaultEffect(string effect, string? bone, Entity target, BbHookFrame frame, bool world = false)
    {
        if (!_host.Spells.Data.HasClientParticle(effect)) return;
        var stem = effect.Contains('.') ? effect[..effect.LastIndexOf('.')] : effect;
        if (frame.Effects.Any(done => done.Unit == target.NetId &&
                                      string.Equals(done.Name.Contains('.') ? done.Name[..done.Name.LastIndexOf('.')] : done.Name, stem,
                                          StringComparison.OrdinalIgnoreCase))) return;
        // Reconstruction: the server's order of a lethal hit and its death/effect delivery is not recovered; a lethal hit has taken the target's health before this pass runs, and the replication layer delivers the
        // target's death ahead of the effect, so the client would bind the particle to a unit that is already dead. Owner playtest
        // of 17 October (Kayle E, game 8ff36171): the nova and hit flash showed on every attacked unit except the one that died,
        // and the log shows each of those killing blows sent as Die, then the bound effect. A dying target gets the effect at its
        // position instead, bound to nothing. Retail draws the pair on the client (the cast routine).
        var dying = !target.Alive || target.Health <= 0.0;
        if (world)
        {
            var character = target.ScriptCharacterData?.SkinName ?? (target.UnitType == UnitType.Hero ? target.ChampionName : target.BoundCharacter ?? target.Name);
            var height = bone is null ? 0.0 : (_host.Spells.Data.JointHeight(character, target.SkinId, bone) ?? 0.0) * Amberline.Simulation.Buffs.ModelScale.Of(target);
            _host.Spells.Effects.CreateHitEffect(effect, target.Position, dying ? 0u : target.NetId, height);
        }
        else
            _host.Spells.Effects.CreateEffect(effect, target.Position, targetNetId: dying ? 0u : target.NetId,
                bindNetId: dying ? 0u : target.NetId, boneName: dying ? null : bone);
        frame.Effects.Add((effect, target.NetId));
    }

    private sealed record DefaultEffectData((string Effect, string? Bone)? Hit, string? After);
    private readonly Dictionary<string, DefaultEffectData?> _defaultEffects = new(StringComparer.OrdinalIgnoreCase);

    private DefaultEffectData? DefaultEffects(string spellName)
    {
        if (_defaultEffects.TryGetValue(spellName, out var cached)) return cached;
        DefaultEffectData? result = null;
        try
        {
            var data = _host.Spells.Data.Spell(spellName, 5);
            if (data.DefaultHitEffect is not null || data.DefaultAfterEffect is not null)
                result = new DefaultEffectData(data.DefaultHitEffect, data.DefaultAfterEffect);
        }
        catch (Amberline.Data.InibinException) { }
        return _defaultEffects[spellName] = result;
    }

    private static BbHookFrame SpellFrame(string hook, BbScript script, Entity hero, int slot, int rank, double manaCost,
        Position3 position, Position3 end, int targetsHit = 0, bool attackOverride = false, uint missileNetId = 0,
        bool itemActive = false, int itemId = 0) =>
        new(hook, script.Name, hero)
        {
            Caster = hero, SpellSlot = slot, SpellRank = rank, ManaCost = manaCost, CastPosition = position, CastEndPosition = end,
            SpellName = script.Name, TargetsHit = targetsHit, IsAttackOverride = attackOverride, MissileNetId = missileNetId,
            ItemActive = itemActive, ItemId = itemId, CastRunning = true,
        };

    /// <summary>
    /// <c>CanCast</c>: the spell script's own gate (Starcall: some enemy unit within 610). Runs after the data gates (rank,
    /// cooldown, mana) and returns false only when the script stored <c>false</c> with <c>BBSetReturnValue</c>; no hook, no
    /// value, or a script that raised all allow the cast. Reconstruction: the stock client has no CanCast string, so
    /// where the server asks is derived from the hook's name and the shipped Starcall script.
    /// </summary>
    public bool RunCanCast(BbScript script, Entity hero, int slot, int rank, double manaCost, BbLoadoutBinding? binding = null)
    {
        if (script.Event("CanCast") is null) return true;
        var scope = NewScope(hero, hero, null, hero, rank >= 1 ? rank : null, binding: binding);
        scope.SpellVars = SpellVars(script);
        var position = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        Run(script, "CanCast", scope, SpellFrame("CanCast", script, hero, slot, rank, manaCost, position, position));
        return scope.ReturnValue is not false;
    }

    /// <summary>
    /// <c>AdjustCooldown</c>: the spell script's say over the cooldown a cast starts (Swain's Metamorphism, Anivia's Glacial Storm and Dr.
    /// Mundo's Burning Agony give the toggle-on press 0.5, 0 and 0 seconds and let the toggle-off press take the spell's own). The value the
    /// hook stores with <c>BBSetReturnValue</c> is the whole cooldown; no hook, no number or a script that raised leaves the engine's. Runs
    /// when the cast is accepted, before its own <c>SelfExecute</c>, so a toggle's buff is still what the previous press left.
    /// Reconstruction: the stock client has no AdjustCooldown string, so where the server asks is derived from the hook's name and the
    /// shipped scripts. The summoner spells' hooks apply their mastery bonus themselves and the champion cooldown reduction is not applied
    /// to a returned value, which is how the summoner cooldowns already run (<c>BbSummonerModule.AdjustCooldown</c>).
    /// </summary>
    public double? RunAdjustCooldown(BbScript script, Entity hero, int slot, int rank, BbLoadoutBinding? binding = null)
    {
        if (script.Event("AdjustCooldown") is null) return null;
        var scope = NewScope(hero, hero, hero, hero, rank >= 1 ? rank : null, binding: binding);
        scope.SpellVars = SpellVars(script);
        var position = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        Run(script, "AdjustCooldown", scope, SpellFrame("AdjustCooldown", script, hero, slot, rank, 0.0, position, position));
        return scope.ReturnValue is double adjusted && double.IsFinite(adjusted) ? Math.Max(0.0, adjusted) : null;
    }

    /// <summary>
    /// <c>AdjustCastInfo</c>: the spell script's say over how far this cast reaches (Tristana's Buster Shot adds a reach per level while Draw
    /// a Bead stands). The range comes back from <c>BBSetSpellCastRange</c>; a script that sets none, has no hook or raised leaves the
    /// spell's own. Reconstruction (hook name and timing: no client string); the summoner hand-written Flash applies its range bonus itself.
    /// </summary>
    public double? RunAdjustCastInfo(BbScript script, Entity hero, int slot, int rank, BbLoadoutBinding? binding = null)
    {
        if (script.Event("AdjustCastInfo") is null) return null;
        var scope = NewScope(hero, hero, hero, hero, rank >= 1 ? rank : null, binding: binding);
        scope.SpellVars = SpellVars(script);
        var position = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        var frame = SpellFrame("AdjustCastInfo", script, hero, slot, rank, 0.0, position, position);
        Run(script, "AdjustCastInfo", scope, frame);
        return frame.CastRange is { } range && double.IsFinite(range) ? Math.Max(0.0, range) : null;
    }

    /// <summary>
    /// <c>UpdateSelfBuffActions</c> of the character script: the hero's own per-update hook (Soraka's Consecration aura
    /// refreshes its allies' buff once per second from it; Blitzcrank R and Ezreal Q/R publish their tooltip numbers from
    /// it). Runs every spell update while the hero exists, dead or alive, because the script guards on <c>IS_NOT_DEAD</c>
    /// itself and what a corpse's poll does to the corpse is the script's own business — the spellbook tooltip values
    /// update every second including while dead; see docs/champions/module-layout.md.
    /// <para>
    /// Owner decision 20 Oct 2026, "dead champions' passives stop": while a hero is dead its ally-affecting passives and
    /// auras stop until it respawns. Retail footage shows Zilean's passive leaving his allies while he is dead, which the
    /// shipped scripts produce where they test the owner — CharScriptZilean's HeightenedLearning, CharScriptJanna's
    /// Tailwind and CharScriptSoraka's Consecration all gate their re-add on <c>CO_IS_NOT_DEAD</c> and hold the ally's
    /// aura as a short buff this poll renews (5 s every 4 s, 1.15 s every 1 s), so a corpse's poll lapses it. That is the
    /// retail case: the scripts are the rule and the engine adds nothing. The one script with no dead check of its own is
    /// CharScriptNidalee's <c>BBIncExp</c>, which would hand her allies experience from a corpse's poll (batch 108
    /// Reconstruction): <c>BBIncExp</c> refuses a dead owner (Reconstruction: owner decision 20 Oct 2026; the retail native's refusal is not recovered). A temporary life
    /// (Yorick's Omen of Death) is a life again: the hero stands up and its poll runs for that life. Buff hooks are
    /// untouched, so the death mechanics still run on the corpse — <c>BuffOnDeath</c>, the zombie lives and the revives
    /// (Chronoshift's <c>ChronoRevive</c>, Guardian Angel) — and so are the item copies' own polls
    /// (<c>BbItemModule.Poll</c>, which must not lapse a corpse's <c>DoranT2Health</c> or its Will Revive marker).
    /// </para>
    /// Reconstruction (hook timing is server-only). Batch 246: a hero's poll runs on its self-buff line (<paramref name="due"/>,
    /// <see cref="BbSelfUpdate"/>: a bare hook on the 0.25 s line, a hook that owns a periodic every update); batch 256: a bound unit's
    /// poll (<see cref="BbUnitCharacterModule.Poll"/>) runs on its own line by the same rule.
    /// </summary>
    public void RunUpdateSelfBuffActions(Entity unit, bool due = true)
    {
        if (CharacterOf(unit) is not { } script || script.Event("UpdateSelfBuffActions") is null || !Owns(unit)) return;
        if (!BbSelfUpdate.Runs(script, "UpdateSelfBuffActions", due)) return;
        // The shipped scripts name the hero as Owner, Attacker or Target interchangeably (tooltip calls use all three).
        var scope = NewScope(unit, unit, unit, unit, null, CharInstanceVars(unit));
        Run(script, "UpdateSelfBuffActions", scope, new BbHookFrame("UpdateSelfBuffActions", script.Name, unit) { Caster = unit });
    }

    private readonly Dictionary<Entity, string> _charStatSignatures = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <c>UpdateSelfBuffStats</c> of the character script: the unit's own stats hook (Vayne's Night Hunter adds movement speed while she
    /// hunts, and adds and removes the hunted marker and the speed-part buffs from the same hook; a turret's samples its armor penetration).
    /// Retail polls it whenever the stat
    /// accumulators are rebuilt; here it runs on the same self-buff line as <see cref="RunUpdateSelfBuffActions"/> (batch 246, <paramref name="due"/>).
    /// The <c>BBIncStat</c> deltas and statuses it asks for are collected into <see cref="Entity.CharacterScriptStats"/>, which the hero stat
    /// calculator reads next to the hero's buffs; a different result refreshes the hero and tells its client. The statuses are also written
    /// onto the unit itself on every poll (<see cref="BbBuffRuntime.ApplyCharacterScriptStatuses"/>; batch 174), because the unit-state
    /// readers take them from the unit's script status (batch 201) and not from the stat payload. A dead hero's hook does not
    /// run and its deltas are dropped (Reconstruction, batch 98: nothing in the scripts or the client says whether retail polls a corpse;
    /// the owner decision 20 Oct 2026 stops only the ally-affecting passives, and this hook has none — see
    /// <c>RunUpdateSelfBuffActions</c>).
    /// </summary>
    public void RunUpdateSelfBuffStats(Entity unit, bool due = true)
    {
        if (CharacterOf(unit) is not { } script || script.Event("UpdateSelfBuffStats") is null || !Owns(unit)) return;
        if (!BbSelfUpdate.Runs(script, "UpdateSelfBuffStats", due)) return;
        var collector = new BbStatCollector();
        if (unit.Alive)
        {
            var scope = NewScope(unit, unit, unit, unit, null, CharInstanceVars(unit));
            // IsResample: the poll is never the first sample of anything, so natives that apply once (BBIncMaxHealth) stay out of it.
            var frame = new BbHookFrame("UpdateSelfBuffStats", script.Name, unit) { Caster = unit, Stats = collector, IsResample = true };
            // A hook that raised leaves a half-filled collector: the poll is skipped whole (production safety, see Fail).
            try { Run(script, "UpdateSelfBuffStats", scope, frame, rethrow: true); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                Fail(script.Name, "UpdateSelfBuffStats", ex);
                return;
            }
        }
        foreach (var colours in collector.ParColorCalls) _host.ApplyParColorOverride(unit, colours);
        // Batch 174: a status the sample writes (CharScriptWorm's SetCanAttack/SetCanMove false) is a contribution of that sample,
        // the same way a buff payload's statuses are one of its sample: it is held while the hook asks for it and released when the
        // hook stops. It is written on every poll, not only on a signature change, because another script's rest write releases every
        // script status of the unit (BbBuffRuntime.ReleaseStatus), and the character's own sample is what puts it back.
        Buffs.ApplyCharacterScriptStatuses(unit, collector.Statuses);
        var signature = collector.Signature();
        if (_charStatSignatures.TryGetValue(unit, out var last) ? last == signature : signature.Length == 0) return;
        _charStatSignatures[unit] = signature;
        unit.CharacterScriptStats = signature.Length == 0 ? null : BbBuffRuntime.PayloadOf(collector);
        if (unit.UnitType == UnitType.Hero)
            _host.Spells.HeroStats?.Refresh(unit, push: _host.Spells.Effects.IsReferenceReady(unit));
        else ApplyUnitCharacterStats(unit);
    }

    /// <summary>
    /// The non-hero twin of the hero stat write-back (<c>HeroStatsServices</c>, which copies the penetration and
    /// dodge-piercing columns of the rebuilt stats onto the entity fields): <c>CombatService.ApplyMitigation</c> and the dodge
    /// roll read them as entity fields for every attacker. Batch 467: the penetration is the sample composed with the unit's
    /// buffs (<see cref="Amberline.Simulation.Buffs.BuffLifecycle.RecomputeNpcPenetration"/>, which the buff stat path runs
    /// too); before, the sample assigned it and a buff's penetration never reached a non-hero. The other columns are the
    /// sample's alone (a non-hero wears no items
    /// and has no runes), so they assign, and only the columns the sample itself names (<c>BuffPayload.Has</c>): a hook that
    /// says nothing about a field leaves whatever else set it alone (the hand-written path a unit with no script keeps).
    /// <para>
    /// <c>DodgePiercing</c> comes from the same payload when the sample marked it. <c>BBSetDodgePiercing</c> outside a stats
    /// hook writes the entity field directly (<see cref="BbNativeHandlers"/>), which is where a turret's activation puts it.
    /// </para>
    /// </summary>
    private void ApplyUnitCharacterStats(Entity unit)
    {
        Amberline.Simulation.Buffs.BuffLifecycle.RecomputeNpcPenetration(unit);
        if (unit.CharacterScriptStats is not { } payload) return;
        if (payload.Has(BuffPayloadField.DodgePiercing)) unit.DodgePiercing = payload.DodgePiercing > 0.0;
        // Life-steal is the same kind of column (batch 159 part 2): CharScriptWraith's UpdateSelfBuffStats samples
        // BBIncStat IncPercentLifeStealMod 1.0, which is the unit's whole life-steal pool (the hand-written
        // LifeSteal = 1.0 seed the camp spawn used to set). The field is the fraction the heal-on-hit reads.
        if (payload.Has(BuffPayloadField.LifeSteal)) unit.LifeSteal = payload.LifeSteal;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Character and buff hooks of attacks, casts and hits (M3). Every timing is a Reconstruction: the stock client has none of
    // these hook names, the server dispatch is not recoverable, and each rule follows from the shipped scripts' own variables.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BuffOnAllowAdd</c> of one buff on the unit about to receive a new buff: false when the script stored <c>false</c> with
    /// <c>BBSetReturnValue</c>. A hook that raised allows the add. Owner is the holder, Attacker the unit adding it. The hook may rewrite
    /// <c>Duration</c> (Judgment halves a slow): the add goes on with the value the hook left in <paramref name="duration"/>.
    /// </summary>
    public bool RunAllowAdd(BbBuffInstance instance, Entity adder, string buffName, ref double duration, double buffType, double maxStack)
    {
        var scope = NewScope(instance.Owner, adder, instance.Owner, _host.Spells.World.Get(instance.CasterNetId) ?? instance.Owner,
            instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
        // The shipped hooks read the incoming buff as Type (its BUFF_* number: Boots of Mobility, Noxious Trap and 60 other tables test
        // Fear, Net, Silence ...), MaxStack (76 marks the Shaco box rule) and Duration (37037 is BBBreakSpellShields' marker).
        scope.Vars["Type"] = buffType;
        scope.Vars["MaxStack"] = maxStack;
        scope.Vars["Duration"] = duration;
        scope.Vars["BuffName"] = buffName;
        Run(instance.Script, "BuffOnAllowAdd", scope, BuffFrame(instance, "BuffOnAllowAdd", null, inDamage: false));
        // Every held buff's rewrite is written back, and the next hook reads it: several cuts multiply (batch 296). The stat's copy of
        // tenacity is Hardening.luaobj's own hook (1 - GetPercentHardnessMod; BuffLifecycle.AddBuff has no native copy any more), so
        // MercuryTreads' x0.65 is written back as shipped too: Items/3111.luaobj adds that buff for the 10 s after the buy (OnActivate), and
        // inside them a hostile control on the wearer is cut by both (0.65 x 0.65), as the two shipped scripts do.
        if (scope.Vars["Duration"] is double rewritten && double.IsFinite(rewritten)) duration = rewritten;
        return scope.ReturnValue is not false;
    }

    /// <summary>
    /// The character script's <c>BuffOnAllowAdd</c> for an add onto its champion: Owner the hero, Attacker the unit adding, the add's
    /// <c>Type</c>, <c>MaxStack</c>, <c>Duration</c> and <c>BuffName</c>. The character is no buff instance, so <see cref="BbBuffRuntime.AllowAdd"/>
    /// never asked it before. The Duration rewrite is written back as a held buff's is (CharScriptIrelia's Ionian Fervor halves nothing but
    /// the disables of an enemy add, multiplied by CharVars.CCReduction that her UpdateSelfBuffStats sets from the enemies near her).
    /// </summary>
    public bool RunCharacterAllowAdd(Entity target, Entity adder, string buffName, ref double duration, double buffType, double maxStack)
    {
        if (CharacterOf(target) is not { } script || script.Event("BuffOnAllowAdd") is null || !Owns(target)) return true;
        var scope = NewScope(target, adder, target, adder, null, CharInstanceVars(target));
        scope.Vars["Type"] = buffType;
        scope.Vars["MaxStack"] = maxStack;
        scope.Vars["Duration"] = duration;
        scope.Vars["BuffName"] = buffName;
        var frame = new BbHookFrame("BuffOnAllowAdd", script.Name, target) { Caster = adder };
        Run(script, "BuffOnAllowAdd", scope, frame);
        if (scope.Vars["Duration"] is double rewritten && double.IsFinite(rewritten)) duration = rewritten;
        return scope.ReturnValue is not false;
    }

    /// <summary>
    /// The same <c>BuffOnAllowAdd</c> for an inventory copy rather than a held buff (<c>BbItemModule.AllowBuffAdd</c>):
    /// Owner and Target the unit the add would land on, Attacker the unit adding it, the copy's own
    /// <c>InstanceVars</c> the table. Items/3117.luaobj is the shipped user: a hostile control on its wearer starts
    /// BootsOfMobilityDebuff first, and the add it makes runs this same hook of the other copies.
    /// </summary>
    public bool RunAllowAddOf(BbLoadoutBinding binding, Entity target, Entity adder, string buffName, ref double duration,
        double buffType, double maxStack)
    {
        var scope = NewScope(target, adder, target, adder, binding.Rank, binding: binding);
        scope.Vars["Type"] = buffType;
        scope.Vars["MaxStack"] = maxStack;
        scope.Vars["Duration"] = duration;
        scope.Vars["BuffName"] = buffName;
        Run(binding.Script, "BuffOnAllowAdd", scope, new BbHookFrame("BuffOnAllowAdd", binding.Script.Name, target)
        {
            Caster = target,
        });
        if (scope.Vars["Duration"] is double rewritten && double.IsFinite(rewritten)) duration = rewritten;
        return scope.ReturnValue is not false;
    }

    /// <summary>
    /// <c>BuffOnBeingSpellHit</c> of every script buff on <paramref name="target"/>: an enemy spell landed. Owner is the holder,
    /// Attacker the casting unit, <c>SpellVars</c> the flags of the spell script that is running. Returns whether a buff took the
    /// hit (it set <c>WillRemove</c> in its <c>InstanceVars</c>, the shipped shields' own marker).
    /// </summary>
    public bool RunBeingSpellHit(Entity target, Entity? source)
    {
        var absorbed = false;
        foreach (var instance in Buffs.WithHook(target, "BuffOnBeingSpellHit"))
        {
            // A shield that already popped (WillRemove, until its next update) has taken its hit: the same spell reaches the gate once
            // at TargetExecute and again with its damage, and the script would draw its proc effect a second time.
            if (instance.InstanceVars["WillRemove"] is true) continue;
            var caster = _host.Spells.World.Get(instance.CasterNetId) ?? instance.Owner;
            var scope = NewScope(instance.Owner, source, instance.Owner, caster, instance.SpellRank >= 1 ? instance.SpellRank : null,
                instance.InstanceVars);
            var spell = new BbTable();
            if (BbSpellContext.Current is { } running) BbSpellContext.Fill(spell, running);
            scope.SpellVars = spell;
            // GetCastInfo(GetIsAttackOverride) reads the hit spell, not the shield: an override attack never pops it.
            var frame = BuffFrame(instance, "BuffOnBeingSpellHit", null, inDamage: false, attackOverride: BbSpellContext.IsAttack);
            Run(instance.Script, "BuffOnBeingSpellHit", scope, frame);
            if (instance.InstanceVars["WillRemove"] is true) absorbed = true;
        }
        return absorbed;
    }

    /// <summary>
    /// <c>BBBreakSpellShields</c>: the base function adds the buff <c>SpellShieldMarker</c> with the duration 37037 to the unit (the unit
    /// itself is the adder), and <c>BuffOnAllowAdd</c> of every shield turns that add into a pop (<c>WillRemove</c>) and refuses it.
    /// Runs the hook of each script buff of the unit with that add; true when a shield popped.
    /// </summary>
    public bool RunBreakSpellShields(Entity target)
    {
        var popped = false;
        foreach (var instance in Buffs.WithHook(target, "BuffOnAllowAdd"))
        {
            var before = instance.InstanceVars["WillRemove"];
            var marker = BbBuffRuntime.SpellShieldMarkerDuration;
            RunAllowAdd(instance, target, BbBuffRuntime.SpellShieldMarker, ref marker, (double)BbBuffType.BUFF_Internal, 1.0);
            if (instance.InstanceVars["WillRemove"] is true && before is not true) popped = true;
        }
        return popped;
    }

    /// <summary>
    /// <c>CharOnHitUnit</c> and <c>BuffOnHitUnit</c> of a landed basic attack, run as the hit is resolved (before mitigation, see
    /// <see cref="ModifyBasicAttackDamage"/>); returns the damage the hit deals after them. Owner, Attacker and Caster are the attacking hero,
    /// Target the victim; <c>HitResult</c> is the swing's result (<see cref="HitResultOf"/>: HIT_Normal or HIT_Critical, a miss or
    /// dodge never lands) and <c>DamageAmount</c> the hit before mitigation, which the scripts may change: 49 of the 146 shipped on-hit scripts write it (monsters'
    /// x1.43 against minions, Sheen, Backstab, Mace of Spades), which only makes sense before the damage is taken.
    /// Reconstruction: 43 of the 146 shipped on-hit tables guard on HIT_Dodge and HIT_Miss, which suggests retail also ran the hooks
    /// for an avoided swing, but the other 103 do not guard, so running them on every miss would trigger those on a miss (Kayle's
    /// Righteous Fury, Master Yi's Double Strike, Nasus's Siphoning Strike); retail's dispatch is not in the client, so a miss or
    /// dodge keeps reaching no hook, and the guards stay unreachable. Batch 555: <c>Items/3124.luaobj</c> points the same way, since
    /// its <c>ItemOnHitUnit</c>, <c>ItemOnBeingDodged</c> and <c>ItemOnMiss</c> each add one unguarded Rageblade stack, which is one stack
    /// per swing only if a hit hook never sees an avoided one (so Ionic Spark's <c>LightningRodApplicator</c> counts landed hits only).
    /// <para>
    /// Engine gap closed 23 October 2026 (batch 164): the hooks read the hit before the engine's critical multiplier, and the
    /// <c>HitResult</c> they leave decides whether that multiplier is applied to what they left. The engine multiplies a critical
    /// hit as a whole (<see cref="OnAttackOverrideDamage"/>: VayneTumbleAttack divides its own ratio by the multiplier for that
    /// reason), so a hook that folds a HIT_Critical into a HIT_Normal takes the multiplier off its hit.
    /// <c>YorickSpectral.luaobj</c> <c>BuffOnHitUnit</c> does exactly that before it deals the Omen of War proc (and zeroes the
    /// swing), which is why an armed Omen of War swing is never a critical strike; the hand-written
    /// <c>CombatRules.IsEmpoweredAttack</c>, which skipped the roll for Yorick, is gone.
    /// </para>
    /// </summary>
    public double RunHitUnit(Entity attacker, Entity target, double rawDamage, BasicAttackOutcome outcome = BasicAttackOutcome.Normal)
    {
        // The hit flows through the hooks in order (the character script, then the buffs): each reads the amount the one before left in
        // DamageAmount, and the last one's is what the swing deals. Engine gap closed 19 October 2026: the hooks used to run after the damage and
        // their DamageAmount was lost.
        var rolledCritical = outcome == BasicAttackOutcome.Critical;
        var multiplier = rolledCritical && attacker.CritMultiplier > 0.0 ? attacker.CritMultiplier : 1.0;
        var damage = multiplier == 1.0 ? rawDamage : rawDamage / multiplier;
        var critical = rolledCritical;
        if (CharacterOf(attacker) is { } script && script.Event("CharOnHitUnit") is not null && Owns(attacker))
        {
            var scope = NewScope(attacker, attacker, target, attacker, null, CharInstanceVars(attacker));
            scope.Vars["HitResult"] = HitResultOf(outcome);
            scope.Vars["DamageAmount"] = damage;
            scope.Vars["DamageType"] = (double)BbDamageType.PHYSICAL_DAMAGE;
            var frame = new BbHookFrame("CharOnHitUnit", script.Name, attacker) { Caster = attacker };
            Run(script, "CharOnHitUnit", scope, frame);
            damage = ChangedHit(frame, scope, damage);
            critical = KeptCritical(frame, scope, critical);
        }
        foreach (var instance in Buffs.WithHook(attacker, "BuffOnHitUnit"))
        {
            // Caster is the buff's caster (what BBSetBuffCasterUnit reads): a ghoul's Logic buff and the revenant's buff were added by Yorick and
            // deal their hit as him. For a buff a hero holds from its own cast it is the attacker, as before.
            var buffCaster = _host.Spells.World.Get(instance.CasterNetId) ?? attacker;
            var scope = NewScope(attacker, attacker, target, buffCaster, instance.SpellRank >= 1 ? instance.SpellRank : null,
                instance.InstanceVars);
            scope.Vars["HitResult"] = HitResultOf(outcome);
            scope.Vars["DamageAmount"] = damage;
            scope.Vars["DamageType"] = (double)BbDamageType.PHYSICAL_DAMAGE;
            var hitFrame = BuffFrame(instance, "BuffOnHitUnit", DamageSourceKind.Basic, inDamage: false);
            Run(instance.Script, "BuffOnHitUnit", scope, hitFrame);
            damage = ChangedHit(hitFrame, scope, damage);
            critical = KeptCritical(hitFrame, scope, critical);
        }
        return critical && multiplier != 1.0 ? damage * multiplier : damage;
    }

    /// <summary>Whether the <c>HitResult</c> a hit hook left in its scope still says the hit is a critical strike.</summary>
    private static bool KeptCritical(BbScope scope, bool critical) =>
        scope.Vars["HitResult"] is double result ? result == (double)BbHitResult.HIT_Critical : critical;

    /// <summary>The same for one hook run: a refused run (<see cref="BbHookFrame.GroupDenied"/>) keeps neither its damage change nor its fold.</summary>
    private static bool KeptCritical(BbHookFrame frame, BbScope scope, bool critical) =>
        frame.GroupDenied ? critical : KeptCritical(scope, critical);

    /// <summary>The <c>DamageAmount</c> a hit hook left in its scope: a finite, non-negative number, else the amount it was given.</summary>
    internal static double ChangedHit(BbScope scope, double given) =>
        scope.Vars["DamageAmount"] is double changed && double.IsFinite(changed) ? Math.Max(0.0, changed) : given;

    /// <summary>
    /// The same for one hook run: a run whose add the table refused (<see cref="BbHookFrame.GroupDenied"/>) is declined in
    /// full, so it keeps neither its buff nor its damage change (the rule <c>BbItemModule.RunDamageChain</c> applies to a
    /// held proc group, and <c>BbBuffRuntime.Add</c> raises for a single add).
    /// </summary>
    internal static double ChangedHit(BbHookFrame frame, BbScope scope, double given) =>
        frame.GroupDenied ? given : ChangedHit(scope, given);

    /// <summary>
    /// The <c>SelfExecute</c> of the spell a script's <c>BBOverrideAutoAttack</c> made the hero's attack, run when the swing lands: an attack override
    /// is a cast of its extra slot, and the swing that lands is that cast. Umbra Blades' attack adds the 0.01 second marker whose removal ends the passive;
    /// the marker outlives the hit, so the hit itself still sees the passive. Reconstruction: a missile swing runs it at its hit, not at its launch
    /// (the shipped scripts name no launch hook of an attack).
    /// </summary>
    private void RunAttackSelfExecute(Entity attacker, Entity target)
    {
        if ((_hitAttacks.TryGetValue(attacker.NetId, out var swung) ? swung : attacker.ScriptAttackOverride) is not { } attack ||
            _scripts.Find(attack.SpellName) is not { } script || script.Event("SelfExecute") is null ||
            !string.Equals(attacker.ChampionName, _scripts.Champion, StringComparison.OrdinalIgnoreCase)) return;
        var here = new Position3(attacker.Position.X, 0.0, attacker.Position.Z);
        RunSelfExecute(script, attacker, attack.Slot, attack.Level, 0.0, here, here, target: target);
    }

    /// <summary>
    /// The <c>TargetExecute</c> of the spell a script's <c>BBOverrideAutoAttack</c> made the hero's attack (Kayle's Righteous Fury
    /// splash). The engine has already applied the attack itself, so the script's <c>DAMAGESOURCE_ATTACK</c> hit on the primary
    /// target is skipped (see <c>BbNativeHandlers.ApplyDamage</c>); everything else the script does runs. Level is the override's.
    /// </summary>
    public void RunAttackSpell(Entity attacker, Entity target, BasicAttackOutcome outcome = BasicAttackOutcome.Normal)
    {
        // The swing's spell is the one it was fired with: a hit hook of this same swing that ends the override (RelentlessAssaultMarker's
        // BuffOnHitUnit removes the marker, whose OnBuffDeactivate calls BBRemoveOverrideAutoAttack) does not take the spell's TargetExecute
        // away. In retail that hook runs inside the spell's own DAMAGESOURCE_ATTACK BBApplyDamage, and the script goes on after it
        // (Jax's third hit: 140 / 170 / 210 magic after the swing). Batch 273.
        _hitAttacks.Remove(attacker.NetId, out var swung);
        if ((swung ?? attacker.ScriptAttackOverride) is not { } attack || _scripts.Find(attack.SpellName) is not { } script ||
            script.Event("TargetExecute") is null ||
            !string.Equals(attacker.ChampionName, _scripts.Champion, StringComparison.OrdinalIgnoreCase)) return;
        // A live run made as the swing landed already did what came before the primary hit, in the script's order: this run finishes the script after it.
        _liveRuns.Remove(attacker.NetId, out var live);
        if (live is not null && live.Target != target.NetId) live = null;
        var scope = live?.Probe?.Scope ?? AttackScope(attacker, target, attack.Level);
        // RenektonExecute, TrundleQ, YorickSpectral and the Missfortune ricochet read the swing's result here and fold a crit into
        // their own damage (HitResult == HIT_Critical, then HitResult = HIT_Normal).
        if (live is null)
        {
            scope.Vars["HitResult"] = HitResultOf(outcome);
            scope.SpellVars = new BbTable();
            BbSpellContext.Fill(scope.SpellVars, script);
        }
        var here = new Position3(attacker.Position.X, 0.0, attacker.Position.Z);
        var frame = new BbHookFrame("TargetExecute", script.Name, attacker)
        {
            Caster = attacker, SpellSlot = attack.Slot, SpellRank = attack.Level, CastPosition = here, CastEndPosition = here,
            SpellName = script.Name, IsAttackOverride = true, AttackTarget = target,
        };
        if (live is null) RunInSpell(script, "TargetExecute", scope, frame);
        // A live run that failed half way did its effects; running the script again would do them twice, so the rest of it is dropped.
        else if (live.Probe is { Path: { } path }) RunInSpell(script, "TargetExecute", scope, frame, resumeAfter: path);
        // An attack's own hit and after particles are the client's: the cast routine draws them for a cast flagged as an auto attack,
        // and every swing of a script override leaves as such a cast (RunAttackCast). The server used to draw Righteous Fury's pair itself
        // (until 19 October 2026 it was a deviation: the 16 October playtest showed the nova never on the attacked unit, because the swing was a 0x0D that never ran the
        // override spell on the client). The W23 throwaway-client run of 19 October 2026 showed the nova and the hit flash from the client
        // alone once the swing was a cast, so the server pass was deleted.
        StartChainAttack(attacker, target, attack, script);
    }

    /// <summary>
    /// The pre-mitigation damage of a swing fired from a script's attack override, when the override spell's own primary attack hit is not a
    /// plain swing. <c>VayneTumbleAttack</c> computes its hit (<c>DamageVar</c> = total attack damage x (1 + the Q rank's ratio), the ratio
    /// divided by the critical multiplier when the script's own roll crit, because the engine multiplies a critical hit as a whole) and applies
    /// it as <c>DAMAGESOURCE_ATTACK</c>; the engine's basic attack is the one attack pipeline (crit, life steal, on-hit items), so the script's
    /// hit is not applied twice (<c>BbNativeHandlers.ApplyDamage</c> skips it) and its amount is read here instead.
    /// <para>
    /// The part of <c>TargetExecute</c> before that hit runs as a dry run (<see cref="BbDamageProbe"/>): it may only read and compute, and its
    /// random draw agrees with the swing's own critical strike. The hit's amount minus the plain total attack damage is the script's addition,
    /// multiplied by the critical multiplier for a critical swing, and it is added to the engine's amount. A script whose hit equals a plain
    /// swing (Kayle's Righteous Fury, Sivir's Ricochet, Annie's attacks: base attack damage plus the bonus) changes nothing. Reconstruction: how
    /// the stock server folds a script's attack hit into the swing is server-only; the formula is the one the script's crit branch implies.
    /// </para>
    /// <para>
    /// Batch 301: the hit's result is the one the script left in <c>HitResult</c> when it reached the hit. <c>BBApplyDamage</c>
    /// (the damage native) puts the calling script's own <c>HitResult</c> variable into the damage record next to its <c>IgnoreDamageCrit</c>
    /// (see <c>BbNativeHandlers.ApplyDamage</c>), so a script that makes a normal swing critical (Pantheon's Certain Death, <c>PantheonBasicAttack</c>,
    /// at fifteen percent health or less) gets the critical multiplier and the critical text, and one that folds a critical swing into a normal
    /// one loses it. The hero's own attack script (<c>Data*BaseAttack</c>, not an override) is read the same way, by a dry run, since it runs whole
    /// after the hit (<see cref="RunBasicAttackScript"/>).
    /// </para>
    /// </summary>
    public double OnAttackOverrideDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome)
    {
        _ignoreIncreaseMods = false;
        _scriptedHitResult = null;
        _liveRuns.Remove(attacker.NetId, out _);
        _hitAttacks.Remove(attacker.NetId);
        if (attackOverride && attacker.ScriptAttackOverride is { } fired && Owns(attacker)) _hitAttacks[attacker.NetId] = fired;
        var probe = attackOverride
            ? ProbeAttackHit(attacker, target, outcome, popsShields: true, live: true)
            : BasicAttackScript(attacker, outcome) is { } own ? ProbeHit(own, NewScope(attacker, attacker, target, attacker, null), attacker, target, outcome) : null;
        if (probe is not { Amount: { } primary }) return damage;
        var rolled = outcome == BasicAttackOutcome.Critical;
        var multiplier = attacker.CritMultiplier > 0.0 ? attacker.CritMultiplier : 1.0;
        // A critical HitResult multiplies the hit unless the call ignores critical strikes (IgnoreDamageCrit, the damage record's IgnoreCrit).
        var critical = probe.Scope?.Vars["HitResult"] is double result ? result == (double)BbHitResult.HIT_Critical && !probe.IgnoreCrit : rolled;
        if (critical != rolled)
        {
            damage = critical ? damage * multiplier : damage / multiplier;
            _scriptedHitResult = critical ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal;
        }
        if (!attackOverride) return damage;
        _ignoreIncreaseMods = probe.IgnoreIncreaseMods;
        var extra = primary - attacker.AttackDamage;
        if (Math.Abs(extra) < 1e-6) return damage;
        // A critical swing: the script divides its ratio by the multiplier (GetFlatCritDamageMod + 2) and the engine multiplies the whole hit.
        return Math.Max(0.0, damage + extra * (critical ? multiplier : 1.0));
    }

    private bool _ignoreIncreaseMods;
    private BasicAttackOutcome? _scriptedHitResult;

    /// <summary>
    /// The dry run of the part of the attack override's <c>TargetExecute</c> before its primary <c>DAMAGESOURCE_ATTACK</c> hit
    /// (<see cref="BbDamageProbe"/>), or null when the hero has no attack script, the script never reaches such a hit, or it fails.
    /// </summary>
    private BbDamageProbe? ProbeAttackHit(Entity attacker, Entity target, BasicAttackOutcome outcome, bool popsShields = false, bool live = false)
    {
        if (attacker.ScriptAttackOverride is not { } attack || !Owns(attacker) ||
            _scripts.Find(attack.SpellName) is not { } script || script.Event("TargetExecute") is null) return null;
        // Reconstruction (19 October 2026, Udyr): retail runs the attack spell's script in order, its own hit where the script has it. The engine deals
        // the swing itself, so what the script does before that hit has to happen before the swing lands (Turtle Stance adds the life steal buff
        // the hit has to see; Renekton spends his fury, Twisted Fate breaks the shield, Xin Zhao stacks the mark first). A live run does it, once.
        return ProbeHit(script, AttackScope(attacker, target, attack.Level), attacker, target, outcome, attack.Slot, attack.Level, popsShields, live);
    }

    /// <summary>The run of <see cref="ProbeAttackHit"/> for a given attack script and scope (a hero's own attack script runs dry, slot and rank unset).</summary>
    private BbDamageProbe? ProbeHit(BbScript script, BbScope scope, Entity attacker, Entity target, BasicAttackOutcome outcome,
        int slot = -1, int rank = 0, bool popsShields = false, bool live = false)
    {
        var probe = new BbDamageProbe { PopsShields = popsShields, Live = live && LiveCapable(script) };
        scope.Vars["HitResult"] = HitResultOf(outcome);
        scope.SpellVars = new BbTable();
        BbSpellContext.Fill(scope.SpellVars, script);
        var here = new Position3(attacker.Position.X, 0.0, attacker.Position.Z);
        var critical = outcome == BasicAttackOutcome.Critical;
        var frame = new BbHookFrame("TargetExecute", script.Name, attacker)
        {
            Caster = attacker, SpellSlot = slot, SpellRank = rank, CastPosition = here, CastEndPosition = here,
            SpellName = script.Name, IsAttackOverride = true, AttackTarget = target, Probe = probe,
            ForcedRandom = critical ? -1.0 : 2.0,
        };
        var outerContext = BbSpellContext.Current;
        var outerAttack = BbSpellContext.IsAttack;
        if (probe.Live)
        {
            BbSpellContext.Current = script;
            BbSpellContext.IsAttack = true;
        }
        try { Run(script, "TargetExecute", scope, frame, rethrow: true); }
        catch (BbProbeAbort) { }
        catch (BbRuntimeException ex)
        {
            Fail(script.Name, "TargetExecute (attack damage)", ex);
            // Half of a live prefix has happened: the resumed run has nothing to continue from, and a full run would do it again.
            if (probe.EffectsRan) _liveRuns[attacker.NetId] = new LiveRun(target.NetId, probe);
            return null;
        }
        finally
        {
            BbSpellContext.Current = outerContext;
            BbSpellContext.IsAttack = outerAttack;
        }
        if (probe is { Live: true, EffectsRan: true })
        {
            // The run stopped at the primary hit (Path) or before it; either way its effects are done and the script goes on from there, not from the start.
            _liveRuns[attacker.NetId] = new LiveRun(target.NetId, probe);
        }
        return probe.Amount is null ? null : probe;
    }

    /// <summary>The live run of an attack script made for the swing that is landing: who it hit and where it stopped.</summary>
    private sealed record LiveRun(uint Target, BbDamageProbe? Probe);

    private readonly Dictionary<uint, LiveRun> _liveRuns = new();

    /// <summary>The attack override a landing swing was fired with, taken as its hit starts (<see cref="OnAttackOverrideDamage"/>), before its hit hooks run.</summary>
    private readonly Dictionary<uint, ScriptAttackOverride> _hitAttacks = new();

    private readonly Dictionary<BbScript, bool> _liveCapable = new();

    /// <summary>
    /// Whether an attack script can be run in two parts, before and after the swing: every block of its <c>TargetExecute</c> that holds sub-blocks is a
    /// conditional (<c>BBIf</c>, <c>BBElse</c>, <c>BBElseIf</c>, <c>BBIfHasBuff</c> ...), so that the run can be resumed after the primary hit from its block path.
    /// A loop or a native that runs its sub-blocks (an iterator) cannot be re-entered; such a script keeps the dry probe and runs after the hit.
    /// </summary>
    private bool LiveCapable(BbScript script)
    {
        if (_liveCapable.TryGetValue(script, out var known)) return known;
        return _liveCapable[script] = script.Walk().Where(item => item.Hook == "TargetExecute")
            .All(item => item.Block.SubBlocks.Count == 0 || item.Block.Function.StartsWith("BBIf", StringComparison.Ordinal) ||
                         item.Block.Function is "BBElse" or "BBElseIf");
    }

    /// <summary>Whether the attack override's primary hit ignores critical strikes (the swing's roll, before it is made).</summary>
    public bool AttackOverrideIgnoresCrit(Entity attacker, Entity target) =>
        attacker.ScriptAttackOverride is not null && ProbeAttackHit(attacker, target, BasicAttackOutcome.Normal)?.IgnoreCrit == true;

    private readonly Dictionary<(string Script, string Variable), bool> _effectRemovals = new();

    /// <summary>
    /// Whether the named script removes (<c>BBSpellEffectRemove</c>) the effect whose id it keeps in <paramref name="variable"/>.
    /// Batch 484: the script is resolved as the buff runtime resolves it (<see cref="BbScriptSet.FindOrShipped"/>), not from the closure alone. The
    /// unit character module's closure is empty (its buff scripts are read from the shipped files on use), so OdinShieldRelicAura's
    /// odin_heal_rune.troy, which its OnBuffDeactivate removes, was taken as fire-and-forget: a client that first saw a health relic after its
    /// spawn step never got the rune (the persistent-effect replay of ReplicationDelivery) and the relic stayed invisible to it.
    /// </summary>
    internal bool RemovesEffectVar(string scriptName, string variable)
    {
        if (_effectRemovals.TryGetValue((scriptName, variable), out var cached)) return cached;
        return _effectRemovals[(scriptName, variable)] = _scripts.FindOrShipped(scriptName) is { } script &&
            script.Walk().Any(item => item.Block.Function == "BBSpellEffectRemove" && item.Block.Text("EffectIDVar") == variable);
    }

    /// <summary>Whether the closure has a script with a <c>TargetExecute</c> for this attack-override spell.</summary>
    internal bool HasAttackScript(string spellName) => _scripts.Find(spellName)?.Event("TargetExecute") is not null;

    private readonly Dictionary<string, BbScript?> _attackScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped attack spell script of that name (<c>AnnieBasicAttack</c>), or null when the character has none.</summary>
    private BbScript? AttackScript(string spellName)
    {
        if (_attackScripts.TryGetValue(spellName, out var cached)) return cached;
        var paths = _host.Spells.Data.Paths;
        BbScript? script = null;
        if (File.Exists(paths.Data($"Spells/{spellName}.luaobj")))
        {
            var loaded = BbScriptLoader.Load(paths, $"Spells/{spellName}.luaobj");
            if (loaded.IsClean) script = loaded.Script;
        }
        return _attackScripts[spellName] = script;
    }

    /// <summary>
    /// The <c>TargetExecute</c> of the hero's own attack spell (<c>AnnieBasicAttack</c> and its crit twin: eight shipped characters have one, the
    /// others' attacks are the inibin alone) at a landed swing. The engine's basic attack has already dealt the attack's
    /// <c>DAMAGESOURCE_ATTACK</c> hit, crit and life steal included, so it is skipped on the primary target exactly as for an attack
    /// override (<see cref="BbNativeHandlers"/> <c>ApplyDamage</c>); what stays is everything else the script does, for Annie the hit
    /// particle of <c>AnnieBasicAttack_tar</c> on every AI target (<c>BBIf Target CO_IS_TYPE_AI</c>, frost on skin 5). Like an override
    /// attack it is no spell hit: <c>GetIsAttackOverride</c> is true and no spell shield pops.
    /// Reconstruction: which of the character's attack spells a swing uses is not tracked here (the three Annie scripts are identical), so
    /// a critical swing runs <c>Data*CritAttack</c> and any other <c>Data*BaseAttack</c> (default <c>{character}BasicAttack</c>); when the server
    /// runs an attack script (before or after the damage, for a lethal hit) is not in the client.
    /// </summary>
    private void RunBasicAttackScript(Entity attacker, Entity target, BasicAttackOutcome outcome)
    {
        if (BasicAttackScript(attacker, outcome) is not { } script) return;
        var scope = NewScope(attacker, attacker, target, attacker, null);
        scope.Vars["HitResult"] = HitResultOf(outcome);
        scope.SpellVars = new BbTable();
        BbSpellContext.Fill(scope.SpellVars, script);
        var here = new Position3(attacker.Position.X, 0.0, attacker.Position.Z);
        RunInSpell(script, "TargetExecute", scope, new BbHookFrame("TargetExecute", script.Name, attacker)
        {
            Caster = attacker, CastPosition = here, CastEndPosition = here, SpellName = script.Name, IsAttackOverride = true,
            AttackTarget = target,
        });
    }

    /// <summary>The hero's own attack spell script a swing of this roll runs (<see cref="RunBasicAttackScript"/>), or null when it has none.</summary>
    private BbScript? BasicAttackScript(Entity attacker, BasicAttackOutcome outcome)
    {
        // Hero-only on purpose (batch 159 step 2): the spell named is the character's own Data*BaseAttack, read from the champion
        // inibin of attacker.ChampionName. A module-owned unit carries another unit's ChampionName (a turret is created with
        // "Annie" for the hero stats) and has no attack spell of its own here; its swing's hit is its buffs' BuffOnHitUnit.
        if (attacker.UnitType != UnitType.Hero || !Owns(attacker) || attacker.ScriptAttackOverride is not null) return null;
        var data = _host.Spells.Data.Champion(attacker.ChampionName);
        string Name(string key, string fallback) =>
            data.Raw.TryGetValue(Amberline.Data.InibinReader.KeyHash(key), out var value) && value.Kind == Amberline.Data.InibinValueKind.String
                ? value.AsString() : fallback;
        var name = outcome == BasicAttackOutcome.Critical
            ? Name("Data*CritAttack", string.Empty)
            : Name("Data*BaseAttack", attacker.ChampionName + "BasicAttack");
        return name.Length == 0 || AttackScript(name) is not { } script || script.Event("TargetExecute") is null ? null : script;
    }

    /// <summary>
    /// The <c>TargetExecute</c> of the attack spell of a unit the champion's script spawned with an AI script (<c>HeimerTBlueBasicAttack</c>,
    /// <c>MalzaharVoidlingBasicAttack</c>), at a landed swing. The hit is the script's: it applies the unit's total attack damage as its owner's
    /// <c>DAMAGESOURCE_PROC</c> (Tibbers' attack spell does the same), so the caller takes the swing's own damage away. Like an override attack it is no spell
    /// hit (<c>GetIsAttackOverride</c> is true, no spell shield pops). The attack spell is the one of the character the unit is now
    /// (<see cref="Entity.StatCharacterName"/>: the turret's pushed skin); a spell of that name that is not in the closure is read from the shipped files.
    /// Reconstruction: which of the character's attack spells a swing uses is not tracked here (the voidling's three scripts are identical).
    /// </summary>
    private bool RunUnitAttackScript(Entity unit, Entity target)
    {
        // Batch 163: the gate is the three fighting scripts, not "has an AiScript" — a unit a call gave idle.lua carries one now
        // (SpawnedUnitAi.Idle) and Idle.luaobj takes no target and runs no attack spell.
        if (!SpawnedUnitAi.Fights(unit.AiScript) || unit.UnitType == UnitType.Hero || _host.Spells.World.Get(unit.OwnerNetId) is not { } owner || !Owns(owner)) return false;
        if (AttackScript(unit.StatCharacterName + "BasicAttack") is not { } script || script.Event("TargetExecute") is null) return false;
        var scope = NewScope(unit, unit, target, unit, null);
        scope.Vars["HitResult"] = HitResultOf(BasicAttackOutcome.Normal);
        scope.SpellVars = new BbTable();
        BbSpellContext.Fill(scope.SpellVars, script);
        var here = new Position3(unit.Position.X, 0.0, unit.Position.Z);
        RunInSpell(script, "TargetExecute", scope, new BbHookFrame("TargetExecute", script.Name, unit)
        {
            Caster = unit, CastPosition = here, CastEndPosition = here, SpellName = script.Name, IsAttackOverride = true, AttackTarget = target,
        });
        // The attack is a spell the unit cast, so the script buffs it carries see the hit (Heimerdinger's UrAniumRounds and ExplosiveCartridges on the turret).
        RunSpellHit(script, unit, here, here, target, 0, 0);
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Chain attacks (Sivir's Ricochet): the override spell lists ChainMissileParameters
    // ------------------------------------------------------------------------------------------------------------

    private readonly Dictionary<uint, List<(uint Target, double At, uint[] Hops, uint Missile)>> _chainPlans = new();
    private const double ChainPlanLifetime = 10.0;

    /// <summary>
    /// Whether this router's character script runs for the unit: the heroes of its champion (a champion module's rule), or a
    /// non-champion unit <see cref="BindUnit"/> bound to a shipped <c>CharScript</c> (batch 159 step 2, the unit character
    /// module's rule — a turret). A router only ever holds one of the two kinds of owner.
    /// </summary>
    private bool Owns(Entity unit) =>
        _unitCharacters.ContainsKey(unit) ||
        (unit.UnitType == UnitType.Hero && string.Equals(unit.ChampionName, _scripts.Champion, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <see cref="Owns"/> admits the unit (the gameplay hooks' dispatch gate, see <see cref="IBbHookSink.OwnsUnit"/>).</summary>
    public bool OwnsUnit(Entity unit) => Owns(unit);

    /// <summary>
    /// The character script whose hooks run for the unit: the script <see cref="BindUnit"/> bound to it, else the closure's
    /// own character script (a champion's). Null for a unit this router has nothing to say about.
    /// </summary>
    private BbScript? CharacterOf(Entity unit) =>
        _unitCharacters.TryGetValue(unit, out var bound) ? bound : _scripts.Character;

    /// <summary>
    /// Binds one non-champion unit to a shipped character script: the claim that makes its later character hooks run from
    /// the gameplay events as they arrive, with the unit as Owner, Attacker, Target and Caster and its own
    /// <c>CharVars</c>/<c>InstanceVars</c> tables. The bind's own hook runs are the caller's to order —
    /// <see cref="BbUnitCharacterModule.BindUnit"/> takes the first <c>UpdateSelfBuffStats</c>/<c>Actions</c> sample first and
    /// calls <see cref="RunCharOnActivate"/> last, the order the shipped dragon's own hooks are written in.
    /// </summary>
    public void BindUnit(Entity unit, BbScript script) => _unitCharacters[unit] = script;

    /// <summary>
    /// <c>CharOnActivate</c> of a non-champion unit's character script, with the unit as Owner, Attacker, Target and Caster
    /// and its own <c>CharVars</c>/<c>InstanceVars</c> tables. Retail runs the hook when the character is created (the
    /// same rule as a hero and as a script-spawned minion, <see cref="ActivateSpawned"/>). The hook's stat deltas and statuses
    /// stay on the unit in one permanent internal buff (<see cref="SpawnedCharacterBuff"/>), the way
    /// <see cref="BbBuffRuntime"/> keeps a buff's; the buffs it adds are ordinary script buffs.
    /// </summary>
    public void RunCharOnActivate(Entity unit, BbScript script)
    {
        if (script.Event("CharOnActivate") is null) return;
        var collector = new BbStatCollector();
        var scope = NewScope(unit, unit, unit, unit, null, CharInstanceVars(unit));
        Run(script, "CharOnActivate", scope, new BbHookFrame("CharOnActivate", script.Name, unit) { Caster = unit, Stats = collector });
        if (collector.Totals.Count == 0 && collector.Statuses.Count == 0) return;
        // Batch 440: the statuses go to the unit's script status (released by a later write of the rest value, as CharScriptUrf's
        // HalloweenUrfAppear releases SetNoRender at its OnBuffActivate); only the stat deltas stay in the permanent carrier.
        _host.Buffs.SetSpawnStatuses(unit, collector.Statuses.Where(pair => BbBuffRuntime.StatusActs(pair.Key, pair.Value))
            .Select(pair => pair.Key).ToList());
        _host.Spells.Buffs.Add(unit, SpawnedCharacterBuff, BuffType.Internal, 25000.0, unit.NetId, BbBuffRuntime.PayloadOf(collector, statuses: false),
            canMitigateDuration: false);
    }

    /// <summary>Unbinds a unit that is gone (died, despawned): its character script and its per-unit tables go with it.</summary>
    public void UnbindUnit(Entity unit)
    {
        if (!_unitCharacters.Remove(unit)) return;
        _charVars.Remove(unit);
        _charInstanceVars.Remove(unit);
        _charStatSignatures.Remove(unit);
    }

    /// <summary>The character script bound to a non-champion unit (a check's pin), or null.</summary>
    internal BbScript? BoundCharacter(Entity unit) => _unitCharacters.TryGetValue(unit, out var bound) ? bound : null;

    /// <summary>
    /// The override spell's <c>ManaCost{level}</c> for one swing (RicochetAttack lists 6), 0 for none. Batch 348: retail charges it per
    /// swing at the swing's cast frame. A 0x0D cannot carry an override (its handler takes the character's own attack,
    /// GetBasicAttack, the attack slots only), so the swing is a cast at the override's extra slot (42 + n), and the
    /// shared cost routine, the first step of the cast routine at the cast frame,
    /// spends the castInfo's <c>ManaCost</c> (the cost routine) for every slot but the attack slots 0x40..0x46.
    /// The castInfo's amount is the server's; Sivir's patch history ("Per-attack cost increased to 6 from 4", 1 May 2009, wiki) names
    /// RicochetAttack's own <c>ManaCost</c>, and Ricochet's script charges nothing itself.
    /// </summary>
    private double OverrideSwingCost(Entity attacker, ScriptAttackOverride attack)
    {
        var data = _host.Spells.Data.Spell(attack.SpellName, 5);
        return data.ManaCosts.Count == 0 ? 0.0
            : SpellSystem.ResourceCost(attacker, data.ManaCosts[Math.Clamp(attack.Level, 1, data.ManaCosts.Count) - 1]);
    }

    /// <summary>The cost each attacker's swing in its windup pays at its cast frame (<see cref="RunAttackLaunch"/>).</summary>
    private readonly Dictionary<uint, double> _swingCosts = new();

    /// <summary>
    /// The start of a swing: its cost is held for its cast frame (<see cref="OverrideSwingCost"/>), where <see cref="RunAttackLaunch"/> spends it,
    /// so a swing stopped in its windup costs nothing. Reconstruction (M3): a hero who cannot pay loses the override (the server's own refusal of a
    /// swing it cannot pay is not in the client): the buffs whose activation overrode the attack (Ricochet) are removed, which restores the normal
    /// attack before this one is sent.
    /// </summary>
    private void RunAttackStart(Entity attacker)
    {
        if (!Owns(attacker)) return;
        _swingCosts.Remove(attacker.NetId);
        if (attacker.ScriptAttackOverride is not { } attack) return;
        var cost = OverrideSwingCost(attacker, attack);
        if (cost <= 0.0) return;
        if (attacker.Mana >= cost)
        {
            _swingCosts[attacker.NetId] = cost;
            return;
        }
        foreach (var buff in attacker.Buffs.ToArray())
            if (_scripts.Find(buff.Name) is { } script && script.Walk().Any(item => item.Hook == "OnBuffActivate" &&
                                                                        item.Block.Function == "BBOverrideAutoAttack"))
                Buffs.RemoveAll(attacker, buff.Name, attacker);
    }

    /// <summary>The auto-attack bit of the cast packet's flags byte (the isAutoAttack flag).</summary>
    private const byte AutoAttackFlag = 0x01;

    private uint _castSwingMissile;

    /// <summary>
    /// Reconstruction: an attack whose spell a script overrode (Sivir's Ricochet, Kayle's Righteous Fury) leaves as a 0xBD cast of its
    /// override spell, flagged as an auto attack, instead of a 0x0D. First found for the chain override; the W23 throwaway-client run of
    /// 19 October 2026 showed the same for Righteous Fury: as a 0x0D the client never requests the override spell's sound events
    /// (LoL_Audio/Spells/JudicatorRighteousFuryAttack/OnCast, OnHit, OnHitLocation: none in 11 s of swings, only KayleBasicAttack's), as a cast it
    /// requests all three for every swing and draws the nova and the hit flash itself.
    /// Evidence (the 18 October throwaway-client run, log-only hooks): with Ricochet up every <c>ChainMissileSync</c> fell into the
    /// world-event fallback (no missile had registered for its sender) and no <c>obj_SpellChainMissile</c> was ever constructed, so the
    /// bounces had nothing to follow. The 0x0D handler takes the attack from the character's own attack list by its
    /// slot byte (the attack lookup maps 0x40..0x46 only; the 0x22 autocast slot is not read there), so the override
    /// spell never reaches the cast routine and its CastType 2 never starts a chain missile. A cast does: the client builds the
    /// missile from the cast (the spell-caster init at the cast frame, <c>mTimeCast = now + designerCastTime</c>), and with
    /// <c>isAutoAttack</c> set draws the attack's own hit and sounds (the cast routine) and opens no cast bar.
    /// The cast carries the swing's windup and period as its designer times, the attacked unit as its single target (the chain missile
    /// asserts one) and the swing's missile id; damage stays the combat queue's. How the retail server announced an override attack is
    /// not in the client. Seen on the throwaway client by W23 (19 October 2026): Sivir's chain missiles are built (obj_SpellChainMissile
    /// constructed for every swing, the sync handler filled the hop list, UpdateProjectile walked all four hops) and Kayle's swings request
    /// their own sound events; native acceptance by the owner is still pending (the revert is this change alone). Misses and dodges keep their 0x0D (its slot byte picks the character's own miss and dodge attacks).
    /// <para>
    /// Batch 677 (GitHub issue #1): every override spell with spell data leaves as a cast, a scriptless one too. Final Hour's plain attack is
    /// <c>VayneUltAttack</c> (VayneInquisition.OnBuffActivate, ExtraSlots 4), which ships only its inibin and .fx: its missile and hit particles are
    /// the spell data's, which the client draws only from a cast of it. The script gate sent it as a 0x0D, so the client drew her own attack, while
    /// the scripted <c>VayneTumbleUltAttack</c> of a Tumble under Final Hour went out as a cast and showed its particles.
    /// </para>
    /// </summary>
    private bool RunAttackCast(Entity attacker, Entity target, uint missileNetId)
    {
        if (missileNetId == 0 || attacker.ScriptAttackOverride is not { } attack || !Owns(attacker) ||
            !_host.Spells.Data.HasSpell(attack.SpellName) ||
            !_host.Spells.Effects.IsReferenceReady(attacker)) return false;
        var navigation = _host.Spells.Movement.Navigation;
        var at = new Position3(target.Position.X, navigation.HeightAt(target.Position), target.Position.Z);
        var from = new Position3(attacker.Position.X, navigation.HeightAt(attacker.Position), attacker.Position.Z);
        // InitSpell asserts a positive designer cast time and a spell end at or after the cast frame.
        var castTime = Math.Max(attacker.AttackWindup, 0.05);
        var info = new SpellCastInfo(SpellSystem.ProtocolHash(attack.SpellName), attacker.NetId, Math.Max(1, attack.Level), attacker.NetId,
            // The swing's cost rides in the castInfo (ManaCost), which the client's own cost routine spends at its cast frame.
            missileNetId, at, at, [new SpellCastTarget(target.NetId, at, 0)], 0.0f, (float)_swingCosts.GetValueOrDefault(attacker.NetId),
            checked((byte)attack.Slot), from, 1.0f,
            (float)castTime, 0.0f, (float)Math.Max(attacker.AttackPeriod, castTime), 0.0f, AutoAttackFlag);
        _host.Spells.Effects.SendCast(attacker, info);
        _castSwingMissile = missileNetId;
        return true;
    }

    /// <summary>
    /// A chain attack's hops are chosen when the swing leaves and kept for the hit. ChainMissileSync is sent in the name of
    /// the attack's missile once the client has it, one attack windup later (<see cref="BbChainSync"/>).
    /// </summary>
    private void RunAttackSent(Entity attacker, Entity target, uint missileNetId)
    {
        if (attacker.ScriptAttackOverride is not { } attack || !Owns(attacker) || _scripts.Find(attack.SpellName) is not { } script ||
            BbChainParameters.Read(script) is not { } chain) return;
        var data = _host.Spells.Data.Spell(attack.SpellName, 5);
        var now = _host.Spells.CurrentTime;
        var hops = chain.Build(_host.Spells, attacker, target, data, attack.Level,
            BbSpellTargeting.Names(BbSpellTargeting.Flags(data))).Select(unit => unit.NetId).ToArray();
        if (!_chainPlans.TryGetValue(attacker.NetId, out var plans)) _chainPlans[attacker.NetId] = plans = new();
        plans.RemoveAll(plan => now - plan.At > ChainPlanLifetime);
        plans.Add((target.NetId, now, hops, missileNetId));
        // A swing sent as a cast is a spell's missile for the replication layer (the views the cast reached); a 0x0D's is the attack's.
        if (missileNetId != 0) _host.Spells.EnqueueSharedProjectile(new BbChainSync(attacker, missileNetId, hops, attacker.AttackWindup,
            attack: missileNetId != _castSwingMissile));
    }

    /// <summary>The hops after the primary hit fly on from the primary target at the attack spell's missile speed.</summary>
    private void StartChainAttack(Entity attacker, Entity target, ScriptAttackOverride attack, BbScript script)
    {
        if (BbChainParameters.Read(script) is not { } chain) return;
        uint[] hops;
        uint missile = 0;
        if (_chainPlans.TryGetValue(attacker.NetId, out var plans) && plans.FindIndex(plan => plan.Target == target.NetId) is >= 0 and var index)
        {
            hops = plans[index].Hops;
            missile = plans[index].Missile;
            plans.RemoveRange(0, index + 1);
        }
        else
        {
            var data = _host.Spells.Data.Spell(attack.SpellName, 5);
            hops = chain.Build(_host.Spells, attacker, target, data, attack.Level,
                BbSpellTargeting.Names(BbSpellTargeting.Flags(data))).Select(unit => unit.NetId).ToArray();
        }
        if (hops.Length < 2) return;
        _host.Spells.EnqueueSharedProjectile(new BbChainAttack(this, script, attacker, attack, hops, target.Position,
            _host.Spells.Data.Spell(attack.SpellName, 5).MissileSpeed, missile));
    }

    /// <summary>One hop of a chain attack arrived: the override spell's <c>TargetExecute</c> for that unit, counted as the n-th hit.</summary>
    internal void RunChainHop(Entity attacker, ScriptAttackOverride attack, BbScript script, Entity target, int hop)
    {
        var scope = AttackScope(attacker, target, attack.Level);
        scope.SpellVars = new BbTable();
        BbSpellContext.Fill(scope.SpellVars, script);
        var at = new Position3(target.Position.X, 0.0, target.Position.Z);
        RunInSpell(script, "TargetExecute", scope, new BbHookFrame("TargetExecute", script.Name, attacker)
        {
            Caster = attacker, SpellSlot = attack.Slot, SpellRank = attack.Level, CastPosition = at, CastEndPosition = at,
            SpellName = script.Name, IsAttackOverride = true, TargetsHit = hop,
        });
    }

    /// <summary>
    /// <c>CharOnPreDealDamage</c>: the casting unit's own script may change the damage it deals before mitigation (Kayle's
    /// Reckoning mark). Target is the victim; the changed <c>DamageAmount</c> is returned.
    /// </summary>
    public double RunPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        if (CharacterOf(source) is not { } script || script.Event("CharOnPreDealDamage") is null || !Owns(source)) return amount;
        var scope = NewScope(source, source, target, source, null, CharInstanceVars(source));
        scope.Vars["DamageAmount"] = amount;
        scope.Vars["DamageType"] = DamageTypeNumber(type);
        var frame = new BbHookFrame("CharOnPreDealDamage", script.Name, source)
        {
            Caster = source, DamageSource = kind, InDamageHook = true,
        };
        Run(script, "CharOnPreDealDamage", scope, frame);
        return frame.GroupDenied || scope.Vars["DamageAmount"] is not double changed ? amount : changed;
    }

    /// <summary>
    /// The unit the cast being started is aimed at, for the hooks <see cref="RunSpellCast"/> runs (their <c>Target</c>): set by the module around
    /// <c>BeginChampionCast</c> for a unit-target spell, null for every other cast. Reconstruction: the hook names are server-only; the one shipped
    /// reader of <c>Target</c> there is Lee Sin's character script (Dragon's Rage roots the unit it is aimed at for its windup).
    /// </summary>
    internal Entity? CastTarget { get; set; }

    /// <summary>
    /// The point the champion cast being started was aimed at (the request's position), given to the cast hooks as
    /// <c>BBGetCastSpellTargetPos</c>: the champion module sets it around <c>BeginChampionCast</c>, as it sets <see cref="CastTarget"/>. Batch 327:
    /// Olaf's <c>CharOnSpellCast</c> stores the point of his axe there, so the cast hooks answer it (the native threw "outside a spell hook" before).
    /// </summary>
    internal Position3? CastPoint { get; set; }

    /// <summary>
    /// The hero and slot of the windup spell whose <c>SelfExecute</c> is running (its cast frame), until a <c>BBSetSpell</c> on that slot retired the
    /// client's cast. The stock client keeps a windup cast active until <c>FinishCast</c> (0x39) and asserts when the slot's spell is replaced
    /// meanwhile (stock-client log, duel-regressions.md: Lee Sin's Tempest, E1 to E2).
    /// </summary>
    internal (uint Hero, int Slot)? WindupCastFrame { get; set; }

    /// <summary>
    /// <c>CharOnSpellCast</c> of the casting hero and <c>BuffOnSpellCast</c> of its script buffs, at the cast start (after cost and
    /// cooldown). <c>SpellVars</c> carry the cast spell's flags and <c>GetCastInfo</c> answers the slot and spell name.
    /// </summary>
    public void RunSpellCast(Entity hero, int slot, string spellName, BbScript? spell, int? rankOverride = null, bool buffHooks = true)
    {
        var rank = rankOverride ?? (slot is >= 0 and < 4 && hero.SpellRanks.Count > slot ? hero.SpellRanks[slot] : 0);
        // The caster's own character script: a champion's (its champion's heroes, as before) or a non-champion unit this router
        // binds (batch 174 part 2: CharScriptWorm's CharOnSpellCast rolls its next action after each of the Baron's casts).
        if (CharacterOf(hero) is { } script && script.Event("CharOnSpellCast") is not null && Owns(hero))
        {
            var scope = NewScope(hero, hero, CastTarget, hero, rank >= 1 ? rank : null, CharInstanceVars(hero));
            scope.SpellVars = SpellVars(spell);
            Run(script, "CharOnSpellCast", scope, new BbHookFrame("CharOnSpellCast", script.Name, hero)
            {
                Caster = hero, SpellSlot = slot, SpellRank = rank, SpellName = spellName, CastPosition = CastPoint,
            });
        }
        if (!buffHooks) return;
        foreach (var instance in Buffs.WithHook(hero, "BuffOnSpellCast"))
        {
            // Caster is the buff's caster, as in every other buff hook: Kassadin's Force Pulse sits on every hero in range and reads CharVars through it.
            var scope = NewScope(hero, hero, CastTarget, _host.Spells.World.Get(instance.CasterNetId) ?? hero, rank >= 1 ? rank : null, instance.InstanceVars);
            scope.SpellVars = SpellVars(spell);
            Run(instance.Script, "BuffOnSpellCast", scope, new BbHookFrame("BuffOnSpellCast", instance.Name, hero)
            {
                Caster = hero, SpellSlot = slot, SpellRank = rank, SpellName = spellName, Buff = instance, CastPosition = CastPoint,
            });
        }
    }

    private bool _inChildCastHooks;

    /// <summary>
    /// The cast hooks of an extra-slot child a hero's script casts (<c>BBSpellCast</c> with <c>ExtraSlots</c>): <c>GetCastInfo</c> answers the child's
    /// name and its absolute slot (42 and up, so a hook that tests a champion slot does not see it), the rank is the one the child casts at, and
    /// <c>Target</c> is the unit it is aimed at. Reconstruction: the hook names are server-only; the evidence is a shipped character script that tests
    /// the name of a child (Dr. Mundo's) and the scripts that read <c>SpellVars.DoesntTriggerSpellCasts</c> to ignore such casts themselves. A hook
    /// that casts a child itself does not run the hooks again (no retail rule is known; the guard keeps a script from looping).
    /// <para>
    /// Batch 324: a child whose script sets <c>DoesntTriggerSpellCasts = true</c> runs <c>CharOnSpellCast</c> but not the caster's
    /// <c>BuffOnSpellCast</c>. Reconstruction from the shipped scripts (the stock client names the flag nowhere: its binary holds no
    /// string, so the dispatch is the server's): Talon's <c>TalonShadowAssault</c> adds the stealth <c>TalonShadowAssaultBuff</c>, whose
    /// <c>BuffOnSpellCast</c> removes it on any cast without reading the flag, and then casts eight <c>TalonShadowAssaultMisOne</c> children
    /// (flag true) in the same <c>SelfExecute</c>; a buff hook for them ends the 2.5 s stealth at once. Dr. Mundo's <c>CharOnSpellCast</c> pays
    /// Infected Cleaver's health on the name of the child <c>InfectedCleaverMissile</c> (flag true; the slot spell
    /// <c>InfectedCleaverMissileCast</c> pays nothing), so the character hook does run for a flagged child. A slot cast keeps both hooks: the
    /// buff scripts that react to slot casts read the flag from <c>SpellVars</c> themselves (stealth buffs, Tear, Kassadin's charge).
    /// </para>
    /// </summary>
    internal void RunChildSpellCast(Entity hero, int slot, string spellName, BbScript spell, int rank, Entity? target)
    {
        if (_inChildCastHooks) return;
        var previous = CastTarget;
        var previousPoint = CastPoint;
        CastTarget = target;
        // The child's own cast point is not passed here, so its hooks get none (as before the cast point existed) rather than the parent's.
        CastPoint = null;
        _inChildCastHooks = true;
        try { RunSpellCast(hero, slot, spellName, spell, rank, spell.Global("DoesntTriggerSpellCasts") is not BbValue.Bool { Value: true }); }
        finally
        {
            _inChildCastHooks = false;
            CastTarget = previous;
            CastPoint = previousPoint;
        }
    }

    /// <summary><c>CharOnLevelUpSpell</c>: the hero spent a skill point; <c>Slot</c> is the spell slot that gained the rank.</summary>
    public void RunLevelUpSpell(Entity hero, int slot)
    {
        RunBuffLevelUpSpell(hero, slot);
        if (_scripts.Character is not { } script || script.Event("CharOnLevelUpSpell") is null) return;
        var rank = slot is >= 0 and < 4 && hero.SpellRanks.Count > slot ? hero.SpellRanks[slot] : 0;
        var scope = NewScope(hero, hero, hero, hero, rank >= 1 ? rank : null, CharInstanceVars(hero));
        scope.Vars["Slot"] = (double)slot;
        Run(script, "CharOnLevelUpSpell", scope, new BbHookFrame("CharOnLevelUpSpell", script.Name, hero)
        {
            Caster = hero, SpellSlot = slot, SpellRank = rank,
        });
    }

    // ------------------------------------------------------------------------------------------------------------
    // IBbHookSink: each forward is a safety boundary (a sink that raised would stall the simulation tick)
    // ------------------------------------------------------------------------------------------------------------

    public void OnBuffTick(Entity unit, BuffState buff, double dt) =>
        Guard("sink", "OnBuffTick", () => Buffs.OnTick(unit, buff, dt));

    // A hook that raised allows the add (RunAllowAdd runs it guarded), so a broken shield never blocks a unit's buffs for good.
    public bool AllowBuffAdd(Entity target, Entity adder, string name, ref double duration, double type, double maxStack) =>
        (ItemCopyAllowAdd is null || ItemCopyAllowAdd(target, adder, name, ref duration, type, maxStack)) &&
        Buffs.AllowAdd(target, adder, name, ref duration, type, maxStack);

    public void OnAmmoTimer(Entity unit, BuffState buff) =>
        Guard("sink", "OnAmmoTimer", () => Buffs.OnAmmoTimer(unit, buff));

    public double OnPreDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind)
    {
        var result = amount;
        Guard("sink", "OnPreDamage", () =>
        {
            result = Buffs.OnPreDamage(target, source, amount, type, kind);
            result = RunCharPreDamage(target, source, result, type, kind);
        });
        return result;
    }

    public void OnTakeDamage(Entity target, Entity? source, double amount, DamageSourceKind? kind, bool periodic) =>
        Guard("sink", "OnTakeDamage", () => Buffs.OnTakeDamage(target, source, amount, kind, periodic));

    public double OnPreMitigationDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind)
    {
        var result = amount;
        Guard("sink", "OnPreMitigationDamage", () => result = Buffs.OnPreDamage(target, source, amount, type, kind, "BuffOnPreMitigationDamage"));
        return result;
    }

    public void OnUnitDeath(Entity unit, uint killerNetId) =>
        Guard("sink", "OnUnitDeath", () =>
        {
            Buffs.OnUnitDeath(unit, killerNetId);
            RunNearbyDeath(unit, killerNetId);
        });

    public void OnUnitDeathReleased(Entity unit) =>
        Guard("sink", "OnUnitDeathReleased", () => Buffs.RemoveNonPersistent(unit));

    public void OnSpellCast(Entity hero, int slot, string spellName) =>
        Guard("sink", "OnSpellCast", () => RunSpellCast(hero, slot, spellName, _scripts.FindOrShipped(spellName)));

    public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target) =>
        Guard("sink", "OnNamedSpellCast", () =>
        {
            var previous = CastTarget;
            CastTarget = target;
            try { RunSpellCast(hero, slot, spellName, _scripts.FindOrShipped(spellName), rankOverride: 0); }
            finally { CastTarget = previous; }
        });

    public void OnBasicAttackStart(Entity attacker, Entity target) =>
        Guard("sink", "OnBasicAttackStart", () =>
        {
            RunAttackStart(attacker);
            RunPreAttack(attacker, target);
        });

    public bool SendAttackAsCast(Entity attacker, Entity target, uint missileNetId)
    {
        var sent = false;
        Guard("sink", "SendAttackAsCast", () => sent = RunAttackCast(attacker, target, missileNetId));
        return sent;
    }

    double IBbHookSink.OnAttackOverrideDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome)
    {
        var result = damage;
        Guard("sink", "OnAttackOverrideDamage", () => result = OnAttackOverrideDamage(attacker, target, damage, attackOverride, outcome));
        return result;
    }

    bool IBbHookSink.AttackOverrideIgnoresCrit(Entity attacker, Entity target)
    {
        var result = false;
        Guard("sink", "AttackOverrideIgnoresCrit", () => result = AttackOverrideIgnoresCrit(attacker, target));
        return result;
    }

    BasicAttackOutcome? IBbHookSink.TakeScriptedHitResult()
    {
        var answer = _scriptedHitResult;
        _scriptedHitResult = null;
        return answer;
    }

    bool IBbHookSink.TakeAttackOverrideIgnoresIncreaseMods()
    {
        var answer = _ignoreIncreaseMods;
        _ignoreIncreaseMods = false;
        return answer;
    }

    bool IBbHookSink.OnUnitAttackHit(Entity unit, Entity target)
    {
        var ran = false;
        Guard("sink", "OnUnitAttackHit", () => ran = RunUnitAttackScript(unit, target));
        return ran;
    }

    public void OnBasicAttackSent(Entity attacker, Entity target, uint missileNetId) =>
        Guard("sink", "OnBasicAttackSent", () => RunAttackSent(attacker, target, missileNetId));

    public void OnBasicAttackHit(Entity attacker, Entity target, double rawDamage, bool attackOverride, BasicAttackOutcome outcome) =>
        Guard("sink", "OnBasicAttackHit", () =>
        {
            if (attackOverride)
            {
                RunAttackSelfExecute(attacker, target);
                RunAttackSpell(attacker, target, outcome);
            }
            else RunBasicAttackScript(attacker, target, outcome);
        });

    public double ModifyBasicAttackDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome)
    {
        var result = damage;
        Guard("sink", "ModifyBasicAttackDamage", () => result = RunHitUnit(attacker, target, damage, outcome));
        return result;
    }

    /// <summary>The script constant of a swing's result (<c>HIT_Normal</c> 0, <c>HIT_Critical</c> 1, <c>HIT_Dodge</c> 2, <c>HIT_Miss</c> 3).</summary>
    internal static double HitResultOf(BasicAttackOutcome outcome) => (double)(outcome switch
    {
        BasicAttackOutcome.Critical => BbHitResult.HIT_Critical,
        BasicAttackOutcome.Dodge => BbHitResult.HIT_Dodge,
        BasicAttackOutcome.Miss => BbHitResult.HIT_Miss,
        _ => BbHitResult.HIT_Normal,
    });

    public void OnBasicAttackOnCorpse(Entity attacker, Entity corpse, bool attackOverride) =>
        Guard("sink", "OnBasicAttackOnCorpse", () =>
        {
            if (attackOverride && attacker.ScriptAttackOverride is { } attack && _scripts.Find(attack.SpellName) is { } script &&
                Owns(attacker)) StartChainAttack(attacker, corpse, attack, script);
        });

    public double OnPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        var result = amount;
        Guard("sink", "OnPreDealDamage", () => result = RunPreDealDamage(source, target, amount, type, kind));
        return result;
    }

    public double OnBuffPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        var result = amount;
        Guard("sink", "OnBuffPreDealDamage", () => result = RunBuffPreDealDamage(source, target, amount, type, kind));
        return result;
    }

    public bool OnBeingSpellHit(Entity? source, Entity target, DamageSourceKind? kind)
    {
        var absorbed = false;
        Guard("sink", "OnBeingSpellHit", () => absorbed = RunBeingSpellHit(target, source));
        return absorbed;
    }

    public bool OnBreakSpellShields(Entity target)
    {
        var popped = false;
        Guard("sink", "OnBreakSpellShields", () => popped = RunBreakSpellShields(target));
        return popped;
    }

    public bool ShieldPopped(Entity target) =>
        Buffs.WithHook(target, "BuffOnBeingSpellHit").Any(instance => instance.InstanceVars["WillRemove"] is true);

    public bool IsScripted(Entity target, BuffState buff) =>
        Buffs.OfUnit(target).Any(instance => ReferenceEquals(instance.State, buff));

    private void Guard(string script, string hook, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { Fail(script, hook, ex); }
    }
}
