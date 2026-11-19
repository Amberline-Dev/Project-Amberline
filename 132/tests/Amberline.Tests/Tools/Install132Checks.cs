using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.AirCompanion;
using Amberline.Tests.Air;

namespace Amberline.Tests;

/// <summary>
/// Batch 776: the 1.0.0.132 install (tools/install-132.ps1 and the start-132.ps1 it installs), run by Windows PowerShell 5.1 as START-132.cmd
/// runs it. Temp folders only: no check creates the owner's AIR132-PREVIEW install or C:\rads132, and none starts a server or a client. The 1.0.0.126 paths
/// named below are only ever refused, before anything is read.
/// </summary>
public static class Install132Checks
{


    private static List<Process> HelperCopies(string image) =>
        Process.GetProcessesByName("PING").Where(process =>
        {
            try { return string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase); }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        }).ToList();

    private static (int ExitCode, string Output) Run(string[] arguments) =>
        AirBootstrapChecks.RunPowerShell(AirPlayFileChecks.WindowsPowerShell(), arguments);

    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A harmless process whose image lies in <paramref name="folder"/>: a copy of the system's ping, pinging loopback for two minutes.</summary>
    private static Process Probe(string folder)
    {
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "PING.EXE");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), image, overwrite: true);
        var start = new ProcessStartInfo(image, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        var probe = Process.Start(start) ?? throw new InvalidOperationException("the probe did not start");
        probe.BeginOutputReadLine();
        return probe;
    }

    private static void Stop(List<Process> probes)
    {
        foreach (var probe in probes)
        {
            try { if (!probe.HasExited) { probe.Kill(); probe.WaitForExit(5000); } } catch (InvalidOperationException) { }
            probe.Dispose();
        }
        probes.Clear();
    }
}
