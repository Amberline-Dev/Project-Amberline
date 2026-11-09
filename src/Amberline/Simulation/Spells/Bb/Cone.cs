using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Cone target selection from the spell's own data (cast cone distance and half angle), as the client's
/// cone selection runs it: the circle candidates (<see cref="BbSpellTargeting.InCircle"/>), then four
/// planes and the plane test, over the units the spell's own <c>SpellData*Flags</c> admit.
/// <para>
/// Batch 256. The planes: yaw = atan2(dir.x, dir.z); the two sides have the normals (sin a, 0, cos a) for a = yaw -/+ (90 - halfAngle) degrees
/// through the origin, the front plane has the direction as its normal through the origin, and the far plane its reverse at the cone
/// distance. A unit is in while every plane's signed distance plus <c>0.5 * max(BBox.Size.x, BBox.Size.z) + 0.001</c> is positive; the box is
/// the bound-box routine's, whose X and Z sizes are both <c>SkinScale * mScaleSkinCoef * (meshX + meshZ) / 2</c>
/// (<see cref="GameData.SkinHalfWidth"/>). The character is the one the unit shows (a pushed layer's, else its own). Reconstruction: the
/// planes are taken on the ground (the engine's positions carry no height; the client's direction and positions are 3D).
/// </para>
/// <para>
/// Batch 258. The units an area of the same flags admits (<see cref="BbSpellTargeting.Area"/>: NotAffectSelf, AlwaysSelf, then
/// <see cref="BbSpellTargeting.Admits"/>): the cone took enemies only, so Mantra's Heavenly Wave (KarmaHeavenlyWaveC, AffectFriends and
/// AlwaysSelf) healed nobody; every other shipped cone spell carries AffectEnemies AffectNeutral AffectMinions AffectHeroes, which admits the
/// units the enemy-only rule took.
/// </para>
/// </summary>
internal static class Cone
{
    // Same raw key the hand-written cone reads (SpellData half angle in degrees, 24.76 for Incinerate).
    private const uint HalfAngleKey = 0x8a05f8b9;

    /// <summary>
    /// The client's cone targeter (batch 324), for an aim point <paramref name="aim"/> and the caster at <paramref name="caster"/>. The reach is
    /// <c>R - inner / 1.5</c> with <c>R</c> = <c>CastConeDistance</c> (default 400) and <c>inner</c> =
    /// the inner-radius helper: the chord <c>|(R sin a, R cos a - R)|</c> for <c>a</c> =
    /// <c>CastConeAngle</c> (default 45) in radians. the final-target helper (the typed client
    /// its version): no direction within 30 of the caster (null); an aim nearer than the reach moves out to it; the start
    /// is the end minus the reach along the direction. <c>CanFireSpell</c>: the caster within 200 of the start
    /// (the 2D distance test) and the end between 0.7 L and L from the caster, <c>L</c> = reach + 75 (constants
    /// 1.5, 75 and 0.7). The targeter's own refusal of a start in an unwalkable cell (nav flag 0x2) is not ported.
    /// </summary>
    public static (Position2 Start, Position2 End, bool CanFire)? Fire(SpellData spell, Position2 caster, Position2 aim)
    {
        var dx = aim.X - caster.X;
        var dz = aim.Z - caster.Z;
        var distance2 = dx * dx + dz * dz;
        if (distance2 <= 900.0) return null;
        var r = ChampionSpellSupport.RawNumber(spell, "SpellData*CastConeDistance", 400.0);
        var angle = ChampionSpellSupport.RawNumber(spell, "SpellData*CastConeAngle", 45.0) * Math.PI / 180.0;
        var reach = r - Math.Sqrt(Math.Pow(r * Math.Sin(angle), 2) + Math.Pow(r * Math.Cos(angle) - r, 2)) / 1.5;
        var distance = Math.Sqrt(distance2);
        var (ux, uz) = (dx / distance, dz / distance);
        var end = distance < reach ? new Position2(caster.X + ux * reach, caster.Z + uz * reach) : aim;
        var start = new Position2(end.X - ux * reach, end.Z - uz * reach);
        var length = reach + 75.0;
        var sx = start.X - caster.X;
        var sz = start.Z - caster.Z;
        var ex = end.X - caster.X;
        var ez = end.Z - caster.Z;
        var toEnd = ex * ex + ez * ez;
        return (start, end, sx * sx + sz * sz <= 200.0 * 200.0 && toEnd <= length * length && 0.7 * length * (0.7 * length) <= toEnd);
    }

    public static IReadOnlyList<Entity> Units(SpellSystem system, SpellData spell, Position2 origin, Position2 direction, Entity caster)
    {
        var length = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (length <= 1e-9) return Array.Empty<Entity>();
        var radius = ChampionSpellSupport.RawNumber(spell, "SpellData*CastConeDistance", spell.CastRange);
        var halfAngle = spell.Raw.TryGetValue(HalfAngleKey, out var value) && value.IsNumeric ? value.AsDouble() : 24.76;
        var dx = direction.X / length;
        var dz = direction.Z / length;
        var yaw = Math.Atan2(dx, dz);
        var side = (90.0 - halfAngle) * Math.PI / 180.0;
        (double X, double Z)[] sides = [(Math.Sin(yaw - side), Math.Cos(yaw - side)), (Math.Sin(yaw + side), Math.Cos(yaw + side))];
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(spell));
        return system.World.Entities.Values.Where(unit =>
            !(ReferenceEquals(unit, caster) && flags.Contains("NotAffectSelf")) &&
            (ReferenceEquals(unit, caster) && flags.Contains("AlwaysSelf") || BbSpellTargeting.Admits(caster, unit, flags) && InCone(unit))).ToArray();

        bool InCone(Entity unit)
        {
            if (!BbSpellTargeting.InCircle(unit, origin, radius)) return false;
            var ux = unit.Position.X - origin.X;
            var uz = unit.Position.Z - origin.Z;
            var widen = HalfWidth(system.Data, unit) + 0.001;
            var along = ux * dx + uz * dz;
            return sides.All(normal => normal.X * ux + normal.Z * uz + widen > 0.0) && along + widen > 0.0 && radius - along + widen > 0.0;
        }
    }

    /// <summary>Half the unit's bounding-box width: the shown character's skin (<see cref="GameData.SkinHalfWidth"/>) times its model scale. A
    /// unit whose character the engine does not name (an unbound turret is named by its map object) takes the client's no-mesh 25.</summary>
    private static double HalfWidth(GameData data, Entity unit)
    {
        var character = unit.ScriptCharacterData?.SkinName ?? (unit.UnitType == UnitType.Hero ? unit.ChampionName : unit.BoundCharacter ?? unit.Name);
        return (string.IsNullOrWhiteSpace(character) ? 12.5 : data.SkinHalfWidth(character, unit.SkinId)) * Buffs.ModelScale.Of(unit);
    }
}
