using System.Text.Json;
using Amberline.Tools.Telemetry;

namespace Amberline.Tests;

/// <summary>Direct coverage for the JSONL capture append boundary.</summary>
public static class CaptureWriterChecks
{
    private const string Build = "1111111111111111111111111111111111111111111111111111111111111111";




    private static string TempPath() => Path.Combine(Path.GetTempPath(),
        "amberline-capture-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
