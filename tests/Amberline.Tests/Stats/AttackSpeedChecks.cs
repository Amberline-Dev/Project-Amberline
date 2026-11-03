using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Stats;

/// <summary>
/// Small, source-backed attack-speed checks. These deliberately use the
/// shipped champion/rune/item records and independent expected constants;
/// replay fixtures are not involved.
/// </summary>
public static class AttackSpeedChecks
{
    // The current maintained GameData convention is 1.0 + growth * level.
    // Keep these values explicit until native level-stage evidence settles the
    // historical level-one offset.
    private static readonly HeroCase[] Roster =
    [
        new("Annie", "Annie", 1.3600000143051147, 1.0136000001430512, 1.2448000025749206,
            0.07999999821186066, -0.104205247),
        new("Orianna", "Orianna", 3.5, 1.035, 1.63, -0.05, -0.124561404),
        new("LeeSin", "LeeSin", 3.0, 1.03, 1.54, -0.04, -0.10468749701976776),
        new("Nidalee", "Nidalee", 3.2200000286102295, 1.0322000002861023,
            1.5796000051498413, -0.06719999760389328, -0.092292024),
        new("Nidalee (Cougar)", "Nidalee_Cougar", 3.2200000286102295,
            1.0322000002861023, 1.5796000051498413, -0.06719999760389328,
            // Nidalee_Cougar ships its own cast offset; this is shorter than
            // human form's -0.092292024 and yields the native cougar windup.
            -0.132493568, true),
        new("Yorick", "Yorick", 3.0, 1.03, 1.54, 0.0, -0.091666667),
        new("Shaco", "Shaco", 3.0, 1.03, 1.54, -0.10000000149011612, -0.078496169),
        new("Ezreal", "Ezreal", 2.8, 1.028, 1.504, -0.05999999865889549, -0.111613475),
        // Vayne growth: byte 31 read as the client float32 (float)31 * 0.1f (batch 64).
        new("Vayne", "Vayne", 3.1000001430511475, 1.031, 1.558, -0.05, -0.124561404),
        new("Janna", "Janna", 2.609999895095825, 1.0260999989509583,
            1.4697999811172484, 0.0, -0.05249999836087227),
        new("Blitzcrank", "Blitzcrank", 1.1299999995, 1.0112999999523162,
            1.2033999991416932, 0.0, -0.029999999329447747)
    ];

    [Check("stats.attack-speed.roster-level-matrix")]
    public static void RosterLevelOneAndEighteenUseShippedGrowthAndDelayData()
    {
        var data = new GameData();
        var items = new ItemCatalog(data.Paths);
        foreach (var row in Roster)
        {
            var champion = data.Champion(row.DataName);
            TestAssert.Near(row.Growth, champion.AttackSpeedPerLevel, 1e-7,
                row.Label + " shipped attack-speed growth");
            // Some assets (including Janna) contain legacy Data*AttackSpeed.
            // The native loader ignores it; the explicit expected multipliers
            // below verify that it does not become a second base multiplier.

            foreach (var (level, expectedSpeed) in new[]
                     { (1, row.LevelOne), (18, row.LevelEighteen) })
            {
                var source = data.StatsAtLevel(champion, level);
                TestAssert.Near(expectedSpeed, source.AttackSpeedMod, 1e-7,
                    row.Label + " level " + level + " attack speed");

                var calculated = HeroStatCalculator.Calculate(data, items,
                    new HeroStatsInput(row.Cougar ? "Nidalee" : row.DataName,
                        level, Inventory.Empty(), statCharacter: row.DataName));
                TestAssert.Near(expectedSpeed, calculated.AttackSpeedMod, 1e-7,
                    row.Label + " level " + level + " HeroStats");

                var hero = new Entity((uint)(level + 100), EntityKind.Hero,
                    UnitType.Hero, Team.Order, new Position2(0, 0))
                {
                    ChampionName = row.Cougar ? "Nidalee" : row.DataName,
                    AttackSpeedMod = expectedSpeed,
                    ScriptCharacterData = row.Cougar ? new ScriptCharacterLayer(1, row.DataName, OverrideSpells: true) : null
                };
                var timing = HeroAttackTimings.Apply(hero, data);
                var expectedPeriod = Period(row, expectedSpeed);
                TestAssert.Near(expectedPeriod, timing.Period, 1e-7,
                    row.Label + " level " + level + " attack period");
                TestAssert.Near(expectedPeriod * Math.Max(0.0, 0.3 + row.CastOffset),
                    timing.Windup, 1e-7,
                    row.Label + " level " + level + " attack windup");
                TestAssert.True(hero.AttackTimingBaseline.HasValue,
                    row.Label + " level " + level + " stores an attack baseline");
                var baseline = hero.AttackTimingBaseline.GetValueOrDefault();
                TestAssert.Near(expectedSpeed, baseline.BaseAttackSpeed, 1e-7,
                    row.Label + " baseline effective speed");
                TestAssert.Near(1.6 * (1.0 + row.DelayOffset), baseline.BasePeriod, 1e-7,
                    row.Label + " baseline raw period");
                TestAssert.Near(Math.Max(0.0, 0.3 + row.CastOffset), baseline.CastFraction, 1e-7,
                    row.Label + " baseline cast fraction");
            }
        }
    }

