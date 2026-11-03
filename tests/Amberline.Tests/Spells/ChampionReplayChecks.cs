using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Champion replays against current-build recordings with preserved scenario inputs. Gameplay corpora use production hooks, duel
/// traces cross session delivery, and focused smoke checks isolate spell setup.
/// </summary>
public static class ChampionReplayChecks
{


    private static void PushCorpusStats(Entity unit, HeroStatsService stats,
        AnnieChecks.ReplayQueue queue, ReplicationSyncCounter syncIds)
    {
        if (unit.UnitType == UnitType.Hero)
        {
            stats.Push(unit);
            return;
        }
        var actionState = unit.Kind == EntityKind.ShacoBox ? 0u : 7u;
        queue.Enqueue(CombatPackets.MinionStats(unit.NetId,
            new MinionStatsSnapshot((float)unit.Health, (float)unit.AttackDamage,
                (float)unit.Speed, (float)unit.Armor, (float)unit.SpellBlock,
                (float)unit.AttackSpeedMod, (float)unit.HealthRegen, ActionState: actionState),
            syncIds.Next(unit.NetId)));
    }


    private static void ReplayDuelCase(JsonElement testCase, string caseContext, bool compareFixture = true)
    {
        var champion = testCase.GetProperty("champion").GetString()
            ?? throw new InvalidDataException(caseContext + " has no champion");
        var context = caseContext + "/" + champion;
        var duel = CreateProductionDuelRuntime(testCase, champion, context);
        var runtime = duel.SnapshotRuntime;
        var request = testCase.GetProperty("requests")[0];
        // The source matrix applies small per-case mutations after the
        // shared initial world has been created (for example disabling
        // Blitzcrank's R for Q/W/E cases and moving Orianna's dummy for W).
        // Hydrate that recorded input state before sending the request; never
        // hydrate expected output state.
        HydrateDuelRequestState(request, duel.Runtime);
        var actor = runtime.World.Get(request.GetProperty("actorNetId").GetUInt32())
            ?? throw new InvalidOperationException(context + " actor is missing");
        var plaintext = EncodeDuelRequest(request, actor.Position);
        plaintext[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(1, 4), actor.NetId);
        var connectionId = duel.ConnectionFor(actor);
        var accepted = false;
        // The request crosses the same session ownership/visibility boundary
        // as a native peer.  The production gameplay dispatcher is called only
        // by DuelSession.Receive; this check never invokes SpellSystem directly.
        duel.Runtime.Session.Receive(connectionId, 1,
            duel.Runtime.Cipher.Encrypt(plaintext), gameplayContext =>
            {
                accepted = duel.Runtime.Dependencies.Gameplay(gameplayContext);
                return accepted;
            });
        duel.Runtime.Delivery.Flush();

        var expectedPackets = testCase.GetProperty("expectedPeerPackets");
        if (testCase.GetProperty("case").TryGetProperty("wait", out var wait) &&
            wait.ValueKind == JsonValueKind.Number)
        {
            TestAssert.True(accepted, context + " request must be accepted");
            var remaining = wait.GetDouble();
            const double sourceTick = 1.0 / 30.0;
            while (remaining > 1e-12)
            {
                var step = Math.Min(sourceTick, remaining);
                duel.Runtime.Tick(step);
                duel.Runtime.Session.RunSchedulers();
                duel.Runtime.Delivery.Flush();
                remaining -= step;
            }
        }
        else
        {
            TestAssert.False(accepted, context + " passive/rejected request");

            // Silver Bolts is a passive slot.  The source acceptance case
            // deliberately follows the rejected command with the native
            // attack-to order, then advances the real fixed-step server so
            // the third hit is produced by combat rather than by a test-only
            // damage shortcut.
            if (testCase.GetProperty("case").TryGetProperty("passive", out var passive) &&
                passive.ValueKind == JsonValueKind.True)
            {
                var target = runtime.World.Entities.Values.FirstOrDefault(entity =>
                    entity.UnitType == UnitType.Hero && entity.NetId != actor.NetId);
                TestAssert.True(target is not null, context + " passive target is missing");
                var attack = EncodeAttackOrder(actor, target!);
                var attackAccepted = false;
                duel.Runtime.Session.Receive(connectionId, 1,
                    duel.Runtime.Cipher.Encrypt(attack), gameplayContext =>
                    {
                        attackAccepted = duel.Runtime.Dependencies.Gameplay(gameplayContext);
                        return attackAccepted;
                    });
                TestAssert.True(attackAccepted, context + " attack order must be accepted");
                duel.Runtime.Delivery.Flush();

                var remaining = 3.1;
                const double sourceTick = 1.0 / 30.0;
                while (remaining > 1e-12)
                {
                    var step = Math.Min(sourceTick, remaining);
                    duel.Runtime.Tick(step);
                    duel.Runtime.Session.RunSchedulers();
                    duel.Runtime.Delivery.Flush();
                    remaining -= step;
                }
            }
        }

        AssertDuelRetailPackets(champion, request.GetProperty("slot").GetInt32(), actor,
            duel.Order.Packets, duel.Chaos.Packets);
        if (!compareFixture) { runtime.Random.AssertComplete(); return; }
        CompareCorpusPackets(expectedPackets.GetProperty("0"), duel.Order.Packets, context + " peer 0");
        CompareCorpusPackets(expectedPackets.GetProperty("1"), duel.Chaos.Packets, context + " peer 1");
        CompareCorpusState(testCase.GetProperty("expectedFinalState"), runtime,
            champion, context + " final state");
        runtime.Random.AssertComplete();
    }


