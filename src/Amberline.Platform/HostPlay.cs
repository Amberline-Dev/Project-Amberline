using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: Play on this PC (Amberline Host H12, design section 3.5). Retail players never ran the servers; here the host plays
/// on the PC that hosts. The Platform writes the stage <c>data\stage\stage.json</c> (the host's own game executable, the host's AIR
/// folder, the page's port, Maestro 8393, the local game address) and starts the companion beside the program
/// (<c>..\companion</c>) with <c>--desktop --stage-root data\stage</c>. The companion's local path does the rest: it reuses this
/// running Platform, makes the stage's AIR copy from the host's AIR folder (the source is only read) and opens AIR. The game and AIR
/// folders stay where the host keeps them. The friends' Play file is never used here: its proxy would take this Platform's loopback
/// ports. One companion at a time: a second press while the first still runs starts nothing.
/// </summary>
public sealed class HostPlay(Func<ProcessStartInfo, Process?> start)
{
    public const string CompanionName = "Amberline.AirCompanion.exe";
    public const int MaestroPort = 8393;
    private readonly Lock _gate = new();
    private Process? _running;

    /// <summary>The host's own stage, under the data folder: <c>stage.json</c>, the AIR copy (<c>air\</c>) and the companion's files.</summary>
    public static string StageRoot(HostPaths paths) => Path.Combine(paths.Data, "stage");

    /// <summary>The companion of the release, beside the Platform's folder (<c>app\companion</c>), as the server is.</summary>
    public static string CompanionExecutable(HostPaths paths) => Path.GetFullPath(Path.Combine(paths.App, "..", "companion", CompanionName));

    /// <summary>
    /// Writes <c>stage.json</c> for the companion's <c>LoadDesktopStage</c>. The port is named here, so the stage needs no
    /// <c>platform-data</c>; the game address is loopback, because the match servers run on this PC (the address friends are given
    /// may be the router's, which many routers do not reach from inside).
    /// </summary>
    public static string WriteStage(HostPaths paths, PlatformSettings settings)
    {
        var stage = StageRoot(paths);
        HostFiles.Write(Path.Combine(stage, "stage.json"), new
        {
            stockExecutable = Path.Combine(Path.GetFullPath(settings.DataRoot), HostFolders.GameExecutable),
            airSource = Path.GetFullPath(settings.AirRoot!),
            httpPort = settings.HttpPort,
            maestroPort = MaestroPort,
            gameHost = "127.0.0.1",
        });
        return stage;
    }

    /// <summary>The companion's start: no window of its own (its output goes to Amberline's log), from its own folder.</summary>
    public static ProcessStartInfo StartInfo(HostPaths paths)
    {
        var executable = CompanionExecutable(paths);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--desktop", "--stage-root", StageRoot(paths) }) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>The real start (<see cref="HostMode.MainAsync"/>): the companion's lines go to Amberline's log file.</summary>
    public static Process? Start(ProcessStartInfo info)
    {
        var process = Process.Start(info);
        if (process is null) return null;
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine("companion: " + line.Data); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine("companion: " + line.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Writes the stage and starts the companion; the page's text, in plain words, and whether it started.</summary>
    public (bool Started, string Page) Run(HostPaths paths, PlatformSettings settings)
    {
        lock (_gate)
        {
            if (_running is { HasExited: false })
                return (false, "<h1>Your game client is already open</h1><p>It may still be starting. Look for it on the taskbar.</p>");
            var executable = CompanionExecutable(paths);
            if (!File.Exists(executable))
                return (false, "<h1>Play on this PC</h1><p class=problem role=alert>A part of Amberline is missing: " +
                    $"{WebUtility.HtmlEncode(Path.GetRelativePath(Path.GetFullPath(Path.Combine(paths.App, "..")), executable))}. " +
                    "Download Amberline again and start it. (A-FILES-06)</p>");
            var stage = WriteStage(paths, settings);
            var first = !Directory.Exists(Path.Combine(stage, "air"));
            _running?.Dispose();
            try { _running = start(StartInfo(paths)); }
            catch (Win32Exception ex)
            {
                _running = null;
                return (false, $"<h1>Play on this PC</h1><p class=problem role=alert>Windows did not start your game client ({WebUtility.HtmlEncode(ex.Message)}). " +
                    "Try again, or restart Amberline.</p>");
            }
            if (_running is null)
                return (false, "<h1>Play on this PC</h1><p class=problem role=alert>Windows did not start your game client. Try again.</p>");
            return (true, "<h1>Your game client is opening</h1><p>Its window opens by itself" + (first
                ? ". The first time takes a few minutes.</p>"
                : " in up to a minute.</p>") + "<p>Log in with your own login.</p>" + Warnings(settings));
        }
    }

    /// <summary>The entry point for the dashboard (H7): one button, with the page's antiforgery field; it says it is starting (batch 652).</summary>
    public static string Button(string antiforgeryField) =>
        $"<form method=post action=/host/play{HostPages.Wait("Starting the game client. This takes up to a minute.")}>{antiforgeryField}<button>Play</button></form>";

    /// <summary>
    /// <c>/host/play</c> (gated by <see cref="HostGate"/>): GET explains and shows the button, POST plays. After a start the page
    /// goes back to <c>/open</c> (the host's landing) by itself; a refusal stays on screen with a link back.
    /// </summary>
    internal static void Map(WebApplication app, HostLoop loop, PlatformSettings settings)
    {
        app.MapGet("/host/play", (HttpContext context, IAntiforgery antiforgery) => HostMode.Page(
            "<h1>Play on this PC</h1><p>Open the game client here and log in with your own login.</p>" +
            Warnings(settings) + Button(Program.RegistrationToken(context, antiforgery))));
        app.MapPost("/host/play", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                return HostMode.Page("<h1>Play on this PC</h1><p class=problem role=alert>The page expired. Please try again.</p>" +
                    Button(Program.RegistrationToken(context, antiforgery)), StatusCodes.Status400BadRequest);
            }
            var (started, page) = loop.Play.Run(loop.Paths, settings);
            return started ? HostMode.Page(page + "<p class=tip>This page goes back by itself.</p>", refresh: "/open")
                : HostMode.Page(page + "<p><a href=/open>Back</a></p>", StatusCodes.Status409Conflict);
        });
    }

    private static string Warnings(PlatformSettings settings) =>
        string.Concat(HostFolders.GameWarnings(settings.DataRoot).Select(warning => $"<p class=warn>{WebUtility.HtmlEncode(warning)}</p>"));
}
