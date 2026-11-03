using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 262): FiddleSticks plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed; not a retail bot, so the generic bot template applies). Expected numbers are the literals of the shipped scripts and inibins: the
/// passive <c>Paranoia</c> (<c>ParanoiaMissChance</c>: 10 magic resist less inside 800, renewed every second), <c>Terrify</c> (a fear of
/// <c>DurationByLevel</c> 1.0 .. 3.0 seconds), <c>Drain</c> (a five-second channel: 30 .. 90 magic damage and the life steal of 60 .. 80 percent
/// every half second, the stop beyond 650), <c>FiddlesticksDarkWind</c> (65 .. 145 magic damage, the crow's four bounces and the silence),
/// <c>Crowstorm</c> (a 1.5-second channel, the blink to the point, a storm of 62.5 .. 162.5 magic damage every half second for five seconds).
/// The scenario <c>S-fiddlesticks</c> shows the same facts, and the death and respawn, on a headless match.
/// </summary>
public static class BbFiddleSticksChecks
{
    // The closure's shared system buffs: the closure check counts them, the engine runs their shipped scripts as well.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // Dread takes ten magic resist from a unit inside 800 of FiddleSticks (ParanoiaMissChance's OnBuffActivate, BBIncPermanentStat -10).
    private const double DreadResist = 10.0;

    /// <summary>A match with FiddleSticks at rank <paramref name="rank"/>, the enemy 300 away (inside every range that is checked).</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("FiddleSticks", foeChampion: "Orianna");
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(6000, 6000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Foe.Armor = 20.0;
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Refill();
        return match;
    }

    private static string Describe(Entity unit) => Names(unit);

