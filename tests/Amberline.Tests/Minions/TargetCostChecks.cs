using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (16 October 2026): the ai_Target* constants of LEVELS/Map1/Constants.var pick a minion's target inside one
/// priority tier. The expectations below are arithmetic on the shipped values (0.7 range, 0.5 path, 0.8 per attacker,
/// 5 attackers counted at most), written out in each comment, not read back from the code under test. With the path equal to
/// the straight line the cost of a candidate at distance d with a attackers is 1.2 * d * (1 + 0.8 * a).
/// </summary>
public static class TargetCostChecks
{
    [Check("minions.n2-target-cost-constants")]
    public static void ConstantsAreTheShippedMap1Values()
    {
        // Literal pins of LEVELS/Map1/Constants.var. The neighbour and hero-boost values are read but have no term.
        var shipped = new TargetCostConstants(0.6, 0.8, 0.7, 0.5, 150.0, 5);
        TestAssert.Equal(shipped, MapConstants.LoadMap1().TargetCost);
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        TestAssert.Equal(shipped, runtime.Minions!.TargetCost, "the production AI reads the shipped constants");
    }

    [Check("minions.n2-target-cost-spreads-equal-candidates")]
    public static void EqualCandidatesAreSpreadByTheirAttackers()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var minion = ScriptDecisionChecks.Spawn(runtime);
        var east = EnemyMinion(runtime, new(7100, 7000));
        var west = EnemyMinion(runtime, new(6900, 7000));
        var ai = runtime.Minions!;
        var first = ai.SelectTarget(minion)!;
        TestAssert.True(first.NetId == east.NetId || first.NetId == west.NetId, "one of the two is picked");
        var other = first.NetId == east.NetId ? west : east;

