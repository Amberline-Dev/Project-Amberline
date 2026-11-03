using System.Runtime.CompilerServices;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// One live script buff: the shipped script, its <c>InstanceVars</c> (the copy of the <c>BuffVarsTable</c> the add call
/// handed over) and where it came from. The production buff system keeps one <see cref="BuffState"/> per name (and per caster for
/// the stacking add types); a stacking buff has one instance whose <see cref="Stacks"/> list holds a record per stack, so that each stack
/// runs its own activation and deactivation on its own variables (retail keeps an instance per stack).
/// </summary>
internal sealed class BbBuffInstance
{
    public required BbScript Script { get; init; }
    public required string Name { get; init; }
    public required Entity Owner { get; init; }
    public required BuffState State { get; set; }
    public uint CasterNetId { get; set; }
    public BbTable InstanceVars { get; set; } = new();
    /// <summary>The spell slot and rank that started the chain (damage recap origin, <c>Level</c> of the hooks), or -1.</summary>
    public int SpellSlot { get; set; } = -1;
    public int SpellRank { get; set; }
    public double ManaCost { get; set; }
    /// <summary>
    /// The recap identity this chain keeps when it has no champion spell behind it: the item whose own window opened
    /// it (3184 Entropic Claymore, whose <c>Spells/OdinEntropicClaymore.luaobj</c> hit hook adds EntropyBurning).
    /// Inherited by every buff the chain adds (<see cref="BbBuffRuntime.Add"/>) and read by
    /// <c>BbHookFrame.RecapOrigin</c>, because <c>BBApplyDamage</c> carries no recap field of its own and a chain
    /// with neither this nor a spell slot would leave its damage unclassified in the recap. Null for a chain a
    /// champion spell started: its frame answers with the slot and rank it inherited instead.
    /// </summary>
    public DeathRecapOrigin? RecapOrigin { get; set; }
    public double TickRate { get; set; }
    public double TickElapsed { get; set; }
    /// <summary>
    /// The game-clock instant of the next <c>BuffOnUpdateStats</c> sample (the native keeps its own stamp for that hook
    /// next to the update-actions one: <c>mLastTickTimeUpdateStats</c>), on the grid of
    /// <see cref="BbBuffRuntime.StatsUpdateInterval"/>. It is an instant of the clock <c>BBExecutePeriodically</c> reads
    /// (<c>IBbHost.GetTime</c>), not a sum of update steps, so a periodic inside the stats hook meets its due line on the
    /// sample that reaches it. The add or renewal that takes the sample sets it; one sample per update, overdue time kept
    /// the way <see cref="TickElapsed"/> is (the line moves one interval per sample).
    /// </summary>
    public double StatsNextAt { get; set; }
    /// <summary>
    /// The game-clock instant of the next bare <c>BuffOnUpdateActions</c> run when the add carried no <c>TickRate</c>
    /// (<see cref="BbBuffRuntime.ActionsUpdateInterval"/>): the native's <c>mLastTickTimeUpdateActions</c> stamp beside
    /// <see cref="StatsNextAt"/>. One run per update at most; the add that creates the instance stamps it one interval after its own
    /// clock, so a buff's lines are the add + k * the interval (PickACard's even seed and its 2/4/6 turns are written to that phase,
    /// batch 175). A buff that ends before its first line gets that one run at its first update (<see cref="ActionsFirstLinePending"/>).
    /// After every run the line advances by whole intervals to the first line after it, so a line the clock has passed is skipped,
    /// not repaid one update at a time (the burst the owner saw on first use, batch 175), and the add's phase is kept. A renewal
    /// keeps it. A buff with a <c>TickRate</c> runs on <see cref="TickElapsed"/> instead and this line is not read.
    /// </summary>
    public double ActionsNextAt { get; set; }
    /// <summary>
    /// The first bare <c>BuffOnUpdateActions</c> line of this life has not come yet. A buff that ends before that line takes its one
    /// run at its first update instead (<see cref="BbBuffRuntime.OnTick"/>): the shipped "do it once" applicators live shorter than
    /// the interval (<c>FrostArrowApplicator</c> and <c>ToxicShotApplicator</c> 0.1 s, Renekton's unlock animations 0.01 s) and their
    /// hook is the whole buff — it removes itself or releases the slot it sealed. One run only: the line then moves a full interval
    /// on, past the buff's end, so a short-lived buff cannot spin. A renewal keeps the flag the way it keeps
    /// <see cref="ActionsNextAt"/>.
    /// </summary>
    public bool ActionsFirstLinePending { get; set; }
    /// <summary>
    /// The update hook carries a <c>BBExecutePeriodically</c> of the script's own. Its due test runs inside the hook body, on the host
    /// clock, so that hook has to be polled every buff update for the script's period to resolve (Tryndamere's Spinning Slash scans every
    /// 0.1 s, Annie's burning every 1.0, Brand's blaze every 1.05, the Bushwhack trap every 0.9): the hook is the periodic's caller, not a
    /// timer of its own. A hook without one is a bare poll and runs on the add's <see cref="BbBuffRuntime.ActionsUpdateInterval"/> tick.
    /// </summary>
    public bool ActionsCarriesPeriodic { get; set; }
    /// <summary>
    /// The game-clock instant the next bare mutation of this buff's <c>BuffOnUpdateStats</c> accumulates at
    /// (Reconstruction M4, "contributions are re-sampled; mutations are ticked"). A <c>BBIncPermanentStat</c> or a
    /// perception bubble adds to the unit once per the add's <c>TickRate</c> when the add carried one (first at add +
    /// TickRate, the line <c>TurretRules.BonusStacks</c> used to stack on), and once for the add's own sample when it
    /// carried none (TurretBonusHealth's one-shot health and bubble). A mutation inside a <c>BBExecutePeriodically</c>
    /// follows that periodic's clock instead (TurretDecay and ResistantSkin wrap theirs in a 60 s one) and is not gated
    /// here. A renewal keeps the clock the way <see cref="StartTime"/> is kept, so it cannot re-accumulate. The add that
    /// creates the instance stamps it; a due sample moves it one tick on — infinitely far for a buff with no tick rate,
    /// which accumulates once and never again.
    /// </summary>
    public double MutateNextAt { get; set; }
    /// <summary>The game time the buff was created (BBGetBuffStartTime); a renewal keeps it.</summary>
    public double StartTime { get; set; }
    /// <summary>
    /// The add that created the instance landed shorter than its <c>Duration</c>: a held buff's <c>BuffOnAllowAdd</c> cut it (Hardening's
    /// tenacity, Insanity Potion's CCreduction, Cleanse's window). Such a buff is no longer sized to anything its script measured, so a dash
    /// it starts does not stretch it to the trip (<c>BbScriptMoves.Track</c>; batch 296).
    /// </summary>
    public bool Shortened { get; set; }
    /// <summary>When the client's copy of the buff ends: the last wire add or renewal plus the duration it carried.</summary>
    public double WireEndsAt { get; set; }
    /// <summary>
    /// When this life of the buff runs out on its holder, from the lifecycle's own countdown (see <see cref="BbBuffRuntime.OnTick"/>).
    /// A block that schedules itself periodically runs only for a due time strictly before it (<c>BBExecutePeriodically</c>).
    /// </summary>
    public double LifeEndsAt { get; set; }
    /// <summary>
    /// How long the buff has been on its holder as of the last update (the <c>LifeTime</c> variable of its hooks), in seconds. The update that
    /// finds the buff run out counts one step more than the time reached: see <see cref="BbBuffRuntime.OnTick"/>.
    /// </summary>
    public double LifeTime { get; set; }
    /// <summary>The time the updates have added up to (not counting the step the expiring update adds to <see cref="LifeTime"/>).</summary>
    public double Age { get; set; }
    /// <summary>
    /// The statuses the buff's <c>OnBuffActivate</c> set (XenZhaoSweep: SetCanMove(false); Poppy's charge: SetCanAttack(false)) and its
    /// <c>OnBuffDeactivate</c> has not cleared yet. A status bit set outside the stats hook persists in retail until a script clears it, so every
    /// stats sample of the buff holds them.
    /// </summary>
    public Dictionary<BbSetStatus, bool> HeldStatuses { get; } = new();
    /// <summary>What the last <c>BuffOnUpdateStats</c> sample asked for; a different sample replaces the buff's stat payload.</summary>
    public string StatsSignature { get; set; } = "";
    /// <summary>The internal buffs that carry the stats this buff's stats hook asked for on other units (by unit net id).</summary>
    public Dictionary<uint, BuffState> ForeignStats { get; } = new();
    /// <summary>Its <c>BuffOnDeath</c> already ran for the death in progress (the zombie question is asked before the death packet), so the death hook skips it.</summary>
    public bool DeathHookRan { get; set; }
    /// <summary>
    /// The effects an <c>OnBuffActivate</c> run lost track of: a stacking buff runs the hook again for every stack on its one instance and
    /// shares its <c>InstanceVars</c>, so a stack's <c>BBSpellEffectCreate</c> overwrites the effect id the previous stack stored (Ezreal's
    /// Rising Spell Force creates a glow per stack in <c>Particle</c>). Retail keeps one instance per stack and each removes its own effect;
    /// here the overwritten ids wait in this list and go with the buff (<see cref="BbBuffRuntime.OnRemoved"/>).
    /// </summary>
    public List<uint> SupersededEffects { get; } = new();
    /// <summary>
    /// One record per live stack, oldest first (a buff added once has one). Retail keeps an instance per stack, each with the variables its own
    /// <c>OnBuffActivate</c> worked on and its own <c>OnBuffDeactivate</c>; here the hooks that run once per buff (updates, damage hooks) read the
    /// newest stack's table, <see cref="InstanceVars"/>, and the activation and deactivation hooks run for each stack with that stack's own.
    /// </summary>
    public List<BbBuffStack> Stacks { get; } = new();
}

