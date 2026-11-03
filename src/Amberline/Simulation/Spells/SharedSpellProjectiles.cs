using System;
using System.Collections.Generic;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.Spells;

/// <summary>
/// One delayed spell projectile in the match-owned shared spell phase.
/// Implementations retain their own typed metadata and return <c>true</c>
/// while the projectile remains in flight.
/// </summary>
public interface ISharedSpellProjectile
{
    SharedProjectileSnapshot Snapshot { get; }
    bool Tick(SpellSystem spells, double dt);
}

/// <summary>
/// A projectile that announces its launch when it is queued, for a flight whose cast has no windup (batch 177 part 2: an Instacast homing
/// child). Its cast answer is queued before the flight, so the announcement reaches the views the answer reached, in the same flush.
/// </summary>
internal interface ILaunchAnnouncer
{
    void AnnounceLaunch(SpellSystem spells);
}

/// <summary>Stable replay-visible state for one queued spell projectile.</summary>
public readonly record struct SharedProjectileSnapshot(
    string SpellName,
    uint CasterNetId,
    uint TargetNetId,
    uint MissileNetId,
    double CastRemaining,
    bool Launched,
    Position2 Position,
    double Speed,
    int Rank = 0,
    int Slot = -1,
    double Damage = 0.0,
    double ManaCost = 0.0,
    bool PyromaniaArmed = false,
    double CastWindupRemaining = 0.0,
    Position2 Origin = default,
    Position2 Direction = default,
    SpellCastInfo? CastInfo = null);

public sealed partial class SpellSystem
{
    private readonly List<ISharedSpellProjectile> _sharedProjectiles = new();
    private readonly List<ISharedSpellProjectile> _deferredProjectiles = new();
    private ISharedSpellProjectile[] _processingProjectiles = [];
    private bool _processingSharedProjectiles;
    private readonly HashSet<ISharedSpellProjectile> _retiredProcessingProjectiles =
        new(ReferenceEqualityComparer.Instance);
    private ISharedSpellProjectile? _activeSharedProjectile;

    /// <summary>Ordered, match-local projectiles awaiting the shared spell phase.</summary>
    public IReadOnlyList<ISharedSpellProjectile> SharedProjectiles => _sharedProjectiles;

    /// <summary>Queues a projectile without advancing it during its insertion phase.</summary>
    public void EnqueueSharedProjectile(ISharedSpellProjectile projectile)
    {
        ArgumentNullException.ThrowIfNull(projectile);
        // A flight with no windup has left its caster the moment its cast answer is queued: it announces itself now (batch 177 part 2).
        if (projectile is ILaunchAnnouncer announcer) announcer.AnnounceLaunch(this);
        if (_processingSharedProjectiles) _deferredProjectiles.Add(projectile);
        else _sharedProjectiles.Add(projectile);
    }

    private readonly List<Action<SpellSystem>> _lateLandings = new();

    /// <summary>
    /// The arrival work of an item active's sub-missile, held for the phase after the buff lifecycle. That is where the
    /// hand-written item missiles of the old code resolved theirs (<c>ItemService.Tick</c> runs after the buff tick), so
    /// the slow their hit adds keeps its full shipped duration for the step that delivered it. A champion child of the
    /// same <c>BBSpellCast</c> lands in the shared phase with its own kind.
    /// </summary>
    public void DeferLanding(Action<SpellSystem> landing) => _lateLandings.Add(landing);

    /// <summary>Runs the arrivals left to the phase after buff lifecycle processing.</summary>
    public void RunLateLandings()
    {
        if (_lateLandings.Count == 0) return;
        var pending = _lateLandings.ToArray();
        _lateLandings.Clear();
        foreach (var landing in pending) landing(this);
    }

