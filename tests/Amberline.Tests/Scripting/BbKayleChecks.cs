using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine M3: Kayle plays from her shipped scripts in a production <c>MatchRuntime</c>. Expected numbers come
/// from the shipped files (script tables and spell inibins) and are pinned a second time as literals.
/// </summary>
public static class BbKayleChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, packets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Kayle");
        TestAssert.Equal(19, module.ScriptNames.Count, "the Kayle closure is 19 scripts (audit bb-coverage)");
        var hero = new Entity { ChampionName = "Kayle" };
        TestAssert.Equal(["JudicatorReckoning", "JudicatorDivineBlessing", "JudicatorRighteousFury", "JudicatorIntervention"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Kayle.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Kayle");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 19 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Kayle hook runs is bound: " + string.Join(", ", unbound));
        TestAssert.Equal("JudicatorRighteousFuryAttack", new GameDataExtra().ExtraSpell("Kayle", 0), "ExtraSpell1 is the Righteous Fury attack");
    }

    /// <summary>Reads a character's extra spell name straight from the inibin (the check does not use the engine's own reader).</summary>
    private sealed class GameDataExtra
    {
        public string? ExtraSpell(string champion, int index) =>
            GameData.Default.Champion(champion).Raw.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{index + 1}"), out var value) &&
            value.Kind == InibinValueKind.String ? value.AsString() : null;
    }

    [Check("scripting.bb-kayle-cast-packets-pass-the-four-client-gates")]
    public static void CastPacketsCarryNamesTimesCostsAndTargets()
    {
        using var match = new Match("Kayle");
        match.Learn(1);
        var spells = GameData.Default.Champion("Kayle").SpellNames;
        TestAssert.Equal(["JudicatorReckoning", "JudicatorDivineBlessing", "JudicatorRighteousFury", "JudicatorIntervention"],
            spells.ToArray(), "slot names");
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Hero);
        TestAssert.Equal([70.0, 60.0, 45.0, 100.0], snapshot.ManaCosts.ToArray(), "rank-one mana costs from the spell inibins");
        TestAssert.True(snapshot.CanCastBits1 == uint.MaxValue && snapshot.CanCastBits2 == uint.MaxValue, "cast bits are open");
        var expectedCooldown = new[] { 8.0, 15.0, 16.0, 90.0 };
        var expectedCost = new[] { 70.0, 60.0, 45.0, 100.0 };
        var targets = new Entity?[] { match.Foe, match.Ally, null, match.Ally };
        var missile = new[] { true, false, false, false };
        for (var slot = 0; slot < 4; slot++)
        {
            match.Refill();
            var before = match.Log.Casts.Count;
            TestAssert.True(match.Cast(slot, targets[slot]), $"{spells[slot]} is accepted");
            TestAssert.Equal(before + 1, match.Log.Casts.Count, $"{spells[slot]} announces exactly one cast");
            var cast = match.Log.Casts[^1];
            var data = GameData.Default.Spell(spells[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(SpellSystem.ProtocolHash(spells[slot]), cast.SpellHash, $"{spells[slot]} hash");
            TestAssert.Equal((byte)slot, cast.Slot, $"{spells[slot]} slot");
            TestAssert.Equal(1, cast.Rank, $"{spells[slot]} rank");
            TestAssert.Equal(match.Hero.NetId, cast.CasterNetId, $"{spells[slot]} caster");
            TestAssert.Near(expectedCost[slot], cast.ManaCost, 1e-6, $"{spells[slot]} mana cost");
            TestAssert.Near(expectedCooldown[slot], cast.Cooldown, 1e-6, $"{spells[slot]} cooldown");
            TestAssert.True(cast.DesignerTotalTime > 0.0, $"{spells[slot]} designer total time {cast.DesignerTotalTime}");
            TestAssert.Near(data.CastDelay, cast.DesignerCastTime, 1e-6, $"{spells[slot]} designer cast time is the data's");
            TestAssert.Near(data.TotalTime, cast.DesignerTotalTime, 1e-6, $"{spells[slot]} designer total time is the data's");
            TestAssert.Equal(targets[slot] is null ? 0 : 1, cast.Targets.Count, $"{spells[slot]} target list");
            if (targets[slot] is { } target) TestAssert.Equal(target.NetId, cast.Targets[0].NetId, $"{spells[slot]} names its target");
            TestAssert.Equal(missile[slot], cast.MissileNetId != 0u, $"{spells[slot]} missile");
            match.Advance(1.0);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Reckoning and the damage mark
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-q-reckoning-damages-slows-and-marks")]
    public static void ReckoningDamagesSlowsAndAmplifiesHerLaterDamage()
    {
        var reckoning = Script("JudicatorReckoning");
        var hit = Blocks(reckoning, "TargetExecute").First(block => block.Function == "BBApplyDamage");
        var damage = Table(hit, "DamageByLevel");
        var data = GameData.Default.Spell("JudicatorReckoning", 5);
        TestAssert.Equal([60.0, 110.0, 160.0, 210.0, 260.0], damage, "JudicatorReckoning.luaobj DamageByLevel");
        TestAssert.Near(0.0, hit.Number("SpellDamageRatio")!.Value, 1e-12, "the ratios are explicit terms (AP and bonus AD, each 1.0)");
        var terms = Blocks(reckoning, "TargetExecute").Where(block => block.Function == "BBMath" && block.Number("Src1Value") == 1.0).ToArray();
        TestAssert.Equal(2, terms.Length, "one coefficient 1.0 for ability power, one for bonus attack damage");
        var slow = ByLevel("JudicatorReckoning", "TargetExecute", "MoveSpeedMod");
        TestAssert.Equal([-0.35, -0.35, -0.35, -0.35, -0.35], slow, "JudicatorReckoning.luaobj MoveSpeedMod");
        var buff = Blocks(reckoning, "TargetExecute").First(block => block.Function == "BBSpellBuffAdd");
        TestAssert.Near(4.0, buff.Number("Duration")!.Value, 1e-12, "the slow lasts 4 s");
        TestAssert.Equal((double)BbBuffAddType.BUFF_STACKS_AND_OVERLAPS, (double)BbConstants.All["BUFF_STACKS_AND_OVERLAPS"], "the add type");
        var amplify = Table(Blocks(CharScript("Kayle"), "CharOnPreDealDamage").First(block => block.Function == "BBSetVarInTable"), "SrcValueByLevel");
        TestAssert.Equal([1.06, 1.07, 1.08, 1.09, 1.1], amplify, "CharScriptKayle.luaobj damage multiplier on a marked target");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"JudicatorReckoning.inibin Effect1 rank {rank}");
            TestAssert.Equal(35.0, data.EffectNumber(2, rank), $"JudicatorReckoning.inibin Effect2 rank {rank} is the slow");
            TestAssert.Near((amplify[rank - 1] - 1.0) * 100.0, data.EffectNumber(3, rank), 1e-9, $"JudicatorReckoning.inibin Effect3 rank {rank} is the bonus percent");
        }
        TestAssert.Equal([70.0, 75.0, 80.0, 85.0, 90.0], data.ManaCosts.ToArray(), "Reckoning mana costs");
        TestAssert.Equal([8.0, 8.0, 8.0, 8.0, 8.0], data.Cooldowns.ToArray(), "Reckoning cooldowns");
        TestAssert.Near(650.0, data.CastRange, 1e-9, "Reckoning range");

        using var match = new Match("Kayle");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var baseAttack = GameData.Default.StatsAtLevel(GameData.Default.Champion("Kayle"), match.Hero.Level).AttackDamage;
        var bonusAttack = match.Hero.AttackDamage - baseAttack;
        var spellBlock = match.Foe.SpellBlock;
        var speed = match.Foe.Speed;
        match.Refill();
        TestAssert.True(match.CanCast(0, match.Foe).Allowed, "an enemy in range");
        TestAssert.True(match.CanCast(0, match.Ally).Failure == SpellCastFailure.Target, "an ally is not a target");
        TestAssert.True(match.Cast(0, match.Foe), "Q cast");
        match.Advance(0.15);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands before the windup and the flight");
        match.Advance(0.7);
        // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        TestAssert.Near(8.0, match.Hero.SpellCooldowns[0] + 0.85 - match.CastFrameOf(0), Step + 1e-6, "cooldown 8 s");
        var raw = 160.0 + ap + bonusAttack;
        var qHit = HitsOn(match, "Foe").Single();
        TestAssert.Near(raw, qHit.Raw, 1e-6, "rank 3 damage 160 + AP + bonus AD");
        TestAssert.True(qHit.Kind == "Spell", "damage source SPELL");
        TestAssert.Near(Mitigated(raw, spellBlock), qHit.Dealt, 1e-6, "magic damage mitigated by spell block (the mark is not on yet)");
        TestAssert.True(Buff(match.Foe, "JudicatorReckoning") is { Type: BuffType.Slow, Stacks: 1 }, "the slow buff is on the target");
        TestAssert.Near(Amberline.Simulation.Stats.HeroStatCalculator.ApplyMovementSoftCaps(speed * (1.0 - 0.35)), match.Foe.Speed, 1e-6,
            "35 percent slower (through the shared speed soft cap)");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Kayle->Foe", StringComparison.Ordinal)), "assist marker");
        // The mark: her next damage to the target is 8 percent higher (rank 3), whatever its source; nobody else's is.
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.Health = match.Foe.MaxHealth;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "an attack on the marked target");
        match.Advance(0.9);
        var attack = HitsOn(match, "Foe").Single(item => item.Kind == "Basic");
        TestAssert.Near(match.Hero.AttackDamage * 1.08, attack.Raw, 1e-6, "rank 3 mark: +8 percent damage from Kayle");
        // A second caster stacks its own slow beside hers (Reckoning overlaps).
        match.Module.Buffs!.Add(new BbBuffRequest(match.Foe, match.Ally, "JudicatorReckoning", (int)BbBuffAddType.BUFF_STACKS_AND_OVERLAPS,
            (int)BbBuffType.BUFF_Slow, 100, 4.0, 0.0, false, new BbTable { ["MoveSpeedMod"] = -0.35 }));
        TestAssert.Equal(2, match.Foe.Buffs.Count(item => item.Name == "JudicatorReckoning"), "one slow per caster");
        // It ends after four seconds.
        match.Advance(4.2);
        TestAssert.True(Buff(match.Foe, "JudicatorReckoning") is null, "the slow ends");
        TestAssert.Near(speed, match.Foe.Speed, 1e-6, "speed restored");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Divine Blessing
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-w-divine-blessing-heals-and-speeds-an-ally")]
    public static void DivineBlessingHealsAndHastesTheTarget()
    {
        var blessing = Script("JudicatorDivineBlessing");
        var heal = ByLevel("JudicatorDivineBlessing", "TargetExecute", "HealLevel");
        var speed = ByLevel("JudicatorDivineBlessing", "TargetExecute", "MoveSpeedMod");
        var data = GameData.Default.Spell("JudicatorDivineBlessing", 5);
        TestAssert.Equal([45.0, 85.0, 125.0, 165.0, 205.0], heal, "JudicatorDivineBlessing.luaobj HealLevel");
        TestAssert.Equal([0.15, 0.17, 0.19, 0.21, 0.23], speed, "JudicatorDivineBlessing.luaobj MoveSpeedMod");
        TestAssert.Near(0.35, Blocks(blessing, "TargetExecute").First(block => block.Function == "BBMath" && block.Text("Src2Var") is null &&
            block.Text("Src1Var") == "AbilityPower").Number("Src2Value")!.Value, 1e-12, "JudicatorDivineBlessing.luaobj healing ratio");
        TestAssert.Near(2.5, Blocks(blessing, "TargetExecute").First(block => block.Function == "BBSpellBuffAdd" && block.Text("BuffName") is null)
            .Number("Duration")!.Value, 1e-12, "the speed lasts 2.5 s");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(heal[rank - 1], data.EffectNumber(1, rank), $"JudicatorDivineBlessing.inibin Effect1 rank {rank}");
            TestAssert.Equal(speed[rank - 1] * 100.0, data.EffectNumber(2, rank), $"JudicatorDivineBlessing.inibin Effect2 rank {rank} is the speed percent");
        }
        TestAssert.Near(0.35, data.Coefficient, 1e-6, "JudicatorDivineBlessing.inibin Coefficient");
        TestAssert.Equal([60.0, 65.0, 70.0, 75.0, 80.0], data.ManaCosts.ToArray(), "Divine Blessing mana costs");
        TestAssert.Equal([15.0, 15.0, 15.0, 15.0, 15.0], data.Cooldowns.ToArray(), "Divine Blessing cooldowns");
        TestAssert.Near(1000.0, data.CastRange, 1e-9, "Divine Blessing range");
        TestAssert.True(data.IsInstant, "Divine Blessing is an instacast");

        using var match = new Match("Kayle");
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Hero.AbilityPower;
        var amount = 85.0 + 0.35 * ap;
        var speedBefore = match.Ally.Speed;
        match.Ally.Health = 50.0;
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(7700, 7000);
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.CanCast(1, match.Ally).Allowed, "an ally champion up to 1000 away");
        TestAssert.True(match.CanCast(1, match.Foe).Failure == SpellCastFailure.Target, "an enemy is not a target");
        TestAssert.True(match.Cast(1, match.Ally), "W cast");
        TestAssert.Near(mana - 65.0, match.Hero.Mana, 1e-6, "rank 2 costs 65 mana");
        TestAssert.Near(15.0, match.Hero.SpellCooldowns[1], 1e-6, "cooldown 15 s");
        TestAssert.Near(Math.Min(match.Ally.MaxHealth, 50.0 + amount), match.Ally.Health, 1.0, "an instacast heals at once: 85 + 0.35 AP");
        TestAssert.True(Buff(match.Ally, "JudicatorDivineBlessing") is { Type: BuffType.CombatEnhancer }, "the speed buff is on the ally");
        TestAssert.Near(2.5, Buff(match.Ally, "JudicatorDivineBlessing")!.ExpiresAt, 0.1, "for 2.5 s");
        TestAssert.Near(speedBefore * 1.17, match.Ally.Speed, 1e-6, "rank 2: +17 percent speed");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Kayle->Ally", StringComparison.Ordinal)), "assist marker on the ally");
        TestAssert.True(Buff(match.Hero, "KayleDivineBlessingAnim") is { Type: BuffType.Internal }, "the one second animation buff is on Kayle");
        TestAssert.True(match.Log.Animations.Any(line => line.Contains("play Kayle Spell2", StringComparison.Ordinal)), "Spell2 plays: " + string.Join(" | ", match.Log.Animations));
        match.Advance(1.2);
        TestAssert.True(Buff(match.Hero, "KayleDivineBlessingAnim") is null, "the animation buff ends");
        TestAssert.True(match.Log.Animations.Any(line => line.Contains("stop Kayle", StringComparison.Ordinal)), "and unlocks the animation");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Ally, "JudicatorDivineBlessing") is null, "the speed ends");
        TestAssert.Near(speedBefore, match.Ally.Speed, 1e-6, "speed restored");

        // On herself: the same heal and speed (the flags carry no NotAffectSelf).
        match.Refill();
        match.Hero.Health = 40.0;
        var heroSpeed = match.Hero.Speed;
        TestAssert.True(match.Cast(1, match.Hero), "W on herself");
        TestAssert.Near(Math.Min(match.Hero.MaxHealth, 40.0 + amount), match.Hero.Health, 1.0, "the self heal");
        TestAssert.Near(heroSpeed * 1.17, match.Hero.Speed, 1e-6, "and the speed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Righteous Fury
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-e-righteous-fury-lengthens-her-reach-and-splashes")]
    public static void RighteousFuryOverridesTheAutoAttack()
    {
        var fury = Script("JudicatorRighteousFury");
        var attack = Script("JudicatorRighteousFuryAttack");
        var range = ByLevel("JudicatorRighteousFury", "SelfExecute", "AttackRangeIncrease");
        var bonus = ByLevel("JudicatorRighteousFuryAttack", "TargetExecute", "BonusDamage");
        var splash = ByLevel("JudicatorRighteousFuryAttack", "TargetExecute", "DamagePercent");
        var data = GameData.Default.Spell("JudicatorRighteousFury", 5);
        var attackData = GameData.Default.Spell("JudicatorRighteousFuryAttack", 5);
        TestAssert.Equal([400.0, 400.0, 400.0, 400.0, 400.0], range, "JudicatorRighteousFury.luaobj AttackRangeIncrease");
        TestAssert.Equal([20.0, 30.0, 40.0, 50.0, 60.0], bonus, "JudicatorRighteousFuryAttack.luaobj BonusDamage");
        TestAssert.Equal([0.2, 0.25, 0.3, 0.35, 0.4], splash, "JudicatorRighteousFuryAttack.luaobj DamagePercent");
        TestAssert.Near(0.2, Blocks(attack, "TargetExecute").First(block => block.Function == "BBMath" && block.Text("DestVar") == "AbilityPower")
            .Number("Src1Value")!.Value, 1e-12, "the ability power ratio");
        TestAssert.Near(10.0, Blocks(fury, "SelfExecute").First(block => block.Function == "BBSpellBuffAdd" && block.Text("BuffName") is null)
            .Number("Duration")!.Value, 1e-12, "Righteous Fury lasts 10 s");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(bonus[rank - 1], data.EffectNumber(1, rank), $"JudicatorRighteousFury.inibin Effect1 rank {rank}");
            TestAssert.Equal(splash[rank - 1] * 100.0, data.EffectNumber(2, rank), $"JudicatorRighteousFury.inibin Effect2 rank {rank} is the splash percent");
        }
        TestAssert.Near(0.2, data.Coefficient, 1e-6, "JudicatorRighteousFury.inibin Coefficient");
        TestAssert.Equal([45.0, 45.0, 45.0, 45.0, 45.0], data.ManaCosts.ToArray(), "Righteous Fury mana costs");
        TestAssert.Equal([16.0, 16.0, 16.0, 16.0, 16.0], data.Cooldowns.ToArray(), "Righteous Fury cooldowns");
        TestAssert.Near(779.9, attackData.MissileSpeed, 1e-3, "the empowered attack's missile speed");

        using var match = new Match("Kayle");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var baseRange = match.Hero.AttackRange;
        var baseMissile = match.Hero.MissileSpeed;
        TestAssert.Near(125.0, baseRange, 1e-9, "Kayle is a melee champion (attack range 125)");
        match.Foe.Position = new Position2(7400, 7000);
        match.Foe.Health = match.Foe.MaxHealth;
        var splashed = Enemy(match, "Splashed", new Position2(7400, 7200));
        var outside = Enemy(match, "Outside", new Position2(7400, 7400));
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.False(match.Runtime.Combat.CanAttack(match.Hero, match.Foe), "without the buff she cannot reach 400 units");
        TestAssert.True(match.Cast(2), "E cast");
        TestAssert.Near(mana - 45.0, match.Hero.Mana, 1e-6, "costs 45 mana at every rank");
        TestAssert.Near(16.0, match.Hero.SpellCooldowns[2], 1e-6, "cooldown 16 s");
        TestAssert.True(Buff(match.Hero, "JudicatorRighteousFury") is { Type: BuffType.CombatEnhancer }, "the buff is on Kayle");
        TestAssert.Near(10.0, Buff(match.Hero, "JudicatorRighteousFury")!.ExpiresAt, 0.1, "for ten seconds");
        TestAssert.Near(baseRange + 400.0, match.Hero.AttackRange, 1e-9, "attack range +400");
        TestAssert.True(match.Hero.ScriptAttackOverride is { Slot: 42, SpellName: "JudicatorRighteousFuryAttack", Level: 1 },
            "her attacks now fire ExtraSpell1 (slot 42) at level 1");
        // JudicatorRighteousFuryAttack.inibin is CastType 0: the client flies a missile only for CastType 1, 2 and 3 (its cast routine),
        // so the 780 in the data is unused and the attack lands at its cast frame (16 October 2026 playtest: the passive
        // arrived half a second after the client's hit).
        TestAssert.Near(0.0, match.Hero.MissileSpeed, 1e-9, "no flight for the new attack spell (CastType 0)");
        TestAssert.True(match.Log.AttackSlots.Any(line => line.EndsWith("Kayle 42", StringComparison.Ordinal)), "the client is told: " + string.Join(" | ", match.Log.AttackSlots));
        TestAssert.True(Buff(match.Hero, "KayleRighteousFuryAnim") is not null, "the animation buff");
        TestAssert.True(match.Log.Animations.Any(line => line.Contains("play Kayle Spell3", StringComparison.Ordinal)), "Spell3 plays");

        // The attack: 400 away, a missile, then the engine's own hit plus the script's bonus magic and splash.
        TestAssert.True(match.Runtime.Combat.CanAttack(match.Hero, match.Foe), "now she reaches it");
        var totalAttack = match.Hero.AttackDamage;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack starts");
        match.Advance(1.2);
        var onFoe = HitsOn(match, "Foe");
        var basic = onFoe.Single(item => item.Kind == "Basic");
        TestAssert.Near(totalAttack, basic.Raw, 1e-6, "the attack itself (total attack damage) is dealt once");
        var bonusMagic = onFoe.Single(item => item.Kind == "SpellAoe");
        NearF32(40.0 + 0.2 * ap, bonusMagic.Raw, "rank 3 bonus magic damage on the target: 40 + 0.2 AP");
        var cleave = HitsOn(match, "Splashed").Single();
        NearF32(0.3 * totalAttack + 40.0 + 0.2 * ap, cleave.Raw, "the unit beside it takes the bonus magic plus 30 percent of her attack damage");
        TestAssert.True(cleave.Kind == "SpellAoe", "splash damage source SPELLAOE");
        TestAssert.Equal(0, HitsOn(match, "Outside").Count, "nothing beyond 300 of the target");
        TestAssert.True(basic.At > match.Now - 1.2 + 0.3 && basic.At < match.Now - 1.2 + 0.6, "the physical hit comes at the cast frame, after the windup and before any flight could end");

        // After ten seconds: reach, missile and the client's attack slot go back; an unfinished swing is dropped.
        match.Advance(10.0);
        TestAssert.True(Buff(match.Hero, "JudicatorRighteousFury") is null, "the buff ends");
        TestAssert.Near(baseRange, match.Hero.AttackRange, 1e-9, "range restored");
        TestAssert.True(match.Hero.ScriptAttackOverride is null, "the override is gone");
        TestAssert.Near(baseMissile, match.Hero.MissileSpeed, 1e-9, "missile speed restored");
        TestAssert.True(match.Log.AttackSlots.Any(line => line.EndsWith("Kayle -1", StringComparison.Ordinal)), "the client gets slot -1 back");
        TestAssert.False(match.Runtime.Combat.CanAttack(match.Hero, match.Foe), "she is back to melee");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Intervention
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-r-intervention-makes-an-ally-invulnerable-but-lets-them-act")]
    public static void InterventionProtectsWithoutStunningTheTarget()
    {
        var intervention = Script("JudicatorIntervention");
        var duration = Table(Blocks(intervention, "TargetExecute").Last(block => block.Function == "BBSpellBuffAdd"), "DurationByLevel");
        var data = GameData.Default.Spell("JudicatorIntervention", 3);
        TestAssert.Equal([2.0, 2.5, 3.0], duration, "JudicatorIntervention.luaobj DurationByLevel");
        for (var rank = 1; rank <= 3; rank++) TestAssert.Equal(duration[rank - 1], data.EffectNumber(1, rank), $"JudicatorIntervention.inibin Effect1 rank {rank}");
        TestAssert.Equal([90.0, 75.0, 60.0], data.Cooldowns.ToArray(), "Intervention cooldowns (the inibin's, not the script's flat 90)");
        TestAssert.Equal([100.0, 75.0, 50.0], data.ManaCosts.ToArray(), "Intervention mana costs");
        TestAssert.Near(1200.0, data.CastRange, 1e-9, "Intervention range");
        TestAssert.True(data.IsInstant, "Intervention is an instacast");

        using var match = new Match("Kayle");
        match.Learn(2);
        match.Hero.SpellRanks[3] = 2;
        match.Ally.Position = new Position2(7600, 7000);
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.CanCast(3, match.Ally).Allowed, "an allied champion");
        TestAssert.True(match.CanCast(3, match.Foe).Failure == SpellCastFailure.Target, "not an enemy");
        TestAssert.True(match.Cast(3, match.Ally), "R on the ally");
        TestAssert.Near(mana - 75.0, match.Hero.Mana, 1e-6, "rank 2 costs 75 mana");
        TestAssert.Near(75.0, match.Hero.SpellCooldowns[3], 1e-6, "rank 2 cooldown 75 s");
        var shield = Buff(match.Ally, "JudicatorIntervention");
        TestAssert.True(shield is { Type: BuffType.Invulnerability }, "the ally holds the invulnerability buff");
        TestAssert.Near(2.5, shield!.ExpiresAt, 0.1, "for 2.5 s at rank 2");
        TestAssert.True(shield.Payload.Invulnerable && shield.Payload.ActionsAllowed, "invulnerable, and the type does not stop the holder");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("eyeforaneye_cas.troy", StringComparison.OrdinalIgnoreCase)), "the cast effect on the target");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Kayle->Ally", StringComparison.Ordinal)), "assist marker");
        TestAssert.True(Buff(match.Hero, "KayleInterventionAnim") is not null, "the animation buff");
        TestAssert.True(match.Log.Animations.Any(line => line.Contains("play Kayle Spell4", StringComparison.Ordinal)), "Spell4 plays");

        // Damage is refused, the ally still acts: the cast bits and the targetable bit stay open.
        var health = match.Ally.Health;
        TestAssert.False(match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Ally.NetId, 100.0, DamageType.Physical, DamageSourceKind.Spell),
            "damage to the invulnerable ally is refused");
        TestAssert.Near(health, match.Ally.Health, 1e-9, "no health lost");
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Ally);
        TestAssert.Equal(7, snapshot.ActionState, "the ally can still move, attack and cast");
        TestAssert.Equal(1, snapshot.IsTargetable, "and stays targetable");
        match.Ally.SpellRanks[0] = 1;
        match.Ally.Mana = match.Ally.MaxMana;
        match.Ally.SpellCooldowns[0] = 0.0;
        TestAssert.True(match.Runtime.Spells.CanCast(match.Ally, 0, match.Foe.NetId, new Position3(match.Foe.Position.X, 0, match.Foe.Position.Z), match.Now).Allowed,
            "the protected ally can cast a spell");
        match.Advance(2.7);
        TestAssert.True(Buff(match.Ally, "JudicatorIntervention") is null, "the protection ends after 2.5 s");
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Ally.NetId, 10.0, DamageType.Physical, DamageSourceKind.Spell), "damage works again");

        // On herself.
        match.Advance(80.0);
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "R on herself");
        TestAssert.True(Buff(match.Hero, "JudicatorIntervention") is { Type: BuffType.Invulnerability }, "Kayle is invulnerable");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("eyeforaneye_self.troy", StringComparison.OrdinalIgnoreCase)), "the self effect");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Holy Fervor
    // ------------------------------------------------------------------------------------------------------------

    // Batch 227 (owner request, 25 October 2026): the fountain's swing (the shipped OrderTurretShrineBasicAttack TargetExecute, DAMAGESOURCE_INTERNALRAW
    // on a champion) kills an ally under Kayle's Intervention. The invulnerability is the shipped JudicatorIntervention's SetInvulnerable.
    [Check("scripting.bb-kayle-fountain-kills-through-intervention")]
    public static void TheFountainKillsThroughIntervention()
    {
        using var match = new Match("Kayle");
        match.Learn(2);
        match.Hero.SpellRanks[3] = 2;
        match.Refill();
        var ally = match.Ally;
        var fountain = match.ChaosFountain;
        // Kayle stands within her 1200 cast range of the ally; both are inside the fountain's reach.
        match.Hero.Position = fountain.Position + new Position2(300, 0);
        ally.Position = fountain.Position + new Position2(400, 0);
        ally.Health = 600.0;
        TestAssert.True(match.Cast(3, ally), "R on the ally");
        TestAssert.True(Buff(ally, "JudicatorIntervention") is { Type: BuffType.Invulnerability }, "the ally is invulnerable");
        fountain.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(fountain, ally), "the fountain swings at the invulnerable ally");
        match.Advance(2.0);
        TestAssert.False(ally.Alive, "its 1000 true damage kills him through Kayle's Intervention");
    }

    [Check("scripting.bb-kayle-passive-holy-fervor-shreds-champions-she-hits")]
    public static void HolyFervorStacksAnArmorAndSpellBlockShred()
    {
        var debuff = Script("JudicatorHolyFervorDebuff");
        var shred = Blocks(debuff, "BuffOnUpdateStats").Where(block => block.Function == "BBIncStat").ToArray();
        TestAssert.Equal(["IncPercentArmorMod", "IncPercentSpellBlockMod"], shred.Select(block => ((BbValue.Reference)block.Param("Stat")!).Name).ToArray(),
            "JudicatorHolyFervorDebuff.luaobj shreds armor and spell block");
        TestAssert.Equal([-0.03, -0.03], shred.Select(block => block.Number("Delta")!.Value).ToArray(), "3 percent per stack");
        var add = Blocks(CharScript("Kayle"), "CharOnHitUnit").First(block => block.Function == "BBSpellBuffAdd");
        TestAssert.Near(5.0, add.Number("MaxStack")!.Value, 1e-12, "five stacks");
        TestAssert.Near(5.0, add.Number("Duration")!.Value, 1e-12, "for five seconds");

        using var match = new Match("Kayle");
        match.Learn(1);
        TestAssert.True(Buff(match.Hero, "JudicatorHolyFervor") is { Type: BuffType.Aura }, "CharOnActivate added the passive's buff");
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.Health = match.Foe.MaxHealth;
        var armor = match.Foe.Armor;
        var spellBlock = match.Foe.SpellBlock;
        var minion = Enemy(match, "Minion", new Position2(7000, 7100));
        for (var stacks = 1; stacks <= 6; stacks++)
        {
            match.Hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), $"attack {stacks}");
            match.Advance(match.Hero.AttackPeriod + 0.1);
            var expected = Math.Min(stacks, 5);
            TestAssert.True(Buff(match.Foe, "JudicatorHolyFervorDebuff") is { Type: BuffType.CombatDehancer } fervor && fervor.Stacks == expected,
                $"{expected} stacks after attack {stacks}: " + Names(match.Foe));
            TestAssert.Near(armor * (1.0 - 0.03 * expected), match.Foe.Armor, 1e-6, $"armor -{3 * expected} percent");
            TestAssert.Near(spellBlock * (1.0 - 0.03 * expected), match.Foe.SpellBlock, 1e-6, $"spell block -{3 * expected} percent");
        }
        TestAssert.Equal(1, match.Foe.Buffs.Count(buff => buff.Name == "JudicatorHolyFervorDebuff"), "one buff state holds the stacks");
        // Only champions: a minion takes no stack.
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, minion), "an attack on a minion");
        match.Advance(match.Hero.AttackPeriod + 0.1);
        TestAssert.True(Buff(minion, "JudicatorHolyFervorDebuff") is null, "minions are not shredded");
        // Five seconds without a hit and the stacks end.
        match.Advance(5.3);
        TestAssert.True(Buff(match.Foe, "JudicatorHolyFervorDebuff") is null, "the shred ends");
        TestAssert.Near(armor, match.Foe.Armor, 1e-6, "armor restored");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Registration, ranks and production safety
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-production-installs-the-sink-and-her-passives")]
    public static void KayleJoinsTheMatchThroughTheSharedScriptSink()
    {
        using var match = new Match("Kayle");
        TestAssert.True(((Amberline.Simulation.Runtime.MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks is not null,
            "a Kayle in the roster installs the sink when her hero is created");
        TestAssert.True(match.Module.IsBound, "the module is bound to the match");
        TestAssert.True(Buff(match.Hero, "JudicatorHolyFervor") is { Type: BuffType.Aura }, "CharOnActivate added Holy Fervor");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "ChampionChampionDelta"),
            "the shared system buffs are applied once, natively");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "batch 203: the champion's shipped CharOnActivate add is the one tower buff; nothing refuses it any more");
        // The tooltip of Righteous Fury (attack damage line) and of Reckoning (bonus attack damage) are published once a second.
        match.Advance(0.2);
        TestAssert.True(match.Hero.ScriptSpellTooltips.ContainsKey(2) && match.Hero.ScriptSpellTooltips.ContainsKey(0),
            "CharScriptKayle.UpdateSelfBuffActions wrote tooltip variables for the slots 0 and 2");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-kayle-ranks-levels-and-cooldowns")]
    public static void RankGatesLevelUpsAndCooldownsFollowTheData()
    {
        using var match = new Match("Kayle");
        var kayle = match.Hero;
        kayle.SkillPoints = 20;
        TestAssert.True(match.Runtime.Spells.RankUp(kayle, 0), "Q rank 1 at level 1");
        TestAssert.False(match.Runtime.Spells.RankUp(kayle, 0), "Q rank 2 needs level 3");
        TestAssert.False(match.Runtime.Spells.RankUp(kayle, 3), "R needs level 6");
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(kayle, 0.0, 100000.0), "experience");
        TestAssert.Equal(18, kayle.Level, "enough experience reaches level 18");
        for (var slot = 0; slot < 4; slot++)
            while (match.Runtime.Spells.RankUp(kayle, slot)) { }
        TestAssert.Equal([5, 5, 5, 3], kayle.SpellRanks.ToArray(), "all ranks reachable at level 18");

        var cooldowns = new[] { new[] { 8.0, 8, 8, 8, 8 }, new[] { 15.0, 15, 15, 15, 15 }, new[] { 16.0, 16, 16, 16, 16 }, new[] { 90.0, 75, 60 } };
        var manaCosts = new[] { new[] { 70.0, 75, 80, 85, 90 }, new[] { 60.0, 65, 70, 75, 80 }, new[] { 45.0, 45, 45, 45, 45 }, new[] { 100.0, 75, 50 } };
        for (var slot = 0; slot < 4; slot++)
        {
            var data = GameData.Default.Spell(GameData.Default.Champion("Kayle").SpellNames[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(cooldowns[slot], data.Cooldowns.ToArray(), $"slot {slot} cooldowns");
            TestAssert.Equal(manaCosts[slot], data.ManaCosts.ToArray(), $"slot {slot} mana costs");
        }
        foreach (var slot in new[] { 0, 3 })
        {
            kayle.SpellRanks[slot] = slot == 3 ? 2 : 4;
            match.Refill();
            kayle.CooldownMod = -0.1;
            kayle.Position = new Position2(7000, 7000);
            TestAssert.True(match.Cast(slot, slot == 0 ? match.Foe : match.Ally), $"slot {slot} cast");
            // Batch 340: a windup's cooldown starts at its cast frame (CastFrameOf; 0 for an instacast).
            match.Advance(match.CastFrameOf(slot));
            TestAssert.Near(cooldowns[slot][kayle.SpellRanks[slot] - 1] * 0.9, kayle.SpellCooldowns[slot], Step + 1e-6, $"slot {slot} cooldown with 10% reduction");
            match.Advance(0.5);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Kayle in a headless match (she has no bot script of her own: the match drives her spells)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kayle-plays-a-six-minute-headless-match", Serial = true)]
    public static void KayleCastsThroughAFullMatchWithoutScriptExceptionsOrPacketViolations()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-kayle-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] roster = ["Kayle", "Annie", "Janna", "Ezreal", "Vayne", "Taric", "Orianna", "Blitzcrank", "Nidalee", "Shaco"];
            var casts = new Dictionary<int, int>();
            var failures = new List<string>();
            var rng = new Random(20261016);
            var options = new Amberline.Tools.Diagnostics.HeadlessMatchOptions(20261016, Path.Combine(root, "m"), 6.0, roster)
            {
                OnStep = runtime =>
                {
                    var hero = runtime.World.Heroes().FirstOrDefault(item => item.ChampionName == "Kayle" && item.Team == Team.Order);
                    var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Kayle")!;
                    foreach (var failure in module.ScriptFailures.Skip(failures.Count))
                        failures.Add($"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}");
                    if (hero is null || !hero.Alive || runtime.Spells.CurrentTime < 5.0) return;
                    // Batch 617: W and E both need a rank, so a second skill point, which comes with level two. The bots of this match gain little
                    // experience (levels 1 to 4 after five and a half minutes, Kayle 230 experience with the client's movement and level two only
                    // at about five minutes with the port's), so the stand-in starts her at level two instead of leaving the precondition to chance.
                    if (hero.Level < 2) runtime.Combat.AwardHeroProgress(hero, experience: ProgressionRules.ExperienceThreshold(2) - hero.Experience);
                    // The bots only attack and walk; Kayle's spells are cast by this stand-in for a player, one attempt a step.
                    if (hero.SkillPoints > 0) runtime.Spells.RankUp(hero, new[] { 3, 1, 2, 0 }.First(slot => runtime.Spells.CanRankUp(hero, slot).Allowed));
                    var slot = rng.Next(4);
                    var enemy = runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team != hero.Team && unit.UnitType is UnitType.Hero or UnitType.Minion)
                        .OrderBy(unit => (unit.Position.X - hero.Position.X) * (unit.Position.X - hero.Position.X) +
                                         (unit.Position.Z - hero.Position.Z) * (unit.Position.Z - hero.Position.Z)).FirstOrDefault();
                    var friend = runtime.World.Heroes().Where(unit => unit.Alive && unit.Team == hero.Team).OrderBy(_ => rng.Next()).FirstOrDefault() ?? hero;
                    var target = slot is 0 ? enemy : slot is 1 or 3 ? friend : null;
                    var point = target?.Position ?? hero.Position;
                    var before = hero.SpellCooldowns[slot];
                    if (runtime.Spells.CanCast(hero, slot, target?.NetId ?? 0, new Position3(point.X, 0, point.Z), runtime.Spells.CurrentTime).Allowed &&
                        runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(slot, false, new Position3(point.X, 0, point.Z),
                            new Position3(point.X, 0, point.Z), target?.NetId ?? 0), runtime.Spells.CurrentTime) && hero.SpellCooldowns[slot] > before)
                        casts[slot] = casts.GetValueOrDefault(slot) + 1;
                },
            };
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(options);
            TestAssert.Equal("cap", report.Outcome, "six game minutes end at the cap, not in a crash");
            TestAssert.Equal(0L, report.PacketViolations, "the packet-stream checker finds no violation");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
            TestAssert.Equal(0, failures.Count, "no script exception: " + string.Join(" | ", failures.Take(3)));
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            TestAssert.False(log.Contains("script.exception", StringComparison.Ordinal), "no script.exception line in the session log");
            TestAssert.True(casts.ContainsKey(1) && casts.ContainsKey(2), "Kayle cast her self and ally spells (W and E): " +
                string.Join(", ", casts.OrderBy(pair => pair.Key).Select(pair => $"slot {pair.Key} x{pair.Value}")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
