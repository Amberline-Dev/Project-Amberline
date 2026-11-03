using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Server;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>
/// Owner decision, 17 October 2026: the scenario director's production seams (the host's per-step hook, the factory's RNG seed,
/// the bot controller's scripted-seat switch and the 0x7C camera-lock builder) are approved because each does nothing unless the
/// tools use it. These checks pin that: the shipped composition leaves every seam unset and its behaviour is unchanged, and each
/// seam does exactly its one job when a tool does use it. The existing corpora (<c>*-corpus</c>) pin the rest: they replay
/// recorded production behaviour byte for byte.
/// </summary>
public static class ScenarioSeamChecks
{
    [Check("server.runtime-host-before-tick-once-per-step", Serial = true)]
    public static void BeforeTickRunsOncePerFixedStepAndOnlyForAStartedUnpausedMatch()
    {
        var root = TempRoot();
        try
        {
            var options = ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            using var logger = ServerLogger.Open(root, new StringWriter());
            using var network = new BuiltinServerMatchFactory(11).Create(options, logger);
            var runtime = network.Runtime;
            var host = new ServerRuntimeHost(network, options, logger);
            TestAssert.True(host.BeforeTick is null, "the shipped server (Program, ServerApplication) never sets the hook: it starts unset");

            var calls = new List<double>();
            var seen = new List<MatchRuntime>();
            host.BeforeTick = match => { calls.Add(match.Combat.CurrentTime); seen.Add(match); };
            host.RunIteration(1.0);
            TestAssert.Equal(0, calls.Count, "an unstarted match has no step to hook");

            runtime.World.Clock.SetVirtual(0.0);
            var session = runtime.Session;
            var participant = session.Authenticate(1, session.BuildKeyCheck(1))
                ?? throw new InvalidOperationException("the solo seat did not authenticate");
            session.Spawn(1);
            session.MarkReady(1);
            runtime.World.Clock.Advance(2.5);
            TestAssert.True(session.Start(), "the solo session starts");
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team,
                participant.Hero!.NetId, new NullTransport(), ready: true, view: participant.View));

            host.RunIteration(0.01);
            TestAssert.Equal(0, calls.Count, "less than a step of elapsed time runs no step and no hook");
            var origin = runtime.Combat.CurrentTime;
            for (var step = 0; step < 6; step++) host.RunIteration(1.0 / 30.0);
            TestAssert.Equal(6, calls.Count, "one hook call per executed step");
            for (var step = 0; step < calls.Count; step++)
                TestAssert.Near(origin + step / 30.0, calls[step], 1e-9, "the hook runs before the step: it sees the time the step starts from");
            TestAssert.Near(origin + 6 / 30.0, runtime.Combat.CurrentTime, 1e-9, "and every hooked step was a real step");
            TestAssert.True(seen.All(match => ReferenceEquals(match, runtime)), "the hook gets the host's own match runtime");

            host.RunIteration(5.0);
            TestAssert.Equal(7, calls.Count, "a long wall interval is still one step per iteration, as before the hook existed");

