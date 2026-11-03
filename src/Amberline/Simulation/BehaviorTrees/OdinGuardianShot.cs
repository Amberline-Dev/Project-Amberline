using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 481b: the capture guardian's shot. Only the guardian casts <c>OdinGuardianSpellAttack</c> (its ExtraSpell1, cast by its Spell1
/// <c>OdinGuardianSpellAttackCast</c>), so the name alone selects the shot. Batch 501: the Spell1 cast is its tower tree's CastUnitSpell of
/// slot 0 (OdinTowerCastAbility0); the shot's facts stay here, in the cast path every caller of that slot goes through.
/// </summary>
internal static class OdinGuardianShot
{
    public const string AttackSpell = "OdinGuardianSpellAttack";
    private const string Model = "OdinNeutralGuardian";

    /// <summary>
    /// The height the guardian's missile leaves from, above its ground point: the bind-pose height of the spell's
    /// <c>SpellData*MissileBoneName</c> bone (the crystal, 333.9 units) in the guardian's skeleton. Zero for any other spell.
    /// Reconstruction: the retail client starts a line or homing missile at that joint when the cast does not override its position
    /// (<c>DoCast_ClientAndServer</c>, BbChampionModule.cs <c>OverrideCastPositionFlag</c>), and the replicated missile position follows the
    /// server on every refresh. The bind pose is used (no animation), and the fall to the target's ground is linear in the distance flown
    /// (<c>BbSpellProjectile.HeightAt</c>); the retail flight path past the joint is not recovered.
    /// </summary>
    public static double LaunchHeight(string spell, SpellData data)
    {
        if (spell != AttackSpell || data.RawText("SpellData*MissileBoneName") is not { Length: > 0 } bone) return 0.0;
        var skeleton = GameDataPaths.Default.Data($"Characters/{Model}/{Model}.skl");
        return SkeletonFile.BindOrigin(skeleton, bone)?.Y ?? 0.0;
    }

    /// <summary>
    /// Batch 481b (Reconstruction): the spell whose missile the client draws for the guardian's shot. The shipped team attacks are
    /// <c>OdinChaosGuardianBasicAttack</c> (<c>ChaosTurretFire_mis.troy</c>) and <c>OdinOrderGuardianBasicAttack</c>
    /// (<c>OrderTurretFire_mis.troy</c>); <c>OdinGuardianSpellAttack</c>, the attack every guardian casts, names only the Order missile. Retail's
    /// per-team guardians (AiEntityDataFile's <c>OdinOrderGuardian</c> and <c>OdinChaosGuardian</c>) must have carried their own attacks, and the
    /// 126 client ships the Chaos one, so a team-owned guardian's shot takes its team's attack. The damage stays with
    /// <c>OdinGuardianSpellAttack</c>'s script. A neutral guardian does not attack (the capture host never ticks its tower tree), so its name is kept.
    /// </summary>
    public static string Spell(Entity caster, string spell) => spell != AttackSpell ? spell : caster.Team switch
    {
        Team.Chaos => "OdinChaosGuardianBasicAttack",
        Team.Order => "OdinOrderGuardianBasicAttack",
        _ => spell,
    };
}
