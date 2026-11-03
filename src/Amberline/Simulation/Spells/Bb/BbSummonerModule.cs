using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The summoner spells' script owner (batch 142 step 2): every <c>DATA/Spells/Summoner&lt;Name&gt;.luaobj</c> the
/// 1.0.0.126 client offers runs from its shipped code, bound to <c>(hero, summoner slot)</c> through
/// <see cref="BbLoadoutOwner.SetSummoner"/> the way a talent is bound to its rank. This is not a
/// <see cref="BbChampionModule"/>: the two slots exist on every hero of every champion, the ten hand-written ones
/// included, and a summoner script never names a champion.
/// <para>
/// The module owns its own <see cref="BbSpellHost"/>, <see cref="BbHookRouter"/> and <see cref="BbBuffRuntime"/> so its
/// per-spell tables are keyed by the summoner slot (0..1) and cannot collide with a champion module's slots 0..3. The
/// hero's <c>AvatarVars</c> is the one table the mastery feeders write (batch 142 step 1c) and these scripts read, so
/// every router is pointed at <see cref="BbLoadoutOwner.AvatarVars"/> through <see cref="BbHookRouter.AvatarVarsOf"/>.
/// </para>
/// <para>
/// What stays with the engine is what no shipped script decides: which units a cast reaches (the client's target and the
/// inibin's target flags), the cast announcement, the cooldown clock, and the map rules a teleport has to obey. Everything
/// else — the damage, the buffs, the particles, the gold, the teleport itself — is the script's own <c>SelfExecute</c> and
/// <c>TargetExecute</c>.
/// </para>
/// </summary>
public sealed class BbSummonerModule
{
    private readonly GameData _data;
    private readonly BbScriptSet _scripts;
    private BbSpellHost? _host;
    private BbHookRouter? _router;

    public BbSummonerModule(GameData? data = null)
    {
        _data = data ?? GameData.Default;
        _scripts = new BbScriptSet(ClosureOf(_data.Paths), _data.Paths);
    }

    /// <summary>
    /// The summoner closure: one script per shipped <c>Spells/Summoner*.luaobj</c>. The summoner-named buffs
    /// (<c>SummonerDot</c>, <c>SummonerHealCheck</c>, <c>SummonerReviveSpeedBoost</c>, <c>SummonerBoostSpellShield</c>,
    /// the Odyssey pair) are those same files — a spell script and a buff script are one file in the shipped layout — so
    /// the list is the whole area. The buffs the scripts name that live under other names (<c>GrievousWound</c>,
    /// <c>FortifyBuff</c>, <c>Teleport_Target</c> ...) are not closure members: <see cref="BbBuffRuntime"/> resolves them
    /// from the shipped <c>Spells/&lt;name&gt;.luaobj</c> the first time one is added.
    /// </summary>
    private static BbClosure ClosureOf(GameDataPaths paths)
    {
        var directory = paths.Data("Spells");
        var scripts = new List<BbClosureScript>();
        var diagnostics = new List<BbDiagnostic>();
        if (Directory.Exists(directory))
        {
            foreach (var file in CaseInsensitivePaths.EnumerateFiles(directory, "Summoner*.luaobj")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var loaded = BbScriptLoader.Load(paths, $"Spells/{name}.luaobj");
                diagnostics.AddRange(loaded.Diagnostics);
                if (loaded.Script is { } script)
                    scripts.Add(new BbClosureScript(name, $"Spells/{name}.luaobj", BbScriptRole.Spell, "SummonerSpells", script));
            }
        }
        return new BbClosure("SummonerSpells", scripts, Array.Empty<BbDanglingReference>(),
            Array.Empty<string>(), diagnostics);
    }

    /// <summary>Every summoner script the shipped data holds, by name.</summary>
    public IReadOnlyList<string> ScriptNames => _scripts.Closure.ScriptNames.ToArray();

    public bool HasScript(string spellName) => _scripts.Find(spellName) is not null;

    /// <summary>
    /// Binds the module to a match. The router's <c>AvatarVars</c> is the hero's loadout table, so the mastery feeders'
    /// writes and these scripts' reads are the same entries. Call once per match.
    /// </summary>
    public IBbHookSink Bind(SpellSystem system, BbLoadoutHooks loadout, Action<BbScriptFailure>? failureSink = null)
    {
        if (_host is not null) throw new InvalidOperationException("the module is already bound to a match");
        var host = new BbSpellHost(system, (hero, slot) => SpellName(hero, slot));
        var router = new BbHookRouter(host, _scripts);
        host.Router = router;
        router.FailureSink = failureSink;
        router.AvatarVarsOf = hero => loadout.AvatarVars(hero) ?? loadout.OwnerFor(hero)?.AvatarVars ?? new BbTable();
        host.Buffs = new BbBuffRuntime(host, _scripts, router);
        _host = host;
        _router = router;
        return router;
    }

