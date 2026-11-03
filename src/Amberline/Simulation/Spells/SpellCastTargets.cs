using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells;

/// <summary>
/// Cast target lists and the caster reveal that reads them. The stock client uses a cast's
/// <c>SpellCastInfo.Targets</c> in four places (the client's cast code): the reveal routine
/// (first resolvable unit, called from the cast frame and from the
/// instant-cast path), the cast routine (per-target hit audio for an
/// instant cast), the cast routine (one missile per listed target for a
/// CastType 1 missile cast), and the spell-caster init (HUD selection and facing). No visual depends on
/// the list; spell hit particles arrive as separate server effect packets.
/// <para>
/// Reconstruction: the original server fills the list in server-only code that no capture or
/// binary here contains. Its shape is inferred from the shipped scripts: Incinerate,
/// InfernalGuardian and StaticField have no unit loop and only a TargetExecute block, so the
/// engine hands them the units their SpellData targeting selects (cone, area, self area);
/// the other area spells loop in their own script. This emulator lists, for every instant
/// area or cone cast of a spell that sets SpellRevealsChampion, the enemy units the
/// champion's own effect predicate selects at the cast, in world registration order. Skillshot
/// (line) casts keep an empty list: listing hit units there would make the client spawn one
/// extra missile per unit, so their reveal comes at impact through
/// <see cref="RevealMissileHit"/>.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Enemy units within <paramref name="radius"/> of <paramref name="center"/> (centre
    /// distance, the maintained area rule) that the champion's area effect would select, as
    /// the target list of an instant area cast. <paramref name="also"/> adds the effect's own
    /// extra filter (for example Orianna R's sight rule for non-champions).
    /// </summary>
    public IReadOnlyList<SpellCastTarget> AreaCastTargets(Entity caster, Position2 center,
        double radius, Func<Entity, bool>? also = null) =>
        CastTargetsOf(ChampionSpellSupport.CombatTargets(this, caster).Where(unit =>
            ChampionSpellSupport.Distance(center, unit.Position) <= radius &&
            (also is null || also(unit))));

    /// <summary>
    /// The cast-packet entries for <paramref name="units"/>. The target position carries X/Z
    /// with a zero Y, as the Annie W list always has; the client reads only the netId of an
    /// instant cast's entry.
    /// </summary>
    public static IReadOnlyList<SpellCastTarget> CastTargetsOf(IEnumerable<Entity> units) =>
        units.Select(unit => new SpellCastTarget(unit.NetId,
            new Position3(unit.Position.X, 0.0, unit.Position.Z), 0)).ToArray();

    /// <summary>
    /// The reveal routine for a cast: when <paramref name="data"/> sets
    /// SpellRevealsChampion, the first listed unit that resolves shows the caster to its team
    /// at the caster's position, through the shared attack-reveal bubble. Later entries never
    /// matter, exactly as in the native loop.
    /// </summary>
    internal void RevealCaster(Entity caster, SpellData data, IReadOnlyList<SpellCastTarget> targets)
    {
        if (!data.RevealsChampion) return;
        foreach (var target in targets)
        {
            if (_world.Get(target.NetId) is not { } unit) continue;
            _combat.RevealAttack(caster, unit);
            return;
        }
    }

    /// <summary>
    /// A launched missile spell that hit <paramref name="hit"/>: a line missile's cast lists no
    /// unit, so its hit is the first moment a unit-list reveal can run. The child spell
    /// carries its own SpellRevealsChampion (explicit on EzrealMysticShotMissile,
    /// RocketGrabMissile, BlindMonkQOne and OrianaIzuna; False on EzrealEssenceFluxMissile,
    /// EzrealTrueshotBarrage, JavelinToss and OrianaRedact), which would be inert unless
    /// something read it at the hit. Reconstruction: the stock client has no
    /// missile-hit call site of RevealSpellCaster, so the exact server moment is unrecovered;
    /// the hit is treated as a one-unit cast list.
    /// </summary>
    public void RevealMissileHit(Entity caster, string spellName, Entity hit) =>
        RevealCaster(caster, _data.Spell(spellName), CastTargetsOf([hit]));
}
