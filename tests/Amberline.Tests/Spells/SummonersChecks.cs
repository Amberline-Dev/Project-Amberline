using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Spells.Bb;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

public static class SummonersChecks
{


    private static void Replay(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString() ?? "summoner";
        var runtime = ReplayRuntime.Create(testCase.GetProperty("initial").GetProperty("entities"));
        var world = runtime.World;
        var queue = runtime.Queue;
        var lifecycle = runtime.Lifecycle;
        var spells = runtime.Spells;
        var heroId = testCase.GetProperty("hero").GetUInt32();
        var hero = world.Get(heroId) ?? throw new InvalidDataException($"{name}: missing hero");

        var accepted = spells.HandleCastRequest(hero,
            Convert.FromHexString(testCase.GetProperty("request").GetString()!), 0.0);
        if (IsIgniteDisabledAcceptanceCase(name)) TestAssert.True(accepted, name + " disabled Ignite admission");
        if (FixtureRecorder.Enabled) SpellReplayRecording.Fields(testCase, new JsonObject { ["accepted"] = accepted });
        else
        TestAssert.Equal(testCase.GetProperty("accepted").GetBoolean(), accepted, name + " accepted");
        var afterCast = testCase.GetProperty("afterCast");
        RecordState(afterCast, world, spells);
        if (!FixtureRecorder.Enabled) CompareState(afterCast.GetProperty("entities"), world, name + " after cast");
        ComparePackets(afterCast.GetProperty("packets"), queue.Packets,
            name + " after cast");
        queue.Clear();

        foreach (var frame in testCase.GetProperty("frames").EnumerateArray())
        {
            ApplyActions(frame.GetProperty("actions"), testCase, world, lifecycle, spells);
            var dt = frame.GetProperty("dt").GetDouble();
            world.Clock.Advance(dt);
            spells.Tick(dt);
            foreach (var unit in world.Entities.Values.ToArray()) lifecycle.TickBuffs(unit, dt);
            var expectedFrame = frame;
            RecordState(expectedFrame, world, spells);
            if (!FixtureRecorder.Enabled) CompareState(expectedFrame.GetProperty("entities"), world,
                $"{name} frame {frame.GetProperty("frame").GetInt32()}");
            ComparePackets(expectedFrame.GetProperty("packets"), queue.Packets,
                $"{name} frame {frame.GetProperty("frame").GetInt32()}");
            queue.Clear();
        }
        TestAssert.Equal(0, runtime.Module.ScriptFailures.Count + runtime.SummonerModule.ScriptFailures.Count,
            name + " script failures: " +
            string.Join("; ", runtime.Module.ScriptFailures.Concat(runtime.SummonerModule.ScriptFailures)
                .Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));
    }


    private static void RecordState(JsonElement snapshot, WorldState world, SpellSystem spells)
    {
        if (!FixtureRecorder.Enabled) return;
        SpellReplayRecording.Fields(snapshot, new JsonObject
        {
            ["entities"] = SpellReplayRecording.Entities(snapshot.GetProperty("entities"), world),
            ["pending"] = SpellReplayRecording.Pending(spells, compact: true),
            ["markers"] = SpellReplayRecording.Markers(spells),
        });
    }

    private static bool IsIgniteDisabledAcceptanceCase(string name) => name is
        "ignite-0-6-stun-(7500.0, 6500.0)" or
        "ignite-0-6-silence-(7500.0, 6500.0)" or
        "ignite-0-6-snare-(7500.0, 6500.0)" or
        "ignite-0-6-taunt-(7500.0, 6500.0)" or
        "ignite-0-6-fear-(7500.0, 6500.0)" or
        "ignite-0-6-invulnerability-(7500.0, 6500.0)" or
        "ignite-0-6-action-payload-(7500.0, 6500.0)" or
        "ignite-0-6-pending-summon-(7500.0, 6500.0)";

