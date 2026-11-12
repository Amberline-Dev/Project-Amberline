using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 447: the Dominion score (DualUpdateScoreMath, shipped in LEVELS/Map8/Scripts/OdinScriptBehaviorTrees.xml) sends 0xDD and
/// drains the trailing team's nexus; a champion kill takes 2 nexus health; each nexus and spawn stands at its map 8 place.
/// </summary>
public static class DominionScoreChecks
{
    private const double Step = 1.0 / 30.0;

    [Check("dominion.score-sends-both-teams-at-start")]
    public static void ScoreSendsBothTeamsAtTheStart()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(0, 0), sink);
        for (var tick = 1; tick <= 60; tick++)
            score.Tick(tick * Step, Step);
        // Batch 450: 0xDD is each team's whole nexus health (500 at the start, RecordingScoreSink's nexus), not the tree's total.
        TestAssert.True(sink.Scores.Contains((Team.Order, 500)) && sink.Scores.Contains((Team.Chaos, 500)),
            "0xDD carries both teams' whole nexus health (500) at the start of the game");
        TestAssert.Equal(2, sink.Scores.Count, "with no control the score does not change, so only the start is sent");
    }

    [Check("dominion.score-tie-drains-nothing")]
    public static void TiedControlCountsDrainNothing()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(2, 2), sink);
        for (var tick = 1; tick <= 600; tick++)
            score.Tick(tick * Step, Step);
        TestAssert.Equal(0, sink.Drains.Count, "a tie (2 points each) damages neither nexus");
        TestAssert.True(score.Total(Team.Order) > 0.0 && score.Total(Team.Order) == score.Total(Team.Chaos),
            "both teams still gain their points while tied");
    }

    [Check("dominion.score-leader-drains-trailing-nexus-to-zero")]
    public static void LeaderDrainsTheTrailingNexusToZeroAndOrderWins()
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var order = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var chaos = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Chaos, new Position2(12163.1846, 4568.9819)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Chaos_Crystal" });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
                ?? throw new InvalidOperationException("map 8 has a quest host");
            var score = host.AttachScore(new FixedControl(3, 1), new CombatScoreSink(combat));

            // Order holds 3 points against Chaos's 1: 3-1-1 takes 1.5 from Chaos's nexus every 2 s (DrainAmount(3, 1), DrainIntervalSeconds),
            // so it reaches 0 after 334 applications, about 668 s (batch 505).
            var tick = 0;
            while (!combat.GameOver && tick < 30000)
                score.Tick(++tick * Step, Step);

            TestAssert.True(combat.GameOver, "the drained nexus ends the match");
            TestAssert.Near(0.0, chaos.Health, 1e-9, "Chaos's nexus reaches 0 health");
            TestAssert.False(chaos.Alive, "the nexus death is the shipped building-death path");
            TestAssert.Near(500.0, order.Health, 1e-9, "Order's nexus is not drained while Order leads");
            TestAssert.Equal(Team.Order, combat.Winner, "the other team wins");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.score-meter-follows-nexus-health")]
    public static void TheMeterCountsEachNexusDownByWholePoints()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(3, 1), sink);
        for (var tick = 1; tick <= 1830; tick++)
            score.Tick(tick * Step, Step);
        var chaos = sink.Scores.Where(entry => entry.Team == Team.Chaos).Select(entry => entry.Score).ToList();
        TestAssert.True(chaos.Count > 1 && chaos.SequenceEqual(chaos.OrderByDescending(value => value)) && chaos.Distinct().Count() == chaos.Count,
            "the trailing meter counts down, each whole value sent once (batch 450: 0xDD is nexus health)");
        TestAssert.True(chaos[^1] == 455, $"61 s at 1.5 per 2 s (3-1-1, batch 505: thirty applications) leaves Chaos's nexus at 455 on the meter ({chaos[^1]})");
        TestAssert.True(sink.Scores.All(entry => entry.Team != Team.Order || entry.Score == 500), "the leading team's meter stays at 500");
    }

    // Batch 505: the drain per tick is the forum post's table (15 February 2012, "You-Neutral-Enemy: nexus reduction per tick"), by both
    // teams' counts; 2-3-0 is the Reconstruction 1.5 (DominionScore.DrainAmount). A tie drains nothing (wiki, Dominion: "When both teams have the
    // same amount of capture points, neither nexus will be damaged"). Steps are 1/30 s; one application falls in the first 2.5 s (DrainIntervalSeconds).
    [Check("dominion.drain-follows-the-control-configuration")]
    public static void DrainFollowsTheControlConfiguration()
    {
        foreach (var (order, chaos, expected) in new[]
                 {
                     (1, 0, 1.0), (2, 0, 1.5), (2, 1, 1.0), (3, 0, 2.0), (3, 1, 1.5), (3, 2, 1.0), (4, 0, 3.0), (4, 1, 2.5), (5, 0, 5.0),
                     (0, 0, 0.0), (1, 1, 0.0), (2, 2, 0.0), (1, 4, 2.5),
                 })
            TestAssert.Near(expected, NexusLossAfter(order, chaos, 75), 1e-6,
                $"{order}-{5 - order - chaos}-{chaos} drains {expected} once per tick (the trailing team's nexus)");
    }

    // Batch 505 (two-second tick, Gamerevolution 4 August 2011: "every two seconds"): the drain is applied once per tick, not every step.
    // Before the first tick nothing is lost; the second tick takes a second application.
    [Check("dominion.drain-applies-once-every-two-seconds")]
    public static void DrainAppliesOnceEveryTwoSeconds()
    {
        TestAssert.Near(0.0, NexusLossAfter(2, 1, 45), 1e-6, "no drain before the first two seconds (1.5 s)");
        TestAssert.Near(1.0, NexusLossAfter(2, 1, 75), 1e-6, "one application at two seconds (2.5 s)");
        TestAssert.Near(2.0, NexusLossAfter(2, 1, 135), 1e-6, "two applications by 4.5 s");
    }

    /// <summary>Batch 478b: the nexus health the trailing team loses in the first <paramref name="steps"/> steps of 1/30 s under a fixed control split.</summary>
    private static double NexusLossAfter(int order, int chaos, int steps)
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Chaos, new Position2(12163.1846, 4568.9819)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Chaos_Crystal" });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
                ?? throw new InvalidOperationException("map 8 has a quest host");
            var score = host.AttachScore(new FixedControl(order, chaos), new CombatScoreSink(combat));
            for (var tick = 1; tick <= steps; tick++)
                score.Tick(tick * Step, Step);
            var trailing = order > chaos ? Team.Chaos : Team.Order;
            return 500.0 - combat.DominionNexusHealth(trailing);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // Batch 478b: a champion kill takes nexus health only down to the floor. The wiki (Dominion, Patch History V1.0.0.127): "Lowered the point
    // where Nexus health will only be affected by holding capture points (and not by Champion Kills or Neutralizes) to 100 Nexus health from
    // 125 Nexus health." So the 1.0.0.126 floor for kills is 125.
    [Check("dominion.champion-kill-stops-at-the-125-floor")]
    public static void ChampionKillStopsAtTheFloor()
    {
        TestAssert.Near(125.0, KillAtNexusHealth(126.0), 1e-9, "a kill at 126 takes the nexus down to the 125 floor and no further");
        TestAssert.Near(125.0, KillAtNexusHealth(125.0), 1e-9, "a kill at the floor takes nothing");
    }

    private static double KillAtNexusHealth(double health)
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = health, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            combat.Kill(hero);
            return nexus.Health;
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.champion-kill-takes-two-nexus-health")]
    public static void ChampionKillTakesTwoFromTheVictimTeamsNexus()
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            combat.Kill(hero);
            TestAssert.Near(498.0, nexus.Health, 1e-9, "a champion kill takes 2 from the victim team's nexus (wiki: Dominion)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.nexuses-and-spawns-stand-at-map-8-bases")]
    public static void MapEightNexusesAndSpawnsStandAtTheirShippedPlaces()
    {
        ConfigureMapEight();
        try
        {
            var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
            var orderCrystal = SceneMesh.CentralPoint(Path.Combine(scene, "LevelProp_Odin_SOG_Order_Crystal.SCB"));
            var chaosCrystal = SceneMesh.CentralPoint(Path.Combine(scene, "LevelProp_Odin_SOG_Chaos_Crystal.SCB"));
            var orderSpawn = SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T1.SCB"));
            var chaosSpawn = SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T2.SCB"));

            var nexuses = MapDefinitions.Nexuses;
            TestAssert.Equal(2, nexuses.Count, "map 8 has one nexus per team");
            var order = nexuses.Single(nexus => nexus.Team == Team.Order);
            var chaos = nexuses.Single(nexus => nexus.Team == Team.Chaos);
            TestAssert.Near(orderCrystal.X, order.Position.X, 1e-3, "Order nexus X is the crystal prop's centre");
            TestAssert.Near(orderCrystal.Z, order.Position.Z, 1e-3, "Order nexus Z is the crystal prop's centre");
            TestAssert.Near(chaosCrystal.X, chaos.Position.X, 1e-3, "Chaos nexus X is the crystal prop's centre");
            TestAssert.Near(chaosCrystal.Z, chaos.Position.Z, 1e-3, "Chaos nexus Z is the crystal prop's centre");
            TestAssert.Near(500.0, order.MaxHealth, 1e-9, "a nexus starts at 500 health (wiki: Crystal Scar)");
            TestAssert.Near(500.0, chaos.MaxHealth, 1e-9, "a nexus starts at 500 health (wiki: Crystal Scar)");
            TestAssert.Near(0.0, order.HealthRegen, 1e-9, "no regeneration: it would cancel the drain (Reconstruction)");
            TestAssert.Equal(MapDefinitions.BuildingNetId("LevelProp_Odin_SOG_Order_Crystal"), order.NetId, "netid from the placement name");

            TestAssert.Near(orderSpawn.X, MapDefinitions.TeamSpawn(Team.Order).X, 1e-3, "Order spawn is __Spawn_T1 (Locations.dat OrderStartPoint)");
            TestAssert.Near(orderSpawn.Z, MapDefinitions.TeamSpawn(Team.Order).Z, 1e-3, "Order spawn is __Spawn_T1 (Locations.dat OrderStartPoint)");
            TestAssert.Near(chaosSpawn.X, MapDefinitions.TeamSpawn(Team.Chaos).X, 1e-3, "Chaos spawn is __Spawn_T2 (Locations.dat ChaosStartPoint)");
            TestAssert.Near(chaosSpawn.Z, MapDefinitions.TeamSpawn(Team.Chaos).Z, 1e-3, "Chaos spawn is __Spawn_T2 (Locations.dat ChaosStartPoint)");
            // The first Chaos hero of a one-champion team takes the shipped slot offset (HeroSpawnOffsets: Chaos1 is -10, 0) from the base.
            var initial = MapDefinitions.InitialHeroSpawn(Team.Chaos, 1, 0).Position;
            TestAssert.Near(chaosSpawn.X - 10.0, initial.X, 1e-3, "the first Chaos hero spawns at the map 8 base plus its slot offset");
            TestAssert.Near(chaosSpawn.Z, initial.Z, 1e-3, "the first Chaos hero's Z is the map 8 base's Z (slot offset 0)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.summoners-rift-spawns-unchanged")]
    public static void SummonersRiftSpawnsAreUnchanged()
    {
        GameData.ConfigureDefault(GameDataPaths.Default);
        try
        {
            TestAssert.Near(MapDefinitions.HeroSpawn.X, MapDefinitions.TeamSpawn(Team.Order).X, 1e-9, "Map 1 Order spawn is HeroSpawn");
            TestAssert.Near(13933.7890625, MapDefinitions.TeamSpawn(Team.Chaos).X, 1e-9, "Map 1 Chaos spawn X");
            TestAssert.Near(14170.09375, MapDefinitions.TeamSpawn(Team.Chaos).Z, 1e-9, "Map 1 Chaos spawn Z");
            TestAssert.Equal(2, MapDefinitions.Nexuses.Count, "Summoner's Rift keeps its two HQs");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.hero-respawns-at-its-map-8-base")]
    public static void HeroRespawnsAtItsOwnMapEightBase()
    {
        ConfigureMapEight();
        try
        {
            var data = GameData.Default;
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 0, MaxHealth = 1000, Alive = false, RespawnAt = 10.0 });
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths), new CombatChecks.LifecycleStatsHooks(),
                new CombatChecks.LifecycleStatsTransport());
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
                new SystemRandomSource(), stats, data);
            TestAssert.True(combat.RespawnImmediately(hero), "the hero respawns");
            var spawn = MapDefinitions.TeamSpawn(Team.Chaos);
            TestAssert.Near(spawn.X, hero.Position.X, 1e-6, "a respawned Chaos hero stands at the Chaos base on map 8 (X)");
            TestAssert.Near(spawn.Z, hero.Position.Z, 1e-6, "a respawned Chaos hero stands at the Chaos base on map 8 (Z)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>
    /// Batch 747 (Reconstruction, DominionScore.PlayMusicPhase): the decoded 0xE5 is 10 bytes, the opcode, a hero's net id (the stock client routes
    /// the packet to a hero), the command at 5 (1 = BeginMusicCue) and the FMOD cue at 6. Nothing goes out at the start: the client begins its
    /// own phase 1 theme (Map_CrystalScar, cue 13) on StartGame. As the lower nexus of either team passes 375, 250 and 125, every client gets one
    /// Begin of Global_Phase2, 3 and 4 (cues 15, 16, 17 of LoL_Audio.fev), once each; a fallen nexus sends none, since the client plays its own
    /// victory or defeat music on EndGame.
    /// </summary>
    [Check("dominion.music-phase-follows-the-lower-nexus")]
    public static void MusicPhaseFollowsTheLowerNexus()
    {
        var (_, hero, packets, sink, host) = MusicMatch();
        var score = host.Score!;
        var tick = 0;
        void Run() { for (var step = 0; step < 30; step++) score.Tick(++tick * Step, Step); }
        List<byte[]> Music() => packets.Where(packet => packet.Plaintext.Span[0] == 0xE5).Select(packet => packet.Plaintext.ToArray()).ToList();
        void Phases(string what, params uint[] cues) =>
            TestAssert.True(Music().Select(packet => Convert.ToHexString(packet))
                    .SequenceEqual(cues.Select(cue => Convert.ToHexString(MusicBegin(hero.NetId, cue)))),
                $"{what}: expected Begin of cues [{string.Join(", ", cues)}], got [{string.Join(", ", Music().Select(Convert.ToHexString))}]");

        Run();
        Phases("the start sends nothing (the client starts its own theme)");
        sink.DamageNexus(Team.Order, 120.0);
        Run();
        Phases("380 is still phase 1");
        sink.DamageNexus(Team.Chaos, 125.0);
        Run();
        Phases("Chaos at 375: phase 2", 15);
        var first = packets.First(packet => packet.Plaintext.Span[0] == 0xE5);
        TestAssert.True(first.Channel == 3 && first.Reliability == LeaguePacketReliability.Reliable,
            $"0xE5 goes reliable on the S2C channel 3 (channel {first.Channel}, {first.Reliability})");
        sink.DamageNexus(Team.Chaos, 25.0);
        Run();
        Phases("Chaos at 350 stays in phase 2", 15);
        sink.DamageNexus(Team.Order, 140.0);
        Run();
        Phases("Order at 240 is now the lower nexus: phase 3", 15, 16);
        sink.DamageNexus(Team.Chaos, 300.0);
        Run();
        Phases("Chaos at 50: phase 4", 15, 16, 17);
        sink.DamageNexus(Team.Chaos, 50.0);
        Run();
        Phases("the fallen nexus sends nothing more", 15, 16, 17);
    }

    /// <summary>
    /// Batch 747: a returning client (BehaviorTreeQuestHost.ReconnectState) gets the current music phase as one Begin addressed to its own hero,
    /// and none while the match is still in phase 1. Two thresholds passed in one step send only the deeper phase.
    /// </summary>
    [Check("dominion.reconnecting-client-gets-the-current-music-phase")]
    public static void ReconnectingClientGetsTheCurrentMusicPhase()
    {
        var (world, hero, packets, sink, host) = MusicMatch();
        var returning = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(13000, 4500))
            { Name = "Annie", ChampionName = "Annie" });
        var known = new HashSet<uint> { hero.NetId, returning.NetId };
        List<byte[]> Music(IEnumerable<PlaintextPacket> stream) =>
            stream.Where(packet => packet.Plaintext.Span[0] == 0xE5).Select(packet => packet.Plaintext.ToArray()).ToList();
        var tick = 0;
        void Run() { for (var step = 0; step < 30; step++) host.Score!.Tick(++tick * Step, Step); }

        Run();
        TestAssert.Equal(0, Music(host.ReconnectState(returning.NetId, Team.Chaos, known)).Count,
            "in phase 1 the returning client gets no 0xE5 (its own StartGame began the theme)");
        sink.DamageNexus(Team.Chaos, 260.0);
        Run();
        var live = Music(packets);
        TestAssert.True(live.Count == 1 && live[0].AsSpan().SequenceEqual(MusicBegin(world.Heroes()[0].NetId, 16)),
            $"500 to 240 in one step sends only phase 3 (cue 16), got {live.Count} packets");
        var burst = Music(host.ReconnectState(returning.NetId, Team.Chaos, known));
        TestAssert.True(burst.Count == 1 && burst[0].AsSpan().SequenceEqual(MusicBegin(returning.NetId, 16)),
            $"the returning client gets one Begin of the current phase (cue 16) addressed to its own hero, got {burst.Count}");
    }

    // Batch 747: map 8's score with a packet sender, one Order champion and a recorded nexus pair (500 each).
    private static (WorldState World, Entity Hero, List<PlaintextPacket> Packets, RecordingScoreSink Sink, BehaviorTreeQuestHost Host) MusicMatch()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(1000, 4500))
            { Name = "Annie", ChampionName = "Annie" });
        var packets = new List<PlaintextPacket>();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world, hooks: new CapturePointHooks(Send: packets.Add))
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var sink = new RecordingScoreSink();
        host.AttachScore(new FixedControl(0, 0), sink);
        return (world, hero, packets, sink, host);
    }

    // The decoded 0xE5 Begin, written byte by byte (not through the builder).
    private static byte[] MusicBegin(uint heroNetId, uint cue)
    {
        var bytes = new byte[10];
        bytes[0] = 0xE5;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), heroNetId);
        bytes[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), cue);
        return bytes;
    }

    private static void ConfigureMapEight() =>
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));

    /// <summary>Records what the score trees send: 0xDD team scores and nexus drains.</summary>
    internal sealed class RecordingScoreSink : IDominionScoreSink
    {
        public List<(Team Team, int Score)> Scores { get; } = new();
        public List<(Team Team, double Amount)> Drains { get; } = new();
        private readonly Dictionary<Team, double> _nexus = new() { [Team.Order] = 500.0, [Team.Chaos] = 500.0 };
        public void SetGameScore(Team team, int score) => Scores.Add((team, score));
        public double NexusHealth(Team team) => _nexus[team];
        public void DamageNexus(Team team, double amount)
        {
            Drains.Add((team, amount));
            _nexus[team] = Math.Max(0.0, _nexus[team] - amount);
        }
    }

    /// <summary>The score's nexus drain goes through the combat service, so the nexus death reaches the shipped FinishMatch path.</summary>
    internal sealed class CombatScoreSink(CombatService combat) : IDominionScoreSink
    {
        public void SetGameScore(Team team, int score) { }
        public double NexusHealth(Team team) => combat.DominionNexusHealth(team);
        public void DamageNexus(Team team, double amount) => combat.DamageDominionNexus(team, amount);
    }

    /// <summary>Fixed control counts (batch 446's capture points replace this seed).</summary>
    internal sealed class FixedControl(int order, int chaos) : IDominionControl
    {
        public int ControlCount(Team team) => team == Team.Order ? order : chaos;
    }
}
