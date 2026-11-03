using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static class WireDiagnosticsChecks
{
    // Batch 291: the 0xC2's movement data (the client's movement data reader) decodes like the packets it carries the entries of: type 1 a 0x67 entry, type 2 a
    // 0x64 entry (each with its teleport flag and id), type 3 a stop, read as a one-point route.
    [Check("tools.wire.reveal-movement-data")]
    public static void RevealMovementDataDecodes()
    {
        var grid = NavigationGrid.LoadDefault();
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        const uint unit = 0x40010001;
        Dictionary<string, object?> Fields(byte[] movement) => WirePacketDecoder.Decode(VisionPackets.EnterVisibilityClient(unit, "Minion",
            position: new Position2(3000, 9000), movementData: movement).Plaintext.Span, 3).Fields.ToDictionary();

        var route = Fields(VisionPackets.RouteMovementData(unit, [new(3000, 9000), new(3100, 9100), new(3600, 9100)], bounds, 41, 7)!);
        TestAssert.Equal(unit, route["net_id"]);
        TestAssert.Equal((byte)2, route["movement_type"]);
        TestAssert.Equal(41u, route["sync_id"]);
        TestAssert.Equal((byte)7, route["teleport_id"], "the route entry's teleport id");
        TestAssert.Equal(3, ((object?[])route["waypoints"]!).Length);

        var stop = Fields(VisionPackets.StopMovementData(42, new Position2(3000, 9000), new Position2(0, 1)));
        TestAssert.Equal((byte)3, stop["movement_type"]);
        var at = (double[])((object?[])stop["waypoints"]!).Single()!;
        TestAssert.Equal(3000.0, at[0], "the stop's position is its one point");
        TestAssert.False(stop.ContainsKey("teleport_id"));

        var dash = Fields(VisionPackets.DashMovementData(unit, new Position2(3000, 9000), new Position2(3475, 9000), 2000f, bounds, 43,
            teleportId: 9));
        TestAssert.Equal((byte)1, dash["movement_type"]);
        TestAssert.Equal((byte)9, dash["teleport_id"], "the dash entry's teleport id");
        TestAssert.Near(2000, (double)dash["speed"]!, 1e-6, "the speed data after the teleport id");
        TestAssert.Equal(2, ((object?[])dash["waypoints"]!).Length);
    }

    [Check("tools.wire.channel-four-emote-prefix")]
    public static void MovementChannelPrefixIsNotAnEmote()
    {
        var payload = new byte[] { 0x48, 1, 2, 3, 4, 5, 6, 7, 8 };
        var movement = WirePacketDecoder.Decode(payload, 4);
        TestAssert.Equal("UnparsedMovementChannel", movement.Name);
        TestAssert.False(movement.HasDecoder);
        TestAssert.Equal("UnparsedMovementChannel", WirePacketDecoder.LogName(0x48, 4));
        var emote = WirePacketDecoder.Decode(payload, LeagueProtocolConstants.CombatChannel);
        TestAssert.Equal("PlayEmote", emote.Name);
        TestAssert.True(emote.HasDecoder);
    }


    [Check("tools.wire.diagnostics.tooltip-selector-kinds")]
    public static void TooltipSelectorKinds()
    {
        var packet = new byte[7 + 17 * 3];
        packet[0] = 0x83;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), 3);
        foreach (var (selector, index) in new[] { (0, 0), (60, 1), (120, 2) })
        {
            var offset = 7 + index * 17;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), 1);
            packet[offset + 4] = checked((byte)selector);
        }

        var decoded = WirePacketDecoder.Decode(packet, LeagueProtocolConstants.CombatChannel);
        var fields = JsonSerializer.SerializeToElement(decoded.Fields, JsonOptions);
        var entries = fields.GetProperty("entries");
        foreach (var (index, kind, slot) in new[]
                 { (0, "buff", 0), (1, "spellbook", 0), (2, "alternate_spellbook", 0) })
        {
            TestAssert.Equal(kind, entries[index].GetProperty("selector_kind").GetString());
            TestAssert.Equal(slot, entries[index].GetProperty("slot").GetInt32());
        }
    }

    private static JsonElement AdaptArchivedTooltipSelectors(JsonElement expected)
    {
        if (!expected.TryGetProperty("entries", out _)) return expected;
        var root = JsonNode.Parse(expected.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("tooltip expectation is not an object");
        if (root["entries"] is not JsonArray entries)
            throw new InvalidOperationException("tooltip expectation entries is not an array");
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || entry["selector"] is not JsonValue selectorValue)
                throw new InvalidOperationException("tooltip expectation entry is malformed");
            var selector = selectorValue.GetValue<byte>();
            entry["selector_kind"] = selector < 0x3C ? "buff" :
                selector < 0x78 ? "spellbook" : "alternate_spellbook";
            entry["slot"] = selector < 0x3C ? selector :
                selector < 0x78 ? selector - 0x3C : selector - 0x78;
        }
        return JsonSerializer.SerializeToElement(root, JsonOptions);
    }

    // [0xC2][net id][item count 0][look-at 0][type 3][sync id][x][z][facing x][facing z]: the fields present in a prefix of that.
    private static Dictionary<string, object?> RetainedEnterVisibility(byte[] input)
    {
        var fields = new Dictionary<string, object?> { ["op"] = input[0], ["len"] = input.Length, ["name"] = "EnterVisibility" };
        if (input.Length < 7) return fields;
        TestAssert.True(input[5] == 0 && input[6] == 0, "the retained samples have no items and no look-at");
        fields["net_id"] = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(1));
        if (input.Length < 12) return fields;
        TestAssert.Equal((byte)3, input[7], "the retained samples are stops");
        fields["movement_type"] = input[7];
        fields["sync_id"] = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8));
        if (input.Length < 28) return fields;
        double At(int offset) => BinaryPrimitives.ReadSingleLittleEndian(input.AsSpan(offset));
        fields["waypoints"] = new[] { new[] { At(12), At(16) } };
        fields["facing"] = new[] { At(20), At(24) };
        return fields;
    }

    private static bool Equal(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("bytesHex", out var bytes))
            return actual.ValueKind == JsonValueKind.String &&
                TryPythonBytes(actual.GetString() ?? string.Empty, out var actualBytes) &&
                string.Equals(actualBytes, bytes.GetString(), StringComparison.OrdinalIgnoreCase);
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("nonFinite", out var nonFinite))
            return actual.ValueKind == JsonValueKind.String &&
                string.Equals(actual.GetString(), nonFinite.GetString(), StringComparison.OrdinalIgnoreCase);
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return expected.TryGetDouble(out var e) && actual.TryGetDouble(out var a) && e == a;
        if (expected.ValueKind != actual.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var ep = expected.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            var ap = actual.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            return ep.Count == ap.Count && ep.Keys.All(k => ap.TryGetValue(k, out var value) && Equal(ep[k], value));
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            var ea = expected.EnumerateArray().ToArray(); var aa = actual.EnumerateArray().ToArray();
            return ea.Length == aa.Length && ea.Zip(aa).All(pair => Equal(pair.First, pair.Second));
        }
        return expected.ToString() == actual.ToString();
    }

    private static bool TryPythonBytes(string value, out string hex)
    {
        hex = string.Empty;
        if (!value.StartsWith("b'", StringComparison.Ordinal) || !value.EndsWith("'", StringComparison.Ordinal))
            return false;
        var bytes = new List<byte>();
        for (var i = 2; i < value.Length - 1; i++)
        {
            if (value[i] != '\\') { bytes.Add((byte)value[i]); continue; }
            if (++i >= value.Length - 1) return false;
            switch (value[i])
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case '\'': bytes.Add((byte)'\''); break;
                case 'x' when i + 2 < value.Length - 1 &&
                    byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                    bytes.Add(b); i += 2; break;
                default: return false;
            }
        }
        hex = Convert.ToHexString(bytes.ToArray()).ToLowerInvariant();
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
