using Amberline.Simulation.Spells.Champions;

namespace Amberline.Core;

public sealed partial class Entity
{
    /// <summary>
    /// Cross-champion revival state owned by Yorick R. It is separate from
    /// ChampionState because the marked ally may be Annie, Orianna, or any
    /// other champion whose own action state must survive the mark.
    /// </summary>
    public YorickRevivalState? YorickRevival { get; set; }

    /// <summary>
    /// The client's zombie flag (what <c>BBGetIsZombie</c> reads): the unit is in a zombie life, one that
    /// the death packet's <c>becomeZombie</c> starts and <c>BBForceDead</c> ends. The emulator's only zombie life is Yorick's Omen of Death
    /// temporary life (<see cref="YorickRevival"/>, any phase); the revivals of Mordekaiser's Children of the Grave and Kog'Maw's Icathian
    /// Surprise are not run. Reconstruction: which phases of the temporary life count as the zombie is not in the client.
    /// </summary>
    public bool IsZombie => YorickRevival is not null;
}
