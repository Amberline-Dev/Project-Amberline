using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (16 to 17 October 2026) pet fixes from the pet audit: the 0xC8 aggro outline of engaged pets, the follow
/// route cadence, Jack in the Box's 0.5 s shot grid and the ghouls' building scan flags.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-wire-aggro-outline-engaged-pets")]
    public static void EngagedPetsSendTheAggroOutlineOnceAndClearItOnTargetLoss()
    {
        // Reconstruction: the client's CollectAggroUnits has no unit-type filter (memory aggro-outline-0xc8), so
        // Tibbers, Shaco's clone and Yorick's ghouls get the 0xC8 outline like an engaged lane minion.
        foreach (var scenario in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            runtime.Tick(10.1);
            enemy.Position = new Position2(12000, 12000);
            var pet = SpawnPet(scenario, match, owner);
            runtime.Delivery.Flush();
            match.Clear();
            TestAssert.Equal((uint?)null, pet.AttackTarget, scenario + ": the pet starts without a target");

            enemy.Position = pet.Position + new Position2(300, 0);
            for (var tick = 0; tick < 40 && pet.AttackTarget != enemy.NetId; tick++) runtime.Tick(0.05);
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, scenario + ": the pet engages the hero");
            runtime.Tick(0.3);
            TestAssert.Equal(new[] { enemy.NetId }, PetAggroOutlines(match, pet.NetId),
                scenario + ": one outline while the engagement lasts, not one per scan");

            enemy.Protected = true;
            for (var tick = 0; tick < 40 && pet.AttackTarget is not null; tick++) runtime.Tick(0.05);
            TestAssert.Equal((uint?)null, pet.AttackTarget, scenario + ": the pet loses the protected hero");
            runtime.Tick(0.3);
            TestAssert.Equal(new[] { enemy.NetId, 0u }, PetAggroOutlines(match, pet.NetId),
                scenario + ": the outline is cleared once on target loss");
        }
    }

    [Check("pets-wire-box-sends-no-aggro-outline")]
    public static void JackInTheBoxNeverSendsAnAggroOutline()
    {
        // The box only casts ShacoBoxSpell; it does not auto-attack, so it has no aggro edge to publish.
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(12000, 12000);
        match.CastGround(0, shaco.Position + new Position2(0, 100), 1);
        for (var tick = 0; tick < 30 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.ShacoBox); tick++)
            runtime.Tick(1.0 / 30.0);
        var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
        match.Clear();
        runtime.Tick(2.2);
        enemy.Position = box.Position + new Position2(200, 0);
        runtime.Delivery.Flush();
        for (var tick = 0; tick < 45; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(ShacoScriptSupport.IsAwake(box), "the box woke and shot");
        TestAssert.Equal(0, PetAggroOutlines(match, box.NetId).Length, "the box sends no 0xC8 outline");
    }

    // Restated in batch 229 (owner, 25 October 2026: retail re-paths whenever its script or native state asks, no drift gate): a
    // following pet's route ends at its owner on every scan of his walk. Renamed from pets-wire-follow-repath-gated-by-drift.
    [Check("pets-wire-follow-repaths-as-the-owner-walks")]
    public static void FollowingPetsRepathToTheOwnerOnEveryScanOfHisWalk()
    {
        foreach (var scenario in new[] { "Annie", "Shaco", "Yorick", "Ghoul" })
        {
            using var match = new Match(scenario == "Ghoul" ? "Yorick" : scenario, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            Entity pet;
            Action<double> tick;
            Func<Position2?> destination;
            // Batch 146: the walk starts 400 back along the open mid lane from map centre. YorickPHPet's
            // TimerFindEnemies takes a structure with nothing else in range (the acquisition ladder's
            // last resort), so a master who walks the follow path toward the Chaos mid outer tower hands
            // his pet a fight instead of a follow route - Turret_T2_C_05 at (8548.8, 8289.5) comes
            // inside the Spectral ghoul's 600 around the master on the last scan from map centre.
            // The walk is a follow-only stretch of lane: 28 scans stay clear of every enemy unit.
            var walkStart = new Position2(6600, 6600);
            if (scenario == "Ghoul")
            {
                owner.Position = walkStart;
                pet = YorickScriptSupport.SpawnUnit(runtime,owner, "Spectral", owner.Position + new Position2(-283, -283), 0);
                tick = now => runtime.Pets.Tick(now, 0);
                destination = () => runtime.Pets.LastDestinations.TryGetValue(pet.NetId, out var point) ? point : null;
            }
            else
            {
                pet = SpawnRetailFollowPet(match, scenario);
                // Hallucinate relocates Shaco at random, so place the owner only after the spawn. 400 behind, on the
                // open mid lane.
                owner.Position = walkStart;
                pet.Position = owner.Position + new Position2(-283, -283);
                pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
                ResetRetailPetScan(match, pet);
                tick = now => TickRetailPet(match, pet, now);
                destination = () => RetailPetDestinations(match, pet).TryGetValue(pet.NetId, out var point) ? point : null;
            }
            // The owner walks away 52.5 u per scan (350 u/s at 0.15 s); the pet itself never moves (dt = 0). The scans
            // are 0.16 s apart so floating-point drift in the 0.15 s timers cannot skip one. The ghoul's first
            // distance scan is at 0.15 s, the others' at once. 28 scans keep the pet under the 2000 u leash.
            const int scans = 28;
            var routes = new List<Position2>();
            for (var scan = 0; scan <= scans; scan++)
            {
                if (scan > 0) owner.Position += new Position2(52.5 / Math.Sqrt(2.0), 52.5 / Math.Sqrt(2.0));
                tick(0.16 * scan);
                if (destination() is not { } current)
                {
                    TestAssert.True(scenario == "Ghoul" && scan == 0, scenario + ": the follow route vanished");
                    continue;
                }
                if (routes.Count == 0 || current != routes[^1]) routes.Add(current);
                var drift = Math.Sqrt(Math.Pow(current.X - owner.Position.X, 2) + Math.Pow(current.Z - owner.Position.Z, 2));
                TestAssert.True(drift <= 1.0,
                    $"{scenario}: the route ends at the owner on every scan (scan {scan}: {drift:F1} off)");
            }
            // Every scan re-paths as the owner walks: a 52.5 u step moves the destination on each scan (retail re-paths on ask).
            TestAssert.True(routes.Count >= 28,
                $"{scenario}: {scans} scans of a walking owner issue a route on each scan, got {routes.Count}");
        }
    }

    [Check("pets-wire-follow-state-entry-repaths")]
    public static void AMirroredOwnerMoveRepathsEvenInsideTheDriftGate()
    {
        // The drift gate holds a following pet's route; a new state (the owner's mirrored MOVE order) always starts one.
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, Team.Order);
            var owner = match.Hero(0);
            var pet = SpawnRetailFollowPet(match, champion);
            owner.Position = new Position2(7000, 7000); // after the spawn: Hallucinate relocates Shaco at random
            pet.Position = owner.Position + new Position2(-283, -283);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, 0);
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId], champion + ": follow route");

            owner.Position += new Position2(70, 70); // about 99 u: inside the gate
            owner.Waypoints.Add(owner.Position + new Position2(500, 0));
            owner.Target = owner.Waypoints[^1];
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(1001, 0)), champion + ": MOVE accepted");
            // The mirrored MOVE leaves the pet in AI_PET_MOVE (Pet.lua OnOrder's far MOVETO); the
            // old check read PetState.Order = ORDER_MOVETO, the wire id the script maps from.
            TestAssert.Equal(PetLuaAiEngine.StatePetMove, match.Runtime.Pets.StateOf(pet)!.Value);
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                champion + ": state entry re-paths to the owner's present position");
        }
    }

    [Check("pets-live-shaco-box-shots-on-spawn-grid")]
    public static void BoxShotsFallOnTheHalfSecondGridFromSpawn()
    {
        // JackInTheBox.luaobj: BBExecutePeriodically(0.5, ExecuteImmediately = false) is a sibling of the
        // LifeTime >= 2 wake block, so its ticks fall at spawn + k * 0.5 whether the box was asleep or not.
        const double Step = 1.0 / 30.0;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var wakeOffset in new[] { 3.2, 4.37 })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            match.CastGround(0, shaco.Position + new Position2(0, 100), 1);
            for (var tick = 0; tick < 60 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.ShacoBox); tick++)
                runtime.Tick(Step);
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            var born = ShacoScriptSupport.Born(runtime, box);
            while (runtime.Combat.CurrentTime < born + wakeOffset) runtime.Tick(Step);
            TestAssert.False(ShacoScriptSupport.IsAwake(box),
                $"{team}: the box is asleep before the enemy arrives");

            enemy.Position = box.Position + new Position2(200, 0);
            runtime.Delivery.Flush();
            var woke = runtime.Combat.CurrentTime;
            double? shot = null;
            for (var tick = 0; tick < 60 && shot is null; tick++)
            {
                runtime.Tick(Step);
                if (runtime.Spells.SharedProjectiles.Any(projectile => projectile.Snapshot.SpellName == "ShacoBoxSpell"))
                    shot = runtime.Combat.CurrentTime;
            }
            TestAssert.True(shot is not null, $"{team}: the woken box shoots");
            var phase = (shot!.Value - born) % 0.5;
            TestAssert.True(phase < 2.0 * Step || 0.5 - phase < 1e-6,
                $"{team}/{wakeOffset}: the first shot ({shot - born:F3} s after spawn) lands on the 0.5 s grid, phase {phase:F3}");
            TestAssert.True(shot.Value - woke <= 0.5 + 2.0 * Step,
                $"{team}/{wakeOffset}: it waits for the next grid tick, at most one period");
        }
    }

    private static uint[] PetAggroOutlines(Match match, uint petNetId) => match.Captures[0].Packets
        .Where(packet => packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == 0xC8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == petNetId)
        .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..])).ToArray();
}
