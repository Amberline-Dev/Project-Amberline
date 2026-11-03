using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static class AirMaestroLifecycleChecks
{
    [Check("server.air-window-restore-policy")]
    public static void MinimizesUnfocusedAirDuringOwnedGamesAndRestoresOnce()
    {
        foreach (var iconic in new[] { false, true })
        {
            var hidden = new AirWindowTransition();
            for (var sample = 0; sample < 2; sample++)
                TestAssert.Equal(AirWindowAction.None, hidden.Observe(99, ownedWindow: true, iconic: iconic,
                    visible: false, airFocused: false), "B5 AIR: stock tray hiding needs no show command, even first");
            TestAssert.Equal(AirWindowAction.None, hidden.Observe(null, ownedWindow: true, iconic: iconic,
                visible: false, airFocused: false), "stock AIR owns restoring its hidden tray window");
        }
        var window = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, window.Observe(100, ownedWindow: false, iconic: false,
            visible: false, airFocused: false), "wait for the AIR-owned window handle");
        TestAssert.Equal(AirWindowAction.Minimize, window.Observe(100, ownedWindow: true, iconic: false,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.Hide, window.Observe(100, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "29 Sept: AIR's deferred minimize re-showed its hidden window; hide it, not minimize");
        // playtest #20, 25 Sept 2026: a restore is respected only while AIR
        // has focus; the game-9 sequence below covers the re-minimize.
        TestAssert.Equal(AirWindowAction.None, window.Observe(100, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "manual restore during the same game is not undone while AIR has focus");
        TestAssert.Equal(AirWindowAction.None, window.Observe(null, ownedWindow: false, iconic: true,
            visible: false, airFocused: false), "wait for a verifiable AIR window at match end");
        TestAssert.Equal(AirWindowAction.Restore, window.Observe(null, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, window.Observe(null, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.Minimize, window.Observe(101, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "the next native match gets its own minimize, even while AIR has focus");
        TestAssert.Equal(AirWindowAction.None, window.Observe(null, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "stock AIR already restored the window");

        // Playtest #20 game 9: AIR minimized itself before the first sample,
        // then reappeared blank mid-match; the restore initiator is unknown.
        var game9 = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.Hide, game9.Observe(109, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "29 Sept: visible + iconic at the first sample is the taskbar entry; hide it");
        TestAssert.Equal(AirWindowAction.None, game9.Observe(109, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "restored AIR stays up while it has focus");
        TestAssert.Equal(AirWindowAction.Minimize, game9.Observe(109, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "focus back in the game re-minimizes AIR");
        TestAssert.Equal(AirWindowAction.None, game9.Observe(109, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "a queued minimize is not repeated before it lands");
        TestAssert.Equal(AirWindowAction.None, game9.Observe(109, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.Minimize, game9.Observe(109, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "every later reappearance is hidden again");
        TestAssert.Equal(AirWindowAction.None, game9.Observe(109, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, game9.Observe(109, ownedWindow: true, iconic: false,
            visible: false, airFocused: false), "a hidden (tray-only) window is left alone");
        TestAssert.Equal(AirWindowAction.None, game9.Observe(null, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "a visible AIR at match end needs no restore");
    }

    [Check("server.air-window-hide-policy")]
    public static void HidesAirOffTheTaskbarOnlyWhenItsOwnHideRacedAndLeavesUserOpenedWindowsAlone()
    {
        // The race: AIR's minimize completed on its already hidden window, leaving
        // visible + iconic (a taskbar entry) from the first sample on.
        var race = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.Hide, race.Observe(200, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "first sample visible + iconic hides the window");
        TestAssert.Equal(AirWindowAction.None, race.Observe(200, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "a queued hide is not repeated before it lands");
        TestAssert.Equal(AirWindowAction.None, race.Observe(200, ownedWindow: true, iconic: false,
            visible: false, airFocused: false), "hidden: nothing more to send");
        TestAssert.Equal(AirWindowAction.None, race.Observe(200, ownedWindow: true, iconic: false,
            visible: false, airFocused: false));
        TestAssert.Equal(AirWindowAction.Hide, race.Observe(200, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "a later re-shown taskbar entry is hidden again");
        TestAssert.Equal(AirWindowAction.None, race.Observe(200, ownedWindow: true, iconic: false,
            visible: false, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, race.Observe(null, ownedWindow: true, iconic: false,
            visible: false, airFocused: false), "hidden at game end: AIR restores itself, send nothing");
        TestAssert.Equal(AirWindowAction.None, race.Observe(null, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "AIR's own post-game restore is left alone");

        // The pre-hide lobby window is not a user-opened window: minimize it, then hide the race result.
        var lobby = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.Minimize, lobby.Observe(201, ownedWindow: true, iconic: false,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, lobby.Observe(201, ownedWindow: true, iconic: false,
            visible: false, airFocused: false));
        TestAssert.Equal(AirWindowAction.Hide, lobby.Observe(201, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "minimize completing on the hidden window is hidden again");
        TestAssert.Equal(AirWindowAction.Restore, lobby.Observe(null, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "still visible + iconic at game end: restore once");
        TestAssert.Equal(AirWindowAction.None, lobby.Observe(null, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));

        // A window the user opened (tray or taskbar) has AIR's visible flag set: never hide it behind
        // AIR's back, keep the minimize-once-focus-is-lost behaviour.
        var user = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: false,
            visible: false, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "user opened AIR: usable while focused");
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: true,
            visible: true, airFocused: true), "user-opened window that is iconic again is never hidden");
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "still never hidden");
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: false,
            visible: true, airFocused: true), "restored again while focused");
        TestAssert.Equal(AirWindowAction.Minimize, user.Observe(202, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "old behaviour: minimized once AIR loses focus");
        TestAssert.Equal(AirWindowAction.None, user.Observe(202, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "the minimize landed; a user-opened window is not hidden afterwards");
        TestAssert.Equal(AirWindowAction.Restore, user.Observe(null, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "visible + iconic at game end: restore once");

        // The next game starts fresh: the user-opened flag does not carry over.
        TestAssert.Equal(AirWindowAction.Hide, user.Observe(203, ownedWindow: true, iconic: true,
            visible: true, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, user.Observe(null, ownedWindow: true, iconic: false,
            visible: false, airFocused: false), "hidden at game end returns none");

        // A window that is not AIR-owned or not verifiable is never touched.
        var unowned = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, unowned.Observe(204, ownedWindow: false, iconic: true,
            visible: true, airFocused: false));
    }

    [Check("server.air-window-native-stop-policy")]
    public static void LeavesAirAloneOnceTheNativeGameReportedItsEnd()
    {
        // The native stop was forwarded while the game process is still alive, so the watcher still sees a
        // game. AIR restores its own window at this point (handleMaximizeEvent); the companion must not hide
        // or minimize it behind AIR's back, or AIR is left hidden and nothing restores it.
        var restoring = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.Minimize, restoring.Observe(300, ownedWindow: true, iconic: false,
            visible: true, airFocused: false), "the first visible sample minimizes, as before");
        TestAssert.Equal(AirWindowAction.None, restoring.Observe(300, ownedWindow: true, iconic: true,
            visible: true, airFocused: false, nativeStopped: true),
            "visible + iconic after the native stop is AIR's own restore, not a taskbar entry to hide");

        var restored = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, restored.Observe(301, ownedWindow: true, iconic: false,
            visible: false, airFocused: false));
        TestAssert.Equal(AirWindowAction.None, restored.Observe(301, ownedWindow: true, iconic: false,
            visible: true, airFocused: false, nativeStopped: true),
            "AIR restored itself at the native stop: no minimize while the process exits");

        // Without a native stop the game-start race hide is unchanged.
        var midGame = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.Hide, midGame.Observe(302, ownedWindow: true, iconic: true,
            visible: true, airFocused: false), "a live game without a native stop still hides the taskbar entry");
    }

    [Check("server.air-maestro-lifecycle")]
    public static void OwnsOnlySyntheticNativeProcessesAndDeduplicatesLifecycleMessages() => RunAsync().GetAwaiter().GetResult();

    [Check("server.air-platform-startup-functional")]
    public static void CmdWrapperActuallyRedirectsAPathWithSpacesToDurableLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "league air platform " + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "synthetic platform.cmd");
        var data = Path.Combine(root, "platform data");
        var log = Path.Combine(root, "logs", "platform output.log");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            File.WriteAllText(executable, "@echo off\r\necho AIR-STAGE-MARKER\r\nexit /b 0\r\n");
            using var process = Process.Start(Amberline.AirCompanion.Program.CreatePlatformStartInfo(root, executable, data, log)) ??
                throw new InvalidOperationException("synthetic platform process did not start");
            try
            {
                TestAssert.True(process.WaitForExit(5000), "synthetic platform wrapper must exit within the bound");
                TestAssert.Equal(0, process.ExitCode, "synthetic platform wrapper must preserve the command exit code");
                TestAssert.True(File.Exists(log), "synthetic platform output log must be created");
                TestAssert.Equal("AIR-STAGE-MARKER", File.ReadAllLines(log).Single());
            }
            finally { KillSynthetic(process); }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task RunAsync()
    {
        var launched = new List<Process>();
        var connected = 0;
        var acceptedPeers = 0;
        var peersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new MaestroServer(0, _ =>
        {
            var process = StartSyntheticNativeProcess();
            launched.Add(process);
            return process;
        });
        server.NativeGameConnected += () => Interlocked.Increment(ref connected);
        server.PeerConnected += () =>
        {
            if (Interlocked.Increment(ref acceptedPeers) == 3) peersReady.TrySetResult();
        };
        using var controller = new TcpClient();
        using var observer = new TcpClient();
        using var native = new TcpClient();
        try
        {
            await server.StartAsync();
            await controller.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
            await observer.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
            await native.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
            await peersReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var controllerStream = controller.GetStream();
            var observerStream = observer.GetStream();
            var nativeStream = native.GetStream();

            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientCreate,
                "127.0.0.1 5219 Abcd1234Abcd1234 1000001");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientLaunched);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientLaunched);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "the first Create emits exactly one launch notification");
            TestAssert.Equal(1, launched.Count, "the first Create launches one synthetic process");
            TestAssert.Equal((int?)launched[0].Id, server.OwnedGamePid,
                "window diagnostics must identify only the currently owned native process");
            TestAssert.Equal(0, Volatile.Read(ref connected), "Create does not fabricate GameConnected");

            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientLaunched);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "the native launch acknowledgement is consumed by the synthetic launch");
            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientLaunched);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "a duplicate native launch acknowledgement cannot restart the AIR lifecycle");
            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientConnectedToServer, "native");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientConnectedToServer);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientConnectedToServer);
            TestAssert.Equal(1, Volatile.Read(ref connected), "only the actual type-10 frame raises GameConnected");
            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientConnectedToServer, "duplicate-native");
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "a repeated connected frame cannot duplicate the current match lifecycle");
            TestAssert.Equal(1, Volatile.Read(ref connected), "repeated connected frames do not re-raise GameConnected");

            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.ChatMessageFromGame, "native-chat");
            var nativeChat = await ReadAsync(controllerStream);
            AssertMessage(nativeChat, MaestroMessageType.ChatMessageFromGame);
            TestAssert.Equal("native-chat", nativeChat.PayloadText);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.ChatMessageFromGame);

            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientStopped);
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientStopped);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientStopped);
            var stopped = server.OwnedGameState;
            TestAssert.True(stopped.Pid == launched[0].Id && stopped.TerminalNotified,
                "the window watcher sees the native stop while the process is still alive");
            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientCrashed);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "a duplicate terminal frame is suppressed");
            KillSynthetic(launched[0]);
            WaitFor(() => !server.HasOwnedGame, "the first owned process is released after exit");
            TestAssert.True(server.OwnedGamePid is null, "a finished game has no diagnostic native PID");

            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientCreate,
                "127.0.0.1 5219 Abcd1234Abcd1234 1000002");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientLaunched);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientLaunched);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "the second Create emits exactly one launch notification");
            TestAssert.Equal(2, launched.Count, "a second Create works after the first process exits");
            TestAssert.Equal((int?)launched[1].Id, server.OwnedGamePid,
                "window diagnostics follow the second native process generation");
            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientStopped);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "the previous native socket cannot terminate the second lifecycle");

            using var nativeSecond = new TcpClient();
            await nativeSecond.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
            await MaestroProtocol.WriteAsync(nativeSecond.GetStream(), MaestroMessageType.GameClientConnectedToServer, "second-native");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientConnectedToServer);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientConnectedToServer);
            TestAssert.Equal(2, Volatile.Read(ref connected), "the second native socket claims the second cycle");
            await MaestroProtocol.WriteAsync(nativeStream, MaestroMessageType.GameClientStopped);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "the previous native socket cannot terminate a connected second lifecycle");
            await MaestroProtocol.WriteAsync(nativeSecond.GetStream(), MaestroMessageType.GameClientCrashed);
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientCrashed);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientCrashed);

            KillSynthetic(launched[1]);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "owned process exit cannot duplicate a connected crash terminal");
            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientStopped);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "normal terminal input cannot duplicate an emitted crash terminal");
            WaitFor(() => !server.HasOwnedGame, "the crashed process is released");

            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientCreate,
                "127.0.0.1 5219 Abcd1234Abcd1234 1000003");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientLaunched);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientLaunched);
            TestAssert.Equal(3, launched.Count, "the server can own a fresh process before shutdown");

            using var nativeThird = new TcpClient();
            await nativeThird.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
            await MaestroProtocol.WriteAsync(nativeThird.GetStream(), MaestroMessageType.GameClientCrashed);
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientCrashed);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientCrashed);
            KillSynthetic(launched[2]);
            TestAssert.False(TryRead(observerStream, TimeSpan.FromMilliseconds(250)),
                "owned process exit cannot duplicate a pre-connect crash terminal");
            WaitFor(() => !server.HasOwnedGame, "the pre-connect crash releases the third process");

            await MaestroProtocol.WriteAsync(controllerStream, MaestroMessageType.GameClientCreate,
                "127.0.0.1 5219 Abcd1234Abcd1234 1000004");
            AssertMessage(await ReadAsync(controllerStream), MaestroMessageType.GameClientLaunched);
            AssertMessage(await ReadAsync(observerStream), MaestroMessageType.GameClientLaunched);
            TestAssert.Equal(4, launched.Count, "the server can own a fresh process before shutdown");

            await server.StopAsync(stopOwnedGame: false);
            TestAssert.False(launched[3].HasExited, "StopAsync(false) leaves the owned substitute alive");
            KillSynthetic(launched[3]);
            WaitFor(() => !server.HasOwnedGame, "explicit cleanup releases the substitute after StopAsync(false)");
            DisposeSynthetic(launched[3]);
        }
        finally
        {
            try { await server.DisposeAsync(); }
            finally
            {
                foreach (var process in launched)
                {
                    KillSynthetic(process);
                    DisposeSynthetic(process);
                }
            }
        }
    }

    private static Process StartSyntheticNativeProcess()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        return Process.Start(start) ?? throw new InvalidOperationException("synthetic native process did not start");
    }

    private static async Task<MaestroMessage> ReadAsync(NetworkStream stream) =>
        await (MaestroProtocol.ReadAsync(stream).AsTask().WaitAsync(TimeSpan.FromSeconds(2))) ??
        throw new InvalidDataException("Maestro peer closed before the expected message.");

    private static void AssertMessage(MaestroMessage message, MaestroMessageType expected) =>
        TestAssert.Equal((int)expected, message.Type, $"expected Maestro {expected}");

    private static bool TryRead(NetworkStream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return MaestroProtocol.ReadAsync(stream, cancellation.Token).AsTask().GetAwaiter().GetResult() is not null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
    }

    private static void WaitFor(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && !condition()) Thread.Sleep(20);
        TestAssert.True(condition(), description);
    }

    private static void KillSynthetic(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(2000); }
        catch (InvalidOperationException) { }
    }

    private static void DisposeSynthetic(Process process)
    {
        try { process.Dispose(); }
        catch (InvalidOperationException) { }
    }
}
