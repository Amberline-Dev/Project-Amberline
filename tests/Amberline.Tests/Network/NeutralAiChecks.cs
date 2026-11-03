using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;
using Amberline.Network.Sessions;
using Amberline.Tests.Spells;

namespace Amberline.Tests.Network;

/// <summary>Replay checks for the typed ordinary-neutral and Worm boundaries.</summary>
public static partial class NeutralAiChecks
{


    [Check("neutral.b6-wraith-landed-hit-effect")]
    public static void WraithLandedHitEffect()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            random: new ConstantRandomSource(.99));
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var observerHero = runtime.Session.Participants[0].Hero!;
        observerHero.Position = new Position2(7000, 7000);
        observerHero.Created = true;
        observerHero.HeroAutoAcquire = false;
        var capture = new NeutralEffectCapture();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, observerHero.Team,
            observerHero.NetId, capture, ready: true));
        // Batch 159 part 2: the camp spawns under its own character scripts, which is what puts the landed
        // hit's EternalThirst_buf.troy on the wire (CharScriptWraith's CharOnHitUnit, after its x1.43).
        var scheduler = new JungleScheduler(runtime.World, GameData.Default, campIds: [3]);
        scheduler.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        scheduler.Start();
        scheduler.Tick(100);
        foreach (var name in new[] { "Wraith", "LesserWraith" })
        foreach (var outcome in new[] { "landed", "lethal", "miss", "dodge" })
        {
            var source = scheduler.Members.Values.First(member => member.Name == name).Entity;
            source.Position = observerHero.Position + new Position2(40, 0);
            source.Created = true;
            source.Health = source.MaxHealth - 100;
            source.HealthRegen = 0;
            source.AttackDamage = 100;
            source.AttackCooldown = source.AttackWindup = source.MissileSpeed = 0;
            source.Buffs.Clear();
            if (outcome == "miss") source.Buffs.Add(new BuffState("Blind", type: BuffType.Internal,
                payload: new BuffPayload { MissChance = 1 }));
            var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, observerHero.Team,
                source.Position + new Position2(40, 0))
                { Health = outcome == "lethal" ? 20 : 1000, MaxHealth = 1000,
                    Name = "Blue_Minion_Basic", MinionType = MinionType.Melee,
                    Created = true, Dodge = outcome == "dodge" ? 1 : 0 });
            runtime.Delivery.Flush();
            capture.Packets.Clear();
            var health = source.Health;
            TestAssert.True(runtime.Combat.QueueBasicAttack(source, target, new AttackRolls(.99, 0, 0)));
            runtime.Combat.CombatTick(.01);
            runtime.Delivery.Flush();
            var effects = capture.Packets.Where(packet => packet.Plaintext.Length == 73 &&
                packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(6, 4)) ==
                    SpellPackets.BuffNameHash("EternalThirst_buf")).ToArray();
            var landed = outcome is "landed" or "lethal";
            TestAssert.Equal(landed ? 1 : 0, effects.Length,
                $"JNG-B6-06: {name}/{outcome} produces exactly the landed-hit effect");
            if (landed)
            {
                TestAssert.Equal(target.NetId,
                    BinaryPrimitives.ReadUInt32LittleEndian(effects[0].Plaintext.Span.Slice(21, 4)));
                TestAssert.Equal(source.NetId,
                    BinaryPrimitives.ReadUInt32LittleEndian(effects[0].Plaintext.Span.Slice(29, 4)));
            }
            if (name == "LesserWraith") TestAssert.Near(health, source.Health, 0, "lesser wraith has no lifesteal");
            if (outcome == "lethal") TestAssert.False(target.Alive, "lethal-hit effect survives death");
        }
    }

    internal sealed class NeutralEffectCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }


    [Check("neutral.b6-baron-face-before-cast")]
    public static void BaronFaceBeforeCast()
    {
        // Backstab is Shaco's script now (BuffOnPreAttack adds FromBehind, BuffOnHitUnit multiplies the swing by 1.2), so the attacker is a
        // real Shaco whose script buffs the world creation attached, not an entity carrying his name.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, orderChampion: "Shaco");
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [12]);
        scheduler.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        scheduler.Start();
        scheduler.Tick(900);
        var worm = scheduler.Members.Values.Single().Entity;
        var queue = new CaptureQueue();
        var grid = runtime.Navigation;
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var sink = new ProtocolNeutralPacketSink(packet => queue.Enqueue(packet), runtime.SyncIds, world.Allocate,
            bounds, (x, z) => grid.HeightAt(new Position2(x, z)));
        var effects = new ProtocolSpellEffectSink(queue, runtime.SyncIds, bounds,
            (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate, _ => { });
        var ai = new NeutralAiService(world, scheduler, runtime.Movement, runtime.Combat,
            sink, effects, runtime.SpellBuffs, random: new ConstantRandomSource(.99));
        worm.Position = new Position2(7000, 7000);
        worm.NeutralHome = new Position3(7000, 0, 7000);
        var hero = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, worm.Position + new Position2(100, 0))
            { Health = 1000, MaxHealth = 1000, ChampionName = "Annie" });
        var shaco = world.Heroes().Single(unit => unit.Team == Team.Order && unit.ChampionName == "Shaco");
        TestAssert.True(shaco.Buffs.Any(buff => buff.Name == "Backstab"), "the world creation attached Shaco's Backstab");
        shaco.Position = worm.Position + new Position2(-200, 0);
        shaco.Facing = new Position2(1, 0);
        shaco.Health = shaco.MaxHealth = 1000;
        worm.Facing = new Position2(0, 1);
        runtime.Combat.ApplyHit(hero.NetId, worm.NetId, 1.0, DamageType.Physical, DamageSourceKind.Basic);
        AssertFacing(1);
        // Batch 174 part 2: this check pins the first engagement's turn. A second turn comes only from the Baron's ActionTimer chain, which needs
        // BBCanSeeTarget on the moved hero; that visibility question is open (see the part 2 record), so the second turn is not asserted here.

        void AssertFacing(double x)
        {
            TestAssert.Equal(new Position2(x, 0), worm.Facing, "JNG-B6-03: authoritative target heading");
            var packets = queue.Packets.Select(packet => packet.Plaintext.Span[0]).ToArray();
            // The swing as the combat service resolves it: the attack start (Backstab's FromBehind, judged from the heading now), then the hit.
            foreach (var behind in shaco.Buffs.Where(buff => buff.Name == "FromBehind").ToArray())
                runtime.SpellBuffs.Remove(shaco, behind.Slot);
            var hooks = runtime.Dependencies.CombatHooks;
            hooks.OnBasicAttackStart(shaco, worm);
            TestAssert.Near(x > 0 ? 120 : 100, hooks.ModifyBasicAttackDamage(shaco, worm, 100), 0,
                "JNG-B6-03: the production Backstab predicate follows Baron's new heading");
        }
    }


    [Check("neutral.wraith-basic-lifesteal")]
    public static void WraithBasicLifesteal()
    {
        var world = new WorldState();
        // Batch 159 part 2: the Wraith's shipped life-steal is its own character script's stats sample
        // (CharScriptWraith 0000-0044: BBIncStat IncPercentLifeStealMod 1.0, then its LifestealAttack buff).
        var characters = Amberline.Tests.Spells.UnitCharacterRig.Create(world,
            NavigationGrid.LoadDefault(GameDataPaths.Default, NavigationGridSettings.Default));
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [3, 9]);
        scheduler.BindUnitCharacter = (unit, character) => characters.Bind(unit, character);
        scheduler.Start();
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds);
        var hero = new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, new Position2(6750, 5350))
        {
            Health = 1000.0, MaxHealth = 1000.0, Armor = 100.0,
        };
        world.Register(hero);
        var queue = new CaptureQueue();
        var stats = new HeroStatsService(GameData.Default, new ItemCatalog(GameData.Default.Paths),
            new NeutralStatsHooks(), new NeutralStatsTransport());
        var combat = new CombatService(world, new NeutralHooks(),
            new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter()),
            new ConstantRandomSource(0.99));

        foreach (var member in scheduler.Members.Values)
        {
            var monster = member.Entity;
            var lifeSteal = member.Name == "Wraith" ? 1.0 : 0.0;
            TestAssert.Near(lifeSteal, monster.LifeSteal, 1e-9,
                "only the large Wraith spawns with shipped lifesteal in either camp");
            foreach (var (missingHealth, healingReduction, kind) in new[]
                     {
                         (100.0, 0.0, DamageSourceKind.Basic),
                         (10.0, 0.0, DamageSourceKind.Basic),
                         (100.0, -0.5, DamageSourceKind.Basic),
                         (100.0, 0.0, DamageSourceKind.Spell),
                     })
            {
                hero.Health = hero.MaxHealth;
                monster.Health = monster.MaxHealth - missingHealth;
                monster.SpellVamp = 1.0; // Non-hero spell vamp must remain excluded.
                monster.Buffs.Clear();
                monster.Buffs.Add(new BuffState("WraithHealingCheck", type: BuffType.Internal,
                    payload: new BuffPayload { HealingReceivedMult = healingReduction }));
                queue.Clear();
                var before = monster.Health;
                TestAssert.True(combat.ApplyHit(monster.NetId, hero.NetId, 100.0,
                    DamageType.Physical, kind), "wraith hit lands");
                TestAssert.Near(50.0, hero.MaxHealth - hero.Health, 1e-9,
                    "lifesteal uses damage after armor");
                var healed = kind == DamageSourceKind.Basic
                    ? Math.Min(missingHealth, 50.0 * lifeSteal * (1.0 + healingReduction)) : 0.0;
                TestAssert.Near(before + healed, monster.Health, 1e-9,
                    "basic lifesteal respects healing reduction and maximum HP; lesser and spell hits do not heal");
                var healthPackets = queue.Packets.Where(packet =>
                    packet.Plaintext.Length == 23 && packet.Plaintext.Span[0] == 0xCC &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(11, 4)) == monster.NetId)
                    .ToArray();
                TestAssert.Equal(healed > 0.0 ? 1 : 0, healthPackets.Length,
                    "only an effective heal replicates the attacker's HP");
                if (healed > 0.0)
                    TestAssert.Near(monster.Health,
                        BinaryPrimitives.ReadSingleLittleEndian(healthPackets[0].Plaintext.Span.Slice(19, 4)),
                        1e-5, "replicated neutral HP equals capped sustain health");
            }
        }
    }


    private static Position2 AdvanceToNaturalReset(NeutralHarness harness, Entity monster,
        bool requireIdle = true, Action<Position2>? onArrival = null)
    {
        for (var frame = 0; frame < 2000 &&
             (requireIdle ? monster.NeutralState != "idle" : monster.Waypoints.Count > 0); frame++)
        {
            harness.Movement.Tick(0.1);
            var beforeAi = monster.Position;
            var facingBeforeAi = monster.Facing;
            harness.Ai.Tick(0.1);
            if (requireIdle && monster.NeutralState == "idle")
            {
                TestAssert.Equal(beforeAi, monster.Position,
                    "home completion and resting facing do not move the camp");
                onArrival?.Invoke(facingBeforeAi);
                return beforeAi;
            }
        }

        if (requireIdle)
            TestAssert.Equal("idle", monster.NeutralState,
                "neutral naturally completes its saved-home reset");
        else
            TestAssert.Equal(0, monster.Waypoints.Count,
                "neutral reaches the saved home before the healing gate completes");
        return monster.Position;
    }

    private static double Distance(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static void Replay(JsonElement expected)
    {
        var series = "neutral:" + expected.GetProperty("variant").GetString() + ":" +
            expected.GetProperty("camp").GetInt32();
        // The neutral-ai fixture uses the same typed world adapter as the
        // Worm fixture; its case actions are intentionally replayed below.
        var harness = NeutralHarness.Create(expected.GetProperty("initial"),
            Array.Empty<JsonElement>(), expected.GetProperty("camp").GetInt32(), expected.GetProperty("spawnTime").GetDouble(),
            preserveSpawnSync: true);
        foreach (var frame in expected.GetProperty("frames").EnumerateArray())
        {
            harness.Queue.Clear();
            ApplyActions(harness, frame.GetProperty("actions"));
            var dt = frame.GetProperty("dt").GetDouble();
            harness.World.Clock.Advance(dt);
            harness.Movement.Tick(dt);
            harness.Ai.Tick(dt);
            harness.TickBuffs(dt);
            CompareFrame(frame, harness, series: series);
        }
    }

    /// <summary>
    /// A replay expectation. Record mode (AMBERLINE_RECORD_FIXTURES=1) writes what this build produces, so it does not assert the
    /// replay's expectation; the bookkeeping the condition carries (the Remove and Add calls) still runs in both modes. Batch 174b.
    /// </summary>
    private static void ReplayExpect(bool condition, string message)
    {
        if (!FixtureRecorder.Enabled) TestAssert.True(condition, message);
    }

    private static void ReplayEqual<T>(T expected, T actual, string message)
    {
        if (!FixtureRecorder.Enabled) TestAssert.Equal(expected, actual, message);
    }

    private static void ReplayNear(double expected, double actual, double tolerance, string message)
    {
        if (!FixtureRecorder.Enabled) TestAssert.Near(expected, actual, tolerance, message);
    }

    private static void ReplayWorm(JsonElement expected)
    {
        var name = expected.GetProperty("name").GetString()!;
        var randomCalls = expected.GetProperty("after").GetProperty("randomCalls").EnumerateArray()
            .Concat(expected.GetProperty("frames").EnumerateArray()
                .SelectMany(frame => frame.GetProperty("randomCalls").EnumerateArray()))
            .ToArray();
        var harness = NeutralHarness.Create(expected.GetProperty("initial"), randomCalls,
            12, 900.0, preserveSpawnSync: false);
        var expectedSweepProjections = ProjectWormSweepLandings(
            expected.GetProperty("initial"), harness.Movement);
        var worm = harness.Worm;
        var hero = harness.Hero;
        switch (name)
        {
            case "idle-hit":
            case "out-of-range":
                harness.Combat.ApplyHit(hero.NetId, worm.NetId, 100.0,
                    DamageType.True, DamageSourceKind.Spell);
                break;
            case "sweep":
                harness.TriggerWorm(worm, NeutralWormAction.Sweep);
                break;
            case "wrath":
                harness.TriggerWorm(worm, NeutralWormAction.Wrath);
                break;
            case "propel":
            case "dead-geyser":
                harness.TriggerWorm(worm, NeutralWormAction.Propel);
                break;
            case "growth-recovery":
                // The source starts this case immediately before its 60-second
                // growth edge; the replay frames provide the authoritative dt.
                worm.Health = worm.MaxHealth * 0.5;
                break;
            default:
                throw new InvalidOperationException("Unknown Worm case: " + name);
        }

        if (FixtureRecorder.Enabled) RecordNeutralState(expected.GetProperty("after"), harness);
        else
        {
        CompareEntities(expected.GetProperty("after").GetProperty("entities"), harness.World,
            name + " after");
        ComparePendingHits(expected.GetProperty("after").GetProperty("pendingHits"),
            harness.Combat.PendingHits, name + " after pending hits");
        CompareScriptState(expected.GetProperty("after").GetProperty("entities"), harness,
            name + " after script state");
        }
        var wormDashState = name is "sweep" or "propel" or "dead-geyser"
            ? new WormDashReplayState() : null;
        if (name is "propel" or "dead-geyser")
        {
            // Playtest B4 JNG-04: immutable initial target and recorded random roll.
            // Batch 194: PropelTimer.luaobj creates one ground warning (one BBSpellEffectCreate of PropelBubbles.troy).
            wormDashState!.WarningEffectsRemaining = 1;
            // Batch 208: the warning's bind is the geyser the script spawned (BBSpawnMinion, Other1). It is taken from the world at the
            // 'after' step, where the warning packets are produced and validated.
            // Batch 194 pinned the geyser point from the rig's fixed roll (4527.608, 10400.461); batch 284 moved it (GetRandomAreaPoint
            // draws the radius first), so the warning is not checked against that constant. PropelTimer.luaobj gives the spawn
            // (BBSpawnMinion) and the warning (BBSpellEffectCreate) the same PosVar, PropelPos, so the warning sits on the geyser's own
            // position. CompareEntities pins that position against the corpus ('propel after x').
            var geyser = harness.World.Entities.Values
                .FirstOrDefault(entity => entity.Placemarker && entity.Buffs.Any(buff => buff.HasName("PropelSpellCaster")));
            wormDashState.WarningGeyserNetId = geyser?.NetId;
            wormDashState.WarningPosition = geyser?.Position;
        }
        ComparePackets(expected.GetProperty("after").GetProperty("packets"), harness.Queue.Packets,
            name + " after", adaptCanCastBits1: true,
            wormHarness: wormDashState is null ? null : harness,
            wormDashState: wormDashState,
            expectedSweepProjections: expectedSweepProjections);
        // Batch 194: record mode writes the calls this build consumed (the expectation the replay asserts), so the RNG
        // rows follow the build; the replay consumes exactly those rows (the supply below is built from the same rows).
        if (FixtureRecorder.Enabled)
            CorpusSnapshot.Set(expected.GetProperty("after").GetProperty("randomCalls"), harness.Random.ConsumedRows());
        else
            harness.Random.AssertCalls(expected.GetProperty("after").GetProperty("randomCalls"),
                name + " after RNG");

        var sweepElapsed = 0.0;
        foreach (var frame in expected.GetProperty("frames").EnumerateArray())
        {
            harness.Queue.Clear();
            ApplyActions(harness, frame.GetProperty("actions"));
            var dt = frame.GetProperty("dt").GetDouble();
            harness.World.Clock.Advance(dt);
            harness.Movement.Tick(dt);
            harness.Ai.Tick(dt);
            // The source replay advances combat time with regeneration
            // disabled.  This still performs the real pending-hit, stat
            // refresh, and attack bookkeeping without HP/MP regeneration.
            harness.Combat.CombatTick(dt, regenerate: false);
            harness.TickBuffs(dt);
            sweepElapsed += dt;
            if (FixtureRecorder.Enabled && frame.TryGetProperty("randomCalls", out var frameCalls))
                CorpusSnapshot.Set(frameCalls, harness.Random.ConsumedRows());
            CompareFrame(frame, harness, adaptCanCastBits1: true, series: "worm:" + name,
                wormHarness: wormDashState is null ? null : harness,
                wormDashState: wormDashState,
                expectedSweepProjections: expectedSweepProjections,
                expectedSweepElapsed: expectedSweepProjections is null ? null : sweepElapsed);
        }
    }

    private static IReadOnlyDictionary<uint, SweepMotionProjection>? ProjectWormSweepLandings(
        JsonElement initial, MovementSystem movement)
    {
        // Batch 234 (batch 222 item 6): the rotation's SweepTimer branch is a Lua-stream roll, so a SweepingBlow dash
        // can come from any case, not only the scripted "sweep" case (this build sweeps the hero in idle-hit). Each
        // case's projection is taken from its own initial rows, which is the input rule, not the after-state.
        var rows = initial.EnumerateArray().ToArray();
        var wormRow = rows.Single(row => row.GetProperty("name").GetString() == "Worm");
        var wormPositionValue = wormRow.GetProperty("position");
        var wormPosition = new Position2(wormPositionValue[0].GetDouble(), wormPositionValue[1].GetDouble());
        // The sweep scenario starts with one hostile hero; eligibility is an
        // input rule, never inferred from a recorded after-state.
        return rows.Where(row => row.GetProperty("unitType").GetInt32() == 0)
            .ToDictionary(row => row.GetProperty("netid").GetUInt32(), row =>
            {
                var positionValue = row.GetProperty("position");
                var start = new Position2(positionValue[0].GetDouble(), positionValue[1].GetDouble());
                var dx = start.X - wormPosition.X;
                var dz = start.Z - wormPosition.Z;
                var length = Math.Sqrt(dx * dx + dz * dz);
                if (length < 1e-9) { dx = 1.0; dz = 0.0; length = 1.0; }
                var requested = new Position2(start.X + dx / length * 600.0,
                    start.Z + dz / length * 600.0);
                // Batch 194: Destination is the wall-cut landing the dash packet carries (FirstCollisionLanding). The forced move's
                // destination field is the raw 600-unit request (BbScriptMoves.Start keeps it for a FIRST_COLLISION_HIT move;
                // ForcedMoveState.FirstCollision stops the unit at the wall, x 5101, which CompareEntities pins), so Request is checked there.
                return new SweepMotionProjection(start,
                    movement.FirstCollisionLanding(start, requested, 1200.0, 0.5), 1200.0,
                    wormPosition, requested);
            });
    }

    private readonly record struct SweepMotionProjection(
        Position2 Start, Position2 Destination, double Speed, Position2 Source, Position2 Request);

    private static void ApplyActions(NeutralHarness harness, JsonElement actions)
    {
        foreach (var action in actions.EnumerateArray())
        {
            var kind = action.GetProperty("kind").GetString();
            var entityId = kind == "wake"
                ? action.GetProperty("monster").GetUInt32()
                : action.GetProperty("netid").GetUInt32();
            var entity = harness.World.Get(entityId)
                ?? throw new InvalidOperationException("action entity missing");
            switch (kind)
            {
                case "wake":
                    var attacker = harness.World.Get(action.GetProperty("attacker").GetUInt32())
                        ?? throw new InvalidOperationException("wake attacker missing");
                    harness.Ai.OnTakeDamage(entity, attacker);
                    break;
                case "position":
                    var position = action.GetProperty("value");
                    entity.Position = new Position2(position[0].GetDouble(), position[1].GetDouble());
                    break;
                case "dead":
                    entity.Alive = false;
                    entity.Health = 0.0;
                    break;
                case "retreat":
                    entity.NeutralState = "retreat";
                    break;
                case "level":
                    entity.Level = action.GetProperty("value").GetInt32();
                    break;
                case "buff":
                    harness.AddBuff(entity, action.GetProperty("name").GetString()!,
                        (BuffType)action.GetProperty("type").GetInt32(),
                        action.GetProperty("duration").GetDouble(),
                        action.GetProperty("caster").GetUInt32());
                    break;
                default:
                    throw new InvalidOperationException("Unknown neutral action: " + kind);
            }
        }
    }

    private static Dictionary<string, object?> SnapshotNeutral(Entity actual, JsonElement shape, NeutralHarness harness)
    {
        object? P(Position2? point) => point is { } p ? new[] { p.X, p.Z } : null;
        return shape.EnumerateObject().ToDictionary(field => field.Name, field => field.Name switch
        {
            "ability_power" => (object?)(actual.AbilityPower),
            "acquisition_range" => (object?)(actual.AcquisitionRange),
            "alive" => (object?)(actual.Alive),
            "anchor_path" => (object?)(actual.AnchorPath.Select(p => P(p)).ToArray()),
            "armor" => (object?)(actual.Armor),
            "armor_pen_flat" => (object?)(actual.ArmorPenetrationFlat),
            "armor_pen_percent" => (object?)(actual.ArmorPenetrationPercent),
            "atk_damage" => (object?)(actual.AttackDamage),
            "atk_period" => (object?)(actual.AttackPeriod),
            "atk_range" => (object?)(actual.AttackRange),
            "attack_speed_mod" => (object?)(actual.AttackSpeedMod),
            "attack_target" => (object?)(actual.AttackTarget),
            "buffs" => (object?)(actual.Buffs.Select(buff => SnapshotNeutralBuff(buff, shape.GetProperty("buffs"))).ToArray()),
            "camp_id" => (object?)(actual.CampId),
            "champion_name" => (object?)(actual.ChampionName),
            "cooldown_mod" => (object?)(actual.CooldownMod),
            "cooldowns_disabled" => (object?)(actual.CooldownsDisabled),
            "created" => (object?)(actual.Created),
            "crit_chance" => (object?)(actual.CritChance),
            "crit_multiplier" => (object?)(actual.CritMultiplier),
            "dodge" => (object?)(actual.Dodge),
            "exp" => (object?)(actual.Experience),
            "facing" => (object?)(P(actual.Facing)),
            "forced_move" => (object?)(actual.ForcedMove is { } move ? (object)new { destination = P(move.Destination), speed = move.Speed, remaining = move.Remaining, source_netid = move.SourceNetId, effect = move.Effect, first_collision = move.FirstCollision, completed = move.Completed } : new Dictionary<string, object?>()),
            "forced_move_grace" => (object?)(actual.ForcedMoveGrace),
            "global_gold_reward" => (object?)(actual.GlobalGoldReward),
            "god_mode" => (object?)(actual.GodMode),
            "gold" => (object?)(actual.Gold),
            "gold_reward" => (object?)(actual.GoldReward),
            "hp" => (object?)(actual.Health),
            "hp_regen" => (object?)(actual.HealthRegen),
            "inventory" => (object?)(actual.Inventory.Select(slot => slot is null ? null : (object)new { item_id = slot.ItemId, count = slot.Count, charges = slot.Charges }).ToArray()),
            "item_cooldowns" => (object?)(actual.ItemCooldowns),
            "kind" => (object?)(actual.Kind.ToString().ToLowerInvariant()),
            "last_anchor_time" => (object?)(actual.LastAnchorTime),
            "level" => (object?)(actual.Level),
            "life_steal" => (object?)(actual.LifeSteal),
            "magic_pen_flat" => (object?)(actual.MagicPenetrationFlat),
            "magic_pen_percent" => (object?)(actual.MagicPenetrationPercent),
            "mana" => (object?)(actual.Mana),
            "max_hp" => (object?)(actual.MaxHealth),
            "max_mana" => (object?)(actual.MaxMana),
            "missile_speed" => (object?)(actual.MissileSpeed),
            "mp_regen" => (object?)(actual.ManaRegen),
            "name" => (object?)(actual.Name),
            "netid" => (object?)(actual.NetId),
            "neutral_face_pos" => (object?)(actual.NeutralFacePosition is { } neutralfaceposition ? new[] { neutralfaceposition.X, neutralfaceposition.Y, neutralfaceposition.Z } : Array.Empty<double>()),
            "neutral_group_pos" => (object?)(actual.NeutralGroupPosition is { } neutralgroupposition ? new[] { neutralgroupposition.X, neutralgroupposition.Y, neutralgroupposition.Z } : Array.Empty<double>()),
            "neutral_home" => (object?)(actual.NeutralHome is { } neutralhome ? new[] { neutralhome.X, neutralhome.Y, neutralhome.Z } : Array.Empty<double>()),
            "neutral_last_move" => (object?)(P(actual.NeutralLastMove)),
            "neutral_leash_counter" => (object?)(actual.NeutralLeashCounter),
            "neutral_member" => (object?)(actual.NeutralMember),
            "neutral_player_level" => (object?)(actual.NeutralPlayerLevel),
            "neutral_regen_elapsed" => (object?)(actual.NeutralRegenElapsed),
            "neutral_retreat_elapsed" => (object?)(actual.NeutralRetreatElapsed),
            "neutral_scaling_elapsed" => (object?)(actual.NeutralScalingElapsed),
            "neutral_script_state" => (object?)(actual.Name == "Worm" ? SnapshotWorm(harness.WormState(actual), field.Value) : new Dictionary<string, object?>()),
            "neutral_state" => (object?)(actual.NeutralState),
            "position" => (object?)(P(actual.Position)),
            "radius" => (object?)(actual.Radius),
            "skill_points" => (object?)(actual.SkillPoints),
            "spawn_stats" => (object?)(field.Value.Clone()),
            "speed" => (object?)(actual.Speed),
            "spell_block" => (object?)(actual.SpellBlock),
            "spell_cooldowns" => (object?)(actual.SpellCooldowns),
            "spell_ranks" => (object?)(actual.SpellRanks),
            "spell_vamp" => (object?)(actual.SpellVamp),
            "summoner_cooldowns" => (object?)(actual.SummonerCooldowns),
            "target" => (object?)(P(actual.Target)),
            "team" => (object?)((int)actual.Team),
            "unitType" => (object?)((int)actual.UnitType),
            "visible" => (object?)(actual.Visible),
            "waypoints" => (object?)(actual.Waypoints.Select(p => P(p)).ToArray()),
            "windup" => (object?)(actual.AttackWindup),
            "xp_reward" => (object?)(actual.ExperienceReward),
            _ => throw new InvalidOperationException("Unknown neutral output: " + field.Name),
        });
    }

    private static Dictionary<string, object?> SnapshotNeutralBuff(BuffState buff, JsonElement shapes)
    {
        var shape = shapes.EnumerateArray().FirstOrDefault(row => row.GetProperty("name").GetString() == buff.Name);
        var payload = new Dictionary<string, object?>();
        if (buff.Payload.Has(BuffPayloadField.MaxHp)) payload["max_hp"] = buff.Payload.MaxHp;
        if (buff.Payload.Has(BuffPayloadField.AttackDamage)) payload["attack_damage"] = buff.Payload.AttackDamage;
        if (buff.Payload.Has(BuffPayloadField.SpellBlock)) payload["spell_block"] = buff.Payload.SpellBlock;
        if (buff.Payload.FxNetId != 0) payload["fx_netid"] = buff.Payload.FxNetId;
        if (buff.Payload.ActionDisabled) payload["action_disabled"] = true;
        var result = new Dictionary<string, object?>
        {
            ["name"] = buff.Name, ["slot"] = buff.Slot, ["type"] = (int)buff.Type,
            ["expires_at"] = buff.ExpiresAt, ["caster"] = buff.CasterNetId, ["payload"] = payload,
        };
        if (buff.Stacks != 1 || shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("stacks", out _)) result["stacks"] = buff.Stacks;
        if (buff.MaxStack is not null || shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("max_stack", out _)) result["max_stack"] = buff.MaxStack;
        return result;
    }

    private static Dictionary<string, object?> SnapshotWorm(NeutralWormStateSnapshot actual, JsonElement shape)
    {
        var values = new Dictionary<string, object?>
        {
            ["worm_growth_elapsed"] = actual.GrowthElapsed, ["worm_recoup_elapsed"] = actual.RecoupElapsed,
            ["worm_recoup_block"] = actual.RecoupBlock, ["worm_action_remaining"] = actual.ActionRemaining,
            ["worm_wrath_cooldown"] = actual.WrathCooldown, ["worm_action"] = actual.Action,
            ["worm_wrath_selections_remaining"] = actual.WrathSelections,
            ["worm_wrath_select_remaining"] = actual.WrathSelectRemaining,
            ["worm_wrath_pending"] = actual.PendingWrath.Select(p => new { target_netid = p.TargetNetId, remaining = p.Remaining }).ToArray(),
            ["worm_geysers"] = actual.Geysers.Select(p => new { pos = new[] { p.Position.X, p.Position.Z }, remaining = p.Remaining }).ToArray(),
        };
        return values.Where(p => shape.TryGetProperty(p.Key, out _) || p.Value switch
        {
            null => false, double number => number != 0, int number => number != 0,
            Array array => array.Length != 0, _ => true,
        }).ToDictionary();
    }

    private static void RecordNeutralState(JsonElement frame, NeutralHarness harness)
    {
        var rows = frame.GetProperty("entities");
        // Batch 174 part 2: an entity the fixture does not hold yet (the propel placemarker that the shipped PropelSpellCaster creates)
        // takes the first row's shape; its values come from the entity itself.
        CorpusSnapshot.Set(rows, harness.World.Entities.Values.OrderBy(entity => entity.NetId).Select(entity =>
            SnapshotNeutral(entity, rows.EnumerateArray().FirstOrDefault(row =>
                row.GetProperty("netid").GetUInt32() == entity.NetId) is { ValueKind: JsonValueKind.Object } known
                    ? known : rows.EnumerateArray().First(), harness)).ToArray());
        if (frame.TryGetProperty("pendingHits", out var hits))
            CorpusSnapshot.Set(hits, harness.Combat.PendingHits.Select(hit => new object?[]
            {
                hit.WindupRemaining, hit.ProjectilePosition.X, hit.ProjectilePosition.Z,
                hit.SourceNetId, hit.TargetNetId, hit.Damage, hit.MissileSpeed, CorpusSnapshot.SourceKind(hit.SourceKind),
            }).ToArray());
    }

    private static void CompareFrame(JsonElement frame, NeutralHarness harness,
        bool adaptCanCastBits1 = false, string? series = null,
        NeutralHarness? wormHarness = null, WormDashReplayState? wormDashState = null,
        IReadOnlyDictionary<uint, SweepMotionProjection>? expectedSweepProjections = null,
        double? expectedSweepElapsed = null)
    {
        if (FixtureRecorder.Enabled) RecordNeutralState(frame, harness);
        else
        {
        CompareEntities(frame.GetProperty("entities"), harness.World,
            (series is null ? "" : series + " ") + "frame " + frame.GetProperty("frame").GetInt32());
        ComparePendingHits(frame.TryGetProperty("pendingHits", out var pendingHits)
                ? pendingHits : default,
            harness.Combat.PendingHits,
            "frame " + frame.GetProperty("frame").GetInt32() + " pending hits");
        CompareScriptState(frame.GetProperty("entities"), harness,
            "frame " + frame.GetProperty("frame").GetInt32() + " script state");
        }
        ComparePackets(frame.GetProperty("packets"), harness.Queue.Packets,
            (series is null ? "" : series + " ") + "frame " + frame.GetProperty("frame").GetInt32(),
            adaptCanCastBits1,
            wormHarness, wormDashState, expectedSweepProjections, expectedSweepElapsed);
        if (!FixtureRecorder.Enabled && frame.TryGetProperty("randomCalls", out var randomCalls))
        {
            harness.Random.AssertCalls(randomCalls, "recorded RNG");
        }
        harness.Random.ClearConsumed();
    }

    private static void CompareEntities(JsonElement rows, WorldState world, string context)
    {
        foreach (var row in rows.EnumerateArray())
        {
            var entity = world.Get(row.GetProperty("netid").GetUInt32());
            if (entity is null)
                throw new InvalidOperationException(context + " missing entity " + row.GetProperty("netid").GetUInt32());
            TestAssert.Near(row.GetProperty("hp").GetDouble(), entity.Health, 1e-5,
                context + " hp expected=" + row.GetProperty("hp").GetDouble() + " actual=" + entity.Health);
            TestAssert.Near(row.GetProperty("max_hp").GetDouble(), entity.MaxHealth, 1e-5, context + " max hp");
            TestAssert.Near(row.GetProperty("atk_damage").GetDouble(), entity.AttackDamage, 1e-5, context + " ad");
            TestAssert.Near(row.GetProperty("mana").GetDouble(), entity.Mana, 1e-5, context + " mana");
            TestAssert.Near(row.GetProperty("max_mana").GetDouble(), entity.MaxMana, 1e-5, context + " max mana");
            TestAssert.Near(row.GetProperty("armor").GetDouble(), entity.Armor, 1e-5, context + " armor");
            TestAssert.Near(row.GetProperty("spell_block").GetDouble(), entity.SpellBlock, 1e-5, context + " MR");
            TestAssert.Near(row.GetProperty("speed").GetDouble(), entity.Speed, 1e-5, context + " speed");
            TestAssert.Near(row.GetProperty("radius").GetDouble(), entity.Radius, 1e-5, context + " radius");
            TestAssert.Near(row.GetProperty("atk_range").GetDouble(), entity.AttackRange, 1e-5, context + " range");
            var actorName = entity.Kind == EntityKind.Neutral ? entity.Name : entity.ChampionName;
            TestAssert.Near(row.GetProperty("atk_period").GetDouble(), entity.AttackPeriod, 1e-5, context + " period");
            TestAssert.Near(row.GetProperty("windup").GetDouble(), entity.AttackWindup, 1e-5, context + " windup");
            TestAssert.Near(row.GetProperty("missile_speed").GetDouble(), entity.MissileSpeed, 1e-5, context + " missile");
            TestAssert.Near(row.GetProperty("hp_regen").GetDouble(), entity.HealthRegen, 1e-5, context + " regen");
            TestAssert.Equal(row.GetProperty("alive").GetBoolean(), entity.Alive, context + " alive");
            TestAssert.Equal(row.GetProperty("created").GetBoolean(), entity.Created, context + " created");
            TestAssert.Equal(row.GetProperty("visible").GetBoolean(), entity.Visible, context + " visible");
            TestAssert.Equal(row.GetProperty("level").GetInt32(), entity.Level, context + " level");
            var position = row.GetProperty("position");
            TestAssert.Near(position[0].GetDouble(), entity.Position.X, 1e-4, context + " x");
            TestAssert.Near(position[1].GetDouble(), entity.Position.Z, 1e-4, context + " z");
            TestAssert.Equal(row.GetProperty("attack_target").ValueKind == JsonValueKind.Null
                ? 0u : row.GetProperty("attack_target").GetUInt32(), entity.AttackTarget ?? 0u,
                context + " target");
            var expectedState = row.GetProperty("neutral_state").GetString();
            TestAssert.Equal(expectedState, entity.NeutralState,
                context + " state expected=" + expectedState + " actual=" + entity.NeutralState);
            TestAssert.Near(row.GetProperty("neutral_scaling_elapsed").GetDouble(),
                entity.NeutralScalingElapsed, 1e-5,
                context + " scaling elapsed expected=" + row.GetProperty("neutral_scaling_elapsed").GetDouble() +
                " actual=" + entity.NeutralScalingElapsed + " netid=" + entity.NetId + " name=" + entity.Name);
            TestAssert.Equal(row.GetProperty("buffs").GetArrayLength(), entity.Buffs.Count,
                context + " buff count expected=" + row.GetProperty("buffs").GetArrayLength() +
                " actual=" + entity.Buffs.Count + " names=" + string.Join(',', entity.Buffs.Select(buff => buff.Name)));
            CompareEntityState(row, entity, context + $" {entity.Name}/{entity.NetId}");
        }
    }

    private static void CompareEntityState(JsonElement row, Entity entity, string context)
    {
        if (row.TryGetProperty("target", out var target))
            CompareOptionalPosition(target, entity.Target, context + " target position");
        if (row.TryGetProperty("waypoints", out var waypoints))
            ComparePositions(waypoints, entity.Waypoints, context + " waypoints");
        if (row.TryGetProperty("anchor_path", out var anchorPath))
            ComparePositions(anchorPath, entity.AnchorPath, context + " anchor path");
        if (row.TryGetProperty("last_anchor_time", out var anchorTime))
            TestAssert.Near(anchorTime.GetDouble(), entity.LastAnchorTime, 1e-8,
                context + " last anchor time");
        if (row.TryGetProperty("forced_move_grace", out var forcedGrace))
            TestAssert.Near(forcedGrace.GetDouble(), entity.ForcedMoveGrace, 1e-8,
                context + " forced move grace");
        if (row.TryGetProperty("neutral_leash_counter", out var leashCounter))
            TestAssert.Equal(leashCounter.GetInt32(), entity.NeutralLeashCounter,
                context + " leash counter");
        // neutral_retreat_elapsed and neutral_regen_elapsed are not compared: they were the
        // hand-written machine's private 0.5 s and 1 s clocks. Leashed.luaobj times the same work
        // with its host timers (TimerRetreat, TimerRegen), so nothing writes the entity fields any
        // more and the recorded rows pin bookkeeping that no longer exists. The script wins; the
        // corpora are untouched and keep capturing the fields.
        if (row.TryGetProperty("neutral_last_move", out var lastMove))
            CompareOptionalPosition(lastMove, entity.NeutralLastMove, context + " last move");
        if (row.TryGetProperty("neutral_player_level", out var playerLevel))
            TestAssert.Equal(playerLevel.GetInt32(), entity.NeutralPlayerLevel,
                context + " player level");

        var expectedBuffs = row.GetProperty("buffs").EnumerateArray().ToArray();
        for (var index = 0; index < expectedBuffs.Length; index++)
            CompareBuff(expectedBuffs[index], entity.Buffs[index],
                context + " buff[" + index + "]");

        if (row.TryGetProperty("forced_move", out var forcedMove))
            CompareForcedMove(forcedMove, entity.ForcedMove, context + " forced move");
    }

    private static void CompareBuff(JsonElement expected, BuffState actual, string context)
    {
        TestAssert.Equal(expected.GetProperty("name").GetString(), actual.Name, context + " name");
        TestAssert.Equal(expected.GetProperty("slot").GetInt32(), actual.Slot, context + " slot");
        TestAssert.Equal((BuffType)expected.GetProperty("type").GetInt32(), actual.Type, context + " type");
        TestAssert.Near(expected.GetProperty("expires_at").GetDouble(), actual.ExpiresAt, 1e-8,
            context + " expiry");
        TestAssert.Equal(expected.GetProperty("caster").GetUInt32(), actual.CasterNetId, context + " caster");
        if (expected.TryGetProperty("stacks", out var stacks))
            TestAssert.Equal(stacks.GetInt32(), actual.Stacks, context + " stacks");
        if (expected.TryGetProperty("max_stack", out var maxStack))
        {
            var expectedMax = maxStack.ValueKind == JsonValueKind.Null ? (int?)null : maxStack.GetInt32();
            TestAssert.Equal(expectedMax, actual.MaxStack, context + " max stack");
        }

        if (!expected.TryGetProperty("payload", out var payload)) return;
        var expectedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            expectedFields.Add(property.Name);
            switch (property.Name)
            {
                case "max_hp":
                    TestAssert.True(actual.Payload.Has(BuffPayloadField.MaxHp), context + " max_hp presence");
                    TestAssert.Near(property.Value.GetDouble(), actual.Payload.MaxHp, 1e-8, context + " max_hp");
                    break;
                case "attack_damage":
                    TestAssert.True(actual.Payload.Has(BuffPayloadField.AttackDamage), context + " attack_damage presence");
                    TestAssert.Near(property.Value.GetDouble(), actual.Payload.AttackDamage, 1e-8, context + " attack_damage");
                    break;
                case "spell_block":
                    TestAssert.True(actual.Payload.Has(BuffPayloadField.SpellBlock), context + " spell_block presence");
                    TestAssert.Near(property.Value.GetDouble(), actual.Payload.SpellBlock, 1e-8, context + " spell_block");
                    break;
                case "fx_netid":
                    TestAssert.Equal(property.Value.GetUInt32(), actual.Payload.FxNetId, context + " fx_netid");
                    break;
                case "action_disabled":
                    TestAssert.Equal(property.Value.GetBoolean(), actual.Payload.ActionDisabled, context + " action_disabled");
                    break;
                default:
                    throw new InvalidOperationException(context + " fixture has an unsupported payload field '" + property.Name + "'");
            }
        }

        // The fixture currently exercises these typed fields. Check their
        // presence too, so a zero-valued field cannot silently appear or vanish.
        if (!expectedFields.Contains("max_hp"))
            TestAssert.True(!actual.Payload.Has(BuffPayloadField.MaxHp), context + " unexpected max_hp");
        if (!expectedFields.Contains("attack_damage"))
            TestAssert.True(!actual.Payload.Has(BuffPayloadField.AttackDamage), context + " unexpected attack_damage");
        if (!expectedFields.Contains("spell_block"))
            TestAssert.True(!actual.Payload.Has(BuffPayloadField.SpellBlock), context + " unexpected spell_block");
    }

    private static void CompareOptionalPosition(JsonElement expected, Position2? actual, string context)
    {
        if (expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            TestAssert.True(actual is null, context + " expected null");
            return;
        }
        TestAssert.True(expected.ValueKind == JsonValueKind.Array && expected.GetArrayLength() == 2,
            context + " malformed fixture position");
        TestAssert.True(actual is not null, context + " unexpectedly null");
        TestAssert.Near(expected[0].GetDouble(), actual!.Value.X, 1e-5, context + " x");
        TestAssert.Near(expected[1].GetDouble(), actual.Value.Z, 1e-5, context + " z");
    }

    private static void ComparePositions(JsonElement expected, IReadOnlyList<Position2> actual, string context)
    {
        TestAssert.True(expected.ValueKind == JsonValueKind.Array, context + " malformed fixture path");
        TestAssert.Equal(expected.GetArrayLength(), actual.Count,
            context + $" count expected={expected.GetRawText()} actual=[{string.Join(';', actual)}]");
        var index = 0;
        foreach (var point in expected.EnumerateArray())
        {
            TestAssert.True(point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2,
                context + " malformed point");
            TestAssert.Near(point[0].GetDouble(), actual[index].X, 1e-5, context + " x[" + index + "]");
            TestAssert.Near(point[1].GetDouble(), actual[index].Z, 1e-5, context + " z[" + index + "]");
            index++;
        }
    }

    private static void CompareForcedMove(JsonElement expected, ForcedMoveState? actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null ||
            expected.ValueKind == JsonValueKind.Object && expected.GetPropertyCount() == 0)
        {
            TestAssert.True(actual is null, context + " expected inactive");
            return;
        }
        TestAssert.True(actual is not null, context + " unexpectedly inactive");
        var destination = expected.GetProperty("destination");
        var expectedDestination = new Position2(destination[0].GetDouble(), destination[1].GetDouble());
        TestAssert.Near(expectedDestination.X, actual!.Destination.X, 1e-5, context + " destination x");
        TestAssert.Near(expectedDestination.Z, actual.Destination.Z, 1e-5, context + " destination z");
        TestAssert.Near(expected.GetProperty("speed").GetDouble(), actual.Speed, 1e-8, context + " speed");
        TestAssert.Near(expected.GetProperty("remaining").GetDouble(), actual.Remaining, 1e-8, context + " remaining");
        TestAssert.Equal(expected.GetProperty("source_netid").GetUInt32(), actual.SourceNetId, context + " source");
        TestAssert.Equal(expected.GetProperty("effect").GetString(), actual.Effect, context + " effect");
        TestAssert.Equal(expected.GetProperty("first_collision").GetBoolean(), actual.FirstCollision, context + " collision");
        TestAssert.Equal(expected.GetProperty("completed").GetBoolean(), actual.Completed, context + " completed");
    }

    private static void ComparePendingHits(JsonElement rows, IReadOnlyList<PendingHit> actual, string context)
    {
        if (rows.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            TestAssert.Equal(0, actual.Count, context + " count");
            return;
        }
        var expected = rows.EnumerateArray().ToArray();
        TestAssert.Equal(expected.Length, actual.Count, context + " count");
        for (var index = 0; index < expected.Length; index++)
        {
            var row = expected[index];
            var hit = actual[index];
            var expectedPosition = new Position2(row[1].GetDouble(), row[2].GetDouble());
            TestAssert.Near(row[0].GetDouble(), hit.WindupRemaining, 1e-8, context + " remaining[" + index + "]");
            TestAssert.Near(expectedPosition.X, hit.ProjectilePosition.X, 1e-5, context + " x[" + index + "]");
            TestAssert.Near(expectedPosition.Z, hit.ProjectilePosition.Z, 1e-5, context + " z[" + index + "]");
            TestAssert.Equal(row[3].GetUInt32(), hit.SourceNetId, context + " source[" + index + "]");
            TestAssert.Equal(row[4].GetUInt32(), hit.TargetNetId, context + " target[" + index + "]");
            TestAssert.Near(row[5].GetDouble(), hit.Damage, 1e-8, context + " damage[" + index + "]");
            TestAssert.Near(row[6].GetDouble(), hit.MissileSpeed, 1e-8, context + " missile[" + index + "]");
            TestAssert.Equal(DamageTypeExtensions.ParseSourceKind(row[7].GetString()), hit.SourceKind,
                context + " kind[" + index + "]");
        }
    }

    private static void CompareScriptState(JsonElement rows, NeutralHarness harness, string context)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("name").GetString() != "Worm") continue;
            var expected = row.GetProperty("neutral_script_state");
            if (expected.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(context + " malformed Worm script state");
            var worm = harness.World.Get(row.GetProperty("netid").GetUInt32())
                ?? throw new InvalidOperationException(context + " missing Worm");
            var actual = harness.WormState(worm);
            CompareWormState(expected, actual, context);
        }
    }

    private static void CompareWormState(JsonElement expected, NeutralWormStateSnapshot actual, string context)
    {
        CompareDefaultedDouble(expected, "worm_growth_elapsed", actual.GrowthElapsed, context);
        CompareDefaultedDouble(expected, "worm_recoup_elapsed", actual.RecoupElapsed, context);
        CompareDefaultedDouble(expected, "worm_recoup_block", actual.RecoupBlock, context);
        CompareDefaultedDouble(expected, "worm_action_remaining", actual.ActionRemaining, context);
        CompareDefaultedDouble(expected, "worm_wrath_cooldown", actual.WrathCooldown, context);
        var action = expected.TryGetProperty("worm_action", out var actionValue)
            ? actionValue.GetString() : null;
        TestAssert.Equal(action, actual.Action, context + " action");
        if (expected.TryGetProperty("worm_wrath_selections_remaining", out var selections))
            TestAssert.Equal(selections.GetInt32(), actual.WrathSelections, context + " wrath selections");
        else
            TestAssert.Equal(0, actual.WrathSelections, context + " wrath selections");
        if (expected.TryGetProperty("worm_wrath_select_remaining", out var selectRemaining))
            TestAssert.Near(selectRemaining.GetDouble(), actual.WrathSelectRemaining, 1e-8,
                context + " wrath select timer");
        else
            TestAssert.Near(0.0, actual.WrathSelectRemaining, 1e-8, context + " wrath select timer");
        if (expected.TryGetProperty("worm_wrath_pending", out var pending))
        {
            var pendingRows = pending.EnumerateArray().ToArray();
            TestAssert.Equal(pendingRows.Length, actual.PendingWrath.Count, context + " wrath pending count");
            for (var i = 0; i < pendingRows.Length; i++)
            {
                TestAssert.Equal(pendingRows[i].GetProperty("target_netid").GetUInt32(),
                    actual.PendingWrath[i].TargetNetId, context + " wrath target");
                TestAssert.Near(pendingRows[i].GetProperty("remaining").GetDouble(),
                    actual.PendingWrath[i].Remaining, 1e-8, context + " wrath remaining");
            }
        }
        else
            TestAssert.Equal(0, actual.PendingWrath.Count, context + " wrath pending count");
        if (expected.TryGetProperty("worm_geysers", out var geysers))
        {
            var geyserRows = geysers.EnumerateArray().ToArray();
            TestAssert.Equal(geyserRows.Length, actual.Geysers.Count, context + " geyser count");
            for (var i = 0; i < geyserRows.Length; i++)
            {
                var position = geyserRows[i].GetProperty("pos");
                TestAssert.Near(geyserRows[i].GetProperty("remaining").GetDouble(),
                    actual.Geysers[i].Remaining, 1e-8, context + " geyser remaining");
                TestAssert.Near(position[0].GetDouble(), actual.Geysers[i].Position.X, 1e-5,
                    context + " geyser x");
                TestAssert.Near(position[1].GetDouble(), actual.Geysers[i].Position.Z, 1e-5,
                    context + " geyser z");
            }
        }
        else
            TestAssert.Equal(0, actual.Geysers.Count, context + " geyser count");
    }

    private static void CompareDefaultedDouble(JsonElement expected, string name, double actual, string context)
    {
        var value = expected.TryGetProperty(name, out var expectedValue)
            ? expectedValue.GetDouble() : 0.0;
        TestAssert.Near(value, actual, 1e-8, context + " " + name);
    }

    private static void ComparePackets(JsonElement rows, IReadOnlyList<PlaintextPacket> actual,
        string context, bool adaptCanCastBits1 = false, NeutralHarness? wormHarness = null,
        WormDashReplayState? wormDashState = null,
        IReadOnlyDictionary<uint, SweepMotionProjection>? expectedSweepProjections = null,
        double? expectedSweepElapsed = null)
    {
        if (wormHarness is not null && wormDashState is not null)
        {
            ValidateWormEffects(actual, wormHarness, wormDashState);
            ValidateWormDashes(actual, wormHarness, wormDashState, expectedSweepProjections);
        }
        var cipher = LegacyBlowfish.CreateDefault();
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(rows, actual.Select(packet => new
            {
                channel = packet.Channel, flags = (byte)packet.Reliability,
                plaintext = Convert.ToHexString(packet.Plaintext.Span).ToLowerInvariant(),
                wire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant(),
            }).ToArray());
            return;
        }
        var expected = rows.EnumerateArray().ToArray();
        TestAssert.Equal(expected.Length, actual.Count, context + " packet count");
        for (var index = 0; index < expected.Length; index++)
        {
            var row = expected[index];
            TestAssert.Equal(row.GetProperty("channel").GetByte(), actual[index].Channel, context + " channel");
            TestAssert.Equal(row.GetProperty("flags").GetByte(), (byte)actual[index].Reliability, context + " flags");
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("plaintext").GetString()!),
                actual[index].Plaintext.ToArray(), context + " plaintext " + index);
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("wire").GetString()!),
                actual[index].ToWireBytes(cipher), context + " wire " + index);
        }
    }

    private static HashSet<int> ValidateWormEffects(
        IReadOnlyList<PlaintextPacket> packets, NeutralHarness harness,
        WormDashReplayState state)
    {
        var activeBuffs = harness.World.Entities.Values.SelectMany(entity => entity.Buffs
            .Where(buff => buff.HasName("Propel"))
            .Select(buff => (Id: buff.Payload.FxNetId, Target: entity)))
            .ToArray();
        TestAssert.True(activeBuffs.All(pair => pair.Id != 0),
            "every active Propel buff has its required persistent effect");
        var current = activeBuffs.ToDictionary(pair => pair.Id, pair => pair.Target);
        var created = current.Keys.Except(state.ActiveEffects).ToHashSet();
        var removed = state.ActiveEffects.Except(current.Keys).ToHashSet();
        var reviewed = new HashSet<int>();
        var bounds = new NavigationBounds(harness.Movement.Navigation.MinX,
            harness.Movement.Navigation.MaxX, harness.Movement.Navigation.MinZ,
            harness.Movement.Navigation.MaxZ);
        for (var index = 0; index < packets.Count; index++)
        {
            var packet = packets[index];
            var bytes = packet.Plaintext.ToArray();
            PlaintextPacket expected;
            if (bytes.Length == 73 && bytes[0] == FxPackets.CreateGroupOpcode &&
                // Batch 208, from the shipped script (PropelTimer.luaobj): the Propel's one BBSpellEffectCreate (PropelBubbles.troy
                // at PropelPos) has BindObjectVar = Other1, the geyser that the same script's BBSpawnMinion (DestVar Other1,
                // Placemarker true) spawned. TargetObjectVar 'Target' is never assigned in that script, so the target field keeps
                // the engine's default, the Worm (bytes 21-24, unchanged by batch 202). SpecificUnitOnlyVar Owner is unused
                // (UseSpecificUnit is false). PropelSpellCaster's SetNoRender leaves the announced geyser drawing only particles.
                // Batch 194 pinned bind 0 (the Worm) from the unannounced stand-in; batch 202 announced the geyser, so bind was its netid.
                // Batches 397-398 (Reconstruction): an effect bound to a placemarker is sent unbound at the placemarker's point, and a target
                // that is another unit (the Worm) is kept, so bind is 0 again.
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21)) == harness.Worm.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)) == SpellPackets.BuffNameHash("PropelBubbles"))
            {
                TestAssert.True(state.WarningPosition is not null && state.WarningEffectsRemaining > 0,
                    "only the one reviewed initial Propel warning is projected");
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25));
                TestAssert.True(state.AllocatedEffects.Add(id));
                state.WarningEffectsRemaining--;
                TestAssert.True(state.WarningGeyserNetId is not null, "the Propel geyser was observed before its warning");
                expected = FxPackets.CreateGroup(SpellPackets.BuffNameHash("PropelBubbles"),
                    id, state.WarningPosition!.Value, bounds,
                    (x, z) => harness.Movement.Navigation.HeightAt(new Position2(x, z)),
                    targetPosition: harness.Worm.Position, ownerPosition: harness.Worm.Position,
                    targetNetId: harness.Worm.NetId, bindNetId: 0);
            }
            // Each active Propel holder's persistent bubble is bound to the holder and targets it (frame packets, e.g. the minion holder
            // of the propel case). The warning above targets the Worm instead (the script's unset 'Target' default, batch 208).
            else if (bytes.Length == 73 && bytes[0] == FxPackets.CreateGroupOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)) ==
                    SpellPackets.BuffNameHash("PropelBubbles"))
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25));
                ReplayExpect(created.Remove(id), "PropelBubbles creates exactly one new effect per active buff");
                ReplayExpect(state.AllocatedEffects.Add(id), "new persistent effects use unique world allocations");
                // Batch 174b: a bubble whose unit has left the world (the dead geyser's) has no reviewed target; record mode leaves that packet to
                // the generic recorder, replay names it.
                if (!current.TryGetValue(id, out var target))
                {
                    ReplayExpect(false, "PropelBubbles belongs to a unit in the world");
                    continue;
                }
                expected = FxPackets.CreateGroup(SpellPackets.BuffNameHash("PropelBubbles"),
                    id, target.Position, bounds,
                    (x, z) => harness.Movement.Navigation.HeightAt(new Position2(x, z)),
                    targetNetId: target.NetId, bindNetId: target.NetId);
            }
            else if (bytes.Length == 9 && bytes[0] == FxPackets.KillOpcode &&
                state.ActiveEffects.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5))))
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5));
                ReplayExpect(removed.Remove(id), "PropelBubbles is destroyed exactly once when its buff ends");
                expected = FxPackets.Kill(id);
            }
            else continue;
            if (!FixtureRecorder.Enabled)
            {
                TestAssert.Equal(expected.Channel, packet.Channel, "PropelBubbles channel");
                TestAssert.Equal(expected.Reliability, packet.Reliability, "PropelBubbles reliability");
                TestAssert.Equal(expected.Plaintext.ToArray(), bytes,
                    "PropelBubbles asset, position, attachment and default packet fields");
            }
            reviewed.Add(index);
        }
        if (!FixtureRecorder.Enabled)
        {
            TestAssert.Equal(0, created.Count, "all new PropelBubbles effects were created");
            TestAssert.Equal(0, removed.Count, "all ended PropelBubbles effects were destroyed");
        }
        ReplayEqual(0, state.WarningEffectsRemaining, "the ground warning was emitted");
        state.ActiveEffects.Clear();
        state.ActiveEffects.UnionWith(current.Keys);
        return reviewed;
    }

    private static void ValidateWormDashes(
        IReadOnlyList<PlaintextPacket> actual, NeutralHarness harness,
        WormDashReplayState state, IReadOnlyDictionary<uint, SweepMotionProjection>? expectedSweepProjections)
    {
        var moves = harness.World.Entities.Values
            .Where(entity => entity.ForcedMove is { Completed: false, Effect: "Propel" or "SweepingBlow" })
            .ToDictionary(entity => entity.NetId,
                entity => new WormMoveSnapshot(entity.ForcedMove!.Effect!,
                    entity.ForcedMove.SourceNetId, entity.ForcedMove.Destination,
                    entity.ForcedMove.Speed));
        var newMoves = moves.Where(pair => !state.ActiveMoves.TryGetValue(pair.Key, out var previous) ||
                !previous.Equals(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var dashes = actual.Where(packet => packet.Plaintext.Span.Length > 0 &&
            packet.Plaintext.Span[0] == VisionPackets.DashOpcode).ToArray();
        ReplayEqual(newMoves.Count, dashes.Length,
            "Worm emits one presentation dash for each new successful forced move");
        var grid = harness.Movement.Navigation;
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var seen = new HashSet<uint>();
        uint previousSync = state.LastDashSyncId;
        for (var index = 0; index < dashes.Length; index++)
        {
            var packet = dashes[index];
            var bytes = packet.Plaintext.ToArray();
            // Batch 315: the entry's two points are PathToNetworkData's (a hop under 256 units on an axis is a delta byte), so the
            // packet is 57 to 61 bytes; it was a fixed 61 with both points as shorts.
            ReplayExpect(bytes.Length is >= 57 and <= 61, "Worm dash uses the native packet shape");
            ReplayEqual(VisionPackets.DashOpcode, bytes[0], "Worm dash opcode");
            ReplayEqual(LeagueProtocolConstants.LowPriorityChannel, packet.Channel,
                "Worm dash uses the low-priority movement channel");
            ReplayEqual(LeaguePacketReliability.Reliable, packet.Reliability,
                "Worm dash is reliable");

            var sync = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
            var actorNetId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15, 4));
            ReplayExpect(newMoves.TryGetValue(actorNetId, out var move) && seen.Add(actorNetId),
                "Worm dash actor is a unique new successful target");
            var target = harness.World.Get(actorNetId)!;
            ReplayEqual(harness.Worm.NetId, move.SourceNetId,
                "sweep dash forced move retains the Worm source");
            var speed = BitConverter.ToSingle(bytes, 19);
            ReplayEqual(move.Effect == "SweepingBlow" ? 1200.0f : 100.0f, speed,
                "Worm dash speed matches the shipped action");
            ReplayEqual((float)move.Speed, speed,
                "Worm dash speed matches the active forced move");
            // Batch 174 part 2: the dash is the shipped move's own gravity, SweepingBlow's BBMoveAway (Gravity 20, its 0044-0045) and
            // Propel's BBMove (Gravity 25, Propel.luaobj); the hand-written adaptation sent 0.
            ReplayEqual(move.Effect == "SweepingBlow" ? 20.0f : 25.0f, BitConverter.ToSingle(bytes, 23),
                "Worm dash gravity is the shipped move's Gravity");
            var startX = BitConverter.ToSingle(bytes, 27);
            var startZ = BitConverter.ToSingle(bytes, 31);
            ReplayEqual((float)target.Position.X, startX,
                "Worm dash float32 start X matches the target at impact");
            ReplayEqual((float)target.Position.Z, startZ,
                "Worm dash float32 start Z matches the target at impact");
            if (move.Effect == "SweepingBlow")
            {
                if (expectedSweepProjections is null ||
                    !expectedSweepProjections.TryGetValue(actorNetId, out var projection))
                    throw new InvalidOperationException(
                        "SweepingBlow dash is missing its immutable initial landing projection");
                // Batch 174b: record mode writes this build's own dash (the wall-cut projection is the replay's expectation, see the record).
                if (!FixtureRecorder.Enabled)
                {
                    // Batch 194: the forced move's destination is its raw 600-unit request (see ProjectWormSweepLandings).
                    ReplayNear(projection.Request.X, move.Destination.X, 1e-5,
                        "SweepingBlow destination follows its immutable initial 600-unit request");
                    ReplayNear(projection.Request.Z, move.Destination.Z, 1e-5,
                        "SweepingBlow destination follows its immutable initial 600-unit request");
                }
            }
            else
            {
                ReplayEqual("Propel", move.Effect,
                    "Worm dash retains the shipped Propel effect name");
                var dx = move.Destination.X - target.Position.X;
                var dz = move.Destination.Z - target.Position.Z;
                // Batch 194: the client quantises the landing to its 2-unit lattice, so the endpoint lies within one
                // lattice step of the 100-unit radius (this build: 99.51).
                ReplayNear(100.0, Math.Sqrt(dx * dx + dz * dz), 2.0 * Math.Sqrt(2.0),
                    "Propel destination uses its shipped 100-unit radial action, to one lattice step");
            }
            // Batch 194: a sweep's dash packet carries the wall-cut landing the client stops at (the projection's Destination); the
            // forced move's own destination is the raw request (see ProjectWormSweepLandings). Propel's packet carries its destination.
            var wireDestination = move.Effect == "SweepingBlow" && expectedSweepProjections is not null &&
                                  expectedSweepProjections.TryGetValue(actorNetId, out var wireProjection)
                ? wireProjection.Destination : move.Destination;
            // Batch 315: the path (flag byte from offset 52) is compared whole, as the production encoder writes those two points.
            ReplayEqual(Convert.ToHexString(VisionPackets.Dash(actorNetId, target.Position, wireDestination, speed, bounds)
                    .Plaintext.Span[52..]), Convert.ToHexString(bytes.AsSpan(52)),
                "sweep dash packed start and destination match the target and the forced move");
            var clockId = checked((uint)((long)(harness.World.Clock.Now() * 1000.0) & 0x7fff_ffffL));
            ReplayExpect(sync >= clockId,
                "Worm dash sync remains anchored to the current native clock");
            if (previousSync != 0)
                ReplayExpect(sync > previousSync,
                    "each Worm dash advances the native clock sync");
            previousSync = sync;
        }
        ReplayExpect(newMoves.Keys.OrderBy(id => id).SequenceEqual(seen.OrderBy(id => id)),
            "Worm dash coverage matches every new successful forced move");
        state.ActiveMoves.Clear();
        foreach (var pair in moves) state.ActiveMoves[pair.Key] = pair.Value;
        if (dashes.Length > 0) state.LastDashSyncId = previousSync;
    }

    private sealed class WormDashReplayState
    {
        public Position2? WarningPosition { get; set; }
        public int WarningEffectsRemaining { get; set; }
        public uint? WarningGeyserNetId { get; set; }
        public uint LastDashSyncId { get; set; }
        public Dictionary<uint, WormMoveSnapshot> ActiveMoves { get; } = new();
        public HashSet<uint> ActiveEffects { get; } = new();
        public SortedSet<uint> AllocatedEffects { get; } = new();
    }

    private readonly record struct WormMoveSnapshot(string Effect, uint SourceNetId,
        Position2 Destination, double Speed);

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class NeutralHarness
    {
        private NeutralHarness(WorldState world, MovementSystem movement,
            CombatService combat, NeutralAiService ai, CaptureQueue queue, FixtureRandom random,
            Entity hero, Entity worm, BuffLifecycle lifecycle, List<AiScriptFailure> scriptFailures,
            SpellSystem spells)
        {
            World = world; Movement = movement; Combat = combat; Ai = ai;
            Queue = queue; Random = random; Hero = hero; Worm = worm; Lifecycle = lifecycle;
            ScriptFailures = scriptFailures; Spells = spells;
        }

        public WorldState World { get; }
        public MovementSystem Movement { get; }
        public CombatService Combat { get; }
        public NeutralAiService Ai { get; }
        public CaptureQueue Queue { get; }
        public FixtureRandom Random { get; }
        public Entity Hero { get; }
        public Entity Worm { get; }
        public List<AiScriptFailure> ScriptFailures { get; }
        /// <summary>
        /// The spell host the camp members' character scripts are bound to (batch 173; production's <c>Spells</c>). Its
        /// per-update poll is <see cref="TickBuffs"/>' first step, and <c>Spells.UnitScripts</c> is the module.
        /// </summary>
        public SpellSystem Spells { get; }
        private BuffLifecycle Lifecycle { get; }

        /// <summary>Leashed.luaobj raised nothing while this harness was running.</summary>
        public void AssertNoScriptFailure()
        {
            if (ScriptFailures.Count == 0) return;
            TestAssert.True(false, "Leashed.luaobj raised: " + string.Join("; ",
                ScriptFailures.Select(failure => $"{failure.Script}/{failure.Hook}: {failure.Message}")));
        }
        public void TickBuffs(double dt)
        {
            // Batch 173: the match's spell update first, then the buff phase — the order ProductionSimulationDriver.Tick
            // runs. SpellSystem.Tick both advances the script clock (a script buff's BuffOnUpdateStats samples on a grid
            // of that clock: BbBuffRuntime.StatsNextAt) and polls the bound units' UpdateSelfBuffStats/Actions
            // (`UnitScripts?.Poll()`); ticking only the poll left the grid frozen at the bind instant, so e.g. the golem's
            // HPByPlayerLevel never re-evaluated.
            Spells.Tick(dt);
            foreach (var entity in World.Entities.Values.ToArray()) Lifecycle.TickBuffs(entity, dt);
        }

        public BuffState? AddBuff(Entity entity, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null) =>
            Lifecycle.AddBuff(entity, name, type, duration, casterNetId, payload);

        /// <summary>
        /// Batch 174 part 2: starts one of the Baron's shipped action timers the way its rotation does. The timer buff is added and
        /// removed again, so its OnBuffDeactivate runs the action (the cast, the geysers, the wrath marks). The roll is the script's.
        /// </summary>
        public bool TriggerWorm(Entity worm, NeutralWormAction action)
        {
            return NeutralWormScriptView.Trigger(Spells.UnitScripts!, Lifecycle, worm, action);
        }

        public NeutralWormStateSnapshot WormState(Entity worm) => NeutralWormScriptView.Snapshot(World, worm);

        public static NeutralHarness Create(JsonElement initial,
            IReadOnlyList<JsonElement> randomCalls, int campId, double spawnTime,
            bool preserveSpawnSync, Func<Entity, Entity, bool>? targetVisible = null,
            IRandomSource? scriptRandom = null)
        {
            var world = new WorldState();
            world.Clock.SetVirtual();
            var grid = NavigationGrid.LoadDefault(GameDataPaths.Default, NavigationGridSettings.Default);
            var queue = new CaptureQueue();
            var initialSync = new ReplicationSyncCounter();
            var neutralSink = new ProtocolNeutralPacketSink(packet => queue.Enqueue(packet), initialSync, world.Allocate,
                new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)));
            var definition = NeutralDefinitions.Camps[campId];
            var scheduler = new JungleScheduler(world, GameData.Default, neutralSink, [campId]);
            scheduler.Start(spawnTime - NeutralDefinitions.InitialSpawnSeconds - definition.InitialDelay);

            var heroRow = initial.EnumerateArray().First(row => row.GetProperty("unitType").GetInt32() == 0);
            var hero = ReadEntity(heroRow);
            // These replay rows intentionally omit champion metadata.  The
            // source scenario uses Annie's level-6 hero stat schema for its
            // periodic full-stat refresh.
            hero.ChampionName = "Annie";
            world.Register(hero);
            scheduler.Tick(spawnTime);
            var neutralMembers = scheduler.MembersForCamp(campId);
            var worm = neutralMembers.FirstOrDefault(member => member.Name == "Worm")?.Entity;
            if (worm is null)
                worm = neutralMembers.First().Entity;
            var minionRow = initial.EnumerateArray().FirstOrDefault(row => row.GetProperty("unitType").GetInt32() == 1);
            if (minionRow.ValueKind != JsonValueKind.Undefined) world.Register(ReadEntity(minionRow));
            foreach (var row in initial.EnumerateArray())
            {
                var entity = world.Get(row.GetProperty("netid").GetUInt32());
                if (entity is not null) ApplyEntity(row, entity);
            }
            queue.Clear();

            // Spawn packets are deliberately omitted by the replay fixtures;
            // their match counter starts at the first observed gameplay send.
            // The initial neutral spawn consumes the first per-unit replication
            // sequence even though those packets are outside the replay window.
            // Keep that authoritative counter when gameplay output starts.
            var sync = preserveSpawnSync ? initialSync : new ReplicationSyncCounter();
            neutralSink = new ProtocolNeutralPacketSink(packet => queue.Enqueue(packet), sync, world.Allocate,
                new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)));

            var random = new FixtureRandom(randomCalls);
            var hooks = new NeutralHooks();
            var statsTransport = new NeutralStatsTransport();
            var stats = new HeroStatsService(GameData.Default, new ItemCatalog(GameData.Default.Paths),
                new NeutralStatsHooks(), statsTransport);
            var combatTransport = new ProtocolCombatTransport(queue, stats, _ => true, sync);
            // BuffLifecycle.Push is the source's immediate full-stat refresh
            // for hero stat modifiers (including WormAttack). Route that
            // callback through the real combat transport so the replay sees
            // the same 0xCC packet and shared per-unit counter.
            statsTransport.Attach(snapshot =>
            {
                var refreshed = world.Get(snapshot.HeroNetId);
                if (refreshed is not null) combatTransport.SendHeroStats(refreshed);
            });
            // Ordinary wake-up uses the combat attack-roll helper, while the
            // exported neutral AI RNG stream records only scripted choices.
            // Keep those domains separate so an unrecorded basic-attack roll
            // cannot consume a scripted replay value.
            var combat = new CombatService(world, hooks, combatTransport,
                new ConstantRandomSource(0.99), stats);
            var effects = new ProtocolSpellEffectSink(queue, sync,
                new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate,
                // Batch 202: an announced placemarker (the Worm's geyser) reaches this sink too. Production builds a minion's stats through
                // its replication holder; this harness models hero stats only, and the geyser's stats are not what these checks read.
                unit =>
                {
                    if (unit.UnitType is UnitType.Hero) combatTransport.SendHeroStats(unit);
                });
            var services = new NeutralBuffServices(hooks, combat, effects, queue, world);
            var lifecycle = new BuffLifecycle(world, stats, services);
            // Batch 173: production's own buff boundary (MatchRuntime's BuffLifecycleAdapter), not a reduced copy — a script buff's
            // BuffOnUpdateStats resample goes through ReplaceScriptPayload, and the camp character scripts' HPByPlayerLevel
            // is exactly that. The copy this replaces raised "does not implement script payload replacement", which
            // production never does.
            var spellBuffs = new BuffLifecycleAdapter(lifecycle);
            var movement = new MovementSystem(world, grid);
            // Batch 173: the camp members' own shipped character scripts run here, the way ProductionMatchComposition runs
            // them. The module goes on a spell system the replay ticks (SpellSystem.Tick is the units' UpdateSelfBuffStats/
            // Actions poll) and each monster is bound as the scheduler creates it: without this, CharScriptAncientGolem's
            // UpdateSelfBuffActions (its CrestoftheAncientGolem and HPByPlayerLevel adds) and every camp's CharOnHitUnit
            // x1.43 never ran, so the recorded world was not the game's.
            var spellHost = UnitCharacterRig.SpellHost(world, movement, new CombatSpellAdapter(combat), effects, spellBuffs);
            var (unitScripts, unitHooks) = MatchScriptSeams.InstallUnitCharacters(spellHost);
            services.AttachUnitScripts(unitScripts, unitHooks);
            // The bind runs as the scheduler creates each camp member (batch 159 part 2). The scheduler already spawned
            // this fixture's camp before it existed, so the members it created are bound now, in its own order; the
            // fixture's restored state is applied above, and the script's first sample runs on the replay's first poll.
            MatchScriptSeams.BindUnitCharacters(scheduler, unitScripts);
            foreach (var member in scheduler.Members.Values)
                MatchScriptSeams.BindUnitCharacter(unitScripts, member.Entity, member.Name);
            var ai = new NeutralAiService(world, scheduler, movement, combat, neutralSink,
                effects, spellBuffs, GameData.Default, scriptRandom ?? random, targetVisible);
            var scriptFailures = new List<AiScriptFailure>();
            ai.AiScriptError = scriptFailures.Add;
            hooks.Attach(ai, lifecycle);
            hooks.AttachUnitScripts(unitHooks);
            lifecycle.Neutrals = ai;
            // The Worm fixture starts after the match clock has already
            // reached its scheduled spawn time. Restore that authoritative
            // combat time so the first 1-second hero-stat interval is emitted
            // by the real CombatService during the replay tick.
            combat.RestoreClock(spawnTime);
            world.Clock.SetVirtual(spawnTime);
            return new NeutralHarness(world, movement, combat, ai, queue, random, hero, worm, lifecycle,
                scriptFailures, spellHost);
        }

        private static Entity ReadEntity(JsonElement row)
        {
            var kind = row.GetProperty("kind").GetString() switch
            {
                "hero" => EntityKind.Hero,
                "minion" => EntityKind.Minion,
                "neutral" => EntityKind.Neutral,
                _ => throw new InvalidOperationException("unsupported neutral fixture entity kind"),
            };
            var unitType = (UnitType)row.GetProperty("unitType").GetInt32();
            var team = (Team)row.GetProperty("team").GetInt32();
            var pos = row.GetProperty("position");
            return new Entity(row.GetProperty("netid").GetUInt32(), kind, unitType, team,
                new Position2(pos[0].GetDouble(), pos[1].GetDouble()));
        }

        private static void ApplyEntity(JsonElement row, Entity entity)
        {
            entity.Name = row.GetProperty("name").GetString() ?? entity.Name;
            if (row.TryGetProperty("champion_name", out var champion))
                entity.ChampionName = champion.GetString() ?? entity.ChampionName;
            entity.Health = row.GetProperty("hp").GetDouble();
            entity.MaxHealth = row.GetProperty("max_hp").GetDouble();
            entity.Mana = row.GetProperty("mana").GetDouble();
            entity.MaxMana = row.GetProperty("max_mana").GetDouble();
            if (row.TryGetProperty("mana_regen", out var manaRegen))
                entity.ManaRegen = manaRegen.GetDouble();
            if (row.TryGetProperty("mp_regen", out var mpRegen))
                entity.ManaRegen = mpRegen.GetDouble();
            entity.AttackDamage = row.GetProperty("atk_damage").GetDouble();
            entity.Armor = row.GetProperty("armor").GetDouble();
            entity.SpellBlock = row.GetProperty("spell_block").GetDouble();
            entity.Speed = row.GetProperty("speed").GetDouble();
            entity.Radius = row.GetProperty("radius").GetDouble();
            entity.AttackRange = row.GetProperty("atk_range").GetDouble();
            entity.AttackPeriod = row.GetProperty("atk_period").GetDouble();
            entity.AttackWindup = row.GetProperty("windup").GetDouble();
            if (entity.UnitType == UnitType.Monster)
                entity.AttackPeriod = Math.Clamp(entity.AttackPeriod * row.GetProperty("attack_speed_mod").GetDouble(), 0.4, 5.0);
            else if (entity.ChampionName == "Annie" && Math.Abs(entity.AttackPeriod - 1.6 / row.GetProperty("attack_speed_mod").GetDouble()) < 1e-7)
            {
                entity.AttackPeriod = Math.Clamp(1.6 * (1 + 0.07999999821186066) / row.GetProperty("attack_speed_mod").GetDouble(), 0.4, 5.0);
                entity.AttackWindup = entity.AttackPeriod * 0.195794753;
            }
            entity.MissileSpeed = row.GetProperty("missile_speed").GetDouble();
            entity.HealthRegen = row.GetProperty("hp_regen").GetDouble();
            var actorName = entity.Kind == EntityKind.Neutral ? entity.Name : entity.ChampionName;
            entity.AttackSpeedMod = entity.UnitType == UnitType.Monster ? 1.0 : row.GetProperty("attack_speed_mod").GetDouble();
            if (row.TryGetProperty("facing", out var facing) && facing.ValueKind == JsonValueKind.Array)
                entity.Facing = new Position2(facing[0].GetDouble(), facing[1].GetDouble());
            if (row.TryGetProperty("ability_power", out var abilityPower)) entity.AbilityPower = abilityPower.GetDouble();
            if (row.TryGetProperty("skill_points", out var skillPoints)) entity.SkillPoints = skillPoints.GetInt32();
            if (row.TryGetProperty("gold", out var gold)) entity.Gold = gold.GetDouble();
            if (row.TryGetProperty("exp", out var experience)) entity.Experience = experience.GetDouble();
            if (row.TryGetProperty("cooldown_mod", out var cooldownMod)) entity.CooldownMod = cooldownMod.GetDouble();
            if (row.TryGetProperty("life_steal", out var lifeSteal)) entity.LifeSteal = lifeSteal.GetDouble();
            if (row.TryGetProperty("spell_vamp", out var spellVamp)) entity.SpellVamp = spellVamp.GetDouble();
            if (row.TryGetProperty("crit_chance", out var critChance)) entity.CritChance = critChance.GetDouble();
            if (row.TryGetProperty("crit_multiplier", out var critMultiplier)) entity.CritMultiplier = critMultiplier.GetDouble();
            if (row.TryGetProperty("armor_pen_flat", out var armorPenFlat)) entity.ArmorPenetrationFlat = armorPenFlat.GetDouble();
            if (row.TryGetProperty("armor_pen_percent", out var armorPenPercent)) entity.ArmorPenetrationPercent = armorPenPercent.GetDouble();
            if (row.TryGetProperty("magic_pen_flat", out var magicPenFlat)) entity.MagicPenetrationFlat = magicPenFlat.GetDouble();
            if (row.TryGetProperty("magic_pen_percent", out var magicPenPercent)) entity.MagicPenetrationPercent = magicPenPercent.GetDouble();
            if (row.TryGetProperty("dodge", out var dodge)) entity.Dodge = dodge.GetDouble();
            if (row.TryGetProperty("god_mode", out var godMode)) entity.GodMode = godMode.GetBoolean();
            if (row.TryGetProperty("cooldowns_disabled", out var cooldownsDisabled)) entity.CooldownsDisabled = cooldownsDisabled.GetBoolean();
            if (row.TryGetProperty("acquisition_range", out var acquisitionRange)) entity.AcquisitionRange = acquisitionRange.GetDouble();
            if (row.TryGetProperty("spell_ranks", out var spellRanks))
                entity.SpellRanks = spellRanks.EnumerateArray().Select(value => value.GetInt32()).ToList();
            if (row.TryGetProperty("spell_cooldowns", out var spellCooldowns))
                entity.SpellCooldowns = spellCooldowns.EnumerateArray().Select(value => value.GetDouble()).ToList();
            if (row.TryGetProperty("summoner_cooldowns", out var summonerCooldowns))
                entity.SummonerCooldowns = summonerCooldowns.EnumerateArray().Select(value => value.GetDouble()).ToList();
            if (row.TryGetProperty("item_cooldowns", out var itemCooldowns))
                entity.ItemCooldowns = itemCooldowns.EnumerateArray().Select(value => value.GetDouble()).ToList();
            entity.Alive = row.GetProperty("alive").GetBoolean();
            entity.Created = row.GetProperty("created").GetBoolean();
            entity.Visible = row.GetProperty("visible").GetBoolean();
            entity.Level = row.GetProperty("level").GetInt32();
            if (row.TryGetProperty("neutral_player_level", out var playerLevel))
                entity.NeutralPlayerLevel = playerLevel.GetInt32();
            entity.NeutralState = row.GetProperty("neutral_state").GetString() ?? entity.NeutralState;
            entity.NeutralHome = ReadPosition3(row.GetProperty("neutral_home"));
            entity.NeutralGroupPosition = ReadPosition3(row.GetProperty("neutral_group_pos"));
            entity.NeutralFacePosition = ReadPosition3(row.GetProperty("neutral_face_pos"));
            entity.CampId = row.GetProperty("camp_id").GetInt32();
            entity.NeutralMember = row.GetProperty("neutral_member").GetInt32();
            if (row.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
                entity.AttackTarget = row.GetProperty("attack_target").GetUInt32();
            entity.Buffs.Clear();
            if (row.TryGetProperty("buffs", out var buffs) && buffs.ValueKind == JsonValueKind.Array)
            {
                foreach (var buff in buffs.EnumerateArray())
                    entity.Buffs.Add(ReadBuff(buff));
            }
        }

        private static BuffState ReadBuff(JsonElement row)
        {
            var payload = new BuffPayload();
            if (row.TryGetProperty("payload", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var value in values.EnumerateObject())
                {
                    switch (value.Name)
                    {
                        case "max_hp": payload.MaxHp = value.Value.GetDouble(); break;
                        case "attack_damage": payload.AttackDamage = value.Value.GetDouble(); break;
                        case "spell_block": payload.SpellBlock = value.Value.GetDouble(); break;
                        case "fx_netid": payload.FxNetId = value.Value.GetUInt32(); break;
                        case "action_disabled": payload.ActionDisabled = value.Value.GetBoolean(); break;
                        default: throw new InvalidOperationException("unsupported neutral fixture payload " + value.Name);
                    }
                }
            }
            return new BuffState(row.GetProperty("name").GetString()!, payload,
                (BuffType)row.GetProperty("type").GetInt32(), row.GetProperty("slot").GetInt32(),
                row.GetProperty("expires_at").GetDouble(), row.GetProperty("caster").GetUInt32(),
                row.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1,
                row.TryGetProperty("max_stack", out var maxStack) && maxStack.ValueKind != JsonValueKind.Null
                    ? maxStack.GetInt32() : null);
        }

        private static Position3? ReadPosition3(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3
                ? new Position3(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble())
                : null;
    }

    private sealed class CaptureQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
        public void Clear() => Packets.Clear();
    }

    private sealed class FixtureRandom(IEnumerable<JsonElement> calls) : IRandomSource
    {
        private readonly Queue<JsonElement> _expected = new(calls.Select(call => call.Clone()));
        private readonly List<ActualRandomCall> _consumed = new();

        public double NextDouble() => Consume("random", null, null, null).Value;

        public int NextIndex(int count)
        {
            var call = Consume("choice", count, null, null);
            return checked((int)call.Value);
        }

        public double Uniform(double minimum, double maximum) =>
            Consume("uniform", null, minimum, maximum).Value;

        /// <summary>
        /// The calls consumed since the last <see cref="ClearConsumed"/>, in the fixture's row shapes. Record mode writes them as the
        /// expectation, so the replay asserts what this build draws (batch 194: the tape was a kept input the build no longer drew).
        /// </summary>
        public object[] ConsumedRows() => _consumed.Select(call => call.Method switch
        {
            "choice" => (object)new { method = call.Method, count = call.Count!.Value, index = (int)call.Value },
            "uniform" => new { method = call.Method, minimum = call.Minimum!.Value, maximum = call.Maximum!.Value, value = call.Value },
            _ => new { method = call.Method, value = call.Value },
        }).ToArray();

        private ActualRandomCall Consume(string method, int? count, double? minimum, double? maximum)
        {
            if (_expected.Count == 0)
                throw new InvalidOperationException($"unexpected RNG call {method}");
            var expected = _expected.Dequeue();
            TestAssert.Equal(method, expected.GetProperty("method").GetString(), "RNG method");
            if (method == "choice")
                TestAssert.Equal(count!.Value, expected.GetProperty("count").GetInt32(), "RNG choice count");
            if (method == "uniform")
            {
                TestAssert.Near(minimum!.Value, expected.GetProperty("minimum").GetDouble(), 1e-12, "RNG minimum");
                TestAssert.Near(maximum!.Value, expected.GetProperty("maximum").GetDouble(), 1e-12, "RNG maximum");
            }
            var value = method == "choice"
                ? expected.GetProperty("index").GetDouble()
                : expected.GetProperty("value").GetDouble();
            var actual = new ActualRandomCall(method, count, minimum, maximum, value);
            _consumed.Add(actual);
            return actual;
        }

        public void AssertCalls(JsonElement expectedCalls, string context)
        {
            var expected = expectedCalls.EnumerateArray().ToArray();
            TestAssert.Equal(expected.Length, _consumed.Count, context + " count");
            for (var index = 0; index < expected.Length; index++)
            {
                var row = expected[index];
                var actual = _consumed[index];
                TestAssert.Equal(row.GetProperty("method").GetString(), actual.Method,
                    context + " method");
                if (actual.Method == "choice")
                    TestAssert.Equal(row.GetProperty("count").GetInt32(), actual.Count!.Value,
                        context + " choice count");
                if (actual.Method == "uniform")
                {
                    TestAssert.Near(row.GetProperty("minimum").GetDouble(), actual.Minimum!.Value, 1e-12,
                        context + " minimum");
                    TestAssert.Near(row.GetProperty("maximum").GetDouble(), actual.Maximum!.Value, 1e-12,
                        context + " maximum");
                }
                var expectedValue = actual.Method == "choice"
                    ? row.GetProperty("index").GetDouble()
                    : row.GetProperty("value").GetDouble();
                TestAssert.Near(expectedValue, actual.Value, 1e-12,
                    context + " value");
            }
            _consumed.Clear();
        }

        public void ClearConsumed() => _consumed.Clear();

        private readonly record struct ActualRandomCall(string Method, int? Count,
            double? Minimum, double? Maximum, double Value);
    }

    private sealed class ConstantRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    private sealed class NeutralBuffServices(NeutralHooks hooks, CombatService combat,
        ISpellEffectSink effects, ICombatPacketQueue queue, WorldState world) : IBuffServices
    {
        public ICombatBuffHooks Combat { get; } = new CombatBuffHooks(hooks, combat);
        public ICastBuffHooks Casts { get; } = new CastHooks();
        public IChampionBuffHooks Champions { get; private set; } = new ChampionHooks();
        public IBuffEffectHooks Effects { get; } = new BuffEffectService(effects,
            new ProtocolBuffVisionSink(world.Get, world.Allocate, queue),
            entity => world.Clock.SyncId());
        public IItemBuffHooks Items { get; } = new ItemHooks();

        /// <summary>
        /// Batch 173: forwards a buff's update and removal to the shipped unit character scripts, the way production's
        /// <c>MatchGameplayHooks.ChampionBuffView</c> fans them out to <c>UnitHooks</c>. The camp members' own script buffs
        /// (the golem's <c>CrestoftheAncientGolem</c>/<c>HPByPlayerLevel</c>) tick only when this boundary reaches them.
        /// </summary>
        public void AttachUnitScripts(BbUnitCharacterModule module, IBbHookSink sink) =>
            Champions = new ScriptFanout(module, sink);

        private sealed class CombatBuffHooks(NeutralHooks hooks, CombatService combat) : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => hooks.IsActionBlocked(unit);
            public double HealingMultiplier(Entity unit) => combat.HealingReceivedMultiplier(unit);
            public void InterruptBasicAttack(Entity unit) => combat.InterruptBasicAttack(unit);
            public void InterruptBasicAttack(Entity unit, out bool cancelledWindup) =>
                cancelledWindup = combat.InterruptBasicAttack(unit) > 0;
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) => combat.ApplyHit(request.SourceNetId,
                request.TargetNetId, request.Amount, request.DamageType, request.SourceKind);
            public void Kill(Entity unit, uint killerNetId = 0) => combat.Kill(unit, killerNetId);
        }
        private sealed class CastHooks : ICastBuffHooks { public void CancelPendingCast(Entity hero) => hero.PendingCast = null; }
        private sealed class ChampionHooks : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) { }
            public void OnBuffRemoved(Entity unit, BuffState buff) { }
            public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
        }

        /// <summary>The champion-buff boundary forwarded to the unit character router (production's <c>Sinks</c> fanout).</summary>
        private sealed class ScriptFanout(BbUnitCharacterModule module, IBbHookSink sink) : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) => sink.OnBuffTick(unit, buff, dt);
            // The module's own runtime answers a script buff's OnBuffDeactivate (production routes the same call through
            // MatchGameplayHooks.ChampionBuffView.OnBuffRemoved → SpellSystem.OnUnitBuffRemoved).
            public void OnBuffRemoved(Entity unit, BuffState buff) => module.OnBuffRemoved(unit, buff);
            public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
        }
        private sealed class ItemHooks : IItemBuffHooks
        {
            public bool HasItem(Entity unit, int itemId) => false;
            public void SyncItemSlow(Entity target) { }
        }
    }

    private sealed class NeutralStatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            HeroAttackTimings.ApplyRefresh(hero, GameData.Default);
        public IReadOnlyList<double> ManaCosts(Entity hero) =>
            GameData.Default.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire) { }
    }
    private sealed class NeutralStatsTransport : IHeroStatsTransport
    {
        private Action<HeroStatsReplicationSnapshot>? _push;
        public void Attach(Action<HeroStatsReplicationSnapshot> push) =>
            _push = push ?? throw new ArgumentNullException(nameof(push));
        public void Push(HeroStatsReplicationSnapshot snapshot) =>
            (_push ?? throw new InvalidOperationException("hero stat transport is not attached"))(snapshot);
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) { }
    }

    private sealed class NeutralHooks : ICombatHooks
    {
        private NeutralAiService? _ai;
        private BuffLifecycle? _lifecycle;
        private IBbHookSink? _unitScripts;
        public void Attach(NeutralAiService ai, BuffLifecycle lifecycle) { _ai = ai; _lifecycle = lifecycle; }
        /// <summary>
        /// Batch 173: the camp members' character hooks (batch 159 part 2), the fanout production's
        /// <c>MatchGameplayHooks</c> runs through <c>Sinks</c>. <c>CharOnHitUnit</c> is the monster's own x1.43.
        /// </summary>
        public void AttachUnitScripts(IBbHookSink sink) => _unitScripts = sink;
        public bool IsInvulnerable(Entity unit) => false;
        public bool IsGodMode(Entity unit) => unit.GodMode;
        public bool IsTargetable(Entity unit, Entity? observer) => unit.Alive &&
            !unit.Buffs.Any(buff => buff.Payload.Untargetable);
        public void OnBoxDamageSensor(Entity? source, Entity target, DamageSourceKind? sourceKind) { }
        public void OnBasicAttackPreDamage(Entity source, Entity target) { }
        public bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind, bool force) => false;
        public bool TryFinishExistingRevival(Entity unit, uint killerNetId) => false;
        public PreDamageResult PreDamage(Entity? source, Entity target, double amount, DamageType damageType, DamageSourceKind? sourceKind) => PreDamageResult.Unhandled;
        public double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source, double amount, DamageType damageType, DamageSourceKind? sourceKind) =>
            _unitScripts?.OnPreDamage(target, source, amount, damageType, sourceKind) ?? amount;
        public void OnBasicAttackStart(Entity attacker, Entity target) { }
        public void BreakAttackStealth(Entity attacker) { }
        // Batch 159 part 2's CharOnHitUnit (the camp monsters' own x1.43) runs from the script sink, the way
        // MatchGameplayHooks.ModifyBasicAttackDamage runs it for a unit a character module owns.
        public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) =>
            _unitScripts is { } scripts && scripts.OwnsUnit(source)
                ? scripts.ModifyBasicAttackDamage(source, target, damage, false, BasicAttackOutcome.Normal)
                : damage;
        public double ModifyOutgoingDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyIncomingDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double AbsorbDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double PreprocessItemDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyHealthRegeneration(Entity hero, double amount, double dt) => amount;
        public double ModifyManaRegeneration(Entity hero, double amount, double dt) => amount;
        public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage, DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic) { }
        public void OnMoltenShieldReflect(Entity source, Entity target) { }
        public void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted) { }
        public void RemoveBuff(Entity target, int slot) => _lifecycle?.RemoveBuff(target, slot);
        public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) { }
        public void OnBasicAttackMiss(Entity source) { }
        public void OnPendingHitCanceled(PendingHit hit) { }
        public void OnPendingCastCanceled(Entity unit) { }
        public bool IsActionBlocked(Entity unit) => unit.Buffs.Any(buff => buff.Payload.ActionDisabled);
        public void OnDeath(Entity unit, uint killerNetId) { }
        public void OnPetDeath(Entity pet) { }
        public void OnNeutralDeath(Entity neutral, uint killerNetId) { }
        public void OnItemKill(Entity? killer, Entity victim) { }
        public void OnItemDeath(Entity hero) { }
        public void OnKill(Entity? killer, Entity victim) { }
        public void OnRespawn(Entity unit) { }
        public void OnLevelChanged(Entity hero) { }
        public void OnNeutralDamaged(Entity target, Entity? source) { }
        public void OnItemPassiveTick(double now, double dt) { }
        public void OnPeriodicDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? sourceKind) { }
        public void OnEndGame(Team winner) { }
        public void HaltAllAi() { }
    }
}
