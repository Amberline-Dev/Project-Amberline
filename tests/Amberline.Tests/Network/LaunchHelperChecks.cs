using System.Diagnostics;

namespace Amberline.Tests.Network;

public static class LaunchHelperChecks
{


    private static (int ExitCode, string Output) Invoke(string shell, string script, string[] arguments)
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
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start PowerShell");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("launch helper PowerShell invocation timed out");
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


    private static string FindWorkspaceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var direct = Path.Combine(current.FullName, relativePath);
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(current.FullName, "emu", relativePath);
                if (File.Exists(nested)) return nested;
            }
        }
        throw new FileNotFoundException(relativePath);
    }
}
