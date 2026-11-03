using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 501: the engine actions the tower leaves call. Each is optional: a leaf that needs a missing one fails its node and reports it
/// once, so a context without them still runs the tree.
/// </summary>
public sealed record TowerHooks(
    // TestUnitIsVisible: whether the target is in the sight of the unit's team (the fog the clients get).
    Func<Entity, Entity, bool>? Sees = null,
    // TestUnitAIAttackTargetValid: whether the unit may still attack the target (alive, an enemy, targetable to it, not a ward).
    Func<Entity, Entity, bool>? Attackable = null,
    // GetUnitSpellCastRange: the cast range of the spell in the unit's own slot; null when the slot holds none.
    Func<Entity, int, double?>? CastRange = null,
    // CastUnitSpell: the unit casts its own slot's spell on the target (the slot's cooldown is checked and started by the cast).
    Action<Entity, int, Entity>? Cast = null,
    // The unit's target line (0x6D with the 0xC8 outline), sent only on a change (CombatService.PublishTargetLine).
    Action<Entity, Entity?>? TargetLine = null,
    // Batch 662: Turret.luaobj's FindTargetInAcR for a guardian, the turret ladder within its acquisition range (GuardianTurretAi).
    Func<Entity, Entity?>? Acquire = null);

/// <summary>Batch 501: an AI tree's own unit (GetUnitAISelf) and the targets its tree set on it.</summary>
public sealed class BehaviorUnitAi(Entity self)
{
    public Entity Self { get; } = self;

    /// <summary>Get/Set/ClearUnitAIAttackTarget.</summary>
    public Entity? AttackTarget { get; internal set; }

    /// <summary>SetUnitAIAssistTarget. No tree reads it back.</summary>
    public Entity? AssistTarget { get; internal set; }

    /// <summary>SetUnitAISpellTarget: the target CastUnitSpell casts each slot at.</summary>
    public Dictionary<int, Entity> SpellTargets { get; } = new();
}

/// <summary>
/// Batch 501: the leaves of the shipped tower AI, <c>OdinTowerBehaviorTrees.xml</c> (LEVELS/Map8/Scripts). Batch 662: no unit runs it; the
/// guardians run Turret.luaobj (<see cref="GuardianTurretAi"/>, owner, 3 November 2026: "Normal turret", the map 8 AI list's OdinTower lines are
/// commented out), and the leaves stay with the tree file the loader reads. Each leaf's inputs and outputs are the parameter names of its node in the
/// shipped XML. Every node type the reachable trees use has a leaf: OdinTowerBehavior and its three procedures OdinTower_CheckForChampions,
/// OdinTowerCanCastAbility0 and OdinTowerCastAbility0 (XML :79, :605, :610). The other 15 trees are called by no shipped tree; of their node
/// types only the generic ones are here (ClearUnitAIAttackTarget, GreaterEqualFloat, LessInt, EqualString).
/// </summary>
public static partial class BehaviorLeaves
{
    public static readonly Dictionary<string, BehaviorLeaf> TowerTable = new(StringComparer.Ordinal)
    {
        // The AI's own unit, and its first tick (the tree's "Run Once", XML :7-68).
        ["GetUnitAISelf"] = call => call.Ai is { } ai ? Set(call, ai.Self) : NoAi(call),
        ["TestUnitAIFirstTime"] = call => Test(call.FirstTick == call.Flag("ReturnSuccessIf")),

        // Variables and comparisons the quest trees do not use.
        ["SetVarVector"] = call => Set(call, call.Vector("Input")),
        ["SetVarAttackableUnit"] = call => Set(call, call.Unit("Input")),
        ["LessEqualFloat"] = call => Test(call.Number("LeftHandSide") <= call.Number("RightHandSide")),
        ["GreaterEqualFloat"] = call => Test(call.Number("LeftHandSide") >= call.Number("RightHandSide")),
        ["LessInt"] = call => Test(call.Number("LeftHandSide") < call.Number("RightHandSide")),
        ["EqualString"] = call => Test(call.Text("LeftHandSide") == call.Text("RightHandSide")),
        ["NotEqualUnit"] = call => Test(!ReferenceEquals(call.Unit("LeftHandSide"), call.Unit("RightHandSide"))),
        // Reconstruction: a unit type reads as its UnitType in the shipped form (HERO_UNIT, the only value a shipped tree names, XML :128).
        ["GetUnitType"] = call => call.Unit("Unit") is { } unit
            ? Set(call, unit.UnitType.ToString().ToUpperInvariant() + "_UNIT") : BehaviorStatus.Failure,
        ["EqualUnitType"] = call => Test(call.Text("LeftHandSide") == call.Text("RightHandSide")),
        // Reconstruction: distances are centre to centre on the ground plane, as the capture leaves' radius tests are.
        ["DistanceBetweenObjectAndPoint"] = call => call.Unit("Unit") is { } unit && call.Vector("Point") is { } point
            ? Set(call, Distance(unit.Position.X, unit.Position.Z, point.X, point.Z)) : BehaviorStatus.Failure,
        ["GetDistanceBetweenUnits"] = call => call.Unit("SourceUnit") is { } from && call.Unit("DestinationUnit") is { } to
            ? Set(call, Distance(from.Position.X, from.Position.Z, to.Position.X, to.Position.Z)) : BehaviorStatus.Failure,

        ["SetUnitInvulnerable"] = SetUnitInvulnerable,
        ["TestUnitIsVisible"] = call => call.Unit("Unit") is { } unit && call.Unit("TargetUnit") is { } target &&
            Hook(call, call.Context.Capture?.Tower?.Sees, "sees") is { } sees
                ? Test(sees(unit, target) == call.Flag("ReturnSuccessIf")) : BehaviorStatus.Failure,

        // The AI's targets. Reconstruction: the AI is the tree's own unit; a node without a Unit input acts on it.
        ["GetUnitAIAttackTarget"] = call => call.Ai is { } ai ? Set(call, ai.AttackTarget) : NoAi(call),
        ["SetUnitAIAttackTarget"] = call => call.Ai is { } ai ? SetAttackTarget(ai, call.Unit("TargetUnit")) : NoAi(call),
        ["ClearUnitAIAttackTarget"] = call => call.Ai is { } ai ? SetAttackTarget(ai, null) : NoAi(call),
        ["TestUnitAIAttackTargetValid"] = call => call.Ai is { } ai
            ? Test(IsValidAttackTarget(call.Context, ai) == call.Flag("ReturnSuccessIf")) : NoAi(call),
        ["SetUnitAIAssistTarget"] = call =>
        {
            if (call.Ai is not { } ai) return NoAi(call);
            ai.AssistTarget = call.Unit("TargetUnit");
            return BehaviorStatus.Success;
        },
        ["SetUnitAISpellTarget"] = call =>
        {
            if (call.Ai is not { } ai) return NoAi(call);
            if (call.Unit("TargetUnit") is not { } target) return BehaviorStatus.Failure;
            ai.SpellTargets[(int)call.Number("SlotIndex")] = target;
            return BehaviorStatus.Success;
        },

        // The unit's own spell slots. Reconstruction: SPELLBOOK_CHAMPION (the only shipped Spellbook) is the character's Spell1..4, slots 0..3,
        // the slots BbUnitCharacterModule.CastSpellSlot casts and Entity.ScriptSlotReadyAt times.
        ["GetSpellSlotCooldown"] = call => call.Unit("Unit") is { } unit
            ? Set(call, unit.ScriptSlotReadyAt.TryGetValue((int)call.Number("SlotIndex"), out var ready) ? Math.Max(0.0, ready - call.Now) : 0.0)
            : BehaviorStatus.Failure,
        ["GetUnitSpellCastRange"] = call => call.Unit("Unit") is { } unit &&
            Hook(call, call.Context.Capture?.Tower?.CastRange, "cast-range") is { } range && range(unit, (int)call.Number("SlotIndex")) is { } reach
                ? Set(call, reach) : BehaviorStatus.Failure,
        ["CastUnitSpell"] = CastUnitSpell,
    };

