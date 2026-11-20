using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Amberline.AirCompanion;

/// <summary>Whether this start opens the classic launcher; a null reason when it doesn't means there is nothing to tell (no launcher here).</summary>
internal sealed record ClassicPlan(bool Use, string? Reason);

/// <summary>
/// NOT RETAIL: the classic start (design 2026-11-12 §2, §5). In 2011 the player opened the launcher,
/// which ran on Riot's update service and then started AIR and the game itself. That service is gone, so the player tool decides whether the
/// player's own 2011 launcher (the line's release, <see cref="ClassicLauncherFiles"/>) can run, stands in for the service while it runs (<see cref="RadsKernelStandIn"/>), and stays until the
/// launcher, AIR and the game have all closed, ending a launcher that outlives AIR (<see cref="WaitForEndAsync(Func{bool}, Func{bool}, Func{bool}, Action, Action, Action{string}, int, TimeSpan, CancellationToken)"/>).
/// Any reason it can't run sends the player to the usual start in the same process, told once why.
/// </summary>
internal static class ClassicStart
{
    /// <summary>In the stage folder from the launcher's start until AIR is seen: a classic start that never got the player into AIR makes the
    /// next start ask before anything opens.</summary>
    internal const string UnfinishedMark = "classic.unfinished";

    /// <summary>The launcher's own Maestro: the launcher binds 8395 at once, AIR's 8393 and the game's 8394 on Play (the three ports of design 2026-11-12 §2).</summary>
    private static readonly int[] LauncherPorts = [LauncherAirPort, 8394, 8395];

    /// <summary>The launcher's Maestro port for AIR: 0x20c9, pushed into MaestroServerController_Init by 0.0.0.76 and by
    /// 1.0.0.251 alike; AIR's MAESTROMESSAGETYPE_CLOSE arrives here.</summary>
    internal const int LauncherAirPort = 8393;

    /// <summary>Polls (seconds) after AIR and the game have closed before the launcher is sent AIR's close, and as many again before it is
    /// stopped. Longer than the launcher's own teardown after a close (about 7 s: a 3 s wait in MaestroMessageAgent_Delete 0x10002db0, up
    /// to 1 s per Maestro accept thread 0x10004180, its 1 s poll), so a second close never lands in the middle of it.</summary>
    internal const int GracePolls = 15;

    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(60);

    /// <param name="sha256">DIAGNOSTICS: test seam only: the accepted launcher's fingerprint.</param>
    internal static ClassicPlan Decide(Program.DesktopStage stage, string? root, Func<int, bool> portFree, Func<string, bool> mutexFree,
        string sha256 = ClassicLauncherFiles.LauncherSha256)
    {
        // Not a reason: an install without the launcher, or a stage that names none (the operator's START-AIR without launcherRoot).
        if (stage.LauncherRoot is not { } folder || !Directory.Exists(folder)) return new(false, null);
        if (ClassicLauncherFiles.Problem(folder, sha256) is { } problem) return new(false, problem);
        if (root is null) return new(false, "The RADS folder named in the registry is not a full path.");
        foreach (var port in LauncherPorts)
            if (!portFree(port)) return new(false, $"Port {port} is in use by another program.");
        if (!mutexFree(RadsKernelStandIn.MutexName)) return new(false, "Another classic launcher or update service is already running on this PC.");
        return new(true, null);
    }

    /// <summary>Whether this start opens the launcher: the plan allows it and, after a classic start that never saw AIR, the player chose it again.</summary>
    internal static bool Takes(ClassicPlan plan, string stageRoot, Func<bool> askClassic) => plan.Use && (!ShouldAsk(stageRoot) || askClassic());

    internal static bool ShouldAsk(string stageRoot) => File.Exists(Path.Combine(stageRoot, UnfinishedMark));

    // The mark only adds a question, so a write that fails is skipped rather than stopping a start.
    internal static void MarkStarted(string stageRoot) => Courtesy(() => File.WriteAllText(Path.Combine(stageRoot, UnfinishedMark), ""));

    internal static void MarkAirSeen(string stageRoot) => Courtesy(() => File.Delete(Path.Combine(stageRoot, UnfinishedMark)));

    private static void Courtesy(Action write)
    {
        try { write(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The one line the player is told when the classic start falls back.</summary>
    internal static string FallbackMessage(string reason) => $"The classic launcher did not open: {reason.TrimEnd().TrimEnd('.')}. Starting the usual way.";

    internal static bool MutexFree(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var held)) return true;
            held.Dispose();
            return false;
        }
        // Held by an elevated or another user's process, or Windows could not tell: not free.
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>
    /// The classic start's own steps (the layout and the session) with no way to leave the player stuck (design anti-goal): any exception but
    /// a stop becomes a fallback reason naming it, so the usual start always follows.
    /// </summary>
    internal static async Task<string?> GuardedAsync(Func<Task<string?>> steps, CancellationToken stop)
    {
        try { return await steps(); }
        catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            return $"It stopped with {exception.GetType().Name} ({exception.Message.TrimEnd('.')}).";
        }
    }

