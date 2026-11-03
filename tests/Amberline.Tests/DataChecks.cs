using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Tools.Audits;
using Amberline.Data;

namespace Amberline.Tests;

public static class DataChecks
{
    [Check("data.inibin")]
    public static void Inibin()
    {
        TestAssert.Equal(0x64DA2F80u, InibinReader.KeyHash("Data*PathfindingCollisionRadius"));
        TestAssert.Equal(InibinReader.KeyHash("Data*PathfindingCollisionRadius"),
            InibinReader.KeyHash("data*pathfindingcollisionradius"));

        var anniePath = GameDataPaths.Default.Data("Characters/Annie/Annie.inibin");
        var annie = InibinReader.Load(anniePath);
        TestAssert.True(annie.Count > 0);
        TestAssert.Equal(new System.Numerics.BigInteger(348), annie[InibinReader.KeyHash("Data*BaseHP")].AsInteger());
        TestAssert.Near(54.544, annie[InibinReader.KeyHash("Data*PathfindingCollisionRadius")].AsDouble(), 0.01);
        TestAssert.Equal("Disintegrate", annie[InibinReader.KeyHash("Data*Spell1")].AsString());
        TestAssert.Equal("310", InibinReader.Load(anniePath, raw: true)[InibinReader.KeyHash("Data*MoveSpeed")].AsString());

        var orianna = InibinReader.LoadData("Characters/Orianna/Orianna.inibin");
        TestAssert.True(orianna.Count > 0);
        TestAssert.True(orianna.ContainsKey(InibinReader.KeyHash("Data*BaseHP")));

        TestAssert.Throws<InibinException>(() => InibinReader.Parse(new byte[] { 2, 0 }));
        var bytes = File.ReadAllBytes(anniePath);
        TestAssert.Throws<InibinException>(() => InibinReader.Parse(bytes.AsSpan(0, Math.Min(40, bytes.Length))));
        TestAssert.Throws<InibinException>(() => InibinReader.Parse(new byte[] { 1, 0, 0, 0, 0 }));
    }


