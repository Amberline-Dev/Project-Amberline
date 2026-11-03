using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// Map1's shipped <c>NeutralMinionSpawn.luaobj</c> is the camp table and the spawn/respawn
/// schedule. The rows below are its own <c>NeutralMinionCamps</c> as <c>NeutralMinionInit</c>
/// leaves them, the experience column of its <c>DefaultNeutralMinionValues</c>, and the
/// <c>UniqueNames</c> its <c>InitializeNeutralMinionInfo</c> stores; the engine used to carry
/// the same facts as hand-written literals in <c>NeutralContracts</c> and those are gone.
/// The Map1 bytecode listing of that script is the reference for these values.
/// </summary>
public static class NeutralMinionSpawnScriptChecks
{
    /// <summary>
    /// One <c>NeutralMinionCamps</c> entry. <c>Points</c>/<c>Faces</c> are the script's
    /// <c>Make3DPoint</c> literals; the shipped compiler wrote them as 32-bit floats, so the
    /// comparison is to 1e-3. Camps 2, 5, 6, 8 and 11 carry one more point than their roster has
    /// members: <c>script = retail</c>, the hand-written table used to keep only the member slots.
    /// </summary>
    private sealed record CampRow(int Number, string[] Group, double Delay, double Respawn, int Level,
        double[][] Points, double[][] Faces);

    private static readonly CampRow[] Table =
    [
        new(1, ["AncientGolem", "YoungLizard", "YoungLizard"], 15, 300, 1,
            [[3632.700195, 42.270264, 7600.373047], [3552.700195, 42.270264, 7799.373047],
             [3452.700195, 42.270264, 7590.373047]],
            [[3418, 45, 7726], [3418, 45, 7726], [3418, 45, 7726]]),
        new(2, ["wolf", "wolf", "GiantWolf"], 0, 100, 1,
            [[3323.678223, 44.799927, 6373.345703], [3523.678223, 44.799927, 6223.345703],
             [3373.678223, 44.799927, 6223.345703], [3523.678223, 44.799927, 6373.345703]],
            [[3294, 46, 6165], [3294, 46, 6165], [3294, 46, 6165], [3294, 46, 6165]]),
        new(3, ["Wraith", "LesserWraith", "LesserWraith", "LesserWraith"], 0, 100, 1,
            [[6452.914795, 50.27832, 5336.885742], [6652.914795, 50.27832, 5336.885742],
             [6452.914795, 50.27832, 5136.885742], [6652.914795, 50.27832, 5136.885742]],
            [[6552, 48, 5240], [6552, 48, 5240], [6552, 48, 5240], [6552, 48, 5240]]),
        new(4, ["LizardElder", "YoungLizard", "YoungLizard"], 15, 300, 1,
            [[7455.615234, 50.383179, 3890.202637], [7460.615234, 50.383179, 3710.202637],
             [7237.615234, 50.383179, 3890.202637]],
            [[7348, 48, 3829], [7348, 48, 3829], [7348, 48, 3829]]),
        new(5, ["Golem", "Golem"], 0, 100, 1,
            [[7916.842285, 50.652954, 2533.963379], [8216.842285, 50.652954, 2533.963379],
             [8066.842285, 50.652954, 2483.963379]],
            [[7913, 45, 2421], [8163, 45, 2413], [8163, 45, 2413]]),
        new(6, ["Dragon"], 50, 360, 1,
            [[9459.52, -69.7511, 4193.03], [9354.931152, -77.426025, 4241.924805],
             [9554.931152, -77.426025, 4041.924805]],
            [[9622, -69, 4490], [9622, -69, 4490], [9622, -69, 4490]]),
        new(7, ["AncientGolem", "YoungLizard", "YoungLizard"], 15, 300, 1,
            [[10386.605469, 47.390503, 6811.112061], [10586.605469, 47.390503, 6831.112061],
             [10526.605469, 47.390503, 6601.112061]],
            [[10679, 45, 6660], [10679, 45, 6660], [10679, 45, 6660]]),
        new(8, ["wolf", "wolf", "GiantWolf"], 0, 100, 1,
            [[10451.523438, 51.239746, 8116.424316], [10651.523438, 51.239746, 7916.424316],
             [10651.523438, 51.239746, 8116.424316], [10601.523438, 51.239746, 8216.424316]],
            [[10721, 53, 8282], [10721, 53, 8282], [10721, 53, 8282], [10721, 53, 8282]]),
        new(9, ["Wraith", "LesserWraith", "LesserWraith", "LesserWraith"], 0, 100, 1,
            [[7397.368164, 48.800049, 9141.405029], [7597.368164, 48.800049, 9141.405029],
             [7597.368164, 48.800049, 9341.405029], [7397.368164, 48.800049, 9341.405029]],
            [[7495, 46, 9259], [7495, 46, 9259], [7495, 46, 9259], [7495, 46, 9259]]),
        new(10, ["LizardElder", "YoungLizard", "YoungLizard"], 15, 300, 1,
            [[6504.240723, 51.270142, 10584.5625], [6704.240723, 51.270142, 10584.5625],
             [6504.240723, 51.270142, 10784.5625]],
            [[6618, 45, 10709], [6618, 45, 10709], [6618, 45, 10709]]),
        new(11, ["Golem", "Golem"], 0, 100, 1,
            [[5810.463867, 47.607788, 11925.473633], [6140.463867, 47.607788, 11935.473633],
             [5963.463867, 47.607788, 11944.473633]],
            [[5859, 30, 12006], [6111, 30, 12012], [6111, 30, 12012]]),
        new(12, ["Worm"], 800, 420, 2,
            [[4600.495117, 24.390137, 10250.461914]],
            [[4329.43, -71, 9887]]),
    ];

