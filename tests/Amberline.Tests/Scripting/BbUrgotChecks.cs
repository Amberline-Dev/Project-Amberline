using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 2 (batch 285, 26 October 2026): Urgot plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped scripts and spell inibins (rank one unless stated); the scenario <c>S-Urgot</c> shows the same facts on a headless match.
/// Zaun-Touched Bolt Augmenter marks the unit his attack hits (2.5 seconds, 15 percent less non-true damage); Acid Hunter's line missile hits for
/// 10 plus 0.85 of his total attack damage, and its homing child takes a unit that carries Noxian Corrosive Charge's debuff; Terror Capacitor's
/// shield is 80 plus 0.8 of ability power for seven seconds and its hits slow by 20 percent; Noxian Corrosive Charge hurts for 75 plus 0.6 of his bonus
/// attack damage in five one-second ticks and cuts armour by 12 percent; Hyper-Kinetic Position Reverser channels for a second, suppresses its target,
/// swaps places with it, gives him 80 armour and magic resistance and slows the target by 40 percent.
/// </summary>
public static class BbUrgotChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Urgot level one with Q, W and E learnt and the ultimate learnt at level six, the enemy 500 away, the second enemy 300 beside it.</summary>
    private static Match Arena(int level = 1)
    {
        var match = new Match("Urgot", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7500, 7000);
        match.FarFoe.Position = new Position2(7500, 7300);
        match.Ally.Position = new Position2(6500, 6500);
        foreach (var unit in new[] { match.Foe, match.FarFoe, match.Ally }) unit.SpellBlock = 0.0;
        match.Hero.Level = level;
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Refill();
        return match;
    }

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>Damage dealt to a named unit in a window, of one damage source kind (null: every kind).</summary>
    private static double HitTotal(Match match, string victim, double from, double to, string? kind = null) =>
        HitsOn(match, victim).Where(hit => hit.At >= from && hit.At <= to && (kind is null || hit.Kind == kind)).Sum(hit => hit.Dealt);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-urgot-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Urgot");
        foreach (var name in new[] { "CharScriptUrgot", "UrgotHeatseekingMissile", "UrgotHeatseekingLineMissile", "UrgotHeatseekingHomeMissile",
                     "UrgotTerrorCapacitorActive2", "UrgotPlasmaGrenade", "UrgotPlasmaGrenadeBoom", "UrgotSwap2", "UrgotSwapExecute", "UrgotSwapMissile",
                     "UrgotSwapMissile2", "UrgotSwapTarget", "UrgotSwapDef", "UrgotEntropyPassive", "UrgotCorrosiveDebuff", "UrgotSlow", "Suppression" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Urgot");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(32, closure.ScriptNames.Count(), "the closure is 32 scripts (audit bb-coverage)");

        var hero = new Entity { ChampionName = "Urgot" };
        TestAssert.Equal(["UrgotHeatseekingMissile", "UrgotTerrorCapacitorActive2", "UrgotPlasmaGrenade", "UrgotSwap2"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Urgot.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Urgot")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 3150, Id: 6, RetailBot: false },
            "playable as a top laner for 3150 IP, not a retail bot (the generic template)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Urgot"), "listed once");
    }

    [Check("scripting.bb-urgot-entropy-passive-marks-the-hit-target")]
    public static void PassiveMarksTheHitTargetForTwoAndAHalfSeconds()
    {
        using var match = Arena();
        var start = match.Now;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Urgot orders an attack on the enemy");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "UrgotEntropyPassive") is not null, "the enemy carries Degenerative Bolt after his hit");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "UrgotEntropyPassive") is null, "and loses it 2.5 seconds after the hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        TestAssert.True(match.Now > start, "the clock moved");
    }

    [Check("scripting.bb-urgot-acid-hunter-line-hits-for-ten-plus-bonus-attack-damage")]
    public static void AcidHunterLineHitsForTenAndEightyFivePercentOfBonusDamage()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        var attackDamage = match.Hero.AttackDamage;
        match.Foe.Position = new Position2(7600, 7000);
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Acid Hunter is cast at the enemy");
        TestAssert.Near(40.0, mana - match.Hero.Mana, 1e-6, "Acid Hunter costs 40 mana at rank one");
        TestAssert.Near(2.0, match.Hero.SpellCooldowns[0], 0.05, "and has a two-second cooldown (the inibin; AutoCooldownByLevel is not read)");
        var from = match.Now;
        var armour = match.Foe.Armor;
        match.Advance(1.5);
        var dealt = HitTotal(match, "Foe", from, from + 1.4);
        TestAssert.Near(Mitigated(10.0 + 0.85 * attackDamage, armour), dealt, 0.05,
            "10 plus 0.85 of his total attack damage, physical, mitigated by the enemy's armour");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-acid-hunter-homes-on-the-corroded-unit")]
    public static void AcidHunterHomesOnTheUnitWithNoxianCorrosiveCharge()
    {
        using var match = Arena();
        var corroded = match.Foe;
        var other = match.FarFoe;
        match.Hero.Mana = match.Hero.MaxMana;
        TestAssert.True(match.Cast(2, at: corroded.Position), "Noxian Corrosive Charge lands on the first enemy");
        // The charge's boom is a missile of its own: the debuff lands about 0.9 seconds after the cast (its tick follows a second later).
        match.Advance(1.2);
        TestAssert.True(Buff(corroded, "UrgotCorrosiveDebuff") is not null, "the first enemy carries the corrosive debuff");
        var from = match.Now;
        TestAssert.True(match.Cast(0, at: other.Position), "Acid Hunter is aimed at the second enemy, 300 from the corroded one");
        match.Advance(1.0);
        TestAssert.True(HitTotal(match, "Foe", from, from + 0.9, "Spell") > 0.0, "the missile turns to the corroded enemy");
        TestAssert.Near(0.0, HitTotal(match, "FarEnemy", from, from + 0.9, "Spell"), 1e-9, "and the other enemy takes nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-terror-capacitor-shields-and-slows-his-hits")]
    public static void TerrorCapacitorShieldsAndSlows()
    {
        using var match = Arena();
        match.Hero.Armor = 0.0;
        match.Hero.SpellBlock = 0.0;
        TestAssert.True(match.Cast(1, match.Hero), "Terror Capacitor is cast on himself");
        TestAssert.True(Buff(match.Hero, "UrgotTerrorCapacitorActive2") is not null, "he carries the capacitor");
        match.Advance(0.2);
        var before = match.Hero.Health;
        match.Runtime.Combat.QueueScriptedHit(match.Foe, match.Hero, 200.0, DamageType.Physical, DamageSourceKind.Spell, 0.0);
        match.Advance(0.5);
        TestAssert.Near(120.0, before - match.Hero.Health, 1.0, "the shield (80 plus 0.8 of ability power, 0 here) takes 80 of a 200 hit (health regeneration in the half second is the margin)");
        // The shield is spent by that hit, and the capacitor ends with it (BuffOnPreDamage removes the buff when the shield is gone).
        TestAssert.True(Buff(match.Hero, "UrgotTerrorCapacitorActive2") is null, "the spent shield ends the capacitor");
        match.Hero.SpellCooldowns[1] = 0.0;
        TestAssert.True(match.Cast(1, match.Hero), "Terror Capacitor is cast again for the slow");
        match.Foe.Position = new Position2(7300, 7000);
        var speed = match.Foe.Speed;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "he attacks");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "UrgotSlow") is not null, "his hit slows the enemy");
        TestAssert.Near(0.8 * speed, match.Foe.Speed, 0.5, "by 20 percent at rank one");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-noxian-charge-ticks-for-75-plus-bonus-damage-over-five-seconds")]
    public static void NoxianChargeTicksOverFiveSeconds()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Noxian Corrosive Charge is cast on the enemy");
        var from = match.Now;
        match.Advance(1.2);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; regeneration allowed.
        TestAssert.Near(50.0, mana - match.Hero.Mana, 2.5, "it costs 50 mana at rank one");
        TestAssert.Near(11.0, match.Hero.SpellCooldowns[2] + 1.2 - match.CastFrameOf(2), 0.05, "and has an eleven-second cooldown");
        TestAssert.True(Buff(match.Foe, "UrgotCorrosiveDebuff") is not null, "the enemy carries the debuff");
        var debuffed = match.Foe.Armor;
        match.Advance(5.5);
        var ticks = HitsOn(match, "Foe").Where(hit => hit.At >= from && hit.Kind == "SpellAoe").ToList();
        TestAssert.Equal(5, ticks.Count, "five one-second ticks");
        // 75 in all (0.6 of his FLAT bonus attack damage is none on a bare Urgot), 15 a tick, each mitigated by the armour the debuff leaves.
        TestAssert.Near(Mitigated(75.0, debuffed), HitTotal(match, "Foe", from, from + 6.7), 0.2, "the charge's 75 in the ticks, mitigated by the debuffed armour");
        TestAssert.True(Buff(match.Foe, "UrgotCorrosiveDebuff") is null, "the debuff is gone after five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-noxian-charge-cuts-armour-by-twelve-percent")]
    public static void NoxianChargeCutsArmour()
    {
        using var match = Arena();
        var armour = match.Foe.Armor;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Noxian Corrosive Charge is cast on the enemy");
        match.Advance(1.2);
        TestAssert.Near(armour * 0.88, match.Foe.Armor, 0.05, "the debuff cuts armour by 12 percent at rank one");
        match.Advance(5.5);
        TestAssert.Near(armour, match.Foe.Armor, 0.05, "and the armour comes back when it ends");
    }

    [Check("scripting.bb-urgot-position-reverser-channels-then-swaps-places")]
    public static void PositionReverserChannelsThenSwaps()
    {
        using var match = Arena(level: 6);
        var urgotStart = match.Hero.Position;
        var foeStart = match.Foe.Position;
        var armour = match.Hero.Armor;
        var magicResist = match.Hero.SpellBlock;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, match.Foe), "Hyper-Kinetic Position Reverser is cast on the enemy hero");
        TestAssert.Near(120.0, mana - match.Hero.Mana, 1e-6, "it costs 120 mana at rank one");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "Suppression") is not null && Buff(match.Foe, "UrgotSwapTarget") is not null,
            "the enemy is suppressed and marked during the channel");
        TestAssert.True(Dist(match.Hero.Position, urgotStart) < 5.0, "Urgot cannot move during the channel");
        match.Advance(1.5);
        // The two stand where the other stood. Batch 728: the teleport has no actor test (the client's own BBTeleportToPosition body), so each
        // lands on the other's point, on the two-unit lattice; the server's old backoff from the other body left each about 50 units short.
        TestAssert.True(Dist(match.Hero.Position, foeStart) < 3.0 && Dist(match.Foe.Position, urgotStart) < 3.0,
            "the two swap places");
        TestAssert.Near(armour + 80.0, match.Hero.Armor, 0.05, "he gains 80 armour (the rank-one defence)");
        TestAssert.Near(magicResist + 80.0, match.Hero.SpellBlock, 0.05, "and 80 magic resistance");
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the enemy is slowed after the swap");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 582 (owner finding 121; the owner's retail look: the arrows point from one champion to the other, green from Urgot, red from the enemy):
    /// the arrows are the swap missiles <c>UrgotSwapMissile</c> (SwapArrow_green) and <c>UrgotSwapMissile2</c> (SwapArrow_red), LineWidth 1200 and range 800,
    /// that the <c>UrgotSwapMissile</c> buff casts every 0.1 s and that their TargetExecute destroys on the marked champion. With the width's round end
    /// every arrow met that champion on its first step and its 0x5D left in the flush of its 0x41; now each one flies most of the 700 units first.
    /// </summary>
    [Check("scripting.bb-urgot-position-reverser-arrows-fly-to-the-other-champion")]
    public static void PositionReverserArrowsFlyToTheOtherChampion()
    {
        using var match = Arena(level: 6);
        match.Foe.Position = new Position2(7700, 7000);
        var caster = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(3, match.Foe), "Hyper-Kinetic Position Reverser is cast on the enemy hero 700 away");
        match.Advance(2.0);
        match.Runtime.Delivery.Flush();
        var arrows = new[] { SpellSystem.ProtocolHash("UrgotSwapMissile"), SpellSystem.ProtocolHash("UrgotSwapMissile2") };
        var launched = caster.Packets.Where(item => item.Packet.Plaintext.Span[0] == 0x41 && MissileOf(item.Packet) is { } named && arrows.Contains(named.Spell))
            .GroupBy(item => MissileOf(item.Packet)!.Value.Missile).ToDictionary(group => group.Key, group => (group.First().At, MissileOf(group.First().Packet)!.Value.Spell));
        TestAssert.True(launched.Values.Count(arrow => arrow.Spell == arrows[0]) >= 3 && launched.Values.Count(arrow => arrow.Spell == arrows[1]) >= 3,
            $"green and red arrows reach the caster's client ({launched.Count} launches)");
        var destroyed = caster.Packets.Where(item => item.Packet.Plaintext.Span[0] == 0x5D)
            .ToDictionary(item => BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]), item => item.At);
        // The arrows of the first 0.4 s, which land before the swap (about 1.07 s) moves the champions: each one reaches the other champion's body first
        // (about 635 units at 1000 per second; the first measured 0.633 s), so its 0x5D is the hit's and not its launch's.
        var early = launched.Where(pair => pair.Value.At <= 0.4).ToArray();
        TestAssert.True(early.Length >= 8, $"both colours fly in the first 0.4 s ({early.Length} arrows)");
        foreach (var (missile, launch) in early)
        {
            TestAssert.True(destroyed.TryGetValue(missile, out var end), $"arrow {missile} ends inside the window");
            TestAssert.True(end - launch.At is >= 0.55 and <= 0.7, $"arrow {missile} flies {end - launch.At:0.###} s to the other champion before its 0x5D");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-entropy-passive-cuts-the-damage-its-target-deals")]
    public static void PassiveCutsTheDamageItsTargetDeals()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Urgot's attack is queued");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "UrgotEntropyPassive") is not null, "the enemy is marked by the hit");
        var from = match.Now;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero), "the marked enemy swings at him");
        match.Advance(1.0);
        var marked = HitTotal(match, "Urgot", from, from + 1.0);
        match.Advance(3.0);
        TestAssert.True(Buff(match.Foe, "UrgotEntropyPassive") is null, "the mark ends 2.5 seconds after the hit");
        from = match.Now;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero), "the unmarked enemy swings again");
        match.Advance(1.0);
        var unmarked = HitTotal(match, "Urgot", from, from + 1.0);
        TestAssert.True(unmarked > 0.0 && marked > 0.0, "both swings land");
        TestAssert.Near(0.85, marked / unmarked, 0.01, "the marked enemy's damage is 15 percent less (non-true damage)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-urgot-position-reverser-refuses-a-minion-by-its-flags")]
    public static void PositionReverserRefusesAMinion()
    {
        // SpellData*Flags 0x10404 names AffectHeroes and AffectEnemies only, so a minion is refused at the cast and the script's own
        // non-hero branch (ManaRefund 120, a five-second cooldown) cannot run in play.
        using var match = Arena(level: 6);
        var minion = Enemy(match, "Minion", new Position2(7300, 7000));
        var mana = match.Hero.Mana;
        TestAssert.True(!match.Cast(3, minion), "a minion is not a legal target of the reverser");
        TestAssert.Near(mana, match.Hero.Mana, 1e-6, "and nothing is spent");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 1e-6, "and no cooldown starts");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}

