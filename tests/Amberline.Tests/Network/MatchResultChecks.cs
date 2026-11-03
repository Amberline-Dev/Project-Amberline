using System.Text.Json;
using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Server;
using Amberline.Simulation;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;

namespace Amberline.Tests.Network;

public static class MatchResultChecks
{
    [Check("server.match-result-persistence")]
    public static void FinalResultIsDetachedAndPersistedOnce()
    {
        foreach (var winner in new[] { Team.Order, Team.Chaos })
        foreach (var count in new[] { 1, 10 })
        {
            var root = Path.Combine(Path.GetTempPath(), "amberline-result-" + Guid.NewGuid().ToString("N"));
            using var logger = ServerLogger.Open(root, new StringWriter());
            var runtime = CreateMatch(logger, count, winner);
            var sessionDirectory = Directory.GetDirectories(root).Single();
            var path = Path.Combine(sessionDirectory, "match-result.json");
            TestAssert.False(File.Exists(path), "unfinished matches have no final result");
            TestAssert.True(runtime.Session.Result is null);
            runtime.Tick(1.25);
            var hero = runtime.Session.Participants[0].Hero!;
            hero.ChampionKills = 3; hero.ChampionDeaths = 5; hero.ChampionAssists = 7;
            hero.KillingSprees = 2; hero.LargestKillingSpree = 3;
            hero.TurretsKilled = 4; hero.InhibitorsKilled = 2;
            hero.LaneMinionsKilled = 31;
            hero.OtherMinionsKilled = 4;
            hero.NeutralMinionsKilled = 9; hero.Gold = 1311.25; hero.Experience = 512.5; hero.Level = 3;
            hero.SpellRanks[0] = 2;
            var inventory = hero.Inventory.ToArray();
            inventory[0] = new InventorySlot(1055);
            inventory[3] = new InventorySlot(2003, 2);
            hero.Inventory = inventory;
            TestAssert.True(runtime.Session.Disconnect(count));
            var duration = runtime.Combat.CurrentTime;
            var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team != winner);
            // Ten-seat cases keep seat 1 on Order even when Chaos wins;
            // this direct lifecycle call must not credit a friendly last hit.
            var nexusKills = hero.Team == winner ? 1 : 0;
            TestAssert.True(runtime.Combat.Kill(nexus, hero.NetId));
            var result = runtime.Session.Result ?? throw new InvalidOperationException("No final result");
            TestAssert.Equal(winner.ToString(), result.Winner);
            TestAssert.Near(duration, result.DurationSeconds, 1e-9);
            TestAssert.Equal(count, result.Players.Count, "disconnected seats stay in the result");
            TestAssert.False(result.Players[^1].Connected);
            var player = result.Players[0];
            TestAssert.Equal((3, 5, 7, 9), (player.Kills, player.Deaths, player.Assists, player.NeutralMinionsKilled));
            TestAssert.Equal(31, player.LaneMinionsKilled);
            TestAssert.Equal(4, player.OtherMinionsKilled);
            TestAssert.Equal((2, 3, 4, 2, nexusKills), (player.KillingSprees, player.LargestKillingSpree,
                player.TurretsKilled, player.InhibitorsKilled, player.NexusesKilled));
            TestAssert.Equal((1311.25, 512.5, 3), (player.GoldOnHand, player.Experience, player.Level));
            TestAssert.Equal((1UL, "Final 1", "Annie", 6), (player.PlayerId, player.Name, player.Champion, player.SkinId));
            TestAssert.Equal(new[] { "SummonerHeal", "SummonerTeleport" }, player.SummonerSpells.ToArray());
            TestAssert.Equal(6, player.Inventory.Count);
            TestAssert.Equal(new MatchResultItem(2003, 2, 0), player.Inventory[3]);
            TestAssert.True(player.Inventory[1] is null);
            var saved = File.ReadAllText(path);
            using var document = JsonDocument.Parse(saved);
            TestAssert.Equal(count, document.RootElement.GetProperty("result").GetProperty("Players").GetArrayLength());
            TestAssert.Equal(winner.ToString(), document.RootElement.GetProperty("result").GetProperty("Winner").GetString());
            TestAssert.Equal(8, document.RootElement.GetProperty("result").GetProperty("SchemaVersion").GetInt32());
            TestAssert.False(document.RootElement.GetProperty("result").GetProperty("Surrendered").GetBoolean());
            TestAssert.Equal(31, document.RootElement.GetProperty("result").GetProperty("Players")[0]
                .GetProperty("LaneMinionsKilled").GetInt32());
            TestAssert.Equal(4, document.RootElement.GetProperty("result").GetProperty("Players")[0]
                .GetProperty("OtherMinionsKilled").GetInt32());

            hero.SpellRanks[0] = 5; hero.SummonerSpells[0] = "SummonerRevive";
            inventory[0] = null; hero.ChampionKills = 99;
            hero.LaneMinionsKilled = 99;
            hero.OtherMinionsKilled = 99;
            hero.KillingSprees = hero.LargestKillingSpree = hero.TurretsKilled =
                hero.InhibitorsKilled = hero.NexusesKilled = 99;
            runtime.Tick(6);
            TestAssert.Equal(2, player.SpellRanks[0]);
            TestAssert.Equal("SummonerHeal", player.SummonerSpells[0]);
            TestAssert.Equal(1055, player.Inventory[0]!.ItemId);
            TestAssert.Equal(3, player.Kills);
            TestAssert.Equal(31, player.LaneMinionsKilled);
            TestAssert.Equal(4, player.OtherMinionsKilled);
            TestAssert.Equal((2, 3, 4, 2, nexusKills), (player.KillingSprees, player.LargestKillingSpree,
                player.TurretsKilled, player.InhibitorsKilled, player.NexusesKilled));
            TestAssert.Near(duration, result.DurationSeconds, 1e-9, "end animation time does not extend match duration");
            TestAssert.False(runtime.Combat.Kill(nexus, hero.NetId), "duplicate death is ignored");
            logger.MatchCompleted(result with { Winner = "invalid replacement" });
            logger.Dispose();
            TestAssert.Equal(saved, File.ReadAllText(path), "final result is written once");
            TestAssert.False(File.Exists(path + ".tmp"), "successful export leaves only its final file");
            TestAssert.Equal(1, File.ReadLines(Path.Combine(sessionDirectory, "session.jsonl"))
                .Count(line => line.Contains("\"msg\":\"match.ended\"", StringComparison.Ordinal)));
        }
    }

    [Check("server.match-result-leaver-rule")]
    public static void LeaverFollowsTheShippedDisconnectRule()
    {
        // GamePermanent.cfg [Disconnect]: .12 / .10 of the game, or 300 / 240 s still disconnected at the end (win / loss).
        var rules = LeaverRules.Load(GameData.Default.Paths);
        TestAssert.Near(0.12, rules.PercentOnWin, 0);
        TestAssert.Near(0.10, rules.PercentOnFail, 0);
        TestAssert.Near(300, rules.SecondsAtEndOnWin, 0);
        TestAssert.Near(240, rules.SecondsAtEndOnFail, 0);
        TestAssert.False(rules.IsLeaver(true, 1800, 0, false, 0), "never disconnected");
        TestAssert.False(rules.IsLeaver(true, 1800, 200, false, 900), "11% of a won game, back in the game");
        TestAssert.True(rules.IsLeaver(true, 1800, 216, false, 900), "12% of a won game");
        TestAssert.False(rules.IsLeaver(false, 1800, 170, false, 900), "9.4% of a lost game");
        TestAssert.True(rules.IsLeaver(false, 1800, 180, false, 900), "10% of a lost game");
        TestAssert.False(rules.IsLeaver(true, 6000, 299, true, 299), "299 s gone at the end of a won game");
        TestAssert.True(rules.IsLeaver(true, 6000, 300, true, 300), "300 s gone at the end of a won game");
        TestAssert.True(rules.IsLeaver(false, 6000, 240, true, 240), "240 s gone at the end of a lost game");
        TestAssert.False(rules.IsLeaver(false, 6000, 240, false, 240), "240 s gone, but back before the end");
        TestAssert.False(rules.IsLeaver(true, 0, 0, true, 0), "no time played");
    }

    [Check("server.match-result-write-failure")]
    public static void ExportFailureDoesNotInterruptVictory()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-result-failure-" + Guid.NewGuid().ToString("N"));
        using var logger = ServerLogger.Open(root, new StringWriter());
        var runtime = CreateMatch(logger, 2, Team.Order);
        var sessionDirectory = Directory.GetDirectories(root).Single();
        // An occupied destination exercises an actual filesystem failure without changing permissions.
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "match-result.json"));
        var capture = new Capture();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order,
            runtime.Session.Participants[0].Hero!.NetId, capture, ready: true));
        var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        TestAssert.True(runtime.Combat.Kill(nexus));
        runtime.Tick(8.6);
        logger.Dispose();
        TestAssert.True(runtime.Session.Result is not null, "in-memory result survives an export failure");
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0xCE), "victory sequence still finishes");
        var log = File.ReadAllText(Path.Combine(sessionDirectory, "session.jsonl"));
        TestAssert.True(log.Contains("match.result.save-failed", StringComparison.Ordinal), "export failure is reported");
        TestAssert.True(log.Contains("match.ended", StringComparison.Ordinal), "JSONL retains the result");
    }

    [Check("combat.nexus-winner-is-final")]
    public static void FirstNexusDeathDecidesTheMatch()
    {
        foreach (var winner in new[] { Team.Order, Team.Chaos })
        {
            var runtime = CreateMatch(null, 2, winner);
            var capture = new Capture();
            runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order,
                runtime.Session.Participants[0].Hero!.NetId, capture, ready: true));
            var nexuses = runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.Nexus).ToArray();
            TestAssert.True(runtime.Combat.Kill(nexuses.Single(nexus => nexus.Team != winner)));
            TestAssert.False(runtime.Combat.Kill(nexuses.Single(nexus => nexus.Team == winner)),
                "a second Nexus death in the same frame cannot reverse the winner");
            runtime.Tick(8.6);
            foreach (var opcode in new byte[] { 0xCD, 0xCE })
            {
                var packet = capture.Packets.Single(packet => packet.Plaintext.Span[0] == opcode);
                TestAssert.Equal(winner == Team.Order ? (byte)1 : (byte)0, packet.Plaintext.Span[5]);
            }
        }
    }

    private static MatchRuntime CreateMatch(ISessionLogSink? log, int count, Team soloTeam, string champion = "Annie")
    {
        var roster = Enumerable.Range(0, count).Select(index => new SessionParticipantDefinition(
            (ulong)index + 1, (uint)index, $"Final {index + 1}", champion,
            count == 1 ? soloTeam : index % 2 == 0 ? Team.Order : Team.Chaos,
            "SummonerHeal", "SummonerTeleport", SkinId: champion == "Annie" ? 6 : 0)).ToArray();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0,
            sessionLog: log, roster: roster);
        runtime.World.Clock.SetVirtual(1234);
        for (var id = 1; id <= count; id++)
        {
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null);
            runtime.Session.Spawn(id);
            runtime.Session.MarkReady(id);
            runtime.Session.Participants[id - 1].Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(runtime.Session.Start());
        return runtime;
    }

    [Check("combat.end-game-ai-halt")]
    public static void EndGameStopsOrdersAndPetAiWhileLifetimesContinue()
    {
        foreach (var winner in new[] { Team.Order, Team.Chaos })
        foreach (var viaPendingHit in new[] { false, true })
        {
            // Shaco seats: the box's AI is the script's JackInTheBox buff (batch 106), which only a match with a Shaco has bound.
            var runtime = CreateMatch(null, 2, winner, "Shaco");
            runtime.Movement.CollisionSolverEnabled = false;
            var player = runtime.Session.Participants.Single(p => p.Team == winner);
            var owner = player.Hero!;
            var enemy = runtime.Session.Participants.Single(p => p.Team != winner).Hero!;
            owner.Position = new(7000, 7000);
            enemy.Position = new(7400, 7300);
            var pet = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                EntityKind.Pet, UnitType.Monster, winner, new(7000, 6500))
            {
                Name = "AnnieTibbers", OwnerNetId = owner.NetId, Health = 1000, MaxHealth = 1000,
                Speed = 350, ExpiresAt = 0.6, Created = true,
            });
            var clone = runtime.Clones.Spawn(owner, owner, new(6600, 7000), CloneKind.YorickRevenant,
                1000, "YorickRAPetBuff2", 10, "Revenant");
            var ghoul = YorickScriptSupport.SpawnUnit(runtime, owner, "Spectral", new(7000, 6600), 0);
            var box = ShacoScriptSupport.SpawnBox(runtime, owner, new(7400, 7250));
            var actors = new[] { owner, pet, clone, ghoul };
            foreach (var actor in actors)
            {
                runtime.Movement.SetMoveOrder(actor.NetId, new(actor.Position.X + 200, actor.Position.Z));
                actor.AttackTarget = enemy.NetId;
                TestAssert.True(actor.Waypoints.Count > 0 || actor.Target is not null);
            }
            var positions = actors.Select(actor => actor.Position).ToArray();
            var displacement = new ForcedMoveState(new(7600, 7300), 400, 1, owner.NetId, "test");
            enemy.ForcedMove = displacement;
            var capture = new Capture();
            runtime.Delivery.AddObserver(new ReplicationObserver(player.PlayerId, winner,
                owner.NetId, capture, ready: true));
            runtime.Delivery.Flush();
            capture.Packets.Clear();
            var nexus = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.Nexus && unit.Team != winner);
            if (viaPendingHit)
            {
                nexus.Protected = false;
                runtime.Combat.QueueScriptedHit(owner, nexus, 100000,
                    DamageType.True, DamageSourceKind.Spell, 0);
                runtime.Combat.QueueScriptedHit(owner, enemy, 20,
                    DamageType.True, DamageSourceKind.Spell, 1);
                runtime.Combat.QueueScriptedHit(owner, enemy, 20,
                    DamageType.True, DamageSourceKind.Spell, 0);
                var health = enemy.Health;
                runtime.Combat.CombatTick(0.01, regenerate: false);
                TestAssert.True(runtime.Combat.GameOver);
                TestAssert.Equal(health, enemy.Health, "the same hit batch stops after the Nexus ends the match");
                TestAssert.Equal(1, runtime.Combat.PendingHits.Count,
                    "unreleased windups cancel; the existing launched hit remains paused");
            }
            else runtime.Combat.Kill(nexus);
            foreach (var actor in actors)
            {
                TestAssert.Equal(0, actor.Waypoints.Count, "Nexus completion clears ordinary paths");
                TestAssert.True(actor.Target is null && actor.AttackTarget is null);
            }
            runtime.Delivery.Flush();
            var stop = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner.NetId);
            TestAssert.Equal(17, stop.Plaintext.Length, "one-position stop reaches the moving player's client");
            TestAssert.Equal((float)owner.Position.X, BinaryPrimitives.ReadSingleLittleEndian(stop.Plaintext.Span[9..]));
            TestAssert.True(capture.Packets.IndexOf(stop) < capture.Packets.FindIndex(packet =>
                packet.Plaintext.Span[0] == 0xCD), "stop precedes the victory animation");
            runtime.Tick(0.4);
            for (var i = 0; i < actors.Length; i++)
            {
                TestAssert.Equal(positions[i], actors[i].Position, "halted pet AI cannot restart pursuit/follow");
                TestAssert.True(actors[i].AttackTarget is null);
            }
            TestAssert.True(runtime.Session.Disconnect(player.ConnectionId!.Value));
            runtime.Tick(2);
            TestAssert.False(pet.Alive, "pet lifetime cleanup continues");
            TestAssert.True(displacement.Completed, "existing scripted displacement can finish");
            TestAssert.False(ShacoScriptSupport.IsAwake(box),
                "a box cannot acquire and fear a new target during the ceremony");
        }
    }

    [Check("combat.end-game-minion-no-render-state")]
    public static void EndGameMinionNoRenderSurvivesLateRevealAndStatsRefresh()
    {
        var runtime = CreateMatch(null, 2, Team.Order);
        var capture = new Capture();
        var observer = new ReplicationObserver(1, Team.Order,
            runtime.Session.Participants[0].Hero!.NetId, capture, ready: true);
        runtime.Delivery.AddObserver(observer);
        var beforePhase = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(5000, 5000))
        {
            Created = true, Alive = true, Health = 211, MaxHealth = 900,
        });
        var winningTeamMinion = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(5000, 5000))
        {
            Created = true, Alive = true, Health = 211, MaxHealth = 900,
        });
        var deadBeforePhase = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Monster), EntityKind.Pet, UnitType.Monster,
            Team.Chaos, new Position2(5000, 5000))
        {
            Created = true, Alive = false, Health = 0, MaxHealth = 900,
        });
        var nexus = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Nexus && entity.Team == Team.Chaos);
        TestAssert.True(runtime.Combat.Kill(nexus));
        runtime.Combat.CombatTick(5.5, regenerate: false);
        TestAssert.True(beforePhase.NoRender, "losing-team minions latch no-render at 5.5s");
        TestAssert.True(deadBeforePhase.NoRender, "dead losing-team pets remain in the native minion set");
        TestAssert.False(winningTeamMinion.NoRender, "winner minions remain renderable");

        // A late object is picked up on the next ceremony tick. It is not
        // revealed by the state transition; the re-entry builder carries the
        // bit only when the observer later knows the object.
        var afterPhase = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(5000, 5000))
        {
            Created = true, Alive = true, Health = 173, MaxHealth = 900,
        });
        runtime.Combat.CombatTick(0.05, regenerate: false);
        TestAssert.True(afterPhase.NoRender, "late losing-team minion inherits the ceremony latch");

        var delta = runtime.Delivery.Services.BuildEntityStats(beforePhase, observer)
            .Single(packet => packet.Plaintext.Span.Length == 23 &&
                packet.Plaintext.Span[15] == 0x10).Plaintext.Span;
        TestAssert.Equal((byte)0x10, delta[15], "action state selects map1 property bit 4");
        TestAssert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(delta[15..19]),
            "action state refresh does not select HP");
        TestAssert.Equal(0x2007u, BinaryPrimitives.ReadUInt32LittleEndian(delta[19..23]),
            "latched no-render bit is included with the normal minion action state");

        beforePhase.Health = 111;
        var later = runtime.Delivery.Services.BuildEntityStats(beforePhase, observer);
        var laterDelta = later.Single(packet =>
            packet.Plaintext.Span.Length == 23 &&
            packet.Plaintext.Span[15] == 0x10 &&
            packet.EntityReferences.Any(reference => reference.NetId == beforePhase.NetId));
        TestAssert.Equal(0x2007u,
            BinaryPrimitives.ReadUInt32LittleEndian(laterDelta.Plaintext.Span[19..23]),
            "later minion stat refresh retains the full latched action state");
        TestAssert.Near(111, beforePhase.Health, 0.0,
            "the later refresh does not overwrite authoritative injured HP");

        // Exercise a mutable full MinionStats writer as well as the ordinary
        // re-entry path.  Nidalee traps are registered minion actors and the
        // native no-render bit must survive their effect refresh.
        var trap = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Minion), EntityKind.NidaleeTrap,
            UnitType.Minion, Team.Chaos, new Position2(5000, 5000))
        {
            Created = true, Alive = true, NoRender = true,
        });
        runtime.Session.Participants[0].Hero!.Position = new Position2(5000, 5000);
        runtime.Delivery.Flush();
        capture.Packets.Clear();
        runtime.Effects.SendStats(trap);
        runtime.Delivery.Flush();
        var trapStats = capture.Packets.Last(packet =>
            packet.Plaintext.Length == 111 &&
            packet.EntityReferences.Any(reference => reference.NetId == trap.NetId));
        TestAssert.Equal(0x2000u,
            BinaryPrimitives.ReadUInt32LittleEndian(trapStats.Plaintext.Span[35..39]),
            "a later trap full stats refresh preserves CharacterState no-render");
    }

    [Check("combat.end-game-orianna-ball-no-render-state")]
    public static void EndGameOriannaBallNoRenderSurvivesVisibilityRefresh()
    {
        foreach (var winner in new[] { Team.Order, Team.Chaos })
        {
            var runtime = CreateMatch(null, 2, winner);
            var capture = new Capture();
            var orderHero = runtime.Session.Participants.Single(p => p.Team == Team.Order).Hero!;
            var chaosHero = runtime.Session.Participants.Single(p => p.Team == Team.Chaos).Hero!;
            var observerTeam = winner;
            var observerHero = observerTeam == Team.Order ? orderHero : chaosHero;
            var observerParticipant = runtime.Session.Participants.Single(p => p.Team == observerTeam);
            observerHero.Position = new Position2(7000, 7000);
            var observer = new ReplicationObserver(observerParticipant.PlayerId, observerTeam,
                observerHero.NetId, capture, ready: true);
            runtime.Delivery.AddObserver(observer);

            orderHero.Position = observerHero.Position;
            chaosHero.Position = observerHero.Position;
            // 19 October 2026: the managed ball projection follows a script-spawned ball unit (a placemarker of the world), not Orianna's state.
            Entity Ball(uint id, Team team, Entity owner) => runtime.World.Register(new Entity(id, EntityKind.Monster, UnitType.Monster, team,
                observerHero.Position)
            {
                Name = "OriannaBall", ChampionName = "OriannaBall", OwnerNetId = owner.NetId, Placemarker = true, Protected = true,
                Created = false, Alive = true, Health = 1, MaxHealth = 1,
            });

            var winningBallId = runtime.World.Allocate(UnitType.Monster);
            var losingBallId = runtime.World.Allocate(UnitType.Monster);
            var winningTeam = winner;
            var losingTeam = winner == Team.Order ? Team.Chaos : Team.Order;
            var winningHero = winningTeam == Team.Order ? orderHero : chaosHero;
            var losingHero = losingTeam == Team.Order ? orderHero : chaosHero;
            Ball(winningBallId, winningTeam, winningHero);
            Ball(losingBallId, losingTeam, losingHero);
            var hiddenBallId = runtime.World.Allocate(UnitType.Monster);
            runtime.Delivery.Balls.Add(new BallProjection(
                hiddenBallId, losingHero.NetId, losingTeam, observerHero.Position));

            runtime.Effects.SendOriannaBall(new OriannaBallReplication(
                winningBallId, observerHero.Position, TeamMapping.ToNative(winningTeam),
                100, 10, 300, 30, 30, winningHero.NetId), false, 0);
            runtime.Effects.SendOriannaBall(new OriannaBallReplication(
                losingBallId, observerHero.Position, TeamMapping.ToNative(losingTeam),
                100, 10, 300, 30, 30, losingHero.NetId), false, 0);
            var nexus = runtime.World.Entities.Values.Single(entity =>
                entity.Kind == EntityKind.Nexus && entity.Team != winner);
            TestAssert.True(runtime.Combat.Kill(nexus), "the losing nexus starts the ceremony");
            runtime.Delivery.Flush();

            var initialLosingStats = capture.Packets.Last(packet =>
                packet.Plaintext.Length == 111 &&
                packet.EntityReferences.Any(reference => reference.NetId == losingBallId));
            TestAssert.Equal(0x4400u,
                BinaryPrimitives.ReadUInt32LittleEndian(initialLosingStats.Plaintext.Span[35..39]),
                "pre-ceremony losing Orianna ball is ghosted and forces particles without no-render");
            var hiddenBallIndex = runtime.Delivery.Balls.FindIndex(ball => ball.NetId == hiddenBallId);
            TestAssert.True(hiddenBallIndex >= 0, "hidden known ball is registered before the ceremony");
            var hiddenPoint = losingTeam == Team.Chaos
                ? new Position2(12000, 12000)
                : new Position2(1000, 1000);
            runtime.Delivery.Balls[hiddenBallIndex] =
                runtime.Delivery.Balls[hiddenBallIndex] with { Position = hiddenPoint };
            runtime.Delivery.Flush();
            TestAssert.True(observer.View.Known.Contains(hiddenBallId),
                "the hidden losing ball remains known after leaving vision");
            TestAssert.False(observer.View.Visible.Contains(hiddenBallId),
                "the hidden losing ball leaves visibility in the losing team's base");
            capture.Packets.Clear();

            runtime.Combat.CombatTick(5.49, regenerate: false);
            runtime.Delivery.Flush();
            TestAssert.False(capture.Packets.Any(packet =>
                packet.Plaintext.Length == 23 && packet.Plaintext.Span[15] == 0x10 &&
                packet.EntityReferences.Any(reference => reference.NetId == losingBallId)),
                "losing Orianna ball has no action delta before the ceremony boundary");

            capture.Packets.Clear();
            runtime.Combat.CombatTick(0.01, regenerate: false);
            runtime.Delivery.Flush();
            var losingBallDelta = capture.Packets.Single(packet =>
                packet.Plaintext.Length == 23 && packet.Plaintext.Span[15] == 0x10 &&
                packet.EntityReferences.Any(reference => reference.NetId == losingBallId));
            TestAssert.Equal(0x6400u,
                BinaryPrimitives.ReadUInt32LittleEndian(losingBallDelta.Plaintext.Span[19..23]),
                "losing Orianna ball receives no-render while retaining ghosted and particle state");
            TestAssert.False(capture.Packets.Any(packet =>
                packet.Plaintext.Length == 23 && packet.Plaintext.Span[15] == 0x10 &&
                packet.EntityReferences.Any(reference => reference.NetId == winningBallId)),
                "winning Orianna ball remains renderable");
            TestAssert.False(capture.Packets.Any(packet =>
                packet.Plaintext.Length == 23 && packet.Plaintext.Span[15] == 0x10 &&
                packet.EntityReferences.Any(reference => reference.NetId == hiddenBallId)),
                "a known hidden losing ball receives no transition delta while out of sight");

            capture.Packets.Clear();
            runtime.Effects.SendOriannaBall(new OriannaBallReplication(
                losingBallId, observerHero.Position, TeamMapping.ToNative(losingTeam),
                100, 10, 300, 30, 30, losingHero.NetId), false, 0);
            runtime.Delivery.Flush();
            var losingBallStats = capture.Packets.Last(packet =>
                packet.Plaintext.Length == 111 &&
                packet.EntityReferences.Any(reference => reference.NetId == losingBallId));
            TestAssert.Equal(0x6400u,
                BinaryPrimitives.ReadUInt32LittleEndian(losingBallStats.Plaintext.Span[35..39]),
                "later Orianna ball full stats preserve no-render and particle state");

            capture.Packets.Clear();
            runtime.Delivery.Balls[hiddenBallIndex] =
                runtime.Delivery.Balls[hiddenBallIndex] with { Position = observerHero.Position };
            runtime.Delivery.Flush();
            var hiddenBallDelta = capture.Packets.Single(packet =>
                packet.Plaintext.Length == 23 && packet.Plaintext.Span[15] == 0x10 &&
                packet.EntityReferences.Any(reference => reference.NetId == hiddenBallId));
            TestAssert.Equal(0x6400u,
                BinaryPrimitives.ReadUInt32LittleEndian(hiddenBallDelta.Plaintext.Span[19..23]),
                "re-entry reveals latched no-render and particle state for a known ball");

            var lateBallId = runtime.World.Allocate(UnitType.Monster);
            Ball(lateBallId, losingTeam, losingHero);
            capture.Packets.Clear();
            runtime.Effects.SendOriannaBall(new OriannaBallReplication(
                lateBallId, observerHero.Position, TeamMapping.ToNative(losingTeam),
                100, 10, 300, 30, 30, losingHero.NetId), false, 0);
            runtime.Delivery.Flush();
            var lateBallStats = capture.Packets.Last(packet =>
                packet.Plaintext.Length == 111 &&
                packet.EntityReferences.Any(reference => reference.NetId == lateBallId));
            TestAssert.Equal(0x6400u,
                BinaryPrimitives.ReadUInt32LittleEndian(lateBallStats.Plaintext.Span[35..39]),
                "late losing Orianna ball inherits the ceremony no-render and particle state");
        }
    }

    [Check("session.finished-match-inputs")]
    public static void FinishedMatchRejectsGameplayAndDevelopmentCommands()
    {
        var runtime = CreateMatch(null, 2, Team.Order);
        var session = runtime.Session;
        var hero = session.Participants[0].Hero!;
        var movement = new byte[22]; movement[0] = 0x75; movement[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(movement.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(6), (float)hero.Position.X + 100);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(14), (float)hero.Position.Z);
        TestAssert.True(session.DispatchGameplay(1, 1, movement, _ => true), "live movement reaches gameplay");
        var open = new byte[5]; open[0] = 0x60;
        TestAssert.True(session.DispatchShop(Input(open)), "live fountain shop can open");
        var cheat = new byte[16]; cheat[0] = 0xB6; cheat[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(cheat.AsSpan(8), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(cheat.AsSpan(12), 25);
        TestAssert.True(session.DispatchCheat(Input(cheat)), "live development command works");
        var buy = new byte[9]; buy[0] = 0x87;
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 1001);
        var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        runtime.Combat.Kill(nexus);
        var gold = hero.Gold;
        var level = hero.Level;
        var speed = runtime.SimulationSpeed;
        foreach (var opcode in new byte[] { 0x75, 0x3E, 0xA2, EmotePackets.ClientOpcode })
        {
            var payload = new byte[34]; payload[0] = opcode;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
            var invoked = false;
            TestAssert.False(session.DispatchGameplay(1, 1, payload, _ => invoked = true));
            TestAssert.False(invoked, "finished gameplay never reaches a mutation handler");
        }
        TestAssert.False(session.DispatchShop(Input(open)));
        TestAssert.False(session.DispatchShop(Input(buy)));
        TestAssert.False(session.DispatchCheat(Input(cheat)));
        foreach (var command in new[] { "/gold 50", "/level 3", "/cooldowns on", "/god on", "/speed 2" })
        {
            var reply = Chat(command);
            TestAssert.Equal(1, reply.Count);
            TestAssert.Equal("The match has ended.", Encoding.UTF8.GetString(reply[0].Packet.Plaintext.Span[12..^1]));
        }
        TestAssert.Equal(gold, hero.Gold);
        TestAssert.Equal(level, hero.Level);
        TestAssert.Equal(speed, runtime.SimulationSpeed);
        TestAssert.False(hero.CooldownsDisabled);
        TestAssert.True(hero.Inventory.All(item => item is null));
        TestAssert.Equal(2, Chat("gg").Count, "ordinary ceremony chat still reaches both players");

        SessionInput Input(byte[] payload) => new(1, 1, payload, payload, false);
        IReadOnlyList<SessionOutboundPacket> Chat(string text)
        {
            var packet = ChatPackets.Envelope(0, false, Encoding.UTF8.GetBytes(text));
            return session.Receive(1, ChatPackets.Channel, packet.ToWireBytes(runtime.Cipher));
        }
    }

    [Check("session.terminal-chat-gate")]
    public static void TerminalChatWaitsForEndGameDispatch()
    {
        foreach (var ending in new[] { "nexus", "surrender" })
        {
            var runtime = CreateMatch(null, 2, Team.Order);
            var session = runtime.Session;

            TestAssert.Equal(2, SendChat(runtime, 1, "before completion").Count);
            var forgedSender = SendChat(runtime, 1, "assigned owner", senderClientId: 99);
            TestAssert.Equal(2, forgedSender.Count, "valid chat still relays before the terminal phase");
            TestAssert.Equal(new[] { 0u, 0u }, forgedSender.Select(output =>
                BinaryPrimitives.ReadUInt32LittleEndian(output.Packet.Plaintext.Span[..4])).ToArray(),
                "chat ownership comes from the authenticated connection");
            TestAssert.Equal(0, SendChat(runtime, 999, "unknown connection").Count,
                "an unknown connection cannot relay chat");
            TestAssert.Equal(0, session.Receive(1, ChatPackets.Channel, new byte[13]).Count,
                "an invalid chat envelope is rejected");

            if (ending == "nexus")
            {
                var nexus = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
                TestAssert.True(runtime.Combat.Kill(nexus), "Nexus completion starts the ceremony");
            }
            else
            {
                TestAssert.True(runtime.Combat.Surrender(Team.Chaos),
                    "surrender completion starts the ceremony");
            }

            // GameOver is immediate, so the existing command refusal remains
            // visible while ordinary chat follows the shipped 8.5s gate.
            TestAssert.Equal(1, SendChat(runtime, 1, "/gold 50").Count);
            TestAssert.Equal(2, SendChat(runtime, 1, "during ceremony").Count);
            runtime.Combat.CombatTick(8.49, regenerate: false);
            TestAssert.False(runtime.Combat.EndGameSent);
            TestAssert.Equal(2, SendChat(runtime, 1, "last message").Count,
                "ordinary chat remains open before EndGame dispatch");

            runtime.Combat.CombatTick(0.01, regenerate: false);
            TestAssert.True(runtime.Combat.EndGameSent);
            TestAssert.Equal(0, SendChat(runtime, 1, "at EndGame").Count,
                "chat closes at the exact EndGame boundary");
            TestAssert.Equal(0, SendChat(runtime, 1, "/gold 50").Count,
                "the terminal receive gate closes slash commands with chat");
            runtime.Combat.CombatTick(0.01, regenerate: false);
            TestAssert.Equal(0, SendChat(runtime, 1, "after EndGame").Count,
                "chat stays closed after the boundary");

            var seat = session.Participants[0];
            TestAssert.True(session.Disconnect(seat.ConnectionId!.Value));
            TestAssert.True(session.AuthenticateReconnect(101,
                session.BuildKeyCheck(seat.PlayerId)) is not null);
            session.Receive(101, 1, new byte[] { 0xC5, 0, 0, 0, 0 });
            seat.Loaded = true;
            runtime.World.Clock.SetVirtual(runtime.World.Clock.Monotonic() + 3.0);
            session.RunSchedulers();
            TestAssert.Equal(101L, seat.ConnectionId);
            TestAssert.Equal(0, SendChat(runtime, 101, "reconnected message").Count,
                "a reconnected seat remains terminally chat-gated");
        }

        var coarse = CreateMatch(null, 2, Team.Order);
        var coarseNexus = coarse.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        TestAssert.True(coarse.Combat.Kill(coarseNexus));
        coarse.Combat.CombatTick(20, regenerate: false);
        TestAssert.True(coarse.Combat.EndGameSent, "a coarse tick crosses the final phase once");
        TestAssert.Equal(0, SendChat(coarse, 1, "coarse after EndGame").Count);
    }

    private static IReadOnlyList<SessionOutboundPacket> SendChat(MatchRuntime runtime,
        long connectionId, string text, uint senderClientId = 0)
    {
        var packet = ChatPackets.Envelope(senderClientId, false,
            Encoding.UTF8.GetBytes(text));
        return runtime.Session.Receive(connectionId, ChatPackets.Channel,
            packet.ToWireBytes(runtime.Cipher));
    }

    [Check("combat.shipped-end-game-timeline")]
    public static void CeremonyUsesShippedTimingAndIndividualInputLocks()
    {
        foreach (var winner in new[] { Team.Order, Team.Chaos })
        foreach (var coarse in new[] { false, true })
        {
            var runtime = CreateMatch(null, 2, winner);
            var capture = new Capture();
            var player = runtime.Session.Participants.Single(seat => seat.Team == winner);
            runtime.Delivery.AddObserver(new ReplicationObserver(player.PlayerId, winner,
                player.Hero!.NetId, capture, ready: true));
            if (coarse)
            {
                player.Hero.Alive = false;
                player.Hero.Health = 0;
                player.Hero.RespawnAt = 1e6;
            }
            var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team != winner);
            runtime.Combat.Kill(nexus);
            runtime.Delivery.Flush();
            var locks = Packets(0x89);
            // The order is EndOfGameCeremony's own (EndOfGame.txt proto 0.0 pc 0046-0073):
            // camera locking, camera movement, abilities, summoner spells, movement, shop, minimap.
            TestAssert.Equal(new uint[] { 1, 128, 4, 8, 2, 16, 64 }, locks.Select(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..])).ToArray(),
                "each non-chat input flag invokes its native widget callback, in the script's order");
            foreach (var packet in locks)
            {
                TestAssert.Equal(10, packet.Plaintext.Length);
                TestAssert.Equal((byte)1, packet.Plaintext.Span[9]);
                TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
            }
            foreach (var camera in Packets(0x27))
                TestAssert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(camera.Plaintext.Span[30..]));
            TestAssert.Equal(1, Packets(0x27).Length, "a fresh observer receives the pan for its known own hero");
            TestAssert.Equal(player.Hero.NetId,
                BinaryPrimitives.ReadUInt32LittleEndian(Packets(0x27).Single().Plaintext.Span[1..]));
            var greyscale = Packets(0xB2).Single().Plaintext.Span;
            TestAssert.Equal(6, greyscale.Length);
            TestAssert.Equal(player.Hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(greyscale[1..]));
            TestAssert.Equal((byte)0, greyscale[5], "dead and living observers see the ceremony in color");
            TestAssert.Equal(0, Packets(0x62).Length);
            TestAssert.Equal(0, Packets(0xD4).Length);
            TestAssert.Equal(0, Packets(0x77).Length, "Nexus skin stays intact during the camera pan");
            if (coarse)
            {
                runtime.Combat.CombatTick(20);
                runtime.Delivery.Flush();
            }
            else
            {
                runtime.Tick(0.99);
                TestAssert.Equal(0, Packets(0x62).Length, "sound fade waits one second");
                runtime.Tick(0.02);
                TestAssert.Equal(1, Packets(0x62).Length);
                runtime.Tick(2.48);
                TestAssert.Equal(0, Packets(0xD4).Length, "minion fade waits for the 3.5-second destruction phase");
                TestAssert.Equal(0, Packets(0x77).Length);
                runtime.Tick(0.02);
                TestAssert.Equal(1, Packets(0xD4).Length);
                TestAssert.Equal(1, Packets(0x77).Length);
                runtime.Tick(4.98);
                TestAssert.Equal(0, Packets(0xCE).Length, "finish waits five seconds after destruction");
                TestAssert.Equal(7, Packets(0x89).Length, "chat remains unlocked during the ceremony");
                runtime.Tick(0.02);
            }
            var sound = Packets(0x62).Single().Plaintext.Span;
            TestAssert.Equal(9, sound.Length);
            TestAssert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(sound[5..]));
            var fade = Packets(0xD4).Single().Plaintext.Span;
            TestAssert.Equal(14, fade.Length);
            var losingWireTeam = winner == Team.Order ? (byte)200 : (byte)100;
            TestAssert.Equal(losingWireTeam, fade[5], "native team 100/200, not the server's 0/1 enum");
            TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(fade[6..]));
            TestAssert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(fade[10..]));
            var destroyed = Packets(0x77).Single();
            TestAssert.Equal(10, destroyed.Plaintext.Length);
            TestAssert.Equal(losingWireTeam, destroyed.Plaintext.Span[5]);
            TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(destroyed.Plaintext.Span[6..]));
            TestAssert.True(capture.Packets.IndexOf(destroyed) < capture.Packets.IndexOf(Packets(0xD4).Single()),
                "the destruction phase switches HQ skin before fading minions");
            var chatLock = Packets(0x89).Last();
            TestAssert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(chatLock.Plaintext.Span[5..]));
            TestAssert.Equal(1, Packets(0xCE).Length);
            TestAssert.True(capture.Packets.IndexOf(chatLock) < capture.Packets.IndexOf(Packets(0xCE).Single()),
                "chat locks before final transition");
            runtime.Combat.CombatTick(20);
            runtime.Delivery.Flush();
            TestAssert.Equal(1, Packets(0x62).Length);
            TestAssert.Equal(1, Packets(0xD4).Length);
            TestAssert.Equal(1, Packets(0x77).Length);
            TestAssert.Equal(8, Packets(0x89).Length);
            TestAssert.Equal(1, Packets(0xCE).Length, "finished phases cannot repeat");

            PlaintextPacket[] Packets(byte opcode) => capture.Packets
                .Where(packet => packet.Plaintext.Span[0] == opcode).ToArray();
        }
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
