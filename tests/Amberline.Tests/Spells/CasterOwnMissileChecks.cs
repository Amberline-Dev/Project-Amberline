using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 719 (batch 713's open question 3). A child missile that is not an instacast reaches its caster's own client as the 0x41 alone
    /// (batch 393): the missile's first refresh builds it there, in place of the cast answer the other views get, and a cast answer builds the
    /// client's missile wherever it flies. The refresh was routed by sight like a late view's, so a blade whose refresh points lay behind a
    /// wall never reached its caster. Talon stands at a river-bank wall and casts Rake 540 toward (4900, 9500), inside the cone's
    /// reach (batch 713's aim, 630 away, is past it: the approach walks to a cone start in the wall and the cast never fires). Talon's own
    /// view must build all three outgoing blades, as the ally's and the enemy's do; before the fix it built 1 of 3. Ezreal's Q and
    /// Morgana's Q from the same spot reach their caster as the cast answer, before and after.
    /// </summary>
    [Check("replication.casters-own-missile-reaches-its-client-out-of-its-sight")]
    public static void CastersOwnMissileReachesItsClientOutOfItsSight()
    {
        var at = new Position2(5447, 9813);
        var toward = new Position2(4900 - at.X, 9500 - at.Z);
        var length = Math.Sqrt(toward.X * toward.X + toward.Z * toward.Z);
        var aim = new Position2(at.X + toward.X / length * 540.0, at.Z + toward.Z / length * 540.0);
        foreach (var (champion, slot, missile, count) in new[]
                 {
                     ("Ezreal", 0, "EzrealMysticShotMissile", 1), ("Morgana", 0, "DarkBindingMissile", 1), ("Talon", 1, "TalonRakeMissileOne", 3),
                 })
        {
            using var match = new Match(champion, Team.Order, ally: true);
            match.Hero(0).Position = at;
            match.Hero(1).Position = new Position2(5747, 9513);
            match.Hero(2).Position = new Position2(5247, 9613);
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.CastGround(0, aim, slot: slot);
            var hash = SpellSystem.ProtocolHash(missile);
            // Each view's missiles: a cast answer that carries one, or a missile packet.
            var built = match.Captures.Select(_ => new HashSet<uint>()).ToArray();
            for (var step = 0; step < 30; step++)
            {
                for (var view = 0; view < match.Captures.Count; view++)
                    foreach (var packet in match.Captures[view].Packets.Where(packet => Opcode(packet) is 0xBD or 0x41 && ChildSpellHash(packet) == hash))
                        if (MissileNetId(packet) != 0) built[view].Add(MissileNetId(packet));
                match.Clear();
                match.Runtime.Tick(1.0 / 30.0);
                match.Runtime.Delivery.Flush();
            }
            TestAssert.Equal(count, built[1].Count, $"{champion}: the enemy's client builds every {missile} (the cast fired)");
            TestAssert.Equal(count, built[2].Count, $"{champion}: the ally's client builds every {missile}");
            TestAssert.Equal(count, built[0].Count, $"{champion}: the caster's own client builds every {missile} it cast over the wall");
        }
    }
}
