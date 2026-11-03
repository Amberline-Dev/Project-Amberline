using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>Batch 446: the map 8 capture points stand on the map (five neutral guardians) and their capture trees tick for 60 s.</summary>
public static class BehaviorTreeCaptureChecks
{
    [Check("scripting.behaviour-tree-map8-capture-points-stand")]
    public static void MapEightCapturePointsStandAndTheirTreesTick()
    {
        var world = new WorldState();
        // Batch 448: no test turret seam; the host spawns the point turrets (one per team per point) and GetTurret answers them.
        // DIAGNOSTICS: test spawn path: a unit of the character record, registered and not bound to any script (no CharOnActivate).
        var spawned = new List<Entity>();
        var notes = new List<string>();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (key, _) => notes.Add(key),
            null,
            (skin, at, team) =>
            {
                var unit = new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
                {
                    Name = skin,
                    ChampionName = skin,
                };
                world.Register(unit);
                spawned.Add(unit);
                return unit;
            })
            ?? throw new InvalidOperationException("map 8 has a quest host");

        const double step = 1.0 / 30.0;
        for (var tick = 1; tick <= 1800; tick++)
            host.Tick(tick * step, step);

        // Five guardians, one per capture point, on each point's centre (Locations.dat: CapturePointA to E are Info_PointA to E).
        var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
        var guards = spawned.Where(unit => unit.Name == "OdinNeutralGuardian").ToList();
        TestAssert.Equal(5, guards.Count, "five OdinNeutralGuardian units spawn, one per capture point");
        foreach (var letter in new[] { "A", "B", "C", "D", "E" })
        {
            var centre = SceneMesh.CentralPoint(Path.Combine(scene, "Info_Point" + letter + ".SCB"));
            TestAssert.True(guards.Any(guard => guard.Position.X == centre.X && guard.Position.Z == centre.Z),
                "a guardian stands on Info_Point" + letter);
        }
        TestAssert.True(guards.All(guard => guard.Team == Team.Neutral && guard.NotTargetableToEnemy),
            "the guardians are neutral and untargetable");

        // Each point's capture instance ticks for the whole minute, with its id and its guard squad.
        TestAssert.Equal(5, host.Captures.Count, "one EvaluateCapturePoint instance per capture point");
        TestAssert.True(host.Captures.All(capture => capture.Ticks == 1800), "every capture instance ticks 1800 times with no exception");
        TestAssert.True(Enumerable.Range(1, 5).SequenceEqual(host.Captures.Select(capture => (int)(double)capture.Variable("CapturePointID")!)),
            "the instances carry CapturePointID 1 to 5");
        TestAssert.True(host.Captures.All(capture => (double)capture.Variable("TurretGuardId")! > 0.0),
            "each point's guard squad is its TurretGuardId");

