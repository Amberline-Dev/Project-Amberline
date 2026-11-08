using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Batch 458: Dominion's base on a map 8 production match. The opening ceremony (OdinScriptBehaviorTrees.xml:4624) holds every champion
/// within 1200 of its team's start point until the game time passes 45, the opening barriers stand over both bases for that time, and
/// Recall lands on the team's spawn point (the client's LocationsManager SPAWN_LOCATION).
/// </summary>
public static class DominionBaseChecks
{
    private const double Step = 1.0 / 30.0;
    private const double HoldRadius = 1200.0;
    // Past the Order base's south-east exit, outside the hold (map 8 grid: walkable).
    private static readonly Position2 OutsideOrderBase = new(1800.0, 3250.0);
    // Batch 474: the base plateau's grid height is about -35 (VisionPackets.cs); the exit ramps descend below -40.
    private const double PlatformFloor = -40.0;
    // Batch 474 follow-up: inside the 1200 circle, on the south-east ramp (map 8 grid height -112).
    private static readonly Position2 OnRampInsideHold = new(1450.0, 3450.0);

    [Check("dominion.opening-ceremony-holds-champions-in-base-until-45-seconds")]
    public static void OpeningCeremonyHoldsChampionsInTheirBase()
    {
        WithMatch(runtime =>
        {
            var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
            var spawn = MapDefinitions.TeamSpawn(Team.Order);
            Advance(runtime, 1.0);
            Move(runtime, 1, hero, OutsideOrderBase);
            Advance(runtime, 10.0);
            var held = Distance(hero.Position, spawn);
            TestAssert.True(held <= HoldRadius, $"the champion stays within {HoldRadius} of its start point during the setup ({held:0.0})");
            // Batch 474: the hold stops the champion at the base platform's edge, the top of the south-east ramp (about 820 from the start
            // point on map 8), not at the 1200 circle, which reaches past the ramp onto the void.
            // Batch 504/514: the visible forcefield (HoldFootprint, about 434 from the barrier) stops the champion before the platform's edge;
            // the walk still goes out from the spawn, and the platform's own edge is checked by the checks below.
            TestAssert.True(held > 150.0, $"the champion walks to the edge of the hold ({held:0.0})");
            Advance(runtime, Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds - runtime.Combat.CurrentTime + 0.5);
            Move(runtime, 1, hero, OutsideOrderBase);
            Advance(runtime, 8.0);
            TestAssert.True(Distance(hero.Position, spawn) > HoldRadius,
                $"the champion leaves its base after the setup ({Distance(hero.Position, spawn):0.0})");
        });
    }

    /// <summary>
    /// Batch 474: the hold keeps the champion on its base platform (grid height at or above the base floor), not only inside the 1200 circle.
    /// The south-east exit's walkable ramp (map 8 grid, heights -37 down to -165) is the slab path that has not formed yet.
    /// </summary>
    [Check("dominion.opening-hold-keeps-champion-on-the-base-platform")]
    public static void OpeningHoldKeepsChampionOnThePlatform()
    {
        var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        WithMatch(runtime =>
        {
            var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
            Advance(runtime, 1.0);
            Move(runtime, 1, hero, OutsideOrderBase);
            Advance(runtime, 10.0);
            var height = grid.HeightAt(hero.Position);
            TestAssert.True(height >= PlatformFloor,
                $"the champion stays on the base platform during the setup; it stands on the ramp at ({hero.Position.X:0}, {hero.Position.Z:0}), height {height:0}");
        });
    }

    /// <summary>
    /// Batch 474 follow-up: a dash or a blink (Flash, Recall's channel) of a held champion ends on the base platform, not on the ramp
    /// beyond it (<c>MovementSystem.HeldInside</c>). The request lies inside the 1200 circle, so only the platform cut stops it.
    /// </summary>
    [Check("dominion.opening-hold-cuts-a-dash-at-the-platform-edge")]
    public static void OpeningHoldCutsADashAtThePlatformEdge()
    {
        var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        WithMatch(runtime =>
        {
            var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
            Advance(runtime, 1.0);
            var requested = OnRampInsideHold;
            TestAssert.True(Distance(requested, MapDefinitions.TeamSpawn(Team.Order)) < HoldRadius,
                "the dash request lies inside the 1200 circle, so the circle alone would allow it");
            TestAssert.True(grid.HeightAt(requested) < PlatformFloor, "the dash request lies on the ramp");
            var end = runtime.Movement.HeldInside(hero, requested);
            var height = grid.HeightAt(end);
            TestAssert.True(height >= PlatformFloor,
                $"a held dash ends on the base platform, not on the ramp (ends at ({end.X:0}, {end.Z:0}), height {height:0})");
            TestAssert.True(Distance(end, hero.Position) > 100.0, $"the dash still moves the champion toward the edge ({Distance(end, hero.Position):0})");
        });
    }

