using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 258 (26 October 2026, waves 7 and 8): Karma plays from her shipped scripts (<c>CharScriptKarma</c>, the four spell scripts and their children, the
/// buffs). The numbers are the literals of Karma.inibin (<c>Data*MaxLevels</c> 60/60/60/10 read as byte/10, <c>Data*SpellsUpLevels1..4</c>) and of the
/// bytecode listings of the scripts, the scenario <c>S-karma</c> shows the probes on a headless match, and Inner Flame is
/// <c>KarmaTranscendence.luaobj</c> (its stat script reads the missing-health percentage). Written 26 October 2026.
/// </summary>
public static class BbKarmaChecks
{
    [Check("scripting.bb-karma-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Karma");
        foreach (var name in new[] { "CharScriptKarma", "KarmaHeavenlyWave", "KarmaSpiritBond", "KarmaSoulShield", "KarmaChakra", "KarmaTranscendence",
                     "KarmaChakraCharge", "KarmaHeavenlyWaveC", "KarmaSpiritBondC", "KarmaSoulShieldC", "KarmaMantraSBSlow" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Karma");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");

        var hero = new Entity { ChampionName = "Karma" };
        TestAssert.Equal(["KarmaHeavenlyWave", "KarmaSpiritBond", "KarmaSoulShield", "KarmaChakra"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Karma's Data*Spell1..4");
        TestAssert.Equal([6, 6, 6, 1], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "Q, W and E have six ranks and Mantra one (Data*MaxLevels 60/60/60/10)");
        TestAssert.Equal([1, 3, 5, 7, 9, 11], Enumerable.Range(1, 6).Select(rank => module.RankGate(hero, 0, rank)).ToArray(),
            "Q is learnt at 1, 3, 5, 7, 9 and 11 (Data*SpellsUpLevels1)");
        TestAssert.Equal(1, module.RankGate(hero, 3, 1), "Mantra is learnt at level one (Data*SpellsUpLevels4)");
        TestAssert.True(module.RankGate(hero, 3, 2) >= 95, "Mantra takes no second rank (the row reads 95 and up for rank two)");

        var info = ChampionCatalog.ByName("Karma")!;
        TestAssert.True(info is { Playable: true, Role: "support", DefaultIp: 3150, Id: 43, RetailBot: false },
            "playable as a support for 3150 IP (store-prices-2011.json id 43), not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Karma"), "listed once");
    }

    /// <summary>
    /// KarmaTranscendence.luaobj (the passive, Data*Passive1LuaName KarmaPassive), BuffOnUpdateStats: PercentMissing = 1 - GetHealthPercent, then APGain =
    /// PercentMissing x 30 / 50 / 70 / 90 / 110 / 130 by the BBIf/BBElseIf chain on CharLevel (CO_LESS_THAN 3 / 6 / 9 / 12 / 15, else), and
    /// BBIncStat IncFlatMagicDamageMod APGain. Two health levels at level 18 and one at level 1 (the first branch).
    /// </summary>
    [Check("scripting.bb-karma-inner-flame-adds-ability-power-from-missing-health")]
    public static void InnerFlameAddsAbilityPowerFromMissingHealth()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            void At(int level, double health, double perMissing)
            {
                match.Hero.Level = level;
                match.Runtime.HeroStats.Refresh(match.Hero, push: false);
                match.Hero.Health = match.Hero.MaxHealth * health;
                match.Advance(0.5);
                var missing = 1.0 - match.Hero.Health / match.Hero.MaxHealth;
                TestAssert.Near(missing * perMissing, match.Hero.AbilityPower, 0.01,
                    $"level {level} at {health:P0} health: {perMissing} x the missing share ({missing:0.####})");
            }
            match.Refill();
            match.Advance(0.5);
            TestAssert.Equal(0.0, match.Hero.AbilityPower, "at full health Inner Flame adds nothing");
            At(1, 0.5, 30.0);
            At(18, 0.5, 130.0);
            At(18, 0.2, 130.0);
            TestAssert.True(match.Hero.AbilityPower > 100.0, $"level 18 at 20% health: about 104 ability power ({match.Hero.AbilityPower:0.##})");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// KarmaHeavenlyWave.luaobj: SelfExecute only spends the Mantra energy (BBIfHasBuff KarmaChakra -> BBSpellBuffRemove); TargetExecute deals DamageByLevel
    /// (70 at rank one) with SpellDamageRatio 0.6, magic, to each unit the cone selects. The cone is requested the way the client and the bots send it
    /// (BotAim.Cone, SeatOrders.CastToward): from the caster's own position toward the aim. This check's first form sent a unit-form request with the cast
    /// point on the foe 150 units away; the cone rule (BbChampionModule.Validate: a cone's cast point within 75 units of the caster) refused it for range,
    /// SpellSystem queued it as a walk to that point, the hero stopped about 78 units short against the foe and the cast never ran (0 damage).
    /// </summary>
    [Check("scripting.bb-karma-heavenly-wave-base-cast-deals-the-shipped-damage")]
    public static void HeavenlyWaveBaseCastDealsTheShippedDamage()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.GiveAbilityPower();
            match.Ally.Position = new Position2(7000, 7600);
            match.Foe.Position = new Position2(7150, 7000);
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            var from = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
            var aim = new Position3(match.Foe.Position.X, 0, match.Foe.Position.Z);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(0, false, from, aim, 0), match.Now),
                "the base Heavenly Wave casts toward the foe 150 units in front");
            match.Advance(1.0);
            var hits = HitsOn(match, "Foe");
            TestAssert.Equal(1, hits.Count, "one hit on the foe");
            NearF32(70.0 + 0.6 * match.Hero.AbilityPower, hits[0].Raw, "DamageByLevel 70 + SpellDamageRatio 0.6 x ability power");
            TestAssert.Near(Mitigated(hits[0].Raw, 30.0), hits[0].Dealt, 1e-6, "magic damage, mitigated by the foe's 30 magic resist");
            TestAssert.Equal(0, HitsOn(match, "Ally").Count, "the base cone affects enemies only (SpellData*Flags 0x1C400)");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// KarmaHeavenlyWaveC.luaobj (the Q while Mantra stands): TargetExecute heals a unit of Karma's team by BaseHealthRestore (35 at rank one) plus its
    /// missing health x (0.05 + 0.0002 x Karma's ability power), and damages an enemy as the base wave does. Its SpellData*Flags 0x5CC00 admit friends
    /// (AffectFriends) and Karma herself (AlwaysSelf), so the cone's units follow the spell's flags, as an area's do (BbSpellTargeting.Area); the engine's
    /// cone took enemies only, and the empowered wave healed nobody (S-karma: the ally gained 0.63, its own regeneration).
    /// </summary>
    [Check("scripting.bb-karma-heavenly-wave-mantra-heals-allies-in-the-cone")]
    public static void HeavenlyWaveMantraHealsAlliesInTheCone()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.GiveAbilityPower();
            match.Ally.Position = new Position2(7100, 7020);
            match.Foe.Position = new Position2(7250, 7000);
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            TestAssert.True(match.Cast(3, match.Hero), "Mantra");
            match.Advance(0.3);
            match.Refill();
            TestAssert.Equal("KarmaHeavenlyWaveC", match.Module.SpellName(match.Hero, 0), "Mantra swaps Q to KarmaHeavenlyWaveC");
            match.Ally.Health = match.Ally.MaxHealth - 1000.0;
            var before = match.Ally.Health;
            var power = match.Hero.AbilityPower;
            var from = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
            var aim = new Position3(match.Foe.Position.X, 0, match.Foe.Position.Z);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(0, false, from, aim, 0), match.Now),
                "the empowered Heavenly Wave casts toward the foe, the ally inside the cone");
            match.Advance(1.0);
            var hits = HitsOn(match, "Foe");
            TestAssert.Equal(1, hits.Count, "the foe is hit once");
            NearF32(70.0 + 0.6 * power, hits[0].Raw, "the enemy takes the base wave's damage");
            var expected = 35.0 + 1000.0 * (0.05 + 0.0002 * power);
            TestAssert.Near(expected, match.Ally.Health - before, 2.0,
                $"the ally heals 35 + 1000 missing x (0.05 + 0.0002 x {power:0.#} AP) = {expected:0.##} (got {match.Ally.Health - before:0.##}, regeneration within 2)");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// Mantra's charges (CharScriptKarma CharOnActivate, KarmaChakra, KarmaChakraCharge): the AmmoStack buff KarmaChakraCharge (STACKS_AND_RENEWS, MaxStack 3)
    /// starts with NumberOfStacks 2 and the duration MantraTimerCooldown, which CharOnActivate sets to 25 just before; its BuffOnUpdateAmmo adds a stack when
    /// the duration runs out (30 s at levels 1 to 6 by then: UpdateSelfBuffActions, SrcValueByLevel x (1 + CooldownMod)). KarmaChakra's CanCast is
    /// BBGetBuffCountFromAll(KarmaChakraCharge) > 1 and its SelfExecute removes one stack, so the buff keeps one base stack and Mantra holds count - 1
    /// charges: one at the start, two at most (the tooltip's "up to 2 charges"; MaxStack 3 counts the base stack). With no charge left the CanCast refuses.
    /// </summary>
    [Check("scripting.bb-karma-mantra-charges-follow-the-script")]
    public static void MantraChargesFollowTheScript()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            int Stacks() => Buff(match.Hero, "KarmaChakraCharge")?.Stacks ?? 0;
            void Until(double time) => match.Advance(time - match.Now);
            match.Advance(0.5);
            TestAssert.Equal(2, Stacks(), "one charge at the start (two stacks: the base stack and the charge)");
            TestAssert.True(match.Cast(3, match.Hero), "Mantra casts with one charge");
            match.Advance(0.3);
            TestAssert.Equal(1, Stacks(), "the cast spends the charge; the base stack stays");
            TestAssert.True(Buff(match.Hero, "KarmaChakra") is not null, "Mantra energizes the next ability (KarmaChakra)");
            TestAssert.Equal("KarmaHeavenlyWaveC", match.Module.SpellName(match.Hero, 0), "KarmaChakra's OnBuffActivate swaps Q to KarmaHeavenlyWaveC");
            TestAssert.Equal(0.0, match.Hero.SpellCooldowns[3], "Mantra's cooldown is 0 while KarmaChakraCharge stands (BBSetSlotSpellCooldownTimeVer2 0)");
            var refused = match.CanCast(3, match.Hero);
            TestAssert.True(refused is { Allowed: false, Failure: SpellCastFailure.Target },
                $"a second Mantra with no charge is refused by KarmaChakra's CanCast ({refused.Reason})");
            TestAssert.False(match.Cast(3, match.Hero), "the second request is refused");
            Until(24.5);
            TestAssert.Equal(1, Stacks(), "no charge yet at 24.5 s");
            Until(25.5);
            TestAssert.Equal(2, Stacks(), "the first reload lands 25 s after the spawn (MantraTimerCooldown 25 at CharOnActivate)");
            TestAssert.True(match.CanCast(3, match.Hero).Allowed, "Mantra casts again with the reloaded charge");
            Until(54.5);
            TestAssert.Equal(2, Stacks(), "the next reload is not in yet at 54.5 s");
            Until(56.0);
            TestAssert.Equal(3, Stacks(), "the second reload lands 30 s later (SrcValueByLevel 30 at level one)");
            Until(100.0);
            TestAssert.Equal(3, Stacks(), "two charges at most (MaxStack 3 with the base stack)");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// KarmaSpiritBond.luaobj TargetExecute on an enemy: NextBuffVars.MoveSpeedMod = -0.1 at rank one (SrcValueByLevel -0.1 .. -0.2), the link buff
    /// KarmaSpiritBondC on the target (its BuffOnUpdateStats hastens Karma by -MoveSpeedMod, IncPercentMovementSpeedMod) and KarmaMantraSBSlow (BUFF_Slow,
    /// 5 s: IncPercentMultiplicativeMovementSpeedMod MoveSpeedMod). KarmaSpiritBondC.luaobj, the slot spell while Mantra stands (KarmaChakra's BBSetSpell),
    /// does the same with -0.2 .. -0.4: the doubled modifier the tooltip names.
    /// </summary>
    [Check("scripting.bb-karma-spirit-bond-mantra-doubles-the-speed-modifier")]
    public static void SpiritBondMantraDoublesTheSpeedModifier()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Ally.Position = new Position2(7000, 7600);
            match.Refill();
            match.Advance(0.5);
            var foeSpeed = match.Foe.Speed;
            var karmaSpeed = match.Hero.Speed;
            TestAssert.True(match.Cast(1, match.Foe), "the base Spirit Bond casts at the foe");
            match.Advance(0.6);
            TestAssert.True(Buff(match.Foe, "KarmaMantraSBSlow") is not null && Buff(match.Foe, "KarmaSpiritBondC") is not null,
                $"the foe carries the link and the slow ({Names(match.Foe)})");
            TestAssert.Near(0.9, match.Foe.Speed / foeSpeed, 1e-3, $"rank one slows by 10% ({foeSpeed:0.##} -> {match.Foe.Speed:0.##})");
            TestAssert.Near(1.1, match.Hero.Speed / karmaSpeed, 1e-3, $"Karma gains 10% ({karmaSpeed:0.##} -> {match.Hero.Speed:0.##})");
            match.Advance(5.5);
            TestAssert.Near(foeSpeed, match.Foe.Speed, 1e-6, "the slow ends after 5 s");

            match.Refill();
            TestAssert.True(match.Cast(3, match.Hero), "Mantra");
            match.Advance(0.3);
            match.Refill();
            TestAssert.Equal("KarmaSpiritBondC", match.Module.SpellName(match.Hero, 1), "Mantra swaps W to KarmaSpiritBondC");
            TestAssert.True(match.Cast(1, match.Foe), "the empowered Spirit Bond casts at the foe");
            match.Advance(0.6);
            TestAssert.Near(0.8, match.Foe.Speed / foeSpeed, 1e-3, $"with Mantra the slow doubles to 20% ({match.Foe.Speed:0.##})");
            TestAssert.Near(1.2, match.Hero.Speed / karmaSpeed, 1e-3, $"and Karma's haste to 20% ({match.Hero.Speed:0.##})");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// KarmaSoulShield.luaobj TargetExecute: the shield only (the KarmaSoulShield buff, 80 .. 280 + 0.8 AP, 5 s; no damage block in the script).
    /// KarmaSoulShieldC.luaobj, the slot spell while Mantra stands: the same shield, then at once BBForEachUnitInTargetArea around the target (Range 500,
    /// AffectEnemies AffectNeutral AffectMinions AffectHeroes) with BBApplyDamage DamageVar ArmorAmount (80 at rank one), SpellDamageRatio 0.8, magic.
    /// The burst lands at the cast, not when the shield pops: neither shield's OnBuffDeactivate deals damage.
    /// </summary>
    [Check("scripting.bb-karma-soul-shield-mantra-burst-damages-enemies-around-the-target")]
    public static void SoulShieldMantraBurstDamagesEnemiesAroundTheTarget()
    {
        var match = new Match("Karma", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.GiveAbilityPower();
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            TestAssert.True(match.Cast(2, match.Ally), "the base Soul Shield casts on the ally");
            match.Advance(0.6);
            TestAssert.True(Buff(match.Ally, "KarmaSoulShield") is not null, $"the ally is shielded ({Names(match.Ally)})");
            match.Advance(5.0);
            TestAssert.True(Buff(match.Ally, "KarmaSoulShield") is null, "the shield ends after 5 s");
            TestAssert.Equal(0, HitsOn(match, "Foe").Count, "the base shield deals no damage, at the cast or at its end");

            match.Refill();
            TestAssert.True(match.Cast(3, match.Hero), "Mantra");
            match.Advance(0.3);
            match.Refill();
            TestAssert.Equal("KarmaSoulShieldC", match.Module.SpellName(match.Hero, 2), "Mantra swaps E to KarmaSoulShieldC");
            var cast = match.Now;
            TestAssert.True(match.Cast(2, match.Ally), "the empowered Soul Shield casts on the ally");
            match.Advance(0.6);
            var hits = HitsOn(match, "Foe");
            TestAssert.Equal(1, hits.Count, "the foe 150 units from the ally is hit once");
            TestAssert.True(hits[0].At - cast < 0.6, $"at the cast ({hits[0].At - cast:0.###} s after the request), with the shield still up");
            NearF32(80.0 + 0.8 * match.Hero.AbilityPower, hits[0].Raw, "ArmorAmount 80 + SpellDamageRatio 0.8 x ability power");
            TestAssert.Near(Mitigated(hits[0].Raw, 30.0), hits[0].Dealt, 1e-6, "magic damage, mitigated by the foe's 30 magic resist");
            // The script's BBSpellBuffAdd names no BuffName, so the shield is the buff of the casting script itself: KarmaSoulShieldC.
            TestAssert.True(Buff(match.Ally, "KarmaSoulShieldC") is not null, $"the ally is shielded as well ({Names(match.Ally)})");
        }
        finally
        {
            match.Dispose();
        }
    }
}
