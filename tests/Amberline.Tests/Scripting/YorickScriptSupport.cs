using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Yorick runs from his shipped scripts (phase 3, W35), so a check that needs a ghoul at a chosen place builds the one his child spell
/// builds: the unit of the <c>YorickSummon*</c> <c>BBSpawnMinion</c> block (batch 164 dropped the hand-written <c>YorickGhoulService</c>;
/// the block is the only thing that asks for a ghoul and it goes down the generic <c>BBSpawnMinion</c> path,
/// <c>BbNativeHandlers.SpawnGeneric</c>) plus the Logic buff the child's <c>SelfExecute</c> adds to it (<c>Yorick{Kind}Logic</c>,
/// BUFF_Aura, 25000 s, caster Yorick). The Logic buff is the ghoul's stats, decay (a fifth of its health every 0.9 s) and the hit hook
/// that deals its swing as Yorick's PROC; the swing of a bare ghoul deals nothing. The marker on Yorick (its five-second timer and its
/// target scan) is not part of this fixture: the ghoul is acquired by the native pet AI and rots away by itself.
/// <para>
/// <see cref="SpawnUnit"/> mirrors that generic path one for one: the character record the block names as <c>Skin</c>
/// (<c>Yorick{Kind}Ghoul</c>, its <c>Data*BaseHP</c>, <c>BaseDamage</c>, <c>BaseStaticHPRegen</c>, <c>AcquisitionRange</c> and the gold and
/// XP of its death), <c>EntityKind.YorickGhoul</c>, a <see cref="CloneKind.YorickGhoul"/> pet, the record's attack timing and the
/// owner's placement rule. The display name of the spawn packet is the block's own <c>Name</c> (Clyde, Inky, Blinky).
/// </para>
/// </summary>
internal static class YorickScriptSupport
{
    /// <summary>
    /// The bare <paramref name="kind"/> ("Spectral", "Decayed" or "Ravenous") ghoul of <paramref name="owner"/>: the unit of the shipped
    /// <c>YorickSummon*</c> <c>BBSpawnMinion</c>, no script buff on it yet.
    /// </summary>
    public static Entity SpawnUnit(MatchRuntime runtime, Entity owner, string kind, Position2 position, double now)
    {
        if (kind is not ("Spectral" or "Ravenous" or "Decayed"))
            throw new ArgumentException("Yorick ghoul kind is Spectral, Ravenous, or Decayed", nameof(kind));
        var skin = "Yorick" + kind + "Ghoul";
        // The block's own Name parameter, which the spawn packet carries as the unit's display name.
        var display = kind switch { "Spectral" => "Clyde", "Ravenous" => "Blinky", "Decayed" => "Inky", _ => throw new InvalidOperationException() };
        var data = runtime.GameData.Champion(skin);
        // NOT RETAIL: the YorickSummon* scripts BBSpawnMinion at the cast point as given; the projection onto the nearest valid floor is
        // the owner's 24 September 2026 placement rule (BbNativeHandlers.SpawnGeneric applies the same one).
        position = runtime.Movement.Navigation.ClosestSummonPlacement(position, data.PathfindingRadius);
        var ghoul = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.YorickGhoul,
            UnitType.Monster, owner.Team, position)
        {
            Name = skin,
            ChampionName = skin,
            OwnerNetId = owner.NetId,
            Health = data.BaseHp,
            MaxHealth = data.BaseHp,
            HealthRegen = data.HpRegen,
            Mana = data.NamesParType ? data.BaseMp : 0.0,
            MaxMana = data.NamesParType ? data.BaseMp : 0.0,
            AttackDamage = data.BaseDamage,
            Armor = data.Armor,
            SpellBlock = data.SpellBlock,
            Speed = data.MoveSpeed,
            Radius = data.PathfindingRadius,
            CombatRadius = data.CombatRadius,
            SelectionRadius = data.SelectionRadius,
            AttackRange = data.AttackRange,
            AcquisitionRange = data.AcquisitionRange,
            GoldReward = data.GoldGivenOnDeath,
            ExperienceReward = data.ExpGivenOnDeath,
            AiState = "idle",
            // Yorick{Spectral,Ravenous,Decayed}GhoulBasicAttack is CastType 0, so its missile lands at the cast frame (0.0), as the
            // hand-written service left it.
            MissileSpeed = 0.0,
            ExpiresAt = double.PositiveInfinity,
            NodeId = 0x40,
            Created = false,
            Visible = false,
        };
        UnitAttackTiming.Apply(ghoul, data);
        ghoul.PetState = new PetState(CloneKind.YorickGhoul, owner.NetId) { BornAt = now };
        runtime.World.Register(ghoul);
        runtime.Effects.SendMinionSpawn(ghoul, display, skin);
        ghoul.Created = true;
        runtime.Effects.SendInitialVisibility(ghoul);
        runtime.Effects.SendStats(ghoul);
        return ghoul;
    }

    /// <summary>A <paramref name="kind"/> ("Spectral", "Decayed" or "Ravenous") ghoul of <paramref name="owner"/> (a Yorick of the match, so his module is bound).</summary>
    public static Entity SpawnGhoul(MatchRuntime runtime, Entity owner, string kind, Position2 position)
    {
        var module = runtime.Dependencies.ChampionDispatcher.FindModule("Yorick") as BbChampionModule
            ?? throw new InvalidOperationException("the match has no script-driven Yorick");
        var ghoul = SpawnUnit(runtime, owner, kind, position, runtime.Combat.CurrentTime);
        TestAssert.True(module.Buffs!.Add(new BbBuffRequest(ghoul, owner, $"Yorick{kind}Logic", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Aura, 1, 25000.0, 0.0, false, new BbTable())) is not null, $"the {kind} Logic buff is added");
        return ghoul;
    }
}
