using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat.live-spree-and-structure-statistics")]
    public static void SpreeAndStructureCountersUseKillCreditAndSurviveDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var combat = match.Runtime.Combat;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var ally = match.Hero(2);
            for (var kill = 1; kill <= 3; kill++)
            {
                match.Clear();
                TestAssert.True(combat.Kill(enemy, hero.NetId));
                TestAssert.False(combat.Kill(enemy, hero.NetId), "duplicate deaths cannot count twice");
                match.Runtime.Delivery.Flush();
                TestAssert.Equal(kill, hero.ChampionKills);
                TestAssert.Equal(kill == 1 ? 0 : 1, hero.KillingSprees);
                TestAssert.Equal(kill == 1 ? 0 : kill, hero.LargestKillingSpree);
                foreach (var capture in match.Captures)
                {
                    var death = capture.Packets.Single(p => Opcode(p) == 0x61).Plaintext.Span;
                    TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(death[1..]));
                    TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(death[5..]));
                    // Batch 690: the client raises no OnChampionDie (event 2) itself, so the server's 0xAB of id 2 is the one event besides the
                    // multi-kills (4..8); kill, first blood and spree (1, 3, 9) still come from the client's own death dispatch.
                    TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x4A ||
                        Opcode(p) == 0xAB && p.Plaintext.Span[5] is not (2 or (>= 4 and <= 8))),
                        "stock death dispatch derives kill/first-blood/spree events once");
                }
                combat.ScheduleRespawn(enemy);
                TestAssert.True(combat.RespawnImmediately(enemy));
                match.Runtime.Delivery.Flush();
            }
            // Playtest B3 #12 (26 Sept 2026): only a credited champion kill
            // ends a spree; an uncredited execution now keeps it.
            combat.Kill(hero, enemy.NetId);
            TestAssert.Equal(0, hero.KillStreak);
            TestAssert.Equal((1, 3), (hero.KillingSprees, hero.LargestKillingSpree));
            var box = new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.ShacoBox,
                UnitType.Minion, team, hero.Position) { OwnerNetId = hero.NetId, Health = 100, MaxHealth = 100 };
            match.Runtime.World.Register(box);
            for (var kill = 0; kill < 2; kill++)
            {
                TestAssert.True(combat.Kill(enemy, box.NetId), "owned damage still credits a dead champion");
                combat.ScheduleRespawn(enemy);
                TestAssert.True(combat.RespawnImmediately(enemy));
            }
            TestAssert.Equal((2, 3), (hero.KillingSprees, hero.LargestKillingSpree));
            TestAssert.Equal(0, ally.KillingSprees);

            foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
            {
                var building = match.Runtime.World.Entities.Values.First(u => u.Kind == kind && u.Team == enemy.Team);
                TestAssert.True(combat.Kill(building, box.NetId));
                TestAssert.False(combat.Kill(building, box.NetId));
            }
            TestAssert.Equal((1, 1, 1), (hero.TurretsKilled, hero.InhibitorsKilled, hero.NexusesKilled));
            TestAssert.Equal((0, 0, 0), (ally.TurretsKilled, ally.InhibitorsKilled, ally.NexusesKilled),
                "global structure gold does not grant every teammate a last hit");
            var saved = match.Runtime.Session.Result!.Players[0];
            TestAssert.Equal((2, 3, 1, 1, 1), (saved.KillingSprees, saved.LargestKillingSpree,
                saved.TurretsKilled, saved.InhibitorsKilled, saved.NexusesKilled));
        }
    }

    [Check("combat.economy.unit-rewards")]
    public static void UnitRewardsUseCharacterDataAndNativeDefaults()
    {
        // Playtest B3 #12 (26 Sept 2026): kill rewards come from CharacterData,
        // with the native loader defaults (Gold 25, Exp 48) for missing keys.
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime; var combat = runtime.Combat;
        var annie = match.Hero(0); var enemy = match.Hero(1);

        annie.Inventory = [new InventorySlot(2044), null, null, null, null, null];
        TestAssert.True(match.TryCastGround(0, 4, annie.Position + new Position2(200, 0)),
            "the sight ward is placed through the production item path");
        var ward = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Ward);
        TestAssert.Near(25.0, ward.GoldReward, 1e-9, "SightWard omits gold: native default 25");
        TestAssert.Near(0.0, ward.ExperienceReward, 1e-9, "SightWard stores zero XP");
        var gold = enemy.Gold;
        TestAssert.True(combat.Kill(ward, enemy.NetId));
        TestAssert.Near(gold + 25.0, enemy.Gold, 1e-9, "a ward kill pays 25 gold");

        match.CastGround(0, annie.Position + new Position2(300, 0), 3);
        Entity? Tibbers() => runtime.World.Entities.Values.SingleOrDefault(unit =>
            unit.Kind == EntityKind.Pet && unit.OwnerNetId == annie.NetId && unit.Alive);
        for (var tick = 0; tick < 30 && Tibbers() is null; tick++) runtime.Tick(0.1);
        var tibbers = Tibbers() ?? throw new InvalidOperationException("Tibbers did not spawn");
        TestAssert.Near(50.0, tibbers.GoldReward, 1e-9, "AnnieTibbers GoldGivenOnDeath");
        TestAssert.Near(0.0, tibbers.ExperienceReward, 1e-9, "AnnieTibbers ExpGivenOnDeath");
        gold = enemy.Gold;
        TestAssert.True(combat.Kill(tibbers, enemy.NetId));
        TestAssert.Near(gold + 50.0, enemy.Gold, 1e-9, "a Tibbers kill pays 50 gold");

        var box = runtime.Spells.ScriptPets!.Boxes.SpawnForScript(annie, annie.Position + new Position2(0, 300), combat.CurrentTime);
        TestAssert.Near(10.0, box.GoldReward, 1e-9, "ShacoBox GoldGivenOnDeath");
        TestAssert.Near(0.0, box.ExperienceReward, 1e-9, "ShacoBox ExpGivenOnDeath");
        gold = enemy.Gold;
        TestAssert.True(combat.Kill(box, enemy.NetId));
        TestAssert.Near(gold + 10.0, enemy.Gold, 1e-9, "a Jack in the Box kill pays 10 gold");

        // Batch 164 dropped the hand-written YorickGhoulService, which left gold and XP at the entity default: the
        // YorickSpectralGhoul record says Data*GoldGivenOnDeath '0' and Data*ExpGivenOnDeath '0', so the generic
        // BBSpawnMinion path reproduces the 0/0. The same record's Data*BaseStaticHPRegen is 2.00, which the service
        // never set and the record now wins (the Yorick*Logic buff is the ghoul's own decay on top of it).
        var ghoul = YorickScriptSupport.SpawnUnit(runtime, annie, "Spectral", annie.Position + new Position2(0, 400), combat.CurrentTime);
        TestAssert.Near(0.0, ghoul.GoldReward, 1e-9, "YorickSpectralGhoul GoldGivenOnDeath");
        TestAssert.Near(0.0, ghoul.ExperienceReward, 1e-9, "YorickSpectralGhoul ExpGivenOnDeath");
        TestAssert.Near(2.0, ghoul.HealthRegen, 1e-9, "YorickSpectralGhoul BaseStaticHPRegen, which the record now sets");
        gold = enemy.Gold;
        TestAssert.True(combat.Kill(ghoul, enemy.NetId));
        TestAssert.Near(gold, enemy.Gold, 1e-9, "a ghoul kill pays nothing");

        var dragonStats = NeutralStatsCalculator.Calculate(runtime.GameData, "Dragon");
        TestAssert.Near(0.0, dragonStats.GlobalExperienceReward, 1e-9, "Dragon stores no global XP");
        var dragon = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Neutral, UnitType.Monster, Team.Neutral, annie.Position)
        {
            Name = "Dragon", Health = 100.0, MaxHealth = 100.0,
            GoldReward = dragonStats.GoldReward,
            GlobalGoldReward = dragonStats.GlobalGoldReward,
            ExperienceReward = dragonStats.ExperienceReward,
        });
        gold = annie.Gold;
        TestAssert.True(combat.Kill(dragon, annie.NetId));
        TestAssert.Near(gold + 190.0 + 25.0, annie.Gold, 1e-9,
            "the Dragon killer adds the native default 25 to the 190 global gold");

        // Oct 2011 Kill page: an execution keeps the victim's streak and
        // bounty; only a credited champion kill resets them.
        annie.KillStreak = 3;
        annie.DeathStreak = 0;
        var turret = runtime.World.Entities.Values.First(unit =>
            unit.Kind == EntityKind.Turret && unit.Team == enemy.Team);
        TestAssert.True(combat.Kill(annie, turret.NetId), "a turret executes Annie");
        TestAssert.Equal((1, 3, 0), (annie.ChampionDeaths, annie.KillStreak, annie.DeathStreak),
            "the execution counts a death without ending the streak");
        combat.ScheduleRespawn(annie);
        TestAssert.True(combat.RespawnImmediately(annie));
        gold = enemy.Gold;
        TestAssert.True(combat.Kill(annie, enemy.NetId));
        TestAssert.Near(gold + ProgressionRules.HeroBounty(3, 0, firstBlood: true), enemy.Gold, 1e-9,
            "the later champion kill still pays the preserved spree bounty");
        TestAssert.Equal((2, 0, 1), (annie.ChampionDeaths, annie.KillStreak, annie.DeathStreak),
            "a credited kill ends the streak");

        enemy.Inventory = [new InventorySlot(3096), null, null, null, null, null];
        // Batch 149 step 5: the income is the window buff the copy opens, not the inventory row (no shipped item
        // record carries FlatGoldPer10Mod) — Items/3096.luaobj's OnBuffActivate adds PhilosophersStone and
        // Spells/PhilosophersStone.luaobj's BuffOnUpdateStats is the whole income (BBIncStat IncFlatGoldPer10Mod,
        // Delta 5). The copy binds on the next spell update (BbItemModule.SyncInventory), so the inventory set has
        // to see one update before the window pays.
        runtime.Tick(0.01);
        gold = enemy.Gold;
        combat.CombatTick(1.0, regenerate: false);
        TestAssert.Near(gold, enemy.Gold, 1e-9, "a GP10 item pays nothing before 90 seconds");
        combat.CombatTick(89.0 - combat.CurrentTime, regenerate: false);
        gold = enemy.Gold;
        combat.CombatTick(2.0, regenerate: false);
        // Batch 161 (retail footage — the Xerath champion spotlight, October 2011): ambient income is continuous
        // (ai_AmbientGoldAmount / ai_AmbientGoldInterval = 1.3 g/s), so the 89..91 window earns exactly one second
        // of it — the second after the 90-second gate — along with the GP10 accrual of that second.
        TestAssert.Near(gold + 1.3 * 1.0 +
            (0.5 + 0.1 * enemy.LoadoutGoldPer10) * 1.0, enemy.Gold, 1e-8,
            "GP10 and ambient income accrue only for the second after the 90-second gate");
    }
}
