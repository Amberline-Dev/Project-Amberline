using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Xin Zhao runs from his shipped scripts (phase 3, new champions, wave 2, 19 October 2026, W32c): Tireless Warrior, Battle Cry, Three Talon Strike,
/// Audacious Charge and Crescent Sweep, plus the two engine rules his conversion found (a CastType 0 attack spell has no missile; the hero is the
/// <c>Target</c> of <c>CharOnResurrect</c>). The expected numbers are the scripts' and the inibins' (rank-one values of <c>XenZhaoPuncture</c>,
/// <c>XenZhaoBattleCry</c>, <c>XenZhaoComboTarget</c>/<c>XenZhaoThrust*</c>, <c>XenZhaoSweep</c>, <c>XenZhaoParry</c>); the scenario <c>S-xinzhao</c>
/// reads the same kit at level 11.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Health of the target after each tick: the drops of one fight, as (seconds since the start, amount).
    private static List<(double T, double Damage)> XinFight(Match match, Entity watched, double seconds)
    {
        var drops = new List<(double T, double Damage)>();
        var last = watched.Health;
        for (var tick = 1; tick <= (int)Math.Round(seconds * 30.0); tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            if (watched.Health < last - 1e-6) drops.Add((tick / 30.0, last - watched.Health));
            last = watched.Health;
        }
        return drops;
    }

    private static double XinMitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    // The first rank of a slot the way the player trains it (0x3E), so that CharOnLevelUpSpell runs: the Battle Cry passive is added by it, not by the harness.
    private static void XinTrain(Match match, int slot)
    {
        var hero = match.Hero(0);
        hero.SpellRanks[slot] = 0;
        hero.SkillPoints = 1;
        var request = new byte[6];
        request[0] = 0x3E;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        request[5] = (byte)slot;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), match.Runtime.Dependencies.Gameplay);
        match.Runtime.Delivery.Flush();
        TestAssert.Equal(1, hero.SpellRanks[slot], "the training request raised the slot");
    }

    [Check("scripting.bb-xinzhao-module-binds-the-shipped-closure")]
    public static void XinZhaoClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("XinZhao");
        var needed = new[]
        {
            "CharScriptXinZhao", "XenZhaoPuncture", "XenZhaoBattleCry", "XenZhaoBattleCryPassive", "XenZhaoBattleCryPH", "XenZhaoComboTarget", "XenZhaoComboAuto",
            "XenZhaoComboAutoFinish", "XenZhaoKnockup", "XenZhaoThrust", "XenZhaoThrust2", "XenZhaoThrust3", "XenZhaoSweep", "XenZhaoSweepArmor", "XenZhaoParry", "Slow",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Xin Zhao closure");
        var hero = new Entity { ChampionName = "XinZhao" };
        TestAssert.Equal(["XenZhaoComboTarget", "XenZhaoBattleCry", "XenZhaoSweep", "XenZhaoParry"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from XinZhao.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("XinZhao");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(26, closure.Scripts.Count, "the Xin Zhao closure is 26 scripts");
        // Batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts now; they are skipped here as shared system buffs of the closure.
        var system = new[] { "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta", "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff" };
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !system.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Xin Zhao hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-xinzhao-tireless-warrior-heals-on-every-third-landed-attack")]
    public static void XinZhaoPassiveHealsEveryThirdAttack()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("XinZhao", team);
            var xin = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = xin.Position + new Position2(120, 0);
            xin.Health = 2500;
            TestAssert.True(xin.Buffs.Any(buff => buff.Name == "XenZhaoPuncture"), $"{team}: CharOnActivate adds Tireless Warrior");
            var swings = new List<double>();
            var gains = new List<double>();
            var lastEnemy = enemy.Health;
            var lastXin = xin.Health;
            match.Attack(0, enemy);
            for (var tick = 1; tick <= 240; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                if (enemy.Health < lastEnemy - 1e-6) swings.Add(lastEnemy - enemy.Health);
                if (xin.Health > lastXin + 5.0) gains.Add(xin.Health - lastXin);
                lastEnemy = enemy.Health;
                lastXin = xin.Health;
            }
            TestAssert.True(swings.Count >= 6, $"{team}: the swings land at the cast frame, not after a flight at the unused missile speed: {swings.Count} in 8 s");
            // Level 1: 25 health (XenZhaoPuncture, SrcValueByLevel[Level - 1]); the third and the sixth landed attack heal, the others do not.
            TestAssert.Equal(swings.Count / 3, gains.Count, $"{team}: one heal per three landed attacks");
            foreach (var gain in gains) TestAssert.Near(25.0, gain, 0.5, $"{team}: level 1 heals 25");
        }
    }

    [Check("scripting.bb-xinzhao-battle-cry-doubles-the-passive-and-cuts-cooldowns-per-hit")]
    public static void XinZhaoBattleCryDoublesTheBonusAndCutsCooldowns()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("XinZhao", team);
            var xin = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = xin.Position + new Position2(120, 0);
            XinTrain(match, 1);
            var passive = xin.Buffs.Single(buff => buff.Name == "XenZhaoBattleCryPassive");
            TestAssert.Near(0.2, passive.Payload.AttackSpeedMod, 1e-9, $"{team}: rank 1 passive attack speed 20 percent");
            match.Cast(0, 1, xin);
            match.Runtime.Tick(0.4);
            var active = xin.Buffs.Single(buff => buff.Name == "XenZhaoBattleCry");
            TestAssert.Near(0.2, active.Payload.AttackSpeedMod, 1e-9, $"{team}: the cast adds the same 20 percent again (the tooltip: doubling the passive bonus)");
            TestAssert.True(active.ExpiresAt is > 4.0 and <= 4.7, $"{team}: for five seconds: {active.ExpiresAt}");
            TestAssert.True(xin.Buffs.Any(buff => buff.Name == "XenZhaoBattleCryPassive") && xin.Buffs.Any(buff => buff.Name == "XenZhaoBattleCryPH"),
                $"{team}: the passive stays and the cooldown placeholder is added");
            TestAssert.Near(24.0, xin.SpellCooldowns[1] + 0.4, 0.1, $"{team}: rank 1 cooldown 24 s");
            xin.SpellCooldowns[0] = xin.SpellCooldowns[2] = xin.SpellCooldowns[3] = 20.0;
            // The next landed attack takes one second off Q, E and R (BuffOnHitUnit), never off W.
            match.Attack(0, enemy);
            var firstHit = double.NaN;
            var before = enemy.Health;
            double w0 = xin.SpellCooldowns[1];
            for (var tick = 1; tick <= 60 && double.IsNaN(firstHit); tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                if (enemy.Health < before - 1e-6) firstHit = tick / 30.0;
            }
            TestAssert.False(double.IsNaN(firstHit), $"{team}: the attack lands");
            var elapsed = firstHit + 0.0;
            foreach (var slot in new[] { 0, 2, 3 })
                TestAssert.Near(20.0 - elapsed - 1.0, xin.SpellCooldowns[slot], 0.1, $"{team}: slot {slot} lost the time and one second for the landed attack");
            TestAssert.Near(w0 - elapsed, xin.SpellCooldowns[1], 0.1, $"{team}: W only runs down");
        }
    }

    [Check("scripting.bb-xinzhao-three-talon-strike-replaces-three-attacks-and-knocks-up")]
    public static void XinZhaoThreeTalonStrikeComboAndKnockup()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("XinZhao", team);
            var xin = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = xin.Position + new Position2(120, 0);
            match.Cast(0, 0, xin);
            match.Runtime.Tick(0.3);
            TestAssert.True(xin.Buffs.Any(buff => buff.Name == "XenZhaoComboTarget"), $"{team}: the cast arms the combo");
            TestAssert.True(xin.SpellSeals.IsSealed(false, 0), $"{team}: Q is sealed while the combo runs");
            TestAssert.Near(0.0, xin.SpellCooldowns[0], 1e-9, $"{team}: and its cooldown is held at 0 (BBSetSlotSpellCooldownTime in OnBuffActivate)");
            var attackDamage = xin.AttackDamage;
            var armor = enemy.Armor;
            match.Attack(0, enemy);
            var drops = XinFight(match, enemy, 7.0);
            TestAssert.True(drops.Count >= 4, $"{team}: four hits in seven seconds: {drops.Count}");
            // Rank 1: 15 + total attack damage physical, the first three hits; the fourth is the plain swing.
            for (var hit = 0; hit < 3; hit++)
                TestAssert.Near(XinMitigated(15.0 + attackDamage, armor), drops[hit].Damage, 0.5, $"{team}: hit {hit + 1} is the combo");
            TestAssert.Near(XinMitigated(attackDamage, armor), drops[3].Damage, 0.5, $"{team}: the fourth hit is a plain swing");
            TestAssert.False(xin.SpellSeals.IsSealed(false, 0), $"{team}: Q is open again");
            TestAssert.True(xin.SpellCooldowns[0] is > 3.0 and <= 10.0, $"{team}: the cooldown (10 s) began when the third hit ended the combo: {xin.SpellCooldowns[0]}");
            TestAssert.False(xin.Buffs.Any(buff => buff.Name.StartsWith("XenZhaoCombo", StringComparison.Ordinal)), $"{team}: no combo buff is left");
        }
    }

    [Check("scripting.bb-xinzhao-three-talon-strike-hits-for-twice-the-attack-damage-on-a-critical-swing")]
    public static void XinZhaoComboReadsTheReplacedSwingsRoll()
    {
        using var match = new Match("XinZhao", Team.Order);
        var xin = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = xin.Position + new Position2(120, 0);
        match.Runtime.Buffs.AddBuff(xin, "TestCritChance", BuffType.Internal, 60.0, xin.NetId, new BuffPayload { CritChance = 1.0 }, canMitigateDuration: false);
        match.Runtime.HeroStats.Refresh(xin);
        TestAssert.Near(1.0, xin.CritChance, 1e-9, "the test buff gives a certain critical strike");
        match.Cast(0, 0, xin);
        match.Runtime.Tick(0.3);
        var attackDamage = xin.AttackDamage;
        var armor = enemy.Armor;
        match.Attack(0, enemy);
        var drops = XinFight(match, enemy, 5.0);
        TestAssert.True(drops.Count >= 3, $"three hits: {drops.Count}");
        // XenZhaoThrust: HitResult == HIT_Critical -> 15 + 2 x attack damage (the swing's roll reaches the spell that replaced the swing).
        for (var hit = 0; hit < 3; hit++)
            TestAssert.Near(XinMitigated(15.0 + 2.0 * attackDamage, armor), drops[hit].Damage, 0.6, $"hit {hit + 1} of the combo is a critical hit");
    }

    [Check("scripting.bb-xinzhao-three-talon-strike-third-hit-knocks-the-target-up-for-half-a-second")]
    public static void XinZhaoKnockupIsAHalfSecondStunWithAHop()
    {
        using var match = new Match("XinZhao", Team.Order);
        var xin = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = xin.Position + new Position2(120, 0);
        match.Cast(0, 0, xin);
        match.Runtime.Tick(0.3);
        match.Attack(0, enemy);
        var origin = enemy.Position;
        BuffState? seen = null;
        for (var tick = 1; tick <= 150 && seen is null; tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            seen = enemy.Buffs.FirstOrDefault(buff => buff.Name == "XenZhaoKnockup");
        }
        TestAssert.True(seen is not null, "the third hit adds XenZhaoKnockup");
        TestAssert.True(seen!.ExpiresAt is > 0.3 and <= 0.5, $"for half a second: {seen.ExpiresAt}");
        TestAssert.True(seen.Type == BuffType.Stun || enemy.Buffs.Any(buff => buff.Type == BuffType.Stun), "a stun");
        TestAssert.True(enemy.ForcedMove is not null, "the target hops (BBMove of 80 units at speed 100)");
        match.Runtime.Tick(0.7);
        TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "XenZhaoKnockup"), "over after half a second");
        TestAssert.Near(80.0, Math.Sqrt(Math.Pow(enemy.Position.X - origin.X, 2) + Math.Pow(enemy.Position.Z - origin.Z, 2)), 12.0, "the hop is 80 units");
    }

    [Check("scripting.bb-xinzhao-three-talon-strike-cooldown-starts-when-the-combo-expires")]
    public static void XinZhaoUnusedComboStartsItsCooldownAtExpiry()
    {
        using var match = new Match("XinZhao", Team.Order);
        var xin = match.Hero(0);
        match.Cast(0, 0, xin);
        match.Runtime.Tick(5.4);
        TestAssert.False(xin.Buffs.Any(buff => buff.Name.StartsWith("XenZhaoCombo", StringComparison.Ordinal)), "the combo ran out after five seconds");
        TestAssert.False(xin.SpellSeals.IsSealed(false, 0), "Q is open");
        TestAssert.True(xin.SpellCooldowns[0] is > 9.0 and < 10.0, $"the 10 s cooldown started at the expiry: {xin.SpellCooldowns[0]}");
    }

    /// <summary>
    /// Batch 583 (the checklist's question for retail memory): XenZhaoSweep.TargetExecute keeps the target's centre at the cast and
    /// the buff's OnBuffActivate moves him there (BBMove, Speed 3000, MoveBackBy 0, IdealDistanceVar = the whole distance): he ends
    /// on the centre, the bodies overlapped, with no contact gap. The spell is DoesntBreakShields, so Sivir's shield does not stop
    /// the dash; BuffOnMoveSuccess breaks it (BBBreakSpellShields, then the damage and the slow on everything within 225): the holder
    /// takes nothing and no slow, the unit behind her both, and he is still ordered to attack her.
    /// </summary>
    [Check("scripting.bb-xinzhao-audacious-charge-into-a-spell-shield-lands-on-the-holder-and-pops-it")]
    public static void XinZhaoAudaciousChargeIntoASpellShield()
    {
        using var match = new Match("XinZhao", Team.Order, extraEnemy: true, enemyChampion: "Sivir");
        var xin = match.Hero(0);
        var sivir = match.Hero(1);
        var behind = match.Hero(2);
        sivir.Position = xin.Position + new Position2(450, 0);
        behind.Position = sivir.Position + new Position2(150, 0);
        match.Runtime.HeroStats.Refresh(sivir);
        match.Runtime.HeroStats.Refresh(behind);
        sivir.Mana = sivir.MaxMana * 0.5;
        match.Cast(1, 2, sivir);
        match.Runtime.Tick(0.3);
        TestAssert.True(sivir.Buffs.Any(buff => buff.Name == "SpellShield"), "Sivir's shield is up");
        var centre = sivir.Position;
        var sivirHealth = sivir.Health;
        var sivirMana = sivir.Mana;
        var behindHealth = behind.Health;
        match.Cast(0, 2, sivir);
        var dashed = false;
        for (var tick = 0; tick < 30 && !(dashed && xin.ForcedMove is null); tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            dashed |= xin.ForcedMove is { Effect: "XenZhaoSweep" };
        }
        TestAssert.True(dashed && xin.ForcedMove is null, "the charge ran to its end through the shield");
        match.Runtime.Tick(1.0 / 30.0);
        TestAssert.True(Math.Sqrt(Math.Pow(xin.Position.X - centre.X, 2) + Math.Pow(xin.Position.Z - centre.Z, 2)) < 5.0,
            $"he ends on her centre ({xin.Position.X:0.0},{xin.Position.Z:0.0} against {centre.X:0.0},{centre.Z:0.0})");
        TestAssert.Near(sivirHealth, sivir.Health, 1e-6, "the shield holder takes nothing");
        TestAssert.False(sivir.Buffs.Any(buff => buff.Name == "Slow"), "and is not slowed");
        TestAssert.Near(XinMitigated(70.0, behind.SpellBlock), behindHealth - behind.Health, 0.6, "the unit 150 behind her takes 70 magic");
        TestAssert.True(behind.Buffs.Any(buff => buff.Name == "Slow"), "and is slowed");
        TestAssert.Equal(sivir.NetId, xin.AttackTarget, "he is ordered to attack her");
        match.Runtime.Tick(0.3);
        TestAssert.False(sivir.Buffs.Any(buff => buff.Name == "SpellShield"), "the shield is spent");
        TestAssert.True(sivir.Mana > sivirMana + 50.0, $"and gives her its mana ({sivirMana:0.0} to {sivir.Mana:0.0})");
    }

    [Check("scripting.bb-xinzhao-audacious-charge-damages-and-slows-the-units-around-the-target")]
    public static void XinZhaoAudaciousChargeDashesDamagesAndSlows()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("XinZhao", team, extraEnemy: true);
            var xin = match.Hero(0);
            var target = match.Hero(1);
            var near = match.Hero(2);
            target.Position = xin.Position + new Position2(450, 0);
            near.Position = target.Position + new Position2(150, 0);
            match.Runtime.HeroStats.Refresh(target); // the first stat refresh clamps the harness's 5000 health to the champion's own
            match.Runtime.HeroStats.Refresh(near);
            var targetBlock = target.SpellBlock;
            var nearBlock = near.SpellBlock;
            var targetHealth = target.Health;
            var nearHealth = near.Health;
            match.Cast(0, 2, target);
            var dashed = false;
            var firstTarget = 0.0;
            var firstNear = 0.0;
            for (var tick = 0; tick < 18; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                dashed |= xin.ForcedMove is { Effect: "XenZhaoSweep" };
                // The first drop of each is the charge; the order to attack the target follows it with a swing of its own.
                if (firstTarget == 0.0 && target.Health < targetHealth - 1e-6) firstTarget = targetHealth - target.Health;
                if (firstNear == 0.0 && near.Health < nearHealth - 1e-6) firstNear = nearHealth - near.Health;
            }
            TestAssert.True(dashed, $"{team}: the dash is the script's move");
            // 70 magic damage at rank 1 (+ 0.4 ability power, none here), then 20 percent slow for 1.5 s on everything within 225 of the target.
            TestAssert.Near(XinMitigated(70.0, targetBlock), firstTarget, 0.6, $"{team}: the target takes 70 magic damage");
            TestAssert.Near(XinMitigated(70.0, nearBlock), firstNear, 0.6, $"{team}: the unit 150 behind it too");
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "Slow") && near.Buffs.Any(buff => buff.Name == "Slow"), $"{team}: both are slowed");
            TestAssert.True(Math.Sqrt(Math.Pow(xin.Position.X - target.Position.X, 2) + Math.Pow(xin.Position.Z - target.Position.Z, 2)) < 100.0, $"{team}: Xin Zhao ended on the target");
            TestAssert.Equal(target.NetId, xin.AttackTarget, $"{team}: and was ordered to attack it");
            TestAssert.Near(16.0, xin.SpellCooldowns[2] + 0.6, 0.2, $"{team}: rank 1 cooldown 16 s");
            TestAssert.Near(xin.MaxMana - 70.0, xin.Mana, 12.0, $"{team}: 70 mana");
        }
    }

    [Check("scripting.bb-xinzhao-crescent-sweep-damages-by-current-health-and-adds-resistances")]
    public static void XinZhaoCrescentSweepDamageAndResistances()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("XinZhao", team, extraEnemy: true);
            var xin = match.Hero(0);
            var enemy = match.Hero(1);
            var far = match.Hero(2);
            xin.Level = 6;
            match.Runtime.HeroStats.Refresh(xin);
            enemy.Position = xin.Position + new Position2(200, 0);
            far.Position = xin.Position + new Position2(0, 650);
            enemy.Health = 3000;
            var armor = enemy.Armor;
            var xinArmor = xin.Armor;
            var xinBlock = xin.SpellBlock;
            match.Cast(0, 3, xin);
            match.Runtime.Tick(0.6);
            // 125 + 15 percent of the current health, physical, within 500; one champion hit: armor and magic resistance 25 + 5 for six seconds.
            TestAssert.Near(XinMitigated(125.0 + 0.15 * 3000.0, armor), 3000.0 - enemy.Health, 2.0, $"{team}: 125 plus 15 percent of the current health");
            TestAssert.Near(5000.0, far.Health, 1e-6, $"{team}: nothing at 650");
            TestAssert.Near(xinArmor + 30.0, xin.Armor, 0.01, $"{team}: armor +25 +5 for the champion hit");
            TestAssert.Near(xinBlock + 30.0, xin.SpellBlock, 0.01, $"{team}: magic resistance too");
            // Batch 340: the cooldown starts at the cast frame, not at the press.
            TestAssert.Near(75.0, xin.SpellCooldowns[3] + 0.6 - GameData.Default.Spell("XenZhaoParry", 3).CastDelay, 0.2,
                $"{team}: rank 1 cooldown 75 s");
            match.Runtime.Tick(5.8);
            TestAssert.Near(xinArmor, xin.Armor, 0.01, $"{team}: the armor is gone after six seconds");
            TestAssert.Near(xinBlock, xin.SpellBlock, 0.01, $"{team}: and the magic resistance");
        }
    }

    [Check("scripting.bb-xinzhao-dies-and-comes-back-with-his-passives")]
    public static void XinZhaoRespawnGivesTheCharacterBuffsBack()
    {
        using var match = new Match("XinZhao", Team.Order);
        var xin = match.Hero(0);
        var enemy = match.Hero(1);
        match.Cast(0, 0, xin);
        match.Runtime.Tick(0.4);
        TestAssert.True(match.Runtime.Combat.Kill(xin, enemy.NetId), "Xin Zhao dies");
        TestAssert.False(xin.Buffs.Any(buff => buff.Name is "XenZhaoComboTarget" or "XenZhaoPuncture" or "XenZhaoBattleCryPassive"), "his buffs left with him");
        match.Runtime.Combat.ScheduleRespawn(xin);
        for (var tick = 0; tick < 30 * 90 && !xin.Alive; tick++) match.Runtime.Tick(1.0 / 30.0);
        match.Runtime.Tick(0.5);
        TestAssert.True(xin.Alive, "and is back");
        TestAssert.True(xin.Buffs.Any(buff => buff.Name == "XenZhaoPuncture"), "CharOnResurrect gives Tireless Warrior back (its Target is the hero)");
        TestAssert.True(xin.Buffs.Any(buff => buff.Name == "XenZhaoBattleCryPassive"), "and the Battle Cry passive");
    }

    [Check("combat.melee-attack-spell-with-cast-type-0-has-no-missile-speed")]
    public static void HeroAttackMissileFollowsTheCastType()
    {
        // Melee champions ship a MissileSpeed on a CastType 0 attack spell (unused data); ranged ones fly (CastType 1, 2, 3).
        foreach (var (name, flies) in new[] { ("XinZhao", false), ("Udyr", false), ("JarvanIV", false), ("Annie", true), ("Ezreal", true), ("Nidalee", true) })
        {
            var hero = new Entity { UnitType = UnitType.Hero, ChampionName = name, AttackSpeedMod = 1.0 };
            var timing = HeroAttackTimings.For(hero, GameData.Default);
            TestAssert.Equal(flies, timing.MissileSpeed > 0.0, $"{name}: a missile only for CastType 1, 2 and 3 (speed {timing.MissileSpeed})");
        }
    }
}
