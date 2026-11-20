using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL (batch 856, design 2026-11-17 Part 1; retail had no player tool): a server that moved from patch 1.0.0.126 to 1.0.0.132. While
/// its 1.0.0.126 side is closed, the host's 1.0.0.132 server answers on the old port with the old manifest plus <c>movedTo</c> (the 1.0.0.132
/// port, pin, server name and player folder). The tool then never plays: it says so first, before every other check of the shortcut's
/// start and of the <c>--install</c> window, and offers to set up 1.0.0.132 for the same server the way that line's invite does
/// (<see cref="PlayerInvite.SetUpAsync"/>), or to start this PC's 1.0.0.132 install of it. The 1.0.0.132 tool carries this code too; no
/// server sends it <c>movedTo</c>.
/// </summary>
internal static class ServerMoved
{
    /// <summary>
    /// The manifest's <c>movedTo</c> as the server sent it; <see cref="Problem"/> checks it before any of it is used. Any <c>movedTo</c> but
    /// null counts (<see cref="Lenient"/>), so a malformed one still never plays.
    /// </summary>
    [JsonConverter(typeof(Lenient))]
    internal sealed record Target(int Port, string? CertificateSha256, string? Name, string? PlayerFolder);

    /// <summary>
    /// Reads any JSON value as a <see cref="Target"/>: a field that is missing or of another type (or a <c>movedTo</c> that is not an object)
    /// reads as 0 or null, which <see cref="Problem"/> refuses. A typed read would throw instead, and a manifest that cannot be read plays.
    /// </summary>
    private sealed class Lenient : JsonConverter<Target>
    {
        public override Target Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            // As the manifest's own fields are read (JsonSerializerDefaults.Web): any case; the last of a repeated name.
            JsonElement? Field(string name)
            {
                JsonElement? found = null;
                if (root.ValueKind == JsonValueKind.Object)
                    foreach (var field in root.EnumerateObject())
                        if (field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) found = field.Value;
                return found;
            }
            string? Text(string name) => Field(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            var port = Field("port") is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var parsed) ? parsed : 0;
            return new(port, Text("certificateSha256"), Text("name"), Text("playerFolder"));
        }

