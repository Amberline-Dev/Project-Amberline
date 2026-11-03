using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.Spells.Champions;

/// <summary>
/// The token of a zombie life (<see cref="Entity.YorickRevival"/>; Yorick's Omen of Death temporary life is the only one the emulator runs). It
/// is created when the death becomes a zombie and cleared when <c>BBForceDead</c> ends the life; its identity tells an old life from a new one.
/// </summary>
public sealed class YorickRevivalState
{
    public YorickRevivalState(double deadline) => Deadline = deadline;

    /// <summary>DIAGNOSTICS, the lifecycle monitor only: the latest time the life should still be going on (the scripts own the real end).</summary>
    public double Deadline { get; }
}