    [Check("data.luaobj")]
    public static void LuaObject()
    {
        var q = LuaObjectReader.LoadData("Spells/Disintegrate.luaobj");
        TestAssert.Equal(LuaLoadStatus.Success, q.Status, q.Error ?? q.Reason);
        TestAssert.True(q.Globals.Count > 0);
        var damage = FindValue(q.Globals.Values, "DamageByLevel");
        TestAssert.True(damage is LuaValue.Array, "DamageByLevel should reconstruct as an array");
        TestAssert.Equal(5, ((LuaValue.Array)damage).Values.Count);
        TestAssert.Near(85.0, ((LuaValue.Number)((LuaValue.Array)damage).Values[0]).Value, 0.001);

        var molten = LuaObjectReader.LoadData("Spells/MoltenShield.luaobj");
        TestAssert.Equal(LuaLoadStatus.Success, molten.Status, molten.Error ?? molten.Reason);
        var deltaVars = FindAllValues(molten.Globals.Values, "DeltaVar").OfType<LuaValue.String>().ToArray();
        TestAssert.Equal(2, deltaVars.Length);
        TestAssert.True(deltaVars.All(value => value.Value == "ArmorAmount"));

        var logic = LuaObjectReader.LoadData("Scripts/Minion.luaobj");
        TestAssert.Equal(LuaLoadStatus.Partial, logic.Status);
        TestAssert.True(logic.Reason?.Contains("CALL", StringComparison.Ordinal) == true);
        TestAssert.True(logic.Strings.Count > 0);

        var bad = LuaObjectReader.Load(GameDataPaths.Default.Data("Characters/Annie/Annie.inibin"));
        TestAssert.Equal(LuaLoadStatus.Error, bad.Status);
        var missing = LuaObjectReader.Load(Path.Combine(Path.GetTempPath(),
            "amberline-missing-" + Guid.NewGuid().ToString("N") + ".luaobj"));
        TestAssert.Equal(LuaLoadStatus.Error, missing.Status,
            "a missing LevelScript-like source cannot enter the Partial literal fallback");
        var aggro = LuaObjectReader.ListBytecodeData("Scripts/Aggro.luaobj");
        var addToIgnore = aggro.IndexOf("AddToIgnore", StringComparison.Ordinal);
        TestAssert.True(addToIgnore >= 0 && aggro[addToIgnore..Math.Min(aggro.Length, addToIgnore + 200)].Contains("0.1", StringComparison.Ordinal));
        var taskKill = LuaObjectReader.ListBytecodeData("Scripts/TaskKillHero.luaobj");
        var engage = taskKill.IndexOf("SETGLOBAL R0 K2=", StringComparison.Ordinal);
        TestAssert.True(engage >= 0 && taskKill.Contains("500.0", StringComparison.Ordinal));
        var bot = LuaObjectReader.ListBytecodeData("Scripts/Bot.luaobj");
        var castSpell = bot.IndexOf("K16='CastSpell'", StringComparison.Ordinal);
        TestAssert.True(castSpell >= 0 && bot[Math.Max(0, bot.LastIndexOf("SETTABLE", castSpell))..castSpell].Contains("K15='Name'", StringComparison.Ordinal));
        TestAssert.True(LuaObjectReader.ListBytecode(GameDataPaths.Default.Data("Characters/Annie/Annie.inibin")).StartsWith("error:", StringComparison.Ordinal));
        TestAssert.Throws<LuaFormatException>(() => LuaObjectReader.Parse(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' }));
    }

    [Check("data.luaobj.restricted-operand-semantics")]
    public static void LuaObjectRestrictedOperandSemantics()
    {
        var skipped = LoadSynthetic(
            [LuaAbc(2, 0, 1, 1), LuaAbx(1, 1, 1), LuaAbx(7, 0, 0), LuaAbc(30, 0, 1, 0)],
            new LuaConstant.String("flag"), new LuaConstant.Number(99));
        TestAssert.Equal(LuaLoadStatus.Success, skipped.Status, skipped.Error ?? skipped.Reason);
        TestAssert.Equal(1, skipped.Globals.Count, "LOADBOOL skip must not execute the next instruction");
        TestAssert.True(skipped.Globals["flag"] is LuaValue.Boolean { Value: true });

        var code = new List<uint> { LuaAbc(10, 0, 0, 0) };
        var constants = new List<LuaConstant>();
        for (var i = 0; i < 50; i++)
        {
            constants.Add(new LuaConstant.Number(i + 1));
            code.Add(LuaAbx(1, i + 1, i));
        }
        code.Add(LuaAbc(34, 0, 50, 1));
        constants.Add(new LuaConstant.Number(51));
        code.Add(LuaAbx(1, 1, 50));
        code.Add(LuaAbc(34, 0, 1, 2));
        constants.Add(new LuaConstant.String("values"));
        code.Add(LuaAbx(7, 0, 51));
        code.Add(LuaAbc(30, 0, 1, 0));
        var contiguous = LoadSynthetic(code, constants.ToArray());
        TestAssert.Equal(LuaLoadStatus.Success, contiguous.Status, contiguous.Error ?? contiguous.Reason);
        var values = (LuaValue.Array)contiguous.Globals["values"];
        TestAssert.Equal(51, values.Values.Count, "SETLIST must honor consecutive 50-value blocks");
        TestAssert.Equal(1.0, ((LuaValue.Number)values.Values[0]).Value);
        TestAssert.Equal(51.0, ((LuaValue.Number)values.Values[^1]).Value);

        var noncontiguous = LoadSynthetic(
            [LuaAbc(10, 0, 0, 0), LuaAbx(1, 1, 0), LuaAbc(34, 0, 1, 2)],
            new LuaConstant.Number(1));
        TestAssert.Equal(LuaLoadStatus.Partial, noncontiguous.Status);
        TestAssert.True(noncontiguous.Reason?.Contains("noncontiguous", StringComparison.Ordinal) == true);

        var variableCount = LoadSynthetic([LuaAbc(10, 0, 0, 0), LuaAbc(34, 0, 0, 1)]);
        TestAssert.Equal(LuaLoadStatus.Partial, variableCount.Status);
        TestAssert.True(variableCount.Reason?.Contains("B=0", StringComparison.Ordinal) == true);

        var extension = LoadSynthetic([LuaAbc(10, 0, 0, 0), LuaAbc(34, 0, 1, 0)],
            new LuaConstant.Number(1));
        TestAssert.Equal(LuaLoadStatus.Partial, extension.Status);
        TestAssert.True(extension.Reason?.Contains("C=0", StringComparison.Ordinal) == true);
    }

    [Check("data.luaobj.corpus")]
    public static void LuaObjectCorpus()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(GameDataPaths.Default.Root, "DATA"), "*.luaobj", SearchOption.AllDirectories).ToArray();
        TestAssert.True(files.Length > 2000, $"Expected the shipped Lua object corpus, found {files.Length} files");
        foreach (var file in files)
            _ = LuaObjectReader.ParseFile(file);
    }

