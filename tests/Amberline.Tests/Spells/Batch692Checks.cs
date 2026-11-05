using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 692 (community report "Using Katarina E on a target that kills it returns katarina to previous position"). The cast announcement goes out
    /// when the cast is accepted, before ShadowStep.luaobj's TargetExecute teleports her and deals the damage that kills, so the original server
    /// could never put the foe's death on the wire ahead of the cast that caused it. Every client gets the step in that order: the cast (0xBD),
    /// the teleport (0x64) and the hit (0x68), then the death (0x61). It was the death first (lifecycle packets ahead of the step), so the
    /// clients saw a cast aimed at a corpse.
    /// </summary>
    [Check("replication.killing-cast-reaches-clients-before-the-death")]
    public static void KillingCastReachesClientsBeforeTheDeath()
    {
        foreach (var prior in new[] { "stand", "walk", "attack" })
        {
            using var match = new Match("Katarina", Team.Order);
            var kat = match.Hero(0);
            var foe = match.Hero(1);
            kat.Position = new Position2(7000, 7000);
            foe.Position = new Position2(7400, 7000);
            foe.Health = 10.0;
            match.Runtime.Tick(0.5);
            if (prior == "walk") match.Move(0, new Position2(7000, 6000));
            else if (prior == "attack") match.Attack(0, foe);
            match.Runtime.Tick(0.3);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 2, foe);
            match.Runtime.Tick(1.0 / 30.0);
            match.Runtime.Delivery.Flush();
            TestAssert.False(foe.Alive, $"{prior}: Shunpo's hit kills the foe");
            for (var seat = 0; seat < 2; seat++)
            {
                var packets = match.Captures[seat].Packets;
                var death = packets.FindIndex(packet => Opcode(packet) == 0x61);
                var cast = packets.FindIndex(packet => Opcode(packet) == SpellPackets.CastSpellAnswerOpcode);
                var teleport = packets.FindIndex(packet => Opcode(packet) == VisionPackets.TeleportOpcode);
                var hit = packets.FindIndex(packet => Opcode(packet) == 0x68);
                var label = $"{prior}, seat {seat}";
                TestAssert.True(death >= 0 && cast >= 0 && teleport >= 0 && hit >= 0,
                    $"{label}: the death, the cast, the teleport and the hit reach the client ({death}, {cast}, {teleport}, {hit})");
                TestAssert.True(cast < death && teleport < death && hit < death,
                    $"{label}: the cast ({cast}), the teleport ({teleport}) and the hit ({hit}) come before the death ({death})");
            }
        }
    }

    /// <summary>
    /// Batch 692, the same rule for killing dashes (Irelia Q, Jax Q, Pantheon W; Akali R is refused in this fixture): the cast, the dash or teleport and the hit reach every
    /// client before the death they cause, in the step of the kill as well (the death is sent in its place in the step).
    /// </summary>
    [Check("replication.killing-dash-reaches-clients-before-the-death")]
    public static void KillingDashReachesClientsBeforeTheDeath()
    {
        foreach (var (champion, slot) in new[] { ("Irelia", 0), ("Jax", 0), ("Pantheon", 1) })
        {
            using var match = new Match(champion, Team.Order);
            var hero = match.Hero(0);
            var foe = match.Hero(1);
            hero.Position = new Position2(7000, 7000);
            foe.Position = new Position2(7350, 7000);
            foe.Health = 10.0;
            match.Runtime.Tick(0.5);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, slot, foe);
            for (var step = 0; step < 90 && foe.Alive; step++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                match.Runtime.Delivery.Flush();
            }
            TestAssert.False(foe.Alive, $"{champion}: the dash's hit kills the foe");
            for (var seat = 0; seat < 2; seat++)
            {
                var packets = match.Captures[seat].Packets;
                var death = packets.FindIndex(packet => Opcode(packet) == 0x61);
                var cast = packets.FindIndex(packet => Opcode(packet) == SpellPackets.CastSpellAnswerOpcode);
                var move = packets.FindIndex(packet => Opcode(packet) is VisionPackets.TeleportOpcode or 0x67);
                var hit = packets.FindLastIndex(death < 0 ? 0 : death, packet => Opcode(packet) == 0x68);
                var label = $"{champion}, seat {seat}";
                TestAssert.True(death >= 0 && cast >= 0 && move >= 0 && hit >= 0,
                    $"{label}: the death, the cast, the move and the hit reach the client ({death}, {cast}, {move}, {hit})");
                TestAssert.True(cast < death && move < death && hit < death,
                    $"{label}: the cast ({cast}), the move ({move}) and the hit ({hit}) come before the death ({death})");
            }
        }
    }

    /// <summary>
    /// Batch 692 (community report "minions hitting player do not cancel recall"): what the recalling client gets when a lane minion's hit ends the
    /// channel. The stock client ends its recall on the forced stop alone: the 0x39 handler with ForceStop (0x04) and no
    /// AvatarSpell takes the hero's own spellbook's spellcasting object, Recall's SpellCaster, and finishes it (vtable +0x10) with
    /// KeepAnimating clear, so the client hides the local hero's channel bar and stops the channel pose
    ///. The TeleportHome kill (0x3D) and the Recall buff removal (0x7F) are the script's ChannelingCancelStop. This pins that wire
    /// for both clients, the recaller's and the enemy's.
    /// </summary>
    [Check("spells-recall-minion-hit-sends-the-forced-stop")]
    public static void RecallMinionHitSendsTheForcedStop()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        match.Hero(1).Position = new Position2(hero.Position.X + 300.0, hero.Position.Z);
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(1.0);
        var effect = match.Runtime.Spells.RecallEffectId(hero);
        TestAssert.True(effect != 0, "the channel runs with its TeleportHome effect");
        var minion = match.Runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0.0, 0.0);
        minion.Position = new Position2(hero.Position.X + 100.0, hero.Position.Z);
        minion.Waypoints.Clear();
        match.Runtime.Delivery.Flush(); match.Clear();
        var health = hero.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(minion, hero, new AttackRolls(1.0, 1.0, 1.0)), "the minion swings");
        for (var step = 0; step < 40 && match.Runtime.Spells.RecallRemaining(hero) > 0.0; step++)
        {
            match.Runtime.Tick(0.05);
            match.Runtime.Delivery.Flush();
        }
        TestAssert.True(hero.Health < health, "the minion's hit lands");
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "the hit cancels the recall");
        for (var seat = 0; seat < 2; seat++)
        {
            var packets = match.Captures[seat].Packets;
            var stops = packets.Where(packet => Opcode(packet) == 0x39 && packet.Plaintext.Length == 6 &&
                BitConverter.ToUInt32(packet.Plaintext.Span[1..5]) == hero.NetId).ToList();
            TestAssert.Equal(1, stops.Count, $"seat {seat}: one stop for the recaller");
            TestAssert.Equal(CombatPackets.ForceStopFlags, stops[0].Plaintext.Span[5], $"seat {seat}: ForceStop alone (no KeepAnimating, no AvatarSpell)");
            var kill = packets.FindIndex(packet => Opcode(packet) == FxPackets.KillOpcode);
            var removal = packets.FindIndex(packet => Opcode(packet) == 0x7F);
            var stop = packets.IndexOf(stops[0]);
            TestAssert.True(kill >= 0 && removal >= 0 && kill < stop && removal < stop,
                $"seat {seat}: the TeleportHome kill ({kill}) and the buff removal ({removal}) precede the stop ({stop})");
        }
    }

    /// <summary>
    /// Batch 692, the same rule for the other channels: a stun ends Fiddlesticks' Drain (its child DrainChannel draws the bar), Katarina's Death
    /// Lotus and Nunu's Absolute Zero through BbChannels.Stop, which sends the caster's clients the forced stop (0x39, ForceStop 0x04) that ends
    /// the book's current SpellCaster, so the bar and the pose end with the channel.
    /// </summary>
    [Check("spells-channels-stun-sends-the-forced-stop")]
    public static void ChannelsStunSendsTheForcedStop()
    {
        foreach (var (champion, slot, onFoe) in new[] { ("FiddleSticks", 1, true), ("Katarina", 3, false), ("Nunu", 3, false) })
        {
            using var match = new Match(champion, Team.Order);
            var hero = match.Hero(0);
            var foe = match.Hero(1);
            foe.Position = new Position2(hero.Position.X + 250.0, hero.Position.Z);
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, slot, onFoe ? foe : hero);
            match.Runtime.Tick(1.0);
            match.Runtime.Delivery.Flush();
            TestAssert.True(hero.ActiveScriptChannels > 0, $"{champion}: the channel runs");
            match.Clear();
            TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "B692Stun", Amberline.Core.Buffs.BuffType.Stun, 1.0, foe.NetId,
                canMitigateDuration: false) is not null, $"{champion}: the stun lands");
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(0, hero.ActiveScriptChannels, $"{champion}: the stun ends the channel");
            var stops = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x39 && packet.Plaintext.Length == 6 &&
                BitConverter.ToUInt32(packet.Plaintext.Span[1..5]) == hero.NetId).ToList();
            TestAssert.True(stops.Count == 1 && stops[0].Plaintext.Span[5] == CombatPackets.ForceStopFlags,
                $"{champion}: one forced stop reaches the caster's client ({string.Join(",", stops.Select(stop => stop.Plaintext.Span[5]))})");
        }
    }
}
