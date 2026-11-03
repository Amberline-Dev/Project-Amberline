using Amberline.Core;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Bots;

/// <summary>What the Dominion objective chose for a bot on its last priority update (map 8 only).</summary>
internal enum DominionPlan
{
    None,
    /// <summary>The opening ceremony holds the champions in their base (batch 458): wait at the start point.</summary>
    Hold,
    /// <summary>Walk to a point the team does not hold and channel its capture on the guardian.</summary>
    Capture,
    /// <summary>The bot channels a capture: give no order (any order ends the channel).</summary>
    Channel,
    /// <summary>An enemy channels on a point the team holds nearby: go there (the shipped KillHero task fights the channeler).</summary>
    Defend,
    /// <summary>Low health with a health relic near: walk over it.</summary>
    Relic,
}

// NOT RETAIL: Dominion bots. The 1.0.0.126 files ship no Dominion bot logic: no shipped Bot or Task script mentions capture, relic or
// victory, as section 3.9 of docs/research/2026-10-28-dominion-plan.md records, and retail added Dominion bots in a later patch. On map 8 the shipped
// Bot.luaobj keeps its own arbiter and its fighting, retreating, shopping and casting tasks (CastSpell, Retreat, KillLowHPMinion, Buy Item,
// Kill Hero), and its lane tasks (Push Lane, KillNearbyStruct, Defend <structure>), which have no lanes or attackable structures there, are
// replaced by one task of this engine's, "Capture Point". The task is a table in the script's own task list whose UpdatePriority, BeginTask and
// Tick are natives here, so the shipped arbiter weighs it against the shipped tasks with their own numbers. Its one draw is the back-off's
// seeded jitter (batch 468, below).
public sealed partial class BotController
{
    /// <summary>Batch 463: map 8's Dominion host (its capture points); null on every other map, where the task list is the shipped one.</summary>
    internal BehaviorTreeQuestHost? Quests { get; set; }

    /// <summary>
    /// Batch 463: the player's capture order (ProductionGameplayDispatcher.OrderCapture): within the capture range it starts the channel behind
    /// the order's gates, from out of range it marks the bot's walk to the guardian so the channel starts on arrival.
    /// </summary>
    internal Func<Entity, Entity, bool>? CaptureOrder { get; set; }

    internal const string DominionTaskName = "Capture Point";
    private const string GuardianName = "OdinNeutralGuardian";
    private const string HealthRelicName = "OdinShieldRelic";

    // The task's priorities, set against the shipped tasks' own numbers, as the rules table of docs/research/patch126-bot-scripts.md gives them: above
    // KillLowHPMinion (0.45) so the bots do not farm on the way, below an engaged KillHero (0.5) so they fight a hero that comes close. A held
    // channel outranks the fight (a hit from that hero breaks it, the shipped rule, and the fight follows). A relic outranks Retreat at moderate
    // damage (Retreat is (0.25 * closeness + 0.75) * missing health). Anything Buy Item scores near the base (0.8) still shops first.
    // Through the opening hold the task asks for little, enough to beat the idle zeros and to keep the bot home, and Buy Item shops first.
    private const double HoldPriority = 0.01;
    private const double CapturePriority = 0.47;
    private const double DefendPriority = 0.49;
    private const double ChannelPriority = 0.55;
    private const double RelicPriority = 0.6;
    // Every teammate already heading for a point counts as this much more distance to it, so the team spreads over the points.
    private const double SpreadPenalty = 2000.0;
    private const double DefendRange = 3500.0;
    private const double RelicRange = 2000.0;
    private const double RelicHealthFraction = 0.5;
    private const double HoldSlack = 300.0;
    // Batch 468 (NOT RETAIL: retail 1.0.0.126 has no Dominion bots, so the bots' pacing is this engine's choice). A capture channel that ends
    // on a point its team does not hold (the other team channels the same point, both channels end on the shipped suppression; or the
    // channel is interrupted) leaves its bot out of that point for this long, plus a seeded draw up to the jitter, so a team does not retry
    // in lockstep. Without it the bots re-ordered the channel every 0.25 to 0.5 s (about 580 channel starts in the batch 467 match).
    private const double CaptureBackoffSeconds = 4.0;
    private const double CaptureBackoffJitterSeconds = 2.0;

