using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Combat;

namespace Amberline.Tools.Audits;

public sealed record LuaPinAudit(int Index, string File, string Note,
    IReadOnlyList<double> Ours, IReadOnlyList<double> Theirs, double Tolerance,
    bool Matches, string? Error = null);

public sealed record LuaObjAuditReport(IReadOnlyList<LuaPinAudit> Pins,
    IReadOnlyList<LuaPinAudit> Divergences, string Coverage)
{
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = indented });
}

/// <summary>Replays the maintained Lua-object audit pins against C# data facades.</summary>
public static class LuaObjAudit
{
    private const string LevelScript = "LEVELS/Map1/Scripts/LevelScript.luaobj";

    public static LuaObjAuditReport Audit(GameDataPaths? paths = null)
    {
        var root = paths ?? GameDataPaths.Default;
        var data = new GameData(root);
        var pins = new List<LuaPinAudit>();
        // Annie pins 0 to 7 and 15 (the stun, the passive threshold, Tibbers' lifetime, the Q mark, the aura, his armor) used to compare the
        // constants of the hand-written Annie with the scripts. Annie now runs from those scripts (19 October 2026), so "ours" is the retail
        // figure written out here and the pin guards the shipped script data against a change.
        Add(pins, 0, "DATA/Characters/Annie/Scripts/CharScriptAnnie.luaobj",
            "Pyromania stun duration", 1e-6,
            () => [1.75],
            () => First(FunctionNumbers(root, "Characters/Annie/Scripts/CharScriptAnnie.luaobj",
                "BBSetVarInTable", 0, "SrcValueByLevel", "DestVar", "StunDuration")));
        Add(pins, 1, "DATA/Spells/Pyromania.luaobj", "Pyromania cast threshold", 1e-6,
            () => [5.0], () => FunctionNumbers(root, "Spells/Pyromania.luaobj", "BBIf", 0, "Value2", "Src1Var", "Count"));
        Add(pins, 2, "DATA/Spells/MoltenShield.luaobj", "Molten Shield duration", 1e-6,
            () => [data.Spell("MoltenShield").EffectNumber(3, 1)], () => FunctionNumbers(root, "Spells/MoltenShield.luaobj", "BBSpellBuffAdd", 0, "Duration", "BuffAddType", "BUFF_RENEW_EXISTING"));
        Add(pins, 3, "DATA/Spells/InfernalGuardian.luaobj", "Tibbers lifetime", 1e-6,
            () => [45.0], () => FunctionNumbers(root, "Spells/InfernalGuardian.luaobj", "BBSpawnPet", 0, "Duration", "Name", "Tibbers"));
        Add(pins, 4, "DATA/Spells/Disintegrate.luaobj", "Q internal marker duration", 1e-6,
            () => [1.0], () => FunctionNumbers(root, "Spells/Disintegrate.luaobj", "BBSpellBuffAdd", 0, "Duration", "BuffType", "BUFF_Internal"));
        Add(pins, 5, "DATA/Spells/InfernalGuardian.luaobj", "Tibbers aura tick damage", 1e-6,
            () => [35.0],
            () => First(FunctionNumbers(root, "Spells/InfernalGuardian.luaobj", "BBSetVarInTable", 0, "SrcValueByLevel", "DestVar", "DamageAmount")));
        Add(pins, 6, "DATA/Spells/InfernalGuardianBurning.luaobj", "Tibbers aura radius", 1e-6,
            () => [350.0], () => First(FunctionNumbers(root, "Spells/InfernalGuardianBurning.luaobj", "BBForEachUnitInTargetArea", 0, "Range")));
        Add(pins, 7, "DATA/Spells/InfernalGuardianBurning.luaobj", "Tibbers aura interval", 1e-6,
            () => [1.0], () => First(FunctionNumbers(root, "Spells/InfernalGuardianBurning.luaobj", "BBExecutePeriodically", 0, "TimeBetweenExecutions")));
        AddSpellEffect(pins, root, data, 8, "DATA/Spells/Disintegrate.luaobj", "Disintegrate damage", "Disintegrate", 1);
        AddSpellEffect(pins, root, data, 9, "DATA/Spells/Incinerate.luaobj", "Incinerate damage", "Incinerate", 1);
        Add(pins, 10, "DATA/Spells/MoltenShield.luaobj", "Molten Shield armor", 1e-6,
            () => data.Spell("MoltenShield").Effects[1].Select(value => value.AsDouble()).ToArray(),
            () => FunctionNumbers(root, "Spells/MoltenShield.luaobj", "BBSetVarInTable", 0, "SrcValueByLevel", "DestVar", "ArmorAmount"));
        Add(pins, 11, "DATA/Spells/MoltenShield.luaobj", "Molten Shield reflection", 1e-6,
            () => data.Spell("MoltenShield").Effects[2].Select(value => value.AsDouble()).ToArray(),
            () => FunctionNumbers(root, "Spells/MoltenShield.luaobj", "BBSetVarInTable", 0, "SrcValueByLevel", "DestVar", "DamageReturn"));
        AddSpellEffect(pins, root, data, 12, "DATA/Spells/InfernalGuardian.luaobj", "Infernal Guardian damage", "InfernalGuardian", 1);
        Add(pins, 13, "DATA/Spells/InfernalGuardian.luaobj", "Tibbers health", 1e-6,
            () => data.Spell("InfernalGuardian").Effects[2].Take(3).Select(value => value.AsDouble()).ToArray(),
            () => FunctionNumbers(root, "Spells/InfernalGuardian.luaobj", "BBSpawnPet", 0, "HealthBonusByLevel", "Name", "Tibbers").Take(3).Select(value => value + data.Champion("AnnieTibbers").BaseHp).ToArray());
        Add(pins, 14, "DATA/Spells/InfernalGuardian.luaobj", "Tibbers damage", 1e-6,
            () => data.Spell("InfernalGuardian").Effects[3].Take(3).Select(value => value.AsDouble()).ToArray(),
            () => FunctionNumbers(root, "Spells/InfernalGuardian.luaobj", "BBSpawnPet", 0, "DamageBonusByLevel", "Name", "Tibbers").Take(3).Select(value => value + data.Champion("AnnieTibbers").BaseDamage).ToArray());
        Add(pins, 15, "DATA/Spells/InfernalGuardian.luaobj", "Tibbers armor", 1e-6,
            () => [30.0, 50.0, 70.0],
            () => FunctionNumbers(root, "Spells/InfernalGuardian.luaobj", "BBSetVarInTable", 0, "SrcValueByLevel", "DestVar", "ArmorAmount").Take(3).Select(value => value + data.Champion("AnnieTibbers").Armor).ToArray());

        var level = LoadLevel(root);
        AddGlobal(pins, root, 16, "melee minion gold reward", MinionDefinitions.BaseStats(MinionType.Melee).GoldReward, "MELEE_GOLD_GIVEN", level);
        AddGlobal(pins, root, 17, "melee minion experience", MinionDefinitions.BaseStats(MinionType.Melee).ExperienceReward, "MELEE_EXP_GIVEN", level);
        AddGlobal(pins, root, 18, "caster minion gold reward", MinionDefinitions.BaseStats(MinionType.Caster).GoldReward, "ARCHER_GOLD_GIVEN", level);
        AddGlobal(pins, root, 19, "caster minion experience", MinionDefinitions.BaseStats(MinionType.Caster).ExperienceReward, "ARCHER_EXP_GIVEN", level);
        AddGlobal(pins, root, 20, "siege minion gold reward", MinionDefinitions.BaseStats(MinionType.Siege).GoldReward, "CASTER_GOLD_GIVEN", level);
        AddGlobal(pins, root, 21, "siege minion experience", MinionDefinitions.BaseStats(MinionType.Siege).ExperienceReward, "CASTER_EXP_GIVEN", level);
        AddGlobal(pins, root, 22, "super minion gold reward", MinionDefinitions.BaseStats(MinionType.Super).GoldReward, "SUPER_GOLD_GIVEN", level);
        AddGlobal(pins, root, 23, "super minion experience", MinionDefinitions.BaseStats(MinionType.Super).ExperienceReward, "SUPER_EXP_GIVEN", level);
        AddGlobal(pins, root, 24, "first wave time", MinionDefinitions.FirstWaveSeconds, "INITIAL_TIME_TO_SPAWN", level);
        AddGlobal(pins, root, 25, "experience radius", CombatService.ExperienceShareRadius, "EXP_GIVEN_RADIUS", level);

        Add(pins, 26, "DATA/Characters/Blue_Minion_Basic/Blue_Minion_Basic.inibin", "melee minion base attack damage", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Melee).AttackDamage], () => InibinNumber(root, "Characters/Blue_Minion_Basic/Blue_Minion_Basic.inibin", "Data*BaseDamage"));
        Add(pins, 27, "DATA/Characters/Blue_Minion_Wizard/Blue_Minion_Wizard.inibin", "caster minion base attack damage", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Caster).AttackDamage], () => InibinNumber(root, "Characters/Blue_Minion_Wizard/Blue_Minion_Wizard.inibin", "Data*BaseDamage"));
        Add(pins, 28, "DATA/Characters/Blue_Minion_MechCannon/Blue_Minion_MechCannon.inibin", "siege minion base attack damage", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Siege).AttackDamage], () => InibinNumber(root, "Characters/Blue_Minion_MechCannon/Blue_Minion_MechCannon.inibin", "Data*BaseDamage"));
        Add(pins, 29, "DATA/Characters/Blue_Minion_MechMelee/Blue_Minion_MechMelee.inibin", "super minion base attack damage", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Super).AttackDamage], () => InibinNumber(root, "Characters/Blue_Minion_MechMelee/Blue_Minion_MechMelee.inibin", "Data*BaseDamage"));
        Add(pins, 30, "DATA/Spells/Blue_Minion_WizardBasicAttack.inibin", "caster minion missile speed", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Caster).MissileSpeed], () => InibinNumber(root, "Spells/Blue_Minion_WizardBasicAttack.inibin", "SpellData*MissileSpeed"));
        Add(pins, 31, "DATA/Spells/Blue_Minion_MechCannonBasicAttack.inibin", "siege minion missile speed", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Siege).MissileSpeed], () => InibinNumber(root, "Spells/Blue_Minion_MechCannonBasicAttack.inibin", "SpellData*MissileSpeed"));
        Add(pins, 32, "DATA/Characters/Blue_Minion_Wizard/Blue_Minion_Wizard.inibin", "caster minion acquisition range", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Caster).AcquisitionRange], () => InibinNumber(root, "Characters/Blue_Minion_Wizard/Blue_Minion_Wizard.inibin", "Data*AcquisitionRange"));
        Add(pins, 33, "DATA/Characters/Blue_Minion_MechMelee/Blue_Minion_MechMelee.inibin", "super minion acquisition range", 1e-6, () => [MinionDefinitions.BaseStats(MinionType.Super).AcquisitionRange], () => InibinNumber(root, "Characters/Blue_Minion_MechMelee/Blue_Minion_MechMelee.inibin", "Data*AcquisitionRange"));
        Add(pins, 34, "DATA/NSEAI.inibin", "collision separation ratio", 1e-6,
            () => [MinionDefinitions.SeparationTargetRatio],
            () => [data.NseaiValue("Collision", "MinionMaxCollisionAvoidanceRatio").AsDouble()]);

        return new LuaObjAuditReport(pins,
            pins.Where(pin => !pin.Matches).ToArray(),
            "35 pins over shipped spell, character, level, and inibin data; Partial LevelScript globals compare literal LOADK/SETGLOBAL declarations, not verified execution");
    }

    private static void AddSpellEffect(List<LuaPinAudit> pins, GameDataPaths root, GameData data,
        int index, string file, string note, string spellName, int effect)
    {
        Add(pins, index, file, note, 1e-6,
            () => data.Spell(spellName).Effects[effect].Select(value => value.AsDouble()).ToArray(),
            () => FunctionNumbers(root, file["DATA/".Length..], "BBApplyDamage", 0, "DamageByLevel"));
    }

    private static void AddGlobal(List<LuaPinAudit> pins, GameDataPaths root, int index,
        string note, double ours, string name, LuaLoadResult level)
    {
        var provenance = level.Status switch
        {
            LuaLoadStatus.Success => "reconstructed LevelScript global",
            LuaLoadStatus.Partial => "literal LOADK/SETGLOBAL declaration (LevelScript source is Partial)",
            _ => "unavailable LevelScript global",
        };
        Add(pins, index, LevelScript, $"{note} [{provenance}]", 1e-6,
            () => [ours], () => GlobalNumber(root, level, name));
    }

    private static IReadOnlyList<double> InibinNumber(GameDataPaths root, string relative, string key)
    {
        var data = InibinReader.Load(root.Data(relative));
        return [data[InibinReader.KeyHash(key)].AsDouble()];
    }

    private static void Add(List<LuaPinAudit> pins, int index, string file, string note,
        double tolerance, Func<IReadOnlyList<double>> ours, Func<IReadOnlyList<double>> theirs)
    {
        try
        {
            var left = ours(); var right = theirs();
            var matches = left.Count == right.Count && left.Zip(right).All(pair =>
                Math.Abs(pair.First - pair.Second) <= tolerance);
            pins.Add(new LuaPinAudit(index, file, note, left, right, tolerance, matches));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or LuaFormatException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            pins.Add(new LuaPinAudit(index, file, note, Array.Empty<double>(), Array.Empty<double>(),
                tolerance, false, ex.Message));
        }
    }

    private static LuaLoadResult LoadLevel(GameDataPaths root) =>
        LuaObjectReader.Load(Path.Combine(root.Root, LevelScript.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyList<double> FunctionNumbers(GameDataPaths root, string relative,
        string function, int occurrence, string key, string? matchKey = null, string? matchValue = null)
    {
        var loaded = LuaObjectReader.LoadData(root, relative);
        RequireSuccessfulSource(relative, loaded);
        var parameters = matchKey is null ? null : new Dictionary<string, string> { [matchKey] = matchValue! };
        var value = FindNthFunction(loaded.Globals.Values, function, occurrence, parameters);
        return AuditLuaValues.Numbers(AuditLuaValues.Get(AuditLuaValues.Get(value!, "Params")!, key));
    }

    private static LuaValue? FindNthFunction(IEnumerable<LuaValue> roots, string name, int occurrence,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var found = 0;
        foreach (var value in AuditLuaValues.Walk(roots))
        {
            if (value is not LuaValue.Map) continue;
            if (!string.Equals(AuditLuaValues.Text(AuditLuaValues.Get(value, "Function")), name,
                StringComparison.Ordinal)) continue;
            if (parameters is not null && !AuditLuaValues.ParametersMatch(
                AuditLuaValues.Get(value, "Params"), parameters)) continue;
            if (found++ == occurrence) return value;
        }
        throw new InvalidDataException($"Lua function {name} occurrence {occurrence} not found");
    }

    private static IReadOnlyList<double> First(IReadOnlyList<double> values) =>
        values.Count == 0 ? throw new InvalidDataException("Lua audit value is empty") : [values[0]];

    private static void RequireSuccessfulSource(string relativePath, LuaLoadResult source)
    {
        if (source.Status != LuaLoadStatus.Success)
            throw new InvalidDataException(
                $"Lua source '{relativePath}' did not load successfully ({source.Status}): " +
                (source.Error ?? source.Reason ?? "unknown parse error"));
    }

    private static IReadOnlyList<double> GlobalNumber(GameDataPaths root, LuaLoadResult level, string name)
    {
        if (level.Status == LuaLoadStatus.Success)
        {
            if (level.Globals.TryGetValue(name, out var value) &&
                AuditLuaValues.Number(value) is double direct)
                return [direct];
            throw new InvalidDataException(
                $"Lua global {name} was not reconstructed in successful LevelScript source");
        }
        if (level.Status != LuaLoadStatus.Partial)
            RequireSuccessfulSource(LevelScript, level);
        var text = LuaObjectReader.ListBytecode(Path.Combine(root.Root,
            LevelScript.Replace('/', Path.DirectorySeparatorChar)));
        var match = Regex.Match(text,
            $"LOADK R(?<register>\\d+) K\\d+=(?<value>-?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][+-]?\\d+)?)\\r?\\n\\s*\\d+\\s+SETGLOBAL R\\k<register> K\\d+='" + Regex.Escape(name) + "'",
            RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException($"Lua global {name} not found");
        return [double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)];
    }
}
