using System.Diagnostics;

namespace Amberline.Core;

/// <summary>
/// A per-match wall and monotonic clock that can be switched to deterministic time.
/// </summary>
public sealed class GameClock
{
    private static readonly Func<double> DefaultWallClock = GetWallClock;
    private static readonly Func<double> DefaultMonotonicClock = GetMonotonicClock;

    private readonly Func<double> _wallClock;
    private readonly Func<double> _monotonicClock;
    private bool _virtual;
    private double _virtualTime;
    private long _lastSyncId;

    /// <summary>
    /// Creates a real-time clock. Optional providers make real mode deterministic in tests.
    /// </summary>
    public GameClock(Func<double>? wallClock = null, Func<double>? monotonicClock = null)
    {
        _wallClock = wallClock ?? DefaultWallClock;
        _monotonicClock = monotonicClock ?? DefaultMonotonicClock;
    }

    public bool IsVirtual => _virtual;

    /// <summary>Returns Unix wall-clock seconds in real mode or the virtual seconds value.</summary>
    public double Now() => _virtual ? _virtualTime : _wallClock();

    /// <summary>Returns monotonic seconds, sharing virtual time with <see cref="Now"/>.</summary>
    public double Monotonic() => _virtual ? _virtualTime : _monotonicClock();

    /// <summary>Returns the rounded integer monotonic anchor used by the Python runtime.</summary>
    public long MonotonicNanoseconds()
    {
        var rounded = Math.Round(Monotonic() * 1_000_000_000.0, MidpointRounding.ToEven);
        if (rounded < long.MinValue || rounded > long.MaxValue)
        {
            throw new OverflowException("The monotonic time does not fit in Int64 nanoseconds.");
        }

        return checked((long)rounded);
    }

    /// <summary>
    /// Returns a strictly increasing 31-bit-clock-derived waypoint sync ID.
    /// </summary>
    public long SyncId()
    {
        var milliseconds = checked((long)(Now() * 1000.0));
        var clockId = milliseconds & 0x7fff_ffffL;
        _lastSyncId = Math.Max(clockId, checked(_lastSyncId + 1));
        return _lastSyncId;
    }

    /// <summary>Restores real mode and resets the sync-ID sequence.</summary>
    public void UseReal()
    {
        _virtual = false;
        _virtualTime = 0.0;
        _lastSyncId = 0;
    }

    /// <summary>Enters virtual mode at the supplied Unix seconds value.</summary>
    public void SetVirtual(double seconds = 0.0)
    {
        _virtual = true;
        _virtualTime = seconds;
        _lastSyncId = 0;
    }

    /// <summary>Advances virtual mode by the supplied number of seconds.</summary>
    public void Advance(double seconds)
    {
        if (!_virtual)
        {
            throw new InvalidOperationException("GameClock.Advance requires virtual mode.");
        }

        _virtualTime += seconds;
    }

    private static double GetWallClock() => (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds;

    private static double GetMonotonicClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
