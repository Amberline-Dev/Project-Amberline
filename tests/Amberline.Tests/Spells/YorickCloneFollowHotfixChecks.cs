using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-yorick-revenant-flat-speed-sync")]
    public static void YorickRevenantSynchronizesFlatSpeedOnly()
    {
        using var match = new Match("Yorick", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        match.Hero(1).Position = new Position2(12000, 12000);
        match.Cast(0, 3, owner);
        runtime.Tick(.3);
        var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
        var baseline = clone.Speed;
        var haste = runtime.Buffs.AddBuff(owner, "B4FlatSpeed", BuffType.Haste, 10,
            owner.NetId, new BuffPayload { MoveSpeedFlat = 40 })!;
        runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
        TestAssert.Near(baseline + 40, clone.Speed, 1e-6);
        runtime.Buffs.RemoveBuff(owner, haste.Slot);
        runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
        TestAssert.Near(baseline, clone.Speed, 1e-6);
        runtime.Buffs.AddBuff(clone, "B4RevenantHaste", BuffType.Haste, 10,
            owner.NetId, new BuffPayload { MoveSpeedPercent = .1 });
        runtime.Buffs.AddBuff(owner, "B4OwnerHaste", BuffType.Haste, 10,
            owner.NetId, new BuffPayload { MoveSpeedFlat = 40, MoveSpeedPercent = .5 });
        runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
        TestAssert.Near((baseline + 40) * 1.1, clone.Speed, 1e-6,
            "the revenant keeps its own percentage effect, not the owner's final capped speed");
    }

    [Check("spells-live-yorick-r-spawns-at-current-target")]
    public static void YorickRSpawnsAtCurrentTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ally in new[] { false, true })
        {
            using var match = new Match("Yorick", team, ally: ally);
            var runtime = match.Runtime;
            var source = match.Hero(ally ? 2 : 0);
            match.Hero(1).Position = new Position2(10000, 10000);
            var castPosition = source.Position;
            var castDelay = GameData.Default.Spell("YorickReviveAlly", 3).CastDelay;

            match.Cast(0, 3, source);
            runtime.Tick(.1);
            TestAssert.False(runtime.World.Entities.Values.Any(unit =>
                unit.Kind == EntityKind.YorickRevenant), "R remains pending during its cast delay");
            source.Position += new Position2(96, 48);
            runtime.Tick(castDelay - .1);

            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickRevenant);
            TestAssert.True(source.Position != castPosition, "target moved after cast acceptance");
            TestAssert.Equal(source.Position, clone.Position,
                $"{team}/ally={ally}: revenant is created inside the live target");
            var spawn = match.Captures[0].Packets.Single(packet =>
                Opcode(packet) == 0x3C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == clone.NetId);
            TestAssert.Near(source.Position.X,
                BinaryPrimitives.ReadSingleLittleEndian(spawn.Plaintext.Span[10..]), 1e-5,
                $"{team}/ally={ally}: native pet spawn X uses live target");
            TestAssert.Near(source.Position.Z,
                BinaryPrimitives.ReadSingleLittleEndian(spawn.Plaintext.Span[18..]), 1e-5,
                $"{team}/ally={ally}: native pet spawn Z uses live target");
        }
    }

    [Check("spells-live-yorick-r-clone-explicit-move-spacing")]
    public static void YorickRCloneExplicitMoveKeepsOwnerRing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(10000, 10000);
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);

            var parked = owner.Position + new Position2(500, 0);
            SendPetOrder(match, clone, 6, parked);
            for (var tick = 0; tick < 20; tick++) runtime.Tick(.1);
            // Pet.lua TimerScanDistance parks a finished AI_PET_HARDMOVE in AI_PET_HARDIDLE.
            TestAssert.Equal(PetLuaAiEngine.StatePetHardIdle, match.Runtime.Pets.StateOf(clone)!.Value,
                $"{team}: completed explicit pet move retains hold state before Yorick arrives");
            match.Clear();
            match.Move(0, parked);
            for (var tick = 0; tick < 35; tick++) runtime.Tick(.1);

            // owner decision 28 Sept 2026: original game values.
            // Preserve physical clearance, without a voluntary owner ring.
            TestAssert.True(Distance(clone.Position, owner.Position) >=
                runtime.Movement.BodyClearance(clone, owner) - 2.0,
                $"{team}: explicit pet hold must leave the revenant targetable beside Yorick; clone={clone.Position}, owner={owner.Position}, state={match.Runtime.Pets.StateOf(clone)}, wp={clone.Waypoints.Count}");
            var cloneWaypointEndpoints = match.Captures.SelectMany(capture => capture.Packets)
                .Where(packet => Opcode(packet) == 0xC1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == clone.NetId)
                .Select(packet => Decode(packet).GetProperty("waypoints").EnumerateArray().ToArray())
                .Where(waypoints => waypoints.Length > 0)
                .Select(waypoints => waypoints[^1])
                .Select(point => new Position2(point[0].GetDouble(), point[1].GetDouble()))
                .ToArray();
            TestAssert.True(cloneWaypointEndpoints.Length > 0,
                $"{team}: clone spacing correction must publish a native C1 path");
            // owner decision 28 Sept 2026: original game values.
            TestAssert.True(cloneWaypointEndpoints.Any(point => Distance(point, owner.Position) >=
                runtime.Movement.BodyClearance(clone, owner) - 2.0),
                $"{team}: clone C1 endpoint must preserve the owner gap for the native client");
            TestAssert.Equal(PetLuaAiEngine.StatePetHardIdle, match.Runtime.Pets.StateOf(clone)!.Value,
                $"{team}: owner movement does not convert explicit pet hold into follow");
        }
    }

    [Check("spells-live-yorick-r-clone-normal-move-follows")]
    public static void YorickRCloneNormalHeroMoveKeepsFollowing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(10000, 10000);
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
            clone.Position = owner.Position;
            clone.Waypoints.Clear();
            clone.Target = null;
            var destination = owner.Position + new Position2(700, 0);

            match.Move(0, destination);
            for (var tick = 0; tick < 35; tick++) runtime.Tick(.1);

            TestAssert.True(Distance(owner.Position, destination) < 10.0,
                $"{team}: normal hero movement reaches its own destination; owner={owner.Position}, goal={destination}, wp={owner.Waypoints.Count}");
            TestAssert.False(UnderHardOrder(match, clone),
                $"{team}: normal hero movement does not convert autonomous R follow into hold");
            // owner decision 28 Sept 2026: original game values.
            // Preserve physical clearance, without a voluntary owner ring.
            TestAssert.True(Distance(clone.Position, owner.Position) >=
                runtime.Movement.BodyClearance(clone, owner) - 2.0,
                $"{team}: autonomous R follow leaves the revenant outside Yorick");
        }
    }

    [Check("spells-live-yorick-r-clone-target-death-resumes-follow")]
    public static void YorickRCloneTargetDeathResumesFollowAndReacquires()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team, extraEnemy: true);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var target = match.Hero(1);
            var replacement = match.Hero(2);
            target.Position = owner.Position + new Position2(260, 0);
            replacement.Position = new Position2(10000, 10000);
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);

            SendPetOrder(match, clone, 5, target.Position, target.NetId);
            runtime.Tick(.3);
            runtime.Combat.Kill(target, clone.NetId);
            var destination = owner.Position + new Position2(700, 0);
            match.Move(0, destination);
            var sawFollow = false;
            for (var tick = 0; tick < 35; tick++)
            {
                runtime.Tick(.1);
                sawFollow |= clone.Waypoints.Count > 0;
            }

            TestAssert.True(sawFollow,
                $"{team}: target death resumes owner follow instead of leaving a stale attack hold; clone={clone.Position}, owner={owner.Position}, state={match.Runtime.Pets.StateOf(clone)}, target={clone.AttackTarget}, wp={clone.Waypoints.Count}");
            // owner decision 28 Sept 2026: original game values.
            // Preserve physical clearance, without a voluntary owner ring.
            TestAssert.True(Distance(clone.Position, owner.Position) >=
                runtime.Movement.BodyClearance(clone, owner) - 2.0,
                $"{team}: follow after target death keeps the owner ring clear");

            replacement.Position = clone.Position + new Position2(220, 0);
            for (var tick = 0; tick < 5; tick++) runtime.Tick(.1);
            TestAssert.Equal(replacement.NetId, clone.AttackTarget ?? 0,
                $"{team}: the revenant reacquires a new enemy after follow resumes; clone={clone.Position}, replacement={replacement.Position}, target={clone.AttackTarget}, state={match.Runtime.Pets.StateOf(clone)}");
        }
    }

    private static void SendPetOrder(Match match, Entity clone, byte order,
        Position2 destination, uint targetNetId = 0)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), clone.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(18), targetNetId);
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context =>
            accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        TestAssert.True(accepted, $"production pet order {order} is accepted");
    }
}
