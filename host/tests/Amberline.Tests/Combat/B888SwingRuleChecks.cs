using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Combat;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 888 (owner decision, 18 November 2026: "check at start, cut on blink"): a basic attack's reach is tested when the swing starts;
/// a target that walks away during the windup is hit at the cast frame, and only a target that jumps beyond reach + 100 in one step (a
/// blink, a teleport, a dash or another displacement) cuts it (<c>CombatService.TargetJumpedOutOfWindup</c>).
/// docs/maintenance/2026-11-18-batch-888-swing-rule.md.
/// </summary>
public static class B888SwingRuleChecks
{
    // The melee champion swings at a champion at the edge of its reach, which walks straight away as the swing starts (hasted by half, so it
    // crosses reach + 100 inside any melee windup). The swing lands at its cast frame.
    [Check("combat.b888-melee-hero-swing-on-a-champion-walking-away-from-the-reach-edge-lands")]
    public static void MeleeHeroSwingOnAWalkingChampionLands()
    {
        using var match = new Match("Garen", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        Haste(match, match.Foe);
        match.Foe.Position = match.Hero.Position + new Position2(150, 0);
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
        var swing = Watch(match, match.Hero, match.Foe, WalkAway(match, match.Foe));
        TestAssert.True(swing.CrossedInWindup, "fixture: the champion crosses reach + 100 inside the windup: " + swing.Story);
        TestAssert.True(swing.Landed is not null && swing.Cut is null, "the swing started in reach lands on the walking champion: " + swing.Story);
    }

    // The same swing, with the champion blinking 400 units away (a Flash: one step) or dashing 450 at 1500 a second two steps into the
    // windup: the swing is cut and nothing lands.
    [Check("combat.b888-swing-on-a-target-that-blinks-or-dashes-away-mid-windup-is-cut")]
    public static void SwingOnABlinkingOrDashingTargetIsCut()
    {
        var failures = new List<string>();
        Escape("blink 400", (match, foe, direction) =>
        {
            foe.Waypoints.Clear();
            foe.Position = foe.Position + Scaled(direction, 400);
        });
        Escape("dash 450 at 1500", (match, foe, direction) =>
        {
            if (!match.Runtime.Movement.StartForcedMove(foe.NetId, foe.Position + Scaled(direction, 450), 1500.0, 0.3, foe.NetId, "B888Dash"))
                failures.Add("dash: fixture: the forced move did not start");
        });
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Escape(string name, Action<Match, Entity, Position2> escape)
        {
            using var match = new Match("Garen", foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            match.Foe.Position = match.Hero.Position + new Position2(150, 0);
            match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
            var swing = Watch(match, match.Hero, match.Foe, (step, direction) =>
            {
                if (step == 2) escape(match, match.Foe, direction);
            });
            if (swing.Landed is not null || swing.Cut is null) failures.Add($"{name}: the swing was not cut: {swing.Story}");
        }
    }

    // A lane minion's and a camp monster's swing on a champion that walks straight away from the edge of their reach: their scripts turn the
    // attack off (STOPREASON_MOVING) once the champion leaves TargetInCancelAttackRange, and the swing still lands. The champion pulls the
    // camp with a basic attack, whose 400-unit reveal (ca_RevealAttackerTimeOut 4.5 s) keeps it in the camp's sight on its short walk: a
    // target that steps out of the monster's sight still cuts the swing (the first green run's camp case, cut 2 steps after the crossing).
    [Check("combat.b888-minion-and-monster-swings-on-a-walking-champion-land")]
    public static void MinionAndMonsterSwingsOnAWalkingChampionLand()
    {
        var failures = new List<string>();
        Swing("lane minion", match =>
        {
            match.Ally.Position = new Position2(12000, 11000);
            return LaneMinion(match, match.Hero.Position + new Position2(150, 0));
        });
        Swing("camp monster", match =>
        {
            var monster = CampMonster(match);
            match.Hero.AttackCooldown = 0.0;
            if (!match.Runtime.Combat.QueueBasicAttack(match.Hero, monster)) failures.Add("camp monster: fixture: the champion's pull did not start");
            return monster;
        });
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Swing(string name, Func<Match, Entity> place)
        {
            using var match = new Match("Garen", foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            Haste(match, match.Hero);
            var attacker = place(match);
            var swing = Watch(match, attacker, match.Hero, WalkAway(match, match.Hero));
            if (!swing.Started) failures.Add($"{name}: fixture: no swing on the champion: {swing.Story}");
            else if (!swing.CrossedInWindup) failures.Add($"{name}: fixture: the champion did not cross reach + 100 in the windup: {swing.Story}");
            else if (swing.Landed is null || swing.Cut is not null)
                failures.Add($"{name}: the swing on the walking champion did not land: {swing.Story}; state {attacker.MinionState}/{attacker.NeutralState}");
        }
    }

    // The rule's signals, on the bare combat service (positions stepped by hand, 30 Hz, a 0.5 s melee windup from the reach edge): a
    // walk of 15 a step crosses reach + 100 at step 7 and is hit; so is a walk after a dash that has finished (its completed forced move
    // can stay attached); a running dash of 50 a step is cut, and so is a swing whose attacker a running displacement carries away.
    [Check("combat.b888-windup-cut-reads-live-displacements-only")]
    public static void WindupCutReadsLiveDisplacementsOnly()
    {
        var failures = new List<string>();
        Case("walk", targetStep: 15, attackerStep: 0, targetDash: null, attackerKnocked: false, lands: true);
        Case("walk after a finished dash", targetStep: 15, attackerStep: 0, targetDash: true, attackerKnocked: false, lands: true);
        Case("dash still running", targetStep: 50, attackerStep: 0, targetDash: false, attackerKnocked: false, lands: false);
        Case("attacker knocked back", targetStep: 0, attackerStep: 50, targetDash: null, attackerKnocked: true, lands: false);
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Case(string name, double targetStep, double attackerStep, bool? targetDash, bool attackerKnocked, bool lands)
        {
            var world = new WorldState();
            var source = world.Register(new Entity(1, EntityKind.Minion, UnitType.Minion, Team.Order, new(7000, 7000))
            {
                AttackDamage = 100.0, AttackRange = 100.0, AttackPeriod = 1.0, AttackWindup = 0.5, Health = 100.0, MaxHealth = 100.0,
            });
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7000, 7000))
            {
                Health = 1000.0, MaxHealth = 1000.0,
            });
            target.Position = new Position2(7000 + CombatService.AttackReach(source, target) - 1.0, 7000);
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
                new AnnieChecks.FixedRandomSource(0.5));
            if (!combat.QueueBasicAttack(source, target, new AttackRolls(1.0, 1.0, 1.0)))
            {
                failures.Add($"{name}: fixture: the swing did not start");
                return;
            }
            if (targetDash is { } finished)
                target.ForcedMove = new ForcedMoveState(new Position2(9000, 7000), 1500.0, 1.0, target.NetId, "B888Dash") { Completed = finished };
            if (attackerKnocked) source.ForcedMove = new ForcedMoveState(new Position2(5000, 7000), 1500.0, 1.0, target.NetId, "B888Knockback");
            for (var step = 0; step < 14; step++)
            {
                target.Position = new Position2(target.Position.X + targetStep, 7000);
                source.Position = new Position2(source.Position.X - attackerStep, 7000);
                combat.CombatTick(Step, regenerate: false);
            }
            var kept = combat.PendingHits.Any(hit => hit.SourceNetId == source.NetId);
            if (kept != lands) failures.Add($"{name}: the swing was {(kept ? "kept" : "cut")} 0.47 s into its windup");
            for (var step = 0; step < 6; step++) combat.CombatTick(Step, regenerate: false);
            if ((target.Health < 1000.0) != lands) failures.Add($"{name}: health {R(target.Health)} after the cast frame");
        }
    }

    internal sealed record SwingWatch(bool Started, bool CrossedInWindup, double? Landed, double? Cut, string Story);

    /// <summary>
    /// Waits (up to 20 s) for <paramref name="attacker"/>'s basic attack on <paramref name="target"/> to start, puts the target at the edge of
    /// the reach on the line from the attacker, then steps the windup, calling <paramref name="act"/> before each step (step 0 at once) with
    /// the direction away from the attacker. Reports when the target crossed reach + 100 (in the windup: by the end of the step the cast
    /// frame falls in, whose combat tick still sees the windup running), when the swing landed (the target's health fell) and when it was
    /// cut (it left the queue without landing).
    /// </summary>
    internal static SwingWatch Watch(Match match, Entity attacker, Entity target, Action<int, Position2> act)
    {
        var combat = match.Runtime.Combat;
        PendingHit hit = default;
        for (var step = 0; step < 600 && hit.MissileNetId == 0; step++)
        {
            match.Advance(Step);
            hit = combat.PendingHits.FirstOrDefault(item => item.SourceNetId == attacker.NetId && item.TargetNetId == target.NetId &&
                item.SourceKind == DamageSourceKind.Basic && item.MissileNetId != 0 && item.WindupRemaining > 0.0);
        }
        if (hit.MissileNetId == 0) return new SwingWatch(false, false, null, null, $"no swing of {attacker.Name} on {target.Name} in 20 s");
        var start = match.Now;
        var castFrame = start + hit.WindupRemaining;
        var reach = CombatService.AttackReach(attacker, target);
        var dx = target.Position.X - attacker.Position.X;
        var dz = target.Position.Z - attacker.Position.Z;
        var length = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        var direction = new Position2(dx / length, dz / length);
        target.Waypoints.Clear();
        target.Target = null;
        target.Position = attacker.Position + Scaled(direction, reach - 1.0);
        double? crossed = null, landed = null, cut = null;
        for (var step = 0; step < 60 && landed is null && cut is null; step++)
        {
            act(step, direction);
            var health = target.Health;
            match.Advance(Step);
            var gap = Math.Sqrt(Square(target.Position.X - attacker.Position.X) + Square(target.Position.Z - attacker.Position.Z));
            if (crossed is null && gap > reach + 100.0) crossed = match.Now;
            if (target.Health < health - 1e-6) landed = match.Now;
            else if (!combat.PendingHits.Any(item => item.MissileNetId == hit.MissileNetId)) cut = match.Now;
        }
        var story = $"{attacker.Name} on {target.Name}: swing at {R(start)}, cast frame {R(castFrame)}, reach {R(reach)}, " +
            $"target speed {R(target.Speed)}, crossed reach + 100 at {R(crossed ?? -1)}, landed {R(landed ?? -1)}, cut {R(cut ?? -1)}";
        return new SwingWatch(true, crossed is not null && crossed < castFrame + Step - 1e-9, landed, cut, story);
    }

    /// <summary>A walk order straight away from the attacker, 1500 units, given at once.</summary>
    internal static Action<int, Position2> WalkAway(Match match, Entity hero) => (step, direction) =>
    {
        if (step == 0) match.Runtime.Spells.HeroOrders!.StartMove(hero, hero.Position + Scaled(direction, 1500.0));
    };

    private static void Haste(Match match, Entity unit) =>
        match.Runtime.Buffs.AddBuff(unit, "B888Haste", BuffType.CombatEnhancer, 1e6, unit.NetId, new BuffPayload { MoveSpeedPercent = 0.5 });

    private static Position2 Scaled(Position2 direction, double length) => new(direction.X * length, direction.Z * length);

    private static double Square(double value) => value * value;

    /// <summary>The first camp member of the production map once the camps have spawned, with the champion 200 west of it.</summary>
    private static Entity CampMonster(Match match)
    {
        for (var tick = 0; tick < 800 && !match.Runtime.World.Entities.Values.Any(IsCampMember); tick++) match.Runtime.Tick(0.25);
        var monster = match.Runtime.World.Entities.Values.Where(IsCampMember).OrderBy(unit => unit.CampId).First();
        match.Hero.Position = monster.Position + new Position2(-200, 0);
        match.Hero.Waypoints.Clear();
        return monster;
    }

    private static bool IsCampMember(Entity unit) =>
        unit.Kind == EntityKind.Neutral && unit.Alive && unit.NeutralMember == 1 && unit.CampId is >= 1 and <= 12;
}
