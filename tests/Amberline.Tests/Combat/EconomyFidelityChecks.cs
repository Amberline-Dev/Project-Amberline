using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>Overnight fidelity fixes of 16 October 2026 (audits A1, A3, A5): economy, Molten Shield, Prowl.</summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("buffs.team-size-catch-up-matches-scripts")]
    public static void TeamSizeCatchUpConstantsMatchTheShippedScripts()
    {
        var paths = GameDataPaths.Default;
        var controller = BbScriptLoader.Load(paths, "Spells/ChampionChampionDelta.luaobj");
        var positive = BbScriptLoader.Load(paths, "Spells/PositiveChampionDelta.luaobj");
        TestAssert.True(controller.IsClean && positive.IsClean, "both catch-up scripts load cleanly");
        var controllerBlocks = controller.Script!.Walk().Select(entry => entry.Block).ToArray();

        var periodic = controllerBlocks.Single(block => block.Function == "BBExecutePeriodically");
        TestAssert.Equal(TeamSizeCatchUp.CheckSeconds, periodic.Number("TimeBetweenExecutions"));
        TestAssert.Equal(false, ((BbValue.Bool)periodic.Param("ExecuteImmediately")!).Value,
            "the first comparison is one period after activation");
        foreach (var count in controllerBlocks.Where(block => block.Function == "BBGetNumberOfHeroesOnTeam"))
        {
            TestAssert.Equal(false, ((BbValue.Bool)count.Param("ConnectedFromStart")!).Value);
            TestAssert.Equal(true, ((BbValue.Bool)count.Param("IncludeBots")!).Value);
        }
        TestAssert.Equal(4, controllerBlocks.Count(block => block.Function == "BBGetNumberOfHeroesOnTeam"));
        var add = controllerBlocks.Single(block => block.Function == "BBSpellBuffAdd");
        TestAssert.Equal(TeamSizeCatchUp.DeltaBuff, add.Text("BuffName"));
        TestAssert.Equal(TeamSizeCatchUp.DeltaSeconds, add.Number("Duration"));
        TestAssert.Equal(1.0, add.Number("MaxStack"));
        TestAssert.Equal("BUFF_RENEW_EXISTING", ((BbValue.Reference)add.Param("BuffAddType")!).Name);

        var math = positive.Script!.Walk().Select(entry => entry.Block)
            .Where(block => block.Function == "BBMath").ToArray();
        TestAssert.Equal(TeamSizeCatchUp.RampSeconds,
            math.Single(block => ((BbValue.Reference)block.Param("MathOp")!).Name == "MO_MIN").Number("Src2Value"));
        TestAssert.Equal(TeamSizeCatchUp.RespawnPerRamp,
            math.Single(block => block.Text("DestVar") == "PercentBonus" &&
                ((BbValue.Reference)block.Param("MathOp")!).Name == "MO_MULTIPLY").Number("Src1Value"));
        TestAssert.Equal(TeamSizeCatchUp.ExperiencePerRamp,
            math.Single(block => block.Text("DestVar") == "expPercentBonus").Number("Src1Value"));
        var stats = positive.Script.Walk().Select(entry => entry.Block)
            .Where(block => block.Function == "BBIncStat")
            .Select(block => ((BbValue.Reference)block.Param("Stat")!).Name).ToArray();
        TestAssert.Equal(new[] { "IncPercentRespawnTimeMod", "IncPercentEXPBonus" }, stats);

        // Every champion character script gives the permanent controller buff
        // (minions, monsters and turrets have character scripts without it).
        var granting = 0;
        foreach (var file in Directory.EnumerateFiles(paths.Data("Characters"), "CharScript*.luaobj",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(paths.Root, "DATA"), file).Replace(Path.DirectorySeparatorChar, '/');
            var script = BbScriptLoader.Load(paths, relative).Script!;
            var granted = script.Walk().Select(entry => entry.Block).SingleOrDefault(block =>
                block.Function == "BBSpellBuffAdd" && block.Text("BuffName") == TeamSizeCatchUp.ControllerBuff);
            if (granted is null) continue;
            granting++;
            TestAssert.Equal(TeamSizeCatchUp.ControllerSeconds, granted.Number("Duration"), relative);
        }
        TestAssert.True(granting >= 84, "all 84 champion character scripts grant the controller: " + granting);

        // NegativeChampionDelta is preloaded but no shipped script adds it.
        var adders = new[] { "Spells", "Characters", "Items", "Talents" }
            .SelectMany(directory => Directory.EnumerateFiles(paths.Data(directory), "*.luaobj",
                SearchOption.AllDirectories))
            .Where(file => System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file))
                .Contains("NegativeChampionDelta", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray();
        TestAssert.True(adders.Length == 0, "no script adds NegativeChampionDelta: " + string.Join(",", adders));
    }

    [Check("combat.team-size-catch-up-ramp-and-equalise")]
    public static void TeamSizeCatchUpRampsXpAndRespawnAndClearsWhenTeamsEqualise()
    {
        // Equal teams: no delta, neutral XP and respawn after any number of checks.
        using (var even = new Match("Annie", Team.Order))
        {
            var hero = even.Hero(0);
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == TeamSizeCatchUp.ControllerBuff),
                "CharOnActivate gives every hero the controller buff");
            TestAssert.True(hero.Buffs.Single(buff => buff.Name == TeamSizeCatchUp.ControllerBuff)
                .Payload.NonDispellable, "controller is NonDispellable");
            Step(even.Runtime, 120.0);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.DeltaBuff),
                "1v1 never adds PositiveChampionDelta");
            TestAssert.Near(100.0, ExperienceFor(even.Runtime, hero), 1e-9, "1v1 XP is x1.0");
            TestAssert.Near(1.0, RespawnFactor(even.Runtime, hero), 1e-9, "1v1 respawn is x1.0");
        }

        using var match = new Match("Annie", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var small = match.Hero(0);
        var big = match.Hero(1);
        var extra = match.Hero(2);
        Step(runtime, 9.5);
        TestAssert.False(small.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.DeltaBuff),
            "the first comparison waits one full 10 s period");
        match.Clear();
        Step(runtime, 1.0);
        var delta = small.Buffs.Single(buff => buff.Name == TeamSizeCatchUp.DeltaBuff);
        // Batch 159 part 3: the activation time is Spells/PositiveChampionDelta.luaobj's own InstanceVars.StartTime (its
        // OnBuffActivate writes BBGetGameTime there, 0002-0010); the hand-written Payload.Born field is not the script's.
        var start = (double)match.ScriptBuff(small, TeamSizeCatchUp.DeltaBuff).InstanceVars["StartTime"]!;
        TestAssert.Near(10.0, start, 0.51, "activation at the first 10 s comparison");
        TestAssert.False(big.Buffs.Concat(extra.Buffs).Any(buff => buff.Name == TeamSizeCatchUp.DeltaBuff),
            "the larger team gets no delta");
        TestAssert.True(match.Captures[0].Packets.Any(packet => packet.Plaintext.Span[0] == 0xBF &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                SpellPackets.BuffNameHash(TeamSizeCatchUp.DeltaBuff)),
            "the known client buff is replicated as a real buff");
        TestAssert.Near(100.0, ExperienceFor(runtime, small), 1.0, "ramp starts near zero");

        // Ramp at 45 s: T = 0.5.
        Step(runtime, start + 45.0 - runtime.Combat.CurrentTime);
        TestAssert.Equal(start, (double)match.ScriptBuff(small, TeamSizeCatchUp.DeltaBuff).InstanceVars["StartTime"]!,
            "BUFF_RENEW_EXISTING keeps the activation time");
        TestAssert.Near(102.5, ExperienceFor(runtime, small), 1e-9, "XP bonus 0.05 * 0.5");
        TestAssert.Near(0.95, RespawnFactor(runtime, small), 1e-9, "respawn modifier 0.1 * 0.5");
        TestAssert.Near(100.0, ExperienceFor(runtime, big), 1e-9, "the larger team is untouched");

        // Ramp at 90 s and beyond: capped at T = 1, still renewed past the 21 s duration.
        Step(runtime, start + 90.0 - runtime.Combat.CurrentTime);
        TestAssert.Near(105.0, ExperienceFor(runtime, small), 1e-9, "XP x1.05 after 90 s");
        TestAssert.Near(0.90, RespawnFactor(runtime, small), 1e-9, "respawn x0.90 after 90 s");
        Step(runtime, 60.0);
        TestAssert.Near(105.0, ExperienceFor(runtime, small), 1e-9, "the ramp stays capped");

        // Equalise: a training dummy is not a team member, so 1v1 again clears the buff.
        extra.IsTrainingDummy = true;
        Step(runtime, 10.5);
        TestAssert.False(small.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.DeltaBuff),
            "equal teams clear PositiveChampionDelta");
        TestAssert.Near(100.0, ExperienceFor(runtime, small), 1e-9, "XP back to x1.0");
        TestAssert.Near(1.0, RespawnFactor(runtime, small), 1e-9, "respawn back to x1.0");

        // Outnumbered again: the buff re-activates and the ramp restarts from zero.
        extra.IsTrainingDummy = false;
        Step(runtime, 10.5);
        TestAssert.True((double)match.ScriptBuff(small, TeamSizeCatchUp.DeltaBuff).InstanceVars["StartTime"]! >
            start + 100.0, "a new activation time");
        TestAssert.Near(100.0, ExperienceFor(runtime, small), 1.0, "the ramp restarted near zero");

        // A hero who joins the larger team stops being outnumbered.
        small.Team = Team.Chaos;
        Step(runtime, 10.5);
        TestAssert.False(small.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.DeltaBuff),
            "a hero on the larger team clears the buff");
    }

    // Batch 159 part 3: the 10 s comparison and the ramp are Spells/ChampionChampionDelta.luaobj's and
    // Spells/PositiveChampionDelta.luaobj's own BBExecutePeriodically blocks on their live script instances, which read the
    // spell clock (BBGetGameTime) rather than a hand-written elapsed counter. The step drives the production simulation so the
    // spell tick, the buff ticker and the combat clock advance together, the way a match runs them.
    private static void Step(MatchRuntime runtime, double seconds)
    {
        while (seconds > 1e-9)
        {
            var dt = Math.Min(1.0 / 30.0, seconds);
            runtime.Tick(dt);
            seconds -= dt;
        }
        runtime.Delivery.Flush();
    }

    // XP gained for 100 base XP, measured on a copy of the hero's counters.
    private static double ExperienceFor(MatchRuntime runtime, Entity hero)
    {
        var before = hero.Experience;
        TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: 100.0));
        var gained = hero.Experience - before;
        hero.Experience = before;
        return gained;
    }

    // Death timer divided by the unmodified one.
    private static double RespawnFactor(MatchRuntime runtime, Entity hero)
    {
        var now = runtime.Combat.CurrentTime;
        hero.Alive = false;
        hero.Health = 0.0;
        runtime.Combat.ScheduleRespawn(hero);
        var factor = (hero.RespawnAt - now) / ProgressionRules.DeathTime(hero.Level, now);
        hero.Alive = true;
        hero.Health = hero.MaxHealth;
        hero.RespawnAt = 0.0;
        return factor;
    }

    [Check("spells-nidalee-prowl-is-additive-percent-speed")]
    public static void ProwlUsesTheAdditivePercentMovementStat()
    {
        using var match = new Match("Nidalee", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var grid = runtime.Navigation;
        var brush = Enumerable.Range(0, grid.CountX * grid.CountY)
            .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX)))
            .First(point => grid.IsMovementWalkable(point) && grid.BrushIdAt(point) is not null);
        runtime.HeroStats.Refresh(hero);
        var baseSpeed = hero.Speed;
        TestAssert.True(baseSpeed * 1.2 < 415.0, "the case stays below the first soft cap");
        hero.Position = brush;
        // Prowl is the script buff CharScriptNidalee.UpdateSelfBuffActions adds while she stands in brush: the script module's tick polls it.
        runtime.Tick(.1);
        var prowl = hero.Buffs.Single(buff => buff.Name == "Prowl");
        TestAssert.Equal(0.15, prowl.Payload.MoveSpeedPercent, "Prowl.luaobj IncPercentMovementSpeedMod 0.15");
        TestAssert.Equal(0.0, prowl.Payload.MoveSpeedMult, "not the multiplicative stat");
        TestAssert.Near(baseSpeed * 1.15, hero.Speed, 1e-9, "Prowl alone");

        runtime.SpellBuffs.Add(hero, "ProwlAdditiveProbe", BuffType.Aura, 10.0, hero.NetId,
            new BuffPayload { MoveSpeedPercent = 0.05 });
        TestAssert.Near(baseSpeed * (1.0 + 0.15 + 0.05), hero.Speed, 1e-9,
            "additive percent stats sum (multiplicative stacking would give " + baseSpeed * 1.15 * 1.05 + ")");
    }

    [Check("combat.team-size-catch-up-runs-for-a-champion-with-no-character-script")]
    public static void TeamSizeCatchUpRunsForAChampionWithNoCharacterScript()
    {
        // Batch 159 part 3: the controller is Spells/ChampionChampionDelta.luaobj on a live script instance, and a hero whose
        // champion has no BbChampionModule runs no character script at all, so ProductionMatchComposition.HeroCreated adds the
        // same row through BbUnitCharacterModule.AddShippedBuff. This check is that path end to end: a hand-written champion
        // (Annie is scripted; LeeSin is one of the ten with no module of his own at the moment) is outnumbered, the frozen
        // script's own periodic fires, and both ramp rows reach HeroStats through the buff payload.
        //
        // The instance is also what proves the tick reaches it: BuffLifecycle.TickBuffs routes every buff through
        // IChampionBuffHooks.OnBuffTick, whose production view fans out to the match's script sinks — the unit-character
        // module's runtime included — so a stand-in row's BuffOnUpdateStats runs from the same ticker a script-bound hero's does.
        using var match = new Match("LeeSin", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var controller = hero.Buffs.Single(buff => buff.Name == TeamSizeCatchUp.ControllerBuff);
        TestAssert.True(controller.Payload.NonDispellable, "the controller carries the script's NonDispellable global");
        TestAssert.Equal(TeamSizeCatchUp.ControllerSeconds, controller.ExpiresAt,
            "and the script's own 25000 s duration");
        // It is the script's instance, not a hand-built row: the module's runtime must know it.
        var instance = match.ScriptBuff(hero, TeamSizeCatchUp.ControllerBuff);
        TestAssert.Equal("ChampionChampionDelta", instance.Script.Name, "the shipped script is what runs");
        TestAssert.True(instance.Script.Event("BuffOnUpdateStats") is not null,
            "whose stats hook holds the periodic comparison");
        Step(runtime, 10.5);
        var delta = match.ScriptBuff(hero, TeamSizeCatchUp.DeltaBuff);
        TestAssert.Equal("PositiveChampionDelta", delta.Script.Name, "the ramp is its own shipped script");
        var start = (double)delta.InstanceVars["StartTime"]!;
        TestAssert.Near(10.0, start, 0.51, "activated by the controller's first 10 s comparison");
        Step(runtime, start + 90.0 - runtime.Combat.CurrentTime);
        TestAssert.Near(105.0, ExperienceFor(runtime, hero), 1e-9, "XP x1.05 at T = 1, through IncPercentEXPBonus");
        TestAssert.Near(0.90, RespawnFactor(runtime, hero), 1e-9,
            "respawn x0.90, through IncPercentRespawnTimeMod reaching HeroStats.TimeDeadPercent");
    }
}
