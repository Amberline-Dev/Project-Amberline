using System.Buffers.Binary;
using System.Text;
using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-leesin-w-dash-caster-animation")]
    public static void SafeguardDashPlaysAndUnlocksShippedSpell2()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var hero = match.Hero(0);
            var ally = match.Hero(2);
            ally.Position = hero.Position + new Position2(600, 0);
            match.Runtime.Delivery.Flush();
            match.Clear();

            match.Cast(0, 1, ally);
            TestAssert.True(hero.ForcedMove is { Effect: "BlindMonkWOneDash" },
                "Safeguard to an ally starts its move buff");
            var packets = match.Captures[0].Packets;
            var animations = packets.Where(packet => Opcode(packet) == 0xB8).ToArray();
            TestAssert.Equal(1, animations.Length, "the ally dash plays one explicit model clip");
            var bytes = animations[0].Plaintext.Span;
            TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
            TestAssert.Equal(0x24Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]),
                "Spell2 loops, locks, and disables blending");
            TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), 1e-6,
                "the shipped animation uses natural playback speed");
            TestAssert.Equal("Spell2", Encoding.ASCII.GetString(bytes[13..^1]));
            TestAssert.True(packets.FindIndex(packet => Opcode(packet) == 0xBD) <
                packets.IndexOf(animations[0]), "cast acknowledgement precedes dash animation");

            match.Clear();
            match.Runtime.Tick(.8);
            var stops = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x2B).ToArray();
            TestAssert.Equal(1, stops.Length, "move-buff expiry unlocks the dash clip once");
            TestAssert.Equal(0x40u,
                BinaryPrimitives.ReadUInt32LittleEndian(stops[0].Plaintext.Span[5..]),
                "Safeguard's shipped unlock blends out");
            match.Clear();
            match.Runtime.Tick(.1);
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B),
                "completed W cannot unlock twice");

            using var self = new Match("LeeSin", team);
            self.Cast(0, 1, self.Hero(0));
            TestAssert.True(self.Hero(0).ForcedMove is null &&
                !self.Hero(0).Buffs.Any(buff => buff.Name == "BlindMonkWOneDash"),
                "self Safeguard has no travel buff");
            TestAssert.False(self.Captures[0].Packets.Any(packet => Opcode(packet) == 0xB8),
                "self Safeguard must not play the dash buff's Spell2 clip");

            using var death = new Match("LeeSin", team, ally: true);
            var dying = death.Hero(0);
            death.Hero(2).Position = dying.Position + new Position2(600, 0);
            death.Cast(0, 1, death.Hero(2));
            death.Clear();
            TestAssert.True(death.Runtime.Combat.Kill(dying, death.Hero(1).NetId),
                "real death interrupts Safeguard mid-flight");
            death.Runtime.Delivery.Flush();
            TestAssert.Equal(1, death.Captures[0].Packets.Count(packet => Opcode(packet) == 0x2B),
                "death releases W's locked clip exactly once");
            death.Clear();
            death.Runtime.Tick(.1);
            TestAssert.False(death.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B),
                "dead Safeguard cannot unlock again");
        }
    }
}
