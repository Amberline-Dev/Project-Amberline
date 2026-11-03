using System.Diagnostics;

namespace Amberline.Tests.Network;

public static class GuestLauncherChecks
{


    private static (int ExitCode, string Output) Invoke(string shell, string script,
        string exeDir, params string[] tail)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-ExeDir");
        start.ArgumentList.Add(exeDir);
        start.ArgumentList.Add("-DryRun");
        foreach (var value in tail) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start PowerShell");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("guest launcher PowerShell invocation timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static bool ExecutableOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, name)) ||
                OperatingSystem.IsWindows() && File.Exists(Path.Combine(directory, name + ".exe")))
                return true;
        }
        return false;
    }

    private static string FindWorkspaceFile(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(fileName);
    }
}
