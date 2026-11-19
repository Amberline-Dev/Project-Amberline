using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 906: 414 Improved Recall (1.0.0.132; batch 902's open item). Talents/414.luaobj adds MasteryImprovedRecallBuff while the hero lacks
    /// it (BUFF_REPLACE_EXISTING, BUFF_Internal, 5 s); its <c>OnBuffActivate</c> reads <c>GetSlotSpellName</c> of inventory slot 6 (the map's
    /// recall item, spell slot 10) and, for <c>Recall</c>, writes <c>RecallImproved</c> there (<c>BBSetSpell</c>; <c>OdinRecall</c> becomes
    /// <c>OdinRecallImproved</c>). RecallImproved.luaobj is Recall with <c>ChannelDuration</c> 7 (Recall 8) and TeleportHomeImproved.troy.
    /// Links: (a) the slot holds RecallImproved on the server, (b) the last name seat 0's client got for that slot is RecallImproved, (c) the
    /// cast answer names RecallImproved and its total is the cast time plus 7, (d) the teleport home lands at that total and not before,
    /// (e) a hero without the talent keeps Recall's cast time plus 8, (f) a hit cancels the improved channel (its buff and its effect go),
    /// (g) a level-up, a death and the 5 s re-adds keep the slot and send it no second name, and the recall after the respawn is still 7 s.
    /// </summary>
    [Check("masteries-live-b906-p132-improved-recall-swaps-in-the-seven-second-recall")]
    public static void B906ImprovedRecall()
    {
        var failures = new List<string>();
        var data = GameData.Default;
        var improvedCast = data.Spell("RecallImproved").CastTime;
        var improvedTotal = (float)(improvedCast + data.SpellChannelDuration("RecallImproved"));
        var plainCast = data.Spell("Recall").CastTime;
        var plainTotal = (float)(plainCast + data.SpellChannelDuration("Recall"));
        if (Math.Abs(data.SpellChannelDuration("RecallImproved") - 7.0) > 1e-9 || Math.Abs(data.SpellChannelDuration("Recall") - 8.0) > 1e-9)
            failures.Add($"channel durations {data.SpellChannelDuration("RecallImproved")} / {data.SpellChannelDuration("Recall")} (7 / 8 shipped)");

        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Annie",
            masteries: B902Page(new() { [411] = 1, [414] = 1 }));
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
        enemy.Position = new Position2(3000, 11500);
        ally.Position = new Position2(7000, 6500);
        var spawn = MapDefinitions.TeamSpawn(Team.Order);
        string Slot10(Entity unit) => unit.ActiveSpellNames.Count > SpellSystem.RecallSlot ? unit.ActiveSpellNames[SpellSystem.RecallSlot] : "";
        string? SlotName(PlaintextPacket packet)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length == 14 && bytes[0] == 0x74 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == hero.NetId && bytes[13] == SpellSystem.RecallSlot)
            {
                var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
                return hash == SpellSystem.ProtocolHash("RecallImproved") ? "RecallImproved" : hash == SpellSystem.ProtocolHash("Recall") ? "Recall" : $"#{hash:X8}";
            }
            if (bytes.Length > 7 && bytes[0] == 0x35 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == hero.NetId && bytes[5] == SpellSystem.RecallSlot)
                return Encoding.ASCII.GetString(bytes[6..^1]);
            return null;
        }

        // (a), (b)
        if (Slot10(hero) != "RecallImproved") failures.Add($"(a) seat 0's recall slot holds '{Slot10(hero)}'");
        if (!hero.Buffs.Any(buff => buff.Name == "MasteryImprovedRecallBuff")) failures.Add("(a) no MasteryImprovedRecallBuff on the hero");
        if (Slot10(ally) is { Length: > 0 } other && other != "Recall") failures.Add($"(a) the ally without the talent holds '{other}'");
        var named = match.SpawnBursts[0].Concat(match.Captures[0].Packets).Select(SlotName).Where(name => name is not null).ToList();
        if (named.LastOrDefault() != "RecallImproved") failures.Add($"(b) seat 0's client got recall slot names [{string.Join(", ", named)}]");

        // (c), (d)
        var caster = match.Captures[0];
        runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        var hashed = caster.Packets.Any(packet => packet.Plaintext.Span is var bytes && bytes.Length > 9 && bytes[0] == SpellPackets.CastSpellAnswerOpcode &&
            bytes.IndexOf(BitConverter.GetBytes(SpellSystem.ProtocolHash("RecallImproved"))) >= 0);
        if (!hashed) failures.Add("(c) no cast answer names RecallImproved");
        if (!ContainsCastTimes(caster, (float)improvedCast, 0f, improvedTotal)) failures.Add($"(c) no cast answer carries {improvedCast} / {improvedTotal}");
        runtime.Tick(improvedCast);
        runtime.Delivery.Flush();
        var effect = runtime.Spells.RecallEffectId(hero);
        if (effect == 0 || EffectCreatesNamed(caster, effect, "TeleportHomeImproved") != 1) failures.Add("(d) the channel did not begin with TeleportHomeImproved");
        for (var step = 0; step < 69; step++) runtime.Tick(0.1);
        if (runtime.Spells.RecallRemaining(hero) <= 0.0 || RecallDistance(hero.Position, spawn) < 1000.0)
            failures.Add($"(d) not channelling {improvedCast + 6.9:F1} s after the press");
        runtime.Tick(0.1);
        if (runtime.Spells.RecallRemaining(hero) != 0.0 || RecallDistance(hero.Position, spawn) > 25.0)
            failures.Add($"(d) not home {improvedTotal:F1} s after the press (remaining {runtime.Spells.RecallRemaining(hero):F2})");

        // (e) the ally without the talent: Recall, the cast time plus 8.
        runtime.Delivery.Flush(); match.Clear();
        match.Cast(2, SpellSystem.RecallSlot, ally);
        if (!ContainsCastTimes(match.Captures[2], (float)plainCast, 0f, plainTotal)) failures.Add($"(e) the ally's cast answer does not carry {plainCast} / {plainTotal}");
        runtime.Tick(plainCast + 7.5);
        if (runtime.Spells.RecallRemaining(ally) <= 0.0) failures.Add("(e) the ally's recall ended before 8 s");
        runtime.Tick(0.6);
        if (RecallDistance(ally.Position, spawn) > 25.0) failures.Add("(e) the ally is not home after its 8 s recall");

        // (f) a hit cancels the improved channel.
        hero.Position = new Position2(7000, 7000);
        runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        runtime.Tick(improvedCast + 1.0);
        effect = runtime.Spells.RecallEffectId(hero);
        runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10.0, sourceIsPeriodic: false);
        runtime.Tick(0.3);
        runtime.Delivery.Flush();
        if (runtime.Spells.RecallRemaining(hero) != 0.0) failures.Add("(f) a hit did not cancel the improved recall");
        if (hero.Buffs.Any(buff => buff.Name is "RecallImproved" or "Recall")) failures.Add("(f) the recall buff stayed after the cancel");
        if (effect == 0 || CountKill(caster, effect) != 1) failures.Add("(f) TeleportHomeImproved was not removed on the cancel");

        // (g) a level-up, a death and two 5 s cycles of the talent's re-add.
        match.Clear();
        hero.Level = 2;
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.LoadoutHooks?.OwnerFor(hero);
        B902Advance(match, 1.0);
        runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 1.0e6, DamageType.True, DamageSourceKind.Spell);
        B902Advance(match, 20.0);
        if (!hero.Alive) failures.Add("(g) the hero did not respawn in 20 s");
        runtime.Delivery.Flush();
        var resent = caster.Packets.Select(SlotName).Where(name => name is not null).ToList();
        if (Slot10(hero) != "RecallImproved" || resent.Count > 0)
            failures.Add($"(g) after a level-up, a death and 21 s the slot holds '{Slot10(hero)}' and seat 0 got [{string.Join(", ", resent)}]");
        hero.Position = new Position2(7000, 7000);
        match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        if (!ContainsCastTimes(caster, (float)improvedCast, 0f, improvedTotal)) failures.Add("(g) the recall after the respawn is not the 7 s one");
        TestAssert.True(failures.Count == 0, "Improved Recall: " + string.Join("; ", failures));
    }
}
