using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (17 Oct 2026), second pass of the stop-attack rule (<see cref="CombatPackets.StopAttack"/>, first written for lane
/// minions, <c>minions.n2-stop-attack-*</c>): a stop-attack (0x39) is sent only while the unit's client is in its attack state
/// (<see cref="Entity.ClientAttackArmed"/>; a hero always gets the stop it is asked for, because the stop also ends a channel),
/// and its flags follow what the server did with the hit: DestroyMissile alone (0x10) for a swing the server dropped before its
/// hit, nothing (0x00) for a hit it still applies, never ForceSpellCast + DestroyMissile (0x12). Each check runs a production
/// <c>MatchRuntime</c> and reads the bytes a client was sent.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const byte N2BasicAttackOpcode = 0x0D;
    private const byte N2StopAttackOpcode = 0x39;
    private const byte N2DestroyClientMissileOpcode = 0x5D;
    private const byte N2CastOpcode = 0xBD;

    private static List<byte> N2Stops(Match match, int seat, Entity unit) => N2Stops(match.Captures[seat].Packets, unit);

    private static List<byte> N2Stops(IEnumerable<PlaintextPacket> packets, Entity unit) => packets
        .Where(packet => Opcode(packet) == N2StopAttackOpcode &&
                         BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == unit.NetId)
        .Select(packet => packet.Plaintext.Span[5]).ToList();

    private static int N2Swings(Match match, int seat, Entity unit) => match.Captures[seat].Packets.Count(packet =>
        Opcode(packet) == N2BasicAttackOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == unit.NetId);

    [Check("movement.n2-hero-stop-attack-says-what-the-server-did-with-the-hit")]
    public static void AHeroStopFollowsTheHit()
    {
        foreach (var how in new[] { "stop-order", "cast-approach", "detach" })
        foreach (var launched in new[] { false, true })
        {
            var label = $"{how}, {(launched ? "hit launched" : "mid-windup")}";
            using var match = new Match("Annie", Team.Order, extraEnemy: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0); var enemy = match.Hero(1); var far = match.Hero(2);
            // Disintegrate reaches 625: the second enemy is out of range, so a cast on it queues an approach.
            far.Position = hero.Position + new Position2(700, 0);
            hero.AttackTarget = enemy.NetId;
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy), label + ": the swing starts");
            if (launched) runtime.Combat.CombatTick(hero.AttackWindup + 0.01, regenerate: false);
            runtime.Delivery.Flush();
            TestAssert.Equal(1, N2Swings(match, 0, hero), label + ": the swing reached the client");
            var hit = runtime.Combat.PendingHits.Single(pending => pending.SourceNetId == hero.NetId);
            TestAssert.Equal(launched, hit.WindupRemaining <= 0.0, label + ": the hit is in the expected phase");
            match.Clear();

            List<byte> stops;
            switch (how)
            {
                case "stop-order":
                    SendHeroOrder(match, 10, hero.Position);
                    stops = N2Stops(match, 0, hero);
                    break;
                case "cast-approach":
                    match.Cast(0, 0, far);
                    stops = N2Stops(match, 0, hero);
                    break;
                default:
                    // The detach is built before the seat is released, with the hero's unfinished swing dropped first.
                    var detach = runtime.Session.DisconnectPackets(1);
                    TestAssert.Equal(2, detach.Count, label + ": a stop and a re-anchor");
                    stops = N2Stops(detach, hero);
                    break;
            }
            TestAssert.Equal(1, stops.Count, label + ": one stop-attack");
            TestAssert.Equal(launched ? CombatPackets.StandingSwingFlags : CombatPackets.CancelWindupFlags, stops[0],
                label + (launched
                    ? ": the hit stands: a client still in the swing's windup shows it, and the shot in flight is not destroyed"
                    : ": the dropped swing is not played and its shot never exists"));
            var pendingNow = runtime.Combat.PendingHits.Count(pending => pending.SourceNetId == hero.NetId);
            TestAssert.Equal(launched ? 1 : 0, pendingNow, label + ": the server kept or dropped the hit to match");
            if (launched)
                TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == N2DestroyClientMissileOpcode &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hit.MissileNetId),
                    label + ": a launched auto is never destroyed");
            if (launched)
            {
                var health = enemy.Health;
                runtime.Combat.CombatTick(1.0, regenerate: false);
                TestAssert.True(enemy.Health < health, label + ": the launched hit lands");
            }
        }
    }

    [Check("spells-n2-a-cast-tells-the-client-about-a-swing-the-server-dropped")]
    public static void ACastFollowsTheStopOfADroppedSwing()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0); var enemy = match.Hero(1);
        hero.AttackTarget = enemy.NetId;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy), "the swing starts");
        runtime.Delivery.Flush();
        match.Clear();
        // A spell that drops the swing by itself (the champion code interrupts the attack, then casts) and has no order to stop.
        TestAssert.True(runtime.Combat.InterruptBasicAttack(hero) > 0, "the server drops the swing");
        hero.AttackTarget = null;
        match.Cast(0, 0, enemy);

        var order = match.Captures[0].Packets.Select(Opcode).ToList();
        var stops = N2Stops(match, 0, hero);
        TestAssert.Equal(1, stops.Count, "the cast announced the dropped swing once");
        TestAssert.Equal(CombatPackets.CancelWindupFlags, stops[0], "DestroyMissile alone: nothing of the swing is played");
        TestAssert.True(order.IndexOf(N2StopAttackOpcode) >= 0 && order.IndexOf(N2StopAttackOpcode) < order.IndexOf(N2CastOpcode),
            "the stop comes before the cast, which would otherwise force the swing's cast frame");
    }

    [Check("pets.n2-stop-attack-by-unit-class")]
    public static void PetsCloneAndGhoulStopsFollowTheHit()
    {
        foreach (var kind in new[] { EntityKind.Pet, EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.YorickGhoul })
        foreach (var via in kind == EntityKind.YorickGhoul ? new[] { "scan" } : new[] { "pet-command", "owner-hold" })
        foreach (var launched in new[] { false, true })
        {
            var label = $"{kind} {via}, {(launched ? "hit launched" : "mid-windup")}";
            using var match = new Match(kind == EntityKind.Pet ? "Annie" : "Yorick", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0); var enemy = match.Hero(1);
            var spawnAt = new Position2(7000, 7150);
            enemy.Position = new Position2(7100, 7150);
            Entity pet;
            if (kind == EntityKind.Pet)
            {
                pet = AnnieChecks.SummonTibbers(runtime, owner, 1, spawnAt);
            }
            else if (kind == EntityKind.YorickGhoul)
                pet = YorickScriptSupport.SpawnUnit(runtime,owner, "Spectral", spawnAt, 0);
            else
            {
                var cloneKind = kind == EntityKind.ShacoClone ? CloneKind.ShacoClone : CloneKind.YorickRevenant;
                var buff = kind == EntityKind.ShacoClone ? "HallucinateFull" : "YorickRAPetBuff2";
                pet = runtime.Clones.Spawn(owner, owner, spawnAt, cloneKind, 500, buff, 30, "Pet");
                // The controller buff is the clone's life; the order it carried is the script's now
                // (Pet.lua's OnOrder), so there is nothing left to seed in its payload.
                runtime.SpellBuffs.Add(pet, buff, BuffType.Internal, 30, owner.NetId, new BuffPayload());
            }
            pet.Position = spawnAt;
            // These actors are already known to the observing clients; this checks the stop, not the spawn encoding.
            foreach (var seat in runtime.Session.Participants)
            {
                seat.View.Known.Add(pet.NetId);
                seat.View.Visible.Add(pet.NetId);
            }
            runtime.Delivery.Flush();
            match.Clear();

            // The owner moves the pet (pets.command, order 6) or holds it (Pet.lua OnOrder mirrors the owner's hold, order 1).
            void Command(Position2 to)
            {
                if (kind == EntityKind.YorickGhoul)
                {
                    // A hero in the ghoul's way is untargetable: the next scan lets go of the target.
                    enemy.Protected = true;
                    runtime.Pets.Tick(10.0, 0.1);
                }
                else if (via == "pet-command")
                    TestAssert.True(kind == EntityKind.Pet
                        ? runtime.Pets.Command(owner, 6, to, actorNetId: pet.NetId)
                        : runtime.Pets.Command(owner, 6, to, actorNetId: pet.NetId), label + ": order");
                else
                    // Pet.lua's OnOrder mirrors the owner's hold (a pet-actor 0x75 of order 1) onto
                    // the pet whichever family it is.
                    TestAssert.True(runtime.Pets.Order(owner, pet.NetId, 1, owner.Position),
                        label + ": order");
                runtime.Delivery.Flush();
            }

            // A pet that never attacked has no attack state to end, even with a target to chase.
            if (kind != EntityKind.YorickGhoul)
            {
                pet.AttackTarget = enemy.NetId;
                Command(spawnAt + new Position2(0, -300));
                TestAssert.Equal(0, N2Stops(match, 0, pet).Count, label + ": an idle pet is not told to stop");
                match.Clear();
            }

            pet.AttackTarget = enemy.NetId;
            pet.AttackCooldown = 0.0;
            TestAssert.True(runtime.Combat.QueueBasicAttack(pet, enemy), label + ": the swing starts");
            if (launched) runtime.Combat.CombatTick(pet.AttackWindup + 0.01, regenerate: false);
            runtime.Delivery.Flush();
            TestAssert.Equal(1, N2Swings(match, 0, pet), label + ": the swing reached the client");
            TestAssert.Equal(launched, runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == pet.NetId).WindupRemaining <= 0.0,
                label + ": the hit is in the expected phase");
            match.Clear();

            Command(spawnAt + new Position2(0, -300));
            var stops = N2Stops(match, 0, pet);
            TestAssert.Equal(1, stops.Count, label + ": one stop-attack for the attack");
            TestAssert.Equal(launched ? CombatPackets.StandingSwingFlags : CombatPackets.CancelWindupFlags, stops[0],
                label + (launched ? ": the hit stands: its cast frame is forced and its shot is not destroyed" : ": the dropped swing is not played"));
            TestAssert.False(pet.ClientAttackArmed, label + ": the client's attack state ended");
            TestAssert.Equal(launched ? 1 : 0, runtime.Combat.PendingHits.Count(hit => hit.SourceNetId == pet.NetId),
                label + ": the server kept or dropped the hit to match");

            // A second order finds the client already stopped.
            match.Clear();
            Command(spawnAt + new Position2(300, 0));
            TestAssert.Equal(0, N2Stops(match, 0, pet).Count, label + ": a stopped pet is not stopped again");
        }
    }

    [Check("spells-n2-a-stop-forces-the-cast-frame-of-a-standing-swing-and-never-of-a-spell")]
    public static void ForceSpellCastBelongsToAStandingSwingOnly()
    {
        // The server's hit launched a moment ago, so a client one latency behind may still be in the swing's windup. The S key then
        // forces the swing's cast frame (0x02) when the swing is the current SpellCaster, and carries neither 0x02 nor 0x10 when a
        // spell is: a forced cast frame would show a spell the server never cast.
        foreach (var current in new[] { "swing", "windup-spell", "recall" })
        {
            using var match = new Match("Annie", Team.Order);
            var runtime = match.Runtime;
            var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.AttackTarget = enemy.NetId;
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy), current + ": the swing starts");
            runtime.Combat.CombatTick(hero.AttackWindup + 0.01, regenerate: false);
            TestAssert.True(runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId).WindupRemaining <= 0.0,
                current + ": the hit has launched");
            if (current == "windup-spell") match.Cast(0, 0, enemy);
            else if (current == "recall") match.Cast(0, SpellSystem.RecallSlot, hero);
            runtime.Delivery.Flush();
            if (current != "swing")
                TestAssert.Equal(0u, hero.ClientSwingMissileNetId, current + ": the cast replaced the swing");
            match.Clear();

            SendHeroOrder(match, 10, hero.Position);
            if (current is "recall" or "windup-spell")
            {
                // Batch 47 (a84f249) for Recall, the windup gate for every spell (SpellWindup): the S key inside an uncancellable
                // windup (Recall 0.5 s, Disintegrate 0.25 s) is held and applied 0.1 s after the windup, so the stop reaches a
                // client whose own windup is over.
                TestAssert.Equal(0, N2Stops(match, 0, hero).Count, current + ": the S key inside the windup is held");
                if (current == "recall")
                    TestAssert.True(runtime.Spells.RecallRemaining(hero) > 0.0, "recall: still channelling while the stop is held");
                runtime.Tick(0.7);
                if (current == "recall")
                    TestAssert.Near(0.0, runtime.Spells.RecallRemaining(hero), 0.0, "recall: the held S key ends the recall");
            }
            var stops = N2Stops(match, 0, hero);
            TestAssert.True(stops.Count >= 1, current + ": the S key stops the hero");
            if (current == "swing")
                TestAssert.Equal([CombatPackets.StandingSwingFlags], stops.ToArray(),
                    "the launched swing is the current SpellCaster: its cast frame is forced if the client has not reached it");
            else
                TestAssert.False(stops.Any(flags => (flags & (CombatPackets.StandingSwingFlags | CombatPackets.CancelWindupFlags)) != 0),
                    $"{current} is the current SpellCaster: nothing is forced and nothing destroyed (" +
                    string.Join(",", stops.Select(flags => $"0x{flags:X2}")) + ")");
        }
    }
}
