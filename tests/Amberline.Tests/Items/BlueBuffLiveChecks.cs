using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Items;

/// <summary>
/// Production-graph coverage for the patch-126 blue buff resource domains. The transfer is the shipped buff
/// script's own <c>BuffOnDeath</c> now (batch 159 part 2): Spells/CrestoftheAncientGolem.luaobj writes .015 of the
/// holder's own pool into a flat PAR regeneration row (0088-0117 for mana, 0119-0150 for energy), the direct hero
/// kill renews the live instance (0323, <c>BUFF_RENEW_EXISTING</c>) and a pet kill resolves to the owner with
/// <c>BUFF_REPLACE_EXISTING</c> (0350-0441).
/// </summary>
public static class BlueBuffLiveChecks
{
    private const int MpRegenOffset = 91;

    [Check("items-live-blue-buff-resource-domain-production")]
    public static void BlueBuffTransfersAcrossTeamsAndResourceDomains()
    {
        RunForTeam(Team.Order);
        RunForTeam(Team.Chaos);
    }

    /// <summary>
    /// Batch 196: the utility mastery's marker (Talents/135.luaobj: <c>MonsterBuffs</c> at rank 1, <c>MonsterBuffs2</c> at rank 2)
    /// is a live buff on its hero from creation, and the shipped death scripts multiply their <c>NewDuration</c> by 1.15 / 1.3 when
    /// the killer holds it (Spells/CrestoftheAncientGolem.luaobj, Spells/BlessingoftheLizardElder.luaobj). The Baron's team buff
    /// reads no marker (ExaltedWithBaronNashor and ResistantSkin do not name it), so its duration does not move.
    /// </summary>
    [Check("items-live-utility-marker-scales-neutral-buff-durations")]
    public static void UtilityMarkerScalesTheShippedNeutralBuffDurations()
    {
        var without = NeutralBuffDurations(utilityRank: 0);
        var rankOne = NeutralBuffDurations(utilityRank: 1);
        var rankTwo = NeutralBuffDurations(utilityRank: 2);

        TestAssert.True(without.Blue > 0 && without.Red > 0 && without.Baron > 0,
            $"the blue, red and Baron buffs last a positive time without the utility mastery " +
            $"(blue {without.Blue}, red {without.Red}, Baron {without.Baron})");
        TestAssert.Near(1.15, rankOne.Blue / without.Blue, 1e-9, "rank 1 utility scales the blue buff by 1.15");
        TestAssert.Near(1.15, rankOne.Red / without.Red, 1e-9, "rank 1 utility scales the red buff by 1.15");
        TestAssert.Near(1.30, rankTwo.Blue / without.Blue, 1e-9, "rank 2 utility scales the blue buff by 1.30");
        TestAssert.Near(1.30, rankTwo.Red / without.Red, 1e-9, "rank 2 utility scales the red buff by 1.30");
        TestAssert.Near(1.0, rankTwo.Baron / without.Baron, 1e-9, "the Baron buff has no utility term");
    }

    private static (double Blue, double Red, double Baron) NeutralBuffDurations(int utilityRank)
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var killer = fixture.Hero(0);
        if (utilityRank > 0) killer.Masteries = UtilityRankPage(utilityRank);
        // Handed in after creation: the next loadout hook binds the page again and installs its marker live.
        runtime.Spells.LoadoutHooks?.OwnerFor(killer);
        var spawnAt = NeutralDefinitions.InitialSpawnSeconds +
                      new[] { 1, 4, 12 }.Max(camp => NeutralDefinitions.Camps[camp].InitialDelay);
        runtime.Combat.RestoreClock(spawnAt);
        runtime.Tick(0.01);
        var golem = SpawnedCamp(runtime, "AncientGolem", 1);
        var lizard = SpawnedCamp(runtime, "LizardElder", 4);
        var worm = SpawnedCamp(runtime, "Worm", 12);
        var marker = utilityRank switch { 1 => "MonsterBuffs", 2 => "MonsterBuffs2", _ => null };

        TestAssert.True(runtime.Combat.Kill(golem, killer.NetId),
            $"utility rank {utilityRank}: the killer takes the blue golem");
        var blue = killer.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
        if (marker is not null)
        {
            TestAssert.True(killer.Buffs.Count(buff => buff.Name == marker) == 1,
                $"utility rank {utilityRank}: the marker is one live buff on the killer");
            TestAssert.True(killer.Buffs.Single(buff => buff.Name == marker).ExpiresAt > 1000.0,
                $"utility rank {utilityRank}: the marker outlasts the neutral kills of a match");
        }

