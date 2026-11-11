using Amberline.Simulation.Combat;
using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("movement.hero-move-cancels-windup")]
    public static void MoveClickCancelsOnlyUnlaunchedBasicAttacks()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "LeeSin" })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match(champion, team);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(100, 0);
            hero.AttackTarget = enemy.NetId;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, enemy));
            var before = enemy.Health;
            if (launched) match.Runtime.Combat.CombatTick(hero.AttackWindup + .01);
            // Batch 741: Lee Sin's swing has no flight, so it lands at its cast frame: once launched it has hit, and no hit is left for the
            // move to keep. Annie's missile is still in flight.
            var flies = hero.MissileSpeed > 0.0;
            TestAssert.Equal(launched && !flies, enemy.Health < before, "a launched melee swing has hit at its cast frame");
            var hit = match.Runtime.Combat.PendingHits.SingleOrDefault(attack => attack.SourceNetId == hero.NetId);
            TestAssert.Equal(launched && !flies ? 0 : 1, match.Runtime.Combat.PendingHits.Count(attack => attack.SourceNetId == hero.NetId));
            if (!launched || flies) TestAssert.Equal(launched, hit.WindupRemaining <= 0);
            match.Clear();
            match.Move(0, new Position2(7900, 7000));
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count > 0);
            var remaining = match.Runtime.Combat.PendingHits.Where(attack => attack.SourceNetId == hero.NetId).ToArray();
            TestAssert.Equal(launched && flies ? 1 : 0, remaining.Length,
                "ordinary movement cancels unfinished windup and preserves a launched attack");
            if (launched && flies) TestAssert.Equal(hit.MissileNetId, remaining[0].MissileNetId);
            foreach (var capture in match.Captures)
            {
                var stopped = capture.Packets.Where(packet => Opcode(packet) == 0x39 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
                TestAssert.Equal(launched ? 0 : 1, stopped.Length,
                    "only the canceled attack receives the native instant-stop packet");
            }
            var health = enemy.Health;
            match.Runtime.Combat.CombatTick(1);
            TestAssert.Equal(launched && flies, enemy.Health < health, "only a launched attack can still hit after the move");
        }
    }

    [Check("movement.hero-attack-move")]
    public static void HeroAttackMoveAcquiresAndResumes()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, extraEnemy: true);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var second = match.Hero(2);
            var goal = new Position2(8100, 7000);
            SendHeroOrder(match, 7, goal);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget!.Value, "attack-move picks the nearer enemy");
            TestAssert.Equal(goal, hero.AttackMoveGoal!.Value);
            TestAssert.Equal(0, hero.Waypoints.Count, "an enemy already in range plants the hero");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1 &&
                packet.Plaintext.Length == 17 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId),
                "the client receives a stop path for its predicted ground order");
            match.Runtime.Tick(.25);
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x0D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == enemy.NetId),
                "the shared combat loop emits an ordinary basic attack");
            match.Runtime.Tick(1);
            TestAssert.True(enemy.Health < enemy.MaxHealth, "the acquired attack deals damage");
            enemy.Alive = false;
            match.Runtime.Tick(.2);
            TestAssert.Equal(second.NetId, hero.AttackTarget!.Value, "a dead target is replaced");
            second.Alive = false;
            match.Clear();
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null);
            // Owner decision, 18 October 2026 (Hero.lua TimerCheckAttack: AI_SOFTATTACK, no target left, AI_STANDING): the hero that
            // lost the target its attack-move acquired stands; the order does not resume the walk to its position.
            TestAssert.True(hero.AttackMoveGoal is null && hero.Waypoints.Count == 0,
                "the attack-move ends and the hero stands after the fight");

            // The idle scan takes over: an enemy that comes into acquisition range later is attacked, with no ground destination.
            enemy.Alive = true;
            enemy.Position = hero.Position + new Position2(200, 0);
            match.Runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget!.Value);
            TestAssert.True(hero.AttackMoveGoal is null && hero.HeroPursuit is null,
                "idle acquisition installs no ground destination or explicit target lock");
            enemy.Alive = false;
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0, "and the hero stands again");

            // An attack-move that never met an enemy keeps walking and ends at its (terrain-adjusted) destination.
            SendHeroOrder(match, 7, goal);
            TestAssert.True(hero.Waypoints.Count > 0, "no enemy: the order walks");
            TestAssert.Equal(hero.Waypoints[^1], hero.AttackMoveGoal!.Value,
                "navigation's reachable endpoint becomes the retained destination");
            for (var step = 0; step < 80 && hero.AttackMoveGoal is not null; step++) match.Runtime.Tick(.1);
            TestAssert.True(hero.AttackMoveGoal is null, "a terrain-adjusted destination can finish");
            hero.Position = new Position2(7000, 7000);
            enemy.Alive = true;
            enemy.Position = hero.Position + new Position2(200, 0);
            foreach (var order in new byte[] { 1, 10, 2, 3 })
            {
                SendHeroOrder(match, 7, goal);
                SendHeroOrder(match, order, hero.Position, order == 3 ? enemy.NetId : 0);
                TestAssert.True(hero.AttackMoveGoal is null, $"order {order} replaces attack-move");
            }
        }
    }

    [Check("movement.hero-attack-move-visibility")]
    public static void HeroAttackMoveRespectsVisibilityAndControl()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(350, 0);
            var goal = new Position2(8100, 7000);
            var hidden = new BuffState("AttackMoveHidden", slot: 60, expiresAt: 100)
                { Payload = new BuffPayload { Stealthed = true } };
            enemy.Buffs.Add(hidden);
            SendHeroOrder(match, 7, goal);
            TestAssert.True(hero.AttackTarget is null, "attack-move cannot acquire a stealthed enemy");
            TestAssert.True(hero.Waypoints.Count > 0);
            enemy.Buffs.Remove(hidden);
            var snare = new BuffState("AttackMoveRoot", type: BuffType.Snare, slot: 60, expiresAt: 100);
            hero.Buffs.Add(snare);
            var held = hero.Position;
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null, "a blocked order cannot start a chase");
            TestAssert.Equal(held, hero.Position);
            hero.Buffs.Remove(snare);
            match.Runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget!.Value);
            TestAssert.True(hero.Waypoints.Count > 0, "melee attack-move closes to weapon range");
            var endpoint = hero.Waypoints[^1];
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii; audit A5: the client's plan ends next to the body
            var endpointDistance = Math.Sqrt((endpoint.X - enemy.Position.X) * (endpoint.X - enemy.Position.X) +
                (endpoint.Z - enemy.Position.Z) * (endpoint.Z - enemy.Position.Z));
            TestAssert.True(endpointDistance <= CombatService.AttackReach(hero, enemy) && endpointDistance > enemy.Radius,
                "the chase route ends within weapon reach and outside the target's body");

            enemy.Buffs.Add(hidden);
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null, "soft acquisition forgets a hidden target");
            // Owner decision, 18 October 2026: the lost soft target ends the attack-move (AI_STANDING); the hero does not walk on.
            TestAssert.True(hero.AttackMoveGoal is null && hero.Waypoints.Count == 0,
                "a lost soft target ends the attack-move and the hero stands");
            SendHeroOrder(match, 7, goal);
            TestAssert.True(hero.AttackMoveGoal is not null && hero.Waypoints.Count > 0, "a fresh attack-move walks again");
            SendHeroOrder(match, 10, hero.Position);
            match.Runtime.Tick(.4);
            TestAssert.True(hero.AttackMoveGoal is null && hero.Waypoints.Count == 0,
                "stop cannot be overwritten by the next acquisition scan");

            SendHeroOrder(match, 7, goal);
            TestAssert.True(match.Runtime.Session.Disconnect(1));
            TestAssert.True(hero.AttackMoveGoal is null, "disconnect cancels the active order");
        }
    }

    private static void SendHeroOrder(Match match, byte order, Position2 destination, uint target = 0)
    {
        var packet = ClientGameplayPackets.MoveOrder(match.Hero(0).NetId, order, destination, target);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span,
            match.Runtime.Dependencies.Gameplay), $"native hero order {order} is accepted");
        match.Runtime.Delivery.Flush();
    }

    [Check("movement.hero-attack-move-lifecycle")]
    public static void HeroAttackMoveRecallAndDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var attackOrder in new byte[] { 3, 7 })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            SendHeroOrder(match, attackOrder, new Position2(7800, 7000),
                attackOrder == 3 ? enemy.NetId : 0);
            match.Cast(0, SpellSystem.RecallSlot, hero);
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0 && hero.AttackMoveGoal is null && hero.HeroPursuit is null,
                "native recall replaces the attack order through the production dispatcher");
            match.Runtime.Tick(.4);
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0 && hero.AttackTarget is null && hero.Waypoints.Count == 0,
                "an acquisition scan cannot interrupt recall");
            SendHeroOrder(match, attackOrder, new Position2(7800, 7000),
                attackOrder == 3 ? enemy.NetId : 0);
            // Batch 47 (a84f249): an order inside Recall's uncancellable 0.5 s windup (0.4 s here) is held and applied
            // 0.1 s after the windup, which cancels the recall.
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0 && hero.AttackTarget is null,
                "an attack-move inside the Recall windup is held, not applied");
            match.Runtime.Tick(.3);
            TestAssert.Equal(0.0, match.Runtime.Spells.RecallRemaining(hero), "a new attack-move cancels recall");
            TestAssert.Equal(enemy.NetId, hero.AttackTarget!.Value);
            TestAssert.True(match.Runtime.Combat.Kill(hero, enemy.NetId));
            TestAssert.True(hero.AttackMoveGoal is null && hero.HeroPursuit is null,
                "death discards the saved destination and explicit pursuit");
            foreach (var order in new byte[] { 1, 2, 3, 7, 10 })
            {
                var packet = ClientGameplayPackets.MoveOrder(hero.NetId, order, new Position2(7800, 7000));
                TestAssert.False(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span,
                    match.Runtime.Dependencies.Gameplay), "dead heroes cannot install new voluntary orders");
                TestAssert.True(hero.AttackMoveGoal is null);
            }
        }
    }

    [Check("movement.hero-explicit-pursuit")]
    public static void HeroExplicitAttackTracksMovingTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(7400, 7000);
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            var initialEnd = hero.Waypoints[^1];
            enemy.Position = new Position2(7700, 7000);
            match.Clear();
            match.Runtime.Tick(.2);
            TestAssert.True(hero.Waypoints.Count > 0 && hero.Waypoints[^1].X > initialEnd.X + 200,
                "explicit pursuit refreshes its endpoint when the visible target moves");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId));
            enemy.Position = hero.Position + new Position2(100, 0);
            match.Runtime.Tick(.2);
            TestAssert.Equal(0, hero.Waypoints.Count, "a target entering weapon range stops the chase");
            enemy.Position = hero.Position + new Position2(600, 0);
            match.Runtime.Tick(.2);
            TestAssert.True(hero.Waypoints.Count > 0, "a target leaving range restarts pursuit without another click");
            SendHeroOrder(match, 10, hero.Position);
            match.Runtime.Tick(.4);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0,
                "Stop cancels autonomous pursuit");
        }
    }

    [Check("movement.hero-explicit-pursuit-fog")]
    public static void HeroExplicitAttackUsesLastKnownPosition()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(7600, 7000);
            var lastSeen = enemy.Position;
            SendHeroOrder(match, 3, lastSeen, enemy.NetId);
            var hidden = new BuffState("PursuitHidden", slot: 60, expiresAt: 100)
                { Payload = new BuffPayload { Stealthed = true } };
            enemy.Buffs.Add(hidden);
            enemy.Position = new Position2(7000, 10000);
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null, "a hidden target cannot remain an attack target");
            TestAssert.True(hero.Waypoints.Count > 0);
            TestAssert.Near(lastSeen.X, hero.Waypoints[^1].X, 3,
                "hard pursuit uses the client-grid last visible X");
            TestAssert.Near(lastSeen.Z, hero.Waypoints[^1].Z, 3,
                "hard pursuit uses the client-grid last visible Z");
            enemy.Buffs.Remove(hidden);
            enemy.Position = new Position2(7500, 7000);
            match.Runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget!.Value,
                "a target revealed before arrival restores explicit pursuit");
            lastSeen = enemy.Position;
            enemy.Buffs.Add(hidden);
            enemy.Position = new Position2(7000, 10000);
            for (var step = 0; step < 30; step++) match.Runtime.Tick(.1);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0);
            TestAssert.Near(lastSeen.X, hero.Position.X, 10);
            TestAssert.Near(lastSeen.Z, hero.Position.Z, 10);
        }
    }

    [Check("movement.hero-pursuit-engage-quiet-windup")]
    public static void HeroPursuitEngagesWithoutRouteInWindup()
    {
        // Playtest B2 AE7: Annie (310) chasing Ezreal (305) stood routeless at
        // the range boundary, and her swing shared a flush with (or had its
        // windup cut by) her own route/stop; the stock client runs 0x0D (ch3)
        // before 0xC1 (ch4).
        var (start, heading) = OpenPursuitCorridor(NavigationGrid.LoadDefault(), 1800.0);
        Position2 Along(double distance) =>
            new(start.X + heading.X * distance, start.Z + heading.Z * distance);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, enemyChampion: "Ezreal");
            var runtime = match.Runtime;
            var annie = match.Hero(0);
            var ezreal = match.Hero(1);
            annie.Position = Along(0.0);
            ezreal.Position = Along(500.0);
            annie.Speed = 310.0;
            ezreal.Speed = 305.0;
            runtime.Delivery.Flush();
            // Ezreal flees, stops, then flees again; Annie attacks from range.
            match.Move(1, Along(1100.0));
            SendHeroOrder(match, 3, ezreal.Position, ezreal.NetId);
            bool Winding() => runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == annie.NetId &&
                hit.SourceKind == DamageSourceKind.Basic && hit.WindupRemaining > 0.0);
            bool AnnieSent(byte opcode) => match.Captures[0].Packets.Any(packet => Opcode(packet) == opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == annie.NetId);
            var swings = new List<int>();
            var stall = 0;
            var longestStall = 0;
            for (var step = 1; step <= 180; step++)
            {
                if (step == 90) match.Move(1, Along(1700.0));
                var windingBefore = Winding();
                match.Clear();
                runtime.Tick(1.0 / 30.0);
                var swing = AnnieSent(0x0D);
                var route = AnnieSent(0xC1);
                TestAssert.False(swing && route,
                    $"{team} step {step}: Annie's route/stop shares the flush of her 0x0D");
                TestAssert.False(route && windingBefore && !AnnieSent(0x39),
                    $"{team} step {step}: Annie's route/stop lands inside an uncancelled windup");
                if (swing) swings.Add(step);
                var routed = annie.Target is not null || annie.Waypoints.Count > 1 ||
                    annie.Waypoints.Count == 1 && Distance(annie.Position, annie.Waypoints[0]) > 3.0;
                // Out of range, not winding up and without a route. Hero.lua
                // rescans every 0.2 s, so a stall may last until the next scan
                // (six steps) but never across it.
                stall = !runtime.Combat.CanAttack(annie, ezreal) && !Winding() && !routed ? stall + 1 : 0;
                longestStall = Math.Max(longestStall, stall);
            }
            TestAssert.True(swings.Count >= 2 && swings.Any(step => step > 45),
                $"{team}: Annie re-engages from the pursuit (swings at steps {string.Join(",", swings)})");
            TestAssert.True(longestStall <= 6,
                $"{team}: Annie stood out of range without a route for {longestStall} steps");
        }
    }

    [Check("movement.hero-melee-pursuit-fleeing-target")]
    public static void HeroMeleePursuitHitsFleeingTarget()
    {
        // Playtest B3 #4, 26 Sept 2026: Blitzcrank (300) chasing a fleeing
        // Golem (180, radius 24.575) parked just outside range for ten
        // seconds. Pursuit aimed at a range-edge point tied to the target's
        // scan-time position, and the swing deferred for a quiet flush
        // re-checked the strict start range after the target walked on.
        var (start, heading) = OpenPursuitCorridor(NavigationGrid.LoadDefault(), 1500.0);
        Position2 Along(double distance) =>
            new(start.X + heading.X * distance, start.Z + heading.Z * distance);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Blitzcrank", team);
            var runtime = match.Runtime;
            var blitz = match.Hero(0);
            var target = match.Hero(1);
            blitz.Position = Along(0.0);
            target.Position = Along(250.0);
            blitz.Speed = 300.0;
            target.Speed = 180.0;
            target.Radius = 24.575;
            runtime.Delivery.Flush();
            match.Move(1, Along(1350.0));
            SendHeroOrder(match, 3, target.Position, target.NetId);
            bool Winding() => runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == blitz.NetId &&
                hit.SourceKind == DamageSourceKind.Basic && hit.WindupRemaining > 0.0);
            bool BlitzSent(byte opcode) => match.Captures[0].Packets.Any(packet => Opcode(packet) == opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == blitz.NetId);
            int? firstSwing = null;
            var landedWhileFleeing = false;
            for (var step = 1; step <= 90 && !landedWhileFleeing; step++)
            {
                var windingBefore = Winding();
                var taken = target.MatchStatistics.PhysicalDamageTaken;
                match.Clear();
                runtime.Tick(1.0 / 30.0);
                var swing = BlitzSent(0x0D);
                var route = BlitzSent(0xC1);
                // Batch 2 AE7's quiet engagement still holds.
                TestAssert.False(swing && route,
                    $"{team} step {step}: Blitzcrank's route/stop shares the flush of his 0x0D");
                TestAssert.False(route && windingBefore && !BlitzSent(0x39),
                    $"{team} step {step}: Blitzcrank's route/stop lands inside an uncancelled windup");
                if (swing) firstSwing ??= step;
                landedWhileFleeing = target.MatchStatistics.PhysicalDamageTaken > taken &&
                    target.Waypoints.Count > 0;
            }
            TestAssert.True(firstSwing is <= 45,
                $"{team}: Blitzcrank swings within 1.5 s (first 0x0D at step {firstSwing})");
            TestAssert.True(landedWhileFleeing,
                $"{team}: Blitzcrank lands a hit while the target is still walking away");
        }
    }

    private static (Position2 Start, Position2 Heading) OpenPursuitCorridor(NavigationGrid grid, double length)
    {
        // A straight, brush-free lane far from every turret keeps the chase
        // about engagement timing rather than terrain, fog or tower aggro.
        for (var x = 3000.0; x <= 11000.0; x += 250.0)
        for (var z = 3000.0; z <= 11000.0; z += 250.0)
        for (var index = 0; index < 16; index++)
        {
            var heading = new Position2(Math.Cos(index * Math.PI / 8.0), Math.Sin(index * Math.PI / 8.0));
            var clear = true;
            for (var along = 0.0; clear && along <= length; along += 25.0)
            for (var side = -100.0; clear && side <= 100.0; side += 50.0)
            {
                var point = new Position2(x + heading.X * along - heading.Z * side,
                    z + heading.Z * along + heading.X * side);
                clear = grid.IsMovementWalkable(point) && grid.BrushIdAt(point) is null &&
                    MapDefinitions.TurretPositions.Values.All(turret => Distance(turret, point) > 1100.0);
            }
            if (clear) return (new Position2(x, z), heading);
        }
        throw new InvalidOperationException("Map1 has no open straight pursuit corridor");
    }

    [Check("movement.hero-idle-acquisition")]
    public static void HeroIdleAcquisitionAndHoldRelease()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, autoAcquire: true);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.Runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0,
                "a spawned idle hero acquires a visible nearby enemy");
            SendHeroOrder(match, 10, hero.Position);
            match.Runtime.Tick(.4);
            TestAssert.True(hero.AttackTarget is null, "S/H press suppresses idle acquisition");
            SendHeroOrder(match, 1, hero.Position);
            match.Runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0,
                "H release re-enables idle acquisition");
            SendHeroOrder(match, 2, new Position2(7400, 7000));
            match.Runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count > 0,
                "ordinary movement does not acquire before arrival");
            for (var step = 0; step < 30 && hero.AttackTarget is null; step++) match.Runtime.Tick(.1);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0,
                "arrival restores idle acquisition after a move order");
            TestAssert.True(hero.AttackMoveGoal is null && hero.HeroPursuit is null,
                "idle acquisition does not install a ground destination or explicit target lock");
        }
    }
}
