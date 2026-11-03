using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-janna-e-capacity-pair-rejects-and-renews")]
    public static void JannaEyeOfTheStormCapacityPair()
    {
        // 19 October 2026: Eye of the Storm runs from its script, which adds its two buffs one after the other; the hand-written admission of the
        // pair as a whole (a table with one free slot refusing the cast) is gone with the module. What stays: with room for both the cast spends
        // its mana, starts its cooldown and sends both buffs, and a recast with all 64 slots occupied replaces them in place.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var self in new[] { false, true })
        {
            using var accepted = new Match("Janna", team, ally: true);
            var caster = accepted.Hero(0);
            var recipient = self ? caster : accepted.Hero(2);
            FillChampionCapacity(accepted.Runtime.Buffs, recipient, 62);
            var baseDamage = recipient.AttackDamage;
            var baseMana = caster.Mana;
            accepted.Clear();
            TestAssert.True(accepted.TryCast(0, 2, recipient));
            TestAssert.Equal(64, recipient.Buffs.Count);
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == "EyeOfTheStorm"));
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == "JannaEoTSBuff"));
            TestAssert.True(recipient.AttackDamage > baseDamage);
            TestAssert.True(caster.Mana < baseMana && caster.SpellCooldowns[2] > 0.0,
                "ordinary cast still spends mana and starts cooldown");
            // Batch 357 (restates batch 181): the shield's add carries its binding EyeOfTheStorm; Eye Of The Storm has no script.
            TestAssert.True(HasCapacityBuffAdd(accepted, recipient, "EyeOfTheStorm") &&
                            HasCapacityBuffAdd(accepted, recipient, "JannaEoTSBuff"),
                "successful cast emits both existing buff packets");

            var boostedDamage = recipient.AttackDamage;
            caster.SpellCooldowns[2] = 0.0;
            caster.Mana = caster.MaxMana;
            accepted.Clear();
            TestAssert.True(accepted.TryCast(0, 2, recipient));
            TestAssert.Equal(64, recipient.Buffs.Count);
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == "EyeOfTheStorm"));
            TestAssert.Equal(1, recipient.Buffs.Count(buff => buff.Name == "JannaEoTSBuff"));
            TestAssert.Near(boostedDamage, recipient.AttackDamage, 1e-8,
                "renewal replaces rather than stacking the damage buff");
        }
    }

    [Check("spells-live-blitz-mana-barrier-capacity-pair")]
    public static void BlitzManaBarrierCapacityPair()
    {
        // 19 October 2026 (Blitzcrank from his scripts): ManaBarrierIcon.BuffOnPreDamage does the arithmetic, then draws
        // SteamGolemShield_hit, then adds ManaBarrier (the shield, only when the mana covers the whole block) and
        // ManaBarrierCooldown, in that order. The table's free slots decide which of the two buffs exist afterwards:
        // the engine's 64-slot guard declines the add of a buff that does not fit.
        // Batch 149 part 1 (91e4e2a6) then made a refused add stop half-applying: BbBuffRuntime.Add raises
        // BbHookFrame.GroupDenied and BbHookRouter.RunPreDamage keeps the amount the hook was given ("a refused add does
        // not half-apply", owner decision batch 147, first needed by Spells/WillRevive.luaobj's revival). The hook has
        // two add sites, so the rule reaches it whenever either add is refused: a full table (64) refuses ManaBarrier
        // and the 63-row refuses ManaBarrierCooldown, and in both the hit lands whole - the arithmetic, the particle
        // and the buff adds are the same declined run. The rows with room for both (62) or with no ManaBarrier add at
        // all (63 used, 40 mana: the shield's amount falls short, so only the cooldown row is written) keep the
        // script's own figures.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (used, mana, expectedLoss, cooldown, barrier) in new[]
                 {
                     (64, 200.0, 300.0, false, false),
                     (63, 200.0, 300.0, false, true),
                     (62, 200.0, 250.0, true, true),
                     (63, 40.0, 280.0, true, false),
                 })
        {
            using var match = new Match("Blitzcrank", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Health = hero.MaxHealth * .25;
            hero.Mana = mana;
            FillChampionCapacity(match.Runtime.Buffs, hero, used);
            var startingHealth = hero.Health;
            match.Clear();
            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 300,
                DamageType.True, DamageSourceKind.Spell));
            match.Runtime.Delivery.Flush();
            TestAssert.Near(startingHealth - expectedLoss, hero.Health, 1e-8,
                $"{team}/{used}/{mana}: post-mitigation health matches the admitted passive");
            TestAssert.Equal(cooldown,
                hero.Buffs.Any(buff => buff.Name == "ManaBarrierCooldown"));
            TestAssert.Equal(barrier, hero.Buffs.Any(buff => buff.Name == "ManaBarrier"));
            TestAssert.True(match.Runtime.GameData.HasClientBuff("ManaBarrierCooldown"),
                $"{team}/{used}/{mana}: cooldown uses the shipped client buff script");
            // Batch 357 (restates batch 181): the admitted cooldown's add carries its binding ManaBarrierCooldown; BlitzcrankManaBarrierCD has no script
            // (the Data\Buffs name BlitzcrankManaBarrierCD never reaches the wire).
            TestAssert.Equal(cooldown, HasCapacityBuffAdd(match, hero, "ManaBarrierCooldown"),
                $"{team}/{used}/{mana}: admitted cooldown sends its binding name");
            TestAssert.False(HasCapacityBuffAdd(match, hero, "BlitzcrankManaBarrierCD"),
                $"{team}/{used}/{mana}: the binding name is never sent");
            TestAssert.Equal(barrier, HasCapacityBuffAdd(match, hero, "ManaBarrier"));
            TestAssert.True(HasEffect(match.Captures[0], "SteamGolemShield_hit"),
                "the script draws the barrier's hit effect before it adds either buff");
            if (barrier)
            {
                // The shield the buff keeps: ManaShield (half the mana) less the amount this hit used (amountToSubtract), which the buff's first update takes off.
                var vars = ((BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Blitzcrank")!).Buffs!.Instances
                    .Single(item => item.Name == "ManaBarrier" && ReferenceEquals(item.Owner, hero)).InstanceVars;
                TestAssert.Near(50.0, (double)vars["ManaShield"]! - (double)vars["amountToSubtract"]!,
                    1e-8, "ordinary threshold hit stores the remaining mana shield");
            }
        }
    }

    private static void FillChampionCapacity(BuffLifecycle buffs, Entity target, int total)
    {
        for (var index = 0; target.Buffs.Count < total; index++)
            TestAssert.True(buffs.AddBuff(target, $"CapacityProbe{index}", BuffType.Internal,
                60.0, target.NetId) is not null);
        TestAssert.Equal(total, target.Buffs.Count);
    }

    private static bool HasCapacityBuffAdd(Match match, Entity target, string name) =>
        match.Captures[0].Packets.Any(packet =>
            Opcode(packet) == 0xBF && packet.Plaintext.Length >= 13 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                SpellPackets.BuffNameHash(name));
}
