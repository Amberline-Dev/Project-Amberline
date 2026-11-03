namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// The opening ceremony's release time (the Dominion game start).
/// <para>
/// NOT RETAIL: the shipped ceremony releases the champions when the game time passes <see cref="ShippedReleaseSeconds"/>
/// (<c>LEVELS/Map8/Scripts/OdinScriptBehaviorTrees.xml</c>, tree <c>OdinOpeningCeremony</c>, the <c>GreaterFloat</c> node on
/// <c>CurrentTime</c> with <c>RightHandSide</c> 45, line 4746). The owner's correction of 29 October 2026, "game should start at 1:20
/// not 0:45", moves the release to <see cref="ReleaseSeconds"/>. The shipped file on disk is not edited: the loader applies the
/// threshold when it reads the map's tree (<see cref="ReleaseOverride"/>).
/// </para>
/// </summary>
public static class OpeningCeremonyTiming
{
    /// <summary>The shipped release, as the tree states it (OdinScriptBehaviorTrees.xml:4746).</summary>
    public const double ShippedReleaseSeconds = 45.0;

    /// <summary>NOT RETAIL: the release (and the game start) of this server, by the owner's decision of 29 October 2026.</summary>
    public const double ReleaseSeconds = 80.0;

    /// <summary>
    /// NOT RETAIL: the first start announcement ("the battle begins in 30 seconds", OnStartGameMessage1 = 79, the shipped
    /// <c>Announcement_OnStartGame</c> with <c>FirstMessage</c> true at OdinScriptBehaviorTrees.xml:3169) plays 30 seconds before the
    /// release, not at the start. The owner, 29 October 2026, on bundle 30: "I get an announcement that the battle will begin in 30 seconds
    /// on the start of the game, which should play around 0:50 if game starts at 1:20". The second message (OnStartGameMessage2 = 80, at
    /// the release, :4759) keeps its place after the release.
    /// </summary>
    public const double FirstAnnouncementSeconds = ReleaseSeconds - 30.0;

    private const string ShippedFile = "OdinScriptBehaviorTrees.xml";
    private const string CeremonyTree = "OdinOpeningCeremony";

    /// <summary>
    /// Returns the literal a binding should carry. Only the one shipped threshold changes: a <c>GreaterFloat</c> right-hand side of 45
    /// in the opening ceremony tree of the Crystal Scar's behaviour-tree file. Every other binding is returned as read.
    /// </summary>
    public static string ReleaseOverride(string source, string tree, string nodeType, string parameterName, string value)
    {
        var shipped = nodeType == "GreaterFloat" && parameterName == "RightHandSide" && tree == CeremonyTree
            && value == "45" && source.EndsWith(ShippedFile, StringComparison.OrdinalIgnoreCase);
        return shipped ? ReleaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : value;
    }
}
