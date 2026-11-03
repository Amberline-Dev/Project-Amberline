using System.Globalization;
using System.Text;
using System.Text.Json;
using Amberline.Tools.Telemetry;

namespace Amberline.Tests;

/// <summary>
/// <c>telemetry repl-compare</c> on small synthetic files: a pass, a mismatch, a unit the server says the client cannot see, the
/// owner-only fields of another hero, the regeneration allowance, a volatile unit and unusable input.
/// </summary>
public static class ReplicationCompareChecks
{
    private const long Second = 1_000_000_000L;
    private const long Epoch = 1_700_000_000_000_000_000L;
    private const uint Human = 0x40000001, Ally = 0x40000002, Minion = 0x40010005, Hidden = 0x40000006;

    private sealed record U(uint Id, string Class, (string Name, double Value)[] Fields, bool Visible = true, bool Alive = true, bool IsHuman = false,
        bool VolatileUnit = false, string[]? VolatileFields = null);

    private static readonly (string, double)[] HumanFields =
    [
        ("mHP", 500.0), ("mMaxHP", 600.0), ("mHPRegenRate", 2.0), ("mMP", 300.0), ("mPARRegenRate", 1.5), ("mArmor", 25.5),
        ("ActionState", 7), ("mExp", 280.0), ("mGold", 1200.0), ("ManaCost_0", 60.0),
    ];

    private static readonly (string, double)[] AllyFields =
    [
        ("mHP", 450.0), ("mHPRegenRate", 1.0), ("mArmor", 22.0), ("ActionState", 7), ("mExp", 0.0), ("mGold", 0.0), ("ManaCost_0", 70.0),
    ];

    private static readonly (string, double)[] MinionFields = [("mHP", 380.0), ("mArmor", 10.0), ("IsTargetable", 1)];


    // ----- synthetic files -----

    private static U[] Units((string, double)[]? human = null, (string, double)[]? ally = null, (string, double)[]? minion = null, U[]? extra = null) =>
    [
        new U(Human, "hero", human ?? HumanFields, IsHuman: true),
        new U(Ally, "hero", ally ?? AllyFields),
        new U(Minion, "minion", minion ?? MinionFields),
        new U(Hidden, "hero", [("mHP", 77.0), ("mArmor", 9.0)], Visible: false),
        .. extra ?? [],
    ];

    private static string Checkpoint(int n, string phase, double atSeconds, U[] units, double? quiet = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{{\"kind\":\"truth\",\"n\":{n},\"phase\":\"{phase}\",\"wall_ns\":{Epoch + (long)(atSeconds * Second)},\"game_t\":{atSeconds + 100.0:R}");
        if (quiet is double q) text.Append(CultureInfo.InvariantCulture, $",\"quiet_until_ns\":{Epoch + (long)((atSeconds + q) * Second)}");
        text.Append(",\"units\":[");
        text.Append(string.Join(',', units.Select(unit =>
            $"{{\"net_id\":{unit.Id},\"class\":\"{unit.Class}\",\"team\":100,\"visible_to_human\":{Json(unit.Visible)},\"alive\":{Json(unit.Alive)}" +
            (unit.IsHuman ? ",\"is_human_hero\":true" : "") + (unit.VolatileUnit ? ",\"volatile_unit\":true" : "") +
            (unit.VolatileFields is { } volatileFields ? ",\"volatile_fields\":[" + string.Join(',', volatileFields.Select(name => $"\"{name}\"")) + "]" : "") +
            $",\"fields\":{Fields(unit.Fields)}}}")));
        return text.Append("]}").ToString();
    }

    /// <summary>One sample per offset (seconds after the first truth checkpoint's wall time).</summary>
    private static string[] Samples(U[] units, params double[] offsets) => [.. offsets.Select(offset =>
        $"{{\"kind\":\"stats\",\"wall_ns\":{Epoch + (long)(offset * Second)},\"monotonic_ns\":{(long)(offset * Second)},\"pid\":{(offset > 30 ? 5002 : 5001)},\"units\":[" +
        string.Join(',', units.Where(unit => unit.Visible).Select(unit =>
            $"{{\"net_id\":{unit.Id},\"class\":\"{unit.Class}\",\"team\":100,\"dead\":false,\"active\":true,\"fields\":{Fields(unit.Fields)}}}")) +
        "],\"skipped\":0}")];

    private static string Fields((string Name, double Value)[] fields) => "{" + string.Join(',', fields.Select(field =>
        string.Create(CultureInfo.InvariantCulture, $"\"{field.Name}\":{field.Value:R}"))) + "}";

    private static string Json(bool value) => value ? "true" : "false";

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "repl-compare-" + Guid.NewGuid().ToString("N"))).FullName;
        public string StatsPath => Path.Combine(_directory, "stats.jsonl");
        public string Stats2Path => Path.Combine(_directory, "stats2.jsonl");
        public string TruthPath => Path.Combine(_directory, "replication-truth.jsonl");
        public void Truth(params string[] lines) => File.WriteAllText(TruthPath, string.Join('\n', lines) + "\n");

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
