using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-shaco-close-normal-owner-follow")]
    public static void ShacoCloneFollowsMovingOwnerCloseWithoutRestChurn()
    {
        var gaps = new List<string>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var clone = SpawnHallucinateClone(match, owner);
            match.Hero(1).Position = new Position2(12000, 12000);
            owner.Position = new Position2(7000, 7000);
            clone.Position = new Position2(6880, 7000);
            clone.Waypoints.Clear();
            clone.Target = null;
            TestAssert.False(UnderHardOrder(match, clone),
                $"{team}: newly spawned clone uses autonomous follow without a pet command");
            match.Move(0, new Position2(7600, 7000));

            var movingMax = 0.0;
            var movingMin = double.PositiveInfinity;
            for (var tick = 0; tick < 120; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                if (owner.Position.X > 7100 && owner.Position.X < 7550)
                {
                    movingMax = Math.Max(movingMax, Spacing(owner.Position, clone.Position));
                    movingMin = Math.Min(movingMin, Spacing(owner.Position, clone.Position));
                }
            }
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            // playtest B4 / retail sweep, 27 Sept 2026: full shipped hero/NPC
            // radii also apply to the owner/clone pair.
            TestAssert.Near(125.55, clearance, 1e-5,
                "Shaco and his clone retain both shipped 56.775 radii plus 12 skin");
            TestAssert.Near(clearance, runtime.Movement.BodyClearance(clone, owner), 0,
                "owner/clone clearance is symmetric");
            // owner decision 28 Sept 2026: original game values.
            var followDistance = 200.0;
            TestAssert.True(movingMax > 200.0,
                $"{team}: moving owner crosses the idle return threshold; " +
                $"maxGap={movingMax}, owner={owner.Position}, clone={clone.Position}");
            TestAssert.True(movingMin >= clearance - 2,
                $"{team}: normal follow avoids overlapping the owner body; minGap={movingMin}, clearance={clearance}");
            TestAssert.True(Spacing(owner.Position, clone.Position) >= clearance - 2 &&
                Spacing(owner.Position, clone.Position) <= followDistance,
                $"{team}: clone settles close without entering the owner body; " +
                $"clearance={clearance}, owner={owner.Position}, clone={clone.Position}");

            for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.Equal(0, clone.Waypoints.Count,
                $"{team}: close clone reaches its rest band before packet-churn observation");
            match.Clear();
            var resting = clone.Position;
            for (var tick = 0; tick < 24; tick++) runtime.Tick(1.0 / 30.0);
            runtime.Delivery.Flush();
            TestAssert.True(Spacing(resting, clone.Position) < 1,
                $"{team}: close clone does not oscillate at rest");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == clone.NetId),
                $"{team}: close clone does not churn waypoint packets at rest");
            TestAssert.False(UnderHardOrder(match, clone),
                $"{team}: ordinary hero movement leaves clone in autonomous follow");
            gaps.Add($"{team} movingMax={movingMax:F1} rest={Spacing(owner.Position, clone.Position):F1} clearance={clearance:F1}");
        }
        Console.WriteLine("Shaco close follow: " + string.Join("; ", gaps));
    }

    [Check("pets-live-shaco-paired-right-click-keeps-owner-gap")]
    public static void ShacoPairedRightClickSettlesOutsideOwner()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var clone = SpawnHallucinateClone(match, owner);
            match.Hero(1).Position = new Position2(12000, 12000);
            owner.Position = new Position2(7000, 7000);
            clone.Position = new Position2(6800, 7000);
            clone.Waypoints.Clear();
            clone.Target = null;
            var clicked = new Position2(7300, 7000);

            // owner decision 28 Sept 2026: original game values.
            // Physical clearance replaces the former clearance + 24 owner ring.
            // A hero click and an explicit Alt pet click can name the same
            // point, which the owner may reach before the clone.
            match.Move(0, clicked);
            SendOwnedPetOrder(match, clone, 6, clicked);
            // ORDER_PETHARDMOVE is AI_PET_HARDMOVE in Pet.lua's OnOrder.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardMove, match.Runtime.Pets.StateOf(clone)!.Value,
                "paired click starts a clone move");
            var ownerReachedFirst = false;
            // Restated in batch 254 (150 ticks, 5 s): the clone's route is the engine planner's (the issue-order routine ->
            // the path builder) to the click, planned while the click was free. The owner, standing on it first, holds
            // the clone until the solver's 45-frame give-up counter (the actor's counter; CanCollide) lets it through (about 7.5 s);
            // it then arrives, Pet.lua parks it, and the owner-overlap separation walks it clear. The deleted body-aware planner
            // re-aimed the end off the owner instead. The retail server's stuck-site repath (RefreshPath) is not recovered.
            for (var tick = 0; tick < 300; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                ownerReachedFirst |= Spacing(owner.Position, clicked) < 10 &&
                                     Spacing(clone.Position, clicked) > 50;
            }
            TestAssert.True(ownerReachedFirst,
                $"{team}: owner reaches the shared click before the clone");
            // Pet.lua TimerScanDistance parks a finished AI_PET_HARDMOVE in AI_PET_HARDIDLE.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardIdle, match.Runtime.Pets.StateOf(clone)!.Value,
                $"{team}: clone finishes its move as a hold");
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            TestAssert.True(Spacing(owner.Position, clone.Position) >= clearance - 2,
                $"{team}: completed shared click leaves a clear owner gap; " +
                $"owner={owner.Position}, clone={clone.Position}, route={string.Join(';', clone.Waypoints)}");

            match.Clear();
            var resting = clone.Position;
            for (var tick = 0; tick < 18; tick++) runtime.Tick(1.0 / 30.0);
            runtime.Delivery.Flush();
            TestAssert.True(Spacing(resting, clone.Position) < 1,
                $"{team}: parked clone does not oscillate at rest");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == clone.NetId),
                $"{team}: parked clone does not churn waypoint packets");

            // A later explicit pet click on the owner must not undo the gap.
            SendOwnedPetOrder(match, clone, 6, owner.Position);
            var orderedEndpoint = clone.Waypoints.Count > 1
                ? clone.Waypoints[^1] : clone.Position;
            // Restated in batch 254 (was clearance - 2, the deleted body-aware planner's projection): the engine planner's end-cell snap
            // (the nearest-cell snap) moves the end off the cells the owner covers, beside him (84 units here); the
            // contact solver keeps the clone outside his body on the way.
            TestAssert.True(Spacing(owner.Position, orderedEndpoint) >= runtime.Movement.MovementRadius(owner),
                $"{team}: an immediate owner-centred pet click ends off the owner's cells; " +
                $"owner={owner.Position}, endpoint={orderedEndpoint}");
            for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(Spacing(owner.Position, clone.Position) >= clearance - 2,
                $"{team}: owner-centred pet click settles outside the owner");
        }
    }

    [Check("pets-live-shaco-held-clone-escapes-owner-crossing")]
    public static void ShacoHeldCloneEscapesOwnerCrossing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var clone = SpawnHallucinateClone(match, owner);
            match.Hero(1).Position = new Position2(12000, 12000);
            owner.Position = new Position2(7000, 7000);
            // Cross the near-follow band while the requested destination
            // remains outside the parked clone's physical collision shell.
            var crossingGoal = new Position2(6700, 7000);
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            var parkedGap = Math.Max(100.0, clearance + 5.0);
            clone.Position = crossingGoal + new Position2(parkedGap, 0);
            // playtest B4 / retail sweep, 27 Sept 2026: the near boundary
            // grows with shipped owner/clone clearance, not the old 120 floor.
            var followDistance = Math.Max(120.0, clearance + 24.0);
            TestAssert.True(parkedGap < followDistance - 12.0 && parkedGap > clearance,
                "held-clone fixture ends inside the near-follow band but outside body collision");
            clone.Waypoints.Clear();
            clone.Target = null;
            SendOwnedPetOrder(match, clone, 11, clone.Position);
            // ORDER_PETHARDSTOP is AI_PET_HARDSTOP in Pet.lua's OnOrder.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, match.Runtime.Pets.StateOf(clone)!.Value,
                "clone accepts a real hold order");
            var heldAt = clone.Position;

            match.Move(0, crossingGoal);
            for (var tick = 0; tick < 150; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(Spacing(owner.Position, crossingGoal) < 15,
                $"{team}: owner crosses the held clone's spacing ring; " +
                $"owner={owner.Position}, clone={clone.Position}, " +
                $"ownerRoute={string.Join(';', owner.Waypoints)}, cloneRoute={string.Join(';', clone.Waypoints)}");
            TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, match.Runtime.Pets.StateOf(clone)!.Value,
                $"{team}: owner crossing does not turn hold into follow");
            // PET-03, 27 Sept 2026 base-game sweep: reverse the old voluntary
            // spacing assertion. Pet.lua hard-stop stays put outside overlap.
            TestAssert.Equal(heldAt, clone.Position, $"{team}: held clone stays where ordered");
            TestAssert.True(clone.Target is null && clone.Waypoints.Count == 0,
                $"{team}: hard-stop owns no voluntary spacing route");
            TestAssert.True(Spacing(owner.Position, clone.Position) >= clearance,
                $"{team}: the owner's crossing still respects physical collision");
        }
    }

    private static double Spacing(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));
}
