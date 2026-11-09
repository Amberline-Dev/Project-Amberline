using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Kassadin runs from his shipped scripts (phase 3, new champions, 19 October 2026, W37): Void Stone, Null Sphere, Nether Blade, Force Pulse and Riftwalk.
/// These are the engine-level checks of the pieces the conversion needed (the Force Pulse charge goes to its caster through <c>BBSetBuffCasterUnit</c>, the
/// flags of a spell outside the closure reach <c>BuffOnSpellCast</c>, a dead hero takes no charge); the per-spell numbers are the shipped scripts' and are read in
/// the <c>S-kassadin</c> scenario. None of them was run when they were written (no tests in the conversion); a failing one is more likely a wrong expectation than a bug.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static int KassadinCounter(Entity hero) => hero.Buffs.Where(buff => buff.Name == "ForcePulseCounter").Sum(buff => Math.Max(1, buff.Stacks));

    private static void KassadinLearn(Entity hero)
    {
        hero.SpellRanks[0] = 5;
        hero.SpellRanks[1] = 5;
        hero.SpellRanks[2] = 5;
        hero.SpellRanks[3] = 3;
        hero.Mana = hero.MaxMana;
    }

    // Annie (the enemy, seat 1) casts Disintegrate at Kassadin with no cooldown: one nearby cast of any hero is one charge.
    private static void KassadinChargeBy(Match match, int casts, Entity caster, Entity target)
    {
        for (var i = 0; i < casts; i++)
        {
            caster.SpellCooldowns[0] = 0.0;
            caster.Mana = caster.MaxMana;
            match.Cast(caster == match.Hero(1) ? 1 : 2, 0, target);
            match.Runtime.Tick(0.5);
        }
    }

    // A direction from the point along which the ground is open for 1700 units (the blinks of the Riftwalk check go along it).
    private static Position2 KassadinOpenDirection(Match match, Position2 origin)
    {
        for (var degrees = 0; degrees < 360; degrees += 15)
        {
            var direction = new Position2(Math.Cos(degrees * Math.PI / 180.0), Math.Sin(degrees * Math.PI / 180.0));
            var open = true;
            for (var step = 50.0; step <= 1700.0 && open; step += 50.0)
                open = match.Runtime.Navigation.IsMovementWalkable(new Position2(origin.X + direction.X * step, origin.Z + direction.Z * step));
            if (open) return direction;
        }
        throw new InvalidOperationException("no open direction of 1700 units near " + origin);
    }

    [Check("scripting.bb-kassadin-module-binds-the-shipped-closure")]
    public static void KassadinClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Kassadin");
        var needed = new[]
        {
            "CharScriptKassadin", "NullLance", "NetherBlade", "NetherBladeBuff", "ForcePulse", "forcepulsechaos", "ForcePulseCanCast", "ForcePulseCounter",
            "RiftWalk", "VoidStone", "VoidStoneAttackSpeedBoost", "Slow",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Kassadin closure");
        var hero = new Entity { ChampionName = "Kassadin" };
        TestAssert.Equal(["NullLance", "NetherBlade", "ForcePulse", "RiftWalk"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Kassadin.inibin");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Kassadin");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => needed.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Kassadin hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-kassadin-e-starts-sealed-and-the-sixth-nearby-cast-opens-it")]
    public static void KassadinForcePulseChargesByCastsNearby()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            // Annie's fifth spell stuns its target (Pyromania): the enemy Annie casts at the ally Annie, so that Kassadin can cast.
            using var match = new Match("Kassadin", team, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
            var kassadin = match.Hero(0);
            var annie = match.Hero(1);
            var ally = match.Hero(2);
            KassadinLearn(kassadin);
            TestAssert.True(kassadin.SpellSeals.IsSealed(false, 2), $"{team}: CharOnActivate sealed E");
            match.Runtime.Tick(1.2);
            // The buff is added to everyone in 1800, enemies and himself included; the Chaos team gets the second copy of the script.
            var expected = team == Team.Order ? "ForcePulse" : "forcepulsechaos";
            TestAssert.True(annie.Buffs.Any(buff => buff.Name == expected) && kassadin.Buffs.Any(buff => buff.Name == expected),
                $"{team}: {expected} is on the enemy and on Kassadin himself");
            KassadinChargeBy(match, 5, annie, ally);
            TestAssert.Equal(5, KassadinCounter(kassadin), $"{team}: five enemy casts are five charges");
            TestAssert.True(kassadin.SpellSeals.IsSealed(false, 2) && !kassadin.Buffs.Any(buff => buff.Name == "ForcePulseCanCast"), $"{team}: not ready yet");
            KassadinChargeBy(match, 1, annie, ally);
            TestAssert.Equal(0, KassadinCounter(kassadin), $"{team}: the sixth stack ends the counter (ForcePulseCounter.OnBuffActivate)");
            TestAssert.True(kassadin.Buffs.Any(buff => buff.Name == "ForcePulseCanCast") && !kassadin.SpellSeals.IsSealed(false, 2), $"{team}: E is open");
            KassadinChargeBy(match, 2, annie, ally);
            TestAssert.Equal(0, KassadinCounter(kassadin), $"{team}: no further charge while E is ready");
            kassadin.SpellCooldowns[2] = 0.0;
            match.CastDirected(0, 2, kassadin.Position, kassadin.Position + new Position2(300, 0)); // a cone: the origin is the caster, the aim a second point
            match.Runtime.Tick(0.6);
            TestAssert.True(kassadin.SpellSeals.IsSealed(false, 2) && !kassadin.Buffs.Any(buff => buff.Name == "ForcePulseCanCast"), $"{team}: the cast spent the charge and sealed E again");
        }
    }

    [Check("scripting.bb-kassadin-the-charge-goes-to-kassadin-and-never-to-the-caster")]
    public static void KassadinChargeNamesTheBuffCasterNotTheCastingHero()
    {
        using var match = new Match("Kassadin", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var kassadin = match.Hero(0);
        var enemy = match.Hero(1);
        var ally = match.Hero(2);
        match.Runtime.Tick(1.2);
        // Disintegrate hits enemies only: Annie (the enemy) casts at Kassadin, Annie (the ally) at the enemy Annie.
        KassadinChargeBy(match, 6, enemy, kassadin);
        KassadinChargeBy(match, 6, ally, enemy);
        foreach (var other in new[] { enemy, ally })
        {
            TestAssert.False(other.Buffs.Any(buff => buff.Name is "ForcePulseCounter" or "ForcePulseCanCast"), "the casting hero holds no charge (BBSetBuffCasterUnit names the buff's caster as Attacker)");
            TestAssert.False(other.SpellSeals.IsSealed(false, 2), "and its E slot was not sealed when a stray ready buff ended");
        }
        TestAssert.True(kassadin.Buffs.Any(buff => buff.Name == "ForcePulseCanCast") || KassadinCounter(kassadin) > 0, "the charges are Kassadin's");
    }

    [Check("scripting.bb-kassadin-a-spell-that-does-not-trigger-casts-gives-no-charge")]
    public static void KassadinForcePulseReadsTheCastSpellsShippedFlags()
    {
        // Sivir's Spiral Blade says DoesntTriggerSpellCasts; her script is not in Kassadin's closure, so the flag is read from the shipped file.
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Sivir");
        var kassadin = match.Hero(0);
        var sivir = match.Hero(1);
        match.Runtime.Tick(1.2);
        for (var i = 0; i < 3; i++)
        {
            sivir.SpellCooldowns[0] = 0.0;
            sivir.Mana = sivir.MaxMana;
            match.CastGround(1, sivir.Position + new Position2(-300, 0), slot: 0);
            match.Runtime.Tick(0.5);
        }
        TestAssert.Equal(0, KassadinCounter(kassadin), "Spiral Blade charges nothing");
        sivir.SpellCooldowns[2] = 0.0;
        sivir.Mana = sivir.MaxMana;
        match.Cast(1, 2, sivir);
        match.Runtime.Tick(0.5);
        TestAssert.Equal(1, KassadinCounter(kassadin), "a cast that does trigger casts (Spell Shield) is one charge");
    }

    [Check("scripting.bb-kassadin-a-cast-more-than-five-seconds-after-leaving-range-gives-no-charge")]
    public static void KassadinForcePulseLastsFiveSecondsAfterTheRangeIsLeft()
    {
        // Both Annies move 2600 away together: the enemy Annie casts at the ally Annie beside her (Disintegrate reaches 625).
        using var match = new Match("Kassadin", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        var ally = match.Hero(2);
        match.Runtime.Tick(1.2);
        annie.Position = new Position2(kassadin.Position.X + 2600.0, kassadin.Position.Z);
        ally.Position = new Position2(kassadin.Position.X + 2750.0, kassadin.Position.Z);
        match.Runtime.Tick(2.0);
        KassadinChargeBy(match, 1, annie, ally);
        TestAssert.Equal(1, KassadinCounter(kassadin), "the buff she wore (5 s) still counts two seconds after she left the 1800");
        match.Runtime.Tick(6.0);
        TestAssert.False(annie.Buffs.Any(buff => buff.Name == "ForcePulse"), "the buff ended");
        KassadinChargeBy(match, 1, annie, ally);
        TestAssert.Equal(1, KassadinCounter(kassadin), "and a cast out of range charges nothing");
    }

    [Check("scripting.bb-kassadin-a-dead-kassadin-takes-no-charge")]
    public static void KassadinCorpseDoesNotCollectCharges()
    {
        using var match = new Match("Kassadin", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        var ally = match.Hero(2);
        match.Runtime.Tick(1.2);
        match.Runtime.Combat.Kill(kassadin, annie.NetId);
        match.Runtime.Tick(0.1);
        TestAssert.False(kassadin.Alive, "he died");
        // The heroes around the corpse still wear his ForcePulse (5 s): the enemy Annie casts at the living ally, seven times inside the window.
        for (var i = 0; i < 7; i++)
        {
            annie.SpellCooldowns[0] = 0.0;
            annie.Mana = annie.MaxMana;
            match.TryCast(1, 0, ally);
            match.Runtime.Tick(0.4);
        }
        TestAssert.False(kassadin.Buffs.Any(buff => buff.Name is "ForcePulseCounter" or "ForcePulseCanCast"), "the corpse holds no charge (a dead hero takes no new buff that does not persist through death)");
        TestAssert.True(kassadin.SpellSeals.IsSealed(false, 2), "and E stays sealed for the respawn");
    }

    [Check("scripting.bb-kassadin-riftwalk-blinks-700-and-stacks-cost-and-damage")]
    public static void KassadinRiftwalkClampsAndStacks()
    {
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        KassadinLearn(kassadin);
        // Level 18: the pool of a level 1 Kassadin (275) cannot pay the third and fourth Riftwalk (300 and 400).
        var required = ProgressionRules.ExperienceThreshold(18) - kassadin.Experience;
        if (required > 0.0) match.Runtime.Combat.AwardHeroProgress(kassadin, experience: required);
        match.Runtime.HeroStats.Refresh(kassadin);
        annie.GodMode = false;
        var dir = KassadinOpenDirection(match, kassadin.Position);
        var cost = 0.0;
        var damage = new List<double>();
        for (var cast = 0; cast < 3; cast++)
        {
            kassadin.SpellCooldowns[3] = 0.0;
            kassadin.Mana = kassadin.MaxMana;
            var from = kassadin.Position;
            // The foe stands 120 past the landing, inside the 270 of the burst (batch 728: a foe on the landing would not move it either).
            annie.Position = new Position2(from.X + dir.X * 370.0, from.Z + dir.Z * 370.0);
            annie.Health = annie.MaxHealth;
            var health = annie.Health;
            var mana = kassadin.Mana;
            match.CastGround(0, new Position2(from.X + dir.X * 250.0, from.Z + dir.Z * 250.0), slot: 3);
            match.Runtime.Tick(0.7);
            cost = mana - kassadin.Mana;
            damage.Add((health - annie.Health) * (100.0 + annie.SpellBlock) / 100.0);
            TestAssert.Near(250.0, Math.Sqrt(Math.Pow(kassadin.Position.X - from.X, 2) + Math.Pow(kassadin.Position.Z - from.Z, 2)), 3.0, $"cast {cast + 1}: the blink lands on the point (inside 700)");
            TestAssert.Near(100.0 * (cast + 1), cost, 6.0, $"cast {cast + 1}: the cost is 100 per stack plus the base (regeneration allowed)");
        }
        TestAssert.True(damage[1] > damage[0] * 1.8 && damage[2] > damage[0] * 2.7, "the burst grows with every stack: 120, 240, 360 at rank 3");
        kassadin.SpellCooldowns[3] = 0.0;
        kassadin.Mana = kassadin.MaxMana;
        var before = kassadin.Position;
        match.CastGround(0, new Position2(before.X + dir.X * 2000.0, before.Z + dir.Z * 2000.0), slot: 3);
        match.Runtime.Tick(0.7);
        // The script clamps a point 700 or more away to 700 along the facing; a landing in rock would go to the nearest floor, so the distance is a ceiling here.
        var blinked = Math.Sqrt(Math.Pow(kassadin.Position.X - before.X, 2) + Math.Pow(kassadin.Position.Z - before.Z, 2));
        TestAssert.True(blinked is > 300.0 and <= 701.0, $"a point 2000 away is clamped to 700 along the facing (moved {blinked:0.0})");
        match.Runtime.Tick(8.5);
        TestAssert.False(kassadin.Buffs.Any(buff => buff.Name == "RiftWalk"), "the stacks end together after 8 s");
        TestAssert.Equal(0.0, kassadin.SpellParCostIncrements.GetValueOrDefault(3), "and the cost is the base again (RiftWalk.OnBuffDeactivate)");
    }

    [Check("scripting.bb-kassadin-void-stone-takes-15-percent-off-magic-damage-only")]
    public static void KassadinVoidStoneReducesMagicDamage()
    {
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        match.Runtime.Tick(0.5);
        // The Match helper gives every hero 5000 health; the first stat refresh (a buff add) would clamp it to the real maximum in the middle of a hit.
        match.Runtime.HeroStats.Refresh(kassadin);
        kassadin.Health = kassadin.MaxHealth;
        double Taken(double raw, DamageType type)
        {
            var health = kassadin.Health;
            match.Runtime.Combat.ApplyHit(annie.NetId, kassadin.NetId, raw, type, DamageSourceKind.Spell);
            return health - kassadin.Health;
        }
        var physical = Taken(100.0, DamageType.Physical);
        var magic = Taken(100.0, DamageType.Magic);
        var truth = Taken(100.0, DamageType.True);
        TestAssert.Near(100.0, truth, 1e-6, "true damage is untouched");
        TestAssert.Near(100.0 * 100.0 / (100.0 + kassadin.Armor), physical, 0.5, "physical damage is only armor-reduced");
        TestAssert.Near(0.85 * 100.0 * 100.0 / (100.0 + kassadin.SpellBlock), magic, 0.5, "magic damage is magic-resistance-reduced and then 15 percent lower (MagicAbsorb 0.85)");
        TestAssert.True(kassadin.Buffs.Any(buff => buff.Name == "VoidStoneAttackSpeedBoost"), "the first magic hit adds the attack speed buff");
    }

    /// <summary>
    /// Batch 583 (the owner's checklist question: one magic hit shows 0 percent or a few?). VoidStone.BuffOnPreDamage cuts magic damage to
    /// CharVars.MagicAbsorb (0.85) and then adds VoidStoneAttackSpeedBoost; its own 0.0015 product has no
    /// DestVarTable, so it never reaches the buff. The boost's OnBuffActivate sets AttackSpeedBoost to 0 and its BuffOnPreDamage adds 0.0015 of each
    /// later magic hit: the first hit gives 0, the second 0.15 percent of the damage after the cut. The setup frees an instance-table
    /// entry older than Void Stone's, which the boost then takes: before the fix the walk ran the boost first and it read the hit before the cut.
    /// </summary>
    [Check("scripting.bb-kassadin-void-stone-boost-starts-at-zero-and-counts-the-cut-damage")]
    public static void KassadinVoidStoneBoostReadsTheCutDamage()
    {
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        match.Runtime.Tick(0.5);
        match.Runtime.HeroStats.Refresh(kassadin);
        kassadin.Health = kassadin.MaxHealth;
        var buffs = ((BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Kassadin")!).Buffs!;
        BuffState Add(string name) => buffs.Add(new BbBuffRequest(kassadin, kassadin, name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BuffType.Aura, 1, 25000.0, 0.0, false, new BbTable()))!;
        match.Runtime.Buffs.RemoveBuff(kassadin, kassadin.Buffs.Single(buff => buff.Name == "VoidStone").Slot);
        var spare = Add("VoidStoneAttackSpeedBoost");
        Add("VoidStone");
        match.Runtime.Buffs.RemoveBuff(kassadin, spare.Slot);
        TestAssert.True(kassadin.Buffs.All(buff => buff.Name != "VoidStoneAttackSpeedBoost"), "no boost before the first hit");

        double Taken(double raw)
        {
            var health = kassadin.Health;
            match.Runtime.Combat.ApplyHit(annie.NetId, kassadin.NetId, raw, DamageType.Magic, DamageSourceKind.Spell);
            return health - kassadin.Health;
        }
        double Boost() => (double)match.ScriptBuff(kassadin, "VoidStoneAttackSpeedBoost").InstanceVars["AttackSpeedBoost"]!;
        Taken(100.0);
        var order = buffs.Instances.Where(item => ReferenceEquals(item.Owner, kassadin)).Select(item => item.Name).ToList();
        TestAssert.True(order.IndexOf("VoidStoneAttackSpeedBoost") < order.IndexOf("VoidStone"),
            "the boost took the freed entry ahead of Void Stone's: " + string.Join(",", order));
        TestAssert.Near(0.0, Boost(), 1e-12, "the hit that adds the boost does not count (its OnBuffActivate starts it at 0)");
        var second = Taken(100.0);
        TestAssert.Near(0.85 * 100.0 * 100.0 / (100.0 + kassadin.SpellBlock), second, 0.5, "the second hit is cut by resistance and Void Stone");
        TestAssert.Near(0.0015 * second, Boost(), 1e-6, "the boost grows by 0.15 percent of the damage after the cut (Void Stone runs first)");
        Taken(100.0);
        TestAssert.Near(0.0015 * 2.0 * second, Boost(), 1e-6, "and stacks with the third");
    }

    [Check("scripting.bb-kassadin-null-sphere-damages-and-silences-by-rank")]
    public static void KassadinNullSphereSilencesForItsRank()
    {
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        KassadinLearn(kassadin);
        annie.Position = new Position2(kassadin.Position.X + 500.0, kassadin.Position.Z);
        var health = annie.Health;
        match.Cast(0, 0, annie);
        match.Runtime.Tick(1.0);
        TestAssert.Near(280.0 * 100.0 / (100.0 + annie.SpellBlock), health - annie.Health, 3.0, "rank 5 deals 280 magic damage at 0 ability power");
        var silence = annie.Buffs.Single(buff => buff.Name == "Silence");
        // BuffState.ExpiresAt counts down the seconds left; the missile needed 0.35 s to arrive, so well over a second of the 2.6 is left a second after the cast.
        TestAssert.True(silence.ExpiresAt is > 1.2 and <= 2.65, "rank 5 silences for 2.6 s");
        annie.SpellCooldowns[0] = 0.0;
        TestAssert.False(match.TryCast(1, 0, kassadin), "a silenced Annie cannot cast");
    }

    [Check("scripting.bb-kassadin-nether-blade-passive-mana-and-active-bonus")]
    public static void KassadinNetherBladeRestoresManaAndAddsMagicDamage()
    {
        using var match = new Match("Kassadin", Team.Order, enemyChampion: "Annie");
        var kassadin = match.Hero(0);
        var annie = match.Hero(1);
        KassadinLearn(kassadin);
        annie.Position = new Position2(kassadin.Position.X + 100.0, kassadin.Position.Z);
        match.Runtime.Tick(1.2);
        TestAssert.True(kassadin.Buffs.Any(buff => buff.Name == "NetherBlade"), "the passive buff is on once W is learned (UpdateSelfBuffActions)");
        kassadin.Mana = 100.0;
        var mana = kassadin.Mana;
        match.Attack(0, annie);
        match.Runtime.Tick(1.5);
        TestAssert.True(kassadin.Mana - mana >= 55.0, "a swing at a champion restores 20 x 3 mana at rank 5");
        kassadin.SpellCooldowns[1] = 0.0;
        match.Cast(0, 1, kassadin);
        match.Runtime.Tick(0.4);
        TestAssert.True(kassadin.Buffs.Any(buff => buff.Name == "NetherBladeBuff"), "the active adds its 5 s buff");
        match.Runtime.Tick(5.5);
        TestAssert.False(kassadin.Buffs.Any(buff => buff.Name == "NetherBladeBuff"), "it ends after 5 s");
    }
}
