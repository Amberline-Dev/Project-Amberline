using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 1 (19 October 2026): Udyr plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). Expected numbers
/// are the literals of the shipped scripts and spell inibins (rank one unless a check learns more); the scenarios <c>S-udyr</c> and <c>S-udyr-reconnect</c> show
/// the same facts, and more, on a headless match. Written 19 October 2026; the engine pieces Udyr needed have checks of their own in
/// <c>BbSlotChecks</c> (the character-data stack), <c>DataChecks</c> (the rank gate) and here (resuming a script after a block, the live prefix of an attack
/// script, the cone child).
/// </summary>
public static class BbUdyrChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Udyr adjacent to the enemy hero (his basic attack range is 125), the stances learnt at <paramref name="rank"/>.</summary>
    private static Match Melee(int rank = 1)
    {
        var match = new Match("Udyr");
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7080, 7000);
        match.Foe.Armor = 40.0;
        match.Foe.SpellBlock = 30.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        return match;
    }

    private static void Swing(Match match, Entity target, double seconds = 1.0)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target), "swing");
        match.Advance(seconds);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-udyr-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Udyr");
        TestAssert.Equal(28, module.ScriptNames.Count, "the Udyr closure is 28 scripts (audit bb-coverage)");
        foreach (var name in new[] { "CharScriptUdyr", "UdyrTigerStance", "UdyrTurtleStance", "UdyrBearStance", "UdyrPhoenixStance", "UdyrPhoenixBreath",
                     "UdyrTigerAttack", "UdyrTurtleAttack", "UdyrBearAttack", "UdyrPhoenixAttack", "UdyrMonkeyAgility", "UdyrMonkeyAgilityBuff",
                     "UdyrTigerPunch", "UdyrTigerShred", "UdyrTigerPunchBleed", "UdyrTurtleActivation", "UdyrBearActivation", "UdyrPhoenixActivation",
                     "UdyrBearStunCheck", "GlobalDrainMana" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Udyr");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)), "eight shared system buffs");

        var hero = new Entity { ChampionName = "Udyr" };
        TestAssert.Equal(["UdyrTigerStance", "UdyrTurtleStance", "UdyrBearStance", "UdyrPhoenixStance"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Udyr.inibin");
        // Udyr's R is a normal spell: five ranks, the same level gates as Q, W and E (Data*MaxLevels 5/5/5/5, Data*SpellsUpLevels "1 3 5 7 9 99").
        TestAssert.Equal([5, 5, 5, 5], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "four spells of five ranks");
        foreach (var slot in new[] { 0, 3 })
            TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, slot, rank)).ToArray(), $"slot {slot} is learnt at 1, 3, 5, 7, 9");

        var info = ChampionCatalog.ByName("Udyr")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 3150, Id: 77, RetailBot: false }, "playable as a jungler for 3150 IP, not a retail bot");
    }

    [Check("scripting.bb-udyr-r-can-be-learnt-at-level-one")]
    public static void TheUltimateSlotFollowsTheCharactersOwnRankGate()
    {
        using var match = new Match("Udyr");
        var hero = match.Hero;
        TestAssert.Equal(1, hero.Level, "level one");
        hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.CanRankUp(hero, 3).Allowed, "R rank 1 at level 1 (the table of the stock client says level 6 for every other champion)");
        TestAssert.True(match.Runtime.Spells.RankUp(hero, 3), "learnt");
        hero.SkillPoints = 1;
        var second = match.Runtime.Spells.CanRankUp(hero, 3);
        TestAssert.False(second.Allowed, "rank 2 needs level 3");
        TestAssert.Equal(3, second.RequiredLevel, "the gate the refusal names");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Stances: the cost, the shared cooldown, the model layers
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-udyr-stances-share-a-cooldown-and-stack-their-models")]
    public static void EachStanceReplacesTheOtherAndPushesItsModel()
    {
        using var match = Melee();
        var hero = match.Hero;
        var layers = new List<string>();
        var stances = new List<string>();
        foreach (var slot in new[] { 0, 1, 2, 3 })
        {
            match.Refill();
            var mana = hero.Mana;
            TestAssert.True(match.Cast(slot, hero), $"slot {slot} casts");
            var spent = mana - hero.Mana;
            match.Advance(0.3);
            if (slot == 0)
            {
                TestAssert.Near(55.0, spent, 0.05, "55 mana at rank 1");
                TestAssert.Near(6.0, hero.SpellCooldowns[0], 0.4, "6 s of its own");
                foreach (var other in new[] { 1, 2, 3 })
                    TestAssert.Near(1.5, hero.SpellCooldowns[other], 0.4, $"slot {other} is held for 1.5 s (the stance cooldown the script writes)");
            }
            layers.Add(hero.ScriptCharacterData?.SkinName ?? "-");
            stances.Add(string.Join("+", hero.Buffs.Where(buff => buff.Name.EndsWith("Stance", StringComparison.Ordinal)).Select(buff => buff.Name)));
        }
        TestAssert.Equal(["UdyrTiger", "UdyrTurtle", "Udyr", "UdyrPhoenix"], layers.ToArray(), "each stance pushes its model");
        TestAssert.Equal(["UdyrTigerStance", "UdyrTurtleStance", "UdyrBearStance", "UdyrPhoenixStance"], stances.ToArray(), "and drops the stance before it");
        TestAssert.Equal(["UdyrTiger", "UdyrTurtle", "Udyr"], hero.BuriedCharacterLayers.Select(layer => layer.SkinName).ToArray(), "the models before stay under it");
        TestAssert.Equal("UdyrPhoenixAttack", hero.ScriptAttackOverride?.SpellName, "the attack is the Phoenix attack");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-udyr-monkeys-agility-stacks-three-times-for-five-seconds")]
    public static void EveryStanceCastAddsAStack()
    {
        using var match = Melee();
        var hero = match.Hero;
        var dodge = hero.Dodge;
        var stacks = new List<int>();
        foreach (var slot in new[] { 0, 1, 2, 3 })
        {
            match.Refill();
            match.Cast(slot, hero);
            match.Advance(0.3);
            stacks.Add(Of(hero, "UdyrMonkeyAgilityBuff")?.Stacks ?? 0);
        }
        TestAssert.Equal([1, 2, 3, 3], stacks.ToArray(), "up to three stacks");
        TestAssert.Near(0.09, hero.Dodge - dodge, 1e-6, "3 percent dodge a stack");
        TestAssert.Near(0.3, Of(hero, "UdyrMonkeyAgilityBuff")!.Payload.AttackSpeedMod, 1e-6, "ten percent attack speed a stack (read as the sum of the three)");
        match.Advance(5.1);
        TestAssert.True(Of(hero, "UdyrMonkeyAgilityBuff") is null, "they end five seconds after the last cast");
        TestAssert.True(Of(hero, "UdyrMonkeyAgility") is not null, "the passive stays");
        // A swing is a cast of an attack spell that does not trigger the passive.
        Swing(match, match.Foe);
        TestAssert.True(Of(hero, "UdyrMonkeyAgilityBuff") is null, "a basic attack adds none");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Tiger, Turtle, Bear, Phoenix
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-udyr-tiger-stance-rends-with-the-first-attack")]
    public static void TigerBleedsForTwoSecondsAfterTheFirstHit()
    {
        using var match = Melee();
        var hero = match.Hero;
        match.Cast(0, hero);
        match.Advance(0.3);
        NearF32(0.2, Of(hero, "UdyrTigerStance")!.Payload.AttackSpeedMod, "the passive: 20 percent attack speed at rank 1");
        NearF32(0.15, Of(hero, "UdyrTigerPunch")!.Payload.AttackSpeedMod, "the activation: 15 percent for five seconds");
        TestAssert.True(Of(hero, "UdyrTigerShred") is not null, "the next hit rends");
        Swing(match, match.Foe, 2.4);
        var hits = HitsOn(match, "Foe");
        var swing = hits.Single(hit => hit.Kind == "Basic");
        NearF32(hero.AttackDamage, swing.Raw, "the swing is the attack damage");
        var tick = (30.0 + 1.5 * hero.AttackDamage) * 0.25;
        var ticks = hits.Where(hit => hit.Kind != "Basic").ToArray();
        TestAssert.Equal(4, ticks.Length, "a tick at 0, 0.5, 1.0 and 1.5 s");
        foreach (var hit in ticks) NearF32(tick, hit.Raw, "a quarter of 30 + 1.5 attack damage a tick, magic");
        TestAssert.True(Of(hero, "UdyrTigerShred") is null, "the mark is spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-udyr-turtle-stance-shields-and-restores-from-the-dealt-hit")]
    public static void TurtleShieldsFiveSecondsAndRestoresAShareOfTheDamageDealt()
    {
        using var match = Melee();
        var hero = match.Hero;
        match.Cast(1, hero);
        match.Advance(0.3);
        TestAssert.True(Of(hero, "UdyrTurtleActivation") is not null, "the shield buff");
        TestAssert.Near(0.0, hero.CritChance, 1e-9, "the attacks cannot crit");
        // 100 true damage: 60 is absorbed (rank 1, no ability power), 40 reaches the health.
        var health = hero.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 100.0, DamageType.True, DamageSourceKind.Raw);
        TestAssert.Near(40.0, health - hero.Health, 0.5, "the shield took 60");
        TestAssert.True(Of(hero, "UdyrTurtleActivation") is null, "and is gone");
        // The attack restores a tenth of what it dealt as health and half of that as mana, from the hit the script sees (after the armor).
        hero.Health = hero.MaxHealth * 0.5;
        hero.Mana = hero.MaxMana * 0.5;
        // The regeneration of the seconds the swing takes is measured first, with the same wait and no swing, and added to what the hit restores.
        var restHealth = hero.Health;
        var restMana = hero.Mana;
        match.Advance(1.0);
        var regenHealth = hero.Health - restHealth;
        var regenMana = hero.Mana - restMana;
        hero.Health = hero.MaxHealth * 0.5;
        hero.Mana = hero.MaxMana * 0.5;
        var healthBefore = hero.Health;
        var manaBefore = hero.Mana;
        Swing(match, match.Foe);
        var dealt = HitsOn(match, "Foe").Single(hit => hit.Kind == "Basic").Dealt;
        TestAssert.Near(Mitigated(hero.AttackDamage, match.Foe.Armor), dealt, 0.05, "the hit after the armor");
        TestAssert.Near(0.10 * dealt + regenHealth, hero.Health - healthBefore, 0.3, "10 percent of the damage dealt as health");
        TestAssert.Near(0.05 * dealt + regenMana, hero.Mana - manaBefore, 0.3, "5 percent as mana");
        TestAssert.Equal(1, match.Log.EffectCalls.Count(call => call.Name == "ItemLifesteal.troy"), "the script's particle was drawn once, before the hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-udyr-bear-stance-stuns-once-in-five-seconds")]
    public static void BearStunsTheFirstHitAndMarksTheTargetForFiveSeconds()
    {
        using var match = Melee();
        var hero = match.Hero;
        var speed = hero.Speed;
        match.Cast(2, hero);
        match.Advance(0.3);
        TestAssert.Near(speed * 1.15, hero.Speed, speed * 0.01, "the rush: 15 percent at rank 1");
        TestAssert.Equal("Idle2", hero.ScriptAnimationOverrides.GetValueOrDefault("idle1"), "the stance idle");
        // The swing is stepped, to read the stun the tick it appears (a buff's ExpiresAt is the time it has left).
        hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "swing");
        BuffState? stun = null;
        for (var tick = 0; tick < 30 && stun is null; tick++)
        {
            match.Advance(Step);
            stun = Of(match.Foe, "Stun");
        }
        TestAssert.True(stun is not null, "the first hit stuns");
        TestAssert.Near(1.0, stun!.ExpiresAt, 0.05, "for a second");
        TestAssert.True(Of(match.Foe, "UdyrBearStunCheck") is not null, "and marks the unit");
        match.Advance(1.2);
        TestAssert.True(Of(match.Foe, "Stun") is null, "the stun is over");
        Swing(match, match.Foe, 0.6);
        TestAssert.True(Of(match.Foe, "Stun") is null, "the second hit, inside the five seconds, stuns nobody");
        match.Advance(5.0);
        Swing(match, match.Foe, 0.6);
        TestAssert.True(Of(match.Foe, "Stun") is not null, "after the mark it stuns again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-udyr-phoenix-stance-pulses-and-flames-every-third-attack")]
    public static void PhoenixPulsesFiveTimesAndBreathesOnTheThirdHit()
    {
        using var match = Melee();
        var hero = match.Hero;
        var behind = Enemy(match, "Behind", new Position2(7300, 7000));
        var attack = hero.AttackDamage;
        var power = hero.AbilityPower;
        match.Cast(3, hero);
        match.Advance(0.3);
        NearF32(attack + 8.0, hero.AttackDamage, "+8 attack damage at rank 1");
        NearF32(power + 16.0, hero.AbilityPower, "+16 ability power at rank 1");
        match.Advance(5.4);
        var pulses = HitsOn(match, "Behind");
        TestAssert.Equal(5, pulses.Count, "a pulse a second for five seconds, 325 around");
        foreach (var pulse in pulses) NearF32(15.0 + 0.25 * 16.0, pulse.Raw, "15 + a quarter of the ability power");
        TestAssert.Near(attack, hero.AttackDamage, 1e-6, "the bonuses end with the activation");
        // The flame: two plain swings, then the third with the cone (40 at rank 1, magic, in front, 625 long).
        var before = HitsOn(match, "Behind").Count;
        Swing(match, match.Foe);
        Swing(match, match.Foe);
        TestAssert.Equal(before, HitsOn(match, "Behind").Count, "no flame before the third attack");
        Swing(match, match.Foe);
        var flames = HitsOn(match, "Behind").Skip(before).ToArray();
        TestAssert.Equal(1, flames.Length, "the third attack breathes once on the unit behind the target");
        NearF32(40.0 + 0.25 * hero.AbilityPower, flames[0].Raw, "40 + a quarter of the ability power");
        Swing(match, match.Foe);
        Swing(match, match.Foe);
        TestAssert.Equal(1, HitsOn(match, "Behind").Skip(before).Count(), "and not before the sixth");
        Swing(match, match.Foe);
        TestAssert.Equal(2, HitsOn(match, "Behind").Skip(before).Count(), "the sixth breathes again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine pieces: resuming a script after a block
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-interpreter-resumes-after-a-block-in-the-scope-it-left")]
    public static void ExecuteAfterRunsTheRestOfEveryLevel()
    {
        // 1: n1++   2: if a == 1 { 2.1: n21++, 2.2: n22++ }   3: n3++   4: else { n4++ }
        var blocks = new List<BbBlock>
        {
            BbTest.Increment("n1"),
            BbTest.If("a", "CO_EQUAL", 1, BbTest.Increment("n21"), BbTest.Increment("n22")),
            BbTest.Increment("n3"),
            BbTest.Block("BBElse", [], BbTest.Increment("n4")),
        };
        var core = new OracleCore();
        var interpreter = new BbInterpreter(new OracleHost(core));
        var scope = BbScope.ForHook(OracleCore.Owner, OracleCore.Attacker, OracleCore.Target, OracleCore.Caster, null);
        foreach (var name in new[] { "n1", "n21", "n22", "n3", "n4" }) scope.Vars[name] = 0.0;
        scope.Vars["a"] = 1.0;
        scope.Vars[BbScope.LastIfSucceededKey] = true; // the state the interrupted run left: the If at block 2 succeeded
        interpreter.ExecuteAfter(blocks, [2, 1], scope);
        TestAssert.Equal(0.0, scope.Vars["n1"], "what came before is not run again");
        TestAssert.Equal(0.0, scope.Vars["n21"], "nor is the block the run stopped at");
        TestAssert.Equal(1.0, scope.Vars["n22"], "the rest of the body is");
        TestAssert.Equal(1.0, scope.Vars["n3"], "then the siblings after the conditional");
        TestAssert.Equal(0.0, scope.Vars["n4"], "and the else of the conditional that succeeded stays unrun");
    }
}