    [Check("stats.attack-speed.bonuses-and-multiplicative-slows")]
    public static void ItemsRunesMasteriesAndDistinctSlowsComposeInTheOwnedStages()
    {
        var data = new GameData();
        var items = new ItemCatalog(data.Paths);
        var baseSpeed = data.StatsAtLevel(data.Champion("Annie"), 1).AttackSpeedMod;

        var dagger = items.Get(1042) ?? throw new InvalidOperationException("shipped Dagger is missing");
        // The shipped inibin stores PercentAttackSpeedMod as Float32.
        const double daggerAttackSpeed = 0.15000000596046448;
        TestAssert.Near(daggerAttackSpeed, dagger.Modifier("PercentAttackSpeedMod"), 1e-9);
        var daggerSlots = Inventory.Empty().ToArray();
        daggerSlots[0] = new InventorySlot(1042);
        var withDagger = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, daggerSlots));
        TestAssert.Near(baseSpeed + daggerAttackSpeed, withDagger.AttackSpeedMod, 1e-7,
            "item additive attack speed");

        var additive = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(),
                [new BuffState("test-additive", new BuffPayload { AttackSpeedMod = 0.20 })]));
        TestAssert.Near(baseSpeed + 0.20, additive.AttackSpeedMod, 1e-7,
            "active additive attack speed");

        var oneSlow = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(),
                [new BuffState("slow-one", new BuffPayload { AttackSpeedMult = -0.20 })]));
        TestAssert.Near(baseSpeed * 0.80, oneSlow.AttackSpeedMod, 1e-7,
            "one multiplicative slow");

        var twoSlows = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(),
            [
                new BuffState("slow-one", new BuffPayload { AttackSpeedMult = -0.20 }),
                new BuffState("slow-two", new BuffPayload { AttackSpeedMult = -0.30 })
            ]));
        TestAssert.Near(baseSpeed * 0.80 * 0.70, twoSlows.AttackSpeedMod, 1e-7,
            "distinct multiplicative slows multiply per native call");

        var afterRemoval = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(),
                [new BuffState("slow-two", new BuffPayload { AttackSpeedMult = -0.30 })]));
        TestAssert.Near(baseSpeed * 0.70, afterRemoval.AttackSpeedMod, 1e-7,
            "removing one slow leaves the other factor intact");

        var runes = new RuneCatalog(items, data.Paths);
        var speedRune = runes.All.First(rune => rune.Modifier("PercentAttackSpeedMod") > 0.0);
        var runePage = OneRune(runes, speedRune);
        var withRune = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: runePage));
        TestAssert.Near(baseSpeed + speedRune.Modifier("PercentAttackSpeedMod"),
            withRune.AttackSpeedMod, 1e-7, "rune additive attack speed");

        var masteryCatalog = new MasteryCatalog(data.Paths);
        var attackSpeedMastery = MasteryPage.Create(masteryCatalog, new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [126] = 1, [102] = 4, [105] = 4,
            [146] = 1, [101] = 3, [100] = 3, [104] = 3
        });
        var withMastery = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), masteries: attackSpeedMastery));
        TestAssert.Near(baseSpeed + 0.04, withMastery.AttackSpeedMod, 1e-7,
            "rank-105 attack-speed mastery");

        var ardor = MasteryPage.Create(masteryCatalog, new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1, [125] = 1
        });
        // Ardor is not a calculator rule any more (batch 144): Talents/111 sets
        // NextBuffVars.PercentMod = { 0.01333, 0.02667, 0.04 }[TalentLevel] and adds the live
        // Spells/Ardor.luaobj, whose BuffOnUpdateActions stores
        // AS = (GetPercentAttackSpeedMod - AS) * PercentMod and puts it back through
        // IncPercentAttackSpeedMod — one PercentMod share of the additive pool it does not already
        // hold (GetPercentAttackSpeedMod is the native mPercentAttackSpeedMod accumulator, whose
        // reset default is 0, so the pool is the explicit item/rune/mastery/buff points). With a
        // Dagger and rank 3 the steady state is 0.15 * 0.04 on top of the 0.15. A calculator input
        // with the mastery page but no live buffs holds the pool before that loop's first update
        // (1.1636); the retail number needs the script, so it is measured on a production hero.
        var withArdor = LiveAttackSpeed("Annie", 1, daggerSlots, ardor);
        TestAssert.Near(baseSpeed + daggerAttackSpeed * 1.04, withArdor,
            1e-7, "Ardor scales the explicit additive attack-speed pool");
    }

    /// <summary>The attack speed of a production hero that wears <paramref name="masteries"/> and bought
    /// <paramref name="ids"/>, read after the loadout scripts installed their live buffs (batch 144). The
    /// mastery page's own numbers arrive as those buffs now, so this is the only way a mastery page can
    /// reach the stat this check reads.</summary>
    private static double LiveAttackSpeed(string champion, int level, InventorySlot?[] ids, MasteryPage masteries)
    {
        var roster = new List<SessionParticipantDefinition> { new(1, 0, "Hero", champion, Team.Order) };
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0, roster: roster);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        const long connection = 100L;
        TestAssert.True(runtime.Session.Authenticate(connection,
            runtime.Session.BuildKeyCheck(1)) is not null, "production seat authenticates");
        runtime.Session.Spawn(connection);
        TestAssert.True(runtime.Session.MarkReady(connection), "production seat reaches ready");
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "production seat starts");
        var hero = runtime.Session.Participants[0].Hero!;
        hero.Level = level;
        hero.Masteries = masteries;
        hero.Inventory = ids;
        runtime.HeroStats.Refresh(hero);
        // Ardor's number only reaches the stat once its BuffOnUpdateStats applies the AS its BuffOnUpdateActions
        // computed, and that stats hook runs on the 0.25 s grid (BbBuffRuntime.StatsUpdateInterval) while the actions
        // hook runs on the simulation step: several actions runs share one sample, so the first sample holds
        // PercentMod/(1 + PercentMod) of the pool (0.15 * 0.04 / 1.04 with this Dagger) and every later sample walks AS
        // up to PercentMod of it (0.15 * 0.04, its own share already out of the pool GetPercentAttackSpeedMod reads).
        // The loop is settled by three seconds, so the window waits for the designed share instead of reading the ramp.
        for (var elapsed = 0.0; elapsed < 3.0 - 1e-9; elapsed += 0.01) runtime.Tick(0.01);
        runtime.HeroStats.Refresh(hero);
        return hero.AttackSpeedMod;
    }

    [Check("stats.attack-speed.period-caps-and-form-refresh")]
    public static void PeriodCapsAndNidaleeRefreshKeepTimingStateCoherent()
    {
        var data = new GameData();
        foreach (var row in Roster.Where(row => !row.Cougar))
        {
            var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
                new Position2(0, 0)) { ChampionName = row.DataName };
            foreach (var speed in new[] { 100.0, 0.001 })
            {
                hero.AttackSpeedMod = speed;
                var timing = HeroAttackTimings.Apply(hero, data);
                TestAssert.Near(speed == 100.0 ? 0.4 : 5.0, timing.Period, 0,
                    row.Label + " native period cap");
                TestAssert.True(timing.Windup >= 0.0 && timing.Windup <= timing.Period,
                    row.Label + " windup is inside the period");
                TestAssert.Near(1.6 * (1.0 + row.DelayOffset),
                    hero.AttackTimingBaseline!.Value.BasePeriod, 1e-7,
                    row.Label + " retains the raw period after either cap");
            }
        }

        var cougar = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0, 0))
        {
            ChampionName = "Nidalee",
            AttackSpeedMod = Roster.Single(row => row.Cougar).LevelOne,
            MissileSpeed = 0.0
        };
        cougar.ScriptCharacterData = new ScriptCharacterLayer(1, "Nidalee_Cougar", OverrideSpells: true);
        var refreshed = HeroAttackTimings.ApplyRefresh(cougar, data);
        var cougarCase = Roster.Single(row => row.Cougar);
        TestAssert.Near(Period(cougarCase, cougar.AttackSpeedMod), refreshed.Period, 1e-7);
        TestAssert.Near(0.0, refreshed.MissileSpeed, 0,
            "Nidalee form refresh preserves the existing missile override");
    }

    [Check("stats.attack-speed.committed-windup-survives-refresh")]
    public static void CommittedBasicAttackKeepsItsOriginalTimingAfterRefresh()
    {
        var data = GameData.Default;
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data,
            orderChampion: "Annie", chaosChampion: "Shaco");
        runtime.Session.CreateWorld();
        var attacker = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        var target = runtime.World.Heroes().Single(hero => hero.Team == Team.Chaos);
        attacker.Position = target.Position;
        attacker.AttackCooldown = 0.0;
        var beforePeriod = attacker.AttackPeriod;
        TestAssert.True(runtime.Combat.QueueBasicAttack(attacker, target,
            new AttackRolls(1, 1, 1)));
        var beforeCooldown = attacker.AttackCooldown;
        var beforeWindup = runtime.Combat.PendingHits.Single().WindupRemaining;

        attacker.Buffs.Add(new BuffState("haste",
            new BuffPayload { AttackSpeedMod = 0.50 }));
        runtime.HeroStats.Refresh(attacker);
        TestAssert.True(attacker.AttackPeriod < beforePeriod,
            "refresh changes the next attack period");
        TestAssert.Near(beforeCooldown, attacker.AttackCooldown, 1e-9,
            "refresh does not rewrite a committed cooldown");
        TestAssert.Near(beforeWindup, runtime.Combat.PendingHits.Single().WindupRemaining, 1e-9,
            "refresh does not rewrite a committed windup");
    }

    [Check("stats.attack-speed.attack-packet-animation-field")]
    public static void BasicAttackPacketCarriesAnimationChoiceSeparatelyFromAttackSpeed()
    {
        var packet = CombatPackets.BasicAttack(1, 2, 3, slot: 0x41);
        TestAssert.Equal(15, packet.Plaintext.Length);
        TestAssert.Equal((byte)0x41, packet.Plaintext.Span[14]);
        // The 0x0D attack packet has no attack-speed scalar. HeroStats is the
        // packet that publishes the current modifier; the animation byte is
        // only the weighted basic-attack choice.
    }

    private static double Period(HeroCase row, double attackSpeed) =>
        Math.Clamp(1.6 * (1.0 + row.DelayOffset) / attackSpeed, 0.4, 5.0);

    private static RunePage OneRune(RuneCatalog catalog, RuneData rune) => rune.Color switch
    {
        RuneColor.Red => RunePage.Create(catalog, red: new[] { rune.Id }),
        RuneColor.Blue => RunePage.Create(catalog, blue: new[] { rune.Id }),
        RuneColor.Yellow => RunePage.Create(catalog, yellow: new[] { rune.Id }),
        RuneColor.Black => RunePage.Create(catalog, black: new[] { rune.Id }),
        _ => throw new InvalidOperationException("unknown rune color")
    };

    private readonly record struct HeroCase(
        string Label,
        string DataName,
        double Growth,
        double LevelOne,
        double LevelEighteen,
        double DelayOffset,
        double CastOffset,
        bool Cougar = false);
}
