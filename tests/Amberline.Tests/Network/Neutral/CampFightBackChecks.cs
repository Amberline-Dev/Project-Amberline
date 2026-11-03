using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 219 (coordinator item 4): every camp member, hit by a hero, fights back within 2 seconds. A member fights back when its
// AI leaves idle for engage within two game seconds of the hit. Each member runs in a fresh production match, so one member's
// call for help cannot wake its camp-mates. The hero stands 300 units from the member on the first walkable spot, in eight
// directions, that the member can see; a member that can see none of those spots fails the check. The members are the script's
// own camp table (32 members on Map1). The Dragon (camp 6) and the Baron (camp 12) spawn after 50 and 800 seconds.
// Batch 230 (C2): each fresh match starts its clock just before the Baron's spawn and ticks only until its own member has
// spawned, not a fixed 120 or 900 seconds; lane waves are off. The match, the hit and the two-second window are unchanged.
public static class CampFightBackChecks
{
    [Check("neutral.every-member-fights-back-within-2s")]
    public static void EveryCampMemberFightsBackWithinTwoSeconds()
    {
        var table = Start(0.0).Waves!.LevelScript.NeutralCamps;
        var members = table.SelectMany(camp => camp.Groups[0].Select((name, index) => (Camp: camp.Number, Member: index + 1)))
            .ToArray();
        TestAssert.Equal(32, members.Length, "Map1 camp members in the script's table");
        var failures = new List<string>();
        foreach (var (camp, member) in members)
        {
            var runtime = Start(899.9);
            Entity? monster = null;
            for (var tick = 0; tick < 9000 && monster is null; tick++)
            {
                runtime.Tick(0.1);
                monster = runtime.World.Entities.Values.SingleOrDefault(entity =>
                    entity.Kind == EntityKind.Neutral && entity.CampId == camp && entity.NeutralMember == member);
            }
            if (monster is null)
            {
                failures.Add($"camp {camp} member {member}: not spawned");
                continue;
            }
            var reason = FightBack(runtime, monster);
            if (reason is not null) failures.Add($"camp {camp} member {member} {monster.Name}: {reason}");
        }
        TestAssert.True(failures.Count == 0,
            $"{failures.Count} member(s) do not fight back within 2 s: " + string.Join("; ", failures));
    }

    // Returns null when the member engages within two seconds of a hero's hit, otherwise why it does not.
    private static string? FightBack(MatchRuntime runtime, Entity monster)
    {
        var grid = runtime.Navigation;
        var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
        var at = monster.Position;
        Position2? spot = null;
        foreach (var (dx, dz) in new[] { (300.0, 0.0), (-300.0, 0.0), (0.0, 300.0), (0.0, -300.0),
                     (212.0, 212.0), (-212.0, 212.0), (212.0, -212.0), (-212.0, -212.0) })
        {
            var candidate = new Position2(at.X + dx, at.Z + dz);
            if (!grid.IsMovementWalkable(candidate) ||
                !grid.HasUnitVision(at, monster.SelectionRadius, candidate, hero.SelectionRadius)) continue;
            spot = candidate;
            break;
        }
        if (spot is null) return "no walkable spot 300 units away that the member can see";
        hero.Position = spot.Value;
        runtime.Delivery.Flush();
        if (!runtime.Combat.ApplyHit(hero.NetId, monster.NetId, 60.0, DamageType.True, DamageSourceKind.Spell))
            return "the hit was refused";
        for (var step = 1; step <= 20; step++)
        {
            runtime.Tick(0.1);
            if (monster.NeutralState == "engage") return null;
        }
        return $"still {monster.NeutralState} after 2 s (health {monster.Health:F0}/{monster.MaxHealth:F0})";
    }

    private static MatchRuntime Start(double seconds)
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
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
        TestAssert.True(session.Start(), "production neutral match starts");
        // Batch 230 (C2): the lane waves are not part of this check. The member and its camp-mates are the only bodies it reads,
        // and with the lane minions off each fresh match is cheap enough to tick to its member's spawn.
        runtime.Waves!.AutomaticSpawningEnabled = false;
        // The clock starts just before the Baron's spawn (100 s initial spawn plus its 800 s delay). Nothing in the match happens
        // before a camp's spawn, so the production scheduler spawns every due camp on the first tick and the match is not ticked
        // through the empty first 899 seconds. The spawn times stay the scheduler's own.
        runtime.World.Clock.SetVirtual(seconds);
        runtime.Tick(0.0);
        return runtime;
    }
}
