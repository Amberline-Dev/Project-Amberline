using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Diagnostics;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("protocol.multikill-events")]
    public static void MultiKillPacketsMatchTheStockEntityEventLayout()
    {
        foreach (var (count, expected) in new[] { (2, "ab443322110488776655"),
                     (3, "ab443322110588776655"), (4, "ab443322110688776655"),
                     (5, "ab443322110788776655"), (6, "ab443322110888776655"),
                     (7, "ab443322110888776655") })
        {
            var packet = CombatPackets.HeroMultiKill(0x11223344, 0x55667788, count);
            TestAssert.Equal(Convert.FromHexString(expected), packet.Plaintext.ToArray());
            TestAssert.Equal((byte)3, packet.Channel);
            TestAssert.True(packet.Encrypt && packet.Reliability == LeaguePacketReliability.Reliable);
            var decoded = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
            TestAssert.True(decoded.HasDecoder);
            TestAssert.Equal("OnEntityEvent", decoded.Name);
            TestAssert.Equal(0x11223344u, (uint)decoded.Fields["source_netid"]!);
            TestAssert.Equal(0x55667788u, (uint)decoded.Fields["other_netid"]!);
            TestAssert.Equal((byte)Math.Min(count + 2, 8), (byte)decoded.Fields["event_id"]!);
        }
        TestAssert.Throws<ArgumentOutOfRangeException>(() => CombatPackets.HeroMultiKill(1, 2, 1));
    }

    [Check("combat.multikill-credit-and-tiers")]
    public static void MultiKillsFollowChampionCreditAcrossDeathsAndPetKills()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            var box = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.ShacoBox,
                UnitType.Minion, team, hero.Position) { OwnerNetId = hero.NetId, Health = 100, MaxHealth = 100 };
            runtime.World.Register(box);
            for (var count = 1; count <= 7; count++)
            {
                if (count > 1)
                {
                    // Seven seconds between kills, but much longer from the
                    // first to the last, exercises the rolling window.
                    runtime.Combat.CombatTick(7, regenerate: false);
                    TestAssert.True(runtime.Combat.RespawnImmediately(enemy));
                }
                match.Clear();
                TestAssert.True(runtime.Combat.Kill(enemy, count % 2 == 0 ? box.NetId : hero.NetId));
                runtime.Combat.ScheduleRespawn(enemy);
                TestAssert.False(runtime.Combat.Kill(enemy, hero.NetId));
                runtime.Delivery.Flush();
                TestAssert.Equal(count, hero.ChampionKills);
                TestAssert.Equal(count, hero.MatchStatistics.LargestMultiKill);
                TestAssert.Equal(0, match.Hero(2).MatchStatistics.LargestMultiKill, "assist/global-gold recipients do not gain a multi-kill");
                foreach (var capture in match.Captures)
                {
                    var packets = capture.Packets.Where(packet => Opcode(packet) == 0xAB).ToArray();
                    TestAssert.Equal(count == 1 ? 0 : 1, packets.Length);
                    if (count == 1) continue;
                    var bytes = packets.Single().Plaintext.Span;
                    TestAssert.Equal(10, bytes.Length);
                    TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
                    TestAssert.Equal((byte)Math.Min(count + 2, 8), bytes[5]);
                    TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]));
                    TestAssert.True(capture.Packets.FindIndex(p => Opcode(p) == 0x61) <
                        capture.Packets.FindIndex(p => Opcode(p) == 0xAB), "death is delivered before its multi-kill");
                }
                if (count == 1)
                {
                    TestAssert.True(runtime.Combat.Kill(hero), "posthumous box credit retains the timed chain");
                    runtime.Combat.ScheduleRespawn(hero);
                }
            }
            var stats = hero.MatchStatistics;
            TestAssert.Equal((1, 1, 1, 1, 2), (stats.DoubleKills, stats.TripleKills,
                stats.QuadraKills, stats.PentaKills, stats.UnrealKills));
            var frozen = stats.Snapshot(runtime.Combat.CurrentTime);
            var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team != team);
            runtime.Combat.Kill(nexus, hero.NetId);
            var result = runtime.Session.Result!;
            TestAssert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(result.Players[0].Statistics));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var saved = json.RootElement.GetProperty("Players")[0].GetProperty("Statistics");
            TestAssert.Equal(7, saved.GetProperty("LargestMultiKill").GetInt32());
            TestAssert.Equal(2, saved.GetProperty("UnrealKills").GetInt32());
        }
    }

    [Check("combat.multikill-window-boundaries")]
    public static void MultiKillWindowIgnoresUncreditedAndNonChampionDeaths()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var gap in new[] { 7.999, 8.0, 8.001 })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            TestAssert.True(runtime.Combat.Kill(enemy, hero.NetId));
            runtime.Combat.ScheduleRespawn(enemy);
            runtime.Combat.CombatTick(4, regenerate: false);
            foreach (var kind in new[] { EntityKind.Minion, EntityKind.Neutral, EntityKind.Pet, EntityKind.Ward })
            {
                var unit = new Entity(runtime.World.Allocate(UnitType.Minion), kind,
                    UnitType.Minion, enemy.Team, enemy.Position) { Health = 100, MaxHealth = 100 };
                runtime.World.Register(unit);
                TestAssert.True(runtime.Combat.Kill(unit, hero.NetId));
            }
            TestAssert.True(runtime.Combat.RespawnImmediately(enemy));
            TestAssert.True(runtime.Combat.Kill(enemy), "an execution without champion credit does not extend a chain");
            runtime.Combat.ScheduleRespawn(enemy);
            runtime.Combat.CombatTick(gap - 4, regenerate: false);
            TestAssert.True(runtime.Combat.RespawnImmediately(enemy));
            match.Clear();
            TestAssert.True(runtime.Combat.Kill(enemy, hero.NetId));
            runtime.Delivery.Flush();
            var qualifies = gap <= 8;
            TestAssert.Equal(qualifies ? 2 : 1, hero.MatchStatistics.LargestMultiKill);
            TestAssert.Equal(qualifies ? 1 : 0, hero.MatchStatistics.DoubleKills);
            foreach (var capture in match.Captures)
                TestAssert.Equal(qualifies ? 1 : 0, capture.Packets.Count(packet => Opcode(packet) == 0xAB));
        }
    }

    [Check("network.multikill-fog-and-reconnect")]
    public static void MultiKillEventsUseKnownHeroesAndAreNotReplayedOnReconnect()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, extraEnemy: true);
            var runtime = match.Runtime; var session = runtime.Session;
            var hero = match.Hero(0); var victim = match.Hero(1); var viewer = match.Hero(2);
            hero.Position = new(7000, 7000); victim.Position = new(11000, 11000); viewer.Position = new(1000, 13000);
            runtime.Delivery.Flush();
            TestAssert.False(session.Participants[2].View.Visible.Contains(hero.NetId));
            runtime.Combat.Kill(victim, hero.NetId);
            runtime.Combat.ScheduleRespawn(victim);
            TestAssert.True(runtime.Combat.RespawnImmediately(victim));
            runtime.Combat.Kill(victim, hero.NetId);
            runtime.Combat.ScheduleRespawn(victim);
            runtime.Delivery.Flush();
            var effect = match.Captures[2].Packets.Single(packet => Opcode(packet) == 0xAB);
            TestAssert.Equal(Convert.FromHexString("ab010000400402000040"), effect.Plaintext.ToArray());
            TestAssert.True(effect.Encrypt && effect.Reliability == LeaguePacketReliability.Reliable);
            TestAssert.Equal(new[] { hero.NetId, victim.NetId }, effect.EntityReferences.Select(r => r.NetId).ToArray());
            TestAssert.False(session.Participants[2].View.Visible.Contains(hero.NetId), "announcements do not reveal the killer");

            var seat = session.Participants[0];
            var statistics = hero.MatchStatistics;
            TestAssert.True(session.Disconnect(1));
            var packets = session.Receive(10, 0, session.BuildKeyCheck(seat.PlayerId)).ToList();
            packets.AddRange(session.Receive(10, 1, new byte[] { 0xC5 }));
            packets.AddRange(session.Receive(10, 1, new byte[] { 0xC6, 0, 0, 0, 0 }));
            runtime.World.Clock.SetVirtual(seat.SpawnAt!.Value);
            packets.AddRange(session.RunSchedulers());
            packets.AddRange(session.Receive(10, 1, new byte[] { 0xA4, 0, 0, 0, 0, 1 }));
            packets.AddRange(session.Receive(10, 1, new byte[] { 0xC6, 0, 0, 0, 0 }));
            packets.AddRange(session.RunSchedulers());
            TestAssert.True(seat.Spawned && seat.Ready);
            TestAssert.True(ReferenceEquals(statistics, seat.Hero!.MatchStatistics));
            TestAssert.Equal((2, 1), (statistics.LargestMultiKill, statistics.DoubleKills));
            TestAssert.False(packets.Any(packet => packet.Packet.Plaintext.Span[0] == 0xAB),
                "reconnect does not replay additive historical events");
            TestAssert.True(runtime.Combat.RespawnImmediately(victim));
            match.Clear();
            runtime.Combat.Kill(victim, hero.NetId);
            runtime.Delivery.Flush();
            TestAssert.Equal(1, statistics.TripleKills, "reconnecting does not reset the live chain");
            TestAssert.Equal((byte)5, match.Captures[2].Packets.Single(packet => Opcode(packet) == 0xAB).Plaintext.Span[5]);
        }
    }
}
