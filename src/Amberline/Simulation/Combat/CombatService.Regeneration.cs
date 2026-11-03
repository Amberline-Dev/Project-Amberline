using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>Returns the live incoming-heal multiplier used by combat.</summary>
    public double HealingReceivedMultiplier(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        return HealingMultiplier(hero);
    }

    // sp_RegenTickInterval boundaries crossed by this step (whole seconds of the global simulation clock for both fountains).
    private double FountainTicks(double dt) =>
        Math.Floor((_time + 1e-9) / _fountainTickSeconds) - Math.Floor((_time - dt + 1e-9) / _fountainTickSeconds);

    private void Regenerate(Entity hero, double dt)
    {
        // Reconstruction: the per-update regeneration (hero.HealthRegen/ManaRegen are the shipped per-second
        // BaseStaticHPRegen + HPRegenPerLevel * level values, applied as value * dt each step) is the server's native
        // routine, which is not in the client; the tick size of the retail server is not recovered.
        var hpGain = hero.HealthRegen > 0.0 ? hero.HealthRegen * dt : 0.0;
        var manaGain = hero.ManaRegen > 0.0 ? hero.ManaRegen * dt : 0.0;
        // Batch 149 step 5: the Chalice of Harmony's (3028) missing-mana regeneration is
        // Spells/ChaliceOfHarmony.luaobj's own BuffOnUpdateStats (BBIncPercentPARRegenMod of
        // 1 - GetPARPercent), which Items/3028.luaobj's nine-second window keeps alive; it reaches this tick as
        // hero.ManaRegen. The hand-written missing-fraction multiplier that stood in for it is gone — it doubled
        // the same bonus once the window was script-bound.
        hpGain = _hooks.ModifyHealthRegeneration(hero, hpGain, dt) * HealingMultiplier(hero);
        manaGain = _hooks.ModifyManaRegeneration(hero, manaGain, dt);
        // Natural regeneration is not a script IncHealth (Reconstruction: no UnitApplyHeal).
        Healing.Apply(hero, hpGain, announce: false);
        // Batch 149 step 5: Force of Nature's own second-tick heal is Spells/ForceofNature.luaobj's
        // BuffOnUpdateActions (BBExecutePeriodically 1, not immediate, IncHealth Owner += 0.0035 x max health), which
        // Items/3109.luaobj's window keeps alive; BBIncHealth announces it with UnitApplyHeal. The hand-written
        // tick that stood in for it is gone.
        if (manaGain > 0.0) hero.Mana = Math.Min(hero.MaxMana, hero.Mana + manaGain);
        var fountain = FountainOf(hero.Team);
        var fountainRadius = _fountainHealRadius;
        if (DistanceSquared(hero.Position, fountain) <= fountainRadius * fountainRadius)
        {
            // Map1 Constants.var: sp_HealthRegenPercent 0.085 per sp_RegenTickInterval (1 s). The client reads the radius and the
            // tick (the spawn point's update), not the percentage: the heal amounts are server-side. The retail phase is
            // unrecovered; use global whole simulation seconds for both fountains.
            var ticks = FountainTicks(dt);
            for (var tick = 0.0; tick < ticks; tick++)
            {
                Healing.Apply(hero, hero.MaxHealth * _fountainHealFraction * HealingMultiplier(hero),
                    announce: false);
                // Reconstruction: only a mana, energy or souls pool is refilled (PrimaryResourceType.RefillsAtFountain); a fury or heat pool is
                // not. What the client shows: its spawn-point code excludes no pool by name (it compares each unit's own resource type with
                // itself), and the refill itself is not in the client, so the exclusion has no client evidence.
                if (_gameData.Champion(hero.StatCharacterName).PrimaryResource.RefillsAtFountain())
                    hero.Mana = Math.Min(hero.MaxMana, hero.Mana + hero.MaxMana * _fountainManaFraction);
            }
        }
    }

    /// <summary>
    /// Map1 Constants.var sp_RegenRadius: "The spawn point will heal units in this radius". The fountain is the client's
    /// the spawn point's update: it reads sp_RegenRadius and sp_RegenTickInterval and queries
    /// the units in that radius. the temple's update is not the fountain: its 800 radius and 0.04
    /// only drive floating text, and it changes no state. The heal percentages have no client reader (the amounts are server-side).
    /// Heroes keep their own pass in <see cref="Regenerate"/>.
    /// Reconstruction: the client's target gate has no unit-type test, so which classes the server heals is not in the
    /// client. Only minion-type actors are added here (pets and clones are left out) until the owner decides otherwise.
    /// </summary>
    private void RegenerateFountainAllies(double dt)
    {
        var ticks = FountainTicks(dt);
        if (ticks <= 0.0) return;
        foreach (var unit in _world.Entities.Values.Where(entity =>
                     entity.UnitType == UnitType.Minion && entity.Alive &&
                     entity.Team is Team.Order or Team.Chaos && entity.MaxHealth > 0.0).ToArray())
        {
            var fountain = FountainOf(unit.Team);
            if (DistanceSquared(unit.Position, fountain) > _fountainHealRadius * _fountainHealRadius) continue;
            var health = unit.Health;
            for (var tick = 0.0; tick < ticks; tick++)
            {
                Healing.Apply(unit, unit.MaxHealth * _fountainHealFraction * HealingMultiplier(unit),
                    announce: false);
                unit.Mana = Math.Min(unit.MaxMana, unit.Mana + unit.MaxMana * _fountainManaFraction);
            }
            if (unit.Health != health && _transport.IsWireReady(unit))
                _transport.SendHealth(unit, unit.Health);
        }
    }

    // Retail data for the buildings: LEVELS/Map1/Scene/CFG/ObjectCFG.cfg stores mBaseStaticHPRegen 20 for HQ_T1/HQ_T2 and 15
    // for the six Barracks_* sections (and mMaxHP 5500 for the HQs; an inhibitor's maximum comes from Constants.var bar_MaxHP
    // 4000, the client's Barracks/Dampener only read the regeneration). It is the same field the characters use (a per-second
    // BaseStaticHPRegen, Annie 0.9), so 20 and 15 HP/s are applied continuously. Summoner's Rift keeps those numbers as reviewed
    // literals; Map4 reads its own file (15 and 10, MapLayout.FromScene). Minions and Tibbers: Reconstruction (the server's tick is not in the client).
    private void RegenerateMinionsAndStructures(double dt)
    {
        foreach (var minion in _world.Entities.Values.Where(entity =>
                     (entity.UnitType == UnitType.Minion || entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus ||
                      CanRegenerateTibbers(entity)) &&
                     entity.Alive &&
                     // SetBuildingHealthRegenEnabled switches a building's own regeneration off
                     // (EndOfGame.txt proto 0.0 pc 0026-0044 does it to both HQs).
                     entity.BuildingHealthRegenEnabled &&
                     entity.HealthRegen > 0.0 && entity.Health < entity.MaxHealth))
        {
            minion.Health = Math.Min(minion.MaxHealth, minion.Health + minion.HealthRegen * dt *
                (minion.PetState is { Kind: Pets.CloneKind.Tibbers } ? HealingMultiplier(minion) : 1.0));
            if (!_transport.IsWireReady(minion)) continue;
            if (!_lastMinionRegenSend.TryGetValue(minion.NetId, out var last) || _time - last >= GoldSendInterval ||
                minion.UnitType == UnitType.Structure && minion.Health >= minion.MaxHealth)
            {
                _transport.SendHealth(minion, minion.Health);
                _lastMinionRegenSend[minion.NetId] = _time;
            }
        }
    }

    private bool CanRegenerateTibbers(Entity entity) =>
        entity.Kind == EntityKind.Pet && entity.PetState is { Kind: Pets.CloneKind.Tibbers } &&
        entity.HealthRegen > 0.0 && HealingMultiplier(entity) > 0.0;

    private double HealingMultiplier(Entity hero)
    {
        // BuffOnHeal of the unit's script buffs: the ghouls' and the revenant's buffs return Health * 0 (they cannot be healed), the zombie
        // buff does once its own full heal is done. Reconstruction: the hook returns the heal for the amount it is given and the shipped ones
        // are all multiples of it, so it is read once for the amount 1 and the answer is the factor. The Spirit Visage window is one of
        // them now (batch 149 step 2): Spells/SpiritVisage.luaobj returns Health * 1.15 for a heal whose target is the holder, and its
        // REPLACE_EXISTING window keeps only one such buff however many copies the holder wears, so the amplification is the script's.
        // The hand-written inventory-row x1.15 is gone with it See docs/maintenance/2026-09-21-healing-order.md.
        // Owner-adopted rule, 21 September 2026: reduce the received heal first (the Grievous Wounds stage below), then amplify it
        // once with the scripted factor. The sustain stats are the scripts' own concern, not this stage's: the window raises them
        // and GrievousWound.luaobj's own gated stats hook cuts them, each in its own payload.
        var scripted = _hooks.ScriptHealingScale(hero);
        if (scripted <= 0.0) return 0.0;
        var multiplier = Math.Max(0.0, 1.0 + hero.Buffs
            .Where(buff => buff.Payload.Has(BuffPayloadField.HealingReceivedMult))
            .Sum(buff => buff.Payload.HealingReceivedMult)) * scripted;
        return multiplier;
    }
}
