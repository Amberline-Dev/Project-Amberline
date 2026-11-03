using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 453: Dominion's respawn points (0xDE) and its relics and speed shrines (map 8). The units spawn through the encounter path
/// (EncounterService) and the respawn leaves run inside the shipped trees. Batch 469: the relics first spawn at the wiki's times and come
/// back after a pickup (Reconstruction, the wiki's Health Relic and Greater Relic pages; the shipped files hold no time).
/// </summary>
public static class BehaviorTreeRelicChecks
{
    private const double Step = 1.0 / 30.0;

    // Batch 469: the health relic locations (RelicA to C, LesserRelic1 to 7), the ten the wiki counts.
    private static readonly string[] HealthLocations = ["RelicA", "RelicB", "RelicC", .. Enumerable.Range(1, 7).Select(index => "LesserRelic" + index)];

    [Check("dominion.respawn-points-send-0xde-create")]
    public static void RespawnPointsSendTheirCreateRecord()
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var host = MapEightHost(world, packets, null);
        Tick(host, 600);

        // The shipped Initialization tree creates GraveyardA to E (RespawnPointUIID 0 to 4) at OdinScriptBehaviorTrees.xml:2217-2259.
        var creates = packets.Where(packet => packet.Plaintext.Span[0] == 0xDE && packet.Plaintext.Span[5] == 0).ToList();
        TestAssert.Equal(5, creates.Count, "five point graveyards send a 0xDE create record");
        TestAssert.True(creates.Select(packet => packet.Plaintext.Span[6]).OrderBy(index => index).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }),
            "the creates name the point indexes 0 to 4");
    }

    [Check("dominion.respawn-logic-allows-the-owner-team")]
    public static void RespawnLogicAllowsTheOwnersTeamAtItsGraveyard()
    {
        // Reconstruction: the state byte is a team mask (1 Order, 2 Chaos). RespawnLogic (OdinScriptBehaviorTrees.xml:5052) names
        // the owner's team at GraveyardID and disallows the other; no neutral owner allows nobody.
        TestAssert.Equal((byte)1, RespawnStateFor(Team.Order), "an Order-owned point allows Order only");
        TestAssert.Equal((byte)2, RespawnStateFor(Team.Chaos), "a Chaos-owned point allows Chaos only");
        TestAssert.Equal((byte)0, RespawnStateFor(Team.Neutral), "a neutral point allows neither team");
    }

    [Check("dominion.speed-shrines-and-relics-stand-at-their-places")]
    public static void SpeedShrinesAndRelicsStandAtTheirPlaces()
    {
        var world = new WorldState();
        var spawned = new List<Entity>();
        var host = MapEightHost(world, null, spawned);
        var clock = RunHostTo(host, 0.0, Step * 5);

        // Speed shrines (batch 506, the owner's triangle; batch 511, the bottom one on its pad): SpeedShrine01 and 02 stand at their shipped
        // places and the bottom shrine at SigilB; 03 and 05 are not placed (03 outside the triangle, 05 in the south wall). SpeedShrine04 has
        // no scene object.
        var shrines = spawned.Where(unit => unit.Name == "OdinSpeedShrine").ToList();
        TestAssert.Equal(3, shrines.Count, "three speed shrines spawn at the start: 01, 02 and the bottom pad");
        foreach (var name in new[] { "SpeedShrine01", "SpeedShrine02", "SigilB" })
            TestAssert.True(StandsAt(host, shrines, name), $"a speed shrine stands at {name}");

        // Batch 469 (Reconstruction, the wiki's Health Relic and Greater Relic pages): health relics first spawn at 2:00 (120 s) and
        // the greater relics at HUD 3:01 (game 181.96 s, batch 476e). Info_LesserRelic8 has a scene object but Locations.dat names no location for it, so no relic
        // stands there.
        clock = RunHostTo(host, clock, 119.0);
        TestAssert.Equal(0, spawned.Count(unit => unit.Name == "OdinShieldRelic"), "no health relic stands before 2:00");
        clock = RunHostTo(host, clock, 121.0);
        var health = spawned.Where(unit => unit.Name == "OdinShieldRelic").ToList();
        TestAssert.Equal(10, health.Count, "ten health relics spawn at 2:00 on the named relic locations");
        foreach (var name in HealthLocations)
            TestAssert.True(StandsAt(host, health, name), $"a health relic stands at {name}");

        clock = RunHostTo(host, clock, 179.0);
        TestAssert.Equal(0, spawned.Count(unit => unit.Name == "OdinCenterRelic"), "no greater relic stands before 3:01");
        RunHostTo(host, clock, 182.5);
        // Centre relics: one per team side, LargeRelic1 (left) and LargeRelic2 (right).
        var centre = spawned.Where(unit => unit.Name == "OdinCenterRelic").ToList();
        TestAssert.Equal(2, centre.Count, "two greater relics spawn at 3:01");
        TestAssert.True(StandsAt(host, centre, "LargeRelic1") && StandsAt(host, centre, "LargeRelic2"),
            "the greater relics stand at LargeRelic1 and LargeRelic2");

        // Every relic and shrine stands on a walkable cell of map 8's grid (the blocked-cell invariant of a map 8 match).
        var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        foreach (var unit in spawned)
            TestAssert.True(grid.IsWalkable(grid.WorldToCell(unit.Position)), $"{unit.Name} at {unit.Position} stands on a walkable cell");
    }

    /// <summary>
    /// Batch 469: a champion picks up a health relic on a map 8 match with the shipped unit scripts (CharScriptOdinShieldRelic and its
    /// OdinShieldRelicAura: the pickup kills the relic through the aura's deactivate). The relic stays gone for its respawn time after the
    /// pickup and then stands again at its place: 33 seconds (the wiki's Health Relic (Crystal Scar) page, "respawn 33 seconds after being
    /// picked up"). A greater relic is taken by a kill here, 180 seconds (the wiki's Greater Relic page, "a re-spawn timer of 3
    /// minutes"); its real pickup, the use channel, is DominionRelicPickupChecks' (batch 470). The first spawns follow the same pages (2:00 and 3:00). The map's grid is
    /// map 8's; the host is the map 8 quest host, ticked with the match at the combat clock (the production order).
    /// </summary>
    [Check("dominion.relic-pickup-respawns-the-relic-after-its-time")]
    public static void RelicPickupRespawnsTheRelicAfterItsTime()
    {
        using var match = new BbM3.Match("Ryze", navigation: NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)));
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        var failures = new List<BbScriptFailure>();
        // The match's own seam (as production's composition): it sets the spell system's unit scripts, which hear a unit buff's removal.
        var (unitScripts, unitSink) = MatchScriptSeams.InstallUnitCharacters(match.Runtime.Spells, match.Runtime.Spells.Data, failures.Add);
        ((BbHookFanout)hooks.ScriptHooks!).Add(unitSink);
        var world = match.Runtime.World;
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world, null, null,
                (skin, at, team) => unitScripts.SpawnEncounterUnit(skin, at, team))
            ?? throw new InvalidOperationException("map 8 has a quest host");

        void RunMatchTo(double seconds)
        {
            while (match.Runtime.Combat.CurrentTime + Step / 2 < seconds)
            {
                match.Runtime.Tick(Step);
                host.Tick(match.Runtime.Combat.CurrentTime, Step);
            }
        }

        Entity? Standing(string definition, string location)
        {
            var at = host.Context.NamedLocation(location)
                ?? throw new InvalidOperationException($"the map names no location {location}");
            return world.Entities.Values.FirstOrDefault(unit => unit.Name == definition && unit.Alive
                && unit.Position.X == at.X && unit.Position.Z == at.Z);
        }

        // The champion walks onto the relic: its aura finds the hero within 175 and the relic dies. The hero then steps off, so the
        // respawned relic is not picked up at once.
        Entity Pickup(string definition, string location)
        {
            var relic = Standing(definition, location) ?? throw new InvalidOperationException($"no {definition} stands at {location}");
            match.Hero.Position = relic.Position;
            RunMatchTo(match.Runtime.Combat.CurrentTime + 1.0);
            match.Hero.Position = new Position2(7000, 7000);
            return relic;
        }

        RunMatchTo(119.0);
        TestAssert.True(Standing("OdinShieldRelic", "RelicA") is null, "no health relic stands before 2:00");
        RunMatchTo(121.0);
        foreach (var name in HealthLocations)
            TestAssert.True(Standing("OdinShieldRelic", name) is not null, $"a health relic stands at {name} from 2:00");

        var relic = Pickup("OdinShieldRelic", "RelicA");
        TestAssert.True(!relic.Alive, "the pickup kills the health relic (OdinShieldRelicAura's deactivate)");
        // The pickup's own score buff (OdinShieldRelicAura's BuffOnUpdateActions, 3 seconds) shows the champion took it.
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "OdinScoreSmallRelic"), "the champion takes the health relic (OdinScoreSmallRelic)");
        var death = relic.DeathTime ?? throw new InvalidOperationException("the relic died with no death time");
        TestAssert.True(Standing("OdinShieldRelic", "RelicA") is null, "the picked-up health relic is gone");
        RunMatchTo(death + 32.5);
        TestAssert.True(Standing("OdinShieldRelic", "RelicA") is null,
            "the health relic is still gone 32.5 seconds after the pickup (wiki: respawn 33 seconds after pickup)");
        RunMatchTo(death + 33.5);
        TestAssert.True(Standing("OdinShieldRelic", "RelicA") is not null, "the health relic stands at RelicA again 33.5 seconds after the pickup");

        RunMatchTo(179.0);
        TestAssert.True(Standing("OdinCenterRelic", "LargeRelic1") is null && Standing("OdinCenterRelic", "LargeRelic2") is null,
            "no greater relic stands before 3:01");
        RunMatchTo(182.5);
        TestAssert.True(Standing("OdinCenterRelic", "LargeRelic1") is not null && Standing("OdinCenterRelic", "LargeRelic2") is not null,
            "both greater relics stand from 3:01");

        // The greater relic is taken by a use (batch 470: OdinCenterRelic is Useable, HeroUseSpell OdinCaptureChannelBomb, whose
        // OdinBombSuppression gives the Storm Shield and deals the relic 500 true damage; dominion.greater-relic-use-gives-the-storm-shield-
        // and-respawns). This harness has no order dispatcher, so the relic dies through the combat path here; the respawn is the same.
        var centre = Standing("OdinCenterRelic", "LargeRelic1") ?? throw new InvalidOperationException("no greater relic stands at LargeRelic1");
        match.Runtime.Combat.Kill(centre);
        TestAssert.True(!centre.Alive, "a greater relic that is picked up dies");
        var centreDeath = centre.DeathTime ?? throw new InvalidOperationException("the relic died with no death time");
        RunMatchTo(centreDeath + 179.5);
        TestAssert.True(Standing("OdinCenterRelic", "LargeRelic1") is null,
            "the greater relic is still gone 179.5 seconds after the pickup (wiki: respawn timer 3 minutes)");
        RunMatchTo(centreDeath + 180.5);
        TestAssert.True(Standing("OdinCenterRelic", "LargeRelic1") is not null,
            "the greater relic stands at LargeRelic1 again 180.5 seconds after the pickup");
        TestAssert.True(failures.Count == 0, "the relic scripts raised no failure: " + string.Join("; ", failures));
    }

    // Runs RespawnLogic for one point owner (a champion of each team stands on the map so the iteration runs) and returns the state
    // byte of the last 0xDE state record sent for point A's graveyard.
    private static byte RespawnStateFor(Team owner)
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var host = MapEightHost(world, packets, null);
        world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0))
            { Name = "Ryze", ChampionName = "Ryze" });
        Tick(host, 5);
        packets.Clear();

        var logic = new BehaviorTreeInstance(host.Library, "RespawnLogic", host.Context);
        logic.SetVariable("PointOwner", owner);
        logic.SetVariable("RespawnUIPoint", 0.0);
        logic.SetVariable("GraveyardID", 0.0);
        logic.Tick(1.0, 1.0 / 30.0);

        var states = packets.Where(packet => packet.Plaintext.Span[0] == 0xDE && packet.Plaintext.Span[5] == 1).ToList();
        return states.Count == 0 ? (byte)0xFF : states[^1].Plaintext.Span[6];
    }

    private static BehaviorTreeQuestHost MapEightHost(WorldState world, List<PlaintextPacket>? packets, List<Entity>? spawned)
    {
        var hooks = packets is null ? null : new CapturePointHooks(Send: packets.Add);
        return BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            null, null,
            (skin, at, team) =>
            {
                var unit = new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
                {
                    Name = skin,
                    ChampionName = skin,
                };
                world.Register(unit);
                spawned?.Add(unit);
                return unit;
            },
            hooks)
            ?? throw new InvalidOperationException("map 8 has a quest host");
    }

    private static bool StandsAt(BehaviorTreeQuestHost host, IEnumerable<Entity> units, string location)
    {
        var at = host.Context.NamedLocation(location)
            ?? throw new InvalidOperationException($"the map names no location {location}");
        return units.Any(unit => unit.Position.X == at.X && unit.Position.Z == at.Z);
    }

    private static void Tick(BehaviorTreeQuestHost host, int steps)
    {
        for (var tick = 1; tick <= steps; tick++)
            host.Tick(tick * Step, Step);
    }

    // Batch 469: ticks the host one simulation step at a time from clock to seconds and returns the clock reached.
    private static double RunHostTo(BehaviorTreeQuestHost host, double clock, double seconds)
    {
        while (clock + Step / 2 < seconds)
        {
            clock += Step;
            host.Tick(clock, Step);
        }
        return clock;
    }
}
