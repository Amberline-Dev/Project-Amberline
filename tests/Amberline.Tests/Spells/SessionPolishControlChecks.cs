using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Source controls for the remaining session-polish boundaries. These use
/// the production graph and real spell, combat, movement, and pet services.
/// </summary>
public static class SessionPolishControlChecks
{
    [Check("session-polish-teleport-rejects-turret-movement-stamp")]
    public static void TeleportRejectsTurretMovementStampAndEscapes()
    {
        var runtime = CreateRuntime(new AnnieChecks.FixedRandomSource(0.25));
        var turretPosition = MapDefinitions.TurretPositions["Turret_T1_C_04"];
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var turret = Unit(runtime, EntityKind.Turret, UnitType.Turret, Team.Order,
            turretPosition, MapDefinitions.TurretRadius);
        turret.Name = "Turret_T1_C_04";

        // playtest B4 / retail sweep, 27 Sept 2026: the turret radius is now
        // 88.4 rather than 88. Use 137 units to clear its body while remaining
        // inside the independent 140-unit movement stamp.
        var oldLanding = ClientPoint(runtime.Navigation,
            turretPosition + new Position2(0.0, 137.0));
        TestAssert.True(runtime.Navigation.IsWalkable(oldLanding),
            "the old Teleport predicate accepts the Map1 turret ring point");
        TestAssert.False(runtime.Navigation.IsMovementWalkable(oldLanding),
            "the same ring point is blocked by the client movement turret stamp");
        var turretClearance = runtime.Movement.BodyClearance(hero, turret);
        TestAssert.True(DistanceSquared(oldLanding, turret.Position) >=
            turretClearance * turretClearance,
            "the old ring point also passes Teleport body clearance");
        TestAssert.True(Cast(runtime, hero, 0, turretPosition, turret.NetId),
            "Teleport cast must accept a real Map1 turret target");

        Advance(runtime, 4.0);

        TestAssert.True(hero.TeleportId > 0,
            "Teleport completes after its production channel");
        TestAssert.True(runtime.Navigation.IsMovementWalkable(hero.Position),
            "completed Teleport rejects the movement-blocked turret ring point");
        TestAssert.True(hero.Position != oldLanding,
            "completed Teleport does not use the old raw-terrain landing");

        var escapeGoal = new Position2(turretPosition.X - 400.0, turretPosition.Z);
        var route = runtime.Movement.SetMoveOrder(hero.NetId, escapeGoal);
        TestAssert.True(route.Count > 1,
            "a hero can issue an immediate movement order after Teleport");
        var beforeMove = hero.Position;
        runtime.Movement.Tick(0.1);
        TestAssert.True(DistanceSquared(hero.Position, beforeMove) > 0.0,
            "the immediate movement order escapes the turret landing");
        TestAssert.True(runtime.Navigation.IsMovementWalkable(hero.Position),
            "the escape step remains on the movement grid");
    }

