using System.Globalization;
using Amberline.Data;

namespace Amberline.Tools.Audits;

internal static class AuditLuaValues
{
    public static IEnumerable<LuaValue> Walk(IEnumerable<LuaValue> values)
    {
        foreach (var value in values)
        {
            yield return value;
            if (value is LuaValue.Map map)
            {
                foreach (var child in map.Values)
                {
                    foreach (var nested in Walk(new[] { child.Key })) yield return nested;
                    foreach (var nested in Walk(new[] { child.Value })) yield return nested;
                }
            }
            else if (value is LuaValue.Array array)
            {
                foreach (var nested in Walk(array.Values)) yield return nested;
            }
        }
    }

    public static LuaValue? Get(LuaValue value, string key)
    {
        if (value is not LuaValue.Map map) return null;
        return map.Values.FirstOrDefault(pair => pair.Key is LuaValue.String text &&
            string.Equals(text.Value, key, StringComparison.Ordinal)).Value;
    }

    public static string? Text(LuaValue? value) => value switch
    {
        LuaValue.String text => text.Value,
        LuaValue.Number number => number.Value.ToString("R", CultureInfo.InvariantCulture),
        LuaValue.Boolean boolean => boolean.Value.ToString(),
        LuaValue.Global global => global.Name,
        LuaValue.Nil => null,
        _ => null,
    };

    public static double? Number(LuaValue? value) => value switch
    {
        LuaValue.Number number => number.Value,
        LuaValue.Boolean boolean => boolean.Value ? 1.0 : 0.0,
        LuaValue.String text when double.TryParse(text.Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static IReadOnlyList<double> Numbers(LuaValue? value)
    {
        if (value is LuaValue.Array array)
            return array.Values.Select(Number).Select(number => number ??
                throw new InvalidDataException("Lua audit array contains a non-number")).ToArray();
        var scalar = Number(value);
        return scalar.HasValue ? new[] { scalar.Value } :
            throw new InvalidDataException("Lua audit value is not numeric");
    }

    public static bool ParametersMatch(LuaValue? value,
        IReadOnlyDictionary<string, string> expected)
    {
        foreach (var pair in expected)
            if (!string.Equals(Text(Get(value!, pair.Key)), pair.Value,
                    StringComparison.Ordinal)) return false;
        return true;
    }
}
