using System.Text.Json;
using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

public static class NeutralChecks
{
    [Check("neutral.b6-wolf-critical-stats")]
    public static void WolfCriticalStats()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var world = runtime.World;
        world.Clock.SetVirtual();
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [2, 8, 5]);
        scheduler.Start();
        scheduler.Tick(100);
        var target = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, new Position2(7000, 7000))
            { ChampionName = "Annie", Health = 10000, MaxHealth = 10000, HeroAutoAcquire = false });
        var capture = new NeutralAiChecks.NeutralEffectCapture();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order, target.NetId, capture, ready: true));
        foreach (var member in scheduler.Members.Values)
        {
            var wolf = member.Name is "wolf" or "GiantWolf";
            var multiplier = member.Name == "GiantWolf" ? 3.0 : 2.0;
            TestAssert.Near(wolf ? .25 : 0, member.Entity.CritChance, 0,
                "JNG-B6-01: shipped BaseCritChance is a percentage in both wolf camps; Golem is zero");
            TestAssert.Near(multiplier, member.Entity.CritMultiplier, 0);
            // Put the actors on open ground so terrain does not decide the feedback assertion.
            member.Entity.Position = new Position2(7000, 7000);
            target.Position = new Position2(7050, 7000);
            runtime.Delivery.Flush();
            foreach (var roll in new[] { .249999, .25 })
            {
                var critical = wolf && roll < .25;
                var hit = CombatRules.BasicAttackDamage(member.Entity, target, new AttackRolls(roll, .99, .99));
                TestAssert.Equal(critical ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal, hit.Outcome,
                    "critical outcome drives basic-attack feedback");
                // Raw shipped AD: small wolf 18, GiantWolf 26, Golem 65; Map1 dr_UnitToHero (0.7,
                // obj_AI_Minion attacking obj_AI_Hero). The character scripts' own CharOnHitUnit factor 1.43
                // (CharScriptGolem and its nine siblings, 0039-0063) is applied by the hook, not by this raw formula
                // (batch 159 part 2).
                var unitToHero = GameData.Default.LevelConstants.DamageRatios.UnitToHero;
                var damage = member.Name == "wolf" ? 18.0 : member.Name == "GiantWolf" ? 26.0 : 65.0;
                TestAssert.Near(damage * unitToHero * (critical ? multiplier : 1), hit.Damage, 1e-8);
                member.Entity.AttackCooldown = 0;
                capture.Packets.Clear();
                TestAssert.True(runtime.Combat.QueueBasicAttack(member.Entity, target,
                    new AttackRolls(roll, .99, .99)));
                runtime.Delivery.Flush();
                var attack = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0x0D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(1, 4)) == member.NetId);
                TestAssert.Equal(critical, attack.Plaintext.Span[14] == 0x44,
                    "critical feedback uses the native critical attack slot");
                runtime.Combat.InterruptBasicAttack(member.Entity);
            }
        }
    }

    [Check("neutral.scheduler-bounded-members")]
    public static void SchedulerRetiresPreviousSpawn()
    {
        var world = new WorldState();
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [1, 12]);
        scheduler.Start();
        scheduler.Tick(900);
        for (var cycle = 0; cycle < 6; cycle++)
        {
            TestAssert.Equal(4, scheduler.Members.Count, "scheduler retains one generation per camp");
            var previous = scheduler.Members.Values.ToArray();
            var now = 900 + cycle * 500;
            foreach (var member in previous)
            {
                member.Entity.Alive = false;
                member.Entity.Health = 0;
                scheduler.OnDeath(member.Entity, now);
            }
            TestAssert.True(previous.All(member => scheduler.TryGetMember(member.NetId, out _)),
                "dead members remain available for pending script work before the next spawn");
            var respawn = scheduler.Camps.Values.Max(camp => camp.NextSpawnAt!.Value);
            scheduler.Tick(respawn);
            TestAssert.True(previous.All(member => !scheduler.TryGetMember(member.NetId, out _)),
                "respawn must release every previous camp member");
            TestAssert.True(previous.All(member => world.Get(member.NetId) == member.Entity),
                "scheduler cleanup must leave corpse lifetime to the world");
            TestAssert.Equal(4, scheduler.Members.Count);
            TestAssert.True(scheduler.Members.Values.All(member => member.Entity.Alive));
        }
    }

    // Batch 325: the client's character loader reads Data*PARType with the default "MP" and Data*BaseMP with the default 100.
    // Every lane minion and camp monster record names its BaseMP, as 0 (no PARType, so a mana unit), so the pool the client's
    // loader builds is 0/0, which the spawns (WaveScheduler.Spawn, JungleScheduler) and their 0xCC template (CurrentMana/MaxMana) carry.
    [Check("neutral.b325-lane-minions-and-camp-monsters-hold-their-record-mana-pool")]
    public static void LaneMinionsAndCampMonstersHoldTheirRecordManaPool()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var world = runtime.World;
        world.Clock.SetVirtual();
        var units = new List<Entity>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in Enum.GetValues<Amberline.Core.MinionType>())
            units.Add(runtime.Waves!.SpawnFixture(team, type, 0, "L", 0, 0));
        var scheduler = new JungleScheduler(world, GameData.Default);
        scheduler.Start();
        scheduler.Tick(900);
        units.AddRange(scheduler.Members.Values.Select(member => member.Entity));
        var names = units.Select(unit => unit.Name).ToHashSet();
        TestAssert.True(names.Count >= 8 + 8 && new[] { "Dragon", "Worm", "AncientGolem", "LizardElder" }.All(names.Contains),
            "four minion kinds per team and every camp's monsters: " + string.Join(",", names));
        var observer = new ReplicationObserver(1, Team.Order, 0, new NeutralAiChecks.NeutralEffectCapture(), ready: true);
        foreach (var unit in units)
        {
            var record = GameData.Default.Champion(unit.Name);
            TestAssert.True(record.Raw.ContainsKey(InibinReader.KeyHash("Data*BaseMP")), unit.Name + " names its BaseMP (the loader's 100 does not apply)");
            TestAssert.False(record.NamesParType, unit.Name + " names no PARType");
            TestAssert.Equal(PrimaryResourceType.Mana, record.PrimaryResource, unit.Name + " is a mana unit (default MP)");
            TestAssert.Near(0, record.BaseMp, 0, unit.Name + " BaseMP");
            TestAssert.Near(record.BaseMp, unit.MaxMana, 0, unit.Name + " max mana");
            TestAssert.Near(record.BaseMp, unit.Mana, 0, unit.Name + " mana");
            // The 111-byte MinionStats template of the unit's creation: map 1 starts at 19 with mHP, mMP, mMaxHP, mMaxMP.
            var template = runtime.Delivery.Services.BuildEntityCreation(unit, observer).Single(packet => packet.Plaintext.Length == 111 &&
                packet.Plaintext.Span[0] == 0xCC).Plaintext.Span;
            TestAssert.Near(record.BaseMp, BinaryPrimitives.ReadSingleLittleEndian(template[23..]), 0, unit.Name + " replicated mana");
            TestAssert.Near(record.BaseMp, BinaryPrimitives.ReadSingleLittleEndian(template[31..]), 0, unit.Name + " replicated max mana");
        }
    }

    // Batch 329: the same template's mPARRegenRate (LOCAL1 field 18, offset 91) carries the record's BaseStaticMPRegen: 0.5 for the camp
    // monsters, 0 for the Worm and every lane minion (retail even though a 0 pool shows nothing).
    [Check("neutral.b329-camp-monsters-replicate-their-record-mana-regeneration")]
    public static void CampMonstersReplicateTheirRecordManaRegeneration()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var world = runtime.World;
        world.Clock.SetVirtual();
        var units = new List<Entity>();
        foreach (var type in Enum.GetValues<Amberline.Core.MinionType>())
            units.Add(runtime.Waves!.SpawnFixture(Team.Order, type, 0, "L", 0, 0));
        var scheduler = new JungleScheduler(world, GameData.Default);
        scheduler.Start();
        scheduler.Tick(900);
        units.AddRange(scheduler.Members.Values.Select(member => member.Entity));
        var observer = new ReplicationObserver(1, Team.Order, 0, new NeutralAiChecks.NeutralEffectCapture(), ready: true);
        var regen = new Dictionary<string, double>();
        foreach (var unit in units)
        {
            var template = runtime.Delivery.Services.BuildEntityCreation(unit, observer).Single(packet => packet.Plaintext.Length == 111 &&
                packet.Plaintext.Span[0] == 0xCC).Plaintext.Span;
            var expected = GameData.Default.Champion(unit.Name).MpRegen;
            TestAssert.Near(expected, BinaryPrimitives.ReadSingleLittleEndian(template[91..]), 1e-6, unit.Name + " replicated PAR regeneration");
            regen[unit.Name] = expected;
        }
        TestAssert.Near(0.5, regen["Dragon"], 1e-6, "the Dragon's record");
        TestAssert.Near(0.5, regen["AncientGolem"], 1e-6, "the blue golem's record");
        TestAssert.Near(0.0, regen["Worm"], 1e-6, "the Worm's record");
        TestAssert.True(units.Where(unit => unit.MinionType is not null).All(unit => regen[unit.Name] == 0.0), "and every lane minion's");
    }


    [Check("neutral.attack-timing-native-cast-offsets")]
    public static void AllCampAttackWindupsUseCharacterCastOffsets()
    {
        var expected = new[]
        {
            (Name: "AncientGolem", Period: 1.632, CastFraction: 0.4609999984502792),
            (Name: "YoungLizard", Period: 1.472, CastFraction: 0.4086499989032745),
            (Name: "wolf", Period: 1.520, CastFraction: 0.37199999690055846),
            (Name: "GiantWolf", Period: 1.472, CastFraction: 0.38416999876499175),
            (Name: "Wraith", Period: 1.568, CastFraction: 0.180000002682209),
            (Name: "LesserWraith", Period: 1.568, CastFraction: 0.360649998486042),
            (Name: "LizardElder", Period: 1.600, CastFraction: 0.208330000936985),
            (Name: "Golem", Period: 1.632, CastFraction: 0.40999999940395354),
            (Name: "Dragon", Period: 1.520, CastFraction: 0.6118000030517579),
            (Name: "Worm", Period: 4.800, CastFraction: 0.13732640743255614),
        };
        foreach (var row in expected)
        {
            var actual = NeutralStatsCalculator.Calculate(GameData.Default, row.Name);
            TestAssert.Near(row.Period, actual.AttackPeriod, 1e-6, row.Name + " native period");
            TestAssert.Near(row.Period * row.CastFraction, actual.Windup, 1e-6,
                row.Name + " native cast-offset windup");
            TestAssert.True(Math.Abs(actual.Windup - 0.5) > 1e-5,
                row.Name + " must not use the obsolete fixed .5 windup");
        }
    }

    private static double? NativeNeutralCastFraction(string name) => name switch
    {
        "AncientGolem" => 0.4609999984502792,
        "YoungLizard" => 0.4086499989032745,
        "wolf" => 0.37199999690055846,
        "GiantWolf" => 0.38416999876499175,
        "Wraith" => 0.180000002682209,
        "LesserWraith" => 0.360649998486042,
        "LizardElder" => 0.208330000936985,
        "Golem" => 0.40999999940395354,
        "Dragon" => 0.6118000030517579,
        "Worm" => 0.13732640743255614,
        _ => null,
    };


    private static void RunCase(JsonElement testCase, NavigationGrid grid)
    {
        var camp = testCase.GetProperty("camp").GetInt32();
        var heroJson = testCase.GetProperty("hero");
        var position = heroJson.GetProperty("position");
        var world = new WorldState();
        var hero = new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(position[0].GetDouble(), position[1].GetDouble()))
        {
            Level = heroJson.GetProperty("level").GetInt32(),
            ChampionName = "Annie",
        };
        world.Register(hero);

        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add,
            new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        // Batch 159 part 2: the members spawn under their own character scripts (production wires the same seam in
        // ProductionMatchComposition), so the buffs the corpus was recorded with are the scripts' own adds.
        var characters = Amberline.Tests.Spells.UnitCharacterRig.Create(world, grid);
        var scheduler = new JungleScheduler(world, GameData.Default, sink, [camp]);
        scheduler.BindUnitCharacter = (unit, character) => characters.Bind(unit, character);
        scheduler.Start(testCase.GetProperty("origin").GetDouble());
        // Batch 174 part 2: the Worm's spawn-time growth is ResistantSkin's BBGetGameTime, which reads the spell clock, so the rig's clock
        // follows the scheduler's (the origin, then each tick) before the scheduler does.
        var spellClock = characters.Spells!;
        void AlignSpellClock(double time)
        {
            if (time > spellClock.CurrentTime) spellClock.Tick(time - spellClock.CurrentTime);
        }
        AlignSpellClock(testCase.GetProperty("origin").GetDouble());

        foreach (var expected in testCase.GetProperty("events").EnumerateArray())
        {
            packets.Clear();
            var action = expected.GetProperty("action").GetString()!;
            switch (action)
            {
                case "tick":
                    AlignSpellClock(expected.GetProperty("time").GetDouble());
                    scheduler.Tick(expected.GetProperty("time").GetDouble());
                    break;
                case "memberDeath":
                {
                    var id = expected.GetProperty("member").GetUInt32();
                    var member = world.Get(id) ?? throw new InvalidOperationException($"missing neutral 0x{id:X8}");
                    member.Alive = false;
                    member.Health = 0.0;
                    scheduler.OnDeath(member, expected.GetProperty("time").GetDouble());
                    break;
                }
                case "observeEmptyTwice":
                {
                    // This archived action represented the old global
                    // ObserveEmpty sink. Connected delivery now owns team
                    // observation, so keep the corpus's death state and
                    // verify that the scheduler remains pending here.
                    TestAssert.True(scheduler.Camps[camp].PendingEmpty,
                        "empty observation remains pending until the next real spawn");
                    TestAssert.Equal(0, packets.Count,
                        "the disconnected scheduler no longer broadcasts 0xCB");
                    break;
                }
                default:
                    throw new InvalidOperationException("Unknown neutral event: " + expected.GetProperty("action"));
            }

            var state = scheduler.Camps[camp];
            AssertState(expected.GetProperty("state"), state);
            AssertMembers(expected.GetProperty("members"), scheduler);
            if (action == "observeEmptyTwice")
                TestAssert.Equal(0, packets.Count);
            AssertPackets(expected.GetProperty("packets"), packets);
        }
    }

    private static void AssertState(JsonElement expected, NeutralCampState actual)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected.GetProperty("next_spawn_at"), actual.NextSpawnAt);
            CorpusSnapshot.Set(expected.GetProperty("health_bonus"), actual.HealthBonus);
            CorpusSnapshot.Set(expected.GetProperty("damage_bonus"), actual.DamageBonus);
            CorpusSnapshot.Set(expected.GetProperty("member_netids"), actual.MemberNetIds);
            CorpusSnapshot.Set(expected.GetProperty("active"), actual.Active);
            CorpusSnapshot.Set(expected.GetProperty("spawn_cycles"), actual.SpawnCycles);
            CorpusSnapshot.Set(expected.GetProperty("pending_empty"), actual.PendingEmpty);
            return;
        }
        var next = expected.GetProperty("next_spawn_at");
        if (next.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual.NextSpawnAt is null, "unexpected next spawn");
        else
            TestAssert.Near(next.GetDouble(), actual.NextSpawnAt ?? double.NaN, 1e-9, "next spawn");
        TestAssert.Near(expected.GetProperty("health_bonus").GetDouble(), actual.HealthBonus, 1e-9);
        TestAssert.Near(expected.GetProperty("damage_bonus").GetDouble(), actual.DamageBonus, 1e-9);
        TestAssert.Equal(expected.GetProperty("member_netids").EnumerateArray()
            .Select(item => item.GetUInt32()).ToArray(), actual.MemberNetIds.ToArray());
        TestAssert.Equal(expected.GetProperty("active").GetBoolean(), actual.Active);
        TestAssert.Equal(expected.GetProperty("spawn_cycles").GetInt32(), actual.SpawnCycles);
        // The old killer NetId field was never a valid 0xCB player identity
        // and is intentionally absent from production state. Archived empty
        // actions normalize only pending_empty until the next real spawn;
        // every other state field remains corpus-checked.
        TestAssert.Equal(expected.GetProperty("pending_empty").GetBoolean(),
            actual.PendingEmpty);
    }

    private static void AssertMembers(JsonElement expected, JungleScheduler scheduler)
    {
        var rows = expected.EnumerateArray().ToArray();
        TestAssert.Equal(rows.Length, scheduler.MembersForCamp(rows.Length == 0 ? 0 :
            rows[0].GetProperty("camp_id").GetInt32()).Count);
        foreach (var row in rows)
        {
            var id = row.GetProperty("netid").GetUInt32();
            TestAssert.True(scheduler.TryGetMember(id, out var member), $"missing member 0x{id:X8}");
            var entity = member.Entity;
            if (FixtureRecorder.Enabled)
            {
                CorpusSnapshot.Set(row.GetProperty("hp"), entity.Health);
                CorpusSnapshot.Set(row.GetProperty("max_hp"), entity.MaxHealth);
                CorpusSnapshot.Set(row.GetProperty("alive"), entity.Alive);
                CorpusSnapshot.Set(row.GetProperty("created"), entity.Created);
                CorpusSnapshot.Set(row.GetProperty("atk_damage"), entity.AttackDamage);
                CorpusSnapshot.Set(row.GetProperty("level"), entity.Level);
                continue;
            }
            TestAssert.Near(row.GetProperty("hp").GetDouble(), entity.Health, 1e-7, member.Name + " hp");
            TestAssert.Near(row.GetProperty("max_hp").GetDouble(), entity.MaxHealth, 1e-7, member.Name + " max hp");
            TestAssert.Equal(row.GetProperty("alive").GetBoolean(), entity.Alive, member.Name + " alive");
            TestAssert.Equal(row.GetProperty("created").GetBoolean(), entity.Created, member.Name + " created");
            TestAssert.Near(row.GetProperty("atk_damage").GetDouble(), entity.AttackDamage, 1e-7, member.Name + " damage");
            TestAssert.Near(row.GetProperty("level").GetInt32(), entity.Level, 1e-7, member.Name + " level");
            TestAssert.Equal(row.GetProperty("camp_id").GetInt32(), entity.CampId);
            TestAssert.Equal(row.GetProperty("neutral_member").GetInt32(), entity.NeutralMember);
        }
    }

    private static void AssertPackets(JsonElement expected, IReadOnlyList<PlaintextPacket> actual)
    {
        if (FixtureRecorder.Enabled)
        {
            var recordingCipher = LegacyBlowfish.CreateDefault();
            CorpusSnapshot.Set(expected, actual.Select(packet => new
            {
                channel = packet.Channel, flags = (byte)packet.Reliability,
                plaintext = Convert.ToHexString(recordingCipher.Decrypt(packet.ToWireBytes(recordingCipher))).ToLowerInvariant(),
                wire = Convert.ToHexString(packet.ToWireBytes(recordingCipher)).ToLowerInvariant(),
            }).ToArray());
            return;
        }
        var rows = expected.EnumerateArray().ToArray();
        TestAssert.Equal(rows.Length, actual.Count, "neutral packet count");
        var cipher = LegacyBlowfish.CreateDefault();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var packet = actual[index];
            TestAssert.Equal(row.GetProperty("channel").GetByte(), packet.Channel, $"packet {index} channel");
            TestAssert.Equal(row.GetProperty("flags").GetByte(), (byte)packet.Reliability, $"packet {index} flags");
            var expectedPlain = Convert.FromHexString(row.GetProperty("plaintext").GetString()!);
            var actualPlain = packet.Plaintext.ToArray();
            var expectedPrefix = expectedPlain.Length == actualPlain.Length
                ? expectedPlain
                : expectedPlain[..actualPlain.Length];
            TestAssert.Equal(expectedPrefix, actualPlain,
                $"packet {index} plaintext expected {Convert.ToHexString(expectedPrefix)} actual {Convert.ToHexString(actualPlain)}");
            var expectedWire = Convert.FromHexString(row.GetProperty("wire").GetString()!);
            TestAssert.Equal(expectedWire, packet.ToWireBytes(cipher), $"packet {index} wire");
        }
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
