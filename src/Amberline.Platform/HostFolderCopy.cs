using System.Net;
using Amberline.Platform.Folders;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the folder step's copy into a one-folder install (batch 642; retail's patcher installed its clients). When the program
/// runs from an install folder (batch 637's marker) and a picked folder is outside it, "Use these folders" first offers to copy the
/// game and the AIR client in, with their size and the drive's free space, and asks whether the originals go. The copy is START-HERE's
/// (<see cref="ClientPlan"/>, <see cref="FolderMove"/>): only the clients' own files, all or nothing, never a folder a player install
/// uses. Its page follows the progress through one script file (<see cref="ScriptPath"/>, as the dashboard's <c>page.js</c>) and says
/// when it is done; <c>platform.json</c> then names the copies. "Use them where they are" saves the picked folders as before.
/// </summary>
internal sealed class HostFolderCopy(HostLoop loop, string install, HostFolders.FolderPlace place)
{
    /// <summary>The copy's page under its folder step: setup's <c>/setup/copy</c>, or Settings' (batch 641).</summary>
    public string PagePath { get; } = place.Path + "/copy";
    private string StatusPath => PagePath + "/status.json";
    private string ScriptPath => PagePath + "/page.js";
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _job = Task.CompletedTask;
    private volatile Copy? _copy;

    /// <summary>One copy: the folders picked, its latest progress, then how it ended (the lines for the host, or why nothing changed).</summary>
    private sealed class Copy(string game, string air, bool delete, long bytes, string? launcher) : IProgress<InstallProgress>
    {
        public string Game { get; } = game;
        public string Air { get; } = air;
        /// <summary>NOT RETAIL (batch 761, task 8): the 2011 launcher's folder the page carried through the copy; it is never copied, it stays where it is.</summary>
        public string? Launcher { get; } = launcher;
        public bool Delete { get; } = delete;
        /// <summary>Batch 652: the copy's size, for the page's first line before any progress came in.</summary>
        public long Bytes { get; } = bytes;
        public volatile InstallProgress? Now;
        public volatile string? Problem;
        public string? Inside, InsideAir;
        public volatile IReadOnlyList<string>? Lines;
        public bool Running => Problem is null && Lines is null;
        // A step without a measure (a rename) keeps the bar where it was.
        public void Report(InstallProgress value) => Now = value.Percent is null ? value with { Percent = Now?.Percent ?? 0 } : value;
    }

    /// <summary>The copy step when this program runs from a one-folder install; null anywhere else (the folders are used where they are).</summary>
    public static HostFolderCopy? For(HostLoop loop, HostFolders.FolderPlace place) =>
        HostPaths.InstallFolder(loop.Paths.App) is { } install ? new(loop, install, place) : null;

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>The folder fields the copy's pages post back: the game, the AIR client and (batch 761, task 8) the 2011 launcher, which must survive the copy.</summary>
    private static string Hidden(string token, string? game, string? air, string? launcher) =>
        $"{token}<input type=hidden name=game value=\"{Encode(game)}\"><input type=hidden name=air value=\"{Encode(air)}\">" +
        $"<input type=hidden name=launcher value=\"{Encode(launcher)}\">";

    private ClientPlan Plan(FolderCheck game, FolderCheck air, bool delete) =>
        ClientPlan.Make(install, new(game.Folder, air.Folder, delete), loop.Folders.Records, (loop.Folders.GameSha256, loop.Folders.AirSha256), null);

    private static string Readable(Exception ex) => ex is InstallRefusedException ? ex.Message
        : $"Amberline cannot read every file of the folders you picked ({ex.Message}). Close programs that use them, then try again.";

    /// <summary>What the copy says at once (batch 652): its size and that it takes a while.</summary>
    private static string Starting(long bytes) => $"Copying the game files ({FolderMove.Gigabytes(bytes)} GB). This takes several minutes.";

    /// <summary>The folder step's own forms wait while the folders are checked (their files are hashed).</summary>
    private static readonly string Checking = HostPages.Wait("Checking the folders. This takes a few seconds.");

