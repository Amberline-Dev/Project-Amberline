using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// What <c>DATA/Scripts/EndOfGame.luaobj</c>'s ceremony natives ask of the engine. The script is
/// the timing authority: it locks the inputs, disables the HUD, halts every AI and arms its own
/// phases, and every packet that burst goes out because one of these was called — there is no
/// hand-written ceremony left to send them. The owner of the surface is the match's combat
/// service (it holds the transport, the winner and the surrender flag); the pieces it does not
/// own — the AI halt and the shop — reach their own services through it.
/// </summary>
public interface IEndOfGameEngine
{
    /// <summary><c>SetInputLockingFlag(INPUT_*, locked)</c>: one native widget callback per flag.</summary>
    void SetInputLockingFlag(double flag, bool locked);

    /// <summary><c>DisableHUDForEndOfGame()</c>.</summary>
    void DisableHudForEndOfGame();

    /// <summary><c>SetBuildingHealthRegenEnabled(hq, false)</c> on one of the two HQs.</summary>
    void SetBuildingHealthRegenEnabled(Entity building, bool enabled);

    /// <summary><c>SetBarracksSpawnEnabled(false)</c>: no more waves from any barracks.</summary>
    void SetBarracksSpawnEnabled(bool enabled);

    /// <summary><c>CloseShop()</c>.</summary>
    void CloseShop();

    /// <summary><c>HaltAllAI()</c>: every unit's ordinary orders and in-flight swings stop.</summary>
    void HaltAllAi();

    /// <summary><c>MoveCameraFromCurrentPositionToPoint(champion, point, seconds)</c>.</summary>
    void MoveCameraToPoint(Entity hero, Position3 point, double seconds);

    /// <summary><c>SetGreyscaleEnabledWhenDead(champion, false)</c>.</summary>
    void SetGreyscaleEnabledWhenDead(Entity hero, bool enabled);

    /// <summary><c>FadeOutMainSFX(seconds)</c>.</summary>
    void FadeOutMainSfx(double seconds);

    /// <summary><c>SetHQCurrentSkin(team, skin)</c>: the losing HQ's destroyed skin.</summary>
    void SetNexusSkin(Team team, int skin);

    /// <summary><c>FadeMinions(team, amount, seconds)</c>.</summary>
    void FadeMinions(Team team, double amount, double seconds);

    /// <summary><c>SetMinionsNoRender(team, true)</c>.</summary>
    void SetMinionsNoRender(Team team, bool noRender);

    /// <summary><c>EndGame(winningTeam)</c>: the terminal phase.</summary>
    void EndGame(Team winner);
}

/// <summary>
/// The match's <c>EndOfGame.luaobj</c> through the two entry points the engine runs. The
/// ceremony itself is Map1's <c>HandleDestroyedObject</c> HQ branch (proto 0.14 pc 0016-0024)
/// handing the destroyed nexus to <c>EndOfGameCeremony(winningTeam, destroyedHQ)</c>, so the
/// engine calls that same global; the phase timers it arms are advanced from the update phase the
/// checks drive the ceremony through.
/// </summary>
public interface IEndOfGameScript
{
    /// <summary>
    /// Runs the script's <c>EndOfGameCeremony(winningTeam, destroyedObject)</c> as of
    /// <paramref name="now"/>, and answers whether the chunk had that hook at all.
    /// </summary>
    bool EndOfGameCeremony(Team winner, Entity destroyedNexus, double now);

    /// <summary>Runs the ceremony's phase timers up to <paramref name="now"/>, one fire per deadline.</summary>
    void AdvanceEndOfGame(double now);
}
