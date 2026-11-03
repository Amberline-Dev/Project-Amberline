using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("pets-yorick-ghouls-approach-and-hit-map1-turrets")]
    public static void YorickGhoulsApproachAndHitMap1Turrets()
    {
        foreach (var kind in new[] { "Spectral", "Decayed", "Ravenous" })
        foreach (var competitor in new[] { "none", "champion", "minion" })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime; var owner = match.Hero(0);
            var start = new Position2(13395.091, 7941.109);
            owner.Position = start + new Position2(0, -200);
            var turret = runtime.World.Entities.Values.Single(entity => entity.Name == "Turret_T2_R_02");
            turret.Protected = false;
            foreach (var tower in runtime.World.Entities.Values.Where(entity => entity.UnitType == UnitType.Turret))
                tower.AttackCooldown = 100;
            var ghoul = YorickScriptSupport.SpawnGhoul(runtime, owner, kind, start);
            // Preserve the captured authoritative origin rather than the spawn floor projection.
            ghoul.Position = start;
            TestAssert.Near(478.623, Distance(start, turret.Position), .01, "captured inner-bottom turret geometry");
            TestAssert.Near(125, ghoul.AttackRange, 0, "shipped attack range is unchanged");
            Entity? competing = null;
            if (competitor == "champion")
            {
                competing = match.Hero(1);
                competing.Position = start;
            }
            else if (competitor == "minion")
            {
                competing = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, Team.Chaos, start)
                {
                    Health = 1000, MaxHealth = 1000, Radius = 35, Created = true, Visible = true,
                };
                runtime.World.Register(competing);
            }
            runtime.Delivery.Flush(); match.Clear();
            // No YorickSummon* buff drives this ghoul, so it acquires on the 0.15 s TimerFindEnemies grid
            // (FindTargetInAcRUsingGoldRedirectTarget: the ghoul's acquisition range around its master).
            // ScanNow arms YorickPHPet's timers on this check's own axis, so that grid starts here.
            match.Runtime.Pets.ScanNow(ghoul);
            runtime.Pets.Tick(0.15, 0);
            if (competitor == "none" && ghoul.AcquisitionRange < Distance(start, turret.Position))
            {
                // FindTargetInAcRUsingGoldRedirectTarget reaches a turret only within the ghoul's own
                // acquisition range (400 for Ravenous and Decayed, 600 for Spectral) around its master;
                // the turret scan of the YorickSummonDecayed script (750) is the script's. The captured
                // spot is 478 away, so it starts outside that range and moves inside it for the approach
                // below. Batch 146: that scan circle is the master's, so he walks up with the ghoul -
                // 100 off the tower line keeps their bodies apart and leaves the tower 364 from him,
                // inside the 400 the smallest ghouls ask with.
                TestAssert.Equal<uint?>(null, ghoul.AttackTarget,
                    kind + " has no script scan here and the turret is outside its acquisition range");
                var toward = start - turret.Position;
                var span = Distance(start, turret.Position);
                var scale = 350.0 / span;
                ghoul.Position = turret.Position + new Position2(toward.X * scale, toward.Z * scale);
                owner.Position = ghoul.Position + new Position2(-toward.Z / span * 100.0,
                    toward.X / span * 100.0);
                // Batch 51 (db5a9bbe): a ghoul without a target acquires on the 0.15 s TimerFindEnemies grid (InitTimer(0.15)
                // in YorickPHPet), not at every step, so the next acquisition is due 0.15 s after the first scan.
                runtime.Pets.Tick(0.3, 0);
            }
            TestAssert.Equal<uint?>(competing?.NetId ?? turret.NetId, ghoul.AttackTarget,
                kind + " takes a champion or minion before a structure");
            if (competing is not null) continue;
            TestAssert.True(ghoul.Waypoints.Count > 1, kind + " commits an approach route");
            var reach = CombatService.AttackReach(ghoul, turret);
            TestAssert.True(Distance(ghoul.Waypoints[^1], turret.Position) <= reach,
                "route ends inside legal basic-attack reach");
            var hp = turret.Health;
            var ticks = 0;
            while (ghoul.Alive && turret.Health >= hp && ticks++ < 100)
                runtime.Tick(.05);
            TestAssert.True(turret.Health < hp && ghoul.Alive && ticks * .05 < 5,
                kind + " reaches and damages the turret before expiring");
            TestAssert.True(Distance(start, ghoul.Position) > 100, "ghoul actually moves along its route");
            TestAssert.True(Distance(ghoul.Position, turret.Position) <= reach, "hit lands from legal range");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x0D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == ghoul.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == turret.NetId),
                "ghoul publishes a basic attack against the turret");
            var packets = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == ghoul.NetId).ToArray();
            TestAssert.True(packets.Any(packet => packet.Plaintext.Length > 17), "publishes the moving route");
            // Restated in batch 229 (owner, 25 October 2026: retail re-sends the stop a unit is asked for, so a repeat is not dropped and
            // the count is not bounded): every stop the ghoul sends after its route stands inside its attack reach of the turret, where
            // the swing is asked for; a stop before the route is the spawn's.
            var routed = false;
            foreach (var packet in packets)
            {
                if (packet.Plaintext.Length > 17) { routed = true; continue; }
                if (!routed) continue;
                var stop = new Position2(BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[9..]),
                    BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[13..]));
                TestAssert.True(Distance(stop, turret.Position) <= reach + 1.0,
                    kind + ": a stop after the route stands inside attack reach of the turret");
            }
        }
    }
}
