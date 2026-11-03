using System.Globalization;
using Amberline.Core;
using Amberline.Simulation.Encounters;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>One leaf: the data or game action of a node type. It reports Success or Failure (Running only where a node waits).</summary>
public delegate BehaviorStatus BehaviorLeaf(BehaviorCall call);

/// <summary>
/// What a tree reads and does besides its own variables, and the notes it has already reported. <see cref="Note"/> sends each
/// key to <see cref="Sink"/> once, so an unimplemented node type is logged once per process, not once per tick.
/// </summary>
public sealed class BehaviorTreeContext
{
    /// <summary>The world the trees read (units and heroes); the clock comes from each tick.</summary>
    public required WorldState World { get; init; }

    /// <summary>GetWorldLocationByName: a named location's position, or null when the map names no such location.</summary>
    public Func<string, Position3?> NamedLocation { get; init; } = _ => null;

    /// <summary>GetTurret(team, lane, position): the turret, or null when the map names none.</summary>
    public Func<Team, int, int, Entity?> Turret { get; init; } = (_, _, _) => null;

    /// <summary>Batch 466: the guardian of the capture point at GetTurret position 2 to 6 (point A to E), or null when none stands.</summary>
    public Func<int, Entity?> PointGuardian { get; init; } = _ => null;

    /// <summary>Batch 446: the encounters the encounter leaves create, spawn, mutate and delete; null on a map with none.</summary>
    public EncounterService? Encounters { get; init; }

    /// <summary>Batch 448: the engine hooks the capture leaves call (packets, kills, gold, announcements); null on a map with none.</summary>
    public CapturePointHooks? Capture { get; init; }

    /// <summary>Batch 518: the Dominion personal score the capture leaves credit (set by the map 8 host); null elsewhere.</summary>
    public DominionPlayerScore? PlayerScore { get; set; }

    /// <summary>The terrain height under a point: the Y of a unit's position.</summary>
    public Func<Position2, double> Height { get; init; } = _ => 0.0;

    /// <summary>DIAGNOSTICS: test seam. Leaves here run before the generic table, so a check can give a node a scripted result.</summary>
    public Dictionary<string, BehaviorLeaf> Leaves { get; } = new(StringComparer.Ordinal);

    /// <summary>Receives each note once: an unimplemented node type, an unresolved name.</summary>
    public Action<string, string>? Sink { get; init; }

    /// <summary>The keys already reported.</summary>
    public HashSet<string> Noted { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The last capture record sent per opcode and point net id (the capture leaves send a record only when it changes). Batch 472: a
    /// returning client gets the 0xDB and 0xDC held here (BehaviorTreeQuestHost.ReconnectState); a 0xDE key only shows its create went out.
    /// </summary>
    internal Dictionary<(byte Opcode, uint NetId), Amberline.Network.Protocol.PlaintextPacket> SentCapture { get; } = new();

    /// <summary>Batch 453 (Reconstruction): the teams a respawn point allows, per RespawnPointID (bit 0 Order, bit 1 Chaos).</summary>
    internal Dictionary<int, byte> RespawnTeams { get; } = new();

    /// <summary>Batch 455: the one-time announcements already sent in this match (the start messages go out once).</summary>
    internal HashSet<string> AnnouncedOnce { get; } = new(StringComparer.Ordinal);

    /// <summary>Batch 460: <c>SetBTInstanceStatus</c>'s switches by BTInstance name (OdinScript.xml); an instance not named here runs.</summary>
    internal Dictionary<string, bool> InstanceEnabled { get; } = new(StringComparer.Ordinal);

    public void Note(string key, string detail)
    {
        if (Noted.Add(key)) Sink?.Invoke(key, detail);
    }
}

/// <summary>One tree invocation's variables, and the state of nodes that are running (composites, procedure calls, iterations).</summary>
internal sealed class BehaviorFrame
{
    public BehaviorFrame(BehaviorTreeDefinition tree) => Tree = tree;

