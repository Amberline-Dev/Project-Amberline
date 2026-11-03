using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>
/// Batch 466: the capture point's guardian is the point's attacker. Its record (OdinNeutralGuardian.inibin) names Spell1
/// OdinGuardianSpellAttackCast (CastRange 1000, Cooldown 1), which casts ExtraSpell1 OdinGuardianSpellAttack, a missile that deals the
/// guardian's total attack damage (BaseDamage 110 plus OdinGuardianStatsByLevel's 15 per highest champion level). The shrine-turret stand-ins
/// batch 448 put on each point (1600 range, 462 damage) are gone from the world. A two-seat map 8 match (Order and Chaos Annie) on point C.
/// </summary>
public static class DominionGuardianChecks
{
    private const string GuardianName = "OdinNeutralGuardian";
    private const string Channel = "OdinCaptureChannel";
    private const double Step = 1.0 / 30.0;
    // Point C: a wave minion on a point moves its capture. Batch 499: waves leave only the points a team holds (SpawnMinionWave), so before a
    // capture no wave walks, and none reaches point C.
    private const int PointIndex = 2;
    // Where an enemy champion stands to draw the guardian's fire, inside its 950 acquisition range (batch 662: Turret.luaobj; batch 501's tree
    // needed 600).
    private const double EnemyDistance = 500.0;
    // Where the Order champion stands beside its point: on the far side, so the Chaos champion stays beyond the idle Order Annie's acquisition
    // (750 by default, GameData AcquisitionRange): her hit would cancel the capture channel (OdinCaptureChannel BuffOnPreDamage, a hero's
    // damage) and stand in for the guardian's. Batch 662: the guardian no longer needs her there (batch 501's tree needed her within 700).
    private const double AllyDistance = 650.0;

    private sealed record Match(MatchRuntime Runtime, Entity Order, Entity Chaos, Entity InitialGuardian, BehaviorTreeQuestHost Quests,
        Position2 OrderHome, Position2 ChaosHome) : IDisposable
    {
        // Batch 475 (second follow-up): a capture re-creates the point's guardian on the owner's team, so the guardian is the point's current
        // one (the start's guardian until the first capture).
        public Entity Guardian => Quests.Context.PointGuardian(PointIndex + 2) ?? InitialGuardian;

        // The process default goes back to map 1 (DominionBaseChecks' pattern).
        public void Dispose()
        {
            Runtime.Dispose();
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>
    /// The guardian of an Order point casts its attack on a Chaos champion within its range and never on an Order one. Batch 466 wrote this
    /// check for its own 1000-range attacker (Chaos at 800); batch 501's tree needed 600. Batch 662: the guardian runs Turret.luaobj
    /// (acquisition 950, no ally gate; see the check below).
    /// </summary>
    [Check("dominion.guardian-of-an-owned-point-attacks-an-enemy-champion-in-its-range", TimeoutSeconds = 300)]
    public static void TheGuardianOfAnOwnedPointAttacksAnEnemyChampionInItsRange()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (guardian, order, chaos) = (match.Guardian, match.Order, match.Chaos);
        order.Position = Offset(guardian, 300.0);
        chaos.Position = Offset(guardian, -EnemyDistance);
        var orderHealth = order.Health;
        var chaosHealth = chaos.Health;
        var lastCast = CastAt(guardian);
        var hits = new List<double>();
        for (var tick = 0; tick < 150; tick++)
        {
            var before = chaos.Health;
            match.Runtime.Tick(Step);
            if (chaos.Health < before - 1.0) hits.Add(before - chaos.Health);
        }
        TestAssert.True(CastAt(guardian) > lastCast,
            $"the Order guardian casts OdinGuardianSpellAttackCast (its Spell1 cooldown starts) on the Chaos champion {EnemyDistance:F0} away");
        TestAssert.True(chaos.Health < chaosHealth - 50.0,
            $"the Chaos champion takes the guardian's hits ({chaosHealth:F0} to {chaos.Health:F0}; hits {string.Join(", ", hits.Select(h => h.ToString("F1")))})");
        TestAssert.True(order.Health >= orderHealth - 1e-6,
            $"the Order champion 300 from its own guardian takes no hit ({orderHealth:F0} to {order.Health:F0})");
    }

    /// <summary>
    /// Batch 662 (owner, 3 November 2026: "Normal turret"): the shipped map 8 AI list (LEVELS/Map8/Scripts/AiEntityDataFile.dat) has the lines
    /// that bound the team guardians to the OdinTower tree commented out (18-19), so the guardian of an owned point attacks like a turret with
    /// no champion of its own team near (the wiki's Capture point page: an allied point "will attack enemy units like a turret"; a point
    /// minions attack "will still attack"). It runs the plain turret AI, DATA/Scripts/Turret.luaobj: it acquires within its record's
    /// AcquisitionRange 950 (OdinNeutralGuardian.inibin), fires its Spell1 OdinGuardianSpellAttackCast (CastRange 1000, Cooldown 1) once a
    /// second, and takes a minion before a champion (the turret ladder, CombatService.AcquireTurretTarget). The Order champion is at its base.
    /// </summary>
    [Check("dominion.guardian-of-an-owned-point-fires-like-a-turret-with-no-ally-near", TimeoutSeconds = 300)]
    public static void TheGuardianOfAnOwnedPointFiresLikeATurretWithNoAllyNear()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, chaos) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(guardian, -1100.0);
        var idle = CastAt(guardian);
        Advance(runtime, 2.0);
        TestAssert.True(CastAt(guardian) == idle, "a Chaos champion 1100 away, past the guardian's 950 acquisition range, draws no shot");

