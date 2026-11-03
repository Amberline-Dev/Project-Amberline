using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Simulation.Items;

/// <summary>
/// The cooldown an item's active leaves on its inventory slots, read from the shipped data in this order (Reconstruction: the client's
/// <c>DoCast_ClientAndServer</c> has no <c>ApplyCostsOfSpell</c> call, so the cost-and-cooldown step is the server's and its
/// order against the script is not in the client; the order below is another build's, which is not evidence for this client):
/// <c>ApplyCostsOfSpell</c> first sets the slot to the
/// cast's cooldown, the item spell inibin's <c>Cooldown&lt;SpellLevel&gt;</c> (the client displays the same key; an item's
/// <c>SpellLevel</c> is 1 unless the record says otherwise), then the item script runs and calls
/// <c>BBSetSlotSpellCooldownTimeVer2</c> for every inventory slot that holds the item, which overwrites it unconditionally
/// (the slot's cooldown setter plus the 0x8A <c>CharSetCooldown</c>). So a script that sets a cooldown wins over
/// its inibin (Executioner's Calling "Mourning" sets 20 against <c>Cooldown1..5</c> = 40, Quicksilver Sash 90 against 0, Wriggle's
/// Lantern a 180 held in <c>NewCooldown</c>), and one that sets none (the wards, the potions and elixirs) keeps the inibin value.
/// <para>
/// No cooldown reduction is applied to either kind. A script's value is unconditional: 66 of the 76 shipped Spells, Buffs and Characters
/// scripts that read <c>GetPercentCooldownMod</c> also set a slot cooldown and scale it themselves, and none of the 38 item spells is one of
/// them (F3 audit, 18 October 2026). Reconstruction for the inibin fallback (potions 1 s, elixirs 12 s, Oracle items 1 s, wards 0): the 126 client has no
/// reader of the stat for a cast (the replicated <c>mPercentCooldownMod</c> is only read by its Lua helpers), and the cast builder that
/// fills <c>castInfo.cooldown</c> is server code in no binary we hold. The only client-side trace is the tooltip: the hero-spell helper
/// (its fill step) shows <c>(PercentCooldownMod + 1) * GetSpellCooldownTime</c>, the summoner-spell helper
/// (its fill step) the bare cooldown, and the item helper shows no cooldown. Item slots are neither of the hero slots, so
/// they are treated like the summoner spells; the difference would be 12 s against 7.2 s at the 40 % cap on the two elixirs.
/// </para>
/// </summary>
public sealed class ItemCooldowns
{
    private readonly GameData _data;
    private readonly ItemCatalog _catalog;
    private readonly Dictionary<int, double> _cache = new();

    public ItemCooldowns(GameData data, ItemCatalog catalog)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The cooldown in seconds, before any "cooldowns off" cheat. Throws for an item without an active spell.</summary>
    public double Of(int itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached)) return cached;
        var item = _catalog.Get(itemId);
        if (item is null || item.SpellName.Length == 0)
            throw new InvalidDataException($"item {itemId} has no active spell");
        var level = Math.Clamp(item.SpellLevel, 1, SpellData.MaxSpellLevel);
        return _cache[itemId] = ScriptCooldown(item.SpellName) ?? _data.Spell(item.SpellName, level).Cooldowns[level - 1];
    }

    /// <summary>
    /// The first <c>BBSetSlotSpellCooldownTime(Ver2)</c> of the spell script's cast hooks: its <c>Src</c> number plus, when it names one,
    /// the <c>SrcVar</c> the same hook assigned with <c>BBSetVarInTable</c> (Wriggle's Lantern). Null when the script sets none or is missing.
    /// </summary>
    private double? ScriptCooldown(string spellName)
    {
        var script = BbScriptLoader.Load(_data.Paths, $"Spells/{spellName}.luaobj").Script;
        if (script is null) return null;
        var blocks = script.Walk().Where(item => item.Hook is "SelfExecute" or "TargetExecute").ToArray();
        foreach (var (hook, block) in blocks)
        {
            if (block.Function is not ("BBSetSlotSpellCooldownTimeVer2" or "BBSetSlotSpellCooldownTime")) continue;
            var seconds = block.Number("Src") ?? 0.0;
            if (block.Text("SrcVar") is { Length: > 0 } variable)
            {
                var assigned = blocks.Where(other => other.Hook == hook && other.Block.Function == "BBSetVarInTable" &&
                    other.Block.Text("DestVar") == variable).Select(other => other.Block.Number("SrcValue")).FirstOrDefault();
                if (assigned is null) return null;
                seconds += assigned.Value;
            }
            return seconds;
        }
        return null;
    }
}
