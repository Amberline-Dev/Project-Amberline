using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-shaco-fear-yields-to-later-movement-lock")]
    public static void BoxFearYieldsToRootStunAndSuppression()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var control in new[]
                 {
                     (Name: "FearOverlapSnare", Type: BuffType.Snare),
                     (Name: "FearOverlapStun", Type: BuffType.Stun),
                     (Name: "FearOverlapSuppression", Type: BuffType.Suppression),
                 })
        {
            using var match = new Match("Shaco", team, random: new FixedControlRandom(.75));
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var victim = match.Hero(1);
            shaco.SpellRanks[1] = 5;
            victim.Position = shaco.Position + new Position2(200, 0);
            match.CastGround(0, shaco.Position, 1);
            // The scripted box adds the Fear buff in its hook (BBApplyFear); the shared fear walk (ShacoBoxService.TickFears) starts the wander
            // on the next simulation step, so wait for the walking segment, not only the buff.
            for (var tick = 0; tick < 120 && victim.ForcedMove?.Effect != "Fear"; tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Fear") &&
                            victim.ForcedMove is { Effect: "Fear", Completed: false },
                $"{team}/{control.Name}: real box fear starts its wandering segment");
            var walking = victim.ForcedMove;
            match.Clear();
            TestAssert.True(runtime.SpellBuffs.Add(victim, "FearOverlapUnrelatedBuff",
                    BuffType.CombatEnhancer, 1.0, victim.NetId) is not null);
            runtime.Delivery.Flush();
            TestAssert.True(ReferenceEquals(walking, victim.ForcedMove),
                $"{team}/{control.Name}: an unrelated buff keeps Fear's live walking owner");
            TestAssert.False(match.Captures.Any(capture => capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == 0xC1 && packet.EntityReferences.Any(reference =>
                        reference.NetId == victim.NetId))),
                $"{team}/{control.Name}: an unrelated buff does not stop Fear's live route");
            var stop = victim.Position;
            TestAssert.True(runtime.SpellBuffs.Add(victim, control.Name, control.Type, .3,
                    shaco.NetId) is not null,
                $"{team}/{control.Name}: later movement control is admitted");
            TestAssert.True(victim.ForcedMove is null &&
                            victim.Buffs.Any(buff => buff.Name == "Fear"),
                $"{team}/{control.Name}: only fear walking is cancelled, not the timed fear buff");
            runtime.Tick(.1);
            TestAssert.Near(stop.X, victim.Position.X, 1e-6,
                $"{team}/{control.Name}: no X drift through the new lock");
            TestAssert.Near(stop.Z, victim.Position.Z, 1e-6,
                $"{team}/{control.Name}: no Z drift through the new lock");

            for (var tick = 0; tick < 35 && victim.ForcedMove?.Effect != "Fear"; tick++)
                runtime.Tick(.03);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Fear") &&
                            victim.ForcedMove is { Effect: "Fear" },
                $"{team}/{control.Name}: Fear resumes only if time remains after the lock");

            // A displacement remains authoritative through the same root or
            // stun. This synthetic move tests shared movement only; it has no
            // spell owner to retire it, unlike real Monsoon in the next check.
            var pushOrigin = victim.Position;
            TestAssert.True(runtime.SpellBuffs.Add(victim, control.Name, control.Type, .3,
                    shaco.NetId) is not null,
                $"{team}/{control.Name}: second lock is admitted over resumed Fear");
            TestAssert.True(victim.ForcedMove is null,
                $"{team}/{control.Name}: second lock retires only the resumed Fear walk");
            var push = new ForcedMoveState(pushOrigin + new Position2(100, 0), 1000, .2,
                shaco.NetId, "JannaReapTheWhirlwind");
            TestAssert.True(runtime.Movement.StartForcedMove(victim.NetId, push, cancelOrder: true));
            runtime.Tick(.05);
            TestAssert.True(victim.Position.X > pushOrigin.X + 1 &&
                            ReferenceEquals(push, victim.ForcedMove),
                $"{team}/{control.Name}: genuine displacement progresses under the lock");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { EntityKind.Minion, EntityKind.Monster, EntityKind.YorickGhoul })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var type = kind == EntityKind.Monster ? UnitType.Monster : UnitType.Minion;
            var actor = runtime.World.Register(new Entity(runtime.World.Allocate(type),
                kind, type, kind == EntityKind.Monster ? Team.Neutral : match.Hero(1).Team,
                shaco.Position + new Position2(300, 0))
            {
                Name = $"FearOverlap{kind}", Health = 5000, MaxHealth = 5000,
                Speed = 350, Radius = 35, Created = true, Visible = true,
                MinionType = kind == EntityKind.Minion ? MinionType.Melee : null,
                OwnerNetId = kind == EntityKind.YorickGhoul ? match.Hero(1).NetId : 0,
                ExpiresAt = 20,
            });
            TestAssert.True(runtime.SpellBuffs.Add(actor, "Fear", BuffType.Fear, 1.5,
                    shaco.NetId, new BuffPayload { ActionDisabled = true,
                        FearOrigin = actor.Position }) is not null);
            TestAssert.True(runtime.Movement.StartForcedMove(actor.NetId,
                new ForcedMoveState(actor.Position + new Position2(200, 0), 350, 1,
                    shaco.NetId, "Fear"), cancelOrder: true));
            var stop = actor.Position;
            TestAssert.True(runtime.SpellBuffs.Add(actor, "FearOverlapStun", BuffType.Stun,
                    .3, shaco.NetId) is not null);
            TestAssert.True(actor.ForcedMove is null,
                $"{team}/{kind}: nonhero fear walking retires on stun");
            runtime.Tick(.05);
            TestAssert.Near(stop.X, actor.Position.X, 1e-6,
                $"{team}/{kind}: no nonhero Fear X drift during stun");
            TestAssert.Near(stop.Z, actor.Position.Z, 1e-6,
                $"{team}/{kind}: no nonhero Fear Z drift during stun");
            for (var tick = 0; tick < 35 && actor.ForcedMove?.Effect != "Fear"; tick++)
                runtime.Tick(.03);
            TestAssert.True(actor.Buffs.Any(buff => buff.Name == "Fear") &&
                            actor.ForcedMove is { Effect: "Fear" },
                $"{team}/{kind}: nonhero Fear resumes after the later control ends");
        }
    }

    [Check("pets-live-shaco-fear-preserves-janna-knockback-and-resumes")]
    public static void BoxFearDoesNotReplaceARealKnockback()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team, ally: true,
                random: new FixedControlRandom(.75), allyChampion: "Janna");
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var victim = match.Hero(1);
            var janna = match.Hero(2);
            shaco.SpellRanks[1] = 5;
            victim.Position = shaco.Position + new Position2(200, 0);
            janna.Position = shaco.Position + new Position2(50, 0);
            match.CastGround(0, shaco.Position, 1);
            for (var tick = 0; tick < 30 && !runtime.World.Entities.Values.Any(unit =>
                     unit.Kind == EntityKind.ShacoBox); tick++) runtime.Tick(.02);
            var box = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoBox);
            var born = ShacoScriptSupport.Born(runtime, box);
            // Monsoon pushes when its 0.25 s windup ends: press it a windup earlier than the push is wanted (the box is awake at 2 s).
            for (var tick = 0; tick < 50 && runtime.Combat.CurrentTime - born < 1.70 - 1e-8; tick++)
                runtime.Tick(.05);
            TestAssert.False(ShacoScriptSupport.IsAwake(box),
                $"{team}: box has not awakened before Monsoon");
            match.Cast(2, 3, janna);
            var knockback = AwaitForcedMove(match, victim, "MoveAway");
            TestAssert.True(knockback is { Effect: "MoveAway", Completed: false },
                $"{team}: real Janna R owns the victim's displacement");
            match.Clear();
            runtime.Tick(.06);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Fear") &&
                            ReferenceEquals(knockback, victim.ForcedMove) &&
                            knockback?.Completed == false,
                $"{team}: real box fear cannot replace an active Monsoon knockback");
            TestAssert.False(match.Captures.Any(capture => capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == 0xC1 && packet.EntityReferences.Any(reference =>
                        reference.NetId == victim.NetId))),
                $"{team}: Fear does not send a stationary waypoint over Monsoon's dash");
            for (var tick = 0; tick < 35 && victim.ForcedMove?.Effect != "Fear"; tick++)
                runtime.Tick(.03);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Fear") &&
                            victim.ForcedMove is { Effect: "Fear" },
                $"{team}: remaining fear may resume after the knockback completes");

            // Also cover the opposite ordering: an existing Fear segment is
            // displaced by Monsoon just before Fear's next real timer pulse.
            using var follow = new Match("Shaco", team, ally: true,
                random: new FixedControlRandom(.75), allyChampion: "Janna");
            var secondRuntime = follow.Runtime;
            var secondShaco = follow.Hero(0);
            var secondVictim = follow.Hero(1);
            var secondJanna = follow.Hero(2);
            secondShaco.SpellRanks[1] = 5;
            secondVictim.Position = secondShaco.Position + new Position2(200, 0);
            secondJanna.Position = secondShaco.Position + new Position2(50, 0);
            follow.CastGround(0, secondShaco.Position, 1);
            // The Fear buff comes from the box's hook and the wander starts on the next step: wait for the walking segment.
            for (var tick = 0; tick < 120 && secondVictim.ForcedMove?.Effect != "Fear"; tick++)
                secondRuntime.Tick(1.0 / 30.0);
            var fear = secondVictim.Buffs.Single(buff => buff.Name == "Fear");
            TestAssert.True(secondVictim.ForcedMove is { Effect: "Fear" },
                $"{team}: box established walking before the second Monsoon");
            var untilPulse = fear.Payload.NextWander - secondRuntime.Combat.CurrentTime;
            TestAssert.True(untilPulse > .05,
                $"{team}: next Fear pulse has a real later deadline");
            // Press Monsoon a windup before the Fear pulse is due, when the pulse is that far away.
            if (untilPulse > .30) secondRuntime.Tick(untilPulse - .30);
            follow.Cast(2, 3, secondJanna);
            var laterKnockback = AwaitForcedMove(follow, secondVictim, "MoveAway") ??
                throw new InvalidOperationException($"{team}: Monsoon did not replace the old Fear segment");
            TestAssert.True(laterKnockback is { Effect: "MoveAway", Completed: false },
                $"{team}: Monsoon replaces the old Fear segment");
            for (var tick = 0; tick < 12 && !laterKnockback.Completed; tick++)
            {
                secondRuntime.Tick(.01);
                TestAssert.True(ReferenceEquals(laterKnockback, secondVictim.ForcedMove) ||
                                laterKnockback.Completed,
                    $"{team}: due Fear pulse cannot steal Monsoon's still-active move");
            }

            // The same AddBuff stop path also runs for NPC Stun. A real
            // Monsoon displacement remains the route owner through that lock.
            using var npc = new Match("Janna", team);
            var npcRuntime = npc.Runtime;
            var npcJanna = npc.Hero(0);
            var otherTeam = team == Team.Order ? Team.Chaos : Team.Order;
            var minion = npcRuntime.Waves!.SpawnFixture(otherTeam, MinionType.Melee, 0,
                "C", 0, 0);
            minion.Position = npcJanna.Position + new Position2(200, 200);
            minion.Waypoints.Clear();
            npcRuntime.Tick(.01);
            minion.Position = npcJanna.Position + new Position2(200, 200);
            npcRuntime.Delivery.Flush();
            TestAssert.True(npcRuntime.Navigation.IsWalkable(minion.Position) &&
                            npcRuntime.Effects.IsReferenceReady(minion),
                $"{team}: the real enemy minion is in a visible, walkable Monsoon lane");
            npc.Cast(0, 3, npcJanna);
            var npcPush = AwaitForcedMove(npc, minion, "MoveAway");
            TestAssert.True(npcPush is { Effect: "MoveAway", Completed: false },
                $"{team}: Monsoon owns the minion's forced displacement");
            npc.Clear();
            TestAssert.True(npcRuntime.SpellBuffs.Add(minion, "StunDuringMonsoon",
                    BuffType.Stun, .2, npcJanna.NetId) is not null);
            npcRuntime.Delivery.Flush();
            TestAssert.True(ReferenceEquals(npcPush, minion.ForcedMove),
                $"{team}: new Stun retains the minion's real forced move");
            TestAssert.False(npc.Captures.Any(capture => capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == 0xC1 && packet.EntityReferences.Any(reference =>
                        reference.NetId == minion.NetId))),
                $"{team}: NPC Stun does not publish a stationary route over Monsoon");
            var npcStart = minion.Position;
            // A forced move consumes only the part of a frame after its start (AdvanceForcedMove), so a half-length NPC frame (0.05 of the
            // 0.1 s cohort) can be consumed by the lead of the simulation clock; a whole NPC frame always leaves a slice that moves it.
            npcRuntime.Movement.Tick(.1);
            TestAssert.True(ReferenceEquals(npcPush, minion.ForcedMove) &&
                            Math.Abs(minion.Position.X - npcStart.X) +
                            Math.Abs(minion.Position.Z - npcStart.Z) > 1.0,
                $"{team}: genuine minion displacement still moves under Stun");
        }
    }
}
