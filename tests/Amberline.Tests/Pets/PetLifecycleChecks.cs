using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>Original summon expiry and Yorick revival lifecycle regressions.</summary>
public static class PetLifecycleChecks
{
    [Check("spells-yorick-b5-zombie-early-force-death")]
    public static void YorickZombieHonorsBothEarlyDeathCallbacks()
    {
        foreach (var periodic in new[] { false, true })
        {
            var runtime = StartedProduction("Yorick", "Orianna");
            var caster = RegisterHero(runtime, 0x93000101, Team.Order, "Yorick", new(7000, 7000));
            var victim = RegisterHero(runtime, 0x93000102, Team.Order, "Annie", new(7200, 7000));
            var killer = RegisterHero(runtime, 0x93000103, Team.Chaos, "Orianna", new(7300, 7000));
            MarkWithOmenOfDeath(runtime, victim, caster);
            runtime.Combat.ApplyHit(killer.NetId, victim.NetId, 10000, DamageType.True, DamageSourceKind.Spell);
            var deadline = victim.RespawnAt;
            var gold = killer.Gold; var deaths = victim.ChampionDeaths;
            runtime.Tick(1);
            TestAssert.True(victim.YorickRevival is not null && victim.Alive);
            var capture = new PacketCapture();
            runtime.Delivery.AddObserver(new ReplicationObserver(0x93000110, Team.Order, victim.NetId, capture));
            runtime.Delivery.Flush(); capture.Packets.Clear();
            if (periodic)
            {
                victim.Health = victim.MaxHealth * .1 + 1;
                // YorickRAZombie.BuffOnUpdateActions: BBExecutePeriodically every 0.95 s of the world clock (buff ticks alone do not move it), and the
                // run that finds health <= 10 percent + 1 removes the buff. The buff has lived 10 - ExpiresAt seconds; tick to 0.94 and then past 0.95.
                var lived = 10.0 - victim.Buffs.Single(buff => buff.Name == "YorickRAZombie").ExpiresAt;
                runtime.Tick(.94 - lived);
                TestAssert.True(victim.Alive, "no early scan before .95 seconds");
                TestAssert.Near(victim.MaxHealth * .1 + 1, victim.Health, 0, "scan does not decay health");
                runtime.Tick(.06);
            }
            else
            {
                victim.Health = 200; victim.Armor = 900;
                runtime.SpellBuffs.Add(victim, "TestZombieShield", BuffType.Internal, 10,
                    victim.NetId, new BuffPayload { DamageShield = 1000 }, canMitigateDuration: false);
                runtime.Combat.ApplyHit(killer.NetId, victim.NetId, 200, DamageType.Physical, DamageSourceKind.Spell);
            }
            runtime.Delivery.Flush();
            TestAssert.False(victim.Alive, "YOR-02 callback force-ends temporary life");
            TestAssert.True(victim.YorickRevival is null);
            TestAssert.Near(deadline, victim.RespawnAt, 0, "original respawn deadline survives");
            TestAssert.Near(gold, killer.Gold, 0, "no second kill reward");
            TestAssert.Equal(deaths, victim.ChampionDeaths, "no second ordinary death");
            TestAssert.Equal(1, capture.CountOpcode(0x1E, victim.NetId), "one force-dead packet");
            runtime.World.Clock.UseReal();
        }
    }

