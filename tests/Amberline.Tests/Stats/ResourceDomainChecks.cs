using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Stats;

public static class ResourceDomainChecks
{
    [Check("stats.hero.mana-only-aura-domain")]
    public static void ManaOnlyAuraDoesNotRegenEnergy()
    {
        var data = new GameData();
        var items = new ItemCatalog(data.Paths);
        var manaAuras = new[]
        {
            new BuffState("ManaManipulatorAuraFriend", new BuffPayload
            {
                MpRegen = 1.44,
                ResourceDomain = BuffResourceDomain.Mana,
            }),
            new BuffState("SoulShroudAuraFriend", new BuffPayload
            {
                MpRegen = 2.4,
                CooldownMod = -0.10,
                ResourceDomain = BuffResourceDomain.Mana,
            }),
            // Untagged shared payloads retain the historical Any behavior.
            new BuffState("SharedRegen", new BuffPayload { MpRegen = 3.0 }),
        };
        var mana = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), manaAuras));
        var energy = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("LeeSin", 1, Inventory.Empty(), manaAuras));
        var baseMana = data.StatsAtLevel(data.Champion("Annie"), 1).MpRegen;
        var baseEnergy = data.StatsAtLevel(data.Champion("LeeSin"), 1).MpRegen;

        TestAssert.Near(baseMana + 1.44 + 2.4 + 3.0, mana.MpRegen, 1e-9,
            "mana champions retain mana-only and shared regeneration");
        TestAssert.Near(baseEnergy + 3.0, energy.EnergyRegen, 1e-9,
            "energy champions exclude PAR_MANA aura regeneration");
        TestAssert.Near(baseEnergy + 3.0, energy.MpRegen, 1e-9,
            "energy public regeneration matches the filtered energy domain");

        var clone = manaAuras[0].Payload.Clone();
        TestAssert.Equal(BuffResourceDomain.Mana, clone.ResourceDomain,
            "resource domain survives buff payload renewal cloning");
    }
}
