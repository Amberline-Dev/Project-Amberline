using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 7 bundle (W27c, 19 October 2026): the model scale of a script buff, the geometry iterators, the sight link, the per-team
/// targetability, the path refresh and the solidity of the wall units. The probe scripts are hand-built block lists run on a production
/// <c>MatchRuntime</c> whose Sivir module hosts them; every parameter name is one the shipped scripts write. Written 19 October 2026 and compiled, not
/// run (owner rule).
/// </summary>
public static class BbW27cChecks
{
    private static BbScript Probe(string name, params BbBlock[] blocks) =>
        new(name, "Spells/" + name + ".luaobj", [], [new BbEventTable("TargetExecute", blocks)], []);

    private static BbBlock Block(string function, params object[] pairs) => BbTest.Block(function, pairs);

    private static BbBlock WithSubBlocks(BbBlock block, params BbBlock[] subBlocks) => block with { SubBlocks = subBlocks };

    private static void Run(BbM3.Match match, BbScript script, Entity? target = null)
    {
        var at = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
        match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, at, at, target ?? match.Foe);
    }

    private static void NoFailures(BbM3.Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    private static BbBlock Spawn(string skin) => Block("BBSpawnMinion", "Name", "WallPiece", "Skin", skin, "AiScript", "idle.lua", "PosVar", "Pos",
        "Team", (double)BbTeam.TEAM_UNKNOWN, "TeamVar", "teamID", "Stunned", true, "Rooted", true, "Silenced", true, "Invulnerable", true,
        "MagicImmune", false, "IgnoreCollision", true, "IsWard", false, "Placemarker", false, "VisibilitySize", 0.0,
        "DestVar", "Piece", "GoldRedirectTargetVar", "Nothing");

    private static List<Entity> Spawned(BbM3.Match match, string skin, HashSet<uint> known) =>
        match.Runtime.World.Entities.Values.Where(unit => unit.ChampionName == skin && !known.Contains(unit.NetId)).OrderBy(unit => unit.NetId).ToList();

    [Check("scripting.bb-w7-point-on-line-is-the-clients-line-across-the-facing")]
    public static void TheLineRunsAcrossTheFacingAndStartsOneStepIn()
    {
        using var match = new BbM3.Match("Sivir");
        // The owner at (7000, 7000) faces the foe at (7300, 7000): the line is centred 400 in front of the owner and runs along z.
        var known = new HashSet<uint>();
        Run(match, Probe("W7Line",
            Block("BBGetUnitPosition", "UnitVar", "Owner", "PositionVar", "OwnerPos"),
            Block("BBGetUnitPosition", "UnitVar", "Target", "PositionVar", "TargetPos"),
            Block("BBGetTeamID", "TargetVar", "Owner", "DestVar", "teamID"),
            WithSubBlocks(Block("BBForEachPointOnLine", "CenterVar", "OwnerPos", "FaceTowardsPosVar", "TargetPos", "Size", 800.0, "PushForward", 400.0,
                "Iterations", 8, "IteratorVar", "Pos"), Spawn("AniviaIceblock"))));
        NoFailures(match);
        var pieces = Spawned(match, "AniviaIceblock", known);
        TestAssert.Equal(8, pieces.Count, "eight points");
        TestAssert.True(pieces.All(piece => Math.Abs(piece.Position.X - 7400.0) < 1e-3), "all on the line x = owner + 400");
        // perp = (d.z, -d.x) = (0, -1); the first point is a step (100) in from the start 400 to the +perp side's far end.
        TestAssert.Near(7300.0, pieces[0].Position.Z, 1e-3, "the first point is one step from the 7400 end of the line");
        TestAssert.Near(6600.0, pieces[^1].Position.Z, 1e-3, "the last point is the other end of the line (centre - Size / 2)");
        TestAssert.Near(100.0, pieces[0].Radius, 1e-6, "the ice block's pathfinding radius is the character data's");
        TestAssert.True(pieces.All(piece => !piece.Buffs.Any(buff => buff.Payload.Ghosted)),
            "a unit of that size is a solid body: the IgnoreCollision flag no longer makes it a ghost");
    }

    [Check("scripting.bb-w7-point-around-circle-steps-the-integer-degrees")]
    public static void TheRingHasTwelvePointsOnTheRadius()
    {
        using var match = new BbM3.Match("Sivir");
        var known = new HashSet<uint>();
        Run(match, Probe("W7Ring",
            Block("BBGetUnitPosition", "UnitVar", "Owner", "PositionVar", "CenterPos"),
            Block("BBGetTeamID", "TargetVar", "Owner", "DestVar", "teamID"),
            WithSubBlocks(Block("BBForEachPointAroundCircle", "CenterVar", "CenterPos", "Radius", 350.0, "Iterations", 12, "IteratorVar", "Pos"),
                Spawn("JarvanIVWall"))));
        NoFailures(match);
        var ring = Spawned(match, "JarvanIVWall", known);
        TestAssert.Equal(12, ring.Count, "360 / 12 = 30 degrees: twelve points");
        TestAssert.True(ring.All(piece => Math.Abs(Math.Sqrt(Math.Pow(piece.Position.X - 7000.0, 2) + Math.Pow(piece.Position.Z - 7000.0, 2)) - 350.0) < 1e-3),
            "each on the 350 circle");
        TestAssert.Equal(12, ring.Select(piece => (Math.Round(piece.Position.X), Math.Round(piece.Position.Z))).Distinct().Count(), "twelve distinct points");
    }

    [Check("scripting.bb-w7-rectangle-is-the-box-rounded-by-the-unit-radius")]
    public static void TheRectangleCountsAUnitWhoseDiscTouchesTheBox()
    {
        using var match = new BbM3.Match("Sivir");
        var flags = new HashSet<string> { "AffectEnemies", "AffectFriends", "AffectHeroes", "NotAffectSelf" };
        var forward = new Position2(1.0, 0.0);
        var centre = new Position2(7300.0, 7000.0);
        // The ally stands 150 behind the centre on the long axis, 0 off it; its disc (the combat radius) must reach the box.
        var radius = match.Ally.CombatRadius;
        var inside = BbSpellTargeting.Rectangle(match.Runtime.Spells, match.Hero, centre, forward, 100.0, 150.0 - radius + 1.0, flags);
        var outside = BbSpellTargeting.Rectangle(match.Runtime.Spells, match.Hero, centre, forward, 100.0, 150.0 - radius - 1.0, flags);
        TestAssert.True(inside.Contains(match.Ally), "a box that ends within the unit's radius of its centre counts the unit");
        TestAssert.True(!outside.Contains(match.Ally), "one that ends further does not");
        TestAssert.True(inside.Contains(match.Foe), "the unit at the centre is in");
        TestAssert.True(!inside.Contains(match.Hero), "NotAffectSelf leaves the attacker out");
    }

    [Check("scripting.bb-w7-inc-scale-skin-coef-counts-in-the-stats-hook-only")]
    public static void TheModelScaleIsOnePlusTheStatsHookIncrements()
    {
        using var match = new BbM3.Match("Sivir");
        TestAssert.Near(1.0, ModelScale.Of(match.Hero), 1e-6, "a unit without scaling buffs has the base coefficient");
        // A call outside a stats hook changes a value the next update overwrites: it does nothing.
        Run(match, Probe("W7ScaleOutside", Block("BBIncScaleSkinCoef", "OwnerVar", "Owner", "Scale", 0.1)), match.Hero);
        NoFailures(match);
        TestAssert.Near(1.0, ModelScale.Of(match.Hero), 1e-6, "OnBuffActivate-style call is a no-op");
        match.Runtime.Spells.Buffs.Add(match.Hero, "W7Scale", BuffType.Internal, 5.0, match.Hero.NetId, new BuffPayload { ScaleSkinCoef = 0.1 },
            canMitigateDuration: false);
        TestAssert.Near(1.1, ModelScale.Of(match.Hero), 1e-6, "Olaf's Ragnarok is 1.1");
        match.Runtime.Spells.Buffs.Add(match.Hero, "W7ScaleDown", BuffType.Internal, 5.0, match.Hero.NetId, new BuffPayload { ScaleSkinCoef = -5.0 },
            canMitigateDuration: false);
        TestAssert.True(ModelScale.Of(match.Hero) > 0.0f, "the client never takes a coefficient of 0 or below (the model would vanish)");
    }

    [Check("protocol.w7-model-scale-and-targetable-to-team-layouts")]
    public static void TheReplicationPacketsCarryTheRegistryBits()
    {
        // The frame is the one the retail client's receive path reads (the receive routine,
        // docs/network/patch126-replication-tables.md "The 0xCC frame"): [0xCC][u32 0][u32 syncId]
        // [u8 count] then per unit [u8 setMaps][u32 netId] and, for each set bit of setMaps,
        // [u32 mask][one u32 per set mask bit]. The property mask therefore sits at offset 15,
        // behind the net id, not at 11: this check was written 19 October and compiled but never
        // run, and it read the mask one field early (offset 11 holds 0x40000001, the unit's net id).
        var hero = CombatPackets.UnitModelScale(0x40000001, 1.5f, isHero: true, syncId: 5).Plaintext.Span;
        TestAssert.Equal(23, hero.Length, "one float of a replication block is 23 bytes");
        TestAssert.Equal((byte)0xCC, hero[0]);
        TestAssert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(hero[^4..]), "the coefficient");
        TestAssert.Equal(0x08, hero[10], "the MAP registry");
        TestAssert.Equal(0x40000001u, BinaryPrimitives.ReadUInt32LittleEndian(hero[11..]), "[u8 setMaps][u32 netId]");
        TestAssert.Equal(0x80u, BinaryPrimitives.ReadUInt32LittleEndian(hero[15..]), "bit 7 of a hero (registered as mCrit)");
        var minion = CombatPackets.UnitModelScale(0x40000002, 1.5f, isHero: false).Plaintext.Span;
        TestAssert.Equal(0x08u, BinaryPrimitives.ReadUInt32LittleEndian(minion[15..]), "bit 3 of every other class");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => CombatPackets.UnitModelScale(0x40000002, 0.0f, false), "never 0");

        var flags = CombatPackets.UnitTargetableToTeam(0x40000003, notToAlly: false, notToEnemy: true, isHero: false).Plaintext.Span;
        TestAssert.Equal(0x02, flags[10], "the LOCAL1 registry");
        TestAssert.Equal(0x40000003u, BinaryPrimitives.ReadUInt32LittleEndian(flags[11..]), "[u8 setMaps][u32 netId]");
        TestAssert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(flags[15..]), "bit 8 of a minion");
        TestAssert.Equal(0x01000000u, BinaryPrimitives.ReadUInt32LittleEndian(flags[^4..]), "SPELL_NonTargetableEnemy");
        TestAssert.Equal(0x02000000u, BinaryPrimitives.ReadUInt32LittleEndian(
            CombatPackets.UnitTargetableToTeam(0x40000003, false, false, true).Plaintext.Span[^4..]), "neither team excluded is targetable to all");
    }

    [Check("scripting.bb-w7-not-targetable-to-team-reads-the-observers-side")]
    public static void AUnitNotTargetableToEnemiesIsTargetableToItsOwnTeam()
    {
        using var match = new BbM3.Match("Sivir");
        // Jarvan's Demacian Standard: ToAlly false, ToEnemy true. The foe stands in for the standard.
        Run(match, Probe("W7Targetable", Block("BBSetNotTargetableToTeam", "TargetVar", "Target", "ToAlly", false, "ToEnemy", true)), match.Foe);
        NoFailures(match);
        TestAssert.True(!match.Runtime.Combat.IsTargetable(match.Foe, match.Hero), "its enemies cannot target it");
        TestAssert.True(match.Runtime.Combat.IsTargetable(match.Foe, match.FarFoe), "its own team can");
        Run(match, Probe("W7TargetableAlly", Block("BBSetNotTargetableToTeam", "TargetVar", "Target", "ToAlly", true, "ToEnemy", false)), match.Foe);
        TestAssert.True(match.Runtime.Combat.IsTargetable(match.Foe, match.Hero), "Monkey King's decoy: the enemy can target it");
        TestAssert.True(!match.Runtime.Combat.IsTargetable(match.Foe, match.FarFoe), "and its own team cannot");
    }

    [Check("scripting.bb-w7-link-visibility-is-a-pair-the-world-keeps")]
    public static void TheLinkIsKeptUntilTheRemoval()
    {
        using var match = new BbM3.Match("Sivir");
        Run(match, Probe("W7Link", Block("BBLinkVisibility", "Unit1Var", "Owner", "Unit2Var", "Target")), match.FarFoe);
        NoFailures(match);
        var pair = match.Hero.NetId < match.FarFoe.NetId ? (match.Hero.NetId, match.FarFoe.NetId) : (match.FarFoe.NetId, match.Hero.NetId);
        TestAssert.True(match.Runtime.World.VisibilityLinks.Contains(pair), "the pair is linked, smaller id first");
        Run(match, Probe("W7Unlink", Block("BBRemoveLinkVisibility", "Unit1Var", "Target", "Unit2Var", "Owner")), match.FarFoe);
        TestAssert.True(!match.Runtime.World.VisibilityLinks.Contains(pair), "the removal takes it back whichever way the units are named");
    }

    [Check("scripting.bb-w7-force-refresh-path-plans-the-hero-order-again")]
    public static void AHeroWithAMoveOrderIsPlannedAgain()
    {
        using var match = new BbM3.Match("Sivir");
        match.Hero.Position = new Position2(7000, 7000);
        var route = match.Runtime.Movement.SetMoveOrder(match.Hero.NetId, new Position2(7600, 7000));
        TestAssert.True(route.Count > 1, "the hero walks");
        var before = match.Runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("force-refresh-path");
        Run(match, Probe("W7Refresh", Block("BBForceRefreshPath", "UnitVar", "Target")), match.Hero);
        NoFailures(match);
        TestAssert.Equal(before + 1, match.Runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("force-refresh-path"), "one new route was published");
        TestAssert.True(match.Hero.Waypoints.Count > 1, "the hero still walks");
        // A unit without a move order has nothing to plan again.
        Run(match, Probe("W7RefreshIdle", Block("BBForceRefreshPath", "UnitVar", "Target")), match.Ally);
        TestAssert.Equal(before + 1, match.Runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("force-refresh-path"), "an idle hero publishes nothing");
    }

    [Check("scripting.bb-w7-ignore-collision-keeps-the-ghost-only-for-small-units")]
    public static void OnlyAMarkerSizedUnitPassesMobileBodies()
    {
        using var match = new BbM3.Match("Sivir");
        var known = new HashSet<uint>();
        Run(match, Probe("W7Flags",
            Block("BBGetTeamID", "TargetVar", "Owner", "DestVar", "teamID"),
            Block("BBGetUnitPosition", "UnitVar", "Owner", "PositionVar", "Pos"),
            Spawn("TestCube"), Spawn("AniviaIceblock")));
        NoFailures(match);
        var cube = Spawned(match, "TestCube", known).Single();
        var block = Spawned(match, "AniviaIceblock", known).Single();
        // Batch 252 (restated from the buff list): batch 241 item 1 moved the spawn flags from a BBSpawnFlags buff to the unit's status bits
        // (Entity.ScriptStatus, BbBuffRuntime.SetSpawnStatuses); the marker-size ghost is SetGhosted there.
        TestAssert.True(cube.AnyBuffOrStatus(buff => buff.Payload.Ghosted), "a TestCube (radius 1) still passes mobile bodies");
        TestAssert.True(!block.AnyBuffOrStatus(buff => buff.Payload.Ghosted), "an ice block (radius 100) is solid");
    }
}
