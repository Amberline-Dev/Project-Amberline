using System.Text.Json;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Tools.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Tests.Network;

namespace Amberline.Tests;

public static class SessionReplayChecks
{


    private static JsonArray RecordSession(JsonElement session, JsonElement replayed)
    {
        var records = session.GetProperty("records").EnumerateArray().ToArray();
        var templates = records.Where(IsOutbound).ToArray();
        TestAssert.True(templates.Length > 0, "session must retain an outbound layout template");
        var packets = replayed.EnumerateArray().ToArray();
        var recorded = new JsonArray();
        var outbound = new JsonArray();
        var replay = new JsonArray();
        var inbound = new JsonArray();
        var replayTemplates = session.GetProperty("expectedReplay").EnumerateArray().ToArray();
        TestAssert.True(replayTemplates.Length > 0, "session must retain a replay layout template");
        var packetIndex = 0;
        var lastInboundTime = JsonSerializer.SerializeToElement(0.0);
        foreach (var record in records)
        {
            if (IsOutbound(record))
            {
                if (packetIndex < packets.Length) AddPacket(record, record.GetProperty("t"));
                continue;
            }
            recorded.Add(JsonNode.Parse(record.GetRawText()));
            if (record.GetProperty("cat").GetString() == "NET" &&
                record.GetProperty("msg").GetString() == "recv")
            {
                lastInboundTime = record.GetProperty("t");
                inbound.Add(new JsonObject
                {
                    ["t"] = JsonNode.Parse(lastInboundTime.GetRawText()),
                    ["ch"] = JsonNode.Parse(record.GetProperty("ch").GetRawText()),
                    ["data"] = new JsonObject { ["$bytes"] = record.GetProperty("hex").GetString() },
                });
            }
        }
        // Corresponding slots keep their original time. Extra packets follow
        // the last record at the last inbound time; outbound time is metadata.
        while (packetIndex < packets.Length) AddPacket(templates[^1], lastInboundTime);
        CorpusSnapshot.Set(session.GetProperty("records"), recorded);
        CorpusSnapshot.Set(session.GetProperty("expectedOutbound"), outbound);
        CorpusSnapshot.Set(session.GetProperty("expectedReplay"), replay);
        CorpusSnapshot.Set(session.GetProperty("expectedInbound"), inbound);
        return recorded;

        void AddPacket(JsonElement template, JsonElement time)
        {
            var packet = packets[packetIndex];
            var row = RecordPacket(template, packet, time);
            recorded.Add(row);
            outbound.Add(row.DeepClone());
            replay.Add(RecordPacket(replayTemplates[Math.Min(packetIndex, replayTemplates.Length - 1)], packet, time));
            packetIndex++;
        }
    }

    private static bool IsOutbound(JsonElement record) =>
        record.GetProperty("cat").GetString() == "PKT" && record.GetProperty("msg").GetString() == "->";

    private static JsonObject RecordPacket(JsonElement template, JsonElement packet, JsonElement time)
    {
        var row = new JsonObject();
        var fields = packet.GetProperty("Fields");
        var fieldsWritten = false;
        foreach (var property in template.EnumerateObject())
        {
            var envelope = property.Name is "op" or "name" or "n" or "ch" or "rel" or
                "raw" or "truncated" or "t" or "cat" or "lvl" or "msg" or "mono_ns" or "session_id";
            if (!fieldsWritten && (!envelope || property.Name == "t"))
            {
                foreach (var field in fields.EnumerateObject())
                    row[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                fieldsWritten = true;
            }
            if (!envelope) continue;
            row[property.Name] = property.Name switch
            {
                "op" => JsonValue.Create("0x" + packet.GetProperty("op").GetInt32().ToString(
                    property.Value.GetString()!.Any(c => c is >= 'a' and <= 'f') ? "x2" : "X2")),
                "name" => JsonNode.Parse(packet.GetProperty("Name").GetRawText()),
                "n" => JsonNode.Parse(packet.GetProperty("Length").GetRawText()),
                "ch" => JsonNode.Parse(packet.GetProperty("Channel").GetRawText()),
                "rel" => JsonNode.Parse(packet.GetProperty("Reliability").GetRawText()),
                "raw" => JsonValue.Create(property.Value.GetString()!.Any(c => c is >= 'A' and <= 'F')
                    ? packet.GetProperty("Raw").GetString()!.ToUpperInvariant() : packet.GetProperty("Raw").GetString()),
                "truncated" => JsonValue.Create(packet.GetProperty("Raw").GetString()!.Length / 2 < packet.GetProperty("Length").GetInt32()),
                "t" => JsonNode.Parse(time.GetRawText()),
                _ => JsonNode.Parse(property.Value.GetRawText()),
            };
        }
        return row;
    }


    private static bool JsonEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return expected.TryGetDouble(out var e) && actual.TryGetDouble(out var a) && e == a;
        if (expected.ValueKind != actual.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var ep = expected.EnumerateObject().ToDictionary(item => item.Name, item => item.Value,
                StringComparer.Ordinal);
            var ap = actual.EnumerateObject().ToDictionary(item => item.Name, item => item.Value,
                StringComparer.Ordinal);
            return ep.Count == ap.Count && ep.Keys.All(key => ap.TryGetValue(key, out var value) &&
                JsonEqual(ep[key], value));
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            var ea = expected.EnumerateArray().ToArray();
            var aa = actual.EnumerateArray().ToArray();
            return ea.Length == aa.Length && ea.Zip(aa).All(pair => JsonEqual(pair.First, pair.Second));
        }
        return expected.ToString() == actual.ToString();
    }

}
