using System.Globalization;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static class TibbersChecks
{
    [Check("pets-b6-tibbers-retains-target-beyond-acquisition")]
    public static void TibbersRetainsTargetBeyondAcquisition()
    {
        // PET-02: Pet.lua AI_PET_ATTACK uses GetTarget, not FindTargetInAcR.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", solo: true, readySettleSeconds: 0);
        runtime.Session.CreateWorld();
        var owner = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        owner.Position = new Position2(7000, 7000);
        var pet = AnnieChecks.SummonTibbers(runtime, owner, 1);
        var enemy = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Chaos, pet.Position + new Position2(650, 0))
        { Health = 1000, MaxHealth = 1000, Created = true };
        runtime.World.Register(enemy);
        var visible = true;
        var ai = new PetAiService(runtime.World, runtime.Movement, runtime.Combat,
            runtime.Effects, canSee: (_, target) => target == enemy && visible);
        void Decide()
        {
            ai.ScanNow(pet);
            ai.Tick(0, 0);
        }
        TestAssert.Near(625, GameData.Default.Champion("AnnieTibbers").AcquisitionRange, 0,
            "shipped AnnieTibbers acquisition radius");
        Decide();
        TestAssert.Equal((uint?)null, pet.AttackTarget, "650 is outside new acquisition");
        enemy.Position = pet.Position + new Position2(620, 0);
        Decide();
        TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, "acquire at 620");
        enemy.Position = pet.Position + new Position2(650, 0);
        Decide();
        TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, "retain the visible target at 650");
        TestAssert.True(pet.Target is not null || pet.Waypoints.Count > 0, "retained target keeps pursuit");
        visible = false;
        Decide();
        TestAssert.Equal((uint?)null, pet.AttackTarget, "sight loss still releases the distant target");
    }

    [Check("spells-annie-tibbers-data-regen-and-exclusions")]
    public static void TibbersRegeneratesWithoutAdmittingOtherPets()
    {
        foreach (var rank in new[] { 1, 2, 3 })
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, orderChampion: "Annie", chaosChampion: "Orianna",
                solo: true, readySettleSeconds: 0.0);
            runtime.Session.CreateWorld();
            var owner = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
            var tibbers = AnnieChecks.SummonTibbers(runtime, owner, rank, new Position2(7000, 7000));
            TestAssert.Near(.5, tibbers.HealthRegen, 1e-9, "AnnieTibbers BaseStaticHPRegen");
            tibbers.Health -= 10;
            var excluded = new List<Entity>();
            foreach (var kind in new[] { EntityKind.YorickGhoul, EntityKind.YorickRevenant,
                         EntityKind.ShacoClone, EntityKind.ShacoBox, EntityKind.Neutral })
            {
                var unit = new Entity(runtime.World.Allocate(UnitType.Monster), kind,
                    UnitType.Monster, Team.Order, tibbers.Position)
                {
                    Name = "AnnieTibbers", Health = 50, MaxHealth = 100, HealthRegen = .5,
                    Created = true,
                };
                runtime.World.Register(unit);
                excluded.Add(unit);
            }
            runtime.Combat.CombatTick(10);
            TestAssert.Near(tibbers.MaxHealth - 5, tibbers.Health, 1e-9, $"rank {rank} restores five HP in ten seconds");
            foreach (var unit in excluded)
                TestAssert.Near(50, unit.Health, 1e-9, unit.Kind + " stays outside ordinary pet regen");
            tibbers.Health = tibbers.MaxHealth - 1;
            runtime.Combat.CombatTick(10);
            TestAssert.Near(tibbers.MaxHealth, tibbers.Health, 1e-9, "regen caps at maximum HP");
            tibbers.Health -= 10;
            tibbers.Buffs.Add(new BuffState("RegenBlocked", new BuffPayload { HealingReceivedMult = -1 }));
            runtime.Combat.CombatTick(1);
            TestAssert.Near(tibbers.MaxHealth - 10, tibbers.Health, 1e-9, "unhealable Tibbers is excluded");
        }
    }

    [Check("pets.tibbers-native-attack-timing")]
    public static void TibbersUsesNativeDefaultAttackTiming()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", chaosChampion: "Orianna",
            solo: true, readySettleSeconds: 0.0);
        runtime.Session.CreateWorld();
        var owner = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        var tibbers = AnnieChecks.SummonTibbers(runtime, owner, 1, new Position2(7000.0, 7000.0));
        TestAssert.Near(1.0, tibbers.AttackSpeedMod, 1e-9,
            "Tibbers uses the native default live attack-speed multiplier");
        TestAssert.Near(1.6, tibbers.AttackPeriod, 1e-9,
            "Tibbers keeps the 1.6-second native base period");
        TestAssert.Near(0.633279995918274, tibbers.AttackWindup, 1e-9,
            "Tibbers uses the shipped cast offset against the native period");
    }


    [Check("pets-w18-tibbers-skips-wards-in-acquisition-but-takes-an-ordered-one")]
    public static void TibbersSkipsAWardInAcquisitionButTakesAnOrderedOne()
    {
        // Owner decision, 18 October 2026 (evening): a pet's automatic acquisition never takes a ward (CombatService.IsAutoAcquirable);
        // an explicit attack order on a ward still works. The ward is nearer than the minion, so it would have been picked first.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", solo: true, readySettleSeconds: 0);
        runtime.Session.CreateWorld();
        var owner = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        owner.Position = new Position2(7000, 7000);
        var pet = AnnieChecks.SummonTibbers(runtime, owner, 1);
        var ward = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Ward, UnitType.Monster, Team.Chaos,
            pet.Position + new Position2(300, 0))
        { Health = 3, MaxHealth = 3, Created = true, IsWard = true };
        runtime.World.Register(ward);
        var minion = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos,
            pet.Position + new Position2(500, 0))
        { Health = 1000, MaxHealth = 1000, Created = true };
        runtime.World.Register(minion);
        var ai = new PetAiService(runtime.World, runtime.Movement, runtime.Combat,
            runtime.Effects, canSee: (_, _) => true);
        void Decide()
        {
            ai.ScanNow(pet);
            ai.Tick(0, 0);
        }
        Decide();
        TestAssert.Equal((uint?)minion.NetId, pet.AttackTarget, "the nearer ward is skipped and the minion behind it is acquired");
        TestAssert.True(ai.Command(owner, 5, new Position2(0, 0), ward.NetId), "an explicit attack order on the ward is accepted");
        Decide();
        TestAssert.Equal((uint?)ward.NetId, pet.AttackTarget, "and the ordered ward is kept as the target");
    }


    private static void Replay(JsonElement testCase, bool compareFixture = true)
    {
        var name = testCase.GetProperty("name").GetString() ?? "tibbers";
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        foreach (var raw in testCase.GetProperty("initial").EnumerateArray()) world.Register(ReadEntity(raw));
        // R's resolve creates the ground effect before the pet starts ticking;
        // that effect consumes the first missile-range id even though its FX
        // packet is outside this pet-only replay stream.
        world.Allocate(UnitType.Missile);

        var grid = NavigationGrid.LoadDefault();
        var movement = new MovementSystem(world, grid);
        var queue = new ReplayQueue();
        var syncIds = new ReplicationSyncCounter();
        var data = GameData.Default;
        // The archived buff records only the FX id. Reconstruct the immutable
        // cast-time aura payload that production R now attaches at summon.
        var pet = world.Get(testCase.GetProperty("pet").GetUInt32())!;
        var owner = world.Get(pet.OwnerNetId)!;
        var burning = pet.Buffs.Single(buff => buff.Name == "InfernalGuardianBurning");
        burning.Payload.Born = 0.0;
        burning.Payload.DamagePerTick = 35.0 +
            data.Spell("InfernalGuardian").Coefficient2 * owner.AbilityPower;
        burning.Payload.SpellOwnerNetId = owner.NetId;
        burning.Payload.Rank = testCase.GetProperty("rank").GetInt32();
        burning.Payload.SourceKind = DamageSourceKind.SpellAoe;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new StatsHooks(), new ProtocolHeroStatsTransport(queue, syncIds));
        var hooks = new CombatHooks(world);
        var transport = new ProtocolCombatTransport(queue, stats,
            unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
            syncIds);
        var combat = new CombatService(world, hooks, transport, new FixedRandomSource(0.5), stats);
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
            new VisionSink(world.Get, world.Allocate), _ => world.Clock.SyncId());
        var buffServices = new BuffServices(hooks, buffEffects, combat);
        var lifecycle = new BuffLifecycle(world, stats, buffServices);
        hooks.Attach(lifecycle, buffEffects);
        // Batch 146: a pet runs its shipped Pet.luaobj on PetAiService, so a replay drives that and
        // not the retired machine (TibbersAiSystem's decision half, deleted with the hand-written pet
        // AIs) the fixture was recorded from. spells-tibbers-ai-corpus therefore compares the script's
        // world against that machine's packets and is a moved corpus check until the next full run
        // re-records it from this harness - it is not re-recorded here.
        var pets = new PetAiService(world, movement, combat, effects, data);
        var cipher = LegacyBlowfish.CreateDefault();
        syncIds.Next(owner.NetId);
        syncIds.Next(pet.NetId);
        pet.PetState = new PetState(CloneKind.Tibbers, owner.NetId)
        {
            Rank = testCase.GetProperty("rank").GetInt32(), BornAt = 0.0,
        };
        // The scene's selected target reaches the pet as its owner's hard attack order (the 0x75 pet
        // control packet): the fixture carries the selection only as the pet's attack target, and
        // Pet.lua's own acquisition would take the nearer body. In the engaging state the order
        // leaves behind the script keeps the ordered one (PET-02 reads GetTarget, not
        // FindTargetInAcR), which is what "its original selected target" means.
        if (pet.AttackTarget is uint selected && world.Get(selected) is { } selection)
        {
            pet.AttackTarget = null;
            TestAssert.True(pets.Command(owner, 5, selection.Position, selection.NetId),
                "the scene's selected target is taken by a hard attack order");
        }
        var stickyBasicHit = false;

        foreach (var frame in testCase.GetProperty("frames").EnumerateArray())
        {
            ApplyActions(frame.GetProperty("actions"), world, combat, lifecycle);
            var dt = frame.GetProperty("dt").GetDouble();
            world.Clock.Advance(dt);
            movement.Tick(dt);
            combat.CombatTick(dt);
            pets.Tick(world.Clock.Now(), dt);
            foreach (var unit in world.Entities.Values.ToArray())
                lifecycle.TickBuffs(unit, dt);

            if (name == "sticky-target")
            {
                var controlledPet = world.Get(testCase.GetProperty("pet").GetUInt32())!;
                var originalTarget = world.Get(0x40010001)!;
                var blockingMinion = world.Get(0x40010002)!;
                var clearance = controlledPet.Radius + blockingMinion.Radius;
                TestAssert.True(DistanceSquared(controlledPet.Position, blockingMinion.Position) >=
                    clearance * clearance,
                    "Tibbers pursuit keeps its body outside the intervening minion shell");
                if (frame.GetProperty("frame").GetInt32() == 0)
                {
                    TestAssert.Equal(originalTarget.NetId, controlledPet.AttackTarget,
                        "body detour retains the original selected target");
                        TestAssert.True(combat.CanAttack(controlledPet, originalTarget) &&
                            controlledPet.Waypoints.Count == 0 && controlledPet.Target is null,
                            "Tibbers holds inside shared reach of the original target");
                }
                stickyBasicHit |= queue.Packets.Any(packet =>
                    packet.Plaintext.Length >= 10 && packet.Plaintext.Span[0] == 0x68 &&
                    packet.Plaintext.Span[5] == 0x84 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == controlledPet.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == originalTarget.NetId);
            }

            if (compareFixture)
                CompareFrame(frame, world, combat, pets.LastDestinations, name, cipher, queue);
            queue.Clear();
        }
        if (name == "sticky-target")
            TestAssert.True(stickyBasicHit,
                "Tibbers hits its original target without crossing the blocking body");
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private static void ApplyActions(JsonElement actions, WorldState world,
        CombatService combat, BuffLifecycle lifecycle)
    {
        foreach (var action in actions.EnumerateArray())
        {
            var unit = world.Get(action.GetProperty("netid").GetUInt32())
                ?? throw new InvalidDataException("Tibbers action references an unknown unit");
            switch (action.GetProperty("kind").GetString())
            {
                case "position":
                    unit.Position = Point(action.GetProperty("value"));
                    break;
                case "kill":
                    combat.Kill(unit, action.GetProperty("killer").GetUInt32());
                    break;
                case "buff":
                    lifecycle.AddBuff(unit, action.GetProperty("name").GetString()!,
                        (BuffType)action.GetProperty("type").GetInt32(),
                        action.GetProperty("duration").GetDouble(),
                        action.GetProperty("caster").GetUInt32());
                    break;
                default:
                    throw new InvalidDataException($"unknown Tibbers action {action.GetProperty("kind")}");
            }
        }
    }

    private static void CompareFrame(JsonElement frame, WorldState world,
        CombatService combat, IReadOnlyDictionary<uint, Position2> lastDestinations, string name,
        LegacyBlowfishCipher cipher, ReplayQueue queue)
    {
        if (FixtureRecorder.Enabled)
        {
            SpellReplayRecording.Fields(frame, new JsonObject
            {
                ["entities"] = SpellReplayRecording.Entities(frame.GetProperty("entities"), world),
                ["pendingHits"] = SpellReplayRecording.Hits(combat.PendingHits),
                ["lastDest"] = SpellReplayRecording.Map(lastDestinations),
                ["nextAura"] = SpellReplayRecording.Map(new Dictionary<uint, double>()),
            });
            SpellReplayRecording.Packets(frame.GetProperty("packets"), queue.Packets);
            return;
        }

        var frameNumber = frame.GetProperty("frame").GetInt32();
        var label = $"{name} frame {frameNumber}";
        foreach (var source in frame.GetProperty("entities").EnumerateArray())
        {
            var entity = world.Get(source.GetProperty("netid").GetUInt32())
                ?? throw new InvalidOperationException($"{label}: missing entity");
            var raw = source;
            Near(raw.GetProperty("hp").GetDouble(),
                entity.Health, label + " hp (native zero floor)");
            TestAssert.Equal(raw.GetProperty("alive").GetBoolean(), entity.Alive, label + " alive");
            Near(raw.GetProperty("position")[0].GetDouble(), entity.Position.X, label + " x");
            Near(raw.GetProperty("position")[1].GetDouble(), entity.Position.Z, label + " z");
            var target = raw.GetProperty("attack_target");
            TestAssert.Equal(target.ValueKind == JsonValueKind.Null ? (uint?)null : target.GetUInt32(),
                entity.AttackTarget, label + " attack target");
            Near(raw.GetProperty("attack_cd").GetDouble(), entity.AttackCooldown, label + " attack cd");
            ComparePoints(raw.GetProperty("waypoints"), entity.Waypoints, label + " waypoints");
            CompareBuffs(raw.GetProperty("buffs"), entity.Buffs, label + " buffs");
        }

        var expectedHits = frame.GetProperty("pendingHits").EnumerateArray().ToArray();
        TestAssert.Equal(expectedHits.Length, combat.PendingHits.Count, label + " pending hits");
        for (var i = 0; i < expectedHits.Length; i++)
        {
            var expected = expectedHits[i];
            var actual = combat.PendingHits[i];
            Near(expected[0].GetDouble(), actual.WindupRemaining, label + $" hit {i} windup");
            Near(expected[1].GetDouble(), actual.ProjectilePosition.X, label + $" hit {i} x");
            Near(expected[2].GetDouble(), actual.ProjectilePosition.Z, label + $" hit {i} z");
            TestAssert.Equal(expected[3].GetUInt32(), actual.SourceNetId, label + $" hit {i} source");
            TestAssert.Equal(expected[4].GetUInt32(), actual.TargetNetId, label + $" hit {i} target");
            Near(expected[5].GetDouble(), actual.Damage, label + $" hit {i} damage");
            Near(expected[6].GetDouble(), actual.MissileSpeed, label + $" hit {i} speed");
        }

        CompareDestinations(frame.GetProperty("lastDest"), lastDestinations, label);
        // 19 October 2026: the aura is the InfernalGuardianBurning script buff, so the pet-only replay world has no pulse clock to compare.
        CompareAuras(frame.GetProperty("nextAura"), new Dictionary<uint, double>(), label);
        ComparePackets(frame.GetProperty("packets"), queue.Packets, cipher, label);
    }

    private static void CompareDestinations(JsonElement expected,
        IReadOnlyDictionary<uint, Position2> actual, string label)
    {
        TestAssert.Equal(expected.EnumerateObject().Count(), actual.Count, label + " destinations");
        foreach (var property in expected.EnumerateObject())
        {
            var id = uint.Parse(property.Name, CultureInfo.InvariantCulture);
            TestAssert.True(actual.TryGetValue(id, out var point), label + " destination id");
            Near(property.Value[0].GetDouble(), point.X, label + " destination x");
            Near(property.Value[1].GetDouble(), point.Z, label + " destination z");
        }
    }

    private static void CompareAuras(JsonElement expected,
        IReadOnlyDictionary<uint, double> actual, string label)
    {
        TestAssert.Equal(expected.EnumerateObject().Count(), actual.Count, label + " aura timers");
        foreach (var property in expected.EnumerateObject())
        {
            var id = uint.Parse(property.Name, CultureInfo.InvariantCulture);
            TestAssert.True(actual.TryGetValue(id, out var value), label + " aura id");
            Near(property.Value.GetDouble(), value, label + " aura time");
        }
    }

    private static void ComparePackets(JsonElement expected,
        IReadOnlyList<PlaintextPacket> actual, LegacyBlowfishCipher cipher, string label,
        IReadOnlySet<uint>? deadEntities = null)
    {
        if (FixtureRecorder.Enabled) { SpellReplayRecording.Packets(expected, actual); return; }
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
        var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unitType").GetInt32(),
            (Team)raw.GetProperty("team").GetInt32(), Point(raw.GetProperty("position")))
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
            LastAnchorTime = raw.GetProperty("last_anchor_time").GetDouble(),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            entity.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        entity.Waypoints = raw.GetProperty("waypoints").EnumerateArray().Select(Point).ToList();
        entity.AnchorPath = raw.GetProperty("anchor_path").EnumerateArray().Select(Point).ToList();
        if (raw.TryGetProperty("gold_reward", out var goldReward))
            entity.GoldReward = goldReward.GetDouble();
        if (raw.TryGetProperty("global_gold_reward", out var globalGoldReward))
            entity.GlobalGoldReward = globalGoldReward.GetDouble();
        if (raw.TryGetProperty("xp_reward", out var experienceReward))
            entity.ExperienceReward = experienceReward.GetDouble();
        LoadBuffs(entity, raw.GetProperty("buffs"));
        return entity;
    }

    private static void LoadBuffs(Entity entity, JsonElement raw)
    {
        foreach (var value in raw.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (value.TryGetProperty("payload", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                SetBool(fields, "action_disabled", v => payload.ActionDisabled = v);
                SetBool(fields, "movement_disabled", v => payload.MovementDisabled = v);
                if (fields.TryGetProperty("fx_netid", out var fx)) payload.FxNetId = fx.GetUInt32();
            }
            entity.Buffs.Add(new BuffState(value.GetProperty("name").GetString()!, payload,
                (BuffType)value.GetProperty("type").GetInt32(), value.GetProperty("slot").GetInt32(),
                value.GetProperty("expires_at").GetDouble(), value.GetProperty("caster").GetUInt32(),
                value.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1));
        }
    }

    private static void SetBool(JsonElement value, string name, Action<bool> setter)
    {
        if (value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False)
            setter(field.GetBoolean());
    }

    private static void CompareBuffs(JsonElement expected, IList<BuffState> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var i = 0; i < actual.Count; i++)
        {
            var raw = expected[i];
            TestAssert.Equal(raw.GetProperty("name").GetString(), actual[i].Name, label + $" {i} name");
            Near(raw.GetProperty("expires_at").GetDouble(), actual[i].ExpiresAt, label + $" {i} expiry");
        }
    }

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var i = 0; i < actual.Count; i++)
        {
            Near(expected[i][0].GetDouble(), actual[i].X, label + $" {i} x");
            Near(expected[i][1].GetDouble(), actual[i].Z, label + $" {i} z");
        }
    }

    private static Position2 Point(JsonElement value) => value.GetArrayLength() == 3
        ? new Position2(value[0].GetDouble(), value[2].GetDouble())
        : new Position2(value[0].GetDouble(), value[1].GetDouble());

    private static void Near(double expected, double actual, string label) =>
        TestAssert.Near(expected, actual, 1e-6, label + $" expected {expected:R} actual {actual:R}");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    internal sealed class ReplayQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
        public void Clear() => Packets.Clear();
    }

    internal sealed class StatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) => hero.AttackPeriod = attackSpeedMod <= 0 ? 0 : 1.6 / attackSpeedMod;
        public IReadOnlyList<double> ManaCosts(Entity hero) => GameData.Default.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire) { }
    }

    internal sealed class CombatHooks(WorldState world) : ICombatHooks
    {
        private readonly WorldState _world = world;
        private BuffLifecycle? _lifecycle;
        private IBuffEffectHooks? _effects;
        public void Attach(BuffLifecycle lifecycle, IBuffEffectHooks effects) { _lifecycle = lifecycle; _effects = effects; }
        public bool IsInvulnerable(Entity unit) => unit.Buffs.Any(b => b.Type == BuffType.Invulnerability || b.Payload.Invulnerable);
        public bool IsGodMode(Entity unit) => unit.GodMode;
        public bool IsTargetable(Entity unit, Entity? observer) => unit.Alive && !unit.Buffs.Any(b => b.Payload.Untargetable);
        public void OnBoxDamageSensor(Entity? source, Entity target, DamageSourceKind? sourceKind) { }
        public void OnBasicAttackPreDamage(Entity source, Entity target) { }
        public bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind, bool force)
        {
            var shield = target.Buffs.FirstOrDefault(buff => buff.Payload.MagicShield > 0.0);
            if (shield is null) return false;
            _lifecycle?.RemoveBuff(target, shield.Slot);
            return true;
        }
        public bool TryFinishExistingRevival(Entity unit, uint killerNetId) => false;
        public bool TryPreventDeath(Entity unit, uint killerNetId) => false;
        public PreDamageResult PreDamage(Entity? source, Entity target, double amount, DamageType type, DamageSourceKind? kind) => PreDamageResult.Unhandled;
        public double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount, DamageType type, DamageSourceKind? kind) => amount;
        public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind) => amount;
        public void OnBasicAttackStart(Entity attacker, Entity target) { }
        public void BreakAttackStealth(Entity attacker) { }
        public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) => damage;
        public double ModifyOutgoingDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? kind) => damage;
        public double ModifyIncomingDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? kind) => damage;
        public double AbsorbDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? kind) => damage;
        public double PreprocessItemDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? kind) => damage;
        public double ModifyHealthRegeneration(Entity hero, double amount, double dt) => amount;
        public double ModifyManaRegeneration(Entity hero, double amount, double dt) => amount;
        public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage, DamageSourceKind? kind, bool reflected, bool periodic) { }
        public void OnMoltenShieldReflect(Entity source, Entity target) { }
        public void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted) =>
            _effects?.SendDamageShieldDelta(target, delta);
        public void RemoveBuff(Entity target, int slot) => _lifecycle?.RemoveBuff(target, slot);
        public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) { }
        public void OnBasicAttackMiss(Entity source) { }
        public void OnPendingHitCanceled(PendingHit hit) { }
        public void OnPendingCastCanceled(Entity unit) { }
        public void BeginRevival(Entity unit, uint casterNetId) { }
        public bool IsActionBlocked(Entity unit) => unit.Buffs.Any(b => b.Type is BuffType.Stun or BuffType.Invulnerability || b.Payload.ActionDisabled);
        public void OnDeath(Entity unit, uint killerNetId) { }
        public void OnPetDeath(Entity pet)
        {
            foreach (var buff in pet.Buffs.ToArray()) _lifecycle?.RemoveBuff(pet, buff.Slot);
            var owner = _world.Get(pet.OwnerNetId);
            if (owner is null) return;
            foreach (var buff in owner.Buffs.Where(buff => buff.Name == "InfernalGuardianTimer").ToArray())
                _lifecycle?.RemoveBuff(owner, buff.Slot);
        }
        public void OnNeutralDeath(Entity neutral, uint killerNetId) { }
        public void OnItemKill(Entity? killer, Entity victim) { }
        public void OnItemDeath(Entity hero) { }
        public void OnKill(Entity? killer, Entity victim) { }
        public void OnRespawn(Entity unit) { }
        public void OnLevelChanged(Entity hero) { }
        public void OnNeutralDamaged(Entity target, Entity? source) { }
        public void OnItemPassiveTick(double now, double dt) { }
        public void OnPeriodicDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind) { }
        public void OnEndGame(Team winner) { }
        public void HaltAllAi() { }
    }

    internal sealed class BuffServices : IBuffServices
    {
        public BuffServices(CombatHooks hooks, IBuffEffectHooks effects, CombatService combat,
            IChampionBuffHooks? champions = null)
        {
            Combat = new CombatBuffHooks(hooks, combat); Casts = new CastHooks();
            Champions = champions ?? new ChampionHooks();
            Effects = effects; Items = new ItemHooks();
        }
        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; }
        private sealed class CombatBuffHooks(CombatHooks hooks, CombatService combat) : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => hooks.IsActionBlocked(unit);
            public double HealingMultiplier(Entity unit) => 1.0;
            public void InterruptBasicAttack(Entity unit) => combat.InterruptBasicAttack(unit);
            public void InterruptBasicAttack(Entity unit, out bool cancelledWindup) =>
                cancelledWindup = combat.InterruptBasicAttack(unit) > 0;
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) => combat.ApplyHit(request.SourceNetId, request.TargetNetId, request.Amount, request.DamageType, request.SourceKind, sourceIsPeriodic: request.SourceIsPeriodic, spellOwnerNetId: request.SpellOwnerNetId == 0 ? null : request.SpellOwnerNetId);
            public void Kill(Entity unit, uint killerNetId = 0) => combat.Kill(unit, killerNetId);
        }
        private sealed class CastHooks : ICastBuffHooks { public void CancelPendingCast(Entity hero) => hero.PendingCast = null; }
        private sealed class ChampionHooks : IChampionBuffHooks { public void OnBuffTick(Entity unit, BuffState buff, double dt) { } public void OnBuffRemoved(Entity unit, BuffState buff) { } public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { } }
        private sealed class ItemHooks : IItemBuffHooks { public bool HasItem(Entity unit, int itemId) => unit.Inventory.Any(slot => slot?.ItemId == itemId); public void SyncItemSlow(Entity target) { } }
    }

    internal sealed class VisionSink(Func<uint, Entity?> resolve, Func<UnitType, uint> allocate) : IBuffVisionSink
    {
        public Entity? ResolveCaster(uint netId) => resolve(netId);
        public uint AddUnitPerceptionBubble(Entity unit, double radius, double ttl, uint bubbleType, uint flags) => allocate(UnitType.Missile);
        public void RemovePerceptionBubble(Entity unit, uint bubbleId) { }
    }
}
