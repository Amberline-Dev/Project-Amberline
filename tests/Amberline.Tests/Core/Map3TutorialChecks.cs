using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Ai;

namespace Amberline.Tests;

/// <summary>
/// Batch 563 (Basic Tutorial, phase 1): the shipped TutorialScript runs in the level script's state on map 3's structures (from its scene,
/// Map3LayoutChecks), through its opening lessons: the welcome popup, the first voice lines and the opening skirmish.
/// </summary>
public static class Map3TutorialChecks
{
    [Check("map3.tutorial-script-runs-its-first-steps")]
    public static void TheShippedTutorialScriptRunsItsOpeningLessons()
    {
        var map3 = GameDataPaths.Default.ForMap(GameMaps.Tutorial);
        var layout = MapLayout.For(map3);
        var world = new WorldState();
        uint next = 1;
        Entity Hero(string champion, Team team) => world.Register(new Entity(next++, EntityKind.Hero, UnitType.Hero, team, layout.TeamSpawn(team))
            { Name = champion, ChampionName = champion, Health = 600, MaxHealth = 600 });
        var ashe = Hero("Ashe", Team.Order);
        var nunu = Hero("Nunu", Team.Order);
        var yi = Hero("MasterYi", Team.Chaos);
        var buildings = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (var turret in layout.Turrets)
            buildings[turret.Name] = world.Register(new Entity(next++, EntityKind.Turret, UnitType.Turret, turret.Team, turret.Position) { Name = turret.Name });
        foreach (var inhibitor in layout.Inhibitors)
            buildings[inhibitor.Name] = world.Register(new Entity(next++, EntityKind.Inhibitor, UnitType.Structure, inhibitor.Team, inhibitor.Position) { Name = inhibitor.Name });
        foreach (var nexus in layout.Nexuses)
            buildings[nexus.Name] = world.Register(new Entity(next++, EntityKind.Nexus, UnitType.Structure, nexus.Team, nexus.Position) { Name = nexus.Name });

        var errors = new List<string>();
        var calls = new List<TutorialCall>();
        var blocks = new List<(string Function, object? Target, object? Pos)>();
        var granted = new List<(Entity Hero, int Item)>();
        var host = new LevelScriptHost(world, new AiScriptCatalog(map3))
        {
            OnError = failure => errors.Add(failure.Hook + ": " + failure.Message),
            TotalTeamMinionsSpawned = _ => 0,
            TutorialEngine = calls.Add,
            GrantItem = (hero, item) => { granted.Add((hero, item)); return null; },
            // The BuildingBlocks natives behind BBLuaConversionLibrary: GetChampionBySkinName answers the hero, the rest are recorded.
            RunBuildingBlock = (function, parameters, scope, owner) =>
            {
                blocks.Add((function, scope["Target"] ?? scope["Owner"], scope["Pos"]));
                if (function == "BBGetChampionBySkinName")
                    scope["DestObj"] = world.Heroes().FirstOrDefault(hero => hero.ChampionName == parameters["Skin"] as string &&
                                                                             hero.Team == (parameters["Team"] is 200.0 ? Team.Chaos : Team.Order));
                if (function == "BBSpawnMinion")
                    scope["DestObj"] = world.Register(new Entity(next++, EntityKind.Minion, UnitType.Minion,
                        parameters["Team"] is 200.0 ? Team.Chaos : Team.Order, layout.TeamSpawn(Team.Order))
                        { Name = parameters["Skin"] as string ?? "", Health = 100, MaxHealth = 100 });
            },
        };

        host.OnLevelInit(0.0);
        TestAssert.True(host.TutorialLoaded, "map 3 loads its TutorialScript");
        // Batch 593: OnTutorialInit runs at the game's first step (not TUTORIAL_START_TIME's 20 s), locks every input and opens the popup.
        TestAssert.False(host.TutorialSteps.Any(step => step.Step == "OnTutorialInit"), "nothing before the first step");
        host.Advance(0.0, 0.0);
        TestAssert.True(host.TutorialSteps.FirstOrDefault().Step == "OnTutorialInit", "OnTutorialInit is the first step, at the start");
        TestAssert.True(calls.Any(call => call.Native == "OpenTutorialPopup"), "the welcome popup opens at the start");
        host.AdvanceTo(20.0);
        TestAssert.Equal(0, errors.Count, "OnTutorialInit raises nothing: " + string.Join("; ", errors));
        TestAssert.True(host.TutorialWaits.Contains("Popup:EventWelcome"), "the welcome popup waits for its OK");
        var popup = calls.Single(call => call.Native == "OpenTutorialPopup");
        TestAssert.Equal("game_messagebox_text_tutorial", popup.Text, "the popup's text key");

        // The script's own structure plan, through GetTurret with Map3's numbering (FRONT_TOWER 2, BACK_TOWER 1, CENTER_LANE 1).
        TestAssert.True(buildings["Turret_T1_C_07"].Protected, "secondOrderTurret (BACK_TOWER, T1_C_07) is invulnerable");
        TestAssert.False(buildings["Turret_T1_C_08"].Protected, "firstOrderTurret (FRONT_TOWER, T1_C_08) is open");
        TestAssert.True(buildings["Turret_T2_L_01"].Protected, "firstChaosTurret (T2_L_01) is invulnerable");
        TestAssert.True(buildings["Barracks_T2_C1"].Protected, "the Chaos dampener is invulnerable");
        TestAssert.True(buildings["HQ_T2"].Protected, "the Chaos HQ is invulnerable");

        // The BBLuaConversionLibrary wrappers reach their BuildingBlocks natives with units and points (OnTutorialInit pc 0009-0012, 0252-0311).
        var teleport = blocks.First(block => block.Function == "BBTeleportToPosition");
        TestAssert.True(ReferenceEquals(ashe, teleport.Target), "the player is teleported");
        TestAssert.Near(780.75, ((BbTable)teleport.Pos!)["x"] is double x ? x : double.NaN, 1e-9, "to playerStartPoint");
        TestAssert.True(blocks.Any(block => block.Function == "BBSpellBuffAdd" && ReferenceEquals(block.Target, yi)), "Master Yi (botID) is buffed");
        TestAssert.True(blocks.Any(block => block.Function == "BBSpellBuffAdd" && ReferenceEquals(block.Target, nunu)), "Nunu (allyID) is buffed");
        TestAssert.True(calls.Any(call => call.Native == "ToggleFogOfWar"), "the fog is toggled");
        TestAssert.True(calls.Any(call => call.Native == "MoveCameraToPoint" && ReferenceEquals(call.Unit, ashe)), "the camera flies to the start");

        // OK -> EventWelcome (audio with no callback, EventTheObject armed 4 s later) -> its audio's end -> EventInLoL.
        host.TutorialPopupClosed(21.0);
        TestAssert.True(calls.Any(call => call.Native == "PlayTutorialAudioEvent" && call.Text == "Tutorial_Welcome" && !call.Flag),
            "the welcome line asks for no callback");
        host.AdvanceTo(25.0);
        var theObject = calls.Last(call => call.Native == "PlayTutorialAudioEvent");
        TestAssert.Equal("Tutorial_TheObject", theObject.Text, "EventTheObject plays its line");
        TestAssert.True(theObject.Flag, "and asks for the line's end");
        host.TutorialAudioFinished(theObject.Id, 27.0);
        TestAssert.True(blocks.Count(block => block.Function == "BBSpawnMinion") >= 16, "EventInLoL spawns the opening skirmish");
        TestAssert.Equal("OnTutorialInit,EventWelcome,EventTheObject,EventInLoL",
            string.Join(",", host.TutorialSteps.Select(step => step.Step).Where(step => step != "AllowDamageOnBuildings")),
            "the opening lessons, in the script's order");
        TestAssert.Equal(0, errors.Count, "no lesson raises: " + string.Join("; ", errors));

        // Batch 576: EventForThisBattle's KillStartingMinions gives the sixteen skirmish minions ExpirationTimer. One that outlives the timer
        // (here every one: the recorded natives add no buff) is retired at the next pass, once.
        var retired = new List<Entity>();
        host.RetireUnit = unit => { retired.Add(unit); unit.Alive = false; };
        host.TutorialAutoAcknowledgeSeconds = 3.0;
        for (var at = 27.5; at <= 55.0; at += 0.5) host.AdvanceTo(at);
        TestAssert.True(host.TutorialSteps.Any(step => step.Step == "EventForThisBattle"), "the opening skirmish ends at EventForThisBattle");
        TestAssert.Equal(16, retired.Count, "each skirmish minion is retired once");
        TestAssert.Equal(0, host.TutorialExpiredAlive, "no expired minion is left alive");

        // The packets: text at +5 (133 bytes), the VO event's callback and id after its two 64-byte names, the C2S id at +5.
        var objective = TutorialPackets.Text(TutorialPackets.ShowObjectiveTextOpcode, "game_tutorial_objective_waypointI").Plaintext.Span;
        TestAssert.Equal(133, objective.Length, "ShowObjectiveText is 5 + 128 bytes");
        TestAssert.Equal((byte)'g', objective[5], "the key starts at +5");
        var audio = TutorialPackets.PlayVoAudioEvent("Tutorial", "Tutorial_TheObject", true, 9).Plaintext.Span;
        TestAssert.Equal(138, audio.Length, "PlayVOAudioEvent is 5 + 64 + 64 + 1 + 4 bytes");
        TestAssert.Equal((byte)1, audio[0x85], "callbackType at +0x85");
        TestAssert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(audio[0x86..]), "audioEventNetworkID at +0x86");
        TestAssert.Equal(9u, TutorialPackets.AudioEventFinishedId([0x05, 0, 0, 0, 0, 9, 0, 0, 0]), "C2S 0x05 carries the id at +5");

