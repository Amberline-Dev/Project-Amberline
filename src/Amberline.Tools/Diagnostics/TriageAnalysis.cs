using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Tools.Telemetry;

namespace Amberline.Tools.Diagnostics;

// DIAGNOSTICS: retail has no emulator post-game triage. These offline summaries
// measure recorded diagnostics; they neither change gameplay nor certify client rendering.
public sealed class TriageAnalysis
{
    public static readonly string[] MetricNames =
    [
        "Minion retained-route stall (s)", "Minion no-target empty-route stall (s)",
        "Minion wrong-target enemy-collider stall (s)", "Lane-merge hold (s)",
        "Collision park (s)", "Target-death to next action (s)",
        "Near-wave hero detour ratio", "Near-wave hero stall (s)",
        "Hero client/server gap (units)", "Near-wave hero client/server gap (units)",
        "Minion reference error (units)", "Waypoint intent to enqueue (ms)",
        "Hero retained-route stall (s)",
        "Minion waypoint intent to enqueue (ms)",
    ];
    public sealed record Moment(string Kind, double Time, double End, uint Unit, double Value, double Score);
    public sealed record Hero(string Champion, string Team);
    public sealed class Mark(double time, string note)
    {
        public double Time { get; } = time;
        public string Note { get; } = note;
        public Dictionary<string, Moment> Worst { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> InvariantKinds { get; } = new(StringComparer.Ordinal);
        public long InvariantEvents { get; internal set; }
        public long ClientErrors { get; internal set; }
    }
    public sealed class InvariantSummary
    {
        public long Count { get; internal set; }
        public long Ends { get; internal set; }
        public double Duration { get; internal set; }
        public double OpenDuration { get; internal set; }
        public List<Moment> Worst { get; } = [];
    }
    // DIAGNOSTICS: PACKET lines come from the emulator's packet-stream checker (headless matches only).
    public sealed record PacketViolation(double Time, int Peer, string Opcode, uint NetId, string Detail);
    public sealed class PacketSummary
    {
        public const int KeepFirst = 5;
        public long Count { get; internal set; }
        /// <summary>The earliest violations of this kind, in time order.</summary>
        public List<PacketViolation> First { get; } = [];
    }
    public sealed class ClientError(string key, int line, double? time)
    {
        public string Key { get; } = key;
        public int Line { get; } = line;
        public double? Time { get; } = time;
        public long Count { get; internal set; }
    }
    public sealed class Metric
    {
        public const int SampleLimit = 8192;
        private readonly List<double> _sample = [];
        private readonly Random _random = new(126);
        public long Count { get; private set; }
        public double Total { get; private set; }
        public double Maximum { get; private set; }
        public int RetainedSamples => _sample.Count;
        public bool Approximate => Count > SampleLimit;
        public List<Moment> Worst { get; } = [];
        internal void Add(double value, Moment moment)
        {
            Count++;
            Total += value;
            Maximum = Math.Max(Maximum, value);
            // ponytail: fixed reservoir bounds long-game memory; use an external
            // sort if exact quantiles above 8192 observations become necessary.
            if (_sample.Count < SampleLimit) _sample.Add(value);
            else
            {
                var index = _random.NextInt64(Count);
                if (index < SampleLimit) _sample[(int)index] = value;
            }
            KeepWorst(Worst, moment);
        }
        public double Median
        {
            get
            {
                var sorted = _sample.Order().ToArray();
                return sorted.Length == 0 ? 0 :
                    (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
        }
        public double P95 => ClientSimulationDiagnostics.NearestRankPercentile(_sample.Order().ToArray(), 0.95);
    }

    private sealed class UnitState
    {
        public double Seen;
        public double SampleTime = double.NaN;
        public (double X, double Z) Position;
        public bool Hero;
        public bool Minion;
        public bool Alive = true;
        public string Team = "unknown";
        public uint Target;
        public uint SampleTarget;
        public bool Route;
        public bool WrongCollider;
        public bool Attacking;
        public bool NearWave;
        public double? DeathAt;
        public double? MergeAt;
        public double? ParkAt;
        public double[] Stalls = [double.NaN, double.NaN, double.NaN, double.NaN];
        public Dictionary<uint, WaypointPair> Waypoints { get; } = [];
    }
    private sealed class WaypointPair
    {
        public double Seen;
        public double? Intent;
        public double? Enqueue;
        public bool Counted;
        public bool IntentProcessed;
        public bool PacketProcessed;
    }
    private readonly Dictionary<uint, UnitState> _units = [];
    private readonly Dictionary<(uint Unit, string Kind), double> _openInvariants = [];
    private readonly HashSet<string> _privateNames = new(StringComparer.OrdinalIgnoreCase);
    private double? _monoOrigin;
    private double _lastTime;
    private double _nextSweep;
    public string GameId { get; private set; } = "unknown";
    public string Build { get; private set; } = "unknown";
    public double? HudOffset { get; private set; }
    public double? EndTime { get; private set; }
    public double Length => Math.Max(0, (EndTime ?? _lastTime) - (HudOffset ?? 0));
    public double Window { get; }
    public Dictionary<uint, Hero> Heroes { get; } = [];
    public List<Mark> Marks { get; } = [];
    public Dictionary<string, Metric> Metrics { get; } = MetricNames.ToDictionary(name => name, _ => new Metric());
    public Dictionary<string, long> PacketReasons { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, InvariantSummary> Invariants { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PacketSummary> PacketViolations { get; } = new(StringComparer.Ordinal);
    /// <summary>Packets the checker saw (headless meta.json); null when no checker ran for this log.</summary>
    public long? PacketsChecked { get; private set; }
    /// <summary>RNG seed of the match: meta.json of a headless match, or a live log's rng_seed record.</summary>
    public long? Seed { get; private set; }
    public Dictionary<string, ClientError> ClientErrors { get; } = new(StringComparer.Ordinal);
    public List<Moment> Worst { get; } = [];
    public long FidelityBatches { get; private set; }
    public long NearWaveOrders { get; private set; }
    public long UnmatchedWaypoints { get; private set; }
    public long CensoredHolds { get; private set; }
    public long MissingColliderTeams { get; private set; }
    public int PeakUnits { get; private set; }
    public int PeakPairsPerUnit { get; private set; }
    public bool ClientLogPresent { get; private set; }
    public double? ClientClockOffset { get; private set; }
    public bool IncompleteTail { get; private set; }

    private TriageAnalysis(double window) => Window = window;

    public static TriageAnalysis Read(string directory, double window = 30)
    {
        if (!double.IsFinite(window) || window < 0 || window > 3600)
            throw new ArgumentException("triage window must be between 0 and 3600 seconds");
        var result = new TriageAnalysis(window);
        result.AddPrivateName(Environment.UserName);
        var meta = Path.Combine(directory, "meta.json");
        if (File.Exists(meta))
        {
            using var stream = File.OpenRead(meta);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            result.GameId = Guid.TryParse(Text(root, "session_id"), out var id) ? id.ToString("N") : "unknown";
            var build = Text(root, "build") ?? Text(root, "commit") ?? "unknown";
            result.Build = Regex.IsMatch(build, @"\A[A-Za-z0-9.+_-]{1,160}\z") ? build : "redacted";
            result._monoOrigin = Number(root, "t0_mono_ns") ?? Number(root, "mono_ns");
            result.Seed = Number(root, "seed") is double seed ? (long)seed : null;
            result.PacketsChecked = Number(root, "packets_checked") is double checkedPackets ? (long)checkedPackets : null;
        }
        var matchResult = Path.Combine(directory, "match-result.json");
        if (File.Exists(matchResult))
        {
            using var stream = File.OpenRead(matchResult);
            using var document = JsonDocument.Parse(stream);
            result.ReadRoster(document.RootElement);
        }
        var session = Path.Combine(directory, "session.jsonl");
        // Pass one keeps only the index needed for windows, clocks and privacy.
        // Pass two never retains JSON records or complete sample trajectories.
        foreach (var root in result.Records(session)) result.Index(root);
        foreach (var root in result.Records(session)) result.Observe(root);
        foreach (var (id, unit) in result._units) result.FinishUnit(id, unit);
        foreach (var (key, start) in result._openInvariants)
            result.Invariants[key.Kind].OpenDuration += Math.Max(0, (result.EndTime ?? result._lastTime) - start);
        result._units.Clear();
        result._openInvariants.Clear();
        result.ReadClientLog(Path.Combine(directory, "client_r3dlog.txt"));
        return result;
    }

    private IEnumerable<JsonElement> Records(string path)
    {
        // Reuse the telemetry line reader/parser, including its line-numbered
        // errors. As in replay diagnostics, only an incomplete final row is tolerated.
        using var lines = TelemetryAnalyzer.ReadLines(path).GetEnumerator();
        var more = lines.MoveNext();
        while (more)
        {
            var (line, text) = lines.Current;
            more = lines.MoveNext();
            JsonElement value = default;
            try { value = TelemetryAnalyzer.ParseElement(text, "session.jsonl", line); }
            catch (InvalidDataException) when (!more) { IncompleteTail = true; }
            if (value.ValueKind == JsonValueKind.Undefined) yield break;
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"session.jsonl:{line}: expected object");
            yield return value;
        }
    }

    private void Index(JsonElement root)
    {
        var t = Number(root, "t") ?? 0;
        _lastTime = Math.Max(_lastTime, t);
        _monoOrigin ??= Number(root, "mono_ns") is double mono ? mono - t * 1e9 : null;
        if (GameId == "unknown" && Guid.TryParse(Text(root, "session_id"), out var id)) GameId = id.ToString("N");
        var cat = Text(root, "cat");
        var msg = Text(root, "msg");
        if (Seed is null && Number(root, "rng_seed") is double loggedSeed) Seed = (long)loggedSeed;
        if (cat == "PKT" && Outbound(root) && Opcode(root) == 0x5F) HudOffset ??= t;
        if (msg == "match.ended")
        {
            EndTime ??= t;
            ReadRoster(Property(root, "result"));
        }
        if (cat == "PLAYTEST" && msg == "mark")
        {
            Marks.Add(new Mark(t, Text(root, "note") ?? ""));
            ReadHero(Id(root, "hero"), Text(root, "champion"), Team(root));
        }
        if (cat == "INVARIANT" && Text(root, "unit_type") == "hero")
            ReadHero(Id(root, "unit"), Text(root, "champion"), Team(root));
        // Pets (Shaco's clone, Yorick's ghouls) log their champion's name here;
        // redacting it would blank that champion across the report. Player names
        // from the roster and CreateHero stay redacted even when they match a champion.
        if (cat == "AI" && Text(root, "minion_type") is null && Text(root, "unit_name") is var unitName
            && Amberline.Data.ChampionCatalog.ByName(unitName) is null)
            AddPrivateName(unitName);
        // CreateHero is not projected by the existing wire decoder. Read only
        // its fixed champion/team fields; never render the adjacent player name.
        if (cat == "PKT" && Outbound(root) && Opcode(root) == 0x4F && Text(root, "raw") is { Length: 206 } raw)
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(raw); }
            catch (FormatException) { return; }
            AddPrivateName(Encoding.ASCII.GetString(bytes, 23, 40).TrimEnd('\0'));
            ReadHero(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)),
                Encoding.ASCII.GetString(bytes, 63, 40).TrimEnd('\0'), bytes[15] == 1 ? "Order" : "Chaos");
        }
    }

