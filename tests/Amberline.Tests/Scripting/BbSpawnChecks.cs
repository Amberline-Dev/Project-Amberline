using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbAnnieDifferentialChecks;

namespace Amberline.Tests;

/// <summary>
/// The spawn natives of the BuildingBlocks engine (BBSpawnMinion, BBCloneUnitPet), driven with the shipped call blocks on the
/// production <c>MatchRuntime</c> of the M3 harness: each route is run through the real script block (taken from the shipped file) and
/// compared with the hand-written service call it replaces. Soraka's match supplies the caster and the services; the units are the ones the
/// shipped Shaco, Yorick, Nidalee and Janna scripts create.
/// </summary>
public static class BbSpawnChecks
{
    // ------------------------------------------------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock Block(string function, params (string Key, BbValue Value)[] fields) =>
        new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), []);

    private static BbValue Text(string value) => new BbValue.Text(value);
    private static BbValue Num(double value) => new BbValue.Number(value);
    private static BbValue Flag(bool value) => new BbValue.Bool(value);
    private static BbValue Ref(string name) => new BbValue.Reference(name);

    /// <summary>The first block of a shipped hook that calls <paramref name="function"/> (and names <paramref name="skin"/> when given).</summary>
    private static BbBlock Shipped(string script, string hook, string function, string? skin = null) =>
        BbM3.Blocks(BbM3.Script(script), hook).First(block => block.Function == function && (skin is null || block.Text("Skin") == skin));

    /// <summary>Runs <paramref name="blocks"/> as the <c>SelfExecute</c> of a throw-away spell of <paramref name="caster"/>.</summary>
    private static void Run(BbM3.Match match, Entity caster, IReadOnlyList<BbBlock> blocks, Entity? target = null, int rank = 1,
        Position2? at = null)
    {
        var script = new BbScript("W22Spawn", "Spells/W22Spawn.luaobj", [], [new BbEventTable("SelfExecute", blocks)], []);
        var point = at ?? caster.Position;
        var position = new Position3(point.X, 0, point.Z);
        match.Module.Router!.RunSelfExecute(script, caster, 0, rank, 0.0, position, position, target);
    }

    private static void NoFailure(BbM3.Match match, string what) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            what + ": " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => $"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}")));

    /// <summary>The units a run adds to the world, in allocation order.</summary>
    private static List<Entity> NewUnits(BbM3.Match match, Action run)
    {
        var before = match.Runtime.World.Entities.Keys.ToHashSet();
        run();
        return match.Runtime.World.Entities.Values.Where(unit => !before.Contains(unit.NetId)).ToList();
    }

    /// <summary>Records which spawn packets the effect sink was asked for (the services keep their own sink; this one sees the natives').</summary>
    public class SinkRecorder : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is "SendMinionSpawn" or "SendInitialVisibility" or "SendStats" or "SendNidaleeTrap" && args?[0] is Entity unit)
                Calls.Add($"{targetMethod.Name}:{unit.NetId}");
            try { return targetMethod.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private static SinkRecorder Record(BbM3.Match match)
    {
        var field = typeof(SpellSystem).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var proxy = DispatchProxy.Create<ISpellEffectSink, SinkRecorder>();
        var recorder = (SinkRecorder)(object)proxy;
        recorder.Target = field.GetValue(match.Runtime.Spells)!;
        field.SetValue(match.Runtime.Spells, proxy);
        return recorder;
    }

    /// <summary>
    /// The call's flags: retail sets the unit's status bits from them (batch 241), which is the unit's script status here
    /// (<see cref="Entity.ScriptStatus"/>), not a buff. Before batch 241 they rode on a permanent internal buff.
    /// </summary>
    private static BuffState? FlagsBuff(Entity unit) => unit.ScriptStatus;

    // ------------------------------------------------------------------------------------------------------------
    // What the family binds and refuses
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-spawn-binds-what-it-claims-and-refuses-the-rest")]
    public static void BoundNativesAreHandledAndTheRefusalsAreNamed()
    {
        foreach (var function in new[] { "BBSpawnMinion", "BBCloneUnitPet" })
        {
            TestAssert.True(BbNativeHandlers.IsBound(function), $"{function} is bound");
            // A bare block reaches the handler (which then misses its Skin or UnitToClone), never the "not bound" fall-through.
            using var match = new BbM3.Match("Soraka");
            Run(match, match.Hero, [Block(function)]);
            TestAssert.Equal(1, match.Module.ScriptFailures.Count, $"{function}: the bare block fails once");
            var message = match.Module.ScriptFailures[0].Message;
            TestAssert.False(message.Contains("not bound", StringComparison.Ordinal), $"{function} is handled, not refused as unbound: {message}");
        }

        // The refusals: an AI script no service runs, a clone controller the engine has none for, and a service called with the wrong AI
        // script. Orianna's ball is no refusal since batch 112 (a placemarker the engine announces through the ball projection).
        // A unit with a VisibilitySize is a vision source (W6).
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Shipped("OrianaIzunaCommand", "SelfExecute", "BBSpawnMinion", "OriannaBall")) is null,
            "the Orianna ball is accepted");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Shipped("H28GEvolutionTurret", "SelfExecute", "BBSpawnMinion", "HeimerTBlue")) is null,
            "Heimerdinger's turret (Minion.lua) is accepted since W26c: the stationary Minion.lua controller runs it");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Block("BBSpawnMinion", ("Skin", Text("HeimerTBlue")), ("AiScript", Text("Pet.lua")))) is { } petAi &&
                        petAi.Contains("Pet.lua", StringComparison.Ordinal), "a lane-less unit with an AI script no service runs is refused by that script");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Shipped("XerathArcanopulse", "SelfExecute", "BBSpawnMinion")) is null,
            "a unit with a vision size is accepted: it is a vision source of that radius");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Shipped("LeblancMIApplicator", "OnBuffDeactivate", "BBCloneUnitPet")) is null,
            "LeBlanc's image is a clone family since W26c (LeblancMIFull is its controller)");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(Block("BBCloneUnitPet", ("Buff", Text("NoSuchClone")))) is { } controller &&
                        controller.Contains("NoSuchClone", StringComparison.Ordinal), "a clone of a family the engine has no controller buff for is refused");
        var box = Shipped("JackInTheBoxInternal", "OnBuffDeactivate", "BBSpawnMinion");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(box) is null, "the Jack in the Box call is accepted");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(new BbBlock(box.Function, box.Params.Select(field =>
                field.Key == "AiScript" ? new BbField(field.Key, Text("idle.lua")) : field).ToArray(), [])) is not null,
            "a box with another AI script is refused: its service expects turret.lua");

        // Run time: the ball spawns as the generic placemarker unit of its owner, announced by the ball projection and by no entity packet.
        using (var match = new BbM3.Match("Soraka"))
        {
            var balls = NewUnits(match, () => Run(match, match.Hero, [Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("TargetPos"))),
                Shipped("OrianaIzunaCommand", "SelfExecute", "BBSpawnMinion", "OriannaBall")]));
            NoFailure(match, "the ball call");
            var ball = balls.Single();
            TestAssert.True(ball.Name == "OriannaBall" && ball.Placemarker && !ball.Created, "an OriannaBall placemarker, never sent as an entity");
            TestAssert.True(ball.Team == match.Hero.Team && ball.OwnerNetId == match.Hero.NetId, "on the caster's team, owned by him");
        }
    }

    [Check("scripting.bb-spawn-the-ten-closures-spawn-only-what-the-family-runs")]
    public static void TheTenHandWrittenChampionsSpawnsAreAccountedFor()
    {
        var resolver = new BbScriptResolver(GameData.Default.Paths);
        var accepted = new SortedSet<string>(StringComparer.Ordinal);
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        var clones = new SortedSet<string>(StringComparer.Ordinal);
        var refusedClones = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var champion in new[] { "Annie", "Janna", "Ezreal", "Vayne", "Blitzcrank", "LeeSin", "Nidalee", "Shaco", "Yorick", "Orianna" })
        {
            var closure = resolver.Resolve(champion);
            foreach (var item in closure.Scripts.SelectMany(script => script.Script.Walk()))
            {
                if (item.Block.Function == "BBSpawnMinion")
                    (BbNativeHandlers.SpawnRefusal(item.Block) is null ? accepted : refused).Add(item.Block.Text("Skin")!);
                else if (item.Block.Function == "BBCloneUnitPet")
                    (BbNativeHandlers.SpawnRefusal(item.Block) is null ? clones : refusedClones).Add(item.Block.Text("Buff")!);
            }
        }
        // Annie's BBSpawnPet is BbPetSpawn's. Everything else the ten closures spawn runs (the Orianna ball since batch 112; Heimerdinger's
        // turret skins, which sit in Yorick's closure only because the revived ally's borrowed scripts reach them, since W26c: the Minion.lua
        // controller runs them); LeblancMIApplicator is borrowed the same way.
        TestAssert.Equal("HeimerTBlue,HeimerTGreen,HeimerTRed,HeimerTYellow,Nidalee_Spear,OriannaBall,ShacoBox,TestCube,TestCubeRender,YorickDecayedGhoul,YorickRavenousGhoul,YorickSpectralGhoul",
            string.Join(',', accepted), "the skins the family runs for the ten closures");
        TestAssert.Equal("", string.Join(',', refused),
            "the skins it refuses for the ten closures");
        TestAssert.Equal("Hallucinate,LeblancMI,YorickRARevive", string.Join(',', clones), "the clones it runs");
        TestAssert.Equal("", string.Join(',', refusedClones), "the clone it refuses (none)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shaco: the box and the clone
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-spawn-shaco-box-goes-through-the-box-service")]
    public static void JackInTheBoxIsTheServicesUnitWithoutItsHandWrittenAi()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var record = GameData.Default.Champion("ShacoBox");
        var spawn = Shipped("JackInTheBoxInternal", "OnBuffDeactivate", "BBSpawnMinion");

        var units = NewUnits(match, () => Run(match, hero, [
            Block("BBGetTeamID", ("TargetVar", Text("Owner")), ("DestVar", Text("TeamID"))),
            Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("TargetPos"))),
            spawn,
            // DestVar = Other3: the unit the script goes on to buff.
            Block("BBIncMaxHealth", ("TargetVar", Text("Other3")), ("Delta", Num(7)), ("IncCurrentHealth", Flag(true)))]));
        NoFailure(match, "the Jack in the Box spawn");
        var scripted = units.Single();
        TestAssert.True(scripted.Kind == EntityKind.ShacoBox && scripted.UnitType == UnitType.Monster, "a Shaco box");
        TestAssert.True(scripted.Team == hero.Team && scripted.OwnerNetId == hero.NetId, "on the caster's team, owned by him");
        TestAssert.Equal("ShacoBox", scripted.Name);
        TestAssert.Near(record.BaseHp + 7, scripted.MaxHealth, 1e-9, "the record's hit points, and DestVar names the unit (+7)");
        TestAssert.True(scripted.Created, "announced to the clients");
        var flags = FlagsBuff(scripted);
        TestAssert.True(flags is { Payload: { Silenced: true, Invulnerable: false, MagicImmune: false, MovementDisabled: false, Ghosted: false } },
            "the call's flags: silenced only");

        TestAssert.False(scripted.Buffs.Any(buff => buff.Name == "JackInTheBox"), "the unit has no AI buff until the script adds it");

        // Nothing but the script's JackInTheBox buff drives a box: the service ticks leave this one alone.
        match.Foe.Position = new Position2(scripted.Position.X + 100, scripted.Position.Z);
        match.Advance(2.5);
        TestAssert.False(scripted.Buffs.Any(buff => buff.Name is "JackInTheBox" or "Stealth" or "EndKill"), "the service tick leaves the scripted box alone");
        NoFailure(match, "after the ticks");
    }

    [Check("scripting.bb-spawn-shaco-clone-goes-through-the-clone-service")]
    public static void HallucinateCloneIsTheSharedCloneServicesUnit()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var services = match.Runtime.Spells.ScriptPets!;
        var spawn = Shipped("HallucinateApplicator", "OnBuffDeactivate", "BBCloneUnitPet");
        var clones = NewUnits(match, () => Run(match, hero, [
            Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("Pos1"))), spawn,
            Block("BBIncMaxHealth", ("TargetVar", Text("Other1")), ("Delta", Num(7)), ("IncCurrentHealth", Flag(false)))]));
        NoFailure(match, "the Hallucinate clone");
        var clone = clones.Single();
        TestAssert.True(clone.Kind == EntityKind.ShacoClone && clone.PetState is { Kind: CloneKind.ShacoClone }, "a Shaco-family clone");
        TestAssert.True(clone.OwnerNetId == hero.NetId && clone.PetState!.OwnerNetId == hero.NetId && clone.PetState.CloneSourceNetId == hero.NetId,
            "owned by, and a copy of, the caster");
        TestAssert.Equal("Hallucinate", clone.PetState!.SpawnBuffName);
        TestAssert.Near(18.0, clone.PetState!.SpawnDuration, 1e-9, "Duration 18");
        TestAssert.True(clone.PetState!.ShowMinimapIcon, "ShowMinimapIcon = true");
        TestAssert.Near(hero.Health, clone.Health, 1e-9, "it starts at the source's health");
        TestAssert.Near(hero.MaxHealth + 7, clone.MaxHealth, 1e-9, "its maximum is the source's (+7 proves DestVar)");
        TestAssert.True(clone.Created, "announced to the clients");
        TestAssert.True(FlagsBuff(clone) is null, "no flags buff: the call has none");

        var written = services.Clones.Spawn(hero, hero, hero.Position + new Position2(100, 0), CloneKind.ShacoClone, hero.Health, "Hallucinate", 18.0,
            hero.ChampionName, showMinimapIcon: true);
        foreach (var (name, expected, actual) in new (string, double, double)[]
                 {
                     ("attack damage", clone.AttackDamage, written.AttackDamage), ("armor", clone.Armor, written.Armor),
                     ("speed", clone.Speed, written.Speed), ("attack range", clone.AttackRange, written.AttackRange),
                     ("level", clone.Level, written.Level), ("max mana", clone.MaxMana, written.MaxMana),
                 })
            TestAssert.Near(expected, actual, 1e-9, $"the hand-written clone has the same {name}");
        // Without its HallucinateFull controller the clone service leaves the unit alone; it must not fault on it.
        match.Advance(1.0);
        TestAssert.True(clone.Alive, "the clone survives the service ticks");
        NoFailure(match, "after the ticks");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Yorick: the ghouls and the revenant
    // ------------------------------------------------------------------------------------------------------------

    // Batch 164 dropped the hand-written YorickGhoulService: the YorickSummonDecayed BBSpawnMinion block (Name 'Inky', Skin
    // 'YorickDecayedGhoul', AiScript 'YorickPHPet.lua', Silenced true, GoldRedirectTargetVar 'Owner', DestVar 'Other1') is the only thing
    // that asks for a ghoul and it goes down the generic BBSpawnMinion path. The check keeps its id; the body below is that path.
    [Check("scripting.bb-spawn-yorick-ghoul-goes-through-the-ghoul-service")]
    public static void SummonDecayedIsTheGenericSpawnWithoutTheBakedLevelScaling()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        hero.Level = 7;
        var record = GameData.Default.Champion("YorickDecayedGhoul");
        var script = BbM3.Script("YorickSummonDecayed");
        var blocks = BbM3.Blocks(script, "SelfExecute").ToList();
        var spawn = blocks.First(block => block.Function == "BBSpawnMinion");
        var units = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetCastSpellTargetPos"), blocks.First(block => block.Function == "BBGetTeamID"), spawn,
            Block("BBIncMaxHealth", ("TargetVar", Text("Other1")), ("Delta", Num(7)), ("IncCurrentHealth", Flag(false)))],
            at: hero.Position + new Position2(150, 0)));
        NoFailure(match, "the Decayed ghoul");
        var ghoul = units.Single();
        TestAssert.True(ghoul.Kind == EntityKind.YorickGhoul && ghoul.PetState is { Kind: CloneKind.YorickGhoul }, "a Yorick ghoul with its pet state");
        TestAssert.True(ghoul.OwnerNetId == hero.NetId && ghoul.Team == hero.Team, "his, on his team");
        TestAssert.Equal("YorickDecayedGhoul", ghoul.Name);
        TestAssert.Near(record.BaseHp + 7, ghoul.MaxHealth, 1e-9, "the record's hit points with no level scaling (+7 proves DestVar)");
        TestAssert.Near(record.Armor, ghoul.Armor, 1e-9, "the record's armor: the Logic buff adds the level scaling");
        TestAssert.True(ghoul.Created && FlagsBuff(ghoul) is { Payload.Silenced: true }, "announced, with the call's Silenced flag");
        TestAssert.False(ghoul.Buffs.Any(buff => buff.Name.EndsWith("Logic", StringComparison.Ordinal)), "no hand-written Logic buff");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "YorickSummonDecayed"), "no hand-written owner marker");

        // The pet AI is YorickPHPet.luaobj on the pet host and needs no buff on the ghoul: it must not fault on the scripted unit.
        match.Advance(1.0);
        NoFailure(match, "after the ticks");

        // Owner decision (batch 71): his ghouls die with Yorick. The cascade keys on the pet state the spawn set.
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.False(hero.Alive, "the owner died through the normal kill path");
        TestAssert.False(ghoul.Alive, "the scripted ghoul died with him");
        NoFailure(match, "after the owner's death");
    }

    [Check("scripting.bb-spawn-yorick-revenant-goes-through-the-clone-service")]
    public static void OmenOfDeathCloneIsTheRevenantFamilyUnit()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var ally = match.Ally;
        var blocks = BbM3.Blocks(BbM3.Script("YorickRARevive"), "TargetExecute").ToList();
        var clones = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetUnitPosition"), blocks.First(block => block.Function == "BBCloneUnitPet")], target: ally));
        NoFailure(match, "the revenant");
        var revenant = clones.Single();
        TestAssert.True(revenant.Kind == EntityKind.YorickRevenant && revenant.PetState is { Kind: CloneKind.YorickRevenant }, "a revenant");
        TestAssert.True(revenant.OwnerNetId == hero.NetId && revenant.PetState!.CloneSourceNetId == ally.NetId, "Yorick's, a copy of the ally");
        TestAssert.Equal("YorickRARevive", revenant.PetState!.SpawnBuffName);
        TestAssert.Near(0.0, revenant.PetState!.SpawnDuration, 1e-9, "Duration 0: the controller buff ends it");
        TestAssert.False(revenant.PetState!.ShowMinimapIcon, "ShowMinimapIcon = false");
        TestAssert.Near(ally.Health, revenant.Health, 1e-9, "it starts at the ally's health; the script heals it right after");
        TestAssert.Near(ally.MaxHealth, revenant.MaxHealth, 1e-9, "with the ally's maximum");
        TestAssert.Near(ally.Position.X, revenant.Position.X, 1e-9, "on the ally (the call's Pos)");
        NoFailure(match, "after the spawn");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Nidalee: the trap
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-spawn-nidalee-trap-is-a-protected-one-hit-point-unit")]
    public static void BushwhackSpawnIsTheNidaleeTrapUnit()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var recorder = Record(match);
        var blocks = BbM3.Blocks(BbM3.Script("Bushwhack"), "SelfExecute").ToList();
        var aim = hero.Position + new Position2(200, 0);
        var units = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetCastSpellTargetPos"), blocks.First(block => block.Function == "BBGetTeamID"),
            blocks.First(block => block.Function == "BBSpawnMinion")], at: aim));
        NoFailure(match, "the Noxious Trap");
        var trap = units.Single();
        TestAssert.True(trap.Kind == EntityKind.NidaleeTrap && trap.UnitType == UnitType.Monster, "a Nidalee trap");
        TestAssert.True(trap.OwnerNetId == hero.NetId && trap.Team == hero.Team, "hers, on her team");
        // Not Protected: the script path keeps the trap out of reach with statuses (the call's flags, CharScriptNidalee_Spear, the Bushwhack buff), and the buff's
        // end is the trap's own InternalRaw hit, which CombatService.ApplyHit refuses on a Protected unit.
        TestAssert.False(trap.Protected, "not Protected: its own 4000-damage hit has to land");
        TestAssert.Equal("Noxious Trap", trap.Name);
        TestAssert.Near(aim.X, trap.Position.X, 1e-9, "at the aim point");
        TestAssert.Near(1.0, trap.MaxHealth, 1e-9, "one hit point, as the trap template");
        TestAssert.True(recorder.Calls.Contains($"SendNidaleeTrap:{trap.NetId}"), "the trap's replication template was queued: " + string.Join(",", recorder.Calls));
        var flags = FlagsBuff(trap);
        TestAssert.True(flags is { Payload: { Invulnerable: true, MagicImmune: true, MovementDisabled: true, Ghosted: true } },
            "the call's flags (rooted, invulnerable, magic immune, ignore collision) are the unit's status bits");
        TestAssert.True(flags!.Payload.Stealthed,
            "NOT RETAIL, owner request of 19 October 2026: Nidalee's trap (Skin Nidalee_Spear, named Noxious Trap) is stealthed (BbNativeHandlers.TrapUnitName); the shipped call adds no stealth");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The generic unit: placemarkers, wards
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-spawn-placemarker-is-registered-but-never-sent-or-targeted")]
    public static void HowlingGaleTestCubeIsAnUnseenUnTargetableHolder()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var recorder = Record(match);
        var record = GameData.Default.Champion("TestCubeRender");
        var spawn = Shipped("HowlingGale", "OnBuffDeactivate", "BBSpawnMinion");
        var units = NewUnits(match, () => Run(match, hero, [
            Block("BBGetTeamID", ("TargetVar", Text("Owner")), ("DestVar", Text("TeamID"))),
            Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("CastPos")), ("PositionVarTable", Text("InstanceVars"))),
            spawn,
            Block("BBIncMaxHealth", ("TargetVar", Text("Other1")), ("Delta", Num(7)), ("IncCurrentHealth", Flag(false)))]));
        NoFailure(match, "the Howling Gale cube");
        var cube = units.Single();
        TestAssert.True(cube.Kind == EntityKind.Monster && cube.UnitType == UnitType.Monster && cube.Name == "TestCubeRender", "a TestCubeRender unit");
        TestAssert.True(cube.Team == hero.Team, "TEAM_CASTER with the team variable: the caster's team");
        TestAssert.Near(record.BaseHp + 7, cube.MaxHealth, 1e-9, "the record's hit points, and DestVar names the unit");
        TestAssert.Near(hero.Position.X, cube.Position.X, 1e-9, "at the position variable (InstanceVars.CastPos)");
        // Batch 202: announced like a minion (Entity.Announced); the spawn packet and the stats are queued below.
        TestAssert.True(cube.Announced, "a placemarker is announced like a minion");
        TestAssert.True(cube.Protected, "and kept out of target selection");
        TestAssert.True(recorder.Calls.Contains($"SendMinionSpawn:{cube.NetId}") && recorder.Calls.Contains($"SendStats:{cube.NetId}"),
            "the spawn packet and the stats were queued: " + string.Join(",", recorder.Calls));
        var flags = FlagsBuff(cube);
        TestAssert.True(flags is { Payload: { MovementDisabled: true, Invulnerable: true, Ghosted: true, MagicImmune: false, Silenced: false } },
            "Rooted, Invulnerable and IgnoreCollision are the unit's status bits");
        TestAssert.False(ChampionSpellSupport.CombatTargets(match.Runtime.Spells, match.Foe).Contains(cube), "an enemy's spells do not see the cube");
    }

    [Check("scripting.bb-spawn-flags-are-status-bits-a-rest-write-clears")]
    public static void SpawnFlagsAreTheUnitsStatusBits()
    {
        // Retail: BBSpawnMinion sets the new unit's status bits from its flags; there is no carrier buff. A bit is one bit, so a later
        // BBSetStatus of the rest value on the unit (OrianaGhostMinion's OnBuffDeactivate writes SetInvulnerable false on the ball) clears it.
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var spawn = Shipped("HowlingGale", "OnBuffDeactivate", "BBSpawnMinion");
        var cube = NewUnits(match, () => Run(match, hero, [
            Block("BBGetTeamID", ("TargetVar", Text("Owner")), ("DestVar", Text("TeamID"))),
            Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("CastPos")), ("PositionVarTable", Text("InstanceVars"))),
            spawn])).Single();
        NoFailure(match, "the Howling Gale cube");
        TestAssert.False(cube.Buffs.Any(buff => buff.Name == "BBSpawnFlags"), "no carrier buff: the flags take no buff slot");
        TestAssert.True(cube.ScriptStatus is { Payload: { Invulnerable: true, MovementDisabled: true } }, "Invulnerable and Rooted are its bits");
        match.Module.Router!.RunSelfExecute(new BbScript("W241Clear", "Spells/W241Clear.luaobj", [],
            [new BbEventTable("SelfExecute", [Block("BBSetStatus", ("TargetVar", Text("Target")), ("SrcValue", Flag(false)),
                ("Status", Ref("SetInvulnerable")))])], []), hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), cube);
        NoFailure(match, "the rest write");
        TestAssert.True(cube.ScriptStatus is { Payload: { Invulnerable: false, MovementDisabled: true } },
            "a script's SetInvulnerable false clears the spawn's bit and leaves Rooted");
    }

    [Check("scripting.bb-spawn-ward-is-announced-with-the-ward-flag")]
    public static void SightWardIsAGenericWardUnit()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var recorder = Record(match);
        var blocks = BbM3.Blocks(BbM3.Script("SightWard"), "SelfExecute").ToList();
        var aim = hero.Position + new Position2(0, 300);
        var units = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetTeamID"), blocks.First(block => block.Function == "BBGetCastSpellTargetPos"),
            blocks.First(block => block.Function == "BBSpawnMinion")], at: aim));
        NoFailure(match, "the Sight Ward");
        var ward = units.Single();
        TestAssert.True(ward.Kind == EntityKind.Ward && ward.IsWard, "a ward");
        TestAssert.True(ward.Created && !ward.Protected, "announced, and targetable");
        TestAssert.True(ward.WardOwnerNetId == hero.NetId && ward.OwnerNetId == hero.NetId, "credited to the hero GoldRedirectTarget names");
        TestAssert.Near(aim.Z, ward.Position.Z, 1e-9, "where the script put it");
        TestAssert.True(recorder.Calls.Contains($"SendMinionSpawn:{ward.NetId}") && recorder.Calls.Contains($"SendInitialVisibility:{ward.NetId}"),
            "spawn and reveal were sent: " + string.Join(",", recorder.Calls));
        TestAssert.True(FlagsBuff(ward) is { Payload: { ActionDisabled: true, MovementDisabled: true, Ghosted: false } }, "Stunned and Rooted");
    }

    [Check("scripting.bb-spawn-team-variable-wins-over-the-team-constant")]
    public static void TeamVarBeatsTeamAndTeamCasterIsTheCastersTeam()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        BbBlock Cube(params (string, BbValue)[] extra) => Block("BBSpawnMinion", [("Name", Text("T")), ("Skin", Text("TestCube")),
            ("AiScript", Text("idle.lua")), ("PosVar", Text("P")), ("Placemarker", Flag(true)), ("VisibilitySize", Num(0)), ("DestVar", Text("U")),
            .. extra]);
        var prelude = Block("BBGetUnitPosition", ("UnitVar", Text("Owner")), ("PositionVar", Text("P")));
        // TeamVar holds a team number (BBGetTeamID): it wins over a concrete Team.
        var chaos = NewUnits(match, () => Run(match, hero, [prelude, Block("BBSetVarInTable", ("DestVar", Text("X")), ("SrcValue", Num(200))),
            Cube(("Team", Ref("TEAM_ORDER")), ("TeamVar", Text("X")))])).Single();
        TestAssert.True(chaos.Team == Team.Chaos, "TeamVar 200 wins over TEAM_ORDER");
        var caster = NewUnits(match, () => Run(match, hero, [prelude, Cube(("Team", Ref("TEAM_CASTER")))])).Single();
        TestAssert.True(caster.Team == hero.Team, "TEAM_CASTER is the casting unit's team");
        var neutral = NewUnits(match, () => Run(match, hero, [prelude, Cube(("Team", Ref("TEAM_NEUTRAL")))])).Single();
        TestAssert.True(neutral.Team == Team.Neutral, "a concrete team is that team");
        var unset = NewUnits(match, () => Run(match, hero, [prelude, Cube()])).Single();
        TestAssert.True(unset.Team == hero.Team, "no team falls back to the Owner's");
        NoFailure(match, "the team rules");
    }
}
