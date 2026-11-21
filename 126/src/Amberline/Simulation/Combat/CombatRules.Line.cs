namespace Amberline.Simulation.Combat;

/// <summary>The release line's part of <see cref="CombatRules"/> (batch 932; the 1.0.0.132 line keeps its own copy of this file).</summary>
public static partial class CombatRules
{
    /// <summary>
    /// None: a camp monster's swing on a champion takes <c>dr_UnitToHero</c> (0.7) like every Unit. Retail footage of 1 October 2011
    /// (patch 1.0.0.125, batch 758) shows AD x 0.7, then armour. The 1.0.0.132 line takes 1.0 (December 2011 footage, batch 932).
    /// </summary>
    private static readonly double? CampMonsterToHeroRatio = null;
}
