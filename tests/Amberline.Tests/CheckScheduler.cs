using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Amberline.Tests;

/// <summary>
/// <c>--parallel N</c>: the non-serial checks run on N long-lived worker processes,
/// longest first; then the serial checks run alone, each in its own child process.
/// </summary>
internal static class CheckScheduler
{
    internal const int MaximumWorkers = 64;

    // A check missing from check-timings.txt is assumed to be this long (seconds).
    private const double UnknownCheckSeconds = 0.5;

    internal static int Run(IReadOnlyList<Program.DiscoveredCheck> selected, int workers, double timeout)
    {
        var parallelChecks = selected.Where(check => !check.Serial).ToArray();
        var serialChecks = selected.Where(check => check.Serial).ToArray();
        var failed = new List<string>();
        var gate = new object();

        var parallelClock = Stopwatch.StartNew();
        var peaks = new long[Math.Min(workers, Math.Max(parallelChecks.Length, 1))];
        if (parallelChecks.Length > 0)
        {
            // Longest first, so the 90-second soak starts at once instead of being the
            // last check on one worker. Workers take the next check as they free up.
            var estimates = Timings();
            var queue = new ConcurrentQueue<Program.DiscoveredCheck>(parallelChecks
                .OrderByDescending(check => estimates.GetValueOrDefault(check.Name, UnknownCheckSeconds))
                .ThenBy(check => check.Name, StringComparer.Ordinal));
            var slots = Enumerable.Range(0, peaks.Length).Select(index =>
            {
                var thread = new Thread(() => peaks[index] = Slot(queue, timeout, failed, gate));
                thread.Start();
                return thread;
            }).ToArray();
            foreach (var slot in slots) slot.Join();
        }

        var parallelSeconds = parallelClock.Elapsed.TotalSeconds;

        var serialClock = Stopwatch.StartNew();
        failed.AddRange(Program.RunIsolatedSequence(serialChecks, timeout));
        var serialSeconds = serialClock.Elapsed.TotalSeconds;

        Console.WriteLine($"parallel run: {parallelChecks.Length} checks on up to {workers} workers in " +
            $"{parallelSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s " +
            $"(largest worker peak working set {peaks.Max() / (1024 * 1024)} MiB), " +
            $"{serialChecks.Length} serial checks in {serialSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
        if (failed.Count > 0)
        {
            Console.WriteLine($"failed checks ({failed.Count}):");
            foreach (var name in failed.Order(StringComparer.Ordinal)) Console.WriteLine($"  {name}");
        }

        Console.WriteLine($"{selected.Count - failed.Count}/{selected.Count} passed");
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>One worker slot: takes checks until the queue is empty and returns its worker's peak working set.</summary>
    private static long Slot(ConcurrentQueue<Program.DiscoveredCheck> queue, double timeout, List<string> failed, object gate)
    {
        WorkerProcess? worker = null;
        long peak = 0;
        try
        {
            while (queue.TryDequeue(out var check))
            {
                var start = Stopwatch.GetTimestamp();
                WorkerProcess.Outcome outcome;
                try
                {
                    worker ??= WorkerProcess.Start();
                    outcome = worker.Run(check.Name, check.Timeout(timeout));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    outcome = new WorkerProcess.Outcome(1, $"The check worker failed: {exception}", true);
                }

                if (outcome.Lost)
                {
                    peak = Math.Max(peak, worker?.PeakWorkingSetBytes ?? 0);
                    worker?.Dispose();
                    worker = null;
                }

                var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                // One lock covers both streams, so a check's RUN line and its failure
                // text are never split by another check's output.
                lock (gate)
                {
                    Console.WriteLine($"RUN   {check.Name}  {(outcome.ExitCode == 0 ? "PASS" : "FAIL")} ({elapsed.ToString("0.00", CultureInfo.InvariantCulture)}s)");
                    if (outcome.ExitCode != 0)
                    {
                        failed.Add(check.Name);
                        if (!string.IsNullOrWhiteSpace(outcome.Output)) Console.Error.WriteLine(outcome.Output.TrimEnd());
                    }
                }
            }
        }
        finally
        {
            if (worker is not null)
            {
                peak = Math.Max(peak, worker.PeakWorkingSetBytes);
                worker.Dispose();
            }
        }

        return peak;
    }

    /// <summary>Seconds per check from check-timings.txt beside the runner ("name seconds" per line, # comments).</summary>
    internal static Dictionary<string, double> Timings()
    {
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "check-timings.txt");
        if (!File.Exists(path)) return timings;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !parts[0].StartsWith('#') &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                timings[parts[0]] = seconds;
        }

        return timings;
    }
}
