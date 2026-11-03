using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-yorick-b5-delay-targeting-and-buff-admission")]
    public static void YorickDelayProtectsWithoutInterceptingJavelins()
    {
        using var match = new Match("Yorick", Team.Order, ally: true, enemyChampion: "Nidalee");
        var runtime = match.Runtime;
        var owner = match.Hero(0); var nidalee = match.Hero(1); var ally = match.Hero(2);
        nidalee.Position = new(7000, 7000); ally.Position = new(7300, 7000); owner.Position = new(7600, 7000);
        match.Cast(0, 3, ally);
        runtime.Tick(.3);
        foreach (var type in new[] { BuffType.Suppression, BuffType.Poison, BuffType.Polymorph })
            runtime.SpellBuffs.Add(ally, "BeforeRevival" + type, type, 10, nidalee.NetId, canMitigateDuration: false);
        runtime.Combat.ApplyHit(nidalee.NetId, ally.NetId, 10000, DamageType.True, DamageSourceKind.Spell);
        TestAssert.True(ally.YorickRevival is not null && !ally.Buffs.Any(buff => buff.Name == "YorickRAZombie"));
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "YorickRADelay" && buff.Payload.Untargetable));
        TestAssert.False(ally.Buffs.Any(buff => buff.Name.StartsWith("BeforeRevival", StringComparison.Ordinal)),
            "YOR-03 revival clears suppression, poison and polymorph");
        var rejected = new[] { BuffType.Fear, BuffType.Net, BuffType.Silence, BuffType.Sleep,
            BuffType.Slow, BuffType.Snare, BuffType.Stun, BuffType.Taunt, BuffType.Blind,
            BuffType.Suppression, BuffType.CombatDehancer, BuffType.DamageOverTime };
        foreach (var type in rejected)
        {
            TestAssert.True(runtime.SpellBuffs.Add(ally, "DelayHostile" + type, type, 3,
                nidalee.NetId, canMitigateDuration: false) is null, $"delay rejects hostile {type}");
            TestAssert.True(runtime.Buffs.AddStackingBuff(ally, "DelayStack" + type, type, 3,
                nidalee.NetId, 3, new BuffPayload()) is null, $"stacking also rejects hostile {type}");
        }
        TestAssert.True(runtime.SpellBuffs.Add(ally, "DelayFriendly", BuffType.CombatDehancer, 3,
            owner.NetId, canMitigateDuration: false) is not null, "veto is hostile-only");
        TestAssert.True(runtime.SpellBuffs.Add(ally, "DelayPoison", BuffType.Poison, 3,
            nidalee.NetId, canMitigateDuration: false) is not null, "poison is not in the delay veto list");
        var hp = owner.Health;
        match.Cast(1, 0, owner);
        SpellStep(match, .25); SpellStep(match, .5);
        TestAssert.True(owner.Health < hp, "fresh javelin passes through the delayed body to the rear hero");
        TestAssert.Near(1, ally.Health, 0);
        runtime.Tick(1.01);
        TestAssert.True(ally.YorickRevival is not null && ally.Buffs.Any(buff => buff.Name == "YorickRAZombie"));
        TestAssert.False(ally.Buffs.Any(buff => buff.Name == "DelayPoison"), "zombie activation cleanses again");
        foreach (var type in rejected)
        {
            var buff = runtime.SpellBuffs.Add(ally, "AfterDelay" + type, type, 3,
                nidalee.NetId, canMitigateDuration: false);
            TestAssert.True(buff is not null, $"after delay accepts {type}");
            runtime.Buffs.RemoveBuff(ally, buff!.Slot);
        }
    }

    [Check("spells-live-annie-w-windup-keeps-native-cast")]
    public static void AnnieWWindupKeepsNativeCast()
    {
        // 19 October 2026: Annie runs from her scripts. The hand-written W-only action hold (AnnieWCastState: a client action word of 2, a
        // second stats packet at the impact, an older W unable to unlock a newer one) is gone; W is held like every uncancellable windup by the
        // generic gate of batch 76 (SpellWindup): the cast packet opens the window, an attack or a route that arrives inside it is held and
        // applied once the cast frame has run, and no cast permission is masked.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, autoAcquire: true);
            var annie = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            TestAssert.True(SpellWindup.IsWindingUp(annie), "W's cast packet opens the client's uncancellable windup");
            TestAssert.Equal(uint.MaxValue, match.Runtime.HeroStats.Snapshot(annie).CanCastBits1,
                "W does not mask Q/E/R cast slots");

            match.Clear();
            match.Runtime.Tick(.05);
            match.Move(0, annie.Position + new Position2(500, 0));
            match.Attack(0, enemy);
            match.Runtime.Tick(.10);
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet =>
                    (Opcode(packet) is 0x0D or 0xC1) && packet.Plaintext.Length >= 5 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == annie.NetId),
                    "attack and route packets cannot interrupt the native W windup");
            TestAssert.True(SpellWindup.IsWindingUp(annie), "W is still winding up before its 0.25-second cast frame");

            match.Runtime.Tick(.11);
            TestAssert.False(SpellWindup.IsWindingUp(annie), "the window is over at the cast frame");
            TestAssert.True(enemy.Health < enemy.MaxHealth, "W damage still resolves");
            match.Runtime.Tick(.10);
            TestAssert.True(annie.HeroPursuit is not null,
                "the attack order held inside the windup runs once the cast has completed");
        }
    }

    [Check("spells-live-annie-w-windup-stays-retired-after-revival")]
    public static void AnnieWWindupStaysRetiredAfterRevival()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var annie = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            TestAssert.True(SpellWindup.IsWindingUp(annie), "W opens its windup");
            match.Runtime.Combat.Kill(annie, enemy.NetId);
            match.Runtime.Combat.ScheduleRespawn(annie);
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(annie),
                "Annie can immediately enter a new life");
            TestAssert.False(SpellWindup.IsWindingUp(annie, settled: true),
                "old W cannot hold the orders of the new life (death retires the window)");
            annie.SpellCooldowns[1] = 0.0;
            annie.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7200, 7000);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            TestAssert.True(SpellWindup.IsWindingUp(annie),
                "new-life W gets its own windup");
            match.Runtime.Tick(.26);
            TestAssert.False(SpellWindup.IsWindingUp(annie),
                "new-life W releases normally");
        }
    }

    [Check("spells-live-annie-w-windup-stays-retired-in-yorick-life")]
    public static void AnnieWWindupStaysRetiredInYorickLife()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true,
                allyChampion: "Yorick");
            var annie = match.Hero(0);
            var enemy = match.Hero(1);
            match.Cast(2, 3, annie);
            match.Runtime.Tick(.3);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            match.Runtime.Combat.Kill(annie, enemy.NetId);
            TestAssert.True(annie.Alive && annie.YorickRevival is not null,
                "Yorick gives Annie a temporary new life");
            TestAssert.False(SpellWindup.IsWindingUp(annie, settled: true),
                "old W cannot hold the orders of the temporary life (death retires the window)");
            match.Runtime.Tick(1.1);
            TestAssert.True(annie.YorickRevival is not null && annie.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
                "temporary life completes its initial delay");
            annie.SpellCooldowns[1] = 0.0;
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            TestAssert.True(SpellWindup.IsWindingUp(annie),
                "temporary-life W gets its own windup");
            match.Runtime.Tick(.26);
            TestAssert.False(SpellWindup.IsWindingUp(annie),
                "temporary-life W releases normally");
        }
    }

    [Check("spells-live-annie-w-keeps-existing-spell-permissions")]
    public static void AnnieWKeepsExistingSpellPermissions()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var annie = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            TestAssert.True(match.TryCast(0, 0, enemy),
                "W windup does not newly reject an ordinary Q cast");
            TestAssert.True(match.TryCast(0, 0, enemy, summoner: true),
                "W windup does not newly reject Flash");
        }
    }

    [Check("spells-live-annie-e-during-w-does-not-stop-cone")]
    public static void AnnieEDuringWDoesNotStopCone()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var annie = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastDirected(0, 1, annie.Position, enemy.Position);
            match.Clear();
            match.Cast(0, 2, annie);
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet =>
                    Opcode(packet) == 0xC1 && packet.Plaintext.Length >= 5 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == annie.NetId),
                    "E's harmless shield buff must not stop the ongoing W cone");
            TestAssert.True(SpellWindup.IsWindingUp(annie),
                "E keeps the original W windup live");

            match.Clear();
            TestAssert.True(match.Runtime.Buffs.AddBuff(annie, "Stun", BuffType.Stun,
                1.0, enemy.NetId) is not null, "real stun is admitted");
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(0, match.Runtime.HeroStats.Snapshot(annie).ActionState & 7,
                "real stun still clears all three client action bits");
            foreach (var capture in match.Captures)
                TestAssert.True(capture.Packets.Any(packet =>
                    Opcode(packet) == 0xC1 && packet.Plaintext.Length >= 5 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == annie.NetId),
                    "real stun still sends its stop route");
        }
    }

    [Check("spells-live-annie-cast-retires-explicit-pursuit")]
    public static void AnnieCastRetiresPursuit()
    {
        using var match = new Match("Annie", Team.Order, enemyChampion: "Ezreal");
        var runtime = match.Runtime;
        var annie = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = annie.Position + new Position2(400, 400);
        runtime.Delivery.Flush();
        match.Attack(0, enemy);
        runtime.Tick(.05);
        TestAssert.True(annie.HeroPursuit is not null, "accepted attack order starts pursuit");

        match.Cast(0, 0, enemy);
        TestAssert.True(annie.HeroPursuit is null && annie.AttackMoveGoal is null,
            "Q retires the complete explicit attack order");
        var castPosition = annie.Position;
        enemy.Position = annie.Position + new Position2(650, 650);
        runtime.Tick(.15);
        TestAssert.Equal(castPosition, annie.Position,
            "old pursuit cannot restart during Q windup");
        TestAssert.True(annie.HeroPursuit is null,
            "order scan does not recover the old pursuit");
    }

    [Check("spells-live-control-does-not-cross-yorick-revival")]
    public static void LethalControlDoesNotCrossYorickRevival()
    {
        foreach (var spell in new[] { "OriannaR", "AnnieQ", "AnnieW", "AnnieR", "JannaQ", "JannaW" })
        {
            var champion = spell.StartsWith("Orianna", StringComparison.Ordinal) ? "Orianna" :
                spell.StartsWith("Annie", StringComparison.Ordinal) ? "Annie" : "Janna";
            using var match = new Match(champion, Team.Order, enemyChampion: "Yorick",
                random: new FixedControlRandom(.25));
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var yorick = match.Hero(1);
            match.Cast(1, 3, yorick);
            runtime.Tick(.3);
            TestAssert.True(yorick.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                $"{spell}: Yorick's first life has an armed revival");
            yorick.Health = 1.0;
            if (spell.StartsWith("Annie", StringComparison.Ordinal))
                runtime.Buffs.AddBuff(hero, "Pyromania_particle", BuffType.CombatEnhancer, 25000.0, hero.NetId);

            switch (spell)
            {
                case "OriannaR": match.CastGround(0, hero.Position, 3); break;
                case "AnnieQ": match.Cast(0, 0, yorick); break;
                case "AnnieW": match.CastDirected(0, 1, hero.Position, yorick.Position); break;
                case "AnnieR": match.CastGround(0, yorick.Position, 3); break;
                case "JannaQ":
                    match.CastGround(0, yorick.Position);
                    match.CastGround(0, yorick.Position);
                    break;
                case "JannaW": match.Cast(0, 1, yorick); break;
            }
            runtime.Tick(.85);
            TestAssert.True(yorick.YorickRevival is not null,
                $"{spell}: lethal hit entered Yorick's temporary life");
            TestAssert.True(yorick.ForcedMove is null,
                $"{spell}: first-life hit cannot move the revived body");
            TestAssert.False(yorick.Buffs.Any(buff => buff.Name is "Stun" or "OrianaStun" or "Move" or "Slow"),
                $"{spell}: first-life hit cannot control the revived body");
        }
    }

    [Check("spells-live-janna-q-random-body-clear-landing")]
    public static void JannaQRandomBodyClearLanding()
    {
        foreach (var (roll, expected) in new[]
                 {
                     (0.0, new Position2(1, 0)),
                     (.25, new Position2(0, 1)),
                     (.5, new Position2(-1, 0)),
                 })
        {
            using var match = new Match("Janna", Team.Order, random: new FixedControlRandom(roll));
            var janna = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = janna.Position + new Position2(300, 0);
            var origin = enemy.Position;
            match.CastGround(0, enemy.Position);
            match.CastGround(0, enemy.Position);
            match.Runtime.Tick(.5);
            var move = enemy.ForcedMove ?? throw new InvalidOperationException("Q did not hit");
            var displacement = move.Destination - origin;
            TestAssert.True(displacement.X * expected.X + displacement.Z * expected.Z > 40,
                $"random roll {roll}: knockup follows its sampled direction");
            TestAssert.True(match.Runtime.Navigation.IsMovementWalkable(move.Destination),
                "clear-field knockup endpoint is walkable");
        }

        // The wall case: HowlingGaleSpell's bounce is a point 100 units from the victim (BBGetRandomPointInAreaUnit, radius 100) and the Move
        // buff goes there with IdealDistance 100; the furthest legal point of that ray is the landing. (The hand-written Gale bounced 75
        // units, and its zero-distance witness at (8569, 6389) was a notch whose 75-unit ray held no floor; the ray is longer now.)
        using var wallMatch = new Match("Janna", Team.Order, random: new FixedControlRandom(0));
        var runtime = wallMatch.Runtime;
        var caster = wallMatch.Hero(0);
        var victim = wallMatch.Hero(1);
        var witness = FindJannaQWallWitness(wallMatch);
        var victimOrigin = witness.Origin;
        victim.Position = victimOrigin;
        caster.Position = witness.Caster;
        var clearance = runtime.Movement.MovementRadius(victim) + 4.0;
        var initialVictimHealth = victim.Health;
        runtime.Delivery.Flush(); wallMatch.Clear();
        var targetAim = victimOrigin + new Position2(witness.Direction.X * 500,
            witness.Direction.Z * 500);
        wallMatch.CastGround(0, targetAim);
        wallMatch.CastGround(0, targetAim);
        for (var frame = 0; frame < 100 && victim.ForcedMove is null; frame++) runtime.Tick(.01);
        TestAssert.True(victim.Health < initialVictimHealth &&
            victim.Buffs.Any(buff => buff.Name == "Move"), "Q hits the positive-backoff wall witness");
        var wallMove = victim.ForcedMove ?? throw new InvalidOperationException("Q hit without selected wall move");
        TestAssert.Equal(witness.Landing, wallMove.Destination,
            "Q uses the prechecked body-clear furthest landing");
        TestAssert.True(runtime.Navigation.IsMovementWalkable(wallMove.Destination) &&
            runtime.Navigation.PointClearance(wallMove.Destination, clearance) >= clearance,
            "Q endpoint is walkable and clears the victim's body");
        var dash = Decode(wallMatch.Captures[1].Packets.Single(packet =>
            Opcode(packet) == 0x67 && packet.Channel == 4));
        var waypoints = dash.GetProperty("waypoints");
        var end = waypoints[waypoints.GetArrayLength() - 1];
        TestAssert.Equal(wallMove.Destination,
            new Position2(end[0].GetDouble(), end[1].GetDouble()),
            "victim wire and authoritative Q landings agree");
        for (var frame = 0; frame < 100 && victim.ForcedMove is not null; frame++) runtime.Tick(.01);
        TestAssert.True(victim.ForcedMove is null && runtime.Navigation.IsMovementWalkable(victim.Position),
            "forced movement completes on movement floor");
        // A clipped landing is a short move: the Move buff's half-second stun outlasts it, and a stunned unit drops the orders it is given.
        for (var frame = 0; frame < 100 && victim.Buffs.Any(buff => buff.Name == "Move"); frame++) runtime.Tick(.01);
        TestAssert.False(victim.Buffs.Any(buff => buff.Name == "Move"), "the Move stun ends");
        var landing = victim.Position;
        var recovery = victimOrigin - new Position2(witness.Direction.X * 100,
            witness.Direction.Z * 100);
        var route = runtime.Movement.SetMoveOrder(victim.NetId, recovery);
        TestAssert.True(route.Count > 0, "an ordinary follow-up move gets a route");
        runtime.Tick(.5);
        runtime.Tick(.5);
        TestAssert.True(victim.Position != landing, "victim can move after the knockup");
    }

    private static (Position2 Origin, Position2 Caster, Position2 Direction, Position2 Landing)
        FindJannaQWallWitness(Match match)
    {
        var runtime = match.Runtime;
        var grid = runtime.Navigation;
        var victim = match.Hero(1);
        var caster = match.Hero(0);
        var victimClearance = runtime.Movement.MovementRadius(victim) + 4.0;
        var casterClearance = runtime.Movement.MovementRadius(caster) + 4.0;
        // The wall case fixes the random bounce angle at zero (east),
        // independently of the tornado's firing direction.
        var directions = new[] { new Position2(1, 0) };

        bool TryPoint(Position2 start, Position2 direction,
            out (Position2 Origin, Position2 Caster, Position2 Direction, Position2 Landing) result)
        {
            result = default;
            var casterStart = start - new Position2(direction.X * 200, direction.Z * 200);
            var recovery = start - new Position2(direction.X * 100, direction.Z * 100);
            var rawEnd = start + new Position2(direction.X * 100, direction.Z * 100);
            if (!grid.IsMovementWalkable(start) || !grid.IsMovementWalkable(casterStart) ||
                !grid.IsMovementWalkable(recovery) || grid.IsMovementWalkable(rawEnd) ||
                grid.PointClearance(start, victimClearance) < victimClearance ||
                grid.PointClearance(recovery, victimClearance) < victimClearance ||
                grid.PointClearance(casterStart, casterClearance) < casterClearance)
                return false;
            var safeEnd = runtime.Movement.FurthestWalkableLanding(start, rawEnd, victim);
            if (safeEnd == start || !grid.IsMovementWalkable(safeEnd) ||
                grid.PointClearance(safeEnd, victimClearance) < victimClearance)
                return false;
            result = (start, casterStart, direction, safeEnd);
            return true;
        }

        var anchor = grid.WorldToCell(new Position2(6006.097, 4914.808));
        for (var radius = 0; radius <= 80; radius++)
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
            var cell = new Amberline.Data.NavCell(anchor.Column + dx, anchor.Row + dz);
            if (!grid.IsInBounds(cell)) continue;
            var start = grid.CellToWorld(cell) + new Position2(10, 0);
            foreach (var direction in directions)
                if (TryPoint(start, direction, out var result)) return result;
        }
        for (var row = 100; row < grid.CountY - 100; row += 3)
        for (var column = 100; column < grid.CountX - 100; column += 3)
        {
            var start = grid.CellToWorld(new Amberline.Data.NavCell(column, row));
            foreach (var direction in directions)
                if (TryPoint(start, direction, out var result)) return result;
        }
        throw new InvalidOperationException("Map1 has no body-clear Q wall witness");
    }

    [Check("spells-live-janna-r-collision-wire-landing")]
    public static void JannaRCollisionWireLanding()
    {
        using var match = new Match("Janna", Team.Order);
        var runtime = match.Runtime;
        var janna = match.Hero(0);
        var victim = match.Hero(1);
        victim.Position = new Position2(6006.097, 4914.808);
        janna.Position = victim.Position + new Position2(-200, 0);
        runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, 3, janna);
        var move = AwaitForcedMove(match, victim, "MoveAway") ?? throw new InvalidOperationException("Monsoon did not push victim");
        // DEV-01, 27 Sept 2026 base-game sweep: owner's 28 Sept retail decision
        // supersedes the earlier first-collision wall-blocking assertion.
        TestAssert.False(move.FirstCollision, "Monsoon uses MoveAway's furthest-within-range mode");
        foreach (var capture in match.Captures)
        {
            var dash = Decode(capture.Packets.Single(packet => Opcode(packet) == 0x67 && packet.Channel == 4));
            var waypoints = dash.GetProperty("waypoints");
            var end = waypoints[waypoints.GetArrayLength() - 1];
            TestAssert.Equal(move.Destination,
                new Position2(end[0].GetDouble(), end[1].GetDouble()),
                "Monsoon announces its legal server endpoint");
        }
        runtime.Tick(1.0);
        TestAssert.True(victim.ForcedMove is null && runtime.Navigation.IsMovementWalkable(victim.Position),
            "Monsoon finishes on legal movement floor");
        TestAssert.Equal(move.Destination, victim.Position, "announced and authoritative landings agree");
    }

    [Check("movement-b6-monsoon-crosses-wall-with-matching-wire-landing")]
    public static void MonsoonCrossesWallWithMatchingWireLanding()
    {
        foreach (var gap in new[] { 50.0, 200.0 })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime;
            var janna = match.Hero(0);
            var victim = match.Hero(1);
            // The old (7395,7071) witness is too far from clear floor beyond
            // this wall for Monsoon's 800/900-unit travel budget.
            // Batch 271: on its lattice anchor. The victim's in-range attack stop (a one-point path) seats it on its anchor at once, as
            // the client's install does; at a 50-unit gap that one-unit seat turns the push by about a degree.
            var origin = runtime.Movement.ClientAnchor(new Position2(7583, 7139.5));
            // Independent script expectation: MoveAway ends the unit 1000 from the centre, so it goes 1000 minus its distance, at speed
            // 1200. Select clear floor beyond intervening Map1 terrain without using the production landing resolver as an oracle.
            var travel = 1000.0 - gap;
            var witness = Enumerable.Range(0, 72).Select(index =>
            {
                var angle = index * Math.PI / 36.0;
                var direction = new Position2(Math.Cos(angle), Math.Sin(angle));
                return (Caster: origin - new Position2(direction.X * gap, direction.Z * gap),
                    End: runtime.Movement.ClientAnchor(origin +
                        new Position2(direction.X * travel, direction.Z * travel)));
            }).First(pair => runtime.Navigation.IsMovementWalkable(pair.Caster) &&
                runtime.Navigation.HasMovementLineOfSight(pair.Caster, origin) &&
                runtime.Navigation.HasUnitVision(pair.Caster, janna.SelectionRadius, origin, victim.SelectionRadius) &&
                runtime.Navigation.IsMovementWalkable(pair.End) &&
                runtime.Navigation.PointClearance(pair.End, victim.Radius + 4) >= victim.Radius + 4 &&
                Distance(origin, pair.End) <= travel &&
                Enumerable.Range(1, 79).Any(step => !runtime.Navigation.IsMovementWalkable(
                    origin + new Position2((pair.End.X - origin.X) * step / 80.0,
                        (pair.End.Z - origin.Z) * step / 80.0))));
            victim.Position = origin;
            janna.Position = witness.Caster;
            victim.Target = origin + new Position2(100, 0);
            victim.AttackTarget = janna.NetId;
            runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 3, janna);
            var move = AwaitForcedMove(match, victim, "MoveAway") ?? throw new InvalidOperationException("Monsoon did not push");
            TestAssert.False(move.FirstCollision, "MoveAway uses FURTHEST_WITHIN_RANGE");
            // Batch 279: Monsoon's windup stop seats Janna on her lattice anchor at once, and the push runs away from her there.
            var seat = runtime.Movement.ClientAnchor(witness.Caster);
            var away = Distance(origin, seat);
            var landing = runtime.Movement.ClientAnchor(origin + new Position2((origin.X - seat.X) / away * (1000.0 - away),
                (origin.Z - seat.Z) / away * (1000.0 - away)));
            TestAssert.Equal(landing, move.Destination, "furthest legal endpoint beyond the wall");
            TestAssert.Near(1200, move.Speed, 0);
            // Batch 252 (restated from "length over speed plus the engine's 0.05 s end slack"): a script move has no duration in retail; the
            // dash ends when it reaches its destination (below: completed and succeeded on its end), and its timer is only the safety
            // stop past the trip (batch 241, `scripting.bb-dash-move-has-no-length-based-time-cap`).
            var trip = Distance(origin, landing) / 1200.0;
            TestAssert.True(move.Remaining > trip + 1.0, $"no timer sized to the trip: {move.Remaining} for a {trip} s trip");
            TestAssert.True(victim.Target is null && victim.AttackTarget is null,
                "MoveAway uses CANCEL_ORDER");
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "MoveAway" && buff.Type == BuffType.Stun),
                "terrain crossing preserves the control buff");
            foreach (var capture in match.Captures) AssertDash(capture);

            runtime.Movement.Tick(.1);
            TestAssert.True(victim.Position != origin && !move.Completed, "re-entry occurs mid-push");
            var observer = runtime.Delivery.Observers.Single(view => view.HeroNetId == victim.NetId);
            observer.View.Visible.Remove(victim.NetId);
            match.Clear(); runtime.Delivery.Flush();
            AssertDash(match.Captures[1]);
            match.Clear();
            runtime.Tick(1.0);
            TestAssert.True(move.Completed && move.Succeeded && victim.ForcedMove is null,
                "spell cleanup retires a successful wall-crossing move");
            TestAssert.Equal(landing, victim.Position, "expiry cannot strand the target inside terrain");
            TestAssert.True(runtime.Navigation.IsMovementWalkable(victim.Position));
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "MoveAway"), "control expires normally");

            // Batch 291: the re-entry's dash is the reveal's 0xC2 movement data type 1 since batch 276 (AnnouncesDash).
            void AssertDash(Capture capture) =>
                TestAssert.Equal(landing, AnnouncedDashEnd(capture.Packets, victim.NetId),
                    "initial and re-entry dash use the same authoritative endpoint");
        }
    }

    [Check("spells-live-control-area-untargetable-and-stealth")]
    public static void FreshAreasSkipUntargetableButKeepStealthedHeroes()
    {
        foreach (var spell in new[]
                 {
                     "AnnieW", "AnnieR", "OriannaW", "OriannaR", "OriannaField", "JannaQ", "JannaR",
                 })
        foreach (var untargetable in new[] { true, false })
        {
            var champion = spell.StartsWith("Annie", StringComparison.Ordinal) ? "Annie" :
                spell.StartsWith("Orianna", StringComparison.Ordinal) ? "Orianna" : "Janna";
            using var match = new Match(champion, Team.Order, enemyChampion: "Shaco",
                random: new FixedControlRandom(.25));
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var shaco = match.Hero(1);
            shaco.Position = caster.Position + new Position2(spell == "OriannaField" ? 300 : 200, 0);
            shaco.HealthRegen = 0;
            var health = shaco.Health;
            runtime.SpellBuffs.Add(shaco, untargetable ? "AuditUntargetable" : "AuditStealth",
                untargetable ? BuffType.Internal : BuffType.Invisibility, 2.0, shaco.NetId,
                untargetable ? new BuffPayload { Untargetable = true } : new BuffPayload { Stealthed = true },
                canMitigateDuration: false);
            if (!untargetable) shaco.StealthHidden = true;
            runtime.Delivery.Flush(); match.Clear();

            switch (spell)
            {
                case "AnnieW": match.CastDirected(0, 1, caster.Position, shaco.Position); break;
                case "AnnieR": match.CastGround(0, shaco.Position, 3); break;
                case "OriannaW":
                case "OriannaField": match.CastGround(0, caster.Position, 1); break;
                case "OriannaR": match.CastGround(0, caster.Position, 3); break;
                case "JannaQ":
                    match.CastGround(0, shaco.Position);
                    match.CastGround(0, shaco.Position);
                    break;
                case "JannaR": match.Cast(0, 3, caster); break;
            }
            if (spell == "OriannaField") shaco.Position = caster.Position + new Position2(150, 0);
            runtime.Tick(spell switch
            {
                "JannaR" => .3,
                "OriannaField" => .3,
                "OriannaR" => .85,
                _ => .5,
            });
            var shouldHit = !untargetable;
            if (spell is not ("JannaR" or "OriannaField"))
                TestAssert.Equal(shouldHit, shaco.Health < health,
                    $"{spell}: fresh area eligibility with untargetable={untargetable}");
            if (spell is "OriannaW" or "OriannaField")
                TestAssert.Equal(shouldHit, shaco.Buffs.Any(buff => buff.Name == "OrianaSlow"),
                    $"{spell}: new enemy slow eligibility with untargetable={untargetable}");
            if (spell is "OriannaR" or "JannaQ" or "JannaR")
                TestAssert.Equal(shouldHit, match.Captures[1].Packets.Any(packet =>
                    Opcode(packet) == 0x67 && packet.Channel == 4),
                    $"{spell}: victim-visible displacement with untargetable={untargetable}");
        }
    }

    [Check("spells-live-attached-ignite-ticks-through-hallucinate")]
    public static void AttachedIgniteTicksThroughHallucinate()
    {
        using var match = new Match("Annie", Team.Order, enemyChampion: "Shaco");
        var runtime = match.Runtime;
        var shaco = match.Hero(1);
        shaco.HealthRegen = 0;
        match.Cast(0, 1, shaco, summoner: true);
        TestAssert.True(shaco.Buffs.Any(buff => buff.Name == "SummonerDot"),
            "real Ignite damage-over-time buff is attached before Hallucinate");
        match.Cast(1, 3, shaco);
        // The scripted R's 0.25 s windup runs in the spell system's tick, then the 0.25 s applicator follows.
        runtime.Spells.Tick(.3);
        runtime.Spells.TickLateChampions(.3);
        TestAssert.True(shaco.Buffs.Any(buff => buff.Name == "HallucinateApplicator" &&
            buff.Payload.Untargetable), "real Hallucinate untargetable window is active");
        var health = shaco.Health;
        runtime.Tick(.01);
        TestAssert.True(shaco.Buffs.Any(buff => buff.Name == "HallucinateApplicator" &&
            buff.Payload.Untargetable), "Hallucinate remains active during the periodic tick");
        TestAssert.True(shaco.Health < health,
            "an already attached Ignite tick continues through untargetability");
    }

    private sealed class FixedControlRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
