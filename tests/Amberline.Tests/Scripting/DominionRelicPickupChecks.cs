using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Buffs;

namespace Amberline.Tests;

/// <summary>
/// Batch 470: Dominion's relics work when a champion takes them, in a two-seat map 8 match built by the production composition
/// (MatchRuntime.CreateProduction, the relics spawned by the quest host through the production unit scripts).
/// <list type="bullet">
/// <item>A health relic (OdinShieldRelic): its OdinShieldRelicAura gives the champion within 175 OdinShieldRelicBuffHeal, whose
/// OnBuffActivate heals HealAmount (90 to 243 by level, x1.25 with OdinPlayerBuff, then x0.8 through OdinPlayerBuff's own BuffOnHeal) and
/// restores half the base amount as mana. It works on master: batch 469's harness hero stood at full health.</item>
/// <item>A greater relic (OdinCenterRelic): its character file is Useable (Useable*IsUseable 1, EnemyCanUse 1, HeroUseSpell
/// OdinCaptureChannelBomb). The stock client's right-click on it sends UseObject (0x3F: actor, target;
/// the use routine), and the channel drains the relic's mana through OdinBombSuppression until it gives the champion the
/// Storm Shield (OdinCenterRelicBuff, OdinCenterRelicBuffDamage) and kills the relic (500 true damage on 5 health).</item>
/// </list>
/// </summary>
public static class DominionRelicPickupChecks
{
    private const double Step = 1.0 / 30.0;
    private const long OrderConnection = 100;

    private sealed record Match(MatchRuntime Runtime, Entity Order, BehaviorTreeQuestHost Quests, ScriptFailures Failures) : IDisposable
    {
        public void Dispose()
        {
            Runtime.Dispose();
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.health-relic-heals-a-hurt-champion", TimeoutSeconds = 300)]
    public static void HealthRelicHealsAHurtChampion()
    {
        using var match = StartMatch();
        RunTo(match.Runtime, 121.0);
        var relic = Standing(match, "OdinShieldRelic", "RelicA") ?? throw new InvalidOperationException("no health relic stands at RelicA at 2:01");
        var hero = match.Order;
        hero.Health = 200.0;
        hero.Mana = 0.0;
        hero.Position = relic.Position;

        // OdinShieldRelicBuffHeal: HealAmount = SrcValueByLevel[level] (90 + 9 per level), ManaAmount = HealAmount * 0.5, then x1.25 with
        // OdinPlayerBuff (the guardian's char script gives it to every hero on the map, the hero as its caster) before BBIncHealth. The
        // received heal then passes OdinPlayerBuff's own BuffOnHeal (Health * 0.8, Dominion's heal cut), so the champion gains HealAmount.
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OdinPlayerBuff" && buff.CasterNetId == hero.NetId),
            "the champion holds its own OdinPlayerBuff (CharScriptOdinNeutralGuardian's map-wide add)");
        var level = hero.Level;
        var baseHeal = 90.0 + 9.0 * (level - 1);
        var health = hero.Health;
        var mana = hero.Mana;
        for (var tick = 0; tick < 30 && relic.Alive; tick++)
        {
            health = hero.Health;
            mana = hero.Mana;
            match.Runtime.Tick(Step);
        }
        TestAssert.True(!relic.Alive, "the health relic dies when the champion touches it (OdinShieldRelicAura's deactivate)");
        var heal = baseHeal * 1.25 * 0.8;
        TestAssert.True(Math.Abs(hero.Health - health - heal) < 1.0,
            $"the champion gains the shipped heal: {heal:F1} at level {level}, got {hero.Health - health:F1}");
        TestAssert.True(Math.Abs(hero.Mana - mana - baseHeal * 0.5) < 1.0,
            $"the champion gains the shipped mana: {baseHeal * 0.5:F1}, got {hero.Mana - mana:F1}");
        TestAssert.True(match.Failures.Records.Count == 0, "no relic script raised: " + string.Join("; ", match.Failures.Records));
    }

