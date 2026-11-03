using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-janna-q-charge-caster-animation-flags")]
    public static void JannaGaleUsesTheShippedNonblendedChargeGesture()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team);
            var owner = match.Hero(0);
            match.CastGround(0, owner.Position + new Position2(250, 0));
            var gesture = match.Captures[0].Packets.Single(packet =>
                packet.Plaintext.Span[0] == 0xB8);
            AssertCasterGesture(gesture, owner.NetId, "Spell1", 0x208u);

            match.Clear();
            match.Cast(0, 0, owner);
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                packet.Plaintext.Span[0] == 0xB8),
                team + ": Q release does not restart the caster gesture");
        }
    }

    [Check("spells-live-yorick-w-child-caster-animation")]
    public static void YorickPestilencePresentsItsChildCastGesture()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var owner = match.Hero(0);
            var start = owner.Position;
            match.CastGround(0, start + new Position2(100, 0), slot: 1);

            var packets = match.Captures[0].Packets;
            // VFX sweep F4, 27 Sept 2026: the normal child cast supplies
            // Spell1 and its FX; the old animation-only workaround is gone.
            var gesture = packets.Single(packet => IsB6Child(packet, 0x00556F54));
            AssertB6ChildCast(gesture, owner, 0x00556F54, 42, .25, 0, 0x04);
            TestAssert.False(packets.Any(packet => Opcode(packet) == 0xB8));
            var gestureIndex = packets.IndexOf(gesture);
            TestAssert.True(packets.FindIndex(packet => packet.Plaintext.Span[0] ==
                    SpellPackets.CastSpellAnswerOpcode) < gestureIndex,
                team + ": accepted W precedes its child summon gesture");
            TestAssert.False(packets.Skip(gestureIndex + 1).Any(packet =>
                    packet.Plaintext.Span[0] == 0xC1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner.NetId),
                team + ": no later same-flush stationary route cancels Spell1");
            // Batch 279: the cast's stop seats Yorick on his lattice anchor at once; the gesture moves him no further.
            TestAssert.Equal(match.Runtime.Movement.ClientAnchor(start), owner.Position,
                team + ": the gesture does not move Yorick");
            TestAssert.False(packets.Any(packet => packet.Plaintext.Span[0] == 0x2B),
                team + ": non-loop child cast needs no premature animation stop");

            // Playtest #21/#22, 25 Sept 2026: the 0.25 s summon windup is
            // uncancellable on the stock client. A move during it is held
            // (SpellWindup) and sends no stop-animation or stationary route over Spell1.
            var windupMark = packets.Count;
            match.Move(0, start + new Position2(0, 300));
            var windupOrder = packets.Skip(windupMark).ToArray();
            TestAssert.False(windupOrder.Any(packet => packet.Plaintext.Span[0] == 0x2B),
                team + ": a move during the windup keeps Spell1");
            TestAssert.False(windupOrder.Any(packet => IsOwnerRoute(packet, owner.NetId)),
                team + ": a move during the windup sends no stationary Yorick route");

            for (var tick = 0; tick < 12 && !match.Runtime.World.Entities.Values.Any(unit =>
                         unit.Kind == EntityKind.YorickGhoul); tick++)
                match.Runtime.Tick(0.05);
            var ghoul = match.Runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickGhoul && unit.OwnerNetId == owner.NetId);
            var summon = packets.FindIndex(packet => packet.Plaintext.Span[0] == 0x80 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == ghoul.NetId);
            TestAssert.True(summon > gestureIndex,
                team + ": the child Spell1 gesture precedes the delayed ghoul summon");
            TestAssert.True(owner.Waypoints.Count == 0,
                team + ": the held windup move is not applied before the windup ends");

            // VFX sweep F4, 27 Sept 2026: the native cast owns its winddown;
            // movement no longer stops a synthetic Spell1 gesture.
            // 19 October 2026 (batch 109): the ghoul now appears at the cast frame (YorickSummonDecayed.SelfExecute), and the dispatcher keeps the held
            // move until 0.1 s after it (batch 76), so the held move is the first move after the summon: it runs by itself within that gap.
            var summonedMark = packets.Count;
            match.Runtime.Tick(0.2);
            var firstMove = packets.Skip(summonedMark).ToArray();
            TestAssert.Equal(0, firstMove.Count(packet => packet.Plaintext.Span[0] == 0x2B &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner.NetId),
                team + ": the first move after the summon leaves native cast animation ownership intact");
            TestAssert.True(firstMove.Any(packet => IsOwnerRoute(packet, owner.NetId)),
                team + ": the same move publishes Yorick's route");
            var nextMark = packets.Count;
            match.Move(0, start + new Position2(0, 350));
            TestAssert.False(packets.Skip(nextMark).Any(packet => packet.Plaintext.Span[0] == 0x2B),
                team + ": later moves have no gesture left to stop");
        }

        static bool IsOwnerRoute(PlaintextPacket packet, uint owner) =>
            packet.Plaintext.Span[0] == 0xC1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner;
    }

    [Check("spells-live-janna-e-scripted-caster-animation")]
    public static void JannaShieldPresentsItsScriptedCasterGesture()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var allyTarget in new[] { false, true })
        {
            using var match = new Match("Janna", team, ally: allyTarget);
            var owner = match.Hero(0);
            var target = allyTarget ? match.Hero(2) : owner;
            var start = owner.Position;
            if (allyTarget) match.Hero(1).Position = new Position2(11000, 11000);
            if (allyTarget) match.Move(0, start + new Position2(300, 0));
            // Batch 61: a hero order seats the hero on the lattice point its client path starts on (up to 1.4 units), so the
            // gesture is measured from where the order left Janna.
            start = owner.Position;
            var route = owner.Waypoints.ToArray();
            match.Clear();
            match.Cast(0, 2, target);

            var packets = match.Captures[0].Packets;
            var gesture = packets.Single(packet => packet.Plaintext.Span[0] == 0xB8);
            AssertCasterGesture(gesture, owner.NetId, "Spell3", 0x208u);
            TestAssert.False(packets.Any(packet => packet.Plaintext.Span[0] == 0xB8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
                target != owner),
                team + ": shield recipient is not the animation actor");
            TestAssert.True(packets.FindIndex(packet => packet.Plaintext.Span[0] ==
                    SpellPackets.CastSpellAnswerOpcode) < packets.IndexOf(gesture),
                team + ": accepted E precedes the scripted gesture");
            TestAssert.Equal(start, owner.Position,
                team + ": the shield gesture does not move Janna");
            TestAssert.False(packets.Any(packet => packet.Plaintext.Span[0] == 0x2B),
                team + ": the non-loop shield gesture has no premature stop");
            if (!allyTarget) continue;
            TestAssert.True(route.Length > 0 && owner.Waypoints.SequenceEqual(route),
                team + ": E gesture preserves the accepted movement order");
            match.Runtime.Tick(0.1);
            TestAssert.True(owner.Position.X > start.X,
                team + ": E gesture does not freeze moving Janna");
        }
    }

    private static void AssertCasterGesture(PlaintextPacket packet,
        uint owner, string name, uint flags)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal((byte)0xB8, bytes[0], "caster animation opcode");
        TestAssert.Equal(owner, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
            "animation belongs to the casting hero");
        TestAssert.Equal(flags, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]),
            "shipped animation loop/lock/blend flags");
        TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]),
            "animation keeps natural playback duration");
        TestAssert.Equal(name, Encoding.ASCII.GetString(bytes[13..^1]),
            "animation name maps to the shipped model clip");
    }
}
