using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 884 (18 November 2026, 1.0.0.132 player reports): "Volibear's Q sometimes appears to fail flinging enemies. The flinging animation
/// happens, but no fling actually happens" and "Volibear's E fails to fear minions and monsters". docs/maintenance/2026-11-18-batch-884-volibear.md.
/// </summary>
public static class BbB884VolibearChecks
{
    // VolibearQ (4 s) ends with BBRemoveOverrideAutoAttack CancelAttack=false. A swing that started as VolibearQAttack (the 0xBE cast the client
    // plays) and whose windup the buff's end falls into still lands as VolibearQAttack: its TargetExecute adds VolibearQExtra, the fling.
    [Check("scripting.b884-volibear-rolling-thunder-swing-started-armed-flings-when-the-buff-ends-in-its-windup")]
    public static void RollingThunderSwingStartedArmedStillFlings()
    {
        using var match = new Match("Volibear", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        var minion = LaneMinion(match, match.Hero.Position + new Position2(150, 0));
        match.Runtime.Minions!.HaltAI(minion);
        minion.Waypoints.Clear();
        var wire = Observe(match, Team.Order, match.Hero);
        var swing = SpellSystem.ProtocolHash("VolibearQAttack");
        TestAssert.True(match.Cast(0), "Q is cast");
        var cast = match.Now;
        // The swing starts 3.8 s into the 4 s buff, so the buff ends in its 0.36 s windup.
        match.Advance(3.8);
        TestAssert.True(Buff(match.Hero, "VolibearQ") is not null, "fixture: Rolling Thunder is still up at 3.8 s; buffs: " + Names(match.Hero));
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, minion);
        double? sent = null, ended = null, flung = null;
        for (var step = 0; step < 45; step++)
        {
            match.Advance(Step);
            if (sent is null && wire.Packets.Any(item => MissileOf(item.Packet) is { } info && info.Spell == swing)) sent = match.Now;
            if (ended is null && Buff(match.Hero, "VolibearQ") is null) ended = match.Now;
            if (flung is null && Buff(minion, "VolibearQExtra") is not null) flung = match.Now;
        }
        var story = $"cast {R(cast)}, swing sent {R(sent ?? -1)}, buff ended {R(ended ?? -1)}, fling {R(flung ?? -1)}, windup {R(match.Hero.AttackWindup)}";
        TestAssert.True(sent is not null, "the swing leaves as VolibearQAttack's cast (the fling animation): " + story);
        TestAssert.True(ended is not null && ended > sent && ended < sent + match.Hero.AttackWindup,
            "fixture: the buff ends inside the swing's windup: " + story);
        TestAssert.True(flung is not null, "the swing that started armed lands as VolibearQAttack and flings: " + story);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // VolibearE SelfExecute: every unit in 425 is slowed and hit; one that is not a hero and not dead is feared for 2 s (BBApplyFear). The 132
    // Fear.luaobj draws Global_Fear.troy on the feared unit from its OnBuffActivate and removes it in OnBuffDeactivate (AutoBuffActivateEffect "").
    [Check("scripting.b884-volibear-majestic-roar-fears-minions-and-monsters-with-the-fear-particle")]
    public static void MajesticRoarFearsMinionsAndMonsters()
    {
        var e = Script("VolibearE");
        TestAssert.True(Blocks(e, "SelfExecute").Any(block => block.Function == "BBApplyFear" && HasNumber(block, "Duration", 2)),
            "the shipped VolibearE fears for 2 s");
        var fear = Script("Fear");
        TestAssert.True(fear.Global("AutoBuffActivateEffect") is BbValue.Text { Value: "" }, "the 132 Fear.luaobj has no automatic particle");
        TestAssert.True(Blocks(fear, "OnBuffActivate").Any(block => block.Function == "BBSpellEffectCreate" && block.Text("EffectName") == "Global_Fear.troy"),
            "the 132 Fear.luaobj draws Global_Fear.troy itself");
        var failures = new List<string>();
        Roar("lane minion", match => LaneMinion(match, match.Hero.Position + new Position2(200, 0)), feared: true);
        Roar("camp monster", match => CampMonster(match), feared: true);
        Roar("champion", match =>
        {
            match.Foe.Position = match.Hero.Position + new Position2(200, 0);
            return match.Foe;
        }, feared: false);
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Roar(string name, Func<Match, Entity> place, bool feared)
        {
            using var match = new Match("Volibear", foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            var unit = place(match);
            var wire = Observe(match, Team.Order, match.Hero);
            // A fixture minion is on the wire (Entity.Created) once a client's view knows it, from the next flush; a fear on a unit no client
            // knows draws nothing (BBSpellEffectCreate drops such an effect the same way).
            match.Advance(0.2);
            match.Learn(1);
            match.Refill();
            if (!match.Cast(2))
            {
                failures.Add($"{name}: E was not cast");
                return;
            }
            match.Advance(0.2);
            if (Buff(unit, "VolibearE") is null) failures.Add($"{name}: no VolibearE slow; buffs {Names(unit)}");
            var held = unit.Buffs.FirstOrDefault(buff => buff.Name == "Fear");
            if (!feared)
            {
                if (held is not null) failures.Add($"{name}: feared, but the script fears no hero");
                return;
            }
            if (held is null)
            {
                failures.Add($"{name}: not feared; buffs {Names(unit)}");
                return;
            }
            var effect = held.Payload.FxNetId;
            if (effect == 0 || !wire.Packets.Any(item => EffectNamed(item.Packet, "Global_Fear", "global_fear")))
                failures.Add($"{name}: no Global_Fear.troy reached Volibear's client (effect id {effect})");
            match.Advance(2.0);
            if (unit.Buffs.Any(buff => buff.Name == "Fear")) failures.Add($"{name}: the fear outlived its 2 s");
            var kill = FxPackets.Kill(effect).Plaintext;
            if (effect != 0 && !wire.Packets.Any(item => item.Packet.Plaintext.Span.SequenceEqual(kill.Span)))
                failures.Add($"{name}: the fear particle was not removed with the fear");
            if (match.Module.ScriptFailures.Count != 0) failures.Add($"{name}: {match.Module.ScriptFailures.Count} script failures");
        }
    }

    // The particle ApplyFear draws (persistent, for late observers) leaves with a feared unit that dies: a minion killed under Majestic Roar,
    // a champion killed under Terrify. A client that first sees the area afterwards gets no fear particle.
    [Check("scripting.b884-fear-particle-leaves-with-a-feared-unit-that-dies")]
    public static void FearParticleLeavesWithAFearedUnitThatDies()
    {
        var failures = new List<string>();
        Die("lane minion under Majestic Roar", "Volibear", match => LaneMinion(match, match.Hero.Position + new Position2(200, 0)),
            (match, _) => match.Cast(2));
        Die("champion under Terrify", "FiddleSticks", match =>
        {
            match.Foe.Position = match.Hero.Position + new Position2(300, 0);
            return match.Foe;
        }, (match, unit) => match.Cast(0, unit));
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Die(string name, string champion, Func<Match, Entity> place, Func<Match, Entity, bool> cast)
        {
            using var match = new Match(champion, foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            var unit = place(match);
            var wire = Observe(match, Team.Order, match.Hero);
            match.Advance(0.2);
            match.Learn(1);
            match.Refill();
            if (!cast(match, unit))
            {
                failures.Add($"{name}: not cast");
                return;
            }
            match.Advance(0.5);
            var effect = unit.Buffs.FirstOrDefault(buff => buff.Name == "Fear")?.Payload.FxNetId ?? 0;
            if (effect == 0)
            {
                failures.Add($"{name}: no fear particle drawn; buffs {Names(unit)}");
                return;
            }
            match.Runtime.Combat.ApplyHit(match.Hero.NetId, unit.NetId, 100000);
            match.Advance(0.5);
            if (unit.Alive) failures.Add($"{name}: fixture: the unit survived");
            var kill = FxPackets.Kill(effect).Plaintext;
            if (!wire.Packets.Any(item => item.Packet.Plaintext.Span.SequenceEqual(kill.Span)))
                failures.Add($"{name}: the fear particle was not removed at the death");
            var late = Observe(match, Team.Order, match.Ally, id: 9002);
            match.Advance(0.5);
            if (late.Packets.Any(item => EffectNamed(item.Packet, "Global_Fear", "global_fear")))
                failures.Add($"{name}: a later client still got the dead unit's fear particle");
        }
    }

    private static bool HasNumber(BbBlock block, string key, double value) =>
        block.Param(key) is BbValue.Number number && Math.Abs(number.Value - value) < 1e-6;

    /// <summary>The first camp member of the production map once the camps have spawned, with Volibear 200 west of it.</summary>
    private static Entity CampMonster(Match match)
    {
        for (var tick = 0; tick < 800 && !match.Runtime.World.Entities.Values.Any(IsCampMember); tick++) match.Runtime.Tick(0.25);
        var monster = match.Runtime.World.Entities.Values.Where(IsCampMember).OrderBy(unit => unit.CampId).First();
        match.Hero.Position = monster.Position + new Position2(-200, 0);
        match.Hero.Waypoints.Clear();
        return monster;
    }

    private static bool IsCampMember(Entity unit) =>
        unit.Kind == EntityKind.Neutral && unit.Alive && unit.NeutralMember == 1 && unit.CampId is >= 1 and <= 12;
}