    /// <summary>
    /// Advances a snapshot of the queue. New entries are therefore deferred to
    /// the next shared phase, while entries already present retain insertion order.
    /// </summary>
    public void TickSharedProjectiles(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        var pending = _sharedProjectiles.ToArray();
        _sharedProjectiles.Clear();
        _processingSharedProjectiles = true;
        _processingProjectiles = pending;
        try
        {
            foreach (var projectile in pending)
            {
                if (_retiredProcessingProjectiles.Contains(projectile)) continue;
                _activeSharedProjectile = projectile;
                try
                {
                    var remains = projectile.Tick(this, dt);
                    if (remains && !_retiredProcessingProjectiles.Contains(projectile))
                        _sharedProjectiles.Add(projectile);
                    else
                        _retiredProcessingProjectiles.Add(projectile);
                }
                finally { _activeSharedProjectile = null; }
            }
        }
        finally
        {
            _processingProjectiles = [];
            _processingSharedProjectiles = false;
            _activeSharedProjectile = null;
            _retiredProcessingProjectiles.Clear();
        }
        _sharedProjectiles.AddRange(_deferredProjectiles);
        _deferredProjectiles.Clear();
    }

    /// <summary>
    /// Runs the source projectile phase and its immediate Q-death marker
    /// cleanup without advancing champion cooldowns or other hero phases.
    /// Replay drivers use this for fixtures that isolate projectile ordering.
    /// </summary>
    public void TickSharedProjectilePhase(double dt)
    {
        TickSharedProjectiles(dt);
        _championDispatcher.TickSharedProjectilePhase(this, dt);
    }

    /// <summary>
    /// Removes homing entries aimed at a unit before it becomes untargetable (Zhonya's Hourglass, Hallucinate: the scripts call
    /// <c>BBDestroyMissileForTarget</c> themselves) or while it dies.
    /// <para>
    /// The retail rule for a unit that dies (one place for every homing and chain missile; the champion modules'
    /// <see cref="IChampionSpellModule.RetireTargetedFlights"/> follow it with <see cref="TargetedFlightRetirement.TargetDeath"/>):
    /// a missile that has left its caster is <b>not</b> destroyed by its target's death. The client's homing missile
    /// (the missile's update routine) asks only for the target <i>object</i>, not for its life, and keeps
    /// the target's last position (the last-target setter); the chain missile flies to that
    /// last position, runs its hit there and goes on to its next hop. A corpse stays in the object table until it is reaped, so the
    /// missile flies on and ends at the corpse without damaging it (it ends at once only when the unit is gone entirely). The server
    /// therefore sends no <c>DestroyClientMissile</c> and keeps the flight (a script missile runs its hit at the corpse, batch 348:
    /// the shared hit pass has no life check; the corpse refuses the damage). A cast still in its windup has no missile yet and is still cancelled and refunded (Reconstruction,
    /// owner decision of 29 September 2026: the client's caster update merely stops the cast animation when every target
    /// is dead; the retail server's windup policy is not recovered).
    /// </para>
    /// </summary>
    public int DestroyTargetedProjectiles(uint targetNetId)
    {
        if (targetNetId == 0) return 0;
        // Hallucinate also calls this helper while its owner remains alive.
        // An already-landed Condemn and a Q2 follow move are no longer
        // missiles; only an actual life ending may retire those owner states.
        var targetDead = World.Get(targetNetId) is { Alive: false };
        var removed = 0;
        foreach (var projectile in _sharedProjectiles.Concat(_deferredProjectiles)
                     .Concat(_processingProjectiles)
                     .Distinct<ISharedSpellProjectile>(ReferenceEqualityComparer.Instance).ToArray())
        {
            if (_retiredProcessingProjectiles.Contains(projectile)) continue;
            if (projectile.Snapshot.TargetNetId != targetNetId) continue;
            // The retail rule above: the missile in the air flies on to the corpse.
            if (targetDead && projectile.Snapshot.Launched) continue;
            _sharedProjectiles.Remove(projectile);
            _deferredProjectiles.Remove(projectile);
            if (_processingProjectiles.Contains(projectile))
                _retiredProcessingProjectiles.Add(projectile);
            var missile = projectile.Snapshot.MissileNetId;
            // A target-bound impact can itself kill the recipient. Its owner
            // retires that currently resolving missile after the callback.
            if (missile != 0 && !ReferenceEquals(projectile, _activeSharedProjectile))
                Effects.SendDestroyMissile(missile);
            removed++;
        }
        foreach (var hero in World.Entities.Values)
            removed += _championDispatcher.RetireTargetedFlights(this, hero, targetNetId,
                targetDead ? TargetedFlightRetirement.TargetDeath : TargetedFlightRetirement.Untargetable);
        return removed;
    }
}