    private void ReadRoster(JsonElement root)
    {
        foreach (var player in Items(root, "Players"))
        {
            AddPrivateName(Text(player, "Name"));
            ReadHero(Id(player, "NetId"), Text(player, "Champion"), NormalizeTeam(Text(player, "Team")));
        }
    }
    private void ReadHero(uint id, string? champion, string team)
    {
        if (id != 0 && champion is not null && Regex.IsMatch(champion, @"\A[A-Za-z][A-Za-z0-9]{0,39}\z"))
            Heroes[id] = new Hero(champion, team);
    }

    private void Observe(JsonElement root)
    {
        var t = Number(root, "t") ?? 0;
        // The checker runs after the match, so its lines are not cut at the end time.
        if (Text(root, "cat") == "PACKET") { ObservePacket(root, t); return; }
        if (EndTime is double end && t > end) return;
        if (t >= _nextSweep)
        {
            foreach (var (id, unit) in _units.ToArray())
            {
                foreach (var key in unit.Waypoints.Where(pair => t - pair.Value.Seen > 5).Select(pair => pair.Key).ToArray())
                {
                    if (!unit.Waypoints[key].Counted) UnmatchedWaypoints++;
                    unit.Waypoints.Remove(key);
                }
                if (t - unit.Seen <= 10) continue;
                FinishUnit(id, unit);
                _units.Remove(id);
            }
            _nextSweep = t + 1;
        }
        var cat = Text(root, "cat");
        var msg = Text(root, "msg");
        if (cat == "INVARIANT") { ObserveInvariant(root, t); return; }
        if (cat == "AI" && msg == "fidelity_batch")
        {
            FidelityBatches++;
            foreach (var minion in Items(root, "minions"))
                if (Id(minion, "netid") is var id && id != 0) Unit(id, t).Minion = true;
            // Embedded orders can precede the containing snapshot by one batch.
            foreach (var order in Items(root, "orders")) ObserveOrder(order, EventTime(order, t));
            foreach (var minion in Items(root, "minions")) ObserveSample(minion, t, false);
            foreach (var hero in Items(root, "heroes")) ObserveSample(hero, t, true);
            return;
        }
        if (cat == "AI" && (msg == "movement_event" || msg == "minion_ai_transition"))
        {
            var id = Id(root, "netid");
            if (id == 0) return;
            var unit = Unit(id, t);
            if (Team(root) != "unknown") unit.Team = Team(root);
            unit.Target = Id(root, "attack_target");
            var kind = Text(root, "kind") ?? "";
            var reason = Text(root, "reason") ?? "unattributed";
            if (kind == "hero-order")
            {
                unit.Hero = true;
                unit.NearWave = Point(Property(root, "authority")) is { } position && NearWave(position, t);
                if (unit.NearWave) NearWaveOrders++;
            }
            if (kind.StartsWith("waypoint/", StringComparison.Ordinal))
            {
                if (Text(root, "outcome") == "enqueued")
                {
                    // Counts are successful observer enqueues, not intent counts.
                    Increment(PacketReasons, SafeReason(reason));
                    var pair = Pair(unit, Id(root, "sync_id"), t);
                    pair.Enqueue ??= Number(root, "observed_mono_ns") ?? Number(root, "mono_ns");
                    MatchPair(pair, id, t);
                }
            }
            if (kind == "minion-resync")
            {
                // The collision park/resume and the reference probe (K10, K9) were removed on 16 Oct 2026, so a
                // current build never emits these; they stay so logs recorded before it still measure.
                unit.Minion = true;
                // A re-park without movement in between keeps the original start: the client
                // has been stopped since then. The server t clock matches the stall classes.
                if (reason == "collision-park") unit.ParkAt ??= t;
                if (reason == "collision-resume") EndHold(unit, id, t, false);
                if (reason == "reference-probe" && Number(root, "reference_error") is double error)
                    Add(10, error, t, t, id, error / 225, "divergence");
            }
            return;
        }
        if (cat != "PKT" || !Outbound(root)) return;
        var opcode = Opcode(root);
        if (opcode == 0x80)
        {
            var id = Id(root, "net_id");
            if (id != 0) Unit(id, t).Team = Team(root);
        }
        if (opcode == 0x4D)
        {
            var id = Id(root, "netid");
            if (id != 0) Unit(id, t).Alive = true;
        }
        if (opcode is 0xA6 or 0x61 or 0x8E)
        {
            var dead = Id(root, "dying_netid");
            if (dead == 0) dead = Id(root, "netid");
            if (dead == 0) return;
            if (_units.TryGetValue(dead, out var alreadyDead) && !alreadyDead.Alive) return;
            foreach (var other in _units.Values.Where(unit => unit.Minion && unit.Alive && unit.Target == dead))
                other.DeathAt ??= t;
            var dying = Unit(dead, t);
            FinishStalls(dead, dying);
            AbandonHold(dying, false);
            AbandonHold(dying, true);
            dying.DeathAt = null;
            dying.Alive = false;
        }
        if (opcode == 0x0D)
        {
            var id = Id(root, "attacker_netid");
            if (!_units.TryGetValue(id, out var unit)) return;
            NextAction(id, unit, t);
            EndStall(id, unit, 2);
            unit.Attacking = true;
        }
        // DIAGNOSTICS: (batch 291) a reveal's route or stop (0xC2 movement data type 2 or 3) is the reveal-time 0xC1 batch 276 moved into it.
        if (opcode == 0xC1 || opcode == 0xC2 && Id(root, "movement_type") is 2 or 3)
        {
            var id = Id(root, "net_id");
            if (id == 0) return;
            var unit = Unit(id, t);
            var pair = Pair(unit, Id(root, "sync_id"), t);
            if (pair.PacketProcessed) return;
            pair.PacketProcessed = true;
            var points = Items(root, "waypoints").Select(Point).Where(p => p.HasValue).Select(p => p!.Value).ToArray();
            if (points.Length > 1)
            {
                NextAction(id, unit, t);
                unit.Attacking = false;
                // The route that follows a park but is not its collision-resume ends it unpaired.
                // Merge holds end in ObserveOrder instead: their resume is read from the later batch.
                AbandonHold(unit, false);
            }
            if (!unit.Hero || !unit.NearWave || points.Length < 2) return;
            var chord = Distance(points[0], points[^1]);
            if (chord < 50) return;
            var length = 0.0;
            for (var i = 1; i < points.Length; i++) length += Distance(points[i - 1], points[i]);
            Add(6, length / chord, t, t, id, length / chord, "hero detour");
        }
    }