    /// <summary>
    /// Batch 474c: the owner's bundle 28 clicks in the setup, from the real Order spawn (533.8, 4171.2). The C2S move bodies are encrypted
    /// with the game's key, which the log does not hold in plaintext, so each target is the end of the route the server echoed to the
    /// owner's client at that click (owner log 29 October 13:19, 0xC1 WaypointList). Game time is the HUD clock: session time minus 15.75.
    /// Every route the server holds after a click, and the hero at the end, must stay on the base platform.
    /// </summary>
    [Check("dominion.opening-hold-owner-clicks-stop-on-the-platform")]
    public static void OpeningHoldOwnerClicksStopOnThePlatform()
    {
        var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        WithMatch(runtime =>
        {
            var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
            hero.Position = new Position2(533.8126, 4171.2314);
            var clicks = new (double Time, Position2 Target)[]
            {
                (6.70, new Position2(645.0, 3945.0)), (7.38, new Position2(1167.0, 3701.0)), (9.13, new Position2(1157.0, 3681.0)),
                (9.33, new Position2(1491.0, 3455.0)), (10.56, new Position2(1493.0, 3455.0)), (15.07, new Position2(1493.0, 3455.0)),
            };
            var trace = new List<string>();
            foreach (var (time, target) in clicks)
            {
                Advance(runtime, time - runtime.Combat.CurrentTime);
                trace.Add($"click {time:0.00} at hero ({hero.Position.X:0.0},{hero.Position.Z:0.0}) h {grid.HeightAt(hero.Position):0.0} -> ({target.X:0},{target.Z:0})");
                Move(runtime, 1, hero, target);
                trace.Add($"  route [{string.Join(" ", hero.Waypoints.Select(p => $"({p.X:0.0},{p.Z:0.0}) h{grid.HeightAt(p):0.0}"))}]");
                // The first point is the unit's own (client-anchored) start, which the route does not move; the points it adds must stay on the platform.
                var off = hero.Waypoints.Skip(1).Select(point => (point, height: grid.HeightAt(point))).Where(route => route.height < PlatformFloor).ToList();
                TestAssert.True(off.Count == 0,
                    $"the route the server holds after the click at {time:0.00} stays on the platform; it reaches ({off.FirstOrDefault().point.X:0}, {off.FirstOrDefault().point.Z:0}), height {off.FirstOrDefault().height:0}. Trace: {string.Join(" | ", trace)}");
            }
            Advance(runtime, 46.0 - runtime.Combat.CurrentTime);
            var height = grid.HeightAt(hero.Position);
            TestAssert.True(height >= PlatformFloor,
                $"the champion stands on the platform at 46 s ({hero.Position.X:0}, {hero.Position.Z:0}), height {height:0}");
        });
    }

