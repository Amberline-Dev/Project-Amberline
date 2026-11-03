using Amberline.Core;
using Amberline.Data;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-approach-interrupts-attack")]
    public static void WalkingToCastCancelsTheOldBasicAttackOrder()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var allied in new[] { false, true })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match(allied ? "Janna" : "Annie", team, ally: allied, extraEnemy: !allied);
            var hero = match.Hero(0); var attacked = match.Hero(1); var castTarget = match.Hero(2);
            attacked.Position = hero.Position + new Position2(100, 0);
            castTarget.Position = hero.Position + new Position2(900, 900);
            match.Runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                match.Runtime.World.Allocate(UnitType.Missile), team, castTarget.Position, 600, 10));
            match.Runtime.Delivery.Flush();
            hero.AttackTarget = attacked.NetId;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, attacked));
            if (launched) match.Runtime.Combat.CombatTick(hero.AttackWindup + .01);
            match.Cast(0, allied ? 2 : 0, castTarget);
            TestAssert.True(hero.PendingCast is not null && hero.Waypoints.Count > 0);
            TestAssert.True(hero.AttackTarget is null, "walking to a spell target replaces the previous attack order");
            TestAssert.Equal(launched ? 1 : 0,
                match.Runtime.Combat.PendingHits.Count(hit => hit.SourceNetId == hero.NetId),
                "cast approach cancels windup but keeps a launched basic attack");
            var health = attacked.Health;
            match.Runtime.Combat.CombatTick(1);
            TestAssert.Equal(launched, attacked.Health < health);
        }
    }

    [Check("spells-live-approach-fog-cancellation")]
    public static void ApproachingUnitSpellCannotFollowOrCastIntoFog()
    {
        var grid = NavigationGrid.LoadDefault();
        var brush = Enumerable.Range(0, grid.BrushLabels.Count)
            .Where(index => grid.BrushLabels[index] != 0)
            .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX)))
            .First(point => point.X is > 5000 and < 9000 && point.Z is > 5000 and < 9000 &&
                grid.IsMovementWalkable(point) &&
                grid.IsMovementWalkable(point + new Position2(-250, 0)) &&
                grid.BrushIdAt(point + new Position2(-250, 0)) is null);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (champion, slot) in new[]
        {
            ("Annie", 0), ("Shaco", 2), ("Yorick", 2),
            ("Janna", 1), ("LeeSin", 3), ("Vayne", 2),
        })
        foreach (var entersRange in new[] { false, true })
        {
            using var match = new Match(champion, team);
            var hero = match.Hero(0); var target = match.Hero(1);
            hero.Position = brush + new Position2(-900, -900);
            target.Position = brush;
            match.Runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                match.Runtime.World.Allocate(UnitType.Missile), team, brush, 600, 10));
            match.Runtime.Delivery.Flush();
            match.Cast(0, slot, target);
            TestAssert.True(hero.PendingCast is not null, $"{champion}: start a visible out-of-range approach");
            // Movement projects an occupied unit-centre approach onto the
            // legal body shell. Capture the endpoint actually sent to the
            // client; PendingCast.LastMovePoint remains the target's last
            // observed position for follow-up visibility decisions.
            var previousGoal = hero.Waypoints.Count > 0 ? hero.Waypoints[^1] : (Position2?)null;
            var mana = hero.Mana;
            if (entersRange) hero.Position = brush + new Position2(-250, 0);
            else target.Position = brush + new Position2(0, 1500);
            match.Runtime.World.TimedVisionBubbles.Clear();
            match.Runtime.Delivery.Flush(); match.Clear();
            TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(target.NetId),
                $"{champion}: test target must be hidden without stealth or untargetability");
            match.Runtime.Spells.Tick(.01);
            match.Runtime.Delivery.Flush();
            TestAssert.True(hero.PendingCast is null, $"{champion}: losing sight cancels the queued unit spell");
            TestAssert.Equal(mana, hero.Mana, "canceled approach spends no mana");
            TestAssert.Equal(0.0, hero.SpellCooldowns[slot], "canceled approach starts no cooldown");
            TestAssert.Equal(0, hero.MatchStatistics.SpellCasts[slot], "canceled approach does not count a cast");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                "a hidden target cannot receive a new cast even after entering cast range");
            if (!entersRange && hero.Waypoints.Count > 0)
            {
                TestAssert.True(previousGoal is Position2,
                    "a visible approach must retain its committed route endpoint");
                TestAssert.Near(previousGoal!.Value.X, hero.Waypoints[^1].X, 3);
                TestAssert.Near(previousGoal.Value.Z, hero.Waypoints[^1].Z, 3,
                    "the remaining walk uses the old visible position, never the hidden target");
            }
        }
    }

    [Check("spells-live-approach-moving-target")]
    public static void ApproachMovingSpellTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (champion, slot, allied) in new[]
        {
            ("Annie", 0, false), ("Shaco", 2, false),
            ("Yorick", 2, false), ("Yorick", 3, true),
            ("Janna", 1, false), ("Janna", 2, true),
            ("LeeSin", 1, true), ("LeeSin", 3, false),
            ("Orianna", 2, true), ("Nidalee", 2, true), ("Vayne", 2, false),
        })
        {
            using var match = new Match(champion, team, ally: allied);
            var hero = match.Hero(0);
            var target = match.Hero(allied ? 2 : 1);
            hero.Position = new Position2(6100, 6100);
            target.Position = new Position2(7000, 7000);
            match.Runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                match.Runtime.World.Allocate(UnitType.Missile), team, target.Position, 600, 10));
            match.Runtime.Delivery.Flush(); match.Clear();
            TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(target.NetId),
                "approach target must be visible to the requesting seat");
            var validation = match.Runtime.Spells.CanCast(hero, slot, target.NetId,
                new Position3(target.Position.X, 0, target.Position.Z), 0);
            TestAssert.Equal(SpellCastFailure.Range, validation.Failure,
                $"{champion}/{slot}: only range should block the cast: {validation}");
            match.Cast(0, slot, target);
            TestAssert.True(hero.PendingCast is not null, $"{champion}/{slot}: approach a distant target");

            target.Position += new Position2(200, 0);
            match.Runtime.Spells.Tick(.01);
            TestAssert.Equal(target.Position, hero.PendingCast!.LastMovePoint!.Value,
                $"{champion}/{slot}: follow the unit instead of the original click position");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                "approaching alone must not acknowledge a cast or charge its cost");

            hero.Position = target.Position + new Position2(-250, 0);
            match.Runtime.Spells.Tick(.01);
            match.Runtime.Delivery.Flush();
            TestAssert.True(hero.PendingCast is null, $"{champion}/{slot}: execute when in range");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                $"{champion}/{slot}: approaching completes through the real cast dispatcher");
        }
    }

    [Check("spells-live-approach-unit-in-wave")]
    public static void UnitSpellApproachStopsInsideCastRangeOutsideWave()
    {
        // Playtest B2 AE4: Annie Q on a minion just beyond 625 inside its
        // wave sent a one-point wait and never cast. The approach must stop
        // at the firing point, not route to the ringed unit centre.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
            match.Hero(1).Position = new Position2(10000, 10000);
            hero.Position = new Position2(7000, 7000);
            var range = GameData.Default.Spell("Disintegrate").CastRange;
            Entity Minion(Position2 position)
            {
                var minion = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, enemyTeam, position)
                {
                    Name = "Blue_Minion_Basic", Alive = true, Created = true, Visible = true,
                    Speed = 325.0, Radius = 35.744, Health = 1000.0, MaxHealth = 1000.0,
                };
                runtime.World.Register(minion);
                return minion;
            }
            var target = Minion(hero.Position + new Position2(range + 20.0, 0.0));
            // Twelve planted wave bodies sit on the hero's body shell around
            // the target, so every route to (or projection of) its centre is
            // blocked, while the firing point 622 units out stays open.
            var shell = runtime.Movement.BodyClearance(hero, target) + 4.0;
            for (var index = 0; index < 12; index++)
            {
                var angle = index * Math.PI / 6.0;
                Minion(target.Position + new Position2(Math.Cos(angle) * shell, Math.Sin(angle) * shell));
            }
            runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                runtime.World.Allocate(UnitType.Missile), team, target.Position, 600, 10));
            runtime.Delivery.Flush(); match.Clear();
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(target.NetId),
                "the wave target must be visible to Annie");

            match.Cast(0, 0, target);
            TestAssert.True(hero.PendingCast is not null, $"{team}: Q beyond cast range queues an approach");
            TestAssert.True(hero.Waypoints.Count > 1,
                $"{team}: the approach is a route, not a one-point crowd wait ({hero.Waypoints.Count} points)");
            TestAssert.True(Distance(hero.Waypoints[^1], target.Position) <= range,
                $"{team}: the route ends inside Disintegrate's centre-to-centre range " +
                $"({Distance(hero.Waypoints[^1], target.Position):0.0})");
            TestAssert.Equal(target.Position, hero.PendingCast!.LastMovePoint!.Value,
                "the queued cast still follows the unit position");
            var cast = false;
            for (var step = 0; step < 15 && !cast; step++)
            {
                runtime.Tick(1.0 / 30.0);
                cast = match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD);
            }
            TestAssert.True(cast, $"{team}: Q casts within half a second of the request");
            TestAssert.True(hero.PendingCast is null, $"{team}: the queued Q executed");
        }
    }

    [Check("spells-live-approach-ground-target")]
    public static void ApproachGroundSpellTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            // Janna's Howling Gale is a TTYPE_Direction skillshot from her script: it has no cast point to reach (its CastRange is how far the
            // wave flies), so a far aim is no approach. The ground approach is checked with a ground-targeted spell that has one, Annie's R.
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var range = GameData.Default.Spell("InfernalGuardian").CastRange;
            var aim = hero.Position + new Position2(range + 100, 0);
            // A ground-target request has an aim position and no unit ID.
            var point = new Entity(0, EntityKind.Monster, UnitType.Monster, Team.Neutral, aim);
            match.Cast(0, 3, point);
            TestAssert.Equal(aim, hero.PendingCast!.LastMovePoint!.Value,
                "Infernal Guardian approaches its ground aim without requiring Annie Q's enemy target");
            hero.Position += new Position2(200, 0);
            match.Runtime.Spells.Tick(.01);
            match.Runtime.Delivery.Flush();
            TestAssert.True(hero.PendingCast is null, "ground cast executes once its aim is in range");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                "ground approach delivers a cast answer");
        }
    }
}
