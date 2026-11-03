using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 454: The Crystal Scar's (map 8) own economy. Ambient gold and experience, the experience curve and the death timers come
/// from the map's shipped files (LEVELS/Map8/Constants.var, ExpCurve.inibin, DeathTimes.inibin); Summoner's Rift's (map 1) numbers
/// are the guards. Each map builds its own game data and combat service, so the checks do not depend on the process default.
/// </summary>
public static class MapEconomyChecks
{
    [Check("combat.map-8-ambient-experience-ticks-from-its-own-constants")]
    public static void MapEightPaysAmbientExperienceFromItsOwnRateFromTheStart()
    {
        // ai_AmbientXPDelay 0, ai_AmbientXPInterval 1, ai_AmbientXPAmount 8 (LEVELS/Map8/Constants.var:75-77): 8 XP a second from 0 s.
        var (combat, _, hero) = CreateMatch(8);
        Tick(combat, from: 0.0, to: 10.0);
        TestAssert.Near(80.0, hero.Experience, 1e-6, "map 8 pays 8 XP a second from the first second, 80 in 10 s");
    }

    [Check("combat.map-8-ambient-gold-is-its-own-rate-from-ninety-seconds")]
    public static void MapEightPaysAmbientGoldAtItsOwnRate()
    {
        // ai_AmbientGoldDelay 90, ai_AmbientGoldInterval 5, ai_AmbientGoldAmount 28 (Map8/Constants.var:71-73): 5.6 gold a second.
        var (combat, _, hero) = CreateMatch(8);
        Tick(combat, from: 0.0, to: 100.0);
        TestAssert.Near(56.0, hero.Gold, 1e-3, "map 8 pays 5.6 gold a second from 90 s, 56 gold by 100 s");
    }

    [Check("combat.map-8-experience-curve-is-its-own-shipped-curve")]
    public static void MapEightLevelsFromItsOwnExperienceCurve()
    {
        // ExpCurve.inibin on map 8 reaches level 4 at 880 XP (Map1 needs 1170 for level 4 and so stops at level 3 with 880).
        var (combat, _, eight) = CreateMatch(8);
        combat.AwardHeroProgress(eight, experience: 880.0);
        TestAssert.Equal(4, eight.Level, "map 8's curve: 880 XP is level 4");

        var (mapOneCombat, _, one) = CreateMatch(1);
        mapOneCombat.AwardHeroProgress(one, experience: 880.0);
        TestAssert.Equal(3, one.Level, "Summoner's Rift's curve: 880 XP is level 3");
    }

    /// <summary>
    /// Batch 592: the Basic Tutorial's experience is map 3's own steep curve (LEVELS/Map3/ExpCurve.inibin: level 2 at 100, 3 at 6200, 4 at
    /// 20000, 5 at 70000, 6 at 80000, 7 at 90000), so EventLevelUp's <c>IncExp(playerID, 2000)</c> after the opening kills leaves Ashe at
    /// level 2 with 2300 XP, and OnTutorialInit's 90000 and 30000 make Nunu level 7 and Master Yi level 4.
    /// </summary>
    [Check("map3.tutorial-experience-follows-its-shipped-curve")]
    public static void TutorialLevelsFromMapThreesOwnCurve()
    {
        var rules = MapProgressionRules.Load(GameDataPaths.Default.ForMap(GameMaps.Tutorial));
        TestAssert.Equal("0,100,6200,20000,70000,80000,90000",
            string.Join(",", Enumerable.Range(1, 7).Select(level => rules.ExperienceThreshold(level).ToString(System.Globalization.CultureInfo.InvariantCulture))),
            "map 3's thresholds for levels 1 to 7");
        var (combat, _, ashe) = CreateMatch(GameMaps.Tutorial);
        combat.AwardHeroProgress(ashe, experience: 300.0);
        TestAssert.Equal(2, ashe.Level, "the opening kills' 300 XP pass map 3's level-2 threshold (100)");
        combat.AwardHeroProgress(ashe, experience: 2000.0);
        TestAssert.Equal(2, ashe.Level, "IncExp(playerID, 2000): 2300 XP stays level 2, level 3 needs 6200");
        foreach (var (experience, level) in new[] { (90000.0, 7), (30000.0, 4) })
        {
            var (bots, _, bot) = CreateMatch(GameMaps.Tutorial);
            var points = bot.SkillPoints;
            bots.AwardHeroProgress(bot, experience: experience);
            TestAssert.Equal(level, bot.Level, $"{experience} XP is level {level} on map 3");
            TestAssert.Equal(points + level - 1, bot.SkillPoints, "one skill point for each level gained");
        }
    }