    private static void ApplyActions(JsonElement actions, JsonElement testCase,
        WorldState world, BuffLifecycle lifecycle, SpellSystem spells)
    {
        foreach (var action in actions.EnumerateArray())
        {
            switch (action.GetProperty("kind").GetString())
            {
                case "dead":
                    var dead = world.Get(action.GetProperty("netid").GetUInt32())!;
                    dead.Alive = false;
                    dead.Health = 0.0;
                    break;
                case "position":
                    world.Get(action.GetProperty("netid").GetUInt32())!.Position = Point(action.GetProperty("value"));
                    break;
                case "cancelRecall":
                    spells.CancelRecall(world.Get(testCase.GetProperty("hero").GetUInt32())!);
                    break;
                case "buff":
                    var hero = world.Get(testCase.GetProperty("hero").GetUInt32())!;
                    lifecycle.AddBuff(hero, action.GetProperty("name").GetString()!,
                        (BuffType)action.GetProperty("type").GetInt32(),
                        action.GetProperty("duration").GetDouble(), hero.NetId);
                    break;
                case "cast":
                    var caster = world.Get(testCase.GetProperty("hero").GetUInt32())!;
                    spells.HandleCastRequest(caster,
                        Convert.FromHexString(action.GetProperty("request").GetString()!),
                        world.Clock.Now());
                    break;
                default: throw new InvalidDataException($"unknown summoner action {action.GetProperty("kind")}");
            }
        }
    }

    private static void CompareState(JsonElement expected, WorldState world, string label)
    {
        foreach (var raw in expected.EnumerateArray())
        {
            var entity = world.Get(raw.GetProperty("netid").GetUInt32())
                ?? throw new InvalidOperationException($"{label}: missing entity");
            Near(raw.GetProperty("position")[0].GetDouble(), entity.Position.X, label + " position x");
            Near(raw.GetProperty("position")[1].GetDouble(), entity.Position.Z, label + " position z");
            Near(raw.GetProperty("hp").GetDouble(), entity.Health, label + " hp");
            Near(raw.GetProperty("max_hp").GetDouble(), entity.MaxHealth, label + " max hp");
            Near(raw.GetProperty("mana").GetDouble(), entity.Mana, label + " mana");
            Near(raw.GetProperty("max_mana").GetDouble(), entity.MaxMana, label + " max mana");
            TestAssert.Equal(raw.GetProperty("alive").GetBoolean(), entity.Alive, label + " alive");
            TestAssert.Equal(raw.GetProperty("created").GetBoolean(), entity.Created, label + " created");
            TestAssert.Equal(raw.GetProperty("visible").GetBoolean(), entity.Visible, label + " visible");
            TestAssert.Equal(raw.GetProperty("teleport_id").GetByte(), entity.TeleportId, label + " teleport id");
            Near(raw.GetProperty("last_anchor_time").GetDouble(), entity.LastAnchorTime, label + " anchor time");
            ComparePoints(raw.GetProperty("anchor_path"), entity.AnchorPath, label + " anchor path");
            ComparePoints(raw.GetProperty("waypoints"), entity.Waypoints, label + " waypoints");
            var target = raw.GetProperty("target");
            if (target.ValueKind == JsonValueKind.Null) TestAssert.True(entity.Target is null, label + " target");
            else ComparePoint(target, entity.Target ?? new Position2(double.NaN, double.NaN), label + " target");
            var attackTarget = raw.GetProperty("attack_target");
            TestAssert.Equal(attackTarget.ValueKind == JsonValueKind.Null ? (uint?)null : attackTarget.GetUInt32(),
                entity.AttackTarget, label + " attack target");
            var cooldowns = raw.GetProperty("summoner_cooldowns");
            for (var i = 0; i < cooldowns.GetArrayLength(); i++)
                Near(cooldowns[i].GetDouble(), entity.SummonerCooldowns[i], label + $" cooldown {i}");
            var spellCooldowns = raw.GetProperty("spell_cooldowns");
            for (var i = 0; i < spellCooldowns.GetArrayLength(); i++)
                Near(spellCooldowns[i].GetDouble(), entity.SpellCooldowns[i], label + $" spell cooldown {i}");
            Near(raw.GetProperty("gold").GetDouble(), entity.Gold, label + " gold");
            Near(raw.GetProperty("exp").GetDouble(), entity.Experience, label + " exp");
            CompareBuffs(raw.GetProperty("buffs"), entity.Buffs, label + " buffs");
        }
    }

