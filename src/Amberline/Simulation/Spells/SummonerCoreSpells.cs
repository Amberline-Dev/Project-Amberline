using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Spells;

/// <summary>
/// What the engine keeps of the patch-126 core summoners. Their casts are the shipped <c>Summoner*.luaobj</c>
/// (batch 142); only the rules no script decides live here — the request's own gates and the units a cast reaches.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Whether the caster's own state refuses this cast. The summoners ship <c>CanCastWhileDisabled=True</c>, so
    /// the owner's can-use callback only refuses a dead caster (<c>IsDisabledWhileDead</c>) and a suppressed one
    /// (none of them ships <c>CannotBeSuppressed</c>): silence, roots and ordinary control do not matter. The dead test is the
    /// spell's own flag — every summoner but Fortify and Revive sets it, Fortify's is 0, so a dead hero's Fortify goes out.
    /// The rooted test is the spell's own flag too (<see cref="CannotMove"/>, read from <c>CantCastWhileRooted</c>).
    /// Flash's own gate is its script's CanCastBuildingBlocks (a movable, casting owner), read by module.CanCast after these
    /// request gates; Teleport's shipped script has no CanCast hook at all, so its inibin is the whole rule here. Revive adds
    /// one death test of its own (<see cref="ReviveSpendsThisDeath"/>).
    /// <para>
    /// Reconstruction: retail's gate for a hero in a temporary-revival state (Yorick's revival, the unused champion IsReviving hook)
    /// is not recovered. The shipped summoner scripts have no such test; this guard keeps the revival controllers' state untouched,
    /// so such a hero cannot cast.
    /// </para>
    /// </summary>
    private bool SummonerCasterGate(Entity hero, string spellName) =>
        hero.ChampionState?.IsReviving != true && hero.YorickRevival is null &&
        !hero.Buffs.Any(buff => buff.Type == BuffType.Suppression) &&
        (hero.Alive || !_data.Spell(spellName).RawFlag("SpellData*IsDisabledWhileDead")) &&
        (!_data.Spell(spellName).RawFlag("SpellData*CantCastWhileRooted") || !CannotMove(hero)) &&
        (spellName != "SummonerRevive" || ReviveSpendsThisDeath(hero));

    /// <summary>
    /// Whether the death a Revive cast would take is its own. The shipped script's <c>CanCast</c> is only
    /// <c>Owner IS_DEAD</c>, and its <c>TargetExecute</c> stands the owner up unconditionally.
    /// <para>
    /// Reconstruction: retail's behaviour when another revival path already holds the death is not recovered. This refuses such a
    /// death — the hero is standing again or waiting on
    /// Guardian Angel, or the death has no respawn scheduled and the hero would be stood up with nowhere to return to.
    /// The hand-written cast refused the same set, so the two paths never revive one death twice.
    /// </para>
    /// </summary>
    private static bool ReviveSpendsThisDeath(Entity hero) =>
        !hero.Alive && hero.RespawnAt > 0.0 &&
        !hero.Buffs.Any(buff => buff.Name == "GuardianAngel");

    /// <summary>
    /// The CanMove status bit (the unit status word, bit 2) is clear. The stock setters clear it for SetStunned,
    /// SetRooted, SetNetted, SetSleep and SetCanMove(false) (the setters' callers,
    /// SetFeared (bit 6) and SetTaunted (bit 5) leave it alone. Suppression.luaobj also sets
    /// SetStunned. An active non-Fear forced move replicates CanMove off (playtest B3 #10); a finished move awaiting
    /// retirement no longer does.
    /// </summary>
    private static bool CannotMove(Entity hero) =>
        hero.ForcedMove is { Completed: false, Effect: not "Fear" } ||
        hero.AnyBuffOrStatus(buff => buff.Payload.MovementDisabled ||
            buff.Type is BuffType.Stun or BuffType.Snare or BuffType.Sleep or BuffType.Net or BuffType.Suppression);

    /// <summary>
    /// Whether the units this cast reaches can hold the rows its script adds, named as the shipped scripts
    /// name them (batch 142): Heal's <c>SummonerHealCheck</c> on the caster, Ghost's <c>SummonerHaste</c> and Cleanse's
    /// <c>SummonerBoost</c> on the caster, Ignite's <c>SummonerDot</c> / <c>Internal_50MS</c> / <c>GrievousWound</c>
    /// and Exhaust's <c>ExhaustSlow</c> / <c>ExhaustDebuff</c> on the target, Fortify's own row (the nameless
    /// <c>BBSpellBuffAdd</c> of <c>SummonerFortify</c> TargetExecute) on every turret the cast reaches, Teleport's
    /// channel on the caster plus its marker on the target (batch 150), and Revive's marker and speed boost on the
    /// caster (batch 150). A name already on the unit reuses its row, so
    /// only a missing name needs one; a hostile control on a hero wearing Boots of Mobility (3117) starts
    /// <c>BootsOfMobilityDebuff</c> first, so that name is reserved as well. Ignite, Exhaust, Teleport and Fortify are
    /// all-or-none: the cast that cannot hold every row adds none of them — one saturated turret refuses global Fortify
    /// before any turret takes the immunity.
    /// <para>
    /// Reconstruction: no summoner script checks the buff table, and retail's overflow outcome is unrecovered, so a cast that
    /// cannot hold its buff rows is refused before it spends a cooldown (the same gate the hand-written casts kept).
    /// </para>
    /// </summary>
    private bool SummonerBuffsFit(Entity hero, string spellName, Entity[] reached) => spellName switch
    {
        "SummonerHeal" => BuffLifecycle.CanAddNamedBuffs(hero, "SummonerHealCheck"),
        "SummonerHaste" => BuffLifecycle.CanAddNamedBuffs(hero, "SummonerHaste"),
        // Cleanse removes its controls before adding the follow-up, so at a full table their removal itself supplies
        // the slot the follow-up needs.
        "SummonerBoost" => BuffLifecycle.CanAddNamedBuffs(hero, "SummonerBoost") ||
            hero.Buffs.Any(buff => !buff.Payload.NonDispellable && buff.Type is BuffType.Blind or
                BuffType.Silence or BuffType.Stun or BuffType.Taunt or BuffType.Slow or BuffType.Snare or
                BuffType.Sleep or BuffType.Fear or BuffType.Net),
        "SummonerDot" => reached is [Entity target] && BuffLifecycle.CanAddNamedBuffs(target,
            target.UnitType == UnitType.Hero && Inventory.HasItem(target.Inventory, 3117)
                ? ["BootsOfMobilityDebuff", "SummonerDot", "Internal_50MS", "GrievousWound"]
                : ["SummonerDot", "Internal_50MS", "GrievousWound"]),
        "SummonerExhaust" => reached is [Entity target] && BuffLifecycle.CanAddNamedBuffs(target,
            Inventory.HasItem(target.Inventory, 3117)
                ? ["BootsOfMobilityDebuff", "ExhaustSlow", "ExhaustDebuff"]
                : ["ExhaustSlow", "ExhaustDebuff"]),
        "SummonerFortify" => reached.All(unit => BuffLifecycle.CanAddNamedBuffs(unit, "SummonerFortify")),
        // The rows of SummonerTeleport.TargetExecute: its nameless add (the channel buff, named after the running
        // script) and Teleport_DeathRemoval on the caster, Teleport_Turret or Teleport_Target on the target, and
        // Destealth on a target its BBIfHasBuff answers SharedWardBuff for. Batch 149 step 3: the ward scripts add
        // that marker to the wards they spawn, so the gate reads the row the script reads
        // (BbSpellHost.CarriesSharedWardBuff) — which is why an item ward needs both target rows, and why a
        // ward-flagged unit that carries no marker needs only one. They are reserved together: two independent
        // one-name probes would each claim the same last free slot.
        "SummonerTeleport" => reached is [Entity target] &&
            BuffLifecycle.CanAddNamedBuffs(hero, "SummonerTeleport", "Teleport_DeathRemoval") &&
            (target.UnitType == UnitType.Turret
                ? BuffLifecycle.CanAddNamedBuffs(target, "Teleport_Turret")
                : BuffLifecycle.CanAddNamedBuffs(target, BbSpellHost.CarriesSharedWardBuff(target)
                    ? ["Teleport_Target", "Destealth"]
                    : ["Teleport_Target"])),
        // The rows of SummonerRevive.TargetExecute on its owner: ReviveMarker under its own
        // AvatarVars.RevivePreservationBonus == 400 test (Preservation), and SummonerReviveSpeedBoost always.
        // Reserved together, as Teleport's rows are: two one-name probes would each claim the same last free slot.
        "SummonerRevive" => BuffLifecycle.CanAddNamedBuffs(hero,
            RevivePreservationBonus(hero) == 400.0
                ? ["ReviveMarker", "SummonerReviveSpeedBoost"]
                : ["SummonerReviveSpeedBoost"]),
        _ => true,
    };

    /// <summary>
    /// The number SummonerRevive's own <c>TargetExecute</c> compares,
    /// <c>BBIf AvatarVars.RevivePreservationBonus CO_EQUAL 400.0</c>. Read from the same AvatarVars table the hook
    /// reads — the mastery feeders' <c>SetVarsByLevel</c> write, mastery 129's Preservation at its one rank — so this
    /// preflight reserves exactly the rows the script adds; asking the mastery page behind it instead would drift from
    /// the script the moment the two tables disagree.
    /// </summary>
    private double RevivePreservationBonus(Entity hero) =>
        Amberline.Scripting.BuildingBlocks.Runtime.BbRuntime.NumberOrNull(
            (LoadoutHooks?.AvatarVars(hero) ?? LoadoutHooks?.OwnerFor(hero)?.AvatarVars)?[
                "RevivePreservationBonus"]) ?? 0.0;

    private Position3 Ground(Position2 point) =>
        new(point.X, _movement.Navigation.HeightAt(point), point.Z);

    private bool IsInMap(Position2 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Z) &&
        point.X >= _movement.Navigation.MinX && point.X <= _movement.Navigation.MaxX &&
        point.Z >= _movement.Navigation.MinZ && point.Z <= _movement.Navigation.MaxZ;

    private static double CoreDistance(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
