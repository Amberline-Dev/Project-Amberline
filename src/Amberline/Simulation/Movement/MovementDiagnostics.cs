using Amberline.Core;

namespace Amberline.Simulation.Movement;

/// <summary>Optional observation of a completed planning decision.</summary>
public sealed record MovementDecisionObservation(
    uint NetId,
    string Kind,
    string Reason,
    Position2? RequestedGoal = null,
    IReadOnlyList<Position2>? Route = null,
    int Attempt = 0,
    Position2? BlockerPosition = null,
    double? BlockerRadius = null,
    Position2? RawAnchor = null,
    Position2? QuantizedAnchor = null,
    long? SyncId = null,
    string? Phase = null);

/// <summary>Copying diagnostic view for a unit after a movement solve.</summary>
public sealed record MovementDiagnosticSnapshot(
    uint NetId,
    IReadOnlyList<uint> ColliderIds,
    double StuckElapsed,
    int RepathAttempts,
    int RepathCount,
    Position2 SeparationAccumulation,
    Position2? StartPosition = null,
    Position2? MarchPosition = null,
    Position2? CommittedPosition = null,
    IReadOnlyList<string>? Branches = null,
    int AvoidCount = 0,
    double? ColliderDot = null,
    bool Halted = false,
    IReadOnlyList<uint>? AvoiderIds = null,
    double QueryRadius = 0.0,
    bool Static = false,
    double DeltaTime = 0.1,
    Position2? CollisionCandidatePosition = null,
    Position2? RequestedGoal = null);

/// <summary>Optional typed sink for movement diagnostics.</summary>
public interface IMovementDiagnosticSink
{
    /// <summary>Whether the sink currently needs per-unit movement facts.</summary>
    bool Enabled => true;

    void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots);

    /// <summary>Receives the actual simulation interval, including empty frames.</summary>
    void OnMovementTick(double dt, IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
        OnMovementTick(snapshots);

    /// <summary>Receives the interval and elapsed time of this actor cohort.</summary>
    void OnMovementTick(double dt, double elapsed,
        IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
        OnMovementTick(dt, snapshots);
}

/// <summary>Fan-out for independent opt-in movement diagnostics.</summary>
public sealed class MovementDiagnosticFanout : IMovementDiagnosticSink
{
    private readonly IMovementDiagnosticSink[] _sinks;

    public MovementDiagnosticFanout(params IMovementDiagnosticSink?[] sinks) =>
        _sinks = sinks.Where(sink => sink is not null).Cast<IMovementDiagnosticSink>().ToArray();

    public bool Enabled => _sinks.Any(sink => sink.Enabled);

    public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        foreach (var sink in _sinks)
        {
            try { sink.OnMovementTick(snapshots); }
            catch { /* diagnostics never take down the simulation */ }
        }
    }

    public void OnMovementTick(double dt, IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        foreach (var sink in _sinks)
        {
            try { sink.OnMovementTick(dt, snapshots); }
            catch { /* diagnostics never take down the simulation */ }
        }
    }

    public void OnMovementTick(double dt, double elapsed,
        IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        foreach (var sink in _sinks)
        {
            try { sink.OnMovementTick(dt, elapsed, snapshots); }
            catch { /* diagnostics never take down the simulation */ }
        }
    }
}
