using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Server;

namespace Amberline.Tests.Network;

/// <summary>
/// Dominion (The Crystal Scar, map 8) at the server's selectors: the SynchVersion map id and mode string the client reads
/// (batch 441 research section 4), and the map files and <c>--map-id</c> option the server reads for it.
/// </summary>
public static class DominionMapPacketChecks
{
    [Check("server.dominion-map-8-synch-version-carries-the-id-and-odin-mode")]
    public static void SynchVersionWritesTheCrystalScarIdAndTheDominionMode()
    {
        var dominion = LoadingPackets.SynchVersionS2C(mapId: 8);
        TestAssert.Equal(8, MapIdOf(dominion), "SynchVersion map id");
        // The stock AIR client creates map 8 with GameMode.DOMINION = "ODIN" (batch 441 section 5.1).
        TestAssert.Equal("ODIN", ModeOf(dominion), "the mode string of map 8");
        TestAssert.Equal("CLASSIC", ModeOf(LoadingPackets.SynchVersionS2C()), "Summoner's Rift keeps CLASSIC");
        TestAssert.Equal("CLASSIC", ModeOf(LoadingPackets.SynchVersionS2C(mapId: 2)), "the Harrowing rift keeps CLASSIC");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => LoadingPackets.SynchVersionS2C(mapId: 9));
    }

    /// <summary>
    /// Batch 669 (next to the map 8 selectors for their decoders): Battle Training is map 1 in mode TUTORIAL, the mode whose loading screen
    /// is Battle Training's (TutorialModeSettings.ini). The server takes it as <c>--game-mode</c>; a map keeps its own mode otherwise, and a
    /// mode the map is not played in is refused.
    /// </summary>
    [Check("server.battle-training-synch-version-carries-map-1-and-the-tutorial-mode")]
    public static void BattleTrainingIsSummonersRiftInTutorialMode()
    {
        var training = LoadingPackets.SynchVersionS2C(mapId: GameMaps.Normal, mode: GameMaps.TutorialMode);
        TestAssert.Equal(1, MapIdOf(training), "Summoner's Rift");
        TestAssert.Equal("TUTORIAL", ModeOf(training));
        // Batch 674: the Basic Tutorial, TUTORIAL at the Platform too, tells its client CLASSIC, so the client reads Map3's own loading screen
        // (Mission.ini LoadScreen "Tutorial", TutorialSettings.ini) and not Battle Training's (GameMaps.ClientModeOf).
        TestAssert.Equal("CLASSIC", ModeOf(LoadingPackets.SynchVersionS2C(mapId: GameMaps.Tutorial, mode: GameMaps.TutorialMode)),
            "the Basic Tutorial's client mode");
        TestAssert.Equal("TUTORIAL", ServerOptions.Parse(["--map-id", "1", "--game-mode", "TUTORIAL"]).GameMode);
        TestAssert.True(ServerOptions.Parse(["--map-id", "1"]).GameMode is null, "no --game-mode: the map's own");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ServerOptions.Parse(["--map-id", "4", "--game-mode", "TUTORIAL"]));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ServerOptions.Parse(["--map-id", "1", "--game-mode", "ODIN"]));
        TestAssert.True(GameMaps.IsPlayedIn(GameMaps.Tutorial, GameMaps.TutorialMode) && GameMaps.IsPlayedIn(GameMaps.CrystalScar, GameMaps.DominionMode)
            && !GameMaps.IsPlayedIn(GameMaps.CrystalScar, GameMaps.ClassicMode), "every other map plays only its own mode");

        // The production match the Platform starts (PlatformGames.CreateBattleTraining): the player's Ryze and the nine bots, composed on
        // Summoner's Rift in TUTORIAL, whose SynchVersion carries the mode (the server's Program path needs the ENet bridge).
        var champions = new[] { "Ryze", "MasterYi", "Soraka", "Shen", "Sivir", "Malphite", "Sivir", "Nasus", "Annie", "MasterYi" };
        var roster = champions.Select((champion, seat) => new Amberline.Network.Sessions.SessionParticipantDefinition((ulong)seat + 1, (uint)seat,
            seat == 0 ? "Trainee" : $"{GameData.Default.BotName(champion)} {seat}", champion, seat < 5 ? Team.Order : Team.Chaos,
            "SummonerHeal", "SummonerFlash", IsBot: seat != 0)).ToArray();
        var runtime = Amberline.Network.Sessions.MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0,
            roster: roster, gameMode: GameMaps.TutorialMode);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.Receive(1, 0, runtime.Session.BuildKeyCheck(1));
        var version = runtime.Session.Receive(1, 1, [0xC5]).Select(item => item.Packet).Single(packet => packet.Plaintext.Span[0] == 0x57);
        TestAssert.Equal(1, MapIdOf(version), "the match is Summoner's Rift");
        TestAssert.Equal("TUTORIAL", ModeOf(version), "in Battle Training's mode");
    }

    [Check("server.dominion-map-8-level-files-and-map-option")]
    public static void MapEightReadsItsOwnFilesAndTheServerAcceptsIt()
    {
        TestAssert.Equal("Map8", GameDataPaths.MapFolder(8));
        TestAssert.Equal("Map8", GameDataPaths.Default.ForMap(8).MapName);
        TestAssert.Equal(8, ServerOptions.Parse(["--map-id", "8"]).MapId);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ServerOptions.Parse(["--map-id", "9"]));
    }

    /// <summary>
    /// Batch 457, the owner's native Dominion game (28 October 2026): the live server sent Summoner's Rift's 24 turret names and spawned the
    /// hero at Summoner's Rift's fountain, because only its validate-only path set the process map. The client binds a <c>CreateTurret</c>
    /// (0xA5) by name to a turret of its own scene (the client compares the packet's name with each scene turret's
    /// <c>&lt;object&gt;_A</c>); an unknown name asserts <c>turret</c> and leaves the turret unbound, which draws its
    /// destroyed skin. Its <c>IsValidCell</c> is the navgrid's bounds, which the Summoner's Rift fountain lies outside.
    /// The server program's own entry runs here (the run path, not --validate-only): <c>--launcher-control</c> reads the launcher's
    /// <c>stop</c> from stdin, so the program composes the match and ends. The statics are process-wide, which a server.* check owns.
    /// </summary>
    [Check("server.dominion-program-on-map-8-composes-map-8-turrets-crystals-and-spawns", Serial = true)]
    public static void TheServerProgramOnMapEightComposesMapEightsStatics()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-map8-" + Guid.NewGuid().ToString("N"));
        var input = Console.In;
        try
        {
            Console.SetIn(new StringReader("stop\n"));
            var exit = Amberline.Server.Program.Main(["--solo", "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8",
                "--map-id", "8", "--launcher-control", "--log-dir", root]).GetAwaiter().GetResult();
            TestAssert.Equal(0, exit, "the server program composes the map 8 match and stops");
            var paths = GameData.Default.Paths;
            TestAssert.Equal("Map8", paths.MapName, "the program's match reads map 8's files");
            var scene = Path.Combine(paths.Root, "LEVELS", paths.MapName, "Scene");
            var turrets = MapDefinitions.Turrets;
            TestAssert.Equal(4, turrets.Count, "map 8's CreateTurret burst names its four shrine turrets, no Turret_T1_* or Turret_T2_*");
            foreach (var turret in turrets)
                TestAssert.True(File.Exists(Path.Combine(scene, turret.Name + ".SCB")),
                    $"CreateTurret {turret.Name}_A names no turret of map 8's scene");
            TestAssert.True(MapDefinitions.Nexuses.Count == 2 &&
                MapDefinitions.Nexuses.All(nexus => nexus.Name.StartsWith("LevelProp_Odin_SOG_", StringComparison.Ordinal)),
                "map 8's nexuses are its two Odin crystals");
            var grid = NavigationGrid.Load(paths);
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                TestAssert.True(grid.IsInBounds(grid.WorldToCell(MapDefinitions.TeamSpawn(team))), $"{team} spawn inside map 8's grid");
                for (var size = 1; size <= 5; size++)
                    for (var slot = 0; slot < size; slot++)
                    {
                        var at = MapDefinitions.InitialHeroSpawn(team, size, slot).Position;
                        TestAssert.True(grid.IsInBounds(grid.WorldToCell(at)), $"{team} hero slot {slot} of {size} at {at} inside map 8's grid");
                    }
            }
        }
        finally
        {
            Console.SetIn(input);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int MapIdOf(PlaintextPacket packet)
    {
        // Byte 0 is the opcode (0x57), bytes 6..9 the little-endian int the client reads as the map id.
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal((byte)0x57, bytes[0], "SynchVersionS2C opcode");
        return BitConverter.ToInt32(bytes.Slice(6, 4));
    }

    private static string ModeOf(PlaintextPacket packet)
    {
        // The 128-byte fixed ASCII mode string is the packet's last field (1438 bytes in all).
        var bytes = packet.Plaintext.Span;
        return Encoding.ASCII.GetString(bytes.Slice(bytes.Length - 128, 128)).TrimEnd('\0');
    }
}
