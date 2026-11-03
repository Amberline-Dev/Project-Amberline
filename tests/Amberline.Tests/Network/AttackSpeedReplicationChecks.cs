using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("stats.attack-speed.capped-clone-timing")]
    public static void ClonesRetainRawTimingWhenTheirSourceIsAlreadyCapped()
    {
        foreach (var kind in new[] { CloneKind.ShacoClone, CloneKind.YorickRevenant })
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            owner.AttackSpeedMod = 10.0;
            HeroAttackTimings.ApplyRefresh(owner, GameData.Default);
            TestAssert.Near(0.4, owner.AttackPeriod, 1e-9);
            var rawPeriod = owner.AttackTimingBaseline!.Value.BasePeriod;
            var clone = runtime.Clones.Spawn(owner, owner, owner.Position, kind,
                owner.MaxHealth, "Hallucinate", 30, "Hallucinate");
            var slow = runtime.Buffs.AddBuff(clone, "test-strong-slow", BuffType.CombatDehancer,
                10, match.Hero(1).NetId, new BuffPayload { AttackSpeedMult = -0.90 },
                canMitigateDuration: false)!;
            TestAssert.Near(1.0, clone.AttackSpeedMod, 1e-9);
            TestAssert.Near(rawPeriod, clone.AttackPeriod, 1e-9,
                "slowing a capped clone recovers its character period, not the capped snapshot");
            runtime.Buffs.RemoveBuff(clone, slow.Slot);
            TestAssert.Near(10.0, clone.AttackSpeedMod, 1e-9);
            TestAssert.Near(0.4, clone.AttackPeriod, 1e-9);
        }
    }

    [Check("stats.attack-speed.production-replication")]
    public static void AttackSpeedSurvivesPetCreationBuffsAndFog()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            owner.Inventory = [new InventorySlot(1042), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            var copiedSpeed = owner.AttackSpeedMod;
            TestAssert.True(copiedSpeed > 1.0, "a purchased dagger makes the copied rate nondefault");
            match.Clear();
            var clone = runtime.Clones.Spawn(owner, owner, new Position2(7100, 7000),
                CloneKind.ShacoClone, owner.MaxHealth / 2, "Hallucinate", 30, "Hallucinate");
            runtime.Delivery.Flush();
            var health = clone.Health;
            var maxHealth = clone.MaxHealth;
            var copiedMovementSpeed = clone.Speed;
            TestAssert.Near(325.0, copiedMovementSpeed, 1e-9,
                "the clone captures the owner's movement baseline");
            foreach (var capture in match.Captures)
            {
                var initial = capture.Packets.Where(packet => IsAttackSpeedPacket(packet, clone.NetId) &&
                    packet.Plaintext.Length == 111).ToArray();
                TestAssert.True(initial.Length > 0, "both observers receive the pet's initial stat template");
                foreach (var packet in initial)
                    TestAssert.Near(copiedSpeed, BitConverter.ToSingle(packet.Plaintext.Span[71..]),
                        1e-6, "initial template retains copied attack speed");
            }

            match.Clear();
            var slow = runtime.Buffs.AddBuff(clone, "FrozenHeartAura", BuffType.Aura,
                30, enemy.NetId, new BuffPayload
                {
                    AttackSpeedMult = -0.20,
                    MoveSpeedMult = -0.20,
                },
                canMitigateDuration: false)!;
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var update = capture.Packets.Single(packet => HasMinionMask(packet, clone.NetId, 0x2000));
                TestAssert.Equal(23, update.Plaintext.Length, "speed delta contains no health fields");
                TestAssert.Near(copiedSpeed * 0.8, BitConverter.ToSingle(update.Plaintext.Span[19..]),
                    1e-6, "a visible slow publishes the changed modifier");
                var movement = capture.Packets.Single(packet => HasMovementMask(packet, clone.NetId));
                TestAssert.Equal(23, movement.Plaintext.Length,
                    "a visible movement change uses a speed-only delta");
                TestAssert.Near(clone.Speed, BitConverter.ToSingle(movement.Plaintext.Span[19..]),
                    1e-6, "a visible movement change carries the recomputed NPC speed");
                TestAssert.False(capture.Packets.Any(packet =>
                        IsAttackSpeedPacket(packet, clone.NetId) && packet.Plaintext.Length == 111),
                    "a live movement change does not resend the full creation template");
            }
            TestAssert.Near(copiedMovementSpeed * 0.8, clone.Speed, 1e-9,
                "the live NPC movement state uses the captured baseline");
            TestAssert.Near(health, clone.Health, 1e-9, "speed changes do not heal an injured pet");
            TestAssert.Near(maxHealth, clone.MaxHealth, 1e-9,
                "speed changes do not replace an injured pet's maximum health");

            var enemyPosition = enemy.Position;
            enemy.Position = new Position2(14000, 14000);
            runtime.Delivery.Flush();
            runtime.Tick(1.0);
            TestAssert.False(runtime.Delivery.Observers.Single(observer => observer.ObserverId == 2)
                .View.Visible.Contains(clone.NetId), "enemy observer loses pet vision");
            match.Clear();
            runtime.Buffs.RemoveBuff(clone, slow.Slot);
            runtime.Delivery.Flush();
            TestAssert.False(match.Captures[1].Packets.Any(packet => HasMinionMask(packet, clone.NetId, 0x2000)),
                "hidden speed changes do not leak to the opponent");
            TestAssert.False(match.Captures[1].Packets.Any(packet => HasMovementMask(packet, clone.NetId)),
                "hidden movement changes do not leak to the opponent");
            TestAssert.Near(copiedMovementSpeed, clone.Speed, 1e-9,
                "removing the hidden slow restores the captured baseline");
            TestAssert.Near(health, clone.Health, 1e-9,
                "removing a hidden speed effect preserves current health");
            TestAssert.Near(maxHealth, clone.MaxHealth, 1e-9,
                "removing a hidden speed effect preserves maximum health");

            match.Clear();
            enemy.Position = enemyPosition;
            runtime.Delivery.Flush();
            var revealed = match.Captures[1].Packets.Single(packet => HasMinionMask(packet, clone.NetId, 0x2001));
            TestAssert.Equal(27, revealed.Plaintext.Length, "re-entry carries only current HP and attack speed");
            TestAssert.Near(clone.Health, BitConverter.ToSingle(revealed.Plaintext.Span[19..]), 1e-4,
                "re-entry preserves current HP separately from max HP");
            TestAssert.Near(copiedSpeed, BitConverter.ToSingle(revealed.Plaintext.Span[23..]), 1e-6,
                "re-entry restores the modifier after hidden slow removal");
            var reentryMovement = match.Captures[1].Packets.Single(packet => HasMovementMask(packet, clone.NetId));
            TestAssert.Equal(23, reentryMovement.Plaintext.Length,
                "re-entry movement update is a narrow speed-only packet");
            TestAssert.Near(clone.Speed, BitConverter.ToSingle(reentryMovement.Plaintext.Span[19..]), 1e-6,
                "re-entry movement update carries the current NPC speed");

            var observer = runtime.Delivery.Observers.Single(item => item.ObserverId == 2);
            var restored = runtime.Delivery.Services.BuildEntityCreation(clone, observer)
                .Single(packet => IsAttackSpeedPacket(packet, clone.NetId) && packet.Plaintext.Length == 111);
            TestAssert.Near(copiedSpeed, BitConverter.ToSingle(restored.Plaintext.Span[71..]), 1e-6,
                "reconnect creation restores the current copied rate");
        }
    }

    [Check("stats.movement.item-slow-marker-removal-production")]
    public static void ProductionItemSlowMarkerRemovalRebuildsOnce()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var clone = runtime.Clones.Spawn(owner, owner, new Position2(7100, 7000),
            CloneKind.ShacoClone, owner.MaxHealth / 2, "Hallucinate", 30, "Hallucinate");
        runtime.Delivery.Flush();
        match.Clear();

        // Script wins (batch 147): Spells/ItemSlow.luaobj selects the strongest self-bound Internal_*Slow
        // row by name and applies that magnitude from the ItemSlow marker itself, leaving the rows empty.
        // The shipped row scripts are empty, so these production rows carry no payload either.
        var marker = runtime.Buffs.AddBuff(clone, "ItemSlow", BuffType.Slow,
            10.0, owner.NetId, canMitigateDuration: false)!;
        runtime.Buffs.AddBuff(clone, "Internal_20Slow", BuffType.Internal,
            10.0, owner.NetId, canMitigateDuration: false);
        runtime.Buffs.AddBuff(clone, "Internal_40Slow", BuffType.Internal,
            10.0, owner.NetId, canMitigateDuration: false);
        runtime.Dependencies.BuffServices.Items.SyncItemSlow(clone);
        runtime.Buffs.RefreshMovementSpeed(clone, push: false);
        TestAssert.Near(325.0 * (1.0 - 0.40), clone.Speed, 1e-9,
            "production item selector applies the strongest slow");
        runtime.Delivery.Flush();
        match.Clear();

        runtime.Buffs.RemoveBuff(clone, marker.Slot);
        runtime.Delivery.Flush();
        TestAssert.Near(325.0, clone.Speed, 1e-9,
            "production marker removal restores the clone baseline");
        foreach (var capture in match.Captures)
        {
            var movement = capture.Packets.Where(packet => HasMovementMask(packet, clone.NetId)).ToArray();
            TestAssert.Equal(1, movement.Length,
                "marker cleanup emits one final speed-only packet per observer");
            TestAssert.Near(clone.Speed, BitConverter.ToSingle(movement[0].Plaintext.Span[19..]),
                1e-6, "marker cleanup packet carries the restored speed");
        }

        var silentMarker = runtime.Buffs.AddBuff(clone, "ItemSlow", BuffType.Slow,
            10.0, owner.NetId, canMitigateDuration: false)!;
        runtime.Buffs.AddBuff(clone, "Internal_20Slow", BuffType.Internal,
            10.0, owner.NetId, canMitigateDuration: false);
        runtime.Buffs.AddBuff(clone, "Internal_40Slow", BuffType.Internal,
            10.0, owner.NetId, canMitigateDuration: false);
        runtime.Dependencies.BuffServices.Items.SyncItemSlow(clone);
        runtime.Buffs.RefreshMovementSpeed(clone, push: false);
        runtime.Delivery.Flush();
        match.Clear();

        runtime.Buffs.RemoveBuff(clone, silentMarker.Slot, emitWire: false);
        runtime.Delivery.Flush();
        TestAssert.Near(325.0, clone.Speed, 1e-9,
            "silent production marker removal restores the clone baseline");
        foreach (var capture in match.Captures)
            TestAssert.False(capture.Packets.Any(packet => HasMovementMask(packet, clone.NetId)),
                "silent marker cleanup emits no movement packet");
    }

    private static bool IsAttackSpeedPacket(PlaintextPacket packet, uint netId) =>
        packet.Plaintext.Length >= 19 && packet.Plaintext.Span[0] == 0xCC &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == netId;

    private static bool HasMinionMask(PlaintextPacket packet, uint netId, uint mask) =>
        IsAttackSpeedPacket(packet, netId) && packet.Plaintext.Span[10] == 0x02 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == mask;

    private static bool HasMovementMask(PlaintextPacket packet, uint netId) =>
        IsAttackSpeedPacket(packet, netId) && packet.Plaintext.Span[10] == 0x08 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == 0x04;
}
