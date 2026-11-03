using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Tools;

namespace Amberline.Tests;

/// <summary>Fixture-specific Lua VM conformance belongs with the tests.</summary>
internal static class LuaVmConformanceChecks
{
    public static void Run(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var failures = new List<string>();
        var total = 0;
        foreach (var item in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            total++;
            var name = item.GetProperty("name").GetString()!;
            var expectedError = item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
            try
            {
                var vm = LuaVm.FromCase(item.GetProperty("globals"), CorpusStub);
                var result = vm.Run(LuaVm.ReadProto(item.GetProperty("prototype")), ReadArray(item.GetProperty("args")).ToArray());
                if (expectedError)
                    failures.Add($"{name}: expected {error.GetProperty("type").GetString()}");
                else
                {
                    if (!SameJson(LuaVm.Normalize(result), item.GetProperty("result")))
                        failures.Add($"{name}: result mismatch");
                    var actualGlobals = LuaVm.Normalize(vm.Globals.ToDictionary(x => x.Key, x => x.Value));
                    if (item.TryGetProperty("afterGlobals", out var after) && !SameJson(actualGlobals, after))
                        failures.Add($"{name}: globals mismatch");
                }
            }
            catch (LuaVmException ex)
            {
                if (!expectedError) failures.Add($"{name}: {ex.Message}");
                else if (!MatchesLuaError(ex.Message, error)) failures.Add($"{name}: error mismatch");
            }
        }

        foreach (var failure in failures.Take(20)) Console.WriteLine($"FAIL {failure}");
        TestAssert.Equal(0, failures.Count == 0 ? 0 : 1,
            $"lua-vm: {total - failures.Count}/{total} cases passed");
    }

    private static LuaStub CorpusStub(string name) => name switch
    {
        "identity" => LuaVm.CreateStub(name, args => args.Length == 0 ? Array.Empty<object?>() : args.Length == 1 ? new[] { args[0] } : args),
        "sum2" => LuaVm.CreateStub(name, args => new object?[]
        {
            Number(args.ElementAtOrDefault(0)) + Number(args.ElementAtOrDefault(1))
        }),
        "pairValues" => LuaVm.CreateStub(name, _ => new object?[] { 3d, 5d }),
        "nextPair" => LuaVm.CreateStub(name, args =>
        {
            if (args.Length < 2 || args[0] is not LuaTable table) return Array.Empty<object?>();
            var keys = table.Entries.Select(entry => entry.Key).ToArray();
            var next = args[1] is null ? 0 : Array.IndexOf(keys, LuaTable.Key(args[1]!)) + 1;
            return next >= 0 && next < keys.Length ? new[] { keys[next], table.Get(keys[next]) } : Array.Empty<object?>();
        }),
        _ => throw new LuaVmException($"unknown Lua stub '{name}'")
    };

    private static double Number(object? value) => value is double number
        ? number
        : throw new LuaVmException("corpus sum2 expects numeric arguments");

    private static List<object?> ReadArray(JsonElement element) => element.ValueKind == JsonValueKind.Array
        ? element.EnumerateArray().Select(ReadScalar).ToList()
        : [];

    private static object? ReadScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        _ => throw new LuaVmException("corpus argument must be scalar")
    };

    private static bool SameJson(object? actual, JsonElement expected) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(actual), JsonNode.Parse(expected.GetRawText()));

    private static bool MatchesLuaError(string actual, JsonElement expected)
    {
        if (!expected.TryGetProperty("message", out var message)) return false;
        var prefix = message.GetString() ?? string.Empty;
        var comment = prefix.IndexOf(" --", StringComparison.Ordinal);
        if (comment >= 0) prefix = prefix[..comment];
        var detail = prefix.IndexOf(" (", StringComparison.Ordinal);
        if (detail >= 0 && prefix.StartsWith("unsupported opcode", StringComparison.Ordinal)) prefix = prefix[..detail];
        var pc = prefix.IndexOf(" at pc=", StringComparison.Ordinal);
        if (pc >= 0) prefix = prefix[..pc];
        return actual.StartsWith(prefix, StringComparison.Ordinal);
    }
}