    [Check("combat.map-8-death-timer-is-flat-at-its-shipped-value-late-in-the-match")]
    public static void MapEightDeathTimerIsTwentySixSecondsAndDoesNotScale()
    {
        // DeathTimes.inibin on map 8: TimeDeadPerLevel 26 for every level, and the scaling keys are 0 (no scaling). Late in the match
        // (3000 s) Summoner's Rift's scaled timer would be 18 for a level-1 hero (12 x 1.5 cap); map 8's stays 26.
        var (combat, _, hero) = CreateMatch(8);
        Tick(combat, from: 0.0, to: 3000.0);
        // The lethal-damage path: the hero is dead, then its respawn is scheduled from the death timer.
        hero.Alive = false;
        combat.ScheduleRespawn(hero);
        TestAssert.Near(26.0, hero.RespawnAt - combat.CurrentTime, 1e-9,
            "map 8 death timer at level 1, 3000 s in, is its shipped 26 seconds");
    }

    [Check("combat.map-8-deaths-respawn-in-waves-per-team")]
    public static void MapEightDeathsRespawnInWavesPerTeam()
    {
        // Batch 584: DeathTimes.inibin on map 8 ships DeathWaveRespawn*WaveRespawnInterval 12, which the client's shared
        // death timer manager (one manager per team) turns into respawn waves:
        // a death more than 12 s after the team's wave start opens a new wave, and every death of one wave respawns with the first.
        var (combat, transport, heroes) = CreateDeathMatch(8);
        var (killer, a, b, c, d, chaos) = (heroes[0], heroes[1], heroes[2], heroes[3], heroes[4], heroes[5]);
        double Kill(Entity victim, Entity source, double at)
        {
            Tick(combat, from: combat.CurrentTime, to: at);
            combat.ApplyHit(source.NetId, victim.NetId, 100000.0, DamageType.True);
            TestAssert.False(victim.Alive, $"hero {victim.NetId} dies at {at} s");
            TestAssert.Near(victim.RespawnAt - combat.CurrentTime, transport.LastDeathSeconds, 1e-9,
                $"hero {victim.NetId}: the death packet carries the scheduled timer");
            return victim.RespawnAt;
        }
        TestAssert.Near(126.0, Kill(a, killer, 100.0), 1e-9, "the first Order death opens a wave: 26 s, back at 126 s");
        TestAssert.Near(126.0, Kill(b, killer, 105.0), 1e-9, "an Order death 5 s later joins it: 21 s, back at 126 s");
        TestAssert.Near(131.0, Kill(chaos, c, 105.0), 1e-9, "Chaos keeps its own wave: 26 s from 105 s");
        TestAssert.Near(126.0, Kill(c, killer, 112.0), 1e-9, "12 s after the wave start is still the same wave: 14 s");
        TestAssert.Near(139.0, Kill(d, killer, 113.0), 1e-9, "13 s after it a new wave opens: 26 s, back at 139 s");

        // Summoner's Rift ships no interval: every death takes its full timer.
        var (riftCombat, _, rift) = CreateDeathMatch(1);
        Tick(riftCombat, from: 0.0, to: 100.0);
        riftCombat.ApplyHit(rift[0].NetId, rift[1].NetId, 100000.0, DamageType.True);
        Tick(riftCombat, from: 100.0, to: 105.0);
        riftCombat.ApplyHit(rift[0].NetId, rift[2].NetId, 100000.0, DamageType.True);
        TestAssert.Near(12.0, rift[2].RespawnAt - riftCombat.CurrentTime, 1e-9, "map 1: a second death 5 s later still takes 12 s");
    }

