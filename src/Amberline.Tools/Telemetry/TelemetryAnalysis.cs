using System.Text.Json;

namespace Amberline.Tools.Telemetry;

/// <summary>The JSON-lines readers <c>triage</c> uses for a match's session log (the client telemetry tools are not part of this tree).</summary>
public static class TelemetryAnalyzer
{
    internal static IEnumerable<(int Line, string Text)> ReadLines(string path)
    {
        var line = 0;
        foreach (var text in File.ReadLines(path))
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"{Path.GetFileName(path)}:{line}: blank record");
            yield return (line, text);
        }
    }

    internal static JsonElement ParseElement(string text, string file, int line)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException exception) { throw new InvalidDataException($"{file}:{line}: invalid JSON", exception); }
    }
}
