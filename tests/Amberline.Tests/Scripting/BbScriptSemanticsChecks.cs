using System.Reflection;
using Amberline.Core;
using Amberline.Simulation.Spells.Bb;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests;

/// <summary>Batch 269: engine rules of the BuildingBlocks runtime that the champion lanes reported.</summary>
public static class BbScriptSemanticsChecks
{
    internal static BbSpellHost Host(BbM3.Match match) =>
        (BbSpellHost)typeof(BbChampionModule).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(match.Module)!;

    // Goal 1. CO_IS_TYPE_AI / _HERO / _TURRET and CO_IS_MELEE call the natives IsObjectAI, IsObjectHero,
    // IsTurretAI and IsMelee. Each answers false for nil or a value that is no game object, and otherwise reads the
    // object's type bits (0x400 for a base unit, 0x1000 for a hero, 0x2000 for a turret). A hero is AI.
    [Check("scripting.bb-type-tests-read-the-object-type-bits-and-nil-is-no-unit")]
    public static void TypeTestsReadTheObjectTypeBits()
    {
        using var match = new BbM3.Match("Soraka");
        var host = Host(match);
        bool C(BbComparison op, object? a) => BbBaseLibrary.Compare(host, op, a, null);
        Entity Unit(UnitType type) => new(90000u + (uint)type, EntityKind.Unknown, type, Team.Chaos, new Position2(7000, 7000));

        TestAssert.True(C(BbComparison.CO_IS_TYPE_AI, match.Foe) && C(BbComparison.CO_IS_TYPE_HERO, match.Foe), "a hero is obj_AI_Base and obj_AI_Hero");
        foreach (var type in new[] { UnitType.Minion, UnitType.Monster, UnitType.Turret })
            TestAssert.True(C(BbComparison.CO_IS_TYPE_AI, Unit(type)) && C(BbComparison.CO_IS_NOT_HERO, Unit(type)), $"{type} is AI and not a hero");
        TestAssert.True(C(BbComparison.CO_IS_TYPE_TURRET, Unit(UnitType.Turret)) && C(BbComparison.CO_IS_NOT_TURRET, match.Foe), "only a turret is IsTurretAI");
        foreach (var type in new[] { UnitType.Structure, UnitType.Missile })
            TestAssert.True(C(BbComparison.CO_IS_NOT_AI, Unit(type)), $"a {type} carries no obj_AI_Base bit");

        // nil is no game object: every positive test fails and every negative one holds, whoever runs the hook (no frame stands in for it).
        TestAssert.False(C(BbComparison.CO_IS_TYPE_AI, null), "IsObjectAI(nil) is false");
        TestAssert.False(C(BbComparison.CO_IS_TYPE_HERO, null), "IsObjectHero(nil) is false");
        TestAssert.False(C(BbComparison.CO_IS_TYPE_TURRET, null), "IsTurretAI(nil) is false");
        TestAssert.False(C(BbComparison.CO_IS_MELEE, null), "IsMelee(nil) is false");
        TestAssert.True(C(BbComparison.CO_IS_NOT_AI, null) && C(BbComparison.CO_IS_NOT_HERO, null) && C(BbComparison.CO_IS_NOT_TURRET, null) &&
                        C(BbComparison.CO_IS_RANGED, null), "the NOT forms hold for nil");
        TestAssert.False(C(BbComparison.CO_IS_TYPE_AI, "Target"), "a string is no game object");
    }

    // Batch 320: the character loader reads Data*IsMelee with the default "No" and sets the melee bit only for
    // "Yes"; the character-data setter sets mCombatType from the data of every push, so the top layer's record decides.
    [Check("scripting.bb-is-melee-reads-the-top-character-record-with-default-no")]
    public static void IsMeleeReadsTheTopCharacterRecordWithDefaultNo()
    {
        using var match = new BbM3.Match("Soraka");
        var host = Host(match);
        var mushroom = new Entity(90001u, EntityKind.Unknown, UnitType.Minion, Team.Chaos, new Position2(7000, 7000))
            { Name = "TeemoMushroom", AttackRange = 0.0 };
        TestAssert.False(host.IsMelee(mushroom), "TeemoMushroom's record has no IsMelee key: the loader's default No (ranged)");
        var wolf = new Entity(90002u, EntityKind.Unknown, UnitType.Monster, Team.Neutral, new Position2(7000, 7000))
            { Name = "Wraith", AttackRange = 150.0 };
        TestAssert.False(host.IsMelee(wolf), "Wraith's record says No whatever its range");
        var hero = match.Foe;
        var before = hero.ChampionName;
        hero.ChampionName = "Nidalee";
        TestAssert.False(host.IsMelee(hero), "Nidalee's own record says No");
        hero.ScriptCharacterData = new ScriptCharacterLayer(1u, "Nidalee_Cougar", OverrideSpells: true);
        TestAssert.True(host.IsMelee(hero), "the pushed Nidalee_Cougar record says Yes");
        hero.ScriptCharacterData = null;
        hero.ChampionName = before;
    }

    // Batch 320: the loader reads Data*PARType with the default "MP", the PARTypeHelper map's MANA name, so a
    // non-hero whose record names no PARType is a mana unit; one that names Shield keeps its shield.
    [Check("scripting.bb-non-hero-without-a-par-type-is-a-mana-unit")]
    public static void NonHeroWithoutAParTypeIsAManaUnit()
    {
        using var match = new BbM3.Match("Soraka");
        var host = Host(match);
        Entity Unit(string name) => new(90003u, EntityKind.Unknown, UnitType.Minion, Team.Chaos, new Position2(7000, 7000)) { Name = name };
        TestAssert.Equal((BbParType?)BbParType.PAR_MANA, host.PowerType(Unit("CaitlynTrap")), "CaitlynTrap names no PARType: mana");
        TestAssert.Equal((BbParType?)BbParType.PAR_SHIELD, host.PowerType(Unit("SightWard")), "a ward names Shield");
        TestAssert.Equal((BbParType?)null, host.PowerType(Unit("NoSuchCharacterB320")), "a made-up body has no record and no pool");
    }
}
