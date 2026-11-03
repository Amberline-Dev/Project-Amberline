using Amberline.Data;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Bots;

/// <summary>How a bot aims one spell, read from the spell's own data (see <see cref="BotCast.Classify"/>).</summary>
internal enum BotAim
{
    /// <summary>Cast at the bot: <c>TTYPE_Self</c>, or a self-centred spell that only helps allies.</summary>
    Self,
    /// <summary>A self-centred spell that hits enemies: cast only with an enemy inside its radius.</summary>
    SelfArea,
    /// <summary>A unit-target spell for enemies.</summary>
    EnemyUnit,
    /// <summary>A unit-target spell for allies; the bot itself counts as one.</summary>
    AllyUnit,
    /// <summary>A unit-target spell that admits both: an enemy first, else an ally.</summary>
    EnemyOrAllyUnit,
    /// <summary>A ground or location spell (a skillshot or an area) for enemies: at the target, led when a missile flies.</summary>
    EnemyPoint,
    /// <summary>A ground or location spell that only helps allies: at the bot.</summary>
    AllyPoint,
    /// <summary>A cone or direction spell for enemies: from the bot toward a target inside its length.</summary>
    Cone,
}

/// <summary>
/// NOT RETAIL: owner decision 18 October 2026. Retail 126 has a champion <c>TaskCastSpell</c> only for the bots Annie, Sivir and
/// Soraka; the other 15 retail bots (Alistar, Ashe, Cho'Gath, Malphite, Maokai, Master Yi, Miss Fortune, Nasus, Nunu, Renekton,
/// Ryze, Shen, Taric, Trundle, Warwick) load the generic script, whose <c>BeginTask</c> is empty, and never cast. The owner wants
/// them to cast, so they run the template the three scripts share (random slot, <c>CanUseSpell == 0</c>, then the cast) with the
/// aim the native <c>ServerCastSpell</c> would have to make, taken from each spell's own data: its <c>TargettingType</c> and the
/// <c>SpellData*Flags</c> that say whom it affects.
/// </summary>
internal static class BotCast
{
    /// <summary>The slot's aim from its data alone: no champion, spell name or world state is consulted.</summary>
    public static BotAim Classify(SpellData spell) => Classify(spell, FlagNames(spell));

    public static HashSet<string> FlagNames(SpellData spell) => BbSpellTargeting.Names(BbSpellTargeting.Flags(spell));

    public static BotAim Classify(SpellData spell, HashSet<string> flags)
    {
        var enemies = flags.Contains("AffectEnemies");
        var friends = flags.Contains("AffectFriends");
        var type = (BbTargetType)(uint)BbChampionModule.RawCount(spell, "SpellData*TargettingType");
        return type switch
        {
            BbTargetType.TTYPE_Self => BotAim.Self,
            BbTargetType.TTYPE_SelfAOE => enemies ? BotAim.SelfArea : BotAim.Self,
            BbTargetType.TTYPE_Target => enemies && friends ? BotAim.EnemyOrAllyUnit : friends ? BotAim.AllyUnit : BotAim.EnemyUnit,
            BbTargetType.TTYPE_Area or BbTargetType.TTYPE_TargetOrLocation or BbTargetType.TTYPE_Location =>
                enemies ? BotAim.EnemyPoint : BotAim.AllyPoint,
            BbTargetType.TTYPE_Cone or BbTargetType.TTYPE_Direction => enemies ? BotAim.Cone : BotAim.Self,
            _ => BotAim.Self,
        };
    }

    /// <summary>How far a self-area spell reaches from the bot: the larger of its radius and its range (Shatter 400/400, Starcall 660/660).</summary>
    public static double AreaRadius(SpellData spell, int rank) => Math.Max(spell.CastRadius, spell.CastRangeAt(rank));

    /// <summary>
    /// How far a cone reaches: its <c>CastConeDistance</c>, or the cast range when the inibin ships none (the key defaults to 100 in the
    /// client, which no cone uses).
    /// </summary>
    public static double ConeLength(SpellData spell, int rank)
    {
        var cone = BbChampionModule.RawCount(spell, "SpellData*CastConeDistance");
        return cone > 100 ? cone : Math.Max(cone, spell.CastRangeAt(rank));
    }

    /// <summary>Whether a flying missile (CastType 1, 2 or 3 with a speed) makes the aim worth leading.</summary>
    public static bool LeadsTarget(SpellData spell) =>
        BbChampionModule.CastType(spell) is 1 or 2 or 3 && spell.MissileSpeed is > 0.0 and < 10000.0;
}