    /// <summary>
    /// Batch 514 (restates batch 474d's stairs check): the visible forcefield is the hold's only limit (owner, 31 October 2026). The owner's
    /// bundle 30 route endpoints toward the Order stairs (session log 29 October 17:07) are replayed from the real Order spawn: the champion
    /// walks over the stairs' old footprint inside the ring, every route and step stays inside the forcefield, and the owner's 31 October stop at
    /// the footprint's edge (825, 3777), from which every order west was cut at the start, walks west.
    /// </summary>
    [Check("dominion.opening-hold-keeps-champion-inside-the-forcefield-over-the-stairs")]
    public static void OpeningHoldKeepsChampionInsideTheForcefieldOverTheStairs()
    {
        WithMatch(runtime =>
        {
            var hero = runtime.Session.Participants.Single(participant => participant.Team == Team.Order).Hero!;
            hero.Position = new Position2(533.8126, 4171.2314);
            var clicks = new (double Time, Position2 Target)[]
            {
                (6.90, new Position2(989.0, 4411.0)), (7.80, new Position2(1081.0, 4275.0)), (8.25, new Position2(1231.0, 3775.0)),
                (9.40, new Position2(1239.0, 3751.0)),
            };
            var overStairs = false;
            void StepUntil(double time)
            {
                while (runtime.Combat.CurrentTime < time)
                {
                    Advance(runtime, Step);
                    overStairs |= OldStairsFootprint(hero.Position);
                    TestAssert.True(InsideForcefield(hero.Position, hero.CombatRadius),
                        $"the champion stays inside the forcefield; at {runtime.Combat.CurrentTime:0.0} s it stands at ({hero.Position.X:0}, {hero.Position.Z:0})");
                }
            }
            foreach (var (time, target) in clicks)
            {
                StepUntil(time);
                Move(runtime, 1, hero, target);
                var outside = hero.Waypoints.Skip(1).Where(point => !InsideForcefield(point, hero.CombatRadius)).ToList();
                TestAssert.True(outside.Count == 0,
                    $"the route the server holds after the click at {time:0.00} stays inside the forcefield; it reaches ({outside.FirstOrDefault().X:0}, {outside.FirstOrDefault().Z:0})");
            }
            StepUntil(20.0);
            TestAssert.True(overStairs, "the champion walks over the stairs' old footprint inside the ring (no invisible wall there)");
            hero.Position = new Position2(825.0, 3777.0);
            Move(runtime, 1, hero, new Position2(646.2, 4235.9));
            Advance(runtime, 1.0);
            TestAssert.True(Distance(hero.Position, new Position2(825.0, 3777.0)) > 100.0,
                $"the owner's stop at the footprint's edge walks west ({hero.Position.X:0}, {hero.Position.Z:0})");
        });
    }

    // Batch 514: the Order forcefield (the test's own copy of HoldFootprint: the wall 498.8 less the champion's body about the ring's centre, the
    // Order barrier (578.98, 4114.34) plus forcefield.sco's CentralPoint scaled by 0.8 (+5.20, -27.98)), with a 2-unit margin for the lattice.
    private static bool InsideForcefield(Position2 point, double body) => Distance(point, new Position2(584.1866, 4086.3642)) <= 498.8 - body + 2.0;

    // Batch 474d's Order stairs footprint (the scene box, and a disc of 64 at each stair bone's bind-pose point), now walkable ground.
    private static bool OldStairsFootprint(Position2 point)
    {
        if (point.X >= 782.7 && point.X <= 906.4 && point.Z >= 4219.9 && point.Z <= 4343.7) return true;
        foreach (var stair in new[] { new Position2(844.5, 4281.8), new Position2(690.3, 3820.6), new Position2(872.7, 3975.6), new Position2(1127.6, 3882.9) })
            if (Distance(point, stair) <= 62.0) return true;
        return false;
    }

