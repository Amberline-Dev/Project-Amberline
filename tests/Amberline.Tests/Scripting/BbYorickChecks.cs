using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3 (19 October 2026): Yorick plays from his shipped scripts in a production <c>MatchRuntime</c> (the hand-written module, its spell
/// service and its state are gone). The numbers come from the shipped files (script tables and the ghoul records) and from the measured
/// output of the scenario <c>S-yorick</c> (level 11, Q3 W3 E3 R3 here), and are pinned a second time as literals where a table has them.
/// Written, not run (owner rule: no check runs during the overnight phase 3). Every match is a <c>BbM3.Match</c> with Yorick as the hero,
/// the ally Annie, the enemy Orianna ("Foe", 100 units in front of him) and a far Ezreal; the damage the checks compare is read from the
/// committed hits (<c>HitsOn</c>: the raw amount before resistance), so the resistances of the dummies do not matter.
/// </summary>
public static class BbYorickChecks
{
    // The scripts the four slots, the ghouls, the revenant and the zombie life run (the closure also holds borrowed scripts whose spawns are refused).
    private static readonly string[] KeyScripts =
    [
        "CharScriptYorick", "YorickUnholySymbiosis", "YorickRavenousLifesteal", "GlobalDrain",
        "YorickSpectral", "YorickSummonSpectral", "YorickSpectralLogic", "YorickSpectralPrimaryTarget", "YorickSpectralUnlock", "YorickActiveSpectral",
        "YorickDecayed", "YorickSummonDecayed", "YorickDecayedLogic", "YorickDecayedDiseaseCloud", "YorickDecayedAuraSlow", "YorickDecayedSlow",
        // YorickActiveDecayed.luaobj ships, but no script of the closure adds it (the W marker on Yorick is the Active buff of Q and E only).
        "YorickRavenous", "YorickSummonRavenous", "YorickRavenousLogic", "YorickRavenousPrimaryTarget", "YorickActiveRavenous",
        "YorickReviveAlly", "YorickRARevive", "YorickReviveAllySelf", "YorickRADelay", "YorickRAZombie", "YorickRAPetBuff2", "YorickRARemovePet",
        "YorickUltPetActive", "YorickUltStun",
    ];

    private static readonly string[] SummonMarkers = ["YorickSummonSpectral", "YorickSummonRavenous", "YorickSummonDecayed"];

    private static double Between(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static double BonusAttackDamage(Match match) =>
        match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Yorick"), match.Hero.Level).AttackDamage;

    private static double Literal(BbScript script, string hook, string variable)
    {
        var block = Blocks(script, hook).First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable);
        return block.Number("SrcValue")!.Value;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The match and the small readers every check shares
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Yorick at level 11 with Q3 W3 E3 R3, full resources, facing east; the Foe stands 100 units in front of him, in reach of his swing.</summary>
    private static Match Open()
    {
        var match = new Match("Yorick");
        match.Hero.Level = 11;
        match.Learn(3);
        match.Refill();
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7100, 7000);
        return match;
    }

    private static Entity[] Ghouls(Match match) => match.Runtime.World.Entities.Values
        .Where(unit => unit.Kind == EntityKind.YorickGhoul && unit.OwnerNetId == match.Hero.NetId && unit.Alive).ToArray();

    private static Entity[] Revenants(Match match) => match.Runtime.World.Entities.Values
        .Where(unit => unit.Kind == EntityKind.YorickRevenant && unit.OwnerNetId == match.Hero.NetId && unit.Alive).ToArray();

    /// <summary>Ticks one step at a time until Yorick owns <paramref name="count"/> living ghouls, so the caller sees them in the tick they were born.</summary>
    private static Entity[] AwaitGhouls(Match match, int count, int ticks = 180)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            match.Advance(Step);
            var found = Ghouls(match);
            if (found.Length >= count) return found;
        }
        throw new InvalidOperationException($"Yorick has no {count} ghoul(s) after {ticks} ticks");
    }

    /// <summary>Starts a swing of Yorick at the Foe (retrying for a moment while a cast still holds him).</summary>
    private static void QueueSwing(Match match)
    {
        for (var attempt = 0; attempt < 45; attempt++)
        {
            match.Hero.AttackCooldown = 0.0;
            if (match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe)) return;
            match.Advance(Step);
        }
        throw new InvalidOperationException("Yorick could not start a swing at the foe within 1.5 s");
    }

    /// <summary>One swing at the Foe and the hits on it during the next <paramref name="seconds"/>.</summary>
    private static List<(double At, double Dealt, double Raw, string Kind)> Swing(Match match, double seconds)
    {
        var before = HitsOn(match, "Foe").Count;
        QueueSwing(match);
        match.Advance(seconds);
        return HitsOn(match, "Foe").Skip(before).ToList();
    }

    /// <summary>Omen of War and the swing that spends it: returns the Spectral Ghoul in the tick it was born.</summary>
    private static Entity SpawnSpectral(Match match)
    {
        TestAssert.True(match.Cast(0, match.Hero), "Omen of War");
        match.Advance(0.2);
        QueueSwing(match);
        return AwaitGhouls(match, 1)[0];
    }

    /// <summary>Omen of Death on <paramref name="target"/>: returns the revenant once the child spell has cloned the target.</summary>
    private static Entity CastUltimate(Match match, Entity target)
    {
        TestAssert.True(match.Cast(3, target), "Omen of Death");
        for (var tick = 0; tick < 90; tick++)
        {
            match.Advance(Step);
            if (Revenants(match).FirstOrDefault() is { } revenant)
            {
                match.Advance(0.1);
                return revenant;
            }
        }
        throw new InvalidOperationException("no revenant of Yorick after 3 s: " + Names(target) + " | " + Names(match.Hero));
    }

    private static void KillAlly(Match match) =>
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Ally.NetId, match.Ally.MaxHealth * 3.0, DamageType.True, DamageSourceKind.Raw),
            "the lethal hit on the ally lands");

    private static bool Sent(WireCapture wire, byte opcode, uint netId, int flagIndex = -1, byte flag = 0)
    {
        foreach (var (_, packet) in wire.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 5 || bytes[0] != opcode || BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != netId) continue;
            if (flagIndex < 0 || (bytes.Length > flagIndex && bytes[flagIndex] == flag)) return true;
        }
        return false;
    }

    private static void NoScriptFailed(Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsKeyScripts()
    {
        var module = new BbChampionModule("Yorick");
        foreach (var name in KeyScripts)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var hero = new Entity { ChampionName = "Yorick" };
        TestAssert.Equal(["YorickSpectral", "YorickDecayed", "YorickRavenous", "YorickReviveAlly"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Yorick.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        // The closure also holds borrowed scripts whose spawns are refused; see docs/scripting/building-blocks-engine.md, so the unbound-native
        // scan covers the scripts the four slots, the ghouls, the revenant and the zombie life run.
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Yorick");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => KeyScripts.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a key Yorick script runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-yorick-production-runs-the-shipped-scripts")]
    public static void ProductionRunsYorickFromHisScripts()
    {
        using (var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0))
        {
            var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
            TestAssert.True(dispatcher.FindModule("Yorick") is BbChampionModule, "Yorick is a BbChampionModule in production (phase 3, 19 October 2026)");
            TestAssert.Equal(1, dispatcher.RegisteredChampionNames.Count(name => string.Equals(name, "Yorick", StringComparison.OrdinalIgnoreCase)),
                "one module answers for Yorick");
            TestAssert.False(dispatcher.RegisteredChampionNames.Where(name => dispatcher.FindModule(name) is not BbChampionModule)
                    .Contains("Yorick", StringComparer.OrdinalIgnoreCase),
                "no hand-written Yorick is registered");
        }
        // The hand-written module, its spell service, its state and its cast stages are deleted, not only unregistered.
        TestAssert.False(typeof(BbChampionModule).Assembly.GetTypes()
                .Any(type => type.Name is "YorickModule" or "YorickSpellService" or "YorickChampionState" or "YorickCastStage"),
            "the hand-written Yorick types are gone");
        using var match = new Match("Yorick");
        TestAssert.True(match.Module.IsBound, "production binds the Yorick module to a match that has him");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Unholy Symbiosis
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-passive-scales-his-swing-up-and-the-hits-he-takes-down-by-five-percent-per-ghoul")]
    public static void UnholySymbiosisScalesTheSwingAndTheHitsByTheGhoulCount()
    {
        var passive = Script("YorickUnholySymbiosis");
        TestAssert.Near(0.05, Literal(passive, "BuffOnHitUnit", "PassiveMultiplier"), 1e-12, "YorickUnholySymbiosis.luaobj: 5 percent per ghoul on the swing");
        TestAssert.Near(0.05, Literal(passive, "BuffOnPreDamage", "PassiveMultiplier"), 1e-12, "and on the hits he takes");

        using var match = Open();
        var hero = match.Hero;
        var attack = hero.AttackDamage;
        for (var count = 0; count <= 2; count++)
        {
            if (count == 1)
            {
                match.Refill();
                TestAssert.True(match.Cast(1, at: match.Foe.Position), "Omen of Pestilence");
                AwaitGhouls(match, 1);
            }
            if (count == 2)
            {
                match.Refill();
                TestAssert.True(match.Cast(2, match.Foe), "Omen of Famine");
                AwaitGhouls(match, 2);
            }
            match.Advance(0.1);
            TestAssert.Equal(count, SummonMarkers.Count(name => Buff(hero, name) is not null), $"{count} summon marker(s) on Yorick: {Names(hero)}");

            var basic = Swing(match, 0.9).Where(item => item.Kind == "Basic").ToList();
            TestAssert.Equal(1, basic.Count, "one swing of Yorick himself (the ghouls' swings are PROC hits)");
            NearF32(attack * (1.0 + 0.05 * count), basic[0].Raw, $"his swing is x(1 + 0.05 x {count})");

            var before = HitsOn(match, "Yorick").Count;
            TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 100.0, DamageType.True, DamageSourceKind.Raw), "a true hit on Yorick");
            var taken = HitsOn(match, "Yorick").Skip(before).Last();
            NearF32(100.0 * (1.0 - 0.05 * count), taken.Dealt, $"100 true damage becomes x(1 - 0.05 x {count})");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q: Omen of War
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-q-omen-of-war-seals-its-slot-arms-the-swing-and-the-proc-calls-a-ghoul-that-lives-five-seconds")]
    public static void OmenOfWarSealsArmsAndTheProcSwingSpawnsTheSpectralGhoul()
    {
        var bonus = new[] { 30.0, 60.0, 90.0, 120.0, 150.0 };
        TestAssert.Equal(bonus, ByLevel("YorickSpectral", "SelfExecute", "BonusDamage"), "YorickSpectral.luaobj: bonus damage by rank");
        TestAssert.Equal([9.0, 8.0, 7.0, 6.0, 5.0], ByLevel("YorickSpectral", "SelfExecute", "SpellCooldown"), "YorickSpectral.luaobj: the cooldown that starts when the buff ends");
        TestAssert.Near(1.2, Blocks(Script("YorickSpectral"), "BuffOnHitUnit").First(item => item.Function == "BBMath" && item.Text("DestVar") == "DamageAmount")
            .Number("Src1Value")!.Value, 1e-12, "the swing is multiplied by 1.2");
        TestAssert.Near(5.0, Blocks(Script("YorickSummonSpectral"), "SelfExecute").First(item => item.Function == "BBSpellBuffAdd").Number("Duration")!.Value,
            1e-12, "YorickSummonSpectral.luaobj: the summon marker, and with it the ghoul, lasts 5 s");

        using var match = Open();
        var hero = match.Hero;
        var foe = match.Foe;
        var spell = GameData.Default.Spell("YorickSpectral", 5);
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, hero), "Omen of War");
        match.Advance(0.1);
        TestAssert.Near(mana - spell.ManaCosts[2], hero.Mana, 2.0, "the mana cost of rank 3");
        TestAssert.True(Buff(hero, "YorickSpectral") is { } armed && armed.ExpiresAt is > 9.5 and <= 10.0, $"the empowered attack is armed for 10 s: {Names(hero)}");
        TestAssert.True(hero.SpellSeals.IsSealed(false, 0), "the buff seals the Q slot");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "the cooldown is zero while the buff is up");
        TestAssert.True(hero.DodgePiercing, "the empowered attack does not miss");
        TestAssert.False(match.Cast(0, hero), "Q cannot be cast again while the slot is sealed");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "YorickSpectral") is not null && hero.SpellSeals.IsSealed(false, 0) && hero.SpellCooldowns[0] == 0.0, "nothing changes until the swing");

        var attack = hero.AttackDamage;
        var before = HitsOn(match, "Foe").Count;
        var dealtBefore = hero.MatchStatistics.TotalDamageDealt;
        QueueSwing(match);
        var ghoul = AwaitGhouls(match, 1)[0];
        var born = match.Now;
        TestAssert.Near(25.0, Between(ghoul.Position, hero.Position), 30.0, "the ghoul is born 25 units in front of Yorick (one tick of walking at most)");
        TestAssert.True(ghoul.Position.X > hero.Position.X, "in front: toward the foe");
        TestAssert.Equal("YorickSpectralGhoul", ghoul.Name, "the Spectral Ghoul");
        match.Advance(3 * Step);

        var hits = HitsOn(match, "Foe").Skip(before).ToList();
        var proc = hits.First(item => item.Kind == "Proc");
        NearF32(1.2 * attack + bonus[2], proc.Raw, "1.2 x attack damage + 90 at rank 3, dealt as one PROC hit");
        TestAssert.Near(0.0, hits.Where(item => item.Kind == "Basic").Sum(item => item.Raw), 1e-6, "and the swing itself deals nothing");
        TestAssert.True(hero.MatchStatistics.TotalDamageDealt - dealtBefore >= proc.Dealt - 1e-6, "a PROC hit counts as damage dealt by Yorick");
        TestAssert.True(Buff(foe, "YorickSpectralPrimaryTarget") is { } mark && mark.ExpiresAt is > 4.0 and <= 5.0, $"the foe is marked for the ghoul: {Names(foe)}");
        TestAssert.True(Buff(hero, "YorickSpectral") is null, "the swing spends the empowered attack");
        TestAssert.True(Buff(hero, "YorickSummonSpectral") is { } marker && marker.ExpiresAt is > 4.5 and <= 5.0, $"the summon marker is on Yorick: {Names(hero)}");
        TestAssert.True(Buff(hero, "YorickActiveSpectral") is not null, "and the speed of the Omen");
        TestAssert.False(hero.SpellSeals.IsSealed(false, 0), "the spent buff opens the slot");
        TestAssert.True(hero.SpellCooldowns[0] is > 6.5 and <= 7.0, $"the rank 3 cooldown starts when the buff ends: {hero.SpellCooldowns[0]}");
        TestAssert.False(hero.DodgePiercing, "and the attack misses again");

        // The ghoul fights for Yorick (PROC hits) and is gone five seconds after it was born.
        while (ghoul.Alive && match.Now - born < 8.0) match.Advance(Step);
        TestAssert.Near(4.75, match.Now - born, 0.45, "the ghoul lives 5 s (its decay brings it to zero health at 4.5 s)");
        TestAssert.True(HitsOn(match, "Foe").Skip(before).Count(item => item.Kind == "Proc") >= 2, "the ghoul's own swings are PROC hits as well");
        match.Advance(0.1);
        TestAssert.True(Buff(hero, "YorickSummonSpectral") is null && Ghouls(match).Length == 0, "the marker and the ghoul are gone");
        NoScriptFailed(match);
    }

    /// <summary>The armed Omen of War swing folds a critical strike into a normal one: its proc is never crit-multiplied.</summary>
    [Check("scripting.bb-yorick-q-armed-swing-folds-a-critical-into-a-normal-hit")]
    public static void TheArmedSwingFoldsACriticalIntoANormalHit()
    {
        // YorickSpectral.luaobj BuffOnHitUnit: "BBIf HitResult CO_EQUAL HIT_Critical" then "BBSetVarInTable DestVar='HitResult'
        // SrcValue=HIT_Normal", before it multiplies DamageAmount by 1.2 and sets the swing to nothing. The engine multiplies a
        // critical hit as a whole (BbHookRouter.RunHitUnit, which hands the hooks the hit before the multiplier and applies it to
        // what they leave unless their HitResult is not HIT_Critical), so the fold takes the multiplier off the proc. Batch 164:
        // the hand-written CombatRules.IsEmpoweredAttack that skipped the roll for Yorick is gone and the script owns the rule.
        using var match = Open();
        var hero = match.Hero;
        var bonus = new[] { 30.0, 60.0, 90.0, 120.0, 150.0 };
        hero.CritChance = 1.0;
        hero.CritMultiplier = 2.0;
        var attack = hero.AttackDamage;

        // The roll is live: a plain swing at a hundred percent critical chance is a critical strike.
        var plain = Swing(match, 0.6);
        NearF32(2.0 * attack, plain.First(item => item.Kind == "Basic").Raw, "the roll itself crits: the plain swing is doubled");

        TestAssert.True(match.Cast(0, hero), "Omen of War");
        match.Advance(0.1);

        var before = HitsOn(match, "Foe").Count;
        QueueSwing(match);
        AwaitGhouls(match, 1);
        match.Advance(3 * Step);
        var hits = HitsOn(match, "Foe").Skip(before).ToList();
        var proc = hits.First(item => item.Kind == "Proc");
        NearF32(1.2 * attack + bonus[2], proc.Raw, "1.2 x attack damage + 90 at rank 3, with no critical multiplier folded in");
        TestAssert.Near(0.0, hits.Where(item => item.Kind == "Basic").Sum(item => item.Raw), 1e-6, "and the swing itself deals nothing");
        NoScriptFailed(match);
    }

    [Check("scripting.bb-yorick-spectral-ghoul-is-scaled-by-yoricks-level-attack-damage-and-health")]
    public static void TheSpectralGhoulTakesArmorResistAttackHealthAndSpeedFromYorick()
    {
        var speedPercent = new[] { 0.15, 0.2, 0.25, 0.3, 0.35 };
        var attackBonus = new[] { 8.0, 16.0, 24.0, 32.0, 40.0 };
        TestAssert.Equal(speedPercent, ByLevel("YorickSummonSpectral", "SelfExecute", "MovementSpeedPercent"), "YorickSummonSpectral.luaobj: speed by rank");
        TestAssert.Equal(attackBonus, ByLevel("YorickSummonSpectral", "SelfExecute", "AttackDamageMod"), "YorickSummonSpectral.luaobj: attack damage by rank");
        var logic = Script("YorickSpectralLogic");
        TestAssert.Near(0.35, Blocks(logic, "OnBuffActivate").First(item => item.Function == "BBMath" && item.Text("DestVar") == "ADFromStats")
            .Number("Src2Value")!.Value, 1e-12, "YorickSpectralLogic.luaobj: 0.35 of Yorick's attack damage");
        TestAssert.Near(0.35, Blocks(logic, "OnBuffActivate").First(item => item.Function == "BBMath" && item.Text("DestVar") == "HealthFromStats")
            .Number("Src2Value")!.Value, 1e-12, "and 0.35 of his maximum health");

        using var match = Open();
        var hero = match.Hero;
        var totalAttack = hero.AttackDamage;
        var maxHealth = hero.MaxHealth;
        var ghoul = SpawnSpectral(match);
        match.Advance(3 * Step);
        var data = GameData.Default.Champion("YorickSpectralGhoul");
        var level = hero.Level;
        var rank = hero.SpellRanks[0];
        TestAssert.True(Buff(ghoul, "YorickSpectralLogic") is not null, $"the Logic buff scales the ghoul: {Names(ghoul)}");

        TestAssert.Near(data.Armor + 2.0 * level, ghoul.Armor, 1e-6, "armor: the record + 2 per Yorick level");
        TestAssert.Near(data.SpellBlock + 2.0 * level, ghoul.SpellBlock, 1e-6, "magic resist: the record + 2 per Yorick level");
        TestAssert.Near(32.0, ghoul.Armor, 1e-6, "level 11: 10 + 22 (the measured ghoul)");
        TestAssert.Near(42.0, ghoul.SpellBlock, 1e-6, "level 11: 20 + 22 (the measured ghoul)");
        TestAssert.Near(data.BaseDamage + Math.Max(10.0, 0.35 * totalAttack - 10.0) + attackBonus[rank - 1], ghoul.AttackDamage, 0.01,
            "attack damage: the record + max(10, 0.35 of his attack damage - 10) + the Omen's bonus");
        TestAssert.Near(data.BaseHp + Math.Max(60.0, 0.35 * maxHealth - 60.0), ghoul.MaxHealth, 0.01, "health: the record + max(60, 0.35 of his maximum health - 60)");
        TestAssert.Near(ghoul.MaxHealth, ghoul.Health, 0.01, "born at full health");
        var flatSpeed = data.MoveSpeed + (level >= 3 ? 30.0 : 0.0) + (level >= 6 ? 30.0 : 0.0) + (level >= 11 ? 40.0 : 0.0);
        TestAssert.Near(flatSpeed * (1.0 + speedPercent[rank - 1]), ghoul.Speed, 0.6, "speed: +30/+30/+40 at levels 3/6/11, then the Omen's percent");
        TestAssert.Near(562.5, ghoul.Speed, 0.6, "level 11, rank 3: 450 x 1.25 (the measured ghoul)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: Omen of Pestilence
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-w-omen-of-pestilence-bursts-slows-clouds-and-the-decaying-ghoul-rots-away")]
    public static void OmenOfPestilenceBurstsSlowsAndTheDecayingGhoulDiesByItself()
    {
        var baseDamage = new[] { 60.0, 95.0, 130.0, 165.0, 200.0 };
        TestAssert.Equal(baseDamage, ByLevel("YorickSummonDecayed", "SelfExecute", "BaseDamage"), "YorickSummonDecayed.luaobj: the burst by rank");
        var decay = Blocks(Script("YorickDecayedLogic"), "BuffOnUpdateActions");
        TestAssert.Near(0.9, decay.First(item => item.Function == "BBExecutePeriodically").Number("TimeBetweenExecutions")!.Value, 1e-12, "the decay ticks every 0.9 s");
        TestAssert.Near(0.2, decay.First(item => item.Function == "BBMath" && item.Text("DestVar") == "MaxHealth").Number("Src1Value")!.Value, 1e-12,
            "and takes 20 percent of the maximum health each time");

        using var match = Open();
        match.GiveAbilityPower();
        var hero = match.Hero;
        var foe = match.Foe;
        TestAssert.True(match.Cast(1, at: foe.Position), "Omen of Pestilence at the champion");
        var ghoul = AwaitGhouls(match, 1)[0];
        var born = match.Now;
        TestAssert.Equal("YorickDecayedGhoul", ghoul.Name, "the Decaying Ghoul");
        match.Advance(3 * Step);

        var burst = HitsOn(match, "Foe").Where(item => item.Kind == "SpellAoe").ToList();
        TestAssert.Equal(1, burst.Count, "one burst hit on the foe at the ghoul");
        NearF32(baseDamage[2] + hero.AbilityPower, burst[0].Raw, "130 + 1.0 ability power at rank 3, magic area damage");
        TestAssert.True(Buff(foe, "YorickDecayedSlow") is { Type: BuffType.Slow } slow && slow.ExpiresAt is > 1.2 and <= 1.5, $"the burst slows for 1.5 s: {Names(foe)}");
        TestAssert.True(Buff(foe, "YorickDecayedAuraSlow") is not null, "the cloud already slows the foe");
        TestAssert.True(Buff(ghoul, "YorickDecayedDiseaseCloud") is not null && Buff(ghoul, "YorickDecayedLogic") is not null, $"the cloud and the Logic buff are on the ghoul: {Names(ghoul)}");
        TestAssert.True(Buff(hero, "YorickSummonDecayed") is { } marker && marker.ExpiresAt is > 4.5 and <= 5.0, $"the summon marker is on Yorick: {Names(hero)}");

        // The ghoul rots: 20 percent of its maximum health per 0.9 s.
        match.Advance(born + 1.0 - match.Now);
        TestAssert.Near(0.8 * ghoul.MaxHealth, ghoul.Health, 1.0, "one decay tick after a second");
        match.Advance(born + 2.0 - match.Now);
        TestAssert.Near(0.6 * ghoul.MaxHealth, ghoul.Health, 1.0, "two after two seconds");
        TestAssert.True(Buff(foe, "YorickDecayedSlow") is null, "the burst slow is over");
        TestAssert.True(Buff(foe, "YorickDecayedAuraSlow") is not null, "the cloud keeps slowing whoever stands within 275 of the ghoul");
        while (ghoul.Alive && match.Now - born < 8.0) match.Advance(Step);
        TestAssert.Near(4.75, match.Now - born, 0.55, "the ghoul dies by itself at about 4.5 s (the summon marker ends it at 5 s)");
        match.Advance(0.1);
        TestAssert.True(Ghouls(match).Length == 0 && Buff(hero, "YorickSummonDecayed") is null, "the marker is gone with it");
        match.Advance(1.2);
        TestAssert.True(Buff(foe, "YorickDecayedAuraSlow") is null, "the cloud left with the ghoul and its slow runs out");

        // A second ghoul takes half of the damage of an area spell (YorickDecayedLogic.BuffOnPreMitigationDamage), not of a single-target one.
        match.Refill();
        TestAssert.True(match.Cast(1, at: foe.Position), "a second Omen of Pestilence");
        var second = AwaitGhouls(match, 1)[0];
        match.Advance(3 * Step);
        var health = second.Health;
        TestAssert.True(match.Runtime.Combat.ApplyHit(foe.NetId, second.NetId, 100.0, DamageType.True, DamageSourceKind.SpellAoe), "an area spell hits the ghoul");
        TestAssert.Near(50.0, health - second.Health, 1e-6, "DAMAGESOURCE_SPELLAOE is halved");
        health = second.Health;
        TestAssert.True(match.Runtime.Combat.ApplyHit(foe.NetId, second.NetId, 100.0, DamageType.True, DamageSourceKind.Spell), "a single-target spell hits the ghoul");
        TestAssert.Near(100.0, health - second.Health, 1e-6, "and DAMAGESOURCE_SPELL is not");
        NoScriptFailed(match);
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Omen of Famine
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-e-omen-of-famine-hurts-heals-forty-percent-and-the-ravenous-ghoul-steals-all-its-hit-for-him")]
    public static void OmenOfFamineHurtsHealsAndTheRavenousGhoulStealsLife()
    {
        var baseDamage = new[] { 55.0, 85.0, 115.0, 145.0, 175.0 };
        TestAssert.Equal(baseDamage, ByLevel("YorickRavenous", "TargetExecute", "BaseDamage"), "YorickRavenous.luaobj: damage by rank");
        TestAssert.Equal([0.4, 0.4, 0.4, 0.4, 0.4], ByLevel("YorickRavenous", "TargetExecute", "DrainPercent"), "YorickRavenous.luaobj: Yorick heals 40 percent of it");
        TestAssert.Equal([1.0, 1.0, 1.0, 1.0, 1.0], ByLevel("YorickRavenousLogic", "BuffOnHitUnit", "LifestealPercent"), "YorickRavenousLogic.luaobj: the ghoul's life steal");
        TestAssert.Near(250.0, Blocks(Script("YorickRavenous"), "TargetExecute").First(item => item.Function == "BBMath" && item.Text("DestVar") == "Distance")
            .Number("Src2Value")!.Value, 1e-12, "the ghoul is born 250 units past the target");

        using var match = Open();
        // A Long Sword: the script adds Yorick's bonus attack damage to the damage.
        match.Hero.Inventory = [new InventorySlot(1036), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        TestAssert.True(BonusAttackDamage(match) > 5.0, "a Long Sword raises Yorick's attack damage");
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Health = hero.MaxHealth * 0.4;
        var health = hero.Health;
        TestAssert.True(match.Cast(2, foe), "Omen of Famine");
        var ghoul = AwaitGhouls(match, 1)[0];
        TestAssert.Equal("YorickRavenousGhoul", ghoul.Name, "the Ravenous Ghoul");
        // YorickRavenous.TargetExecute: Distance = BBDistanceBetweenObjects(Yorick, target) + 250, the point that far along Yorick's facing. The distance
        // is the gap between the two bodies since batch 112 (BbSpellHost.DistanceBetweenObjects: centres minus both collision radii), so the ghoul is
        // born 250 units past the point where the bodies touch, on the far side from Yorick.
        var gap = Math.Max(0.0, Between(hero.Position, foe.Position) - hero.CombatRadius - foe.CombatRadius);
        TestAssert.Near(gap + 250.0, ghoul.Position.X - hero.Position.X, 30.0,
            $"born 250 units past the gap to the target, on the far side from Yorick (hero {hero.Position} facing {hero.Facing}, foe {foe.Position}, ghoul {ghoul.Position})");
        TestAssert.True(ghoul.Position.X > foe.Position.X, "and beyond the target");
        match.Advance(3 * Step);

        var spellHits = HitsOn(match, "Foe").Where(item => item.Kind == "Spell").ToList();
        TestAssert.Equal(1, spellHits.Count, "one hit of the Omen");
        NearF32(baseDamage[2] + BonusAttackDamage(match), spellHits[0].Raw, "115 + the bonus attack damage at rank 3, magic damage");
        // GlobalDrain restores a share of the DamageAmount of its BuffOnPreDealDamage hook, which carries the damage after armor and magic resistance
        // since batch 123 (69f2e17d; V1.0.0.120: heals "based upon the actual damage dealt (instead of the raw damage dealt)"): 40 percent of what the foe took.
        TestAssert.Near(0.4 * spellHits[0].Dealt, hero.Health - health, 3.0, "Yorick heals 40 percent of the damage dealt (the global drain buff)");
        TestAssert.True(spellHits[0].Dealt < spellHits[0].Raw, "the foe's magic resistance took its share first");
        TestAssert.True(Buff(foe, "YorickRavenousPrimaryTarget") is not null, $"the foe is marked for the ghoul: {Names(foe)}");
        TestAssert.True(Buff(hero, "YorickSummonRavenous") is { } marker && marker.ExpiresAt is > 4.5 and <= 5.0, $"the summon marker is on Yorick: {Names(hero)}");
        TestAssert.True(Buff(ghoul, "YorickRavenousLogic") is not null, "the Logic buff is on the ghoul");

        // The ghoul's first swing: Yorick gets back the whole hit (a PROC hit he deals), in the tick it lands.
        var landed = false;
        for (var tick = 0; tick < 240 && !landed; tick++)
        {
            var healthBefore = hero.Health;
            var procs = HitsOn(match, "Foe").Count(item => item.Kind == "Proc");
            match.Advance(Step);
            var after = HitsOn(match, "Foe").Where(item => item.Kind == "Proc").ToList();
            if (after.Count <= procs) continue;
            landed = true;
            TestAssert.Near(after[^1].Raw, hero.Health - healthBefore, 1.0, "the ghoul's hit heals Yorick by 100 percent of its damage");
        }
        TestAssert.True(landed, "the ghoul swings at the foe within 8 s");
        NoScriptFailed(match);
    }

    /// <summary>
    /// Finding 34 (owner's hand test, 24 October 2026): "E: sometimes spawns far from the enemy instead of behind them."
    /// <c>YorickRavenous.TargetExecute</c> spawns the ghoul at <c>BBGetPointByUnitFacingOffset(Owner, Distance = gap + 250)</c>,
    /// i.e. along <b>Yorick's facing</b>. A unit-target cast turns the caster toward the unit it was cast at: the stock
    /// client does it at the cast frame (the spell caster's animation step: the target
    /// when <c>NeedTargetSelect</c> is true, the target is not the caster, the squared distance is above 4 and
    /// <c>ShouldFaceTarget</c> holds), and the server must turn the unit the same way or a Yorick facing away from the foe
    /// spawns the ghoul behind <i>himself</i>. The old check forced his facing east with the foe east, so it could not see it.
    /// </summary>
    [Check("scripting.bb-yorick-e-the-ghoul-lands-behind-the-foe-he-cast-at-not-along-his-old-facing")]
    public static void OmenOfFamineSpawnsTheGhoulBehindTheFoeWhateverYorickWasFacing()
    {
        using var match = Open();
        var hero = match.Hero;
        var foe = match.Foe;
        // Yorick faces west, the foe stands east of him: the cast must turn him east before the script reads his facing.
        hero.Facing = new Position2(-1.0, 0.0);
        TestAssert.True(match.Cast(2, foe), "Omen of Famine");
        var ghoul = AwaitGhouls(match, 1)[0];
        TestAssert.True(ghoul.Position.X > foe.Position.X,
            $"born on the far side of the foe, not behind Yorick (hero {hero.Position} facing {hero.Facing}, foe {foe.Position}, ghoul {ghoul.Position})");
        NoScriptFailed(match);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The ghouls and their owner
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-ghouls-die-with-yorick-and-a-ghoul-never-acquires-a-ward")]
    public static void GhoulsDieWithTheirOwnerAndSkipWardsInAcquisition()
    {
        using var match = Open();
        var hero = match.Hero;
        var foe = match.Foe;

        // Owner decision, 18 October 2026: automatic acquisition never takes a ward (CombatService.IsAutoAcquirable). The ghoul is the script
        // fixture (no taunt names a target), the ward is revealed and nearer than the champion, so it would be the pick without the rule.
        // Batch 149 step 3: a ward is revealed by having no Stealth buff (the ward script adds one only two seconds after a placement),
        // so this ward carries no reveal clock of its own.
        var origin = hero.Position;
        var fixture = YorickScriptSupport.SpawnUnit(match.Runtime, hero, "Decayed", origin, match.Runtime.Combat.CurrentTime);
        var ward = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Ward, UnitType.Monster, Team.Chaos,
            new Position2(origin.X + 100.0, origin.Z))
        { Health = 3, MaxHealth = 3, Created = true, Visible = true, IsWard = true };
        match.Runtime.World.Register(ward);
        foe.Position = new Position2(origin.X + 300.0, origin.Z);
        TestAssert.False(CombatService.IsAutoAcquirable(ward), "a ward is not auto-acquirable");
        TestAssert.True(CombatService.IsAutoAcquirable(foe), "a champion is");
        for (var tick = 0; tick < 18; tick++)
        {
            match.Advance(Step);
            TestAssert.True(fixture.AttackTarget != ward.NetId, "the ghoul never targets the ward");
        }
        TestAssert.Equal<uint?>(foe.NetId, fixture.AttackTarget, "it acquired the champion behind the nearer ward");
        TestAssert.Near(3.0, ward.Health, 1e-9, "and the ward is untouched");
        match.Runtime.Combat.Kill(fixture, 0);
        match.Runtime.Combat.Kill(ward, 0);
        foe.Position = new Position2(7100, 7000);

        // Retail (owner research, 18 October 2026): the ghouls die with Yorick.
        match.Refill();
        TestAssert.True(match.Cast(1, at: foe.Position), "Omen of Pestilence");
        AwaitGhouls(match, 1);
        match.Advance(0.3);
        match.Refill();
        TestAssert.True(match.Cast(2, foe), "Omen of Famine");
        var ghouls = AwaitGhouls(match, 2);
        match.Advance(0.2);
        TestAssert.Equal(2, Ghouls(match).Length, "a Decaying and a Ravenous Ghoul stand");
        match.Runtime.Combat.Kill(hero, foe.NetId);
        TestAssert.True(ghouls.All(ghoul => !ghoul.Alive) && Ghouls(match).Length == 0, "both ghouls died with Yorick (CombatService.DespawnPetsOf)");
        TestAssert.True(ghouls.All(ghoul => !match.Runtime.Pets.LastDestinations.ContainsKey(ghoul.NetId)), "and left no route behind");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: Omen of Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-r-omen-of-death-raises-a-revenant-and-the-allys-death-becomes-a-zombie-life-that-ends-by-force-dead")]
    public static void OmenOfDeathMarksTheAllyAndHerDeathBecomesATenSecondZombieLife()
    {
        TestAssert.Equal([0.45, 0.6, 0.75], ByLevel("YorickRAPetBuff2", "OnBuffActivate", "ADRatio"), "YorickRAPetBuff2.luaobj: the revenant's hit as a ratio of its attack");
        using var match = Open();
        var hero = match.Hero;
        var ally = match.Ally;
        ally.Level = 11; // the original respawn timer must outlast the zombie life
        var wire = Observe(match, Team.Order, hero);
        match.Advance(0.3);

        var revenant = CastUltimate(match, ally);
        TestAssert.True(revenant.Kind == EntityKind.YorickRevenant && revenant.OwnerNetId == hero.NetId, "the revenant is Yorick's");
        TestAssert.True(revenant.MaxHealth > 0.0 && revenant.Health >= revenant.MaxHealth - 1e-6, "cloned and healed to full");
        TestAssert.True(Buff(revenant, "YorickRAPetBuff2") is { } pet && pet.ExpiresAt is > 8.5 and <= 10.0, $"the revenant lives 10 s: {Names(revenant)}");
        TestAssert.True(Buff(ally, "YorickReviveAllySelf") is { } mark && mark.ExpiresAt is > 8.5 and <= 10.0, $"the ally carries the marker for 10 s: {Names(ally)}");
        TestAssert.True(Buff(hero, "YorickRARemovePet") is not null, $"and Yorick the link to the revenant: {Names(hero)}");

        // The marked ally dies: the death becomes a zombie life that stands at one health and is told so by the death packet.
        var death = match.Now;
        KillAlly(match);
        match.Advance(3 * Step);
        TestAssert.True(ally.Alive && ally.IsZombie && ally.YorickRevival is not null, "the ally stands up again as a zombie");
        TestAssert.True(ally.Health is > 0.0 and < 5.0, $"at one health: {ally.Health}");
        TestAssert.Equal(1, ally.ChampionDeaths, "the original death is counted once");
        TestAssert.True(ally.RespawnAt - match.Runtime.Combat.CurrentTime > 20.0, $"and keeps its original respawn timer: {ally.RespawnAt - match.Runtime.Combat.CurrentTime}");
        TestAssert.True(Buff(ally, "YorickRADelay") is { } delay && delay.ExpiresAt is > 0.5 and <= 1.0, $"the one-second delay runs: {Names(ally)}");
        TestAssert.True(ally.Buffs.Any(buff => buff.Payload.Invulnerable) && ally.Buffs.Any(buff => buff.Payload.Untargetable), "invulnerable and untargetable in it");
        TestAssert.True(Buff(hero, "YorickUltPetActive") is not null, $"Yorick's pet marker: {Names(hero)}");
        TestAssert.True(Buff(hero, "YorickRARemovePet") is null && !revenant.Alive && Revenants(match).Length == 0, "the revenant is removed");
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, 500.0, DamageType.True, DamageSourceKind.Raw);
        TestAssert.True(ally.Alive && ally.Health < 5.0, "nothing hurts it in the delay");

        match.Advance(1.2);
        TestAssert.True(ally.IsZombie && Buff(ally, "YorickRAZombie") is { } zombie && zombie.ExpiresAt is > 8.0 and <= 10.0, $"then the zombie buff runs: {Names(ally)}");
        TestAssert.Near(ally.MaxHealth, ally.Health, 1.0, "at full health");

        // The life ends by itself: BBForceDead of the zombie buff, about ten seconds after the delay.
        while (ally.IsZombie && match.Now - death < 14.0) match.Advance(Step);
        TestAssert.Near(11.1, match.Now - death, 0.35, "the life lasts the delay (1 s) and the zombie buff (10 s)");
        TestAssert.True(!ally.Alive && !ally.IsZombie && ally.YorickRevival is null && ally.Health == 0.0, "BBForceDead ends it: dead again, no longer a zombie");
        TestAssert.Equal(1, ally.ChampionDeaths, "without a second death");
        TestAssert.True(Buff(ally, "YorickRAZombie") is null, "the zombie buff is gone");
        match.Runtime.Delivery.Flush();
        TestAssert.True(Sent(wire, 0x61, ally.NetId, 15, 1), "the death packet carried the become-zombie flag");
        TestAssert.True(Sent(wire, 0x1E, ally.NetId), "and the end of the life went out as NpcForceDead");
        NoScriptFailed(match);
    }

    [Check("scripting.bb-yorick-r-a-lethal-hit-on-the-zombie-in-its-second-phase-ends-the-life")]
    public static void ALethalHitOnTheZombieInItsSecondPhaseEndsTheLife()
    {
        using var match = Open();
        var ally = match.Ally;
        ally.Level = 11;
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        CastUltimate(match, ally);
        var death = match.Now;
        KillAlly(match);
        match.Advance(1.4);
        TestAssert.True(ally.IsZombie && Buff(ally, "YorickRAZombie") is not null && Buff(ally, "YorickRADelay") is null, $"the zombie buff has taken over: {Names(ally)}");
        TestAssert.Near(ally.MaxHealth, ally.Health, 1.0, "at full health");
        TestAssert.Equal(0.0, match.Runtime.Combat.HealingReceivedMultiplier(ally), "and no heal reaches it any more (YorickRAZombie.BuffOnHeal)");

        // YorickRAZombie.BuffOnPreMitigationDamage: a hit that is at least the current health removes the buff, and its end forces the death.
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, ally.MaxHealth * 2.0, DamageType.True, DamageSourceKind.Raw), "the lethal hit lands");
        TestAssert.True(!ally.Alive && !ally.IsZombie && ally.YorickRevival is null && ally.Health == 0.0, "the zombie life is over at once");
        TestAssert.True(match.Now - death < 3.0, "long before its ten seconds");
        TestAssert.True(Buff(ally, "YorickRAZombie") is null, "the zombie buff is gone");
        TestAssert.Equal(1, ally.ChampionDeaths, "and the hit is not a second death");
        match.Runtime.Delivery.Flush();
        TestAssert.True(Sent(wire, 0x1E, ally.NetId), "NpcForceDead went out");
        NoScriptFailed(match);
    }

    /// <summary>
    /// Finding 36 (owner's hand test, 24 October 2026): "R: the ghost sometimes produces double basic attack VFX and hit
    /// sound." A swing the server drops in its windup stays the client's current SpellCaster until a 0x39 retires it
    /// (<c>Entity.ClientSwingDropped</c>): the client plays it to its cast frame, with hit particles and sound on a target
    /// that takes no damage. A ghost whose windup was cancelled and that swung again at once sent a second 0x0D with no
    /// stop between, so the dropped swing's hit showed beside the new one. This pins the wire: the dropped swing is
    /// stopped (0x39 with DestroyMissile, 0x10) before the next 0x0D.
    /// </summary>
    [Check("scripting.bb-yorick-r-a-swing-dropped-in-its-windup-is-stopped-before-the-next-swing")]
    public static void ADroppedRevenantSwingIsStoppedBeforeTheNextOne()
    {
        using var match = Open();
        var hero = match.Hero;
        var ally = match.Ally;
        ally.Level = 11;
        var wire = Observe(match, Team.Order, hero);
        match.Advance(0.3);
        var revenant = CastUltimate(match, ally);
        var foe = match.Foe;
        foe.Position = revenant.Position + new Position2(120.0, 0.0);
        foe.Health = foe.MaxHealth = 5000.0;
        match.Runtime.Delivery.Flush();
        wire.Packets.Clear();
        for (var step = 0; step < 300 && !wire.Packets.Any(item => Opcode(item.Packet) == 0x0D && AttackSender(item.Packet) == revenant.NetId); step++)
        {
            match.Advance(0.01);
            match.Runtime.Delivery.Flush();
        }
        // The swing's windup is still ahead of its hit here: a cancel made by a script or a native, with no stop of its own, is the case under test.
        match.Runtime.Combat.InterruptBasicAttack(revenant);
        match.Advance(0.1);
        match.Runtime.Delivery.Flush();
        var sent = wire.Packets
            .Where(item => Opcode(item.Packet) is 0x0D or 0x39 && AttackSender(item.Packet) == revenant.NetId)
            .ToList();
        var timeline = string.Join(" | ", sent.Select(item => Opcode(item.Packet) == 0x39
            ? $"{item.At:0.###}:39/{item.Packet.Plaintext.Span[5]:X2}"
            : $"{item.At:0.###}:0D"));
        var first = sent.FindIndex(item => Opcode(item.Packet) == 0x0D);
        TestAssert.True(first >= 0, $"the revenant swings before its windup is dropped ({timeline})");
        var second = sent.FindIndex(first + 1, item => Opcode(item.Packet) == 0x0D);
        TestAssert.True(second > first, $"the ghost swings again at once after the cancel ({timeline})");
        var stopped = sent.Skip(first + 1).Take(second - first - 1)
            .Any(item => Opcode(item.Packet) == 0x39 && (item.Packet.Plaintext.Span[5] & 0x10) != 0);
        TestAssert.True(stopped, $"the dropped swing is retired (0x39 with DestroyMissile) before the next 0x0D ({timeline})");
        NoScriptFailed(match);
    }

    private static byte Opcode(PlaintextPacket packet) => packet.Plaintext.Span[0];

    /// <summary>The unit a 0x0D or a 0x39 is sent for: its netId at offset 1.</summary>
    private static uint AttackSender(PlaintextPacket packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]);

    // ------------------------------------------------------------------------------------------------------------
    // Heal immunity
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-ghouls-and-the-revenant-cannot-be-healed")]
    public static void TheGhoulsAndTheRevenantTakeNoHealing()
    {
        // BuffOnHeal of the Logic buffs and of YorickRAPetBuff2: the heal times 0.
        foreach (var (script, hook) in new[] { ("YorickDecayedLogic", "BuffOnHeal"), ("YorickRavenousLogic", "BuffOnHeal"), ("YorickSpectralLogic", "BuffOnHeal"), ("YorickRAPetBuff2", "BuffOnHeal") })
            TestAssert.Near(0.0, Blocks(Script(script), hook).First(item => item.Function == "BBMath" && item.Text("DestVar") == "EffectiveHeal").Number("Src2Value")!.Value,
                1e-12, $"{script}: EffectiveHeal = Health x 0");

        using var match = Open();
        var combat = match.Runtime.Combat;
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Omen of Pestilence");
        AwaitGhouls(match, 1);
        match.Advance(0.3);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "Omen of Famine");
        var ghouls = AwaitGhouls(match, 2);
        match.Advance(0.1);
        foreach (var ghoul in ghouls)
            TestAssert.Equal(0.0, combat.HealingReceivedMultiplier(ghoul), $"{ghoul.Name} takes no healing: {Names(ghoul)}");
        TestAssert.Near(1.0, combat.HealingReceivedMultiplier(match.Hero), 1e-9, "Yorick himself does");

        match.Advance(0.3);
        match.Refill();
        TestAssert.Near(1.0, combat.HealingReceivedMultiplier(match.Ally), 1e-9, "the ally does before the ultimate");
        var revenant = CastUltimate(match, match.Ally);
        TestAssert.Equal(0.0, combat.HealingReceivedMultiplier(revenant), $"the revenant takes no healing: {Names(revenant)}");
        TestAssert.Near(1.0, combat.HealingReceivedMultiplier(match.Ally), 1e-9, "the marked ally still does");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Statistics
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-yorick-internal-raw-kills-of-his-pets-are-not-counted-as-damage-he-dealt")]
    public static void TheScriptsOwnBookkeepingDamageIsNoDamageDealt()
    {
        using var match = Open();
        var hero = match.Hero;
        // Nobody within reach of the ghoul: no burst, no taunt, no swing. Everything that happens to it is the scripts' own damage: the decay
        // (DAMAGESOURCE_INTERNALRAW true damage the ghoul deals itself) and the 9999 of the summon marker's end.
        match.Foe.Position = new Position2(9000, 9000);
        var dealtBefore = hero.MatchStatistics.TotalDamageDealt;
        TestAssert.True(match.Cast(1, at: new Position2(6700, 6700)), "Omen of Pestilence at an empty spot");
        var ghoul = AwaitGhouls(match, 1)[0];
        var born = match.Now;
        match.Advance(1.0);
        TestAssert.True(ghoul.Health < ghoul.MaxHealth, "the ghoul rots");
        while (ghoul.Alive && match.Now - born < 8.0) match.Advance(Step);
        TestAssert.False(ghoul.Alive, "and dies by itself");
        TestAssert.True(match.Log.Hits.Any(line => line.Contains(" InternalRaw ", StringComparison.Ordinal)), "its own damage went through the hit pipeline as DAMAGESOURCE_INTERNALRAW");
        TestAssert.True(HitsOn(match, "Foe").Count == 0, "and Yorick hit nobody");
        TestAssert.Near(dealtBefore, hero.MatchStatistics.TotalDamageDealt, 1e-9, "none of it is damage dealt by Yorick");
    }
}