    /// <summary><c>DefaultNeutralMinionValues</c>'s <c>ExpGiven</c> column, by monster name.</summary>
    private static readonly (string Name, double Experience)[] Experience =
    [
        ("AncientGolem", 220), ("Dragon", 400), ("GiantWolf", 90), ("Golem", 140),
        ("LesserWraith", 20), ("Lizard", 90), ("LizardElder", 200), ("Worm", 800),
        ("Wraith", 120), ("YoungLizard", 30), ("wolf", 50),
    ];

    [Check("neutral.script-camp-table")]
    public static void CampTableComesFromTheScript()
    {
        var (scheduler, errors) = Create();
        scheduler.Start();

        TestAssert.Equal(0, errors.Count, "the chained map scripts raise nothing: " + string.Join("; ", errors));
        var camps = scheduler.LevelScript.NeutralCamps;
        TestAssert.Equal(12, camps.Count, "NeutralMinionCamps has one entry per camp");
        TestAssert.Equal(Table.Length, NeutralDefinitions.Camps.Count, "the engine reads one definition per camp");

        foreach (var expected in Table)
        {
            var camp = camps.Single(row => row.Number == expected.Number);
            var context = "camp " + expected.Number;
            TestAssert.Equal(1, camp.Groups.Count, $"{context} lists one group (Map1 has no alternatives)");
            TestAssert.Equal(expected.Group, camp.Groups[0].ToArray(), $"{context} roster");
            TestAssert.Near(expected.Delay, camp.GroupDelaySpawnTime, 1e-9, $"{context} GroupDelaySpawnTime");
            TestAssert.Near(expected.Respawn, camp.GroupsRespawnTime, 1e-9, $"{context} GroupsRespawnTime");
            TestAssert.Equal(expected.Level, camp.CampLevel, $"{context} CampLevel");
            TestAssert.Near(0.0, camp.HealthBonus, 1e-9, $"{context} starts with no health bonus");
            TestAssert.Near(0.0, camp.DamageBonus, 1e-9, $"{context} starts with no damage bonus");
            AssertPoints(expected.Points, camp.Positions, $"{context} Positions");
            AssertPoints(expected.Faces, camp.FacePositions, $"{context} FacePositions");

            // The engine's own definition is that table projected, keeping the names its scheduling has always used.
            var definition = NeutralDefinitions.Camps[expected.Number];
            TestAssert.Equal(expected.Group, definition.Roster.ToArray(), $"{context} engine roster");
            TestAssert.Near(expected.Delay, definition.InitialDelay, 1e-9, $"{context} engine initial delay");
            TestAssert.Near(expected.Respawn, definition.RespawnDelay, 1e-9, $"{context} engine respawn delay");
            TestAssert.Equal(expected.Level, definition.Level, $"{context} engine level");
            // The minimap icon follows the camp's monster (Summoner's Rift camps 6 and 12 are Dragon and Worm), so it is checked per monster.
            TestAssert.Equal(expected.Number is 6 or 12 ? "Epic" : "Camp", NeutralDefinitions.MinimapIcon(expected.Group[0]),
                $"Reconstruction: {context} minimap icon (NeutralMinionSpawn names none)");
        }

        // The experience the script's NeutralMinionDeath hands out, and the radius it shares it over.
        foreach (var (name, experience) in Experience)
            TestAssert.Near(experience, NeutralDefinitions.MonsterExperience[name], 1e-9,
                $"DefaultNeutralMinionValues {name} ExpGiven");
        TestAssert.Equal(Experience.Length, NeutralDefinitions.MonsterExperience.Count,
            "one experience row per monster name");
        TestAssert.Near(800.0, scheduler.LevelScript.HeroExperienceRadius, 1e-9, "HERO_EXP_RADIUS");
        TestAssert.Near(scheduler.LevelScript.HeroExperienceRadius, CombatService.NeutralExperienceShareRadius, 1e-9,
            "the engine shares neutral experience over the script's own HERO_EXP_RADIUS");
    }

