using System.Collections.Frozen;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the host's two switches that Settings sets while the server runs (Skins and RP, and the Champions list). Retail had no host
/// switch for either. Every reader checks the live value, so a Save applies at once without a restart; each setter swaps in a whole new
/// value, so a reader sees the old value or the new one, never half of a list. Running games keep what they started with.
/// </summary>
public sealed class PlatformControls
{
    private volatile bool _paidContentOff;
    private volatile FrozenSet<int> _disabled = FrozenSet<int>.Empty;

    /// <summary>True: skins and RP are not offered (the default looks, and the store shows neither). Nothing owned is removed.</summary>
    public bool PaidContentOff { get => _paidContentOff; set => _paidContentOff = value; }

    /// <summary>The champion ids the host has disabled; empty while none is.</summary>
    public IReadOnlySet<int> DisabledChampions { get => _disabled; set => _disabled = value.ToFrozenSet(); }

    public bool IsEnabled(int championId) => !_disabled.Contains(championId);

    public static PlatformControls From(PlatformSettings settings) => new()
    {
        PaidContentOff = settings.PaidContentOff,
        DisabledChampions = (settings.DisabledChampions ?? Array.Empty<int>()).ToHashSet(),
    };
}
