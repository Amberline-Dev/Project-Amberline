using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Amberline.AirCompanion;

/// <summary>Keep the verified AIR window out of the way (hidden or minimized) during each owned native game.</summary>
internal static class AirWindowDiagnostics
{
    private const int MaxChangesPerGame = 96;
    private const int SwHide = 0;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    // Keep sampling briefly after the game ends so AIR's own post-game restore is logged.
    private const long PostGameSampleMilliseconds = 3000;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private readonly record struct Sample(int GamePid, string AirState, bool Visible, bool Iconic, uint ForegroundPid,
        bool PostGame);

    /// <summary>Milliseconds since the last keyboard or mouse input (session-wide), or -1 when unavailable.
    /// Shows whether a window restore in the log was a user click.</summary>
    private static long IdleMilliseconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : -1;
    }

    /// <summary>Restores the hidden main window of each given process and returns how many restore requests were queued.
    /// NOT RETAIL: an operator-side recovery added by the AIR platform; the stock client has no launcher that restores a
    /// hidden copy. An orphaned copy's window is hidden, so Process.MainWindowHandle cannot see it. Reconstruction: the
    /// AIR main window is taken to be the unowned, titled top-level window of that process (not verifiable offline).
    /// ponytail: the titled-window match is the ceiling; an untitled AIR main window is never restored and the start
    /// refuses as before. Upgrade: match the AIR main window's class or caption once a native run shows it.</summary>
    internal static int RestoreHiddenWindows(IReadOnlyCollection<int> processIds)
    {
        if (!OperatingSystem.IsWindows() || processIds.Count == 0) return 0;
        var queued = 0;
        EnumWindows((window, lParam) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains((int)processId) || GetWindow(window, GwOwner) != 0 || IsWindowVisible(window) ||
                GetWindowTextLength(window) == 0) return true;
            if (!ShowWindowAsync(window, SwRestore)) return true;
            queued++;
            Console.WriteLine($"air.window-restorehidden t={DateTimeOffset.Now:O} air_pid={processId} request_queued=True");
            return true;
        }, 0);
        return queued;
    }

    public static async Task WatchAsync(Process air, MaestroServer maestro, CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        nint airWindow = 0;
        Sample? previous = null;
        var transition = new AirWindowTransition();
        var changes = 0;
        int? lastGamePid = null;
        long postGameEnd = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (air.HasExited) return;
                    air.Refresh();
                    // MainWindowHandle can name another AIR window while the
                    // main one is minimized; keep the cached handle while it exists.
                    if ((airWindow == 0 || !IsWindow(airWindow)) && air.MainWindowHandle != 0)
                        airWindow = air.MainWindowHandle;
                    var (gamePid, terminalNotified) = maestro.OwnedGameState;
                    var ownedWindow = airWindow != 0 && IsWindow(airWindow) &&
                        GetWindowThreadProcessId(airWindow, out var windowPid) != 0 && windowPid == (uint)air.Id;
                    var iconic = ownedWindow && IsIconic(airWindow);
                    var visible = ownedWindow && IsWindowVisible(airWindow);
                    var foreground = GetForegroundWindow();
                    _ = GetWindowThreadProcessId(foreground, out var foregroundPid);
                    var action = transition.Observe(gamePid, ownedWindow, iconic, visible,
                        airFocused: foregroundPid == (uint)air.Id, nativeStopped: terminalNotified,
                        nowMilliseconds: Environment.TickCount64);
                    // Never send a show/hide command to a window that is not visible: a hidden
                    // window is already where the stock AIR (tray-only) leaves it. RestoreHidden is the one
                    // exception: it exists to show a window that stayed hidden after the game ended.
                    if ((action is (AirWindowAction.Minimize or AirWindowAction.Restore or AirWindowAction.Hide) &&
                         IsWindowVisible(airWindow)) || action == AirWindowAction.RestoreHidden)
                    {
                        var queued = ShowWindowAsync(airWindow, action switch
                        {
                            AirWindowAction.Minimize => SwMinimize,
                            AirWindowAction.Hide => SwHide,
                            _ => SwRestore,
                        });
                        Console.WriteLine($"air.window-{action.ToString().ToLowerInvariant()} t={DateTimeOffset.Now:O} " +
                            $"air_pid={air.Id} game_pid={gamePid} request_queued={queued}");
                    }
                    if (gamePid is not null)
                    {
                        lastGamePid = gamePid;
                        postGameEnd = 0;
                    }
                    else if (lastGamePid is not null && postGameEnd == 0)
                        postGameEnd = Environment.TickCount64 + PostGameSampleMilliseconds;
                    var loggedGamePid = gamePid ?? (Environment.TickCount64 < postGameEnd ? lastGamePid : null);
                    if (loggedGamePid is null)
                    {
                        previous = null;
                        lastGamePid = null;
                        postGameEnd = 0;
                        continue;
                    }

                    var airState = !ownedWindow ? "unknown" :
                        iconic ? "minimized" : visible ? "visible" : "hidden";
                    var sample = new Sample(loggedGamePid.Value, airState, visible, iconic, foregroundPid,
                        PostGame: gamePid is null);
                    if (sample == previous) continue;
                    if (sample.GamePid != previous?.GamePid || sample.PostGame != previous?.PostGame) changes = 0;
                    previous = sample;
                    if (changes++ < MaxChangesPerGame)
                    {
                        var role = foregroundPid == 0 ? "none" : foregroundPid == (uint)air.Id ? "air" :
                            foregroundPid == (uint)loggedGamePid ? "game" : "other";
                        Console.WriteLine($"air.window-state t={DateTimeOffset.Now:O} air_pid={air.Id} " +
                            $"game_pid={loggedGamePid} phase={(sample.PostGame ? "post-game" : "game")} " +
                            $"air_state={airState} visible={visible} iconic={iconic} " +
                            $"foreground={role} foreground_pid={foregroundPid} idle_ms={IdleMilliseconds()}");
                    }
                    else if (changes == MaxChangesPerGame + 1)
                        Console.WriteLine("air.window-state change limit reached; later focus changes in this game are omitted.");
                }
                catch (ObjectDisposedException) when (!stop.IsCancellationRequested) { }
                catch (InvalidOperationException) { /* a racing game exit can dispose its Process between samples */ }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or Win32Exception or
                                      DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine($"air.window-state unavailable reason={error.GetType().Name}");
        }
    }

}

