using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests.Network;

public static class ProtocolBuilderChecks
{


    [Check("protocol.item-parsers")]
    public static void ItemParsers()
    {
        var shop = ItemsPackets.ParseShopOpen(Convert.FromHexString("6001000040"));
        TestAssert.Equal(0x40000001u, shop.Header);
        var buy = ItemsPackets.ParseBuyItem(Convert.FromHexString("8701000040BE0B0000"));
        TestAssert.Equal(new BuyItemRequest(0x40000001, 3006), buy);
        var remove = ItemsPackets.ParseRemoveItem(Convert.FromHexString("090100004083"));
        TestAssert.Equal(new RemoveItemRequest(0x40000001, 3, true), remove);
        var swap = ItemsPackets.ParseSwapItem(Convert.FromHexString("23010000400501"));
        TestAssert.Equal(new SwapItemRequest(0x40000001, 5, 1), swap);

        var edge = FxPackets.CompressPosition(
            new Position2(65_535.8, 0), new NavigationBounds(0, 0, 0, 0), (_, _) => 12.5);
        TestAssert.Equal((short)32_767, edge.X);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => FxPackets.CompressPosition(
            new Position2(65_537, 0), new NavigationBounds(0, 0, 0, 0), (_, _) => 12.5));
    }

    private static IReadOnlyList<PlaintextPacket> BuildCase(
        JsonElement testCase,
        NavigationBounds bounds)
    {
        var function = testCase.GetProperty("function").GetString()!;
        var args = testCase.GetProperty("args").EnumerateArray().ToArray();
        var kwargs = testCase.GetProperty("kwargs");
        switch (function)
        {
            case "send_query_status_ans":
                return new[] { LoadingPackets.QueryStatusAns() };
            case "send_ping_load_info":
                return new[] { LoadingPackets.PingLoadInfo(
                    args[0].GetUInt32(), args[1].GetUInt64(),
                    args.Length > 2 ? args[2].GetSingle() : 100.0f,
                    args.Length > 3 ? args[3].GetSingle() : 0.0f,
                    args.Length > 4 ? args[4].GetInt16() : (short)0,
                    args.Length > 5 ? args[5].GetUInt16() : (ushort)0,
                    args.Length > 6 ? args[6].GetBoolean() : true) };
            case "send_reconnect_ack":
                return new[] { LoadingPackets.ReconnectAck(args[0].GetUInt64()) };
            case "send_world_send_game_number":
                return new[] { LoadingPackets.WorldSendGameNumber(args[0].GetUInt64()) };
            case "send_team_roster_update":
                return new[] { LoadingPackets.TeamRosterUpdate(
                    Ulongs(args[0]), Ulongs(args[1]), args[2].GetUInt32(), args[3].GetUInt32()) };
            case "send_request_reskin":
                return new[] { LoadingPackets.RequestReskin(
                    args[0].GetUInt64(), args[1].GetString()!, args[2].GetUInt32()) };
            case "send_request_rename":
                return new[] { LoadingPackets.RequestRename(args[0].GetUInt64(), args[1].GetString()!) };
            case "send_ch6_loading_burst":
                return LoadingPackets.LoadingBurst(
                    args[0].GetUInt64(), players: Players(kwargs.GetProperty("players")));
            case "send_synch_version_s2c":
                return new[] { kwargs.TryGetProperty("players", out var players)
                    ? LoadingPackets.SynchVersionS2C(players: Players(players))
                    : LoadingPackets.SynchVersionS2C() };
            case "send_buy_item":
                return new[] { ItemsPackets.BuyItem(
                    args[0].GetUInt32(), args[1].GetInt32(), args[2].GetUInt32(),
                    args[3].GetInt32(), args[4].GetBoolean()) };
            case "send_remove_item":
                return new[] { ItemsPackets.RemoveItem(
                    args[0].GetUInt32(), args[1].GetInt32(), args[2].GetInt32()) };
            case "send_swap_item":
                return new[] { ItemsPackets.SwapItem(
                    args[0].GetUInt32(), args[1].GetInt32(), args[2].GetInt32()) };
            case "send_use_item":
                return new[] { ItemsPackets.UseItem(
                    args[0].GetUInt32(), args[1].GetInt32(), args[2].GetInt32(), args[3].GetInt32()) };
            case "send_set_item":
                return new[] { ItemsPackets.SetItem(
                    args[0].GetUInt32(), args[1].GetInt32(), args[2].GetUInt32(),
                    args[3].GetInt32(), args[4].GetInt32()) };
            case "send_fx_create_group":
                return new[] { BuildFxCreate(args, kwargs, bounds) };
            case "send_fx_kill":
                return new[] { FxPackets.Kill(args[0].GetUInt32(), args[1].GetUInt32()) };
            default:
                throw new InvalidOperationException($"Fixture function is not dispatched by the test: {function}");
        }
    }

    private static PlaintextPacket BuildFxCreate(
        JsonElement[] args,
        JsonElement kwargs,
        NavigationBounds bounds)
    {
        var position = Position2Of(args[2]);
        var target = kwargs.TryGetProperty("target_position", out var targetValue)
            ? Position2Of(targetValue) : (Position2?)null;
        var owner = kwargs.TryGetProperty("owner_position", out var ownerValue)
            ? Position2Of(ownerValue) : (Position2?)null;
        var direction = kwargs.TryGetProperty("direction", out var directionValue)
            ? Position3Of(directionValue) : default;
        return FxPackets.CreateGroup(
            args[0].GetUInt32(), args[1].GetUInt32(), position, bounds,
            KnownHeight,
            target, owner,
            UInt32(kwargs, "target_netid"), UInt32(kwargs, "bind_netid"),
            (ushort)UInt32(kwargs, "flags"),
            UInt32(kwargs, "target_bone_hash"), UInt32(kwargs, "bone_hash"),
            direction,
            kwargs.TryGetProperty("time_spent", out var time) ? time.GetSingle() : 0.01f,
            UInt32(kwargs, "header_netid"));
    }

    private static double KnownHeight(double x, double z)
    {
        if (Near(x, 7000.5) && Near(z, 7001.75)) return 45.88380432128906;
        if (Near(x, 1825.97) && Near(z, 1532.2)) return 98.33090209960938;
        if (Near(x, 12400.1) && Near(z, 12750.9)) return 96.93518829345703;
        if (Near(x, 6990.99) && Near(z, 7222.99)) return 45.5125732421875;
        throw new InvalidOperationException($"No fixture height is known for ({x}, {z}).");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

    private static LeagueLoadingPlayer[] Players(JsonElement value) => value.EnumerateArray()
        .Select(item =>
        {
            var fields = item.EnumerateArray().ToArray();
            return new LeagueLoadingPlayer(
                fields[0].GetUInt64(), fields[1].GetString()!, fields[2].GetString()!, fields[3].GetInt32());
        }).ToArray();

    private static ulong[] Ulongs(JsonElement value) => value.EnumerateArray()
        .Select(item => item.GetUInt64()).ToArray();

    private static Position2 Position2Of(JsonElement value)
    {
        var fields = value.EnumerateArray().ToArray();
        return new Position2(fields[0].GetDouble(), fields[1].GetDouble());
    }

    private static Position3 Position3Of(JsonElement value)
    {
        var fields = value.EnumerateArray().ToArray();
        return new Position3(fields[0].GetDouble(), fields[1].GetDouble(), fields[2].GetDouble());
    }

    private static uint UInt32(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) ? result.GetUInt32() : 0;

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static byte[] Hex(JsonElement value, string property) =>
        Convert.FromHexString(value.GetProperty(property).GetString()!);
}
