using System.Buffers;
using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Data;

public readonly record struct NavCell(int Column, int Row);

public readonly record struct NavigationObstacle(Position2 Center, double Radius);

public readonly record struct NavigationGridMetadata(
    double MinX,
    double MinZ,
    double MaxX,
    double MaxZ,
    double CellSize,
    int CountX,
    int CountY);

/// <summary>
/// A per-instance Map1 navigation grid. Raw terrain, inflated routing,
/// per-frame movement, vision, and brush components remain separate views.
/// </summary>
public sealed partial class NavigationGrid
{
    private const int CellStride = 56;
    private const int HeightOffset = 0;
    private const int FlagsOffset = 50;
    private const ushort BlockedMask = 0x42;
    private const ushort NotPassableMask = 0x02;
    private const ushort SeeThroughMask = 0x40;
    // Global vision (later emulators name it HAS_GLOBAL_VISION): every client lights these cells in its fog, the client /
    // The client (see HasGlobalVision). Of the shipped grids only Map8's has it: the Crystal Scar's outer track and its centre.
    private const ushort GlobalVisionMask = 0x100;
    private const ushort GrassMask = 0x01;
    // Constants of the client's brush test.
    private const double MinBrushAreaRadius = 35.0;
    private const double MaxBrushAreaRadius = 500.0;
    private const float BrushAreaFraction = 0.4f;
    private static readonly (int Dc, int Dr, double Cost)[] Neighbors =
    [
        (-1, -1, 1.4), (0, -1, 1.0), (1, -1, 1.4),
        (-1, 0, 1.0), (1, 0, 1.0),
        (-1, 1, 1.4), (0, 1, 1.0), (1, 1, 1.4),
    ];
    private const double HeroMarginCellCost = 2.0;
    private static readonly (int Dc, int Dr)[] FourNeighbors =
    [(-1, 0), (1, 0), (0, -1), (0, 1)];

    private readonly byte[] _walkable;
    private readonly byte[] _walkablePathing;
    private readonly byte[] _walkableHeroPathing;
    private readonly byte[] _walkableMovement;
    // The client's own vision flags: a cell blocks sight when it is NotPassable without SeeThrough, the rule of both the fog raycast
    // (bit 1 set, bit 6 clear) and the object walk (which also lets global vision 0x100 through; Map1 ships
    // no SeeThrough or 0x100 cell, and no shipped 0x100 cell is NotPassable). That is the shipped NotPassable cells with every building
    // stamp (flag 0x42, see-through) written over them, as the client stamps its own grid. Every sight test walks it.
    private readonly byte[] _visionBlocking;
    private readonly byte[] _globalVision;
    private readonly byte[] _grass;
    private readonly byte[] _notPassable;
    private readonly int[] _brushLabels;
    private readonly double[] _height;

    private NavigationGrid(
        NavigationGridMetadata metadata,
        byte[] walkable,
        byte[] walkablePathing,
        byte[] walkableHeroPathing,
        byte[] walkableMovement,
        byte[] visionBlocking,
        byte[] globalVision,
        byte[] grass,
        byte[] notPassable,
        int[] brushLabels,
        double[] height)
    {
        Metadata = metadata;
        _walkable = walkable;
        _walkablePathing = walkablePathing;
        _walkableHeroPathing = walkableHeroPathing;
        _walkableMovement = walkableMovement;
        _visionBlocking = visionBlocking;
        _globalVision = globalVision;
        _grass = grass;
        _notPassable = notPassable;
        _brushLabels = brushLabels;
        _height = height;
    }

    public NavigationGridMetadata Metadata { get; }
    public int CountX => Metadata.CountX;
    public int CountY => Metadata.CountY;
    public double MinX => Metadata.MinX;
    public double MinZ => Metadata.MinZ;
    public double MaxX => Metadata.MaxX;
    public double MaxZ => Metadata.MaxZ;
    public double CellSize => Metadata.CellSize;

    // Read-only views make fixture and diagnostic checks possible without
    // exposing mutable grid state.
    public ReadOnlyMemory<byte> Walkable => _walkable;
    public ReadOnlyMemory<byte> WalkablePathing => _walkablePathing;
    internal ReadOnlyMemory<byte> WalkableHeroPathing => _walkableHeroPathing;
    public ReadOnlyMemory<byte> WalkableMovement => _walkableMovement;
    public ReadOnlyMemory<byte> VisionBlocking => _visionBlocking;
    public ReadOnlyMemory<byte> Grass => _grass;
    public IReadOnlyList<int> BrushLabels => _brushLabels;
    public IReadOnlyList<double> Heights => _height;

    /// <summary>Diagnostic counts for bounded path-planning scenario checks.</summary>
    public long FindPathCalls { get; private set; }
    internal long ReachabilitySearches { get; private set; }
    internal long AStarSearches { get; private set; }
    internal long PathSearchExpansions { get; private set; }
    public long FindPathAroundUnitsCalls { get; private set; }

    /// <summary>
    /// Deterministic cost meter of the path planners, in units of about half a microsecond on the development
    /// PC: one per visibility-graph edge test, <see cref="GridSearchWork"/> per grid-search expansion. The
    /// per-call count is a poor cost proxy (one query measured 0.1 to 4 ms); automatic hero planning budgets
    /// against this meter instead, so a budget bounds time whatever the crowd.
    /// </summary>
    public long PlanningWork { get; internal set; }

    private const long GridSearchWork = 16;

    /// <summary>
    /// Optional deterministic planner seam for scenario diagnostics. The
    /// normal runtime leaves this null and executes the bounded A* planner;
    /// source parity checks can explicitly model a patched occupancy query
    /// without changing the production route algorithm.
    /// </summary>
    internal Func<Position2, Position2, IReadOnlyList<NavigationObstacle>,
        IReadOnlyList<Position2>>? FindPathAroundUnitsOverride { get; set; }

    /// <summary>
    /// Optional deterministic route seam used by bounded source-scenario
    /// checks. Normal runtime navigation leaves this null and executes the
    /// shipped A* implementation.
    /// </summary>
    internal Func<Position2, Position2, IReadOnlyList<Position2>>? FindPathOverride { get; set; }

