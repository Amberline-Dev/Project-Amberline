using System.Threading;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Network;

public static class MatchFaultChecks
{
    [Check("server.match-fault-contained")]
    public static void AFaultedMatchEndsAloneAndItsNeighbourKeepsRunning() => MatchFaultAsync().GetAwaiter().GetResult();

    [Check("server.platform-timer-contained")]
    public static void APlatformTimerFaultIsLoggedAndTheNextTickStillRuns() => PlatformTimerAsync().GetAwaiter().GetResult();

    private static async Task MatchFaultAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-match-fault-" + Guid.NewGuid().ToString("N"));
        var options = new ServerOptions("127.0.0.1", 0, 1, 8, ServerMatchMode.Solo, "Annie", "Orianna", 1.0, null, root, 32);
        using var logger = ServerLogger.Open(root, new StringWriter());
        var ticks = 0;
        using var stop = new CancellationTokenSource();
        // An ENet host must be used only by the thread that created it, so each match is created, run and disposed on its own thread.
        // The test seam (BeforeTick) throws on the faulted match's first tick; the healthy match ticks normally.
        var faultyRun = Task.Run(() =>
        {
            using var network = NewStartedMatch();
            var host = new ServerRuntimeHost(network, options, logger)
            {
                BeforeTick = _ => throw new InvalidOperationException("injected match fault"),
            };
            host.Run(stop.Token);
            return host.Faulted;
        });
        var healthyRun = Task.Run(() =>
        {
            using var network = NewStartedMatch();
            var host = new ServerRuntimeHost(network, options, logger)
            {
                BeforeTick = _ => Interlocked.Increment(ref ticks),
            };
            host.Run(stop.Token);
            return host.Faulted;
        });

        // The faulted match returns from Run by itself: no exception reaches the caller, so the process is not ended.
        TestAssert.True(await faultyRun.WaitAsync(TimeSpan.FromSeconds(30)), "the injected fault is recorded on the match that raised it");
        var ticksAtFault = Volatile.Read(ref ticks);
        await Task.Delay(500);
        // A neighbour that failed on its own surfaces its own exception here, not as a vague "stopped" result.
        if (healthyRun.IsFaulted) healthyRun.GetAwaiter().GetResult();
        TestAssert.False(healthyRun.IsCompleted, "the neighbouring match keeps running after the fault");
        TestAssert.True(Volatile.Read(ref ticks) > ticksAtFault, "the neighbouring match keeps ticking after the fault");
        stop.Cancel();
        TestAssert.False(await healthyRun.WaitAsync(TimeSpan.FromSeconds(30)), "the neighbouring match is not marked faulted");
    }

    private static readonly object CreateGate = new();

    private static MatchRuntimeNetwork NewStartedMatch()
    {
        // Both matches are created on their own threads; creation is serialised so the shared catalogs load once.
        lock (CreateGate)
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
            runtime.World.Clock.SetVirtual(0.0);
            TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null);
            runtime.Session.Spawn(1);
            TestAssert.True(runtime.Session.MarkReady(1));
            TestAssert.True(runtime.Session.Start());
            return new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true);
        }
    }

    private static async Task PlatformTimerAsync()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var timer = PlatformGames.RunTimerAsync(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("injected platform timer fault");
        }, TimeSpan.FromMilliseconds(10), stop.Token);
        await Task.Delay(300);
        TestAssert.False(timer.IsCompleted, "a faulted timer callback does not stop the platform timer");
        TestAssert.True(Volatile.Read(ref calls) > 2, "the platform timer runs again after the fault");
        stop.Cancel();
        await timer.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
