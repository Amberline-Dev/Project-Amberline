using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Platform;

public static class AirWire
{
    public static AsObject Object(string alias, params (string Name, object? Value)[] fields)
    {
        var value = new AsObject(alias);
        foreach (var (name, field) in fields)
            value[name] = field;
        return value;
    }

    public static ArrayCollection List(IEnumerable<object> values)
    {
        var result = new ArrayCollection();
        result.AddRange(values);
        return result;
    }
}
