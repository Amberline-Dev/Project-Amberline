using Amberline.Core;
using Amberline.Server;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 222, item 7: the shipped server's script clock is the wall clock (retail's <c>os.time</c>), so LevelScript seeds Lua's
/// <c>math.random</c> with the time. The match seed stays only the labelled test seam of a seeded tools or check match.
/// The clock is observed through the match's Lua stream: LevelScript's <c>math.randomseed(os.time())</c> sets the CRT state.
/// </summary>
public static class ProductionScriptClockChecks
{
    [Check("server.production-lua-seed-is-the-wall-clock", Serial = true)]
    public static void ProductionLuaSeedIsTheWallClock()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            using var logger = ServerLogger.Open(root, new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(options, logger);
            var seed = (network.Runtime.Dependencies.Random as SeededRandomSource
                ?? throw new InvalidOperationException("the shipped match draws from a seeded source")).Seed;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var state = network.Runtime.World.LuaRandom.HoldRand;
            TestAssert.True(state != (uint)seed,
                $"the shipped match must not seed Lua with its match seed {seed} (CRT state {state})");
            TestAssert.True(Math.Abs((long)state - now) <= 120,
                $"the shipped match seeds Lua with the wall clock (CRT state {state}, wall clock {now})");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
