using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Batch 468 (NOT RETAIL: the 1.0.0.126 files ship no Dominion bot logic). Two map 8 bots, one per team, stand at point A's guardian. When
/// both teams channel the one point, the shipped suppression ends both channels (batch 467), and a bot whose channel ends contested waits a
/// back-off before it channels on that point again: its channel starts are at least the back-off apart, not every 0.25 s.
/// </summary>
public static class DominionBotBackoffChecks
{
    private const double Step = 1.0 / 30.0;
    // The back-off floor (BotController.Dominion.cs, CaptureBackoffSeconds). A start-to-start gap is at least this, less one step's slack.
    private const double BackoffFloor = 4.0;
    private const double Slack = 0.1;
    // Capture point A of Map8 (Info_PointA.SCB centre, as the capture channel checks read it).
    private static readonly Position2 Point = PointA();

    private static Position2 PointA()
    {
        var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
        var centre = SceneMesh.CentralPoint(Path.Combine(scene, "Info_PointA.SCB"));
        return new Position2(centre.X, centre.Z);
    }

    [Check("dominion.bot-channel-backs-off-when-contested")]
    public static void BotsChannelOnAContestedPointOncePerBackoff()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            // DIAGNOSTICS: the headless match's seats (botsControlAllSeats): connected seats the bot scripts drive, as in the seeded match tool.
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar, random: new SeededRandomSource(9801),
                botsControlAllSeats: true);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            for (var id = 1; id <= roster.Count; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                _ = runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
            }
            TestAssert.True(runtime.Session.Start(), "start");
            Advance(runtime, Step);
            var guardian = runtime.World.Entities.Values
                .Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
                .OrderBy(unit => Distance(unit.Position, Point))
                .FirstOrDefault();
            TestAssert.True(guardian is not null, "point A has its guardian");

            // The opening ceremony holds the champions until its release (batch 458); the bots channel only after it.
            var heroes = runtime.Session.Participants.Select(seat => seat.Hero!).ToArray();
            TestAssert.True(Until(runtime, () => heroes.Any(hero => hero.MovementRestriction is not null), 30.0),
                "the opening ceremony holds the champions");
            TestAssert.True(Until(runtime, () => heroes.All(hero => hero.MovementRestriction is null), 90.0),
                "the opening ceremony releases both champions");
            // DIAGNOSTICS: test seam. The champions stand just inside the capture range of point A's guardian (600), on opposite sides of it, 1196 apart:
            // there KillHero (0.5 inside attack range + 500, falling to 0 at twice that) is below the capture task's 0.47, so the bots channel
            // rather than fight each other. Both seats' point is A: the first pick after the release is the nearest point from the fountain.
            var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests;
            TestAssert.True(quests is not null && quests.Captures.Count > 0, "map 8 runs its capture points");
            var pointA = Enumerable.Range(0, quests!.Captures.Count)
                .OrderBy(index => quests.Captures[index].Variable("CapturePosition") is Position3 at
                    ? Distance(new Position2(at.X, at.Z), Point) : double.MaxValue)
                .First();
            heroes[0].Position = new Position2(guardian!.Position.X, guardian.Position.Z + 598.0);
            heroes[1].Position = new Position2(guardian.Position.X, guardian.Position.Z - 598.0);
            foreach (var hero in heroes) runtime.Bots!.StateOf(hero)!.CapturePoint = pointA;

            var starts = new List<double>[heroes.Length];
            var channelling = new bool[heroes.Length];
            for (var seat = 0; seat < heroes.Length; seat++) starts[seat] = [];
            var end = runtime.Combat.CurrentTime + 60.0;
            while (runtime.Combat.CurrentTime < end)
            {
                runtime.Tick(Step);
                for (var seat = 0; seat < heroes.Length; seat++)
                {
                    var now = runtime.Spells.CaptureChannelTarget(heroes[seat]) is not null;
                    if (now && !channelling[seat]) starts[seat].Add(runtime.Combat.CurrentTime);
                    channelling[seat] = now;
                }
            }
            runtime.Delivery.Flush();

            var listed = string.Join(" / ", starts.Select((list, seat) =>
                $"seat {seat}: " + string.Join(", ", list.Select(t => t.ToString("0.00")))));
            listed += " || " + string.Join(" / ", heroes.Select((hero, seat) => runtime.Bots!.StateOf(hero) is { } bot
                ? $"seat {seat} plan {bot.Plan} point {bot.CapturePoint} task {bot.Active?.Kind} at ({hero.Position.X:0},{hero.Position.Z:0}) hp {hero.Health:0} alive {hero.Alive}"
                : $"seat {seat} no state"));
            for (var seat = 0; seat < heroes.Length; seat++)
            {
                TestAssert.True(starts[seat].Count >= 1, $"each seat channels on point A within 60 s ({listed})");
                for (var index = 1; index < starts[seat].Count; index++)
                {
                    var gap = starts[seat][index] - starts[seat][index - 1];
                    TestAssert.True(gap >= BackoffFloor - Slack,
                        $"seat {seat}: channel starts {gap:F2} s apart; a contested channel waits at least {BackoffFloor} s ({listed})");
                }
            }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) runtime.Tick(Step);
        runtime.Delivery.Flush();
    }

    /// <summary>Steps until <paramref name="done"/> holds or <paramref name="seconds"/> pass; whether it held.</summary>
    private static bool Until(MatchRuntime runtime, Func<bool> done, double seconds)
    {
        var end = runtime.Combat.CurrentTime + seconds;
        while (runtime.Combat.CurrentTime < end)
        {
            if (done()) return true;
            runtime.Tick(Step);
        }
        return done();
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
