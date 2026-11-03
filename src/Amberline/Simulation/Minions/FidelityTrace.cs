using System.Text.Json;
using System.Text.Json.Serialization;
using Amberline.Core;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

/// <summary>A copied movement order retained by the optional minion trace.</summary>
public sealed record FidelityTraceOrder(
    [property: JsonPropertyName("mono_ns")]
    long MonotonicNanoseconds,
    [property: JsonPropertyName("netid")]
    uint NetId,
    string Kind,
    string Reason,
    long SyncId,
    IReadOnlyList<Position2> Waypoints,
    [property: JsonPropertyName("target_netid")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    uint? TargetNetId = null,
    [property: JsonPropertyName("source_pos")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    Position2? SourcePosition = null,
    [property: JsonPropertyName("target_pos")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    Position2? TargetPosition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    double? AttackRange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    double? WindupRemaining = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool? CanAttack = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool? InCommittedRange = null);

/// <summary>A copied post-visibility minion state.</summary>
public sealed record FidelityTraceMinion(
    [property: JsonPropertyName("netid")]
    uint NetId,
    [property: JsonPropertyName("pos")]
    Position2 Position,
    IReadOnlyList<Position2> Waypoints,
    string AiState,
    uint? AttackTarget,
    bool Created,
    bool Visible,
    bool Alive,
    [property: JsonPropertyName("collision")]
    FidelityTraceCollision Collision);

/// <summary>Complete copying view of the movement collision frame.</summary>
public sealed record FidelityTraceCollision(
    [property: JsonPropertyName("collider_ids")]
    IReadOnlyList<uint> ColliderIds,
    [property: JsonPropertyName("stuck_elapsed")]
    double StuckElapsed,
    [property: JsonPropertyName("repath_attempts")]
    int RepathAttempts,
    [property: JsonPropertyName("repath_count")]
    int RepathCount,
    [property: JsonPropertyName("sep_accum")]
    Position2 SeparationAccumulation);

/// <summary>One bounded, post-visibility fidelity batch.</summary>
public sealed record FidelityTraceBatch(
    [property: JsonPropertyName("mono_ns")]
    long MonotonicNanoseconds,
    IReadOnlyList<FidelityTraceMinion> Minions,
    IReadOnlyList<FidelityTraceOrder> Orders,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int DroppedOrders,
    IReadOnlyList<FidelityTraceHero>? Heroes = null);

public sealed record FidelityTraceHero(uint NetId, Position2 Position, uint? RequestedTarget,
    uint? AttackTarget, Position2? RequestedGoal, IReadOnlyList<Position2> Waypoints);

public sealed record FidelityTraceCutoff(long MonotonicNanoseconds, int BudgetBytes,
    int WrittenBytes, int RejectedBatchBytes, int PublishedBatches);

/// <summary>
/// DIAGNOSTICS: diagnostics only (LOG_FIDELITY_TRACE); retail has no such trace and it never changes a decision.
/// Opt-in, behavior-neutral authoritative minion tracing.  The recorder is
/// per match and bounded; failures in a consumer never affect simulation.
/// </summary>
public sealed class FidelityTrace : IMovementDiagnosticSink, IDisposable
{
    private const int Capacity = 4096;
    public static int ConfiguredBudgetBytes =>
        int.TryParse(Environment.GetEnvironmentVariable("LOG_FIDELITY_BUDGET_MB"), out var megabytes) &&
        megabytes is >= 1 and <= 1024 ? megabytes * 1024 * 1024 : 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new Position2JsonConverter() },
    };

    private readonly WorldState _world;
    private readonly int _rate;
    private readonly Action<FidelityTraceBatch>? _publish;
    private readonly List<FidelityTraceOrder> _orders = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> _colliders = new();
    private readonly Dictionary<uint, Position2?> _goals = new();
    private int _calls;
    private int _dropped;
    private bool _armed;
    private readonly int _budgetBytes;
    private readonly Action<FidelityTraceCutoff>? _cutoff;
    private int _remainingBytes;
    private int _publishedBatches;
    private bool _cutoffReported;

    public bool Enabled => _remainingBytes > 0;

    public FidelityTrace(WorldState world, int rate = 1,
        Action<FidelityTraceBatch>? publish = null,
        Action<FidelityTraceCutoff>? cutoff = null, int budgetBytes = 16 * 1024 * 1024)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (rate < 1) throw new ArgumentOutOfRangeException(nameof(rate));
        if (budgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        _rate = rate;
        _publish = publish;
        _cutoff = cutoff;
        _budgetBytes = _remainingBytes = budgetBytes;
    }

    public int PendingOrderCount => _orders.Count;

    /// <summary>Encodes the source-compatible fidelity payload for a session logger.</summary>
    public static string ToJson(FidelityTraceBatch batch) =>
        JsonSerializer.Serialize(batch ?? throw new ArgumentNullException(nameof(batch)), JsonOptions);

    /// <summary>
    /// Returns the flattened payload expected by the source AI/DEBUG
    /// <c>fidelity_batch</c> session record. JsonElement values are cloned so
    /// the returned dictionary remains valid after the temporary document is
    /// disposed.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ToProperties(FidelityTraceBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var document = JsonDocument.Parse(ToJson(batch));
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name,
                property => (object?)property.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>Creates a trace from LOG_FIDELITY_TRACE, or null when disabled.</summary>
    public static FidelityTrace? FromEnvironment(WorldState world,
        Action<FidelityTraceBatch>? publish = null,
        Action<FidelityTraceCutoff>? cutoff = null)
    {
        var raw = Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE");
        if (!int.TryParse(raw, out var rate) || rate < 1) return null;

        // The server owns the logger/session writer. This factory only turns
        // on collection; callers inject a publisher when they compose logging.
        return new FidelityTrace(world, rate, publish, cutoff, ConfiguredBudgetBytes);
    }

    /// <summary>Arms the next post-visibility flush as a simulation tick.</summary>
    public void BeginTick() => _armed = true;

    public void NoteOrder(uint netId, string kind, string reason, long syncId,
        IReadOnlyList<Position2> waypoints)
    {
        try
        {
            AddOrder(new FidelityTraceOrder(
                _world.Clock.MonotonicNanoseconds(), netId, kind, reason, syncId,
                waypoints.Select(WirePoint).ToArray()));
        }
        catch
        {
            // Diagnostics are deliberately isolated from the authoritative path.
        }
    }

    /// <summary>
    /// Records one authoritative hero wind-up cancellation in the existing
    /// bounded order stream. This is diagnostic only; it does not alter the
    /// combat or movement state.
    /// </summary>
    public void NoteHeroAttack(uint netId, uint? targetNetId, Position2 sourcePosition,
        Position2? targetPosition, string reason, long syncId, double attackRange,
        double? windupRemaining, bool canAttack, bool inCommittedRange)
    {
        try
        {
            AddOrder(new FidelityTraceOrder(
                _world.Clock.MonotonicNanoseconds(), netId, "hero-attack-cancel",
                reason, syncId, Array.Empty<Position2>(), targetNetId,
                WirePoint(sourcePosition), targetPosition is Position2 target
                    ? WirePoint(target) : null, attackRange, windupRemaining,
                canAttack, inCommittedRange));
        }
        catch
        {
            // Diagnostics are deliberately isolated from the authoritative path.
        }
    }

    private void AddOrder(FidelityTraceOrder order)
    {
        if (!Enabled) return;
        if (_orders.Count >= Capacity)
        {
            _dropped++;
            return;
        }
        _orders.Add(order);
    }

    public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        if (!Enabled) return;
        // Hero and NPC movement arrive in separate callbacks. Preserve each
        // actor's last movement facts until that actor moves again.
        foreach (var netId in _colliders.Keys.ToArray())
        {
            if (WorldState.IsAlive(_world.Get(netId))) continue;
            _colliders.Remove(netId);
            _goals.Remove(netId);
        }
        foreach (var snapshot in snapshots)
        {
            if (!WorldState.IsAlive(_world.Get(snapshot.NetId))) continue;
            _colliders[snapshot.NetId] = snapshot.ColliderIds.ToArray();
            _goals[snapshot.NetId] = snapshot.RequestedGoal;
        }
    }

    /// <summary>Publishes a batch after ReplicationDelivery has refreshed visibility.</summary>
    public void FlushPostVisibility()
    {
        if (!_armed || !Enabled) return;
        _armed = false;
        _calls++;
        if (_calls % _rate != 0)
            return;

        try
        {
            var minions = _world.Entities.Values
                .Where(entity => entity.UnitType == UnitType.Minion)
                .Select(entity => new FidelityTraceMinion(
                    entity.NetId, entity.Position,
                    entity.Waypoints.ToArray(), entity.AiState,
                    entity.AttackTarget, entity.Created,
                    entity.Visible, entity.Alive,
                    new FidelityTraceCollision(
                        _colliders.TryGetValue(entity.NetId, out var ids)
                            ? ids.ToArray() : Array.Empty<uint>(),
                        entity.StuckElapsed, entity.RepathAttempts,
                        entity.RepathCount, entity.SeparationAccumulation)))
                .ToArray();
            var batch = new FidelityTraceBatch(
                _world.Clock.MonotonicNanoseconds(), minions,
                _orders.ToArray(), _dropped, _world.Heroes().Select(hero => new FidelityTraceHero(
                    hero.NetId, hero.Position, hero.HeroPursuit?.TargetNetId, hero.AttackTarget,
                    _goals.GetValueOrDefault(hero.NetId) ?? hero.AttackMoveGoal ?? hero.HeroPursuit?.LastKnownPosition ?? hero.Target,
                    hero.Waypoints.ToArray())).ToArray());
            var bytes = System.Text.Encoding.UTF8.GetByteCount(ToJson(batch));
            if (bytes > _remainingBytes)
            {
                ReportCutoff(bytes);
                return;
            }
            _remainingBytes -= bytes;
            _publish?.Invoke(batch);
            _publishedBatches++;
            if (_remainingBytes == 0) ReportCutoff(0);
        }
        catch
        {
            // A broken file/logger must not stop a live match.
        }
        finally
        {
            _orders.Clear();
            _dropped = 0;
        }
    }

    public void Reset()
    {
        _orders.Clear();
        _colliders.Clear();
        _goals.Clear();
        _calls = 0;
        _dropped = 0;
        _armed = false;
    }

    public void Dispose()
    {
        Reset();
        _remainingBytes = 0;
    }

    private void ReportCutoff(int rejectedBytes)
    {
        if (_cutoffReported) return;
        _cutoffReported = true;
        var writtenBytes = _budgetBytes - _remainingBytes;
        _remainingBytes = 0;
        _colliders.Clear();
        _goals.Clear();
        _cutoff?.Invoke(new FidelityTraceCutoff(_world.Clock.MonotonicNanoseconds(),
            _budgetBytes, writtenBytes, rejectedBytes, _publishedBatches));
    }

    private static Position2 WirePoint(Position2 point) =>
        new((float)point.X, (float)point.Z);

    private sealed class Position2JsonConverter : JsonConverter<Position2>
    {
        public override Position2 Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Position2 value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }
}
