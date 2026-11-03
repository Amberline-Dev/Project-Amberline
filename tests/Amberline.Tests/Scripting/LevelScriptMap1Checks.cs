using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Map1's shipped <c>LevelScript.luaobj</c> running on the production host: its init, its timers
/// and the wave answers the barracks path is built from. The expected numbers are the script's
/// own — the corpus in <c>Fixtures/lua-vm-shipped-corpus.json</c> pins the same function at the
/// Lua level.
/// </summary>
public static class LevelScriptMap1Checks
{
    [Check("scripting.level-script-map1-init")]
    public static void OnLevelInitRunsTheScript()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);

        TestAssert.Equal(0, errors.Count, "OnLevelInit raises nothing: " + string.Join("; ", errors));

        // The script preloads its eight lane characters and eight turret characters itself;
        // NeutralMinionInit, which OnLevelInit chains in, preloads the eleven camp monsters.
        var characters = host.Preloads.Where(entry => entry.Kind == "character").Select(entry => entry.Name).ToArray();
        foreach (var name in new[]
                 {
                     "Blue_Minion_Basic", "Blue_Minion_Wizard", "Blue_Minion_MechCannon", "Blue_Minion_MechMelee",
                     "Red_Minion_Basic", "Red_Minion_Wizard", "Red_Minion_MechCannon", "Red_Minion_MechMelee",
                     "OrderTurretAngel", "OrderTurretDragon", "OrderTurretNormal2", "OrderTurretNormal",
                     "ChaosTurretWorm", "ChaosTurretWorm2", "ChaosTurretGiant", "ChaosTurretNormal",
                 })
            TestAssert.True(characters.Contains(name), $"{name} is preloaded by OnLevelInit");
        TestAssert.Equal(27, characters.Length,
            "16 lane/turret characters plus the 11 NeutralMinionInit camp monsters");
        TestAssert.Equal(new[] { "RespawnClassic", "SpellShieldMarker" },
            host.Preloads.Where(entry => entry.Kind == "spell").Select(entry => entry.Name).ToArray(),
            "the two spell preloads OnLevelInit asks for");

        // InitTimer('UpgradeMinionTimer', UPGRADE_MINION_TIMER, true),
        // InitTimer('IncreaseCannonMinionSpawnRate', INCREASE_CANNON_RATE_TIMER, false),
        // InitTimer('AllowDamageOnBuildings', 10.0, false).
        TestAssert.Equal(3, host.Timers.Count, "OnLevelInit arms three timers");
        var upgrade = host.Timers.Single(timer => timer.Name == "UpgradeMinionTimer");
        TestAssert.Near(180.0, upgrade.Interval, 1e-9, "UPGRADE_MINION_TIMER");
        TestAssert.True(upgrade.Repeat, "UpgradeMinionTimer repeats");
        var cannon = host.Timers.Single(timer => timer.Name == "IncreaseCannonMinionSpawnRate");
        TestAssert.Near(2090.0, cannon.Interval, 1e-9, "INCREASE_CANNON_RATE_TIMER");
        TestAssert.False(cannon.Repeat, "IncreaseCannonMinionSpawnRate is a one-shot");
        var damage = host.Timers.Single(timer => timer.Name == "AllowDamageOnBuildings");
        TestAssert.Near(10.0, damage.Interval, 1e-9, "AllowDamageOnBuildings is armed at ten seconds");
        TestAssert.False(damage.Repeat, "AllowDamageOnBuildings is a one-shot");

        TestAssert.Near(90.0, host.FirstSpawnSeconds, 1e-9, "INITIAL_TIME_TO_SPAWN");
    }

    [Check("scripting.level-script-map1-turrets")]
    public static void CreateLevelPropsBuildsTheTurretRoster()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        // OnPostLevelLoad (LevelScript.txt:916-921) dofiles CreateLevelProps.lua and calls its
        // CreateLevelProps(), whose CreateChildTurret rows are the map's turret roster. The tower
        // ids are the script's own BACK_TOWER/MIDDLE_TOWER/FRONT_TOWER/HQ_TOWER2/HQ_TOWER1 = 1/2/3/4/5
        // and 0 for the two fountain shrines; the lane ids are RIGHT/CENTER/LEFT = 0/1/2.
        host.OnPostLevelLoad();
        TestAssert.Equal(0, errors.Count, "CreateLevelProps raises nothing: " + string.Join("; ", errors));

        var rows = host.LevelTurrets;
        TestAssert.Equal(24, rows.Count, "CreateLevelProps emits twenty-four CreateChildTurret rows");
        var expected = new (int Tower, int Lane, Team Team, string Name, string Character)[]
        {
            (3, 0, Team.Order, "Turret_T1_R_03", "OrderTurretNormal"),
            (2, 0, Team.Order, "Turret_T1_R_02", "OrderTurretNormal2"),
            (1, 0, Team.Order, "Turret_T1_C_07", "OrderTurretDragon"),
            (3, 0, Team.Chaos, "Turret_T2_R_03", "ChaosTurretWorm"),
            (2, 0, Team.Chaos, "Turret_T2_R_02", "ChaosTurretWorm2"),
            (1, 0, Team.Chaos, "Turret_T2_R_01", "ChaosTurretGiant"),
            (3, 1, Team.Order, "Turret_T1_C_05", "OrderTurretNormal"),
            (2, 1, Team.Order, "Turret_T1_C_04", "OrderTurretNormal2"),
            (1, 1, Team.Order, "Turret_T1_C_03", "OrderTurretDragon"),
            (5, 1, Team.Order, "Turret_T1_C_01", "OrderTurretAngel"),
            (4, 1, Team.Order, "Turret_T1_C_02", "OrderTurretAngel"),
            (3, 1, Team.Chaos, "Turret_T2_C_05", "ChaosTurretWorm"),
            (2, 1, Team.Chaos, "Turret_T2_C_04", "ChaosTurretWorm2"),
            (1, 1, Team.Chaos, "Turret_T2_C_03", "ChaosTurretGiant"),
            (5, 1, Team.Chaos, "Turret_T2_C_01", "ChaosTurretNormal"),
            (4, 1, Team.Chaos, "Turret_T2_C_02", "ChaosTurretNormal"),
            (0, 1, Team.Order, "Turret_OrderTurretShrine", "OrderTurretShrine"),
            (0, 1, Team.Chaos, "Turret_ChaosTurretShrine", "ChaosTurretShrine"),
            (3, 2, Team.Order, "Turret_T1_L_03", "OrderTurretNormal"),
            (2, 2, Team.Order, "Turret_T1_L_02", "OrderTurretNormal2"),
            (1, 2, Team.Order, "Turret_T1_C_06", "OrderTurretDragon"),
            (3, 2, Team.Chaos, "Turret_T2_L_03", "ChaosTurretWorm"),
            (2, 2, Team.Chaos, "Turret_T2_L_02", "ChaosTurretWorm2"),
            (1, 2, Team.Chaos, "Turret_T2_L_01", "ChaosTurretGiant"),
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var row = rows[index];
            var want = expected[index];
            TestAssert.Equal(want.Name, row.Name, $"row {index} name");
            TestAssert.Equal(want.Character, row.Character, $"row {index} character");
            TestAssert.Equal(want.Team, row.Team, $"row {index} team");
            TestAssert.Equal(want.Tower, row.TowerId, $"row {index} tower id");
            TestAssert.Equal(want.Lane, row.LaneId, $"row {index} lane id");
        }

        // One character per (tier, team): the ten pairs the turret rules ask for. They are the
        // ten the hand-written switch in TurretRules used to name (deleted in this batch); the
        // script is now the only source and it names exactly the same characters.
        var characters = new (TurretTier Tier, Team Team, string Character)[]
        {
            (TurretTier.Outer, Team.Order, "OrderTurretNormal"),
            (TurretTier.Outer, Team.Chaos, "ChaosTurretWorm"),
            (TurretTier.Inner, Team.Order, "OrderTurretNormal2"),
            (TurretTier.Inner, Team.Chaos, "ChaosTurretWorm2"),
            (TurretTier.Inhibitor, Team.Order, "OrderTurretDragon"),
            (TurretTier.Inhibitor, Team.Chaos, "ChaosTurretGiant"),
            (TurretTier.Nexus, Team.Order, "OrderTurretAngel"),
            (TurretTier.Nexus, Team.Chaos, "ChaosTurretNormal"),
            (TurretTier.Fountain, Team.Order, "OrderTurretShrine"),
            (TurretTier.Fountain, Team.Chaos, "ChaosTurretShrine"),
        };
        foreach (var (tier, team, character) in characters)
            TestAssert.Equal(character, MapDefinitions.TurretCharacter(tier, team),
                $"CreateLevelProps gives every {tier} of {team} the {character} character");

        // The tier of each row is the script's own tower id projected onto the engine's tiers.
        foreach (var (tower, tier) in new (int Tower, TurretTier Tier)[]
                 {
                     (0, TurretTier.Fountain), (1, TurretTier.Inhibitor), (2, TurretTier.Inner),
                     (3, TurretTier.Outer), (4, TurretTier.Nexus), (5, TurretTier.Nexus),
                 })
            TestAssert.Equal(tier, MapDefinitions.TurretTierOf(tower), $"tower id {tower} is the {tier} tier");
        foreach (var want in expected)
            TestAssert.Equal(MapDefinitions.TurretTierOf(want.Tower), MapDefinitions.TurretTiers[want.Name],
                $"{want.Name} carries the tier its tower id gives it");
    }

    [Check("scripting.level-script-map1-wave-composition")]
    public static void WaveCompositionComesFromTheScript()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);

        // GetMinionSpawnInfo(barracks, waveIndex + 1, 0, team, waveIndex + 1): the native barracks
        // counter is incremented before the Lua call, so the
        // script's CASTER_MINION_SPAWN_FREQUENCY phase is 1-based and cannons land on human waves
        // 3, 6 and 9. The corpus records the same function at counter 0 (a cannon) and counter 1
        // (none); those two inputs are the same table this drives, one step before the first wave.
        var cannons = new List<int>();
        for (var wave = 0; wave < 12; wave++)
        {
            var info = host.GetMinionSpawnInfo(Team.Order, "C", wave);
            TestAssert.Equal(3, info.Melee, $"wave {wave + 1} melee");
            TestAssert.Equal(3, info.Caster, $"wave {wave + 1} archer");
            TestAssert.Equal(0, info.Super, $"wave {wave + 1} super");
            cannons.Add(info.Siege);
            TestAssert.Near(30.0, info.WaveSpawnRateSeconds, 1e-9, "SpawnTable.WaveSpawnRate is 30000 ms");
            TestAssert.Near(0.8, info.SingleMinionSpawnDelaySeconds, 1e-9,
                "SpawnTable.SingleMinionSpawnDelay is 800 ms");
            TestAssert.Equal("Blue_Minion_Basic", info.MeleeName, "OrderNames.MeleeMinionName");
            TestAssert.Equal("Blue_Minion_Wizard", info.CasterName, "OrderNames.ArcherMinionName");
            TestAssert.Equal("Blue_Minion_MechCannon", info.SiegeName, "OrderNames.CasterMinionName");
            TestAssert.Equal("Blue_Minion_MechMelee", info.SuperName, "OrderNames.SuperMinionName");
        }
        TestAssert.Equal(new[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 }, cannons.ToArray(),
            "cannons on human waves 3, 6, 9 and 12 while CASTER_MINION_SPAWN_FREQUENCY is 3");

        // The Chaos answer is the mirrored name table; the composition is the shared SpawnTable.
        var chaos = host.GetMinionSpawnInfo(Team.Chaos, "L", 2);
        TestAssert.Equal(1, chaos.Siege, "the cannon cadence is per wave, not per team");
        TestAssert.Equal("Red_Minion_MechCannon", chaos.SiegeName, "ChaosNames.CasterMinionName");

        // The five members of a wave come back in the script's own order.
        var members = host.GetMinionSpawnInfo(Team.Order, "R", 2).Members;
        TestAssert.Equal(new[]
        {
            MinionType.Melee, MinionType.Melee, MinionType.Melee,
            MinionType.Caster, MinionType.Caster, MinionType.Caster, MinionType.Siege,
        }, members.ToArray(), "a cannon wave is three melee, three archers and one cannon");
    }

    [Check("scripting.level-script-map1-timers-and-barracks")]
    public static void TimersAndBarracksEventsRun()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        // The first step arms the timers at match start, the way a zero-length tick does.
        host.Advance(0.0, 0.0);

        // UpgradeMinionTimer is pure table arithmetic and must not raise on any tick.
        host.Advance(180.0, 0.1);
        host.Advance(360.0, 0.1);
        TestAssert.Equal(0, errors.Count, "UpgradeMinionTimer raises nothing: " + string.Join("; ", errors));

        // IncreaseCannonMinionSpawnRate is the 2090 s one-shot that sets
        // CASTER_MINION_SPAWN_FREQUENCY to 2, so cannons move to every second wave.
        host.Advance(2090.0, 0.1);
        var cannons = new List<int>();
        for (var wave = 0; wave < 6; wave++)
            cannons.Add(host.GetMinionSpawnInfo(Team.Order, "C", wave).Siege);
        TestAssert.Equal(new[] { 0, 1, 0, 1, 0, 1 }, cannons.ToArray(),
            "after IncreaseCannonMinionSpawnRate the cannon cadence is every second wave");

        // AllowDamageOnBuildings is a one-shot that has already been dispatched by 2090 s.
        TestAssert.False(host.Timers.Any(timer => timer.Name == "AllowDamageOnBuildings"),
            "the ten-second one-shot has stopped");
        TestAssert.Equal(0, errors.Count, "the dispatch raises nothing: " + string.Join("; ", errors));

        // An inhibitor down runs ApplyBarracksDestructionBonuses: the affected lane of the
        // beneficiary team spawns a super instead of a cannon, and two down lanes spawn two.
        var lane = "L";
        host.OnBarracksFell(Inhibitor(Team.Chaos, lane));
        var supers = host.GetMinionSpawnInfo(Team.Order, lane, 20);
        TestAssert.Equal(1, supers.Super, "one down enemy inhibitor grants one super in its lane");
        TestAssert.Equal(0, supers.Siege, "WillSpawnSuperMinion zeroes the cannon count");
        TestAssert.Equal(0, host.GetMinionSpawnInfo(Team.Order, "C", 20).Super,
            "the other lanes keep their normal composition");

        host.OnBarracksFell(Inhibitor(Team.Chaos, "C"));
        host.OnBarracksFell(Inhibitor(Team.Chaos, "R"));
        TestAssert.Equal(2, host.GetMinionSpawnInfo(Team.Order, "R", 20).Super,
            "every enemy inhibitor down grants two supers");

        // The restore path runs ApplyBarracksRespawnReductions, which re-locks the HQ and its
        // towers once the third inhibitor is back; the structure natives must not raise on a
        // world that has no structures registered.
        foreach (var restored in new[] { "L", "C", "R" })
            host.OnBarracksRestored(Inhibitor(Team.Chaos, restored));
        TestAssert.Equal(0, host.GetMinionSpawnInfo(Team.Order, "R", 20).Super,
            "a restored inhibitor drops the super override");
        TestAssert.Equal(0, errors.Count, "the barracks hooks raise nothing: " + string.Join("; ", errors));
    }

    [Check("scripting.level-script-map1-economy")]
    public static void EconomyComesFromTheScript()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);

        // proto 0 pc0034-0141 (LevelScript.txt:244-351) seeds the six barracks tables from the
        // *_EXP_GIVEN / *_GOLD_GIVEN constants with every bonus at zero, so at time zero a minion
        // pays its own character's rewards and wears its own base stats.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var lane in MapDefinitions.Lanes)
        {
            var economy = host.EconomyOf(team, lane);
            TestAssert.Near(1250.0, economy.ExperienceRadius, 1e-9, "EXP_GIVEN_RADIUS");
            foreach (var type in Enum.GetValues<MinionType>())
            {
                var row = economy.RowOf(type);
                var baseStats = MinionDefinitions.BaseStats(type, team);
                TestAssert.Near(0.0, row.HealthBonus, 1e-9, $"{team} {lane} {type} starting HP bonus");
                TestAssert.Near(0.0, row.DamageBonus, 1e-9, $"{team} {lane} {type} starting AD bonus");
                TestAssert.Near(0.0, row.MinionArmor, 1e-9, $"{team} {lane} {type} starting armor bonus");
                TestAssert.Near(0.0, row.MinionMagicResistance, 1e-9, $"{team} {lane} {type} starting MR bonus");
                TestAssert.Near(baseStats.GoldReward, row.GoldGiven, 1e-9, $"{team} {lane} {type} starting gold");
                TestAssert.Near(baseStats.ExperienceReward, row.ExperienceGiven, 1e-9, $"{team} {lane} {type} starting XP");
                var stats = economy.StatsOf(type, team);
                TestAssert.Near(baseStats.MaxHealth, stats.MaxHealth, 1e-9, $"{team} {lane} {type} starting HP");
                TestAssert.Near(baseStats.AttackDamage, stats.AttackDamage, 1e-9, $"{team} {lane} {type} starting AD");
                TestAssert.Near(baseStats.Armor, stats.Armor, 1e-9, $"{team} {lane} {type} starting armor");
                TestAssert.Near(baseStats.SpellBlock, stats.SpellBlock, 1e-9, $"{team} {lane} {type} starting MR");
            }
        }

        // UpgradeMinionTimer (proto 0.4, LevelScript.txt:936-1606) adds the *_UPGRADE constants to
        // every lane's bonuses and recomputes each class's own *GoldGiven / *ExpGiven from them,
        // capping *GoldBonus at MAXIMUM_*_GOLD_BONUS (proto 0 pc0098-0105).
        var interval = host.Number("UPGRADE_MINION_TIMER", 180.0);
        for (var upgrades = 1; upgrades <= 25; upgrades++)
        {
            host.AdvanceTo(upgrades * interval);
            foreach (var lane in MapDefinitions.Lanes)
            foreach (var type in Enum.GetValues<MinionType>())
            {
                var row = host.EconomyOf(Team.Order, lane).RowOf(type);
                var curve = Curve(type);
                TestAssert.Near(upgrades * curve.Health, row.HealthBonus, 1e-9, $"{type} HP bonus after {upgrades} upgrades");
                TestAssert.Near(upgrades * curve.AttackDamage, row.DamageBonus, 1e-9, $"{type} AD bonus after {upgrades} upgrades");
                TestAssert.Near(upgrades * curve.Armor, row.MinionArmor, 1e-9, $"{type} armor after {upgrades} upgrades");
                TestAssert.Near(upgrades * curve.SpellBlock, row.MinionMagicResistance, 1e-9, $"{type} MR after {upgrades} upgrades");
                TestAssert.Near(MinionDefinitions.BaseStats(type, Team.Order).ExperienceReward +
                    upgrades * curve.Experience, row.ExperienceGiven, 1e-9, $"{type} XP after {upgrades} upgrades");
                TestAssert.Near(MinionDefinitions.BaseStats(type, Team.Order).GoldReward +
                    Math.Min(upgrades * curve.Gold, curve.GoldCap), row.GoldGiven, 1e-9,
                    $"{type} gold after {upgrades} upgrades");
            }
        }
        // The melee cap binds on the walk above: MELEE_GOLD_GIVEN + MAXIMUM_MELEE_GOLD_BONUS.
        TestAssert.Near(32.0, host.EconomyOf(Team.Order, "C").Melee.GoldGiven, 1e-9,
            "melee gold is capped at 22 + MAXIMUM_MELEE_GOLD_BONUS");
        TestAssert.Near(47.0, host.EconomyOf(Team.Order, "C").Siege.GoldGiven, 1e-9,
            "cannon gold is capped at 27 + MAXIMUM_CASTER_GOLD_BONUS");
        TestAssert.Near(27.0, host.EconomyOf(Team.Order, "C").Super.GoldGiven, 1e-9,
            "super gold never moves: SUPER_GOLD_UPGRADE is 0.0");

        // ApplyBarracksDestructionBonuses (proto 0.11, LevelScript.txt:2218-2548) adds the
        // *_HEALTH_INHIBITOR / *_DAMAGE_INHIBITOR terms to all three lanes of the beneficiary team
        // (only the lane's WillSpawnSuperMinion is per lane); ApplyBarracksRespawnReductions
        // (proto 0.12) subtracts them again. Both write Archer/Caster/Super GoldGiven from
        // MeleeGoldGiven (proto 0.11 pc0098-0137, proto 0.12 pc0295-0330): the shipped defect this
        // emulator reproduces by owner decision, 18 October 2026. The *_GOLD_INHIBITOR and
        // *_EXP_INHIBITOR terms there are all 0.0, so XP never moves and the repair is exact.
        var (fallen, fallenErrors) = CreateHost();
        fallen.OnLevelInit(0.0);
        fallen.OnBarracksFell(Inhibitor(Team.Chaos, "L"));
        foreach (var lane in MapDefinitions.Lanes)
        foreach (var type in Enum.GetValues<MinionType>())
        {
            var stats = fallen.EconomyOf(Team.Order, lane).StatsOf(type, Team.Order);
            var baseStats = MinionDefinitions.BaseStats(type, Team.Order);
            var bonus = InhibitorBonus(type);
            TestAssert.Near(baseStats.MaxHealth + bonus.Health, stats.MaxHealth, 1e-9,
                $"destroyed inhibitor {lane} {type} HP");
            TestAssert.Near(baseStats.AttackDamage + bonus.AttackDamage, stats.AttackDamage, 1e-9,
                $"destroyed inhibitor {lane} {type} AD");
            TestAssert.Near(baseStats.ExperienceReward, stats.ExperienceReward, 1e-9,
                $"destroyed inhibitor {lane} {type} XP");
            TestAssert.Near(22.0, stats.GoldReward, 1e-9,
                $"destroyed inhibitor {lane} {type} pays the melee gold");
        }
        foreach (var type in Enum.GetValues<MinionType>())
            TestAssert.Near(MinionDefinitions.BaseStats(type, Team.Chaos).GoldReward,
                fallen.EconomyOf(Team.Chaos, "C").RowOf(type).GoldGiven, 1e-9,
                $"the beneficiary's opponent {type} gold is untouched");

        // The respawn takes the bonuses back and re-runs the same gold defect.
        fallen.OnBarracksRestored(Inhibitor(Team.Chaos, "L"));
        foreach (var type in Enum.GetValues<MinionType>())
        {
            var row = fallen.EconomyOf(Team.Order, "C");
            TestAssert.Near(MinionDefinitions.BaseStats(type, Team.Order).MaxHealth,
                row.StatsOf(type, Team.Order).MaxHealth, 1e-9, $"restored inhibitor {type} HP");
            TestAssert.Near(22.0, row.RowOf(type).GoldGiven, 1e-9, $"restored inhibitor {type} gold defect");
        }
        // The next UpgradeMinionTimer recomputes every class from its own base.
        fallen.AdvanceTo(180.0 + 1.0);
        var repaired = fallen.EconomyOf(Team.Order, "C");
        TestAssert.Near(22.5, repaired.Melee.GoldGiven, 1e-9, "repaired melee gold");
        TestAssert.Near(16.5, repaired.Caster.GoldGiven, 1e-9, "repaired archer gold");
        TestAssert.Near(28.0, repaired.Siege.GoldGiven, 1e-9, "repaired cannon gold");
        TestAssert.Near(27.0, repaired.Super.GoldGiven, 1e-9, "repaired super gold");
        TestAssert.Near(69.0, repaired.Melee.ExperienceGiven, 1e-9, "the upgrade step moves XP too");

        // "Team-size catch-up" is not Map1 script state at all: the catch-up a match applies is the
        // ChampionChampionDelta / PositiveChampionDelta CharScript buff (TeamSizeCatchUp), pinned by
        // buffs.team-size-catch-up-matches-scripts and combat.team-size-catch-up-ramp-and-equalise.
        TestAssert.False(host.GlobalNames.Any(name =>
                name.Contains("CatchUp", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TeamSize", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ChampionDelta", StringComparison.OrdinalIgnoreCase)),
            "Map1's LevelScript holds no team-size catch-up state");

        TestAssert.Equal(0, errors.Count + fallenErrors.Count,
            "the economy path raises nothing: " + string.Join("; ", errors.Concat(fallenErrors)));
    }

    /// <summary>
    /// One class's <c>*_UPGRADE</c> constants and its <c>MAXIMUM_*_GOLD_BONUS</c> cap. The script's
    /// "Archer" is our <see cref="MinionType.Caster"/> and its "Caster" our
    /// <see cref="MinionType.Siege"/>, so those two rows read the ARCHER_*/CASTER_* constants
    /// crossed.
    /// </summary>
    private static (double Health, double AttackDamage, double Armor, double SpellBlock,
        double Experience, double Gold, double GoldCap) Curve(MinionType type) => type switch
    {
        MinionType.Melee => (20.0, 1.0, 2.0, 1.25, 5.0, 0.5, 10.0),
        MinionType.Caster => (15.0, 2.0, 1.25, 2.0, 3.0, 0.5, 10.0),
        MinionType.Siege => (27.0, 3.0, 3.0, 3.0, 7.0, 1.0, 20.0),
        MinionType.Super => (200.0, 10.0, 0.0, 0.0, 0.0, 0.0, 20.0),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>One class's <c>*_HEALTH_INHIBITOR</c> / <c>*_DAMAGE_INHIBITOR</c> pair.</summary>
    private static (double Health, double AttackDamage) InhibitorBonus(MinionType type) => type switch
    {
        MinionType.Melee => (100.0, 10.0),
        MinionType.Caster => (60.0, 18.0),
        MinionType.Siege => (200.0, 25.0),
        MinionType.Super => (0.0, 0.0),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Check("scripting.inhibitor-death-runs-handle-destroyed-object")]
    public static void InhibitorDeathRunsTheShippedHandler()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        host.Advance(0.0, 0.0);
        var counter = host.Number("DisactivatedCounter", 0.0);
        host.OnBarracksFell(Inhibitor(Team.Chaos, "C"));
        TestAssert.True(errors.Count == 0, "HandleDestroyedObject raises nothing: " + string.Join("; ", errors));
        TestAssert.Near(counter + 1.0, host.Number("DisactivatedCounter", 0.0), 1e-9,
            "HandleDestroyedObject runs to its end for a fallen inhibitor");
        // Batch 310: DisableBarracksSpawn's SetDisableMinionSpawn (the client's body) arms the barracks' countdown to its dampener's
        // return; the shared barracks update never reads it in its wave spawn, so the fallen inhibitor's team keeps
        // spawning in that lane (this asserted 0 before).
        var paused = host.GetMinionSpawnInfo(Team.Chaos, "C", 1);
        TestAssert.True(paused.Melee + paused.Caster + paused.Siege + paused.Super > 0,
            "the fallen inhibitor's team barracks in that lane keeps spawning");
        var running = host.GetMinionSpawnInfo(Team.Order, "C", 1);
        TestAssert.True(running.Melee + running.Caster + running.Siege + running.Super > 0,
            "the other team's barracks in that lane keeps spawning");
    }

    /// <summary>
    /// Batch 314: the inhibitor's return is the barracks' countdown its own script arms. HandleDestroyedObject's DisableBarracksSpawn calls
    /// SetDisableMinionSpawn(barracks, DISABLE_MINION_SPAWN_BASE_TIME + MAG x NumOfSpawnDisables) (the barracks' time field takes that value,
    /// its flag field is set to 1); the shared barracks update counts it down and at zero sets the linked dampener's state to 1.
    /// The check lowers the shipped 300 to 20 so the script, not a clock of the engine's own, is what returns the inhibitor.
    /// </summary>
    [Check("scripting.inhibitor-returns-on-the-barracks-script-countdown")]
    public static void InhibitorReturnsOnTheBarracksScriptCountdown()
    {
        using var match = new BbM3.Match("Annie");
        var level = match.Runtime.Waves!.LevelScript;
        TestAssert.Near(300.0, level.Number("DISABLE_MINION_SPAWN_BASE_TIME", 0.0), 1e-9, "the shipped base time is 300 s");
        level.SetScriptGlobal("DISABLE_MINION_SPAWN_BASE_TIME", 20.0);
        var inhibitor = match.Runtime.World.Entities.Values.Single(entity => entity.Name == "Barracks_T2_C1");
        TestAssert.True(match.Runtime.Combat.Kill(inhibitor, match.Hero.NetId), "the Chaos middle inhibitor falls");
        var spawn = level.GetMinionSpawnInfo(Team.Chaos, "C", 1);
        TestAssert.True(spawn.Melee + spawn.Caster + spawn.Siege + spawn.Super > 0, "its lane's barracks keeps spawning during the countdown");
        match.Advance(19.0);
        TestAssert.False(inhibitor.Alive, "still down before the script's 20 s");
        match.Advance(2.0);
        TestAssert.True(inhibitor.Alive, "back when the barracks' countdown reaches zero");
        TestAssert.Near(inhibitor.MaxHealth, inhibitor.Health, 1e-9, "back at full health");
    }

    /// <summary>
    /// Batch 336: the same return at the shipped time, in a production match (MatchRuntime.CreateProduction wires the level script's
    /// SetDisableMinionSpawn to the combat countdown). A fallen inhibitor is down for DISABLE_MINION_SPAWN_BASE_TIME 300 + MAG 0 and back at 300.
    /// </summary>
    [Check("scripting.inhibitor-returns-at-the-shipped-300-seconds-in-a-match")]
    public static void InhibitorReturnsAtTheShipped300SecondsInAMatch()
    {
        using var match = new BbM3.Match("Annie");
        var inhibitor = match.Runtime.World.Entities.Values.Single(entity => entity.Name == "Barracks_T2_C1");
        TestAssert.True(match.Runtime.Combat.Kill(inhibitor, match.Hero.NetId), "the Chaos middle inhibitor falls");
        TestAssert.Near(300.0, inhibitor.RespawnAt - match.Runtime.Combat.CurrentTime, 1e-9, "the script arms the shipped 300 s");
        match.Advance(299.5);
        TestAssert.False(inhibitor.Alive, "still down half a second before 300 s");
        match.Advance(1.0);
        TestAssert.True(inhibitor.Alive, "back at 300 s");
        TestAssert.Near(inhibitor.MaxHealth, inhibitor.Health, 1e-9, "back at full health");
    }

    private static (LevelScriptHost Host, List<string> Errors) CreateHost()
    {
        var errors = new List<string>();
        var host = new LevelScriptHost(new WorldState())
        {
            OnError = failure => errors.Add(failure.Hook + ": " + failure.Message),
            TotalTeamMinionsSpawned = _ => 0,
        };
        return (host, errors);
    }

    private static Entity Inhibitor(Team team, string lane) => new(
        0x50000000u, EntityKind.Inhibitor, UnitType.Structure, team, Position2.Zero)
    {
        Name = (team == Team.Order ? "Barracks_T1_" : "Barracks_T2_") + lane + "1",
    };
}
