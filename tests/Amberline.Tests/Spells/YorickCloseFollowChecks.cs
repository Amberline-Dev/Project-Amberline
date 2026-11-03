using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-yorick-r-close-owner-follow-self-and-ally")]
    public static void YorickRCloneFollowsCasterCloseAfterSelfOrAllyCast()
    {
        var gaps = new List<string>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var allyCopy in new[] { false, true })
        {
            using var match = new Match("Yorick", team, ally: true);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var source = allyCopy ? match.Hero(2) : owner;
            match.Hero(1).Position = new Position2(12000, 12000);
            match.Cast(0, 3, source);
            runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickRevenant);
            var label = $"{team}/allyCopy={allyCopy}";
            TestAssert.Equal(owner.NetId, clone.OwnerNetId,
                $"{label}: Yorick remains the R clone's follow owner");
            TestAssert.Equal(source.NetId, clone.PetState!.CloneSourceNetId,
                $"{label}: R clone retains its copied champion source");
            TestAssert.False(UnderHardOrder(match, clone),
                $"{label}: newly spawned R clone starts autonomous follow");

            // Keep the copied ally elsewhere so the test distinguishes owner
            // follow from source follow and removes an unrelated path body.
            if (allyCopy) source.Position = new Position2(10000, 10000);
            else match.Hero(2).Position = new Position2(10000, 10000);
            owner.Position = new Position2(7000, 7000);
            clone.Position = new Position2(6880, 7000);
            clone.Waypoints.Clear();
            clone.Target = null;
            clone.AttackTarget = null;
            var goal = new Position2(7600, 7000);
            match.Move(0, goal);

            var movingMax = 0.0;
            var movingMin = double.PositiveInfinity;
            for (var tick = 0; tick < 120; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                if (owner.Position.X > 7100 && owner.Position.X < 7550)
                {
                    var gap = Distance(owner.Position, clone.Position);
                    movingMax = Math.Max(movingMax, gap);
                    movingMin = Math.Min(movingMin, gap);
                }
            }
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            // owner decision 28 Sept 2026: original game values.
            var target = 200.0;
            var resting = Distance(owner.Position, clone.Position);
            TestAssert.True(Distance(owner.Position, goal) < 10,
                $"{label}: ordinary owner click reaches its goal");
            TestAssert.True(movingMax > 0 && movingMax <= target + owner.Speed * .2 + 2.0,
                $"{label}: R clone follows its moving owner closely; maxGap={movingMax}, target={target}");
            TestAssert.True(movingMin >= clearance - 2.0,
                $"{label}: moving clone avoids owner-body overlap; minGap={movingMin}, clearance={clearance}");
            TestAssert.True(resting >= clearance - 2.0 && resting <= target,
                $"{label}: R clone rests inside the retail return radius; gap={resting}, target={target}");
            TestAssert.False(UnderHardOrder(match, clone),
                $"{label}: ordinary owner movement does not become a pet command");

            match.Clear();
            var parked = clone.Position;
            for (var tick = 0; tick < 24; tick++) runtime.Tick(1.0 / 30.0);
            runtime.Delivery.Flush();
            TestAssert.True(Distance(parked, clone.Position) < 1.0,
                $"{label}: close R clone does not drift at rest");
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == clone.NetId),
                $"{label}: close R clone does not churn waypoints at rest");

            SendPetOrder(match, clone, 9, owner.Position);
            // ORDER_PETHARDRETURN is AI_PET_HARDRETURN in Pet.lua's OnOrder.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, match.Runtime.Pets.StateOf(clone)!.Value,
                $"{label}: explicit Alt return keeps the R clone commandable");
            match.Move(0, new Position2(7840, 7000));
            for (var tick = 0; tick < 80; tick++) runtime.Tick(1.0 / 30.0);
            // Playtest B3 #7, 26 Sept 2026: Pet.lua's hard return ends inside
            // the return radius and normal follow/attack resumes. Reversed
            // from "explicit return remains active".
            TestAssert.False(UnderHardOrder(match, clone),
                $"{label}: explicit return hands the close clone back to normal AI");
            // owner decision 28 Sept 2026: original game values.
            TestAssert.True(Distance(owner.Position, clone.Position) >= clearance - 2.0 &&
                Distance(owner.Position, clone.Position) <= target,
                $"{label}: Alt return follows Yorick closely rather than the copied ally");
            gaps.Add($"{label} movingMax={movingMax:F1} rest={resting:F1} clearance={clearance:F1}");
        }
        Console.WriteLine("Yorick R close follow: " + string.Join("; ", gaps));
    }
}
