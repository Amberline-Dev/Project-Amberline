using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-yorick-b11-e-target-dies-during-windup-refunds-once")]
    public static void YorickRavenousTargetLossRefundsOnce()
    {
        VerifyWindupRefund("Yorick", 2);
        VerifyWindupRefund("Yorick", 2, rank: 3);
        using var match = new Match("Yorick", Team.Order);
        var hero = match.Hero(0); var target = match.Hero(1);
        target.Health = 1;
        var mana = hero.Mana;
        match.Cast(0, 2, target);
        SpellStep(match, .3);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), so the charge is read after it.
        var paid = hero.Mana;
        TestAssert.True(paid < mana);
        TestAssert.False(target.Alive, "E's own hit kills its target");
        TestAssert.True(match.Runtime.World.Entities.Values.Any(entity =>
            entity.Alive && entity.Kind == EntityKind.YorickGhoul && entity.Name == "YorickRavenousGhoul"),
            "lethal E still summons Ravenous");
        TestAssert.Near(paid, hero.Mana, 1e-9, "own lethal hit keeps the mana charge");
        TestAssert.True(hero.SpellCooldowns[2] > 0, "own lethal hit keeps cooldown");
        SpellStep(match, .3);
        TestAssert.Near(paid, hero.Mana, 1e-9, "follow-up cannot refund a committed hit");
    }

    [Check("spells-yorick-b11-r-target-loss-refunds-once")]
    public static void YorickRevivalTargetLossRefundsOnce() => VerifyWindupRefund("Yorick", 3, ally: true);

    [Check("spells-annie-b11-q-target-loss-refunds-before-launch")]
    public static void AnnieQTargetLossRefundsBeforeLaunch()
    {
        VerifyWindupRefund("Annie", 0);
        VerifyLaunchedSpellIsNotRefunded("Annie", 0, "Disintegrate");
    }

    [Check("spells-shaco-b11-e-target-loss-refunds-before-launch")]
    public static void ShacoETargetLossRefundsBeforeLaunch()
    {
        VerifyWindupRefund("Shaco", 2);
        VerifyLaunchedSpellIsNotRefunded("Shaco", 2, "TwoShivPoison");
    }

    [Check("spells-janna-b11-w-target-loss-refunds-before-launch")]
    public static void JannaWTargetLossRefundsBeforeLaunch()
    {
        VerifyWindupRefund("Janna", 1);
        VerifyLaunchedSpellIsNotRefunded("Janna", 1, "SowTheWind");
    }

    [Check("spells-vayne-b11-e-target-loss-refunds-before-launch")]
    public static void VayneETargetLossRefundsBeforeLaunch()
    {
        VerifyWindupRefund("Vayne", 2);
        VerifyLaunchedSpellIsNotRefunded("Vayne", 2, "VayneCondemn");
    }

    [Check("spells-leesin-b11-r-target-loss-refunds-once")]
    public static void LeeSinRTargetLossRefundsOnce() => VerifyWindupRefund("LeeSin", 3);

    [Check("spells-nidalee-b11-e-target-loss-refunds-once")]
    public static void NidaleeETargetLossRefundsOnce() => VerifyWindupRefund("Nidalee", 2, ally: true);

    private static void VerifyWindupRefund(string champion, int slot, bool ally = false, int rank = 1)
    {
        foreach (var loss in new[] { "death", "untargetable", "untargetable-callback" })
        {
            using var match = new Match(champion, Team.Order, ally: ally);
            var hero = match.Hero(0); var target = match.Hero(ally ? 2 : 1);
            target.Position = hero.Position + new Position2(200, 0);
            hero.SpellRanks[slot] = rank;
            hero.Mana -= 20;
            var mana = hero.Mana;
            match.Cast(0, slot, target);
            // Batch 340: a windup cast pays at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), so the press spends nothing and a
            // target lost in the windup leaves nothing to refund (was: the press paid and the loss refunded).
            TestAssert.Near(0, hero.SpellCooldowns[slot], 0, "the press starts no cooldown");
            TestAssert.Near(mana, hero.Mana, 1e-9, "the press spends no mana before the cast frame");
            SpellStep(match, .05);
            // Refund the accepted cost, not a newly learned rank or a changed cheat setting.
            hero.SpellRanks[slot] = 2;
            hero.UnlimitedResource = true;
            match.Runtime.Delivery.Flush(); match.Clear();
            if (loss == "death")
                match.Runtime.Combat.Kill(target, ally ? match.Hero(1).NetId : hero.NetId);
            else
            {
                target.Buffs.Add(new BuffState("TargetLossProbe",
                    new BuffPayload { Untargetable = true }, BuffType.Internal));
                if (loss == "untargetable-callback")
                    match.Runtime.Spells.DestroyTargetedProjectiles(target.NetId);
            }
            SpellStep(match, .5);
            match.Runtime.Delivery.Flush();
            TestAssert.Near(mana, hero.Mana, 1e-9, champion + " restores the original paid cost");
            TestAssert.Near(0, hero.SpellCooldowns[slot], 0, champion + " clears cooldown");
            TestAssert.False(hero.ChampionState?.BlocksActions == true, "aborted cast releases action lock");
            TestAssert.Equal(1, RefundPacketCount(match, hero, slot), "one owner cooldown reset");
            var stats = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0xCC &&
                packet.Plaintext.Length == 171 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == hero.NetId);
            TestAssert.Near(mana, BinaryPrimitives.ReadSingleLittleEndian(stats.Plaintext.Span[151..]), 1e-3,
                "owner stats synchronize the refunded mana");
            if (champion == "Yorick")
                TestAssert.False(match.Runtime.World.Entities.Values.Any(entity => entity.OwnerNetId == hero.NetId &&
                    entity.Kind is EntityKind.YorickGhoul or EntityKind.YorickRevenant),
                    "aborted cast does not summon");
            if (champion == "Nidalee")
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "PrimalSurge"), "no heal buff after abort");
            if (champion == "LeeSin")
            {
                // 19 October 2026: the pre-kick root is the character script's 0.25 s BlindMonkRRoot buff. An aborted windup leaves it to
                // run out (a spell step does not age buffs, so the target's buffs are stepped here).
                match.Runtime.SpellBuffs.Tick(target, .3);
                TestAssert.False(target.Buffs.Any(buff => buff.Name == "BlindMonkRRoot"), "the pre-kick root runs out after the abort");
            }
            // Spend some mana to expose a repeated refund even at the resource cap.
            hero.Mana -= 10;
            match.Runtime.Spells.DestroyTargetedProjectiles(target.NetId);
            SpellStep(match, .5);
            match.Runtime.Spells.DestroyTargetedProjectiles(target.NetId);
            match.Runtime.Delivery.Flush();
            TestAssert.Near(mana - 10, hero.Mana, 1e-9, "later ticks and retirement callbacks cannot refund twice");
            TestAssert.Equal(1, RefundPacketCount(match, hero, slot), "no duplicate cooldown reset");
        }
    }

    private static void VerifyLaunchedSpellIsNotRefunded(string champion, int slot, string spellName)
    {
        using var match = new Match(champion, Team.Order);
        var hero = match.Hero(0); var target = match.Hero(1);
        target.Position = hero.Position + new Position2(500, 0);
        match.Cast(0, slot, target);
        SpellStep(match, GameData.Default.Spell(spellName).CastTime + .001);
        // Batch 340: the cast frame (the launch) pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), so the charge is read after it.
        var paid = hero.Mana;
        TestAssert.True(FlightLaunched(match, champion, target.NetId), "control crosses the real launch boundary");
        TestAssert.True(target.Alive, "target survives the missile launch boundary");
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Runtime.Combat.Kill(target, hero.NetId);
        SpellStep(match, .2);
        match.Runtime.Delivery.Flush();
        TestAssert.Near(paid, hero.Mana, 1e-9, "target loss after launch keeps the mana charge");
        TestAssert.True(hero.SpellCooldowns[slot] > 0, "target loss after launch keeps cooldown");
        TestAssert.Equal(0, RefundPacketCount(match, hero, slot), "no reset packet after launch");
    }

    private static int RefundPacketCount(Match match, Entity hero, int slot) =>
        match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x8A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
            packet.Plaintext.Span[5] == slot &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..]) == 0);
}
