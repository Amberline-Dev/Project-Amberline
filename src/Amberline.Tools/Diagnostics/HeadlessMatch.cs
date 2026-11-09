using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Spells;
using Amberline.Tools.Scenarios;

namespace Amberline.Tools.Diagnostics;

// DIAGNOSTICS: retail has no headless bot match. This tool plays one complete ten-bot Summoner's Rift
// match through the production composition on the virtual clock, with no network and no client. Each of the
// ten seats is a connected participant (it authenticates and loads through DuelSession.Receive and
// RunSchedulers exactly as a stock client would) so its per-peer replication is the stream a real client
// gets; the retail bot-script port drives every seat (BotController.Controls). The stream of every peer is
// fed to the packet-rule checker and the result is written as a live-shaped game log (session.jsonl,
// meta.json, match-result.json) that `triage` reads unchanged. One seed feeds every random roll.

/// <summary>
/// A seat's connection that drops during the match and comes back (DIAGNOSTICS: a tool-only scenario). <see cref="Seat"/> is 0-9 (Order 0-4,
/// Chaos 5-9). The drop happens at the first step at or after game second <see cref="AtSeconds"/> where <see cref="When"/> holds:
/// <c>any</c> (immediately), <c>fight</c> (the seat's hero is alive with an enemy hero within 1000 u) or <c>dead</c> (the seat's hero is dead).
/// The new connection authenticates <see cref="DropSeconds"/> later and sends its loading marker (0xC6) <see cref="LoadSeconds"/> after that,
/// the 14 to 25 s a stock client measured between its handshake and 0xC6. <see cref="Prime"/> gives the hero script-held state just before the drop
/// (cooldowns on every slot and a summoner spell, an extra-slot child cooldown, a sealed slot, a renamed extra slot, a re-typed slot) so that the
/// burst has something to replay (and a character layer that carries spells, so that the order of the burst matters); bots rarely cast, and the
/// state is removed again after the judgement. <see cref="Fault"/> is a control for the packet checker: the burst is delivered damaged
/// (<c>layer-last</c> moves the layer push behind the rest, <c>no-cooldowns</c> and <c>no-names</c> drop those packets) and the checker must say so.
/// </summary>
public sealed record ReconnectPlan(int Seat, double AtSeconds, double DropSeconds = 10.0, double LoadSeconds = 15.0, string When = "any", bool Prime = false, string Fault = "");

/// <summary>Inputs of one headless match.</summary>
public sealed record HeadlessMatchOptions(
    int Seed, string OutDirectory, double Minutes = 60.0, IReadOnlyList<string>? Roster = null)
{
    /// <summary>The map the match is played on (<c>--map-id</c>: 1 Summoner's Rift, 2 the Harrowing rift, 4 Twisted Treeline).</summary>
    public int MapId { get; init; } = 1;

    /// <summary>The seats that drop and reconnect through the production reconnect path (one plan per seat); empty for the ordinary match.</summary>
    public IReadOnlyList<ReconnectPlan> Reconnects { get; init; } = [];

    /// <summary>Test seam: runs at the start of every simulation step (to end or crash a match on demand).</summary>
    internal Action<MatchRuntime>? OnStep { get; init; }

    /// <summary>Scenario seam: receives the session log as soon as it is open, before the first step.</summary>
    internal Action<ISessionLogSink>? LogOpened { get; init; }

    /// <summary>
    /// Scenario seam: checked after every started step; once it returns true the match ends with outcome
    /// <c>stopped</c> (exit code 0), so a scenario that has finished does not play on to the minute cap.
    /// </summary>
    internal Func<bool>? StopWhen { get; init; }

    /// <summary>DIAGNOSTICS seam: sees each packet a peer is sent, after the replication store's filter (per run, unlike <c>PacketTap</c>).</summary>
    internal Action<int, PlaintextPacket>? PacketSink { get; init; }
}

/// <summary>What one team's five bots did; the check that both sides act reads these.</summary>
public sealed record TeamActivity(
    int Kills, int Deaths, int LaneMinionsKilled, int SpellCasts, double DamageDealt, double GoldEarned, int LevelSum);

/// <summary>One step of <see cref="TickStats.Worst"/>: game seconds, wall milliseconds, planner calls, planning work and the GC pause.</summary>
public sealed record TickStep(double Game, double Ms, long Calls, long Work, double GcMs);

/// <summary>
/// Wall time of the simulation steps of one headless match. A live server must finish a step inside its 33.3 ms
/// budget; every step beyond it delays the ENet service and every packet of every peer behind it (batch 20: a
/// 0.4 s step is a 0.4 s freeze of all units for the client). <see cref="WorstAt"/> is game seconds.
/// </summary>
public sealed record TickStats(
    int Steps, double MeanMs, double P99Ms, double MaxMs, double WorstAt, int Over33Ms, int Over100Ms,
    long MaxPlannerCalls = 0, long TotalPlannerCalls = 0,
    IReadOnlyList<TickStep>? Worst = null)
{
    public const double BudgetMs = 1000.0 / 30.0;

    /// <param name="steps">Game seconds, wall milliseconds and path-planner (<c>FindPathAroundUnits</c>) calls of each step.</param>
    public static TickStats From(IReadOnlyList<(double Game, double Ms, long Calls, long Work, double GcMs)> steps)
    {
        if (steps.Count == 0) return new TickStats(0, 0, 0, 0, 0, 0, 0);
        var sorted = steps.Select(step => step.Ms).Order().ToArray();
        var worst = steps.MaxBy(step => step.Ms);
        return new TickStats(steps.Count, Math.Round(sorted.Average(), 3),
            Math.Round(sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * 0.99)], 3),
            Math.Round(worst.Ms, 3), Math.Round(worst.Game, 2),
            sorted.Count(ms => ms > BudgetMs), sorted.Count(ms => ms > 100.0),
            steps.Max(step => step.Calls), steps.Sum(step => step.Calls),
            steps.OrderByDescending(step => step.Ms).Take(8)
                .Select(step => new TickStep(Math.Round(step.Game, 2), Math.Round(step.Ms, 1), step.Calls, step.Work, Math.Round(step.GcMs, 1))).ToArray());
    }

    public override string ToString() => FormattableString.Invariant(
        $"ticks steps={Steps} mean={MeanMs:0.00}ms p99={P99Ms:0.0}ms max={MaxMs:0.0}ms@{WorstAt:0.0}s over33ms={Over33Ms} over100ms={Over100Ms} planner-calls max/step={MaxPlannerCalls} total={TotalPlannerCalls}") +
        (Worst is null ? "" : Environment.NewLine + "worst steps (game s, ms, planner calls, planning work, GC pause ms): " +
            string.Join("; ", Worst.Select(step => FormattableString.Invariant($"{step.Game:0.0}s {step.Ms:0.0}ms {step.Calls} {step.Work} {step.GcMs:0.0}"))));
}

/// <summary>What the reconnect of a <see cref="ReconnectPlan"/> did (game seconds) and what the packet checker found in it.</summary>
public sealed record ReconnectReport(
    int Seat, string Champion, string When, string Outcome, double? DroppedAt, double? ConnectedAt, double? LoadedAt, double? SpawnedAt,
    double? ReadyAt, double? BurstAt, int BurstPackets, double? JudgedAt, bool HeroAliveAtDrop, bool HeroAliveAtBurst,
    ReconnectJudgement? Judgement, IReadOnlyList<string> Findings);

/// <summary>One lane-wave shape: how many waves had this member count and name mix ("36x Blue_Minion_Basic,18x ...").</summary>
public sealed record LaneWaveShape(int Waves, int Members, string Names);

/// <summary>What the waves of one match did: clusters of lane-minion creates, as the wire carried them.</summary>
public sealed record LaneWaveSummary(int Waves, int Members, double FirstSeconds, double LastSeconds,
    IReadOnlyList<LaneWaveShape> Shapes);

/// <summary>What one headless match did. <see cref="ExitCode"/> is the process exit code.</summary>
public sealed record HeadlessMatchReport(
    int Seed, string Outcome, string? Winner, double DurationSeconds, double WallSeconds,
    int OrderKills, int ChaosKills, long InvariantEvents, long PacketViolations,
    long LifecycleViolations, long PacketsChecked, string OutDirectory,
    IReadOnlyDictionary<string, TeamActivity> Teams, TickStats? Ticks = null,
    DashStatistics? Dashes = null, IReadOnlyDictionary<string, long>? PacketTotals = null,
    HeroRouteStatistics? HeroRoutes = null, HeroMotionStatistics? HeroMotion = null,
    IReadOnlyDictionary<string, long>? HeroAutomatic = null,
    IReadOnlyDictionary<string, (long Count, long Bytes)>? OpcodeCounts = null,
    IReadOnlyDictionary<string, (long Count, long Bytes)>? ReplicationShapes = null,
    IReadOnlyList<ReconnectReport>? Reconnects = null, int TurretShots = 0, LaneWaveSummary? Waves = null, IReadOnlyList<(string Name, uint NetId, long Routes)>? PetRoutes = null,
    RevealMovementStatistics? RevealMovement = null,
    // Batch 451: the Odin wave units alive at the end of the match (the map 8 wave check of the orchestrator's brief).
    int LiveWaveUnits = 0)
{
    public int ExitCode => Outcome switch { "natural" or "stopped" => 0, "cap" => 2, _ => 3 };

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"match seed={Seed} outcome={Outcome} winner={Winner ?? "none"} game={DurationSeconds:0.0}s " +
        $"wall={WallSeconds:0.0}s kills={OrderKills}/{ChaosKills} turret-shots={TurretShots} waves={Waves?.Waves ?? 0} " +
        $"live-wave-units={LiveWaveUnits} " +
        $"invariant={InvariantEvents} packet={PacketViolations} lifecycle={LifecycleViolations} packets={PacketsChecked} out={OutDirectory}");
}

public static class HeadlessMatch
{
    /// <summary>
    /// DIAGNOSTICS test tooling: sees every packet a peer is sent (peer index, game seconds, the packet the replication store let through).
    /// A scenario that studies the wire sets it for the length of its run and clears it after; one match runs per process.
    /// </summary>
    internal static Action<int, double, PlaintextPacket>? PacketTap { get; set; }

    /// <summary>The ten champions with a bot controller: five per side, as the live roster lists them.</summary>
    // The default match has ten seats (five per side, no repeat within a team): the first ten playable champions, which stay
    // the ten hand-written ones when a script-driven champion (Soraka, M2) is appended to the catalog. Any playable champion
    // can be named with --roster.
    public static readonly IReadOnlyList<string> DefaultRoster = ChampionCatalog.PlayableNames.Take(10).ToArray();

    /// <summary>
    /// Batch 563: the Basic Tutorial's three seats, Ashe and Nunu (Order) against Master Yi (Chaos). The TutorialScript finds its ally and
    /// its enemy by <c>GetChampionBySkinName('Nunu', TEAM_ORDER)</c> and <c>('MasterYi', TEAM_CHAOS)</c> (TutorialScript.txt proto 0.3 pc
    /// 0044-0053) and teaches Ashe's kit (EventMoveAshe, EventUseVolley; Characters/Ashe/defaults/RecItemsTUTORIAL.inibin).
    /// </summary>
    public static readonly IReadOnlyList<string> TutorialRoster = ["Ashe", "Nunu", "MasterYi"];

