using Amberline.Core;

namespace Amberline.Simulation.Movement;

/// <summary>
/// Batch 504: the visible spawn forcefield of each Dominion base, the edge a held champion stays inside before the release (80 s).
/// Batch 514 (owner, 31 October 2026: the visible blue ring is the only limit): it is the hold's single limit inside the ring. The stairs
/// footprint (batch 474d) is gone: every point of it is walkable base ground at or above -40 (the shipped grid), inside the ring and out, so it
/// kept nobody off the void and only made invisible walls inside the ring.
/// </summary>
public static class HoldFootprint
{
    /// <summary>
    /// Batch 504: the radius of the visible spawn forcefield's wall at its foot. The barrier's buff (DATA/Spells/OdinOpeningBarrierParticle.luaobj)
    /// binds Odin_Forcefield_green.troy to the barrier unit, no bone. Its four wall emitters (distort-light, distort-light2, distort, fadetop)
    /// draw DATA/Particles/forcefield.sco (pSphere1, a ring of 623.5 at its lowest row y -98.75, 631.3 at y 0) with p-scale stored as the byte
    /// vector (8, 5, 8), which the client reads divided by ten: (0.8, 0.5, 0.8), and p-offset (0, 90, 0). So the wall is a circle of 498.8 at
    /// 40 above the ground and 505.0 at 90; it does not follow the platform. Kennen R's disc.sco (radius 0.1, p-scale 5400) and saucer9.sco
    /// (15.8, (40, 40, 40) as floats) both give its 550 area, so the client multiplies a mesh by p-scale as it reads it. No other scale applies:
    /// the file has no p-scalebyradius, p-scalebyheight or e-shape-scale, and the 0x8C carries none.
    /// </summary>
    public const double WallRadius = 498.8;

    /// <summary>
    /// Batch 514b: the forcefield's centre off its barrier. Every ring of forcefield.sco is centred on its CentralPoint (6.504, -34.970) in x and
    /// z, not on the mesh origin, and the SCO loader keeps the vertices as stored (the loader: CentralPoint goes to its field, the
    /// vertices to their field, nothing is subtracted; the scene meshes it loads, such as Info_Spawn_Barrier1.sco, are drawn at their stored world
    /// vertices). Every wall emitter has e-local-orient 0 (the client's default is 1) and the 0x8C carries direction 0 and no bone,
    /// so the mesh is not turned with the barrier. Scaled by 0.8: (+5.20, -27.98) in world x and z, for both bases.
    /// Reconstruction: the client's mesh-particle draw is not decoded.
    /// </summary>
    private static readonly Position2 MeshCentre = new(6.5039 * 0.8, -34.9695 * 0.8);

    // The barrier units stand at Locations.dat OrderBarrier / ChaosBarrier: the central points of LEVELS/Map8/Scene/Info_Spawn_Barrier1.SCB and
    // Info_Spawn_Barrier2.SCB, (578.98, 4114.34) and (13300.33, 4131.78). The rings' centres are (584.19, 4086.36) and (13305.54, 4103.80).
    private static readonly Position2 OrderCentre = new(578.9835 + MeshCentre.X, 4114.3398 + MeshCentre.Z);
    private static readonly Position2 ChaosCentre = new(13300.3320 + MeshCentre.X, 4131.7773 + MeshCentre.Z);

    /// <summary>
    /// Batch 514b: how far from the forcefield's centre a unit's centre may stand with its whole body inside the wall: the wall less the unit's
    /// gameplay radius (Entity.CombatRadius, 65 for most champions; batch 508 used 65 for all). NOT RETAIL (owner decision, 31 October 2026:
    /// champions stop at the visible forcefield before the release): the shipped ceremony holds a 1200 circle about the start point
    /// (OdinScriptBehaviorTrees.xml:4637), and the base's walkable ground reaches about 790-900 from the spawn.
    /// </summary>
    public static double Limit(double bodyRadius) => WallRadius - bodyRadius;

    /// <summary>Batch 504: a point's distance from the nearer base's forcefield centre.</summary>
    public static double ForcefieldDistance(Position2 point) => Math.Min(Distance(point, OrderCentre), Distance(point, ChaosCentre));

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
