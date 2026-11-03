using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (26 October 2026, batch 260): Rumble plays from his shipped scripts. Expected numbers are the literals of the shipped scripts (the
/// condensed block trees of <c>DATA/Spells/Rumble*.luaobj</c>, read with the batch's throwaway reader) and the spell inibins (the audit fixture's
/// standalone decode). Each probe runs at rank one and level one with no items, so the bonus attack damage and the ability power are zero; the foe has no
/// spell block, so a magic hit reads its raw damage. Damage is read from the committed hit log (<see cref="HitsOn"/>). The scenario <c>S-Rumble</c> shows
/// the same facts on a headless match. Written 26 October 2026.
/// </summary>
public static class BbRumbleChecks
{
    [Check("scripting.bb-rumble-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Rumble");
        foreach (var name in new[]
                 {
                     "CharScriptRumble", "RumbleHeatSystem", "RumbleOverheat", "RumbleFlameThrower", "RumbleFlameThrowerSpraySuper", "RumbleShield",
                     "RumbleGrenade", "RumbleCarpetBomb", "RumbleOverheatAttack", "RumbleDangerZone", "RumbleHeatDelay",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Rumble");
        TestAssert.Equal(47, closure.ScriptNames.Count(), "the closure is 47 scripts (four spells and their helpers, the passive, the system buffs)");

        var hero = new Entity { ChampionName = "Rumble" };
        TestAssert.Equal(["RumbleFlameThrower", "RumbleShield", "RumbleGrenade", "RumbleCarpetBomb"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Rumble.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Rumble")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 4800, RetailBot: false }, "playable as a top laner for 4800 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Rumble"), "listed once");
    }

    /// <summary>The heat pool decays by ten on each half-second tick (BBIncPAR -5 for HeatDecay and -5 for BonusHeatDecay): sixty heat falls to about thirty in one and a half seconds.</summary>
    [Check("scripting.bb-rumble-heat-decays-ten-a-tick")]
    public static void IdleHeatDecays()
    {
        var match = Setup();
        SetHeat(match, 60.0);
        match.Advance(1.5);
        TestAssert.True(match.Hero.Mana is >= 15.0 and <= 45.0, $"three half-second ticks of -10 take sixty heat to about thirty (got {match.Hero.Mana:0.##})");
    }

    /// <summary>At fifty heat the Danger Zone buff is on (RumbleDangerZone, refreshed on each tick) and the bar is yellow; below fifty it is not, and the bar is white.</summary>
    [Check("scripting.bb-rumble-danger-zone-at-fifty-heat-paints-the-bar")]
    public static void FiftyHeatIsTheDangerZone()
    {
        var match = Setup();
        SetHeat(match, 40.0);
        match.Advance(0.6);
        TestAssert.True(Buff(match.Hero, "RumbleDangerZone") is null, "below fifty there is no Danger Zone");
        TestAssert.True(Colour(match.Hero).Contains("Bar = 4294967295", StringComparison.Ordinal), $"below fifty the bar is white (got {Colour(match.Hero)})");
        SetHeat(match, 50.0);
        match.Advance(0.6);
        TestAssert.True(Buff(match.Hero, "RumbleDangerZone") is not null, "at fifty the Danger Zone is on");
        TestAssert.True(Colour(match.Hero).Contains("Bar = 4294967040", StringComparison.Ordinal), $"at fifty the bar is yellow (got {Colour(match.Hero)})");
    }

    /// <summary>At a hundred heat Overheat starts (5.25 s), the four spells are sealed, and a cast is refused.</summary>
    [Check("scripting.bb-rumble-overheat-at-a-hundred-seals-the-spells")]
    public static void HundredHeatOverheatsAndSeals()
    {
        var match = Setup();
        SetHeat(match, 100.0);
        match.Advance(0.6);
        TestAssert.True(Buff(match.Hero, "RumbleOverheat") is not null, "at a hundred the RumbleOverheat buff is on");
        for (var slot = 0; slot < 4; slot++)
            TestAssert.False(match.CanCast(slot, match.Hero).Allowed, $"slot {slot} is sealed while overheated");
    }

    /// <summary>Overheat drains the heat (-10 a tick, and -20 when it ends) and ends about 5.25 seconds after it starts, with the seals lifted.</summary>
    [Check("scripting.bb-rumble-overheat-drains-and-ends-after-five-seconds")]
    public static void OverheatEnds()
    {
        var match = Setup();
        SetHeat(match, 100.0);
        match.Advance(6.0);
        TestAssert.True(Buff(match.Hero, "RumbleOverheat") is null, "the overheat is over after six seconds");
        TestAssert.Near(0.0, match.Hero.Mana, 1.0, "the heat is drained");
        TestAssert.True(match.CanCast(0, match.Hero).Allowed, "the seals are lifted when the overheat ends");
    }

    /// <summary>Flamespitter adds twenty heat (BBIncPAR of InitialHeatCost) when it is cast and pauses the decay for four seconds (RumbleHeatDelay).</summary>
    [Check("scripting.bb-rumble-flamespitter-adds-twenty-heat-and-pauses-the-decay")]
    public static void FlamespitterAddsHeat()
    {
        var match = Setup();
        SetHeat(match, 0.0);
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast");
        match.Advance(0.4);
        TestAssert.Near(20.0, match.Hero.Mana, 1.0, "the cast adds twenty heat");
        match.Advance(2.0);
        TestAssert.Near(20.0, match.Hero.Mana, 1.0, "the decay is paused while RumbleHeatDelay runs");
    }

    /// <summary>Flamespitter channels six cones of 15 magic damage each (rank one, no ability power) against a foe in front of him.</summary>
    [Check("scripting.bb-rumble-flamespitter-channels-six-cones")]
    public static void FlamespitterHurtsInFront()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast");
        match.Advance(4.0);
        var raw = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        TestAssert.True(raw is >= 75.0 and <= 105.0, $"six cones of 15 magic damage (got {raw:0.##} raw; hits {Hits(match)})");
    }

    /// <summary>At fifty heat the channel carries RumbleFlameThrowerBuff and fires the super cone (Data*ExtraSpell5), 1.3 times the normal damage.</summary>
    [Check("scripting.bb-rumble-flamespitter-fifty-heat-fires-the-super-cone")]
    public static void FiftyHeatSuperCone()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast");
        match.Advance(4.0);
        var normal = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        match.Advance(9.0);
        SetHeat(match, 50.0);
        match.Advance(0.6);
        SetHeat(match, 50.0);
        var before = HitsOn(match, "Foe").Count;
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast at fifty heat");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "RumbleFlameThrowerBuff") is not null,
            $"at fifty heat the cast adds RumbleFlameThrowerBuff (the Danger Zone is on at the cast; buffs {Names(match.Hero)})");
        match.Advance(3.6);
        var super = HitsOn(match, "Foe").Skip(before).Sum(hit => hit.Raw);
        TestAssert.True(super >= normal * 1.2 && super <= normal * 1.4,
            $"the super cone deals 1.3 times the normal one (normal {normal:0.##}, super {super:0.##}; hits {Hits(match)})");
    }

    /// <summary>Scrap Shield is a self-shield for two seconds and a ten percent speed boost for one second (RumbleShieldBuff, rank one).</summary>
    [Check("scripting.bb-rumble-scrap-shield-shields-and-hastes")]
    public static void ScrapShieldHastes()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(1, match.Hero), "Scrap Shield is cast");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "RumbleShield") is not null, "the shield buff is on for two seconds");
        TestAssert.Near(352.0, match.Hero.Speed, 1.5, "the speed boost is ten percent of 320 (RumbleShieldBuff, rank one)");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Hero, "RumbleShield") is null, "the shield is gone after two seconds");
        TestAssert.Near(320.0, match.Hero.Speed, 1.5, "the boost ends with its buff");
    }

    /// <summary>At fifty heat Scrap Shield's boost is thirteen percent (the Danger Zone of the target multiplies the speed by 1.3).</summary>
    [Check("scripting.bb-rumble-scrap-shield-fifty-heat-is-a-third-larger")]
    public static void ScrapShieldFifty()
    {
        var match = Setup();
        SetHeat(match, 50.0);
        match.Advance(0.6);
        SetHeat(match, 50.0);
        TestAssert.True(match.Cast(1, match.Hero), "Scrap Shield is cast at fifty heat");
        match.Advance(0.4);
        TestAssert.Near(361.6, match.Hero.Speed, 1.5, "the speed boost is thirteen percent at fifty heat");
    }

    /// <summary>Electro-Harpoon's second cast is free (SecondCost 0) and ends the first cast's window; its real cooldown then runs (RumbleGrenadeCD).</summary>
    [Check("scripting.bb-rumble-electro-harpoon-second-cast-is-free")]
    public static void HarpoonSecondCastIsFree()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(2, null, null, match.Foe.Position), "the first harpoon is cast");
        match.Advance(0.8);
        TestAssert.Near(20.0, match.Hero.Mana, 1.0, "the first cast adds twenty heat");
        TestAssert.True(Buff(match.Hero, "RumbleGrenadeCounter") is not null, "the second cast window is open");
        TestAssert.True(match.Cast(2, null, null, match.Foe.Position), "the second harpoon is cast");
        match.Advance(0.6);
        TestAssert.Near(20.0, match.Hero.Mana, 1.0, "the second cast costs no heat");
        TestAssert.True(Buff(match.Hero, "RumbleGrenadeCounter") is null, "the second cast closes the window");
        TestAssert.True(Buff(match.Hero, "RumbleGrenadeCD") is not null, "the real cooldown runs (RumbleGrenadeCD)");
    }

    /// <summary>The harpoon hits for 55 magic damage at rank one and slows the foe (RumbleGrenadeSlow); the first hit marks it (RumbleGrenadeDebuff).</summary>
    [Check("scripting.bb-rumble-electro-harpoon-hits-for-fifty-five-and-slows")]
    public static void HarpoonHits()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(2, null, null, match.Foe.Position), "the harpoon is cast at the foe");
        match.Advance(1.5);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => hit.Raw is >= 50.0 and <= 60.0),
            $"the harpoon deals 55 magic damage at rank one (hits {Hits(match)})");
        TestAssert.True(Buff(match.Foe, "RumbleGrenadeSlow") is not null, "the foe is slowed");
        TestAssert.True(Buff(match.Foe, "RumbleGrenadeDebuff") is not null, "the foe is marked for the second hit");
    }

    /// <summary>The Equalizer's first rocket hits the foe under it for 150 magic damage at rank one (BuffOrder or BuffDest guards it for three seconds).</summary>
    [Check("scripting.bb-rumble-equalizer-rocket-hits-once-for-a-hundred-fifty")]
    public static void EqualizerRocket()
    {
        var match = Setup();
        match.Advance(0.8);
        TestAssert.True(match.Cast(3, null, null, match.Foe.Position), "The Equalizer is cast on the foe's spot");
        match.Advance(1.5);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => hit.Raw is >= 140.0 and <= 170.0),
            $"the first rocket deals 150 magic damage (hits {Hits(match)})");
        TestAssert.True(Buff(match.Foe, "RumbleCarpetBombBuffOrder") is not null || Buff(match.Foe, "RumbleCarpetBombBuffDest") is not null,
            "the rocket marks the foe for three seconds");
    }

    /// <summary>Overheat's basic attack adds the magic punch on a champion (25 at level one, plus the physical hit of a normal attack).</summary>
    [Check("scripting.bb-rumble-overheat-attack-punches-champions")]
    public static void OverheatAttackPunches()
    {
        var match = Setup();
        match.Advance(0.8);
        var before = match.Foe.Health;
        Swing(match, match.Foe, 1.5);
        var normal = before - match.Foe.Health;
        SetHeat(match, 100.0);
        match.Advance(0.6);
        before = match.Foe.Health;
        Swing(match, match.Foe, 1.5);
        var overheat = before - match.Foe.Health;
        TestAssert.True(overheat - normal >= 22.0 && overheat - normal <= 28.0,
            $"the overheat attack adds the 25 magic punch (normal {normal:0.##}, overheat {overheat:0.##})");
    }

    /// <summary>A death keeps the heat system (PersistsThroughDeath), and the respawn empties the heat pool (CharOnResurrect).</summary>
    [Check("scripting.bb-rumble-respawn-empties-the-heat")]
    public static void RespawnEmptiesHeat()
    {
        var match = Setup();
        SetHeat(match, 50.0);
        match.Advance(0.6);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        match.Advance(60.0);
        TestAssert.True(match.Hero.Alive, "Rumble respawns");
        TestAssert.True(Buff(match.Hero, "RumbleHeatSystem") is not null, "the heat system is back");
        TestAssert.Near(0.0, match.Hero.Mana, 1.0, "the respawn empties the heat");
    }

    /// <summary>The same empowered cone at character level eleven (the scenario's level): the super cone is still 1.3 times the normal one.</summary>
    [Check("scripting.bb-rumble-flamespitter-fifty-heat-super-cone-at-level-eleven")]
    public static void FiftyHeatSuperConeAtLevelEleven()
    {
        var match = Setup();
        match.Hero.Level = 11;
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast");
        match.Advance(4.0);
        var normal = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        match.Advance(9.0);
        SetHeat(match, 50.0);
        match.Advance(0.6);
        SetHeat(match, 50.0);
        var before = HitsOn(match, "Foe").Count;
        TestAssert.True(match.Cast(0, match.Hero), "Flamespitter is cast at fifty heat");
        match.Advance(4.0);
        var super = HitsOn(match, "Foe").Skip(before).Sum(hit => hit.Raw);
        TestAssert.True(super >= normal * 1.2 && super <= normal * 1.4,
            $"at level eleven the super cone deals 1.3 times the normal one (normal {normal:0.##}, super {super:0.##}; hits {Hits(match)})");
    }

    private static Match Setup()
    {
        var match = new Match("Rumble");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Foe.SpellBlock = 0.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        SetHeat(match, 0.0);
        return match;
    }

    private static void SetHeat(Match match, double heat)
    {
        match.Hero.Mana = heat;
        match.Runtime.Effects.SendStats(match.Hero);
    }

    private static string Colour(Entity hero) => hero.ParColorOverride?.ToString() ?? "-";

    private static string Hits(Match match) =>
        string.Join(" ", HitsOn(match, "Foe").Select(hit => $"{hit.At:0.00}:{hit.Dealt:0.##}/{hit.Raw:0.##}/{hit.Kind}"));

    private static bool Swing(Match match, Entity target, double seconds = 1.0)
    {
        match.Hero.AttackCooldown = 0.0;
        var accepted = match.Runtime.Combat.QueueBasicAttack(match.Hero, target);
        TestAssert.True(accepted, "the swing is queued");
        match.Advance(seconds);
        return accepted;
    }
}