    private void ObserveOrder(JsonElement order, double t)
    {
        var id = Id(order, "netid");
        if (id == 0) return;
        var unit = Unit(id, t);
        var kind = Text(order, "kind");
        var points = Items(order, "waypoints").Select(Point).Where(p => p.HasValue).Select(p => p!.Value).ToArray();
        if (kind == "input" && unit.Hero && points.Length > 0)
            unit.NearWave = NearWave(points[0], t);
        if (kind == "client-report" && points.Length >= 2)
        {
            var gap = Distance(points[0], points[1]);
            Add(8, gap, t, t, id, gap / 225, "divergence");
            if (NearWave(points[0], t)) Add(9, gap, t, t, id, gap / 225, "divergence");
        }
        if (kind is not ("waypoint" or "waypoints")) return;
        var pair = Pair(unit, Id(order, "sync_id"), t);
        if (pair.IntentProcessed) return;
        pair.IntentProcessed = true;
        pair.Intent = Number(order, "mono_ns");
        MatchPair(pair, id, t);
        var reason = Text(order, "reason");
        // The lane-merge yield (K6) was removed on 16 Oct 2026, so a current build never
        // emits these two reasons; they stay so logs recorded before it still measure.
        if (reason == "lane-merge-yield") unit.MergeAt ??= t;
        else if (reason == "lane-merge-resume") EndHold(unit, id, t, true);
        // Any other route the minion moves on ends a yield without pairing it with a later resume.
        else if (points.Length > 1) AbandonHold(unit, true);
    }

