using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 731: what an idle champion attacks on its own. Hero.luaobj's 0.2 s TimerDistanceScan (proto 0.13 pc 0015-0029, and
/// pc 0037-0054 once a move has stopped) and its TimerCheckAttack re-acquisition (proto 0.12 pc 0037-0061) take what
/// FindTargetInAcR returns only when <c>CanSeeMe(target)</c> holds: the target's side must see the hero. A camp monster's side
/// sees through each monster's own CharacterData perception bubble (the client, makes it with the unit's
/// own team) and through the reveal an attack on a camp leaves (the neutral slot of the reveal routine).
/// </summary>
public static class HeroIdleAcquisitionTargetChecks
{
    // A champion's idle scan runs every 0.2 s; three periods are ample.
    private const double ScanWindow = 0.6;

    [Check("movement.hero-idle-acquisition-unit-kinds")]
    public static void IdleHeroAcquiresMinionAndTurret()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var enemy = team == Team.Order ? Team.Chaos : Team.Order;
            using (var runtime = Start(0.0))
            {
                var hero = HeroOf(runtime, team);
                Park(runtime, enemy);
                hero.Position = new Position2(7000, 7000);
                var minion = runtime.Waves!.SpawnFixture(enemy, MinionType.Melee, 0, "C", 0, 0);
                minion.Position = hero.Position + new Position2(300, 0);
                runtime.Delivery.Flush();
                TickFor(runtime, ScanWindow);
                TestAssert.Equal(minion.NetId, hero.AttackTarget ?? 0u, $"{team}: an idle hero takes an enemy minion in its acquisition range");
            }
            using (var runtime = Start(0.0))
            {
                var hero = HeroOf(runtime, team);
                Park(runtime, enemy);
                var turret = runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Turret && unit.Team == enemy &&
                        runtime.Combat.IsTargetable(unit, hero))
                    .OrderBy(unit => Distance(unit.Position, new Position2(7000, 7000))).First();
                var away = Normalized(new Position2(7000, 7000) - turret.Position);
                hero.Position = turret.Position + new Position2(away.X * 450, away.Z * 450);
                hero.MaxHealth = hero.Health = 1_000_000;
                runtime.Delivery.Flush();
                TickFor(runtime, ScanWindow);
                TestAssert.Equal(turret.NetId, hero.AttackTarget ?? 0u, $"{team}: an idle hero takes an enemy turret in its acquisition range");
            }
        }
    }

    [Check("movement.hero-idle-acquisition-camp-in-sight")]
    public static void IdleHeroAcquiresCampThatSeesIt()
    {
        using var runtime = Start(899.9);
        var monsters = SpawnedCamps(runtime);
        foreach (var (team, campId) in new[] { (Team.Order, 2), (Team.Chaos, 8) })
        {
            var hero = HeroOf(runtime, team);
            var camp = monsters.Where(unit => unit.CampId == campId).ToArray();
            hero.Position = OpenSpot(runtime, camp[0], 300.0);
        }
        runtime.Delivery.Flush();
        TickFor(runtime, ScanWindow);
        foreach (var (team, campId) in new[] { (Team.Order, 2), (Team.Chaos, 8) })
        {
            var hero = HeroOf(runtime, team);
            var target = hero.AttackTarget is uint id ? runtime.World.Get(id) : null;
            TestAssert.True(target is { Kind: EntityKind.Neutral } && target.CampId == campId,
                $"{team}: an idle hero 300 units from camp {campId}, in the open, takes one of its monsters " +
                $"(target {target?.Name ?? "none"}; {Gates(runtime, hero, monsters.First(unit => unit.CampId == campId))})");
        }

        // The gate stays the camp's sight, not a blanket yes: a stealthed hero the camp cannot see takes nothing (CanSeeMe).
        using var hidden = Start(899.9);
        var camp2 = SpawnedCamps(hidden).Where(unit => unit.CampId == 2).ToArray();
        var stealthed = HeroOf(hidden, Team.Order);
        Park(hidden, Team.Chaos);
        stealthed.Position = OpenSpot(hidden, camp2[0], 300.0);
        stealthed.Buffs.Add(new Amberline.Core.Buffs.BuffState("IdleAcquisitionHidden", slot: 60, expiresAt: 10_000)
            { Payload = new Amberline.Core.Buffs.BuffPayload { Stealthed = true } });
        hidden.Delivery.Flush();
        TickFor(hidden, ScanWindow);
        TestAssert.True(stealthed.AttackTarget is null && hidden.Session.CurrentTargetVisible!(stealthed, camp2[0]),
            $"a stealthed hero that sees the camp but is not seen by it stays idle ({Gates(hidden, stealthed, camp2[0])})");
    }

    [Check("movement.hero-idle-acquisition-aggroed-camp")]
    public static void IdleHeroFightsBackAggroedCamp()
    {
        foreach (var (team, campId) in new[] { (Team.Order, 2), (Team.Chaos, 8) })
        {
            using var runtime = Start(899.9);
            var monsters = SpawnedCamps(runtime);
            var hero = HeroOf(runtime, team);
            Park(runtime, team == Team.Order ? Team.Chaos : Team.Order);
            hero.MaxHealth = hero.Health = 1_000_000;
            var wolf = monsters.Single(unit => unit.CampId == campId && unit.Name == "GiantWolf");
            hero.Position = OpenSpot(runtime, wolf, 300.0);
            runtime.Delivery.Flush();
            Order(runtime, hero, 3, wolf.Position, wolf.NetId);
            TickFor(runtime, 1.5);
            TestAssert.True(wolf.Health < wolf.MaxHealth && wolf.NeutralState == "engage", $"{team}: the ordered attack aggroes the camp");
            // A short walk ends the fight (Hero.luaobj OnOrder MOVETO); the hero stands again where the camp keeps hitting it.
            var away = Normalized(hero.Position - wolf.Position);
            Order(runtime, hero, 2, hero.Position + new Position2(away.X * 60, away.Z * 60), 0);
            var arrived = false;
            for (var step = 0; step < 30 && !arrived; step++)
            {
                runtime.Tick(0.1);
                arrived = hero.Waypoints.Count == 0;
            }
            TestAssert.True(arrived, $"{team}: the move ends");
            TickFor(runtime, ScanWindow);
            TestAssert.True(IsCampMonster(runtime, hero.AttackTarget, campId),
                $"{team}: once the move ends the idle hero fights the camp that is hitting it ({Gates(runtime, hero, wolf)})");
            // Past the 4.5 s ca_RevealAttackerTimeOut of its last attack the camp still sees it from its own bubbles.
            TickFor(runtime, 6.0);
            TestAssert.True(IsCampMonster(runtime, hero.AttackTarget, campId),
                $"{team}: the hero keeps fighting the camp ({Gates(runtime, hero, wolf)})");
            // Stop ends auto-attacking until the player's next order (the stock S key sends order 10; Hero.luaobj leaves it to the
            // engine), even under the camp's attacks; any new order brings the idle scan back.
            Order(runtime, hero, 10, hero.Position, 0);
            TickFor(runtime, 1.0);
            TestAssert.True(hero.AttackTarget is null && hero.Alive, $"{team}: after a stop the hero does not fight back");
            Order(runtime, hero, 2, hero.Position, 0);
            TickFor(runtime, ScanWindow);
            TestAssert.True(IsCampMonster(runtime, hero.AttackTarget, campId),
                $"{team}: a fresh move order ends at once and the idle scan takes the camp again ({Gates(runtime, hero, wolf)})");
        }

        // A camp pulled with a spell: once the cast gesture's hold (owner rule AE6) is over the idle hero fights it.
        foreach (var (team, campId) in new[] { (Team.Order, 2), (Team.Chaos, 8) })
        {
            using var runtime = Start(899.9);
            var monsters = SpawnedCamps(runtime);
            var hero = HeroOf(runtime, team);
            Park(runtime, team == Team.Order ? Team.Chaos : Team.Order);
            hero.MaxHealth = hero.Health = 1_000_000;
            hero.SpellRanks[0] = 1;
            hero.Mana = hero.MaxMana;
            var wolf = monsters.Single(unit => unit.CampId == campId && unit.Name == "GiantWolf");
            hero.Position = OpenSpot(runtime, wolf, 300.0);
            runtime.Delivery.Flush();
            Cast(runtime, hero, 0, wolf);
            TickFor(runtime, 1.5);
            TestAssert.True(wolf.Health < wolf.MaxHealth && wolf.NeutralState == "engage", $"{team}: Disintegrate aggroes the camp");
            TickFor(runtime, ScanWindow);
            TestAssert.True(IsCampMonster(runtime, hero.AttackTarget, campId),
                $"{team}: after the cast the idle hero fights the camp it pulled ({Gates(runtime, hero, wolf)})");
        }
    }

    private static void Cast(MatchRuntime runtime, Entity hero, int slot, Entity target)
    {
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = (byte)(slot << 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
        var connection = runtime.Session.Participants.Single(seat => seat.Hero == hero).PlayerId;
        TestAssert.True(runtime.Session.DispatchGameplay(checked((long)connection), 1, request, runtime.Dependencies.Gameplay),
            $"cast {slot} is accepted");
        runtime.Delivery.Flush();
    }

    [Check("movement.hero-idle-acquisition-camp-sees-attack-reveal")]
    public static void CampSideSeesTheRevealOfAnAttackOnIt()
    {
        // The reveal routine (CombatService.RevealAttack, ca_RevealAttackerTimeOut 4.5 s, ca_RevealAttackerRange 400) starts its
        // bubble for the attacked unit's team, the neutral slot for a camp. Far from every camp's own bubble, that reveal alone is
        // what lets the camp's side see the attacker, which is what CanSeeMe asks before a soft engage.
        using var runtime = Start(899.9);
        var monsters = SpawnedCamps(runtime);
        var hero = HeroOf(runtime, Team.Order);
        Park(runtime, Team.Chaos);
        hero.Position = new Position2(7000, 7000);
        TestAssert.True(monsters.All(unit => Distance(unit.Position, hero.Position) > 1400.0), "no camp bubble reaches the spot");
        var engine = runtime.Spells.HeroOrders!.Engine;
        var monster = monsters[0];
        TestAssert.False(engine.CanSeeMe(hero, monster), "unrevealed, the camp's side does not see the hero");
        runtime.Combat.RevealAttack(hero, monster);
        TestAssert.True(engine.CanSeeMe(hero, monster), "the attack's reveal shows the hero to the camp's side");
        TickFor(runtime, 4.6);
        TestAssert.False(engine.CanSeeMe(hero, monster), "and lapses after ca_RevealAttackerTimeOut");
    }

    private static bool IsCampMonster(MatchRuntime runtime, uint? netId, int campId) =>
        netId is uint id && runtime.World.Get(id) is { Kind: EntityKind.Neutral, Alive: true } monster && monster.CampId == campId;

    /// <summary>A walkable spot outside brush, in the monster's line of sight, <paramref name="distance"/> units from it.</summary>
    private static Position2 OpenSpot(MatchRuntime runtime, Entity monster, double distance)
    {
        var grid = runtime.Navigation;
        for (var index = 0; index < 16; index++)
        {
            var angle = index * Math.PI / 8.0;
            var candidate = monster.Position + new Position2(Math.Cos(angle) * distance, Math.Sin(angle) * distance);
            if (grid.IsMovementWalkable(candidate) && grid.BrushIdAt(candidate) is null &&
                grid.HasUnitVision(monster.Position, monster.SelectionRadius, candidate, 65.0))
                return candidate;
        }
        throw new InvalidOperationException($"no open spot {distance} units from {monster.Name} of camp {monster.CampId}");
    }

    private static IReadOnlyList<Entity> SpawnedCamps(MatchRuntime runtime)
    {
        Entity[] Monsters() => runtime.World.Entities.Values
            .Where(unit => unit.Kind == EntityKind.Neutral && unit.Alive).OrderBy(unit => unit.NetId).ToArray();
        // The level script spawns the delayed camps on its own clock (GroupDelaySpawnTime after the 100 s initial spawn).
        for (var step = 0; step < 3000 && !Monsters().Any(unit => unit.CampId == 8 && unit.Name == "GiantWolf"); step++)
            runtime.Tick(0.1);
        TestAssert.True(Monsters().Any(unit => unit.CampId == 8 && unit.Name == "GiantWolf"), "the camps have spawned");
        return Monsters();
    }

    private static string Gates(MatchRuntime runtime, Entity hero, Entity target)
    {
        var orders = runtime.Spells.HeroOrders!;
        return $"state {orders.Engine.AiState(hero)}, auto {hero.HeroAutoAcquire}, blocked {orders.Blocked(hero)}, " +
            $"eligible {orders.Eligible(hero, target)}, hero sees {runtime.Session.CurrentTargetVisible!(hero, target)}, " +
            $"seen by its side {orders.Engine.CanSeeMe(hero, target)}, distance {Distance(hero.Position, target.Position):F0}";
    }

    private static void Order(MatchRuntime runtime, Entity hero, byte order, Position2 destination, uint target)
    {
        var connection = runtime.Session.Participants.Single(seat => seat.Hero == hero).PlayerId;
        var packet = ClientGameplayPackets.MoveOrder(hero.NetId, order, destination, target);
        TestAssert.True(runtime.Session.DispatchGameplay(checked((long)connection), 1, packet.Plaintext.Span,
            runtime.Dependencies.Gameplay), $"order {order} is accepted");
        runtime.Delivery.Flush();
    }

    private static void TickFor(MatchRuntime runtime, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.1) runtime.Tick(0.1);
    }

    private static Entity HeroOf(MatchRuntime runtime, Team team) =>
        runtime.Session.Participants.Single(seat => seat.Team == team).Hero!;

    /// <summary>Moves the other side's hero out of everyone's way.</summary>
    private static void Park(MatchRuntime runtime, Team team) =>
        HeroOf(runtime, team).Position = team == Team.Order ? new Position2(1500, 1500) : new Position2(12500, 12500);

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static Position2 Normalized(Position2 vector)
    {
        var length = Math.Sqrt(vector.X * vector.X + vector.Z * vector.Z);
        return length <= 0.0 ? new Position2(1, 0) : new Position2(vector.X / length, vector.Z / length);
    }

    /// <summary>
    /// A production match of Annie (acquisition range 625) against Annie, both seats observed, lane waves off. The clock starts at
    /// <paramref name="seconds"/>: 899.9 lets the scheduler spawn every camp on the first ticks (as neutral.every-member-fights-back-within-2s
    /// does), and the heroes keep their idle acquisition.
    /// </summary>
    private static MatchRuntime Start(double seconds)
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team,
                participant.Hero!.NetId, new Sink(), ready: true, view: participant.View));
        }
        TestAssert.True(session.Start(), "production match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        runtime.World.Clock.SetVirtual(seconds);
        runtime.Tick(0.0);
        return runtime;
    }

    private sealed class Sink : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }
}
