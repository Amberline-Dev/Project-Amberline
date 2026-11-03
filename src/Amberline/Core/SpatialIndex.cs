namespace Amberline.Core;

/// <summary>
/// A snapshot uniform grid. Queries intentionally return a whole-cell bounding
/// box superset; callers retain the exact distance or collision predicate.
/// </summary>
public sealed class SpatialIndex<T>
{
    public const double DefaultCellSize = 400.0;

    private readonly Dictionary<Cell, List<Entry>> _cells = new();
    private readonly double _cellSize;
    private readonly List<List<Entry>> _activeBuckets = new();
    private int _sequence;

    public SpatialIndex(IEnumerable<(double X, double Z, T Item)> items,
                        double cellSize = DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!double.IsFinite(cellSize) || cellSize <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        _cellSize = cellSize;
        foreach (var (x, z, item) in items)
            Add(x, z, item);
    }

    public double CellSize => _cellSize;

    // Reusable snapshots retain buckets; existing Query keeps insertion order.
    internal void Clear()
    {
        foreach (var bucket in _activeBuckets) bucket.Clear();
        _activeBuckets.Clear();
        _sequence = 0;
    }

    internal void Add(double x, double z, T item)
    {
        var cell = GetCell(x, z);
        if (!_cells.TryGetValue(cell, out var bucket))
            _cells.Add(cell, bucket = new List<Entry>());
        if (bucket.Count == 0) _activeBuckets.Add(bucket);
        bucket.Add(new Entry(_sequence++, item));
    }

    /// <summary>Unordered broad-phase results in caller-owned storage.</summary>
    internal void QueryInto(double x, double z, double radius, List<T> results)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || !double.IsFinite(radius) || radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        results.Clear();
        var minColumn = ToCellCoordinate(x - radius);
        var maxColumn = ToCellCoordinate(x + radius);
        var minRow = ToCellCoordinate(z - radius);
        var maxRow = ToCellCoordinate(z + radius);
        for (var column = minColumn;; column++)
        {
            for (var row = minRow;; row++)
            {
                if (_cells.TryGetValue(new Cell(column, row), out var bucket))
                    foreach (var entry in bucket) results.Add(entry.Item);
                if (row == maxRow) break;
            }
            if (column == maxColumn) break;
        }
    }

    public IReadOnlyList<T> Query(double x, double z, double radius)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Query coordinates must be finite.");
        }

        if (!double.IsFinite(radius) || radius < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        var minColumn = ToCellCoordinate(x - radius);
        var maxColumn = ToCellCoordinate(x + radius);
        var minRow = ToCellCoordinate(z - radius);
        var maxRow = ToCellCoordinate(z + radius);
        var hits = new List<Entry>();

        for (var column = minColumn;; column++)
        {
            for (var row = minRow;; row++)
            {
                if (_cells.TryGetValue(new Cell(column, row), out var bucket))
                {
                    hits.AddRange(bucket);
                }

                if (row == maxRow)
                {
                    break;
                }
            }

            if (column == maxColumn)
            {
                break;
            }
        }

        hits.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return hits.Select(static entry => entry.Item).ToArray();
    }

    private Cell GetCell(double x, double z) => new(ToCellCoordinate(x), ToCellCoordinate(z));

    private int ToCellCoordinate(double value)
    {
        var coordinate = Math.Floor(value / _cellSize);
        if (coordinate < int.MinValue || coordinate > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The coordinate is outside the grid range.");
        }

        return checked((int)coordinate);
    }

    private readonly record struct Cell(int Column, int Row);

    private readonly record struct Entry(int Sequence, T Item);
}
