using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-ionic-spark-chain-targets")]
    public static void IonicSparkSkipsUntargetableUnitsAndActiveChainMarkers()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var attacker = match.Hero(0); var primary = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3178), null, null, null, null, null];
            // Batch 149 step 5: a copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory),
            // the way a real buy does, and that poll opens the LightningRodApplicator window.
            runtime.Tick(0.01);
            attacker.MagicPenetrationFlat = attacker.MagicPenetrationPercent = 0;
            primary.SpellBlock = 0;
            var trap = Add(EntityKind.NidaleeTrap, 5);
            trap.Protected = true;
            var ward = Add(EntityKind.Ward, 7);
            ward.IsWard = true;
            var marked = Add(EntityKind.Minion, 10);
            runtime.Buffs.AddBuff(marked, "LightningRodChain", BuffType.Internal, 1.5,
                match.Hero(2).NetId, canMitigateDuration: false);
            var targets = new[] { Add(EntityKind.Minion, 30), Add(EntityKind.Minion, 60),
                Add(EntityKind.Minion, 90), Add(EntityKind.Minion, 120) };
            runtime.Delivery.Flush();

            Proc();
            foreach (var unit in targets.Take(3)) TestAssert.Near(900, unit.Health, 1e-6);
            TestAssert.Near(1000, targets[3].Health, 1e-6, "four targets includes the primary");
            TestAssert.Near(1000, marked.Health, 1e-6, "another caster's chain marker is excluded");
            TestAssert.Near(1000, trap.Health, 1e-6, "untargetable trap cannot consume a bounce");
            TestAssert.Near(1000, ward.Health, 1e-6, "the source flags do not include wards");

            Proc();
            foreach (var unit in targets) TestAssert.Near(900, unit.Health, 1e-6,
                "only the previously unmarked fourth candidate receives the next proc");
            TestAssert.Near(1000, marked.Health, 1e-6);

            foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, 1.5);
            Proc();
            TestAssert.Near(900, marked.Health, 1e-6, "an expired marker permits another bounce");
            TestAssert.Near(800, targets[0].Health, 1e-6);
            TestAssert.Near(800, targets[1].Health, 1e-6);
            TestAssert.Near(900, targets[2].Health, 1e-6);
            TestAssert.Near(900, targets[3].Health, 1e-6);
            TestAssert.False(trap.Buffs.Any(buff => buff.Name == "LightningRodChain"));
            TestAssert.False(ward.Buffs.Any(buff => buff.Name == "LightningRodChain"));

            Entity Add(EntityKind kind, double offset)
            {
                var type = kind == EntityKind.NidaleeTrap ? UnitType.Monster : UnitType.Minion;
                return runtime.World.Register(new Entity(runtime.World.Allocate(type), kind, type,
                    primary.Team, new Position2(primary.Position.X + offset, primary.Position.Z))
                {
                    Name = "Minion_T200Basic", Health = 1000, MaxHealth = 1000,
                    AttackRange = 100, Speed = 325, Radius = 0,
                });
            }
            void Proc()
            {
                // Batch 149 step 5: the fourth-hit arm is Spells/LightningRodApplicator.luaobj's own
                // InstanceVars.AttackCounter. The chain-fires branch of its AttackCounter == 4 block zeroes the
                // counter (instruction 0165), so a proc that started the chain leaves it at 0 — only a hit that
                // starts nothing, a structure, lets it overshoot (polish.ionic-spark-counter-…-overshoots…).
                // The hand-written Item3178Stacks were consumed to 0.
                ItemScriptState.ArmWindowCounter(runtime, attacker, "LightningRodApplicator", "AttackCounter", 3.0);
                TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, primary.NetId, 1,
                    DamageType.True, DamageSourceKind.Basic));
                TestAssert.Near(0.0,
                    ItemScriptState.WindowCounter(runtime, attacker, "LightningRodApplicator", "AttackCounter"), 1e-9);
            }
        }
    }
}
