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
/// dirty states are made here.
/// </summary>
public static class HostReleaseChecks
{
    private const string Version = "1.4.0";
    private const string ZipName = "Amberline-1.4.0-win-x64.zip";


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
