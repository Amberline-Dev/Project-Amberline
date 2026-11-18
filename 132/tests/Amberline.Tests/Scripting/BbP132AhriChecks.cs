using Amberline.Core;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): the 1.0.0.132 Ahri rows (131.03). The numbers are the shipped scripts' (the Ahri scripts, read by the
/// engine's loader); the Charm check also casts Charm (E) through the match.
/// </summary>
public static class BbP132AhriChecks
{
    // 131.03: Charm (E) stops the charmed foe from attacking, and the foe walks towards Ahri.
    [Check("scripting.p132-ahri-charm-stops-the-foe-attacking-and-walks-it-to-ahri")]
    public static void CharmStopsAttacksAndWalksTheFoe()
    {
        var seduce = Script("AhriSeduce");
        TestAssert.True(Calls(seduce, "BBSetStatus").Any(block => Flag(block, "SrcValue") == false && Named(block, "Status") == "SetCanAttack"),
            "the charm takes away attacking (shipped AhriSeduce OnBuffActivate)");
        TestAssert.True(Calls(seduce, "BBIssueOrder").Any(block => Named(block, "Order") == "AI_MOVETO"),
            "the charmed foe is ordered to walk to Ahri (shipped AhriSeduce)");
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E is cast at the foe, which is in front of Ahri (the charm needs it in front)");
        // The charm's length is the shipped TauntLength variable, so the first step with the buff is read, not a fixed time.
        double? on = null;
        for (var waited = 0.0; on is null && waited < 1.5; waited += Step)
        {
            match.Advance(Step);
            if (Buff(match.Foe, "AhriSeduce") is not null) on = match.Now;
        }
        TestAssert.True(on is not null, "the foe carries Charm within 1.5 seconds; buffs: " + Names(match.Foe));
        TestAssert.False(CharacterState.Of(match.Foe).CanAttack, "a charmed foe cannot attack");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // 131.03: Spirit Rush (R) has three charges: the first cast and two stacks (MaxStack 2); the cooldown after the last charge is 90/80/70.
    [Check("scripting.p132-ahri-spirit-rush-has-three-charges-and-cooldown-ninety-eighty-seventy")]
    public static void SpiritRushHasThreeCharges()
    {
        var tumble = Script("AhriTumble");
        TestAssert.True(Calls(tumble, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "AhriTumble" && Is(Number(block, "MaxStack"), 2)),
            "the charges are the first cast and two stacks (shipped AhriTumble)");
        TestAssert.True(Sets(tumble, "NewCd").Any(block => LevelsAre(block, "SrcValueByLevel", 90, 80, 70)),
            "the cooldown after the last charge is 90/80/70 (shipped AhriTumble)");
    }

    // 131.03: the orb's return (AhriOrbDamageSilence) deals TRUE damage, 40/65/90/115/140 by rank (the outgoing orb hit is magic).
    [Check("scripting.p132-ahri-orb-return-deals-true-damage-by-rank")]
    public static void OrbReturnDealsTrueDamage()
    {
        var silence = Script("AhriOrbDamageSilence");
        TestAssert.True(Calls(silence, "BBApplyDamage").Any(block => Named(block, "DamageType") == "TRUE_DAMAGE" && LevelsAre(block, "DamageByLevel", 40, 65, 90, 115, 140)),
            "the return deals true damage 40/65/90/115/140 (shipped AhriOrbDamageSilence)");
    }

    // 131.03: Essence Theft gains a charge per spell hit; the ninth charge makes the next spell cast Essence Theft's buff (AhriSoulCrusher).
    [Check("scripting.p132-ahri-essence-theft-counts-to-nine-charges-then-the-next-spell-buff")]
    public static void EssenceTheftNineCharges()
    {
        var counter = Script("AhriSoulCrusherCounter");
        TestAssert.True(Calls(counter, "BBIf").Any(block => Named(block, "Src1Var") == "Count" && Is(Number(block, "Value2"), 9)),
            "the counter fires at nine charges (shipped AhriSoulCrusherCounter)");
        TestAssert.True(Calls(counter, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "AhriSoulCrusher"),
            "the ninth charge adds Essence Theft's next-spell buff (shipped AhriSoulCrusherCounter)");
    }

    // Batch 891 (player report, 18 November 2026: "Ahri Q never gives passive charges after the first Essence Theft heal"). A Q cast with
    // Essence Theft ready sets CharVars.OrbofDeceptionIsActive = 1 (CharScriptAhri), and only AhriOrbReturn's Target == Attacker branch, the
    // returning orb reaching Ahri, sets it back to 0. While it is 1 every orb hit takes the heal branch and adds no charge (AhriOrbDamage).
    [Check("scripting.p132-ahri-orb-return-reaching-ahri-ends-essence-theft-so-later-q-casts-give-charges")]
    public static void OrbReturnEndsEssenceTheft()
    {
        TestAssert.True(Calls(Script("AhriOrbReturn"), "BBSetVarInTable").Any(block =>
                Named(block, "DestVar") == "OrbofDeceptionIsActive" && Named(block, "DestVarTable") == "CharVars"),
            "the return orb's arrival at Ahri resets the flag (shipped AhriOrbReturn TargetExecute)");
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Learn(1);
        var vars = match.Module.CharVars(match.Hero);
        int Charges() => Buff(match.Hero, "AhriSoulCrusherCounter")?.Stacks ?? 0;
        bool Ready() => Buff(match.Hero, "AhriSoulCrusher") is not null;
        void Q(string what)
        {
            match.Refill();
            TestAssert.True(match.Cast(0, match.Foe), what + ": Q is cast at the foe");
            // Out to 880 and back to Ahri in under two seconds (AhriOrbMissile 2500 slowing to 400, AhriOrbReturn 60 speeding up to 2600), and
            // AhriSoulCrusher5 (the 2 s window that caps the charges at three) ends before the next cast.
            match.Advance(4.0);
        }
        void Charge(string cycle)
        {
            // One foe: the outgoing hit and the return hit give a charge each, so five casts reach nine.
            for (var cast = 1; !Ready(); cast++)
            {
                TestAssert.True(cast <= 6, $"{cycle}: Essence Theft is ready within six casts; buffs: {Names(match.Hero)}");
                var before = Charges();
                Q($"{cycle} cast {cast}");
                TestAssert.True(Ready() || Charges() > before,
                    $"{cycle} cast {cast}: the hits give charges ({before} -> {Charges()}); flag {vars["OrbofDeceptionIsActive"]}");
            }
        }

        Charge("first cycle");
        Q("charged cast");
        TestAssert.False(Ready(), "the charged Q's hit spent Essence Theft (the heal branch)");
        TestAssert.Equal(0.0, vars["OrbofDeceptionIsActive"] as double? ?? -1.0, "the orb came back to Ahri and reset the flag");
        Charge("second cycle");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // Batch 891 (player report: "Ahri R has only 2 dashes instead of 3"). AhriTumble's first cast adds itself BUFF_REPLACE_EXISTING with
    // NumberOfStacks 2 (MaxStack 2) and a 0.75 s recast; each later cast removes one stack, and the last stack's OnBuffDeactivate (count 0) starts
    // the 90/80/70 cooldown. Cooldowns stay on here (no Refill between the dashes).
    [Check("scripting.p132-ahri-spirit-rush-dashes-three-times-with-cooldowns-on")]
    public static void SpiritRushDashesThreeTimes()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        for (var dash = 1; dash <= 3; dash++)
        {
            // Along the middle lane, back and forth.
            var side = dash % 2 == 1 ? 1.0 : -1.0;
            var from = hero.Position;
            TestAssert.True(match.Cast(3, at: from + new Position2(283.0 * side, 283.0 * side)), $"dash {dash} is cast; R cooldown {hero.SpellCooldowns[3]}");
            match.Advance(0.5);
            var moved = Math.Sqrt((hero.Position.X - from.X) * (hero.Position.X - from.X) + (hero.Position.Z - from.Z) * (hero.Position.Z - from.Z));
            TestAssert.True(moved > 200.0, $"dash {dash} moved Ahri ({moved:0})");
            TestAssert.Equal(3 - dash, Buff(hero, "AhriTumble")?.Stacks ?? 0, $"dash {dash}: the charges left; buffs: {Names(hero)}");
            if (dash < 3)
                TestAssert.True(hero.SpellCooldowns[3] <= 0.75, $"dash {dash}: the recast waits at most 0.75 s ({hero.SpellCooldowns[3]})");
            else
                TestAssert.True(hero.SpellCooldowns[3] > 60.0, $"the third dash starts the 90 s cooldown ({hero.SpellCooldowns[3]})");
            match.Advance(0.5);
        }
        TestAssert.False(match.Cast(3, at: hero.Position + new Position2(283.0, 283.0)), "a fourth dash is refused");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // Batch 891: one dash, then AhriTumble's 10 s run out with both charges left. The buff ends whole, and its OnBuffDeactivate must read count 0
    // there, or R would end free and with no cooldown.
    [Check("scripting.p132-ahri-spirit-rush-unused-charges-expire-into-the-cooldown")]
    public static void SpiritRushChargesExpireIntoTheCooldown()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        TestAssert.True(match.Cast(3, at: hero.Position + new Position2(283.0, 283.0)), "one dash is cast");
        match.Advance(0.5);
        TestAssert.Equal(2, Buff(hero, "AhriTumble")?.Stacks ?? 0, "two charges are left; buffs: " + Names(hero));
        match.Advance(10.0);
        TestAssert.True(Buff(hero, "AhriTumble") is null, "the unused charges expire after 10 s; buffs: " + Names(hero));
        TestAssert.True(hero.SpellCooldowns[3] > 60.0, $"their expiry starts the 90 s cooldown ({hero.SpellCooldowns[3]})");
        TestAssert.False(match.Cast(3, at: hero.Position + new Position2(-283.0, -283.0)), "R is refused after the expiry");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // Batch 891: Fox-Fire's counter. AhriFoxFire's SelfExecute adds AhriFoxFireMissile BUFF_REPLACE_EXISTING with NumberOfStacks 3 (MaxStack 3); each
    // fox-fire that launches removes one stack, and the counter's OnBuffDeactivate removes AhriFoxFire (W's seal, its cooldown at the end) at 0.
    [Check("scripting.p132-ahri-fox-fire-counter-holds-three-fox-fires")]
    public static void FoxFireCounterHoldsThree()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        TestAssert.True(match.Cast(1), "W is cast");
        var waited = 0.0;
        for (; Buff(hero, "AhriFoxFireMissile") is null && waited < 1.0; waited += Step) match.Advance(Step);
        TestAssert.Equal(3, Buff(hero, "AhriFoxFireMissile")?.Stacks ?? 0, "the cast holds three fox-fires; buffs: " + Names(hero));
        for (; Buff(hero, "AhriFoxFireMissile") is not null && waited < 6.0; waited += Step)
        {
            TestAssert.True(Buff(hero, "AhriFoxFire") is not null, $"W stays sealed while fox-fires are left ({match.Now:0.00} s)");
            match.Advance(Step);
        }
        TestAssert.True(Buff(hero, "AhriFoxFireMissile") is null && Buff(hero, "AhriFoxFire") is null, "the last fox-fire ends the counter and W");
        TestAssert.True(hero.SpellCooldowns[1] > 0.0, "W's cooldown runs from the counter's end (AhriFoxFire's OnBuffDeactivate)");
        // The counter ends when the last fox-fire launches; the launched fox-fires (AhriFoxFireMissileTwo) are still on their way.
        match.Advance(1.5);
        TestAssert.Equal(3, HitsOn(match, "Foe").Count, "three fox-fires strike the foe");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