    /// <summary>
    /// Runs one classic session: the stand-in's mutex and pipe, the layout (<see cref="ClassicLauncherLayout.Ensure"/>, which first takes the root
    /// over from the other line), then the launcher from its release folder. The mutex and the pipe come first, so a second start, or another
    /// program serving the pipe, never touches the layout or re-points the shared junctions at its own install. Null once the launcher connected
    /// and it, AIR and the game have all closed (or <paramref name="stop"/> ended the wait); else why the launcher didn't open, for the usual
    /// start. A launcher that never connects within a minute is stopped, and one that outlives AIR is sent AIR's close and then stopped
    /// (<see cref="WaitForEndAsync(Func{bool}, Func{bool}, Func{bool}, Action, Action, Action{string}, int, TimeSpan, CancellationToken)"/>);
    /// only that process: AIR and the game are never touched.
    /// </summary>
    internal static async Task<string?> RunAsync(Program.DesktopStage stage, ClassicLayoutRequest request, Action<string> log, Action? clientShown,
        CancellationToken stop)
    {
        var root = request.Root;
        RadsKernelStandIn kernel;
        try { kernel = new RadsKernelStandIn(root, log, releases: ClassicLauncherLayout.AnsweredReleases); }
        catch (InvalidOperationException) { return "Another classic launcher or update service is already running on this PC."; }
        using (kernel)
        using (var serve = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            var serving = kernel.ServeAsync(serve.Token);
            try
            {
                // The pipe is made before ServeAsync's first wait, so a pipe that could not be made has ended it already.
                if (serving.IsCompleted)
                    return stop.IsCancellationRequested ? null : $"The launcher's update service did not start ({serving.Exception?.GetBaseException().Message}).";
                var (layout, refusal) = ClassicLauncherLayout.Ensure(request, log);
                if (layout is null) return refusal;
                Process launcher;
                try { launcher = Process.Start(new ProcessStartInfo(layout.LauncherExecutable) { WorkingDirectory = layout.Deploy, UseShellExecute = false })!; }
                catch (Win32Exception exception) { return $"Windows did not start it ({exception.Message})."; }
                using (launcher)
                {
                    MarkStarted(stage.Root);
                    log($"classic: started the launcher (process {launcher.Id}) from {layout.Deploy}");
                    await Task.WhenAny(kernel.Connected, serving, launcher.WaitForExitAsync(stop), Task.Delay(ConnectLimit, stop));
                    if (!kernel.Connected.IsCompleted)
                    {
                        if (stop.IsCancellationRequested) return null;
                        if (launcher.HasExited) return "It closed before it was ready.";
                        if (serving.IsCompleted) return $"The launcher's update service stopped ({serving.Exception?.GetBaseException().Message}).";
                        Stop(launcher);
                        return $"It was not ready within {ConnectLimit.TotalSeconds:0} seconds.";
                    }
                    log("classic: the launcher is connected; waiting until it, AIR and the game have closed");
                    clientShown?.Invoke();
                    await WaitForEndAsync(stage, root, launcher, log, stop);
                    return null;
                }
            }
            finally
            {
                serve.Cancel();
                try { await serving; }
                catch (Exception exception) { log($"classic: the update service stand-in ended with {exception.GetType().Name}: {exception.Message}"); }
            }
        }
    }

    /// <summary>Polls every second until the launcher, AIR and the game have exited; clears the mark the first time AIR is seen.</summary>
    private static Task WaitForEndAsync(Program.DesktopStage stage, string root, Process launcher, Action<string> log, CancellationToken stop)
    {
        // The launcher starts both through the layout's junctions; either path may be reported, so both count.
        var classic = ClassicLauncherLayout.Images(root);
        string[] air = [Path.Combine(stage.AirRoot, "LolClient.exe"), classic.Air];
        string[] game = [stage.StockExecutable, classic.Game];
        return WaitForEndAsync(() => Running(air), () => Running(game), () => launcher.HasExited, () => MarkAirSeen(stage.Root),
            () => Stop(launcher), log, LauncherAirPort, TimeSpan.FromSeconds(1), stop);
    }

