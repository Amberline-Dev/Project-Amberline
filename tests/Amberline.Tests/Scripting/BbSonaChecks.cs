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
/// Phase 3, wave 3 (26 October 2026, batch 282): Sona plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever
/// existed). Expected numbers are the literals of the shipped scripts and inibins (the rank-one figures of <c>Sona.inibin</c> and her spells' inibins,
/// the table values of her <c>.luaobj</c> scripts). The scenario <c>S-Sona</c> shows the same facts, and the death and respawn, on a scripted match.
/// </summary>
public static class BbSonaChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));

    /// <summary>A match with Sona at <paramref name="rank"/> on every spell, the foe 300 units east, the ally 150 units east of her.</summary>
    private static Match Arena(int rank = 1, double gap = 300.0)
    {
        var match = new Match("Sona", foeChampion: "Orianna");
        match.Learn(rank);
        // The harness gives every champion a 5000 maximum; a stats refresh replaces it with the real one (and clamps health to it).
        foreach (var champion in new[] { match.Hero, match.Ally, match.Foe, match.FarFoe })
            match.Runtime.HeroStats.Refresh(champion, push: false);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7000.0 + gap, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        return match;
    }

    /// <summary>Casts a spell after clearing its cooldown: a song's cast sets the other songs' cooldowns to two seconds (SelfExecute).</summary>
    private static bool Press(Match match, int slot, Entity? target = null)
    {
        match.Hero.SpellCooldowns[slot] = 0.0;
        return match.Cast(slot, target);
    }

    private static void Swing(Match match, Entity target, double seconds = 1.2)
    {
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, target), "swing");
        match.Advance(seconds);
    }

    private static int Stacks(Entity unit, string name) => Buff(unit, name)?.Stacks ?? 0;

    // ------------------------------------------------------------------------------------------------------------
    // The module and its tables
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Sona");
        foreach (var name in new[] { "CharScriptSona", "SonaHymnofValor", "SonaAriaofPerseverance", "SonaSongofDiscord", "SonaCrescendo", "SonaPowerChord",
                     "SonaHymnofValorMissile", "SonaAriaofPerseveranceMissile", "SonaPowerChordMissile", "SonaAriaofPerseveranceAttack",
                     "SonaHymnofValorAttack", "SonaSongofDiscordAttack", "SonaSongofDiscordMissile", "SonaHymnofValorAura",
                     "SonaAriaofPerseveranceAura", "SonaSongofDiscordAura", "SonaHymnofValorAuraB", "SonaAriaofPerseveranceAuraB",
                     "SonaSongofDiscordAuraB", "SonaAriaShield", "SonaSongofDiscordHaste", "SonaPowerChordCount", "SonaPowerChordDebuff",
                     "SonaHymnofValorCheck", "SonaAriaofPerseveranceCheck", "SonaSongofDiscordCheck", "SonaHymnofValorAttackUpgrade",
                     "SonaAriaofPerseveranceUpgrade", "SonaSongofDiscordAttackUpgrade", "SonaSoundOff", "Slow" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Sona");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(44, closure.ScriptNames.Count(), "the closure is 44 scripts (bb-coverage): her four spells, passive, the missiles and attacks, the auras, the checks and the system buffs");

        var hero = new Entity { ChampionName = "Sona" };
        TestAssert.Equal(new[] { "SonaHymnofValor", "SonaAriaofPerseverance", "SonaSongofDiscord", "SonaCrescendo" },
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Sona.inibin");
        TestAssert.Equal(new[] { 5, 5, 5, 3 }, Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three songs of five ranks and an ultimate of three");
        TestAssert.Equal(new[] { 1, 3, 5, 7, 9 }, Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal(new[] { 6, 11, 16 }, Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Sona")!;
        TestAssert.True(info is { Playable: true, Role: "support", DefaultIp: 3150, Id: 37, RetailBot: false },
            "playable as a support for 3150 IP (store-prices-2011.json id 37), not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Sona"), "listed once");

        // The tables the figures rest on, a second time as literals of the shipped scripts.
        TestAssert.Equal([50.0, 100.0, 150.0, 200.0, 250.0], Numbers(Blocks(Script("SonaHymnofValorMissile"), "TargetExecute")
            .First(block => block.Function == "BBApplyDamage").Param("DamageByLevel")!), "Hymn of Valor's missile: magic damage by rank");
        TestAssert.Equal([40.0, 60.0, 80.0, 100.0, 120.0], Numbers(Blocks(Script("SonaAriaofPerseveranceMissile"), "TargetExecute")
            .First(block => block.Function == "BBIncHealth").Param("DeltaByLevel")!), "Aria of Perseverance: the ally's heal by rank");
    }

    [Check("scripting.bb-sona-songs-and-the-line-carry-their-rank-tables")]
    public static void SongsAndTheLineCarryTheirRankTables()
    {
        TestAssert.Equal([8.0, 11.0, 14.0, 17.0, 20.0], Numbers(Blocks(Script("SonaAriaofPerseverance"), "SelfExecute")
            .First(block => block.Function == "BBSetVarInTable" && block.Text("DestVar") == "DefenseBonus").Param("SrcValueByLevel")!),
            "Aria of Perseverance: the armour and magic resistance shield by rank");
        TestAssert.Equal([0.08, 0.1, 0.12, 0.14, 0.16], Numbers(Blocks(Script("SonaSongofDiscord"), "SelfExecute")
            .First(block => block.Function == "BBSetVarInTable" && block.Text("DestVar") == "MoveSpeedMod").Param("SrcValueByLevel")!),
            "Song of Discord: the haste of a minion in reach by rank");
        TestAssert.Equal([0.06, 0.08, 0.1, 0.12, 0.14], Numbers(Blocks(Script("SonaSongofDiscordMissile"), "TargetExecute")
            .First(block => block.Function == "BBSetVarInTable").Param("SrcValueByLevel")!), "Song of Discord's missile: the haste of an ally by rank");
        TestAssert.Equal([50.0, 83.3, 116.6], Numbers(Blocks(Script("SonaCrescendo"), "TargetExecute")
            .First(block => block.Function == "BBSetVarInTable").Param("SrcValueByLevel")!), "Crescendo: the damage of each of its three hits by rank");
        TestAssert.Equal([8.0, 11.0, 14.0, 17.0, 20.0], Numbers(Blocks(Script("SonaSongofDiscordAura"), "BuffOnUpdateActions")
            .First(block => block.Function == "BBSetVarInTable").Param("SrcValueByLevel")!), "the Song's aura: the movement speed of each ally by rank");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hymn of Valor (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-hymn-of-valor-hits-the-nearest-champion-and-the-nearest-minion")]
    public static void HymnHitsTheNearestChampionAndMinion()
    {
        var match = Arena();
        Enemy(match, "Minion", new Position2(7000.0, 7400.0));
        TestAssert.True(match.Cast(0), "Hymn of Valor is cast");
        match.Advance(1.0);
        var champion = HitsOn(match, "Foe");
        var minion = HitsOn(match, "Minion");
        TestAssert.Equal(1, champion.Count, "the nearest champion is hit once");
        TestAssert.Equal(1, minion.Count, "with one champion in reach (650) the nearest minion within 850 is hit once");
        // 50 at rank one plus 0.7 of her ability power; the Hymn aura's eight is on her by the time the missile lands.
        var expected = 50.0 + 0.7 * match.Hero.AbilityPower;
        TestAssert.Near(expected, champion[0].Raw, 1e-6, "rank one: 50 magic plus 0.7 of her ability power (the aura's eight)");
        TestAssert.Near(expected, minion[0].Raw, 1e-6, "the minion takes the same");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "an ally is never hit");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sona-hymn-of-valor-with-two-champions-in-reach-hits-the-two-champions-only")]
    public static void HymnWithTwoChampionsHitsTwoChampionsOnly()
    {
        var match = Arena();
        match.FarFoe.Position = new Position2(6700.0, 7000.0);
        Enemy(match, "Minion", new Position2(7000.0, 7400.0));
        TestAssert.True(match.Cast(0), "Hymn of Valor is cast");
        match.Advance(1.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the champion in front is hit once");
        TestAssert.Equal(1, HitsOn(match, "FarFoe").Count, "the champion behind her, also in reach, is hit once");
        TestAssert.Equal(0, HitsOn(match, "Minion").Count, "with two champions in reach (the two nearest champions) no minion is chosen");
    }

    [Check("scripting.bb-sona-hymn-of-valor-puts-the-other-songs-on-a-cooldown-overrides-the-attack-and-adds-the-aura")]
    public static void HymnPutsTheOtherSongsOnACooldownOverridesTheAttackAndAddsTheAura()
    {
        var match = Arena();
        var attackDamage = match.Hero.AttackDamage;
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(0), "Hymn of Valor is cast");
        TestAssert.Near(7.0, match.Hero.SpellCooldowns[0], 1e-6, "its own cooldown: seven seconds at every rank");
        TestAssert.Near(2.0, match.Hero.SpellCooldowns[1], 1e-6, "Aria of Perseverance is put on a two-second cooldown (SelfExecute: twice one plus the cooldown modifier)");
        TestAssert.Near(2.0, match.Hero.SpellCooldowns[2], 1e-6, "Song of Discord too");
        TestAssert.Equal("SonaHymnofValorAttack", match.Hero.ScriptAttackOverride?.SpellName, "the basic attack is the Hymn attack (extra slot 4)");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "SonaHymnofValor") is not null, "the song is on");
        TestAssert.True(Buff(match.Hero, "SonaHymnofValorAura") is not null, "its aura is on");
        TestAssert.Near(8.0, match.Hero.AttackDamage - attackDamage, 1e-6, "the aura's bonus attack damage, on herself too: eight at rank one");
        TestAssert.Near(8.0, match.Hero.AbilityPower - abilityPower, 1e-6, "and the bonus ability power: eight");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Aria of Perseverance (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-aria-of-perseverance-heals-herself-and-the-most-wounded-ally-and-shields-them")]
    public static void AriaHealsHerselfAndTheMostWoundedAllyAndShieldsThem()
    {
        var match = Arena();
        // Learn refreshes the stats, which resets the harness's 5000 maximum: the wounds are set against the real maxima.
        var heroHealth = match.Hero.MaxHealth * 0.5;
        var allyHealth = match.Ally.MaxHealth * 0.25;
        match.Hero.Health = heroHealth;
        match.Ally.Health = allyHealth;
        var allyArmor = match.Ally.Armor;
        TestAssert.True(match.Cast(1), "Aria of Perseverance is cast");
        match.Advance(0.8);
        var heroGain = match.Hero.Health - heroHealth;
        var allyGain = match.Ally.Health - allyHealth;
        TestAssert.True(heroGain is >= 40.0 - 1e-6 and <= 44.0, $"herself: 40 at rank one (regeneration may add a little): {heroGain}");
        TestAssert.True(allyGain is >= 40.0 - 1e-6 and <= 44.0, $"the most wounded ally (the other ally is not chosen): 40: {allyGain}");
        TestAssert.Near(16.0, match.Ally.Armor - allyArmor, 1e-6, "the ally's armour: the shield (eight) and the aura (eight)");
        TestAssert.True(Buff(match.Ally, "SonaAriaShield") is not null, "the ally carries the shield");
        match.Advance(3.5);
        TestAssert.Near(8.0, match.Ally.Armor - allyArmor, 1e-6, "when the shield ends the aura alone is left: eight");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Song of Discord (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-song-of-discord-gives-allies-flat-speed-and-hastes-minions-and-champions")]
    public static void DiscordGivesFlatSpeedAndHastesMinionsAndChampions()
    {
        var match = Arena();
        var friend = Enemy(match, "Friend minion", new Position2(7000.0, 7500.0), UnitType.Minion, Team.Order);
        var allySpeed = match.Ally.Speed;
        TestAssert.True(match.Cast(2), "Song of Discord is cast");
        TestAssert.Near(7.0, match.Hero.SpellCooldowns[2], 1e-6, "its own cooldown: seven seconds");
        TestAssert.Near(2.0, match.Hero.SpellCooldowns[0], 1e-6, "Hymn of Valor is put on a two-second cooldown");
        TestAssert.Near(2.0, match.Hero.SpellCooldowns[1], 1e-6, "Aria of Perseverance too");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Ally, "SonaSongofDiscordAuraB") is not null, "the ally in reach carries the aura's speed");
        TestAssert.True(Buff(match.Hero, "SonaSongofDiscordAuraB") is not null, "herself too: the aura reaches her");
        TestAssert.True(match.Ally.Speed - allySpeed >= 8.0 - 1e-6, $"flat speed: at least eight more: {match.Ally.Speed - allySpeed}");
        TestAssert.True(Buff(friend, "SonaSongofDiscordHaste") is not null, "a minion in reach is hasted");
        TestAssert.True(Buff(match.Ally, "SonaSongofDiscordHaste") is not null, "a champion in reach is hasted by the Song's missile");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Crescendo (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-crescendo-stuns-the-champion-and-hits-it-three-times")]
    public static void CrescendoStunsAndHitsThreeTimes()
    {
        var match = Arena();
        var start = match.Foe.Position;
        TestAssert.True(match.Cast(3, match.Foe), "Crescendo is cast at the foe");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "SonaCrescendo") is not null, "the foe is stunned for 1.5 s");
        TestAssert.True(Distance(match.Foe.Position, start) < 1.0, "a stunned foe does not move");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "SonaCrescendo") is null, "the stun ends");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "three hits of 50 (on arrival, at 0.75 s and as the stun ends: the script's three damage steps)");
        foreach (var hit in hits)
            TestAssert.Near(50.0, hit.Raw, 1e-6, "rank one: 50 before resistances");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Power Chord (passive): the third spell, the last song's empowered attack
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-power-chord-takes-the-third-spell-and-empowers-the-attack-of-the-last-song")]
    public static void PowerChordTakesTheThirdSpellAndEmpowersTheLastSong()
    {
        var match = Arena();
        TestAssert.True(Press(match, 0), "Hymn of Valor");
        TestAssert.Equal(1, Stacks(match.Hero, "SonaPowerChordCount"), "one spell, one stack");
        TestAssert.True(Press(match, 1), "Aria of Perseverance");
        TestAssert.Equal(2, Stacks(match.Hero, "SonaPowerChordCount"), "two spells, two stacks");
        TestAssert.True(Press(match, 2), "Song of Discord: the third spell");
        TestAssert.True(Buff(match.Hero, "SonaPowerChord") is not null, "the third spell readies Power Chord");
        TestAssert.Equal(0, Stacks(match.Hero, "SonaPowerChordCount"), "the stacks are spent");
        TestAssert.Equal("SonaSongofDiscordAttackUpgrade", match.Hero.ScriptAttackOverride?.SpellName, "the last song's empowered attack");
        // Hymn's own missile (cast on the way) lands on the nearest champion too: only the swing's hits count.
        match.Advance(1.0);
        var before = HitsOn(match, "Foe").Count;
        Swing(match, match.Foe);
        var hits = HitsOn(match, "Foe").Skip(before).ToList();
        TestAssert.Equal(2, hits.Count, "the empowered attack: the attack damage and one magic hit");
        TestAssert.True(hits.Any(hit => Math.Abs(hit.Raw - 23.0) < 1e-6), "the magic hit: 14 + 9 x level 1 = 23");
        TestAssert.True(Buff(match.Hero, "SonaPowerChord") is null, "the empowered attack spends Power Chord");
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "a champion is slowed too: the script's test is CO_IS_TYPE_AI, which holds for champions (only turrets are spared)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sona-power-chord-after-aria-debuffs-the-damage-of-a-minion-it-hits")]
    public static void PowerChordAfterAriaDebuffsAMinion()
    {
        var match = Arena();
        var minion = Enemy(match, "Minion", new Position2(7000.0, 7400.0));
        TestAssert.True(Press(match, 0), "Hymn of Valor");
        TestAssert.True(Press(match, 1), "Aria of Perseverance");
        TestAssert.True(Press(match, 3, match.Foe), "Crescendo: the third spell, which re-arms the empowered attack of the song in use (Aria)");
        TestAssert.True(Buff(match.Hero, "SonaPowerChord") is not null, "Power Chord is ready");
        TestAssert.Equal("SonaAriaofPerseveranceUpgrade", match.Hero.ScriptAttackOverride?.SpellName, "the empowered attack is Aria's");
        match.Advance(1.0);
        var before = HitsOn(match, "Minion").Count;
        Swing(match, minion);
        var hits = HitsOn(match, "Minion").Skip(before).ToList();
        TestAssert.Equal(2, hits.Count, "the attack damage and one magic hit on the minion");
        TestAssert.True(hits.Any(hit => Math.Abs(hit.Raw - 23.0) < 1e-6), "the magic hit: 23 at rank one");
        TestAssert.True(Buff(minion, "SonaPowerChordDebuff") is not null, "a minion hit by Aria's empowered attack is debuffed (four seconds)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-sona-power-chord-after-hymn-adds-a-magic-hit-of-twice-the-bonus")]
    public static void PowerChordAfterHymnDoublesTheMagicHit()
    {
        var match = Arena();
        var minion = Enemy(match, "Minion", new Position2(7000.0, 7400.0));
        TestAssert.True(Press(match, 1), "Aria of Perseverance");
        TestAssert.True(Press(match, 2), "Song of Discord");
        TestAssert.True(Press(match, 0), "Hymn of Valor: the third spell, the last song");
        TestAssert.Equal("SonaHymnofValorAttackUpgrade", match.Hero.ScriptAttackOverride?.SpellName, "the empowered attack is Hymn's");
        match.Advance(1.0);
        var before = HitsOn(match, "Minion").Count;
        Swing(match, minion);
        var hits = HitsOn(match, "Minion").Skip(before).ToList();
        TestAssert.Equal(2, hits.Count, "the attack damage and one magic hit on the minion");
        TestAssert.True(hits.Any(hit => Math.Abs(hit.Raw - 46.0) < 1e-6), "Hymn's magic hit is twice the bonus: 2 x (14 + 9) = 46");
        TestAssert.True(Buff(minion, "SonaPowerChordDebuff") is null && Buff(minion, "Slow") is null, "Hymn's empowered attack neither debuffs nor slows");
    }

    [Check("scripting.bb-sona-power-chord-after-discord-slows-a-minion-for-two-seconds")]
    public static void PowerChordAfterDiscordSlowsAMinion()
    {
        var match = Arena();
        var minion = Enemy(match, "Minion", new Position2(7000.0, 7400.0));
        TestAssert.True(Press(match, 0), "Hymn of Valor");
        TestAssert.True(Press(match, 1), "Aria of Perseverance");
        TestAssert.True(Press(match, 2), "Song of Discord: the third spell, the last song");
        Swing(match, minion);
        TestAssert.True(Buff(minion, "Slow") is not null, "the minion is slowed (forty percent for two seconds)");
        TestAssert.True(Buff(minion, "SonaPowerChordDebuff") is null, "Discord's empowered attack does not debuff");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The songs replace each other
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sona-songs-switch-each-other-off")]
    public static void SongsSwitchEachOtherOff()
    {
        var match = Arena();
        TestAssert.True(Press(match, 0), "Hymn of Valor is on");
        TestAssert.True(Buff(match.Hero, "SonaHymnofValor") is not null, "Hymn is on");
        TestAssert.True(Press(match, 2), "Song of Discord is cast");
        TestAssert.True(Buff(match.Hero, "SonaHymnofValor") is null, "Discord ends Hymn (its name in the script differs in case)");
        TestAssert.True(Buff(match.Hero, "SonaSongofDiscord") is not null, "Discord is on");
        TestAssert.True(Press(match, 1), "Aria of Perseverance is cast");
        TestAssert.True(Buff(match.Hero, "SonaSongofDiscord") is null, "Aria ends Discord");
        TestAssert.True(Buff(match.Hero, "SonaAriaofPerseverance") is not null, "Aria is on");
    }
}