        // Equal distance: one attacker makes the other the cheaper target (120 * 1.8 > 120).
        AttackWithMinions(runtime, first, 1);
        TestAssert.Equal(other.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "the unattacked twin is taken");
        // Two attackers on the twin against one: the first is cheaper again (120 * 1.8 < 120 * 2.6).
        AttackWithMinions(runtime, other, 2);
        TestAssert.Equal(first.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "the less attacked of the two is taken");
        // Any unit attacking counts, not only lane minions: two heroes make it 3 against 2 (120 * 3.4 > 120 * 2.6).
        AttackWithHero(runtime, first);
        AttackWithHero(runtime, first);
        TestAssert.Equal(other.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "hero attackers count like minions");
    }

    [Check("minions.n2-target-cost-weighs-distance-and-attackers")]
    public static void DistanceStillMattersAgainstAttackers()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var minion = ScriptDecisionChecks.Spawn(runtime);
        var near = EnemyMinion(runtime, new(7100, 7000));
        var far = EnemyMinion(runtime, new(7200, 7000));
        var ai = runtime.Minions!;
        TestAssert.Equal(near.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "no attackers: the nearest");
        // near: 1.2 * 100 * 1.8 = 216 against far: 1.2 * 200 = 240.
        AttackWithMinions(runtime, near, 1);
        TestAssert.Equal(near.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "216 < 240: one attacker does not outweigh half the distance");
        // near: 1.2 * 100 * 2.6 = 312 against 240.
        AttackWithMinions(runtime, near, 1);
        TestAssert.Equal(far.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "312 > 240: two attackers do");
    }

    [Check("minions.n2-target-cost-attacker-cap-saturates")]
    public static void AttackersBeyondTheCapAddNoCost()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var minion = ScriptDecisionChecks.Spawn(runtime);
        var near = EnemyMinion(runtime, new(7100, 7000));
        var far = EnemyMinion(runtime, new(7140, 7000));
        var ai = runtime.Minions!;
        // near with 4 attackers: 1.2 * 100 * 4.2 = 504 against far 1.2 * 140 = 168.
        AttackWithMinions(runtime, near, 4);
        TestAssert.Equal(far.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "4 attackers outweigh 40 units");
        // far with 5 attackers: 168 * 5 = 840 against near 504.
        AttackWithMinions(runtime, far, 5);
        TestAssert.Equal(near.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "the less attacked of the two is taken");
        // near with 9 attackers: the count saturates at ai_TargetMaxNumAttackers = 5, so 1.2 * 100 * 5 = 600 < 840.
        // Unsaturated it would be 1.2 * 100 * 8.2 = 984 > 840 and the minion would walk to far.
        AttackWithMinions(runtime, near, 5);
        TestAssert.Equal(near.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "attackers beyond 5 add no cost");
        // Both saturated: the nearer one, and a hero (tier 7) never comes into it.
        AttackWithMinions(runtime, far, 4);
        ScriptDecisionChecks.Enemy(runtime, new(7050, 7000));
        TestAssert.Equal(near.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "both full: the cheapest of the minion tier");
    }

    [Check("minions.n2-target-cost-keeps-the-priority-tiers")]
    public static void CostNeverReordersTheTiers()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var minion = ScriptDecisionChecks.Spawn(runtime);
        ScriptDecisionChecks.Enemy(runtime, new(7050, 7000));
        var target = EnemyMinion(runtime, new(7500, 7000));
        var ai = runtime.Minions!;
        // The hero costs 1.2 * 50 = 60 and the minion 1.2 * 500 * 4.2 = 2520, yet minions are tier 6 and heroes tier 7.
        AttackWithMinions(runtime, target, 4);
        TestAssert.Equal(target.NetId, ai.SelectTarget(minion)?.NetId ?? 0, "a minion before a hero, however cheap the hero");

        // Inside the hero tier the same cost applies, without a hero boost (no pooled tier): near with one attacker
        // is 1.2 * 100 * 1.8 = 216 against far 1.2 * 190 = 228. A boost on both would flip it:
        // (120 + 150) * 1.8 = 486 against 228 + 150 = 378.
        using var heroes = ScriptDecisionChecks.CreateRuntime();
        var selector = ScriptDecisionChecks.Spawn(heroes);
        var nearHero = ScriptDecisionChecks.Enemy(heroes, new(7100, 7000));
        ScriptDecisionChecks.Enemy(heroes, new(7190, 7000));
        AttackWithMinions(heroes, nearHero, 1);
        TestAssert.Equal(nearHero.NetId, heroes.Minions!.SelectTarget(selector)?.NetId ?? 0, "no hero boost inside a hero tier");
    }

    [Check("minions.n2-target-cost-selecting-minion-is-not-its-own-attacker")]
    public static void TheSelectingMinionDoesNotCountAsAnAttackerOfItsTarget()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var minion = ScriptDecisionChecks.Spawn(runtime);
        var current = EnemyMinion(runtime, new(7095, 7000));
        EnemyMinion(runtime, new(6900, 7000));
        minion.AttackTarget = current.NetId;
        minion.MinionState = MinionAiState.Engage;
        // Counting itself would price the current target at 1.2 * 95 * 1.8 = 205 against 1.2 * 100 = 120 and move the minion.
        TestAssert.Equal(current.NetId, runtime.Minions!.SelectTarget(minion)?.NetId ?? 0, "its own attack is not a crowd");
    }

    [Check("minions.n2-stale-plant-stop-attack-needs-an-attack")]
    public static void StalePlantStopAttackIsSentOnlyWhileAnAttackIsUnderWay()
    {
        using var runtime = ScriptDecisionChecks.CreateRuntime();
        var idle = ScriptDecisionChecks.Spawn(runtime);
        var attacking = ScriptDecisionChecks.Spawn(runtime);
        attacking.Position = new(7000, 7800);
        attacking.AnchorPath = [attacking.Position];
        var idleTarget = ScriptDecisionChecks.Enemy(runtime, new(7400, 7000));
        var attackingTarget = ScriptDecisionChecks.Enemy(runtime, new(7400, 7800));
        var packets = new StopRecorder();
        using var ai = new MinionAiSystem(runtime.World, runtime.Navigation, runtime.Movement,
            new ProductionMinionCombatService(runtime.Combat), packets, new AllVisible(),
            canSee: (_, _) => true);
        ai.ReplantMinimumGapSeconds = 0.0;
        foreach (var (unit, target) in new[] { (idle, idleTarget), (attacking, attackingTarget) })
        {
            unit.AttackTarget = target.NetId;
            unit.MinionState = MinionAiState.Engage;
        }
        attacking.MinionAutoAttackEnabled = true;
        // It has attacked: its 0x0D was sent, so its client is in the attack state until a stop-attack.
        attacking.ClientAttackArmed = true;

        // Both stand planted with a target out of reach, so both replant; only the one with an attack stops it.
        ai.RunEngagementProbe(idle, idleTarget);
        ai.RunEngagementProbe(attacking, attackingTarget);
        TestAssert.True(idle.Waypoints.Count > 0 || idle.Target is not null, "the idle minion still chases");
        TestAssert.True(attacking.Waypoints.Count > 0 || attacking.Target is not null, "the attacking minion chases");
        TestAssert.Equal(1, packets.StopAttacks.Count, "one stop-attack for two replants");
        TestAssert.Equal(attacking.NetId, packets.StopAttacks[0].NetId, "sent for the minion that was attacking");
        TestAssert.Equal("stale-plant", packets.StopAttacks[0].Reason);
    }

    private static Entity EnemyMinion(MatchRuntime runtime, Position2 position)
    {
        var minion = runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee,
            runtime.World.Entities.Count, "C", 0, 0);
        minion.Position = position;
        minion.Waypoints.Clear();
        minion.Target = null;
        minion.AnchorPath = [position];
        minion.Created = true;
        minion.Visible = true;
        minion.Health = minion.MaxHealth = 1_000_000.0;
        return minion;
    }

    /// <summary>Allied lane minions engaged on <paramref name="target"/>, parked far from the selector.</summary>
    private static void AttackWithMinions(MatchRuntime runtime, Entity target, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var attacker = runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee,
                runtime.World.Entities.Count, "C", 0, 0);
            attacker.Position = new(4000 + 40 * i, 4000);
            attacker.Waypoints.Clear();
            attacker.Target = null;
            attacker.AttackTarget = target.NetId;
            attacker.MinionState = MinionAiState.Engage;
        }
    }

    private static void AttackWithHero(MatchRuntime runtime, Entity target)
    {
        var hero = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, new Position2(4500, 4000))
        {
            ChampionName = "Annie", Health = 10000, MaxHealth = 10000,
            Radius = 35, PathfindingRadius = 35, Created = true, Visible = true,
        });
        hero.AttackTarget = target.NetId;
    }

    private sealed class AllVisible : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class StopRecorder : IMinionPacketSink
    {
        public List<(uint NetId, string Reason)> StopAttacks { get; } = new();
        public bool IsReferenceReady(Entity minion) => true;
        public bool TryGetClientPosition(Entity minion, out Position2 position) { position = default; return false; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason)
        {
            if (CombatPackets.StopAttack(minion) is not null) StopAttacks.Add((minion.NetId, reason));
        }
    }
}
