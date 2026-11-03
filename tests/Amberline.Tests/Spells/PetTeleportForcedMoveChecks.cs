using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Core;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-b6-stopped-clones-do-not-restore-owner-spacing")]
    public static void StoppedClonesDoNotRestoreOwnerSpacing()
    {
        // PET-03: Pet.lua hard-stop closes to self; park/hold SetTarget without chase.
        // PetState.Order/Parked and the controller buff's payload order are gone with the
        // hand-written machine: the old (order 11 | order 1) x parked matrix collapses to the
        // three states Pet.lua expresses — AI_PET_HARDSTOP (never acquires), AI_PET_HARDIDLE
        // (the park of a hard move that got nowhere) and AI_PET_HOLDPOSITION (ORDER_HOLD),
        // the last two acquiring and attacking in place.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick" })
        foreach (var stance in new[] { "hard-stop", "park", "hold" })
        {
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            Entity clone;
            if (champion == "Shaco") clone = SpawnHallucinateClone(match, owner);
            else
            {
                match.Cast(0, 3, owner);
                runtime.Tick(.3);
                clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            }
            clone.Position = new Position2(7000, 7000);
            var clearance = runtime.Movement.BodyClearance(clone, owner);
            owner.Position = clone.Position + new Position2(clearance + 2, 0);
            TestAssert.True(Distance(clone.Position, owner.Position) > clearance, "no physical body overlap");
            enemy.Position = clone.Position + new Position2(0, 100);
            clone.AttackCooldown = 0;
            clone.Waypoints.Clear();
            clone.Target = null;
            clone.AttackTarget = null;
            SendOwnedPetOrder(match, clone, stance switch
            {
                // PETHARDSTOP -> AI_PET_HARDSTOP, PETHARDMOVE to its own position -> the park,
                // ORDER_HOLD -> AI_PET_HOLDPOSITION.
                "hard-stop" => (byte)11,
                "park" => (byte)6,
                _ => (byte)1,
            }, clone.Position);
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Pets.ScanNow(clone);
            runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
            if (stance == "park")
                TestAssert.True(IsParked(match, clone), "a hard move to its own position parks the clone");
            TestAssert.True(clone.Target is null && clone.Waypoints.Count == 0,
                "stopped clone creates no voluntary owner-spacing route");
            // Batch 279: a stance that publishes a stop seats the clone on its point's lattice anchor at once; it walks nowhere.
            var point = new Position2(7000, 7000);
            TestAssert.True(clone.Position == point || clone.Position == runtime.Movement.ClientAnchor(point),
                $"{stance}: the stopped clone stays on its point ({clone.Position})");
            var mayAttack = stance != "hard-stop";
            TestAssert.Equal(mayAttack ? (uint?)enemy.NetId : null, clone.AttackTarget);
            TestAssert.Equal(mayAttack, runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == clone.NetId && hit.TargetNetId == enemy.NetId),
                "park/hold may attack in place; hard-stop cannot");
        }
    }

    [Check("pets-live-owner-teleport-waits-for-champion-displacement")]
    public static void OwnerTeleportWaitsForChampionPetDisplacement()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "Shaco" })
        {
            using var match = new Match(champion, team, enemyChampion: "Janna");
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var janna = match.Hero(1);
            Entity pet;
            if (champion == "Annie")
            {
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            }
            else pet = SpawnHallucinateClone(match, owner);

            var petStart = new Position2(7000, 7000);
            var jannaStart = new Position2(7125, 7000);
            var ownerStart = new Position2(7500, 8000);
            var ownerDestination = new Position2(7500, 10000);
            foreach (var point in new[] { petStart, jannaStart, ownerStart, ownerDestination })
                TestAssert.True(runtime.Navigation.IsWalkable(point) &&
                    runtime.Navigation.IsMovementWalkable(point),
                    $"{team}/{champion}: setup point {point} is open on shipped Map1 terrain and movement grid");
            owner.Position = ownerStart;
            pet.Position = petStart;
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            janna.Position = jannaStart;
            match.Cast(1, 3, janna);

            var move = AwaitForcedMove(match, pet, "MoveAway") ?? throw new InvalidOperationException(
                $"{team}/{champion}: Janna R did not displace the live pet");
            TestAssert.True(pet.Buffs.Any(buff => buff.Name == "MoveAway"),
                $"{team}/{champion}: champion cast applies its control buff");

            var teleportId = pet.TeleportId;
            owner.Position = ownerDestination;
            var sawDisplacement = false;
            for (var tick = 0; tick < 90 && pet.TeleportId == teleportId; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                if (ReferenceEquals(pet.ForcedMove, move))
                {
                    sawDisplacement = true;
                    TestAssert.Equal(teleportId, pet.TeleportId,
                        $"{team}/{champion}: owner leash cannot override the active champion knockback");
                }
            }

            TestAssert.True(sawDisplacement,
                $"{team}/{champion}: live displacement advanced before leash teleport");
            TestAssert.True(pet.TeleportId > teleportId,
                $"{team}/{champion}: pet reaches its distant owner after displacement cleanup");
            TestAssert.Equal((ForcedMoveState?)null, pet.ForcedMove,
                $"{team}/{champion}: no old knockback can resume after teleport");
            // Restated in batch 254: the teleport carries the owner's position; the one-point path after it seats the actor on that
            // point's 2-unit anchor (the client's path install), and the server's pet stands there at once (batch 263).
            TestAssert.True(pet.Position == owner.Position || pet.Position == runtime.Movement.ClientAnchor(owner.Position),
                $"{team}/{champion}: teleport publishes the owner's authoritative position ({pet.Position})");

            runtime.Tick(.4);
            TestAssert.False(pet.Buffs.Any(buff => buff.Name == "MoveAway"),
                $"{team}/{champion}: champion control buff expires without trapping the relocated pet");
            var delta = pet.Position - owner.Position;
            TestAssert.True(Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z) <= 180.0,
                $"{team}/{champion}: pet remains near its owner after further movement frames");
        }
    }

    // Playtest #14/#23, 25 Sept 2026: Pet.lua's 2000-unit leash blink ends in
    // NetSetState(AI_PET_IDLE), so an ALT-move hold must not survive it.
    [Check("pets-live-leash-teleport-clears-alt-hold")]
    public static void LeashTeleportReturnsHeldPetToFollow()
    {
        var parked = new Position2(7500, 7000);
        var ownerFar = new Position2(7500, 10000);
        var ownerWalk = new Position2(8100, 10000);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick", "Annie" })
        {
            var label = $"{team}/{champion}";
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            foreach (var point in new[] { owner.Position, parked, ownerFar, ownerWalk })
                TestAssert.True(runtime.Navigation.IsWalkable(point) &&
                    runtime.Navigation.IsMovementWalkable(point),
                    $"{label}: setup point {point} is open on shipped Map1 terrain and movement grid");
            Entity pet;
            if (champion == "Shaco") pet = SpawnHallucinateClone(match, owner);
            else if (champion == "Yorick")
            {
                match.Cast(0, 3, owner);
                runtime.Tick(.3);
                pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            }
            else
            {
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            }

            SendOwnedPetOrder(match, pet, 6, parked);
            // Pet.lua TimerScanDistance parks the finished AI_PET_HARDMOVE in AI_PET_HARDIDLE.
            for (var tick = 0; tick < 150 && !IsParked(match, pet); tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(IsParked(match, pet), $"{label}: completed ALT move parks the pet on hold");

            var teleportId = pet.TeleportId;
            owner.Position = ownerFar;
            for (var tick = 0; tick < 30 && pet.TeleportId == teleportId; tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(pet.TeleportId != teleportId,
                $"{label}: owner beyond 2000 units blinks the held pet");
            // Pet.lua's leash blink ends in NetSetState(AI_PET_IDLE), and the controller buff's
            // payload order — the hand-written machine's re-applied hold — is gone with it.
            TestAssert.Equal(PetLuaAiEngine.StatePetIdle, runtime.Pets.StateOf(pet),
                $"{label}: leash blink returns the pet to normal idle");

            match.Move(0, ownerWalk);
            for (var tick = 0; tick < 120; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(Distance(owner.Position, ownerWalk) < 15,
                $"{label}: owner finishes the follow-up walk; owner={owner.Position}");
            // owner decision 28 Sept 2026: original game values.
            var reach = 200.0;
            TestAssert.True(Distance(pet.Position, owner.Position) <= reach,
                $"{label}: blinked pet follows the moving owner; pet={pet.Position}, " +
                $"owner={owner.Position}, state={runtime.Pets.StateOf(pet)}, wp={pet.Waypoints.Count}");
            TestAssert.False(UnderHardOrder(match, pet), $"{label}: follow does not fall back into hold");
        }
    }

    // Playtest B3 #7, 26 Sept 2026: Pet.lua parks a pet after an ALT move
    // (AI_PET_HARDIDLE) and ignores the owner's ordinary clicks there. An ALT
    // recall (AI_PET_HARDRETURN) becomes AI_PET_IDLE inside the return radius,
    // so the pet attacks and follows again. In a normal state an owner move
    // more than 1000 units from the pet makes it drop its target and follow.
    [Check("pets-live-hard-return-resumes-normal-ai")]
    public static void HardReturnResumesNormalPetAi()
    {
        var parked = new Position2(7500, 7000);
        var ownerFar = new Position2(7500, 10000);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick", "Annie" })
        {
            var label = $"{team}/{champion}";
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            foreach (var point in new[] { owner.Position, parked, ownerFar })
                TestAssert.True(runtime.Navigation.IsWalkable(point) &&
                    runtime.Navigation.IsMovementWalkable(point),
                    $"{label}: setup point {point} is open on shipped Map1 terrain and movement grid");
            Entity pet;
            if (champion == "Shaco") pet = SpawnHallucinateClone(match, owner);
            else if (champion == "Yorick")
            {
                match.Cast(0, 3, owner);
                runtime.Tick(.3);
                pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            }
            else
            {
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            }

            SendOwnedPetOrder(match, pet, 6, parked);
            // Pet.lua TimerScanDistance parks the finished AI_PET_HARDMOVE in AI_PET_HARDIDLE.
            for (var tick = 0; tick < 150 && !IsParked(match, pet); tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(IsParked(match, pet), $"{label}: completed ALT move parks the pet");
            // Pet.lua's hard states swallow an ordinary MOVETO and answer true; the hand-written
            // machine rejected the far click at the wire.
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(0, 1500)),
                $"{label}: a parked pet swallows the owner's far ordinary click");
            TestAssert.True(IsParked(match, pet), $"{label}: the owner's click leaves the park intact");

            enemy.Position = owner.Position + new Position2(0, 150);
            SendOwnedPetOrder(match, pet, 9, owner.Position);
            match.Clear();
            // owner decision 28 Sept 2026: original game values.
            var returnRadius = 200.0;
            var resumed = false;
            for (var tick = 0; tick < 150 && !resumed; tick++)
            {
                var returning = runtime.Pets.StateOf(pet) == PetLuaAiEngine.StatePetHardReturn;
                runtime.Tick(1.0 / 30.0);
                if (runtime.Pets.StateOf(pet) == PetLuaAiEngine.StatePetHardReturn)
                    TestAssert.Equal((uint?)null, pet.AttackTarget,
                        $"{label}: the hard return does not stop to fight");
                else if (returning)
                    TestAssert.True(Distance(pet.Position, owner.Position) <= returnRadius + 30.0,
                        $"{label}: the return ends inside the return radius; pet={pet.Position}");
                // Pet.lua ends AI_PET_HARDRETURN in AI_PET_IDLE inside the return radius, and the
                // next TimerFindEnemies sends the normal pet at the enemy beside its owner.
                resumed = !UnderHardOrder(match, pet) && pet.AttackTarget == enemy.NetId &&
                    CountPetBasicAttacks(match, pet.NetId, enemy.NetId) > 0;
            }
            TestAssert.True(resumed,
                $"{label}: the returned pet attacks the enemy beside its owner; state={runtime.Pets.StateOf(pet)}, " +
                $"target={pet.AttackTarget}, pet={pet.Position}, owner={owner.Position}");

            // Pet.lua OnOrder swallows a near MOVETO and answers true whatever the pet is doing;
            // the hand-written machine rejected the near click at the wire.
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(300, 0)),
                $"{label}: a near owner click leaves the fighting pet alone");
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, $"{label}: the near click keeps the target");
            match.Move(0, ownerFar);
            TestAssert.True(SendPetInput(match, pet, 2, ownerFar),
                $"{label}: the owner's far click reaches the normal pet");
            TestAssert.Equal(PetLuaAiEngine.StatePetMove, runtime.Pets.StateOf(pet),
                $"{label}: the pet moves to its owner");
            TestAssert.Equal((uint?)null, pet.AttackTarget, $"{label}: the far click drops the fight");
            for (var tick = 0; tick < 15; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                TestAssert.Equal((uint?)null, pet.AttackTarget,
                    $"{label}: no acquisition while the owner walks away");
            }
            // SendPetInput sends any owned actor's order; this is the hero's stop.
            TestAssert.True(SendPetInput(match, owner, 10, owner.Position), $"{label}: the owner stops");
            // Pet.lua TimerScanDistance ends AI_PET_MOVE once the owner stops moving.
            for (var tick = 0; tick < 10 && runtime.Pets.StateOf(pet) == PetLuaAiEngine.StatePetMove; tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(runtime.Pets.StateOf(pet) != PetLuaAiEngine.StatePetMove,
                $"{label}: the pet resumes normal AI once the owner stops");
        }
    }

    // Playtest B3 #7, 26 Sept 2026 (owner: retail even over reviewed replays):
    // Pet.lua's 0.15 s timers only pick targets and routes; the native
    // auto-attack keeps swinging at the current in-range target every step.
    [Check("pets-live-pet-lua-scans-and-native-swings")]
    public static void PetLuaScansPickTargetsWhileNativeSwingsRepeat()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var label = team.ToString();
            using (var match = new Match("Annie", team))
            {
                var runtime = match.Runtime;
                var owner = match.Hero(0);
                var enemy = match.Hero(1);
                enemy.Position = new Position2(12000, 12000);
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                var tibbers = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);

                TestAssert.Equal(80.0, tibbers.CombatRadius, "Tibbers carries its shipped gameplay radius");
                var start = runtime.Combat.CurrentTime;
                runtime.Pets.ScanNow(tibbers);
                runtime.Pets.Tick(start, 0.0);
                TestAssert.Equal((uint?)null, tibbers.AttackTarget, $"{label}: no enemy at the first scan");
                enemy.Position = tibbers.Position + new Position2(300, 0);
                runtime.Pets.Tick(start + 0.1, 0.0);
                TestAssert.Equal((uint?)null, tibbers.AttackTarget,
                    $"{label}: Tibbers picks no target 0.1 s after the previous scan");
                runtime.Pets.Tick(start + 0.16, 0.0);
                TestAssert.Equal((uint?)enemy.NetId, tibbers.AttackTarget,
                    $"{label}: the next 0.15 s scan acquires the enemy");

                // Between scans (next at +0.31) the in-range swing still starts.
                enemy.Position = tibbers.Position +
                    // owner decision 27 Sept 2026: edge-to-edge gameplay radii
                    new Position2(CombatService.AttackReach(tibbers, enemy) - 13.0, 0);
                tibbers.Waypoints.Clear();
                tibbers.Target = null;
                runtime.Combat.CancelPendingWindups(tibbers.NetId);
                tibbers.AttackCooldown = 0.0;
                runtime.Pets.Tick(start + 0.2, 0.0);
                TestAssert.True(runtime.Combat.PendingHits.Any(hit =>
                        hit.SourceNetId == tibbers.NetId && hit.TargetNetId == enemy.NetId),
                    $"{label}: Tibbers' native auto-attack swings between scans");
            }

            using (var match = new Match("Shaco", team))
            {
                var runtime = match.Runtime;
                var owner = match.Hero(0);
                var enemy = match.Hero(1);
                enemy.Position = new Position2(12000, 12000);
                var clone = SpawnHallucinateClone(match, owner);
                // The host creates a unit's Lua instance on its first step of it (spawn services have
                // no handle on it), so that step is the one Pet.lua OnInit lands in — the step under
                // test below is then due no timer and runs the native swing half alone.
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0.0);
                clone.Waypoints.Clear();
                clone.Target = null;
                enemy.Position = clone.Position +
                    // owner decision 27 Sept 2026: edge-to-edge gameplay radii
                    new Position2(CombatService.AttackReach(clone, enemy) - 13.0, 0);
                clone.AttackTarget = enemy.NetId;
                clone.AttackCooldown = 0.0;
                runtime.Combat.CancelPendingWindups(clone.NetId);
                var now = runtime.Combat.CurrentTime;
                var state = runtime.Pets.StateOf(clone);
                runtime.Pets.Tick(now, 0.0);
                TestAssert.True(runtime.Combat.PendingHits.Any(hit =>
                        hit.SourceNetId == clone.NetId && hit.TargetNetId == enemy.NetId),
                    $"{label}: the clone's native auto-attack swings between scans");
                // The controller buff's NextAi scan clock is gone with the hand-written machine:
                // this step ran the engine's native swing half alone and moved no AI state.
                TestAssert.Equal(state, runtime.Pets.StateOf(clone),
                    $"{label}: the swing runs no Lua scan");
            }
        }
    }

    // Playtest B3 #7, 26 Sept 2026 (owner: as close to retail as possible).
    // Pet.lua OnOrder ORDER_ATTACKTO sends a normal pet after the owner's
    // target at any distance; AI_PET_HARDIDLE, the park after an ALT move,
    // attacks in range without moving; AI_PET_HARDSTOP, the ALT hold, never
    // acquires.
    [Check("pets-live-copied-attack-park-and-hold")]
    public static void CopiedAttackParkAndHoldFollowPetLua()
    {
        var home = new Position2(7000, 7000);
        var far = new Position2(7500, 7000);
        var park = new Position2(7250, 7000);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Yorick", "Annie" })
        {
            var label = $"{team}/{champion}";
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            foreach (var point in new[] { home, far, park })
                TestAssert.True(runtime.Navigation.IsWalkable(point) &&
                    runtime.Navigation.IsMovementWalkable(point),
                    $"{label}: setup point {point} is open on shipped Map1 terrain and movement grid");
            Entity pet;
            if (champion == "Shaco") pet = SpawnHallucinateClone(match, owner);
            else if (champion == "Yorick")
            {
                match.Cast(0, 3, owner);
                runtime.Tick(.3);
                pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            }
            else
            {
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            }

            // The owner's attack beyond the pet's acquisition range.
            owner.Position = home;
            pet.Position = home + new Position2(-150, 0);
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            enemy.Position = far;
            runtime.Delivery.Flush();
            var acquisition = champion == "Annie" ? GameData.Default.Champion("AnnieTibbers").AcquisitionRange : pet.AcquisitionRange;
            TestAssert.True(Distance(pet.Position, enemy.Position) > acquisition,
                $"{label}: the owner's target starts outside the pet's own acquisition");
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                $"{label}: the owner sees his target");
            TestAssert.True(SendPetInput(match, pet, 3, enemy.Position, enemy.NetId),
                $"{label}: the owner's mirrored attack reaches the normal pet");
            TestAssert.Equal(PetLuaAiEngine.StatePetAttack, runtime.Pets.StateOf(pet), $"{label}: AI_PET_ATTACK");
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, $"{label}: the pet takes the owner's target");
            match.Clear();
            var hit = false;
            for (var tick = 0; tick < 120 && !hit; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                hit = CountPetBasicAttacks(match, pet.NetId, enemy.NetId) > 0;
            }
            TestAssert.True(hit && Distance(pet.Position, enemy.Position) < 400.0,
                $"{label}: the pet walks onto the owner's target and attacks it; pet={pet.Position}");

            // The ALT-move park attacks an enemy in range without moving.
            SendOwnedPetOrder(match, pet, 6, park);
            for (var tick = 0; tick < 150 && !IsParked(match, pet); tick++)
                runtime.Tick(1.0 / 30.0);
            // Pet.lua TimerScanDistance parks the finished AI_PET_HARDMOVE in AI_PET_HARDIDLE.
            TestAssert.True(IsParked(match, pet),
                $"{label}: the finished ALT move parks the pet in hard idle");
            var parkedAt = pet.Position;
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            enemy.Position = parkedAt + new Position2(CombatService.AttackReach(pet, enemy) - 13.0, 0);
            runtime.Delivery.Flush();
            match.Clear();
            hit = false;
            for (var tick = 0; tick < 60 && !hit; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                hit = CountPetBasicAttacks(match, pet.NetId, enemy.NetId) > 0;
            }
            TestAssert.True(hit, $"{label}: the parked pet attacks the enemy in range");
            TestAssert.True(Distance(pet.Position, parkedAt) < 5.0 && IsParked(match, pet),
                $"{label}: the parked pet attacks without leaving its park; pet={pet.Position}");

            // The explicit ALT hold never acquires, even with an enemy in range.
            SendOwnedPetOrder(match, pet, 11, pet.Position);
            // ORDER_PETHARDSTOP is AI_PET_HARDSTOP in Pet.lua's OnOrder: hard stop, not the park.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, runtime.Pets.StateOf(pet),
                $"{label}: the ALT hold is hard stop, not the park");
            match.Clear();
            for (var tick = 0; tick < 30; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                TestAssert.Equal((uint?)null, pet.AttackTarget, $"{label}: the held pet takes no target");
            }
            TestAssert.Equal(0, CountPetBasicAttacks(match, pet.NetId, enemy.NetId),
                $"{label}: the held pet does not attack the enemy in range");
            TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, runtime.Pets.StateOf(pet), $"{label}: the hold remains");
        }
    }
}