        TestAssert.True(runtime.Combat.Kill(lizard, killer.NetId),
            $"utility rank {utilityRank}: the killer takes the red golem");
        var red = killer.Buffs.Single(buff => buff.Name == "BlessingoftheLizardElder");

        TestAssert.True(runtime.Combat.Kill(worm, killer.NetId),
            $"utility rank {utilityRank}: the killer takes the Baron");
        var baron = killer.Buffs.Single(buff => buff.Name == "ExaltedWithBaronNashor");
        // The Baron's team experience can level the killer, which rebinds the page: the marker must renew, not stack.
        if (marker is not null)
            TestAssert.True(killer.Buffs.Count(buff => buff.Name == marker) == 1,
                $"utility rank {utilityRank}: the marker is still one live buff after the Baron");
        // ExpiresAt is the remaining duration (BuffLifecycle ticks it down). Read straight after each kill, before any tick,
        // it is the duration the script's BuffOnDeath gave the buff.
        return (blue.ExpiresAt, red.ExpiresAt, baron.ExpiresAt);
    }

    private static Entity SpawnedCamp(MatchRuntime runtime, string name, int camp) =>
        runtime.World.Entities.Values.SingleOrDefault(entity =>
            entity.Kind == EntityKind.Neutral && entity.Name == name && entity.Alive && entity.CampId == camp)
        ?? throw new InvalidOperationException($"production scheduler did not spawn {name} in camp {camp}");

    private static MasteryPage UtilityRankPage(int rank) => MasteryPage.Create(new MasteryCatalog(),
        new Dictionary<int, int>
        {
            [124] = 1, [134] = 1, [132] = 1, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3,
            [135] = rank, [147] = 1, [119] = 3, [140] = 1,
            [118] = 3, [122] = 1
        });

    /// <summary>
    /// Batch 196 follow-up: the utility marker is a non-persistent script buff, so a death retires it, and the talent's own
    /// UpdateSelfBuffActions add (Talents/135.luaobj, BUFF_RENEW_EXISTING) puts it back on the living hero's next poll. The
    /// respawned hero then takes its blue buff before any level-up, and that buff carries the factor of a hero that never died.
    /// </summary>
    [Check("items-live-utility-marker-returns-after-a-death")]
    public static void UtilityMarkerReturnsAfterADeath()
    {
        foreach (var rank in new[] { 1, 2 })
        {
            var marker = rank == 1 ? "MonsterBuffs" : "MonsterBuffs2";
            var neverDied = NeutralBuffDurations(utilityRank: rank).Blue;
            var fixture = StartProduction();
            var runtime = fixture.Runtime;
            var hero = fixture.Hero(0);
            hero.Masteries = UtilityRankPage(rank);
            runtime.Spells.LoadoutHooks?.OwnerFor(hero);
            var level = hero.Level;

            TestAssert.True(runtime.Combat.Kill(hero, fixture.Hero(2).NetId),
                $"utility rank {rank}: the Chaos hero kills the Order hero");
            // The lethal hit's caller schedules the respawn after Kill (CombatService damage path); Kill alone does not.
            runtime.Combat.ScheduleRespawn(hero);
            TestAssert.False(hero.Alive, $"utility rank {rank}: the hero is dead");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == marker),
                $"utility rank {rank}: the death retires the non-persistent marker");

            runtime.Combat.RestoreClock(hero.RespawnAt);
            runtime.Tick(0.01);
            TestAssert.True(hero.Alive, $"utility rank {rank}: the hero respawns");
            runtime.Tick(0.25);
            TestAssert.True(hero.Buffs.Count(buff => buff.Name == marker) == 1,
                $"utility rank {rank}: the marker is back once on the first poll after the respawn");

            // The blue camp comes up while the hero is still at level 1: its buff must carry the marker's factor.
            runtime.Combat.RestoreClock(Math.Max(runtime.Combat.CurrentTime,
                NeutralDefinitions.InitialSpawnSeconds + NeutralDefinitions.Camps[1].InitialDelay));
            runtime.Tick(0.01);
            var golem = SpawnedCamp(runtime, "AncientGolem", 1);
            for (var poll = 0; poll < 4; poll++) runtime.Tick(0.25);
            TestAssert.True(hero.Buffs.Count(buff => buff.Name == marker) == 1,
                $"utility rank {rank}: more polls add no second marker");
            TestAssert.True(hero.Level == level, $"utility rank {rank}: no level-up before the blue camp");
            TestAssert.True(runtime.Combat.Kill(golem, hero.NetId),
                $"utility rank {rank}: the respawned hero takes the blue camp");
            var blue = hero.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
            TestAssert.Near(neverDied, blue.ExpiresAt, 1e-9,
                $"utility rank {rank}: the blue buff after the respawn carries the factor of a hero that never died");
        }
    }

    /// <summary>
    /// Batch 241: retail runs a talent's <c>UpdateSelfBuffActions</c> on every poll. Talents/135.luaobj adds its marker with
    /// <c>BUFF_RENEW_EXISTING</c> unconditionally (0012-0027, 0037-0052), so every poll renews the live marker; Talents/111.luaobj
    /// guards Ardor's add with its own <c>BBIfNotHasBuff</c> (0002-0008), so a hero that holds Ardor gets no add. The renewal reaches
    /// the client as an in-place renewal of the slot it holds (0x32, or 0x1F; batch 157's rule in ReplicationDelivery.AsHeldBuffUpdate),
    /// never a second 0xBF, so the marker does not flicker. Before batch 241 the add ran only while the buff was missing.
    /// <para>
    /// Batch 246: the poll is the self-buff update line, every 0.25 s per hero (<c>BbSelfUpdate</c>; Reconstruction, the engine's
    /// <c>BuffOnUpdateActions</c> grid), not every 33 ms simulation step: four renewals a second reach each observer. On the step
    /// cadence batch 241 measured 8 per 0.25 s, about 32 a second per observer.
    /// </para>
    /// </summary>
    [Check("items-live-utility-marker-renews-on-every-poll-without-flicker")]
    public static void UtilityMarkerRenewsOnEveryPoll()
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var hero = fixture.Hero(0);
        hero.Masteries = UtilityRankPage(1);
        runtime.Spells.LoadoutHooks?.OwnerFor(hero);
        runtime.Tick(0.25);
        var marker = hero.Buffs.Single(buff => buff.Name == "MonsterBuffs");
        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(9700UL, hero.Team, hero.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        capture.Clear();
        var endsBefore = marker.ExpiresAt;
        runtime.Tick(1.0);
        runtime.Delivery.Flush();
        TestAssert.True(ReferenceEquals(marker, hero.Buffs.Single(buff => buff.Name == "MonsterBuffs")), "one marker, the same instance");
        // ExpiresAt is the time the buff has left: a renewal puts it back at the 25000 s of the add, an untouched buff counts down.
        TestAssert.True(marker.ExpiresAt > 25000.0 - 0.3, $"the last self-buff poll renewed the marker's 25000 s timer ({endsBefore} -> {marker.ExpiresAt})");
        var hash = SpellPackets.BuffNameHash("MonsterBuffs");
        TestAssert.False(capture.Packets.Any(packet => HasBuff(packet, hero.NetId, hash)), "no 0xBF re-add of the marker (no flicker)");
        var renewals = capture.Packets.Count(packet => packet.Plaintext.Span is var bytes && bytes.Length >= 6 &&
            bytes[0] is SpellPackets.BuffReplaceOpcode or SpellPackets.BuffUpdateCountOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == hero.NetId && bytes[5] == marker.Slot);
        Console.WriteLine($"utility marker: {renewals} in-place renewals in 1.0 s");
        TestAssert.Equal(4, renewals, "one in-place renewal of the held slot per 0.25 s self-buff poll");

        // Ardor (Talents/111.luaobj): the script's own BBIfNotHasBuff keeps the poll from touching a live Ardor.
        var ardorHero = fixture.Hero(1);
        ardorHero.Masteries = ArdorPage(new MasteryCatalog());
        runtime.Spells.LoadoutHooks?.OwnerFor(ardorHero);
        runtime.Tick(0.25);
        var ardor = ardorHero.Buffs.Single(buff => buff.Name == "Ardor");
        var ardorEnds = ardor.ExpiresAt;
        runtime.Tick(0.25);
        TestAssert.True(ReferenceEquals(ardor, ardorHero.Buffs.Single(buff => buff.Name == "Ardor")), "one Ardor, the same instance");
        TestAssert.True(ardor.ExpiresAt < ardorEnds, $"the poll does not replace a live Ardor (its script's BBIfNotHasBuff): {ardorEnds} -> {ardor.ExpiresAt}");
        TestAssert.Equal(0, runtime.Spells.LoadoutHooks!.Failures.Count, "no talent script failure");
    }

    /// <summary>
    /// Batch 196 follow-up: Ardor's buff declares PersistsThroughDeath (Spells/Ardor.luaobj), so a death keeps it. Its numbers
    /// must be the same after the respawn, with exactly one live instance. The Blasting Wand (1026) is the flat AP pool Ardor
    /// scales: it has no script, so no item multiplier can move the number on its own (Deathcap's 1.30 is not restored after a
    /// death, a separate item-layer gap).
    /// </summary>
    [Check("items-live-ardor-keeps-its-numbers-after-a-death")]
    public static void ArdorKeepsItsNumbersAfterADeath()
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var hero = fixture.Hero(0);
        hero.Masteries = ArdorPage(new MasteryCatalog());
        hero.Inventory = [new InventorySlot(1026), null, null, null, null, null];
        runtime.Spells.LoadoutHooks?.OwnerFor(hero);
        SettleStats(runtime, hero);
        var attackPower = hero.AbilityPower;
        var attackSpeed = hero.AttackSpeedMod;
        TestAssert.True(attackPower > 0.0, "Ardor scales the flat AP pool before the death");
        TestAssert.True(hero.Buffs.Count(buff => buff.Name == "Ardor") == 1, "one Ardor instance before the death");

        TestAssert.True(runtime.Combat.Kill(hero, fixture.Hero(2).NetId), "the Chaos hero kills the Order hero");
        runtime.Combat.ScheduleRespawn(hero);
        runtime.Combat.RestoreClock(Math.Max(runtime.Combat.CurrentTime, hero.RespawnAt));
        runtime.Tick(0.01);
        TestAssert.True(hero.Alive, "the hero respawns");
        SettleStats(runtime, hero);
        TestAssert.True(hero.Buffs.Count(buff => buff.Name == "Ardor") == 1, "one Ardor instance after the respawn");
        TestAssert.Near(attackPower, hero.AbilityPower, 1e-9, "Ardor's AP after the respawn");
        TestAssert.Near(attackSpeed, hero.AttackSpeedMod, 1e-9, "Ardor's attack speed after the respawn");
    }

    /// <summary>
    /// Batch 204: Rabadon's Deathcap (Items/3089.luaobj) re-adds ZhonyasRing from its own UpdateSelfBuffActions
    /// (BBExecutePeriodically 10 s, ExecuteImmediately). ZhonyasRing is not persistent, so the death removes it and the
    /// periodic's re-add on a corpse is refused. The respawn restarts the periodic (BbItemModule.OnRespawn): the living
    /// hero's first poll puts the buff back, with one instance and the AP it had before the death. Before the fix the AP
    /// stayed at 140 for 7 s after the respawn (probe, batch 204).
    /// </summary>
    [Check("items-live-deathcap-restores-its-multiplier-after-a-death")]
    public static void DeathcapRestoresItsMultiplierAfterADeath() => AssertDeathcapSurvivesADeath(null);

    /// <summary>Batch 204: the same death with the Blasting Wand (1026, a flat AP item with no script) on top of Deathcap.</summary>
    [Check("items-live-deathcap-with-an-ap-item-restores-after-a-death")]
    public static void DeathcapWithAnApItemRestoresAfterADeath() => AssertDeathcapSurvivesADeath(1026);

    private static void AssertDeathcapSurvivesADeath(int? companion)
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var hero = fixture.Hero(0);
        var catalog = new ItemCatalog(runtime.GameData.Paths);
        // The flat pool Deathcap multiplies: its own 140 and the companion's flat AP, both from the shipped catalogue.
        var flatPool = catalog.Get(3089)!.Modifier("FlatMagicDamageMod") +
                       (companion is { } id ? catalog.Get(id)!.Modifier("FlatMagicDamageMod") : 0.0);
        if (companion is null) hero.Inventory = [new InventorySlot(3089), null, null, null, null, null];
        else hero.Inventory = [new InventorySlot(3089), new InventorySlot(companion.Value), null, null, null, null];
        SettleStats(runtime, hero);
        var before = hero.AbilityPower;
        TestAssert.Near(flatPool * 1.30, before, 1e-9, "Deathcap's 1.30 over the whole flat-AP pool before the death");
        TestAssert.True(hero.Buffs.Count(buff => buff.Name == "ZhonyasRing") == 1, "one ZhonyasRing before the death");

        TestAssert.True(runtime.Combat.Kill(hero, fixture.Hero(2).NetId), "the Chaos hero kills the Order hero");
        runtime.Combat.ScheduleRespawn(hero);
        runtime.Combat.RestoreClock(Math.Max(runtime.Combat.CurrentTime, hero.RespawnAt));
        runtime.Tick(0.01);
        TestAssert.True(hero.Alive, "the hero respawns");
        runtime.Tick(0.25);
        runtime.HeroStats.Refresh(hero);
        TestAssert.True(hero.Buffs.Count(buff => buff.Name == "ZhonyasRing") == 1,
            "one ZhonyasRing on the living hero's first poll after the respawn");
        TestAssert.Near(before, hero.AbilityPower, 1e-9, "the AP on the first poll after the respawn");

        // Twelve seconds cover at least one periodic renewal: the same single instance and the same number.
        for (var tick = 0; tick < 48; tick++) runtime.Tick(0.25);
        runtime.HeroStats.Refresh(hero);
        TestAssert.True(hero.Buffs.Count(buff => buff.Name == "ZhonyasRing") == 1, "one ZhonyasRing after the renewals");
        TestAssert.Near(before, hero.AbilityPower, 1e-9, "no second multiplier after the periodic renewals");
    }

    private static void SettleStats(MatchRuntime runtime, Entity hero)
    {
        for (var tick = 0; tick < 12; tick++) runtime.Tick(0.25);
        runtime.HeroStats.Refresh(hero);
    }

    private static MasteryPage ArdorPage(MasteryCatalog catalog) => MasteryPage.Create(catalog,
        new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1, [125] = 1
        });

    private static void RunForTeam(Team winningTeam)
    {
        var fixture = StartProduction();
        var runtime = fixture.Runtime;
        var manaIndex = winningTeam == Team.Order ? 0 : 2;
        var leeIndex = winningTeam == Team.Order ? 1 : 3;
        var manaHero = fixture.Hero(manaIndex);
        var lee = fixture.Hero(leeIndex);
        var deadOwner = fixture.Hero(winningTeam == Team.Order ? 2 : 0);

        var spawnAt = NeutralDefinitions.InitialSpawnSeconds +
                      NeutralDefinitions.Camps[1].InitialDelay;
        runtime.Combat.RestoreClock(spawnAt);
        runtime.Tick(0.01);
        var firstGolem = runtime.World.Entities.Values.SingleOrDefault(entity =>
            entity.Kind == EntityKind.Neutral && entity.Name == "AncientGolem" && entity.Alive &&
            entity.CampId == 1)
            ?? throw new InvalidOperationException("production scheduler did not spawn the blue golem");

        var manaBefore = runtime.HeroStats.Calculate(manaHero);
        var leeBefore = runtime.HeroStats.Calculate(lee);
        TestAssert.True(runtime.Combat.Kill(firstGolem, manaHero.NetId),
            $"{winningTeam} real AncientGolem death reaches blue transfer");

        var first = manaHero.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
        // The payload is the script's own stats sample, not the hand-written MaxManaRegenPercent /
        // MaxEnergyRegenPercent pair: Spells/CrestoftheAncientGolem.luaobj 0088-0117 multiplies the holder's maximum
        // mana by 0.015 and hands it to BBIncFlatPARRegenMod (PAR_MANA), so the row is a flat regeneration of .015 of
        // the pool already held. The energy twin (0119-0150) writes the same row from the energy pool, which is what
        // lands on Lee Sin below.
        TestAssert.True(first.Payload.Has(BuffPayloadField.MpRegen),
            $"{winningTeam} blue payload marks the PAR regeneration field");
        TestAssert.Near(manaBefore.MaxMp * 0.015, first.Payload.MpRegen, 1e-9,
            $"{winningTeam} blue regeneration payload is .015 of the mana pool");
        TestAssert.Near(manaBefore.MaxMp * 0.015,
            runtime.HeroStats.Calculate(manaHero).MpRegen - manaBefore.MpRegen, 1e-9,
            $"{winningTeam} blue contributes .015 of the mana pool");

        var firstSlot = first.Slot;
        // Batch 171: the crest's particle is the buff script's own. Spells/CrestoftheAncientGolem.luaobj's OnBuffActivate
        // (0010-0037) is a BBSpellEffectCreate of NeutralMonster_buf_blue_defense.troy into InstanceVars.buffParticle, and
        // its OnBuffDeactivate (0039-0048) takes that id back. Since batch 157 part 1 a script that draws its own
        // activation effect suppresses the server-owned PersistentEffects row (BuffEffectService.EnsureBuffFx reads
        // BuffPayload.ScriptOwnsActivationFx), so the payload's FxNetId is 0 and the primary identity is the id the script
        // stored — the one the wire's 0x8C carries. The old pin read FxNetId, which was the hand-written row batch 157
        // removed; the shipped script is the truth, so the check reads the script's own variable.
        var firstInstance = runtime.Spells.UnitScripts!.Buffs!.InstanceOf(first)
            ?? throw new InvalidOperationException(
                $"{winningTeam} the crest runs as a live script instance on the hero");
        var firstFx = (uint)(double)firstInstance.InstanceVars["buffParticle"]!;
        var firstExtraFx = first.Payload.ExtraFxNetId;
        TestAssert.True(firstFx != 0,
            $"{winningTeam} direct hero blue transfer allocates a primary FX identity");
        TestAssert.Equal(0u, first.Payload.FxNetId,
            $"{winningTeam} the script's own particle is the only blue visual");
        first.Payload.PeriodElapsed = 4.0;
        runtime.Buffs.TickBuffs(manaHero, 7.0);
        var firstExpiry = first.ExpiresAt;
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), manaHero.NetId),
            $"{winningTeam} repeated direct hero acquisition reaches production transfer");
        var renewed = manaHero.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
        TestAssert.True(ReferenceEquals(first, renewed),
            $"{winningTeam} direct hero reacquisition renews the same live buff");
        TestAssert.Equal(firstSlot, renewed.Slot,
            $"{winningTeam} direct hero reacquisition keeps the buff slot");
        var renewedInstance = runtime.Spells.UnitScripts.Buffs!.InstanceOf(renewed)
            ?? throw new InvalidOperationException(
                $"{winningTeam} the renewed crest keeps its live script instance");
        TestAssert.Equal(firstFx, (uint)(double)renewedInstance.InstanceVars["buffParticle"]!,
            $"{winningTeam} direct hero reacquisition keeps the primary FX identity");
        TestAssert.Equal(firstExtraFx, renewed.Payload.ExtraFxNetId,
            $"{winningTeam} direct hero reacquisition keeps the secondary FX identity");
        TestAssert.Near(4.0, renewed.Payload.PeriodElapsed, 1e-12,
            $"{winningTeam} direct hero renewal keeps the periodic clock");
        TestAssert.True(renewed.ExpiresAt > firstExpiry,
            $"{winningTeam} direct hero renewal resets the remaining duration");
        TestAssert.Equal(1, manaHero.Buffs.Count(buff =>
                buff.Name == "CrestoftheAncientGolem"),
            $"{winningTeam} direct hero renewal keeps one authoritative blue buff");

        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), lee.NetId),
            $"{winningTeam} Lee Sin direct acquisition reaches production transfer");
        var leeBlue = lee.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
        var leeAfter = runtime.HeroStats.Calculate(lee);
        // The energy twin of the sample above: Spells/CrestoftheAncientGolem.luaobj 0119-0150 multiplies the energy
        // pool by 0.015 and writes the same flat PAR regeneration row, so an energy hero's payload is that row too.
        TestAssert.Near(leeBefore.EnergyPool * 0.015, leeBlue.Payload.MpRegen, 1e-9,
            $"{winningTeam} energy blue regeneration payload is .015 of Lee Sin's energy pool");
        TestAssert.Near(leeBefore.EnergyPool * 0.015,
            leeAfter.EnergyRegen - leeBefore.EnergyRegen, 1e-9,
            $"{winningTeam} blue contributes .015 of Lee Sin's energy pool");
        TestAssert.Near(leeAfter.EnergyRegen, lee.ManaRegen, 1e-9,
            $"{winningTeam} Lee Sin runtime energy regen refreshes from the blue payload");

        var capture = new CaptureTransport();
        var observerId = 9500UL + (ulong)leeIndex;
        runtime.Delivery.AddObserver(new ReplicationObserver(
            observerId, lee.Team, lee.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        capture.Clear();
        runtime.HeroStats.Refresh(lee);
        runtime.HeroStats.Push(lee);
        runtime.Delivery.Flush();
        var expectedLee = runtime.HeroStats.Calculate(lee);
        var published = capture.Packets.Single(packet => IsHeroStats(packet, lee.NetId));
        TestAssert.Near(expectedLee.EnergyRegen, FloatAt(published, MpRegenOffset), 1e-5,
            $"{winningTeam} blue energy regen publishes in the owner stats row");

        var reconnect = Reconnect(runtime, fixture.Connection(leeIndex),
            (ulong)(leeIndex + 1), 6000 + leeIndex, observerId, capture);
        var reconnectStats = reconnect.Baseline.Single(packet => IsHeroStats(packet, lee.NetId));
        TestAssert.Near(expectedLee.EnergyRegen, FloatAt(reconnectStats, MpRegenOffset), 1e-5,
            $"{winningTeam} blue energy regen survives reconnect publication");
        TestAssert.True(reconnect.Baseline.Concat(reconnect.Ready).Concat(reconnect.State)
                .Concat(reconnect.Delivery)
                .Any(packet => HasBuff(packet, lee.NetId,
                    SpellPackets.BuffNameHash("CrestoftheAncientGolem"))),
            $"{winningTeam} reconnect retains the blue buff");

        // The pet branch is gated on the killer's own lineage marker: Spells/CrestoftheAncientGolem.luaobj counts
        // APBonusDamageToTowers on the attacker first (0233-0238) and takes the owner-resolution branch only when that
        // count is not zero (0341-0349). Batch 203: the shipped add puts that buff on a champion or Hallucinate's clone only, so a bare pet has none — the building rule it stands
        // for was MatchGameplayHooks.ModifyBasicAttackPreDamage before batch 203 — so a pet that carries no marker must not transfer. Reconstruction:
        // whether an ordinary retail pet carries the marker is not settled by the shipped data.
        var barePet = NewBlueBuffPet(runtime, winningTeam, manaHero.Position, manaHero.NetId,
            "BareBlueBuffPet", carriesLineageMarker: false);
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), barePet.NetId),
            $"{winningTeam} bare pet death reaches the transfer hook");
        TestAssert.True(ReferenceEquals(renewed,
                manaHero.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem")),
            $"{winningTeam} bare pet death does not resolve to the owner");

        // Pet kills resolve to the living owner but use BUFF_REPLACE_EXISTING.
        var pet = NewBlueBuffPet(runtime, winningTeam, manaHero.Position, manaHero.NetId,
            "BlueBuffPet", carriesLineageMarker: true);
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), pet.NetId),
            $"{winningTeam} pet acquisition reaches production transfer");
        var petReplacement = manaHero.Buffs.Single(buff => buff.Name == "CrestoftheAncientGolem");
        TestAssert.False(ReferenceEquals(renewed, petReplacement),
            $"{winningTeam} pet acquisition uses the replacement path");
        TestAssert.Equal(1, manaHero.Buffs.Count(buff =>
                buff.Name == "CrestoftheAncientGolem"),
            $"{winningTeam} pet acquisition keeps one owner blue buff");
        TestAssert.Near(manaBefore.MaxMp * 0.015,
            runtime.HeroStats.Calculate(manaHero).MpRegen - manaBefore.MpRegen, 1e-9,
            $"{winningTeam} pet replacement does not stack blue regeneration");

        // A dead or missing owner is rejected before any buff is added. Both stand-ins carry the lineage marker so the
        // refusal they exercise is the owner gate (CO_IS_NOT_DEAD at 0372, and the unresolved BBGetPetOwner at
        // 0350-0357), not the count gate above.
        deadOwner.Alive = false;
        var deadPet = NewBlueBuffPet(runtime, deadOwner.Team, deadOwner.Position, deadOwner.NetId,
            "DeadOwnerBlueBuffPet", carriesLineageMarker: true);
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), deadPet.NetId),
            $"{winningTeam} invalid-owner pet death reaches production transfer");
        TestAssert.False(deadOwner.Buffs.Any(buff =>
                buff.Name == "CrestoftheAncientGolem"),
            $"{winningTeam} dead pet owner does not receive blue");

        var missingOwnerPet = NewBlueBuffPet(runtime, winningTeam, manaHero.Position, 0xDEAD_BEEFu,
            "MissingOwnerBlueBuffPet", carriesLineageMarker: true);
        var blueCountBeforeMissingOwner = runtime.World.Heroes().Sum(hero =>
            hero.Buffs.Count(buff => buff.Name == "CrestoftheAncientGolem"));
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), missingOwnerPet.NetId),
            $"{winningTeam} missing-owner pet death reaches production transfer");
        TestAssert.True(runtime.World.Get(missingOwnerPet.OwnerNetId) is null,
            $"{winningTeam} missing pet owner remains absent from the world");
        TestAssert.Equal(blueCountBeforeMissingOwner,
            runtime.World.Heroes().Sum(hero =>
                hero.Buffs.Count(buff => buff.Name == "CrestoftheAncientGolem")),
            $"{winningTeam} missing pet owner does not receive blue");

        // Removal restores the pre-buff resource domain exactly.
        TestAssert.True(runtime.Buffs.RemoveBuff(lee, leeBlue.Slot),
            $"{winningTeam} blue removal succeeds for Lee Sin");
        TestAssert.Near(leeBefore.EnergyRegen,
            runtime.HeroStats.Calculate(lee).EnergyRegen, 1e-9,
            $"{winningTeam} blue removal restores Lee Sin energy regen");
        TestAssert.True(runtime.Buffs.RemoveBuff(manaHero, petReplacement.Slot),
            $"{winningTeam} blue removal succeeds for the mana hero");
        TestAssert.Near(manaBefore.MpRegen,
            runtime.HeroStats.Calculate(manaHero).MpRegen, 1e-9,
            $"{winningTeam} blue removal restores mana regeneration");
    }

    private static Entity NewBlueGolem(MatchRuntime runtime)
    {
        var golem = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, new Position2(3632.7, 7600.4))
        {
            Name = "AncientGolem",
            Alive = true,
            Created = true,
            Visible = true,
            Health = 1000.0,
            MaxHealth = 1000.0,
            CampId = 1,
        };
        runtime.World.Register(golem);
        // The crest is the character script's own now (batch 159 part 2), not a hand-built row: binding
        // CharScriptAncientGolem runs its UpdateSelfBuffActions (0000-0075), whose BBSpellBuffAdd puts
        // CrestoftheAncientGolem on the golem, so the death hook that transfers it is the buff script's own
        // BuffOnDeath. The hand-built BuffState this helper used to add carried no script instance at all.
        TestAssert.True(runtime.Spells.UnitScripts!.BindUnit(golem, "AncientGolem"),
            "the replacement AncientGolem binds its shipped character script");
        return golem;
    }

    /// <summary>
    /// A stand-in pet for the transfer's pet branch. The lineage marker is hand-built on purpose: the shipped gate
    /// counts <c>APBonusDamageToTowers</c> on the killer (Spells/CrestoftheAncientGolem.luaobj 0233-0238), and this
    /// engine places that buff only through the shipped add (batch 203): on a champion or the Hallucinate clone, never on a bare pet. Whether a retail pet carries it is a Reconstruction.
    /// </summary>
    private static Entity NewBlueBuffPet(MatchRuntime runtime, Team team, Position2 at, uint ownerNetId,
        string name, bool carriesLineageMarker)
    {
        var pet = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Pet,
            UnitType.Monster, team, at)
        {
            OwnerNetId = ownerNetId,
            Name = name,
            Created = true,
            Visible = true,
            Health = 100.0,
            MaxHealth = 100.0,
        };
        if (carriesLineageMarker)
            pet.Buffs.Add(new BuffState("APBonusDamageToTowers",
                type: BuffType.Internal, slot: 0, expiresAt: 25000.0,
                casterNetId: pet.NetId));
        runtime.World.Register(pet);
        return pet;
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
                $"Blue buff seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"Blue buff seat {index} reaches ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "Blue buff production fixture starts");
        return new ProductionFixture(runtime, connections);
    }

    private static ReconnectResult Reconnect(MatchRuntime runtime, long oldConnection,
        ulong playerId, long newConnection, ulong oldObserverId, CaptureTransport capture)
    {
        TestAssert.True(runtime.Session.Disconnect(oldConnection),
            $"Blue buff player {playerId} disconnects");
        TestAssert.True(runtime.Delivery.RemoveObserver(oldObserverId),
            $"Blue buff player {playerId} removes the detached observer");
        var participant = runtime.Session.AuthenticateReconnect(newConnection,
            runtime.Session.BuildKeyCheck(playerId));
        TestAssert.True(participant is not null,
            $"Blue buff player {playerId} reconnects");
        var handshake = runtime.Session.Receive(newConnection, 1, [0xC5]);
        TestAssert.True(handshake.Any(output => output.Packet.Plaintext.Span[0] == 0x57),
            "Blue buff reconnect receives the staged handshake");
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = participant!.SpawnAt ?? throw new InvalidOperationException(
            "Blue buff reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        var baseline = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var ready = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        TestAssert.True(participant.Spawned && participant.Loaded && participant.Ready,
            "Blue buff reconnect reaches ready");
        var state = runtime.Session.BuildReconnectMatchState(newConnection);
        var reconnectObserver = new ReplicationObserver(
            participant.PlayerId, participant.Team, participant.Hero!.NetId, capture,
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
            ?? throw new InvalidOperationException($"Blue buff hero {index} missing");

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
