using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using static Amberline.Tests.ForcedMoveAiChecks;

namespace Amberline.Tests;

/// <summary>
/// The real spells that displace a lane minion (Blitzcrank's Rocket Grab pull and Power Fist knock-up, Vayne's Condemn), driven
/// through the production tick and read at the observer of the spell's caster: the minion's AI sends it nothing between the
/// dash and the end of the displacement (the stationary anchors that wrap the dash, batch 270, are ahead of it in its flush or at its
/// end), and the AI orders it a few times at most afterwards
/// (night 2, F-FORCED; the real-client captures of batches 34 and 35 held 59 AI routes inside 62 displacements).
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-forced-move-blitzcrank-grab-lane-minion")]
    public static void RocketGrabbedLaneMinionIsLeftAloneUntilItsStunEnds() =>
        WatchLaneMinionDisplacement("Blitzcrank", pull: true, (match, minion) => match.Cast(0, 0, minion));

    [Check("spells-live-forced-move-blitzcrank-power-fist-lane-minion")]
    public static void PowerFistKnockedUpLaneMinionIsLeftAloneUntilItLands() =>
        WatchLaneMinionDisplacement("Blitzcrank", pull: false, (match, minion) =>
        {
            var hero = match.Hero(0);
            match.Cast(0, 2, hero);
            minion.Position = hero.Position + new Position2(90, 0);
            TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, minion.NetId, hero.AttackDamage,
                DamageType.Physical, DamageSourceKind.Basic), "Power Fist hit lands");
        });

    [Check("spells-live-forced-move-vayne-condemn-lane-minion")]
    public static void CondemnedLaneMinionIsLeftAloneUntilItLands() =>
        WatchLaneMinionDisplacement("Vayne", pull: false, (match, minion) => match.Cast(0, 2, minion));

    private static void WatchLaneMinionDisplacement(string champion, bool pull, Action<Match, Entity> displace)
    {
        foreach (var casterTeam in new[] { Team.Order, Team.Chaos })
        {
            var label = $"{champion}/{(pull ? "pull" : "displace")}/{casterTeam}";
            using var match = new Match(champion, casterTeam);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            match.Hero(1).Position = new Position2(6000, 6200); // out of the way
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var minion = runtime.Waves.SpawnFixture(casterTeam == Team.Order ? Team.Chaos : Team.Order,
                MinionType.Melee, 0, "C", 0.0, 0.0);
            minion.Position = hero.Position + new Position2(pull ? 650 : 380, 0);
            minion.Waypoints = [hero.Position];
            minion.AnchorPath = [minion.Position];
            runtime.Delivery.Flush();
            // The minion engages the hero before it is displaced.
            for (var tick = 0; tick < 6; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(minion.Created && minion.Alive, $"{label}: the caster's team knows the lane minion");
            match.Clear();

            displace(match, minion);
            var log = new List<Sent>();
            ForcedMoveState? move = null;
            int started = -1, completeTick = -1, retireTick = -1;
            for (var tick = 0; tick <= 360; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                log.AddRange(Movement(match.Captures[0].Packets, minion.NetId, tick));
                match.Clear();
                if (move is null && minion.ForcedMove is not null) { move = minion.ForcedMove; started = tick; }
                if (move is not null && completeTick < 0 && move.Completed) completeTick = tick;
                if (move is not null && retireTick < 0 && minion.ForcedMove is null) retireTick = tick;
                // 60 steps after the owner retires the move: a status that outlasts the move (a snare) releases the AI late; its order is in the log.
                if (retireTick >= 0 && tick >= retireTick + 60) break;
            }
            TestAssert.True(started >= 0 && retireTick >= 0, $"{label}: the spell displaced the minion and its owner retired the move");
            TestAssert.Equal(1, log.Count(sent => sent.Opcode == VisionPackets.DashOpcode),
                $"{label}: the observer receives the one dash ({Describe(log)})");
            // The AI must stay quiet until the client has landed (one update after the move completes) and until the owner
            // has retired the move; a pull is retired by the end of its stun, and the AI decides in that very step.
            var dashTick = log.First(sent => sent.Opcode == VisionPackets.DashOpcode).Tick;
            // Batch 291: the stun's anchors ahead of the dash in its flush (batch 270) are replaced by it (AfterDash, the client's
            // path install); the rule reads what the client handles after the dash. The owner's landing anchor at the
            // dash's end counts among the orders.
            var orders = AfterDash(log).Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode).ToArray();
            // Batch 211 follow-up: the AI decides when the displacement's status ends (minionLib OnCanMove), and its packet waits for the
            // dash's wire end. A status that outlasts the pull (Blitzcrank's grab) releases the AI after the landing, and the release can
            // be a stop, so the first order is checked against the dash end, not the landing, and the count only bounds a stream.
            // A status that persists keeps the AI held (no release, so no order): the Chaos Power Fist's snare outlasts the window.
            TestAssert.True(orders.Length == 0 || (orders[0].Tick >= dashTick && orders[0].Tick >= completeTick - 1),
                $"{label}: no order before the dash ends (step {completeTick}); the first is at step {(orders.Length > 0 ? orders[0].Tick : -1)}: {Describe(log)}");
            TestAssert.True(orders.Length <= 4,
                $"{label}: a few AI orders after the release, not a stream ({Describe(log)})");
        }
    }

    [Check("pets.forced-move-holds-the-pet-ai")]
    public static void DisplacedPetGetsNoOrderWhileItsDashRuns()
    {
        // Tibbers, Shaco's clone and Yorick's ghoul decide on their own scans (Pet.lua, 0.15 s). A pet a script displaces and
        // that is not stunned (a Power Fist knock-up outlasts its 0.5 s lock, Condemn its 0.25 s one) must not get the stop or
        // chase route of such a scan: the stock client would replace its dash with it.
        foreach (var scenario in new[] { "Annie", "Shaco", "Yorick" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var label = $"{scenario}/{team}";
            using var match = new Match(scenario, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            runtime.Tick(10.1); // the production opening protection ends
            var pet = SpawnPet(scenario, match, owner);
            enemy.Position = pet.Position + new Position2(650, 0); // out of its reach: the pet chases
            runtime.Delivery.Flush();
            for (var tick = 0; tick < 12; tick++) runtime.Tick(0.1);
            match.Clear();

            var requested = pet.Position + new Position2(0, 300);
            const double speed = 1200.0, duration = 300.0 / speed;
            var landing = runtime.Movement.FirstCollisionDashLanding(pet.Position, requested, speed, duration, pet);
            TestAssert.True(Distance(pet.Position, landing) > 150.0, $"{label}: an open 300 u displacement");
            TestAssert.True(runtime.Movement.StartForcedMove(pet.NetId, landing, speed, duration, enemy.NetId,
                "TestDisplacement", cancelOrder: true, firstCollision: true), $"{label}: the forced move starts");
            runtime.Effects.SendDash(pet, pet.Position, landing, speed, 0.0, runtime.World.Clock.SyncId());
            var move = pet.ForcedMove!;
            var log = new List<Sent>();
            var completeTick = -1;
            for (var tick = 0; tick <= 60; tick++)
            {
                runtime.Tick(1.0 / 30.0);
                log.AddRange(Movement(match.Captures[0].Packets, pet.NetId, tick));
                match.Clear();
                if (move.Completed) { completeTick = tick; pet.ForcedMove = null; break; } // the owner retires it
            }
            TestAssert.True(completeTick >= 0, $"{label}: the displacement completes");
            TestAssert.Equal(1, log.Count(sent => sent.Opcode == VisionPackets.DashOpcode), $"{label}: the one dash ({Describe(log)})");
            TestAssert.Equal(0, log.Count(sent => sent.Opcode == VisionPackets.WaypointListOpcode),
                $"{label}: no route or stop while the dash runs ({Describe(log)})");
        }
    }
}