    [Check("dominion.opening-barriers-stand-over-both-bases-then-open")]
    public static void OpeningBarriersStandThenOpen()
    {
        WithMatch(runtime =>
        {
            Advance(runtime, 1.0);
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                var barrier = Barriers(runtime).SingleOrDefault(unit => unit.Team == team);
                TestAssert.True(barrier is not null && barrier.Alive, $"the {team} opening barrier stands at the start");
                TestAssert.True(Distance(barrier!.Position, MapDefinitions.TeamSpawn(team)) < 150.0,
                    $"the {team} barrier stands at its base (Locations.dat {team}Barrier)");
            }
            Advance(runtime, 40.0);
            TestAssert.Equal(2, Barriers(runtime).Count(unit => unit.Alive), "both barriers still stand at 41 s");
            Advance(runtime, Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds - runtime.Combat.CurrentTime + 0.5);
            TestAssert.Equal(0, Barriers(runtime).Count(unit => unit.Alive), "both barriers open when the ceremony lets the champions go");
        });
    }

    /// <summary>
    /// Batch 668: the forcefield goes on the wire as the shipped script makes it. <c>OdinOpeningBarrierParticle.luaobj</c> binds
    /// <c>Odin_Forcefield_green.troy</c> to the barrier (BindObjectVar Owner, SpecificTeamOnly the barrier's team). Batch 521 sent it unbound to keep
    /// the blur after the camera looks away; the client drops a hidden system's one-shot layers either way (batch 668 record), so the bind is the script's.
    /// </summary>
    [Check("dominion.opening-barrier-forcefield-is-bound-to-the-barrier")]
    public static void OpeningBarrierForcefieldIsBoundToTheBarrier()
    {
        WithMatch(runtime =>
        {
            var seat = runtime.Session.Participants.Single(participant => participant.Team == Team.Order);
            var wire = new List<Amberline.Network.Protocol.PlaintextPacket>();
            runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId,
                new Capture(wire), ready: true, view: seat.View));
            Advance(runtime, 2.0);
            var barrier = Barriers(runtime).Single(unit => unit.Team == Team.Order);
            var green = Amberline.Network.Protocol.SpellPackets.BuffNameHash("Odin_Forcefield_green");
            var creates = wire.Select(packet => packet.Plaintext.ToArray())
                .Where(bytes => bytes.Length == 73 && bytes[0] == 0x8C && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)) == green).ToList();
            TestAssert.Equal(1, creates.Count, "the Order client gets the Order barrier's green forcefield once");
            TestAssert.Equal(barrier.NetId, BinaryPrimitives.ReadUInt32LittleEndian(creates[0].AsSpan(29)),
                "the forcefield is bound to the barrier (the script's BindObjectVar Owner)");
        });
    }

    private sealed class Capture(List<Amberline.Network.Protocol.PlaintextPacket> sink) : Amberline.Network.Replication.IReplicationTransport
    {
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) => sink.Add(packet);
    }

    /// <summary>
    /// Batch 715: the stairs' thump. The client fires a clip's AnimEffects once the clip's frame reaches their start frame (AnimationFXPlayer,
    /// The client). At frame 0 of Close{n} that is the closing sound, <c>Odin-SOG_Stair{n}_OBA_Sound.troy</c>, whose OnCreate sound
    /// (<c>BOG/Stair{n}/buffactivate</c>, the waves <c>odin-bog_stair{n}_extend_01/02</c>) plays once when the particle is made,
    /// so the sound starts when the 0xDA lands. At the slam frame it is the slamming dust. The thump is the wave's end event, <see cref="ThumpSeconds"/>
    /// after the sound starts. The 0xDA's start and duration time-sync the clip, so its slam comes at start + frame * duration / frames. Every client
    /// gets each stage once on both stairs, and each slam lands within 0.75 s of its sound's thump, for both waves the sound picks from.
    /// </summary>
    [Check("dominion.stairs-slam-lands-on-the-thump-of-their-closing-sound")]
    public static void StairsSlamLandsOnTheThumpOfTheirClosingSound()
    {
        WithMatch(runtime =>
        {
            var wire = new Dictionary<Team, List<(double Time, byte[] Bytes)>>();
            foreach (var seat in runtime.Session.Participants)
            {
                var packets = wire[seat.Team] = new List<(double Time, byte[] Bytes)>();
                runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId,
                    new StampedCapture(packets, () => runtime.Combat.CurrentTime), ready: true, view: seat.View));
            }
            for (var tick = 0; tick < (OpeningCeremonyTiming.ReleaseSeconds + 5.0) / Step && runtime.Combat.CurrentTime < OpeningCeremonyTiming.ReleaseSeconds + 1.0; tick++)
            {
                runtime.Tick(Step);
                runtime.Delivery.Flush();
            }
            foreach (var (team, packets) in wire)
                foreach (var (stairs, character) in new[] { (DominionProps.OrderStairsNetId, "Odin_SoG_Order"), (DominionProps.ChaosStairsNetId, "Odin_SoG_Chaos") })
                {
                    var clips = packets.Where(packet => packet.Bytes.Length == 89 && packet.Bytes[0] == 0xDA
                        && BinaryPrimitives.ReadUInt32LittleEndian(packet.Bytes.AsSpan(77)) == stairs).ToList();
                    var timeline = string.Join("; ", clips.Select(clip => $"{clip.Time:0.000}: {ClipName(clip.Bytes)} start "
                        + $"{BinaryPrimitives.ReadSingleLittleEndian(clip.Bytes.AsSpan(69)):0.000} duration {BinaryPrimitives.ReadSingleLittleEndian(clip.Bytes.AsSpan(73)):0.000}"));
                    var stages = clips.Where(clip => ClipName(clip.Bytes).StartsWith("Close", StringComparison.Ordinal)).ToList();
                    TestAssert.Equal(4, stages.Count, $"the {team} client gets the four Close stages of {character} once each ({timeline})");
                    var effects = File.ReadAllLines(GameDataPaths.Default.Data($"Characters/{character}/AnimEffects.list"));
                    for (var n = 1; n <= 4; n++)
                    {
                        var (received, bytes) = stages[n - 1];
                        TestAssert.Equal($"Close{n}", ClipName(bytes), $"{character} stage {n} is Close{n} ({timeline})");
                        TestAssert.Equal(0, EffectFrame(effects, $"Close{n}", $"Odin-SOG_Stair{n}_OBA_Sound.troy"), $"Close{n}'s closing sound fires at frame 0");
                        var header = File.ReadAllBytes(GameDataPaths.Default.Data($"Characters/{character}/Animations/{character}_Close_{n}.anm"));
                        var frames = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
                        var start = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(69));
                        var duration = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(73));
                        var slam = start + EffectFrame(effects, $"Close{n}", "Odin_SOG_slamming_dust.troy") * duration / frames;
                        foreach (var thump in ThumpSeconds[n - 1])
                            TestAssert.True(Math.Abs(received + thump - slam) <= 0.75,
                                $"{team} client, {character} Close{n}: the slam at {slam:0.00} s lands on the thump at {received + thump:0.00} s ({timeline})");
                    }
                }
        });
    }

    /// <summary>
    /// Batch 715: the onset of each closing sound's end event (its thump), in seconds after the sound starts, for both waves of the sound
    /// (<c>odin-bog_stair{n}_extend_01</c> and <c>_02</c> in <c>DATA/Sounds/FMOD/LoL_SFX_odin.fsb</c>, CELT 0.6.1; the coarse band energies decoded
    /// at 0.1 s steps, batch 715 record).
    /// </summary>
    private static readonly double[][] ThumpSeconds = [[13.62, 13.04], [17.54, 17.75], [17.54, 17.54], [17.89, 17.65]];

    private static string ClipName(byte[] bytes) => System.Text.Encoding.ASCII.GetString(bytes, 5, 64).Split('\0')[0];

    // The start frame (s:) of an AnimEffects.list line: the clip, a bone, the effect file, then its options. Both bones of a stage share it.
    private static int EffectFrame(string[] lines, string clip, string effect) => lines
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(words => words.Length > 3 && words[0] == clip && words[2].Equals(effect, StringComparison.OrdinalIgnoreCase))
        .Select(words => int.Parse(words.First(word => word.StartsWith("s:", StringComparison.Ordinal))[2..]))
        .Distinct().Single();

    private sealed class StampedCapture(List<(double Time, byte[] Bytes)> sink, Func<double> now) : Amberline.Network.Replication.IReplicationTransport
    {
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) => sink.Add((now(), packet.Plaintext.ToArray()));
    }

    [Check("dominion.recall-lands-at-the-map-8-spawn-point")]
    public static void RecallLandsAtTheMapEightSpawnPoint()
    {
        WithMatch(runtime =>
        {
            Advance(runtime, 46.0);
            foreach (var (seat, team) in new[] { (1, Team.Order), (2, Team.Chaos) })
            {
                var hero = runtime.Session.Participants.Single(participant => participant.Team == team).Hero!;
                hero.Position = new Position2(6900.0, 6450.0);
                TestAssert.True(Cast(runtime, seat, hero, SpellSystem.RecallSlot), $"{team}: recall accepted");
                Advance(runtime, 9.0);
                var spawn = MapDefinitions.TeamSpawn(team);
                TestAssert.True(Distance(hero.Position, spawn) < 25.0,
                    $"{team}: Recall lands on the map 8 spawn point ({hero.Position.X:0.0}, {hero.Position.Z:0.0})");
            }
        });
    }

    private static IEnumerable<Entity> Barriers(MatchRuntime runtime) =>
        runtime.World.Entities.Values.Where(unit => unit.Name == "OdinOpeningBarrier" || unit.ChampionName == "OdinOpeningBarrier");

    private static void WithMatch(Action<MatchRuntime> body)
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            for (var id = 1; id <= roster.Count; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                _ = runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
            }
            TestAssert.True(runtime.Session.Start(), "start");
            body(runtime);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) runtime.Tick(Step);
        runtime.Delivery.Flush();
    }

    private static void Move(MatchRuntime runtime, int seat, Entity hero, Position2 destination)
    {
        var request = new byte[22]; request[0] = 0x75; request[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        runtime.Session.Receive(seat, 1, runtime.Cipher.Encrypt(request), runtime.Dependencies.Gameplay);
        runtime.Delivery.Flush();
    }

    private static bool Cast(MatchRuntime runtime, int seat, Entity hero, int slot)
    {
        var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)(slot << 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)hero.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)hero.Position.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), hero.NetId);
        var accepted = false;
        runtime.Session.Receive(seat, 1, runtime.Cipher.Encrypt(request), context => accepted = runtime.Dependencies.Gameplay(context));
        runtime.Delivery.Flush();
        return accepted;
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