    public BehaviorTreeDefinition Tree { get; }
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>The running state of a node: a composite's child index, a Procedure's callee frame, an iteration's index.</summary>
    public Dictionary<BehaviorNode, object?> Running { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The child a composite resumes at: the one that was running, else the first.</summary>
    public int Resume(BehaviorNode node) => Running.TryGetValue(node, out var index) && index is int child ? child : 0;
}

/// <summary>
/// One running copy of a behaviour tree: its instance variables persist between ticks, and every Procedure call gets its own
/// frame. The engine variables (names starting with <c>__</c>) are shared by the instance.
/// Reconstruction: the shipped XML has no scope attribute on any variable; this split is the convention the trees follow
/// (tree inputs and outputs, the root's persisting variables, and the <c>__</c> names the engine supplies).
/// </summary>
public sealed class BehaviorTreeInstance
{
    private readonly BehaviorTreeLibrary _library;
    private readonly BehaviorFrame _root;
    private readonly Dictionary<string, object?> _engine = new(StringComparer.Ordinal);
    private bool _ticked;

    public BehaviorTreeInstance(BehaviorTreeLibrary library, string treeName, BehaviorTreeContext context)
    {
        _library = library;
        Context = context;
        TreeName = treeName;
        _root = new BehaviorFrame(library.Trees.TryGetValue(treeName, out var tree)
            ? tree
            : throw new ArgumentException($"no behaviour tree named {treeName}", nameof(treeName)));
    }

    public string TreeName { get; }
    public BehaviorTreeContext Context { get; }

    /// <summary>Batch 501: the unit an AI tree runs for and the targets its tree set (OdinTower.xml, Script Type "AI"); null for a quest.</summary>
    public BehaviorUnitAi? Ai { get; init; }

    /// <summary>What the last tick reported (Failure before the first tick).</summary>
    public BehaviorStatus Status { get; private set; } = BehaviorStatus.Failure;

    /// <summary>How many ticks the instance has run.</summary>
    public int Ticks { get; private set; }

    /// <summary>The game time of the current tick.</summary>
    internal double Now { get; private set; }

    /// <summary>The length of the current tick: <c>NormalizeFloatToTickRate</c> multiplies by it.</summary>
    internal double TickSeconds { get; private set; }

    /// <summary>An instance variable as the last tick left it (null when unset).</summary>
    public object? Variable(string name) => _root.Variables.GetValueOrDefault(name);

    /// <summary>Sets an instance variable before a tick (a host seeding the quest's inputs).</summary>
    public void SetVariable(string name, object? value) => _root.Variables[name] = value;

    /// <summary>
    /// Runs one tick. <c>__IsFirstRun</c> is true during the first tick only (Reconstruction: no node writes it; the shipped
    /// trees read it as "first run of this instance", and the tower trees name the same idea "Is this the first tick?").
    /// </summary>
    public BehaviorStatus Tick(double now, double dt)
    {
        Now = now;
        TickSeconds = dt;
        Ticks++;
        _engine["__IsFirstRun"] = !_ticked;
        var root = _library.Trees[TreeName].Root;
        if (root is null)
        {
            Context.Note("no-root:" + TreeName, $"{TreeName} has no body");
            Status = BehaviorStatus.Failure;
        }
        else
        {
            Status = Execute(root, _root);
        }
        _ticked = true;
        return Status;
    }

    internal object? Resolve(BehaviorBinding binding, BehaviorFrame frame) => binding.VariableType switch
    {
        "Value" => binding.Literal,
        "Reference" => binding.Text.Length == 0 ? null : Read(frame, binding.Text),
        // Reconstruction: "Reference, Value" (tree inputs) reads the variable when it is set, else the literal default.
        _ => binding.Text.Length > 0 && Read(frame, binding.Text) is { } variable ? variable : binding.Literal,
    };

    internal object? Read(BehaviorFrame frame, string name) =>
        name.StartsWith("__", StringComparison.Ordinal) ? _engine.GetValueOrDefault(name) : frame.Variables.GetValueOrDefault(name);

    internal void Write(BehaviorFrame frame, string name, object? value)
    {
        if (name.StartsWith("__", StringComparison.Ordinal)) _engine[name] = value;
        else frame.Variables[name] = value;
    }

    internal BehaviorStatus Execute(BehaviorNode node, BehaviorFrame frame) => node.Type switch
    {
        "Sequence" => Sequence(node, frame),
        "Selector" => Selector(node, frame),
        "Procedure" => Procedure(node, frame),
        "IterateOverAllDecorator" => Iterate(node, frame),
        "MaskFailure" => MaskFailure(node, frame),
        _ => Leaf(node, frame),
    };