    [Check("pets.clone.attack-animations")]
    public static void ClonesKeepTheCopiedChampionsAttackAnimations()
    {
        var data = GameData.Default;
        var runtime = StartedProduction("Yorick", "Shaco");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { CloneKind.ShacoClone, CloneKind.YorickRevenant })
        {
            var owner = runtime.World.Entities.Values.First(e => e.Kind == EntityKind.Hero && e.Team == team);
            var sourceName = kind == CloneKind.ShacoClone ? "Shaco" : "LeeSin";
            var source = RegisterHero(runtime, runtime.World.Allocate(UnitType.Hero), team, sourceName,
                new(7000, 7000));
            source.AttackSpeedMod = 1;
            HeroAttackTimings.Apply(source, data);
            var clone = runtime.Clones.Spawn(owner, source, new(7100, 7000), kind,
                500, kind == CloneKind.ShacoClone ? "HallucinateFull" : "YorickReviveAlly",
                10, sourceName);
            TestAssert.Equal(kind == CloneKind.ShacoClone ? 2 : 4, clone.BasicAttackAnimationProbabilities.Count);
            TestAssert.True(clone.BasicAttackAnimationProbabilities.SequenceEqual(
                data.Champion(sourceName).BasicAttackAnimationProbabilities), "copy source champion, not owner weights");
            source.BasicAttackAnimationProbabilities = [];
            HeroAttackTimings.ApplyRefresh(source, data);
            TestAssert.True(source.BasicAttackAnimationProbabilities.SequenceEqual(clone.BasicAttackAnimationProbabilities),
                "refresh restores loaded weights without changing the clone snapshot");
        }
    }

    [Check("pets.shaco.clock-domain-lifecycle")]
    public static void ShacoTimersUseCombatClock()
    {
        // W33: the box's timers are the script's. The match has a scripted Shaco, and a box is the unit factory's box plus the JackInTheBox buff
        // the placement adds (ShacoScriptSupport.SpawnBox), with the Unix world clock far from the combat clock.
        var runtime = StartedProduction("Shaco", "Orianna");
        runtime.World.Clock.SetVirtual(1_700_000_000.0);
        var owner = runtime.Session.Participants[0].Hero!;
        var box = ShacoScriptSupport.SpawnBox(runtime, owner, new Position2(7000.0, 7000.0));

        TestAssert.Near(0.0, runtime.Combat.CurrentTime, 1e-9,
            "the combat clock starts independently of the Unix world clock");
        TestAssert.Near(90.0, box.ExpiresAt, 1e-9,
            "box expiry is relative to combat time, not Unix wall time");
        TestAssert.Near(0.0, ShacoScriptSupport.Born(runtime, box), 1e-9,
            "the box buff's start time (the 2 s arming and the 0.5 s shot grid count from it) uses the combat clock");
        TestAssert.True(box.Buffs.Any(buff => buff.Name == "JackInTheBox"), "the script's buff drives the box");

        // The production simulation driver owns the box phase.  A single
        // large diagnostic step is sufficient to exercise the expiry gate.
        runtime.Dependencies.Simulation.Tick(90.0);
        TestAssert.False(box.Alive, "box expires from its combat-clock deadline");
    }

    [Check("pets.shaco.hallucination-expiry")]
    public static void ShacoHallucinationExpiryAndDeathExplodeOnce()
    {
        // W33: HallucinateFull.OnBuffDeactivate is the explosion: a spell-shield break and DamageAmount (300 / 450 / 600 of the R rank) plus the
        // attacker's full AP (SPELLAOE, SpellDamageRatio 1, read when it goes off) in 300 units, then 10000 damage to the clone itself.
        foreach (var rank in new[] { 1, 3 })
        foreach (var killBeforeExpiry in new[] { false, true })
        {
            var runtime = StartedProduction("Shaco", "Orianna");
            runtime.World.Clock.SetVirtual(1_700_000_000.0);
            var owner = runtime.Session.Participants[0].Hero!;
            owner.Position = new Position2(6000.0, 6000.0);
            owner.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            var clone = ShacoScriptSupport.SpawnClone(runtime, owner, new Position2(7000.0, 7000.0), rank);
            var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
                Team.Chaos, new Position2(7300.0, 7000.0))
            {
                Name = "Target", Health = 2000.0, MaxHealth = 2000.0,
                SpellBlock = 100.0, Created = true,
            };
            runtime.World.Register(target);

            runtime.Buffs.TickBuffs(clone, 17.5);
            TestAssert.Near(2000.0, target.Health, 1e-9,
                $"rank {rank} explosion must not fire before expiry");
            owner.Inventory = [new InventorySlot(1058), new InventorySlot(1058), null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            TestAssert.Near(160.0, owner.AbilityPower, 1e-9, "the owner has 160 AP when the clone goes off");
            if (killBeforeExpiry) runtime.Combat.Kill(clone, 0);
            else runtime.Buffs.TickBuffs(clone, 0.5);

            var expectedRaw = (rank == 1 ? 300.0 : 600.0) + 160.0;
            var expected = expectedRaw * 100.0 / 200.0;
            TestAssert.Near(2000.0 - expected, target.Health, 1e-8,
                $"rank {rank} {(killBeforeExpiry ? "death" : "expiry")} explosion damage");
            TestAssert.False(clone.Alive, "hallucination clone is dead after lifecycle end");
            TestAssert.False(clone.Buffs.Any(buff => buff.Name == "HallucinateFull"),
                "hallucination controller is removed exactly once");

            var after = target.Health;
            runtime.Combat.Kill(clone, 0);
            runtime.Buffs.TickBuffs(clone, 20.0);
            TestAssert.Near(after, target.Health, 1e-9,
                "hallucination death/expiry re-entry must not explode twice");
        }
    }

    [Check("pets.clone-route-cache-leaves-with-the-clone")]
    public static void CloneRouteCacheLeavesWithEveryCloneFamily()
    {
        // Pet.lua drives every family, so the route cache is the script's own path requests and a dead clone drops it with the unit (the
        // hand-written Shaco module did that at its own expiry; the script path has no such module). Every family the registry builds:
        // Shaco's clone, Yorick's revenant, LeBlanc's image, Mordekaiser's ghost and Wukong's decoy. The decoy stands still not because
        // anything refuses to drive it but because its own shipped buff holds it (MonkeyKingDecoyClone's OnBuffActivate is
        // BBSetStatus SetCanMove false), which is the freeze Pet.lua's scans cannot walk through.
        foreach (var kind in new[] { CloneKind.ShacoClone, CloneKind.YorickRevenant, CloneKind.LeblancImage, CloneKind.MordekaiserGhost,
                     CloneKind.MonkeyKingDecoy })
        {
            var runtime = StartedProduction("Shaco", "Orianna");
            runtime.World.Clock.SetVirtual(1_700_000_000.0);
            var owner = runtime.Session.Participants[0].Hero!;
            owner.Position = new Position2(6000.0, 6000.0);
            var clone = runtime.Clones.Spawn(owner, owner, new Position2(6600.0, 6000.0), kind, 500.0,
                CloneFamilies.ControllerBuff(kind) ?? "MonkeyKingDecoyDummy", 30.0, "Pet");
            if (kind == CloneKind.MonkeyKingDecoy)
                runtime.SpellBuffs.Add(clone, "MonkeyKingDecoyClone", BuffType.Internal, 30.0, owner.NetId,
                    new BuffPayload { MovementDisabled = true });
            else
                runtime.SpellBuffs.Add(clone, CloneFamilies.ControllerBuff(kind)!, BuffType.Internal, 30.0, owner.NetId, new BuffPayload());
            runtime.Pets.Tick(runtime.World.Clock.Now(), 0.15);
            if (kind == CloneKind.MonkeyKingDecoy)
                TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(clone.NetId), $"{kind}: its own buff holds it, so Pet.lua never routes it");
            else
                TestAssert.True(runtime.Pets.LastDestinations.ContainsKey(clone.NetId), $"{kind}: Pet.lua returns it to its owner, so it has a route cache");

            runtime.Combat.Kill(clone, 0);
            TestAssert.False(clone.Alive, $"{kind}: the clone is dead");
            TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(clone.NetId), $"{kind}: the route cache of a dead clone is dropped");
        }
    }

    [Check("pets.yorick.revival-lifecycle")]
    public static void YorickRevivalKeepsRewardsAndCancelsOwnedWork()
    {
        foreach (var killEarly in new[] { false, true })
        {
            var runtime = StartedProduction("Yorick", "Orianna");
            runtime.World.Clock.SetVirtual(0.0);
            var caster = RegisterHero(runtime, 0x93000001, Team.Order, "Yorick",
                new Position2(7000.0, 7000.0));
            var victim = RegisterHero(runtime, 0x93000002, Team.Order, "Annie",
                new Position2(7300.0, 7000.0), hp: 1000.0, mana: 300.0);
            var killer = RegisterHero(runtime, 0x93000003, Team.Chaos, "Orianna",
                new Position2(7400.0, 7000.0));
            victim.GoldReward = 300.0;
            victim.PendingCast = new PendingCastState(
                new SpellCastRequest(0, false, new Position3(0, 0, 0),
                    new Position3(0, 0, 0), 0), 4.0);
            // A real Recall on the victim, through the production summoner module (batch 179 review); the revival must cancel it.
            ChampionLiveRegressionChecks.StartRecallWindup(runtime, victim);
            MarkWithOmenOfDeath(runtime, victim, caster);

            // Use the production delivery path so the lifecycle assertions
            // cover the same death/force-dead/respawn packets as the source
            // server-tick scenario.  The observer is installed after the
            // session bootstrap and its initial stream is discarded.
            var packetCapture = new PacketCapture();
            runtime.Delivery.AddObserver(new ReplicationObserver(
                0x93000010, Team.Order, victim.NetId, packetCapture));
            runtime.Delivery.Flush();
            packetCapture.Packets.Clear();

            var goldBefore = killer.Gold;
            var initialPending = victim.PendingCast;
            var initialDeadline = runtime.Combat.CurrentTime + 25.0; // Map1 level-6 death time.
            TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                10_000.0, DamageType.True, DamageSourceKind.Basic),
                "marked victim must enter the ordinary death/reward path through ApplyHit");
            TestAssert.Near(initialDeadline, victim.RespawnAt, 1e-9,
                "ordinary lethal hit preserves the initial level-6 respawn deadline");
            TestAssert.True(victim.Alive, "Yorick revival starts after ordinary death");
            TestAssert.Near(1.0, victim.Health, 1e-9, "revival starts at one health");
            TestAssert.True(victim.YorickRevival is not null, "revival state is independent");
            // YorickRADelay.luaobj: SetCanAttack, SetCanCast and SetCanMove false (three status bits, CharacterState), not one hard-control flag.
            var delayed = CharacterState.Of(victim);
            TestAssert.True(!delayed.CanAttack && !delayed.CanCast && !delayed.CanMove,
                "revival delay keeps the victim from attacking, casting and moving");
            TestAssert.Equal<PendingCastState?>(null, victim.PendingCast,
                "revival cancels the victim's pending cast");
            TestAssert.True(initialPending is not null,
                "the pending-cast cancellation assertion has a real source value");
            TestAssert.Near(0.0, runtime.Spells.RecallRemaining(victim), 1e-9,
                "revival cancels the owner's recall");
            TestAssert.True(victim.SummonerCooldowns.All(cooldown => cooldown >= 4.0),
                "revival blocks summoner casts during the temporary life");
            TestAssert.Near(goldBefore + 400.0, killer.Gold, 1e-9,
                "temporary revival keeps the one ordinary champion reward");

            runtime.Delivery.Flush();
            TestAssert.Equal(1, packetCapture.CountOpcode(0x61, victim.NetId),
                "ordinary lethal hit delivers exactly one hero death packet");

            // During the one-second delay, the marked hero is alive but
            // invulnerable. This is a separate source assertion from the
            // eventual temporary-life death.
            var delayHealth = victim.Health;
            TestAssert.False(runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                10_000.0, DamageType.True, DamageSourceKind.Basic),
                "revival delay is invulnerable");
            TestAssert.Near(delayHealth, victim.Health, 1e-9,
                "revival delay preserves health");

            runtime.Tick(1.0);
            TestAssert.True(victim.YorickRevival is not null,
                "revival enters zombie state after the delay");
            TestAssert.Near(victim.MaxHealth, victim.Health, 1e-9,
                "zombie state restores health");
            var released = CharacterState.Of(victim);
            TestAssert.True(released.CanAttack && released.CanCast && released.CanMove,
                "zombie state releases the delay action lock");
            TestAssert.Near(0.0, runtime.Combat.HealingReceivedMultiplier(victim), 1e-9,
                "zombie state blocks incoming healing");
            if (killEarly)
            {
                // The source server tick kills the zombie at t=2, then drives
                // through t=10. The temporary death must
                // not award a second kill or replace that deadline.
                runtime.Tick(1.0);
                TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                    100_000.0, DamageType.True, DamageSourceKind.Basic),
                    "zombie can be killed through the real lethal-hit path");
                TestAssert.False(victim.Alive, "early zombie death ends the temporary life");
                TestAssert.Equal<YorickRevivalState?>(null, victim.YorickRevival,
                    "early temporary death clears revival state");
                TestAssert.Near(initialDeadline, victim.RespawnAt, 1e-9,
                    "early temporary death preserves the original deadline");
                runtime.Tick(8.0 + (1.0 / 30.0));
            }
            else
            {
                runtime.Tick(9.0);
                TestAssert.True(victim.Alive && victim.YorickRevival is not null,
                    "at t=10 the zombie is still alive with revival state");
                TestAssert.Near(initialDeadline, victim.RespawnAt, 1e-9,
                    "zombie transition retains the original respawn deadline");
                runtime.Tick(1.0 + (1.0 / 30.0));
            }

            TestAssert.False(victim.Alive, "temporary life ends before the level-6 respawn deadline");
            TestAssert.Near(initialDeadline, victim.RespawnAt, 1e-9,
                "temporary death retains the original level-6 deadline");
            runtime.Tick(initialDeadline - runtime.Combat.CurrentTime + 1.0 / 30.0);
            TestAssert.True(victim.Alive, "victim eventually respawns after revival");
            TestAssert.Equal<YorickRevivalState?>(null, victim.YorickRevival,
                "revival state is cleared before normal respawn");
            TestAssert.Near(0.0, victim.RespawnAt, 1e-9,
                "normal respawn clears the preserved deadline");
            TestAssert.Near(victim.MaxHealth, victim.Health, 1e-9,
                "normal respawn restores full health");
            TestAssert.Near(victim.MaxMana, victim.Mana, 1e-9,
                "normal respawn restores full mana");
            TestAssert.True(runtime.Combat.CurrentTime < 90.0,
                "revival scenario finishes before ambient income starts");
            TestAssert.Near(goldBefore + 400.0,
                killer.Gold, 1e-8, "gold reward is paid exactly once");

            TestAssert.Equal(1, packetCapture.CountOpcode(0x61, victim.NetId),
                "repeated ticks do not deliver a second death packet");
            TestAssert.Equal(1, packetCapture.CountOpcode(0x1E, victim.NetId),
                "repeated ticks do not deliver a second force-dead packet");
            TestAssert.Equal(1, packetCapture.CountOpcode(0x4D, victim.NetId),
                "normal respawn delivers exactly one reincarnate packet");
        }
    }

    /// <summary>
    /// The marker <c>YorickReviveAlly.TargetExecute</c> adds to its target (10 s, REPLACE_EXISTING), through the script runtime of the match's Yorick
    /// (phase 3, 19 October 2026): the zombie life is the marker's <c>BuffOnDeath</c> and <c>BuffOnZombie</c>, so a buff the plain buff service adds
    /// would not run them. The match must hold a Yorick, which is what binds his module.
    /// </summary>
    private static void MarkWithOmenOfDeath(MatchRuntime runtime, Entity target, Entity caster)
    {
        var module = runtime.Dependencies.ChampionDispatcher.FindModule("Yorick") as BbChampionModule
            ?? throw new InvalidOperationException("the match has no script-driven Yorick");
        TestAssert.True(module.Buffs!.Add(new BbBuffRequest(target, caster, "YorickReviveAllySelf", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_CombatEnchancer, 1, 10.0, 0.0, false, new BbTable())) is not null, "the marker is added");
    }

    private sealed class PacketCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();

        public void Send(PlaintextPacket packet) => Packets.Add(packet);

        public int CountOpcode(byte opcode, uint netId)
            => Packets.Count(packet => packet.Plaintext.Span.Length >= 5 &&
                packet.Plaintext.Span[0] == opcode &&
                BitConverter.ToUInt32(packet.Plaintext.Span.Slice(1, 4)) == netId);
    }

    private static MatchRuntime StartedProduction(string orderChampion, string chaosChampion)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion, chaosChampion, solo: false, readySettleSeconds: 0.0,
            random: new FixedRandom(0.5));
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1));
        runtime.Session.Authenticate(2, runtime.Session.BuildKeyCheck(2));
        runtime.Session.Spawn(1);
        runtime.Session.Spawn(2);
        runtime.Session.MarkReady(1);
        runtime.Session.MarkReady(2);
        TestAssert.True(runtime.Session.Start(), "production lifecycle session starts");
        return runtime;
    }

    private static Entity RegisterHero(MatchRuntime runtime, uint netId, Team team,
        string champion, Position2 position, double abilityPower = 0.0,
        double hp = 1000.0, double mana = 300.0)
    {
        var hero = new Entity(netId, EntityKind.Hero, UnitType.Hero, team, position)
        {
            ChampionName = champion, Name = champion, Health = hp, MaxHealth = hp,
            Mana = mana, MaxMana = mana, AbilityPower = abilityPower,
            Created = true, Visible = true, Level = 6,
            // Synthetic actors receive only the scenario's scripted attacks.
            HeroAutoAcquire = false,
            SpellRanks = [1, 1, 1, 1],
        };
        runtime.World.Register(hero);
        return hero;
    }

    private sealed class FixedRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
