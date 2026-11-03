using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    public bool GameOver => _gameOver;

    /// <summary>The team that won the match (set by FinishMatch; Neutral before the end).</summary>
    public Team Winner => _winner;

    public bool EndGameSent => _endGameSent;

    /// <summary><c>CloseShop()</c>: the shop stays shut for the rest of the match.</summary>
    public bool ShopClosed => _shopClosed;

    /// <summary><c>SetBarracksSpawnEnabled(false)</c>: no more waves from any barracks.</summary>
    public bool BarracksSpawnEnabled => _barracksSpawnEnabled;

    /// <summary>
    /// The match's <c>EndOfGame.luaobj</c>, once Map1's level host has it. The ceremony that
    /// replaces this service's own end-game sequence runs through it; without one the fallback in
    /// <see cref="FinishMatch"/> keeps the match's own book.
    /// </summary>
    public IEndOfGameScript? EndOfGameScript { get; set; }

    public bool Surrendered { get; private set; }

    /// <summary>Optional replication hook for the managed Orianna ball.</summary>
    public Action<Team>? EndGameBallNoRender { get; set; }

    /// <summary>Forfeit without combat rewards or an enemy's structure-kill credit.</summary>
    public bool Surrender(Team team)
    {
        if (team is not (Team.Order or Team.Chaos) || _gameOver) return false;
        var nexus = _world.Entities.Values.SingleOrDefault(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == team && unit.Alive);
        if (nexus is null) return false;
        Surrendered = true;
        nexus.Alive = false;
        nexus.Health = 0;
        nexus.DeathTime = _time;
        RecordStructureLoss(nexus);
        if (_transport.IsWireReady(nexus)) _transport.SendStructureDestroyed(nexus, 0);
        FinishMatch(nexus);
        RecomputeProtection();
        return true;
    }

    private void FinishMatch(Entity nexus)
    {
        _gameOver = true;
        _deathRecapHistories.Clear();
        _lastDeathRecaps.Clear();
        _winner = nexus.Team == Team.Order ? Team.Chaos : Team.Order;
        _gameOverAt = _time;
        _hooks.OnEndGame(_winner);
        // Map1's HandleDestroyedObject hands a fallen HQ to EndOfGameCeremony(winner, obj)
        // (LevelScript.txt proto 0.14 pc 0016-0024), so the engine calls that same global. The
        // script locks the inputs, freezes both HQs, halts every AI and arms the phase timers this
        // service then advances from its update phase.
        if (EndOfGameScript?.EndOfGameCeremony(_winner, nexus, _time) != true)
        {
            // NOT RETAIL: no EndOfGame.lua chunk is loaded (a map that does not chain it, or a
            // fixture), so the halt and the shop close the ceremony would have ordered are done
            // here. The ceremony's own packets have no stand-in.
            _hooks.HaltAllAi();
            CloseShop();
        }
        // The immediate winner animation is not the script's: 0xCD goes out on the nexus death
        // See docs/network/patch126-end-game.md.
        _transport.SendEndOfGameEvent(_winner);
    }

    // IEndOfGameEngine: the ceremony natives of DATA/Scripts/EndOfGame.luaobj, each forwarded to
    // the one packet family or latch it stands for. The script's own call order is the timing.
    void IEndOfGameEngine.SetInputLockingFlag(double flag, bool locked)
        => _transport.SendInputLockingFlag(AiGlobals.InputLockMask(flag), locked);

    void IEndOfGameEngine.DisableHudForEndOfGame() => _transport.SendDisableHudForEndOfGame();

    void IEndOfGameEngine.SetBuildingHealthRegenEnabled(Entity building, bool enabled)
        => building.BuildingHealthRegenEnabled = enabled;

    void IEndOfGameEngine.SetBarracksSpawnEnabled(bool enabled) => _barracksSpawnEnabled = enabled;

    void IEndOfGameEngine.CloseShop() => CloseShop();

    void IEndOfGameEngine.HaltAllAi() => _hooks.HaltAllAi();

    void IEndOfGameEngine.MoveCameraToPoint(Entity hero, Position3 point, double seconds)
        => _transport.SendMoveCameraToPoint(hero, point, seconds);

    void IEndOfGameEngine.SetGreyscaleEnabledWhenDead(Entity hero, bool enabled)
        => _transport.SendGreyscaleEnabledWhenDead(hero, enabled);

    void IEndOfGameEngine.FadeOutMainSfx(double seconds) => _transport.SendMainSfxFade(seconds);

    void IEndOfGameEngine.SetNexusSkin(Team team, int skinId)
        => _transport.SendNexusSkin(team, checked((uint)skinId));

    void IEndOfGameEngine.FadeMinions(Team team, double amount, double seconds)
        => _transport.SendMinionFade(team, amount, seconds);

    void IEndOfGameEngine.SetMinionsNoRender(Team team, bool noRender)
    {
        // SetMinionsNoRender is the Minions manager's state for that team, not a one-shot list:
        // a unit that appears under it is hidden as well (the check pins that). The managed Orianna
        // balls latch their own units first, so the walk below leaves them their own word.
        EndGameBallNoRender?.Invoke(team);
        _endGameNoRenderTeam = noRender ? team : null;
        ApplyEndOfGameNoRender();
    }

    void IEndOfGameEngine.EndGame(Team winner)
    {
        _endGameSent = true;
        _transport.SendEndGame(winner, Surrendered);
    }

    private void CloseShop() => _shopClosed = true;

    /// <summary>Every losing-team minion the no-render phase covers that is not latched yet.</summary>
    private void ApplyEndOfGameNoRender()
    {
        if (_endGameNoRenderTeam is not { } team) return;
        foreach (var minion in _world.Entities.Values
                     .Where(entity => entity.Team == team && IsEndGameMinion(entity) && !entity.NoRender)
                     .ToArray())
        {
            minion.NoRender = true;
            _transport.SendMinionNoRender(minion);
        }
    }

    private static bool IsEndGameMinion(Entity entity) =>
        entity.Team != Team.Neutral &&
        (entity.UnitType is UnitType.Minion or UnitType.Monster);
}
