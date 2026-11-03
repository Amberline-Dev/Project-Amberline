using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Phase 1 loose ends (18 October 2026): the damage reduction stats of the script engine (Amumu's Tantrum <c>IncFlatPhysicalReduction</c>,
    /// the Worm's WrathDebuff <c>IncFlatMagicReduction</c>, <c>IncPercent*Reduction</c>) are taken off each hit after armor or spell block.
    /// </summary>
    [Check("combat.flat-and-percent-reduction-stage")]
    public static void ReductionStatsComeOffEachHitAfterResistance()
    {
        using var match = new Match("Annie", Team.Order);
        var victim = match.Hero(0);
        var attacker = match.Hero(1);
        victim.MaxHealth = 100000;

        double Lost(DamageType type, double raw)
        {
            victim.Health = victim.MaxHealth;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, raw, type, DamageSourceKind.Spell));
            return victim.MaxHealth - victim.Health;
        }

        var physical = Lost(DamageType.Physical, 100.0);
        var magic = Lost(DamageType.Magic, 100.0);
        TestAssert.Near(100.0 * CombatService.ResistanceMultiplier(victim.Armor), physical, 1e-6, "armor alone");
        TestAssert.Near(100.0 * CombatService.ResistanceMultiplier(victim.SpellBlock), magic, 1e-6, "spell block alone");

        // Tantrum rank 3: 6 off each physical hit, none off magic or true damage.
        var tantrum = match.Runtime.SpellBuffs.Add(victim, "Tantrum", BuffType.CombatEnhancer, 30.0, victim.NetId,
            new BuffPayload { FlatPhysicalReduction = 6.0 });
        TestAssert.True(tantrum is not null, "the buff is added");
        TestAssert.Near(physical - 6.0, Lost(DamageType.Physical, 100.0), 1e-6, "flat physical reduction");
        TestAssert.Near(magic, Lost(DamageType.Magic, 100.0), 1e-6, "magic damage is untouched");
        TestAssert.Near(100.0, Lost(DamageType.True, 100.0), 1e-6, "true damage is untouched");
        TestAssert.Near(0.0, Lost(DamageType.Physical, 1.0), 1e-9, "a hit smaller than the reduction deals nothing");
        match.Runtime.SpellBuffs.Remove(victim, tantrum!.Slot);
        TestAssert.Near(physical, Lost(DamageType.Physical, 100.0), 1e-6, "the reduction leaves with its buff");

        // WrathDebuff: -15 per stack, so every magic hit takes 15 more per stack; stacks add up.
        for (var stacks = 1; stacks <= 2; stacks++)
        {
            TestAssert.True(match.Runtime.SpellBuffs.AddStacking(victim, "WrathDebuff", BuffType.CombatDehancer, 3.0, attacker.NetId, 10,
                new BuffPayload { FlatMagicReduction = -15.0 }) is not null);
            TestAssert.Near(magic + 15.0 * stacks, Lost(DamageType.Magic, 100.0), 1e-6, $"{stacks} Wrath stack(s)");
        }

        // A percentage first, then the flat amount.
        var both = match.Runtime.SpellBuffs.Add(victim, "ReductionProbe", BuffType.CombatEnhancer, 30.0, victim.NetId,
            new BuffPayload { PercentPhysicalReduction = 0.25, FlatPhysicalReduction = 4.0 });
        TestAssert.True(both is not null);
        TestAssert.Near(physical * 0.75 - 4.0, Lost(DamageType.Physical, 100.0), 1e-6, "percent, then flat");
    }
}
