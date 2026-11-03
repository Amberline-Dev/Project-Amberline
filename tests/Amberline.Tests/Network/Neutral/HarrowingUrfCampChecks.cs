using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
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

    // Batch 667 (owner, 3 November 2026: "URF attacking warwick with skin is bugged, URF invisible, hit sound plays, no damage is dealt to
    // warwick"). CharScriptUrf's scan (pc 0034-0096): a unit with EternalThirstIcon whose BBGetSkinID is 2 (warwick_Urf.skn, Urf the Manatee)
    // gets UrfSpell cast at it (slot 0, Urf.inibin Data*Spell1) and the ghost gets HalloweenUrfWarwick (4.5 s). That buff shows the ghost
    // from its BuffOnUpdateStats (SetNoRender false, pc 0250-0260) and hides him in OnBuffDeactivate (SetNoRender true, pc 0182-0191).
    // UrfSpell's TargetExecuteBuildingBlocks is an empty table (UrfSpell.luaobj pc 0022-0023): the slap deals no damage (script = retail).
    [Check("neutral.harrowing-urf-shows-itself-and-slaps-urf-skin-warwick")]
    public static void UrfShowsItselfAndSlapsUrfSkinWarwick()
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, new GameData(paths), readySettleSeconds: 0.0,
            mapId: GameMaps.Harrowing,
            roster: [new(1, 0, "Manatee", "Warwick", Team.Order, SkinId: 2), new(2, 1, "Wolf", "Warwick", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        foreach (var id in new[] { 1L, 2L })
        {
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the map 2 match starts");
        var manatee = runtime.Session.Participants[0].Hero!;
        var wolf = runtime.Session.Participants[1].Hero!;
        manatee.HeroAutoAcquire = wolf.HeroAutoAcquire = false;
        TestAssert.Equal(2, manatee.SkinId, "the Order Warwick wears skin 2 (Urf the Manatee)");
        TestAssert.True(manatee.Buffs.Any(buff => buff.Name == "EternalThirstIcon"), "Warwick's character script adds EternalThirstIcon");
        var capture = new UrfCapture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, runtime.Session.Participants[0], capture));
        for (var t = 0.0; t < 99.0; t += 0.25) runtime.Tick(0.25);
        // The Order Warwick waits by the grave (CampSpawnPoints[13]) when the camp's first spawn comes at 100 s, on its south side: 200 units
        // east is a wall cell with no line of sight to the grave.
        manatee.Position = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z - 200.0);
        TestAssert.True(grid.IsMovementWalkable(manatee.Position) &&
                        grid.HasVisionLos(manatee.Position, new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z)), "a walkable spot that sees the grave");
        var health = manatee.Health;
        Entity? urf = null;
        var shown = false;
        for (var t = 0.0; t < 6.0; t += 0.25)
        {
            runtime.Tick(0.25);
            urf ??= runtime.World.Entities.Values.SingleOrDefault(unit => unit.Kind == EntityKind.Neutral && unit.Name == "Urf" && unit.Alive);
            shown |= urf is not null && urf.Buffs.Any(buff => buff.Name == "HalloweenUrfWarwick") &&
                     (CharacterState.ActionWord(urf) & CharacterState.NoRenderBit) == 0;
        }
        TestAssert.True(urf is not null, "the Urf camp spawns at 100 s");
        var urfId = BitConverter.GetBytes(urf!.NetId);
        var wire = capture.Packets.Where(packet => packet.Plaintext.Span.IndexOf(urfId) >= 0).Select(packet => packet.Plaintext.Span[0]).ToArray();
        var opcodes = string.Join(",", wire.Select(op => op.ToString("X2")));
        TestAssert.True(urf.Buffs.Any(buff => buff.Name == "HalloweenUrfWarwick"),
            $"Urf-skin Warwick within 300 takes the Warwick branch; buffs [{string.Join(",", urf.Buffs.Select(b => b.Name))}]");
        TestAssert.False(urf.Buffs.Any(buff => buff.Name == "HalloweenUrfAppear"), "not the appear branch");
        TestAssert.True(shown, $"HalloweenUrfWarwick's stats hook shows the ghost (SetNoRender false); Urf packets [{opcodes}]");
        // What the owner's client was sent: the ghost enters its sight (0xC2), the buff's eight ghostUrf.troy glows (0x8C) and UrfSpell's
        // cast (0x41, the "hit sound"; a unit's CastType 0 child, BbUnitCharacterModule.CastChild).
        TestAssert.True(wire.Contains((byte)0xC2) && wire.Count(op => op == 0x8C) == 8 && wire.Contains((byte)0x41),
            $"the Order client sees the ghost, his glows and the slap; Urf packets [{opcodes}]");
        TestAssert.Near(health, manatee.Health, 1e-9, "UrfSpell's empty TargetExecute deals no damage (script = retail)");
    }

    // Batch 672 (owner, 3 November 2026: the Ghost of Urf "spins in place, continuously"). The client's 0x66 handler (the client case 0x66,
    // -) takes the spawn facing as normalize(face point (+0x22) - position (+0x0a)) in 3D, falls back to (0, 0, 1) only when the
    // two points are exactly equal, and stores it as the unit's target facing. The
    // per-frame turn yaws a standing unit toward that target and never reaches a vertical one, so a face point straight above or
    // below the position spins the unit for ever; the 0xC2 stop (SetStoppedPosition) sets only the matrix, not the target.
    // Camp 13's script puts its spawn and face point on the same Make3DPoint(2532.0, 44.4, 9940.0); batch 588 grounded the spawn Y only.
    [Check("neutral.camp-spawn-facing-is-never-vertical")]
    public static void CampSpawnFacingIsNeverVertical()
    {
        var failures = new List<string>();
        foreach (var map in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.TwistedTreeline })
        {
            var paths = GameDataPaths.Default.ForMap(map);
            var jungle = new JungleScheduler(new WorldState(), new GameData(paths), navigation: NavigationGrid.Load(paths));
            jungle.Start();
            jungle.Tick(1000.0);
            TestAssert.True(jungle.Members.Count > 0, $"map {map} spawns its camps");
            foreach (var member in jungle.Members.Values)
            {
                // The wire's float32 points, as the client subtracts them.
                var dx = (float)member.FacePosition.X - (float)member.Home.X;
                var dy = (float)member.FacePosition.Y - (float)member.Home.Y;
                var dz = (float)member.FacePosition.Z - (float)member.Home.Z;
                if ((dx != 0f || dy != 0f || dz != 0f) && dx == 0f && dz == 0f)
                    failures.Add($"map {map} {member.Name} camp {member.CampId}: face - home = (0, {dy}, 0)");
            }
        }
        TestAssert.True(failures.Count == 0, $"a camp's 0x66 face point is straight above or below it: {string.Join("; ", failures)}");
    }

    // Batch 672 reproduction (brief step 2): everything a map 2 match sends the Order client for the ghost's net id over his first 30 s, with a
    // default-skin Warwick (the appear branch) and an Urf-skin Warwick (the slap branch) by the grave.
    [Check("neutral.harrowing-urf-stands-facing-a-horizontal-direction")]
    public static void UrfStandsFacingAHorizontalDirection()
    {
        foreach (var skin in new[] { 0, 2 })
        {
            var (urf, packets) = RunUrfWindow(skin, 30.0);
            var opcodes = string.Join(",", packets.Select(packet => packet.Span[0].ToString("X2")));
            var create = packets.Where(packet => packet.Span[0] == 0x66).ToArray();
            TestAssert.Equal(1, create.Length, $"skin {skin}: one creation; Urf packets [{opcodes}]");
            var at = create[0];
            float F(int offset) => BitConverter.ToSingle(at.Span.Slice(offset, 4));
            var (dx, dy, dz) = (F(0x22) - F(0x0a), F(0x26) - F(0x0e), F(0x2a) - F(0x12));
            TestAssert.True((dx == 0f && dy == 0f && dz == 0f) || dx != 0f || dz != 0f,
                $"skin {skin}: the 0x66 spawn facing face - position = ({dx}, {dy}, {dz}) is not vertical (the client spins toward a vertical one)");
            // Nothing else turns him: no FaceDirection (0x53) and no route (0xC1); each sight entry (0xC2) is a stop with a unit forward.
            TestAssert.False(packets.Any(packet => packet.Span[0] is 0x53 or 0xC1), $"skin {skin}: no 0x53 or 0xC1; Urf packets [{opcodes}]");
            foreach (var reveal in packets.Where(packet => packet.Span[0] == 0xC2))
            {
                var span = reveal.Span;
                TestAssert.Equal(VisionPackets.MovementDataStop, span[VisionPackets.EnterVisibilityMovementTypeOffset], $"skin {skin}: the 0xC2 is a stop");
                var fx = BitConverter.ToSingle(span.Slice(20, 4));
                var fz = BitConverter.ToSingle(span.Slice(24, 4));
                TestAssert.Near(1.0, Math.Sqrt(fx * fx + fz * fz), 1e-6, $"skin {skin}: the 0xC2 stop forward ({fx}, {fz}) is a unit vector");
            }
            TestAssert.True(urf.Position == new Position2(urf.NeutralHome!.Value.X, urf.NeutralHome.Value.Z), $"skin {skin}: the ghost stays on his grave");
        }
    }

    private static (Entity Urf, List<ReadOnlyMemory<byte>> Packets) RunUrfWindow(int skinId, double seconds)
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, new GameData(paths), readySettleSeconds: 0.0,
            mapId: GameMaps.Harrowing,
            roster: [new(1, 0, "Grave", "Warwick", Team.Order, SkinId: skinId), new(2, 1, "Wolf", "Warwick", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        foreach (var id in new[] { 1L, 2L })
        {
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        runtime.Session.RunSchedulers();
        var hero = runtime.Session.Participants[0].Hero!;
        hero.HeroAutoAcquire = runtime.Session.Participants[1].Hero!.HeroAutoAcquire = false;
        var capture = new UrfCapture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, runtime.Session.Participants[0], capture));
        for (var t = 0.0; t < 99.0; t += 0.25) runtime.Tick(0.25);
        hero.Position = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z - 200.0);
        Entity? urf = null;
        for (var t = 0.0; t < 1.0 + seconds; t += 0.25)
        {
            runtime.Tick(0.25);
            urf ??= runtime.World.Entities.Values.SingleOrDefault(unit => unit.Kind == EntityKind.Neutral && unit.Name == "Urf" && unit.Alive);
        }
        TestAssert.True(urf is not null, "the Urf camp spawns at 100 s");
        var urfId = BitConverter.GetBytes(urf!.NetId);
        return (urf, capture.Packets.Where(packet => packet.Plaintext.Span.IndexOf(urfId) >= 0)
            .Select(packet => (ReadOnlyMemory<byte>)packet.Plaintext.ToArray()).ToList());
    }

    private sealed class UrfCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
