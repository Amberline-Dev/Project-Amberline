using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Combat;

/// <summary>The stock probability history, scoped to this match and hero/category.</summary>
internal sealed class CombatKarma(GameData data, IRandomSource random)
{
    private readonly Dictionary<(uint Hero, int Category), (float Multiplier, float Previous)> _states = new();

    public bool Roll(uint heroNetId, int category, double chance)
    {
        var probability = (float)Math.Clamp(chance, 0, 1);
        if (!float.IsFinite(probability)) throw new ArgumentOutOfRangeException(nameof(chance));
        if (probability <= 1e-6f) return false;
        if (probability >= 0.999999f) return true;

        var key = (heroNetId, category);
        if (!_states.TryGetValue(key, out var state)) state = (1, probability);
        probability = (float)((double)probability + (double)0.0001f);
        var threshold = Threshold(probability, state.Multiplier);
        if (state.Previous < probability)
        {
            var previousThreshold = Threshold(state.Previous, state.Multiplier);
            var multiplier = previousThreshold / Lookup(probability);
            if (multiplier > 1)
            {
                state.Multiplier = multiplier;
                threshold = previousThreshold;
            }
            else state.Multiplier = 1;
        }
        var success = random.NextDouble() <= threshold;
        _states[key] = (success ? 1 : Math.Min(5, state.Multiplier + 1), probability);
        return success;
    }

    private float Lookup(float probability)
        => data.CriticalKarmaConstants[(int)(probability / 0.005)];

    private float Threshold(float probability, float multiplier)
        => Math.Min(Lookup(probability) * multiplier, (float)(1 - (1 - (double)probability) / 5));
}