    // ------------------------------------------------------------------------------------------------------------
    // Closure and passive
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-fiddlesticks-module-binds-the-shipped-closure")]
    public static void ModuleBindsTheShippedClosure()
    {
        var module = new BbChampionModule("FiddleSticks");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("FiddleSticks");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        foreach (var name in new[] { "CharScriptFiddleSticks", "Terrify", "Drain", "DrainChannel", "DrainCheck", "FiddlesticksDarkWind",
                     "FiddleSticksDarkWindMissile", "DarkWind", "Crowstorm", "Paranoia", "ParanoiaMissChance", "GlobalDrain", "Fearmonger_marker" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        TestAssert.Equal(21, closure.ScriptNames.Count(), "the closure is 21 scripts: the character, four spells, the passive and the scripts they reference");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of them are the shared system buffs");

        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a FiddleSticks hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-fiddlesticks-dread-takes-ten-magic-resist-inside-800")]
    public static void DreadTakesTenMagicResistInsideEight()
    {
        using var match = Arena();
        match.Advance(1.5);
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        TestAssert.True(Buff(match.Hero, "Paranoia") is not null, "the passive aura is on FiddleSticks from the start: " + Describe(match.Hero));
        TestAssert.True(Buff(match.Foe, "ParanoiaMissChance") is not null, "the enemy 300 away carries the miss-chance buff: " + Describe(match.Foe));
        TestAssert.Near(30.0 - DreadResist, match.Foe.SpellBlock, 1e-6, "the enemy's magic resist is 10 lower");
        TestAssert.True(Buff(match.FarFoe, "ParanoiaMissChance") is null, "an enemy 6000 away is not marked: " + Describe(match.FarFoe));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q: Terrify
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-fiddlesticks-terrify-fears-the-target-for-the-rank-duration")]
    public static void TerrifyFearsTheTargetForTheRankDuration()
    {
        using var match = Arena(1);
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        var speed0 = match.Foe.Speed;
        TestAssert.True(match.Cast(0, match.Foe), "Terrify is accepted at rank one");
        match.Advance(0.5); // the cast takes 0.25 s; the fear is applied at the cast frame
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        var fear = Buff(match.Foe, "Fear");
        TestAssert.True(fear is not null, "the target carries the Fear buff the spell's script adds: " + Describe(match.Foe));
        TestAssert.True(fear!.ExpiresAt is > 0.6 and < 0.8, $"the fear runs 1.0 s from the cast frame: {fear.ExpiresAt:0.00} left");
        // Fear.luaobj slows 40 percent; the movement slow below the 220 soft floor is halved (the rule the Zilean record measured), so 180 reads 200.
        var raw = speed0 * 0.6;
        TestAssert.Near(raw < 220.0 ? 220.0 - (220.0 - raw) / 2.0 : raw, match.Foe.Speed, 1e-3, "a feared unit moves 40 percent slower, through the soft floor");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "Fear") is null, "the fear has ended: " + Describe(match.Foe));
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: Drain
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-fiddlesticks-drain-channels-five-seconds-and-steals-life")]
    public static void DrainChannelsFiveSecondsAndStealsLife()
    {
        using var match = Arena(1);
        match.Hero.Health = match.Hero.MaxHealth - 1000.0;
        var selfStart = match.Hero.Health;
        match.Advance(1.2); // the enemy carries Dread from the aura's first one-second execution
        TestAssert.True(match.Cast(1, match.Foe), "Drain is accepted at rank one");
        match.Advance(0.5);
        // The channel's buff: the channel's script adds it without a name, so it is the buff of the running script (DrainChannel); its client name is "Drain".
        TestAssert.True(Buff(match.Foe, "DrainChannel") is not null || Buff(match.Foe, "Drain") is not null,
            "the target carries the drain buff the channel's script adds: " + Describe(match.Foe));
        TestAssert.True(Buff(match.Hero, "Fearmonger_marker") is not null, "FiddleSticks carries the marker of the channel: " + Describe(match.Hero));
        match.Advance(5.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 9 && hits.Count <= 11, $"the channel ticks about every half second for five seconds: {hits.Count} hits");
        var dealt = hits.Sum(hit => hit.Dealt);
        // The victim is inside 800 of FiddleSticks, so the 30 magic damage meets 20 resist: 30 * 100 / 120 = 25 per tick.
        TestAssert.Near(Mitigated(30.0, 30.0 - DreadResist), hits[0].Dealt, 0.01, "the first tick is 30 magic damage against 20 resist");
        var gained = match.Hero.Health - selfStart;
        // The life steal: 60 percent of the damage (DrainPercent at rank one), plus the regeneration of the time.
        TestAssert.True(gained >= 0.55 * dealt, $"the drain heals FiddleSticks for the share of the damage: gained {gained:0.0} of {dealt:0.0}");
        TestAssert.True(gained <= 0.65 * dealt + 20.0, $"the heal is not more than the drain share: gained {gained:0.0} of {dealt:0.0}");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "DrainChannel") is null && Buff(match.Foe, "Drain") is null,
            "the drain buff is gone with the channel: " + Describe(match.Foe));
    }

    // Drain's stop beyond 650 runs the cancel hook: the victim's drain buff must go at that stop, not at its five-second end.
    [Check("scripting.bb-fiddlesticks-drain-breaks-beyond-650-and-frees-the-victim")]
    public static void DrainBreaksBeyond650AndFreesTheVictim()
    {
        using var match = Arena(1);
        match.Advance(1.2); // Dread from the aura's first one-second execution
        TestAssert.True(match.Cast(1, match.Foe), "Drain is accepted at rank one");
        match.Advance(1.0);
        var before = HitsOn(match, "Foe").Count;
        TestAssert.True(Buff(match.Foe, "DrainChannel") is not null, "the drain buff runs while the target is in range: " + Describe(match.Foe));
        // The range is the gap between the two bodies (BBDistanceBetweenObjects: centre distance minus both collision radii), so 700 centre
        // units (a gap of about 570) does not break the channel; the check keeps the channel running for a second.
        match.Foe.Position = new Position2(7700, 7000);
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "Fearmonger_marker") is not null, "a gap under 650 keeps the channel: " + Describe(match.Hero));
        // 900 centre units is a gap of about 770: the channel checks the distance every half second and stops.
        match.Foe.Position = new Position2(7900, 7000);
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "Fearmonger_marker") is null, "the channel has stopped: the marker is gone: " + Describe(match.Hero));
        // The script removes the victim's drain buff only from a target that is not AI: the cancel hook's BBIf (Target CO_IS_TYPE_AI) has an empty body and
        // the BBSpellBuffRemove "Drain" sits in its BBElse. A hero is AI (hero, minion, monster), so the debuff keeps its own five seconds.
        // Batch 269: retail reads it that way. IsObjectAI is the unit type bit 0x400, which heroes carry, and the else
        // branch names "Drain" while the unnamed ChannelingStart add is the buff of the running script, DrainChannel.
        TestAssert.True(Buff(match.Foe, "DrainChannel") is not null, "the script keeps the hero's drain buff after the stop: " + Describe(match.Foe));
        var after = HitsOn(match, "Foe").Count;
        match.Advance(5.0);
        TestAssert.True(Buff(match.Foe, "DrainChannel") is null, "the drain buff runs out with its five seconds: " + Describe(match.Foe));
        TestAssert.True(HitsOn(match, "Foe").Count == after, $"no more drain ticks after the stop: {before} before, {after} at the stop");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Dark Wind
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-fiddlesticks-dark-wind-bounces-four-times-and-silences")]
    public static void DarkWindBouncesFourTimesAndSilences()
    {
        using var match = Arena(1);
        // The enemy's partner stands 400 beyond the target: a bounce has a unit to pick (the random pick takes the other enemy).
        match.FarFoe.Position = new Position2(7700, 7000);
        match.Advance(1.2); // Dread from the aura's first one-second execution
        TestAssert.True(match.Cast(2, match.Foe), "Dark Wind is accepted at rank one");
        match.Advance(1.0);
        // The crow flies 300 units at 1100 a second after the cast frame; its hit adds the silence (DarkWind, 1.2 s) on the target.
        TestAssert.True(Buff(match.Foe, "DarkWind") is not null, "the hit target carries the silence buff: " + Describe(match.Foe));
        match.Advance(2.5);
        var onFoe = HitsOn(match, "Foe");
        var onFar = HitsOn(match, "FarFoe");
        TestAssert.True(onFoe.Count + onFar.Count == 5, $"the cast and four bounces hit five times: foe {onFoe.Count}, far {onFar.Count}");
        // The crow deals 65 magic damage (BaseDamage at rank one) and the 0.45 ability power the spell has (none here), against the resist Dread leaves.
        TestAssert.Near(Mitigated(65.0, 30.0 - DreadResist), onFoe[0].Dealt, 0.01, "the cast's own hit is 65 magic damage against 20 resist");
        TestAssert.True(Buff(match.Hero, "FiddleSticksDarkWindMissile") is not null && Buff(match.Hero, "FiddleSticksDarkWindMissile")!.Stacks == 4,
            "the crow counter ends at its maximum of four: " + Describe(match.Hero));
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: Crowstorm
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-fiddlesticks-crowstorm-channels-then-blinks-and-storms")]
    public static void CrowstormChannelsThenBlinksAndStorms()
    {
        using var match = Arena(1);
        var point = new Position2(7500, 7000);
        match.Foe.Position = new Position2(7700, 7000);
        match.Advance(1.2); // Dread from the aura's first one-second execution
        TestAssert.True(match.Cast(3, at: point), "Crowstorm is accepted at rank one on the ground");
        match.Advance(1.0);
        TestAssert.True(Distance(match.Hero.Position, point) > 100.0, "the caster has not blinked during the channel");
        match.Advance(1.5);
        // The blink lands on the point (the navgrid cell and the client's quantisation move it by a unit or two).
        TestAssert.True(Distance(match.Hero.Position, point) < 5.0, $"the blink ends on the point: {match.Hero.Position.X:0.0},{match.Hero.Position.Z:0.0}");
        match.Advance(5.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 9 && hits.Count <= 11, $"the storm ticks about every half second for five seconds: {hits.Count} hits");
        TestAssert.Near(Mitigated(62.5, 30.0 - DreadResist), hits[0].Dealt, 0.01, "the storm's first tick is 62.5 magic damage against 20 resist");
    }

    /// <summary>
    /// Batch 583 (the checklist's open question 3): Crowstorm's ChannelingSuccessStop takes the cast point (BBGetCastSpellTargetPos)
    /// and teleports him onto it (BBTeleportToPosition) before it starts the storm on him; nothing clamps the point, and the inibin's
    /// CastRange is 800. The wiki: V0.9.25.24 "Now targets a location to teleport to when channeling completes, it works like a built-in blink";
    /// casting at the maximum range for a farther click came only in V6.9. A click 1000 away walks him into range and he blinks onto the click.
    /// </summary>
    [Check("scripting.bb-fiddlesticks-crowstorm-beyond-800-walks-in-and-blinks-onto-the-clicked-point")]
    public static void CrowstormBeyondItsRangeBlinksOntoTheClick()
    {
        using var match = Arena(1);
        var start = match.Hero.Position;
        // Up the middle lane (its diagonal), so the clicked point is open floor.
        var point = new Position2(7707, 7707);
        match.Foe.Position = new Position2(7848, 7848);
        match.Advance(1.2); // Dread from the aura's first one-second execution
        TestAssert.True(match.Cast(3, at: point), "Crowstorm is accepted 1000 away (he walks into range)");
        match.Advance(1.2);
        var during = Distance(match.Hero.Position, point);
        TestAssert.True(during > 700.0 && during < 1000.0 - 50.0, $"he walked toward the point and channels short of it ({during:0.0} away)");
        match.Advance(3.0);
        TestAssert.True(Distance(match.Hero.Position, point) < 5.0,
            $"the blink ends on the clicked point, not 800 from where he stood: {match.Hero.Position.X:0.0},{match.Hero.Position.Z:0.0} (start {start.X:0},{start.Z:0})");
        TestAssert.True(Buff(match.Hero, "Crowstorm") is not null, "the storm runs on him at the landing");
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Raw - 62.5) < 1e-6), "and hits the foe 200 from the point for 62.5");
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
