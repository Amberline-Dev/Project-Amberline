namespace Amberline.Core;

/// <summary>
/// The small champion-state contract the shared movement loop needs.
/// Champion modules own their complete typed state; movement only needs to
/// know whether a pending action currently blocks movement.
/// </summary>
public interface IChampionState
{
    bool BlocksActions { get; }

    /// <summary>Whether a champion-specific revival state forbids summoner casts.</summary>
    bool IsReviving { get; }
}
