using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static partial class MinionChecks
{
    [Check("minions.b15-inhibitor-change-does-not-split-wave-stats")]
    public static void InhibitorChangesPreserveWaveSnapshot()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var initiallyDown in new[] { false, true })
        foreach (var delayed in new[] { false, true })
        {
            var world = InhibitorWorld(initiallyDown ? 1 : 0, team);
            var inhibitor = world.Entities.Values.First(entity => entity.Kind == EntityKind.Inhibitor);
            var lane = MapDefinitions.Lanes[0];
            // Custom start straddles the 180-second upgrade; normal 30-second
            // waves do not. This also pins rewards to wave creation, not birth.
            var level = LevelScriptFixtures.Init(world);
            if (initiallyDown) LevelScriptFixtures.FellInhibitors(level, world, team);
            var scheduler = new WaveScheduler(world, grid, new FixturePackets(),
                level, firstSpawnSeconds: 119.2);
            scheduler.Start(0, new[] { lane });
            // Batch 51 (b5cd29c): the stock barracks update never replays a missed wave, so the two earlier waves are
            // ticked at their nominal times (119.2, 149.2) instead of one late tick at 160.
            _ = scheduler.Tick(119.2);
            _ = scheduler.Tick(149.2);
            _ = scheduler.Tick(160); // Drain both waves so the observed wave is the third and includes cannons.
            var first = scheduler.Tick(179.2).ToList();
            // Batch 310: an inhibitor already down at init runs the shipped HandleDestroyedObject, whose DisableBarracksSpawn arms the
            // barracks' 300 s countdown (the disable-minion-spawn setter), which the shared barracks update
            // only counts down to the dampener's return (the dampener's state set to 1) and never reads in its wave spawn. Both teams' slot
            // zero is born (batch 183 had asserted one, the fallen inhibitor's team paused).
            TestAssert.Equal(2, first.Count, "slot zero is born for both teams");
            // The edge is what tells the script — CombatService raises BarracksChanged on the
            // lethal transition and on the respawn, and only that runs the destruction bonuses.
            inhibitor.Alive = initiallyDown;
            if (initiallyDown) level.OnBarracksRestored(inhibitor);
            else level.OnBarracksFell(inhibitor);
            first.AddRange(scheduler.Tick(delayed ? 200 : 186));
            // The third wave was snapshotted at 179.2, before the event above, but an inhibitor
            // that was already down at init left the script's defect in the tables it read.
            AssertStats(first, initiallyDown, 0, goldDefect: initiallyDown);
            TestAssert.Equal(0, scheduler.Pending.Count, "the current stagger is fully drained");
            // The next UpgradeMinionTimer fired at 180, inside the tick above, so the fourth wave
            // is snapshot after both the upgrade and the repair of each class's own gold.
            var next = scheduler.Tick(216);
            AssertStats(next, !initiallyDown, 1, goldDefect: false);
            TestAssert.Equal(initiallyDown ? 1 : 0,
                first.Count(entity => entity.Team == team && entity.MinionType == MinionType.Super));
            TestAssert.Equal(initiallyDown ? 0 : 1,
                next.Count(entity => entity.Team == team && entity.MinionType == MinionType.Super));

            void AssertStats(IEnumerable<Entity> members, bool down, int upgrades, bool goldDefect)
            {
                foreach (var minion in members)
                {
                    var boosted = down && minion.Team == team;
                    var expected = minion.MinionType switch
                    {
                        MinionType.Melee => (445 + 20 * upgrades + (boosted ? 100 : 0),
                            11 + upgrades + (boosted ? 10 : 0), 22 + .5 * upgrades, 64 + 5 * upgrades),
                        MinionType.Caster => (280 + 15 * upgrades + (boosted ? 60 : 0),
                            23 + 2 * upgrades + (boosted ? 18 : 0), 16 + .5 * upgrades, 32 + 3 * upgrades),
                        MinionType.Siege => (700 + 27 * upgrades + (boosted ? 200 : 0),
                            40 + 3 * upgrades + (boosted ? 25 : 0), 27.0 + upgrades, 100 + 7 * upgrades),
                        MinionType.Super => (1500 + 200 * upgrades, 180 + 10 * upgrades, 27.0, 100),
                        _ => throw new InvalidOperationException("unexpected wave member"),
                    };
                    // LevelScript.txt proto 0.11 pc0098-0137 (owner decision, 18 October 2026): while
                    // an enemy inhibitor is down or has just respawned, Archer/Caster/Super GoldGiven
                    // are read from MeleeGoldGiven, so every class of the beneficiary team pays the
                    // melee gold until the next UpgradeMinionTimer (proto 0.4 pc0062-0077).
                    var gold = expected.Item3;
                    if (goldDefect && minion.Team == team && minion.MinionType != MinionType.Melee)
                        gold = 22 + .5 * upgrades;
                    TestAssert.Near(expected.Item1, minion.MaxHealth, 1e-8, "wave HP snapshot");
                    TestAssert.Near(expected.Item2, minion.AttackDamage, 1e-8, "wave AD snapshot");
                    TestAssert.Near(gold, minion.GoldReward, 1e-8, "wave gold snapshot");
                    TestAssert.Near(expected.Item4, minion.ExperienceReward, 1e-8, "wave XP snapshot");
                }
            }
        }
    }

    [Check("minions.b15-inhibitor-gold-policy")]
    public static void InhibitorEventsReproduceTheScriptGoldDefect()
    {
        // Owner decision, 18 October 2026: reproduce the shipped Map1 LevelScript defect. On
        // destruction (ApplyBarracksDestructionBonuses, proto 0.11 pc0098-0137) and on respawn
        // (ApplyBarracksRespawnReductions, proto 0.12 pc0295-0330) the Archer, Caster and Super
        // gold fields of every lane of the beneficiary team are read from MeleeGoldGiven instead
        // of their own field; the *_GOLD_INHIBITOR / *_EXP_INHIBITOR terms subtracted or added
        // there are all 0.0 (proto 0 pc0112-0139), so each class pays the melee gold until the
        // next UpgradeMinionTimer (proto 0.4 pc0062-0077) recomputes every class from its own base.
        static double Own(MinionType type, int upgrade) => type switch
        {
            MinionType.Melee => 22 + Math.Min(.5 * upgrade, 10),
            MinionType.Caster => 16 + Math.Min(.5 * upgrade, 10),
            MinionType.Siege => 27 + Math.Min(upgrade, 20),
            MinionType.Super => 27,
            _ => throw new InvalidOperationException(),
        };
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var upgrade in new[] { 0, 1, 25 })
        {
            var interval = upgrade * 180.0;
            var world = InhibitorWorld(0, team);
            var inhibitors = world.Entities.Values.Where(entity => entity.Kind == EntityKind.Inhibitor).ToArray();
            var host = LevelScriptFixtures.Init(world);
            host.AdvanceTo(interval + 1);
            void Expect(int count, bool bugged, string label)
            {
                foreach (var lane in MapDefinitions.Lanes)
                foreach (var type in Enum.GetValues<MinionType>())
                    TestAssert.Near(bugged ? Own(MinionType.Melee, count) : Own(type, count),
                        host.EconomyOf(team, lane).RowOf(type).GoldGiven, 1e-8,
                        $"{team} {type} lane {lane} {label}");
            }

            Expect(upgrade, false, "no event yet");
            inhibitors[1].Alive = false;
            host.OnBarracksFell(inhibitors[1]);
            Expect(upgrade, true, "destruction, every lane");
            host.AdvanceTo(interval + 179);
            Expect(upgrade, true, "destruction, until the upgrade tick");
            inhibitors[1].Alive = true;
            host.OnBarracksRestored(inhibitors[1]);
            Expect(upgrade, true, "respawn is the same defect");
            host.AdvanceTo(interval + 181);
            Expect(upgrade + 1, false, "the next upgrade tick recomputes each class");

            // The team's own inhibitors feed the opposing tables and never its own.
            var ownWorld = InhibitorWorld(0, MinionDefinitions.Opposing(team));
            var ownHost = LevelScriptFixtures.Init(ownWorld);
            var own = ownWorld.Entities.Values.First(entity => entity.Kind == EntityKind.Inhibitor);
            ownHost.OnBarracksFell(own);
            foreach (var type in Enum.GetValues<MinionType>())
                TestAssert.Near(Own(type, 0),
                    ownHost.EconomyOf(team, MapDefinitions.Lanes[0]).RowOf(type).GoldGiven,
                    1e-8, $"{team} {type} own inhibitor");
        }
    }
}