    /// <summary>The AI's attack target.</summary>
    private static BehaviorStatus SetAttackTarget(BehaviorUnitAi ai, Entity? target)
    {
        ai.AttackTarget = target;
        return BehaviorStatus.Success;
    }

    // Reconstruction: TestUnitAIAttackTargetValid's native did not ship. A valid target is set, still in the world, attackable by the unit
    // (batch 466's rule: an enemy, targetable to it, not a ward) and in its team's sight; the tree tests sight again itself (XML :137, :371).
    private static bool IsValidAttackTarget(BehaviorTreeContext context, BehaviorUnitAi ai) =>
        ai.AttackTarget is { } target && ReferenceEquals(context.World.Get(target.NetId), target) &&
        context.Capture?.Tower is { Attackable: { } attackable, Sees: { } sees } && attackable(ai.Self, target) && sees(ai.Self, target);

    // CastUnitSpell (XML :1428): the slot's spell at the target SetUnitAISpellTarget set for it (else the attack target). The cast itself
    // refuses a slot that is cooling down and starts the cooldown; the guardian's Spell1 script refuses while the guardian is stunned.
    private static BehaviorStatus CastUnitSpell(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        if (Hook(call, call.Context.Capture?.Tower?.Cast, "cast") is not { } cast) return BehaviorStatus.Failure;
        var slot = (int)call.Number("SlotIndex");
        var target = call.Ai is { } ai && ReferenceEquals(ai.Self, unit) ? ai.SpellTargets.GetValueOrDefault(slot) ?? ai.AttackTarget : null;
        if (target is null) return BehaviorStatus.Failure;
        cast(unit, slot, target);
        return BehaviorStatus.Success;
    }

    // Reconstruction: the guardian is invulnerable already through its shipped OdinGuardianBuff (SetInvulnerable on every stats update), so the
    // tree's SetUnitInvulnerable Self true (XML :56) agrees with it and changes nothing. Invulnerability outside a buff is not modelled; a
    // request that differs from the unit's buffs is reported once and succeeds.
    private static BehaviorStatus SetUnitInvulnerable(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        var invulnerable = unit.AnyBuffOrStatus(buff => buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable);
        if (invulnerable != call.Flag("Enable"))
            call.Note("invulnerable:" + unit.Name, $"SetUnitInvulnerable {call.Flag("Enable")} on {unit.Name}, whose buffs say otherwise: not modelled");
        return BehaviorStatus.Success;
    }

    private static double Distance(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    private static BehaviorStatus NoAi(BehaviorCall call)
    {
        call.Note("tower:no-ai:" + call.Node.Type, $"{call.Node.Type} runs in a tree with no AI unit");
        return BehaviorStatus.Failure;
    }

    private static T? Hook<T>(BehaviorCall call, T? hook, string name) where T : class
    {
        if (hook is null) call.Note("tower:no-hook:" + name, $"this context has no tower hook {name}: {call.Node.Type} fails");
        return hook;
    }
}
