using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{


    // The first point of the hero's route in a decoded 0xC1 or 0x64 (one entry each here); null when the packet moves another unit.
    private static Position2? RouteStart(JsonElement fields, uint netId)
    {
        var id = fields.TryGetProperty("net_id", out var single) ? single.GetUInt32()
            : fields.TryGetProperty("teleport_netid", out var group) && group.ValueKind == JsonValueKind.Number ? group.GetUInt32() : 0u;
        if (id != netId || !fields.TryGetProperty("waypoints", out var points) || points.GetArrayLength() == 0) return null;
        return new Position2(points[0][0].GetDouble(), points[0][1].GetDouble());
    }

    private static double Gap(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
