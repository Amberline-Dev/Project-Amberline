using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 296 (replaces batch 222's native-stat reading): the target's tenacity is the treads' shipped hooks, written back as shipped.
    // Two-Shiv's poison is a BUFF_Slow from an enemy (Spells/TwoShivPoison.luaobj), so MercuryTreads.luaobj cuts it x0.65 for the 10 s after
    // the buy (Items/3111.luaobj OnActivate) and Hardening.luaobj x(1 - 0.35) once its one-second aura is on; neither tests CanMitigateDuration.
    [Check("spells-live-shaco-active-poison-renewal")]
    public static void ActiveTwoShivRenewsTheLivePoison()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var shaco = match.Hero(0);
            var target = match.Hero(1);
            target.Position = shaco.Position + new Position2(25.0, 0.0);
            target.Inventory = [new InventorySlot(3111), null, null, null, null, null];
            runtime.HeroStats.Refresh(target);
            TestAssert.True(target.Hardness > 0.0,
                "the real tenacity item supplies positive target hardness");
            target.MaxHealth = target.Health = 10_000.0;
            var baseSpeed = target.Speed;
            // The cut the treads' held hooks make on an enemy slow added now (see the comment above).
            double Cut() => (target.Buffs.Any(buff => buff.Name == "MercuryTreads") ? 0.65 : 1.0) *
                            (target.Buffs.Any(buff => buff.Name == "Hardening") ? 0.65 : 1.0);

            match.Cast(0, 2, target);
            for (var tick = 0; tick < 100 && !target.Buffs.Any(buff => buff.Name == "TwoShivPoison"); tick++)
                runtime.Tick(0.01);
            var first = target.Buffs.Single(buff => buff.Name == "TwoShivPoison");
            var firstSlot = first.Slot;
            var firstFx = first.Payload.FxNetId;
            var firstExtraFx = first.Payload.ExtraFxNetId;
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "MercuryTreads"), "the buy's MercuryTreads window is on");
            TestAssert.Near(3.0 * Cut(), first.ExpiresAt, 0.05,
                "the first active poison is three seconds cut by the treads' hooks");
            TestAssert.Near(-0.2, first.Payload.MoveSpeedMult, 1e-9,
                "rank one active poison payload");
            TestAssert.Near(baseSpeed * 0.8, target.Speed, 1e-9,
                "first poison applies one slow");

            match.Clear();
            shaco.SpellRanks[2] = 5;
            shaco.SpellCooldowns[2] = 0.0;
            shaco.Mana = shaco.MaxMana;
            match.Cast(0, 2, target);
            for (var tick = 0; tick < 100 && !target.Buffs.Any(buff =>
                         buff.Name == "TwoShivPoison" && Math.Abs(buff.Payload.MoveSpeedMult + 0.3) < 1e-9); tick++)
                runtime.Tick(0.01);
            var renewed = target.Buffs.Single(buff => buff.Name == "TwoShivPoison");

            TestAssert.True(ReferenceEquals(first, renewed),
                "active poison renewal keeps the live buff instance");
            TestAssert.Equal(firstSlot, renewed.Slot,
                "active poison renewal keeps the buff slot");
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "TwoShivPoison"),
                "renewal keeps one poison");
            var renewedLength = 3.0 * Cut();
            TestAssert.Near(renewedLength, renewed.ExpiresAt, 0.05,
                "active poison renewal resets the three-second duration, cut the same way");
            TestAssert.Near(-0.3, renewed.Payload.MoveSpeedMult, 1e-9,
                "rank five payload replaces the rank one payload");
            TestAssert.Equal(shaco.NetId, renewed.CasterNetId,
                "renewal retains the Shaco caster");
            TestAssert.Near(baseSpeed * 0.7, target.Speed, 1e-9,
                "renewal replaces the slow without double-applying it");
            if (firstFx != 0)
            {
                TestAssert.Equal(firstFx, renewed.Payload.FxNetId,
                    "renewal retains a server-owned primary effect");
                TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                    Opcode(packet) == FxPackets.KillOpcode && packet.Plaintext.Length >= 9 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == firstFx),
                    "renewal does not kill the primary effect");
            }
            if (firstExtraFx != 0)
            {
                TestAssert.Equal(firstExtraFx, renewed.Payload.ExtraFxNetId,
                    "renewal retains a server-owned secondary effect");
                TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                    Opcode(packet) == FxPackets.KillOpcode && packet.Plaintext.Length >= 9 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == firstExtraFx),
                    "renewal does not kill the secondary effect");
            }

            var poisonHash = SpellPackets.BuffNameHash("TwoShivPoison"); // Batch 357 (restates batch 181): the binding; Two Shiv Poison has no script
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                ShacoPoisonPacket(packet, 0x7F, target.NetId, poisonHash)),
                "renewal emits no poison removal packet");
            // Batch 55 (d3ea329): a same-count renewal of a slot the client holds is a silent refresh of the live
            // instance, not a second 0xBF that would replay the activation sound. Batch 55 pinned that refresh as
            // NpcBuffReplace (0x32: slot @5, duration @10, caster @14); the batch 162 client read says which of
            // the two silent refreshes it is. TwoShivPoison's own template hands the client a torch
            // (AutoBuffActivateEffect='global_slow.troy'), and the 0x32 handler (the buff manager's packet handler)
            // ends in its torch restart, which deactivates the instance's four
            // auto-effect particles and re-creates them: a renewal sent as 0x32 kills and re-lights the slow's
            // particle. NpcBuffUpdateCount (0x1F: slot @5, count @6, duration @7, running @11, caster @15) is the
            // refresh that leaves it alone: its handler feeds those fields to
            // the resize routine, which restarts the slot's timers (start = now - running,
            // end = now + duration) and rebuilds the stack to the count. So the 0x1F carries the renewed duration
            // and the count back to the client — the HUD timer the renewal resets is the owner-visible half, and a
            // renewal that left the timer running out would be a regression even with no torch blink. What 0x32 does
            // besides the torch restart is write the torch's start anchor and leave the count alone; nothing else
            // in that handler is renewal work.
            // Reconstruction: retail's server buff manager is not in the client binary; the renewal is pinned as the
            // 0x1F because the template's torch is the script's own claim and both silent handlers reset the timers.
            var renewals = match.Captures.SelectMany(capture => capture.Packets).Where(packet =>
                packet.Plaintext.Length >= 19 && packet.Plaintext.Span[0] == 0x1F &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
                packet.Plaintext.Span[5] == (byte)renewed.Slot).ToArray();
            TestAssert.True(renewals.Length > 0, "renewal emits the native buff refresh");
            TestAssert.True(renewals.All(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == shaco.NetId),
                "the refresh names the Shaco caster");
            TestAssert.True(renewals.All(packet => packet.Plaintext.Span[6] == 1),
                "the refresh keeps the one poison stack");
            TestAssert.True(renewals.All(packet =>
                    Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[7..]) - renewedLength) < 0.05),
                "the refresh carries the renewed (cut) duration (the HUD timer must not run out)");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                ShacoPoisonPacket(packet, SpellPackets.BuffAddOpcode, target.NetId, poisonHash)),
                "renewal is not a second add");

            runtime.Tick(3.1);
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "TwoShivPoison"),
                "the renewed poison expires naturally");
            TestAssert.True(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                ShacoPoisonPacket(packet, 0x7F, target.NetId, poisonHash)),
                "natural expiry emits the poison removal");
        }
    }

    private static bool ShacoPoisonPacket(PlaintextPacket packet, byte opcode,
        uint targetNetId, uint buffNameHash)
    {
        var plaintext = packet.Plaintext.Span;
        if (plaintext.Length < (opcode == SpellPackets.BuffAddOpcode ? 13 : 10) ||
            plaintext[0] != opcode ||
            BinaryPrimitives.ReadUInt32LittleEndian(plaintext[1..]) != targetNetId)
            return false;
        var hashOffset = opcode == SpellPackets.BuffAddOpcode ? 9 : 6;
        return BinaryPrimitives.ReadUInt32LittleEndian(plaintext[hashOffset..]) == buffNameHash;
    }
}
