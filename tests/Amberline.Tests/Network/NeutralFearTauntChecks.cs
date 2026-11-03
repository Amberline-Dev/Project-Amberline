using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>Leashed.luaobj fear and taunt handlers for jungle camp members (OnFear*, OnTaunt*, TimerFeared).</summary>
public static partial class NeutralAiChecks
{


    private sealed class SequenceRandom(params double[] values) : IRandomSource
    {
        public int Calls { get; private set; }
        public double NextDouble() => values[Calls++ % values.Length];
    }
}
