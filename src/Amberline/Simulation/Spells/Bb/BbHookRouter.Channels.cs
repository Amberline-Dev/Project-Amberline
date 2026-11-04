using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Hook dispatch of the channelled spells (Phase 3, W25): <c>ChannelingStart</c>, <c>ChannelingUpdateStats</c>, <c>ChannelingUpdateActions</c>, <c>ChannelingSuccessStop</c>,
/// <c>ChannelingCancelStop</c> and <c>ChannelingStop</c> of a spell script whose <c>ChannelDuration</c> global makes it a channel
/// (<see cref="BbScriptChannels"/>), the cancel entry the hero hooks use, and the toggled-slot read of the stats snapshot. The hook names are the
/// client's (it registers <c>ChannelingStart</c> and <c>ChannelingStop</c>); when the server runs each is a
/// Reconstruction.
/// </summary>
internal sealed partial class BbHookRouter
{
    /// <summary>
    /// A hook of a channelled spell's script. Owner, Attacker and Caster are the channelling hero, Target the unit the cast was aimed at (the hero
    /// itself for a self cast: <c>ChannelingStart</c> of Janna's Monsoon reads the hero's ability power as Owner), Level the rank the spell was
    /// cast at, AccumTime the seconds the channel has run, CastPosition the point it was cast at; the script is the ambient spell, so its flags decide whether what it does pops a shield.
    /// The buffs the hook adds take the cast's slot and rank for their recap origin.
    /// </summary>
    public void RunChanneling(BbChannel channel, string hook)
    {
        if (channel.Script.Event(hook) is null) return;
        var caster = channel.Caster;
        var scope = NewScope(caster, caster, channel.Target ?? caster, caster, channel.Rank, channel.InstanceVars);
        scope.SpellVars = SpellVars(caster, channel.Slot, channel.Script);
        scope.Vars["AccumTime"] = channel.Elapsed;
        scope.Vars["CastPosition"] = channel.InstanceVars["CastPosition"];
        var frame = SpellFrame(hook, channel.Script, caster, channel.Slot, channel.Rank, channel.ManaCost, channel.At, channel.End);
        RunInSpell(channel.Script, hook, scope, frame);
    }

    /// <summary>
    /// A fresh order or an accepted cast of a hero (<c>IChampionSpellHooks.CancelChannel</c>) or a script's <c>BBStopChanneling</c>: the channel the
    /// hero runs, if any, ends with the matching stop source, as a cancel unless the script said <c>StopCondition_Success</c>.
    /// </summary>
    public void OnCancelChannel(Entity hero, string reason) =>
        Guard("sink", "OnCancelChannel", () =>
        {
            if (_host.Channels.CancelWindup(hero)) return;
            if (_host.Channels.Of(hero) is null) return;
            if (reason is "order" or "spell request") _host.Channels.CancelByPlayer(hero, reason);
            else _host.Channels.Stop(hero, BbScriptChannels.ConditionOf(reason), BbScriptChannels.SourceOf(reason));
        });

    /// <summary>
    /// Whether the hero's slot is toggled on, the client's <c>eToggleOn</c> (the slot's state field = 2): the slot's spell is a toggle spell
    /// (<c>SpellData IsToggleSpell</c>) and one of the hero's script buffs names the slot in <c>SpellToggleSlot</c> (a slot number from 1;
    /// the slot read returns the global minus one). the client's buff-activate routine
    /// sets the state to on (2) when such a buff activates and <c>OnBuffDeactivate</c> to off (1) when it goes.
    /// Batch 331: a cast of such a slot costs nothing. The shared cost routine,
    /// which <c>ApplyCostsOfSpell</c> runs from the cast routine, spends the cast's PAR only when the slot is
    /// not on, and the can-use check never refuses an on slot for PAR. The engine's earlier extra condition (the script had
    /// switched the slot's targeting, Howling Gale's Self) is gone: Poison Trail, Ricochet, Frost Shot and Radiance keep their targeting and
    /// switch off for free too.
    /// </summary>
    public bool IsSpellToggledOn(Entity hero, int slot)
    {
        if (!IsToggleSpell(hero, slot)) return false;
        foreach (var buff in hero.Buffs)
            if (Buffs.InstanceOf(buff) is { } instance && instance.Script.Global("SpellToggleSlot") is BbValue.Number { Value: var toggled } &&
                (int)toggled - 1 == slot) return true;
        return false;
    }

    private bool IsToggleSpell(Entity hero, int slot)
    {
        // A host's SpellNameOf answers "" for a slot it holds no spell in: the loadout host names no slots at all
        // (BbLoadoutHooks.BindMatch), the item host's ItemName is "" for an empty or unconverted copy, and a champion
        // table can hold an empty Data*SpellN. That is the same "no spell here" an unlisted name gives (the InibinException
        // below), so a blank name must not reach GameData.Spell, which refuses it with ArgumentException. This read runs
        // outside the hook Guard (hero stats replication, cast validation), so before this guard an unnamed slot took the
        // match down: QSS used while Janna's forced move was on the unit, since batch 144 added the loadout and item hook
        // sinks whose hosts name no champion slot.
        if (_host.SpellNameOf(hero, slot) is not { Length: > 0 } name) return false;
        try { return _host.Spells.Data.Spell(name, 5).IsToggleSpell; }
        catch (Amberline.Data.InibinException) { return false; }
    }
}