    private readonly record struct DominionPoint(Position2 Position, Team Owner, Entity? Guardian, bool OrderChannels, bool ChaosChannels);

    private DominionPoint[] _points = [];
    private readonly List<Entity> _relics = new();

    /// <summary>Once per controller step: each point's place, owner, guardian and who channels on it, and the living health relics.</summary>
    private void RefreshDominion()
    {
        var captures = Quests!.Captures;
        if (_points.Length != captures.Count) _points = new DominionPoint[captures.Count];
        var guardians = new List<Entity>();
        _relics.Clear();
        foreach (var unit in _world.Entities.Values)
        {
            if (!unit.Alive) continue;
            if (unit.ChampionName == GuardianName) guardians.Add(unit);
            else if (unit.ChampionName == HealthRelicName) _relics.Add(unit);
        }
        for (var index = 0; index < captures.Count; index++)
        {
            var at = captures[index].Variable("CapturePosition") is Position3 position ? new Position2(position.X, position.Z) : new Position2(double.NaN, double.NaN);
            var owner = captures[index].Variable("CurrentPointOwner") is Team held ? held : Team.Neutral;
            var guardian = guardians.FirstOrDefault(unit => DistanceSquared(unit.Position, at) <= BehaviorTreeQuestHost.CaptureRadius * BehaviorTreeQuestHost.CaptureRadius);
            bool order = false, chaos = false;
            if (guardian is not null)
                foreach (var participant in _participants)
                    if (participant.Hero is { Alive: true } channeler && ReferenceEquals(_spells.CaptureChannelTarget(channeler), guardian))
                    {
                        order |= channeler.Team == Team.Order;
                        chaos |= channeler.Team == Team.Chaos;
                    }
            _points[index] = new DominionPoint(at, owner, guardian, order, chaos);
        }
        // Batch 468 (NOT RETAIL): read every step, so a channel that ends between two of the script's 250 ms updates is seen. A channel that
        // ends on a point its team does not hold puts this seat's back-off on that point (the owner is read after the channel's end).
        foreach (var participant in _participants)
        {
            if (participant.Hero is not { } hero || !_stateByHero.TryGetValue(hero.NetId, out var state)) continue;
            if (_spells.CaptureChannelTarget(hero) is { } channelled)
            {
                if (channelled.NetId == state.ChannelTarget) continue;
                state.Measure.CaptureChannelStarts++;
                state.ChannelTarget = channelled.NetId;
                state.ChannelPoint = Array.FindIndex(_points, point => ReferenceEquals(point.Guardian, channelled));
            }
            else if (state.ChannelTarget != 0)
            {
                if (state.ChannelPoint >= 0 && _points[state.ChannelPoint].Owner != hero.Team)
                {
                    state.BackoffPoint = state.ChannelPoint;
                    state.BackoffUntil = _combat.CurrentTime + CaptureBackoffSeconds + _random.Uniform(0.0, CaptureBackoffJitterSeconds);
                }
                state.ChannelTarget = 0;
                state.ChannelPoint = -1;
            }
        }
    }

