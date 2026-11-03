using Amberline.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amberline.Tests;

// Output adapters select fields explicitly; this only preserves the parsed
// source layout and unchanged numeric spelling. FixtureRecorder owns writing.
internal static class CorpusSnapshot
{
    private static readonly Dictionary<nint, (JsonNode Parent, object Key)> Fields = new();
    private static JsonElement Source;
    internal static JsonNode Root { get; private set; } = null!;

    internal static JsonDocument Read(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        if (FixtureRecorder.Enabled)
        {
            var directory = Environment.GetEnvironmentVariable("AMBERLINE_RECORD_DIR");
            if (!string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, fixture)))
                path = Path.Combine(directory, fixture);
        }
        var text = File.ReadAllText(path);
        var document = JsonDocument.Parse(text);
        if (FixtureRecorder.Enabled)
        {
            Root = JsonNode.Parse(text)!;
            if (Root is JsonObject root && (root.ContainsKey("source") || root.ContainsKey("provenance")))
                root["recordingNote"] = "Outputs re-recorded from the current build on 28 September 2026; original inputs and source metadata retained.";
            Source = document.RootElement;
            Fields.Clear();
            Index(document.RootElement, Root);
        }
        return document;
    }

    // JsonElement's default hash collides for elements of the same document.
    // The public .NET raw-span API gives each value a stable document offset.
    private static nint Offset(JsonElement element) => Unsafe.ByteOffset(
        ref MemoryMarshal.GetReference(JsonMarshal.GetRawUtf8Value(Source)),
        ref MemoryMarshal.GetReference(JsonMarshal.GetRawUtf8Value(element)));

    private static void Index(JsonElement element, JsonNode node)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                Fields.Add(Offset(property.Value), (node, property.Name));
                if (node[property.Name] is { } child) Index(property.Value, child);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            for (var i = 0; i < element.GetArrayLength(); i++)
            {
                Fields.Add(Offset(element[i]), (node, i));
                if (node[i] is { } child) Index(element[i], child);
            }
    }

    internal static void Set(JsonElement field, object? actual)
    {
        var (parent, key) = Fields[Offset(field)];
        var value = JsonSerializer.SerializeToNode(actual);
        var original = key is int index ? parent[index] : parent[(string)key];
        var merged = Merge(original, value);
        if (ReferenceEquals(original, merged)) return;
        if (key is int item) parent[item] = merged;
        else parent[(string)key] = merged;
    }

    internal static string? SourceKind(DamageSourceKind? kind) => kind switch
    {
        DamageSourceKind.SpellAoe => "spell_aoe", DamageSourceKind.SpellPersist => "spell_persist",
        DamageSourceKind.WormAttack => "worm_attack", DamageSourceKind.InternalRaw => "internal_raw",
        _ => kind?.ToString().ToLowerInvariant(),
    };

    [Check("tools.corpus-snapshot-merge")]
    public static void MergePreservesUnchangedNumbersAndParents()
    {
        using var source = JsonDocument.Parse("[0,0,0]");
        Source = source.RootElement;
        TestAssert.Equal(3, Source.EnumerateArray().Select(Offset).Distinct().Count());
        var original = JsonNode.Parse("{\"rows\":[{\"same\":1.0,\"changed\":2},3]}")!;
        var actual = JsonNode.Parse("{\"rows\":[{\"same\":1,\"changed\":4},3,5]}")!;
        var merged = Merge(original, actual)!;
        TestAssert.True(JsonNode.DeepEquals(actual, merged));
        TestAssert.True(merged.ToJsonString().Contains("1.0", StringComparison.Ordinal));
        TestAssert.True(ReferenceEquals(merged, Merge(merged, actual)));
        // Batch 208: a null over a key the paired original row lacks is kept (the row shifted by one position).
        var shifted = Merge(JsonNode.Parse("[{\"name\":\"ActionTimer\"}]")!, JsonNode.Parse("[{\"name\":\"WormRecouperateOn\",\"max_stack\":null}]")!)!;
        TestAssert.True(shifted[0]!.AsObject().ContainsKey("max_stack"));
        TestAssert.True(shifted[0]!["max_stack"] is null);
    }

    private static JsonNode? Merge(JsonNode? original, JsonNode? actual)
    {
        if (JsonNode.DeepEquals(original, actual)) return original;
        if (original is JsonObject oldObject && actual is JsonObject newObject)
        {
            foreach (var key in oldObject.Select(p => p.Key).Except(newObject.Select(p => p.Key)).ToArray())
                oldObject.Remove(key);
            foreach (var (key, value) in newObject)
            {
                var previous = oldObject[key];
                var merged = Merge(previous, value?.DeepClone());
                // Batch 208: a null over a key the original lacks is written too. Arrays pair by position, so after a
                // buff leaves the list the new row can meet an old row without that key (batch 201's carriers).
                if (!ReferenceEquals(previous, merged) || !oldObject.ContainsKey(key)) oldObject[key] = merged;
            }
            return oldObject;
        }
        if (original is JsonArray oldArray && actual is JsonArray newArray)
        {
            while (oldArray.Count > newArray.Count) oldArray.RemoveAt(oldArray.Count - 1);
            for (var i = 0; i < newArray.Count; i++)
                if (i < oldArray.Count)
                {
                    var previous = oldArray[i];
                    var merged = Merge(previous, newArray[i]?.DeepClone());
                    if (!ReferenceEquals(previous, merged)) oldArray[i] = merged;
                }
                else oldArray.Add(newArray[i]?.DeepClone());
            return oldArray;
        }
        return actual;
    }
}
