using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 7 (batch 332, 26 October 2026): Jarvan IV plays from his shipped scripts. The expected numbers are the literals of the shipped
/// scripts and inibins (read through <see cref="BbM3"/>), not the engine's output; the scenario <c>S-jarvaniv</c> shows the same kit in a headless
/// match. The measured check is Dragon Strike: its box hits the foe in front of him and lowers her armour.
/// </summary>
public static class BbJarvanIVChecks
{
    private static Match Arena(string foe = "Orianna")
    {
        var match = new Match("JarvanIV", foeChampion: foe);
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    /// <summary>The constant operand of the first <c>BBMath</c> that writes <paramref name="dest"/> with <paramref name="op"/>.</summary>
    private static double MathConstant(BbScript script, string dest, string op, string constantParam)
    {
        var block = script.Walk().Select(item => item.Block)
            .FirstOrDefault(candidate => candidate.Function == "BBMath" && candidate.Text("DestVar") == dest &&
                candidate.Param("MathOp") is BbValue.Reference reference && reference.Name == op);
        TestAssert.True(block is not null, $"{script.Name} has the {op} into {dest}");
        return block!.Number(constantParam)!.Value;
    }

    /// <summary>The <c>SrcValueByLevel</c> table of the first <c>BBSetVarInTable</c> that writes <paramref name="variable"/> in a hook of the character script.</summary>
    private static double[] CharTable(string hook, string variable)
    {
        var block = Blocks(CharScript("JarvanIV"), hook)
            .FirstOrDefault(candidate => candidate.Function == "BBSetVarInTable" && candidate.Text("DestVar") == variable);
        TestAssert.True(block is not null, $"the character script writes {variable} in {hook}");
        return Table(block!, "SrcValueByLevel");
    }

    [Check("scripting.bb-jarvaniv-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("JarvanIV");
        foreach (var name in new[] { "CharScriptJarvanIV", "JarvanIVDragonStrike", "JarvanIVGoldenAegis", "JarvanIVDemacianStandard", "JarvanIVCataclysm",
                     "JarvanIVMartialCadenceAttack", "JarvanIVCataclysmAttack", "JarvanIVDemacianStandardDelay", "JarvanIVCataclysmVisibility", "CrystallizePush" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("JarvanIV");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(33, closure.ScriptNames.Count(), "the closure is 33 scripts (the four spells, the passive and their buffs)");

        var hero = new Entity { ChampionName = "JarvanIV" };
        TestAssert.Equal(["JarvanIVDragonStrike", "JarvanIVGoldenAegis", "JarvanIVDemacianStandard", "JarvanIVCataclysm"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from JarvanIV.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "Cataclysm is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("JarvanIV")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 4800, RetailBot: false }, "playable as a jungler for 4800 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "JarvanIV"), "listed once");
    }

    [Check("scripting.bb-jarvaniv-martial-cadence-is-eight-percent-of-current-health-between-two-and-four-hundred")]
    public static void MartialCadenceBonusByCurrentHealth()
    {
        var attack = Script("JarvanIVMartialCadenceAttack");
        TestAssert.Equal(0.08, MathConstant(attack, "HealthDamage", "MO_MULTIPLY", "Src2Value"), "eight percent of the target's current health");
        TestAssert.Equal(400.0, MathConstant(attack, "HealthDamage", "MO_MIN", "Src1Value"), "capped at 400");
        TestAssert.Equal(2.0, MathConstant(attack, "HealthDamage", "MO_MAX", "Src1Value"), "at least 2");
        var check = Blocks(attack, "TargetExecute").First(block => block.Function == "BBSpellBuffAdd" && block.Text("BuffName") == "JarvanIVMartialCadenceCheck");
        TestAssert.Equal([6.0, 6.0, 6.0, 6.0, 6.0], Table(check, "DurationByLevel"), "the check buff on the target lasts six seconds at every rank");
    }

    [Check("scripting.bb-jarvaniv-dragon-strike-damage-and-armour-debuff-by-rank")]
    public static void DragonStrikeNumbers()
    {
        var script = "JarvanIVDragonStrike";
        TestAssert.Equal([70.0, 115.0, 160.0, 205.0, 250.0], ByLevel(script, "SelfExecute", "BonusDamage"), "bonus damage by rank");
        TestAssert.Equal(1.2, MathConstant(Script(script), "PhysPreMod", "MO_MULTIPLY", "Src2Value"), "1.2 times the bonus attack damage");
        TestAssert.Equal([-0.1, -0.14, -0.18, -0.22, -0.26], ByLevel(script, "SelfExecute", "ArmorDebuff"), "the armour debuff by rank");
    }

    [Check("scripting.bb-jarvaniv-golden-aegis-shield-and-slow-by-rank")]
    public static void GoldenAegisNumbers()
    {
        var script = "JarvanIVGoldenAegis";
        TestAssert.Equal([50.0, 90.0, 130.0, 170.0, 210.0], ByLevel(script, "SelfExecute", "ShieldAmount"), "the shield by rank");
        TestAssert.Equal([20.0, 25.0, 30.0, 35.0, 40.0], ByLevel(script, "SelfExecute", "ShieldBonus"), "twenty to forty per enemy champion within 500");
        TestAssert.Equal([-0.15, -0.2, -0.25, -0.3, -0.35], ByLevel(script, "SelfExecute", "MoveSpeedMod"), "the slow of the foes near him by rank");
    }

    [Check("scripting.bb-jarvaniv-demacian-standard-blast-aura-and-level-passive")]
    public static void DemacianStandardNumbers()
    {
        var standard = "JarvanIVDemacianStandard";
        var delay = "JarvanIVDemacianStandardDelay";
        TestAssert.Equal([60.0, 105.0, 150.0, 195.0, 240.0], ByLevel(standard, "SelfExecute", "BaseDamage"), "the landing damage by rank");
        TestAssert.Equal(0.8, MathConstant(Script(standard), "AbilityPowerPostMod", "MO_MULTIPLY", "Src1Value"), "0.8 times the ability power");
        TestAssert.Equal([10.0, 13.0, 16.0, 19.0, 22.0], ByLevel(delay, "OnBuffDeactivate", "ArmorMod"), "the aura's armour by rank");
        TestAssert.Equal([0.1, 0.13, 0.16, 0.19, 0.22], ByLevel(delay, "OnBuffDeactivate", "AttackSpeedMod"), "the aura's attack speed by rank");
        TestAssert.Equal([10.0, 3.0, 3.0, 3.0, 3.0], CharTable("CharOnLevelUpSpell", "ArmorBoost"), "the passive armour gained with each rank of the standard");
        TestAssert.Equal([0.1, 0.03, 0.03, 0.03, 0.03], CharTable("CharOnLevelUpSpell", "AttackSpeedBoost"), "the passive attack speed gained with each rank");
    }

    [Check("scripting.bb-jarvaniv-cataclysm-cooldown-and-cost-match-the-inibin")]
    public static void CataclysmCooldownAgreesWithTheInibin()
    {
        var spell = GameData.Default.Spell("JarvanIVCataclysm", 3);
        TestAssert.Equal([120.0, 105.0, 90.0], spell.Cooldowns, "the inibin's cooldown is the script's own leap cooldown (NewCd)");
        TestAssert.Equal([100.0, 125.0, 150.0], spell.ManaCosts, "the cost is the inibin's");
        TestAssert.Equal([120.0, 105.0, 90.0, 0.0, 0.0], ByLevel("JarvanIVCataclysm", "OnBuffActivate", "NewCd"), "the leap's cooldown is set when the leap ends");
        TestAssert.Equal([-100.0, -125.0, -150.0], ByLevel("JarvanIVCataclysm", "OnBuffActivate", "ManaReduction"), "the script's negative cost while the leap is armed");
        TestAssert.Equal([200.0, 325.0, 450.0], ByLevel("JarvanIVCataclysmAttack", "TargetExecute", "BaseDamage"), "the leap's hit by rank");
    }

    [Check("scripting.bb-jarvaniv-dragon-strike-hits-the-foe-in-front-and-lowers-her-armour")]
    public static void DragonStrikeMeasured()
    {
        var match = Arena();
        var armour = match.Foe.Armor;
        var cast = match.Cast(0, at: match.Foe.Position);
        match.Advance(1.0);
        TestAssert.True(cast, "Dragon Strike is accepted at rank one");
        // Batch 332, session 2: the damage is read from the committed hits, not from the foe's health. The harness raises her health to 5000 above
        // her real maximum; the debuff's stat refresh rebuilds the maximum (464 at level one) and clamps the health to it, which read as a 4536 hit.
        var hits = HitsOn(match, "Foe");
        var diagnostic = string.Join(" ", hits.Select(hit => $"[{hit.At:0.000} {hit.Kind} raw={R(hit.Raw)} dealt={R(hit.Dealt)}]")) +
            $"; ally {match.Ally.Health:0.0}; far {match.FarFoe.Health:0.0}";
        // 70 physical at rank one through the foe's armour (the bonus attack damage is zero in the arena); the hit reads the armour before the debuff.
        TestAssert.Equal(1, hits.Count, "one hit on the foe: " + diagnostic);
        NearF32(70.0, hits[0].Raw, "rank-one damage before mitigation: " + diagnostic);
        TestAssert.Near(Mitigated(70.0, armour), hits[0].Dealt, 1e-3, "rank-one damage through the foe's armour: " + diagnostic);
        TestAssert.Near(armour * 0.9, match.Foe.Armor, 0.5, "the armour debuff is 10 percent at rank one");
    }

    [Check("scripting.bb-jarvaniv-martial-cadence-first-swing-adds-eight-percent-of-current-health-as-magic")]
    public static void MartialCadenceMeasured()
    {
        var match = Arena();
        match.Runtime.HeroStats.Refresh(match.Foe, push: false); // her real maximum and resistances: the harness's 5000 sits above them
        match.Foe.Health = match.Foe.MaxHealth;
        match.Foe.Position = new Position2(7150, 7000);
        var health = match.Foe.Health;
        var (armour, resist) = (match.Foe.Armor, match.Foe.SpellBlock);
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the first swing");
        match.Advance(1.2);
        var first = HitsOn(match, "Foe");
        var diagnostic = string.Join(" ", first.Select(hit => $"[{hit.At:0.000} {hit.Kind} raw={R(hit.Raw)} dealt={R(hit.Dealt)}]")) +
            $"; health {R(health)} armour {R(armour)} mr {R(resist)} ad {R(match.Hero.AttackDamage)}; failures " +
            string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));
        var magic = Math.Clamp(0.08 * health, 2.0, 400.0);
        TestAssert.Equal(2, first.Count, "the first swing is two hits, the magic part and the attack: " + diagnostic);
        TestAssert.True(first.Any(hit => Math.Abs(hit.Raw - magic) < 1e-3 && Math.Abs(hit.Dealt - Mitigated(magic, resist)) < 1e-3),
            $"8 percent of her current health ({R(magic)}) as magic damage through her resistance: " + diagnostic);
        TestAssert.True(first.Any(hit => Math.Abs(hit.Raw - match.Hero.AttackDamage) < 1e-3), "and his base attack damage as the swing: " + diagnostic);
        TestAssert.True(Buff(match.Foe, "JarvanIVMartialCadenceCheck") is not null, "the six-second check is on her");

        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the second swing");
        match.Advance(1.2);
        var second = HitsOn(match, "Foe").Skip(first.Count).ToList();
        TestAssert.Equal(1, second.Count, "the second swing inside six seconds is the plain attack: " +
            string.Join(" ", second.Select(hit => $"[{hit.Kind} raw={R(hit.Raw)}]")));
    }

    [Check("scripting.bb-jarvaniv-cataclysm-leaps-to-a-foe-at-full-range")]
    public static void CataclysmLeapsAtFullRange()
    {
        var match = Arena();
        var range = GameData.Default.Spell("JarvanIVCataclysm", 3).CastRange;
        match.Foe.Position = new Position2(7000 + range, 7000);
        var validation = match.CanCast(3, match.Foe);
        var cast = match.Cast(3, match.Foe);
        var diagnostic = $"range {R(range)} validation {validation}; failures " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));
        TestAssert.True(cast, "Cataclysm on a foe at its cast range is accepted: " + diagnostic);
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "JarvanIVCataclysm") is not null, "the leap's buff is on him: " + Names(match.Hero));
        TestAssert.True(match.Hero.Position.X > 7000 + range - 200, $"he leapt to her: {R(match.Hero.Position.X)},{R(match.Hero.Position.Z)}");
        TestAssert.True(Buff(match.Hero, "JarvanIVCataclysmAttack") is not null, "the ring of walls stands: " + Names(match.Hero));
    }

    [Check("scripting.bb-jarvaniv-demacian-standard-activation-raises-no-script-exception")]
    public static void DemacianStandardActivationRunsCleanly()
    {
        // The standard's OnBuffActivate cuts its sight with IncPercentBubbleRadiusMod (-0.5); the rest of that hook (the statuses, the untargetable team) follows it.
        var match = Arena();
        match.Cast(2, at: match.Foe.Position);
        match.Advance(1.0);
        var failures = string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));
        TestAssert.True(failures.Length == 0, "the standard's activation and landing run without a script exception: " + failures);
    }

    [Check("scripting.bb-jarvaniv-dragon-strike-through-the-standard-dashes-him-to-it")]
    public static void DragonStrikeThroughStandardDashesHimToIt()
    {
        // The Demacian Standard is planted about a quarter second after the press (the Delay script's Beacon). Dragon Strike connects with
        // it through the pull rectangle (AffectFriends admits his own team's standard, which carries his buff) and the pull moves him to it.
        var match = Arena();
        TestAssert.True(match.Cast(2, at: new Position2(7400, 7000)), "the standard is cast four hundred units ahead");
        match.Advance(1.0);
        match.Refill();
        var standard = match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Name == "JarvanIVStandard");
        TestAssert.True(standard is not null, "the standard stands after its delay");
        var stand = standard!;
        TestAssert.True(match.Cast(0, at: stand.Position), "Dragon Strike through the standard is accepted");
        match.Advance(1.5);
        var gap = Math.Sqrt(Math.Pow(match.Hero.Position.X - stand.Position.X, 2) + Math.Pow(match.Hero.Position.Z - stand.Position.Z, 2));
        TestAssert.True(gap < 25.0, $"he dashed to the standard: {R(gap)} units from it, at {R(match.Hero.Position.X)},{R(match.Hero.Position.Z)}");
    }

    [Check("scripting.bb-jarvaniv-dragon-strike-through-a-standard-800-ahead-reaches-it")]
    public static void DragonStrikeThroughStandardEightHundredAheadReachesIt()
    {
        // Owner finding 31 (handtest-20261026): a standard 800 ahead pulled him only 500 units, because BBMove capped the dash at the pull's
        // IdealDistance (500). The pull is a dash to the standard itself (TargetVar Attacker, a unit), which ends on it, as the wiki's Dragon
        // Strike text says ("dashes to its location"). Before the fix he stopped about 300 short (batch 367 record).
        var match = Arena();
        TestAssert.True(match.Cast(2, at: new Position2(7800, 7000)), "the standard is cast eight hundred units ahead");
        match.Advance(1.0);
        match.Refill();
        var standard = match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Name == "JarvanIVStandard");
        TestAssert.True(standard is not null, "the standard stands after its delay");
        var stand = standard!;
        TestAssert.True(match.Cast(0, at: stand.Position), "Dragon Strike through the standard is accepted");
        match.Advance(1.5);
        var gap = Math.Sqrt(Math.Pow(match.Hero.Position.X - stand.Position.X, 2) + Math.Pow(match.Hero.Position.Z - stand.Position.Z, 2));
        TestAssert.True(gap < 25.0, $"he dashed to the standard eight hundred units ahead: {R(gap)} units from it, at {R(match.Hero.Position.X)},{R(match.Hero.Position.Z)}");
    }

    [Check("scripting.bb-jarvaniv-dragon-strike-without-a-standard-does-not-move-him")]
    public static void DragonStrikeWithoutStandardStaysPut()
    {
        var match = Arena();
        match.Advance(0.5); // the first ticks settle the arena placement (he starts at 7000,7000 and moves about a unit)
        var start = match.Hero.Position;
        TestAssert.True(match.Cast(0, at: new Position2(7400, 7000)), "Dragon Strike at four hundred units is accepted");
        match.Advance(1.5);
        var moved = Math.Sqrt(Math.Pow(match.Hero.Position.X - start.X, 2) + Math.Pow(match.Hero.Position.Z - start.Z, 2));
        TestAssert.True(moved < 5.0, $"no standard, no pull: he moved {R(moved)} units from {R(start.X)},{R(start.Z)} to {R(match.Hero.Position.X)},{R(match.Hero.Position.Z)}");
    }
}