    /// <summary>
    /// The offer for two checked folders: what comes in, its size and the drive's free space, what stays in a folder that holds other
    /// things, and the delete question. Null when nothing would come in (both folders are inside the install, or it has its own copies).
    /// A <paramref name="problem"/> (a refused copy) shows in the error summary, beside the delete question when the host said yes.
    /// </summary>
    public IResult? Offer(string token, FolderCheck game, FolderCheck air, string? launcher, string? problem = null, bool delete = false, int status = StatusCodes.Status200OK)
    {
        ClientPlan? plan = null;
        try { plan = Plan(game, air, delete: false); }
        catch (Exception ex) when (ex is InstallRefusedException or IOException or UnauthorizedAccessException) { problem ??= Readable(ex); }
        if (plan is { Moves.Count: 0 }) return null;
        var here = Encode(install);
        var asked = plan is not null && delete && problem is not null;
        var details = "";
        if (plan is not null)
        {
            var drive = Path.GetPathRoot(install)!.TrimEnd('\\');
            var free = new DriveInfo(Path.GetPathRoot(install)!).AvailableFreeSpace;
            details = "<h2>What Amberline copies</h2><ul>" + string.Concat(plan.Moves.Select(move => $"<li>The {move.Label}: {Encode(move.Source)}</li>")) + "</ul>" +
                $"<p>They take {FolderMove.Gigabytes(plan.Bytes)} GB. Drive {Encode(drive)} has {FolderMove.Gigabytes(free)} GB free.</p>" +
                string.Concat(plan.Moves.Select(move => FolderMove.Others(move) is { Count: > 0 } others
                    ? $"<p>Other things in {Encode(move.Source)} stay where they are: {Encode(string.Join(", ", others))}.</p>" : "")) +
                string.Concat(plan.Notes.Select(note => $"<p>{Encode(note)}</p>"));
        }
        var question = plan is null ? "" : $"""
            <fieldset id=delete aria-describedby="delete-hint{(asked ? " delete-error" : "")}"><legend>Delete the original folders you picked?</legend>
            <p class=hint id=delete-hint>Only their game files go, once the copy is complete.</p>
            {HostPages.FieldError("delete", asked ? problem : null)}
            <label class=choice><input type=radio name=delete value=no{(delete ? "" : " checked")}>No, keep them</label>
            <label class=choice><input type=radio name=delete value=yes{(delete ? " checked" : "")}>Yes, delete the originals</label></fieldset>
            """;
        // Batch 649: reached from Settings, the question that the heading used to ask is the first line of the text.
        var lead = place.Games is null ? "" : $"<p>Copy your game files into {here}?</p>";
        var copy = plan is null ? "" : $"<button{HostPages.Wait(Starting(plan.Bytes))}>Copy the game files</button>";
        return HostMode.Page($"""
            <h1>{HostFolders.Heading(place, "Copy your game files into " + here + "?")}</h1>{lead}
            {HostPages.Errors((problem, asked ? "delete" : null))}
            <p>Then everything of Amberline is in one folder.</p>
            {details}
            <form method=post action={PagePath}>{Hidden(token, game.Folder, air.Folder, launcher)}{question}
            <div class=actions><a href={place.Path}>Back</a><button{(plan is null ? "" : " class=secondary")} formaction={place.Path} name=keep value=1>Use them where they are</button>{copy}</div></form>
            """, status, step: place.Step);
    }

