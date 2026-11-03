using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Items;

/// <summary>Production neutral-death coverage for Baron's resource domain.</summary>
public static class BaronBuffLiveChecks
{
    private const int MpRegenOffset = 91;

    [Check("items-live-baron-removed-on-death-before-expiry")]
    public static void BaronEndsAtDeath()
    {
        var fixture = StartProduction();
        using var runtime = fixture.Runtime;
        var hero = fixture.Hero(0);
        runtime.Combat.AwardHeroProgress(hero,
            experience: ProgressionRules.ExperienceThreshold(18) - hero.Experience);
        var baseline = runtime.HeroStats.Calculate(hero);
        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(9001, hero.Team,
            hero.NetId, capture, ready: true));
        TestAssert.True(runtime.Combat.Kill(NewReplacementWorm(runtime), hero.NetId));
        var buff = hero.Buffs.Single(buff => buff.Name == "ExaltedWithBaronNashor");
        // Batch 174 part 2: the shipped Exalted draws nashor_rune_buf.troy from its own OnBuffActivate (BBSpellEffectCreate into
        // InstanceVars.buffParticle) and takes it back in OnBuffDeactivate, so Payload.FxNetId stays 0 (BuffEffectService.EnsureBuffFx
        // leaves a script-drawn particle alone). The id the death's 0x3D kill must name is the script's stored one, read the way the
        // BlueBuffLiveChecks crest reads its own.
        var instance = runtime.Spells.UnitScripts!.Buffs!.InstanceOf(buff)
            ?? throw new InvalidOperationException("the Baron buff runs as a live script instance on the hero");
        var effect = (uint)(double)instance.InstanceVars["buffParticle"]!;
        TestAssert.True(effect != 0 && buff.ExpiresAt > 0);
        TestAssert.Equal(0u, buff.Payload.FxNetId, "the script's own particle is the only Baron visual");
        TestAssert.True(runtime.HeroStats.Calculate(hero).AttackDamage > baseline.AttackDamage);
        runtime.Delivery.Flush();
        capture.Clear();
        TestAssert.True(runtime.Combat.Kill(hero, fixture.Hero(2).NetId));
        runtime.Delivery.Flush();
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor"));
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Length == 9 &&
            packet.Plaintext.Span[0] == FxPackets.KillOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == effect),
            "death destroys the observed persistent Baron effect");
        runtime.Combat.ScheduleRespawn(hero);
        TestAssert.True(runtime.Combat.RespawnImmediately(hero));
        var after = runtime.HeroStats.Calculate(hero);
        TestAssert.Near(baseline.AttackDamage, after.AttackDamage, 1e-9);
        TestAssert.Near(baseline.AbilityPower, after.AbilityPower, 1e-9);
        TestAssert.Near(baseline.MpRegen, after.MpRegen, 1e-9);
        hero.Health = hero.MaxHealth / 2;
        var health = hero.Health;
        // Batch 174 part 2: the Baron's heal is the script's BBExecutePeriodically, which reads the spell clock, so the step
        // advances that clock too; an unremoved Baron would reach its five-second heal here.
        runtime.Spells.Tick(5.0);
        runtime.Buffs.TickBuffs(hero, 5.0);
        TestAssert.Near(health, hero.Health, 1e-9, "no Baron periodic healing after respawn");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor"));
    }

    /// <summary>
    /// ResistantSkin.luaobj's OnBuffActivate clears SetFeared and the other control statuses on its owner (BBSetStatus, SrcValue false,
    /// TargetVar Owner; blocks 0338-0432). This engine keeps those controls as native buffs, so the clear removes them from that unit only.
    /// </summary>
    [Check("items-live-resistant-skin-clears-controls-on-its-holder")]
    public static void ResistantSkinClearsTheControlsOnlyOnItsHolder()
    {
        var fixture = StartProduction();
        using var runtime = fixture.Runtime;
        var bystander = fixture.Hero(2);
        runtime.Buffs.AddBuff(bystander, "Fear", BuffType.Fear, 60.0, bystander.NetId, new BuffPayload());
        // Binding the Baron activates ResistantSkin on it; a control it already holds at that moment is what the activation's SetFeared clears.
        var worm = NewReplacementWorm(runtime, unit => runtime.Buffs.AddBuff(unit, "Fear", BuffType.Fear, 60.0, unit.NetId, new BuffPayload()));
        TestAssert.True(worm.Buffs.Any(buff => buff.Name == "ResistantSkin"), "the binding activates ResistantSkin on the Baron");
        TestAssert.False(worm.Buffs.Any(buff => buff.Type == BuffType.Fear), "the activation clears SetFeared on its owner");
        TestAssert.True(bystander.Buffs.Any(buff => buff.Type == BuffType.Fear), "the clear reaches only the unit it runs on");
    }

    [Check("items-live-audited-nonpersistent-buffs-end-on-death")]
    public static void AuditedBuffsFollowShippedDeathFlags()
    {
        string[] removed =
        [
            "BlindMonkPassive", "ManaBarrierCooldown", "Prowl", "Pyromania", "Pyromania_particle",
            "DeceiveCritBonus", "FromBehind", "CastFromBehind", "OrianaPowerDaggerDisplay",
            "OrianaHaste", "OrianaSlow", "RegenerationPotion", "FlaskOfCrystalWater", "CatalystHeal",
            "OracleElixirSight", "OracleExtractSight", "SpectralFury", "Haste", "ZhonyasRingShield",
            "LightstrikerBuff", "OdinEntropicClaymore", "Sheen", "LichBane", "SheenDelay", "Rageblade",
            "WitsEndCounter", "OdinBloodbursterBuff", "Hexdrinker", "BansheesVeil", "BansheesVeilTimer",
            "BootsOfMobilityDebuff", "BurningEmbers", "SummonerHealCheck", "FortifyBuff", "Tailwind",
            "AbyssalScepterAuraSelf", "ManaManipulatorAuraSelf", "ManaManipulatorAuraFriend",
            "RallyingBannerAuraSelf", "RallyingBannerAuraFriend", "EmblemOfValor", "EmblemOfValorParticle",
            "AegisoftheLegionAuraSelf", "AegisoftheLegionAuraFriend", "WillOfTheAncientsSelf",
            "WillOfTheAncientsFriendly", "TwoShivPoison", "SunfireCapeAura", "LightningRodChain",
            "SoulShroudAuraSelf", "SoulShroudAuraFriend", "JackInTheBoxInternal",
            "Root", "Silence", "Fear", "CantAttack", "BlindMonkETwoMissile", "BlindMonkRRoot", "BlindMonkRMarker",
        ];
        string[] retained = ["PotionOfGiantStrength", "PotionOfElusiveness", "PotionOfBrilliance",
            "HexdrinkerTimer", "Backstab", "VayneSilveredBolts", "ManaBarrierIcon", "TailwindSelf"];
        var fixture = StartProduction();
        using var runtime = fixture.Runtime;
        var hero = fixture.Hero(0);
        foreach (var name in removed.Concat(retained))
        {
            var persists = retained.Contains(name);
            hero.Inventory = name == "BansheesVeilTimer"
                ? [new InventorySlot(3102), null, null, null, null, null]
                : [null, null, null, null, null, null];
            var script = LuaObjectReader.LoadData("Spells/" + name + ".luaobj");
            TestAssert.False(script.Status == LuaLoadStatus.Error, name + " shipped script loads");
            var flag = script.Globals.TryGetValue("PersistsThroughDeath", out var value) &&
                value is LuaValue.Boolean { Value: true };
            TestAssert.Equal(persists, flag, name + " shipped persistence flag (default false)");
            TestAssert.True(runtime.Buffs.AddBuff(hero, name, BuffType.CombatEnhancer, 240,
                hero.NetId) is not null, name + " installs");
            runtime.Buffs.AddBuff(hero, "Pyromania_particle", BuffType.CombatEnhancer, 25000, hero.NetId);
            TestAssert.True(runtime.Combat.Kill(hero, fixture.Hero(2).NetId));
            TestAssert.Equal(persists, hero.Buffs.Any(buff => buff.Name == name), name + " at death");
            if (name == "BansheesVeilTimer")
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"),
                    "removing the owned timer cannot recreate a shield on a dead recipient");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Pyromania_particle"), "the ready Pyromania charge ends with the hero");
            runtime.Combat.ScheduleRespawn(hero);
            TestAssert.True(runtime.Combat.RespawnImmediately(hero));
            TestAssert.Equal(persists, hero.Buffs.Any(buff => buff.Name == name), name + " after respawn");
            foreach (var buff in hero.Buffs.Where(buff => buff.Name == name).ToArray())
                runtime.Buffs.RemoveBuff(hero, buff.Slot);
        }
    }

    [Check("items-live-baron-resource-domain-production")]
    public static void BaronManaRegenDoesNotLeakIntoLeeEnergy()
    {
        RunForTeam(Team.Order);
        RunForTeam(Team.Chaos);
    }

    private static void RunForTeam(Team winningTeam)
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var manaIndex = winningTeam == Team.Order ? 0 : 2;
        var leeIndex = winningTeam == Team.Order ? 1 : 3;
        var manaHero = fixture.Hero(manaIndex);
        var lee = fixture.Hero(leeIndex);

        // Move the production clock to the real Worm's first spawn deadline,
        // then let the normal simulation phase create it through JungleScheduler.
        var wormSpawnAt = NeutralDefinitions.InitialSpawnSeconds +
                          NeutralDefinitions.Camps[12].InitialDelay;
        runtime.Combat.RestoreClock(wormSpawnAt);
        runtime.Tick(0.01);
        var worm = runtime.World.Entities.Values.SingleOrDefault(entity =>
            entity.Kind == EntityKind.Neutral && entity.Name == "Worm" && entity.Alive)
            ?? throw new InvalidOperationException("production scheduler did not spawn the Worm");
        // Playtest B3 #12 (26 Sept 2026): the real Worm now grants its 900
        // global XP to the whole winning team (combat.progression.global-
        // experience). Start that team at level 18 so the kill cannot move
        // the stat baselines this resource-domain check compares.
        foreach (var hero in runtime.World.Heroes().Where(hero => hero.Team == winningTeam))
            runtime.Combat.AwardHeroProgress(hero,
                experience: ProgressionRules.ExperienceThreshold(18) - hero.Experience);
        var beforeMana = runtime.HeroStats.Calculate(manaHero);
        var beforeLee = runtime.HeroStats.Calculate(lee);
        var beforeManaLevel = manaHero.Level;
        var beforeLeeLevel = lee.Level;

        TestAssert.True(runtime.Combat.Kill(worm, manaHero.NetId),
            $"{winningTeam} real Worm death enters the production neutral hook");
        var winnerHeroes = runtime.World.Heroes().Where(hero => hero.Team == winningTeam).ToArray();
        TestAssert.Equal(2, winnerHeroes.Count(hero => hero.Buffs.Any(
                buff => buff.Name == "ExaltedWithBaronNashor")),
            $"{winningTeam} receives Baron exactly once per living hero");
        TestAssert.False(runtime.World.Heroes().Where(hero => hero.Team != winningTeam)
            .Any(hero => hero.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor")),
            $"{winningTeam} Baron death does not grant the opponent team");

        var baron = manaHero.Buffs.Single(buff => buff.Name == "ExaltedWithBaronNashor");
        // Batch 174 part 2: the shipped Exalted writes its mana regeneration as the permanent PAR_MANA modifier
        // (BBIncPermanentFlatPARRegenMod, Spells/ExaltedWithBaronNashor.luaobj 0008-0014), so the mana domain is the modifier's key.
        TestAssert.True(manaHero.PermanentStatMods.Any(pair => pair.Key.EndsWith("RegenMod", StringComparison.Ordinal) &&
                Math.Abs(pair.Value - 3.0) < 1e-9),
            $"{winningTeam} Baron's mana regeneration is the permanent PAR_MANA modifier (3)");
        // Batch 174 part 2: the particle is the script's own (see BaronEndsAtDeath), so the payload keeps no FxNetId and the script
        // instance stores the id.
        var baronInstance = runtime.Spells.UnitScripts!.Buffs!.InstanceOf(baron)
            ?? throw new InvalidOperationException($"{winningTeam} the Baron buff runs as a live script instance");
        TestAssert.Equal(0u, baron.Payload.FxNetId,
            $"{winningTeam} the script's own particle is the only Baron visual");
        TestAssert.True((uint)(double)baronInstance.InstanceVars["buffParticle"]! != 0,
            $"{winningTeam} Baron buff binds the shipped persistent nashor_rune_buf effect through its script");
        var afterMana = runtime.HeroStats.Calculate(manaHero);
        var afterLee = runtime.HeroStats.Calculate(lee);
        TestAssert.Equal(beforeManaLevel, manaHero.Level,
            $"{winningTeam} Worm kill does not level the mana baseline");
        TestAssert.Equal(beforeLeeLevel, lee.Level,
            $"{winningTeam} Worm kill does not level the Lee Sin baseline");
        TestAssert.Near(3.0, afterMana.MpRegen - beforeMana.MpRegen, 1e-9,
            $"{winningTeam} mana hero receives Baron's +3 mana regen");
        TestAssert.Near(0.0, afterLee.MpRegen - beforeLee.MpRegen, 1e-9,
            $"{winningTeam} Lee Sin does not receive Baron's +3 energy regen");
        TestAssert.Near(beforeLee.EnergyRegen, afterLee.EnergyRegen, 1e-9,
            $"{winningTeam} Lee Sin authoritative energy regen is unchanged");

        manaHero.Health = manaHero.MaxHealth * 0.5;
        var healthBeforePeriod = manaHero.Health;
        // Batch 174 part 2: the heal is the script's BBExecutePeriodically in its stats hook, which reads the spell clock
        // (BBGetGameTime), so each step advances that clock with the buff update. A TickBuffs-only step never reached the 5 s line.
        runtime.Spells.Tick(4.9);
        runtime.Buffs.TickBuffs(manaHero, 4.9);
        TestAssert.Near(healthBeforePeriod, manaHero.Health, 1e-9,
            $"{winningTeam} Baron health update waits for five seconds");
        runtime.Spells.Tick(0.1);
        runtime.Buffs.TickBuffs(manaHero, 0.1);
        // The heal amount is a float32 product (51.48 is held as 51.4799995 in float32), so the expectation holds to float32 precision.
        TestAssert.Near(healthBeforePeriod + manaHero.MaxHealth * 0.03, manaHero.Health, 1e-4,
            $"{winningTeam} Baron health update heals three percent max health at five seconds");

        // A second production neutral death hook exercises the shipped grant's
        // renewal path: one authoritative buff, reset duration, no duplicate
        // stat payload.
        var remaining = baron.ExpiresAt;
        var replacementWorm = NewReplacementWorm(runtime);
        TestAssert.True(runtime.Combat.Kill(replacementWorm, manaHero.NetId),
            $"{winningTeam} second Worm death reaches the production reapply hook");
        var renewed = manaHero.Buffs.Single(buff => buff.Name == "ExaltedWithBaronNashor");
        // Batch 174 part 2: the shipped grant is BUFF_RENEW_EXISTING with MaxStack 1 (ResistantSkin.luaobj 0558-0571), so the
        // live instance is renewed in place rather than replaced.
        TestAssert.True(ReferenceEquals(baron, renewed),
            $"{winningTeam} Baron reapply renews the live instance (BUFF_RENEW_EXISTING)");
        var renewedInstance = runtime.Spells.UnitScripts!.Buffs!.InstanceOf(renewed)
            ?? throw new InvalidOperationException($"{winningTeam} the renewed Baron keeps a live script instance");
        TestAssert.True((uint)(double)renewedInstance.InstanceVars["buffParticle"]! != 0,
            $"{winningTeam} Baron reapply recreates its persistent visual through the script");
        TestAssert.True(renewed.ExpiresAt > remaining,
            $"{winningTeam} Baron reapply resets its remaining duration");
        TestAssert.Equal(1, manaHero.Buffs.Count(buff => buff.Name == "ExaltedWithBaronNashor"),
            $"{winningTeam} Baron reapply keeps one authoritative buff");

        var expectedLee = runtime.HeroStats.Calculate(lee);
        var capture = new CaptureTransport();
        var observerId = 9000UL + (ulong)leeIndex;
        runtime.Delivery.AddObserver(new ReplicationObserver(
            observerId, lee.Team, lee.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        capture.Clear();
        runtime.HeroStats.Push(lee);
        runtime.Delivery.Flush();
        var published = capture.Packets.Single(packet => IsHeroStats(packet, lee.NetId));
        TestAssert.Near(expectedLee.MpRegen, FloatAt(published, MpRegenOffset), 1e-5,
            $"{winningTeam} published Lee Sin stats keep the mana-domain exclusion");

        var reconnect = Reconnect(runtime, fixture.Connection(leeIndex), (ulong)(leeIndex + 1),
            5000 + leeIndex, observerId, capture);
        var reconnectStats = reconnect.Baseline.Single(packet => IsHeroStats(packet, lee.NetId));
        TestAssert.Near(expectedLee.MpRegen, FloatAt(reconnectStats, MpRegenOffset), 1e-5,
            $"{winningTeam} reconnect publishes Lee Sin's unchanged energy regen");
        TestAssert.True(reconnect.Baseline.Concat(reconnect.Ready).Concat(reconnect.State)
                .Concat(reconnect.Delivery)
                .Any(packet => HasBuff(packet, lee.NetId,
                    SpellPackets.BuffNameHash("ExaltedWithBaronNashor"))), // Batch 357 (restates batch 181): the binding; its BuffName has no script
            $"{winningTeam} reconnect retains the renewed Baron buff identity");
    }

    private static ProductionFixture StartProduction()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order mana", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order energy", "LeeSin", Team.Order),
            new SessionParticipantDefinition(3, 2, "Chaos mana", "Orianna", Team.Chaos),
            new SessionParticipantDefinition(4, 3, "Chaos energy", "LeeSin", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, random: new Amberline.Core.SeededRandomSource(7));
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        var connections = new long[roster.Length];
        for (var index = 0; index < roster.Length; index++)
        {
            var connection = 100L + index;
            connections[index] = connection;
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"Baron seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"Baron seat {index} reaches ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "Baron production fixture starts");
        return new ProductionFixture(runtime, connections);
    }

    /// <summary>
    /// Batch 174 part 2: a Baron kill is the shipped ResistantSkin BuffOnDeath's grant (Spells/ResistantSkin.luaobj 0519-0634): the killer's
    /// team gets ExaltedWithBaronNashor for the script's 240 s, and the script's own bonus (clamp(GameTime / 30 - 15, 20, 40), so 20 before
    /// GameTime 1050) reaches the killer's attack damage. The other team gets nothing.
    /// </summary>
    [Check("items-live-baron-kill-grants-exalted-duration-and-stats")]
    public static void BaronKillGrantsExaltedWithItsScriptedDurationAndStats()
    {
        var fixture = StartProduction();
        using var runtime = fixture.Runtime;
        var killer = fixture.Hero(0);
        var rival = fixture.Hero(2);
        runtime.Combat.AwardHeroProgress(killer,
            experience: ProgressionRules.ExperienceThreshold(18) - killer.Experience);
        var baseline = runtime.HeroStats.Calculate(killer).AttackDamage;
        var worm = NewReplacementWorm(runtime);
        TestAssert.True(runtime.Combat.Kill(worm, killer.NetId), "a Baron kill reaches the scripted team grant");
        var grant = killer.Buffs.Single(buff => buff.Name == "ExaltedWithBaronNashor");
        TestAssert.Near(240.0, grant.ExpiresAt, 1e-6, "Exalted lasts the shipped 240 s");
        TestAssert.Near(20.0, runtime.HeroStats.Calculate(killer).AttackDamage - baseline, 1e-6,
            "the shipped bonus (clamp(GameTime / 30 - 15, 20, 40)) reaches the killer's attack damage");
        TestAssert.False(rival.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor"),
            "the Baron's team grant does not reach the other team");
    }

    /// <summary>
    /// Batch 256: a bound non-champion unit's character script polls on its own 0.25 s self-buff line, as a hero's does (batch 246,
    /// <c>BbSelfUpdate</c>). The throttle (<c>srvNextUpdate</c>) and the character script are both <c>obj_AI_Base</c> members in the
    /// client's layout, so a turret, a camp and the Worm take the hero's rule (Reconstruction for the period). CharScriptWorm's
    /// <c>UpdateSelfBuffStats</c> renews ResistantSkin with <c>BUFF_RENEW_EXISTING</c> on every poll: four in-place renewals a
    /// second reach an observer, not one per 33 ms step. Its <c>SetCanMove</c>/<c>SetCanAttack</c> false are one bit each that the last
    /// writer sets (the status natives write the character state's word directly), so a rest write from another script holds until
    /// the Worm's next poll, at most one line.
    /// </summary>
    [Check("items-live-worm-character-poll-runs-on-the-quarter-second-line")]
    public static void WormCharacterPollRunsOnTheQuarterSecondLine()
    {
        var fixture = StartProduction();
        using var runtime = fixture.Runtime;
        var worm = NewReplacementWorm(runtime);
        runtime.Tick(0.25);
        var skin = worm.Buffs.Single(buff => buff.Name == "ResistantSkin");
        var units = runtime.Spells.UnitScripts!;
        var pollsBefore = units.PollRuns["Worm"];
        // A renewal puts the timer back at the add's 25000 s; between renewals it counts down by the step. (Each renewal is one
        // in-place 0x32/0x1F to an observer that holds the slot; the hand-registered Worm here was never spawned to one.)
        const double step = 1.0 / 30.0;
        var renewals = 0;
        for (var index = 0; index < 30; index++)
        {
            var ends = skin.ExpiresAt;
            runtime.Tick(step);
            if (skin.ExpiresAt > ends - step + 1e-6) renewals++;
        }
        var polls = units.PollRuns["Worm"] - pollsBefore;
        Console.WriteLine($"worm: {polls} polls, {renewals} ResistantSkin renewals in 1.0 s");
        TestAssert.True(ReferenceEquals(skin, worm.Buffs.Single(buff => buff.Name == "ResistantSkin")), "one ResistantSkin, the same instance");
        TestAssert.Equal(4, polls, "the Worm's character script polls once per 0.25 s line");
        TestAssert.Equal(4, renewals, "one ResistantSkin renewal per poll");

        // The rest write of another script releases the bit; the Worm's next poll puts it back, inside one line.
        TestAssert.False(CharacterState.Of(worm).CanMove, "the Worm's stats hook holds SetCanMove false");
        units.Buffs!.WriteStatus(worm, BbSetStatus.SetCanMove, true, null, "OnBuffDeactivate");
        TestAssert.True(CharacterState.Of(worm).CanMove, "a rest write releases the one bit");
        var lapse = 0.0;
        while (CharacterState.Of(worm).CanMove && lapse < 1.0)
        {
            runtime.Tick(step);
            lapse += step;
        }
        Console.WriteLine($"worm: SetCanMove lapse {lapse:0.000} s");
        // One line, rounded up to the 33 ms step that reaches it.
        TestAssert.True(lapse <= 0.25 + step + 1e-6, $"the next poll re-asserts the bit within one line ({lapse:0.000} s)");
        TestAssert.Equal(0, units.ScriptFailures.Count, "no unit script failure");
    }

    private static Entity NewReplacementWorm(MatchRuntime runtime, Action<Entity>? beforeBind = null)
    {
        var worm = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, new Position2(4600.0, 10250.0))
        {
            Name = "Worm",
            Alive = true,
            Created = true,
            Visible = true,
            Health = 1000.0,
            MaxHealth = 1000.0,
            CampId = 12,
        };
        runtime.World.Register(worm);
        // Batch 174 part 2: a live Baron is bound to CharScriptWorm, whose ResistantSkin BuffOnDeath is the team grant.
        beforeBind?.Invoke(worm);
        runtime.Spells.UnitScripts!.BindUnit(worm, "Worm");
        return worm;
    }

    private static ReconnectResult Reconnect(MatchRuntime runtime, long oldConnection,
        ulong playerId, long newConnection, ulong oldObserverId, CaptureTransport capture)
    {
        TestAssert.True(runtime.Session.Disconnect(oldConnection),
            $"Baron player {playerId} disconnects");
        TestAssert.True(runtime.Delivery.RemoveObserver(oldObserverId),
            $"Baron player {playerId} removes the detached observer");
        var participant = runtime.Session.AuthenticateReconnect(newConnection,
            runtime.Session.BuildKeyCheck(playerId));
        TestAssert.True(participant is not null, $"Baron player {playerId} reconnects");
        var handshake = runtime.Session.Receive(newConnection, 1, [0xC5]);
        TestAssert.True(handshake.Any(output => output.Packet.Plaintext.Span[0] == 0x57),
            "Baron reconnect receives the staged handshake");
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = participant!.SpawnAt ?? throw new InvalidOperationException(
            "Baron reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        var baseline = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var ready = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        TestAssert.True(participant.Spawned && participant.Loaded && participant.Ready,
            "Baron reconnect reaches ready");
        var state = runtime.Session.BuildReconnectMatchState(newConnection);
        var reconnectObserver = new ReplicationObserver(
            participant!.PlayerId, participant.Team, participant.Hero!.NetId, capture,
            ready: participant.Ready, view: participant.View, clientId: participant.ClientId);
        runtime.Delivery.AddObserver(reconnectObserver);
        capture.Clear();
        runtime.Delivery.Flush();
        return new ReconnectResult(baseline, ready, state, capture.Packets.ToArray());
    }

    private static bool IsHeroStats(PlaintextPacket packet, uint netId) =>
        packet.Plaintext.Length >= 171 && packet.Plaintext.Span[0] == 0xCC &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..15]) == netId;

    private static bool HasBuff(PlaintextPacket packet, uint targetNetId, uint hash)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 13 && bytes[0] == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == targetNetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..13]) == hash;
    }

    private static float FloatAt(PlaintextPacket packet, int offset) =>
        BitConverter.ToSingle(packet.Plaintext.Span[offset..(offset + 4)]);

    private sealed record ProductionFixture(MatchRuntime Runtime, long[] Connections)
    {
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!
            ?? throw new InvalidOperationException($"Baron hero {index} missing");

        public long Connection(int index) => Connections[index];
    }

    private sealed record ReconnectResult(
        IReadOnlyList<PlaintextPacket> Baseline,
        IReadOnlyList<PlaintextPacket> Ready,
        IReadOnlyList<PlaintextPacket> State,
        IReadOnlyList<PlaintextPacket> Delivery);

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);

        public void Clear() => Packets.Clear();
    }
}
