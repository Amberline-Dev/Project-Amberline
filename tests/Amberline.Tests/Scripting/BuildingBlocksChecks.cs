using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Tests;

/// <summary>Milestone M0 of the BuildingBlocks engine: the typed script model, loader and closure resolver over the shipped corpus.</summary>
public static class BuildingBlocksChecks
{
    [Check("scripting.bb-corpus-loads")]
    public static void EveryShippedScriptLoads()
    {
        var paths = GameDataPaths.Default;
        var resolver = new BbScriptResolver(paths);
        var files = ScriptFiles(paths);
        TestAssert.Equal(2277, files.Count, "shipped script files under Spells, Characters/*/Scripts, Items, Talents, BuildingBlocks");

        var problems = new List<string>();
        var bearing = 0;
        var blocks = 0;
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var withoutPreload = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in files)
        {
            var result = resolver.Load(relative);
            if (!result.IsClean)
            {
                problems.Add($"{relative}: {result.Status} " + string.Join("; ", result.Diagnostics.Take(2).Select(d => $"{d.Kind} {d.Path}")));
                continue;
            }
            var script = result.Script!;
            if (relative.StartsWith("BuildingBlocks/", StringComparison.Ordinal)) continue; // function libraries, not scripts
            if (script.HasBlocks) bearing++;
            foreach (var (hook, block) in script.Walk())
            {
                blocks++;
                functions.Add(block.Function);
                if (hook != BbScript.PreLoadHook) withoutPreload.Add(block.Function);
            }
        }