    private void ObserveSample(JsonElement row, double t, bool hero)
    {
        var id = Id(row, hero ? "net_id" : "netid");
        var point = Point(Property(row, hero ? "position" : "pos"));
        if (id == 0 || point is null) return;
        var unit = Unit(id, t);
        unit.Hero = hero;
        if (Team(row) != "unknown") unit.Team = Team(row);
        // Hero snapshots have no alive flag; a dead hero's cleared route excludes
        // it from stalls, and the next respawn must not inherit a dead minion latch.
        var alive = Property(row, "alive").ValueKind != JsonValueKind.False && (hero || unit.Alive);
        var route = Items(row, "waypoints").Any();
        var target = Id(row, "attack_target");
        var wrong = false;
        foreach (var collider in Items(Property(row, "collision"), "collider_ids"))
        {
            if (!collider.TryGetUInt32(out var otherId) || otherId == target || target == 0) continue;
            var otherTeam = _units.TryGetValue(otherId, out var other) ? other.Team :
                Heroes.TryGetValue(otherId, out var otherHero) ? otherHero.Team : "unknown";
            if (otherTeam == "unknown" || unit.Team == "unknown") { MissingColliderTeams++; continue; }
            wrong |= otherTeam != unit.Team;
        }
        var continuous = double.IsFinite(unit.SampleTime) && t >= unit.SampleTime && t - unit.SampleTime <= 1;
        var still = continuous && alive && Distance(unit.Position, point.Value) <= (hero ? 1 : 0.1);
        var retained = still && route && unit.Route;
        var conditions = hero
            ? new[] { retained, false, false, retained && unit.NearWave }
            : new[] { retained, still && !route && !unit.Route && target == 0 && unit.SampleTarget == 0,
                still && wrong && unit.WrongCollider && !unit.Attacking, false };
        for (var i = 0; i < conditions.Length; i++)
        {
            if (!conditions[i]) EndStall(id, unit, i);
            else if (double.IsNaN(unit.Stalls[i])) unit.Stalls[i] = unit.SampleTime;
        }
        unit.Position = point.Value;
        unit.SampleTime = t;
        unit.Route = route;
        unit.Target = target;
        unit.SampleTarget = target;
        unit.WrongCollider = wrong;
        unit.Alive = alive;
        if (!still) unit.Attacking = false;
    }