    /// <summary>
    /// Batch 476: a health relic is hidden from its spawn through its pickup. OdinShieldRelicAura's OnBuffActivate sets NoRender and
    /// ForceRenderParticles (the relic draws only its heal rune), and its OnBuffDeactivate sets NoRender again as its last step, after the
    /// death. The owner's native test saw the relic's model play a death animation on the pickup, so the dead relic must still carry
    /// NoRender in the ActionState the client is sent.
    /// </summary>
    [Check("dominion.health-relic-stays-hidden-through-its-pickup", TimeoutSeconds = 300)]
    public static void HealthRelicStaysHiddenThroughItsPickup()
    {
        using var match = StartMatch();
        RunTo(match.Runtime, 121.0);
        var relic = Standing(match, "OdinShieldRelic", "RelicA") ?? throw new InvalidOperationException("no health relic stands at RelicA at 2:01");
        TestAssert.True(Hidden(relic), $"the health relic is hidden while it stands (ActionState 0x{State(relic):X})");
        var hero = match.Order;
        hero.Health = 200.0;
        hero.Position = relic.Position;
        for (var tick = 0; tick < 30 && relic.Alive; tick++) match.Runtime.Tick(Step);
        TestAssert.True(!relic.Alive, "the pickup kills the health relic");
        TestAssert.True(Hidden(relic),
            $"the dead health relic is still hidden, so the client plays no Death animation (ActionState 0x{State(relic):X})");
        TestAssert.True(match.Failures.Records.Count == 0, "no relic script raised: " + string.Join("; ", match.Failures.Records));
    }

    // The ActionState word the client is sent for a unit (CharacterState.ScriptedCombatOf, the same word the 0xCC stats carry).
    private static uint State(Entity unit) => CharacterState.ScriptedCombatOf(unit).ActionState;

    private static bool Hidden(Entity unit) => (State(unit) & CharacterState.NoRenderBit) != 0;

    [Check("dominion.greater-relic-use-gives-the-storm-shield-and-respawns", TimeoutSeconds = 600)]
    public static void GreaterRelicUseGivesTheStormShieldAndRespawns()
    {
        using var match = StartMatch();
        RunTo(match.Runtime, 182.5); // batch 476e: the crystals first spawn at game 181.96 (HUD 3:01)
        var relic = Standing(match, "OdinCenterRelic", "LargeRelic1") ?? throw new InvalidOperationException("no greater relic stands at LargeRelic1 at 3:01");
        var hero = match.Order;
        // Out of OdinCaptureChannelBomb's CastRange (250): the champion walks to the relic first. The client sends the use only for a unit
        // it sees under the cursor, so the seat's view must hold the relic (DuelSession.DispatchGameplay's sight gate).
        hero.Position = new Position2(relic.Position.X + 600.0, relic.Position.Z);
        // DIAGNOSTICS: test seam, the offline seat has no replication pump to fill its view (DeathRecapLiveChecks' pattern).
        match.Runtime.Session.Participant(OrderConnection)!.View.Visible.Add(relic.NetId);
        TestAssert.True(Use(match, hero, relic), "the server takes the champion's use of the greater relic (UseObject)");
        TestAssert.True(!Channels(hero) && hero.Waypoints.Count > 0,
            "from 600 away the champion walks to the relic before it channels");

        // In range the champion channels OdinCaptureChannelBomb at the relic. The channel engages after 1.5 s (ChargeTimePassed) and
        // OdinBombSuppression drains 7000 of the relic's 50000 mana every 0.5 s until 5% or less is left (7 ticks, 3 s): about 4.5 s.
        var channelled = false;
        for (var tick = 0; tick < 9 * 30 && relic.Alive; tick++)
        {
            match.Runtime.Tick(Step);
            channelled |= Channels(hero);
        }
        TestAssert.True(channelled, "the champion channels OdinCaptureChannelBomb (the relic's Useable*HeroUseSpell)");
        TestAssert.True(!relic.Alive, "the greater relic dies when the champion's channel drains it (OdinBombSuppression's 500 true damage)");
        foreach (var name in new[] { "OdinCenterRelicBuff", "OdinCenterRelicBuffDamage", "OdinScoreBigRelic" })
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == name), $"the champion holds {name} after taking the greater relic");