    [Check("neutral.script-spawn-respawn-schedule")]
    public static void SpawnAndRespawnComeFromTheScript()
    {
        var (scheduler, _) = Create();
        // Start() chains NeutralMinionSpawn and runs InitializeNeutralMinionInfo; the script
        // itself stores no base delay, so the engine's InitialSpawnSeconds is the only one.
        scheduler.Start();
        TestAssert.Near(0.0, scheduler.StartedAt ?? -1, 1e-9, "the schedule is measured from the match clock");
        TestAssert.Near(100.0, NeutralDefinitions.InitialSpawnSeconds, 1e-9,
            "Reconstruction: the script stores only GroupDelaySpawnTime, and 100 s is the base the HUD times imply");

        // SpawnInitialNeutralMinions runs every camp as of 100 s, so a delay-0 camp is up on the
        // 100 s step and a delayed camp stays empty until its own GroupDelaySpawnTime has passed.
        scheduler.Tick(100.0);
        foreach (var expected in Table)
        {
            var state = scheduler.Camps[expected.Number];
            if (expected.Delay == 0.0)
            {
                TestAssert.True(state.Active, $"camp {expected.Number} spawns at 100 s");
                TestAssert.Equal(expected.Group.Length, state.MemberNetIds.Count,
                    $"camp {expected.Number} has one member per roster entry");
            }
            else
            {
                TestAssert.False(state.Active, $"camp {expected.Number} waits {expected.Delay} s more");
                TestAssert.Near(100.0 + expected.Delay, state.NextSpawnAt ?? -1, 1e-9,
                    $"camp {expected.Number} is due at 100 s + GroupDelaySpawnTime");
            }
        }

        // A delayed camp's first generation is the script's own camp timer firing, not a direct
        // spawn. Baron's GroupDelaySpawnTime of 800 is the last of them.
        scheduler.Tick(900.0);
        foreach (var expected in Table)
            TestAssert.True(scheduler.Camps[expected.Number].Active,
                $"camp {expected.Number} is up once its GroupDelaySpawnTime has passed");

        // The identity InitializeNeutralMinionInfo gave each member, one per roster index.
        var golem = scheduler.MembersForCamp(1);
        TestAssert.Equal("AncientGolem1.1.1|YoungLizard1.1.2|YoungLizard1.1.3",
            string.Join("|", golem.Select(member => scheduler.UniqueNames[member.NetId])),
            "camp 1's UniqueNames are name+camp.group.member");
        var baron = scheduler.MembersForCamp(12);
        TestAssert.Equal("Worm12.1.1",
            string.Join("|", baron.Select(member => scheduler.UniqueNames[member.NetId])),
            "camp 12's single member");

        // Emptying a camp arms the script's own respawn closure at GroupsRespawnTime after the
        // death that emptied it; only the last member's report empties the camp.
        foreach (var expected in Table)
        {
            var members = scheduler.MembersForCamp(expected.Number);
            for (var index = 0; index < members.Count; index++)
            {
                members[index].Entity.Alive = false;
                members[index].Entity.Health = 0.0;
                var emptied = scheduler.OnDeath(members[index].Entity, 1000.0);
                TestAssert.Equal(index == members.Count - 1, emptied,
                    $"camp {expected.Number} empties only on its last member (member {index + 1})");
            }
            TestAssert.False(scheduler.Camps[expected.Number].Active, $"camp {expected.Number} emptied");
            TestAssert.Near(1000.0 + expected.Respawn, scheduler.Camps[expected.Number].NextSpawnAt ?? -1, 1e-9,
                $"camp {expected.Number} respawns GroupsRespawnTime after the emptiness");
        }

        scheduler.Tick(1000.0 + 420.0 + 1.0);
        foreach (var expected in Table)
            TestAssert.Equal(2, scheduler.Camps[expected.Number].SpawnCycles,
                $"camp {expected.Number} respawned once its GroupsRespawnTime passed");
    }

    private static void AssertPoints(double[][] expected, IReadOnlyList<Position3> actual, string context)
    {
        TestAssert.Equal(expected.Length, actual.Count, $"{context} length");
        for (var index = 0; index < expected.Length; index++)
        {
            TestAssert.Near(expected[index][0], actual[index].X, 1e-3, $"{context}[{index}].X");
            TestAssert.Near(expected[index][1], actual[index].Y, 1e-3, $"{context}[{index}].Y");
            TestAssert.Near(expected[index][2], actual[index].Z, 1e-3, $"{context}[{index}].Z");
        }
    }