    private static (CombatService Combat, CombatChecks.TestTransport Transport, Entity[] Heroes) CreateDeathMatch(int mapId)
    {
        // A Chaos killer, four Order heroes and a Chaos hero, all level 1 (map 8's timer is 26 at every level).
        var data = new GameData(GameDataPaths.Default.ForMap(mapId));
        var clock = new GameClock();
        clock.SetVirtual(0.0);
        var world = new WorldState(clock);
        var transport = new CombatChecks.TestTransport();
        var stats = new HeroStatsService(data, new ItemCatalog(),
            new CombatChecks.LifecycleStatsHooks(), new CombatChecks.LifecycleStatsTransport());
        var combat = new CombatService(world, new CombatChecks.TestHooks(), transport, new FixedRandomSource(), stats, data);
        var teams = new[] { Team.Chaos, Team.Order, Team.Order, Team.Order, Team.Order, Team.Chaos };
        var heroes = teams.Select((team, index) => new Entity((uint)(index + 1), EntityKind.Hero, UnitType.Hero, team, new Position2(0.0, 0.0))
        {
            Level = 1, MaxHealth = 500.0, Health = 500.0, MaxMana = 100.0, Mana = 100.0,
        }).ToArray();
        foreach (var hero in heroes) world.Register(hero);
        return (combat, transport, heroes);
    }

    [Check("combat.map-4-absent-ambient-experience-keys-take-the-client-defaults")]
    public static void TwistedTreelineBuildsAndPaysNoAmbientExperience()
    {
        // Map4/Constants.var has no ai_AmbientXP* keys (its ai_AmbientGold* keys are present). The client's initialisers apply:
        // Delay 0, Interval 5, Amount 0, so the combat service builds and pays no ambient experience.
        TestAssert.Near(5.0, new GameData(GameDataPaths.Default.ForMap(4)).LevelConstants.Values["ai_AmbientXPInterval"], 1e-9,
            "map 4's ai_AmbientXPInterval is the client default 5");
        var (combat, _, hero) = CreateMatch(4);
        Tick(combat, from: 0.0, to: 60.0);
        TestAssert.Near(0.0, hero.Experience, 1e-9, "map 4 ships no ambient experience (the client default Amount is 0)");
    }

    [Check("combat.map-1-economy-stays-summoners-rift-values")]
    public static void SummonersRiftKeepsItsEconomyValues()
    {
        var (combat, _, hero) = CreateMatch(1);
        Tick(combat, from: 0.0, to: 10.0);
        TestAssert.Near(0.0, hero.Experience, 1e-9, "Summoner's Rift ships ai_AmbientXPAmount 0: no ambient experience");

        var (goldCombat, _, goldHero) = CreateMatch(1);
        Tick(goldCombat, from: 0.0, to: 100.0);
        TestAssert.Near(13.0, goldHero.Gold, 1e-3, "Summoner's Rift pays 6.5 gold per 5 s from 90 s: 13 gold by 100 s");

        var (deathCombat, _, deathHero) = CreateMatch(1);
        deathHero.Alive = false;
        deathCombat.ScheduleRespawn(deathHero);
        TestAssert.Near(12.0, deathHero.RespawnAt - deathCombat.CurrentTime, 1e-9, "Summoner's Rift level-1 death timer is 12");
    }