        public override void Write(Utf8JsonWriter writer, Target value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    /// <summary>The dialog: titled with this server's name; <paramref name="Installed"/> offers Start it instead of Set up.</summary>
    internal sealed record Question(string Title, string WhatHappened, string WhatToDo, bool Installed);

    /// <summary>
    /// DIAGNOSTICS: the windows and the process start as delegates (a test seam), so a check drives the dialog, Set up and its failures
    /// without a window, a real profile or a started program. <see cref="Ask"/> is true for Set up (or Start it); <see cref="Start"/> gets the
    /// program and its arguments; <see cref="Log"/> gets the log's lines (the shortcut's log, or the install log: the <c>--install</c>
    /// window has no console of its own).
    /// </summary>
    internal sealed record Ui(string LocalAppData, Action<string> Status, Func<Question, bool> Ask, Func<PlayerProblem, bool> TryAgain,
        Action<string, IReadOnlyList<string>> Start, Action<string> Log)
    {
        public static Ui Windows(PlayerInstallWindow.StatusWindow status, Action<string> log) => new(PlayerProfile.Current().LocalAppData, status.Show,
            question =>
            {
                status.Hide();
                return PlayerInstallWindow.AskMoved(question);
            },
            problem =>
            {
                status.Hide();
                return PlayerInstallWindow.AskTryAgain(problem);
            }, Launch, log);
    }

    // The design's words (Part 1 step 4); the dialog only ever shows on the 1.0.0.126 line.
    internal const string Patch = "1.0.0.132 (January 2012)";
    internal const string SignIn = "You sign in with the same username and password as before. Your 1.0.0.126 setup stays.";
    internal const string SettingUp = "Setting up patch 1.0.0.132. This takes a minute or two.";

    /// <summary>
    /// The <c>--install</c> window's first step (the path an old Play file or an invite takes): the manifest of the server it reached. Null
    /// when it did not move, or when the manifest cannot be read (the install goes on as before); otherwise the moved dialog's exit code. A
    /// <c>movedTo</c> that <see cref="Problem"/> refuses is the plain "could not start" problem, whose Try again reads the manifest again.
    /// </summary>
    internal static async Task<int?> BeforeInstallAsync(AirRemoteInvitation connection, string title, string log, Ui ui)
    {
        while (true)
        {
            PlayerInstall.Manifest manifest;
            // Nothing is printed here: the server's address is never shown.
            try { manifest = await PlayerInstall.ReadManifestAsync(connection.Host, connection.Port, connection.CertificateSha25664hex, CancellationToken.None); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
            if (manifest.MovedTo is not { } moved) return null;
            try { return await RunAsync(title, connection.Host, moved, log, ui); }
            catch (InvalidDataException refused)
            {
                if (!ui.TryAgain(PlayerMode.StartProblem(refused, title, connection.Host, log))) return 1;
            }
        }
    }

    /// <summary>
    /// The moved dialog, then Start it (this PC's 1.0.0.132 install of the server: its shortcut's program) or Set up (<see cref="SetUpAsync"/>,
    /// then the 1.0.0.132 tool's <c>--install</c>). A failure asks Try again. 0 once a program started; 1 for Not now or Close: never play.
    /// A <c>movedTo</c> that <see cref="Problem"/> refuses gets neither: it throws <see cref="InvalidDataException"/> before the dialog, which the
    /// caller shows as the plain "could not start" problem (never play).
    /// </summary>
    internal static async Task<int> RunAsync(string title, string host, Target moved, string log, Ui ui)
    {
        if (Problem(moved) is { } invalid) throw Invalid(invalid);
        ui.Log($"moved: the server now runs patch {Patch}");
        var installed = Installed(ui.LocalAppData, moved);
        var question = new Question(title, $"{title} now runs patch {Patch}.",
            (installed is null ? "Set it up now? " : "It is set up on this PC already. Start it? ") + SignIn, installed is not null);
        if (!ui.Ask(question))
        {
            ui.Log("moved: not now");
            return 1;
        }
        while (true)
        {
            try
            {
                if (installed is { } found) ui.Start(found.Tool, ["--shortcut", found.Id]);
                else
                {
                    ui.Status(SettingUp);
                    var (file, arguments) = await SetUpAsync(ui.LocalAppData, host, moved, CancellationToken.None);
                    ui.Start(file, arguments);
                }
                ui.Log(installed is null ? "moved: started the 1.0.0.132 setup" : "moved: started the 1.0.0.132 install");
                return 0;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The address is kept out of the log, as every window keeps it out.
                ui.Log(string.IsNullOrEmpty(host) ? exception.ToString() : exception.ToString().Replace(host, "the server", StringComparison.OrdinalIgnoreCase));
                var problem = PlayerMode.StartProblem(exception, title, host, log);
                // A refusal of the setup's own (a check that failed, a file) names the setup, not the start.
                if (exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or
                    HttpRequestException { StatusCode: not null })
                    problem = problem with { WhatHappened = $"Patch 1.0.0.132 could not be set up for {title}." };
                if (!ui.TryAgain(problem)) return 1;
            }
        }
    }

    /// <summary>
    /// What is wrong with <paramref name="moved"/>, or null: a port, a 64-hex pin, a server name by the desktop file name rule, and a player
    /// folder by the player folders' rule (<c>^Amberline( [0-9]+)? Player\z</c>, any case, as the old Play file's script checked it),
    /// never this line's own player folder, the host's folder or <c>Amberline</c>.
    /// </summary>
    internal static string? Problem(Target moved) =>
        moved.Port is < 1 or > 65535 ? "no valid port"
        : moved.CertificateSha256 is not { Length: 64 } pin || !pin.All(char.IsAsciiHexDigit) ? "no valid certificate pin"
        : !PlayerInstall.IsName(moved.Name) ? "no valid server name"
        : moved.PlayerFolder is not { } folder || !PlayerFolderRule.IsMatch(folder) ||
          new[] { Path.GetFileName(FolderMove.PlayerFolder("")), FolderMove.HostName, "Amberline" }.Contains(folder, StringComparer.OrdinalIgnoreCase)
            ? "no valid player folder"
        : null;

    private static readonly Regex PlayerFolderRule = new(@"^Amberline( [0-9]+)? Player\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static InvalidDataException Invalid(string problem) =>
        new($"The server's message about patch 1.0.0.132 is not valid ({problem}). Ask the host to update the server.");

    /// <summary>
    /// This PC's 1.0.0.132 install of the server, by its port and pin (never its host: the 1.0.0.132 server may advise another address):
    /// the install record in <c>%LOCALAPPDATA%\&lt;playerFolder&gt;</c>, that server's connection in the install, and the shortcut's program.
    /// </summary>
    internal static (string Tool, string Id)? Installed(string localAppData, Target moved)
    {
        if (Problem(moved) is not null || !Path.IsPathFullyQualified(localAppData)) return null;
        var install = PlayerInstall.Text(PlayerInstall.ReadObject(Path.Combine(localAppData, moved.PlayerFolder!, "install.json")), "install");
        if (install is null || !Path.IsPathFullyQualified(install)) return null;
        var id = PlayerInstall.ServerId(moved.CertificateSha256!);
        try
        {
            var connection = Program.ReadRemoteInvitation(Path.Combine(install, "servers", id, "server.json"));
            var tool = Path.Combine(install, PlayerInstall.ToolFolder, PlayerInstall.WindowlessName);
            return connection.Port == moved.Port && string.Equals(connection.CertificateSha25664hex, moved.CertificateSha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(tool) ? (tool, id) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return null; }
    }

    /// <summary>
    /// What the 1.0.0.132 line's invite does (<see cref="PlayerInvite.SetUpAsync"/>, which does what that line's Play file did), with the host
    /// this tool reached and <paramref name="moved"/>'s port, pin, name and player folder: the manifest and the player tool from that host over
    /// the pinned port, unpacked under <c>%LOCALAPPDATA%\&lt;playerFolder&gt;</c> (<c>companion</c>, <c>companion.sha256</c>, <c>server.json</c>).
    /// Returns the program to start and its arguments.
    /// </summary>
    internal static Task<(string File, string[] Arguments)> SetUpAsync(string localAppData, string host, Target moved, CancellationToken cancel)
    {
        if (Problem(moved) is { } invalid) throw Invalid(invalid);
        if (!Path.IsPathFullyQualified(localAppData))
            throw new IOException("Windows has no app data folder for your user. Sign in to Windows with your own account, then try again.");
        return PlayerInvite.SetUpAsync(Path.Combine(localAppData, moved.PlayerFolder!), host, moved.Port, moved.CertificateSha256!, moved.Name!, cancel);
    }

    /// <summary>
    /// Starts <paramref name="file"/> from its own folder with no console window: the player tool's console program shows only its setup
    /// window (the 1.0.0.132 tool's here, the server's tool for an invite, <see cref="PlayerInvite"/>).
    /// </summary>
    internal static void Launch(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using (Process.Start(start)) { }
    }
}
