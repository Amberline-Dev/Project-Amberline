using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-gunblade-missile-lifecycle")]
    public static void GunbladeUsesItsShippedMissileAndImpact()
    {
        var data = GameData.Default.Spell("HextechGunbladeSpell", 1);
        TestAssert.Near(1600.0, data.MissileSpeed, 0, "shipped Gunblade flight speed");
        TestAssert.Equal("hextech_gunBlade_mis.troy",
            data.Raw[InibinReader.KeyHash("SpellData*MissileEffect")].AsString());
        TestAssert.Equal("hextech_gunBlade_tar.troy",
            data.Raw[InibinReader.KeyHash("SpellData*HitEffectName")].AsString());

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "impact", "caster-death", "target-death", "shield" })
        {
            // Keep allied sight after caster death so this test checks missile
            // survival and impact delivery, independently of ordinary fog loss.
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var target = match.Hero(1);
            caster.Inventory = [new InventorySlot(3146), null, null, null, null, null];
            target.Position = caster.Position + new Position2(640, 0);
            if (ending == "shield")
                target.Inventory = [new InventorySlot(3102), null, null, null, null, null];
            runtime.HeroStats.Refresh(target, push: false);
            target.Health = target.MaxHealth;
            if (ending == "shield")
                runtime.Buffs.AddBuff(target, "BansheesVeil", BuffType.Aura, 10.0, target.NetId);
            runtime.Delivery.Flush();
            match.Clear();
            var hp = target.Health;
            var damage = 300.0 * CombatRules.ResistanceMultiplier(target.SpellBlock);
            match.Cast(0, 4, target);
            TestAssert.Near(hp, target.Health, 0, "Gunblade damage waits for flight");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"));
            uint missileId = 0;
            foreach (var capture in match.Captures)
            {
                var castFx = FindEffect(capture.Packets.Where(packet => Opcode(packet) == 0x8C).ToArray(),
                    "hexTech_Gunblade_cas.troy");
                TestAssert.Equal(caster.NetId, FxBinding(castFx));
                TestAssert.Equal(SpellPackets.BuffNameHash("root"), ReadUInt32(castFx, 16));
                var missile = Decode(capture.Packets.Single(packet => Opcode(packet) == 0x41));
                TestAssert.Near(1600.0, missile.GetProperty("speed").GetDouble(), 0);
                var info = missile.GetProperty("cast_info");
                TestAssert.Equal(SpellPackets.BuffNameHash("HextechGunbladeSpell"),
                    info.GetProperty("spell_hash").GetUInt32());
                missileId = info.GetProperty("missile_netid").GetUInt32();
                TestAssert.True(missileId != 0);
                TestAssert.Equal(target.NetId, info.GetProperty("targets")[0].GetProperty("netid").GetUInt32());
            }
            runtime.Tick(.2);
            TestAssert.Near(hp, target.Health, 0, "half-flight still has no damage");
            if (ending == "caster-death") runtime.Combat.Kill(caster, target.NetId);
            if (ending == "target-death") runtime.Combat.Kill(target, caster.NetId);
            if (ending == "shield")
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "BansheesVeil"),
                    "shield consumption waits for impact");
            runtime.Delivery.Flush();
            match.Clear();
            var remainingFlight = AdvanceGunbladeToRetirement(match, missileId);
            if (ending is "impact" or "caster-death" or "shield")
                TestAssert.True(remainingFlight >= .2 - 1e-9 && remainingFlight <= .2 + 1.0 / 30.0 + 1e-9,
                    "640-unit flight resolves at 0.4 seconds within one production step");
            runtime.Delivery.Flush();
            var hit = ending is "impact" or "caster-death";
            if (ending != "target-death")
                TestAssert.Near(hit ? hp - damage : hp, target.Health, 1e-6);
            TestAssert.Equal(hit, target.Buffs.Any(buff => buff.Name == "BilgewaterCutlass"));
            if (hit)
                TestAssert.Near(3.0, target.Buffs.Single(buff => buff.Name == "BilgewaterCutlass").ExpiresAt, 0);
            if (ending == "shield")
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"));
            var deadTargetImpacts = 0;
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x5D &&
                    ReadUInt32(packet, 1) == missileId), "one missile retirement per observer");
                var impacts = capture.Packets.Count(packet => Opcode(packet) == 0x8C &&
                    ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("hextech_gunBlade_tar"));
                // Batch 348: a homing script missile runs its hit on a target that died in flight (the client's shared hit pass has no
                // life check), so the impact effect plays on the corpse, for the observers that still see the corpse; the corpse takes no damage
                // and no slow (asserted above).
                if (ending == "target-death") deadTargetImpacts += impacts;
                TestAssert.True(ending == "target-death" ? impacts <= 1 : impacts == (hit ? 1 : 0),
                    $"{team}/{ending}: impact effect once on a hit, including a target that died in flight");
            }
            if (ending == "target-death")
                TestAssert.True(deadTargetImpacts >= 1, $"{team}/target-death: the hit runs on the corpse");
            match.Clear();
            runtime.Tick(1.0);
            runtime.Delivery.Flush();
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                .Any(packet => Opcode(packet) is 0x5D or 0x68), "retired flight cannot resolve twice");
        }
    }

    private static double AdvanceGunbladeToRetirement(Match match, uint missileId = 0)
    {
        if (missileId == 0)
            missileId = Decode(match.Captures[0].Packets.Last(packet => Opcode(packet) == 0x41))
                .GetProperty("cast_info").GetProperty("missile_netid").GetUInt32();
        const double step = 1.0 / 30.0;
        for (var tick = 1; tick <= 16; tick++)
        {
            match.Runtime.Tick(step);
            if (match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x5D &&
                    ReadUInt32(packet, 1) == missileId))
                return tick * step;
        }
        throw new InvalidOperationException("Gunblade did not retire within the bounded production flight.");
    }
}
