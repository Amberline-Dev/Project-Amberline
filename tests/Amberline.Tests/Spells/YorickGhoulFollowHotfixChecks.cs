using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-yorick-b5-ghoul-distance-scan")]
    public static void YorickGhoulsTeleportOnlyBeyondTheMovableLeash()
    {
        foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
        foreach (var distance in new[] { 2500.0, 2501.0 })
        foreach (var rooted in new[] { false, true })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime; var owner = match.Hero(0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, kind, owner.Position, 0);
            ghoul.Position = owner.Position + new Position2(distance, 0);
            var origin = ghoul.Position;
            // Batch 146: the blink is YorickPHPet TimerScanDistance's SetActorPositionFromObject and
            // NetSetState(AI_PET_IDLE), and its own branch is what clears the pursuit. The same
            // 0.15 s pass then runs TimerFindEnemies, which takes an enemy standing in the hero's
            // scan circle at the landing point (200 away in this scene, well inside the 400-600 the
            // ghouls ask with), so the foe under test stands outside it while staying visible —
            // past the largest ghoul acquisition range, short of the vision one.
            match.Hero(1).Position = owner.Position + new Position2(800.0, 0.0);
            ghoul.AttackTarget = match.Hero(1).NetId;
            ghoul.Target = owner.Position;
            ghoul.Waypoints.Add(owner.Position);
            if (rooted) ghoul.Buffs.Add(new BuffState("LeashRoot", type: BuffType.Snare));
            runtime.Pets.Tick(.149, .149);
            TestAssert.Equal(origin, ghoul.Position, "no distance scan before .15 seconds");
            runtime.Pets.Tick(.15, .001);
            var teleported = !rooted && distance > 2500;
            // Restated in batch 254: the ghoul stands on the point (the owner's after a blink) or, once an order's path seats it
            // (the client's path install), on that point's 2-unit anchor.
            var stand = teleported ? owner.Position : origin;
            TestAssert.True(ghoul.Position == stand || ghoul.Position == runtime.Movement.ClientAnchor(stand),
                $"YOR-04 {kind}, distance={distance}, rooted={rooted}: at {ghoul.Position}");
            if (teleported)
            {
                // The blink is TimerScanDistance's SetActorPositionFromObject onto the gold-redirect
                // target and NetSetState(AI_PET_IDLE), and that is what clears the pursuit. Batch 146
                // read the two script differences into the scene above: the blink lands the ghoul on
                // the hero's body, where the emulator's overlap recovery (NOT RETAIL, SeparateFromOwner)
                // walks it one step clear of him, and the same 0.15 s pass's TimerFindEnemies takes
                // an enemy standing in the hero's scan circle at the landing point. So what is left
                // is checked against the enemy rather than against nothing.
                TestAssert.Equal<uint?>(null, ghoul.AttackTarget, "teleport clears the pursued target");
                TestAssert.True(ghoul.Target is null, "teleport clears the pursued move goal");
                var towardEnemy = ghoul.Waypoints.Any(point => Distance(point, match.Hero(1).Position) < 100.0) ||
                    (runtime.Pets.LastDestinations.TryGetValue(ghoul.NetId, out var left) &&
                     Distance(left, match.Hero(1).Position) < 100.0);
                TestAssert.False(towardEnemy, "teleport leaves no route or destination at the enemy");
            }
        }
    }

    [Check("spells-live-yorick-ghoul-follow-after-target-death")]
    public static void YorickGhoulFollowsOwnerAfterTargetDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
        foreach (var attackState in new[] { false, true })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = owner.Position + new Position2(attackState ? 60.0 : 800.0, 0.0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, kind, owner.Position, runtime.Combat.CurrentTime);
            // The YorickSummon* script's taunt sends the ghoul at its target, and the pet AI under
            // test has to start from the state that leaves it in (AI_TAUNTED): a direct
            // AttackTarget write leaves the ghoul in AI_PET_IDLE, which neither closes on the
            // target (the chase case) nor swings at it (the attack case) - its first step is the
            // overlap recovery of a ghoul spawned on its owner's body. Batch 146: the script wins.
            runtime.Spells.Buffs.Add(ghoul, "Taunt", BuffType.Taunt, 5.0, enemy.NetId,
                canMitigateDuration: false);

            runtime.Pets.Tick(0.0, 0.0);
            if (attackState)
                TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == ghoul.NetId),
                    $"{kind} dispatches an attack before the target dies");
            else
                TestAssert.True(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                    $"{kind} enters chase before the target dies");

            owner.Position += new Position2(500.0, 0.0);
            runtime.Combat.Kill(enemy, owner.NetId);
            runtime.Pets.Tick(0.1, 0.1);
            TestAssert.Equal<uint?>(null, ghoul.AttackTarget,
                $"{kind} clears a dead { (attackState ? "attack" : "chase") } target immediately");
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId) ||
                             ghoul.Waypoints.Count > 0,
                $"{kind} clears the stale target route before following");

            runtime.Pets.Tick(0.5, 0.4);
            TestAssert.True(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                $"{kind} follows the living owner after target death");
            // owner decision 28 Sept 2026: original game values.
            TestAssert.Equal(owner.Position, runtime.Pets.LastDestinations[ghoul.NetId],
                $"{kind} closes to the owner, with no 200-unit destination offset");
        }
    }

    [Check("spells-live-yorick-ghoul-follow-gates-and-lifecycle")]
    public static void YorickGhoulFollowPreservesGatesAndLifecycle()
    {
        foreach (var gate in new[] { "root", "stun" })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, "Spectral", owner.Position, 0.0);
            owner.Position += new Position2(500.0, 0.0);
            ghoul.Buffs.Add(gate == "root"
                ? new BuffState("TestRoot", new BuffPayload { MovementDisabled = true }, BuffType.Snare)
                : new BuffState("TestStun", type: BuffType.Stun));
            runtime.Pets.Tick(0.5, 0.5);
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId) ||
                             ghoul.Waypoints.Count > 0,
                $"{gate} blocks idle owner-follow movement");
        }

        foreach (var kind in new[] { "Spectral", "Decayed", "Ravenous" })
        {
            using var ownerDeath = new Match("Yorick", Team.Order);
            var runtime = ownerDeath.Runtime;
            var owner = ownerDeath.Hero(0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, kind, owner.Position, 0.0);
            runtime.Combat.Kill(owner, ownerDeath.Hero(1).NetId);
            // Retail, owner research 18 Oct 2026: Yorick's ghouls die with him.
            TestAssert.False(ghoul.Alive, $"{kind} ghoul dies with its owner");
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                $"{kind} ghoul leaves no route behind");
        }

        foreach (var kind in new[] { "Spectral", "Decayed", "Ravenous" })
        {
            // The end of Yorick's Omen of Death temporary life is a second death that bypasses CombatService.Kill.
            using var revival = new Match("Yorick", Team.Order);
            var runtime = revival.Runtime;
            var owner = revival.Hero(0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, kind, owner.Position, 0.0);
            owner.YorickRevival = new YorickRevivalState(100.0);
            runtime.Combat.Kill(owner, revival.Hero(1).NetId);
            TestAssert.False(ghoul.Alive, $"{kind} ghoul dies when Yorick's temporary life ends");
        }
    }

    [Check("spells-live-yorick-ghoul-production-follow")]
    public static void YorickGhoulProductionCastsCreateFollowableKinds()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var w = new Match("Yorick", team))
            {
                var owner = w.Hero(0);
                var enemy = w.Hero(1);
                enemy.Position = owner.Position + new Position2(800.0, 0.0);
                w.CastGround(0, owner.Position, 1);
                var ghoul = WaitForGhoul(w, "Decayed");
                TestAssert.Equal("Decayed", GhoulKind(ghoul), "W creates a Decayed ghoul");
                w.Runtime.Tick(0.1);
                TestAssert.True(w.Runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                    "production W ghoul enters chase through the shared AI");
                owner.Position += new Position2(500.0, 0.0);
                w.Runtime.Combat.Kill(enemy, owner.NetId);
                w.Runtime.Tick(0.1);
                // owner decision 28 Sept 2026: original game values.
                // A due .15 s distance scan may replace the dead route immediately.
                TestAssert.Equal((uint?)null, ghoul.AttackTarget, "dead target is released");
                if (w.Runtime.Pets.LastDestinations.TryGetValue(ghoul.NetId, out var following))
                    TestAssert.Equal(owner.Position, following, "only the owner route can replace pursuit");
                w.Runtime.Tick(0.5);
                TestAssert.True(w.Runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                    "production W ghoul follows the owner after target death");
                var followStart = ghoul.Position;
                var distanceBeforeMovement = Distance(ghoul.Position, owner.Position);
                for (var tick = 0; tick < 3; tick++) w.Runtime.Tick(0.1);
                var distanceAfterMovement = Distance(ghoul.Position, owner.Position);
                TestAssert.True(Distance(followStart, ghoul.Position) > 1.0,
                    "production W ghoul advances along the owner-follow route");
                TestAssert.True(distanceAfterMovement < distanceBeforeMovement - 1.0,
                    "production W ghoul closes distance to the owner");

                var replacement = new Entity(w.Runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, enemy.Team,
                    owner.Position + new Position2(100.0, 0.0))
                {
                    Health = 500.0,
                    MaxHealth = 500.0,
                    Radius = 35.0,
                    Created = true,
                    Visible = true,
                };
                w.Runtime.World.Register(replacement);
                w.Runtime.Tick(0.5);
                TestAssert.Equal<uint?>(replacement.NetId, ghoul.AttackTarget,
                    "production W ghoul reacquires a new enemy after following");
            }

            using (var e = new Match("Yorick", team))
            {
                var owner = e.Hero(0);
                var enemy = e.Hero(1);
                enemy.Position = owner.Position + new Position2(200.0, 0.0);
                e.Cast(0, 2, enemy);
                var ghoul = WaitForGhoul(e, "Ravenous");
                TestAssert.Equal("Ravenous", GhoulKind(ghoul), "E creates a Ravenous ghoul");
            }

            using (var q = new Match("Yorick", team))
            {
                var owner = q.Hero(0);
                var enemy = q.Hero(1);
                enemy.Position = owner.Position + new Position2(100.0, 0.0);
                owner.Facing = new Position2(1.0, 0.0);
                q.Cast(0, 0, enemy);
                q.Attack(0, enemy);
                var ghoul = WaitForGhoul(q, "Spectral");
                TestAssert.Equal("Spectral", GhoulKind(ghoul), "Q basic attack creates a Spectral ghoul");
            }
        }
    }

    [Check("spells-live-yorick-ghoul-acquires-next-neutral")]
    public static void YorickGhoulFindsNextNeutralInAcquisitionRange()
    {
        // Playtest B2 YV7, 26 Sept 2026: YorickPHPet's OnTargetLost runs
        // FindTargetInAcR; an unaggroed camp monster qualifies (owner decision).
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000.0, 12000.0);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, "Ravenous", owner.Position, 0.0);
            TestAssert.Near(400.0, ghoul.AcquisitionRange, 1e-9,
                $"{team}: Ravenous ghoul uses its shipped acquisition range");
            Entity Neutral(string name, Position2 position) => runtime.World.Register(new Entity(
                runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster,
                Team.Neutral, position)
            {
                Name = name, Health = 5000.0, MaxHealth = 5000.0, Radius = 30.0,
                Created = true, Visible = true,
            });
            var marked = Neutral("GhoulMarkedMonster", owner.Position + new Position2(250.0, 0.0));
            var next = Neutral("GhoulNextMonster", marked.Position + new Position2(0.0, 200.0));
            // The YorickSummonRavenous script's taunt sends the ghoul at the marked neutral, and
            // the pet AI under test has to start from the state that leaves it in: YorickPHPet.lua's
            // OnTargetLost keeps a re-found target only from a fighting state (AI_PET_ATTACK,
            // AI_PET_HARDATTACK, AI_TAUNTED and the three *_ATTACKING ones) and drops the find from
            // any other — the same shape CombatService records for Turret.luaobj. A direct
            // AttackTarget write left the ghoul in AI_PET_IDLE, where the script takes the find and
            // throws it away (NetSetState(AI_PET_IDLE)) and the host then clears the target it
            // still held. Batch 146: the script wins, so the taunt is the setup.
            runtime.Spells.Buffs.Add(ghoul, "Taunt", BuffType.Taunt, 5.0, marked.NetId,
                canMitigateDuration: false);

            runtime.Pets.Tick(0.0, 0.0);
            TestAssert.Equal<uint?>(marked.NetId, ghoul.AttackTarget,
                $"{team}: the ghoul attacks its marked neutral");
            runtime.Combat.Kill(marked, owner.NetId);
            runtime.Pets.Tick(0.1, 0.1);
            TestAssert.Equal<uint?>(next.NetId, ghoul.AttackTarget,
                $"{team}: the ghoul takes the next monster in acquisition range instead of following");
            TestAssert.True(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                $"{team}: the ghoul chases the next monster");
        }
    }

    private static Entity WaitForGhoul(Match match, string kind)
    {
        for (var tick = 0; tick < 40; tick++)
        {
            var ghoul = match.Runtime.World.Entities.Values.FirstOrDefault(entity =>
                entity.Alive && entity.Kind == EntityKind.YorickGhoul &&
                entity.OwnerNetId == match.Hero(0).NetId && GhoulKind(entity) == kind);
            if (ghoul is not null) return ghoul;
            match.Runtime.Tick(0.1);
        }
        throw new InvalidOperationException($"production cast did not create a {kind} ghoul");
    }

    private static string GhoulKind(Entity ghoul) => ghoul.Buffs
        .Single(buff => buff.Name.EndsWith("Logic", StringComparison.Ordinal)).Name[6..^5];
}