/// <summary>A stack of a script buff: what its <c>OnBuffActivate</c> was handed and wrote (see <see cref="BbBuffInstance.Stacks"/>).</summary>
internal sealed class BbBuffStack
{
    public required BbTable Vars { get; init; }
    /// <summary>The spell chain that added the stack (the <c>Level</c> and the recap origin of its hooks), or -1.</summary>
    public int SpellSlot { get; init; } = -1;
    public int SpellRank { get; init; }
    public double ManaCost { get; init; }
}

/// <summary>
/// What <c>BBSpellBuffAdd</c> resolved before the buff is created. <paramref name="StacksExclusive"/> is the add's <c>StacksExclusive</c> key
/// (the client parse defaults it to true); a host-made add leaves it false. <paramref name="HiddenOnClient"/> is the add's
/// <c>IsHiddenOnClient</c> (default false): it becomes the <c>isHidden</c> byte of the buff's 0xBF (<see cref="BuffState.HiddenOnClientByte"/>).
/// <paramref name="RecapOrigin"/> is what a host-made add states its chain's damage is (see
/// <see cref="BbBuffInstance.RecapOrigin"/>); a script's own add leaves it null and takes the identity of the buff that made it.
/// </summary>
internal sealed record BbBuffRequest(
    Entity Target, Entity Caster, string Name, int AddType, int ScriptBuffType, int MaxStack,
    double Duration, double TickRate, bool CanMitigateDuration, BbTable Vars, int Stacks = 1, bool StacksExclusive = false,
    bool HiddenOnClient = false, DeathRecapOrigin? RecapOrigin = null);
