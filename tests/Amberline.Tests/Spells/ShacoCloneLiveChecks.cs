using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Pets;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-shaco-clone-owner-spacing")]
    public static void ShacoCloneCannotRemainInsideOwner()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            // Pet.lua OnInit leaves the clone AI_PET_IDLE and autonomous, and a clone inside the
            // return radius stays idle (TimerScanDistance only follows past GetPetReturnRadius), so
            // the ordinary acquisition is what would take the default enemy: keep it out of range to
            // check spacing. Before: the controller buff was seeded with Order 9.
            match.Hero(1).Position = new Position2(12000, 12000);
            var clone = runtime.Clones.Spawn(owner, owner, owner.Position,
                CloneKind.ShacoClone, owner.Health, "Hallucinate", 18.0,
                owner.ChampionName, showMinimapIcon: true);
            runtime.SpellBuffs.Add(clone, "HallucinateFull", BuffType.Internal, 18.0,
                owner.NetId, new BuffPayload());
            clone.Position = owner.Position;
            clone.Waypoints.Clear();
            clone.Target = null;
            clone.AttackTarget = null;
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
            TestAssert.True(clone.Waypoints.Count > 1,
                "an overlapped clone installs an explicit owner-escape route");
            // The pet phase queues the ring route after the movement phase;
            // one following simulation slice consumes that route.
            runtime.Tick(.6);
            var distance = Math.Sqrt(Math.Pow(clone.Position.X - owner.Position.X, 2) +
                Math.Pow(clone.Position.Z - owner.Position.Z, 2));
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            // owner decision 28 Sept 2026: original game values.
            TestAssert.True(distance >= clearance - 2.0 && distance <= 200.0,
                $"Shaco clone follow order restores a close, clear owner gap after overlap: team={team}, owner={owner.Position}, clone={clone.Position}, distance={distance}, route={string.Join(';', clone.Waypoints)}");
        }
    }

    [Check("spells-live-shaco-clone-target-keeps-owner-ring")]
    public static void ShacoCloneTargetApproachKeepsOwnerRing()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        owner.Position = new Position2(7000, 7000);
        enemy.Position = owner.Position;
        var clone = runtime.Clones.Spawn(owner, owner, owner.Position,
            CloneKind.ShacoClone, owner.Health, "Hallucinate", 18.0,
            owner.ChampionName, showMinimapIcon: true);
        runtime.SpellBuffs.Add(clone, "HallucinateFull", BuffType.Internal, 18.0,
            owner.NetId, new BuffPayload());

        runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
        TestAssert.Equal(enemy.NetId, clone.AttackTarget,
            "an in-range target remains acquired despite owner overlap");
        TestAssert.True(runtime.Combat.CanAttack(clone, enemy),
            "the clone can attack the coincident target immediately");
        TestAssert.True(runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == clone.NetId && hit.TargetNetId == enemy.NetId),
            "in-range attack queues a real basic hit without an owner-ring detour");
        TestAssert.False(runtime.Pets.LastDestinations.ContainsKey(clone.NetId),
            "combat does not invent a route when the target is already in range");
    }

    [Check("movement.shaco-clone-approach-walks-past-live-bodies")]
    public static void ShacoCloneApproachWalksPastLiveBodies()
    {
        // Restated in batch 254 (was movement.shaco-clone-approach-clears-live-bodies, which held the deleted body-aware planner's
        // clearance discs): the clone's approach is the engine planner's AttackTo plan to the target's position (the issue-order routine
        // -> the path builder), whose actor test meets the planted body, and the clone walks into attack reach.
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        owner.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7800, 7000);
        var clone = runtime.Clones.Spawn(owner, owner,
            owner.Position + new Position2(300, 0), CloneKind.ShacoClone,
            owner.Health, "Hallucinate", 18.0, owner.ChampionName,
            showMinimapIcon: true);
        runtime.SpellBuffs.Add(clone, "HallucinateFull", BuffType.Internal,
            18.0, owner.NetId, new BuffPayload());
        var body = runtime.World.Register(new Entity(
            runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7500, 7000))
        {
            Alive = true, Created = true, Visible = true,
            Speed = 325.0, Radius = 35.0, Health = 1000.0, MaxHealth = 1000.0,
        });

        // The target is 500 units away, beyond Shaco's shipped 400-unit
        // acquisition radius. An explicit attack order exercises pursuit;
        // an idle autonomous scan correctly leaves this target alone.
        var engine = runtime.Movement.PlanClientHeroRoute(clone, enemy.Position, static _ => true);
        runtime.Pets.Command(owner, 5, enemy.Position, enemy.NetId, clone.NetId);
        runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
        TestAssert.Equal(enemy.NetId, clone.AttackTarget!.Value,
            "the explicit clone attack retains its requested target");
        TestAssert.True(clone.Waypoints.Count > 1,
            "clone target approach retains a live routed path around a planted body");
        EnginePlannerRoutes.AssertSame(engine, clone.Waypoints, "the clone's approach");
        var reach = Amberline.Simulation.Combat.CombatService.AttackReach(clone, enemy);
        var nearest = double.PositiveInfinity;
        for (var tick = 0; tick < 40 && Distance2(clone.Position, enemy.Position) > reach; tick++)
        {
            runtime.Movement.Tick(.1);
            nearest = Math.Min(nearest, Distance2(clone.Position, body.Position));
        }
        TestAssert.True(Distance2(clone.Position, enemy.Position) <= reach,
            "the clone walks past the planted body into attack reach");
        TestAssert.True(nearest >= runtime.Movement.MovementRadius(clone) + runtime.Movement.MovementRadius(body) - 1.0,
            $"without walking through it (nearest {nearest:F1})");
    }

    [Check("spells-live-shaco-hallucinate-slot-lifecycle")]
    public static void ShacoHallucinateSlotLifecycleCoversCooldownModesAndReconnect()
    {
        // W33 (19 October 2026): Shaco runs from his scripts. HallucinateFull.luaobj has no BBSealSpellSlot and no recast gate, so the slot is
        // never sealed for the clone's life: the cooldown (set by the cast) is all that stops a second Hallucinate. The hand-written busy gate
        // (a non-retail rule: the R cast bit clear while the applicator or the clone existed, even with cooldowns off) is gone.
        using (var match = new Match("Shaco", Team.Order))
        {
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            owner.CooldownsDisabled = false;
            match.Cast(0, 3, owner);
            // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell); checked after the tick below.
            TestAssert.Near(0.0, owner.SpellCooldowns[3], 1e-9, "the press starts no cooldown");
            // Batch 168: the cast's cooldown travels in the cast answer's own cooldown field and the client starts the
            // slot from it (ApplyCostsOfSpell/AdjustVarsAfterSpellCast); no 0x8A is sent for the engine's cost write.
            // Hallucinate.luaobj writes no BBSetSlotSpellCooldownTime, so R has no cooldown packet at all.
            TestAssert.True(HasCastCooldown(match.Captures[0].Packets, owner, 3, expected: 100.0),
                "normal R publishes its cooldown in its cast answer");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x8A &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == owner.NetId &&
                packet.Plaintext.Span[5] == 3), "and no 0x8A repeats it (batch 168)");
            TestAssert.False(owner.SpellSeals.IsSealed(false, 3), "R acceptance does not seal the slot");

            runtime.Tick(.6);
            TestAssert.True(owner.SpellCooldowns[3] is > 99.3 and <= 100.0,
                $"normal R still owns its rank-one cooldown, from its cast frame ({owner.SpellCooldowns[3]:0.###})");
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive);
            match.Runtime.Session.Participants[0].View.Reset();
            match.Clear(); runtime.Delivery.Flush();
            var activeSpellName = runtime.GameData.Champion(owner.ChampionName).SpellNames[3];
            TestAssert.Equal("HallucinateFull", activeSpellName,
                $"fresh Shaco data retains the canonical R identity (observed {activeSpellName})");
            TestAssert.True(HasHallucinateCastBit(match.Captures[0].Packets, owner,
                busy: false), "reconnect shows the R cast bit set while the clone lives");
            var second = runtime.Spells.CanCast(owner, 3, owner.NetId,
                new Position3(owner.Position.X, 0.0, owner.Position.Z), runtime.Spells.CurrentTime);
            TestAssert.Equal(SpellCastFailure.Cooldown, second.Failure, "the cooldown alone refuses a second Hallucinate");

            match.Clear();
            runtime.Tick(18.1);
            TestAssert.False(clone.Alive, "the controller expiry removes the clone");
            match.Runtime.Session.Participants[0].View.Reset();
            match.Clear(); runtime.Delivery.Flush();
            var restoredSpellName = runtime.GameData.Champion(owner.ChampionName).SpellNames[3];
            TestAssert.Equal("HallucinateFull", restoredSpellName,
                $"fresh Shaco data restores the canonical R icon (observed {restoredSpellName})");
            TestAssert.True(HasHallucinateCastBit(match.Captures[0].Packets, owner,
                busy: false), "expiry leaves the R cast bit set");
        }

        using (var match = new Match("Shaco", Team.Order))
        {
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            owner.CooldownsDisabled = true;
            match.Cast(0, 3, owner);
            TestAssert.Near(0.0, owner.SpellCooldowns[3], 1e-9,
                "God cooldown mode leaves R's timer at zero");
            runtime.Tick(.6);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive);
            var open = runtime.Spells.CanCast(owner, 3, owner.NetId,
                new Position3(owner.Position.X, 0.0, owner.Position.Z),
                runtime.Spells.CurrentTime);
            TestAssert.True(open.Allowed,
                "with cooldowns off nothing stops R while a clone lives (no script gate; the hand-written busy gate was not retail)");

            match.Clear();
            runtime.Combat.Kill(clone, enemy.NetId);
            runtime.Delivery.Flush();
            // No gate means no republish at the clone's death; a reconnect snapshot shows the bit the state holds (as after the expiry above).
            match.Runtime.Session.Participants[0].View.Reset();
            match.Clear(); runtime.Delivery.Flush();
            TestAssert.True(HasHallucinateCastBit(match.Captures[0].Packets, owner,
                busy: false), "clone death leaves the R cast bit set");
        }

        using (var match = new Match("Shaco", Team.Order))
        {
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            owner.CooldownsDisabled = true;
            match.Cast(0, 3, owner);
            match.Clear();
            runtime.Combat.Kill(owner, enemy.NetId);
            runtime.Delivery.Flush();
            runtime.Tick(.5);
            TestAssert.False(runtime.World.Entities.Values.Any(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive),
                "death during the applicator window creates no clone (BBCloneUnitPet clones nothing for a dead owner)");
            TestAssert.False(owner.Buffs.Any(buff => buff.Name == "HallucinateApplicator"),
                "pre-applicator death clears the applicator");
            TestAssert.True(owner.ChampionState is null,
                "a script champion keeps no champion cast state");
        }
    }

    [Check("spells-live-shaco-clone-follow-replans-owner-turn")]
    public static void ShacoCloneFollowReplansOwnerMovementAndRouteSnap()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            owner.Position = new Position2(7000, 7000);
            // Pet.lua's own distance scan follows a clone past GetPetReturnRadius (500 here); the
            // default enemy must stay out of the ordinary acquisition's range. Before: the
            // controller buff was seeded with Order 9.
            match.Hero(1).Position = new Position2(12000, 12000);
            var clone = runtime.Clones.Spawn(owner, owner,
                owner.Position + new Position2(-500, 0), CloneKind.ShacoClone,
                owner.Health, "Hallucinate", 18.0, owner.ChampionName,
                showMinimapIcon: true);
            runtime.SpellBuffs.Add(clone, "HallucinateFull", BuffType.Internal,
                18.0, owner.NetId, new BuffPayload());

            runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
            var first = runtime.Pets.LastDestinations[clone.NetId];
            owner.Position = new Position2(7070, 7000);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .4, .2);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .6, .2);
            // Restated in batch 229 (owner, 25 October 2026: retail re-paths whenever its script or native state asks, no drift gate):
            // a 70-unit owner move re-paths the clone to him on the next scan.
            TestAssert.True(Distance2(runtime.Pets.LastDestinations[clone.NetId], owner.Position) <= 3.0,
                "a 70-unit owner move re-paths the clone to him (no follow-drift gate)");
            owner.Position = new Position2(7270, 7000);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .8, .2);
            var afterMove = runtime.Pets.LastDestinations[clone.NetId];
            TestAssert.True(Distance2(first, afterMove) > 20.0,
                "a 270-unit owner move is past the gate and replans the route");
            var clearance = runtime.Movement.BodyClearance(owner, clone);
            // owner decision 28 Sept 2026: original game values
            TestAssert.True(Distance2(afterMove, owner.Position) <= 3.0,
                "idle return replans toward the moving owner");

            runtime.Movement.Tick(.1);
            owner.Position = new Position2(6930, 7000);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + 1.0, .2);
            var afterReverse = runtime.Pets.LastDestinations[clone.NetId];
            TestAssert.True(Distance2(afterMove, afterReverse) > 20.0,
                "reversing the owner refreshes the clone route");
            TestAssert.True(Distance2(afterReverse, owner.Position) <= 3.0,
                "owner reversal replans the idle return toward the owner");

            var retained = clone.Waypoints.Count > 1
                ? clone.Waypoints.ToArray()
                : [clone.Position, clone.Position + new Position2(120, 0)];
            clone.Waypoints = retained.ToList();
            clone.Target = null;
            // Restated in batch 254: the clone's order is planned by the engine planner like every unit's (PlanMoveRoute; the deleted
            // body-aware planner kept the old route instead). A replacement the planner finds no route for is a stop: the non-hero's
            // route is cleared and its owner publishes the stop. DIAGNOSTICS: FindPathOverride stands in for the non-hero's planner.
            runtime.Navigation.FindPathOverride = (_, _) => [];
            var failedReplacement = runtime.Movement.SetMoveOrder(
                clone.NetId, owner.Position + new Position2(240, 0));
            runtime.Navigation.FindPathOverride = null;
            TestAssert.Equal(0, failedReplacement.Count, "a replacement with no route plans nothing");
            TestAssert.Equal(0, clone.Waypoints.Count, "and stops the clone instead of keeping the old route");

            // Restated in batch 254: the injected planner snap into the owner (FindPathOverride) stood in for the deleted body-aware
            // planner, which rejected it; the engine planner plans the recovery itself, and its end-cell snap (the nearest-cell search) which
            // keeps the end off the cells the owner covers.
            clone.Position = owner.Position;
            clone.Waypoints.Clear();
            runtime.Pets.Tick(runtime.Combat.CurrentTime + 1.2, .2);
            var snapped = runtime.Pets.LastDestinations[clone.NetId];
            TestAssert.True(Distance2(snapped, owner.Position) >= clearance,
                "overlap recovery aims outside the owner's physical body");
            TestAssert.True(clone.Waypoints.Count == 0 ||
                Distance2(clone.Waypoints[^1], owner.Position) >=
                    clearance - 2.0,
                "the emitted recovery endpoint remains outside the owner's body");
        }
    }

    private static double Distance2(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    [Check("spells-live-shaco-deceive-does-not-reacquire")]
    public static void ShacoDeceiveDoesNotReacquireIdleTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7200, 7000);
            hero.HeroAutoAcquire = true;
            hero.HeroPursuit = new HeroPursuitState(enemy.NetId, enemy.Position);
            hero.AttackMoveGoal = enemy.Position;
            hero.LastAttackStopPoint = enemy.Position;

            match.Cast(0, 0, hero);
            runtime.Tick(.1);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Deceive"),
                "Deceive finishes its fade and installs stealth");
            // W33: Q is an Instacast spell (SpellData flags bit 4), which runs its script at once and does not take the hero's order
            // (BbChampionModule.Execute, the A6 rule of batch 53 that Blitzcrank's W, Annie's E and Vayne's R follow); the hand-written Q
            // cleared the pursuit and the attack-move goal itself. The order stays; what the check pins is that no attack is restored.
            TestAssert.True(hero.HeroPursuit is not null && hero.AttackMoveGoal is not null,
                "the instacast Q keeps the prior order, as every instant spell does");
            TestAssert.Equal<uint?>(null, hero.AttackTarget,
                "idle acquisition does not restore an attack target in stealth");
        }
    }

    /// <summary>The cast answer (0xBD) whose CastSpellInfo names <paramref name="slot"/> with a cooldown; batch 168: the engine's
    /// own cost write reaches the client this way, not as a 0x8A (only a script's <c>BBSetSlotSpellCooldownTime</c> sends that).</summary>
    private static bool HasCastCooldown(IReadOnlyList<PlaintextPacket> packets,
        Entity hero, int slot, double expected) => packets.Any(packet =>
        Opcode(packet) == 0xBD &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
        CastSlotCooldown(packet, slot) is { } cooldown && Math.Abs(cooldown - expected) < 1e-4);

    /// <summary>The cast answer's own cooldown when it names <paramref name="slot"/>, else null. Layout: 0xBD, caster,
    /// sync id, then CastSpellInfo: [u16 pad][hash][netid][rank][asmod][caster][missile][3 positions][3 positions]
    /// [count][targets][designer cast/extra/total/cooldown][flags][slot][mana][launch].</summary>
    private static double? CastSlotCooldown(PlaintextPacket packet, int slot)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 9 + 48) return null;
        var info = span[9..];
        var tail = 48 + 17 * info[47];
        if (info.Length < tail + 22 || info[tail + 21] != slot) return null;
        return BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 12)..]);
    }

    private static bool HasHallucinateCastBit(IReadOnlyList<PlaintextPacket> packets,
        Entity hero, bool busy)
    {
        var bits = packets
        .Where(packet => Opcode(packet) == 0xCC && packet.Plaintext.Length >= 35 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == hero.NetId)
        .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[27..]))
        .ToArray();
        return bits.Length > 0 && ((bits[^1] & 8u) == 0u) == busy;
    }
}
