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
            // now and then (seed 563: 17 visits of 0.1 s each); the wire stays clean. Batch 664: the scripted minions now fight as minions (they
            // answer calls for help and rank enemy minions above the player, MinionAiSystem.IsMinionUnit), so they chase one another through
            // the turret footprints more often: seed 563 has 63 such visits of 0.1 s, all by script minions (master 2a33fcc9 capped at 1500 s
            // with 394). The bound is the known brush, not a new mechanism.
            TestAssert.True(report is { Outcome: "natural", Winner: "Order", PacketViolations: 0, LifecycleViolations: 0, InvariantEvents: <= 80 },
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


    /// <summary>
    /// Batch 664 (owner's hand test, 3 November 2026), the Basic Tutorial headless (seed 664):
    /// <list type="bullet">
    /// <item>"Champ doesn't get damaged": EventNormalBattle's Aggro.lua wizards (TutorialScript proto 0.19) answer the player's attack
    /// (Aggro.lua OnCallForHelp, proto 0.3) and wound her before the level-up lesson (EventLevelUp, proto 0.22, then EventWounded's heal
    /// lesson). TutorialPlayerBuff only scales a hit by the health left and keeps the last point (DATA/Spells/TutorialPlayerBuff.luaobj).</item>
    /// <item>"Minions do not show yellow targeting border": a scripted minion that attacks the player sends her the 0xC8 outline.</item>
    /// <item>"Minions froze after destroying last turret": Master Yi, freed by EventTurretDeath (proto 0.74), comes for the player before
    /// EventLookOut (the camera finding him, proto 0.77) instead of waiting for it at his fountain.</item>
    /// <item>The turret Nunu attacks (firstChaosTurret, Turret_T2_L_01): its server health only falls after EventOhNo.</item>
    /// <item>Batch 671, "Tower attacked by nunu has no HP and full heals when he attacks" (the owner's game log of 3 November): the health the
    /// player's client holds for that turret, mirrored from what her seat is sent (0xCC LOCAL1 bit 8, the turret's mHP, and 0xB5's mHP). Each
    /// reveal wrote the turret's IsTargetableToTeam value 0x1800000 (OnTutorialInit's SetTargetable false) into bit 8, the minion layout's
    /// field: mHP 4.7e-38, an empty bar, until Nunu's first hit sent 1224.8.</item>
    /// </list>
    /// </summary>
    [Check("map3.tutorial-hand-test-findings", Serial = true, TimeoutSeconds = 600)]
    public static void TheTutorialHandTestFindings()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-map3-findings-" + Guid.NewGuid().ToString("N"));
        LevelScriptHost? level = null;
        Entity? ashe = null, yi = null, turret = null;
        var clock = 0.0;
        double lastAsheHealth = double.NaN, lastTurret = double.NaN, yiOrderedAt = double.NaN;
        string? yiOrderedAfter = null;
        double fightDamage = 0.0, turretHeals = 0.0, turretLowest = double.MaxValue;
        // Batch 675: the player's own last tower (secondOrderTurret, Turret_T1_C_07) under the Chaos waves once EventMinionAttack opens it.
        Entity? backTower = null;
        var backLowest = double.MaxValue;
        var outlines = new List<(uint Unit, uint Target)>();
        var minionIds = new HashSet<uint>();
        // Every mHP the player's seat is sent, per unit: 0xCC LOCAL1 bit 8 (a turret's mHP) and 0xB5's mHP at +9 (VisionPackets.EnterLocalVisibility).
        var clientHealth = new List<(uint Unit, float Health, bool AfterOhNo)>();
        // Batch 671, the owner's tutorial rules: where Yi is freed and EventTurretDeath's wave spawns, and Yi's respawn.
        var firstSeen = new HashSet<uint>();
        var wave = new List<Position2>();
        Position2? yiFreed = null, yiBack = null, midway = null;
        double yiDiedAt = double.NaN, yiBackAt = double.NaN;
        var yiBackOrdered = false;
        string? turretCharacter = null;
        try
        {
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(new Amberline.Tools.Diagnostics.HeadlessMatchOptions(664, Path.Combine(root, "map3"), 25.0,
                Amberline.Tools.Diagnostics.HeadlessMatch.TutorialRoster)
            {
                MapId = GameMaps.Tutorial,
                PacketSink = (peer, packet) =>
                {
                    var bytes = packet.Plaintext.Span;
                    if (peer != 0) return;
                    if (bytes.Length >= 9 && bytes[0] == 0xC8)
                        outlines.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..])));
                    var afterOhNo = level?.TutorialStepRan("EventOhNo") == true;
                    if (bytes.Length >= 13 && bytes[0] == 0xB5)
                        clientHealth.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), afterOhNo));
                    Span<uint> masks = stackalloc uint[5];
                    if (CombatPackets.TryReadReplicationBlock(bytes, out _, out var unit, masks) && (masks[1] & 0x100u) != 0)
                    {
                        // [u32 mask][one u32 per set bit] per registry from offset 15; CLIENT_ONLY (0) comes before LOCAL1 (1).
                        var at = 15 + (masks[0] != 0 ? 4 + 4 * System.Numerics.BitOperations.PopCount(masks[0]) : 0);
                        at += 4 + 4 * System.Numerics.BitOperations.PopCount(masks[1] & 0xFFu);
                        clientHealth.Add((unit, BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]), afterOhNo));
                    }
                },
                OnStep = runtime =>
                {
                    level ??= runtime.Waves?.LevelScript;
                    if (level is null || !level.TutorialStepRan("OnTutorialInit")) return;
                    var world = runtime.World;
                    clock = world.Clock.Now();
                    ashe ??= world.Heroes().Single(hero => hero.ChampionName == "Ashe");
                    yi ??= world.Heroes().Single(hero => hero.ChampionName == "MasterYi");
                    turret ??= world.Entities.Values.Single(unit => unit.Name == "Turret_T2_L_01");
                    backTower ??= world.Entities.Values.Single(unit => unit.Name == "Turret_T1_C_07");
                    if (backTower.Alive) backLowest = Math.Min(backLowest, backTower.Health);
                    foreach (var unit in world.Entities.Values.Where(unit => unit.AiScript is "Minion.lua" or "Aggro.lua")) minionIds.Add(unit.NetId);
                    turretCharacter ??= turret.BoundCharacter;
                    var hq = world.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
                    midway ??= new Position2((turret.Position.X + hq.Position.X) / 2.0, (turret.Position.Z + hq.Position.Z) / 2.0);
                    var turretDeathRan = level.TutorialStepRan("EventTurretDeath");
                    foreach (var unit in world.Entities.Values.Where(unit => unit.AiScript == "Minion.lua" && unit.Team == Team.Chaos))
                        if (firstSeen.Add(unit.NetId) && turretDeathRan && yiFreed is null) wave.Add(unit.Position);
                    if (turretDeathRan && yiFreed is null) yiFreed = yi.Position;
                    if (yiFreed is not null && !yi.Alive && double.IsNaN(yiDiedAt)) yiDiedAt = clock;
                    if (!double.IsNaN(yiDiedAt) && yi.Alive && yiBack is null) (yiBack, yiBackAt) = (yi.Position, clock);
                    if (yiBack is not null && (yi.AttackTarget == ashe.NetId || yi.HeroPursuit is not null)) yiBackOrdered = true;
                    // The minion fight: from EventNormalBattle to the level-up lesson that follows its last kill.
                    if (level.TutorialStepRan("EventNormalBattle") && !level.TutorialStepRan("EventLevelUp") && ashe.Alive && ashe.Health < lastAsheHealth)
                        fightDamage += lastAsheHealth - ashe.Health;
                    lastAsheHealth = ashe.Alive ? ashe.Health : double.NaN;
                    if (double.IsNaN(yiOrderedAt) && (yi.AttackTarget == ashe.NetId || yi.HeroPursuit is not null))
                    {
                        // The steps' moments are the level script's clock, not the world's; the order is placed among the steps instead.
                        yiOrderedAt = clock;
                        yiOrderedAfter = level.TutorialSteps[^1].Step;
                    }
                    if (turret.Alive)
                    {
                        if (turret.Health > lastTurret + 0.5 * turret.MaxHealth) turretHeals++;
                        if (!level.TutorialStepRan("EventOhNo")) TestAssert.Equal(turret.MaxHealth, turret.Health, "the guarded turret is untouched before EventOhNo");
                        else turretLowest = Math.Min(turretLowest, turret.Health);
                        lastTurret = turret.Health;
                    }
                },
            });
            Console.WriteLine(report.Summary());
            var steps = level!.TutorialSteps;
            var minionOutlines = outlines.Count(outline => outline.Target == ashe!.NetId && minionIds.Contains(outline.Unit));
            Console.WriteLine(FormattableString.Invariant(
                $"fight damage {fightDamage:0}, minion outlines on the player {minionOutlines}, Yi sent at {yiOrderedAt:0.0} after {yiOrderedAfter}, turret lowest {turretLowest:0} heals {turretHeals}"));
            TestAssert.True(report is { Outcome: "natural", Winner: "Order", PacketViolations: 0, LifecycleViolations: 0 }, report.Summary());
            TestAssert.True(fightDamage > 0.0, "the minion fight's Aggro.lua wizards wound the player");
            TestAssert.True(minionOutlines > 0, "a scripted minion that attacks the player sends her the 0xC8 outline");
            var turretDeath = steps.ToList().FindIndex(entry => entry.Step == "EventTurretDeath");
            var ordered = steps.ToList().FindLastIndex(entry => entry.Step == yiOrderedAfter);
            var lookOut = steps.ToList().FindIndex(entry => entry.Step == "EventLookOut");
            TestAssert.True(turretDeath >= 0 && ordered >= turretDeath && ordered < lookOut,
                $"Master Yi comes for the player from EventTurretDeath, before the camera finds him (sent after {yiOrderedAfter})");
            TestAssert.True(turretLowest < turret!.MaxHealth && turretHeals == 0, "the Chaos turret keeps the damage it takes");
            // Batch 671: what the player's client holds for that turret's health.
            var held = clientHealth.Where(entry => entry.Unit == turret.NetId).ToList();
            Console.WriteLine("turret mHP on the player's client: " + string.Join(", ", held.Take(12).Select(entry => entry.Health.ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
            TestAssert.True(held.Count > 0 && held.Where(entry => !entry.AfterOhNo).All(entry => Math.Abs(entry.Health - turret.MaxHealth) < 0.5),
                "until EventOhNo the player's client holds the guarded turret at full health (no empty bar)");
            TestAssert.True(held.Zip(held.Skip(1)).All(pair => pair.Second.Health <= pair.First.Health + 0.5 * turret.MaxHealth),
                "the player's client never sees the turret heal (no jump back to full on Nunu's hit)");
            TestAssert.True(held.Any(entry => entry.AfterOhNo && entry.Health < turret.MaxHealth - 1.0), "the client sees the damage after EventOhNo");

            // Batch 671, the owner's tutorial rules (NOT RETAIL, 3 November 2026).
            static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
            var mid = midway!.Value;
            var waveCentre = wave.Count == 0 ? new Position2(double.NaN, double.NaN)
                : new Position2(wave.Average(point => point.X), wave.Average(point => point.Z));
            Console.WriteLine(FormattableString.Invariant(
                $"midway ({mid.X:0},{mid.Z:0}); Yi freed at ({yiFreed?.X:0},{yiFreed?.Z:0}); wave of {wave.Count} centred ({waveCentre.X:0},{waveCentre.Z:0}); Yi died {yiDiedAt:0.0}, back {yiBackAt:0.0} at ({yiBack?.X:0},{yiBack?.Z:0}) ordered {yiBackOrdered}"));
            // Rule 1: the turret runs the tutorial character whose CharOnPreDamage no longer spares it from minions
            // (scripting.bb-unit-character-the-tutorial-turrets-fall-to-minions).
            TestAssert.Equal("ChaosTurretTutorial", turretCharacter, "the Chaos turret is bound to its shipped tutorial character");
            // Batch 675 (owner decision 3 November 2026, "protect it"): the Chaos waves bring the player's own last tower down to one health at
            // most, as in retail (scripting.bb-unit-character-the-tutorial-order-back-tower-holds-at-one-health).
            Console.WriteLine(FormattableString.Invariant($"Order back tower: alive {backTower!.Alive}, lowest {backLowest:0.0} of {backTower.MaxHealth:0}"));
            TestAssert.True(backTower.Alive && backLowest >= 1.0 - 1e-6, "the Order back tower is never felled");
            // Rule 3: Yi and EventTurretDeath's wave start midway between firstChaosTurret and the Chaos HQ, and Yi respawns there and fights on.
            TestAssert.True(yiFreed is { } freed && Distance(freed, mid) < 150.0, "Master Yi is freed midway between the Chaos turret and HQ");
            TestAssert.True(wave.Count == 6 && Distance(waveCentre, mid) < 400.0, "EventTurretDeath's six Chaos minions spawn around the midway point");
            TestAssert.True(yiBack is { } back && Distance(back, mid) < 150.0 && yiBackOrdered, "Master Yi respawns midway and comes for the player again");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
