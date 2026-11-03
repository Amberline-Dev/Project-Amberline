using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-q-missile-identity")]
    public static void SonicWaveIdentity()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var distance in new[] { 30.0, 300.0, 800.0 })
        {
            using var match = new Match("LeeSin", team, extraEnemy: true);
            var lee = match.Hero(0); var first = match.Hero(1); var second = match.Hero(2);
            first.Position = lee.Position + new Position2(distance, 0);
            second.Position = first.Position + new Position2(150, 0);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 0, first);
            match.Runtime.Tick(1);
            foreach (var capture in match.Captures)
            {
                var cast = capture.Packets.Single(packet => Opcode(packet) == 0xBD);
                // 19 October 2026 (batch 105): Lee's Q is the script's line missile; the client creates it at its own cast frame from the cast
                // packet's missile id, so the cast is the only carrier of the identity (no separate launch packet) and the retire names it.
                var missile = BinaryPrimitives.ReadUInt32LittleEndian(cast.Plaintext.Span[(9 + 0x13)..]);
                TestAssert.True(missile != 0, "the cast carries the missile identity the client creates its missile under");
                var damage = capture.Packets.Where(packet => Opcode(packet) == 0x68).Select(Decode).ToArray();
                TestAssert.Equal(1, damage.Length, "Q deals damage once");
                TestAssert.Equal(first.NetId, damage[0].GetProperty("target_netid").GetUInt32(), "first body intercepts Q");
                TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missile), "the cast's missile retires once");
            }
        }
    }

    [Check("spells-live-revenant-pursuit-and-follow")]
    public static void RevenantPursuitAndFollow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ranged in new[] { false, true })
        {
            using var match = new Match("Yorick", team, ally: true);
            var runtime = match.Runtime; var owner = match.Hero(0); var enemy = match.Hero(1);
            var source = ranged ? match.Hero(2) : owner;
            if (ranged) { source.AttackRange = 550; source.MissileSpeed = 1400; }
            enemy.Position = new Position2(7350, 7000);
            source.Position = owner.Position;
            match.Cast(0, 3, source); runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            match.Clear();
            for (var tick = 0; tick < 60; tick++) runtime.Tick(.1);
            var damage = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x68)
                .Select(Decode).Where(fields => fields.GetProperty("target_netid").GetUInt32() == enemy.NetId).ToArray();
            TestAssert.True(damage.Length >= 2,
                $"revenant closes distance and attacks repeatedly: ranged={ranged}, hits={damage.Length}, pos={clone.Position}, target={clone.AttackTarget}, range={clone.AttackRange}, cooldown={clone.AttackCooldown}");
        }
        using var follow = new Match("Yorick", Team.Order);
        follow.Hero(1).Position = new Position2(10000, 10000);
        var yorick = follow.Hero(0);
        follow.Cast(0, 3, yorick); follow.Runtime.Tick(.3);
        var pet = follow.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
        pet.Position = yorick.Position + new Position2(-500, 0);
        // Moving the pet by hand leaves the spawn overlap-recovery route behind; its end is inside the follow-drift
        // gate, so clear it as a real stop would.
        pet.Waypoints.Clear(); pet.Target = null;
        for (var tick = 0; tick < 8; tick++) follow.Runtime.Tick(.1);
        TestAssert.True(pet.Waypoints.Count > 0, "idle clone starts following");
        // owner decision 28 Sept 2026: original game values
        TestAssert.True(Distance(pet.Waypoints[^1], yorick.Position) <= 3,
            "idle return aims at the owner position");
        for (var tick = 0; tick < 15; tick++) follow.Runtime.Tick(.1);
        TestAssert.True(Distance(pet.Position, yorick.Position) >=
            follow.Runtime.Movement.BodyClearance(pet, yorick) - 2.0 &&
            // Batch 316: PetOrders.ReturnRadius is gone; the radius is the map constant GetPetReturnRadius reads.
            Distance(pet.Position, yorick.Position) <=
                Amberline.Data.GameData.Default.LevelConstants.Values["ai_DefaultPetReturnRadius"],
            "clone stops inside the return radius without overlapping owner");
        TestAssert.True(pet.Waypoints.Count == 0 && pet.Target is null,
            "idle return stops the route before reaching the owner endpoint");
    }

    [Check("spells-live-revenant-repeated-attack-orders")]
    public static void RepeatedRevenantAttackOrders()
    {
        using var match = new Match("Yorick", Team.Order);
        var runtime = match.Runtime; var owner = match.Hero(0); var enemy = match.Hero(1);
        enemy.Position = owner.Position + new Position2(130, 0);
        match.Cast(0, 3, owner); runtime.Tick(.3); match.Clear();
        for (var tick = 0; tick < 50; tick++)
        {
            runtime.Pets.Command(owner, 5, enemy.Position, enemy.NetId);
            runtime.Tick(.1);
        }
        TestAssert.True(match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x68) >= 2,
            "repeated attack commands retain an unfinished swing against the same target");
    }

    [Check("spells-live-q-dash-animation-lifecycle")]
    public static void DashAnimationLifecycle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "landing", "death", "lost-target", "interrupted" })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new Position2(7600, 7000);
            match.Cast(0, 0, enemy); runtime.Tick(1); lee.Mana = 200; match.Clear();
            match.Cast(0, 0, enemy);
            // 19 October 2026: the travel effect is the particle BlindMonkQTwoDash creates (blindMonk_Q_resonatingStrike_mis, bound to Lee Sin); its id
            // is read from the creation packet, since the script keeps it in a variable.
            var dashEffect = LeeEffectId(match, "blindMonk_Q_resonatingStrike_mis");
            TestAssert.True(dashEffect != 0, "Q2 owns a persistent dash effect before cleanup");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet => Opcode(packet) == 0xBA),
                "Q2 must not push character fade: stock animation registration is 0xB8");
            var animation = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0xB8).Plaintext.Span;
            TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(animation[1..]), "animate the dashing champion");
            TestAssert.Equal(0x24Au, BinaryPrimitives.ReadUInt32LittleEndian(animation[5..]),
                "native replace/loop/lock/no-blend flags");
            TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(animation[9..]), 0, "native clip speed");
            TestAssert.Equal("Spell1b\0", System.Text.Encoding.ASCII.GetString(animation[13..]), "shipped Q2 flight clip");
            if (ending == "death") runtime.Combat.Kill(lee, enemy.NetId);
            if (ending == "lost-target") enemy.Position = new Position2(11000, 11000);
            if (ending == "interrupted") runtime.Movement.StartForcedMove(lee.NetId,
                lee.Position + new Position2(-100, 0), 1000, .2, enemy.NetId, "test-displacement", cancelOrder: true);
            runtime.Tick(1);
            var stop = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x2B).Plaintext.Span;
            TestAssert.Equal(9, stop.Length, "native unlock packet length");
            TestAssert.Equal(0x40u, BinaryPrimitives.ReadUInt32LittleEndian(stop[5..]), "unlock and blend on landing/death");
            TestAssert.True(match.Captures[1].Packets.Any(packet => Opcode(packet) == 0x2B),
                "an observer that saw the dash receives cleanup even after going out of vision");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == FxPackets.KillOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == dashEffect),
                "Q2 retires its travel effect exactly once");
            // 19 October 2026: the dash is a script move (BbScriptMoves detaches it and ends its buff). The test displacement that replaces it has no
            // owner, so it stays attached as a completed move; what must not remain is the dash itself, its buff or an unfinished move.
            TestAssert.True(lee.ForcedMove is null || ending == "interrupted" && lee.ForcedMove is { Completed: true, Effect: "test-displacement" },
                "interruption cannot strand dash state");
            TestAssert.False(lee.Buffs.Any(buff => buff.Name == "BlindMonkQTwoDash") && ending != "lost-target",
                "the dash buff is gone with its move");
            if (ending == "death")
            {
                match.Clear();
                runtime.Tick(.1);
                TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B ||
                    Opcode(packet) == FxPackets.KillOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == dashEffect),
                    "a later dead tick cannot repeat dash cleanup");
            }
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var ally = match.Hero(2);
            ally.Position = lee.Position + new Position2(600, 0);
            match.Cast(0, 1, ally);
            var effect = LeeEffectId(match, "blindMonk_W_self_mis");
            TestAssert.True(effect != 0, "Safeguard has an active travel effect before death");
            match.Clear();
            runtime.Combat.Kill(lee, match.Hero(1).NetId);
            runtime.Delivery.Flush();
            runtime.Tick(.1);
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == FxPackets.KillOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == effect),
                "death retires the Safeguard travel effect exactly once");
            match.Clear();
            runtime.Tick(.1);
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == FxPackets.KillOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == effect),
                "a later dead tick cannot retire Safeguard twice");
        }
    }

    // The effect id of the particle a Lee Sin script created (the id sits in the creation packet; the scripts keep it in variables).
    private static uint LeeEffectId(Match match, string name)
    {
        match.Runtime.Delivery.Flush();
        var create = match.Captures[0].Packets.Single(packet => IsNamedVisual(packet, name));
        return BinaryPrimitives.ReadUInt32LittleEndian(create.Plaintext.Span[25..]);
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));

    [Check("movement-live-stable-crowd-detour")]
    public static void StableCrowdDetour()
    {
        using var match = new Match("LeeSin", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var body = match.Hero(1);
        body.Position = new Position2(7300, 7000); body.Radius = 90;
        runtime.Movement.SetMoveOrder(hero.NetId, new Position2(7800, 7000));
        var route = hero.Waypoints;
        var corner = route.First(point => Math.Abs(point.Z - 7000) > 20);
        // The body steps away from the chosen side, so the existing detour
        // stays safe while the direct route is still obstructed.
        body.Position += new Position2(0, corner.Z < 7000 ? 20 : -20);
        runtime.Movement.Tick(.3);
        TestAssert.True(ReferenceEquals(route, hero.Waypoints),
            "a safe crowd detour survives small body movements instead of changing steering side");
        // Collision retries can leave an active waypoint inside a stationary
        // body. Recovery must not depend on that body moving again.
        // Batch 61: the server-side stall retry (RefreshHeroOrders, the stall re-plan) is deleted; the stock hero never re-plans. A
        // waypoint left inside a stationary body is recovered by the client's own update: the bend step pushes interior
        // waypoints out of the cluster once the hero has been refused four times without progress, and CanCollide gives up on the body
        // after 15 stuck frames of a flagged hero (so the hero may pass through it then, which is why no overlap is asserted). The
        // body does not have to move.
        hero.Waypoints[0] = body.Position;
        for (var tick = 0; tick < 200 && Distance(hero.Position, new Position2(7800, 7000)) > 5.0; tick++)
            runtime.Movement.Tick(Amberline.Simulation.Movement.MovementSystem.HeroTickSeconds);
        TestAssert.True(hero.Waypoints.All(point => Distance(point, body.Position) > body.Radius),
            "recovery removes the occupied waypoint");
        TestAssert.True(Distance(hero.Position, new Position2(7800, 7000)) <= 5.0,
            "a stalled order recovers against a stationary obstacle without the body moving");
    }

    [Check("movement-live-crossing-minion-wave")]
    public static void CrossingMinionWave()
    {
        foreach (var reverse in new[] { false, true })
        {
            using var match = new Match("LeeSin", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0);
            match.Hero(1).Position = new Position2(10000, 10000);
            hero.Position = new Position2(reverse ? 7800 : 7000, 7000);
            var goal = new Position2(reverse ? 7000 : 7800, 7000);
            var bodies = new List<Entity>();
            for (var column = 0; column < 3; column++)
            for (var row = 0; row < 4; row++)
            {
                var body = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, row % 2 == 0 ? Team.Order : Team.Chaos,
                    new Position2(7310 + column * 85, 6850 + row * 100))
                { Radius = 35, Speed = 325, Health = 1000, MaxHealth = 1000, Alive = true, Visible = true };
                runtime.World.Register(body); bodies.Add(body);
            }
            runtime.Movement.SetMoveOrder(hero.NetId, goal);
            var stalled = 0; var longestStall = 0; var reversals = 0; var previous = Position2.Zero;
            var tick = 0;
            for (; tick < 100 && Distance(hero.Position, goal) > 5; tick++)
            {
                // Both ranks advance through the crossing while retaining the
                // lane units' real collision sizes; a single hero click owns
                // the destination throughout.
                for (var i = 0; i < bodies.Count; i++)
                    bodies[i].Position += new Position2(i % 2 == 0 ? -3 : 3, i % 2 == 0 ? 2 : -2);
                var start = hero.Position;
                runtime.Movement.Tick(.1);
                var step = hero.Position - start;
                if (Distance(hero.Position, start) < 1) { stalled++; longestStall = Math.Max(longestStall, stalled); }
                else stalled = 0;
                if (step.X * previous.X + step.Z * previous.Z < -25) reversals++;
                previous = step;
            }
            TestAssert.True(Distance(hero.Position, goal) <= 5 && longestStall < 5 && reversals <= 2,
                $"crossing completes without repeated stalls/turnarounds: reverse={reverse}, ticks={tick}, remaining={Distance(hero.Position, goal)}, stall={longestStall}, reversals={reversals}");
        }
    }
}