            // A paused match runs no step, so the hook does not run either.
            runtime.Session.Receive(1, 1, NativeRequestWire.Encrypt(runtime.Cipher,
                PausePackets.Pause(uint.MaxValue, 1).Plaintext.Span));
            TestAssert.True(runtime.Session.Paused, "the match is paused");
            host.RunIteration(0.5);
            host.RunIteration(0.5);
            TestAssert.Equal(7, calls.Count, "no hook while paused");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Check("server.factory-rng-seed-reaches-runtime", Serial = true)]
    public static void FactorySeedReachesTheRuntimeAndTheShippedFactoryDrawsItsOwn()
    {
        var root = TempRoot();
        try
        {
            var options = ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            var firstRolls = new List<double>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var logger = ServerLogger.Open(Path.Combine(root, attempt.ToString()), new StringWriter());
                var factory = new BuiltinServerMatchFactory(20261017);
                TestAssert.Equal(20261017, factory.RngSeed, "the factory reports its seed");
                using var network = factory.Create(options, logger);
                var random = network.Runtime.Dependencies.Random as SeededRandomSource
                    ?? throw new InvalidOperationException("a match draws from a seeded source");
                TestAssert.Equal(20261017, random.Seed, "the seed reaches the runtime's RNG");
                firstRolls.Add(random.NextDouble());
                logger.Flush();
                using var stream = new FileStream(Path.Combine(Directory.GetDirectories(Path.Combine(root, attempt.ToString())).Single(), "session.jsonl"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var composed = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonDocument.Parse(line).RootElement)
                    .Single(row => row.GetProperty("msg").GetString() == "match.composed");
                TestAssert.Equal(20261017, composed.GetProperty("rng_seed").GetInt32(), "match.composed logs the seed that was given");
            }
            TestAssert.Equal(firstRolls[0], firstRolls[1], "one seed, one sequence of rolls");

            // The shipped executable constructs the factory with no argument: no fixed seed, an entropy-drawn one per match.
            var shipped = new BuiltinServerMatchFactory();
            TestAssert.True(shipped.RngSeed is null, "the shipped factory has no fixed seed");
            using var shippedLogger = ServerLogger.Open(Path.Combine(root, "shipped"), new StringWriter());
            using var shippedNetwork = shipped.Create(options, shippedLogger);
            var drawn = shippedNetwork.Runtime.Dependencies.Random as SeededRandomSource
                ?? throw new InvalidOperationException("a shipped match draws from a seeded source");
            TestAssert.True(drawn.Seed >= 0, "an entropy-drawn seed is a non-negative int");
            TestAssert.True(shippedNetwork.Runtime.Bots!.ScriptedSeatCount == 0, "the shipped composition scripts no seat");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Check("runtime.bots-scripted-seat-issues-no-orders")]
    public static void ASeatReleasedFromTheBotScriptsIssuesNoOrder()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Chaos),
            new SessionParticipantDefinition(2, 1, "BotA", "Orianna", Team.Order, IsBot: true),
            new SessionParticipantDefinition(3, 2, "BotB", "Ezreal", Team.Order, IsBot: true),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0.0,
            random: new SeededRandomSource(5), roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var session = runtime.Session;
        session.CreateWorld();
        TestAssert.True(session.Authenticate(1, session.BuildKeyCheck(1)) is not null);
        session.Spawn(1);
        session.MarkReady(1);
        session.BuildMapReadyPackets(1);
        TestAssert.True(session.Start(), "the roster starts");
        var bots = runtime.Bots ?? throw new InvalidOperationException("the production graph has a bot controller");
        var botA = session.Participants[1];
        var botB = session.Participants[2];
        TestAssert.Equal(0, bots.ScriptedSeatCount, "nothing is scripted unless a tool asks (the shipped path never does)");
        TestAssert.False(bots.IsSeatScripted(botB.PlayerId));
        TestAssert.Equal(2, bots.ControlledBotCount, "both bot seats run the retail scripts");

        bots.SetSeatScripted(botB.PlayerId, true);
        TestAssert.True(bots.IsSeatScripted(botB.PlayerId));
        TestAssert.Equal(1, bots.ControlledBotCount, "a scripted seat is no longer a controlled bot");
        var startB = botB.Hero!.Position;
        for (var tick = 0; tick < 20 * 30; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(bots.OrdersIssued(botA.PlayerId) > 0, "the seat still under the scripts played");
        TestAssert.Equal(0, bots.OrdersIssued(botB.PlayerId), "the released seat issued no order");
        TestAssert.Equal(startB, botB.Hero.Position, "and never left its spawn");
        TestAssert.True(botB.Hero.Inventory.All(slot => slot is null), "nor bought anything");

        // The switch is reversible: handing the seat back lets the scripts play it again.
        bots.SetSeatScripted(botB.PlayerId, false);
        TestAssert.Equal(0, bots.ScriptedSeatCount);
        for (var tick = 0; tick < 10 * 30; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(bots.OrdersIssued(botB.PlayerId) > 0, "a seat handed back plays again");
    }

    /// <summary>
    /// Batch 167: the Lua AI family a champion runs is its seat's, not the unit's. A bot seat's hero
    /// runs <c>Bot.luaobj</c> on the bot controller's host and never a <c>Hero.luaobj</c> instance on
    /// the hero host; a human player's hero the other way round. Two chunks over one hero would run
    /// two decision makers over the same orders (and each one's <c>OnInit</c> would clear what the
    /// other just ordered), and a bot's tasks must never see a teammate's hero as a script instance
    /// of its own.
    /// </summary>
    [Check("runtime.bots-hero-script-family-follows-the-seat")]
    public static void EachSeatRunsExactlyOneHeroScriptFamily()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Chaos),
            new SessionParticipantDefinition(2, 1, "BotA", "Orianna", Team.Order, IsBot: true),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, random: new SeededRandomSource(5), roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var session = runtime.Session;
        session.CreateWorld();
        TestAssert.True(session.Authenticate(1, session.BuildKeyCheck(1)) is not null);
        session.Spawn(1);
        session.MarkReady(1);
        session.BuildMapReadyPackets(1);
        TestAssert.True(session.Start(), "the roster starts");
        var bots = runtime.Bots ?? throw new InvalidOperationException("the production graph has a bot controller");
        var heroHost = (LuaAiService)runtime.Buffs.Heroes!;
        var human = session.Participants[0].Hero!;
        var bot = session.Participants[1].Hero!;

        TestAssert.True(heroHost.Drives(human), "the hero host claims a human player's hero");
        TestAssert.False(heroHost.Drives(bot), "and not a bot seat's");
        TestAssert.True(bots.Host.Drives(bot), "the bot host claims a bot seat's hero");
        TestAssert.False(bots.Host.Drives(human), "and not a human player's");

        for (var tick = 0; tick < 5 * 30; tick++) runtime.Tick(1.0 / 30.0);

        // InstanceFor creates an instance (and runs its OnInit) on first use, so an answer of null
        // is the claim refusing the unit: there is no Hero.luaobj behind a bot seat's hero for
        // anything to dispatch to.
        TestAssert.True(heroHost.InstanceFor(human) is not null, "the human player's hero runs Hero.luaobj");
        TestAssert.True(heroHost.InstanceFor(bot) is null, "a bot seat's hero never gets a Hero.luaobj instance");
        TestAssert.True(bots.Host.InstanceFor(bot) is not null, "the bot seat's hero runs Bot.luaobj");
        TestAssert.True(bots.Host.InstanceFor(human) is null, "a human player's hero never gets a Bot.luaobj instance");
        TestAssert.Equal(1, heroHost.Instances.Count(), "one chunk per seat, not one per hero in the world");
        TestAssert.Equal(1, bots.Host.Instances.Count(), "the bot host drives its own seat alone");

        // A seat the director takes over is a human seat again at once: the same switch the order
        // gate reads is the one the claim reads.
        bots.SetSeatScripted(session.Participants[1].PlayerId, true);
        TestAssert.False(bots.Host.Drives(bot), "a released seat runs no Bot.luaobj");
        TestAssert.True(heroHost.Drives(bot), "it is the hero host's now");
    }

    [Check("protocol.lock-camera-layout")]
    public static void LockCameraIsTheSixByteStockPacket()
    {
        const uint hero = 0x40000001;
        var locked = VisionPackets.LockCamera(hero, true);
        TestAssert.Equal(new byte[] { 0x7C, 0x01, 0x00, 0x00, 0x40, 0x01 }, locked.Plaintext.ToArray(),
            "[0x7C][netid u32][lock u8]: the client's case 0x7C reads only the byte at offset 5");
        TestAssert.Equal(new byte[] { 0x7C, 0x01, 0x00, 0x00, 0x40, 0x00 }, VisionPackets.LockCamera(hero, false).Plaintext.ToArray());
        TestAssert.Equal(LeagueProtocolConstants.VariantChannel, locked.Channel, "gameplay channel 3");
        TestAssert.True(locked.Reliability == LeaguePacketReliability.Reliable, "reliable like SetCameraPosition");
        TestAssert.Equal(hero, locked.EntityReferences.Single().NetId, "it is addressed through the hero");

        var catalog = PacketCatalog.Default;
        TestAssert.True(catalog.TryGet(0x7C, LeagueProtocolConstants.VariantChannel, out var spec), "the packet-stream checker knows it");
        TestAssert.True(spec.Reliable && spec.Name == "LockCamera");
        Span<uint> found = stackalloc uint[4];
        TestAssert.Equal(1, spec.ReadReferences(locked.Plaintext.Span, found), "the catalog reads the hero's net ID");
        TestAssert.Equal(hero, found[0]);
    }

    [Check("packet-rules.lock-camera-known")]
    public static void ThePacketStreamCheckerKnowsTheCameraLock()
    {
        const uint hero = 0x40000001;
        // A lock for a hero the peer was created with is clean...
        var known = new Amberline.Tools.Diagnostics.PacketStreamChecker(1);
        known.Observe(PacketRuleChecks.Observation(SpawnPackets.CreateHero(hero, 1, "Annie", "Annie", teamIsOrder: true), 0.0));
        known.Observe(PacketRuleChecks.Observation(VisionPackets.LockCamera(hero, true), 1.0));
        var clean = known.Finish();
        TestAssert.True(clean.Count == 0, "the director's 0x7C is on the catalog's channel with the catalog's delivery: " +
            string.Join("; ", clean.Select(violation => violation.Kind + " " + violation.Detail)));

        // ...one for a unit the peer never saw is not: the net ID at offset 1 is read as a reference.
        var unknown = new Amberline.Tools.Diagnostics.PacketStreamChecker(1);
        unknown.Observe(PacketRuleChecks.Observation(VisionPackets.LockCamera(hero, true), 1.0));
        var found = unknown.Finish().Where(violation => violation.Kind == Amberline.Tools.Diagnostics.PacketRuleKinds.UnknownUnit).ToArray();
        TestAssert.True(found.Length >= 1 && found[0].Opcode == 0x7C && found[0].NetId == hero, "a lock for an unknown hero is flagged");
    }

    [Check("protocol.production-never-sends-lock-camera")]
    public static void AProductionMatchSendsNoCameraLockToItsPlayer()
    {
        // The seam is inert in the shipped path: a human seat in a production composition with working bots receives packets for a
        // minute of game time and none of them is the 0x7C lock-camera packet (only the scenario director ever sends it).
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Chaos),
            new SessionParticipantDefinition(2, 1, "BotA", "Orianna", Team.Order, IsBot: true),
            new SessionParticipantDefinition(3, 2, "BotB", "Ezreal", Team.Chaos, IsBot: true),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0.0,
            random: new SeededRandomSource(9), roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        var participant = session.Authenticate(1, session.BuildKeyCheck(1)) ?? throw new InvalidOperationException("the human did not authenticate");
        session.Spawn(1);
        session.MarkReady(1);
        session.BuildMapReadyPackets(1);
        TestAssert.True(session.Start());
        var capture = new CountingTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team, participant.Hero!.NetId, capture,
            ready: true, view: participant.View));
        for (var tick = 0; tick < 60 * 30; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(capture.Total > 100, $"the human received a real stream ({capture.Total} packets)");
        TestAssert.Equal(0, capture.LockCameras, "and none of it is LockCamera");
        TestAssert.Equal(0, runtime.Bots!.ScriptedSeatCount, "no seat was scripted");
    }

    private sealed class CountingTransport : IReplicationTransport
    {
        public int Total { get; private set; }
        public int LockCameras { get; private set; }

        public void Send(PlaintextPacket packet)
        {
            Total++;
            if (packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == VisionPackets.LockCameraOpcode) LockCameras++;
        }
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "amberline-seams-" + Guid.NewGuid().ToString("N"));

    private sealed class NullTransport : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }
}
