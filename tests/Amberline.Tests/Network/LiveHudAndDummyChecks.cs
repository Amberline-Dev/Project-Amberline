using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("server.training-dummy-command")]
    public static void DummyCommandCreatesStationaryUnownedChampions()
    {
        using var match = new Match("Ezreal", Team.Order);
        var runtime = match.Runtime;
        var roster = runtime.Session.Participants.Select(p => p.Hero!.NetId).ToArray();
        match.Hero(0).Facing = new(1, 0);
        foreach (var (command, team) in new[] { ("/dummy", Team.Chaos), ("/dummy order", Team.Order), ("/dummy chaos", Team.Chaos) })
        {
            match.Hero(0).Position += new Position2(0, 400);
            match.Hero(1).Position = match.Hero(0).Position + new Position2(0, 100);
            var oldIds = runtime.World.Entities.Keys.ToHashSet();
            Chat(command);
            var dummy = runtime.World.Entities.Values.Single(e => e.IsTrainingDummy && !oldIds.Contains(e.NetId));
            TestAssert.Equal("Ezreal", dummy.ChampionName);
            TestAssert.Equal(18, dummy.Level);
            TestAssert.Equal(team, dummy.Team);
            TestAssert.True(dummy.Health > 1000 && runtime.Navigation.IsMovementWalkable(dummy.Position));
            TestAssert.False(dummy.HeroAutoAcquire);
            var position = dummy.Position;
            runtime.Tick(1);
            TestAssert.Equal(position, dummy.Position);
            TestAssert.True(dummy.AttackTarget is null && dummy.Waypoints.Count == 0);
            var packet = match.Captures[0].Packets.Single(p => Opcode(p) == 0x4F && Id(p.Plaintext.Span, 5) == dummy.NetId);
            TestAssert.Equal(uint.MaxValue, Id(packet.Plaintext.Span, 9), "a dummy cannot reuse a player's client ID");
            TestAssert.True(runtime.SpellCombat.ApplyDamage(match.Hero(team == Team.Order ? 1 : 0), dummy,
                10, DamageType.True, DamageSourceKind.Spell));
        }
        TestAssert.True(roster.SequenceEqual(runtime.Session.Participants.Select(p => p.Hero!.NetId)));
        var count = runtime.World.Entities.Count;
        Chat("/dummy invalid");
        TestAssert.Equal(count, runtime.World.Entities.Count);
        while (runtime.World.Allocator.Next(UnitType.Hero) < NetIdAllocator.RangeFor(UnitType.Hero).Ceiling)
            runtime.World.Allocate(UnitType.Hero);
        Chat("/dummy");
        TestAssert.Equal(count, runtime.World.Entities.Count, "an exhausted ID range rejects the command without crashing");

        void Chat(string text)
        {
            var packet = ChatPackets.Envelope(0, false, Encoding.ASCII.GetBytes(text));
            runtime.Session.Receive(1, ChatPackets.Channel, packet.ToWireBytes(runtime.Cipher));
            runtime.Delivery.Flush();
        }
    }

    [Check("replication.unknown-buff-names-stay-server-side")]
    public static void InternalMarkersDoNotTriggerNativeHashMapAssertions()
    {
        using var match = new Match("Blitzcrank", Team.Order);
        var runtime = match.Runtime;
        var target = match.Hero(1);
        TestAssert.False(runtime.GameData.HasClientBuff("SummonerBeaconMagicImmune"));
        TestAssert.False(runtime.GameData.HasClientBuff("StaticFieldSilence"));
        TestAssert.True(runtime.GameData.HasClientBuff("Silence"));
        var buff = runtime.SpellBuffs.Add(target, "SummonerBeaconMagicImmune", BuffType.Stun,
            10, target.NetId, new BuffPayload { MagicImmune = true, ActionDisabled = true });
        TestAssert.True(buff is not null && target.Buffs.Contains(buff));
        runtime.Delivery.Flush();
        foreach (var capture in match.Captures)
            TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0xBF &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("SummonerBeaconMagicImmune")));
        foreach (var observer in runtime.Delivery.Observers)
            observer.View.Visible.Remove(target.NetId);
        match.Clear();
        runtime.Delivery.Flush();
        foreach (var capture in match.Captures)
            TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0xBF &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("SummonerBeaconMagicImmune")),
                "reveal snapshots obey the same native buff-name restriction");
    }
}
