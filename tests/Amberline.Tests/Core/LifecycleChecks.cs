using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static class LifecycleChecks
{


    [Check("core-lifecycle-reporting")]
    public static void ReporterUsesActiveViolationEdges()
    {
        var world = new WorldState();
        var unit = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(10, 20))
        { ChampionName = "Annie", Health = 100, MaxHealth = 100 };
        world.Register(unit);
        var reports = new List<LifecycleViolation>();
        var monitor = new LifecycleMonitor(world, report: reports.Add);
        var firstMove = new ForcedMoveState(new(30, 40), 100, 1, unit.NetId, "Move");
        firstMove.Remaining = double.NaN;
        unit.ForcedMove = firstMove;
        monitor.Check(0.0, force: true);
        monitor.Check(0.1, force: true);
        TestAssert.Equal(1, reports.Count, "persistent lifecycle violation must log once");
        unit.ForcedMove = null;
        monitor.Check(0.2, force: true);
        var secondMove = new ForcedMoveState(new(30, 40), 100, 1, unit.NetId, "Move");
        secondMove.Remaining = double.NaN;
        unit.ForcedMove = secondMove;
        monitor.Check(0.3, force: true);
        TestAssert.Equal(2, reports.Count, "a reappearing lifecycle violation must log again");
    }



    private sealed class LifecycleReplay
    {
        private readonly JsonElement _root;
        private readonly WorldState _world = new();
        private readonly Dictionary<uint, Entity> _entities = new();
        private readonly Dictionary<uint, LifecycleStateView> _views = new();
        private readonly Dictionary<string, object> _objects = new(StringComparer.Ordinal);
        private LifecycleMonitor? _monitor;
        private int _monitorId = -1;
        public LifecycleReplay(JsonElement root) => _root = root;

        public void Run()
        {
            TestAssert.Equal(1, _root.GetProperty("schemaVersion").GetInt32());
            TestAssert.Equal(14, _root.GetProperty("monitorCount").GetInt32());
            var calls = _root.GetProperty("calls");
            TestAssert.Equal(126, calls.GetArrayLength());
            var n = 0;
            foreach (var call in calls.EnumerateArray())
            {
                n++;
                var id = call.GetProperty("monitor").GetInt32();
                if (_monitor is null || id != _monitorId)
                {
                    _monitorId = id;
                    _monitor = new LifecycleMonitor(_world, call.GetProperty("interval").GetDouble(), call.GetProperty("grace").GetDouble(),
                        entity => _views.TryGetValue(entity.NetId, out var view)
                            ? view : LifecycleStateView.FromEntity(entity));
                }
                _world.StrictInvariants = false;
                Apply(call.GetProperty("before"), call.GetProperty("objectIdentities"));
                _world.Clock.SetVirtual(call.GetProperty("combatClock").GetDouble());
                _world.StrictInvariants = call.GetProperty("strict").GetBoolean();
                var fingerprint = Surface();
                IReadOnlyList<LifecycleViolation> actual = Array.Empty<LifecycleViolation>();
                Exception? error = null;
                try { actual = _monitor.Check(call.GetProperty("now").GetDouble(), call.GetProperty("force").GetBoolean()); }
                catch (Exception ex) { error = ex; actual = _monitor.Records; }
                CompareRecords(call, actual, n);
                CompareError(call, error, n);
                if (call.GetProperty("worldUnchanged").GetBoolean())
                    TestAssert.Equal(fingerprint, Surface(), $"lifecycle monitor mutated world at call {n}");
            }
        }

        private void Apply(JsonElement before, JsonElement ids)
        {
            var present = new HashSet<uint>();
            foreach (var raw in before.GetProperty("entities").EnumerateArray())
            {
                var id = raw.GetProperty("netid").GetUInt32();
                present.Add(id);
                if (!_entities.TryGetValue(id, out var e))
                    e = _entities[id] = new Entity(id, EntityKind.Unknown, UnitType.Hero, Team.Order, Point(raw.GetProperty("pos")));
                ReadEntity(e, raw, ids);
            }
            foreach (var id in _entities.Keys.Where(x => !present.Contains(x)).ToArray())
            {
                _entities.Remove(id);
                _views.Remove(id);
            }
            _world.Entities.Clear();
            foreach (var e in _entities.Values) _world.Entities.Add(e.NetId, e);
        }

        private void ReadEntity(Entity e, JsonElement raw, JsonElement ids)
        {
            e.Kind = EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString());
            e.UnitType = (UnitType)raw.GetProperty("unit_type").GetInt32();
            e.Team = (Team)raw.GetProperty("team").GetInt32();
            e.Position = Point(raw.GetProperty("pos"));
            e.Alive = raw.GetProperty("alive").GetBoolean();
            e.OwnerNetId = raw.GetProperty("owner_netid").GetUInt32();
            e.Name = raw.GetProperty("name").GetString() ?? "";
            e.ChampionName = raw.GetProperty("champion_name").GetString() ?? "";
            e.ExpiresAt = raw.GetProperty("expires_at").GetDouble();
            e.RespawnAt = raw.GetProperty("respawn_at").GetDouble();
            e.GodMode = raw.GetProperty("god_mode").GetBoolean();
            e.CooldownsDisabled = raw.GetProperty("cooldowns_disabled").GetBoolean();
            e.Health = e.MaxHealth = 100; e.Radius = 0;
            var path = e.NetId.ToString(CultureInfo.InvariantCulture);
            e.ForcedMove = ReadForced(e, raw.GetProperty("forced_move"), ids, path + "/forced_move");
            e.Buffs.Clear();
            var buffs = raw.GetProperty("buffs");
            for (var i = 0; i < buffs.GetArrayLength(); i++) e.Buffs.Add(ReadBuff(e, buffs[i], ids, $"{path}/buffs/{i}"));
            e.YorickRevival = raw.GetProperty("yorick_revival").ValueKind == JsonValueKind.Null ? null :
                ReadRevival(raw.GetProperty("yorick_revival"), ids, path + "/yorick_revival");
            // The fixture deliberately records independent state machines for
            // one synthetic hero. Keep that test projection beside the world;
            // production Entity has only its single authoritative state slot.
            _views[e.NetId] = new LifecycleStateView();
            // 19 October 2026: Janna, Vayne, Blitzcrank, Shaco, Nidalee, Orianna, Ezreal, Annie and Yorick have no champion state any more (script flights,
            // buffs and units); their recorded columns are not read, and the monitor has no cast-timer or missile component to watch for them. The
            // corpus has no recorder hook; the records of those components (shaco_cast, ezreal_missile, janna_r_channel, pending_summon ...) were
            // removed by hand from the expected output of the calls, with the strict-mode error that only they raised.
        }

        private ForcedMoveState? ReadForced(Entity owner, JsonElement r, JsonElement ids, string path)
        {
            if (r.ValueKind != JsonValueKind.Object || !r.EnumerateObject().Any()) return null;
            var p = Point(r.GetProperty("destination"));
            if (Identity(ids, path) is int id && Get<ForcedMoveState>(path, id) is { } old)
            { old.Destination = p; old.Speed = r.GetProperty("speed").GetDouble(); old.Remaining = r.GetProperty("remaining").GetDouble(); old.Completed = r.GetProperty("completed").GetBoolean(); old.Succeeded = r.TryGet("succeeded", out var ok) && ok.GetBoolean(); return old; }
            var value = new ForcedMoveState(p, r.GetProperty("speed").GetDouble(), r.GetProperty("remaining").GetDouble(), r.GetProperty("source_netid").GetUInt32(), r.GetProperty("effect").GetString() ?? "move", r.TryGet("first_collision", out var fc) && fc.GetBoolean());
            value.Completed = r.GetProperty("completed").GetBoolean(); value.Succeeded = r.TryGet("succeeded", out var succ) && succ.GetBoolean(); return Store(path, ids, value);
        }
        private BuffState ReadBuff(Entity owner, JsonElement r, JsonElement ids, string path)
        {
            var payload = ReadPayload(r.TryGet("payload", out var p) ? p : default, ids, path + "/payload", owner);
            if (Identity(ids, path) is int id && Get<BuffState>(path, id) is { } old)
            { old.ExpiresAt = r.GetProperty("expires_at").GetDouble(); old.CasterNetId = r.GetProperty("caster").GetUInt32(); old.Slot = r.TryGet("slot", out var slot) ? slot.GetInt32() : -1; old.Payload = payload; return old; }
            return Store(path, ids, new BuffState(r.GetProperty("name").GetString() ?? "buff", payload, (BuffType)r.GetProperty("type").GetInt32(), r.TryGet("slot", out var s) ? s.GetInt32() : -1, r.GetProperty("expires_at").GetDouble(), r.GetProperty("caster").GetUInt32(), r.TryGet("stacks", out var stacks) ? stacks.GetInt32() : 1, r.TryGet("max_stack", out var max) && max.ValueKind != JsonValueKind.Null ? max.GetInt32() : null));
        }
        private BuffPayload ReadPayload(JsonElement r, JsonElement ids, string path, Entity owner)
        {
            var p = Identity(ids, path) is int id && Get<BuffPayload>(path, id) is { } old ? old : Store(path, ids, new BuffPayload());
            if (r.ValueKind != JsonValueKind.Object) return p;
            if (r.TryGet("rank", out var rank)) p.Rank = rank.GetInt32();
            if (r.TryGet("born", out var born)) p.Born = born.GetDouble();
            if (r.TryGet("next_shot", out var next)) p.NextShot = next.GetDouble();
            if (r.TryGet("movement_disabled", out var md)) p.MovementDisabled = md.GetDouble() != 0;
            if (r.TryGet("active", out var active)) p.Active = active.GetBoolean();
            if (r.TryGet("pos", out var pos)) p.Position = Point(pos);
            return p;
        }
        private YorickRevivalState ReadRevival(JsonElement r, JsonElement ids, string path)
        {
            var v = GetOrCreate(path, ids, () => new YorickRevivalState(r.TryGet("deadline", out var d) ? d.GetDouble() : 0)); return v;
        }

        private string Surface()
        {
            var s = new StringBuilder();
            foreach (var e in _world.Entities.Values.OrderBy(x => x.NetId))
            {
                s.Append(e.NetId).Append('|').Append(e.Kind).Append('|').Append(e.Team).Append('|').Append(e.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(e.Position.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(e.Alive).Append('|').Append(e.ExpiresAt.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(e.RespawnAt.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(e.GodMode).Append('|').Append(e.CooldownsDisabled).Append('|').Append(e.ForcedMove is null ? 0 : RuntimeHelpers.GetHashCode(e.ForcedMove)).Append('|').Append(e.ForcedMove?.Remaining.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(e.ForcedMove?.Completed);
                foreach (var b in e.Buffs) s.Append(';').Append(b.Name).Append(':').Append(b.ExpiresAt.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(RuntimeHelpers.GetHashCode(b));
                s.AppendLine();
            }
            return s.ToString();
        }

        private static void CompareRecords(JsonElement call, IReadOnlyList<LifecycleViolation> actual, int n)
        {
            var expected = call.GetProperty("records");
            TestAssert.Equal(expected.GetArrayLength(), actual.Count,
                $"lifecycle record count at call {n}; expected=[{string.Join(',', expected.EnumerateArray().Select(x => x.GetProperty("component").GetString()))}] actual=[{string.Join(',', actual.Select(x => x.Component))}]");
            for (var i = 0; i < actual.Count; i++)
            {
                var r = expected[i]; var a = actual[i];
                TestAssert.Equal(r.GetProperty("hero").GetString(), a.Hero, $"call {n} record {i} hero");
                TestAssert.Equal(TeamOf(r.GetProperty("team").GetString()), a.Team, $"call {n} record {i} team");
                TestAssert.Equal(r.GetProperty("netid").GetUInt32(), a.NetId, $"call {n} record {i} netid");
                TestAssert.Equal(r.GetProperty("component").GetString(), a.Component, $"call {n} record {i} component");
                TestAssert.Near(r.GetProperty("timer").GetDouble(), a.Timer, 1e-8, $"call {n} record {i} timer");
                var p = r.GetProperty("position"); TestAssert.Near(p[0].GetDouble(), a.Position.X, 1e-8, $"call {n} record {i} x"); TestAssert.Near(p[1].GetDouble(), a.Position.Z, 1e-8, $"call {n} record {i} z");
                TestAssert.Equal(r.GetProperty("god_mode").GetBoolean(), a.GodMode, $"call {n} record {i} god");
                TestAssert.Equal(r.GetProperty("cooldowns_disabled").GetBoolean(), a.CooldownsDisabled, $"call {n} record {i} cooldown");
                TestAssert.Equal(r.GetProperty("detail").GetString(), a.Detail, $"call {n} record {i} detail");
            }
        }
        private static void CompareError(JsonElement call, Exception? error, int n)
        {
            var expected = call.TryGet("error", out var raw); TestAssert.Equal(expected, error is not null, $"lifecycle error presence at call {n}");
            if (!expected || error is null) return;
            TestAssert.True(error is InvalidOperationException, $"call {n} error type");
            var component = call.GetProperty("records").GetArrayLength() > 0 ? call.GetProperty("records")[0].GetProperty("component").GetString()! : "lifecycle";
            TestAssert.True(error.Message.Contains(component, StringComparison.Ordinal), $"call {n} error component");
        }
        private T Store<T>(string path, JsonElement ids, T value) where T : class { if (Identity(ids, path) is int id) _objects[path + ":" + id] = value; return value; }
        private T GetOrCreate<T>(string path, JsonElement ids, Func<T> factory) where T : class { if (Identity(ids, path) is int id && _objects.TryGetValue(path + ":" + id, out var x)) return (T)x; return Store(path, ids, factory()); }
        private T? Get<T>(string path, int id) where T : class => _objects.TryGetValue(path + ":" + id, out var x) ? x as T : null;
        private static int? Identity(JsonElement ids, string path) => ids.TryGet(path, out var x) ? x.GetInt32() : null;
        private static Team TeamOf(string? x) => x?.ToUpperInvariant() switch { "ORDER" => Team.Order, "CHAOS" => Team.Chaos, "NEUTRAL" => Team.Neutral, _ => throw new InvalidDataException(x) };
        private static Position2 Point(JsonElement x) => new(x[0].GetDouble(), x[1].GetDouble());
        private static Position2 PointOrDefault(JsonElement x, string name) => x.TryGet(name, out var p) && p.ValueKind != JsonValueKind.Null ? Point(p) : default;
        private static SpellCastInfo EmptyCastInfo => new(0, 0, 1, 0, 0, default, default, Array.Empty<SpellCastTarget>(), 0, 0, 0, default);
    }

    private sealed class InvariantWaveSink : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => true;
        public bool TryGetClientPosition(Entity minion, out Position2 position) { position = minion.Position; return true; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}

internal static class LifecycleJsonExtensions
{
    public static bool TryGet(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default; return false;
    }
}
