using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>
/// The Dominion (Crystal Scar) props, <see cref="DominionProps"/>. The first check pins the offsets the 0xD9 create and the 0xDA animation
/// write (the offsets the client reads, see <see cref="LevelPropPackets"/>). The second pins the seam's parse: unset is production's facing,
/// and <c>off</c> sends nothing.
/// </summary>
public static class DominionPropsChecks
{
    [Check("network.odin-props-encoders-write-the-probed-offsets")]
    public static void TheCreateAndAnimationWriteTheProbedOffsets()
    {
        var create = LevelPropPackets.Create(0x12345678u, 0x40, new Position3(1.5, 2.25, -3.0), new Position3(0.0, 0.0, 1.0),
            new Position3(0.0, 0.0, 0.0), 300, 1, 7, "LevelProp_Odin_SoG_Order", "Odin_SoG_Order");
        var b = create.Plaintext.Span;
        TestAssert.Equal(181, b.Length, "0xD9 is 181 bytes");
        TestAssert.Equal((byte)0xD9, b[0], "opcode 0xD9 at byte 0");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(1, 4)), "header zero at 1..4");
        TestAssert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(5, 4)), "netid uint32 at 5");
        TestAssert.Equal((byte)0x40, b[9], "node byte at 9");
        TestAssert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(10, 4)), "position X at 10");
        TestAssert.Equal(2.25f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(14, 4)), "position Y at 14");
        TestAssert.Equal(-3.0f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(18, 4)), "position Z at 18");
        TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(22, 4)), "facing X at 22");
        TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(26, 4)), "facing Y at 26");
        TestAssert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(b.Slice(30, 4)), "facing Z at 30");
        for (var offset = 34; offset < 46; offset++)
            TestAssert.Equal((byte)0, b[offset], "offset vector (raw 34..45) is zero at " + offset);
        TestAssert.Equal(300u, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(46, 4)), "team uint32 at 46");
        TestAssert.Equal((byte)0, b[50], "byte at 50 is zero");
        TestAssert.Equal((byte)7, b[51], "raw 51 at 51");
        TestAssert.Equal((byte)1, b[52], "branch flag at 52");
        TestAssert.Equal("LevelProp_Odin_SoG_Order", Ascii(b, 53, 64), "Name at 53 (64 bytes, NUL-terminated)");
        TestAssert.Equal("Odin_SoG_Order", Ascii(b, 117, 64), "PropName at 117 (64 bytes, NUL-terminated)");

        var animation = LevelPropPackets.Animation(0xABCDEF01u, "Close1", 0.5f, 2.0f);
        var a = animation.Plaintext.Span;
        TestAssert.Equal(89, a.Length, "0xDA is 89 bytes");
        TestAssert.Equal((byte)0xDA, a[0], "opcode 0xDA at byte 0");
        TestAssert.Equal("Close1", Ascii(a, 5, 64), "animation name C-string at 5");
        TestAssert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(a.Slice(69, 4)), "float at 69");
        TestAssert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(a.Slice(73, 4)), "float at 73");
        TestAssert.Equal(0xABCDEF01u, BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(77, 4)), "netid key uint32 at 77");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(81, 4)), "uint32 at 81 is zero");
        TestAssert.Equal((byte)0, a[85], "flag byte at 85 is zero");
        TestAssert.Equal((byte)0, a[86], "byte at 86 is zero");
        TestAssert.Equal((byte)0, a[87], "byte at 87 is zero");
        TestAssert.Equal((byte)0, a[88], "byte at 88 is zero");
    }

    [Check("network.odin-props-default-on-and-the-off-switch")]
    public static void OnByDefaultAndOffWhenAsked()
    {
        // The environment is read only through FromValue in this check, so no test touches the process environment.
        TestAssert.Equal(DominionPropFacing.Z, DominionProps.FromValue(null), "unset is on (the default since 29 October)");
        TestAssert.Equal(DominionPropFacing.Z, DominionProps.FromValue(""), "empty is on");
        TestAssert.Equal(DominionPropFacing.Off, DominionProps.FromValue("off"), "off turns it off");
        TestAssert.Equal(DominionPropFacing.Off, DominionProps.FromValue("1"), "any other value is off");
        TestAssert.Equal(DominionPropFacing.Off, DominionProps.FromValue("Z"), "the value is case-sensitive: Z is off");
        TestAssert.Equal(DominionPropFacing.Z, DominionProps.FromValue("z"), "z enables the facing (0, 0, 1)");
        TestAssert.Equal(DominionPropFacing.Zero, DominionProps.FromValue("0"), "0 enables the facing (0, 0, 0)");

        TestAssert.Equal(0, DominionProps.SpawnPackets(DominionPropFacing.Off).Count, "off: no spawn creates");
        TestAssert.Equal(0, new DominionProps.StairAnimator().Due(1000.0, DominionPropFacing.Off, 80.0).Count, "off: no stair stage");

        var on = DominionProps.SpawnPackets(DominionPropFacing.Z);
        TestAssert.Equal(4, on.Count, "on: two stairs and two crystal props (batch 491)");
        // The crystal props' fallback position is the scene centre: Y from the .sco CentralPoint plus ObjectCFG Move (not the nav height).
        TestAssert.Near(1740.5343, MapDefinitions.CrystalScarNexusList[0].Position.X, 1e-2, "Order crystal X");
        TestAssert.Near(59.1680, DominionProps.CrystalSceneHeight(Team.Order), 1e-3, "Order crystal scene Y");
        TestAssert.Near(3.6124, DominionProps.CrystalSceneHeight(Team.Chaos), 1e-3, "Chaos crystal scene Y");
        var crystal = on[2].Plaintext.Span;
        TestAssert.Equal((byte)1, crystal[52], "the crystal is branch 1 (by name at its spawner)");
        TestAssert.Equal("LevelProp_Odin_SOG_Order_Crystal", Ascii(crystal, 53, 64), "the Order crystal's scene spawner name");
        TestAssert.Equal("Odin_SOG_Order_Crystal", Ascii(crystal, 117, 64), "the Order crystal's character");
    }

    [Check("network.odin-props-stair-stages-end-at-the-release")]
    public static void TheFourStairClipsRunBackToBackAndEndAtTheRelease()
    {
        // Batch 715: each stage is time-synced over the length its closing sound puts the slam at (15 s for Close1, 20 s for Close2 to Close4),
        // no longer the clips' own 30 fps (9.5333 s and 10.0333 s), which played each thump in the next stage.
        var release = OpeningCeremonyTiming.ReleaseSeconds;
        var stages = DominionProps.StairStages(release);
        TestAssert.Equal(4, stages.Count, "four stages");
        TestAssert.Near(release - (15.0 + 3 * 20.0), stages[0].Start, 1e-9, "Close1 starts the sum of the four stages before the release");
        TestAssert.Near(15.0, stages[0].Duration, 1e-9, "Close1 plays over 15 s");
        TestAssert.Near(20.0, stages[3].Duration, 1e-9, "Close4 plays over 20 s");
        for (var index = 1; index < stages.Count; index++)
            TestAssert.Near(stages[index - 1].Start + stages[index - 1].Duration, stages[index].Start, 1e-9, "each stage starts when the previous one ends");
        TestAssert.Near(release, stages[3].Start + stages[3].Duration, 1e-9, "Close4 ends at the release");

        // Each stage goes out once, on the first call at or after its start.
        var animator = new DominionProps.StairAnimator();
        TestAssert.Equal(0, animator.Due(stages[0].Start - 0.01, DominionPropFacing.Z, release).Count, "before Close1: nothing");
        TestAssert.Equal(2, animator.Due(stages[0].Start, DominionPropFacing.Z, release).Count, "at Close1's start: both stairs");
        TestAssert.Equal(0, animator.Due(stages[0].Start + 0.5, DominionPropFacing.Z, release).Count, "a stage goes out once");
        TestAssert.Equal(6, animator.Due(release, DominionPropFacing.Z, release).Count, "the rest go out at their starts, two stairs each");
        TestAssert.Equal(0, animator.Due(release + 5.0, DominionPropFacing.Z, release).Count, "after the last one: nothing more");

        // The flags are written at raw 81 (the hold bit is not identified, so the default is 0).
        var flagged = LevelPropPackets.Animation(0x1u, "Close1", 40.0f, 9.5f, 0x20u).Plaintext.Span;
        TestAssert.Equal(0x20u, BinaryPrimitives.ReadUInt32LittleEndian(flagged.Slice(81, 4)), "flags uint32 at 81");
    }

    [Check("network.odin-props-crystal-raise-follows-the-release")]
    public static void TheCrystalsRaiseAtTheReleaseThenIdle()
    {
        var raiser = new DominionProps.CrystalRaiser();
        var release = OpeningCeremonyTiming.ReleaseSeconds;
        TestAssert.Equal(0, raiser.Due(release - 0.01, DominionPropFacing.Z, release).Count, "before the release: nothing");
        var raise = raiser.Due(release, DominionPropFacing.Z, release);
        TestAssert.Equal(2, raise.Count, "at the release: Raise on both crystals");
        var span = raise[0].Plaintext.Span;
        TestAssert.Equal("Raise", Ascii(span, 5, 64), "Raise is the clip at raw 5");
        TestAssert.Equal(MapDefinitions.CrystalScarNexusList[0].NetId, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(77, 4)), "netid at 77");
        TestAssert.Equal((float)release, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(69, 4)), "start at the release at 69 (time-synced)");
        TestAssert.Near(DominionProps.CrystalRaiser.RaiseSeconds, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(73, 4)), 1e-3, "Raise's length at 73");
        var idle = raiser.Due(release + DominionProps.CrystalRaiser.RaiseSeconds, DominionPropFacing.Z, release);
        TestAssert.Equal(2, idle.Count, "after Raise's 39 frames: Raised_Idle on both crystals");
        TestAssert.Equal("Raised_Idle", Ascii(idle[0].Plaintext.Span, 5, 64), "Raised_Idle is the idle that holds");
        TestAssert.Equal((float)(release + DominionProps.CrystalRaiser.RaiseSeconds), BinaryPrimitives.ReadSingleLittleEndian(idle[0].Plaintext.Span.Slice(69, 4)), "Raised_Idle starts when Raise ends");
        TestAssert.Equal((float)DominionProps.HoldSeconds, BinaryPrimitives.ReadSingleLittleEndian(idle[0].Plaintext.Span.Slice(73, 4)), "Raised_Idle holds (long duration at 73)");
        TestAssert.Equal(0, raiser.Due(release + 1000.0, DominionPropFacing.Z, release).Count, "each step goes out once");
        TestAssert.Equal(0, new DominionProps.CrystalRaiser().Due(release + 1.0, DominionPropFacing.Off, release).Count, "off: nothing");
    }

    [Check("network.odin-props-stair-hold-flag-is-written")]
    public static void TheStairStagesCarryTheHoldFlag()
    {
        var animator = new DominionProps.StairAnimator();
        var release = OpeningCeremonyTiming.ReleaseSeconds;
        var first = animator.Due(DominionProps.StairStages(release)[0].Start, DominionPropFacing.Z, release);
        TestAssert.Equal(2, first.Count, "Close1 goes out for both stairs");
        TestAssert.Equal(0x204u, BinaryPrimitives.ReadUInt32LittleEndian(first[0].Plaintext.Span.Slice(81, 4)),
            "the stairs' stage flags at raw 81: hold the last frame (0x4), full weight (0x200)");
    }

    [Check("network.odin-props-crystal-beams-fire-at-each-latch")]
    public static void TheStairLasersFireAtEachLatchIntoTheCrystal()
    {
        // Batch 507: the shipped (commented) AnimEffects lines: Close1 s:270 from Crystal_r_4_aim and Crystal_l_4_aim, ... Close4 s:290.
        var release = OpeningCeremonyTiming.ReleaseSeconds;
        var stages = DominionProps.StairStages(release);
        var bounds = new NavigationBounds(-1000.0, 15000.0, -1000.0, 15000.0);
        var beams = new DominionProps.CrystalBeams();
        // Batch 715: the clip is time-synced over its stage, so frame 270 of Close1's 286 comes 270/286 of the stage in.
        var latch = stages[0].Start + 270 * stages[0].Duration / 286;
        TestAssert.Equal(0, beams.Due(latch - 0.01, DominionPropFacing.Z, release, bounds, _ => false).Count, "before Close1's frame 270: nothing");
        var first = beams.Due(latch, DominionPropFacing.Z, release, bounds, _ => false);
        TestAssert.Equal(6, first.Count, "at the latch: two lasers and a lit core per team");
        // Batch 523: green to the base's own team, red to the other; persistent, so late and returning clients get it.
        TestAssert.Equal(Team.Order, first[0].AlliedTeam, "the Order base's laser is Order's own");
        TestAssert.True(first[0].Persistent, "a laser is a persistent effect");
        TestAssert.Equal(SpellPackets.BuffNameHash(DominionProps.EnemyBeamEffect),
            BinaryPrimitives.ReadUInt32LittleEndian(first[0].EnemyPacket!.Plaintext.Span.Slice(6, 4)), "the other team sees it red");
        TestAssert.Equal(SpellPackets.BuffNameHash("Odin_crystal_beam_hit_blue"),
            BinaryPrimitives.ReadUInt32LittleEndian(first[2].EnemyPacket!.Plaintext.Span.Slice(6, 4)), "Chaos sees the Order crystal's core blue");
        var b = first[0].Packet.Plaintext.Span;
        TestAssert.Equal((byte)0x8C, b[0], "an FX create");
        TestAssert.Equal(SpellPackets.BuffNameHash(DominionProps.OwnBeamEffect), BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(6, 4)), "the own team sees it green");
        TestAssert.Equal(SpellPackets.BuffNameHash("crystal_core"), BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12, 4)), "aimed at the gem bone");
        TestAssert.Equal(SpellPackets.BuffNameHash("Crystal_r_4_aim"), BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(16, 4)), "from the stair piece's aim bone");
        TestAssert.Equal(DominionProps.OrderCrystalNetId, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4)), "target: the crystal prop");
        TestAssert.Equal(DominionProps.OrderStairsNetId, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(29, 4)), "bound to the stairs");
        TestAssert.Equal(0, beams.Due(latch + 0.5, DominionPropFacing.Z, release, bounds, _ => false).Count, "a stage goes out once");
        var rest = beams.Due(release, DominionPropFacing.Z, release, bounds, team => team == Team.Chaos);
        TestAssert.Equal(9, rest.Count, "the other three stages by the release, only for the team whose nexus stands");
        TestAssert.Equal(0, new DominionProps.CrystalBeams().Due(release, DominionPropFacing.Off, release, bounds, _ => false).Count, "off: nothing");
        TestAssert.Equal(0, beams.Removals(DominionPropFacing.Z, _ => false).Count, "no removal while both nexuses stand");
        TestAssert.Equal(12, beams.Removals(DominionPropFacing.Z, team => team == Team.Chaos).Count, "the fall removes the team's 8 lasers and 4 cores");
        TestAssert.Equal(0, beams.Removals(DominionPropFacing.Z, team => team == Team.Chaos).Count, "once");
    }

    /// <summary>
    /// Batch 725 (owner, 9 November 2026: "The lasers on chaos side in dominion are misaligned from the walls"): each laser starts on its
    /// stair piece. The two stairs are one model mirrored across X, and each skeleton holds two sets of the eight pieces and their aim
    /// bones: the plain <c>Crystal_{r,l}_{n}</c> and the <c>chaos_</c> set. Each skin draws one set (the .skl influence list): Order's the
    /// plain pieces, Chaos's the <c>chaos_</c> pieces, which sit at the X-mirror of Order's. So the bone a Chaos laser starts from must sit at
    /// the X-mirror of the bone the matching Order laser starts from, in the pose the laser fires in (its Close clip at the latch frame) and
    /// in the held pose (Raised_Idle, the stairs' <c>Crystal_Idle</c> frame 0). The bone is read back from each laser's 0x8C (its bone hash
    /// at 16) and placed through the clip's tracks (r3d2anmd v3: per frame a quaternion and a position, each track local to its .skl parent).
    /// </summary>
    [Check("network.odin-props-chaos-lasers-start-at-the-mirror-of-the-order-lasers")]
    public static void TheChaosLasersStartAtTheMirrorOfTheOrderLasers()
    {
        foreach (var (team, piece, other) in new[] { (Team.Order, "Crystal_r_4", "chaos_Crystal_r_4"), (Team.Chaos, "chaos_Crystal_r_4", "Crystal_r_4") })
        {
            var drawn = Skeleton(team).Influences;
            TestAssert.True(drawn.Contains(piece) && !drawn.Contains(other), $"the {team} skin draws {piece}, not {other}");
        }
        var bounds = new NavigationBounds(-1000.0, 15000.0, -1000.0, 15000.0);
        foreach (var (stage, frame, _, _) in DominionProps.BeamStages)
        {
            var order = DominionProps.BeamMessages(stage, Team.Order, bounds);
            var chaos = DominionProps.BeamMessages(stage, Team.Chaos, bounds);
            for (var laser = 0; laser < 2; laser++)
                foreach (var (clip, at) in new[] { ($"Close_{stage + 1}", frame), ("Crystal_Idle", 0) })
                {
                    var (orderBone, from) = BoneAt(Team.Order, clip, at, BinaryPrimitives.ReadUInt32LittleEndian(order[laser].Packet.Plaintext.Span.Slice(16, 4)));
                    var (chaosBone, start) = BoneAt(Team.Chaos, clip, at, BinaryPrimitives.ReadUInt32LittleEndian(chaos[laser].Packet.Plaintext.Span.Slice(16, 4)));
                    var apart = System.Numerics.Vector3.Distance(start, from * new System.Numerics.Vector3(-1f, 1f, 1f));
                    TestAssert.True(apart < 1.0f, $"stage {stage + 1}, {clip} frame {at}: the Chaos laser's {chaosBone} {start} sits {apart:0.0} units off the "
                        + $"mirror of the Order laser's {orderBone} {from}");
                }
        }
    }

    // A stairs skeleton (r3d2sklt v2): 88-byte records from 20 (name[32], parent int32), then the skin's influence list (a count, then bone indices).
    private static (string[] Names, int[] Parents, string[] Influences) Skeleton(Team team)
    {
        var character = team == Team.Order ? "Odin_SoG_Order" : "Odin_SoG_Chaos";
        var skl = File.ReadAllBytes(Amberline.Data.GameDataPaths.Default.Data($"Characters/{character}/{character}.skl"));
        var count = BinaryPrimitives.ReadInt32LittleEndian(skl.AsSpan(16));
        var names = Enumerable.Range(0, count).Select(bone => Ascii(skl, 20 + bone * 88, 32)).ToArray();
        var parents = Enumerable.Range(0, count).Select(bone => BinaryPrimitives.ReadInt32LittleEndian(skl.AsSpan(20 + bone * 88 + 32))).ToArray();
        var tail = 20 + count * 88;
        var influences = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(skl.AsSpan(tail)))
            .Select(index => names[BinaryPrimitives.ReadInt32LittleEndian(skl.AsSpan(tail + 4 + 4 * index))]).ToArray();
        return (names, parents, influences);
    }

    // The bone with this hash, and its model position in one frame of the team's stairs clip (r3d2anmd v3: from 28, per track name[32], flags,
    // then per frame a quaternion x, y, z, w and a position; a track is local to its parent).
    private static (string Bone, System.Numerics.Vector3 Position) BoneAt(Team team, string clip, int frame, uint boneHash)
    {
        var character = team == Team.Order ? "Odin_SoG_Order" : "Odin_SoG_Chaos";
        var (names, parents, _) = Skeleton(team);
        var anm = File.ReadAllBytes(Amberline.Data.GameDataPaths.Default.Data($"Characters/{character}/Animations/{character}_{clip}.anm"));
        var (tracks, frames) = (BinaryPrimitives.ReadInt32LittleEndian(anm.AsSpan(16)), BinaryPrimitives.ReadInt32LittleEndian(anm.AsSpan(20)));
        float F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(anm.AsSpan(offset));
        var pose = new Dictionary<string, (System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Position)>(StringComparer.OrdinalIgnoreCase);
        for (var track = 0; track < tracks; track++)
        {
            var name = 28 + track * (36 + 28 * frames);
            var at = name + 36 + 28 * frame;
            pose[Ascii(anm, name, 32)] = (new(F(at), F(at + 4), F(at + 8), F(at + 12)), new(F(at + 16), F(at + 20), F(at + 24)));
        }
        var index = Array.FindIndex(names, name => SpellPackets.BuffNameHash(name) == boneHash);
        TestAssert.True(index >= 0, $"the {team} stairs have the laser's bone 0x{boneHash:X8}");
        var point = System.Numerics.Vector3.Zero;
        for (var bone = index; bone >= 0; bone = parents[bone])
            point = System.Numerics.Vector3.Transform(point, System.Numerics.Quaternion.Normalize(pose[names[bone]].Rotation)) + pose[names[bone]].Position;
        return (names[index], point);
    }

    [Check("dominion.opening-announcement-plays-thirty-seconds-before-the-release")]
    public static void TheFirstStartMessageIsThirtySecondsBeforeTheRelease()
    {
        TestAssert.Near(50.0, OpeningCeremonyTiming.FirstAnnouncementSeconds, 1e-9,
            "the battle-begins-in-30-seconds message plays at 50 s, 30 s before the 80 s release (NOT RETAIL)");
        TestAssert.Near(OpeningCeremonyTiming.ReleaseSeconds - 30.0, OpeningCeremonyTiming.FirstAnnouncementSeconds, 1e-9,
            "the first message is 30 s before the release");
    }

    private static string Ascii(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        var field = bytes.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? field : field.Slice(0, end));
    }
}
