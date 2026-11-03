using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-summon-terrain-blocked-nearest-and-wire")]
    public static void BlockedSummonsUseNearestBodyClearFloor()
    {
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, Team.Order, ally: true);
            var runtime = match.Runtime;
            runtime.Tick(10.1); // Production opening protection would hide the burst damage.
            var hero = match.Hero(0);
            var (slot, bodyRadius, castRange) = SummonSpec(champion);
            var fixture = FindBlockedSummonFixture(runtime.Navigation, hero.Radius,
                bodyRadius, castRange);
            hero.Position = fixture.Caster;
            var observer = match.Hero(2);
            observer.Position = fixture.Escape;
            TestAssert.True(runtime.Navigation.HasVisionLos(observer.Position, fixture.Expected) &&
                runtime.Navigation.BrushAllowsVision(observer.Position, fixture.Expected),
                $"{champion}: allied observer sees the resolved summon floor");
            var victim = match.Hero(1);
            var delta = fixture.Requested - fixture.Expected;
            var length = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            TestAssert.True(length > 8, $"{champion}: blocked aim has a distinct nearest landing");
            if (champion is "Annie" or "Yorick")
            {
                var radius = champion == "Annie"
                    ? GameData.Default.Spell("InfernalGuardian").CastRadius : 250.0;
                // Playtest #4, 25 Sept 2026: Annie's burst now follows Tibbers'
                // relocated floor like Yorick's disease, so the victim sits just
                // inside that center and beyond the radius of the submitted aim.
                var center = fixture.Expected;
                var away = new Position2(delta.X / length, delta.Z / length);
                victim.Position = center - new Position2(away.X * (radius - 1), away.Z * (radius - 1));
                victim.HealthRegen = 0;
            }
            var victimHealth = victim.Health;
            runtime.Delivery.Flush(); match.Clear();
            match.CastGround(0, fixture.Requested, slot);
            var actor = WaitForSummon(match, champion);

            TestAssert.Equal(fixture.Expected, actor.Position,
                $"{champion}: pet appears at the nearest quantized movement floor");
            TestAssert.True(SummonFits(runtime.Navigation, actor.Position, bodyRadius),
                $"{champion}: full body clears terrain and the map edge");
            TestAssert.False(runtime.Navigation.IsMovementWalkable(fixture.Requested),
                $"{champion}: the submitted aim really is inside blocked terrain");
            AssertNoCloserClientFloor(runtime.Navigation, fixture.Requested,
                actor.Position, bodyRadius, champion);
            var towardCaster = fixture.Caster - fixture.Requested;
            var towardLanding = actor.Position - fixture.Requested;
            TestAssert.True(towardCaster.X * towardLanding.X +
                towardCaster.Z * towardLanding.Z < 0,
                $"{champion}: placement is opposite the caster ray, not a ray-backoff");

            AssertSummonSpawnWire(match, actor, captureIndex: 2);
            AssertCastAimWire(match, hero.NetId, slot, fixture.Requested);
            var effect = champion switch
            {
                "Annie" => "InfernalGuardian_tar.troy",
                "Shaco" => "jackintheboxpoof.troy",
                _ => "yorick_necroExplosion.troy",
            };
            if (champion == "Annie")
            {
                TestAssert.False(runtime.Navigation.IsVisionClear(
                    runtime.Navigation.WorldToCell(fixture.Requested)),
                    "Annie: blocked aim point is an opaque wall cell");
                TestAssert.False(runtime.Navigation.HasVisionLos(hero.Position, fixture.Requested),
                    "Annie: caster has no sight into the opaque aim cell");
            }
            // Playtest #4, 25 Sept 2026: Annie's burst FX now follows Tibbers
            // to the relocated floor instead of staying hidden in the wall.
            AssertSummonEffectWire(match, effect, actor.Position, captureIndex: 2);
            if (champion == "Annie")
            {
                TestAssert.True(victim.Health < victimHealth,
                    "Tibbers burst is centered on the relocated summon (playtest #4)");
                var initialAnchor = VisionPackets.WaypointList(actor.NetId,
                    [actor.Position], syncId: 1);
                TestAssert.True(match.Captures[0].Packets.Any(packet =>
                    packet.Channel == initialAnchor.Channel &&
                    packet.Plaintext.Span.SequenceEqual(initialAnchor.Plaintext.Span)),
                    "Tibbers' explicit initial waypoint has sync 1 and the resolved body position");
            }
            else if (champion == "Yorick")
                TestAssert.True(victim.Health < victimHealth,
                    "Decayed ghoul's initial disease burst follows the relocated ghoul");

            if (champion is "Annie" or "Yorick")
            {
                observer.Position = hero.Position; // Free the witnessed escape endpoint for the pet route.
                TestAssert.True(runtime.Movement.SetMoveOrder(actor.NetId, fixture.Escape).Count > 0,
                    $"{champion}: mobile pet gets a route away from the wall");
            }
        }
    }

    [Check("spells-live-summon-terrain-valid-point-unchanged")]
    public static void LegalSummonPointStaysUnquantized()
    {
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, Team.Order);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var (slot, bodyRadius, _) = SummonSpec(champion);
            var requested = new Position2(7000.25, 7000.75);
            TestAssert.True(SummonFits(runtime.Navigation, requested, bodyRadius),
                $"{champion}: fractional control point clears the whole summon body");
            hero.Position = requested;
            runtime.Delivery.Flush(); match.Clear();
            match.CastGround(0, requested, slot);
            var actor = WaitForSummon(match, champion);
            // Restated in batch 263 (bundle 4 red from batch 254's seat): the summon is placed on the requested point without client-grid
            // snapping (the spawn packet's position is floats). A unit that is then given a path stands on that path's first point, the
            // point's two-unit lattice anchor (the client's path install): Tibbers gets its spawn's one-point path
            // and a ghoul its first Pet.lua order in the spawn's tick, both before the observer's create goes out at the flush, so the
            // spawn packet carries the anchor too. The Jack In The Box gets no path and keeps the point.
            var stands = champion == "Shaco" ? requested : runtime.Movement.ClientAnchor(requested);
            TestAssert.Equal(stands, actor.Position,
                $"{champion}: a legal requested point is preserved without client-grid snapping until the unit's first path");
            AssertSummonSpawnWire(match, actor);
            AssertCastAimWire(match, hero.NetId, slot, requested);
            var effect = champion switch
            {
                "Annie" => "InfernalGuardian_tar.troy",
                "Shaco" => "jackintheboxpoof.troy",
                _ => "yorick_necroExplosion.troy",
            };
            AssertSummonEffectWire(match, effect, requested);
        }
    }

    [Check("spells-live-summon-terrain-yorick-e-ghoul")]
    public static void RavenousGhoulPastTargetLeavesTerrain()
    {
        // Playtest B2 YV6, 26 Sept 2026: E summons 250 units past its target
        // and three logged Ravenous ghouls stayed inside walls.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var yorick = match.Hero(0);
            var enemy = match.Hero(1);
            var radius = GameData.Default.Champion("YorickRavenousGhoul").PathfindingRadius;
            var (caster, target, wall) = FindRavenousWallFixture(runtime.Navigation,
                yorick.Radius, enemy.Radius, yorick.CombatRadius + enemy.CombatRadius);
            yorick.Position = caster;
            yorick.Facing = new Position2((target.X - caster.X) / 200.0, (target.Z - caster.Z) / 200.0);
            enemy.Position = target;
            runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 2, enemy);
            Entity? ghoul = null;
            for (var step = 0; step < 100 && ghoul is null; step++)
            {
                runtime.Tick(.01);
                ghoul = runtime.World.Entities.Values.FirstOrDefault(entity => entity.Alive &&
                    entity.Kind == EntityKind.YorickGhoul && entity.OwnerNetId == yorick.NetId);
            }
            if (ghoul is null)
                throw new InvalidOperationException($"{team}: production E did not create its Ravenous ghoul");

            // The point YorickRavenous.TargetExecute casts the child at: Yorick + facing * (BBDistanceBetweenObjects + 250), the distance being the
            // gap between the two bodies (centres minus both collision radii) since batch 112.
            var distance = Math.Max(0.0, Distance(yorick.Position, enemy.Position) - yorick.CombatRadius - enemy.CombatRadius) + 250.0;
            var requested = new Position2(yorick.Position.X + yorick.Facing.X * distance,
                yorick.Position.Z + yorick.Facing.Z * distance);
            TestAssert.True(Distance(requested, wall) < 1.0,
                $"{team}: E aims its ghoul at the fixture wall point");
            TestAssert.False(runtime.Navigation.IsMovementWalkable(requested),
                $"{team}: E's point 250 units past the body gap is inside terrain");
            TestAssert.Equal(runtime.Navigation.ClosestSummonPlacement(requested, radius), ghoul.Position,
                $"{team}: the Ravenous ghoul lands on the nearest body-clear floor");
            TestAssert.True(SummonFits(runtime.Navigation, ghoul.Position, radius),
                $"{team}: the Ravenous ghoul body clears terrain and the map edge");
            AssertSummonSpawnWire(match, ghoul);
        }
    }

    /// <summary>
    /// A wall cell with a clear caster 200 units behind a clear target; the wall is the point E aims its ghoul at, 250 units past the gap between the
    /// two bodies (their centres 200 apart, less <paramref name="combatRadii"/>, the sum of both collision radii).
    /// </summary>
    private static (Position2 Caster, Position2 Target, Position2 Wall) FindRavenousWallFixture(
        NavigationGrid grid, double casterRadius, double targetRadius, double combatRadii)
    {
        var anchor = grid.WorldToCell(new Position2(6081.097, 4914.808));
        for (var radius = 0; radius <= 35; radius++)
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
            var cell = new NavCell(anchor.Column + dx, anchor.Row + dz);
            if (!grid.IsInBounds(cell)) continue;
            var wall = grid.CellToWorld(cell);
            if (grid.IsMovementWalkable(wall)) continue;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Math.PI / 8.0;
                var (x, z) = (Math.Cos(angle), Math.Sin(angle));
                var target = new Position2(wall.X - x * (250.0 - combatRadii), wall.Z - z * (250.0 - combatRadii));
                var caster = new Position2(wall.X - x * (450.0 - combatRadii), wall.Z - z * (450.0 - combatRadii));
                if (SummonFits(grid, target, targetRadius) && SummonFits(grid, caster, casterRadius) &&
                    grid.HasMovementLineOfSight(caster, target) &&
                    grid.BrushIdAt(caster) is null && grid.BrushIdAt(target) is null &&
                    grid.HasVisionLos(caster, target))
                    return (caster, target, wall);
            }
        }
        throw new InvalidOperationException("Map1 has no wall 250 units past the body gap of a clear Yorick E target");
    }

    private static (int Slot, double Radius, double CastRange) SummonSpec(string champion) => champion switch
    {
        "Annie" => (3, GameData.Default.Champion("AnnieTibbers").PathfindingRadius,
            GameData.Default.Spell("InfernalGuardian").CastRange),
        "Shaco" => (1, GameData.Default.Champion("ShacoBox").GameplayRadius,
            GameData.Default.Spell("JackInTheBox", 5).CastRange),
        "Yorick" => (1, GameData.Default.Champion("YorickDecayedGhoul").PathfindingRadius,
            GameData.Default.Spell("YorickDecayed", 5).CastRange),
        _ => throw new ArgumentOutOfRangeException(nameof(champion)),
    };

    private static (Position2 Requested, Position2 Caster, Position2 Expected, Position2 Escape)
        FindBlockedSummonFixture(NavigationGrid grid, double casterRadius,
            double bodyRadius, double castRange)
    {
        var anchor = grid.WorldToCell(new Position2(6081.097, 4914.808));
        for (var radius = 0; radius <= 35; radius++)
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
            var cell = new NavCell(anchor.Column + dx, anchor.Row + dz);
            if (!grid.IsInBounds(cell)) continue;
            var center = grid.CellToWorld(cell);
            var requested = new Position2((float)(center.X + .25), (float)(center.Z + .75));
            if (grid.IsMovementWalkable(requested) || grid.IsVisionClear(cell)) continue;
            var expected = grid.ClosestSummonPlacement(requested, bodyRadius);
            var displacement = expected - requested;
            var length = Math.Sqrt(displacement.X * displacement.X +
                displacement.Z * displacement.Z);
            if (length < 8 || length > 120) continue;
            var escape = expected + new Position2(
                displacement.X / length * 100, displacement.Z / length * 100);
            if (!SummonFits(grid, escape, bodyRadius) ||
                !grid.HasMovementLineOfSight(expected, escape) ||
                !grid.HasVisionLos(escape, expected) ||
                !grid.BrushAllowsVision(escape, expected)) continue;
            foreach (var distance in new[] { 180.0, 240.0, 320.0, 400.0 })
            {
                if (distance >= castRange - 10.0) continue;
                for (var step = 0; step < 32; step++)
                {
                    var angle = step * 2.0 * Math.PI / 32.0;
                    var caster = requested + new Position2(
                        Math.Cos(angle) * distance, Math.Sin(angle) * distance);
                    var ray = caster - requested;
                    if (ray.X * displacement.X + ray.Z * displacement.Z >= -length * distance * .25 ||
                        !SummonFits(grid, caster, casterRadius)) continue;
                    return (requested, caster, expected, escape);
                }
            }
        }
        throw new InvalidOperationException("Map1 has no blocked summon point with an opposite-side caster");
    }

    private static bool SummonFits(NavigationGrid grid, Position2 point, double bodyRadius)
    {
        var clearance = bodyRadius + 4.0;
        return point.X >= grid.MinX + clearance && point.X <= grid.MaxX - clearance &&
            point.Z >= grid.MinZ + clearance && point.Z <= grid.MaxZ - clearance &&
            grid.IsMovementWalkable(point) &&
            grid.PointClearance(point, clearance) >= clearance;
    }

    private static void AssertNoCloserClientFloor(NavigationGrid grid, Position2 requested,
        Position2 actual, double bodyRadius, string champion)
    {
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        TestAssert.Equal(actual, Geometry.ClientWaypoints([actual], bounds)[0],
            $"{champion}: blocked placement is on the client waypoint lattice");
        var anchor = Geometry.ClientWaypoints([requested], bounds)[0];
        var distanceSquared = Math.Pow(actual.X - requested.X, 2) +
            Math.Pow(actual.Z - requested.Z, 2);
        var steps = (int)Math.Ceiling(Math.Sqrt(distanceSquared) / 2.0) + 2;
        for (var dx = -steps; dx <= steps; dx++)
        for (var dz = -steps; dz <= steps; dz++)
        {
            var candidate = anchor + new Position2(dx * 2.0, dz * 2.0);
            var candidateDistance = Math.Pow(candidate.X - requested.X, 2) +
                Math.Pow(candidate.Z - requested.Z, 2);
            if (candidateDistance > distanceSquared + 1e-9) continue;
            var precedes = candidateDistance < distanceSquared - 1e-9 ||
                (Math.Abs(candidateDistance - distanceSquared) <= 1e-9 &&
                 (candidate.X < actual.X || candidate.X == actual.X && candidate.Z < actual.Z));
            if (precedes)
                TestAssert.False(SummonFits(grid, candidate, bodyRadius),
                    $"{champion}: a closer or earlier tied client-grid floor exists at {candidate}");
        }
    }

    private static Entity WaitForSummon(Match match, string champion)
    {
        var owner = match.Hero(0);
        for (var step = 0; step < 150; step++)
        {
            match.Runtime.Tick(.01);
            var actor = match.Runtime.World.Entities.Values.FirstOrDefault(entity =>
                entity.OwnerNetId == owner.NetId && (champion switch
                {
                    "Annie" => entity.Kind == EntityKind.Pet && entity.Name == "AnnieTibbers",
                    "Shaco" => entity.Kind == EntityKind.ShacoBox,
                    "Yorick" => entity.Kind == EntityKind.YorickGhoul &&
                        entity.Name.Contains("Decayed", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                }));
            if (actor is not null) return actor;
        }
        throw new InvalidOperationException($"{champion}: native-format W/R cast did not spawn its actor");
    }

    private static void AssertSummonSpawnWire(Match match, Entity actor,
        int captureIndex = 0)
    {
        // Champion sweep ANN-02, 27 Sept 2026: Tibbers uses SpawnPet;
        // boxes and ghouls retain SpawnMinion. Both layouts share X/Y/Z offsets.
        var opcode = actor.Kind == EntityKind.Pet ? (byte)0x3C : (byte)0x80;
        var packet = match.Captures[captureIndex].Packets.Single(packet =>
            Opcode(packet) == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == actor.NetId);
        TestAssert.Near(actor.Position.X,
            BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[10..]), .001,
            "spawn packet X agrees with authoritative actor");
        TestAssert.Near(actor.Position.Z,
            BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[18..]), .001,
            "spawn packet Z agrees with authoritative actor");
    }

    private static void AssertCastAimWire(Match match, uint heroNetId, int slot,
        Position2 requested)
    {
        var cast = match.Captures[0].Packets.Select(packet => (Packet: packet, Fields: Decode(packet)))
            .Single(item => Opcode(item.Packet) == 0xBD &&
                item.Fields.GetProperty("caster_netid").GetUInt32() == heroNetId &&
                item.Fields.GetProperty("cast_info").GetProperty("slot").GetInt32() == slot);
        var aim = cast.Fields.GetProperty("cast_info").GetProperty("target_pos");
        TestAssert.Near(requested.X, aim[0].GetDouble(), .001,
            "cast answer retains the submitted target X");
        TestAssert.Near(requested.Z, aim[2].GetDouble(), .001,
            "cast answer retains the submitted target Z");
    }

    private static void AssertSummonEffectWire(Match match, string name, Position2 expected,
        int captureIndex = 0)
    {
        var hash = SpellPackets.BuffNameHash(name[..^5]);
        var group = match.Captures[captureIndex].Packets
            .Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode)
            .SelectMany(packet => Decode(packet).GetProperty("groups").EnumerateArray())
            .Single(group => group.GetProperty("effect_name_hash").GetUInt32() == hash);
        var effect = group.GetProperty("effects")[0];
        var packed = effect.GetProperty("position_compressed");
        var grid = match.Runtime.Navigation;
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var expectedPacked = FxPackets.CompressPosition(expected, bounds,
            (x, z) => grid.HeightAt(new Position2(x, z)));
        TestAssert.Equal(expectedPacked.X, packed[0].GetInt16(), $"{name}: FX X follows its owning center");
        TestAssert.Equal(expectedPacked.Z, packed[2].GetInt16(), $"{name}: FX Z follows its owning center");
    }
}
