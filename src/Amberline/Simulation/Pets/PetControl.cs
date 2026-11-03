using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The unit status Pet.lua and YorickPHPet.lua react to for Tibbers, the clones and the ghouls, read through the shared
/// <see cref="CharacterState"/> rule (the one the cast gate uses).
/// </summary>
internal static class PetControl
{
    /// <summary>
    /// A pet swing only starts while the CanAttack status bit is set (a disarm, a pacify, a stun, a sleep or a script's
    /// SetCanAttack(false) clears it). The combat tick applies the same rule to a swing it repeats on its own
    /// (<c>CombatService.AttackDisabled</c>); the pet scans queue their swings directly and come through here.
    /// A blind pet keeps every bit: Blind.luaobj only cancels the swing in progress and adds a miss chance, which
    /// <c>CombatService.LiveAttackRolls</c> already rolls for any attacker.
    /// </summary>
    public static bool QueueAttack(CombatService combat, Entity pet, Entity target) =>
        CharacterState.Of(pet).CanAttack && combat.QueueBasicAttack(pet, target);

    /// <summary>
    /// Pet.lua / YorickPHPet.lua GetTauntTarget: the caster of the newest live Taunt buff (the same pick minionLib and
    /// Leashed.lua make). OnTauntBegin sends the pet at that unit in AI_TAUNTED whatever it was doing; OnTauntEnd goes to
    /// AI_PET_ATTACK on the next taunter or back to AI_PET_IDLE.
    /// </summary>
    public static Entity? TauntTarget(WorldState world, Entity pet) => pet.Buffs
        .Where(buff => buff.Type == BuffType.Taunt)
        .Select(buff => world.Get(buff.CasterNetId))
        .LastOrDefault(caster => caster is { Alive: true } && caster.NetId != pet.NetId);
}
