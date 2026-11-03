using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, Phase 3 (W22): the missile family. <c>BBDestroyMissile</c>, <c>BBDestroyMissileForTarget</c> and
/// <c>BBGetMissilePosFromID</c> and the missile hooks
/// <c>BuffOnLaunchMissile</c>, <c>SpellOnMissileUpdate</c>, <c>SpellOnMissileEnd</c> and <c>BuffOnMissileEnd</c> run in a production
/// <c>MatchRuntime</c>. The missiles are real ones of the engine's own classes (a homing <see cref="BbSpellProjectile"/> through a Ryze Overload cast,
/// a <see cref="BbLineMissile"/> built the way <c>CastChild</c> builds it); the scripts that probe them are hand-built block lists (the shipped
/// scripts that use the hooks belong to champions the engine does not run yet), and every name they read is one the shipped scripts read, which
/// <c>scripting.bb-missile-natives-and-hooks-match-the-shipped-scripts</c> pins against the whole corpus.
/// </summary>
public static class BbMissileChecks
{
    // ------------------------------------------------------------------------------------------------------------
    // Evidence: the shipped call sites and hook users
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-missile-natives-and-hooks-match-the-shipped-scripts")]
    public static void NativesAndHooksMatchTheShippedScripts()
    {
        foreach (var native in new[] { "BBDestroyMissile", "BBDestroyMissileForTarget", "BBGetMissilePosFromID" })
            TestAssert.True(BbNativeHandlers.IsBound(native), native + " is bound");

        var resolver = new BbScriptResolver(GameData.Default.Paths);
        var destroy = new List<BbBlock>();
        var forTarget = new List<BbBlock>();
        var position = new List<BbBlock>();
        var hookUsers = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["BuffOnLaunchMissile"] = new(StringComparer.Ordinal), ["SpellOnMissileEnd"] = new(StringComparer.Ordinal),
            ["SpellOnMissileUpdate"] = new(StringComparer.Ordinal), ["BuffOnMissileEnd"] = new(StringComparer.Ordinal),
        };
        foreach (var relative in BbRuntimeChecks.ScriptFiles())
        {
            var script = resolver.Load(relative).Script;
            if (script is null) continue;
            foreach (var (_, block) in script.Walk())
            {
                if (block.Function == "BBDestroyMissile") destroy.Add(block);
                else if (block.Function == "BBDestroyMissileForTarget") forTarget.Add(block);
                else if (block.Function == "BBGetMissilePosFromID") position.Add(block);
            }
            foreach (var table in script.Events)
                if (hookUsers.TryGetValue(table.Hook, out var users)) users.Add(Path.GetFileNameWithoutExtension(relative));
        }

        // Call counts of the coverage audit: 74, 8 and 3 calls.
        TestAssert.Equal(74, destroy.Count, "BBDestroyMissile calls");
        TestAssert.Equal(8, forTarget.Count, "BBDestroyMissileForTarget calls");
        TestAssert.Equal(3, position.Count, "BBGetMissilePosFromID calls");
        // The parameters the client's bodies read are the only ones the call sites write: MissileID (literal, Var, VarTable),
        // the unit Target, TargetID and the Result output.
        TestAssert.True(destroy.All(block => block.Params.All(field => field.Key is "MissileIDVar" or "MissileIDVarTable")),
            "BBDestroyMissile parameters are MissileIDVar and MissileIDVarTable");
        TestAssert.True(forTarget.All(block => block.Params.All(field => field.Key == "TargetVar")), "BBDestroyMissileForTarget has TargetVar only");
        TestAssert.True(position.All(block => block.Params.All(field => field.Key is "TargetIDVar" or "TargetIDVarTable" or "TargetID" or "ResultVar" or "ResultVarTable")),
            "BBGetMissilePosFromID parameters");
        // 62 calls name the id TargetExecute hands a missile spell; 12 read an id a script stored itself.
        TestAssert.Equal(62, destroy.Count(block => block.Text("MissileIDVar") == "MissileNetworkID" && block.Param("MissileIDVarTable") is null),
            "calls that pass MissileNetworkID");
        TestAssert.Equal(12, destroy.Count(block => block.Text("MissileIDVar") != "MissileNetworkID"), "calls that pass a stored id");

