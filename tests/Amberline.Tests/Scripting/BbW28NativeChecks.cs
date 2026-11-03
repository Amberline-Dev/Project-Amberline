using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Champions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, Phase 3 (W28): the natives the six remaining hand-written champions (Ezreal, Lee Sin, Nidalee, Shaco, Yorick,
/// Orianna) still called after the movement, spawn and missile families: the perception bubbles, the visible nearest-N iteration, the death
/// and unit natives, experience, the mana cost increment, the offset target, channel stops and the on-hit damage carry-back. The probe
/// scripts are hand-built block lists run on a production <c>MatchRuntime</c> whose Sivir module hosts them; every parameter name is one the
/// shipped scripts write. Written 19 October 2026 and compiled, not run (owner rule).
/// </summary>
public static class BbW28NativeChecks
{
    private static BbScript Probe(string name, params BbBlock[] blocks) =>
        new(name, "Spells/" + name + ".luaobj", [], [new BbEventTable("TargetExecute", blocks)], []);

    /// <summary>Runs a probe's <c>TargetExecute</c> with the Sivir hero as Owner and <paramref name="target"/> (default: the enemy hero) as Target.</summary>
    private static void Run(BbM3.Match match, BbScript script, Entity? target = null)
    {
        var at = new Position3(0, 0, 0);
        match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, at, at, target ?? match.Foe);
    }

    private static void NoFailures(BbM3.Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    private static object? CharVar(BbM3.Match match, string name) => match.Module.CharVars(match.Hero)[name];

    // ------------------------------------------------------------------------------------------------------------
    // Perception bubbles
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock AddUnitBubble(double radius, double duration, bool reveal, string id, string target = "Target", string team = "TeamID",
        string client = "Nothing", string revealOnly = "Nothing") =>
        BbTest.Block("BBAddUnitPerceptionBubble", ["TeamVar", team, "Radius", radius, "TargetVar", target, "Duration", duration,
            "SpecificUnitsClientOnlyVar", client, "RevealSpecificUnitOnlyVar", revealOnly, "RevealSteath", reveal,
            "BubbleIDVar", id, "BubbleIDVarTable", "CharVars"]);

    private static BbBlock TeamOfOwner() => BbTest.Block("BBGetTeamID", ["TargetVar", "Owner", "DestVar", "TeamID"]);

    [Check("scripting.bb-w28-unit-perception-bubble-is-a-timed-team-bubble-on-the-unit")]
    public static void AUnitPerceptionBubbleIsATimedTeamBubbleAnchoredToTheUnit()
    {
        using var match = new BbM3.Match("Sivir");
        var bubbles = match.Runtime.World.TimedVisionBubbles;
        var before = bubbles.Count;
        // Lee Sin's Resonating Strike (BlindMonkQOne.OnBuffActivate): a 400 unit sight bubble and a 50 unit stealth reveal on the marked unit.
        Run(match, Probe("W28UnitBubble", TeamOfOwner(), AddUnitBubble(400, 20, false, "BubbleID"), AddUnitBubble(50, 20, true, "BubbleID2")));
        NoFailures(match);

        TestAssert.Equal(before + 2, bubbles.Count, "two timed bubbles were added to the one timed vision list");
        var sight = bubbles.Single(bubble => bubble.AnchorNetId == match.Foe.NetId && bubble.Radius == 400.0);
        var reveal = bubbles.Single(bubble => bubble.AnchorNetId == match.Foe.NetId && bubble.Radius == 50.0);
        TestAssert.Equal(match.Hero.Team, sight.Team, "the team is the TeamID variable (BBGetTeamID of the Owner)");
        TestAssert.Equal(match.Foe.NetId, sight.AnchorNetId, "the bubble follows the Target unit");
        TestAssert.True(!sight.RevealStealth && reveal.RevealStealth, "RevealSteath sets the stealth flag of the second bubble only");
        TestAssert.Near(20.0, sight.Remaining, 1e-9, "Duration is the lifetime in seconds");
        TestAssert.Equal((double)sight.BubbleId, CharVar(match, "BubbleID"), "the bubble id is written to BubbleID in BubbleIDVarTable");
        TestAssert.Equal((double)reveal.BubbleId, CharVar(match, "BubbleID2"), "and the second to BubbleID2");

        // The unit overload of the client's FindUpdateBubble: an identical live bubble only has its lifetime refreshed, and the id the native
        // returns is a new one that names no bubble.
        match.Advance(1.0);
        TestAssert.True(bubbles.Single(bubble => bubble.BubbleId == sight.BubbleId).Remaining < 19.5, "the bubble aged with the spell tick");
        Run(match, Probe("W28UnitBubbleAgain", TeamOfOwner(), AddUnitBubble(400, 20, false, "BubbleID3")));
        TestAssert.Equal(before + 2, bubbles.Count, "an identical bubble is not added twice");
        TestAssert.Near(20.0, bubbles.Single(bubble => bubble.BubbleId == sight.BubbleId).Remaining, 1e-9, "its lifetime was set to Duration again");
        var fresh = (double)CharVar(match, "BubbleID3")!;
        TestAssert.True(fresh != sight.BubbleId && bubbles.All(bubble => bubble.BubbleId != (uint)fresh), "the returned id is new and names no bubble");
        NoFailures(match);
    }

    [Check("scripting.bb-w28-unit-perception-bubble-needs-a-target-and-binds-the-client-only-variant")]
    public static void AUnitBubbleWithoutATargetDoesNothingAndTheClientOnlyVariantBinds()
    {
        using var match = new BbM3.Match("Sivir");
        var bubbles = match.Runtime.World.TimedVisionBubbles;
        var before = bubbles.Count;
        // The client body tests the Target lookup first: with no unit nothing is spawned and no id is pushed.
        Run(match, Probe("W28NoTarget", TeamOfOwner(), AddUnitBubble(300, 5, false, "NoTargetID", target: "Nothing")));
        NoFailures(match);
        TestAssert.Equal(before, bubbles.Count, "no unit, no bubble");
        TestAssert.True(CharVar(match, "NoTargetID") is null, "and no id is pushed");

        // Batch 452: SpecificUnitsClientOnly with a unit is bound (the Odin shrines' owner-only bubble, CharScriptOdinOrderTurretShrine block .12):
        // the bubble is added and carries its client's unit (TimedVisionBubble.ClientOnlyNetId), so the replication layer shows it to that unit's
        // client alone. Before batch 452 this was refused as "no delivery here"; the script wins, so the old refusal is gone.
        Run(match, Probe("W28ClientOnly", TeamOfOwner(), AddUnitBubble(300, 5, true, "ClientID", client: "Owner")));
        NoFailures(match);
        TestAssert.Equal(before + 1, bubbles.Count, "the client-only bubble is added");
        TestAssert.True(bubbles[bubbles.Count - 1].ClientOnlyNetId != 0, "for one client (its unit's netid)");
        // The slot validation no longer refuses it either.
        TestAssert.True(BbNativeHandlers.SpawnRefusal(AddUnitBubble(10, 2, false, "X", client: "Owner")) is null,
            "a bubble for one client only passes the validation");
        // W6 (19 October 2026): RevealSpecificUnitOnly is run (TimedVisionBubble.RevealUnitNetId), see BbW6VisionChecks.
        TestAssert.True(BbNativeHandlers.SpawnRefusal(AddUnitBubble(10, 2, false, "X", revealOnly: "Owner")) is null,
            "a reveal for one unit only is not refused any more");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(AddUnitBubble(400, 20, true, "X")) is null, "the ordinary bubble is not");
    }

    private static BbBlock AddPosBubble(string position, double radius, double duration, bool reveal, string id, string team = "TeamID") =>
        BbTest.Block("BBAddPosPerceptionBubble", ["TeamVar", team, "Radius", radius, "PosVar", position, "Duration", duration,
            "SpecificUnitsClientOnlyVar", "Nothing", "RevealSteath", reveal, "BubbleIDVar", id, "BubbleIDVarTable", "CharVars"]);

    [Check("scripting.bb-w28-pos-perception-bubble-stays-where-it-was-put")]
    public static void APositionBubbleIsATimedTeamBubbleAtThePoint()
    {
        using var match = new BbM3.Match("Sivir");
        var bubbles = match.Runtime.World.TimedVisionBubbles;
        var before = bubbles.Count;
        match.Hero.Facing = new Position2(1.0, 0.0);
        // Ezreal's Arcane Shift-style ground bubble: a point from the facing offset, then a bubble on it; a second one on the Owner (PosVar="Owner").
        Run(match, Probe("W28PosBubble", TeamOfOwner(),
            BbTest.Block("BBGetPointByUnitFacingOffset", ["UnitVar", "Owner", "Distance", 300, "OffsetAngle", 0, "PositionVar", "Spot"]),
            AddPosBubble("Spot", 500, 4, false, "BubbleID"), AddPosBubble("Owner", 120, 2.5, true, "BubbleID2")));
        NoFailures(match);

        TestAssert.Equal(before + 2, bubbles.Count, "two position bubbles were added to the timed vision list");
        var ground = bubbles.Single(bubble => bubble.Radius == 500.0 && bubble.AnchorNetId == 0);
        var owner = bubbles.Single(bubble => bubble.Radius == 120.0 && bubble.AnchorNetId == 0);
        TestAssert.Equal(match.Hero.Team, ground.Team, "the team is the TeamID variable");
        TestAssert.Near(4.0, ground.Remaining, 1e-9, "Duration");
        TestAssert.True(!ground.RevealStealth && owner.RevealStealth, "RevealSteath");
        TestAssert.Near(match.Hero.Position.X, owner.Position.X, 1e-3, "PosVar naming a unit is the unit's position at the call (x)");
        TestAssert.Near(match.Hero.Position.Z, owner.Position.Z, 1e-3, "and z");
        TestAssert.True(Math.Abs(ground.Position.X - match.Hero.Position.X) + Math.Abs(ground.Position.Z - match.Hero.Position.Z) > 250.0,
            "the point variable is a place away from the hero");
        TestAssert.Equal((double)ground.BubbleId, CharVar(match, "BubbleID"), "the id is written to BubbleID");

        // The same bubble at the same point is the manager's refresh.
        match.Advance(1.0);
        Run(match, Probe("W28PosBubbleAgain", TeamOfOwner(), AddPosBubble("Owner", 120, 2.5, true, "BubbleID3")));
        TestAssert.Equal(before + 2, bubbles.Count, "an identical position bubble is not added twice");
        TestAssert.Near(2.5, bubbles.Single(bubble => bubble.BubbleId == owner.BubbleId).Remaining, 1e-9, "its lifetime was set again");
    }

    private static BbBlock RemoveBubble(string id) =>
        BbTest.Block("BBRemovePerceptionBubble", ["BubbleIDVar", id, "BubbleIDVarTable", "CharVars"]);

    [Check("scripting.bb-w28-remove-perception-bubble-removes-by-the-stored-id")]
    public static void RemovingABubbleByItsStoredIdTakesItOutOfTheService()
    {
        using var match = new BbM3.Match("Sivir");
        var bubbles = match.Runtime.World.TimedVisionBubbles;
        var before = bubbles.Count;
        Run(match, Probe("W28Add", TeamOfOwner(), AddUnitBubble(400, 20, false, "BubbleID"), AddUnitBubble(50, 20, true, "BubbleID2")));
        TestAssert.Equal(before + 2, bubbles.Count, "two bubbles");
        // Lee Sin's OnBuffDeactivate removes both ids it stored in the buff's variables.
        Run(match, Probe("W28Remove", RemoveBubble("BubbleID"), RemoveBubble("BubbleID2")));
        NoFailures(match);
        TestAssert.Equal(before, bubbles.Count, "both bubbles are gone");
        // Removing an id again, or an id that names no bubble, does nothing.
        Run(match, Probe("W28RemoveAgain", RemoveBubble("BubbleID")));
        NoFailures(match);
        TestAssert.Equal(before, bubbles.Count, "a gone bubble stays gone");

        // A refreshed duplicate returns an id that names no bubble: removing it leaves the live bubble (the client's manager does the same).
        Run(match, Probe("W28AddTwice", TeamOfOwner(), AddUnitBubble(400, 20, false, "First"), AddUnitBubble(400, 20, false, "Second")));
        Run(match, Probe("W28RemoveSecond", RemoveBubble("Second")));
        NoFailures(match);
        TestAssert.Equal(before + 1, bubbles.Count, "the duplicate's id removed nothing");
        Run(match, Probe("W28RemoveFirst", RemoveBubble("First")));
        TestAssert.Equal(before, bubbles.Count, "the first id removes it");

        // An id variable that was never written is the client's Lua error.
        Run(match, Probe("W28RemoveUnset", RemoveBubble("NeverWritten")));
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "an unset BubbleID raised");
        TestAssert.True(match.Module.ScriptFailures[0].Message.Contains("must be present as a Integer", StringComparison.Ordinal),
            "with the client's message: " + match.Module.ScriptFailures[0].Message);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBForNClosestVisibleUnitsInTargetArea
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock Nearest(string function, string flags, double range) =>
        BbTest.Block(function, ["AttackerVar", "Owner", "CenterVar", "Owner", "Range", range, "Flags", flags, "IteratorVar", "Unit",
            "MaximumUnitsToPick", 1, "InclusiveBuffFilter", true], BbTest.Block("BBSetVarInTable",
            ["DestVar", function == "BBForNClosestVisibleUnitsInTargetArea" ? "SeenVisible" : "SeenAny", "DestVarTable", "CharVars", "SrcVar", "Unit"]));

    [Check("scripting.bb-w28-for-n-closest-visible-units-skips-what-the-attacker-cannot-see")]
    public static void TheVisibleIterationSkipsAStealthedEnemy()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var vars = match.Module.CharVars(match.Hero);
        const string flags = "AffectEnemies AffectHeroes ";
        // Shaco's Jack-in-the-Box asks for the nearest visible enemy within 400 of the box: an enemy hero 300 away, in plain sight.
        Run(match, Probe("W28Visible", Nearest("BBForNClosestVisibleUnitsInTargetArea", flags, 400), Nearest("BBForNClosestUnitsInTargetArea", flags, 400)));
        NoFailures(match);
        TestAssert.True(ReferenceEquals(vars["SeenVisible"], foe), "the visible iteration picks the enemy in sight");
        TestAssert.True(ReferenceEquals(vars["SeenAny"], foe), "so does the plain nearest-N one");

        // Stealthed and unrevealed, the enemy is still in the area but no longer visible to the Attacker's team.
        vars["SeenVisible"] = null;
        vars["SeenAny"] = null;
        match.Runtime.Spells.Buffs.Add(foe, "W28Hidden", BuffType.Internal, 30.0, foe.NetId, new BuffPayload { Stealthed = true });
        Run(match, Probe("W28Hidden", Nearest("BBForNClosestVisibleUnitsInTargetArea", flags, 400), Nearest("BBForNClosestUnitsInTargetArea", flags, 400)));
        NoFailures(match);
        TestAssert.True(vars["SeenVisible"] is null, "the visible iteration skips a stealthed enemy");
        TestAssert.True(ReferenceEquals(vars["SeenAny"], foe), "the plain iteration does not look at sight");
        TestAssert.True(BbNativeHandlers.IsBound("BBForNClosestVisibleUnitsInTargetArea") && BbNativeHandlers.IsBound("BBSetUnit") &&
                        BbNativeHandlers.IsBound("BBInvalidateUnit") &&
                        BbNativeHandlers.IsBound("BBGetChampionBySkinName") &&
                        BbNativeHandlers.IsBound("BBIncExp") &&
                        BbNativeHandlers.IsBound("BBGetIsZombie") &&
                        BbNativeHandlers.IsBound("BBForceDead") &&
                        BbNativeHandlers.IsBound("BBShowHealthBar") &&
                        BbNativeHandlers.IsBound("BBSetPARCostInc") &&
                        BbNativeHandlers.IsBound("BBSetSpellOffsetTarget") &&
                        BbNativeHandlers.IsBound("BBStopChanneling"), "the natives are bound");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetUnit, BBInvalidateUnit, BBGetChampionBySkinName
    // ------------------------------------------------------------------------------------------------------------

    private static object? Root(BbM3.Match match, string name) => match.Module.CharVars(match.Hero)[name];

    /// <summary>Copies a root-scope variable into CharVars, so a check can read what a probe's scope held.</summary>
    private static BbBlock Keep(string variable, string key) =>
        BbTest.Block("BBSetVarInTable", ["DestVar", key, "DestVarTable", "CharVars", "SrcVar", variable]);

    [Check("scripting.bb-w28-set-unit-copies-the-unit-variable")]
    public static void SetUnitStoresTheSameUnitUnderTheDestinationName()
    {
        using var match = new BbM3.Match("Sivir");
        // The H-28G turret and Caitlyn's trap: Other2 = Owner, then the oldest unit an iteration finds.
        Run(match, Probe("W28SetUnit",
            BbTest.Block("BBSetUnit", ["SrcVar", "Owner", "DestVar", "Other2"]), Keep("Other2", "First"),
            BbTest.Block("BBSetUnit", ["SrcVar", "Target", "DestVar", "Other2"]), Keep("Other2", "Second"),
            BbTest.Block("BBSetUnit", ["SrcVar", "NoSuchVariable", "DestVar", "Other2"]), Keep("Other2", "Third")));
        NoFailures(match);
        TestAssert.True(ReferenceEquals(Root(match, "First"), match.Hero), "Other2 holds the Owner after the first copy");
        TestAssert.True(ReferenceEquals(Root(match, "Second"), match.Foe), "and the Target after the second");
        TestAssert.True(Root(match, "Third") is not Entity, "a source variable that holds no unit leaves the destination holding none");
    }

    [Check("scripting.bb-w28-invalidate-unit-empties-the-variable")]
    public static void InvalidateUnitLeavesNoUnitUnderTheName()
    {
        using var match = new BbM3.Match("Sivir");
        // H28GEvolutionTurret.SelfExecute: Other2 is emptied before the iteration refills it with the unit that has the least time left.
        Run(match, Probe("W28Invalidate",
            BbTest.Block("BBSetUnit", ["SrcVar", "Owner", "DestVar", "Other2"]), Keep("Other2", "Before"),
            BbTest.Block("BBInvalidateUnit", ["TargetVar", "Other2"]), Keep("Other2", "Emptied"),
            // A native that takes a unit finds none in the emptied slot and does nothing (the damage would have been 10000).
            BbTest.Block("BBApplyDamage", ["AttackerVar", "Owner", "TargetVar", "Other2", "Damage", 10000, "DamageType", BbTest.Ref("TRUE_DAMAGE"),
                "SourceDamageType", BbTest.Ref("DAMAGESOURCE_INTERNALRAW"), "PercentOfAttack", 1, "SpellDamageRatio", 0, "PhysicalDamageRatio", 1]),
            BbTest.Block("BBSetUnit", ["SrcVar", "Target", "DestVar", "Other2"]), Keep("Other2", "Refilled")));
        NoFailures(match);
        TestAssert.True(ReferenceEquals(Root(match, "Before"), match.Hero), "Other2 held the Owner");
        TestAssert.True(Root(match, "Emptied") is not Entity, "it holds no unit after BBInvalidateUnit");
        TestAssert.True(ReferenceEquals(Root(match, "Refilled"), match.Foe), "and can be filled again");
        TestAssert.True(match.Hero.Health >= 5000.0 - 1e-6, "the damage aimed at the emptied slot hit nobody");
    }

    [Check("scripting.bb-w28-get-champion-by-skin-name-finds-the-hero-of-that-team")]
    public static void TheChampionIsFoundByCharacterNameAndTeam()
    {
        using var match = new BbM3.Match("Sivir");
        // The match: Sivir (Order, hero), Annie (Order, ally), Orianna (Chaos), Ezreal (Chaos). Shaco's box reads its master with TeamVar = its own team.
        Run(match, Probe("W28Champion", TeamOfOwner(),
            BbTest.Block("BBGetChampionBySkinName", ["Skin", "Annie", "Team", BbTest.Ref("TEAM_UNKNOWN"), "TeamVar", "TeamID", "DestVar", "Master"]), Keep("Master", "ByVar"),
            // The literal team (ShacoBoxBasicAttack passes TEAM_ORDER) and the case-insensitive compare.
            BbTest.Block("BBGetChampionBySkinName", ["Skin", "oRiAnNa", "Team", BbTest.Ref("TEAM_CHAOS"), "DestVar", "Foe"]), Keep("Foe", "Literal"),
            // Right name, wrong team: the variable keeps what it held.
            BbTest.Block("BBGetChampionBySkinName", ["Skin", "Orianna", "Team", BbTest.Ref("TEAM_ORDER"), "DestVar", "Wrong"]), Keep("Wrong", "WrongTeam"),
            // No hero of that name at all.
            BbTest.Block("BBGetChampionBySkinName", ["Skin", "Teemo", "Team", BbTest.Ref("TEAM_CHAOS"), "DestVar", "None"]), Keep("None", "NoHero")));
        NoFailures(match);
        TestAssert.True(ReferenceEquals(Root(match, "ByVar"), match.Ally), "TeamVar (the Owner's team) and the name find the allied Annie");
        TestAssert.True(ReferenceEquals(Root(match, "Literal"), match.Foe), "a literal team and a name in another case find the enemy Orianna");
        TestAssert.True(Root(match, "WrongTeam") is null, "a hero of that name on the other team is not found");
        TestAssert.True(Root(match, "NoHero") is null, "a name no hero has finds nothing and leaves the variable unset");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBIncExp
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-w28-inc-exp-awards-experience-through-the-progression-service")]
    public static void IncExpGivesAHeroExperience()
    {
        using var match = new BbM3.Match("Sivir");
        var ally = match.Ally;
        var before = ally.Experience;
        // CharScriptNidalee.UpdateSelfBuffActions: BBIncExp TargetVar=Unit Delta=5 on a lower level allied champion (here the ally is Target).
        Run(match, Probe("W28IncExp", BbTest.Block("BBSetUnit", ["SrcVar", "Target", "DestVar", "Unit"]), BbTest.Block("BBIncExp", ["TargetVar", "Unit", "Delta", 5])), ally);
        NoFailures(match);
        TestAssert.Near(5.0, ally.Experience - before, 1.0, "the ally gained the 5 experience (the masteries' bonus aside)");
        TestAssert.True(ally.Experience - before >= 5.0 - 1e-9, "never less than the script's delta");

        // A zero delta changes nothing and a negative one is refused.
        var foeBefore = match.Foe.Experience;
        Run(match, Probe("W28IncExpMinion", BbTest.Block("BBIncExp", ["TargetVar", "Target", "Delta", 0])), match.Foe);
        TestAssert.Equal(foeBefore, match.Foe.Experience, "a zero delta changes nothing");
        Run(match, Probe("W28IncExpNegative", BbTest.Block("BBIncExp", ["TargetVar", "Target", "Delta", -5])), match.Foe);
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "a negative delta raised");
        TestAssert.Equal(foeBefore, match.Foe.Experience, "and took nothing away");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBGetIsZombie, BBForceDead
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-w28-get-is-zombie-reads-the-temporary-life")]
    public static void GetIsZombieIsTrueForAUnitInYoricksTemporaryLife()
    {
        using var match = new BbM3.Match("Sivir");
        // YorickReviveAlly.TargetExecute: BBGetIsZombie UnitVar=Owner ResultVar=zombie, then the marker child only when it is false.
        Run(match, Probe("W28Zombie", BbTest.Block("BBGetIsZombie", ["UnitVar", "Owner", "ResultVar", "zombie"]), Keep("zombie", "Living"),
            BbTest.Block("BBGetIsZombie", ["UnitVar", "Target", "ResultVar", "zombie"]), Keep("zombie", "Marked")));
        NoFailures(match);
        TestAssert.Equal(false, Root(match, "Living"), "a living unit is no zombie");
        TestAssert.Equal(false, Root(match, "Marked"), "nor is an enemy before it has a temporary life");

        match.Foe.YorickRevival = new Amberline.Simulation.Spells.Champions.YorickRevivalState(match.Now + 1.0);
        TestAssert.True(match.Foe.IsZombie, "a unit with a temporary life is a zombie");
        Run(match, Probe("W28ZombieNow", BbTest.Block("BBGetIsZombie", ["UnitVar", "Target", "ResultVar", "zombie"]), Keep("zombie", "Marked")));
        NoFailures(match);
        TestAssert.Equal(true, Root(match, "Marked"), "BBGetIsZombie reports it");
    }

    [Check("scripting.bb-w28-force-dead-finishes-the-zombie-life-once")]
    public static void ForceDeadEndsATemporaryLifeAndRefusesALivingUnitWithNone()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var wire = BbM3.Observe(match, Team.Order, match.Hero);
        match.Advance(0.1); // the new observer learns the world first: a unit it has not met gets no lifecycle packet, and a hidden bar is told with its entry
        int ForceDeadPackets() => wire.Packets.Count(entry => entry.Packet.Plaintext.Span[0] == 0x1E &&
                                                              BitConverter.ToUInt32(entry.Packet.Plaintext.Span.Slice(1, 4)) == foe.NetId);
        var force = Probe("W28ForceDead", BbTest.Block("BBForceDead", ["OwnerVar", "Target"]));

        // A living unit in no zombie life: nothing to end, refused (and not killed).
        Run(match, force, foe);
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "a living unit in no zombie life raised");
        TestAssert.True(foe.Alive, "and was not killed");

        // A unit in a zombie life (the token Yorick's script life sets; one HP): ForceDead finishes it through SpellSystem.EndZombieLife,
        // the seam the production composition binds to MatchGameplayHooks.EndZombieLife.
        foe.YorickRevival = new Amberline.Simulation.Spells.Champions.YorickRevivalState(match.Now + 10.0);
        foe.Health = 1.0;
        Run(match, force, foe);
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "the zombie's ForceDead raised nothing more");
        TestAssert.True(!foe.Alive && foe.Health == 0.0, "the unit is dead");
        TestAssert.True(foe.YorickRevival is null && !foe.IsZombie, "the temporary life is over, so the unit is no zombie");
        match.Advance(0.1);
        TestAssert.Equal(1, ForceDeadPackets(), "the ForceDead packet (0x1E) was sent once");

        // The unit is dead now: a second ForceDead (the buff's deactivation hook runs inside the finish) sets the same flags again and changes nothing.
        Run(match, force, foe);
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "a dead unit is left as it is");
        match.Advance(0.1);
        TestAssert.Equal(1, ForceDeadPackets(), "no second packet");
    }

    [Check("scripting.bb-w28-show-health-bar-sends-the-0xd7-packet-and-keeps-the-flag")]
    public static void ShowHealthBarBroadcastsTheFlag()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        var wire = BbM3.Observe(match, Team.Order, match.Hero);
        match.Advance(0.1); // the new observer learns the world first: a unit it has not met gets no lifecycle packet, and a hidden bar is told with its entry
        List<byte[]> HealthBarPackets() => wire.Packets.Select(entry => entry.Packet.Plaintext.ToArray())
            .Where(bytes => bytes[0] == 0xD7 && BitConverter.ToUInt32(bytes, 1) == foe.NetId).ToList();
        // DeathDefiedBuff.OnBuffActivate hides the bar, OnBuffDeactivate shows it again.
        Run(match, Probe("W28Hide", BbTest.Block("BBShowHealthBar", ["UnitVar", "Target", "Show", false])), foe);
        NoFailures(match);
        TestAssert.True(foe.HealthBarHidden, "the server keeps the hidden flag for a client that learns the unit later");
        match.Advance(0.1);
        var hide = HealthBarPackets();
        TestAssert.Equal(1, hide.Count, "one 0xD7 packet reached the observer");
        TestAssert.Equal(6, hide[0].Length, "it is six bytes");
        TestAssert.Equal((byte)0, hide[0][5], "show is false");
        Run(match, Probe("W28Show", BbTest.Block("BBShowHealthBar", ["UnitVar", "Target", "Show", true])), foe);
        match.Advance(0.1);
        TestAssert.True(!foe.HealthBarHidden, "shown again");
        var all = HealthBarPackets();
        TestAssert.Equal(2, all.Count, "and told");
        TestAssert.Equal((byte)1, all[1][5], "show is true");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetPARCostInc
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock CostInc(int slot, double cost, string parType = "PAR_MANA") =>
        BbTest.Block("BBSetPARCostInc", ["SpellSlotOwnerVar", "Owner", "SpellSlot", slot, "SlotType", BbTest.Ref("SpellSlots"), "Cost", cost,
            "PARType", BbTest.Ref(parType)]);

    [Check("scripting.bb-w28-set-par-cost-inc-adds-to-what-the-slot-costs")]
    public static void TheCostIncrementChangesTheDisplayedAndTheChargedCost()
    {
        using var match = new BbM3.Match("Sivir");
        match.Learn(1);
        var hero = match.Hero;
        double Shown(int slot) => match.Runtime.HeroStats.Snapshot(hero).ManaCosts[slot];
        var baseCost = Shown(0);
        TestAssert.True(baseCost > 30.0, $"Spiral Blade rank 1 has a real cost ({baseCost})");

        // DeathDefiedBuff and Nocturne's Paranoia lower a slot's cost by a negative increment; the displayed cost follows, and so does the cast gate.
        Run(match, Probe("W28CostInc", CostInc(0, -30)));
        NoFailures(match);
        TestAssert.Near(baseCost - 30.0, Shown(0), 1e-6, "the stat block shows the spell's cost plus the increment");
        TestAssert.Near(baseCost - 30.0, SpellSystem.SlotResourceCost(hero, 0, baseCost), 1e-6, "and so does the cost a cast pays");
        hero.Mana = baseCost - 10.0;
        TestAssert.True(match.CanCast(0, match.Foe).Failure != SpellCastFailure.Mana, "a hero 10 short of the full cost can pay the lowered one");

        // It is a set, not an add: the same call again leaves the same cost, and 0 puts the spell's own cost back.
        Run(match, Probe("W28CostIncAgain", CostInc(0, -30)));
        TestAssert.Near(baseCost - 30.0, Shown(0), 1e-6, "setting the same increment twice does not stack");
        Run(match, Probe("W28CostIncZero", CostInc(0, 0)));
        TestAssert.Near(baseCost, Shown(0), 1e-6, "Cost 0 removes the increment");
        TestAssert.True(match.CanCast(0, match.Foe).Failure == SpellCastFailure.Mana, "and the 10 short hero cannot cast it again");

        // A cost can never go below zero: Cost -100 on a 70 mana spell is free.
        Run(match, Probe("W28CostFree", CostInc(0, -1000)));
        TestAssert.Near(0.0, Shown(0), 1e-6, "the cost is floored at zero");
        Run(match, Probe("W28CostInc0", CostInc(0, 0)));

        // The client's two alert paths change nothing: an owner of another resource type, and a slot outside the spellbook.
        Run(match, Probe("W28CostEnergy", CostInc(0, -30, "PAR_ENERGY"), CostInc(7, -30)));
        NoFailures(match);
        TestAssert.Near(baseCost, Shown(0), 1e-6, "a PARType the owner does not have changes nothing");
        TestAssert.True(hero.SpellParCostIncrements.Count == 0, "and a slot out of range stores nothing");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetSpellOffsetTarget
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-w28-set-spell-offset-target-sends-0x37-and-keeps-the-target")]
    public static void TheOffsetTargetIsSentAndKept()
    {
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        var wire = BbM3.Observe(match, Team.Order, hero);
        // OrianaGhost.OnBuffActivate: Dissonance (slot 1) and Shockwave (slot 3) are centred on the new carrier of the ball.
        Run(match, Probe("W28OffsetTarget",
            BbTest.Block("BBSetSpellOffsetTarget", ["SlotNumber", 1, "SlotType", BbTest.Ref("SpellSlots"), "SpellName", "JunkName",
                "SpellbookType", BbTest.Ref("SPELLBOOK_CHAMPION"), "OwnerVar", "Owner", "OffsetTargetVar", "Target"]),
            BbTest.Block("BBSetSpellOffsetTarget", ["SlotNumber", 3, "SlotType", BbTest.Ref("SpellSlots"), "SpellName", "JunkName",
                "SpellbookType", BbTest.Ref("SPELLBOOK_CHAMPION"), "OwnerVar", "Owner", "OffsetTargetVar", "Target"])), match.Ally);
        NoFailures(match);
        TestAssert.Equal(match.Ally.NetId, hero.SpellOffsetTargets[1], "slot 1 is centred on the ally");
        TestAssert.Equal(match.Ally.NetId, hero.SpellOffsetTargets[3], "and slot 3");
        match.Advance(0.1);
        var packets = wire.Packets.Select(entry => entry.Packet.Plaintext.ToArray())
            .Where(bytes => bytes[0] == 0x37 && BitConverter.ToUInt32(bytes, 1) == hero.NetId).ToList();
        TestAssert.Equal(2, packets.Count, "two 0x37 packets reached the observer");
        TestAssert.Equal(10, packets[0].Length, "ten bytes: header, slot, target");
        TestAssert.Equal((byte)1, packets[0][5], "the slot byte (champion book, slot 1)");
        TestAssert.Equal(match.Ally.NetId, BitConverter.ToUInt32(packets[0], 6), "the offset target's id");

        // A slot outside the spellbook is the client's out-of-range error; the summoner book has no call and is refused.
        Run(match, Probe("W28OffsetBadSlot", BbTest.Block("BBSetSpellOffsetTarget", ["SlotNumber", 9, "SlotType", BbTest.Ref("SpellSlots"),
            "SpellName", "JunkName", "SpellbookType", BbTest.Ref("SPELLBOOK_CHAMPION"), "OwnerVar", "Owner", "OffsetTargetVar", "Target"])), match.Ally);
        TestAssert.Equal(1, match.Module.ScriptFailures.Count, "slot 9 of the spell slots raised");
        TestAssert.True(!hero.SpellOffsetTargets.ContainsKey(9), "and stored nothing");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBStopChanneling
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock StopChannel(string source, string condition = "ChannelingStopCondition_Cancel") =>
        BbTest.Block("BBStopChanneling", ["CasterVar", "Owner", "StopCondition", BbTest.Ref(condition), "StopSource", BbTest.Ref(source)]);

    [Check("scripting.bb-w28-stop-channeling-ends-a-recall-unless-the-source-is-completion")]
    public static void StopChannelingEndsTheRecallExceptForACompletedChannel()
    {
        using var match = new BbM3.Match("Sivir");
        // The Taunt, Zhonya's Hourglass and zombie scripts stop the owner's channel with a cancel; a recall is one. Batch 179: the
        // recall's own stop assertions moved with its channel to the summoner module (no harness here starts a Recall channel), so this
        // check keeps the native's probes and its no-failure rule.
        Run(match, Probe("W28StopTime", StopChannel("ChannelingStopSource_TimeCompleted", "ChannelingStopCondition_Success")));
        Run(match, Probe("W28StopAnimation", StopChannel("ChannelingStopSource_Animation")));
        Run(match, Probe("W28StopUnknown", StopChannel("ChannelingStopSource_Unknown")));
        Run(match, Probe("W28StopTaunted", StopChannel("ChannelingStopSource_StunnedOrSilencedOrTaunted")));
        NoFailures(match);
        // Nothing channelling: harmless. A unit that is not a hero never channels here.
        Run(match, Probe("W28StopNothing", StopChannel("ChannelingStopSource_Die")));
        Run(match, Probe("W28StopMinion", BbTest.Block("BBStopChanneling", ["CasterVar", "Target", "StopCondition", BbTest.Ref("ChannelingStopCondition_Cancel"),
            "StopSource", BbTest.Ref("ChannelingStopSource_Die")])), BbM3.Enemy(match, "Dummy", new Position2(7400, 7000)));
        NoFailures(match);
    }

    [Check("scripting.bb-w28-natives-are-bound")]
    public static void TheW28NativesAreBound()
    {
        foreach (var native in new[] { "BBAddUnitPerceptionBubble", "BBAddPosPerceptionBubble", "BBRemovePerceptionBubble" })
            TestAssert.True(BbNativeHandlers.IsBound(native), native + " is bound");
    }

    // ------------------------------------------------------------------------------------------------------------
    // CharOnHitUnit / BuffOnHitUnit carry DamageAmount back to the hit
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>A buff whose BuffOnHitUnit records the HitResult and DamageAmount it read (a second run after the damage would overwrite them with the doubled hit) and doubles the hit (Backstab's shape, x2).</summary>
    private static BbScript HitProbe() => new("W28HitProbe", "Spells/W28HitProbe.luaobj", [],
    [
        new BbEventTable("BuffOnHitUnit",
        [
            BbTest.Block("BBSetVarInTable", ["DestVar", "SeenDamage", "DestVarTable", "InstanceVars", "SrcVar", "DamageAmount"]),
            BbTest.Block("BBSetVarInTable", ["DestVar", "SeenResult", "DestVarTable", "InstanceVars", "SrcVar", "HitResult"]),
            BbTest.Block("BBMath", ["Src1Var", "DamageAmount", "Src1Value", 0, "Src2Value", 2, "DestVar", "DamageAmount", "MathOp", BbTest.Ref("MO_MULTIPLY")]),
        ]),
    ], []);

    /// <summary>Installs a second Sivir module whose closure holds the probe buff (the rig of the event hook checks) and gives the hero the buff.</summary>
    private static (BbChampionModule Module, BbBuffInstance Probe) InstallHitProbe(BbM3.Match match)
    {
        var real = new BbScriptResolver(GameData.Default.Paths).Resolve("Sivir");
        var probe = HitProbe();
        var scripts = new List<BbClosureScript> { new(probe.Name, probe.SourcePath, BbScriptRole.Referenced, "BbW28NativeChecks", probe) };
        scripts.AddRange(real.Scripts);
        var closure = new BbClosure(real.Champion, scripts, real.Dangling, real.DataOnly, real.Diagnostics);
        var module = new BbChampionModule("Sivir");
        typeof(BbChampionModule).GetField("_scripts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, new BbScriptSet(closure));
        var dispatcher = (ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher;
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modules = ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).ToArray();
        var index = Array.FindIndex(modules, existing => ReferenceEquals(existing, match.Module));
        TestAssert.True(index >= 0, "production registers the script-driven Sivir");
        modules[index] = module;
        field.SetValue(dispatcher, modules);
        ((MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(match.Runtime.Spells);
        var state = module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Hero, probe.Name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 25000.0, 0.0, false, new BbTable()));
        return (module, module.Buffs.InstanceOf(state ?? throw new InvalidOperationException("the probe buff was not added"))
                        ?? throw new InvalidOperationException("the probe buff has no script instance"));
    }

    [Check("scripting.bb-w28-on-hit-hooks-change-the-damage-of-the-hit-they-run-in")]
    public static void AnOnHitHookThatChangesDamageAmountChangesTheHit()
    {
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        match.Foe.Position = new Position2(7100, 7000);
        var rolls = new AttackRolls(1.0, 1.0, 1.0);

        // Baseline: Sivir's plain swing, no probe.
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe, rolls), "the first attack starts");
        match.Advance(2.5);
        var plain = BbM3.HitsOn(match, "Foe").Single(hit => hit.Kind == "Basic");

        // With a BuffOnHitUnit that doubles DamageAmount (Backstab's x1.2 has the same shape): the hit is resolved once, doubled, before mitigation.
        var (module, probe) = InstallHitProbe(match);
        match.Foe.Health = match.Foe.MaxHealth;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe, rolls), "the second attack starts");
        match.Advance(2.5);
        var doubled = BbM3.HitsOn(match, "Foe").Where(hit => hit.Kind == "Basic").Skip(1).Single();
        TestAssert.Near(plain.Raw, (double)probe.InstanceVars["SeenDamage"]!, 1e-6 + plain.Raw * 1e-6,
            "BuffOnHitUnit read the swing's damage as DamageAmount, and did not run a second time after the damage (that would have read the doubled hit)");
        TestAssert.Equal(0.0, probe.InstanceVars["SeenResult"], "it read HIT_Normal as HitResult");
        TestAssert.Near(plain.Raw * 2.0, doubled.Raw, plain.Raw * 1e-4, "the hit that landed carries the doubled DamageAmount");
        TestAssert.True(doubled.Dealt > plain.Dealt * 1.9 && doubled.Dealt < plain.Dealt * 2.1, "and so does the damage it dealt after mitigation");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }
}