        var death = relic.DeathTime ?? throw new InvalidOperationException("the relic died with no death time");
        RunTo(match.Runtime, death + 179.5);
        TestAssert.True(Standing(match, "OdinCenterRelic", "LargeRelic1") is null, "the greater relic is still gone 179.5 seconds after it was taken");
        RunTo(match.Runtime, death + 180.5);
        TestAssert.True(Standing(match, "OdinCenterRelic", "LargeRelic1") is not null, "the greater relic stands again 180.5 seconds after it was taken");
        TestAssert.True(match.Failures.Records.Count == 0, "no relic script raised: " + string.Join("; ", match.Failures.Records));
    }

    /// <summary>
    /// The capture point's guardian is the other useable unit (OdinNeutralGuardian.inibin: IsUseable 1, EnemyCanUse 1, HeroUseSpell
    /// OdinCaptureChannel). The same use packet on a neutral guardian within OdinCaptureChannel's CastRange (500) starts the capture channel
    /// at it, the channel batch 449 starts from an attack order.
    /// </summary>
    /// <summary>
    /// Batch 476: a greater relic's use channel runs with no client SpellCaster, so nothing ends the Channel loop OdinCaptureChannelBomb
    /// plays. The channel ends when its target dies (the drain kills the relic, the owner's log at 232.611): the champion must then get the
    /// stop that releases the loop as its last animation packet (the server's rule in BbScriptChannels.Stop).
    /// </summary>
    [Check("dominion.greater-relic-channel-releases-its-loop-when-it-ends", TimeoutSeconds = 600)]
    public static void GreaterRelicChannelReleasesItsLoopWhenItEnds()
    {
        using var match = StartMatch();
        RunTo(match.Runtime, 182.5); // batch 476e: the crystals first spawn at game 181.96 (HUD 3:01)
        var relic = Standing(match, "OdinCenterRelic", "LargeRelic1") ?? throw new InvalidOperationException("no greater relic stands at LargeRelic1 at 3:01");
        var hero = match.Order;
        hero.Position = new Position2(relic.Position.X + 200.0, relic.Position.Z);
        // DIAGNOSTICS: test seam, as in dominion.greater-relic-use-gives-the-storm-shield-and-respawns.
        match.Runtime.Session.Participant(OrderConnection)!.View.Visible.Add(relic.NetId);
        var capture = new AnimationCapture();
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(99, hero.Team, hero.NetId, capture, ready: true));
        match.Runtime.Delivery.Flush();
        TestAssert.True(Use(match, hero, relic), "the server takes the champion's use of the greater relic (UseObject)");
        var channelled = false;
        for (var tick = 0; tick < 12 * 30 && (!channelled || Channels(hero)); tick++)
        {
            capture.Packets.Clear();
            match.Runtime.Tick(Step);
            channelled |= Channels(hero);
        }
        TestAssert.True(channelled, "the champion channels OdinCaptureChannelBomb (the relic's Useable*HeroUseSpell)");
        TestAssert.True(!Channels(hero) && !relic.Alive, "the channel ends with its target, the relic's death (the drain)");
        var tail = new List<PlaintextPacket>(capture.Packets);
        for (var tick = 0; tick < 30; tick++)
        {
            capture.Packets.Clear();
            match.Runtime.Tick(Step);
            tail.AddRange(capture.Packets);
        }
        // Animation packets for the champion: PlayAnimation (0xB8) and StopAnimation (0x2B), both with the unit's net id at [1..4].
        var last = tail.LastOrDefault(packet => packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] is 0xB8 or 0x2B &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
        TestAssert.True(last is not null && last.Plaintext.Span[0] == 0x2B,
            "after the channel ends the champion's last animation packet is a stop, which releases the looped Channel pose " +
            $"(last: {(last is null ? "none" : "0x" + last.Plaintext.Span[0].ToString("X2"))})");
        TestAssert.True(match.Failures.Records.Count == 0, "no relic script raised: " + string.Join("; ", match.Failures.Records));
    }

    /// <summary>
    /// Batch 476 guard: a capture channel (OdinCaptureChannel) releases its own loop in its ChannelingSuccessStop (BBUnlockAnimation,
    /// OdinCaptureChannel.txt:1080), so the server's release rule must not add a second stop when it ends.
    /// </summary>
    [Check("dominion.capture-channel-end-sends-one-stop", TimeoutSeconds = 900)]
    public static void CaptureChannelEndSendsOneStop()
    {
        using var match = StartMatch();
        var at = (Position3)match.Quests.Captures[2].Variable("CapturePosition")!;
        var guardian = match.Runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
            .OrderBy(unit => Math.Abs(unit.Position.X - at.X) + Math.Abs(unit.Position.Z - at.Z)).First();
        var hero = match.Order;
        hero.Position = new Position2(guardian.Position.X + 400.0, guardian.Position.Z);
        // DIAGNOSTICS: test seam, as in dominion.guardian-use-starts-the-capture-channel.
        match.Runtime.Session.Participant(OrderConnection)!.View.Visible.Add(guardian.NetId);
        var capture = new AnimationCapture();
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(99, hero.Team, hero.NetId, capture, ready: true));
        match.Runtime.Delivery.Flush();
        TestAssert.True(Use(match, hero, guardian), "the server takes the champion's use of a neutral guardian (UseObject)");
        var channelled = false;
        for (var tick = 0; tick < 120 * 30 && (!channelled || CaptureChannels(hero)); tick++)
        {
            capture.Packets.Clear();
            match.Runtime.Tick(Step);
            channelled |= CaptureChannels(hero);
        }
        TestAssert.True(channelled, "the champion channels OdinCaptureChannel");
        TestAssert.True(!CaptureChannels(hero), "the capture channel ends within two minutes");
        var tail = new List<PlaintextPacket>(capture.Packets);
        for (var tick = 0; tick < 30; tick++)
        {
            capture.Packets.Clear();
            match.Runtime.Tick(Step);
            tail.AddRange(capture.Packets);
        }
        var stops = tail.Count(packet => packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == 0x2B &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
        TestAssert.True(stops == 1,
            $"the capture channel's end sends exactly one stop: its own unlock, or the rule's when there is none ({stops} sent)");
        TestAssert.True(match.Failures.Records.Count == 0, "no capture script raised: " + string.Join("; ", match.Failures.Records));
    }

    private static bool CaptureChannels(Entity hero) => hero.Buffs.Any(buff => buff.Name == "OdinCaptureChannel");

    /// <summary>
    /// Batch 476 (DIAGNOSTICS): <c>AMBERLINE_ODIN_NO_RELIC_BUBBLE=1</c> withholds exactly the 0x26 of the centre crystal and nothing else. Two
    /// matches, the gate unset and set: the crystal's own 0x26 packets are sent unset and not set; every other opcode is sent as often in both;
    /// the 0x26 difference is the crystal's count; and the server keeps the crystal's timed sight bubble in both.
    /// </summary>
    [Check("diagnostics.odin-relic-bubble-gate-withholds-only-its-0x26", TimeoutSeconds = 900)]
    public static void OdinRelicBubbleGateWithholdsOnlyItsPacket()
    {
        // On by default since 29 October: "0" sends the packet, unset withholds it.
        var unset = CenterRelicRun("0");
        var set = CenterRelicRun(null);
        TestAssert.True(unset.OwnBubbles >= 1, $"unset: the crystal's own 0x26 is sent ({unset.OwnBubbles})");
        TestAssert.True(set.OwnBubbles == 0, $"set: no 0x26 names the crystal ({set.OwnBubbles})");
        TestAssert.True(unset.ServerBubbles >= 1 && set.ServerBubbles == unset.ServerBubbles,
            $"the server keeps the crystal's sight bubble either way (unset {unset.ServerBubbles}, set {set.ServerBubbles})");
        foreach (var op in unset.Opcodes.Keys.Union(set.Opcodes.Keys).Where(op => op != 0x26))
            TestAssert.True(unset.Count(op) == set.Count(op),
                $"opcode 0x{op:X2} is sent as often with the gate set ({unset.Count(op)} unset, {set.Count(op)} set)");
        TestAssert.True(unset.Count(0x26) - set.Count(0x26) == unset.OwnBubbles,
            $"the gate removes exactly the crystal's 0x26 packets ({unset.Count(0x26)} unset, {set.Count(0x26)} set)");
    }

    /// <summary>The packet record of one run: the crystal's 0x26 count, every opcode's count, and the server's bubbles anchored to the crystal.</summary>
    private sealed record CenterRelicRunRecord(int OwnBubbles, Dictionary<byte, int> Opcodes, int ServerBubbles)
    {
        public int Count(byte op) => Opcodes.GetValueOrDefault(op);
    }

    private static CenterRelicRunRecord CenterRelicRun(string? gate)
    {
        var previous = Environment.GetEnvironmentVariable(OdinRelicBubbleGate.EnvironmentVariable);
        Environment.SetEnvironmentVariable(OdinRelicBubbleGate.EnvironmentVariable, gate);
        try
        {
            using var match = StartMatch();
            var capture = new AnimationCapture();
            match.Runtime.Delivery.AddObserver(new ReplicationObserver(99, match.Order.Team, match.Order.NetId, capture, ready: true));
            match.Runtime.Delivery.Flush();
            capture.Packets.Clear();
            RunTo(match.Runtime, 182.5); // batch 476e: the crystals first spawn at game 181.96 (HUD 3:01)
            // The gate is keyed on the character: both centre crystals (LargeRelic1 and LargeRelic2, spawned at 3:00) are OdinCenterRelic units.
            var crystals = match.Runtime.World.Entities.Values.Where(unit => unit.ChampionName == OdinRelicBubbleGate.CenterRelicName)
                .Select(unit => unit.NetId).ToHashSet();
            if (crystals.Count == 0) throw new InvalidOperationException("no centre crystal stands at 3:01");
            var opcodes = new Dictionary<byte, int>();
            var own = 0;
            foreach (var packet in capture.Packets)
            {
                var span = packet.Plaintext.Span;
                opcodes[span[0]] = opcodes.GetValueOrDefault(span[0]) + 1;
                // 0x26 (AddUnitPerceptionBubble): the unit's net id at [17..20] (SpawnPackets.AddUnitPerceptionBubble).
                if (span[0] == 0x26 && span.Length >= 21 && crystals.Contains(BinaryPrimitives.ReadUInt32LittleEndian(span[17..]))) own++;
            }
            var bubbles = match.Runtime.World.TimedVisionBubbles.Count(bubble => crystals.Contains(bubble.AnchorNetId));
            return new CenterRelicRunRecord(own, opcodes, bubbles);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OdinRelicBubbleGate.EnvironmentVariable, previous);
        }
    }

    private sealed class AnimationCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    [Check("dominion.guardian-use-starts-the-capture-channel", TimeoutSeconds = 300)]
    public static void GuardianUseStartsTheCaptureChannel()
    {
        using var match = StartMatch();
        var at = (Position3)match.Quests.Captures[2].Variable("CapturePosition")!;
        var guardian = match.Runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
            .OrderBy(unit => Math.Abs(unit.Position.X - at.X) + Math.Abs(unit.Position.Z - at.Z)).First();
        var hero = match.Order;
        hero.Position = new Position2(guardian.Position.X + 400.0, guardian.Position.Z);
        match.Runtime.Session.Participant(OrderConnection)!.View.Visible.Add(guardian.NetId); // DIAGNOSTICS: test seam, as above
        TestAssert.True(Use(match, hero, guardian), "the server takes the champion's use of a neutral guardian (UseObject)");
        TestAssert.True(ReferenceEquals(match.Runtime.Spells.CaptureChannelTarget(hero), guardian),
            "the use starts OdinCaptureChannel at the guardian (its Useable*HeroUseSpell)");
        TestAssert.True(match.Failures.Records.Count == 0, "no capture script raised: " + string.Join("; ", match.Failures.Records));
    }

    // The channel's own buff: OdinCaptureChannelBomb's ChannelingStart adds it to the champion (a BBSpellBuffAdd named after the script).
    private static bool Channels(Entity hero) => hero.Buffs.Any(buff => buff.Name == "OdinCaptureChannelBomb");

    // UseObject as the stock client writes it: 0x3F, the sender's net ID, the used unit's net ID.
    private static bool Use(Match match, Entity hero, Entity used)
    {
        var payload = new byte[9];
        payload[0] = 0x3F;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), used.NetId);
        return match.Runtime.Session.DispatchGameplay(OrderConnection, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    private static Entity? Standing(Match match, string definition, string location)
    {
        var at = match.Quests.Context.NamedLocation(location) ?? throw new InvalidOperationException($"the map names no location {location}");
        return match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.ChampionName == definition &&
            unit.Position.X == at.X && unit.Position.Z == at.Z);
    }

    private static void RunTo(MatchRuntime runtime, double seconds)
    {
        while (runtime.Combat.CurrentTime + Step / 2 < seconds) runtime.Tick(Step);
    }

    // DominionGuardianChecks.StartMatch's pattern: two Annie seats on map 8, the production composition.
    private static Match StartMatch()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
        };
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var failures = new ScriptFailures();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)), GameData.Default,
            readySettleSeconds: 0.0, sessionLog: failures, roster: roster, mapId: GameMaps.CrystalScar);
        runtime.World.Clock.SetVirtual(0.0);
        // DIAGNOSTICS: no client drives these seats over the minutes the relic timers take, so AFK latching stays off (as in a headless match).
        runtime.Session.AfkTrackingDisabled = true;
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            var connection = OrderConnection + index;
            TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection), $"seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the map 8 match starts");
        runtime.Tick(Step);
        var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests
            ?? throw new InvalidOperationException("map 8 runs its quest host");
        var order = runtime.Session.Participants.Select(participant => participant.Hero!).Single(hero => hero.Team == Team.Order);
        return new Match(runtime, order, quests, failures);
    }

    /// <summary>The production composition's script.exception records (a hook run that raised), as the match tool counts them.</summary>
    private sealed class ScriptFailures : ISessionLogSink
    {
        public List<string> Records { get; } = [];

        public bool Admit(string category, string level) => level == "ERROR";

        public void Write(string category, string level, string message, IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (message == "script.exception") Records.Add(string.Join(" ", (properties ?? new Dictionary<string, object?>()).Select(entry => $"{entry.Key}={entry.Value}")));
        }
    }
}
