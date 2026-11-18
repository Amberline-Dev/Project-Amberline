using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 885 (18 November 2026, player reports on 1.0.0.132: "E's animations do not work except for VFX/SFX"; "Fizz appears capable of basic
/// attacking during the untargetable part of his E"). The shipped <c>FizzJump</c> switches Ghosted on and Targetable, CanAttack and CanMove off in
/// its <c>OnBuffActivate</c>; the hop's move end clears the buff, whose <c>OnBuffDeactivate</c> adds <c>FizzJumpDelay</c> (0.2 s, Fizz on top of his
/// spear: the recast window). Nothing restores the bits there: <c>FizzJumpDelay</c>'s <c>OnBuffDeactivate</c> writes Targetable, Ghosted and
/// CanAttack back, and the <c>FizzTrickSlam</c> it adds writes CanMove back. The engine let FizzJump's statuses go with FizzJump, so on the spear
/// Fizz was targetable and swung (a queued attack, or a click) or walked off.
/// </summary>
public static class B885FizzPlayfulTricksterChecks
{
    internal static bool Cast(Match match, int slot, Position2 point)
    {
        var payload = new byte[34];
        payload[0] = 0xA3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), match.Hero.NetId);
        payload[5] = checked((byte)(slot << 1));
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, 4), (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 8, 4), (float)point.Z);
        }
        return match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    private static bool Order(Match match, byte type, Position2 point, uint target = 0)
    {
        var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, type, point, target);
        return match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay);
    }

    /// <summary>The net id a packet is about: the first entry's unit of a 0x68 group, else the sender at 1.</summary>
    private static uint About(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 5) return 0u;
        return span[0] == VisionPackets.DashOpcode && span.Length >= 19
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[15..])
            : BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
    }

    private static bool Untargetable(Entity unit) => unit.AnyBuffOrStatus(buff => buff.Payload.Untargetable);

    /// <summary>Fizz at level 9 with every rank, an Orianna 150 east (in reach), both clients watching; E aimed 300 east.</summary>
    internal static (Match Match, WireCapture Own, WireCapture Enemy) Arena()
    {
        var match = new Match("Fizz", foeChampion: "Orianna");
        var hero = match.Hero;
        hero.Level = 9;
        match.Learn(5);
        match.Refill();
        match.Runtime.HeroStats.Refresh(hero, push: false);
        match.Ally.Position = new Position2(hero.Position.X - 400.0, hero.Position.Z);
        match.Foe.Position = new Position2(hero.Position.X + 150.0, hero.Position.Z);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        var own = Observe(match, Team.Order, hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(match.Foe.NetId);
        view.Visible.Add(match.Foe.NetId);
        match.Advance(Step);
        return (match, own, enemy);
    }

    private sealed record Case(string Name, double At, Action<Match>? Act, bool QueuedAttack = false);

    private static readonly Case[] SpearCases =
    [
        new("an attack queued before E", -1.0, null, QueuedAttack: true),
        new("an attack click during the hop", 0.1, match => Order(match, 3, match.Foe.Position, match.Foe.NetId)),
        new("an attack click on the spear", 0.55, match => Order(match, 3, match.Foe.Position, match.Foe.NetId)),
        new("a move click on the spear", 0.55, match => Order(match, 2, new Position2(match.Hero.Position.X, match.Hero.Position.Z + 400.0))),
    ];

    [Check("spells-live-fizz-playful-trickster-holds-fizz-untargetable-and-still-on-the-spear")]
    public static void PlayfulTricksterHoldsItsStatusesOnTheSpear()
    {
        foreach (var item in SpearCases)
        {
            var (match, own, enemy) = Arena();
            using var scope = match;
            var hero = match.Hero;
            if (item.QueuedAttack)
            {
                var before = own.Packets.Count;
                TestAssert.True(Order(match, 3, match.Foe.Position, match.Foe.NetId), $"{item.Name}: the attack order");
                for (var step = 0; step < 60 && !own.Packets.Skip(before).Any(entry => entry.Packet.Plaintext.Span[0] == 0x0D); step++) match.Advance(Step);
                match.Advance(Step * 3);
            }
            var start = match.Now;
            var from = new[] { own.Packets.Count, enemy.Packets.Count };
            TestAssert.True(Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), $"{item.Name}: Playful is accepted");
            var acted = item.Act is null;
            var spear = false;
            double? slam = null;
            var leaks = new List<string>();
            for (var step = 0; step < 45 && slam is null; step++)
            {
                if (!acted && match.Now - start >= item.At - 1e-6)
                {
                    item.Act!(match);
                    acted = true;
                }
                match.Advance(Step);
                if (hero.Buffs.Any(buff => buff.Name == "FizzTrickSlam"))
                {
                    slam = match.Now;
                    break;
                }
                spear |= hero.Buffs.Any(buff => buff.Name == "FizzJumpDelay");
                var state = CharacterState.Of(hero);
                if (!Untargetable(hero) || state.CanAttack || state.CanMove)
                    leaks.Add($"{match.Now - start:0.000}: untargetable={Untargetable(hero)} canAttack={state.CanAttack} canMove={state.CanMove} " +
                              $"[{string.Join(",", hero.Buffs.Where(buff => buff.Name.StartsWith("Fizz", StringComparison.Ordinal)).Select(buff => buff.Name))}]");
            }
            TestAssert.True(spear && slam is not null, $"{item.Name}: Fizz stands on the spear (FizzJumpDelay), then slams (FizzTrickSlam)");
            TestAssert.True(leaks.Count == 0, $"{item.Name}: from the cast to the slam Fizz is untargetable and can neither attack nor move: " + string.Join(" | ", leaks));
            var ownAfter = own.Packets.Skip(from[0]).Where(entry => entry.At < slam!.Value && About(entry.Packet) == hero.NetId).ToList();
            TestAssert.False(ownAfter.Any(entry => entry.Packet.Plaintext.Span[0] == 0x0D),
                $"{item.Name}: no basic attack (0x0D) starts before the slam: " +
                string.Join(",", ownAfter.Where(entry => entry.Packet.Plaintext.Span[0] == 0x0D).Select(entry => $"{entry.At - start:0.000}")));
            TestAssert.False(ownAfter.Any(entry => entry.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode && entry.Packet.Plaintext.Length > 17),
                $"{item.Name}: no walk route is sent before the slam");
            // The enemy's client is told Fizz is not targetable (0xF4, IsTargetable at 5): every flush before the slam that carries a 0xF4 for Fizz
            // leaves him untargetable (the client applies a flush's packets in order in one frame, so its last 0xF4 is the state it keeps).
            var states = enemy.Packets.Skip(from[1]).Where(entry => entry.At < slam!.Value && entry.Packet.Plaintext.Span[0] == CombatPackets.UnitTargetableStatusOpcode &&
                                                                   About(entry.Packet) == hero.NetId)
                .GroupBy(entry => entry.At).Select(flush => (At: flush.Key, Targetable: flush.Last().Packet.Plaintext.Span[5] != 0)).ToList();
            TestAssert.True(states.Count > 0 && states.All(entry => !entry.Targetable),
                $"{item.Name}: every flush before the slam leaves Fizz untargetable on the enemy's client: " +
                string.Join(",", states.Select(entry => $"{entry.At - start:0.000}:{entry.Targetable}")));
            match.Advance(Step * 3);
            var after = CharacterState.Of(hero);
            TestAssert.True(!Untargetable(hero) && after.CanAttack && after.CanMove, $"{item.Name}: the slam gives Fizz back targetable, attack and move");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count,
                $"{item.Name}: no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
        }
    }

    // The recast window: FizzJumpTwo carries no CantCastWhileRooted (FizzJump does), so the second jump is taken with CanMove off on the spear.
    [Check("spells-live-fizz-trickster-recast-on-the-spear-jumps-again")]
    public static void TricksterRecastOnTheSpearJumpsAgain()
    {
        var (match, _, _) = Arena();
        using var scope = match;
        var hero = match.Hero;
        TestAssert.True(Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), "Playful is accepted");
        for (var step = 0; step < 30 && !hero.Buffs.Any(buff => buff.Name == "FizzJumpDelay"); step++) match.Advance(Step);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FizzJumpDelay") && hero.ForcedMove is null, "Fizz stands on the spear");
        TestAssert.Equal("FizzJumpTwo", match.Module.SpellName(hero, 2), "slot 2 is the recast on the spear");
        TestAssert.True(Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), "Trickster is accepted on the spear");
        match.Advance(Step);
        TestAssert.True(hero.ForcedMove is { Effect: "FizzJumpTwo" } && Untargetable(hero), "the second jump runs and Fizz stays untargetable");
        for (var step = 0; step < 45 && hero.ForcedMove is not null; step++) match.Advance(Step);
        match.Advance(Step);
        var state = CharacterState.Of(hero);
        TestAssert.True(hero.ForcedMove is null && !Untargetable(hero) && state.CanAttack && state.CanMove, "the landing gives Fizz back targetable, attack and move");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name is "FizzTrickSlam" or "FizzJumpDelay"), "the recast takes the place of the slam");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }
}
