using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The unit and spell state natives of the new-champion engine bundles: wave 2 (W27b, 19 October 2026: the basic-attack skip, the spell rank, the
/// cast end point and the skin attribute flag) and wave 7 (W27c, 19 October 2026: the model scale, the per-team targetability, the sight link and the
/// path refresh). Each one is its own section below, with the client parse it was bound from.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>
    /// <c>BBSkipNextAutoAttack</c>: <c>Target</c> (required), and the body sets the
    /// unit's <c>mbSkipNextAutoAttack</c>; the reader is server-side. All six shipped callers (Xin Zhao's three combo buffs, Garen's Decisive Strike, Leona's
    /// Shield of Daybreak, Renekton's Ruthless Predator, Trundle's Troll Smash, Pick a Card) call it from <c>BuffOnPreAttack</c>, then cast the
    /// replacing spell with <c>BBSpellCast</c> (an extra-slot child): the hero's swing is that cast.
    /// </summary>
    private void SkipNextAutoAttack(BbCall call)
    {
        if (Unit(call, "TargetVar") is { } unit) unit.SkipNextAutoAttack = true;
    }

    /// <summary>
    /// <c>BBGetCastSpellDragEndPos</c>: the output <c>Dest</c>, which the body fills with the cast's end position as a point
    /// (the vector-to-table helper, the sibling of <c>BBGetCastSpellTargetPos</c>). A drag cast (a line the player pulls out
    /// from a start point) carries the start as its cast position and the end of the drag as its end position; every other cast has one point,
    /// so the end equals the cast position. Rumble's Equalizer reads both to aim its carpet of fire along the drag.
    /// </summary>
    private void GetCastSpellDragEndPos(BbCall call)
    {
        var end = _host.Frame.CastEndPosition ?? throw new BbRuntimeException("GetCastSpellDragEndPos outside a spell hook");
        StoreOut(call, "Dest", BbTable.Point(end.X, end.Y, end.Z));
    }

    /// <summary>
    /// <c>BBTestUnitAttributeFlag</c>: <c>Target</c>, <c>AttributeFlag</c> (the number getter; the shipped constant
    /// <c>HAS_SUNGLASSES</c> is 0) and the output <c>Result</c>, true when bit <c>AttributeFlag</c> of the unit's <c>mExtraAttributeFlags</c> is set. The
    /// flags are the character data of the unit's skin (<see cref="ChampionData.ExtraAttributeFlags"/>); Leona's Sunlight deals one point less to
    /// a hero who wears sunglasses. Reconstruction: only heroes are read, because no shipped minion or monster carries the key.
    /// </summary>
    private void TestUnitAttributeFlag(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        var flag = (int)Sum(call, "AttributeFlag");
        var flags = unit.UnitType == UnitType.Hero ? Spells.Data.Champion(unit.ChampionName).ExtraAttributeFlags(unit.SkinId) : 0;
        StoreOut(call, "Result", ((uint)flags & 1u << (flag & 31)) != 0);
    }

    /// <summary>
    /// <c>BBIncSpellLevel</c>: <c>SpellSlotOwner</c>, <c>SlotType</c> and <c>SpellSlot</c> (the shared slot ranges; a slot outside
    /// them raises the script alert "slot was out of range for BBSetPARCostInc!" and nothing happens). The body sends <c>LevelUpSpell</c>
    /// (<c>0x5E</c>, the slot) and raises the level of the same slot in the server's own spellbook (the spell instance's level step: level + 1,
    /// capped at 6). It spends no skill point and runs no level-up hook. Karma's character script gives her Mantra its first rank when she
    /// spawns. Only the champion's four spell slots are bound: the shipped call names no other. A unit that has no wire yet keeps the level
    /// as state, which the spawn packets carry.
    /// </summary>
    private void IncSpellLevel(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is not { } owner) return;
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        var slot = (int)Sum(call, "SpellSlot");
        if (type is BbSlotType.InventorySlots or BbSlotType.ExtraSlots)
            throw BbSpellHost.Unbound($"BBIncSpellLevel on an {type} slot", "W27b");
        if ((uint)slot >= SpellSystem.ChampionSlotCount || owner.UnitType != UnitType.Hero) return;
        owner.SpellRanks[slot] = Math.Min(owner.SpellRanks[slot] + 1, 6);
        if (Spells.Effects.IsReferenceReady(owner)) Spells.Effects.SendLevelUpSpell(owner, slot);
        Spells.SendChampionStats(owner);
    }

    /// <summary>
    /// <c>BBIncScaleSkinCoef</c>: <c>Owner</c> (a unit) and <c>Scale</c>
    /// (the number getter); the body adds <c>Scale</c> to the unit's <c>CharInter.mScaleSkinCoef</c>, the replicated coefficient the client multiplies
    /// into the model's own <c>SkinScale</c> (the unit's draw scale) and whose base value is 1. The intermediate is rebuilt from
    /// the stats hooks on every update, so the increment that counts is the one of <c>BuffOnUpdateStats</c> (Olaf's Ragnarok +0.1, Nasus' Fury of
    /// the Sands +0.3, Renekton's Dominus +0.2, Cho'Gath's Feast the script's <c>Bonus</c>, Malzahar's voidling phase +0.5, the Odin
    /// buffs); the same call in <c>OnBuffActivate</c> changes a value the next update overwrites, so it does nothing here. The server keeps the
    /// sum in the buff payload and replicates 1 + the sum of the unit's buffs (<c>ModelScale</c>, MAP registry bit 7 of a hero and bit 3 of any
    /// other unit).
    /// </summary>
    private void IncScaleSkinCoef(BbCall call)
    {
        if (Unit(call, "OwnerVar") is not { } owner) return;
        if (!_host.HasFrame || _host.Frame.Stats is not { } collector || !ReferenceEquals(owner, _host.Frame.Owner)) return;
        collector.AddScaleSkinCoef(Sum(call, "Scale"));
    }

    /// <summary>
    /// <c>BBSetNotTargetableToTeam</c>: <c>Target</c>
    /// (a unit), and the booleans <c>ToAlly</c> and <c>ToEnemy</c>. ToAlly gives <c>SPELL_NonTargetableAlly</c> 0x800000, ToEnemy ORs in
    /// <c>SPELL_NonTargetableEnemy</c> 0x1000000, and neither gives <c>SPELL_TargetableToAll</c> 0x2000000. The body sets IsTargetable to 1, then
    /// the targetable-to-team flag to 0x2000000 and then to the flags. Jarvan's Demacian Standard can be targeted by its own team only
    /// (<c>ToEnemy</c>), Monkey King's decoy by the enemy only (<c>ToAlly</c>). The IsTargetable write has no shipped case: neither caller is
    /// untargetable at the call (the standard sets only call-for-help statuses, the decoy SetGhosted, SetCanAttack and SetCanMove), so the
    /// status is left alone here. The unit keeps the setting until a script changes it; the server's targeting rules (<c>IsTargetable</c>
    /// against an observer, the area scans) read it, and the client is sent the flags.
    /// </summary>
    private void SetNotTargetableToTeam(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        unit.NotTargetableToAlly = Flag(call, "ToAlly", false);
        unit.NotTargetableToEnemy = Flag(call, "ToEnemy", false);
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendTargetableToTeam(unit);
    }

    /// <summary>
    /// <c>BBLinkVisibility</c> (both <c>Unit1</c> and <c>Unit2</c> required) and <c>BBRemoveLinkVisibility</c>
    /// (both optional): the client parses the two units (the client's two parse functions, not re-read; the other build's line numbers are not valid
    /// evidence here) and does nothing else, so the semantics are the server's. A team that sees one of the two
    /// units sees the other (<see cref="WorldState.VisibilityLinks"/>, closed over chains by <c>VisibilityRules</c>). Lux's Final Spark and Xerath's
    /// Arcanopulse link the cubes that carry the beam, Karthus' Wall of Pain its three cubes, Karma's Spirit Bond the two linked champions; the
    /// removal is called when the buff ends. Reconstruction (see <c>VisibilityRules.AddLinkedUnits</c>).
    /// </summary>
    private void LinkVisibility(BbCall call, bool link)
    {
        if (Unit(call, "Unit1Var") is not { } first || Unit(call, "Unit2Var") is not { } second) return;
        if (link) Spells.World.LinkVisibility(first.NetId, second.NetId);
        else Spells.World.UnlinkVisibility(first.NetId, second.NetId);
    }

    /// <summary>
    /// <c>BBForceRefreshPath</c>: <c>Unit</c> (required); the body
    /// sets the unit's pathing actor <c>m_ForceRefreshPath</c> (the client stubs <c>RefreshPath</c> and <c>ServerPathfinding</c>, so the planner that
    /// reads it is the server's). See <see cref="MovementSystem.ForceRefreshPath"/>.
    /// </summary>
    private void ForceRefreshPath(BbCall call)
    {
        if (Unit(call, "UnitVar") is { } unit) Spells.Movement.ForceRefreshPath(unit);
    }
}
