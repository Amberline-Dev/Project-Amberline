using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-b7-retail-idle-follow-radius-destination-and-cadence")]
    public static void RetailPetIdleFollow()
    {
        // owner decision 28 Sept 2026: original game values.
        // Pet.lua TimerScanDistance 0044-0076: > GetPetReturnRadius starts
        // CloseToTarget(owner); <= radius returns to idle. OnInit: .15 s.
        // The stock follow-radius field defaults to 200.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, team);
            var pet = SpawnRetailFollowPet(match, champion);
            var owner = match.Hero(0);
            owner.Position = new Position2(7000, 7000);
            pet.Position = owner.Position + new Position2(200, 0);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, 0);
            TestAssert.False(RetailPetDestinations(match, pet).ContainsKey(pet.NetId),
                champion + ": exactly 200 does not start a follow route");

            pet.Position += new Position2(1, 0);
            TickRetailPet(match, pet, .149);
            TestAssert.False(RetailPetDestinations(match, pet).ContainsKey(pet.NetId),
                "no follow decision before the next .15 s scan");
            TickRetailPet(match, pet, .15);
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                "201 starts immediately at the scan, aimed at the owner, without a delay or ring");
            TestAssert.True(pet.Waypoints.Count > 0, "the real body-aware planner accepts the follow");

            var oldOwner = owner.Position;
            owner.Position += new Position2(0, 40);
            TickRetailPet(match, pet, .299);
            // Batch 229 follow-up (coordinator, 25 October 2026): the follow is re-issued on the script's 0.15 s scan, not on every step,
            // so the owner's 40-unit shift waits for the scan at .30 (the next assertion).
            TestAssert.Equal(oldOwner, RetailPetDestinations(match, pet)[pet.NetId],
                "the follow is not re-pathed between scans: the owner's shift waits for the scan at .30");
            TickRetailPet(match, pet, .30);
            // Restated in batch 229 (owner, 25 October 2026: retail re-paths whenever its script or native state asks, no drift gate):
            // the next scan re-paths the follow to the owner's new point.
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                "an owner shift of 40 re-paths the follow on the next scan");
            owner.Position += new Position2(0, 200);
            TickRetailPet(match, pet, .451);
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                "a route end 240 from the owner is past the gate: the scan re-paths to the owner");

            pet.Position = owner.Position + new Position2(200, 0);
            TickRetailPet(match, pet, .61);
            TestAssert.True(pet.Waypoints.Count == 0 && pet.Target is null,
                "return ends at 200, without walking to an invented inner band");
            var rest = pet.Position;
            TickRetailPet(match, pet, .77);
            TestAssert.Equal(rest, pet.Position);
            TestAssert.False(RetailPetDestinations(match, pet).ContainsKey(pet.NetId));

            pet.Position = owner.Position + new Position2(1999, 0);
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, .91);
            TestAssert.True(pet.Position != owner.Position,
                champion + ": below 2000 the pet returns without teleporting");
            // Pet.luaobj proto 0.10 is TELEPORT_DISTANCE < distance: exactly 2000 does not teleport (A2 audit).
            pet.Position = owner.Position + new Position2(2000, 0);
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, 1.06);
            TestAssert.True(pet.Position != owner.Position,
                champion + ": exactly 2000 does not teleport (strict TELEPORT_DISTANCE < distance)");
            pet.Position = owner.Position + new Position2(2001, 0);
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, 1.21);
            // Restated in batch 254: the blink publishes a one-point path, on whose 2-unit anchor the client's path install seats
            // the actor, and the server's next order or stop seats the pet there too.
            TestAssert.True(pet.Position == owner.Position || pet.Position == match.Runtime.Movement.ClientAnchor(owner.Position),
                champion + $": beyond 2000 teleports to the owner on the scan ({pet.Position})");
            TestAssert.Equal(PetLuaAiEngine.StatePetIdle, match.Runtime.Pets.StateOf(pet)!.Value,
                "leash teleport returns the pet to idle (Pet.lua NetSetState(AI_PET_IDLE) after the blink)");

            var data = GameData.Default.Champion(champion == "Annie" ? "AnnieTibbers" : champion);
            TestAssert.False(data.Raw.ContainsKey(InibinReader.KeyHash("Data*PetReturnRadius")),
                "shipped character does not override the native return radius");
        }
    }

    [Check("pets-b7-retail-return-and-far-owner-orders")]
    public static void RetailPetReturnAndFarOrders()
    {
        // owner decision 28 Sept 2026: original game values.
        // Pet.lua OnOrder: hard return closes immediately; far MOVETO is
        // strictly >1000 from pet to click, and MOVE ignores the return radius.
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, Team.Order);
            var pet = SpawnRetailFollowPet(match, champion);
            var owner = match.Hero(0); var enemy = match.Hero(1);
            owner.Position = new Position2(7000, 7000);
            pet.Position = owner.Position + new Position2(250, 0);
            pet.Waypoints.Clear(); pet.Target = null; pet.AttackTarget = null;
            TestAssert.True(SendPetInput(match, pet, 9, owner.Position));
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                "ALT return starts at command time, before the next scan");
            enemy.Position = pet.Position + new Position2(0, 100);
            ResetRetailPetScan(match, pet);
            TickRetailPet(match, pet, 0);
            TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, match.Runtime.Pets.StateOf(pet)!.Value,
                "250 is still hard return, including Tibbers");
            TestAssert.Equal((uint?)null, pet.AttackTarget, "hard return does not acquire");
            pet.Position = owner.Position + new Position2(200, 0);
            TickRetailPet(match, pet, .15);
            // Pet.lua's two 0.15 s timers run in one step and in InitTimer order: TimerScanDistance
            // ends the hard return at the radius (NetSetState AI_PET_IDLE), then TimerFindEnemies
            // takes the enemy from the ordinary acquisition.
            TestAssert.Equal(PetLuaAiEngine.StatePetAttack, match.Runtime.Pets.StateOf(pet)!.Value,
                "exactly 200 resumes ordinary acquisition");
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget);

            // Pet.lua OnOrder answers true and changes nothing for a near MOVETO (strictly inside
            // FAR_MOVMEMENT_DISTANCE); the hand-written machine rejected the click at the wire.
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(1000, 0)),
                "exactly 1000 is a near move the script swallows");
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, "near move retains combat");
            owner.Waypoints.Add(owner.Position + new Position2(500, 0));
            owner.Target = owner.Waypoints[^1];
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(1001, 0)));
            TestAssert.Equal(PetLuaAiEngine.StatePetMove, match.Runtime.Pets.StateOf(pet)!.Value);
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId],
                "far move closes to owner even at the 200 return boundary");
            TickRetailPet(match, pet, .31);
            TestAssert.Equal((uint?)null, pet.AttackTarget, "MOVE does not acquire while owner walks");

            TestAssert.True(SendPetInput(match, pet, 7, owner.Waypoints[^1]));
            TestAssert.Equal(PetLuaAiEngine.StatePetAttackMove, match.Runtime.Pets.StateOf(pet)!.Value,
                "retain the attack-move state, rather than idle");
            TickRetailPet(match, pet, .47);
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, "ATTACKMOVE can acquire while closing");
            enemy.Position = new Position2(12000, 12000);
            TestAssert.True(SendPetInput(match, pet, 1, pet.Position));
            TestAssert.True(SendPetInput(match, pet, 2, pet.Position + new Position2(100, 0)),
                "a near move also leaves HOLDPOSITION and closes to the owner");
            TestAssert.Equal(owner.Position, RetailPetDestinations(match, pet)[pet.NetId]);
            owner.Waypoints.Clear(); owner.Target = null;
            TickRetailPet(match, pet, .63);
            TestAssert.Equal(PetLuaAiEngine.StatePetIdle, match.Runtime.Pets.StateOf(pet)!.Value,
                "MOVE ends when owner stops");
        }
    }

    [Check("pets-b7-ghoul-script-follow-radius-destination-and-cadence")]
    public static void RetailGhoulFollow()
    {
        // owner decision 28 Sept 2026: original game values.
        // YorickPHPet OnInit and TimerScanDistance 0074-0109 use .15 s,
        // GetPetReturnRadius and CloseToTarget(GetGoldRedirectTarget()).
        foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime; var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            owner.Position = new Position2(7000, 7000);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime, owner, kind, owner.Position + new Position2(200, 0), 0);
            // The host arms a unit's script timers one interval after the step that first observed
            // it, so observing the ghoul at its spawn time puts YorickPHPet's 0.15 s grid on the
            // spawn: the first TimerScanDistance runs at .15.
            runtime.Pets.Tick(0, 0);
            runtime.Pets.Tick(.15, 0);
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                "exactly 200 remains idle");
            ghoul.Position = owner.Position + new Position2(201, 0);
            runtime.Pets.Tick(.299, 0);
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId));
            runtime.Pets.Tick(.30, 0);
            TestAssert.Equal(owner.Position, runtime.Pets.LastDestinations[ghoul.NetId],
                kind + ": immediate .15 s follow, no Tibbers bands or .4 s delay");
            owner.Position += new Position2(0, 40);
            runtime.Pets.Tick(.45, 0);
            // Restated in batch 229 (owner, 25 October 2026): no drift gate, the ghoul re-paths to the owner's new point on the scan.
            TestAssert.Equal(owner.Position, runtime.Pets.LastDestinations[ghoul.NetId],
                "an owner shift of 40 re-paths the ghoul's follow on the next scan");
            owner.Position += new Position2(0, 200);
            runtime.Pets.Tick(.60, 0);
            TestAssert.Equal(owner.Position, runtime.Pets.LastDestinations[ghoul.NetId],
                "a route end 240 from the owner is past the gate: the scan re-paths");
            ghoul.Position = owner.Position + new Position2(200, 0);
            runtime.Pets.Tick(.75, 0);
            TestAssert.True(ghoul.Waypoints.Count == 0 && ghoul.Target is null);
            TestAssert.False(GameData.Default.Champion("Yorick" + kind + "Ghoul").Raw
                .ContainsKey(InibinReader.KeyHash("Data*PetReturnRadius")));
        }
    }

    private static Entity SpawnRetailFollowPet(Match match, string champion)
    {
        var owner = match.Hero(0);
        match.Hero(1).Position = new Position2(12000, 12000);
        Entity pet;
        if (champion == "Shaco") pet = SpawnHallucinateClone(match, owner);
        else
        {
            if (champion == "Annie") match.CastGround(0, owner.Position, 3);
            else match.Cast(0, 3, owner);
            match.Runtime.Tick(.5);
            pet = match.Runtime.World.Entities.Values.Single(unit => unit.Alive &&
                unit.OwnerNetId == owner.NetId &&
                (unit.PetState?.Kind is CloneKind.Tibbers or CloneKind.YorickRevenant));
        }
        // Pet.lua OnInit leaves the pet AI_PET_IDLE and autonomous, so there is no spawn-time order
        // to clear; what the spawn leaves behind is the path and any target, which the stand-down
        // drops. Before: an ORDER_PETHARDSTOP through the wire and PetState.Order = 0. That park is
        // a hard state Pet.lua never leaves — its follow rules run from AI_PET_IDLE only.
        match.Runtime.Pets.Engine.SetTarget(pet, null);
        match.Runtime.Pets.Engine.Stop(pet);
        return pet;
    }

    // The scripts' 0.15 s timers run on the host clock, whose phase the production driver has already
    // advanced past this check's own time axis. ScanNow re-bases that phase so the next step is the
    // scan step (the hand-written machines' zeroed next scan), and the ticks below are one scan step
    // each on the caller's axis: 0, .149, .15, .299, .30 ... pin the cadence itself.
    private static void ResetRetailPetScan(Match match, Entity pet) => match.Runtime.Pets.ScanNow(pet);

    private static void TickRetailPet(Match match, Entity pet, double now) =>
        match.Runtime.Pets.Tick(now, 0);

    private static IReadOnlyDictionary<uint, Position2> RetailPetDestinations(Match match, Entity pet) =>
        match.Runtime.Pets.LastDestinations;

    /// <summary>
    /// Whether a pet's script left it under a hard order. Before batch 146 this was
    /// <c>PetState.Order != 0</c>; the hard orders are what Pet.lua's OnOrder turns into the pet
    /// hard states, and the ordinary owner orders never do.
    /// </summary>
    private static bool UnderHardOrder(Match match, Entity pet) =>
        match.Runtime.Pets.StateOf(pet) is { } state && PetLuaAiEngine.IsHardState(state);

    /// <summary>
    /// Whether a pet stands in Pet.lua's park: <c>AI_PET_HARDIDLE</c>, or
    /// <c>AI_PET_HARDIDLE_ATTACKING</c> where a target is already in range. Before batch 146 this
    /// was <c>PetState.Parked</c>.
    /// </summary>
    private static bool IsParked(Match match, Entity pet) =>
        match.Runtime.Pets.StateOf(pet) is PetLuaAiEngine.StatePetHardIdle
            or PetLuaAiEngine.StatePetHardIdleAttacking;
}
