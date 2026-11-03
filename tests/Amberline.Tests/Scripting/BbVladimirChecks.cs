using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (25 October 2026, batch 261): Vladimir plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). The expected numbers are the literals of the shipped scripts and spell inibins (Transfusion's damage and heal, Tides of Blood's cost
/// and damage by stack, Hemoplague's cost and detonation by rank). The scenario <c>S-vladimir</c> shows the same facts on a headless match, and the
/// record names the facts that do not hold yet (Tides of Blood's damage, Sanguine Pool's untargetability, slow and damage, Crimson Pact's bonus health).
/// Written and run by the worker that made them.
/// </summary>
public static class BbVladimirChecks
{
    private static readonly double[] TransfusionDamage = [90.0, 125.0, 160.0, 195.0, 230.0];
    private static readonly double[] TransfusionHeal = [15.0, 25.0, 35.0, 45.0, 55.0];
    private static readonly double[] TidesCost = [30.0, 40.0, 50.0, 60.0, 70.0];
    private static readonly double[] HemoDamage = [150.0, 250.0, 350.0];

    private static double Mitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    /// <summary>A match with Vladimir at a fixed spot with a dummy foe in front of him (armour 0, magic resistance 30), every spell at rank three.</summary>
    private static Match Duel()
    {
        var match = new Match("Vladimir");
        match.Learn(3);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.Armor = 0.0;
        match.Foe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    [Check("scripting.bb-vladimir-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRegistry()
    {
        var module = new BbChampionModule("Vladimir");
        TestAssert.Equal(24, module.ScriptNames.Count, "the Vladimir closure is 24 scripts (the gap tool of the wave plan)");
        foreach (var name in new[] { "CharScriptVladimir", "VladimirTransfusion", "VladimirSanguinePool", "VladimirTidesofBlood", "VladimirTidesofBloodNuke",
                     "VladimirHemoplague", "VladimirHemoplagueDebuff", "VladimirBloodGorged" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Vladimir");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var hero = new Entity { ChampionName = "Vladimir" };
        TestAssert.Equal(["VladimirTransfusion", "VladimirSanguinePool", "VladimirTidesofBlood", "VladimirHemoplague"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Vladimir.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "5, 5, 5 and 3 ranks");
        var info = ChampionCatalog.ByName("Vladimir")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 6300, RetailBot: false }, "playable as a mid laner for 6300 IP, not a retail bot");
        using var match = new Match("Vladimir");
        TestAssert.True(match.Runtime.Dependencies.ChampionDispatcher.FindModule("Vladimir") is BbChampionModule, "production runs him from the script module");
    }

    [Check("scripting.bb-vladimir-the-pool-is-empty-and-health-follows-the-level-curve")]
    public static void HealthPoolAndNoMana()
    {
        using var match = new Match("Vladimir");
        TestAssert.Near(0.0, match.Hero.MaxMana, 1e-9, "BaseMP -10000 is a sentinel: the pool is empty (no mana, no cost gate)");
        // The shipped stats at level one: base 400 plus 85 (CharScriptVladimir's baseHP and healthPerLevel); the duel fixture's own health is not a stat.
        var stats = GameData.Default.StatsAtLevel(GameData.Default.Champion("Vladimir"), 1);
        TestAssert.Near(485.0, stats.MaxHp, 1e-6, "health at level one is 400 plus 85 (CharScriptVladimir: 400 and 85 a level)");
    }

    [Check("scripting.bb-vladimir-transfusion-hits-for-the-script-and-heals-for-its-child")]
    public static void TransfusionDamageAndHeal()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var rank = hero.SpellRanks[0];
        TestAssert.Equal(3, rank, "Transfusion at rank three");
        hero.Health = hero.MaxHealth * 0.5;
        var healthBefore = hero.Health;
        var foeBefore = foe.Health;
        TestAssert.True(match.Cast(0, foe), "Transfusion casts at a foe in range");
        match.Advance(1.0);
        var damage = foeBefore - foe.Health;
        var heal = hero.Health - healthBefore;
        TestAssert.Near(Mitigated(TransfusionDamage[rank - 1], foe.SpellBlock), damage, 2.0, "90 to 230 magic damage (0.6 AP), reduced by the foe's magic resistance");
        TestAssert.Near(TransfusionHeal[rank - 1], heal, 2.0, "the self-heal of its child: 15 to 55 (0.25 AP)");
        TestAssert.Equal(0.0, hero.Mana, "Transfusion costs nothing from the empty pool");
    }

    [Check("scripting.bb-vladimir-tides-of-blood-costs-more-with-each-empowered-stack")]
    public static void TidesCostGrowsWithTheStacks()
    {
        using var match = Duel();
        var hero = match.Hero;
        var rank = hero.SpellRanks[2];
        TestAssert.Equal(3, rank, "Tides of Blood at rank three");
        for (var stacks = 0; stacks < 4; stacks++)
        {
            hero.SpellCooldowns[2] = 0.0;
            hero.Health = hero.MaxHealth;
            var before = hero.Health;
            TestAssert.True(match.Cast(2, hero), $"Tides of Blood casts with {stacks} stacks");
            match.Advance(0.5);
            var cost = before - hero.Health;
            var expected = TidesCost[rank - 1] * (1.0 + 0.5 * Math.Min(stacks, 4));
            TestAssert.Near(expected, cost, 2.5, $"health cost 50 more per stack, max 200 % ({stacks} stacks before this cast)");
            var counted = hero.Buffs.Where(buff => buff.Name == "VladimirTidesofBloodCost").Sum(buff => buff.Stacks);
            TestAssert.Equal(stacks + 1, counted, "each cast adds one Empowered stack");
        }
        match.Advance(11.0);
        TestAssert.Equal(0, hero.Buffs.Where(buff => buff.Name == "VladimirTidesofBloodCost").Sum(buff => buff.Stacks), "the stacks run out 10 s after the last cast");
    }

    [Check("scripting.bb-vladimir-hemoplague-costs-fifteen-percent-and-detonates-after-five-seconds")]
    public static void HemoplagueCostAndDetonation()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var rank = hero.SpellRanks[3];
        TestAssert.Equal(3, rank, "Hemoplague at rank three");
        hero.Health = hero.MaxHealth;
        var before = hero.Health;
        TestAssert.True(match.Cast(3, null, null, foe.Position), "Hemoplague casts on the foe's point");
        match.Advance(0.6);
        TestAssert.Near(before * 0.15, before - hero.Health, 2.5, "the cost is 15 % of his current health");
        TestAssert.True(Buff(foe, "VladimirHemoplagueDebuff") is not null, "the foe carries the debuff");
        var foeBefore = foe.Health;
        match.Advance(5.5);
        TestAssert.Near(Mitigated(HemoDamage[rank - 1], foe.SpellBlock), foeBefore - foe.Health, 3.0,
            "the detonation after 5 s: 350 at rank three, reduced by the foe's magic resistance");
    }

    /// <summary>
    /// The nova is drawn on the victim: <c>VladimirHemoplagueMissile</c> names <c>PosVar='Target'</c>, so the 0x8C source is the foe's position (300 ahead of
    /// Vladimir), while the third position stays on Vladimir, the create's target object.
    /// </summary>
    [Check("scripting.bb-vladimir-hemoplague-nova-is-drawn-on-the-victim")]
    public static void HemoplagueNovaIsDrawnOnTheVictim()
    {
        using var match = Duel();
        var caster = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(3, null, null, match.Foe.Position), "Hemoplague casts on the foe's point");
        match.Advance(6.5);
        match.Runtime.Delivery.Flush();
        var novas = caster.Packets.Select(item => item.Packet).Where(packet => EffectNamed(packet, "VladHemoplague_nova")).ToArray();
        TestAssert.True(novas.Length > 0, "the nova reaches the caster's client");
        var (dx, dz) = EffectDelta(novas[0], 33, 49);
        var expected = match.Foe.Position.X - match.Hero.Position.X;
        TestAssert.Near(expected, dx, 2.5, $"the nova is drawn on the foe (source minus Vladimir, x; expected {expected:0.#}, got {dx:0.#})");
        TestAssert.Near(0.0, dz, 2.5, "and on his line (z)");
    }

    /// <summary>
    /// Hemoplague's far rule (<c>VladimirHemoplague.SelfExecute</c>): a cast point more than 701 away (<c>CO_GREATER_THAN</c>) is replaced by the point
    /// 1050 ahead of him after <c>BBFaceDirection</c> (<c>BBGetPointByUnitFacingOffset</c>), where the placemarker <c>k</c> spawns and the child lands its
    /// 375 zone. The hook is run directly: a player's cast walks into the 700 cast range first, so the far branch is the script's own guard.
    /// </summary>
    [Check("scripting.bb-vladimir-hemoplague-beyond-701-lands-1050-ahead")]
    public static void HemoplagueFarPoint()
    {
        using var match = Duel();
        var hero = match.Hero;
        var near = match.Foe;
        var far = match.FarFoe;
        near.Position = new Position2(7550, 7000); // 350 short of the point, 500 short of the far zone
        far.Position = new Position2(8400, 7000); // 500 beyond the point, 350 beyond the far zone
        var point = new Position3(7900, 0, 7000); // 900 ahead
        match.Module.Router!.RunSelfExecute(Script("VladimirHemoplague"), hero, 3, hero.SpellRanks[3], 0.0, point, point);
        // The engine names a spawned unit by its skin (BbNativeHandlers.Spawn), so the placemarker k is the TestCubeRender placemarker.
        var marker = match.Runtime.World.Entities.Values.Where(unit => unit is { Placemarker: true, Name: "TestCubeRender", Alive: true }).ToList();
        TestAssert.Equal(1, marker.Count, "one placemarker k");
        TestAssert.Near(8050.0, marker[0].Position.X, 2.0, "the placemarker stands 1050 ahead of him");
        TestAssert.Near(7000.0, marker[0].Position.Z, 2.0, "on his facing");
        match.Advance(0.5);
        TestAssert.True(Buff(far, "VladimirHemoplagueDebuff") is not null, "the unit near the far zone is plagued");
        TestAssert.True(Buff(near, "VladimirHemoplagueDebuff") is null, "the unit near the cast point is not");
    }

    /// <summary>A Transfusion that kills its target still heals him: the heal child is cast after the damage, from the target's spot, at Vladimir.</summary>
    [Check("scripting.bb-vladimir-transfusion-that-kills-still-heals")]
    public static void TransfusionKillHeals()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Health = hero.MaxHealth * 0.5;
        foe.Health = 40.0;
        var before = hero.Health;
        TestAssert.True(match.Cast(0, foe), "Transfusion casts");
        match.Advance(1.0);
        TestAssert.False(foe.Alive, "the foe dies");
        TestAssert.Near(TransfusionHeal[hero.SpellRanks[0] - 1] + 0.25 * hero.AbilityPower, hero.Health - before, 2.0, "the heal lands anyway");
    }

    /// <summary>
    /// A dead Vladimir comes back: the death timer of a lethal hit runs out and he respawns at full health (nothing in his scripts holds him).
    /// The lethal hit is the death path that schedules the respawn (<c>CombatService.ApplyHit</c>); <c>Combat.Kill</c> alone is the lifecycle call
    /// that schedules none, which is why the scripted Vladimir of the first <c>S-vladimir</c> runs never came back.
    /// </summary>
    [Check("scripting.bb-vladimir-respawns-after-a-death")]
    public static void Respawns()
    {
        using var match = Duel();
        var hero = match.Hero;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, hero.Health + 1000.0, DamageType.True, DamageSourceKind.Spell);
        match.Advance(0.5);
        TestAssert.False(hero.Alive, "dead");
        match.Advance(60.0);
        TestAssert.True(hero.Alive, $"alive within 60 s of a level-{hero.Level} death: health {hero.Health}");
        TestAssert.Near(hero.MaxHealth, hero.Health, 1.0, "at full health");
    }

    private static readonly double[] TidesDamage = [60.0, 90.0, 120.0, 150.0, 180.0];

    /// <summary>
    /// Tides of Blood's nuke: <c>VladimirTidesofBlood.SelfExecute</c> casts extra slot 4 (<c>VladimirTidesofBloodNuke</c>, a homing missile child)
    /// at every enemy in 620, whose <c>TargetExecute</c> deals 60 to 180 x (1 + 0.25 per Empowered stack counted before the cast) + 0.45 AP.
    /// </summary>
    [Check("scripting.bb-vladimir-tides-of-blood-hits-every-enemy-around-him")]
    public static void TidesHitsTheEnemiesAround()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var rank = hero.SpellRanks[2];
        for (var stacks = 0; stacks < 2; stacks++)
        {
            hero.SpellCooldowns[2] = 0.0;
            var hits = HitsOn(match, "Foe").Count;
            TestAssert.True(match.Cast(2, hero), $"Tides of Blood casts with {stacks} stacks");
            match.Advance(1.0);
            var dealt = HitsOn(match, "Foe").Skip(hits).Sum(hit => hit.Dealt);
            var raw = TidesDamage[rank - 1] * (1.0 + 0.25 * stacks) + 0.45 * hero.AbilityPower;
            TestAssert.Near(Mitigated(raw, foe.SpellBlock), dealt, 2.0, $"the nuke hits the foe in 620 ({stacks} stacks before the cast): {Names(hero)}");
        }
    }

    /// <summary>
    /// The Tides nuke buff on Vladimir (<c>VladimirTidesofBloodNuke.BuffOnHeal</c>, 10 s) raises his heals by 8 % per Empowered stack:
    /// Transfusion's heal child heals 35 x 1.08 at rank three after one cast of Tides of Blood, and its damage is unchanged.
    /// </summary>
    [Check("scripting.bb-vladimir-transfusion-heals-more-after-tides-of-blood")]
    public static void TransfusionAfterTides()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var rank = hero.SpellRanks[0];
        TestAssert.True(match.Cast(2, hero), "Tides of Blood casts");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "VladimirTidesofBloodNuke") is not null, "the nuke buff is on Vladimir");
        hero.Health = hero.MaxHealth * 0.5;
        var healthBefore = hero.Health;
        var hits = HitsOn(match, "Foe").Count;
        TestAssert.True(match.Cast(0, foe), "Transfusion casts");
        match.Advance(1.0);
        var damage = HitsOn(match, "Foe").Skip(hits).Sum(hit => hit.Dealt);
        TestAssert.Near(Mitigated(TransfusionDamage[rank - 1] + 0.6 * hero.AbilityPower, foe.SpellBlock), damage, 2.0, "Transfusion's damage");
        TestAssert.Near((TransfusionHeal[rank - 1] + 0.25 * hero.AbilityPower) * 1.08, hero.Health - healthBefore, 2.0,
            "the heal, 8 % more for the one Empowered stack");
    }

    /// <summary>
    /// Sanguine Pool (<c>VladimirSanguinePool</c>): the 2 s buff's <c>OnBuffActivate</c> makes him untargetable (SetTargetable false), its
    /// <c>BuffOnUpdateActions</c> slows the enemies in 350 (the <c>Slow</c> buff every 0.25 s) and hits them every 0.5 s for <c>DamageTick</c>
    /// (20 to 75 by rank + 0.0375 bonus health), healing 15 % of it. <c>BBRequireVar</c> at the head of the activation is the base file's empty function.
    /// </summary>
    [Check("scripting.bb-vladimir-sanguine-pool-hides-him-slows-and-drains")]
    public static void SanguinePoolActs()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var rank = hero.SpellRanks[1];
        var tick = new[] { 20.0, 33.75, 47.5, 61.25, 75.0 }[rank - 1] + 0.0375 * (hero.MaxHealth - (400.0 + 85.0 * hero.Level));
        TestAssert.True(match.Cast(1, hero), "Sanguine Pool casts");
        match.Advance(0.3);
        TestAssert.True(Buff(hero, "VladimirSanguinePool") is not null, $"the pool buff is on Vladimir: {Names(hero)}");
        TestAssert.True(hero.AnyBuffOrStatus(buff => buff.Payload.Untargetable), "he cannot be targeted in the pool");
        TestAssert.True(Buff(foe, "Slow") is not null, $"the foe in 350 is slowed: {Names(foe)}");
        match.Advance(2.2);
        var hits = HitsOn(match, "Foe").Where(hit => hit.Dealt > 0.0).ToList();
        TestAssert.True(hits.Count >= 4, $"a hit every 0.5 s for 2 s: {hits.Count}");
        TestAssert.Near(Mitigated(tick, foe.SpellBlock), hits[0].Dealt, 1.0, "each pulse deals DamageTick");
        TestAssert.False(hero.AnyBuffOrStatus(buff => buff.Payload.Untargetable), "targetable again after the pool");
    }

    /// <summary>
    /// Crimson Pact (<c>VladimirBloodGorged</c>): <c>BuffOnUpdateActions</c> sets APMod = FlatHPPoolMod x 0.025 and HPMod = (FlatMagicDamageMod - APMod)
    /// x 1.8, so the bonus health follows only the ability power that is not the passive's own (and <c>BBIncMaxHealth</c> with IncCurrentHealth
    /// false is the non-healing pool, which <c>GetFlatHPPoolMod</c> does not read): a Ruby Crystal gives 4.5 AP and no health back.
    /// </summary>
    [Check("scripting.bb-vladimir-crimson-pact-trades-health-and-power")]
    public static void CrimsonPact()
    {
        using var match = Duel();
        var hero = match.Hero;
        void Wear(params int[] items)
        {
            hero.Inventory = [.. items.Select(id => (InventorySlot?)new InventorySlot(id)), .. Enumerable.Repeat<InventorySlot?>(null, 6 - items.Length)];
            match.Runtime.HeroStats.Refresh(hero, push: false);
            match.Advance(1.0);
        }
        Wear();
        var baseHealth = hero.MaxHealth;
        TestAssert.Near(0.0, hero.AbilityPower, 1e-6, "no items: no ability power");
        Wear(1052);
        var tomeAp = hero.AbilityPower;
        TestAssert.True(tomeAp > 1.0, "the Amplifying Tome gives ability power");
        TestAssert.Near(baseHealth + 1.8 * tomeAp, hero.MaxHealth, 0.5, "1.8 bonus health per point of the tome's ability power");
        Wear(1028);
        TestAssert.Near(180.0 * 0.025, hero.AbilityPower, 0.05, "a Ruby Crystal's 180 health gives 4.5 ability power");
        TestAssert.Near(baseHealth + 180.0, hero.MaxHealth, 0.5, "and no health back: the passive's own power is subtracted");
        Wear(1028, 1052);
        TestAssert.Near(tomeAp + 4.5, hero.AbilityPower, 0.05, "both items: the tome's power plus 4.5");
        TestAssert.Near(baseHealth + 180.0 + 1.8 * tomeAp, hero.MaxHealth, 0.5, "both items: 180 plus 1.8 per tome point");
    }
}
