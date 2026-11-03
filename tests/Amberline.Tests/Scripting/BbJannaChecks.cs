using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, phase 3 (19 October 2026): Janna plays from her shipped scripts in a production <c>MatchRuntime</c> (the hand-written
/// module is deleted). Every expected number is read from the shipped files (script tables through <see cref="BbScriptLoader"/>, spell inibins
/// through <see cref="GameData"/>) and pinned a second time as the literal the two files agree on. Written for the owner's later full run;
/// nobody ran them when they were written (owner rule, tests only at the end of a cycle).
/// </summary>
public static class BbJannaChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static readonly string[] WaveSpells = ["HowlingGaleSpell", "HowlingGaleSpell1", "HowlingGaleSpell2", "HowlingGaleSpell3"];

    // ------------------------------------------------------------------------------------------------------------
    // Closure
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-janna-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Janna");
        foreach (var name in new[] { "CharScriptJanna", "HowlingGale", "HowlingGaleSpell", "HowlingGaleSpell1", "HowlingGaleSpell2",
                     "HowlingGaleSpell3", "SowTheWind", "EyeOfTheStorm", "JannaEoTSBuff", "ReapTheWhirlwind", "Move", "MoveAway",
                     "Slow", "Tailwind", "TailwindSelf", "ExpirationTimer" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Janna closure");
        var hero = new Entity { ChampionName = "Janna" };
        TestAssert.Equal(["HowlingGale", "SowTheWind", "EyeOfTheStorm", "ReapTheWhirlwind"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Janna.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Janna");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Janna hook runs is bound: " + string.Join(", ", unbound));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Howling Gale
    // ------------------------------------------------------------------------------------------------------------

    // The damage tables of the four tiers: BuffOnSpellHit of each HowlingGaleSpell script (the first BBApplyDamage of its hook).
    private static double[] WaveDamage(int tier)
    {
        var block = Blocks(Script(WaveSpells[tier]), "BuffOnSpellHit").First(item => item.Function == "BBApplyDamage");
        return Table(block, "DamageByLevel");
    }

    // Janna and a Foe 900 units east of her, the wave's line.
    private static void AimEast(Match match)
    {
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7900, 7000);
        match.Foe.Health = match.Foe.MaxHealth = 5000;
        match.Foe.HealthRegen = 0.0;
    }

    private static Entity? Cube(Match match) => match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Placemarker);

    [Check("scripting.bb-janna-q-howling-gale-charges-and-releases-on-the-recast")]
    public static void HowlingGaleChargesAndTheRecastReleasesIt()
    {
        TestAssert.Equal([60.0, 85.0, 110.0, 135.0, 160.0], WaveDamage(0), "HowlingGaleSpell.luaobj tier 0 damage");
        TestAssert.Equal([85.0, 115.0, 150.0, 185.0, 220.0], WaveDamage(1), "HowlingGaleSpell1.luaobj tier 1 damage");
        TestAssert.Equal([110.0, 145.0, 190.0, 235.0, 280.0], WaveDamage(2), "HowlingGaleSpell2.luaobj tier 2 damage");
        TestAssert.Equal([135.0, 175.0, 230.0, 285.0, 340.0], WaveDamage(3), "HowlingGaleSpell3.luaobj tier 3 damage");
        using var match = new Match("Janna");
        match.Learn(3);
        AimEast(match);
        var data = GameData.Default.Spell("HowlingGale", 5);
        var cost = data.ManaCosts[2];
        var manaBefore = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Howling Gale is accepted");
        var charge = Buff(match.Hero, "HowlingGale");
        TestAssert.True(charge is not null, "the cast adds the HowlingGale buff");
        TestAssert.Near(3.0, charge!.ExpiresAt, 1e-6, "HowlingGale.luaobj charge duration");
        TestAssert.Equal(0, match.Hero.SpellTargetingOverrides.TryGetValue(0, out var type) ? type : -1,
            "the charge switches slot Q to TTYPE_Self (BBSetTargetingType)");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "the first statement of SelfExecute clears the cooldown so that the recast is possible");
        NearF32(manaBefore - cost, match.Hero.Mana, "the first press pays the mana cost of rank 3");
        TestAssert.True(((IHeroStatsHooks)match.Runtime.Dependencies.HeroStatsHooks).IsSpellToggledOn(match.Hero, 0),
            "the SpellToggleSlot buff toggles Q on");
        TestAssert.True(Cube(match) is null, "no wave yet");

        match.Advance(0.6);
        var instance = match.Module.Buffs!.Find(match.Hero, "HowlingGale")!;
        TestAssert.Near(0.6, instance.LifeTime, 0.05, "LifeTime is the time the buff has been on Janna");
        var manaCharging = match.Hero.Mana;
        TestAssert.True(match.CanCast(0).Allowed, "the recast is possible while charging");
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "the second press releases the wave");
        TestAssert.True(Buff(match.Hero, "HowlingGale") is null, "the recast removes the buff");
        TestAssert.Near(manaCharging, match.Hero.Mana, 2.0, "the release costs no mana (a toggled slot casts for free)");
        TestAssert.Equal(6, match.Hero.SpellTargetingOverrides.TryGetValue(0, out var after) ? after : -1, "the release restores TTYPE_Location");
        var cooldown = data.Cooldowns[2] * (1.0 + match.Hero.CooldownMod);
        TestAssert.Near(cooldown, match.Hero.SpellCooldowns[0], 1e-6, "OnBuffDeactivate sets the cooldown of the rank (HowlingGale.inibin)");
        var cube = Cube(match);
        TestAssert.True(cube is not null, "the release spawns the TestCube placemarker the wave is cast from");
        // Batch 202: a placemarker is announced like a minion (Entity.Announced); it is still no target.
        TestAssert.True(cube!.Announced && cube.Protected, "a placemarker is announced and is no target");
        TestAssert.Equal("HowlingGaleSpell", cube.ActiveSpellNames[SpellSystem.ExtraSpellSlotStart], "tier 0 (a release inside the first second)");

        match.Advance(2.0);
        var hit = HitsOn(match, "Foe");
        TestAssert.Equal(1, hit.Count, "the wave hits the Foe once");
        NearF32(110.0, hit[0].Raw, "tier 0 at rank 3 is 110 magic damage (+0.75 AP, none here)");
        TestAssert.True(Buff(match.Foe, "Move") is not null || match.Log.Buffs.Any(line => line.Contains("add Foe Move ", StringComparison.Ordinal)),
            "the hit knocks the Foe up with the Move buff");
        TestAssert.True(match.Foe.Position.X > 7900.0 - 120.0 && match.Foe.Position.X < 7900.0 + 120.0, "the hop is at most 100 units");
        match.Advance(3.0);
        // Batch 246: the cube dies by the combat kill, so its corpse stays until the reap like any unit's.
        TestAssert.True(Cube(match) is not { Alive: true }, "ExpirationTimer ends the TestCube four seconds after the release");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-janna-q-howling-gale-tier-follows-the-charge-time")]
    public static void HowlingGaleTierFollowsTheTimeTheChargeRan()
    {
        // The recast at 0.5, 1.5 and 2.5 s picks the tiers 0, 1 and 2 (LifeTime <= 1, <= 2, <= 3); the automatic release at 3 s reads
        // more than 3 (the update that finds the buff run out counts the step that ended it) and is the full tier 3.
        var expected = new[] { (0.5, 0), (1.5, 1), (2.5, 2), (-1.0, 3) };
        foreach (var (release, tier) in expected)
        {
            using var match = new Match("Janna");
            match.Learn(2);
            AimEast(match);
            TestAssert.True(match.Cast(0, at: match.Foe.Position), "cast");
            if (release > 0.0)
            {
                match.Advance(release);
                TestAssert.True(match.Cast(0, at: match.Foe.Position), "recast");
            }
            match.Advance(release > 0.0 ? 2.5 : 5.0);
            TestAssert.Equal(WaveSpells[tier], Cube(match)!.ActiveSpellNames[SpellSystem.ExtraSpellSlotStart],
                $"release at {release} s casts {WaveSpells[tier]}");
            var hit = HitsOn(match, "Foe");
            TestAssert.Equal(1, hit.Count, $"tier {tier}: one hit");
            NearF32(WaveDamage(tier)[1], hit[0].Raw, $"tier {tier} at rank 2");
        }
    }

    [Check("scripting.bb-janna-q-wave-range-is-the-tier-range")]
    public static void TheWaveFliesTheRangeOfItsTier()
    {
        // HowlingGaleSpell..3 CastRange 1100 / 1300 / 1500 / 1700 (inibins). The line's contact radius is its LineWidth (150) plus the Foe's combat
        // radius (65), so the swept line reaches a unit whose centre is within range + 215 of the cube: 1515 for tier 1, 1715 for tier 2. A Foe 1615
        // units away is out of reach of tier 1 and inside tier 2's.
        var ranges = WaveSpells.Select(name => GameData.Default.Spell(name, 5).CastRange).ToArray();
        TestAssert.Equal([1100.0, 1300.0, 1500.0, 1700.0], ranges, "the tier ranges of the inibins");
        foreach (var tier in new[] { 1, 2 })
        {
            using var match = new Match("Janna");
            match.Learn(1);
            AimEast(match);
            match.Foe.Position = new Position2(8615, 7000);
            TestAssert.True(match.Cast(0, at: match.Foe.Position), "cast");
            match.Advance(tier == 1 ? 1.5 : 2.5);
            TestAssert.True(match.Cast(0, at: match.Foe.Position), "recast");
            match.Advance(3.0);
            TestAssert.Equal(tier == 1 ? 0 : 1, HitsOn(match, "Foe").Count, tier == 1 ? "tier 1 (1300) falls short of 1615" : "tier 2 (1500) reaches 1615");
        }
    }

    [Check("scripting.bb-janna-q-dying-while-charging-releases-the-wave-from-where-she-stood")]
    public static void TheChargeReleasesWhenJannaDies()
    {
        // Script truth: HowlingGale's OnBuffDeactivate runs when death removes the buff, and nothing in it asks whether Janna lives. The
        // hand-written module cancelled the charge instead (a Reconstruction nobody could confirm).
        using var match = new Match("Janna");
        match.Learn(1);
        AimEast(match);
        match.Hero.GodMode = false;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "cast");
        match.Advance(0.5);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(!match.Hero.Alive, "Janna is dead");
        match.Advance(3.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the wave she released as she died hits the Foe");
    }

    [Check("scripting.bb-janna-q-the-testcube-is-registered-but-never-announced")]
    public static void ThePlacemarkerIsKeptOutOfTheReplication()
    {
        using var match = new Match("Janna");
        match.Learn(1);
        AimEast(match);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.3);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.2);
        var cube = Cube(match);
        TestAssert.True(cube is not null && cube.Placemarker, "the TestCube is a placemarker");
        match.Runtime.Delivery.Flush();
        match.Advance(0.2);
        match.Runtime.Delivery.Flush();
        // Batch 202: the placemarker was announced with its spawn (Entity.Announced). Created is only what the views know of it now.
        TestAssert.True(cube!.Announced, "the placemarker was announced with its spawn packet, like a minion");
        TestAssert.True(cube.Alive, "it is alive until ExpirationTimer ends it");
    }

    /// <summary>
    /// Batch 246 (batch 241 item 4c): <c>ExpirationTimer</c> ends the TestCube with 9999 <c>DAMAGESOURCE_INTERNALRAW</c> damage to itself
    /// (<c>BBApplyDamage</c>, Attacker = Target = the cube). The hit takes the retail route, <c>CombatService.ApplyHit</c>, whose
    /// <c>Protected</c> refusal (the engine's placemarker flag) lets a unit's own InternalRaw hit through, as its invulnerability test already
    /// did; any other hit on the Protected cube is still refused. The cube dies by the ordinary kill (its death time is set and its corpse
    /// is reaped like any unit's), no longer retired outside the combat path.
    /// </summary>
    [Check("scripting.bb-expiration-timer-kills-its-placemarker-through-the-combat-hit")]
    public static void ExpirationTimerKillsItsPlacemarkerThroughTheCombatHit()
    {
        using var match = new Match("Janna");
        match.Learn(1);
        AimEast(match);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.3);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.2);
        var cube = Cube(match)!;
        TestAssert.True(cube.Protected && cube.Alive, "the TestCube is a live Protected placemarker");
        TestAssert.False(match.Runtime.Combat.ApplyHit(match.Hero.NetId, cube.NetId, 9999.0, DamageType.True, DamageSourceKind.InternalRaw),
            "another unit's InternalRaw hit on the Protected cube is refused");
        TestAssert.False(match.Runtime.Combat.ApplyHit(cube.NetId, cube.NetId, 9999.0, DamageType.True, DamageSourceKind.Spell),
            "the cube's own hit of another source is refused");
        TestAssert.True(cube.Alive, "the refused hits leave the cube alive");
        match.Advance(5.0);
        TestAssert.False(cube.Alive, "ExpirationTimer ends the cube");
        TestAssert.True(cube.DeathTime is not null, "the cube died by the combat kill (CombatService.Kill stamps the death time)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Batch 277 (the worm corpus held by batch 274): since batch 246 a placemarker's own end (ExpirationTimer's, PropelSpellCaster's 9999
    /// InternalRaw self hit) runs through <c>CombatService.Kill</c>, whose lane-unit share then paid the enemy heroes near it the character's
    /// <c>ExpGivenOnDeath</c> (the Baron's geyser placemarker SpellBook1: 77 x 0.92 = 70.84 to the hero beside it). Before batch 246 the
    /// helper left the world unpaid, and no shipped script pays for it, so its death pays nobody.
    /// </summary>
    [Check("scripting.bb-expiration-timer-placemarker-death-pays-no-experience")]
    public static void ExpirationTimerPlacemarkerDeathPaysNoExperience()
    {
        using var match = new Match("Janna");
        match.Learn(1);
        AimEast(match);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.3);
        match.Cast(0, at: match.Foe.Position);
        match.Advance(0.2);
        var cube = Cube(match)!;
        TestAssert.True(cube.ExperienceReward > 0.0, $"the TestCube's character data names an experience reward ({cube.ExperienceReward})");
        var dx = match.Foe.Position.X - cube.Position.X;
        var dz = match.Foe.Position.Z - cube.Position.Z;
        TestAssert.True(dx * dx + dz * dz <= CombatService.ExperienceShareRadius * CombatService.ExperienceShareRadius,
            "the enemy hero stands inside the lane-unit share radius of the cube");
        var experience = match.Foe.Experience;
        match.Advance(5.0);
        TestAssert.False(cube.Alive, "ExpirationTimer ends the cube");
        TestAssert.Equal(experience, match.Foe.Experience, "the placemarker's own end pays the enemy hero no experience");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Zephyr and the passive
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-janna-w-zephyr-buff-launch-damage-and-slow")]
    public static void ZephyrIsAPassiveBuffThatTheLaunchTakesAndTheHitSlows()
    {
        var damage = Table(Blocks(Script("SowTheWind"), "TargetExecute").First(block => block.Function == "BBApplyDamage"), "DamageByLevel");
        TestAssert.Equal([60.0, 115.0, 170.0, 225.0, 280.0], damage, "SowTheWind.luaobj damage");
        var slow = ByLevel("SowTheWind", "TargetExecute", "MoveSpeedMod");
        TestAssert.Equal([-0.24, -0.3, -0.36, -0.42, -0.48], slow, "SowTheWind.luaobj slow");
        var speed = ByLevel("SowTheWind", "BuffOnUpdateStats", "MovementSpeedMod");
        TestAssert.Equal([0.08, 0.1, 0.12, 0.14, 0.16], speed, "SowTheWind.luaobj passive speed");
        using var match = new Match("Janna");
        match.Learn(2);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7400, 7000);
        match.Advance(0.5);
        var passive = Buff(match.Hero, "SowTheWind");
        TestAssert.True(passive is not null, "CharScriptJanna adds the SowTheWind buff once W is learned and ready");
        TestAssert.Near(speed[1], passive!.Payload.MoveSpeedPercent, 1e-6, "the buff's move speed (BuffOnUpdateStats)");
        TestAssert.True(passive.Payload.Ghosted, "Zephyr makes Janna ghosted (SetGhosted)");

        var foeSpeed = match.Foe.Speed;
        TestAssert.True(match.Cast(1, match.Foe), "Zephyr is accepted");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "SowTheWind") is not null, "the buff stays through the cast time");
        match.Advance(0.4);
        TestAssert.True(Buff(match.Hero, "SowTheWind") is null, "BuffOnLaunchMissile removes it when the missile leaves");
        // The slow is read the step it is added: a buff's ExpiresAt is the time it has left.
        for (var wait = 0; wait < 45 && Buff(match.Foe, "Slow") is null; wait++) match.Advance(1.0 / 30.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        NearF32(damage[1], hits[0].Raw, "rank 2 damage (+0.8 AP)");
        var slowed = Buff(match.Foe, "Slow");
        TestAssert.True(slowed is not null, "the hit adds the global Slow");
        TestAssert.Near(4.0, slowed!.ExpiresAt, 0.2, "SowTheWind.luaobj slow duration");
        TestAssert.True(match.Foe.Speed < foeSpeed * (1.0 + slow[1] + 0.02), "the Foe is slowed by the script's multiplier");
        match.Advance(12.0);
        TestAssert.True(Buff(match.Hero, "SowTheWind") is not null, "the passive buff returns when the cooldown ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Batch 348 item 5 (the duel replay's cases 65 and 69): a SowTheWind buff born inside Zephyr's windup outlives the launch. CharScriptJanna's
    /// UpdateSelfBuffActions adds SowTheWind on every update where W is learned, ready and the buff is missing, and since batch 340 W reads ready
    /// until its cast frame. The buff's BuffOnSpellCast marks the cast (SowCast = 1) and its BuffOnLaunchMissile removes the buff only when that mark
    /// is set; a buff added after the press never saw the cast, so the launch keeps it. The duel replay sends its request before any update has run
    /// (its recorded start state holds no SowTheWind), so its passive is born in the windup: the missile launches and hits all the same. In play the
    /// buff is on her from the update after she learns W (the check above), and this order is only reached when W comes off cooldown within the
    /// update before the press.
    /// </summary>
    [Check("scripting.bb-janna-w-passive-born-in-the-windup-outlives-the-launch")]
    public static void ZephyrPassiveBornInTheWindupOutlivesTheLaunch()
    {
        using var match = new Match("Janna");
        match.Learn(2);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7400, 7000);
        TestAssert.True(Buff(match.Hero, "SowTheWind") is null, "no update has run: the character script has not added the passive yet");
        TestAssert.True(match.Cast(1, match.Foe), "Zephyr is accepted");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "SowTheWind") is not null, "the first update adds it inside the windup (W reads ready until its cast frame)");
        for (var wait = 0; wait < 45 && HitsOn(match, "Foe").Count == 0; wait++) match.Advance(1.0 / 30.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the missile launched and hit");
        TestAssert.True(Buff(match.Hero, "SowTheWind") is not null, "the buff never saw the cast (no SowCast), so its launch hook kept it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-janna-passive-tailwind-follows-the-character-script")]
    public static void TailwindIsAddedToTheAlliesAndRenewed()
    {
        using var match = new Match("Janna");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "TailwindSelf") is not null, "CharOnActivate adds TailwindSelf");
        var tailwind = Buff(match.Ally, "Tailwind");
        TestAssert.True(tailwind is not null, "and Tailwind on the allied hero");
        TestAssert.Near(0.03, tailwind!.Payload.MoveSpeedPercent, 1e-9, "Tailwind.luaobj +3 % movement speed");
        TestAssert.True(Buff(match.Foe, "Tailwind") is null, "an enemy gets none");
        match.Advance(5.0);
        TestAssert.True(Buff(match.Ally, "Tailwind") is not null, "UpdateSelfBuffActions renews it every four seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Eye of the Storm
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-janna-e-eye-of-the-storm-shield-and-attack-damage")]
    public static void EyeOfTheStormShieldsAndAddsAttackDamage()
    {
        var shield = ByLevel("EyeOfTheStorm", "TargetExecute", "ArmorAmount");
        TestAssert.Equal([80.0, 120.0, 160.0, 200.0, 240.0], shield, "EyeOfTheStorm.luaobj shield");
        var bonus = ByLevel("EyeOfTheStorm", "TargetExecute", "DamageBonus");
        TestAssert.Equal([14.0, 23.0, 32.0, 41.0, 50.0], bonus, "EyeOfTheStorm.luaobj attack damage");
        using var match = new Match("Janna");
        match.Learn(2);
        match.Ally.Health = 1000;
        var attackBefore = match.Ally.AttackDamage;
        TestAssert.True(match.Cast(2, match.Ally), "Eye of the Storm is accepted");
        var eye = Buff(match.Ally, "EyeOfTheStorm");
        var damage = Buff(match.Ally, "JannaEoTSBuff");
        TestAssert.True(eye is not null && damage is not null, "both buffs");
        TestAssert.Near(5.0, eye!.ExpiresAt, 1e-6, "shield duration");
        TestAssert.Near(5.0, damage!.ExpiresAt, 1e-6, "attack damage duration");
        TestAssert.Near(attackBefore + bonus[1], match.Ally.AttackDamage, 1e-6, "JannaEoTSBuff adds the rank's attack damage");
        var health = match.Ally.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Ally.NetId, shield[1] - 20.0, DamageType.Magic, DamageSourceKind.Spell);
        TestAssert.Near(health, match.Ally.Health, 1e-6, "the shield takes a hit it can cover whole");
        TestAssert.True(Buff(match.Ally, "EyeOfTheStorm") is not null, "and stays while it is not exceeded");
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Ally.NetId, 500.0, DamageType.Magic, DamageSourceKind.Spell);
        TestAssert.True(match.Ally.Health < health, "a hit above the rest goes through");
        TestAssert.True(Buff(match.Ally, "EyeOfTheStorm") is null, "and ends the shield");
        TestAssert.True(Buff(match.Ally, "JannaEoTSBuff") is not null, "the attack damage buff outlives its shield");
        match.Advance(5.5);
        TestAssert.True(Buff(match.Ally, "JannaEoTSBuff") is null, "and ends by its own time");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Monsoon
    // ------------------------------------------------------------------------------------------------------------

    private static void MonsoonStage(Match match)
    {
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(7100, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Ally.Health = 1000;
        match.Ally.HealthRegen = 0.0;
        match.Foe.Health = match.Foe.MaxHealth = 5000;
    }

    [Check("scripting.bb-janna-r-monsoon-is-a-four-second-script-channel")]
    public static void MonsoonChannelsForFourSecondsAfterItsWindup()
    {
        var length = Script("ReapTheWhirlwind").Global("ChannelDuration") as BbValue.Number;
        TestAssert.Near(4.0, length!.Value, 1e-12, "ReapTheWhirlwind.luaobj ChannelDuration");
        var tick = ByLevel("ReapTheWhirlwind", "ChannelingStart", "BaseTickAmount");
        TestAssert.Equal([35.0, 55.0, 75.0], tick, "ReapTheWhirlwind.luaobj heal per pulse");
        using var match = new Match("Janna");
        MonsoonStage(match);
        TestAssert.True(match.Cast(3, match.Hero), "Monsoon is accepted");
        var cast = match.Log.Casts[^1];
        // Batch 114: the total is the cast time plus the channel (the client ends its pose and bar at mTimeSpellEnd, one cast time after the channel).
        TestAssert.Near(0.25 + length.Value, cast.DesignerTotalTime, 1e-6, "the cast packet names the windup plus the channel as its total time");
        TestAssert.True((cast.Flags & 0x04) != 0, "and flags it as a channelling cast");
        TestAssert.True(match.Module.Channels!.Of(match.Hero) is null, "the channel begins when the 0.25 s windup ends");
        match.Advance(0.3);
        var channel = match.Module.Channels!.Of(match.Hero);
        TestAssert.True(channel is not null, "the channel is running");
        var buff = Buff(match.Hero, "ReapTheWhirlwind");
        TestAssert.True(buff is not null, "ChannelingStart adds the ReapTheWhirlwind buff");
        TestAssert.Near(10.0, buff!.ExpiresAt, 0.1, "its scripted lifetime is ten seconds");
        TestAssert.True(Buff(match.Foe, "MoveAway") is not null, "the first pulse knocks the enemy away (MoveAway, stun type, 0.75 s)");
        TestAssert.Near(0.75, Buff(match.Foe, "MoveAway")!.ExpiresAt, 0.1, "MoveAway.luaobj stun time");
        TestAssert.Equal(1, match.Log.Assists.Count(line => line.Contains("->Ally ", StringComparison.Ordinal)), "the first heal marks the ally as assisted");
        NearF32(1000.0 + tick[0], match.Ally.Health, "the first pulse heals 35 at rank 1");
        match.Advance(4.2);
        TestAssert.True(match.Module.Channels!.Of(match.Hero) is null, "the channel has ended");
        TestAssert.True(Buff(match.Hero, "ReapTheWhirlwind") is null, "ChannelingSuccessStop removes the buff");
        TestAssert.Equal(8, match.Log.Assists.Count(line => line.Contains("->Ally ", StringComparison.Ordinal)),
            "eight heals: the activation and one every 0.5 s up to 3.5 s");
        TestAssert.True(Math.Abs(match.Foe.Position.X - 7000.0) > 600.0, "the enemy was thrown towards 1000 units from Janna");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-janna-r-monsoon-stops-on-the-sources-of-a-channel")]
    public static void MonsoonStopsWhenJannaMovesIsDisabledCastsOrDies()
    {
        foreach (var source in new[] { "order", "stun", "cast", "death", "moved" })
        {
            using var match = new Match("Janna");
            MonsoonStage(match);
            match.Learn(2);
            match.Cast(3, match.Hero);
            match.Advance(1.0);
            TestAssert.True(match.Module.Channels!.Of(match.Hero) is not null, $"{source}: the channel runs");
            switch (source)
            {
                case "order":
                    match.Runtime.Spells.CancelOrder(match.Hero);
                    break;
                case "stun":
                    match.Runtime.SpellBuffs.Add(match.Hero, "Stun", BuffType.Stun, 1.0, match.Foe.NetId, null, canMitigateDuration: false);
                    break;
                case "cast":
                    TestAssert.True(match.Cast(2, match.Ally), "Eye of the Storm is accepted during the channel: the channel does not lock her");
                    break;
                case "death":
                    match.Hero.GodMode = false;
                    match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
                    break;
                case "moved":
                    match.Hero.Position += new Position2(60, 0);
                    break;
            }
            match.Advance(0.1);
            TestAssert.True(match.Module.Channels!.Of(match.Hero) is null, $"{source}: the channel ended");
            TestAssert.True(Buff(match.Hero, "ReapTheWhirlwind") is null, $"{source}: ChannelingCancelStop removed the buff");
            match.Advance(1.0);
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{source}: no script failed");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine rules the Janna scripts need
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-janna-nil-variable-of-a-tableless-number-adds-nothing")]
    public static void MoveAndMoveAwayRunWithoutTheirIdealDistanceVariable()
    {
        foreach (var name in new[] { "Move", "MoveAway" })
        {
            var move = Blocks(Script(name), "OnBuffActivate").First(block => block.Function is "BBMove" or "BBMoveAway");
            TestAssert.Equal("IdealDistance", move.Text("IdealDistanceVar"), $"{name}.luaobj names IdealDistanceVar");
            TestAssert.True(move.Text("IdealDistanceVarTable") is null, $"{name}.luaobj names no table for it, so the root scope is read");
        }
    }
}
