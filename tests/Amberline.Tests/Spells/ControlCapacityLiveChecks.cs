using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-janna-control-capacity-keeps-channel-and-wave-coherent")]
    public static void JannaControlCapacityKeepsChannelAndWaveCoherent()
    {
        // 19 October 2026: Janna runs from her scripts, which never guard the 64-slot buff table; the engine declines a buff add the table has
        // no slot for (NOT RETAIL, BbBuffRuntime) and the spell goes on without it. Nothing crashes and nothing is left half done: the
        // hand-written admission of a whole cast (a full caster table refusing Monsoon or Gale before they begin) is gone with the module.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var passive = new Match("Janna", team))
            {
                var caster = passive.Hero(0);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "SowTheWind"));
                FillControlCapacity(caster);
                Advance(passive, .1);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "SowTheWind"),
                    "a full table leaves the passive uninstalled without crashing its tick");
            }

            using (var wave = new Match("Janna", team))
            {
                var caster = wave.Hero(0);
                var enemy = wave.Hero(1);
                enemy.Position = caster.Position + new Position2(250, 0);
                FillControlCapacity(enemy);
                var health = enemy.Health;
                wave.CastGround(0, enemy.Position);
                wave.CastGround(0, enemy.Position);
                Advance(wave, 4.5);
                TestAssert.True(enemy.Health < health, "Gale's landed damage remains");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "Move"));
                TestAssert.True(enemy.ForcedMove is null,
                    "an unadmitted Gale stun cannot start a forced move");
                TestAssert.True(JannaCubes(wave).Count == 0,
                    "the released wave's TestCube retires with its timer even when its hit has no buff slot");
            }

            using (var zephyr = new Match("Janna", team))
            {
                var caster = zephyr.Hero(0);
                var enemy = zephyr.Hero(1);
                FillControlCapacity(enemy);
                var health = enemy.Health;
                zephyr.Cast(0, 1, enemy);
                Advance(zephyr, 1.0);
                TestAssert.True(enemy.Health < health, "Zephyr's missile still deals damage");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "Slow"));
                TestAssert.False(zephyr.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.CasterNetId == caster.NetId),
                    "Zephyr retires its missile when the slow has no slot");
            }

            using (var pulse = new Match("Janna", team, extraEnemy: true))
            {
                var caster = pulse.Hero(0);
                var full = pulse.Hero(1);
                var mobile = pulse.Hero(2);
                full.Position = caster.Position + new Position2(200, 0);
                mobile.Position = caster.Position + new Position2(300, 0);
                mobile.Inventory = [new InventorySlot(3117), null, null, null, null, null];
                // The first stats refresh after an inventory change settles the equipment (it clears a Boots of Mobility penalty that is already on
                // the hero); done here, the penalty Monsoon's stun starts is not cleared by the refresh that adding it triggers.
                pulse.Runtime.HeroStats.Refresh(mobile);
                FillControlCapacity(full);
                FillControlCapacity(mobile);
                FreeControlCapacity(mobile);
                FreeControlCapacity(mobile);
                pulse.CastGround(0, caster.Position, slot: 3);
                Advance(pulse, .3);
                TestAssert.True(full.ForcedMove is null &&
                                full.Buffs.All(buff => buff.Name != "MoveAway"),
                    "a full target neither receives a partial stun nor moves");
                TestAssert.True(mobile.ForcedMove is not null &&
                                mobile.Buffs.Any(buff => buff.Name == "MoveAway") &&
                                mobile.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"),
                    "two free slots admit Monsoon control plus the nested Boots debuff");
            }
        }
    }

    [Check("spells-live-blitz-control-capacity-retires-missile-and-empowerment")]
    public static void BlitzControlCapacityRetiresMissileAndEmpowerment()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var denied = new Match("Blitzcrank", team))
            {
                var caster = denied.Hero(0);
                FillControlCapacity(caster);
                var mana = caster.Mana;
                // 19 October 2026 (script path): the shipped scripts never guard the 64-slot table, so Rocket Grab is no longer refused at
                // validation (the hand-written refusal was non-retail); the engine's add guard declines the Root cast lock instead.
                TestAssert.True(denied.TryCastGround(0, 0, denied.Hero(1).Position),
                    "a full caster table does not refuse Rocket Grab");
                TestAssert.True(caster.Mana < mana, "the accepted cast is charged");
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "Root"), "the Root cast lock has no slot and is declined");
            }

            using (var grab = new Match("Blitzcrank", team))
            {
                var caster = grab.Hero(0);
                var enemy = grab.Hero(1);
                enemy.Position = caster.Position + new Position2(350, 0);
                FillControlCapacity(enemy);
                var health = enemy.Health;
                grab.CastGround(0, enemy.Position);
                Advance(grab, .8);
                TestAssert.True(enemy.Health < health, "Rocket Grab still deals its one hit");
                TestAssert.True(enemy.ForcedMove is null);
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "RocketGrab2"));
                var damageTaken = enemy.MatchStatistics.TotalDamageTaken;
                Advance(grab, .2);
                TestAssert.Near(damageTaken, enemy.MatchStatistics.TotalDamageTaken, 1e-8,
                    "a full-table grab does not repeat damage on the next tick");
            }

            using (var lateHook = new Match("Blitzcrank", team))
            {
                var caster = lateHook.Hero(0);
                caster.Inventory = [new InventorySlot(3057), null, null, null, null, null];
                // Batch 147: the Sheen arm is Items/3057.luaobj's own ItemOnSpellCast, so its copy has to reach the
                // inventory slot first (BbItemModule.SyncInventory, one spell update) before the cast below.
                lateHook.Runtime.Tick(0.01);
                FillControlCapacity(caster);
                FreeControlCapacity(caster);
                lateHook.CastGround(0, lateHook.Hero(1).Position);
                // 19 October 2026 (script path): CharOnSpellCast adds the Root cast lock first and takes the one free slot; the item proc that
                // runs after the script's cast hooks (MatchGameplayHooks.OnSpellCast) finds the table full and its Sheen is declined. The
                // hand-written Q ran the item proc first and dropped the lock instead.
                TestAssert.True(caster.Buffs.Any(buff => buff.Name == "Root"), "the script's cast lock takes the one free slot");
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "Sheen"), "the item proc after it finds no slot");
                TestAssert.True(caster.SpellCooldowns[0] > 0);
            }

            using (var fist = new Match("Blitzcrank", team))
            {
                var caster = fist.Hero(0);
                var enemy = fist.Hero(1);
                fist.CastGround(0, caster.Position, slot: 2);
                Advance(fist, .3);
                TestAssert.True(caster.Buffs.Any(buff => buff.Name == "PowerFist"));
                FillControlCapacity(enemy);
                TestAssert.True(fist.Runtime.Combat.ApplyHit(caster.NetId, enemy.NetId,
                    10.0, DamageType.Physical, DamageSourceKind.Basic));
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "PowerFist"),
                    "the landed attack consumes empowerment even when stun cannot fit");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "PowerFistSlow"));
                TestAssert.True(enemy.ForcedMove is null);
            }

            using (var blast = new Match("Blitzcrank", team, extraEnemy: true))
            {
                var caster = blast.Hero(0);
                var full = blast.Hero(1);
                var later = blast.Hero(2);
                FillControlCapacity(full);
                var firstHealth = full.Health;
                var laterHealth = later.Health;
                blast.CastGround(0, caster.Position, slot: 3);
                Advance(blast, .3);
                TestAssert.True(full.Health < firstHealth && later.Health < laterHealth,
                    "one full target cannot abort the rest of Static Field's AoE");
                TestAssert.False(full.Buffs.Any(buff => buff.Name == "Silence"));
                TestAssert.True(later.Buffs.Any(buff => buff.Name == "Silence"));
            }
        }
    }

    [Check("spells-live-lee-r-full-target-keeps-damage-without-stale-kick")]
    public static void LeeRFullTargetKeepsDamageWithoutStaleKick()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var caster = match.Hero(0);
            var enemy = match.Hero(1);
            FillControlCapacity(enemy);
            var health = enemy.Health;
            match.Cast(0, 3, enemy);
            Advance(match, .6);
            TestAssert.True(enemy.Health < health, "Dragon's Rage direct hit remains");
            TestAssert.True(enemy.ForcedMove is null);
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name is "BlindMonkRRoot" or "BlindMonkRKick"));
            TestAssert.True(caster.ForcedMove is null && enemy.ForcedMove is null,
                "a missing kick buff cannot leave a tracked displacement");
        }
    }

    private static void FillControlCapacity(Entity unit)
    {
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots; slot++)
        {
            if (unit.Buffs.Any(buff => buff.Slot == slot)) continue;
            unit.Buffs.Add(new BuffState($"ControlCapacity{slot}", slot: slot, expiresAt: 60.0));
        }
        TestAssert.Equal(SpellPackets.MaxBuffSlots, unit.Buffs.Count);
    }

    private static void FreeControlCapacity(Entity unit)
    {
        var filler = unit.Buffs.First(buff => buff.Name.StartsWith("ControlCapacity", StringComparison.Ordinal));
        unit.Buffs.Remove(filler);
    }
}
