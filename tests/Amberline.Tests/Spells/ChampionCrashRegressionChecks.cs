using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat-live-onhit-cancels-windup")]
    public static void OnHitCancelsWindup()
    {
        using var match = new Match("Blitzcrank", Team.Order);
        var blitz = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = blitz.Position + new Position2(150, 0);
        match.Cast(0, 2, blitz);
        blitz.AttackWindup = 0;
        blitz.MissileSpeed = 0;
        enemy.AttackWindup = 1;
        enemy.MissileSpeed = 0;
        var health = blitz.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(blitz, enemy));
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(enemy, blitz));
        match.Runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.True(enemy.ForcedMove is not null, "Power Fist applies the shipped knockup");
        TestAssert.Near(health, blitz.Health, 0, "the interrupted attack cannot hit");
        TestAssert.False(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == enemy.NetId),
            "on-hit cancellation persists after processing the queue");
    }

    [Check("combat-live-death-cancels-basic-windup")]
    public static void DeathCancelsQueuedBasicInProductionTick()
    {
        using var match = new Match("LeeSin", Team.Order);
        var runtime = match.Runtime;
        var killer = match.Hero(0);
        var victim = match.Hero(1);
        victim.Position = killer.Position + new Position2(150, 0);
        killer.AttackRange = victim.AttackRange = 1000;
        killer.AttackDamage = 10_000;
        killer.AttackWindup = killer.MissileSpeed = 0;
        victim.AttackWindup = .5;
        victim.MissileSpeed = 0;
        killer.AttackCooldown = victim.AttackCooldown = 0;
        killer.AttackPeriod = victim.AttackPeriod = 1;
        victim.Health = 1;
        match.Clear();

        var goldBefore = killer.Gold;
        var killsBefore = killer.ChampionKills;
        var deathsBefore = victim.ChampionDeaths;
        TestAssert.True(runtime.Combat.QueueBasicAttack(killer, victim, new AttackRolls(1, 1, 1)));
        TestAssert.True(runtime.Combat.QueueBasicAttack(victim, killer, new AttackRolls(1, 1, 1)));
        var victimHit = runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == victim.NetId);
        TestAssert.True(victimHit.WindupRemaining > 0 && victimHit.MissileNetId != 0,
            "the second basic is queued with a real positive-windup missile");
        runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.False(victim.Alive, "the first instant basic kills the second attacker");
        TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == victim.NetId),
            "the dead attacker's windup stays canceled after queue callbacks");
        TestAssert.Equal(killsBefore + 1, killer.ChampionKills, "one champion kill is credited");
        TestAssert.Equal(deathsBefore + 1, victim.ChampionDeaths, "one champion death is recorded");
        TestAssert.Near(goldBefore + 400, killer.Gold, 1e-9, "one hero bounty is awarded");

        runtime.Delivery.Flush();
        foreach (var capture in match.Captures)
        {
            TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x0D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[10..]) == victimHit.MissileNetId),
                "the observer received the canceled attack's preparation");
            TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x61 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == victim.NetId),
                "the victim's production death is delivered once");
            TestAssert.Equal(0, capture.Packets.Count(packet => Opcode(packet) == 0x68 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == killer.NetId),
                "the canceled basic never delivers damage to its target");
        }
    }

    [Check("stats-live-dead-equipment-health")]
    public static void DeadEquipmentHealth()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        match.Runtime.Combat.Kill(hero);
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Runtime.HeroStats.Refresh(hero);
        var previousMaximum = hero.MaxHealth;
        var inventory = hero.Inventory.ToArray();
        inventory[0] = new Amberline.Simulation.InventorySlot(1011);
        hero.Inventory = inventory;
        match.Runtime.HeroStats.Refresh(hero, push: true);
        TestAssert.True(hero.MaxHealth > previousMaximum, "Giant's Belt updates maximum health while dead");
        TestAssert.Near(0, hero.Health, 0, "equipment cannot restore a corpse's current health");
        TestAssert.False(hero.Alive);
        TestAssert.Near(0, match.Runtime.HeroStats.Snapshot(hero).CurrentHealth, 0);
    }

    [Check("vision-live-fountain-team-isolation")]
    public static void FountainLightingTeamIsolation()
    {
        using var match = new Match("LeeSin", Team.Order);
        foreach (var seat in match.Runtime.Session.Participants) seat.View.Reset();
        match.Runtime.Delivery.Flush();
        for (var seat = 0; seat < 2; seat++)
        {
            var bubbles = match.Captures[seat].Packets.Where(packet => Opcode(packet) == 0x7B).ToArray();
            // Batch 259: structure sight is the bubble the client's own structure objects make; the server sends no 0x7B for it.
            TestAssert.Equal(0, bubbles.Length, "no server position bubble for the allied structures");
            foreach (var packet in bubbles)
                TestAssert.Equal((uint)seat, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]),
                    "terrain packets belong to their actual recipient team");
            var extras = bubbles.Count(packet =>
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]);
                return id == 0x6FFF0000u || id is >= 0x50000180u and <= 0x50000191u;
            });
            TestAssert.Equal(0, extras, "neither team receives artificial fountain lighting");
        }
        var chaosProviders = VisibilityRules.BuildTeamProviders(match.Runtime.World, Team.Chaos,
            [], _ => []);
        // Batch 259: the fountain is lit by the team's shop (the shop: a 1350 bubble without IsUnit).
        var fountain = chaosProviders.First();
        TestAssert.Near(13360.6055, fountain.Position.X, .001, "Chaos owns its fountain sight, the Chaos shop's bubble");
        TestAssert.Near(1350, fountain.Radius, 0, "Chaos has ordinary persistent fountain sight");
        TestAssert.False(fountain.IsUnit, "the shop's bubble has no wall mask");
        TestAssert.False(chaosProviders.Any(provider => Distance(provider.Position, new Position2(26, 264)) < provider.Radius),
            "Chaos has no server sight provider in the Order fountain");
        var orderFountain = VisibilityRules.BuildTeamProviders(match.Runtime.World, Team.Order, [], _ => []).First();
        TestAssert.Near(1350, orderFountain.Radius, 0, "Order fountain sight is 1350 as well (owner decision, 18 October 2026)");
    }

    [Check("spells-live-clone-single-native-creation")]
    public static void CloneSingleNativeCreation()
    {
        foreach (var champion in new[] { "Yorick", "Shaco" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(champion, team, ally: true);
            var runtime = match.Runtime; var owner = match.Hero(0);
            var source = champion == "Yorick" ? match.Hero(2) : owner;
            match.Cast(0, 3, source); runtime.Tick(1);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind is EntityKind.YorickRevenant or EntityKind.ShacoClone);
            foreach (var capture in match.Captures) AssertOneCloneCreation(capture, clone, owner, source);

            // A fresh observer, including reconnect, must preserve native pet
            // ownership and clone-source identity as well as the model.
            match.Clear(); runtime.Session.Participants[1].View.Reset(); runtime.Delivery.Flush();
            AssertOneCloneCreation(match.Captures[1], clone, owner, source);
            TestAssert.False(match.Captures[0].Packets.Any(packet => IsCloneCreation(packet, clone.NetId)),
                "existing observers do not recreate the pet");
            match.Clear(); runtime.Delivery.Flush();
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                .Any(packet => IsCloneCreation(packet, clone.NetId)), "idle flush never creates a second object");

            runtime.Combat.Kill(clone, match.Hero(1).NetId); runtime.Tick(8.1);
            TestAssert.False(runtime.World.Entities.ContainsKey(clone.NetId), "dead clone is reaped once");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                .Any(packet => IsCloneCreation(packet, clone.NetId)), "teardown never recreates a dead clone");
        }
    }

    private static bool IsCloneCreation(PlaintextPacket packet, uint netId) =>
        packet.Plaintext.Length >= 9 && Opcode(packet) is 0x80 or 0x3C &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == netId;

    private static void AssertOneCloneCreation(Capture capture, Entity clone, Entity owner, Entity source)
    {
        var creation = capture.Packets.Single(packet => IsCloneCreation(packet, clone.NetId));
        var bytes = creation.Plaintext.Span;
        TestAssert.Equal((byte)0x3C, bytes[0], "one native pet constructor, no preceding generic minion");
        TestAssert.Equal(owner.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "pet owner");
        TestAssert.Equal(source.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[170..]), "independent clone source");
        var reveal = capture.Packets.First(packet => Opcode(packet) == 0xC2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == clone.NetId);
        TestAssert.True(capture.Packets.IndexOf(creation) < capture.Packets.IndexOf(reveal), "construct before reveal");
    }

    [Check("combat-live-overkill-health-replication")]
    public static void OverkillHealthReplication()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var victim = match.Hero(0); var killer = match.Hero(1);
            victim.Health = 10;
            runtime.Combat.ApplyHit(killer.NetId, victim.NetId, 100000, DamageType.True, DamageSourceKind.Proc);
            TestAssert.False(victim.Alive);
            TestAssert.Near(0, victim.Health, 0, "overkill leaves valid authoritative health");
            TestAssert.True(victim.RespawnAt > runtime.Combat.CurrentTime, "ordinary respawn is scheduled");
            runtime.HeroStats.Push(victim);
            runtime.Tick(2);
            runtime.Session.Participants[0].View.Reset(); runtime.Delivery.Flush();
            var snapshots = match.Captures.SelectMany(capture => capture.Packets)
                .Where(packet => Opcode(packet) == 0xCC && packet.Plaintext.Length == 171 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == victim.NetId)
                .Select(Decode).ToArray();
            TestAssert.True(snapshots.Length > 0, "full hero snapshots exercised after death and reconnect");
            foreach (var snapshot in snapshots)
                TestAssert.Near(0, snapshot.GetProperty("fields").GetProperty("mHP").GetDouble(), 0,
                    "the stock health invariant check must never receive a negative current HP");
        }
    }
}