    private bool NearWave((double X, double Z) position, double t) => _units.Values.Any(unit =>
        unit.Minion && unit.Alive && double.IsFinite(unit.SampleTime) &&
        t - unit.SampleTime is >= 0 and <= 1 && Distance(position, unit.Position) <= 400);

    private UnitState Unit(uint id, double t)
    {
        if (!_units.TryGetValue(id, out var unit))
        {
            if (_units.Count >= 4096) throw new InvalidDataException("triage active entity limit (4096) exceeded");
            var hero = Heroes.GetValueOrDefault(id);
            _units[id] = unit = new UnitState { Hero = hero is not null, Team = hero?.Team ?? "unknown" };
            PeakUnits = Math.Max(PeakUnits, _units.Count);
        }
        unit.Seen = Math.Max(unit.Seen, t);
        return unit;
    }
    private WaypointPair Pair(UnitState unit, uint sync, double t)
    {
        if (!unit.Waypoints.TryGetValue(sync, out var pair))
        {
            if (unit.Waypoints.Count >= 128)
            {
                var oldest = unit.Waypoints.MinBy(item => item.Value.Seen);
                if (!oldest.Value.Counted) UnmatchedWaypoints++;
                unit.Waypoints.Remove(oldest.Key);
            }
            unit.Waypoints[sync] = pair = new WaypointPair();
            PeakPairsPerUnit = Math.Max(PeakPairsPerUnit, unit.Waypoints.Count);
        }
        pair.Seen = Math.Max(pair.Seen, t);
        return pair;
    }
    private void MatchPair(WaypointPair pair, uint id, double t)
    {
        if (pair.Counted || pair.Intent is not double intent || pair.Enqueue is not double enqueue || enqueue < intent) return;
        pair.Counted = true;
        var ms = (enqueue - intent) / 1e6;
        Add(11, ms, t, t, id, ms / 100, "enqueue delay");
        if (_units.TryGetValue(id, out var unit) && unit.Minion)
            Add(13, ms, t, t, id, ms / 100, "enqueue delay");
    }
    private void NextAction(uint id, UnitState unit, double t)
    {
        if (unit.DeathAt is double death && unit.Minion && t >= death)
            Add(5, t - death, death, t, id, t - death, "minion stall");
        unit.DeathAt = null;
    }
    // A park or merge hold is measured only from its start to the matching resume. Other
    // ends (the minion moving on, its death, the capture ending) leave the duration unknown.
    private void EndHold(UnitState unit, uint id, double t, bool merge)
    {
        var start = merge ? unit.MergeAt : unit.ParkAt;
        if (start is double begin && t >= begin) Add(merge ? 3 : 4, t - begin, begin, t, id, t - begin, "minion stall");
        if (merge) unit.MergeAt = null;
        else unit.ParkAt = null;
    }
    private void AbandonHold(UnitState unit, bool merge)
    {
        if ((merge ? unit.MergeAt : unit.ParkAt) is null) return;
        CensoredHolds++;
        if (merge) unit.MergeAt = null;
        else unit.ParkAt = null;
    }
    private void EndStall(uint id, UnitState unit, int index)
    {
        var start = unit.Stalls[index];
        var duration = unit.SampleTime - start;
        if (double.IsFinite(start) && duration >= (unit.Hero ? 0 : 0.3) - 1e-9 && duration > 0)
            Add(unit.Hero ? index == 3 ? 7 : 12 : index, duration, start, unit.SampleTime, id, duration,
                unit.Hero ? "hero stall" : "minion stall");
        unit.Stalls[index] = double.NaN;
    }
    private void FinishStalls(uint id, UnitState unit)
    {
        for (var i = 0; i < unit.Stalls.Length; i++) EndStall(id, unit, i);
    }
    private void FinishUnit(uint id, UnitState unit)
    {
        FinishStalls(id, unit);
        if (unit.MergeAt.HasValue) CensoredHolds++;
        if (unit.ParkAt.HasValue) CensoredHolds++;
        UnmatchedWaypoints += unit.Waypoints.Values.LongCount(pair => !pair.Counted);
    }