    /// <summary>
    /// Once the script's <c>TimerHackDelayedInit</c> has built <c>tasks</c>: the lane tasks leave the list and the capture task joins it where
    /// the Push Lane tasks stood (after KillLowHPMinion), so the arbiter's tie order among the shipped tasks is unchanged.
    /// </summary>
    private void InstallDominionTask(LuaAiInstance instance, Entity hero, BotState state)
    {
        if (state.DominionTask is not null) return;
        var tasks = instance.GetGlobal("tasks");
        if (tasks.Type != LuaType.Table || tasks.AsTable.Length == 0) return;
        var list = tasks.AsTable;
        var task = new LuaTable();
        task.Set(LuaValue.Of("Name"), LuaValue.Of(DominionTaskName));
        task.Set(LuaValue.Of("Priority"), LuaValue.Of(0.0));
        task.Set(LuaValue.Of("UpdatePriority"), new LuaNative("UpdatePriority", (_, args) =>
        {
            SelfOf(args)?.Set(LuaValue.Of("Priority"), LuaValue.Of(DominionPriority(hero, state)));
            return [];
        }));
        LuaNativeFunc tick = (_, _) => { DominionTick(hero, state); return []; };
        task.Set(LuaValue.Of("BeginTask"), new LuaNative("BeginTask", tick));
        task.Set(LuaValue.Of("Tick"), new LuaNative("Tick", tick));
        // Bot.luaobj OnLostTarget forwards to the active task's OnTargetLost; the shipped handlers are a bare RETURN, and so is this one.
        task.Set(LuaValue.Of("OnTargetLost"), new LuaNative("OnTargetLost", (_, _) => []));

        var kept = new List<LuaValue>();
        var placed = false;
        for (var index = 1; index <= list.Length; index++)
        {
            var entry = list.Get(LuaValue.Of((double)index));
            var name = entry.Type == LuaType.Table && entry.AsTable.Get(LuaValue.Of("Name")) is { Type: LuaType.String } label ? label.AsString : "";
            if (name == "KillNearbyStruct" || name.StartsWith("Push Lane ", StringComparison.Ordinal) || name.StartsWith("Defend ", StringComparison.Ordinal))
            {
                if (!placed) { kept.Add(task); placed = true; }
                continue;
            }
            kept.Add(entry);
        }
        if (!placed) kept.Add(task);
        for (var index = list.Length; index >= 1; index--) list.Set(LuaValue.Of((double)index), LuaValue.Nil);
        for (var index = 0; index < kept.Count; index++) list.Set(LuaValue.Of((double)(index + 1)), kept[index]);
        state.DominionTask = task;
    }

    private static LuaTable? SelfOf(LuaValue[] args) => args.Length > 0 && args[0].Type == LuaType.Table ? args[0].AsTable : null;

    /// <summary>UpdatePriority: chooses the plan and answers its priority (0 when there is nothing to take, hold or defend).</summary>
    private double DominionPriority(Entity hero, BotState state)
    {
        state.Plan = DominionPlan.None;
        state.PlanSubject = 0;
        if (!hero.Alive) return 0.0;
        // The opening: the ceremony holds the champions until its release (SetUnitCircularMovementRestriction, 45 s); the bots wait at their
        // start point instead of walking to the barrier, and the release lifts the hold, so the next update chooses a point.
        if (hero.MovementRestriction is not null)
        {
            state.Plan = DominionPlan.Hold;
            return HoldPriority;
        }
        // A channel in progress keeps its point.
        if (_spells.CaptureChannelTarget(hero) is { } channelled)
        {
            state.Plan = DominionPlan.Channel;
            state.PlanSubject = channelled.NetId;
            state.CapturePoint = Array.FindIndex(_points, point => ReferenceEquals(point.Guardian, channelled));
            return ChannelPriority;
        }
        if (hero.Health <= RelicHealthFraction * hero.MaxHealth && Nearest(hero, _relics, RelicRange) is { } relic)
        {
            state.Plan = DominionPlan.Relic;
            state.PlanSubject = relic.NetId;
            return RelicPriority;
        }
        var defend = -1;
        for (var index = 0; index < _points.Length; index++)
        {
            var point = _points[index];
            var enemyChannels = hero.Team == Team.Order ? point.ChaosChannels : point.OrderChannels;
            if (point.Owner != hero.Team || !enemyChannels || point.Guardian is null) continue;
            if (DistanceSquared(hero.Position, point.Position) > DefendRange * DefendRange) continue;
            if (defend < 0 || DistanceSquared(hero.Position, point.Position) < DistanceSquared(hero.Position, _points[defend].Position)) defend = index;
        }
        if (defend >= 0)
        {
            state.Plan = DominionPlan.Defend;
            state.PlanSubject = _points[defend].Guardian!.NetId;
            return DefendPriority;
        }
        // Batch 468 (NOT RETAIL): the point its last channel was contested on stays this bot's point through the back-off, with no channel and no
        // walk to another point: the bot fights a hero that comes near (the shipped KillHero task) or holds.
        if (InBackoff(state, state.CapturePoint)) return 0.0;
        if (!Capturable(hero, state.CapturePoint)) state.CapturePoint = PickPoint(hero);
        if (state.CapturePoint < 0) return 0.0;
        state.Plan = DominionPlan.Capture;
        state.PlanSubject = _points[state.CapturePoint].Guardian!.NetId;
        return CapturePriority;
    }

