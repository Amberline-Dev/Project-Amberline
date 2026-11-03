using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Items;

/// <summary>
/// Small production-graph acceptance checks for item lifecycle, resource-domain,
/// and reconnect behavior.  These deliberately use the in-process match graph;
/// native client rendering remains outside this check boundary.
/// </summary>
public static class ItemAcceptanceFollowupChecks
{
    private const int MpRegenOffset = 91;
    private const int CooldownOffset = 115;

    [Check("items-live-b4-mana-only-restoration")]
    public static void ManaItemsPreserveEnergyAndHealthEffects()
    {
        foreach (var champion in new[] { "LeeSin", "Annie" })
        foreach (var item in new[] { 2003, 2004, 3010, 3027, 3028, 3026 })
        {
            var fixture = StartProduction(
            [
                new SessionParticipantDefinition(1, 0, "Item carrier", champion, Team.Order),
                new SessionParticipantDefinition(2, 1, "Opponent", "Orianna", Team.Chaos),
            ]);
            using var runtime = fixture.Runtime;
            var hero = fixture.Hero(0);
            var enemy = fixture.Hero(1);
            hero.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(9000, 9000);
            hero.HeroAutoAcquire = enemy.HeroAutoAcquire = false;
            hero.Inventory = [new(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero);
            hero.Health = hero.MaxHealth - 100;
            hero.Mana = 50;
            var usesMana = champion == "Annie";
            var context = $"{champion}/{item}";
            if (item is 2003 or 2004)
            {
                var request = new byte[34];
                request[0] = 0xA2;
                request[5] = 4 << 1;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
                bool Use() => runtime.Dependencies.Gameplay(new SessionGameplayContext(
                    runtime.Session.Participants[0], hero, 1, request, null, null));
                // Batch 149 step 3: the use gate is the shipped script's own CanCastBuildingBlocks now —
                // Spells/RegenerationPotion.luaobj refuses above 99 % health (GetHealthPercent) and
                // Spells/FlaskOfCrystalWater.luaobj above 99 % mana (GetPARPercent of PAR_MANA) — and a refusal
                // lands before the inventory consume, so the charge stays. The hand-written resource gate (2004
                // answered "full_mana" for a hero with no mana pool) is not in the script and is gone:
                // GetPARPercent answers 0 for a pool the unit does not carry (AB 2649241 to 2649283), so an
                // energy hero is never at full mana and the script accepts its drink.
                if (item == 2003 || usesMana)
                {
                    if (item == 2003) hero.Health = hero.MaxHealth;
                    else hero.Mana = hero.MaxMana;
                    TestAssert.False(Use(), context + " the script CanCast refuses");
                    TestAssert.Equal(item, hero.Inventory[0]!.ItemId, "a refused use keeps the charge");
                    hero.Health = hero.MaxHealth - 100;
                    hero.Mana = 50;
                }
                TestAssert.True(Use(), context + " the script CanCast accepts the drink");
                TestAssert.True(hero.Inventory[0] is null, context + " the accepted drink spends its charge");
                var health = hero.Health;
                // Batch 149 step 3: the restore is Spells/Potion_Internal.luaobj's own one-second
                // BBExecutePeriodically (BBIncHealth +10 while a health potion row is up, BBIncPAR +5 for a mana
                // one) and not a payload row a coarse buff tick catches up. The spell phase advances that clock
                // and the buff phase runs the hook, the way a match's steps do: the drink's next update seeds it
                // (ExecuteImmediately false), the two seconds after it fire the two ticks these numbers read.
                // BBIncPAR picks the pool by its PARType and leaves a unit without that pool alone, so the energy
                // hero's drunk mana potion is consumed and restores nothing at all.
                runtime.Spells.Tick(1.0 / 30);
                runtime.Buffs.TickBuffs(hero, 1.0 / 30);
                runtime.Spells.Tick(1.0);
                runtime.Buffs.TickBuffs(hero, 1.0);
                runtime.Spells.Tick(1.0);
                runtime.Buffs.TickBuffs(hero, 1.0);
                TestAssert.Near(item == 2004 && usesMana ? 60 : 50, hero.Mana, 1e-9, context);
                TestAssert.Near(health + (item == 2003 ? 20 : 0), hero.Health, 1e-9, context + " health");
            }
            else if (item is 3010 or 3027)
            {
                // Batch 149 part 2: the level-up restore is the shipped script's own now. Items/3010.luaobj's
                // UpdateSelfBuffActions (and Items/3027.luaobj's, beside its sixty-second ageing periodic) reads
                // BBGetLevel into TempLevel and, past the OwnerLevel its OnActivate stored, adds
                // Spells/CatalystHeal.luaobj's own 8.5 s row. That row's BuffOnUpdateStats is a half-second
                // BBExecutePeriodically of BBIncHealth 15.625 and BBIncPAR 12.5 PAR_MANA (a pool the hero may not
                // carry stays untouched), and it is sampled on the stats hook's 0.25 s grid. So the copy binds on
                // the next spell update, the hero really levels up, and the two half-second steps below are the
                // second those numbers read. The hand-written OnLevelChanged restore is gone.
                runtime.Spells.Tick(1.0 / 30);
                var health = hero.Health;
                hero.Level += 1;
                // Batch 246: 3010's UpdateSelfBuffActions is bare, so it reads the level on the hero's next 0.25 s self-buff line
                // (BbSelfUpdate), which one quarter-second step always reaches; 3027's carries its ageing periodic and runs every update.
                runtime.Spells.Tick(item == 3010 ? 0.25 : 1.0 / 30);
                runtime.Spells.Tick(0.5);
                runtime.Buffs.TickBuffs(hero, 0.5);
                runtime.Spells.Tick(0.5);
                runtime.Buffs.TickBuffs(hero, 0.5);
                TestAssert.Near(usesMana ? 75 : 50, hero.Mana, 1e-9, context + " resource ticks");
                TestAssert.Near(health + 31.25, hero.Health, 1e-9, context + " health ticks retained");
            }
            else if (item == 3028)
            {
                // Batch 149 part 2: the missing-mana half is Spells/ChaliceOfHarmony.luaobj's own
                // BuffOnUpdateStats — BBGetPAROrHealth GetPARPercent of PAR_MANA into PercentMana, BBMath's
                // 1 - PercentMana into PercentMissing, then BBIncPercentPARRegenMod of that — and
                // Items/3028.luaobj keeps the row alive (its OnActivate opens the ten-second ChaliceOfHarmony
                // window, its nine-second UpdateSelfBuffStats periodic renews it). The mod is PAR_MANA-only, so
                // a hero without that pool keeps its plain regen. Script = retail: the percent reaches this tick
                // as hero.ManaRegen (HeroStats scales the pool's regen by 1 + the row), so it is asserted on the
                // regen and the tick adds exactly that number once — the hand-written missing-fraction scale
                // that stood in for the script is gone (CombatService.Regenerate) and is not applied a second
                // time here. The fraction reaches the regen at float precision (5/6 reads back as
                // 0.8333333134651184), hence 1e-6 on the scaled regen and 1e-9 on the tick itself.
                var plainRegen = hero.ManaRegen;
                runtime.Spells.Tick(1.0 / 30);
                runtime.Buffs.TickBuffs(hero, 0.5);
                runtime.HeroStats.Refresh(hero);
                var scaled = plainRegen *
                    (usesMana ? 1 + (hero.MaxMana - hero.Mana) / hero.MaxMana : 1);
                TestAssert.Near(scaled, hero.ManaRegen, 1e-6, context + " Chalice scales the pool regen");
                var regen = hero.ManaRegen;
                runtime.Combat.CombatTick(1);
                TestAssert.Near(50 + regen, hero.Mana, 1e-9, context + " Chalice missing-mana multiplier");
            }
            else
            {
                // Batch 149: the copy binds on the next spell update (BbItemModule.SyncInventory) and only then does
                // Items/3026.luaobj keep WillRevive alive, whose BuffOnPreDamage is the revival that leaves the row
                // below behind (the same latency the other guardian checks account for).
                runtime.Spells.Tick(1.0 / 30);
                runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
                TestAssert.Near(usesMana ? 0 : 50, hero.Mana, 1e-9, context + " activation");
                TestAssert.Near(1, hero.Health, 1e-9);
                runtime.Buffs.TickBuffs(hero, 4);
                TestAssert.Near(usesMana ? Math.Min(hero.MaxMana, 375) : 50,
                    hero.Mana, 1e-9, context + " revival");
                TestAssert.Near(Math.Min(hero.MaxHealth, 751), hero.Health, 1e-9,
                    context + " health restoration retained");
            }
        }
    }

    [Check("items-live-sunfire-combat-kill-reconnect")]
    public static void SunfireCombatKillStopsDamageAndReconnectHasNoStaleBuff()
    {
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Sunfire owner", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Sunfire target", "Orianna", Team.Chaos),
            new SessionParticipantDefinition(3, 2, "Sunfire killer", "LeeSin", Team.Chaos),
        ]);
        var runtime = fixture.Runtime;
        var owner = fixture.Hero(0);
        var target = fixture.Hero(1);
        var killer = fixture.Hero(2);

        owner.Position = new Position2(7000.0, 7000.0);
        target.Position = new Position2(7050.0, 7000.0);
        killer.Position = new Position2(9000.0, 9000.0);
        owner.Inventory = [new InventorySlot(3068), null, null, null, null, null];
        runtime.HeroStats.Refresh(owner);

        var targetHealthBeforeBurn = target.Health;
        var damageTakenBeforeBurn = target.MatchStatistics.TotalDamageTaken;
        runtime.Tick(0.05);
        TestAssert.True(owner.Buffs.Any(buff => buff.Name == "SunfireCloakParticle"),
            "production Sunfire tick creates the owner particle");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "SunfireCapeAura"),
            "production Sunfire tick marks the nearby enemy");
        var targetHealthAfterBurn = target.Health;
        var damageTakenAfterBurn = target.MatchStatistics.TotalDamageTaken;
        TestAssert.True(targetHealthAfterBurn < targetHealthBeforeBurn,
            "production Sunfire tick actually damages the nearby enemy");
        TestAssert.True(damageTakenAfterBurn > damageTakenBeforeBurn,
            "production Sunfire damage is recorded on the target");

        TestAssert.True(runtime.Combat.Kill(owner, killer.NetId),
            "production Combat.Kill accepts the Sunfire owner's death");
        TestAssert.False(owner.Alive, "Combat.Kill marks the Sunfire owner dead");
        TestAssert.False(owner.Buffs.Any(buff => buff.Name == "SunfireCloakParticle"),
            "Combat.Kill death hook removes Sunfire immediately");

        var damageTakenAfterDeath = target.MatchStatistics.TotalDamageTaken;
        runtime.Tick(1.0);
        TestAssert.Equal(damageTakenAfterDeath, target.MatchStatistics.TotalDamageTaken,
            "a dead Sunfire owner cannot deal damage on later production ticks");

        var reconnect = Reconnect(runtime, fixture.Connection(0), 1, 201);
        var reconnectPackets = reconnect.Baseline
            .Concat(reconnect.Ready)
            .Concat(reconnect.State)
            .ToArray();
        TestAssert.True(reconnect.Baseline.Any(packet => IsHeroStats(packet, owner.NetId)),
            "fresh dead-owner reconnect includes the owner's decoded stats snapshot");
        // Batch 357 (restates batch 181): the buff's add carries its binding SunfireCloakParticle; Sunfire Cape Self has no script.
        TestAssert.False(reconnectPackets.Any(packet => HasBuff(packet, owner.NetId,
                SpellPackets.BuffNameHash("SunfireCloakParticle"))),
            "fresh dead-owner reconnect packets contain no stale Sunfire particle");

        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            9001, owner.Team, owner.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        TestAssert.True(capture.Packets.Any(packet => IsHeroStats(packet, owner.NetId)),
            "fresh owner observer receives a decoded stats packet");
        TestAssert.False(capture.Packets.Any(packet => HasBuff(packet, owner.NetId,
                SpellPackets.BuffNameHash("SunfireCloakParticle"))),
            "fresh owner's decoded buff snapshot contains no stale Sunfire particle");
    }

    [Check("items-live-mana-aura-energy-reconnect")]
    public static void ManaAuraKeepsLeeEnergyClarityAndReconnectStats()
    {
        var energyRunes = EnergyRunePage();
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Aura source", "Annie", Team.Order,
                SummonerSpell1: "SummonerMana", SummonerSpell2: "SummonerDot"),
            new SessionParticipantDefinition(2, 1, "Mana ally", "Orianna", Team.Order),
            new SessionParticipantDefinition(3, 2, "Energy ally", "LeeSin", Team.Order,
                Runes: energyRunes),
            new SessionParticipantDefinition(4, 3, "Enemy", "Annie", Team.Chaos),
        ]);
        var runtime = fixture.Runtime;
        var source = fixture.Hero(0);
        var manaAlly = fixture.Hero(1);
        var lee = fixture.Hero(2);
        var enemy = fixture.Hero(3);

        source.Position = new Position2(7000.0, 7000.0);
        manaAlly.Position = new Position2(7050.0, 7000.0);
        lee.Position = new Position2(7100.0, 7000.0);
        enemy.Position = new Position2(10000.0, 10000.0);
        source.Inventory = [new InventorySlot(3037), new InventorySlot(3099),
            null, null, null, null];
        runtime.HeroStats.Refresh(source);
        // Batch 145 step 2: Items/3037.luaobj and Items/3099.luaobj re-add their auras from BBExecutePeriodically
        // (1.0 s, ExecuteImmediately false), so the copies are a period old here rather than the first sync tick's.
        runtime.Tick(1.15);

        var data = runtime.GameData;
        var items = new ItemCatalog(data.Paths);
        var baseMana = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Orianna", manaAlly.Level, Inventory.Empty()));
        var baseEnergy = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("LeeSin", lee.Level, Inventory.Empty(), runes: energyRunes));
        TestAssert.True(lee.Buffs.Any(buff => buff.Name == "ManaManipulatorAuraFriend"),
            "production Mana Manipulator reaches Lee Sin");
        TestAssert.True(lee.Buffs.Any(buff => buff.Name == "SoulShroudAuraFriend"),
            "production Soul Shroud reaches Lee Sin");
        // The resource gate is the scripts' own accessor now (BBIncFlatPARRegenMod with PARType = PAR_MANA refuses
        // a non-mana target), so Lee Sin's copies carry no MpRegen field at all. The hand-written rows tagged the
        // payload ResourceDomain = Mana instead and let the calculator drop it.
        TestAssert.True(lee.Buffs.Where(buff => buff.Name is "ManaManipulatorAuraFriend" or
                "SoulShroudAuraFriend")
            .All(buff => buff.Payload.MpRegen == 0.0),
            "Lee Sin's production aura copies carry no PAR_MANA regen");
        TestAssert.Near(baseEnergy.EnergyRegen, lee.ManaRegen, 1e-9,
            "mana-only aura regen does not leak into Lee Sin energy regen");
        TestAssert.Near(baseEnergy.CooldownMod - 0.10, lee.CooldownMod, 1e-9,
            "Soul Shroud cooldown reduction applies to Lee Sin");
        // Batch 145 step 2: the host's shared number getter is float32 (BbNativeHandlers.Sum, the client's own
        // number getter), so BBIncFlatPARRegenMod's deltas arrive as 1.44000006 and 2.40000010 and their sum is off
        // the decimal 3.84 in the seventh digit. The script's numbers are still 1.44 and 2.4.
        TestAssert.Near(baseMana.MpRegen + 1.44 + 2.4, manaAlly.ManaRegen, 1e-6,
            "mana ally receives both production aura regen values");

        lee.Mana = Math.Min(50.0, Math.Max(0.0, lee.MaxMana - 1.0));
        var leeEnergyBeforeClarity = lee.Mana;
        TestAssert.True(leeEnergyBeforeClarity < lee.MaxMana,
            "Clarity fixture starts Lee Sin below his energy cap");
        source.Mana = 0.0;
        manaAlly.Mana = 0.0;
        var clarity = new SpellCastRequest(0, true,
            new Position3(source.Position.X, 0.0, source.Position.Z),
            new Position3(source.Position.X, 0.0, source.Position.Z), 0);
        TestAssert.True(runtime.Spells.HandleCastRequest(source, clarity,
                runtime.Combat.CurrentTime), "production Clarity cast is accepted");
        var clarityAmount = 160.0 + 30.0 * source.Level;
        TestAssert.Near(clarityAmount, source.Mana, 1e-9,
            "Clarity restores the source's full production amount");
        TestAssert.Near(clarityAmount * 0.5, manaAlly.Mana, 1e-9,
            "Clarity restores half amount to the allied mana hero");
        TestAssert.Equal(leeEnergyBeforeClarity, lee.Mana,
            "Clarity does not restore the allied Lee Sin energy resource");
        TestAssert.True(source.SummonerCooldowns[0] > 0.0,
            "production Clarity consumes its summoner cooldown");

        var expectedResource = runtime.HeroStats.Calculate(lee);
        var reconnect = Reconnect(runtime, fixture.Connection(2), 3, 203);
        var baselineStats = reconnect.Baseline.Single(packet =>
            IsHeroStats(packet, lee.NetId));
        AssertDecodedResourceStats(baselineStats, expectedResource,
            "fresh Lee Sin reconnect 0xCC");

        var refreshPayload = new byte[5];
        refreshPayload[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(refreshPayload.AsSpan(1), lee.NetId);
        var refresh = runtime.Session.BuildClassicHeroStatsRefreshBurst(203, refreshPayload);
        TestAssert.True(refresh.Any(packet => packet.Plaintext.Length >= 5 &&
                packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == lee.NetId),
            "owner-only 0x59 refresh returns the reconnected Lee Sin row");
        runtime.HeroStats.Refresh(lee);
        TestAssert.Near(expectedResource.EnergyRegen, lee.ManaRegen, 1e-9,
            "stat refresh preserves Lee Sin energy regeneration after reconnect");
        TestAssert.Near(expectedResource.CooldownMod, lee.CooldownMod, 1e-9,
            "stat refresh preserves Soul Shroud cooldown after reconnect");

        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            9002, lee.Team, lee.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        TestAssert.True(capture.Packets.Any(packet => IsHeroStats(packet, lee.NetId)),
            "fresh Lee Sin observer receives a decoded stats packet");
        var freshStats = capture.Packets.Single(packet => IsHeroStats(packet, lee.NetId));
        AssertDecodedResourceStats(freshStats, expectedResource,
            "fresh Lee Sin replication snapshot");
    }

    private static void AssertDecodedResourceStats(PlaintextPacket packet,
        HeroStats expected, string context)
    {
        TestAssert.Near(expected.MpRegen, FloatAt(packet, MpRegenOffset), 1e-5, context + " energy regen");
        TestAssert.Near(expected.CooldownMod, FloatAt(packet, CooldownOffset), 1e-5, context + " cooldown");
    }

    private static float FloatAt(PlaintextPacket packet, int offset) =>
        BitConverter.ToSingle(packet.Plaintext.Span[offset..(offset + 4)]);

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

    private static RunePage EnergyRunePage()
    {
        var catalog = new RuneCatalog(GameData.Default.Paths);
        return RunePage.Create(catalog,
            red: [5026, 5028], blue: [5371, 5372], yellow: [5369, 5370],
            black: [5373, 5374]);
    }

    internal static ProductionFixture StartProduction(
        IReadOnlyList<SessionParticipantDefinition> roster,
        Amberline.Core.IRandomSource? random = null)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, random: random);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        var connections = new long[roster.Count];
        for (var index = 0; index < roster.Count; index++)
        {
            var connection = 100L + index;
            connections[index] = connection;
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"production seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"production seat {index} reaches ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "production item fixture starts");
        return new ProductionFixture(runtime, connections);
    }

    private static ReconnectResult Reconnect(MatchRuntime runtime, long oldConnection,
        ulong playerId, long newConnection)
    {
        TestAssert.True(runtime.Session.Disconnect(oldConnection),
            $"player {playerId} disconnects for reconnect");
        var participant = runtime.Session.AuthenticateReconnect(newConnection,
            runtime.Session.BuildKeyCheck(playerId));
        TestAssert.True(participant is not null, $"player {playerId} authenticates reconnect");
        var handshake = runtime.Session.Receive(newConnection, 1, [0xC5]);
        TestAssert.True(handshake.Any(output => output.Packet.Plaintext.Span[0] == 0x57),
            "reconnect receives the staged version handshake");
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = participant!.SpawnAt ??
            throw new InvalidOperationException("reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        var baseline = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        runtime.Session.Receive(newConnection, 1, [0xC6, 0, 0, 0, 0]);
        var ready = runtime.Session.RunSchedulers()
            .Where(output => output.ConnectionId == newConnection)
            .Select(output => output.Packet).ToArray();
        TestAssert.True(participant.Spawned && participant.Loaded && participant.Ready,
            "reconnect reaches the ready boundary");
        var state = runtime.Session.BuildReconnectMatchState(newConnection);
        return new ReconnectResult(baseline, ready, state);
    }

    internal sealed record ProductionFixture(MatchRuntime Runtime, long[] Connections)
    {
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!
            ?? throw new InvalidOperationException($"hero {index} missing");

        public long Connection(int index) => Connections[index];
    }

    private sealed record ReconnectResult(
        IReadOnlyList<PlaintextPacket> Baseline,
        IReadOnlyList<PlaintextPacket> Ready,
        IReadOnlyList<PlaintextPacket> State);

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
