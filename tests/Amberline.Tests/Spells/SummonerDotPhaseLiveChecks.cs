using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Production checks for the decoded SummonerDot BuffOnUpdate phase.
/// </summary>
public static class SummonerDotPhaseLiveChecks
{
    [Check("spells-live-summoner-dot-phase")]
    public static void SummonerDotDealsOnlyFromItsFirstBuffUpdate()
    {
        using var match = new LiveMatch();
        var caster = match.Hero(0);
        var target = match.Hero(1);
        var amount = 14.0 + 4.0 * (caster.Level - 1);
        var beforeDamage = target.MatchStatistics.TotalDamageTaken;

        match.Clear();
        TestAssert.True(match.CastSummoner(0, 1, target), "Ignite cast is accepted");
        var dot = target.Buffs.Single(buff => buff.Name == "SummonerDot");
        // Batch 212: the shipped BBExecutePeriodically is not capped (BbBaseLibrary.ExecutePeriodically runs every due time
        // inside the buff's life), so the payload records no pulse count. The five pulses are pinned by the damage below.
        TestAssert.False(dot.Payload.Has(BuffPayloadField.MaxTicks),
            "the shipped periodic carries no pulse cap: its five pulses are its own due times");
        TestAssert.Near(beforeDamage, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "accepted cast has no synchronous damage");
        TestAssert.Equal(0, match.DamagePackets(1, target.NetId),
            "accepted cast has no synchronous damage packet");

        match.Runtime.Tick(1.0 / 30.0);
        TestAssert.Near(beforeDamage + amount, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "first BuffOnUpdate applies one true-damage pulse");
        TestAssert.Equal(1, match.DamagePackets(1, target.NetId),
            "first BuffOnUpdate emits one damage packet");
        // Batch 142 part 2 (script = retail): the pulses are SummonerDot.luaobj's own BBExecutePeriodically, so its
        // clock is the script's InstanceVars.LastTimeExecuted and the engine ticker's TicksDone / PeriodElapsed never
        // move. Before the summoner scripts were bound this payload was filled by hand and BuffLifecycle ran the DoT,
        // which is what these two asserts used to peek at. The schedule the shipped blocks describe is what the payload
        // reports, and the health deltas above and below pin the pulse count.
        TestAssert.Near(1.0, dot.Payload.Period, 1e-9, "the script schedules one pulse a second");
        TestAssert.Near(amount, dot.Payload.DamagePerTick, 1e-9,
            "the script's per-tick amount reaches the payload");
        TestAssert.False(dot.Payload.Has(BuffPayloadField.TicksDone) ||
            dot.Payload.Has(BuffPayloadField.PeriodElapsed),
            "the engine ticker keeps neither a pulse count nor a clock for a script-scheduled DoT");

        for (var period = 0; period < 4; period++)
        {
            match.Clear();
            match.Runtime.Tick(1.0);
            TestAssert.Near(beforeDamage + amount * (period + 2),
                target.MatchStatistics.TotalDamageTaken, 1e-9,
                $"delayed pulse {period + 2} applies once");
            TestAssert.Equal(1, match.DamagePackets(1, target.NetId),
                $"delayed pulse {period + 2} emits one damage packet");
        }

        match.Clear();
        match.Runtime.Tick(0.95);
        TestAssert.Near(beforeDamage + amount * 5, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "no sixth pulse arrives before expiry");
        TestAssert.Equal(0, match.DamagePackets(1, target.NetId),
            "no sixth damage packet arrives before expiry");
        match.Runtime.Tick(0.1);
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "SummonerDot"),
            "SummonerDot expires at five seconds");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Internal_50MS"),
            "SummonerDot movement support expires with the DoT");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "GrievousWound"),
            "GrievousWound expires with the DoT");
        TestAssert.Near(beforeDamage + amount * 5, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "expiry does not add another pulse");
    }

    [Check("spells-live-summoner-dot-caster-death")]
    public static void SummonerDotContinuesAfterCasterDeath()
    {
        foreach (var reverseTeams in new[] { false, true })
        {
            using var match = new LiveMatch(reverseTeams);
            var caster = match.Hero(0);
            var target = match.Hero(1);
            var amount = 14.0 + 4.0 * (caster.Level - 1);

            TestAssert.True(match.CastSummoner(0, 1, target), "Ignite cast is accepted");
            match.Runtime.Tick(1.0 / 30.0);
            var beforeDeath = target.MatchStatistics.TotalDamageTaken;
            TestAssert.True(match.Runtime.Combat.Kill(caster, target.NetId),
                "caster death is accepted");
            for (var pulse = 0; pulse < 4; pulse++)
            {
                match.Runtime.Tick(1.0);
                TestAssert.Near(beforeDeath + amount * (pulse + 1),
                    target.MatchStatistics.TotalDamageTaken, 1e-9,
                    $"pulse {pulse + 2} survives caster death");
            }
            TestAssert.False(caster.Alive, "caster remains dead during the DoT");
        }
    }

    [Check("spells-live-summoner-dot-target-death")]
    public static void SummonerDotStopsDamageAndClearsOnTargetDeath()
    {
        foreach (var reverseTeams in new[] { false, true })
        {
            using var match = new LiveMatch(reverseTeams);
            var caster = match.Hero(0);
            var target = match.Hero(1);

            TestAssert.True(match.CastSummoner(0, 1, target), "Ignite cast is accepted");
            match.Runtime.Tick(1.0 / 30.0);
            var beforeDeath = target.MatchStatistics.TotalDamageTaken;
            TestAssert.True(match.Runtime.Combat.Kill(target, caster.NetId),
                "target death is accepted");
            // Shipped SummonerDot.luaobj has no PersistsThroughDeath field and
            // SummonerDot.inibin SpellData.Flags has bit 8 clear. The old
            // keep-until-expiry assertion reflected emulator behavior only.
            TestAssert.False(target.Buffs.Any(buff => buff.Name is
                "SummonerDot" or "Internal_50MS" or "GrievousWound"),
                "recipient death removes all three nonpersistent Ignite buffs");
            match.Clear();
            match.Runtime.Tick(4.9);
            TestAssert.Near(beforeDeath, target.MatchStatistics.TotalDamageTaken, 1e-9,
                "dead target receives no remaining periodic damage");
            TestAssert.Equal(0, match.DamagePackets(1, target.NetId),
                "dead target receives no remaining damage packet");
            match.Runtime.Tick(0.2);
            TestAssert.False(target.Buffs.Any(buff => buff.Name is
                "SummonerDot" or "Internal_50MS" or "GrievousWound"),
                "removed recipient Ignite buffs do not return at former expiry");
        }
    }

    [Check("spells-live-summoner-recipient-death-revive-reconnect")]
    public static void HarmfulSummonerBuffsEndWithRecipientLife()
    {
        string[] temporary = ["SummonerDot", "Internal_50MS", "GrievousWound",
            "ExhaustSlow", "ExhaustDebuff", "SummonerHaste", "SummonerBoost"];
        foreach (var reverseTeams in new[] { false, true })
        foreach (var useSummonerRevive in new[] { false, true })
        {
            using var match = new LiveMatch(reverseTeams);
            var caster = match.Hero(0);
            var target = match.Hero(1);
            caster.SummonerSpells[0] = "SummonerExhaust";
            target.SummonerSpells[1] = "SummonerHaste";
            TestAssert.True(match.CastSummoner(1, 0, target), "Cleanse arms its follow-up");
            TestAssert.True(match.CastSummoner(1, 1, target), "Ghost arms its speed buff");
            TestAssert.True(match.CastSummoner(0, 1, target), "Ignite attaches its recipient buffs");
            TestAssert.True(match.CastSummoner(0, 0, target), "Exhaust attaches its pair");
            // This alias explicitly persists in ReviveMarker.luaobj. It is a
            // positive owner-death control, not part of the harmful sweep.
            TestAssert.True(match.Runtime.SpellBuffs.Add(target, "ReviveMarker",
                BuffType.CombatEnhancer, 120.0, target.NetId,
                new BuffPayload { MaxHp = 400.0 }, canMitigateDuration: false) is not null);
            foreach (var name in temporary)
                TestAssert.True(target.Buffs.Any(buff => buff.Name == name),
                    $"{name} is active before recipient death");

            // A lethal production damage call invokes Kill and schedules the
            // ordinary respawn after the complete death/reward callback path.
            TestAssert.True(match.Runtime.Combat.ApplyHit(caster.NetId, target.NetId,
                target.Health + 1000.0, DamageType.True, DamageSourceKind.Spell));
            TestAssert.False(target.Alive);
            TestAssert.True(target.RespawnAt > 0.0, "lethal hit scheduled ordinary respawn");
            foreach (var name in temporary)
                TestAssert.False(target.Buffs.Any(buff => buff.Name == name),
                    $"recipient death removed nonpersistent {name}");
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "ReviveMarker"),
                "explicitly persistent Revive marker remains on its owner");

            if (useSummonerRevive)
            {
                target.SummonerSpells[0] = "SummonerRevive";
                target.SummonerCooldowns[0] = 0.0;
                TestAssert.True(match.CastSummoner(1, 0, target),
                    "dead-only Revive uses the authenticated cast boundary");
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
                    "Revive adds a new-life speed buff");
            }
            else TestAssert.True(match.Runtime.Combat.RespawnImmediately(target));
            TestAssert.True(target.Alive && target.RespawnAt == 0.0);
            foreach (var name in temporary)
                TestAssert.False(target.Buffs.Any(buff => buff.Name == name),
                    $"rapid respawn did not restore old-life {name}");

            TestAssert.True(match.Runtime.Session.Disconnect(2));
            TestAssert.True(match.Runtime.Delivery.RemoveObserver(2));
            var reconnected = match.Runtime.Session.AuthenticateReconnect(101,
                match.Runtime.Session.BuildKeyCheck(2));
            TestAssert.True(reconnected is not null);
            TestAssert.True(match.Runtime.Session.Receive(101, 1, [0xC5]).Any(output =>
                Opcode(output.Packet) == 0x57), "reconnect enters staged handshake");
            match.Runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
            var spawnAt = reconnected!.SpawnAt ?? throw new InvalidOperationException(
                "reconnect handshake did not arm spawn");
            match.Runtime.World.Clock.SetVirtual(spawnAt);
            match.Runtime.Session.RunSchedulers();
            match.Runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
            match.Runtime.Session.RunSchedulers();
            TestAssert.True(reconnected.Spawned && reconnected.Loaded && reconnected.Ready);
            match.Runtime.Delivery.AddObserver(new ReplicationObserver(
                reconnected.PlayerId, reconnected.Team, target.NetId, match.PacketsFor(1),
                ready: reconnected.Ready, view: reconnected.View,
                clientId: reconnected.ClientId));
            match.Clear();
            match.Runtime.Delivery.Flush();
            var buffs = match.PacketsFor(1).Packets.Where(packet =>
                Opcode(packet) == SpellPackets.BuffAddOpcode &&
                packet.Plaintext.Length >= 13 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId)
                .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]))
                .ToHashSet();
            TestAssert.False(temporary.Select(SpellPackets.BuffNameHash).Any(buffs.Contains),
                "reconnect does not replay old-life harmful summoner buffs");
            // The Revive marker is the engine's own add (no script BuffName reaches its add), so it keeps the binding name (batch 181).
            TestAssert.True(buffs.Contains(SpellPackets.BuffNameHash("ReviveMarker")),
                "reconnect retains explicitly persistent Revive marker");
            if (!useSummonerRevive)
                TestAssert.False(buffs.Contains(SpellPackets.BuffNameHash("SummonerReviveSpeedBoost")),
                    "ordinary respawn has no Revive speed effect");
            else
            {
                TestAssert.True(buffs.Contains(SpellPackets.BuffNameHash("SummonerReviveSpeedBoost")),
                    "Revive's new-life speed effect is visible on reconnect");
                TestAssert.True(match.Runtime.Combat.Kill(target, caster.NetId));
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
                    "a later recipient death retires Revive's nonpersistent speed buff");
                TestAssert.True(target.Buffs.Any(buff => buff.Name == "ReviveMarker"),
                    "the persistent marker survives the later death");
            }
        }
    }

    [Check("spells-live-summoner-dot-replace")]
    public static void SummonerDotReplacementStartsAFreshPeriodicClock()
    {
        using var match = new LiveMatch();
        var caster = match.Hero(0);
        var target = match.Hero(1);
        var amount = 14.0 + 4.0 * (caster.Level - 1);

        TestAssert.True(match.CastSummoner(0, 1, target), "first Ignite cast is accepted");
        match.Runtime.Tick(1.0 / 30.0);
        var previous = target.Buffs.Single(buff => buff.Name == "SummonerDot");
        caster.SummonerCooldowns[1] = 0.0;
        var beforeReplacement = target.MatchStatistics.TotalDamageTaken;
        match.Clear();
        TestAssert.True(match.CastSummoner(0, 1, target), "replacement Ignite cast is accepted");
        var replacement = target.Buffs.Single(buff => buff.Name == "SummonerDot");
        TestAssert.False(ReferenceEquals(previous, replacement),
            "BUFF_REPLACE_EXISTING creates a fresh live Dot instance");
        TestAssert.False(replacement.Payload.Has(BuffPayloadField.PeriodElapsed),
            "replacement resets the first-update marker");
        TestAssert.Equal(0, replacement.Payload.TicksDone,
            "replacement resets completed pulse count");
        TestAssert.Near(beforeReplacement, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "replacement cast has no synchronous damage");
        match.Runtime.Tick(1.0 / 30.0);
        TestAssert.Near(beforeReplacement + amount, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "replacement receives one first-update pulse");
    }

    /// <summary>
    /// Batch 551: a second caster's Ignite replaces the first one's on the same target (SummonerDot is a BUFF_REPLACE_EXISTING add; the
    /// replacement is by name, Reconstruction from the wiki's Ignite notes: a later Ignite overrides and refreshes, never stacks). The target
    /// holds one Dot, the second caster's, and burns for the second caster's five pulses only.
    /// </summary>
    [Check("spells-summoners-ignite-second-caster-replaces-the-first")]
    public static void SecondCastersIgniteReplacesTheFirst()
    {
        using var match = new LiveMatch(secondCaster: true);
        var first = match.Hero(0);
        var target = match.Hero(1);
        var second = match.Hero(2);
        var amount = 14.0 + 4.0 * (first.Level - 1);
        var before = target.MatchStatistics.TotalDamageTaken;

        TestAssert.True(match.CastSummoner(0, 1, target), "the first Ignite is accepted");
        match.Runtime.Tick(1.0 / 30.0);
        TestAssert.Near(before + amount, target.MatchStatistics.TotalDamageTaken, 1e-9, "the first Ignite's first pulse");
        TestAssert.True(match.CastSummoner(2, 1, target), "the second caster's Ignite is accepted");
        var dot = target.Buffs.Single(buff => buff.Name == "SummonerDot");
        TestAssert.Equal(second.NetId, dot.CasterNetId, "the target holds one Dot, the second caster's");
        // The pulse schedule of spells-live-summoner-dot-phase: the first update, four whole seconds, then past the five-second end.
        match.Runtime.Tick(1.0 / 30.0);
        for (var step = 0; step < 4; step++) match.Runtime.Tick(1.0);
        match.Runtime.Tick(0.95);
        match.Runtime.Tick(0.1);
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "SummonerDot"), "the Dot ends five seconds after the second cast");
        TestAssert.Near(before + amount * 6.0, target.MatchStatistics.TotalDamageTaken, 1e-9,
            "one pulse of the first Ignite, then the second Ignite's five");
    }

    [Check("spells-live-summoner-dot-banshee")]
    public static void IgniteIntoBansheeAddsItsDotAndLeavesTheVeil()
    {
        // SummonerDot.luaobj sets DoesntTriggerSpellCasts and no DoesntBreakShields, so the third branch of BuffOnBeingSpellHit in
        // BansheesVeil.luaobj does not pop for it (18 October 2026, P4): Ignite lands in full on a veiled hero.
        using var match = new LiveMatch();
        var target = match.Hero(1);
        // The Veil is the item's buff: the Dot's own GrievousWound add runs its shipped stats hook (batch 149 step 2), and a
        // script payload swap refreshes the target's stats — which removes Banshee's buffs from a hero who does not hold
        // item 3102 (HeroStatsService.ApplyEquipmentTransitions), the way spells-live-summoner-exhaust-banshee already reads it.
        target.Inventory = [new InventorySlot(3102), null, null, null, null, null];
        match.Runtime.SpellBuffs.Add(target, "BansheesVeil", BuffType.Aura, 10.0,
            target.NetId, canMitigateDuration: false);
        match.Clear();

        TestAssert.True(match.CastSummoner(0, 1, target), "Ignite against Banshee is accepted");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "SummonerDot"), "Ignite adds its Dot to a veiled hero");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BansheesVeil"), "and the Veil stays");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "without starting its replacement timer");
    }

    [Check("spells-live-summoner-dot-banshee-single-shield-break")]
    public static void IgniteIntoBansheeDrawsNoShieldBreakEffect()
    {
        // Ignite does not pop the Veil (see above), so neither the proc effect nor the replacement timer is announced.
        using var match = new LiveMatch();
        var target = match.Hero(1);
        // As above: the veiled hero carries item 3102, so the Dot's stats refresh cannot strip the Veil the check is about.
        target.Inventory = [new InventorySlot(3102), null, null, null, null, null];
        match.Runtime.SpellBuffs.Add(target, "BansheesVeil", BuffType.Aura, 10.0,
            target.NetId, canMitigateDuration: false);
        match.Clear();

        TestAssert.True(match.CastSummoner(0, 1, target), "Ignite against Banshee is accepted");
        TestAssert.Equal(0, target.Buffs.Count(buff => buff.Name == "BansheesVeilTimer"), "no replacement timer on the unit");
        foreach (var seat in new[] { 0, 1 })
        {
            var packets = match.PacketsFor(seat).Packets;
            TestAssert.Equal(0, packets.Count(packet => Opcode(packet) == 0x8C && packet.Plaintext.Length >= 10 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                    SpellPackets.BuffNameHash("SpellEffect_proc")),
                $"seat {seat}: no shield-break effect");
        }
    }

    [Check("spells-live-summoner-exhaust-banshee")]
    public static void ExhaustIntoBansheeAddsBothBuffsAndNoWindupIsCancelled()
    {
        // SummonerExhaust.luaobj sets DoesntTriggerSpellCasts and no DoesntBreakShields (like Ignite), so it does not pop the Veil, and
        // its only BBCancelAutoAttack sits in an OnBuffActivate that never runs.
        using var match = new LiveMatch();
        var caster = match.Hero(0);
        var target = match.Hero(1);
        caster.SummonerSpells[1] = "SummonerExhaust";
        // The Veil belongs to the item: ExhaustDebuff refreshes the target's stats, and a refresh removes Banshee's buffs from a hero who does
        // not hold item 3102 (HeroStatsService.ApplyEquipmentTransitions), so the veiled target carries the item as in play.
        target.Inventory = [new InventorySlot(3102), null, null, null, null, null];
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(target, caster));
        match.Runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == target.NetId),
            "the victim has an unlaunched basic attack");
        match.Runtime.SpellBuffs.Add(target, "BansheesVeil", BuffType.Aura, 10.0,
            target.NetId, canMitigateDuration: false);
        match.Clear();

        TestAssert.True(match.CastSummoner(0, 1, target), "Exhaust against Banshee is accepted");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "ExhaustSlow") &&
            target.Buffs.Any(buff => buff.Name == "ExhaustDebuff"), "a veiled hero gets both Exhaust buffs");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BansheesVeil"), "and keeps the Veil");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "with no replacement timer");
        TestAssert.Near(210.0, caster.SummonerCooldowns[1], 1e-9, "the cast spends its cooldown");
        TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == target.NetId),
            "Exhaust does not cancel the victim's unlaunched attack");
    }

    [Check("spells-live-summoner-dot-cleanse-policy")]
    public static void CleanseKeepsDamageOverTimeAndRemovesOrdinaryControl()
    {
        using var match = new LiveMatch();
        var caster = match.Hero(0);
        var target = match.Hero(1);

        TestAssert.True(match.CastSummoner(0, 1, target), "Ignite cast is accepted");
        var snare = match.Runtime.SpellBuffs.Add(target, "CleanseSnare", BuffType.Snare,
            5.0, caster.NetId);
        TestAssert.True(snare is not null, "ordinary control fixture is added");
        match.Clear();
        TestAssert.True(match.CastSummoner(1, 0, target), "Cleanse cast is accepted");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "SummonerDot"),
            "Cleanse leaves the damage-over-time buff in place");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "GrievousWound"),
            "Cleanse leaves the GrievousWound dehancer in place");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "CleanseSnare"),
            "Cleanse still removes ordinary removable control");
    }

    private static byte Opcode(PlaintextPacket packet) =>
        packet.Plaintext.Length == 0 ? (byte)0 : packet.Plaintext.Span[0];

    private sealed class LiveMatch : IDisposable
    {
        private readonly List<PacketCapture> _captures = [];

        public LiveMatch(bool reverseTeams = false, bool secondCaster = false)
        {
            var casterTeam = reverseTeams ? Team.Chaos : Team.Order;
            var targetTeam = reverseTeams ? Team.Order : Team.Chaos;
            var roster = new[]
            {
                new SessionParticipantDefinition(1, 0, "Caster", "Annie", casterTeam,
                    SummonerSpell1: "SummonerFlash", SummonerSpell2: "SummonerDot"),
                new SessionParticipantDefinition(2, 1, "Target", "Annie", targetTeam,
                    SummonerSpell1: "SummonerBoost", SummonerSpell2: "SummonerFlash"),
            };
            if (secondCaster)
                roster = [.. roster, new SessionParticipantDefinition(3, 2, "Caster2", "Annie", casterTeam,
                    SummonerSpell1: "SummonerFlash", SummonerSpell2: "SummonerDot")];
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                readySettleSeconds: 0.0, roster: roster);
            Runtime.World.Clock.SetVirtual(0.0);
            Runtime.Session.CreateWorld();
            for (var index = 0; index < roster.Length; index++)
            {
                var connection = index + 1;
                TestAssert.True(Runtime.Session.Authenticate(connection,
                    Runtime.Session.BuildKeyCheck((ulong)connection)) is not null,
                    "production seat authenticates");
                Runtime.Session.Spawn(connection);
                TestAssert.True(Runtime.Session.MarkReady(connection), "production seat ready");
                var participant = Runtime.Session.Participants[index];
                var hero = participant.Hero!;
                hero.HeroAutoAcquire = false;
                hero.Position = new Position2(7000.0 + index * 250.0, 7000.0);
                for (var slot = 0; slot < 4; slot++) hero.SpellRanks[slot] = 1;
                hero.MaxHealth = hero.Health = 5000.0;
                var capture = new PacketCapture();
                _captures.Add(capture);
                Runtime.Delivery.AddObserver(new ReplicationObserver(
                    participant.PlayerId, participant.Team, hero.NetId,
                    capture, ready: true, view: participant.View));
            }
            TestAssert.True(Runtime.Session.Start(), "production test match starts");
            Runtime.Delivery.Flush();
            Clear();
        }

        public MatchRuntime Runtime { get; }
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!;
        public PacketCapture PacketsFor(int index) => _captures[index];

        public bool CastSummoner(int seat, int slot, Entity target)
        {
            var request = new byte[34];
            request[0] = 0xA2;
            request[5] = checked((byte)((slot << 1) | 1));
            var source = Hero(seat);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), source.NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            return accepted;
        }

        public int DamagePackets(int observerSeat, uint targetNetId) =>
            _captures[observerSeat].Packets.Count(packet =>
                Opcode(packet) == 0x68 && packet.Plaintext.Length >= 10 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == targetNetId);

        public void Clear()
        {
            foreach (var capture in _captures) capture.Packets.Clear();
        }

        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    private sealed class PacketCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