    public static NavigationGrid LoadFile(string path, NavigationGridSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream, settings);
    }

    /// <summary>
    /// The shipped Map1 grid. With the default settings the file is decoded once per
    /// process and every call returns its own instance (its own query counters and
    /// planner seams) over the same load-time arrays; nothing mutates those arrays after
    /// construction, and <c>data.shared-instances-unchanged-after-match</c> proves it.
    /// Custom settings stamp different obstacles and always load a private grid.
    /// </summary>
    public static NavigationGrid LoadDefault(GameDataPaths? paths = null, NavigationGridSettings? settings = null) =>
        Load(paths ?? GameDataPaths.Default, settings);

    /// <summary>The navigation grid of the map the paths name (LEVELS/&lt;MapName&gt;/AIPath.aimesh_ngrid); the Harrowing rift reads its own file.</summary>
    public static NavigationGrid Load(GameDataPaths paths, NavigationGridSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = MapDefinitions.NavigationGridPath(paths);
        return settings is null ? SharedMap(path) : LoadFile(path, settings);
    }

    private static readonly object SharedDefaultLock = new();
    private static readonly Dictionary<string, (long Length, long WriteTicks, NavigationGrid Base)> SharedDefaultGrids =
        new(StringComparer.OrdinalIgnoreCase);

    private static NavigationGrid SharedMap(string path)
    {
        var file = new FileInfo(path);
        // A missing file fails exactly as an unshared load does.
        if (!file.Exists) return LoadFile(path, NavigationGridSettings.Default);
        lock (SharedDefaultLock)
        {
            if (!SharedDefaultGrids.TryGetValue(file.FullName, out var shared) ||
                shared.Length != file.Length || shared.WriteTicks != file.LastWriteTimeUtc.Ticks)
            {
                shared = (file.Length, file.LastWriteTimeUtc.Ticks, LoadFile(path, NavigationGridSettings.Default));
                SharedDefaultGrids[file.FullName] = shared;
            }

            return shared.Base.Fork();
        }
    }

    // The client's path-search region table after the cells (NavigationGrid.ClientRegions.cs, read for the ported A*); a build without the
    // port reads only the cells.
    partial void CopyClientRegionsFrom(NavigationGrid other);
    partial void ReadClientRegions(byte[] cellBytes, int count, BinaryReader reader);

    /// <summary>A new instance over the same immutable cell arrays, with fresh counters and seams.</summary>
    private NavigationGrid Fork()
    {
        var fork = new NavigationGrid(Metadata, _walkable, _walkablePathing, _walkableHeroPathing,
            _walkableMovement, _visionBlocking, _globalVision, _grass, _notPassable, _brushLabels, _height);
        fork.CopyClientRegionsFrom(this);
        return fork;
    }

    public static NavigationGrid Load(Stream stream, NavigationGridSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Navigation stream must be readable", nameof(stream));
        settings ??= NavigationGridSettings.Default;
        settings.Validate();

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var major = reader.ReadByte();
        if (major != 2)
            _ = reader.ReadUInt16();
        var minX = reader.ReadSingle();
        _ = reader.ReadSingle();
        var minZ = reader.ReadSingle();
        var maxX = reader.ReadSingle();
        _ = reader.ReadSingle();
        var maxZ = reader.ReadSingle();
        var cellSize = reader.ReadSingle();
        var countX = checked((int)reader.ReadUInt32());
        var countY = checked((int)reader.ReadUInt32());
        if (countX <= 0 || countY <= 0 || !float.IsFinite(cellSize) || cellSize <= 0.0f)
            throw new InvalidDataException("Invalid navigation-grid metadata");
        if (!float.IsFinite(minX) || !float.IsFinite(minZ) || !float.IsFinite(maxX) || !float.IsFinite(maxZ))
            throw new InvalidDataException("Navigation-grid bounds are non-finite");

        var count = checked(countX * countY);
        var bytes = checked(count * CellStride);
        var cellBytes = reader.ReadBytes(bytes);
        if (cellBytes.Length != bytes)
            throw new EndOfStreamException($"Navigation grid ended after {cellBytes.Length} of {bytes} cell bytes");

        var walkable = new byte[count];
        var visionBlocking = new byte[count];
        var globalVision = new byte[count];
        var grass = new byte[count];
        var notPassable = new byte[count];
        var height = new double[count];
        for (var index = 0; index < count; index++)
        {
            var offset = checked(index * CellStride);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(cellBytes.AsSpan(offset + FlagsOffset, sizeof(ushort)));
            walkable[index] = (byte)((flags & BlockedMask) == 0 ? 1 : 0);
            grass[index] = (byte)((flags & GrassMask) != 0 ? 1 : 0);
            notPassable[index] = (byte)((flags & NotPassableMask) != 0 ? 1 : 0);
            visionBlocking[index] = (byte)((flags & NotPassableMask) != 0 &&
                                            (flags & SeeThroughMask) == 0 &&
                                            (flags & GlobalVisionMask) == 0 ? 1 : 0);
            globalVision[index] = (byte)((flags & GlobalVisionMask) != 0 ? 1 : 0);
            var bits = BinaryPrimitives.ReadInt32LittleEndian(cellBytes.AsSpan(offset + HeightOffset, sizeof(float)));
            height[index] = BitConverter.Int32BitsToSingle(bits);
        }

        var metadata = new NavigationGridMetadata(minX, minZ, maxX, maxZ, cellSize, countX, countY);
        StampBuildings(visionBlocking, metadata, settings, settings.TurretClientStampRadius);
        var terrainPathing = BuildTerrainPathingGrid(walkable, metadata, settings);
        // The client writes one turret footprint into its grid for every walkability view: the turret's creation
        // stamps radius 140.0 (flag 0x42). Batch 223 item 3 retires the 260-unit strategic
        // disc (added by commit e7f2ffa for minion-wave safety): NPC routes use the client footprint, as hero routes do.
        // The Nexus and the inhibitors are stamped at their pathfinding radius in every view (the client stamps them
        // once, at creation, and never takes the stamp off; see StampFilledCircle).
        var pathing = (byte[])terrainPathing.Clone();
        StampBuildings(pathing, metadata, settings, settings.TurretClientStampRadius);
        // Heroes share the 65u terrain dilation, but their route planner must
        // agree with the client's 140u turret movement stamp. The wider 260u
        // stamp remains the NPC/minion route policy above.
        var heroPathing = (byte[])terrainPathing.Clone();
        StampBuildings(heroPathing, metadata, settings, settings.TurretClientStampRadius);
        var movement = (byte[])walkable.Clone();
        StampBuildings(movement, metadata, settings, settings.TurretClientStampRadius);
        var labels = LabelBrushes(grass, countX, countY);
        var grid = new NavigationGrid(metadata, walkable, pathing, heroPathing, movement, visionBlocking,
            globalVision, grass, notPassable, labels, height);
        grid.ReadClientRegions(cellBytes, count, reader);
        return grid;
    }

    public NavCell WorldToCell(Position2 point)
    {
        EnsureFinite(point, nameof(point));
        return new NavCell(ToGridInt((point.X - MinX) / CellSize),
            ToGridInt((point.Z - MinZ) / CellSize));
    }

    public Position2 CellToWorld(NavCell cell) => new(
        cell.Column * CellSize + MinX + CellSize / 2.0,
        cell.Row * CellSize + MinZ + CellSize / 2.0);

    public bool IsInBounds(NavCell cell) =>
        cell.Column >= 0 && cell.Column < CountX && cell.Row >= 0 && cell.Row < CountY;

    public bool IsWalkable(Position2 point) => IsWalkable(WorldToCell(point));
    public bool IsPathingWalkable(Position2 point) => IsPathingWalkable(WorldToCell(point));
    internal bool IsHeroPathingWalkable(Position2 point) => IsHeroPathingWalkable(WorldToCell(point));
    public bool IsMovementWalkable(Position2 point) => IsMovementWalkable(WorldToCell(point));

    public Position2 ProjectToMovementFloor(Position2 point)
    {
        if (IsMovementWalkable(point)) return point;
        var cell = Nearest(WorldToCell(point), IsMovementWalkable, maxRadius: 50)
            ?? throw new InvalidOperationException($"No movement floor near {point}.");
        return CellToWorld(cell);
    }

    /// <summary>
    /// The point itself when it is on movement floor, else the centre of the nearest floor cell at most <paramref name="maxCells"/> cells
    /// away, or null when there is none that near.
    /// </summary>
    public Position2? NearestMovementFloor(Position2 point, int maxCells) =>
        IsMovementWalkable(point) ? point
            : Nearest(WorldToCell(point), IsMovementWalkable, maxRadius: maxCells) is NavCell cell ? CellToWorld(cell) : null;

    /// <summary>The point itself when the NPC pathing grid accepts it, else the centre of the nearest cell that does.</summary>
    public Position2 ProjectToPathingFloor(Position2 point)
    {
        if (IsPathingWalkable(point)) return point;
        var cell = Nearest(WorldToCell(point), IsPathingWalkable, maxRadius: 50)
            ?? throw new InvalidOperationException($"No pathing floor near {point}.");
        return CellToWorld(cell);
    }

    /// <summary>
    /// The nearest cell the hero planner can start from: the point itself when the hero-pathing grid accepts it, else the centre of
    /// the nearest cell that does. A cell can be client floor and still be refused by the planner (the turret stamp).
    /// </summary>
    internal Position2 ProjectToHeroPathingFloor(Position2 point)
    {
        if (IsHeroPathingWalkable(point)) return point;
        var cell = Nearest(WorldToCell(point), IsHeroPathingWalkable, maxRadius: 50)
            ?? throw new InvalidOperationException($"No hero-pathing floor near {point}.");
        return CellToWorld(cell);
    }

    /// <summary>Find the nearest client-grid landing for a terrain-blocked summon.</summary>
    public Position2 ClosestSummonPlacement(Position2 requested, double bodyRadius)
    {
        EnsureFinite(requested, nameof(requested));
        if (!double.IsFinite(bodyRadius) || bodyRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(bodyRadius));
        // Reuse the emulator's existing four-unit body safety margin; the
        // native summon projection constant has not been recovered.
        var clearance = bodyRadius + 4.0;
        if (!double.IsFinite(clearance)) throw new ArgumentOutOfRangeException(nameof(bodyRadius));

        bool Fits(Position2 point) => FitsClearance(point, clearance);
        if (Fits(requested) && Fits(ClientPoint(requested))) return requested;

        // First find any valid client-grid point by walking terrain cells.
        // Blocked interiors cost one cell check each; only movement-floor
        // cells enumerate the two-unit client coordinates inside them.
        var lattice = ClientPoint(Position2.Zero);
        var sourceCell = WorldToCell(requested);
        var origin = new NavCell(Math.Clamp(sourceCell.Column, 0, CountX - 1),
            Math.Clamp(sourceCell.Row, 0, CountY - 1));
        var maxRadius = Math.Max(Math.Max(origin.Column, CountX - 1 - origin.Column),
            Math.Max(origin.Row, CountY - 1 - origin.Row));
        var best = default(Position2);
        var bestSquared = double.PositiveInfinity;
        var found = false;
        for (var radius = 0; radius <= maxRadius && !found; radius++)
        for (var dc = -radius; dc <= radius && !found; dc++)
        for (var dr = -radius; dr <= radius && !found; dr++)
        {
            if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;
            Consider(new NavCell(origin.Column + dc, origin.Row + dr));
        }
        if (!found)
            throw new InvalidOperationException($"No movement floor fits the summon near {requested}.");

        // The first ring is only an upper bound. Check every terrain cell
        // intersecting that distance to select the true nearest valid client
        // coordinate, with lower X then Z as an exact-distance tie-break.
        var reach = Math.Sqrt(bestSquared);
        var from = WorldToCell(new Position2(requested.X - reach, requested.Z - reach));
        var to = WorldToCell(new Position2(requested.X + reach, requested.Z + reach));
        for (var column = Math.Max(0, from.Column); column <= Math.Min(CountX - 1, to.Column); column++)
        for (var row = Math.Max(0, from.Row); row <= Math.Min(CountY - 1, to.Row); row++)
            Consider(new NavCell(column, row));
        return best;

        void Consider(NavCell cell)
        {
            if (!IsMovementWalkable(cell)) return;
            var left = MinX + cell.Column * CellSize;
            var top = MinZ + cell.Row * CellSize;
            var nearX = Math.Max(Math.Max(left - requested.X, 0.0), requested.X - (left + CellSize));
            var nearZ = Math.Max(Math.Max(top - requested.Z, 0.0), requested.Z - (top + CellSize));
            if (nearX * nearX + nearZ * nearZ > bestSquared) return;
            var firstX = (int)Math.Ceiling((left - lattice.X) / 2.0);
            var lastX = (int)Math.Ceiling((left + CellSize - lattice.X) / 2.0) - 1;
            var firstZ = (int)Math.Ceiling((top - lattice.Z) / 2.0);
            var lastZ = (int)Math.Ceiling((top + CellSize - lattice.Z) / 2.0) - 1;
            for (var x = firstX; x <= lastX; x++)
            for (var z = firstZ; z <= lastZ; z++)
            {
                var point = new Position2(lattice.X + x * 2.0, lattice.Z + z * 2.0);
                var dx = point.X - requested.X;
                var dz = point.Z - requested.Z;
                var squared = dx * dx + dz * dz;
                if (squared > bestSquared || !Fits(point)) continue;
                if (!found || squared < bestSquared ||
                    (squared == bestSquared && (point.X < best.X ||
                        (point.X == best.X && point.Z < best.Z))))
                {
                    best = point;
                    bestSquared = squared;
                    found = true;
                }
            }
        }
    }

    /// <summary>Whether a body fits at this point by the ClosestSummonPlacement rule.</summary>
    public bool FitsSummonPlacement(Position2 point, double bodyRadius)
    {
        EnsureFinite(point, nameof(point));
        if (!double.IsFinite(bodyRadius) || bodyRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(bodyRadius));
        return FitsClearance(point, bodyRadius + 4.0);
    }

    private bool FitsClearance(Position2 point, double clearance) =>
        point.X >= MinX + clearance && point.X <= MaxX - clearance &&
        point.Z >= MinZ + clearance && point.Z <= MaxZ - clearance &&
        IsMovementWalkable(point) && PointClearance(point, clearance) >= clearance;

    public bool IsVisionClear(NavCell cell) => IsInBounds(cell) && _visionBlocking[Index(cell)] == 0;
    public bool IsWalkable(NavCell cell) => IsInBounds(cell) && _walkable[Index(cell)] != 0;
    public bool IsPathingWalkable(NavCell cell) => IsInBounds(cell) && _walkablePathing[Index(cell)] != 0;
    internal bool IsHeroPathingWalkable(NavCell cell) =>
        IsInBounds(cell) && _walkableHeroPathing[Index(cell)] != 0;
    public bool IsMovementWalkable(NavCell cell) => IsInBounds(cell) && _walkableMovement[Index(cell)] != 0;

    /// <summary>
    /// Batch 474: the grid height at or above which a walkable cell is a base platform rather than an exit ramp (Crystal Scar's bases stand
    /// at about -35, VisionPackets.cs; the exit ramps descend from their edge to the lanes at -189; 112 walkable cells lie between -50 and
    /// -40). Reconstruction: the threshold is read from the shipped grid, not a shipped value.
    /// </summary>
    public const double PlatformFloor = -40.0;

    /// <summary>
    /// Batch 474: a movement-walkable point on a base platform (grid height at or above <see cref="PlatformFloor"/>). While a Dominion setup
    /// hold stands, a champion's route stays on the platform: the walkable exit ramps are the slab path, which has not formed yet.
    /// Reconstruction: the shipped scripts hold the champion in a 1200 circle only (OdinScriptBehaviorTrees.xml:4637); the client's void
    /// before the slabs land is read from the same grid.
    /// </summary>
    public bool IsPlatformPoint(Position2 point)
    {
        var cell = WorldToCell(point);
        return IsMovementWalkable(cell) && _height[Index(cell)] >= PlatformFloor;
    }

    /// <summary>
    /// Whether the point's cell carries the grid's global vision flag (0x100). The stock client lights every such cell for its team:
    /// the per-frame fog update first builds the set of fog cells over the flagged nav cells (the client,
    /// flag word +0x32 bit 8), then every 0.5 s writes it into the fog field (full brightness)
    /// unless the local hero has a NearSight buff (its vtable slot 0x24c). A point outside
    /// the grid, or a non-finite one, has none (the visibility pass asks it of every unit, so it never throws as WorldToCell does).
    /// </summary>
    public bool HasGlobalVision(Position2 point)
    {
        var column = Math.Truncate((point.X - MinX) / CellSize);
        var row = Math.Truncate((point.Z - MinZ) / CellSize);
        return column >= 0 && column < CountX && row >= 0 && row < CountY && _globalVision[(int)row * CountX + (int)column] != 0;
    }

    public int? BrushIdAt(Position2 point)
    {
        var cell = WorldToCell(point);
        if (!IsInBounds(cell)) return null;
        var label = _brushLabels[Index(cell)];
        return label == 0 ? null : label;
    }

    /// <summary>
    /// The brush a unit stands in, matching the stock client's brush test for a unit,
    /// which is called with the unit's CharacterData SelectionRadius
    /// (mOverrideCollisionRadius, -1 when absent). Below 35u, or for NaN, only
    /// the centre cell counts. Otherwise the radius is clamped to 500u and every
    /// cell whose centre lies in the circle is counted (indices clamped to the
    /// grid), minus non-grass walls; the unit is in brush when it covers a grass
    /// cell and at least (int)(total * 0.4f) of them. The label is the centre
    /// cell's when that is grass, otherwise the most common covered brush.
    /// </summary>
    public int? BrushIdAt(Position2 point, double radius)
    {
        if (!(radius >= MinBrushAreaRadius)) return BrushIdAt(point);
        EnsureFinite(point, nameof(point));
        radius = Math.Min(radius, MaxBrushAreaRadius);
        var x = point.X - MinX;
        var z = point.Z - MinZ;
        var minColumn = ToGridInt((x - radius) / CellSize);
        var maxColumn = ToGridInt((x + radius) / CellSize);
        var minRow = ToGridInt((z - radius) / CellSize);
        var maxRow = ToGridInt((z + radius) / CellSize);
        var squared = radius * radius;
        var cells = (maxColumn - minColumn + 1) * (maxRow - minRow + 1);
        // Covered brush labels in first-seen scan order with their counts (the
        // lookup is over distinct labels, a handful within 500u); a very fine
        // grid rents instead of overflowing the stack.
        int[]? rented = null;
        Span<int> buffer = cells <= 1024
            ? stackalloc int[cells * 2]
            : (rented = ArrayPool<int>.Shared.Rent(cells * 2));
        try
        {
            var labels = buffer.Slice(0, cells);
            var counts = buffer.Slice(cells, cells);
            var distinct = 0;
            var total = 0;
            var grass = 0;
            for (var row = minRow; row <= maxRow; row++)
            {
                var dz = row * CellSize + CellSize / 2.0 - z;
                for (var column = minColumn; column <= maxColumn; column++)
                {
                    var dx = column * CellSize + CellSize / 2.0 - x;
                    if (dx * dx + dz * dz > squared) continue;
                    var index = Math.Clamp(row, 0, CountY - 1) * CountX + Math.Clamp(column, 0, CountX - 1);
                    total++;
                    if (_grass[index] != 0)
                    {
                        grass++;
                        var slot = labels.Slice(0, distinct).IndexOf(_brushLabels[index]);
                        if (slot < 0)
                        {
                            slot = distinct++;
                            labels[slot] = _brushLabels[index];
                            counts[slot] = 0;
                        }
                        counts[slot]++;
                    }
                    else if (_notPassable[index] != 0) total--;
                }
            }
            if (grass == 0 || grass < (int)(total * (double)BrushAreaFraction)) return null;

            if (BrushIdAt(point) is int centre) return centre;
            // Most common label; a tie keeps the label seen first in scan order.
            var best = 0;
            for (var i = 1; i < distinct; i++)
                if (counts[i] > counts[best]) best = i;
            return labels[best];
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    public bool BrushAllowsVision(Position2 provider, Position2 target)
    {
        var targetBrush = BrushIdAt(target);
        return targetBrush is null || BrushIdAt(provider) == targetBrush;
    }

    /// <summary>
    /// Unit-to-unit sight (<see cref="UnitLineOfSightTest"/>), each unit's brush decided by IsWallOfGrass at its CharacterData
    /// SelectionRadius; a negative radius is a point.
    /// </summary>
    public bool HasUnitVision(Position2 viewer, double viewerRadius, Position2 target, double targetRadius) =>
        UnitLineOfSightTest(viewer, viewerRadius, BrushIdAt(viewer, viewerRadius) is not null, target,
            BrushIdAt(target, targetRadius) is not null);

    public double HeightAt(Position2 point)
    {
        var cell = WorldToCell(point);
        if (!IsWalkable(cell))
        {
            var nearest = Nearest(cell, IsWalkable, maxRadius: 50);
            if (nearest is not null)
                cell = nearest.Value;
            else if (!IsInBounds(cell))
                return 0.0;
        }

        return _height[Index(cell)];
    }

    public double PointClearance(Position2 point, double maxRadius = 600.0)
    {
        EnsureFinite(point, nameof(point));
        if (!double.IsFinite(maxRadius) || maxRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maxRadius));
        var origin = WorldToCell(point);
        var maxCells = checked((int)(maxRadius / CellSize) + 3);
        double? best = null;
        for (var radius = 0; radius < maxCells; radius++)
        {
            for (var dc = -radius; dc <= radius; dc++)
            for (var dr = -radius; dr <= radius; dr++)
            {
                if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;
                var cell = new NavCell(origin.Column + dc, origin.Row + dr);
                if (!IsInBounds(cell) || IsWalkable(cell)) continue;
                var minX = cell.Column * CellSize + MinX;
                var minZ = cell.Row * CellSize + MinZ;
                var dx = Math.Max(Math.Max(minX - point.X, 0.0), point.X - (minX + CellSize));
                var dz = Math.Max(Math.Max(minZ - point.Z, 0.0), point.Z - (minZ + CellSize));
                var distance = Math.Sqrt(dx * dx + dz * dz);
                if (best is null || distance < best.Value) best = distance;
            }

            if (best is not null && radius * CellSize > best.Value + CellSize) break;
        }
        return best ?? maxRadius;
    }

    /// <summary>
    /// Sight along a segment from a point viewer: the client's walk (<see cref="ColumnWalkClear"/>,
    /// the inner line-of-sight walk) on the client's flags. A viewer on grass sees through grass until the
    /// walk first leaves it; grass blocks otherwise. Movement and pathfinding keep their own LOS algorithms. Reconstruction (server
    /// helper, kept from VIS-01): both ends must lie in the grid, and a zero-length segment is clear when its cell is.
    /// </summary>
    public bool HasVisionLos(Position2 source, Position2 target)
    {
        var start = WorldToCell(source);
        if (!IsInBounds(start) || !IsInBounds(WorldToCell(target))) return false;
        if (source == target) return IsVisionClear(start);
        return ColumnWalkClear((float)source.X, (float)HeightAt(source), (float)source.Z, (float)target.X,
            (float)HeightAt(target), (float)target.Z, 0f, _grass[Index(start)] != 0, targetInBrush: false, viewerCellsSquared: 0f);
    }

    /// <summary>
    /// The turret's line-of-sight test: whether <paramref name="viewer"/> sees the
    /// turret at <paramref name="turret"/>, without the client's per-position cache (it changes no result).
    /// <list type="number">
    /// <item>A turret in brush (IsWallOfGrass by SelectionRadius) is not seen from outside brush.</item>
    /// <item>Range: the squared 3D distance must be below min(viewer PerceptionBubble radius, 3000) squared (the float constant is the
    /// value without a bubble, <paramref name="viewerPerceptionRadius"/> null).</item>
    /// <item>The column test (<see cref="ColumnLineOfSight"/>) with the turret's SelectionRadius and the start offset
    /// <c>2 * GameplayCollisionRadius + 50.0</c>: GameplayCollisionRadius is the turret's collision radius times the skin scale
    /// coefficient, which is 1 for every turret.</item>
    /// </list>
    /// Heights are the grid's (<see cref="HeightAt"/>): Reconstruction, the client reads the units' own Y.
    /// </summary>
    public bool TurretLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double? viewerPerceptionRadius,
        Position2 turret, double turretSelectionRadius, double turretGameplayRadius)
    {
        var viewerInBrush = BrushIdAt(viewer, viewerSelectionRadius) is not null;
        var turretInBrush = BrushIdAt(turret, turretSelectionRadius) is not null;
        if (turretInBrush && !viewerInBrush) return false;
        var range = (float)Math.Min(viewerPerceptionRadius ?? 3000.0, 3000.0);
        var viewerY = (float)HeightAt(viewer);
        var turretY = (float)HeightAt(turret);
        float dx = (float)viewer.X - (float)turret.X, dy = viewerY - turretY, dz = (float)viewer.Z - (float)turret.Z;
        if (range * range <= dx * dx + dy * dy + dz * dz) return false;
        return ColumnLineOfSight(viewer, viewerY, viewerSelectionRadius, viewerInBrush, turret, turretY,
            turretSelectionRadius, (float)(2.0 * turretGameplayRadius + 50.0), turretInBrush);
    }

    /// <summary>
    /// Unit sight on the client's flags and walk: the column test (<see cref="ColumnLineOfSight"/>) with no footprint and no start
    /// offset, the shape the spell missile's sight test calls it with. A target in brush (IsWallOfGrass by its
    /// SelectionRadius) is not seen from outside brush, as in the turret's sight test, and the walk hides it once it
    /// reaches a non-grass cell beyond the viewer's SelectionRadius. Reconstruction: the base unit's sight test
    /// is an asserting stub, so the unit form is the client's own walk for the objects it does test.
    /// The client's fog overlay draws the same rule on its 128 by 128 field: walls are
    /// NotPassable without SeeThrough, a viewer outside brush stops at grass, and a viewer in brush sees through grass until its ray
    /// leaves the grass beyond the viewer's SelectionRadius (the perception bubble's update grass-check radius).
    /// </summary>
    public bool UnitLineOfSightTest(Position2 viewer, double viewerSelectionRadius, bool viewerInBrush, Position2 target,
        bool targetInBrush)
    {
        // Server guard (Reconstruction, kept from the old walk): an end outside the grid is not seen. The client's walk only skips
        // out-of-grid samples and never tests a unit outside the map.
        if (!IsInBounds(WorldToCell(viewer)) || !IsInBounds(WorldToCell(target))) return false;
        if (targetInBrush && !viewerInBrush) return false;
        // A viewer exactly on the target sees it, even in an opaque cell: the column test passes when footprint^2 >= distance^2
        // (the client's compare and branch), and the footprint here is 0.
        return ColumnLineOfSight(viewer, (float)HeightAt(viewer), viewerSelectionRadius, viewerInBrush, target,
            (float)HeightAt(target), 0.0, 0f, targetInBrush);
    }

    /// <summary>
    /// The neutral camp's position-marker sight test:
    /// a viewer sees a camp marker within 1000 (squared 3D distance below 1e6) when the column test
    /// with no footprint passes, the walk starting the viewer's PathfindingCollisionRadius + 200.0 out.
    /// The marker is never in brush; the viewer's brush is IsWallOfGrass by its SelectionRadius.
    /// </summary>
    public bool CampMarkerLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double viewerPathfindingRadius,
        Position2 marker)
    {
        var viewerY = (float)HeightAt(viewer);
        var markerY = (float)HeightAt(marker);
        float dx = (float)viewer.X - (float)marker.X, dy = viewerY - markerY, dz = (float)viewer.Z - (float)marker.Z;
        if (!(dx * dx + dy * dy + dz * dz < 1000000f)) return false;
        return ColumnLineOfSight(viewer, viewerY, viewerSelectionRadius, BrushIdAt(viewer, viewerSelectionRadius) is not null,
            marker, markerY, 0.0, (float)(viewerPathfindingRadius + 200.0), targetInBrush: false);
    }

    /// <summary>
    /// The column test. A viewer within the target's footprint radius
    /// <paramref name="targetRadius"/> (XZ) sees it. Otherwise, with <c>dir</c> the XZ direction from the viewer to the target and
    /// <c>perp</c> it turned 90 degrees about Y, columns start at <c>target - 0.7 * radius * perp</c>
    /// and step <c>2 * cellSize</c> along <c>perp</c>, <c>1 - trunc(-0.7 * radius / cellSize)</c> of them.
    /// The viewer sees the target when, for some column centre <c>c</c>, <c>dot(c - viewer, dir) &lt;= 0</c>, or the walk
    /// (<see cref="ColumnWalkClear"/>) to the column's near side <c>c - radius * dir</c> is clear.
    /// </summary>
    private bool ColumnLineOfSight(Position2 viewer, float viewerY, double viewerSelectionRadius, bool viewerInBrush,
        Position2 target, float targetY, double targetRadius, float startOffset, bool targetInBrush)
    {
        var cell = (float)CellSize;
        float vx = (float)viewer.X, vz = (float)viewer.Z, radius = (float)targetRadius;
        float dx = (float)target.X - vx, dz = (float)target.Z - vz;
        if (dx * dx + dz * dz <= radius * radius) return true;
        var length = MathF.Sqrt(dx * dx + dz * dz);
        float dirX = dx / length, dirZ = dz / length;
        float perpX = dirZ, perpZ = -dirX;
        var columns = 1 - (int)(-0.7f * radius / cell);
        // The viewer's SelectionRadius in cells, squared: how far the walk keeps the viewer's brush allowance.
        var viewerCells = viewerSelectionRadius > 0.0 ? (float)viewerSelectionRadius / cell : 0f;
        float columnX = (float)target.X - 0.7f * radius * perpX, columnZ = (float)target.Z - 0.7f * radius * perpZ;
        for (var column = 0; column < columns; column++)
        {
            if ((columnX - vx) * dirX + (columnZ - vz) * dirZ <= 0f) return true;
            if (ColumnWalkClear(vx, viewerY, vz, columnX - radius * dirX, targetY, columnZ - radius * dirZ, startOffset,
                    viewerInBrush, targetInBrush, viewerCells * viewerCells))
                return true;
            columnX += 2f * cell * perpX;
            columnZ += 2f * cell * perpZ;
        }
        return false;
    }

    /// <summary>
    /// The inner walk, as the column test calls it. The walk starts
    /// <paramref name="startOffset"/> units from the viewer toward the near point (the direction is normalised in place before the
    /// offset is applied, so the offset is a distance); a start at or past the point is clear. It then takes half-cell steps over
    /// the 3D length plus 0.02 (the height difference counts, the step is horizontal), grid coordinates divided by
    /// <c>cellSize - 0.01</c>, at most 999 samples. An in-bounds cell blocks when it is opaque (<see cref="_visionBlocking"/>)
    /// or brush seen from outside brush. A viewer in brush may look through brush until the walk reaches a non-brush cell beyond the
    /// viewer's SelectionRadius: there a target in brush is hidden, otherwise the allowance ends.
    /// </summary>
    private bool ColumnWalkClear(float vx, float vy, float vz, float sx, float sy, float sz, float startOffset,
        bool viewerInBrush, bool targetInBrush, float viewerCellsSquared)
    {
        var cell = (float)CellSize;
        float minX = (float)MinX, minZ = (float)MinZ;
        float dx = sx - vx, dz = sz - vz;
        var length = MathF.Sqrt(dx * dx + dz * dz);
        if (length == 0f) return true;
        float dirX = dx / length, dirZ = dz / length;
        float startX = vx + startOffset * dirX, startZ = vz + startOffset * dirZ;
        float restX = sx - startX, restY = sy - vy, restZ = sz - startZ;
        if (restX * dirX + restZ * dirZ <= 0f) return true;
        var x = (startX - minX) / (cell - 0.01f);
        var z = (startZ - minZ) / (cell - 0.01f);
        float viewerColumn = (vx - minX) / cell, viewerRow = (vz - minZ) / cell;
        var remaining = MathF.Sqrt(restX * restX + restY * restY + restZ * restZ) + 0.02f;
        for (var samples = 0; samples < 999 && remaining >= 0f; samples++)
        {
            var cellHere = new NavCell((int)x, (int)z);
            if (IsInBounds(cellHere))
            {
                var index = Index(cellHere);
                if (_visionBlocking[index] != 0) return false;
                var brush = _grass[index] != 0;
                if (!viewerInBrush)
                {
                    if (brush) return false;
                }
                else if (!brush)
                {
                    float offX = x - viewerColumn, offZ = z - viewerRow;
                    if (viewerCellsSquared < offX * offX + offZ * offZ)
                    {
                        if (targetInBrush) return false;
                        viewerInBrush = false;
                    }
                }
            }
            x += dirX * 0.5f;
            z += dirZ * 0.5f;
            remaining -= cell * 0.5f;
        }
        return true;
    }

    /// <summary>Checks a segment against the client movement grid.</summary>
    public bool HasMovementLineOfSight(Position2 source, Position2 target) =>
        HasLineOfSight(source, target, IsMovementWalkable);

    /// <summary>Checks a segment against the server pathing grid.</summary>
    public bool HasPathingLineOfSight(Position2 source, Position2 target) =>
        HasLineOfSight(source, target, IsPathingWalkable);

    /// <summary>
    /// Checks a world segment through every grid cell it actually crosses.
    /// The generic LOS contract intentionally remains cell-centre Bresenham;
    /// hero movement also needs the exact sub-cell origin because the fixed
    /// follower can enter a blocked cell before reaching the next waypoint.
    /// </summary>
    private bool HasExactLineOfSight(Position2 source, Position2 target,
        Func<NavCell, bool> checker)
    {
        EnsureFinite(source, nameof(source));
        EnsureFinite(target, nameof(target));
        var cell = WorldToCell(source);
        var goal = WorldToCell(target);
        if (!IsInBounds(cell) || !IsInBounds(goal) || !checker(cell)) return false;
        if (cell == goal) return true;

        var dx = target.X - source.X;
        var dz = target.Z - source.Z;
        var stepColumn = Math.Sign(dx);
        var stepRow = Math.Sign(dz);
        var nextColumn = stepColumn > 0 ? cell.Column + 1 : cell.Column;
        var nextRow = stepRow > 0 ? cell.Row + 1 : cell.Row;
        var nextColumnT = stepColumn == 0 ? double.PositiveInfinity :
            (MinX + nextColumn * CellSize - source.X) / dx;
        var nextRowT = stepRow == 0 ? double.PositiveInfinity :
            (MinZ + nextRow * CellSize - source.Z) / dz;
        var columnTStep = stepColumn == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dx);
        var rowTStep = stepRow == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dz);

        for (var traversed = 0; traversed <= CountX + CountY + 4; traversed++)
        {
            if (cell == goal) return true;
            // A mixed-sign segment may reach the goal column (or row) before
            // the other axis. Do not advance that completed axis again when
            // the remaining boundary happens to be an exact corner tie.
            if (cell.Column == goal.Column) nextColumnT = double.PositiveInfinity;
            if (cell.Row == goal.Row) nextRowT = double.PositiveInfinity;
            if (Math.Abs(nextColumnT - nextRowT) <= 1e-12)
            {
                // Keep the existing diagonal rule: one open side permits
                // point contact; two blocked sides close the corner.
                if (!checker(new NavCell(cell.Column + stepColumn, cell.Row)) &&
                    !checker(new NavCell(cell.Column, cell.Row + stepRow)))
                    return false;
                cell = new NavCell(cell.Column + stepColumn, cell.Row + stepRow);
                nextColumnT += columnTStep;
                nextRowT += rowTStep;
            }
            else if (nextColumnT < nextRowT)
            {
                cell = new NavCell(cell.Column + stepColumn, cell.Row);
                nextColumnT += columnTStep;
            }
            else
            {
                cell = new NavCell(cell.Column, cell.Row + stepRow);
                nextRowT += rowTStep;
            }
            if (!checker(cell)) return false;
        }
        return false;
    }

    internal bool HasHeroMovementLineOfSight(Position2 source, Position2 target) =>
        HasExactLineOfSight(source, target, IsMovementWalkable);

    /// <summary>
    /// <see cref="HasHeroMovementLineOfSight"/> for a chord that leaves the hero's own position: the cell it starts in is not tested, as the
    /// client's A* seeds its start cell untested (see <c>ClientPathPlanner</c>), so a hero
    /// standing on a blocked cell beside floor plans out of it (batch 676).
    /// </summary>
    internal bool HasHeroMovementLineOfSightLeaving(Position2 source, Position2 target)
    {
        var own = WorldToCell(source);
        return HasExactLineOfSight(source, target, cell => cell == own || IsMovementWalkable(cell));
    }

    /// <summary>Checks a hero segment against the client-sized turret pathing grid.</summary>
    internal bool HasHeroPathingLineOfSight(Position2 source, Position2 target) =>
        HasExactLineOfSight(source, target, IsHeroPathingWalkable);

    public bool SegmentClearOfUnits(Position2 start, Position2 end,
        IEnumerable<NavigationObstacle> obstacles, bool escape = false)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dz * dz;
        bool Blocks(NavigationObstacle obstacle)
        {
            if (!double.IsFinite(obstacle.Center.X) || !double.IsFinite(obstacle.Center.Z) ||
                !double.IsFinite(obstacle.Radius) || obstacle.Radius < 0.0)
                throw new ArgumentException("Obstacles must have finite non-negative radii", nameof(obstacles));
            var radius = obstacle.Radius;
            var ax = start.X - obstacle.Center.X;
            var az = start.Z - obstacle.Center.Z;
            var dot = ax * dx + az * dz;
            if (escape && ax * ax + az * az < radius * radius)
                return dot < -1e-6;

            var t = lengthSquared == 0.0 ? 0.0 : Math.Clamp(-dot / lengthSquared, 0.0, 1.0);
            var closestX = ax + t * dx;
            var closestZ = az + t * dz;
            return closestX * closestX + closestZ * closestZ < radius * radius - 1e-6;
        }
        // Every route validation calls this thousands of times per planning pass with an array; iterating the
        // array directly avoids one enumerator allocation per call. The answer is the same for any sequence.
        if (obstacles is NavigationObstacle[] array)
        {
            foreach (var obstacle in array)
                if (Blocks(obstacle)) return false;
            return true;
        }
        foreach (var obstacle in obstacles)
            if (Blocks(obstacle)) return false;
        return true;
    }

    internal bool FirstSegmentClearOfUnits(Position2 wireStart, Position2 authoritativeStart,
        Position2 end, IReadOnlyList<NavigationObstacle> obstacles)
    {
        const double wireScale = 2.0;
        const double epsilon = 1e-6;
        if (Math.Abs(wireStart.X - authoritativeStart.X) > wireScale + epsilon ||
            Math.Abs(wireStart.Z - authoritativeStart.Z) > wireScale + epsilon ||
            !SegmentClearOfUnits(wireStart, end, obstacles, escape: true))
            return false;

        var delta = end - authoritativeStart;
        var length = Distance(authoritativeStart, end);
        var anchorDistance = Distance(wireStart, authoritativeStart);
        var ordinary = new List<NavigationObstacle>();
        foreach (var obstacle in obstacles)
        {
            var exactOffset = authoritativeStart - obstacle.Center;
            var wireOffset = wireStart - obstacle.Center;
            var radiusSquared = obstacle.Radius * obstacle.Radius;
            if (exactOffset.X * exactOffset.X + exactOffset.Z * exactOffset.Z >= radiusSquared ||
                wireOffset.X * wireOffset.X + wireOffset.Z * wireOffset.Z >= radiusSquared)
            {
                ordinary.Add(obstacle);
                continue;
            }

            var dot = exactOffset.X * delta.X + exactOffset.Z * delta.Z;
            if (dot >= -epsilon) continue;
            if (length <= epsilon || -dot / length > Math.Min(anchorDistance, length) + epsilon)
                return false;
        }

        // Obstacles containing both representations are an existing overlap.
        // Their wire leg already moves outward, and the exact leg must turn
        // outward within the at-most-one-wire-step-per-axis displacement above.
        // Every other obstacle keeps the ordinary full-segment check.
        return SegmentClearOfUnits(authoritativeStart, end, ordinary, escape: true);
    }

    private int Index(NavCell cell) => checked(cell.Row * CountX + cell.Column);

    private static void EnsureFinite(Position2 point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Z))
            throw new ArgumentException("Position must be finite", parameterName);
    }

    private static int ToGridInt(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)Math.Truncate(value));
    }

    private static double Distance(Position2 a, Position2 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private Position2 ClientPoint(Position2 point) =>
        Geometry.ClientWaypoints([point], new NavigationBounds(MinX, MaxX, MinZ, MaxZ))[0];

    private Position2 HeroCellToWire(NavCell cell) => ClientPoint(CellToWorld(cell));

    private NavCell? Nearest(NavCell origin, Func<NavCell, bool> checker, int maxRadius)
    {
        if (checker(origin) && IsInBounds(origin)) return origin;
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var dc = -radius; dc <= radius; dc++)
            for (var dr = -radius; dr <= radius; dr++)
            {
                if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;
                var candidate = new NavCell(origin.Column + dc, origin.Row + dr);
                if (checker(candidate)) return candidate;
            }
        }
        return null;
    }

    private bool LineOfSight(NavCell start, NavCell end, Func<NavCell, bool> checker)
    {
        var x0 = start.Column;
        var y0 = start.Row;
        var x1 = end.Column;
        var y1 = end.Row;
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            if (!checker(new NavCell(x0, y0))) return false;
            if (x0 == x1 && y0 == y1) return true;
            var twice = 2 * error;
            if (twice >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (twice <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private bool HasLineOfSight(Position2 source, Position2 target, Func<NavCell, bool> checker)
    {
        var start = WorldToCell(source);
        var end = WorldToCell(target);
        return IsInBounds(start) && IsInBounds(end) && LineOfSight(start, end, checker);
    }

    private static int[] LabelBrushes(byte[] grass, int countX, int countY)
    {
        var labels = new int[grass.Length];
        var nextLabel = 0;
        for (var start = 0; start < grass.Length; start++)
        {
            if (grass[start] == 0 || labels[start] != 0) continue;
            labels[start] = ++nextLabel;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var column = current % countX;
                var row = current / countX;
                foreach (var (dc, dr) in FourNeighbors)
                {
                    var nextColumn = column + dc;
                    var nextRow = row + dr;
                    if (nextColumn < 0 || nextColumn >= countX || nextRow < 0 || nextRow >= countY)
                        continue;
                    var next = nextRow * countX + nextColumn;
                    if (grass[next] != 0 && labels[next] == 0)
                    {
                        labels[next] = nextLabel;
                        stack.Push(next);
                    }
                }
            }
        }
        return labels;
    }

    private static byte[] BuildTerrainPathingGrid(
        byte[] walkable,
        NavigationGridMetadata metadata,
        NavigationGridSettings settings)
    {
        var pathing = (byte[])walkable.Clone();
        var radiusCells = Math.Max(1, checked((int)Math.Ceiling(settings.CollisionRadius / metadata.CellSize)));
        for (var index = 0; index < pathing.Length; index++)
        {
            if (walkable[index] != 0) continue;
            var column = index % metadata.CountX;
            var row = index / metadata.CountX;
            var minRow = Math.Max(0, row - radiusCells);
            var maxRow = Math.Min(metadata.CountY - 1, row + radiusCells);
            var minColumn = Math.Max(0, column - radiusCells);
            var maxColumn = Math.Min(metadata.CountX - 1, column + radiusCells);
            for (var currentRow = minRow; currentRow <= maxRow; currentRow++)
            {
                var dz = (currentRow - row) * metadata.CellSize;
                for (var currentColumn = minColumn; currentColumn <= maxColumn; currentColumn++)
                {
                    var dx = (currentColumn - column) * metadata.CellSize;
                    if (Math.Sqrt(dx * dx + dz * dz) <= settings.CollisionRadius)
                        pathing[currentRow * metadata.CountX + currentColumn] = 0;
                }
            }
        }
        return pathing;
    }
}
