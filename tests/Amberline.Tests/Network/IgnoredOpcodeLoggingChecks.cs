using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Server;

namespace Amberline.Tests.Network;

public static class IgnoredOpcodeLoggingChecks
{
    [Check("server.authentication-log-fields")]
    public static void AuthenticationDiagnosticsKeepMetadataOnlyInBothModes()
    {
        var source = FindWorkspaceFile(Path.Combine("src", "Amberline", "Network", "Sessions",
            "MatchRuntimeNetwork.cs"));
        var text = File.ReadAllText(source);
        foreach (var eventName in new[] { "auth.accepted", "reconnect.replaced-connection" })
        {
            var start = text.IndexOf(eventName, StringComparison.Ordinal);
            var end = text.IndexOf("});", start, StringComparison.Ordinal);
            TestAssert.True(start >= 0 && end > start, eventName + " log body remains discoverable");
            TestAssert.False(text[start..end].Contains("player_id", StringComparison.Ordinal),
                eventName + " must not persist decoded credential fields");
        }
        // Trace mode changes packet-detail sampling only; these field sets are
        // shared by both ServerLogger modes.
        TestAssert.True(text.Contains("[\"redacted\"] = true", StringComparison.Ordinal));
    }

    [Check("server.ignored-opcode-logging")]
    public static void ClassifiesStockClientIgnoredPackets()
    {
        var oldTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE");
        try
        {
            foreach (var trace in new string?[] { null, "1" })
            {
                Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", trace);
                ClassifyStockClientIgnoredPacketsOnce();
            }
        }
        finally { Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", oldTrace); }
    }

    private static void ClassifyStockClientIgnoredPacketsOnce()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "amberline-ignored-opcodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logger = ServerLogger.Open(root, new StringWriter());
        try
        {
            var runtime = MatchRuntime.CreateProduction(
                NavigationGrid.LoadDefault(), GameData.Default, "Annie", solo: true,
                readySettleSeconds: 0.0, sessionLog: logger);
            using var server = new MatchRuntimeNetwork(runtime,
                new LeagueEnetHost(0, 2, 8, bindHost: "127.0.0.1"), ownsHost: true, log: logger);
            using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var peer = client.Connect("127.0.0.1", server.Port, 8);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var connected = false;
            while (!connected)
            {
                server.Pump(1, 32, flushDelivery: false);
                var @event = client.Service(0);
                connected |= @event.Type == LeagueEnetEventType.Connect;
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("ignored-opcode probe did not connect");
            }

            // Match the source harness before classifying gameplay-channel
            // traffic: the logger must see these packets on an authenticated
            // session, not merely on a raw ENet peer.
            var keyCheck = runtime.Session.BuildKeyCheck(1);
            var keyHex = Convert.ToHexString(keyCheck).ToLowerInvariant();
            peer.Send(0, keyCheck, LeagueEnetPacketFlags.Reliable);
            client.Flush();
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   (server.ConnectionIds.Count == 0 ||
                    runtime.Session.Participant(server.ConnectionIds[0]) is null))
            {
                server.Pump(1, 32, flushDelivery: false);
                while (client.Service(0).Type != LeagueEnetEventType.None) { }
            }
            TestAssert.True(server.ConnectionIds.Count != 0 &&
                            runtime.Session.Participant(server.ConnectionIds[0]) is not null,
                "ignored-opcode probe did not authenticate");

            var face = new byte[]
            {
                0x53, 0, 0, 0, 0,
                0, 0, 0x80, 0x3F, // 1.0f
                0, 0, 0, 0,
                0, 0, 0, 0,
            };
            peer.Send(1, new byte[] { 0x30, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            var waypointAck = new byte[15]; waypointAck[0] = 0x7A;
            peer.Send(1, runtime.Cipher.Encrypt(waypointAck), LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0x99, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            var encryptedFace = runtime.Cipher.Encrypt(face.AsSpan(0, 16))
                .Concat(face.AsSpan(16).ToArray()).ToArray();
            peer.Send(1, encryptedFace, LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0x53, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0xAC, 0, 0, 0, 0, 1 }, LeagueEnetPacketFlags.Reliable);
            foreach (var pauseRequest in new[] { PausePackets.Pause(uint.MaxValue, 1), PausePackets.Resume(uint.MaxValue, false) })
                peer.Send(1, [.. runtime.Cipher.Encrypt(pauseRequest.Plaintext.Span[..8]),
                    .. pauseRequest.Plaintext.Span[8..]], LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0x09, 0, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            peer.Send(2, face, LeagueEnetPacketFlags.Reliable);
            client.Flush();

            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                server.Pump(1, 32, flushDelivery: false);
                while (client.Service(0).Type != LeagueEnetEventType.None) { }
            }

            // Replace the authenticated generation after the ordinary packet
            // traffic so the replacement record is also checked without using
            // the retired peer for later sends.
            var oldConnection = server.ConnectionIds.Single();
            using var replacementClient = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var replacementPeer = replacementClient.Connect("127.0.0.1", server.Port, 8);
            var replacementConnected = false;
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!replacementConnected || !server.ConnectionIds.Any(id => id != oldConnection))
            {
                server.Pump(1, 32, flushDelivery: false);
                replacementConnected |= replacementClient.Service(0).Type == LeagueEnetEventType.Connect;
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("authentication log replacement did not connect");
            }
            var newConnection = server.ConnectionIds.Single(id => id != oldConnection);
            replacementPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
            replacementClient.Flush();
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (runtime.Session.Participants.Single().ConnectionId != newConnection)
            {
                server.Pump(1, 32, flushDelivery: false);
                while (replacementClient.Service(0).Type != LeagueEnetEventType.None) { }
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("authentication log replacement did not authenticate");
            }

            logger.Flush();
            logger.Dispose();
            var records = ReadRecords(root);
            TestAssert.False(records.Any(record => record.GetRawText().Contains(keyHex,
                StringComparison.OrdinalIgnoreCase)),
                "channel-zero credentials must never enter any diagnostic record");
            var accepted = records.Where(record => record.GetProperty("msg").GetString() == "auth.accepted").ToArray();
            TestAssert.Equal(2, accepted.Length, "initial and replacement authentication remain observable");
            foreach (var record in accepted)
            {
                TestAssert.Equal(SessionKeyCheck.Length, record.GetProperty("n").GetInt32());
                TestAssert.False(record.TryGetProperty("player_id", out _),
                    "accepted authentication logs must not persist decoded credential fields");
            }
            var replaced = records.Single(record => record.GetProperty("msg").GetString() == "reconnect.replaced-connection");
            TestAssert.False(replaced.TryGetProperty("player_id", out _),
                "replacement authentication logs must not persist decoded credential fields");
            foreach (var opcode in new[] { "0xac", "0x09", "0xa9", "0x0a" })
                TestAssert.False(records.Any(record =>
                    record.GetProperty("msg").GetString()!.StartsWith("unhandled ch1 opcode", StringComparison.Ordinal) &&
                    record.GetProperty("op").GetString() == opcode),
                    $"handled session/shop request {opcode} must not warn");
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "DEBUG" &&
                record.GetProperty("msg").GetString() == "ignored ch1 opcode" &&
                record.GetProperty("op").GetString() == "0x7a" &&
                record.GetProperty("name").GetString() == "WaypointAcc"),
                "stock waypoint acknowledgement must be named at DEBUG level");
            TestAssert.False(records.Any(record =>
                record.GetProperty("lvl").GetString() == "WARN" &&
                record.GetProperty("msg").GetString()!.StartsWith("unhandled ch1 opcode", StringComparison.Ordinal) &&
                record.GetProperty("op").GetString() == "0x7a"),
                "stock waypoint acknowledgement must not warn");
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "DEBUG" &&
                record.GetProperty("msg").GetString() == "ignored ch1 opcode" &&
                record.GetProperty("op").GetString() == "0x30" &&
                record.GetProperty("name").GetString() == "WorldSendCameraServer"),
                "known ignored camera opcode must be named at DEBUG level");
            TestAssert.False(records.Any(record =>
                record.GetProperty("lvl").GetString() == "WARN" &&
                record.GetProperty("msg").GetString()!.StartsWith("unhandled ch1 opcode", StringComparison.Ordinal) &&
                record.GetProperty("op").GetString() == "0x30"),
                "known ignored camera opcode must not warn");
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "WARN" &&
                record.GetProperty("msg").GetString()!.StartsWith("unhandled ch1 opcode", StringComparison.Ordinal) &&
                record.GetProperty("op").GetString() == "0x99" &&
                record.GetProperty("msg").GetString() == "unhandled ch1 opcode 0x99 (5 bytes)" &&
                record.GetProperty("length").GetInt32() == 5 &&
                record.GetProperty("prefix").GetString() == "9900000000"),
                "unknown opcode diagnostics must identify the packet in both console and structured log");
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "DEBUG" &&
                record.GetProperty("msg").GetString() == "ignored ch1 opcode" &&
                record.GetProperty("op").GetString() == "0x53" &&
                record.GetProperty("name").GetString() == "C2S_FaceDirection"),
                "the observed 17-byte face-direction packet must be named at DEBUG level; records=" +
                string.Join(" | ", records.Select(record => record.GetRawText())));
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "WARN" &&
                record.GetProperty("msg").GetString()!.StartsWith("unhandled ch1 opcode", StringComparison.Ordinal) &&
                record.GetProperty("op").GetString() == "0x53"),
                "a short face-direction packet must remain an unhandled warning");
            TestAssert.True(records.Any(record =>
                record.GetProperty("lvl").GetString() == "WARN" &&
                record.GetProperty("msg").GetString() == "unhandled opcode on wrong channel" &&
                record.GetProperty("op").GetString() == "0x53" &&
                record.GetProperty("ch").GetInt32() == 2 &&
                record.GetProperty("n").GetInt32() == 17),
                "face-direction on channel 2 must warn with its channel and length");
        }
        finally
        {
            logger.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static List<JsonElement> ReadRecords(string root)
    {
        var session = Directory.GetDirectories(root).Single();
        var path = Path.Combine(session, "session.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private static string FindWorkspaceFile(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var direct = Path.Combine(current.FullName, relative);
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(current.FullName, "emu", relative);
                if (File.Exists(nested)) return nested;
            }
        }
        throw new FileNotFoundException(relative);
    }
}