    [Check("data.map-8-starting-gold-and-its-item-rules-come-from-its-files")]
    public static void MapEightStartingGoldAndItemRulesAreItsOwn()
    {
        // Constants.var ai_StartingGold 1375 (Map8:46), and Items.inibin UnpurchasableItemList holds Doran's Blade (1055) on map 8 only.
        var eight = new GameData(GameDataPaths.Default.ForMap(8));
        TestAssert.Near(1375.0, eight.LevelConstants.Values["ai_StartingGold"], 1e-9, "map 8 starting gold");
        TestAssert.False(MapItemRules.Load(GameDataPaths.Default.ForMap(8)).AllowsPurchase(1055),
            "map 8 refuses Doran's Blade (its Items.inibin lists it as unpurchasable)");
        TestAssert.True(MapItemRules.Load(GameDataPaths.Default.ForMap(1)).AllowsPurchase(1055),
            "Summoner's Rift still sells Doran's Blade");
    }

    [Check("combat.map-8-ambient-experience-sends-a-bounded-number-of-gold-exp-blocks")]
    public static void MapEightAmbientExperienceSendsOneBlockAWholeSecondNotPerStep()
    {
        // 100 steps of 0.1 s = 10 s of 8 XP. The XP is awarded once a second (ten blocks), not once a step (one hundred).
        var data = new GameData(GameDataPaths.Default.ForMap(8));
        var clock = new GameClock();
        clock.SetVirtual(0.0);
        var world = new WorldState(clock);
        var transport = new CombatChecks.TestTransport();
        var stats = new HeroStatsService(data, new ItemCatalog(),
            new CombatChecks.LifecycleStatsHooks(), new CombatChecks.LifecycleStatsTransport());
        var combat = new CombatService(world, new CombatChecks.TestHooks(), transport, new FixedRandomSource(), stats, data);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Level = 1, MaxHealth = 500.0, Health = 500.0, MaxMana = 100.0, Mana = 100.0,
        };
        world.Register(hero);
        for (var step = 0; step < 100; step++)
            combat.CombatTick(0.1, regenerate: false);
        // Paid up to the last one-second award: at most 80 XP and no more than one award (about a second, 8 XP) behind the clock.
        TestAssert.True(hero.Experience <= 80.0 + 1e-6 && hero.Experience >= 72.0 - 1e-6,
            $"10 s of 8 XP is paid up to the last award (72 to 80 XP; got {hero.Experience})");
        TestAssert.True(transport.GoldExpPackets >= 5 && transport.GoldExpPackets <= 12,
            $"10 s of ambient experience sends one gold/exp block a second, not one a step (sent {transport.GoldExpPackets})");
    }

    [Check("progression.map-1-and-2-tables-equal-the-summoners-rift-constants")]
    public static void MapOneAndTwoShipTheSummonersRiftProgressionTables()
    {
        // Maps 1 and 2 ship the same DeathTimes, ExpCurve and Mission files, so reading their own files must give the constants.
        foreach (var map in new[] { 1, 2 })
        {
            var rules = MapProgressionRules.Load(GameDataPaths.Default.ForMap(map));
            for (var level = 1; level <= 18; level++)
            {
                TestAssert.Near(ProgressionRules.BaseDeathTime(level), rules.BaseDeathTime(level), 0.0, $"map {map} death time, level {level}");
                TestAssert.Near(ProgressionRules.ExperienceThreshold(level), rules.ExperienceThreshold(level), 0.0, $"map {map} XP threshold, level {level}");
            }
            TestAssert.Near(ProgressionRules.DeathTimeScalingStart, rules.ScalingStart, 0.0, $"map {map} scaling start");
            TestAssert.Near(ProgressionRules.DeathTimeScalingIncrement, rules.ScalingIncrement, 0.0, $"map {map} scaling increment");
            TestAssert.Near(ProgressionRules.DeathTimeScalingIncrease, rules.ScalingIncrease, 0.0, $"map {map} scaling increase");
            TestAssert.Near(ProgressionRules.DeathTimeScalingCap, rules.ScalingCap, 0.0, $"map {map} scaling cap");
            TestAssert.Near(ProgressionRules.SoloLaneUnitExperience, rules.SoloLaneUnitExperience, 0.0, $"map {map} solo share");
            for (var recipients = 2; recipients <= 5; recipients++)
                TestAssert.Near(ProgressionRules.SharedUnitExperience(100.0, recipients), rules.SharedUnitExperience(100.0, recipients),
                    0.0, $"map {map} shared share for {recipients} recipients");
            TestAssert.True(rules.AllowDeathTimeMods, $"map {map} leaves AllowDeathTimeMods absent (on)");
            TestAssert.Near(0.0, rules.WaveRespawnInterval, 0.0, $"map {map} ships no respawn wave (the client default 0)");
        }
    }

    [Check("progression.map-8-tables-are-its-own-shipped-values")]
    public static void MapEightProgressionTablesAreItsShippedValues()
    {
        // DeathTimes.inibin: 26 for every level, scaling keys 0 (off). ExpCurve.inibin: 1 (L2), 2 (L3), 880 (L4) ... 13805 (L18).
        // Mission.inibin ships no shares, so the Summoner's Rift shares stand (Reconstruction).
        var rules = MapProgressionRules.Load(GameDataPaths.Default.ForMap(8));
        for (var level = 1; level <= 18; level++)
            TestAssert.Near(26.0, rules.BaseDeathTime(level), 0.0, $"map 8 death time, level {level}");
        TestAssert.Near(0.0, rules.ScalingStart, 0.0, "map 8 scaling start is 0 (off)");
        TestAssert.Near(26.0, rules.DeathTime(18, 99999.0), 0.0, "map 8 death timer never scales");
        TestAssert.True(rules.AllowDeathTimeMods, "map 8 ships AllowDeathTimeMods True");
        TestAssert.Near(12.0, rules.WaveRespawnInterval, 0.0, "map 8 ships DeathWaveRespawn*WaveRespawnInterval 12");
        TestAssert.Near(1.0, rules.ExperienceThreshold(2), 0.0, "map 8 level-2 threshold (shipped L2 is True)");
        TestAssert.Near(2.0, rules.ExperienceThreshold(3), 0.0, "map 8 level-3 threshold (shipped L3 is 2)");
        TestAssert.Near(880.0, rules.ExperienceThreshold(4), 0.0, "map 8 level-4 threshold");
        TestAssert.Near(13805.0, rules.ExperienceThreshold(18), 0.0, "map 8 level-18 threshold");
        TestAssert.Near(1.0, rules.ExperienceToNextLevel(1), 0.0, "map 8's level-1 increment is 1 XP");
        TestAssert.Near(0.55, rules.ChampionKillExperienceBase(1), 1e-12, "map 8 champion-kill base is 55% of that increment");
        TestAssert.Near(0.92, rules.SoloLaneUnitExperience, 0.0, "map 8 ships no Mission shares: the Summoner's Rift solo share stands");
    }

    private static void Tick(CombatService combat, double from, double to)
    {
        // One-second steps keep 3000 s cheap; the ambient rates are per second and the gates are whole seconds.
        for (var time = from; time < to - 1e-9; time += 1.0)
            combat.CombatTick(1.0, regenerate: false);
    }

    private static (CombatService Combat, WorldState World, Entity Hero) CreateMatch(int mapId)
    {
        var data = new GameData(GameDataPaths.Default.ForMap(mapId));
        var clock = new GameClock();
        clock.SetVirtual(0.0);
        var world = new WorldState(clock);
        var stats = new HeroStatsService(data, new ItemCatalog(),
            new CombatChecks.LifecycleStatsHooks(), new CombatChecks.LifecycleStatsTransport());
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(), stats, data);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Level = 1, MaxHealth = 500.0, Health = 500.0, MaxMana = 100.0, Mana = 100.0,
        };
        world.Register(hero);
        return (combat, world, hero);
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public double NextDouble() => 0.5;
    }
}