    public bool IsBound => _host is not null;

    internal BbHookRouter? Router => _router;
    internal BbBuffRuntime? Buffs => _host?.Buffs;

    /// <summary>Hook runs that raised and were skipped (production safety); empty in a healthy match.</summary>
    public IReadOnlyList<BbScriptFailure> ScriptFailures => _router?.Failures ?? Array.Empty<BbScriptFailure>();

    private BbHookRouter RouterOrThrow => _router ?? throw new InvalidOperationException("BbSummonerModule.Bind was not called");

    /// <summary>
    /// The spell a hero's summoner slot holds now, in its shipped-script name. The spellbook stores the loadout's
    /// display name ("Flash", "Ignite"), so it is canonicalised the same way the cast path does it. A name the loadout
    /// does not know — the <c>TeleportCancel</c> placeholder a running Teleport channel writes into the slot
    /// (<c>BBSetSpell</c>, SummonerTeleport.OnBuffActivate) — is answered as it is stored, since that is the script
    /// name of the spell the slot now holds.
    /// </summary>
    public string SpellName(Entity hero, int slot) =>
        (uint)slot < (uint)hero.SummonerSpells.Count && hero.SummonerSpells[slot].Length > 0
            ? SummonerLoadout.TryCanonicalFromDisplay(hero.SummonerSpells[slot]) ?? hero.SummonerSpells[slot]
            : "";

