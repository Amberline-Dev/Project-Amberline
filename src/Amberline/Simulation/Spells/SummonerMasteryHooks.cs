using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells;

/// <summary>
/// The mastery numbers the summoner path reads outside the script engine. Their AvatarVars branches are the
/// shipped scripts' own (batch 142 onward: the feeders' keys land in <c>AvatarVars</c> and
/// <see cref="Bb.BbSummonerModule"/> runs the arithmetic); what is left here is the read the engine's own
/// buff bookkeeping makes of them.
/// </summary>
public sealed partial class SpellSystem
{
    private static MasterySummonerEffects SummonerMasteries(Entity hero) =>
        hero.LoadoutMasteries.Summoner;

    // Ghost's duration and speed, Clairvoyance's duration, Exhaust's duration and Clarity's ally share are the shipped
    // scripts' own AdjustCooldown / buff blocks now (batch 142); the masteries they read arrive as AvatarVars. Revive's
    // cooldown and its Preservation row left with it in batch 150 (SummonerRevive.luaobj's AdjustCooldown and its
    // TargetExecute), so the hand-written cooldown arithmetic and the rune 5378 constant went with them — both now
    // live where the scripts are read: the script's own AdjustCooldown (BbSummonerModule.AdjustCooldown) and its HasRune5378 fill.

    private static bool HasBurningEmbers(Entity hero) =>
        SummonerMasteries(hero).BurningEmbersWhenIgniteOnCooldown;

    private static double BurningEmbersAbilityPower(Entity hero) =>
        SummonerMasteries(hero).BurningEmbersAbilityPowerBonus;

    private void UpdateBurningEmbers(Entity hero)
    {
        var existing = hero.Buffs.FirstOrDefault(buff => buff.Name == "BurningEmbers");
        if (!HasBurningEmbers(hero))
        {
            if (existing is not null) _buffs.Remove(hero, existing.Slot);
            return;
        }

        var igniteOnCooldown = hero.SummonerSpells
            .Select((name, slot) => (name, slot))
            .Any(pair => string.Equals(pair.name, "SummonerDot",
                    StringComparison.OrdinalIgnoreCase) &&
                pair.slot < hero.SummonerCooldowns.Count &&
                hero.SummonerCooldowns[pair.slot] > 0.0);
        if (!igniteOnCooldown)
        {
            if (existing is not null) _buffs.Remove(hero, existing.Slot);
            return;
        }

        // Talents/146.luaobj UpdateSelfBuffActions adds the buff only while it is missing:
        // BUFF_REPLACE_EXISTING, BUFF_Internal, Duration 25000 (BurningEmbers.luaobj is +10 AP and nothing else).
        if (existing is not null) return;
        // This runs on every spell tick, outside the original Ignite
        // cast. Retry when a slot opens instead of aborting the tick.
        if (!BuffLifecycle.CanAddNamedBuffs(hero, "BurningEmbers")) return;
        _buffs.Add(hero, "BurningEmbers", BuffType.Internal, 25000.0, hero.NetId,
            new BuffPayload { AbilityPower = BurningEmbersAbilityPower(hero) },
            canMitigateDuration: false);
    }
}
