using System.Buffers.Binary;
using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 6 (19 October 2026, W32b): what the thirteen sight champions (Ashe, Caitlyn, Cassiopeia, Galio, Gangplank,
/// Kog'Maw, Leona, Maokai, Nocturne, Skarner, Talon, Teemo, Veigar) need to see: the sight radius accessors (E04), a minion's
/// <c>VisibilitySize</c> and the <c>RevealSpecificUnitOnly</c> bubbles (E18). The probe scripts are hand-built block lists run on a production
/// <c>MatchRuntime</c> whose Sivir module hosts them; every parameter name is one the shipped scripts write. Written and compiled, not run
/// (owner rule).
/// </summary>
public static class BbW6VisionChecks
{
    private static BbScript Probe(string name, params BbBlock[] blocks) =>
        new(name, "Spells/" + name + ".luaobj", [], [new BbEventTable("TargetExecute", blocks)], []);

    private static void Run(BbM3.Match match, BbScript script, Entity? target = null)
    {
        var at = new Position3(0, 0, 0);
        match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, at, at, target ?? match.Foe);
    }

    private static void NoFailures(BbM3.Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    private sealed class OpenVision : IVisionEnvironment
    {
        public double CellSize => 400.0;
        public bool HasVisionLos(Position2 source, Position2 target) => true;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }

    /// <summary>A buff whose <c>BuffOnUpdateStats</c> holds the two sight accessors the way the shipped trap and zone buffs write them.</summary>
    private static BbScript BubbleStats(double percent, double flat) => new("W6BubbleStats", "Spells/W6BubbleStats.luaobj", [],
    [
        new BbEventTable("BuffOnUpdateStats",
        [
            BbTest.Block("BBIncStat", ["Stat", BbTest.Ref("IncPercentBubbleRadiusMod"), "TargetVar", "Owner", "Delta", percent]),
            BbTest.Block("BBIncStat", ["Stat", BbTest.Ref("IncFlatBubbleRadiusMod"), "TargetVar", "Owner", "Delta", flat]),
        ]),
    ], []);

    /// <summary>Installs a second Sivir module whose closure holds <paramref name="probe"/> (the rig of the event hook checks) and gives <paramref name="holder"/> the buff.</summary>
    private static BbBuffInstance InstallBuff(BbM3.Match match, BbScript probe, Entity holder)
    {
        var real = new BbScriptResolver(GameData.Default.Paths).Resolve("Sivir");
        var scripts = new List<BbClosureScript> { new(probe.Name, probe.SourcePath, BbScriptRole.Referenced, "BbW6VisionChecks", probe) };
        scripts.AddRange(real.Scripts);
        var closure = new BbClosure(real.Champion, scripts, real.Dangling, real.DataOnly, real.Diagnostics);
        var module = new BbChampionModule("Sivir");
        typeof(BbChampionModule).GetField("_scripts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, new BbScriptSet(closure));
        var dispatcher = (ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher;
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modules = ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).ToArray();
        // By name: a check that installs a second probe finds the first probe's module in the slot, not the production one.
        var index = Array.FindIndex(modules, existing => existing.ChampionName == "Sivir");
        TestAssert.True(index >= 0, "production registers the script-driven Sivir");
        modules[index] = module;
        field.SetValue(dispatcher, modules);
        ((MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(match.Runtime.Spells);
        var state = module.Buffs!.Add(new BbBuffRequest(holder, match.Hero, probe.Name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 25000.0, 0.0, false, new BbTable()));
        return module.Buffs.InstanceOf(state ?? throw new InvalidOperationException("the probe buff was not added"))
               ?? throw new InvalidOperationException("the probe buff has no script instance");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E04: the sight radius accessors
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-w6-bubble-radius-accessors-scale-the-sight-radius")]
    public static void TheSightAccessorsFollowTheClientsRadiusFormula()
    {
        using var match = new BbM3.Match("Sivir");
        var foe = match.Foe;
        TestAssert.Near(1350.0, VisibilityRules.TerrainVisionRange(foe), 1e-9, "a hero sees 1350 by default (CharacterData PerceptionBubbleRadius)");

        // BantamTrap / CaitlynYordleTrap / LayWaste write IncPercentBubbleRadiusMod from their stats hook; Maokai's sapling adds IncFlatBubbleRadiusMod.
        // The client's radius is (percent + 1) * base + flat.
        var buff = InstallBuff(match, BubbleStats(-0.7, 100.0), foe);
        TestAssert.True(buff.State.Payload.Has(Amberline.Core.Buffs.BuffPayloadField.BubbleRadiusPercent), "the percent accessor reached the payload");
        TestAssert.Near(-0.7, buff.State.Payload.BubbleRadiusPercent, 1e-6, "IncPercentBubbleRadiusMod -0.7");
        TestAssert.Near(100.0, buff.State.Payload.BubbleRadiusFlat, 1e-6, "IncFlatBubbleRadiusMod +100");
        var (flat, percent) = VisibilityRules.BubbleRadiusMods(foe);
        TestAssert.Near(100.0, flat, 1e-6, "the unit's flat modifier is the sum of its buffs'");
        TestAssert.Near(-0.7, percent, 1e-6, "and its percent modifier");
        TestAssert.Near(1350.0 * 0.3 + 100.0, VisibilityRules.TerrainVisionRange(foe), 1e-3, "(percent + 1) * base + flat");

        // IncPermanent*: one sum on the unit, kept until a script subtracts it (Ashe's Hawkshot -0.9, Nocturne's Paranoia SightReduction).
        Run(match, Probe("W6Permanent",
            BbTest.Block("BBIncPermanentStat", ["Stat", BbTest.Ref("IncPermanentFlatBubbleRadiusMod"), "TargetVar", "Target", "Delta", -300.0]),
            BbTest.Block("BBIncPermanentStat", ["Stat", BbTest.Ref("IncPermanentPercentBubbleRadiusMod"), "TargetVar", "Target", "Delta", -0.1])), foe);
        NoFailures(match);
        (flat, percent) = VisibilityRules.BubbleRadiusMods(foe);
        TestAssert.Near(-200.0, flat, 1e-6, "the permanent flat sum joins the buffs'");
        TestAssert.Near(-0.8, percent, 1e-6, "and the permanent percent sum");
        TestAssert.Near(1350.0 * 0.2 - 200.0, VisibilityRules.TerrainVisionRange(foe), 1e-3, "both apply");

        // The buff's share goes with the buff; the permanent share stays.
        match.Runtime.Spells.Buffs.Remove(foe, buff.State.Slot);
        (flat, percent) = VisibilityRules.BubbleRadiusMods(foe);
        TestAssert.Near(-300.0, flat, 1e-6, "only the permanent flat is left");
        TestAssert.Near(-0.1, percent, 1e-6, "and the permanent percent");
        TestAssert.Near(1350.0 * 0.9 - 300.0, VisibilityRules.TerrainVisionRange(foe), 1e-3, "the radius follows");

        // A radius never goes below zero (Bushwhack's trap: -1 is blind).
        Run(match, Probe("W6Blind",
            BbTest.Block("BBIncPermanentStat", ["Stat", BbTest.Ref("IncPermanentPercentBubbleRadiusMod"), "TargetVar", "Target", "Delta", -2.0])), foe);
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(foe), 1e-9, "a radius is at least zero");

        // BBGetStat reads both sums back.
        Run(match, Probe("W6Read",
            BbTest.Block("BBGetStat", ["Stat", BbTest.Ref("GetFlatBubbleRadiusMod"), "TargetVar", "Target", "DestVar", "ReadFlat", "DestVarTable", "CharVars"]),
            BbTest.Block("BBGetStat", ["Stat", BbTest.Ref("GetPercentBubbleRadiusMod"), "TargetVar", "Target", "DestVar", "ReadPercent", "DestVarTable", "CharVars"])), foe);
        NoFailures(match);
        TestAssert.Near(-300.0, (double)match.Module.CharVars(match.Hero)["ReadFlat"]!, 1e-6, "GetFlatBubbleRadiusMod");
        TestAssert.Near(-2.1, (double)match.Module.CharVars(match.Hero)["ReadPercent"]!, 1e-6, "GetPercentBubbleRadiusMod");
    }

    [Check("scripting.bb-w6-bubble-radius-modifiers-reach-the-allied-client")]
    public static void TheModifiersAreReplicatedToTheOwnTeamsClients()
    {
        using var match = new BbM3.Match("Sivir");
        var ally = match.Ally;
        var wire = BbM3.Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        wire.Packets.Clear();

        // Default modifiers send nothing: the client's fields start at zero.
        match.Advance(0.2);
        TestAssert.False(wire.Packets.Any(item => IsBubbleMods(item.Packet.Plaintext.Span, ally.NetId)), "zero modifiers are not sent");

        var buff = InstallBuff(match, BubbleStats(-0.5, 20.0), ally);
        match.Advance(0.2);
        var sent = wire.Packets.Select(item => item.Packet.Plaintext).Where(bytes => IsBubbleMods(bytes.Span, ally.NetId)).ToArray();
        TestAssert.Equal(1, sent.Length, "one block carries the pair");
        var block = sent[0].Span;
        TestAssert.Equal((byte)0x08, block[10], "MAP registry");
        TestAssert.Equal(0x30u, BinaryPrimitives.ReadUInt32LittleEndian(block[15..]), "a hero: bits 4 and 5 (mFlatBubbleRadiusMod, mPercentBubbleRadiusMod)");
        TestAssert.Near(20.0, BinaryPrimitives.ReadSingleLittleEndian(block[19..]), 1e-6, "flat");
        TestAssert.Near(-0.5, BinaryPrimitives.ReadSingleLittleEndian(block[23..]), 1e-6, "percent");

        // An unchanged pair is not repeated; the end of the buff sends the zero pair.
        wire.Packets.Clear();
        match.Advance(0.2);
        TestAssert.False(wire.Packets.Any(item => IsBubbleMods(item.Packet.Plaintext.Span, ally.NetId)), "an unchanged pair is not repeated");
        match.Runtime.Spells.Buffs.Remove(ally, buff.State.Slot);
        match.Advance(0.2);
        var cleared = wire.Packets.Select(item => item.Packet.Plaintext).Where(bytes => IsBubbleMods(bytes.Span, ally.NetId)).ToArray();
        TestAssert.Equal(1, cleared.Length, "the end of the modifier is sent");
        TestAssert.Near(0.0, BinaryPrimitives.ReadSingleLittleEndian(cleared[0].Span[19..]), 1e-6, "flat back to zero");
        TestAssert.Near(0.0, BinaryPrimitives.ReadSingleLittleEndian(cleared[0].Span[23..]), 1e-6, "percent back to zero");

        // Enemy units' modifiers matter to nobody on this client: it draws only its own team's bubbles.
        wire.Packets.Clear();
        InstallBuff(match, BubbleStats(-1.0, 0.0), match.Foe);
        match.Advance(0.2);
        TestAssert.False(wire.Packets.Any(item => IsBubbleMods(item.Packet.Plaintext.Span, match.Foe.NetId)), "an enemy's modifiers are not sent");
    }

    private static bool IsBubbleMods(ReadOnlySpan<byte> bytes, uint netId) =>
        bytes.Length == 27 && bytes[0] == 0xCC && bytes[10] == 0x08 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]) == netId &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]) is 0x30u or 0x03u;

    // ------------------------------------------------------------------------------------------------------------
    // E18: unit-borne vision
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock SpawnCube(double visibility, bool placemarker, string skin = "TestCube") =>
        BbTest.Block("BBSpawnMinion", ["Name", "BloodDrop", "Skin", skin, "AiScript", "idle.lua", "PosVar", "P", "Team", BbTest.Ref("TEAM_CASTER"),
            "Rooted", true, "Invulnerable", true, "IgnoreCollision", true, "Placemarker", placemarker, "VisibilitySize", visibility, "DestVar", "U"]);

    // Batch 473 (owner decision, 29 October 2026: "Yes match the client"): the minion create hook
    // makes the CharacterData bubble only when the unit's ServerOnly check (bit 0x20 of
    // CharacterData byte 0x61, ServerOnly, whose default is NeverRender) is false. TestCube is ServerOnly (NeverRender 1, no ServerOnly key);
    // TestCubeRender sets ServerOnly 0.
    [Check("scripting.bb-serveronly-spawn-has-no-character-bubble")]
    public static void AServerOnlySpawnSeesOnlyItsVisibilitySize()
    {
        foreach (var skin in new[] { "TestCube", "SpellBook1", "XerathArcaneBarrageLauncher", "OdinSpeedShrine", "OdinMinionSpawnPortal", "OdinQuestBuff" })
            TestAssert.True(GameData.Default.Champion(skin).ServerOnly, $"{skin} is ServerOnly (its NeverRender)");
        foreach (var skin in new[] { "TestCubeRender", "TestCubeRender10Vision", "OdinTestCubeRender" })
            TestAssert.False(GameData.Default.Champion(skin).ServerOnly, $"{skin} sets ServerOnly 0");
        using var match = new BbM3.Match("Sivir");
        var world = match.Runtime.World;
        var before = world.Entities.Keys.ToHashSet();
        var prelude = BbTest.Block("BBGetUnitPosition", ["UnitVar", "Owner", "PositionVar", "P"]);
        Run(match, Probe("B473Cubes", prelude, SpawnCube(0.0, false), SpawnCube(450.0, false), SpawnCube(0.0, false, "TestCubeRender")));
        NoFailures(match);
        var cubes = world.Entities.Values.Where(unit => !before.Contains(unit.NetId)).OrderBy(unit => unit.NetId).ToArray();
        TestAssert.Equal(3, cubes.Length, "three cubes were spawned");
        var (bare, sized, render) = (cubes[0], cubes[1], cubes[2]);
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(bare), 1e-9, "a ServerOnly cube with no size sees nothing");
        TestAssert.Near(450.0, VisibilityRules.TerrainVisionRange(sized), 1e-9, "a ServerOnly cube with a size sees that size");
        TestAssert.Near(450.0, VisibilityRules.VisibilitySizeRange(sized), 1e-9, "the size stays its own bubble");
        var renderBubble = GameData.Default.Champion("TestCubeRender").PerceptionBubbleRadius;
        TestAssert.True(renderBubble > 0.0, "TestCubeRender has a CharacterData bubble");
        TestAssert.Near(renderBubble, VisibilityRules.TerrainVisionRange(render), 1e-9, "a non-ServerOnly cube keeps its CharacterData bubble");

        // The cubes stand on Sivir (PosVar is her position), so the team's providers tell them apart by radius only.
        var radii = VisibilityRules.BuildTeamProviders(world, match.Hero.Team, [], _ => [])
            .Where(provider => provider.Position == bare.Position).Select(provider => provider.Radius).ToArray();
        TestAssert.Equal(1, radii.Count(radius => radius == 450.0), "the sized cube is a 450 team provider");
        TestAssert.Equal(renderBubble == 1350.0 ? 2 : 1, radii.Count(radius => radius == 1350.0),
            "Sivir's 1350 (and the render cube's bubble); the ServerOnly cubes add no 1350");
    }

    [Check("scripting.bb-w6-visibility-size-makes-a-vision-source")]
    public static void AUnitSpawnedWithAVisibilitySizeSeesThatFar()
    {
        using var match = new BbM3.Match("Sivir");
        var world = match.Runtime.World;
        var before = world.Entities.Keys.ToHashSet();
        var prelude = BbTest.Block("BBGetUnitPosition", ["UnitVar", "Owner", "PositionVar", "P"]);
        // Talon's blood drop: a TestCube of the caster's team, rooted, invulnerable, VisibilitySize 450 (TalonBleedDebuff.BuffOnUpdateActions).
        Run(match, Probe("W6Cubes", prelude, SpawnCube(450.0, false), SpawnCube(0.0, false), SpawnCube(100.0, true)));
        NoFailures(match);
        var cubes = world.Entities.Values.Where(unit => !before.Contains(unit.NetId)).OrderBy(unit => unit.NetId).ToArray();
        TestAssert.Equal(3, cubes.Length, "three cubes were spawned");
        var drop = cubes[0];
        TestAssert.Near(450.0, drop.VisibilitySize, 1e-9, "the size is kept on the unit");
        TestAssert.Equal(match.Hero.Team, drop.Team, "TEAM_CASTER");
        // Batch 473: TestCube is ServerOnly, so the minion create hook makes no CharacterData bubble
        // for it and the cube sees only its size, the second bubble (VisibilitySizeRange). Batches 306/310 asserted 1350 for all three
        // (the 1350 default read without the ServerOnly test); before them this asserted 450, 0 and 100.
        TestAssert.Near(450.0, VisibilityRules.TerrainVisionRange(drop), 1e-9, "it sees its size of 450 for its team");
        TestAssert.Near(450.0, VisibilityRules.VisibilitySizeRange(drop), 1e-9, "and its size is the second bubble");
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(cubes[1]), 1e-9, "a ServerOnly cube with no size sees nothing");
        TestAssert.Near(100.0, VisibilityRules.TerrainVisionRange(cubes[2]), 1e-9, "a placemarker sees its size (Galio's gust)");
        TestAssert.Near(100.0, VisibilityRules.VisibilitySizeRange(cubes[2]), 1e-9, "with its size of 100");
        // Batch 202: a placemarker is announced like any unit (Entity.Announced), so its size reaches the clients with its spawn packet.
        TestAssert.True(cubes[2].Announced, "and announced like any unit");

        // The server's team sight uses it: an enemy 400 away from the cube and out of every other provider is seen.
        var near = BbM3.Enemy(match, "Spy", new Position2(drop.Position.X + 400.0, drop.Position.Z), UnitType.Minion);
        var far = BbM3.Enemy(match, "Out", new Position2(drop.Position.X + 500.0, drop.Position.Z), UnitType.Minion);
        var providers = VisibilityRules.BuildTeamProviders(world, match.Hero.Team, [], _ => []);
        var cubeProvider = providers.Where(provider => provider.Position == drop.Position && provider.Radius == 450.0).ToArray();
        TestAssert.True(cubeProvider.Length >= 1, "the cube is a team provider of radius 450");
        TestAssert.True(VisibilityRules.ProvidersReveal(new OpenVision(), cubeProvider, near.Position), "400 away is inside it");
        TestAssert.False(VisibilityRules.ProvidersReveal(new OpenVision(), cubeProvider, far.Position), "500 away is outside it");

        // It dies with the unit, and the spawn packet carries the size (SpawnMinion.visibilitySize, +34).
        var spawn = SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(drop.NetId, 0x40, drop.Position, 100, "BloodDrop", "TestCube", false, true, 0, 450.0f), 0.0);
        TestAssert.Near(450.0, BinaryPrimitives.ReadSingleLittleEndian(spawn.Plaintext.Span[34..]), 1e-6, "the spawn packet's visibilitySize");
        drop.Alive = false;
        TestAssert.Near(0.0, VisibilityRules.TerrainVisionRange(drop), 1e-9, "a dead cube sees nothing");

        // The slot validation no longer refuses it.
        TestAssert.True(BbNativeHandlers.SpawnRefusal(SpawnCube(450.0, false)) is null, "VisibilitySize is accepted");
    }

    private static BbBlock RevealBubble(string revealOnly, bool stealth, string id) =>
        BbTest.Block("BBAddUnitPerceptionBubble", ["TeamVar", "TeamID", "Radius", 10.0, "TargetVar", "Target", "Duration", 2.0,
            "SpecificUnitsClientOnlyVar", "Nothing", "RevealSpecificUnitOnlyVar", revealOnly, "RevealSteath", stealth,
            "BubbleIDVar", id, "BubbleIDVarTable", "CharVars"]);

    [Check("scripting.bb-w6-reveal-specific-unit-bubble-shows-that-unit-and-nothing-else")]
    public static void ARevealSpecificUnitBubbleRevealsOnlyItsUnit()
    {
        using var match = new BbM3.Match("Sivir");
        var world = match.Runtime.World;
        var foe = match.Foe;
        var other = match.FarFoe;
        other.Position = new Position2(foe.Position.X + 5.0, foe.Position.Z);
        // Skarner's Impale on its victim: a 10 unit bubble on the victim, stealth revealed, for the impaler's team.
        Run(match, Probe("W6Reveal", BbTest.Block("BBGetTeamID", ["TargetVar", "Owner", "DestVar", "TeamID"]), RevealBubble("Target", true, "BubbleID")), foe);
        NoFailures(match);
        var bubble = world.TimedVisionBubbles.Single(item => item.RevealUnitNetId == foe.NetId);
        TestAssert.Equal(match.Hero.Team, bubble.Team, "the bubble belongs to the team the script named");
        TestAssert.Equal(foe.NetId, bubble.AnchorNetId, "and follows its Target");
        TestAssert.True(bubble.RevealStealth && bubble.Radius == 10.0, "RevealSteath, 10 units");

        var order = match.Hero.Team;
        var seen = VisibilityRules.ComputeRawVisibility(world, order, new OpenVision(), Array.Empty<VisionProvider>());
        TestAssert.True(seen.Contains(foe.NetId), "the named unit is revealed although no provider covers it");
        TestAssert.False(seen.Contains(other.NetId), "a unit 5 units beside it is not: the bubble reveals the named unit only");
        TestAssert.True(VisibilityRules.SpecificReveal(world, order, foe) is not null, "the rule finds the bubble");
        TestAssert.True(VisibilityRules.SpecificReveal(world, Team.Chaos, foe) is null, "and only for its team");

        // Stealth: revealed only if the bubble reveals stealth.
        foe.StealthHidden = true;
        TestAssert.True(VisibilityRules.ComputeRawVisibility(world, order, new OpenVision(), Array.Empty<VisionProvider>()).Contains(foe.NetId),
            "RevealSteath shows a stealthed victim");
        match.Runtime.World.TryRemoveTimedVisionBubble(bubble.BubbleId, out _);
        Run(match, Probe("W6RevealNoStealth", BbTest.Block("BBGetTeamID", ["TargetVar", "Owner", "DestVar", "TeamID"]), RevealBubble("Target", false, "BubbleID2")), foe);
        TestAssert.False(VisibilityRules.ComputeRawVisibility(world, order, new OpenVision(), Array.Empty<VisionProvider>()).Contains(foe.NetId),
            "without it a stealthed victim stays hidden");
        foe.StealthHidden = false;

        // The ground is not revealed: the item sight service skips such a bubble.
        TestAssert.False(match.Runtime.Delivery.AdditionalProviders(order).Any(provider => provider.Radius == 10.0), "no sight of the ground");

        // The bubble ends with its life.
        match.Advance(2.5);
        TestAssert.False(world.TimedVisionBubbles.Any(item => item.RevealUnitNetId == foe.NetId), "it aged out");
        TestAssert.True(VisibilityRules.SpecificReveal(world, order, foe) is null, "and reveals nothing afterwards");
    }

    [Check("scripting.bb-w6-reveal-specific-unit-bubble-packet-names-its-unit")]
    public static void TheBubblePacketsCarryTheRevealedUnit()
    {
        var unit = SpawnPackets.AddUnitPerceptionBubble(0x40000001u, 10.0f, 2.0f, 77u, 0u, 0u, 5u, 0x40000002u).Plaintext.Span;
        TestAssert.Equal(41, unit.Length, "0x26 is 41 bytes");
        TestAssert.Equal(0x40000002u, BinaryPrimitives.ReadUInt32LittleEndian(unit[33..]), "revealSpecificNetworkID follows the flag bits at +29");
        var ground = SpawnPackets.AddPerceptionBubble(new PerceptionBubble(0u, new Position3(1, 2, 3), 10.0f, 2.0f, 78u, 0u, 0u, 0x40000003u)).Plaintext.Span;
        TestAssert.Equal(49, ground.Length, "0x7B is 49 bytes");
        TestAssert.Equal(0x40000003u, BinaryPrimitives.ReadUInt32LittleEndian(ground[41..]), "and so does the ground variant at +41");
    }
}