        // The initialisation reaches its encounter nodes: the wave encounter is created (CreateEncounterFromDefinition), and
        // MeleeWaveBase is added to it. It stops at RangedWaveBase, which no shipped file holds (OdinScriptBehaviorTrees.xml:2068).
        var manager = host.Instances[0];
        TestAssert.True(manager.Variable("WaveEncounterID") is double, "CreateEncounterFromDefinition created the wave encounter");
        // Batch 448: the missing mutator (RangedWaveBase) is noted and succeeds, so the initialisation runs to its last debug circle.
        TestAssert.True(notes.Contains("mutator:RangedWaveBase"), "the missing mutator is noted once");
        TestAssert.True(manager.Variable("ChaosDebugCircleE") is double, "the initialisation reaches its last node");
        TestAssert.True(notes.Count == notes.Distinct().Count(), "every note is reported once");
    }

    /// <summary>
    /// Batch 448: a champion on point A for the whole minute makes point A its team's: the owner changes, the capture packets (0xDB,
    /// 0xDC) go to the clients, the captured announcement is raised and the owner's turret is shown. An empty point stays neutral.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-point-changes-owner")]
    public static void MapEightCapturePointChangesOwnerWhenHeldAndShowsTheTurret()
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var notes = new List<string>();
        // Batch 456: a champion captures a point only by channelling OdinCaptureChannel on its guardian (see the standing-champion check).
        var channelling = new Dictionary<Entity, Entity>();
        var hooks = new CapturePointHooks(
            Send: packets.Add,
            Kill: (unit, _) => unit.Alive = false,
            AwardGold: (_, _) => true,
            CaptureChannelTarget: hero => channelling.GetValueOrDefault(hero));
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (key, _) => notes.Add(key), null,
            (skin, at, team) => world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
            {
                Name = skin,
                ChampionName = skin,
            }),
            hooks)
            ?? throw new InvalidOperationException("map 8 has a quest host");

        // A champion of Order channels OdinCaptureChannel on point A's guardian (a stand-in at the centre). A Chaos champion stands on
        // point B without channelling: it moves nothing.
        var ryze = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, PointACentre())
            { Name = "Ryze", ChampionName = "Ryze" });
        channelling[ryze] = PointGuardian(world, PointACentre());
        world.Register(MapEightChampion(world, Team.Chaos, PointCentre("B")));

        // CapturePointMath adds 0.85 per tick for one attacker (the shipped BaseCaptureContribution); the reference tick is 0.1 s
        // (batch 450), so one champion takes about 11.8 s. The check still holds the channel for 150 s ("for long enough").
        const double step = 1.0 / 30.0;
        for (var tick = 1; tick <= 4500; tick++)
            host.Tick(tick * step, step);

        var owners = host.CapturePointOwners;
        TestAssert.True(owners[1] == Team.Order, "a champion held point A for 150 s: point A is Order's");
        TestAssert.True(owners.Where(pair => pair.Key != 1).All(pair => pair.Value == Team.Neutral), "the empty points stay neutral");
        var opcodes = packets.Select(packet => packet.Plaintext.Span[0]).ToList();
        TestAssert.True(opcodes.Contains((byte)0xDB) && opcodes.Contains((byte)0xDC), "the 0xDB and 0xDC capture records go to the clients");
        // A record goes out only when its bytes change: 4500 ticks of five points would be 45,000 packets if each tick sent both.
        TestAssert.True(packets.Count < 1000, $"capture records are sent on change only, not every tick ({packets.Count} sent)");
        // Batch 455: the captured announcement is 0x4A event 90 (OnCapturePointCaptured_A) on map 8, sourced by a unit of Order (the text's
        // @src your team | enemy team@). Batch 466: the source is the channelling Order champion (the point turrets are server-side handles no
        // client holds; it was Order's turret of point A). The start messages 79 and 80 (FirstMessage true in CapturePointManager, false in
        // OdinOpeningCeremony) each go out once.
        var events = packets.Where(packet => packet.Plaintext.Span[0] == 0x4A)
            .Select(packet => (Event: packet.Plaintext.Span[5], Source: BitConverter.ToUInt32(packet.Plaintext.Span.Slice(6, 4)),
                Map: BitConverter.ToInt32(packet.Plaintext.Span.Slice(14, 4)))).ToList();
        // Batch 475: the 18-byte 0x4A's i32 is the announcement's token argument. @CapturePoint@ and @MapNumber@ (the client's
        // readers) read the same dword, so point A's captured event carries its capture index 0 ("game_capture_point_0", Quarry,
        // fontconfig_en_US.txt:1539). Batch 455 sent the map number 8 for every Dominion event, which the client read as "game_capture_point_8".
        TestAssert.True(events.Count(e => e.Event == 90 && e.Source == ryze.NetId && e.Map == 0) == 1,
            "point A captured by Order: one 0x4A event 90 sourced by the Order champion channelling it, carrying capture index 0");
        TestAssert.True(events.Where(e => e.Event is 79 or 80).All(e => e.Map == 8), "the start messages carry map number 8");
        // Batch 466: the capture records name point A's guardian, the unit whose PAR is the capture meter.
        var guardianA = host.Context.PointGuardian(2)!.NetId;
        TestAssert.True(packets.Any(packet => packet.Plaintext.Span[0] == 0xDB && BitConverter.ToUInt32(packet.Plaintext.Span.Slice(5, 4)) == guardianA),
            "point A's 0xDB names its guardian");
        TestAssert.True(events.Count(e => e.Event == 79) == 1 && events.Count(e => e.Event == 80) == 1,
            "the start messages 79 and 80 each go out once (" + string.Join(", ", events.GroupBy(e => e.Event)
                .Select(group => group.Key + "x" + group.Count())) + ")");
        TestAssert.True(host.Context.Turret(Team.Order, 0, 2) is { NoRender: false }, "the Order turret of point A is shown");
        TestAssert.True(host.Context.Turret(Team.Chaos, 0, 2) is { NoRender: true }, "the Chaos turret of point A stays hidden");
        TestAssert.True(notes.Count == notes.Distinct().Count(), "every note is reported once");
    }

    /// <summary>
    /// Batch 475 (Reconstruction): a point's guardian (the OdinNeutralGuardian that stands on the point, the unit the owner's test showed
    /// with a health bar) shows no health bar. No shipped Odin script or character names a bar hide (no ShowHealthBar in the Odin scripts),
    /// so the hide is the engine's: the guardian's HealthBarHidden, which BuildBuffs sends as ShowHealthBar (0xD7) false, as it
    /// does for a placemarker.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-point-guardian-shows-no-health-bar")]
    public static void MapEightPointGuardianShowsNoHealthBar()
    {
        var (host, _, _) = NewMapEightHost();
        host.Tick(1.0 / 30.0, 1.0 / 30.0);
        for (var position = 2; position <= 6; position++)
        {
            var guardian = host.Context.PointGuardian(position) ?? throw new InvalidOperationException($"point {position} has no guardian");
            TestAssert.True(guardian.HealthBarHidden, $"point {position}'s guardian hides its health bar");
        }
    }

    /// <summary>
    /// Batch 475 (follow-up, proven in the client): the capture records' bytes. The 0xDB flex (mode 0) carries the capture index in
    /// its index byte: the client's flex-value reader takes that byte as an index into the CapturePoints list
    /// A rate has no client field, so no packet carries it. Batch 494 (restated): the 0xDC record holds the PAR type in one byte,
    /// which AttachCapturePointToObject binds the guardian with, 0 (MP); the client reads the PAR percent of that type.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-record-carries-index-and-progress")]
    public static void MapEightCaptureRecordCarriesIndexAndProgress()
    {
        var packets = new List<PlaintextPacket>();
        var (host, world, channelling) = NewMapEightHost(packets.Add);
        var ryze = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, PointACentre())
            { Name = "Ryze", ChampionName = "Ryze" });
        channelling[ryze] = PointGuardian(world, PointACentre());
        const double step = 1.0 / 30.0;
        host.Tick(step, step);
        // Batch 475: the guardians are re-created on a capture, so each point's records name the guardian of their time: the first one, then
        // the replacement (added after the run).
        var indexOf = Enumerable.Range(2, 5).ToDictionary(position => host.Context.PointGuardian(position)!.NetId, position => position - 2);
        for (var tick = 2; tick <= 2100; tick++)
            host.Tick(tick * step, step);
        foreach (var position in Enumerable.Range(2, 5))
            indexOf.TryAdd(host.Context.PointGuardian(position)!.NetId, position - 2);
        var flex = packets.Where(packet => packet.Plaintext.Span[0] == 0xDB).ToList();
        TestAssert.True(flex.Count > 0, "the 0xDB flex records are sent");
        foreach (var packet in flex)
        {
            var span = packet.Plaintext.Span;
            TestAssert.True(indexOf.TryGetValue(BitConverter.ToUInt32(span.Slice(5, 4)), out var index), "0xDB names a point guardian");
            // Batch 493e (restated, replaces batch 479b's mode 1): the gauge is the point's CapturePointFlexValue (mode 0) of its capture index.
            TestAssert.True(span[9] == 0 && span[10] == index && span[11] == 0,
                $"0xDB binds the point's capture flex (event 0, index {index}, mode 0; got {span[9]}, {span[10]}, {span[11]})");
            TestAssert.True(span.Length == 12, $"0xDB is a 12-byte flex record ({span.Length} bytes)");
        }

        var records = packets.Where(packet => packet.Plaintext.Span[0] == 0xDC).ToList();
        TestAssert.True(records.All(packet => packet.Plaintext.Span[15] == 0), "every 0xDC applies (mode 0)");
        // Point A's records (index 0) across its guardians: each guardian is attached with the guardian's PAR type (0, MP), the first and the
        // replacement alike.
        var parTypesA = records.Where(packet => packet.Plaintext.Span[5] == 0).Select(packet => packet.Plaintext.Span[10]).ToList();
        TestAssert.True(parTypesA.Count > 1, $"point A's 0xDC attaches each of its guardians ({parTypesA.Count} sent)");
        TestAssert.True(parTypesA.All(type => type == 0), $"point A's 0xDC names the guardian's PAR type 0 ({string.Join(",", parTypesA)})");
    }

    /// <summary>
    /// Batch 475d (item 1): the guardian's character record carries its pool (Data*BaseMP 50000, the same value as Data*BaseHP) and its health.
    /// The spawn takes both from this record (BbNativeHandlers.Spawn.cs MaxMana and MaxHealth).
    /// </summary>
    [Check("scripting.odin-neutral-guardian-record-has-its-pool-and-health")]
    public static void OdinNeutralGuardianRecordHasItsPoolAndHealth()
    {
        var record = GameData.Default.Champion("OdinNeutralGuardian");
        TestAssert.True(record.BaseMp == 50000.0 && record.BaseHp == 50000.0,
            $"the guardian's record reads BaseMp {record.BaseMp} and BaseHp {record.BaseHp} (shipped: 50000 and 50000)");
    }

    /// <summary>
    /// Batch 475 (second follow-up, Reconstruction): a capture re-creates the point's guardian on the owner's team. AiEntityDataFile.dat:18-21
    /// names a team guardian per owner (OdinOrderGuardian, OdinChaosGuardian) that the 126 client does not hold, and no team-change packet is
    /// proven, so the neutral guardian is replaced. The old one leaves the world and every client silently (LeaveVisibility: no kill, no
    /// death packet); the new one is spawned at the same place through the encounter path, so its character script runs again. The ring and
    /// the minimap take the owner from the 0xDC-bound unit's team.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-recreates-the-guardian-on-the-owner-team")]
    public static void MapEightCaptureRecreatesTheGuardianOnTheOwnerTeam()
    {
        var packets = new List<PlaintextPacket>();
        var departed = new List<Entity>();
        var (host, world, channelling) = NewMapEightHost(packets.Add, departed.Add);
        const double step = 1.0 / 30.0;
        host.Tick(step, step);
        var original = host.Context.PointGuardian(2) ?? throw new InvalidOperationException("point A has a guardian");
        var ryze = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, PointACentre())
            { Name = "Ryze", ChampionName = "Ryze" });
        channelling[ryze] = PointGuardian(world, PointACentre());
        for (var tick = 2; tick <= 2100; tick++)
            host.Tick(tick * step, step);

        TestAssert.True(host.CapturePointOwners[1] == Team.Order, "a champion held point A for 70 s: point A is Order's");
        var current = host.Context.PointGuardian(2) ?? throw new InvalidOperationException("point A still has a guardian");
        TestAssert.True(current.NetId != original.NetId, "point A's guardian is a new unit after the capture");
        TestAssert.True(current.Team == Team.Order, "the new guardian is on the owner's team");
        TestAssert.True(world.Get(current.NetId) == current, "the new guardian is in the world");
        TestAssert.True(world.Get(original.NetId) is null, "the old guardian left the world");
        TestAssert.True(original.Alive, "the old guardian was not killed (no kill credit)");
        // Batch 493: the leave goes through the replication's departure (ReplicationDelivery.Depart builds it for each observer that sees the unit);
        // a 0x54 queued through Send is dropped by the delivery as refresh-rebuilt.
        TestAssert.True(departed.Count == 1 && ReferenceEquals(departed[0], original),
            "the old guardian departs the clients (LeaveVisibility through ReplicationDelivery.Depart)");
        TestAssert.True(packets.All(packet => packet.Plaintext.Span[0] != 0xA6), "no death packet (0xA6) is sent");
        // The new guardian is attached to the point: its first 0xDC is sent on the step after the spawn, with the guardian's PAR type (0, MP;
        // batch 494: the client reads the point's value as GetPARPercent of that type).
        var firstRecord = packets.FirstOrDefault(packet => packet.Plaintext.Span[0] == 0xDC && packet.Plaintext.Span[5] == 0 &&
            BitConverter.ToUInt32(packet.Plaintext.Span.Slice(6, 4)) == current.NetId);
        TestAssert.True(firstRecord is not null, "a 0xDC binds the new guardian (point A's index 0)");
        TestAssert.True(firstRecord!.Plaintext.Span[10] == 0,
            $"the new guardian's first 0xDC names its PAR type 0 (byte {firstRecord.Plaintext.Span[10]})");
        // Batch 475 (second follow-up, root cause): the graveyard's 0xDE create names the point's guardian (BehaviorLeaves.Relics CreateRespawnPoint),
        // and the tree creates it once. A re-created guardian needs its own create, or its graveyard is not known to the clients.
        TestAssert.True(packets.Any(packet => packet.Plaintext.Span[0] == 0xDE && packet.Plaintext.Span.Length == 11 && packet.Plaintext.Span[5] == 0 &&
            packet.Plaintext.Span[6] == 0 && BitConverter.ToUInt32(packet.Plaintext.Span.Slice(7, 4)) == current.NetId),
            "a 0xDE create names the new guardian for graveyard 0 (the respawn point follows the guardian)");
    }

    /// <summary>
    /// Batch 456 (Reconstruction, part 1): a champion counts toward a point only while it channels OdinCaptureChannel on that point's guardian
    /// (wiki, Capture point: a champion "will begin channeling", Dominion: "the more Champions that are channeling, the faster"). The shipped
    /// tree counted every hero in its radius, so a champion standing on a point drained its owner in one tick (batch 452's remark). Now a
    /// standing champion moves nothing: after one tick no point's progress has changed, and after 150 s every point is still neutral.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-standing-champion-moves-nothing")]
    public static void MapEightAStandingChampionMovesNothing()
    {
        var (host, world, _) = NewMapEightHost();
        world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        world.Register(MapEightChampion(world, Team.Chaos, PointCentre("B")));
        const double step = 1.0 / 30.0;
        host.Tick(step, step);
        TestAssert.True(host.Captures.All(capture => (double)capture.Variable("CaptureProgress")! == 0.0),
            "one tick with a standing champion on A and on B leaves every point's progress at zero");
        for (var tick = 2; tick <= 4500; tick++)
            host.Tick(tick * step, step);
        TestAssert.True(host.CapturePointOwners.Values.All(owner => owner == Team.Neutral),
            "two champions standing on points A and B for 150 s leave every point neutral");
        TestAssert.True(host.Captures.All(capture => (double)capture.Variable("CaptureProgress")! == 0.0),
            "and no point's progress moves");
    }

    // Batch 450: the capture time base. The League wiki (its Capture point page): one champion takes
    // 10 s to move a point from enemy to neutral or from neutral to allied, twice that from full enemy alignment, and more champions are
    // faster with diminishing returns. The Reconstruction time base is 0.1 s per shipped tick (BehaviorLeaves.ReferenceTickSeconds).
    // Batch 456: the champions here channel (the rule of part 1); a standing champion would count the same as before, so only the channel
    // is what the checks name.
    [Check("scripting.behaviour-tree-map8-capture-one-champion-takes-ten-to-twelve-seconds")]
    public static void MapEightOneChampionTakesTenToTwelveSecondsOnANeutralPoint()
    {
        var (host, world, channelling) = NewMapEightHost();
        var champion = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[champion] = PointGuardian(world, PointACentre());
        var clock = 0.0;
        var seconds = SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock);
        TestAssert.True(seconds is >= 10.0 and <= 12.0,
            $"one channelling champion makes a neutral point its team's in 10 to 12 s of game time ({seconds:F2} s)");
    }

    [Check("scripting.behaviour-tree-map8-capture-two-champions-are-faster")]
    public static void MapEightTwoChampionsCaptureFasterThanOne()
    {
        var (oneHost, oneWorld, oneChannelling) = NewMapEightHost();
        var oneChampion = oneWorld.Register(MapEightChampion(oneWorld, Team.Order, PointACentre()));
        oneChannelling[oneChampion] = PointGuardian(oneWorld, PointACentre());
        var oneClock = 0.0;
        var one = SecondsUntilOwned(oneHost, 1, Team.Order, 60.0, ref oneClock);

        var (twoHost, twoWorld, twoChannelling) = NewMapEightHost();
        var firstChampion = twoWorld.Register(MapEightChampion(twoWorld, Team.Order, PointACentre()));
        var secondChampion = twoWorld.Register(MapEightChampion(twoWorld, Team.Order, PointACentre()));
        var twoGuardian = PointGuardian(twoWorld, PointACentre());
        twoChannelling[firstChampion] = twoGuardian;
        twoChannelling[secondChampion] = twoGuardian;
        var twoClock = 0.0;
        var two = SecondsUntilOwned(twoHost, 1, Team.Order, 60.0, ref twoClock);
        TestAssert.True(one is not null && two is not null && two < one,
            $"two channelling champions take less time than one ({two:F2} s against {one:F2} s)");

        // Batch 456: a standing champion adds nothing. One channelling with one standing takes as long as one channelling alone.
        var (standHost, standWorld, standChannelling) = NewMapEightHost();
        var channeller = standWorld.Register(MapEightChampion(standWorld, Team.Order, PointACentre()));
        standChannelling[channeller] = PointGuardian(standWorld, PointACentre());
        standWorld.Register(MapEightChampion(standWorld, Team.Order, PointACentre()));
        var standClock = 0.0;
        var withStanding = SecondsUntilOwned(standHost, 1, Team.Order, 60.0, ref standClock);
        TestAssert.True(one is not null && withStanding == one,
            $"a standing champion adds nothing: one channelling with one standing takes {withStanding:F2} s, one alone {one:F2} s");
    }

    [Check("scripting.behaviour-tree-map8-capture-enemy-point-takes-twice-as-long")]
    public static void MapEightAnEnemyPointTakesTwiceTheNeutralTime()
    {
        var (host, world, channelling) = NewMapEightHost();
        var guardian = PointGuardian(world, PointACentre());
        var chaos = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        channelling[chaos] = guardian;
        var clock = 0.0;
        var toEnemy = SecondsUntilOwned(host, 1, Team.Chaos, 60.0, ref clock);
        // The Chaos champion leaves the point (its channel ends with it) and an Order champion channels on it: the point goes from full enemy
        // alignment to Order.
        chaos.Position = new Position2(PointACentre().X + 5000.0, PointACentre().Z);
        channelling.Remove(chaos);
        var order = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[order] = guardian;
        var toAllied = SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock);
        TestAssert.True(toEnemy is >= 10.0 and <= 12.0 && toAllied is >= 21.0 and <= 26.0,
            $"a point held by the enemy takes twice the neutral time ({toEnemy:F2} s to take it, {toAllied:F2} s to turn it allied)");
    }

    /// <summary>
    /// Batch 462: the champions of the acting team that channel the capture count it when the tree announces it (NODE_CAPTURE, NODE_NEUTRALIZE;
    /// BehaviorLeaves.CreditChannellers), a standing champion counts nothing, and the owner's map 8 0x4B carries the counts in the ODIN collection
    /// the client reads (ClassicHeroStatsChecks.DecodeClient).
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-credits-the-channelling-champions")]
    public static void MapEightCaptureCreditsTheChannellingChampions()
    {
        var (host, world, channelling) = NewMapEightHost();
        var guardian = PointGuardian(world, PointACentre());
        var chaos = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        var standing = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        channelling[chaos] = guardian;
        var clock = 0.0;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Chaos, 60.0, ref clock) is not null, "Chaos takes point A");
        chaos.Position = new Position2(PointACentre().X + 5000.0, PointACentre().Z);
        channelling.Remove(chaos);
        var order = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[order] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock) is not null, "Order neutralises point A and takes it");
        TestAssert.True(chaos is { NodeCaptures: 1, NodeNeutralizes: 0 },
            $"the Chaos channeller captured once ({chaos.NodeCaptures} captures, {chaos.NodeNeutralizes} neutralisations)");
        TestAssert.True(standing is { NodeCaptures: 0, NodeNeutralizes: 0 }, "a standing champion counts nothing");
        TestAssert.True(order is { NodeCaptures: 1, NodeNeutralizes: 1 },
            $"the Order channeller neutralised once and captured once ({order.NodeCaptures} captures, {order.NodeNeutralizes} neutralisations)");

        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default, GameMaps.DominionMode);
        var packet = ClassicHeroStatsPackets.HeroStats(order.NetId, schema,
            Amberline.Network.Replication.ClassicHeroStatsSnapshot.Values(order, schema, now: 0.0));
        var values = ClassicHeroStatsChecks.DecodeClient(packet.Plaintext.Span, GameMaps.DominionMode);
        TestAssert.True((int)values["NODE_CAPTURE"] == 1 && (int)values["NODE_NEUTRALIZE"] == 1,
            "the map 8 0x4B carries NODE_CAPTURE 1 and NODE_NEUTRALIZE 1");
    }

    /// <summary>
    /// Batch 467: each credit goes to every client as the client's hero-stat increment, 0xE3 (the hero net
    /// id at +5, the stat index byte at +9 into the name table, 0 NODE_CAPTURE and 1 NODE_NEUTRALIZE; the client adds one to that
    /// stat of the hero's collection), so the scoreboard counts it at once instead of at the next 0x4B. 10 bytes. Batch 518: a standing champion
    /// of the team on the point gets the capture assist (8 NODE_CAPTURE_ASSIST) instead of none.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-capture-sends-the-hero-stat-increment")]
    public static void MapEightCaptureSendsTheHeroStatIncrement()
    {
        var sent = new List<byte[]>();
        var (host, world, channelling) = NewMapEightHost(packet => sent.Add(packet.Plaintext.ToArray()));
        var guardian = PointGuardian(world, PointACentre());
        var chaos = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        var standing = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        channelling[chaos] = guardian;
        var clock = 0.0;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Chaos, 60.0, ref clock) is not null, "Chaos takes point A");
        chaos.Position = new Position2(PointACentre().X + 5000.0, PointACentre().Z);
        channelling.Remove(chaos);
        var order = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[order] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock) is not null, "Order neutralises point A and takes it");
        var increments = sent.Where(bytes => bytes[0] == 0xE3)
            .Select(bytes => $"{bytes.Length}:{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)):X8}:{bytes[9]}").ToList();
        var expected = new[] { $"10:{chaos.NetId:X8}:0", $"10:{standing.NetId:X8}:8", $"10:{order.NetId:X8}:1", $"10:{order.NetId:X8}:0" };
        TestAssert.True(increments.SequenceEqual(expected),
            $"0xE3 NODE_CAPTURE for the Chaos channeller and NODE_CAPTURE_ASSIST for the standing one, then NODE_NEUTRALIZE and NODE_CAPTURE for the Order one (sent {string.Join(", ", increments)}; expected {string.Join(", ", expected)})");
    }

    /// <summary>
    /// Batch 456: the map 8 host with the channel seam. <c>channelling</c> names each champion's OdinCaptureChannel target (its guardian); a champion
    /// absent from it is not channelling. Batch 467: <paramref name="send"/> records the global capture packets.
    /// </summary>
    private static (BehaviorTreeQuestHost Host, WorldState World, Dictionary<Entity, Entity> Channelling) NewMapEightHost(
        Action<PlaintextPacket>? send = null, Action<Entity>? depart = null)
    {
        var world = new WorldState();
        var channelling = new Dictionary<Entity, Entity>();
        var hooks = new CapturePointHooks(
            Send: send ?? (_ => { }),
            Kill: (unit, _) => unit.Alive = false,
            AwardGold: (_, _) => true,
            CaptureChannelTarget: hero => channelling.GetValueOrDefault(hero),
            Depart: depart);
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (_, _) => { }, null,
            (skin, at, team) => world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
            {
                Name = skin,
                ChampionName = skin,
            }),
            hooks)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        return (host, world, channelling);
    }

    private static Entity MapEightChampion(WorldState world, Team team, Position2 at) =>
        new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, team, at) { Name = "Ryze", ChampionName = "Ryze" };

    /// <summary>
    /// Batch 456: a stand-in guardian at a point's centre, the unit a channel is aimed at (the batch 452 precedent). The guardian's position is
    /// what the rule reads: the channel counts when the guardian stands within the capture radius of the point.
    /// </summary>
    private static Entity PointGuardian(WorldState world, Position2 centre) =>
        world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, Team.Neutral, centre)
        {
            Name = "OdinNeutralGuardian",
            ChampionName = "OdinNeutralGuardian",
        });

    private static Position2 PointCentre(string letter)
    {
        var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
        var centre = SceneMesh.CentralPoint(Path.Combine(scene, "Info_Point" + letter + ".SCB"));
        return new Position2(centre.X, centre.Z);
    }

    private static Position2 PointACentre() => PointCentre("A");

    /// <summary>The game seconds until point <paramref name="point"/> is <paramref name="team"/>'s, ticked at the simulation step; null past the limit.</summary>
    private static double? SecondsUntilOwned(BehaviorTreeQuestHost host, int point, Team team, double limitSeconds, ref double clock)
    {
        const double step = 1.0 / 30.0;
        var start = clock;
        for (var tick = 1; tick <= (int)(limitSeconds / step); tick++)
        {
            clock += step;
            host.Tick(clock, step);
            if (host.CapturePointOwners[point] == team) return clock - start;
        }
        return null;
    }

    /// <summary>
    /// The Odin turrets' CharOnActivate (CharScriptOdinOrderTurretShrine, CharScriptOdinChaosTurretShrine) reads GetFlatAttackRangeMod at
    /// block .6 and adds its owner-only perception bubble (BBAddPosPerceptionBubble SpecificUnitsClientOnly, block .12). Both run to their
    /// end with no script failure (batch 452: the bubble is bound).
    /// </summary>
    [Check("scripting.odin-turret-character-scripts-run-to-their-end")]
    public static void OdinTurretCharacterScriptsRunToTheirEnd()
    {
        using var match = new BbM3.Match("Ryze");
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        var unitScripts = new BbUnitCharacterModule(match.Runtime.Spells.Data);
        var failures = new List<BbScriptFailure>();
        ((BbHookFanout)hooks.ScriptHooks!).Add(unitScripts.Bind(match.Runtime.Spells, failures.Add));
        unitScripts.SpawnEncounterUnit("OdinOrderTurretShrine", new Position2(7000.0, 7000.0), Team.Order);
        unitScripts.SpawnEncounterUnit("OdinChaosTurretShrine", new Position2(7300.0, 7000.0), Team.Chaos);
        match.Advance(1.0);
        TestAssert.True(failures.Count == 0,
            "the Odin turret character scripts run to their end with no script failure: " + string.Join("; ", failures));
    }

    /// <summary>Batch 452: a map 8 host. Batch 466: the host makes the point turret handles itself; the seam spawns the guardians.</summary>
    private static (BehaviorTreeQuestHost Host, WorldState World) NewMapEightTurretHost(Func<Entity, Entity?>? channelTarget = null)
    {
        var world = new WorldState();
        var hooks = new CapturePointHooks(
            Send: _ => { },
            Kill: (unit, _) => unit.Alive = false,
            AwardGold: (_, _) => true,
            CaptureChannelTarget: channelTarget);
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (_, _) => { }, null,
            (skin, at, team) => world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
                { Name = skin, ChampionName = skin }),
            hooks)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        return (host, world);
    }

    /// <summary>
    /// Batch 466 (replaces batch 452's scripting.odin-point-turret-fights-only-for-the-point-its-team-holds): the trees' OrderTurret and
    /// ChaosTurret are server-side handles. Each stands on its point with its team, hidden, and is not a world unit, so no client is sent it, no
    /// turret AI runs for it and it shoots nothing; the point's guardian is the attacker (the dominion.guardian-* checks). The trees still read
    /// and set them: GetTurret answers them, and a capture shows the owner's handle (SetUnitRendered, OdinScriptBehaviorTrees.xml:1320).
    /// </summary>
    [Check("scripting.odin-point-turrets-are-hidden-tree-handles-outside-the-world")]
    public static void OdinPointTurretsAreHiddenTreeHandlesOutsideTheWorld()
    {
        var channelling = new Dictionary<Entity, Entity>();
        var (host, world) = NewMapEightTurretHost(hero => channelling.GetValueOrDefault(hero));
        const double step = 1.0 / 30.0;
        var clock = 0.0;
        host.Tick(clock += step, step);
        for (var position = 2; position <= 6; position++)
        {
            var guardian = host.Context.PointGuardian(position);
            TestAssert.True(guardian is { ChampionName: "OdinNeutralGuardian" }, $"GetTurret position {position} keeps its point's guardian");
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                var handle = host.Context.Turret(team, 0, position);
                TestAssert.True(handle is { NoRender: true } && handle.Team == team && handle.Position == guardian!.Position,
                    $"the {team} handle of position {position} stands hidden on its point");
                TestAssert.True(world.Get(handle!.NetId) is null, $"the {team} handle of position {position} is not a world unit");
            }
        }

        var champion = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[champion] = host.Context.PointGuardian(2)!;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock) is not null, "Order takes point A by its channel");
        var order = host.Context.Turret(Team.Order, 0, 2)!;
        TestAssert.True(!order.NoRender && world.Get(order.NetId) is null, "the capture shows the Order handle, which stays out of the world");
    }

    /// <summary>
    /// Batch 466 (replaces batch 462's scripting.odin-point-turret-render-and-targetability-reach-the-clients): the trees' render and
    /// targetability changes land on the point's handles and send nothing, since no client holds a handle (the leaves send a unit's block only
    /// for a unit the clients hold as a minion, Entity.ReplicatedAsMinion).
    /// </summary>
    [Check("scripting.odin-point-turret-handles-take-render-and-targetability-and-send-nothing")]
    public static void OdinPointTurretHandlesTakeRenderAndTargetabilityAndSendNothing()
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var channelling = new Dictionary<Entity, Entity>();
        var sync = new Amberline.Simulation.Combat.ReplicationSyncCounter();
        var hooks = new CapturePointHooks(
            Send: packets.Add,
            Kill: (unit, _) => unit.Alive = false,
            AwardGold: (_, _) => true,
            CaptureChannelTarget: hero => channelling.GetValueOrDefault(hero),
            SyncId: sync.Next);
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (_, _) => { }, null,
            (skin, at, team) => world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
                { Name = skin, ChampionName = skin }),
            hooks)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        const double step = 1.0 / 30.0;
        var clock = 0.0;
        host.Tick(clock += step, step);
        var point = host.Captures[0];
        var orderTurret = (Entity)point.Variable("OrderTurret")!;
        var chaosTurret = (Entity)point.Variable("ChaosTurret")!;
        TestAssert.True(orderTurret.NoRender && chaosTurret.NoRender, "the point turret handles start hidden");

        var guardian = PointGuardian(world, PointACentre());
        var order = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[order] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock) is not null, "Order takes point A");
        TestAssert.True(!orderTurret.NoRender && orderTurret.NotTargetableToEnemy && !orderTurret.NotTargetableToAlly,
            "the capture shows the Order handle and makes it not targetable to Chaos");

        channelling.Remove(order);
        order.Alive = false;
        var chaos = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        channelling[chaos] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Neutral, 60.0, ref clock) is not null, "Chaos neutralises point A");
        TestAssert.True(orderTurret.NoRender && orderTurret.NotTargetableToEnemy && orderTurret.NotTargetableToAlly,
            "the neutralisation hides the Order handle and makes it untargetable");
        TestAssert.False(Blocks(packets, orderTurret).Any() || Blocks(packets, chaosTurret).Any(), "no state block names a handle");
    }

    // The single-entry minion blocks (map 0x02) the capture leaves send for a unit: (property mask, value).
    private static IEnumerable<(uint Mask, uint Value)> Blocks(IEnumerable<PlaintextPacket> packets, Entity unit) =>
        packets.Where(packet => packet.Plaintext.Span[0] == 0xCC && packet.Plaintext.Length == 23 &&
                                BitConverter.ToUInt32(packet.Plaintext.Span.Slice(11, 4)) == unit.NetId && packet.Plaintext.Span[10] == 0x02)
            .Select(packet => (BitConverter.ToUInt32(packet.Plaintext.Span.Slice(15, 4)), BitConverter.ToUInt32(packet.Plaintext.Span.Slice(19, 4))));

    /// <summary>
    /// Batch 456 (part 2): the point turrets' sight follows the shipped trees' IncPermanentPercentBubbleRadiusMod calls. The init sets -1 on
    /// every point turret (OdinScriptBehaviorTrees.xml:2932-2986), so a hidden turret sees nothing; the team that takes a point adds +1.5
    /// to its turret (:1326, :1404), so an owned turret sees 1350 * (1 + 0.5) = 2025; the team that loses it takes -1.5 back (:1486, :1527).
    /// Reconstruction: the radius is (1 + percent) * base, the sight formula of VisibilityRules.Scaled (the client's own formula).
    /// Batch 466: the turrets are server-side handles outside the world now, so this sight reaches no team; the check keeps the leaf's sum.
    /// </summary>
    [Check("scripting.odin-point-turret-vision-follows-the-capture-trees")]
    public static void OdinPointTurretVisionFollowsTheCaptureTrees()
    {
        var channelling = new Dictionary<Entity, Entity>();
        var (host, world) = NewMapEightTurretHost(hero => channelling.GetValueOrDefault(hero));
        const double step = 1.0 / 30.0;
        var clock = 0.0;
        host.Tick(clock += step, step);
        host.Tick(clock += step, step);
        var orderTurret = host.Context.Turret(Team.Order, 0, 2) ?? throw new InvalidOperationException("the Order turret of point A");
        var chaosTurret = host.Context.Turret(Team.Chaos, 0, 2) ?? throw new InvalidOperationException("the Chaos turret of point A");
        TestAssert.True(Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(orderTurret) == 0.0 &&
            Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(chaosTurret) == 0.0,
            "a neutral point's hidden turrets give no sight (the init's -1)");

        var guardian = PointGuardian(world, PointACentre());
        var champion = world.Register(MapEightChampion(world, Team.Order, PointACentre()));
        channelling[champion] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Order, 60.0, ref clock) is not null, "Order takes point A by its channel");
        TestAssert.True(Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(orderTurret) == 2025.0 &&
            Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(chaosTurret) == 0.0,
            "the owner's turret sees 1350 * 1.5 = 2025 and the other turret stays hidden");

        // Chaos takes the point back through neutral: the losing team's turret loses its +1.5 and is hidden (the Neutralization branch), then
        // Chaos's turret gains its own +1.5.
        channelling.Remove(champion);
        var chaos = world.Register(MapEightChampion(world, Team.Chaos, PointACentre()));
        channelling[chaos] = guardian;
        TestAssert.True(SecondsUntilOwned(host, 1, Team.Chaos, 90.0, ref clock) is not null, "Chaos takes point A back by its channel");
        TestAssert.True(Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(orderTurret) == 0.0 &&
            Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(chaosTurret) == 2025.0,
            "the turret of the team that lost the point is hidden again, and the new owner's turret sees 2025");
    }

    /// <summary>
    /// Batch 452: a point turret is named by its shrine character (not a lane tier), so its visibility body must read that record. Before, the
    /// tier fallback asked for (Outer, Order), which The Crystal Scar does not hold, and the map 8 match crashed on its first step.
    /// </summary>
    [Check("replication.odin-point-turret-body-reads-its-character-record")]
    public static void OdinPointTurretBodyReadsItsCharacterRecord()
    {
        var turret = new Entity(new WorldState().Allocate(UnitType.Turret), EntityKind.Turret, UnitType.Turret, Team.Order, new Position2(0.0, 0.0))
        {
            Name = "OdinOrderTurretShrine",
        };
        var (selection, gameplay) = Amberline.Network.Replication.VisibilityRules.TurretBody(turret);
        TestAssert.True(selection > 0.0 && gameplay > 0.0, "the point turret reads its shrine record's selection and collision radii");
    }

    [Check("scripting.behaviour-tree-encounter-guardian-takes-its-character-script")]
    public static void EncounterGuardianTakesItsCharacterScriptBuffs()
    {
        // Map 1's match stands in for map 8 (as batch 445's probe does); the guardian's record is read from the shipped data.
        using var match = new BbM3.Match("Ryze");
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        var unitScripts = new BbUnitCharacterModule(match.Runtime.Spells.Data);
        var failures = new List<BbScriptFailure>();
        ((BbHookFanout)hooks.ScriptHooks!).Add(unitScripts.Bind(match.Runtime.Spells, failures.Add));

        // The encounter's spawn path (the composition's seam): the squad unit gets its character script's CharOnActivate.
        var guard = unitScripts.SpawnEncounterUnit("OdinNeutralGuardian", new Position2(7150.0, 7000.0), Team.Neutral);
        match.Advance(1.0);
        TestAssert.True(guard is not null, "the guardian spawns through the unit-character module");
        TestAssert.True(failures.Count == 0, "its character script raised no failure: " + string.Join("; ", failures));
        TestAssert.True(guard!.Buffs.Any(buff => buff.Name == "OdinGuardianBuff"),
            "CharOnActivate of CharScriptOdinNeutralGuardian gives the guardian OdinGuardianBuff");
    }
}
