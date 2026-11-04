namespace Amberline.Network.Diagnostics;

/// <summary>
/// DIAGNOSTICS (batch 476, the owner's native test of the centre crystal's visibility): with <c>AMBERLINE_ODIN_NO_RELIC_BUBBLE=1</c> the
/// server withholds the 0x26 AddUnitPerceptionBubble packet of an OdinCenterRelic unit, at each of the three places that send it, and nothing
/// else. The bubble is still spawned and the server's sight is unchanged (<c>WorldState.TimedVisionBubbles</c>). It was on by default from
/// 29 October to 4 November; the owner then ended the experiment (batch 683: without the bubble the client may treat the centre as fogged),
/// so it is off unless the variable is 1, and the packet is sent as the original does. It is a test seam: it changes no production gameplay
/// and can be removed when the project is finished.
/// </summary>
public static class OdinRelicBubbleGate
{
    public const string EnvironmentVariable = "AMBERLINE_ODIN_NO_RELIC_BUBBLE";

    /// <summary>The centre crystal's character (OdinCenterRelic.inibin, skin OdinCenterRelic).</summary>
    public const string CenterRelicName = "OdinCenterRelic";

    /// <summary>True when the 0x26 packet of a unit of this character must not be sent.</summary>
    public static bool Withholds(string? championName) =>
        championName == CenterRelicName && Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
}