    private void ObservePacket(JsonElement row, double t)
    {
        var kind = Text(row, "msg") ?? "unknown";
        // The writer caps lines per kind and states what it dropped in one record.
        if (kind == "packet.suppressed")
        {
            if (Number(row, "count") is double dropped && Text(row, "kind") is { } droppedKind)
                PacketKind(droppedKind).Count += (long)dropped;
            return;
        }
        if (Text(row, "lvl") != "WARN") return;
        var summary = PacketKind(kind);
        summary.Count++;
        var violation = new PacketViolation(t, (int)(Number(row, "peer") ?? -1), Text(row, "opcode") ?? "unknown",
            Id(row, "netid"), Text(row, "detail") ?? "");
        var first = summary.First;
        var at = first.FindIndex(existing => existing.Time > t);
        if (at < 0) at = first.Count;
        if (at >= PacketSummary.KeepFirst) return;
        first.Insert(at, violation);
        if (first.Count > PacketSummary.KeepFirst) first.RemoveAt(PacketSummary.KeepFirst);
    }

    private PacketSummary PacketKind(string kind)
    {
        // A kind is checker-chosen text; bound the table so a bad stream cannot grow it.
        if (!PacketViolations.ContainsKey(kind) && PacketViolations.Count >= 256) kind = "other";
        if (!PacketViolations.TryGetValue(kind, out var summary))
            PacketViolations[kind] = summary = new PacketSummary();
        return summary;
    }