    private static void AssertDuelRetailPackets(string champion, int slot, Entity caster,
        IReadOnlyList<PlaintextPacket> order, IReadOnlyList<PlaintextPacket> chaos)
    {
        if (!(champion == "Ezreal" && slot == 2 || champion == "Blitzcrank" && slot == 0)) return;
        foreach (var (packets, team) in new[] { (order, Team.Order), (chaos, Team.Chaos) })
        {
            // Batch 52 (79d621b): the stock client runs RevealSpellCaster itself in its cast frame and adds the
            // positional 400 / 4.5 s bubble, so no 0x7B is sent to either team for Rocket Grab's reveal (RocketGrabMissile.inibin
            // SpellRevealsChampion = 1, still server sight). Ezreal's Arcane Shift bolt vision (0x26) is a server packet.
            var bubbleOpcode = champion == "Ezreal" ? 0x26 : 0x7B;
            var bubbles = packets.Where(packet => packet.Plaintext.Span[0] == bubbleOpcode).ToArray();
            var receives = champion == "Ezreal" && team == caster.Team;
            var expectedBubbles = receives ? 1 : 0;
            TestAssert.Equal(expectedBubbles, bubbles.Length, champion +
                $" ordinary vision team privacy caster={caster.NetId:X8} viewer={team}");
            foreach (var bubble in bubbles)
            {
                var bytes = bubble.Plaintext.Span;
                var target = caster.NetId == 0x40000001u ? 0x40000002u : 0x40000001u;
                // The timed bubble reaches a view once, with its remaining lifetime (ReplicationDelivery: the view must know the anchor unit
                // first); the script's one second (BBAddUnitPerceptionBubble Duration) has run down by the ticks since its creation.
                var lifetime = BinaryPrimitives.ReadSingleLittleEndian(bytes[21..]);
                TestAssert.True(lifetime > 0.9f && lifetime <= 1.0f, champion + $" ordinary sight lifetime {lifetime}");
                var expected = SpawnPackets.AddUnitPerceptionBubble(target, 100, lifetime,
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]), (uint)team);
                TestAssert.Equal(expected.Channel, bubble.Channel);
                TestAssert.Equal(expected.Reliability, bubble.Reliability);
                TestAssert.Equal(expected.Plaintext.ToArray(), bytes.ToArray(), champion + " shipped ordinary sight contract");
            }
            var faces = packets.Where(packet => packet.Plaintext.Span[0] == 0x53).ToArray();
            // Arcane Shift runs from its shipped script (phase 3, W31): the script faces the clicked point, the unit it picked after the blink, and the
            // engine faces the cast point, so the packets carry several updates. Rocket Grab runs from its shipped script too (phase 3, W27): the
            // script's BBFaceDirection and the child cast's engine facing send the same direction twice.
            TestAssert.True(faces.Length >= 1, champion + " target-facing update");
            if (champion == "Blitzcrank")
            {
                // Batch 279: Rocket Grab's windup stop seats Blitzcrank on his lattice anchor at once, where the grab leaves
                // him, and the script faces the cast point (7250, 7000) of both replayed cases from there.
                var dx = 7250.0 - caster.Position.X;
                var dz = 7000.0 - caster.Position.Z;
                var length = Math.Sqrt(dx * dx + dz * dz);
                var face = SpellPackets.FaceDirection(caster.NetId, new Position3(dx / length, 0, dz / length));
                TestAssert.Equal(face.Channel, faces[0].Channel);
                TestAssert.Equal(face.Reliability, faces[0].Reliability);
                TestAssert.Equal(face.Plaintext.ToArray(), faces[0].Plaintext.ToArray(), champion + " exact target-facing");
                // RocketGrabMissile.fx owns the gesture via its normal child
                // cast (batch 6); the older explicit Spell1 overlay is obsolete.
                TestAssert.Equal(0, packets.Count(packet => packet.Plaintext.Span[0] == 0xB8),
                    "Rocket Grab child cast owns its gesture");
                var child = packets.Single(packet => packet.Plaintext.Length > 15 &&
                    packet.Plaintext.Span[0] == 0xBD &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == 0x00DD4395);
                TestAssert.True(packets.ToList().IndexOf(child) > packets.ToList().IndexOf(faces[0]),
                    "Rocket Grab facing precedes its child cast");
                TestAssert.Equal(0, packets.Count(packet => packet.Plaintext.Span[0] == 0x26),
                    "Rocket Grab attack reveal stays fixed at the attack point");
                if (receives)
                    TestAssert.True(packets.ToList().IndexOf(bubbles[0]) <
                        packets.ToList().FindIndex(packet => packet.Plaintext.Span[0] == 0x0D),
                        "attack reveal precedes the follow-up swing");
            }
            else
            {
                // The bolt is the script's BBSpellCast child (EzrealArcaneShiftMissile, CastType 1, Instacast). Batch 582: it reaches the client as its
                // missile packet alone (cast info at 98), which builds the missile; it has no cast answer.
                var list = packets.ToList();
                var bolt = list.FindIndex(packet => packet.Plaintext.Length > 102 && packet.Plaintext.Span[0] == 0x41 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[100..]) == SpellSystem.ProtocolHash("EzrealArcaneShiftMissile"));
                TestAssert.True(bolt >= 0 && list.IndexOf(faces[0]) < bolt, "Arcane Shift target-facing precedes its bolt's cast");
            }
        }
    }



    private static ProductionDuelRuntime CreateProductionDuelRuntime(
        JsonElement testCase, string champion, string context)
    {
        // DuelFixture gives the selected champion only to the casting seat;
        // the other seat is the ordinary Annie dummy.  Keeping both seats on
        // the selected module makes passive/state composition differ before
        // the first request (especially for Blitz and Janna).
        var casterTeam = testCase.GetProperty("casterTeam").GetInt32();
        var orderChampion = casterTeam == 0 ? champion : "Annie";
        var chaosChampion = casterTeam == 1 ? champion : "Annie";
        var random = new RecordingRandomSource(
            testCase.TryGetProperty("randomCalls", out var randomCalls)
                ? randomCalls : default,
            "duel/" + context, testCase);
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: orderChampion, chaosChampion: chaosChampion,
            // DuelFixture.mark_ready/start has no settle delay; the live
            // server composition keeps its delayed loading policy separately.
            readySettleSeconds: 0.0, random: random);
        runtime.World.Clock.SetVirtual(0.0);

        // The source DuelFixture creates the world and performs its seat
        // lifecycle directly. Keep that setup shape while still exercising
        // the production MatchRuntime, Session, Delivery, and gameplay
        // boundaries for every request and simulation tick. Hydration occurs
        // before Spawn so the real burst observes the fixture's inputs.
        runtime.Session.CreateWorld();
        HydrateDuelInitialState(testCase, runtime);
        // These spell recordings predate idle acquisition. Start their
        // scripted actors stopped, preserving every recorded result and
        // random draw; live order checks cover automatic attacks separately.
        foreach (var hero in runtime.World.Heroes()) hero.HeroAutoAcquire = false;
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null,
            context + " order authentication");
        TestAssert.True(runtime.Session.Authenticate(2, runtime.Session.BuildKeyCheck(2)) is not null,
            context + " chaos authentication");
        _ = runtime.Session.Spawn(1);
        _ = runtime.Session.Spawn(2);
        TestAssert.True(runtime.Session.MarkReady(1), context + " order ready");
        TestAssert.True(runtime.Session.MarkReady(2), context + " chaos ready");
        TestAssert.True(runtime.Session.Start(), context + " session start");

        var order = new DuelCaptureTransport();
        var chaos = new DuelCaptureTransport();
        foreach (var participant in runtime.Session.Participants)
        {
            if (participant.ConnectionId is not long connectionId || participant.Hero is not { } hero)
                throw new InvalidOperationException(context + " missing authenticated hero");
            // Spawn establishes the native known-set and the source's own
            // hero visibility.  Keep that exact state; the delivery pass may
            // reveal the opponent when the normal vision rules allow it.
            runtime.Delivery.AddObserver(new ReplicationObserver(
                (ulong)connectionId, participant.Team, hero.NetId,
                participant.Team == Team.Order ? order : chaos,
                ready: true, view: participant.View));
        }
        // Complete the observer bootstrap through the real delivery path,
        // then discard only those setup packets. Sync IDs remain consumed by
        // the production session just as they are in a live match.
        runtime.Delivery.Flush();
        order.Clear();
        chaos.Clear();
        return new ProductionDuelRuntime(runtime, order, chaos,
            new CorpusRuntime(runtime.World, runtime.Movement, new AnnieChecks.ReplayQueue(),
                runtime.Combat, runtime.Buffs, runtime.Spells,
                runtime.Clones, null,
                runtime.Tibbers, runtime.Pets, random));
    }

    private static void HydrateDuelInitialState(JsonElement testCase, MatchRuntime runtime)
    {
        var initial = InitialState(testCase);
        foreach (var raw in initial.GetProperty("entities").EnumerateArray())
        {
            var source = ReadEntity(raw);
            var target = runtime.World.Get(source.NetId)
                ?? throw new InvalidOperationException($"duel bootstrap missing entity 0x{source.NetId:X8}");
            CopyDuelEntityState(source, target);
        }

    }

    private static void HydrateDuelRequestState(JsonElement request, MatchRuntime runtime)
    {
        if (!request.TryGetProperty("beforeState", out var beforeState) ||
            beforeState.ValueKind != JsonValueKind.Object)
            return;
        foreach (var raw in beforeState.GetProperty("entities").EnumerateArray())
        {
            var source = ReadEntity(raw);
            var target = runtime.World.Get(source.NetId)
                ?? throw new InvalidOperationException(
                    $"request state missing entity 0x{source.NetId:X8}");
            CopyDuelEntityState(source, target);
        }
    }

    private static void CopyDuelEntityState(Entity source, Entity target)
    {
        target.Position = source.Position;
        target.Facing = source.Facing;
        target.Health = source.Health;
        target.MaxHealth = source.MaxHealth;
        target.Mana = source.Mana;
        target.MaxMana = source.MaxMana;
        target.Armor = source.Armor;
        target.SpellBlock = source.SpellBlock;
        target.AttackDamage = source.AttackDamage;
        target.AbilityPower = source.AbilityPower;
        target.Speed = source.Speed;
        target.Radius = source.Radius;
        target.AttackRange = source.AttackRange;
        target.AttackPeriod = source.AttackPeriod;
        target.AttackWindup = source.AttackWindup;
        target.AttackSpeedMod = source.AttackSpeedMod;
        target.MissileSpeed = source.MissileSpeed;
        target.AttackCooldown = source.AttackCooldown;
        target.Level = source.Level;
        target.SkillPoints = source.SkillPoints;
        target.Alive = source.Alive;
        target.Created = source.Created;
        target.Visible = source.Visible;
        target.OwnerNetId = source.OwnerNetId;
        target.ExpiresAt = source.ExpiresAt;
        target.Gold = source.Gold;
        target.Experience = source.Experience;
        target.CooldownMod = source.CooldownMod;
        target.HealthRegen = source.HealthRegen;
        target.ManaRegen = source.ManaRegen;
        target.GoldReward = source.GoldReward;
        target.GlobalGoldReward = source.GlobalGoldReward;
        target.ExperienceReward = source.ExperienceReward;
        target.AcquisitionRange = source.AcquisitionRange;
        target.LifeSteal = source.LifeSteal;
        target.SpellVamp = source.SpellVamp;
        target.CritChance = source.CritChance;
        target.CritMultiplier = source.CritMultiplier;
        target.ArmorPenetrationFlat = source.ArmorPenetrationFlat;
        target.ArmorPenetrationPercent = source.ArmorPenetrationPercent;
        target.MagicPenetrationFlat = source.MagicPenetrationFlat;
        target.MagicPenetrationPercent = source.MagicPenetrationPercent;
        target.Dodge = source.Dodge;
        target.Hardness = source.Hardness;
        target.TeleportId = source.TeleportId;
        target.IsWard = source.IsWard;
        target.IsSummonerBeacon = source.IsSummonerBeacon;
        target.Protected = source.Protected;
        target.GodMode = source.GodMode;
        target.CooldownsDisabled = source.CooldownsDisabled;
        target.ChampionName = source.ChampionName;
        target.Name = source.Name;
        target.DeathTime = source.DeathTime;
        target.RespawnAt = source.RespawnAt;
        target.ForcedMoveGrace = source.ForcedMoveGrace;
        target.LastAnchorTime = source.LastAnchorTime;
        target.Waypoints = source.Waypoints.ToList();
        target.AnchorPath = source.AnchorPath.ToList();
        target.Target = source.Target;
        target.AttackTarget = source.AttackTarget;
        target.ForcedMove = source.ForcedMove;
        target.SpellRanks = source.SpellRanks.ToList();
        target.ActiveSpellNames = source.ActiveSpellNames.ToList();
        target.SpellCooldowns = source.SpellCooldowns.ToList();
        target.SummonerCooldowns = source.SummonerCooldowns.ToList();
        target.ItemCooldowns = source.ItemCooldowns.ToList();
        target.Buffs.Clear();
        foreach (var buff in source.Buffs)
            target.Buffs.Add(buff);
    }


    /// <summary>
    /// Builds the same callback graph as a live match, with only its packet
    /// delivery replaced by the deterministic replay queue.  Corpus checks
    /// must exercise the real combat, item, buff, champion and neutral hooks;
    /// the older fixture adapters remain reserved for packet-focused tests.
    /// </summary>
    private static CorpusRuntime CreateProductionCorpusRuntime(JsonElement testCase,
        string champion, params string[] otherChampions)
    {
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        foreach (var raw in InitialState(testCase).GetProperty("entities").EnumerateArray())
            world.Register(ReadEntity(raw));

        var grid = NavigationGrid.LoadDefault();
        var movement = new MovementSystem(world, grid);
        var queue = new AnnieChecks.ReplayQueue();
        var syncIds = new ReplicationSyncCounter();
        var data = GameData.Default;
        var caseName = testCase.TryGetProperty("name", out var caseNameValue) &&
            caseNameValue.ValueKind == JsonValueKind.String
                ? caseNameValue.GetString() ?? "case" : "case";
        var random = new RecordingRandomSource(
            testCase.TryGetProperty("randomCalls", out var randomCalls) ? randomCalls : default,
            champion + "/" + caseName, testCase);
        var itemVision = (ItemVisionService?)null;
        var hooks = new MatchGameplayHooks(world, data,
            (team, point) => itemVision?.RevealsStealthAt(team, point) ?? false);
        var catalog = new ItemCatalog(data.Paths);
        var stats = new HeroStatsService(data, catalog, hooks,
            new ProtocolHeroStatsTransport(queue, syncIds));
        var transport = new ProtocolCombatTransport(queue, stats,
            unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
            syncIds);
        var combat = new CombatService(world, hooks, transport, random, stats);
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var effects = new ProtocolSpellEffectSink(queue, syncIds, bounds,
            (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate,
            unit => PushCorpusStats(unit, stats, queue, syncIds));
        var buffEffects = new BuffEffectService(effects,
            new ProtocolBuffVisionSink(world.Get, world.Allocate, queue),
            _ => world.Clock.SyncId());
        hooks.AttachBuffEffectHooks(buffEffects);
        var buffs = new BuffLifecycle(world, stats, hooks.BuffServices);
        // The production adapters, so a script champion meets the members its natives call (script payload replacement, continuing stacks).
        var spellCombat = new CombatSpellAdapter(combat);
        var spellBuffs = new BuffLifecycleAdapter(buffs);
        var neutralSink = new ProtocolNeutralPacketSink(packet => queue.Enqueue(packet), syncIds,
            world.Allocate, bounds, (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(world, data, neutralSink);
        var neutral = new NeutralAiService(world, scheduler, movement, combat,
            neutralSink, effects, spellBuffs, data, random);
        var itemTransport = new ProtocolItemRuntimeTransport(queue,
            unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
            grid, syncIds);
        var items = new ItemService(world, catalog, buffs, buffEffects, spellCombat,
            effects, itemTransport, grid);
        itemVision = new ItemVisionService(world);
        var passives = new ItemPassiveService(world, data, buffs, buffEffects,
            spellCombat, effects, stats, random);
        var damage = new ItemDamageService(world, buffs, buffEffects, passives,
            spellCombat, effects);

        var clones = new SharedCloneService(world, movement, combat, effects, data);
        var boxes = new ShacoBoxService(world, movement, combat, effects, random, grid, data);
        var tibbers = new TibbersAiSystem(world, movement, combat, effects);
        // Batch 146: every pet runs its shipped AI script on one host. The corpora drive no
        // vision service here (the retired machines got none either), so a target is visible to a
        // pet unless the production canSee callback says otherwise.
        var pets = new PetAiService(world, movement, combat, effects, data);
        buffs.Pets = pets.Host;
        // Recipient callbacks retain their legacy order; hero ticks follow world order. Phase 3 (19 October 2026): every champion the corpora
        // replay (Annie, Shaco) runs from its shipped scripts, so the dispatcher holds the script module of the champion under replay.
        var modules = new[] { champion }.Concat(otherChampions).Select(name => new BbChampionModule(name, data)).ToArray();
        var dispatcher = new ChampionSpellDispatcher(modules);
        var spells = new SpellSystem(world, movement, spellCombat, effects,
            spellBuffs, hooks, dispatcher, data, random);
        spells.ScriptPets = new BbPetServices(clones, boxes);
        hooks.Attach(stats, combat, buffs, buffEffects, effects, damage, passives,
            neutral, scheduler,
            spells);
        // Batch 173: the shipped seams MatchRuntime.CreateProduction installs and this rig did not — the camp and lane
        // units' own character scripts, and a champion's Hero.luaobj (its order engine plus BuffLifecycle.Heroes, which is
        // what raises the script's taunt and fear callbacks). Without them a recorded world lacked the scripts production
        // runs (the Shaco corpus's feared hero never walked, and a camp's x1.43 and level growth never applied).
        var (unitScripts, unitHooks) = MatchScriptSeams.InstallUnitCharacters(spells, data);
        hooks.UnitHooks = unitHooks;
        MatchScriptSeams.BindUnitCharacters(scheduler, unitScripts);
        // The corpus world has no map turrets or waves; the units it does have are bound below, over the restored state.
        foreach (var unit in world.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral).ToArray())
            MatchScriptSeams.BindUnitCharacter(unitScripts, unit, unit.Name);
        var heroOrders = MatchScriptSeams.AttachHeroOrders(spells, buffs, world, movement, combat, effects,
            (hero, other) => hero.Team == other.Team || other.Visible, spellBuffs, data, random);
        // The first hero of a script-driven champion binds its module to the match and every hero of it is activated (SetVarsByLevel,
        // CharOnActivate), as ProductionMatchComposition does when a hero is created.
        var scriptHooks = new BbHookFanout();
        foreach (var scripted in modules) scriptHooks.Add(scripted.Bind(spells));
        hooks.ScriptHooks = scriptHooks;
        foreach (var hero in world.Heroes().ToArray())
        {
            if (dispatcher.FindModule(hero.ChampionName) is BbChampionModule scripted) scripted.ActivateHero(hero);
            // Batch 159 part 3, production's session.HeroCreated: a hero whose champion has no character-script module
            // (and CharScriptOrianna, which omits the block) gets the team-size catch-up through the unit-character
            // module, since that is the only host a hand-written or script-driven champion shares. The Seams' bind of a
            // camp or lane character already puts it on a scripted champion whose own CharOnActivate adds it.
            if (!hero.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.ControllerBuff))
                unitScripts.AddShippedBuff(hero, TeamSizeCatchUp.ControllerBuff, BuffType.Internal,
                    TeamSizeCatchUp.ControllerSeconds);
        }
        return new CorpusRuntime(world, movement, queue, combat, buffs, spells,
            clones, boxes, tibbers, pets, random)
            {
                ItemPassives = passives,
                Modules = modules,
                HeroOrders = heroOrders,
            };
    }

    private static void TickCorpus(CorpusRuntime runtime, double dt)
    {
        runtime.World.Clock.Advance(dt);
        // Batch 173: the champions' Hero.luaobj step, before movement — the order ProductionSimulationDriver.Tick runs
        // (its TimerFeared is what walks a feared hero; without the host the hero stood still).
        runtime.HeroOrders?.Tick(dt);
        runtime.MovementAccumulator += dt;
        while (runtime.MovementAccumulator + 1e-12 >= MovementSystem.FixedTickSeconds)
        {
            runtime.Movement.Tick(MovementSystem.FixedTickSeconds);
            runtime.MovementAccumulator -= MovementSystem.FixedTickSeconds;
        }
        runtime.Combat.CombatTick(dt, regenerate: false);
        var now = runtime.World.Clock.Now();
        // Batch 146: every pet runs its shipped AI on one host, stepped once (the three retired
        // pet machines were ticked once each here).
        runtime.Pets.Tick(now, dt);
        if (runtime.Boxes is not null) runtime.Boxes.Tick(now, dt);
        runtime.Spells.Tick(dt);
        // Batch 149 step 5: the aura loop an item still needs is its own script's UpdateSelfBuffStats; the hand-written
        // ItemPassiveService.SyncAuras is gone.
        foreach (var entity in runtime.World.Entities.Values.ToArray())
            runtime.Lifecycle.TickBuffs(entity, dt);
        runtime.Spells.TickLateChampions(dt);
    }

    private static void ApplyCorpusActions(JsonElement actions, CorpusRuntime runtime, string champion)
    {
        if (actions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        var values = actions.ValueKind == JsonValueKind.Array
            ? actions.EnumerateArray().ToArray() : [actions];
        foreach (var action in values)
        {
            var kind = action.GetProperty("kind").GetString();
            if (kind == "cast")
            {
                var caster = runtime.World.Entities.Values.FirstOrDefault(entity =>
                    entity.UnitType == UnitType.Hero &&
                    string.Equals(entity.ChampionName, champion, StringComparison.OrdinalIgnoreCase));
                if (caster is null) throw new InvalidOperationException(
                    $"corpus cast has no {champion} hero");
                var request = EncodeRequest(action.GetProperty("request"));
                var accepted = runtime.Spells.HandleCastRequest(caster, request,
                    runtime.Spells.CurrentTime);
                if (action.TryGetProperty("validation", out var validation))
                    TestAssert.Equal(validation[0].GetBoolean(), accepted,
                        $"{champion} action cast accepted");
                continue;
            }
            var unit = runtime.World.Get(action.GetProperty("unit").GetUInt32())
                ?? throw new InvalidOperationException("corpus action references an unknown entity");
            switch (kind)
            {
                case "set-dead":
                    unit.Alive = false;
                    unit.Health = 0.0;
                    break;
                case "move":
                    var destination = new Position2(action.GetProperty("position")[0].GetDouble(),
                        action.GetProperty("position")[1].GetDouble());
                    // The exporter records a client move order, rather than
                    // teleporting the body. The route is one point and the
                    // target is retained for the next fixed movement update.
                    unit.Waypoints = [destination];
                    unit.Target = destination;
                    break;
                case "attack-target":
                    unit.AttackTarget = action.GetProperty("target").GetUInt32();
                    break;
                case "kill":
                    runtime.Combat.Kill(unit, action.GetProperty("killer").GetUInt32());
                    break;
                default:
                    throw new InvalidDataException($"unknown corpus action {kind}");
            }
        }
    }

    private static void CompareCompactState(JsonElement snapshot, CorpusRuntime runtime,
        string context)
    {
        if (FixtureRecorder.Enabled)
        {
            SpellReplayRecording.Fields(snapshot, new JsonObject
            {
                ["entities"] = SpellReplayRecording.Entities(snapshot.GetProperty("entities"), runtime.World),
                ["pending"] = SpellReplayRecording.Pending(runtime.Spells, compact: true),
                ["markers"] = SpellReplayRecording.Markers(runtime.Spells),
            });
            return;
        }
        var expectedEntities = snapshot.GetProperty("entities");
        TestAssert.Equal(expectedEntities.GetArrayLength(), runtime.World.Entities.Count,
            context + " entity count");
        foreach (var raw in expectedEntities.EnumerateArray())
        {
            var id = raw.GetProperty("netid").GetUInt32();
            var entity = runtime.World.Get(id) ?? throw new InvalidOperationException(
                context + " missing entity " + id);
            CompareCompactEntity(raw, entity, context + " entity " + id);
            CompareCorpusBuffs(raw.GetProperty("buffs"), entity.Buffs,
                context + " entity " + id + " buffs");
        }
        CompareSharedPending(snapshot.GetProperty("pending"), runtime.Spells, context);
    }

    private static void CompareCompactEntity(JsonElement raw, Entity entity, string context)
    {
        Near(raw.GetProperty("position")[0].GetDouble(), entity.Position.X, context + " X");
        Near(raw.GetProperty("position")[1].GetDouble(), entity.Position.Z, context + " Z");
        Near(raw.GetProperty("facing")[0].GetDouble(), entity.Facing.X, context + " facing X");
        Near(raw.GetProperty("facing")[1].GetDouble(), entity.Facing.Z, context + " facing Z");
        foreach (var (name, value) in new (string, double)[]
        {
            ("hp", entity.Health), ("max_hp", entity.MaxHealth), ("mana", entity.Mana),
            ("max_mana", entity.MaxMana), ("armor", entity.Armor),
            ("spell_block", entity.SpellBlock), ("atk_damage", entity.AttackDamage),
            ("ability_power", entity.AbilityPower), ("speed", entity.Speed),
            ("radius", entity.Radius), ("atk_range", entity.AttackRange),
            ("atk_period", entity.AttackPeriod), ("windup", entity.AttackWindup),
            ("attack_speed_mod", entity.AttackSpeedMod), ("missile_speed", entity.MissileSpeed),
            ("attack_cd", entity.AttackCooldown), ("level", entity.Level),
            ("skill_points", entity.SkillPoints), ("expires_at", entity.ExpiresAt),
            ("gold", entity.Gold), ("exp", entity.Experience),
            ("respawn_at", entity.RespawnAt), ("cooldown_mod", entity.CooldownMod),
            ("hp_regen", entity.HealthRegen), ("mp_regen", entity.ManaRegen),
            ("gold_reward", entity.GoldReward), ("global_gold_reward", entity.GlobalGoldReward),
            ("xp_reward", entity.ExperienceReward),
        })
        {
            Near(SpellReplayRecording.Number(raw.GetProperty(name)), value, context + " " + name);
        }
        CompareList(raw.GetProperty("spell_ranks"), entity.SpellRanks.Select(x => (double)x).ToArray(), context + " ranks");
        CompareList(raw.GetProperty("spell_cooldowns"), entity.SpellCooldowns, context + " cooldowns");
        CompareList(raw.GetProperty("summoner_cooldowns"), entity.SummonerCooldowns, context + " summoner cooldowns");
        CompareList(raw.GetProperty("item_cooldowns"), entity.ItemCooldowns, context + " item cooldowns");
        ComparePoints(raw.GetProperty("waypoints"), entity.Waypoints, context + " waypoints");
        CompareNullablePoint(raw.GetProperty("target"), entity.Target, context + " target");
        TestAssert.Equal(raw.GetProperty("alive").GetBoolean(), entity.Alive, context + " alive");
        TestAssert.Equal(raw.GetProperty("created").GetBoolean(), entity.Created, context + " created");
        TestAssert.Equal(raw.GetProperty("visible").GetBoolean(), entity.Visible, context + " visible");
        TestAssert.Equal(raw.GetProperty("owner_netid").GetUInt32(), entity.OwnerNetId, context + " owner");
        var attackTarget = raw.GetProperty("attack_target");
        if (attackTarget.ValueKind == JsonValueKind.Null)
            TestAssert.True(entity.AttackTarget is null || entity.AttackTarget == 0,
                context + $" attack target expected null/0 actual={entity.AttackTarget?.ToString() ?? "null"}");
        else
            TestAssert.Equal(attackTarget.GetUInt32(), entity.AttackTarget, context + " attack target");
        TestAssert.Equal((int)entity.UnitType, raw.GetProperty("unitType").GetInt32(), context + " unit type");
        TestAssert.Equal((int)entity.Team, raw.GetProperty("team").GetInt32(), context + " team");
        TestAssert.Equal(raw.GetProperty("champion").GetString(), entity.ChampionName, context + " champion");
    }

    private static void CompareSharedPending(JsonElement expected, SpellSystem spells,
        string context)
    {
        var actual = spells.SharedProjectiles.Select(projectile => projectile.Snapshot).ToArray();
        TestAssert.Equal(expected.GetArrayLength(), actual.Length, context + " pending projectile count");
        for (var index = 0; index < actual.Length; index++)
        {
            var row = expected[index];
            var item = actual[index];
            var label = context + " pending projectile " + index;
            var metadata = row.GetProperty("metadata");
            TestAssert.Equal(metadata.GetProperty("spell").GetString(), item.SpellName, label + " spell");
            TestAssert.Equal(row.GetProperty("caster").GetUInt32(), item.CasterNetId, label + " caster");
            TestAssert.Equal(row.GetProperty("target").GetUInt32(), item.TargetNetId, label + " target");
            TestAssert.Equal(row.GetProperty("missile").GetUInt32(), item.MissileNetId, label + " missile");
            Near(row.GetProperty("remaining").GetDouble(), item.CastRemaining, label + " remaining");
            Near(row.GetProperty("damage").GetDouble(), item.Damage, label + " damage");
            Near(row.GetProperty("manaCost").GetDouble(), item.ManaCost, label + " mana");
            TestAssert.Equal(row.GetProperty("slot").GetInt32(), item.Slot, label + " slot");
            TestAssert.Equal(row.GetProperty("pyromania").GetBoolean(), item.PyromaniaArmed,
                label + " pyromania");
            if (metadata.TryGetProperty("rank", out var rank))
                TestAssert.Equal(rank.GetInt32(), item.Rank, label + " rank");
            if (metadata.TryGetProperty("cast_remaining", out var castRemaining))
                Near(castRemaining.GetDouble(), item.CastWindupRemaining, label + " cast remaining");
            if (metadata.TryGetProperty("pos", out var position))
            {
                if (position.ValueKind == JsonValueKind.Array)
                {
                    Near(position[0].GetDouble(), item.Position.X, label + " X");
                    Near(position[1].GetDouble(), item.Position.Z, label + " Z");
                }
                else if (position.ValueKind == JsonValueKind.String)
                {
                    var parts = position.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Near(double.Parse(parts[0], CultureInfo.InvariantCulture), item.Position.X, label + " X");
                    Near(double.Parse(parts[1], CultureInfo.InvariantCulture), item.Position.Z, label + " Z");
                }
            }
            if (metadata.TryGetProperty("origin", out var origin))
            {
                Near(origin[0].GetDouble(), item.Origin.X, label + " origin X");
                Near(origin[1].GetDouble(), item.Origin.Z, label + " origin Z");
            }
            if (metadata.TryGetProperty("direction", out var direction))
            {
                Near(direction[0].GetDouble(), item.Direction.X, label + " direction X");
                Near(direction[1].GetDouble(), item.Direction.Z, label + " direction Z");
            }
            if (metadata.TryGetProperty("speed", out var speed))
                Near(speed.GetDouble(), item.Speed, label + " speed");
            if (metadata.TryGetProperty("cast_info", out var castInfo))
            {
                TestAssert.True(item.CastInfo is not null,
                    label + " cast info missing");
                CompareCastInfo(castInfo, item.CastInfo!, label + " cast info");
            }
        }
    }

    private static void CompareCorpusState(JsonElement snapshot, CorpusRuntime runtime,
        string champion, string context)
    {
        if (FixtureRecorder.Enabled)
        {
            // Batch 146: one host keeps every pet's route cache (the retired machines kept one each).
            var destinations = new Dictionary<uint, Position2>();
            foreach (var pair in runtime.Pets.LastDestinations) destinations[pair.Key] = pair.Value;
            SpellReplayRecording.Fields(snapshot, new JsonObject
            {
                ["entities"] = SpellReplayRecording.Entities(snapshot.GetProperty("entities"), runtime.World),
                ["pendingHits"] = SpellReplayRecording.Hits(runtime.Combat.PendingHits),
                ["pendingCasts"] = SpellReplayRecording.Pending(runtime.Spells, compact: false),
                ["petLastDest"] = SpellReplayRecording.Map(destinations),
                ["petNextAura"] = SpellReplayRecording.Map(new Dictionary<uint, double>()),
            });
            return;
        }
        var world = runtime.World;
        var expectedEntities = snapshot.GetProperty("entities");
        TestAssert.Equal(expectedEntities.GetArrayLength(), world.Entities.Count,
            context + " entity count");
        foreach (var raw in expectedEntities.EnumerateArray())
        {
            var entity = world.Get(raw.GetProperty("netid").GetUInt32())
                ?? throw new InvalidOperationException($"{context}: missing entity");
            CompareCorpusEntity(raw, entity, world, runtime.Movement, champion, context);
            CompareCorpusBuffs(raw.GetProperty("buffs"), entity.Buffs, context + " buffs");
        }
        ComparePendingHits(snapshot.GetProperty("pendingHits"), runtime.Combat.PendingHits, context);
        ComparePendingCasts(snapshot.GetProperty("pendingCasts"), runtime.Spells, context);
        ComparePetMaps(snapshot, runtime, context);
        var hero = world.Entities.Values.First(entity =>
            string.Equals(entity.ChampionName, champion, StringComparison.OrdinalIgnoreCase));
        CompareChampionState(snapshot, hero, champion, context);
    }

    private static void CompareCorpusEntity(JsonElement raw, Entity entity,
        WorldState world, MovementSystem movement, string champion, string context)
    {
        var id = raw.GetProperty("netid").GetUInt32();
        var label = context + $" entity {id}";
        Near(raw.GetProperty("pos")[0].GetDouble(), entity.Position.X,
            label + " position X");
        Near(raw.GetProperty("pos")[1].GetDouble(), entity.Position.Z,
            label + " position Z");
        var facing = raw.GetProperty("facing");
        Near(facing[0].GetDouble(), entity.Facing.X, label + " facing X");
        Near(facing[1].GetDouble(), entity.Facing.Z, label + " facing Z");
        Near(raw.GetProperty("hp").GetDouble(),
            entity.Health, label + " hp (native zero floor)");
        CompareNumber(raw, "max_hp", entity.MaxHealth, label);
        CompareNumber(raw, "mana", entity.Mana, label);
        CompareNumber(raw, "max_mana", entity.MaxMana, label);
        CompareNumber(raw, "armor", entity.Armor, label);
        CompareNumber(raw, "spell_block", entity.SpellBlock, label);
        CompareNumber(raw, "atk_damage", entity.AttackDamage, label);
        CompareNumber(raw, "ability_power", entity.AbilityPower, label);
        TestAssert.Near(raw.GetProperty("speed").GetDouble(), entity.Speed, 1e-6, label + " speed");
        CompareNumber(raw, "radius", entity.Radius, label);
        CompareNumber(raw, "atk_range", entity.AttackRange, label);
        Near(raw.GetProperty("atk_period").GetDouble(), entity.AttackPeriod, label + " attack period");
        Near(raw.GetProperty("windup").GetDouble(), entity.AttackWindup, label + " windup");
        Near(raw.GetProperty("attack_speed_mod").GetDouble(), entity.AttackSpeedMod, label + " attack speed");
        CompareNumber(raw, "missile_speed", entity.MissileSpeed, label);
        Near(raw.GetProperty("attack_cd").GetDouble(), entity.AttackCooldown, label + " attack cooldown");
        CompareNumber(raw, "level", entity.Level, label);
        CompareNumber(raw, "skill_points", entity.SkillPoints, label);
        CompareNumber(raw, "expires_at", entity.ExpiresAt, label);
        CompareNumber(raw, "gold", entity.Gold, label);
        CompareNumber(raw, "exp", entity.Experience, label);
        CompareNumber(raw, "respawn_at", entity.RespawnAt, label);
        CompareNumber(raw, "cooldown_mod", entity.CooldownMod, label);
        CompareNumber(raw, "hp_regen", entity.HealthRegen, label);
        CompareNumber(raw, "mp_regen", entity.ManaRegen, label);
        CompareNumber(raw, "forced_move_grace", entity.ForcedMoveGrace, label);
        // Batch 149 step 4: `odyn_stored_damage` is a retired field (Entity.OdynStoredDamage is gone; the shipped
        // Spells/OdynsVeil.luaobj keeps the store in its own CharVars.StoredDamage), so the archived column is
        // superseded data here and is no longer asserted.
        Near(raw.GetProperty("gold_reward").GetDouble(), entity.GoldReward, label + " gold_reward");
        CompareNumber(raw, "global_gold_reward", entity.GlobalGoldReward, label);
        CompareNumber(raw, "xp_reward", entity.ExperienceReward, label);
        CompareNumber(raw, "acquisition_range", entity.AcquisitionRange, label);
        CompareNumber(raw, "life_steal", entity.LifeSteal, label);
        CompareNumber(raw, "spell_vamp", entity.SpellVamp, label);
        CompareNumber(raw, "crit_chance", entity.CritChance, label);
        CompareNumber(raw, "crit_multiplier", entity.CritMultiplier, label);
        CompareNumber(raw, "armor_pen_flat", entity.ArmorPenetrationFlat, label);
        CompareNumber(raw, "armor_pen_percent", entity.ArmorPenetrationPercent, label);
        CompareNumber(raw, "magic_pen_flat", entity.MagicPenetrationFlat, label);
        CompareNumber(raw, "magic_pen_percent", entity.MagicPenetrationPercent, label);
        CompareNumber(raw, "dodge", entity.Dodge, label);
        CompareNumber(raw, "hardness", entity.Hardness, label);
        TestAssert.Equal(raw.GetProperty("alive").GetBoolean(), entity.Alive, label + " alive");
        TestAssert.Equal(raw.GetProperty("created").GetBoolean(), entity.Created, label + " created");
        TestAssert.Equal(raw.GetProperty("visible").GetBoolean(), entity.Visible, label + " visible");
        TestAssert.Equal(raw.GetProperty("teleport_id").GetByte(), entity.TeleportId, label + " teleport id");
        TestAssert.Equal(raw.GetProperty("owner_netid").GetUInt32(), entity.OwnerNetId, label + " owner");
        CompareList(raw.GetProperty("spell_ranks"), entity.SpellRanks.Select(value => (double)value).ToArray(), label + " ranks");
        CompareList(raw.GetProperty("spell_cooldowns"), entity.SpellCooldowns, label + " cooldowns");
        CompareList(raw.GetProperty("summoner_cooldowns"), entity.SummonerCooldowns, label + " summoner cooldowns");
        CompareList(raw.GetProperty("item_cooldowns"), entity.ItemCooldowns, label + " item cooldowns");
        ComparePoints(raw.GetProperty("waypoints"), entity.Waypoints, label + " waypoints");
        CompareNullablePoint(raw.GetProperty("target"), entity.Target, label + " target");
        ComparePoints(raw.GetProperty("anchor_path"), entity.AnchorPath, label + " anchor path");
        CompareNumber(raw, "last_anchor_time", entity.LastAnchorTime, label);
        CompareNullableNumber(raw.GetProperty("death_time"), entity.DeathTime, label + " death time");
        CompareForcedMove(raw.GetProperty("forced_move"), entity.ForcedMove, label);
    }

    private static void CompareNumber(JsonElement raw, string name, double actual, string context) =>
        Near(SpellReplayRecording.Number(raw.GetProperty(name)), actual, context + " " + name);

    private static void CompareNullableNumber(JsonElement expected, double? actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual is null, context + " expected null");
        else
            Near(expected.GetDouble(), actual ?? double.NaN, context);
    }

    private static void CompareNullablePoint(JsonElement expected, Position2? actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual is null, context + " expected null");
        else
        {
            TestAssert.True(actual is not null, context + " missing");
            Near(expected[0].GetDouble(), actual!.Value.X, context + " X");
            Near(expected[1].GetDouble(), actual.Value.Z, context + " Z");
        }
    }

    private static void CompareForcedMove(JsonElement expected, ForcedMoveState? actual, string context)
    {
        if (expected.ValueKind != JsonValueKind.Object || !expected.EnumerateObject().Any())
        {
            TestAssert.True(actual is null, context + " forced move expected null");
            return;
        }
        TestAssert.True(actual is not null, context + " forced move missing");
        Near(expected.GetProperty("destination")[0].GetDouble(), actual!.Destination.X, context + " forced destination X");
        Near(expected.GetProperty("destination")[1].GetDouble(), actual.Destination.Z, context + " forced destination Z");
        Near(expected.GetProperty("speed").GetDouble(), actual.Speed, context + " forced speed");
        Near(expected.GetProperty("remaining").GetDouble(), actual.Remaining, context + " forced remaining");
        TestAssert.Equal(expected.GetProperty("source_netid").GetUInt32(), actual.SourceNetId, context + " forced source");
        TestAssert.Equal(expected.GetProperty("effect").GetString(), actual.Effect, context + " forced effect");
        TestAssert.Equal(expected.GetProperty("first_collision").GetBoolean(), actual.FirstCollision, context + " forced collision");
        TestAssert.Equal(expected.GetProperty("completed").GetBoolean(), actual.Completed, context + " forced completed");
    }

    private static void ComparePendingHits(JsonElement expected, IReadOnlyList<PendingHit> actual, string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, context + " pending hits count");
        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            var row = expected[index];
            var hit = actual[index];
            Near(row[0].GetDouble(), hit.WindupRemaining, context + $" pending hit {index} windup");
            Near(row[1].GetDouble(), hit.ProjectilePosition.X, context + $" pending hit {index} X");
            Near(row[2].GetDouble(), hit.ProjectilePosition.Z, context + $" pending hit {index} Z");
            TestAssert.Equal(row[3].GetUInt32(), hit.SourceNetId, context + $" pending hit {index} source");
            TestAssert.Equal(row[4].GetUInt32(), hit.TargetNetId, context + $" pending hit {index} target");
            Near(row[5].GetDouble(), hit.Damage, context + $" pending hit {index} damage");
            Near(row[6].GetDouble(), hit.MissileSpeed, context + $" pending hit {index} speed");
            TestAssert.Equal(DamageTypeExtensions.ParseSourceKind(row[7].GetString()), hit.SourceKind,
                context + $" pending hit {index} source kind");
            if (row.GetArrayLength() > 8)
            {
                var expectedMissile = row[8].GetUInt32();
                TestAssert.Equal(expectedMissile, hit.MissileNetId,
                    context + $" pending hit {index} missile");
            }
        }
    }

    private static void ComparePendingCasts(JsonElement expected, SpellSystem spells,
        string context)
    {
        var actual = spells.SharedProjectiles.Select(projectile => projectile.Snapshot).ToArray();
        if (expected.GetArrayLength() != actual.Length)
        {
            var actualSummary = string.Join(",", actual.Select(item =>
                $"{item.SpellName}:{item.CasterNetId:X8}->{item.TargetNetId:X8}:{item.CastRemaining:R}"));
            throw new InvalidOperationException($"{context} pending casts count expected={expected.GetArrayLength()} actual={actual.Length} actual={actualSummary}");
        }
        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            var row = expected[index];
            var projectile = actual[index];
            var label = context + $" pending cast {index}";
            Near(row[0].GetDouble(), projectile.CastRemaining, label + " remaining");
            TestAssert.Equal(row[1].GetUInt32(), projectile.CasterNetId, label + " caster");
            TestAssert.Equal(row[2].GetUInt32(), projectile.TargetNetId, label + " target");
            Near(row[3].GetDouble(), projectile.Damage, label + " damage");
            Near(row[4].GetDouble(), projectile.ManaCost, label + " mana");
            TestAssert.Equal(row[5].GetInt32(), projectile.Slot, label + " slot");
            TestAssert.Equal(row[6].GetBoolean(), projectile.PyromaniaArmed, label + " pyromania");
            TestAssert.Equal(row[7].GetUInt32(), projectile.MissileNetId, label + " missile");
            var metadata = row.GetArrayLength() > 8 ? row[8] : default;
            if (metadata.ValueKind == JsonValueKind.Object)
            {
                TestAssert.Equal(metadata.GetProperty("spell").GetString(),
                    projectile.SpellName, label + " spell");
                if (metadata.TryGetProperty("rank", out var rank))
                    TestAssert.Equal(rank.GetInt32(), projectile.Rank, label + " rank");
                if (metadata.TryGetProperty("launched", out var launched))
                    TestAssert.Equal(launched.GetBoolean(), projectile.Launched,
                        label + " launched");
                if (metadata.TryGetProperty("speed", out var speed))
                    Near(speed.GetDouble(), projectile.Speed, label + " speed");
                if (metadata.TryGetProperty("pos", out var pos))
                {
                    Near(pos[0].GetDouble(), projectile.Position.X, label + " position X");
                    Near(pos[1].GetDouble(), projectile.Position.Z, label + " position Z");
                }
                if (metadata.TryGetProperty("cast_info", out var castInfo))
                {
                    TestAssert.True(projectile.CastInfo is not null,
                        label + " cast info missing");
                    CompareCastInfo(castInfo, projectile.CastInfo!, label + " cast info");
                }
            }
        }
    }

    private static void ComparePetMaps(JsonElement snapshot, CorpusRuntime runtime,
        string context)
    {
        var expectedDestinations = snapshot.GetProperty("petLastDest");
        // Batch 146: one host keeps every pet's route cache (the retired machines kept one each).
        var actualDestinations = new Dictionary<uint, Position2>();
        foreach (var pair in runtime.Pets.LastDestinations)
            actualDestinations[pair.Key] = pair.Value;
        TestAssert.Equal(expectedDestinations.EnumerateObject().Count(),
            actualDestinations.Count,
            context + $" pet destination count expectedIds=[{string.Join(',', expectedDestinations.EnumerateObject().Select(p => p.Name))}] " +
            $"actualIds=[{string.Join(',', actualDestinations.Keys)}]");
        foreach (var property in expectedDestinations.EnumerateObject())
        {
            var id = uint.Parse(property.Name, CultureInfo.InvariantCulture);
            if (!actualDestinations.TryGetValue(id, out var point))
                throw new InvalidOperationException(context + " missing pet destination " + property.Name);
            Near(property.Value[0].GetDouble(), point.X,
                context + " pet destination X " + property.Name);
            Near(property.Value[1].GetDouble(), point.Z,
                context + " pet destination Z " + property.Name);
        }
        var expectedAuras = snapshot.GetProperty("petNextAura");
        // 19 October 2026: Tibbers' aura is the InfernalGuardianBurning script buff, no longer a pulse clock of TibbersAiSystem.
        var actualAuras = (IReadOnlyDictionary<uint, double>)new Dictionary<uint, double>();
        TestAssert.Equal(expectedAuras.EnumerateObject().Count(), actualAuras.Count,
            context + " pet aura count");
        foreach (var property in expectedAuras.EnumerateObject())
        {
            var id = uint.Parse(property.Name, CultureInfo.InvariantCulture);
            if (!actualAuras.TryGetValue(id, out var at))
                throw new InvalidOperationException(context + " missing pet aura " + property.Name);
            Near(property.Value.GetDouble(), at, context + " pet aura " + property.Name);
        }
    }

    private static void CompareChampionState(JsonElement snapshot, Entity hero,
        string champion, string context)
    {
        var actor = snapshot.GetProperty("entities").EnumerateArray()
            .First(raw => raw.GetProperty("netid").GetUInt32() == hero.NetId);
        var expectedShaco = actor.GetProperty("shaco_state");
        if (champion == "Shaco" && expectedShaco.ValueKind == JsonValueKind.Null)
            TestAssert.True(hero.ChampionState is null, context + " unexpected Shaco state");
        else if (champion == "Shaco")
        {
            // Shaco runs from his shipped scripts: no hand-written cast state exists, whatever the corpus recorded.
            TestAssert.True(hero.ChampionState is null, context + " Shaco has no hand-written state");
        }
    }

    private static void CompareCastInfo(JsonElement expected, SpellCastInfo actual, string context)
    {
        var reviewed = Convert.FromHexString(expected.GetProperty("bytes").GetString()!);
        var expectedBytes = Convert.ToHexString(reviewed).ToLowerInvariant();
        var actualBytes = Convert.ToHexString(SpellPackets.BuildCastInfo(actual)).ToLowerInvariant();
        TestAssert.Equal(expectedBytes, actualBytes, context + " bytes");
    }

    private static void CompareIdSet(JsonElement expected, IEnumerable<uint> actual, string context)
    {
        var ids = actual.OrderBy(value => value).ToArray();
        var expectedIds = expected.EnumerateArray().Select(value => value.GetUInt32()).OrderBy(value => value).ToArray();
        TestAssert.Equal(expectedIds.Length, ids.Length, context + " count");
        for (var index = 0; index < ids.Length; index++)
            TestAssert.Equal(expectedIds[index], ids[index], context + $"[{index}]");
    }

    private static void ComparePoint(JsonElement expected, Position2 actual, string context)
    {
        Near(expected[0].GetDouble(), actual.X, context + " X");
        Near(expected[1].GetDouble(), actual.Z, context + " Z");
    }

    private static void CompareCorpusBuffs(JsonElement expected, IList<BuffState> actual,
        string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, context + " count");
        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            var raw = expected[index];
            var buff = actual[index];
            var expectedName = raw.GetProperty("name").GetString();
            TestAssert.Equal(expectedName, buff.Name, context + $"[{index}] name");
            TestAssert.Equal(raw.GetProperty("type").GetInt32(), (int)buff.Type, context + $"[{index}] type");
            TestAssert.Equal(raw.GetProperty("slot").GetInt32(), buff.Slot, context + $"[{index}] slot");
            TestAssert.Equal(raw.GetProperty("caster").GetUInt32(), buff.CasterNetId, context + $"[{index}] caster");
            Near(raw.GetProperty("expires_at").GetDouble(), buff.ExpiresAt, context + $"[{index}] expiry");
            var expectedStacks = raw.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1;
            TestAssert.Equal(expectedStacks, buff.Stacks, context + $"[{index}] stacks");
            var expectedMaxStack = raw.GetProperty("max_stack");
            if (expectedMaxStack.ValueKind == JsonValueKind.Null)
                TestAssert.True(buff.MaxStack is null, context + $"[{index}] max stack expected null");
            else
                TestAssert.Equal(expectedMaxStack.GetInt32(), buff.MaxStack ?? -1,
                    context + $"[{index}] max stack");
            if (raw.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                ComparePayload(payload, buff.Payload, context + $"[{index}] payload", buff.Name);
        }
    }

    private static void ComparePayload(JsonElement expected, BuffPayload actual, string context,
        string buffName)
    {
        foreach (var field in expected.EnumerateObject())
        {
            var propertyName = field.Name switch
            {
                "caster_pos" => nameof(BuffPayload.CasterPosition),
                "deceive_pos" => nameof(BuffPayload.DeceivePosition),
                "fear_origin" => nameof(BuffPayload.FearOrigin),
                "pos" => nameof(BuffPayload.Position),
                "extra_fx_netid" => nameof(BuffPayload.ExtraFxNetId),
                "fx_netid" => nameof(BuffPayload.FxNetId),
                "silver_caster" => nameof(BuffPayload.SilverCasterNetId),
                _ => SnakeToPascal(field.Name),
            };
            var property = typeof(BuffPayload).GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"{context}: payload field {field.Name} has no typed C# property");
            var value = property.GetValue(actual);
            ComparePayloadValue(field.Value, value, property.PropertyType,
                context + " " + field.Name);
        }
    }

    private static void ComparePayloadValue(JsonElement expected, object? actual,
        Type propertyType, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            TestAssert.True(actual is null, context + " expected null");
            return;
        }
        if (actual is ForcedMoveState forcedMove)
        {
            if (expected.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{context}: forced move payload must be an object");
            var destination = expected.GetProperty("destination");
            Near(destination[0].GetDouble(), forcedMove.Destination.X, context + " destination X");
            Near(destination[1].GetDouble(), forcedMove.Destination.Z, context + " destination Z");
            Near(expected.GetProperty("speed").GetDouble(), forcedMove.Speed, context + " speed");
            Near(expected.GetProperty("remaining").GetDouble(), forcedMove.Remaining, context + " remaining");
            TestAssert.Equal(expected.GetProperty("source_netid").GetUInt32(), forcedMove.SourceNetId,
                context + " source_netid");
            TestAssert.Equal(expected.GetProperty("effect").GetString(), forcedMove.Effect,
                context + " effect");
            TestAssert.Equal(expected.GetProperty("first_collision").GetBoolean(), forcedMove.FirstCollision,
                context + " first_collision");
            TestAssert.Equal(expected.GetProperty("completed").GetBoolean(), forcedMove.Completed,
                context + " completed");
            return;
        }
        if (expected.ValueKind == JsonValueKind.Array &&
            propertyType != typeof(Position2?) && propertyType != typeof(Position2))
        {
            if (actual is not System.Collections.IEnumerable sequence)
                throw new InvalidOperationException($"{context}: expected sequence payload");
            var actualValues = sequence.Cast<object?>().ToArray();
            TestAssert.Equal(expected.GetArrayLength(), actualValues.Length, context + " count");
            for (var index = 0; index < actualValues.Length; index++)
                ComparePayloadValue(expected[index], actualValues[index],
                    actualValues[index]?.GetType() ?? typeof(object), context + $"[{index}]");
            return;
        }
        if (propertyType == typeof(Position2?) || propertyType == typeof(Position2))
        {
            TestAssert.True(actual is Position2, context + " position missing");
            var point = (Position2)actual!;
            Near(expected[0].GetDouble(), point.X, context + " X");
            Near(expected[expected.GetArrayLength() == 3 ? 2 : 1].GetDouble(), point.Z,
                context + " Z");
            return;
        }
        if (propertyType == typeof(bool))
        {
            var value = expected.ValueKind == JsonValueKind.Number
                ? Math.Abs(expected.GetDouble()) > 1e-12
                : expected.GetBoolean();
            TestAssert.Equal(value, actual is true, context);
            return;
        }
        if (propertyType == typeof(string))
        {
            TestAssert.Equal(expected.GetString(), actual as string, context);
            return;
        }
        if (expected.ValueKind == JsonValueKind.Number && actual is not null)
        {
            Near(expected.GetDouble(), Convert.ToDouble(actual, CultureInfo.InvariantCulture), context);
            return;
        }
        throw new InvalidOperationException($"{context}: unsupported payload value {propertyType.Name}");
    }

    private static string SnakeToPascal(string name) => string.Concat(
        name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static void CompareCorpusPackets(JsonElement expected,
        AnnieChecks.ReplayQueue queue, string context, JsonElement? expectedState = null,
        WorldState? world = null, string? softCapChampion = null,
        double? sourceAttackSpeed = null, MovementSystem? movement = null) =>
        CompareCorpusPackets(expected, (IReadOnlyList<PlaintextPacket>)queue.Packets,
            context, expectedState, world, softCapChampion, sourceAttackSpeed, movement);

    private static void CompareCorpusPackets(JsonElement expected,
        IReadOnlyList<PlaintextPacket> packets, string context,
        JsonElement? expectedState = null, WorldState? world = null,
        string? softCapChampion = null, double? sourceAttackSpeed = null,
        MovementSystem? movement = null)
    {
        if (FixtureRecorder.Enabled) { SpellReplayRecording.Packets(expected, packets); return; }
        var cipher = LegacyBlowfish.CreateDefault();
        TestAssert.Equal(expected.GetArrayLength(), packets.Count, context + " packet count");
        for (var index = 0; index < packets.Count; index++)
        {
            var raw = expected[index];
            var packet = packets[index];
            var label = $"{context} packet {index}";
            TestAssert.Equal(raw.GetProperty("channel").GetByte(), packet.Channel, label + " channel");
            TestAssert.Equal(raw.GetProperty("flags").GetByte(), (byte)packet.Reliability, label + " flags");
            var wire = packet.ToWireBytes(cipher);
            TestAssert.Equal(Convert.FromHexString(raw.GetProperty("wire").GetString()!), wire, label + " wire");
            TestAssert.Equal(Convert.FromHexString(raw.GetProperty("plaintext").GetString()!), cipher.Decrypt(wire), label + " plaintext");
        }
    }

    internal static System.Text.Json.Nodes.JsonObject MovementPacketRow(
        PlaintextPacket packet)
    {
        var cipher = LegacyBlowfish.CreateDefault();
        var wire = packet.ToWireBytes(cipher);
        // The corpus comparator decrypts encrypted rows. Keep the projected
        // plaintext in that same padded representation; unencrypted rows
        // retain their short wire payload.
        var plaintext = packet.Encrypt ? cipher.Decrypt(wire) : wire;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["channel"] = packet.Channel,
            ["flags"] = (byte)packet.Reliability,
            ["plaintext"] = Convert.ToHexString(plaintext),
            ["wire"] = Convert.ToHexString(wire),
        };
    }

    private static void Near(double expected, double actual, string context) =>
        TestAssert.Near(expected, actual, 1e-5,
            context + $" expected {expected.ToString("R", CultureInfo.InvariantCulture)} actual {actual.ToString("R", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Why a cast the corpus recorded as accepted is refused now: the assertion alone says only "cast accepted". Empty when the outcome matches.
    /// </summary>
    private static string Refusal(SpellSystem spells, Entity hero, byte[] request, bool matches)
    {
        if (matches) return "";
        if (SpellSystem.ParseCastRequest(request) is not { } parsed) return " (the request does not parse)";
        if (parsed.IsSummoner) return " (summoner slot)";
        var check = spells.CanCast(hero, parsed.Slot, parsed.TargetNetId, parsed.Position, spells.CurrentTime);
        return check.Allowed ? " (CanCast allows it: refused later by the approach, the pending order or the module)"
            : $" (CanCast {check.Failure}: {check.Reason})";
    }

    private sealed record CorpusRuntime(WorldState World, MovementSystem Movement,
        AnnieChecks.ReplayQueue Queue, CombatService Combat,
        BuffLifecycle Lifecycle,
        SpellSystem Spells,
        SharedCloneService? Clones, ShacoBoxService? Boxes,
        TibbersAiSystem? Tibbers,
        PetAiService Pets,
        RecordingRandomSource Random)
    {
        public double MovementAccumulator { get; set; }
        public ItemPassiveService? ItemPassives { get; init; }
        /// <summary>The bound script module of the champion under replay; a hook that raised is skipped by production, so a replay asserts none did.</summary>
        public IReadOnlyList<BbChampionModule> Modules { get; init; } = [];
        /// <summary>The champions' Hero.luaobj host (batch 173; production's <c>buffs.Heroes</c>), stepped by the replay.</summary>
        public HeroAttackOrderSystem? HeroOrders { get; init; }
    }

    private static void AssertNoScriptFailure(CorpusRuntime runtime, string context)
    {
        var failures = runtime.Modules.SelectMany(module => module.ScriptFailures).ToArray();
        TestAssert.Equal(0, failures.Length, context + " script failures: " +
            string.Join("; ", failures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));
    }

    private sealed record ProductionDuelRuntime(MatchRuntime Runtime,
        DuelCaptureTransport Order, DuelCaptureTransport Chaos, CorpusRuntime SnapshotRuntime)
    {
        public long ConnectionFor(Entity hero) => Runtime.Session.Participants
            .First(participant => participant.Hero == hero).ConnectionId
            ?? throw new InvalidOperationException("duel hero has no authenticated connection");
    }

    private sealed class DuelCaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();
        public IReadOnlyList<PlaintextPacket> Packets => _packets;
        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
    }

    private sealed class RecordingRandomSource : IRandomSource
    {
        private readonly List<ExpectedRandomCall> _expected;
        private readonly JsonElement _case;
        private readonly string _context;
        private int _index;

        public RecordingRandomSource(JsonElement calls, string context, JsonElement testCase)
        {
            _context = context;
            _case = testCase;
            _expected = calls.ValueKind == JsonValueKind.Array
                ? calls.EnumerateArray().Select(ReadCall).ToList()
                : new List<ExpectedRandomCall>();
        }

        public double NextDouble()
        {
            var call = Consume("random");
            return call.Value;
        }

        public double Uniform(double minimum, double maximum)
        {
            var call = Consume("uniform", minimum, maximum);
            Near(call.Minimum, minimum, _context + " random uniform minimum");
            Near(call.Maximum, maximum, _context + " random uniform maximum");
            return minimum + call.Fraction * (maximum - minimum);
        }

        public int NextIndex(int count)
        {
            var call = Consume("choice", count: count);
            if (call.Count >= 0 && call.Count != count)
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: recorded random choice count {1} actual {2}",
                    _context, call.Count, count));
            if (call.Index < 0 || call.Index >= count)
                throw new InvalidOperationException($"{_context}: recorded random choice {call.Index} is outside {count}");
            return call.Index;
        }

        public void AssertComplete()
        {
            if (FixtureRecorder.Enabled)
            {
                // Keep the supplied RNG tape intact even when current gameplay
                // consumes fewer draws; consumption is an exact recorded output.
                if (_index != _expected.Count || _case.TryGetProperty("randomCallsConsumed", out _))
                    SpellReplayRecording.Fields(_case, new JsonObject { ["randomCallsConsumed"] = _index });
                return;
            }
            var expected = _case.TryGetProperty("randomCallsConsumed", out var consumed)
                ? consumed.GetInt32() : _expected.Count;
            TestAssert.Equal(expected, _index,
                _context + $" ordered random calls consumed expected={expected} actual={_index}");
        }

        private ExpectedRandomCall Consume(string kind, double minimum = 0, double maximum = 0, int count = -1)
        {
            if (FixtureRecorder.Enabled && _index < _expected.Count && !Draws(_expected[_index], kind, minimum, maximum, count))
            {
                // 19 October 2026: the tape follows its consumer (a script draws the kind and range its Lua names).
                var replacement = new ExpectedRandomCall(kind, .5, minimum, maximum, .35, 0, count);
                _expected[_index] = replacement;
                SpellReplayRecording.ReplaceRandom(_case, _index, DefaultRow(kind, minimum, maximum, count));
            }
            if (_index >= _expected.Count && FixtureRecorder.Enabled)
            {
                var extension = new ExpectedRandomCall(kind, .5, minimum, maximum, .35, 0, count);
                _expected.Add(extension);
                SpellReplayRecording.AppendRandom(_case, DefaultRow(kind, minimum, maximum, count));
            }
            if (_index >= _expected.Count)
                throw new InvalidOperationException($"{_context}: unexpected random call kind={kind}");
            var call = _expected[_index++];
            if (!string.Equals(call.Kind, kind, StringComparison.Ordinal))
                throw new InvalidOperationException($"{_context}: random call {_index} expected {call.Kind} actual {kind}");
            return call;
        }

        private static bool Draws(ExpectedRandomCall call, string kind, double minimum, double maximum, int count) =>
            string.Equals(call.Kind, kind, StringComparison.Ordinal) &&
            (kind != "uniform" || Math.Abs(call.Minimum - minimum) <= 1e-9 && Math.Abs(call.Maximum - maximum) <= 1e-9) &&
            (kind != "choice" || call.Count < 0 || call.Count == count);

        private static JsonObject DefaultRow(string kind, double minimum, double maximum, int count)
        {
            var row = new JsonObject { ["kind"] = kind };
            if (kind == "random") row["value"] = .5;
            else if (kind == "uniform") { row["a"] = minimum; row["b"] = maximum; row["fraction"] = .35; }
            else { row["count"] = count; row["index"] = 0; }
            return row;
        }

        private static ExpectedRandomCall ReadCall(JsonElement value)
        {
            var kind = value.GetProperty("kind").GetString() ?? string.Empty;
            return kind switch
            {
                "random" => new ExpectedRandomCall(kind, value.GetProperty("value").GetDouble(),
                    0.0, 0.0, 0.0, -1),
                "uniform" => new ExpectedRandomCall(kind, 0.0,
                    GetDouble(value, "a", "min"), GetDouble(value, "b", "max"),
                    value.GetProperty("fraction").GetDouble(), -1, -1),
                "choice" => new ExpectedRandomCall(kind, 0.0, 0.0, 0.0, 0.0,
                    value.GetProperty("index").GetInt32(),
                    value.TryGetProperty("count", out var count) ? count.GetInt32() : -1),
                _ => throw new InvalidDataException($"unknown recorded random call kind {kind}"),
            };
        }

        private static double GetDouble(JsonElement value, string primary, string alternate) =>
            value.TryGetProperty(primary, out var field)
                ? field.GetDouble()
                : value.GetProperty(alternate).GetDouble();

        private readonly record struct ExpectedRandomCall(string Kind, double Value,
            double Minimum, double Maximum, double Fraction, int Index, int Count = -1);
    }


    private static void VerifyShacoHallucinateCase(JsonElement testCase, int caseIndex,
        bool expireNaturally)
    {
        var rank = testCase.GetProperty("spec").GetProperty("rank").GetInt32();
        var context = $"Shaco/case{caseIndex}/slot3/rank{rank}/normal/" +
            (expireNaturally ? "expiry" : "death");
        var runtime = CreateProductionCorpusRuntime(testCase, "Shaco");
        var hero = runtime.World.Get(testCase.GetProperty("hero").GetUInt32())
            ?? throw new InvalidOperationException(context + ": missing caster");
        var targetId = InitialState(testCase).GetProperty("entities")
            .EnumerateArray()
            .Where(raw => raw.GetProperty("etype").GetString() == "minion")
            .Select(raw => raw.GetProperty("netid").GetUInt32())
            .Single();
        var target = runtime.World.Get(targetId)
            ?? throw new InvalidOperationException(context + ": missing enemy minion");
        var initialHealth = target.Health;
        var request = testCase.GetProperty("cast").GetProperty("action")
            .GetProperty("request");

        TestAssert.True(runtime.Spells.HandleCastRequest(hero, EncodeRequest(request),
                runtime.Spells.CurrentTime),
            context + ": Hallucinate cast accepted");
        runtime.Queue.Clear();

        Entity? clone = null;
        var initialDistance = double.NaN;
        var closestDistance = double.PositiveInfinity;
        var sawApproachRoute = false;
        var sawAttackRange = false;
        var launchPackets = new List<PlaintextPacket>();
        var impactPackets = new List<PlaintextPacket>();
        for (var step = 0; step < 160; step++)
        {
            TickCorpus(runtime, 0.05);
            clone = runtime.World.Entities.Values.FirstOrDefault(entity =>
                entity.Kind == EntityKind.ShacoClone);
            if (clone is not null)
            {
                var dx = clone.Position.X - target.Position.X;
                var dz = clone.Position.Z - target.Position.Z;
                var distance = Math.Sqrt(dx * dx + dz * dz);
                if (double.IsNaN(initialDistance)) initialDistance = distance;
                closestDistance = Math.Min(closestDistance, distance);
                sawApproachRoute |= clone.Waypoints.Count > 0 || clone.Target is not null;
                // owner decision 27 Sept 2026: edge-to-edge gameplay radii
                sawAttackRange |= distance <= CombatService.AttackReach(clone, target);
                foreach (var packet in runtime.Queue.Packets.ToArray())
                {
                    var payload = packet.Plaintext.Span;
                    if (payload.Length >= 9 && payload[0] == 0x0D &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]) == clone.NetId &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[5..9]) == target.NetId)
                        launchPackets.Add(packet);
                    if (payload.Length >= 10 && payload[0] == 0x68 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]) == hero.NetId &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[6..10]) == target.NetId)
                        impactPackets.Add(packet);
                }
            }
            var hit = impactPackets.Count != 0;
            runtime.Queue.Clear();
            if (hit) break;
        }

        TestAssert.True(clone is not null && clone.Created,
            context + ": Hallucinate creates a clone");
        TestAssert.True(!double.IsNaN(initialDistance) &&
            closestDistance < initialDistance - 1.0,
            context + ": clone makes bounded progress toward its target");
        TestAssert.True(sawApproachRoute,
            context + ": clone issues an approach route");
        var lastDestination = runtime.Pets.LastDestinations.TryGetValue(clone!.NetId,
            out var destination) ? destination.ToString() : "none";
        TestAssert.True(sawAttackRange,
            context + $": clone reaches the legal attack range; initial={initialDistance:R}" +
            $" closest={closestDistance:R} hero={hero.Position} clone={clone.Position}" +
            $" target={target.Position} cloneRadius={clone.Radius:R}" +
            $" targetRadius={target.Radius:R} attackRange={clone.AttackRange:R}" +
            $" attackTarget={clone.AttackTarget} route={string.Join(';', clone.Waypoints)}" +
            $" lastDestination={lastDestination} canAttack={runtime.Combat.CanAttack(clone, target)}");
        TestAssert.True(launchPackets.Count > 0,
            context + ": clone attack launch is emitted");
        TestAssert.True(impactPackets.Count > 0 && target.Health < initialHealth,
            context + ": clone attack deals damage");
        TestAssert.Equal(target.NetId, clone!.AttackTarget,
            context + ": clone retains the selected enemy minion");
        TestAssert.True(launchPackets.All(packet =>
                packet.Channel == LeagueProtocolConstants.CombatChannel &&
                packet.Reliability == LeaguePacketReliability.Unreliable),
            context + ": attack launch wire contract");
        TestAssert.True(impactPackets.All(packet =>
                packet.Channel == LeagueProtocolConstants.CombatChannel &&
                packet.Reliability == LeaguePacketReliability.Unreliable),
            context + ": owner-attributed impact wire contract");

        var healthBeforeCleanup = target.Health;
        if (expireNaturally)
        {
            var controller = clone.Buffs.FirstOrDefault(buff => buff.Name == "HallucinateFull")
                ?? throw new InvalidOperationException(context + ": missing Hallucinate controller");
            runtime.Lifecycle.TickBuffs(clone, controller.ExpiresAt + 1e-6);
        }
        else
        {
            runtime.Combat.Kill(clone, hero.NetId);
        }
        TestAssert.False(clone.Alive, context + ": clone cleanup kills the clone");
        TestAssert.False(clone.Buffs.Any(buff => buff.Name == "HallucinateFull"),
            context + ": Hallucinate controller is cleaned up");
        TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(clone.NetId),
            context + ": clone destination cache is cleaned up");
        TestAssert.True(target.Health < healthBeforeCleanup,
            context + ": clone cleanup resolves its Hallucinate explosion");
        var healthAfterCleanup = target.Health;
        runtime.Lifecycle.TickBuffs(clone, 18.0);
        TestAssert.Near(healthAfterCleanup, target.Health, 1e-8,
            context + ": clone cleanup cannot detonate twice");
    }

    private static void VerifyShacoHallucinateOwnerDeath(JsonElement testCase)
    {
        var spec = testCase.GetProperty("spec");
        var rank = spec.GetProperty("rank").GetInt32();
        var context = $"Shaco/case11/slot3/rank{rank}/owner-dies";
        var runtime = CreateProductionCorpusRuntime(testCase, "Shaco");
        var owner = runtime.World.Get(testCase.GetProperty("hero").GetUInt32())
            ?? throw new InvalidOperationException(context + ": missing caster");
        var targetId = InitialState(testCase).GetProperty("entities")
            .EnumerateArray()
            .Where(raw => raw.GetProperty("etype").GetString() == "minion")
            .Select(raw => raw.GetProperty("netid").GetUInt32())
            .Single();
        var target = runtime.World.Get(targetId)
            ?? throw new InvalidOperationException(context + ": missing enemy minion");
        var request = testCase.GetProperty("cast").GetProperty("action")
            .GetProperty("request");

        TestAssert.True(runtime.Spells.HandleCastRequest(owner, EncodeRequest(request),
                runtime.Spells.CurrentTime),
            context + ": Hallucinate cast accepted");
        runtime.Queue.Clear();

        Entity? clone = null;
        Entity? killer = null;
        Position2? initialClonePosition = null;
        var farthestCloneDisplacement = 0.0;
        var sawBodyClearApproachRoute = false;
        var ownerDeathSeen = false;
        foreach (var frame in testCase.GetProperty("frames").EnumerateArray())
        {
            var actions = frame.GetProperty("actions");
            var observedClone = runtime.World.Entities.Values.SingleOrDefault(entity =>
                entity.Kind == EntityKind.ShacoClone);
            if (observedClone is { Alive: true })
            {
                initialClonePosition ??= observedClone.Position;
                var displacement = observedClone.Position - initialClonePosition.Value;
                farthestCloneDisplacement = Math.Max(farthestCloneDisplacement,
                    Math.Sqrt(displacement.X * displacement.X + displacement.Z * displacement.Z));
                sawBodyClearApproachRoute |= observedClone.Waypoints.Count > 0 ||
                    observedClone.Target is not null;
                var ownerOffset = observedClone.Position - owner.Position;
                var ownerGap = Math.Sqrt(ownerOffset.X * ownerOffset.X +
                    ownerOffset.Z * ownerOffset.Z);
                TestAssert.True(ownerGap + 1.0 >= observedClone.Radius + owner.Radius,
                    context + ": clone pursuit remains outside its live owner's body");
            }
            var actionValues = actions.ValueKind == JsonValueKind.Array
                ? actions.EnumerateArray().ToArray()
                : actions.ValueKind == JsonValueKind.Object
                    ? new[] { actions }
                    : Array.Empty<JsonElement>();
            var ownerDeath = actionValues.FirstOrDefault(action =>
                action.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "kill" &&
                action.TryGetProperty("unit", out var unit) &&
                unit.GetUInt32() == owner.NetId);

            if (ownerDeath.ValueKind != JsonValueKind.Undefined)
            {
                TestAssert.Equal(24, frame.GetProperty("frame").GetInt32(),
                    context + ": owner death remains at the archived trigger frame");
                TestAssert.Near(frame.GetProperty("time").GetDouble() -
                    frame.GetProperty("dt").GetDouble(), runtime.Spells.CurrentTime,
                    1e-8, context + ": owner kill is applied before its frame tick");
                clone = runtime.World.Entities.Values.SingleOrDefault(entity =>
                    entity.Kind == EntityKind.ShacoClone);
                TestAssert.True(clone is not null && clone.Alive,
                    context + ": clone is alive immediately before owner death");
                TestAssert.True(sawBodyClearApproachRoute &&
                    farthestCloneDisplacement > 1.0,
                    context + $": clone advances along a body-clear attack route before owner death; " +
                    $"displacement={farthestCloneDisplacement:R} clone={clone!.Position} " +
                    $"owner={owner.Position} target={target.Position} route={string.Join(';', clone.Waypoints)}");
                TestAssert.Equal(target.NetId, clone!.AttackTarget,
                    context + ": clone keeps the fixture target before owner death");
                // playtest B4 / retail sweep, 27 Sept 2026: fixture hydration
                // does not advance the allocator. Do not replace its original
                // minion with the synthetic explosion recipient.
                uint victimId;
                do { victimId = runtime.World.Allocate(UnitType.Minion); }
                while (runtime.World.Get(victimId) is not null);
                var explosionVictim = new Entity(victimId,
                    EntityKind.Minion, UnitType.Minion, Team.Chaos,
                    clone.Position + new Position2(0, 100))
                {
                    Name = "CloneExplosionVictim", MinionType = MinionType.Melee,
                    Health = 900, MaxHealth = 900, SpellBlock = target.SpellBlock,
                    Radius = target.Radius, Created = true, Visible = true,
                };
                runtime.World.Register(explosionVictim);
                var victimHealthBeforeOwnerDeath = explosionVictim.Health;
                var healthBeforeOwnerDeath = target.Health;
                var explosionDx = clone.Position.X - target.Position.X;
                var explosionDz = clone.Position.Z - target.Position.Z;
                TestAssert.True(ReferenceEquals(target, runtime.World.Get(target.NetId)),
                    context + ": synthetic victim preserves the original target identity");
                var originalTargetWithinExplosion =
                    explosionDx * explosionDx + explosionDz * explosionDz <= 300.0 * 300.0;
                var killerId = ownerDeath.GetProperty("killer").GetUInt32();
                killer = runtime.World.Get(killerId)
                    ?? throw new InvalidOperationException(context + ": missing owner killer");
                var killsBefore = killer.ChampionKills;

                // Apply the recorded action before the frame tick. Owner decision,
                // 18 October 2026 (owner's information): Combat.Kill no longer
                // cascades to the clone; it lives out its 18 s and explodes then.
                ApplyCorpusActions(actions, runtime, "Shaco");
                ownerDeathSeen = true;
                TestAssert.False(owner.Alive,
                    context + ": recorded owner kill resolves the owner");
                TestAssert.True(clone.Alive,
                    context + ": owner death leaves the Hallucinate clone alive");
                TestAssert.True(clone.Buffs.Any(buff => buff.Name == "HallucinateFull"),
                    context + ": the Hallucinate controller outlives the owner");
                TestAssert.Equal(killsBefore + 1, killer.ChampionKills,
                    context + ": owner killer receives normal champion credit");
                TestAssert.Near(victimHealthBeforeOwnerDeath, explosionVictim.Health, 1e-8,
                    context + ": owner death causes no clone explosion");

                // The controller expires on its own: one owner-attributed explosion.
                runtime.Lifecycle.TickBuffs(clone, 18.0);
                TestAssert.False(clone.Alive,
                    context + ": the clone ends with its controller");
                TestAssert.True(explosionVictim.Health < victimHealthBeforeOwnerDeath,
                    context + ": expiry hits an enemy inside the 300-unit explosion");
                if (originalTargetWithinExplosion)
                    TestAssert.True(target.Health < healthBeforeOwnerDeath,
                        context + ": original target inside the explosion radius takes damage");
                else
                    TestAssert.Near(healthBeforeOwnerDeath, target.Health, 1e-8,
                        context + $": original target outside the 300-unit radius is unharmed; " +
                        $"distance={Math.Sqrt(explosionDx * explosionDx + explosionDz * explosionDz):R}");
                var explosionPackets = runtime.Queue.Packets.Where(packet =>
                {
                    var payload = packet.Plaintext.Span;
                    return payload.Length >= 18 && payload[0] == 0x68 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]) == owner.NetId &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[6..10]) == explosionVictim.NetId;
                }).ToArray();
                TestAssert.Equal(1, explosionPackets.Length,
                    context + ": expiry emits one owner-attributed clone explosion hit");
                TestAssert.Equal(LeagueProtocolConstants.CombatChannel,
                    explosionPackets[0].Channel,
                    context + ": clone explosion uses the combat channel");
                TestAssert.Equal(LeaguePacketReliability.Unreliable,
                    explosionPackets[0].Reliability,
                    context + ": clone explosion uses unreliable combat delivery");
                TestAssert.Equal((byte)0x84, explosionPackets[0].Plaintext.Span[5],
                    context + ": clone explosion uses the magic-damage wire type");
                TestAssert.Near(500.0,
                    BitConverter.ToSingle(explosionPackets[0].Plaintext.Span[14..18]), 1e-5,
                    context + ": clone explosion wire damage");

                var healthAfterExplosion = explosionVictim.Health;
                TickCorpus(runtime, frame.GetProperty("dt").GetDouble());
                TestAssert.Near(healthAfterExplosion, explosionVictim.Health, 1e-8,
                    context + ": the explosion does not repeat on the frame tick");
                TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(clone.NetId),
                    context + ": expiry cleans the clone destination cache");
                runtime.Lifecycle.TickBuffs(clone, 18.0);
                TestAssert.Near(healthAfterExplosion, explosionVictim.Health, 1e-8,
                    context + ": a cleaned clone cannot detonate twice");
                break;
            }

            ApplyCorpusActions(actions, runtime, "Shaco");
            TickCorpus(runtime, frame.GetProperty("dt").GetDouble());
            runtime.Queue.Clear();
        }

        TestAssert.True(ownerDeathSeen,
            context + ": fixture must contain the owner-death trigger");
        TestAssert.True(killer is not null,
            context + ": owner-death must identify a champion killer");
    }

    private static Entity ReadEntity(JsonElement raw)
    {
        if (raw.TryGetProperty("unitType", out _))
            return ReadCompactEntity(raw);
        var champion = raw.TryGetProperty("champion_name", out var championName)
            ? championName.GetString() ?? "Annie"
            : raw.TryGetProperty("champion", out var oldChampion)
                ? oldChampion.GetString() ?? "Annie" : "Annie";
        var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unit_type").GetInt32(),
            (Team)raw.GetProperty("team").GetInt32(), Point(raw.GetProperty("pos")))
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
            Alive = raw.GetProperty("alive").GetBoolean(), Created = raw.GetProperty("created").GetBoolean(),
            Visible = raw.GetProperty("visible").GetBoolean(), OwnerNetId = raw.GetProperty("owner_netid").GetUInt32(),
            ExpiresAt = SpellReplayRecording.Number(raw.GetProperty("expires_at")), Gold = raw.GetProperty("gold").GetDouble(),
            Experience = raw.GetProperty("exp").GetDouble(), CooldownMod = raw.GetProperty("cooldown_mod").GetDouble(),
            HealthRegen = raw.GetProperty("hp_regen").GetDouble(), ManaRegen = raw.GetProperty("mp_regen").GetDouble(),
            ChampionName = champion, Name = raw.GetProperty("name").GetString() ?? string.Empty,
            Facing = Point(raw.GetProperty("facing")),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            entity.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        entity.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray().Select(v => v.GetInt32()).ToList();
        entity.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.SummonerCooldowns = raw.GetProperty("summoner_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.ItemCooldowns = raw.GetProperty("item_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.Waypoints = raw.GetProperty("waypoints").EnumerateArray().Select(Point).ToList();
        if (raw.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null)
            entity.Target = Point(target);
        if (raw.TryGetProperty("protected", out var protectedField))
            entity.Protected = protectedField.GetBoolean();
        if (raw.TryGetProperty("is_ward", out var ward)) entity.IsWard = ward.GetBoolean();
        if (raw.TryGetProperty("god_mode", out var god)) entity.GodMode = god.GetBoolean();
        if (raw.TryGetProperty("cooldowns_disabled", out var cooldownsDisabled))
            entity.CooldownsDisabled = cooldownsDisabled.GetBoolean();
        if (raw.TryGetProperty("gold_reward", out var goldReward)) entity.GoldReward = goldReward.GetDouble();
        if (raw.TryGetProperty("global_gold_reward", out var globalGoldReward))
            entity.GlobalGoldReward = globalGoldReward.GetDouble();
        if (raw.TryGetProperty("xp_reward", out var xpReward)) entity.ExperienceReward = xpReward.GetDouble();
        if (raw.TryGetProperty("death_time", out var deathTime) && deathTime.ValueKind != JsonValueKind.Null)
            entity.DeathTime = deathTime.GetDouble();
        if (raw.TryGetProperty("respawn_at", out var respawnAt)) entity.RespawnAt = respawnAt.GetDouble();
        if (raw.TryGetProperty("teleport_id", out var teleportId)) entity.TeleportId = teleportId.GetByte();
        if (raw.TryGetProperty("forced_move_grace", out var forcedMoveGrace))
            entity.ForcedMoveGrace = forcedMoveGrace.GetDouble();
        if (raw.TryGetProperty("anchor_path", out var anchorPath))
            entity.AnchorPath = anchorPath.EnumerateArray().Select(Point).ToList();
        if (raw.TryGetProperty("last_anchor_time", out var lastAnchorTime))
            entity.LastAnchorTime = lastAnchorTime.GetDouble();
        // Batch 149 step 4: `odyn_stored_damage` is a retired field (Entity.OdynStoredDamage is gone; the shipped
        // Spells/OdynsVeil.luaobj keeps the store in its own CharVars.StoredDamage), so the archived column is not
        // applied here either.
        if (raw.TryGetProperty("life_steal", out var lifeSteal)) entity.LifeSteal = lifeSteal.GetDouble();
        if (raw.TryGetProperty("spell_vamp", out var spellVamp)) entity.SpellVamp = spellVamp.GetDouble();
        if (raw.TryGetProperty("crit_chance", out var critChance)) entity.CritChance = critChance.GetDouble();
        if (raw.TryGetProperty("crit_multiplier", out var critMultiplier)) entity.CritMultiplier = critMultiplier.GetDouble();
        if (raw.TryGetProperty("armor_pen_flat", out var armorPenFlat)) entity.ArmorPenetrationFlat = armorPenFlat.GetDouble();
        if (raw.TryGetProperty("armor_pen_percent", out var armorPenPercent)) entity.ArmorPenetrationPercent = armorPenPercent.GetDouble();
        if (raw.TryGetProperty("magic_pen_flat", out var magicPenFlat)) entity.MagicPenetrationFlat = magicPenFlat.GetDouble();
        if (raw.TryGetProperty("magic_pen_percent", out var magicPenPercent)) entity.MagicPenetrationPercent = magicPenPercent.GetDouble();
        if (raw.TryGetProperty("dodge", out var dodge)) entity.Dodge = dodge.GetDouble();
        if (raw.TryGetProperty("hardness", out var hardness)) entity.Hardness = hardness.GetDouble();
        if (raw.TryGetProperty("forced_move", out var forcedMove) &&
            forcedMove.ValueKind == JsonValueKind.Object && forcedMove.EnumerateObject().Any())
        {
            var destination = Point(forcedMove.GetProperty("destination"));
            entity.ForcedMove = new ForcedMoveState(destination,
                forcedMove.GetProperty("speed").GetDouble(), forcedMove.GetProperty("remaining").GetDouble(),
                forcedMove.GetProperty("source_netid").GetUInt32(), forcedMove.GetProperty("effect").GetString() ?? "move",
                forcedMove.GetProperty("first_collision").GetBoolean())
            {
                Completed = forcedMove.GetProperty("completed").GetBoolean(),
            };
        }
        LoadCorpusBuffs(entity, raw.GetProperty("buffs"));
        AnnieChecks.PrepareInputTiming(entity, raw);
        return entity;
    }

    private static Entity ReadCompactEntity(JsonElement raw)
    {
        var champion = raw.GetProperty("champion").GetString() ?? "Annie";
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
            Alive = raw.GetProperty("alive").GetBoolean(), Created = raw.GetProperty("created").GetBoolean(),
            Visible = raw.GetProperty("visible").GetBoolean(), OwnerNetId = raw.GetProperty("owner_netid").GetUInt32(),
            ExpiresAt = SpellReplayRecording.Number(raw.GetProperty("expires_at")), Gold = raw.GetProperty("gold").GetDouble(),
            Experience = raw.GetProperty("exp").GetDouble(), CooldownMod = raw.GetProperty("cooldown_mod").GetDouble(),
            HealthRegen = raw.GetProperty("hp_regen").GetDouble(), ManaRegen = raw.GetProperty("mp_regen").GetDouble(),
            GoldReward = raw.GetProperty("gold_reward").GetDouble(),
            GlobalGoldReward = raw.GetProperty("global_gold_reward").GetDouble(),
            ExperienceReward = raw.GetProperty("xp_reward").GetDouble(),
            ChampionName = champion, Name = raw.GetProperty("name").GetString() ?? string.Empty,
            Facing = Point(raw.GetProperty("facing")),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            entity.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        entity.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray().Select(v => v.GetInt32()).ToList();
        entity.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.SummonerCooldowns = raw.GetProperty("summoner_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.ItemCooldowns = raw.GetProperty("item_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.Waypoints = raw.GetProperty("waypoints").EnumerateArray().Select(Point).ToList();
        if (raw.GetProperty("target").ValueKind != JsonValueKind.Null)
            entity.Target = Point(raw.GetProperty("target"));
        LoadCorpusBuffs(entity, raw.GetProperty("buffs"));
        AnnieChecks.PrepareInputTiming(entity, raw);
        return entity;
    }

    private static void LoadCorpusBuffs(Entity entity, JsonElement raw)
    {
        foreach (var value in raw.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (value.TryGetProperty("payload", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in fields.EnumerateObject())
                    SetPayloadField(payload, field.Name, field.Value);
            }
            int? maxStack = value.GetProperty("max_stack").ValueKind == JsonValueKind.Null
                ? null : value.GetProperty("max_stack").GetInt32();
            entity.Buffs.Add(new BuffState(value.GetProperty("name").GetString() ?? "CorpusBuff",
                payload, (BuffType)value.GetProperty("type").GetInt32(), value.GetProperty("slot").GetInt32(),
                value.GetProperty("expires_at").GetDouble(), value.GetProperty("caster").GetUInt32(),
                value.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1, maxStack));
        }
    }

    private static void SetPayloadField(BuffPayload payload, string sourceName, JsonElement value)
    {
        var propertyName = sourceName switch
        {
            "caster_pos" => nameof(BuffPayload.CasterPosition),
            "deceive_pos" => nameof(BuffPayload.DeceivePosition),
            "fear_origin" => nameof(BuffPayload.FearOrigin),
            "pos" => nameof(BuffPayload.Position),
            "extra_fx_netid" => nameof(BuffPayload.ExtraFxNetId),
            "fx_netid" => nameof(BuffPayload.FxNetId),
            "silver_caster" => nameof(BuffPayload.SilverCasterNetId),
            "stunned" => nameof(BuffPayload.ActionDisabled),
            _ => SnakeToPascal(sourceName),
        };
        var property = typeof(BuffPayload).GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidDataException($"fixture payload field {sourceName} has no typed property {propertyName}");
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? converted;
        if (targetType == typeof(Position2))
            converted = Point(value);
        else if (targetType == typeof(bool))
            converted = value.ValueKind == JsonValueKind.Number ? Math.Abs(value.GetDouble()) > 1e-12 : value.GetBoolean();
        else if (targetType == typeof(double))
            converted = value.GetDouble();
        else if (targetType == typeof(int))
            converted = value.GetInt32();
        else if (targetType == typeof(uint))
            converted = value.GetUInt32();
        else if (targetType == typeof(string))
            converted = value.GetString();
        else if (targetType.IsEnum)
            converted = Enum.Parse(targetType, value.GetString()!, ignoreCase: true);
        else if (property.PropertyType == typeof(IReadOnlyList<uint>))
            converted = value.EnumerateArray().Select(item => item.GetUInt32()).ToArray();
        else
            throw new InvalidDataException($"fixture payload field {sourceName} has unsupported property type {property.PropertyType}");
        property.SetValue(payload, converted);
    }

    private static byte[] EncodeRequest(JsonElement request)
    {
        var position = request.GetProperty("pos");
        var end = request.GetProperty("end_pos");
        var writer = new PacketBuffer(34);
        writer.WriteBytes(new byte[5]);
        var slot = checked((byte)request.GetProperty("slot").GetInt32());
        writer.WriteByte((byte)(slot << 1));
        WritePoint(writer, position);
        WritePoint(writer, end);
        writer.WriteUInt32(request.GetProperty("target_netid").GetUInt32());
        return writer.ToArray();
    }

    private static byte[] EncodeDuelRequest(JsonElement request, Position2 defaultPoint)
    {
        var arguments = request.GetProperty("arguments");
        var writer = new PacketBuffer(34);
        writer.WriteBytes(new byte[5]);
        writer.WriteByte(checked((byte)(request.GetProperty("slot").GetInt32() << 1)));
        if (arguments.TryGetProperty("pos", out var position) &&
            position.ValueKind == JsonValueKind.Array)
            WritePoint(writer, position);
        else
            WritePoint(writer, new Position3(defaultPoint.X, 0.0, defaultPoint.Z));
        if (arguments.TryGetProperty("end", out var end) &&
            end.ValueKind == JsonValueKind.Array)
            WritePoint(writer, end);
        else
            WritePoint(writer, new Position3(defaultPoint.X, 0.0, defaultPoint.Z));
        var target = arguments.TryGetProperty("target", out var targetValue) &&
            targetValue.ValueKind != JsonValueKind.Null
            ? targetValue.GetUInt32() : 0u;
        writer.WriteUInt32(target);
        return writer.ToArray();
    }

    private static byte[] EncodeAttackOrder(Entity actor, Entity target)
    {
        var writer = new PacketBuffer(22);
        writer.WriteByte(0x75);
        writer.WriteUInt32(actor.NetId);
        writer.WriteByte(3); // source ORDER_ATTACK_TO
        writer.WriteSingle(target.Position.X);
        writer.WriteSingle(0.0);
        writer.WriteSingle(target.Position.Z);
        writer.WriteUInt32(target.NetId);
        return writer.ToArray();
    }

    private static JsonElement InitialState(JsonElement testCase) =>
        testCase.TryGetProperty("initial", out var initial)
            ? initial : testCase.GetProperty("initialState");

    private static void WritePoint(PacketBuffer writer, JsonElement point)
    {
        writer.WriteSingle(point[0].GetDouble());
        writer.WriteSingle(point[1].GetDouble());
        writer.WriteSingle(point[2].GetDouble());
    }

    private static void WritePoint(PacketBuffer writer, Position3 point)
    {
        writer.WriteSingle(point.X);
        writer.WriteSingle(point.Y);
        writer.WriteSingle(point.Z);
    }

    private static Position2 Point(JsonElement value) => value.GetArrayLength() == 3
        ? new Position2(value[0].GetDouble(), value[2].GetDouble())
        : new Position2(value[0].GetDouble(), value[1].GetDouble());

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, context + " count");
        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            Near(expected[index][0].GetDouble(), actual[index].X, context + $"[{index}] X");
            Near(expected[index][1].GetDouble(), actual[index].Z, context + $"[{index}] Z");
        }
    }

    private static void CompareList(JsonElement expected, IReadOnlyList<double> actual, string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, context + " count");
        for (var index = 0; index < expected.GetArrayLength(); index++)
            Near(expected[index].GetDouble(), actual[index], context + $"[{index}]");
    }

}

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-shaco-b5-shiv-data-ad-coefficient")]
    public static void ShacoShivReadsSecondCoefficient()
    {
        // 19 October 2026: Two-Shiv Poison runs from TwoShivPoison.luaobj, whose TargetExecute takes half of the total attack damage as a
        // literal (BBMath x 0.5) next to the rank table (40/80/120/160/200) and the spell's AP ratio (SpellDamageRatio 1); the isolated
        // coefficient of the inibin (Coefficient2 .5) is the same number the tooltip shows, not an input any more.
        var data = new GameData();
        var spell = data.Spell("TwoShivPoison", 5);
        TestAssert.Near(.5, spell.Coefficient2, 1e-9, "shipped coefficient");
        using var match = new Match("Shaco", Team.Order, data: data);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        hero.AttackDamage = 100; hero.AbilityPower = 20;
        enemy.Facing = new Position2(-1, 0); enemy.SpellBlock = 0;
        var damage = enemy.MatchStatistics.TotalDamageTaken;
        match.Cast(0, 2, enemy);
        SpellStep(match, .25); SpellStep(match, 1);
        TestAssert.Near(40 + 50 + 20, enemy.MatchStatistics.TotalDamageTaken - damage, 1e-6,
            "rank-one E is the table, half of the attack damage and the full ability power at impact");
    }
}
