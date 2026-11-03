using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("vision-b6-ward-placement-stealth-delay")]
    public static void ItemWardsRemainVisibleForTwoSeconds()
    {
        foreach (var item in new[] { 2044, 2043, 3154 })
        {
            using var match = new Match("Annie", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            owner.Inventory = [new InventorySlot(item), null, null, null, null, null];
            var point = new Position2(7100, 7000);
            TestAssert.True(match.TryCastGround(0, 4, point));
            var ward = runtime.World.Entities.Values.Single(unit => unit.IsWard);
            enemy.Position = new Position2(7400, 7000);
            AssertVisible(true, "placement");
            TestAssert.False(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "placement interval does not borrow Teleport's reveal marker");
            runtime.Tick(1.9);
            AssertVisible(true, "1.9 seconds");
            runtime.Tick(0.1);
            AssertVisible(false, "LifeTime >= 2.0 (SightWard/VisionWard/WriggleLanternWard scripts)");
            runtime.SpellBuffs.Add(enemy, "OracleElixirSight", BuffType.CombatEnhancer,
                10.0, enemy.NetId);
            AssertVisible(true, "true sight after placement stealth");

            void AssertVisible(bool expected, string phase)
            {
                runtime.Delivery.Flush();
                TestAssert.Equal(expected, runtime.Session.Participants[1].View.Visible.Contains(ward.NetId),
                    $"item {item}: {phase}");
            }
        }
    }

    [Check("spells-live-teleport-ward-mixed-channel-duration")]
    public static void ShorterMasteredChannelCannotEndEarlierWardReveal()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cancelSecond in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var first = match.Hero(0);
            var second = match.Hero(2);
            var enemy = match.Hero(1);
            first.SummonerSpells[0] = second.SummonerSpells[0] = "SummonerTeleport";
            second.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
            {
                [124] = 1, [134] = 3, [132] = 3, [130] = 1,
                [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
            });
            runtime.HeroStats.Refresh(second, push: false);
            first.Inventory = [new InventorySlot(2044), null, null, null, null, null];
            var wardPoint = new Position2(7100, 7000);
            TestAssert.True(match.TryCastGround(0, 4, wardPoint));
            var ward = runtime.World.Entities.Values.Single(unit => unit.IsWard &&
                unit.WardOwnerNetId == first.NetId && unit.Position == wardPoint);
            // VIS-02, 27 Sept 2026 base-game sweep: Teleport starts on a mature ward.
            runtime.Tick(2.0);
            first.Position = new Position2(6000, 7000);
            second.Position = new Position2(6200, 7000);
            enemy.Position = new Position2(7400, 7000);
            runtime.Delivery.Flush();
            var enemyView = runtime.Session.Participants[1].View;
            TestAssert.False(enemyView.Visible.Contains(ward.NetId));

            match.Cast(0, 0, ward, summoner: true);
            runtime.Tick(0.1);
            var firstRemaining = ward.Buffs.Single(buff => buff.Name == "Destealth").ExpiresAt;
            // The shipped marker is added with its own Duration 1.0 on top of its DurationVar BuffDuration
            // (the channel's), so it runs one second past the channel it was cast for.
            TestAssert.Near(4.9, firstRemaining, 1e-6, "Destealth is Duration 1.0 + BuffDuration");
            match.Clear();
            match.Cast(2, 0, ward, summoner: true);
            // The script adds the marker BUFF_REPLACE_EXISTING: the second cast's own 1.0 + BuffDuration
            // (4.5) replaces the earlier deadline instead of retaining it (the hand-written cast kept the
            // longest remaining). Its one-second tail is what keeps the ward revealed past the first
            // channel's end (4.6 > 4.0): the mixed-duration continuity is the tail's, not a Max of deadlines.
            TestAssert.Near(4.5,
                ward.Buffs.Single(buff => buff.Name == "Destealth").ExpiresAt, 1e-6,
                "the shipped replace carries the second cast's own deadline");
            // Batch 157 part 3 / batch 162: the second cast re-adds the marker into a slot the observer already
            // holds under the same name hash, and Destealth.luaobj carries no AutoBuffActivateEffect (its
            // OnBuffActivate clears Stealth and its own BBSpellEffectCreate is the 0x8C/0x3D pair the renewals do
            // not touch), so ReplicationDelivery.AsHeldBuffUpdate sends the silent NpcBuffReplace (0x32:
            // slot @5, duration @10, caster @14) instead of a second 0xBF that would replay the activation sound.
            // The first cast is still the 0xBF add (the slot is new), and its Duration 1.0 + BuffDuration is pinned
            // in AssertDestealthDuration below.
            var renewal = match.Captures[0].Packets.Last(packet =>
            {
                var bytes = packet.Plaintext.Span;
                return bytes.Length >= 18 && bytes[0] == SpellPackets.BuffReplaceOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == ward.NetId &&
                    bytes[5] == ward.Buffs.Single(buff => buff.Name == "Destealth").Slot;
            });
            // Batch 365 (wire grace): the replace is a renewal-type add, so the renewal's duration carries one step of margin.
            TestAssert.Near(4.5 + Amberline.Simulation.Spells.Bb.BbBuffRuntime.WireGraceStep,
                BinaryPrimitives.ReadSingleLittleEndian(renewal.Plaintext.Span[10..]), 1e-5,
                "the silent renewal publishes the replaced 1.0 + BuffDuration plus its wire margin");

            if (cancelSecond)
            {
                runtime.Tick(1.1);
                var channel = second.Buffs.Single(buff => buff.Name == "SummonerTeleport");
                TestAssert.True(runtime.SpellBuffs.Remove(second, channel.Slot));
                runtime.Tick(0.05);
            }
            else
                runtime.Tick(3.55);
            TestAssert.Equal("TeleportCancel", first.SummonerSpells[0],
                "original normal-duration channel remains active");
            TestAssert.Equal("SummonerTeleport", second.SummonerSpells[0],
                "shorter mastered channel has ended");
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "shorter channel release retains the shared marker");
            TestAssert.True(enemyView.Visible.Contains(ward.NetId),
                "enemy keeps ordinary sight to ward while the original channel runs");
            runtime.Tick(cancelSecond ? 3.0 : 0.5);
            TestAssert.Equal("SummonerTeleport", first.SummonerSpells[0]);
            // Both channels have ended 4.0 s after the first cast and the replaced marker still holds its
            // 1.0 s tail; nothing in the shipped scripts removes it early.
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "the marker outlives both channel ends");
            TestAssert.True(enemyView.Visible.Contains(ward.NetId),
                "the ward stays revealed until the marker's own expiry");
            runtime.Tick(0.75);
            TestAssert.False(ward.Buffs.Any(buff => buff.Name == "Destealth"));
            TestAssert.False(enemyView.Visible.Contains(ward.NetId),
                "marker expiry restores implicit ward stealth");
        }
    }

    [Check("spells-live-teleport-ward-destealth-overlap")]
    public static void TeleportRevealsItemWardThroughItsScriptedDestealthTail()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var mastered in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var first = match.Hero(0);
            var second = match.Hero(2);
            var enemy = match.Hero(1);
            first.SummonerSpells[0] = second.SummonerSpells[0] = "SummonerTeleport";
            if (mastered)
            {
                var page = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
                {
                    [124] = 1, [134] = 3, [132] = 3, [130] = 1,
                    [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
                });
                first.Masteries = second.Masteries = page;
                runtime.HeroStats.Refresh(first, push: false);
                runtime.HeroStats.Refresh(second, push: false);
            }
            var duration = mastered ? 3.5 : 4.0;
            var wardPoint = new Position2(7100, 7000);
            first.Inventory = [new InventorySlot(2044), null, null, null, null, null];
            TestAssert.True(match.TryCastGround(0, 4, wardPoint),
                $"{team} mastered={mastered}: actual sight ward item places");
            var ward = runtime.World.Entities.Values.Single(unit => unit.IsWard &&
                unit.WardOwnerNetId == first.NetId && unit.Position == wardPoint);
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "SightWard"),
                "item ward retains its named invisibility buff");
            // Batch 149 step 3: the ward is the shipped Spells/SightWard.luaobj's own SelfExecute now, and that
            // script places the shared ward marker (SharedWardBuff) on the ward it spawns. The non-retail stand-in
            // that answered for it in BbSpellHost.SpellBuffCount is gone with it.
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "SharedWardBuff"),
                "item ward carries the scripted shared ward marker");
            // VIS-02, 27 Sept 2026 base-game sweep: Teleport starts on a mature ward.
            runtime.Tick(2.0);
            first.Position = new Position2(6000, 7000);
            second.Position = new Position2(6200, 7000);
            enemy.Position = new Position2(7400, 7000);
            runtime.Delivery.Flush();
            var enemyView = runtime.Session.Participants[1].View;
            TestAssert.True(VisibilityRules.ProvidersReveal(
                    runtime.Dependencies.VisionEnvironment,
                    VisibilityRules.BuildTeamProviders(runtime.World, enemy.Team,
                        runtime.Delivery.Balls, runtime.Dependencies.AdditionalProviders),
                    ward.Position),
                "enemy has ordinary line of sight and range to the ward position");
            AssertWardVisible(false, "before Teleport");
            TestAssert.True(VisibilityRules.IsStealthed(ward),
                "mature item ward starts the Teleport scenario stealthed");

            match.Clear();
            match.Cast(0, 0, ward, summoner: true);
            runtime.Delivery.Flush();
            AssertDestealthDuration(duration, first.NetId, "first channel");
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "SightWard"),
                "Teleport does not remove the ward's unrelated invisibility buff");
            TestAssert.False(runtime.Combat.IsTargetable(ward, enemy),
                "visible ward remains protected by Teleport_Target during channel");
            AssertWardVisible(true, "first channel");
            runtime.Tick(1.2);
            AssertWardVisible(true, "after the obsolete one-second reveal interval");

            match.Clear();
            match.Cast(2, 0, ward, summoner: true);
            runtime.Delivery.Flush();
            AssertDestealthDuration(duration, second.NetId, "second channel");
            var untilFirstEnds = duration - 1.2 + 0.05;
            runtime.Tick(untilFirstEnds);
            TestAssert.Equal("SummonerTeleport", first.SummonerSpells[0],
                "first channel completes");
            TestAssert.Equal("TeleportCancel", second.SummonerSpells[0],
                "overlapping second channel remains active");
            AssertWardVisible(true, "after first channel completes");
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "first release retains the shared Destealth marker");

            var secondChannel = second.Buffs.Single(buff => buff.Name == "SummonerTeleport");
            TestAssert.True(runtime.SpellBuffs.Remove(second, secondChannel.Slot),
                "external removal cancels the remaining channel");
            runtime.Tick(0.05);
            TestAssert.Equal("SummonerTeleport", second.SummonerSpells[0],
                "second channel cancels");
            // The shipped scripts never take the marker off at a channel end: Teleport_DeathRemoval strips
            // only the target lock, and only on a TeleportCancel cast or the caster's death. Both rows run to
            // their own timers — Destealth to Duration 1.0 + BuffDuration from the last add, Teleport_Target
            // to Duration 0.1 + BuffDuration — so the ward outlives the channel revealed and protected.
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "the marker outlives the channel end on its own timer");
            TestAssert.True(ward.Buffs.Any(buff => buff.Name == "SightWard"),
                "original ward invisibility buff survives cancellation");
            AssertWardVisible(true, "while the shipped marker tail runs");
            TestAssert.False(runtime.Combat.IsTargetable(ward, enemy),
                "the target lock outlives the channel end too");

            runtime.Tick(3.0);
            TestAssert.False(ward.Buffs.Any(buff => buff.Name == "Destealth"),
                "the marker expires one second past the last channel");
            TestAssert.True(VisibilityRules.IsStealthed(ward),
                "marker expiry restores ward stealth");
            AssertWardVisible(false, "after the marker's expiry");
            // Batch 149 step 3: the ward is hidden by the ward script's own Stealth buff now, and that buff's
            // Payload.Stealthed is what the targeting gate reads, so an enemy without true sight cannot target a
            // released ward either. The old call measured Teleport_Target and that gate at once (a ward used to be
            // stealthed by a flag no payload carried); the protection itself is measured without an observer.
            TestAssert.True(runtime.Combat.IsTargetable(ward, observer: null),
                "the shipped Teleport_Target lock expires on its own timer");
            TestAssert.False(runtime.Combat.IsTargetable(ward, enemy),
                "the restored ward stealth keeps it from an enemy that cannot see it");

            void AssertWardVisible(bool expected, string phase)
            {
                runtime.Delivery.Flush();
                var raw = VisibilityRules.ComputeRawVisibility(runtime.World, enemy.Team,
                    runtime.Dependencies.VisionEnvironment,
                    VisibilityRules.BuildTeamProviders(runtime.World, enemy.Team,
                        runtime.Delivery.Balls, runtime.Dependencies.AdditionalProviders),
                    point => runtime.Dependencies.RevealsStealth(enemy.Team, point));
                TestAssert.Equal(expected, raw.Contains(ward.NetId),
                    $"{team} mastered={mastered}: raw enemy sight {phase}");
                TestAssert.Equal(expected, enemyView.Visible.Contains(ward.NetId),
                    $"{team} mastered={mastered}: observer sight {phase}");
            }

            void AssertDestealthDuration(double channelSeconds, uint casterNetId, string phase)
            {
                // The shipped add is Duration 1.0 on top of its DurationVar BuffDuration (the channel's), so
                // the marker runs one second past the channel it was cast for.
                var expected = channelSeconds + 1.0;
                var buff = ward.Buffs.Single(candidate => candidate.Name == "Destealth");
                TestAssert.Near(expected, buff.ExpiresAt, 1e-8,
                    $"{team} mastered={mastered}: {phase} uses the script's Duration 1.0 + BuffDuration");
                TestAssert.Equal(casterNetId, buff.CasterNetId,
                    $"{team} mastered={mastered}: {phase} records its caster");
                // The first cast's add is a 0xBF (name hash @9, duration @17). The second cast re-adds into a slot
                // the observer already holds under the same name, and Destealth.luaobj carries no
                // AutoBuffActivateEffect, so ReplicationDelivery.AsHeldBuffUpdate sends the silent
                // NpcBuffReplace (0x32: slot @5, duration @10) instead of a second 0xBF that would replay the
                // activation sound (batch 157 part 3, named by batch 162). Both publish the same deadline.
                var destealthHash = SpellPackets.BuffNameHash("Destealth");
                var packet = match.Captures[0].Packets.Last(candidate =>
                {
                    var bytes = candidate.Plaintext.Span;
                    if (bytes.Length < 18 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != ward.NetId)
                        return false;
                    return bytes[0] == SpellPackets.BuffAddOpcode &&
                            bytes.Length >= 25 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) == destealthHash ||
                        bytes[0] == SpellPackets.BuffReplaceOpcode && bytes[5] == buff.Slot;
                });
                var durationOffset = packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode ? 17 : 10;
                // Batch 365 (wire grace): the marker is a BUFF_REPLACE_EXISTING add (renewal-type), so its wire duration carries one step of margin.
                TestAssert.Near(expected + Amberline.Simulation.Spells.Bb.BbBuffRuntime.WireGraceStep,
                    BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[durationOffset..]), 1e-6,
                    $"{team} mastered={mastered}: {phase} publishes native buff duration plus its wire margin");
            }
        }
    }
}
