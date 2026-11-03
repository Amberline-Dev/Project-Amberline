using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 434: the Autumn rift's Urf camp. Map2's NeutralMinionSpawn.luaobj adds camp 13: PredefinedCamps[11] = {'Urf'} (the main chunk
// pc 0600-0603), one spawn point and one face point Make3DPoint(2532.0, 44.4, 9940.0) (pc 0310-0316, 0534-0540), GroupDelaySpawnTime 0,
// GroupsRespawnTime 100 and CampLevel 1 (NeutralMinionInit pc 0284-0301). DefaultNeutralMinionValues['Urf'] pays 50 experience (pc 0087-0091).
// Summoner's Rift (LEVELS/Map1) keeps its twelve camps and has no camp 13.
public static class HarrowingUrfCampChecks
{
    private static readonly Position3 ShippedUrfPoint = new(2532.0, 44.4, 9940.0);

    [Check("neutral.harrowing-urf-camp-spawns-on-map-two")]
    public static void UrfCampSpawnsOnTheHarrowingRiftAtItsShippedTimeAndPlace()
    {
        TestAssert.False(NeutralDefinitions.Camps.ContainsKey(13), "Summoner's Rift (Map1) has no camp 13");
        var mapOneJungle = new JungleScheduler(new WorldState(), GameData.Default, navigation: NavigationGrid.LoadDefault());
        mapOneJungle.Start();
        mapOneJungle.Tick(1000.0);
        TestAssert.Equal(12, mapOneJungle.Camps.Count, "Summoner's Rift keeps its twelve camps");
        TestAssert.False(mapOneJungle.Members.Values.Any(member => member.Name == "Urf"), "no Urf on Summoner's Rift");

        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        var jungle = new JungleScheduler(new WorldState(), data, navigation: grid);
        jungle.Start();
        jungle.Tick(99.0);
        TestAssert.False(jungle.Members.Values.Any(member => member.Name == "Urf"), "the Urf camp is not up before its 100 s first spawn");
        jungle.Tick(100.0);
        var urf = jungle.Members.Values.Single(member => member.Name == "Urf");
        TestAssert.Equal(13, urf.CampId, "the Urf is camp 13 of the Harrowing rift");
        TestAssert.Equal(1, jungle.Camps[13].Definition.Roster.Count, "camp 13 has one member");

        // The shipped spawn point is kept when it stands on a legal cell (the script's own Make3DPoint). Where it does not, the engine
        // moves it to a nearby legal cell: Reconstruction (batch 219 rule, kept), so the check accepts that case as long as the cell is legal.
        // Batch 588: it stands on the terrain, so its Y is the grid's height there, not the script's 44.4.
        var shipped = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z);
        TestAssert.Near(grid.HeightAt(new Position2(urf.Home.X, urf.Home.Z)), urf.Home.Y, 1e-9, "the Urf stands on the terrain");
        if (grid.IsMovementWalkable(shipped))
            TestAssert.True(urf.Home == ShippedUrfPoint with { Y = urf.Home.Y }, $"Urf spawns at its shipped point, not {urf.Home}");
        else
            TestAssert.True(grid.IsMovementWalkable(new Position2(urf.Home.X, urf.Home.Z)), "a blocked Urf point moves to a legal cell");
        TestAssert.True(urf.Entity.Position == new Position2(urf.Home.X, urf.Home.Z), "the Urf stands at its home");

        // Shipped stats: Urf.inibin base health and base damage, the experience the script pays, and the attack spell's missile speed.
        var urfData = data.Champion("Urf");
        TestAssert.Near(urfData.BaseHp, urf.Stats.Hp, 1e-9, "Urf's shipped base health");
        TestAssert.Near(urfData.BaseDamage, urf.Stats.AttackDamage, 1e-9, "Urf's shipped base damage");
        TestAssert.Near(50.0, urf.Stats.ExperienceReward, 1e-9, "DefaultNeutralMinionValues Urf experience");
        // No UrfBasicAttack is shipped, so the Urf flies no basic-attack missile (Reconstruction: AttackMissileSpeed's rule for a missing spell).
        TestAssert.Near(0.0, urf.Stats.MissileSpeed, 1e-9, "Urf flies no basic-attack missile");
    }

    [Check("neutral.harrowing-urf-binds-its-shipped-character-script")]
    public static void UrfBindsItsShippedCharacterScriptOnTheHarrowingRift()
    {
        // The production binder (runtime.Spells.UnitScripts, as a match's jungle uses it) runs CharScriptUrf: its CharOnActivate
        // sets SetNoRender, so the ghost is not drawn until its HalloweenUrfAppear buff shows it.
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, data);
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var jungle = new JungleScheduler(runtime.World, data, navigation: grid);
        var urfBound = false;
        jungle.BindUnitCharacter = (unit, character) =>
        {
            var bound = runtime.Spells.UnitScripts!.BindUnit(unit, character);
            if (character == "Urf") urfBound = bound;
        };
        jungle.Start();
        jungle.Tick(100.0);
        var urf = jungle.Members.Values.Single(member => member.Name == "Urf");
        TestAssert.True(urfBound, "the production binder runs CharScriptUrf for the Urf");
        // Batch 440: the bind's SetNoRender is held by the unit's script status (released by HalloweenUrfAppear), so the check reads the render word.
        TestAssert.True((CharacterState.ActionWord(urf.Entity) & CharacterState.NoRenderBit) != 0,
            "CharOnActivate of CharScriptUrf hides the ghost (SetNoRender) before its appear buff");
    }

    [Check("neutral.harrowing-urf-reveals-when-a-champion-walks-within-300")]
    public static void UrfRevealsWhenAChampionWalksWithin300()
    {
        // Batch 440 reproduction: the Urf's UpdateSelfBuffActions (every 2 s) finds the champion within 300 and adds HalloweenUrfAppear, which
        // shows the ghost. The production runtime polls the bound unit (SpellSystem -> UnitScripts.Poll), so the reveal must reach the render word.
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, data);
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var jungle = new JungleScheduler(runtime.World, data, navigation: grid);
        jungle.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        jungle.Start();
        jungle.Tick(100.0);
        var urf = jungle.Members.Values.Single(member => member.Name == "Urf").Entity;
        var champion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order,
            urf.Position + new Position2(250, 0)) { Health = 1000, MaxHealth = 1000, Created = true, ChampionName = "Annie", Radius = 55 });
        // The spell system's own tick: its clock (BBExecutePeriodically reads CurrentTime) and the bound units' self-update poll.
        for (var t = 0.0; t < 3.0; t += 0.25)
        {
            runtime.World.Clock.Advance(0.25);
            runtime.Spells.Tick(0.25);
        }
        // DIAGNOSTICS (batch 440): the poll count of the Urf's character and the script failures, in the failure message.
        var failures = string.Join(" | ", runtime.Spells.UnitScripts!.ScriptFailures.Select(f => $"{f.Script}/{f.Hook}@{f.BlockPath}: {f.Message}"));
        var polls = string.Join(",", runtime.Spells.UnitScripts!.PollRuns.Select(pair => $"{pair.Key}={pair.Value}"));
        TestAssert.True(urf.Buffs.Any(buff => string.Equals(buff.Name, "HalloweenUrfAppear", StringComparison.OrdinalIgnoreCase)),
            $"a champion within 300 makes the Urf appear (HalloweenUrfAppear); polls [{polls}] failures [{failures}] buffs [{string.Join(",", urf.Buffs.Select(b => b.Name))}]");
        TestAssert.Equal(0u, CharacterState.ActionWord(urf) & CharacterState.NoRenderBit, "the revealed ghost is not NoRender");
        TestAssert.True(champion.Alive, "the walking champion is still alive");
    }
}
