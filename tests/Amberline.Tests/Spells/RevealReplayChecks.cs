using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 726 (owner report, 9 November 2026: an enemy entering vision still shows a recall effect that ends later). The production composition
    // (the nested Match): seat 0 recalls (Recall.luaobj through the summoner module) while the enemy observer, seat 1, stands in fog and walks into
    // sight before the recall's end, in the step it ends or after it; or it saw the start and lost sight before the end. What that client is sent:
    // TeleportHome (0x8C) and the Recall buff (0xBF) at the reveal, and the end (0x3D kill, 0x7F removal, 0x39 stop).
    // Timing (seconds after the press): the channel and its effect start at 0.5; a move cancels at 4.0; a hit at 4.1 cancels on the buff's next
    // 0.25 s actions line, about 4.25; a full channel ends at 8.5 (the buff, 7.9 s long, runs out at 8.4). Whether the recall still ran when the
    // reveal went out is read from the server at that flush, so a reveal that lands a step before the end counts as a reveal of a running recall.

    private enum RecallEnd { Complete, Move, Damage }

    [Check("spells-recall-reveal-shows-only-a-live-recall")]
    public static void RecallRevealShowsOnlyALiveRecall()
    {
        // Every case runs; the failures are reported together.
        var failures = new List<string>();
        foreach (var end in new[] { RecallEnd.Complete, RecallEnd.Move, RecallEnd.Damage })
        foreach (var (offset, seenAtStart) in new[] { (-1.5, false), (0.0, false), (1.0, false), (20.0, false), (1.0, true) })
        {
            try { RecallReveal(end, offset, seenAtStart); }
            catch (Exception failure) { failures.Add(failure.Message); }
        }
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void RecallReveal(RecallEnd end, double offset, bool seenAtStart)
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var observer = match.Hero(1);
        var view = match.Runtime.Session.Participants[1].View;
        var own = match.Captures[0];
        var wire = match.Captures[1];
        // Out of the recaller's sight and of every Order turret's reach (5000, 5000 stands under Order's mid inner turret).
        var far = new Position2(9000, 9500);
        var context = $"{end} reveal {(seenAtStart ? "after a seen start, " : "")}{offset:+0.0;-0.0;0} s from the end";
        observer.Position = seenAtStart ? hero.Position + new Position2(300, 0) : far;
        match.Runtime.Delivery.Flush(); match.Clear();
        var press = match.Runtime.World.Clock.Now();
        void To(double seconds)
        {
            var dt = press + seconds - match.Runtime.World.Clock.Now();
            if (dt > 1e-9) match.Runtime.Tick(dt);
        }

        match.Cast(0, SpellSystem.RecallSlot, hero);
        To(1.0);
        var id = match.Runtime.Spells.RecallEffectId(hero);
        TestAssert.True(id != 0, $"{context}: the channel holds TeleportHome");
        TestAssert.Equal(seenAtStart ? 1 : 0, EffectCreatesNamed(wire, id, "TeleportHome"), $"{context}: the start reaches only a seeing client");
        if (seenAtStart)
        {
            observer.Position = far;
            To(2.0);
            TestAssert.False(view.Visible.Contains(hero.NetId), $"{context}: the observer lost sight before the end");
        }

        var endAt = end switch { RecallEnd.Complete => 8.5, RecallEnd.Move => 4.0, _ => 4.25 };
        // A reveal in the end's own step: just before the move's flush, or in the step that crosses the timed end.
        var revealAt = offset == 0.0 && end != RecallEnd.Move ? endAt - 0.01 : endAt + offset;
        var revealIndex = -1;
        bool? live = null;
        var buffLeft = 0.0;
        void Reveal()
        {
            observer.Position = hero.Position + new Position2(300, 0);
            revealIndex = wire.Packets.Count;
        }
        // Whether the recall still ran when the reveal (the recaller's 0xC2) went out, and the time its buff had left then.
        var delivery = match.Runtime.Delivery;
        var production = delivery.AfterVisibilityFlush;
        delivery.AfterVisibilityFlush = () =>
        {
            production?.Invoke();
            if (revealIndex < 0 || live is not null || !wire.Packets.Skip(revealIndex).Any(packet =>
                    Opcode(packet) == VisionPackets.EnterVisibilityOpcode && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId))
                return;
            live = match.Runtime.Spells.RecallRemaining(hero) > 0.0;
            // As the live packets carry it: the time left plus the add's wire margin (ProtocolSpellEffectSink.WireDuration).
            buffLeft = hero.Buffs.FirstOrDefault(buff => buff.Name == "Recall") is { } recallBuff
                ? recallBuff.ExpiresAt + recallBuff.Payload.WireDurationMargin : 0.0;
        };
        var actions = new List<(double At, int Order, Action Run)> { (revealAt, 0, Reveal) };
        if (end == RecallEnd.Move) actions.Add((4.0, 1, () => match.Move(0, hero.Position + new Position2(0, 300))));
        if (end == RecallEnd.Damage) actions.Add((4.1, 1, () => match.Runtime.Combat.ApplyHit(observer.NetId, hero.NetId, 10.0, sourceIsPeriodic: false)));
        foreach (var (at, _, run) in actions.OrderBy(action => action.At).ThenBy(action => action.Order))
        {
            To(at);
            run();
        }
        To(Math.Max(revealAt, endAt) + 0.05);
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, $"{context}: the recall is over");
        // The one reveal that cannot happen: a completion in the reveal's own step has already teleported the recaller home.
        TestAssert.True(live is not null || end == RecallEnd.Complete && offset == 0.0, $"{context}: the observer sees the recaller");

        // From the reveal on: what it replays. From the press on: the end, wherever the observer stood.
        var after = wire.Packets.Skip(revealIndex).ToList();
        var recall = SpellPackets.BuffNameHash("Recall");
        var creates = after.Count(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == id && reference.Role == LeaguePacketEntityRole.Create));
        var adds = after.Where(packet => SpellPackets.TryReadBuffAdd(packet.Plaintext.Span, out var add) &&
            add.TargetNetId == hero.NetId && add.BuffNameHash == recall).ToList();
        var kills = CountKill(wire, id);
        var removals = wire.Packets.Count(packet => Opcode(packet) == 0x7F && packet.Plaintext.Length >= 10 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == recall);
        var stops = wire.Packets.Count(packet => Opcode(packet) == 0x39 && packet.Plaintext.Length >= 5 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
        Console.WriteLine($"B726 {context}: recalling at the reveal {live?.ToString() ?? "(no reveal)"}; from the reveal TeleportHome creates {creates}, " +
            $"Recall adds {adds.Count}; since the press kills {kills}, " +
            $"Recall removals {removals}, 0x39 {stops}; the recaller's client kills {CountKill(own, id)}");

        // A reveal shows a recall only while it runs: one TeleportHome and the Recall buff with the time it has left; never after its end.
        var running = live == true;
        TestAssert.Equal(running ? 1 : 0, creates, $"{context}: TeleportHome created at the reveal only for a running recall");
        TestAssert.Equal(running ? 1 : 0, adds.Count, $"{context}: the Recall buff added at the reveal only for a running recall");
        if (running && SpellPackets.TryReadBuffAdd(adds[0].Plaintext.Span, out var replayed))
            TestAssert.Near(buffLeft, replayed.Duration, 0.01, $"{context}: the replayed buff carries the time it has left");
        // The end: a cancel removes the effect from every client that holds it (ChannelingCancelStop's BBSpellEffectRemove). A completion
        // removes nothing (ChannelingSuccessStop has no remove): the copy a client saw created runs out on its own clock, the copy a reveal
        // replayed (it started late) ends with the channel.
        var holds = running || seenAtStart;
        var expectedKills = !holds ? 0 : end != RecallEnd.Complete ? 1 : running ? 1 : 0;
        TestAssert.Equal(expectedKills, kills, $"{context}: TeleportHome kills");
        TestAssert.Equal(end == RecallEnd.Complete ? 0 : 1, CountKill(own, id), $"{context}: the recaller's own copy");
        TestAssert.False(view.Effects.Contains(id), $"{context}: the observer's view keeps no record of an ended TeleportHome");
    }


    // Batch 763 (owner report, 12 November 2026: after slot 0 (HideInShadows) and a recall the stealth particle came back, and again whenever the
    // camera came back to the caster). HideInShadows.TargetExecute creates its cast particle (EffectIDVar 'a' in NextBuffVars) and no script
    // removes it; three of its four emitters are single-particle with no e-life, so the client keeps the system until a 0x3D and fires the
    // particles again each time it comes back into view (GameData.EndlessParticleSeconds). Every client that saw the cast (the caster's, an ally's,
    // an enemy's) gets the kill once the particles have played (1.5 s), and nothing creates it again: not the recall, not a fog re-entry.
    [Check("spells-endless-cast-effect-is-killed-once-shown-and-never-replayed")]
    public static void EndlessCastEffectIsKilledOnceShownAndNeverReplayed()
    {
        const string swirl = "twitch_invis_cas";
        TestAssert.Near(1.5, GameData.Default.EndlessParticleSeconds(swirl + ".troy") ?? -1.0, 1e-6, "the swirl's emitters never end; its particles live 1.5 s");
        TestAssert.True(GameData.Default.EndlessParticleSeconds("TeleportHome.troy") is null, "a system with finite emitters ends on its own");
        using var match = new Match("Twitch", Team.Order, ally: true, allyChampion: "Annie");
        var caster = match.Hero(0);
        var enemy = match.Hero(1);
        caster.Position = new Position2(7000, 7000);
        match.Hero(2).Position = new Position2(7150, 7000);
        enemy.Position = new Position2(7300, 7000);
        match.Runtime.Delivery.Flush(); match.Clear();
        var start = match.Runtime.World.Clock.Now();
        var kills = new double[match.Captures.Count];
        var wire = match.Captures.Select(_ => new Capture()).ToArray();
        uint id = 0;
        void Run(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.1)
            {
                match.Runtime.Tick(0.1);
                for (var seat = 0; seat < wire.Length; seat++)
                {
                    foreach (var packet in match.Captures[seat].Packets)
                    {
                        if (id == 0 && Opcode(packet) == FxPackets.CreateGroupOpcode &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(swirl))
                            id = packet.EntityReferences.First(reference => reference.Role == LeaguePacketEntityRole.Create).NetId;
                        if (id != 0 && Opcode(packet) == FxPackets.KillOpcode && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == id)
                            kills[seat] = match.Runtime.World.Clock.Now() - start;
                        wire[seat].Packets.Add(packet);
                    }
                    match.Captures[seat].Packets.Clear();
                }
            }
        }
        match.Cast(0, 0, caster);
        Run(3.0);
        TestAssert.True(id != 0, "slot 0 created the swirl");
        TestAssert.True(caster.Buffs.Any(buff => buff.Name == "HideInShadows"), "stealthed");
        for (var seat = 0; seat < wire.Length; seat++)
        {
            TestAssert.Equal(1, EffectCreatesNamed(wire[seat], id, swirl), $"seat {seat}: one create at the cast");
            TestAssert.Equal(1, CountKill(wire[seat], id), $"seat {seat}: the kill once the swirl has played");
            TestAssert.True(kills[seat] is >= 1.5 and <= 1.7, $"seat {seat}: killed {kills[seat]:0.##} s after the cast, after its 1.5 s and within two steps");
            TestAssert.False(match.Runtime.Session.Participants[seat].View.Effects.Contains(id), $"seat {seat}: the view no longer holds it");
        }
        // The recall breaks the stealth (Recall's CastingBreaksStealth) and takes the caster home; he comes back and the enemy loses and regains him.
        match.Cast(0, SpellSystem.RecallSlot, caster);
        Run(9.5);
        TestAssert.True(caster.Position.X < 1000, "recalled home");
        caster.Position = new Position2(7000, 7000);
        Run(1.0);
        enemy.Position = new Position2(11000, 11000);
        Run(1.0);
        TestAssert.False(match.Runtime.Session.Participants[1].View.Visible.Contains(caster.NetId), "the enemy lost sight");
        enemy.Position = new Position2(7300, 7000);
        Run(1.0);
        TestAssert.True(match.Runtime.Session.Participants[1].View.Visible.Contains(caster.NetId), "the enemy sees him again");
        for (var seat = 0; seat < wire.Length; seat++)
        {
            TestAssert.Equal(1, CountCreatesNamed(wire[seat], swirl), $"seat {seat}: never created again (recall, fog re-entry)");
            TestAssert.Equal(1, CountKill(wire[seat], id), $"seat {seat}: one kill");
        }
    }

    [Check("spells-buff-effect-left-by-its-buff-is-not-replayed")]
    public static void BuffEffectLeftByItsBuffIsNotReplayed()
    {
        var failures = new List<string>();
        foreach (var during in new[] { true, false })
        {
            try { BuffEffectLeftByItsBuff(during); }
            catch (Exception failure) { failures.Add(failure.Message); }
        }
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void BuffEffectLeftByItsBuff(bool during)
    {
        {
            using var match = new Match("Nasus", Team.Order);
            var nasus = match.Hero(0);
            var observer = match.Hero(1);
            var wire = match.Captures[1];
            var context = during ? "reveal during the buff" : "reveal after the buff";
            observer.Position = new Position2(9000, 9500);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 0, nasus);
            match.Runtime.Tick(0.1);
            var id = match.ScriptBuff(nasus, "SiphoningStrikeNew").InstanceVars["ParticleID"] is double held ? (uint)held : 0u;
            TestAssert.True(id != 0, $"{context}: the buff holds its effect");
            TestAssert.Equal(1, EffectCreatesNamed(match.Captures[0], id, "nassus_siphonStrike_beam_buf"), $"{context}: Nasus's client sees it created");
            TestAssert.Equal(0, EffectCreatesNamed(wire, id, "nassus_siphonStrike_beam_buf"), $"{context}: the observer in fog does not");
            if (during)
            {
                match.Runtime.Tick(4.9);
                observer.Position = nasus.Position + new Position2(300, 0);
                match.Runtime.Tick(0.1);
                TestAssert.Equal(1, EffectCreatesNamed(wire, id, "nassus_siphonStrike_beam_buf"), $"{context}: the reveal replays the running buff's effect");
            }
            match.Runtime.Tick(during ? 5.5 : 10.5);
            TestAssert.False(nasus.Buffs.Any(buff => buff.Name == "SiphoningStrikeNew"), $"{context}: the buff ran out");
            if (!during)
            {
                observer.Position = nasus.Position + new Position2(300, 0);
                match.Runtime.Tick(0.1);
                TestAssert.True(match.Runtime.Session.Participants[1].View.Visible.Contains(nasus.NetId), $"{context}: the observer sees Nasus");
            }
            TestAssert.Equal(during ? 1 : 0, EffectCreatesNamed(wire, id, "nassus_siphonStrike_beam_buf"), $"{context}: no replay after the buff");
            TestAssert.Equal(during ? 1 : 0, CountKill(wire, id), $"{context}: the late copy ends with the buff");
            TestAssert.Equal(0, CountKill(match.Captures[0], id), $"{context}: Nasus's own copy runs out on its own (the script removes nothing)");
        }
    }
}
