using Amberline.Core;

namespace Amberline.Simulation.Buffs;

/// <summary>
/// The model scale coefficient of a unit, <c>CharInter.mScaleSkinCoef</c>: 1 and the <c>BBIncScaleSkinCoef</c> increments of its script buffs.
/// The client multiplies it into the model's <c>SkinScale</c> and never takes a non-positive value (a zero makes the model invisible), so the
/// result is floored at a small positive number.
/// </summary>
public static class ModelScale
{
    public static float Of(Entity unit)
    {
        var scale = 1.0;
        foreach (var buff in unit.Buffs) scale += buff.Payload.ScaleSkinCoef;
        return (float)Math.Max(scale, 0.01);
    }
}
