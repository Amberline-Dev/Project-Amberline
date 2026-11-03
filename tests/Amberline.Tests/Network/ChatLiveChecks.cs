using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

public static class ChatLiveChecks
{
    [Check("network.chat-live-mixed-team-routing")]
    public static void ProductionChatRoutesBothTeamsAndIgnoresForgedSender()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "OrderA", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "OrderB", "Orianna", Team.Order),
            new SessionParticipantDefinition(3, 2, "ChaosA", "LeeSin", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();

        var connections = new[] { 11L, 12L, 13L };
        for (var index = 0; index < connections.Length; index++)
        {
            var playerId = (ulong)(index + 1);
            TestAssert.True(runtime.Session.Authenticate(connections[index],
                runtime.Session.BuildKeyCheck(playerId)) is not null,
                $"seat {playerId} authenticates");
            runtime.Session.Spawn(connections[index]);
            TestAssert.True(runtime.Session.MarkReady(connections[index]),
                $"seat {playerId} reaches ready state");
        }
        TestAssert.True(runtime.Session.Start(), "mixed-team chat starts after all seats are ready");

        var allFromOrder = Send(runtime, connections[0], 0xDEADBEEFu, false, "order all");
        AssertRelay(allFromOrder, connections, senderClientId: 0, teamChat: false,
            "order all", "order all");

        var orderTeam = Send(runtime, connections[0], 0xFFFFFFFFu, true, "order team");
        AssertRelay(orderTeam, connections[..2], senderClientId: 0, teamChat: true,
            "order team", "order team");

        var chaosTeam = Send(runtime, connections[2], 0xFFFFFFFFu, true, "chaos team");
        AssertRelay(chaosTeam, connections[2..], senderClientId: 2, teamChat: true,
            "chaos team", "chaos team");

        var allFromChaos = Send(runtime, connections[2], 0xFFFFFFFFu, false, "chaos all");
        AssertRelay(allFromChaos, connections, senderClientId: 2, teamChat: false,
            "chaos all", "chaos all");

        var forgedConnection = Send(runtime, 99, 0, false, "unknown owner");
        TestAssert.Equal(0, forgedConnection.Count,
            "an unauthenticated connection cannot relay chat");

        TestAssert.True(runtime.Session.Disconnect(connections[1]),
            "disconnecting a ready seat detaches its chat recipient");
        var afterDisconnect = Send(runtime, connections[0], 123, false, "connected only");
        AssertRelay(afterDisconnect, new[] { connections[0], connections[2] },
            senderClientId: 0, teamChat: false, "chat omits detached recipients",
            "connected only");
    }

    private static IReadOnlyList<SessionOutboundPacket> Send(MatchRuntime runtime,
        long connectionId, uint senderClientId, bool teamChat, string text)
    {
        var packet = ChatPackets.Envelope(senderClientId, teamChat,
            Encoding.ASCII.GetBytes(text));
        return runtime.Session.Receive(connectionId, ChatPackets.Channel,
            packet.ToWireBytes(runtime.Cipher));
    }

    private static void AssertRelay(IReadOnlyList<SessionOutboundPacket> outputs,
        IReadOnlyList<long> expectedConnections, uint senderClientId, bool teamChat,
        string label, string expectedText)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expectedText);
        TestAssert.Equal(expectedConnections.Count, outputs.Count, $"{label} recipient count");
        TestAssert.Equal(expectedConnections.ToArray(),
            outputs.Select(output => output.ConnectionId).ToArray(),
            $"{label} recipient order");
        foreach (var output in outputs)
        {
            var payload = output.Packet.Plaintext.Span;
            TestAssert.Equal(ChatPackets.Channel, output.Packet.Channel,
                $"{label} uses chat channel");
            TestAssert.Equal(senderClientId,
                BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                $"{label} uses assigned sender client ID");
            TestAssert.Equal(teamChat ? 1u : 0u,
                BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]),
                $"{label} team flag");
            TestAssert.Equal(13 + expectedBytes.Length, payload.Length,
                $"{label} envelope length");
            TestAssert.Equal(expectedBytes,
                payload[12..(12 + expectedBytes.Length)].ToArray(),
                $"{label} exact text bytes");
            TestAssert.Equal((byte)0, payload[^1],
                $"{label} text terminator");
        }
    }
}
