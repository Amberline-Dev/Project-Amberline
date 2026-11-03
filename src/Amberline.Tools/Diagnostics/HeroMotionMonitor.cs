using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Movement;

namespace Amberline.Tools.Diagnostics;

/// <summary>What <see cref="HeroMotionMonitor"/> saw: hero-seconds on a route and the stalls among them.</summary>
public readonly record struct HeroMotionStatistics(double RouteSeconds, long StallEpisodes, double StallSeconds,
    IReadOnlyList<string>? FirstStalls = null, double LongestStallSeconds = 0.0);

/// <summary>
/// DIAGNOSTICS: an emulator-only diagnostic. Every half second of game time it looks at each live hero that owns a
/// route and is free to move; a hero that moved less than <see cref="MovedUnits"/> since the last sample is stalled.
/// A stall that lasts two samples (one second) is an episode. Used to compare hero pathing builds on the same seed.
/// </summary>
public sealed class HeroMotionMonitor : IMovementDiagnosticSink
{
    public const double SampleSeconds = 0.5;
    public const double MovedUnits = 2.0;

    private readonly Dictionary<uint, (Position2 Position, bool Routed, int Run)> _last = [];
    private double _nextSample = SampleSeconds;
    private double _routeSeconds, _stallSeconds;
    private long _episodes;
    // Every stall episode (the report text taken when it began, and how long it lasted); the report prints the longest twelve.
    private readonly List<(string Text, double Seconds)> _stalls = [];
    private readonly Dictionary<uint, int> _open = [];
    private readonly Dictionary<uint, MovementDiagnosticSnapshot> _lastSnapshot = [];

    /// <summary>Keeps the latest movement solve of every unit so a stall report can say which branch halted the hero.</summary>
    public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots) _lastSnapshot[snapshot.NetId] = snapshot;
    }

    public HeroMotionStatistics Statistics => new(_routeSeconds, _episodes, _stallSeconds,
        _stalls.Select((stall, index) => (stall, index)).OrderByDescending(item => item.stall.Seconds).ThenBy(item => item.index)
            .Take(12).OrderBy(item => item.index)
            .Select(item => item.stall.Text + System.FormattableString.Invariant($" dur={item.stall.Seconds:0.0}")).ToArray(),
        _stalls.Count == 0 ? 0.0 : _stalls.Max(stall => stall.Seconds));

    public void Observe(double gameSeconds, IEnumerable<Entity> heroes, IEnumerable<Entity>? world = null, Amberline.Data.NavigationGrid? grid = null)
    {
        if (gameSeconds + 1e-9 < _nextSample) return;
        _nextSample += SampleSeconds;
        foreach (var hero in heroes)
        {
            if (!hero.Alive)
            {
                _last.Remove(hero.NetId);
                _open.Remove(hero.NetId);
                continue;
            }
            var free = hero.ForcedMove is null && hero.Waypoints.Count > 0 &&
                !hero.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled || buff.DisablesActions);
            var run = 0;
            if (_last.TryGetValue(hero.NetId, out var before) && before.Routed && free)
            {
                _routeSeconds += SampleSeconds;
                var moved = Math.Sqrt(Math.Pow(hero.Position.X - before.Position.X, 2) +
                                      Math.Pow(hero.Position.Z - before.Position.Z, 2));
                if (moved < MovedUnits)
                {
                    run = before.Run + 1;
                    if (run == 2)
                    {
                        _episodes++;
                        _stallSeconds += 2 * SampleSeconds;
                        _open[hero.NetId] = _stalls.Count;
                        _stalls.Add((System.FormattableString.Invariant(
                                $"t={gameSeconds:0.0} hero={hero.NetId:X} at=({hero.Position.X:0},{hero.Position.Z:0}) route={hero.Waypoints.Count} ") +
                                System.FormattableString.Invariant(
                                $"wp0=({hero.Waypoints[0].X:0},{hero.Waypoints[0].Z:0}) last=({hero.Waypoints[^1].X:0},{hero.Waypoints[^1].Z:0}) stuck={hero.RepathCount} speed={hero.Speed:0} atk={hero.AttackTarget is not null} recall={hero.Buffs.Any(buff => buff.Name == "Recall")} cast={hero.PendingCast is not null} diag={(_lastSnapshot.TryGetValue(hero.NetId, out var diag) ? $"branches={string.Join('/', diag.Branches ?? [])} halted={diag.Halted} colliders={string.Join(',', diag.ColliderIds.Select(id => (world ?? []).FirstOrDefault(e => e.NetId == id) is { } c ? $"{c.Name}@{Math.Sqrt(Math.Pow(c.Position.X - hero.Position.X, 2) + Math.Pow(c.Position.Z - hero.Position.Z, 2)):0}/r{c.Radius:0}{(hero.AttackTarget == id ? "*TARGET" : "")}" : id.ToString("X")))} target={(hero.AttackTarget is uint tid && (world ?? []).FirstOrDefault(e => e.NetId == tid) is { } tg ? $"{tg.Name}@{Math.Sqrt(Math.Pow(tg.Position.X - hero.Position.X, 2) + Math.Pow(tg.Position.Z - hero.Position.Z, 2)):0}" : "none")} range={hero.AttackRange:0} avoid={diag.AvoidCount} rc={diag.RepathCount}" : "none")} buffs=[{string.Join(',', hero.Buffs.Select(buff => buff.Name).Take(6))}] blockedHere={(grid is not null && !grid.IsMovementWalkable(hero.Position))} los0={(grid is not null && grid.HasMovementLineOfSight(hero.Position, hero.Waypoints[0]))} route=[{string.Join(' ', hero.Waypoints.Take(6).Select(point => $"({point.X:0},{point.Z:0})"))}] near=") +
                                string.Join(';', (world ?? []).Where(other => other != hero && other.Alive).Select(other => (Entity: other,
                                    Distance: Math.Sqrt(Math.Pow(other.Position.X - hero.Position.X, 2) + Math.Pow(other.Position.Z - hero.Position.Z, 2))))
                                    .Where(item => item.Distance < 400).OrderBy(item => item.Distance).Take(4)
                                    .Select(item => System.FormattableString.Invariant($"{item.Entity.UnitType}:{item.Entity.Name}@{item.Distance:0}/r{item.Entity.Radius:0}/sp{item.Entity.Speed:0}"))), 1.0));
                    }
                    else if (run > 2)
                    {
                        _stallSeconds += SampleSeconds;
                        if (_open.TryGetValue(hero.NetId, out var open))
                            _stalls[open] = (_stalls[open].Text, _stalls[open].Seconds + SampleSeconds);
                    }
                }
                else _open.Remove(hero.NetId);
            }
            else _open.Remove(hero.NetId);
            _last[hero.NetId] = (hero.Position, free, run);
        }
    }
}