    private void ObserveInvariant(JsonElement row, double t)
    {
        var name = Text(row, "msg") ?? "unknown";
        var ending = name.EndsWith("-end", StringComparison.Ordinal);
        var kind = ending ? name[..^4] : name;
        if (kind is not ("blocked-cell" or "overspeed" or "stacked" or "target-beyond-retention" or
            "client-divergence" or "minion-reference-divergence")) kind = "unknown";
        if (!Invariants.TryGetValue(kind, out var summary)) Invariants[kind] = summary = new InvariantSummary();
        var id = Id(row, "unit");
        var key = (id, kind);
        if (ending)
        {
            summary.Ends++;
            var duration = Number(Property(row, "detail"), "duration") ??
                (_openInvariants.TryGetValue(key, out var start) ? t - start : 0);
            summary.Duration += Math.Max(0, duration);
            _openInvariants.Remove(key);
            var moment = new Moment(kind + " duration (s)", t - duration, t, id, duration, duration);
            KeepWorst(summary.Worst, moment);
            Record(moment, "invariant");
        }
        else
        {
            summary.Count++;
            if (_openInvariants.Count >= 24576 && !_openInvariants.ContainsKey(key))
                throw new InvalidDataException("triage active invariant limit exceeded");
            _openInvariants.TryAdd(key, t);
            var value = Number(row, "value") ?? 0;
            var limit = Number(row, "limit") ?? 1;
            var moment = new Moment(kind, t, t, id, value, limit > 0 ? value / limit : value);
            KeepWorst(summary.Worst, moment);
            Record(moment, "invariant");
            foreach (var mark in Marks.Where(mark => Math.Abs(mark.Time - t) <= Window))
            {
                mark.InvariantEvents++;
                Increment(mark.InvariantKinds, kind);
            }
        }
    }

    private void Add(int metric, double value, double start, double end, uint id, double score, string group)
    {
        if (!double.IsFinite(value) || value < 0) return;
        var moment = new Moment(MetricNames[metric], start, end, id, value, score);
        Metrics[MetricNames[metric]].Add(value, moment);
        Record(moment, group);
    }
    private void Record(Moment moment, string group)
    {
        if (!Worst.Any(previous => previous.Time == moment.Time && previous.End == moment.End &&
                previous.Unit == moment.Unit && previous.Score == moment.Score)) KeepWorst(Worst, moment);
        foreach (var mark in Marks)
        {
            if (moment.Time > mark.Time + Window || moment.End < mark.Time - Window) continue;
            if (!mark.Worst.TryGetValue(group, out var previous) || moment.Score > previous.Score) mark.Worst[group] = moment;
        }
    }
    internal static void KeepWorst(List<Moment> list, Moment moment)
    {
        list.Add(moment);
        list.Sort((a, b) => { var score = b.Score.CompareTo(a.Score); return score != 0 ? score : a.Time.CompareTo(b.Time); });
        if (list.Count > 5) list.RemoveAt(5);
    }

