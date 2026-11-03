using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amberline.Tools.Diagnostics;

public static class TriageReport
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--compare")
            {
                Console.Write(Compare(TriageAnalysis.Read(args[1]), TriageAnalysis.Read(args[2])));
                return 0;
            }
            if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("triage requires a game-log directory, or --compare dirA dirB");
            string? output = null;
            var window = 30.0;
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !flags.Add(args[i]))
                    throw new ArgumentException("triage options need one value and cannot be repeated");
                switch (args[i])
                {
                    case "--out": output = args[i + 1]; break;
                    case "--window":
                        if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out window))
                            throw new ArgumentException("triage --window requires seconds");
                        break;
                    default: throw new ArgumentException("triage accepts only --out and --window");
                }
            }
            if (output is not null && !string.Equals(Path.GetExtension(output), ".md", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("triage --out must be a Markdown (.md) file, preserving capture inputs");
            var report = Render(TriageAnalysis.Read(args[0], window));
            if (output is null) Console.Write(report);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, report, new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Exception messages can contain the owner's absolute path or raw JSON.
            Console.Error.WriteLine("triage: unable to read the capture or write the report; check input files and output permissions");
            return 2;
        }
    }

    public static string Render(TriageAnalysis report)
    {
        var lines = new List<string>
        {
            "# Post-game triage", "",
            $"Game: `{report.GameId}`; build: `{report.Build}`" + (report.Seed is long seed ? $"; seed: `{seed}`" : "") + ".",
            $"HUD offset: {(report.HudOffset is double offset ? F(offset) + " s (0x5F StartGame)" : "unknown; times use server t")}; game length: {F(report.Length)} s{(report.EndTime is null ? " (capture end; match end missing)" : "") }.",
            "Heroes: " + (report.Heroes.Count == 0 ? "none recorded" : string.Join("; ", report.Heroes.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Value.Champion} / {pair.Value.Team} / 0x{pair.Key:X8}"))) + ".",
            "", "## Look here first", "",
        };
        if (report.Worst.Count == 0) lines.Add("none recorded");
        foreach (var moment in report.Worst) lines.Add("- " + Describe(report, moment));
        lines.AddRange(["", $"## Marks (+/-{F(report.Window)} s)", ""]);
        if (report.Marks.Count == 0) lines.Add("none recorded");
        foreach (var mark in report.Marks)
        {
            var groups = new[] { "minion stall", "hero stall", "hero detour", "divergence", "invariant", "client assert/error" };
            lines.Add($"- **{Time(report, mark.Time)} — {Escape(report.Redact(mark.Note))}**: " +
                string.Join("; ", groups.Select(group => mark.Worst.TryGetValue(group, out var moment)
                    ? $"{group}: {Describe(report, moment)}" : $"{group}: none recorded")) +
                $". Window totals: {mark.InvariantEvents} invariant events" +
                (mark.InvariantKinds.Count == 0 ? "" : " (" + string.Join(", ", mark.InvariantKinds.Select(pair => $"{pair.Key}: {pair.Value}")) + ")") +
                $"; {mark.ClientErrors} client asserts/errors.");
        }
        lines.AddRange(["", "## Game-wide metrics", "",
            "| Metric | Count | Median | p95 | Max | Total |",
            "|---|---:|---:|---:|---:|---:|"]);
        foreach (var (name, metric) in report.Metrics)
            lines.Add($"| {name} | {metric.Count} | {Quantile(metric, metric.Median)} | {Quantile(metric, metric.P95)} | {Value(metric, metric.Maximum)} | {Value(metric, metric.Total)} |");
        lines.Add($"Near-wave hero input orders: {report.NearWaveOrders}; fidelity batches: {report.FidelityBatches}.");
        lines.Add("Top five per stall class (HUD time, netid, seconds):");
        foreach (var index in new[] { 0, 1, 2, 7, 12 })
        {
            var metric = report.Metrics[TriageAnalysis.MetricNames[index]];
            lines.Add($"- {TriageAnalysis.MetricNames[index]}: " + (metric.Worst.Count == 0 ? "none recorded" :
                string.Join("; ", metric.Worst.Select(moment => $"{Time(report, moment.Time)} / 0x{moment.Unit:X8} / {F(moment.Value)}"))));
        }
        lines.Add("Successful waypoint enqueues by emulator reason (includes each observer): " +
            (report.PacketReasons.Count == 0 ? "none recorded" : string.Join("; ", report.PacketReasons.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value}"))) + ".");
        lines.AddRange(["", "## Invariant summary", ""]);
        if (report.Invariants.Count == 0) lines.Add("none recorded");
        else
        {
            lines.AddRange(["| Kind | Events | Ends | Completed duration (s) | Open lower bound (s) |", "|---|---:|---:|---:|---:|"]);
            foreach (var (kind, summary) in report.Invariants.OrderBy(pair => pair.Key))
                lines.Add($"| {kind} | {summary.Count} | {summary.Ends} | {F(summary.Duration)} | {F(summary.OpenDuration)} |");
            lines.Add("Worst five invariant observations/episodes:");
            foreach (var moment in report.Invariants.Values.SelectMany(summary => summary.Worst).OrderByDescending(moment => moment.Score).ThenBy(moment => moment.Time).Take(5))
                lines.Add("- " + Describe(report, moment));
        }
        lines.AddRange(["", "## Packet-rule summary", ""]);
        lines.Add(report.PacketsChecked is long checkedPackets
            ? $"Packet checker ran over {checkedPackets} per-peer packets."
            : "No packet-checker coverage recorded (only headless `match` runs check packet streams).");
        if (report.PacketViolations.Count == 0) lines.Add("none recorded");
        else
        {
            lines.AddRange(["", "| Kind | Violations |", "|---|---:|"]);
            foreach (var (kind, summary) in report.PacketViolations.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal))
                lines.Add($"| {Escape(kind)} | {summary.Count} |");
            lines.Add("First five violations per kind (HUD time, peer, opcode, netid):");
            foreach (var (kind, summary) in report.PacketViolations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                foreach (var violation in summary.First)
                    lines.Add($"- {Escape(kind)} at {Time(report, violation.Time)}: peer {violation.Peer}, {Escape(violation.Opcode)}" +
                        (violation.NetId == 0 ? "" : $", 0x{violation.NetId:X8}") + (violation.Detail.Length == 0 ? "" : " - " + Escape(violation.Detail)));
        }
        lines.AddRange(["", "## Client log", ""]);
        lines.Add(!report.ClientLogPresent ? "none recorded (client_r3dlog.txt missing)" : report.ClientClockOffset is double clientOffset
            ? $"Clock: server t = client elapsed + {F(clientOffset)} s, aligned at StartGame receipt (approximate; receipt latency unknown)."
            : "Clock: unmapped; no matching StartGame anchor. Client times cannot be assigned to mark windows.");
        if (report.ClientErrors.Count == 0) lines.Add("none recorded");
        // Pack unusually noisy native logs without dropping distinct fingerprints.
        foreach (var chunk in report.ClientErrors.Values.OrderBy(error => error.Line).Chunk(Math.Max(1, (int)Math.Ceiling(report.ClientErrors.Count / 25.0))))
            lines.Add("- " + string.Join("; ", chunk.Select(error => $"{error.Key}: {error.Count}, first {Time(report, error.Time)}, r3dlog line {error.Line}")));
        lines.AddRange(["", "## Measurement limits", "",
            "- Sampled minion stalls: adjacent positions within 0.1 unit, >=0.3 s; hero stalls: <=1 unit with retained routes. Gaps >1 s split intervals. Post-match samples are excluded.",
            "- No-target stalls require empty routes; wrong-target stalls require a known enemy collider different from the target and stop at a basic attack. Control/Teleport can explain stalls; these are not proven bugs.",
            "- Near-wave: a living sampled minion within 400 units, snapshot age <=1 s. Detours measure each published 0xC1 route/chord >=50 units during those inputs. Hero client gaps use logged client-report orders; reference error is a server estimate.",
            "- Death delay ends at the next moving 0xC1 or basic attack (not pure AI latency). Merge/park durations are server t from the hold to its matching resume; a hold that ends otherwise (the minion moves on, dies, or the capture ends) is not measured. Silent initial merge deferrals are not identifiable.",
            "- Quantiles use exact samples up to 8192 values, then a deterministic 8192-value reservoir (approximate median/p95); counts, totals and maxima remain exact. p95 uses nearest rank.",
            "- Intent/enqueue joins use netid + sync_id and monotonic clocks, deduplicating observer copies; up to 128 recent pairs per entity, expired after 5 s. ENet enqueue is not client receipt.",
            $"- Unmatched/expired waypoint pairs: {report.UnmatchedWaypoints}; holds without a matching resume: {report.CensoredHolds}; collider observations with unknown teams: {report.MissingColliderTeams}. Missing coverage means unknown, not zero defects.",
            "- Ranking is triage policy: duration in seconds, detour ratio, gap/225, enqueue ms/100, invariant value/limit, assert=5. Values with different units are not directly comparable; inspect each metric's maxima.",
            "- Packet-rule lines (cat PACKET) are written only by headless `match` runs, which check every per-peer stream; a live game's packets are not checked, so \"none recorded\" there means unchecked, not clean. The first five per kind are the earliest by time.",
            "- Client messages are represented by stable fingerprints and source line numbers for privacy. Notes redact known player names, user paths, IPs and e-mail addresses. Native acceptance remains separate.",
        ]);
        if (report.IncompleteTail) lines.Add("- Incomplete final JSONL row ignored; the capture is partial.");
        return report.Redact(string.Join('\n', lines) + "\n");
    }

    public static string Compare(TriageAnalysis a, TriageAnalysis b)
    {
        var lines = new List<string>
        {
            "# Triage comparison", "",
            $"A: `{a.GameId}` / `{a.Build}` / {F(a.Length)} s; B: `{b.GameId}` / `{b.Build}` / {F(b.Length)} s. Delta = B - A.",
            "Each metric cell is count / median / p95 / max / total; units are in the metric label. Missing observations are n/a.", "",
            "| Metric | A | B | Delta |", "|---|---|---|---|",
        };
        foreach (var name in TriageAnalysis.MetricNames)
        {
            var left = a.Metrics[name];
            var right = b.Metrics[name];
            var delta = left.Count == 0 || right.Count == 0 ? "n/a" :
                string.Join(" / ", new[] { right.Median - left.Median, right.P95 - left.P95,
                    right.Maximum - left.Maximum, right.Total - left.Total }.Select(Signed));
            lines.Add($"| {name} | {Cell(left)} | {Cell(right)} | {Signed(right.Count - left.Count)} / {delta} |");
        }
        lines.AddRange(["", "| Counter | A | B | Delta |", "|---|---:|---:|---:|"]);
        AddCounter("Near-wave hero orders", a.NearWaveOrders, b.NearWaveOrders);
        foreach (var key in a.PacketReasons.Keys.Union(b.PacketReasons.Keys).Order())
            AddCounter("Waypoint enqueue: " + key, a.PacketReasons.GetValueOrDefault(key), b.PacketReasons.GetValueOrDefault(key));
        foreach (var key in a.Invariants.Keys.Union(b.Invariants.Keys).Order())
        {
            var left = a.Invariants.GetValueOrDefault(key);
            var right = b.Invariants.GetValueOrDefault(key);
            AddCounter("Invariant " + key + " events", left?.Count ?? 0, right?.Count ?? 0);
            AddCounter("Invariant " + key + " completed seconds", left?.Duration ?? 0, right?.Duration ?? 0);
            AddCounter("Invariant " + key + " open lower-bound seconds", left?.OpenDuration ?? 0, right?.OpenDuration ?? 0);
        }
        foreach (var key in a.PacketViolations.Keys.Union(b.PacketViolations.Keys).Order(StringComparer.Ordinal))
            AddCounter("Packet violations: " + key, a.PacketViolations.GetValueOrDefault(key)?.Count ?? 0, b.PacketViolations.GetValueOrDefault(key)?.Count ?? 0);
        AddCounter("Client asserts/errors", a.ClientErrors.Values.Sum(error => error.Count), b.ClientErrors.Values.Sum(error => error.Count));
        lines.AddRange(["", "Counts are raw, not normalized for match length, roster, trace coverage or input density. Missing marks/invariants in older logs do not prove absence of defects.",
            "Quantiles are approximate only above 8192 observations. Compare the individual reports for coverage, definitions, open intervals and the worst moments."]);
        return b.Redact(a.Redact(string.Join('\n', lines) + "\n"));

        void AddCounter(string name, double left, double right) => lines.Add($"| {name} | {F(left)} | {F(right)} | {Signed(right - left)} |");
    }

    private static string Cell(TriageAnalysis.Metric metric) => $"{metric.Count} / {Quantile(metric, metric.Median)} / {Quantile(metric, metric.P95)} / {Value(metric, metric.Maximum)} / {Value(metric, metric.Total)}";
    private static string Describe(TriageAnalysis report, TriageAnalysis.Moment moment) =>
        $"{Time(report, moment.Time)}{(moment.End > moment.Time ? "–" + Time(report, moment.End) : "")}: {moment.Kind}, {F(moment.Value)}" +
        (moment.Unit == 0 ? "" : $", 0x{moment.Unit:X8}");
    private static string Time(TriageAnalysis report, double? time)
    {
        if (time is not double value) return "HUD unknown";
        if (report.HudOffset is not double offset) return $"server t={F(value)}";
        var milliseconds = (long)Math.Round((value - offset) * 1000);
        var absolute = Math.Abs(milliseconds);
        return $"{(milliseconds < 0 ? "-" : "")}{absolute / 60000:00}:{absolute % 60000 / 1000:00}.{absolute % 1000:000}";
    }
    private static string Escape(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|")
        .Replace("<", "&lt;").Replace(">", "&gt;").Replace("*", "\\*").Replace("`", "\\`");
    private static string Value(TriageAnalysis.Metric metric, double value) => metric.Count == 0 ? "n/a" : F(value);
    private static string Quantile(TriageAnalysis.Metric metric, double value) => (metric.Approximate ? "~" : "") + Value(metric, value);
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Signed(double value) => value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
}
