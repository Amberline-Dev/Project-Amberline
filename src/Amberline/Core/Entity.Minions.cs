namespace Amberline.Core;

public enum MinionAiState
{
    March,
    Engage,
    Idle,
    Taunted,
    Feared,
    Halted,
}

/// <summary>Latched attack bearing used to keep co-attackers separated.</summary>
public readonly record struct MinionAttackPoint
{
    public MinionAttackPoint(uint targetNetId, double angle, double radius)
    {
        if (!double.IsFinite(angle) || !double.IsFinite(radius) || radius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        TargetNetId = targetNetId;
        Angle = angle;
        Radius = radius;
    }

    public uint TargetNetId { get; }
    public double Angle { get; }
    public double Radius { get; }
}

public sealed partial class Entity
{
    /// <summary>Permission controlled by Minion.lua, independent of the retained target.</summary>
    public bool MinionAutoAttackEnabled { get; set; }

    /// <summary>
    /// The AI script a <c>BBSpawnMinion</c> gave the unit (<c>UncontrollablePet.lua</c> for Malzahar's voidlings), or null for a unit that
    /// stands where it was put. Only the scripts the engine runs a controller for are recorded (<see cref="Simulation.Pets.SpawnedUnitAi"/>).
    /// </summary>
    public string? AiScript { get; set; }

    /// <summary>
    /// Batch 593: the lane route ahead of a <c>Minion.lua</c> unit a level script spawned (the Basic Tutorial's), which its forward navigation
    /// walks node by node (<c>SpawnedUnitLuaAiEngine.SetStateAndMoveToForwardNav</c>); null for every other spawned unit, which stands.
    /// </summary>
    public List<Position2>? ScriptLaneRoute { get; set; }

    /// <summary>Stable wave position used for deterministic fan/formation order.</summary>
    public int WaveSlot { get; set; }

    /// <summary>Map1 lane key for scheduled minions, or null for ad-hoc units.</summary>
    public string? MinionLane { get; set; }

    /// <summary>Scheduled birth time used for data-driven stat upgrades.</summary>
    public double SpawnScheduledAt { get; set; }

    /// <summary>Wire node ID and spawn character identity used by observers.</summary>
    public uint NodeId { get; set; }

    /// <summary>Latched formation bearing for the current attack target.</summary>
    public MinionAttackPoint? AttackPoint { get; set; }

    /// <summary>
    /// Unconsumed lane suffix held until a safe march connector exists. Reconstruction: retail Lua only calls
    /// SetStateAndMoveToForwardNav and the native server knows the lane's forward nodes; the emulator keeps the
    /// unconsumed node list itself, so a stun, snare, fear or forced move that cancels the route can restore it.
    /// </summary>
    public List<Position2> InterruptedLaneWaypoints { get; set; } = new();

    internal void RetainLaneRouteForControl()
    {
        if (UnitType != UnitType.Minion || MinionType is null ||
            MinionState is not (MinionAiState.March or MinionAiState.Idle) || LaneDone ||
            InterruptedLaneWaypoints.Count != 0 || Waypoints.Count == 0) return;
        InterruptedLaneWaypoints = Waypoints.ToList();
    }

    /// <summary>Typed view over the legacy source state name.</summary>
    public MinionAiState MinionState
    {
        get => AiState switch
        {
            "march" => MinionAiState.March,
            "engage" => MinionAiState.Engage,
            "idle" => MinionAiState.Idle,
            "taunted" => MinionAiState.Taunted,
            "feared" => MinionAiState.Feared,
            "halted" => MinionAiState.Halted,
            _ => throw new InvalidOperationException($"Unknown minion AI state '{AiState}'."),
        };
        set => AiState = value switch
        {
            MinionAiState.March => "march",
            MinionAiState.Engage => "engage",
            MinionAiState.Idle => "idle",
            MinionAiState.Taunted => "taunted",
            MinionAiState.Feared => "feared",
            MinionAiState.Halted => "halted",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}