    private bool Capturable(Entity hero, int index) =>
        index >= 0 && index < _points.Length && _points[index].Owner != hero.Team && _points[index].Guardian is not null &&
        !(_stateByHero.TryGetValue(hero.NetId, out var state) && InBackoff(state, index));

    /// <summary>Batch 468 (NOT RETAIL): whether this seat's back-off on the point (its last channel there was contested) still runs.</summary>
    private bool InBackoff(BotState state, int index) =>
        index >= 0 && index == state.BackoffPoint && _combat.CurrentTime < state.BackoffUntil;

    /// <summary>
    /// The point to take: the nearest one the team does not hold, where every teammate already heading there adds <see cref="SpreadPenalty"/>
    /// to its distance (a tie goes to the lower point id). A bot keeps its point until the team holds it or its guardian is gone.
    /// </summary>
    private int PickPoint(Entity hero)
    {
        var best = -1;
        var bestCost = double.PositiveInfinity;
        for (var index = 0; index < _points.Length; index++)
        {
            if (!Capturable(hero, index)) continue;
            var allies = 0;
            foreach (var participant in _participants)
                if (participant.Hero is { Alive: true } ally && ally.NetId != hero.NetId && ally.Team == hero.Team &&
                    _stateByHero.TryGetValue(ally.NetId, out var other) && other.CapturePoint == index)
                    allies++;
            var cost = Math.Sqrt(DistanceSquared(hero.Position, _points[index].Position)) + SpreadPenalty * allies;
            if (cost < bestCost) { best = index; bestCost = cost; }
        }
        return best;
    }

    /// <summary>BeginTask and Tick: carry the plan out with the same orders a player gives.</summary>
    private void DominionTick(Entity hero, BotState state)
    {
        // Batch 568: a running channel takes no order of this task either (the capture's own is the Channel plan, which orders nothing); a
        // walk or a capture order would end a spell channel the CastSpell task has just started (IsActionLocked).
        if (!hero.Alive || hero.ActiveScriptChannels > 0) return;
        var now = _combat.CurrentTime;
        switch (state.Plan)
        {
            case DominionPlan.Hold when hero.MovementRestriction is { } hold:
                if (DistanceSquared(hero.Position, hold.Centre) > HoldSlack * HoldSlack) IssueMove(hero, state, hold.Centre, now);
                break;
            case DominionPlan.Capture when _world.Get(state.PlanSubject) is { Alive: true } guardian:
                // The attack order on the guardian: from within range it starts the channel; otherwise the bot walks to the guardian and
                // the walk is marked so the channel starts on arrival, as a player's order from afar does.
                if (CaptureOrder?.Invoke(hero, guardian) == true) break;
                IssueMove(hero, state, guardian.Position, now);
                CaptureOrder?.Invoke(hero, guardian);
                break;
            case DominionPlan.Defend or DominionPlan.Relic when _world.Get(state.PlanSubject) is { Alive: true } subject:
                IssueMove(hero, state, subject.Position, now);
                break;
        }
    }

    private static Entity? Nearest(Entity hero, List<Entity> units, double range)
    {
        Entity? best = null;
        var bestDistance = range * range;
        foreach (var unit in units)
        {
            var distance = DistanceSquared(hero.Position, unit.Position);
            if (distance <= bestDistance) { best = unit; bestDistance = distance; }
        }
        return best;
    }
}
