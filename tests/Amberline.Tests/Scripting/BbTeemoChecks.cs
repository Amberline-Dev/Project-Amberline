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
/// Batch 300 (25 October 2026): Teemo plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module). Expected numbers are the
/// literals of the shipped scripts (CharScriptTeemo, BlindingDart, MoveQuick, ToxicShot, BantamTrap, Camouflage and their buffs, read from the bytecode listings) and the
/// inibins; the scenario <c>S-teemo</c> shows the same facts on a headless match. Teemo and the foe stand at level one with 30 magic resist, so the
/// level-dependent resistance of a higher-level foe does not move the figures. Training goes through the rank-up path, so the character script's level-up hook
/// adds the passive, the aura and the mushroom charges the way the client's own casts do.
/// </summary>
public static class BbTeemoChecks
{
    private static readonly double[] DartDamage = [80.0, 125.0, 170.0, 215.0, 260.0];
    private static readonly double[] TrapTick = [50.0, 100.0, 150.0];

    /// <summary>A match with Teemo trained to rank <paramref name="rank"/> (the level-up path, then the rank), the enemy 250 away and 30 magic resist.</summary>
    private static Match Arena(int rank = 1, string foe = "Ezreal", int mushrooms = 0)
    {
        var match = new Match("Teemo", foeChampion: foe);
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "Teemo reaches level six (the experience of the seat helper)");
        // Stored mushrooms before the trap is trained: the recharge counter starts only when the stack is below three (TeemoMushroomManager).
        if (mushrooms > 0) Mushrooms(match, mushrooms);
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"Teemo's slot {slot} is trained at level six");
        }
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        // The passive starts a five-second debuff when it is added (its OnBuffActivate): the probes start once it has run out, which is when the
        // passive's speed comes on (the debuff's end is the buff's own timer, not a removal).
        match.Advance(5.5);
        return match;
    }

    private static int Stacks(Entity unit, string name) => unit.Buffs.Where(buff => buff.Name == name).Sum(buff => buff.Stacks);

    /// <summary>Removes buffs through the script runtime (BBSpellBuffRemove's path), so the hooks and the stealth see the removal.</summary>
    private static void Drop(Match match, Entity unit, params string[] names)
    {
        foreach (var name in names) match.Module.Buffs!.Remove(unit, name, unit);
    }

    /// <summary>The Chaos hero or a Chaos minion hits the unit, the way a queued basic attack does (the same path the scenarios use).</summary>
    private static bool Swing(Match match, Entity attacker, Entity victim)
    {
        attacker.AttackCooldown = 0.0;
        return match.Runtime.Combat.QueueBasicAttack(attacker, victim);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Teemo");
        foreach (var name in new[] { "CharScriptTeemo", "BlindingDart", "MoveQuick", "ToxicShot", "BantamTrap", "Camouflage", "TeemoMushroomManager",
                     "TeemoMushroomCounter", "TeemoMoveQuickPassive", "BantamTrapTarget", "ToxicShotParticle" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Teemo");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(54, closure.ScriptNames.Count(), "the closure is 54 scripts (audit bb-coverage: 836 blocks, 71 functions, nothing dangling)");

        var hero = new Entity { ChampionName = "Teemo" };
        TestAssert.Equal(["BlindingDart", "MoveQuick", "ToxicShot", "BantamTrap"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Teemo.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the trap of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "the trap is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Teemo")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 17, RetailBot: false }, "playable as a top laner for 1350 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Teemo"), "listed once");

        // The tables the figures rest on, a second time as literals.
        var dart = Blocks(Script("BlindingDart"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal(DartDamage, Numbers(dart.Param("DamageByLevel")!), "Blinding Dart damage by rank (BBApplyDamage DamageByLevel)");
        var trapSetters = Blocks(Script("BantamTrap"), "BuffOnUpdateActions").Where(item => item.Function == "BBSetVarInTable").ToList();
        var tick = trapSetters.FirstOrDefault(item => item.Text("DestVar") == "DamagePerTick");
        TestAssert.True(tick is not null, "the trap sets DamagePerTick in BuffOnUpdateActions; its setters: " + string.Join(",", trapSetters.Select(item => item.Text("DestVar"))));
        TestAssert.Equal(TrapTick, Numbers(tick!.Param("SrcValueByLevel")!), "the trap's poison per tick by rank (BBSetVarInTable SrcValueByLevel)");
        var setters = Blocks(CharScript("Teemo"), "UpdateSelfBuffActions").Where(item => item.Function == "BBSetVarInTable").ToList();
        var recharge = setters.FirstOrDefault(item => item.Text("DestVar") == "MushroomCooldown");
        TestAssert.True(recharge is not null, "the recharge variable is set in UpdateSelfBuffActions: " + string.Join(",", setters.Select(item => item.Text("DestVar"))));
        TestAssert.Equal([35.0, 31.0, 27.0], Numbers(recharge!.Param("SrcValueByLevel")!), "the mushroom recharge by rank (CharScriptTeemo)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Blinding Dart
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-blinding-dart-damage-and-blind-follow-the-script")]
    public static void BlindingDartDamageAndBlind()
    {
        var measured = new List<double>();
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = Arena(rank);
            var foe = match.Foe;
            var hp = foe.Health;
            TestAssert.True(match.Cast(0, foe), $"rank {rank}: the dart is cast at the enemy");
            // The blind is on from the hit (the spell's TargetExecute) for DurationByLevel seconds; the damage is read at the same moment.
            var duration = new[] { 1.5, 1.75, 2.0, 2.25, 2.5 }[rank - 1];
            var start = match.Now;
            var on = -1.0;
            var off = -1.0;
            var dealt = 0.0;
            var miss = 0.0;
            while (match.Now - start < 6.0 && off < 0.0)
            {
                match.Advance(0.05);
                var blind = Buff(foe, "BlindingDart");
                if (blind is not null && on < 0.0)
                {
                    on = match.Now - start;
                    dealt = hp - foe.Health;
                    miss = blind.Payload.MissChance;
                }
                if (blind is null && on >= 0.0 && off < 0.0) off = match.Now - start;
            }
            TestAssert.True(on > 0.0, $"rank {rank}: the blind (the spell's own script) is on the victim once the dart lands");
            TestAssert.Near(1.0, miss, 1e-9, "IncFlatMissChanceMod +1 (BuffOnUpdateStats)");
            // The hit lands about 0.42 seconds after the cast (0.25 cast, 250 units of flight), and the blind lasts DurationByLevel after it.
            TestAssert.Near(0.42, on, 0.15, $"rank {rank}: the dart lands about 0.42 seconds after the cast");
            measured.Add(off - on);
            // The 1.2 allows for the victim's regeneration over the 0.6 seconds read.
            TestAssert.Near(Mitigated(DartDamage[rank - 1], 30.0), dealt, 1.2,
                $"rank {rank}: {DartDamage[rank - 1]} magic against 30 magic resist (no ability power on Teemo at level one)");
        }
        // The script's DurationByLevel: 1.5 seconds at rank one, 2.5 at rank five.
        TestAssert.True(measured[0] is > 1.4 and < 1.6 && measured[1] is > 2.4 and < 2.6,
            $"the blind lasts DurationByLevel after the hit: rank 1 {measured[0]:0.00} (1.5), rank 5 {measured[1]:0.00} (2.5)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Move Quick
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-move-quick-bursts-for-three-seconds-over-the-passive")]
    public static void MoveQuickBurstAndPassive()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        TestAssert.True(Buff(hero, "TeemoMoveQuickPassive") is not null, "the passive is on Teemo (added with W's level-up): " + Names(hero));
        TestAssert.Near(305.0 * 1.10, hero.Speed, 1e-6, "the passive speed: 10% at rank one (TeemoMoveQuickSpeed, 305 base)");
        hero.SpellCooldowns[1] = 0.0;
        hero.Mana = hero.MaxMana;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(1), "Move Quick is cast on Teemo");
        match.Advance(0.2);
        TestAssert.Near(305.0 * 1.20, hero.Speed, 1e-6, "the burst doubles the passive: 20% at rank one (MoveSpeedBonus 0.2)");
        TestAssert.True(Buff(hero, "MoveQuick") is not null, "the burst is the MoveQuick buff");
        TestAssert.Near(40.0, mana - hero.Mana + 0.5, 1.0, "40 mana (ManaCost 40)");
        TestAssert.Near(22.0, hero.SpellCooldowns[1] + 0.2, 0.3, "a 22 second cooldown");
        match.Advance(3.0);
        TestAssert.True(Buff(hero, "MoveQuick") is null, "the burst ends after three seconds (its TargetExecute add: Duration 3.0)");
        TestAssert.Near(305.0 * 1.10, hero.Speed, 1e-6, "the passive returns when the burst ends");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The passive: the speed is lost to a hero's hit, not to a minion's
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-passive-speed-is-lost-to-a-hero-for-five-seconds")]
    public static void PassiveSpeedEndsWithAHeroHit()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7300, 7000);
        // Standing still for the arena's wait made Teemo stealthed, and a stealthed Teemo cannot be attacked: the probe drops the stealth as a step would.
        hero.Position = new Position2(hero.Position.X + 40.0, hero.Position.Z);
        match.Advance(0.3);
        var hit = Swing(match, foe, hero);
        TestAssert.True(hit, "the enemy hero's attack is queued");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "TeemoMoveQuickDebuff") is not null, "a hero's hit starts the five-second debuff (BuffOnPreDamage, CO_IS_TYPE_HERO)");
        TestAssert.Near(305.0, hero.Speed, 1e-6, "no passive speed while the debuff runs");
        match.Advance(6.0);
        TestAssert.True(Buff(hero, "TeemoMoveQuickDebuff") is null, "the debuff ends five seconds after the hit");
        TestAssert.Near(305.0 * 1.10, hero.Speed, 1e-6, "the passive speed is back");
    }

    /// <summary>
    /// TeemoMoveQuickPassive's BuffOnPreDamage starts the debuff only for a hero (CO_IS_TYPE_HERO) or a turret (CO_IS_TYPE_TURRET) attacker: a lane
    /// minion's hit lands and the speed stays (batch 300 open question; batch 309 gave the harness an attacking minion, <see cref="LaneMinion"/>).
    /// </summary>
    [Check("scripting.bb-teemo-passive-speed-survives-a-minion-hit")]
    public static void PassiveSpeedSurvivesAMinionHit()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        hero.Position = new Position2(hero.Position.X + 40.0, hero.Position.Z);
        match.Advance(0.3);
        TestAssert.Near(305.0 * 1.10, hero.Speed, 1e-6, "the passive speed is on before the hit");
        var minion = LaneMinion(match, new Position2(hero.Position.X + 100.0, hero.Position.Z));
        var health = hero.Health;
        TestAssert.True(Swing(match, minion, hero), "the minion's attack is queued");
        match.Advance(1.0);
        TestAssert.True(HitsOn(match, "Teemo").Any(hit => hit.Kind == "Basic" && hit.Dealt > 0.0) && hero.Health < health, "the minion's hit lands on Teemo");
        TestAssert.True(Buff(hero, "TeemoMoveQuickDebuff") is null, "a minion's hit starts no debuff (the script names heroes and turrets only)");
        TestAssert.Near(305.0 * 1.10, hero.Speed, 1e-6, "the passive speed stays");
    }


    // ------------------------------------------------------------------------------------------------------------
    // Camouflage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-camouflage-stealth-after-two-seconds-still-and-attack-speed-after-the-attack")]
    public static void CamouflageStealthAndAttackSpeed()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        // The arena's wait stood Teemo still long enough to be stealthed: a step ends the stealth, and the two seconds of standing still start again.
        var baseSpeed = hero.AttackSpeedMod;
        hero.Position = new Position2(7150, 7000);
        match.Advance(0.3);
        TestAssert.True(Buff(hero, "CamouflageStealth") is null, "a step ends the stealth");
        TestAssert.Near(baseSpeed + 0.4, hero.AttackSpeedMod, 1e-6, "leaving the stealth gives +0.4 attack speed (CamouflageBuff, IncPercentAttackSpeedMod)");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "CamouflageStealth") is null, "not stealthed after one second still");
        match.Advance(1.6);
        TestAssert.True(Buff(hero, "CamouflageStealth") is not null, "stealthed after two seconds still (CamouflageCheck, Duration 2.0)");
        hero.Position = new Position2(7200, 7000);
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "CamouflageStealth") is null, "a step ends the stealth");
        var foe = match.Foe;
        foe.Position = new Position2(7250, 7000);
        TestAssert.True(Swing(match, hero, foe), "Teemo swings");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "CamouflageBuff") is not null, "the attack speed buff follows the attack that leaves the stealth");
        TestAssert.Near(baseSpeed + 0.4, hero.AttackSpeedMod, 1e-6, "the attack keeps the +0.4 (it does not stack)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Toxic Shot
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-toxic-shot-is-sealed-and-poisons-the-attack")]
    public static void ToxicShotIsSealedAndPoisons()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // UpdateSelfBuffActions adds the aura every second while E is trained (ExecutePeriodically, TimeBetweenExecutions 1.0).
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "ToxicShot") is not null, "the Toxic Shot aura is on once E is trained (UpdateSelfBuffActions)");
        var verdict = match.CanCast(2, foe);
        TestAssert.True(!verdict.Allowed && verdict.Failure == SpellCastFailure.Disabled, "the E slot is sealed by its character script (BBSealSpellSlot)");
        TestAssert.Equal("spell slot is sealed (BBSealSpellSlot)", verdict.Reason, "the refusal names the seal");
        TestAssert.True(Swing(match, hero, foe), "Teemo's attack is queued (the override is the aura's ToxicShotAttack)");
        for (var step = 0; step < 60 && Buff(foe, "ToxicShotParticle") is null; step++) match.Advance(0.05);
        TestAssert.True(Buff(foe, "ToxicShotParticle") is not null, "the applicator puts the poison on the target");
        // The first tick (1.5 x DamagePerTick) lands with the poison; the five one-second ticks that follow are read from HP, with the victim's
        // regeneration added back (the spell damage is not in the hit log). Each tick is 6 raw against 30 resist.
        var regen = foe.HealthRegen;
        var hp = foe.Health;
        match.Advance(5.5);
        TestAssert.Near(5 * Mitigated(6.0, 30.0), hp - foe.Health + regen * 5.5, 0.5,
            "five one-second ticks of 6 magic against 30 resist (ToxicShotParticle lasts 5.1 s)");
        TestAssert.True(Buff(foe, "ToxicShotParticle") is null, "the poison is gone after its duration");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Noxious Trap
    // ------------------------------------------------------------------------------------------------------------

    private static void Mushrooms(Match match, int stacks) =>
        match.Module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Hero, "TeemoMushrooms", (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BbBuffType.BUFF_Aura,
            3, 25000.0, 0.0, false, new BbTable(), stacks, true));

    private static Entity? Trap(Match match) =>
        match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Alive && entity.Name == "TeemoMushroom");

    [Check("scripting.bb-teemo-noxious-trap-costs-a-charge-and-arms-after-two-seconds")]
    public static void TrapCostsACharge()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        match.Foe.Position = new Position2(7900, 7000); // out of the trap's reach: the blast of this check is not the subject
        Mushrooms(match, 3);
        hero.SpellCooldowns[3] = 0.0;
        hero.Mana = hero.MaxMana;
        TestAssert.Equal(3, Stacks(hero, "TeemoMushrooms"), "three stored mushrooms");
        TestAssert.True(match.Cast(3, at: new Position2(7150, 7000)), "the trap is cast at 150 units (its cast range 230)");
        match.Advance(0.1);
        TestAssert.Equal(3, Stacks(hero, "TeemoMushrooms"), "no charge is spent before the cast's quarter second ends");
        match.Advance(0.3);
        TestAssert.Equal(2, Stacks(hero, "TeemoMushrooms"), "one charge spent when the trap is placed (SelfExecute)");
        match.Advance(0.2);
        var trap = Trap(match);
        TestAssert.True(trap is not null, "the trap unit is spawned (its character record: TeemoMushroom)");
        // The placement's own buffs: the invisibility (the spell's script, BUFF_Invisibility, nameless add) and the shared ward (BBSpellBuffAdd).
        TestAssert.True(Buff(trap!, "BantamTrap") is not null && Buff(trap!, "SharedWardBuff") is not null,
            "the trap is invisible (its own buff) and shares its ward; its buffs: " + Names(trap!));
        TestAssert.True(Buff(trap!, "BantamArmor") is null && Buff(trap!, "Stealth") is null, "no armour and no re-stealth before the arming");
        match.Advance(1.9);
        TestAssert.True(Buff(trap!, "BantamArmor") is not null && Buff(trap!, "Stealth") is not null,
            "armed after two seconds of life (LifeTime >= 2): BantamArmor and the stealth are added; its buffs: " + Names(trap!));
        TestAssert.Near(150.0, Buff(trap!, "BantamArmor")!.Payload.Armor, 1e-6, "+150 armour (IncFlatArmorMod)");
    }

    [Check("scripting.bb-teemo-trap-triggers-only-once-armed-and-blasts-by-rank")]
    public static void TrapTriggersOnceArmed()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // The enemy stands at level six like Teemo: the level-difference buff gives a level-one enemy speed the script does not count.
        var foeExperience = ProgressionRules.ExperienceThreshold(6) - foe.Experience;
        if (foeExperience > 0.0) match.Runtime.Combat.AwardHeroProgress(foe, experience: foeExperience);
        Mushrooms(match, 3);
        hero.SpellCooldowns[3] = 0.0;
        hero.Mana = hero.MaxMana;
        foe.Position = new Position2(7150, 7000);
        match.Cast(3, at: new Position2(7150, 7000));
        match.Advance(0.5);
        // An enemy inside the 160-unit range before the arming does not spring it.
        foe.Position = new Position2(7260, 7000);
        match.Advance(0.5);
        TestAssert.True(Trap(match) is not null, "an enemy in range before the arming does not spring the trap");
        foe.Position = new Position2(7270, 7000);
        // The trap was placed a quarter second after the cast and arms two seconds after that: the enemy is in range now, and the trap springs
        // when it arms. The speed is read on the tick before the slow lands, so both reads share the foe's state.
        // The enemy's speed settles about half a second after it arrives (its level-difference buff); the reads start after that.
        match.Advance(0.5);
        double before = 0.0, hp = 0.0, regen = 0.0;
        for (var step = 0; step < 80 && Buff(foe, "Slow") is null; step++)
        {
            for (var pass = 0; pass < 5; pass++) match.Runtime.HeroStats.Refresh(foe, push: false);
            before = foe.Speed;
            hp = foe.Health;
            regen = foe.HealthRegen;
            match.Advance(0.05);
        }
        TestAssert.True(Buff(foe, "Slow") is not null, "the trap springs once armed: the victim is slowed (the Slow buff)");
        TestAssert.True(Trap(match) is null, "the trap kills itself (500 true damage) when it springs");
        for (var pass = 0; pass < 5; pass++) match.Runtime.HeroStats.Refresh(foe, push: false);
        // The script's MoveSpeedMod -0.3 at rank one (IncPercentMovementSpeedMod): the speed it leaves is 0.7 of the speed before it.
        // The same victim with the slow taken off (the runtime's removal): the slowed speed over the unslowed one is the script's 0.7.
        var slowed = foe.Speed;
        match.Module.Buffs!.Remove(foe, "Slow", hero);
        for (var pass = 0; pass < 5; pass++) match.Runtime.HeroStats.Refresh(foe, push: false);
        // The slow is -0.3 of the speed (IncPercentMovementSpeedMod); below 220 the hero soft cap applies (110 + s / 2, HeroStats.ApplyMovementSoftCaps).
        var expected = 0.7 * foe.Speed;
        if (expected < 220.0) expected = 110.0 + expected / 2.0;
        TestAssert.Near(expected, slowed, 0.5,
            $"MoveSpeedMod -0.3 at rank one: {foe.Speed} without the slow, {slowed} with it (0.7 of it, then the soft cap below 220)");
        TestAssert.True(Buff(foe, "BantamTrapTarget") is not null, "the poison debuff is on the victim for 4 s");
        // Four ticks of 50 magic against 30 resist: the first lands with the spring, three follow each second (spell damage is not in the hit log).
        match.Advance(5.0);
        TestAssert.Near(4 * Mitigated(TrapTick[0], foe.SpellBlock), hp - foe.Health + regen * 5.0, 0.5,
            "four ticks of 50 magic against 30 resist (the first is immediate, three follow each second)");
    }

    private sealed class RouteCapture(Func<double> now) : Amberline.Network.Replication.IReplicationTransport
    {
        public readonly List<(double T, Amberline.Network.Protocol.PlaintextPacket Packet)> Packets = [];
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) => Packets.Add((now(), packet));
    }

    // Batch 316 (batch 300's telemetry: 3050 pet-routes for seven traps): the trap runs idle.lua (BantamTrap.luaobj's BBSpawnMinion
    // AiScript), whose only order is OnInit's SetState(AI_HARDIDLE), and it is spawned Stunned and Rooted with no speed, so no route
    // (0xC1) or reveal route reaches any client while it stands.
    [Check("scripting.bb-teemo-standing-trap-sends-no-route")]
    public static void StandingTrapSendsNoRoute()
    {
        using var match = Arena(1);
        var captures = new List<RouteCapture>();
        foreach (var participant in match.Runtime.Session.Participants)
        {
            var capture = new RouteCapture(() => match.Now);
            captures.Add(capture);
            match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver((ulong)participant.ClientId + 1,
                participant.Team, participant.Hero!.NetId, capture, ready: true, view: participant.View));
        }
        match.Foe.Position = new Position2(7900, 7000);
        Mushrooms(match, 3);
        match.Refill();
        TestAssert.True(match.Cast(3, at: new Position2(7150, 7000)), "the trap is cast");
        match.Advance(0.6);
        var trap = Trap(match);
        TestAssert.True(trap is not null, "the trap stands");
        var spawnRoutes = captures.Sum(capture => Routes(capture, trap!.NetId).Count);
        foreach (var capture in captures) capture.Packets.Clear();
        // The enemy walks past outside the trap's 160-unit reach, then everyone stands.
        match.Foe.Position = new Position2(7500, 7000);
        match.Advance(30.0);
        var routes = captures.SelectMany((capture, seat) => Routes(capture, trap!.NetId).Select(route => $"seat {seat} t={route.T:F2} 0x{route.Opcode:X2}"))
            .ToList();
        TestAssert.True(trap!.Alive, "the trap still stands");
        TestAssert.True(routes.Count == 0, $"no route for the standing trap after its spawn ({spawnRoutes} at the spawn); sent: {string.Join(", ", routes.Take(40))}");
    }

    private static List<(double T, byte Opcode)> Routes(RouteCapture capture, uint netId) => capture.Packets
        .Where(entry => entry.Packet.Plaintext.Length >= 5 &&
            entry.Packet.Plaintext.Span[0] is Amberline.Network.Protocol.VisionPackets.WaypointListOpcode or
                Amberline.Network.Protocol.VisionPackets.EnterVisibilityOpcode &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == netId)
        .Select(entry => (entry.T, entry.Packet.Plaintext.Span[0])).ToList();

    [Check("scripting.bb-teemo-mushroom-recharge-is-35-31-27-seconds-by-rank")]
    public static void RechargeByRank()
    {
        foreach (var (rank, seconds) in new[] { (1, 35.0), (2, 31.0), (3, 27.0) })
        {
            using var match = Arena(rank, mushrooms: 3);
            var hero = match.Hero;
            match.Foe.Position = new Position2(7900, 7000);
            TestAssert.True(Buff(hero, "TeemoMushroomCounter") is null, $"rank {rank}: no recharge counter before the first charge is spent: {Names(hero)}");
            hero.SpellCooldowns[3] = 0.0;
            hero.Mana = hero.MaxMana;
            match.Advance(0.1);
            var start = match.Now;
            TestAssert.True(match.Cast(3, at: new Position2(7150, 7000)), $"rank {rank}: the trap is cast");
            match.Advance(0.5);
            TestAssert.True(Buff(hero, "TeemoMushroomCounter") is not null, $"rank {rank}: the recharge counter runs once a charge is spent");
            // The charge comes back when the counter ends (BuffOnDeactivate adds one). MushroomCooldown is SrcValueByLevel (35 / 31 / 27) times
            // CooldownMod, which is 1 + GetPercentCooldownMod (CharScriptTeemo); the time is read on the game clock.
            while (Stacks(hero, "TeemoMushrooms") < 3 && match.Now - start < 80.0) match.Advance(0.1);
            TestAssert.Near(seconds * (1.0 + hero.CooldownMod), match.Now - start, 1.0,
                $"rank {rank}: MushroomCooldown {seconds} s times the cooldown modifier {hero.CooldownMod}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-teemo-death-keeps-the-passives-and-the-mushroom-charges")]
    public static void DeathKeepsThePassives()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        match.Advance(0.5);
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Advance(0.5);
        TestAssert.True(!hero.Alive, "Teemo is dead");
        foreach (var name in new[] { "TeemoMushroomManager", "TeemoMoveQuickPassive", "Camouflage" })
            TestAssert.True(Buff(hero, name) is not null, $"{name} persists through death (PersistsThroughDeath)");
        match.Advance(120.0);
        TestAssert.True(hero.Alive, "Teemo respawns");
        TestAssert.True(Buff(hero, "TeemoMoveQuickPassive") is not null && Buff(hero, "TeemoMushroomManager") is not null, "the passives are still there");
    }
}
