using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine M3: Taric plays from his shipped scripts in a production <c>MatchRuntime</c>. Every expected number
/// is read from the shipped files (the script tables through <see cref="BbScriptLoader"/>, the spell inibins through
/// <see cref="GameData"/>) and pinned a second time as the literal the two files agree on.
/// </summary>
public static class BbTaricChecks
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

    [Check("scripting.bb-taric-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Taric");
        TestAssert.Equal(19, module.ScriptNames.Count, "the Taric closure is 19 scripts (audit bb-coverage)");
        var hero = new Entity { ChampionName = "Taric" };
        TestAssert.Equal(["Imbue", "Shatter", "Dazzle", "TaricHammerSmash"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Taric.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Taric");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 19 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Taric hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-taric-cast-packets-pass-the-four-client-gates")]
    public static void CastPacketsCarryNamesTimesCostsAndTargets()
    {
        using var match = new Match("Taric");
        match.Learn(1);
        var spells = GameData.Default.Champion("Taric").SpellNames;
        TestAssert.Equal(["Imbue", "Shatter", "Dazzle", "TaricHammerSmash"], spells.ToArray(), "slot names");
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Hero);
        TestAssert.Equal([80.0, 50.0, 95.0, 100.0], snapshot.ManaCosts.ToArray(), "rank-one mana costs from the spell inibins");
        TestAssert.True(snapshot.CanCastBits1 == uint.MaxValue && snapshot.CanCastBits2 == uint.MaxValue, "cast bits are open");
        var expectedCooldown = new[] { 20.0, 10.0, 14.0, 60.0 };
        var expectedCost = new[] { 80.0, 50.0, 95.0, 100.0 };
        var targets = new Entity?[] { match.Ally, null, match.Foe, null };
        // Imbue is CastType 0 (the client runs its effect at once, no obj_SpellMissile although the inibin lists a MissileSpeed);
        // Dazzle is CastType 1.
        var missile = new[] { false, false, true, false };
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
            TestAssert.True(cast.DesignerCastTime > 0.0 && cast.DesignerTotalTime > 0.0,
                $"{spells[slot]} designer times {cast.DesignerCastTime}/{cast.DesignerTotalTime}");
            TestAssert.Near(data.CastDelay, cast.DesignerCastTime, 1e-6, $"{spells[slot]} designer cast time is the data's");
            TestAssert.Near(data.TotalTime, cast.DesignerTotalTime, 1e-6, $"{spells[slot]} designer total time is the data's");
            // A self-centred area cast lists the enemies its area holds (SpellSystem.AreaCastTargets, batch 111, the reveal rule): Shatter's
            // and Command Power's radii hold the foe 300 away and not the far one; a unit-targeted cast names its target.
            var listed = targets[slot] is { } named ? new[] { named } : slot is 1 or 3 ? new[] { match.Foe } : Array.Empty<Entity>();
            TestAssert.Equal(listed.Select(unit => unit.NetId).ToArray(), cast.Targets.Select(target => target.NetId).ToArray(), $"{spells[slot]} target list");
            TestAssert.Equal(missile[slot], cast.MissileNetId != 0u, $"{spells[slot]} missile");
            match.Advance(1.0);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Imbue
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-q-imbue-heals-himself-and-a-wounded-ally")]
    public static void ImbueHealsTheCasterAndTheWoundedTarget()
    {
        var heal = ByLevel("Imbue", "TargetExecute", "BaseHealAmount");
        var data = GameData.Default.Spell("Imbue", 5);
        TestAssert.Equal([60.0, 100.0, 140.0, 180.0, 220.0], heal, "Imbue.luaobj BaseHealAmount");
        var imbue = Blocks(Script("Imbue"), "TargetExecute").ToArray();
        var ratio = imbue.First(block => block.Function == "BBMath" && block.Text("DestVar") == "APMod").Number("Src1Value")!.Value;
        var selfScale = imbue.First(block => block.Function == "BBMath" && block.Text("Src1Var") == "HealAmount").Number("Src2Value")!.Value;
        TestAssert.Near(0.6, ratio, 1e-12, "Imbue.luaobj ability power ratio");
        TestAssert.Near(1.4, selfScale, 1e-12, "Imbue.luaobj self-cast scale");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(heal[rank - 1], data.EffectNumber(1, rank), $"Imbue.inibin Effect1 rank {rank} agrees with the script");
            TestAssert.Near(heal[rank - 1] * 1.4, data.EffectNumber(2, rank), 1e-9, $"Imbue.inibin Effect2 rank {rank} is the self heal");
        }
        TestAssert.Near(0.6, data.Coefficient, 1e-6, "Imbue.inibin Coefficient");
        TestAssert.Near(0.84, data.Coefficient2, 1e-6, "Imbue.inibin Coefficient2 is the self-cast ratio");
        TestAssert.Equal([20.0, 19.0, 18.0, 17.0, 16.0], data.Cooldowns.ToArray(), "Imbue cooldowns");
        TestAssert.Equal([80.0, 95.0, 110.0, 125.0, 140.0], data.ManaCosts.ToArray(), "Imbue mana costs");
        TestAssert.Near(750.0, data.CastRange, 1e-9, "Imbue range");

        using var match = new Match("Taric");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var amount = heal[2] + 0.6 * ap;

        // On a wounded ally: the cast ends its windup on the ally, then both heal (the ally only because it is below full health).
        // Real maximum health is a few hundred at level one (the harness's 5000 is reset by any stat refresh), so the heal is
        // measured against the cap.
        match.Hero.Health = 50.0;
        match.Ally.Health = 40.0;
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.CanCast(0, match.Ally).Allowed, "an ally in range is a legal target");
        TestAssert.True(match.CanCast(0, match.Foe).Failure == SpellCastFailure.Target, "an enemy is not (AffectFriends only)");
        TestAssert.True(match.Cast(0, match.Ally), "Q cast");
        match.Advance(0.1);
        TestAssert.Near(40.0, match.Ally.Health, 1.0, "nothing lands before the windup and the flight (regeneration aside)");
        match.Advance(0.6);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 110.0, match.Hero.Mana, 1.0, "rank 3 costs 110 mana");
        TestAssert.Near(18.0, match.Hero.SpellCooldowns[0] + 0.7 - match.CastFrameOf(0), Step + 1e-6, "rank 3 cooldown 18 s");
        TestAssert.Near(Math.Min(match.Hero.MaxHealth, 50.0 + amount), match.Hero.Health, 2.0, "Taric heals the full amount");
        TestAssert.Near(Math.Min(match.Ally.MaxHealth, 40.0 + amount), match.Ally.Health, 2.0, "so does the wounded ally");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Taric->Ally", StringComparison.Ordinal)), "assist marker on the ally");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("Global_Heal.troy", StringComparison.Ordinal)), "the heal effect on Taric");

        // A healthy ally takes no heal; Taric still heals.
        match.Advance(18.0);
        match.Refill();
        match.Hero.Health = 50.0;
        match.Ally.Health = match.Ally.MaxHealth;
        TestAssert.True(match.Cast(0, match.Ally), "Q on a healthy ally");
        match.Advance(0.7);
        TestAssert.Near(Math.Min(match.Hero.MaxHealth, 50.0 + amount), match.Hero.Health, 2.0, "Taric heals");
        TestAssert.Near(match.Ally.MaxHealth, match.Ally.Health, 1e-9, "a full-health ally is not healed");

        // On himself: one heal, 40 percent stronger, no missile.
        match.Advance(18.0);
        match.Refill();
        match.Hero.Health = 10.0;
        var beforeSelf = match.Log.Casts.Count;
        TestAssert.True(match.Cast(0, match.Hero), "Q on himself");
        TestAssert.Equal(0u, match.Log.Casts[beforeSelf].MissileNetId, "a self cast has no missile");
        match.Advance(0.5);
        TestAssert.Near(Math.Min(match.Hero.MaxHealth, 10.0 + amount * 1.4), match.Hero.Health, 2.0, "a self cast heals 140 percent once");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Shatter
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-w-shatter-damages-shreds-and-armors-his-side")]
    public static void ShatterDamagesShredsArmorAndAurasAllies()
    {
        var shatter = Script("Shatter");
        var damage = Table(Blocks(shatter, "TargetExecute").First(block => block.Function == "BBApplyDamage"), "DamageByLevel");
        var shred = ByLevel("Shatter", "TargetExecute", "ArmorReduction");
        var data = GameData.Default.Spell("Shatter", 5);
        TestAssert.Equal([60.0, 105.0, 150.0, 195.0, 240.0], damage, "Shatter.luaobj damage");
        TestAssert.Equal([-10.0, -15.0, -20.0, -25.0, -30.0], shred, "Shatter.luaobj ArmorReduction");
        TestAssert.Near(0.4, Blocks(shatter, "TargetExecute").First(block => block.Function == "BBApplyDamage").Number("SpellDamageRatio")!.Value,
            1e-12, "Shatter.luaobj SpellDamageRatio");
        TestAssert.Near(4.0, Blocks(shatter, "TargetExecute").First(block => block.Function == "BBSpellBuffAdd").Number("Duration")!.Value,
            1e-12, "the shred lasts 4 s");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"Shatter.inibin Effect1 rank {rank}");
            TestAssert.Equal(-shred[rank - 1], data.EffectNumber(2, rank), $"Shatter.inibin Effect2 rank {rank}");
            TestAssert.Equal(-shred[rank - 1], data.EffectNumber(3, rank), $"Shatter.inibin Effect3 rank {rank} (the aura)");
        }
        TestAssert.Near(0.4, data.Coefficient, 1e-6, "Shatter.inibin Coefficient");
        TestAssert.Equal([50.0, 60.0, 70.0, 80.0, 90.0], data.ManaCosts.ToArray(), "Shatter mana costs");
        TestAssert.Equal([10.0, 10.0, 10.0, 10.0, 10.0], data.Cooldowns.ToArray(), "Shatter cooldowns");
        TestAssert.Near(400.0, data.CastRadius, 1e-9, "Shatter radius");

        using var match = new Match("Taric");
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Hero.AbilityPower;
        var armorBefore = match.Foe.Armor;
        var spellBlockBefore = match.Foe.SpellBlock;
        var heroArmorBefore = match.Hero.Armor;
        var allyArmorBefore = match.Ally.Armor;
        match.Refill();
        var mana = match.Hero.Mana;
        match.Foe.Position = new Position2(7300, 7000);
        var minion = Enemy(match, "Minion", new Position2(7000, 7390));
        var far = Enemy(match, "FarMinion", new Position2(7000, 7450));
        TestAssert.True(match.CanCast(1).Allowed, "Shatter has no target");
        TestAssert.True(match.Cast(1), "W cast");
        // SelfExecute (the nova and the removal of the self bonus) runs at the cast frame, when the windup ends (18 October 2026).
        TestAssert.True(!match.Log.Effects.Any(line => line.Contains("Shatter_nova.troy", StringComparison.Ordinal)), "no nova during the windup");
        match.Advance(data.CastDelay - 0.1);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands during the windup");
        match.Advance(0.3);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 60.0, match.Hero.Mana, 1.0, "rank 2 costs 60 mana");
        TestAssert.Near(10.0, match.Hero.SpellCooldowns[1] + 0.2, Step + 1e-6, "cooldown 10 s");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("Shatter_nova.troy", StringComparison.Ordinal)), "the nova plays at the cast frame");
        var raw = 105.0 + 0.4 * ap;
        foreach (var name in new[] { "Foe", "Minion" })
        {
            var hits = HitsOn(match, name);
            TestAssert.Equal(1, hits.Count, $"{name} is hit once");
            TestAssert.Near(raw, hits[0].Raw, 1e-6, $"{name}: rank 2 damage 105 + 0.4 AP");
            TestAssert.True(hits[0].Kind == "SpellAoe", "damage source SPELLAOE");
        }
        TestAssert.Equal(0, HitsOn(match, "FarMinion").Count + HitsOn(match, "Ally").Count + HitsOn(match, "FarEnemy").Count,
            "units beyond 400 and friends are untouched");
        var foeHit = HitsOn(match, "Foe")[0];
        TestAssert.Near(Mitigated(raw, spellBlockBefore), foeHit.Dealt, 1e-6, "Shatter's damage is magic: mitigated by the spell block");
        TestAssert.True(Buff(match.Foe, "Shatter") is { Type: BuffType.CombatDehancer }, "the armor shred is on the enemy");
        TestAssert.Near(4.0, Buff(match.Foe, "Shatter")!.ExpiresAt, 0.4, "for four seconds");
        TestAssert.Near(armorBefore - 15.0, match.Foe.Armor, 1e-6, "rank 2 shred: -15 armor");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Taric->Foe", StringComparison.Ordinal)), "assist marker on the enemy");
        match.Advance(4.2);
        TestAssert.True(Buff(match.Foe, "Shatter") is null, "the shred ends");
        TestAssert.Near(armorBefore, match.Foe.Armor, 1e-6, "armor restored");

        // The passive: allies within 1000 hold ShatterAura (+15 armor at rank 2); Taric also has ShatterSelfBonus while the
        // spell is ready, and loses it for the cooldown after the cast.
        TestAssert.True(Buff(match.Ally, "ShatterAura") is { Type: BuffType.Aura }, "the ally holds the aura");
        TestAssert.Near(allyArmorBefore + 15.0, match.Ally.Armor, 1e-6, "ally armor +15");
        TestAssert.True(Buff(match.Foe, "ShatterAura") is null && Buff(match.FarFoe, "ShatterAura") is null, "enemies are not covered");
        TestAssert.True(Buff(match.Hero, "ShatterSelfBonus") is null, "the self bonus waits for the cooldown (10 s)");
        // Batch 340: the 10 s run from the cast frame, not the press, so the poll that restores the bonus comes later.
        match.Advance(6.6);
        TestAssert.True(Buff(match.Hero, "ShatterSelfBonus") is not null, "the cooldown is over: the self bonus is back");
        TestAssert.Near(heroArmorBefore + 30.0, match.Hero.Armor, 1e-6, "Taric holds the aura and the self bonus: +15 and +15");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-taric-w-shatter-and-ultimate-break-a-spell-shield")]
    public static void ShatterBreaksBansheesVeilBeforeItDamages()
    {
        using var match = new Match("Taric");
        match.Learn(2);
        // The veil is the item passive's buff (a stat refresh removes it from a unit without the item), so the Foe owns the item.
        match.Foe.Inventory = [new InventorySlot(3102), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        match.Advance(0.3);
        TestAssert.True(Buff(match.Foe, "BansheesVeil") is not null, "the Foe holds a Banshee's Veil: " + Names(match.Foe));
        match.Refill();
        TestAssert.True(match.Cast(1), "W cast");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "BansheesVeil") is null, "BBBreakSpellShields popped the veil");
        TestAssert.True(Buff(match.Foe, "BansheesVeilTimer") is not null, "and started its cooldown marker");
        // Batch 56 (811c362): BBBreakSpellShields pops the veil, whose BuffOnPreDamage zeroes the damage that follows
        // (BansheesVeil.luaobj, WillRemove), so the hit is still recorded but deals nothing.
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the damage block that follows the break still runs");
        TestAssert.Near(0.0, HitsOn(match, "Foe")[0].Dealt, 0.0, "and the popped veil zeroes it");
        TestAssert.False(match.Foe.Buffs.Any(buff => buff.Name == "SpellShieldMarker"), "the marker buff itself is never added");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Dazzle
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-e-dazzle-stuns-and-hits-harder-up-close")]
    public static void DazzleStunsAndScalesWithDistance()
    {
        var dazzle = Script("Dazzle");
        var blocks = Blocks(dazzle, "TargetExecute").ToArray();
        var damage = ByLevel("Dazzle", "TargetExecute", "BaseDamage");
        var data = GameData.Default.Spell("Dazzle", 5);
        TestAssert.Equal([40.0, 70.0, 100.0, 130.0, 160.0], damage, "Dazzle.luaobj BaseDamage");
        TestAssert.Near(0.4, blocks.First(block => block.Function == "BBMath" && block.Text("DestVar") == "APStat").Number("Src1Value")!.Value,
            1e-12, "Dazzle.luaobj ability power ratio");
        TestAssert.Near(1.5, blocks.First(block => block.Function == "BBApplyStun").Number("Duration")!.Value, 1e-12, "stun seconds");
        TestAssert.Near(250.0, blocks.First(block => block.Function == "BBSetVarInTable" && block.Text("DestVar") == "FullDamageRange")
            .Number("SrcValue")!.Value, 1e-12, "full damage inside 250");
        TestAssert.Near(2.0, blocks.First(block => block.Function == "BBSetVarInTable" && block.Text("DestVar") == "MaxMultiplier")
            .Number("SrcValue")!.Value, 1e-12, "the multiplier peaks at 2");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"Dazzle.inibin Effect1 rank {rank}");
            TestAssert.Equal(1.5, data.EffectNumber(2, rank), $"Dazzle.inibin Effect2 rank {rank} is the stun");
            TestAssert.Equal(damage[rank - 1] * 2.0, data.EffectNumber(3, rank), $"Dazzle.inibin Effect3 rank {rank} is the close-range maximum");
        }
        TestAssert.Equal([14.0, 13.0, 12.0, 11.0, 10.0], data.Cooldowns.ToArray(), "Dazzle cooldowns");
        TestAssert.Equal([95.0, 95.0, 95.0, 95.0, 95.0], data.ManaCosts.ToArray(), "Dazzle mana costs");
        TestAssert.Near(625.0, data.CastRange, 1e-9, "Dazzle range");

        using var match = new Match("Taric");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var baseDamage = 100.0 + 0.4 * ap;

        // 300 away: 50 beyond the full-damage range of 250, over 375 left of the cast range: 1 + (1 - 50 / 375).
        match.Foe.Position = new Position2(7300, 7000);
        match.Refill();
        TestAssert.True(match.CanCast(2, match.Foe).Allowed, "an enemy in range");
        TestAssert.True(match.CanCast(2, match.Ally).Failure == SpellCastFailure.Target, "an ally is not a legal target");
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        // Batch 293 (batch 279's seat): the cast's windup stop seats Taric on his lattice anchor at once, a diagonal unit from
        // (7000, 7000), and Dazzle.luaobj measures the distance from there (301.0 here, 621.0 below).
        double Factor()
        {
            var offset = match.Foe.Position - match.Hero.Position;
            return 1.0 + (1.0 - (Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z) - 250.0) / 375.0);
        }
        TestAssert.True(Math.Abs(match.Hero.Position.X - 7000.0) <= 1.0 && Math.Abs(match.Hero.Position.Z - 7000.0) <= 1.0, "seated");
        var factor = Factor();
        match.Advance(0.1);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands in the windup");
        match.Advance(0.7);
        // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(12.0, match.Hero.SpellCooldowns[2] + 0.8 - match.CastFrameOf(2), Step + 1e-6, "rank 3 cooldown 12 s");
        var hit = HitsOn(match, "Foe").Single();
        NearF32(baseDamage * factor, hit.Raw, "damage scaled by distance");
        TestAssert.True(hit.Kind == "Spell", "damage source SPELL");
        TestAssert.True(Buff(match.Foe, "Stun") is { Type: BuffType.Stun }, "the Foe is stunned");
        TestAssert.True(match.Log.Buffs.Any(line => line.Contains(" add Foe Stun type=Stun dur=1.5 ", StringComparison.Ordinal)),
            "for 1.5 s: " + string.Join(" | ", match.Log.Buffs));

        // Inside 250: the full factor 2. At the end of the range: the factor 1.
        match.Advance(2.0);
        match.Foe.Position = new Position2(7200, 7000);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E up close");
        match.Advance(0.8);
        NearF32(baseDamage * 2.0, HitsOn(match, "Foe")[1].Raw, "inside 250 the damage doubles");
        match.Advance(3.0);
        match.Foe.Position = new Position2(7000 + 620, 7000);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E at range");
        factor = Factor();
        match.Advance(1.2);
        var far = HitsOn(match, "Foe")[2];
        NearF32(baseDamage * factor, far.Raw, "620 from his spawn point: the factor is nearly 1");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Command Power (TaricHammerSmash) and Radiance
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-r-hammer-smash-damages-and-radiance-shares-its-bonus")]
    public static void HammerSmashDamagesTheAreaAndRadianceBuffsTheTeam()
    {
        var smash = Script("TaricHammerSmash");
        var apply = Blocks(smash, "SelfExecute").First(block => block.Function == "BBApplyDamage");
        var damage = Table(apply, "DamageByLevel");
        var bonus = ByLevel("TaricHammerSmash", "SelfExecute", "DamageIncrease");
        var power = ByLevel("TaricHammerSmash", "SelfExecute", "AbilityPower");
        var data = GameData.Default.Spell("TaricHammerSmash", 3);
        TestAssert.Equal([150.0, 250.0, 350.0], damage, "TaricHammerSmash.luaobj damage");
        TestAssert.Near(0.6, apply.Number("SpellDamageRatio")!.Value, 1e-12, "TaricHammerSmash.luaobj SpellDamageRatio");
        TestAssert.Equal([30.0, 50.0, 70.0], bonus, "TaricHammerSmash.luaobj Radiance attack damage");
        TestAssert.Equal([30.0, 50.0, 70.0], power, "TaricHammerSmash.luaobj Radiance ability power");
        for (var rank = 1; rank <= 3; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"TaricHammerSmash.inibin Effect1 rank {rank}");
            TestAssert.Equal(10.0, data.EffectNumber(2, rank), $"TaricHammerSmash.inibin Effect2 rank {rank} is the buff duration");
            TestAssert.Equal(bonus[rank - 1], data.EffectNumber(3, rank), $"TaricHammerSmash.inibin Effect3 rank {rank}");
        }
        TestAssert.Near(0.6, data.Coefficient, 1e-6, "TaricHammerSmash.inibin Coefficient");
        TestAssert.Equal([60.0, 60.0, 60.0], data.Cooldowns.ToArray(), "Command Power cooldowns");
        TestAssert.Equal([100.0, 100.0, 100.0], data.ManaCosts.ToArray(), "Command Power mana costs");
        var radiance = Script("Radiance");
        TestAssert.Near(10.0, Blocks(smash, "SelfExecute").First(block => block.Function == "BBSpellBuffAdd").Number("Duration")!.Value, 1e-12,
            "the Radiance buff lasts 10 s");
        TestAssert.Near(1000.0, Blocks(radiance, "OnBuffActivate").First(block => block.Function == "BBForEachUnitInTargetArea").Number("Range")!.Value,
            1e-12, "Radiance covers 1000");
        TestAssert.Near(0.5, Blocks(radiance, "OnBuffActivate").First(block => block.Function == "BBMath" && block.Text("Src1Var") == "DamageIncrease")
            .Number("Src2Value")!.Value, 1e-12, "allies get half");

        using var match = new Match("Taric");
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Hero.AbilityPower;
        var heroAttack = match.Hero.AttackDamage;
        var allyAttack = match.Ally.AttackDamage;
        var allyAp = match.Ally.AbilityPower;
        var spellBlock = match.Foe.SpellBlock;
        var allyMinion = Enemy(match, "AllyMinion", new Position2(7000, 7300), team: Team.Order);
        var allyMinionAttack = allyMinion.AttackDamage;
        var enemyMinion = Enemy(match, "Minion", new Position2(7000, 6700));
        var farMinion = Enemy(match, "FarMinion", new Position2(7000, 6500));
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.CanCast(3).Allowed, "Command Power has no target");
        TestAssert.True(match.Cast(3), "R cast");
        TestAssert.True(Buff(match.Hero, "TaricHammerInternal") is { Type: BuffType.Internal }, "CharOnSpellCast adds the 2 s weapon glow for slot 3");
        TestAssert.Near(2.0, Buff(match.Hero, "TaricHammerInternal")!.ExpiresAt, 0.2, "TaricHammerInternal lasts 2 s");
        match.Advance(data.CastDelay - 0.05);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "the whole effect waits for the end of the windup");
        TestAssert.True(Buff(match.Hero, "Radiance") is null, "and so does Radiance");
        match.Advance(0.2);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 100.0, match.Hero.Mana, 1.0, "costs 100 mana");
        TestAssert.Near(60.0, match.Hero.SpellCooldowns[3] + 0.15, Step + 1e-6, "cooldown 60 s");

        var raw = 250.0 + 0.6 * ap;
        foreach (var name in new[] { "Foe", "Minion" })
        {
            var hits = HitsOn(match, name);
            TestAssert.Equal(1, hits.Count, $"{name} is hit once");
            NearF32(raw, hits[0].Raw, $"{name}: rank 2 damage 250 + 0.6 AP");
            TestAssert.True(hits[0].Kind == "SpellAoe", "damage source SPELLAOE");
        }
        NearF32(Mitigated(raw, spellBlock), HitsOn(match, "Foe")[0].Dealt, "magic damage mitigated by spell block");
        TestAssert.Equal(0, HitsOn(match, "FarMinion").Count + HitsOn(match, "Ally").Count + HitsOn(match, "AllyMinion").Count,
            "units beyond 400 and friends are untouched");

        // Radiance: Taric gains the full bonus, allies within 1000 half of it (heroes) or a third of the attack damage (minions).
        TestAssert.True(Buff(match.Hero, "Radiance") is { Type: BuffType.CombatEnhancer }, "Radiance is on Taric");
        TestAssert.Near(10.0, Buff(match.Hero, "Radiance")!.ExpiresAt, 0.5, "for ten seconds");
        TestAssert.Near(heroAttack + 50.0, match.Hero.AttackDamage, 1e-6, "rank 2: +50 attack damage");
        TestAssert.Near(ap + 50.0, match.Hero.AbilityPower, 1e-6, "rank 2: +50 ability power");
        TestAssert.True(Buff(match.Ally, "RadianceAura") is { Type: BuffType.Aura }, "the ally hero holds the aura");
        TestAssert.Near(allyAttack + 25.0, match.Ally.AttackDamage, 1e-6, "an ally champion gets half: +25 attack damage");
        TestAssert.Near(allyAp + 25.0, match.Ally.AbilityPower, 1e-6, "and +25 ability power");
        TestAssert.True(Buff(allyMinion, "RadianceAura") is not null, "an allied minion holds the aura too");
        TestAssert.Near(allyMinionAttack + 50.0 * 0.5 / 3.0, allyMinion.AttackDamage, 1e-6, "a minion gets a third of the shared half, no ability power");
        TestAssert.True(Buff(match.Foe, "RadianceAura") is null && Buff(match.FarFoe, "RadianceAura") is null, "enemies get nothing");
        // The aura is renewed once a second while the buff lives, and lapses with it.
        match.Advance(7.0);
        TestAssert.Near(allyAttack + 25.0, match.Ally.AttackDamage, 1e-6, "the aura is still on after seven seconds");
        match.Advance(5.0);
        TestAssert.True(Buff(match.Hero, "Radiance") is null, "Radiance ends after ten seconds");
        TestAssert.Near(heroAttack, match.Hero.AttackDamage, 1e-6, "attack damage restored");
        TestAssert.Near(ap, match.Hero.AbilityPower, 1e-6, "ability power restored");
        TestAssert.True(Buff(match.Ally, "RadianceAura") is null, "the ally's aura has lapsed");
        TestAssert.Near(allyAttack, match.Ally.AttackDamage, 1e-6, "ally attack damage restored");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Gemcraft and the cooldown of Imbue
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-passive-gemcraft-refunds-mana-and-trims-imbue")]
    public static void BasicAttacksRestoreManaAndShortenImbue()
    {
        var gem = Script("Gemcraft");
        TestAssert.Near(0.075, Blocks(gem, "BuffOnHitUnit").First(block => block.Function == "BBMath").Number("Src1Value")!.Value, 1e-12,
            "Gemcraft.luaobj mana per damage dealt");
        var hit = Blocks(CharScript("Taric"), "CharOnHitUnit").Where(block => block.Function == "BBMath").ToArray();
        TestAssert.Equal([3.0, 1.0], hit.Select(block => block.Number("Src2Value")!.Value).ToArray(),
            "CharScriptTaric.luaobj: Imbue is shortened by 3 s against a champion, 1 s against anything else");

        using var match = new Match("Taric");
        match.Learn(1);
        TestAssert.True(Buff(match.Hero, "Gemcraft") is { Type: BuffType.CombatEnhancer }, "CharOnActivate added Gemcraft");
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.Health = match.Foe.MaxHealth;
        var minion = Enemy(match, "Minion", new Position2(7000, 7100));

        // An attack on a champion: mana comes back, Imbue's cooldown loses three seconds.
        match.Hero.SpellCooldowns[0] = 10.0;
        match.Hero.Mana = 100.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack starts");
        match.Advance(1.0);
        var raw = HitsOn(match, "Foe").Single(item => item.Kind == "Basic").Raw;
        TestAssert.Near(100.0 + 0.075 * raw + match.Hero.ManaRegen * 1.0, match.Hero.Mana, 0.3, "7.5 percent of the damage dealt returns as mana, plus the second's regeneration");
        TestAssert.Near(6.0, match.Hero.SpellCooldowns[0], 0.05, "Imbue: 10 s minus 3 s against a champion, minus the second that passed");
        // On a minion: one second.
        match.Hero.SpellCooldowns[0] = 10.0;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, minion), "the attack on a minion starts");
        match.Advance(1.0);
        TestAssert.Near(8.0, match.Hero.SpellCooldowns[0], 0.05, "Imbue: 10 s minus 1 s against a minion, minus the second that passed");
        // No cooldown running, no rank: nothing changes.
        match.Hero.SpellCooldowns[0] = 0.0;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "another attack");
        match.Advance(1.0);
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "a ready Imbue stays ready");
        match.Hero.SpellRanks[0] = 0;
        match.Hero.SpellCooldowns[0] = 10.0;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "an attack with Imbue unlearned");
        match.Advance(0.5);
        TestAssert.True(match.Hero.SpellCooldowns[0] > 9.0, "an unlearned Imbue is not trimmed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Registration, ranks and production safety
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-production-installs-the-sink-and-his-passives")]
    public static void TaricJoinsTheMatchThroughTheSharedScriptSink()
    {
        using var match = new Match("Taric");
        TestAssert.True(((Amberline.Simulation.Runtime.MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks is not null,
            "a Taric in the roster installs the sink when his hero is created");
        TestAssert.True(match.Module.IsBound, "the module is bound to the match");
        TestAssert.True(Buff(match.Hero, "Gemcraft") is { Type: BuffType.CombatEnhancer }, "CharOnActivate added Gemcraft");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "ChampionChampionDelta"),
            "the shared system buffs are applied once, natively: the engine skips the script's copies");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "batch 203: the champion's shipped CharOnActivate add is the one tower buff; nothing refuses it any more");
        // Until Shatter is learned the aura loop does nothing (the script tests the slot's level).
        match.Advance(1.5);
        TestAssert.True(Buff(match.Hero, "ShatterAura") is null && Buff(match.Hero, "ShatterSelfBonus") is null, "no aura at rank 0");
        match.Hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 1), "W rank 1 at level 1");
        match.Advance(0.2);
        TestAssert.True(Buff(match.Hero, "ShatterAura") is not null, "the aura starts with the first rank");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-taric-ranks-levels-and-cooldowns")]
    public static void RankGatesLevelUpsAndCooldownsFollowTheData()
    {
        using var match = new Match("Taric");
        var taric = match.Hero;
        taric.SkillPoints = 20;
        TestAssert.True(match.Runtime.Spells.RankUp(taric, 0), "Q rank 1 at level 1");
        TestAssert.False(match.Runtime.Spells.RankUp(taric, 0), "Q rank 2 needs level 3");
        TestAssert.False(match.Runtime.Spells.RankUp(taric, 3), "R needs level 6");
        var manaBefore = taric.MaxMana;
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(taric, 0.0, 100000.0), "experience");
        TestAssert.Equal(18, taric.Level, "enough experience reaches level 18");
        TestAssert.True(taric.MaxMana > manaBefore, "the level-up refreshed his stats");
        for (var slot = 0; slot < 4; slot++)
            while (match.Runtime.Spells.RankUp(taric, slot)) { }
        TestAssert.Equal([5, 5, 5, 3], taric.SpellRanks.ToArray(), "all ranks reachable at level 18");

        var cooldowns = new[] { new[] { 20.0, 19, 18, 17, 16 }, new[] { 10.0, 10, 10, 10, 10 }, new[] { 14.0, 13, 12, 11, 10 }, new[] { 60.0, 60, 60 } };
        var manaCosts = new[] { new[] { 80.0, 95, 110, 125, 140 }, new[] { 50.0, 60, 70, 80, 90 }, new[] { 95.0, 95, 95, 95, 95 }, new[] { 100.0, 100, 100 } };
        for (var slot = 0; slot < 4; slot++)
        {
            var data = GameData.Default.Spell(GameData.Default.Champion("Taric").SpellNames[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(cooldowns[slot], data.Cooldowns.ToArray(), $"slot {slot} cooldowns");
            TestAssert.Equal(manaCosts[slot], data.ManaCosts.ToArray(), $"slot {slot} mana costs");
        }
        // Cooldown reduction scales the cooldown of the cast (the shared BeginChampionCast rule), per rank.
        foreach (var slot in new[] { 1, 3 })
        {
            taric.SpellRanks[slot] = slot == 3 ? 2 : 4;
            match.Refill();
            taric.CooldownMod = -0.1; // a stat refresh (a buff payload change) recomputes it from the stat sources
            taric.Position = new Position2(7000, 7000);
            TestAssert.True(match.Cast(slot), $"slot {slot} cast");
            // Batch 340: a windup's cooldown starts at its cast frame (CastFrameOf; 0 for an instacast).
            match.Advance(match.CastFrameOf(slot));
            TestAssert.Near(cooldowns[slot][taric.SpellRanks[slot] - 1] * 0.9, taric.SpellCooldowns[slot], Step + 1e-6, $"slot {slot} cooldown with 10% reduction");
            match.Advance(0.5);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Taric bots in a headless match
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-taric-bots-play-a-six-minute-headless-match", Serial = true)]
    public static void TaricBotsPlayWithoutScriptExceptionsOrPacketViolations()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-taric-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] roster = ["Taric", "Annie", "Janna", "Ezreal", "Vayne", "Taric", "Orianna", "Blitzcrank", "Nidalee", "Shaco"];
            var casts = new Dictionary<(uint Hero, int Slot), int>();
            var previous = new Dictionary<(uint Hero, int Slot), double>();
            var failures = new List<string>();
            var options = new Amberline.Tools.Diagnostics.HeadlessMatchOptions(20261016, Path.Combine(root, "m"), 6.0, roster)
            {
                OnStep = runtime =>
                {
                    foreach (var hero in runtime.World.Heroes().Where(hero => hero.ChampionName == "Taric"))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            var key = (hero.NetId, slot);
                            var now = hero.SpellCooldowns[slot];
                            if (previous.TryGetValue(key, out var before) && now > before + 0.5) casts[key] = casts.GetValueOrDefault(key) + 1;
                            previous[key] = now;
                        }
                    }
                    var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Taric")!;
                    foreach (var failure in module.ScriptFailures.Skip(failures.Count))
                        failures.Add($"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}");
                },
            };
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(options);
            TestAssert.Equal("cap", report.Outcome, "six game minutes end at the cap, not in a crash");
            TestAssert.Equal(0L, report.PacketViolations, "the packet-stream checker finds no violation");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
            TestAssert.Equal(0, failures.Count, "no script exception: " + string.Join(" | ", failures.Take(3)));
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            TestAssert.False(log.Contains("script.exception", StringComparison.Ordinal), "no script.exception line in the session log");
            TestAssert.True(casts.Values.Sum() > 0, "the Taric bots cast their spells: " +
                string.Join(", ", casts.OrderBy(pair => pair.Key.Slot).Select(pair => $"slot {pair.Key.Slot} x{pair.Value}")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
