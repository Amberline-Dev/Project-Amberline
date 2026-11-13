using System.ComponentModel;
using System.Diagnostics;

namespace Amberline.AirCompanion;

/// <summary>Whether this start opens the classic launcher; a null reason when it doesn't means there is nothing to tell (no launcher here).</summary>
internal sealed record ClassicPlan(bool Use, string? Reason);

/// <summary>
/// NOT RETAIL: the classic start (design 2026-11-12 §2, §5). In 2011 the player opened the launcher,
/// which ran on Riot's update service and then started AIR and the game itself. That service is gone, so the player tool decides whether the
/// player's own 1.0.0.251 launcher can run, stands in for the service while it runs (<see cref="RadsKernelStandIn"/>), and stays until the
/// launcher, AIR and the game have all closed. Any reason it can't run sends the player to the usual start in the same process, told once why.
/// </summary>
internal static class ClassicStart
{
    /// <summary>In the stage folder from the launcher's start until AIR is seen: a classic start that never got the player into AIR makes the
    /// next start ask before anything opens.</summary>
    internal const string UnfinishedMark = "classic.unfinished";

    /// <summary>The launcher's own Maestro: the launcher binds 8395 at once, AIR's 8393 and the game's 8394 on Play (the three ports of design 2026-11-12 §2).</summary>
    private static readonly int[] LauncherPorts = [8393, 8394, 8395];

    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(60);

    /// <param name="sha256">DIAGNOSTICS: test seam only: the accepted launcher's fingerprint.</param>
    internal static ClassicPlan Decide(Program.DesktopStage stage, string? root, Func<int, bool> portFree, Func<string, bool> mutexFree,
        string sha256 = ClassicLauncherFiles.LauncherSha256)
    {
        // Not a reason: an install without the launcher, or a stage that never takes the classic start (the operator's START-AIR).
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
    internal static string FallbackMessage(string reason) => $"The classic launcher didn't open: {reason.TrimEnd().TrimEnd('.')}. Starting the usual way.";

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
    /// Runs one classic session: the stand-in's mutex, the layout (<see cref="ClassicLauncherLayout.Ensure"/>), the stand-in's pipe, then the
    /// launcher from its release folder. The mutex comes first, so a second start that loses it never re-points the shared junctions at its own
    /// install. Null once the launcher connected and it, AIR and the game have all closed (or <paramref name="stop"/> ended the wait); else why
    /// the launcher didn't open, for the usual start. Only a launcher that never connects within a minute is stopped, and only that process: AIR
    /// and the game are never touched.
    /// </summary>
    internal static async Task<string?> RunAsync(Program.DesktopStage stage, ClassicLayoutRequest request, Action<string> log, Action? clientShown,
        CancellationToken stop)
    {
        var root = request.Root;
        RadsKernelStandIn kernel;
        try { kernel = new RadsKernelStandIn(root, log); }
        catch (InvalidOperationException) { return "Another classic launcher or update service is already running on this PC."; }
        using (kernel)
        using (var serve = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            var (layout, refusal) = ClassicLauncherLayout.Ensure(request, log);
            if (layout is null) return refusal;
            var serving = kernel.ServeAsync(serve.Token);
            try
            {
                // The pipe is made before ServeAsync's first wait, so a pipe that could not be made has ended it already.
                if (serving.IsCompleted)
                    return stop.IsCancellationRequested ? null : $"The update service stand-in did not start ({serving.Exception?.GetBaseException().Message}).";
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
                        if (serving.IsCompleted) return $"The update service stand-in stopped ({serving.Exception?.GetBaseException().Message}).";
                        try { launcher.Kill(); }
                        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
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
    private static async Task WaitForEndAsync(Program.DesktopStage stage, string root, Process launcher, Action<string> log, CancellationToken stop)
    {
        // The launcher starts both through the layout's junctions; either path may be reported, so both count.
        var classic = ClassicLauncherLayout.Images(root);
        string[] air = [Path.Combine(stage.AirRoot, "LolClient.exe"), classic.Air];
        string[] game = [stage.StockExecutable, classic.Game];
        var airSeen = false;
        try
        {
            while (true)
            {
                var airRunning = Running(air);
                if (airRunning && !airSeen)
                {
                    airSeen = true;
                    MarkAirSeen(stage.Root);
                    log("classic: AIR is running");
                }
                if (!airRunning && launcher.HasExited && !Running(game)) break;
                await Task.Delay(TimeSpan.FromSeconds(1), stop);
            }
            log("classic: the launcher, AIR and the game have closed");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