    [Check("neutral.map4-camps-keep-every-group-and-their-respawn-time")]
    public static void TwistedTreelineCampsKeepEveryGroupAndTheirRespawnTime()
    {
        // Map4 names the respawn RespawnTime (no GroupsRespawnTime), and camps 1-6 list two alternative groups that the script's own
        // GroupsChance roll picks between (the Map4 bytecode listing of NeutralMinionSpawn: proto 0.4 arms the respawn with
        // RespawnTime; proto 0.1.0 and 0.3 roll the group).
        var map4 = GameDataPaths.Default.ForMap(4);
        var camps = NeutralDefinitions.CampsFor(map4);
        TestAssert.Equal(8, camps.Count, "Twisted Treeline has eight camps");
        TestAssert.Equal(2, camps[1].Groups.Count, "camp 1 lists two alternative groups");
        TestAssert.Equal(2, camps[5].Groups.Count, "camp 5 lists two alternative groups");
        TestAssert.Near(75.0, camps[1].RespawnDelay, 1e-9, "camp 1's RespawnTime is the script's default 75");
        TestAssert.Near(180.0, camps[5].RespawnDelay, 1e-9, "camp 5's RespawnTime is 180");
        TestAssert.Near(300.0, camps[7].RespawnDelay, 1e-9, "camp 7 (blueDragon) RespawnTime is 300");
        TestAssert.Near(240.0, camps[8].RespawnDelay, 1e-9, "camp 8 (TwistedLizardElder) RespawnTime is 240");
        TestAssert.Equal("blueDragon", string.Join("|", camps[7].Roster), "camp 7's roster");

        // The engine's respawn is the script's: camp 8 emptied at 1000 s is due RespawnTime later, not at the death.
        var (scheduler, errors) = Create(new GameData(map4));
        scheduler.Start();
        TestAssert.Equal(0, errors.Count, "Map4's chained scripts raise nothing: " + string.Join("; ", errors));
        scheduler.Tick(130.0);
        var members = scheduler.MembersForCamp(8);
        TestAssert.Equal(1, members.Count, "camp 8 spawns its one TwistedLizardElder at 100 s + GroupDelaySpawnTime 30");
        members[0].Entity.Alive = false;
        members[0].Entity.Health = 0.0;
        TestAssert.True(scheduler.OnDeath(members[0].Entity, 1000.0), "camp 8's only member empties the camp");
        TestAssert.Near(1240.0, scheduler.Camps[8].NextSpawnAt ?? -1, 1e-9, "camp 8 respawns 240 s after the death");
    }

    [Check("neutral.minimap-icon-follows-the-monster")]
    public static void MinimapIconFollowsTheMonsterNotTheCampNumber()
    {
        // The epic monsters: Summoner's Rift's Dragon and Worm, and Twisted Treeline's blueDragon (fontconfig_en_US.txt:1857, 1866,
        // 1952 "Epic Monster"). Twisted Treeline's TwistedLizardElder is a plain "Monster" (fontconfig_en_US.txt:1944).
        TestAssert.Equal("Epic", NeutralDefinitions.MinimapIcon("Dragon"), "Dragon is epic");
        TestAssert.Equal("Epic", NeutralDefinitions.MinimapIcon("Worm"), "Worm (Baron Nashor) is epic");
        TestAssert.Equal("Epic", NeutralDefinitions.MinimapIcon("blueDragon"), "blueDragon is epic");
        TestAssert.Equal("Camp", NeutralDefinitions.MinimapIcon("TwistedLizardElder"), "TwistedLizardElder is a camp monster");
        TestAssert.Equal("Camp", NeutralDefinitions.MinimapIcon("AncientGolem"), "AncientGolem keeps the camp icon (SR camp 1 is unchanged)");
        var camps = NeutralDefinitions.CampsFor(GameDataPaths.Default.ForMap(4));
        TestAssert.Equal("Epic", NeutralDefinitions.MinimapIcon(camps[7].Roster[0]), "Twisted Treeline camp 7 is the epic blueDragon");
        TestAssert.Equal("Camp", NeutralDefinitions.MinimapIcon(camps[8].Roster[0]), "Twisted Treeline camp 8 is a camp");
    }

    private static (JungleScheduler Scheduler, List<string> Errors) Create(GameData? data = null)
    {
        var errors = new List<string>();
        var world = new WorldState();
        var sink = new ProtocolNeutralPacketSink(_ => { }, new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(0, 15000, 0, 15000), (_, _) => 0.0);
        var scheduler = new JungleScheduler(world, data ?? GameData.Default, sink);
        scheduler.LevelScript.OnError = failure => errors.Add(failure.Hook + ": " + failure.Message);
        return (scheduler, errors);
    }
}
