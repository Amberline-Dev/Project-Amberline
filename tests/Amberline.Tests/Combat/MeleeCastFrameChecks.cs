using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

// Batch 741 (owner, 11 November 2026, on b739's open question: "Fix it"). A melee swing's hit lands at its cast frame, in the production step
// its windup ends (the client's cast builder runs a CastType 0 attack's effect at once, DoCast_ClientAndServer case 0), so an attacker
// killed right after that step has already dealt it, with its own on-hit. Before: the monsters whose CastType 0 attack ships a MissileSpeed
// (AncientGolem, LizardElder, 500) flew it and landed it ~0.4 s later, after such a death and without CharOnHitUnit's x1.43 (b739: 50.00
// for 71.50), and every other melee hit waited for the next step, so a death in between took the dead attacker's on-hit away.
public static class MeleeCastFrameChecks
{
    private const double Step = 1.0 / 30.0;

    [Check("combat.melee-hit-lands-at-its-cast-frame-before-its-attacker-dies")]
    public static void MeleeHitLandsAtItsCastFrameBeforeItsAttackerDies()
    {
        var (runtime, annie, garen, capture) = Start();
        using var _ = runtime;
        var minion = StandingMinion(runtime, MinionType.Melee);

        var failures = new List<string>();
        foreach (var (attacker, label) in new[] { (Monster(runtime, 1), "AncientGolem"), (Monster(runtime, 5), "Golem"), (minion, "melee minion"),
                     (garen, "Garen") })
        {
            // Annie stands in the attacker's melee reach (a lane minion and Garen meet her in the river, away from every turret).
            if (attacker.Kind != EntityKind.Neutral) attacker.Position = new Position2(7000, 7000);
            annie.Position = attacker.Position + new Position2(attacker.CombatRadius + annie.CombatRadius + 20.0, 0.0);
            annie.Waypoints.Clear();
            runtime.Delivery.Flush();
            var control = Swing(runtime, capture, attacker, annie, kill: false);
            var killed = Swing(runtime, capture, attacker, annie, kill: true);
            Console.WriteLine($"{label}: control {control.Damage:F2} (cast frame +{control.StepsLate} steps), " +
                              $"killed at its cast frame {killed.Damage:F2} (+{killed.StepsLate} steps)");
            if (control.StepsLate != 0) failures.Add($"{label}'s hit lands {control.StepsLate} step(s) after its cast frame");
            if (killed.Damage == 0.0 || Math.Abs(killed.Damage - control.Damage) > 1e-3)
                failures.Add($"{label} killed right after its cast frame deals {killed.Damage:F2}, its swing deals {control.Damage:F2}");
        }
        TestAssert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // Batch 742 (owner, 11 November 2026, on a ranged monster's shot in flight when it dies: "Full hit, like the rest"). A shot already in
    // the air lands as it would have with its attacker alive, its character script's on-hit included: the Wraith's CharOnHitUnit x1.43 on
    // Annie, the siege minion's CharOnHitUnit x2 on a turret. Before, the unit character module dropped the dead unit's script at its death.
    [Check("combat.missile-in-flight-lands-with-its-dead-attackers-on-hit")]
    public static void MissileInFlightLandsWithItsDeadAttackersOnHit()
    {
        var (runtime, annie, _, capture) = Start();
        using var _ = runtime;
        var wraith = Monster(runtime, 3);
        TestAssert.Equal("Wraith", wraith.Name, "camp 3's first member");
        var siege = StandingMinion(runtime, MinionType.Siege);
        // A bare Order turret in the river: a real one's armour moves with its backdoor bonus (TurretBackdoorBonus, 150) as minions come
        // and go, which a comparison of two shots cannot take. It has no wire, so its health is read.
        siege.Position = new Position2(7000.0, 6000.0);
        var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret), EntityKind.Turret, UnitType.Turret, Team.Order,
            siege.Position + new Position2(0.0, siege.CombatRadius + 250.0)) { MaxHealth = 1e6, Health = 1e6 });
        var failures = new List<string>();
        foreach (var (attacker, target, label) in new[] { (wraith, annie, "Wraith on Annie"), (siege, turret, "siege minion on a turret") })
        {
            // A shot of a few hundred units: the Wraith's flies at 750 a second to Annie 400 units off on a spot it sees, the cannon's at 1200
            // to the turret 250 units off.
            if (attacker == wraith)
                annie.Position = new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0), (0.7071, 0.7071), (-0.7071, 0.7071), (0.7071, -0.7071),
                        (-0.7071, -0.7071) }
                    .Select(direction => wraith.Position + new Position2(direction.Item1 * 400.0, direction.Item2 * 400.0))
                    .First(spot => runtime.Navigation.IsMovementWalkable(spot) &&
                                   runtime.Navigation.HasUnitVision(wraith.Position, wraith.SelectionRadius, spot, annie.SelectionRadius));
            annie.Waypoints.Clear();
            runtime.Delivery.Flush();
            var control = Swing(runtime, capture, attacker, target, kill: false, wire: target == annie);
            var killed = Swing(runtime, capture, attacker, target, kill: true, wire: target == annie);
            Console.WriteLine($"{label}: alive {control.Damage:F2}, killed with its shot in the air {killed.Damage:F2} " +
                              $"(lands {killed.StepsLate} steps after the cast frame)");
            if (killed.StepsLate == 0) failures.Add($"{label}: the shot did not fly");
            if (killed.Damage == 0.0 || Math.Abs(killed.Damage - control.Damage) > 1e-3)
                failures.Add($"{label}: killed with its shot in the air it deals {killed.Damage:F2}, alive {control.Damage:F2}");
        }
        TestAssert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // A production Map1 match, Annie for Order and Garen for Chaos, run to the level script's first camps (Golem and Wraith at 100 s,
    // AncientGolem at 115 s), Annie's pool too deep to empty and her wire captured.
    private static (MatchRuntime Runtime, Entity Annie, Entity Garen, Capture Capture) Start()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0.0,
            roster: [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Garen", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.AfkTrackingDisabled = true;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "production match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        for (var tick = 0; tick < 600 && runtime.World.Entities.Values.Count(unit => unit.Kind == EntityKind.Neutral && unit.Alive &&
                 unit.NeutralMember == 1 && unit.CampId is 1 or 3 or 5) < 3; tick++)
            runtime.Tick(0.25);
        var seat = session.Participants.Single(participant => participant.Team == Team.Order);
        var annie = seat.Hero!;
        runtime.Buffs.AddBuff(annie, "B741Pool", BuffType.CombatEnhancer, 1e6, annie.NetId, new BuffPayload { MaxHp = 50000 });
        var capture = new Capture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, seat, capture));
        return (runtime, annie, session.Participants.Single(participant => participant.Team == Team.Chaos).Hero!, capture);
    }

    private static Entity Monster(MatchRuntime runtime, int camp) => runtime.World.Entities.Values.Single(unit =>
        unit.Kind == EntityKind.Neutral && unit.Alive && unit.CampId == camp && unit.NeutralMember == 1);

    // A Chaos lane minion in the river. Its own script would walk it on along its lane, out of the windup's leash, and swing at what it
    // finds: it stands where it is put and swings only when a check makes it.
    private static Entity StandingMinion(MatchRuntime runtime, MinionType type)
    {
        var minion = runtime.Waves!.SpawnFixture(Team.Chaos, type, 0, "C", 0, 0);
        runtime.Buffs.AddBuff(minion, "B741Stand", BuffType.CombatEnhancer, 1e6, minion.NetId, new BuffPayload { MovementDisabled = true });
        minion.AttackCooldown = 1e6;
        return minion;
    }

    // One swing from a ready attacker, stepped at the production rate; the damage it deals (on the target's wire, or off its health for a
    // target with no wire and no regeneration) and how many steps after its cast frame (the step its windup ends) it landed. With kill, the
    // attacker dies right after that step.
    private static (double Damage, int StepsLate) Swing(MatchRuntime runtime, Capture capture, Entity attacker, Entity target, bool kill,
        bool wire = true)
    {
        var combat = runtime.Combat;
        target.Health = target.MaxHealth;
        attacker.AttackCooldown = 0.0;
        capture.Packets.Clear();
        TestAssert.True(combat.QueueBasicAttack(attacker, target, new AttackRolls(1, 1, 1)), $"{attacker.Name} swings at {target.Name}");
        // No second swing of the attacker's own (a lane minion's script would take the target) in the window.
        attacker.AttackCooldown = 1e6;
        var missile = combat.PendingHits.Last(hit => hit.SourceNetId == attacker.NetId).MissileNetId;
        var castFrame = -1;
        var landed = -1;
        var damage = 0.0;
        for (var step = 0; step < 120; step++)
        {
            var pending = combat.PendingHits.SingleOrDefault(hit => hit.MissileNetId == missile);
            if (castFrame < 0 && pending.MissileNetId == missile && pending.WindupRemaining > 0.0 && pending.WindupRemaining <= Step + 1e-9)
                castFrame = step;
            var health = target.Health;
            runtime.Tick(Step);
            if (!wire && target.Health < health)
            {
                damage += health - target.Health;
                if (landed < 0) landed = step;
            }
            foreach (var packet in capture.Packets)
            {
                var span = packet.Plaintext.Span;
                if (!wire || span.Length < 18 || span[0] != 0x68 || BitConverter.ToUInt32(span[1..5]) != attacker.NetId ||
                    BitConverter.ToUInt32(span[6..10]) != target.NetId) continue;
                damage += BitConverter.ToSingle(span[14..18]);
                if (landed < 0) landed = step;
            }
            capture.Packets.Clear();
            if (kill && step == castFrame) combat.Kill(attacker);
        }
        TestAssert.True(castFrame >= 0, $"{attacker.Name}'s windup ends in the window");
        return (damage, landed < 0 ? int.MaxValue : landed - castFrame);
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
