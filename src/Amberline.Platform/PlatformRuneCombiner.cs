using System.Collections.ObjectModel;
using Amberline.Data;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Platform;

/// <summary>Local patch-126 rune-combiner recipes exposed to the AIR client.</summary>
public sealed record PlatformRuneCombiner(
    int Id,
    int InputCount,
    RuneTier InputTier,
    RuneTier OutputTier,
    string Name)
{
    public const string AirAlias = AirProtocolNames.RuneCombiner;

    public const int TwoTierOneId = 1001;
    public const int TwoTierTwoId = 1002;
    public const int TwoTierThreeId = 1003;
    public const int FiveTierOneId = 2001;
    public const int FiveTierTwoId = 2002;

    public static IReadOnlyList<PlatformRuneCombiner> All { get; } =
        new ReadOnlyCollection<PlatformRuneCombiner>
        (
        [
            new(TwoTierOneId, 2, RuneTier.One, RuneTier.One, "Tier 1 Rune Combiner"),
            new(TwoTierTwoId, 2, RuneTier.Two, RuneTier.Two, "Tier 2 Rune Combiner"),
            new(TwoTierThreeId, 2, RuneTier.Three, RuneTier.Three, "Tier 3 Rune Combiner"),
            new(FiveTierOneId, 5, RuneTier.One, RuneTier.Two, "Tier 1 Rune Combiner"),
            new(FiveTierTwoId, 5, RuneTier.Two, RuneTier.Three, "Tier 2 Rune Combiner"),
        ]);

    public static PlatformRuneCombiner Get(int id) =>
        All.FirstOrDefault(combiner => combiner.Id == id) ??
        throw new ArgumentException($"Rune combiner {id} is not supported.", nameof(id));

    public static AsObject ToAir(PlatformRuneCombiner combiner)
    {
        ArgumentNullException.ThrowIfNull(combiner);
        return AirWire.Object(AirAlias,
            ("id", (double)combiner.Id),
            ("inputCount", combiner.InputCount),
            ("inputTier", (int)combiner.InputTier),
            ("name", combiner.Name));
    }
}
