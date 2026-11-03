using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 2 (batch 290, 26 October 2026): Kennen plays from his shipped scripts in a production <c>MatchRuntime</c>. The expected numbers are the
/// literals of his shipped scripts and spell inibins (the rank-one figures are read from the inibin dumps); the probes run on the <see cref="Match"/> harness
/// (Summoner's Rift, Kennen against Ezreal 300 away, Ezreal's 30 magic resistance). The scenario <c>S-kennen</c> shows the same mechanics on a headless match.
/// </summary>
public static class BbKennenChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static int Stacks(Entity unit, string name) => Buff(unit, name)?.Stacks ?? 0;

    private static double Cooldown(Match match, int slot) => match.Hero.SpellCooldowns[slot];

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks, shipped numbers
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Kennen");
        foreach (var name in new[]
                 {
                     "CharScriptKennen", "KennenShurikenHurlMissile1", "KennenBringTheLight", "KennenLightningRush", "KennenShurikenStorm",
                     "KennenShurikenHurl1", "KennenMegaProc", "KennenMarkofStorm", "KennenDoubleStrikeProc", "KennenDoubleStrikeLive",
                     "KennenLightningRushBuff", "KennenLRCancel", "KennenMoSDiminish",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Kennen");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(31, closure.ScriptNames.Count(), "the closure is 31 scripts (the character script, four spells, the extra spells, the passive's marks and procs, the system buffs)");
        // The passive's data name ("KennenMarkOfStorm.lua") names no file: the mark is the buff the spells add (KennenMarkofStorm), as in Zilean's Heightened Learning.
        TestAssert.Equal(["KennenMarkOfStorm.lua"], closure.Dangling.Select(item => item.Name).ToArray(), "the one dangling name is the passive's data name");

        var hero = new Entity { ChampionName = "Kennen" };
        TestAssert.Equal(["KennenShurikenHurlMissile1", "KennenBringTheLight", "KennenLightningRush", "KennenShurikenStorm"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Kennen.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the storm of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Kennen")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 6300, Id: 85, RetailBot: false }, "playable as a top laner for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Kennen"), "listed once");

        // The rank-one tables the figures rest on, as literals of the shipped scripts (the inibins give the same numbers in SpellData*Effect1 .. 3).
        TestAssert.Equal([75.0, 115.0, 155.0, 195.0, 235.0], ByLevel("KennenShurikenHurlMissile1", "TargetExecute", "ProperDamage"), "Thundering Shuriken damage by rank");
        TestAssert.Equal([65.0, 95.0, 125.0, 155.0, 185.0], ByLevel("KennenBringTheLight", "SelfExecute", "BaseDamage"), "Electrical Surge damage by rank");
        TestAssert.Equal([85.0, 125.0, 165.0, 205.0, 245.0], ByLevel("KennenLightningRush", "SelfExecute", "RushDamage"), "Lightning Rush damage by rank");
        TestAssert.Equal([80.0, 145.0, 210.0], ByLevel("KennenShurikenStorm", "SelfExecute", "BonusDamage"), "Slicing Maelstrom damage by rank");
        TestAssert.Equal([0.4, 0.5, 0.6, 0.7, 0.8], ByLevel("KennenMegaProc", "TargetExecute", "DamageMod"), "the empowered attack's magic share by rank");
        TestAssert.Equal([10.0, 20.0, 30.0, 40.0, 50.0], ByLevel("KennenLightningRush", "OnBuffActivate", "DefenseBonus"), "Lightning Rush armour and magic resistance by rank");
        TestAssert.Equal([10.0, 9.0, 8.0, 7.0, 6.0], ByLevel("KennenLightningRush", "OnBuffDeactivate", "SpellCD"), "Lightning Rush cooldown by rank");

        var q = GameData.Default.Spell("KennenShurikenHurlMissile1", 5);
        TestAssert.Equal([7.0, 6.0, 5.0, 4.0, 3.0], q.Cooldowns.ToArray(), "Thundering Shuriken cooldowns from the inibin");
        TestAssert.Equal([65.0, 60.0, 55.0, 50.0, 45.0], q.ManaCosts.ToArray(), "Thundering Shuriken costs from the inibin");
        TestAssert.Equal(1050.0, q.CastRange, "Thundering Shuriken range");
        TestAssert.Equal(14.0, GameData.Default.Spell("KennenBringTheLight", 5).Cooldowns[0], "Electrical Surge cooldown at rank one");
        TestAssert.Equal(10.0, GameData.Default.Spell("KennenLightningRush", 5).Cooldowns[0], "Lightning Rush cooldown at rank one");
        TestAssert.Equal(120.0, GameData.Default.Spell("KennenShurikenStorm", 3).Cooldowns[0], "Slicing Maelstrom cooldown");
        TestAssert.Equal(100.0, GameData.Default.Spell("KennenLightningRush", 5).ManaCosts[0], "Lightning Rush costs 100 energy at rank one");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Thundering Shuriken and the mark
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-thundering-shuriken-hits-the-line-and-marks")]
    public static void ShurikenHitsAndMarks()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        var mana = match.Hero.Mana;
        var castAt = match.Now;
        TestAssert.True(match.Cast(0, match.Foe), "Thundering Shuriken casts at the enemy");
        TestAssert.Near(0.0, mana - match.Hero.Mana, 0.01, "nothing at the press");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; one step of energy regeneration allowed.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(65.0, mana - match.Hero.Mana, 0.5, "65 energy at rank one, paid at the cast frame");
        match.Advance(1.2 - match.CastFrameOf(0));
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit on the line");
        // 75 magic at rank one with no ability power; Ezreal's 30 magic resistance.
        TestAssert.Near(Mitigated(75.0, 30.0), hits[0].Dealt, 0.05, "75 magic after 30 resistance");
        // The cast takes 0.175 s and the missile flies at 1700 (the target is 300 away: about 0.35 s after the press).
        TestAssert.Near(0.175 + 300.0 / 1700.0, hits[0].At - castAt, 0.05, "the throw lands 0.175 s after the press plus the flight");
        TestAssert.Near(7.0, Cooldown(match, 0) + 1.2 - match.CastFrameOf(0), 0.1, "cooldown 7 s at rank one, from the cast frame");
        TestAssert.Equal(1, Stacks(match.Foe, "KennenMarkofStorm"), "the hit leaves one mark");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    /// <summary>
    /// Batch 591 (owner decision, 1 November 2026): a line missile's hit reaches the client as LineMissileHitList (0x28, the missile's net ID,
    /// the count, the unit) before the 0x5D its script sends at the same hit. The client line has no collision; the packet's handler runs
    /// the spell's hit pass on the unit and skips a missile that is over, so the order matters. Kennen's Thundering Shuriken on a hero and Ezreal's
    /// Mystic Shot on a hero, each seen by the caster's client and the enemy's.
    /// </summary>
    [Check("scripting.bb-line-missile-hit-list-reaches-the-client-before-the-destroy")]
    public static void LineMissileHitListReachesTheClientBeforeTheDestroy()
    {
        foreach (var (champion, spell) in new[] { ("Kennen", "KennenShurikenHurlMissile1"), ("Ezreal", "EzrealMysticShotMissile") })
        {
            using var match = new Match(champion, "Annie", "Ezreal", Team.Order);
            match.Learn(1);
            match.Refill();
            match.Foe.Position = new Position2(7400, 7000);
            var caster = Observe(match, Team.Order, match.Hero);
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            TestAssert.True(match.Cast(0, at: match.Foe.Position), $"{champion} Q at the enemy 400 away");
            match.Advance(1.2);
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(1, HitsOn(match, "Foe").Count, $"{champion} Q hits the enemy once");
            var hash = Amberline.Simulation.Spells.SpellSystem.ProtocolHash(spell);
            foreach (var (name, capture) in new[] { ("caster", caster), ("enemy", enemy) })
            {
                var packets = capture.Packets.Select(item => item.Packet).ToList();
                var missile = packets.Select(MissileOf).First(named => named?.Spell == hash)!.Value.Missile;
                var lists = packets.Select((packet, index) => (packet, index)).Where(item => item.packet.Plaintext.Span[0] == 0x28).ToArray();
                TestAssert.Equal(1, lists.Length, $"{champion}: the {name}'s client gets one hit list");
                var list = lists[0].packet.Plaintext.ToArray();
                TestAssert.Equal(11, list.Length, $"{champion}: 7 bytes and one unit");
                TestAssert.Equal(missile, BitConverter.ToUInt32(list, 1), $"{champion}: in the name of the missile the {name}'s client holds");
                TestAssert.Equal((short)1, BitConverter.ToInt16(list, 5), $"{champion}: one unit");
                TestAssert.Equal(match.Foe.NetId, BitConverter.ToUInt32(list, 7), $"{champion}: the enemy it hit");
                var destroy = packets.FindIndex(packet => packet.Plaintext.Span[0] == 0x5D && BitConverter.ToUInt32(packet.Plaintext.ToArray(), 1) == missile);
                TestAssert.True(destroy > lists[0].index, $"{champion}: the {name}'s hit list comes before the missile's 0x5D ({lists[0].index} < {destroy})");
            }
        }
    }

    [Check("scripting.bb-kennen-thundering-shuriken-scales-with-ability-power")]
    public static void ShurikenScalesWithAbilityPower()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.GiveAbilityPower();
        match.Refill();
        var ap = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(0, match.Foe), "casts");
        match.Advance(1.2);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        // 75 + 0.75 x ability power (the shipped Effect1 ratio is the spell's SpellData*Coefficient, 0.75).
        TestAssert.Near(Mitigated(75.0 + 0.75 * ap, 30.0), hits[0].Dealt, 0.05, "75 plus 0.75 ability power, after 30 resistance");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-kennen-third-mark-stuns-and-the-target-is-then-immune")]
    public static void ThirdMarkStunsAndImmunityFollows()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        var thirdPress = 0.0;
        for (var throws = 0; throws < 3; throws++)
        {
            match.Refill();
            if (throws == 2) thirdPress = match.Now;
            TestAssert.True(match.Cast(0, match.Foe), $"throw {throws + 1} casts");
            match.Advance(1.2);
        }
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "three hits, one per throw");
        TestAssert.True(hits.All(hit => Math.Abs(hit.Dealt - Mitigated(75.0, 30.0)) < 0.05), "each throw deals the same damage");
        // The third mark detonates: the target is stunned for 1.25 s, takes no mark, and is immune to the stun of a second detonation for 7 s.
        // The third throw landed 0.35 s after its press (cast 0.175 s, flight 300 at 1700), and the press was 1.2 s before this read.
        TestAssert.True(Buff(match.Foe, "Stun") is not null, "stunned by the third mark");
        var detonation = thirdPress + 0.175 + 300.0 / 1700.0;
        TestAssert.Near(detonation + 1.25 - match.Now, Buff(match.Foe, "Stun")!.ExpiresAt, 0.1, "a 1.25 second stun from the detonation (within a tick of the stun clock)");
        TestAssert.True(Buff(match.Foe, "KennenMoSDiminish") is not null, "the hero carries the immunity");
        TestAssert.Equal(0, Stacks(match.Foe, "KennenMarkofStorm"), "the detonation removes every mark");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "Stun") is null, "the stun is over after 2 seconds");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Electrical Surge
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-electrical-surge-needs-a-mark-and-hits-marked-units")]
    public static void SurgeNeedsAMarkAndHitsMarkedUnits()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        TestAssert.False(match.CanCast(1, match.Hero).Allowed, "no mark within 800: the active is refused");
        TestAssert.True(match.Cast(0, match.Foe), "Thundering Shuriken marks the target");
        match.Advance(1.2);
        match.Refill();
        TestAssert.True(match.CanCast(1, match.Hero).Allowed, "a mark within 800 opens the active");
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(1, match.Hero), "Electrical Surge casts");
        match.Advance(1.2);
        var hits = HitsOn(match, "Foe");
        var surge = hits.Where(hit => hit.Kind == "SpellAoe").ToArray();
        TestAssert.Equal(1, surge.Length, "one Electrical Surge hit on the marked target");
        TestAssert.Near(Mitigated(65.0, 30.0), surge[0].Dealt, 0.05, "65 magic at rank one after 30 resistance");
        TestAssert.Equal(2, Stacks(match.Foe, "KennenMarkofStorm"), "the surge marks it again (renewed, one more mark)");
        TestAssert.Equal("", Failures(match), "no script failure");
        TestAssert.True(before > match.Foe.Health, "the target lost health");
    }

    [Check("scripting.bb-kennen-electrical-surge-passive-empowers-the-fifth-attack")]
    public static void PassiveFifthAttackIsEmpowered()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        // The proc is granted by the rank-up of the surge (CharOnLevelUpSpell); Learn alone does not run that hook.
        match.Hero.Level = 6;
        match.Hero.SpellRanks[1] = 0;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 1), "the surge is learnt at level six");
        match.Hero.Level = 1;
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Learn(1);
        match.Refill();
        TestAssert.True(Buff(match.Hero, "KennenDoubleStrikeProc") is not null, "the passive is on");
        for (var attack = 1; attack <= 4; attack++)
        {
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), $"attack {attack} is made");
            match.Advance(1.7);
            match.Foe.Health = match.Foe.MaxHealth;
        }
        TestAssert.True(Buff(match.Hero, "KennenDoubleStrikeLive") is not null, "the fourth hit readies the empowered attack");
        TestAssert.Equal(0, match.Log.Hits.Count(line => line.Contains("SpellAoe", StringComparison.Ordinal)), "no magic share before the fifth attack");
        match.Log.Hits.Clear();
        var ad = match.Hero.AttackDamage;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the fifth attack is made");
        match.Advance(1.7);
        var hits = HitsOn(match, "Foe");
        var basic = hits.Single(hit => hit.Kind == "Basic");
        var magic = hits.Single(hit => hit.Kind == "SpellAoe");
        TestAssert.Near(ad, basic.Raw, 0.01, "the physical part is the attack damage");
        TestAssert.Near(0.4 * ad, magic.Raw, 0.05, "the magic part is 0.4 of the attack damage at rank one");
        TestAssert.True(Buff(match.Hero, "KennenDoubleStrikeLive") is null, "the empowered attack is spent");
        TestAssert.Equal(1, Stacks(match.Foe, "KennenMarkofStorm"), "the empowered attack marks the target");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Lightning Rush
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-lightning-rush-speeds-hits-once-and-cancels")]
    public static void RushSpeedsHitsOnceAndCancels()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Foe.Position = new Position2(7120, 7000);
        match.Refill();
        var armor = match.Hero.Armor;
        var magicResist = match.Hero.SpellBlock;
        var speed = match.Hero.Speed;
        TestAssert.True(match.Cast(2, match.Hero), "Lightning Rush casts");
        match.Advance(0.5);
        // 310 doubled is 620, which the movement soft cap of the client brings to 540 (475 + half of the excess over 490).
        TestAssert.Near(540.0, match.Hero.Speed, 0.5, "movement speed 540 under Lightning Rush");
        TestAssert.Near(speed, 310.0, 0.01, "310 before the rush");
        TestAssert.Near(10.0, match.Hero.Armor - armor, 0.01, "10 bonus armour at rank one");
        TestAssert.Near(10.0, match.Hero.SpellBlock - magicResist, 0.01, "10 bonus magic resistance at rank one");
        TestAssert.True(Buff(match.Hero, "KennenLightningRush") is not null, "the rush is on");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one rush tick on the unit beside the path");
        TestAssert.Near(Mitigated(85.0, 30.0), hits[0].Dealt, 0.05, "85 magic at rank one after 30 resistance");
        match.Advance(1.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the marker keeps the unit from a second tick for 2.2 seconds");
        // Pressing the rush again cancels it: the rush and its damage end, the armour buff lasts its four seconds.
        TestAssert.True(match.Cast(2, match.Hero), "the cancel casts");
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "KennenLightningRush") is null, "the cancel ends the rush");
        TestAssert.True(Buff(match.Hero, "KennenLightningRushBuff") is not null, "the bonus armour lasts four seconds");
        TestAssert.Near(10.0, Cooldown(match, 2) + 0.3, 0.1, "the rush's cooldown (10 at rank one) starts when it ends");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Electrical Surge's active is covered above; Slicing Maelstrom
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-slicing-maelstrom-ticks-one-hero-in-turn")]
    public static void StormTicksOneHeroInTurn()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Slicing Maelstrom casts");
        // The storm starts when its 0.25 s cast ends and lasts three seconds: 3.25 s after the press it is over.
        match.Advance(3.5);
        var hits = HitsOn(match, "Foe");
        // The storm lasts three seconds at rank one; a single unit is struck about once a second, because the 0.5 s no-target buff keeps it out of the next pick.
        TestAssert.Equal(3, hits.Count, "three ticks in three seconds");
        TestAssert.True(hits.All(hit => Math.Abs(hit.Raw - 80.0) < 0.01), "80 at rank one");
        TestAssert.Near(Mitigated(80.0, 30.0), hits[0].Dealt, 0.05, "80 magic after 30 resistance");
        TestAssert.Near(1.0, hits[1].At - hits[0].At, 0.1, "one second between ticks on one unit");
        TestAssert.True(Buff(match.Hero, "KennenShurikenStorm") is null, "the storm ends after three seconds");
        // Each tick marks the unit; the third mark detonates (the hero is immune afterwards, KennenMoSDiminish) and removes the marks.
        TestAssert.Equal(0, Stacks(match.Foe, "KennenMarkofStorm"), "the third tick's mark detonates and removes the marks");
        TestAssert.True(Buff(match.Foe, "KennenMoSDiminish") is not null, "the hero is immune to the next detonation");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death and respawn
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-passive-returns-on-respawn")]
    public static void PassiveReturnsOnRespawn()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Hero.Level = 6;
        match.Hero.SpellRanks[1] = 0;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 1), "the surge is learnt");
        match.Hero.Level = 1;
        match.Learn(1);
        match.Refill();
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        TestAssert.False(match.Hero.Alive, "dead after the kill");
        for (var step = 0; step < 600 && !match.Hero.Alive; step++) match.Advance(0.5);
        TestAssert.True(match.Hero.Alive, "respawned");
        TestAssert.True(Buff(match.Hero, "KennenDoubleStrikeProc") is not null, "the passive is granted again on the respawn (CharOnResurrect)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 378 (owner finding 40, 27 October 2026): the energy the cast spends reaches the owner's client in the cast step
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-kennen-shuriken-energy-reaches-the-client-in-the-cast-step")]
    public static void ShurikenEnergyReachesTheClientInTheCastStep()
    {
        using var match = new Match("Kennen", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        var owner = Observe(match, Team.Order, match.Hero);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "Thundering Shuriken casts at the enemy");
        var press = match.Now;
        // The charge lands on the first step at or after the cast frame; its replication goes out in that same flush, with two steps of slack.
        var stepEnd = press + match.CastFrameOf(0) + 3 * BbM3.Step;
        match.Advance(stepEnd - press);
        TestAssert.Near(65.0, mana - match.Hero.Mana, 2.0, "65 energy spent at the cast frame (about one step of regeneration allowed)");
        var charged = mana - 65.0;
        var replicated = owner.Packets
            .Where(item => item.Packet.Plaintext.Length > 0 && item.Packet.Plaintext.Span[0] == 0xCC && item.At <= stepEnd + 1e-9)
            .Select(item => WirePacketDecoder.Decode(item.Packet.Plaintext.Span, item.Packet.Channel).Fields)
            .Where(fields => fields.TryGetValue("unit_netid", out var id) && Convert.ToUInt32(id) == match.Hero.NetId)
            // The decoder keeps a block's registry fields under "fields" (mMP among them); a block without the MAP mana registry yields NaN.
            .Select(fields => fields.TryGetValue("fields", out var block) && block is IDictionary<string, object?> values &&
                              values.TryGetValue("mMP", out var energy) ? Convert.ToDouble(energy) : double.NaN)
            .Where(value => !double.IsNaN(value))
            .ToList();
        // Before the fix the owner's client first heard of the spent energy in the once-per-second hero block (1.033 s in the probe).
        TestAssert.True(replicated.Any(value => Math.Abs(value - charged) <= 1.0),
            "the owner's client holds the charged energy by the cast step (a 0xCC with mMP in that flush): mana " + mana + " charged " + charged +
            " replicated [" + string.Join(",", replicated) + "] packets " + owner.Packets.Count(item => item.Packet.Plaintext.Length > 0 && item.Packet.Plaintext.Span[0] == 0xCC) +
            " hero " + match.Hero.NetId + " press " + press + " stepEnd " + stepEnd);
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
