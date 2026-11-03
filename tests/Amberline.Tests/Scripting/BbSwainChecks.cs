using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (26 October 2026, batch 267): Swain plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed; not a retail bot, so the generic bot template applies). Expected numbers are the literals of the shipped scripts and inibins:
/// Carrion Renewal (<c>SwainDampeningFieldMana</c>: 10 mana on a kill at level one), Decrepify (<c>SwainBeamDamage</c>: 25 magic damage at once and
/// then every second, the slow, the beam's break at a gap of 605), Nevermove (<c>GraspDamage</c> 80 magic after 0.75 s, the root for
/// <c>RootDuration</c> 2 s), Torment (<c>DoTDamage</c> 18.75 for four seconds and the <c>SwainMultiplier</c> 1.08 on his damage to the target),
/// Ravenous Flock (the toggle: the drain takes its cost of 30 mana a second and the cost climbs by 5 each second (ManaCostInc), strikes of 50 magic
/// on enemies within 625, the switch-off once the mana is under the cost, a 0.5-second cooldown on the toggle-on press). The scenario <c>S-swain</c> shows the same facts, and the death and respawn, on a headless match.
/// </summary>
public static class BbSwainChecks
{
    // The closure's shared system buffs: the closure check counts them, the engine runs their shipped scripts as well.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    /// <summary>A match with Swain at rank <paramref name="rank"/>, the enemy 300 away (inside every range that is checked).</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("Swain", foeChampion: "Orianna");
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

