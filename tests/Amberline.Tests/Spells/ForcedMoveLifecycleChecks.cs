using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-nidalee-b5-pounce-buff-effect-lifecycle")]
    public static void NidaleePounceOwnsItsMovementBuffEffect()
    {
        foreach (var ending in new[] { "landing", "interruption", "death" })
        {
            using var match = new Match("Nidalee", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0);
            match.Cast(0, 3, hero); match.Cast(0, 1, hero);
            var buff = hero.Buffs.Single(item => item.Name == "Pounce");
            TestAssert.Equal(BuffType.Internal, buff.Type);
            TestAssert.Near(.5, buff.ExpiresAt, 0, "NID-07: half-second movement buff");
            // VFX sweep F1, 27 Sept 2026: Pounce's native auto effect follows
            // its script-filename buff; the old explicit particle was a duplicate.
            TestAssert.Equal(0u, buff.Payload.FxNetId);
            TestAssert.False(HasEffect(match.Captures[0], "Interventionspeed_buf"));
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0xBF &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == 0x0776C495u));
            match.Clear(); runtime.Session.Participants[0].View.Reset(); runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0xBF &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == 0x0776C495u),
                "reconnect restores Pounce through native buff activation");
            TestAssert.False(HasEffect(match.Captures[0], "Interventionspeed_buf"));
            if (ending == "landing") runtime.Movement.Tick(.5);
            else if (ending == "interruption") ReplaceDash(match, hero);
            else runtime.Combat.Kill(hero, match.Hero(1).NetId);
            // The buff is the script's (Pounce.BuffOnMoveEnd removes it): the buff service ticks it, not the late champion phase.
            runtime.Tick(.01); runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(item => item.Name == "Pounce"), ending);
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x7F &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == 0x0776C495u),
                "movement end retires the native buff effect");
        }
    }

    [Check("spells-live-safeguard-interruption")]
    public static void SafeguardInterruption()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var hero = match.Hero(0); var ally = match.Hero(2);
            ally.Position = hero.Position + new Position2(600, 0);
            match.Cast(0, 1, ally);
            // 19 October 2026: the dash is the script's BlindMonkWOneDash buff and its move; its travel effect is the script's SelfParticle.
            TestAssert.True(hero.ForcedMove is { Effect: "BlindMonkWOneDash" } && hero.Buffs.Any(buff => buff.Name == "BlindMonkWOneDash"),
                "Safeguard did not start its dash");
            var travelId = BinaryPrimitives.ReadUInt32LittleEndian(match.Captures[0].Packets
                .Single(packet => IsNamedVisual(packet, "blindMonk_W_self_mis")).Plaintext.Span[25..]);
            var replacement = ReplaceDash(match, hero);
            match.Clear();
            // The script's BuffOnMoveEnd removes the buff by the caster Owner, but the dash buff is added with the ally as its caster, so that remove finds
            // nothing: the buff leaves through its WillRemove flag at the next 0.25 s BuffOnUpdateActions (a quarter of a second at most).
            for (var step = 0; step < 10; step++) match.Runtime.Tick(1.0 / 30.0);
            match.Runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BlindMonkWOneDash"), "interrupted Safeguard retires its dash buff");
            TestAssert.True(ReferenceEquals(replacement, hero.ForcedMove), "Safeguard cleanup preserves the new displacement");
            TestAssert.False(hero.Buffs.Concat(ally.Buffs).Any(buff => buff.Name == "BlindMonkWOneShield"),
                "an interrupted dash does not grant landing shields");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == FxPackets.KillOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == travelId),
                "interrupted Safeguard retires its travel effect");
        }
    }

    [Check("spells-live-kick-interruption")]
    public static void KickInterruption()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        // 19 October 2026: the kick is the script's BlindMonkRKick stun on the target (its OnBuffActivate starts the move); the hand-written
        // tracking of a unit removed from the world mid-kick has no counterpart, so that ending is gone.
        foreach (var ending in new[] { "replaced", "death", "buff-replaced" })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 3, enemy);
            match.Runtime.Spells.Tick(.3);
            var kickMove = enemy.ForcedMove is { Effect: "BlindMonkRKick" } started ? started
                : throw new InvalidOperationException("Dragon's Rage did not start its kick");
            var kickBuff = enemy.Buffs.Single(buff => buff.Name == "BlindMonkRKick");
            ForcedMoveState? replacement = null;
            BuffState? newBuff = null;
            if (ending == "death") match.Runtime.Combat.Kill(enemy, hero.NetId);
            else
            {
                if (ending == "buff-replaced")
                {
                    match.Runtime.Spells.Buffs.Remove(enemy, kickBuff.Slot);
                    newBuff = match.Runtime.Spells.Buffs.Add(enemy, "replacement-control", BuffType.Stun,
                        5, enemy.NetId, new BuffPayload());
                    TestAssert.Equal(kickBuff.Slot, newBuff!.Slot, "replacement reuses the released buff slot");
                    kickMove.Completed = true;
                }
                replacement = ReplaceDash(match, enemy);
            }
            match.Clear();
            match.Runtime.Spells.Tick(.01);
            match.Runtime.Delivery.Flush();
            TestAssert.True(ReferenceEquals(replacement, enemy.ForcedMove), "kick cleanup preserves a replacement move");
            TestAssert.False(enemy.Buffs.Any(buff => ReferenceEquals(buff, kickBuff)), "the old kick stun is gone");
            if (newBuff is not null)
                TestAssert.True(enemy.Buffs.Any(buff => ReferenceEquals(buff, newBuff)), "cleanup does not remove another buff in the reused slot");
        }
    }

    private static ForcedMoveState ReplaceDash(Match match, Entity target)
    {
        var move = new ForcedMoveState(target.Position + new Position2(0, 400), 500, 1,
            target.NetId, "replacement-displacement");
        match.Runtime.Movement.StartForcedMove(target.NetId, move, cancelOrder: true);
        return move;
    }
}