    [Check("data.gamedata")]
    public static void GameData()
    {
        var data = new Amberline.Data.GameData();
        var annie = data.Champion("Annie");
        TestAssert.Equal("Annie", annie.Name);
        TestAssert.Equal(4, annie.SpellNames.Count);
        TestAssert.Equal("Disintegrate", annie.SpellNames[0]);
        var orianna = data.Champion("Orianna");
        TestAssert.Equal("Orianna", orianna.Name);
        TestAssert.Equal(4, orianna.SpellNames.Count);

        var q = data.Spell("Disintegrate");
        TestAssert.Equal(5, q.MaxRank);
        TestAssert.Equal(5, q.ManaCosts.Count);
        TestAssert.Equal(5, q.Effects[1].Count);
        TestAssert.Near(0.7, q.Coefficient, 0.0001);
        TestAssert.Near(85, q.EffectNumber(1, 1), 0.001);
        var gatling = data.Spell("GatlingGun");
        TestAssert.Equal(InibinValueKind.String, gatling.Effects[1][1].Kind);
        // The shipped text "4.5.0000": the client's float getter reads the numeric prefix (atof), so the amount is 4.5.
        TestAssert.Equal(4.5, gatling.EffectNumber(1, 2), "GatlingGun effect 1 rank 2 is the atof prefix of 4.5.0000");
        TestAssert.Equal(3, data.Spell("InfernalGuardian").MaxRank);

        var levelOne = data.StatsAtLevel(annie, 1);
        TestAssert.Near(424, levelOne.MaxHp, 0.001);
        TestAssert.Near(300, levelOne.MaxMp, 0.001);
        TestAssert.Equal(1.5, data.NseaiValue("Collision", "MinionMaxCollisionAvoidanceRatio").AsDouble());
        TestAssert.Equal(new System.Numerics.BigInteger(200), data.NseaiValue("Collision", "StuckDelayInMS").AsInteger());
        TestAssert.Throws<InibinException>(() => data.Champion("NoSuchChampion"));
    }

    [Check("data.spell-max-level-is-the-spellbook-slot-rule")]
    public static void SpellMaxLevelIsTheSpellbookSlotRule()
    {
        // The spellbook's max-level getter: the table (5, 5, 5, 3) unless the character ships Data*MaxLevels (Karma, Udyr and his stances).
        var data = new Amberline.Data.GameData();
        TestAssert.Equal(new[] { 5, 5, 5, 3 }, Enumerable.Range(0, 4).Select(slot => data.SpellMaxLevel("Annie", slot)).ToArray());
        TestAssert.Equal(new[] { 6, 6, 6, 1 }, Enumerable.Range(0, 4).Select(slot => data.SpellMaxLevel("Karma", slot)).ToArray());
        TestAssert.Equal(new[] { 5, 5, 5, 5 }, Enumerable.Range(0, 4).Select(slot => data.SpellMaxLevel("Udyr", slot)).ToArray());
        TestAssert.Throws<ArgumentOutOfRangeException>(() => data.SpellMaxLevel("Annie", 4));
        // No other champion of the catalog overrides the table.
        var overrides = ChampionCatalog.All.Select(row => row.Name)
            .Where(name => !Enumerable.Range(0, 4).Select(slot => data.SpellMaxLevel(name, slot)).SequenceEqual(Amberline.Data.GameData.NormalSpellMaxLevels))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        TestAssert.Equal(new[] { "Karma", "Udyr" }, overrides);
    }

