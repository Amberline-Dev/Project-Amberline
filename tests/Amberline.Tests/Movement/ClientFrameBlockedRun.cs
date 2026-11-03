namespace Amberline.Tests;

/// <summary>
/// What a hero walking the stock client's own route may do on blocked cells, as the checks of the hero planner assert it. The client's
/// planner lets a chord pass a blocked corner (its line of sight tests the cell under a sample every 0.8 cells) and its free-movement frame
/// has no terrain gate, so a frame of the walk can end on a blocked cell (W8, batch 75: the client's actor update commits the frame, the
/// cell is tested only when a neighbour is near). A hero that walks a chord along a wall edge stays on its blocked side for as long as the
/// wall runs, so the guard is on the episode, not on the frame: a hero must never <em>stay</em> in blocked cells. The bound is the longest
/// episode the baseline matches of the same batch measured (0.77 s, headless seeds 11, 12, 13, 21 and 31, before the client route was kept
/// whole); a hero lodged in a wall (<c>movement.hero-lodged-deep-in-a-wall-walks-out</c>) stays far beyond it. The five checks that use it
/// measured at most 0.40 s (eight frames of a chord along a wall edge in the 300 random orders; most episodes are one frame) on 19 October 2026.
/// </summary>
internal sealed class ClientFrameBlockedRun
{
    public const double LongestEpisodeSeconds = 0.77;

    private double _run;

    /// <summary>The longest episode seen so far, in seconds.</summary>
    public double Longest { get; private set; }

    /// <param name="endsOnFloor">Whether the frame ended on a movement-walkable cell.</param>
    /// <param name="frameSeconds">The frame's length.</param>
    /// <param name="context">What to say when the episode is too long.</param>
    public void Observe(bool endsOnFloor, double frameSeconds, Func<string> context)
    {
        _run = endsOnFloor ? 0.0 : _run + frameSeconds;
        Longest = Math.Max(Longest, _run);
        TestAssert.True(_run <= LongestEpisodeSeconds + 1e-9,
            $"the hero stayed in blocked cells for {_run:0.00} s (more than the {LongestEpisodeSeconds} s episode of the baseline matches): {context()}");
    }
}
