using Amberline.Core;

namespace Amberline.Simulation.Minions;

/// <summary>
/// DIAGNOSTICS: diagnostics only; retail has no such trace and it never changes a decision.
/// Optional per-tick AI trace used by the source LOG_MINION_TRACE=N gate.
/// </summary>
public interface IMinionTraceSink
{
    void OnMinionAiTick();
}

/// <summary>
/// Emits one source-shaped <c>AI/DEBUG/trace</c> record for each alive minion
/// on every Nth AI tick.  The sink is match-owned and does no work when the
/// environment gate is disabled.
/// </summary>
public sealed class MinionTraceSink : IMinionTraceSink
{
    private readonly WorldState _world;
    private readonly ISessionLogSink? _log;
    private int _ticks;

    private MinionTraceSink(WorldState world, ISessionLogSink? log)
    {
        _world = world;
        _log = log;
    }

    public static MinionTraceSink? FromEnvironment(WorldState world, ISessionLogSink? log)
    {
        // Python reads LOG_MINION_TRACE at every AI tick, so a sink created
        // during match composition must not freeze the bootstrap value.
        return new MinionTraceSink(world ?? throw new ArgumentNullException(nameof(world)), log);
    }

    public void OnMinionAiTick()
    {
        _ticks++;
        var raw = Environment.GetEnvironmentVariable("LOG_MINION_TRACE");
        if (!int.TryParse(raw, out var rate) || rate <= 0 || _ticks % rate != 0 || _log is null)
            return;

        try
        {
            foreach (var minion in _world.Entities.Values.Where(entity =>
                         entity.UnitType == UnitType.Minion && entity.Alive))
            {
                _log.Write("AI", "DEBUG", "trace", new Dictionary<string, object?>
                {
                    ["netid"] = minion.NetId,
                    ["pos"] = new[] { Math.Round(minion.Position.X, 1), Math.Round(minion.Position.Z, 1) },
                    ["ai_state"] = minion.AiState,
                    ["attack_target"] = minion.AttackTarget,
                });
            }
        }
        catch
        {
            // Instrumentation must never affect the authoritative AI tick.
        }
    }
}
