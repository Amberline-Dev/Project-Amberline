using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-shaco-fear-walk-follows-speed-transitions")]
    public static void BoxFearWalkUsesCurrentHeroAndNpcSpeed()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var npc in new[] { false, true })
        {
            using var match = new Match("Shaco", team,
                random: new FixedControlRandom(.75));
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var enemy = match.Hero(1);
            shaco.SpellRanks[1] = 5;
            shaco.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(10000, 10000);
            Entity actor;
            if (npc)
            {
                actor = runtime.World.Register(new Entity(
                    runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
                    UnitType.Monster, Team.Neutral, shaco.Position + new Position2(200, 0))
                {
                    Name = "FearSpeedProbe", Alive = true, Created = true, Visible = true,
                    Health = 5000, MaxHealth = 5000, Speed = 350, Radius = 35,
                });
            }
            else
            {
                actor = enemy;
                actor.Position = shaco.Position + new Position2(200, 0);
            }

            match.CastGround(0, shaco.Position, slot: 1);
            for (var frame = 0; frame < 120 && actor.ForcedMove?.Effect != "Fear"; frame++)
                runtime.Tick(1.0 / 30.0);
            var move = actor.ForcedMove ??
                throw new InvalidOperationException($"{team}/{npc}: real box did not begin Fear");
            TestAssert.True(move.Effect == "Fear" && !move.Completed &&
                            FearStepDistance(actor.Position, move.Destination) > 80,
                $"{team}/{npc}: live Fear has room for two movement frames");
            var fearSpeed = actor.Speed;
            TestAssert.Near(fearSpeed, move.Speed, 1e-6,
                $"{team}/{npc}: first walking leg starts at current feared speed");

            var slow = runtime.SpellBuffs.Add(actor, "FearSpeedExternalSlow",
                BuffType.Slow, .2, shaco.NetId,
                new BuffPayload { MoveSpeedMult = -.25 });
            TestAssert.True(slow is not null && actor.Speed < fearSpeed &&
                            ReferenceEquals(move, actor.ForcedMove),
                $"{team}/{npc}: a separate speed modifier keeps the same Fear route");
            var step = npc ? .1 : .05;
            // A forced move consumes only the part of a movement frame that follows its start (AdvanceForcedMove), and the scripted
            // box starts the fear mid-frame of the NPC cohort, so the first frame after the start is partial (1/30 s of 0.1 s).
            // The slowed speed is measured on the next frame, a whole one.
            runtime.Movement.Tick(step);
            var slowedSpeed = actor.Speed;
            var beforeSlowStep = actor.Position;
            runtime.Movement.Tick(step);
            TestAssert.Near(slowedSpeed * step, FearStepDistance(beforeSlowStep, actor.Position), .01,
                $"{team}/{npc}: the in-flight walking leg uses the newly slowed speed");
            TestAssert.Near(slowedSpeed, move.Speed, 1e-6,
                $"{team}/{npc}: attached Fear state follows the same speed");

            runtime.Buffs.TickBuffs(actor, .21);
            TestAssert.False(actor.Buffs.Contains(slow!),
                $"{team}/{npc}: the external slow expires before Fear");
            TestAssert.True(actor.Buffs.Any(buff => buff.Name == "Fear") &&
                            ReferenceEquals(move, actor.ForcedMove) && actor.Speed > slowedSpeed,
                $"{team}/{npc}: Fear still owns the walk after speed restoration");
            var restoredSpeed = actor.Speed;
            var beforeRestoredStep = actor.Position;
            runtime.Movement.Tick(step);
            TestAssert.Near(restoredSpeed * step,
                FearStepDistance(beforeRestoredStep, actor.Position), .01,
                $"{team}/{npc}: the same leg resumes at restored current speed");
            TestAssert.Near(restoredSpeed, move.Speed, 1e-6,
                $"{team}/{npc}: no captured slow speed remains in the move");
        }
    }

    private static double FearStepDistance(Position2 first, Position2 second) =>
        Math.Sqrt((second.X - first.X) * (second.X - first.X) +
                  (second.Z - first.Z) * (second.Z - first.Z));
}