    [Check("session-polish-teleport-completion-revalidates-body-clearance")]
    public static void TeleportCompletionRevalidatesBodyClearance()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var target = Unit(runtime, EntityKind.Minion, UnitType.Minion, Team.Order,
            hero.Position + new Position2(300.0, 0.0), radius: 35.0);

        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "Teleport cast must accept a live allied minion");
        // The first random 50-100-unit landing was checked before this body
        // existed. Completion must run the landing query again and choose a
        // safe target-relative ring point instead of committing that stale
        // request through the newly occupied footprint.
        var blocker = Unit(runtime, EntityKind.Inhibitor, UnitType.Structure,
            Team.Chaos, target.Position, radius: 150.0);
        Advance(runtime, 4.0);

        TestAssert.True(hero.TeleportId > 0,
            "Teleport completes after its production channel");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "completed Teleport removes the channel marker");
        var clearance = runtime.Movement.BodyClearance(hero, blocker);
        TestAssert.True(DistanceSquared(hero.Position, blocker.Position) >=
            clearance * clearance,
            "completion revalidates the landing against bodies added during channel");
        TestAssert.True(runtime.Navigation.IsMovementWalkable(hero.Position),
            "completion keeps the reselected landing on a movement-walkable cell");
    }

    [Check("session-polish-teleport-freezes-windup-preserves-missile")]
    public static void TeleportFreezesMinionWindupButPreservesLaunchedMissile()
    {
        var windupRuntime = CreateRuntime();
        var windupCaster = Hero(windupRuntime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var windupMinion = Unit(windupRuntime, EntityKind.Minion, UnitType.Minion,
            Team.Order, windupCaster.Position + new Position2(300.0, 0.0), 35.0);
        ConfigureAttacker(windupMinion);
        var windupVictim = Hero(windupRuntime, Team.Chaos,
            windupMinion.Position + new Position2(100.0, 0.0), ["SummonerDot"]);
        TestAssert.True(windupRuntime.Combat.QueueBasicAttack(windupMinion,
            windupVictim), "a minion positive windup must be queueable");
        TestAssert.True(windupRuntime.Combat.PendingHits.Any(hit =>
            hit.SourceNetId == windupMinion.NetId && hit.WindupRemaining > 0.0),
            "the positive-windup control must remain pending before Teleport");
        TestAssert.True(Cast(windupRuntime, windupCaster, 0,
            windupMinion.Position, windupMinion.NetId),
            "Teleport must start on the windup minion");
        TestAssert.False(windupRuntime.Combat.PendingHits.Any(hit =>
            hit.SourceNetId == windupMinion.NetId),
            "freezing the Teleport target cancels its positive windup");

        var missileRuntime = CreateRuntime();
        var missileCaster = Hero(missileRuntime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var missileMinion = Unit(missileRuntime, EntityKind.Minion, UnitType.Minion,
            Team.Order, missileCaster.Position + new Position2(300.0, 0.0), 35.0);
        ConfigureAttacker(missileMinion);
        var missileVictim = Hero(missileRuntime, Team.Chaos,
            missileMinion.Position + new Position2(400.0, 0.0), ["SummonerDot"]);
        TestAssert.True(missileRuntime.Combat.QueueBasicAttack(missileMinion,
            missileVictim), "the launched-missile control must be queueable");
        missileRuntime.Combat.CombatTick(missileMinion.AttackWindup + 0.01);
        var launched = missileRuntime.Combat.PendingHits.Single(hit =>
            hit.SourceNetId == missileMinion.NetId);
        TestAssert.True(launched.WindupRemaining <= 0.0 && launched.MissileNetId != 0,
            "the control must cross the windup boundary before Teleport");
        TestAssert.True(Cast(missileRuntime, missileCaster, 0,
            missileMinion.Position, missileMinion.NetId),
            "Teleport must start on the launched-missile minion");
        var retained = missileRuntime.Combat.PendingHits.Single(hit =>
            hit.SourceNetId == missileMinion.NetId);
        TestAssert.Equal(launched.MissileNetId, retained.MissileNetId,
            "Teleport target freezing keeps the launched missile identity");
        TestAssert.True(retained.WindupRemaining <= 0.0,
            "Teleport target freezing does not restore a launched windup");
        missileRuntime.Spells.Tick(4.0);
        missileRuntime.Combat.CombatTick(1.0);
        TestAssert.True(missileVictim.Health < missileVictim.MaxHealth,
            "a launched minion missile still resolves after target freezing");
    }

    [Check("session-polish-ghoul-brush-acquisition-loss")]
    public static void GhoulAcquiresAndLosesBrushTargetThroughProductionVision()
    {
        var runtime = CreateRuntime();
        var grid = runtime.Navigation;
        var (brush, outside, targetPosition) = FindBrushCase(grid,
            GameData.Default.Champion("YorickSpectralGhoul").SelectionRadius);
        // Champion sweep YOR-04, 27 Sept 2026: keep the owner within the
        // 2500-unit teleport leash, outside the brush so only the ghoul reveals it.
        var owner = Hero(runtime, Team.Order, outside,
            ["SummonerDot"]);
        owner.ChampionName = "Yorick";
        var target = Hero(runtime, Team.Chaos, targetPosition, ["SummonerDot"]);
        var ghoul = YorickScriptSupport.SpawnUnit(runtime,owner, "Spectral", brush, 0.0);
        // Batch 146: YorickPHPet.lua's TimerFindEnemies looks for a fight around the hero it
        // fights for (FindTargetInAcRUsingGoldRedirectTarget), not around the ghoul, so the owner
        // stands inside the ghoul's acquisition range of the target or the scan finds nobody. The
        // brush case is unchanged: he is still outside it, so only the ghoul reveals the target.
        owner.Position = OwnerStand(grid, owner, target, ghoul.SelectionRadius);
        TestAssert.True(DistanceSquared(owner.Position, ghoul.Position) <= 2500.0 * 2500.0);
        TestAssert.False(grid.HasUnitVision(owner.Position, owner.SelectionRadius,
            target.Position, target.SelectionRadius), "the owner cannot reveal the brush target");

        runtime.Pets.Tick(0.5, 0.5);
        TestAssert.Equal(target.NetId, ghoul.AttackTarget ?? 0u,
            "a ghoul in the target brush acquires through its real vision service");

        ghoul.Position = outside;
        runtime.Pets.Tick(1.0, 0.5);
        TestAssert.Equal<uint?>(null, ghoul.AttackTarget,
            "leaving the brush removes the target when no allied provider sees it");
        // Restated in batch 254 (was: the route ends over 100 units from the target): the owner stands 50 units from the target, and
        // the engine planner's end-cell snap ends the return to him beside both, so the check names the walk's request instead.
        TestAssert.True(ghoul.Waypoints.Count == 0 ||
            runtime.Pets.LastDestinations.GetValueOrDefault(ghoul.NetId) == owner.Position,
            "brush vision loss clears the old enemy route while allowing owner follow");

        ghoul.Position = brush;
        runtime.Pets.Tick(1.5, 0.5);
        TestAssert.Equal(target.NetId, ghoul.AttackTarget ?? 0u,
            "returning to the brush reacquires the target");
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        // Batch 150: Teleport's channel and its completion are the shipped SummonerTeleport buff's own
        // OnBuffDeactivate, so the drive order is the production one — the spell phase first, then every
        // entity's buff lifecycle (the order ProductionSimulationDriver.Tick runs them in).
        runtime.Spells.Tick(seconds);
        foreach (var entity in runtime.World.Entities.Values.ToArray())
            runtime.Buffs.TickBuffs(entity, seconds);
    }

    private static MatchRuntime CreateRuntime(IRandomSource? random = null) =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            solo: true, random: random);

    private static Position2 SpawnPoint() =>
        new(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);

    private static Entity Hero(MatchRuntime runtime, Team team, Position2 position,
        IReadOnlyList<string> spells)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, position)
        {
            Name = team == Team.Order ? "SessionOrder" : "SessionChaos",
            ChampionName = "Annie", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0, Mana = 1000.0, MaxMana = 1000.0,
            Level = 1, Speed = 325.0, Radius = 35.0, AttackDamage = 100.0,
            AttackRange = 550.0, AttackPeriod = 1.6, AttackWindup = 0.3,
        };
        hero.SummonerSpells = spells.ToList();
        runtime.World.Register(hero);
        return hero;
    }

    private static Entity Unit(MatchRuntime runtime, EntityKind kind, UnitType type,
        Team team, Position2 position, double radius)
    {
        var unit = new Entity(runtime.World.Allocate(type), kind, type, team, position)
        {
            Name = type == UnitType.Structure ? "SessionStructure" : "SessionMinion",
            ChampionName = "Annie", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0, Radius = radius,
        };
        runtime.World.Register(unit);
        return unit;
    }

    private static void ConfigureAttacker(Entity minion)
    {
        minion.AttackDamage = 100.0;
        minion.AttackRange = 500.0;
        minion.AttackPeriod = 1.6;
        minion.AttackWindup = 0.25;
        minion.MissileSpeed = 600.0;
        minion.AiState = "engage";
    }

    private static bool Cast(MatchRuntime runtime, Entity caster, int slot,
        Position2 point, uint target) => runtime.Spells.HandleCastRequest(caster,
            EncodeCast(slot, point, target), runtime.World.Clock.Now());

    private static byte[] EncodeCast(int slot, Position2 point, uint target)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        payload[5] = checked((byte)((slot << 1) | 1));
        WriteSingle(payload, 6, (float)point.X);
        WriteSingle(payload, 10, 0.0f);
        WriteSingle(payload, 14, (float)point.Z);
        WriteSingle(payload, 18, (float)point.X);
        WriteSingle(payload, 22, 0.0f);
        WriteSingle(payload, 26, (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));

    private static Position2 ClientPoint(NavigationGrid grid, Position2 point) =>
        Geometry.ClientWaypoints([point], new NavigationBounds(grid.MinX, grid.MaxX,
            grid.MinZ, grid.MaxZ))[0];

    /// <summary>
    /// The nearest stand beside the target that is outside the brush and cannot see into it: the
    /// owner of a ghoul has to be close enough for the ghoul's acquisition scan to reach the target
    /// (it runs around him, see the call site) while only the ghoul reveals it.
    /// </summary>
    private static Position2 OwnerStand(NavigationGrid grid, Entity owner, Entity target,
        double ghoulRadius)
    {
        for (var ring = 1; ring <= 12; ring++)
        foreach (var step in Enumerable.Range(0, 16))
        {
            var angle = step * Math.PI / 8.0;
            var radius = ring * 50.0;
            var point = target.Position + new Position2(Math.Cos(angle) * radius,
                Math.Sin(angle) * radius);
            if (grid.BrushIdAt(point, ghoulRadius) is not null) continue;
            if (grid.HasUnitVision(point, owner.SelectionRadius, target.Position,
                    target.SelectionRadius))
                continue;
            return point;
        }
        throw new InvalidOperationException(
            "no stand outside the brush within the ghoul's acquisition range of the target");
    }

    private static (Position2 Brush, Position2 Outside, Position2 Target) FindBrushCase(
        NavigationGrid grid, double ghoulRadius)
    {
        // Playtest #10 (25 Sept 2026): the ghoul's brush follows its
        // SelectionRadius, so "outside" must leave the brush by that rule too.
        var anchors = Enumerable.Range(0, grid.BrushLabels.Count)
            .Where(index => grid.BrushLabels[index] != 0)
            .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX,
                index / grid.CountX)))
            .Where(point => point.X is > 5000 and < 9000 &&
                point.Z is > 5000 and < 9000 && grid.IsMovementWalkable(point))
            .ToArray();
        foreach (var brush in anchors)
        {
            var brushId = grid.BrushIdAt(brush);
            var outside = brush + new Position2(-250.0, 0.0);
            if (brushId is null || !grid.IsMovementWalkable(outside) ||
                grid.BrushIdAt(outside) is not null ||
                grid.BrushIdAt(outside, ghoulRadius) is not null ||
                grid.BrushIdAt(brush, ghoulRadius) != brushId) continue;
            var target = Enumerable.Range(-20, 41).SelectMany(x =>
                    Enumerable.Range(-20, 41).Select(z => brush +
                        new Position2(x * grid.CellSize * 0.5,
                            z * grid.CellSize * 0.5)))
                .Where(point => point != brush && grid.IsMovementWalkable(point) &&
                    grid.BrushIdAt(point) == brushId &&
                    grid.HasVisionLos(brush, point) &&
                    DistanceSquared(point, brush) >= 500.0 * 500.0)
                .OrderBy(point => DistanceSquared(point, brush))
                .FirstOrDefault();
            if (target != default) return (brush, outside, target);
        }
        throw new InvalidOperationException("Map1 has no usable brush control case");
    }

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);
}
