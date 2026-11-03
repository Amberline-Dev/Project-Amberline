using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// <c>BBSpawnPet</c> (the client's parse): the pet a spell script summons. The client body reads Owner, PosVar,
/// Duration, Level, HealthBonus, DamageBonus (each through the shared number getter, so
/// <c>X + XByLevel[Level] + XVar</c>), CopyItems (default true), Name, Skin, Buff and AiScript (default <c>Pet.lua</c>), builds the 0x3C
/// spawn-pet packet from them and stores nothing else; the server body that creates the unit is not in the client. Only one shipped
/// script calls it: Annie's <c>InfernalGuardian</c> (Tibbers), so only the <c>AnnieTibbers</c> skin is run and any other raises
/// "not bound" instead of inventing a pet.
/// <para>
/// Reconstruction (every line below that is not a client parse): the unit is built from the character record named by <c>Skin</c>
/// (base hit points, damage, armor, magic resist, radii, speed, attack timing and rewards), the previous pet of the owner is killed
/// (a champion has one such pet; the hand-written Annie killed it at a recast too), the spawn point is moved to the closest legal
/// summon floor (<c>ClosestSummonPlacement</c>), <c>Duration</c> is the pet's lifetime, and the spawn packet carries the script's
/// HealthBonus and DamageBonus. <c>DamageBonus</c> is added to the pet's attack damage (80 + 0/25/50 is the designer's 80/105/130,
/// <c>InfernalGuardian</c> Effect3). <c>HealthBonus</c> is <b>not</b> added to its hit points: the pet's buff
/// (<c>InfernalGuardianBurning.BuffOnUpdateStats</c>) raises the maximum by the same 0/400/800 through <c>BBIncMaxHealth</c>, and adding both
/// would give 2800 at rank 3 against the designer's 2000 (<c>InfernalGuardian</c> Effect2, the tooltip). Armor and magic resist come
/// from the same buff over the record's base (30 and 25).
/// </para>
/// </summary>
internal static class BbPetSpawn
{
    public static void Execute(BbSpellHost host, BbCall call)
    {
        var system = host.Spells;
        var owner = BbSpellHost.UnitOrNull(call.Var("OwnerVar")) ?? host.Frame.Owner
            ?? throw new BbRuntimeException("BBSpawnPet has no owner");
        var skin = call.Value("Skin") as string ?? throw new BbRuntimeException("BBSpawnPet has no Skin");
        if (!string.Equals(skin, "AnnieTibbers", StringComparison.Ordinal))
            throw BbSpellHost.Unbound($"BBSpawnPet for {skin}", "M2");
        var rank = Math.Max(1, host.Frame.SpellRank);
        var name = call.Value("Name") as string ?? skin;
        var buff = call.Value("Buff") as string ?? string.Empty;
        var duration = BbNativeHandlers.Sum(call, "Duration");
        var healthBonus = BbNativeHandlers.Sum(call, "HealthBonus");
        var damageBonus = BbNativeHandlers.Sum(call, "DamageBonus");
        var copyItems = call.Value("CopyItems") is { } copy ? BbRuntime.Truthy(copy) : true;
        var aim = call.Value("PosVar") is { } posName && call.Table("PosVarTable", create: false)[posName] is BbTable point
            ? BbSpellHost.Ground(point) : owner.Position;
        var now = system.CurrentTime;

        var data = system.Data.Champion(skin);
        var spawnPosition = system.Movement.Navigation.ClosestSummonPlacement(aim, data.PathfindingRadius);
        // One pet at a time: a recast ends the old Tibbers first (his buff's deactivation then removes the owner's timer, which the
        // new cast adds again after this call).
        foreach (var existing in system.World.Entities.Values.Where(unit =>
                     unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId &&
                     unit.PetState is { Kind: CloneKind.Tibbers }).ToArray())
            system.Combat.Kill(existing, owner.NetId);

        var pet = new Entity(system.World.Allocate(UnitType.Monster), EntityKind.Pet, UnitType.Monster, owner.Team, spawnPosition)
        {
            Name = skin,
            SkinId = owner.SkinId,
            ChampionName = owner.ChampionName,
            OwnerNetId = owner.NetId,
            Health = data.BaseHp,
            MaxHealth = data.BaseHp,
            HealthRegen = data.HpRegen,
            AttackDamage = data.BaseDamage + damageBonus,
            Armor = data.Armor,
            SpellBlock = data.SpellBlock,
            Radius = data.PathfindingRadius,
            CombatRadius = data.CombatRadius,
            SelectionRadius = data.SelectionRadius,
            AttackRange = data.AttackRange,
            // The record's own acquisition radius (AnnieTibbers 625): the pet's FindTargetInAcR
            // circle. The hand-written machine read the same value off the record for every scan;
            // it belongs on the unit like every other spawn puts it.
            AcquisitionRange = data.AcquisitionRange,
            Speed = data.MoveSpeed,
            ExpiresAt = now + duration,
            GoldReward = data.GoldGivenOnDeath,
            ExperienceReward = data.ExpGivenOnDeath,
            Created = false,
            Visible = true,
            NodeId = 0x40,
        };
        // The patch-126 CharacterData loader ignores the legacy raw Data*AttackSpeed field and initializes the live multiplier to one. The pet
        // still keeps its own character delay offset.
        UnitAttackTiming.Apply(pet, data);
        pet.PetState = new PetState(CloneKind.Tibbers, owner.NetId) { Rank = rank, BornAt = now };
        system.World.Register(pet);
        system.Effects.SendPetSpawn(pet, new SpawnPetSnapshot(
            owner.NetId, pet.NetId, checked((byte)pet.NodeId), spawnPosition, name, skin, SkinId: pet.SkinId, Rank: rank,
            Duration: (float)duration, DamageBonus: checked((int)damageBonus), HealthBonus: checked((int)healthBonus),
            BuffName: buff, CopyInventory: copyItems, ClearFocusTarget: false));
        pet.Created = true;
        system.Effects.SendStats(pet);
        system.Effects.SendInitialVisibility(pet, new Position2(pet.Position.X, system.Movement.Navigation.HeightAt(pet.Position)));
        // The R summon path uses the vision packet builder's default sync ID for its initial reveal anchor; later pet movement owns
        // timestamps.
        // The spawn packet puts the pet on its point (floats, unsnapped); this one-point path's install (the client's follow-path routine)
        // then seats it on the point's lattice anchor at once (MovementSystem.InstallStop, batch 263).
        system.Effects.SendWaypoints(pet, system.Movement.InstallStop(pet), 1);
        if (call.Value("DestVar") is { } dest) call.Table("DestVarTable", create: true)[dest] = pet;
    }

}