    /// <summary>
    /// The copy's routes, all host routes behind the gate: POST starts it (one at a time), GET shows it, its status and its script. The
    /// script policy (<see cref="HostDashboard.Policy"/>) covers them. A Stop while copying cancels the copy, which then rolls back.
    /// </summary>
    public void Map(WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(PagePath)) context.Response.Headers.ContentSecurityPolicy = HostDashboard.Policy;
            return next(context);
        });
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            _stop.Cancel();
            // The renames after the copy run to their end or roll back, so nothing is left half moved.
            try { _job.Wait(); } catch (AggregateException) { }
        });
        app.MapPost(PagePath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var token = Program.RegistrationToken(context, antiforgery);
            if (!await antiforgery.IsRequestValidAsync(context))
                return HostMode.Page($"<h1>The page expired</h1><p><a href={place.Path}>Choose your game folders again.</a></p>", StatusCodes.Status400BadRequest, step: place.Step);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (_copy is { Running: true }) return Results.Redirect(PagePath);
            var game = HostFolders.Check(FolderKind.Game, form["game"].ToString(), loop.Folders);
            var air = HostFolders.Check(FolderKind.Air, form["air"].ToString(), loop.Folders);
            var launcher = HostFolders.Picked(FolderKind.Launcher, form["launcher"].ToString(), loop.Folders);
            if (!game.Ok || !air.Ok || !HostFolders.LauncherOk(launcher)) return HostFolders.Form(token, loop, place, game, air, launcher, null, false, StatusCodes.Status400BadRequest);
            if (place.Games?.Invoke() > 0) return HostFolders.Busy(token, loop, place, game, air, launcher);
            var delete = form["delete"] == "yes";
            ClientPlan plan;
            try { plan = Plan(game, air, delete); }
            catch (Exception ex) when (ex is InstallRefusedException or IOException or UnauthorizedAccessException)
            {
                return Offer(token, game, air, launcher.Folder, Readable(ex) + ClientPlan.Unchanged, delete, StatusCodes.Status400BadRequest) ?? Results.Redirect(place.Path);
            }
            if (plan.Moves.Count == 0) return Results.Redirect(place.Path);
            var copy = new Copy(game.Folder!, air.Folder!, plan.Delete, plan.Bytes, launcher.Folder);
            lock (_gate)
            {
                if (_copy is { Running: true }) return Results.Redirect(PagePath);
                _copy = copy;
                _job = Task.Run(() => Run(plan, copy));
            }
            return Results.Redirect(PagePath);
        });
        app.MapGet(PagePath, (HttpContext context, IAntiforgery antiforgery) => _copy is { } copy
            ? Show(copy, Program.RegistrationToken(context, antiforgery)) : Results.Redirect(place.Path));
        app.MapGet(StatusPath, () => _copy is { } copy ? Results.Json(Status(copy)) : Results.NotFound());
        app.MapGet(ScriptPath, () => Results.Text(Script(StatusPath), "text/javascript; charset=utf-8"));
    }

    /// <summary>The copy on a thread of its own; a refusal or failure has rolled everything back by the time it is reported.</summary>
    private void Run(ClientPlan plan, Copy copy)
    {
        IReadOnlyList<string> leftovers;
        try { leftovers = FolderMove.Execute(plan.Moves, plan.Delete, copy, new(AfterCopy: loop.Folders.AfterCopy), _stop.Token); }
        catch (Exception ex) when (ex is InstallRefusedException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Folder step, copy: {ex}");
            copy.Problem = ex switch
            {
                InstallRefusedException => ex.Message,
                OperationCanceledException => "Amberline stopped before the copy was done.",
                _ => "Amberline could not copy the game and the AIR client: the disk may be full, or a program uses their files.",
            } + ClientPlan.Unchanged;
            return;
        }
        try
        {
            (copy.Inside, copy.InsideAir) = (plan.Game, plan.Air);
            copy.Lines = plan.Finish(leftovers, loop.Paths.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Folder step, settings: {ex}");
            copy.Problem = $"The game and the AIR client are in {install} now, but Amberline could not save that in its settings. Choose the folders inside {install}.";
        }
    }

    /// <summary>
    /// The phase of a progress line ("Copying the game") for the live region, and its counts for the line below; before the first line,
    /// what the copy is and how long it takes (batch 652).
    /// </summary>
    private static (string Phase, string Detail) Split(Copy copy) =>
        copy.Now?.Text.Split(':', 2) is [var phase, var detail] ? (phase, detail.Trim()) : (copy.Now?.Text ?? Starting(copy.Bytes), "");

    private static object Status(Copy copy)
    {
        var (phase, detail) = Split(copy);
        return new { state = copy.Problem is not null ? "failed" : copy.Lines is not null ? "done" : "copying", phase, detail, percent = copy.Now?.Percent ?? 0 };
    }

    private IResult Show(Copy copy, string token)
    {
        var here = Encode(install);
        if (copy.Problem is { } problem)
            return HostMode.Page($"""
                <h1>{HostFolders.Heading(place, "Your game files were not copied")}</h1>
                {HostPages.Errors((problem, null))}
                <form method=post action={place.Path}{Checking}>{Hidden(token, copy.Game, copy.Air, copy.Launcher)}
                <div class=actions><a href={place.Path}>Back</a><button class=secondary name=keep value=1>Use them where they are</button><button>Try again</button></div></form>
                """, step: place.Step);
        if (copy.Lines is { } lines)
            return HostMode.Page($"""
                <h1>{HostFolders.Heading(place, "Your game files are in " + here)}</h1>
                <p class=notice>{(place.Games is null ? "Done." : $"Done: the game files are in {here} now.")}</p>
                {(copy.Delete ? "" : "<p>The folders you picked are as they were.</p>")}{string.Concat(lines.Select(line => $"<p>{Encode(line)}</p>"))}
                <form method=post action={place.Path}{Checking}>{Hidden(token, copy.Inside, copy.InsideAir, copy.Launcher)}<div class=actions><button>Continue</button></div></form>
                """, step: place.Step);
        var (phase, detail) = Split(copy);
        var percent = copy.Now?.Percent ?? 0;
        return HostMode.Page($"""
            <h1>{HostFolders.Heading(place, "Copying your game files into " + here)}</h1>
            <p>Keep this page and the Amberline window open.</p>
            <div aria-live=polite><p id=copy-phase class=headline>{Encode(phase)}</p></div>
            <label for=copy-bar>Progress</label><progress id=copy-bar max=100 value={percent}>{percent}%</progress>
            <p id=copy-detail>{Encode(detail)}</p>
            <noscript><p class=warn>Reload the page to see how far the copy is.</p></noscript>
            <script src={ScriptPath} defer></script>
            """, step: place.Step);
    }

    /// <summary>
    /// The copy page's one script: it reads <see cref="StatusPath"/> every second and sets text only, and only when it changed (the
    /// phase is a live region, the counts are not, so a screen reader hears each phase once); when the copy ends it reloads the page,
    /// which then says how it ended.
    /// </summary>
    private static string Script(string status) => $$"""
        'use strict';
        (() => {
          const byId = id => document.getElementById(id);
          const text = (id, value) => { const element = byId(id); if (element && element.textContent !== value) element.textContent = value; };
          async function follow() {
            try {
              const answer = await fetch('{{status}}', { cache: 'no-store' });
              if (!answer.ok) throw new Error(String(answer.status));
              const status = await answer.json();
              if (status.state !== 'copying') { location.reload(); return; }
              text('copy-phase', status.phase);
              text('copy-detail', status.detail);
              const bar = byId('copy-bar');
              if (bar.value !== status.percent) { bar.value = status.percent; bar.textContent = `${status.percent}%`; }
            } catch {
              text('copy-phase', 'Amberline does not answer. If you closed its window, start Amberline again.');
            }
            setTimeout(follow, 1000);
          }
          if (byId('copy-bar')) setTimeout(follow, 1000);
        })();
        """;
}
