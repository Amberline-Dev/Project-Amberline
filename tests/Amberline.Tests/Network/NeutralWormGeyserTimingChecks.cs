using System.Text.Json;
using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

public static partial class NeutralAiChecks
{


    private static JsonElement Choice() =>
        JsonSerializer.SerializeToElement(new { method = "choice", count = 2, index = 0 });
    private static JsonElement Uniform(double minimum, double maximum, double value) =>
        JsonSerializer.SerializeToElement(new { method = "uniform", minimum, maximum, value });
    private static JsonElement Random(double value) =>
        JsonSerializer.SerializeToElement(new { method = "random", value });
}
