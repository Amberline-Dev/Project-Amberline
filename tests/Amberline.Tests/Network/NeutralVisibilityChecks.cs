using System.Text.Json;
using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

public static class NeutralVisibilityChecks
{
    [Check("neutral.ai-brush-target-loss")]
    public static void CampMembersDropTargetAfterBrushEntry()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        using var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "neutral visibility match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;

        runtime.Combat.RestoreClock(120.0);
        runtime.Tick(0.1);
        var camp = runtime.World.Entities.Values
            .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == 4 && entity.Alive)
            .ToArray();
        TestAssert.Equal(NeutralDefinitions.Camps[4].Roster.Count, camp.Length,
            "red lizard camp has every living member");

        var hero = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Hero && entity.Team == Team.Order);
        hero.GodMode = true;
        var (outside, brush) = FindBrushTarget(runtime.Navigation, camp, hero);

        // A neutral and target occupying the same brush retain ordinary sight.
        // This positive control keeps the negative case below about brush
        // blocking rather than treating every brush target as invisible.
        foreach (var member in camp)
        {
            member.Position = brush;
            member.Waypoints.Clear();
            member.Target = null;
            member.Speed = 0.0;
            member.NeutralState = "engage";
            member.AttackTarget = hero.NetId;
        }
        hero.Position = brush;
        runtime.Tick(NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.True(camp.All(member => member.NeutralState == "engage" &&
            member.AttackTarget == hero.NetId),
            "same-brush target remains visible to every camp member");
        // Model members that have chased to the brush edge, within their
        // original home leash, rather than requiring sight through the camp
        // wall from every original spawn position.
        foreach (var member in camp) member.Position = outside;

        hero.Position = outside;
        foreach (var member in camp)
        {
            member.Waypoints.Clear();
            member.Target = null;
            member.Speed = 0.0;
            member.NeutralState = "engage";
            member.AttackTarget = hero.NetId;
        }

        runtime.Tick(NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.True(camp.All(member => member.NeutralState == "engage" &&
            member.AttackTarget == hero.NetId),
            "visible target remains established for every camp member");

        // Queue the real basic attack that seeds the shared neutral reveal
        // slot; neutral retention may use it while the attacker is in brush.
        hero.AttackRange = 1000.0;
        hero.AttackPeriod = 1.0;
        hero.AttackCooldown = 0.0;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, camp[0]),
            "basic attack seeds neutral reveal");
        // Batch 49 (1ccf9f7): the attack reveals its caster at the cast frame (the end of the windup, SpellHelper::
        // RevealSpellCaster), not when the swing is queued; the sweep tick below carries the swing past its windup.
        TestAssert.True(hero.AttackWindup > 0.0 && hero.AttackWindup < NeutralDefinitions.RetreatCheckSeconds,
            "the swing's cast frame falls inside the next sweep tick");
        TestAssert.Equal(0.0, hero.AttackRevealRemaining[(int)Team.Neutral],
            "no neutral reveal while the swing winds up");
        hero.Position = brush;
        runtime.Tick(NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.True(hero.AttackRevealRemaining[(int)Team.Neutral] > 4.0,
            "basic attack uses the neutral reveal timeout");
        TestAssert.True(camp.All(member => member.NeutralState == "engage" &&
            member.AttackTarget == hero.NetId),
            "active basic-attack reveal retains a brush target");

        // Once the shipped reveal timeout expires, direct neutral sight is
        // required and the hidden target is released on the next check.
        runtime.Tick(3.9);
        TestAssert.True(hero.AttackRevealRemaining[(int)Team.Neutral] > 0.0 &&
            camp.All(member => member.NeutralState == "engage" &&
                member.AttackTarget == hero.NetId),
            "the brush target remains available just before reveal expiry");
        runtime.Tick(0.6);
        TestAssert.Near(0.0, hero.AttackRevealRemaining[(int)Team.Neutral], 0.0);
        TestAssert.True(camp.All(member => member.NeutralState != "engage" &&
            member.AttackTarget is null),
            "every camp member drops a brush target after reveal expiry");
    }

    private static (Position2 Outside, Position2 Brush) FindBrushTarget(
        NavigationGrid grid, IReadOnlyList<Entity> camp, Entity hero)
    {
        // Playtest #10 (25 Sept 2026): brush membership follows each unit's
        // SelectionRadius, so both points must hold for every unit placed there.
        var radii = camp.Select(member => member.SelectionRadius)
            .Append(hero.SelectionRadius).Append(-1.0).ToArray();
        var home = camp[0].Position;
        var brushes = Enumerable.Range(0, grid.BrushLabels.Count)
            .Where(index => grid.BrushLabels[index] != 0)
            .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX,
                index / grid.CountX)))
            .Where(point => grid.IsMovementWalkable(point) &&
                DistanceSquared(point, home) <= 2500.0 * 2500.0)
            .OrderBy(point => DistanceSquared(point, home));
        var offsets = new[]
        {
            new Position2(-grid.CellSize, 0.0), new Position2(grid.CellSize, 0.0),
            new Position2(0.0, -grid.CellSize), new Position2(0.0, grid.CellSize),
        };

        foreach (var brush in brushes)
        {
            var brushId = grid.BrushIdAt(brush);
            if (brushId is null || radii.Any(radius => grid.BrushIdAt(brush, radius) != brushId)) continue;
            foreach (var offset in offsets)
            {
                var outside = brush + offset;
                if (!grid.IsMovementWalkable(outside) ||
                    radii.Any(radius => grid.BrushIdAt(outside, radius) is not null))
                    continue;
                if (!camp.All(member => DistanceHomeSquared(member, outside) <=
                        NeutralDefinitions.LeashRadius * NeutralDefinitions.LeashRadius)) continue;
                if (!camp.All(member => DistanceHomeSquared(member, brush) <=
                        NeutralDefinitions.LeashRadius * NeutralDefinitions.LeashRadius)) continue;
                // Adjacent cardinal cells have no intermediate terrain.
                // HasVisionLos already includes brush occlusion, so it must
                // be false here even though both cells are terrain-clear.
                if (!grid.IsVisionClear(grid.WorldToCell(outside)) ||
                    !grid.IsVisionClear(grid.WorldToCell(brush)) ||
                    !grid.HasMovementLineOfSight(outside, brush) ||
                    grid.HasVisionLos(outside, brush) ||
                    grid.BrushAllowsVision(outside, brush)) continue;
                return (outside, brush);
            }
        }

        throw new InvalidOperationException("red lizard camp has no usable brush target");
    }

    private static double DistanceHomeSquared(Entity member, Position2 point)
    {
        var home = member.NeutralHome is Position3 value
            ? new Position2(value.X, value.Z)
            : member.Position;
        return DistanceSquared(home, point);
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }
}


public static partial class NeutralAiChecks
{

}
