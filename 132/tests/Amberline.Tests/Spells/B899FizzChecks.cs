using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 899 (19 November 2026, owner on 1.0.0.132: "Fizz E still allows him to autoattack when he's in the air"; "Fizz Q sometimes doesn't
/// put him inside of an animation, making him run instead"). Both came from a swing the stock client still plays after the server dropped it:
/// <list type="bullet">
/// <item>FizzJump and FizzPiercingStrike are instacasts (SpellData*Flags 4). Under an instacast the client keeps its current swing
/// (batch 575, <c>Client_DoCastSpell</c> -> <c>TryCastInstaSpell</c>), but the server marked every cast as replacing it, so Playful's own
/// <c>BBCancelAutoAttack</c> dropped the swing without a 0x39 and the client played the swing's hit in the hop.</item>
/// <item>Urchin Strike on a champion while Fizz wound up on a minion: the script's AI_ATTACKTO dropped that swing after the Spell1 clip, and
/// the 0x39 made the client stop the current animation (the swing had not cast), so the dash ran in the run
/// pose. A CANCEL_ORDER move now drops the swing, and tells the client, before its dash packet.</item>
/// </list>
/// </summary>
public static class B899FizzChecks
{
    private const byte StopAttackOpcode = 0x39;

    private static bool Cast(Match match, int slot, Position2 point, uint target = 0)
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
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    private static void Attack(Match match, Entity target)
    {
        var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 3, target.Position, target.NetId);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "the attack order");
    }

    private static uint About(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 5) return 0u;
        return span[0] == VisionPackets.DashOpcode && span.Length >= 19
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[15..])
            : BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
    }

    private static string Clip(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span[0] != 0xB9) return "";
        var end = span[13..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(span.Slice(13, end < 0 ? span.Length - 13 : end));
    }

    private static string Show(IEnumerable<PlaintextPacket> packets) =>
        string.Join(" ", packets.Select(packet => packet.Plaintext.Span[0] == 0xB9 ? "B9:" + Clip(packet) : packet.Plaintext.Span[0].ToString("X2")));

    /// <summary>Fizz's own swing is in its windup (its 0x0D went out a step ago).</summary>
    private static void WindUp(Match match, WireCapture own, Entity target)
    {
        var before = own.Packets.Count;
        Attack(match, target);
        for (var step = 0; step < 90 && !own.Packets.Skip(before).Any(entry => entry.Packet.Plaintext.Span[0] == 0x0D && About(entry.Packet) == match.Hero.NetId); step++)
            match.Advance(Step);
        match.Advance(Step);
        TestAssert.True(match.Runtime.Combat.IsWindingUpOn(match.Hero, target), "Fizz's swing is in its windup");
    }

    [Check("spells-live-fizz-playful-in-a-swing-windup-tells-the-client-to-drop-the-swing")]
    public static void PlayfulInAWindupTellsTheClient()
    {
        foreach (var windup in new[] { true, false })
        {
            var name = windup ? "E in the windup of a swing at Orianna" : "E with no swing (control)";
            var (match, own, _) = B885FizzPlayfulTricksterChecks.Arena();
            using var scope = match;
            var hero = match.Hero;
            if (windup) WindUp(match, own, match.Foe);
            var foeHealth = match.Foe.Health;
            var from = own.Packets.Count;
            TestAssert.True(Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), $"{name}: Playful is accepted");
            match.Advance(Step);
            var cast = own.Packets.Skip(from).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
            var hop = cast.FindIndex(packet => Clip(packet) == "Spell3a");
            var stops = cast.Select((packet, index) => (packet, index)).Where(entry => entry.packet.Plaintext.Span[0] == StopAttackOpcode).ToList();
            TestAssert.True(hop >= 0 && hero.ForcedMove is not null, $"{name}: Fizz hops with Spell3a: {Show(cast)}");
            if (windup)
            {
                TestAssert.True(stops.Count == 1 && stops[0].index < hop && (stops[0].packet.Plaintext.Span[5] & CombatPackets.CancelWindupFlags) != 0,
                    $"{name}: the client is told the swing is dropped (one 0x39 with DestroyMissile) before the hop's clip: {Show(cast)}");
                TestAssert.False(match.Runtime.Combat.IsWindingUpOn(hero, match.Foe), $"{name}: the server dropped the swing");
            }
            else TestAssert.Equal(0, stops.Count, $"{name}: no swing, no stop: {Show(cast)}");
            for (var step = 0; step < 20; step++) match.Advance(Step);
            TestAssert.True(match.Foe.Health >= foeHealth - 1e-6, $"{name}: no hit lands on Orianna in the air ({foeHealth - match.Foe.Health:0.0})");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{name}: no script failed");
        }
    }

    /// <summary>
    /// The tail of Playful's slam: <c>FizzTrickSlam</c> (0.45 s) unseals Q at its end and adds <c>UnlockAnimation</c> (0.25 s), whose end unlocks
    /// the locked slam clip. A Q there was cast under the client's lock and the unlock reached the client in the dash; it now goes before the
    /// cast. A W during the slam itself (no <c>UnlockAnimation</c> yet) leaves the slam alone.
    /// </summary>
    [Check("spells-live-fizz-urchin-strike-right-after-the-slam-keeps-its-clip")]
    public static void UrchinStrikeRightAfterTheSlam()
    {
        foreach (var w in new[] { false, true })
        {
            var name = w ? "W during the slam (control)" : "Q in the slam's unlock tail";
            using var match = new Match("Fizz", foeChampion: "Orianna");
            var hero = match.Hero;
            hero.Level = 9;
            match.Learn(5);
            match.Refill();
            match.Runtime.HeroStats.Refresh(hero, push: false);
            match.Ally.Position = new Position2(hero.Position.X - 600.0, hero.Position.Z - 600.0);
            match.Foe.Position = new Position2(hero.Position.X + 150.0, hero.Position.Z);
            match.Foe.MaxHealth = match.Foe.Health = 50000.0;
            var view = match.Runtime.Session.Participants[0].View;
            view.Known.Add(match.Foe.NetId);
            view.Visible.Add(match.Foe.NetId);
            var own = Observe(match, Team.Order, hero);
            match.Advance(Step);
            TestAssert.True(Cast(match, 2, new Position2(hero.Position.X - 200.0, hero.Position.Z)), $"{name}: Playful is accepted");
            for (var step = 0; step < 60 && !hero.Buffs.Any(buff => buff.Name == "FizzTrickSlam"); step++) match.Advance(Step);
            TestAssert.True(hero.AnimationLocked, $"{name}: the slam clip is locked");
            if (w) match.Advance(0.1);
            else for (var step = 0; step < 30 && !hero.Buffs.Any(buff => buff.Name == "UnlockAnimation"); step++) match.Advance(Step);
            TestAssert.True(hero.AnimationLocked && hero.Buffs.Any(buff => buff.Name == (w ? "FizzTrickSlam" : "UnlockAnimation")),
                $"{name}: the lock still holds [{Names(hero)}]");
            var from = own.Packets.Count;
            TestAssert.True(w ? Cast(match, 1, hero.Position) : Cast(match, 0, match.Foe.Position, match.Foe.NetId), $"{name}: the cast is accepted");
            if (w)
            {
                match.Advance(Step);
                var flush = own.Packets.Skip(from).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
                TestAssert.False(flush.Any(packet => packet.Plaintext.Span[0] == 0x2B), $"{name}: W does not cut the slam: {Show(flush)}");
                TestAssert.True(hero.AnimationLocked, $"{name}: the slam keeps its lock");
                continue;
            }
            for (var step = 0; step < 45 && hero.ForcedMove is null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" }, $"{name}: the dash runs");
            for (var step = 0; step < 45 && hero.ForcedMove is not null; step++) match.Advance(Step);
            var about = own.Packets.Skip(from).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
            var unlock = about.FindIndex(packet => packet.Plaintext.Span[0] == 0x2B);
            var cast = about.FindIndex(packet => packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode);
            var clip = about.FindIndex(packet => Clip(packet) == "Spell1");
            var landing = about.FindIndex(packet => packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
            TestAssert.True(unlock >= 0 && cast > unlock && clip > cast && landing > clip,
                $"{name}: the slam's unlock, then the cast, Spell1 and the landing: {Show(about)}");
            TestAssert.Equal(0, about.Skip(clip + 1).Take(landing - clip - 1).Count(packet => packet.Plaintext.Span[0] is StopAttackOpcode or 0x2B),
                $"{name}: nothing stops Spell1 during the dash: {Show(about)}");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{name}: no script failed");
        }
    }

    /// <summary>
    /// The guard of the CANCEL_ORDER drop: Headbutt (an instacast) plays its unlocked Spell2 before its <c>BBMove</c>, so a stop at the move would cut
    /// that clip. The swing Alistar winds up when he presses W is left to the client, as before, and nothing stops the clip during the dash.
    /// </summary>
    [Check("spells-live-headbutt-in-a-windup-keeps-its-clip")]
    public static void HeadbuttInAWindupKeepsItsClip()
    {
        using var match = new Match("Alistar", foeChampion: "Orianna");
        var hero = match.Hero;
        hero.Level = 9;
        match.Learn(5);
        match.Refill();
        match.Runtime.HeroStats.Refresh(hero, push: false);
        match.Ally.Position = new Position2(hero.Position.X - 600.0, hero.Position.Z - 600.0);
        match.Foe.Position = new Position2(hero.Position.X + 200.0, hero.Position.Z);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(match.Foe.NetId);
        view.Visible.Add(match.Foe.NetId);
        var own = Observe(match, Team.Order, hero);
        match.Advance(Step);
        WindUp(match, own, match.Foe);
        var from = own.Packets.Count;
        TestAssert.True(Cast(match, 1, match.Foe.Position, match.Foe.NetId), "Headbutt is accepted");
        for (var step = 0; step < 30 && hero.ForcedMove is null; step++) match.Advance(Step);
        for (var step = 0; step < 45 && hero.ForcedMove is not null; step++) match.Advance(Step);
        var about = own.Packets.Skip(from).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
        var clip = about.FindIndex(packet => Clip(packet) == "Spell2");
        var dash = about.FindIndex(packet => packet.Plaintext.Span[0] == VisionPackets.DashOpcode);
        TestAssert.True(clip >= 0 && dash > clip, $"Spell2, then the dash: {Show(about)}");
        var landing = about.FindIndex(dash, packet => packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
        var end = landing < 0 ? about.Count : landing;
        TestAssert.Equal(0, about.Skip(clip + 1).Take(end - clip - 1).Count(packet => packet.Plaintext.Span[0] == StopAttackOpcode),
            $"no 0x39 cuts Spell2 before the landing: {Show(about)}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    private sealed record QCase(string Name, Position2 Swing, Position2 Victim, bool SwingAtVictim = false);

    private static readonly QCase[] QCases =
    [
        new("Q on a champion out of reach, Fizz winding up on a minion", new Position2(120, 0), new Position2(400, 150)),
        new("Q on a champion beside him, Fizz winding up on a minion north", new Position2(0, 120), new Position2(450, 0)),
        new("Q on a champion in reach, Fizz winding up on it", new Position2(0, 0), new Position2(150, 0), SwingAtVictim: true),
    ];

    [Check("spells-live-fizz-urchin-strike-keeps-its-clip-when-a-swing-is-dropped")]
    public static void UrchinStrikeKeepsItsClip()
    {
        foreach (var item in QCases)
        {
            using var match = new Match("Fizz", foeChampion: "Orianna");
            var hero = match.Hero;
            hero.Level = 9;
            match.Learn(5);
            match.Refill();
            match.Runtime.HeroStats.Refresh(hero, push: false);
            match.Ally.Position = new Position2(hero.Position.X - 600.0, hero.Position.Z - 600.0);
            var victim = match.Foe;
            victim.Position = new Position2(hero.Position.X + item.Victim.X, hero.Position.Z + item.Victim.Z);
            victim.MaxHealth = victim.Health = 50000.0;
            var view = match.Runtime.Session.Participants[0].View;
            Entity swingAt = victim;
            if (!item.SwingAtVictim)
            {
                swingAt = LaneMinion(match, new Position2(hero.Position.X + item.Swing.X, hero.Position.Z + item.Swing.Z));
                swingAt.MaxHealth = swingAt.Health = 50000.0;
            }
            foreach (var unit in new[] { victim, swingAt })
            {
                view.Known.Add(unit.NetId);
                view.Visible.Add(unit.NetId);
            }
            var own = Observe(match, Team.Order, hero);
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            match.Advance(Step);
            WindUp(match, own, swingAt);
            var from = new[] { own.Packets.Count, enemy.Packets.Count };
            TestAssert.True(Cast(match, 0, victim.Position, victim.NetId), $"{item.Name}: Urchin Strike is accepted");
            for (var step = 0; step < 45 && hero.ForcedMove is null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" }, $"{item.Name}: the dash runs");
            for (var step = 0; step < 45 && hero.ForcedMove is not null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is null, $"{item.Name}: Fizz lands");
            for (var index = 0; index < 2; index++)
            {
                var (who, packets) = index == 0 ? ("Fizz's client", own.Packets) : ("the enemy's client", enemy.Packets);
                var about = packets.Skip(from[index]).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
                var dash = about.FindIndex(packet => packet.Plaintext.Span[0] == VisionPackets.DashOpcode);
                var clip = about.FindIndex(packet => Clip(packet) == "Spell1");
                var landing = about.FindIndex(packet => packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
                TestAssert.True(dash >= 0 && clip > dash && landing > clip, $"{item.Name}: {who} gets the dash, Spell1, then the landing: {Show(about)}");
                // Between the clip and the landing's stop nothing may stop the clip: no 0x39 and no 0x2B.
                var cutting = about.Skip(clip + 1).Take(landing - clip - 1).Where(packet => packet.Plaintext.Span[0] is StopAttackOpcode or 0x2B).ToList();
                TestAssert.Equal(0, cutting.Count, $"{item.Name}: {who} gets nothing that stops Spell1 during the dash: {Show(about)}");
                var stop = about.FindIndex(packet => packet.Plaintext.Span[0] == StopAttackOpcode);
                TestAssert.True(stop >= 0 && stop < dash && (about[stop].Plaintext.Span[5] & CombatPackets.CancelWindupFlags) != 0,
                    $"{item.Name}: {who} is told the dropped swing (0x39 with DestroyMissile) before the dash: {Show(about)}");
            }
            TestAssert.False(match.Runtime.Combat.IsWindingUpOn(hero, swingAt), $"{item.Name}: the swing is dropped");
            TestAssert.Equal(victim.NetId, hero.AttackTarget ?? 0u, $"{item.Name}: the script's attack order on the champion stands");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{item.Name}: no script failed");
        }
    }
}