    /// <summary>The Order seats of a roster: the first half, or two of the tutorial's three.</summary>
    internal static int OrderSeats(int seats) => seats == TutorialRoster.Count ? 2 : seats / 2;

    // Reconstruction: the AIR lobby's HARD bot (PlatformBots.MaxSkillLevel) loadout, the strongest the stock
    // client offers. Of the playable champions Annie, Ryze, Sivir, Soraka and Taric ship Bot*.inibin files; the others get the platform defaults.
    private const int BotSkillLevel = 3;
    private const double Step = 1.0 / 30.0;
    private const double HandshakeLimitSeconds = 120.0;
    private const double EndCeremonyLimitSeconds = 30.0;
    private const int MaxLinesPerPacketKind = 1000;

    private const string Usage =
        "usage: match --seed <int> [--minutes <cap, default 60>] [--out <dir>] [--speed max] [--roster <10, 6 or 3 champions>]\n" +
        "             [--players <10|6, default 10: the first 10 or 6 default champions; map 3: Ashe, Nunu, Master Yi>] [--map-id <1|2|3|4|8, default 1>]\n" +
        "             [--reconnect-seat <0-9> --reconnect-at <game seconds> [--drop-seconds D, default 10] [--load-seconds L, default 15]\n" +
        "              [--reconnect-when any|fight|dead, default any] [--reconnect-prime]]\n" +
        "             (repeat the group for another seat)\n" +
        "exit: 0 natural end, 1 usage error, 2 minute cap reached, 3 crash";