        chaos.Position = Offset(guardian, -EnemyDistance);
        var health = chaos.Health;
        TestAssert.True(Until(runtime, () => chaos.Health < health - 50.0, 1.5),
            $"with no Order champion near, the guardian fires on the Chaos champion {EnemyDistance:F0} away ({health:F0} to {chaos.Health:F0})");

        // A Chaos minion at the empty point (beyond the point's capture area): the champion leaves, the guardian takes the minion.
        chaos.Position = match.ChaosHome;
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos,
            Offset(guardian, -800.0)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
        TestAssert.True(Until(runtime, () => minion.Health < 1e7, 2.0), "the guardian fires on the Chaos minion at its point");
        var hits = 0;
        for (var tick = 0; tick < (int)Math.Round(5.0 / Step); tick++)
        {
            var before = minion.Health;
            runtime.Tick(Step);
            if (minion.Health < before) hits++;
        }
        TestAssert.True(hits is >= 4 and <= 6, $"one shot a second, its Spell1's Cooldown 1: {hits} hits in 5 s");
    }

    /// <summary>
    /// Batch 660 (community report, 3 November 2026: a guardian's laser held a diver, "but the turret never fired"): the target line (0x6D) of
    /// a guardian shows only while it fires (the owner's laser rule for the shrines, batch 529: the laser starts and ends with the shots), and
    /// not while an enemy channel suppresses it. Batch 662: the guardian runs Turret.luaobj with no ally gate (owner, 3 November 2026: "Normal
    /// turret"), so the line goes when the enemy leaves its range (batch 660 measured the ally leaving the tree's 700).
    /// </summary>
    [Check("dominion.guardian-target-line-shows-only-while-it-fires", TimeoutSeconds = 300)]
    public static void TheGuardianTargetLineShowsOnlyWhileItFires()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, order, chaos) = (match.Runtime, match.Guardian, match.Order, match.Chaos);
        var seat = Observe(match, Team.Chaos);
        order.Position = Offset(guardian, AllyDistance);
        chaos.Position = Offset(guardian, -EnemyDistance);
        var before = CastAt(guardian);
        Advance(runtime, 2.0);
        TestAssert.True(CastAt(guardian) > before, "precondition: the guardian fires on the Chaos champion");
        TestAssert.Equal(chaos.NetId, Line(seat, guardian), "and its line is on the Chaos champion it fires on");

        chaos.Position = Offset(guardian, -1100.0);
        Advance(runtime, 0.2);
        var held = CastAt(guardian);
        Advance(runtime, 3.0);
        TestAssert.True(CastAt(guardian) == held, "the Chaos champion out of range (1100): the guardian fires nothing");
        TestAssert.Equal(0u, Line(seat, guardian), "and shows no line on the champion it does not fire on");

        chaos.Position = Offset(guardian, -EnemyDistance);
        TestAssert.True(Until(runtime, () => CastAt(guardian) > held, 3.0), "the champion back in range: the guardian fires again");
        TestAssert.Equal(chaos.NetId, Line(seat, guardian), "with its line back on the Chaos champion");

        // The guardian's hits would kill the level 3 Annie before the channel below; she is healed (a test seam).
        chaos.Health = chaos.MaxHealth;
        order.Position = match.OrderHome;
        held = CastAt(guardian);
        TestAssert.True(Until(runtime, () => CastAt(guardian) > held, 1.5), "the Order champion gone home: the guardian goes on firing (no ally gate)");
        TestAssert.Equal(chaos.NetId, Line(seat, guardian), "and its line stays on the Chaos champion it fires on");

        chaos.Position = Offset(guardian, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "the Chaos champion starts the capture channel on the guardian");
        Advance(runtime, 1.0);
        TestAssert.True(runtime.Spells.CaptureChannelTarget(chaos) == guardian, "precondition: the Chaos champion still channels");
        TestAssert.Equal(0u, Line(seat, guardian), "the channel holds the guardian's fire, and its line clears with it");
    }

    /// <summary>The target of the last 0x6D (AiSetTarget: opcode, unit, target) the seat was sent for <paramref name="unit"/>; 0 for a clear or none.</summary>
    private static uint Line(SeatCapture seat, Entity unit)
    {
        var last = seat.Packets.LastOrDefault(packet => packet.Plaintext.Length == 9 && packet.Plaintext.Span[0] == 0x6D &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == unit.NetId);
        return last is null ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(last.Plaintext.Span[5..]);
    }

    /// <summary>
    /// The guardian holds its fire while an enemy champion channels the capture on it (the channel's OdinGuardianSuppression, the wiki's
    /// "will stop attacking when being neutralized by the enemy champion"), and fires again once the channel ends. Batch 501's tree needed the
    /// Order champion within 700 (it channels nothing, so the point's count does not take it) and the Chaos champion back within 600; batch
    /// 662's turret script needs neither.
    /// </summary>
    [Check("dominion.guardian-holds-fire-while-an-enemy-champion-channels-on-it", TimeoutSeconds = 300)]
    public static void TheGuardianHoldsFireWhileAnEnemyChampionChannelsOnIt()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, chaos) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = Offset(guardian, AllyDistance);
        chaos.Position = Offset(guardian, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "the Chaos champion starts the capture channel on the guardian");
        // The guardian may take one shot in the step the channel starts in (the spells run after the capture host); from then on it holds.
        Advance(runtime, 0.2);
        var held = CastAt(guardian);
        Advance(runtime, 3.0);
        TestAssert.True(runtime.Spells.CaptureChannelTarget(chaos) == guardian, "precondition: the Chaos champion still channels");
        TestAssert.True(CastAt(guardian) == held, $"the guardian casts nothing while the enemy champion channels on it (held {held:F2}, now {CastAt(guardian):F2}, guardian buffs {string.Join(",", guardian.Buffs.Select(b => b.Name))}, chaos hp {chaos.Health:F0})");
        TestAssert.True(Order(runtime, chaos, 2, Offset(guardian, -EnemyDistance), 0), "the move order is accepted");
        TestAssert.True(Until(runtime, () => CastAt(guardian) > held, 3.0) && runtime.Spells.CaptureChannelTarget(chaos) is null,
            "once the channel ends the guardian fires on the Chaos champion again");
    }

    /// <summary>
    /// Batch 467: the guardian's hit carries the 65% armour penetration of its OdinGuardianBuff (BuffOnUpdateStats block 5,
    /// IncPercentArmorPenetrationMod 0.65): the first hit on the level 3 Chaos Annie (armour 20.5) is the total attack damage x 0.9 (the
    /// TurretDamageManager ramp with no marker yet) x 100 / (100 + 20.5 x 0.35). Batch 466 measured it with no penetration (x 100 / 120.5).
    /// </summary>
    [Check("dominion.guardian-hit-carries-its-armour-penetration", TimeoutSeconds = 300)]
    public static void TheGuardianHitCarriesItsArmourPenetration()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (guardian, chaos) = (match.Guardian, match.Chaos);
        match.Order.Position = Offset(guardian, AllyDistance);
        chaos.Position = Offset(guardian, -EnemyDistance);
        TestAssert.Near(20.5, chaos.Armor, 1e-9, "precondition: the level 3 Chaos Annie's armour");
        double? hit = null;
        var damage = 0.0;
        for (var tick = 0; tick < 150 && hit is null; tick++)
        {
            var before = chaos.Health;
            damage = guardian.AttackDamage;
            match.Runtime.Tick(Step);
            if (chaos.Health < before - 1.0) hit = before - chaos.Health;
        }
        var expected = damage * 0.9 * 100.0 / (100.0 + 20.5 * (1.0 - 0.65));
        TestAssert.True(hit is { } first && Math.Abs(first - expected) < 0.1,
            $"the first hit is {expected:F1} ({damage:F1} x 0.9 through 35% of 20.5 armour); measured {hit:F1}, the guardian's armour penetration {guardian.ArmorPenetrationPercent:F2}");
    }

    /// <summary>
    /// Batch 467: OdinGuardianSuppression stays on the guardian while an enemy champion's channel holds. The channel adds it (ChannelingStart
    /// block 2, 30 s, renewed) and then its team stack (blocks 8 and 9); its own stats hook clears it only when it counts no team stack or both
    /// teams' (BuffOnUpdateStats blocks 3 and 4). Its activation stuns the guardian (block 3, 0.5 s) and its update actions drain the owner's
    /// capture meter (the guardian's mana) every 0.25 s from 1.5 s on, which stops OdinGuardianBuff's refill (that counts the suppression).
    /// Batch 512: the meter is the point tree's progress (BehaviorLeaves.CapturePointSetValue); the enemy's channel drains it all the same.
    /// </summary>
    [Check("dominion.guardian-suppression-lasts-while-a-channel-holds", TimeoutSeconds = 300)]
    public static void TheGuardianSuppressionLastsWhileAChannelHolds()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, chaos) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(guardian, -300.0);
        var mana = guardian.Mana;
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "the Chaos champion starts the capture channel on the guardian");
        TestAssert.True(guardian.Buffs.Any(buff => buff.Name == "Stun"),
            $"the suppression's activation stuns the guardian (buffs {string.Join(",", guardian.Buffs.Select(b => b.Name))})");
        var lostAt = -1.0;
        for (var tick = 1; tick <= 90 && runtime.Spells.CaptureChannelTarget(chaos) == guardian; tick++)
        {
            if (!guardian.Buffs.Any(buff => buff.Name == Suppression)) { lostAt = (tick - 1) * Step; break; }
            runtime.Tick(Step);
        }
        TestAssert.True(runtime.Spells.CaptureChannelTarget(chaos) == guardian, "precondition: the Chaos champion still channels after 3 s");
        TestAssert.True(lostAt < 0.0, $"OdinGuardianSuppression holds while the channel does (gone at {lostAt:F2} s; buffs {string.Join(",", guardian.Buffs.Select(b => b.Name))})");
        TestAssert.True(guardian.Mana < mana - 1000.0, $"the owner's capture meter drains while the enemy channels ({mana:F0} to {guardian.Mana:F0})");
        TestAssert.True(Order(runtime, chaos, 2, Offset(guardian, -500.0), 0), "the move order is accepted");
        TestAssert.True(Until(runtime, () => !guardian.Buffs.Any(buff => buff.Name == Suppression), 1.0),
            "once the channel ends its stats hook clears the suppression (no team stack left)");
    }

    private const string Suppression = "OdinGuardianSuppression";

    /// <summary>A neutral point's guardian attacks nothing (the wiki: "If the point has not been fully captured from neutral, it will not attack any units").</summary>
    [Check("dominion.guardian-of-a-neutral-point-attacks-nothing", TimeoutSeconds = 300)]
    public static void TheGuardianOfANeutralPointAttacksNothing()
    {
        using var match = StartMatch();
        match.Chaos.Position = Offset(match.Guardian, -500.0);
        match.Order.Position = Offset(match.Guardian, 500.0);
        var health = (match.Order.Health, match.Chaos.Health);
        Advance(match.Runtime, 3.0);
        TestAssert.True(match.Guardian.Team == Team.Neutral, "precondition: point C is neutral");
        TestAssert.False(match.Guardian.ScriptSlotReadyAt.ContainsKey(0), "the neutral guardian never casts its attack");
        TestAssert.True(match.Order.Health >= health.Item1 - 1e-6 && match.Chaos.Health >= health.Item2 - 1e-6,
            $"neither champion beside the neutral point is hit (Order {health.Item1:F0} to {match.Order.Health:F0}, Chaos {health.Item2:F0} to {match.Chaos.Health:F0})");
    }

    /// <summary>
    /// A full-health champion walks to a point the enemy holds and starts the capture channel at the guardian's 600 capture range: the
    /// guardian's 1000 range and its damage leave it alive (batch 463: the 1600-range, 462-damage stand-ins killed it 800 to 1500 out).
    /// </summary>
    [Check("dominion.full-health-champion-reaches-an-enemy-point-and-channels", TimeoutSeconds = 300)]
    public static void AFullHealthChampionReachesAnEnemyPointAndChannels()
    {
        using var match = StartMatch();
        Capture(match, match.Chaos);
        var (runtime, guardian, order) = (match.Runtime, match.Guardian, match.Order);
        match.Chaos.Position = match.ChaosHome;
        order.Position = Offset(guardian, 1500.0);
        order.Health = order.MaxHealth;
        TestAssert.True(Order(runtime, order, 3, guardian.Position, guardian.NetId), "the attack order on the Chaos guardian is accepted");
        TestAssert.True(Until(runtime, () => !order.Alive || runtime.Spells.CaptureChannelTarget(order) == guardian, 8.0) && order.Alive,
            $"the Order champion reaches the 600 range alive and channels (alive {order.Alive}, health {order.Health:F0}/{order.MaxHealth:F0})");
    }

    /// <summary>No shrine model stands at a capture point: the trees' OrderTurret and ChaosTurret are server-side handles, not world units.</summary>
    [Check("dominion.no-shrine-model-stands-at-a-capture-point", TimeoutSeconds = 300)]
    public static void NoShrineModelStandsAtACapturePoint()
    {
        using var match = StartMatch();
        var shrines = match.Runtime.World.Entities.Values
            .Where(unit => unit.ChampionName is "OdinOrderTurretShrine" or "OdinChaosTurretShrine").ToList();
        foreach (var capture in match.Quests.Captures)
        {
            var at = (Position3)capture.Variable("CapturePosition")!;
            TestAssert.False(shrines.Any(unit => Distance(unit.Position, new Position2(at.X, at.Z)) < 1000.0),
                $"no shrine stands at {capture.Variable("CapPointName")}");
            TestAssert.True(capture.Variable("OrderTurret") is Entity { NoRender: true } && capture.Variable("ChaosTurret") is Entity { NoRender: true },
                "the tree still holds its two hidden turret handles");
        }
    }

    /// <summary>
    /// A point the enemy neutralises keeps its guardian, which goes neutral and then takes the next owner's team (OdinGuardianBuff redraws
    /// the guardian for every team change, the neutral one included), so the point changes hands again. A guard for the bot match's
    /// "points change hands" measure: it passes on master too (batch 461 open question 1 does not occur in this flow).
    /// </summary>
    [Check("dominion.neutralized-point-keeps-its-guardian-and-changes-hands-again", TimeoutSeconds = 300)]
    public static void ANeutralizedPointKeepsItsGuardianAndChangesHandsAgain()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, chaos) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(guardian, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "the Chaos champion channels on the Order point");
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Neutral, 20.0), $"point C is neutralised (owner {Owner(match)}, progress {match.Quests.Captures[PointIndex].Variable("CaptureProgress")}, channel {runtime.Spells.CaptureChannelTarget(chaos)?.Name}, chaos {chaos.Position} alive {chaos.Alive} hp {chaos.Health:F0}, buffs {string.Join(",", chaos.Buffs.Select(b => b.Name))})");
        // Batch 475: each change re-creates the point's guardian (BehaviorTreeQuestHost.GuardianTakesOwner), so the point's guardian is the current one.
        TestAssert.True(match.Guardian.Alive && match.Guardian.Team == Team.Neutral,
            $"the point's guardian lives and is neutral (alive {match.Guardian.Alive}, team {match.Guardian.Team})");
        // Batch 475: the channel on the neutralised guardian ended with it (the guardian was replaced), so Chaos channels the new one.
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, match.Guardian), "the Chaos champion channels on the point's new guardian");
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Chaos, 20.0), "point C becomes Chaos's");
        TestAssert.True(match.Guardian.Alive && match.Guardian.Team == Team.Chaos,
            $"the point's guardian is Chaos's (alive {match.Guardian.Alive}, team {match.Guardian.Team})");
    }

    /// <summary>
    /// The setup hold (the opening ceremony's SetUnitCircularMovementRestriction, 1200 around the base) keeps a champion pushed past its
    /// circle from walking further out; it may still walk back in (batch 463 open question 3).
    /// </summary>
    [Check("dominion.setup-hold-keeps-a-pushed-out-champion-from-walking-further-out", TimeoutSeconds = 300)]
    public static void TheSetupHoldKeepsAPushedOutChampionFromWalkingFurtherOut()
    {
        using var match = StartMatch();
        var hero = match.Order;
        TestAssert.True(hero.MovementRestriction is not null, "precondition: the opening ceremony holds the champion");
        var (centre, radius) = hero.MovementRestriction!.Value;
        // The first bearing out of the base whose ground is walkable from inside the circle to 800 past it (the base exit).
        var grid = match.Runtime.Spells.Movement.Navigation;
        var angle = Enumerable.Range(0, 72).Select(step => step * Math.PI / 36.0).First(bearing =>
            Enumerable.Range(-3, 12).All(offset => grid.IsWalkable(new Position2(centre.X + Math.Cos(bearing) * (radius + offset * 100.0),
                centre.Z + Math.Sin(bearing) * (radius + offset * 100.0)))));
        Position2 Out(double distance) => new(centre.X + Math.Cos(angle) * distance, centre.Z + Math.Sin(angle) * distance);
        hero.Position = Out(radius + 100.0);
        Advance(match.Runtime, Step);
        var pushed = Distance(hero.Position, centre);
        TestAssert.True(pushed > radius, $"precondition: the champion stands past its circle ({pushed:F0}, circle {radius:F0})");
        TestAssert.True(Order(match.Runtime, hero, 2, Out(radius + 800.0), 0), "the move order is accepted");
        Advance(match.Runtime, 2.0);
        TestAssert.True(Distance(hero.Position, centre) <= pushed + 2.0,
            $"the pushed-out champion does not walk further out ({pushed:F0} to {Distance(hero.Position, centre):F0} from its base, circle {radius:F0})");
        TestAssert.True(Order(match.Runtime, hero, 2, Out(radius - 300.0), 0), "the move order back in is accepted");
        Advance(match.Runtime, 2.0);
        TestAssert.True(Distance(hero.Position, centre) < radius,
            $"it walks back inside ({Distance(hero.Position, centre):F0} from its base, circle {radius:F0})");
    }

    /// <summary>
    /// Batch 479: a point's guardian is built from its record on the production spawn path (OdinNeutralGuardian.inibin: BaseHP 50000, BaseMP 50000),
    /// so its health and its PAR pool, which the capture ring's fill reads, are the record's.
    /// </summary>
    [Check("dominion.guardian-production-spawn-takes-its-record-pool", TimeoutSeconds = 300)]
    public static void TheProductionGuardianTakesItsRecordPool()
    {
        using var match = StartMatch();
        for (var position = 2; position <= 6; position++)
        {
            var guardian = match.Quests.Context.PointGuardian(position) ?? throw new InvalidOperationException($"point {position} has no guardian");
            TestAssert.True(guardian.MaxHealth == 50000.0, $"point {position}'s guardian has MaxHealth 50000 (was {guardian.MaxHealth:F0})");
            TestAssert.True(guardian.MaxMana == 50000.0, $"point {position}'s guardian has MaxMana 50000 (was {guardian.MaxMana:F0})");
        }
    }

    /// <summary>
    /// Batch 479: the guardian's PAR moves during a capture channel (the shipped suppression drains the owner's meter), and the change goes out
    /// in 0xCC blocks, so the client's ring follows it. Batch 510: the blocks are the pool's own values (one writer; batch 512: the point tree's
    /// CapturePoint_SetValue), so an Order capture of a neutral point never goes back on the wire (batch 493a's ease was a second writer that pulled every step back).
    /// </summary>
    [Check("dominion.guardian-par-changes-are-sent-during-a-channel", TimeoutSeconds = 300)]
    public static void TheGuardianParChangesAreSentDuringAChannel()
    {
        using var match = StartMatch();
        var seat = Observe(match, Team.Order);
        var guardian = match.Guardian;
        match.Order.Position = Offset(guardian, 300.0);
        TestAssert.True(match.Runtime.Spells.BeginCaptureChannel(match.Order, guardian), "Order starts the capture channel on point C");
        var rings = new List<float> { (float)guardian.Mana };
        var last = guardian.Mana;
        for (var tick = 0; tick < 300; tick++)
        {
            match.Runtime.Tick(Step);
            if (guardian.Mana == last) continue;
            last = guardian.Mana;
            rings.Add((float)last);
        }
        TestAssert.True(rings.Count > 1, $"the guardian's PAR changes during the channel (it stayed at {guardian.Mana:F0})");
        var sent = ParBlocks(seat, guardian.NetId).Select(block => block.Current).ToList();
        TestAssert.True(sent.Count > 0 && sent.All(value => rings.Any(ring => Math.Abs(ring - value) < 0.5f)) &&
                        sent.Zip(sent.Skip(1)).All(pair => pair.Second >= pair.First),
            $"the ring goes out as the pool's own steps and never back (rings {string.Join(", ", rings.Select(v => v.ToString("F0")))}; sent {string.Join(", ", sent.Select(v => v.ToString("F0")))})");
    }

    /// <summary>
    /// Batch 479: the replacement guardian of a captured point carries the pool and the PAR the tree sets: MaxMana 50000 and the full pool, in its
    /// 0xCC on the wire. Batch 512: the pool is the tree's progress under the new owner (CaptureMeterPool), so it starts where the flip happened
    /// (above 99 of 100) and fills as the owner's regen takes the progress to 100.
    /// </summary>
    [Check("dominion.guardian-replacement-carries-the-pool-and-par", TimeoutSeconds = 300)]
    public static void TheReplacementGuardianCarriesThePoolAndItsPar()
    {
        using var match = StartMatch();
        var seat = Observe(match, Team.Order);
        var previous = match.Guardian;
        Capture(match, match.Order);
        var replacement = match.Guardian;
        TestAssert.True(replacement.NetId != previous.NetId, "the capture replaces point C's guardian");
        TestAssert.True(replacement.MaxMana == 50000.0, $"the replacement has MaxMana 50000 (was {replacement.MaxMana:F0})");
        TestAssert.True(replacement.Mana >= 0.99 * 50000.0, $"the replacement of an owned point starts at the flip's progress, 99 of 100 or more (PAR {replacement.Mana:F0})");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(Step);
        var blocks = ParBlocks(seat, replacement.NetId);
        TestAssert.True(blocks.Count > 0 && blocks[^1] == (50000f, 50000f),
            $"the replacement's last 0xCC carries PAR and max PAR 50000 ({(blocks.Count > 0 ? $"{blocks[^1].Current:F0}/{blocks[^1].Max:F0}" : "none sent")})");
    }

    /// <summary>
    /// Batch 493: the guardian a capture replaces ends its buffs before it leaves, so every odin_suppression and Odin-Capture-Filling particle bound to it
    /// (the capture sounds: OdinGuardianSuppression's and OdinCaptureSoundFilling's BBSpellEffectRemove in OnBuffDeactivate) gets its 0x3D. The 0x8C
    /// carries the name hash at 6, the effect at 25 and the bound unit at 29; the 0x3D the effect at 5.
    /// </summary>
    [Check("dominion.replaced-guardian-kills-its-capture-particles", TimeoutSeconds = 300)]
    public static void TheReplacedGuardianKillsItsCaptureParticles()
    {
        using var match = StartMatch();
        var seat = Observe(match, Team.Order);
        var previous = match.Guardian;
        Capture(match, match.Order);
        TestAssert.True(match.Guardian.NetId != previous.NetId, "the capture replaces point C's guardian");
        var names = new[] { SpellPackets.BuffNameHash("odin_suppression"), SpellPackets.BuffNameHash("Odin-Capture-Filling") };
        var created = seat.Packets.Select(packet => packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length >= 33 && bytes[0] == 0x8C && names.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6))) &&
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(29)) == previous.NetId)
            .Select(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25))).ToHashSet();
        var killed = seat.Packets.Select(packet => packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length >= 9 && bytes[0] == 0x3D)
            .Select(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5))).ToHashSet();
        TestAssert.True(created.Count > 0, "the capture channel's particles were created on the old guardian");
        TestAssert.True(created.All(killed.Contains),
            $"every capture particle of the old guardian is killed (alive: {string.Join(", ", created.Where(id => !killed.Contains(id)).Select(id => $"0x{id:X8}"))})");
    }

    /// <summary>A replication observer's packets: the seat's client, as the replication layer gives it to the client.</summary>
    private sealed class SeatCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private static SeatCapture Observe(Match match, Team team)
    {
        var participant = match.Runtime.Session.Participants.Single(seat => seat.Team == team);
        var capture = new SeatCapture();
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId, participant.Team, participant.Hero!.NetId, capture,
            ready: true, view: participant.View, clientId: participant.ClientId));
        return capture;
    }

    /// <summary>
    /// The (CurrentMana, MaxMana) of each 0xCC block a seat was sent for a unit: the PAR block (setMaps 0x02, mask 0x0A: mMP at byte 19, mMaxMP at 23)
    /// and a full minion block (mask 0x1FFFFF: mMP at byte 23, mMaxMP at 31).
    /// </summary>
    private static List<(float Current, float Max)> ParBlocks(SeatCapture seat, uint netId)
    {
        var blocks = new List<(float Current, float Max)>();
        foreach (var packet in seat.Packets)
        {
            var span = packet.Plaintext.Span;
            if (span.Length < 19 || span[0] != 0xCC || BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(11)) != netId) continue;
            if (span.Length == 27 && BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(15)) == 0x0A)
                blocks.Add((BitConverter.ToSingle(span.Slice(19, 4)), BitConverter.ToSingle(span.Slice(23, 4))));
            else if (span.Length == 111)
                blocks.Add((BitConverter.ToSingle(span.Slice(23, 4)), BitConverter.ToSingle(span.Slice(31, 4))));
        }
        return blocks;
    }

    private static Match StartMatch(bool secondChaos = false)
    {
        SessionParticipantDefinition[] roster =
        [
            new(1, 0, "Order", "Annie", Team.Order),
            new(2, 1, "Chaos", "Annie", Team.Chaos),
        ];
        if (secondChaos) roster = [.. roster, new(3, 2, "Chaos2", "Annie", Team.Chaos)];
        // A map 8 server process reads map 8's files as its default (Program.cs, GameData.ConfigureDefault): the combat sight grid included.
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            var connection = 100L + index;
            TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection), $"seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the map 8 match starts");
        runtime.Tick(Step);
        var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests;
        TestAssert.True(quests is { Captures.Count: 5 }, "map 8 runs its five capture points");
        var heroes = runtime.Session.Participants.Select(participant => participant.Hero!).ToList();
        var order = heroes.Single(hero => hero.Team == Team.Order);
        var chaos = heroes.First(hero => hero.Team == Team.Chaos);
        var at = (Position3)quests!.Captures[PointIndex].Variable("CapturePosition")!;
        var guardian = runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == GuardianName)
            .OrderBy(unit => Distance(unit.Position, new Position2(at.X, at.Z))).First();
        return new Match(runtime, order, chaos, guardian, quests, order.Position, chaos.Position);
    }

    /// <summary>
    /// Batch 475b (review): an enemy channel that is aimed at a guardian when the guardian is replaced keeps going on the replacement. Order captures
    /// point C (its own channel ends as a success). Chaos then channels the Order-owned point: the point goes neutral, which replaces the guardian
    /// again, and Chaos's channel is aimed at the neutral replacement and neutralises the point with it, as the shipped OdinCaptureChannel does. The shipped
    /// contest stops a capture while both teams' champions count (CapturePointMath, "Chaos Control" needs no Order champion), so the channel is
    /// started on the owned point. Reconstruction: retail's guardian changes team in place, so the channel keeps the same unit.
    /// </summary>
    [Check("dominion.enemy-channel-follows-the-replaced-guardian", TimeoutSeconds = 300)]
    public static void AnEnemyChannelFollowsTheReplacedGuardian()
    {
        using var match = StartMatch();
        var (runtime, chaos) = (match.Runtime, match.Chaos);
        Capture(match, match.Order);
        TestAssert.True(Owner(match) == Team.Order, "Order holds point C");
        var owned = match.Guardian;
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(owned, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, owned), "Chaos starts the capture channel on the Order-owned guardian");
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Neutral, 60.0), "Chaos's channel neutralises point C");
        // Batch 536: the shipped Neutralization's KillSquad (OdinScriptBehaviorTrees.xml:1470) killed the owned guardian first (its 0xA6, its
        // buffs gone with the death), so nothing was carried and the channel cancelled itself on the replacement (ChannelingUpdateStats block 5).
        TestAssert.True(owned.Alive, "the neutralised guardian leaves without a death (no KillSquad kill)");
        var neutralGuardian = match.Guardian;
        TestAssert.True(!ReferenceEquals(neutralGuardian, owned), "the point's guardian is re-created on the neutral team");
        var aimed = runtime.Spells.CaptureChannelTarget(chaos);
        TestAssert.True(aimed == neutralGuardian,
            $"Chaos's channel is aimed at the replacement guardian, not the removed one (aimed at {(aimed is null ? "nothing" : $"0x{aimed.NetId:X8}")}, " +
            $"replacement 0x{neutralGuardian.NetId:X8}, removed 0x{owned.NetId:X8}; Chaos buffs {string.Join(",", chaos.Buffs.Select(b => b.Name))}; " +
            $"replacement buffs {string.Join(",", neutralGuardian.Buffs.Select(b => b.Name))})");
    }

    /// <summary>
    /// Batch 550: a Priscilla's Blessing holder's channel keeps its blessing through the replacement. OdinCaptureChannel's ChannelingStart puts the
    /// holder's PrilisasBlessing on the guardian it channels (Items/3183.luaobj keeps the buff on the holder); retail's guardian changes team in
    /// place, so the copy stays, and the replacement carries it with the channel's team stack (BehaviorTreeQuestHost.GuardianTakesOwner).
    /// </summary>
    [Check("dominion.prilisas-blessing-follows-the-replaced-guardian", TimeoutSeconds = 300)]
    public static void PriscillasBlessingFollowsTheReplacedGuardian()
    {
        using var match = StartMatch();
        var (runtime, chaos) = (match.Runtime, match.Chaos);
        chaos.Inventory = [new Amberline.Simulation.InventorySlot(3183), null, null, null, null, null];
        runtime.HeroStats.Refresh(chaos);
        Capture(match, match.Order);
        TestAssert.True(chaos.Buffs.Any(buff => buff.Name == "PrilisasBlessing"), "Items/3183 keeps PrilisasBlessing on its holder");
        var owned = match.Guardian;
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(owned, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, owned), "Chaos starts the capture channel on the Order-owned guardian");
        TestAssert.True(Until(runtime, () => owned.Buffs.Any(buff => buff.Name == "PrilisasBlessing"), 1.0),
            "ChannelingStart copies the holder's blessing onto the guardian");
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Neutral, 60.0), "Chaos's channel neutralises point C");
        var replacement = match.Guardian;
        TestAssert.True(!ReferenceEquals(replacement, owned) && runtime.Spells.CaptureChannelTarget(chaos) == replacement,
            "Chaos's channel goes on with the neutral replacement");
        TestAssert.Equal(1, replacement.Buffs.Count(buff => buff.Name == "PrilisasBlessing" && buff.CasterNetId == chaos.NetId),
            $"the replacement carries the channel's blessing copy (buffs {string.Join(",", replacement.Buffs.Select(b => b.Name))})");
    }

    /// <summary>
    /// Batch 567 (Reconstruction): a neutralise replaces the guardian, and what it carries keeps its owner and its clock, as retail's guardian keeps
    /// its instances. Two Chaos champions channel the Order-owned point a second apart. The replacement holds each one's OdinGuardianSuppressionChaos
    /// (its own caster and expiry: OdinCaptureChannel ChannelingStart block 9, BUFF_STACKS_AND_RENEWS per caster) and the suppression with the start
    /// of the instance it replaces. So the suppression's update (BuffOnUpdateActions block 1, every 0.25 s, from TimePassed 1.5) adds the neutral
    /// branch's capture sound, OdinCaptureSoundFilling (block 1.3.7.10.3), at its next line; with a re-stamped StartTime it waited 1.5 s.
    /// </summary>
    [Check("dominion.replaced-guardian-keeps-each-casters-stack-and-the-suppression-clock", TimeoutSeconds = 300)]
    public static void TheReplacedGuardianKeepsEachCastersStackAndTheSuppressionClock()
    {
        using var match = StartMatch(secondChaos: true);
        var (runtime, chaos) = (match.Runtime, match.Chaos);
        var second = runtime.Session.Participants.Select(seat => seat.Hero!).Single(hero => hero.Team == Team.Chaos && hero != chaos);
        Capture(match, match.Order);
        var owned = match.Guardian;
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(owned, -300.0);
        second.Position = new Position2(owned.Position.X - 300.0, owned.Position.Z + 100.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, owned), "the first Chaos champion channels the Order-owned guardian");
        Advance(runtime, 1.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(second, owned), "the second Chaos champion channels it a second later");
        const string ChaosStack = "OdinGuardianSuppressionChaos";
        TestAssert.True(Until(runtime, () => owned.Buffs.Count(buff => buff.Name == ChaosStack) == 2, 1.0), "each Chaos caster owns a stack");
        // BuffState.ExpiresAt is the time left (BuffLifecycle.Tick counts it down); each stack's end as a game time.
        var stacks = owned.Buffs.Where(buff => buff.Name == ChaosStack)
            .ToDictionary(buff => buff.CasterNetId, buff => runtime.Combat.CurrentTime + buff.ExpiresAt);
        TestAssert.True(stacks.ContainsKey(chaos.NetId) && stacks.ContainsKey(second.NetId), "the two stacks name their two casters");
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Neutral, 60.0), "the two channels neutralise point C");
        var replacement = match.Guardian;
        TestAssert.True(!ReferenceEquals(replacement, owned), "the point's guardian is re-created on the neutral team");
        foreach (var (caster, endsAt) in stacks)
            TestAssert.True(replacement.Buffs.Any(buff => buff.Name == ChaosStack && buff.CasterNetId == caster &&
                    Math.Abs(runtime.Combat.CurrentTime + buff.ExpiresAt - endsAt) < 2 * Step),
                $"the replacement carries 0x{caster:X8}'s stack, ending at its own {endsAt:0.000} (now {runtime.Combat.CurrentTime:0.000}, has " +
                $"{string.Join(",", replacement.Buffs.Where(buff => buff.Name == ChaosStack).Select(buff => $"0x{buff.CasterNetId:X8}+{buff.ExpiresAt:0.000}"))})");
        TestAssert.True(Until(runtime, () => replacement.Buffs.Any(buff => buff.Name == "OdinCaptureSoundFilling"), 0.5),
            $"the carried suppression keeps its clock: the capture sound goes on within one update (buffs {string.Join(",", replacement.Buffs.Select(b => b.Name))})");
        // The channels that neutralised the point go on to take it, as on retail's one guardian (no second click).
        TestAssert.True(Until(runtime, () => Owner(match) == Team.Chaos ||
                runtime.Spells.CaptureChannelTarget(chaos) is null || runtime.Spells.CaptureChannelTarget(second) is null, 30.0) &&
            Owner(match) == Team.Chaos, $"both Chaos channels go on until point C is Chaos's (owner {Owner(match)}, " +
            $"channels {runtime.Spells.CaptureChannelTarget(chaos) is not null}/{runtime.Spells.CaptureChannelTarget(second) is not null})");
    }

    /// <summary>The champion channels the capture on point C beside its guardian until the point is its team's.</summary>
    private static void Capture(Match match, Entity hero)
    {
        hero.Position = Offset(match.Guardian, hero.Team == Team.Order ? 300.0 : -300.0);
        TestAssert.True(match.Runtime.Spells.BeginCaptureChannel(hero, match.Guardian), $"{hero.Team} starts the capture channel");
        TestAssert.True(Until(match.Runtime, () => Owner(match) == hero.Team, 20.0), $"point C becomes {hero.Team}'s");
        TestAssert.True(Until(match.Runtime, () => match.Runtime.Spells.CaptureChannelTarget(hero) is null, 1.0), "the capture channel ends");
    }

    private static Team Owner(Match match) => match.Quests.CapturePointOwners.GetValueOrDefault(PointIndex + 1);

    /// <summary>The time the guardian's Spell1 is ready again (its cast starts the 1 s cooldown); 0 before its first cast.</summary>
    private static double CastAt(Entity guardian) => guardian.ScriptSlotReadyAt.GetValueOrDefault(0);

    private static Position2 Offset(Entity unit, double x) => new(unit.Position.X + x, unit.Position.Z);

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool Order(MatchRuntime runtime, Entity hero, byte order, Position2 destination, uint targetNetId)
    {
        var payload = new byte[22];
        payload[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        payload[5] = order;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), BitConverter.SingleToInt32Bits((float)destination.X));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(14), BitConverter.SingleToInt32Bits((float)destination.Z));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), targetNetId);
        // DIAGNOSTICS: test seam, the production order handler called directly (DominionCaptureChannelOrderChecks' pattern).
        var participant = runtime.Session.Participants.Single(seat => seat.Hero == hero);
        var target = targetNetId == 0 ? null : runtime.World.Get(targetNetId);
        return runtime.Dependencies.Gameplay(new SessionGameplayContext(participant, hero, 1, payload,
            targetNetId == 0 ? null : targetNetId, target, hero));
    }

    private static bool Until(MatchRuntime runtime, Func<bool> done, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++)
        {
            if (done()) return true;
            runtime.Tick(Step);
        }
        return done();
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) runtime.Tick(Step);
    }
}