    private void ReadClientLog(string path)
    {
        ClientLogPresent = File.Exists(path);
        if (!ClientLogPresent) return;
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("StartGame", StringComparison.Ordinal) || line.Contains("Finished", StringComparison.OrdinalIgnoreCase)) continue;
            if (ClientTime(line) is double client && HudOffset is double server) ClientClockOffset = server - client;
            break;
        }
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var assert = line.Contains("assert", StringComparison.OrdinalIgnoreCase);
            var error = line.Contains("|  ERROR|", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(line, @"\|\s*(ERROR|FATAL)\s*\|", RegexOptions.IgnoreCase);
            if (!assert && !error) continue;
            var message = line.Split('|', 5).Last().Trim();
            // Never echo arbitrary native log messages: paths, account names and
            // addresses are common here. Fingerprints plus line numbers locate them.
            var key = (assert ? "assert " : "error ") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)))[..16];
            var time = ClientTime(line) is double ct && ClientClockOffset is double offset ? ct + offset : (double?)null;
            if (!ClientErrors.TryGetValue(key, out var item))
            {
                if (ClientErrors.Count >= 4096) throw new InvalidDataException("triage distinct client error limit (4096) exceeded");
                ClientErrors[key] = item = new ClientError(key, lineNumber, time);
            }
            item.Count++;
            if (time is not double t) continue;
            Record(new Moment(key + $" line {lineNumber}", t, t, 0, 1, assert ? 5 : 1), "client assert/error");
            foreach (var mark in Marks.Where(mark => Math.Abs(mark.Time - t) <= Window)) mark.ClientErrors++;
        }
    }
    private static double? ClientTime(string line) => double.TryParse(line.Split('|', 2)[0], NumberStyles.Float,
        CultureInfo.InvariantCulture, out var time) && double.IsFinite(time) ? time : null;
    private void AddPrivateName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _privateNames.Add(name);
    }
    public string Redact(string text)
    {
        foreach (var name in _privateNames.OrderByDescending(name => name.Length))
            text = text.Replace(name, "[redacted]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)\b[A-Z]:[\\/]Users[\\/][^\s\\/]+", "[user path]");
        text = Regex.Replace(text, @"\b\d{1,3}(?:\.\d{1,3}){3}\b|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b|(?i)(?<!\w)(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.%]*(?!\w)", "[redacted]");
        return text;
    }
    private double EventTime(JsonElement root, double fallback) => _monoOrigin is double origin &&
        (Number(root, "observed_mono_ns") ?? Number(root, "mono_ns")) is double mono ? (mono - origin) / 1e9 : fallback;
    // collision-resync, prediction-resync and march-reanchor (K10, K9, K8) are no longer sent (16 and 17 Oct 2026); logs recorded before still carry them.
    private static string SafeReason(string reason) => reason is
        "target-chase" or "march-reanchor" or "congestion-route" or "collision-resync" or "control-release-lane" or
        "lane-merge-yield" or "lane-merge-resume" or "reveal-anchor" or "hero-order" or "hero-order-wait" or
        "hero-automatic" or "prediction-resync" or "blocked-route-recovery" or "lane-return" or "spawn" or
        "unattributed" or "stop" or "hold" or "target-acquired" or "plant-stop" or "wave-spawn" or "target-lost" or
        "committed-attack-stop" or "hero-action-locked" or "overlap-declip" or "target-lost-wait-for-lane" or
        "hero-emote-stop" ? reason : "other";
    private static void Increment(Dictionary<string, long> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;
    private static bool Outbound(JsonElement root) => (Text(root, "dir") ?? Text(root, "msg")) == "->";
    private static int Opcode(JsonElement root)
    {
        if (Number(root, "op") is double number) return (int)number;
        var text = Text(root, "op") ?? "";
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var opcode) ? opcode : -1;
    }
    private static string Team(JsonElement root) => NormalizeTeam(Text(root, "team") ?? Number(root, "team")?.ToString(CultureInfo.InvariantCulture));
    private static string NormalizeTeam(string? team) => team?.ToUpperInvariant() switch
    {
        "ORDER" or "100" => "Order", "CHAOS" or "200" => "Chaos", "NEUTRAL" or "300" => "Neutral", _ => "unknown",
    };
    private static JsonElement Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement root, string name) => Property(root, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement root, string name) => Property(root, name) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static uint Id(JsonElement root, string name) => Property(root, name) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var id) ? id : 0;
    private static IEnumerable<JsonElement> Items(JsonElement root, string name) => Property(root, name) is var value &&
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static (double X, double Z)? Point(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2 &&
        value[0].TryGetDouble(out var x) && value[1].TryGetDouble(out var z) && double.IsFinite(x) && double.IsFinite(z) ? (x, z) : null;
    private static double Distance((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
