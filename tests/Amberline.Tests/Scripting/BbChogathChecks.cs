using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Data;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 238 (25 October 2026): Cho'Gath plays from his shipped scripts (wave 7, a retail bot). Expected numbers are the literals of the shipped scripts and
/// spell inibins (Rupture, FeralScream, VorpalSpikes, Feast, Carnivore); the scenario <c>S-chogath</c> shows the same facts on a headless match. No engine rule
/// was added for him, so these checks pin what his scripts already do.
/// </summary>
public static class BbChogathChecks
{
    [Check("scripting.bb-chogath-module-binds-the-shipped-closure")]
    public static void ModuleBindsTheShippedClosure()
    {
        // The closure is 20 scripts with no diagnostic (audit bb-coverage); the construction throws on any diagnostic.
        var module = new BbChampionModule("Chogath", GameData.Default);
        TestAssert.Equal("Chogath", module.ChampionName, "module champion");
        TestAssert.Equal(20, module.ScriptNames.Count, "closure script count");
        foreach (var name in new[] { "CharScriptChogath", "Rupture", "RuptureLaunch", "FeralScream", "VorpalSpikes", "Feast", "Feast_internal", "Carnivore" })
            TestAssert.True(module.ScriptNames.Contains(name), name + " is in the closure");
    }

    // Feast deals true damage FeastHealth (FeastBase at rank 1 plus 0.7 ability power); a unit under that health is executed. A non-hero has FeastBase 1000:
    // a kill. The kill gives the stack (Feast_internal: 90 health and 0.07 of model scale at rank 1).
    [Check("scripting.bb-chogath-feast-executes-a-non-hero-and-stacks")]
    public static void FeastExecutesANonHeroAndStacks()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        var minion = Enemy(match, "ChogathProbeMinion", new Position2(7090, 7000));
        minion.Health = 250.0;
        var maxBefore = match.Hero.MaxHealth;
        TestAssert.True(match.Cast(3, minion), "Feast is accepted on a minion 90 away");
        match.Advance(1.0);
        TestAssert.False(minion.Alive, "the non-hero branch kills a minion whose health is under the damage");
        var stack = Buff(match.Hero, "Feast");
        TestAssert.True(stack is not null && stack.Stacks == 1, "one Feast stack after the kill");
        TestAssert.Near(90.0, match.Hero.MaxHealth - maxBefore, 0.5, "a stack adds 90 health at rank 1");
    }

    // Above the threshold the unit takes the true damage of FeastHealth (300 with no ability power) and gives no stack.
    [Check("scripting.bb-chogath-feast-above-the-threshold-takes-its-damage-only")]
    public static void FeastAboveThresholdTakesItsDamage()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7090, 7000);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(3, match.Foe), "Feast is accepted on a full-health unit 90 away");
        match.Advance(1.0);
        TestAssert.True(match.Foe.Alive, "a unit above the threshold survives");
        TestAssert.Near(300.0, before - match.Foe.Health, 3.0, "true damage FeastBase 300 + 0.7 AP with no ability power");
        TestAssert.True(Buff(match.Hero, "Feast") is null, "no stack without a kill");
    }

    // Rupture is a placemarker at the point: its knock-up stun (RuptureLaunch, 1.0 s) starts 0.75 s after the 0.5 s cast, not at once.
    [Check("scripting.bb-chogath-rupture-stuns-after-the-placemarker")]
    public static void RuptureStunsAfterThePlacemarker()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        var point = new Position2(7500, 7000);
        match.Foe.Position = new Position2(7650, 7000);
        TestAssert.True(match.Cast(0, at: point), "Rupture is accepted at a point 500 away");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "RuptureLaunch") is null, "no stun before the placemarker expires");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "RuptureLaunch") is not null, "the stun has begun by 1.5 s");
    }

    /// <summary>
    /// Batch 583 (the record's known unknown): the client draws a buff from the name hash of the add packet (0xBF, hash at byte 9), and the
    /// server sends the name the script adds. Rupture adds <c>RuptureLaunch</c> (BUFF_Stun, 1 s, Rupture.luaobj; its own BuffName
    /// text is "Rupture"); Feral Scream's silence is <c>BBApplySilence</c> on a hero (FeralScream.luaobj), the shared
    /// <c>Silence</c> buff.
    /// </summary>
    [Check("scripting.bb-chogath-knock-up-and-silence-reach-the-client-by-their-script-names")]
    public static void KnockUpAndSilenceReachTheClientByName()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7650, 7000);
        var wire = Observe(match, Team.Order, match.Hero);
        bool Added(string name) => wire.Packets.Any(item => item.Packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == match.Foe.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash(name));
        TestAssert.True(match.Cast(0, at: new Position2(7500, 7000)), "Rupture at a point 150 from the foe");
        match.Advance(1.6);
        TestAssert.True(Buff(match.Foe, "RuptureLaunch") is not null, "the knock-up is on: " + Names(match.Foe));
        TestAssert.True(Added("RuptureLaunch"), "its add carries the hash of RuptureLaunch");
        match.Advance(1.0);
        match.Foe.Position = new Position2(7200, 7000);
        match.Refill();
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Feral Scream at the foe 200 in front");
        match.Advance(1.0);
        TestAssert.True(Added("Silence"), "the silence's add carries the hash of Silence");
    }

    /// <summary>
    /// Batch 583 (the record's open question: a hero that regenerates during the cast was left with a fraction). Feast.TargetExecute reads the
    /// target's health (BBGetPAROrHealth) and, when FeastHealth is at least that, deals exactly it as true damage in the
    /// same block: the regeneration of the 0.5 s cast is read with it, so the hero dies.
    /// </summary>
    [Check("scripting.bb-chogath-feast-kills-a-regenerating-hero-under-the-threshold")]
    public static void FeastKillsARegeneratingHero()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7090, 7000);
        match.Foe.Health = 250.0;
        TestAssert.True(match.Foe.HealthRegen > 0.0, "the foe regenerates");
        TestAssert.True(match.Cast(3, match.Foe), "Feast on a hero under 300");
        match.Advance(1.0);
        TestAssert.False(match.Foe.Alive, $"the execute leaves nothing (health {match.Foe.Health:0.000000}; hits " +
            string.Join(",", HitsOn(match, "Foe").Select(hit => $"{R(hit.At)}:{R(hit.Dealt)}")) + ")");
        TestAssert.True(Buff(match.Hero, "Feast") is { Stacks: 1 }, "and gives the stack");
    }

    // Vorpal Spikes is a toggle that costs no mana: the first cast adds the aura, the second removes it.
    [Check("scripting.bb-chogath-vorpal-spikes-toggles-without-mana")]
    public static void VorpalSpikesTogglesWithoutMana()
    {
        using var match = new Match("Chogath", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Hero), "Vorpal Spikes is accepted on Cho'Gath");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "VorpalSpikes") is not null, "the aura is on after the first cast");
        TestAssert.True(mana - match.Hero.Mana < 1.0, "the toggle costs no mana");
        match.Advance(1.0);
        TestAssert.True(match.Cast(2, match.Hero), "Vorpal Spikes toggles off");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "VorpalSpikes") is null, "the aura is off after the second cast");
    }
}
