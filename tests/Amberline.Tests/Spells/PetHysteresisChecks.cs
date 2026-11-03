using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (17 October 2026): a pet that stopped to attack stays planted while its target is beyond reach by no more than
/// one client hop (Reconstruction of Pet.lua's cancel-attack range), instead of alternating stop and route at the edge of
/// reach. Every check reads the 0xC1 packets an observer was delivered.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-wire-plant-hold-no-alternation-in-band")]
    public static void APlantedPetDoesNotAlternateStopAndRouteWhileTheTargetSitsInTheBand()
    {
        foreach (var scenario in new[] { "Ghoul", "Shaco", "Yorick" })
            HoldScene(scenario, (match, pet, target, reach) =>
            {
                var runtime = match.Runtime;
                // The target steps in and out of reach, never past the band: no route. Restated in batch 229 (owner, 25 October 2026):
                // the stops a planted pet is asked for in reach go out as retail sends them, so the band is judged by its routes.
                for (var tick = 0; tick < 45; tick++)
                {
                    if (tick % 3 == 0)
                        target.Position = pet.Position + new Position2(tick % 6 == 0 ? reach - 10.0 : reach + 20.0, 0);
                    runtime.Tick(1.0 / 30.0);
                }
                TestAssert.Equal(0, PetRoutes(match, pet.NetId).Count(route => route.Length > 1),
                    scenario + ": a target flickering across the edge of reach, inside the band, draws no route");
                TestAssert.True(pet.Alive && pet.Waypoints.Count == 0 && pet.Target is null, scenario + ": the pet stayed planted");

                // It stays planted only for PlantHoldSeconds once the target stays out of reach.
                target.Position = pet.Position + new Position2(reach + 25.0, 0);
                for (var tick = 0; tick < 7; tick++) runtime.Tick(1.0 / 30.0);
                TestAssert.Equal(0, PetRoutes(match, pet.NetId).Count(route => route.Length > 1),
                    scenario + ": still held 0.23 s after the target stopped just out of reach");
                for (var tick = 0; tick < 36; tick++) runtime.Tick(1.0 / 30.0);
                TestAssert.True(PetRoutes(match, pet.NetId).Any(route => route.Length > 1) || !pet.Alive,
                    scenario + ": a target that stays just out of reach is walked to once the hold ends");
            });
    }

    [Check("pets-wire-plant-hold-target-leaving-band-gets-a-route")]
    public static void ATargetThatLeavesTheBandIsFollowedAtOnce()
    {
        foreach (var scenario in new[] { "Ghoul", "Shaco", "Yorick" })
            HoldScene(scenario, (match, pet, target, reach) =>
            {
                var runtime = match.Runtime;
                target.Position = pet.Position + new Position2(reach + PetOrders.ClientHop(pet) + 150.0, 0);
                for (var tick = 0; tick < 12; tick++) runtime.Tick(1.0 / 30.0);
                var routes = PetRoutes(match, pet.NetId).Where(route => route.Length > 1).ToArray();
                TestAssert.True(routes.Length >= 1, scenario + ": a target past the band draws a route within 0.4 s");
                // Restated in batch 229 (owner, 25 October 2026): the chase re-sends as the pet walks (retail re-paths on ask), so no
                // bound on the count. What is left is where the last route ends: in attack reach of the target.
                var lastEnd = routes[^1][^1];
                TestAssert.True(Math.Sqrt(Math.Pow(lastEnd.X - target.Position.X, 2) + Math.Pow(lastEnd.Z - target.Position.Z, 2)) <=
                                reach + 1.0, scenario + $": the last of {routes.Length} routes ends in attack reach of the target");
            });
    }

    // A pet planted on a stationary target inside its attack reach, with the sink's capture cleared.
    private static void HoldScene(string scenario, Action<Match, Entity, Entity, double> body)
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
        var target = PursuitDummy(match, Team.Chaos, new Position2(6900, 7000), "PlantHold");
        var reach = CombatService.AttackReach(pet, target);
        target.Position = pet.Position + new Position2(reach - 20.0, 0);
        if (scenario == "Ghoul")
            // Batch 146: YorickPHPet.lua's TimerFindEnemies looks for a fight around the hero it
            // fights for (FindTargetInAcRUsingGoldRedirectTarget, the read UncontrollablePet.lua has
            // run on since batch 121), not around the ghoul, so the owner stands inside the ghoul's
            // acquisition range of the target or the ghoul takes nobody at all. The side stand keeps
            // him clear of the pet's body and the target walks only along the pet's x.
            owner.Position = target.Position + new Position2(0.0, -250.0);
        runtime.Delivery.Flush();
        if (scenario != "Ghoul") SendOwnedPetOrder(match, pet, 5, target.Position, target.NetId);
        for (var tick = 0; tick < 30 && runtime.Pets.PlantedTargetOf(pet) != target.NetId; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.Equal(target.NetId, runtime.Pets.PlantedTargetOf(pet), scenario + ": the pet planted on the target");
        match.Clear();
        body(match, pet, target, reach);
    }
}
