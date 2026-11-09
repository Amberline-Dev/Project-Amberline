using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-ezreal-b9-lethal-overlap-cast-completion")]
    public static void EzrealLethalOverlapCompletesChildBeforeRetirement()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Ezreal", team, enemyChampion: "Shaco");
            var hero = match.Hero(0);
            var target = match.Hero(1);
            target.Position = hero.Position;
            target.Health = 1;
            var deaths = target.ChampionDeaths;
            match.Runtime.Delivery.Flush(); match.Clear();

            // Queue the child, launch and lethal collision in one delivery batch.
            var aim = new Position3(hero.Position.X + 900, 0, hero.Position.Z);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(hero,
                new SpellCastRequest(0, false, aim, aim, 0), 0));
            var missileId = EzrealFlights.Of(match.Runtime.Spells, hero).Single().MissileNetId;
            SpellStep(match, .3);
            match.Runtime.Delivery.Flush();
            SpellStep(match, 1);
            match.Runtime.Delivery.Flush();
            TestAssert.False(target.Alive, "overlapping Q is lethal");
            TestAssert.Equal(deaths + 1, target.ChampionDeaths, "exactly one death");
            foreach (var capture in match.Captures)
            {
                var packets = capture.Packets;
                var child = packets.Single(packet => IsB6Child(packet, 0x05FE9CE5));
                TestAssert.Equal(1, packets.Count(IsCastCompletion), "Q launch completes the native child once");
                var finish = packets.Single(IsCastCompletion);
                var retire = packets.Single(packet => IsB6Retirement(packet, missileId));
                TestAssert.Equal(missileId, BinaryPrimitives.ReadUInt32LittleEndian(child.Plaintext.Span[28..]));
                TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(finish.Plaintext.Span[1..]));
                TestAssert.True(packets.IndexOf(child) < packets.IndexOf(finish) &&
                    packets.IndexOf(finish) < packets.IndexOf(retire), "child cast -> completion -> matching retirement");
                foreach (var packet in new[] { child, finish, retire })
                {
                    TestAssert.Equal((byte)3, packet.Channel);
                    TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
                }
                TestAssert.Equal(1, packets.Count(packet => Opcode(packet) == 0x68), "exactly one damage packet");
                TestAssert.Equal(1, packets.Count(packet => Opcode(packet) == 0x61), "exactly one death packet");
                TestAssert.False(packets.Any(packet => Opcode(packet) == 0x41), "child owns initial missile creation");
            }
        }
    }

    [Check("spells-ezreal-b6-q-child-cast")]
    public static void EzrealMysticShotStartsItsBowTrack()
    {
        AssertB6CastTrack("EzrealMysticShotMissile", 0, 8, 2, "Ezreal_bow.troy", "BUFFBONE_HAND_BOW");
        AssertB6MissileChild("Ezreal", 0, 0x05FE9CE5, 42, .251);
    }

    [Check("spells-ezreal-b6-w-child-cast")]
    public static void EzrealEssenceFluxStartsItsBowTrack()
    {
        AssertB6CastTrack("EzrealEssenceFluxMissile", 0, 8, 2, "Ezreal_bow_yellow.troy", "BUFFBONE_HAND_BOW");
        AssertB6MissileChild("Ezreal", 1, 0x0FA2B315, 44, .25);
    }

    private static void AssertB6MissileChild(string champion, int slot, uint hash, byte extraSlot, double castTime)
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "flight", "death" })
        {
            // Retail windups are uncancellable and the script engine has no interruption branch (the hand-written module's "interrupt" ending
            // ran on a private cancellable spell record), so a cast ends in its flight or in the caster's death.
            var data = new GameData();
            using var match = new Match(champion, team, data: data);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(400, 500);
            match.Runtime.Delivery.Flush(); match.Clear();
            var mana = hero.Mana;
            match.CastGround(0, hero.Position + new Position2(900, 0), slot);
            var missileId = EzrealFlights.Of(match.Runtime.Spells, hero).Single().MissileNetId;
            TestAssert.True(missileId != 0, "child uses the allocated gameplay missile");
            var parentName = slot == 0 ? "EzrealMysticShot" : "EzrealEssenceFlux";
            TestAssert.Near(data.Spell(parentName, 5).ManaCosts[0], mana - hero.Mana, 1e-5,
                "only the accepted parent spends mana");
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(2, capture.Packets.Count(packet => Opcode(packet) == 0xBD),
                    "one parent acknowledgement and one normal child");
                AssertB6ChildCast(capture.Packets.Single(packet => IsB6Child(packet, hash)),
                    hero, hash, extraSlot, castTime, missileId);
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) is 0x41 or 0xB8),
                    "child cast alone owns the initial missile and animation");
            }
            SpellStep(match, .1);
            if (ending == "death") match.Runtime.Combat.Kill(hero, enemy.NetId);
            SpellStep(match, .14);
            if (ending == "flight")
            {
                TestAssert.Equal(0u, ActiveFogMissile(match).Id, "gameplay waits for its existing windup");
                SpellStep(match, .02);
                TestAssert.Equal(missileId, ActiveFogMissile(match).Id, "launch keeps child identity");
                match.Runtime.Delivery.Flush();
                var lateCapture = new Capture();
                var late = new ReplicationObserver(90, enemy.Team, enemy.NetId, lateCapture);
                late.View.Known.UnionWith(match.Runtime.Session.Participants.Select(p => p.Hero!.NetId));
                match.Runtime.Delivery.AddObserver(late);
                SpellStep(match, .01); match.Runtime.Delivery.Flush();
                var refresh = lateCapture.Packets.Single(packet => Opcode(packet) == 0x41);
                TestAssert.Equal(hash, BinaryPrimitives.ReadUInt32LittleEndian(refresh.Plaintext.Span[100..]));
                TestAssert.Equal(missileId, BinaryPrimitives.ReadUInt32LittleEndian(refresh.Plaintext.Span[117..]));
                TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(refresh.Plaintext.Span[113..]));
                TestAssert.False(lateCapture.Packets.Any(packet => Opcode(packet) == 0xBD), "late sight never replays the child cast");
                SpellStep(match, .01); match.Runtime.Delivery.Flush();
                TestAssert.Equal(1, lateCapture.Packets.Count(packet => Opcode(packet) == 0x41), "one late creation");
                SpellStep(match, 2); match.Runtime.Delivery.Flush();
                TestAssert.Equal(1, lateCapture.Packets.Count(packet => IsB6Retirement(packet, missileId)));
                TestAssert.False(late.View.Effects.Contains(missileId), "late view retires the same missile");
            }
            else
            {
                SpellStep(match, 2); match.Runtime.Delivery.Flush();
                TestAssert.Equal(0u, ActiveFogMissile(match).Id, "cancelled windup never launches");
                TestAssert.True(!EzrealFlights.WindingUp(match.Runtime.Spells, hero), "pending state is cleared");
            }
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(1, capture.Packets.Count(packet => IsB6Child(packet, hash)), "child is not repeated");
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x41), "no duplicate initial missile");
                // A launched line ends with one DestroyMissile for everyone (BbLineMissile.Finish). A cast the caster's death cancelled in its
                // windup never created a missile: the client makes it at its own cast frame, and a destroy that reaches it before the frame finds
                // nothing (BbLineMissile.Launch), so the script engine sends that retirement only to views that missed the cast (RetireCastMissile,
                // LateCopiesOnly); the cast's viewers just forget the id, which the last loop below asserts (batch 103, row 18).
                TestAssert.Equal(ending == "flight" ? 1 : 0, capture.Packets.Count(packet => IsB6Retirement(packet, missileId)),
                    ending == "flight" ? "one matching retirement" : "a cancelled windup's missile needs no retirement");
            }
            foreach (var participant in match.Runtime.Session.Participants)
                TestAssert.False(participant.View.Effects.Contains(missileId), "no stale observer missile");
        }
    }

    private static bool IsB6Child(PlaintextPacket packet, uint hash) =>
        Opcode(packet) == 0xBD && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == hash;

    private static bool IsB6Retirement(PlaintextPacket packet, uint missileId) =>
        Opcode(packet) == 0x5D && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missileId;

    private static void AssertB6ChildCast(PlaintextPacket packet, Entity hero, uint hash,
        byte slot, double castTime, uint missileId, byte flags = 0)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(hash, BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]), "literal shipped child spell hash");
        TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "cast actor");
        TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]), "spell owner binding");
        TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]), "caster binding");
        TestAssert.Equal(missileId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]), "cast-created missile ID");
        TestAssert.Equal((byte)0, bytes[19], "rank-one child");
        TestAssert.Equal((byte)0, bytes[56], "ground child has no unit target binding");
        TestAssert.Near(castTime, BinaryPrimitives.ReadSingleLittleEndian(bytes[57..]), 1e-6, "normal child windup");
        TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(bytes[61..]), 0, "no extra cast time");
        TestAssert.Near(castTime, BinaryPrimitives.ReadSingleLittleEndian(bytes[65..]), 1e-6,
            "child uses the existing cast-time total-time policy");
        TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(bytes[69..]), 0, "no child cooldown");
        TestAssert.Equal(flags, bytes[77], "force-casting flags");
        TestAssert.Equal(slot, bytes[78], "absolute extra slot");
        TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(bytes[79..]), 0, "no child mana charge");
        TestAssert.Near(hero.Position.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[83..]), 1e-3, "caster launch X");
        TestAssert.Near(hero.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[91..]), 1e-3, "caster launch Z");
    }

    private static void AssertB6CastTrack(string spell, int index, uint flags, uint type, string particle, string bone)
    {
        // SpellCastInfo carries no FX bone: the selected shipped .fx owns it.
        var file = File.ReadAllBytes(GameDataPaths.Default.Data($"Spells/{spell}.fx"));
        var record = file.AsSpan(index * 588, 588);
        TestAssert.Equal(flags, BinaryPrimitives.ReadUInt32LittleEndian(record));
        TestAssert.Equal(type, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        TestAssert.Equal(particle, System.Text.Encoding.ASCII.GetString(record.Slice(16, 64)).Split('\0')[0]);
        TestAssert.Equal(bone, System.Text.Encoding.ASCII.GetString(record.Slice(80, 64)).Split('\0')[0]);
    }

    [Check("spells-ezreal-nominal-launch-pose")]
    public static void EzrealQWUseNominalHandPoseAcrossSkinsAndFacing()
    {
        // Exercise native child casting across every retained skin/facing case.
        foreach (var skin in Enumerable.Range(0, 5))
            foreach (var origin in new[]
            {
                new Position2(7000.0, 7000.0), new Position2(10000.0, 10000.0),
            })
                foreach (var facing in new[]
                {
                    new Position2(1.0, 0.0), new Position2(0.0, 1.0),
                    new Position2(-1.0, 0.0), new Position2(0.0, -1.0),
                })
                    foreach (var slot in new[] { 0, 1 })
                    {
                        AssertEzrealLaunchPose(skin, origin, facing, slot);
                    }
    }

    [Check("spells-ezreal-r-active-flight-fog")]
    public static void EzrealREntersVisionWithoutRevealingCasterOrDuplicatingOwnerCast()
    {
        using var match = new Match("Ezreal", Team.Order);
        var runtime = match.Runtime;
        var caster = match.Hero(0);
        var observerHero = match.Hero(1);
        caster.Position = new Position2(5000, 5000);
        observerHero.Position = new Position2(7200, 7000);
        runtime.Delivery.Flush();
        match.Clear();

        match.CastGround(0, observerHero.Position, slot: 3);
        var missileId = EzrealFlights.Of(runtime.Spells, caster).Select(EzrealFlights.MissileId).SingleOrDefault();
        TestAssert.True(missileId != 0, "Ezreal R allocates one missile identity");
        TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0xBD),
            "owner receives one R cast answer");
        TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0xBD),
            "hidden enemy does not receive R cast answer from fog");
        match.Clear();

        var data = runtime.GameData.Spell("EzrealTrueshotBarrage", 3);
        var castTime = data.CastTime;
        runtime.Tick(castTime + 0.01);
        runtime.Delivery.Flush();
        match.Clear();
        for (var tick = 0; tick < 60 &&
             !match.Captures[1].Packets.Any(packet => Opcode(packet) == 0x41); tick++)
        {
            // Keep each poll to one simulation step. MatchRuntime subdivides
            // larger advances and flushes between substeps, so a first
            // visible packet must be compared with the state from this step.
            runtime.Tick(1.0 / 30.0);
            runtime.Delivery.Flush();
        }

        var enemyMissiles = match.Captures[1].Packets.Where(packet => Opcode(packet) == 0x41).ToArray();
        TestAssert.Equal(1, enemyMissiles.Length,
            "a known hidden caster delivers one R missile when its current point enters vision");
        TestAssert.Equal(0, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x41),
            "owner 0xBD path is not duplicated by active R refresh");
        TestAssert.False(match.Captures[1].Packets.Any(packet => Opcode(packet) is 0xC2 or 0xEE),
            "R presentation does not reveal the hidden caster actor");
        var active = enemyMissiles[0];
        TestAssert.True(BinaryPrimitives.ReadSingleLittleEndian(active.Plaintext.Span[5..]) > 5000.0f,
            "late R packet carries the live missile position");
        var authoritative = EzrealFlights.InFlight(runtime.Spells, caster).Cast<SharedProjectileSnapshot?>()
            .FirstOrDefault(missile => missile!.Value.MissileNetId == missileId)?.Position;
        TestAssert.True(authoritative is not null,
            "the late R packet corresponds to a live authoritative missile");
        TestAssert.Near(authoritative!.Value.X,
            BinaryPrimitives.ReadSingleLittleEndian(active.Plaintext.Span[5..]), 1e-3,
            "late R packet X matches the current authoritative missile");
        TestAssert.Near(authoritative.Value.Z,
            BinaryPrimitives.ReadSingleLittleEndian(active.Plaintext.Span[13..]), 1e-3,
            "late R packet Z matches the current authoritative missile");
        TestAssert.True(BinaryPrimitives.ReadSingleLittleEndian(active.Plaintext.Span[0x5d..]) > 0.0f,
            "late R packet carries elapsed flight life");

        // The first visible snapshot creates the client missile.  A later
        // active-flight tick must keep that object alive without repeating
        // the 0x41 creation before the authoritative endpoint.
        runtime.Tick(0.1);
        runtime.Delivery.Flush();
        TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x41),
            "visible R flight remains one missile after an extra tick");

        caster.Alive = false;
        runtime.Tick(0.1);
        runtime.Delivery.Flush();
        TestAssert.True(EzrealFlights.InFlight(runtime.Spells, caster).Any(missile => missile.MissileNetId == missileId),
            "R remains authoritative after caster death");
        runtime.Tick(data.CastRange / data.MissileSpeed + 0.5);
        runtime.Delivery.Flush();
        var destroys = match.Captures.SelectMany(capture => capture.Packets)
            .Where(packet => packet.Plaintext.Length >= 5 && Opcode(packet) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missileId)
            .ToArray();
        TestAssert.Equal(2, destroys.Length,
            "after-death R retirement reaches both observers with the original missile ID");
    }

    private static void AssertEzrealLaunchPose(int skin, Position2 origin,
        Position2 facing, int slot)
    {
        using var match = new Match("Ezreal", Team.Order, skinId: skin);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var target = match.Hero(1);
        hero.Position = origin;
        hero.Facing = facing;
        target.Position = hero.Position + new Position2(facing.X * 300.0, facing.Z * 300.0);
        target.MaxHealth = target.Health = 5000.0;
        runtime.Delivery.Flush();
        match.Clear();

        var healthBefore = target.Health;
        match.Cast(0, slot, target);
        var missileId = EzrealFlights.Of(runtime.Spells, hero).Select(EzrealFlights.MissileId).SingleOrDefault();
        TestAssert.True(missileId != 0, $"Ezreal skin {skin} slot {slot} allocates missile");
        TestAssert.False(HasMissileCreate(match),
            $"Ezreal skin {skin} slot {slot} has no missile before launch delay");
        runtime.Tick(0.24);
        runtime.Delivery.Flush();
        TestAssert.False(HasMissileCreate(match),
            $"Ezreal skin {skin} slot {slot} has no missile before 0.25 seconds");
        runtime.Tick(0.03);
        runtime.Delivery.Flush();
        // VFX sweep F1/F2, 27 Sept 2026: the child's native L_hand launch
        // supersedes the manually sampled initial 0x41 pose.
        var hash = slot == 0 ? 0x05FE9CE5u : 0x0FA2B315u;
        foreach (var capture in match.Captures)
        {
            AssertB6ChildCast(capture.Packets.Single(candidate => IsB6Child(candidate, hash)),
                hero, hash, (byte)(slot == 0 ? 42 : 44), slot == 0 ? .251 : .25, missileId);
            TestAssert.False(capture.Packets.Any(candidate => Opcode(candidate) is 0x41 or 0xB8));
        }
        runtime.Tick(1); runtime.Delivery.Flush();
        TestAssert.True(target.Health < healthBefore, "native launch keeps gameplay impact across skins/facing");
        foreach (var capture in match.Captures)
            TestAssert.Equal(1, capture.Packets.Count(candidate => IsB6Retirement(candidate, missileId)),
                "native launch retires its original child missile once per observer");
    }

    private static bool HasMissileCreate(Match match) =>
        match.Captures.SelectMany(capture => capture.Packets).Any(candidate =>
            candidate.Plaintext.Span.Length > 0 && candidate.Plaintext.Span[0] == 0x41);

    private static void AssertPosition(PlaintextPacket packet, int offset,
        Position3 expected, string label)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Near(expected.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
            1e-3, label + " X");
        TestAssert.Near(expected.Y, BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
            1e-3, label + " Y");
        TestAssert.Near(expected.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]),
            1e-3, label + " Z");
    }
}
