using Amberline.Core;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>Small runtime-value helpers the host shares with the natives.</summary>
internal static class BbRuntimeNames
{
    public static string Of(object? value) => value switch
    {
        null => "nil",
        bool => "boolean",
        double => "number",
        string => "string",
        BbTable => "table",
        Entity => "unit",
        _ => value.GetType().Name,
    };

    public static double Number(object? value) =>
        value is double d ? d : throw new BbRuntimeException($"attempt to perform arithmetic on a {Of(value)} value");
}