    [Check("data.spell-rank-gate-is-the-spellbook-rule")]
    public static void SpellRankGateIsTheSpellbookRule()
    {
        // Spellbook level requirement (the client's rank gate): the table (Q, W, E at 1 3 5 7 9, R at 6 11 16, 99 beyond) unless the character
        // ships Data*SpellsUpLevels1..4 (Udyr: all four slots 1 3 5 7 9 99, so his R is learnt like the others; Karma: R "1 95 96 97 98 99").
        var data = new Amberline.Data.GameData();
        TestAssert.Equal(new[] { 1, 3, 5, 7, 9 }, Enumerable.Range(1, 5).Select(rank => data.SpellRankGate("Annie", 0, rank)).ToArray());
        TestAssert.Equal(new[] { 6, 11, 16, 99 }, Enumerable.Range(1, 4).Select(rank => data.SpellRankGate("Annie", 3, rank)).ToArray());
        TestAssert.Equal(new[] { 1, 3, 5, 7, 9 }, Enumerable.Range(1, 5).Select(rank => data.SpellRankGate("Udyr", 3, rank)).ToArray());
        TestAssert.Equal(new[] { 1, 95 }, Enumerable.Range(1, 2).Select(rank => data.SpellRankGate("Karma", 3, rank)).ToArray());
        TestAssert.Throws<ArgumentOutOfRangeException>(() => data.SpellRankGate("Annie", 4, 1));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => data.SpellRankGate("Annie", 0, 0));
        // No other champion of the catalog ships a row of its own: the other 82 read the table.
        var withRows = ChampionCatalog.All.Select(row => row.Name)
            .Where(name => Enumerable.Range(0, 4).Any(slot => data.Champion(name).Raw.ContainsKey(Amberline.Data.InibinReader.KeyHash($"Data*SpellsUpLevels{slot + 1}"))))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        TestAssert.Equal(new[] { "Karma", "Udyr" }, withRows);
    }

    [Check("data.character-boolean-segment-numbers")]
    public static void CharacterNumbersReadTheBooleanSegment()
    {
        // The inibin compiler packs a 0/1 value into the boolean segment, and the retail CharacterData
        // loader reads it back through the float getter (true = 1.0, false = 0.0). Batch 19: OriannaBall's
        // BaseHP=True was read as 0, so its first-sight 0xCC wrote mMaxHP = 0.
        var data = new Amberline.Data.GameData();
        TestAssert.Equal(1.0, data.Champion("OriannaBall").BaseHp, "OriannaBall BaseHP is True");
        var beacon = data.Champion("SummonerBeacon");
        TestAssert.Equal(1.0, beacon.BaseDamage, "SummonerBeacon BaseDamage is True");
        TestAssert.Equal(1.0, beacon.AttackRange, "SummonerBeacon AttackRange is True");
        TestAssert.Equal(1.0, beacon.AcquisitionRange, "a stored True replaces the 750 default");
        // A stored False is a present zero, and an absent key still takes its default.
        TestAssert.Equal(0.0, data.Champion("Dragon").DamagePerLevel, "Dragon DamagePerLevel is False");
        TestAssert.Equal(750.0, data.Champion("AncientGolem").AcquisitionRange, "AncientGolem has no AcquisitionRange key");
        TestAssert.Equal(348.0, data.Champion("Annie").BaseHp, "numeric values are unchanged");
    }


    private static LuaLoadResult LoadSynthetic(IReadOnlyList<uint> code,
        params LuaConstant[] constants)
    {
        var path = Path.Combine(Path.GetTempPath(), "amberline-luaobj-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, EncodeSyntheticChunk(code, constants));
        try { return LuaObjectReader.Load(path); }
        finally { File.Delete(path); }
    }

    private static byte[] EncodeSyntheticChunk(IReadOnlyList<uint> code,
        IReadOnlyList<LuaConstant> constants)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a',
            0x51, 0, 1, 4, 4, 4, 8, 0 });
        WriteLuaString(stream, null);
        WriteInt32(stream, 0); WriteInt32(stream, 0);
        stream.Write(new byte[] { 0, 0, 0, 64 });
        WriteInt32(stream, code.Count);
        foreach (var instruction in code) WriteUInt32(stream, instruction);
        WriteInt32(stream, constants.Count);
        foreach (var constant in constants)
        {
            switch (constant)
            {
                case LuaConstant.Nil:
                    stream.WriteByte(0);
                    break;
                case LuaConstant.Boolean boolean:
                    stream.WriteByte(1); stream.WriteByte(boolean.Value ? (byte)1 : (byte)0);
                    break;
                case LuaConstant.Number number:
                    stream.WriteByte(3);
                    WriteInt64(stream, BitConverter.DoubleToInt64Bits(number.Value));
                    break;
                case LuaConstant.String text:
                    stream.WriteByte(4); WriteLuaString(stream, text.Value);
                    break;
                default: throw new InvalidOperationException("unknown synthetic Lua constant");
            }
        }
        WriteInt32(stream, 0); WriteInt32(stream, 0); WriteInt32(stream, 0); WriteInt32(stream, 0);
        return stream.ToArray();
    }

    private static uint LuaAbc(int opcode, int a, int b, int c) =>
        (uint)(opcode | (a << 6) | (c << 14) | (b << 23));

    private static uint LuaAbx(int opcode, int a, int bx) =>
        (uint)(opcode | (a << 6) | (bx << 14));

    private static void WriteLuaString(Stream stream, string? value)
    {
        if (value is null)
        {
            WriteUInt32(stream, 0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, checked((uint)bytes.Length + 1));
        stream.Write(bytes); stream.WriteByte(0);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static LuaValue FindValue(IEnumerable<LuaValue> values, string key)
    {
        return FindAllValues(values, key).FirstOrDefault()
            ?? throw new InvalidOperationException($"Lua key {key} was not found");
    }

    private static byte[] DigestValue(InibinValue value)
    {
        if (value.Kind == InibinValueKind.Boolean)
            return new[] { (byte)'b', value.Boolean ? (byte)1 : (byte)0 };
        if (value.Kind is InibinValueKind.UInt32 or InibinValueKind.UInt16 or InibinValueKind.Byte or InibinValueKind.Integer)
            return Encoding.UTF8.GetBytes("i" + (value.Kind == InibinValueKind.UInt32
                ? value.UInt32.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.Kind == InibinValueKind.UInt16
                    ? value.UInt16.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.Kind == InibinValueKind.Byte
                        ? value.Byte.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : value.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (value.Kind is InibinValueKind.Float32 or InibinValueKind.Double)
        {
            var result = new byte[9];
            result[0] = (byte)'f';
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1), BitConverter.DoubleToInt64Bits(value.Number));
            return result;
        }
        return Encoding.UTF8.GetBytes("s" + value.AsString());
    }

    private static IEnumerable<LuaValue> FindAllValues(IEnumerable<LuaValue> values, string key)
    {
        foreach (var value in values)
        {
            if (value is LuaValue.Map map)
            {
                foreach (var entry in map.Values)
                {
                    if (entry.Key is LuaValue.String name && name.Value == key)
                        yield return entry.Value;
                    foreach (var nested in FindAllValues(new[] { entry.Value }, key)) yield return nested;
                }
            }
            else if (value is LuaValue.Array array)
            {
                foreach (var nested in FindAllValues(array.Values, key)) yield return nested;
            }
        }
    }

    // Batch 19. The source-backed gamedata fixture predates the retail float getter's boolean-segment
    // fallback (the client's binary float getter: true = 1.0), so it records a stat the
    // compiler packed as True as 0. These are the only characters and fields where the shipped inibin
    // stores a True; the fixture is never re-recorded, so the check expects retail's 1.0 for them.
    private static readonly (string Field, string[] Characters)[] BooleanSegmentOnes =
    [
        ("base_hp", ["OriannaBall", "MaokaiSproutling"]),
        ("base_damage", ["JarvanIVStandard", "SummonerBeacon"]),
        ("attack_range", ["JarvanIVStandard", "SummonerBeacon"]),
        ("mp_regen", ["Lux"]),
        ("hp_regen", ["Rammus", "RammusDBC", "RammusPB"]),
        ("pathfinding_radius", ["CaitlynTrap", "HeimerTBlue", "HeimerTGreen", "HeimerTRed", "HeimerTYellow",
            "Nidalee_Spear", "OdinMinionGraveyardPortal", "OdinMinionSpawnPortal", "OdinOpeningBarrier",
            "OdinQuestBuff", "OdinQuestIndicator", "OdinSpeedShrine", "OdinTestCubeRender", "OlafAxe",
            "ShacoBox", "TestCube", "TestCubeRender", "TestCubeRender10Vision", "XerathArcaneBarrageLauncher"]),
        ("gameplay_radius", ["SightWard", "VisionWard", "WriggleLantern"]),
    ];

    private static Dictionary<string, double> BooleanSegmentCorrections(string character) =>
        BooleanSegmentOnes.Where(entry => entry.Characters.Contains(character))
            .ToDictionary(entry => entry.Field, _ => 1.0, StringComparer.Ordinal);

    // Batch OVN (16 October 2026). The same fixture also predates the client's sign-extended 16-bit segment
    // (the client's raw integer lookup, a (int)(short) cast): it records the
    // stored 65535 where the client reads -1. Of the 56 shipped 16-bit values at or above 0x8000, these are the
    // only ones that reach a ChampionData field (the rest are AttackDelayOffsetPercent and unread keys). The
    // fixture is never re-recorded, so the check pins the recorded unsigned value and expects the client's -1.
    private static readonly (string Field, string[] Characters)[] SignedSegmentMinusOnes =
    [
        ("mp_regen", ["MaokaiSproutling", "OdinMinionGraveyardPortal", "OdinMinionSpawnPortal",
            "OdinQuestIndicator", "OriannaBall", "TeemoMushroom"]),
    ];

    private static IEnumerable<KeyValuePair<string, double>> SignedSegmentCorrections(string character, JsonElement recorded)
    {
        foreach (var entry in SignedSegmentMinusOnes.Where(entry => entry.Characters.Contains(character)))
        {
            TestAssert.Equal(65535.0, recorded.GetProperty(entry.Field).GetDouble(),
                $"{character}.{entry.Field} fixture records the unsigned 16-bit value");
            yield return KeyValuePair.Create(entry.Field, -1.0);
        }
    }

    // Batch A10 (18 October 2026). The same fixture also predates the client's character loader defaults for an ABSENT key
    // (the client's character loader): BaseHP/BaseMP 100, HP/MPPerLevel 10, BaseStaticHP/MPRegen 0.3, BaseDamage and
    // DamagePerLevel 10, Armor/ArmorPerLevel 1, AttackRange 100, MoveSpeed 100; the fixture records 0 for them. They only reach the
    // structures, the two super minions, the wards and a few effect units, which lack those keys. The audit (`audit data`, check
    // tools.audit.data) compares every unit against the independent client-rule reader; this table is the same defaults.
    private static readonly (string Field, string Key, double Default)[] ClientCharacterDefaults =
    [
        ("base_hp", "BaseHP", 100.0), ("base_mp", "BaseMP", 100.0), ("hp_per_level", "HPPerLevel", 10.0),
        ("mp_per_level", "MPPerLevel", 10.0), ("hp_regen", "BaseStaticHPRegen", 0.3), ("mp_regen", "BaseStaticMPRegen", 0.3),
        ("base_damage", "BaseDamage", 10.0), ("damage_per_level", "DamagePerLevel", 10.0), ("armor", "Armor", 1.0),
        ("armor_per_level", "ArmorPerLevel", 1.0), ("move_speed", "MoveSpeed", 100.0), ("attack_range", "AttackRange", 100.0),
    ];

    private static IEnumerable<KeyValuePair<string, double>> AbsentKeyDefaults(InibinData raw)
    {
        foreach (var (field, key, fallback) in ClientCharacterDefaults)
            if (!raw.ContainsKey(InibinReader.KeyHash("Data*" + key)))
                yield return KeyValuePair.Create(field, fallback);
    }


    // An effect the fixture lacks but the loader now has: Effect{N}Level{R}Amount at some rank is a boolean-segment 0/1 (the old
    // reader treated that as a missing key and dropped the whole effect).
    private static HashSet<int> ExplainedBooleanEffects(SpellData spell)
    {
        var explained = new HashSet<int>();
        foreach (var (effect, values) in spell.Effects)
        {
            var viaBoolean = Enumerable.Range(1, values.Count).Any(rank =>
                spell.Raw.TryGetValue(InibinReader.KeyHash($"SpellData*Effect{effect}Level{rank}Amount"), out var raw) &&
                raw.Kind == InibinValueKind.Boolean);
            if (viaBoolean) explained.Add(effect);
        }
        return explained;
    }

    private static void CompareObject(JsonElement expected, object actual, bool effects = false,
        IReadOnlyDictionary<string, double>? corrections = null, IReadOnlySet<int>? extraEffects = null)
    {
        var properties = actual.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name), StringComparer.Ordinal);
        foreach (var expectedProperty in expected.EnumerateObject())
        {
            if (expectedProperty.NameEquals("raw")) continue;
            if (effects && expectedProperty.NameEquals("effects")) continue;
            if (!properties.TryGetValue(expectedProperty.Name, out var property))
                throw new InvalidOperationException($"Missing mapped property {expectedProperty.Name} on {actual.GetType().Name}");
            var actualValue = property.GetValue(actual);
            if (corrections is not null && corrections.TryGetValue(expectedProperty.Name, out var corrected))
            {
                TestAssert.Near(corrected, Convert.ToDouble(actualValue, System.Globalization.CultureInfo.InvariantCulture),
                    FixtureTolerance(corrected), $"{actual.GetType().Name}.{expectedProperty.Name} (client-rule correction)");
                continue;
            }
            CompareValue(expectedProperty.Value, actualValue, $"{actual.GetType().Name}.{expectedProperty.Name}");
        }
        if (effects)
        {
            var expectedEffects = expected.GetProperty("effects");
            var actualEffects = (IReadOnlyDictionary<int, IReadOnlyList<InibinValue>>)actual.GetType().GetProperty("Effects")!.GetValue(actual)!;
            var recordedEffects = expectedEffects.EnumerateObject().Select(entry =>
                int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            var extras = actualEffects.Keys.Where(key => !recordedEffects.Contains(key)).ToArray();
            foreach (var extra in extras)
                TestAssert.True(extraEffects?.Contains(extra) == true,
                    $"effect {extra} of {actual.GetType().GetProperty("Name")?.GetValue(actual)} is new without a boolean-segment amount");
            TestAssert.Equal(expectedEffects.EnumerateObject().Count(), actualEffects.Count - extras.Length,
                $"effects count for {actual.GetType().GetProperty("Name")?.GetValue(actual)}");
            foreach (var entry in expectedEffects.EnumerateObject())
            {
                var rankValues = actualEffects[int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture)];
                CompareValue(entry.Value, rankValues, $"effects[{entry.Name}]");
            }
        }
    }

    // The stock client reads a byte/10 inibin value as the float32 (float)b * 0.1f and the fixture recorded the decimal b/10, which differ
    // by at most two float32 roundings (under 2e-7 relative); everything else is compared at the original 1e-9.
    private static double FixtureTolerance(double expected) => Math.Max(1e-9, Math.Abs(expected) * 2e-7);

    private static void CompareValue(JsonElement expected, object? actual, string path)
    {
        if (expected.ValueKind == JsonValueKind.Number)
        {
            if (actual is null) throw new InvalidOperationException($"Missing value {path}");
            if (actual is InibinValue inibinValue) actual = inibinValue.AsDouble();
            TestAssert.Near(expected.GetDouble(), Convert.ToDouble(actual, System.Globalization.CultureInfo.InvariantCulture),
                FixtureTolerance(expected.GetDouble()), path);
            return;
        }
        if (expected.ValueKind == JsonValueKind.String)
        {
            if (actual is InibinValue inibinValue) actual = inibinValue.AsString();
            TestAssert.Equal(expected.GetString(), actual as string, path);
            return;
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual is not System.Collections.IEnumerable values)
                throw new InvalidOperationException($"{path} is not enumerable");
            var actualValues = values.Cast<object?>().ToArray();
            var expectedValues = expected.EnumerateArray().ToArray();
            TestAssert.Equal(expectedValues.Length, actualValues.Length, path);
            for (var i = 0; i < expectedValues.Length; i++) CompareValue(expectedValues[i], actualValues[i], $"{path}[{i}]");
            return;
        }
        throw new InvalidOperationException($"Unsupported fixture value at {path}: {expected.ValueKind}");
    }
}
