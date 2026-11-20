using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amberline.Data;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL: the Home release zip (batch 625, H10): <c>tools/build-server.ps1 -Layout Home</c>. The check runs the real script on
/// fixtures. The stock executable is copied from the shipped release (read only: the script checks its SHA-256), the AIR folder is
/// fabricated at the stock size, and the built parts come from <c>-PartsFrom</c> (DIAGNOSTICS), so no full build runs. Every output
/// lives in a temporary folder. The script runs from a copy of the tools it reads, in a git repository of its own, so its clean and
/// dirty states are made here. Batch 682: the zip also carries the repository's START-HERE.cmd and tools/guide.ps1 (the one guide),
/// and its Start Amberline.cmd opens that guide when it is started without options.
/// </summary>
public static class HostReleaseChecks
{
    private const string Version = "1.4.0";
    // Batch 829: the line's own zip name, as the update check looks for it (Amberline-1.4.0-win-x64.zip on 126, Amberline-132-1.4.0-... on 132).
    private static readonly string ZipName = HostUpdates.ZipName(new Version(Version));
    private static readonly string NotesName = ZipName.Replace("-win-x64.zip", "-release-notes.md", StringComparison.Ordinal);
    // Batch 917: the players' app beside the zip (the invite spec's asset name: Amberline-Player-<v>.exe, Amberline-132-Player-<v>.exe).
    private static readonly string PlayerName = ZipName.Replace($"-{Version}-win-x64.zip", $"-Player-{Version}.exe", StringComparison.Ordinal);

    /// <summary>The release check's inputs: the line's facts, the stand-in game, AIR and built parts, and the script's own repository.</summary>
    private sealed record Release(JsonElement PatchJson, string Game, string Air, string Parts, string Repo, string Tools, string StartHere,
        string[] Fixture, string[] Home);

