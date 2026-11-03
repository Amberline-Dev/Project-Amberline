using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 361 (26 October 2026; owner findings 17, 19, 39, 55, 63, 66, 76, 84): a hero that channels a spell makes no basic attack of its own from
/// the press to the channel's end (the owner: "any channel should disable autoattacking"), and its server channel lasts the shipped script's
/// <c>ChannelDuration</c>. Reproduce-first: the check fails on the code before the batch.
/// </summary>
public static class BbChannelAttackLockChecks
{
    /// <summary>
    /// The channelled spells of the research list (<c>r1-channels.md</c>, fifteen champions) with the slot and the aim the harness presses them with:
    /// a foe, an ally, or no target (a point at the foe, which self casts and cones ignore).
    /// </summary>
    private static readonly (string Champion, int Slot, string Aim)[] ChannelSpells =
    [
        ("Nunu", 3, "self"), ("Malzahar", 3, "foe"), ("Caitlyn", 3, "foe"), ("Fiddlesticks", 3, "foe"), ("Fiddlesticks", 1, "foe"),
        ("Katarina", 3, "self"), ("Karthus", 3, "self"), ("Galio", 3, "self"), ("Gragas", 1, "self"), ("Warwick", 3, "foe"),
        ("MasterYi", 1, "self"), ("MissFortune", 3, "self"), ("Pantheon", 2, "foe"), ("Pantheon", 3, "self"), ("Janna", 3, "self"),
        ("Shen", 3, "ally"),
    ];
    // Twisted Fate's Instagate (R, the four-second channel) is not in the table: a press of Destiny (slot 3) does not switch the slot in the
    // harness, so the channel never opens (open question, batch 361 record).

    /// <summary>One press run to the end of its channel: the server channel's window and the caster's own basic attacks on the wire inside it.</summary>
    private sealed record ChannelRun(BbChannel? Channel, double Start, double End, int Swings, bool Pressed);

