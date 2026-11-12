namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the host page's activity feed (the owner's design, 11 November 2026): logins and logouts, sign-ups, games that start and end,
/// and the users the host adds, disables, enables and deletes. Memory only: a Platform start begins it empty, and it keeps the newest
/// <c>capacity</c> lines. A line names people, maps, counts and sides, never chat, passwords or addresses; the pages escape the text they show.
/// </summary>
public sealed class HostActivity(int capacity = 100, Func<DateTimeOffset>? clock = null)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<HostActivityEntry> _entries = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Adds a line at the clock's time, dropping the oldest once the feed is full.</summary>
    public void Add(string text) => Add(text, null);

    /// <summary>
    /// Adds a line at <paramref name="at"/>, or at the clock's time when it is null. DIAGNOSTICS: the demo data's lines are spread over the
    /// hour before a start this way (<see cref="HostDemoData"/>).
    /// </summary>
    public void Add(string text, DateTimeOffset? at)
    {
        lock (_gate)
        {
            _entries.AddFirst(new HostActivityEntry(at ?? _clock(), text));
            if (_entries.Count > capacity) _entries.RemoveLast();
        }
    }

    /// <summary>The newest <paramref name="max"/> lines, newest first.</summary>
    public IReadOnlyList<HostActivityEntry> Recent(int max = 20)
    {
        lock (_gate) return _entries.Take(max).ToArray();
    }

    /// <summary>The neutral name of a map in the feed: the stock client's own names never reach a host page.</summary>
    public static string MapName(int mapId) => mapId switch
    {
        1 => "5v5 map",
        2 => "5v5 map (autumn)",
        3 => "Tutorial map",
        4 => "3v3 map",
        8 => "Capture map",
        _ => $"Map {mapId}",
    };
}

/// <summary>One line of the activity feed: when it was added, and its text as given (the pages escape it when they show it).</summary>
public sealed record HostActivityEntry(DateTimeOffset At, string Text);
