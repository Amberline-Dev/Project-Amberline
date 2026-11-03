using System.Text.Json;
using Amberline.Data;
using Amberline.Tools.Audits;

namespace Amberline.Tests;

public static class AuditChecks
{


    private static void CompareStats(JsonElement source, ChampionStatsAudit actual)
    {
        FixtureNear(source.GetProperty("max_hp").GetDouble(), actual.MaxHp);
        FixtureNear(source.GetProperty("max_mp").GetDouble(), actual.MaxMp);
        FixtureNear(source.GetProperty("attack_damage").GetDouble(), actual.AttackDamage);
        FixtureNear(source.GetProperty("armor").GetDouble(), actual.Armor);
        FixtureNear(source.GetProperty("spell_block").GetDouble(), actual.SpellBlock);
        FixtureNear(source.GetProperty("hp_regen").GetDouble(), actual.HpRegen);
        FixtureNear(source.GetProperty("mp_regen").GetDouble(), actual.MpRegen);
        FixtureNear(source.GetProperty("attack_speed_mod").GetDouble(), actual.AttackSpeedMod);
        FixtureNear(source.GetProperty("move_speed").GetDouble(), actual.MoveSpeed);
        FixtureNear(source.GetProperty("attack_range").GetDouble(), actual.AttackRange);
    }

    private static void CompareRelated(JsonElement champion, IReadOnlyList<string>? actual)
    {
        var expected = champion.TryGetProperty("related_scripts", out var value)
            ? value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Select(item => Path.GetFileNameWithoutExtension(item.Name)).ToArray()
                : ArrayOfStrings(value)
            : Array.Empty<string>();
        var values = actual ?? Array.Empty<string>();
        TestAssert.Equal(expected.Length, values.Count);
        for (var index = 0; index < expected.Length; index++)
            TestAssert.Equal(expected[index], values[index]);
    }

    // A byte/10 inibin value is the client's float32 (float)b * 0.1f (the client's raw float lookup), which differs from the decimal
    // b/10 these fixtures recorded by at most two float32 roundings, under 2e-7 relative.
    private static void FixtureNear(double expected, double actual) =>
        TestAssert.Near(expected, actual, Math.Max(1e-9, Math.Abs(expected) * 2e-7));

    // Batch 187 (the cast-range reader, GameData.CastRange): the client lifts a shipped cast range at or below 1.0 to 1.0, and so does
    // the engine. The audit keeps the raw shipped entry in its fixture (source-backed, not re-recorded) and restates the
    // expectation as the floored read: ArchersMark, CardmasterStack and MoltenShield record 0 and read 1.
    private static double FlooredCastRange(JsonElement source) => Math.Max(1.0, source.GetProperty("cast_range").GetDouble());

    private static void CompareNumbers(JsonElement source, IReadOnlyList<double> actual)
    {
        var expected = source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().Select(value => value.GetDouble()).ToArray()
            : [source.GetDouble()];
        TestAssert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
            FixtureNear(expected[index], actual[index]);
    }

    private static string[] ArrayOfStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
        : Array.Empty<string>();

    // Batch 64: an effect whose Effect{N}Level{R}Amount is a boolean-segment 0/1 used to vanish from the loader (the old reader treated the
    // key as missing); the recorded roster lacks those effects. An effect the fixture lacks must be explained that way (the same rule as
    // data.gamedata.corpus), and the count that is compared excludes only those.
    private static int ExtraEffects(ChampionSpellAudit spell, JsonElement recorded)
    {
        var recordedIds = recorded.EnumerateObject().Select(entry => int.Parse(entry.Name)).ToHashSet();
        var raw = GameData.Default.Spell(spell.Name).Raw;
        var extras = spell.Effects.Keys.Where(key => !recordedIds.Contains(key)).ToArray();
        foreach (var extra in extras)
            TestAssert.True(Enumerable.Range(1, spell.Effects[extra].Count).Any(rank =>
                    raw.TryGetValue(InibinReader.KeyHash($"SpellData*Effect{extra}Level{rank}Amount"), out var value) &&
                    value.Kind == InibinValueKind.Boolean),
                $"effect {extra} of {spell.Name} is new without a boolean-segment amount");
        return extras.Length;
    }

}
