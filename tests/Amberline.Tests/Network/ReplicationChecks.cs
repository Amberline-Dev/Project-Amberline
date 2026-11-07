using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Spells;

namespace Amberline.Tests.Network;

public static class ReplicationChecks
{
    [Check("replication.b9-turret-shaco-true-sight-ranges")]
    public static void TurretDetectionUsesScriptedRangesInsteadOfOrdinarySight()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var lane = world.Entities.Values.Single(entity => entity.Name == "Turret_T1_C_07");
        var nexus = world.Entities.Values.Single(entity => entity.Name == "Turret_T1_C_02");
        foreach (var entity in world.Entities.Values.ToArray())
            if (entity != lane) world.Remove(entity.NetId);
        var shaco = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Chaos, new(2567, 435))
            { Name = "Shaco", ChampionName = "Shaco" });
        shaco.Buffs.Add(new BuffState("Deceive", type: BuffType.Invisibility,
            expiresAt: 1000, payload: new BuffPayload { Stealthed = true }));
        var environment = new OpenVisionEnvironment();
        TestAssert.True(lane.Alive);
        TestAssert.Near(1350, VisibilityRules.TerrainVisionRange(lane), 0);
        TestAssert.True(VisibilityRules.ProviderReveals(environment,
            new VisionProvider(lane.Position, 1350), shaco.Position), "captured position has ordinary turret sight");
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Order, shaco.Position),
            "captured Deceive position is outside the lane turret's 800 detection bubble");
        TestAssert.False(runtime.Dependencies.CombatHooks.IsTargetable(shaco, lane));
        var delivery = new ReplicationDelivery(world, Services(), environment,
            runtime.Delivery.AdditionalProviders, runtime.Delivery.RevealsStealth);
        var observer = new ReplicationObserver(1, Team.Order, lane.NetId, new CaptureReplicationTransport());
        delivery.AddObserver(observer);
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(shaco.NetId), "no enemy delivery of stealthed Shaco");

        // Spells/TurretBonusHealth.luaobj's BBAddUnitPerceptionBubble (RevealSteath true, Duration 25000, RadiusVar
        // BubbleSize): the add's NextBuffVars.BubbleSize is 800 on the lane characters and 1000 on the two nexus ones,
        // and it is the unit's own detection now that ItemVisionService has no hand-written turret rule. Both include
        // their boundary.
        foreach (var (turret, radius) in new[] { (lane, 800.0), (nexus, 1000.0) })
        {
            world.Remove(lane.NetId);
            world.Register(turret);
            shaco.Position = turret.Position + new Position2(radius, 0);
            TestAssert.True(runtime.Delivery.RevealsStealth(Team.Order, shaco.Position));
            TestAssert.True(runtime.Dependencies.CombatHooks.IsTargetable(shaco, turret));
            delivery.Flush();
            TestAssert.True(observer.View.Visible.Contains(shaco.NetId));
            shaco.Position += new Position2(1, 0);
            TestAssert.False(runtime.Delivery.RevealsStealth(Team.Order, shaco.Position));
            TestAssert.False(runtime.Dependencies.CombatHooks.IsTargetable(shaco, turret));
            delivery.Flush();
            TestAssert.False(observer.View.Visible.Contains(shaco.NetId));
            turret.Alive = false;
            TestAssert.False(runtime.Delivery.RevealsStealth(Team.Order, turret.Position));
        }
    }

    [Check("replication.b9-chaos-shrine-true-sight-anchor")]
    public static void AttachedShrineDetectionMeasuresFromTheUnit()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var shrine = world.Entities.Values.Single(entity =>
            entity.Team == Team.Chaos && VisibilityRules.IsShrine(entity));
        foreach (var entity in world.Entities.Values.ToArray())
            if (entity != shrine) world.Remove(entity.NetId);
        var witness = new Position2(13011.35, 12987.25);
        var clearOffset = witness - MapDefinitions.TeamSpawn(Team.Chaos);
        var anchorOffset = witness - shrine.Position;
        TestAssert.Near(1500, Math.Sqrt(clearOffset.X * clearOffset.X + clearOffset.Z * clearOffset.Z), 0.02);
        TestAssert.Near(1863.02, Math.Sqrt(anchorOffset.X * anchorOffset.X + anchorOffset.Z * anchorOffset.Z), 0.02);
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Chaos, witness),
            "the old fountain sight origin (batch 259 retired it) never moved the attached 1600 detection radius");
        TestAssert.True(runtime.Delivery.RevealsStealth(Team.Chaos, shrine.Position + new Position2(-1600, 0)));
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Chaos, shrine.Position + new Position2(-1601, 0)));
        shrine.Position += new Position2(-500, 0);
        TestAssert.True(runtime.Delivery.RevealsStealth(Team.Chaos, shrine.Position + new Position2(-1600, 0)),
            "attached detection follows the live anchor");
        world.TimedVisionBubbles.Clear();
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Chaos, shrine.Position),
            "a shrine has no implicit ordinary-turret detector");
    }

    [Check("vision-b7-chaos-shrine-scripted-attached-provider")]
    public static void ChaosShrinePublishesItsScriptedTrueSightBubble()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var shrine = world.Entities.Values.Single(entity =>
            entity.Team == Team.Chaos && VisibilityRules.IsShrine(entity));
        var orderShrine = world.Entities.Values.Single(entity =>
            entity.Team == Team.Order && VisibilityRules.IsShrine(entity));
        // Batch 159 step 2: every turret character's CharOnActivate adds TurretBonusHealth, whose own
        // BBAddUnitPerceptionBubble is on the map (Spells/TurretBonusHealth.luaobj: BubbleSize 800 lane, 1000 nexus,
        // 1600 ChaosTurretShrine). The one this check pins is the shrine's.
        var bubble = world.TimedVisionBubbles.Single(value => value.AnchorNetId == shrine.NetId);
        // Independent expected values: ChaosTurretShrine 0030-0032 and
        // TurretBonusHealth 0026-0037; the buff removes itself at 0117-0120.
        TestAssert.Equal(shrine.NetId, bubble.AnchorNetId);
        TestAssert.Equal(Team.Chaos, bubble.Team);
        TestAssert.Near(1600, bubble.Radius, 0);
        TestAssert.Near(25000, bubble.Remaining, 0);
        TestAssert.True(bubble.RevealStealth);
        TestAssert.False(world.TimedVisionBubbles.Any(value => value.AnchorNetId == orderShrine.NetId));

        // Batch 259: the shrine sees from its own position (no fountain point); isolate eligible open
        // terrain from other providers; the packet itself attaches to the unit.
        foreach (var entity in world.Entities.Values.ToArray())
            if (entity != shrine && entity != orderShrine) world.Remove(entity.NetId);
        var center = shrine.Position;
        var target = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, center + new Position2(-1300, 520))
            { StealthHidden = true, Health = 1000, MaxHealth = 1000 });
        TestAssert.False(VisibilityRules.ProviderReveals(new OpenVisionEnvironment(),
            new VisionProvider(shrine.Position, 1350), target.Position));
        TestAssert.True(VisibilityRules.ProviderReveals(new OpenVisionEnvironment(),
            new VisionProvider(shrine.Position, 1600), target.Position),
            "target is between 1350 and 1600 from the shrine");
        var capture = new CaptureReplicationTransport();
        var opponent = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            runtime.Delivery.AdditionalProviders, runtime.Delivery.RevealsStealth);
        var observer = new ReplicationObserver(1, Team.Chaos, shrine.NetId, capture);
        delivery.AddObserver(observer);
        delivery.AddObserver(new ReplicationObserver(2, Team.Order, target.NetId, opponent));
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(target.NetId),
            "the stealthed target beyond 1350 needs both scripted sight and detection");
        TestAssert.True(runtime.Delivery.RevealsStealth(Team.Chaos, target.Position));
        TestAssert.True(runtime.Delivery.AdditionalProviders(Team.Chaos)
            .Any(provider => provider.Position == center && provider.Radius == 1600),
            "scripted sight sees from the shrine, where the client draws the 0x26 (batch 259)");
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Order, target.Position));
        TestAssert.False(runtime.Delivery.AdditionalProviders(Team.Order)
            .Any(provider => provider.Radius == 1600));
        TestAssert.False(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            runtime.Delivery.AdditionalProviders).Any(provider => provider.Radius > 1350),
            "owner decision, 18 October 2026: the Order fountain sees 1350 like Chaos");
        var expected = SpawnPackets.AddUnitPerceptionBubble(shrine.NetId, 1600, 25000,
            bubble.BubbleId, (uint)Team.Chaos, flags: 5);
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(expected.Plaintext.Span)));
        TestAssert.False(opponent.Packets.Any(packet => packet.Plaintext.Span[0] == 0x26 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == bubble.BubbleId),
            "no opponent receives the shrine bubble");

        target.Position = center + new Position2(-1601, 0);
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(target.NetId), "1601 is outside the scripted radius");
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Chaos, target.Position));
        target.Position = center + new Position2(-1300, 520);
        runtime.Spells.Tick(10);
        observer.View.Reset();
        capture.Clear();
        delivery.Flush();
        expected = SpawnPackets.AddUnitPerceptionBubble(shrine.NetId, 1600, 24990,
            bubble.BubbleId, (uint)Team.Chaos, flags: 5);
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(expected.Plaintext.Span)),
            "reconnect restores the remaining lifetime under the same ID");
        runtime.Spells.Tick(24990);
        capture.Clear();
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(target.NetId));
        TestAssert.False(runtime.Delivery.RevealsStealth(Team.Chaos, target.Position));
        var remove = SpawnPackets.RemovePerceptionBubble(bubble.BubbleId);
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(remove.Plaintext.Span)));

    }

    // Batch 259: the client's own structure objects make their sight bubbles (the turret, barracks dampener and HQ objects are units,
    // drawn with the wall mask while the structure is alive and seen), so a retail server sends no bubble for a structure.
    // Before, every allied structure got a 0x7B position bubble (drawn as a full disc).
    [Check("replication.structure-sight-client-makes-its-own-bubble")]
    public static void StructureSightSendsNoServerBubble()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var structures = world.Entities.Values.Where(entity =>
            entity.Team == Team.Order && entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus).ToArray();
        TestAssert.Equal(16, structures.Length, "Map1 Order: 12 turrets (the shrine included), 3 inhibitors, the nexus");
        var bubbleIds = structures.Select(entity => entity.NetId ^ 0x2000_0000u).ToHashSet();
        var capture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            runtime.Delivery.AdditionalProviders, runtime.Delivery.RevealsStealth);
        var observer = new ReplicationObserver(1, Team.Order, structures[0].NetId, capture);
        delivery.AddObserver(observer);
        delivery.Flush();
        TestAssert.True(structures.All(entity => observer.View.Visible.Contains(entity.NetId)),
            "the allied structures are seen, so the client draws their own bubbles");
        TestAssert.False(observer.View.TerrainIds.Overlaps(bubbleIds), "no structure holds a server terrain bubble");
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x7B &&
            bubbleIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]))), "no 0x7B for a structure");
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x26 &&
            bubbleIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]))), "no 0x26 for a structure");
    }

    // Batch 264: a destroyed inhibitor's client object keeps drawing its bubble: the barracks dampener's death packet handler
    // never sets the dead flag (the building's own death code does), and its dead state leaves the fields the client's bubble
    // test reads alone, while the server keeps the dead unit seen by its team. So its team keeps the inhibitor's 1350 sight
    // until it respawns. A destroyed turret's (0xA6 sets the dead flag) and a dead unit's sight still end.
    [Check("replication.dead-inhibitor-keeps-its-bubble-sight")]
    public static void DeadInhibitorKeepsItsBubbleSight()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        var inhibitor = world.Entities.Values.Single(entity => entity.Kind == EntityKind.Inhibitor && entity.Name == "Barracks_T1_C1");
        var turret = world.Entities.Values.First(entity => entity.Kind == EntityKind.Turret && entity.Team == Team.Order &&
            !VisibilityRules.IsShrine(entity));
        inhibitor.Alive = false;
        turret.Alive = false;
        var providers = VisibilityRules.BuildTeamProviders(world, Team.Order, [], _ => []);
        var own = providers.Where(provider => provider.Position == inhibitor.Position).ToArray();
        TestAssert.Equal(1, own.Length, "the destroyed inhibitor still sees from its own position");
        TestAssert.Near(1350, own[0].Radius, 0, "with its bubble's 1350");
        TestAssert.True(own[0].IsUnit, "an IsUnit bubble, drawn with the wall mask");
        TestAssert.False(providers.Any(provider => provider.Position == turret.Position), "a destroyed turret sees nothing");
        inhibitor.Alive = true;
        TestAssert.Equal(1, VisibilityRules.BuildTeamProviders(world, Team.Order, [], _ => [])
            .Count(provider => provider.Position == inhibitor.Position), "a respawned inhibitor has the one provider");
    }

    // Batch 264: every unit's client object makes its own sight bubble, in the hero and minion creation handlers (lane minions, pets,
    // wards, script units; skipped only for a ServerOnly CharacterData, which no shipped character is), with the unit flag set,
    // (percent + 1) * CharacterData PerceptionBubbleRadius + flat, kept current by the client's bubble updates. So the server sends no
    // attached 0x26 for a hero, a lane minion, a pet or a ward (before: one for every allied unit, about 10,000 a match). The client
    // never reads the spawn packet's VisibilitySize field, so a script unit's VisibilitySize bubble is still sent, as is every script bubble.
    [Check("replication.unit-sight-client-makes-its-own-bubble")]
    public static void UnitSightSendsOnlyTheBubblesTheClientDoesNotMake()
    {
        var clock = new GameClock();
        clock.SetVirtual(100);
        var world = new WorldState(clock);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(5000, 5000)) { ChampionName = "Annie" };
        var minion = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Order, new(5300, 5000)) { MinionType = MinionType.Melee };
        var pet = new Entity(3, EntityKind.Pet, UnitType.Monster, Team.Order, new(5000, 5300)) { OwnerNetId = 1, Name = "AnnieTibbers" };
        var ward = new Entity(4, EntityKind.Ward, UnitType.Monster, Team.Order, new(5300, 5300)) { IsWard = true, Name = "SightWard" };
        var cube = new Entity(5, EntityKind.Monster, UnitType.Monster, Team.Order, new(5600, 5000)) { Name = "BloodDrop", VisibilitySize = 450.0 };
        foreach (var unit in new[] { hero, minion, pet, ward, cube }) world.Register(unit);
        // SightWard.luaobj OnBuffActivate: BBAddUnitPerceptionBubble Radius 1100, Duration 180, on the ward.
        var script = world.AddTimedVisionBubble(Team.Order, ward.Position, 1100.0, 180.0, anchorNetId: ward.NetId);
        var capture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var observer = new ReplicationObserver(1, Team.Order, hero.NetId, capture);
        delivery.AddObserver(observer);
        delivery.Flush();
        uint AttachedTo(PlaintextPacket packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[17..]);
        var attached = capture.Packets.Where(packet => packet.Plaintext.Span[0] == 0x26).ToArray();
        foreach (var unit in new[] { hero, minion, pet })
        {
            TestAssert.True(observer.View.Visible.Contains(unit.NetId), $"{unit.Kind} is seen, so its client object draws its bubble");
            TestAssert.False(attached.Any(packet => AttachedTo(packet) == unit.NetId), $"no 0x26 for the {unit.Kind}");
        }
        TestAssert.Equal(1, attached.Count(packet => AttachedTo(packet) == ward.NetId), "the ward gets only its script's bubble");
        var scriptPacket = SpawnPackets.AddUnitPerceptionBubble(ward.NetId, 1100.0f, 180.0f, script, (uint)Team.Order);
        TestAssert.True(attached.Any(packet => packet.Plaintext.Span.SequenceEqual(scriptPacket.Plaintext.Span)), "the script bubble is kept");
        var cubePacket = SpawnPackets.AddUnitPerceptionBubble(cube.NetId, 450.0f, 1.0e9f, cube.NetId ^ 0x2000_0000u, (uint)Team.Order);
        TestAssert.True(attached.Any(packet => packet.Plaintext.Span.SequenceEqual(cubePacket.Plaintext.Span)),
            "the VisibilitySize bubble, which the client does not make, is sent");
        TestAssert.Equal(2, attached.Length, "nothing else");
        TestAssert.Equal(new[] { cube.NetId ^ 0x2000_0000u }, observer.View.TerrainIds.ToArray(), "the cube's bubble is the only terrain bubble");
        capture.Clear();
        cube.Alive = false;
        delivery.Flush();
        var remove = SpawnPackets.RemovePerceptionBubble(cube.NetId ^ 0x2000_0000u);
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(remove.Plaintext.Span)), "the cube's bubble ends with it");
    }

    // Batch 473 follow-up: an effect on no current unit reaches its own fog-of-war team (BBSpellEffectCreate's FOWTeam, else the placemarker
    // cube's team) out of that team's sight; another team needs sight of its point. Rumble's Equalizer lost its far end on Rumble's own client
    // once its ServerOnly cubes stopped seeing 1350 (bundle 26 sweep: FX-CREATE).
    [Check("replication.effect-reaches-its-own-fow-team-out-of-sight")]
    public static void EffectReachesItsOwnFowTeamOutOfSight()
    {
        var clock = new GameClock();
        clock.SetVirtual(100);
        var world = new WorldState(clock);
        var order = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(3000, 3000)) { ChampionName = "Rumble" });
        var chaos = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(11000, 11000)) { ChampionName = "Annie" });
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var orderObserver = new ReplicationObserver(1, Team.Order, order.NetId, new CaptureReplicationTransport());
        var chaosObserver = new ReplicationObserver(2, Team.Chaos, chaos.NetId, new CaptureReplicationTransport());
        delivery.AddObserver(orderObserver);
        delivery.AddObserver(chaosObserver);
        delivery.Flush();
        var far = new Position2(7000, 7000); // outside both heroes' 1350 and both shops
        PlaintextPacket Fx(uint id) => FxPackets.CreateGroup(1, id, far, new NavigationBounds(0, 0, 0, 0), (_, _) => 0);
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9001), 0x9001, far, FowTeam: Team.Order));
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9002), 0x9002, far));
        delivery.Flush();
        TestAssert.True(orderObserver.View.Effects.Contains(0x9001), "its own FOW team gets it out of sight");
        TestAssert.False(chaosObserver.View.Effects.Contains(0x9001), "another team needs sight of its point");
        TestAssert.False(orderObserver.View.Effects.Contains(0x9002), "an effect with no FOW team still needs sight");
        TestAssert.False(chaosObserver.View.Effects.Contains(0x9002), "for every team");
    }

    // Batch 690 (community report: "zilean bomb not visible when lost vision of target"): an effect bound to a unit reaches the effect's own
    // fog-of-war team (FOWTeam / FOWTeamOverrideVar: Time Bomb names the caster's team) when that team holds the unit but cannot see it. The stock
    // client keeps such an effect hidden until the unit is in sight (the emitter's Draw), and its FX create
    // routine moves a target it holds but cannot see to the packet's position, so retail sent these. An effect on no bind (drawn at its point
    // whatever its target) and an effect with no own team still need sight of their unit.
    [Check("replication.bound-effect-reaches-its-own-fow-team-while-the-unit-is-fogged")]
    public static void BoundEffectReachesItsOwnFowTeamWhileTheUnitIsFogged()
    {
        var clock = new GameClock();
        clock.SetVirtual(100);
        var world = new WorldState(clock);
        var zilean = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(3000, 3000)) { ChampionName = "Zilean" });
        var foe = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(3300, 3000)) { ChampionName = "Annie" });
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var orderObserver = new ReplicationObserver(1, Team.Order, zilean.NetId, new CaptureReplicationTransport());
        var chaosObserver = new ReplicationObserver(2, Team.Chaos, foe.NetId, new CaptureReplicationTransport());
        delivery.AddObserver(orderObserver);
        delivery.AddObserver(chaosObserver);
        delivery.Flush();
        TestAssert.True(orderObserver.View.Visible.Contains(foe.NetId), "the foe starts in sight");
        foe.Position = new Position2(11000, 11000);
        delivery.Flush();
        TestAssert.True(orderObserver.View.Known.Contains(foe.NetId) && !orderObserver.View.Visible.Contains(foe.NetId),
            "the foe is held but fogged for Zilean's team");
        PlaintextPacket Fx(uint id, uint bind) => FxPackets.CreateGroup(1, id, foe.Position, new NavigationBounds(0, 0, 0, 0), (_, _) => 0,
            targetNetId: foe.NetId, bindNetId: bind);
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9101, foe.NetId), 0x9101, foe.Position, BindNetId: foe.NetId, FowTeam: Team.Order));
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9102, foe.NetId), 0x9102, foe.Position, BindNetId: foe.NetId));
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9103, 0), 0x9103, foe.Position, FowTeam: Team.Order));
        delivery.Enqueue(new EffectCreateReplicationMessage(Fx(0x9104, foe.NetId), 0x9104, foe.Position, BindNetId: foe.NetId, FowTeam: Team.Chaos));
        delivery.Flush();
        TestAssert.True(orderObserver.View.Effects.Contains(0x9101), "the bomb reaches its own team while its carrier is fogged");
        TestAssert.False(orderObserver.View.Effects.Contains(0x9102), "an effect with no own team needs sight of its unit");
        TestAssert.False(orderObserver.View.Effects.Contains(0x9103), "an effect on no bind is drawn at its point: it still needs sight of its target");
        TestAssert.False(orderObserver.View.Effects.Contains(0x9104), "another team's effect needs sight of its unit");
        TestAssert.True(new uint[] { 0x9101, 0x9102, 0x9103, 0x9104 }.All(chaosObserver.View.Effects.Contains), "the foe's own team sees its unit");
        TestAssert.False(orderObserver.View.Visible.Contains(foe.NetId), "the effect reveals nothing");
    }

    // Batch 306 (owner decision, 26 October 2026; batch 264's open question 1): a script unit (BBSpawnMinion, EntityKind.Monster) without a
    // VisibilitySize sees its own perception bubble, (percent + 1) * CharacterData PerceptionBubbleRadius + flat, the bubble the client's
    // shared minion creation handler makes for it. Before: no sight.
    // Wards keep their script's 1100 (owner decision); batch 310 settles the VisibilitySize units and the Rally beacon (the check below).
    [Check("replication.script-unit-sight-is-its-character-bubble")]
    public static void ScriptUnitSightIsItsCharacterBubble()
    {
        var world = new WorldState(new GameClock());
        Entity Unit(uint id, string character, double x) =>
            world.Register(new Entity(id, EntityKind.Monster, UnitType.Monster, Team.Order, new(x, 5000)) { Name = character, ChampionName = character });
        var cube = Unit(1, "TestCube", 1000);           // Singed's trail, Akali's smoke: ServerOnly (its NeverRender), no bubble
        var mushroom = Unit(2, "TeemoMushroom", 3000);  // BantamTrap's BuffOnUpdateStats: IncPercentBubbleRadiusMod -0.7
        mushroom.PermanentStatMods[VisibilityRules.PermanentPercentBubbleKey] = -0.7;
        var turret = Unit(3, "HeimerTBlue", 5000);      // Data*PerceptionBubbleRadius 700
        var tenVision = Unit(4, "TestCubeRender10Vision", 7000); // radius 0 (a boolean-segment number)
        var wall = Unit(5, "AniviaIceblock", 9000);     // the 1350 default; V7.14 "Removed: Giving vision around the cast area"
        var sized = Unit(6, "TestCube", 11000);
        sized.VisibilitySize = 450.0;
        var ward = world.Register(new Entity(7, EntityKind.Ward, UnitType.Monster, Team.Order, new(13000, 5000))
            { IsWard = true, Name = "SightWard", ChampionName = "SightWard" });
        // Batch 473: the client's minion creation handler skips the bubble of a ServerOnly character; this asserted 1350.
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(cube), 1e-9, "a TestCube (ServerOnly) sees nothing");
        TestAssert.Near(1350.0 * 0.3, VisibilityRules.TerrainVisionRange(mushroom), 1e-6, "the mushroom sees 1350 x (1 - 0.7)");
        TestAssert.Near(700.0, VisibilityRules.TerrainVisionRange(turret), 1e-9, "a Heimerdinger turret sees its 700");
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(tenVision), 1e-9, "TestCubeRender10Vision has a radius of 0");
        TestAssert.Near(1350.0, VisibilityRules.TerrainVisionRange(wall), 1e-9, "Anivia's wall sees 1350");
        // Batch 310: a VisibilitySize unit sees its character bubble too (OnCreate ignores the size); batch 473: a TestCube has none
        // (ServerOnly), so it sees its size alone, as it did before batch 310 (this asserted 1350 in between).
        TestAssert.Near(450.0, VisibilityRules.TerrainVisionRange(sized), 1e-9, "a VisibilitySize TestCube sees its size of 450");
        TestAssert.Near(1100.0, VisibilityRules.TerrainVisionRange(ward), 1e-9, "a ward keeps its script's 1100 (owner decision)");
        var providers = VisibilityRules.BuildTeamProviders(world, Team.Order, [], _ => []);
        foreach (var (unit, radius) in new[] { (sized, 450.0), (mushroom, 405.0), (turret, 700.0), (wall, 1350.0) })
            TestAssert.True(providers.Any(provider => provider.Position == unit.Position && Math.Abs(provider.Radius - radius) < 1e-6 && provider.IsUnit),
                $"{unit.Name} is an IsUnit team provider of {radius}");
        TestAssert.False(providers.Any(provider => provider.Position == tenVision.Position), "a radius-0 unit is no provider");
        TestAssert.False(providers.Any(provider => provider.Position == cube.Position), "nor is a ServerOnly unit without a size");
        wall.Alive = false;
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(wall), 1e-9, "a dead unit sees nothing");
    }

    // Batch 310 (batch 306's open items 1 and 2): the client's minion creation handler makes the CharacterData bubble of every minion-class
    // unit whatever its spawn's VisibilitySize (it reads only the character data's perception bubble radius and the two modifiers; the
    // size field is read by no client code) and whatever its ward flag, so a VisibilitySize unit and the Rally beacon see that bubble too.
    // The size stays a second, server-made bubble (Reconstruction). Before: a VisibilitySize unit saw only its size, the beacon nothing.
    [Check("replication.visibility-size-unit-and-beacon-see-their-character-bubble")]
    public static void VisibilitySizeUnitAndBeaconSeeTheirCharacterBubble()
    {
        var world = new WorldState(new GameClock());
        Entity Unit(uint id, string character, double x, double size) =>
            world.Register(new Entity(id, EntityKind.Monster, UnitType.Monster, Team.Order, new(x, 5000))
                { Name = character, ChampionName = character, VisibilitySize = size });
        var drop = Unit(1, "TestCube", 1000, 450.0);                    // Talon's blood drop, Lux's beam cube: ServerOnly, no bubble
        var pulse = Unit(2, "TestCubeRender10Vision", 3000, 300.0);     // Xerath's Arcanopulse cubes: radius 0
        var sapling = Unit(3, "TestCubeRender10Vision", 5000, 1.0);     // Maokai's sapling: IncFlatBubbleRadiusMod +690
        sapling.PermanentStatMods[VisibilityRules.PermanentFlatBubbleKey] = 690.0;
        var beacon = world.Register(new Entity(4, EntityKind.Ward, UnitType.Monster, Team.Order, new(7000, 5000))
            { IsWard = true, IsSummonerBeacon = true, Name = "Beacon", ChampionName = "SummonerBeacon" });
        var ward = world.Register(new Entity(5, EntityKind.Ward, UnitType.Monster, Team.Order, new(9000, 5000))
            { IsWard = true, Name = "SightWard", ChampionName = "SightWard" });
        TestAssert.Near(1350.0, Amberline.Data.GameData.Default.Champion("SummonerBeacon").PerceptionBubbleRadius, 1e-9,
            "SummonerBeacon.inibin has no PerceptionBubbleRadius: the 1350 default");
        // Batch 473: TestCube is ServerOnly, so the client's creation handler makes it no CharacterData bubble; this asserted 1350.
        TestAssert.Near(450.0, VisibilityRules.TerrainVisionRange(drop), 1e-9, "a TestCube with a size of 450 sees that size alone");
        TestAssert.Near(450.0, VisibilityRules.VisibilitySizeRange(drop), 1e-9, "its size is the second bubble");
        TestAssert.Near(300.0, VisibilityRules.TerrainVisionRange(pulse), 1e-9, "a radius-0 cube sees its size (unchanged)");
        TestAssert.Near(691.0, VisibilityRules.TerrainVisionRange(sapling), 1e-9, "the sapling: 0 + 690 from its bubble, 1 + 690 from its size");
        TestAssert.Near(1350.0, VisibilityRules.TerrainVisionRange(beacon), 1e-9, "the beacon sees its character's 1350 bubble");
        TestAssert.Near(0.0, VisibilityRules.VisibilitySizeRange(beacon), 1e-9, "SummonerRally passes VisibilitySize 0.0");
        TestAssert.Near(1100.0, VisibilityRules.TerrainVisionRange(ward), 1e-9, "a ward keeps its script's 1100 (owner decision)");
        var providers = VisibilityRules.BuildTeamProviders(world, Team.Order, [], _ => []);
        TestAssert.True(providers.Any(provider => provider.Position == drop.Position && provider.Radius == 450.0 && provider.IsUnit),
            "the drop is an IsUnit provider of 450");
        TestAssert.True(providers.Any(provider => provider.Position == beacon.Position && provider.Radius == 1350.0 && provider.IsUnit),
            "the beacon is an IsUnit provider of 1350");
        TestAssert.False(providers.Any(provider => provider.Position == ward.Position), "a ward is still supplied by item vision");
        beacon.Alive = false;
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(beacon), 1e-9, "a dead beacon sees nothing");
        drop.Alive = false;
        TestAssert.Near(0.0, VisibilityRules.VisibilitySizeRange(drop), 1e-9, "nor does a dead unit's size");
    }

    // Batch 259: the server's sight of each structure is the client's bubble of it. A structure sees from its own position (the shrine
    // too, not from the fountain point) as a unit, with its bubble's grass-check radius: a turret's SelectionRadius 130, 0 for an
    // inhibitor or nexus. The fountain is lit by the shop: the shop's bubble has no unit flag, so it is a 1350 disc without a mask
    // (the default mask; Map1's ObjectCFG gives the shops no radius).
    [Check("replication.structure-sight-from-own-position-and-the-shop")]
    public static void StructureSightFromOwnPositionAndTheShop()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true);
        runtime.Session.CreateWorld();
        var world = runtime.World;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var providers = VisibilityRules.BuildTeamProviders(world, team, [], _ => []);
            var shop = MapDefinitions.ShopPosition(team);
            var shopSight = providers.Where(provider => provider.Position == shop).ToArray();
            TestAssert.Equal(1, shopSight.Length, $"{team}: the shop's bubble is a provider");
            TestAssert.Near(1350, shopSight[0].Radius, 0, $"{team}: the shop sees 1350");
            TestAssert.False(shopSight[0].IsUnit, $"{team}: the shop's bubble has no IsUnit, a disc without a mask");
            TestAssert.False(providers.Any(provider => provider.Position == MapDefinitions.TeamSpawn(team)),
                $"{team}: no provider at the fountain point");
            foreach (var structure in world.Entities.Values.Where(entity =>
                         entity.Team == team && entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus))
            {
                var own = providers.Where(provider => provider.Position == structure.Position).ToArray();
                TestAssert.Equal(1, own.Length, $"{structure.Name} sees from its own position");
                TestAssert.True(own[0].IsUnit, $"{structure.Name}: an IsUnit bubble, drawn with the wall mask");
                TestAssert.Near(1350, own[0].Radius, 0, $"{structure.Name}: PerceptionBubbleRadius 1350");
                TestAssert.Near(structure.Kind == EntityKind.Turret ? 130 : 0, own[0].SelectionRadius, 0,
                    $"{structure.Name}: the bubble's grass-check radius");
            }
        }
    }

    [Check("vision-b6-attack-reveal-fixed-position")]
    public static void AttackRevealStaysAtTheAttackAsServerSightOnly()
    {
        var world = new WorldState();
        world.Clock.SetVirtual(0);
        var attacker = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7000, 7000));
        var victim = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new(7100, 7000));
        world.Register(attacker); world.Register(victim);
        var capture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var observer = new ReplicationObserver(1, Team.Order, victim.NetId, capture);
        delivery.AddObserver(observer);
        delivery.Flush(); capture.Clear();
        // Map1 Constants.var: ca_RevealAttackerRange=400, TimeOut=4.5.
        attacker.AttackRevealPosition[(int)Team.Order] = attacker.Position;
        attacker.AttackRevealRemaining[(int)Team.Order] = 4.5;
        delivery.Flush();
        var id = attacker.NetId ^ 0x2000_0000u;
        // The stock client runs its spell-caster reveal in its own cast frame, which adds the very bubble the
        // 0x7B handler adds, so the server sends no bubble packet for an attack reveal; the reveal is server sight only.
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] is 0x7B or 0x38),
            "an attack reveal sends no perception bubble packet");
        TestAssert.True(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            _ => Array.Empty<VisionProvider>()).Any(provider => provider.Position == new Position2(7000, 7000) &&
                provider.Radius == 400), "living attacker supplies authoritative sight at the attack point");
        // The provider stays at the attack point once the attacker has moved on.
        attacker.Position = new(10000, 10000);
        victim.Position = new(3000, 3000);
        world.Clock.Advance(1);
        attacker.AttackRevealRemaining[(int)Team.Order] = 3.5;
        capture.Clear(); delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(attacker.NetId));
        TestAssert.True(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            _ => Array.Empty<VisionProvider>()).Any(provider => provider.Position == new Position2(7000, 7000) &&
                provider.Radius == 400), "moving out of sight leaves the original attack point alone");
        // A later attack replaces the retained latest-attack provider, even from fog.
        attacker.AttackRevealPosition[(int)Team.Order] = attacker.Position;
        attacker.AttackRevealRemaining[(int)Team.Order] = 4.5;
        capture.Clear(); delivery.Flush();
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] is 0x7B or 0x38),
            "a later attack sends no bubble packet either");
        world.Clock.Advance(4.5);
        attacker.AttackRevealRemaining[(int)Team.Order] = 0;
        TestAssert.False(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            _ => Array.Empty<VisionProvider>()).Any(provider => provider.Position == attacker.Position &&
                provider.Radius == 400), "an expired reveal supplies no sight");

        // Death ends this provider even while the latest attack timer remains.
        attacker.AttackRevealRemaining[(int)Team.Order] = 4.5;
        TestAssert.True(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            _ => Array.Empty<VisionProvider>()).Any(provider => provider.Position == attacker.Position &&
                provider.Radius == 400), "living attacker supplies authoritative sight");
        attacker.Alive = false;
        TestAssert.False(VisibilityRules.BuildTeamProviders(world, Team.Order, [],
            _ => Array.Empty<VisionProvider>()).Any(provider => provider.Position == attacker.Position &&
                provider.Radius == 400), "dead attacker supplies no authoritative sight");
        observer.View.Reset(); capture.Clear(); delivery.Flush();
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x7B &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]) == id),
            "reconnect after death restores no attack bubble");
    }

    [Check("replication.pet-vision-lifecycle")]
    public static void PetVisionLifecycle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (kind, skin, radius) in new[]
        {
            (EntityKind.ShacoBox, "ShacoBox", 700.0),
            (EntityKind.Pet, "AnnieTibbers", 1350.0),
            (EntityKind.YorickGhoul, "YorickSpectralGhoul", 1350.0),
            (EntityKind.YorickGhoul, "YorickRavenousGhoul", 1350.0),
            (EntityKind.YorickGhoul, "YorickDecayedGhoul", 1350.0),
            (EntityKind.ShacoClone, "Shaco", 1350.0),
            (EntityKind.YorickRevenant, "Yorick", 1350.0),
        })
        {
            AssertUnitSight(kind, skin, radius, team);
        }
    }

    [Check("replication.lane-vision-radii")]
    public static void LaneVisionRadii()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (type, suffix) in new[] { (MinionType.Melee, "Basic"),
                     (MinionType.Caster, "Wizard"), (MinionType.Siege, "MechCannon"),
                     (MinionType.Super, "MechMelee") })
        {
            var skin = (team == Team.Order ? "Blue" : "Red") + "_Minion_" + suffix;
            AssertUnitSight(EntityKind.Minion, skin, type == MinionType.Super ? 1350 : 1200, team, type);
        }
    }

    private static void AssertUnitSight(EntityKind kind, string skin, double radius,
        Team team, MinionType? minionType = null)
    {
        var data = GameData.Default.Champion(skin).Raw;
        var shippedRadius = data.TryGetValue(InibinReader.KeyHash("Data*PerceptionBubbleRadius"), out var value)
            ? value.AsDouble() : 1350.0;
        TestAssert.Near(radius, shippedRadius, 1e-9, skin + " shipped sight radius");
        var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
        var clock = new GameClock();
        clock.SetVirtual(100);
        var world = new WorldState(clock);
        var owner = new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(5000, 5000));
        var pet = new Entity(2, kind, kind == EntityKind.Minion ? UnitType.Minion : UnitType.Monster, team, new(10000, 0))
        {
            OwnerNetId = kind == EntityKind.Minion ? 0 : owner.NetId,
            MinionType = minionType,
            Name = skin,
        };
        var enemy = new Entity(3, EntityKind.Hero, UnitType.Hero, enemyTeam,
            pet.Position + new Position2(radius, 0));
        world.Register(owner);
        world.Register(pet);
        world.Register(enemy);
        var capture = new CaptureReplicationTransport();
        var enemyCapture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var observer = new ReplicationObserver(1, team, owner.NetId, capture);
        var enemyObserver = new ReplicationObserver(2, enemyTeam, enemy.NetId, enemyCapture);
        delivery.AddObserver(observer);
        delivery.AddObserver(enemyObserver);
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(enemy.NetId), skin + " reveals at its sight boundary");
        // Restated by batch 264: the client's minion creation handler makes the unit's own bubble (this CharacterData radius), so the
        // server sends no attached 0x26 for it (replication.unit-sight-client-makes-its-own-bubble); before, it sent one of this radius.
        var bubbleId = pet.NetId ^ 0x2000_0000u;
        TestAssert.False(observer.View.TerrainIds.Contains(bubbleId) || enemyObserver.View.TerrainIds.Contains(bubbleId),
            "no server terrain bubble for the unit");
        TestAssert.False(capture.Packets.Concat(enemyCapture.Packets).Any(packet => packet.Plaintext.Span[0] == 0x26),
            "no attached bubble packet: the client's object draws the unit's sight");
        enemy.Position += new Position2(1, 0);
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(enemy.NetId), "sight ends beyond the pet radius");
        enemy.Position -= new Position2(1, 0);
        owner.Alive = false;
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(enemy.NetId), "a surviving pet keeps sight after owner death");
        capture.Clear();
        pet.Alive = false;
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(enemy.NetId), "dead pets cannot reveal enemies");
        // The client stops drawing a dead unit's bubble itself, so no removal is sent either.
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x38), "no perception removal for the unit");
    }


    [Check("replication.observer-lifecycle")]
    public static void ObserverLifecycle()
    {
        var clock = new GameClock();
        clock.SetVirtual(100.0);
        var world = new WorldState(clock);
        var ownHero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0));
        var enemyHero = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(10_000, 10_000));
        var nearby = new Entity(3, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(100, 0));
        world.Register(ownHero);
        world.Register(enemyHero);
        world.Register(nearby);
        var open = new OpenVisionEnvironment();
        var transport = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), open,
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var observer = new ReplicationObserver(1, Team.Order, ownHero.NetId, transport);
        delivery.AddObserver(observer);
        delivery.Flush();
        TestAssert.True(observer.View.Known.Contains(ownHero.NetId));
        TestAssert.True(observer.View.Known.Contains(nearby.NetId));
        TestAssert.False(observer.View.Known.Contains(enemyHero.NetId));

        enemyHero.Position = new(100, 0);
        delivery.Flush();
        TestAssert.True(observer.View.Known.Contains(enemyHero.NetId));
        enemyHero.Position = new(10_000, 10_000);
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(enemyHero.NetId));
        var beforeHiddenCaster = transport.Packets.Count;
        delivery.Enqueue(CombatPackets.ApplyDamage(enemyHero.NetId, nearby.NetId, 1.0f));
        delivery.Flush();
        TestAssert.True(transport.Packets.Skip(beforeHiddenCaster)
            .Any(packet => packet.Plaintext.Span[0] == 0x68),
            "a visible victim keeps updates from a known hidden caster");

        delivery.Enqueue(VisionPackets.EnterTeamVisibility(enemyHero.NetId));
        delivery.Flush();
        TestAssert.False(transport.Packets.Any(packet => packet.Plaintext.Span.Length >= 5 &&
            packet.Plaintext.Span[0] == 0xEE &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == enemyHero.NetId),
            "a packet from a known but hidden actor must still require a visible actor");

        var castInfo = new SpellCastInfo(
            1, 2, 1, ownHero.NetId, 0, default, default,
            new[] { new SpellCastTarget(0xD00D, default, 1) },
            0, 0, 0, default);
        delivery.Enqueue(new SpellCastReplicationMessage(ownHero.NetId, castInfo));
        delivery.Flush();
        var cast = transport.Packets.Last(packet => packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode);
        var castTargetCount = cast.Plaintext.Span[9 + 47];
        TestAssert.Equal((byte)0, castTargetCount,
            "unknown AOE target rows are pruned while the caster cast remains");

        var destroyPacket = FxPackets.Kill(0x9000);
        delivery.Enqueue(new EffectCreateReplicationMessage(
            FxPackets.CreateGroup(1, 0x9000, new(0, 0), new NavigationBounds(0, 0, 0, 0), (_, _) => 0),
            0x9000,
            new Position2(0, 0)));
        delivery.Flush();
        TestAssert.True(observer.View.Effects.Contains(0x9000));
        delivery.Enqueue(new EffectDestroyReplicationMessage(destroyPacket, 0x9000));
        delivery.Flush();
        TestAssert.False(observer.View.Effects.Contains(0x9000));

        uint seenKiller = 0;
        delivery.EnqueueLifecycle(nearby.NetId, enemyHero.NetId, killer =>
        {
            seenKiller = killer;
            return SpawnPackets.StartSpawn();
        });
        delivery.Flush();
        TestAssert.Equal(enemyHero.NetId, seenKiller,
            "a known hidden killer remains a valid lifecycle identity");
        delivery.EnqueueLifecycle(nearby.NetId, 0xD00D, killer =>
        {
            seenKiller = killer;
            return SpawnPackets.StartSpawn();
        });
        delivery.Flush();
        TestAssert.Equal(nearby.NetId, seenKiller,
            "an unknown killer is rewritten to the victim identity");
    }

    [Check("replication.team-cache")]
    public static void TeamRawVisibilityIsPerFrame()
    {
        var world = new WorldState();
        world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0)));
        world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(10, 0)));
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        for (var index = 0; index < 5; index++)
        {
            delivery.AddObserver(new ReplicationObserver(
                (ulong)(index + 1), Team.Order, 1, new CaptureReplicationTransport()));
            delivery.AddObserver(new ReplicationObserver(
                (ulong)(index + 101), Team.Chaos, 2, new CaptureReplicationTransport()));
        }

        delivery.Flush();
        TestAssert.Equal(2, delivery.Instrumentation.RawVisibilityCalculations);
        TestAssert.Equal(1, delivery.Instrumentation.RawCalculationsByTeam[Team.Order]);
        TestAssert.Equal(1, delivery.Instrumentation.RawCalculationsByTeam[Team.Chaos]);
        world.Get(2)!.Position = new(1_000, 0);
        delivery.Flush();
        TestAssert.Equal(2, delivery.Instrumentation.RawVisibilityCalculations);
        TestAssert.Equal(2, delivery.Instrumentation.Frame);
    }

    [Check("replication.terrain-and-shrine")]
    public static void TerrainAndShrineContracts()
    {
        var service = Services();
        var transport = new CaptureReplicationTransport();
        var observer = new ReplicationObserver(1, Team.Order, 1, transport);
        var shrine = new Entity(8, EntityKind.Turret, UnitType.Turret, Team.Order, new(0, 0))
        {
            Name = "Turret_OrderTurretShrine",
            MaxHealth = 9_999,
            Health = 9_999,
        };
        var reveal = service.BuildReveal(shrine, observer);
        TestAssert.Equal(3, reveal.Count);
        TestAssert.Equal((byte)0xC2, reveal[0].Plaintext.Span[0]);
        TestAssert.Equal((byte)0xEE, reveal[1].Plaintext.Span[0]);
        AssertFountainProtection(reveal[2], shrine.NetId, 1u,
            "shrine reveal fountain protection");
        TestAssert.Equal(0, service.BuildEntityStats(shrine, observer).Count,
            "a shrine must never receive tower CC stats");
        var shrineLeave = service.BuildLeave(shrine, observer);
        TestAssert.Equal(2, shrineLeave.Count);
        TestAssert.Equal((byte)0x3A, shrineLeave[0].Plaintext.Span[0]);
        TestAssert.Equal((byte)0xEF, shrineLeave[1].Plaintext.Span[0]);

        var bootstrap = service.BuildTerrainBootstrap(observer);
        TestAssert.Equal(0, bootstrap.Count, "Order retains stock fountain shading without artificial fog bubbles");
        var chaosBootstrap = service.BuildTerrainBootstrap(new ReplicationObserver(2, Team.Chaos, 2, transport));
        TestAssert.Equal(0, chaosBootstrap.Count, "Chaos uses ordinary shrine sight without synthetic corner lighting");
        // Batch 259: a structure gets no terrain bubble (the client's obj_AI_Turret makes the shrine's own); the old 0x7B of the shrine at
        // the fountain point is gone, and replication.structure-sight-client-makes-its-own-bubble checks the delivery.

        // 19 October 2026: the ball's sight is the timed bubble of OrianaGhostMinion (BBAddPosPerceptionBubble), no longer a terrain attach of the
        // projection (BuildBallTerrainAttach is gone).

        // A walking minion's reveal carries the position followed by the live route (the collision park that once stopped a
        // congestion-waiting minion on the wire was deleted, owner decision 16 Oct 2026; the wait itself in batch 279).
        // Batch 276: inside the 0xC2 (movement data type 2, a two-point 0x64 entry), with no 0xC1 after it.
        var walkingMinion = new Entity(10, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(12, 34))
        {
            Waypoints = [new Position2(100, 200)],
            AnchorPath = [new Position2(12, 34)],
        };
        var minionReveal = service.BuildReveal(walkingMinion, observer);
        TestAssert.Equal(2, minionReveal.Count, "a minion reveal is its 0xC2 and 0xB5, with no 0xC1");
        var enter = minionReveal[0].Plaintext.Span;
        TestAssert.Equal(VisionPackets.MovementDataRoute, enter[VisionPackets.EnterVisibilityMovementTypeOffset],
            "a minion reveal must contain the current-position anchor and the live route");
        TestAssert.Equal(2u, (BinaryPrimitives.ReadUInt32LittleEndian(enter[12..]) >> 16) & 0x7f, "position and one waypoint");
    }

    [Check("replication.shrine-precombat-vision")]
    public static void ShrinePreCombatVisionUsesProviderAndTargetEdges()
    {
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        var shrinePosition = new Position2(13933.79, 14170.09);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, shrinePosition);
        var shrine = new Entity(2, EntityKind.Turret, UnitType.Turret, Team.Chaos,
            shrinePosition)
        {
            Name = "Turret_ChaosTurretShrine",
            MaxHealth = 100.0,
            Health = 100.0,
        };
        var target = new Entity(3, EntityKind.Minion, UnitType.Minion, Team.Order,
            shrine.Position);
        world.Register(hero);
        world.Register(shrine);
        world.Register(target);
        var transport = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var observer = new ReplicationObserver(1, Team.Order, hero.NetId, transport);
        delivery.AddObserver(observer);

        delivery.PrepareShrineVisibility((_, _) => false);
        TestAssert.True(observer.View.Visible.Contains(shrine.NetId));
        TestAssert.Equal(1, transport.Packets.Count(packet => packet.Plaintext.Span[0] == 0xC2));
        TestAssert.Equal(1, transport.Packets.Count(packet => packet.Plaintext.Span[0] == 0xEE));

        // Keep the target as an existing valid target while the provider is
        // moved away: target visibility is an independent source-preserving
        // condition and must not trigger a falling edge.
        shrine.AttackTarget = target.NetId;
        hero.Position = shrinePosition;
        delivery.PrepareShrineVisibility((_, _) => true);
        TestAssert.True(observer.View.Visible.Contains(shrine.NetId));

        target.Alive = false;
        hero.Position = new(10_000, 10_000);
        delivery.PrepareShrineVisibility((_, _) => false);
        TestAssert.True(observer.View.Visible.Contains(shrine.NetId),
            "source shrine hysteresis keeps the first out-of-vision tick visible");
        world.Clock.Advance(2.1);
        var beforeFall = transport.Packets.Count;
        delivery.PrepareShrineVisibility((_, _) => false);
        var falling = transport.Packets.Skip(beforeFall).ToArray();
        TestAssert.False(observer.View.Visible.Contains(shrine.NetId));
        TestAssert.Equal(1, falling.Count(packet => packet.Plaintext.Span[0] == 0x6D));
        TestAssert.Equal(1, falling.Count(packet => packet.Plaintext.Span[0] == 0xC8));
        TestAssert.Equal(1, falling.Count(packet => packet.Plaintext.Span[0] == 0xEF));
        TestAssert.False(falling.Any(packet => packet.Plaintext.Span[0] == 0x3A),
            "source shrine falling edge clears only the team bit");
        TestAssert.Equal<uint?>(null, shrine.AttackTarget);
    }

    [Check("replication.production-injured-reentry-hp")]
    public static void ProductionReentryKeepsMaxHealth()
    {
        var navigation = NavigationGrid.LoadFile(
            MapDefinitions.NavigationGridPath(GameDataPaths.Default),
            NavigationGridSettings.Default);
        var runtime = MatchRuntime.CreateProduction(navigation, solo: true);
        runtime.World.Clock.SetVirtual(0.0);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0))
        {
            Name = "Annie",
            ChampionName = "Annie",
            MaxHealth = 500.0,
            Health = 500.0,
        };
        var minion = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(0, 0))
        {
            Name = "SRU_OrderMinionMelee",
            MaxHealth = 500.0,
            Health = 100.0,
            AttackDamage = 20.0,
            Speed = 325.0,
            AttackSpeedMod = 1.0,
        };
        runtime.World.Register(hero);
        runtime.World.Register(minion);
        var transport = new CaptureReplicationTransport();
        var observer = new ReplicationObserver(1, Team.Order, hero.NetId, transport);
        runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();

        var initial = transport.Packets.Single(packet => packet.EntityReferences.Any(
                reference => reference.NetId == minion.NetId) &&
            packet.Plaintext.Length == 111 && packet.Plaintext.Span[0] == 0xCC);
        TestAssert.Equal(111, initial.Plaintext.Length,
            "first minion reveal uses the complete creation stats template");
        // Batch 701: the creation block's mHP is the unit's own health, as retail's first replication flush sends every field's value
        // ("Delta replication"). It was the maximum, which every mid-life push of the same
        // block reused and drew an injured unit's bar full (Tormented Soil's shred on a minion).
        TestAssert.Equal(100.0f, BitConverter.ToSingle(initial.Plaintext.Span[19..23]),
            "creation CC current HP is the injured value");
        TestAssert.Equal(500.0f, BitConverter.ToSingle(initial.Plaintext.Span[27..31]),
            "creation CC max HP uses the max-health template");
        var firstRevealIndex = transport.Packets.ToList().FindIndex(packet =>
            packet.EntityReferences.Any(reference => reference.NetId == minion.NetId) &&
            packet.Plaintext.Span[0] == 0xC2);
        var firstCurrentIndex = transport.Packets.ToList().FindIndex(packet =>
            packet.EntityReferences.Any(reference => reference.NetId == minion.NetId) &&
            packet.Plaintext.Length == 27 && packet.Plaintext.Span[0] == 0xCC);
        TestAssert.True(firstRevealIndex > transport.Packets.ToList().IndexOf(initial) &&
            firstCurrentIndex > firstRevealIndex,
            "first reveal order is creation CC, C2, then current HP/attack-speed CC");
        TestAssert.Equal(100.0f, BitConverter.ToSingle(
            transport.Packets[firstCurrentIndex].Plaintext.Span[19..23]),
            "first current-HP CC preserves the injured value");

        minion.Position = new(10_000, 10_000);
        runtime.Delivery.Flush();
        // Production delivery timers use the combat simulation domain.  Move
        // that authoritative clock for this direct observer check instead of
        // advancing the wall/virtual world clock, which is intentionally a
        // separate clock in a composed match.
        runtime.Combat.RestoreClock(2.1);
        runtime.Delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(minion.NetId),
            "injured minion must leave visibility after its fog grace expires");
        minion.Position = new(0, 0);
        runtime.Delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(minion.NetId),
            "injured minion must re-enter visibility");

        var refresh = transport.Packets.Last(packet => packet.EntityReferences.Any(
                reference => reference.NetId == minion.NetId) &&
            packet.Plaintext.Span[0] == 0xCC && packet.Plaintext.Length == 27);
        TestAssert.Equal(27, refresh.Plaintext.Length,
            "re-entry uses current HP/attack-speed replication and cannot overwrite max HP");
        TestAssert.Equal(0x2001u, BitConverter.ToUInt32(refresh.Plaintext.Span[15..19]),
            "only current HP and attack speed are present in the re-entry map");
        TestAssert.Equal(100.0f, BitConverter.ToSingle(refresh.Plaintext.Span[19..23]),
            "re-entry current-HP CC preserves the injured value");
        TestAssert.Equal(1.0f, BitConverter.ToSingle(refresh.Plaintext.Span[23..27]),
            "re-entry also restores the observed attack-speed multiplier");
        var movement = transport.Packets.Last(packet => packet.EntityReferences.Any(
                reference => reference.NetId == minion.NetId) &&
            packet.Plaintext.Span[0] == 0xCC && packet.Plaintext.Length == 23 &&
            packet.Plaintext.Span[10] == 0x08 &&
            BitConverter.ToUInt32(packet.Plaintext.Span[15..]) == 0x04);
        TestAssert.Equal(23, movement.Plaintext.Length,
            "re-entry movement speed uses a narrow map-8 packet");
        TestAssert.Equal(325.0f, BitConverter.ToSingle(movement.Plaintext.Span[19..]),
            "re-entry movement speed carries the current NPC value");
    }

    [Check("replication.production-ball-late-reveal")]
    public static void ProductionBallLateRevealUsesCachedTemplateThenC2()
    {
        var navigation = NavigationGrid.LoadFile(
            MapDefinitions.NavigationGridPath(GameDataPaths.Default),
            NavigationGridSettings.Default);
        var runtime = MatchRuntime.CreateProduction(navigation, solo: true);
        runtime.World.Clock.SetVirtual(0.0);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0))
        {
            Name = "Annie",
            ChampionName = "Annie",
            MaxHealth = 500.0,
            Health = 500.0,
        };
        runtime.World.Register(hero);
        var ball = new BallProjection(9, hero.NetId, Team.Order, new(0, 0));
        runtime.Delivery.Balls.Add(ball);
        var firstTransport = new CaptureReplicationTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order, hero.NetId, firstTransport));
        runtime.Delivery.Flush();

        var secondTransport = new CaptureReplicationTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(2, Team.Order, hero.NetId, secondTransport));
        runtime.Delivery.Flush();
        var ballPackets = secondTransport.Packets.Where(packet => packet.EntityReferences.Any(
            reference => reference.NetId == ball.NetId)).ToArray();
        TestAssert.Equal(1, ballPackets.Count(packet => packet.Plaintext.Length == 111 &&
            packet.Plaintext.Span[0] == 0xCC),
            "late observer receives one cached ball stats template");
        TestAssert.Equal(1, ballPackets.Count(packet => packet.Plaintext.Span[0] == 0xC2),
            "late observer receives one ball C2 reveal");
        TestAssert.Equal(1, ballPackets.Count(packet => packet.Plaintext.Span[0] == 0xB5),
            "late ball reveal includes the source local-visibility healthbar packet");
        TestAssert.False(ballPackets.Any(packet => packet.Plaintext.Span[0] == 0xC1),
            "late ball reveal does not duplicate the movement packet");
    }

    [Check("replication.production-ball-current-projection")]
    public static void ProductionBallProjectionTracksTheBallUnit()
    {
        var navigation = NavigationGrid.LoadFile(
            MapDefinitions.NavigationGridPath(GameDataPaths.Default),
            NavigationGridSettings.Default);
        var runtime = MatchRuntime.CreateProduction(navigation, solo: true);
        var spawn = new Position2(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, spawn)
        {
            Name = "Orianna", ChampionName = "Orianna", MaxHealth = 500.0, Health = 500.0,
        };
        runtime.World.Register(hero);
        // The ball is a script-spawned placemarker unit (BBSpawnMinion OriannaBall); its projection follows the unit and goes when the unit ends.
        var ball = runtime.World.Register(new Entity(9, EntityKind.Monster, UnitType.Monster, Team.Order, spawn)
        {
            Name = "OriannaBall", ChampionName = "OriannaBall", OwnerNetId = hero.NetId, Placemarker = true, Protected = true,
            Created = false, Alive = true, Health = 1, MaxHealth = 1,
        });
        runtime.Delivery.SetManagedBallProjection(new BallProjection(9, hero.NetId, hero.Team, ball.Position));

        runtime.Delivery.Flush();
        ball.Position = new Position2(ball.Position.X + 120.0, ball.Position.Z + 80.0);
        runtime.Delivery.Flush();
        TestAssert.Equal(ball.Position, runtime.Delivery.Balls.Single(projection => projection.NetId == 9).Position,
            "the managed projection takes the ball unit's position on each visibility refresh");
        TestAssert.False(runtime.Delivery.Balls.Single(projection => projection.NetId == 9).NoRender, "a visible ball is not no-render");

        ball.Buffs.Add(new BuffState("ExpirationTimer", new BuffPayload { NoRender = true }, BuffType.Internal, 0, 1.0, hero.NetId));
        runtime.Delivery.Flush();
        TestAssert.True(runtime.Delivery.Balls.Single(projection => projection.NetId == 9).NoRender,
            "the ball unit's SetNoRender status reaches the projection");

        ball.Alive = false;
        runtime.Delivery.Flush();
        TestAssert.False(runtime.Delivery.Balls.Any(projection => projection.NetId == 9), "the projection goes with the unit");
    }

    [Check("replication.death-brush-lifecycle")]
    public static void DeathAndBrushLifecycle()
    {
        var clock = new GameClock();
        clock.SetVirtual(10.0);
        var world = new WorldState(clock);
        // Restated by batch 264 (bundle 4 red after batch 259): the observer is Chaos. Every playing team now has its shop's sight
        // (VisibilityRules.BuildTeamProviders: the shop's own bubble, 1350, without the unit flag, so the client draws
        // its whole disc, brush included), and the Order shop (-97.4, 1033.8) stands 1052 from this fixture's brush strip at (100, 0),
        // so an Order observer saw the "brush" minion through it. The Chaos shop is 5600 or more from every point here.
        var own = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(0, 0));
        var enemy = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0));
        var minion = new Entity(3, EntityKind.Minion, UnitType.Minion, Team.Order, new(0, 0));
        world.Register(own);
        world.Register(enemy);
        world.Register(minion);
        var capture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new BrushEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.AddObserver(new ReplicationObserver(1, Team.Chaos, own.NetId, capture));
        delivery.Flush();
        var observer = delivery.Observers[0];
        TestAssert.True(observer.View.Visible.Contains(enemy.NetId));
        TestAssert.True(observer.View.Visible.Contains(minion.NetId));

        minion.Position = new(100, 0); // target brush
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(minion.NetId));
        minion.Position = new(0, 0);
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(minion.NetId));

        // Out of sight a lane minion is hidden at once: the stock client has no fog grace (A4 audit, 18 Oct 2026).
        minion.Position = new(5_000, 0);
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(minion.NetId));
        TestAssert.True(observer.View.Known.Contains(minion.NetId));

        enemy.Alive = false;
        enemy.Position = new(10_000, 10_000);
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(enemy.NetId), "known corpses remain visible");
        enemy.Alive = true;
        delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(enemy.NetId), "reincarnation does not reveal from fog");
        enemy.Position = new(0, 0);
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(enemy.NetId));
        own.Alive = false;
        own.Position = new(10_000, 10_000);
        delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(own.NetId), "own hero remains visible while dead");
    }

    [Check("replication.ezreal-r-active-flight")]
    public static void EzrealRActiveFlightUsesKnownHiddenVisibilityAndDedup()
    {
        var world = new WorldState();
        var observerHero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0))
        {
            Name = "Annie", ChampionName = "Annie", MaxHealth = 500.0, Health = 500.0,
        };
        var caster = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(5_000, 5_000))
        {
            Name = "Ezreal", ChampionName = "Ezreal", MaxHealth = 500.0, Health = 500.0,
        };
        world.Register(observerHero);
        world.Register(caster);

        var hiddenCapture = new CaptureReplicationTransport();
        var ownerCapture = new CaptureReplicationTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var hiddenObserver = new ReplicationObserver(1, Team.Order, observerHero.NetId, hiddenCapture);
        var ownerObserver = new ReplicationObserver(2, Team.Chaos, caster.NetId, ownerCapture);
        delivery.AddObserver(hiddenObserver);
        delivery.AddObserver(ownerObserver);
        delivery.Flush();

        const uint missileId = 0x4002_0001;
        var info = new SpellCastInfo(SpellSystem.ProtocolHash("EzrealTrueshotBarrage"),
            caster.NetId, 3, caster.NetId, missileId,
            new Position3(0, 100, 0), new Position3(0, 100, 0),
            Array.Empty<SpellCastTarget>(), 0, 0, 3, new Position3(5_000, 100, 5_000));

        var activeSnapshot = new MissileReplicationMessage(caster.NetId,
            new Position3(1_000, 100, 0), new Position3(5_000, 100, 5_000),
            new Position3(-1, 0, -1), new Position3(-1_000, 0, 0),
            new Position3(5_000, 100, 5_000), new Position3(0, 100, 0),
            new Position3(5_000, 100, 5_000), 2_000, 0.5f, 0, info,
            RefreshOnly: true);
        delivery.EnqueueMissile(activeSnapshot);
        delivery.Flush();

        TestAssert.Equal(0, hiddenCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x41),
            "never-known caster remains deferred");
        TestAssert.False(hiddenObserver.View.Effects.Contains(missileId));

        // Roster creation makes the caster known even while fog hides it. The
        // next active-flight snapshot is the same typed producer payload that
        // was deferred while this observer had never registered the caster.
        hiddenObserver.View.Known.Add(caster.NetId);
        hiddenCapture.Clear();
        ownerCapture.Clear();
        delivery.EnqueueCast(new SpellCastReplicationMessage(caster.NetId, info));
        delivery.EnqueueMissile(activeSnapshot);
        delivery.Flush();

        TestAssert.Equal(1, hiddenCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x41),
            "known hidden caster receives one current-position R missile when the flight enters vision");
        TestAssert.False(hiddenCapture.Packets.Any(packet => packet.Plaintext.Span[0] == 0xBD),
            "hidden caster does not leak the launch cast before the projectile is visible");
        TestAssert.False(hiddenCapture.Packets.Any(packet => packet.Plaintext.Span[0] is 0xC2 or 0xEE),
            "active R presentation does not reveal or update the hidden caster actor");
        TestAssert.Equal(1, ownerCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0xBD),
            "owner receives the cast answer");
        TestAssert.Equal(0, ownerCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x41),
            "owner cast answer is not duplicated by the active-flight refresh");
        TestAssert.True(hiddenObserver.View.Effects.Contains(missileId));
        TestAssert.True(ownerObserver.View.Effects.Contains(missileId));

        var destroy = SpellPackets.DestroyMissile(missileId);
        delivery.Enqueue(new EffectDestroyReplicationMessage(destroy, missileId));
        delivery.Flush();
        TestAssert.False(hiddenObserver.View.Effects.Contains(missileId),
            "destroy retires the late-revealed missile ID");
        TestAssert.False(ownerObserver.View.Effects.Contains(missileId),
            "destroy retires the owner missile ID");
    }

    /// <summary>
    /// ChainMissileSync goes to the clients that hold the chain missile (the cast that created it was delivered to them) and
    /// lists, for each, the hops it knows: the client turns each net ID into an object id and a unit it never learned of would end
    /// the chain at that slot.
    /// </summary>
    [Check("replication.chain-missile-sync-reaches-the-views-that-hold-the-missile-with-the-hops-they-know")]
    public static void ChainMissileSyncRoutesByMissileAndFiltersHops()
    {
        var world = new WorldState();
        var caster = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(1000, 1000)) { Name = "Ryze", ChampionName = "Ryze", MaxHealth = 500.0, Health = 500.0 };
        var foe = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(1200, 1000)) { Name = "Foe", ChampionName = "Annie", MaxHealth = 500.0, Health = 500.0 };
        var other = new Entity(3, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(1300, 1000)) { Name = "Other", ChampionName = "Annie", MaxHealth = 500.0, Health = 500.0 };
        world.Register(caster); world.Register(foe); world.Register(other);
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(), _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var holder = new CaptureReplicationTransport();
        var stranger = new CaptureReplicationTransport();
        var holderObserver = new ReplicationObserver(1, Team.Order, caster.NetId, holder);
        var strangerObserver = new ReplicationObserver(2, Team.Order, caster.NetId, stranger);
        delivery.AddObserver(holderObserver);
        delivery.AddObserver(strangerObserver);
        delivery.Flush();
        // The holder knows two of the three units; the stranger never got the cast.
        holderObserver.View.Known.UnionWith(new uint[] { caster.NetId, foe.NetId, other.NetId });
        strangerObserver.View.Known.UnionWith(new uint[] { caster.NetId, foe.NetId, other.NetId });
        const uint missile = 0x4002_0001;
        var info = new SpellCastInfo(SpellSystem.ProtocolHash("SpellFlux"), caster.NetId, 1, caster.NetId, missile,
            new(1200, 0, 1000), new(1200, 0, 1000), [new(foe.NetId, new(1200, 0, 1000), 0)], 0, 0, 2, new(1000, 0, 1000));
        delivery.EnqueueCast(new SpellCastReplicationMessage(caster.NetId, info));
        delivery.Flush();
        TestAssert.True(holderObserver.View.Effects.Contains(missile) && strangerObserver.View.Effects.Contains(missile),
            "both views were told of the missile by the cast; the second one is made a stranger below");
        strangerObserver.View.Effects.Clear();
        holder.Clear(); stranger.Clear();
        // A hop nobody told this client about (id 99) would end its chain there: the list is cut to what the view knows.
        delivery.EnqueueChainMissileSync(new ChainMissileSyncReplicationMessage(missile, caster.NetId,
            new uint[] { foe.NetId, 99, other.NetId, foe.NetId }));
        delivery.Flush();
        TestAssert.Equal(0, stranger.Packets.Count(packet => packet.Plaintext.Span[0] == 0x6F),
            "a client that never got the missile has nothing to feed the list to");
        var sync = holder.Packets.Single(packet => packet.Plaintext.Span[0] == 0x6F).Plaintext.ToArray();
        TestAssert.Equal(141, sync.Length, "sizeof(ChainMissileSync)");
        TestAssert.Equal(missile, BinaryPrimitives.ReadUInt32LittleEndian(sync[1..]), "FromID is the missile");
        TestAssert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(sync[5..]), "the unknown hop is cut out");
        TestAssert.Equal(caster.NetId, BinaryPrimitives.ReadUInt32LittleEndian(sync[9..]), "owner is the caster");
        TestAssert.Equal(new uint[] { foe.NetId, other.NetId, foe.NetId },
            Enumerable.Range(0, 3).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(sync[(13 + 4 * index)..])).ToArray(), "hops in order");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sync[25..]), "unused slots are zero");

        // A chain attack's missile came with the basic-attack packet, so the views that got that packet (attacker and attacked unit
        // known, one of them seen) are the holders; the missile ledger does not list attack missiles.
        holder.Clear(); stranger.Clear();
        delivery.EnqueueChainMissileSync(new ChainMissileSyncReplicationMessage(0x4002_0002, caster.NetId,
            new uint[] { foe.NetId, other.NetId }, Attack: true));
        delivery.Flush();
        TestAssert.Equal(1, holder.Packets.Count(packet => packet.Plaintext.Span[0] == 0x6F), "an attack's list reaches a view that sees the swing");
        TestAssert.Equal(1, stranger.Packets.Count(packet => packet.Plaintext.Span[0] == 0x6F), "the other view sees it as well");
        strangerObserver.View.Known.Remove(foe.NetId);
        stranger.Clear();
        delivery.EnqueueChainMissileSync(new ChainMissileSyncReplicationMessage(0x4002_0002, caster.NetId,
            new uint[] { foe.NetId, other.NetId }, Attack: true));
        delivery.Flush();
        TestAssert.Equal(0, stranger.Packets.Count(packet => packet.Plaintext.Span[0] == 0x6F),
            "a view that never learned of the attacked unit did not get the attack packet either");
    }

    [Check("replication.spell-flight-visibility-boundaries")]
    public static void MissileRefreshRequiresPointVisionAndKnownHomingTarget()
    {
        var world = new WorldState();
        // Restated by batch 264 (bundle 4 red after batch 259): the viewer is Chaos. The Order shop's own bubble (1350, without the
        // unit flag, so its whole disc is seen, brush included) stands 1052 from this fixture's brush strip at
        // (100, 0); the Chaos shop is far from every point here.
        var viewer = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(0, 0));
        var caster = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new(5000, 5000));
        world.Register(viewer); world.Register(caster);
        var capture = new CaptureReplicationTransport();
        var observer = new ReplicationObserver(1, Team.Chaos, viewer.NetId, capture);
        observer.View.VisibilityRefreshSuppressed = true;
        observer.View.Known.UnionWith(new uint[] { 1, 2 });
        var delivery = new ReplicationDelivery(world, Services(), new BrushEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.AddObserver(observer);
        var info = new SpellCastInfo(SpellSystem.ProtocolHash("Disintegrate"), 2, 1, 2, 0x40020001,
            new(300, 0, 0), new(300, 0, 0), [new(3, new(300, 0, 0), 0)], 0, 0, 0, new(5000, 0, 5000));
        var snapshot = new MissileReplicationMessage(2, new(300, 0, 0), new(5000, 0, 5000),
            new(1, 0, 0), new(200, 0, 0), new(5000, 0, 5000), new(500, 0, 0),
            new(5000, 0, 5000), 1400, .5f, 0, info, RefreshOnly: true);
        delivery.EnqueueMissile(snapshot); delivery.Flush();
        TestAssert.Equal(0, capture.Packets.Count, "unknown homing target defers the entire snapshot");
        delivery.EnqueueMissile(snapshot with { RefreshOnly = false }); delivery.Flush();
        TestAssert.Equal(0, capture.Packets.Count, "visible launch still requires a known homing target");
        observer.View.Known.Add(3);
        delivery.EnqueueMissile(snapshot with { Position = new(100, 0, 0) }); delivery.Flush();
        TestAssert.Equal(0, capture.Packets.Count, "missile inside unseen brush remains hidden");
        // Outside the viewer's sight and its team's shop.
        delivery.EnqueueMissile(snapshot with { Position = new(5000, 0, 0) }); delivery.Flush();
        TestAssert.Equal(0, capture.Packets.Count, "missile outside vision radius remains hidden");
        delivery.EnqueueMissile(snapshot); delivery.Flush();
        TestAssert.Equal(1, capture.Packets.Count, "live point entering sight creates missile");
        TestAssert.Equal(1, (int)capture.Packets[0].Plaintext.Span[0x62 + 47], "known homing target retained");
        delivery.EnqueueMissile(snapshot); delivery.Flush();
        TestAssert.Equal(1, capture.Packets.Count, "refresh does not duplicate missile");
        // A spell launched at a visible point can be seen immediately even
        // when its owner is hidden and it has no visible target actor.
        delivery.EnqueueMissile(snapshot with { RefreshOnly = false,
            Info = info with { MissileNetId = 0x40020002, Targets = Array.Empty<SpellCastTarget>() } });
        delivery.Flush();
        TestAssert.Equal(2, capture.Packets.Count, "visible launch point uses missile sight");
        TestAssert.False(observer.View.Visible.Contains(caster.NetId), "neither path reveals caster");
    }

    private static Position2 Point(JsonElement value) =>
        new(value[0].GetDouble(), value[1].GetDouble());

    private static IReadOnlyList<VisionProvider> Providers(JsonElement value)
    {
        var result = new List<VisionProvider>();
        foreach (var provider in value.EnumerateArray())
            result.Add(new(Point(provider[0]), provider[1].GetDouble()));
        return result;
    }

    [Check("replication.fresh-first-sight-stats-supersede-queued-stale-stats")]
    public static void FreshFirstSightStatsSupersedeQueuedStaleStats()
    {
        // The stock client applies a 0xCC block only if its stored sync id is lower
        // (its replication receive handler), so an older queued copy of what first sight
        // just sent is dropped by the client. Delivery must not spend the packet on it.
        var template = new MinionStatsSnapshot(400f, 12f, 325f);
        PlaintextPacket Full(uint sync) => CombatPackets.MinionStats(5, template, sync);
        // (fresh stats built at first sight, queued stats, sent?, reason)
        var cases = new (string Name, PlaintextPacket[] Fresh, PlaintextPacket Queued, bool Sent)[]
        {
            ("older copy of the template", [Full(9)], Full(3), false),
            ("older narrow action-state delta", [Full(9)], CombatPackets.MinionActionState(5, 7, 4), false),
            ("older template split over narrow fresh packets",
                [CombatPackets.MinionCurrentStats(5, 400f, 1f, 9), CombatPackets.MinionMovementSpeed(5, 325f, 10)],
                CombatPackets.MinionMovementSpeed(5, 325f, 6), false),
            ("same sync id is never applied, so it stays visible to the checker", [Full(9)], Full(9), true),
            ("zero sync id likewise", [Full(9)], Full(0), true),
            ("older block carrying fields first sight did not send",
                [CombatPackets.MinionCurrentStats(5, 400f, 1f, 9)], Full(3), true),
            ("newer block than first sight", [Full(9)], Full(12), true),
        };
        foreach (var (name, fresh, queued, sent) in cases)
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0);
            var viewer = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000)));
            world.Register(new Entity(5, EntityKind.Minion, UnitType.Minion, Team.Order, new(7100, 7000))
            { Health = 400, MaxHealth = 400 });
            var capture = new CaptureReplicationTransport();
            var services = new ProtocolReplicationServices(
                (entity, observer) => Marker(),
                (entity, observer) => entity.NetId == 5 ? fresh : Array.Empty<PlaintextPacket>(),
                (entity, observer) => Array.Empty<PlaintextPacket>(),
                (ball, observer) => Marker(), _ => 1u, WireBounds);
            var delivery = new ReplicationDelivery(world, services, new OpenVisionEnvironment(),
                _ => Array.Empty<VisionProvider>(), (_, _) => false);
            delivery.AddObserver(new ReplicationObserver(1, Team.Order, viewer.NetId, capture));
            delivery.Enqueue(queued);
            delivery.Flush();
            var received = capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0xCC);
            TestAssert.Equal(fresh.Length + (sent ? 1 : 0), received, name);
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Summoner's Rift's waypoint grid centre (7000, 7000) is near enough for these checks' worlds.
    private static readonly NavigationBounds WireBounds = new(0, 14000, 0, 14000);

    private static ProtocolReplicationServices Services()
    {
        return new ProtocolReplicationServices(
            (entity, observer) => Marker(),
            (entity, observer) => Marker(),
            (entity, observer) => Marker(),
            (ball, observer) => Marker(),
            _ => 1u, WireBounds);
    }

    private static void AssertFountainProtection(PlaintextPacket packet,
        uint fountainId, uint expectedSyncId, string name)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(CombatPackets.CombatChannel, packet.Channel, name + " channel");
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability, name + " reliability");
        TestAssert.True(packet.Encrypt, name + " encryption");
        TestAssert.Equal(31, bytes.Length, name + " length");
        TestAssert.Equal((byte)0xCC, bytes[0], name + " opcode");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]), name + " header");
        TestAssert.Equal(expectedSyncId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..9]), name + " sync");
        TestAssert.Equal((byte)1, bytes[9], name + " map count");
        TestAssert.Equal((byte)2, bytes[10], name + " map");
        TestAssert.Equal(fountainId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..15]), name + " entity");
        TestAssert.Equal(0x7000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..19]), name + " mask");
        TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[19..23]), name + " invulnerable");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[23..27]), name + " targetable");
        TestAssert.Equal(CombatPackets.SFlagNontargetableAll,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[27..31]), name + " flags");
        TestAssert.Equal(1, packet.EntityReferences.Count, name + " reference count");
        TestAssert.Equal(LeaguePacketEntityRole.World, packet.EntityReferences[0].Role,
            name + " reference role");
        TestAssert.Equal(fountainId, packet.EntityReferences[0].NetId, name + " reference");
    }

    private static Entity RoutingEntity(
        JsonElement testCase, string idName, string positionName, Team team)
    {
        var entity = new Entity(testCase.GetProperty(idName).GetUInt32(),
            EntityKind.Hero, UnitType.Hero, team, Point(testCase.GetProperty(positionName)))
        {
            Name = team == Team.Order ? "Annie" : "Orianna",
            ChampionName = team == Team.Order ? "Annie" : "Orianna",
            MaxHealth = 100.0,
            Health = 100.0,
        };
        return entity;
    }

    private static void EnqueueRoutingEvent(
        ReplicationDelivery delivery, JsonElement testCase, Entity source, Entity target)
    {
        var kind = testCase.GetProperty("kind").GetString()!;
        switch (kind)
        {
            case "damage":
                delivery.Enqueue(CombatPackets.ApplyDamage(source.NetId, target.NetId, 17.5f));
                break;
            case "attack":
                delivery.Enqueue(CombatPackets.BasicAttack(source.NetId, target.NetId, 0x4002_0001u));
                break;
            case "cast":
                var position = new Position3(source.Position.X, 100.0, source.Position.Z);
                var targetPosition = new Position3(target.Position.X, 100.0, target.Position.Z);
                var info = new SpellCastInfo(
                    1, source.NetId, 1, source.NetId, 0x4002_0001u,
                    targetPosition, targetPosition,
                    new[] { new SpellCastTarget(target.NetId, targetPosition, 1) },
                    1.0f, 0.0f, 0, position);
                delivery.EnqueueCast(new SpellCastReplicationMessage(source.NetId, info));
                break;
            case "death":
            case "zero-killer-death":
                target.Alive = false;
                target.Health = 0.0;
                var killer = kind == "zero-killer-death" ? 0u : source.NetId;
                delivery.EnqueueLifecycle(target.NetId, killer, resolvedKiller =>
                    CombatPackets.NpcDie(target.NetId, resolvedKiller, 10.0f, opcode: 0x61));
                break;
            default:
                throw new InvalidOperationException("Unknown routing fixture kind: " + kind);
        }
    }

    private static byte RoutingOpcode(string kind) => kind switch
    {
        "damage" => 0x68,
        "attack" => 0x0D,
        "cast" => 0xBD,
        "death" or "zero-killer-death" => 0x61,
        _ => throw new InvalidOperationException("Unknown routing fixture kind: " + kind),
    };

    private static void AssertPaddedPlaintext(byte[] expected, byte[] actual, string label)
    {
        TestAssert.True(expected.Length >= actual.Length &&
                        expected.AsSpan(0, actual.Length).SequenceEqual(actual),
            label + " plaintext expected=" + Convert.ToHexString(expected) +
            " actual=" + Convert.ToHexString(actual));
        TestAssert.True(expected.AsSpan(actual.Length).IndexOfAnyExcept((byte)0) < 0,
            label + " plaintext has nonzero transport padding");
    }

    private static HashSet<uint> Ids(JsonElement values)
    {
        var result = new HashSet<uint>();
        AddIds(result, values);
        return result;
    }

    private static void AddIds(HashSet<uint> target, JsonElement values)
    {
        foreach (var value in values.EnumerateArray())
            target.Add(value.GetUInt32());
    }

    private static IReadOnlyList<PlaintextPacket> Marker() =>
        new[] { SpawnPackets.StartSpawn() };

    // Batch 329: the send policy of each packet type is the base class it inherits from: PolicyBroadcast (the visibility object's
    // broadcast), PolicyMapView, PolicyLocalView<PolicySelectedUnit>, and PolicySendToOwnerClientOnly<PolicyBroadcast> (the client
    // inlines its client-only filter before the broadcast). A roster of heroes every client holds; each hero's client is an observer of its own.
    private static (ReplicationDelivery Delivery, WorldState World, Dictionary<uint, ReplicationObserver> Views) BroadcastMatch(
        params (uint Id, Team Team, double X, double Z)[] heroes)
    {
        var clock = new GameClock();
        clock.SetVirtual(100.0);
        var world = new WorldState(clock);
        foreach (var hero in heroes)
            world.Register(new Entity(hero.Id, EntityKind.Hero, UnitType.Hero, hero.Team, new(hero.X, hero.Z))
            {
                Name = "Annie", ChampionName = "Annie", MaxHealth = 100.0, Health = 100.0,
            });
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        var views = new Dictionary<uint, ReplicationObserver>();
        foreach (var hero in heroes)
        {
            var observer = views[hero.Id] = new ReplicationObserver(hero.Id, hero.Team, hero.Id, new CaptureReplicationTransport());
            observer.View.Known.UnionWith(heroes.Select(item => item.Id));
            delivery.AddObserver(observer);
        }
        delivery.Flush();
        foreach (var observer in views.Values) ((CaptureReplicationTransport)observer.Transport).Clear();
        return (delivery, world, views);
    }

    private static IReadOnlyList<PlaintextPacket> Wire(ReplicationObserver observer) =>
        ((CaptureReplicationTransport)observer.Transport).Packets;

    private static bool Got(ReplicationObserver observer, byte opcode, uint netId) => Wire(observer).Any(packet =>
        packet.Plaintext.Length >= 5 && packet.Plaintext.Span[0] == opcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId);

    // Batch 457: a point turret (a server turret the clients hold as a minion, Entity.ReplicatedAsMinion) is known to every client from its
    // spawn, so the capture announcement it sources resolves "your team | enemy team" on a client that cannot see it (the client writes
    // "Unknown" for a source it cannot resolve), as a scene turret's would.
    [Check("replication.dominion-point-turret-is-known-to-every-client-from-its-spawn")]
    public static void PointTurretIsKnownToEveryClientFromItsSpawn()
    {
        var (delivery, world, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        const uint turret = 0x40000058;
        world.Register(new Entity(turret, EntityKind.Turret, UnitType.Turret, Team.Order, new(4342, 2516))
        {
            Name = "OdinOrderTurretShrine", MaxHealth = 100.0, Health = 100.0, ReplicatedAsMinion = true, Created = true, Announced = true,
        });
        delivery.Flush();
        TestAssert.True(views[1].View.Visible.Contains(turret), "its own team sees the point turret");
        var enemy = views[2].View;
        TestAssert.True(enemy.Known.Contains(turret) && !enemy.Visible.Contains(turret),
            $"the enemy client holds the point turret it cannot see (known {enemy.Known.Contains(turret)}, visible {enemy.Visible.Contains(turret)})");
    }

    // Batch 462: the capture trees change a point turret's sight with IncPermanentPercentBubbleRadiusMod (batch 456: -1 hidden, +1.5 to the team
    // that takes the point). Its team's client draws the turret's fog from the replicated mPercentBubbleRadiusMod ((percent + 1) * base + flat,
    // the client's bubble-radius update), so the modifier reaches that client as the minion's bubble block (map 0x08 bits 0 and 1);
    // the scene turrets keep their own.
    [Check("replication.dominion-point-turret-sight-modifier-reaches-its-team")]
    public static void PointTurretSightModifierReachesItsTeam()
    {
        var (delivery, world, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        const uint turret = 0x40000058;
        var unit = world.Register(new Entity(turret, EntityKind.Turret, UnitType.Turret, Team.Order, new(4342, 2516))
        {
            Name = "OdinOrderTurretShrine", MaxHealth = 100.0, Health = 100.0, ReplicatedAsMinion = true, Created = true, Announced = true,
        });
        unit.PermanentStatMods[VisibilityRules.PermanentPercentBubbleKey] = -1.0;
        delivery.Flush();
        TestAssert.True(BubbleBlocks(views[1], turret).Contains((0.0f, -1.0f)), "its team's client gets the hidden turret's -1 percent sight modifier");
        unit.PermanentStatMods[VisibilityRules.PermanentPercentBubbleKey] = 0.5;
        delivery.Flush();
        TestAssert.True(BubbleBlocks(views[1], turret).Contains((0.0f, 0.5f)), "and the +0.5 its team's capture leaves");
        TestAssert.False(BubbleBlocks(views[2], turret).Any(), "the enemy client, which does not draw the turret's fog, gets no bubble block");
    }

    // The 27-byte bubble blocks (map 0x08, bits 0 and 1: flat, percent) an observer got for a non-hero unit.
    private static List<(float Flat, float Percent)> BubbleBlocks(ReplicationObserver observer, uint netId) => Wire(observer)
        .Where(packet => packet.Plaintext.Length == 27 && packet.Plaintext.Span[0] == 0xCC && packet.Plaintext.Span[10] == 0x08 &&
                         BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == netId &&
                         BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == 0x03u)
        .Select(packet => (BitConverter.ToSingle(packet.Plaintext.Span[19..23]), BitConverter.ToSingle(packet.Plaintext.Span[23..27])))
        .ToList();

    // ChangeSlotSpellType (0x1A) is PolicyBroadcast; BBSetTargetingType sends it through TryBroadcast, and the client's
    // spell-targeting handler has no visibility test. A viewer in fog holds the new targeting type.
    [Check("replication.b329-slot-type-reaches-a-viewer-in-fog")]
    public static void SlotTypeReachesAViewerInFog()
    {
        var (delivery, _, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        var blind = views[2];
        TestAssert.True(blind.View.Known.Contains(1) && !blind.View.Visible.Contains(1), "the enemy holds the hero but cannot see it");
        delivery.Enqueue(SpellPackets.ChangeSlotSpellType(1, 1, 6));
        delivery.Flush();
        TestAssert.True(Got(blind, 0x1A, 1), "the blind enemy got the slot's targeting type (TryBroadcast)");
    }

    // CharSetCooldown (0x8A), ChangeSlotSpellOffsetTarget (0x37) and ChangeSlotSpellIcon (0x36) are
    // PolicySendToOwnerClientOnly: the unit's own client only, whoever sees the unit. A pet's client is its owner's (the spawn copies the
    // owner's visibility-object client id into the pet's).
    [Check("replication.b329-hud-slot-packets-reach-only-the-owner-client")]
    public static void HudSlotPacketsReachOnlyTheOwnerClient()
    {
        var (delivery, world, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (3, Team.Order, 1100, 1000),
            (2, Team.Chaos, 1200, 1000));
        world.Register(new Entity(10, EntityKind.Pet, UnitType.Minion, Team.Order, new(1050, 1000))
        {
            Name = "AnnieTibbers", MaxHealth = 100.0, Health = 100.0, OwnerNetId = 1,
        });
        delivery.Flush();
        TestAssert.True(views.Values.All(observer => observer.View.Visible.Contains(1) && observer.View.Visible.Contains(10)),
            "every client sees the hero and the pet");
        delivery.Enqueue(SpellPackets.SetCooldown(1, 0, 5.0f));
        delivery.Enqueue(SpellPackets.SpellOffsetTarget(1, 1, 3));
        delivery.Enqueue(SpellPackets.SetCooldown(10, 0, 5.0f));
        delivery.Flush();
        TestAssert.True(Got(views[1], 0x8A, 1) && Got(views[1], 0x37, 1) && Got(views[1], 0x8A, 10),
            "the owner's client got the hero's and the pet's slot packets");
        foreach (var other in new[] { views[3], views[2] })
            TestAssert.False(Got(other, 0x8A, 1) || Got(other, 0x37, 1) || Got(other, 0x8A, 10),
                $"client {other.ObserverId} (it sees the hero) got none of them (FilterClientOnly)");
    }

    // PolicyBroadcast, sent by the shipped building blocks through TryBroadcast, applied by obj_AI_Base_PImpl_Int handlers with no
    // visibility test: 0x99 BBSetPARColorOverride, 0x9E BBSetVoiceOverride, 0x9F BBPushCharacterData, 0x6A BBPopCharacterData and 0xBA
    // BBPushCharacterFade (a layer below full opacity as well as a restore).
    [Check("replication.b329-character-presentation-reaches-a-viewer-in-fog")]
    public static void CharacterPresentationReachesAViewerInFog()
    {
        var (delivery, _, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        var blind = views[2];
        delivery.Enqueue(SpellPackets.ChangeParColorOverride(1, new ParColorOverride(0xFF00FF00, 0xFF0000FF)));
        delivery.Enqueue(SpellPackets.ChangeCharacterVoice(1, "Shadow"));
        delivery.Enqueue(SpellPackets.CharacterFade(1, 7, 0.2f, 0.5f));
        delivery.EnqueueCharacterData(new CharacterDataReplicationMessage(1, 5, "Annie", UseSpells: false));
        delivery.Flush();
        TestAssert.False(blind.View.Visible.Contains(1), "the enemy cannot see the hero");
        foreach (var opcode in new byte[] { 0x99, 0x9E, 0xBA, 0x9F })
            TestAssert.True(Got(blind, opcode, 1), $"the blind enemy got 0x{opcode:X2} (TryBroadcast)");
        delivery.EnqueueCharacterData(new CharacterDataReplicationMessage(1, 5, null));
        delivery.Flush();
        TestAssert.True(Got(blind, 0x6A, 1), "and the pop of the layer");
    }

    // FaceDirection (0x53) is PolicyBroadcast (FacePosition, BBFaceDirection): the client's handler only
    // turns the unit (its face-vector and target-facing setters); the packet carries a direction, no position.
    [Check("replication.b329-facing-reaches-a-viewer-in-fog")]
    public static void FacingReachesAViewerInFog()
    {
        var (delivery, _, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        delivery.Enqueue(SpellPackets.FaceDirection(1, new Position3(0.0, 0.0, 1.0)));
        delivery.Flush();
        TestAssert.False(views[2].View.Visible.Contains(1), "the enemy cannot see the hero");
        TestAssert.True(Got(views[2], 0x53, 1), "the blind enemy got the facing (TryBroadcast)");
    }

    // CharSpawnPet (0x3C) is PolicyBroadcast: BBSpawnPet and BBCloneUnitPet send it through the owner's
    // NetVisibilityObject (TryBroadcast), so every client that holds the owner creates the pet at its spawn, in sight or not; the pet then
    // leaves that client's sight like any created unit. A lane minion (no 0x3C) is still created at first sight.
    [Check("replication.b329-pet-spawn-reaches-every-client-that-holds-its-owner")]
    public static void PetSpawnReachesEveryClientThatHoldsItsOwner()
    {
        var (delivery, world, views) = BroadcastMatch((1, Team.Order, 1000, 1000), (2, Team.Chaos, 13000, 13000));
        world.Register(new Entity(10, EntityKind.Pet, UnitType.Minion, Team.Order, new(1050, 1000))
        {
            Name = "AnnieTibbers", MaxHealth = 100.0, Health = 100.0, OwnerNetId = 1,
            PetState = new Amberline.Simulation.Pets.PetState(Amberline.Simulation.Pets.CloneKind.Tibbers, 1),
        });
        world.Register(new Entity(11, EntityKind.Minion, UnitType.Minion, Team.Order, new(1060, 1000))
        {
            Name = "Blue_Minion_Basic", MaxHealth = 100.0, Health = 100.0,
        });
        delivery.Flush();
        var blind = views[2];
        TestAssert.True(blind.View.Known.Contains(10), "the blind enemy created the pet (TryBroadcast)");
        TestAssert.False(blind.View.Visible.Contains(10), "and does not see it");
        TestAssert.True(Got(blind, VisionPackets.LeaveVisibility(10)[0].Plaintext.Span[0], 10), "the pet left its sight");
        TestAssert.False(blind.View.Known.Contains(11), "a lane minion still waits for first sight");
        TestAssert.True(views[1].View.Visible.Contains(10), "the owner sees its pet");
    }

    private sealed class CaptureReplicationTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();
        public IReadOnlyList<PlaintextPacket> Packets => _packets;
        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
    }

    private sealed class OpenVisionEnvironment : IVisionEnvironment
    {
        public OpenVisionEnvironment(double cellSize = 400.0) => CellSize = cellSize;
        public double CellSize { get; }
        public bool HasVisionLos(Position2 source, Position2 target) => true;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }

    private sealed class KickWallVisionEnvironment : IVisionEnvironment
    {
        public double CellSize => 400.0;
        public bool HasVisionLos(Position2 source, Position2 target) => target.X < 7120.0;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }

    private sealed class BrushEnvironment : IVisionEnvironment
    {
        public double CellSize => 400.0;
        public bool HasVisionLos(Position2 source, Position2 target) => true;
        public bool BrushAllowsVision(Position2 source, Position2 target) =>
            BrushIdAt(target) is not int targetBrush || BrushIdAt(source) == targetBrush;
        public int? BrushIdAt(Position2 point) => point.X is > 50.0 and < 200.0 ? 1 : null;
    }
}
