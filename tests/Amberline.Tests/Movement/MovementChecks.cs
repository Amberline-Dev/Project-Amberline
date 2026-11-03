using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class MovementChecks
{
    [Check("movement.native-terminal-arrival")]
    public static void NativeTerminalArrival()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var type in new[] { UnitType.Hero, UnitType.Minion })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var end = new Position2(7010, 7000);
            var mover = new Entity(1, type == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
                type, team, new Position2(7000, 7000))
                { Speed = 325, Radius = 35, Waypoints = [end] };
            var blocker = new Entity(2, EntityKind.Minion, UnitType.Minion, team,
                new Position2(7030, 7000)) { Speed = 0, Radius = 35 };
            world.Register(mover);
            world.Register(blocker);
            var movement = new MovementSystem(world, grid);
            movement.Tick(0.1);
            // The stock client writes the walkable endpoint immediately; its
            // later commit gate cannot steer that arrival away.
            TestAssert.Equal(end, mover.Position, "terminal arrival must survive nearby-body collision");
            TestAssert.Equal(0, mover.Waypoints.Count);
            movement.Tick(0.1);
            TestAssert.Equal(end, mover.Position, "arrived actor stays at its endpoint");
            TestAssert.Equal(new Position2(7030, 7000), blocker.Position, "arrival never pushes another actor");
        }
    }

    [Check("movement.first-collision-announced-landing")]
    public static void FirstCollisionLandingMatchesForcedMove()
    {
        var grid = NavigationGrid.LoadDefault();
        Verify(new Position2(7000, 7000), new Position2(7100, 7000), expectTruncation: false);

        // This is the existing captured wall crossing used by the kick
        // regressions.  The radial 600-unit endpoint crosses terrain, so the
        // FIRST_COLLISION_HIT projection must announce the last safe sample.
        var wallOrigin = new Position2(6913, 6635);
        var wallRequested = new Position2(6913, 6035);
        TestAssert.True(grid.IsWalkable(wallOrigin), "wall case starts on walkable terrain");
        TestAssert.False(grid.IsWalkable(new Position2(6913, 6435)),
            "wall case contains the captured blocking cell");
        Verify(wallOrigin, wallRequested, expectTruncation: true);

        void Verify(Position2 origin, Position2 requested, bool expectTruncation)
        {
            foreach (var cadence in new[] { 0.5, 0.1, 0.05, 0.016 })
            {
                var world = new WorldState();
                var mover = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, origin)
                {
                    Speed = 1200.0,
                    Radius = 35.0,
                };
                world.Register(mover);
                var movement = new MovementSystem(world, grid);
                var landing = movement.FirstCollisionLanding(origin, requested, 1200.0, 0.5);
                if (expectTruncation)
                {
                    TestAssert.True(landing != requested,
                        "a wall crossing is truncated before the announced endpoint");
                    TestAssert.True(grid.IsWalkable(landing),
                        "the announced wall landing is walkable");
                }
                else TestAssert.Equal(movement.ClientAnchor(requested), landing,
                    "clear path retains its quantized endpoint");

                var move = new ForcedMoveState(landing, 1200.0, 0.5, 2, "SweepingBlow",
                    firstCollision: true);
                TestAssert.True(movement.StartForcedMove(mover.NetId, move));
                movement.Tick(0.0);
                TestAssert.False(move.Completed,
                    "a zero-duration FIRST_COLLISION step does not complete an active move");
                TestAssert.Equal(origin, mover.Position,
                    "a zero-duration FIRST_COLLISION step does not move the actor");
                var remaining = 0.5;
                while (remaining > 1e-12)
                {
                    var dt = Math.Min(cadence, remaining);
                    movement.Tick(dt);
                    remaining -= dt;
                }
                TestAssert.True(move.Completed && move.Succeeded,
                    "forced move completes successfully at the projected landing");
                TestAssert.Equal(landing, mover.Position,
                    "authoritative FIRST_COLLISION_HIT stop equals the announced landing");
            }
        }
    }

    [Check("movement-settings")]
    public static void SettingsAndTypedState()
    {
        var collision = MovementCollisionSettings.Default;
        TestAssert.Equal(45, collision.CanCollideRepathLimit);
        TestAssert.Equal(15, collision.CanCollideRepathLimitFlagged);
        TestAssert.Near(20.0, collision.SkinHighCollider, 1e-12);
        TestAssert.Near(0.1, collision.ExtraSeparationSpeed, 1e-12);

        var entity = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, Position2.Zero)
        {
            Speed = 325.0,
            Radius = 35.7437,
        };
        entity.Buffs.Add(new BuffState("snare", type: BuffType.Snare));
        TestAssert.True(entity.Buffs[0].Type == BuffType.Snare);
        var state = new ForcedMoveState(new Position2(10.0, 20.0), 100.0, 0.4, 2, "fixture");
        entity.ForcedMove = state;
        TestAssert.Equal(state, entity.ForcedMove);
        TestAssert.Throws<ArgumentOutOfRangeException>(() =>
            new ForcedMoveState(Position2.Zero, double.NaN, 1.0, 0, "bad"));
    }

    private static void RunScenario(JsonElement scenario, NavigationGrid grid)
    {
        var clock = new GameClock();
        clock.SetVirtual(0.0);
        var world = new WorldState(clock);
        var movement = new MovementSystem(world, grid);
        var entities = new Dictionary<uint, Entity>();

        foreach (var unit in scenario.GetProperty("units").EnumerateArray())
        {
            var netId = unit.GetProperty("netid").GetUInt32();
            var kind = EntityKindExtensions.FromSourceType(unit.GetProperty("etype").GetString());
            var entity = new Entity(
                netId, kind,
                (UnitType)unit.GetProperty("unit_type").GetInt32(),
                (Team)unit.GetProperty("team").GetInt32(),
                Point(unit.GetProperty("pos")))
            {
                Speed = unit.GetProperty("speed").GetDouble(),
                Radius = unit.GetProperty("radius").GetDouble(),
                Health = Optional(unit, "hp", 1000.0),
                MaxHealth = Optional(unit, "max_hp", 1000.0),
                Alive = Optional(unit, "alive", true),
                PathingFlag290 = Optional(unit, "pathing_flag_290", false),
                AiState = Optional(unit, "ai_state", "march"),
            };
            if (unit.TryGetProperty("buffs", out var buffs) && buffs.ValueKind == JsonValueKind.Array)
            {
                foreach (var buff in buffs.EnumerateArray())
                {
                    var payload = new BuffPayload();
                    if (buff.TryGetProperty("payload", out var rawPayload) &&
                        rawPayload.ValueKind == JsonValueKind.Object)
                    {
                        if (rawPayload.TryGetProperty("ghosted", out var ghosted))
                            payload.Ghosted = ghosted.GetBoolean();
                        if (rawPayload.TryGetProperty("movement_disabled", out var movementDisabled))
                            payload.MovementDisabled = movementDisabled.GetBoolean();
                        if (rawPayload.TryGetProperty("action_disabled", out var actionDisabled))
                            payload.ActionDisabled = actionDisabled.GetBoolean();
                    }
                    entity.Buffs.Add(new BuffState(
                        "fixture", payload,
                        (BuffType)buff.GetProperty("type").GetInt32()));
                }
            }
            world.Register(entity);
            entities.Add(netId, entity);
        }

        if (scenario.TryGetProperty("orders", out var orders))
        {
            foreach (var order in orders.EnumerateArray())
                CompareRoute(scenario, order[0].GetUInt32(),
                    movement.SetMoveOrder(order[0].GetUInt32(), Point(order[1])));
        }

        if (scenario.TryGetProperty("forced", out var forced) && forced.ValueKind == JsonValueKind.Object)
        {
            var netId = forced.GetProperty("netid").GetUInt32();
            var entity = entities[netId];
            var state = new ForcedMoveState(
                Point(forced.GetProperty("destination")),
                forced.GetProperty("speed").GetDouble(),
                forced.GetProperty("duration").GetDouble(),
                forced.GetProperty("source_netid").GetUInt32(),
                forced.GetProperty("effect").GetString() ?? "fixture",
                Optional(forced, "first_collision", false));
            movement.StartForcedMove(netId, state, Optional(forced, "cancel_order", false));
            _ = entity;
        }

        var initial = scenario.GetProperty("initial");
        for (var index = 0; index < initial.GetArrayLength(); index++)
            CompareSnapshot(initial[index], entities[initial[index].GetProperty("netid").GetUInt32()],
                $"{scenario.GetProperty("name").GetString()} initial[{index}]");

        var clearBuffsAt = scenario.TryGetProperty("clearBuffsAt", out var clear)
            ? clear.GetInt32()
            : -1;
        var frames = scenario.GetProperty("frames");
        for (var frameIndex = 0; frameIndex < frames.GetArrayLength(); frameIndex++)
        {
            if (frameIndex == clearBuffsAt)
            {
                foreach (var entity in entities.Values) entity.Buffs.Clear();
            }
            clock.Advance(scenario.GetProperty("dt").GetDouble());
            movement.Tick(scenario.GetProperty("dt").GetDouble());
            var expectedFrame = frames[frameIndex];
            for (var index = 0; index < expectedFrame.GetArrayLength(); index++)
            {
                var expected = expectedFrame[index];
                var entity = entities[expected.GetProperty("netid").GetUInt32()];
                CompareSnapshot(expected, entity,
                    $"{scenario.GetProperty("name").GetString()} frame {frameIndex} unit {entity.NetId}");
            }
        }
    }



    private static void CompareRoute(JsonElement scenario, uint netId, IReadOnlyList<Position2> actual)
    {
        var routes = scenario.GetProperty("issuedRoutes");
        var routeIndex = 0;
        foreach (var order in scenario.GetProperty("orders").EnumerateArray())
        {
            if (order[0].GetUInt32() != netId) routeIndex++;
            else break;
        }
        var key = $"{scenario.GetProperty("name").GetString()} route {netId}";
        ComparePoints(routes[routeIndex], actual, key);
    }

    private static void CompareSnapshot(JsonElement expected, Entity actual, string label)
    {
        if (FixtureRecorder.Enabled)
        {
            object? PointValue(Position2? point) => point is { } p ? new[] { p.X, p.Z } : null;
            var forced = actual.ForcedMove;
            var snapshot = new Dictionary<string, object?>
            {
                ["netid"] = actual.NetId, ["pos"] = PointValue(actual.Position),
                ["waypoints"] = actual.Waypoints.Select(p => new[] { p.X, p.Z }).ToArray(),
                ["target"] = PointValue(actual.Target), ["sep_accum"] = PointValue(actual.SeparationAccumulation),
                ["stuck_elapsed"] = actual.StuckElapsed, ["repath_attempts"] = actual.RepathAttempts,
                ["repath_count"] = actual.RepathCount, ["facing"] = PointValue(actual.Facing),
                ["forced_move_grace"] = actual.ForcedMoveGrace,
                ["forced_move"] = forced is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>
                {
                    ["destination"] = PointValue(forced.Destination), ["speed"] = forced.Speed,
                    ["remaining"] = forced.Remaining, ["source_netid"] = forced.SourceNetId,
                    ["effect"] = forced.Effect, ["first_collision"] = forced.FirstCollision,
                    ["completed"] = forced.Completed,
                },
            };
            if (forced is not null && expected.GetProperty("forced_move").TryGetProperty("succeeded", out _))
                ((Dictionary<string, object?>)snapshot["forced_move"]!)["succeeded"] = forced.Succeeded;
            CorpusSnapshot.Set(expected, snapshot);
            return;
        }
        var expectedPosition = Point(expected.GetProperty("pos"));
        TestAssert.Near(expectedPosition.X, actual.Position.X, 1e-7,
            $"{label} position X expected {expectedPosition.X:R} actual {actual.Position.X:R}");
        TestAssert.Near(expectedPosition.Z, actual.Position.Z, 1e-7,
            $"{label} position Z expected {expectedPosition.Z:R} actual {actual.Position.Z:R}");
        var waypoints = expected.GetProperty("waypoints");
        TestAssert.Equal(waypoints.GetArrayLength(), actual.Waypoints.Count, $"{label} waypoint count");
        for (var index = 0; index < actual.Waypoints.Count; index++)
        {
            var point = Point(waypoints[index]);
            TestAssert.Near(point.X, actual.Waypoints[index].X, 1e-7, $"{label} waypoint {index} X");
            TestAssert.Near(point.Z, actual.Waypoints[index].Z, 1e-7, $"{label} waypoint {index} Z");
        }
        var target = expected.GetProperty("target");
        if (target.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual.Target is null, $"{label} target should be null");
        else
        {
            TestAssert.True(actual.Target is not null, $"{label} target missing");
            var targetPoint = Point(target);
            TestAssert.Near(targetPoint.X, actual.Target!.Value.X, 1e-7, $"{label} target X");
            TestAssert.Near(targetPoint.Z, actual.Target!.Value.Z, 1e-7, $"{label} target Z");
        }
        var separation = Point(expected.GetProperty("sep_accum"));
        TestAssert.Near(separation.X, actual.SeparationAccumulation.X, 1e-7, $"{label} separation X");
        TestAssert.Near(separation.Z, actual.SeparationAccumulation.Z, 1e-7, $"{label} separation Z");
        TestAssert.Near(expected.GetProperty("stuck_elapsed").GetDouble(), actual.StuckElapsed, 1e-7, $"{label} stuck");
        TestAssert.Equal(expected.GetProperty("repath_attempts").GetInt32(), actual.RepathAttempts, $"{label} attempts");
        TestAssert.Equal(expected.GetProperty("repath_count").GetInt32(), actual.RepathCount, $"{label} repath count");
        var facing = Point(expected.GetProperty("facing"));
        TestAssert.Near(facing.X, actual.Facing.X, 1e-7, $"{label} facing X");
        TestAssert.Near(facing.Z, actual.Facing.Z, 1e-7, $"{label} facing Z");
        var expectedForced = expected.GetProperty("forced_move");
        if (expectedForced.ValueKind == JsonValueKind.Object && expectedForced.EnumerateObject().Any())
        {
            TestAssert.True(actual.ForcedMove is not null, $"{label} forced move missing");
            var forced = actual.ForcedMove!;
            var destination = Point(expectedForced.GetProperty("destination"));
            TestAssert.Near(destination.X, forced.Destination.X, 1e-7, $"{label} forced destination X");
            TestAssert.Near(destination.Z, forced.Destination.Z, 1e-7, $"{label} forced destination Z");
            TestAssert.Near(expectedForced.GetProperty("speed").GetDouble(), forced.Speed, 1e-7, $"{label} forced speed");
            TestAssert.Near(expectedForced.GetProperty("remaining").GetDouble(), forced.Remaining, 1e-7, $"{label} forced remaining");
            TestAssert.Equal(expectedForced.GetProperty("source_netid").GetUInt32(), forced.SourceNetId,
                $"{label} forced source");
            TestAssert.Equal(expectedForced.GetProperty("effect").GetString(), forced.Effect,
                $"{label} forced effect");
            TestAssert.Equal(expectedForced.GetProperty("first_collision").GetBoolean(), forced.FirstCollision,
                $"{label} forced collision mode");
            TestAssert.Equal(expectedForced.GetProperty("completed").GetBoolean(), forced.Completed, $"{label} forced completed");
            if (expectedForced.TryGetProperty("succeeded", out var succeeded))
                TestAssert.Equal(succeeded.GetBoolean(), forced.Succeeded, $"{label} forced succeeded");
        }
        else
        {
            TestAssert.True(actual.ForcedMove is null, $"{label} unexpected forced move");
        }
        TestAssert.Near(expected.GetProperty("forced_move_grace").GetDouble(), actual.ForcedMoveGrace, 1e-7,
            $"{label} forced grace");
    }

    private static Position2 Point(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string label)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.Select(p => new[] { p.X, p.Z }).ToArray());
            return;
        }
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, $"{label} count");
        for (var index = 0; index < actual.Count; index++)
        {
            var point = Point(expected[index]);
            TestAssert.Near(point.X, actual[index].X, 1e-7, $"{label}[{index}] X");
            TestAssert.Near(point.Z, actual[index].Z, 1e-7, $"{label}[{index}] Z");
        }
    }

    private static T Optional<T>(JsonElement element, string name, T fallback) =>
        element.TryGetProperty(name, out var value) ?
            (typeof(T) == typeof(bool) ? (T)(object)value.GetBoolean() :
             typeof(T) == typeof(string) ? (T)(object)(value.GetString() ?? string.Empty) :
             (T)(object)value.GetDouble()) : fallback;
}
