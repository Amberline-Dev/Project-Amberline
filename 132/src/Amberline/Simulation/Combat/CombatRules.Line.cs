namespace Amberline.Simulation.Combat;

/// <summary>The release line's part of <see cref="CombatRules"/> (batch 932; the 1.0.0.126 line keeps its own copy of this file).</summary>
public static partial class CombatRules
{
    /// <summary>
    /// Reconstruction (batch 932, players 21 November 2026: "the jungle feels way too weak"): a camp monster's basic attack on a champion takes
    /// no class ratio (1.0, not <c>dr_UnitToHero</c>). The server routine that applies the ratio is not shipped, so the rule is the footage's
    /// result. Retail footage of 25 December 2011 (patch 1.0.0.131, whose jungle is 1.0.0.132's: V1.0.0.131 and V1.0.0.132 change no monster
    /// stat; docs/maintenance/2026-11-21-batch-932-jungle-strength.md) shows a level 1-3 Nocturne of armour 57/64 hit by every Summoner's Rift
    /// camp at its target panel's AD (65, 67, 54, 30, 24, 14, 25, 17: the 132 records and scripts) x 1.0 x 100/(100 + armour), less about 2
    /// (Tough Skin): Ancient Golem 39-40 (ratio 0.6 gives 25), Golem 31 (20), Lizard Elder 38-39 (25), Giant Wolf 13-16 and 43 critical.
    /// The shipped 1.0.0.132 <c>LEVELS/Map1/Constants.var</c> lowered <c>dr_UnitToHero</c> to 0.6 (V1.0.0.130: "Damage from lane minions to
    /// champions reduced by about 15%") as the Summoner's Rift character scripts dropped their CharOnHitUnit x1.43; the monsters did not
    /// take that cut. Lane minions, pets and clones keep <c>dr_UnitToHero</c>. The footage shows Summoner's Rift only: the Dragon's swings
    /// and the Twisted Treeline's camps (whose records and x1.43 scripts are 126's) take the same rule unverified.
    /// </summary>
    private static readonly double? CampMonsterToHeroRatio = 1.0;
}
