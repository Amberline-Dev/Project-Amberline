using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("movement.rooted-attacks-and-stop")]
    public static void RootsPreserveInRangeAttackSelectionAndStop()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "LeeSin" })
        foreach (var payloadRoot in new[] { false, true })
        {
            using var match = new Match(champion, team, extraEnemy: true);
            var runtime = match.Runtime; var hero = match.Hero(0);
            var enemy = match.Hero(1); var second = match.Hero(2);
            enemy.Position = hero.Position + new Position2(100, 0);
            second.Position = hero.Position + new Position2(0, 100);
            var root = runtime.Buffs.AddBuff(hero, "OrderRoot", payloadRoot ? BuffType.Internal : BuffType.Snare,
                30, enemy.NetId, new BuffPayload { MovementDisabled = payloadRoot });
            var held = hero.Position;
            TestAssert.Equal(3, runtime.HeroStats.Snapshot(hero).ActionState & 7,
                "root disables movement while preserving attack/cast flags");
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0);
            runtime.Combat.CombatTick(.01, regenerate: false);
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId));
            SendHeroOrder(match, 3, second.Position, second.NetId);
            TestAssert.Equal(second.NetId, hero.AttackTarget ?? 0, "a root permits changing in-range targets");
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId && hit.TargetNetId == enemy.NetId));
            SendHeroOrder(match, 10, hero.Position);
            TestAssert.True(hero.AttackTarget is null && hero.HeroPursuit is null && !hero.HeroAutoAcquire);
            runtime.Tick(.3);
            TestAssert.True(hero.AttackTarget is null, "Stop remains effective through acquisition scans");
            // Batch 271: the stop's one-point path seats the rooted hero on its lattice anchor (the client's path follower has no root test); it walks nowhere.
            TestAssert.Equal(runtime.Movement.ClientAnchor(held), hero.Position);

            var visibleOutOfRange = false;
            foreach (var offset in new[] { new Position2(850, 0), new Position2(-850, 0),
                         new Position2(0, 850), new Position2(0, -850),
                         new Position2(700, 700), new Position2(-700, -700) })
            {
                second.Position = hero.Position + offset;
                runtime.Delivery.Flush();
                visibleOutOfRange = runtime.Navigation.IsPathingWalkable(second.Position) &&
                    runtime.Session.Participants[0].View.Visible.Contains(second.NetId) &&
                    runtime.Session.CurrentTargetVisible?.Invoke(hero, second) == true &&
                    !runtime.Combat.CanAttack(hero, second);
                if (visibleOutOfRange) break;
            }
            TestAssert.True(visibleOutOfRange, "rooted attack target is visible but out of range");
            SendHeroOrder(match, 3, second.Position, second.NetId);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0,
                "an out-of-range attack cannot start movement during a root");
            var health = enemy.Health;
            enemy.HealthRegen = 0;
            SendHeroOrder(match, 1, hero.Position);
            runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0, "Hold can reacquire an enemy already in range");
            runtime.Tick(hero.AttackPeriod + hero.AttackWindup + .3);
            TestAssert.True(enemy.Health < health, $"{team}/{champion}/{payloadRoot}: rooted basic attacks resolve damage");
            TestAssert.Equal(runtime.Movement.ClientAnchor(held), hero.Position);
            TestAssert.True(hero.HeroHold, "Hold stays in force: attack in reach only (owner decision, 18 Oct 2026)");

            // The explicit attack below is what must resume after the root: a held hero never chases (owner decision, 18 Oct 2026),
            // so the retained pursuit is an ATTACK_TO selected while the enemy is still in range.
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            enemy.Position = hero.Position + new Position2(800, 0);
            runtime.Tick(.2);
            TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null);
            runtime.Buffs.RemoveBuff(hero, root!.Slot);
            runtime.Tick(.2);
            TestAssert.True(hero.Waypoints.Count > 0, "retained attacks resume pursuit after root expiry");

            runtime.Buffs.AddBuff(hero, "OrderStun", BuffType.Stun, 30, enemy.NetId);
            enemy.Position = hero.Position + new Position2(100, 0);
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0,
                "stun still blocks attack selection and movement");
        }
    }

    [Check("movement.rooted-target-visibility")]
    public static void RootedPursuitStillLosesHiddenTargets()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            runtime.Buffs.AddBuff(hero, "VisionRoot", BuffType.Snare, 30, enemy.NetId);
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy));
            var hidden = runtime.Buffs.AddBuff(enemy, "RootTargetHidden", BuffType.Internal, 30,
                enemy.NetId, new BuffPayload { Stealthed = true });
            match.Clear();
            var health = enemy.Health;
            enemy.HealthRegen = 0;
            runtime.Tick(.2);
            TestAssert.True(hero.AttackTarget is null && hero.Waypoints.Count == 0);
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId));
            TestAssert.Equal(health, enemy.Health);
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x39),
                "visibility loss cancels the unlaunched attack on the client too");
            runtime.Buffs.RemoveBuff(enemy, hidden!.Slot);
            runtime.Tick(.2);
            TestAssert.Equal(enemy.NetId, hero.AttackTarget ?? 0, "visibility restoration allows in-range reacquisition");
        }
    }

    [Check("movement.failed-hero-route-and-cast-retry")]
    public static void FailedHeroRoutesStopPredictionAndQueuedCastsCanRetry()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var order in new[] { "move", "attack", "cast" })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            // An allied observer supplies sight beyond intervening terrain;
            // this test isolates path failure from legitimate fog rejection.
            var visible = false;
            foreach (var offset in new[] { new Position2(-1000, 0), new Position2(0, 1000),
                         new Position2(0, -1000), new Position2(1000, 0),
                         new Position2(-700, -700), new Position2(700, 700) })
            {
                enemy.Position = hero.Position + offset;
                match.Hero(2).Position = enemy.Position;
                runtime.Delivery.Flush();
                visible = runtime.Navigation.IsPathingWalkable(enemy.Position) &&
                    runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId);
                if (visible) break;
            }
            TestAssert.True(visible, "visible out-of-range approach target");
            var held = hero.Position;
            hero.Waypoints = [held + new Position2(500, 0)];
            hero.Target = hero.Waypoints[^1];
            // Batch 61: the hero planner ignores NavigationGrid.FindPathOverride. Navigation fails the way the stock planner meets a
            // failure: eight wide friendly bodies wall the hero in, so every neighbouring cell is occupied for the actor test and
            // the search ends on the start cell. A failed search always costs at least one A* iteration, so the planner's work meter
            // counts the attempts (a direct walk costs none).
            var wall = Enumerable.Range(0, 8).Select(index =>
            {
                var angle = index * Math.PI / 4.0;
                return runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, hero.Team, held + new Position2(Math.Cos(angle) * 170.0, Math.Sin(angle) * 170.0))
                {
                    Name = "Minion_Basic", Created = true, Visible = true, Radius = 120.0, Speed = 325.0,
                    Health = 1000.0, MaxHealth = 1000.0,
                });
            }).ToArray();
            var workBefore = runtime.Movement.ClientPlannerWork;
            long Attempts() => runtime.Movement.ClientPlannerWork - workBefore;
            match.Clear();
            if (order == "cast") match.Cast(0, 0, enemy);
            else SendHeroOrder(match, order == "attack" ? (byte)3 : (byte)2,
                enemy.Position, order == "attack" ? enemy.NetId : 0);
            TestAssert.True(Attempts() > 0);
            // Batch 61: a failed plan is the stationary anchor of the hero (a one-point route at its own position, which the next
            // movement frame retires as an arrival), not an empty list.
            TestAssert.True(hero.Waypoints.All(point => point == hero.Position) && hero.Target is null,
                "failed navigation clears the previous route and never installs a straight target");
            AssertHeldRoute(match, hero);
            // The order seated the hero on its wire anchor (batch 61, SeatWireAnchorOnFloor); a failed plan moves it nowhere else.
            held = hero.Position;
            runtime.Movement.Tick(.1);
            TestAssert.Equal(held, hero.Position);
            TestAssert.Equal(0, hero.Waypoints.Count, "the stationary anchor is retired by the next movement frame");

            if (order == "cast")
            {
                var firstAttempts = Attempts();
                runtime.Spells.Tick(.1);
                TestAssert.Equal(firstAttempts, Attempts(), "failed approaches do not replan every frame");
                runtime.Spells.Tick(.11);
                var secondAttempts = Attempts();
                TestAssert.True(secondAttempts > firstAttempts,
                    "an idle queued cast retries after the scan interval");
                var root = runtime.Buffs.AddBuff(hero, "ApproachRoot", BuffType.Snare, 30, enemy.NetId);
                runtime.Spells.Tick(.3);
                TestAssert.Equal(secondAttempts, Attempts(), "rooted approaches do not keep sending stop packets");
                runtime.Buffs.RemoveBuff(hero, root!.Slot);
            }
            foreach (var body in wall) body.Alive = false;
            if (order == "move") SendHeroOrder(match, 2, enemy.Position);
            else runtime.Tick(.21);
            TestAssert.True(hero.Waypoints.Count > 0, "a successful retry restores a real route");
            if (order == "cast")
            {
                for (var step = 0; step < 30 && hero.PendingCast is not null; step++) runtime.Tick(.1);
                TestAssert.True(hero.PendingCast is null);
                TestAssert.Equal(1, hero.MatchStatistics.SpellCasts[0], "the original queued cast executes once after recovery");
            }
        }
    }

    [Check("movement.failed-pet-routes")]
    public static void PetsStopOnFailedNavigationInsteadOfWalkingThroughTerrain()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.Pet, EntityKind.YorickGhoul })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime; var owner = match.Hero(0); var enemy = match.Hero(1);
            var position = new Position2(7000, 7300);
            enemy.Position = new(7400, 7300);
            Entity pet;
            if (kind is EntityKind.ShacoClone or EntityKind.YorickRevenant)
            {
                pet = runtime.Clones.Spawn(owner, owner, position,
                    kind == EntityKind.ShacoClone ? CloneKind.ShacoClone : CloneKind.YorickRevenant,
                    500, kind == EntityKind.ShacoClone ? "HallucinateFull" : "YorickRAPetBuff2", 30, "Pet");
                runtime.SpellBuffs.Add(pet, kind == EntityKind.ShacoClone ? "HallucinateFull" : "YorickRAPetBuff2",
                    BuffType.Internal, 30, owner.NetId, new BuffPayload());
            }
            else if (kind == EntityKind.YorickGhoul)
                pet = YorickScriptSupport.SpawnUnit(runtime, owner, "Spectral", position, 0);
            else
                pet = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster), kind,
                    UnitType.Monster, team, position) { Name = "AnnieTibbers", OwnerNetId = owner.NetId,
                    Health = 500, MaxHealth = 500, AttackRange = 100, Speed = 350, ExpiresAt = 30, Created = true,
                    // Tibbers runs Pet.lua like every pet: the spawn metadata is what the host routes on.
                    PetState = new PetState(CloneKind.Tibbers, owner.NetId) { SpawnBuffName = "AnnieTibbers", SpawnDuration = 30 } });
            // These actors are already known to the observing clients; this
            // regression checks movement failure rather than spawn encoding.
            foreach (var seat in runtime.Session.Participants)
            {
                seat.View.Known.Add(pet.NetId);
                seat.View.Visible.Add(pet.NetId);
            }
            runtime.Delivery.Flush();
            var attempts = 0;
            runtime.Navigation.FindPathOverride = (_, _) => { attempts++; return []; };
            match.Clear();
            // Batch 230: each order is the script's next 0.15 s timer, not a repeat of the same clock reading (a timer that does not
            // come due at an unchanged clock never retries). Pet.txt's TimerScanDistance is the retry.
            var clock = .3;
            void Order()
            {
                // The clone takes the hard move through Pet.lua's OnOrder; the ghoul and Tibbers walk
                // on the script's own 0.15 s scan (playtest B3 #7), which the 0.15 s step runs now.
                if (kind is EntityKind.ShacoClone or EntityKind.YorickRevenant)
                    TestAssert.True(runtime.Pets.Command(owner, 6, enemy.Position, actorNetId: pet.NetId));
                else
                {
                    runtime.Pets.Tick(clock, .15);
                    clock += .15;
                }
                runtime.Delivery.Flush();
            }
            Order();
            TestAssert.True(attempts > 0, kind.ToString());
            TestAssert.True(pet.Waypoints.Count == 0 && pet.Target is null, kind.ToString());
            AssertHeldRoute(match, pet);
            runtime.Movement.Tick(.1);
            // Batch 279: the failed order's stop is installed at once (the client's path follower): the pet stands on its point's anchor.
            TestAssert.Equal(runtime.Movement.ClientAnchor(position), pet.Position);
            runtime.Navigation.FindPathOverride = null;
            Order();
            TestAssert.True(pet.Waypoints.Count > 0, $"{kind} retries without a stale destination cache blocking it");
        }
    }

    private static void AssertHeldRoute(Match match, Entity actor)
    {
        for (var index = 0; index < match.Captures.Count; index++)
        {
            var routes = match.Captures[index].Packets.Where(packet => Opcode(packet) == VisionPackets.WaypointListOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == actor.NetId).ToArray();
            if (!match.Runtime.Session.Participants[index].View.Visible.Contains(actor.NetId))
            {
                TestAssert.Equal(0, routes.Length, "a held route does not reveal an actor in fog");
                continue;
            }
            TestAssert.True(routes.Length > 0, "each observer with sight receives an authoritative stop");
            foreach (var route in routes)
            {
                var bytes = route.Plaintext.Span;
                TestAssert.Equal(17, bytes.Length, "one waypoint, no invented destination");
                TestAssert.Equal((float)actor.Position.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]));
                TestAssert.Equal((float)actor.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[13..]));
            }
        }
    }
}