    [Check("scripting.bb-swain-module-binds-the-shipped-closure")]
    public static void ModuleBindsTheShippedClosure()
    {
        var module = new BbChampionModule("Swain");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Swain");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        foreach (var name in new[] { "CharScriptSwain", "SwainBeam", "SwainBeamDamage", "SwainShadowGrasp", "SwainShadowGraspRoot", "SwainTorment",
                     "SwainMetamorphism", "SwainDampeningFieldMana", "SwainBeamSelf", "SwainBeamTransition" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        TestAssert.Equal(37, closure.ScriptNames.Count(), "the closure is 37 scripts: the character, four spells, the passive and the scripts they reference");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of them are the shared system buffs");

        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Swain hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-swain-carrion-renewal-restores-mana-on-kill")]
    public static void CarrionRenewalRestoresManaOnKill()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "SwainDampeningFieldMana") is not null, "the passive aura is on Swain from the start: " + Describe(match.Hero));
        match.Hero.Mana = 200.0;
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        match.Advance(0.1);
        // manaRegen is 10 at level one (SrcValue in OnBuffActivate); the regeneration of a tenth of a second is a few hundredths.
        TestAssert.Near(210.0, match.Hero.Mana, 0.5, "the kill restores 10 mana: " + Describe(match.Hero));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q: Decrepify
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-swain-decrepify-ticks-and-slows-then-breaks-beyond-605")]
    public static void DecrepifyTicksAndSlowsThenBreaksBeyond605()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        TestAssert.True(match.Cast(0, match.Foe), "Decrepify is accepted at rank one");
        match.Advance(0.5);
        // The beam's damage is DamagePerHalfSecond 25 at rank one, applied at once and then every second: 25 against 30 resist.
        TestAssert.True(Buff(match.Foe, "SwainBeamDamage") is not null, "the target carries the beam's damage buff: " + Describe(match.Foe));
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the target is slowed by the beam: " + Describe(match.Foe));
        // The channel is Swain's own SwainBeamSelf buff (the beam's TargetExecute adds it on the caster; the beam buff's update ends it at the gap).
        TestAssert.True(Buff(match.Hero, "SwainBeamSelf") is not null, "Swain carries the channel buff of the beam: " + Describe(match.Hero));
        var first = HitsOn(match, "Foe");
        TestAssert.True(first.Count >= 1, "the beam hits the target at once");
        TestAssert.Near(Mitigated(25.0, 30.0), first[0].Dealt, 0.01, "the first tick is 25 magic damage against 30 resist");
        match.Advance(2.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 2 && hits.Count <= 4, $"the beam ticks about once a second while it lasts: {hits.Count} hits");
        // A gap of 605 or more ends the beam at its next quarter-second check (the script's BBDistanceBetweenObjects test): centre 900 is a gap near 770.
        match.Foe.Position = new Position2(7900, 7000);
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "SwainBeamSelf") is null, "the beam ends beyond the 605 gap: " + Describe(match.Hero));
        TestAssert.True(Buff(match.Foe, "SwainBeamDamage") is null, "the beam's damage buff goes with the beam: " + Describe(match.Foe));
        // The break's slow test (BBIfHasBuff Slow, AttackerVar Owner) asks for a slow cast by the beam's holder, the hidden emitter that
        // BBSpawnMinion made; the slow was cast by Swain (AttackerVar Attacker in TargetExecute), so the shipped script leaves the slow to its
        // own three seconds. Open question for the playtest (record), not an engine gap.
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the slow stays until its three seconds: " + Describe(match.Foe));
        var after = HitsOn(match, "Foe").Count;
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "Slow") is null, "the slow runs out with its three seconds: " + Describe(match.Foe));
        TestAssert.True(HitsOn(match, "Foe").Count == after, "no more beam ticks after the break");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: Nevermove
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-swain-nevermove-roots-and-damages-after-the-delay")]
    public static void NevermoveRootsAndDamagesAfterTheDelay()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Nevermove is accepted at rank one on the ground");
        match.Advance(0.5);
        // The area lands 0.75 s after the cast frame: nothing is hit and nothing is rooted before it.
        TestAssert.True(HitsOn(match, "Foe").Count == 0, "no damage before the delay");
        TestAssert.True(Buff(match.Foe, "SwainShadowGraspRoot") is null, "no root before the delay");
        match.Advance(0.6);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count == 1, $"the area hits the unit once: {hits.Count} hits");
        TestAssert.Near(Mitigated(80.0, 30.0), hits[0].Dealt, 0.01, "the grasp deals 80 magic damage against 30 resist");
        TestAssert.True(Buff(match.Foe, "SwainShadowGraspRoot") is not null, "the unit is rooted: " + Describe(match.Foe));
        match.Advance(2.5);
        TestAssert.True(Buff(match.Foe, "SwainShadowGraspRoot") is null, "the root ends after its two seconds: " + Describe(match.Foe));
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Torment
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-swain-torment-burns-four-seconds-and-amplifies-his-damage")]
    public static void TormentBurnsFourSecondsAndAmplifiesHisDamage()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, match.Foe), "Torment is accepted at rank one");
        match.Advance(0.5);
        // The buff is named by its script (SwainTorment); its client name is SwainTormentDoT.
        TestAssert.True(Buff(match.Foe, "SwainTorment") is not null, "the target carries the Torment buff: " + Describe(match.Foe));
        match.Advance(4.5);
        var ticks = HitsOn(match, "Foe");
        // DoTDamage is the total (75 at rank one) over four seconds: 18.75 a tick, four ticks, against 30 resist.
        TestAssert.True(ticks.Count is >= 4 and <= 5, $"Torment ticks for four seconds: {ticks.Count} hits");
        // The script's CharOnPreDealDamage raises every hit Swain makes on the tormented target by SwainMultiplier (1.08 at rank one), the
        // Torment ticks included: the first tick is 18.75 * 1.08 magic damage against 30 resist (15.58).
        TestAssert.Near(Mitigated(18.75 * 1.08, 30.0), ticks[0].Dealt, 0.01, "the first tick is 18.75 magic damage, raised 8 percent, against 30 resist");
        TestAssert.True(Buff(match.Foe, "SwainTorment") is null, "the Torment buff ends with its four seconds");
    }

    [Check("scripting.bb-swain-torment-multiplies-his-damage-to-the-target")]
    public static void TormentMultipliesHisDamageToTheTarget()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, match.Foe), "Torment is accepted at rank one");
        match.Advance(0.5);
        var before = HitsOn(match, "Foe").Count;
        // SwainMultiplier is 1.08 at rank one (CharScriptSwain's CharOnPreDealDamage): Nevermove's 80 becomes 86.4 on the tormented target.
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Nevermove is accepted on the tormented target");
        match.Advance(1.5);
        var grasp = HitsOn(match, "Foe").Skip(before).Where(hit => hit.Raw > 80.0 && hit.Raw < 90.0).ToList();
        TestAssert.True(grasp.Count == 1, $"one grasp hit on the tormented target: {grasp.Count}");
        TestAssert.Near(80.0 * 1.08, grasp[0].Raw, 1e-6, "the grasp's raw damage is raised by 8 percent");
        TestAssert.Near(Mitigated(80.0 * 1.08, 30.0), grasp[0].Dealt, 0.01, "the amplified grasp is mitigated by 30 resist");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: Ravenous Flock
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-swain-ravenous-flock-toggles-strikes-and-switches-off-below-its-mana")]
    public static void RavenousFlockTogglesStrikesAndSwitchesOffBelowItsMana()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        var mana0 = match.Hero.Mana;
        TestAssert.True(match.Cast(3, match.Hero), "Ravenous Flock is accepted at rank one on himself");
        // The toggle-on press leaves a 0.5-second cooldown (AdjustCooldown, the contract's Swain row), started when the cast is accepted.
        match.Advance(0.1);
        TestAssert.Near(0.4, match.Hero.SpellCooldowns[3], 0.15, "the toggle-on press starts a half-second cooldown");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "SwainMetamorphism") is not null, "the toggle buff is on: " + Describe(match.Hero));
        TestAssert.True(match.Hero.Mana < mana0 - 20.0, "the cast costs 25 mana: " + Describe(match.Hero));
        // Batch 314: the pushed SwainRaven layer does not bring its own CharScriptSwainRaven (whose CharOnActivate adds Anivia's RebirthMarker and
        // RebirthReady). The unit's network-ID hook binds a unit's character script once, from the character it is
        // created with (its scripts holder, find or create); ChangeCharacterData and SetCharacterData never rebind it.
        TestAssert.True(Buff(match.Hero, "RebirthMarker") is null && Buff(match.Hero, "RebirthReady") is null,
            "the raven form runs Swain's own character script, not the pushed layer's: " + Describe(match.Hero));
        match.Advance(3.0);
        var strikes = HitsOn(match, "Foe");
        TestAssert.True(strikes.Count >= 2, $"the raven strikes the enemy each second while it is up: {strikes.Count} hits");
        TestAssert.Near(Mitigated(50.0, 30.0), strikes[0].Dealt, 0.01, "a strike is 50 magic damage against 30 resist");
        var off = match.Cast(3, match.Hero);
        match.Advance(0.5);
        TestAssert.True(off, "the toggle-off press is accepted");
        TestAssert.True(Buff(match.Hero, "SwainMetamorphism") is null, "the toggle-off press ends the flock: " + Describe(match.Hero));
        TestAssert.True(match.Hero.SpellCooldowns[3] > 5.0, "the toggle-off press takes the spell's own cooldown");

        // Below its mana the flock ends on its own: the toggle's update stops it when the mana is under its cost (30 at rank one).
        match.Hero.SpellCooldowns[3] = 0.0;
        match.Hero.Mana = 40.0;
        TestAssert.True(match.Cast(3, match.Hero), "the flock is accepted at 40 mana");
        match.Advance(5.0);
        TestAssert.True(Buff(match.Hero, "SwainMetamorphism") is null, "the flock ends below its mana: " + Describe(match.Hero));
    }

    // The drain takes the flock's cost each second, and the cost climbs by ManaCostInc (5 at rank one): from 100 mana the flock drains 30, then 35,
    // then 40; the third update finds the cost above the mana left, so the flock is still up at 2.5 seconds and gone by 4.
    [Check("scripting.bb-swain-ravenous-flock-drain-climbs-by-five-each-second")]
    public static void RavenousFlockDrainClimbsByFiveEachSecond()
    {
        using var match = Arena(1);
        match.Advance(0.5);
        match.Hero.Mana = 100.0;
        TestAssert.True(match.Cast(3, match.Hero), "Ravenous Flock is accepted at rank one on himself");
        match.Advance(2.5);
        TestAssert.True(Buff(match.Hero, "SwainMetamorphism") is not null, "the flock is still up after the first two drains: " + Describe(match.Hero));
        match.Advance(1.5);
        TestAssert.True(Buff(match.Hero, "SwainMetamorphism") is null, "the third drain (40) is above the mana left, so the flock ends: " + Describe(match.Hero));
    }

}
