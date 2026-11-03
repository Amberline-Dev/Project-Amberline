using System.Text.Json;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Tools.Audits;

public sealed record ItemSupportRow(int ItemId, IReadOnlyList<string> Signals,
    IReadOnlyList<string> SupportTags);

public sealed record ItemSupportAuditReport(
    int CatalogCount,
    int BehaviorItemCount,
    IReadOnlyList<string> ModifierNames,
    IReadOnlyList<string> ModifierGaps,
    IReadOnlyList<int> BehaviorGaps,
    IReadOnlyList<int> ActiveGaps,
    IReadOnlyList<ItemSupportRow> Rows)
{
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = indented });
}

/// <summary>Audits item metadata and source-backed runtime registrations.</summary>
public static class ItemSupportAudit
{
    private static readonly IReadOnlySet<string> SupportedGenericModifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "FlatArmorMod", "FlatCritChanceMod", "FlatDodgeMod", "FlatHPPoolMod",
        "FlatHPRegenMod", "FlatMPPoolMod", "FlatMPRegenMod", "FlatMagicDamageMod",
        "FlatMovementSpeedMod", "FlatPhysicalDamageMod", "FlatSpellBlockMod",
        "PercentAttackSpeedMod", "PercentHardnessMod", "PercentLifeStealMod",
        "PercentMovementSpeedMod", "PercentSpellVampMod"
    };

    private static readonly IReadOnlySet<int> ConsumableIds =
        new HashSet<int>([2003, 2004, 2037, 2038, 2039, 2042, 2043, 2044, 2047]);
    // The seven whose use the inventory consume owns (the restore potions, the five elixirs and Oracle's). The wards
    // of ConsumableIds are cast the same way any other active is and keep the active tag.
    private static readonly IReadOnlySet<int> ConsumedIds =
        new HashSet<int>([2003, 2004, 2037, 2038, 2039, 2042, 2047]);
    private static readonly HashSet<int> TenacityItems = [3111, 3170, 3172, 3173];

    public static ItemSupportAuditReport Audit(GameDataPaths? paths = null)
    {
        var root = paths ?? GameDataPaths.Default;
        var catalog = new ItemCatalog(root);
        var ids = Directory.Exists(root.Data("Items"))
            ? Directory.EnumerateFiles(root.Data("Items"), "*.inibin")
                .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : 0)
                .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray()
            : Array.Empty<int>();
        // The audited population is the shop records: the ones that ship an InStore flag. Since the loader reads an absent key as the
        // stock default (InStore 1), the rune and placeholder records (5xxx, 8xxx, 42xx, 10001/10002) are InStore too, but no shop lists them.
        var items = ids.Select(catalog.Get).OfType<ItemData>()
            .Where(item => item.InStore && item.HasShippedStoreFlag)
            .OrderBy(item => item.ItemId).ToArray();
        var factsById = items.ToDictionary(item => item.ItemId, item => ReadSourceFacts(root, item));
        // The active tag is the item runtime's own cast path (batch 149 step 4): an item has an active when its own
        // SpellName names a shipped Spells/<name>.luaobj with a cast body (SelfExecute / TargetExecute), which is
        // exactly ItemService.HasCastHook's membership test, so no hand-written id table says which items have an
        // active any more. The item's *signals* are wider: `active/use` also counts a cast body in a spell the item
        // preloads for another purpose. 3022 Frozen Mallet, 3077 Tiamat, 3082 Warden's Mail and 3106 Madred's Razors
        // preload judicatorrighteousfury / randuinsomen / wrigglelantern (other items' actives) and have no SpellName
        // of their own — the runtime refuses a use of them, so they must not carry the tag (batch 166).
        var activeIds = factsById.Values.Where(facts => facts.CastBody)
            .Select(facts => facts.ItemId).Where(id => !ConsumedIds.Contains(id)).ToHashSet();
        var behaviorIds = factsById.Values.Where(facts => facts.HasSource)
            .Select(facts => facts.ItemId).OrderBy(id => id).ToArray();
        var rows = new List<ItemSupportRow>(items.Length);
        foreach (var item in items)
        {
            var facts = factsById[item.ItemId];
            var signals = Signals(facts);
            var tags = Tags(item, activeIds);
            rows.Add(new ItemSupportRow(item.ItemId, signals, tags));
        }
        var modifierNames = items.SelectMany(item => item.Modifiers).Select(modifier => modifier.Name)
            .ToHashSet(StringComparer.Ordinal);
        var registered = ImplementationRegistry;
        var behaviorGaps = behaviorIds.Where(id => !registered.Contains(id)).ToArray();
        return new ItemSupportAuditReport(items.Length, behaviorIds.Length,
            modifierNames.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            modifierNames.Except(SupportedGenericModifiers, StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            behaviorGaps,
            behaviorGaps.Where(id => factsById[id].Signals.Contains("active/use") &&
                !activeIds.Contains(id) && !ConsumableIds.Contains(id)).ToArray()
                .OrderBy(x => x).ToArray(), rows);
    }

    private static IReadOnlyList<string> Signals(SourceFacts facts)
    {
        var result = new List<string>();
        var joined = string.Join(" ", facts.Hooks);
        if (joined.Contains("SelfExecuteBuildingBlocks", StringComparison.Ordinal) ||
            joined.Contains("TargetExecuteBuildingBlocks", StringComparison.Ordinal)) result.Add("active/use");
        if (joined.Contains("ItemOnHitUnit", StringComparison.Ordinal) ||
            joined.Contains("BuffOnHitUnit", StringComparison.Ordinal)) result.Add("on-hit");
        if (joined.Contains("ItemOnSpellCast", StringComparison.Ordinal)) result.Add("spell-trigger");
        if (joined.Contains("ItemOnDealDamage", StringComparison.Ordinal)) result.Add("damage-trigger");
        if (joined.Contains("BeingHit", StringComparison.Ordinal) ||
            joined.Contains("PreDamage", StringComparison.Ordinal)) result.Add("reactive-defense");
        if (joined.Contains("ItemOnKill", StringComparison.Ordinal) ||
            joined.Contains("ItemOnAssist", StringComparison.Ordinal) ||
            joined.Contains("ItemOnDeath", StringComparison.Ordinal)) result.Add("kill/death-stacks");
        if (facts.Functions.Contains("BBForEachUnitInTargetArea")) result.Add("aura/area");
        if (facts.Functions.Contains("BBCreateUnit")) result.Add("creates-unit");
        if (facts.Functions.Contains("BBApplyDamage")) result.Add("damage");
        if (facts.Functions.Contains("BBSpellBuffAdd") || facts.Functions.Contains("BBIncStat")) result.Add("buff/stat");
        return result.Distinct(StringComparer.Ordinal).OrderBy(signal => signal, StringComparer.Ordinal).ToArray();
    }

    // The scripted-static tag is gone with the last Inventory.ScriptedStaticModifiers row (batch 149 step 4): no item
    // carries a hand-written static row any more, so the tags are the inibin rows, the shipped source's own signals and
    // the hand-written behaviour registries below. The 23 audits-corpus.json rows that still listed the tag lost it in
    // batch 166 — it named a hand-written inventory row that no longer exists, so keeping it would be a tag the audit
    // cannot produce. item-catalog-audit.md keeps the tag in its snapshot and says so.
    private static IReadOnlyList<string> Tags(ItemData item, HashSet<int> activeIds)
    {
        var tags = new List<string>();
        if (item.Modifiers.Count != 0) tags.Add("generic-stats");
        if (TenacityItems.Contains(item.ItemId) || item.Modifier("PercentHardnessMod") != 0) tags.Add("tenacity");
        if (AuraTagIds.Contains(item.ItemId)) tags.Add("aura");
        if (OnHitTagIds.Contains(item.ItemId)) tags.Add("on-hit");
        if (ReactiveTagIds.Contains(item.ItemId)) tags.Add("reactive-defense");
        if (ConsumableIds.Contains(item.ItemId)) tags.Add("consumable");
        if (activeIds.Contains(item.ItemId)) tags.Add("active");
        if (ImplementationRegistry.Contains(item.ItemId)) tags.Add("implementation-registered");
        return tags.ToArray();
    }

    private static SourceFacts ReadSourceFacts(GameDataPaths paths, ItemData item)
    {
        var sources = LoadSources(paths, item);
        if (sources.Count == 0) return SourceFacts.Empty with { ItemId = item.ItemId };
        var blocks = new HashSet<string>(StringComparer.Ordinal);
        var functions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loaded in sources)
        {
            foreach (var key in loaded.Globals.Keys.Where(key =>
                         key.EndsWith("BuildingBlocks", StringComparison.Ordinal)))
                blocks.Add(key);
            foreach (var value in AuditLuaValues.Walk(loaded.Globals.Values))
            {
                if (value is not LuaValue.Map map) continue;
                foreach (var pair in map.Values)
                {
                    if (pair.Key is LuaValue.String key && key.Value.EndsWith("BuildingBlocks", StringComparison.Ordinal))
                        blocks.Add(key.Value);
                }
                var function = AuditLuaValues.Text(AuditLuaValues.Get(value, "Function"));
                if (!string.IsNullOrEmpty(function)) functions.Add(function);
            }
        }
        var facts = new SourceFacts(item.ItemId, true, blocks, functions, Array.Empty<string>(),
            CastBody: HasCastBody(paths, item));
        return facts with { Signals = Signals(facts) };
    }

    /// <summary>
    /// Whether the item's own <c>SpellName</c> names a shipped <c>Spells/&lt;name&gt;.luaobj</c> with a cast body:
    /// <c>ItemService.HasCastHook</c>, the runtime's membership test of the active group. The item script's own body
    /// is not this question — an active lives in the spell script the inibin names — and a preloaded spell is another
    /// item's script (3022 preloads <c>judicatorrighteousfury</c>, 3082 <c>randuinsomen</c>), not this item's active.
    /// <para>
    /// It asks the question through <see cref="BbScriptLoader"/>, the loader the runtime uses, rather than testing the
    /// extracted global keys: the loader only makes an event of a table that is actually a block table, so a bare
    /// <c>SelfExecuteBuildingBlocks = 0</c> is no event and <c>HasCastHook</c> refuses the use. A key test would have
    /// tagged that item <c>active</c> anyway — the drift between the two tests is what batch 166 was for. A script
    /// that does not load in full is not a cast body either: the runtime treats a null <c>Script</c> as no hook.
    /// </para>
    /// </summary>
    private static bool HasCastBody(GameDataPaths paths, ItemData item)
    {
        if (string.IsNullOrWhiteSpace(item.SpellName)) return false;
        var relative = $"Spells/{item.SpellName}.luaobj";
        if (!File.Exists(paths.Data(relative))) return false;
        var script = BbScriptLoader.Load(paths, relative).Script;
        return script is not null &&
            (script.Event("SelfExecute") is not null || script.Event("TargetExecute") is not null);
    }

    private static IReadOnlyList<LuaLoadResult> LoadSources(GameDataPaths paths, ItemData item)
    {
        var sources = new List<LuaLoadResult>();
        var pending = new Queue<string>();
        var itemRelative = $"Items/{item.ItemId}.luaobj";
        if (File.Exists(paths.Data(itemRelative)))
            sources.Add(RequireSuccessfulSource(itemRelative, LuaObjectReader.LoadData(paths, itemRelative)));
        if (!string.IsNullOrWhiteSpace(item.SpellName)) pending.Enqueue(item.SpellName);
        foreach (var source in sources)
            EnqueuePreloaded(source, pending);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count != 0)
        {
            var spellName = pending.Dequeue();
            if (!seen.Add(spellName)) continue;
            var relative = $"Spells/{spellName}.luaobj";
            if (!File.Exists(paths.Data(relative))) continue;
            var source = RequireSuccessfulSource(relative, LuaObjectReader.LoadData(paths, relative));
            sources.Add(source);
            EnqueuePreloaded(source, pending);
        }
        return sources;
    }

    private static LuaLoadResult RequireSuccessfulSource(string relativePath, LuaLoadResult source)
    {
        if (source.Status != LuaLoadStatus.Success)
            throw new InvalidDataException(
                $"Lua source '{relativePath}' did not load successfully ({source.Status}): " +
                (source.Error ?? source.Reason ?? "unknown parse error"));
        return source;
    }

    private static void EnqueuePreloaded(LuaLoadResult source, Queue<string> pending)
    {
        foreach (var value in AuditLuaValues.Walk(source.Globals.Values))
        {
            if (value is not LuaValue.Map ||
                !string.Equals(AuditLuaValues.Text(AuditLuaValues.Get(value, "Function")),
                    "BBPreloadSpell", StringComparison.Ordinal)) continue;
            var name = AuditLuaValues.Text(AuditLuaValues.Get(AuditLuaValues.Get(value, "Params")!, "Name"));
            if (!string.IsNullOrWhiteSpace(name)) pending.Enqueue(name);
        }
    }

    private static readonly IReadOnlySet<int> ReactiveTagIds =
        new HashSet<int>([3026, 3075, 3082, 3117, 3155, 3180, 3143]);

    private static readonly IReadOnlySet<int> AuraTagIds =
        new HashSet<int>([3001, 3037, 3050, 3097, 3099, 3105, 3110, 3152]);

    private static readonly IReadOnlySet<int> OnHitTagIds =
        new HashSet<int>([3004, 3022, 3044, 3057, 3071, 3077, 3078, 3091, 3100, 3106, 3114, 3123, 3124, 3126, 3131, 3154, 3178, 3181, 3184, 3185, 3186]);

    private static readonly IReadOnlySet<int> ImplementationRegistry = new HashSet<int>([
        1062, 1063, 2003, 2004, 2037, 2038, 2039, 2042, 2043, 2044, 2047,
        3001, 3003, 3004, 3005, 3010, 3020, 3022, 3024, 3026, 3027, 3028,
        3031, 3035, 3037, 3041, 3044, 3050, 3057, 3065, 3067, 3068, 3069,
        3089, 3091, 3093, 3096, 3097, 3098, 3099, 3070, 3071, 3072, 3075,
        3077, 3078, 3082, 3083, 3100, 3101, 3102, 3105, 3106, 3108, 3109,
        3110, 3111, 3114, 3115, 3116, 3117, 3123, 3124, 3126, 3128, 3131,
        3132, 3134, 3135, 3136, 3138, 3140, 3141, 3142, 3143, 3144, 3145,
        3146, 3152, 3154, 3155, 3157, 3158, 3165, 3170, 3172, 3173, 3178,
        3180, 3181, 3183, 3184, 3185, 3186, 3187]);

    private sealed record SourceFacts(int ItemId, bool HasSource, HashSet<string> Hooks,
        HashSet<string> Functions, IReadOnlyList<string> Signals, bool CastBody = false)
    {
        public static SourceFacts Empty { get; } = new(0, false, [], [], Array.Empty<string>());
    }
    
}