    /// <summary>
    /// The inputs under <paramref name="root"/>. The stock executable is copied from the shipped release (read only: the script checks its
    /// SHA-256), the AIR folder is fabricated at the stock size, the built parts are stand-ins for <c>-PartsFrom</c>, and the script's tools sit in
    /// a git repository of their own with one commit.
    /// </summary>
    private static Release Prepare(string root)
    {
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "DATA", "Scripts"));
        Directory.CreateDirectory(Path.Combine(game, "DATA", "BuildingBlocks"));
        foreach (var map in new[] { "Map1", "Map2", "Map3", "Map4", "Map8" }) Directory.CreateDirectory(Path.Combine(game, "LEVELS", map));
        File.Copy(Path.Combine(GameDataPaths.Default.Root, "League of Legends.exe"), Path.Combine(game, "League of Legends.exe"));
        var air = Path.Combine(root, "air");
        WriteFile(Path.Combine(air, "assets", "swfs", "AirGeneratedContent.swf"), "air");
        // The line's stock AIR .swf size and its label (patch.json), as build-server.ps1 checks them (batch 810).
        var patchJson = JsonDocument.Parse(File.ReadAllText(WorkspaceFiles.Find("patch.json"))).RootElement;
        using (var swf = File.Create(Path.Combine(air, "LolClient.swf"))) swf.SetLength(patchJson.GetProperty("airSwf").GetProperty("size").GetInt64());

        // The built parts (what build-server.ps1 publishes without -PartsFrom): the server, the Platform and the companion archive.
        var parts = Path.Combine(root, "parts");
        WriteFile(Path.Combine(parts, "server", "Amberline.Server.exe"), "server");
        WriteFile(Path.Combine(parts, "server", "amberline_enet.dll"), "enet");
        WriteFile(Path.Combine(parts, "server", "exehost.exe"), "exehost");
        WriteFile(Path.Combine(parts, "platform", "Amberline.Platform.exe"), "platform");
        WriteFile(Path.Combine(parts, "platform", "Amberline.Platform.dll"), "platform library");
        using (var archive = ZipFile.Open(Path.Combine(parts, "companion.zip"), ZipArchiveMode.Create))
        using (var entry = new StreamWriter(archive.CreateEntry("Amberline.AirCompanion.exe").Open(), new UTF8Encoding(false)))
            entry.Write("companion");
        // Batch 917: the players' app as the single-file publish leaves it: the one program, and files beside it that stay out.
        WriteFile(Path.Combine(parts, "player-app", "Amberline.AirCompanion.exe"), "the players' app");
        WriteFile(Path.Combine(parts, "player-app", "Play.cmd"), "@echo off");
        WriteFile(Path.Combine(parts, "player-app", "capabilities.json"), "{}");

        // The script's own tools and the native table it reads, in a repository of their own with one commit, where the checkout
        // has them (batch 797): the tools in host/tools, exehost.c and START-HERE.cmd in the patch folder.
        var repo = Path.Combine(root, "repo");
        var patch = WorkspaceFiles.Patch;
        var tools = Path.Combine(repo, "host", "tools");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(Path.Combine(repo, patch, "native", "exehost"));
        // build-server.ps1 and tools/guide.ps1 read the line's patch.json (batch 810).
        File.Copy(WorkspaceFiles.Find("patch.json"), Path.Combine(repo, patch, "patch.json"));
        var script = Path.Combine(tools, "build-server.ps1");
        var startHere = Path.Combine(repo, patch, "START-HERE.cmd");
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "build-server.ps1")), script);
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")), Path.Combine(tools, "player-file.ps1"));
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("native", "exehost", "exehost.c")), Path.Combine(repo, patch, "native", "exehost", "exehost.c"));
        File.Copy(AirBootstrapChecks.FindWorkspaceFile("START-HERE.cmd"), startHere);
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1")), Path.Combine(tools, "guide.ps1"));
        Git(repo, "init", "-q");
        Git(repo, "add", "-A");
        Git(repo, "-c", "user.name=check", "-c", "user.email=check@example.test", "commit", "-q", "-m", "check");

        string[] fixture = ["-File", script, "-Patch", patch, "-GameRoot", game, "-AirRoot", air];
        return new(patchJson, game, air, parts, repo, tools, startHere, fixture, [.. fixture, "-Layout", "Home", "-Version", Version, "-PartsFrom", parts]);
    }

    /// <summary>
    /// Batch 829: the release build names its zip as the update check looks for it, on each line. build-server.ps1's zip, its <c>.sha256</c>
    /// and the notes are <see cref="HostUpdates.ZipName"/>'s (the line's home name, <see cref="Amberline.AirCompanion.FolderMove.HostName"/>), the
    /// printed <c>gh release create</c> puts that zip at <see cref="HostUpdates.Asset"/>, and the notes, READ ME FIRST and its hash line say the
    /// line's name. The other line's zip name is never this line's.
    /// </summary>
    [Check("server.home-release-zip-name-is-the-update-checks", TimeoutSeconds = 300)]
    public static void TheReleaseZipIsNamedAsTheUpdateCheckLooksForIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-release-name");
        try
        {
            var release = Prepare(root);
            var output = Path.Combine(root, "release");
            var built = AirBootstrapChecks.RunPowerShell("pwsh", [.. release.Home, "-Output", output]);
            TestAssert.Equal(0, built.ExitCode, built.Output);
            var name = Amberline.AirCompanion.FolderMove.HostName;
            TestAssert.Equal($"{release.PatchJson.GetProperty("home").GetProperty("name").GetString()!.Replace(' ', '-')}-{Version}-win-x64.zip", ZipName,
                "HostUpdates.ZipName is the line's home name (patch.json) with hyphens");
            TestAssert.Equal(new[] { NotesName, PlayerName, PlayerName + ".sha256", ZipName, ZipName + ".sha256" }.Order(StringComparer.Ordinal).ToArray(),
                Directory.GetFiles(output).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray(), "the release folder");
            TestAssert.Equal(Amberline.AirCompanion.InviteLink.Line == "126" ? $"Amberline-Player-{Version}.exe" : $"Amberline-132-Player-{Version}.exe", PlayerName,
                "the players' app's name (b917-918 invite spec)");
            TestAssert.True(File.ReadAllText(Path.Combine(output, ZipName + ".sha256")).TrimEnd().EndsWith("  " + ZipName, StringComparison.Ordinal),
                "the .sha256 names the zip the update check reads it for");
            // The publish line uploads the zip under the line's tag, which is where the update check takes it from.
            var asset = HostUpdates.Asset(new Version(Version), ZipName);
            TestAssert.Equal($"{HostUpdates.ReleasesPage}/download/{PlatformLine.ReleaseTagPrefix}{Version}/{ZipName}", asset, "the zip's address");
            TestAssert.True(built.Output.Contains($"gh release create {PlatformLine.ReleaseTagPrefix}{Version} \"{Path.Combine(output, ZipName)}\" " +
                $"\"{Path.Combine(output, ZipName)}.sha256\" \"{Path.Combine(output, PlayerName)}\" \"{Path.Combine(output, PlayerName)}.sha256\" --notes-file", StringComparison.Ordinal),
                "the release attaches the zip, the players' app and their .sha256: " + built.Output);
            var notes = File.ReadAllText(Path.Combine(output, NotesName));
            TestAssert.True(notes.StartsWith($"# {name} {Version}\n", StringComparison.Ordinal) || notes.StartsWith($"# {name} {Version}\r\n", StringComparison.Ordinal),
                "the notes' title names the line: " + notes.Split('\n')[0]);
            TestAssert.True(notes.Contains("- Zip: " + ZipName, StringComparison.Ordinal), "the notes name the zip");
            string readMe;
            using (var archive = ZipFile.OpenRead(Path.Combine(output, ZipName)))
            using (var reader = new StreamReader(archive.GetEntry("READ ME FIRST.txt")!.Open()))
                readMe = reader.ReadToEnd();
            TestAssert.True(readMe.StartsWith($"{name.ToUpperInvariant()} {Version}: READ ME FIRST\r\n", StringComparison.Ordinal), "READ ME FIRST's title: " + readMe.Split('\n')[0]);
            TestAssert.True(readMe.Contains($"Get-FileHash .\\{ZipName}\r\n", StringComparison.Ordinal) && readMe.Contains($"puts an {name} icon on your desktop", StringComparison.Ordinal),
                "READ ME FIRST names the line's zip and icon");
            // Installs from before batch 637 exist only on the line released before it (126); 132's patch.json says it has none.
            var before637 = !(release.PatchJson.TryGetProperty("homeBefore637", out var flag) && flag.ValueKind == JsonValueKind.False);
            TestAssert.Equal(before637, readMe.Contains("installed before this version", StringComparison.Ordinal), "READ ME FIRST's sentence about installs from before batch 637");
            TestAssert.Equal(WorkspaceFiles.Patch == "126", before637, "only the 1.0.0.126 line has installs from before batch 637");

            // Not the other line's: its patch.json's home name gives another zip.
            using var sibling = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(WorkspaceFiles.PatchFolder)!,
                WorkspaceFiles.Patch == "132" ? "126" : "132", "patch.json")));
            var theirs = sibling.RootElement.GetProperty("home").GetProperty("name").GetString()!.Replace(' ', '-') + $"-{Version}-win-x64.zip";
            TestAssert.False(theirs == ZipName, $"the {WorkspaceFiles.Patch} zip {ZipName} is the other line's");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    /// <summary>
    /// Batch 917: a server package (-Layout Server, through the -PartsFrom seam) writes the invite link, not Play.cmd. With -Version the link is
    /// in INVITE-LINK.txt and the summary, and it is exactly the player app's codec's link (InviteLink) for the line's tag, the address, the
    /// friends' port, the package certificate's pin and the server name. Without -Version there is no release to name: no link, and the
    /// summary says why.
    /// </summary>
    [Check("server.build-server-invite-link", TimeoutSeconds = 300)]
    public static void AServerPackageWritesTheInviteLink()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-build-server-invite");
        try
        {
            var release = Prepare(root);
            const string host = "play.example.test";
            var port = release.PatchJson.GetProperty("ports").GetProperty("remote").GetInt32();
            var output = Path.Combine(root, "server");
            var built = AirBootstrapChecks.RunPowerShell("pwsh", [.. release.Fixture, "-PublicHost", host, "-ServerName", "Test Server", "-Version", Version,
                "-PartsFrom", release.Parts, "-Output", output]);
            TestAssert.Equal(0, built.ExitCode, built.Output);
            string pin;
            using (var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                       Path.Combine(output, "platform", "platform-data", "remote", "host.pfx"), null))
                pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var link = Amberline.AirCompanion.InviteLink.Link(new(PlatformLine.ReleaseTagPrefix + Version, host, port, pin, "Test Server"));
            TestAssert.Equal(link + "\r\n", File.ReadAllText(Path.Combine(output, "INVITE-LINK.txt")), "INVITE-LINK.txt holds the codec's link");
            TestAssert.True(built.Output.Contains("Send each player this invite link", StringComparison.Ordinal) && built.Output.Contains("  " + link, StringComparison.Ordinal),
                "the summary gives the link: " + built.Output);
            TestAssert.False(Directory.EnumerateFiles(output, "Play.cmd", SearchOption.AllDirectories).Any() || built.Output.Contains("Play.cmd", StringComparison.Ordinal),
                "no Play.cmd: " + built.Output);
            var readMe = File.ReadAllText(Path.Combine(output, "README-DEPLOY.txt"));
            TestAssert.True(readMe.Contains("INVITE-LINK.txt", StringComparison.Ordinal) && !readMe.Contains("Play.cmd", StringComparison.Ordinal), readMe);
            TestAssert.False(Directory.EnumerateFiles(output, "*.exe", SearchOption.TopDirectoryOnly).Any(), "a server package has no players' app");
            // Batch 917 follow-up: BUILD.json names the release, so the package's /manage makes the same link (HostInvite.InviteFor).
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "BUILD.json"))))
                TestAssert.Equal(Version, manifest.RootElement.GetProperty("version").GetString(), "the package's BUILD.json names its release");
            TestAssert.Equal(link, HostInvite.InviteFor(Path.Combine(output, "platform"), host, port, pin, "Test Server").Link, "/manage's link is INVITE-LINK.txt's");

            // Batch 917 follow-up: without -Version (a development build), or with one before the line's first player-app release (a guide
            // build from a checkout, 0.0.N), the link names that first release.
            var first = PlatformLine.ReleaseTagPrefix + release.PatchJson.GetProperty("firstPlayerRelease").GetString();
            TestAssert.Equal(Amberline.AirCompanion.InviteLink.TagPrefix + Amberline.AirCompanion.InviteLink.FirstPlayerRelease, first, "patch.json and InviteLink agree");
            foreach (var (name, extra) in new (string, string[])[] { ("server-bare", []), ("server-guide", ["-Version", "0.0.461520"]) })
            {
                var other = Path.Combine(root, name);
                var made = AirBootstrapChecks.RunPowerShell("pwsh", [.. release.Fixture, "-PublicHost", host, "-PartsFrom", release.Parts, "-Output", other, .. extra]);
                TestAssert.Equal(0, made.ExitCode, made.Output);
                string otherPin;
                using (var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                           Path.Combine(other, "platform", "platform-data", "remote", "host.pfx"), null))
                    otherPin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                TestAssert.Equal(Amberline.AirCompanion.InviteLink.Link(new(first, host, port, otherPin, "Amberline")) + "\r\n",
                    File.ReadAllText(Path.Combine(other, "INVITE-LINK.txt")), $"{name}: the first player-app release's tag");
            }
        }
        finally
        {
            DeleteTree(root);
        }
    }


    /// <summary>
    /// Batch 682: the unzipped release's Start Amberline.cmd, run by cmd.exe with a stand-in START-HERE.cmd (the guide is never run)
    /// and a stand-in Platform (a copy of Windows' where.exe, which finds no file named --home and ends). Without options it opens
    /// the guide; with options, or started by the guide (AMBERLINE_GUIDE), it runs the Platform.
    /// </summary>
    private static void AssertGuideStart(string extracted)
    {
        File.WriteAllText(Path.Combine(extracted, "START-HERE.cmd"), "@echo the stand-in guide %*\r\n", Encoding.ASCII);
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(extracted, "app", "platform", "Amberline.Platform.exe"), overwrite: true);
        var (bare, _) = Start(null);
        TestAssert.True(bare.Contains("the stand-in guide", StringComparison.Ordinal), "a start without options opens the guide: " + bare);
        foreach (var (option, guide, what) in new[] { ((string?)null, true, "the guide's own start"), (HostInstall.ConfirmedOption, false, "Update now's start") })
        {
            var (output, exit) = Start(option, guide);
            TestAssert.False(output.Contains("the stand-in guide", StringComparison.Ordinal) || output.Contains("files are missing", StringComparison.Ordinal),
                $"{what} runs the Platform: {output}");
            // Batch 863: 9009 is cmd's "not recognized"; with NoDefaultCurrentDirectoryInExePath set the Platform's bare name was never found.
            TestAssert.True(exit != 9009, $"{what} finds the Platform: {output}");
        }

        // Batch 863: as on a hardened PC (and in some development shells), cmd never runs a program from the current folder.
        (string Output, int Exit) Start(string? option, bool guide = false)
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "/d", "/c", Path.Combine(extracted, "Start Amberline.cmd") }) start.ArgumentList.Add(argument);
            if (option is not null) start.ArgumentList.Add(option);
            start.Environment.Remove("AMBERLINE_GUIDE");
            if (guide) start.Environment["AMBERLINE_GUIDE"] = "1";
            start.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException("Start Amberline.cmd timed out");
            }
            return (stdout.Result + stderr.Result, process.ExitCode);
        }
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("git timed out in the release check");
        }
        TestAssert.Equal(0, process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Deletes a temporary tree; git's read-only object files are made writable first.</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
