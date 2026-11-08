using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 717 (player report via the owner, 8 November 2026: Irelia's Q "can't really chain them, there is a delay between each use").
/// A Bladesurge chain on the production composition, driven through the hero's own seat (its session view, which the cast request's
/// sight gate reads, and its wire) with the client's own request (0xA2). Each press is sent only when the 1.0.0.126 client would send it:
/// its HUD (<c>GuiCanCastSpell</c>) plays the "not ready" sound and sends nothing while the slot's
/// <c>CanUseSpell</c> answers 4 (cooldown), and keeps no queue of such a press. The cooldown comes back only with the server's 0x8A.
/// What the chain is made of (measured): the kill at the landing, then the
/// shipped kill check: <c>IreliaGatotsuDash.BuffOnMoveSuccess</c> leaves <c>IreliaGatotsu</c> on Irelia (Attacker the target, Duration 1)
/// before the hit, its <c>BuffOnUpdateActions</c> removes it and its <c>OnBuffDeactivate</c> resets the slot when the Attacker is dead.
/// That first update was one 0.25 s interval after the add (batches 168 and 175); since batch 720 (owner decision, 8 November 2026,
/// Reconstruction) it runs on Irelia's next 0.25 s pass, so the 0x8A reaches the client 0 to 0.25 s after the kill; the server adds
/// nothing after it.
/// </summary>
public static class RecastChainChecks
{
    /// <summary>The hero's own client as a replication observer: the participant's own view (the one the request's sight gate reads) and a capture of its wire.</summary>
    private static WireCapture OwnSeat(Match match)
    {
        var participant = match.Runtime.Session.Participants[0];
        var wire = new WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team, match.Hero.NetId, wire,
            ready: true, view: participant.View, clientId: participant.ClientId));
        return wire;
    }

    private static uint Unit(ReadOnlySpan<byte> span) => span.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) : 0;

    /// <summary>The first packet after <paramref name="from"/> (an index into the capture) that <paramref name="test"/> accepts: its flush time, or null.</summary>
    private static double? First(WireCapture wire, int from, Func<PlaintextPacket, bool> test) =>
        wire.Packets.Skip(from).Where(item => test(item.Packet)).Select(item => (double?)item.At).FirstOrDefault();

    /// <summary>0x8A for champion slot 0 with a cooldown of 0: the script's BBSetSlotSpellCooldownTime.</summary>
    private static bool Reset(PlaintextPacket packet, uint hero)
    {
        var span = packet.Plaintext.Span;
        return span.Length >= 10 && span[0] == 0x8A && Unit(span) == hero && span[5] == 0 && BinaryPrimitives.ReadSingleLittleEndian(span[6..]) == 0f;
    }

    /// <summary>The hero's dash (0x67 carries the mover inside its group, so the net id is searched).</summary>
    private static bool Dash(PlaintextPacket packet, uint hero) =>
        packet.Plaintext.Span[0] == VisionPackets.DashOpcode && packet.Plaintext.Span.IndexOf(BitConverter.GetBytes(hero)) >= 0;

    [Check("spells-live-irelia-bladesurge-chain-the-reset-reaches-the-client-on-the-first-update-and-the-next-press-dashes-at-once")]
    public static void BladesurgeChain()
    {
        using var match = new Match("Irelia");
        var hero = match.Hero;
        match.Learn(1);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(9500, 9500);
        match.Ally.Position = new Position2(5000, 5000);
        // Two lane minions a dash apart, each one hit from death.
        var first = LaneMinion(match, new Position2(7550, 7000));
        var second = LaneMinion(match, new Position2(8000, 7000));
        first.Health = second.Health = 1;
        var wire = OwnSeat(match);
        for (var step = 0; step < 30 && !(match.Runtime.Session.Participants[0].View.Visible.Contains(first.NetId) &&
                                           match.Runtime.Session.Participants[0].View.Visible.Contains(second.NetId)); step++)
            match.Advance(Step);

        // Batch 720: Irelia's 0.25 s passes from here on (SpellSystem.SelfUpdateDue), the line IreliaGatotsu's update hook runs on.
        var passes = new List<double>();
        void Advance()
        {
            match.Advance(Step);
            if (match.Runtime.Spells.SelfUpdateDue(hero)) passes.Add(match.Now);
        }
        var press = wire.Packets.Count;
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 0, first.Position, first), "the first Bladesurge is accepted");
        TestAssert.True(First(wire, press, packet => Dash(packet, hero.NetId)) is { } dash1 && Math.Abs(dash1 - match.Now) < 1e-9,
            "its dash leaves in the press's own flush");
        TestAssert.True(hero.ForcedMove is { Completed: false } && hero.AnimationLocked, "she dashes with spell1 locked");

        // (b) Pressed again during the dash: the slot cools down, so the client would not send it; a request that comes anyway is refused, not queued.
        Advance();
        TestAssert.True(hero.ForcedMove is { Completed: false }, "the dash still runs");
        var during = wire.Packets.Count;
        TestAssert.False(CastsDuringDashChecks.WireCast(match, 0, second.Position, second), "a Bladesurge pressed during the dash is refused (cooldown)");
        TestAssert.True(hero.PendingCast is null && First(wire, during, packet => packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode) is null,
            "and is not queued behind the dash");

        for (var step = 0; step < 30 && hero.ForcedMove is not null; step++) Advance();
        TestAssert.True(hero.ForcedMove is null, "she lands");
        TestAssert.False(first.Alive, "the hit kills the first minion at the landing");
        var kill = match.Now;
        var snapshot = match.Runtime.HeroStats.Snapshot(hero);
        TestAssert.True((snapshot.ActionState & 7) == 7 && (snapshot.CanCastBits1 & 1) == 1,
            "the landing gives the action word and Bladesurge's cast bit back: only the cooldown keeps the client's slot closed");

        // (c) Pressed right after the landing, before the reset: refused, not queued. Batch 720: the reset comes on her next pass at or after
        // the kill, so this press needs a pass that is not the kill's own step; it is not in this match's phase.
        Advance();
        TestAssert.True(hero.SpellCooldowns[0] > 0.0, "the reset has not come one step after the landing (her pass is later in this phase)");
        TestAssert.False(CastsDuringDashChecks.WireCast(match, 0, second.Position, second), "a Bladesurge pressed right after the landing is refused (cooldown)");
        TestAssert.True(hero.PendingCast is null, "and is not queued");

        // The shipped kill check: IreliaGatotsu's first update removes it, and its deactivation finds the target dead and resets the slot
        // (0x8A). Batch 720 (owner decision, 8 November 2026): that update runs on her first 0.25 s pass at or after the kill (0 to 0.25 s; the
        // landing's hit comes before her line is stepped and her buffs update in the step), not one interval after the add (batch 717: kill +
        // 0.25 within a step).
        double? reset = null;
        for (var step = 0; step < 30 && reset is null; step++)
        {
            Advance();
            reset = First(wire, press, packet => Reset(packet, hero.NetId));
        }
        var pass = passes.First(at => at >= kill - 1e-9);
        TestAssert.True(reset is { } at && Math.Abs(at - pass) < 1e-9,
            $"the reset reaches the client on IreliaGatotsu's first update, her first pass at or after the kill {kill:0.000}: {pass:0.000} (got {reset:0.000})");
        // The clip's 0x2B is the dash buff's own 0.25 s TickRate line from the press (WillRemove, here 0.967 after the 0.700 press). Since batch
        // 720 the reset can come before it when the kill lands well inside that line and her pass follows the kill closely; in this match's
        // phase (kill 0.900, reset 1.033) it does not.
        TestAssert.True(First(wire, press, packet => packet.Plaintext.Span[0] == 0x2B && Unit(packet.Plaintext.Span) == hero.NetId) is { } unlock &&
            unlock <= reset!.Value + 1e-9, "the clip is unlocked no later than the reset, so the next spell1 play is not refused");
        TestAssert.Equal(0.0, hero.SpellCooldowns[0], "the server's slot is ready with the packet");

        // (a) Pressed on the client's first frame after the reset: accepted at once, its dash in the same flush.
        Advance();
        var chained = wire.Packets.Count;
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 0, second.Position, second), "the chained Bladesurge is accepted at once");
        TestAssert.True(hero.PendingCast is null && First(wire, chained, packet => Dash(packet, hero.NetId)) is { } dash2 &&
            Math.Abs(dash2 - match.Now) < 1e-9, "its dash leaves in the press's own flush: nothing on the server delays it");
        TestAssert.True(Math.Abs(match.Now - pass - Step) < 1e-9,
            $"from the kill to the next dash: the wait for her pass ({pass - kill:0.000} s) and the client's frame ({match.Now - kill:0.000} s)");
        for (var step = 0; step < 30 && second.Alive; step++) match.Advance(Step);
        TestAssert.False(second.Alive, "the chained Bladesurge kills the second minion");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    /// <summary>
    /// Batch 720 (owner decision, 8 November 2026): the reset after a kill is <c>IreliaGatotsu</c>'s first <c>BuffOnUpdateActions</c>, which
    /// runs on Irelia's next 0.25 s pass (0 to 0.25 s after the kill), not one interval after the add (batch 717: kill + 0.25 and up to a
    /// step, at every phase). The landing's hit kills inside the spell tick (the dash moves in the champions' shared phase), before her line
    /// is stepped and her buffs update, so a pass on the kill's own step resets the slot in that step. Nine kills, each started one step
    /// later than the last, walk across her line. Reconstruction (batch 718: no source gives the phase).
    /// </summary>
    [Check("spells-live-irelia-bladesurge-reset-lands-on-her-next-pass-at-every-phase")]
    public static void BladesurgeResetLandsOnHerNextPass()
    {
        using var match = new Match("Irelia");
        var hero = match.Hero;
        match.Learn(1);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(9500, 9500);
        match.Ally.Position = new Position2(5000, 5000);
        var spells = match.Runtime.Spells;
        var step = 0;
        var passes = new List<int>();
        void Advance()
        {
            match.Advance(Step);
            step++;
            if (spells.SelfUpdateDue(hero)) passes.Add(step);
        }
        var offsets = new List<int>();
        for (var kill = 0; kill < 9; kill++)
        {
            // The last dash over and its clip unlocked, then one step more than last time before the press.
            for (var wait = 0; wait < 30 && (hero.ForcedMove is not null || hero.AnimationLocked); wait++) Advance();
            for (var wait = 0; wait < kill; wait++) Advance();
            match.Refill();
            var minion = LaneMinion(match, new Position2(hero.Position.X + (kill % 2 == 0 ? 500 : -500), hero.Position.Z));
            minion.Health = 1;
            Advance();
            for (var wait = 0; wait < 30 && !spells.CanSeeTarget(hero, minion); wait++) Advance();
            TestAssert.True(match.Cast(0, minion), $"Bladesurge {kill} is accepted");
            TestAssert.True(hero.SpellCooldowns[0] > 0.0, $"Bladesurge {kill}: the slot cools down from the cast");
            for (var wait = 0; wait < 30 && minion.Alive; wait++) Advance();
            TestAssert.False(minion.Alive, $"Bladesurge {kill} kills its minion at the landing");
            var killed = step;
            int? reset = hero.SpellCooldowns[0] == 0.0 ? step : null;
            for (var wait = 0; wait < 30 && reset is null; wait++)
            {
                Advance();
                if (hero.SpellCooldowns[0] == 0.0) reset = step;
            }
            TestAssert.Equal(passes.First(at => at >= killed), reset ?? -1,
                $"kill {kill} at step {killed}: the slot resets on her first pass at or after the kill (passes " +
                $"{string.Join(" ", passes.Where(at => at >= killed - 8 && at <= killed + 16))})");
            offsets.Add(reset!.Value - killed);
        }
        TestAssert.True(offsets.Max() <= 7 && offsets.Distinct().Count() >= 4,
            $"from the kill to the reset: 0 to 7 steps by the phase, never the old fixed 8: {string.Join(" ", offsets)}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }
}
