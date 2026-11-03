using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-shaco-tibbers-yorick-hard-move-return-stop-attack")]
    public static void PetHardMoveAndReturnStopAttackBeforeWaypoints()
    {
        foreach (var champion in new[] { "Shaco", "Yorick", "Annie" })
        foreach (var order in new byte[] { 6, 9 })
        {
            using var match = new Match(champion, Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            Entity pet;
            if (champion == "Shaco")
                pet = SpawnHallucinateClone(match, owner);
            else
            {
                if (champion == "Annie") match.CastGround(0, owner.Position, 3);
                else match.Cast(0, 3, owner);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit => unit.OwnerNetId == owner.NetId &&
                    unit.Kind == (champion == "Annie" ? EntityKind.Pet : EntityKind.YorickRevenant));
            }
            var label = $"{champion}/order {order}";
            owner.Position = new Position2(7000, 7000);
            pet.Position = new Position2(7500, 7000);
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            pet.AttackCooldown = 0;
            enemy.Position = new Position2(7600, 7000);
            runtime.Delivery.Flush();
            TestAssert.True(SendPetInput(match, pet, 5, enemy.Position, enemy.NetId),
                $"{label}: real pet attack input accepted");
            runtime.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget, label);
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId &&
                hit.TargetNetId == enemy.NetId && hit.WindupRemaining > 0),
                $"{label}: attack has an unlaunched windup");
            runtime.Delivery.Flush();
            match.Clear();

            TestAssert.True(SendPetInput(match, pet, order, owner.Position),
                $"{label}: real move/return input accepted");
            TestAssert.Equal((uint?)null, pet.AttackTarget, $"{label}: target cleared");
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId),
                $"{label}: unlaunched windup cancelled");
            var packets = match.Captures[0].Packets;
            var stop = packets.FindIndex(packet => Opcode(packet) == 0x39 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == pet.NetId);
            var waypoint = packets.FindIndex(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == pet.NetId);
            TestAssert.True(stop >= 0 && waypoint > stop,
                $"{label}: actor 0x39 precedes replacement 0xC1 (stop={stop}, waypoint={waypoint})");
            var start = pet.Position;
            for (var tick = 0; tick < 6; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(Spacing(start, pet.Position) > 1 &&
                Spacing(pet.Position, owner.Position) < Spacing(start, owner.Position),
                $"{label}: movement proceeds toward the requested destination");
        }
    }

    [Check("pets-live-shaco-tibbers-yorick-reject-ordinary-click-echoes")]
    public static void OrdinaryHeroClickEchoLeavesCommandablePetsFollowing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Shaco", "Annie", "Yorick" })
        {
        using var match = new Match(champion, team);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(12000, 12000);
        Entity pet;
        if (champion == "Shaco")
            pet = SpawnHallucinateClone(match, owner);
        else if (champion == "Annie")
        {
            match.CastGround(0, owner.Position, 3);
            runtime.Tick(.5);
            pet = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
        }
        else
        {
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            pet = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickRevenant && unit.OwnerNetId == owner.NetId);
        }
        var label = $"{champion}/{team}";
        owner.Position = new Position2(7000, 7000);
        pet.Position = new Position2(6800, 7000);
        pet.Waypoints.Clear();
        pet.Target = null;
        enemy.Position = new Position2(8200, 7000);
        runtime.Delivery.Flush();

        TestAssert.True(SendPetInput(match, pet, 9, owner.Position),
            $"{label}: explicit pet return installs follow");
        // ORDER_PETHARDRETURN is AI_PET_HARDRETURN in Pet.lua's OnOrder.
        TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, match.Runtime.Pets.StateOf(pet)!.Value);
        var clicked = new Position2(7400, 7000);
        match.Move(0, clicked);
        // Pet.lua OnOrder answers true and changes nothing for a near MOVETO (600 units here, inside
        // FAR_MOVMEMENT_DISTANCE); the hand-written machine rejected the click at the wire.
        TestAssert.True(SendPetInput(match, pet, 2, clicked),
            $"{label}: a near mirrored move is swallowed and leaves the pet as it was");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, match.Runtime.Pets.StateOf(pet)!.Value,
            $"{label}: mirrored move leaves autonomous follow intact");

        var start = pet.Position;
        for (var tick = 0; tick < 75; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(Spacing(start, pet.Position) > 20,
            $"{label}: pet autonomously follows the moving owner after plain clicks");
        // Playtest B3 #7, 26 Sept 2026: Pet.lua's hard return becomes
        // AI_PET_IDLE inside the return radius, so the recalled pet resumes
        // normal attack/follow. Reversed from "order 9 survives".
        TestAssert.False(UnderHardOrder(match, pet),
            $"{label}: the returned pet resumes normal AI after a mirrored ordinary move");

        // An attack (ordinary or Alt) is legal only after this peer knows and
        // sees the target.
        enemy.Position = owner.Position;
        runtime.Delivery.Flush();
        var view = runtime.Session.Participants[0].View;
        TestAssert.True(view.Known.Contains(enemy.NetId) && view.Visible.Contains(enemy.NetId),
            $"{label}: explicit Alt attack target is visible to its owner");

        // Playtest B3 #7, 26 Sept 2026 (owner: as close to retail as
        // possible): Pet.lua OnOrder ORDER_ATTACKTO sends a normal pet after
        // the owner's target. Reversed from "a mirrored attack is ignored and
        // selects no target"; the mirrored ORDER_STOP stays a no-op.
        match.Attack(0, enemy);
        TestAssert.True(SendPetInput(match, pet, 3, enemy.Position, enemy.NetId),
            $"{label}: the owner's mirrored attack sends the normal pet after his target");
        // ORDER_ATTACKTO is AI_PET_ATTACK on the target in Pet.lua's OnOrder.
        TestAssert.Equal(PetLuaAiEngine.StatePetAttack, match.Runtime.Pets.StateOf(pet)!.Value);
        TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget,
            $"{label}: the mirrored attack selects the owner's target");
        TestAssert.False(SendPetInput(match, pet, 10, pet.Position),
            $"{label}: ordinary hero stop mirrored to a pet is not an Alt hold");
        TestAssert.Equal(PetLuaAiEngine.StatePetAttack, match.Runtime.Pets.StateOf(pet)!.Value,
            $"{label}: the mirrored stop leaves the pet's order unchanged");

        TestAssert.True(SendPetInput(match, pet, 6, clicked),
            $"{label}: explicit Alt move still commands the pet");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardMove, match.Runtime.Pets.StateOf(pet)!.Value);
        TestAssert.True(SendPetInput(match, pet, 5, enemy.Position, enemy.NetId),
            $"{label}: explicit Alt attack still commands the pet");
        TestAssert.Equal(enemy.NetId, pet.AttackTarget);
        TestAssert.True(SendPetInput(match, pet, 11, pet.Position),
            $"{label}: explicit Alt hold still commands the pet");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, match.Runtime.Pets.StateOf(pet)!.Value);
        TestAssert.True(SendPetInput(match, pet, 9, owner.Position),
            $"{label}: explicit Alt return still commands the pet");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, match.Runtime.Pets.StateOf(pet)!.Value);
        TestAssert.False(SendPetInput(match, pet, 6, clicked, connectionId: 2),
            $"{label}: another player cannot command this owned pet");
        }
    }

    private static bool SendPetInput(Match match, Entity pet, byte order,
        Position2 destination, uint targetNetId = 0, long connectionId = 1)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), pet.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(18), targetNetId);
        var accepted = false;
        match.Runtime.Session.Receive(connectionId, 1,
            match.Runtime.Cipher.Encrypt(request), context =>
                accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        return accepted;
    }
}