public static partial class ChampionLiveRegressionChecks
{
    [Check("minions.b15-turret-shield-buff-and-hit-effect")]
    public static void MinionScriptBuffsAndTurretHitEffect()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        TestAssert.True(runtime.GameData.HasClientBuff("TurretShield"));
        TestAssert.True(runtime.GameData.HasClientBuff("StructureDamageReduction"));
        TestAssert.False(runtime.GameData.HasClientBuff("B15UnknownMinionBuff"));
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in Enum.GetValues<MinionType>())
        {
            var minion = runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0);
            minion.Position = match.Hero(0).Position + new Position2(50, 0);
            minion.Health = minion.MaxHealth = 10000;
            runtime.Delivery.Flush();
            var shielded = type is MinionType.Siege or MinionType.Super;
            TestAssert.Equal(shielded, minion.Buffs.Any(buff => buff.Name == "TurretShield"));
            TestAssert.Equal(type == MinionType.Super,
                minion.Buffs.Any(buff => buff.Name == "StructureDamageReduction"));
            foreach (var buff in minion.Buffs)
            {
                TestAssert.Near(type == MinionType.Siege ? 20000 : 25000, buff.ExpiresAt, 0);
                TestAssert.Equal(buff.Name == "TurretShield" ? BuffType.CombatEnhancer : BuffType.Internal, buff.Type);
                TestAssert.Equal(buff.Name == "TurretShield", buff.Payload.NonDispellable);
                foreach (var capture in match.Captures)
                    TestAssert.True(capture.Packets.Any(packet => Opcode(packet) == 0xBF &&
                        Id(packet.Plaintext.Span, 1) == minion.NetId &&
                        Id(packet.Plaintext.Span, 9) == SpellPackets.BuffNameHash(
                            // Batch 181: the add carries the name the client holds (TurretShield's BuffName is Turret Shield)
                            runtime.GameData.ClientBuffName(buff.Name))),
                        "spawn/reveal publishes the known minion buff to both observers");
            }
            runtime.SpellBuffs.Add(minion, "B15UnknownMinionBuff", BuffType.Internal, 1, minion.NetId);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0xBF &&
                    Id(packet.Plaintext.Span, 9) == SpellPackets.BuffNameHash("B15UnknownMinionBuff")));

            // Buff additions recompute NPC resistances from the spawn baseline.
            // Zero armor after the buff probe to isolate the character scalar.
            minion.Armor = 0;
            var turret = runtime.World.Entities.Values.First(entity => entity.Kind == EntityKind.Turret && entity.Team != team);
            foreach (var source in new[] { turret, match.Hero(team == Team.Order ? 1 : 0) })
            foreach (var kind in new[] { DamageSourceKind.Basic, DamageSourceKind.Spell })
            {
                match.Clear();
                minion.Health = 10000;
                TestAssert.True(runtime.Combat.ApplyHit(source.NetId, minion.NetId, 100, DamageType.Physical, kind));
                runtime.Delivery.Flush();
                var reduced = shielded && source == turret && kind == DamageSourceKind.Basic;
                TestAssert.Near(reduced ? 50 : 100, 10000 - minion.Health, 1e-8,
                    "the character scalar runs exactly once for turret basics only");
                foreach (var capture in match.Captures)
                    TestAssert.Equal(reduced ? 1 : 0, capture.Packets.Count(packet => Opcode(packet) == 0x8C &&
                        Id(packet.Plaintext.Span, 6) == SpellPackets.BuffNameHash("FeelNoPain_eff") &&
                        Id(packet.Plaintext.Span, 29) == minion.NetId), "one bound turret-hit effect");
            }
            if (shielded)
            {
                // Batch 159 part 2: the shield is the minion character's own buff now (CharScriptBlue_Minion_MechCannon
                // 0000-0038 adds TurretShield from its stats sample, CharScriptBlue_Minion_MechMelee 0054-0096 only from
                // its CharOnActivate), so the update cadence that restores a missing one is the character poll.
                var shield = minion.Buffs.Single(buff => buff.Name == "TurretShield");
                runtime.Buffs.TickBuffs(minion, 1);
                runtime.Spells.UnitScripts!.Poll();
                TestAssert.True(ReferenceEquals(shield, minion.Buffs.Single(buff => buff.Name == "TurretShield")),
                    "an existing cannon shield is not replaced each update");
                runtime.Buffs.TickBuffs(minion, 25000);
                runtime.Spells.UnitScripts!.Poll();
                TestAssert.Equal(type == MinionType.Siege, minion.Buffs.Any(buff => buff.Name == "TurretShield"),
                    "only cannon restores a missing shield after expiry");
                if (type == MinionType.Siege)
                    TestAssert.Near(20000, minion.Buffs.Single(buff => buff.Name == "TurretShield").ExpiresAt, 0);
                TestAssert.False(minion.Buffs.Any(buff => buff.Name == "StructureDamageReduction"));
                if (type == MinionType.Super)
                {
                    var nexus = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Structure),
                        EntityKind.Nexus, UnitType.Structure, MinionDefinitions.Opposing(team), minion.Position)
                        { Health = 10000, MaxHealth = 10000 });
                    TestAssert.True(runtime.Combat.ApplyHit(minion.NetId, nexus.NetId, 100, DamageType.True, DamageSourceKind.Basic));
                    TestAssert.Near(100, 10000 - nexus.Health, 1e-8, "expired structure buff no longer quarters damage");
                    runtime.World.Remove(nexus.NetId);
                }
            }
            runtime.World.Remove(minion.NetId);
            runtime.Delivery.Flush();
            match.Clear();
        }
    }
}
