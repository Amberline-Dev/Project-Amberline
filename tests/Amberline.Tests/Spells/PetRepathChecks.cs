using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (17 October 2026) pet wire fixes from the real-client telemetry (T5): a pet sends a route or a stop only when
/// something changed. Every check reads the 0xC1 packets an observer was delivered.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Restated in batch 229 (owner, 25 October 2026: retail re-paths whenever its script or native state asks, with no tolerance on
    // the route's end). A chase re-paths as the point it walks to moves. Renamed from pets-wire-chase-keeps-route-while-target-drifts.
    [Check("pets-wire-chase-repaths-as-the-target-drifts")]
    public static void ChasingPetsRepathAsTheTargetDrifts()
    {
        foreach (var scenario in new[] { "Shaco", "Yorick", "Annie" })
        {
            using var match = new Match(scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var pet = SpawnRetailFollowPet(match, scenario);
            runtime.Tick(0.4);
            owner.Position = new Position2(6600, 7000);
            pet.Position = new Position2(6700, 7000);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            var target = PursuitDummy(match, Team.Chaos, new Position2(7500, 7000), "ChaseDrift");
            runtime.Delivery.Flush();
            match.Clear();

            // A hard attack order at any range. The target creeps 12 units a second, 18 units over the 1.5 s window.
            SendOwnedPetOrder(match, pet, 5, target.Position, target.NetId);
            var start = pet.Position;
            for (var tick = 0; tick < 45; tick++)
            {
                target.Position += new Position2(0, 0.4);
                runtime.Tick(1.0 / 30.0);
            }
            var routes = PetRoutes(match, pet.NetId).Where(route => route.Length > 1).ToArray();
            TestAssert.True(Math.Sqrt(Math.Pow(pet.Position.X - start.X, 2) + Math.Pow(pet.Position.Z - start.Z, 2)) > 150.0,
                scenario + ": the pet walked toward the target");
            TestAssert.True(routes.Length >= 2,
                scenario + ": a target that drifts re-paths the chase as it moves (retail re-paths on ask), got " + routes.Length +
                " routes: " + string.Join(" / ", routes.Select(route => $"{route[^1].X:F0},{route[^1].Z:F0}")));
            var lastEnd = routes[^1][^1];
            TestAssert.True(Math.Sqrt(Math.Pow(lastEnd.X - target.Position.X, 2) + Math.Pow(lastEnd.Z - target.Position.Z, 2)) <=
                            CombatService.AttackReach(pet, target) + 1.0,
                scenario + ": the last route ends within attack reach of the drifted target");

            // The target jumps 400 units: the pet is sent after it, once.
            match.Clear();
            target.Position += new Position2(0, 400);
            for (var tick = 0; tick < 10; tick++) runtime.Tick(1.0 / 30.0);
            routes = PetRoutes(match, pet.NetId).Where(route => route.Length > 1).ToArray();
            // The approach point is recomputed from the pet's own position while it walks, so the jump draws a few re-sent routes
            // (retail re-paths on ask): the check keeps the property that they end at the target's new position.
            TestAssert.True(routes.Length >= 1, scenario + ": the target's 400-unit jump draws a new route");
            var reach = CombatService.AttackReach(pet, target);
            var end = routes[0][^1];
            TestAssert.True(Math.Sqrt(Math.Pow(end.X - target.Position.X, 2) + Math.Pow(end.Z - target.Position.Z, 2)) <= reach,
                scenario + ": the new route ends within attack reach of the target's new position");
        }
    }

    // Restated in batch 229 (owner, 25 October 2026: no re-path tolerance). The clone re-paths when the point it walks to moves, far
    // away or near: a 150-unit sideways step of a target a kilometre away moves that point about 150 units. Renamed from
    // pets-wire-clone-chase-tolerance-shrinks-with-distance (PetOrders.CloneChaseRepathDistance is removed).
    [Check("pets-wire-clone-chase-repaths-on-every-target-move")]
    public static void TheCloneRepathsToAMovedTargetAtAnyDistance()
    {
        foreach (var scenario in new[] { "Shaco", "Yorick" })
        foreach (var (distance, shift) in new[] { (1000.0, 150.0), (450.0, 400.0) })
        {
            using var match = new Match(scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var pet = SpawnRetailFollowPet(match, scenario);
            runtime.Tick(0.4);
            owner.Position = new Position2(6600, 7000);
            pet.Position = new Position2(6700, 7000);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            var target = PursuitDummy(match, Team.Chaos, new Position2(6700 + distance, 7000), "ChaseFraction");
            runtime.Delivery.Flush();
            SendOwnedPetOrder(match, pet, 5, target.Position, target.NetId);
            match.Clear();
            var held = runtime.Pets.LastDestinations[pet.NetId];
            target.Position += new Position2(0, shift);
            for (var tick = 0; tick < 6; tick++) runtime.Tick(1.0 / 30.0);
            var routes = PetRoutes(match, pet.NetId).Where(route => route.Length > 1).ToArray();
            TestAssert.True(routes.Length >= 1,
                $"{scenario}: a {shift}-unit step of a target {distance} units away is re-pathed" +
                $" (pet {pet.Position}, held end {held}, now {runtime.Pets.LastDestinations[pet.NetId]}, target {target.Position})");
        }
    }

    [Check("pets-wire-new-target-is-routed-at-once")]
    public static void AChangedTargetGetsItsRouteInTheSameFlush()
    {
        foreach (var scenario in new[] { "Shaco", "Yorick" })
        {
            using var match = new Match(scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var pet = SpawnRetailFollowPet(match, scenario);
            runtime.Tick(0.4);
            owner.Position = new Position2(6600, 7000);
            pet.Position = new Position2(6700, 7000);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            var first = PursuitDummy(match, Team.Chaos, new Position2(7500, 7000), "ChaseFirst");
            var second = PursuitDummy(match, Team.Chaos, new Position2(6700, 7700), "ChaseSecond");
            runtime.Delivery.Flush();
            SendOwnedPetOrder(match, pet, 5, first.Position, first.NetId);
            for (var tick = 0; tick < 12; tick++) runtime.Tick(1.0 / 30.0);

            // A different target is a state change whatever the geometry: the chase tolerance never holds the old target's route.
            match.Clear();
            SendOwnedPetOrder(match, pet, 5, second.Position, second.NetId);
            var routes = PetRoutes(match, pet.NetId);
            // Batch 291 (restated to batch 270, which sends every route a script decision asks for): Pet.lua's OnOrder answers
            // ORDER_PETHARDATTACK with TurnOffAutoAttack(STOPREASON_TARGET_LOST) and then SetStateAndCloseToTarget, so a pet that was
            // walking gets the stop and then the route, in the order's own flush. The client takes both in order (the sync gate keeps
            // increasing ids) through the client's follow-server-path step, and the stop stands where the route starts.
            TestAssert.True(routes.Count is 1 or 2, scenario + ": the order's own flush answers the new target: " + routes.Count);
            if (routes.Count == 2)
                TestAssert.True(routes[0].Length == 1 && routes[0][0] == routes[1][0],
                    scenario + ": the script's stop first, where the route starts");
            TestAssert.True(routes[^1].Length > 1, scenario + ": and then the route");
            var end = routes[^1][^1];
            TestAssert.True(Math.Sqrt(Math.Pow(end.X - second.Position.X, 2) + Math.Pow(end.Z - second.Position.Z, 2)) <=
                            CombatService.AttackReach(pet, second), scenario + ": the route ends at the new target");
        }
    }

    // Batch 229 (owner, 25 October 2026: retail re-sends an AI unit's path whenever its script or native state asks, even if
    // worse): the wire sends each stop asked for, so this check was renamed from pets-wire-identical-stop-is-never-repeated,
    // whose one-stop expectation pinned the batch 226 dedupe.
    [Check("pets-wire-identical-stop-is-sent-each-time")]
    public static void APetSendsEachStopItIsAskedIncludingOneWhereItsRouteEnded()
    {
        foreach (var scenario in new[] { "Shaco", "Annie" })
        {
            using var match = new Match(scenario, Team.Order);
            var runtime = match.Runtime;
            var pet = SpawnRetailFollowPet(match, scenario);
            runtime.Tick(0.4);
            // Hallucinate places the clone at random; stand it on open ground a little way from its owner (a held clone that
            // overlaps him walks clear, which is a route and not what this check is about).
            match.Hero(0).Position = new Position2(6450, 7000);
            pet.Position = new Position2(6700, 7000);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            runtime.Delivery.Flush();
            match.Clear();

            // The owner's hold order stops the pet through the production path (Pet.lua OnOrder -> Stop); each order is an ask, so three
            // of them at one place are three stops on the wire (restated from one stop, owner 25 October 2026).
            for (var repeat = 0; repeat < 3; repeat++)
            {
                SendOwnedPetOrder(match, pet, 11, pet.Position);
                runtime.Tick(0.2);
            }
            var here = pet.Position;
            TestAssert.Equal(3, PetRoutes(match, pet.NetId).Count(route => route.Length == 1 &&
                Math.Sqrt(Math.Pow(route[0].X - here.X, 2) + Math.Pow(route[0].Z - here.Z, 2)) <= 2.0),
                scenario + ": three hard-stop orders at one place reached the observer as three stops");

            // A stop somewhere else is a real correction.
            match.Clear();
            pet.Position += new Position2(80, 0);
            SendOwnedPetOrder(match, pet, 11, pet.Position);
            TestAssert.Equal(1, PetRoutes(match, pet.NetId).Count, scenario + ": a stop 80 units away is sent");

            // A move order, then a stop where the route ended: the clients already stand there, and retail still re-sends the stop it is asked for.
            var destination = pet.Position + new Position2(90, 0);
            SendOwnedPetOrder(match, pet, 6, destination);
            for (var tick = 0; tick < 60 && (pet.Waypoints.Count > 0 || pet.Target is not null); tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(pet.Waypoints.Count == 0 && pet.Target is null, scenario + ": the pet arrived");
            match.Clear();
            SendOwnedPetOrder(match, pet, 11, pet.Position);
            TestAssert.Equal(1, PetRoutes(match, pet.NetId).Count, scenario + ": a stop where the last route ended is sent too");
        }
    }

    // Restated in batch 229 (owner, 25 October 2026: retail re-sends on ask): every stop a pet, a box or a hero is asked for goes out.
    // Renamed from pets-wire-sink-drops-repeated-stop-per-pet-kind; the raw inputs (the stops, the route ends, the teleport) are unchanged.
    [Check("pets-wire-sink-sends-every-stop-it-is-asked")]
    public static void TheEffectSinkSendsEveryStopEveryKindIsAskedFor()
    {
        foreach (var scenario in new[] { "Shaco", "Annie", "Yorick", "Box", "Hero" })
        {
            using var match = new Match(scenario == "Box" || scenario == "Hero" ? "Shaco" : scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            runtime.Tick(10.1);
            Entity unit;
            if (scenario == "Hero") unit = owner;
            else if (scenario == "Box")
            {
                match.CastGround(0, owner.Position + new Position2(0, 100), 1);
                for (var tick = 0; tick < 60 && !runtime.World.Entities.Values.Any(entity => entity.Kind == EntityKind.ShacoBox); tick++)
                    runtime.Tick(1.0 / 30.0);
                unit = runtime.World.Entities.Values.Single(entity => entity.Kind == EntityKind.ShacoBox);
            }
            else if (scenario == "Yorick")
                unit = YorickScriptSupport.SpawnUnit(runtime, owner, "Spectral", owner.Position + new Position2(100, 0), runtime.Combat.CurrentTime);
            else unit = SpawnPet(scenario, match, owner);
            runtime.Delivery.Flush();
            match.Clear();

            var stops = 0;
            for (var repeat = 0; repeat < 3; repeat++)
            {
                runtime.Effects.SendWaypoints(unit, [unit.Position], runtime.World.Clock.SyncId());
                runtime.Delivery.Flush();
                stops = PetRoutes(match, unit.NetId).Count;
            }
            // Retail re-sends a path whenever the unit's script or native state asks (owner, 25 October 2026): a pet's repeated stops go
            // out as a hero's do, one packet per ask.
            TestAssert.Equal(3, stops, scenario + ": three identical stops, flushed one at a time, all go out (a hero's do too)");

            // After a route, a stop where it ends is an ask like any other and goes out; so does one anywhere short of it.
            if (scenario != "Hero")
            {
                var end = unit.Position + new Position2(200, 0);
                match.Clear();
                runtime.Effects.SendWaypoints(unit, [unit.Position, end], runtime.World.Clock.SyncId());
                runtime.Delivery.Flush();
                runtime.Effects.SendWaypoints(unit, [end + new Position2(1.5, 0)], runtime.World.Clock.SyncId());
                runtime.Delivery.Flush();
                TestAssert.Equal(2, PetRoutes(match, unit.NetId).Count, scenario + ": a stop within 2 units of the route's end is sent too");
                runtime.Effects.SendWaypoints(unit, [end - new Position2(20, 0)], runtime.World.Clock.SyncId());
                runtime.Delivery.Flush();
                TestAssert.Equal(3, PetRoutes(match, unit.NetId).Count, scenario + ": a stop 20 units short of it is sent");
            }

            // A teleport moves the unit outside the route stream: the stop after it is not a repeat.
            if (scenario != "Hero")
            {
                match.Clear();
                runtime.Effects.SendTeleport(unit, unit.Position, 7, runtime.World.Clock.SyncId(), staging: null);
                runtime.Effects.SendWaypoints(unit, [unit.Position], runtime.World.Clock.SyncId());
                runtime.Delivery.Flush();
                TestAssert.Equal(1, PetRoutes(match, unit.NetId).Count, scenario + ": the stop that follows a teleport is sent");
            }
        }
    }

    // Restated in batch 229 (owner, 25 October 2026): the box never walks, so it never has a route. Its stops are the holds its buffs
    // cause (BuffLifecycle.AddBuff sends a stop to any rooted unit a buff lands on: Stealth at 2 s, EndKill on waking) and the
    // reveals' anchors, and the wire sends each one as retail does. Renamed from pets-wire-box-is-silent-after-spawn.
    [Check("pets-wire-box-sends-no-route-after-spawn")]
    public static void JackInTheBoxSendsNoRouteAfterItsSpawn()
    {
        // The box has MovementDisabled for its whole life: it never has a route.
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(12000, 12000);
        runtime.Tick(10.1);
        match.CastGround(0, shaco.Position + new Position2(0, 100), 1);
        for (var tick = 0; tick < 60 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.ShacoBox); tick++)
            runtime.Tick(1.0 / 30.0);
        var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
        runtime.Delivery.Flush();
        match.Clear();

        runtime.Tick(2.5); // asleep, then the Stealth buff
        enemy.Position = box.Position + new Position2(200, 0); // wakes it (EndKill), the enemy sees it
        for (var tick = 0; tick < 45; tick++) runtime.Tick(1.0 / 30.0);
        enemy.Position = new Position2(12000, 12000); // out of sight and back again
        for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0);
        enemy.Position = box.Position + new Position2(200, 0);
        for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0);

        TestAssert.True(ShacoScriptSupport.IsAwake(box), "the box woke");
        TestAssert.True(match.Captures[1].Packets.Any(packet => packet.Plaintext.Span[0] == 0xC2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == box.NetId),
            "the enemy was shown the box (so the silence is not blindness)");
        TestAssert.Equal(0, PetRoutes(match, box.NetId, 0).Count(route => route.Length > 1),
            "the box's own team gets no route after the spawn (its stops are the holds its buffs cause)");
        TestAssert.Equal(0, PetRoutes(match, box.NetId, 1).Count(route => route.Length > 1),
            "the enemy gets no route with the reveals either");
    }

    // Batch 229 follow-up (coordinator, 25 October 2026): the shipped Pet.lua schedules its orders on its own timers (TimerScanDistance
    // and TimerFindEnemies, 0.15 s, armed by OnInit) and on its events, so the engine issues no order on any other step. A follow whose
    // owner walks in 1/30 s steps re-paths on the scans, about one route per 0.15 s, not one per step.
    [Check("pets-wire-follow-orders-run-on-the-script-scans")]
    public static void AFollowingPetRepathsOnTheScriptScansNotEveryStep()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        match.Hero(1).Position = new Position2(12000, 12000);
        runtime.Tick(10.1);
        owner.Position = new Position2(6600, 6600);
        var pet = SpawnRetailFollowPet(match, "Shaco");
        owner.Position = new Position2(6600, 6600);
        pet.Position = owner.Position + new Position2(-283, -283);
        pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
        ResetRetailPetScan(match, pet);
        runtime.Delivery.Flush();
        match.Clear();
        // 1.5 s of production steps (1/30 s) with the owner walking 3.5 units a step: 45 steps, about 10 scans.
        for (var step = 0; step < 45; step++)
        {
            owner.Position += new Position2(3.5, 0);
            runtime.Tick(1.0 / 30.0);
        }
        var routes = PetRoutes(match, pet.NetId).Count(route => route.Length > 1);
        TestAssert.True(routes <= 11,
            $"1.5 s of a walking owner re-paths the follow about once per 0.15 s scan, not once per step: {routes} routes");
    }

    // Batch 229 follow-up: a planted pet that already stands in reach of a target that stands still is not stopped again; it stopped
    // when it arrived. A stop is sent only where the pet still has a route or a target to drop.
    [Check("pets-wire-stationary-pet-in-reach-sends-no-stops")]
    public static void APlantedPetInReachSendsNoStopOnceItStands()
    {
        foreach (var scenario in new[] { "Ghoul", "Shaco", "Yorick" })
            HoldScene(scenario, (match, pet, target, reach) =>
            {
                var runtime = match.Runtime;
                for (var tick = 0; tick < 60; tick++) runtime.Tick(1.0 / 30.0);
                TestAssert.Equal(0, PetRoutes(match, pet.NetId).Count,
                    scenario + ": a pet already standing in reach of a target that stands still is sent no stop");
                TestAssert.True(pet.Alive && pet.Waypoints.Count == 0 && pet.Target is null, scenario + ": the pet stayed planted");
            });
    }

    // Batch 236 (goal 3): a chase aims where the stock client's AttackTo plan aims, at the target's position (the issue-order routine
    // hands it to the path builder; the planner moves the end off the occupied target), not at a point computed from
    // the pet's own position. While the target stands still every scan asks for the same point, which is not a new request, so the
    // chase is planned once instead of once per 0.15 s scan as the pet's position moved its approach point.
    [Check("pets-wire-chase-of-a-standing-target-is-planned-once")]
    public static void AChaseOfAStandingTargetIsPlannedOnce()
    {
        foreach (var scenario in new[] { "Ghoul", "Shaco", "Annie" })
        {
            using var match = new Match(scenario == "Ghoul" ? "Yorick" : scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            runtime.Tick(10.1);
            owner.Position = new Position2(6450, 7000);
            Entity pet;
            if (scenario == "Ghoul")
                pet = YorickScriptSupport.SpawnUnit(runtime, owner, "Ravenous", new Position2(6700, 7000), runtime.Combat.CurrentTime);
            else
            {
                pet = SpawnRetailFollowPet(match, scenario);
                owner.Position = new Position2(6450, 7000);
                pet.Position = new Position2(6700, 7000);
                pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            }
            var target = PursuitDummy(match, Team.Chaos, new Position2(7350, 7130), "StandingChase");
            // YorickPHPet.lua's TimerFindEnemies looks for a fight around the hero it fights for (see HoldScene).
            if (scenario == "Ghoul") owner.Position = target.Position + new Position2(0.0, -250.0);
            runtime.Delivery.Flush();
            match.Clear();
            if (scenario != "Ghoul") SendOwnedPetOrder(match, pet, 5, target.Position, target.NetId);
            var start = pet.Position;
            for (var tick = 0; tick < 45 && runtime.Pets.PlantedTargetOf(pet) != target.NetId; tick++) runtime.Tick(1.0 / 30.0);
            var routes = PetRoutes(match, pet.NetId).Where(route => route.Length > 1).ToArray();
            TestAssert.True(Math.Sqrt(Math.Pow(pet.Position.X - start.X, 2) + Math.Pow(pet.Position.Z - start.Z, 2)) > 150.0,
                scenario + ": the pet walked toward the target");
            TestAssert.True(routes.Length is >= 1 and <= 2,
                scenario + ": a chase of a target that stands still is planned once, got " + routes.Length + " routes: " +
                string.Join(" / ", routes.Select(route => $"{route[^1].X:F1},{route[^1].Z:F1}")));
        }
    }

    private static List<Position2[]> PetRoutes(Match match, uint netId, int seat = 0) => match.Captures[seat].Packets
        .Where(packet => packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == 0xC1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId)
        .Select(packet =>
        {
            var bytes = packet.Plaintext.Span;
            var route = new Position2[(bytes.Length - 9) / 8];
            for (var index = 0; index < route.Length; index++)
                route[index] = new Position2(BinaryPrimitives.ReadSingleLittleEndian(bytes[(9 + index * 8)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[(13 + index * 8)..]));
            return route;
        }).ToList();

    private static Entity PursuitDummy(Match match, Team team, Position2 at, string name) => match.Runtime.World.Register(
        new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, team, at)
        {
            Name = name, MinionType = MinionType.Melee, Health = 50000, MaxHealth = 50000, Speed = 0, Radius = 35.75,
            Created = true, Visible = true,
        });
}
