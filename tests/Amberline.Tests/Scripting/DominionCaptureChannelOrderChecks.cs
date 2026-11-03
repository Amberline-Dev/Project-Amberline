using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Batch 449: a champion's order aimed at a capture point guardian starts the shipped OdinCaptureChannel on map 8. Maps 1 and 2 keep
/// the ordinary order handling (the control check).
/// </summary>
public static class DominionCaptureChannelOrderChecks
{
    private const string Channel = "OdinCaptureChannel";
    private const string Suppression = "OdinGuardianSuppression";
    // Capture point A of Map8 (Info_PointA.SCB centre, as batch 446's check reads it).
    private static readonly Position2 Point = PointA();

    private static Position2 PointA()
    {
        var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
        var centre = SceneMesh.CentralPoint(Path.Combine(scene, "Info_PointA.SCB"));
        return new Position2(centre.X, centre.Z);
    }

    [Check("dominion.capture-channel-starts-on-attack-order")]
    public static void AttackOrderOnAGuardianInRangeStartsTheChannel()
    {
        var runtime = StartMatch(GameMaps.CrystalScar);
        var hero = runtime.Session.Participants.Single().Hero!;
        var guardian = AddGuardian(runtime, Point);
        hero.Position = new Position2(Point.X + 200, Point.Z);
        var start = hero.Position;
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the attack order is accepted");
        Advance(runtime, 1.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == Channel),
            "an attack order on a capture guardian in range starts OdinCaptureChannel on the champion");
        // ChannelingStart suppresses the guardian with OdinGuardianSuppression and, by the champion's team, the Order or Chaos variant.
        TestAssert.True(guardian.Buffs.Any(buff => buff.Name.StartsWith(Suppression, StringComparison.Ordinal)),
            "the channel's ChannelingStart suppresses the guardian (OdinGuardianSuppression) failures: " + string.Join(" / ", (runtime.Spells.SummonerScripts?.ScriptFailures ?? []).Select(f => f.Hook + "@" + f.BlockPath + ": " + f.Message)) + " guardian buffs: " + string.Join(",", guardian.Buffs.Select(b => b.Name)));
        // Batch 461: the capture's stop seats the hero on its lattice anchor (MovementSystem.InstallStop), where the client seats it too.
        TestAssert.True(hero.Position == runtime.Spells.Movement.ClientAnchor(start),
            $"a channel in range starts without a walk ({start} to {hero.Position})");
    }

    /// <summary>
    /// Batch 461: the player's flow on the real point A (its guardian and its capture tree): the champion walks toward the point and is ordered
    /// onto the guardian once in range, while still walking. The channel holds with no other input, and the point becomes Order's.
    /// </summary>
    [Check("dominion.capture-channel-holds-when-ordered-while-walking")]
    public static void AnAttackOrderWhileWalkingKeepsTheChannelAndTakesThePoint()
    {
        var (runtime, hero, guardian, quests) = WalkIntoRangeAndOrder();
        Advance(runtime, 5.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == Channel) && runtime.Spells.CaptureChannelTarget(hero) == guardian,
            "5 s after the order the champion still channels OdinCaptureChannel on point A's guardian (buffs: " +
            string.Join(",", hero.Buffs.Select(buff => buff.Name)) + ")");
        TestAssert.True(Until(runtime, () => quests.CapturePointOwners.GetValueOrDefault(1) == Team.Order, 10.0),
            "point A becomes Order's within 15 s of the order (progress " + quests.Captures[0].Variable("CaptureProgress") + ")");
    }

    /// <summary>
    /// Batch 461: the channel ends when the point is taken, by the shipped script's own success branch (ChannelingUpdateStats: the guardian's
    /// team is the champion's, BBStopChanneling Success), not by its 30 s or by a cancel: it holds until the point's owner changes.
    /// </summary>
    [Check("dominion.capture-channel-ends-when-the-point-is-taken")]
    public static void TheChannelEndsWhenThePointIsTaken()
    {
        var (runtime, hero, guardian, quests) = WalkIntoRangeAndOrder();
        TestAssert.True(Until(runtime, () => runtime.Spells.CaptureChannelTarget(hero) is null ||
                quests.CapturePointOwners.GetValueOrDefault(1) == Team.Order, 15.0) &&
            quests.CapturePointOwners.GetValueOrDefault(1) == Team.Order,
            "the channel holds until point A is Order's");
        TestAssert.True(Until(runtime, () => runtime.Spells.CaptureChannelTarget(hero) is null, 0.5),
            "the channel ends within 0.5 s of the point being taken (guardian team " + guardian.Team + ")");
        TestAssert.True(guardian.Team == Team.Order, "the point's guardian is the capturing team's");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == Channel), "ChannelingSuccessStop clears OdinCaptureChannel");
    }

    /// <summary>
    /// Batch 461: an attack order on a guardian out of range walks the champion first (batch 449) and starts the channel when the champion
    /// comes within the capture range, as an attack order closes in on its target before it swings.
    /// </summary>
    [Check("dominion.capture-channel-starts-on-arrival")]
    public static void AnAttackOrderOutOfRangeStartsTheChannelOnArrival()
    {
        var (runtime, hero, guardian, _) = MatchOnPointA();
        hero.Position = new Position2(guardian.Position.X + 900, guardian.Position.Z);
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the order is accepted");
        TestAssert.True(Until(runtime, () => runtime.Spells.CaptureChannelTarget(hero) == guardian, 6.0),
            $"the channel starts once the champion has walked into range (at {hero.Position}, guardian {guardian.Position})");
        var gap = Math.Sqrt((hero.Position.X - guardian.Position.X) * (hero.Position.X - guardian.Position.X) +
                            (hero.Position.Z - guardian.Position.Z) * (hero.Position.Z - guardian.Position.Z));
        TestAssert.True(gap <= 601.0 && gap > 500.0, $"it starts at the edge of the 600 range ({gap:F0} from the guardian)");
        Advance(runtime, 3.0);
        TestAssert.True(runtime.Spells.CaptureChannelTarget(hero) == guardian && hero.Buffs.Any(buff => buff.Name == Channel),
            "the channel started on arrival holds");
    }

    /// <summary>Batch 461: a fresh move order replaces the walk toward the guardian; the new walk passes through the range and starts nothing.</summary>
    [Check("dominion.capture-pending-dropped-by-a-move-order")]
    public static void AMoveOrderDropsAPendingCapture()
    {
        var (runtime, hero, guardian) = OrderFromOutOfRange();
        TestAssert.True(Order(runtime, hero, 2, new Position2(guardian.Position.X - 700, guardian.Position.Z + 250), 0), "the move order is accepted");
        TestAssert.False(EverChannels(runtime, hero, 4.0), "the move walks past the guardian and starts no capture");
    }

    /// <summary>Batch 461: an emote stops the walk toward the guardian, and a later step that finds the champion in range starts nothing.</summary>
    [Check("dominion.capture-pending-dropped-by-an-emote")]
    public static void AnEmoteDropsAPendingCapture()
    {
        var (runtime, hero, guardian) = OrderFromOutOfRange();
        var emote = new byte[9];
        emote[0] = 0x4C;
        BinaryPrimitives.WriteUInt32LittleEndian(emote.AsSpan(1), hero.NetId);
        TestAssert.True(Dispatch(runtime, hero, emote, null), "the emote is accepted");
        Advance(runtime, 0.1);
        // DIAGNOSTICS: test seam (the champion is put in range without an order).
        hero.Position = new Position2(guardian.Position.X + 300, guardian.Position.Z);
        TestAssert.False(EverChannels(runtime, hero, 1.0), "no capture starts after the emote");
    }

    /// <summary>
    /// Batch 461: crowd control takes the champion off its walk (the lock clears the route), so the capture is not started under it or after it:
    /// a stun (an action lock) and a root, each with the champion put in range while it lasts.
    /// </summary>
    [Check("dominion.capture-pending-dropped-by-crowd-control")]
    public static void CrowdControlDropsAPendingCapture()
    {
        foreach (var (name, type) in new[] { ("Stun", Amberline.Core.Buffs.BuffType.Stun), ("Snare", Amberline.Core.Buffs.BuffType.Snare) })
        {
            var (runtime, hero, guardian) = OrderFromOutOfRange();
            TestAssert.True(runtime.Buffs.AddBuff(hero, name, type, 1.0, hero.NetId) is not null, $"{name}: the champion is controlled");
            // DIAGNOSTICS: test seam (the champion is put in range without an order).
            hero.Position = new Position2(guardian.Position.X + 300, guardian.Position.Z);
            TestAssert.False(EverChannels(runtime, hero, 2.0), $"{name}: no capture starts under it or after it ends");
        }
    }

    /// <summary>
    /// Batch 550: Priscilla's Blessing (3183). Items/3183.luaobj keeps PrilisasBlessing on its holder (first add 5 s after the copy is bound),
    /// OdinCaptureChannel's ChannelingStart copies it onto the guardian for the channel's life (both stops take it back), and the point tree's
    /// champion rate takes OdinGuardianSuppression's ratio for it, 1.31 / 1.06 for one channeller (BehaviorLeaves.CaptureRateScale). The
    /// control is the same capture without the item.
    /// </summary>
    [Check("dominion.prilisas-blessing-speeds-the-capture")]
    public static void PriscillasBlessingSpeedsTheCapture()
    {
        var plain = CaptureGain(blessed: false);
        var blessed = CaptureGain(blessed: true);
        TestAssert.True(plain > 0.0, $"the plain channel moves point A ({plain})");
        TestAssert.Near(1.31 / 1.06, blessed / plain, 1e-6, $"the blessed rate is 1.31 / 1.06 of the plain one ({blessed} / {plain})");
    }

    // Point A's progress over 2 s of a charged channel, with or without Priscilla's Blessing in the champion's inventory.
    private static double CaptureGain(bool blessed)
    {
        var (runtime, hero, guardian, quests) = MatchOnPointA();
        if (blessed) hero.Inventory = [new Amberline.Simulation.InventorySlot(3183), null, null, null, null, null];
        runtime.HeroStats.Refresh(hero);
        Advance(runtime, 6.0);
        TestAssert.Equal(blessed, hero.Buffs.Any(buff => buff.Name == "PrilisasBlessing"), "Items/3183 keeps PrilisasBlessing on its holder");
        // DIAGNOSTICS: test seam (the champion is put in range of the guardian).
        hero.Position = new Position2(guardian.Position.X + 200, guardian.Position.Z);
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the capture order is accepted");
        TestAssert.True(Until(runtime, () => runtime.Spells.ChargedCaptureTarget(hero) == guardian, 3.0), "the channel charges");
        Advance(runtime, 0.1);
        TestAssert.Equal(blessed, guardian.Buffs.Any(buff => buff.Name == "PrilisasBlessing"),
            "ChannelingStart copies the holder's blessing onto the guardian");
        var before = Progress(quests);
        Advance(runtime, 2.0);
        var gain = Progress(quests) - before;
        Order(runtime, hero, 2, new Position2(guardian.Position.X + 900, guardian.Position.Z), 0);
        Advance(runtime, 0.5);
        TestAssert.False(guardian.Buffs.Any(buff => buff.Name == "PrilisasBlessing"), "the channel's cancel takes the copy back");
        return gain;
    }

    private static double Progress(Amberline.Simulation.BehaviorTrees.BehaviorTreeQuestHost quests) =>
        Convert.ToDouble(quests.Captures[0].Variable("CaptureProgress") ?? 0.0, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The champion 900 from point A's guardian, ordered onto it, 0.1 s into its walk (still out of range).</summary>
    private static (MatchRuntime Runtime, Entity Hero, Entity Guardian) OrderFromOutOfRange()
    {
        var (runtime, hero, guardian, _) = MatchOnPointA();
        hero.Position = new Position2(guardian.Position.X + 900, guardian.Position.Z);
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the attack order is accepted");
        Advance(runtime, 0.1);
        TestAssert.True(runtime.Spells.CaptureChannelTarget(hero) is null, "precondition: no capture yet");
        return (runtime, hero, guardian);
    }

    /// <summary>Whether the champion channels the capture at any step in <paramref name="seconds"/>, or carries the channel's end buff.</summary>
    private static bool EverChannels(MatchRuntime runtime, Entity hero, double seconds) =>
        Until(runtime, () => runtime.Spells.CaptureChannelTarget(hero) is not null ||
            hero.Buffs.Any(buff => buff.Name is Channel or "OdinCaptureChannelCooldownBuff"), seconds);

    /// <summary>Map 8 with its Dominion host; the real guardian of point A (the capture tree's own) and the quests.</summary>
    private static (MatchRuntime Runtime, Entity Hero, Entity Guardian, Amberline.Simulation.BehaviorTrees.BehaviorTreeQuestHost Quests) MatchOnPointA()
    {
        var runtime = StartMatch(GameMaps.CrystalScar);
        var hero = runtime.Session.Participants.Single().Hero!;
        Advance(runtime, 1.0 / 30.0);
        var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests;
        TestAssert.True(quests is not null && quests.Captures.Count > 0, "map 8 runs its capture points");
        var guardian = runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
            .OrderBy(unit => (unit.Position.X - Point.X) * (unit.Position.X - Point.X) + (unit.Position.Z - Point.Z) * (unit.Position.Z - Point.Z))
            .FirstOrDefault();
        TestAssert.True(guardian is not null, "point A has its guardian");
        return (runtime, hero, guardian!, quests!);
    }

    /// <summary>The champion walks toward point A's guardian from 900 away and is ordered onto it 400 away, still walking.</summary>
    private static (MatchRuntime Runtime, Entity Hero, Entity Guardian, Amberline.Simulation.BehaviorTrees.BehaviorTreeQuestHost Quests) WalkIntoRangeAndOrder()
    {
        var (runtime, hero, guardian, quests) = MatchOnPointA();
        hero.Position = new Position2(guardian.Position.X + 900, guardian.Position.Z);
        TestAssert.True(Order(runtime, hero, 2, guardian.Position, 0), "the move order is accepted");
        TestAssert.True(Until(runtime, () => hero.Position.X - guardian.Position.X <= 400.0, 5.0), "the champion walks into range");
        TestAssert.True(hero.Waypoints.Count > 0, "precondition: the champion is still walking");
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the attack order is accepted");
        return (runtime, hero, guardian, quests);
    }

    /// <summary>Steps until <paramref name="done"/> holds or <paramref name="seconds"/> pass; whether it held.</summary>
    private static bool Until(MatchRuntime runtime, Func<bool> done, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds * 30.0); tick++)
        {
            if (done()) return true;
            runtime.Tick(1.0 / 30.0);
        }
        return done();
    }


    [Check("dominion.capture-channel-breaks-on-move-order")]
    public static void AFreshMoveOrderBreaksTheChannel()
    {
        var runtime = StartMatch(GameMaps.CrystalScar);
        var hero = runtime.Session.Participants.Single().Hero!;
        var guardian = AddGuardian(runtime, Point);
        hero.Position = new Position2(Point.X + 200, Point.Z);
        Order(runtime, hero, 3, guardian.Position, guardian.NetId);
        Advance(runtime, 1.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == Channel), "precondition: the channel runs");
        Order(runtime, hero, 2, new Position2(Point.X - 400, Point.Z), 0);
        Advance(runtime, 1.0);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == Channel),
            "a fresh move order ends OdinCaptureChannel (CancelChannel \"order\")");
    }

    [Check("dominion.capture-channel-breaks-on-hero-damage")]
    public static void DamageFromAChampionBreaksTheChannel()
    {
        var runtime = StartMatch(GameMaps.CrystalScar);
        var hero = runtime.Session.Participants.Single().Hero!;
        var guardian = AddGuardian(runtime, Point);
        hero.Position = new Position2(Point.X + 200, Point.Z);
        Order(runtime, hero, 3, guardian.Position, guardian.NetId);
        Advance(runtime, 1.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == Channel), "precondition: the channel runs");
        var enemy = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(Point.X + 400, Point.Z))
        {
            ChampionName = "Annie", Alive = true, Created = true, Visible = true, Radius = 65,
            Health = 500, MaxHealth = 500,
        };
        runtime.World.Register(enemy);
        TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 40.0), "the champion's hit lands");
        Advance(runtime, 0.5);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == Channel),
            "BuffOnPreDamage (a hero attacker, not periodic) cancels OdinCaptureChannel");
    }

    [Check("dominion.capture-channel-out-of-range-walks-first")]
    public static void AnAttackOrderOutOfRangeWalksAndDoesNotChannel()
    {
        var runtime = StartMatch(GameMaps.CrystalScar);
        var hero = runtime.Session.Participants.Single().Hero!;
        var guardian = AddGuardian(runtime, Point);
        // Out of the 600 capture range, 800 from the point.
        hero.Position = new Position2(Point.X + 800, Point.Z);
        var start = hero.Position;
        TestAssert.True(Order(runtime, hero, 3, guardian.Position, guardian.NetId), "the order is accepted in sight");
        // Batch 461: the channel now starts on arrival (dominion.capture-channel-starts-on-arrival), about 0.6 s in from 800; the walk comes first.
        Advance(runtime, 0.2);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == Channel), "no channel out of range");
        TestAssert.True(hero.Position != start, "the champion walks toward the guardian first");
    }

    [Check("dominion.capture-channel-maps-one-and-two-order-unchanged")]
    public static void MapOneAndTwoDoNotStartACaptureChannel()
    {
        foreach (var map in new[] { GameMaps.Normal, GameMaps.Harrowing })
        {
            var runtime = StartMatch(map);
            var hero = runtime.Session.Participants.Single().Hero!;
            var guardian = AddGuardian(runtime, Point);
            hero.Position = new Position2(Point.X + 200, Point.Z);
            Order(runtime, hero, 3, guardian.Position, guardian.NetId);
            Advance(runtime, 1.0);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == Channel),
                $"map {map}: the attack order on a guardian does not start the capture channel");
        }
    }

    private static MatchRuntime StartMatch(int mapId)
    {
        var navigation = mapId == GameMaps.CrystalScar
            ? NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar))
            : NavigationGrid.LoadDefault();
        var runtime = MatchRuntime.CreateProduction(
            navigation, GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, mapId: mapId);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static Entity AddGuardian(MatchRuntime runtime, Position2 at)
    {
        var guardian = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, Team.Neutral, at)
        {
            Name = "OdinNeutralGuardian", ChampionName = "OdinNeutralGuardian", Alive = true, Created = true, Visible = true,
            Radius = 65, Health = 4000, MaxHealth = 4000, NotTargetableToAlly = true, NotTargetableToEnemy = true,
        };
        runtime.World.Register(guardian);
        return guardian;
    }

    private static bool Order(MatchRuntime runtime, Entity hero, byte order, Position2 destination, uint targetNetId)
    {
        var payload = new byte[22];
        payload[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        payload[5] = order;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), BitConverter.SingleToInt32Bits((float)destination.X));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(14), BitConverter.SingleToInt32Bits((float)destination.Z));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), targetNetId);
        // The production order handler, called directly: the session's sight gate (DuelSession.DispatchGameplay) does not reveal a
        // neutral guardian in this solo harness, so the check tests the order logic, not the view.
        // DIAGNOSTICS: test seam (the session gate is bypassed for the order handler only).
        return Dispatch(runtime, hero, payload, targetNetId == 0 ? null : targetNetId);
    }

    private static bool Dispatch(MatchRuntime runtime, Entity hero, byte[] payload, uint? targetNetId)
    {
        var participant = runtime.Session.Participants.Single();
        var target = targetNetId is { } id ? runtime.World.Get(id) : null;
        var context = new SessionGameplayContext(participant, hero, 1, payload, targetNetId, target, hero);
        return runtime.Dependencies.Gameplay(context);
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        const double step = 1.0 / 30.0;
        for (var tick = 0; tick < (int)Math.Round(seconds / step); tick++)
            runtime.Tick(step);
    }
}