    private static void CompareBuffs(JsonElement expected, IList<BuffState> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var i = 0; i < actual.Count; i++)
        {
            TestAssert.Equal(expected[i].GetProperty("name").GetString(), actual[i].Name, label + $" {i} name");
            TestAssert.Equal((BuffType)expected[i].GetProperty("type").GetInt32(), actual[i].Type, label + $" {i} type");
            TestAssert.Equal(expected[i].GetProperty("slot").GetInt32(), actual[i].Slot, label + $" {i} slot");
            TestAssert.Equal(expected[i].GetProperty("caster").GetUInt32(), actual[i].CasterNetId, label + $" {i} caster");
            Near(expected[i].GetProperty("expires_at").GetDouble(), actual[i].ExpiresAt, label + $" {i} expiry");
            if (expected[i].TryGetProperty("stacks", out var stacks))
                TestAssert.Equal(stacks.GetInt32(), actual[i].Stacks, label + $" {i} stacks");
            if (expected[i].TryGetProperty("max_stack", out var max) && max.ValueKind != JsonValueKind.Null)
                TestAssert.Equal(max.GetInt32(), actual[i].MaxStack ?? -1, label + $" {i} max stack");
            ComparePayload(expected[i].GetProperty("payload"), actual[i].Payload, label + $" {i} payload");
        }
    }

    private static void ComparePayload(JsonElement expected, BuffPayload actual, string label)
    {
        foreach (var field in expected.EnumerateObject())
        {
            switch (field.Name)
            {
                case "period": Near(field.Value.GetDouble(), actual.Period, label + " period"); break;
                case "period_elapsed": Near(field.Value.GetDouble(), actual.PeriodElapsed, label + " period elapsed"); break;
                case "damage_per_tick": Near(field.Value.GetDouble(), actual.DamagePerTick, label + " damage per tick"); break;
                case "damage_shield": Near(field.Value.GetDouble(), actual.DamageShield, label + " damage shield"); break;
                case "magic_shield": Near(field.Value.GetDouble(), actual.MagicShield, label + " magic shield"); break;
                case "hp_regen_mult": Near(field.Value.GetDouble(), actual.HpRegenMult, label + " hp regen"); break;
                case "life_steal_mult": Near(field.Value.GetDouble(), actual.LifeStealMult, label + " life steal"); break;
                case "spell_vamp_mult": Near(field.Value.GetDouble(), actual.SpellVampMult, label + " spell vamp"); break;
                case "healing_received_mult": Near(field.Value.GetDouble(), actual.HealingReceivedMult, label + " healing received"); break;
                case "armor": Near(field.Value.GetDouble(), actual.Armor, label + " armor"); break;
                case "spell_block": Near(field.Value.GetDouble(), actual.SpellBlock, label + " spell block"); break;
                case "reflect": Near(field.Value.GetDouble(), actual.Reflect, label + " reflect"); break;
                case "fx_netid": TestAssert.Equal(field.Value.GetUInt32(), actual.FxNetId, label + " fx"); break;
                case "max_ticks": TestAssert.Equal(field.Value.GetInt32(), actual.MaxTicks, label + " max ticks"); break;
                case "ticks_done": TestAssert.Equal(field.Value.GetInt32(), actual.TicksDone, label + " ticks done"); break;
                case "action_disabled": TestAssert.Equal(field.Value.GetBoolean(), actual.ActionDisabled, label + " action disabled"); break;
                case "movement_disabled": TestAssert.Equal(field.Value.GetBoolean(), actual.MovementDisabled, label + " movement disabled"); break;
                case "spell_shield": TestAssert.Equal(field.Value.GetBoolean(), actual.MagicShield > 0.0, label + " spell shield"); break;
                case "damage_type": TestAssert.Equal(field.Value.GetString(), actual.DamageType.ToString().ToLowerInvariant(), label + " damage type"); break;
                default:
                    var propertyName = string.Concat(field.Name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
                    var property = typeof(BuffPayload).GetProperty(propertyName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)
                        ?? throw new InvalidDataException($"unhandled summoner payload field {field.Name}");
                    var value = property.GetValue(actual);
                    if (field.Value.ValueKind == JsonValueKind.Null) TestAssert.True(value is null, label + " " + field.Name);
                    else if (value is bool flag) TestAssert.Equal(field.Value.GetBoolean(), flag, label + " " + field.Name);
                    else if (value is string text) TestAssert.Equal(field.Value.GetString(), text, label + " " + field.Name);
                    else if (field.Value.ValueKind == JsonValueKind.Number && value is not null)
                        Near(field.Value.GetDouble(), Convert.ToDouble(value, CultureInfo.InvariantCulture), label + " " + field.Name);
                    else throw new InvalidDataException($"unhandled summoner payload type {field.Name}");
                    break;
            }
        }
    }

    private static void ComparePackets(JsonElement expected, IReadOnlyList<PlaintextPacket> actual, string label)
    {
        if (FixtureRecorder.Enabled) { SpellReplayRecording.Packets(expected, actual); return; }
        var cipher = LegacyBlowfish.CreateDefault();
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " packet count");
        for (var i = 0; i < actual.Count; i++)
        {
            var raw = expected[i];
            var wire = actual[i].ToWireBytes(cipher);
            TestAssert.Equal(raw.GetProperty("channel").GetByte(), actual[i].Channel, label + " channel");
            TestAssert.Equal(raw.GetProperty("flags").GetByte(), (byte)actual[i].Reliability, label + " flags");
            TestAssert.Equal(Convert.FromHexString(raw.GetProperty("wire").GetString()!), wire, label + " wire");
            TestAssert.Equal(Convert.FromHexString(raw.GetProperty("plaintext").GetString()!), cipher.Decrypt(wire), label + " plaintext");
        }
    }

    private static Entity ReadEntity(JsonElement raw)
    {
        var e = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unitType").GetInt32(), (Team)raw.GetProperty("team").GetInt32(),
            Point(raw.GetProperty("position")))
        {
            Health = raw.GetProperty("hp").GetDouble(), MaxHealth = raw.GetProperty("max_hp").GetDouble(),
            Mana = raw.GetProperty("mana").GetDouble(), MaxMana = raw.GetProperty("max_mana").GetDouble(),
            Armor = raw.GetProperty("armor").GetDouble(), SpellBlock = raw.GetProperty("spell_block").GetDouble(),
            AttackDamage = raw.GetProperty("atk_damage").GetDouble(), AbilityPower = raw.GetProperty("ability_power").GetDouble(),
            Speed = raw.GetProperty("speed").GetDouble(), Radius = raw.GetProperty("radius").GetDouble(),
            AttackRange = raw.GetProperty("atk_range").GetDouble(), AttackPeriod = raw.GetProperty("atk_period").GetDouble(),
            AttackWindup = raw.GetProperty("windup").GetDouble(), AttackSpeedMod = raw.GetProperty("attack_speed_mod").GetDouble(),
            MissileSpeed = raw.GetProperty("missile_speed").GetDouble(), AttackCooldown = raw.GetProperty("attack_cd").GetDouble(),
            Level = raw.GetProperty("level").GetInt32(), SkillPoints = raw.GetProperty("skill_points").GetInt32(),
            Alive = raw.GetProperty("alive").GetBoolean(),
            Created = raw.GetProperty("created").GetBoolean(), Visible = raw.GetProperty("visible").GetBoolean(),
            OwnerNetId = raw.GetProperty("owner_netid").GetUInt32(), ExpiresAt = SpellReplayRecording.Number(raw.GetProperty("expires_at")),
            Gold = raw.GetProperty("gold").GetDouble(), Experience = raw.GetProperty("exp").GetDouble(),
            RespawnAt = raw.GetProperty("respawn_at").GetDouble(), CooldownMod = raw.GetProperty("cooldown_mod").GetDouble(),
            HealthRegen = raw.GetProperty("hp_regen").GetDouble(), ManaRegen = raw.GetProperty("mp_regen").GetDouble(),
            ChampionName = raw.GetProperty("champion").GetString() ?? "Annie", Name = raw.GetProperty("name").GetString() ?? "",
            Facing = Point(raw.GetProperty("facing")), TeleportId = raw.GetProperty("teleport_id").GetByte(),
            GodMode = raw.GetProperty("god_mode").GetBoolean(),
            CooldownsDisabled = raw.GetProperty("cooldowns_disabled").GetBoolean(),
            IsWard = raw.GetProperty("is_ward").GetBoolean(),
            LastAnchorTime = raw.GetProperty("last_anchor_time").GetDouble(),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            e.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        e.Waypoints = raw.GetProperty("waypoints").EnumerateArray().Select(Point).ToList();
        e.AnchorPath = raw.GetProperty("anchor_path").EnumerateArray().Select(Point).ToList();
        var target = raw.GetProperty("target");
        if (target.ValueKind != JsonValueKind.Null) e.Target = Point(target);
        LoadBuffs(e, raw.GetProperty("buffs"));
        e.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray().Select(value => value.GetInt32()).ToList();
        e.SummonerCooldowns = raw.GetProperty("summoner_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        e.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        e.ItemCooldowns = raw.GetProperty("item_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        return e;
    }

    private static void LoadBuffs(Entity entity, JsonElement raw)
    {
        foreach (var value in raw.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (value.TryGetProperty("payload", out var fields))
            {
                if (fields.TryGetProperty("action_disabled", out var actionDisabled)) payload.ActionDisabled = actionDisabled.GetBoolean();
                if (fields.TryGetProperty("movement_disabled", out var movementDisabled)) payload.MovementDisabled = movementDisabled.GetBoolean();
                if (fields.TryGetProperty("spell_shield", out var spellShield) && spellShield.GetBoolean()) payload.MagicShield = 1.0;
                if (fields.TryGetProperty("damage_shield", out var damageShield)) payload.DamageShield = damageShield.GetDouble();
                if (fields.TryGetProperty("magic_shield", out var magicShield)) payload.MagicShield = magicShield.GetDouble();
                if (fields.TryGetProperty("period", out var period)) payload.Period = period.GetDouble();
                if (fields.TryGetProperty("period_elapsed", out var periodElapsed)) payload.PeriodElapsed = periodElapsed.GetDouble();
                if (fields.TryGetProperty("damage_per_tick", out var damagePerTick)) payload.DamagePerTick = damagePerTick.GetDouble();
                if (fields.TryGetProperty("damage_type", out var damageType)) payload.DamageType = Enum.Parse<DamageType>(damageType.GetString()!, true);
                if (fields.TryGetProperty("max_ticks", out var maxTicks)) payload.MaxTicks = maxTicks.GetInt32();
                if (fields.TryGetProperty("ticks_done", out var ticksDone)) payload.TicksDone = ticksDone.GetInt32();
                if (fields.TryGetProperty("hp_regen_mult", out var hpRegen)) payload.HpRegenMult = hpRegen.GetDouble();
                if (fields.TryGetProperty("life_steal_mult", out var lifeSteal)) payload.LifeStealMult = lifeSteal.GetDouble();
                if (fields.TryGetProperty("spell_vamp_mult", out var spellVamp)) payload.SpellVampMult = spellVamp.GetDouble();
                if (fields.TryGetProperty("healing_received_mult", out var healing)) payload.HealingReceivedMult = healing.GetDouble();
                if (fields.TryGetProperty("armor", out var armor)) payload.Armor = armor.GetDouble();
                if (fields.TryGetProperty("spell_block", out var spellBlock)) payload.SpellBlock = spellBlock.GetDouble();
                if (fields.TryGetProperty("reflect", out var reflect)) payload.Reflect = reflect.GetDouble();
                if (fields.TryGetProperty("fx_netid", out var fx)) payload.FxNetId = fx.GetUInt32();
            }
            entity.Buffs.Add(new BuffState(value.GetProperty("name").GetString()!, payload,
                (BuffType)value.GetProperty("type").GetInt32(), value.GetProperty("slot").GetInt32(),
                value.GetProperty("expires_at").GetDouble(), value.GetProperty("caster").GetUInt32(),
                value.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1,
                value.TryGetProperty("max_stack", out var max) && max.ValueKind != JsonValueKind.Null ? max.GetInt32() : null));
        }
    }

    private static Position2 Point(JsonElement value) => value.GetArrayLength() == 3
        ? new Position2(value[0].GetDouble(), value[2].GetDouble())
        : new Position2(value[0].GetDouble(), value[1].GetDouble());
    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var i = 0; i < actual.Count; i++) ComparePoint(expected[i], actual[i], label + $" {i}");
    }
    private static void ComparePoint(JsonElement expected, Position2 actual, string label)
    {
        Near(expected[0].GetDouble(), actual.X, label + " x"); Near(expected[1].GetDouble(), actual.Z, label + " z");
    }
    private static void Near(double expected, double actual, string label) =>
        TestAssert.Near(expected, actual, 1e-6, label + $" expected {expected:R} actual {actual:R}");
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class ReplayRuntime(WorldState world, ReplayQueue queue,
        BuffLifecycle lifecycle, SpellSystem spells, BbChampionModule module, BbSummonerModule summoner)
    {
        public BbChampionModule Module { get; } = module;
        public BbSummonerModule SummonerModule { get; } = summoner;
        public WorldState World { get; } = world;
        public ReplayQueue Queue { get; } = queue;
        public BuffLifecycle Lifecycle { get; } = lifecycle;
        public SpellSystem Spells { get; } = spells;

        public static ReplayRuntime Create(JsonElement entities)
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0.0);
            foreach (var raw in entities.EnumerateArray())
                world.Register(ReadEntity(raw));

            var grid = NavigationGrid.LoadDefault();
            var movement = new MovementSystem(world, grid);
            var queue = new ReplayQueue();
            var syncIds = new ReplicationSyncCounter();
            var data = GameData.Default;
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
                new TibbersChecks.StatsHooks(), new ProtocolHeroStatsTransport(queue, syncIds));
            var hooks = new TibbersChecks.CombatHooks(world);
            var transport = new ProtocolCombatTransport(queue, stats,
                unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
                syncIds);
            var combat = new CombatService(world, hooks, transport,
                new TibbersChecks.FixedRandomSource(0.5), stats);
            var effects = new ProtocolSpellEffectSink(queue, syncIds,
                new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate,
                unit =>
                {
                    if (unit.UnitType == UnitType.Hero) stats.Push(unit);
                    else queue.Enqueue(CombatPackets.MinionStats(unit.NetId,
                        new MinionStatsSnapshot((float)unit.Health, (float)unit.AttackDamage,
                            (float)unit.Speed, (float)unit.Armor, (float)unit.SpellBlock,
                            (float)unit.AttackSpeedMod), syncIds.Next(unit.NetId)));
                });
            var buffEffects = new BuffEffectService(effects,
                new TibbersChecks.VisionSink(world.Get, world.Allocate),
                _ => world.Clock.SyncId());
            // Batch 142 part 2: the summoner buffs tick and deactivate through their own module's router, which the
            // harness wires in here (ProductionMatchComposition does the same on MatchGameplayHooks.SummonerHooks).
            var summonerBuffs = new SummonerBuffHooks();
            var lifecycle = new BuffLifecycle(world, stats,
                new TibbersChecks.BuffServices(hooks, buffEffects, combat, summonerBuffs));
            hooks.Attach(lifecycle, buffEffects);
            // 19 October 2026: Annie runs from her shipped scripts, so the module must be bound to the match (an unbound module refuses every cast, and the
            // recorded "new cast" case would silently lose its cast) and her heroes activated, as ProductionMatchComposition does at hero creation.
            var module = new BbChampionModule("Annie", data);
            var spells = new SpellSystem(world, movement,
                new ReplayCombat(combat, hooks), effects, new ReplayBuffs(lifecycle),
                new ReplayHooks(), new ChampionSpellDispatcher([module]), data);
            module.Bind(spells);
            // Batch 142 part 2: the summoner spells run their shipped Summoner*.luaobj here too — the corpus meets the
            // script path the way production runs it. The loadout hooks make the (hero, summoner slot) owner.
            var loadout = new BbLoadoutHooks(() => lifecycle);
            var summoner = new BbSummonerModule(data);
            spells.LoadoutHooks = loadout;
            spells.SummonerScripts = summoner;
            summonerBuffs.Attach(summoner.Bind(spells, loadout), spells);
            queue.Clear();
            return new ReplayRuntime(world, queue, lifecycle, spells, module, summoner);
        }
    }

    /// <summary>
    /// The buff callbacks the summoner module's own router needs in this harness. The champion modules' stay closed:
    /// Annie is bound only so <see cref="SpellSystem"/> accepts her casts, and the corpus fixtures were recorded with
    /// her buffs silent.
    /// </summary>
    private sealed class SummonerBuffHooks : IChampionBuffHooks
    {
        private IBbHookSink? _sink;
        private SpellSystem? _spells;

        public void Attach(IBbHookSink sink, SpellSystem spells)
        {
            _sink = sink;
            _spells = spells;
        }

        public void OnBuffTick(Entity unit, BuffState buff, double dt) => _sink?.OnBuffTick(unit, buff, dt);
        public void OnBuffRemoved(Entity unit, BuffState buff) => _spells?.OnSummonerBuffRemoved(unit, buff);
        public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
    }

    private sealed class ReplayCombat(CombatService service, ICombatHooks hooks) : ISpellCombatService
    {
        public bool IsTargetable(Entity target, Entity? observer) => hooks.IsTargetable(target, observer);
        public double HealingMultiplier(Entity target) => service.HealingReceivedMultiplier(target);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? sourceKind, double vampScale = 1.0) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind, vampScale: vampScale);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? sourceKind, double vampScale, bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind, isReflect: isReflect, sourceIsPeriodic: sourceIsPeriodic, spellOwnerNetId: spellOwnerNetId, vampScale: vampScale);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                recapOrigin: recapOrigin, bypassSpellShield: bypassSpellShield);
        public bool ConsumeSpellShield(Entity source, Entity target) => hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell);
        public bool ConsumeSpellShield(Entity source, Entity target, bool force) => hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell, force);
        public void InterruptBasicAttack(Entity attacker) => service.InterruptBasicAttack(attacker);
        public void Kill(Entity unit, uint killerNetId) => service.Kill(unit, killerNetId);
    }
    private sealed class ReplayBuffs(BuffLifecycle lifecycle) : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) => lifecycle.ScriptStatusGained(unit, wasHardControlled, wasRooted);
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) => lifecycle.ScriptStatusLost(unit, couldMove, couldAttack);
        public BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null) =>
            lifecycle.AddOverlappingBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration, maxStack);
        public BuffState? Add(Entity unit, string name, BuffType type, double duration, uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true) => lifecycle.AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration);
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) => lifecycle.AddStackingBuff(unit, name,
                type, duration, casterNetId, maxStacks, perStackPayload, replaceExistingPayload,
                canMitigateDuration: canMitigateDuration);
        public bool Remove(Entity unit, int slot) => lifecycle.RemoveBuff(unit, slot);
        public bool Has(Entity unit, string name) => lifecycle.HasBuff(unit, name);
        public void Tick(Entity unit, double dt) => lifecycle.TickBuffs(unit, dt);
        public void RefreshMovementSpeed(Entity unit, bool push = true) => lifecycle.RefreshMovementSpeed(unit, push);
        // Production's boundary is BuffLifecycleAdapter (MatchRuntime); a script buff's BuffOnUpdateStats resample
        // goes through it. Without this arm the corpus replay raised a NotSupportedException inside SummonerDot's
        // own hook instead of swapping the payload the way production does.
        public bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload) =>
            lifecycle.ReplaceScriptPayload(unit, buff, perStackPayload);
        public bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1) =>
            lifecycle.DropScriptStack(unit, buff, perStackPayload, count);
    }
    private sealed class ReplayHooks : IChampionSpellHooks
    {
        public void OnSpellCast(Entity hero, int slot, double now) { }
        public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target) { }
        public bool CastPyromania(Entity hero, bool damaging) => false;
        public void CancelChannel(Entity hero, string reason) { }
    }

    private sealed class ReplayQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId, Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
        public void Clear() => Packets.Clear();
    }
}
