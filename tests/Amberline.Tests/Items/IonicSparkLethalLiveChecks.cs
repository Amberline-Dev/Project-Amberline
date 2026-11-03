using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-ionic-spark-lethal-primary-chains-live-neighbors")]
    public static void IonicSparkLethalPrimaryDoesNotReselectZeroHealthUnit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var shielded in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var primary = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3178), null, null, null, null, null];
            // Batch 149 step 5: Items/3178.luaobj opens Spells/LightningRodApplicator.luaobj on the poll that binds the
            // copy (ExecuteImmediately true), and the fourth-hit arm is that window's own InstanceVars.AttackCounter.
            runtime.Tick(0.01);
            ItemScriptState.ArmWindowCounter(runtime, attacker, "LightningRodApplicator", "AttackCounter", 3.0);
            var ownImpact = 100.0 * CombatRules.ResistanceMultiplier(primary.SpellBlock);
            primary.Health = 1;
            if (shielded)
                runtime.Buffs.AddBuff(primary, "BansheesVeil", BuffType.Aura, 10.0,
                    primary.NetId, canMitigateDuration: false);
            var neighbors = Enumerable.Range(1, 4).Select(index =>
                runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, primary.Team,
                    new Position2(primary.Position.X + index * 30, primary.Position.Z))
                {
                    Name = "Minion_T200Basic", Health = 1000, MaxHealth = 1000,
                    AttackRange = 100, Speed = 325, Radius = 0,
                })).ToArray();
            runtime.Delivery.Flush();

            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, primary.NetId, 1,
                DamageType.True, DamageSourceKind.Basic), $"{team}/{shielded}: basic lands");
            // A hand-added BansheesVeil is a payload shield and absorbs the one-damage basic hit whole, so the
            // primary's first life ends only in the unshielded case; the chain is consumed either way.
            TestAssert.Equal(!shielded, !primary.Alive, $"{team}/{shielded}: primary's first life ends once");
            // Script wins (batch 149 step 5): Spells/LightningRodApplicator.luaobj's chain lands its own impact on the
            // primary before the basic hit's damage does, so a one-health primary is already dead when that damage is
            // due and the engine drops it. A Banshee on the primary consumes the chain instead and takes the basic hit
            // whole. Neither case is a re-selected zero-health unit: that is the bounces below.
            if (shielded)
                TestAssert.Near(1.0, primary.Health, 1e-6,
                    $"{team}/{shielded}: the veil absorbs the one-damage basic hit");
            else
                TestAssert.Near(ownImpact, primary.MatchStatistics.TotalDamageTaken, 1e-6,
                    $"{team}/{shielded}: the primary takes its own impact, not a bounce");
            // The counter's own arithmetic (including the reset the chain-fires branch makes) is
            // polish.ionic-spark-counter-counts-every-hit-and-overshoots-on-structures.
            for (var index = 0; index < neighbors.Length; index++)
            {
                // The source has four total iterations. With a lethal first
                // target, the unshielded chain can hit three live neighbors;
                // a primary Banshee consumes the chain before any bounce.
                var hit = !shielded && index < 3;
                TestAssert.Near(hit ? 900 : 1000, neighbors[index].Health, 1e-6,
                    $"{team}/{shielded}: live neighbor {index} obeys chain/shield policy");
                TestAssert.Equal(hit, neighbors[index].Buffs.Any(buff => buff.Name == "LightningRodChain"),
                    $"{team}/{shielded}: marker matches actual live-chain hit {index}");
            }
        }
    }
}
