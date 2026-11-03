using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-shaco-b5-hidden-box-damage-needs-full-sight")]
    public static void ShacoBoxDetectionDoesNotBypassOpaqueWall()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            var (near, beyond) = OpaqueWallSides(match, enemy, hero);
            enemy.Position = near; hero.Position = beyond;
            var box = runtime.Spells.ScriptPets!.Boxes.SpawnForScript(hero, beyond, 0);
            box.Position = beyond;
            runtime.Buffs.AddBuff(box, "Stealth", Amberline.Core.Buffs.BuffType.Internal, 90,
                box.NetId, new Amberline.Core.Buffs.BuffPayload { Stealthed = true });
            runtime.Buffs.AddBuff(enemy, "OracleElixirSight", Amberline.Core.Buffs.BuffType.Aura, 60, enemy.NetId);
            TestAssert.True(runtime.Dependencies.RevealsStealth(enemy.Team, box.Position));
            TestAssert.False(runtime.Spells.CanSeeTarget(enemy, box));
            var health = box.Health;
            TestAssert.False(runtime.Combat.ApplyHit(enemy.NetId, box.NetId, 10, DamageType.True, DamageSourceKind.SpellAoe));
            TestAssert.Near(health, box.Health, 0, "detection through terrain is not full sight");
            enemy.Position = beyond;
            TestAssert.True(runtime.Spells.CanSeeTarget(enemy, box));
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, box.NetId, 10, DamageType.True, DamageSourceKind.SpellAoe));
            TestAssert.Near(health - 10, box.Health, 1e-9);
            enemy.Position = near;
            var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
                EntityKind.Turret, UnitType.Turret, enemy.Team, new Position2(12000, 12000))
                { Health = 1000, MaxHealth = 1000 });
            TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, box.NetId, 10, DamageType.True, DamageSourceKind.Basic),
                "scripted turret exception remains");
        }
    }

    [Check("minions-live-opaque-wall-team-sight")]
    public static void LaneMinionsAcquireOnlyAfterRealTeamSight()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var provider = match.Hero(0);
            var enemy = match.Hero(1);
            var (near, beyond) = OpaqueWallSides(match, provider, enemy);
            provider.Position = near;
            enemy.Position = beyond;
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team, near)
            {
                Name = "SightCaster", MinionType = MinionType.Caster,
                Created = true, Visible = true, Health = 500, MaxHealth = 500,
                Radius = 35, Speed = 0, AcquisitionRange = 450,
                AttackRange = 550, AttackDamage = 20, AttackPeriod = 1,
                AttackWindup = .1, MissileSpeed = 1200,
            });
            var brushOnly = new WorldMinionVisionService(runtime.World, runtime.Navigation,
                _ => Array.Empty<VisionProvider>());
            TestAssert.True(brushOnly.HasBrushVision(team, enemy),
                $"{team}: old brush-only query accepts the nonbrush wall target");
            runtime.Delivery.Flush();
            TestAssert.False(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                $"{team}: replication hides the enemy beyond the opaque wall");
            runtime.Minions!.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)null, minion.AttackTarget,
                $"{team}: no acquisition or route toward an unseen enemy");

            // A living allied hero on the far side is a legitimate team provider.
            provider.Position = beyond;
            runtime.Delivery.Flush();
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                $"{team}: the far-side ally reveals the enemy to the team");
            runtime.Minions.Tick(.25);
            TestAssert.Equal((uint?)enemy.NetId, minion.AttackTarget,
                $"{team}: visible enemy is acquired");

            // Start a real basic attack and let its windup finish while the
            // far-side provider is present. Target loss must retire aggro,
            // not a missile that has already left the minion.
            TestAssert.True(runtime.Combat.QueueBasicAttack(minion, enemy),
                $"{team}: visible caster starts a committed basic shot");
            runtime.Combat.CombatTick(.11, regenerate: false);
            var shot = runtime.Combat.PendingHits.Single(hit =>
                hit.SourceNetId == minion.NetId && hit.TargetNetId == enemy.NetId);
            TestAssert.True(shot.WindupRemaining <= 0.0 && shot.MissileNetId != 0,
                $"{team}: basic missile has passed its windup");
            runtime.Combat.CombatTick(1.0 / 30.0, regenerate: false);

            provider.Position = near;
            runtime.Delivery.Flush();
            TestAssert.False(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
            runtime.Minions.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)null, minion.AttackTarget,
                $"{team}: retained target is released as soon as team sight is lost");
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.MissileNetId == shot.MissileNetId),
                $"{team}: target loss keeps an already launched basic missile");
            var health = enemy.Health;
            runtime.Combat.CombatTick(.2, regenerate: false);
            TestAssert.True(enemy.Health < health,
                $"{team}: launched basic missile lands after target disappears through terrain");
        }
    }

    [Check("pets-live-shaco-box-opaque-wall-wake-and-fear")]
    public static void BoxNeedsSightToWakeButFearStillSweepsItsArea()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team, extraEnemy: true);
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var hidden = match.Hero(1);
            var visible = match.Hero(2);
            var (near, beyond) = OpaqueWallSides(match, shaco, hidden);
            shaco.Position = near;
            hidden.Position = beyond;
            visible.Position = new Position2(11000, 11000);
            match.CastGround(0, near, 1);
            runtime.Tick(3.0);
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            TestAssert.True(double.Hypot(hidden.Position.X - box.Position.X,
                    hidden.Position.Z - box.Position.Z) <= 400.0,
                $"{team}: the wall-hidden enemy remains inside the box's broad fear sweep");
            TestAssert.False(ShacoScriptSupport.IsAwake(box),
                $"{team}: opaque nonbrush enemy does not wake the box");
            TestAssert.False(runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.SpellName == "ShacoBoxSpell"),
                $"{team}: box does not fire through the wall");

            visible.Position = near;
            TestAssert.True(double.Hypot(visible.Position.X - box.Position.X,
                    visible.Position.Z - box.Position.Z) <= 350.0,
                $"{team}: the clear-side enemy is inside the box's wake radius");
            runtime.Delivery.Flush();
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(visible.NetId),
                $"{team}: the clear-side wake target is visible to the whole team");
            runtime.Tick(1.0 / 30.0);
            TestAssert.True(ShacoScriptSupport.IsAwake(box),
                $"{team}: visible enemy wakes the box");
            TestAssert.True(hidden.Buffs.Any(buff => buff.Name == "Fear"),
                $"{team}: valid wake retains the shipped broad fear sweep");
        }
    }

    [Check("pets-live-shaco-box-next-shot-needs-sight-and-launched-hit")]
    public static void BoxSelectsNextShotOnlyWithSightAndKeepsLaunchedMissile()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var enemy = match.Hero(1);
            var (near, beyond) = OpaqueWallSides(match, shaco, enemy);
            shaco.Position = beyond;
            enemy.Position = beyond;
            match.CastGround(0, near, 1);
            for (var step = 0; step < 120 && !runtime.Spells.SharedProjectiles.Any(projectile =>
                     projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true }); step++)
                runtime.Tick(1.0 / 30.0);
            var launched = runtime.Spells.SharedProjectiles.Single(projectile =>
                projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true }).Snapshot;
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            foreach (var fear in enemy.Buffs.Where(buff => buff.Name == "Fear").ToArray())
                runtime.SpellBuffs.Remove(enemy, fear.Slot);
            enemy.ForcedMove = null;
            enemy.Waypoints.Clear();
            enemy.Position = beyond;
            var health = enemy.Health;
            shaco.Position = near;
            runtime.Delivery.Flush();
            TestAssert.False(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                $"{team}: the opaque wall removes team sight after launch");
            for (var step = 0; step < 90 && runtime.Spells.SharedProjectiles.Any(projectile =>
                     projectile.Snapshot.MissileNetId == launched.MissileNetId); step++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.False(runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.MissileNetId == launched.MissileNetId));
            TestAssert.True(enemy.Health < health,
                $"{team}: launched homing shot still hits after target leaves team sight");

            // The stock visible-unit iterator governs the next acquisition.
            // It does not establish whether a scripted cast already in windup cancels.
            runtime.Tick(.7);
            TestAssert.True(box.Alive && ShacoScriptSupport.IsAwake(box),
                $"{team}: the hidden-target control ran while the box was active");
            TestAssert.False(runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.SpellName == "ShacoBoxSpell"),
                $"{team}: active box does not select another shot without team sight");

            shaco.Position = beyond;
            for (var step = 0; step < 30 && !runtime.Spells.SharedProjectiles.Any(projectile =>
                     projectile.Snapshot.SpellName == "ShacoBoxSpell"); step++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.SpellName == "ShacoBoxSpell"),
                $"{team}: sight restoration allows the next box shot");
        }
    }

    private static (Position2 Near, Position2 Beyond) OpaqueWallSides(
        Match match, Entity provider, Entity target)
    {
        var runtime = match.Runtime;
        var grid = runtime.Navigation;
        var dependencies = runtime.Dependencies;
        var originalProvider = provider.Position;
        var originalTarget = target.Position;
        var turrets = runtime.World.Entities.Values.Where(unit =>
            unit.Alive && unit.UnitType == UnitType.Turret).ToArray();
        static double DistanceSquared(Position2 a, Position2 b) =>
            (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
        try
        {
            // The former fixed cell pair was opaque to its nearby hero, but
            // Order's allied turret still revealed the far side. Select a
            // genuine nonbrush Map1 wall pair under the *whole team* rule.
            // Playtest #10 (25 Sept 2026): "nonbrush" holds for both units'
            // SelectionRadius brush area, not only the centre cell.
            double[] radii = [provider.SelectionRadius, target.SelectionRadius];
            for (var x = 90; x <= 190; x++)
            for (var z = 80; z <= 190; z++)
            {
                var near = grid.CellToWorld(new NavCell(x, z));
                if (!grid.IsPathingWalkable(near) || grid.BrushIdAt(near) is not null ||
                    radii.Any(radius => grid.BrushIdAt(near, radius) is not null) ||
                    !grid.HasVisionLos(near, near))
                    continue;
                foreach (var (dx, dz) in new[] { (0, 4), (4, 0), (0, -4), (-4, 0) })
                {
                    var beyond = grid.CellToWorld(new NavCell(x + dx, z + dz));
                    if (!grid.IsPathingWalkable(beyond) || grid.BrushIdAt(beyond) is not null ||
                        radii.Any(radius => grid.BrushIdAt(beyond, radius) is not null) ||
                        !grid.HasVisionLos(beyond, beyond) || grid.HasVisionLos(near, beyond)) continue;
                    if (turrets.Any(turret =>
                            DistanceSquared(near, turret.Position) < 950 * 950 ||
                            DistanceSquared(beyond, turret.Position) < 950 * 950)) continue;
                    provider.Position = near;
                    target.Position = beyond;
                    var providers = VisibilityRules.BuildTeamProviders(runtime.World, provider.Team,
                        runtime.Delivery.Balls, dependencies.AdditionalProviders);
                    if (VisibilityRules.RevealsEntity(runtime.World, provider.Team,
                            dependencies.VisionEnvironment, providers,
                            point => dependencies.RevealsStealth(provider.Team, point), target)) continue;
                    return (near, beyond);
                }
            }
        }
        finally
        {
            provider.Position = originalProvider;
            target.Position = originalTarget;
        }
        throw new InvalidOperationException(
            "shipped Map1 has no walkable nonbrush opaque wall pair hidden from the whole team");
    }

}