        TestAssert.Equal(12, hookUsers["BuffOnLaunchMissile"].Count, "BuffOnLaunchMissile scripts");
        TestAssert.Equal(11, hookUsers["SpellOnMissileEnd"].Count, "SpellOnMissileEnd scripts");
        TestAssert.Equal(5, hookUsers["SpellOnMissileUpdate"].Count, "SpellOnMissileUpdate scripts");
        TestAssert.Equal(6, hookUsers["BuffOnMissileEnd"].Count, "BuffOnMissileEnd scripts");

        // the spell data loader reads LuaOnMissileUpdateDistanceInterval; the five update users carry it.
        TestAssert.Equal(new[] { "GalioRighteousGustMissile", "KogMawVoidOozeMissile", "NocturneDuskbringer", "OrianaDissonance", "RumbleCarpetBombMissile" },
            hookUsers["SpellOnMissileUpdate"].ToArray(), "SpellOnMissileUpdate users");
        var expected = new Dictionary<string, double>
        {
            ["GalioRighteousGustMissile"] = 175, ["KogMawVoidOozeMissile"] = 125, ["NocturneDuskbringer"] = 200,
            ["OrianaDissonance"] = 100, ["RumbleCarpetBombMissile"] = 180,
        };
        foreach (var (spell, interval) in expected)
            TestAssert.Near(interval, BbMissileFlight.UpdateInterval(GameData.Default.Spell(spell, 5)), 1e-9, spell + " LuaOnMissileUpdateDistanceInterval");
        TestAssert.Near(0.0, BbMissileFlight.UpdateInterval(GameData.Default.Spell("EzrealMysticShotMissile", 5)), 1e-9,
            "a spell without the key gets no update run");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Line missile
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-missile-line-destroy-in-the-hit-ends-the-flight-once")]
    public static void ALineMissileDestroyedInItsHitEndsOnce()
    {
        using var rig = new Rig();
        rig.Match.Foe.Position = new Position2(7300, 7000);
        // Both enemies lie inside one step of the line (about 66 units): the second must not be hit after the script destroyed the missile.
        rig.Match.FarFoe.Position = new Position2(7310, 7000);
        rig.Vars["DestroyOnHit"] = true;
        rig.AddProbeBuff();
        var id = rig.Line("EzrealMysticShotMissile", new Position2(8100, 7000));
        rig.Match.Advance(0.6);

        TestAssert.Equal(1.0, rig.Number("Hits"), "TargetExecute ran for one unit: the destroyed missile takes no second hit");
        TestAssert.Equal((double)id, rig.Number("SeenId"), "MissileNetworkID is the missile's net id, exact");
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1,
            "two BBDestroyMissile calls tell the client once: " + string.Join(",", rig.Match.Log.DestroyedMissiles));
        // BuffOnLaunchMissile: missileId, the holder as Owner, no Target for a line, and the missile is already known to BBGetMissilePosFromID.
        TestAssert.Equal((double)id, rig.Number("LaunchId"), "BuffOnLaunchMissile reads the missile id as missileId");
        TestAssert.True(ReferenceEquals(rig.Vars["LaunchOwner"], rig.Match.Hero), "the holder of the buff is Owner");
        TestAssert.True(rig.Vars["LaunchTarget"] is null, "a line missile has no Target");
        TestAssert.Near(7000.0, rig.Point("LaunchPos").X, 1.0, "BBGetMissilePosFromID in the launch hook gives the launch point");
        // SpellOnMissileEnd and BuffOnMissileEnd: once, at the missile's position when it was destroyed.
        TestAssert.Equal(1.0, rig.Number("Ends"), "SpellOnMissileEnd ran once");
        TestAssert.Equal("EzrealMysticShotMissile", rig.Vars["EndSpellName"] as string, "SpellName");
        TestAssert.True(ReferenceEquals(rig.Vars["EndOwner"], rig.Match.Hero), "Owner is the casting hero");
        TestAssert.Equal(1.0, rig.Number("EndLevel"), "Level is the cast's rank");
        TestAssert.Equal(1.0, rig.Number("EndCastLevel"), "GetCastSpellLevelPlusOne reads the rank");
        TestAssert.True(rig.Vars["EndTarget"] is null, "a line ends on no unit");
        var end = rig.Point("EndPos");
        TestAssert.True(end.X > 7100.0 && end.X < 7450.0 && Math.Abs(end.Z - 7000.0) < 1e-6, $"MissileEndPosition is where the line was destroyed: {end}");
        TestAssert.Near(8100.0, rig.Point("EndCastPos").X, 1e-3, "GetCastSpellTargetPos reads the cast's target point");
        TestAssert.Equal(1.0, rig.Number("BuffEnds"), "BuffOnMissileEnd ran once for the holder's buff");
        TestAssert.Equal("EzrealMysticShotMissile", rig.Vars["BuffEndSpell"] as string, "BuffOnMissileEnd reads SpellName");
        TestAssert.Equal(0.0, rig.Number("Updates"), "a spell without LuaOnMissileUpdateDistanceInterval gets no update run");
        // The missile is gone for the next call.
        TestAssert.Equal(0, rig.Router.Missiles.Count, "the registry holds no missile");
        rig.Run(null, new Dictionary<string, object?> { ["id"] = (double)id }, Destroy("id"));
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1, "a destroy of a gone missile is a no-op");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-line-runs-its-end-hooks-once-at-the-end-of-its-range")]
    public static void ALineMissileThatIsNotDestroyedEndsAtItsRange()
    {
        using var rig = new Rig();
        rig.Match.Foe.Position = new Position2(7300, 7000);
        rig.Match.FarFoe.Position = new Position2(7600, 7000);
        rig.Vars["DestroyOnHit"] = false;
        rig.AddProbeBuff();
        var id = rig.Line("EzrealMysticShotMissile", new Position2(8100, 7000));
        rig.Match.Advance(0.3);
        // In flight: the registry knows the missile and BBGetMissilePosFromID reads where it is (a launch tick flies 2000 / 30 units).
        TestAssert.True(rig.Router.Missiles.Find(id) is not null, "the launched missile is registered");
        rig.Vars["Probed"] = null;
        rig.Run(null, new Dictionary<string, object?> { ["id"] = (double)id },
            Block("BBGetMissilePosFromID", "TargetIDVar", "id", "TargetID", 0, "ResultVar", "Probed", "ResultVarTable", "CharVars"));
        var flying = rig.Point("Probed");
        TestAssert.True(flying.X > 7000.0 + 2000.0 * 0.3 - 150.0 && flying.X < 7000.0 + 2000.0 * 0.3 + 150.0, $"the reported position follows the flight: {flying}");

        rig.Match.Advance(1.0);
        TestAssert.Equal(2.0, rig.Number("Hits"), "the line passes through both units: one TargetExecute each");
        TestAssert.Equal(1.0, rig.Number("Ends"), "SpellOnMissileEnd ran once");
        // Batch 433 (Talon R follow-up): a line flies the client's level table at the wire level (rank minus one; entry 0 is the base CastRange,
        // entry N is CastRangeN), not CastRangeAt(rank). Rank 1 of the Mystic Shot missile therefore flies its base 1100 and rank 2
        // the CastRange1 1200 (Reconstruction). Not to the aim point (LineMissileEndsAtTargetPoint is unset), so it ends at 7000 + 1100 whatever the aim.
        var range = GameData.Default.Spell("EzrealMysticShotMissile", 5).LineRangeAt(1);
        TestAssert.Equal(1100.0, range, "the rank 1 line range of the shipped spell (the base entry)");
        TestAssert.Near(7000.0 + range, rig.Point("EndPos").X, 1e-6, "MissileEndPosition is the end of the rank's line range");
        TestAssert.Equal(1.0, rig.Number("BuffEnds"), "BuffOnMissileEnd ran once");
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1, "the client is told once, by the natural end");
        TestAssert.Equal(0, rig.Router.Missiles.Count, "the registry is empty");
        // A gone missile leaves the result variable alone.
        var before = rig.Vars["Probed"];
        rig.Run(null, new Dictionary<string, object?> { ["id"] = (double)id },
            Block("BBGetMissilePosFromID", "TargetIDVar", "id", "TargetID", 0, "ResultVar", "Probed", "ResultVarTable", "CharVars"));
        TestAssert.True(ReferenceEquals(before, rig.Vars["Probed"]), "BBGetMissilePosFromID writes nothing for a missile that is gone");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-line-update-hook-runs-every-update-distance")]
    public static void TheUpdateHookRunsOncePerUpdateDistance()
    {
        using var rig = new Rig();
        // Kog'Maw's Void Ooze: a 1175 unit line, an update every 125 units (LuaOnMissileUpdateDistanceInterval): nine runs, the last at 1125.
        rig.Match.Foe.Position = new Position2(7000, 9500);
        rig.Match.FarFoe.Position = new Position2(7000, 9800);
        rig.Vars["DestroyOnHit"] = false;
        var id = rig.Line("KogMawVoidOozeMissile", new Position2(8300, 7000));
        rig.Match.Advance(1.5);
        TestAssert.Equal(9.0, rig.Number("Updates"), "SpellOnMissileUpdate runs once per 125 units of a 1175 unit flight");
        var last = rig.Point("UpdPos");
        TestAssert.Near(7000.0 + 125.0 * 9.0, last.X, 1e-6, "MissilePosition is the point where the distance was reached");
        TestAssert.Near(7000.0, last.Z, 1e-6, "on the line");
        TestAssert.True(ReferenceEquals(rig.Vars["UpdOwner"], rig.Match.Hero), "Owner is the casting hero");
        TestAssert.Equal(1.0, rig.Number("Ends"), "and the end hook ran once");
        TestAssert.Near(7000.0 + 1175.0, rig.Point("EndPos").X, 1e-6, "MissileEndPosition is the end of the range");
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Contains(id), "the missile left the client");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-ids-are-read-as-exact-integers")]
    public static void MissileIdsAreNotRoundedThroughFloat32()
    {
        using var rig = new Rig();
        rig.Match.Foe.Position = new Position2(7000, 9500);
        rig.Match.FarFoe.Position = new Position2(7000, 9800);
        rig.Vars["DestroyOnHit"] = false;
        var first = rig.Line("EzrealMysticShotMissile", new Position2(8100, 7000));
        var second = rig.Line("EzrealMysticShotMissile", new Position2(8100, 7000));
        TestAssert.Equal(first + 1, second, "the allocator hands out consecutive missile ids");
        // Net ids start from 0x40020000, where float32 values are 128 apart; the client's number getter is float32, its missile id getter is an integer.
        TestAssert.True(first > (1u << 24), "the ids are above 2^24, where float32 cannot tell neighbours apart");
        rig.Match.Advance(0.1);
        rig.Run(null, new Dictionary<string, object?> { ["id"] = (double)second }, Destroy("id"));
        TestAssert.True(rig.Match.Log.DestroyedMissiles.SequenceEqual([second]), "only the second missile was destroyed: " + string.Join(",", rig.Match.Log.DestroyedMissiles));
        TestAssert.True(rig.Router.Missiles.Find(first) is not null, "the first missile still flies");
        // The id may also be the literal MissileID plus the variable, like every number of the client's getter.
        rig.Run(null, new Dictionary<string, object?> { ["offset"] = 1.0 },
            Block("BBDestroyMissile", "MissileID", (double)(first - 1), "MissileIDVar", "offset"));
        TestAssert.True(rig.Match.Log.DestroyedMissiles.SequenceEqual([second, first]), "literal plus variable name the first missile");
    }

    [Check("scripting.bb-missile-natives-without-a-missile-do-nothing")]
    public static void NativesIgnoreIdsNoMissileHas()
    {
        using var rig = new Rig();
        var before = rig.Match.Log.DestroyedMissiles.Count;
        rig.Vars["Probed"] = null;
        rig.Run(null, new Dictionary<string, object?> { ["unset"] = null, ["number"] = 0.0, ["big"] = 4000000000.0, ["text"] = "x" },
            Destroy("unset"), Destroy("number"), Destroy("big"), Destroy("text"), Destroy("missing"),
            Block("BBDestroyMissile", "MissileID", 77),
            Block("BBGetMissilePosFromID", "TargetIDVar", "number", "TargetID", 0, "ResultVar", "Probed", "ResultVarTable", "CharVars"),
            Block("BBGetMissilePosFromID", "TargetID", 77, "ResultVar", "Probed", "ResultVarTable", "CharVars"));
        TestAssert.Equal(before, rig.Match.Log.DestroyedMissiles.Count, "nothing was destroyed");
        TestAssert.True(rig.Vars["Probed"] is null, "no position was written");
        rig.NoMissileHookFailed();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Homing missile (a Ryze Overload cast; the Overload script is the hand-built probe)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-missile-homing-flight-publishes-its-id-and-runs-the-hooks")]
    public static void AHomingMissileRunsLaunchHitAndEndHooks()
    {
        using var rig = new Rig();
        rig.Vars["DestroyOnHit"] = false;
        rig.AddProbeBuff();
        TestAssert.True(rig.Match.Cast(0, rig.Match.Foe), "Overload is accepted");
        var cast = rig.Match.Log.Casts.Last(info => info.CasterNetId == rig.Match.Hero.NetId && info.Slot == 0);
        var id = cast.MissileNetId;
        TestAssert.True(id != 0, "the cast carries the missile");
        rig.Match.Advance(1.0);

        TestAssert.Equal(1.0, rig.Number("Hits"), "TargetExecute ran once, on arrival");
        TestAssert.Equal((double)id, rig.Number("SeenId"), "MissileNetworkID is the cast's missile id");
        TestAssert.True(ReferenceEquals(rig.Vars["HitTarget"], rig.Match.Foe), "the hit unit is Target");
        // BuffOnLaunchMissile: the target of a homing missile is Target.
        TestAssert.Equal((double)id, rig.Number("LaunchId"), "missileId");
        TestAssert.True(ReferenceEquals(rig.Vars["LaunchTarget"], rig.Match.Foe), "Target is the unit the missile flies at");
        TestAssert.Near(7000.0, rig.Point("LaunchPos").X, 1.0, "the missile is registered when the launch hook runs");
        // The end hooks run after the hit, once, at the target.
        TestAssert.Equal(1.0, rig.Number("Ends"), "SpellOnMissileEnd ran once");
        TestAssert.Equal(rig.QName, rig.Vars["EndSpellName"] as string, "SpellName is the cast spell");
        TestAssert.True(ReferenceEquals(rig.Vars["EndTarget"], rig.Match.Foe), "Target is the unit the missile reached");
        TestAssert.Near(7300.0, rig.Point("EndPos").X, 1e-3, "MissileEndPosition is the target's position");
        TestAssert.Equal(1.0, rig.Number("EndCastLevel"), "GetCastSpellLevelPlusOne");
        TestAssert.Equal(1.0, rig.Number("BuffEnds"), "BuffOnMissileEnd ran once");
        TestAssert.Equal(rig.QName, rig.Vars["BuffEndSpell"] as string, "BuffOnMissileEnd reads SpellName");
        TestAssert.Equal(0, rig.Router.Missiles.Count, "the registry is empty");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-homing-destroy-in-the-hit-ends-the-flight-once")]
    public static void ADestroyInTheHitOfAHomingMissileIsOneDestroy()
    {
        using var rig = new Rig();
        rig.Vars["DestroyOnHit"] = true;
        TestAssert.True(rig.Match.Cast(0, rig.Match.Foe), "Overload is accepted");
        var id = rig.Match.Log.Casts.Last(info => info.CasterNetId == rig.Match.Hero.NetId && info.Slot == 0).MissileNetId;
        rig.Match.Advance(1.0);
        TestAssert.Equal(1.0, rig.Number("Hits"), "one hit");
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1,
            "two BBDestroyMissile calls in the hit tell the client once: " + string.Join(",", rig.Match.Log.DestroyedMissiles));
        TestAssert.Equal(1.0, rig.Number("Ends"), "the end hook still ran, once");
        TestAssert.Equal(0, rig.Router.Missiles.Count, "the registry is empty");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-destroy-for-target-takes-the-flight-out-and-ends-it")]
    public static void DestroyMissileForTargetRetiresTheHomingFlights()
    {
        using var rig = new Rig();
        rig.Vars["DestroyOnHit"] = false;
        TestAssert.True(rig.Match.Cast(0, rig.Match.Foe), "Overload is accepted");
        var id = rig.Match.Log.Casts.Last(info => info.CasterNetId == rig.Match.Hero.NetId && info.Slot == 0).MissileNetId;
        rig.Match.Advance(0.35); // launched at 0.27 s, 300 units at 1400 per second arrive after about 0.5 s
        TestAssert.True(rig.Router.Missiles.Find(id) is not null, "the missile is in the air");
        TestAssert.Equal(0.0, rig.Number("Hits"), "and has not hit yet");

        // Hallucinate and Zhonya's Hourglass: the target's own script destroys every missile aimed at it.
        rig.Run(rig.Match.Foe, null, Block("BBDestroyMissileForTarget", "TargetVar", "Target"));
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1, "the client missile was destroyed once");
        rig.Match.Advance(1.0 / 30.0);
        TestAssert.Equal(1.0, rig.Number("Ends"), "the end hooks ran once, at the next step");
        TestAssert.True(rig.Vars["EndTarget"] is null, "no unit was reached");
        TestAssert.Equal(0, rig.Router.Missiles.Count, "the registry is empty");
        rig.Match.Advance(1.0);
        TestAssert.Equal(0.0, rig.Number("Hits"), "the destroyed missile never hit");
        TestAssert.Equal(1.0, rig.Number("Ends"), "and did not end again");
        TestAssert.True(rig.Match.Log.DestroyedMissiles.Count(missile => missile == id) == 1, "still one destroy");
        rig.NoMissileHookFailed();
    }

    [Check("scripting.bb-missile-spell-without-a-missile-has-no-missile-id")]
    public static void ASpellWithoutAMissileGetsNoMissileNetworkId()
    {
        using var rig = new Rig();
        rig.Vars["DestroyOnHit"] = false;
        // Rune Prison (CastType 0) lands when its windup ends: there is no missile, so there is no MissileNetworkID and no missile hook.
        TestAssert.True(rig.Match.Cast(1, rig.Match.Foe), "Rune Prison is accepted");
        rig.Match.Advance(1.0);
        TestAssert.Equal(1.0, rig.Number("Hits"), "TargetExecute ran");
        TestAssert.True(rig.Vars["SeenId"] is null, "no MissileNetworkID");
        TestAssert.Equal(0.0, rig.Number("Ends"), "no missile ended");
        rig.NoMissileHookFailed();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hand-built scripts
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock Block(string function, params object[] parameters) => BbTest.Block(function, parameters);

    /// <summary><c>CharVars[dest] = vars[source]</c>.</summary>
    private static BbBlock Set(string dest, string source) =>
        BbTest.Block("BBSetVarInTable", ["DestVar", dest, "DestVarTable", "CharVars", "SrcVar", source]);

    private static BbBlock Count(string name) =>
        BbTest.Block("BBMath", ["Src1Var", name, "Src1VarTable", "CharVars", "Src1Value", 0, "Src2Value", 1, "DestVar", name, "DestVarTable", "CharVars",
            "MathOp", BbTest.Ref("MO_ADD")]);

    private static BbBlock Destroy(string idVar, string? table = null) =>
        table is null ? Block("BBDestroyMissile", "MissileIDVar", idVar) : Block("BBDestroyMissile", "MissileIDVar", idVar, "MissileIDVarTable", table);

    /// <summary>
    /// A missile spell's script: TargetExecute counts the hit and keeps the ids it was given (and destroys the missile when the test asks);
    /// the end and update hooks keep what they read in <c>CharVars</c>.
    /// </summary>
    private static BbScript ProbeSpell(string name) => new(name, $"Spells/{name}.luaobj", [],
    [
        new BbEventTable("TargetExecute",
        [
            Count("Hits"), Set("SeenId", "MissileNetworkID"), Set("HitTarget", "Target"),
            BbTest.Block("BBIf", ["Src1Var", "DestroyOnHit", "Src1VarTable", "CharVars", "Value2", true, "CompareOp", BbTest.Ref("CO_EQUAL")],
                Destroy("MissileNetworkID"), Destroy("MissileNetworkID")),
        ]),
        new BbEventTable("SpellOnMissileEnd",
        [
            Count("Ends"), Set("EndSpellName", "SpellName"), Set("EndPos", "MissileEndPosition"), Set("EndTarget", "Target"),
            Set("EndOwner", "Owner"), Set("EndLevel", "Level"),
            Block("BBGetCastInfo", "DestVar", "EndCastLevel", "DestVarTable", "CharVars", "Info", BbTest.Ref("GetCastSpellLevelPlusOne")),
            Block("BBGetCastSpellTargetPos", "DestVar", "EndCastPos", "DestVarTable", "CharVars"),
            // The shipped Orianna scripts destroy their own, finished missile at the end of the hook; it is gone by then.
            Destroy("SeenId", "CharVars"),
        ]),
        new BbEventTable("SpellOnMissileUpdate", [Count("Updates"), Set("UpdPos", "MissilePosition"), Set("UpdOwner", "Owner")]),
    ], []);

    /// <summary>A script buff of the caster: it keeps the launch's <c>missileId</c> and <c>Target</c> and the end's <c>SpellName</c>.</summary>
    private static BbScript ProbeBuff() => new("ProbeBuff", "Spells/ProbeBuff.luaobj", [],
    [
        new BbEventTable("BuffOnLaunchMissile",
        [
            Set("LaunchId", "missileId"), Set("LaunchTarget", "Target"), Set("LaunchOwner", "Owner"),
            Block("BBGetMissilePosFromID", "TargetIDVar", "missileId", "TargetID", 0, "ResultVar", "LaunchPos", "ResultVarTable", "CharVars"),
        ]),
        new BbEventTable("BuffOnMissileEnd", [Count("BuffEnds"), Set("BuffEndSpell", "SpellName"), Set("BuffEndPos", "MissileEndPosition")]),
    ], []);

    // ------------------------------------------------------------------------------------------------------------
    // The rig: a production match whose Ryze module is a second, probe-closure instance bound to it
    // ------------------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Match = new BbM3.Match("Ryze");
            Match.Learn(1);
            var names = GameData.Default.Champion("Ryze").SpellNames;
            QName = names[0];
            var real = new BbScriptResolver(GameData.Default.Paths).Resolve("Ryze");
            // The probe scripts come first, so the set's first-wins lookup finds them before the shipped Overload and Rune Prison.
            var probes = new[] { ProbeSpell(names[0]), ProbeSpell(names[1]), ProbeBuff() };
            var scripts = probes.Select(script => new BbClosureScript(script.Name, script.SourcePath, BbScriptRole.Referenced, "BbMissileChecks", script))
                .Concat(real.Scripts).ToList();
            var closure = new BbClosure(real.Champion, scripts, real.Dangling, real.DataOnly, real.Diagnostics);
            Module = new BbChampionModule("Ryze");
            typeof(BbChampionModule).GetField("_scripts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Module, new BbScriptSet(closure));
            // The module builds each slot's spell (script, flags, validation) once, in its constructor, from the closure it had then
            // (BuildSlotSpell, W20): the probe closure has to reach those slots too, or the shipped Overload and Rune Prison run.
            var slots = (Array)typeof(BbChampionModule).GetField("_defaults", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Module)!;
            var build = typeof(BbChampionModule).GetMethod("BuildSlotSpell", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var slot = 0; slot < slots.Length; slot++) slots.SetValue(build.Invoke(Module, [names[slot], slot]), slot);

            var dispatcher = (ChampionSpellDispatcher)Match.Runtime.Dependencies.ChampionDispatcher;
            var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var modules = ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).ToArray();
            var index = Array.FindIndex(modules, module => ReferenceEquals(module, Match.Module));
            TestAssert.True(index >= 0, "production registers the script-driven Ryze");
            modules[index] = Module;
            field.SetValue(dispatcher, modules);
            ((MatchGameplayHooks)Match.Runtime.Dependencies.ChampionHooks).ScriptHooks = Module.Bind(Match.Runtime.Spells);

            Match.Foe.Position = new Position2(7300, 7000);
            Match.Refill();
        }

        public BbM3.Match Match { get; }
        public BbChampionModule Module { get; }
        public string QName { get; }
        public BbHookRouter Router => Module.Router!;
        /// <summary>The probe scripts keep what they read here.</summary>
        public BbTable Vars => Module.CharVars(Match.Hero);

        public double Number(string name) => Vars[name] is double value ? value : 0.0;

        public Position2 Point(string name) => Vars[name] is BbTable table
            ? new Position2(BbRuntimeNames.Number(table["x"]), BbRuntimeNames.Number(table["z"]))
            : throw new InvalidOperationException($"CharVars.{name} is not a point");

        /// <summary>
        /// No run of the probe scripts raised. (The shipped Ryze scripts around them run on this router too and are not what is checked here.)
        /// </summary>
        public void NoMissileHookFailed()
        {
            var failures = Module.ScriptFailures.Where(failure => failure.Hook is "TargetExecute" or "SpellOnMissileEnd" or "SpellOnMissileUpdate"
                or "BuffOnLaunchMissile" or "BuffOnMissileEnd").ToArray();
            TestAssert.Equal(0, failures.Length, "a probe hook failed: " + string.Join("; ", failures.Select(f => $"{f.Script}.{f.Hook} {f.BlockPath} {f.Message}")));
        }

        public void AddProbeBuff()
        {
            var state = Module.Buffs!.Add(new BbBuffRequest(Match.Hero, Match.Hero, "ProbeBuff", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
                (int)BbBuffType.BUFF_Internal, 1, 25000.0, 0.0, false, new BbTable()));
            TestAssert.True(state is not null, "the probe buff was added");
        }

        /// <summary>A line missile built the way <c>BbChampionModule.CastChild</c> builds one, with the probe script, enqueued in the shared queue.</summary>
        public uint Line(string spellName, Position2 aim)
        {
            var system = Match.Runtime.Spells;
            var hero = Match.Hero;
            var data = GameData.Default.Spell(spellName, 5);
            var id = Match.Runtime.World.Allocate(UnitType.Missile);
            var at = new Position3(aim.X, 0.0, aim.Z);
            var info = new SpellCastInfo(SpellSystem.ProtocolHash(spellName), hero.NetId, 1, hero.NetId, id, at, at, Array.Empty<SpellCastTarget>(),
                0.0f, 0.0f, 43, new Position3(hero.Position.X, 0.0, hero.Position.Z), 1.0f, (float)data.CastDelay, 0.0f, (float)data.TotalTime, 0.0f);
            system.EnqueueSharedProjectile(new BbLineMissile(Module.Forget, Router.SpellHost, ProbeSpell(spellName), spellName, hero, 0, 1, info, 0.0, aim, data,
                BbSpellTargeting.Names(BbSpellTargeting.Flags(data))));
            return id;
        }

        /// <summary>Runs a block list as a SelfExecute of the caster, with <paramref name="target"/> as Target and the given root variables.</summary>
        public void Run(Entity? target, IReadOnlyDictionary<string, object?>? vars, params BbBlock[] blocks)
        {
            var script = new BbScript("ProbeRun", "Spells/ProbeRun.luaobj", [], [new BbEventTable("SelfExecute", blocks)], []);
            var scope = Router.NewScope(Match.Hero, Match.Hero, target, Match.Hero, 1);
            if (vars is not null)
                foreach (var (name, value) in vars) scope.Vars[name] = value;
            Router.Run(script, "SelfExecute", scope, new BbHookFrame("SelfExecute", script.Name, Match.Hero) { Caster = Match.Hero, SpellRank = 1 }, rethrow: true);
        }

        public void Dispose() => Match.Dispose();
    }
}
