namespace Amberline.Core;

public sealed partial class Entity
{
    /// <summary>Scripted displacement currently overriding ordinary movement.</summary>
    public ForcedMoveState? ForcedMove { get; set; }

    /// <summary>
    /// Batch 458: the circle the unit's movement orders stay inside (<c>SetUnitCircularMovementRestriction</c>, Dominion's opening ceremony),
    /// null when the unit is free. See <c>MovementSystem.PlanMoveRoute</c>.
    /// </summary>
    public (Position2 Centre, double Radius)? MovementRestriction { get; set; }

    /// <summary>Explicit attack order and its last visible target position.</summary>
    public HeroPursuitState? HeroPursuit { get; set; }

    /// <summary>Stop suppresses idle attacks until a fresh movement/attack order.</summary>
    public bool HeroAutoAcquire { get; set; } = true;

    /// <summary>
    /// Batch 580: the level script's <c>SetCanAutoAcquireTargets(unit, flag)</c> (the Basic Tutorial turns its player's off in
    /// OnTutorialInit and back on in EventEnemyDefenses). Unlike <see cref="HeroAutoAcquire"/> no order changes it. Reconstruction: it
    /// gates the idle acquisition scan only; the native's engine side is not in the shipped files.
    /// </summary>
    public bool CanAutoAcquireTargets { get; set; } = true;

    /// <summary>
    /// The hero is holding position (order 1, the hold key's release): idle acquisition selects only an enemy already in attack
    /// range and never walks (Reconstruction, owner decision 18 October 2026). Every other order, Stop and death clear it.
    /// </summary>
    public bool HeroHold { get; set; }

    /// <summary>
    /// Hero-order clock time before which idle acquisition waits for a cast
    /// gesture to finish; a fresh player order clears it (playtest B2 AE6).
    /// </summary>
    public double AutoAttackResumeAt { get; set; }

    private IChampionState? _championState;

    /// <summary>Typed champion action state, when this entity is a champion.</summary>
    /// <remarks>
    /// Champion-specific partials bind their typed state through
    /// <see cref="BindChampionState"/>. Keeping one backing reference means
    /// movement, casting, and summoner gates observe the same state instance
    /// instead of an accidentally independent auto-property.
    /// </remarks>
    public IChampionState? ChampionState
    {
        get => _championState;
        set => _championState = value;
    }

    internal void BindChampionState(IChampionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_championState is not null && !ReferenceEquals(_championState, state))
            throw new InvalidOperationException(
                $"Entity {NetId} already has champion state {_championState.GetType().Name}; " +
                $"cannot bind {state.GetType().Name}.");
        _championState = state;
    }
}

/// <summary>
/// Mutable state for one scripted displacement. The simulation updates only
/// Remaining, Destination, Completed and Succeeded while the move is active.
/// </summary>
public sealed class ForcedMoveState
{
    public ForcedMoveState(
        Position2 destination,
        double speed,
        double remaining,
        uint sourceNetId,
        string effect,
        bool firstCollision = false,
        uint? followNetId = null,
        double maxTrackDistance = double.PositiveInfinity)
    {
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Z))
            throw new ArgumentOutOfRangeException(nameof(destination));
        if (!double.IsFinite(speed) || speed < 0.0)
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (!double.IsFinite(remaining) || remaining < 0.0)
            throw new ArgumentOutOfRangeException(nameof(remaining));
        if (string.IsNullOrWhiteSpace(effect))
            throw new ArgumentException("An effect name is required.", nameof(effect));
        if (double.IsNaN(maxTrackDistance) || maxTrackDistance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maxTrackDistance));

        Destination = destination;
        Speed = speed;
        Remaining = remaining;
        SourceNetId = sourceNetId;
        Effect = effect;
        FirstCollision = firstCollision;
        FollowNetId = followNetId;
        MaxTrackDistance = maxTrackDistance;
    }

    public Position2 Destination { get; set; }
    public double Speed { get; set; }
    public double Remaining { get; set; }
    internal double StartedAt { get; set; }
    internal Position2? CollisionOrigin { get; set; }
    internal double CollisionDistance { get; set; }
    public uint SourceNetId { get; }
    public string Effect { get; }
    public Amberline.Simulation.Combat.DeathRecapOrigin? RecapOrigin { get; init; }
    public bool FirstCollision { get; }
    public uint? FollowNetId { get; set; }
    public double MaxTrackDistance { get; }
    // Retain the original 0x67 presentation for observers entering vision
    // midway through this move. The simulation destination may keep changing.
    public bool DashPresentation { get; set; }
    /// <summary>
    /// Where the 0x67 dash that announced this move ends (null until one was sent, and for a following dash, whose end moves).
    /// The stock client seats the unit exactly there when its dash arrives, so a unit that lands on it needs no landing anchor.
    /// </summary>
    internal Position2? AnnouncedEnd { get; set; }
    public double DashGravity { get; set; }
    public double DashFollowDistance { get; set; }
    public double DashFollowBackDistance { get; set; }
    public bool Completed { get; set; }
    public bool Succeeded { get; set; }

    /// <summary>
    /// Reconstruction (batch 275): a first-collision move that completed short of its request because a static body stopped it. The shipped
    /// Poppy push (PoppyHeroicChargePart2) tests the distance it covered in BuffOnMoveSuccess, so a stop of this kind reaches that hook for a
    /// unit that does not track collisions (BbScriptMoves.Finish); the retail rule itself is not in the shipped files.
    /// </summary>
    public bool StoppedShort { get; set; }
}

public readonly record struct HeroPursuitState(uint TargetNetId, Position2 LastKnownPosition);