    public static int RunCli(string[] args)
    {
        HeadlessMatchOptions options;
        try { options = Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine("match: " + exception.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }
        var report = Run(options);
        Console.WriteLine(report.Summary());
        foreach (var reconnect in report.Reconnects ?? [])
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"reconnect seat={reconnect.Seat} ({reconnect.Champion}) when={reconnect.When} outcome={reconnect.Outcome} " +
                $"drop={Fmt(reconnect.DroppedAt)} connect={Fmt(reconnect.ConnectedAt)} loaded={Fmt(reconnect.LoadedAt)} spawned={Fmt(reconnect.SpawnedAt)} " +
                $"ready={Fmt(reconnect.ReadyAt)} burst={Fmt(reconnect.BurstAt)} ({reconnect.BurstPackets} packets) judged={Fmt(reconnect.JudgedAt)} " +
                $"alive-at-drop={reconnect.HeroAliveAtDrop} alive-at-burst={reconnect.HeroAliveAtBurst}"));
            if (reconnect.Judgement is { } judged)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"reconnect-judged names={judged.NamesChecked} ranks={judged.RanksChecked} cooldowns={judged.CooldownsChecked} " +
                    $"units={judged.UnitsChecked} items={judged.ItemsChecked} violations={judged.Violations}"));
            foreach (var finding in reconnect.Findings) Console.WriteLine("  reconnect-finding " + finding);
        }
        if (report.Ticks is not null) Console.WriteLine(report.Ticks);
        if (report.PetRoutes is { Count: > 0 } pets)
        {
            // DIAGNOSTICS (diagnostic, batch 219): route packets of owned units (Tibbers, Shaco's box and clone, Yorick's ghouls), per receiving peer:
            // 0xC1 and, since batch 291, a reveal's route or stop (the reveal-time 0xC1 that batch 276 moved into the 0xC2).
            var byName = pets.GroupBy(pet => pet.Name, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal);
            Console.WriteLine("pet-routes " + string.Join(' ', byName.Select(group =>
                $"{group.Key}:{group.Count()}units/{group.Sum(pet => pet.Routes)}routes")));
            Console.WriteLine("pet-routes-netid " + string.Join(' ', pets.OrderByDescending(pet => pet.Routes).Take(8)
                .Select(pet => $"{pet.Name}#{pet.NetId}={pet.Routes}")));
        }
        // Every violation seen, by kind (PacketViolations counts only the listed ones), and the dashes of lane minions, pets and
        // monsters, per receiving peer: the dashes, the stationary 0xC1 queued just before one, the one that lands on its end,
        // and the routes that interrupted one.
        if (report.PacketTotals is { Count: > 0 } totals)
            Console.WriteLine("packet-totals " + string.Join(' ', totals.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        // Every server-to-peer packet by channel and opcode (count, plaintext bytes), to compare two builds of one seed.
        if (report.OpcodeCounts is { Count: > 0 } counts)
            Console.WriteLine("opcode-counts " + string.Join(' ', counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.Count}/{pair.Value.Bytes}")));
        if (report.ReplicationShapes is { Count: > 0 } shapes)
            Console.WriteLine("cc-shapes " + string.Join(' ', shapes.OrderByDescending(pair => pair.Value.Bytes).Take(14)
                .Select(pair => $"[{pair.Key}]={pair.Value.Count}/{pair.Value.Bytes}")));
        if (report.Dashes is { } dashes)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"dashes {dashes.Dashes} start-anchors={dashes.StartAnchors} landing-anchors={dashes.LandingAnchors} interrupted={dashes.Interrupted}"));
        // The waves of the match: how many came, what was in them and when, to compare two builds of one seed.
        if (report.Waves is { } waves)
        {
            var waveShapes = string.Join(' ', waves.Shapes
                .Select(shape => string.Create(CultureInfo.InvariantCulture, $"{shape.Waves}x{shape.Members}[{shape.Names}]")));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"waves count={waves.Waves} members={waves.Members} first={waves.FirstSeconds:0.0}s last={waves.LastSeconds:0.0}s shapes {waveShapes}"));
        }
        // DIAGNOSTICS (batch 291): the reveals' movement data per receiving peer (0xC2 type 2 route, type 3 stop, type 1 dash; entries with the
        // teleport flag among them).
        if (report.RevealMovement is { } reveals)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"reveal-movement route={reveals.Routes} stop={reveals.Stops} dash={reveals.Dashes} teleport={reveals.Teleports}"));
        // Hero routes per receiving peer (ten peers) and per game minute of the ten heroes (0xC1 and a reveal's route or stop; in-reveals is
        // the reveals' share), the routes the server published without a player order (automatic, by cause), and the hero stalls
        // The hero A* record is in docs/maintenance.
        if (report.HeroRoutes is { } routes && report.HeroMotion is { } motion)
        {
            // One hero and one receiving peer per seat (ten, or six for a 3v3).
            var seats = (double)(options.Roster?.Count ?? DefaultRoster.Count);
            var heroMinutes = Math.Max(1e-9, report.DurationSeconds / 60.0 * seats);
            var automatic = report.HeroAutomatic is { Count: > 0 } causes
                ? string.Join(',', causes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}"))
                : "none";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"hero-routes per-hero-minute={routes.Routes / seats / heroMinutes:0.00} stationary={routes.Stationary / seats / heroMinutes:0.00} " +
                $"in-reveals={routes.InReveals / seats / heroMinutes:0.00} " +
                $"automatic={automatic} stalls={motion.StallEpisodes} stall-seconds={motion.StallSeconds:0.0} longest-stall={motion.LongestStallSeconds:0.0} route-seconds={motion.RouteSeconds:0.0}"));
            foreach (var stall in motion.FirstStalls ?? []) Console.WriteLine("  stall " + stall);
        }
        return report.ExitCode;
    }

    private sealed class PlanBuilder(int seat)
    {
        public int Seat { get; } = seat;
        public double? At;
        public double Drop = 10.0, Load = 15.0;
        public string When = "any", Fault = "";
        public bool Prime;
    }

    private static string Fmt(double? seconds) => seconds is double value ? value.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    public static HeadlessMatchOptions Parse(IReadOnlyList<string> args)
    {
        int? seed = null;
        var minutes = 60.0;
        var mapId = 1;
        int? players = null;
        string? output = null;
        List<string>? roster = null;
        var plans = new List<PlanBuilder>();
        PlanBuilder Plan(string flag) => plans.Count != 0 ? plans[^1]
            : throw new ArgumentException($"{flag} needs a --reconnect-seat before it");
        double Seconds(string text, string flag, double min, double max) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) &&
            number >= min && number <= max ? number : throw new ArgumentException($"{flag} must be between {min} and {max}");
        for (var index = 0; index < args.Count; index++)
        {
            string Value() => index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index] : throw new ArgumentException($"'{args[index]}' needs a value");
            switch (args[index])
            {
                case "--seed":
                    seed = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : throw new ArgumentException("--seed must be an integer");
                    break;
                case "--minutes":
                    minutes = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) &&
                        double.IsFinite(cap) && cap > 0.0 && cap <= 600.0
                        ? cap : throw new ArgumentException("--minutes must be between 0 and 600");
                    break;
                case "--out": output = Value(); break;
                case "--map-id":
                    mapId = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapValue) && GameMaps.IsSupported(mapValue)
                        ? mapValue : throw new ArgumentException($"--map-id must be {GameMaps.SupportedText}");
                    break;
                case "--players":
                    // Batch 559 (TT-6): Twisted Treeline seats three a side.
                    players = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count is 6 or 10
                        ? count : throw new ArgumentException("--players must be 10 (5v5) or 6 (3v3)");
                    break;
                case "--speed":
                    // The only mode is the virtual clock at full speed; the flag exists so the intent is explicit.
                    if (!string.Equals(Value(), "max", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("--speed supports only 'max' (virtual clock, no sleeps)");
                    break;
                case "--reconnect-seat":
                    plans.Add(new PlanBuilder(int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seatNumber) && seatNumber is >= 0 and <= 9
                        ? seatNumber : throw new ArgumentException("--reconnect-seat must be a seat from 0 to 9")));
                    break;
                case "--reconnect-prime": Plan(args[index]).Prime = true; break;
                case "--reconnect-at": Plan(args[index]).At = Seconds(Value(), "--reconnect-at", 1.0, 36000.0); break;
                case "--drop-seconds": Plan(args[index]).Drop = Seconds(Value(), "--drop-seconds", 0.0, 600.0); break;
                case "--load-seconds": Plan(args[index]).Load = Seconds(Value(), "--load-seconds", 0.0, 120.0); break;
                case "--reconnect-when":
                {
                    var when = Value().ToLowerInvariant();
                    Plan(args[index]).When = when is "any" or "fight" or "dead" ? when
                        : throw new ArgumentException("--reconnect-when must be any, fight or dead");
                    break;
                }
                case "--reconnect-fault":
                {
                    var fault = Value().ToLowerInvariant();
                    Plan(args[index]).Fault = fault is "layer-last" or "no-cooldowns" or "no-names" ? fault
                        : throw new ArgumentException("--reconnect-fault must be layer-last, no-cooldowns or no-names");
                    break;
                }
                case "--roster":
                    roster = [];
                    while (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                        roster.Add(args[++index]);
                    break;
                default: throw new ArgumentException($"unknown option '{args[index]}'");
            }
        }
        if (seed is not int value) throw new ArgumentException("--seed is required");
        if (players is int seats && roster is not null && roster.Count != seats)
            throw new ArgumentException($"--players {seats} and a --roster of {roster.Count} disagree");
        var chosen = (IReadOnlyList<string>?)roster ?? (players is 6 ? DefaultRoster.Take(6).ToArray()
            : players is null && mapId == GameMaps.Tutorial ? TutorialRoster : null);
        var resolved = ResolveRoster(chosen);
        if (plans.Any(plan => plan.At is null)) throw new ArgumentException("every --reconnect-seat needs a --reconnect-at");
        if (plans.Select(plan => plan.Seat).Distinct().Count() != plans.Count) throw new ArgumentException("a seat can reconnect only once");
        if (plans.Any(plan => plan.Seat >= resolved.Length)) throw new ArgumentException($"--reconnect-seat must be a seat from 0 to {resolved.Length - 1}");
        return new HeadlessMatchOptions(value,
            Path.GetFullPath(output ?? Path.Combine(Environment.CurrentDirectory, $"match-seed-{value}")),
            minutes, chosen)
        {
            MapId = mapId,
            Reconnects = plans.Select(plan => new ReconnectPlan(plan.Seat, plan.At!.Value, plan.Drop, plan.Load, plan.When, plan.Prime, plan.Fault)).ToArray(),
        };
    }

    /// <summary>Ten champions (or six, batch 559: Twisted Treeline's 3v3), the first half Order, canonical case, no repeat within a team.</summary>
    internal static string[] ResolveRoster(IReadOnlyList<string>? requested)
    {
        var names = requested ?? DefaultRoster;
        if (names.Count is not (10 or 6 or 3))
            throw new ArgumentException("--roster needs 10 champions (5 Order, then 5 Chaos), 6 (3 and 3) or 3 (the tutorial: 2 Order, then 1 Chaos)");
        var roster = names.Select(name => BotController.SupportedChampions.FirstOrDefault(supported =>
                string.Equals(supported, name, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException($"'{name}' has no bot controller; use {string.Join(", ", ChampionCatalog.PlayableNames)}")).ToArray();
        var side = OrderSeats(roster.Length);
        if (roster[..side].Distinct().Count() != side || roster[side..].Distinct().Count() != roster.Length - side)
            throw new ArgumentException("a team cannot pick the same champion twice");
        return roster;
    }

    /// <summary>Plays one match and writes its outputs. Never throws for a match failure: that is outcome "crash".</summary>
    public static HeadlessMatchReport Run(HeadlessMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var roster = ResolveRoster(options.Roster);
        Directory.CreateDirectory(options.OutDirectory);
        var wall = Stopwatch.StartNew();
        using var log = new MatchLog(options.OutDirectory, options.Seed);
        options.LogOpened?.Invoke(log);
        // DIAGNOSTICS: the tools run the server's JIT warm-up too (JitWarmup), so their same-seed checks cover it.
        JitWarmup.Start();
        var outcome = "crash";
        var driver = (Driver?)null;
        try
        {
            driver = new Driver(options, roster, log);
            // DIAGNOSTICS: the first step waits for the warm-up only while it runs, as the server's first step does, so the step
            // timings below do not count it. Stderr only: the match log must stay byte-identical from run to run.
            if (JitWarmup.AwaitFirstStep() is { } warm)
                Console.Error.WriteLine(FormattableString.Invariant($"jit.warmup methods={warm.Result.Methods} failed={warm.Result.Failed} prepare_ms={warm.Result.Milliseconds:0.0} waited_ms={warm.WaitedMilliseconds:0.0}"));
            outcome = driver.Play();
        }
        catch (Exception exception)
        {
            log.Write("SESSION", "ERROR", "match.crash", new Dictionary<string, object?>
            {
                ["exc"] = exception.GetType().FullName,
                ["tb"] = MatchLog.RedactUserPaths(exception.ToString()),
            });
        }
        // DIAGNOSTICS (batch 608): which code moved the units, and the helper's cost per frame and per plan (stderr, like the warm-up line).
        if (driver?.Runtime.Movement.HelperSummary() is { } engine) Console.Error.WriteLine(engine);
        driver?.Runtime.Movement.StopEngine();
        // DIAGNOSTICS (batch 563): the Basic Tutorial's progress, every step its TutorialScript ran and what it still waits for.
        if (driver?.Runtime.Waves?.LevelScript is { TutorialLoaded: true } tutorial)
        {
            var steps = tutorial.TutorialSteps;
            log.Write("GAMEPLAY", "INFO", "tutorial.summary", new Dictionary<string, object?>
            {
                ["steps"] = steps.Select(step => FormattableString.Invariant($"{step.At:0.0}:{step.Step}")).ToArray(),
                ["waits"] = tutorial.TutorialWaits.ToArray(),
                ["wait_units"] = tutorial.TutorialWaitUnits().ToArray(),
                ["expired_alive"] = tutorial.TutorialExpiredAlive,
                ["player"] = driver!.Runtime.Session.Participants[0].Hero is { } player
                    ? FormattableString.Invariant($"{player.Name} level {player.Level} xp {player.Experience:0} points {player.SkillPoints} ranks {string.Join("/", player.SpellRanks)} gold {player.Gold:0}") : null,
            });
            Console.Error.WriteLine($"tutorial steps={steps.Count} last={(steps.Count > 0 ? steps[^1].Step : "none")} " +
                                    $"waits={string.Join(",", tutorial.TutorialWaits)} expired-alive={tutorial.TutorialExpiredAlive}");
        }
        var packetViolations = 0L;
        if (driver is not null)
        {
            try { packetViolations = driver.FinishChecker(log); }
            catch (Exception exception)
            {
                // A checker fault must not lose the match's other outputs; it is a crash like any other.
                outcome = "crash";
                log.Write("SESSION", "ERROR", "packet-checker.crash", new Dictionary<string, object?>
                {
                    ["exc"] = exception.GetType().FullName,
                    ["tb"] = MatchLog.RedactUserPaths(exception.ToString()),
                });
            }
        }
        wall.Stop();
        return WriteOutputs(options, roster, log, driver, outcome, wall.Elapsed.TotalSeconds, packetViolations);
    }

    /// <summary>Writes one PACKET line per violation (shared log schema), capped per kind.</summary>
    internal static void WritePacketViolations(MatchLog log, IReadOnlyList<PacketRuleViolation> violations)
    {
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var violation in violations)
        {
            var count = written.GetValueOrDefault(violation.Kind);
            written[violation.Kind] = count + 1;
            if (count >= MaxLinesPerPacketKind) continue;
            log.WriteAt(violation.T, "PACKET", "WARN", violation.Kind, new Dictionary<string, object?>
            {
                ["peer"] = violation.Peer,
                ["opcode"] = $"0x{violation.Opcode:X2}",
                ["netid"] = violation.NetId,
                ["detail"] = violation.Detail,
            });
        }
        foreach (var (kind, count) in written.Where(pair => pair.Value > MaxLinesPerPacketKind))
            log.Write("PACKET", "INFO", "packet.suppressed", new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["count"] = count - MaxLinesPerPacketKind,
            });
    }

    /// <summary>
    /// Lane-minion creates grouped into waves. One wave's members are staged <c>MinionDefinitions.MinionStaggerSeconds</c>
    /// (0.8 s) apart across the three lanes and both teams and waves come every 30 s, so a gap over 10 s starts the next
    /// wave. The shapes are the distinct (member count, name mix) groups, most frequent first.
    /// </summary>
    internal static LaneWaveSummary SummarizeLaneWaves(IReadOnlyDictionary<uint, (double Time, string Name)> spawns)
    {
        var waves = new List<List<(double Time, string Name)>>();
        List<(double Time, string Name)>? wave = null;
        var previous = double.NegativeInfinity;
        foreach (var spawn in spawns.Values.OrderBy(spawn => spawn.Time).ThenBy(spawn => spawn.Name, StringComparer.Ordinal))
        {
            if (wave is null || spawn.Time - previous > 10.0)
            {
                wave = [];
                waves.Add(wave);
            }
            wave.Add(spawn);
            previous = spawn.Time;
        }
        var shapes = waves
            .GroupBy(members => (Members: members.Count,
                Names: string.Join(",", members.GroupBy(member => member.Name)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Count()}x{group.Key}"))))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Members)
            .ThenBy(group => group.Key.Names, StringComparer.Ordinal)
            .Select(group => new LaneWaveShape(group.Count(), group.Key.Members, group.Key.Names))
            .ToArray();
        return new LaneWaveSummary(waves.Count, spawns.Count,
            waves.Count == 0 ? 0.0 : waves[0][0].Time, waves.Count == 0 ? 0.0 : waves[^1][^1].Time, shapes);
    }

    private static HeadlessMatchReport WriteOutputs(HeadlessMatchOptions options, string[] roster, MatchLog log,
        Driver? driver, string outcome, double wallSeconds, long packetViolations)
    {
        var result = log.Result;
        var teams = new Dictionary<string, TeamActivity>(StringComparer.Ordinal);
        // Structures the team's heroes got the killing blow on (hero.TurretsKilled/InhibitorsKilled). A match that
        // ends on the minute cap writes no result envelope, so the scoreboard side of the report needs them here.
        var towers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var heroes = driver is null ? Array.Empty<Entity>() : driver.Runtime.Session.Participants
                .Where(seat => seat.Team == team && seat.Hero is not null).Select(seat => seat.Hero!).ToArray();
            teams[team.ToString()] = new TeamActivity(
                heroes.Sum(hero => hero.ChampionKills), heroes.Sum(hero => hero.ChampionDeaths),
                heroes.Sum(hero => hero.LaneMinionsKilled), heroes.Sum(hero => hero.MatchStatistics.SpellCasts.Sum()),
                Math.Round(heroes.Sum(hero => hero.MatchStatistics.TotalDamageDealt), 1),
                Math.Round(heroes.Sum(hero => hero.MatchStatistics.GoldEarned), 1), heroes.Sum(hero => hero.Level));
            towers[team.ToString()] = heroes.Sum(hero => hero.TurretsKilled + hero.InhibitorsKilled);
        }
        // Per-seat bot measurements (BotMeasure): diagnostics for judging a change to the bot port.
        var bots = new List<Dictionary<string, object?>>();
        if (driver is not null)
            foreach (var seat in driver.Runtime.Session.Participants.Where(seat => seat.Hero is not null))
            {
                var hero = seat.Hero!;
                var measure = driver.Runtime.Bots?.MeasureOf(seat.PlayerId);
                var kinds = Amberline.Simulation.Bots.BotMeasure.TaskNames;
                bots.Add(new Dictionary<string, object?>
                {
                    ["champion"] = seat.ChampionName,
                    ["team"] = seat.Team.ToString(),
                    ["level"] = hero.Level,
                    ["kills"] = hero.ChampionKills,
                    ["deaths"] = hero.ChampionDeaths,
                    ["last_hits"] = hero.LaneMinionsKilled,
                    ["gold_earned"] = Math.Round(hero.MatchStatistics.GoldEarned, 0),
                    ["items"] = hero.Inventory.Count(slot => slot is not null),
                    ["items_purchased"] = hero.MatchStatistics.ItemsPurchased,
                    ["turrets_killed"] = hero.TurretsKilled,
                    ["inhibitors_killed"] = hero.InhibitorsKilled,
                    ["damage"] = Math.Round(hero.MatchStatistics.TotalDamageDealt, 0),
                    ["spell_casts"] = hero.MatchStatistics.SpellCasts.Sum(),
                    ["spell_casts_by_slot"] = hero.MatchStatistics.SpellCasts.ToArray(),
                    ["spell_ranks"] = hero.SpellRanks.ToArray(),
                    ["summoner_casts"] = hero.MatchStatistics.SummonerCasts.ToArray(),
                    ["orders"] = driver.Runtime.Bots?.OrdersIssued(seat.PlayerId) ?? 0,
                    ["measure"] = measure is null ? null : new Dictionary<string, object?>
                    {
                        ["alive_s"] = Math.Round(measure.AliveSeconds, 1),
                        ["task_s"] = kinds.Select((name, index) => (name, seconds: Math.Round(measure.TaskSeconds[index], 1)))
                            .ToDictionary(pair => pair.name, pair => (object?)pair.seconds),
                        ["push_lane_s"] = measure.PushLaneSeconds.Select(seconds => Math.Round(seconds, 1)).ToArray(),
                        ["no_task_s"] = Math.Round(measure.NoTaskSeconds, 1),
                        ["idle_s"] = Math.Round(measure.IdleSeconds, 1),
                        ["idle_run_max_s"] = Math.Round(measure.LongestIdleRun, 1),
                        ["stuck_s"] = Math.Round(measure.StuckSeconds, 1),
                        ["stuck_run_max_s"] = Math.Round(measure.LongestStuckRun, 1),
                        ["fountain_s"] = Math.Round(measure.FountainSeconds, 1),
                        ["purchase_attempts"] = measure.PurchaseAttempts,
                        ["purchases"] = measure.Purchases,
                        ["casts"] = measure.Casts,
                        ["cast_draws"] = measure.CastDraws,
                        ["cast_usable"] = measure.CastUsable,
                        ["cast_ordered"] = measure.CastOrdered,
                        ["cast_refused"] = measure.CastRefused,
                        ["cast_queued"] = measure.CastQueued,
                        ["cast_queued_done"] = measure.CastQueuedDone,
                        ["cast_queued_dropped"] = measure.CastQueuedDropped,
                        ["cast_queued_superseded"] = measure.CastQueuedSuperseded,
                        ["cast_gate"] = measure.CastGate,
                        ["cast_dropped_while"] = measure.CastDroppedWhile,
                        ["task_switches"] = measure.TaskSwitches,
                        ["anti_kite_latches"] = measure.AntiKiteLatches,
                        ["anti_kite_s"] = Math.Round(measure.AntiKiteSeconds, 1),
                        ["capture_channel_starts"] = measure.CaptureChannelStarts,
                    },
                });
            }
        var order = teams["Order"].Kills;
        var chaos = teams["Chaos"].Kills;
        // Jungle clears, the other half of "the bots still play the map": camp creeps the heroes
        // finished (Entity.NeutralMinionsKilled) plus the Dragon/Worm scalps (SuperMonsterKills).
        var monsterKills = driver is null ? 0 : driver.Runtime.Session.Participants
            .Where(seat => seat.Hero is not null)
            .Sum(seat => seat.Hero!.NeutralMinionsKilled + seat.Hero.MatchStatistics.SuperMonsterKills);
        var duration = result?.DurationSeconds ?? driver?.GameSeconds ?? 0.0;
        var invariant = log.InvariantEvents;
        var lifecycle = driver?.LifecycleViolations ?? 0;
        var packets = driver?.PacketsChecked ?? 0;
        var waves = driver is null ? null : SummarizeLaneWaves(driver.LaneSpawns);
        // DIAGNOSTICS (batch 256): the bound units' character-script polls, by character (the unit self-buff cadence's measure).
        if (driver?.Runtime.Spells.UnitScripts is { PollRuns.Count: > 0 } unitScripts)
            Console.WriteLine("unit-polls " + string.Join(' ', unitScripts.PollRuns.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        var motion = driver?.HeroMotion;
        log.Flush();

        var build = typeof(HeadlessMatch).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        var commit = ResolveGitCommit();
        // Pretty JSON of plain dictionaries: key order is the insertion order, so equal runs write equal files.
        var indented = new JsonSerializerOptions { WriteIndented = true };
        var summary = new Dictionary<string, object?>
        {
            ["seed"] = options.Seed,
            ["outcome"] = outcome,
            ["winner"] = result?.Winner,
            ["duration_seconds"] = Math.Round(duration, 4),
            ["minutes_cap"] = options.Minutes,
            ["kills"] = new Dictionary<string, object?> { ["Order"] = order, ["Chaos"] = chaos },
            ["monster_kills"] = monsterKills,
            ["turret_shots"] = driver?.TurretShots ?? 0,
            ["towers_taken"] = towers,
            ["hero_stalls"] = new Dictionary<string, object?>
            {
                ["episodes"] = motion?.StallEpisodes ?? 0,
                ["seconds"] = Math.Round(motion?.StallSeconds ?? 0.0, 1),
                ["longest"] = Math.Round(motion?.LongestStallSeconds ?? 0.0, 1),
            },
            ["lane_waves"] = waves is null ? null : new Dictionary<string, object?>
            {
                ["count"] = waves.Waves,
                ["members"] = waves.Members,
                ["first_s"] = Math.Round(waves.FirstSeconds, 1),
                ["last_s"] = Math.Round(waves.LastSeconds, 1),
                ["shapes"] = waves.Shapes
                    .Select(shape => $"{shape.Waves}x {shape.Members} [{shape.Names}]").ToArray(),
            },
            ["teams"] = teams,
            ["bots"] = bots,
            ["invariant_events"] = invariant,
            ["packet_violations"] = packetViolations,
            ["lifecycle_violations"] = lifecycle,
            ["packets_checked"] = packets,
            ["roster"] = roster,
            ["build"] = build,
            ["commit"] = commit,
            ["reconnects"] = driver?.ReconnectReports(),
        };
        File.WriteAllText(Path.Combine(options.OutDirectory, "match-result.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>(summary)
            {
                // Same envelope as a live game's result file, plus the headless summary above.
                ["session_id"] = log.SessionId,
                ["result"] = result,
            }, indented), new UTF8Encoding(false));

        // meta.json mirrors a live game's. The virtual clock starts at zero, so the epoch fields are
        // zero and every wall-clock fact lives only under "wall_clock" (the determinism check skips it).
        var meta = new Dictionary<string, object?>
        {
            ["t0_epoch"] = 0.0,
            ["t0_human"] = "headless virtual clock",
            ["commit"] = commit,
            ["platform"] = "headless",
            ["session_id"] = log.SessionId,
            ["build"] = build,
            ["mono_ns"] = 0L,
            ["t0_mono_ns"] = 0L,
            ["headless"] = true,
            ["seed"] = options.Seed,
            ["outcome"] = outcome,
            ["minutes_cap"] = options.Minutes,
            ["packets_checked"] = packets,
            ["roster"] = roster,
            ["wall_clock"] = new Dictionary<string, object?>
            {
                ["wall_seconds"] = Math.Round(wallSeconds, 3),
                ["process_id"] = Environment.ProcessId,
                ["tick_ms"] = driver?.Ticks,
                ["finished_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            },
        };
        File.WriteAllText(Path.Combine(options.OutDirectory, "meta.json"),
            JsonSerializer.Serialize(meta, indented), new UTF8Encoding(false));

        return new HeadlessMatchReport(options.Seed, outcome, result?.Winner, duration, wallSeconds,
            order, chaos, invariant, packetViolations, lifecycle, packets, options.OutDirectory, teams, driver?.Ticks,
            driver?.Dashes, driver?.PacketTotals, driver?.HeroRoutes, driver?.HeroMotion, driver?.HeroAutomatic, driver?.OpcodeCounts,
            driver?.ReplicationShapes, driver?.ReconnectReports(), driver?.TurretShots ?? 0, waves, driver?.PetRoutes, driver?.RevealMovement,
            LiveWaveUnits: driver is null ? 0 : driver.Runtime.World.Entities.Values.Count(entity =>
                entity.Alive && Amberline.Simulation.Minions.OdinWaves.ScriptOf(entity) is not null));
    }

    private static string ResolveGitCommit()
    {
        try
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                   !File.Exists(Path.Combine(directory.FullName, ".git")))
                directory = directory.Parent;
            if (directory is null) return "unknown";
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "rev-parse", "HEAD" },
            });
            if (process is null || !process.WaitForExit(2000)) return "unknown";
            var commit = process.StandardOutput.ReadToEnd().Trim();
            return commit.Length == 40 ? commit : "unknown";
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>One match: composition, the stock-client handshake of ten seats, then the simulation loop.</summary>
    private sealed class Driver
    {
        private readonly HeadlessMatchOptions _options;
        private readonly MatchLog _log;
        private readonly PacketStreamChecker _checker;
        private readonly HeroMotionMonitor _heroMotion = new();
        private readonly ReplicationObserver?[] _observers;
        private readonly Dictionary<uint, string> _petNames = new();
        private readonly List<(double Game, double Ms, long Calls, long Work, double GcMs)> _stepMs = [];
        private double _startedAt;
        private bool _startLogged;
        // Connection id to peer index. The ten seats connect as seat + 1; a reconnecting seat gets a new connection id (a new ENet
        // generation), exactly as the network shell keys its peers.
        private readonly Dictionary<long, int> _peerOf = [];
        private readonly List<ReconnectDirector> _reconnects = [];

        public Driver(HeadlessMatchOptions options, string[] roster, MatchLog log)
        {
            _options = options;
            _log = log;
            // One seeded source feeds every roll of the match (combat, Shaco boxes, bots, fear wander).
            var random = new SeededRandomSource(options.Seed);
            var mapPaths = Amberline.Data.GameDataPaths.Default.ForMap(options.MapId);
            if (options.MapId != 1) Amberline.Data.GameData.ConfigureDefault(mapPaths);
            // Batch 457: a CreateTurret must name a turret of this map's scene, the only turrets the client can bind.
            _checker = new(roster.Length) { SceneTurrets = PacketStreamChecker.SceneTurretNames(mapPaths) };
            _observers = new ReplicationObserver?[roster.Length];
            Runtime =MatchRuntime.CreateProduction(NavigationGrid.Load(mapPaths), Amberline.Data.GameData.Default,
                sessionLog: log, random: random, roster: BuildRoster(roster), botsControlAllSeats: true, movementDiagnostics: _heroMotion,
                mapId: options.MapId);
            Runtime.World.Clock.SetVirtual(0.0);
            log.Clock = () => Runtime.World.Clock.Now();
            // DIAGNOSTICS (batch 563): seat 0 is the tutorial's player, released from the bot scripts before the first step (PilotTutorial,
            // which since batch 593 also answers the popups and audio events with the client's 0xD5 and 0x05).
            if (options.MapId == GameMaps.Tutorial && Runtime.Waves?.LevelScript is not null)
                Runtime.Bots?.SetSeatScripted(Runtime.Session.Participants[0].PlayerId, true);
            Runtime.Lifecycle.AttachReporter(violation =>
            {
                LifecycleViolations++;
                log.Write("SESSION", "ERROR", "lifecycle violation", new Dictionary<string, object?>
                {
                    ["hero"] = violation.Hero,
                    ["team"] = violation.Team.ToString(),
                    ["netid"] = violation.NetId,
                    ["component"] = violation.Component,
                    ["timer"] = violation.Timer,
                    ["position"] = new[] { violation.Position.X, violation.Position.Z },
                    ["detail"] = violation.Detail,
                });
            });
            for (var seat = 0; seat < roster.Length; seat++) _peerOf[seat + 1] = seat;
            foreach (var plan in options.Reconnects) _reconnects.Add(new ReconnectDirector(this, plan, log));
            log.Write("SESSION", "INFO", "match.composed", new Dictionary<string, object?>
            {
                ["mode"] = "headless",
                ["rng_seed"] = options.Seed,
                ["roster"] = roster,
            });
        }

        public IReadOnlyList<ReconnectReport> ReconnectReports() => _reconnects.Select(director => director.Report()).ToArray();

        public MatchRuntime Runtime { get; }
        public TickStats Ticks => TickStats.From(_stepMs);
        public DashStatistics Dashes => _checker.Dashes;
        public HeroRouteStatistics HeroRoutes => _checker.HeroRoutes;
        public RevealMovementStatistics RevealMovement => _checker.RevealMovement;
        public IReadOnlyList<(string Name, uint NetId, long Routes)> PetRoutes => _petNames
            .Select(pet => (pet.Value, pet.Key, _checker.RoutesByNetId.GetValueOrDefault(pet.Key)))
            .ToArray();
        public HeroMotionStatistics HeroMotion => _heroMotion.Statistics;
        public IReadOnlyDictionary<string, long> HeroAutomatic => Runtime.Movement.HeroAutomaticRoutes;
        public IReadOnlyDictionary<string, long> PacketTotals => _checker.TotalsByKind;
        private readonly Dictionary<string, (long Count, long Bytes)> _opcodeCounts = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, (long Count, long Bytes)> OpcodeCounts => _opcodeCounts;
        // Turret shots, to compare two builds of one seed: a swing's 0x0D goes to every peer that
        // can see it, so one shot is one distinct (attacker, missile) pair. The opcode totals
        // cannot say which attacker fired; this can.
        private static readonly NetIdRange TurretNetIds = NetIdAllocator.RangeFor(UnitType.Turret);
        private readonly HashSet<(uint Attacker, uint Missile)> _turretShots = new();
        public int TurretShots => _turretShots.Count;
        // Lane-minion creates (0x80), to compare two builds of one seed: the opcode totals cannot say
        // how many waves came or what was in them; this can. One create is one distinct net id, first
        // seen at its game second with the name of the wave member it is. DIAGNOSTICS test tooling, the
        // turret shots' sibling: pets, wards and spell spawns carry other names, other net-id ranges,
        // or both, and never enter the tally.
        private static readonly NetIdRange MinionNetIds = NetIdAllocator.RangeFor(UnitType.Minion);
        private readonly Dictionary<uint, (double Time, string Name)> _laneSpawns = new();
        /// <summary>Lane-minion creates by net id: first-seen game second and the wave member's name.</summary>
        public IReadOnlyDictionary<uint, (double Time, string Name)> LaneSpawns => _laneSpawns;
        private readonly Dictionary<string, (long Count, long Bytes)> _replicationShapes = new(StringComparer.Ordinal);
        /// <summary>Sent 0xCC packets by the registries and field masks they carry (count, plaintext bytes).</summary>
        public IReadOnlyDictionary<string, (long Count, long Bytes)> ReplicationShapes => _replicationShapes;
        public long LifecycleViolations { get; private set; }
        public long PacketsChecked { get; private set; }
        public double GameSeconds => Runtime.Session.Started ? Runtime.Combat.CurrentTime - _startedAt : 0.0;

        /// <summary>Order seats 0-4, Chaos 5-9 (a 3v3: 0-2 and 3-5); PlayerId = connection id = seat + 1.</summary>
        private static SessionParticipantDefinition[] BuildRoster(string[] roster) =>
            roster.Select((champion, index) =>
            {
                var loadout = BotLoadoutReader.Read(GameData.Default.Paths, champion, BotSkillLevel);
                var (first, second) = loadout?.SummonerSpell1 is { } a && loadout.SummonerSpell2 is { } b
                    ? SummonerLoadout.ValidatePair(a, b)
                    : (SummonerLoadout.DefaultFirst, SummonerLoadout.DefaultSecond);
                // Batch 580: the tutorial's player casts Heal from summoner slot 0 in its Heal lesson (Tutorial_Replenish's
                // RegisterOnSpellAvatarCastCallback(0)); the platform's tutorial game seats it with Heal and Ghost (PlatformGames.Tutorial).
                if (roster.Length == TutorialRoster.Count && index == 0) (first, second) = ("SummonerHeal", "SummonerHaste");
                return new SessionParticipantDefinition(
                    (ulong)index + 1, (uint)index, GameData.Default.BotName(champion), champion,
                    index < OrderSeats(roster.Length) ? Team.Order : Team.Chaos, first, second, 0, 30,
                    RunesOf(loadout), MasteriesOf(loadout));
            }).ToArray();

        private static RunePage? RunesOf(BotLoadoutData? loadout)
        {
            if (loadout is null || loadout.Runes.Count == 0) return null;
            return RunePage.CreateBot(new RuneCatalog(GameData.Default.Paths), loadout.Runes);
        }

        private static MasteryPage? MasteriesOf(BotLoadoutData? loadout) =>
            loadout is null || loadout.Masteries.Count == 0 ? null
                : MasteryPage.Create(new MasteryCatalog(GameData.Default.Paths), loadout.Masteries, 30);

        /// <summary>Runs to a natural end or the minute cap; returns "natural" or "cap".</summary>
        public string Play()
        {
            var session = Runtime.Session;
            session.CreateWorld();
            foreach (var seat in session.Participants)
            {
                var connection = (long)seat.PlayerId;
                // The key check, the synch-version request the stock client sends on connecting, then the world-loaded marker
                // (0xC6) it sends when its own loading is over: the server spawns the heroes only after that marker.
                Send(session.Receive(connection, 0, session.BuildKeyCheck(seat.PlayerId), Runtime.Dependencies.Gameplay));
                Send(session.Receive(connection, 1, [0xC5, 0, 0, 0, 0], Runtime.Dependencies.Gameplay));
                Send(session.Receive(connection, 1, [0xC6, 0, 0, 0, 0], Runtime.Dependencies.Gameplay));
            }

            var capSeconds = _options.Minutes * 60.0;
            var handshake = 0.0;
            double? endedAt = null;
            while (true)
            {
                StepOnce();
                if (!session.Started)
                {
                    handshake += Step;
                    if (handshake > HandshakeLimitSeconds)
                        throw new InvalidOperationException("the seats did not reach the start barrier");
                    continue;
                }
                if (_options.StopWhen?.Invoke() == true) return "stopped";
                if (session.Result is not null)
                {
                    endedAt ??= Runtime.Combat.CurrentTime;
                    // Run on through the end-game ceremony so its packets reach the checker. DIAGNOSTICS (batch 465): a scenario (StopWhen
                    // set) that plays the end keeps stepping after EndGame until it completes; the ceremony limit still bounds the run.
                    if ((Runtime.Combat.EndGameSent && _options.StopWhen is null) || Runtime.Combat.CurrentTime - endedAt > EndCeremonyLimitSeconds)
                        return "natural";
                }
                else if (GameSeconds >= capSeconds - 1e-9)
                    return "cap";
            }
        }

        private int _pilotStep;

        /// <summary>
        /// DIAGNOSTICS (batch 563): the tutorial's player seat (seat 0, Ashe) is released from the bot scripts and does what the TutorialScript
        /// waits for, once a second, through the ordinary client dispatcher: it walks to the point or unit a near-callback watches and attacks the
        /// enemy a kill callback waits on (<see cref="LevelScriptHost.TutorialPlayerGoal"/>). Batch 580: it also plays the spell, recall, shop and
        /// camera lessons with the client's own packets (a summoner cast, a cast at the enemy it attacks, Recall, 0x60, 0x87, 0x30).
        /// </summary>
        private void PilotTutorial()
        {
            if (Runtime.Waves?.LevelScript is not { TutorialLoaded: true } level) return;
            var seat = Runtime.Session.Participants[0];
            if (++_pilotStep % 30 != 0 || seat.Hero is not { Alive: true } hero || seat.ConnectionId is not long connection) return;
            var gameplay = Runtime.Dependencies.Gameplay;
            var navigation = Runtime.Navigation;
            // Batch 593: every packet goes through the session's own wire boundary (Receive) the way the stock client sends it: the whole
            // blocks of a channel-1 packet Blowfish-encrypted and the tail clear. The answer is the gameplay dispatch's acceptance.
            bool Wire(byte[] payload)
            {
                var blocks = payload.Length - payload.Length % LegacyBlowfishCipher.BlockSize;
                Send(Runtime.Session.Receive(connection, 1, [.. Runtime.Cipher.Encrypt(payload.AsSpan(0, blocks)), .. payload[blocks..]], gameplay));
                return Runtime.Session.LastGameplayAccepted;
            }
            bool Cast(int slot, bool summoner, Position2 at) =>
                Wire(SeatOrders.CastPayload(hero.NetId, slot, summoner, at, navigation.HeightAt(at), at, navigation.HeightAt(at), 0));
            // The popup's OK (C2S 0xD5) and each audio event's end (C2S 0x05 with its id), 3 s after each opened.
            var now = Runtime.Combat.CurrentTime;
            var (popupAt, audio) = level.TutorialPendingClientAnswers();
            if (popupAt is double opened && now >= opened + AcknowledgeSeconds) Wire([TutorialPackets.PopupClosedClientOpcode, 0, 0, 0, 0]);
            foreach (var (id, at) in audio)
                if (now >= at + AcknowledgeSeconds) Wire([TutorialPackets.AudioEventFinishedClientOpcode, 0, 0, 0, 0, .. BitConverter.GetBytes(id)]);
            // NpcUpgradeSpellReq (0x3E: the hero, then the slot), as the owner's client sent it ("3e0100004001", batch 593).
            if (level.TutorialLevelUpSlot() is int slot) Wire([0x3E, .. BitConverter.GetBytes(hero.NetId), (byte)slot]);
            // The Heal lesson: the summoner slot the avatar-cast wait watches, cast on the player.
            if (level.TutorialWaitValue("SpellAvatarCast") is double summoner) Note("summoner", Cast((int)summoner, true, hero.Position));
            // The shop lesson: the client's OnShopOpened, then BuyItemReq (0x87: the hero, the item) for the awaited item.
            if (level.TutorialWaitValue("ShopOpened") is not null) Wire([TutorialPackets.ShopOpenedClientOpcode, 0, 0, 0, 0]);
            if (level.TutorialWaitValue("ItemPurchased") is double item)
            {
                Wire([0x87, .. BitConverter.GetBytes(hero.NetId), .. BitConverter.GetBytes((uint)item)]);
                Note("buy", hero.Inventory.Any(held => held?.ItemId == (int)item));
            }
            // The camera lessons: a camera report looking straight down at the unit the wait names.
            if (level.TutorialCameraGoal() is { } seen)
                Wire([TutorialPackets.CameraClientOpcode, 0, 0, 0, 0, .. BitConverter.GetBytes((float)seen.Position.X), .. BitConverter.GetBytes(1000f),
                      .. BitConverter.GetBytes((float)seen.Position.Z), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(-1f),
                      .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(seat.ClientId), (byte)_pilotStep]);
            var goal = level.TutorialPlayerGoal();
            // The recall lesson (EventCastRecall, then EventRapidReplenish's near-callback at the start point): Recall once and wait out its
            // channel (a move order would end it), then walk the rest.
            if (level.TutorialStepRan("EventCastRecall") && !level.TutorialStepRan("EventGold") && goal is { Target: null })
            {
                if (_recallAt is null && Cast(SpellSystem.RecallSlot, false, hero.Position))
                {
                    _recallAt = Runtime.Combat.CurrentTime;
                    Note("recall", true);
                }
                if (_recallAt is double recalled && Runtime.Combat.CurrentTime < recalled + 10.0) return;
            }
            if (goal is null) return;
            // The Volley lesson: the spell slot the cast wait watches, aimed at the enemy the player is sent to kill.
            if (level.TutorialWaitValue("SpellCast") is double spell && goal.Value.Target is { } aim) Note("spell", Cast((int)spell, false, aim.Position));
            var (point, target) = goal.Value;
            var payload = ClientGameplayPackets.MoveOrder(hero.NetId, target is null ? (byte)2 : (byte)3, point, target?.NetId ?? 0).Plaintext.ToArray();
            var accepted = Wire(payload);
            // An enemy in the fog refuses the attack order: walk at it until it is in sight (batch 580).
            if (!accepted && target is not null)
                accepted = Wire(ClientGameplayPackets.MoveOrder(hero.NetId, 2, point, 0).Plaintext.ToArray());
            var order = (target?.NetId ?? 0, Math.Round(point.X), Math.Round(point.Z), accepted);
            if (order == _lastPilotOrder) return;
            _lastPilotOrder = order;
            _log.Write("GAMEPLAY", "INFO", "tutorial.pilot", new Dictionary<string, object?>
            {
                ["target"] = target?.NetId, ["target_name"] = target?.Name, ["x"] = order.Item2, ["z"] = order.Item3,
                ["accepted"] = accepted, ["hero"] = new[] { Math.Round(hero.Position.X), Math.Round(hero.Position.Z) },
            });

            void Note(string lesson, bool sent) => _log.Write("GAMEPLAY", "INFO", "tutorial.pilot", new Dictionary<string, object?>
            {
                ["lesson"] = lesson, ["accepted"] = sent, ["hero"] = new[] { Math.Round(hero.Position.X), Math.Round(hero.Position.Z) },
                ["gold"] = Math.Round(hero.Gold), ["items"] = string.Join(",", hero.Inventory.Where(held => held is not null).Select(held => held!.ItemId)),
            });
        }

        private (uint, double, double, bool) _lastPilotOrder;
        private double? _recallAt;

        /// <summary>DIAGNOSTICS: how long the stand-in "listens" to a popup or a voice line before it answers (batch 563's 3 s).</summary>
        private const double AcknowledgeSeconds = 3.0;

        private void StepOnce()
        {
            _options.OnStep?.Invoke(Runtime);
            var session = Runtime.Session;
            // The seat's connection events arrive with the native events, ahead of the scheduler pass (MatchRuntimeNetwork.Pump).
            if (session.Started) foreach (var director in _reconnects) director.BeforePump();
            // MatchRuntimeNetwork.Pump then the host's tick, in the host's order.
            EnsureObservers();
            Send(session.RunSchedulers());
            EnsureObservers();
            if (!session.Started)
            {
                // The driver advances the virtual clock only once the match has started.
                Runtime.World.Clock.Advance(Step);
                return;
            }
            if (!_startLogged)
            {
                _startLogged = true;
                _startedAt = Runtime.Combat.CurrentTime;
                _log.Write("GAMEPLAY", "INFO", "match.started",
                    new Dictionary<string, object?> { ["rng_seed"] = _options.Seed });
                Runtime.Delivery.Flush();
            }
            // Wall time of the simulation step alone (the host's Tick), the number a live server must keep under 33 ms.
            var started = Stopwatch.GetTimestamp();
            var plannerCalls = Runtime.Navigation.FindPathAroundUnitsCalls;
            var planningWork = Runtime.Navigation.PlanningWork;
            var gcPause = GC.GetTotalPauseDuration();
            Runtime.Tick(Step);
            if (_options.MapId == GameMaps.Tutorial) PilotTutorial();
            // MatchRuntimeNetwork.Tick: the returning seat's state burst follows the simulation step and its delivery flush.
            // MatchRuntimeNetwork.SendReconnectStates takes the ready returning seats once; each goes to the director of its seat.
            if (_reconnects.Count != 0)
                foreach (var participant in session.ConsumeReconnects())
                    _reconnects.FirstOrDefault(director => director.Owns(participant))?.Burst(participant);
            foreach (var director in _reconnects) director.AfterTick();
            // DIAGNOSTICS (diagnostic, batch 219): remember each owned non-hero unit's name when it is first seen, so pets that die before the end count too.
            foreach (var unit in Runtime.World.Entities.Values)
                if (unit.UnitType != UnitType.Hero && unit.OwnerNetId != 0) _petNames[unit.NetId] = unit.Name;
            _stepMs.Add((Runtime.Combat.CurrentTime - _startedAt, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Runtime.Navigation.FindPathAroundUnitsCalls - plannerCalls,
                Runtime.Navigation.PlanningWork - planningWork,
                (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds));
            _heroMotion.Observe(Runtime.Combat.CurrentTime - _startedAt,
                session.Participants.Where(seat => seat.Hero is not null).Select(seat => seat.Hero!), Runtime.World.Entities.Values, Runtime.Navigation);
        }

        /// <summary>MatchRuntimeNetwork.EnsureObservers: a connected, created seat gets one observer sharing its view.</summary>
        private void EnsureObservers()
        {
            var seats = Runtime.Session.Participants;
            for (var index = 0; index < seats.Count; index++)
            {
                var seat = seats[index];
                if (seat.ConnectionId is null || seat.Hero is null) continue;
                if (_observers[index] is { } existing)
                {
                    existing.Ready = seat.Ready;
                    continue;
                }
                // The production constructor: a returning seat's death recap waits for its reconnect burst.
                var observer = MatchRuntimeNetwork.NewObserver(Runtime, seat, new PeerTransport(this, index));
                Runtime.Delivery.AddObserver(observer);
                _observers[index] = observer;
            }
        }

        private void Send(IEnumerable<SessionOutboundPacket> outputs)
        {
            // A connection that is gone has no peer: MatchRuntimeNetwork.Send drops such a packet ("missing-peer-drop").
            foreach (var output in outputs)
                if (_peerOf.TryGetValue(output.ConnectionId, out var peer)) Deliver(peer, output.Packet);
        }

        /// <summary>One server-to-peer packet, as ENet would carry it: counted, checked and, for two opcodes, traced.</summary>
        internal void Deliver(int peer, PlaintextPacket packet)
        {
            PacketsChecked++;
            foreach (var director in _reconnects) director.NoteDelivery(peer, packet.Plaintext.Span);
            var now = Runtime.World.Clock.Now();
            // Unsequenced datagrams are not reliable either; the checker's flag means "ENet reliable".
            // The checker models the client from the full stream the delivery produced: the replication store only leaves out
            // what the client already holds, so the rules (sync ids, zeroed stats, lost updates) are the same for both streams.
            _checker.Observe(new PacketObservation(now, peer, packet.Channel,
                packet.Reliability == LeaguePacketReliability.Reliable, packet.Plaintext));
            // The same filter MatchRuntimeNetwork.Send applies to every packet of a connection; the counts below are what is sent.
            var receiver = Runtime.Session.Participants[peer];
            var processed = receiver.View.Store.Process(packet, receiver.Hero?.NetId ?? 0);
            if (processed is null) return;
            packet = processed;
            PacketTap?.Invoke(peer, now, packet);
            _options.PacketSink?.Invoke(peer, packet);
            var plaintext = packet.Plaintext.Span;
            var kind = plaintext.Length == 0 ? $"ch{packet.Channel}/empty" : $"ch{packet.Channel}/0x{plaintext[0]:X2}";
            var counted = _opcodeCounts.GetValueOrDefault(kind);
            _opcodeCounts[kind] = (counted.Count + 1, counted.Bytes + plaintext.Length);
            // 0x0D: attacker at 1..5 and the swing's missile id at 10..14 (CombatPackets.BasicAttack).
            if (plaintext.Length >= 15 && plaintext[0] == 0x0D)
            {
                var attacker = BinaryPrimitives.ReadUInt32LittleEndian(plaintext[1..]);
                if (attacker >= TurretNetIds.Base && attacker < TurretNetIds.Ceiling)
                    _turretShots.Add((attacker, BinaryPrimitives.ReadUInt32LittleEndian(plaintext[10..])));
            }
            // 0x80 SpawnMinion: net id at 5..9 and the unit name at 39. One lane minion is one net id,
            // however many peers the create reaches; the first one carries the name.
            if (plaintext.Length >= 43 && plaintext[0] == 0x80)
            {
                var spawned = BinaryPrimitives.ReadUInt32LittleEndian(plaintext[5..]);
                if (spawned >= MinionNetIds.Base && spawned < MinionNetIds.Ceiling && !_laneSpawns.ContainsKey(spawned))
                {
                    var create = Amberline.Network.Diagnostics.WirePacketDecoder.Decode(plaintext, packet.Channel);
                    if (create.Fields.TryGetValue("unit_name", out var carried) && carried is string unitName &&
                        unitName.Contains("Minion_", StringComparison.Ordinal))
                        _laneSpawns[spawned] = (GameSeconds, unitName);
                }
            }
            if (plaintext.Length >= 15 && plaintext[0] == 0xCC && plaintext[9] == 1)
            {
                var shape = new StringBuilder($"sm={plaintext[10]:X2}");
                var at = 15;
                for (var registry = 0; registry < 5 && at + 4 <= plaintext.Length; registry++)
                {
                    if ((plaintext[10] & (1 << registry)) == 0) continue;
                    var mask = BinaryPrimitives.ReadUInt32LittleEndian(plaintext[at..]);
                    shape.Append($" r{registry}:{mask:X}");
                    at += 4 + 4 * BitOperations.PopCount(mask);
                }
                var shaped = _replicationShapes.GetValueOrDefault(shape.ToString());
                _replicationShapes[shape.ToString()] = (shaped.Count + 1, shaped.Bytes + plaintext.Length);
            }
            // Triage reads the StartGame time (HUD zero) and the hero roster from outbound PKT lines;
            // tracing every packet would be gigabytes, so only seat 0's CreateHero and StartGame are written.
            if (peer == 0 && plaintext.Length > 0 && plaintext[0] is 0x5F or 0x4F)
                _log.Write("PKT", "DEBUG", "->", new Dictionary<string, object?>
                {
                    ["dir"] = "->",
                    ["op"] = $"0x{plaintext[0]:X2}",
                    ["ch"] = packet.Channel,
                    ["player_id"] = peer + 1,
                    ["raw"] = Convert.ToHexString(plaintext).ToLowerInvariant(),
                });
        }

        /// <summary>Runs the checker's end-of-match pass and logs its violations; returns their count.</summary>
        public long FinishChecker(MatchLog log)
        {
            var violations = _checker.Finish();
            WritePacketViolations(log, violations);
            return violations.Count;
        }

        /// <summary>
        /// DIAGNOSTICS: drives one seat's connection through a drop and the production reconnect path, as the network shell does for a stock
        /// client that crashed and was started again: the lost connection (<c>MatchRuntimeNetwork.Detach</c>), the new generation's key check
        /// (<c>DuelSession.AuthenticateReconnect</c>), the handshake and, a load time later, the 0xC6 (<c>RunSchedulers</c> then runs
        /// <c>SpawnReconnect</c> and the ready pass), the observer attached by the production constructor, the observer flush, and the seat-local
        /// burst (<c>MatchRuntimeNetwork.BuildReconnectState</c>, <c>SendReconnectStates</c>). While the seat is away its hero is released from the
        /// bot scripts, as an absent player's would stand still; it goes back to them once the burst has been judged.
        /// </summary>
        internal sealed class ReconnectDirector
        {
            private const double JudgeDelaySeconds = 1.0;
            private enum Phase { Waiting, Dropped, Loading, Joining, Judging, Done }

            private readonly Driver _driver;
            private readonly ReconnectPlan _plan;
            private readonly MatchLog _log;
            private readonly List<string> _findings = [];
            private Phase _phase = Phase.Waiting;
            private long _oldConnection, _newConnection;
            private double? _droppedAt, _connectedAt, _loadedAt, _spawnedAt, _readyAt, _burstAt, _judgedAt;
            private int _burstPackets;
            private bool _aliveAtDrop, _aliveAtBurst;
            private ReconnectJudgement? _judgement;
            private string _outcome = "not-triggered";

            public ReconnectDirector(Driver driver, ReconnectPlan plan, MatchLog log)
            {
                _driver = driver;
                _plan = plan;
                _log = log;
            }

            private MatchRuntime Runtime => _driver.Runtime;
            private SessionParticipant Seat => Runtime.Session.Participants[_plan.Seat];
            private double Now => _driver.GameSeconds;

            public ReconnectReport Report() => new(_plan.Seat, Seat.ChampionName, _plan.When, _outcome, _droppedAt, _connectedAt, _loadedAt,
                _spawnedAt, _readyAt, _burstAt, _burstPackets, _judgedAt, _aliveAtDrop, _aliveAtBurst, _judgement, _findings);

            private void Event(string message, params (string Key, object? Value)[] fields)
            {
                var properties = new Dictionary<string, object?> { ["seat"] = _plan.Seat, ["player_id"] = Seat.PlayerId, ["game_time"] = Math.Round(Now, 3) };
                foreach (var (key, value) in fields) properties[key] = value;
                _log.Write("NET", "INFO", message, properties);
            }

            private void Finding(string text)
            {
                _findings.Add($"t={Now:0.0}: {text}");
                _driver._checker.AddFinding("reconnect-handshake", _plan.Seat, _driver.Runtime.World.Clock.Now(), Seat.Hero?.NetId, text);
            }

            /// <summary>
            /// Batch 77: the stock client creates a hero in the middle of its own world load and faults when the Lua state is not there yet
            /// (an access violation), so nothing that creates or replicates the world may reach a returning client before its 0xC6.
            /// </summary>
            public void NoteDelivery(int peer, ReadOnlySpan<byte> plaintext)
            {
                if (_phase != Phase.Loading || peer != _plan.Seat || plaintext.Length == 0 || plaintext[0] is not (0x4F or 0x65 or 0xCC or 0x5F)) return;
                Finding($"opcode 0x{plaintext[0]:X2} reached the new client {Now - (_connectedAt ?? Now):0.0} s after its handshake, before its 0xC6");
            }

            /// <summary>Connection events: the drop, the new connection's key check and handshake, and its 0xC6.</summary>
            public void BeforePump()
            {
                var session = Runtime.Session;
                switch (_phase)
                {
                    case Phase.Waiting when Triggered():
                        Drop();
                        break;
                    case Phase.Dropped when Now + 1e-9 >= _droppedAt + _plan.DropSeconds:
                        Connect();
                        break;
                    case Phase.Loading:
                        // The hero burst waits for the client's own 0xC6 (batch 77): nothing of the world may reach the new client before it.
                        if (Seat.Spawned || Seat.Ready)
                            Finding($"the seat was spawned ({Seat.Spawned}) or ready ({Seat.Ready}) before its 0xC6; the stock client crashes when a CreateHero reaches it while it loads");
                        if (Now + 1e-9 >= _connectedAt + _plan.LoadSeconds)
                        {
                            _driver.Send(session.Receive(_newConnection, 1, [0xC6, 0, 0, 0, 0], Runtime.Dependencies.Gameplay));
                            _loadedAt = Now;
                            Event("reconnect.loaded", ("connection_id", _newConnection));
                            _phase = Phase.Joining;
                        }
                        break;
                    case Phase.Joining:
                        break;
                }
                if (_phase is Phase.Joining or Phase.Judging)
                {
                    if (_spawnedAt is null && Seat.Spawned) { _spawnedAt = Now; Event("reconnect.spawned"); }
                    if (_readyAt is null && Seat.Ready) { _readyAt = Now; Event("reconnect.ready"); }
                }
            }

            // Tool-only state a BuildingBlocks script would leave on the hero; the reconnect record is docs/maintenance/2026-10-19-w20e-headless-reconnect.md. It is written straight to
            // the entity, because the point is what the burst replays, not how the state arose. Undone after the judgement.
            private List<Action>? _undo;

            private void Prime(Entity hero)
            {
                _undo = [];
                var now = Runtime.Spells.CurrentTime;
                for (var slot = 0; slot < hero.SpellCooldowns.Count; slot++)
                {
                    hero.SpellCooldowns[slot] = 30.0 + 7.0 * slot;
                    var index = slot;
                    _undo.Add(() => hero.SpellCooldowns[index] = 0.0);
                }
                if (hero.SummonerCooldowns.Count > 0) hero.SummonerCooldowns[0] = 180.0;
                hero.ScriptSlotReadyAt[43] = now + 25.0;
                _undo.Add(() => hero.ScriptSlotReadyAt.Remove(43));
                hero.SpellSeals.Set(false, 2, true);
                _undo.Add(() => hero.SpellSeals.Set(false, 2, false));
                while (hero.ActiveSpellNames.Count <= 44) hero.ActiveSpellNames.Add("");
                hero.ActiveSpellNames[44] = "SummonerHeal";
                _undo.Add(() => hero.ActiveSpellNames[44] = "");
                hero.SpellTargetingOverrides[1] = 6;
                _undo.Add(() => hero.SpellTargetingOverrides.Remove(1));
                // A pushed layer that carries spells re-initialises the client's book: everything above has to come after it in the burst.
                // Through the production sink, as BBPushCharacterData sends it: the old client gets it and the persistent character-data ledger
                // holds it, so the new client is given it by the observer flush (or by the burst, for an owner the flush does not reach).
                var layer = new ScriptCharacterLayer(900000 + (uint)_plan.Seat, "Nidalee_Cougar", true);
                hero.ScriptCharacterData = layer;
                Runtime.Spells.Effects.SendPushCharacterData(hero, layer.Id, layer.SkinName, layer.OverrideSpells);
                _undo.Add(() =>
                {
                    hero.ScriptCharacterData = null;
                    Runtime.Spells.Effects.SendPopCharacterData(hero, layer.Id);
                });
            }

            private bool Triggered()
            {
                if (Now < _plan.AtSeconds) return false;
                var hero = Seat.Hero;
                if (hero is null || Seat.ConnectionId is null) return false;
                return _plan.When switch
                {
                    "dead" => !hero.Alive,
                    "fight" => hero.Alive && Runtime.World.Heroes().Any(other => other.Alive && other.Team != hero.Team &&
                        ChampionSpellDistance(hero, other) <= 1000.0),
                    _ => true,
                };
            }

            private static double ChampionSpellDistance(Entity a, Entity b)
            {
                var dx = a.Position.X - b.Position.X;
                var dz = a.Position.Z - b.Position.Z;
                return Math.Sqrt(dx * dx + dz * dz);
            }

            private void Drop()
            {
                var seat = Seat;
                _oldConnection = seat.ConnectionId!.Value;
                _aliveAtDrop = seat.Hero!.Alive;
                _droppedAt = Now;
                Runtime.Bots?.SetSeatScripted(seat.PlayerId, true);
                if (_plan.Prime) Prime(seat.Hero);
                _log.Write("NET", "INFO", "connection.disconnected", new Dictionary<string, object?>
                {
                    ["connection_id"] = _oldConnection, ["player_id"] = seat.PlayerId, ["reason"] = "headless-drop",
                    ["game_time"] = Math.Round(Now, 3), ["seat"] = _plan.Seat, ["hero_alive"] = _aliveAtDrop,
                });
                // MatchRuntimeNetwork.Detach: the presence notice for the other seats, the detached hero's stop and re-anchor packets queued
                // for the remaining observers, the seat's observer removed.
                _driver.Send(MatchRuntimeNetwork.DetachSeat(Runtime, _oldConnection));
                if (_driver._observers[_plan.Seat] is { } observer) Runtime.Delivery.RemoveObserver(observer.ObserverId);
                _driver._observers[_plan.Seat] = null;
                _driver._peerOf.Remove(_oldConnection);
                Runtime.MovementTrace?.ForgetConnection(_oldConnection);
                _phase = Phase.Dropped;
            }

            private void Connect()
            {
                var session = Runtime.Session;
                _newConnection = 1000 + _plan.Seat;
                _driver._peerOf[_newConnection] = _plan.Seat;
                // A restarted client knows nothing: the model of the old one is dropped here.
                _driver._checker.ResetPeer(_plan.Seat, Runtime.World.Clock.Now());
                _connectedAt = Now;
                // The key check (channel 0) as MatchRuntimeNetwork.Pump hands it to the session once the connection is accepted, then the
                // synch-version request the stock client sends right after.
                _driver.Send(session.Receive(_newConnection, 0, session.BuildKeyCheck(Seat.PlayerId), Runtime.Dependencies.Gameplay));
                if (session.Participant(_newConnection) is null)
                {
                    _outcome = "rejected";
                    Finding("the session rejected the returning seat's key check (AuthenticateReconnect returned null)");
                    _phase = Phase.Done;
                    Runtime.Bots?.SetSeatScripted(Seat.PlayerId, false);
                    return;
                }
                _driver.Send(session.Receive(_newConnection, 1, [0xC5, 0, 0, 0, 0], Runtime.Dependencies.Gameplay));
                Event("reconnect.connected", ("connection_id", _newConnection));
                _phase = Phase.Loading;
            }

            /// <summary>
            /// One seat of <c>DuelSession.ConsumeReconnects</c>, which the driver calls once per step for every seat (the session hands each ready
            /// returning seat out once): the burst of <c>MatchRuntimeNetwork.SendReconnectStates</c>, built and delivered for this director's seat.
            /// </summary>
            public void Burst(SessionParticipant participant)
            {
                var session = Runtime.Session;
                if (_phase != Phase.Joining || participant.ConnectionId is not long connection ||
                    !_driver._peerOf.TryGetValue(connection, out var peer) || participant.Hero is null) return;
                var state = MatchRuntimeNetwork.BuildReconnectState(Runtime, participant);
                foreach (var packet in Damaged(state)) _driver.Deliver(peer, packet);
                _driver.Send(session.BuildReconnectNotice(participant));
                if (_driver._observers[peer] is { } observer) observer.DeathRecapReady = true;
                _burstAt = Now;
                _burstPackets = state.Count;
                _aliveAtBurst = participant.Hero.Alive;
                Event("reconnect.burst", ("packets", state.Count), ("hero_alive", _aliveAtBurst));
                _phase = Phase.Judging;
            }

            /// <summary>Whether this director is the one for <paramref name="participant"/>'s seat.</summary>
            public bool Owns(SessionParticipant participant) => ReferenceEquals(participant, Seat);

            /// <summary>After the burst: the judgement a moment later, and the end of the scenario if the match ends first.</summary>
            public void AfterTick()
            {
                var session = Runtime.Session;
                if (_phase == Phase.Judging && Now + 1e-9 >= _burstAt + JudgeDelaySeconds)
                {
                    if (_readyAt is null && Seat.Ready) { _readyAt = Now; Event("reconnect.ready"); }
                    Judge();
                    foreach (var undo in _undo ?? []) undo();
                    Runtime.Bots?.SetSeatScripted(Seat.PlayerId, false);
                    _phase = Phase.Done;
                }
                else if (_phase is Phase.Dropped or Phase.Loading or Phase.Joining && session.Result is not null)
                {
                    _outcome = "match-ended-first";
                    _phase = Phase.Done;
                }
            }

            // The control for the checker: the burst as a buggy server would have sent it.
            private IEnumerable<PlaintextPacket> Damaged(IReadOnlyList<PlaintextPacket> state) => _plan.Fault switch
            {
                "layer-last" => LayerLast(state),
                "no-cooldowns" => state.Where(packet => packet.Plaintext.Span[0] != 0x8A),
                "no-names" => state.Where(packet => packet.Plaintext.Span[0] is not (0x73 or 0x35)),
                _ => state,
            };

            // The burst of BuildReconnectState carries a hero's script layers only when the observer flush did not give them to the new client first
            // (a dead hero); a visible one gets them from the flush ahead of the burst, so there is nothing in the burst to move. The buggy server
            // this control stands for sends the layers after everything else: the ledger's push again, last (a second push of the same change id, which
            // is also a finding, and a layer that carries spells re-initialises the book the names, ranks and cooldowns before it had filled).
            private IEnumerable<PlaintextPacket> LayerLast(IReadOnlyList<PlaintextPacket> state)
            {
                var layers = state.Where(packet => packet.Plaintext.Span[0] == 0x9F).ToList();
                if (layers.Count == 0 && Seat.Hero is { } hero)
                    layers.AddRange(Runtime.Delivery.CharacterDataOf(hero.NetId).Select(model => model.Packet));
                return state.Where(packet => packet.Plaintext.Span[0] != 0x9F).Concat(layers);
            }

            private void Judge()
            {
                var seat = Seat;
                var hero = seat.Hero!;
                var truth = TruthOf(seat, hero);
                _judgement = _driver._checker.JudgeReconnect(_plan.Seat, Runtime.World.Clock.Now(), truth);
                _judgedAt = Now;
                _outcome = _judgement.Violations == 0 && _findings.Count == 0 ? "clean" : "violations";
                Event("reconnect.judged", ("names", _judgement.NamesChecked), ("ranks", _judgement.RanksChecked),
                    ("cooldowns", _judgement.CooldownsChecked), ("units", _judgement.UnitsChecked), ("items", _judgement.ItemsChecked),
                    ("violations", _judgement.Violations));
            }

            private ReconnectTruth TruthOf(SessionParticipant seat, Entity hero)
            {
                var champion = Runtime.GameData.Champion(hero.ChampionName);
                var names = new Dictionary<BookSlot, (string, bool)>();
                bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                for (var slot = 0; slot < 4; slot++)
                {
                    var seed = slot < champion.SpellNames.Count ? champion.SpellNames[slot] : "";
                    var live = hero.ActiveSpellNames.Count > slot && !string.IsNullOrWhiteSpace(hero.ActiveSpellNames[slot]) ? hero.ActiveSpellNames[slot] : seed;
                    if (live.Length != 0) names[new BookSlot(false, slot)] = (live, Same(live, seed));
                }
                for (var slot = 4; slot < SpellSlotSeals.SlotCount; slot++)
                {
                    var seed = slot >= 42 && ExtraSpellSeed(hero.ChampionName, slot - 42) is { Length: > 0 } extra && slot < 50 ? extra : "";
                    var live = hero.ActiveSpellNames.Count > slot && !string.IsNullOrWhiteSpace(hero.ActiveSpellNames[slot]) ? hero.ActiveSpellNames[slot] : seed;
                    // An item slot (4-41) holds the item's own spell, created natively by SetItem: only a name a script wrote is sent.
                    if (live.Length != 0 && (slot >= 42 || hero.ActiveSpellNames.Count > slot && hero.ActiveSpellNames[slot].Length != 0))
                        names[new BookSlot(false, slot)] = (live, Same(live, seed));
                }
                var loadout = new[] { seat.SummonerSpell1, seat.SummonerSpell2 };
                for (var slot = 0; slot < hero.SummonerSpells.Count && slot < 2; slot++)
                    if (!string.IsNullOrWhiteSpace(hero.SummonerSpells[slot]))
                        names[new BookSlot(true, slot)] = (hero.SummonerSpells[slot], Same(hero.SummonerSpells[slot], loadout[slot]));

                var cooldowns = new Dictionary<BookSlot, double>();
                void Cool(BookSlot key, double seconds) { if (seconds > 0.0) cooldowns[key] = seconds; }
                for (var slot = 0; slot < hero.SpellCooldowns.Count; slot++) Cool(new BookSlot(false, slot), hero.SpellCooldowns[slot]);
                for (var slot = 0; slot < hero.SummonerCooldowns.Count; slot++) Cool(new BookSlot(true, slot), hero.SummonerCooldowns[slot]);
                for (var slot = 0; slot < hero.ItemCooldowns.Count; slot++) Cool(new BookSlot(false, slot + 4), hero.ItemCooldowns[slot]);
                foreach (var (slot, readyAt) in hero.ScriptSlotReadyAt)
                    if (slot >= 42) Cool(new BookSlot(false, slot), readyAt - Runtime.Spells.CurrentTime);

                var ranks = Enumerable.Range(0, hero.SpellRanks.Count).ToDictionary(slot => slot, slot => hero.SpellRanks[slot]);
                var targeting = hero.SpellTargetingOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);

                (uint, string, bool)? layer = null;
                if (hero.ScriptCharacterData is { } script) layer = (script.Id, script.SkinName, script.OverrideSpells);
                var buried = hero.BuriedCharacterLayers.Select(under => (under.Id, under.SkinName, under.OverrideSpells)).ToArray();

                var snapshot = Runtime.HeroStats.Snapshot(hero);
                // The burst's own clamps (MatchRuntimeNetwork.BuildReconnectState): a slot is its item id, a stack count and the charges, each a byte.
                var inventory = hero.Inventory.Select(slot => slot is null ? ((int, int, int)?)null
                    : (Math.Max(0, slot.ItemId), Math.Clamp(slot.Count, 0, byte.MaxValue), Math.Clamp(slot.Charges, 0, byte.MaxValue))).ToArray();
                return new ReconnectTruth(hero.NetId, hero.Alive, names, ranks, cooldowns, targeting, layer,
                    snapshot.CanCastBits1, snapshot.CanCastBits2, seat.View.Known.ToArray(), inventory,
                    Math.Clamp(hero.SkillPoints, 0, byte.MaxValue), buried);
            }

            private static string ExtraSpellSeed(string champion, int index)
            {
                var raw = GameData.Default.Champion(champion).Raw;
                return raw.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{index + 1}"), out var value) && value.Kind == InibinValueKind.String
                    ? value.AsString() : "";
            }
        }
    }

    private sealed class PeerTransport(Driver driver, int peer) : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) => driver.Deliver(peer, packet);
    }

    /// <summary>
    /// The live session.jsonl record shape with the virtual match clock for <c>t</c>/<c>mono_ns</c> and a
    /// seed-derived session id, so equal seeds write byte-identical files.
    /// </summary>
    internal sealed class MatchLog : ISessionLogSink, IDisposable
    {
        private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
            { "t", "cat", "lvl", "msg", "mono_ns", "session_id" };
        private readonly StreamWriter _file;

        public MatchLog(string directory, int seed)
        {
            // An int32 in the first Guid field: distinct per seed, no clock and no randomness.
            SessionId = new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString();
            _file = new StreamWriter(Path.Combine(directory, "session.jsonl"), append: false,
                new UTF8Encoding(false)) { AutoFlush = false };
        }

        public string SessionId { get; }
        public Func<double> Clock { get; set; } = () => 0.0;
        public MatchResult? Result { get; private set; }
        public long InvariantEvents { get; private set; }

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null) =>
            WriteAt(Clock(), category, level, message, properties);

        public void WriteAt(double seconds, string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            var t = Math.Round(seconds, 4, MidpointRounding.ToEven);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (properties is not null)
                foreach (var property in properties)
                    if (!Reserved.Contains(property.Key)) record[property.Key] = property.Value;
            record["t"] = t;
            record["cat"] = category;
            record["lvl"] = level;
            record["msg"] = message;
            record["mono_ns"] = (long)Math.Round(t * 1e9);
            record["session_id"] = SessionId;
            _file.WriteLine(JsonSerializer.Serialize(record));
            if (category == "INVARIANT" && !message.EndsWith("-end", StringComparison.Ordinal)) InvariantEvents++;
            if (level is "ERROR") _file.Flush();
        }

        public void MatchCompleted(MatchResult result)
        {
            Result = result;
            Write("GAMEPLAY", "INFO", "match.ended", new Dictionary<string, object?> { ["result"] = result });
        }

        public void Flush() => _file.Flush();

        public void Dispose() => _file.Dispose();

        /// <summary>Stack traces name build paths; the owner's profile folder must not reach a file.</summary>
        public static string RedactUserPaths(string text) =>
            Regex.Replace(text, @"(?i)\b[A-Z]:[\\/]Users[\\/][^\s\\/""]+", "[user path]");
    }
}