    /// <summary>
    /// The wait, one poll per <paramref name="poll"/>; <paramref name="airSeen"/> runs the first time AIR is seen.
    /// NOT RETAIL: retail's launcher leaves its windowless wait after Play
    /// only through launcher.maestro.dll's exit callback, which the DLL runs on the MAESTROMESSAGETYPE_CLOSE that AIR
    /// writes to the launcher's Maestro on port 8393 as it exits (MaestroServiceImpl.sendCloseRequest; the DLL's AIR handler 0x100048d0,
    /// case 3). Nothing else ends it: MaestroServerController_ProcessEvents (0x100052a0), which would when AIR's process is gone, is called
    /// by nothing, so a close that is not processed left retail's launcher running too, and a running launcher makes the next one quit at
    /// once ("Another launcher instance is running") and the other line's takeover refuse. So once AIR has been seen and AIR and
    /// the game have both been closed for <see cref="GracePolls"/> polls while the launcher still runs, this sends the launcher that close
    /// itself, the frame AIR sends, on <paramref name="closePort"/>; <see cref="GracePolls"/> polls later a launcher still running is
    /// stopped (<paramref name="stopLauncher"/>, that process only). Neither while AIR or the game runs: either one starts the count again.
    /// </summary>
    /// <param name="closePort">DIAGNOSTICS: test seam (with <paramref name="poll"/>); <see cref="LauncherAirPort"/> otherwise.</param>
    internal static async Task WaitForEndAsync(Func<bool> airRunning, Func<bool> gameRunning, Func<bool> launcherExited, Action airSeen,
        Action stopLauncher, Action<string> log, int closePort, TimeSpan poll, CancellationToken stop)
    {
        var seen = false;
        var idle = 0;   // polls with the launcher running after AIR, AIR and the game both closed
        try
        {
            while (true)
            {
                var air = airRunning();
                if (air && !seen)
                {
                    seen = true;
                    airSeen();
                    log("classic: AIR is running");
                }
                var game = gameRunning();
                if (launcherExited())
                {
                    if (!air && !game) break;
                }
                else if (seen && !air && !game)
                {
                    if (++idle == GracePolls) await SendAirCloseAsync(closePort, log, stop);
                    else if (idle == 2 * GracePolls)
                    {
                        log("classic: the launcher is still running after AIR's close; stopping it");
                        stopLauncher();
                    }
                }
                else idle = 0;
                await Task.Delay(poll, stop);
            }
            log("classic: the launcher, AIR and the game have closed");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>
    /// Writes AIR's MAESTROMESSAGETYPE_CLOSE to the launcher's Maestro on <paramref name="port"/> and holds the connection while the launcher
    /// answers (a Reply, then it drops the connection in its teardown), five seconds at most. Logs, never throws but for <paramref name="stop"/>.
    /// </summary>
    private static async Task SendAirCloseAsync(int port, Action<string> log, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            var stream = client.GetStream();
            await MaestroProtocol.WriteAsync(stream, MaestroMessageType.Close, cancellationToken: deadline.Token);
            log($"classic: AIR and the game have closed but the launcher has not; sent it AIR's Maestro close on port {port}");
            while (await MaestroProtocol.ReadAsync(stream, deadline.Token) is not null) { }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            log($"classic: the launcher's Maestro on port {port}: no end of the connection within 5 seconds");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException)
        {
            log($"classic: the launcher's Maestro on port {port}: {exception.Message}");
        }
    }

    private static void Stop(Process launcher)
    {
        try { launcher.Kill(); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    /// <summary>
    /// The refusal while a 2011 launcher, an AIR client or a game runs from inside one of <paramref name="folders"/> (the RADS root and the folders
    /// the other line's junctions lead to; <see cref="ClassicLauncherLayout.Ensure"/> asks before it removes that line's layout); else null. Only
    /// processes of those three file names are inspected; one this user cannot inspect (another user's, an elevated one) is not this session's.
    /// </summary>
    internal static string? RunningFrom(IReadOnlyList<string> folders)
    {
        foreach (var name in new[] { "LoLLauncher", "LolClient", "League of Legends" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try
                    {
                        if (process.MainModule?.FileName is { } image && folders.Any(folder => FolderMove.Inside(image, folder)))
                            return $"{Path.GetFileName(image)} is running from {Path.GetDirectoryName(image)}. Close it first.";
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return null;
    }

    /// <summary>Whether a process runs from one of <paramref name="images"/> (only processes of that file name are inspected).</summary>
    private static bool Running(string[] images)
    {
        var found = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(images[0])))
            using (process)
                try { found |= process.MainModule?.FileName is { } image && images.Any(path => FolderMove.Same(image, path)); }
                // A process this user cannot inspect (another user's, an elevated one) is not this session's.
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return found;
    }
}
