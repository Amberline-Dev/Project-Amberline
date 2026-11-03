using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;

namespace Amberline.Tests;

/// <summary>
/// Batch 455: SummonerOdinGarrison, Dominion's summoner (AIR spell id 17). The shipped script clears OdinCaptureChannel from the
/// units in range (batch 449). The loadout names it, the Platform offers it on map 8 only, and maps 1 and 2 keep their list.
/// </summary>
public static class DominionSummonerChecks
{
    [Check("dominion.garrison-summoner-is-offered-on-map-8-only")]
    public static void GarrisonIsOfferedOnMapEightOnly()
    {
        TestAssert.True(SummonerLoadout.CanonicalFromDisplay("Garrison") == "SummonerOdinGarrison",
            "the loadout names Garrison by its display name");
        TestAssert.True(SummonerLoadout.DisplayName("SummonerOdinGarrison") == "Garrison", "its display name is Garrison");
        TestAssert.True(AirGameDtos.SpellId("SummonerOdinGarrison") == 17, "the AIR spell id is 17");
        TestAssert.True(AirGameDtos.SpellNamesFor(GameMaps.CrystalScar).ContainsKey(17), "map 8 offers the Dominion summoner");
        // Maps 1 and 2 (Summoner's Rift and the Harrowing Rift) keep the CLASSIC list: no id 17.
        TestAssert.True(!AirGameDtos.SpellNamesFor(1).ContainsKey(17) && !AirGameDtos.SpellNamesFor(2).ContainsKey(17),
            "maps 1 and 2 do not offer the Dominion summoner");
    }

    [Check("dominion.garrison-shipped-script-clears-the-capture-channel")]
    public static void GarrisonScriptNamesTheCaptureChannelItClears()
    {
        var path = Path.Combine(GameDataPaths.Default.Root, "DATA", "Spells", "SummonerOdinGarrison.luaobj");
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        TestAssert.True(text.Contains("OdinCaptureChannel"), "the shipped script names the capture channel it clears");
        TestAssert.True(text.Contains("BBSpellBuffClear"), "the shipped script clears buffs (BBSpellBuffClear)");
    }
}
