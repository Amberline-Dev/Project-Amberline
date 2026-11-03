using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-janna-turret-shield-routes-delta")]
    public static void JannaTurretShieldReachesAlliedObserver()
    {
        using var match = new Match("Janna", Team.Order);
        var hero = match.Hero(0);
        var turret = match.Runtime.World.Entities.Values.First(entity =>
            entity.UnitType == UnitType.Turret && entity.Team == hero.Team && entity.Alive);
        hero.Position = turret.Position + new Position2(100, 0);
        match.Clear();
        match.Cast(0, 2, turret);

        TestAssert.True(turret.Buffs.Any(buff => buff.Name == "EyeOfTheStorm"),
            "Eye of the Storm gives the turret its shield buff");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x69 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == turret.NetId &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..]) > 0),
            "allied observer receives the turret's native ModifyShield delta");
    }

    [Check("spells-lee-sin-e1-recast-finishes-active-cast")]
    public static void LeeSinTempestFinishesBeforeChangingActiveSlot()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        match.Cast(0, 2, enemy);
        match.Runtime.Tick(.3);

        var packets = match.Captures[0].Packets;
        var cast = packets.FindIndex(packet => Opcode(packet) == 0xBD);
        var finish = packets.FindIndex(packet => Opcode(packet) == 0x39 &&
            packet.Plaintext.Length == 6 && packet.Plaintext.Span[5] == 7 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
        var recast = packets.FindIndex(packet =>
            Amberline.Network.Protocol.SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
            update.NetId == hero.NetId && !update.IsSummoner && update.Slot == 2 && update.SpellName == "BlindMonkETwo");
        TestAssert.True(cast >= 0 && cast < finish && finish < recast,
            "Tempest retires the native E1 cast before replacing its active slot");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BlindMonkEManager") && hero.ActiveSpellNames[2] == "BlindMonkETwo",
            "the E2 recast window still opens after a successful hit");
    }

    [Check("spells-yorick-revival-cancels-vayne-shaco-pending-casts")]
    public static void YorickTemporaryLifeDoesNotCompleteDeadChampionCasts()
    {
        foreach (var champion in new[] { "Vayne", "Shaco" })
        {
            using var match = new Match("Yorick", Team.Order, ally: true,
                allyChampion: champion);
            var runtime = match.Runtime;
            var victim = match.Hero(2);
            var enemy = match.Hero(1);
            match.Cast(0, 3, victim);
            runtime.Tick(.3);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "Yorick's mark is active before the lethal hit");
            match.Cast(2, 2, enemy);
            var enemyHealth = enemy.Health;
            runtime.Combat.Kill(victim, enemy.NetId);
            runtime.Delivery.Flush();
            TestAssert.True(victim.Alive && victim.YorickRevival is not null,
                "the first death immediately enters Yorick's temporary life");
            if (champion == "Shaco")
                TestAssert.True(victim.ChampionState is null,
                    "a staged Two-Shiv cast cannot resume after revival");
            runtime.Tick(.5);
            TestAssert.Equal(enemyHealth, enemy.Health,
                "the dead champion's unfinished cast deals no revival damage");
        }
    }

    [Check("spells-annie-death-cancels-unlaunched-q-and-w-before-revival")]
    public static void AnnieDeathRetiresUnlaunchedSharedImpacts()
    {
        foreach (var slot in new[] { 0, 1 })
        {
            using var match = new Match("Yorick", Team.Order, ally: true,
                allyChampion: "Annie");
            var runtime = match.Runtime;
            var annie = match.Hero(2);
            var enemy = match.Hero(1);
            // W's cast-order range is 75; farther targets only queue an
            // approach order and never enter the shared impact phase.
            enemy.Position = annie.Position + new Position2(40, 0);
            match.Cast(0, 3, annie);
            runtime.Tick(.3);
            match.Cast(2, slot, enemy);
            TestAssert.True(runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.CasterNetId == annie.NetId && projectile.Snapshot.Slot == slot),
                "Annie's cast has an unfinished shared impact before death");
            var enemyHealth = enemy.Health;
            runtime.Combat.Kill(annie, enemy.NetId);
            runtime.Delivery.Flush();
            TestAssert.True(annie.Alive && annie.YorickRevival is not null,
                "Yorick immediately restores temporary life");
            // The cancelled windup entry leaves the queue at its next tick; what matters is that it never lands (below).
            runtime.Tick(.8);
            TestAssert.Equal(enemyHealth, enemy.Health,
                "an unfinished Annie cast does not land during temporary life");
        }
    }

    [Check("spells-annie-death-cancels-impact-already-in-projectile-snapshot")]
    public static void AnnieDeathDuringSharedProjectilePhaseCancelsLaterCast()
    {
        using var match = new Match("Yorick", Team.Order, ally: true,
            allyChampion: "Annie");
        var runtime = match.Runtime;
        var annie = match.Hero(2);
        var enemy = match.Hero(1);
        enemy.Position = annie.Position + new Position2(40, 0);
        match.Cast(0, 3, annie);
        runtime.Tick(.3);
        // The first projectile's lethal callback occurs while the shared
        // queue is iterating a snapshot containing Annie's later W impact.
        runtime.Spells.EnqueueSharedProjectile(new DeathDuringProjectilePhase(
            annie, enemy.NetId));
        match.Cast(2, 1, enemy);
        TestAssert.True(runtime.Spells.SharedProjectiles.Any(projectile =>
            projectile.Snapshot.CasterNetId == annie.NetId && projectile.Snapshot.Slot == 1),
            "the later W is actually queued before the projectile-phase death");
        var enemyHealth = enemy.Health;
        runtime.Spells.TickSharedProjectilePhase(.1);
        runtime.Delivery.Flush();
        TestAssert.True(annie.Alive && annie.YorickRevival is not null,
            "death during projectile processing immediately enters temporary life");
        runtime.Tick(.8);
        TestAssert.Equal(enemyHealth, enemy.Health,
            "snapshotted W never deals damage after revival");
    }

    private sealed class DeathDuringProjectilePhase(Entity victim, uint killerNetId)
        : ISharedSpellProjectile
    {
        public SharedProjectileSnapshot Snapshot => new("death-trigger", victim.NetId,
            0, 0, 0, true, victim.Position, 0);

        public bool Tick(SpellSystem spells, double dt)
        {
            spells.Combat.Kill(victim, killerNetId);
            return false;
        }
    }

    [Check("spells-nidalee-swipe-moving-source-direction")]
    public static void NidaleeSwipeUsesSubmittedDirectionDespiteCasterMovement()
    {
        using var match = new Match("Nidalee", Team.Order, extraEnemy: true);
        var hero = match.Hero(0);
        var front = match.Hero(1);
        var rear = match.Hero(2);
        RankUpTheUltimate(match, hero);
        match.Cast(0, 3, hero);
        match.Clear();

        // The client source is behind the server's current caster, but its
        // short source-to-end vector still points east.
        hero.Position = new Position2(7080, 7000);
        front.Position = new Position2(7250, 7000);
        rear.Position = new Position2(6900, 7000);
        var before = hero.Position;
        var frontHealth = front.Health;
        var rearHealth = rear.Health;
        match.CastDirected(0, 2, new Position2(7000, 7000), new Position2(7015, 7000));

        // Batch 279: the cast's stop seats Nidalee on her lattice anchor at once; Swipe moves her no further.
        TestAssert.Equal(match.Runtime.Movement.ClientAnchor(before), hero.Position, "Swipe does not translate its caster");
        TestAssert.True(hero.Facing.X > .99, "Swipe faces the submitted direction");
        var face = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0x53).Plaintext.Span;
        TestAssert.True(BinaryPrimitives.ReadSingleLittleEndian(face[5..]) > .99f,
            "native face packet keeps the submitted direction");
        var cast = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0xBD).Plaintext.Span;
        TestAssert.True(BinaryPrimitives.ReadSingleLittleEndian(cast[32..]) > hero.Position.X,
            "cast endpoint remains ahead of the current server position");
        Advance(match, .5);
        TestAssert.True(front.Health < frontHealth, "front target takes Swipe damage");
        TestAssert.Equal(rearHealth, rear.Health, "rear target remains outside Swipe");

        using var fallback = new Match("Nidalee", Team.Order);
        var fallbackHero = fallback.Hero(0);
        fallback.Cast(0, 3, fallbackHero);
        fallback.Clear();
        fallbackHero.Facing = new Position2(0, 1);
        // Batch 279: the stock client stands on its lattice anchor (every path it installs seats it there), the point its
        // in-place cast sends; the cast's stop seats the server's hero there too.
        fallbackHero.Position = fallback.Runtime.Movement.ClientAnchor(fallbackHero.Position);
        fallback.CastDirected(0, 2, fallbackHero.Position, fallbackHero.Position);
        TestAssert.True(fallbackHero.Facing.Z > .99,
            "coincident request points preserve the current facing");
    }

    [Check("spells-janna-overlapping-charge-retires-prior-wave")]
    public static void JannaOverlappingChargeKeepsOldWaveMovingAndRetiresIt()
    {
        // Each release spawns its own TestCube and casts its own line missile; a new charge and release while the first wave flies replace
        // nothing, so both waves are in flight, each from its own cube, and the first keeps moving.
        using var match = new Match("Janna", Team.Order);
        var hero = match.Hero(0);
        var aim = hero.Position + new Position2(400, 0);
        match.CastGround(0, aim);
        match.CastGround(0, aim);
        var firstMissile = JannaWaveMissile(match);
        TestAssert.True(firstMissile != 0, "the first release launched a wave");
        var firstCube = JannaCubes(match).Single();
        Position2 WavePosition(uint missile) => match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
            .Single(projectile => projectile.MissileNetId == missile).Position;
        var before = WavePosition(firstMissile);
        match.Clear();

        hero.SpellCooldowns[0] = 0;
        match.CastGround(0, aim);
        match.Runtime.Tick(.1);
        TestAssert.True(WavePosition(firstMissile) != before,
            "an active wave continues during the next charge");
        match.CastGround(0, aim);
        var cubes = JannaCubes(match);
        TestAssert.Equal(2, cubes.Count, "each release has its own TestCube");
        var secondCube = cubes.Single(cube => cube.NetId != firstCube.NetId);
        var flights = match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
            .Where(projectile => projectile.MissileNetId != 0 && cubes.Any(cube => cube.NetId == projectile.CasterNetId)).ToList();
        TestAssert.Equal(2, flights.Count, "both waves are in flight");
        TestAssert.True(flights.Any(flight => flight.MissileNetId == firstMissile && flight.CasterNetId == firstCube.NetId) &&
                        flights.Any(flight => flight.CasterNetId == secondCube.NetId && flight.MissileNetId != firstMissile),
            "the new wave has its own missile and cube");
    }
}
