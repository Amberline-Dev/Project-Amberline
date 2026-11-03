using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

public static class FlashWallPlacementChecks
{
    [Check("spells-flash-map1-base-wall-nearest-passable")]
    public static void BothBaseWallsProjectAroundTheClampedCursor()
    {
        var cases = new[]
        {
            ("Order near-side interior", new Position2(946, 1115),
                new Position2(1010, 1265), 1000.0, false),
            ("Order outward", new Position2(946, 1115),
                new Position2(1310, 1265), 1320.0, true),
            ("Order inward", new Position2(1446, 1215),
                new Position2(980, 1265), 1000.0, false),
            ("Chaos near-side interior", new Position2(12496, 12715),
                new Position2(12600, 12865), 12600.0, false),
            ("Chaos outward", new Position2(12496, 12715),
                new Position2(12920, 12865), 12970.0, true),
            ("Chaos inward", new Position2(13046, 12865),
                new Position2(12600, 12865), 12600.0, false),
        };
        foreach (var (name, origin, cursor, side, toEast) in cases)
        {
            using var runtime = CreateRuntime();
            var grid = runtime.Navigation;
            TestAssert.True(grid.IsWalkable(origin), name + " starting floor");
            TestAssert.False(grid.IsWalkable(cursor), name + " blocked cursor");
            var hero = Hero(runtime, origin);
            TestAssert.True(Cast(runtime, hero, cursor), name + " accepted Flash");
            TestAssert.True(grid.IsWalkable(hero.Position), name + " passable landing");
            TestAssert.True(toEast ? hero.Position.X > side : hero.Position.X < side,
                name + " landed on the cursor's near passable side of the wall");
            TestAssert.True(hero.SummonerCooldowns[0] > 0.0,
                name + " starts cooldown after the valid landing");
        }
    }

    [Check("spells-flash-boundary-short-and-atomic-rejection")]
    public static void BoundaryAndShortCastKeepTeleportAndCooldownContracts()
    {
        using (var runtime = CreateRuntime())
        {
            var hero = Hero(runtime, new Position2(26, 264));
            TestAssert.True(Cast(runtime, hero, new Position2(-10000, 264)),
                "off-map cursor can resolve to a Map1 landing");
            TestAssert.True(runtime.Navigation.IsWalkable(hero.Position) &&
                runtime.Navigation.IsInBounds(runtime.Navigation.WorldToCell(hero.Position)),
                "boundary correction remains inside passable Map1 terrain");
        }

        using (var runtime = CreateRuntime())
        {
            var hero = Hero(runtime, new Position2(7001, 7001));
            var origin = hero.Position;
            TestAssert.True(Cast(runtime, hero, origin),
                "open zero-distance Flash keeps the native short-teleport shim");
            TestAssert.Equal(origin, hero.Position,
                "open zero-distance Flash retains its client-grid position");
            TestAssert.Equal((byte)2, hero.TeleportId,
                "short Flash sends a staging and destination teleport");
            TestAssert.True(hero.SummonerCooldowns[0] > 0.0,
                "accepted short Flash starts cooldown");
        }

        using (var runtime = CreateRuntime())
        {
            var hero = Hero(runtime, new Position2(7001, 7001));
            runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Structure),
                EntityKind.Nexus, UnitType.Structure, Team.Chaos, hero.Position)
            {
                Alive = true, Created = true, Radius = 10000.0,
            });
            var position = hero.Position;
            var id = hero.TeleportId;
            TestAssert.False(Cast(runtime, hero, new Position2(7101, 7001)),
                "Flash rejects when no passable landing clears an occupied area");
            TestAssert.Equal(position, hero.Position,
                "rejected Flash leaves position unchanged");
            TestAssert.Equal(id, hero.TeleportId,
                "rejected Flash does not consume a teleport ID");
            TestAssert.Equal(0.0, hero.SummonerCooldowns[0],
                "rejected Flash does not start cooldown");
        }
    }

    [Check("spells-flash-map1-turret-footprint-nearest-passable")]
    public static void FlashIntoTurretFootprintLandsOnMovementFloor()
    {
        // Playtest B2 BJ4, 26 Sept 2026: Blitzcrank's logged Flashes landed
        // 145-147u from Turret_T1_C_02, inside the client's 140u turret stamp
        // cells, and could not walk out. The stamp now takes the stock
        // nearest-passable search like a wall.
        foreach (var (origin, cursor) in new[]
        {
            (new Position2(2286, 1479), new Position2(1911, 1555)),
            (new Position2(1635, 1129), new Position2(1779, 1445)),
        })
        {
            using var runtime = CreateRuntime();
            var grid = runtime.Navigation;
            TestAssert.True(grid.IsWalkable(cursor) && !grid.IsMovementWalkable(cursor),
                $"{cursor} is raw floor inside the turret stamp");
            var hero = Hero(runtime, origin, radius: 56.44);
            TestAssert.True(Cast(runtime, hero, cursor), $"{origin} accepted Flash");
            TestAssert.True(grid.IsMovementWalkable(hero.Position),
                $"{origin} Flash landed on the movement floor: {hero.Position}");
            TestAssert.True(runtime.Movement.SetMoveOrder(hero.NetId, origin).Count > 1,
                $"{origin} a following move order routes out of {hero.Position}");
        }
    }

    private static MatchRuntime CreateRuntime() =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            solo: true);

    private static Entity Hero(MatchRuntime runtime, Position2 position, double radius = 35.0)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, position)
        {
            Name = "FlashSeat", ChampionName = "Annie", Alive = true,
            Created = true, Visible = true, Health = 1000.0, MaxHealth = 1000.0,
            Mana = 1000.0, MaxMana = 1000.0, Radius = radius, Level = 18,
            Speed = 325.0, SummonerSpells = ["SummonerFlash", "SummonerDot"],
        };
        runtime.World.Register(hero);
        return hero;
    }

    private static bool Cast(MatchRuntime runtime, Entity hero, Position2 cursor)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        payload[5] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6), (float)cursor.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14), (float)cursor.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18), (float)cursor.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(26), (float)cursor.Z);
        return runtime.Spells.HandleCastRequest(hero, payload, runtime.World.Clock.Now());
    }
}
