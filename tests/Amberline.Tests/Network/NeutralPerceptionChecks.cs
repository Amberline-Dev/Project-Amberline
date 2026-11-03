using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// J-AUDIT F2: the production sight rule of a camp member (NeutralTargetVisible) had no range, so a camp chased a hero
/// 2,640 units away. A unit senses through its perception bubble (CharacterData PerceptionBubbleRadius, 1350 by default) with
/// line of sight, plus the 400-unit bubble a basic attack or flagged spell leaves at the attacker's position.
/// </summary>
public static class NeutralPerceptionChecks
{
    [Check("neutral.n2-perception-radius-from-data")]
    public static void PerceptionRadiusComesFromTheShippedData()
    {
        var data = GameData.Default;
        TestAssert.Near(1350.0, data.PerceptionBubbleDefault, 0, "GamePermanent.cfg [Vision] PerceptionBubbleRadius");
        foreach (var camp in NeutralDefinitions.Camps.Values)
        foreach (var name in camp.Roster.Distinct())
            TestAssert.Near(1350.0, data.Champion(name).PerceptionBubbleRadius, 0,
                $"{name} has no own PerceptionBubbleRadius: it inherits the default");
        TestAssert.Near(700.0, data.Champion("ShacoBox").PerceptionBubbleRadius, 0, "a character's [Data] value overrides it");
    }

    [Check("neutral.n2-perception-far-poke-retreats-at-once")]
    public static void FarPokeIsNotChased()
    {
        var (runtime, camp, hero) = StartMatchWithCamp(4);
        using (runtime)
        {
            var spot = FindSpot(runtime, camp, hero, 1450.0, 2400.0);
            Strike(runtime, camp, hero, spot, reveal: false);
            TestAssert.True(camp.All(unit => unit.NeutralState == "engage" && unit.AttackTarget == hero.NetId),
                "the hit wakes the camp on its attacker");
            runtime.Tick(NeutralDefinitions.RetreatCheckSeconds + 0.05);
            TestAssert.True(camp.All(unit => unit.NeutralState != "engage" && unit.AttackTarget is null),
                "beyond the perception bubble and with no attack reveal, the first decision gives the target up " +
                $"({States(camp)}, {Gap(hero.Position, camp[0].Position):F0} units)");
        }
    }

    [Check("neutral.n2-perception-keeps-a-target-in-range")]
    public static void TargetInsidePerceptionRangeIsKept()
    {
        var (runtime, camp, hero) = StartMatchWithCamp(4);
        using (runtime)
        {
            var spot = FindSpot(runtime, camp, hero, 750.0, 1300.0);
            Strike(runtime, camp, hero, spot, reveal: false);
            runtime.Tick(NeutralDefinitions.RetreatCheckSeconds + 0.05);
            TestAssert.True(camp.All(unit => unit.NeutralState == "engage" && unit.AttackTarget == hero.NetId),
                "inside the bubble and in line of sight the camp keeps its target");
        }
    }

    [Check("neutral.n2-perception-reveal-bubble-is-400-units")]
    public static void AttackRevealBubbleIsLimitedToItsRadius()
    {
        var (runtime, camp, hero) = StartMatchWithCamp(4);
        using (runtime)
        {
            var spot = FindSpot(runtime, camp, hero, 1450.0, 2400.0);
            Strike(runtime, camp, hero, spot, reveal: true);
            runtime.Tick(NeutralDefinitions.RetreatCheckSeconds + 0.05);
            TestAssert.True(camp.All(unit => unit.NeutralState == "engage" && unit.AttackTarget == hero.NetId),
                "the attack reveal shows a far attacker to the camp for its 4.5 s");

            // The bubble stays where the attack happened: a hero that runs 500 units from it is out of the bubble
            // and out of perception range, whatever time the timeout has left.
            var outward = Unit(spot - camp[0].Position);
            hero.Position = new Position2(spot.X + outward.X * 500.0, spot.Z + outward.Z * 500.0);
            TestAssert.True(hero.AttackRevealRemaining[(int)Team.Neutral] > 3.0, "the reveal timeout is still running");
            runtime.Tick(NeutralDefinitions.RetreatCheckSeconds);
            TestAssert.True(camp.All(unit => unit.NeutralState != "engage" && unit.AttackTarget is null),
                "the camp loses a hero that left the reveal bubble " +
                $"({States(camp)}, reveal {hero.AttackRevealRemaining[(int)Team.Neutral]:F2})");
        }
    }

    private static (MatchRuntime Runtime, Entity[] Camp, Entity Hero) StartMatchWithCamp(int campNumber)
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "the match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        runtime.Combat.RestoreClock(120.0);
        runtime.Tick(0.1);
        var camp = runtime.World.Entities.Values
            .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == campNumber && entity.Alive)
            .OrderBy(entity => entity.NetId).ToArray();
        TestAssert.Equal(NeutralDefinitions.Camps[campNumber].Roster.Count, camp.Length, "the camp is up");
        // A stationary camp keeps every distance and sight line fixed while the decisions are checked.
        foreach (var unit in camp) unit.Speed = 0.0;
        var hero = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Hero && entity.Team == Team.Order);
        hero.GodMode = true;
        return (runtime, camp, hero);
    }

    // An open spot in line of sight of every camp member, at a distance from every member inside [minimum, maximum].
    private static Position2 FindSpot(MatchRuntime runtime, Entity[] camp, Entity hero, double minimum, double maximum)
    {
        var grid = runtime.Navigation;
        var origin = camp[0].Position;
        for (var distance = minimum - 200.0; distance <= maximum + 200.0; distance += 50.0)
        for (var bearing = 0; bearing < 72; bearing++)
        {
            var point = origin + new Position2(Math.Cos(bearing * Math.PI / 36.0) * distance,
                Math.Sin(bearing * Math.PI / 36.0) * distance);
            if (grid.IsMovementWalkable(point) && grid.PointClearance(point) >= 60.0 &&
                camp.All(unit => Gap(unit.Position, point) >= minimum && Gap(unit.Position, point) <= maximum &&
                    grid.HasUnitVision(unit.Position, unit.SelectionRadius, point, hero.SelectionRadius)))
                return point;
        }
        throw new InvalidOperationException($"no open spot {minimum}-{maximum} units from camp {camp[0].CampId}");
    }

    private static void Strike(MatchRuntime runtime, Entity[] camp, Entity hero, Position2 from, bool reveal)
    {
        hero.Position = from;
        hero.Waypoints.Clear();
        hero.Target = null;
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, camp[0].NetId, 40.0, DamageType.Magic,
            DamageSourceKind.Spell), "the strike lands");
        if (reveal) runtime.Combat.RevealAttack(hero, camp[0]);
    }

    private static double Gap(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));

    private static string States(IEnumerable<Entity> units) => string.Join("; ", units.Select(unit =>
        $"{unit.Name}:{unit.NeutralState}/{(unit.AttackTarget is uint id ? id.ToString("X") : "-")}"));

    private static Position2 Unit(Position2 vector)
    {
        var length = Math.Sqrt(vector.X * vector.X + vector.Z * vector.Z);
        return new Position2(vector.X / length, vector.Z / length);
    }
}
