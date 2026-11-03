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
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Items;

/// <summary>
/// Production acceptance for marker-owned item-slow contributions. Every slow
/// is produced by CombatService.ApplyHit or the YorickScriptSupport ghoul
/// fixture; no synthetic item-slow payload is installed.
/// <para>
/// Script wins (batch 147): <c>Spells/ItemSlow.luaobj</c>'s <c>BuffOnUpdateStats</c> is a
/// <c>BBIfHasBuff</c> cascade over the self-bound Internal_50Slow … Internal_15Slow rows that applies
/// <c>IncPercentMultiplicativeMovementSpeedMod</c> to the marker's own holder, so the penalty lives on the
/// <c>ItemSlow</c> marker and the rows stay empty. The old check asserted the penalty on the strongest row;
/// it now asserts the shipped shape (the marker carries the strongest row's magnitude, every row is at 0).
/// </para>
/// </summary>
public static class ItemSlowMarkerOwnershipLiveChecks
{
    [Check("items-live-item-slow-marker-ownership")]
    public static void ItemSlowMarkerOwnsInternalContribution()
    {
        foreach (var attackingTeam in new[] { Team.Order, Team.Chaos })
            RunTeam(attackingTeam);
    }

    private static void RunTeam(Team attackingTeam)
    {
        var defendingTeam = attackingTeam == Team.Order ? Team.Chaos : Team.Order;
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "SlowCaster", "LeeSin", attackingTeam),
            new SessionParticipantDefinition(2, 1, "SlowAreaCaster", "Annie", attackingTeam),
            new SessionParticipantDefinition(3, 2, "SlowTarget", "Orianna", defendingTeam),
            new SessionParticipantDefinition(4, 3, "GhoulOwner", "Yorick", defendingTeam),
        ]);
        var runtime = fixture.Runtime;
        foreach (var hero in runtime.World.Heroes()) hero.HeroAutoAcquire = false;
        var source = fixture.Hero(0);
        var areaSource = fixture.Hero(1);
        var target = fixture.Hero(2);
        var ghoulOwner = fixture.Hero(3);
        source.Position = areaSource.Position = target.Position = ghoulOwner.Position =
            new Position2(7000.0, 7000.0);
        source.Inventory = [new InventorySlot(3116), null, null, null, null, null];
        areaSource.Inventory = [new InventorySlot(3116), null, null, null, null, null];
        // Batch 147 step 4: Items/3116.luaobj runs its slow from its own ItemOnPreDealDamage /
        // ItemOnDealDamage now, so the copy has to be bound to the slot first (BbItemModule.SyncInventory,
        // the same next-spell-update latency the 3022 cases below already account for).
        runtime.Tick(0.01);
        target.Inventory = [new InventorySlot(3140), null, null, null, null, null];
        target.MaxHealth = target.Health = 100000.0;
        target.Armor = target.SpellBlock = 0.0;
        runtime.HeroStats.Refresh(source, push: false);
        runtime.HeroStats.Refresh(areaSource, push: false);
        runtime.HeroStats.Refresh(target, push: false);

        var capture = AttachCapture(runtime, target);
        capture.Clear();
        var baseSpeed = target.Speed;

        // Ordinary targets retain the real 3116 marker and the shipped name
        // cascade picks the strongest internal row across single-target and
        // area spell damage.  These calls enter OnSpellDamageDealt through
        // combat hooks.
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.Spell),
            $"{attackingTeam} 3116 single-target damage lands");
        runtime.Delivery.Flush();
        var strong = target.Buffs.Single(buff => buff.Name == "Internal_35Slow");
        var marker = target.Buffs.Single(buff => buff.Name == "ItemSlow");
        // Script wins: the marker carries the penalty (Spells/ItemSlow.luaobj), the row is empty.
        TestAssert.Near(-0.35, marker.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} ordinary target accepts 3116 single-target slow on the marker");
        TestAssert.Near(0.0, strong.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} the Internal_35Slow row itself carries no penalty");
        var speedWithStrong = target.Speed;

        TestAssert.True(runtime.Combat.ApplyHit(areaSource.NetId, target.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.SpellAoe),
            $"{attackingTeam} 3116 area damage lands");
        runtime.Delivery.Flush();
        var weak = target.Buffs.Single(buff => buff.Name == "Internal_15Slow");
        TestAssert.Near(-0.35, marker.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} strongest ordinary slow remains selected on the marker");
        TestAssert.Near(0.0, weak.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} weaker area row contributes no movement penalty");
        TestAssert.Near(speedWithStrong, target.Speed, 1e-9,
            $"{attackingTeam} area overlap preserves effective speed");

        // The marker owns the same internal rows after QSS.  ItemService is
        // reached through the production 0xA2 item request, so this checks the
        // real marker removal cascade and the client-visible remove packets.
        TestAssert.True(UseItem(fixture, target, fixture.Connection(2), 4),
            $"{attackingTeam} QSS cast is accepted through the production item path");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "ItemSlow" ||
            BuffLifecycle.IsItemSlowRow(buff.Name)),
            $"{attackingTeam} QSS removes marker and every internal slow row");
        TestAssert.Near(baseSpeed, target.Speed, 1e-9,
            $"{attackingTeam} QSS leaves no stale movement penalty");
        TestAssert.True(capture.Packets.Count(packet => IsBuffRemove(packet, target.NetId)) >= 3,
            $"{attackingTeam} QSS publishes marker/internal removals to the target observer");

        // 3022 runs from its shipped Items/3022.luaobj ItemOnHitUnit and picks
        // the source's real attack range: one melee and one ranged basic hit,
        // still through CombatService's basic-hit callback.
        source.Inventory = [new InventorySlot(3022), null, null, null, null, null];
        areaSource.Inventory = [new InventorySlot(3022), null, null, null, null, null];
        // Batch 147: Items/3022.luaobj binds its copy to the inventory slot on the next spell update
        // (BbItemModule.SyncInventory), the way a real buy does, so give the runtime that update first.
        runtime.Tick(0.01);
        runtime.HeroStats.Refresh(source, push: false);
        runtime.HeroStats.Refresh(areaSource, push: false);
        TestAssert.True(source.AttackRange <= 300.0,
            $"{attackingTeam} melee producer keeps its shipped attack range");
        TestAssert.True(areaSource.AttackRange > 300.0,
            $"{attackingTeam} ranged producer keeps its shipped attack range");
        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic),
            $"{attackingTeam} 3022 melee basic hit lands");
        runtime.Delivery.Flush();
        var melee = target.Buffs.Single(buff => buff.Name == "Internal_40Slow");
        var malletMarker = target.Buffs.Single(buff => buff.Name == "ItemSlow");
        TestAssert.Near(-0.40, malletMarker.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} 3022 melee slow is the strongest marker contribution");

        TestAssert.True(runtime.Combat.ApplyHit(areaSource.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic),
            $"{attackingTeam} 3022 ranged basic hit lands");
        runtime.Delivery.Flush();
        var ranged = target.Buffs.Single(buff => buff.Name == "Internal_30Slow");
        TestAssert.Near(-0.40, malletMarker.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} melee 3022 contribution remains strongest on the marker");
        TestAssert.Near(0.0, ranged.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} ranged 3022 row is suppressed");
        TestAssert.Near(0.0, melee.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} melee 3022 row is suppressed");

        // A single lifecycle step expires all ordinary rows together.  The
        // final marker removal must reconcile every contribution in that same
        // tick and restore the exact pre-slow movement value.
        foreach (var buff in target.Buffs.Where(buff => buff.Name == "ItemSlow" ||
                     BuffLifecycle.IsItemSlowRow(buff.Name)))
            buff.ExpiresAt = 0.01;
        runtime.Buffs.TickBuffs(target, 0.02);
        runtime.Delivery.Flush();
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "ItemSlow" ||
            BuffLifecycle.IsItemSlowRow(buff.Name)),
            $"{attackingTeam} same-tick expiry removes marker and internal rows");
        TestAssert.Near(baseSpeed, target.Speed, 1e-9,
            $"{attackingTeam} same-tick expiry leaves no stale movement penalty");

        // Spawn every ghoul through YorickScriptSupport.SpawnUnit and exercise each real
        // item producer. Marker rejection is expected for all three ghoul
        // kinds; the internal row may still exist transiently, but it must
        // never own movement without ItemSlow. Each row also expires through
        // the ordinary BuffLifecycle tick.
        var ghoulCases = new[]
        {
            (Label: "3116 single", Attacker: source, ItemId: 3116,
                DamageType: DamageType.Magic, SourceKind: DamageSourceKind.Spell,
                InternalName: "Internal_35Slow"),
            (Label: "3116 area", Attacker: areaSource, ItemId: 3116,
                DamageType: DamageType.Magic, SourceKind: DamageSourceKind.SpellAoe,
                InternalName: "Internal_15Slow"),
            (Label: "3022 melee", Attacker: source, ItemId: 3022,
                DamageType: DamageType.Physical, SourceKind: DamageSourceKind.Basic,
                InternalName: "Internal_40Slow"),
            (Label: "3022 ranged", Attacker: areaSource, ItemId: 3022,
                DamageType: DamageType.Physical, SourceKind: DamageSourceKind.Basic,
                InternalName: "Internal_30Slow"),
        };
        foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
        foreach (var slowCase in ghoulCases)
        {
            slowCase.Attacker.Inventory = [new InventorySlot(slowCase.ItemId),
                null, null, null, null, null];
            // The 3022 cases bind their script copy on the next spell update (see above).
            runtime.Tick(0.01);
            var ghoul = YorickScriptSupport.SpawnUnit(runtime, ghoulOwner, kind, source.Position,
                runtime.Combat.CurrentTime);
            var ghoulSpeed = ghoul.Speed;
            capture.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(slowCase.Attacker.NetId, ghoul.NetId,
                1.0, slowCase.DamageType, slowCase.SourceKind),
                $"{attackingTeam} {slowCase.Label} reaches {kind} ghoul");
            runtime.Delivery.Flush();
            var internalRows = ghoul.Buffs.Where(buff =>
                BuffLifecycle.IsItemSlowRow(buff.Name)).ToArray();
            TestAssert.True(internalRows.Length > 0,
                $"{attackingTeam} {slowCase.Label} keeps the {kind} internal item row");
            TestAssert.True(internalRows.Any(buff => buff.Name == slowCase.InternalName),
                $"{attackingTeam} {slowCase.Label} selects its shipped internal row");
            TestAssert.False(ghoul.Buffs.Any(buff => buff.Name == "ItemSlow"),
                $"{attackingTeam} {slowCase.Label} rejects the {kind} ItemSlow marker");
            TestAssert.True(internalRows.All(buff =>
                Math.Abs(buff.Payload.MoveSpeedMult) <= 1e-9),
                $"{attackingTeam} {slowCase.Label} has no active {kind} speed contribution");
            TestAssert.Near(ghoulSpeed, ghoul.Speed, 1e-9,
                $"{attackingTeam} {slowCase.Label} leaves {kind} speed unchanged");
            TestAssert.False(capture.Packets.Any(packet =>
                IsBuffAdd(packet, ghoul.NetId, SpellPackets.BuffNameHash("ItemSlow"))), // Batch 357 (restates batch 181): the binding
                $"{attackingTeam} {slowCase.Label} emits no {kind} ItemSlow add packet");
            foreach (var buff in internalRows) buff.ExpiresAt = 0.01;
            runtime.Buffs.TickBuffs(ghoul, 0.02);
            TestAssert.False(ghoul.Buffs.Any(buff =>
                    BuffLifecycle.IsItemSlowRow(buff.Name)),
                $"{attackingTeam} {slowCase.Label} expires the {kind} internal row");
            TestAssert.Near(ghoulSpeed, ghoul.Speed, 1e-9,
                $"{attackingTeam} {slowCase.Label} leaves no {kind} expiry penalty");
            runtime.Combat.Kill(ghoul, slowCase.Attacker.NetId);
        }

        // The scheduler's real Dragon camp owns ResistantSkinDragon and
        // rejects the visible marker while its self-bound internal row is
        // harmless. Use its patch-126 spawn deadline; do not
        // register a synthetic neutral or hand-add the resistance buff.
        source.Inventory = [new InventorySlot(3116), null, null, null, null, null];
        var dragonSpawnAt = NeutralDefinitions.InitialSpawnSeconds +
            NeutralDefinitions.Camps[6].InitialDelay;
        runtime.Combat.RestoreClock(dragonSpawnAt);
        runtime.Tick(0.01);
        var dragon = runtime.World.Entities.Values.Single(entity =>
            entity.Name == "Dragon" && entity.Alive);
        source.Position = dragon.Position;
        var dragonSpeed = dragon.Speed;
        var dragonHealth = dragon.Health;
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, dragon.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.Spell),
            $"{attackingTeam} Dragon accepts the actual spell damage");
        TestAssert.True(dragon.Health < dragonHealth,
            $"{attackingTeam} Dragon damage remains observable after slow rejection");
        TestAssert.False(dragon.Buffs.Any(buff => buff.Name == "ItemSlow"),
            $"{attackingTeam} Dragon rejects the ItemSlow marker");
        var dragonRows = dragon.Buffs.Where(buff =>
            BuffLifecycle.IsItemSlowRow(buff.Name)).ToArray();
        TestAssert.Equal(1, dragonRows.Length,
            $"{attackingTeam} Dragon retains one self-bound internal slow row");
        TestAssert.Equal(dragon.NetId, dragonRows[0].CasterNetId,
            $"{attackingTeam} Dragon internal slow row binds to itself");
        TestAssert.Near(0.0, dragonRows[0].Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} Dragon internal row has no movement contribution");
        TestAssert.Near(dragonSpeed, dragon.Speed, 1e-9,
            $"{attackingTeam} Dragon speed remains unchanged");
        TestAssert.False(dragon.Buffs.Any(buff => buff.Payload.MoveSpeedMult < 0.0),
            $"{attackingTeam} Dragon has no stale movement modifier");
    }

    private static ProductionFixture StartProduction(
        IReadOnlyList<SessionParticipantDefinition> roster)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, random: new Amberline.Core.SeededRandomSource(7));
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Count; index++)
        {
            var connection = (long)(100 + index);
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck((ulong)roster[index].PlayerId)) is not null,
                $"production seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"production seat {index} reaches ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "production marker fixture starts");
        return new ProductionFixture(runtime);
    }

    private static CaptureTransport AttachCapture(MatchRuntime runtime, Entity observer)
    {
        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            90_000UL + observer.NetId, observer.Team, observer.NetId, capture, ready: true,
            view: runtime.Session.Participants.First(participant =>
                participant.Hero?.NetId == observer.NetId).View));
        runtime.Delivery.Flush();
        return capture;
    }

    private static bool UseItem(ProductionFixture fixture, Entity hero, long connection,
        int slot)
    {
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = checked((byte)(slot << 1));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset),
                (float)hero.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8),
                (float)hero.Position.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), hero.NetId);
        var accepted = false;
        fixture.Runtime.Session.Receive(connection, 1,
            fixture.Runtime.Cipher.Encrypt(request), context =>
                accepted = fixture.Runtime.Dependencies.Gameplay(context));
        fixture.Runtime.Delivery.Flush();
        return accepted;
    }

    private static bool IsBuffAdd(PlaintextPacket packet, uint targetNetId,
        uint buffHash)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 13 && bytes[0] == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == targetNetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) == buffHash;
    }

    private static bool IsBuffRemove(PlaintextPacket packet, uint targetNetId)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 10 && bytes[0] == 0x7F &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == targetNetId;
    }

    private sealed record ProductionFixture(MatchRuntime Runtime)
    {
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!
            ?? throw new InvalidOperationException($"hero {index} missing");

        public long Connection(int index) => Runtime.Session.Participants[index].ConnectionId
            ?? throw new InvalidOperationException($"connection {index} missing");
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
        public void Clear() => Packets.Clear();
    }
}
