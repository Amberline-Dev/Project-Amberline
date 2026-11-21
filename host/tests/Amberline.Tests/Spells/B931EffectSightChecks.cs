using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 931 (players, 21 November 2026; batch 930's W finding): an area effect whose centre lies in a wall reaches the enemy team only when that
/// team sees a point of the effect's <c>FOWVisibilityRadius</c> circle. Akali's Twilight Shroud (AkaliSmokeBomb.OnBuffActivate:
/// akali_smoke_bomb_tar_team_green / _red, FOWVisibilityRadius 250, bound to its placemarker cube and sent unbound at it) is the case both lines
/// share; Graves's Smoke Screen (132) is BbB931GravesChecks.
/// </summary>
public static class B931EffectSightChecks
{
    /// <summary>The first walkable point from <paramref name="from"/> toward <paramref name="toward"/>, then <paramref name="extra"/> further.</summary>
    internal static Position2 FloorBeside(NavigationGrid nav, Position2 from, Position2 toward, double extra)
    {
        var dx = toward.X - from.X;
        var dz = toward.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        for (var d = 10.0; d < length; d += 10.0)
        {
            var p = new Position2(from.X + dx / length * d, from.Z + dz / length * d);
            if (nav.IsWalkable(p)) return new Position2(from.X + dx / length * (d + extra), from.Z + dz / length * (d + extra));
        }
        throw new InvalidOperationException("no floor between the points");
    }

    internal static int Created(WireCapture capture, double after, params string[] stems) =>
        capture.Packets.Count(entry => entry.At >= after && EffectNamed(entry.Packet, stems));

    /// <summary>
    /// Twilight Shroud thrown 30 into the wall south of the hero (the wall edge at (7000,6400), batch 930's table): an enemy standing on the floor
    /// 200 from the centre (inside the smoke) gets the red smoke, as one in the open does; an enemy 2500 away gets none.
    /// </summary>
    [Check("spells.b931-a-smoke-thrown-into-a-wall-reaches-the-enemy-standing-in-it")]
    public static void SmokeInAWallReachesTheEnemyInIt()
    {
        var nav = NavigationGrid.LoadDefault();
        var wall = new Position2(7000.0, 6400.0);
        TestAssert.True(!nav.IsWalkable(wall), "the aim point is inside the wall");
        foreach (var near in new[] { true, false })
        {
            using var match = new Match("Akali");
            match.Learn(1);
            match.Refill();
            var floor = FloorBeside(nav, wall, match.Hero.Position, 0.0);
            var spot = near ? new Position2(wall.X + (floor.X - wall.X) * 200.0 / Distance(wall, floor), wall.Z + (floor.Z - wall.Z) * 200.0 / Distance(wall, floor))
                : new Position2(wall.X + 2500.0, wall.Z - 600.0);
            if (near) TestAssert.True(nav.IsWalkable(spot), $"the enemy's spot {spot} is floor");
            match.Foe.Position = spot;
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            var own = Observe(match, Team.Order, match.Hero);
            match.Advance(0.5);
            var cast = match.Now;
            TestAssert.True(match.Cast(1, at: wall), "Twilight Shroud is cast into the wall");
            match.Advance(1.0);
            TestAssert.True(Created(own, cast, "akali_smoke_bomb_tar_team_green") >= 1, "Akali's team gets the green smoke");
            var red = Created(enemy, cast, "akali_smoke_bomb_tar_team_red");
            if (near) TestAssert.True(red >= 1, $"the enemy 200 from the centre, inside the smoke, gets the red smoke ({red} creates)");
            else TestAssert.Equal(0, red, "an enemy 2500 away whose team sees none of the smoke gets none");
        }
    }

    internal static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// Batch 931 (the owner's "sounds heard from anywhere"): Ashe in a brush attacks the foe standing 400 outside it, whose team does not see her.
    /// The swing reaches the foe's client (the attacked unit is seen) while Ashe is in its fog, so that client starts the shot, and its sounds,
    /// from wherever it last held her. The swing now goes to that client as BasicAttackPos (0x1D: the 0x0D's fields and her x and z),
    /// which puts her there first (and a move of the unit when it is over 400 off). Her own
    /// client, which sees her, keeps the 0x0D.
    /// </summary>
    [Check("network.b931-an-attack-from-fog-carries-the-attackers-position")]
    public static void AttackFromFogCarriesTheAttackersPosition()
    {
        var nav = NavigationGrid.LoadDefault();
        var tried = 0;
        for (var x = 1000.0; x < 13000; x += 150)
        for (var z = 1000.0; z < 13000; z += 150)
        {
            var bush = new Position2(x, z);
            if (!nav.IsWalkable(bush) || nav.BrushIdAt(bush, 65) is null) continue;
            Position2? outside = null;
            for (var k = 0; k < 8 && outside is null; k++)
            {
                var a = k * Math.PI / 4;
                var q = new Position2(x + Math.Cos(a) * 400, z + Math.Sin(a) * 400);
                if (nav.IsWalkable(q) && nav.BrushIdAt(q, 65) is null) outside = q;
            }
            if (outside is not { } spot || ++tried > 60) continue;
            using var match = new Match("Ashe");
            match.Hero.Position = bush;
            match.Foe.Position = spot;
            match.Foe.MaxHealth = match.Foe.Health = 50000;
            match.Ally.Position = new Position2(bush.X - 3000, bush.Z - 3000);
            var own = Observe(match, Team.Order, match.Hero);
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            foreach (var observer in match.Runtime.Delivery.Observers.Where(observer => observer.ObserverId >= 9001))
                foreach (var hero in new[] { match.Hero, match.Ally, match.Foe, match.FarFoe }) observer.View.Known.Add(hero.NetId);
            match.Advance(0.7);
            Amberline.Network.Replication.ReplicationView Of(WireCapture capture) => match.Runtime.Delivery.Observers.First(o => ReferenceEquals(o.Transport, capture)).View;
            if (!Of(own).Visible.Contains(match.Foe.NetId) || Of(enemy).Visible.Contains(match.Hero.NetId)) continue;
            var start = match.Now;
            var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 3, match.Foe.Position, match.Foe.NetId);
            var participant = match.Runtime.Session.Participants[0].View;
            participant.Known.Add(match.Foe.NetId);
            participant.Visible.Add(match.Foe.NetId);
            TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "attack order");
            match.Advance(0.5);
            static bool Attack(PlaintextPacket packet, uint attacker) => packet.Plaintext.Span[0] is 0x0D or 0x1D &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == attacker;
            var mine = own.Packets.First(entry => entry.At >= start && Attack(entry.Packet, match.Hero.NetId)).Packet;
            var theirs = enemy.Packets.First(entry => entry.At >= start && Attack(entry.Packet, match.Hero.NetId)).Packet;
            TestAssert.Equal((byte)0x0D, mine.Plaintext.Span[0], "Ashe's own client gets the 0x0D");
            TestAssert.Equal((byte)0x1D, theirs.Plaintext.Span[0], "the foe's client, which does not see her, gets the positioned attack (0x1D)");
            TestAssert.Equal(23, theirs.Plaintext.Length, "0x1D: the 0x0D's 15 bytes, then x and z");
            TestAssert.True(mine.Plaintext.Span[1..15].SequenceEqual(theirs.Plaintext.Span[1..15]), "the same attacker, target, time, missile and slot");
            var px = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(theirs.Plaintext.Span[15..]);
            var pz = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(theirs.Plaintext.Span[19..]);
            TestAssert.True(Math.Abs(px - match.Hero.Position.X) < 1.0 && Math.Abs(pz - match.Hero.Position.Z) < 1.0,
                $"the position is Ashe's ({px:0},{pz:0} vs {match.Hero.Position})");
            return;
        }
        throw new InvalidOperationException($"no brush spot of {tried} with Ashe seeing the foe and the foe's team not seeing her");
    }
}
