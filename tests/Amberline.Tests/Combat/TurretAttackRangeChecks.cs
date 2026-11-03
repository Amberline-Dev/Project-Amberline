using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Combat;

/// <summary>
/// mp2 R3: a lane turret attacks at its character's shipped Data*AttackRange (the CharacterData default 750 when the key is
/// absent), not at a literal default. The fountain reads its shipped 2250 the same way (batch 204).
/// </summary>
public static class TurretAttackRangeChecks
{
    [Check("combat.turret-attack-range-shipped")]
    public static void LaneTurretsAttackAtTheirShippedRange()
    {
        var data = GameData.Default;
        var mismatches = new List<string>();
        var checkedTurrets = 0;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var tier in new[] { TurretTier.Outer, TurretTier.Inner, TurretTier.Inhibitor, TurretTier.Nexus })
        {
            var name = MapDefinitions.TurretCharacter(tier, team);
            var raw = data.Champion(name).Raw;
            var shipped = raw.TryGetValue(InibinReader.KeyHash("Data*AttackRange"), out var value) &&
                          (value.IsNumeric || value.Kind == InibinValueKind.Boolean)
                ? value.AsClientNumber()
                : 750.0;
            var actual = TurretRules.StatsFor(tier, team, data).AttackRange;
            if (Math.Abs(actual - shipped) > 1e-9) mismatches.Add($"{name} shipped {shipped} got {actual}");
            checkedTurrets++;
        }
        TestAssert.Equal(8, checkedTurrets, "every lane turret of both teams was checked");
        TestAssert.True(mismatches.Count == 0,
            "lane turrets attack at their shipped AttackRange: " + string.Join("; ", mismatches));
    }

    /// <summary>
    /// Batch 220 item 2: a turret built without game data took its stats from the hand-written lane table, whose outer armor (30)
    /// is not the shipped one. Its base armor is the shipped armor plus the backdoor and lane bonuses (150 + 25).
    /// </summary>
    [Check("combat.turret-no-data-armor-is-shipped")]
    public static void TurretBuiltWithoutDataTakesTheShippedArmor()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var definition = MapDefinitions.Turrets.First(t => t.Team == team && t.Tier == TurretTier.Outer);
            var world = new WorldState();
            var turret = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team,
                new(7000, 7000)) { Name = definition.Name });
            TurretRules.Initialize(turret, 1, GameData.Default);
            var shipped = GameData.Default.Champion(MapDefinitions.TurretCharacter(TurretTier.Outer, team)).Armor;
            TestAssert.Equal(shipped + 175.0, turret.TurretBaseArmor,
                $"{definition.Name}: shipped armor {shipped} plus the 175 bonus");
        }
    }

    /// <summary>
    /// Batch 220 item 2: the absent-key defaults of TurretRules.StatsFor (AttackRange and AcquisitionRange) are never read with the
    /// shipped data, because every Map1 turret character carries both keys.
    /// </summary>
    [Check("combat.turret-range-keys-shipped")]
    public static void EveryTurretCharacterCarriesItsRangeKeys()
    {
        var data = GameData.Default;
        var missing = new List<string>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var tier in new[] { TurretTier.Outer, TurretTier.Inner, TurretTier.Inhibitor, TurretTier.Nexus, TurretTier.Fountain })
        {
            var name = MapDefinitions.TurretCharacter(tier, team);
            var raw = data.Champion(name).Raw;
            foreach (var field in new[] { "Data*AttackRange", "Data*AcquisitionRange" })
                if (!raw.ContainsKey(InibinReader.KeyHash(field))) missing.Add($"{name}.{field}");
        }
        TestAssert.True(missing.Count == 0, "every turret character carries its range keys: " + string.Join("; ", missing));
    }
}