    // Reconstruction: Sequence and Selector are the usual BT composites. A Running child is resumed next tick.
    private BehaviorStatus Sequence(BehaviorNode node, BehaviorFrame frame)
    {
        for (var index = frame.Resume(node); index < node.Children.Count; index++)
        {
            var status = Execute(node.Children[index], frame);
            if (status == BehaviorStatus.Running)
            {
                frame.Running[node] = index;
                return status;
            }
            if (status == BehaviorStatus.Failure)
            {
                frame.Running.Remove(node);
                return status;
            }
        }
        frame.Running.Remove(node);
        return BehaviorStatus.Success;
    }

    private BehaviorStatus Selector(BehaviorNode node, BehaviorFrame frame)
    {
        for (var index = frame.Resume(node); index < node.Children.Count; index++)
        {
            var status = Execute(node.Children[index], frame);
            if (status == BehaviorStatus.Running)
            {
                frame.Running[node] = index;
                return status;
            }
            if (status == BehaviorStatus.Success)
            {
                frame.Running.Remove(node);
                return status;
            }
        }
        frame.Running.Remove(node);
        return BehaviorStatus.Failure;
    }

    // Reconstruction: a Procedure binds the caller's values to the callee's declared inputs (unbound ones take the declared
    // Default), runs the callee in its own frame, and copies the callee's outputs back when it finishes (success or failure).
    private BehaviorStatus Procedure(BehaviorNode node, BehaviorFrame frame)
    {
        if (!_library.Trees.TryGetValue(node.Name, out var target) || target.Root is null)
        {
            Context.Note("procedure:" + node.Name, $"{frame.Tree.Name} calls {node.Name}, which has no body");
            return BehaviorStatus.Failure;
        }
        var callee = frame.Running.TryGetValue(node, out var running) && running is BehaviorFrame resumed
            ? resumed
            : Bind(node, target, frame);
        var status = Execute(target.Root, callee);
        if (status == BehaviorStatus.Running)
        {
            frame.Running[node] = callee;
            return status;
        }
        frame.Running.Remove(node);
        foreach (var output in node.Outputs)
            Write(frame, output.Variable, callee.Variables.GetValueOrDefault(output.Name));
        return status;
    }

    private BehaviorFrame Bind(BehaviorNode node, BehaviorTreeDefinition target, BehaviorFrame caller)
    {
        var callee = new BehaviorFrame(target);
        foreach (var parameter in target.Inputs)
        {
            var binding = node.Input(parameter.Name);
            callee.Variables[parameter.Name] = (binding is null ? null : Resolve(binding, caller))
                ?? BehaviorTreeLoader.ParseLiteral(parameter.Default);
        }
        return callee;
    }

    // Reconstruction: the decorator runs its child once per element, the element in its Output variable, and succeeds when
    // the walk ends (a child's failure does not stop the walk). The shipped uses count or pick inside the walk. An unset
    // collection is empty.
    private BehaviorStatus Iterate(BehaviorNode node, BehaviorFrame frame)
    {
        if (node.Children.Count != 1) return BehaviorStatus.Failure;
        var items = (node.Input("Collection") is { } binding ? Resolve(binding, frame) : null) as IReadOnlyList<Entity>
            ?? Array.Empty<Entity>();
        var output = node.Output("Output")?.Variable;
        for (var index = frame.Resume(node); index < items.Count; index++)
        {
            if (output is not null) Write(frame, output, items[index]);
            var status = Execute(node.Children[0], frame);
            if (status == BehaviorStatus.Running)
            {
                frame.Running[node] = index;
                return status;
            }
        }
        frame.Running.Remove(node);
        return BehaviorStatus.Success;
    }

    // Reconstruction: MaskFailure reports Success whenever its child fails; Running passes through.
    private BehaviorStatus MaskFailure(BehaviorNode node, BehaviorFrame frame)
    {
        if (node.Children.Count != 1) return BehaviorStatus.Failure;
        var status = Execute(node.Children[0], frame);
        return status == BehaviorStatus.Failure ? BehaviorStatus.Success : status;
    }

