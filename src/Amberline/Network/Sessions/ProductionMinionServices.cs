using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

internal sealed class ProductionMinionCombatService : IMinionCombatService
{
    private readonly CombatService _combat;
    public ProductionMinionCombatService(CombatService combat) => _combat = combat;
    public bool IsTargetable(Entity target, Entity observer) => _combat.IsTargetable(target, observer);
    public bool IsInAttackRange(Entity attacker, Entity target) =>
        attacker.Alive && target.Alive && CombatService.InRange(attacker, target);
    public bool HasCommittedWindup(Entity attacker, Entity target) => _combat.HasCommittedWindup(attacker, target);
    public bool LastAutoAttackFinished(Entity attacker) => _combat.LastAutoAttackFinished(attacker);
    public void InterruptWindup(Entity attacker) => _combat.InterruptBasicAttack(attacker);
}

internal sealed class ProductionMinionPacketSink : IMinionPacketSink
{
    private readonly NavigationGrid _navigation;
    private readonly ReplicationDelivery _delivery;
    private readonly ReplicationSyncCounter _sync;
    private readonly FidelityTrace? _fidelityTrace;
    private readonly CollisionDiagnosticSink? _movementTrace;
    // Batch 225 follow-up: the world clock, the live minions (to sight their dashes) and the dash destination the packet announces
    // (the first-collision landing).
    private readonly Func<double>? _clock;
    private readonly Func<IEnumerable<Entity>>? _minions;
    private readonly Func<Entity, ForcedMoveState, Position2>? _dashDestination;
    public ProductionMinionPacketSink(NavigationGrid navigation,
        ReplicationDelivery delivery, ReplicationSyncCounter sync,
        FidelityTrace? fidelityTrace = null, CollisionDiagnosticSink? movementTrace = null,
        Func<double>? clock = null, Func<Entity, ForcedMoveState, Position2>? dashDestination = null,
        Func<IEnumerable<Entity>>? minions = null)
    { _navigation = navigation; _delivery = delivery; _sync = sync;
      _fidelityTrace = fidelityTrace; _movementTrace = movementTrace; _clock = clock;
      _dashDestination = dashDestination; _minions = minions; }

