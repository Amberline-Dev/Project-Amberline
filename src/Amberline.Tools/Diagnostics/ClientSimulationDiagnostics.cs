namespace Amberline.Tools.Diagnostics;

/// <summary>The percentile helper <c>triage</c> uses (the client-simulation tool is not part of this tree).</summary>
public static class ClientSimulationDiagnostics
{
    internal static double NearestRankPercentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0.0;
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