    private BehaviorStatus Leaf(BehaviorNode node, BehaviorFrame frame)
    {
        var leaf = Context.Leaves.GetValueOrDefault(node.Type) ?? BehaviorLeaves.Generic.GetValueOrDefault(node.Type)
            ?? BehaviorLeaves.EncounterTable.GetValueOrDefault(node.Type) ?? BehaviorLeaves.CaptureTable.GetValueOrDefault(node.Type)
            ?? BehaviorLeaves.RelicTable.GetValueOrDefault(node.Type) ?? BehaviorLeaves.TowerTable.GetValueOrDefault(node.Type)
            ?? BehaviorLeaves.ScoreTable.GetValueOrDefault(node.Type);
        if (leaf is null)
        {
            // An unknown node type fails its node and is reported once per type; it never throws.
            Context.Note("unimplemented:" + node.Type, $"{frame.Tree.Name}/{node.Name}");
            return BehaviorStatus.Failure;
        }
        return leaf(new BehaviorCall(this, node, frame));
    }
}

/// <summary>What a leaf sees of its node: the resolved inputs, the outputs to write, and the tick's clock.</summary>
public sealed class BehaviorCall
{
    private readonly BehaviorTreeInstance _instance;
    private readonly BehaviorFrame _frame;

    internal BehaviorCall(BehaviorTreeInstance instance, BehaviorNode node, BehaviorFrame frame)
    {
        _instance = instance;
        Node = node;
        _frame = frame;
    }

    public BehaviorNode Node { get; }
    public BehaviorTreeContext Context => _instance.Context;
    public double Now => _instance.Now;
    public double TickSeconds => _instance.TickSeconds;

    /// <summary>Batch 501: the AI state of the unit the tree runs for (GetUnitAISelf and the AI targets); null in a quest tree.</summary>
    public BehaviorUnitAi? Ai => _instance.Ai;

    /// <summary>Batch 501: whether this is the instance's first tick (TestUnitAIFirstTime, the tower trees' "Is this the first tick?").</summary>
    public bool FirstTick => _instance.Ticks == 1;

    /// <summary>Batch 550: the tree the node belongs to (a Procedure's callee runs in its own tree).</summary>
    public string TreeName => _frame.Tree.Name;

    /// <summary>Batch 550: a variable of the running instance's root (the caller of a Procedure, e.g. a capture point's CapturePointID).</summary>
    public object? InstanceVariable(string name) => _instance.Variable(name);

    /// <summary>The value bound to an input (null when unbound or unset).</summary>
    public object? In(string name) => Node.Input(name) is { } binding ? _instance.Resolve(binding, _frame) : null;

    /// <summary>An input as a number; an unset input reads as <paramref name="fallback"/>.</summary>
    public double Number(string name, double fallback = 0.0) => ToNumber(In(name)) ?? fallback;

    /// <summary>An input as a boolean; an unset input is false.</summary>
    public bool Flag(string name) => In(name) is bool flag && flag;

    /// <summary>An input as text (numbers in invariant form, teams as TEAM_*); an unset input is empty.</summary>
    public string Text(string name) => ToText(In(name));

    /// <summary>An input as a team. Reconstruction: an unset team reads as neutral.</summary>
    public Team TeamOf(string name) => In(name) is Team team ? team : Team.Neutral;

    public Position3? Vector(string name) => In(name) is Position3 point ? point : null;

    public Entity? Unit(string name) => In(name) as Entity;

    public IReadOnlyList<Entity>? Units(string name) => In(name) as IReadOnlyList<Entity>;

    /// <summary>Writes an output (the variable the node names for it). A node without that output writes nothing.</summary>
    public void Out(string name, object? value)
    {
        if (Node.Output(name) is { } output) _instance.Write(_frame, output.Variable, value);
    }

    /// <summary>Reports a note once for the whole context.</summary>
    public void Note(string key, string detail) => Context.Note(key, detail);

    internal static double? ToNumber(object? value) => value switch
    {
        double number => number,
        int integer => integer,
        bool flag => flag ? 1 : 0,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    internal static string ToText(object? value) => value switch
    {
        null => "",
        string text => text,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Team team => "TEAM_" + team.ToString().ToUpperInvariant(),
        _ => value.ToString() ?? "",
    };
}
