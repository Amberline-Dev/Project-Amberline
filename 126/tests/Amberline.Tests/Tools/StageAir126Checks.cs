using Amberline.AirCompanion;
using Amberline.Tests.Air;

namespace Amberline.Tests;

/// <summary>
/// Batch 914 (owner, 19 November 2026: the desktop shortcut runs the whole flow, launcher, AIR, game): tools/stage-air126.ps1, which stages the
/// operator's install and its START-AIR, gives the stage the 2011 launcher, so the companion's --desktop start opens it first. Temp folders only;
/// the real companion of this build makes the AIR copy from a stock-layout fixture (its --prepare-only), and nothing is started.
/// </summary>
public static class StageAir126Checks
{


    private static (int ExitCode, string Output) Run(string[] arguments) =>
        AirBootstrapChecks.RunPowerShell(AirPlayFileChecks.WindowsPowerShell(), arguments);

    private static void Put(string path, string text) => Put(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