        TestAssert.True(problems.Count == 0, "scripts that did not load cleanly: " + string.Join(" | ", problems.Take(5)));
        // Pinned from the shipped 1.0.0.126 corpus; the roster study counted 1998 BB-bearing files and 4136 preload blocks.
        TestAssert.Equal(1998, bearing, "block-bearing scripts");
        TestAssert.Equal(35847, blocks, "block invocations");
        TestAssert.Equal(179, functions.Count, "distinct block functions");
        TestAssert.Equal(176, withoutPreload.Count, "distinct block functions outside PreLoad");
    }

    [Check("scripting.bb-annie-disintegrate-shape")]
    public static void AnnieDisintegrateMatchesTheShippedScript()
    {
        var paths = GameDataPaths.Default;
        var load = BbScriptLoader.Load(paths, "Spells/Disintegrate.luaobj");
        TestAssert.True(load.IsClean, "Disintegrate loads cleanly");
        var script = load.Script!;
        TestAssert.Equal("Disintegrate", script.Name);
        TestAssert.Equal("Spells/Disintegrate.luaobj", script.SourcePath);

        // Scalar globals stay generic name/value pairs in file order.
        TestAssert.Equal(new[] { "NotSingleTargetSpell", "DoesntTriggerSpellCasts", "IsDamagingSpell", "SpellDamageRatio" },
            script.Globals.Select(g => g.Key).ToArray());
        TestAssert.Equal(false, ((BbValue.Bool)script.Global("NotSingleTargetSpell")!).Value);
        TestAssert.Equal(true, ((BbValue.Bool)script.Global("IsDamagingSpell")!).Value);
        TestAssert.Equal(1.0, ((BbValue.Number)script.Global("SpellDamageRatio")!).Value);
        TestAssert.Equal(new[] { "OnBuffActivate", "BuffOnUpdateActions", "BuffOnDeath", "TargetExecute" },
            script.Events.Select(e => e.Hook).ToArray());

        var target = script.Event("TargetExecute")!.Blocks;
        TestAssert.Equal(new[]
            {
                "BBGetBuffCountFromCaster", "BBIf", "BBGetCastInfo", "BBSetVarInTable", "BBSpellBuffAdd", "BBApplyDamage",
                "BBSpellBuffAdd", "BBGetTeamID", "BBGetSkinID", "BBIf", "BBElse",
            },
            target.Select(b => b.Function).ToArray());

        // Pyromania stun: the first BBIf carries the stun and the marker removal as sub-blocks.
        TestAssert.Equal(new[] { "BBApplyStun", "BBSpellBuffRemove" }, target[1].SubBlocks.Select(b => b.Function).ToArray());
        TestAssert.Equal("StunDuration", target[1].SubBlocks[0].Text("DurationVar"));
        TestAssert.Equal("Pyromania_particle", target[0].Text("BuffName"));

        // The shipped damage table: 85/125/165/205/245 at 0.7 of ability power, magic damage from a spell.
        var damage = target[5];
        TestAssert.Equal("Attacker", damage.Text("AttackerVar"));
        TestAssert.Equal("Target", damage.Text("TargetVar"));
        var table = (BbValue.Table)damage.Param("DamageByLevel")!;
        var levels = table.Items.Select(item => ((BbValue.Number)item).Value).ToArray();
        TestAssert.Equal(new[] { 85.0, 125.0, 165.0, 205.0, 245.0 }, levels);
        TestAssert.Equal(0.7, damage.Number("SpellDamageRatio"));
        TestAssert.Equal(1.0, damage.Number("PercentOfAttack"));
        TestAssert.Equal("MAGIC_DAMAGE", ((BbValue.Reference)damage.Param("DamageType")!).Name);
        TestAssert.Equal("DAMAGESOURCE_SPELL", ((BbValue.Reference)damage.Param("SourceDamageType")!).Name);
        // Independent source: the spell inibin holds the same table and coefficient.
        var data = new GameData(paths).Spell("Disintegrate");
        for (var rank = 1; rank <= levels.Length; rank++)
            TestAssert.Near(levels[rank - 1], data.EffectNumber(1, rank), 1e-6, $"rank {rank} damage");
        TestAssert.Near(data.Coefficient, damage.Number("SpellDamageRatio")!.Value, 1e-4);

        // Pyromania stack: 5 stacks, renewing, 25000 s (the "until removed" sentinel).
        var stack = target[6];
        TestAssert.Equal("Pyromania", stack.Text("BuffName"));
        TestAssert.Equal(5.0, stack.Number("MaxStack"));
        TestAssert.Equal(25000.0, stack.Number("Duration"));
        TestAssert.Equal("BUFF_STACKS_AND_RENEWS", ((BbValue.Reference)stack.Param("BuffAddType")!).Name);
        TestAssert.Equal("BUFF_CombatEnchancer", ((BbValue.Reference)stack.Param("BuffType")!).Name);

        // Frost skin (id 5) swaps the hit particles; the else branch keeps the default pair.
        TestAssert.Equal(2, target[9].SubBlocks.Count);
        TestAssert.Equal("DisintegrateHit_tar_frost.troy", target[9].SubBlocks[0].Text("EffectName"));
        TestAssert.Equal(new[] { "DisintegrateHit_tar.troy", "Disintegrate_hit.troy" },
            target[10].SubBlocks.Select(b => b.Text("EffectName")!).ToArray());
        TestAssert.Equal(0, target[10].Params.Count, "BBElse has an empty Params table");

        // Other tables and the preloads.
        TestAssert.Equal(new[] { "BBRequireVar" }, script.Event("OnBuffActivate")!.Blocks.Select(b => b.Function).ToArray());
        TestAssert.Equal(new[] { "BBSpellBuffRemoveCurrent" }, script.Event("BuffOnUpdateActions")!.Blocks.Select(b => b.Function).ToArray());
        TestAssert.Equal(new[] { "BBIf", "BBElse" }, script.Event("BuffOnDeath")!.Blocks.Select(b => b.Function).ToArray());
        TestAssert.Equal(new[]
            {
                "BBPreloadSpell", "BBPreloadSpell", "BBPreloadParticle", "BBPreloadParticle", "BBPreloadParticle", "BBPreloadParticle",
            },
            script.PreLoad.Select(b => b.Function).ToArray());
        TestAssert.Equal(new[] { "pyromania_particle", "pyromania" },
            script.PreLoad.Take(2).Select(b => b.Text("Name")!).ToArray());
        TestAssert.Equal(28, script.Walk().Count(), "blocks in Disintegrate, preloads included");
    }

    [Check("scripting.bb-closure-resolution")]
    public static void ClosuresResolveLikeTheRosterStudy()
    {
        var resolver = new BbScriptResolver(GameDataPaths.Default);

        // Batch 203: APBonusDamageToTowers and DisconnectTarget are closure scripts the engine runs now (the champion's CharOnActivate adds the first).

        var annie = resolver.Resolve("Annie");
        TestAssert.Equal(0, annie.Diagnostics.Count);
        TestAssert.Equal(new[]
            {
                "APBonusDamageToTowers", "ChampionChampionDelta", "CharScriptAnnie", "DisconnectTarget", "DisconnectTimer",
                "Disintegrate", "FortifyBuff", "FortifyCheck", "Incinerate", "InfernalGuardian", "InfernalGuardianBurning",
                "InfernalGuardianTimer", "MoltenShield", "NegativeChampionDelta", "PositiveChampionDelta", "Pyromania",
                "Pyromania_marker", "Pyromania_particle",
            },
            annie.ScriptNames.Order(StringComparer.Ordinal).ToArray());
        TestAssert.Equal("CharScriptAnnie", annie.Scripts[0].Name);
        TestAssert.Equal(BbScriptRole.Character, annie.Scripts[0].Role);
        Role(annie, "Disintegrate", BbScriptRole.Spell);
        Role(annie, "InfernalGuardian", BbScriptRole.Spell);
        Role(annie, "InfernalGuardianBurning", BbScriptRole.Referenced);
        // The inibin passive name is a display-style string, not a script file.
        TestAssert.Equal(1, annie.Dangling.Count);
        TestAssert.Equal("Pyromania Marker", annie.Dangling[0].Name);
        TestAssert.Equal(BbReferenceKind.Passive, annie.Dangling[0].Kind);
        TestAssert.Equal(0, annie.DataOnly.Count);

        var soraka = resolver.Resolve("Soraka");
        TestAssert.Equal(0, soraka.Diagnostics.Count);
        TestAssert.Equal(new[]
            {
                "APBonusDamageToTowers", "AstralBlessing", "ChampionChampionDelta", "CharScriptSoraka", "ConsecrationAura",
                "ConsecrationAuraNoParticle", "DisconnectTarget", "DisconnectTimer", "FortifyBuff", "FortifyCheck", "Infuse",
                "NegativeChampionDelta", "PositiveChampionDelta", "Starcall", "StarcallDamage", "Wish",
            },
            soraka.ScriptNames.Order(StringComparer.Ordinal).ToArray());
        TestAssert.Equal(new[] { "Consecration Self" }, soraka.Dangling.Select(d => d.Name).ToArray());
        TestAssert.Equal(soraka.Scripts.Count, soraka.ScriptNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Roster-wide pins (patch 126): 84 champions; dangling names and script-less spells.
        var champions = resolver.DiscoverChampions();
        TestAssert.Equal(84, champions.Count);
        var closures = champions.Select(resolver.Resolve).ToArray();
        TestAssert.Equal(0, closures.Sum(c => c.Diagnostics.Count));
        var sizes = closures.Select(c => c.Scripts.Count).Order().ToArray();
        TestAssert.Equal(16, sizes[0]);
        TestAssert.Equal(25, sizes[sizes.Length / 2]);   // 24 before the character references (a pushed or spawned character's spells joined five closures)
        TestAssert.Equal(80, sizes[^1]);
        TestAssert.Equal("Yorick", closures.MaxBy(c => c.Scripts.Count)!.Champion);
        var byKind = closures.SelectMany(c => c.Dangling).GroupBy(d => d.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        TestAssert.Equal(15, byKind[BbReferenceKind.ExtraSpell]);   // child spells with neither script nor data
        TestAssert.Equal(28, byKind[BbReferenceKind.Passive]);      // display-style passive names
        TestAssert.Equal(13, byKind[BbReferenceKind.Preload]);
        TestAssert.Equal(4, byKind[BbReferenceKind.Buff]);
        TestAssert.Equal(1, byKind[BbReferenceKind.Spell]);
        TestAssert.Equal(61, byKind.Values.Sum());
        TestAssert.Equal(5, closures.Sum(c => c.DataOnly.Count));   // inibin present, script absent
    }

    [Check("scripting.bb-loader-shapes")]
    public static void UnknownShapesBecomeDiagnostics()
    {
        var good = new Dictionary<string, LuaValue>
        {
            ["BuffTextureName"] = new LuaValue.String("Icon.dds"),
            ["NotSingleTargetSpell"] = new LuaValue.Boolean(true),
            ["DoesntBreakShields"] = new LuaValue.Global("TRUE_CONSTANT"),
            ["ExecuteBuildingBlocks"] = new LuaValue.Global(null),
            ["OnBuffActivateBuildingBlocks"] = new LuaValue.Map(new Dictionary<LuaValue, LuaValue>()),
            ["TargetExecuteBuildingBlocks"] = Arr(
                Block("BBIf", Map(("Src1Var", Str("a")), ("CompareOp", new LuaValue.Global("CO_EQUAL"))),
                    Arr(Block("BBApplyDamage", Map(("DamageByLevel", Arr(Num(1), Num(2))))))),
                Block("BBElse", Map())),
            ["PreLoadBuildingBlocks"] = Arr(Block("BBPreloadSpell", Map(("Name", Str("x"))))),
        };
        var ok = BbScriptLoader.Build("Good", "Spells/Good.luaobj", good);
        TestAssert.True(ok.IsClean, string.Join("; ", ok.Diagnostics.Select(d => d.Message)));
        var script = ok.Script!;
        TestAssert.Equal(new[] { "BuffTextureName", "NotSingleTargetSpell", "DoesntBreakShields", "ExecuteBuildingBlocks" },
            script.Globals.Select(g => g.Key).ToArray());
        TestAssert.Equal(BbValueKind.Reference, script.Global("DoesntBreakShields")!.Kind);
        TestAssert.Equal(BbValueKind.Closure, script.Global("ExecuteBuildingBlocks")!.Kind);
        TestAssert.Equal(new[] { "OnBuffActivate", "TargetExecute" }, script.Events.Select(e => e.Hook).ToArray());
        TestAssert.Equal(0, script.Event("OnBuffActivate")!.Blocks.Count);
        var iff = script.Event("TargetExecute")!.Blocks[0];
        TestAssert.Equal(new[] { "Src1Var", "CompareOp" }, iff.Params.Select(p => p.Key).ToArray());
        TestAssert.Equal(2, ((BbValue.Table)iff.SubBlocks[0].Param("DamageByLevel")!).Items.Count);
        TestAssert.Equal(1, script.PreLoad.Count);
        TestAssert.Equal(4, script.Walk().Count());

        var damaged = new Dictionary<string, LuaValue>
        {
            ["BadBuildingBlocks"] = Arr(
                Map(("Params", Map())),                                                      // no Function
                Map(("Function", Str("BBIf"))),                                              // Function is a string
                Map(("Function", new LuaValue.Global("BBIf")), ("Params", Num(1))),          // Params not a table
                Map(("Function", new LuaValue.Global("BBIf")), ("SubBlocks", Num(2))),       // SubBlocks not blocks
                Map(("Function", new LuaValue.Global("BBIf")), ("Comment", Str("x"))),       // unknown key
                Num(3),                                                                       // not a table
                new LuaValue.Map(new Dictionary<LuaValue, LuaValue>
                {
                    [Num(1)] = Str("x"), [Str("Function")] = new LuaValue.Global("BBElse"),
                })),
            ["ScalarBuildingBlocks"] = Num(1),
        };
        var bad = BbScriptLoader.Build("Bad", "Spells/Bad.luaobj", damaged);
        TestAssert.False(bad.IsClean);
        var kinds = bad.Diagnostics.Select(d => d.Kind).ToHashSet();
        foreach (var kind in new[]
                 {
                     BbDiagnosticKind.MissingFunction, BbDiagnosticKind.BadFunction, BbDiagnosticKind.BadParams,
                     BbDiagnosticKind.BadSubBlocks, BbDiagnosticKind.UnknownBlockKey, BbDiagnosticKind.NotABlock,
                     BbDiagnosticKind.NonStringKey, BbDiagnosticKind.BadEventTable,
                 })
            TestAssert.True(kinds.Contains(kind), $"missing diagnostic {kind}");
        // Blocks with a usable Function survive (the unknown key, bad Params and bad SubBlocks are dropped, not fatal).
        TestAssert.Equal(4, bad.Script!.Event("Bad")!.Blocks.Count);
        TestAssert.True(bad.Diagnostics.All(d => d.Path.Length > 0 && d.Message.Length > 0));

        // File-level failures are typed results, not exceptions.
        var paths = GameDataPaths.Default;
        var partial = BbScriptLoader.Load(paths, "Scripts/Minion.luaobj");
        TestAssert.Equal(LuaLoadStatus.Partial, partial.Status);
        TestAssert.True(partial.Script is null && partial.Diagnostics[0].Kind == BbDiagnosticKind.LoadPartial);
        var missing = BbScriptLoader.Load(paths, "Spells/NoSuchScriptForBbLoader.luaobj");
        TestAssert.Equal(LuaLoadStatus.Error, missing.Status);
        TestAssert.True(missing.Script is null && missing.Diagnostics[0].Kind == BbDiagnosticKind.LoadFailed);
        var notLua = BbScriptLoader.Load(paths, "Characters/Annie/Annie.inibin");
        TestAssert.Equal(LuaLoadStatus.Error, notLua.Status);
    }

    [Check("data.inibin.vector-segments")]
    public static void InibinVectorSegmentsAndChampionRoster()
    {
        // Synthetic file: bit 7 (3 floats) then bit 10 (4 bytes), empty string block.
        var bytes = new List<byte> { 2, 0, 0 };
        bytes.AddRange(BitConverter.GetBytes((ushort)((1 << 7) | (1 << 10))));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes(0xAABBCCDDu));
        foreach (var value in new[] { 255f, 0f, -12.5f }) bytes.AddRange(BitConverter.GetBytes(value));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes(0x11223344u));
        bytes.AddRange(new byte[] { 60, 60, 60, 10 });
        var parsed = InibinReader.Parse(bytes.ToArray());
        TestAssert.Equal(2, parsed.Count);
        var floats = parsed[0xAABBCCDDu];
        TestAssert.Equal(InibinValueKind.FloatVector, floats.Kind);
        TestAssert.Equal(new[] { 255.0, 0.0, -12.5 }, floats.AsVector().ToArray());
        var raw = parsed[0x11223344u];
        TestAssert.Equal(InibinValueKind.ByteVector, raw.Kind);
        TestAssert.Equal(new[] { 60.0, 60.0, 60.0, 10.0 }, raw.AsVector().ToArray());
        // The stock client reads byte vectors divided by ten.
        TestAssert.Equal(new[] { 6.0, 6.0, 6.0, 1.0 }, raw.AsClientVector().Select(part => Math.Round(part, 6)).ToArray());
        TestAssert.Equal(new[] { 255.0, 0.0, -12.5 }, floats.AsClientVector().ToArray());
        TestAssert.False(raw.IsNumeric);
        TestAssert.Throws<InvalidCastException>(() => raw.AsDouble());
        // Segments 8 and 9 occur in no shipped file; the client's list reader takes them as 2 bytes and 2 floats,
        // so the reader decodes the same sizes.
        var twoBytes = InibinReader.Parse(new byte[] { 2, 0, 0, 0, 1, 1, 0, 1, 0, 1, 2, 3, 4 });
        TestAssert.Equal(InibinValueKind.ByteVector, twoBytes[0x02010001u].Kind);
        TestAssert.Equal(new[] { 3.0, 4.0 }, twoBytes[0x02010001u].AsVector().ToArray());

        // Shipped files that carry these segments.
        AssertVector("Characters/Karma/Karma.inibin", 0xFD51FBF3u, InibinValueKind.ByteVector, [60, 60, 60, 10]);
        AssertVector("Characters/Udyr/Udyr.inibin", 0xFD51FBF3u, InibinValueKind.ByteVector, [50, 50, 50, 50]);
        AssertVector("Characters/Renekton/Renekton.inibin", 0x32342A22u, InibinValueKind.FloatVector, [85, 85, 85]);
        AssertVector("Characters/Rumble/Rumble.inibin", 0xB1A9A126u, InibinValueKind.FloatVector, [55, 55, 55]);
        AssertVector("Characters/Tryndamere/Tryndamere.inibin", 0x32342A22u, InibinValueKind.FloatVector, [255, 0, 0]);
        AssertVector("Characters/Tryndamere/Tryndamere.inibin", 0xB1A9A126u, InibinValueKind.FloatVector, [175, 0, 0]);

        // All 84 champion inibins parse, including the five that carry vector segments.
        var champions = new BbScriptResolver(GameDataPaths.Default).DiscoverChampions();
        TestAssert.Equal(84, champions.Count);
        foreach (var name in new[] { "Karma", "Renekton", "Rumble", "Tryndamere", "Udyr" })
            TestAssert.True(champions.Contains(name), $"{name} is part of the roster");
        foreach (var champion in champions)
            TestAssert.True(InibinReader.LoadData($"Characters/{champion}/{champion}.inibin").Count > 0, champion);
    }

    private static void AssertVector(string relative, uint key, InibinValueKind kind, double[] expected)
    {
        var value = InibinReader.LoadData(relative)[key];
        TestAssert.Equal(kind, value.Kind, relative);
        TestAssert.Equal(expected, value.AsVector().ToArray(), relative);
    }

    private static void Role(BbClosure closure, string name, BbScriptRole role) =>
        TestAssert.Equal(role, closure.Scripts.Single(s => s.Name == name).Role, name);

    // Same file rule as the coverage audit: Spells, Items, Talents, BuildingBlocks, Characters/*/Scripts.
    private static List<string> ScriptFiles(GameDataPaths paths)
    {
        var dataRoot = Path.Combine(paths.Root, "DATA");
        var files = new List<string>();
        foreach (var area in new[] { "Spells", "Items", "Talents", "BuildingBlocks" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(dataRoot, area), "*.luaobj", SearchOption.AllDirectories));
        foreach (var champion in Directory.EnumerateDirectories(Path.Combine(dataRoot, "Characters")))
        {
            var scripts = Path.Combine(champion, "Scripts");
            if (Directory.Exists(scripts))
                files.AddRange(Directory.EnumerateFiles(scripts, "*.luaobj", SearchOption.AllDirectories));
        }
        return files.Select(file => Path.GetRelativePath(dataRoot, file).Replace('\\', '/'))
            .OrderBy(file => file, StringComparer.Ordinal).ToList();
    }

    private static LuaValue.String Str(string value) => new(value);
    private static LuaValue.Number Num(double value) => new(value);
    private static LuaValue.Array Arr(params LuaValue[] items) => new(items);

    private static LuaValue.Map Map(params (string Key, LuaValue Value)[] pairs) =>
        new(pairs.ToDictionary(pair => (LuaValue)new LuaValue.String(pair.Key), pair => pair.Value));

    private static LuaValue.Map Block(string function, LuaValue.Map parameters, LuaValue.Array? subBlocks = null)
    {
        var pairs = new List<(string, LuaValue)> { ("Function", new LuaValue.Global(function)), ("Params", parameters) };
        if (subBlocks is not null) pairs.Add(("SubBlocks", subBlocks));
        return Map(pairs.ToArray());
    }
}