    public bool IsReferenceReady(Entity minion) => minion.Created;
    public bool TryGetClientPosition(Entity minion, out Position2 position)
    { position = minion.Position; return minion.Created; }
    public void SendCreate(Entity minion) => _delivery.Enqueue(SpawnPackets.SpawnMinion(
        new SpawnMinionSnapshot(minion.NetId, SessionSpawnService.MapNetNodeId,
            minion.Position, TeamMapping.ToNative(minion.Team), minion.Name, minion.Name, SkinId: minion.SkinId),
        _navigation.HeightAt(minion.Position)));
    public void SendVisibility(Entity minion) => _delivery.Enqueue(
        VisionPackets.EnterVisibilityClient(minion.NetId, minion.Name, minion.SkinId,
            (float)minion.MaxHealth, (float)minion.Health, minion.Position,
            minion.Facing, isHero: false));
    public void SendStats(Entity minion)
    {
        var combat = CharacterState.ScriptedCombatOf(minion, minion.Kind is EntityKind.ShacoBox or EntityKind.NidaleeTrap ? 0u : 7u);
        _delivery.Enqueue(CombatPackets.MinionStats(
            minion.NetId, new MinionStatsSnapshot((float)minion.MaxHealth, (float)minion.AttackDamage,
                (float)minion.Speed, (float)minion.Armor, (float)minion.SpellBlock,
                (float)minion.AttackSpeedMod, (float)minion.HealthRegen,
                ActionState: combat.ActionState, MagicImmune: combat.MagicImmune,
                Invulnerable: combat.Invulnerable, Targetable: combat.Targetable,
                TargetableToTeam: CombatPackets.MinionTargetableToTeam(minion),
                // Batch 701: the maximum and the current health each in its own slot (this block wrote the current health into both).
                CurrentHp: (float)Math.Min(minion.Health, minion.MaxHealth)), _sync.Next(minion.NetId)));
    }
    // Reconstruction: retail server waypoint scheduling is unknown. We queue per-entity
    // reliable waypoint packets and flush them with the 30 Hz simulation/visibility
    // phase; NPC movement itself advances at 100 ms.
    public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints,
        long syncId, string reason)
    {
        if (minion.ForcedMove is { Completed: false })
        {
            HoldOrder(minion, waypoints, syncId, reason);
            return;
        }
        // A completed move whose dash still runs on the wire (batch 225 follow-up): the order waits for the wire end.
        if (_clock is not null && _clock() + 1e-9 < WireEndOf(minion))
        {
            HoldOrder(minion, waypoints, syncId, reason);
            return;
        }
        SendNow(minion, waypoints, syncId, reason);
    }

    private void SendNow(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason)
    {
        _delivery.Enqueue(VisionPackets.WaypointList(
            minion.NetId, waypoints, checked((uint)syncId)));
        _fidelityTrace?.NoteOrder(minion.NetId, "waypoint",
            reason == "wave-spawn" ? "reveal-anchor" : reason, syncId, waypoints);
        _movementTrace?.NoteWaypointIntent(minion.NetId, reason, syncId, waypoints);
    }
    // Reconstruction (batch 211 follow-up): the stock client replaces a dash with the first 0xC1 it receives (PacketStreamChecker rule 7;
    // real-client captures of 17 October 2026), so an order issued while the minion's own forced move runs waits for the move's wire end.
    // The AI decides on the script's schedule (OnCanMove at the status end); only the packet waits, and the route starts where the dash lands.
    private readonly List<HeldOrder> _held = new();
    private sealed record HeldOrder(Entity Minion, Position2[] Waypoints, long SyncId, string Reason);
    // Batch 225 follow-up (batch 211's rule): the stock client replaces a dash with the first 0xC1 it receives, and the dash ends
    // at its wire end, the start plus the announced path's length over the speed. The server can complete a move a few
    // milliseconds before that, so an order waits for the later of the completion and the wire end. The wire end is sighted
    // while a dash runs (FlushHeldOrders runs at the top of each minion tick, before the AI): a straight dash from the current
    // position ends at the same time as the packet's, so the wire time left is the distance to the destination over the speed.
    private readonly Dictionary<uint, double> _wireEnds = new();

    private void SightDashes()
    {
        if (_minions is null || _clock is null) return;
        var now = _clock();
        foreach (var minion in _minions())
        {
            var move = minion.ForcedMove;
            if (!minion.Alive || move is not { Completed: false } || move.Effect != "Fear" || !(move.Speed > 0.0)) continue;
            var destination = move.Destination;
            var dx = destination.X - minion.Position.X;
            var dz = destination.Z - minion.Position.Z;
            _wireEnds[minion.NetId] = now + Math.Sqrt(dx * dx + dz * dz) / move.Speed;
        }
    }

    private double WireEndOf(Entity minion) =>
        _wireEnds.TryGetValue(minion.NetId, out var end) ? end : double.NegativeInfinity;

    private void HoldOrder(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason)
    {
        _held.RemoveAll(order => ReferenceEquals(order.Minion, minion));
        _held.Add(new HeldOrder(minion, waypoints.ToArray(), syncId, reason));
    }

    public void FlushHeldOrders()
    {
        SightDashes();
        if (_held.Count == 0) return;
        var now = _clock?.Invoke() ?? double.PositiveInfinity;
        var due = new List<HeldOrder>();
        _held.RemoveAll(order =>
        {
            if (order.Minion.Alive && order.Minion.ForcedMove is { Completed: false }) return false;
            if (order.Minion.Alive && now + 1e-9 < WireEndOf(order.Minion)) return false;
            due.Add(order);
            return true;
        });
        foreach (var order in due)
        {
            if (!order.Minion.Alive) continue;
            var route = order.Waypoints.ToArray();
            if (route.Length > 0) route[0] = order.Minion.Position;
            SendNow(order.Minion, route, order.SyncId, order.Reason);
        }
    }

    public void SendStop(Entity minion, long syncId, string reason) => SendWaypoints(
        minion, [minion.Position], syncId, reason);
    // Reconstruction: the retail server's stop-attack flags per STOPREASON are unknown (native TurnOffAutoAttack); CombatPackets.StopAttack
    // decides whether and with which flags, from what the server did with the hit.
    public void SendStopAttack(Entity minion, string reason)
    {
        if (CombatPackets.StopAttack(minion) is not { } packet || !IsReferenceReady(minion)) return;
        _delivery.Enqueue(packet);
        _fidelityTrace?.NoteOrder(minion.NetId, "stop-attack", $"{reason} 0x{packet.Plaintext.Span[5]:X2}", 0, [minion.Position]);
    }
}
