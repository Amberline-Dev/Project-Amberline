using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (26 October 2026, batch 275): Poppy plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever
/// existed). Expected numbers are the literals of the shipped scripts and inibins; the tooltips of <c>fontconfig_en_US.txt</c> are the second witness
/// (Valiant Fighter, Paragon, Diplomatic Immunity). The scenario <c>S-Poppy</c> shows the same facts, and more, on a headless match.
/// </summary>
public static class BbPoppyChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));

    private static string Dump(List<(double At, double Dealt, double Raw, string Kind)> hits) =>
        string.Join(" ", hits.Select(hit => $"[{hit.At:F2} dealt={hit.Dealt:F1} raw={hit.Raw:F1} {hit.Kind}]"));

    /// <summary>A match with Poppy at <paramref name="rank"/> on every spell (her Paragon manager is learnt through the spellbook, as a player does).</summary>
    private static Match Arena(int rank = 1, string foe = "Orianna", double gap = 300.0)
    {
        var match = new Match("Poppy", foeChampion: foe);
        match.Hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 1), "the first Paragon point learnt (its level-one hook adds the manager)");
        match.Learn(rank);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7000.0 + gap, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        return match;
    }

    private static void Swing(Match match, Entity target, double seconds = 1.2)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target), "swing");
        match.Advance(seconds);
    }

    /// <summary>Whether every sample of a straight walk from <paramref name="from"/> to <paramref name="to"/> lies on walkable ground.</summary>
    private static bool Clear(NavigationGrid grid, Position2 from, Position2 to)
    {
        var length = Distance(from, to);
        for (var step = 0.0; step <= length; step += 10.0)
        {
            var t = step / length;
            if (!grid.IsWalkable(new Position2(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t))) return false;
        }
        return true;
    }

    /// <summary>
    /// A spot on Map1 where Poppy's charge of 300 units ends at an enemy with a wall 100 to 300 units beyond it, so the push stops short (the
    /// Heroic Charge wall case). The grid is the production map's own navigation grid.
    /// </summary>
    private static (Position2 Hero, Position2 Foe, Position2 Dir)? WallAhead(NavigationGrid grid, IReadOnlyList<Position2> turrets)
    {
        Position2[] directions = [new Position2(1, 0), new Position2(-1, 0), new Position2(0, 1), new Position2(0, -1)];
        for (var x = 4000.0; x <= 11000.0; x += 25.0)
        for (var z = 4000.0; z <= 11000.0; z += 25.0)
        foreach (var direction in directions)
        {
            var foe = new Position2(x, z);
            var hero = new Position2(x - 300.0 * direction.X, z - 300.0 * direction.Z);
            if (!grid.IsWalkable(foe) || !Clear(grid, hero, foe)) continue;
            // Out of turret range: a tower's shots would hide the push's own damage.
            if (turrets.Any(turret => Distance(turret, foe) < 1500.0 || Distance(turret, hero) < 1500.0)) continue;
            double blocked = -1.0;
            for (var distance = 10.0; distance <= 300.0; distance += 10.0)
            {
                if (grid.IsWalkable(new Position2(foe.X + distance * direction.X, foe.Z + distance * direction.Z))) continue;
                blocked = distance;
                break;
            }
            if (blocked >= 100.0 && blocked <= 300.0) return (hero, foe, direction);
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Poppy");
        foreach (var name in new[] { "CharScriptPoppy", "PoppyValiantFighter", "PoppyDevastatingBlow", "PoppyParagonOfDemacia", "PoppyParagonManager",
                     "PoppyParagonStats", "PoppyParagonSpeed", "PoppyHeroicCharge", "PoppyHeroicChargePart2", "PoppyDiplomaticImmunity", "PoppyDITarget",
                     "PoppyDITargetDmg" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Poppy");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(24, closure.ScriptNames.Count(), "the closure is 24 scripts (bb-coverage): her spells, passive, buffs and the system buffs");

        var hero = new Entity { ChampionName = "Poppy" };
        TestAssert.Equal(new[] { "PoppyDevastatingBlow", "PoppyParagonOfDemacia", "PoppyHeroicCharge", "PoppyDiplomaticImmunity" },
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Poppy.inibin");
        TestAssert.Equal(new[] { 5, 5, 5, 3 }, Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");
        TestAssert.Equal(new[] { 1, 3, 5, 7, 9 }, Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal(new[] { 6, 11, 16 }, Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Poppy")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 450, Id: 78, RetailBot: false }, "playable as a top laner for 450 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Poppy"), "listed once");

        // The tables the figures rest on, a second time as literals.
        TestAssert.Equal([8.0, 7.0, 6.0, 5.0, 4.0], ByLevel("PoppyDevastatingBlow", "SelfExecute", "SpellCooldown"), "Devastating Blow's cooldown by rank");
        TestAssert.Equal([20.0, 40.0, 60.0, 80.0, 100.0], ByLevel("PoppyDevastatingBlow", "SelfExecute", "BonusDamage"), "Devastating Blow's bonus by rank");
        TestAssert.Equal([75.0, 150.0, 225.0, 300.0, 375.0], ByLevel("PoppyDevastatingBlow", "BuffOnHitUnit", "MaxDamage"), "its cap by rank");
        TestAssert.Equal([1.5, 2.0, 2.5, 3.0, 3.5], ByLevel("PoppyParagonStats", "BuffOnUpdateStats", "ArmorDamageValue"), "a Paragon stack by rank");
        TestAssert.Equal([50.0, 75.0, 100.0, 125.0, 150.0], ByLevel("PoppyHeroicCharge", "TargetExecute", "Damage"), "Heroic Charge's damage by rank");
        TestAssert.Equal([75.0, 125.0, 175.0, 225.0, 275.0], ByLevel("PoppyHeroicCharge", "TargetExecute", "DamageTwo"), "the wall damage by rank");
        TestAssert.Equal([6.0, 7.0, 8.0], Numbers(Blocks(Script("PoppyDiplomaticImmunity"), "TargetExecute").First(block =>
            block.Function == "BBSpellBuffAdd" && block.Text("BuffName") == "PoppyDITarget").Param("DurationByLevel")!), "Diplomatic Immunity's duration by rank");
        TestAssert.Equal([1.2, 1.3, 1.4], Numbers(Blocks(Script("PoppyDITargetDmg"), "BuffOnPreDealDamage").First(block =>
            block.Function == "BBSetVarInTable").Param("SrcValueByLevel")!), "the bonus damage to the target by rank");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Valiant Fighter
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-valiant-fighter-halves-the-damage-above-a-tenth-of-her-health")]
    public static void ValiantFighterHalvesTheExcess()
    {
        using var match = Arena();
        var hero = match.Hero;
        hero.MaxHealth = 500.0;
        hero.Health = 500.0;
        TestAssert.True(Of(hero, "PoppyValiantFighter") is not null, "the passive is on her from the start (CharOnActivate)");
        // A magic hit of 300 at 500 health: the share is 50, the excess 250 halves to 125, so 175 arrives, then the resistance of 30.
        match.Runtime.Combat.QueueScriptedHit(match.Foe, hero, 300.0, DamageType.Magic, DamageSourceKind.Spell, 0);
        match.Advance(0.2);
        var hits = HitsOn(match, "Poppy");
        TestAssert.Equal(1, hits.Count, "one hit lands on Poppy " + Dump(hits));
        // BuffOnPreDamage runs after the resistance (the damage that reaches the health bar): 300 magic is 230.8 after the resistance of 30, and the
        // excess over 10 percent of her health (50 at 500) is halved: 50 + (230.8 - 50) / 2 = 140.4.
        TestAssert.Near(50.0 + (300.0 * 100.0 / 130.0 - 50.0) / 2.0, hits[0].Dealt, 0.5, "the excess over 10 percent of her health is halved, after the resistance " + Dump(hits));

        // The reduction is for damage that is not true damage: a true hit of 300 arrives whole.
        match.Runtime.Combat.QueueScriptedHit(match.Foe, hero, 300.0, DamageType.True, DamageSourceKind.Spell, 0);
        match.Advance(0.2);
        var truth = HitsOn(match, "Poppy").Last();
        TestAssert.Near(300.0, truth.Dealt, 1.0, "true damage is not reduced");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Devastating Blow
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-devastating-blow-hits-once-for-eight-percent-of-the-target-max-health")]
    public static void DevastatingBlowHitsOnce()
    {
        using var match = Arena(1, "Orianna", 100.0);
        var hero = match.Hero;
        TestAssert.True(match.Cast(0), "Q casts");
        TestAssert.True(Of(hero, "PoppyDevastatingBlow") is not null, "the next attack is armed");
        TestAssert.True(hero.SpellSeals.IsSealed(false, 0), "the slot is sealed while the strike waits");
        TestAssert.False(match.CanCast(0).Allowed, "it cannot be pressed again");
        var attack = hero.AttackDamage;
        Swing(match, match.Foe, 0.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 1, "the armed attack lands " + Dump(hits));
        // Rank one: 8 percent of the target's maximum health (5000 here, so 400) plus 20, capped at 75, plus her attack damage, magic, then the
        // resistance of 30. The plain swing that follows (her attack speed brings the next one within a second) is an ordinary hit.
        TestAssert.Near((75.0 + attack) * 100.0 / 130.0, hits[0].Dealt, 0.5, "the cap of 75 plus her attack damage, after the resistance " + Dump(hits));
        TestAssert.True(Of(hero, "PoppyDevastatingBlow") is null, "the buff is spent by the strike");
        TestAssert.False(hero.SpellSeals.IsSealed(false, 0), "the slot opens");
        TestAssert.Near(8.0 - (match.Now - hits[0].At), hero.SpellCooldowns[0], 0.4, "its cooldown (8 s at rank one) runs from the strike");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-devastating-blow-caps-at-375-and-expires-after-ten-seconds")]
    public static void DevastatingBlowCapsAtRankFiveAndExpires()
    {
        using var match = Arena(5, "Orianna", 100.0);
        var hero = match.Hero;
        TestAssert.True(match.Cast(0), "Q casts at rank five");
        var attack = hero.AttackDamage;
        Swing(match, match.Foe, 0.5);
        var hit = HitsOn(match, "Foe")[0];
        // Rank five: 8 percent (400) plus 100 is over the cap of 375, plus her attack damage, after the resistance of 30.
        TestAssert.Near((375.0 + attack) * 100.0 / 130.0, hit.Dealt, 0.5, "the cap at rank five is 375");
        TestAssert.Near(4.0 - (match.Now - hit.At), hero.SpellCooldowns[0], 0.4, "its cooldown at rank five is 4 s");

        // Unused, the buff runs out after ten seconds and the cooldown starts there (the deactivation sets it).
        using var idle = Arena(1);
        TestAssert.True(idle.Cast(0), "Q casts");
        idle.Advance(10.3);
        TestAssert.True(Of(idle.Hero, "PoppyDevastatingBlow") is null, "ten seconds with no attack and the buff is gone");
        TestAssert.Near(8.0, idle.Hero.SpellCooldowns[0], 0.5, "the cooldown of 8 s starts when it expires");
        TestAssert.Equal(0, idle.Module.ScriptFailures.Count, "no script failed: " + Failures(idle));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Paragon of Demacia
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-paragon-gives-ten-stacks-of-armour-and-damage-that-end-after-five-seconds")]
    public static void ParagonStacksTenTimesAndEnds()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var armor = hero.Armor;
        var damage = hero.AttackDamage;
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.5);
        TestAssert.Equal(10, Of(hero, "PoppyParagonStats")?.Stacks ?? 0, "ten stacks at once");
        // Rank one: 1.5 armour and 1.5 attack damage a stack; the tooltip reads "increased armor and damage".
        TestAssert.Near(15.0, hero.Armor - armor, 0.05, "fifteen armour");
        TestAssert.Near(15.0, hero.AttackDamage - damage, 0.05, "fifteen attack damage");
        TestAssert.True(Of(hero, "PoppyParagonSpeed") is not null, "the speed buff of the cast");
        TestAssert.True(Of(hero, "PoppyParagonManager") is not null, "the manager stays (learnt at her first Paragon point)");
        match.Advance(5.5);
        TestAssert.True(Of(hero, "PoppyParagonStats") is null, "the stacks end five seconds after the last renewal");
        TestAssert.Near(armor, hero.Armor, 0.05, "and the armour goes back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-paragon-gains-a-stack-for-each-hit-she-makes-and-takes")]
    public static void ParagonStacksOnHitsAndOnBeingHit()
    {
        using var match = Arena(1, "Orianna", 100.0);
        var hero = match.Hero;
        Swing(match, match.Foe, 1.2);
        TestAssert.Equal(1, Of(hero, "PoppyParagonStats")?.Stacks ?? 0, "one stack for her own hit (BuffOnHitUnit)");
        match.Foe.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, hero), "the enemy swings at her");
        match.Advance(1.2);
        TestAssert.Equal(2, Of(hero, "PoppyParagonStats")?.Stacks ?? 0, "a second stack for the enemy's hit (BuffOnBeingHit)");
        for (var swing = 0; swing < 8; swing++)
        {
            Swing(match, match.Foe, 1.2);
        }
        TestAssert.Equal(10, Of(hero, "PoppyParagonStats")?.Stacks ?? 0, "ten at most");
        TestAssert.True(Of(hero, "PoppyParagonParticle") is not null, "the full-stack particle comes on at ten");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-paragon-speed-is-seventeen-percent-for-five-seconds-at-rank-one")]
    public static void ParagonSpeedIsAPercentForFiveSeconds()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var before = hero.Speed;
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.3);
        TestAssert.Near(before * 1.17, hero.Speed, before * 0.005, "seventeen percent faster at rank one");
        match.Advance(5.0);
        TestAssert.Near(before, hero.Speed, 0.5, "and the speed is back after five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Heroic Charge
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-heroic-charge-hits-the-target-and-pushes-it-four-hundred")]
    public static void HeroicChargeHitsAndPushes()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        var start = foe.Position;
        TestAssert.True(match.Cast(2, foe), "E casts on the enemy");
        match.Advance(0.3);
        TestAssert.False(match.CanCast(0).Allowed, "she cannot cast while she is charging");
        match.Advance(1.2);
        var hits = HitsOn(match, "Foe");
        // The charge's own hit comes first; her attack on the target after the charge (AI_ATTACKTO of the charge's else branch) follows.
        TestAssert.True(hits.Count >= 1, "the charge hits " + Dump(hits));
        // Rank one: 50 magic, no ability power in this rig, then the resistance of 30.
        TestAssert.Near(50.0 * 100.0 / 130.0, hits[0].Dealt, 0.5, "50 magic damage after the resistance");
        TestAssert.True(Of(foe, "PoppyHeroicChargePart2") is null, "the push ends with its move: a clean push stuns nothing");
        // The push carries the target four hundred units beyond the point Poppy reached, in her facing.
        TestAssert.Near(400.0, Distance(foe.Position, start), 25.0, "pushed about four hundred units");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-heroic-charge-stuns-a-target-that-hits-a-wall")]
    public static void HeroicChargeStunsAtAWall()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        var spot = WallAhead(match.Runtime.Navigation, match.Runtime.World.Entities.Values.Where(entity => entity.UnitType == UnitType.Turret).Select(entity => entity.Position).ToArray()) ?? throw new InvalidOperationException("no wall 100 to 300 units beyond a clear charge on Map1");
        hero.Position = spot.Hero;
        hero.Facing = spot.Dir;
        foe.Position = spot.Foe;
        TestAssert.True(match.Cast(2, foe), "E casts on the enemy at the wall");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 2, "the charge, then the wall " + Dump(hits));
        // The wall hit is DamageTwo (75 at rank one) with the 0.4 ratio, here no ability power, after the resistance.
        TestAssert.Near(75.0 * 100.0 / 130.0, hits[1].Dealt, 0.5, "75 magic damage on the wall " + Dump(hits));
        TestAssert.True(foe.Buffs.Any(buff => buff.Type == BuffType.Stun), "stunned by the wall, for a second and a half");
        match.Advance(2.0);
        TestAssert.False(foe.Buffs.Any(buff => buff.Type == BuffType.Stun), "and the stun is over");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Diplomatic Immunity
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-poppy-diplomatic-immunity-ignores-every-enemy-but-its-target")]
    public static void ImmunityIgnoresEveryoneButItsTarget()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        TestAssert.True(match.Cast(3, match.Foe), "R casts on the enemy");
        match.Advance(0.3);
        TestAssert.True(Of(match.Foe, "PoppyDITarget") is not null, "the target carries the mark");
        TestAssert.True(Of(hero, "PoppyDiplomaticImmunity") is not null, "Poppy carries the immunity");

        hero.Health = 1000.0;
        match.Runtime.Combat.QueueScriptedHit(match.FarFoe, hero, 200.0, DamageType.Magic, DamageSourceKind.Spell, 0);
        match.Advance(0.3);
        TestAssert.Near(1000.0, hero.Health, 1.0, "another enemy's hit is refused");
        match.Runtime.Combat.QueueScriptedHit(match.Foe, hero, 200.0, DamageType.Magic, DamageSourceKind.Spell, 0);
        match.Advance(0.3);
        TestAssert.True(hero.Health < 1000.0 - 50.0, "the target's hit lands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-diplomatic-immunity-refuses-enemy-buffs-but-not-its-target")]
    public static void ImmunityRefusesOtherEnemiesBuffs()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        TestAssert.True(match.Cast(3, match.Foe), "R casts on the enemy");
        match.Advance(0.3);
        var stun = match.Runtime.Spells.UnitScripts!.Buffs!.Add(new BbBuffRequest(hero, match.FarFoe, "PoppyProbeStun",
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BuffType.Stun, 1, 2.0, 0.0, false, new BbTable()));
        TestAssert.True(stun is null, "a stun from the other enemy is not added");
        var fromTarget = match.Runtime.Spells.UnitScripts!.Buffs!.Add(new BbBuffRequest(hero, match.Foe, "PoppyProbeStun",
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BuffType.Stun, 1, 2.0, 0.0, false, new BbTable()));
        TestAssert.True(fromTarget is not null, "the same stun from the target is");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-diplomatic-immunity-adds-damage-to-its-target")]
    public static void ImmunityAddsDamageToTheTarget()
    {
        using var match = Arena(1, "Orianna", 100.0);
        var hero = match.Hero;
        TestAssert.True(match.Cast(3, match.Foe), "R casts on the enemy");
        match.Advance(0.3);
        var attack = hero.AttackDamage;
        Swing(match, match.Foe, 0.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 1, "an attack lands " + Dump(hits));
        // Rank one: 1.2 times the damage of the hit (BuffOnPreDealDamage of her own bonus buff), then the resistance of 30.
        TestAssert.Near(Mitigated(1.2 * attack, match.Foe.Armor), hits[0].Dealt, 0.5, "20 percent more damage to the target at rank one, after her armour " + Dump(hits));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-poppy-diplomatic-immunity-ends-with-its-target")]
    public static void ImmunityEndsWhenTheTargetDies()
    {
        using var match = Arena(1);
        TestAssert.True(match.Cast(3, match.Foe), "R casts on the enemy");
        match.Advance(0.3);
        TestAssert.True(Of(match.Hero, "PoppyDiplomaticImmunity") is not null, "the immunity is up");
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        match.Advance(0.2);
        TestAssert.True(Of(match.Hero, "PoppyDiplomaticImmunity") is null, "the target's death ends it (BuffOnDeath of the mark)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
