using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-teleport-capacity-atomic-shared-ward")]
    public static void TeleportCapacityRejectsBeforePublishingSharedWardState()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var first = match.Hero(0);
            var second = match.Hero(2);
            first.SummonerSpells[0] = second.SummonerSpells[0] = "SummonerTeleport";
            second.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
            {
                [124] = 1, [134] = 3, [132] = 3, [130] = 1,
                [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
            });
            runtime.HeroStats.Refresh(second, push: false);
            first.Inventory = [new InventorySlot(2044), null, null, null, null, null];
            var wardPoint = new Position2(7100, 7000);
            TestAssert.True(match.TryCastGround(0, 4, wardPoint), "owned ward item places target");
            var ward = runtime.World.Entities.Values.Single(unit => unit.IsWard &&
                unit.WardOwnerNetId == first.NetId && unit.Position == wardPoint);
            first.Position = new Position2(6000, 7000);
            second.Position = new Position2(6200, 7000);
            runtime.Delivery.Flush();

            FillSummonerSlots(first, 63);
            AssertRejected(first, "one free caster slot cannot hold two channel buffs");
            FillSummonerSlots(first, 64);
            AssertRejected(first, "full caster table rejects before random landing or packets");
            FreeSummonerSlots(first, 2);
            FillSummonerSlots(ward, 63);
            AssertRejected(first, "one free ward slot cannot hold target and reveal buffs");
            FillSummonerSlots(ward, 64);
            AssertRejected(first, "full ward table rejects before target mutation");
            FreeSummonerSlots(ward, 2);

            TestAssert.True(match.TryCast(0, 0, ward, summoner: true),
                "two free slots on each actor permit ordinary channel");
            TestAssert.Equal("TeleportCancel", first.SummonerSpells[0]);
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Teleport_Target"));
            var reveal = ward.Buffs.Single(buff => buff.Name == "Destealth");
            var firstRevealRemaining = reveal.ExpiresAt;
            TestAssert.Equal(64, ward.Buffs.Count);

            FillSummonerSlots(second, 63);
            match.Clear();
            TestAssert.False(match.TryCast(2, 0, ward, summoner: true),
                "rejected second caster cannot disturb the first channel");
            TestAssert.Equal("SummonerTeleport", second.SummonerSpells[0]);
            TestAssert.Near(0, second.SummonerCooldowns[0], 1e-9);
            TestAssert.Equal("TeleportCancel", first.SummonerSpells[0]);
            TestAssert.True(ReferenceEquals(reveal, ward.Buffs.Single(buff => buff.Name == "Destealth")));
            TestAssert.Near(firstRevealRemaining, reveal.ExpiresAt, 1e-9);
            TestAssert.False(match.Captures[2].Packets.Any(packet =>
                packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode),
                "rejected owned input publishes no summoner cast");

            FreeSummonerSlots(second, 1);
            TestAssert.True(match.TryCast(2, 0, ward, summoner: true),
                "full ward renews existing target/reveal names for mastered second channel");
            TestAssert.Equal("TeleportCancel", second.SummonerSpells[0]);
            // The shipped Destealth add is BUFF_REPLACE_EXISTING, so the mastered second cast puts its own
            // 1.0 + BuffDuration (4.5) on the existing row instead of retaining the first cast's 5.0 (the
            // hand-written cast kept the longest remaining). The reveal still covers the first channel:
            // the marker's 1.0 s tail outlives its 4.0 s end.
            TestAssert.Near(4.5,
                ward.Buffs.Single(buff => buff.Name == "Destealth").ExpiresAt, 1e-9,
                "the replace carries the mastered cast's own 1.0 + BuffDuration");

            void AssertRejected(Entity caster, string phase)
            {
                match.Clear();
                var before = caster.Position;
                TestAssert.False(match.TryCast(0, 0, ward, summoner: true), phase);
                TestAssert.Equal(before, caster.Position);
                TestAssert.Equal("SummonerTeleport", caster.SummonerSpells[0]);
                TestAssert.Near(0, caster.SummonerCooldowns[0], 1e-9);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name is
                    "SummonerTeleport" or "Teleport_DeathRemoval"));
                TestAssert.False(ward.Buffs.Any(buff => buff.Name is
                    "Teleport_Target" or "Destealth"));
                TestAssert.False(match.Captures[0].Packets.Any(packet =>
                    packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode));
            }
        }
    }

    [Check("spells-live-exhaust-capacity-includes-mobility-boots")]
    public static void ExhaustCapacityIncludesNestedMobilityBootsBuff()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var target = match.Hero(1);
            caster.SummonerSpells[0] = "SummonerExhaust";
            target.Position = caster.Position + new Position2(200, 0);
            target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            runtime.HeroStats.Refresh(target, push: false);
            // Batch 147 step 4: Items/3117.luaobj starts its Mobility penalty from its own BuffOnAllowAdd, so the copy
            // has to be bound to its slot first (BbItemModule.SyncInventory, the same next-spell-update latency the
            // 3022 cases already account for). A cast in the equip step never runs that gate, so the nested
            // BootsOfMobilityDebuff this check counts would be missing from the Exhaust add.
            runtime.Spells.ItemScripts?.SyncInventory(target);
            runtime.Delivery.Flush();
            TestAssert.True(runtime.Combat.QueueBasicAttack(target, caster));
            runtime.Combat.CombatTick(.01, regenerate: false);
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == target.NetId),
                "target has an unlaunched basic attack before Exhaust");

            foreach (var occupied in new[] { 62, 63, 64 })
            {
                FillSummonerSlots(target, occupied);
                match.Clear();
                TestAssert.False(match.TryCast(0, 0, target, summoner: true),
                    $"{team}: {occupied} slots reject all three required names");
                TestAssert.Near(0, caster.SummonerCooldowns[0], 1e-9);
                TestAssert.False(target.Buffs.Any(buff => buff.Name is
                    "BootsOfMobilityDebuff" or "ExhaustSlow" or "ExhaustDebuff"));
                TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == target.NetId),
                    "rejected Exhaust does not interrupt the target's attack");
                TestAssert.False(match.Captures[0].Packets.Any(packet =>
                    packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode));
            }

            FreeSummonerSlots(target, 3);
            TestAssert.Equal(61, target.Buffs.Count);
            TestAssert.True(match.TryCast(0, 0, target, summoner: true),
                "three free slots permit full Exhaust including Boots debuff");
            // A5-3: BBCancelAutoAttack sits only in SummonerExhaust.luaobj's own
            // OnBuffActivate, which never runs (TargetExecute adds ExhaustSlow and
            // ExhaustDebuff), so an accepted Exhaust leaves the swing alone.
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == target.NetId),
                "accepted Exhaust does not cancel the pending attack");
            TestAssert.Equal(64, target.Buffs.Count);
            foreach (var name in new[] { "BootsOfMobilityDebuff", "ExhaustSlow", "ExhaustDebuff" })
                TestAssert.True(target.Buffs.Any(buff => buff.Name == name), name);
            TestAssert.Near(210, caster.SummonerCooldowns[0], 1e-9);
            caster.SummonerCooldowns[0] = 0;
            TestAssert.True(match.TryCast(0, 0, target, summoner: true),
                "full table can renew all three exact names");
            TestAssert.Equal(64, target.Buffs.Count);
        }
    }

    [Check("spells-live-heal-capacity-skips-ally-not-caster")]
    public static void HealCapacityRejectsCasterAndSkipsSaturatedAlly()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var ally = match.Hero(2);
            caster.SummonerSpells[0] = "SummonerHeal";
            ally.Position = caster.Position + new Position2(100, 0);
            var minion = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, team, caster.Position + new Position2(150, 0))
            {
                Name = "HealCapacityAlly", Created = true, Visible = true,
                Health = 200, MaxHealth = 1000,
            };
            runtime.World.Register(minion);
            caster.Health = ally.Health = 1000;
            runtime.Delivery.Flush();
            FillSummonerSlots(caster, 64);
            match.Clear();
            TestAssert.False(match.TryCast(0, 0, caster, summoner: true),
                "caster without HealCheck slot rejects whole cast");
            TestAssert.Near(0, caster.SummonerCooldowns[0], 1e-9);
            TestAssert.Near(1000, caster.Health, 1e-9);
            TestAssert.Near(1000, ally.Health, 1e-9);
            TestAssert.Near(200, minion.Health, 1e-9);
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode));

            FreeSummonerSlots(caster, 1);
            FillSummonerSlots(ally, 64);
            match.Clear();
            TestAssert.True(match.TryCast(0, 0, caster, summoner: true));
            TestAssert.Near(1160, caster.Health, 1e-9);
            TestAssert.Near(1000, ally.Health, 1e-9,
                "saturated ally receives no heal before failed marker add");
            TestAssert.Near(280, minion.Health, 1e-9,
                "later eligible ally still receives half heal");
            TestAssert.False(ally.Buffs.Any(buff => buff.Name == "SummonerHealCheck"));
            TestAssert.True(caster.Buffs.Any(buff => buff.Name == "SummonerHealCheck"));
            TestAssert.True(minion.Buffs.Any(buff => buff.Name == "SummonerHealCheck"));
            TestAssert.False(match.Captures[2].Packets.Any(packet =>
            {
                var bytes = packet.Plaintext.Span;
                return bytes.Length >= 13 && bytes[0] == SpellPackets.BuffAddOpcode &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == ally.NetId &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) ==
                    SpellPackets.BuffNameHash("SummonerHealCheck"); // Batch 357 (restates batch 181): the binding; HealCheck has no script
            }));
            caster.SummonerCooldowns[0] = 0;
            caster.Health = 1000;
            TestAssert.True(match.TryCast(0, 0, caster, summoner: true),
                "full caster table renews its existing HealCheck");
            TestAssert.Near(1160, caster.Health, 1e-9,
                "caster HealCheck never reduces its own heal");
            TestAssert.Equal(64, caster.Buffs.Count);
        }
    }

    [Check("spells-live-teleport-capacity-reads-the-ward-marker-row")]
    public static void TeleportCapacityReservesDestealthOnlyForTheMarkerRow()
    {
        // Batch 149 step 3 settles the ward question: Spells/SightWard, VisionWard and WriggleLantern add the shared
        // marker (SharedWardBuff, one BBSpellBuffAdd in their SelfExecute) to the wards they spawn, and
        // SummonerTeleport.TargetExecute's own BBIfHasBuff reads that row before its Destealth add. The buff-table
        // gate reserves Destealth exactly when the row is there — the placed-ward stand-in that answered "row or ward
        // flag" is gone with it. Its flag term counted one marker's worth of ward twice over (the row and the flag
        // both answered for it) and still answered true alone for a ward-flagged unit that carries none — a placement
        // whose own add group was refused — where the script reads the row only, adds no Destealth, and the gate
        // reserved one row too many. The marker-present half of the same property is
        // spells-live-teleport-capacity-atomic-shared-ward: "one free ward slot cannot hold target and reveal buffs".
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var first = match.Hero(0);
            first.SummonerSpells[0] = "SummonerTeleport";
            first.Inventory = [new InventorySlot(2044), null, null, null, null, null];
            var wardPoint = new Position2(7100, 7000);
            TestAssert.True(match.TryCastGround(0, 4, wardPoint), $"{team}: owned ward item places target");
            var ward = runtime.World.Entities.Values.Single(unit => unit.IsWard &&
                unit.WardOwnerNetId == first.NetId && unit.Position == wardPoint);
            first.Position = new Position2(6000, 7000);
            runtime.Delivery.Flush();
            var marker = ward.Buffs.Single(buff => buff.Name == "SharedWardBuff");
            TestAssert.True(runtime.SpellBuffs.Remove(ward, marker.Slot),
                "the fixture takes the one marker row the script placed");
            FillSummonerSlots(ward, 63);
            match.Clear();
            TestAssert.True(match.TryCast(0, 0, ward, summoner: true),
                "one free ward slot holds the single row a marker-less ward's script adds");
            TestAssert.Equal("TeleportCancel", first.SummonerSpells[0]);
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Teleport_Target"));
            TestAssert.False(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "the script's BBIfHasBuff reads the row only and adds no reveal");
            TestAssert.Equal(64, ward.Buffs.Count, "exactly the one row the script adds was reserved");
        }
    }

    private static void FillSummonerSlots(Entity unit, int occupied)
    {
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots && unit.Buffs.Count < occupied; slot++)
        {
            if (unit.Buffs.Any(buff => buff.Slot == slot)) continue;
            unit.Buffs.Add(new BuffState($"SummonerCapacity{slot}", slot: slot, expiresAt: 60));
        }
        TestAssert.Equal(occupied, unit.Buffs.Count, "fixture fills exact buff-slot count");
    }

    private static void FreeSummonerSlots(Entity unit, int count)
    {
        foreach (var buff in unit.Buffs.Where(buff =>
                     buff.Name.StartsWith("SummonerCapacity", StringComparison.Ordinal)).Take(count).ToArray())
            unit.Buffs.Remove(buff);
    }
}
