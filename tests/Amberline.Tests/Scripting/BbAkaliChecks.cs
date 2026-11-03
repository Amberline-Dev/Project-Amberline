using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (26 October 2026, batch 251): Akali plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written
/// module ever existed; she is not a retail bot). Expected numbers are the literals of the shipped scripts and spell inibins: the tables of
/// <c>CharScriptAkali</c> and of the spell scripts (<c>AkaliMota</c> 50..150 and energy 20..40, <c>AkaliSmokeBomb</c> armour 10..50 and
/// slow 14..30 percent, <c>AkaliShadowSwipe</c> 30..130, <c>AkaliShadowDance</c> dash damage 100/175/250). The scenario <c>S-akali</c> shows
/// the same facts, and the death and respawn, on a headless match.
/// </summary>
public static class BbAkaliChecks
{
    // The closure's shared system buffs: the closure check counts them, the engine runs their shipped scripts as well.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    /// <summary>A match with Akali at rank <paramref name="rank"/> and 80 ability power, the enemy 100 away (inside every range that is checked).</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("Akali", foeChampion: "Orianna");
        match.Learn(rank);
        match.GiveAbilityPower();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Foe.Armor = 20.0;
        match.FarFoe.Position = new Position2(7500, 7000);
        match.FarFoe.SpellBlock = 30.0;
        match.FarFoe.Armor = 20.0;
        match.Refill();
        return match;
    }

    // The shipped add of N charges (BBSpellBuffAdd with NumberOfStacks N, E29): N applications, each a stack of its own.
    private static void AddCharges(Match match, int count)
    {
        for (var charge = 0; charge < count; charge++)
            match.Module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Hero, "AkaliShadowDance",
                (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BuffType.AmmoStack, 4, 25000.0, 0.0, false, new BbTable(), 1, true));
    }

    private static string Describe(Entity hero) =>
        string.Join(";", hero.Buffs.Where(buff => buff.Name == "AkaliShadowDance").Select(buff => $"{buff.Stacks}/{buff.CasterNetId}/{buff.Type}/{hero.NetId}"));

    private static int Hits(Match match, string victim) => HitsOn(match, victim).Count;

    private static int Charges(Entity hero) => Buff(hero, "AkaliShadowDance")?.Stacks ?? 0;

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-akali-module-binds-the-shipped-closure")]
    public static void ModuleBindsTheShippedClosure()
    {
        var module = new BbChampionModule("Akali");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Akali");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        foreach (var name in new[] { "CharScriptAkali", "AkaliMota", "AkaliSmokeBomb", "AkaliShadowSwipe", "AkaliShadowDance", "AkaliTwinDisciplines",
                     "AkaliTwinAP", "AkaliTwinDmg", "AkaliShadowDanceKick", "AkaliSBStealth", "AkaliSmokeBombInternal", "AkaliSBBuff", "AkaliSBDebuff", "AkaliHoldStealth" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        TestAssert.Equal(26, closure.ScriptNames.Count(), "the closure is 26 scripts: the character, four spells, the passive and the scripts they reference");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of them are the shared system buffs");

        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native an Akali hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-akali-spells-are-the-shipped-slots-costs-and-ranks")]
    public static void SpellsAreTheShippedSlotsCostsAndRanks()
    {
        var module = new BbChampionModule("Akali");
        var hero = new Entity { ChampionName = "Akali" };
        TestAssert.Equal(["AkaliMota", "AkaliSmokeBomb", "AkaliShadowSwipe", "AkaliShadowDance"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Akali.inibin (the icons say Twilight Shroud and Crescent Slash)");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the dash of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");
        TestAssert.Equal(60.0, GameData.Default.Spell("AkaliMota", 5).ManaCosts[0], "Mark of the Assassin costs 60 energy");
        TestAssert.Equal(80.0, GameData.Default.Spell("AkaliSmokeBomb", 5).ManaCosts[0], "Twilight Shroud costs 80 energy");
        TestAssert.Equal(60.0, GameData.Default.Spell("AkaliShadowSwipe", 5).ManaCosts[0], "Crescent Slash costs 60 energy");
        TestAssert.Equal(0.0, GameData.Default.Spell("AkaliShadowDance", 3).ManaCosts[0], "Shadow Dance costs no energy (its charges are the cost)");
        TestAssert.Equal(6.0, GameData.Default.Spell("AkaliMota", 5).Cooldowns[0], "Mark of the Assassin: cooldown 6 at rank one");
        TestAssert.Equal(20.0, GameData.Default.Spell("AkaliSmokeBomb", 5).Cooldowns[0], "Twilight Shroud: cooldown 20 at rank one");
        TestAssert.Equal(7.0, GameData.Default.Spell("AkaliShadowSwipe", 5).Cooldowns[0], "Crescent Slash: cooldown 7 at rank one");
        TestAssert.Equal(2.0, GameData.Default.Spell("AkaliShadowDance", 3).Cooldowns[0], "Shadow Dance: cooldown 2 at rank one");

        var info = ChampionCatalog.ByName("Akali")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 84, RetailBot: false }, "playable in the middle for 3150 IP, not a retail bot");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Mark of the Assassin (Q): hits on cast, detonates on the next hit of hers
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-akali-mark-of-the-assassin-hits-on-cast-and-detonates-on-her-next-hit")]
    public static void MarkHitsOnCastAndDetonatesOnHerNextHit()
    {
        using var match = Arena();
        var expected = Mitigated(50.0 + 0.4 * match.Hero.AbilityPower, 30.0);
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(0.5);
        TestAssert.Equal(1, Hits(match, "Foe"), "the cast hits once");
        TestAssert.Near(expected, HitsOn(match, "Foe").Single().Dealt, 0.05, "50 plus 0.4 ability power, magic, through 30 magic resistance");
        TestAssert.True(Buff(match.Foe, "AkaliMota") is not null, "the mark sits on the target for six seconds");

        var raw = 50.0 + 0.4 * match.Hero.AbilityPower;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "her attack is queued");
        match.Advance(1.5);
        var detonations = HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Raw - raw) < 0.05).ToArray();
        TestAssert.Equal(2, detonations.Length, "the cast and the detonation are the only hits of that size (the attack itself is a basic hit)");
        TestAssert.Near(expected, detonations[^1].Dealt, 0.05, "the detonation deals the same damage as the cast");
        TestAssert.True(Buff(match.Foe, "AkaliMota") is null, "the mark is gone once it detonates");

        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a second attack is queued");
        match.Advance(1.5);
        TestAssert.Equal(2, HitsOn(match, "Foe").Count(hit => Math.Abs(hit.Raw - raw) < 0.05), "the second attack detonates nothing: the mark detonates once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Twilight Shroud (W): a smoke zone of stealth for her, armour, and a slow for the enemies inside
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Batch 583 (the record's "energy numbers"): Akali.inibin has PARType Energy, BaseMP 200, MPPerLevel 0 and BaseStaticMPRegen 10 (a second,
    /// as every shipped regeneration figure is read); Twilight Shroud costs 80 / 75 / 70 / 65 / 60 (AkaliSmokeBomb ManaCost1..5), the other spells
    /// a flat 60 (Mark of the Assassin, Crescent Slash) or nothing (Shadow Dance).
    /// </summary>
    [Check("scripting.bb-akali-energy-is-200-regenerates-ten-a-second-and-shroud-costs-by-rank")]
    public static void EnergyPoolRegenerationAndShroudCost()
    {
        TestAssert.Equal([80.0, 75.0, 70.0, 65.0, 60.0], GameData.Default.Spell("AkaliSmokeBomb", 5).ManaCosts.Take(5).ToArray(), "Twilight Shroud by rank");
        using var match = Arena(3);
        var hero = match.Hero;
        hero.Level = 18;
        match.Runtime.HeroStats.Refresh(hero, push: false);
        TestAssert.Near(200.0, hero.MaxMana, 1e-6, "200 energy at level 18 (no growth)");
        TestAssert.Near(10.0, hero.ManaRegen, 1e-6, "10 energy a second");
        hero.Mana = 100.0;
        match.Advance(1.0);
        TestAssert.Near(110.0, hero.Mana, 0.5, "a second regenerates 10");
        hero.Mana = hero.MaxMana;
        TestAssert.True(match.Cast(1, at: hero.Position), "W casts at her feet at rank three");
        match.Advance(match.CastFrameOf(1) + Step);
        TestAssert.Near(130.0, hero.Mana, 1.0, "rank three costs 70");
    }

    [Check("scripting.bb-akali-twilight-shroud-stealths-her-and-arms-her-inside-the-smoke")]
    public static void SmokeStealthsHerAndArmsHer()
    {
        using var match = Arena();
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        var armor = match.Hero.Armor;
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "W casts at her feet");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Hero, "AkaliSBStealth") is not null, "the smoke stealths her after its quarter second");
        TestAssert.True(VisibilityRules.IsStealthed(match.Hero), "she is stealthed");
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        TestAssert.Near(armor + 10.0, match.Hero.Armor, 1e-6, "the smoke adds 10 armour at rank one");

        match.Hero.Position = new Position2(9000, 9000);
        match.Advance(1.2);
        TestAssert.False(VisibilityRules.IsStealthed(match.Hero), "she walks out of the smoke and is visible again after its half second");
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        TestAssert.Near(armor, match.Hero.Armor, 1e-6, "the armour goes with the smoke");
    }

    [Check("scripting.bb-akali-twilight-shroud-slows-the-enemies-inside-and-not-the-allies")]
    public static void SmokeSlowsTheEnemiesInside()
    {
        using var match = Arena();
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        var speed = match.Foe.Speed;
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "W casts at her feet");
        match.Advance(1.2);
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        TestAssert.True(Buff(match.Foe, "AkaliSBDebuff") is not null, "an enemy inside the smoke carries the debuff");
        TestAssert.Near(speed * (1.0 - 0.14), match.Foe.Speed, 0.5, "14 percent movement speed less at rank one (the multiplicative slow)");
        TestAssert.True(Buff(match.Ally, "AkaliSBDebuff") is null && Buff(match.Ally, "AkaliSBBuff") is null, "an ally inside gets neither the slow nor the armour");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Crescent Slash (E): a self area, physical damage from her attack damage and ability power
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-akali-crescent-slash-hits-the-units-inside-its-area-only")]
    public static void CrescentSlashHitsTheUnitsInsideItsArea()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7400, 7000);
        var expected = Mitigated(30.0 + 0.6 * match.Hero.AttackDamage + 0.3 * match.Hero.AbilityPower, match.Foe.Armor);
        TestAssert.True(match.Cast(2, at: match.Hero.Position), "E casts");
        match.Advance(0.5);
        TestAssert.Near(expected, HitsOn(match, "Foe").Single().Dealt, 0.05, "30 plus 0.6 attack damage plus 0.3 ability power, physical, through 20 armour");
        TestAssert.Equal(0, Hits(match, "FarFoe"), "400 away is outside the 325 area");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shadow Dance (R): charges, a dash to her target and damage on arrival
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-akali-shadow-dance-dashes-to-its-target-and-hits-on-arrival-for-one-charge")]
    public static void ShadowDanceDashesAndSpendsACharge()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7500, 7000);
        AddCharges(match, 2);
        TestAssert.Equal(2, Charges(match.Hero), "two charges at the start of the test");
        TestAssert.True(match.CanCast(3, match.Foe).Allowed, "the dance casts with two charges");
        TestAssert.True(match.Cast(3, match.Foe), "R casts at the enemy 500 away");
        TestAssert.Equal(1, Charges(match.Hero), "the cast itself spends one charge");
        match.Advance(0.2);
        TestAssert.Equal(1, Charges(match.Hero), "the dash keeps the one charge");
        match.Advance(0.8);
        var kick = HitsOn(match, "Foe").First();
        TestAssert.Near(Mitigated(100.0 + 0.5 * match.Hero.AbilityPower, 30.0), kick.Dealt, 0.05, "the arrival deals 100 plus 0.5 ability power, magic (rank one)");
        TestAssert.True(Distance(match.Hero, match.Foe) < 250.0, "she lands beside her target");
        TestAssert.Equal(1, Charges(match.Hero), "one charge is spent");
        TestAssert.False(match.CanCast(3, match.Foe).Allowed, "a single charge is not enough to dance");
    }

    [Check("scripting.bb-akali-shadow-dance-charges-grow-with-kills-and-stop-at-four")]
    public static void ShadowDanceChargesGrowWithKills()
    {
        using var match = Arena(rank: 3);
        AddCharges(match, 2);
        var first = Enemy(match, "FirstVictim", new Position2(7300, 7300), UnitType.Hero, Team.Chaos);
        match.Runtime.Combat.Kill(first, match.Hero.NetId);
        match.Advance(0.1);
        TestAssert.Equal(3, Charges(match.Hero), "a hero kill adds a charge");
        var second = Enemy(match, "SecondVictim", new Position2(7300, 7400), UnitType.Hero, Team.Chaos);
        match.Runtime.Combat.Kill(second, match.Hero.NetId);
        match.Advance(0.1);
        TestAssert.Equal(4, Charges(match.Hero), "the fourth charge");
        var third = Enemy(match, "ThirdVictim", new Position2(7300, 7500), UnitType.Hero, Team.Chaos);
        match.Runtime.Combat.Kill(third, match.Hero.NetId);
        match.Advance(0.1);
        TestAssert.Equal(4, Charges(match.Hero), "four charges is the most she keeps");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Twin Disciplines (passive): proc damage by ability power, spell vamp by attack damage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-akali-twin-disciplines-procs-a-share-of-her-attack-damage-by-ability-power")]
    public static void TwinDisciplinesProcsByAbilityPower()
    {
        using var match = Arena();
        match.Advance(1.2);
        TestAssert.True(Buff(match.Hero, "AkaliTwinDisciplines") is not null, "the passive is on from the start");
        TestAssert.True(Buff(match.Hero, "AkaliTwinAP") is not null, "80 ability power is above 19.5: the magic proc is on");
        var ratio = 0.1 + (match.Hero.AbilityPower - 20.0) / 500.0;
        var proc = match.Hero.AttackDamage * ratio;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "her attack is queued");
        match.Advance(1.5);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Raw - proc) < 0.05),
            $"the attack procs attack damage x {ratio:0.###} (ability power 20 plus 0.1, over 500) as magic");
    }

    /// <summary>
    /// Twin Disciplines' other half (batch 251 left it to the hand test, the harness had no flat attack damage; batch 309 gives it a B.F. Sword):
    /// AkaliTwinDisciplines adds AkaliTwinDmg at 9.5 flat attack damage or more, whose BuffOnUpdateStats raises IncPercentSpellVampMod by
    /// 0.1 + (flat AD - 10) / 500; a spell heals her by that share of the damage it deals.
    /// </summary>
    [Check("scripting.bb-akali-twin-disciplines-spell-vamp-follows-her-bonus-attack-damage")]
    public static void TwinDisciplinesSpellVampByAttackDamage()
    {
        using var match = new Match("Akali", foeChampion: "Orianna");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Hero.Inventory = [new InventorySlot(1038), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Refill();
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "AkaliTwinDmg") is not null, "45 flat attack damage (B.F. Sword) is above 9.5: the vamp half is on");
        TestAssert.Near(0.1 + (45.0 - 10.0) / 500.0, match.Hero.SpellVamp, 1e-6, "spell vamp 0.1 plus (45 - 10) / 500");
        match.Hero.Health = match.Hero.MaxHealth - 300.0;
        var before = match.Hero.Health;
        var regen = match.Hero.HealthRegen * BbM3.Step;
        TestAssert.True(match.Cast(0, match.Foe), "Mark of the Assassin is cast at the foe");
        var dealt = 0.0;
        for (var tick = 0; tick < 60 && dealt <= 0.0; tick++)
        {
            before = match.Hero.Health + regen;
            match.Advance(BbM3.Step);
            dealt = HitsOn(match, "Foe").Sum(hit => hit.Dealt);
        }
        TestAssert.Near(Mitigated(50.0, 30.0), dealt, 0.05, "the dart deals 50 magic at rank one through 30 resist");
        TestAssert.Near(0.17 * dealt, match.Hero.Health - before, 0.05, "she heals 17 percent of the damage dealt");
    }

    /// <summary>
    /// The recharge of Shadow Dance (batch 251 left it unmeasured; batch 309). The first rank (CharOnLevelUpSpell) gives two charges, a stack whose
    /// Duration is 25; at its end AkaliShadowDance's BuffOnUpdateAmmo adds the next one, timed by CharScriptAkali's DanceTimerCooldown (25 / 20 / 15),
    /// and from three charges on the stack it adds is permanent (25000): a charge every 25 s at rank one, up to four.
    /// </summary>
    [Check("scripting.bb-akali-shadow-dance-recharges-a-charge-every-25-seconds-at-rank-one")]
    public static void ShadowDanceRechargesEveryTwentyFiveSeconds()
    {
        using var match = new Match("Akali");
        var experience = Amberline.Simulation.Combat.ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "Akali reaches level six");
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "Shadow Dance takes its first rank");
        int Charges() => match.Hero.Buffs.Where(buff => buff.Name == "AkaliShadowDance").Sum(buff => buff.Stacks);
        TestAssert.Equal(2, Charges(), "the first rank gives two charges");
        match.Advance(24.5);
        TestAssert.Equal(2, Charges(), "no charge before 25 s");
        match.Advance(1.0);
        TestAssert.Equal(3, Charges(), "the third charge comes at 25 s");
        match.Advance(24.0);
        TestAssert.Equal(3, Charges(), "no fourth charge before another 25 s");
        match.Advance(1.0);
        TestAssert.Equal(4, Charges(), "the fourth charge comes 25 s later");
        match.Advance(30.0);
        TestAssert.Equal(4, Charges(), "four is the cap");
    }

    private static double Distance(Entity a, Entity b) => Math.Sqrt(Math.Pow(a.Position.X - b.Position.X, 2) + Math.Pow(a.Position.Z - b.Position.Z, 2));
}