    // ------------------------------------------------------------------------------------------------------------
    // The (hero, summoner slot) binding
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The hero's binding for its summoner <paramref name="slot"/>, made the first time the slot is cast or read. The
    /// binding lives on the hero's <see cref="BbLoadoutOwner"/> next to its talents and keeps its own
    /// <c>InstanceVars</c> for as long as the slot holds the spell. <see cref="BbScriptSet.FindOrShipped"/> rather than
    /// <c>Find</c>: <c>TeleportCancel</c> is a real shipped <c>Spells/TeleportCancel.luaobj</c> but not a
    /// <c>Summoner*.luaobj</c>, so it is not a closure member of this module.
    /// </summary>
    public BbLoadoutBinding? Binding(Entity hero, int slot, BbLoadoutOwner owner)
    {
        var name = SpellName(hero, slot);
        if (name.Length == 0) return null;
        if (owner.Summoner(slot) is { } live && string.Equals(live.Script.Name, name, StringComparison.OrdinalIgnoreCase))
            return live;
        if (_scripts.FindOrShipped(name) is not { } script) return null;
        return owner.SetSummoner(script, name.GetHashCode(StringComparison.Ordinal), slot);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The cast boundary
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The script's own gate (<c>CanCast</c>: Flash refuses while the hero cannot move or cast, Revive and Promote test
    /// their own conditions). A script that has none, or that stored no <c>false</c>, allows the cast.
    /// </summary>
    public bool CanCast(Entity hero, int slot, BbLoadoutBinding binding) =>
        RouterOrThrow.RunCanCast(binding.Script, hero, slot, binding.Rank, 0.0, binding);

    /// <summary>
    /// The reach the script asks for (<c>AdjustCastInfo</c>: Flash's 750 plus its <c>FlashRangeBonus</c>). Null when the
    /// script sets no range, which leaves the inibin's own.
    /// </summary>
    public double? CastRange(Entity hero, int slot, BbLoadoutBinding binding) =>
        RouterOrThrow.RunAdjustCastInfo(binding.Script, hero, slot, binding.Rank, binding);

    /// <summary>
    /// The cooldown a cast starts: what the script's own <c>AdjustCooldown</c> returns (batch 303). With
    /// <c>AvatarVars</c> reading nil for an unwritten entry, every shipped block answers itself: its
    /// <c>SummonerCooldownBonus CO_NOT_EQUAL 0</c> branch runs for a hero without Presence too (nil is not 0), the bonus
    /// reads as the block's literal 0 through <c>GetMathNumber</c>, and the base literal times that multiplier, less the
    /// flat mastery bonus the block names, is the return (Flash 255, Heal 270, Revive 540 through its <c>NewCooldown</c>).
    /// Every literal is the inibin <c>Cooldown</c> except Revive's 540 (SpellData 510): the script's value, which is also
    /// the owner's 16 October 2026 pick and the archived wiki's (the wiki's Revive page).
    /// A hook that raised returns nothing and the inibin cooldown starts.
    /// </summary>
    public double AdjustCooldown(Entity hero, int slot, BbLoadoutBinding binding)
    {
        if (hero.CooldownsDisabled) return 0.0;
        FillAvatarVars(hero);
        if (binding.Script.Event("AdjustCooldown") is null) return CastCooldownOf(binding.Script);
        return RouterOrThrow.RunAdjustCooldown(binding.Script, hero, slot, binding.Rank, binding)
            ?? _data.Spell(binding.Script.Name, 1).Cooldowns[0];
    }

    /// <summary>
    /// The cooldown a script's <c>BBSetSlotSpellCooldownTime</c> names as a plain literal rather than through
    /// <c>SrcVar</c> (<c>SummonerTeleport.OnBuffActivate</c> writes <c>SrcValue="1.0"</c> when it swaps the slot to
    /// <c>TeleportCancel</c>; its <c>OnBuffDeactivate</c> writes <c>SrcVar="BaseCooldown"</c>, the real cooldown at the
    /// end of the channel, and is not this). Read only for a script with no <c>AdjustCooldown</c> hook at all, which of
    /// the summoners is Teleport alone (and the <c>TeleportCancel</c> placeholder).
    /// Reconstruction: what retail's native cast starts for a summoner with no <c>AdjustCooldown</c> is unrecovered; the
    /// script's own 1 s is what its <c>OnBuffActivate</c> leaves on the slot in the same frame, so the cast announcement
    /// carries the value the slot keeps.
    /// </summary>
    public static double CastCooldownOf(BbScript script)
    {
        foreach (var (_, block) in script.Walk())
        {
            if (!string.Equals(block.Function, "BBSetSlotSpellCooldownTime", StringComparison.Ordinal)) continue;
            if (block.Text("SrcVar") is not null) continue;
            if (block.Number("SrcValue") is { } literal) return literal;
        }
        return 0.0;
    }

    /// <summary>
    /// The avatar variables the engine itself fills in before a summoner hook runs. The mastery feeders write their own
    /// keys in <c>SetVarsByLevel</c>; <c>HasRune5378</c> is the one the stock client's native avatar setup would have
    /// set (Reconstruction: no shipped file writes it), so it comes from the hero's rune page.
    /// </summary>
    private void FillAvatarVars(Entity hero)
    {
        if (_host?.Router?.AvatarVarsOf is not { } of) return;
        of(hero)["HasRune5378"] = hero.Runes.AllIds.Contains(PresenceQuintessenceRuneId);
    }

    /// <summary>Greater Quintessence of Presence, the only rune the summoner scripts read (<c>HasRune5378</c>).</summary>
    private const int PresenceQuintessenceRuneId = 5378;

    /// <summary><c>SelfExecute</c>: the cast frame of a summoner spell (Flash's teleport, Heal's area, Clarity's mana).</summary>
    /// <remarks>
    /// Batch 555: Flash's damage locks are leftovers nothing attaches. <c>Spells/FlashCheck.luaobj</c> (a 3 s cooldown floor
    /// after hero or turret damage, keyed on an <c>InstanceVars.flashSlot</c> its adder must pass) and the <c>FlashBeenHit</c>
    /// buff that <c>SummonerFlash</c>'s <c>SelfExecute</c> tests for its 1 s delayed landing are added by no shipped script, have
    /// no string in the stock EXE (<c>FlashBeenHit</c> has no script at all), and the wiki's Flash history from Alpha Week 4 to
    /// V1.0.0.152 lists no damage lock. So no hero carries either one, and Flash always lands at once.
    /// </remarks>
    public void SelfExecute(Entity hero, int slot, BbLoadoutBinding binding, Position3 position, Position3 end)
    {
        FillAvatarVars(hero);
        RouterOrThrow.RunSelfExecute(binding.Script, hero, slot, binding.Rank, 0.0, position, end, hero, hero, binding);
    }

    /// <summary><c>TargetExecute</c>: one unit the cast reached (Ignite's DoT, Smite's damage, Exhaust's debuff).</summary>
    public void TargetExecute(Entity hero, int slot, BbLoadoutBinding binding, Entity target, Position3 position, Position3 end)
    {
        FillAvatarVars(hero);
        RouterOrThrow.RunTargetExecute(binding.Script, hero, slot, binding.Rank, 0.0, position, end, target,
            binding: binding);
    }

    /// <summary>
    /// A buff this module's scripts added left its holder (<c>OnBuffDeactivate</c>): Clairvoyance drops the bubble and
    /// both eye particles from the Owner, Flash's delayed landing teleports. The removal is routed by the host
    /// (<c>SpellSystem.OnSummonerBuffRemoved</c>) the way a champion module's is.
    /// </summary>
    public void OnBuffRemoved(Entity unit, BuffState buff) => _host?.Buffs.OnRemoved(unit, buff);

    // ------------------------------------------------------------------------------------------------------------
    // Recall (Spells/Recall.luaobj on SpellSystem.RecallSlot): the cast frame and the channel
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shipped recall spell on <see cref="SpellSystem.RecallSlot"/>, the spell of the map's extra summoner item
    /// (<see cref="GameData.RecallSpellName"/>: Recall, OdinRecall on The Crystal Scar). It is not a summoner loadout spell.
    /// </summary>
    public string RecallSpellName => _data.RecallSpellName;

    private bool IsRecall(BbChannel? channel) => channel is not null && channel.Script.Name == RecallSpellName;

    /// <summary>Recalls whose cast windup is still running: the binding, the cast point and the seconds left to the cast frame.</summary>
    private readonly Dictionary<Entity, (BbLoadoutBinding Binding, Position3 At, double Remaining)> _recallWindups =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The hero's Recall binding, made the first time the hero casts it: <c>Spells/Recall.luaobj</c> on the owner's summoner slot
    /// <see cref="SpellSystem.RecallSlot"/>. Null when the shipped script is missing.
    /// </summary>
    public BbLoadoutBinding? RecallBinding(BbLoadoutOwner owner)
    {
        if (owner.Summoner(SpellSystem.RecallSlot) is { } live) return live;
        return _scripts.FindOrShipped(RecallSpellName) is { } script
            ? owner.SetSummoner(script, RecallSpellName.GetHashCode(StringComparison.Ordinal), SpellSystem.RecallSlot)
            : null;
    }

    /// <summary>
    /// The cast's windup. The cast packet starts the client's cast frame <c>DesignerCastTime</c> after the cast, and the script's
    /// channel begins there: <c>ChannelingStart</c> (the TeleportHome effect, the Recall buff) runs at the cast frame, as a channelled
    /// champion spell's does (<c>BbHookRouter.RunSelfExecute</c>). A zero windup starts the channel at once.
    /// </summary>
    public void BeginRecall(Entity hero, BbLoadoutBinding binding, Position3 at, double castTime)
    {
        if (_host is null) return;
        if (castTime > 0.0) _recallWindups[hero] = (binding, at, castTime);
        else StartRecallChannel(hero, binding, at);
    }

    /// <summary>
    /// Batch 449: the shipped OdinCaptureChannel as a channel of the hero, aimed at a capture point guardian (<c>Target</c>). No shipped
    /// spell or character script starts it ; see docs/maintenance/2026-10-28-batch-449-dominion-capture-input.md, part 1, so the server
    /// starts it: <c>Reconstruction</c>, the slot is none (-1) and the cast point is the hero's own position. The channel's own hooks
    /// add the buff to the hero each 0.25 s (ChannelingUpdateStats) and end it (BBStopChanneling, the damage hook, a fresh order).
    /// </summary>
    public bool BeginCaptureChannel(Entity hero, Entity guardian) => BeginUseChannel(hero, guardian, CaptureChannelSpellName);

    /// <summary>
    /// Batch 470: the shipped channel a useable unit names for a hero (its character file's <c>Useable*HeroUseSpell</c>: OdinCaptureChannel on
    /// a capture point guardian, OdinCaptureChannelBomb on a greater relic) as a channel of the hero aimed at that unit (<c>Target</c>), started
    /// the way the capture is (<see cref="BeginCaptureChannel"/>). False when the module or the script is missing.
    /// </summary>
    public bool BeginUseChannel(Entity hero, Entity used, string spellName)
    {
        if (_host is null || hero.UnitType != UnitType.Hero || !hero.Alive) return false;
        if (_scripts.FindOrShipped(spellName) is not { } script) return false;
        // Batch 461: the capture's stop, as Recall's (SpellSystem.HandleRecall): the hero drops its walk and its fight and is seated on its
        // lattice anchor (InstallStop) before the channel takes its spot (BbChannel.LastPosition). A hero ordered while walking was seated by
        // the stop of ChannelingStart's OdinCaptureImmobile (SetCanMove false) after the spot was taken, up to 1.4 units away, and the
        // channel's Move rule ended the capture on its first step. A champion channel is seated by its cast's stop long before its cast frame.
        var stop = _host.Spells.Movement.InstallStop(hero);
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        var at = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        // Batch 480 (Reconstruction, owner finding 14): the walk toward the useable unit was sent in full and the channel's seat was never
        // sent, so the client walked on to the unit (the centre crystal's 250 range ends inside it) and a cancel's stop slid the hero back.
        // The seat goes out with the channel, the way a cancel's stop does (BbScriptChannels.Stop).
        if (_host.Spells.Effects.IsReferenceReady(hero))
            _host.Spells.Effects.SendWaypoints(hero, stop, _host.Spells.World.Clock.SyncId());
        _host.Channels.Begin(script, hero, CaptureChannelNoSlot, 1, 0.0, at, at, used);
        AnnounceUseChannel(hero, script, used, at);
        return true;
    }

    /// <summary>
    /// Batch 480 (Reconstruction, owner finding 12): the cast answer a use channel sends so the client opens its channel bar. A use is not a
    /// cast, so no packet named the channel and the client's SpellCaster (the local hero's HUD bar) never started. The cast carries the
    /// channel's spell, the cast time of the spell and its <c>ChannelDuration</c> (the rule <c>BbChampionModule.Channelled</c> follows), the
    /// channel bit and the unit as its target. the spell-caster init asserts a positive cast time, and the HUD bar is drawn at
    /// the cast frame (the cast-frame routine with a positive channel duration); its target is read from slot 51
    /// (<see cref="UseChannelSlot"/>). The end is the client's own (mTimeSpellEnd); a cancel stops it through <see cref="BbScriptChannels.Stop"/>.
    /// Batch 494: the use slot is bound on every viewer first (0x73). Only the user's own client binds it itself (see <see cref="UseChannelSlot"/>);
    /// every other viewer had no SpellData there, and each cast of slot 51 by another hero raised <c>AdjustVarsAfterSpellCast</c> once and
    /// the spell instance's targeting-type getter every frame of the channel (29,883 hard asserts in the owner's game e924eadc). Reconstruction: which
    /// packet retail bound the slot with is not shipped; 0x73 is the client's one server packet that binds a hashed spell to slot 0x33 at
    /// the rank the client's own use binds (1). The user's client already holds the spell and skips the same name.
    /// Batch 517 (Reconstruction, owner finding 31 October 2026: the champion spins in place after a capture until moved): the cast is aimed at
    /// the used unit on the ground, as every unit-target cast is (<c>BbChampionModule.CastHoming</c>), and leaves from the seat on the ground.
    /// It carried the seat at height 0. the caster's animation routine faces the caster along
    /// <c>TargetPos - casterPos</c> in 3D once that is longer than 2 units (4.0, squared): from a seat at the ground height
    /// (about -145 to -190 on the Crystal Scar) to the same point at 0 that is straight up, so the target facing (the direction and the unit field)
    /// became (0, 1, 0). The idle turn takes the flat part of that
    /// facing, falls back to the full vector when the flat part is 0, turns about Y toward it, and snaps only when the cross
    /// product's Y changes sign. For a vertical facing that Y stays 0, so the turn never ends. The channel's SpellCaster holds the
    /// turn off; its end at the capture released it, and only the next move (a new facing from the path) stopped it.
    /// </summary>
    private void AnnounceUseChannel(Entity hero, BbScript script, Entity used, Position3 at)
    {
        var spells = _host!.Spells;
        if (!spells.Effects.IsReferenceReady(hero)) return;
        var data = spells.Data.Spell(script.Name);
        var navigation = spells.Movement.Navigation;
        var aim = new Position3(used.Position.X, navigation.HeightAt(used.Position), used.Position.Z);
        var launch = new Position3(at.X, navigation.HeightAt(new Position2(at.X, at.Z)), at.Z);
        var targets = new[] { new SpellCastTarget(used.NetId, aim, 0) };
        var cast = new SpellCastInfo(SpellSystem.ProtocolHash(script.Name), hero.NetId, 1, hero.NetId, 0, aim, aim, targets,
            0.0f, 0.0f, UseChannelSlot, launch,
            AttackSpeedModifier: 1.0f, DesignerCastTime: (float)data.CastTime, ExtraCastTime: 0.0f,
            DesignerTotalTime: (float)(data.CastTime + BbScriptChannels.DurationOf(script)), StartCastTime: 0.0f, Flags: ChannellingFlag);
        spells.Effects.SendSetSpellData(hero, cast.SpellHash, UseChannelSlot);
        spells.Effects.SendCast(hero, cast, spells.World.Clock.SyncId());
    }

    /// <summary>
    /// Batch 480 (Reconstruction): the spellbook slot a use channel's cast answer carries. The stock client's HUD reads the channel's target
    /// only for slot 0x33 (slot 51 and a target count above zero), and
    /// The client's cast routine excludes 0x33 from its instant-cast rule; slot 51 is the last extra slot, inside the
    /// spellbook of 52. Batch 494 (proven): slot 0x33 is the use slot. The user component's use routine binds the useable's
    /// <c>HeroUseSpell</c> there itself before it sends the 0x3F (the set-spell-data routine, rank 1),
    /// but only on the user's own client; the click gate reads this slot's cooldown.
    /// </summary>
    internal const byte UseChannelSlot = 0x33;

    /// <summary>The <c>isForceCastingOrChannelling</c> bit of <see cref="SpellCastInfo.Flags"/> (0x04), <c>BbChampionModule.ChannellingFlag</c>.</summary>
    private const byte ChannellingFlag = 0x04;

    /// <summary>The shipped channel that a capture point guardian's order starts (batch 449).</summary>
    /// <summary>Batch 452: the guardian a hero's channel is aimed at when that channel is the capture (OdinCaptureChannel), or null.</summary>
    /// <summary>Batch 475b: the channels aimed at a replaced guardian take its replacement (BbScriptChannels.Retarget).</summary>
    public int RetargetCaptureChannels(Entity from, Entity to, Team exceptTeam) => _host?.Channels.Retarget(from, to, exceptTeam) ?? 0;

    /// <summary>
    /// Batch 493 (Reconstruction): a capture guardian that is replaced (BehaviorTreeQuestHost.GuardianTakesOwner) ends before it leaves the world, as
    /// retail's guardian ends the capture in place. First the channels of the new owner's team aimed at it end as a success, which their next
    /// ChannelingUpdateStats does now that the teams match (OdinCaptureChannel block 8: BBStopChanneling Success, StopSource Move), so their
    /// ChannelingSuccessStop takes their stacks off it and removes their beams. That runs here, up to one 0.25 s update early, because block 4 of
    /// that update cancels a channel whose stack is gone, and the stacks go with the buffs. Then every buff it holds ends through the lifecycle, so
    /// each OnBuffDeactivate runs: OdinGuardianSuppression's and OdinCaptureSoundFilling's BBSpellEffectRemove (odin_suppression.troy and
    /// Odin-Capture-Filling.troy, with their persistent capture sounds), OdinGuardianBuff's particles and perception bubbles. Before, the unit left
    /// with live buffs and none of these ran (owner game G1, 29 October: 11 odin_suppression and 8 Odin-Capture-Filling never killed, all bound to
    /// replaced guardians). The other team's channels are left to RetargetCaptureChannels.
    /// </summary>
    internal void EndReplacedGuardian(Entity guardian, Team owner)
    {
        if (_host is null) return;
        _endedClocks.Clear();
        foreach (var channel in _host.Channels.Active.Where(channel => ReferenceEquals(channel.Target, guardian) && channel.Caster.Team == owner).ToArray())
            _host.Channels.Stop(channel.Caster, BbChannelingStopCondition.ChannelingStopCondition_Success, BbChannelingStopSource.ChannelingStopSource_Move);
        foreach (var buff in guardian.Buffs.ToArray())
        {
            if (buff.Slot < 0 || !guardian.Buffs.Contains(buff)) continue;
            if (_host.Buffs.InstanceOf(buff) is { } ended)
                _endedClocks[(buff.Name, buff.CasterNetId)] = (ended.StartTime, ended.InstanceVars[ScriptStartVar]);
            _host.Spells.Buffs.Remove(guardian, buff.Slot);
        }
    }

    /// <summary>
    /// Batch 567: the clocks of the buffs <see cref="EndReplacedGuardian"/> just ended, by name and caster: the instance's start
    /// (BBGetBuffStartTime) and the script's own <see cref="ScriptStartVar"/>. <see cref="CarryGuardianBuffs"/> runs next in the same
    /// replacement (BehaviorTreeQuestHost.GuardianTakesOwner) and gives them back to the carried copies.
    /// </summary>
    private readonly Dictionary<(string Name, uint Caster), (double Start, object? ScriptStart)> _endedClocks = new();

    /// <summary>The script clock a carried copy keeps: OdinGuardianSuppression stamps it in OnBuffActivate (block 2, <c>BBGetGameTime</c>).</summary>
    private const string ScriptStartVar = "StartTime";

    /// <summary>
    /// Batch 479h (Reconstruction): the suppression and the team stacks a replaced capture guardian held are added to its replacement, with their
    /// caster and the time they had left. A team stack is one add per stack (BUFF_STACKS_AND_RENEWS, as the channel adds it); the suppression is one
    /// renewing add, which runs its activation on the new unit (the stun, the particle, the sound's buff). Retail keeps the guardian and changes its
    /// team in place, so these buffs stay on it.
    /// Batch 567: <see cref="BuffState.ExpiresAt"/> is the time a buff has left (BuffLifecycle.Tick counts it down), not a game time. It was read
    /// as one (minus the current time, floored at 0.5), so every carried copy ended 0.5 s after the replacement, and the enemy channel that went on
    /// cancelled itself at its next update after that (OdinCaptureChannel ChannelingUpdateStats block 5: its team stack gone).
    /// Batch 567 (Reconstruction): each copy keeps the clock of the instance it replaces (<see cref="_endedClocks"/>). The re-added suppression's
    /// activation stamps a new StartTime (OnBuffActivate block 2), and its update adds the Filling or Emptying capture sound only from
    /// <c>TimePassed &gt;= 1.5</c> (BuffOnUpdateActions block 1.3), so an enemy channel that went on across the change of hands fell silent for
    /// 1.5 s. Retail's guardian keeps its instance and its StartTime. Each caster's team stack is its own buff (BUFF_STACKS_AND_RENEWS is per
    /// caster, BbBuffRuntime.Add) and is carried with its own caster and time left.
    /// </summary>
    internal void CarryGuardianBuffs(Entity replacement, IReadOnlyList<(string Name, uint CasterNetId, double ExpiresAt, int Stacks)> carried)
    {
        if (_host is null) return;
        foreach (var (name, casterNetId, remaining, stacks) in carried)
        {
            var caster = _host.Spells.World.Get(casterNetId) ?? replacement;
            // Batch 550: a channel's PrilisasBlessing copy is one add, as its ChannelingStart makes it (BUFF_REPLACE_EXISTING, MaxStack 1).
            // Batch 570: the quest mark is the add CharScriptOdinQuestIndicator makes (BUFF_REPLACE_EXISTING, BUFF_Aura, MaxStack 1). Its
            // caster is the old guardian itself (AttackerVar Unit), gone from the world, so the copy names the replacement.
            var (addType, maxStack, count, type) = name switch
            {
                "OdinGuardianSuppression" => ((double)BbBuffAddType.BUFF_RENEW_EXISTING, 1000.0, 1, BbBuffType.BUFF_Internal),
                "PrilisasBlessing" => ((double)BbBuffAddType.BUFF_REPLACE_EXISTING, 1.0, 1, BbBuffType.BUFF_Internal),
                "OdinQuestIndicator" => ((double)BbBuffAddType.BUFF_REPLACE_EXISTING, 1.0, 1, BbBuffType.BUFF_Aura),
                _ => ((double)BbBuffAddType.BUFF_STACKS_AND_RENEWS, 1000.0, Math.Max(1, stacks), BbBuffType.BUFF_Internal),
            };
            for (var index = 0; index < count; index++)
                _host.Buffs.AddFromHost(replacement, caster, name, addType, maxStack, remaining, null, type);
            if (!_endedClocks.TryGetValue((name, casterNetId), out var clock) ||
                replacement.Buffs.FirstOrDefault(buff => buff.Name == name && buff.CasterNetId == caster.NetId) is not { } added ||
                _host.Buffs.InstanceOf(added) is not { } copy) continue;
            copy.StartTime = clock.Start;
            if (clock.ScriptStart is not null) copy.InstanceVars[ScriptStartVar] = clock.ScriptStart;
        }
        _endedClocks.Clear();
    }

    /// <summary>
    /// Batch 480b (Reconstruction): the hero whose channel is aimed at <paramref name="guardian"/> and belongs to <paramref name="team"/>, the capturer
    /// of the point the guardian holds (the channel still runs when the owner changes; see BehaviorTreeQuestHost.GuardianTakesOwner). Null when none.
    /// </summary>
    public Entity? CapturerOf(Entity guardian, Team team) =>
        _host?.Channels.Active.FirstOrDefault(channel => ReferenceEquals(channel.Target, guardian) && channel.Caster.Team == team)?.Caster;

    public Entity? CaptureChannelTarget(Entity hero) =>
        _host?.Channels.Of(hero) is { Script.Name: CaptureChannelSpellName } channel ? channel.Target : null;

    /// <summary>
    /// Batch 517 (Reconstruction, owner finding 31 October 2026: the capture started at the click, not with the channel animation): the guardian
    /// the hero's capture channel is aimed at once that channel has charged, or null. The shipped OdinCaptureChannel charges for 1.5 s:
    /// ChannelingStart plays Channel_WNDUP; ChannelingUpdateActions block 1 (AccumTime 1.5 or more) unlocks it and loops Channel;
    /// ChannelingUpdateStats block 1 (AccumTime above 1.5) sets InstanceVars.ChargeTimePassed to 1 and swaps the beam for
    /// OdinCaptureBeamEngaged. OdinGuardianSuppression's drain waits the same 1.5 s (BuffOnUpdateActions block 1.3). The point's tree counted
    /// the hero from the channel's first tick (<see cref="CaptureChannelTarget"/>), during the wind-up. The tree reads this one instead; the
    /// bots and the checks still see the channel from its start.
    /// </summary>
    public Entity? ChargedCaptureTarget(Entity hero) =>
        _host?.Channels.Of(hero) is { Script.Name: CaptureChannelSpellName } channel &&
        channel.InstanceVars["ChargeTimePassed"] is double charged && charged != 0.0
            ? channel.Target : null;

    /// <summary>The game time each hero's use slot (<see cref="UseChannelSlot"/>) is ready again, set by <see cref="SetUseSlotCooldown"/>.</summary>
    private readonly Dictionary<Entity, double> _useSlotReadyAt = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Batch 494 (Reconstruction, <c>BBSetUseSlotSpellCooldownTime</c>): the hero's use slot cools down for <paramref name="seconds"/>, and the
    /// hero's client is told with a 0x8A for slot 0x33 (owner client only, as every 0x8A), so its own click gate refuses uses until then.
    /// </summary>
    internal void SetUseSlotCooldown(Entity hero, double seconds)
    {
        if (_host is null) return;
        _useSlotReadyAt[hero] = _host.Spells.CurrentTime + seconds;
        _host.Spells.Effects.SendCooldown(hero, UseChannelSlot, seconds);
    }

    /// <summary>Batch 494: whether the hero's use slot is still cooling down (<c>GetCanUseObjectReason</c> reason 1).</summary>
    public bool UseSlotCoolingDown(Entity hero) =>
        _host is not null && _useSlotReadyAt.TryGetValue(hero, out var readyAt) && readyAt > _host.Spells.CurrentTime;

    /// <summary>Batch 494: whether the hero's running channel is the use spell <paramref name="spellName"/> aimed at <paramref name="used"/>.</summary>
    public bool ChannelsUseAt(Entity hero, Entity used, string spellName) =>
        _host?.Channels.Of(hero) is { } channel && ReferenceEquals(channel.Target, used) &&
        string.Equals(channel.Script.Name, spellName, StringComparison.OrdinalIgnoreCase);

    public const string CaptureChannelSpellName = "OdinCaptureChannel";

    /// <summary>The slot a channel that is no slot spell carries (batch 449, Reconstruction).</summary>
    internal const int CaptureChannelNoSlot = -1;

    private void StartRecallChannel(Entity hero, BbLoadoutBinding binding, Position3 at) =>
        _host!.Channels.Begin(binding.Script, hero, SpellSystem.RecallSlot, binding.Rank, 0.0, at, at, hero);

    /// <summary>
    /// Seconds of Recall left: the windup to the cast frame plus the channel's own time. 0 when the hero does not recall.
    /// </summary>
    public double RecallRemaining(Entity hero)
    {
        if (_host is null) return 0.0;
        var windup = _recallWindups.TryGetValue(hero, out var pending) ? pending.Remaining : 0.0;
        return _host.Channels.Of(hero) is { } channel && IsRecall(channel) ? windup + channel.Remaining : windup;
    }

    /// <summary>The TeleportHome effect the running Recall holds (its <c>EffectIDVar</c>), 0 when there is none.</summary>
    public uint RecallEffectId(Entity hero) =>
        _host?.Channels.Of(hero) is { } channel && IsRecall(channel) && channel.InstanceVars["ParticleID"] is double id
            ? (uint)id : 0u;

    /// <summary>
    /// Ends the hero's Recall (a death, a zombie's revival): the windup is dropped, or the channel stops as a cancel from its
    /// death, so its <c>ChannelingCancelStop</c> removes the TeleportHome effect and the Recall buff. False when none runs.
    /// </summary>
    public bool CancelRecall(Entity hero)
    {
        if (_host is null) return false;
        var windup = _recallWindups.Remove(hero);
        if (!IsRecall(_host.Channels.Of(hero))) return windup;
        _host.Channels.Stop(hero, BbChannelingStopCondition.ChannelingStopCondition_Cancel,
            BbChannelingStopSource.ChannelingStopSource_Die);
        return true;
    }

    /// <summary>
    /// Once per spell step: the windups count down into their channels, and the summoner channels run their own clock and stop
    /// checks (<see cref="BbScriptChannels.Tick"/>: death, a lost target, a stun or a displacement, then the channel's success
    /// at its <c>ChannelDuration</c>). A windup whose hero died is dropped.
    /// </summary>
    public void Tick(double dt)
    {
        if (_host is null) return;
        // The channels already running take this step. A windup that reaches its cast frame inside this step starts its channel with
        // the time the step ran past the frame, so the channel is exactly ChannelDuration long whatever the step size (batch 179 review).
        _host.Channels.Tick(dt);
        foreach (var (hero, pending) in _recallWindups.ToArray())
        {
            if (!hero.Alive)
            {
                _recallWindups.Remove(hero);
                continue;
            }
            var remaining = pending.Remaining - dt;
            if (remaining > 1e-9)
            {
                _recallWindups[hero] = (pending.Binding, pending.At, remaining);
                continue;
            }
            _recallWindups.Remove(hero);
            StartRecallChannel(hero, pending.Binding, pending.At);
            if (_host.Channels.Of(hero) is { } channel)
            {
                // remaining is at most zero here: the seconds the step ran past the cast frame, which the channel has already run.
                channel.Remaining += remaining;
                channel.Elapsed -= remaining;
            }
        }
    }
}
