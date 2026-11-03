using Amberline.Core;
using System.Buffers.Binary;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-live-shaco-box-owner-death")]
    public static void ShacoBoxDamagesAfterOwnerDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var target = match.Hero(1);
            runtime.HeroStats.Refresh(target);
            target.Health = target.MaxHealth;
            match.Cast(0, 1, target);
            runtime.Tick(0.5);
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            var spawns = match.Captures.SelectMany(capture => capture.Packets)
                .Where(packet => Opcode(packet) == 0x80 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == box.NetId).ToArray();
            TestAssert.True(spawns.Length > 0, "the box must be created on a real observer");
            TestAssert.True(spawns.All(packet =>
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[30..]) ==
                    (team == Team.Order ? 100u : 200u)),
                "box creation uses native team IDs for either side");
            target.Position = box.Position + new Position2(250, 0);
            if (launched)
            {
                for (var tick = 0; tick < 120 && !HasLaunchedShot(); tick++) runtime.Tick(1.0 / 30.0);
                TestAssert.True(HasLaunchedShot(), "owner must die while a box missile is in flight");
            }
            runtime.Combat.Kill(owner, target.NetId);
            runtime.Delivery.Flush();
            match.Clear();
            for (var tick = 0; tick < (launched ? 30 : 90); tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.False(owner.Alive);
            TestAssert.True(box.Alive, "owner death must leave the armed box active");
            var damage = match.Captures.SelectMany(capture => capture.Packets)
                .Where(packet => Opcode(packet) == 0x68 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == target.NetId)
                .ToArray();
            TestAssert.True(damage.Length > 0, "box shots retain damage and owner attribution after Shaco dies");
            TestAssert.True(damage.All(packet => BitConverter.ToSingle(packet.Plaintext.Span[14..]) > 0));
            TestAssert.Near(0, owner.Health, 1e-9, "posthumous box damage cannot heal the corpse");

            bool HasLaunchedShot() => runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true });
        }
    }

    [Check("pets-live-ghoul-death-clears-orders")]
    public static void GhoulDeathClearsOrders()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(7600, 7000);
            var now = runtime.Combat.CurrentTime;
            foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
            {
                var ghoul = YorickScriptSupport.SpawnUnit(runtime, owner, kind, owner.Position, now);
                // The YorickSummon* script's taunt sends the ghoul at the hero; under the shipped
                // YorickPHPet that chase is the hard-attack order the taunt resolves to
                // (AI_PET_HARDATTACK closes on its target), which is where the pet AI under test
                // starts. Before: a direct AttackTarget write the hand-written ghoul machine chased.
                TestAssert.True(runtime.Pets.Command(owner, 5, match.Hero(1).Position,
                        match.Hero(1).NetId, ghoul.NetId),
                    "the taunt's hard-attack order must take the ghoul to its hero");
                // Batch 229: a pet issues its orders on its script timers (Pet.txt TimerScanDistance and TimerFindEnemies, 0.15 s), not on every
                // step. Measured here: the taunt's route is in place after the second 0.15 s timer step (after one step the ghoul has no route,
                // after two it has four waypoints), so the check steps the clock two timer periods. The death below is unchanged.
                for (var step = 0; step < 2; step++) runtime.Pets.Tick(now += 0.15, 0);
                TestAssert.True(runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId),
                    "live ghoul must have an active chase before death");
                runtime.Combat.Kill(ghoul, match.Hero(1).NetId);
                TestAssert.False(ghoul.Alive, "ghoul must actually die");
                TestAssert.Equal(0, runtime.Pets.LastDestinations.Count,
                    "dead ghouls must not accumulate in the destination cache");
            }
        }
    }
}
