using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 608: production movement through the helper (<c>MovementSystem.ExeHost.cs</c>). A helper that dies is restarted and given every
/// mirror again, with nothing lost; repeated failures switch the match to the fallback; without a usable EXE the match uses the fallback from
/// the start, and its units still plan and walk their orders.
/// </summary>
public static class ExeHostProductionChecks
{
    // Three lane minions far apart on Summoner's Rift's mid lane (no contact), each ordered 1500 units on.
    private static readonly (Position2 Start, Position2 Goal)[] Walks =
    [
        (new Position2(3000, 3000), new Position2(4060, 4060)),
        (new Position2(6000, 6000), new Position2(7060, 7060)),
        (new Position2(9000, 9000), new Position2(10060, 10060)),
    ];

    private sealed record Scene(MovementSystem Movement, Entity[] Units, List<(string Level, string Event, string Text)> Log);

    private static Scene Start(GameDataPaths paths)
    {
        var world = new WorldState();
        var movement = new MovementSystem(world, NavigationGrid.Load(GameDataPaths.Default));
        var log = new List<(string, string, string)>();
        movement.StartEngine(paths, GameMaps.Normal, (level, message, properties) =>
            log.Add((level, message, string.Join(", ", properties.Select(pair => $"{pair.Key}={pair.Value}")))));
        var units = Walks.Select((walk, i) => world.Register(new Entity((uint)(0x40000100 + i), EntityKind.Minion, UnitType.Minion, Team.Order,
            walk.Start) { Speed = 325.0, Radius = 48.0, Health = 1000.0, MaxHealth = 1000.0, Alive = true })).ToArray();
        for (var i = 0; i < units.Length; i++)
            TestAssert.True(movement.SetMoveOrder(units[i].NetId, Walks[i].Goal).Count > 1, $"unit {i} is given a route");
        return new Scene(movement, units, log);
    }

    private static void Frame(MovementSystem movement)
    {
        movement.AdvanceSimulationTime(MovementSystem.FixedTickSeconds);
        movement.TickNonHeroes();
    }

    [Check("movement.b608-exehost-restart-resync", Serial = true, TimeoutSeconds = 300)]
    public static void HelperRestartResync()
    {
        var paths = GameDataPaths.Default;
        var steady = Start(paths);
        var crashed = Start(paths);
        try
        {
            TestAssert.Equal(MovementEngine.Helper, steady.Movement.Engine, "the helper runs (AMBERLINE_EXEHOST, the stock EXE): " +
                string.Join("; ", steady.Log.Select(line => line.Event + " " + line.Text)));
            // Frames 0-39 (every unit is still walking): a helper killed before frame 10 is restarted and resynced, and the units move as if
            // nothing had happened, bit for bit.
            for (var frame = 0; frame < 40; frame++)
            {
                if (frame == 10) crashed.Movement.KillHelperForDiagnostics();
                Frame(steady.Movement);
                Frame(crashed.Movement);
                for (var i = 0; i < Walks.Length; i++)
                    TestAssert.True(steady.Units[i].Position == crashed.Units[i].Position,
                        FormattableString.Invariant($"frame {frame} unit {i}: {steady.Units[i].Position} without a crash, {crashed.Units[i].Position} with it"));
            }
            TestAssert.Equal(MovementEngine.Helper, crashed.Movement.Engine, "one failure restarts the helper");
            TestAssert.Equal(1, crashed.Movement.HelperRestarts, "restarts");
            TestAssert.True(crashed.Log.Any(line => line.Event == "exehost.failed") && crashed.Log.Any(line => line.Event == "exehost.restarted"),
                "the failure and the restart are logged");
            // Two more failures within the minute: the match falls back for the rest, and the units still walk to their goals.
            for (var frame = 40; frame < 120; frame++)
            {
                if (frame is 41 or 43) crashed.Movement.KillHelperForDiagnostics();
                Frame(crashed.Movement);
            }
            TestAssert.Equal(MovementEngine.Fallback, crashed.Movement.Engine, "three failures within a minute switch to the fallback");
            TestAssert.True(crashed.Log.Any(line => line.Event == "exehost.fallback"), "the fallback is logged");
            for (var i = 0; i < Walks.Length; i++)
            {
                var unit = crashed.Units[i];
                TestAssert.True(unit.Waypoints.Count == 0 && Distance(unit.Position, Walks[i].Goal) < 3.0,
                    FormattableString.Invariant($"unit {i} reached its goal {Walks[i].Goal} after the fallback: at {unit.Position}, {unit.Waypoints.Count} waypoints left"));
            }
        }
        finally
        {
            steady.Movement.StopEngine();
            crashed.Movement.StopEngine();
        }
    }

    [Check("movement.b608-fallback-without-exe", Serial = true, TimeoutSeconds = 120)]
    public static void FallbackWithoutExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-b608-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (label, exeBytes, reason) in new[]
                     {
                         ("no EXE", (byte[]?)null, "missing"),
                         ("another EXE", new byte[] { 0x4d, 0x5a, 0, 0 }, "SHA-256 differs"),
                     })
            {
                var exe = Path.Combine(root, "League of Legends.exe");
                if (exeBytes is null) File.Delete(exe);
                else File.WriteAllBytes(exe, exeBytes);
                var scene = Start(new GameDataPaths(root));
                TestAssert.Equal(MovementEngine.Fallback, scene.Movement.Engine, $"{label}: the match uses the fallback");
                var line = scene.Log.SingleOrDefault(entry => entry.Event == "exehost.fallback");
                TestAssert.True(line.Text?.Contains(reason) == true || line.Text?.Contains("exehost is not installed") == true,
                    $"{label}: one log line names why ({line.Text})");
                for (var frame = 0; frame < 80; frame++) Frame(scene.Movement);
                for (var i = 0; i < Walks.Length; i++)
                {
                    var unit = scene.Units[i];
                    TestAssert.True(unit.Waypoints.Count == 0 && Distance(unit.Position, Walks[i].Goal) < 3.0,
                        FormattableString.Invariant($"{label}: unit {i} walked its route to {Walks[i].Goal}: at {unit.Position}"));
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
