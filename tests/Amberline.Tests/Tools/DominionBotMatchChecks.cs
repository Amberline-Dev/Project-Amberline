using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Bots;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// Batch 463: Dominion bots (NOT RETAIL: the 1.0.0.126 files ship no Dominion bot logic). One seeded headless match on The Crystal Scar
/// (map 8, ten bots, at most 25 minutes), watched every step. Serial checks run in a child process each, so the one match is one check and
/// every criterion's failure is reported together.
/// </summary>
public static class DominionBotMatchChecks
{
    private const int Seed = 9801;
    private const double Minutes = 25.0;
    private const double PointRadius = 600.0;
    // Batch 536: more than one step's walk of a wave minion (seed 9801: about 14 units a step on point B, 30 steps a second).
    private const double MinionStepMargin = 50.0;

    [Check("dominion.bots-play-a-map-8-match", Serial = true, TimeoutSeconds = 900)]
    public static void BotsPlayAMapEightMatch()
    {
        var seen = Play();
        var failures = new List<string>();
        void Expect(bool condition, string message) { if (!condition) failures.Add(message); }

        // The opening: the bots wait in their base while the ceremony holds them (its circle is 1200, batch 458) and leave on the release.
        Expect(seen.ReleasedAt is not null, "the opening ceremony held and released the champions");
        Expect(seen.MaxHeldDistance < 1000.0, $"every bot waits in its base while held (farthest {seen.MaxHeldDistance:0} from its start point)");
        for (var seat = 0; seat < seen.LeftBaseAt.Length; seat++)
            Expect(seen.LeftBaseAt[seat] is { } left && seen.ReleasedAt is { } released && left - released <= 30.0,
                $"seat {seat} leaves its base within 30 s of the release (left {seen.LeftBaseAt[seat]?.ToString("0.0") ?? "never"})");
        // Every point is captured by some team within 3 minutes, and only through the capturing team's channel on that point's guardian.
        // The window is the shipped 3 minutes moved with the opening release (80 s here, NOT RETAIL; the shipped 45): 180 + 35 s.
        var window = 180.0 + (Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds
            - Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ShippedReleaseSeconds);
        for (var point = 0; point < seen.FirstCapture.Length; point++)
            Expect(seen.FirstCapture[point] is { } first && first <= window,
                $"point {(char)('A' + point)} is captured within {window:0} s (first capture {seen.FirstCapture[point]?.ToString("0.0") ?? "never"})");
        // Batch 536: or a wave minion of the capturing team on the point. The shipped tree consumes a friendly minion that reaches a point and
        // adds its capture rate (EvaluateCapturePoint "Adjust capture status for minions", OdinScriptBehaviorTrees.xml:528-810, MinionCaptureRate
        // 9 per minion), so a point can turn without a champion's channel once the waves reach it.
        Expect(seen.Captures > 0 && seen.CapturesWithoutChannel == 0,
            $"every capture follows a channel or a minion on the point ({seen.Captures} captures, {seen.CapturesWithoutChannel} without either, {seen.MinionCaptures} by minions)");
        // A bot below half health runs the shipped Retreat task.
        Expect(seen.RetreatAt is not null, "a bot below half health retreats");
        // One team's nexus reaches 0 within 25 minutes: the end-of-game chain runs (outcome natural, not the cap).
        Expect(seen.Report.Outcome == "natural" && seen.Report.Winner is "Order" or "Chaos", "one team wins before the cap: " + seen.Report.Summary());
        Expect(seen.Report.PacketViolations == 0 && seen.Report.LifecycleViolations == 0, "no packet or lifecycle violation");
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private sealed class Observation
    {
        public HeadlessMatchReport Report = null!;
        public readonly double?[] FirstCapture = new double?[5];
        public readonly List<string> Flips = [];
        public int Captures;
        public int CapturesWithoutChannel;
        public int MinionCaptures;
        public double? RetreatAt;
        public double MaxHeldDistance;
        public double? ReleasedAt;
        public readonly double?[] LeftBaseAt = new double?[10];
    }

    private static Observation Play()
    {
        var seen = new Observation();
        var root = Path.Combine(Path.GetTempPath(), "league-dominion-bots-" + Guid.NewGuid().ToString("N"));
        var owners = Enumerable.Repeat(Team.Neutral, 5).ToArray();
        var lastChannel = new double[5, 3];
        var lastMinion = new double[5, 3];
        for (var point = 0; point < 5; point++)
            for (var team = 0; team < 3; team++) lastChannel[point, team] = lastMinion[point, team] = double.NegativeInfinity;
        var home = new Position2?[10];
        try
        {
            seen.Report = HeadlessMatch.Run(new HeadlessMatchOptions(Seed, Path.Combine(root, "map8"), Minutes)
            {
                MapId = GameMaps.CrystalScar,
                OnStep = runtime =>
                {
                    if (!runtime.Session.Started || (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests is not { } quests ||
                        quests.Captures.Count < 5) return;
                    var now = runtime.Combat.CurrentTime;
                    var points = quests.Captures.Select(capture => capture.Variable("CapturePosition") is Position3 at
                        ? new Position2(at.X, at.Z) : new Position2(double.NaN, double.NaN)).ToArray();
                    var seats = runtime.Session.Participants;
                    for (var seat = 0; seat < seats.Count && seat < 10; seat++)
                    {
                        if (seats[seat].Hero is not { } hero) continue;
                        // The channel each hero holds, by point (its target is the point's guardian).
                        if (hero.Alive && runtime.Spells.CaptureChannelTarget(hero) is { } guardian)
                            for (var point = 0; point < 5; point++)
                                if (Distance(guardian.Position, points[point]) <= PointRadius) lastChannel[point, TeamIndex(hero.Team)] = now;
                        if (hero.Alive && hero.Health < 0.5 * hero.MaxHealth &&
                            runtime.Bots?.StateOf(seats[seat].PlayerId)?.Active?.Kind == BotTaskKind.Retreat)
                            seen.RetreatAt ??= now;
                        // The opening: the ceremony's hold circle is centred on the team's start point.
                        if (hero.MovementRestriction is { } hold)
                        {
                            home[seat] = hold.Centre;
                            seen.MaxHeldDistance = Math.Max(seen.MaxHeldDistance, Distance(hero.Position, hold.Centre));
                        }
                        else if (home[seat] is { } start)
                        {
                            seen.ReleasedAt ??= now;
                            if (Distance(hero.Position, start) > 1300.0) seen.LeftBaseAt[seat] ??= now;
                        }
                    }
                    // A wave minion on a point (the tree consumes it in the step it arrives, before this observer runs, so a step's walk of
                    // margin counts it on the step before).
                    foreach (var unit in runtime.World.Entities.Values)
                        if (unit.Alive && Amberline.Simulation.Minions.OdinWaves.ScriptOf(unit) is not null)
                            for (var point = 0; point < 5; point++)
                                if (Distance(unit.Position, points[point]) <= PointRadius + MinionStepMargin) lastMinion[point, TeamIndex(unit.Team)] = now;
                    for (var point = 0; point < 5; point++)
                    {
                        var owner = quests.Captures[point].Variable("CurrentPointOwner") is Team held ? held : Team.Neutral;
                        if (owner == owners[point]) continue;
                        var gainer = owner == Team.Neutral ? Other(owners[point]) : owner;
                        var channelled = now - lastChannel[point, TeamIndex(gainer)] <= 1.0;
                        var minion = now - lastMinion[point, TeamIndex(gainer)] <= 1.0;
                        seen.Flips.Add(FormattableString.Invariant($"{now:0.0}s {(char)('A' + point)} {owners[point]}->{owner} channel={channelled}{(minion ? " minion" : "")}"));
                        if (owner != Team.Neutral)
                        {
                            seen.Captures++;
                            seen.FirstCapture[point] ??= now;
                            if (!channelled && minion) seen.MinionCaptures++;
                            if (!channelled && !minion) seen.CapturesWithoutChannel++;
                        }
                        owners[point] = owner;
                    }
                },
            });
            Console.WriteLine(seen.Report.Summary());
            Console.WriteLine("flips: " + string.Join("; ", seen.Flips));
            Console.WriteLine(FormattableString.Invariant(
                $"first captures: {string.Join(", ", seen.FirstCapture.Select(t => t?.ToString("0.0") ?? "never"))}; retreat at {seen.RetreatAt?.ToString("0.0") ?? "never"}; held max {seen.MaxHeldDistance:0}; released {seen.ReleasedAt?.ToString("0.0") ?? "never"}; left base {string.Join(", ", seen.LeftBaseAt.Select(t => t?.ToString("0.0") ?? "never"))}"));
            return seen;
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static int TeamIndex(Team team) => team switch { Team.Order => 0, Team.Chaos => 1, _ => 2 };

    private static Team Other(Team team) => team == Team.Order ? Team.Chaos : team == Team.Chaos ? Team.Order : Team.Neutral;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