        // Batch 580: EventWelcome's CreateItem grants Cloth Armor and Chain Vest (proto 0.7 pc 0003-0010); EventTheObject highlights the Chaos
        // HQ (proto 0.8 pc 0004-0008) and EventInLoL removes it; EventMoveChampion locks the camera (proto 0.12 pc 0010-0012).
        TestAssert.Equal("Ashe:1029,Ashe:1031", string.Join(",", granted.Select(grant => $"{grant.Hero.ChampionName}:{grant.Item}")));
        TestAssert.True(calls.Any(call => call.Native == "CreateUnitHighlight" && ReferenceEquals(call.Unit, buildings["HQ_T2"])), "the HQ is highlighted");
        TestAssert.True(calls.Any(call => call.Native == "RemoveUnitHighlight" && ReferenceEquals(call.Unit, buildings["HQ_T2"])), "and its highlight removed");
        TestAssert.True(calls.Any(call => call.Native == "LockCamera" && call.Flag && ReferenceEquals(call.Unit, ashe)), "the camera is locked");
        // The packets: case 0x52's element, type, number, subcategory and flag; the unit at +5 of 0x5C/0xBC; 0x7C's lock at +5.
        TestAssert.Equal("52000000000106000001", Convert.ToHexString(TutorialPackets.ToggleUiHighlight(1, 6, 0, 0, true).Plaintext.Span));
        TestAssert.Equal("5C0000000007000000", Convert.ToHexString(TutorialPackets.UnitHighlight(7, create: true).Plaintext.Span));
        TestAssert.Equal("BC0000000007000000", Convert.ToHexString(TutorialPackets.UnitHighlight(7, create: false).Plaintext.Span));
        TestAssert.Equal((byte)1, VisionPackets.LockCamera(7, true).Plaintext.Span[5], "LockCamera's lock at +5");
        // The camera report: the ground point along the stock camera's direction (34 bytes: position, direction, client id, counter).
        var camera = new byte[34];
        camera[0] = TutorialPackets.CameraClientOpcode;
        foreach (var (at, value) in new[] { (5, 100f), (9, 1000f), (13, 200f), (17, 0f), (21, -0.829f), (25, 0.559f) })
            BinaryPrimitives.WriteSingleLittleEndian(camera.AsSpan(at), value);
        var focus = TutorialPackets.CameraFocus(camera, _ => 0.0) ?? throw new InvalidOperationException("no camera focus");
        TestAssert.Near(100.0, focus.X, 1e-3, "the camera looks straight ahead in X");
        TestAssert.Near(200.0 + 0.559 * 1000.0 / 0.829, focus.Z, 0.05, "and 1000/0.829 along its direction in Z");
        TestAssert.True(TutorialPackets.CameraFocus(camera.AsSpan(0, 33), _ => 0.0) is null, "a short report is ignored");
    }

    /// <summary>
    /// Batch 580: the whole Basic Tutorial, headless. The tool's stand-in player answers each lesson with the client's own packets (the
    /// summoner Heal, Volley, Recall, the shop's 0x60 and 0x87, the camera's 0x30), the ally charges the enemy turret and Master Yi the player
    /// (LevelScriptHost.DirectTutorialBots), and the match ends in Order's victory with the script's last lessons played over the ceremony.
    /// Batch 593: every packet of the stand-in goes through the session's wire boundary as the stock client's do (the popup's 0xD5, the audio
    /// events' 0x05, the skill-up 0x3E, the casts, the orders; nothing is answered inside the host), the tutorial starts at the game's first
    /// step, and the opening skirmish's minions walk the lane.
    /// </summary>
    [Check("map3.tutorial-plays-to-victory", Serial = true, TimeoutSeconds = 600)]
    public static void TheTutorialPlaysToVictory()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-map3-tutorial-" + Guid.NewGuid().ToString("N"));
        LevelScriptHost? level = null;
        var opening = new Dictionary<uint, (Position2 First, double Farthest)>();
        try
        {
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(new Amberline.Tools.Diagnostics.HeadlessMatchOptions(563, Path.Combine(root, "map3"), 25.0,
                Amberline.Tools.Diagnostics.HeadlessMatch.TutorialRoster)
            {
                MapId = GameMaps.Tutorial,
                OnStep = runtime =>
                {
                    level ??= runtime.Waves?.LevelScript;
                    if (level is null || level.TutorialStepRan("EventForThisBattle")) return;
                    // The opening skirmish (SpawnStartingMinions): how far each of its units gets from where it was spawned.
                    foreach (var unit in runtime.World.Entities.Values.Where(unit => unit.Alive && unit.AiScript == "Minion.lua"))
                    {
                        var (first, farthest) = opening.TryGetValue(unit.NetId, out var seen) ? seen : (unit.Position, 0.0);
                        var (dx, dz) = (unit.Position.X - first.X, unit.Position.Z - first.Z);
                        opening[unit.NetId] = (first, Math.Max(farthest, Math.Sqrt(dx * dx + dz * dz)));
                    }
                },
            });
            Console.WriteLine(report.Summary());
            // Batch 593: the script's walking minions (the opening skirmish and the later replacements) brush a blocked cell for one step
            // now and then (seed 563: 17 visits of 0.1 s each); the wire stays clean.
            TestAssert.True(report is { Outcome: "natural", Winner: "Order", PacketViolations: 0, LifecycleViolations: 0, InvariantEvents: <= 24 },
                report.Summary());
            TestAssert.True(level!.TutorialSteps[0] is { Step: "OnTutorialInit", At: < 1.0 }, "the tutorial starts at the game's start");
            TestAssert.Equal(16, opening.Count, "the sixteen units of the opening skirmish");
            TestAssert.True(opening.Values.Count(unit => unit.Farthest >= 1000.0) >= 12,
                "the opening skirmish walks its lane: " + string.Join(", ", opening.Values.Select(unit => Math.Round(unit.Farthest))));
            var steps = level.TutorialSteps.Select(step => step.Step).ToHashSet(StringComparer.Ordinal);
            foreach (var lesson in new[]
                     {
                         "EventLevelUpGrats", "EventSummonerHealCongrats", "EventVolleyCast", "EventMoreDamage", "EventGold", "EventPowerEquipment", "EventHighAttackSpeed",
                         "EventOhNo", "EnemyMinionSetup", "PostDeathBubble", "EventMinionAttack", "EventTurnedTide", "EventTurretDeath", "EventLookOut",
                         "EventGreatWork", "EventInhibitorDisabled", "EventAlmostThere", "EventSucceedDelay", "EventSucceededCombined",
                     })
                TestAssert.True(steps.Contains(lesson), $"the tutorial reaches {lesson}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
