using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (19 October 2026): Tryndamere plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). The
/// expected numbers are the literals of the shipped scripts and spell inibins (rank three of Q, W and E unless a check says otherwise); the scenario
/// <c>S-tryndamere</c> shows the same facts, and more, on a headless match, and <c>S-tryndamere-reconnect</c> the bar after a reconnect. The rage pool itself
/// (kind Other, the empty life, the typed accessors) is checked in <c>BbPoolChecks</c>. Written and run by the worker that made them.
/// </summary>
public static class BbTryndamereChecks
{
    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Tryndamere next to the enemy hero (his attack range is 125), Q, W and E at rank three and R at three.</summary>
    private static Match Melee()
    {
        var match = new Match("Tryndamere");
        // Q rank one by the level-up path (CharOnLevelUpSpell adds the Bloodlust marker), then every rank set.
        match.Hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 0), "Q rank one");
        match.Learn(3);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7080, 7000);
        match.Foe.Armor = 40.0;
        match.Foe.SpellBlock = 30.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        SetRage(match, 0.0);
        return match;
    }

    private static void SetRage(Match match, double rage)
    {
        match.Hero.Mana = rage;
        foreach (var buff in match.Hero.Buffs.Where(buff => buff.Name == "RenektonInCombat").ToArray()) match.Runtime.Buffs.RemoveBuff(match.Hero, buff.Slot);
        match.Runtime.Effects.SendStats(match.Hero);
    }

    private static readonly AttackRolls Plain = new(1.0, 1.0, 1.0);
    private static readonly AttackRolls Critical = new(0.0, 1.0, 1.0);

    private static void Swing(Match match, Entity target, AttackRolls rolls, double seconds = 1.0)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target, rolls), "swing");
        match.Advance(seconds);
    }

    [Check("scripting.bb-tryndamere-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRegistry()
    {
        var module = new BbChampionModule("Tryndamere");
        TestAssert.Equal(21, module.ScriptNames.Count, "the Tryndamere closure is 21 scripts (the gap tool of the wave plan)");
        foreach (var name in new[] { "CharScriptTryndamere", "Bloodlust", "BloodlustMarker", "BloodlustParticle", "MockingShout", "MockingShoutSlow", "FacingMe", "slashCast",
                     "slash", "SlashBeenHit", "UndyingRage", "BattleFury", "RenektonInCombat" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Tryndamere");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var hero = new Entity { ChampionName = "Tryndamere" };
        TestAssert.Equal(["Bloodlust", "MockingShout", "slashCast", "UndyingRage"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Tryndamere.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "5, 5, 5 and 3 ranks");
        var info = ChampionCatalog.ByName("Tryndamere")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 23, RetailBot: false }, "playable as a top laner for 1350 IP, not a retail bot");
        using var match = new Match("Tryndamere");
        TestAssert.True(match.Runtime.Dependencies.ChampionDispatcher.FindModule("Tryndamere") is BbChampionModule, "production runs him from the script module");
    }

    [Check("scripting.bb-tryndamere-the-bar-is-a-red-other-pool-of-100-that-nothing-regenerates")]
    public static void TheBarAtTheStart()
    {
        using var match = new Match("Tryndamere");
        var hero = match.Hero;
        TestAssert.Equal(PrimaryResourceType.Other, GameData.Default.Champion(hero.StatCharacterName).PrimaryResource, "an Other pool");
        TestAssert.Equal(100.0, hero.MaxMana, "of 100");
        TestAssert.Near(0.0, hero.ManaRegen, 1e-9, "BaseStaticMPRegen 0: nothing regenerates it");
        TestAssert.True(hero.ParColorOverride is { } colours, "CharOnActivate paints the bar");
        TestAssert.Equal(0xFFFF0000u, hero.ParColorOverride!.Value.Bar, "red (255, 0, 0) opaque");
        TestAssert.Equal(0xFFAF0000u, hero.ParColorOverride.Value.Fade, "and its fade (175, 0, 0)");
        TestAssert.True(Of(hero, "BattleFury") is not null && Of(hero, "BloodlustMarker") is null, "Battle Fury from the start, the marker once Q is learnt");
        hero.SkillPoints = 1;
        TestAssert.True(match.Runtime.Spells.RankUp(hero, 0), "Q rank one");
        match.Advance(0.2);
        TestAssert.True(Of(hero, "BloodlustMarker") is not null, "Q rank one adds the Bloodlust marker");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-tryndamere-battle-fury-gives-rage-for-hits-crits-and-kills-and-drains-out-of-combat")]
    public static void BattleFuryFillsAndDrains()
    {
        using var match = Melee();
        var hero = match.Hero;
        var foe = match.Foe;
        Swing(match, foe, Plain);
        TestAssert.Near(5.0, hero.Mana, 0.01, "a plain hit is worth 5");
        Swing(match, foe, Critical, 1.5);
        TestAssert.Near(15.0, hero.Mana, 0.01, "a critical hit 10");
        // A kill is 10 on top of the hit's 5: a minion and a champion alike.
        var minion = Enemy(match, "TryndamereKill", new Position2(7040, 7000));
        minion.Health = 30.0;
        minion.Armor = 0.0;
        SetRage(match, 0.0);
        Swing(match, minion, Plain, 0.5);
        TestAssert.True(!minion.Alive, "the minion died");
        TestAssert.Near(15.0, hero.Mana, 0.01, "5 for the hit and 10 for the kill");
        // The flag of a fight (eight seconds after a hit dealt or taken) holds the bar; a quiet bar loses 5 a second.
        SetRage(match, 0.0);
        hero.Mana = 60.0;
        Swing(match, foe, Plain, 1.0);
        var held = hero.Mana;
        TestAssert.True(held >= 60.0 && held <= 65.0, $"the hit landed ({held})");
        match.Advance(6.0);
        TestAssert.Near(held, hero.Mana, 0.01, "six seconds into the fight nothing has drained");
        match.Advance(8.0);
        TestAssert.True(hero.Mana < held - 4.9 && hero.Mana > held - 40.0, $"after the eight seconds it drains 5 a second ({hero.Mana})");
        match.Advance(20.0);
        TestAssert.Near(0.0, hero.Mana, 1e-9, "and empties");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-tryndamere-rage-adds-0035-crit-chance-a-point")]
    public static void RageIsCritChance()
    {
        using var match = Melee();
        var hero = match.Hero;
        foreach (var rage in new[] { 0.0, 40.0, 100.0 })
        {
            SetRage(match, rage);
            // BattleFury's crit chance is a stat its BuffOnUpdateStats writes, and that hook samples on the 0.25 s
            // stats grid (batch 150 part 2a) rather than every update; the wait has to cross a sample or the read
            // is the previous rage's modifier still sitting in the payload.
            match.Advance(0.3);
            TestAssert.Near(0.0035 * hero.Mana, hero.CritChance, 1e-6, $"{hero.Mana} rage is {0.0035 * hero.Mana} crit chance");
        }
        TestAssert.True(Of(hero, "BloodlustParticle") is not null, "the flame shows from 3 rage up");
        SetRage(match, 0.0);
        match.Advance(0.3);
        TestAssert.True(Of(hero, "BloodlustParticle") is null, "and goes with it");
    }

    [Check("scripting.bb-tryndamere-bloodlust-heals-from-the-rage-and-spends-it")]
    public static void BloodlustHealsAndSpendsTheBar()
    {
        using var match = Melee();
        var hero = match.Hero;
        match.Advance(0.3);
        hero.Health = hero.MaxHealth;
        // BloodlustMarker's attack damage is its own BuffOnUpdateStats reading the missing health share, sampled on
        // the 0.25 s stats grid (batch 150 part 2a): each read below waits past a sample or it is the previous
        // health's marker still in the payload.
        match.Advance(0.3);
        var atFull = hero.AttackDamage;
        hero.Health = hero.MaxHealth * 0.5;
        match.Advance(0.3);
        // The marker: baseDmg 15 + 25 a missing share of health (rank 3), so half the health adds 12.5 above the full-health value.
        TestAssert.Near(12.5, hero.AttackDamage - atFull, 0.05, $"attack damage grows with the missing health: 25 for all of it at rank 3 (full {atFull}, half {hero.AttackDamage}, marker {Of(hero, "BloodlustMarker") is not null}, health {hero.Health}/{hero.MaxHealth}, ranks {string.Join(",", hero.SpellRanks)})");
        SetRage(match, 60.0);
        var before = hero.Health;
        TestAssert.True(match.Cast(0, hero), "Q casts");
        match.Advance(0.2);
        // 50 + 1.65 per point of rage at rank 3, plus 1.5 ability power (none): 149, less the regeneration of 0.2 s.
        TestAssert.Near(149.0, hero.Health - before, 1.0, "50 + 1.65 x 60 rage health");
        TestAssert.Near(0.0, hero.Mana, 1e-9, "the rage is spent");
        TestAssert.Near(12.0, hero.SpellCooldowns[0], 0.4, "twelve seconds");
        match.Refill();
        SetRage(match, 0.0);
        before = hero.Health;
        TestAssert.True(match.Cast(0, hero), "and casts with an empty bar");
        match.Advance(0.2);
        TestAssert.Near(50.0, hero.Health - before, 1.0, "50 health for no rage");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-tryndamere-mocking-shout-needs-an-enemy-champion-cuts-damage-and-slows-those-facing-away")]
    public static void MockingShoutCutsDamageAndSlowsTheOnesFacingAway()
    {
        using var match = Melee();
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7000 + 1500, 7000);
        match.Refill();
        TestAssert.False(match.CanCast(1).Allowed, "refused with no enemy champion within 800");
        foe.Position = new Position2(7300, 7000);
        foreach (var facingAway in new[] { true, false })
        {
            match.Refill();
            foe.Facing = facingAway ? new Position2(1.0, 0.0) : new Position2(-1.0, 0.0);
            match.Advance(0.3);
            var speed = foe.Speed;
            var damage = foe.AttackDamage;
            TestAssert.True(match.Cast(1, hero), "W casts");
            match.Advance(1.0);
            TestAssert.Near(-60.0, foe.AttackDamage - damage, 0.01, "60 less attack damage at rank 3 for everyone in range");
            var expected = facingAway ? HeroStatCalculator.ApplyMovementSoftCaps(speed * (1.0 - 0.45)) : speed;
            TestAssert.Near(expected, foe.Speed, 0.5, facingAway ? "45 percent slower (through the soft cap) when facing away" : "no slow when facing him");
            TestAssert.True((Of(foe, "MockingShoutSlow") is not null) == facingAway, "the slow buff only for those facing away");
            match.Advance(4.2);
            TestAssert.Near(damage, foe.AttackDamage, 0.01, "both end after four seconds");
            TestAssert.Near(speed, foe.Speed, 0.5, "speed back");
            foe.Buffs.Where(buff => buff.Name.StartsWith("MockingShout", StringComparison.Ordinal)).ToList().ForEach(buff => match.Runtime.Buffs.RemoveBuff(foe, buff.Slot));
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-tryndamere-spinning-slash-dashes-hits-once-gives-rage-and-clamps-at-645")]
    public static void SpinningSlashDashes()
    {
        using var match = Melee();
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7400, 7000);
        foe.Armor = 40.0;
        var start = hero.Position;
        var bonus = hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Tryndamere"), hero.Level).AttackDamage;
        var health = foe.Health;
        TestAssert.True(match.Cast(2, at: new Position2(7600, 7000)), "E casts at 600");
        var trace = new List<string>();
        for (var step = 0; step < 5; step++)
        {
            match.Advance(0.1);
            trace.Add($"{(step + 1) * 0.1:0.0}s rage={hero.Mana:0.#} foe-{health - foe.Health:0.#} pos={Distance(start, hero.Position):0}");
        }
        var mid = Distance(start, hero.Position);
        match.Advance(1.0);
        var moved = Distance(start, hero.Position);
        TestAssert.True(mid > 100.0 && mid < 599.0, "the dash takes time: speed 900 a second");
        TestAssert.Near(600.0, moved, 3.0, "and ends at the point");
        var raw = 130.0 + 1.2 * Math.Max(0.0, bonus) + hero.AbilityPower;
        TestAssert.Near(raw * 100.0 / 140.0, health - foe.Health, 3.0, "130 + 1.2 bonus attack damage, once, less 40 armor");
        var near = match.Runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team != hero.Team && Distance(unit.Position, new Position2(7300, 7000)) < 600.0)
            .Select(unit => $"{unit.Name}:{unit.Kind}:{unit.UnitType}").ToArray();
        TestAssert.Near(5.0, hero.Mana, 0.5, "5 rage for a champion (enemy units near the path: " + string.Join(", ", near) + "; " + string.Join(" ", trace) + ")");
        TestAssert.Near(11.0 - 1.5, hero.SpellCooldowns[2], 0.5, "rank three: eleven seconds from the press");
        // A far point: the dash is clamped to 645. Along the lane diagonal, since the straight line east of the start runs out of walkable ground at 635 (the landing is the furthest walkable point).
        match.Refill();
        SetRage(match, 0.0);
        foe.Position = new Position2(9000, 9000);
        hero.Position = start;
        hero.Facing = new Position2(0.7071, 0.7071);
        TestAssert.True(match.Cast(2, at: new Position2(7000 + 636.4, 7000 + 636.4)), "E casts at 900");
        match.Advance(2.0);
        TestAssert.Near(645.0, Distance(start, hero.Position), 3.0, "the dash stops at 645");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    [Check("scripting.bb-tryndamere-a-critical-hit-cuts-two-seconds-off-spinning-slash")]
    public static void CritsCutTheCooldown()
    {
        using var match = Melee();
        var hero = match.Hero;
        SetRage(match, 20.0);
        match.Advance(0.2);
        hero.SpellCooldowns[2] = 20.0;
        Swing(match, match.Foe, Critical, 1.0);
        TestAssert.Near(17.0, hero.SpellCooldowns[2], 0.4, "two seconds off at the hit, and the second that passed");
        TestAssert.Near(30.0, hero.Mana, 0.01, "a critical hit is worth 10 rage");
        // Not while it is ready: a cooldown of 0 stays 0 and nothing else happens.
        hero.SpellCooldowns[2] = 0.0;
        Swing(match, match.Foe, Critical, 1.0);
        TestAssert.Near(0.0, hero.SpellCooldowns[2], 1e-9, "a ready spell is left alone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Batch 227 (owner request, 25 October 2026; patch V1.0.0.108: "Shrine (spawn) turrets now ignore all resistances and immunities"):
    // the fountain's swing is the shipped OrderTurretShrineBasicAttack / ChaosTurretShrineBasicAttack TargetExecute, a true-damage hit with
    // DAMAGESOURCE_INTERNALRAW on a champion, which kills through Undying Rage. The swing is the real one: QueueBasicAttack, windup, missile, impact.
    [Check("scripting.bb-tryndamere-fountain-kills-through-undying-rage")]
    public static void TheFountainKillsThroughUndyingRage()
    {
        using var match = new Match("Tryndamere");
        match.Learn(3);
        match.Refill();
        var hero = match.Hero;
        var fountain = match.ChaosFountain;
        hero.Position = fountain.Position + new Position2(400, 0);
        hero.Health = 600.0;
        TestAssert.True(match.Cast(3), "R casts");
        TestAssert.True(Of(hero, "UndyingRage") is not null, "Undying Rage is up");
        // The shrine's own acquisition would swing on its own tick; the explicit swing starts from a clear cooldown.
        fountain.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(fountain, hero), "the fountain swings at him");
        match.Advance(2.0);
        TestAssert.False(hero.Alive, "its 1000 true damage kills him through Undying Rage");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // The guard the fountain's rule must not reach: a lane turret's basic attack still stops at one health under Undying Rage.
    [Check("scripting.bb-tryndamere-lane-turret-is-held-at-one-health-by-undying-rage")]
    public static void ALaneTurretIsStillHeldAtOneHealth()
    {
        using var match = new Match("Tryndamere");
        match.Learn(3);
        match.Refill();
        var hero = match.Hero;
        var turret = match.ChaosLaneTurret;
        // The Chaos fountain's reach (2250) can touch a champion placed beside this turret, and its own shots are InternalRaw (they do kill
        // through Undying Rage now). Hold the fountain off its swing so this guard measures the lane turret's shot alone.
        match.ChaosFountain.AttackCooldown = 1000.0;
        hero.Position = turret.Position + new Position2(400, 0);
        hero.Health = 50.0;
        TestAssert.True(match.Cast(3), "R casts");
        turret.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(turret, hero), "the lane turret swings at him");
        match.Advance(2.0);
        TestAssert.True(hero.Alive, "a lane turret's basic attack does not kill him in Undying Rage");
        TestAssert.True(hero.Health < 10.0, "he is held at one health (plus regeneration)");
    }

    // The guard for the damage number: the fountain deals its shipped 1000 true damage to a champion, armor and magic resist unchanged.
    [Check("scripting.bb-tryndamere-fountain-hit-is-1000-true-with-no-armor")]
    public static void TheFountainHitIsTheShippedNumberWithNoArmor()
    {
        using var match = new Match("Tryndamere");
        match.Refill();
        var hero = match.Hero;
        var fountain = match.ChaosFountain;
        // Batch 535: the resistances and the pool are the hero's own stat modifiers, not direct field writes. Since batch 467 a buff's 0.25 s
        // stats sample rebuilds his stats (HeroStats.Refresh) in this window, on the tick his BloodlustParticle is added. That put the
        // written 5000 health and 1000 armor back to his level-one 559 and 18 one tick after the hit.
        hero.PermanentStatMods["FlatArmorMod"] = 1000.0;
        hero.PermanentStatMods["FlatSpellBlockMod"] = 1000.0;
        hero.PermanentStatMods["FlatHPPoolMod"] = 5000.0;
        match.Runtime.HeroStats.Refresh(hero, push: false);
        hero.Health = hero.MaxHealth;
        var before = hero.Health;
        // The shrine spell is CastType 0, so the hit lands at the 0.16 s cast frame (TurretRules.StatsFor); the next swing comes 0.4 s after
        // the first and lands after 0.3 s.
        hero.Position = fountain.Position + new Position2(150, 0);
        fountain.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(fountain, hero), "the fountain swings at him");
        match.Advance(0.3);
        TestAssert.True(hero.Armor >= 1000.0 && hero.SpellBlock >= 1000.0, "the 1000 resistances held through the hit");
        // Ten units of tolerance for the regeneration. A physical or magic 1000 through 1000 resistance deals about 91, so this also pins
        // the damage type.
        TestAssert.Near(before - 1000.0, hero.Health, 10.0, "1000 true damage, the armor and the magic resist are not taken off it");
    }

    [Check("scripting.bb-tryndamere-undying-rage-stops-death-heals-to-three-percent-and-gives-rage")]
    public static void UndyingRageCannotDie()
    {
        using var match = Melee();
        var hero = match.Hero;
        hero.MaxHealth = 1600.0;
        hero.Health = 300.0;
        var foe = match.Foe;
        TestAssert.True(match.Cast(3, hero), "R casts");
        match.Advance(0.3);
        TestAssert.True(Of(hero, "UndyingRage") is not null, "the buff");
        TestAssert.Near(100.0, hero.Mana, 6.0, "100 rage at rank 3 (less a tick of the drain)");
        TestAssert.Near(90.0, hero.SpellCooldowns[3], 1.0, "ninety seconds at rank 3");
        match.Runtime.Combat.ApplyHit(foe.NetId, hero.NetId, 5000.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.2);
        TestAssert.True(hero.Alive, "a lethal hit does not kill");
        TestAssert.Near(1.0, hero.Health, 1.5, "it leaves one health");
        match.Runtime.Combat.ApplyHit(foe.NetId, hero.NetId, 5000.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.2);
        TestAssert.True(hero.Alive, "and again");
        match.Advance(5.0);
        TestAssert.True(Of(hero, "UndyingRage") is null, "the buff ends after five seconds");
        TestAssert.Near(0.03 * hero.MaxHealth, hero.Health, 8.0, "with the health at 3 percent of the maximum (OnBuffDeactivate heals up to it)");
        TestAssert.True(hero.Alive, "alive");
        // Above 3 percent at the end nothing is healed.
        match.Refill();
        hero.Health = 600.0;
        hero.SpellCooldowns[3] = 0.0;
        TestAssert.True(match.Cast(3, hero), "R casts again");
        match.Advance(5.3);
        TestAssert.True(hero.Health < 600.0 + 15.0, "no heal at the end");
        // Without it a lethal hit kills.
        match.Runtime.Combat.ApplyHit(foe.NetId, hero.NetId, 50000.0, DamageType.True, DamageSourceKind.Raw);
        match.Advance(0.2);
        TestAssert.True(!hero.Alive, "the buff gone, the hit kills");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
