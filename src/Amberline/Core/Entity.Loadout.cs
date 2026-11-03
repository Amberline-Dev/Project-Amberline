namespace Amberline.Core;

/// <summary>Per-seat account loadout retained on the authoritative hero.</summary>
public sealed partial class Entity
{
    public RunePage Runes { get; set; } = RunePage.Empty;
    public MasteryPage Masteries { get; set; } = MasteryPage.Empty;
    public int SummonerLevel { get; set; } = 30;

    // Derived once by HeroStatsService and consumed by combat/economy hooks.
    // Keeping the contribution on the authoritative hero avoids treating a
    // pet or clone's owner as the attacking unit when a hook is evaluated.
    public MasteryContribution LoadoutMasteries { get; internal set; } = MasteryContribution.Empty;
    public double LoadoutGoldPer10 { get; internal set; }
    public double LoadoutExperiencePercent { get; internal set; }
    /// <summary>Positive fraction removed from the ordinary respawn timer.</summary>
    public double LoadoutRespawnTimePercent { get; internal set; }
}
