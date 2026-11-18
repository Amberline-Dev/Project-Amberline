using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 886 (player report on 1.0.0.132, 18 November 2026: "Shaco box cannot hit structures. Shaco clone doesn't have aggro."). Both lines
/// ship the same box ladder and the same Pet.luaobj, so these run on 126 and 132.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static readonly Position2 B886Mid = new(7000, 7000);

    /// <summary>
    /// JackInTheBox.BuffOnUpdateActions (126 and 132): the wake scan (132: 300, 126: 350) names <c>AffectBuildings AffectTurrets</c>, and the
    /// shot ladder's last rung is <c>BBForNClosestVisibleUnitsInTargetArea</c> 400 <c>AffectEnemies AffectBuildings AffectTurrets</c> into
    /// <c>BBSpellCast</c> slot 0 (ShacoBoxSpell, whose SpellData*Flags carry Buildings and Turrets). So a box beside an enemy structure with no
    /// unit near wakes and shoots it. The box sits inside 300 of each structure; its health is raised so the structure's own fire (a turret's,
    /// the guards') cannot decide the outcome, and the inhibitor's and nexus's protection is lifted (the lane is not under test).
    /// </summary>
    [Check("pets-live-b886-shaco-box-shoots-turret-inhibitor-and-nexus")]
    public static void ShacoBoxShootsAnEnemyStructureWithNoUnitNear()
    {
        var failures = new List<string>();
        foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var enemy = match.Hero(1);
            shaco.Position = B886Mid;
            enemy.Position = new Position2(3000, 11500);
            var structure = runtime.World.Entities.Values
                .Where(unit => unit.Alive && unit.Team == Team.Chaos && unit.Kind == kind &&
                               (kind != EntityKind.Turret || !unit.Protected && !TurretRules.IsShrineTurret(unit)))
                .OrderBy(unit => double.Hypot(unit.Position.X - B886Mid.X, unit.Position.Z - B886Mid.Z)).First();
            structure.Protected = false;
            // No summon-placeable point lies inside 300 of the nexus (its footprint), so that box is woken by Annie, who leaves at once: the
            // nexus is then the only enemy in its 400 shot range.
            var seat = B886BoxSeat(runtime, structure, 300.0) ?? B886BoxSeat(runtime, structure, 400.0);
            if (seat is null)
            {
                failures.Add($"{kind}: no summon-placeable point within 400 of {structure.Name}");
                continue;
            }
            var wakeByStructure = BbSpellTargeting.InCircle(structure, seat.Value, 300.0);
            if (!wakeByStructure) enemy.Position = seat.Value + new Position2(0, 120);
            var box = ShacoScriptSupport.SpawnBox(runtime, shaco, seat.Value);
            box.MaxHealth = box.Health = 100000.0;
            var start = structure.Health;
            var lowest = start;
            var aimed = false;
            var woke = false;
            for (var step = 0; step < 150; step++)
            {
                runtime.Tick(1.0 / 30.0);
                if (!woke && ShacoScriptSupport.IsAwake(box) && !wakeByStructure)
                {
                    foreach (var fear in enemy.Buffs.Where(buff => buff.Name == "Fear").ToArray()) runtime.SpellBuffs.Remove(enemy, fear.Slot);
                    enemy.ForcedMove = null;
                    enemy.Waypoints.Clear();
                    enemy.Position = new Position2(3000, 11500);
                }
                woke |= ShacoScriptSupport.IsAwake(box);
                aimed |= runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.SpellName == "ShacoBoxSpell" && projectile.Snapshot.TargetNetId == structure.NetId);
                lowest = Math.Min(lowest, structure.Health);
            }
            if (!woke || !aimed || lowest >= start)
                failures.Add($"{kind} {structure.Name}: woke={woke} (by {(wakeByStructure ? "it" : "Annie")}) shot={aimed} health {start:F0} -> lowest {lowest:F0} " +
                             $"(box at {double.Hypot(box.Position.X - structure.Position.X, box.Position.Z - structure.Position.Z):F0})");
        }
        TestAssert.True(failures.Count == 0, "the box must wake on and shoot an enemy structure: " + string.Join("; ", failures));
    }

    /// <summary>A point the box can stand on (the summon placement leaves it where it is) inside the <paramref name="range"/> circle of the structure.</summary>
    private static Position2? B886BoxSeat(Amberline.Network.Sessions.MatchRuntime runtime, Entity structure, double range)
    {
        var radius = Amberline.Data.GameData.Default.Champion("ShacoBox").GameplayRadius;
        var dx = B886Mid.X - structure.Position.X; var dz = B886Mid.Z - structure.Position.Z;
        var length = double.Hypot(dx, dz);
        for (var distance = 60.0; distance <= range; distance += 10.0)
        {
            var point = new Position2(structure.Position.X + dx / length * distance, structure.Position.Z + dz / length * distance);
            var placed = runtime.Navigation.ClosestSummonPlacement(point, radius);
            if (double.Hypot(placed.X - point.X, placed.Z - point.Z) < 1.0 && BbSpellTargeting.InCircle(structure, placed, range))
                return placed;
        }
        return null;
    }

    /// <summary>
    /// Batch 886 follow-up: a structure-naming script scan refuses a protected structure. With the map's protection ladder engaged
    /// (<c>CombatService.EngageProtection</c>, ProtectionRules: SetInvulnerable paired with not-targetable-to-the-enemy), the Chaos inner mid
    /// turret (its outer stands), the mid inhibitor (its guards stand) and the nexus (every dampener stands) are refused by Jack in the Box's
    /// wake scan and its structure rung, and by YorickSummonDecayed's turret scan (750, <c>AffectEnemies AffectTurrets</c>, which taunts the
    /// ghoul onto the turret it finds). Control: once the outer turret falls, the ladder releases the inner one and the ghoul's next scan
    /// takes it.
    /// </summary>
    [Check("pets-live-b886-structure-scans-refuse-protected-structures")]
    public static void StructureScansRefuseProtectedStructures()
    {
        var failures = new List<string>();
        Entity Inner(Amberline.Network.Sessions.MatchRuntime runtime) => runtime.World.Entities.Values
            .Where(unit => unit.Alive && unit.Team == Team.Chaos && unit.UnitType == UnitType.Turret && unit.Protected &&
                           !TurretRules.IsShrineTurret(unit))
            .OrderBy(unit => double.Hypot(unit.Position.X - B886Mid.X, unit.Position.Z - B886Mid.Z)).First();
        foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var enemy = match.Hero(1);
            shaco.Position = B886Mid;
            enemy.Position = new Position2(3000, 11500);
            runtime.Combat.EngageProtection();
            var structure = kind == EntityKind.Turret ? Inner(runtime) : runtime.World.Entities.Values
                .Where(unit => unit.Alive && unit.Team == Team.Chaos && unit.Kind == kind)
                .OrderBy(unit => double.Hypot(unit.Position.X - B886Mid.X, unit.Position.Z - B886Mid.Z)).First();
            if (!structure.Protected)
            {
                failures.Add($"{kind} {structure.Name}: not protected by the ladder");
                continue;
            }
            var seat = B886BoxSeat(runtime, structure, 300.0) ?? B886BoxSeat(runtime, structure, 400.0);
            if (seat is null)
            {
                failures.Add($"{kind}: no summon-placeable point within 400 of {structure.Name}");
                continue;
            }
            var wakeByStructure = BbSpellTargeting.InCircle(structure, seat.Value, 300.0);
            if (!wakeByStructure) enemy.Position = seat.Value + new Position2(0, 120);
            var box = ShacoScriptSupport.SpawnBox(runtime, shaco, seat.Value);
            box.MaxHealth = box.Health = 100000.0;
            var start = structure.Health;
            var lowest = start;
            var aimed = false;
            var woke = false;
            for (var step = 0; step < 150; step++)
            {
                runtime.Tick(1.0 / 30.0);
                if (!woke && ShacoScriptSupport.IsAwake(box) && !wakeByStructure)
                {
                    foreach (var fear in enemy.Buffs.Where(buff => buff.Name == "Fear").ToArray()) runtime.SpellBuffs.Remove(enemy, fear.Slot);
                    enemy.ForcedMove = null;
                    enemy.Waypoints.Clear();
                    enemy.Position = new Position2(3000, 11500);
                }
                woke |= ShacoScriptSupport.IsAwake(box);
                aimed |= runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.SpellName == "ShacoBoxSpell" && projectile.Snapshot.TargetNetId == structure.NetId);
                lowest = Math.Min(lowest, structure.Health);
            }
            if (wakeByStructure && woke || aimed || lowest < start || !structure.Protected)
                failures.Add($"box beside protected {kind} {structure.Name}: woke={woke} (wake by {(wakeByStructure ? "it" : "Annie")}) " +
                             $"shot={aimed} health {start:F0} -> lowest {lowest:F0} protected={structure.Protected}");
        }

        // YorickSummonDecayed's turret scan, before and after the inner turret is released.
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime; var yorick = match.Hero(0); var enemy = match.Hero(1);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            enemy.Position = new Position2(3000, 11500);
            runtime.Combat.EngageProtection();
            var inner = Inner(runtime);
            var outer = runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team == Team.Chaos && unit.UnitType == UnitType.Turret &&
                    !unit.Protected && !TurretRules.IsShrineTurret(unit))
                .OrderBy(unit => double.Hypot(unit.Position.X - inner.Position.X, unit.Position.Z - inner.Position.Z)).First();
            var away = new Position2(B886Mid.X - inner.Position.X, B886Mid.Z - inner.Position.Z);
            var length = double.Hypot(away.X, away.Z);
            Position2 At(double distance) => new(inner.Position.X + away.X / length * distance, inner.Position.Z + away.Z / length * distance);
            yorick.Position = At(700);
            match.CastGround(0, At(500), 1);
            runtime.Tick(0.4);
            var ghoul = runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.Kind == EntityKind.YorickGhoul);
            if (ghoul is null) failures.Add("Yorick's W made no ghoul");
            else
            {
                ghoul.MaxHealth = ghoul.Health = 100000.0;
                bool TauntedOnto(Entity turret) => ghoul.Buffs.Any(buff => buff.Name == "Taunt" && buff.CasterNetId == turret.NetId);
                var refused = true;
                for (var step = 0; step < 30; step++)
                {
                    runtime.Tick(1.0 / 30.0);
                    refused &= !TauntedOnto(inner);
                }
                runtime.Combat.Kill(outer);
                runtime.Combat.RecomputeProtection();
                var taken = false;
                for (var step = 0; step < 30 && !taken; step++)
                {
                    runtime.Tick(1.0 / 30.0);
                    taken = TauntedOnto(inner);
                }
                if (!refused || !taken)
                    failures.Add($"Yorick ghoul scan: protected {inner.Name} refused={refused}; released (outer {outer.Name} dead, " +
                                 $"protected={inner.Protected}) taken={taken}");
            }
        }
        TestAssert.True(failures.Count == 0, "structure scans must refuse protected structures: " + string.Join("; ", failures));
    }

    /// <summary>
    /// The case table of "the clone has no aggro", both senses, Shaco himself in the clone's seat as the control (batch 886 record). Asserted
    /// rows: a lane minion with only the clone in its acquisition range picks it (Shaco in the seat is picked; before batch 886 the clone was
    /// ignored), a minion the clone attacks picks it, a turret picks it, the idle clone takes an enemy minion in its acquisition range,
    /// Shaco's mirrored attack order sends the clone at a champion beyond that range, and a lane minion picks Tibbers as well (the rule is
    /// every pet's). The other rows are printed for the record.
    /// </summary>
    [Check("pets-live-b886-shaco-clone-aggro-case-table")]
    public static void ShacoCloneAggroCaseTable()
    {
        var rows = new List<string>();
        var failures = new List<string>();
        void Row(string name, string clone, string control, bool? pass)
        {
            rows.Add($"{name}: clone={clone} control={control}{(pass is null ? "" : pass.Value ? " PASS" : " FAIL")}");
            if (pass == false) failures.Add(name);
        }
        string Of(Entity? unit, Entity clone, Entity shaco) => unit is null ? "none" : unit == clone ? "clone" : unit == shaco ? "Shaco" : unit.Name;

        // (b) a Chaos lane minion: the clone idle in its acquisition range, then attacking it, then attacking its allied champion.
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var annie = match.Hero(1);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            annie.Position = new Position2(3000, 11500);
            shaco.Position = new Position2(11500, 3000);
            var minion = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", runtime.Combat.CurrentTime, 0.0);
            minion.Position = B886Mid;
            var seat = B886Mid + new Position2(300, 0);
            var clone = ShacoScriptSupport.SpawnClone(runtime, shaco, seat);
            var idle = runtime.Minions!.SelectTarget(minion);
            clone.AttackTarget = minion.NetId;
            var attacked = runtime.Minions.SelectTarget(minion);
            annie.Position = B886Mid + new Position2(0, 150);
            clone.AttackTarget = annie.NetId;
            var helping = runtime.Minions.SelectTarget(minion);
            clone.AttackTarget = null;
            annie.Position = new Position2(3000, 11500);
            clone.Position = new Position2(11000, 2500);
            shaco.Position = seat;
            var shacoIdle = runtime.Minions.SelectTarget(minion);
            shaco.AttackTarget = minion.NetId;
            var shacoAttacked = runtime.Minions.SelectTarget(minion);
            annie.Position = B886Mid + new Position2(0, 150);
            shaco.AttackTarget = annie.NetId;
            var shacoHelping = runtime.Minions.SelectTarget(minion);
            Row("B1 minion, unit idle in range", Of(idle, clone, shaco), Of(shacoIdle, clone, shaco), idle == clone && shacoIdle == shaco);
            Row("B2 minion, unit attacking it", Of(attacked, clone, shaco), Of(shacoAttacked, clone, shaco), attacked == clone);
            Row("B3 minion, unit attacking its champion", Of(helping, clone, shaco), Of(shacoHelping, clone, shaco), null);
        }

        // (b) the Chaos outer mid turret: the clone in its range, Shaco far; then Shaco in the seat.
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var annie = match.Hero(1);
            annie.Position = new Position2(3000, 11500);
            shaco.Position = new Position2(11500, 3000);
            var turret = runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team == Team.Chaos &&
                    unit.UnitType == UnitType.Turret && !unit.Protected && !TurretRules.IsShrineTurret(unit))
                .OrderBy(unit => double.Hypot(unit.Position.X - B886Mid.X, unit.Position.Z - B886Mid.Z)).First();
            var scale = 500.0 / double.Hypot(B886Mid.X - turret.Position.X, B886Mid.Z - turret.Position.Z);
            var seat = new Position2(turret.Position.X + (B886Mid.X - turret.Position.X) * scale,
                turret.Position.Z + (B886Mid.Z - turret.Position.Z) * scale);
            var clone = ShacoScriptSupport.SpawnClone(runtime, shaco, seat);
            runtime.Delivery.Flush();
            var idle = runtime.Combat.AcquireTurretTarget(turret);
            clone.Position = new Position2(11000, 2500);
            shaco.Position = seat;
            runtime.Delivery.Flush();
            var shacoIdle = runtime.Combat.AcquireTurretTarget(turret);
            Row("B4 turret, unit idle in range", Of(idle, clone, shaco), Of(shacoIdle, clone, shaco), idle == clone && shacoIdle == shaco);
        }

        // (a) the idle clone and an enemy lane minion 300 away; then Shaco's mirrored attack order on a champion 600 away.
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var annie = match.Hero(1);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            annie.Position = new Position2(3000, 11500);
            shaco.Position = B886Mid + new Position2(-150, 0);
            var minion = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", runtime.Combat.CurrentTime, 0.0);
            minion.Position = B886Mid + new Position2(300, 0);
            var clone = ShacoScriptSupport.SpawnClone(runtime, shaco, B886Mid);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
            var idle = clone.AttackTarget is uint picked ? runtime.World.Get(picked) : null;
            Row("A1 idle clone, enemy minion at 300", Of(idle, clone, shaco), "-", idle == minion);
        }
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var annie = match.Hero(1);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            shaco.Position = B886Mid + new Position2(-150, 0);
            annie.Position = B886Mid + new Position2(600, 0);
            var clone = ShacoScriptSupport.SpawnClone(runtime, shaco, B886Mid);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
            var before = clone.AttackTarget;
            var accepted = runtime.Pets.Order(shaco, clone.NetId, 3, annie.Position, annie.NetId);
            runtime.Pets.Tick(runtime.Combat.CurrentTime + .2, .2);
            var after = clone.AttackTarget is uint picked ? runtime.World.Get(picked) : null;
            Row("A3 mirrored ORDER_ATTACKTO, champion at 600", $"{Of(after, clone, shaco)} (before {before?.ToString() ?? "none"}, accepted {accepted})", "-",
                accepted && after == annie);
        }

        // (b) the Tibbers row: is the minion rule clone-specific or every pet's? An Order minion beside Annie's Tibbers.
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var shaco = match.Hero(0); var annie = match.Hero(1);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            shaco.Position = new Position2(11500, 3000);
            annie.Position = B886Mid;
            match.CastGround(1, B886Mid + new Position2(300, 0), 3);
            runtime.Tick(0.5);
            var tibbers = runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.OwnerNetId == annie.NetId && unit.PetState is not null);
            annie.Position = new Position2(3000, 11500);
            var minion = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", runtime.Combat.CurrentTime, 0.0);
            minion.Position = (tibbers?.Position ?? B886Mid) + new Position2(-300, 0);
            if (tibbers is not null) tibbers.AttackTarget = null;
            var picked = runtime.Minions!.SelectTarget(minion);
            Row("B1t minion, Tibbers idle in range", tibbers is null ? "no Tibbers" : picked == tibbers ? "Tibbers" : picked?.Name ?? "none", "-",
                tibbers is not null && picked == tibbers);
        }

        var table = string.Join(Environment.NewLine, rows);
        Console.WriteLine("b886 clone case table:" + Environment.NewLine + table);
        TestAssert.True(failures.Count == 0, "clone aggro rows failed: " + string.Join(", ", failures) + Environment.NewLine + table);
    }
}
