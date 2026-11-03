using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-annie-tibbers-stun-attack-gate")]
    public static void AnnieStunStopsTibbersBasicAttacksUntilExpiry() =>
        RunPetStunAttackGate("Annie");

    [Check("spells-live-annie-shaco-clone-stun-attack-gate")]
    public static void AnnieStunStopsShacoCloneBasicAttacksUntilExpiry() =>
        RunPetStunAttackGate("Shaco");

    [Check("spells-live-annie-yorick-ghoul-stun-attack-gate")]
    public static void AnnieStunStopsYorickGhoulBasicAttacksUntilExpiry() =>
        RunPetStunAttackGate("Yorick");

    private static void RunPetStunAttackGate(string scenario)
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(scenario, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var caster = match.Hero(1); // The opposing Annie is the real CC producer.
            runtime.Tick(10.1); // Let the production opening protection expire.
            // The documented ready Pyromania state: the fifth stack's Pyromania_particle buff (script engine, 19 October 2026).
            runtime.Buffs.AddBuff(caster, "Pyromania_particle", Amberline.Core.Buffs.BuffType.CombatEnhancer, 25000.0, caster.NetId);

            var pet = SpawnPet(scenario, match, owner);
            runtime.Delivery.Flush();
            match.Clear();

            // Keep the real producer inside Q range and the pet inside its
            // normal attack range. No manual Stun is inserted in this check.
            caster.Position = pet.Position + new Position2(40.0, 0.0);
            if (scenario == "Shaco")
            {
                // Put the clone on the owner-ring boundary so its real attack
                // order does not spend the opening scan on flank correction.
                owner.Position = pet.Position + new Position2(-200.0, 0.0);
                owner.Waypoints.Clear();
                owner.Target = null;
                owner.AttackTarget = null;
                owner.AnchorPath = [owner.Position];
                runtime.Pets.Command(owner, 5, caster.Position, caster.NetId, pet.NetId);
            }
            var launchedBeforeStun = 0;
            for (var tick = 0; tick < 40 && launchedBeforeStun == 0; tick++)
            {
                match.Clear();
                runtime.Tick(0.1);
                launchedBeforeStun = CountPetBasicAttacks(match, pet.NetId, caster.NetId);
            }
            TestAssert.True(launchedBeforeStun > 0,
                $"{team} {scenario}: spawned pet must dispatch a real basic attack before CC");

            match.Clear();
            match.Cast(1, 0, pet);
            runtime.Tick(1.0); // Q impact applies Pyromania's real Stun.
            TestAssert.True(pet.Buffs.Any(buff => buff.Name == "Stun"),
                $"{team} {scenario}: Annie Q must apply its real Stun to the pet");
            pet.AttackCooldown = 0.0;
            TestAssert.True(pet.Alive, $"{team} {scenario}: stunned pet remains alive for the control window");
            TestAssert.True(runtime.Combat.CanAttack(pet, caster),
                $"{team} {scenario}: stunned pet still has a living, targetable enemy in basic-attack range");

            var activeTicks = 0;
            var launchedWhileStunned = 0;
            while (pet.Buffs.FirstOrDefault(buff => buff.Name == "Stun") is { } stun &&
                   stun.ExpiresAt > 0.11 && activeTicks < 40)
            {
                match.Clear();
                runtime.Tick(0.1);
                launchedWhileStunned += CountPetBasicAttacks(match, pet.NetId, caster.NetId);
                activeTicks++;
            }
            TestAssert.True(activeTicks > 0,
                $"{team} {scenario}: real Annie Stun must remain active for an observable interval");
            TestAssert.Equal(0, launchedWhileStunned,
                $"{team} {scenario}: pet AI must emit no new basic-attack packet while Annie Stun is active");

            var expiry = pet.Buffs.FirstOrDefault(buff => buff.Name == "Stun")?.ExpiresAt ?? 0.0;
            if (expiry > 0.0)
            {
                match.Clear();
                runtime.Tick(expiry + 0.01); // Separate the expiry transition from active sampling.
            }
            TestAssert.False(pet.Buffs.Any(buff => buff.Name == "Stun"),
                $"{team} {scenario}: real Annie Stun must expire before pet resumes");

            var resumed = 0;
            for (var tick = 0; tick < 40 && resumed == 0; tick++)
            {
                match.Clear();
                runtime.Tick(0.1);
                resumed += CountPetBasicAttacks(match, pet.NetId, caster.NetId);
            }
            TestAssert.True(resumed > 0,
                $"{team} {scenario}: pet basic attacks must resume after the real Stun expires");
        }
    }

    private static Entity SpawnPet(string scenario, Match match, Entity owner)
    {
        var runtime = match.Runtime;
        var kind = scenario switch
        {
            "Annie" => EntityKind.Pet,
            "Shaco" => EntityKind.ShacoClone,
            "Yorick" => EntityKind.YorickGhoul,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        switch (scenario)
        {
            case "Annie":
                match.Cast(0, 3, owner);
                break;
            case "Shaco":
                match.Cast(0, 3, owner);
                break;
            case "Yorick":
                match.CastGround(0, owner.Position, 1);
                break;
        }
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var pet = runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.Kind == kind &&
                unit.OwnerNetId == owner.NetId);
            if (pet is not null) return pet;
            runtime.Tick(0.05);
        }
        throw new InvalidOperationException($"{scenario} pet did not register within the bounded cast window");
    }

    private static int CountPetBasicAttacks(Match match, uint petNetId, uint targetNetId)
    {
        // CombatPackets.BasicAttack writes this 15-byte wire opcode; the
        // attacker and target IDs occupy bytes 1..4 and 5..8 respectively.
        const byte opcode = 0x0D;
        return match.Captures.SelectMany(capture => capture.Packets).Count(packet =>
            packet.Plaintext.Length == 15 && packet.Plaintext.Span[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == petNetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == targetNetId);
    }
}
