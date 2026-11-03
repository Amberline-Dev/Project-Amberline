using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Items;

/// <summary>
/// What the item passives still do that no shipped script decides. Batch 149 step 5 moved the last of the
/// <c>UpdateSelfBuffActions</c> periodics and the on-hit procs they armed to the item copies' own scripts
/// (<see cref="Spells.Bb.BbItemModule"/>), so this service is only the two neutral camps that are not items
/// (the Dragon's <c>Burning</c> and <c>BlessingoftheLizardElder</c>'s own) and the shared item-slow pair the
/// <c>Internal_*Slow</c> rows and the <c>ItemSlow</c> marker form.
/// </summary>
public sealed class ItemPassiveService
{
    private readonly BuffLifecycle _buffs;
    private readonly IBuffEffectHooks _buffEffects;

    /// <summary>The scripted-item dependencies are gone with the twins (batch 149 step 5); the signature stays so the
    /// composition still hands over what the shared item-slow pair may one day need.</summary>
    public ItemPassiveService(WorldState world, GameData gameData, BuffLifecycle buffs,
        IBuffEffectHooks buffEffects, ISpellCombatService combat, ISpellEffectSink effects,
        HeroStatsService heroStats, IRandomSource? random = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(gameData);
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _buffEffects = buffEffects ?? throw new ArgumentNullException(nameof(buffEffects));
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(heroStats);
        ArgumentNullException.ThrowIfNull(random ?? new SystemRandomSource());
    }

    public void OnBasicAttackHit(Entity attacker, Entity target, double rawDamage,
        DamageType damageType)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        // Batch 149 step 5: every item that procs on a basic attack does it from the window buff its own script
        // keeps alive — Wit's End, Malady, Ionic Spark, Madred's Razors and Wriggle's Lantern, the two
        // bloodrazors, Sword of the Divine, the Odin bloodburster and Manamune's own track. What used to be here is
        // not an item either, and batch 159 part 2 moved both to the shipped scripts that own them:
        //  - the Dragon's Burning is CharScriptDragon's own CharOnHitUnit (0047-0096: DamageAmount x 1.43, then
        //    Burning BUFF_RENEW_EXISTING with the 15 and the -0.2 of its NextBuffVars);
        //  - the red buff's burn and slow are Spells/BlessingoftheLizardElder.luaobj's own BuffOnHitUnit (0598, its
        //    blocks 0273-0597: the HIT_Dodge/HIT_Miss guards, CO_IS_TYPE_AI / CO_IS_TYPE_TURRET, BBGetLevel into the
        //    10..50 TickDamage table, Burning BUFF_RENEW_EXISTING 3.0 s TickRate 1.0, and Slow
        //    BUFF_STACKS_AND_OVERLAPS MaxStack 100 with the -0.05/-0.10/-0.15 table). The buff is a live script
        //    instance now — the camp's character script places it and the buff's own BuffOnDeath transfers it — so
        //    the engine's hook dispatch already ran that block for this hit before this method (BbHookRouter's shared
        //    IBbHookSink.OnBasicAttackHit). The twin here added a second Slow and a second Burning payload.
    }

    // Spells/ItemSlow.luaobj is the shipped truth: its BuffOnUpdateStats cascade selects the strongest
    // self-bound Internal_*Slow row by name and applies that magnitude from the ItemSlow marker itself.
    // Batch 147 moved the penalty from the strongest row onto the marker and keeps the shared selector in
    // BuffLifecycle so the hand-written producers and the script markers write the same field with the
    // same number.
    public void SyncItemSlow(Entity target) => BuffLifecycle.SyncItemSlow(target);
}
