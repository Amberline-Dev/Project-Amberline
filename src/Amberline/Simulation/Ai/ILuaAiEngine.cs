using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The world side of an AI script's natives: the half that already exists in the C# services
/// (targeting, combat, movement, orders, vision). A host never re-implements any of it —
/// <see cref="CombatLuaAiEngine"/> binds these to the owning <see cref="Combat.CombatService"/>,
/// so a script-driven turret is the same turret the rest of the match already knows.
/// The unit's AI state id lives here too (<see cref="AiState"/>), so a host-local copy and an
/// engine-side adoption of an external state can never disagree.
/// </summary>
public interface ILuaAiEngine
{
    /// <summary>The match clock the timers run on.</summary>
    double Now { get; }

    /// <summary>
    /// The script callback that answers a lost target. Every shipped script but Bot.luaobj names
    /// it <c>OnTargetLost</c>; Bot.luaobj's is <c>OnLostTarget</c>, which forwards to the active
    /// task's <c>OnTargetLost</c>, so the host dispatches the name this family actually defines.
    /// </summary>
    string LostTargetHook { get; }

    /// <summary><c>FindTargetInAcR</c>: the best target inside acquisition range, or none.</summary>
    Entity? FindTargetInAcquisitionRange(Entity unit);

    /// <summary><c>SetTarget</c>: the unit's retained attack target, with its wire pair on change.</summary>
    void SetTarget(Entity unit, Entity? target);

    /// <summary>
    /// <c>GetTarget</c>: the unit's retained attack target — the object the AI took, not the wire
    /// id, which other systems clear (fear clears it for the client while Leashed' OnFearBegin and
    /// OnFearEnd never touch the target). A target that already left the world answers none; ask
    /// <see cref="HeldTargetId"/> to tell that case from the AI holding nothing.
    /// </summary>
    Entity? CurrentTarget(Entity unit);

    /// <summary>
    /// The net id the AI is holding: the object it took, even one that has already left the world,
    /// or none when it holds no target (never took one, or dropped it). This is the id
    /// <see cref="CurrentTarget"/> resolves; the host's target-lost test reads it so a loss is
    /// raised for what the AI was actually fighting and never for a target the script itself
    /// dropped (Leashed' <c>Retreat</c> clears it through the retreat native).
    /// </summary>
    uint? HeldTargetId(Entity unit);

    /// <summary><c>GetTauntTarget</c>: the caster of the newest live Taunt buff.</summary>
    Entity? TauntTarget(Entity unit);

    /// <summary>
    /// The engine's target-lost test — the one <c>OnTargetLost</c> is raised for: the target
    /// died, became untargetable or protected, or changed side. Leaving attack range is not a
    /// loss.
    /// </summary>
    bool TargetStillValid(Entity unit, Entity target);

    /// <summary><c>TargetInAttackRange</c>.</summary>
    bool TargetInAttackRange(Entity unit, Entity? target);

    /// <summary><c>TurnOnAutoAttack(target)</c>: the unit swings at its target from its next attack tick.</summary>
    void TurnOnAutoAttack(Entity unit, Entity? target);

    /// <summary><c>TurnOffAutoAttack(stopReason)</c>: the unit stops swinging.</summary>
    void TurnOffAutoAttack(Entity unit, double stopReason);

    /// <summary>The unit's current <c>AI_*</c> id — what <c>GetState</c> answers.</summary>
    double AiState(Entity unit);

    /// <summary>
    /// <c>SetState</c>/<c>NetSetState</c>: the unit's <c>AI_*</c> id. The id lives here rather than
    /// in the host because a jungle engine also adopts an external <see cref="Entity.NeutralState"/>
    /// into it, and the script must see the state the rest of the match does. No AI-state packet
    /// exists in this build (<c>Reconstruction:</c>): retail put the id on the wire through
    /// NetSetState, and nothing here can.
    /// </summary>
    void SetAiState(Entity unit, double state);
}