    /// <summary>
    /// Every channelled spell of the list, with an enemy hero 100 units from the caster (inside every basic attack range) and the caster's idle
    /// acquisition on (the harness turns it off). From the press to the channel's end the caster sends no basic attack (0x0D, its own netId), and the
    /// server channel runs its shipped <c>ChannelDuration</c> (Miss Fortune's Bullet Time is 2.2 s; the old 0.25 s wave grid ended it at 2.0 s).
    /// </summary>
    [Check("scripting.bb-channels-take-no-automatic-swing-and-last-their-duration")]
    public static void EveryChannelHoldsItsSwingAndItsDuration()
    {
        var failures = new List<string>();
        foreach (var (champion, slot, aim) in ChannelSpells)
        {
            using var match = new BbM3.Match(champion);
            match.Learn(3);
            match.Refill();
            match.Advance(0.5);
            match.Hero.HeroAutoAcquire = true;
            match.Foe.Position = new Position2(match.Hero.Position.X + 100.0, match.Hero.Position.Z);
            var wire = BbM3.Observe(match, Team.Order, match.Hero);
            var target = aim switch { "foe" => match.Foe, "ally" => match.Ally, _ => null };
            // The run outlasts the longest channel of the list (Meditate's five seconds) and the cast frame before it.
            var run = Run(match, wire, slot, target, seconds: 8.0);
            if (run.Channel is null)
            {
                failures.Add($"{champion} slot {slot} ({match.Module.SpellName(match.Hero, slot)}): no channel opened (press accepted: {run.Pressed})");
                continue;
            }
            var name = run.Channel.Script.Name;
            var duration = BbScriptChannels.DurationOf(run.Channel.Script);
            var lifetime = run.End - run.Start;
            if (run.Swings != 0)
                failures.Add($"{champion} ({name}): {run.Swings} basic attack(s) of its own from the press to the channel's end");
            if (Math.Abs(lifetime - duration) > 2 * BbM3.Step + 1e-6)
                failures.Add($"{champion} ({name}): the channel lasts {lifetime:0.###}s, its ChannelDuration is {duration:0.###}s");
        }
        TestAssert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// Batch 406 follow-up (orchestrator, 27 October 2026): the held attack flow. The hero is attacking the foe (a 0x75 attack order; the hero
    /// walks into reach or swings), then Drain is cast on the same foe with no further packet. The held attack must not end the channel, and
    /// the hero makes no swing inside it; the channel runs its ChannelDuration. Red on master where the held attack walks the hero during the
    /// channel (Move stop) or ends it.
    /// </summary>
    [Check("scripting.bb-channel-holds-against-an-attack-order-on-the-wire")]
    public static void AHeldAttackOrderDoesNotEndDrain()
    {
        var failures = new List<string>();
        foreach (var (name, run) in new[] { ("in reach", HeldAttackRun(100.0, 1.2)), ("walking in", HeldAttackRun(500.0, 0.3)) })
        {
            if (!run.CastAccepted || run.Start is null) { failures.Add($"{name}: the cast does not open a channel"); continue; }
            var lifetime = run.End - run.Start.Value;
            if (Math.Abs(lifetime - 5.0) > 2 * BbM3.Step + 1e-6)
                failures.Add($"{name}: the channel lasted {lifetime:0.###} s of its 5 s ChannelDuration {run.Note}");
            if (run.SwingsInChannel != 0) failures.Add($"{name}: {run.SwingsInChannel} swing(s) inside the channel");
        }
        TestAssert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// Batch 406 follow-up: a NEW player attack order (0x75, order 3) sent during the channel still ends it at once, as the master order handler
    /// does (a right-click on the foe during Drain ends it).
    /// </summary>
    [Check("scripting.bb-channel-new-attack-order-ends-drain-on-the-wire")]
    public static void ANewAttackOrderStillEndsDrain()
    {
        var run = WireChannel(order: 3);
        TestAssert.True(run.CastAccepted && run.Start is not null, "the Drain cast on the wire opens its channel");
        TestAssert.True(run.OrderAccepted, "the new attack order is accepted on the wire");
        var lifetime = run.End - run.Start!.Value;
        TestAssert.True(lifetime < 1.0, $"a new attack order ends Drain at once (the channel lasted {lifetime:0.###} s, its ChannelDuration is 5 s)");
    }

    /// <summary>
    /// One held-attack run: an attack order on the foe at <paramref name="distance"/> units, then the W cast on the same foe after
    /// <paramref name="castAfter"/> seconds with no further packet. Returns the channel window and the hero's swings inside it.
    /// </summary>
    private static WireRun HeldAttackRun(double distance, double castAfter)
    {
        using var match = new BbM3.Match("Fiddlesticks");
        match.Learn(3);
        match.Refill();
        var hero = match.Hero;
        hero.HeroAutoAcquire = true;
        match.Foe.Position = new Position2(hero.Position.X + distance, hero.Position.Z);
        var seat = match.Runtime.Session.Participants[0];
        var wire = new BbM3.WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(
            seat.PlayerId, seat.Team, hero.NetId, wire, ready: true, view: seat.View));
        match.Runtime.Delivery.Flush();
        match.Advance(0.5);
        match.Runtime.Delivery.Flush();
        var attack = new byte[34];
        attack[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(attack.AsSpan(1), hero.NetId);
        attack[5] = 3;
        WritePoint(attack, match.Foe.Position);
        BinaryPrimitives.WriteUInt32LittleEndian(attack.AsSpan(18), match.Foe.NetId);
        var orderAccepted = SendClientPacket(match, attack);
        match.Advance(castAfter);
        match.Runtime.Delivery.Flush();
        match.Foe.MaxHealth = match.Foe.Health = 100000.0;
        var cast = new byte[34];
        cast[0] = 0xA2;
        cast[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(1), hero.NetId);
        WritePoint(cast, match.Foe.Position);
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(30), match.Foe.NetId);
        var castAccepted = SendClientPacket(match, cast);
        var pressedAt = match.Now;
        double? start = null, end = null;
        Position2? startAt = null;
        for (var step = 0; step < (int)Math.Round(8.0 / BbM3.Step); step++)
        {
            KeepFoeAlive(match);
            match.Runtime.Tick(BbM3.Step);
            var channel = match.Module.Channels!.Of(hero);
            if (channel is not null && start is null) { start = match.Now - pressedAt; startAt = hero.Position; }
            if (channel is null && start is not null) { end = match.Now - pressedAt; break; }
        }
        var finish = end ?? match.Now - pressedAt;
        var swings = wire.Packets.Count(entry => entry.Packet.Plaintext.Span is { Length: >= 5 } bytes && bytes[0] == 0x0D &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == hero.NetId &&
            entry.At - pressedAt >= (start ?? 0.0) - 1e-9 && entry.At - pressedAt <= finish + 1e-9);
        var endPackets = string.Join(",", wire.Packets
            .Where(entry => Math.Abs(entry.At - pressedAt - finish) < 0.05 && entry.Packet.Plaintext.Span[0] != 0xCC)
            .Select(entry => $"0x{entry.Packet.Plaintext.Span[0]:X2}").Distinct());
        var moved = startAt is { } origin ? Math.Sqrt(Math.Pow(hero.Position.X - origin.X, 2) + Math.Pow(hero.Position.Z - origin.Z, 2)) : 0.0;
        var note = $"[foe alive={match.Foe.Alive} hp={match.Foe.Health:0}; hero moved {moved:0.0} units; end packets ({endPackets})]";
        return new WireRun(castAccepted, orderAccepted, start, finish, swings, note);
    }

    /// <summary>
    /// The owner's decision (27 October 2026) keeps the move: a move order still ends Drain on the wire (the stock Drain has no move lock).
    /// </summary>
    [Check("scripting.bb-channel-move-order-still-ends-a-drain-channel-on-the-wire")]
    public static void AMoveOrderStillEndsADrainChannel()
    {
        var run = WireChannel(order: 2);
        TestAssert.True(run.CastAccepted && run.Start is not null, "the Drain cast on the wire opens its channel");
        TestAssert.True(run.OrderAccepted, "the move order is accepted on the wire");
        var lifetime = run.End - run.Start!.Value;
        TestAssert.True(lifetime < 1.0,
            $"a move order ends Drain at once (the channel lasted {lifetime:0.###} s, its ChannelDuration is 5 s)");
    }

    /// <summary>One wire run: the owner's W press on the foe, a client order 0.5 s into the channel, the channel's window and the hero's swings in it.</summary>
    private sealed record WireRun(bool CastAccepted, bool OrderAccepted, double? Start, double End, int SwingsInChannel, string Note = "");

    /// <summary>
    /// Fiddlesticks' Drain (slot 1) cast the way the stock client sends it: an encrypted 0xA2 through the production dispatch, from a seat that
    /// sees the foe (DeathRecapLiveChecks.LiveMatch's observer). The hero idles beside the foe and swings for 1.5 s first. Then the order packet
    /// (0x75: order 3 attacks the foe, order 2 walks 300 units) is sent 0.5 s into the channel.
    /// </summary>
    private static WireRun WireChannel(byte order)
    {
        using var match = new BbM3.Match("Fiddlesticks");
        match.Learn(3);
        match.Refill();
        var hero = match.Hero;
        hero.HeroAutoAcquire = true;
        match.Foe.Position = new Position2(hero.Position.X + 100.0, hero.Position.Z);
        var seat = match.Runtime.Session.Participants[0];
        var wire = new BbM3.WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(
            seat.PlayerId, seat.Team, hero.NetId, wire, ready: true, view: seat.View));
        match.Runtime.Delivery.Flush();
        match.Advance(0.5);
        match.Advance(1.5);
        match.Runtime.Delivery.Flush();
        // The foe is re-based by the stat refresh of the match start; its health is set after that, so Drain cannot kill it inside its channel.
        match.Foe.MaxHealth = match.Foe.Health = 100000.0;
        var cast = new byte[34];
        cast[0] = 0xA2;
        cast[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(1), hero.NetId);
        WritePoint(cast, match.Foe.Position);
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(30), match.Foe.NetId);
        var castAccepted = SendClientPacket(match, cast);
        var pressedAt = match.Now;
        var orderAt = double.NaN;
        var orderAccepted = false;
        double? start = null, end = null;
        for (var step = 0; step < (int)Math.Round(8.0 / BbM3.Step); step++)
        {
            KeepFoeAlive(match);
            match.Runtime.Tick(BbM3.Step);
            var channel = match.Module.Channels!.Of(hero);
            if (channel is not null && start is null) start = match.Now - pressedAt;
            if (channel is null && start is not null && end is null) { end = match.Now - pressedAt; break; }
            if (start is not null && double.IsNaN(orderAt) && match.Now - pressedAt >= start.Value + 0.5)
            {
                orderAt = match.Now - pressedAt;
                var packet = new byte[34];
                packet[0] = 0x75;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
                packet[5] = order;
                WritePoint(packet, order == 2 ? new Position2(hero.Position.X + 300.0, hero.Position.Z) : match.Foe.Position);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(18), order == 2 ? 0u : match.Foe.NetId);
                orderAccepted = SendClientPacket(match, packet);
            }
        }
        var finish = end ?? match.Now - pressedAt;
        var swings = wire.Packets.Count(entry => entry.Packet.Plaintext.Span is { Length: >= 5 } bytes && bytes[0] == 0x0D &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == hero.NetId &&
            entry.At - pressedAt >= (start ?? 0.0) - 1e-9 && entry.At - pressedAt <= finish + 1e-9);
        return new WireRun(castAccepted, orderAccepted, start, finish, swings);
    }

    /// <summary>
    /// DIAGNOSTICS (batch 406 follow-up): the harness keeps the foe alive through a channel. The stat refresh resets its health after the
    /// match's own writes, and Drain's damage then kills the foe at about 4.8 s, which ends the channel as a lost target (the wire shows the
    /// foe's Die, 0x61). Only the foe's health is held up; the channel's own rules are untouched.
    /// </summary>
    private static void KeepFoeAlive(BbM3.Match match)
    {
        if (match.Foe.Health >= 50000.0) return;
        match.Foe.MaxHealth = match.Foe.Health = 100000.0;
    }

    /// <summary>A 34-byte client packet's point: X at offset 6 and Z at offset 14, Y left at 0 (the stock client's Vector layout).</summary>
    private static void WritePoint(byte[] packet, Position3 point)
    {
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(6), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(14), (float)point.Z);
    }

    private static void WritePoint(byte[] packet, Position2 point) => WritePoint(packet, new Position3(point.X, 0, point.Z));

    /// <summary>Sends one encrypted client packet on the gameplay channel through the production dispatch and reports whether it was accepted.</summary>
    private static bool SendClientPacket(BbM3.Match match, byte[] packet)
    {
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(packet), context =>
        {
            accepted = match.Runtime.Dependencies.Gameplay(context);
            return accepted;
        });
        match.Runtime.Delivery.Flush();
        return accepted;
    }

    /// <summary>Presses a slot at the target (a point at the foe when there is none), or at the caster's own point when the press is refused.</summary>
    private static bool Press(BbM3.Match match, int slot, Entity? target)
    {
        var point = (target ?? match.Foe).Position;
        return match.Cast(slot, target, at: point) || match.Cast(slot, null, at: point) || match.Cast(slot, null, at: match.Hero.Position);
    }

    /// <summary>Presses the slot and runs the runtime until the caster's channel ends (or <paramref name="seconds"/> pass).</summary>
    private static ChannelRun Run(BbM3.Match match, BbM3.WireCapture wire, int slot, Entity? target, double seconds)
    {
        var pressedAt = match.Now;
        var pressed = Press(match, slot, target);
        BbChannel? channel = null;
        var start = 0.0;
        var end = 0.0;
        var open = false;
        for (var step = 0; step < (int)Math.Round(seconds / BbM3.Step); step++)
        {
            match.Runtime.Tick(BbM3.Step);
            var running = match.Module.Channels!.Of(match.Hero);
            if (running is not null && !open)
            {
                channel = running;
                start = match.Now;
                open = true;
            }
            else if (running is null && open)
            {
                end = match.Now;
                open = false;
                break;
            }
        }
        if (open) end = match.Now;
        var swings = wire.Packets.Count(packet =>
            packet.At >= pressedAt - 1e-9 && packet.At <= end + 1e-9 && IsBasicAttackOf(packet.Packet, match.Hero));
        return new ChannelRun(pressed ? channel : null, start, end, swings, pressed);
    }

    /// <summary>
    /// A basic attack (0x0D, <c>CombatPackets.BasicAttack</c>) of the hero: the attacker's netId follows the opcode byte.
    /// </summary>
    private static bool IsBasicAttackOf(PlaintextPacket packet, Entity hero)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 5 && bytes[0] == 0x0D && BitConverter.ToUInt32(bytes.Slice(1, 4)) == hero.NetId;
    }

    /// <summary>
    /// Batch 427 (owner, 28 October 2026, finding 17: "click to move, halfway the path click W, Fiddlesticks instantly breaks the channeling with an
    /// autoattack"). The owner's sequence on the production wire path: a move order (0x75, order 2) to a point past a foe, then Drain (0xA2, slot 1)
    /// on the foe once the hero has walked half the path. Drain must channel its full ChannelDuration and the hero makes no basic attack of its own
    /// inside it. Red on master: the cast keeps the walk, so the channel's Move check ends it at the cast frame and the idle acquisition then
    /// swings at the foe.
    /// </summary>
    [Check("scripting.bb-channel-cast-while-walking-holds-on-the-wire")]
    public static void DrainCastWhileWalkingHoldsItsChannel()
    {
        var run = WalkingWireChannel();
        TestAssert.True(run.Start is not null, $"Drain opened no channel after the walking cast (press accepted: {run.CastAccepted})");
        TestAssert.True(Math.Abs(run.Lifetime - run.Duration) <= 2 * BbM3.Step + 1e-6,
            $"the channel lasted {run.Lifetime:0.###}s, its ChannelDuration is {run.Duration:0.###}s (the walk ended it)");
        TestAssert.True(run.Swings == 0, $"{run.Swings} basic attack(s) of the hero from the press to the channel's end");
    }

    private sealed record WalkingRun(bool CastAccepted, double? Start, double Lifetime, double Duration, int Swings);

    /// <summary>
    /// The owner's sequence: the hero idles 0.5 s, the foe stands 900 units out (beyond the 750 acquisition range), a move order (0x75, order 2)
    /// walks the hero 1600 units out, and when the straight-line walk reaches 800 units (about halfway) Drain is cast on the foe (0xA2, slot 1)
    /// through the production dispatch. The channel and the hero's swings are read per step until the channel ends (or 3 s pass without one).
    /// </summary>
    private static WalkingRun WalkingWireChannel()
    {
        using var match = new BbM3.Match("Fiddlesticks");
        match.Learn(3);
        match.Refill();
        match.Advance(0.5);
        var hero = match.Hero;
        hero.HeroAutoAcquire = true;
        var start = hero.Position;
        match.Foe.Position = new Position2(start.X + 900.0, start.Z);
        var seat = match.Runtime.Session.Participants[0];
        var wire = new BbM3.WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(
            seat.PlayerId, seat.Team, hero.NetId, wire, ready: true, view: seat.View));
        match.Runtime.Delivery.Flush();
        match.Advance(0.5);
        match.Runtime.Delivery.Flush();
        match.Foe.MaxHealth = match.Foe.Health = 100000.0;
        var move = new byte[34];
        move[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(move.AsSpan(1), hero.NetId);
        move[5] = 2;
        WritePoint(move, new Position2(start.X + 1600.0, start.Z));
        TestAssert.True(SendClientPacket(match, move), "the move order is accepted");
        var castSent = false;
        var castAccepted = false;
        var pressedAt = 0.0;
        double? channelStart = null;
        var duration = 0.0;
        var end = 0.0;
        for (var step = 0; step < (int)Math.Round(10.0 / BbM3.Step); step++)
        {
            KeepFoeAlive(match);
            match.Runtime.Tick(BbM3.Step);
            var walked = Math.Sqrt(Math.Pow(hero.Position.X - start.X, 2) + Math.Pow(hero.Position.Z - start.Z, 2));
            if (!castSent && walked >= 800.0)
            {
                var cast = new byte[34];
                cast[0] = 0xA2;
                cast[5] = 2;
                BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(1), hero.NetId);
                WritePoint(cast, match.Foe.Position);
                WriteEndPoint(cast, match.Foe.Position);
                BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(30), match.Foe.NetId);
                castAccepted = SendClientPacket(match, cast);
                castSent = true;
                pressedAt = match.Now;
            }
            var channel = match.Module.Channels!.Of(hero);
            if (channel is not null && channelStart is null)
            {
                channelStart = match.Now - pressedAt;
                duration = BbScriptChannels.DurationOf(channel.Script);
            }
            if (channel is null && channelStart is not null)
            {
                end = match.Now - pressedAt;
                break;
            }
            if (castSent && channelStart is null && match.Now - pressedAt > 3.0) break;
        }
        if (channelStart is not null && end == 0.0) end = match.Now - pressedAt;
        var swings = wire.Packets.Count(entry => entry.At >= pressedAt - 1e-9 && entry.At <= pressedAt + end + 1e-9 &&
            IsBasicAttackOf(entry.Packet, hero));
        return new WalkingRun(castAccepted, channelStart, end - (channelStart ?? 0.0), duration, swings);
    }

    /// <summary>The cast's end point (the cursor): X at offset 18, Z at 26, as the stock client writes it.</summary>
    private static void WriteEndPoint(byte[] packet, Position2 point)
    {
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(18), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(26), (float)point.Z);
    }
}
