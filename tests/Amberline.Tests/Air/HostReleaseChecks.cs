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
    private const string ZipName = "Amberline-1.4.0-win-x64.zip";


    /// <summary>
    /// Batch 682: the unzipped release's Start Amberline.cmd, run by cmd.exe with a stand-in START-HERE.cmd (the guide is never run)
    /// and a stand-in Platform (a copy of Windows' where.exe, which finds no file named --home and ends). Without options it opens
    /// the guide; with options, or started by the guide (AMBERLINE_GUIDE), it runs the Platform.
    /// </summary>
    private static void AssertGuideStart(string extracted)
    {
        File.WriteAllText(Path.Combine(extracted, "START-HERE.cmd"), "@echo the stand-in guide %*\r\n", Encoding.ASCII);
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(extracted, "app", "platform", "Amberline.Platform.exe"), overwrite: true);
        var bare = Start(null);
        TestAssert.True(bare.Contains("the stand-in guide", StringComparison.Ordinal), "a start without options opens the guide: " + bare);
        foreach (var (option, guide, what) in new[] { ((string?)null, true, "the guide's own start"), (HostInstall.ConfirmedOption, false, "Update now's start") })
        {
            var output = Start(option, guide);
            TestAssert.False(output.Contains("the stand-in guide", StringComparison.Ordinal) || output.Contains("files are missing", StringComparison.Ordinal),
                $"{what} runs the Platform: {output}");
        }

        string Start(string? option, bool guide = false)
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
            using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException("Start Amberline.cmd timed out");
            }
            return stdout.Result + stderr.Result;
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
