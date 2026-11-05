using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 662 (owner, 3 November 2026: "Normal turret"): the capture point guardians run the plain turret AI, <c>DATA/Scripts/Turret.luaobj</c>,
/// on a host of their own. The shipped map 8 AI list (<c>LEVELS/Map8/Scripts/AiEntityDataFile.dat</c>) binds OdinNeutralGuardian,
/// OdinOrderGuardian and OdinChaosGuardian to the AI_Entity tag <c>OdinGuardian</c> (lines 17, 20, 21, 28), which did not ship, and has the
/// lines that bound the team guardians to the OdinTower tree commented out (18-19); the wiki's Capture point page says an allied point "will
/// attack enemy units like a turret". So the guardian runs the script the engine runs for every turret, with no ally gate.
/// Reconstruction: the natives answer with the guardian's own record, whose attack is its Spell1, not a basic attack (OdinNeutralGuardian.inibin:
/// AttackRange 0, AcquisitionRange 950, Spell1 OdinGuardianSpellAttackCast, CastRange 1000, Cooldown 1; batch 466 read it the same way):
/// <list type="bullet">
/// <item>FindTargetInAcR: the turret ladder (<c>CombatService.AcquireTurretTarget</c>: summoned units, then minions, then the rest, the nearest
/// of each) over the units it may attack, within its <see cref="Reach"/> (batch 687: the OdinTower tree's 600; batch 662 took the record's 950);</item>
/// <item>TargetInAttackRange: within the same <see cref="Reach"/>, centre to centre (batch 662 took its Spell1's cast range, 1000);</item>
/// <item>TurnOnAutoAttack: it casts its Spell1 at the target on every step the attack is on and the target is valid and in that range; the
/// cast is refused while the slot cools down, so it fires once a second.</item>
/// </list>
/// The target line (0x6D with the 0xC8 outline) is that target on such a step, else none: batch 660's rule, the owner's laser rule of batch 529
/// ("the laser should start targeting exactly where it starts dealing damage").
/// </summary>
internal sealed class GuardianTurretAi : ILuaAiTurretScriptEngine
{
    private readonly WorldState _world;
    private readonly TowerHooks _hooks;
    private readonly LuaAiService _host;
    private readonly Dictionary<uint, double> _state = new();
    private readonly Dictionary<uint, uint> _targets = new();
    private readonly HashSet<uint> _attacking = new();

    public GuardianTurretAi(WorldState world, TowerHooks hooks, Action<string, string> note)
    {
        _world = world;
        _hooks = hooks;
        _host = new LuaAiService(world, this) { OnError = failure => note("guardian-ai:" + failure.Hook, failure.Message) };
    }

    /// <summary>One step of the guardian's script; returns what its target line shows after it, the target it fires at, else none.</summary>
    public Entity? Step(Entity guardian, double now, double dt)
    {
        Now = now;
        _host.StepUnit(guardian, now, dt);
        if (!_attacking.Contains(guardian.NetId) || CurrentTarget(guardian) is not { } target ||
            !TargetStillValid(guardian, target) || !TargetInAttackRange(guardian, target)) return null;
        _hooks.Cast?.Invoke(guardian, 0, target);
        return target;
    }

    public double Now { get; private set; }

    public string LostTargetHook => "OnTargetLost";

    public Entity? FindTargetInAcquisitionRange(Entity unit) => _hooks.Acquire?.Invoke(unit);

    public void SetTarget(Entity unit, Entity? target)
    {
        if (target is null) _targets.Remove(unit.NetId);
        else _targets[unit.NetId] = target.NetId;
    }

    public Entity? CurrentTarget(Entity unit) => HeldTargetId(unit) is uint id ? _world.Get(id) : null;

    public uint? HeldTargetId(Entity unit) => _targets.TryGetValue(unit.NetId, out var id) ? id : null;

    public Entity? TauntTarget(Entity unit) => PetControl.TauntTarget(_world, unit);

    // The turret's loss test (CombatLuaAiEngine): died, untargetable or changed side; leaving range is not a loss.
    public bool TargetStillValid(Entity unit, Entity target) => target.Alive && _hooks.Attackable?.Invoke(unit, target) == true;

    public bool TargetInAttackRange(Entity unit, Entity? target) => target is not null && InReach(unit, target);

    /// <summary>
    /// Batch 687 (community report, 4 November 2026: "Shrines on Dominion have way too big range"; replaces batch 662's 950 and 1000). The
    /// guardian's reach, to acquire and to keep its target, centre to centre: the shipped OdinTower tree's <c>AcquisitionRange</c> and
    /// <c>AttackRadius</c>, both 600 (<c>LEVELS/Map8/Scripts/OdinTowerBehaviorTrees.xml:32-41</c>; it acquires with GetUnitsInTargetArea over
    /// that radius and drops a target past it, :150-170). That tree is the one tower AI the designers wrote for these units. The record holds no
    /// attack range (AttackRange 0); its AcquisitionRange 950 and its attack spell's CastRange 1000 are wider bounds than its tower AI's, as an
    /// SR turret's basic attack spell has CastRange 400 under its 750 range. Reconstruction: the AI_Entity <c>OdinGuardian</c> that ran on
    /// retail's guardians did not ship.
    /// </summary>
    internal const double Reach = 600.0;

    internal static bool InReach(Entity guardian, Entity target) =>
        (guardian.Position.X - target.Position.X) * (guardian.Position.X - target.Position.X) +
        (guardian.Position.Z - target.Position.Z) * (guardian.Position.Z - target.Position.Z) <= Reach * Reach;

    public void TurnOnAutoAttack(Entity unit, Entity? target)
    {
        if (target is not null) SetTarget(unit, target);
        _attacking.Add(unit.NetId);
    }

    public void TurnOffAutoAttack(Entity unit, double stopReason) => _attacking.Remove(unit.NetId);

    public double AiState(Entity unit) => _state.GetValueOrDefault(unit.NetId);

    public void SetAiState(Entity unit, double state) => _state[unit.NetId] = state;
}
