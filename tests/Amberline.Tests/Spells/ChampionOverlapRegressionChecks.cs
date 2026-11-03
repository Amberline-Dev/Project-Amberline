using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-q-overlap-cast-completion")]
    public static void OverlappingQCastCompletion()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var separation in new[] { 0.0, 1.0, 30.0, 150.0, 800.0 })
        foreach (var batched in new[] { false, true })
        {
            using var match = new Match("LeeSin", team, extraEnemy: true);
            var hero = match.Hero(0); var victim = match.Hero(1);
            hero.Facing = new Position2(1, 0);
            victim.Position = hero.Position + new Position2(separation, 0);
            match.Hero(2).Position = hero.Position + new Position2(separation + 200, 0);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 0, victim);
            match.Runtime.Tick(.1);
            TestAssert.False(match.Captures[0].Packets.Any(IsCastCompletion), "Q keeps its .25 windup");
            if (batched) match.Runtime.Tick(1);
            else for (var tick = 0; tick < 30; tick++) match.Runtime.Tick(1.0 / 30);
            foreach (var capture in match.Captures)
            {
                var packets = capture.Packets;
                var finish = packets.Single(IsCastCompletion);
                var cast = packets.Single(packet => Opcode(packet) == 0xBD);
                var destroy = packets.Single(packet => Opcode(packet) == 0x5D);
                TestAssert.Equal(6, finish.Plaintext.Length, "native cast-completion size");
                TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(finish.Plaintext.Span[1..]));
                TestAssert.True(packets.IndexOf(cast) < packets.IndexOf(finish) &&
                    packets.IndexOf(finish) < packets.IndexOf(destroy),
                    "consume pending cast before missile retirement, including one-frame delivery (19 October 2026: the cast packet creates " +
                    "the missile on the client, so there is no separate launch packet between the finish and the retire)");
                foreach (var packet in new[] { cast, finish, destroy })
                {
                    TestAssert.Equal((byte)3, packet.Channel);
                    TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
                }
                TestAssert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(cast.Plaintext.Span[28..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(destroy.Plaintext.Span[1..]), "prediction identity retires");
                var hits = packets.Where(packet => Opcode(packet) == 0x68).Select(Decode).ToArray();
                TestAssert.Equal(1, hits.Length, "one impact even while overlapping");
                TestAssert.Equal(victim.NetId, hits[0].GetProperty("target_netid").GetUInt32());
            }
        }
    }

    private static bool IsCastCompletion(PlaintextPacket packet) =>
        Opcode(packet) == 0x39 && packet.Plaintext.Length == 6 && packet.Plaintext.Span[5] == 7;

    [Check("spells-live-revenant-owner-spacing")]
    public static void RevenantOwnerSpacing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ranged in new[] { false, true })
            VerifyRevenantSpacing(team, ranged);
    }

    private static void VerifyRevenantSpacing(Team team, bool ranged)
    {
        using var match = new Match("Yorick", team, ally: ranged);
        var runtime = match.Runtime; var owner = match.Hero(0); var enemy = match.Hero(1);
        var source = ranged ? match.Hero(2) : owner;
        if (ranged) { source.AttackRange = 550; source.MissileSpeed = 1400; source.Position = owner.Position; }
        enemy.Position = owner.Position + new Position2(350, 0);
        match.Cast(0, 3, source); runtime.Tick(.3);
        var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
        TestAssert.True(clone.Buffs.Any(buff => buff.Name == "YorickRAPetBuff2" && buff.Payload.Ghosted),
            "shipped controller grants ghosted movement");
        foreach (var capture in match.Captures)
            TestAssert.True(capture.Packets.Any(packet => IsGhostedStats(packet, clone.NetId)),
                "both clients receive ghosted action state");
        var ownerGoal = enemy.Position + new Position2(-130, 0);
        match.Move(0, ownerGoal);
        for (var tick = 0; tick < 50; tick++)
        {
            runtime.Pets.Command(owner, 5, enemy.Position, enemy.NetId);
            runtime.Tick(.1);
        }
        TestAssert.True(Distance(owner.Position, ownerGoal) < 10, "owner reaches attack position through clone");
        TestAssert.Equal(enemy.NetId, clone.AttackTarget,
            "repeated combat orders retain the revenant's enemy target");
        TestAssert.True(runtime.Combat.CanAttack(clone, enemy),
            $"combat pursuit reaches attack range even when the owner shares that range: ranged={ranged}, owner={owner.Position}, clone={clone.Position}");
        TestAssert.True(match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x68) >= 2,
            "spacing and repeated orders do not starve attacks");
        // A late observer must not turn a ghosted clone back into a blocker.
        runtime.Session.Participants[1].View.Reset(); match.Clear(); runtime.Delivery.Flush();
        TestAssert.True(match.Captures[1].Packets.Any(packet => IsGhostedStats(packet, clone.NetId)),
            "visibility replay preserves ghosted collision state");
        // Batch 146: the pet runs its shipped script now, so the fight ends the script's way.
        // Pet.lua's OnTargetLost is answered for a target that died or can no longer be attacked
        // (the engine's TargetStillValid), and the old machine's teardown only looked like that
        // because it read the clone's own AttackTarget: a hero that merely walks away keeps the pet
        // engaged at any distance (PET-02), and the engine holds the target in its pet state, so
        // writing the wire field no longer releases it. A protected hero is the script-visible loss
        // both teams share (sight is team-side and would keep only one of them engaged).
        enemy.Protected = true;
        SendOrder(match, 10, owner.Position, 0);
        clone.Position = owner.Position;
        clone.Waypoints.Clear(); clone.Target = null;
        for (var tick = 0; tick < 12; tick++) runtime.Tick(.1);
        // owner decision 28 Sept 2026: original game values
        var finalGap = Distance(clone.Position, owner.Position);
        var clearance = runtime.Movement.BodyClearance(clone, owner);
        TestAssert.True(finalGap >= clearance - 2.0 && finalGap <= 200.0,
            $"idle overlap recovers body clearance within the retail return radius: " +
            $"team={team}, ranged={ranged}, gap={finalGap}, clearance={clearance}, " +
            $"state={runtime.Pets.StateOf(clone)}, route={string.Join(';', clone.Waypoints)}, " +
            $"owner={owner.Position}, clone={clone.Position}");
        TestAssert.True(clone.Waypoints.Count == 0 && clone.Target is null,
            $"idle clone settles without a stale combat route: team={team}, ranged={ranged}");
    }

    private static bool IsGhostedStats(PlaintextPacket packet, uint id) =>
        Opcode(packet) == 0xCC && packet.Plaintext.Length == 111 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == id &&
        (BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[35..]) & (1u << 10)) != 0;

    [Check("spells-live-revive-fountain-acknowledgement")]
    public static void ReviveFountainAcknowledgement()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in new[] { 0, 1 })
        foreach (var noCooldown in new[] { false, true })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.SummonerSpells[slot] = "SummonerRevive";
            hero.CooldownsDisabled = noCooldown;
            hero.SpellCooldowns[0] = 12;
            runtime.Combat.Kill(hero, enemy.NetId); runtime.Combat.ScheduleRespawn(hero);
            runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, slot, hero, summoner: true);
            var spawn = team == Team.Order ? new Position2(25.902751922607422, 263.5519104003906) : new Position2(13933.7890625, 14170.09375); // Order: exact __Spawn_T1.SCB point (owner decision, 18 October 2026)
            // Restated in batch 263: the respawn's teleport goes to that point and its one-point path seats the hero on the point's lattice
            // anchor at once (the client's path install), where the Revive cast then starts.
            var fountain = runtime.Movement.ClientAnchor(spawn);
            TestAssert.Equal(fountain, hero.Position);
            TestAssert.True(hero.Alive && hero.RespawnAt == 0, "normal immediate respawn");
            TestAssert.Equal(12.0, hero.SpellCooldowns[0], "Revive does not reset champion cooldowns");
            // Owner decision 16 Oct 2026: Revive's base cooldown is 540 (the shipped data says 510).
            TestAssert.Equal(noCooldown ? 0.0 : 540.0, hero.SummonerCooldowns[slot]);
            var packets = match.Captures[0].Packets;
            var respawn = packets.Single(packet => Opcode(packet) == 0x4D);
            var cast = packets.Single(packet => Opcode(packet) == 0xBD);
            TestAssert.True(packets.IndexOf(respawn) < packets.IndexOf(cast), "lifecycle precedes cast acknowledgement");
            var info = Decode(cast).GetProperty("cast_info");
            foreach (var field in new[] { "launch_pos", "target_pos", "target_pos_end" })
            {
                TestAssert.Near(fountain.X, info.GetProperty(field)[0].GetDouble(), .001, field + " X");
                TestAssert.Near(fountain.Z, info.GetProperty(field)[2].GetDouble(), .001, field + " Z");
            }
            var cooldown = packets.Where(packet => Opcode(packet) == 0x8A).Select(Decode)
                .Single(fields => fields.GetProperty("is_summoner").GetBoolean());
            TestAssert.Equal(slot, cooldown.GetProperty("slot").GetInt32());
            TestAssert.Equal(noCooldown ? 0.0 : 540.0, cooldown.GetProperty("cooldown").GetDouble());
            TestAssert.Equal(noCooldown ? 0.0 : 540.0, info.GetProperty("cooldown").GetDouble(),
                "the cast answer carries the same HUD cooldown");
            runtime.Tick(.3);
            TestAssert.Equal(fountain, hero.Position, "no stale movement returns the hero to the corpse");
        }
    }
}
