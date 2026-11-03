using System;
using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-lightbringer-mark-recipient-death")]
    public static void LightbringerSightEndsWithItsRecipient()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (itemId, spellHit) in new[] { (3187, true), (3185, false) })
        foreach (var lethal in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var target = match.Hero(1);
            source.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
            runtime.HeroStats.Refresh(source);
            target.Health = lethal ? 1.0 : target.MaxHealth;
            match.Clear();

            if (spellHit)
                match.Cast(0, 0, target); // Real Annie Q spell damage and 3187 proc.
            else
                match.Attack(0, target); // Real basic order and 3185 on-hit proc.
            Advance(match, spellHit ? 1.0 : 2.0);
            runtime.Delivery.Flush();

            var packets = match.Captures[1].Packets;
            // Batch 147: 3185's mark is Items/3185.luaobj ItemOnHitUnit's own add now, and that callback runs on the hit
            // itself while the target is still alive (the Modify stage of CombatService.ApplyHit). A fatal basic therefore
            // installs its mark like any other, and the recipient's death retires it in the same beat
            // (MatchGameplayHooks.RecipientDeathMarks). The hand-written path ran from OnBasicAttackHit, after Health
            // reached zero, and skipped the fresh mark on a fatal hit.
            // Batch 389: on a fatal hit the mark's add and the death's removal share the flush, so the pair coalesces and neither reaches the
            // client (ReplicationDelivery.CoalescesBuffPair); the server still installs the mark and retires it with its recipient.
            TestAssert.Equal(!lethal, packets.Any(packet => IsBuffAdd(packet, target, "OdinLightbringer")),
                $"{team}/{itemId}/{lethal}: the mark's add reaches the client only when the hit is not fatal");
            if (lethal)
            {
                TestAssert.False(target.Alive);
                AssertNoLightbringerSight(runtime, team, target);
                TestAssert.False(packets.Any(packet => IsLightbringerRemoval(packet, target)),
                    "the coalesced pair sends no deactivation either");
                TestAssert.True(runtime.Combat.RespawnImmediately(target));
                AssertNoLightbringerSight(runtime, team, target);
                continue;
            }

            TestAssert.True(target.Alive);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"));
            TestAssert.True(HasLightbringerSight(runtime, team, target),
                "living marked recipient grants the attacker's team attached sight");
            TestAssert.True(runtime.Combat.Kill(source, target.NetId));
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"),
                "caster death does not remove a live recipient's mark");
            TestAssert.True(HasLightbringerSight(runtime, team, target));

            match.Clear();
            TestAssert.True(runtime.Combat.Kill(target, source.NetId));
            runtime.Combat.ScheduleRespawn(target);
            runtime.Delivery.Flush();
            AssertNoLightbringerSight(runtime, team, target);
            TestAssert.True(match.Captures[1].Packets.Any(packet =>
                IsLightbringerRemoval(packet, target)),
                "recipient death sends the removal that deactivates the stock buff effect");
            TestAssert.True(runtime.Combat.RespawnImmediately(target));
            AssertNoLightbringerSight(runtime, team, target);
        }

        // A 3185 mark installed by a real nonlethal basic must be retired when
        // the next basic kills its recipient. That fatal hit runs its own
        // ItemOnHitUnit like any other (batch 147), which renews the mark the
        // victim already carries (Items/3185.luaobj, BUFF_RENEW_EXISTING)
        // before the death retires it.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var target = match.Hero(1);
            source.Inventory = [new InventorySlot(3185), null, null, null, null, null];
            runtime.HeroStats.Refresh(source);
            match.Attack(0, target);
            Advance(match, 2.0);
            runtime.Delivery.Flush();
            TestAssert.True(target.Alive && target.Buffs.Any(buff =>
                buff.Name == "OdinLightbringer"));
            TestAssert.True(HasLightbringerSight(runtime, team, target));

            match.Clear();
            var markSlot = target.Buffs.Single(buff => buff.Name == "OdinLightbringer").Slot;
            target.Health = 1.0;
            match.Attack(0, target);
            Advance(match, 2.0);
            runtime.Delivery.Flush();
            TestAssert.False(target.Alive);
            AssertNoLightbringerSight(runtime, team, target);
            TestAssert.True(match.Captures[1].Packets.Any(packet =>
                IsLightbringerRemoval(packet, target)),
                "fatal basic deactivates the existing exact Lightbringer mark");
            // The renewal of a mark the client already holds is not a 0xBF add: it goes out as
            // NpcBuffReplace (0x32), which restarts the live instance's timer and leaves its
            // count and slot alone (ReplicationDelivery; TryBuffRenewal in ItemRenewalLiveChecks).
            TestAssert.False(match.Captures[1].Packets.Any(packet =>
                IsBuffAdd(packet, target, "OdinLightbringer")),
                "fatal basic renews its mark in place instead of adding a second one");
            TestAssert.True(match.Captures[1].Packets.Any(packet =>
                IsBuffReplace(packet, target, markSlot)),
                "the fatal basic's renewal reaches the client as NpcBuffReplace");
        }
    }

    private static bool HasLightbringerSight(Amberline.Network.Sessions.MatchRuntime runtime,
        Team team, Entity target) => runtime.Dependencies.AdditionalProviders(team).Any(provider =>
            provider.Position == target.Position && Math.Abs(provider.Radius - 400.0) < 1e-9);

    private static void AssertNoLightbringerSight(Amberline.Network.Sessions.MatchRuntime runtime,
        Team team, Entity target)
    {
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"),
            "the marked recipient no longer carries Lightbringer after death or respawn");
        TestAssert.False(HasLightbringerSight(runtime, team, target),
            "the corpse or revived recipient grants no stale attached item sight");
    }

    private static bool IsLightbringerRemoval(Amberline.Network.Protocol.PlaintextPacket packet,
        Entity target)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 10 && bytes[0] == 0x7F &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == target.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]) ==
            Amberline.Network.Protocol.SpellPackets.BuffNameHash("OdinLightbringer");
    }

    // NpcBuffReplace (0x32): target @1, slot @5, caster @14. It keeps the held buff's name hash and count.
    private static bool IsBuffReplace(Amberline.Network.Protocol.PlaintextPacket packet,
        Entity target, int slot) =>
        packet.Plaintext.Length >= 18 &&
        packet.Plaintext.Span[0] == Amberline.Network.Protocol.SpellPackets.BuffReplaceOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
        packet.Plaintext.Span[5] == slot;
}
