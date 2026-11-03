using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3 (19 October 2026): Vayne plays from her shipped scripts in a production <c>MatchRuntime</c> (the hand-written module is gone). Expected numbers
/// come from the shipped files (script tables and spell inibins) and are pinned a second time as literals. Written, not run (owner rule: no check
/// runs during the overnight phase 3); the scenario <c>S-vayne</c> shows the same facts on a headless match.
/// </summary>
public static class BbVayneChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static double Between(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static double BonusAttackDamage(Match match) =>
        match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Vayne"), match.Hero.Level).AttackDamage;

    private static double Literal(BbScript script, string hook, string variable, string table = "")
    {
        var block = Blocks(script, hook).First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable &&
                                                       (table.Length == 0 || item.Text("DestVarTable") == table));
        return block.Number("SrcValue")!.Value;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Vayne");
        TestAssert.Equal(25, module.ScriptNames.Count, "the Vayne closure is 25 scripts (audit bb-coverage)");
        foreach (var name in new[] { "CharScriptVayne", "VayneTumble", "VayneTumbleBonus", "VayneTumbleAttack", "VayneTumbleUltAttack", "VayneTumbleFade",
                     "VayneSilveredBolts", "VayneSilveredDebuff", "VayneSilverParticle1", "VayneCondemn", "VayneCondemnMissile", "VayneInquisition",
                     "VayneInquisitionSpeedPart", "VayneInquisitionSpeedPartNormal", "VayneHunted" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var hero = new Entity { ChampionName = "Vayne" };
        TestAssert.Equal(["VayneTumble", "VayneSilveredBolts", "VayneCondemn", "VayneInquisition"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Vayne.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Vayne");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 25 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Vayne hook runs is bound: " + string.Join(", ", unbound));
        var extras = GameData.Default.Champion("Vayne").Raw;
        TestAssert.Equal("VayneCondemnMissile", extras[InibinReader.KeyHash("Data*ExtraSpell2")].AsString(), "ExtraSpell2 is the Condemn bolt");
        TestAssert.Equal("VayneTumbleAttack", extras[InibinReader.KeyHash("Data*ExtraSpell3")].AsString(), "ExtraSpell3 is the empowered attack");
    }

    [Check("scripting.bb-vayne-production-runs-the-shipped-scripts")]
    public static void ProductionRunsVayneFromHerScripts()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0);
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        TestAssert.True(dispatcher.FindModule("Vayne") is BbChampionModule, "Vayne is a BbChampionModule in production (phase 3, 19 October 2026)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Night Hunter
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-passive-night-hunter-speeds-her-while-she-hunts-a-visible-champion-in-front")]
    public static void NightHunterAddsMovementSpeedWhileSheHuntsAChampionInFront()
    {
        var script = CharScript("Vayne");
        TestAssert.Near(40.0, Literal(script, "UpdateSelfBuffStats", "SpeedBoost"), 1e-12, "CharScriptVayne: +40 movement speed");
        TestAssert.Near(3.0, Blocks(script, "UpdateSelfBuffStats").First(item => item.Function == "BBMath" && item.Text("DestVar") == "SpeedBoost")
            .Number("Src2Value")!.Value, 1e-12, "Final Hour multiplies the bonus by 3");
        using var match = new Match("Vayne");
        match.Learn(3);
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7300, 7000);
        match.Advance(0.3);
        TestAssert.Near(40.0, match.Hero.FlatMovementSpeedMod, 1e-9, "hunting a champion in front: +40 flat movement speed");
        TestAssert.True(Of(match.Hero, "VayneInquisitionSpeedPartNormal") is not null, "the normal speed part is on her");
        TestAssert.True(Of(match.Hero, "VayneInquisitionSpeedPart") is null, "the Final Hour speed part is not");
        var marker = Of(match.Foe, "VayneHunted");
        TestAssert.True(marker is { Type: BuffType.Internal } && marker.CasterNetId == match.Hero.NetId, "the hunted marker is hers, on the champion");
        TestAssert.Near(1.5, marker!.ExpiresAt, 0.1, "the marker lasts 1.5 s");

        match.Foe.Position = new Position2(6700, 7000);
        match.Advance(2.0);
        TestAssert.Near(0.0, match.Hero.FlatMovementSpeedMod, 1e-9, "a champion behind her is not hunted");
        TestAssert.True(Of(match.Hero, "VayneInquisitionSpeedPartNormal") is null && Of(match.Foe, "VayneHunted") is null, "the buffs are gone");

        match.Foe.Position = new Position2(7300, 7000);
        match.Refill();
        TestAssert.True(match.Cast(3), "Final Hour");
        match.Advance(0.3);
        TestAssert.Near(120.0, match.Hero.FlatMovementSpeedMod, 1e-9, "Final Hour triples the bonus");
        TestAssert.True(Of(match.Hero, "VayneInquisitionSpeedPart") is not null && Of(match.Hero, "VayneInquisitionSpeedPartNormal") is null,
            "the Final Hour speed part replaces the normal one");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    /// <summary>
    /// W is learned the way the game does it: <c>CharOnLevelUpSpell</c> adds the aura only when W's level reads 1 (the first rank), so the rank-up
    /// runs at rank 1 and the rest of the skills come after it (rank 3 first would make the script see level 3 and add nothing).
    /// </summary>
    private static void LearnWithAura(Match match)
    {
        match.Hero.SpellRanks[1] = 1;
        match.Module.OnRankUp(match.Runtime.Spells, match.Hero, 1, 1);
        match.Learn(3);
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: sealed at creation, the aura on the first rank, the three-hit mark
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-w-is-sealed-at-creation-and-the-aura-follows-the-first-rank-through-death")]
    public static void SilverBoltsIsASealedSlotWithAnAuraThatSurvivesDeath()
    {
        using var match = new Match("Vayne");
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 1), "CharOnActivate seals the W slot");
        TestAssert.False(match.Hero.SpellSeals.IsSealed(false, 0) || match.Hero.SpellSeals.IsSealed(false, 2) || match.Hero.SpellSeals.IsSealed(false, 3),
            "the other slots are open");
        TestAssert.True(Of(match.Hero, "VayneSilveredBolts") is null, "no aura before W is learned");
        match.Hero.SpellRanks[1] = 1;
        match.Module.OnRankUp(match.Runtime.Spells, match.Hero, 1, 1);
        TestAssert.True(Of(match.Hero, "VayneSilveredBolts") is { Type: BuffType.Aura }, "the first W rank adds the aura (CharOnLevelUpSpell)");
        match.Learn(3);
        match.Refill();
        TestAssert.False(match.Cast(1), "W is not a cast: the slot is sealed");
        TestAssert.True(match.Cast(0, at: new Position2(7000, 7600)), "Q");
        match.Advance(0.5);
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "Tumble seals its own slot while the bonus is up");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(0.2);
        TestAssert.True(Of(match.Hero, "VayneSilveredBolts") is not null, "the aura persists through death");
        TestAssert.True(Of(match.Hero, "VayneTumbleBonus") is null, "the Tumble bonus does not");
        TestAssert.False(match.Hero.SpellSeals.IsSealed(false, 0), "its deactivation opens the Q slot again");
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 1), "W stays sealed");
        TestAssert.True(match.Hero.ScriptAttackOverride is null, "and the override attack is gone");
    }

    [Check("scripting.bb-cast-seal-deceive-death-stays-sealed-until-respawn")]
    public static void DeceiveSealHoldsThroughADeathInItsFadeUntilRespawn()
    {
        // Shaco's Q (Deceive): the cast seals the slot and puts DeceiveFade up; Deceive is added when the fade ends, and the corpse
        // refuses that add. Nothing opens the slot at death; the respawn release does (after CharOnResurrect, for a champion whose script is silent).
        using var match = new Match("Shaco");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0), "Q (Deceive)");
        match.Advance(0.02);
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "the cast seals the Q slot");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        match.Advance(0.2);
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "the Q slot stays sealed while the hero is dead");
        for (var second = 0; second < 120 && !match.Hero.Alive; second++)
            match.Advance(1.0);
        TestAssert.True(match.Hero.Alive, "the hero respawned");
        match.Advance(0.5);
        TestAssert.False(match.Hero.SpellSeals.IsSealed(false, 0), "the respawn releases the Q slot");
    }

    [Check("scripting.bb-vayne-w-third-hit-deals-true-damage-by-the-targets-health")]
    public static void SilverBoltsMarksThreeHitsAndTheThirdDealsTrueDamage()
    {
        var damage = GameData.Default.Spell("VayneSilveredBolts", 5);
        TestAssert.Equal([4.0, 5.0, 6.0, 7.0, 8.0], Enumerable.Range(1, 5).Select(rank => damage.EffectNumber(1, rank)).ToArray(), "inibin Effect1: percent of maximum health");
        TestAssert.Equal([20.0, 30.0, 40.0, 50.0, 60.0], Enumerable.Range(1, 5).Select(rank => damage.EffectNumber(2, rank)).ToArray(), "inibin Effect2: flat");
        TestAssert.Equal([0.04, 0.05, 0.06, 0.07, 0.08], ByLevel("VayneSilveredDebuff", "OnBuffActivate", "RankScaling"), "VayneSilveredDebuff.luaobj RankScaling");
        TestAssert.Equal([20.0, 30.0, 40.0, 50.0, 60.0], ByLevel("VayneSilveredDebuff", "OnBuffActivate", "FlatScaling"), "VayneSilveredDebuff.luaobj FlatScaling");

        using var match = new Match("Vayne");
        LearnWithAura(match);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        var stacks = new List<int>();
        var swings = new List<List<(double At, double Dealt, double Raw, string Kind)>>();
        for (var swing = 1; swing <= 3; swing++)
        {
            match.Hero.AttackCooldown = 0.0;
            var before = HitsOn(match, "Foe").Count;
            var maximum = match.Foe.MaxHealth;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), $"swing {swing}");
            match.Advance(1.0);
            stacks.Add(Of(match.Foe, "VayneSilveredDebuff")?.Stacks ?? 0);
            swings.Add(HitsOn(match, "Foe").Skip(before).ToList());
            if (swing == 3)
            {
                var truth = swings[2].Where(item => item.Kind == "Spell").ToList();
                TestAssert.Equal(1, truth.Count, "the third hit adds one true damage hit");
                NearF32(maximum * 0.06 + 40.0, truth[0].Raw, "rank 3: 6 percent of maximum health + 40");
            }
        }
        TestAssert.Equal([1, 2, 0], stacks.ToArray(), "the mark has one stack after the first hit, two after the second and is spent by the third");
        TestAssert.True(Of(match.Foe, "VayneSilverParticle1") is null, "the ring is gone with the mark");
        // W28 (batch 95) runs the hit hooks before the damage and carries DamageAmount back: BuffOnHitUnit re-applies the swing as one DAMAGESOURCE_PROC hit
        // of its whole amount and zeroes the attack, so the physical part of the third swing is a PROC (Exhaust 0.65, no life steal) and the attack deals nothing.
        var attack = match.Hero.AttackDamage;
        var third = swings[2];
        TestAssert.Equal(1, third.Count(item => item.Kind == "Proc"), "the third swing's physical damage is one PROC hit");
        NearF32(attack, third.Single(item => item.Kind == "Proc").Raw, "of the whole swing");
        TestAssert.Near(0.0, third.Where(item => item.Kind == "Basic").Sum(item => item.Raw), 1e-6, "and the attack itself deals nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q: Tumble
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-q-tumble-dashes-300-seals-q-and-arms-the-bonus")]
    public static void TumbleDashesSealsAndArmsTheEmpoweredAttack()
    {
        var tumble = Script("VayneTumble");
        TestAssert.Near(900.0, Literal(tumble, "SelfExecute", "DashSpeed", "NextBuffVars"), 1e-12, "VayneTumble.luaobj: the dash speed is 900");
        TestAssert.Near(300.0, Literal(tumble, "SelfExecute", "Distance", "NextBuffVars"), 1e-12, "VayneTumble.luaobj: the dash is 300 units");
        TestAssert.Near(6.75, Blocks(tumble, "SelfExecute").First(item => item.Function == "BBSpellBuffAdd" && item.Text("BuffName") == "VayneTumbleBonus")
            .Number("Duration")!.Value, 1e-12, "the bonus lasts 6.75 s");
        TestAssert.Equal([6.0, 5.0, 4.0, 3.0, 2.0], ByLevel("VayneTumbleBonus", "OnBuffDeactivate", "SpellCooldown"), "VayneTumbleBonus.luaobj: the cooldown starts when the bonus ends");
        TestAssert.Equal([0.4, 0.45, 0.5, 0.55, 0.6], ByLevel("VayneTumbleAttack", "TargetExecute", "scalingDamage"), "VayneTumbleAttack.luaobj: the ratio of total attack damage");

        using var match = new Match("Vayne");
        match.Learn(3);
        match.Refill();
        var start = match.Hero.Position;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: new Position2(7000, 7600)), "Tumble toward the north");
        TestAssert.Near(mana - 40.0, match.Hero.Mana, 1.0, "40 mana at every rank");
        match.Advance(0.1);
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "the cast seals the Q slot");
        TestAssert.True(Of(match.Hero, "VayneTumble") is not null && Of(match.Hero, "VayneTumbleBonus") is not null, "the dash buff and the bonus");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "the cooldown is zero while the bonus is up");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Payload.MovementDisabled) && match.Hero.Buffs.Any(buff => buff.Payload.Disarmed),
            "she cannot walk or attack during the dash");
        match.Advance(0.6);
        TestAssert.Near(300.0, Between(start, match.Hero.Position), 2.0, "she moved 300 units");
        TestAssert.True(match.Hero.ForcedMove is null && Of(match.Hero, "VayneTumble") is null, "the dash is over and its buff gone");
        TestAssert.True(match.Hero.ScriptAttackOverride is { SpellName: "VayneTumbleAttack", Level: 1, Slot: 44 }, "her attack is ExtraSpell3");
        TestAssert.False(match.Cast(0, at: new Position2(7000, 7600)), "Q cannot be cast again while the bonus is up");
        match.Advance(6.2);
        TestAssert.True(Of(match.Hero, "VayneTumbleBonus") is null && !match.Hero.SpellSeals.IsSealed(false, 0), "after 6.75 s the bonus is gone and the slot open");
        TestAssert.True(match.Hero.SpellCooldowns[0] is > 3.5 and <= 4.0, $"the rank 3 cooldown started when the bonus ended: {match.Hero.SpellCooldowns[0]}");
        TestAssert.True(match.Hero.ScriptAttackOverride is null && match.Hero.ScriptAnimationOverrides.Count == 0, "the attack and the stance are back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    [Check("scripting.bb-vayne-q-empowered-attack-adds-the-ratio-of-total-attack-damage-once")]
    public static void TheEmpoweredAttackDealsTheScriptsAmountAndConsumesTheBonus()
    {
        using var match = new Match("Vayne");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a plain attack");
        match.Advance(1.2);
        var plain = HitsOn(match, "Foe").Single(item => item.Kind == "Basic");
        TestAssert.Near(match.Hero.AttackDamage, plain.Raw, 1e-6, "a plain attack deals total attack damage");
        foreach (var mark in match.Foe.Buffs.Where(buff => buff.Name.StartsWith("VayneSilver", StringComparison.Ordinal)).ToArray())
            match.Runtime.Buffs.RemoveBuff(match.Foe, mark.Slot);

        TestAssert.True(match.Cast(0, at: new Position2(7000, 7600)), "Tumble north, which keeps the target within reach");
        match.Advance(0.6);
        var before = HitsOn(match, "Foe").Count;
        var attack = match.Hero.AttackDamage;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the empowered attack");
        match.Advance(1.2);
        var hit = HitsOn(match, "Foe").Skip(before).Single(item => item.Kind == "Basic");
        TestAssert.Near(attack * 1.5, hit.Raw, 1e-6, "VayneTumbleAttack: total attack damage x (1 + 0.5) at rank 3, dealt as one hit");
        TestAssert.True(Of(match.Hero, "VayneTumbleBonus") is null && !match.Hero.SpellSeals.IsSealed(false, 0), "the hit consumes the bonus and opens the slot");
        TestAssert.True(match.Hero.SpellCooldowns[0] is > 2.5 and <= 4.0, $"and the cooldown starts: {match.Hero.SpellCooldowns[0]}");
        TestAssert.True(match.Log.EffectCalls.Any(call => call.Name.Equals("vayne_Q_tar.troy", StringComparison.OrdinalIgnoreCase) &&
                                                          (call.Bind == match.Foe.NetId || call.Target == match.Foe.NetId)),
            "vayne_Q_tar is drawn on the target");
        TestAssert.True(match.Hero.ScriptAttackOverride is null, "the override attack is gone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Condemn
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-e-condemn-hits-then-pushes-475-and-marks-the-target")]
    public static void CondemnHitsPushesAndAddsAStack()
    {
        var missile = Script("VayneCondemnMissile");
        TestAssert.Equal([45.0, 80.0, 115.0, 150.0, 185.0], ByLevel("VayneCondemnMissile", "TargetExecute", "Damage"), "VayneCondemnMissile.luaobj: damage by rank");
        TestAssert.Near(0.5, Blocks(missile, "TargetExecute").First(item => item.Function == "BBMath" && item.Text("DestVar") == "BonusDamage").Number("Src2Value")!.Value,
            1e-12, "half of the bonus attack damage");
        TestAssert.Near(475.0, Blocks(missile, "OnBuffActivate").First(item => item.Function == "BBMath" && item.Text("DestVar") == "PushBack").Number("Src2Value")!.Value,
            1e-12, "the push is 475");
        TestAssert.Near(1.5, Blocks(missile, "OnBuffDeactivate").First(item => item.Function == "BBApplyStun").Number("Duration")!.Value, 1e-12, "the wall stun is 1.5 s");

        using var match = new Match("Vayne");
        LearnWithAura(match);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        var start = match.Foe.Position;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Foe), "Condemn");
        var started = match.Now;
        match.Advance(1.0);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; regeneration allowed.
        TestAssert.Near(mana - 90.0, match.Hero.Mana, 1.5, "90 mana");
        TestAssert.Near(16.0, match.Hero.SpellCooldowns[2] + 1.0 - match.CastFrameOf(2), 0.1, "rank 3 cooldown 16 s");
        var hits = HitsOn(match, "Foe");
        var hit = hits.Single(item => item.Kind == "Spell");
        NearF32(115.0 + 0.5 * BonusAttackDamage(match), hit.Raw, "rank 3: 115 + 0.5 of the bonus attack damage");
        TestAssert.True(hit.At - started is > 0.3 and < 0.5, $"the bolt leaves when the quarter-second windup ends and flies 300 units at 2200: {hit.At - started}");
        TestAssert.Near(475.0, Between(start, match.Foe.Position), 3.0, "pushed 475 units away");
        TestAssert.False(match.Foe.Buffs.Any(buff => buff.Name == "Stun"), "no stun without a wall");
        TestAssert.Equal(1, Of(match.Foe, "VayneSilveredDebuff")?.Stacks ?? 0, "Condemn adds a Silver Bolts stack");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    [Check("scripting.bb-vayne-e-condemn-into-a-wall-stuns-and-hurts-again")]
    public static void CondemnAgainstAWallStunsForOneAndAHalfSeconds()
    {
        using var match = new Match("Vayne");
        match.Learn(3);
        match.Refill();
        var navigation = match.Runtime.Navigation;
        (Position2 From, Position2 To)? shot = null;
        for (var radius = 0.0; radius <= 3000.0 && shot is null; radius += 150.0)
            for (var step = 0; step < 16 && shot is null; step++)
            {
                var angle = step * Math.PI / 8.0;
                var target = new Position2(7000 + Math.Cos(angle) * radius, 7000 + Math.Sin(angle) * radius);
                if (!navigation.IsMovementWalkable(target) || navigation.PointClearance(target) < 60.0) continue;
                for (var ray = 0; ray < 16 && shot is null; ray++)
                {
                    var direction = new Position2(Math.Cos(ray * Math.PI / 8.0), Math.Sin(ray * Math.PI / 8.0));
                    var from = new Position2(target.X - direction.X * 350.0, target.Z - direction.Z * 350.0);
                    if (!navigation.IsMovementWalkable(from) || navigation.PointClearance(from) < 60.0) continue;
                    for (var distance = 25.0; distance <= 475.0; distance += 25.0)
                    {
                        if (navigation.IsMovementWalkable(new Position2(target.X + direction.X * distance, target.Z + direction.Z * distance))) continue;
                        if (distance is >= 100.0 and <= 400.0) shot = (from, target);
                        break;
                    }
                }
            }
        TestAssert.True(shot is not null, "the map has a wall 100 to 400 units behind a standing point");
        match.Hero.Position = shot!.Value.From;
        match.Foe.Position = shot.Value.To;
        var start = match.Foe.Position;
        TestAssert.True(match.Cast(2, match.Foe), "Condemn");
        match.Advance(1.0);
        TestAssert.True(Between(start, match.Foe.Position) < 475.0 - 25.0, "stopped short by the wall");
        TestAssert.True(Of(match.Foe, "Stun") is { } stun && stun.CasterNetId == match.Hero.NetId, "the shipped global Stun (BBApplyStun 1.5 s)");
        TestAssert.True(Of(match.Foe, "Stun")!.ExpiresAt is > 0.3 and <= 1.5, $"for 1.5 s from the wall: {Of(match.Foe, "Stun")!.ExpiresAt}");
        var spell = HitsOn(match, "Foe").Where(item => item.Kind == "Spell").ToList();
        TestAssert.Equal(2, spell.Count, "the hit and the wall damage");
        NearF32(spell[0].Raw, spell[1].Raw, "the wall repeats the recorded damage");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: Final Hour
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-r-final-hour-adds-attack-damage-and-hides-her-after-tumble")]
    public static void FinalHourAddsAttackDamageAndStealthOnTumble()
    {
        TestAssert.Equal([25.0, 40.0, 55.0], ByLevel("VayneInquisition", "TargetExecute", "ADMod"), "VayneInquisition.luaobj: attack damage by rank");
        TestAssert.Equal([8.0, 10.0, 12.0], Table(Blocks(Script("VayneInquisition"), "TargetExecute").First(item => item.Function == "BBSpellBuffAdd"), "DurationByLevel"),
            "VayneInquisition.luaobj: duration by rank");
        TestAssert.Near(1.5, Blocks(Script("VayneTumble"), "SelfExecute").First(item => item.Function == "BBSpellBuffAdd" && item.Text("BuffName") == "VayneTumbleFade")
            .Number("Duration")!.Value, 1e-12, "the stealth lasts 1.5 s");

        using var match = new Match("Vayne");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        var attack = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(3), "Final Hour");
        match.Advance(0.3);
        TestAssert.Near(attack + 55.0, match.Hero.AttackDamage, 1e-6, "+55 attack damage at rank 3");
        TestAssert.Near(12.0, Of(match.Hero, "VayneInquisition")!.ExpiresAt, 0.4, "for 12 s");
        TestAssert.True(match.Hero.ScriptAttackOverride is { SpellName: "VayneUltAttack", Slot: 46 }, "her attack is ExtraSpell5 (the animation of the ultimate)");

        match.Refill();
        TestAssert.True(match.Cast(0, at: new Position2(7000, 7600)), "Tumble");
        match.Advance(0.6);
        TestAssert.True(Of(match.Hero, "VayneTumbleFade") is { Type: BuffType.CombatEnhancer } fade && fade.ExpiresAt is > 0.7 and <= 1.5, "Tumble under Final Hour hides her for 1.5 s");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Payload.Stealthed), "stealthed");
        TestAssert.True(match.Hero.ScriptAttackOverride is { SpellName: "VayneTumbleUltAttack" }, "the empowered attack of the ultimate");
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack");
        match.Advance(1.2);
        TestAssert.True(Of(match.Hero, "VayneTumbleFade") is null && Of(match.Hero, "VayneTumbleBonus") is null, "the hit ends the stealth and the bonus");
        TestAssert.True(match.Hero.ScriptAttackOverride is { SpellName: "VayneUltAttack" }, "and she attacks as Final Hour again");
        match.Advance(12.0);
        TestAssert.True(Of(match.Hero, "VayneInquisition") is null && match.Hero.ScriptAttackOverride is null, "Final Hour ends and the attack with it");
        TestAssert.Near(attack, match.Hero.AttackDamage, 1e-6, "attack damage is back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The tooltip numbers of her character script
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vayne-character-script-publishes-the-q-and-e-tooltip-numbers")]
    public static void TheCharacterScriptWritesTheSpellTooltipVariables()
    {
        using var match = new Match("Vayne");
        match.Learn(3);
        match.Advance(1.2);
        TestAssert.True(match.Hero.ScriptSpellTooltips.TryGetValue(0, out var q) && q.Length >= 1, "a tooltip for Q");
        NearF32(match.Hero.AttackDamage * 0.5, q![0], "Q: total attack damage x the rank ratio (0.5 at rank 3)");
        TestAssert.True(match.Hero.ScriptSpellTooltips.TryGetValue(2, out var e) && e.Length >= 1, "a tooltip for E");
        NearF32(0.5 * BonusAttackDamage(match), e![0], "E: half of the bonus attack damage");
    }
}
