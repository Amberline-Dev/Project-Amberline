using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The unit family of the BuildingBlocks natives (Phase 3, W28): the script-variable unit setters, the hero lookup by character name, the
/// zombie and forced-death pair, experience, the health bar, the mana cost increment, the spell offset target and the channel stop. Each
/// one drives the service that already owns the thing: the interpreter scope, the world's hero list, <c>MatchGameplayHooks</c>' zombie life, <c>CombatService.AwardHeroProgress</c>, the spell effect sink, the hero stat snapshot, <c>SpellSystem</c>'s channel hook.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> UnitsBound = new(StringComparer.Ordinal)
    {
        "BBSetUnit", "BBInvalidateUnit", "BBGetChampionBySkinName", "BBIncExp", "BBIncGold", "BBRedirectGold", "BBGetIsZombie", "BBForceDead", "BBShowHealthBar", "BBSetPARCostInc", "BBSetSpellOffsetTarget", "BBStopChanneling",
    };

    private bool ExecuteUnits(BbCall call)
    {
        switch (call.Function)
        {
            case "BBSetUnit": SetUnit(call); return true;
            case "BBInvalidateUnit": InvalidateUnit(call); return true;
            case "BBGetChampionBySkinName": GetChampionBySkinName(call); return true;
            case "BBIncExp": IncExp(call); return true;
            case "BBIncGold": IncGold(call); return true;
            case "BBRedirectGold": RedirectGold(call); return true;
            case "BBGetIsZombie": GetIsZombie(call); return true;
            case "BBForceDead": ForceDead(call); return true;
            case "BBShowHealthBar": ShowHealthBar(call); return true;
            case "BBSetPARCostInc": SetParCostInc(call); return true;
            case "BBSetSpellOffsetTarget": SetSpellOffsetTarget(call); return true;
            case "BBStopChanneling": StopChanneling(call); return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>BBSetUnit</c>: <c>SrcVar</c> and <c>DestVar</c> are variable names in the root scope; the client looks the unit
    /// up under the first name (the unit getter, null when the variable holds no unit) and stores the same unit
    /// handle under the second as a light userdata (it also writes the hashed <c>_gobj</c> key, which has no counterpart here). Orianna's
    /// Detonate command, Riven's Triple Cleave, Caitlyn's trap and the H-28G turret use it to remember the unit an iteration found. A source
    /// that holds no unit leaves the destination holding none.
    /// </summary>
    private void SetUnit(BbCall call)
    {
        if (call.Value("DestVar") is not { } destination) return;
        call.Scope.Vars[destination] = call.Value("SrcVar") is { } source && call.Scope.Vars[source] is Entity unit ? unit : BbScope.NothingUnit;
    }

    /// <summary>
    /// <c>BBInvalidateUnit</c>: <c>TargetVar</c> names a variable and the client stores a NULL unit under it (the hashed
    /// <c>_gobj</c> key too). It empties the slot a loop is about to refill (<c>Other2</c> in the H-28G turret and Caitlyn's trap). The
    /// invalid unit is the interpreter's <see cref="BbScope.NothingUnit"/>, the value of the <c>Nothing</c> slot: natives read it as no unit.
    /// </summary>
    private void InvalidateUnit(BbCall call)
    {
        if (call.Value("TargetVar") is { } name) call.Scope.Vars[name] = BbScope.NothingUnit;
    }

    /// <summary>
    /// The native team number of a team parameter: <c>TeamVar</c> names a variable that holds a team number (<c>BBGetTeamID</c>) and wins over the
    /// literal <c>Team</c> (the client's team getter, the team parameter's conversion, which every native with a <c>Team</c> key shares). Null
    /// when neither is there.
    /// </summary>
    private static double? NativeTeam(BbCall call) =>
        call.Value("TeamVar") is { } variable && call.Table("TeamVarTable", create: false)[variable] is double fromVariable
            ? fromVariable : call.Value("Team") as double?;

    /// <summary>
    /// <c>BBGetChampionBySkinName</c>: <c>Skin</c> (a string) and
    /// <c>Team</c> are required; the client walks the hero manager (every hero of the match, dead or alive, never a clone or pet) and pushes each
    /// hero whose character name equals <c>Skin</c> (case-insensitive, <c>_stricmp</c>) and whose team is <c>Team</c> to <c>Dest</c>, so the last
    /// match wins and no match leaves the variable as it was. Shaco's box and clone, Annie's Tibbers and Yorick's ghouls use it to find
    /// their master by name and team, because the minion's script has no owner slot.
    /// </summary>
    private void GetChampionBySkinName(BbCall call)
    {
        if (call.Value("Skin") is not string skin || NativeTeam(call) is not { } team) return;
        foreach (var hero in Spells.World.Entities.Values)
        {
            if (hero.Kind != EntityKind.Hero || hero.UnitType != UnitType.Hero || hero.OwnerNetId != 0) continue;
            if (string.Equals(hero.ChampionName, skin, StringComparison.OrdinalIgnoreCase) && TeamMapping.ToNative(hero.Team) == (int)team)
                StoreOut(call, "Dest", hero);
        }
    }

    /// <summary>
    /// <c>BBIncExp</c>: the client registers it as a bare return, so the parameters
    /// are the one shipped call site's: <c>Target</c> and <c>Delta</c> (the shared number getter). Nidalee's character script gives every allied
    /// champion within 500 and below her level 5 experience every five seconds. The experience goes through the hero progression service
    /// the kill rewards and the hand-written Nidalee already use (<see cref="ISpellCombatService.AwardHeroProgress"/>: the experience bonus of the
    /// hero's masteries, level-ups, the gold and experience packet); a unit that is not a hero has none, and a dead hero gains none.
    /// <para>
    /// Reconstruction (owner decision 20 Oct 2026, "dead champions' passives stop"; retail's native refusal for a dead owner is not recovered) — while the hero the call runs for is dead, nothing is paid. A dead
    /// champion's ally-affecting passives stop until it respawns, and this is the one shipped ally-affecting pass with no dead check of its own:
    /// CharScriptNidalee's <c>UpdateSelfBuffActions</c> has no <c>CO_IS_NOT_DEAD</c>, so the block would keep handing her allies experience from a
    /// corpse's poll (batch 108 Reconstruction did). The retail footage behind the rule is Zilean's passive leaving his allies while he is dead,
    /// where the script's own <c>Owner IS_NOT_DEAD</c> test is what stops it. A temporary life (Yorick's Omen of Death) is alive again and pays.
    /// </para>
    /// Reconstruction: a negative delta (no shipped script has one) is refused rather than guessed.
    /// </summary>
    private void IncExp(BbCall call)
    {
        if (BbSpellHost.UnitOrNull(call.Scope.Owner) is { UnitType: UnitType.Hero, Alive: false }) return;
        if (Unit(call, "TargetVar") is not { } target) return;
        var delta = Sum(call, "Delta");
        if (delta < 0.0) throw BbSpellHost.Unbound("BBIncExp with a negative Delta", "W28");
        if (target.UnitType == UnitType.Hero && delta > 0.0) Spells.Combat.AwardHeroProgress(target, delta);
    }

    /// <summary>
    /// <c>BBIncGold</c>: the client handler validates <c>TargetVar</c> and <c>Delta</c> only (the rest of its effect is not in the exe), so the parameters are the shipped call
    /// sites': <c>Target</c> and <c>Delta</c> (the shared number getter: a literal, <c>DeltaByLevel</c> or <c>DeltaVar</c>). Voracity pays 25 for a champion kill or
    /// assist, Archer's Mark 1 to 5 and Second Sight 2 for every kill, Gangplank's Parley the gold of his mark, Plentiful Bounty (Smite) its bonus. The gold
    /// joins the hero's gold and earned total through the progression service the kill rewards use (<see cref="ISpellCombatService.AwardHeroGold"/>, the gold and
    /// experience sync of the hero) and the 0x25 (UnitAddGold) of the kill rewards goes out from the unit the gold came from, so the client shows its
    /// floating text (owner finding 20, 27 October 2026). Reconstruction: the shipped scripts do not say what retail sends for script gold; the kill's
    /// packet is the one the client shows gold with. A negative delta (no shipped script has one) is refused rather than guessed.
    /// </summary>
    private void IncGold(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var delta = Sum(call, "Delta");
        if (delta < 0.0) throw BbSpellHost.Unbound("BBIncGold with a negative Delta", "W31b");
        // The 0x25 names the unit the gold came from: the one the hook heard die (Target: Voracity's kill), else the buff's owner (Parley's mark).
        var source = new[] { BbSpellHost.UnitOrNull(call.Scope.Target), BbSpellHost.UnitOrNull(call.Scope.Owner) }
            .FirstOrDefault(unit => unit is not null && unit != target);
        if (target.UnitType == UnitType.Hero && delta > 0.0) Spells.Combat.AwardHeroGold(target, delta, source);
    }

    /// <summary>
    /// <c>BBRedirectGold</c> (registered as a bare return by the client): <c>RedirectSourceVar</c> is the unit whose gold is
    /// redirected and <c>RedirectTargetVar</c> the unit that receives it. Promote's OnBuffActivate (<c>Spells/SummonerOdinPromote.luaobj</c>) names
    /// the buff's Owner (the promoted super minion) and its Attacker (the caster). The redirect is the source's own: its last hits pay the target
    /// (<c>CombatService</c>, the minion reward). A target that is not a hero clears it.
    /// <para>
    /// Reconstruction: the client's body is not recovered. The shipped text ("grant the caster gold for its kills", SpellGeneratedData.as id 18) is the
    /// evidence for what it does; retail's lifetime for the redirect is not recovered, so it lasts until the source dies.
    /// </para>
    /// </summary>
    private void RedirectGold(BbCall call)
    {
        if (Unit(call, "RedirectSourceVar") is not { } source) return;
        source.GoldRedirectTarget = Unit(call, "RedirectTargetVar") is { UnitType: UnitType.Hero } target ? target : null;
    }

    /// <summary>
    /// <c>BBGetIsZombie</c>: <c>Unit</c> (required) and the boolean output
    /// <c>Result</c>, the unit's <c>IsZombie()</c> (the object's <c>bZombie</c> flag). Yorick's Omen of Death casts its marker child only when
    /// its own caster is not a zombie. See <see cref="Entity.IsZombie"/> for what the emulator counts as one.
    /// </summary>
    private void GetIsZombie(BbCall call)
    {
        if (Unit(call, "UnitVar") is { } unit) StoreOut(call, "Result", unit.IsZombie);
    }

    /// <summary>
    /// <c>BBForceDead</c>: <c>Owner</c> (required). The body runs the server's
    /// the non-persistent buff removal (the client build asserts on it: the call is server-only), the unit's forced-death routine
    /// (the object's <c>bDead</c> set, <c>bZombie</c> and <c>bBecomeZombie</c> cleared, the death animation) and sends <c>NpcForceDead</c>
    /// (<c>0x1E</c>, five bytes) to every client. It ends a zombie life: the shipped YorickRAZombie, DeathDefiedBuff and KogMawIcathianSurprise
    /// call it from their <c>OnBuffDeactivate</c>. Here it is the finish of the zombie life the unit is in (<see cref="SpellSystem.EndZombieLife"/>:
    /// the non-persistent buffs go, the unit's work and recipient effects end, its pets die with it, <c>ForceDead</c> and the stats are sent).
    /// A unit that is already dead is left as it is (the client sets the same flags again); a living unit in no zombie life has no shipped
    /// call and no state to end, so it is refused instead of killed without a kill.
    /// </summary>
    private void ForceDead(BbCall call)
    {
        if (Unit(call, "OwnerVar") is not { } unit || !unit.Alive) return;
        if (Spells.EndZombieLife?.Invoke(unit) != true)
            throw BbSpellHost.Unbound("BBForceDead of a living unit that is in no zombie life", "W28");
    }

    /// <summary>
    /// <c>BBShowHealthBar</c>: <c>Unit</c> and <c>Show</c>, both required. The body builds <c>ShowHealthBar</c>
    /// (<c>0xD7</c>: the unit's id and the flag) and broadcasts it; the unit's receiver stores <c>show == false</c> as its hide-health-bar flag.
    /// The zombie lives hide the bar while they last (DeathDefiedBuff, KogMawIcathianSurprise) and show it again when they end. The server
    /// keeps the flag in <see cref="Entity.HealthBarHidden"/> so a client that learns the unit later is told as well.
    /// </summary>
    private void ShowHealthBar(BbCall call)
    {
        if (Unit(call, "UnitVar") is not { } unit || call.Value("Show") is null) return;
        var show = Flag(call, "Show", true);
        unit.HealthBarHidden = !show;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendShowHealthBar(unit, show);
    }

    /// <summary>
    /// <c>BBSetPARCostInc</c>: <c>SpellSlotOwner</c>, <c>SlotType</c>, <c>SpellSlot</c> (the shared slot ranges), <c>Cost</c>
    /// (the number getter) and <c>PARType</c> (default 0, mana). The body checks the slot (otherwise the script alert "slot was out of range for
    /// BBSetPARCostInc!" and nothing happens) and that the owner's resource type is <c>PARType</c> (otherwise "slot owner was not of correct PAR
    /// type"), then stores <c>Cost</c> as the slot record's first float, <c>mIncManaCost</c>, replacing what it held: it is a set, not an add. The
    /// cost the slot pays is the spell's cost plus that increment: Nocturne's Paranoia, Irelia's Transcendent Blades and the zombie lives of
    /// DeathDefiedBuff set it negative (free spells), Kog'Maw's Living Artillery and Rift Walk raise it per cast, and each puts 0 back when it
    /// ends. Only champion spell slots are bound: the shipped calls name nothing else, and no inventory or extra slot reads an increment here.
    /// The floor at zero is required: the shared current-mana-cost getter returns the
    /// replicated <c>mCurManaCost</c> of the slot and hard-asserts 0 &lt;= cost &lt;= 10000. Reconstruction: that the increment is added to the
    /// base cost is read from those scripts (<c>-cost</c> frees a spell); the composition is the server's and not in the client.
    /// </summary>
    private void SetParCostInc(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is not { } owner) return;
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        var slot = (int)Sum(call, "SpellSlot");
        var cost = Sum(call, "Cost");
        var parType = (BbParType)(int)(call.Value("PARType") as double? ?? 0.0);
        if (type is BbSlotType.InventorySlots or BbSlotType.ExtraSlots)
            throw BbSpellHost.Unbound($"BBSetPARCostInc on an {type} slot", "W28");
        // The client's LuaAlert paths: a slot outside the spellbook, or an owner whose resource is not PARType, change nothing.
        if ((uint)slot >= SpellSystem.ChampionSlotCount || _host.PowerType(owner) != parType) return;
        if (cost == 0.0) owner.SpellParCostIncrements.Remove(slot);
        else owner.SpellParCostIncrements[slot] = cost;
        if (owner.UnitType == UnitType.Hero) Spells.SendChampionStats(owner);
    }

    /// <summary>
    /// <c>BBSetSpellOffsetTarget</c>: the slot (the shared slot
    /// parser: <c>SlotNumber</c>, <c>SlotType</c>, <c>SpellbookType</c>), <c>SpellName</c> (required and unused: every shipped call passes
    /// "JunkName"), <c>Owner</c> and <c>OffsetTarget</c>; with both units the body builds <c>ChangeSlotSpellOffsetTarget</c> (<c>0x37</c>: the
    /// owner's header, the slot byte with the summoner bit, the target's id) and applies it to the server's own spellbook
    /// (the change-offset routine). The client's <c>TTYPE_OffsetAOE</c> targeter centres its area on that unit, and
    /// the script reads the same unit for its damage. Orianna's Dissonance and Shockwave (slots 1 and 3) are pointed at her ball, or at the
    /// ally who carries it, this way. The summoner spellbook has no shipped call and is not bound.
    /// </summary>
    private void SetSpellOffsetTarget(BbCall call)
    {
        var type = SlotTypeOf(call, "BBSetSpellOffsetTarget", required: false);
        var slot = StoredSlot("BBSetSpellOffsetTarget", type, (int)Sum(call, "SlotNumber"));
        if (call.Value("SpellName") is not string) throw new BbRuntimeException("BBSetSpellOffsetTarget has no SpellName");
        if (Unit(call, "OwnerVar") is not { } owner || Unit(call, "OffsetTargetVar") is not { } offsetTarget) return;
        if (SummonerBook(call, "SpellbookType")) throw BbSpellHost.Unbound("BBSetSpellOffsetTarget on the summoner spellbook", "W28");
        owner.SpellOffsetTargets[slot] = offsetTarget.NetId;
        if (Spells.Effects.IsReferenceReady(owner)) Spells.Effects.SendSpellOffsetTarget(owner, slot, offsetTarget.NetId);
    }

    /// <summary>
    /// <c>BBStopChanneling</c>: <c>Caster</c>, <c>StopCondition</c> and
    /// <c>StopSource</c>, all required. Unless the source is <c>TimeCompleted</c> (1), <c>Animation</c> (2) or <c>Unknown</c> (11) the body has the
    /// caster's spellbook stop its channelled cast object (the spellbook's stop-cast routine) and always clears the caster's channelling
    /// script (<c>channelingInfo.Script</c>). The stop condition only tells the channel's own hooks
    /// (<c>ChannelingSuccessStop</c> or <c>ChannelingCancelStop</c>) which way it ended: the reason carries it ("BBStopChanneling Success ..."), and
    /// the script channels of the BuildingBlocks engine (<see cref="BbScriptChannels"/>) run the matching hook. The channel services the server has
    /// are the recall and the champion hooks' (<see cref="SpellSystem.StopChanneling"/>), which Zhonya's Hourglass already uses. Taunt, Zhonya's Hourglass, the zombie lives of Yorick's closure and the recall and teleport scripts call it.
    /// </summary>
    private void StopChanneling(BbCall call)
    {
        if (Unit(call, "CasterVar") is not { } caster || call.Value("StopCondition") is null || call.Value("StopSource") is not double source) return;
        var stopSource = (BbChannelingStopSource)(int)source;
        if (stopSource is BbChannelingStopSource.ChannelingStopSource_TimeCompleted or BbChannelingStopSource.ChannelingStopSource_Animation
            or BbChannelingStopSource.ChannelingStopSource_Unknown) return;
        var succeeded = call.Value("StopCondition") is double condition &&
                        (BbChannelingStopCondition)(int)condition == BbChannelingStopCondition.ChannelingStopCondition_Success;
        Spells.StopChanneling(caster, $"BBStopChanneling {(succeeded ? "Success" : "Cancel")} {stopSource}");
    }
}
