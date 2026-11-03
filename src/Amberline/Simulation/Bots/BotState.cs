using Amberline.Core;

namespace Amberline.Simulation.Bots;

/// <summary>Task kinds in the order Bot.luaobj inserts them into its task list.</summary>
internal enum BotTaskKind
{
    CastSpell,
    Retreat,
    KillMinion,
    KillTower,
    PushLane,
    Defend,
    Buy,
    KillHero,
    /// <summary>Batch 463 (map 8 only, NOT RETAIL): the Dominion objective that replaces the lane tasks (BotController.Dominion.cs).</summary>
    Capture,
}

/// <summary>One entry of the script's <c>tasks</c> table.</summary>
internal sealed class BotTask(BotTaskKind kind, int lane = -1, uint subjectNetId = 0)
{
    public BotTaskKind Kind { get; } = kind;
    /// <summary>Lane index (0,1,2 = MapDefinitions.Lanes) for a push task.</summary>
    public int Lane { get; } = lane;
    /// <summary>Structure net ID for a defend task, enemy hero net ID for a kill-hero task.</summary>
    public uint SubjectNetId { get; } = subjectNetId;
    public double Priority { get; set; }
    public bool Done { get; set; }
}

/// <summary>
/// Per-seat measurements for the headless match report. Diagnostics only: nothing in the controller reads them,
/// so they cannot change what a bot does. Times are game seconds sampled at each 0.25 s update.
/// </summary>
public sealed class BotMeasure
{
    /// <summary>The <see cref="BotTaskKind"/> names in enum order, for reports outside this assembly.</summary>
    public static readonly string[] TaskNames = Enum.GetNames<BotTaskKind>();
    /// <summary>Seconds the seat's active task was this <see cref="BotTaskKind"/> (index = enum value).</summary>
    public double[] TaskSeconds { get; } = new double[TaskNames.Length];
    /// <summary>Seconds the active task was Push Lane L, C or R (the lane spread of a team).</summary>
    public double[] PushLaneSeconds { get; } = new double[3];
    /// <summary>Seconds alive with no active task at all.</summary>
    public double NoTaskSeconds { get; set; }
    /// <summary>Seconds alive, no route, no attack target and away from the fountain.</summary>
    public double IdleSeconds { get; set; }
    /// <summary>The longest unbroken run of <see cref="IdleSeconds"/> (the real AFK measure).</summary>
    public double LongestIdleRun { get; set; }
    /// <summary>Seconds with an unfinished route while the hero moved less than 10 units in the update.</summary>
    public double StuckSeconds { get; set; }
    /// <summary>The longest unbroken run of <see cref="StuckSeconds"/>.</summary>
    public double LongestStuckRun { get; set; }
    internal double IdleRun { get; set; }
    internal double StuckRun { get; set; }
    public double FountainSeconds { get; set; }
    public double AliveSeconds { get; set; }
    public int PurchaseAttempts { get; set; }
    public int Purchases { get; set; }
    public int Casts { get; set; }
    /// <summary>Per champion slot (Q, W, E, R): the draws of <c>math.random(0, 3)</c> in TaskCastSpell.BeginTask.</summary>
    public int[] CastDraws { get; } = new int[4];
    /// <summary>Per slot: draws where <c>CanUseSpell(slot) == 0</c>.</summary>
    public int[] CastUsable { get; } = new int[4];
    /// <summary>Per slot: usable draws whose cast order was accepted (cast now, or queued to walk into range).</summary>
    public int[] CastOrdered { get; } = new int[4];
    /// <summary>Per slot: usable draws that came to nothing (no sensible aim, or the cast order was refused).</summary>
    public int[] CastRefused { get; } = new int[4];
    /// <summary>Per slot: accepted orders that were queued behind a range walk (the rest cast at once).</summary>
    public int[] CastQueued { get; } = new int[4];
    /// <summary>Per slot: queued orders that executed (<c>SpellCasts</c> grew before the queue emptied).</summary>
    public int[] CastQueuedDone { get; } = new int[4];
    /// <summary>Per slot: queued orders that emptied without a cast (another order cleared them, timed out, target lost).</summary>
    public int[] CastQueuedDropped { get; } = new int[4];
    /// <summary>Per slot: queued orders replaced by the next cast order of TaskCastSpell while still waiting.</summary>
    public int[] CastQueuedSuperseded { get; } = new int[4];
    /// <summary>Draws that <c>CanUseSpell</c> refused, by slot letter and verdict ("Q:Rank", "W:Cooldown" ...).</summary>
    public Dictionary<string, int> CastGate { get; } = new();
    /// <summary>The active task when a queued cast was found dropped ("PushLane", "KillMinion" ...).</summary>
    public Dictionary<string, int> CastDroppedWhile { get; } = new();
    public int TaskSwitches { get; set; }
    /// <summary>Times the anti-kite latch went from false to true (TaskKillHero's AntiKiteTimer).</summary>
    public int AntiKiteLatches { get; set; }
    /// <summary>Seconds the latch was set.</summary>
    public double AntiKiteSeconds { get; set; }
    /// <summary>DIAGNOSTICS (batch 468): capture channels this seat started (a new OdinCaptureChannel target, BotController.Dominion.cs).</summary>
    public int CaptureChannelStarts { get; set; }
    internal Position2? LastPosition { get; set; }
    internal double LastSampleAt { get; set; } = double.NaN;
}

