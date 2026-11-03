using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Tools.Scenarios;

using Amberline.Tools.Telemetry;

namespace Amberline.Tests;

/// <summary>
/// S-repl on the headless host: every phase happens (the hero is hurt, spends mana, levels, shops, stuns, dies and returns, camps and
/// turrets are hit), each ends in a checkpoint with a truth line, and <c>telemetry repl-compare</c> reads those lines.
/// </summary>
public static class ScenarioReplChecks
{
    private const int Seed = 1;


    private static JsonElement Unit(JsonElement[] truth, string phase, Func<JsonElement, bool> which) =>
        truth.First(line => line.GetProperty("phase").GetString() == phase).GetProperty("units").EnumerateArray().First(which);

    private static Dictionary<string, double> Fields(JsonElement[] truth, string phase, Func<JsonElement, bool> which) =>
        Unit(truth, phase, which).GetProperty("fields").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetDouble());

    /// <summary>
    /// What a client that holds exactly the truth would sample, 3.1 s and 5.0 s after each checkpoint: every unit the server says is
    /// visible, with the truth's fields. <paramref name="tamper"/> changes the first visible hero's armor at the first checkpoint (in both samples).
    /// </summary>
    private static IEnumerable<string> ClientLines(JsonElement[] truth, bool tamper)
    {
        var firstCheckpoint = true;
        foreach (var line in truth)
        {
            var wall = line.GetProperty("wall_ns").GetInt64();
            foreach (var offset in new[] { 3.1, 5.0 })
            {
                var tampered = !(tamper && firstCheckpoint);
                var units = new JsonArray();
                foreach (var unit in line.GetProperty("units").EnumerateArray())
                {
                    if (!unit.GetProperty("visible_to_human").GetBoolean()) continue;
                    var fields = JsonNode.Parse(unit.GetProperty("fields").GetRawText())!.AsObject();
                    if (!tampered && unit.GetProperty("class").GetString() == "hero" && unit.GetProperty("alive").GetBoolean() &&
                        fields.ContainsKey("mArmor"))
                    {
                        fields["mArmor"] = fields["mArmor"]!.GetValue<double>() + 5.0;
                        tampered = true;
                    }
                    units.Add(new JsonObject
                    {
                        ["net_id"] = unit.GetProperty("net_id").GetUInt32(), ["class"] = unit.GetProperty("class").GetString(),
                        ["team"] = unit.GetProperty("team").GetInt32(), ["dead"] = !unit.GetProperty("alive").GetBoolean(), ["active"] = true,
                        ["fields"] = fields,
                    });
                }
                var sample = new StringBuilder();
                sample.Append(string.Create(CultureInfo.InvariantCulture, $"{{\"kind\":\"stats\",\"wall_ns\":{wall + (long)(offset * 1e9)},\"monotonic_ns\":1,\"units\":"));
                sample.Append(units.ToJsonString()).Append(",\"skipped\":0}");
                yield return sample.ToString();
            }
            firstCheckpoint = false;
        }
    }
}