internal enum AirWindowAction { None, Minimize, Restore, Hide, RestoreHidden }

internal sealed class AirWindowTransition
{
    // AIR restores its own window when a game ends. A window still hidden this long after the end is restored once.
    private const long SettleMilliseconds = 4000;
    private int? _gamePid;
    private bool _sampled;
    private bool _minimizeArmed;
    private bool _restorePending;
    private bool _userShown;
    private bool _hideQueued;
    private long? _settleStart;
    private bool _settleChecked;

    public AirWindowAction Observe(int? gamePid, bool ownedWindow, bool iconic, bool visible, bool airFocused,
        bool nativeStopped = false, long nowMilliseconds = 0)
    {
        if (gamePid != _gamePid)
        {
            _restorePending = _gamePid is not null && gamePid is null;
            // A new game starts a fresh settle clock; the exit of the previous game keeps its own running.
            if (gamePid is not null)
            {
                _settleStart = null;
                _settleChecked = false;
            }
            _gamePid = gamePid;
            _sampled = _minimizeArmed = _userShown = _hideQueued = false;
        }
        if (gamePid is not null)
        {
            // NOT RETAIL: once the native client reported its end (GameClientStopped/Crashed/Abandoned), AIR restores
            // its own window on that message (LolClient.as handleMaximizeEvent: restore(); visible=true). A Win32 hide
            // or minimize that lands during that restore leaves AIR's visible flag true with the window hidden, and
            // nothing restores it again (batch 373, owner finding 67). From here the companion neither hides nor
            // minimizes; a window still hidden at the settle time is restored once (Settle).
            if (nativeStopped)
            {
                _settleStart ??= nowMilliseconds;
                return Settle(ownedWindow, visible, nowMilliseconds);
            }
            if (!ownedWindow) return AirWindowAction.None;
            if (!visible)
            {
                _sampled = _minimizeArmed = true;
                _hideQueued = false;
                return AirWindowAction.None;
            }
            if (iconic)
            {
                _sampled = _minimizeArmed = true;
                // NOT RETAIL: retail hides AIR in AS3 (LolClient.as handleMinimizeEvent:
                // minimize(); visible=false; tray icon, no taskbar button), and AIR does it
                // three times per game start. The companion re-applies the hide because its
                // synthetic early GameClientLaunched (MaestroServer.CreateGameAsync) lands
                // AIR's third hide within milliseconds of its deferred minimize completing,
                // and a minimize that completes on an already hidden window shows it again
                // (visible + iconic = the taskbar entry). In that race AIR's own visible flag
                // is already false, so a Win32 hide matches AIR's state and its own restore at
                // game end (handleMaximizeEvent: restore(); visible=true) still works.
                // Once the window was seen reappearing (user opened AIR: tray or taskbar) AIR's
                // flag is true and a hide behind its back could leave it unrestorable, so that
                // game keeps the minimize-after-focus-loss behaviour and never hides.
                if (_userShown || _hideQueued) return AirWindowAction.None;
                _hideQueued = true;
                return AirWindowAction.Hide;
            }
            _hideQueued = false;
            // A visible, non-iconic window after it was seen hidden or minimized in this game
            // was shown by the user (retail never restores AIR mid-game).
            if (_minimizeArmed) _userShown = true;
            // The first visible sample minimizes AIR regardless of focus. Afterwards a
            // reappearance (seen hidden/minimized first) stays usable while
            // AIR has focus and is hidden again once it loses focus (playtest #20).
            if (_sampled && !(_minimizeArmed && !airFocused)) return AirWindowAction.None;
            _sampled = true;
            _minimizeArmed = false;
            return AirWindowAction.Minimize;
        }
        // The process exited. AIR restores its own window at game end (handleMaximizeEvent): a visible + iconic one
        // is restored here, and a window still hidden at the settle time is restored once (Settle).
        if (_restorePending) _settleStart ??= nowMilliseconds;
        if (_restorePending && ownedWindow)
        {
            _restorePending = false;
            if (visible && iconic) return AirWindowAction.Restore;
        }
        return Settle(ownedWindow, visible, nowMilliseconds);
    }

    // One check per game, SettleMilliseconds after the game ended (native stop or process exit). A window still hidden
    // is restored; a visible one is left alone. The check waits for a verifiable AIR window.
    // ponytail: hidden windows only (the batch rule). A minimized AIR (visible + iconic) at the check is left to the
    // post-game rule at process exit. Upgrade: restore it here too if owners see minimized AIR after games.
    private AirWindowAction Settle(bool ownedWindow, bool visible, long now)
    {
        if (_settleStart is null || _settleChecked || !ownedWindow) return AirWindowAction.None;
        if (now - _settleStart.Value < SettleMilliseconds) return AirWindowAction.None;
        _settleChecked = true;
        return visible ? AirWindowAction.None : AirWindowAction.RestoreHidden;
    }
}
