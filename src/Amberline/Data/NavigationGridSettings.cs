using Amberline.Core;

namespace Amberline.Data;

/// <summary>Explicit inputs that turn a raw aimesh into the Map1 navigation views.</summary>
public sealed class NavigationGridSettings
{
    public double CollisionRadius { get; init; } = MapDefinitions.CollisionRadius;
    public double TurretPathStampRadius { get; init; } = MapDefinitions.TurretPathStampRadius;
    public double TurretClientStampRadius { get; init; } = MapDefinitions.TurretClientStampRadius;
    public IReadOnlyList<TurretDefinition> Turrets { get; init; } = MapDefinitions.Turrets;
    /// <summary>The Nexuses and inhibitors the client stamps into its nav grid (see <see cref="NavigationGrid.StampFilledCircle"/>).</summary>
    public IReadOnlyList<NavigationStamp> Structures { get; init; } = MapDefinitions.StructureStamps;

    public static NavigationGridSettings Default => new();

    internal void Validate()
    {
        if (!double.IsFinite(CollisionRadius) || CollisionRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(CollisionRadius));
        if (!double.IsFinite(TurretPathStampRadius) || TurretPathStampRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(TurretPathStampRadius));
        if (!double.IsFinite(TurretClientStampRadius) || TurretClientStampRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(TurretClientStampRadius));
        ArgumentNullException.ThrowIfNull(Turrets);
        ArgumentNullException.ThrowIfNull(Structures);
        foreach (var turret in Turrets)
        {
            if (!double.IsFinite(turret.Position.X) || !double.IsFinite(turret.Position.Z))
                throw new ArgumentException($"Turret {turret.Name} has a non-finite position", nameof(Turrets));
        }
        foreach (var structure in Structures)
        {
            if (!double.IsFinite(structure.Center.X) || !double.IsFinite(structure.Center.Z) ||
                !double.IsFinite(structure.Radius) || structure.Radius < 0.0)
                throw new ArgumentException($"Structure stamp {structure.Name} is not finite", nameof(Structures));
        }
    }
}
