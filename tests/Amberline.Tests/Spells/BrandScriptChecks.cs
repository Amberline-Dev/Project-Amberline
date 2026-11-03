using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Brand runs from his shipped scripts (phase 3, new champions, wave 2, 19 October 2026, W19f): the passive Blaze (BrandAblaze), Sear, Pillar of Flame,
/// Conflagration and Pyroclasm. The figures are the shipped scripts' and inibins'; the scenario <c>S-brand</c> reads every one of them in a headless match too.
/// Brand's ability power comes from an item (80 from a Needlessly Large Rod), because the match helper sets health by field and a stat refresh resets it.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    /// <summary>A random source that hands out the same values again and again (the draws of a shuffle are then fixed).</summary>
    private sealed class CyclingRandom(params double[] values) : IRandomSource
    {
        private int _next;
        public double NextDouble() => values[_next++ % values.Length];
    }

    private static void BrandLearn(Entity hero, int q = 1, int w = 1, int e = 1, int r = 1)
    {
        hero.SpellRanks[0] = q;
        hero.SpellRanks[1] = w;
        hero.SpellRanks[2] = e;
        hero.SpellRanks[3] = r;
        hero.Mana = hero.MaxMana;
    }

    // 80 ability power: the Needlessly Large Rod, then a stat refresh (which also puts Brand's own health at its real maximum).
    private static void BrandGiveAbilityPower(Match match, Entity hero)
    {
        hero.Inventory = [new InventorySlot(1058), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(hero);
        hero.Mana = hero.MaxMana;
    }

    // The burn's last tick is the buff's OnBuffDeactivate, so removing the buff hurts: strip first, then put the health back.
    private static void BrandCleanse(Match match, Entity unit)
    {
        foreach (var buff in unit.Buffs.Where(buff => buff.Name is "BrandAblaze" or "Stun" or "BrandSearParticle" or "BrandConflagration").ToArray())
            match.Runtime.Buffs.RemoveBuff(unit, buff.Slot);
        unit.Health = unit.MaxHealth;
    }

    private static double BrandMitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    /// <summary>Every drop of a unit's health while the match runs for <paramref name="seconds"/> (time since the call, amount); regeneration is a fraction of a point a tick.</summary>
    private static List<(double T, double Damage)> BrandDrops(Match match, Entity victim, double seconds, double step = 1.0 / 30.0)
    {
        var drops = new List<(double T, double Damage)>();
        var last = victim.Health;
        for (var elapsed = step; elapsed <= seconds + 1e-9; elapsed += step)
        {
            match.Runtime.Tick(step);
            if (victim.Health < last - 1.0) drops.Add((elapsed, last - victim.Health));
            last = victim.Health;
        }
        return drops;
    }

    private static Entity BrandEnemyMinion(Match match, Position2 position)
    {
        var minion = new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos, position)
        {
            Name = "BrandProbeMinion", Health = 5000, MaxHealth = 5000, Armor = 0, SpellBlock = 0, Created = true, Visible = true, Radius = 35.0,
        };
        match.Runtime.World.Register(minion);
        return minion;
    }

    [Check("scripting.bb-brand-module-binds-the-shipped-closure")]
    public static void BrandClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Brand");
        var needed = new[]
        {
            "CharScriptBrand", "BrandBlaze", "BrandBlazeMissile", "BrandFissure", "BrandConflagration", "BrandConflagrationMissile", "BrandWildfire",
            "BrandWildfireMissile", "BrandWildfireMissile2", "BrandWildfireMissile3", "BrandWildfireMissile4", "BrandWildfireMissile5", "BrandAblaze",
            "BrandPassive", "BrandPassiveSound", "BrandSearParticle", "BrandScorchAttack", "BrandScorch", "BrandScorchGround",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Brand closure");
        var hero = new Entity { ChampionName = "Brand" };
        TestAssert.Equal(["BrandBlaze", "BrandFissure", "BrandConflagration", "BrandWildfire"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Brand.inibin");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Brand");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(0, closure.Dangling.Count, "and names nothing it does not ship");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => needed.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Brand hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-brand-sear-lights-the-target-and-stuns-a-blazed-one")]
    public static void BrandSearStunsOnlyABlazedTarget()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie");
        var brand = match.Hero(0);
        var annie = match.Hero(1);
        BrandLearn(brand);
        BrandGiveAbilityPower(match, brand);
        annie.Position = new Position2(brand.Position.X + 600.0, brand.Position.Z);
        var ap = brand.AbilityPower;
        TestAssert.Near(80.0, ap, 1e-6, "an Needlessly Large Rod is 80 ability power");
        var drops = BrandDrops(match, annie, 1.0);
        TestAssert.Equal(0, drops.Count, "nothing happens before the cast");
        TestAssert.True(match.TryCastGround(0, 0, annie.Position), "Sear is instant: the cast is accepted at a point");
        drops = BrandDrops(match, annie, 1.0);
        var expected = BrandMitigated(80.0 + 0.65 * ap, annie.SpellBlock);
        TestAssert.True(drops.Count >= 1, "the missile hits");
        TestAssert.Near(expected, drops[0].Damage, 1.5, "rank 1: 80 + 0.65 AP magic damage, magic-resistance reduced");
        TestAssert.True(annie.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the hit lights the target");
        TestAssert.False(annie.Buffs.Any(buff => buff.Name == "Stun"), "a target that was not ablaze is not stunned");
        match.Runtime.Tick(0.3);
        brand.SpellCooldowns[0] = 0.0;
        brand.Mana = brand.MaxMana;
        TestAssert.True(match.TryCastGround(0, 0, annie.Position), "a second Sear");
        match.Runtime.Tick(0.9);
        var stun = annie.Buffs.SingleOrDefault(buff => buff.Name == "Stun");
        TestAssert.True(stun is not null, "the target was ablaze: stunned");
        TestAssert.True(stun!.ExpiresAt is > 0.9 and <= 2.01, $"for two seconds ({stun.ExpiresAt:0.00} s left a moment after the hit)");
        match.Runtime.Tick(2.2);
        TestAssert.False(annie.Buffs.Any(buff => buff.Name == "Stun"), "and no longer");
    }

    [Check("scripting.bb-brand-sear-stops-at-the-first-unit-and-reaches-1100")]
    public static void BrandSearIsALineThatStopsAtTheFirstUnit()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var front = match.Hero(1);
        var behind = match.Hero(2);
        BrandLearn(brand);
        front.Position = new Position2(brand.Position.X + 400.0, brand.Position.Z);
        behind.Position = new Position2(brand.Position.X + 700.0, brand.Position.Z);
        TestAssert.True(match.TryCastGround(0, 0, behind.Position), "the cast");
        match.Runtime.Tick(1.5);
        TestAssert.True(front.Health < front.MaxHealth - 50.0 || front.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the front unit is hit");
        TestAssert.False(behind.Buffs.Any(buff => buff.Name == "BrandAblaze") || behind.Health < behind.MaxHealth - 50.0, "the missile ended there: the unit behind is not touched");
        // The range: a cast aimed 1400 away is clamped to 1050 in front of Brand (BBGetPointByUnitFacingOffset) and the missile flies 1100.
        front.Position = new Position2(brand.Position.X + 3000.0, brand.Position.Z + 3000.0);
        foreach (var (distance, hit) in new[] { (1000.0, true), (1230.0, false) })
        {
            behind.Position = new Position2(brand.Position.X + distance, brand.Position.Z);
            BrandCleanse(match, behind);
            brand.SpellCooldowns[0] = 0.0;
            brand.Mana = brand.MaxMana;
            TestAssert.True(match.TryCastGround(0, 0, new Position2(brand.Position.X + 1400.0, brand.Position.Z)), $"cast aimed 1400 away, unit at {distance}");
            match.Runtime.Tick(1.8);
            var touched = behind.Buffs.Any(buff => buff.Name == "BrandAblaze") || behind.Health < behind.MaxHealth - 50.0;
            TestAssert.Equal(hit, touched, $"a unit {distance} away is {(hit ? "hit" : "out of reach")}");
        }
    }

    /// <summary>
    /// Batch 583 (the record's open list: does he keep walking?). BrandBlaze is an instacast (SpellData*Flags 347140, bit 4) whose SelfExecute casts
    /// the windup child BrandBlazeMissile (BBSpellCast on extra slot 0 with FireWithoutCasting false; its CastFrame 7.5 is 0.25 s,
    /// CantCancelWhileWindingUp). The engine's rule for that shape (batch 28, the owner's Sivir playtest, BbChampionModule.Execute) is that the
    /// press takes the hero's order like a windup cast: the walk stops and he throws standing. Reconstruction: the client creates no SpellCaster
    /// for the instacast parent (TryCastInstaSpell) and none for a child cast without the force-casting bit, so whether retail's server stopped the
    /// walk is not in the client; this pins the engine's answer for the owner's native look.
    /// </summary>
    [Check("scripting.bb-brand-sear-pressed-while-walking-stops-the-walk-for-its-windup-child")]
    public static void BrandSearStopsTheWalk()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie");
        var brand = match.Hero(0);
        BrandLearn(brand);
        var module = (BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Brand")!;
        TestAssert.Equal("BrandBlazeMissile", module.CasterChildSpell(brand, 0), "Sear's windup child");
        TestAssert.True(GameData.Default.Spell("BrandBlaze", 5).IsInstant, "the slot spell is an instacast");
        match.Move(0, new Position2(brand.Position.X, brand.Position.Z + 2000.0));
        match.Runtime.Tick(0.3);
        TestAssert.True(brand.Waypoints.Count > 0, "he is walking");
        TestAssert.True(match.TryCastGround(0, 0, new Position2(brand.Position.X + 800.0, brand.Position.Z)), "Sear pressed on the way");
        match.Runtime.Tick(1.0 / 30.0);
        var stood = brand.Position;
        match.Runtime.Tick(0.2);
        TestAssert.Equal(0, brand.Waypoints.Count, "the press took his walk");
        TestAssert.True(Math.Sqrt(Math.Pow(brand.Position.X - stood.X, 2) + Math.Pow(brand.Position.Z - stood.Z, 2)) < 1.0,
            "he stands through the child's 0.25 s windup");
    }

    [Check("scripting.bb-brand-blaze-burns-two-percent-of-maximum-health-four-times")]
    public static void BrandAblazeBurnsFourTimesAndStopsAtEightyOnAMonster()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie");
        var brand = match.Hero(0);
        var annie = match.Hero(1);
        BrandLearn(brand, e: 1);
        annie.Position = new Position2(brand.Position.X + 400.0, brand.Position.Z);
        TestAssert.True(match.TryCast(0, 2, annie), "Conflagration on Annie");
        // The cast and the missile take 0.4 s at most; then the burn: a tick every 1.05 s and one more when the 4 s run out.
        var drops = BrandDrops(match, annie, 5.2);
        var burn = drops.Skip(1).ToList();
        TestAssert.Equal(4, burn.Count, "four burn ticks after the hit: " + string.Join(" ", drops.Select(drop => $"{drop.T:0.00}:{drop.Damage:0.0}")));
        var expected = BrandMitigated(annie.MaxHealth * 0.02, annie.SpellBlock);
        foreach (var tick in burn) TestAssert.Near(expected, tick.Damage, 1.2, "2 % of the maximum health, magic-resistance reduced");
        TestAssert.True(burn[1].T - burn[0].T is > 1.0 and < 1.12 && burn[2].T - burn[1].T is > 1.0 and < 1.12, "a tick every 1.05 s");
        TestAssert.False(annie.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the buff is gone after four seconds");
        // A monster: 2 % of 20000 would be 400, the script takes at most 80 from a neutral unit.
        var monster = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Monster, UnitType.Monster, Team.Neutral,
            new Position2(brand.Position.X - 400.0, brand.Position.Z))
            { Name = "BrandProbeMonster", Health = 20000, MaxHealth = 20000, Armor = 0, SpellBlock = 0, Created = true, Visible = true };
        match.Runtime.World.Register(monster);
        // The monster is not in the player's view (a unit registered at run time), so Pillar of Flame, a ground cast, lights it.
        brand.SpellCooldowns[1] = 0.0;
        brand.Mana = brand.MaxMana;
        TestAssert.True(match.TryCastGround(0, 1, monster.Position), "Pillar of Flame on the monster");
        var monsterDrops = BrandDrops(match, monster, 5.2);
        TestAssert.Equal(4, monsterDrops.Skip(1).Count(), "four ticks on the monster too: " + string.Join(" ", monsterDrops.Select(drop => $"{drop.T:0.00}:{drop.Damage:0.0}")));
        foreach (var tick in monsterDrops.Skip(1)) TestAssert.Near(80.0, tick.Damage, 1.2, "capped at 80 against a neutral unit");
    }

    [Check("scripting.bb-brand-pillar-of-flame-bursts-after-the-delay-and-adds-a-quarter-on-a-blazed-unit")]
    public static void BrandPillarDelaysAndHitsBlazedUnitsHarder()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var centre = match.Hero(1);
        var edge = match.Hero(2);
        BrandLearn(brand);
        BrandGiveAbilityPower(match, brand);
        var ap = brand.AbilityPower;
        var point = new Position2(brand.Position.X + 700.0, brand.Position.Z);
        centre.Position = point;
        edge.Position = new Position2(point.X, point.Z + 240.0);
        TestAssert.True(match.TryCastGround(0, 1, point), "the ground cast");
        var watch = new[] { centre, edge };
        var last = watch.Select(unit => unit.Health).ToArray();
        var first = new double[2];
        var damage = new double[2];
        for (var tick = 1; tick <= 60; tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            for (var i = 0; i < 2; i++)
            {
                if (watch[i].Health < last[i] - 1.0 && first[i] == 0.0) { first[i] = tick / 30.0; damage[i] = last[i] - watch[i].Health; }
                last[i] = watch[i].Health;
            }
        }
        var expected = BrandMitigated(75.0 + 0.6 * ap, centre.SpellBlock);
        TestAssert.True(first[0] is > 0.7 and < 0.85, $"the pillar bursts after the cast time and half a second ({first[0]:0.00} s)");
        TestAssert.Near(expected, damage[0], 1.5, "rank 1: 75 + 0.6 AP on a unit that is not ablaze");
        TestAssert.Near(expected, damage[1], 1.5, "240 from the centre is inside the 260");
        TestAssert.True(centre.Buffs.Any(buff => buff.Name == "BrandAblaze") && edge.Buffs.Any(buff => buff.Name == "BrandAblaze"), "everyone it touches burns");
        // The second pillar: both are ablaze, 25 % more (93.75 + 0.75 AP).
        brand.SpellCooldowns[1] = 0.0;
        brand.Mana = brand.MaxMana;
        centre.Health = centre.MaxHealth;
        edge.Health = edge.MaxHealth;
        TestAssert.True(match.TryCastGround(0, 1, point), "the second pillar");
        var drops = BrandDrops(match, centre, 1.0);
        var big = drops.OrderByDescending(drop => drop.Damage).First().Damage;
        TestAssert.Near(BrandMitigated(93.75 + 0.75 * ap, centre.SpellBlock), big, 1.5, "a blazed unit takes 25 % more: 93.75 + 0.75 AP");
        // A unit 290 from the centre is outside the 260 of the script.
        edge.Position = new Position2(point.X, point.Z + 290.0);
        BrandCleanse(match, edge);
        BrandCleanse(match, centre);
        brand.SpellCooldowns[1] = 0.0;
        brand.Mana = brand.MaxMana;
        TestAssert.True(match.TryCastGround(0, 1, point), "the third pillar");
        match.Runtime.Tick(1.2);
        TestAssert.True(edge.Health >= edge.MaxHealth - 3.0 && !edge.Buffs.Any(buff => buff.Name == "BrandAblaze"), "290 away is out of the pillar");
        TestAssert.True(centre.Buffs.Any(buff => buff.Name == "BrandAblaze"), "while the centre unit is hit again");
    }

    [Check("scripting.bb-brand-conflagration-spreads-only-from-a-blazed-target")]
    public static void BrandConflagrationSpreadsWithin375OfABlazedTarget()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var target = match.Hero(1);
        var close = match.Hero(2);
        BrandLearn(brand);
        BrandGiveAbilityPower(match, brand);
        var ap = brand.AbilityPower;
        target.Position = new Position2(brand.Position.X + 450.0, brand.Position.Z);
        close.Position = new Position2(target.Position.X, target.Position.Z + 300.0);
        var far = BrandEnemyMinion(match, new Position2(target.Position.X, target.Position.Z - 450.0));
        TestAssert.True(match.TryCast(0, 2, target), "Conflagration");
        var drops = BrandDrops(match, target, 1.2);
        TestAssert.Near(BrandMitigated(70.0 + 0.55 * ap, target.SpellBlock), drops[0].Damage, 1.5, "rank 1: 70 + 0.55 AP");
        TestAssert.True(close.Health >= close.MaxHealth - 3.0 && far.Health >= far.MaxHealth - 3.0, "the target was not ablaze: no spread");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the target burns now");
        brand.SpellCooldowns[2] = 0.0;
        brand.Mana = brand.MaxMana;
        TestAssert.True(match.TryCast(0, 2, target), "the second cast, on a burning target");
        var spread = BrandDrops(match, close, 1.6);
        TestAssert.Near(BrandMitigated(70.0 + 0.55 * ap, close.SpellBlock), spread[0].Damage, 1.5, "the unit 300 away takes the missile (the same damage)");
        TestAssert.True(close.Buffs.Any(buff => buff.Name == "BrandAblaze"), "and burns");
        TestAssert.True(far.Health >= far.MaxHealth - 3.0 && !far.Buffs.Any(buff => buff.Name == "BrandAblaze"), "450 away is out of the 375");
    }

    // Batch 177 part 2, the owner's 24 October finding 13 ("E: the spread to targets has no VFX, though it still deals damage"). The spread is
    // BrandConflagrationMissile, a homing child that is Instacast (SpellData*Flags bit 2): the cast answer alone never builds its missile, because
    // the client's cast handler hands the cast to TryCastInstaSpell, so BrandConflagration_mis.troy never started. Its launch announces it.
    [Check("scripting.bb-brand-conflagration-spread-is-announced-as-a-missile")]
    public static void BrandConflagrationSpreadIsAnnouncedAsAMissile()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var target = match.Hero(1);
        var close = match.Hero(2);
        BrandLearn(brand);
        BrandGiveAbilityPower(match, brand);
        target.Position = new Position2(brand.Position.X + 450.0, brand.Position.Z);
        close.Position = new Position2(target.Position.X, target.Position.Z + 300.0);
        TestAssert.True(match.TryCast(0, 2, target), "the first Conflagration lights the target");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the target burns");
        brand.SpellCooldowns[2] = 0.0;
        brand.Mana = brand.MaxMana;
        match.Clear();
        TestAssert.True(match.TryCast(0, 2, target), "the second cast, on a burning target");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var spread = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x41).Select(Decode)
            .Where(missile => missile.GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() == SpellSystem.ProtocolHash("BrandConflagrationMissile"))
            .ToList();
        TestAssert.Equal(1, spread.Count, "the spread's missile packet is sent once, at its launch");
        var id = spread[0].GetProperty("cast_info").GetProperty("missile_netid").GetUInt32();
        // Batch 582: the Instacast spread has no cast answer; its missile packet is its cast (ProtocolSpellEffectSink.SendInstantMissileCast).
        TestAssert.True(id != 0 && !match.Captures[0].Packets.Where(packet => Opcode(packet) == 0xBD).Select(Decode)
            .Any(cast => cast.GetProperty("cast_info").GetProperty("missile_netid").GetUInt32() == id),
            "and no cast answer names the same missile (it would build a second one under E's SpellCaster)");
        TestAssert.True(close.Buffs.Any(buff => buff.Name == "BrandAblaze"), "the spread still lights the unit 300 away");
    }

    // Batch 177 part 3, the owner's 24 October finding 14 ("R: disappears after hitting the first target, though it still deals damage"). The
    // Pyroclasm's first hit is its own missile, which the cast answer builds. Each bounce is a BrandWildfireMissile child: homing and Instacast,
    // so its cast answer alone built nothing and the bounce flew without BrandWildfire_mis.troy. Every bounce's launch announces its missile.
    [Check("scripting.bb-brand-pyroclasm-bounces-are-announced-as-missiles")]
    public static void BrandPyroclasmBouncesAreAnnouncedAsMissiles()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var annie = match.Hero(1);
        var second = match.Hero(2);
        BrandLearn(brand, r: 1);
        BrandGiveAbilityPower(match, brand);
        annie.Position = new Position2(brand.Position.X + 600.0, brand.Position.Z);
        second.Position = new Position2(annie.Position.X, annie.Position.Z + 300.0);
        match.Clear();
        TestAssert.True(match.TryCast(0, 3, annie), "the Pyroclasm");
        for (var tick = 0; tick < 150; tick++) match.Runtime.Tick(1.0 / 30.0);
        var packets = match.Captures[0].Packets;
        // Batch 582: each Instacast bounce reaches the client as its missile packet alone (no cast answer; ProtocolSpellEffectSink.SendInstantMissileCast).
        var bounces = packets.Where(packet => Opcode(packet) == 0x41).Select(Decode)
            .Where(missile => missile.GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() != SpellSystem.ProtocolHash("BrandWildfire"))
            .Select(missile => missile.GetProperty("cast_info").GetProperty("missile_netid").GetUInt32()).ToList();
        var answered = packets.Where(packet => Opcode(packet) == 0xBD).Select(Decode)
            .Select(cast => cast.GetProperty("cast_info").GetProperty("missile_netid").GetUInt32()).ToHashSet();
        TestAssert.Equal(4, bounces.Distinct().Count(), "the first hit and four bounces: each bounce's missile is announced at its launch");
        TestAssert.False(bounces.Any(answered.Contains), "and no bounce has a cast answer that would build its missile a second time");
    }

    // Batch 177 part 2, retest (owner, 24 October 2026, finding 12): the Pillar's ground ring is missing on the owner's client. The ring (BrandPOF_tar_green
    // for the own team, BrandPOF_tar_red for the other, plus BrandPOF_charge) was bound to the HiddenMinion placemarker that the spawn announces, and a
    // bound effect on that placemarker is not drawn, while the unbound eruption tar at the same point is. The placemarker is announced first, the ring
    // does not bind it, and it keeps it as its target.
    [Check("scripting.bb-brand-pillar-of-flame-ring-waits-for-its-placemarker-render-state")]
    public static void BrandPillarRingWaitsForItsPlacemarkerRenderState()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        BrandLearn(brand);
        BrandGiveAbilityPower(match, brand);
        var point = new Position2(brand.Position.X + 700.0, brand.Position.Z);
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 1, point), "the Pillar of Flame");
        for (var tick = 0; tick < 30; tick++) match.Runtime.Tick(1.0 / 30.0);
        foreach (var (observer, ring) in new[] { (0, "BrandPOF_tar_green"), (1, "BrandPOF_tar_red") })
        {
            var packets = match.Captures[observer].Packets;
            var spawn = packets.FindIndex(packet => Opcode(packet) == 0x80);
            TestAssert.True(spawn >= 0, $"observer {observer}: the placemarker is announced (SpawnMinion)");
            // SpawnMinion: the net id follows the opcode and a 4-byte zero (offset 5).
            var placemarker = BinaryPrimitives.ReadUInt32LittleEndian(packets[spawn].Plaintext.Span[5..]);
            var hash = SpellPackets.BuffNameHash(ring);
            var effects = packets.Select((packet, index) => (packet, index))
                .Where(item => Opcode(item.packet) == 0x8C)
                .SelectMany(item => Decode(item.packet).GetProperty("groups").EnumerateArray()
                    .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash)
                    .SelectMany(group => group.GetProperty("effects").EnumerateArray())
                    .Select(effect => (effect, item.index)))
                .ToList();
            TestAssert.True(effects.Count >= 1, $"observer {observer}: the ring is created");
            foreach (var (effect, index) in effects)
            {
                TestAssert.True(index > spawn, $"observer {observer}: the ring follows its placemarker's announcement");
                // Batch 397 (Reconstruction): the script binds the ring to its cube (BindObjectVar Owner), but a bound effect on the hidden cube never
                // enters the client's view (the A/B on the throwaway client: no ring without this, the green ring at the cast point with it). The ring
                // is sent at the cube's position with no bind and no target, so it draws on its own. The cube's stat block still carries NoRender and ForceRender.
                TestAssert.Equal(0u, effect.GetProperty("bind_netid").GetUInt32(),
                    $"observer {observer}: the ring is sent unbound (batch 397, Reconstruction): bind_netid 0");
                TestAssert.Equal(0u, effect.GetProperty("target_netid").GetUInt32(),
                    $"observer {observer}: its target is unbound too (batch 397): target_netid 0");
                // The client draws particles bound to a hidden unit only with ForceRenderParticles (bit 14) in the unit's action state, next to
                // NoRender (bit 13): both must reach the view in the placemarker's stat block before the ring.
                // OnReplication (0xCC, MinionStats): the unit net id at offset 11, and the ActionState after HP, mana, HP and max mana, at 35
                // (CombatPackets.MinionStats; the decoder's own ActionState label reads the HP bytes).
                var renderStates = packets.Select((packet, i) => (packet, i))
                    .Where(item => Opcode(item.packet) == 0xCC && item.i > spawn && item.i < index && item.packet.Plaintext.Length >= 39)
                    .Where(item => BinaryPrimitives.ReadUInt32LittleEndian(item.packet.Plaintext.Span[11..]) == placemarker)
                    .Select(item => BinaryPrimitives.ReadUInt32LittleEndian(item.packet.Plaintext.Span[35..]))
                    .ToList();
                TestAssert.True(renderStates.Any(state => (state & (1u << 13)) != 0 && (state & (1u << 14)) != 0),
                    $"observer {observer}: the placemarker's render state (NoRender and ForceRenderParticles) reaches the view before the ring");
            }
        }
    }

    // Batch 397 (Reconstruction): Akali's Twilight Shroud binds its team tar effect to the smoke's cube (AkaliSmokeBomb, BindObjectVar Owner). Sent
    // bound, the cube's hidden state keeps the smoke out of the client's view, so the effect is sent unbound at the cube's position, as the Brand ring.
    [Check("scripting.bb-akali-twilight-shroud-smoke-is-sent-unbound-at-its-cube")]
    public static void AkaliSmokeIsSentUnboundAtItsCube()
    {
        using var match = new Match("Akali", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var akali = match.Hero(0);
        akali.SpellRanks[1] = 1;
        akali.Mana = akali.MaxMana;
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 1, akali.Position), "Twilight Shroud is cast at her feet");
        for (var tick = 0; tick < 30; tick++) match.Runtime.Tick(1.0 / 30.0);
        foreach (var (observer, tar) in new[] { (0, "akali_smoke_bomb_tar_team_green"), (1, "akali_smoke_bomb_tar_team_red") })
        {
            var packets = match.Captures[observer].Packets;
            var hash = SpellPackets.BuffNameHash(tar);
            var effects = packets.Where(packet => Opcode(packet) == 0x8C)
                .SelectMany(packet => Decode(packet).GetProperty("groups").EnumerateArray()
                    .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash)
                    .SelectMany(group => group.GetProperty("effects").EnumerateArray()))
                .ToList();
            TestAssert.True(effects.Count >= 1, $"observer {observer}: the smoke's {tar} is created");
            foreach (var effect in effects)
            {
                TestAssert.Equal(0u, effect.GetProperty("bind_netid").GetUInt32(), $"observer {observer}: the smoke is sent unbound (batch 397): bind_netid 0");
                TestAssert.Equal(0u, effect.GetProperty("target_netid").GetUInt32(), $"observer {observer}: its target is unbound too: target_netid 0");
            }
        }
    }

    // Batch 398 (Part 3.1): Karthus's Wall of Pain beam (WallOfPainBeam, shipped) binds to the wall's cube (BindObjectVar Owner) and targets its
    // TargetObjectVar Attacker, the wall's other placemarker cube (SetForceRenderParticles is set on it). Batch 397 sent every placemarker effect with
    // target 0. Only a target that IS the bound cube goes to 0; another unit keeps its net id.
    [Check("scripting.bb-karthus-wall-of-pain-beam-keeps-its-other-cube-as-target")]
    public static void KarthusWallBeamKeepsItsTargetUnit()
    {
        using var match = new Match("Karthus", Team.Order, enemyChampion: "Orianna");
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 1, match.Hero(1).Position), "Wall of Pain is cast at the foe");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var hash = SpellPackets.BuffNameHash("wallofpain__new_beam");
        var effects = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x8C)
            .SelectMany(packet => Decode(packet).GetProperty("groups").EnumerateArray()
                .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash)
                .SelectMany(group => group.GetProperty("effects").EnumerateArray()))
            .ToList();
        TestAssert.True(effects.Count >= 1, "the beam is created");
        foreach (var effect in effects)
        {
            TestAssert.Equal(0u, effect.GetProperty("bind_netid").GetUInt32(), "the beam is sent unbound (batch 397): bind_netid 0");
            var target = effect.GetProperty("target_netid").GetUInt32();
            // Batch 431 (Reconstruction): the beam's end is the other post's point, not its net id (the beam no longer names a unit end).
            TestAssert.Equal(0u, target,
                $"its target is the other cube's point, not a net id: target_netid {target} (batch 398 expected the net id)");
        }
    }

    // Batch 398 (Part 3.2): Chogath's Rupture creates rupture_cas_01 on its cube (OnBuffActivate, BindObjectVar Owner) and never removes it, so the
    // effect is unbound and fire-and-forget. The client ends a bound emitter with its unit, so it must
    // end with the cube's death: a kill (0x3D, the effect's net id at offset 5) reaches the observer.
    [Check("scripting.bb-chogath-rupture-effect-ends-with-its-cube")]
    public static void RuptureEffectEndsWithItsCube()
    {
        using var match = new Match("Chogath", Team.Order, enemyChampion: "Orianna");
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 0, match.Hero(1).Position), "Rupture is cast at the foe");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var hash = SpellPackets.BuffNameHash("rupture_cas_01");
        var created = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x8C)
            .SelectMany(packet => Decode(packet).GetProperty("groups").EnumerateArray()
                .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash)
                .SelectMany(group => group.GetProperty("effects").EnumerateArray()))
            .Select(effect => effect.GetProperty("net_assigned_id").GetUInt32())
            .ToList();
        TestAssert.True(created.Count >= 1, "Rupture's rupture_cas_01 is created");
        for (var tick = 0; tick < 900; tick++) match.Runtime.Tick(1.0 / 30.0);
        var killed = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x3D)
            .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]))
            .ToHashSet();
        foreach (var id in created)
            TestAssert.True(killed.Contains(id), $"effect {id} is killed once its cube is removed (batch 398, Reconstruction)");
    }

    // Batch 404 (Part 1, reproduce): Leona's Solar Flare (LeonaSolarFlare, shipped) lands on a hidden cube. The cube's landing buff runs 0.5 s, its
    // OnBuffDeactivate creates Leona_SolarFlare_tar on the cube (BindObjectVar Owner) and then deals 1000 true damage to the cube, so the cube dies in
    // the same step as the create. The effect is sent unbound at the cube (batch 397), so BbBuffRuntime.OnUnitDeath kills it at the death.
    [Check("scripting.bb-leona-solar-flare-hit-effect-outlives-its-cube-death")]
    public static void LeonaSolarFlareHitEffectOutlivesItsCube() => HitEffectOutlivesItsCube("Leona", "Leona_SolarFlare_tar", "Leona R");

    // Batch 404 (Part 1, reproduce): Xerath's R (XerathArcaneBarrageWrapper, shipped) fires from its launcher cube (XerathArcaneBarrageMinion's
    // TargetExecute): Xerath_E_tar is created on the cube (BindObjectVar Owner, the cube is not a placemarker by name) and the cube then takes 1000
    // true damage, so it dies in that same step.
    [Check("scripting.bb-xerath-arcane-barrage-hit-effect-outlives-its-cube-death")]
    public static void XerathArcaneBarrageHitEffectOutlivesItsCube() => HitEffectOutlivesItsCube("Xerath", "Xerath_E_tar", "Xerath R");

    // Batch 419 (Reconstruction, batch 418 evidence): the R cube dies in the step its hit effect is created. The script creates the effect and then
    // damages the cube to death, but the wire sends that death ahead of the step's creates (ReplicationDelivery.Flush), so the bound create reaches
    // both clients on a dead unit. The effect is sent unbound at the cube's point, and it ends no earlier than the cube's corpse.
    [Check("scripting.bb-xerath-r-hit-effect-is-sent-unbound-at-its-dead-cube")]
    public static void XerathArcaneBarrageHitEffectIsSentUnboundAtItsCube()
    {
        using var match = new Match("Xerath", Team.Order, enemyChampion: "Orianna");
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 3, match.Hero(1).Position), "Xerath R is cast at the foe");
        var hash = SpellPackets.BuffNameHash("Xerath_E_tar");
        var grid = match.Runtime.Navigation;
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var seen = new int[match.Captures.Count];
        var created = new List<(int Observer, uint Id, JsonElement Effect)>();
        var killedAt = new Dictionary<uint, double>();
        Position2? cubeAt = null;
        uint cubeOwner = 0;
        double? death = null;
        for (var tick = 0; tick < 420; tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            var now = match.Runtime.Combat.CurrentTime;
            for (var observer = 0; observer < match.Captures.Count; observer++)
            {
                var packets = match.Captures[observer].Packets;
                for (; seen[observer] < packets.Count; seen[observer]++)
                {
                    var packet = packets[seen[observer]];
                    if (Opcode(packet) == 0x80 && observer == 0 && cubeAt is null)
                    {
                        // Batch 419 follow-up: the scope of the unbound variant is a script- or pet-owned unit, so the cube's owner is measured here.
                        var cube = match.Runtime.World.Get(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]));
                        cubeAt = cube?.Position;
                        cubeOwner = cube?.OwnerNetId ?? 0;
                    }
                    else if (Opcode(packet) == 0x8C)
                        foreach (var group in Decode(packet).GetProperty("groups").EnumerateArray()
                                     .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash))
                            foreach (var effect in group.GetProperty("effects").EnumerateArray())
                                created.Add((observer, effect.GetProperty("net_assigned_id").GetUInt32(), effect));
                    else if (Opcode(packet) == 0x3D && observer == 0)
                        killedAt.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]), now);
                    else if (Opcode(packet) == 0xA6 && observer == 0)
                        death ??= now;
                }
            }
        }
        TestAssert.True(cubeAt is not null, "the R cube is announced (SpawnMinion)");
        TestAssert.True(cubeOwner != 0, $"the R cube is owned by a unit (OwnerNetId != 0, batch 419 follow-up): owner {cubeOwner}, caster {match.Hero(0).NetId}");
        TestAssert.True(death is not null, "the R cube dies (0xA6)");
        TestAssert.True(created.Any(item => item.Observer == 0) && created.Any(item => item.Observer == 1),
            "Xerath_E_tar is created on the caster's and the enemy's clients");
        var expected = FxPackets.CompressPosition(cubeAt!.Value, bounds, (x, z) => grid.HeightAt(new Position2(x, z)));
        foreach (var (observer, id, effect) in created)
        {
            TestAssert.Equal(0u, effect.GetProperty("bind_netid").GetUInt32(),
                $"observer {observer}: the hit effect is sent unbound (batch 419, Reconstruction): bind_netid 0");
            TestAssert.Equal(0u, effect.GetProperty("target_netid").GetUInt32(),
                $"observer {observer}: its target is unbound too: target_netid 0");
            var position = effect.GetProperty("position_compressed");
            TestAssert.Near(expected.X, position[0].GetDouble(), 0);
            TestAssert.Near(expected.Z, position[2].GetDouble(), 0);
        }
        foreach (var (_, id, _) in created.DistinctBy(item => item.Id))
        {
            TestAssert.True(killedAt.TryGetValue(id, out var at), $"effect {id} is killed once its corpse is gone (batch 419, Reconstruction)");
            TestAssert.True(at - death!.Value >= CombatService.CorpseReapSeconds - 0.5,
                $"effect {id} is killed {at - death!.Value:0.00} s after the cube's death; it must outlive the corpse ({CombatService.CorpseReapSeconds} s)");
        }
    }

    /// <summary>
    /// The hit effect may end only after its cube's object is gone: the client keeps a dead unit's object for its corpse (the NpcDie duration; the server
    /// removes the unit at CombatService.CorpseReapSeconds), and an effect bound to that object ends with it. A 0x3D kill sooner than that is a lost
    /// ground effect.
    /// </summary>
    private static void HitEffectOutlivesItsCube(string champion, string effect, string label)
    {
        using var match = new Match(champion, Team.Order, enemyChampion: "Orianna");
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 3, match.Hero(1).Position), $"{label} is cast at the foe");
        var hash = SpellPackets.BuffNameHash(effect);
        var created = new Dictionary<uint, double>();
        var killed = new Dictionary<uint, double>();
        double? death = null;
        var seen = 0;
        for (var tick = 0; tick < 420; tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            var now = match.Runtime.Combat.CurrentTime;
            var packets = match.Captures[0].Packets;
            for (; seen < packets.Count; seen++)
            {
                var packet = packets[seen];
                if (Opcode(packet) == 0x8C)
                {
                    foreach (var group in Decode(packet).GetProperty("groups").EnumerateArray()
                                 .Where(group => group.GetProperty("effect_name_hash").GetUInt32() == hash))
                        foreach (var item in group.GetProperty("effects").EnumerateArray())
                            created.TryAdd(item.GetProperty("net_assigned_id").GetUInt32(), now);
                }
                else if (Opcode(packet) == 0x3D)
                    killed.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]), now);
                else if (Opcode(packet) == 0xA6)
                    death ??= now;
            }
        }
        TestAssert.True(created.Count >= 1, $"{label}: {effect} is created");
        TestAssert.True(death is not null, $"{label}: its cube dies (0xA6)");
        foreach (var id in created.Keys)
        {
            if (!killed.TryGetValue(id, out var at)) continue;
            var outlived = at - death!.Value;
            TestAssert.True(outlived >= CombatService.CorpseReapSeconds - 0.5,
                $"{label}: {effect} (effect {id}) is killed {outlived:0.00} s after its cube's death; it must outlive the corpse ({CombatService.CorpseReapSeconds} s, batch 404)");
        }
    }

    [Check("scripting.bb-brand-pyroclasm-hits-five-times-and-bounces-to-random-units")]
    public static void BrandPyroclasmBouncesFourTimesToRandomUnits()
    {
        // Three candidates around the first target: the hero and two minions. The cycling random makes the shuffles differ from cast to cast.
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", random: new CyclingRandom(0.05, 0.95, 0.4, 0.7, 0.2, 0.55, 0.85, 0.3));
        var brand = match.Hero(0);
        var annie = match.Hero(1);
        BrandLearn(brand, r: 1);
        BrandGiveAbilityPower(match, brand);
        var ap = brand.AbilityPower;
        annie.Position = new Position2(brand.Position.X + 600.0, brand.Position.Z);
        var minionA = BrandEnemyMinion(match, new Position2(annie.Position.X, annie.Position.Z + 250.0));
        var minionB = BrandEnemyMinion(match, new Position2(annie.Position.X + 250.0, annie.Position.Z));
        Entity[] units = [annie, minionA, minionB];
        var names = new[] { "annie", "a", "b" };
        var lit = new Dictionary<string, int> { ["annie"] = 0, ["a"] = 0, ["b"] = 0 };
        for (var cast = 0; cast < 4; cast++)
        {
            foreach (var unit in units) BrandCleanse(match, unit);
            brand.SpellCooldowns[3] = 0.0;
            brand.Mana = brand.MaxMana;
            var health = annie.Health;
            TestAssert.True(match.TryCast(0, 3, annie), $"cast {cast + 1}");
            // The stacks of BrandWildfire on Brand count the hits: the cast is the first, every bounce adds one, the fifth is the last.
            var stacks = 0;
            double? firstHit = null;
            for (var tick = 0; tick < 150; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                stacks = Math.Max(stacks, brand.Buffs.Where(buff => buff.Name == "BrandWildfire").Sum(buff => buff.Stacks));
                if (firstHit is null && annie.Health < health - 20.0) firstHit = health - annie.Health;
            }
            TestAssert.Equal(5, stacks, $"cast {cast + 1}: the cast and four bounces are five hits");
            TestAssert.Near(BrandMitigated(150.0 + 0.5 * ap, annie.SpellBlock), firstHit ?? 0.0, 2.0, $"cast {cast + 1}: the cast hits for 150 + 0.5 AP at rank 1");
            for (var i = 0; i < units.Length; i++)
                if (units[i].Buffs.Any(buff => buff.Name == "BrandAblaze")) lit[names[i]]++;
            match.Runtime.Tick(5.0);
        }
        TestAssert.True(lit["a"] > 0 && lit["b"] > 0 && lit["annie"] == 4,
            $"the bounces reached both minions over four casts (not only the first of the candidate list): {string.Join(", ", lit.Select(pair => pair.Key + "=" + pair.Value))}");
    }

    [Check("scripting.bb-brand-pyroclasm-flies-faster-after-a-blazed-unit")]
    public static void BrandPyroclasmSpeedsUpAfterABlazedUnit()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie", ally: false, extraEnemy: true, extraEnemyChampion: "Annie");
        var brand = match.Hero(0);
        var first = match.Hero(1);
        var second = match.Hero(2);
        BrandLearn(brand);
        first.Position = new Position2(brand.Position.X + 500.0, brand.Position.Z);
        second.Position = new Position2(first.Position.X, first.Position.Z + 400.0);
        // The seconds between the cast's hit on the first unit and the bounce's hit on the second (a rank 1 Pyroclasm hit is 114 here, a burn tick 76).
        double GapOf()
        {
            BrandCleanse(match, second);
            first.Health = first.MaxHealth;
            brand.SpellCooldowns[3] = 0.0;
            brand.Mana = brand.MaxMana;
            TestAssert.True(match.TryCast(0, 3, first), "Pyroclasm");
            double firstHit = 0.0, secondHit = 0.0;
            var lastFirst = first.Health;
            var lastSecond = second.Health;
            for (var tick = 1; tick <= 90; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                if (firstHit == 0.0 && lastFirst - first.Health > 90.0) firstHit = tick / 30.0;
                if (firstHit != 0.0 && secondHit == 0.0 && lastSecond - second.Health > 90.0) secondHit = tick / 30.0;
                lastFirst = first.Health;
                lastSecond = second.Health;
            }
            return secondHit - firstHit;
        }
        BrandCleanse(match, first);
        var plain = GapOf();
        match.Runtime.Tick(6.0);
        // Light the first unit with Sear, then cast again while it burns.
        BrandCleanse(match, first);
        brand.SpellCooldowns[0] = 0.0;
        brand.Mana = brand.MaxMana;
        TestAssert.True(match.TryCastGround(0, 0, first.Position), "Sear lights the first unit");
        match.Runtime.Tick(1.0);
        TestAssert.True(first.Buffs.Any(buff => buff.Name == "BrandAblaze"), "it burns");
        var blazed = GapOf();
        // 400 apart: 0.5 s at the 800 of the first missile, 0.4 s at the 1000 of the second.
        TestAssert.True(plain is > 0.45 and < 0.62, $"the first bounce flies at 800 ({plain:0.00} s for 400)");
        TestAssert.True(blazed is > 0.33 and < 0.47, $"after a blazed unit it flies at 1000 ({blazed:0.00} s for 400)");
    }

    [Check("scripting.bb-brand-passive-carrier-survives-death")]
    public static void BrandPassiveCarrierPersistsThroughDeathAndTheSoundAuraReturns()
    {
        using var match = new Match("Brand", Team.Order, enemyChampion: "Annie");
        var brand = match.Hero(0);
        var annie = match.Hero(1);
        match.Runtime.Tick(1.0);
        TestAssert.True(brand.Buffs.Any(buff => buff.Name == "BrandPassive") && brand.Buffs.Any(buff => buff.Name == "BrandPassiveSound"), "CharOnActivate adds both");
        match.Runtime.Combat.Kill(brand, annie.NetId);
        match.Runtime.Tick(0.2);
        TestAssert.True(brand.Buffs.Any(buff => buff.Name == "BrandPassive"), "the passive carrier persists through death");
        TestAssert.False(brand.Buffs.Any(buff => buff.Name == "BrandPassiveSound"), "the hidden sound aura does not");
    }
}