/// <summary>
/// Per-seat script state: the mirror of Bot.luaobj's <c>tasks</c>/<c>activeTask</c> plus the
/// order bookkeeping the engine needs. Everything a task used to compute here is the script's now.
/// </summary>
internal sealed class BotState
{
    public BotMeasure Measure { get; } = new();
    public double NextUpdateAt { get; set; }
    public bool WasAlive { get; set; } = true;

    // Bot.luaobj: tasks / activeTask, re-read from the script every step (MirrorTasks).
    public List<BotTask> Tasks { get; } = new();
    public BotTask? Active { get; set; }

    /// <summary>
    /// The task's own <c>AI_*</c> state id, as the script wrote it. A bot's tasks invent their own
    /// numbers in their module environment (TaskBuyItem's AI_MOVE is 0 and AI_SHOP 1, while
    /// TaskKillMinion's AI_FOLLOW is 1 and AI_ATTACK 0), so the id round-trips through
    /// GetState/SetState unchanged and nothing outside the module reads it.
    /// </summary>
    public double ScriptState { get; set; }

    // TaskKillHero's own module global, mirrored off whichever task table carries that module:
    // the anti-kite latch is shared by every kill-hero task of this bot (they are all
    // setmetatable'ed onto the one LoadTask('TaskKillHero.lua') table) and outlives any one of
    // them being active. Only the module's own AntiKiteTimer sets or clears it, and Bot.luaobj
    // forwards that timer only while a kill-hero task is active (Bot.txt proto 0.5), so once the
    // latch is set with no kill-hero task left it stays set — retail 2011 behaviour.
    public bool ReducePriority { get; set; }

    // Order bookkeeping (server side only).
    public int OrdersIssued { get; set; }
    public Position2? LastMoveGoal { get; set; }
    public double LastMoveAt { get; set; } = double.NegativeInfinity;
    public double LastAttackOrderAt { get; set; } = double.NegativeInfinity;
    /// <summary>Batch 581: a <c>SetStateAndMove</c> the hero could not take yet, and the active task that ordered it.</summary>
    public (double State, Position2 Point, string Task)? HeldMove { get; set; }
    public int SkillKey { get; set; } = -1;
    /// <summary>Diagnostics only: the slot and cast count of a cast order that was queued behind a range walk (-1 none).</summary>
    public int QueuedSlot { get; set; } = -1;
    public int QueuedCastsBefore { get; set; }
    public PendingCastState? QueuedPending { get; set; }

    // Batch 463 (map 8 only): the Dominion objective task (BotController.Dominion.cs), what it chose and its target.
    public Scripting.Lua.LuaTable? DominionTask { get; set; }
    public DominionPlan Plan { get; set; }
    /// <summary>The capture point (0 to 4) the bot heads for or channels on, -1 none; teammates read it to spread.</summary>
    public int CapturePoint { get; set; } = -1;
    /// <summary>The unit the plan walks to: the point's guardian or a health relic (0 none).</summary>
    public uint PlanSubject { get; set; }
    /// <summary>Batch 468 (NOT RETAIL): the guardian this seat's capture channel runs on (0 none), and that point's index (-1 none).</summary>
    public uint ChannelTarget { get; set; }
    public int ChannelPoint { get; set; } = -1;
    /// <summary>Batch 468 (NOT RETAIL): the point whose channel ended contested or interrupted (-1 none), and the time its back-off ends.</summary>
    public int BackoffPoint { get; set; } = -1;
    public double BackoffUntil { get; set; }

    /// <summary>Death drops the active task; the script's own globals survive it.</summary>
    public void OnDeath()
    {
        Active = null;
        CapturePoint = -1;
        Plan = DominionPlan.None;
        LastMoveGoal = null;
        LastMoveAt = double.NegativeInfinity;
        LastAttackOrderAt = double.NegativeInfinity;
        HeldMove = null;
        Measure.LastSampleAt = double.NaN;
        Measure.IdleRun = 0.0;
        Measure.StuckRun = 0.0;
        WasAlive = false;
    }
}
