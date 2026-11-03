using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class PlaytestDiagnosticsChecks
{
    [Check("session.playtest-mark-logs-and-is-not-broadcast", Serial = true)]
    public static void MarkerIsPrivateAndIncludesTheNearbyWorld()
    {
        var log = new Log();
        using var runtime = Start(log, start: false, bot: true);
        var hero = runtime.Session.Participant(1)!.Hero!;
        hero.Position = new Position2(7000, 7000);
        var near = Minion(runtime, new Position2(7100, 7000));
        var far = Minion(runtime, new Position2(8600, 7000));
        var reply = Chat(runtime, ".MaRk minions laggy");
        TestAssert.Equal(1, reply.Count);
        TestAssert.Equal(1L, reply[0].ConnectionId);
        TestAssert.Equal(ChatPackets.Channel, reply[0].Packet.Channel);
        TestAssert.Equal("[mark --:--] noted", Text(reply[0].Packet));
        var row = log.Rows.Single();
        TestAssert.Equal("PLAYTEST", row.Category);
        TestAssert.Equal("INFO", row.Level);
        TestAssert.Equal("mark", row.Message);
        TestAssert.Equal(JsonValueKind.Null, row.Fields.GetProperty("hud").ValueKind);
        TestAssert.Equal("minions laggy", row.Fields.GetProperty("note").GetString()!);
        TestAssert.Equal(hero.NetId, row.Fields.GetProperty("hero").GetUInt32());
        TestAssert.Equal("Annie", row.Fields.GetProperty("champion").GetString()!);
        TestAssert.Equal("Order", row.Fields.GetProperty("team").GetString()!);
        TestAssert.Equal(7000.0, row.Fields.GetProperty("pos")[0].GetDouble());
        var nearby = row.Fields.GetProperty("nearby").EnumerateArray().ToArray();
        var minion = nearby.Single(item => item.GetProperty("netid").GetUInt32() == near.NetId);
        TestAssert.False(nearby.Any(item => item.GetProperty("netid").GetUInt32() == far.NetId));
        TestAssert.Equal("minion", minion.GetProperty("unit_type").GetString()!);
        TestAssert.Equal("Order", minion.GetProperty("team").GetString()!);
        TestAssert.Equal(JsonValueKind.Null, minion.GetProperty("champion").ValueKind);
        TestAssert.Equal(near.MinionState.ToString(), minion.GetProperty("ai_state").GetString()!);
        TestAssert.Equal(JsonValueKind.Null, minion.GetProperty("attack_target").ValueKind);
        TestAssert.Equal(0, minion.GetProperty("waypoints").GetInt32());
        TestAssert.Equal(0.0, minion.GetProperty("stuck_elapsed").GetDouble());
        TestAssert.False(row.Fields.TryGetProperty("name", out _));

        TestAssert.True(runtime.Session.Start());
        runtime.Combat.RestoreClock(754);
        reply = Chat(runtime, ".mark");
        TestAssert.Equal("[mark 12:34] noted", Text(reply.Single().Packet));
        TestAssert.Equal("", log.Rows[^1].Fields.GetProperty("note").GetString()!);
        TestAssert.Equal(754.0, log.Rows[^1].Fields.GetProperty("hud").GetDouble());
        // The chat permission gate used for paused commands must not gate markers.
        var request = ChatPackets.Envelope(0, false, ".mark paused"u8);
        var paused = runtime.Dependencies.Chat.Dispatch(runtime.Session.Participant(1)!,
            request.Plaintext, runtime.Session.Participants, allowCommands: false);
        TestAssert.Equal(1L, paused.Single().ConnectionId);
        TestAssert.Equal("paused", log.Rows[^1].Fields.GetProperty("note").GetString()!);
    }

    [Check("session.playtest-ordinary-chat-is-unchanged", Serial = true)]
    public static void OrdinaryChatKeepsRawBytesAndTeamRecipients()
    {
        var log = new Log();
        using var runtime = Start(log, bot: true);
        var message = new byte[] { (byte)'h', 0xFF, 0xC3, 0xA9 };
        var request = ChatPackets.Envelope(200, false, message);
        var replies = runtime.Session.Receive(1, ChatPackets.Channel,
            runtime.Cipher.Encrypt(request.Plaintext.Span));
        TestAssert.Equal(2, replies.Count);
        foreach (var reply in replies)
        {
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(reply.Packet.Plaintext.Span));
            TestAssert.Equal(message, reply.Packet.Plaintext.Slice(12, message.Length).ToArray());
        }
        TestAssert.Equal(1L, Chat(runtime, "ordinary team chat", team: true).Single().ConnectionId);
        TestAssert.Equal(0, log.Rows.Count);
    }

    [Check("runtime.invariant-blocked-cell", Serial = true)]
    public static void BlockedCellUsesTheMovementGridAndEndsOnClearFloor()
    {
        var log = new Log();
        using var runtime = Start(log);
        var hero = runtime.Session.Participant(1)!.Hero!;
        var floor = new Position2(7000, 7000);
        var blocked = FindBlocked(runtime.Navigation);
        Exercise(runtime, log, hero, "blocked-cell", 1, 0,
            () => hero.Position = blocked, () => hero.Position = floor);
    }

    [Check("runtime.invariant-overspeed", Serial = true)]
    public static void OverspeedUsesOneCommittedStepAndExcludesForcedMovement()
    {
        var log = new Log();
        using var runtime = Start(log);
        var hero = runtime.Session.Participant(1)!.Hero!;
        hero.Position = new Position2(7000, 7000);
        var observer = runtime.Movement.StepObserver ??
            throw new InvalidOperationException("production wires the playtest step observer");
        // Collision separation can carry a walking step past speed*dt (measured
        // up to 1.82x); the monitor flags only jumps beyond twice the step.
        observer(hero, hero.Position + new Position2(-19, 0), 100, 0.1, false);
        runtime.PlaytestDiagnostics!.Tick();
        TestAssert.Equal(0, log.Rows.Count(row => row.Message == "overspeed"),
            "a 1.9x step (collision separation) is not overspeed");
        Exercise(runtime, log, hero, "overspeed", 300, 20,
            () => observer(hero, hero.Position + new Position2(-300, 0), 100, 0.1, false),
            () => observer(hero, hero.Position, 100, 0.1, false));
        var count = log.Rows.Count;
        observer(hero, hero.Position + new Position2(-1000, 0), 100, 0.1, true);
        runtime.PlaytestDiagnostics!.Tick();
        TestAssert.Equal(count, log.Rows.Count, "forced movement is excluded");
        // A teleport/recall/respawn between committed steps is not a walking step.
        var observedSteps = 0;
        runtime.Movement.StepObserver += (_, _, _, _, _) => observedSteps++;
        hero.Position = new Position2(7200, 7000);
        runtime.Movement.Tick(0.05);
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.True(observedSteps > 0, "the production movement owner supplies committed steps");
        TestAssert.Equal(2, Events(log, hero, "overspeed").Length);
    }

    [Check("runtime.invariant-stacked", Serial = true)]
    public static void StackingRequiresTheSamePairForMoreThanTwoSeconds()
    {
        var log = new Log();
        using var runtime = Start(log);
        var first = Minion(runtime, new Position2(7000, 7000));
        var second = Minion(runtime, first.Position);
        runtime.PlaytestDiagnostics!.Tick();
        runtime.World.Clock.Advance(2);
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.Equal(0, Events(log, first, "stacked").Length);
        runtime.World.Clock.Advance(0.01);
        Exercise(runtime, log, first, "stacked", 100, 12,
            () => { }, () => second.Position = new Position2(7400, 7000));
        TestAssert.Equal(1, Events(log, second, "stacked-end").Length);
    }

    [Check("runtime.invariant-target-beyond-retention", Serial = true)]
    public static void RetainedTargetsUseTheRetail2500Distance()
    {
        var log = new Log();
        using var runtime = Start(log);
        var minion = Minion(runtime, new Position2(7000, 7000));
        var target = runtime.Session.Participant(2)!.Hero!;
        target.Position = new Position2(9600, 7000);
        Exercise(runtime, log, minion, "target-beyond-retention", 2600, 2500,
            () => minion.AttackTarget = target.NetId, () => minion.AttackTarget = null);
    }

    [Check("runtime.invariant-clean-production-control", Serial = true)]
    public static void NormalProductionStepsAndHealthySamplesStayQuiet()
    {
        var log = new Log();
        using var runtime = Start(log, bot: true);
        runtime.Tick(2);
        TestAssert.Equal(0, log.Rows.Count, "normal production movement and AI emit no invariant events");
        var hero = runtime.Session.Participant(1)!.Hero!;
        runtime.PlaytestDiagnostics!.ObserveClientPosition(hero, hero.Position);
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.Equal(0, log.Rows.Count);
    }

    [Check("runtime.invariant-removal-and-disabled-monitor", Serial = true)]
    public static void RemovalEndsAnEpisodeAndDisablingDoesNotDisableMarkers()
    {
        var log = new Log();
        using (var runtime = Start(log))
        {
            var minion = Minion(runtime, FindBlocked(runtime.Navigation));
            runtime.PlaytestDiagnostics!.Tick();
            // A lane minion's first second is not a defect (it is created on its barracks marker, which can lie inside a building stamp;
            // P3, 18 October 2026): the episode opens once the grace is over.
            TestAssert.Equal(0, Events(log, minion, "blocked-cell").Length, "the spawn grace holds the first second");
            runtime.World.Clock.Advance(1.01);
            runtime.PlaytestDiagnostics.Tick();
            TestAssert.Equal(1, Events(log, minion, "blocked-cell").Length);
            runtime.World.Clock.Advance(1);
            runtime.World.Remove(minion.NetId);
            runtime.PlaytestDiagnostics.Tick();
            TestAssert.Equal(1, Events(log, minion, "blocked-cell-end").Length);
        }
        log.Rows.Clear();
        using var disabled = Start(log, invariants: false);
        disabled.Session.Participant(1)!.Hero!.Position = FindBlocked(disabled.Navigation);
        disabled.PlaytestDiagnostics!.Tick();
        TestAssert.Equal(0, log.Rows.Count);
        TestAssert.Equal(1, Chat(disabled, ".mark disabled monitor").Count);
        TestAssert.Equal("PLAYTEST", log.Rows.Single().Category);
    }

    // DIAGNOSTICS (batch 374): a minion whose basic attack stands it still for 12 s is not idle when the attack ends and it
    // re-routes. Batch 373's 25-minute smoke logged 411 minion-idle warns, and every one of them ended within 0.37 s.
    [Check("runtime.invariant-minion-idle-attack-end-is-not-a-stall", Serial = true)]
    public static void AMinionThatStopsAttackingIsNotIdleUntilItStandsStill()
    {
        var log = new Log();
        using var runtime = Start(log);
        var target = runtime.Session.Participant(2)!.Hero!;
        target.Position = new Position2(7100, 7000);
        // Pattern 1: an Engage minion whose target dies is still in Engage, without a route, when the probe sees it.
        var engaged = Minion(runtime, new Position2(7000, 7000));
        // Pattern 2: a minion re-routed into March in the tick its target died has a route it has not walked yet.
        var marched = Minion(runtime, new Position2(7000, 7200));
        engaged.MinionState = marched.MinionState = MinionAiState.Engage;
        engaged.AttackTarget = marched.AttackTarget = target.NetId;
        Idle(runtime, 12);
        engaged.AttackTarget = null;
        marched.AttackTarget = null;
        marched.MinionState = MinionAiState.March;
        marched.Waypoints.Add(new Position2(7300, 7200));
        runtime.PlaytestDiagnostics!.Tick();
        TestAssert.Equal(0, Events(log, engaged, "minion-idle").Length + Events(log, marched, "minion-idle").Length,
            "the tick an attack ends is not a stall: the minion has only just stopped attacking");
        // The shipped FindEnemies timer (0.25 s) turns the Engage minion to March with a route; both then walk.
        engaged.MinionState = MinionAiState.March;
        engaged.Waypoints.Add(new Position2(7300, 7000));
        Idle(runtime, 0.5);
        engaged.Position += new Position2(50, 0);
        marched.Position += new Position2(50, 0);
        Idle(runtime, 0.5);
        TestAssert.Equal(0, Events(log, engaged, "minion-idle").Length + Events(log, marched, "minion-idle").Length);
        TestAssert.Equal(0, Events(log, engaged, "minion-idle-end").Length + Events(log, marched, "minion-idle-end").Length);
    }

    [Check("runtime.invariant-minion-idle-stall-is-reported", Serial = true)]
    public static void AMinionThatStandsStillWithoutAnAttackIsReported()
    {
        var log = new Log();
        using var runtime = Start(log);
        // The Engage-without-target shape and the March-with-a-route-not-walked shape, both with no attack target for 9 s.
        var engaged = Minion(runtime, new Position2(7000, 7000));
        engaged.MinionState = MinionAiState.Engage;
        var marched = Minion(runtime, new Position2(7000, 7200));
        marched.MinionState = MinionAiState.March;
        marched.Waypoints.Add(new Position2(7300, 7200));
        Idle(runtime, 9);
        var warn = Events(log, engaged, "minion-idle").Single();
        TestAssert.Equal("Engage", warn.Fields.GetProperty("detail").GetProperty("ai_state").GetString()!);
        TestAssert.True(warn.Fields.GetProperty("value").GetDouble() > 8.0, "the warn comes once the 8 s clock has run");
        TestAssert.Equal("March", Events(log, marched, "minion-idle").Single().Fields.GetProperty("detail")
            .GetProperty("ai_state").GetString()!);
        engaged.Position += new Position2(50, 0);
        marched.Position += new Position2(50, 0);
        Idle(runtime, 0.5);
        TestAssert.Equal(1, Events(log, engaged, "minion-idle-end").Length);
        TestAssert.Equal(1, Events(log, marched, "minion-idle-end").Length);
    }

    // Advances the virtual clock in 0.5 s steps and lets the probe see each step (the minions are not ticked by the AI).
    private static void Idle(MatchRuntime runtime, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.5)
        {
            runtime.World.Clock.Advance(0.5);
            runtime.PlaytestDiagnostics!.Tick();
        }
    }

    private static void Exercise(MatchRuntime runtime, Log log, Entity unit, string kind,
        double value, double limit, Action violate, Action resolve)
    {
        violate();
        var before = unit.Position;
        runtime.PlaytestDiagnostics!.Tick();
        TestAssert.Equal(before, unit.Position, "the monitor cannot repair gameplay state");
        var first = Events(log, unit, kind).Single();
        TestAssert.Equal("WARN", first.Level);
        TestAssert.Near(value, first.Fields.GetProperty("value").GetDouble(), 1e-6);
        TestAssert.Near(limit, first.Fields.GetProperty("limit").GetDouble(), 1e-6);
        TestAssert.Equal(unit.Team.ToString(), first.Fields.GetProperty("team").GetString()!);
        TestAssert.True(first.Fields.GetProperty("pos").GetArrayLength() == 2);
        TestAssert.True(first.Fields.TryGetProperty("unit_type", out _));
        TestAssert.True(first.Fields.TryGetProperty("champion", out _));
        runtime.World.Clock.Advance(4.9);
        violate();
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.Equal(1, Events(log, unit, kind).Length, "per-unit, per-kind five-second rate limit");
        runtime.World.Clock.Advance(0.2);
        violate();
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.Equal(2, Events(log, unit, kind).Length);
        resolve();
        runtime.PlaytestDiagnostics.Tick();
        var end = Events(log, unit, kind + "-end").Single();
        TestAssert.Near(5.1, end.Fields.GetProperty("detail").GetProperty("duration").GetDouble(), 1e-6);
        runtime.PlaytestDiagnostics.Tick();
        TestAssert.Equal(1, Events(log, unit, kind + "-end").Length, "end is emitted once");
    }

    private static MatchRuntime Start(Log log, bool start = true, bool bot = false, bool invariants = true)
    {
        var roster = new List<SessionParticipantDefinition>
        {
            new(1, 0, "Annie", "Annie", Team.Order),
            new(2, 1, "Orianna", "Orianna", Team.Chaos),
        };
        if (bot) roster.Add(new(3, 2, "LeeSin", "LeeSin", Team.Order, IsBot: true));
        var previous = Environment.GetEnvironmentVariable("LOG_INVARIANTS");
        MatchRuntime runtime;
        try
        {
            Environment.SetEnvironmentVariable("LOG_INVARIANTS", invariants ? null : "0");
            runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                readySettleSeconds: 0, sessionLog: log, roster: roster);
        }
        finally { Environment.SetEnvironmentVariable("LOG_INVARIANTS", previous); }
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        // The map-ready burst needs every seat spawned and ready, as in the production fixtures.
        for (var id = 1; id <= 2; id++)
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null);
        for (var id = 1; id <= 2; id++) runtime.Session.Spawn(id);
        for (var id = 1; id <= 2; id++) TestAssert.True(runtime.Session.MarkReady(id));
        for (var id = 1; id <= 2; id++)
        {
            runtime.Session.BuildMapReadyPackets(id);
            runtime.Session.Participant(id)!.Hero!.HeroAutoAcquire = false;
        }
        if (start) TestAssert.True(runtime.Session.Start());
        TestAssert.Equal(invariants, runtime.PlaytestDiagnostics!.Enabled);
        return runtime;
    }

    private static Entity Minion(MatchRuntime runtime, Position2 position) => runtime.World.Register(new Entity(
        runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Order, position)
    {
        MinionType = MinionType.Melee, Alive = true, Created = true, Speed = 325,
        Radius = 50, Health = 500, MaxHealth = 500,
    });

    private static IReadOnlyList<SessionOutboundPacket> Chat(MatchRuntime runtime, string text, bool team = false)
    {
        var packet = ChatPackets.Envelope(0, team, Encoding.UTF8.GetBytes(text));
        return runtime.Session.Receive(1, ChatPackets.Channel, runtime.Cipher.Encrypt(packet.Plaintext.Span));
    }

    private static string Text(PlaintextPacket packet) => Encoding.UTF8.GetString(packet.Plaintext.Span.Slice(
        12, BinaryPrimitives.ReadInt32LittleEndian(packet.Plaintext.Span[8..])));

    private static Position2 FindBlocked(NavigationGrid navigation)
    {
        for (var row = 1; row < navigation.CountY - 1; row++)
            for (var column = 1; column < navigation.CountX - 1; column++)
            {
                var cell = new NavCell(column, row);
                if (!navigation.IsMovementWalkable(cell)) return navigation.CellToWorld(cell);
            }
        throw new InvalidOperationException("Map1 has no blocked movement cell");
    }

    private static Row[] Events(Log log, Entity unit, string kind) => log.Rows.Where(row =>
        row.Category == "INVARIANT" && row.Message == kind &&
        row.Fields.GetProperty("unit").GetUInt32() == unit.NetId).ToArray();

    private sealed record Row(string Category, string Level, string Message, JsonElement Fields);
    private sealed class Log : ISessionLogSink
    {
        public List<Row> Rows { get; } = new();
        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (category is "PLAYTEST" or "INVARIANT")
                Rows.Add(new Row(category, level, message, JsonSerializer.SerializeToElement(properties)));
        }
    }
}
