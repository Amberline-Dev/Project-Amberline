using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (batch 304): Gangplank plays from his shipped scripts. Expected numbers are the literals of the shipped scripts
/// (Parley, RemoveScurvy, RaiseMorale, RaiseMoraleTeamBuff, CannonBarrage, CannonBarrageBall, Scurvy, ScurvyStrike, CharScriptGangplank)
/// and the spell inibins. The scenario <c>S-gangplank</c> shows the same facts on a headless match. No engine rule was added for him:
/// the wave 6 items he needs (E01, E04, E06) were on master, so these checks pin what the shipped scripts do through the engine.
/// </summary>
public static class BbGangplankChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    [Check("scripting.bb-gangplank-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Gangplank");
        foreach (var name in new[] { "CharScriptGangplank", "Parley", "RemoveScurvy", "RaiseMorale", "RaiseMoraleTeamBuff", "CannonBarrage", "CannonBarrageBall", "Scurvy", "ScurvyStrike", "PirateScurvy" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Gangplank");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(22, closure.ScriptNames.Count(), "the closure is 22 scripts (CannonBarrageAoE is not reached: no script of his names it)");

        var hero = new Entity { ChampionName = "Gangplank" };
        TestAssert.Equal(["Parley", "RemoveScurvy", "RaiseMorale", "CannonBarrage"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Gangplank.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "Cannon Barrage is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Gangplank")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 3150, Id: 41, RetailBot: false }, "playable in the top lane for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Gangplank"), "listed once");

        // The rank tables the figures rest on, a second time as literals (the shipped scripts' SrcValueByLevel tables).
        TestAssert.Equal([20.0, 45.0, 70.0, 95.0, 120.0], ByLevel("Parley", "TargetExecute", "ParBaseDamage"), "Parley base damage by rank");
        TestAssert.Equal([4.0, 5.0, 6.0, 7.0, 8.0], ByLevel("Parley", "TargetExecute", "GoldGain"), "Parley gold by rank");
        TestAssert.Equal([80.0, 150.0, 220.0, 290.0, 360.0], ByLevel("RemoveScurvy", "SelfExecute", "HealLevel"), "Remove Scurvy heal by rank");
        TestAssert.Equal([75.0, 120.0, 165.0], ByLevel("CannonBarrageBall", "SelfExecute", "DamageAmount"), "a cannonball's damage by rank");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Grog-Soaked Blade (passive): a landed attack slows the target and starts a bleed
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-gangplank-grog-soaked-blade-slows-and-bleeds-what-it-hits")]
    public static void GrogSoakedBladeSlowsAndBleeds()
    {
        TestAssert.Equal(4.0, ByLevel("Scurvy", "BuffOnHitUnit", "DotDamage")[0], "the bleed is 4 a second at level one, per stack");
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7110, 7000);
        match.Advance(0.5);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a basic attack on the Foe");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "ScurvyStrike") is not null, "the hit leaves the ScurvyStrike bleed on the Foe");
        TestAssert.Equal(1, Buff(match.Foe, "ScurvyStrikeParticle")?.Stacks ?? 0, "one slow stack after one hit");
        match.Advance(2.5);
        var bleed = HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Raw - 4.0) < 1e-6).ToList();
        TestAssert.True(bleed.Count >= 1, "a bleed tick of 4 on the Foe, one stack, each second");
        TestAssert.True(Buff(match.Hero, "Scurvy") is not null, "the passive buff is on him");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Parley (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-gangplank-parley-hits-for-base-damage-plus-base-attack-damage")]
    public static void ParleyHitsForTheRankPlusBaseAttackDamage()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Foe.Armor = 0.0;
        match.Advance(0.5);
        var baseAttackDamage = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(0, match.Foe), "Parley on the Foe");
        match.Advance(1.0);
        // The base attack damage of the script's GetBaseAttackDamage leaves out the bonus attack damage, and the passive's 8 is a bonus.
        var expected = 20.0 + baseAttackDamage - 8.0;
        var raws = string.Join(",", HitsOn(match, "Foe").Select(hit => hit.Raw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        var shot = HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Raw - expected) < 1e-6).ToList();
        TestAssert.Equal(1, shot.Count, $"one shot of Parley: rank one is 20 plus the base attack damage ({expected}), raws {raws}");
        // Parley is an attack-type hit, so the passive's on-hit bleed starts too: its first tick (4, one stack) lands in the same second.
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Raw - 4.0) < 1e-6), "the on-hit bleed of the passive follows the shot");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-gangplank-parley-gives-its-gold-only-when-the-shot-kills")]
    public static void ParleyGivesItsGoldOnlyOnAKill()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        var spare = Enemy(match, "Spare", new Position2(7100, 7000));
        var victim = Enemy(match, "Victim", new Position2(7100, 7050));
        victim.Health = 1.0;
        match.Advance(0.5);

        var start = match.Hero.Gold;
        TestAssert.True(match.Cast(0, spare), "Parley on the living minion");
        match.Advance(1.0);
        var living = match.Hero.Gold - start;

        match.Refill();
        var second = match.Hero.Gold;
        TestAssert.True(match.Cast(0, victim), "Parley on the dying minion");
        match.Advance(1.0);
        var killed = match.Hero.Gold - second;
        TestAssert.True(!victim.Alive, "the shot kills the minion");
        TestAssert.Near(4.0, killed - living, 1e-6, "the kill adds the rank-one gold (4) and nothing else");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 376 (owner finding 20): the Parley bonus shows its floating gold text. The kill's own gold travels in the 0x25 (UnitAddGold: killed netId,
    /// hero netId, killed netId, gold) that <c>CombatService.Kill</c> sends after the award; the script gold (BBIncGold) has to send the same packet
    /// with the bonus, or the client shows no "+4" over the kill.
    /// </summary>
    [Check("scripting.bb-gangplank-parley-kill-sends-its-gold-to-the-client")]
    public static void ParleyKillSendsItsGoldToTheClient()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        var wire = Observe(match, Team.Order, match.Hero);
        var victim = Enemy(match, "Victim", new Position2(7100, 7050));
        victim.Health = 1.0;
        match.Advance(0.5);
        TestAssert.True(match.Cast(0, victim), "Parley on the dying minion");
        match.Advance(1.0);
        TestAssert.True(!victim.Alive, "the shot kills the minion");
        var golds = wire.Packets
            .Where(entry => entry.Packet.Plaintext.Span[0] == 0x25 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[5..]) == match.Hero.NetId)
            .Select(entry => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(entry.Packet.Plaintext.Span[13..]))
            .ToList();
        TestAssert.True(golds.Any(gold => Math.Abs(gold - 4.0) < 1e-6),
            "a 0x25 with the rank-one Parley gold (4) reaches the hero's client: " + string.Join(",", golds));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Remove Scurvy (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-gangplank-remove-scurvy-clears-crowd-control-and-heals")]
    public static void RemoveScurvyClearsCrowdControlAndHeals()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Hero.Health = 300.0; // a level-one bar is 576 (the health is clamped to it)
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "PirateScurvy") is not null, "Remove Scurvy is ready, so its marker is on");
        match.Runtime.Buffs.AddBuff(match.Hero, "AcceptanceStun", BuffType.Stun, 10.0, match.Foe.NetId, canMitigateDuration: false);
        match.Runtime.Buffs.AddBuff(match.Hero, "AcceptanceSlow", BuffType.Slow, 10.0, match.Foe.NetId, canMitigateDuration: false);
        var before = match.Hero.Health;
        TestAssert.True(match.Cast(1, match.Hero), "Remove Scurvy on himself");
        match.Advance(0.5); // the cast takes 0.25 s before the script's SelfExecute runs
        TestAssert.True(Buff(match.Hero, "AcceptanceStun") is null, "the stun is removed");
        TestAssert.True(Buff(match.Hero, "AcceptanceSlow") is null, "the slow is removed");
        // Half a second of health regeneration (about 0.5) lands in the same window: the heal itself is 80.
        TestAssert.Near(80.0, match.Hero.Health - before, 1.0, $"the heal is 80 at rank one (no ability power): before {before}, after {match.Hero.Health}, W level {match.Hero.SpellRanks[1]}");
        TestAssert.True(Buff(match.Hero, "PirateScurvy") is null, "the marker is spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 314: the shared cast-check gate (code the client and the server
    /// share) skips the suppressed, cannot-cast, feared, asleep and taunted tests for a spell with <c>CanCastWhileDisabled</c>
    /// (RemoveScurvy.inibin: true, and CannotBeSuppressed true). The control the natives add (the shipped Stun and Fear buffs, whose
    /// payload disables actions) was refused by an extra server gate before.
    /// </summary>
    [Check("scripting.bb-gangplank-remove-scurvy-casts-through-script-control")]
    public static void RemoveScurvyCastsThroughScriptControl()
    {
        foreach (var (name, type, payload) in new (string, BuffType, BuffPayload?)[]
                 {
                     ("Stun", BuffType.Stun, new BuffPayload { ActionDisabled = true }),
                     ("Fear", BuffType.Fear, new BuffPayload { ActionDisabled = true, MoveSpeedMult = -0.4 }),
                     ("Suppression", BuffType.Suppression, null),
                 })
        {
            using var match = new Match("Gangplank");
            match.Learn(0);
            match.Learn(1);
            match.Refill();
            match.Advance(0.5);
            TestAssert.True(ChampionSpellSupport.AddBuff(match.Runtime.Spells, match.Hero, name, type, 10.0, match.Foe.NetId, payload, mitigate: true) is not null,
                $"{name} applied");
            match.Advance(0.1);
            TestAssert.False(match.CanCast(0, match.Foe).Allowed, $"{name}: Parley (no CanCastWhileDisabled) is refused");
            var verdict = match.CanCast(1);
            TestAssert.True(verdict.Allowed, $"{name}: Remove Scurvy may be cast ({verdict.Reason})");
            TestAssert.Equal(0x2u, match.Runtime.HeroStats.Snapshot(match.Hero).CanCastBits1 & 0x2u, $"{name}: the HUD keeps Remove Scurvy castable");
            TestAssert.True(match.Cast(1, match.Hero), $"{name}: Remove Scurvy on himself");
            match.Advance(0.5);
            TestAssert.True(Buff(match.Hero, name) is null, $"{name} is removed by the cast");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        }
    }

    /// <summary>
    /// Batch 320 item 0: a hero's stun stops him and cancels his pending cast when it starts. A buff added while the stun lasts (here a slow)
    /// used to cancel the pending cast again, so a Remove Scurvy queued under the stun (batch 314: it passes OnCanUseSpell; SpellSystem queues a
    /// cast behind a displacement that is still moving) was dropped. The queued request is set the way SpellSystem leaves it.
    /// </summary>
    [Check("scripting.bb-gangplank-remove-scurvy-queued-under-a-stun-survives-a-later-buff-add")]
    public static void RemoveScurvyQueuedUnderAStunSurvivesALaterBuffAdd()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(ChampionSpellSupport.AddBuff(match.Runtime.Spells, match.Hero, "Stun", BuffType.Stun, 10.0, match.Foe.NetId,
            new BuffPayload { ActionDisabled = true }, mitigate: true) is not null, "the stun lands");
        var here = new Position3(match.Hero.Position.X, 0.0, match.Hero.Position.Z);
        match.Hero.PendingCast = new PendingCastState(new SpellCastRequest(1, false, here, here, match.Hero.NetId), match.Now + 5.0);
        TestAssert.True(ChampionSpellSupport.AddBuff(match.Runtime.Spells, match.Hero, "AcceptanceSlow", BuffType.Slow, 5.0, match.Foe.NetId,
            new BuffPayload { MoveSpeedMult = -0.2 }, mitigate: true) is not null, "another buff lands while the stun lasts");
        TestAssert.True(match.Hero.PendingCast is not null, "the queued Remove Scurvy is kept: the stun started before it");
        match.Hero.PendingCast = null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Raise Morale (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-gangplank-raise-morale-buffs-the-caster-and-the-allies-then-leaves-the-passive")]
    public static void RaiseMoraleBuffsTheTeamAndLeavesThePassive()
    {
        TestAssert.Equal([14.0, 22.0, 30.0, 38.0, 46.0], ByLevel("RaiseMorale", "SelfExecute", "AttackDmgMod"), "the caster's attack damage by rank");
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "RaiseMorale") is not null, "learnt: the passive RaiseMorale is on him");
        var passive = match.Hero.AttackDamage;
        var allyBase = match.Ally.AttackDamage;
        TestAssert.True(match.Cast(2, match.Hero), "Raise Morale on himself");
        match.Advance(0.5); // the cast takes 0.25 s before the script's SelfExecute runs
        TestAssert.True(Buff(match.Hero, "RaiseMoraleTeamBuff") is not null, "the caster has the team buff; buffs: " + Names(match.Hero));
        TestAssert.True(Buff(match.Hero, "RaiseMorale") is null, "the passive is taken away while the team buff lasts");
        TestAssert.Near(6.0, match.Hero.AttackDamage - passive, 1e-6, "14 from the team buff in place of the passive's 8");
        TestAssert.True(Buff(match.Ally, "RaiseMoraleTeamBuff") is not null, "the ally within range has the team buff");
        TestAssert.Near(7.0, match.Ally.AttackDamage - allyBase, 1e-6, "the ally gets 7 at rank one");
        TestAssert.True(Buff(match.Foe, "RaiseMoraleTeamBuff") is null, "the enemy does not");
        match.Advance(7.5);
        TestAssert.True(Buff(match.Hero, "RaiseMoraleTeamBuff") is null, "the team buff ended after seven seconds");
        TestAssert.True(Buff(match.Hero, "RaiseMorale") is not null, "and the passive is back, from the team buff's deactivation");
        TestAssert.Near(passive, match.Hero.AttackDamage, 1e-6, "the caster's attack damage is the passive's 8");
        TestAssert.Near(allyBase, match.Ally.AttackDamage, 1e-6, "the ally's bonus ended");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Cannon Barrage (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-gangplank-cannon-barrage-drops-cannonballs-and-slows-the-area")]
    public static void CannonBarrageDropsCannonballsAndSlows()
    {
        using var match = new Match("Gangplank");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        TestAssert.True(match.Cast(3, match.Foe), "Cannon Barrage at the Foe");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the area slows the Foe within its radius");
        match.Advance(8.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count > 0, "cannonballs land on the Foe");
        TestAssert.True(hits.All(hit => Math.Abs(hit.Raw - 75.0) < 1e-6), "every cannonball deals 75 at rank one (the script's DamageAmount, plus 0.2 of ability power)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
