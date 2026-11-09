using System.Runtime.CompilerServices;
using Amberline.Core.Buffs;

namespace Amberline.Core;

/// <summary>
/// A read-only observer for state machines that can remain active after their
/// owner stops advancing them.  The monitor never repairs gameplay state.  A
/// separate instance belongs to one <see cref="WorldState"/> and retains only
/// bounded identity/deadline samples for the entities currently in that world.
/// </summary>
public sealed class LifecycleMonitor
{
    public const double DefaultInterval = 0.25;
    public const double DefaultGrace = 0.50;
    public const double ExpiryGrace = 0.50;
    public const double CompletedMoveGrace = 0.75;
    private const double Epsilon = 1e-9;

    private readonly WorldState _world;
    private readonly Func<Entity, LifecycleStateView> _stateView;
    private readonly double _interval;
    private readonly double _grace;
    private readonly Dictionary<TimerKey, TimerObservation> _timers = new();
    private readonly Dictionary<MoveKey, CompletedMoveObservation> _completedMoves = new();
    private Action<LifecycleViolation>? _report;
    private Func<bool>? _reportEnabled;
    private readonly HashSet<ReportKey> _reported = new();
    private double? _lastScan;

    public LifecycleMonitor(WorldState world, double interval = DefaultInterval,
        double grace = DefaultGrace, Func<Entity, LifecycleStateView>? stateView = null,
        Action<LifecycleViolation>? report = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _stateView = stateView ?? LifecycleStateView.FromEntity;
        _report = report;
        if (!double.IsFinite(interval) || interval < 0.0)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (!double.IsFinite(grace) || grace < 0.0)
            throw new ArgumentOutOfRangeException(nameof(grace));
        _interval = interval;
        _grace = grace;
    }

