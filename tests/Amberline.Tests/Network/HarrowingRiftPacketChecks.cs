using Amberline.Network.Protocol;
using Amberline.Server;

namespace Amberline.Tests.Network;

/// <summary>
/// The Harrowing rift's selector on the server: the SynchVersion map id the client loads LEVELS\Map&lt;id&gt; from,
/// and the <c>--map-id</c> option the Host passes to each server it starts.
/// </summary>
public static class HarrowingRiftPacketChecks
{
    [Check("server.harrowing-rift-synch-version-carries-the-map-id")]
    public static void SynchVersionWritesTheMapTheClientLoadsAndRefusesOtherMaps()
    {
        // Byte 0 is the opcode (0x57), bytes 6..9 the little-endian int the client reads as the map id.
        TestAssert.Equal(1, MapIdOf(LoadingPackets.SynchVersionS2C()));
        TestAssert.Equal(2, MapIdOf(LoadingPackets.SynchVersionS2C(mapId: 2)));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => LoadingPackets.SynchVersionS2C(mapId: 5));
    }

    [Check("server.harrowing-rift-map-option-is-one-or-two")]
    public static void MapIdDefaultsToSummonersRiftAndTheServerAcceptsOnlyTheTwoMaps()
    {
        TestAssert.Equal(1, ServerOptions.Parse(["--map-id", "1"]).MapId);
        TestAssert.Equal(2, ServerOptions.Parse(["--map-id", "2"]).MapId);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ServerOptions.Parse(["--map-id", "5"]));
    }

    [Check("server.harrowing-rift-reads-its-own-level-files")]
    public static void TheHarrowingRiftReadsItsOwnLevelFilesAndSummonersRiftStillReadsMap1()
    {
        var map2 = Amberline.Data.GameDataPaths.Default.ForMap(2);
        TestAssert.Equal("Map2", map2.MapName);
        TestAssert.True(Amberline.Core.MapDefinitions.NavigationGridPath(map2).EndsWith(Path.Combine("LEVELS", "Map2", "AIPath.aimesh_ngrid")),
            "the navigation grid of the Harrowing rift is LEVELS/Map2/AIPath.aimesh_ngrid");
        // Constants.var: dr_BuildingToHero is 1.000 on the Harrowing rift (LEVELS/Map2) and 1.050 on Summoner's Rift (LEVELS/Map1).
        TestAssert.Near(1.0, new Amberline.Data.GameData(map2).LevelConstants.DamageRatios.BuildingToHero, 1e-9, "Map2 Constants.var");
        TestAssert.Near(1.05, new Amberline.Data.GameData(Amberline.Data.GameDataPaths.Default.ForMap(1)).LevelConstants.DamageRatios.BuildingToHero,
            1e-9, "Map1 Constants.var");
    }

    private static int MapIdOf(PlaintextPacket packet)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal((byte)0x57, bytes[0], "SynchVersionS2C opcode");
        return BitConverter.ToInt32(bytes.Slice(6, 4));
    }
}
