namespace Amberline.Core;

/// <summary>Random source used by live combat. Tests can inject deterministic rolls.</summary>
public interface IRandomSource
{
    double NextDouble();

    /// <summary>Returns an index in [0,count), preserving the source's integer-choice hook.</summary>
    int NextIndex(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return Math.Clamp((int)Math.Floor(NextDouble() * count), 0, count - 1);
    }

    /// <summary>Returns a uniform value in the inclusive lower/exclusive upper interval.</summary>
    double Uniform(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        return minimum + NextDouble() * (maximum - minimum);
    }
}

public sealed class SystemRandomSource : IRandomSource
{
    // DIAGNOSTICS: inside a check the per-check seed replaces the unseeded stream (SeededRandomSource.CheckSeed).
    private readonly Random _random = SeededRandomSource.CheckSeed is int seed ? new Random(seed) : new();

    public double NextDouble() => _random.NextDouble();
}

/// <summary>
/// Retail rolls from an unseeded server RNG, and a live match does the same: <see cref="NewSeed"/> draws a fresh
/// seed from OS entropy for each match (ServerRuntime.cs, the production factory passes no seed), so no production
/// match repeats another. Every combat roll, Shaco box, bot decision and fear wander of a match comes from this one
/// instance.
/// <para>
/// DIAGNOSTICS: the fixed seed and the logged <c>rng_seed</c> are a test and diagnostic seam, an addition retail never
/// had. <c>Amberline.Tools match --seed</c> and the scenario tools pass a fixed seed, so a headless match can be
/// repeated exactly; the live log names the seed it drew. Production never passes one.
/// </para>
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
    private readonly Random _random;

    public SeededRandomSource(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    public int Seed { get; }

    /// <summary>
    /// DIAGNOSTICS (batch 297): the check runner's per-check seed, a hash of the check's name, set around each check and
    /// null outside one. A match built without a random source (<c>MatchRuntime.CreateProduction</c> in a check) draws from
    /// it and seeds its Lua C-runtime stream with it, and every <see cref="SystemRandomSource"/> a check builds starts from
    /// it, so a check reads the same draws on every run and in every worker. Production never sets it: the shipped server
    /// passes its own entropy seed and the wall clock (ServerRuntime.cs).
    /// </summary>
    public static int? CheckSeed { get; set; }

    /// <summary>A fresh non-negative seed for a live match, drawn from the OS entropy source.</summary>
    public static int NewSeed() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);

    public double NextDouble() => _random.NextDouble();
}