    /// <summary>
    /// Binds the session logger after the match graph has been composed.  The
    /// monitor remains usable in tests and strict mode without a logger.
    /// </summary>
    public void AttachReporter(Action<LifecycleViolation> report, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_report is not null)
            throw new InvalidOperationException("lifecycle reporter is already attached");
        _report = report;
        _reportEnabled = enabled;
    }

    /// <summary>Whether the runtime should pay for a diagnostic scan now.</summary>
    public bool ShouldScan
    {
        get
        {
            if (_world.StrictInvariants) return true;
            if (_report is null) return false;
            try { return _reportEnabled?.Invoke() ?? true; }
            catch { return false; }
        }
    }

    public IReadOnlyList<LifecycleViolation> Records { get; private set; } =
        Array.Empty<LifecycleViolation>();

    /// <summary>Forget observations without changing any world or entity state.</summary>
    public void Reset()
    {
        _lastScan = null;
        _timers.Clear();
        _completedMoves.Clear();
        _reported.Clear();
        Records = Array.Empty<LifecycleViolation>();
    }

    /// <summary>
    /// Scan the current world at an absolute simulation time.  Timer values in
    /// champion and buff state are remaining seconds; unit <see cref="Entity.ExpiresAt"/>
    /// values are absolute match-clock deadlines, matching their owning APIs.
    /// </summary>
    public IReadOnlyList<LifecycleViolation> Check(double? now = null, bool force = false)
    {
        var time = now ?? _world.Clock.Now();
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(now));
        if (_lastScan is double previous && time + Epsilon < previous) Reset();
        if (!force && _lastScan is double last && time - last < _interval - Epsilon)
            return Records;
        _lastScan = time;

        var timerSeen = new HashSet<TimerKey>();
        var violations = new List<LifecycleViolation>();
        CheckCastTimers(time, timerSeen, violations);
        CheckRevivals(time, violations);
        CheckExpiries(time, violations);
        CheckForcedMoves(time, timerSeen, violations);

        foreach (var key in _timers.Keys.Where(key => !timerSeen.Contains(key)).ToArray())
            _timers.Remove(key);

        Records = violations;
        if (_world.StrictInvariants && violations.Count > 0)
            throw new InvalidOperationException($"lifecycle violation: {Format(violations[0])}");
        // Match the source logger's edge semantics: a persistent active
        // violation is emitted once, then becomes eligible again only after
        // it disappears and later reappears.  The retained set is bounded by
        // the current record set and therefore cannot grow with match time.
        var currentReports = violations.Select(ReportKey.For).ToHashSet();
        _reported.IntersectWith(currentReports);
        var reporting = false;
        if (_report is not null)
        {
            try { reporting = _reportEnabled?.Invoke() ?? true; }
            catch { reporting = false; }
        }
        var report = _report;
        if (reporting && report is not null)
        {
            foreach (var violation in violations)
            {
                var key = ReportKey.For(violation);
                if (!_reported.Add(key)) continue;
                try { report(violation); }
                catch { /* diagnostics must never stop simulation */ }
            }
        }
        return Records;
    }

    private void CheckCastTimers(double now, HashSet<TimerKey> seen,
        List<LifecycleViolation> violations)
    {
        foreach (var hero in _world.Heroes())
        {
            var view = _stateView(hero);
        }
    }

    private void ObserveTimer(double now, HashSet<TimerKey> seen,
        List<LifecycleViolation> violations, Entity unit, string component,
        object identity, string token, double value, bool allowZero)
    {
        var key = new TimerKey(unit.NetId, component);
        seen.Add(key);
        if (!double.IsFinite(value) || value < -Epsilon)
        {
            _timers.Remove(key);
            violations.Add(Record(unit, component, value, "active timer is invalid", token));
            return;
        }
        if (value <= Epsilon && !allowZero)
        {
            _timers.Remove(key);
            return;
        }
        if (!_timers.TryGetValue(key, out var old) || !ReferenceEquals(old.Identity, identity) ||
            !string.Equals(old.Token, token, StringComparison.Ordinal) || value > old.Value + Epsilon)
        {
            old = new TimerObservation(identity, token, value, now + value + _grace);
            _timers[key] = old;
        }
        else if (value < old.Value - Epsilon)
        {
            old.Value = value;
        }
        if (now > old.Deadline + Epsilon)
            violations.Add(Record(unit, component, value, "accepted timer stopped advancing", token));
    }

    private void CheckExpiries(double now, List<LifecycleViolation> violations)
    {
        foreach (var unit in _world.Entities.Values)
        {
            if (!unit.Alive || !double.IsFinite(unit.ExpiresAt) || unit.ExpiresAt == 0.0 ||
                now <= unit.ExpiresAt + ExpiryGrace)
                continue;
            var ownerId = unit.OwnerNetId != 0 ? unit.OwnerNetId : unit.WardOwnerNetId;
            var owner = ownerId == 0 ? null : _world.Get(ownerId);
            violations.Add(Record(unit, "temporary_unit_expiry", unit.ExpiresAt - now,
                "owned living unit survived its explicit expiry", $"{ownerId}:{unit.ExpiresAt}", owner));
        }
    }

    private void CheckRevivals(double now, List<LifecycleViolation> violations)
    {
        foreach (var hero in _world.Heroes())
        {
            var state = hero.YorickRevival;
            if (state is null) continue;
            var stage = hero.Buffs.Any(buff => buff.Name.StartsWith("YorickRAZombie", StringComparison.Ordinal)) ? "zombie" : "delay";
            if (!double.IsFinite(state.Deadline) || state.Deadline + ExpiryGrace <= now + Epsilon)
                violations.Add(Record(hero, "yorick_revival", state.Deadline,
                    "revival deadline remained armed after the tick", stage));
            if (!double.IsFinite(hero.RespawnAt) || hero.RespawnAt <= Epsilon)
                violations.Add(Record(hero, "yorick_revival_respawn", hero.RespawnAt,
                    "revival is missing the original respawn deadline", stage));
        }
    }

    private void CheckForcedMoves(double now, HashSet<TimerKey> seen,
        List<LifecycleViolation> violations)
    {
        var activeCompleted = new HashSet<MoveKey>();
        foreach (var unit in _world.Entities.Values)
        {
            var move = unit.ForcedMove;
            if (move is null || !unit.Alive) continue;
            var key = new MoveKey(unit.NetId, move);
            if (move.Completed)
            {
                activeCompleted.Add(key);
                var matching = unit.Buffs.FirstOrDefault(buff => buff.Name == move.Effect);
                if (!_completedMoves.TryGetValue(key, out var old) ||
                    !ReferenceEquals(old.MatchingBuff, matching))
                {
                    var deadline = now + (matching is null
                        ? CompletedMoveGrace
                        : Math.Max(0.0, matching.ExpiresAt) + _grace);
                    old = new CompletedMoveObservation(matching, deadline);
                    _completedMoves[key] = old;
                }
                if (now > old.Deadline + Epsilon)
                    violations.Add(Record(unit, "forced_move_completed", now - old.Deadline,
                        "completed forced move remained armed after grace",
                        $"{RuntimeHelpers.GetHashCode(move)}:{move.Effect}:{move.SourceNetId}"));
                continue;
            }
            _completedMoves.Remove(key);
            var timerKey = new TimerKey(unit.NetId, "forced_move");
            seen.Add(timerKey);
            ObserveTimer(now, seen, violations, unit, "forced_move", move,
                $"{RuntimeHelpers.GetHashCode(move)}:{move.Effect}:{move.SourceNetId}",
                move.Remaining, allowZero: true);
        }
        foreach (var key in _completedMoves.Keys.Where(key => !activeCompleted.Contains(key)).ToArray())
            _completedMoves.Remove(key);
    }

    private LifecycleViolation Record(Entity unit, string component, double timer,
        string detail, string token, Entity? owner = null)
    {
        var context = owner ?? unit;
        return new LifecycleViolation(
            context.ChampionName.Length == 0 ? context.Name : context.ChampionName,
            unit.Team, unit.NetId, component, timer, unit.Position,
            context.GodMode, context.CooldownsDisabled, detail, token);
    }

    private static string Format(LifecycleViolation value) =>
        $"{{component={value.Component}, netid={value.NetId}, timer={value.Timer}, detail={value.Detail}}}";

    private readonly record struct TimerKey(uint NetId, string Component);
    private readonly record struct MoveKey(uint NetId, ForcedMoveState Move);
    private readonly record struct ReportKey(uint NetId, string Component, string Token)
    {
        public static ReportKey For(LifecycleViolation violation) =>
            new(violation.NetId, violation.Component, violation.Token);
    }
    private sealed class TimerObservation
    {
        public TimerObservation(object identity, string token, double value, double deadline)
        { Identity = identity; Token = token; Value = value; Deadline = deadline; }
        public object Identity { get; }
        public string Token { get; }
        public double Value { get; set; }
        public double Deadline { get; }
    }
    private sealed class CompletedMoveObservation
    {
        public CompletedMoveObservation(BuffState? matchingBuff, double deadline)
        { MatchingBuff = matchingBuff; Deadline = deadline; }
        public BuffState? MatchingBuff { get; }
        public double Deadline { get; }
    }
}

/// <summary>Stable typed report returned by <see cref="LifecycleMonitor"/>.</summary>
public sealed record LifecycleViolation(
    string Hero,
    Team Team,
    uint NetId,
    string Component,
    double Timer,
    Position2 Position,
    bool GodMode,
    bool CooldownsDisabled,
    string Detail,
    string Token);
