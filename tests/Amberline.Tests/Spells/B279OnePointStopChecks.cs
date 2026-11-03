using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 279: every stop is a one-point path, and the client installs it on receipt (the path install,
    /// any unit, no root test): the actor stopped (it zeroes the stored velocity) and seated on the path's point,
    /// its lattice anchor. Batch 271 seated the hero's stop order and AI stop; the stops below seated the unit only at its
    /// next frame's stop arrival, or never when it was not following a route (batch 271 open question 3).
    /// </summary>
    [Check("movement.one-point-stops-seat-at-once")]
    public static void OnePointStopsSeatAtOnce()
    {
        var failures = new List<string>();
        using (var match = new Match("Annie", Team.Order))
        {
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastGround(0, hero.Position, 3);
            runtime.Tick(.5);
            var pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == hero.NetId);

            // Pet.lua's hold (PetLuaAiEngine.Stop) and the spawned units' stop (TibbersAiSystem.Stop).
            Stop(runtime, "Pet.lua stop", pet, () => runtime.Pets.Engine.Stop(pet));
            Stop(runtime, "spawned-unit stop", pet, () => runtime.Tibbers.Stop(pet));

            // The stationary waypoint of an order the hero cannot walk (ProductionMatchComposition): rooted, then action-locked.
            var root = runtime.Buffs.AddBuff(hero, "SeatRoot", BuffType.Snare, 30, enemy.NetId);
            Stop(runtime, "rooted refusal", hero, () => SendHeroOrder(match, 2, new Position2(7600, 7600)));
            runtime.Buffs.RemoveBuff(hero, root!.Slot);
            var stun = runtime.Buffs.AddBuff(hero, "SeatStun", BuffType.Stun, 30, enemy.NetId);
            Stop(runtime, "action-locked refusal", hero, () => SendHeroOrder(match, 2, new Position2(7600, 7600)));
            runtime.Buffs.RemoveBuff(hero, stun!.Slot);

            // A windup cast takes the hero's order and stops it (BbChampionModule).
            Stop(runtime, "windup cast", hero, () => match.Cast(0, 0, enemy));

            // EndOfGame.luaobj's HaltAllAI stops a walking unit; the zombie life stands the unit up where it is.
            match.Move(1, new Position2(7600, 7400));
            runtime.Tick(.25);
            TestAssert.True(enemy.Waypoints.Count > 0, "fixture: the enemy walks before the halt");
            Stop(runtime, "end-of-game halt", enemy, () => runtime.Dependencies.CombatHooks.HaltAllAi());
            Stop(runtime, "zombie stand-up", enemy, () => runtime.Dependencies.CombatHooks.BeginZombie(enemy, hero.NetId));

            // Leashed's stop of a walking monster (NeutralLuaAiEngine.Stop).
            var neutral = new NeutralLuaAiEngine(runtime.World, runtime.Movement, runtime.Combat, new SeatNeutralSink());
            var monster = runtime.World.Register(new Entity(0x40027901, EntityKind.Monster, UnitType.Monster, Team.Neutral,
                new Position2(8000, 9000)) { Alive = true, Created = true, Visible = true, Speed = 300, Radius = 50 });
            monster.Waypoints = [new Position2(8300, 9000)];
            Stop(runtime, "jungle stop", monster, () => neutral.Stop(monster));
        }

        // A script move's end (BbScriptMoves.Finish): Rocket Jump lands on its point, and the stand published there is installed.
        using (var match = new Match("Tristana", Team.Order))
        {
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            match.CastGround(0, hero.Position + new Position2(400.7, 100.3), 1);
            var dashed = false;
            for (var step = 0; step < 300 && !(dashed && hero.ForcedMove is null); step++)
            {
                runtime.Tick(.01);
                dashed |= hero.ForcedMove is not null;
            }
            TestAssert.True(dashed && hero.ForcedMove is null, "fixture: Rocket Jump dashes and lands");
            if (hero.Position != runtime.Movement.ClientAnchor(hero.Position))
                failures.Add($"script move end: the hero stands on {hero.Position}, not on its anchor {runtime.Movement.ClientAnchor(hero.Position)}");
            if (hero.FrameVelocity != Position2.Zero) failures.Add($"script move end: the stand's install did not stop it ({hero.FrameVelocity})");
        }
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Stop(Amberline.Network.Sessions.MatchRuntime runtime, string name, Entity unit, Action act)
        {
            // Off the lattice and moving.
            unit.Position = new Position2(Math.Floor(unit.Position.X) + .7, Math.Floor(unit.Position.Z) + .3);
            unit.FrameVelocity = new Position2(0, 15);
            var anchor = runtime.Movement.ClientAnchor(unit.Position);
            TestAssert.True(anchor != unit.Position, $"fixture: the unit stands off the lattice before the {name}");
            act();
            if (unit.Waypoints.Count != 0) failures.Add($"{name}: keeps its route");
            if (unit.Position != anchor) failures.Add($"{name}: stands on {unit.Position}, not on the stop's point {anchor}");
            if (unit.FrameVelocity != Position2.Zero) failures.Add($"{name}: the stop's install did not stop it ({unit.FrameVelocity})");
        }
    }

    /// <summary>
    /// Batch 279: the planted-body planner (deleted in batch 271) left its state on <see cref="Entity"/>: the congestion wait, check
    /// time, plan cursor, blockers, attack seat and order. Nothing set them in production any more, and every reader is deleted with them.
    /// </summary>
    [Check("minions.congestion-planner-state-is-gone")]
    public static void CongestionPlannerStateIsGone()
    {
        var left = typeof(Entity).GetProperties().Select(property => property.Name)
            .Where(name => name.StartsWith("Congestion", StringComparison.Ordinal)).ToArray();
        TestAssert.True(left.Length == 0, "the planner's state is left on Entity: " + string.Join(", ", left));
    }

    private sealed class SeatNeutralSink : INeutralPacketSink
    {
        public NavigationBounds FxBounds => new(0, 15000, 0, 15000);
        public double TerrainHeight(Position2 position) => 0.0;
        public uint Allocate(UnitType unitType) => 0;
        public uint NextSync(uint unitNetId) => 1;
        public void Enqueue(PlaintextPacket packet) { }
        public void CreateNeutral(NeutralMember member) { }
        public void AddBuff(Entity target, BuffState buff) { }
        public void MinionStats(Entity entity, NeutralStats stats) { }
        public void ReplicationHp(Entity entity) { }
        public void StopAttack(Entity entity) { }
        public void Waypoints(Entity entity, IReadOnlyList<Position2> points, uint syncId) { }
    }
}
