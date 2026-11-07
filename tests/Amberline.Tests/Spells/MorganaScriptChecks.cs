using Amberline.Core;
using Amberline.Core.Buffs;
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
/// Morgana runs from her shipped scripts (phase 3, new champions, 19 October 2026, W33c, wave 2): Soul Siphon (the Empathize aura), Dark Binding, Tormented Soil,
/// Black Shield and Soul Shackles. These are the engine-level checks of what the conversion needed (Black Shield asks every adder, Tormented Soil's particle reaches
/// the clients, Soul Shackles' interval reads a whole three seconds); the per-spell numbers are the shipped scripts' and are read in the <c>S-morgana</c> scenario.
/// None of them was run when they were written (no tests in the conversion) unless the record says so; a failing one is more likely a wrong expectation than a bug.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static void MorganaLearn(Entity hero)
    {
        hero.SpellRanks[0] = 5;
        hero.SpellRanks[1] = 5;
        hero.SpellRanks[2] = 5;
        hero.SpellRanks[3] = 3;
        hero.Mana = hero.MaxMana;
    }

    // The Match helper gives every hero 5000 health, which the first stat refresh a buff causes clamps to the real maximum in the middle of a hit. The unit
    // is levelled to 18 (two thousand health or so) and a probe buff makes the refresh happen before the check reads its first health.
    private static void MorganaSettle(Match match, Entity unit)
    {
        match.Runtime.Combat.AwardHeroProgress(unit, experience: ProgressionRules.ExperienceThreshold(18) - unit.Experience);
        match.Runtime.Buffs.AddBuff(unit, "MorganaProbe", BuffType.Internal, 0.2, unit.NetId);
        match.Runtime.Tick(0.5);
        unit.Health = unit.MaxHealth;
    }

    private static int MorganaStacks(Entity unit, string name) => unit.Buffs.Where(buff => buff.Name == name).Sum(buff => Math.Max(1, buff.Stacks));

    // The shipped disable types Black Shield's BuffOnAllowAdd names.
    private static readonly BuffType[] MorganaRefusedTypes =
        [BuffType.Fear, BuffType.Net, BuffType.Silence, BuffType.Sleep, BuffType.Slow, BuffType.Snare, BuffType.Stun, BuffType.Taunt, BuffType.Blind, BuffType.Suppression];

    [Check("scripting.bb-morgana-module-binds-the-shipped-closure")]
    public static void MorganaClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Morgana");
        var needed = new[]
        {
            "CharScriptMorgana", "DarkBindingMissile", "TormentedSoil", "BlackShield", "SoulShackles", "EmpathizeAura", "TormentedSoilDebuff", "SoulShacklesOwner",
            "SoulShackleSlow", "Slow",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Morgana closure");
        var hero = new Entity { ChampionName = "Morgana" };
        TestAssert.Equal(["DarkBindingMissile", "TormentedSoil", "BlackShield", "SoulShackles"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Morgana.inibin (Data*Spell1 is DarkBindingMissile; DarkBinding is its Data*ExtraSpell1)");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Morgana");
        // The passive name Empathize has no script of its own (EmpathizeAura is the passive's buff); it is the one dangling reference.
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(["Empathize"], closure.Dangling.Select(item => item.Name).ToArray(), "only the passive's name has no script");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => needed.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Morgana hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-morgana-soul-siphon-is-ten-twenty-thirty-percent-spell-vamp-by-level")]
    public static void MorganaEmpathizeSpellVampFollowsTheLevel()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team);
            var morgana = match.Hero(0);
            match.Runtime.Tick(1.2);
            var rows = new List<(int Level, double Expected)> { (1, 0.1), (6, 0.1), (7, 0.2), (12, 0.2), (13, 0.3), (18, 0.3) };
            foreach (var (level, expected) in rows)
            {
                if (morgana.Level < level)
                    match.Runtime.Combat.AwardHeroProgress(morgana, experience: ProgressionRules.ExperienceThreshold(level) - morgana.Experience);
                match.Runtime.Tick(1.2);
                TestAssert.Equal(level, morgana.Level, $"{team}: level {level} reached");
                TestAssert.True(morgana.Buffs.Any(buff => buff.Name == "EmpathizeAura"), $"{team}: the aura is on her at level {level}");
                TestAssert.Near(expected, morgana.SpellVamp, 1e-9, $"{team}: spell vamp at level {level} (EmpathizeAura.BuffOnLevelUp)");
            }
        }
    }

    [Check("scripting.bb-morgana-dark-binding-damages-nets-and-heals-by-the-aura")]
    public static void MorganaDarkBindingHitsNetsAndSiphons()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team);
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7600, 7000);
            match.Runtime.Tick(1.2);
            MorganaSettle(match, enemy);
            morgana.Health = morgana.MaxHealth * 0.5;
            var morganaHealth = morgana.Health;
            var health = enemy.Health;
            var resistance = enemy.SpellBlock;
            var mana = morgana.Mana;
            TestAssert.True(match.TryCastGround(0, 0, enemy.Position), $"{team}: Dark Binding is accepted");
            match.Runtime.Tick(1.4);
            var expected = 300.0 * 100.0 / (100.0 + resistance);
            TestAssert.Near(expected, health - enemy.Health, 5.0, $"{team}: rank 5 deals 300 magic damage ({resistance} magic resistance; the target regenerates a little)");
            var net = enemy.Buffs.FirstOrDefault(buff => buff.Name == "DarkBindingMissile");
            TestAssert.True(net is { Type: BuffType.Net }, $"{team}: the target is netted");
            TestAssert.True(net!.ExpiresAt is > 2.0 and <= 3.0, $"{team}: the net lasts 3 s at rank 5 (DurationByLevel 2, 2.25, 2.5, 2.75, 3), {net.ExpiresAt} s left");
            TestAssert.Near(120.0, mana - morgana.Mana, 4.0, $"{team}: the cast cost 120 mana (less the regeneration of the 1.4 s)");
            TestAssert.Near(morganaHealth + 0.1 * (health - enemy.Health), morgana.Health, 3.0, $"{team}: Soul Siphon heals a tenth of a spell's damage at level 1");
            match.Runtime.Tick(3.5);
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "DarkBindingMissile"), $"{team}: the net ended");
        }
    }

    [Check("scripting.bb-morgana-dark-binding-stops-at-its-first-enemy-and-passes-an-ally")]
    public static void MorganaDarkBindingLineStopsAtTheFirstEnemy()
    {
        // Variant 1: two enemies on the line, the second behind the first. Variant 2: an ally stands in front of the only enemy.
        foreach (var withAlly in new[] { false, true })
        {
            using var match = withAlly
                ? new Match("Morgana", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Taric")
                : new Match("Morgana", Team.Order, extraEnemy: true, enemyChampion: "Annie", extraEnemyChampion: "Annie");
            var morgana = match.Hero(0);
            var first = match.Hero(1);
            var other = match.Hero(2);
            MorganaLearn(morgana);
            morgana.Position = new Position2(7000, 7000);
            if (withAlly) { other.Position = new Position2(7250, 7000); first.Position = new Position2(7500, 7000); }
            else { first.Position = new Position2(7500, 7000); other.Position = new Position2(7800, 7000); }
            match.Runtime.Tick(1.2);
            var otherHealth = other.Health;
            TestAssert.True(match.TryCastGround(0, 0, new Position2(7900, 7000)), "the cast is accepted");
            match.Runtime.Tick(2.0);
            TestAssert.True(first.Buffs.Any(buff => buff.Name == "DarkBindingMissile"), $"ally={withAlly}: the first enemy is netted");
            TestAssert.False(other.Buffs.Any(buff => buff.Name == "DarkBindingMissile") || other.Health < otherHealth - 1.0,
                withAlly ? "the ally on the line is passed" : "the enemy behind the first is not hit (BBDestroyMissile on the first hit)");
        }
    }

    [Check("scripting.bb-morgana-tormented-soil-ticks-shreds-and-draws-a-ground-effect")]
    public static void MorganaTormentedSoilZone()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team);
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7500, 7000);
            match.Runtime.Tick(1.2);
            MorganaSettle(match, enemy);
            var baseResistance = enemy.SpellBlock;
            match.Clear();
            TestAssert.True(match.TryCastGround(0, 1, enemy.Position), $"{team}: Tormented Soil is accepted");
            var damages = new List<double>();
            var last = enemy.Health;
            var cube = default(Entity);
            var stacks = 0;
            for (var tick = 0; tick < 30 * 8; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                if (enemy.Health < last - 10.0) { damages.Add(last - enemy.Health); last = enemy.Health; }
                else last = Math.Max(last, enemy.Health);
                cube ??= match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "TestCube" && unit.Alive);
                stacks = Math.Max(stacks, MorganaStacks(enemy, "TormentedSoilDebuff"));
            }
            match.Runtime.Delivery.Flush();
            TestAssert.True(cube is not null, $"{team}: a hidden TestCube stood in the zone");
            TestAssert.Equal(5, damages.Count, $"{team}: one damage at once and one a second for four more");
            TestAssert.Equal(5, stacks, $"{team}: five stacks of the shred at most");
            TestAssert.Near(baseResistance, enemy.SpellBlock, 0.01, $"{team}: the shred ends with the debuff");
            TestAssert.False(match.Runtime.World.Entities.Values.Any(unit => unit.Name == "TestCube" && unit.Alive), $"{team}: the cube is gone after the five seconds");
            // Batch 202: the cube is announced with its spawn (Entity.Announced) and the soil was bound to it on the wire: the green one to the owner's
            // team, the red one to the other. Batch 397 (Reconstruction): an effect the script binds to a placemarker cube is sent unbound at the cube's
            // position (BbNativeHandlers.SpellEffectCreate, as Akali's smoke and Brand's ring), so the soil is checked as sent with no bind and no target.
            var green = SpellPackets.BuffNameHash("TormentedSoil_green_tar");
            var red = SpellPackets.BuffNameHash("TormentedSoil_red_tar");
            var cubeNetId = cube!.NetId;
            bool Unbound(int seat, uint hash) => match.Captures[seat].Packets.Any(packet =>
                packet.Plaintext.Span[0] == 0x8C && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == hash &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == 0);
            TestAssert.True(Unbound(0, green), $"{team}: Morgana's client got the green soil, sent unbound at the cube (batch 397)");
            TestAssert.True(Unbound(1, red), $"{team}: the enemy's client got the red soil, sent unbound at the cube (batch 397)");
            TestAssert.False(Unbound(0, red) || Unbound(1, green), $"{team}: and each team only its own colour");
            TestAssert.False(match.Captures[0].Packets.Any(packet => packet.Plaintext.Span[0] == 0x8C &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == cubeNetId), $"{team}: no soil names the cube as its bind (batch 397)");
            // Batch 202, on Order's wire: the cube came with its spawn packet (0x80), its health bar is hidden (0xD7, show 0) and it is retired
            // (NpcDie, 0xA6 or 0x61) when the zone ends. The no-render bit is not read here; it rides the creation's action state.
            var order = match.Captures[0].Packets;
            TestAssert.True(order.Any(packet => packet.Plaintext.Span[0] == 0x80 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == cubeNetId), $"{team}: the cube was announced");
            TestAssert.True(order.Any(packet => packet.Plaintext.Span[0] == 0xD7 && packet.Plaintext.Span[5] == 0 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == cubeNetId), $"{team}: its health bar is hidden");
            TestAssert.True(order.Any(packet => packet.Plaintext.Span[0] is 0xA6 or 0x61 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == cubeNetId), $"{team}: the cube was retired with NpcDie");
            // The persistent soil particle is killed with the cube.
            TestAssert.True(match.Captures[0].Packets.Any(packet => packet.Plaintext.Span[0] == 0x3D), $"{team}: and ended with it");
        }
    }

    /// <summary>
    /// Batch 701 (community report, 7 November 2026: "While using Morgana's W on enemy non-champion units, they healthbar fills up to full but only
    /// visually"). <c>TormentedSoilDebuff</c>'s OnBuffActivate and OnBuffDeactivate shred the target with <c>BBIncPermanentStat
    /// IncPermanentFlatSpellBlockMod</c>; on a unit that is not a hero the engine pushes that unit's full stat block (BbSpellHost.PermanentOnPet), and the
    /// full minion block wrote the maximum health into the current-health slot (LOCAL1 bit 0) as well, so every stack drew a lane minion's and a camp
    /// monster's bar full until the next damage block. Each client's bar is read here as the stock client keeps it: mHP and mMaxHP from every 0xCC block
    /// its sync gate lets through (LOCAL1 bits 0 and 2 of a minion, MAP bits 0 and 2 of a hero) and from EnterLocalVisibility (0xB5).
    /// </summary>
    [Check("scripting.bb-morgana-tormented-soil-keeps-a-non-champions-health-bar-at-its-health")]
    public static void MorganaTormentedSoilKeepsHealthBars()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team);
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            morgana.SpellRanks[1] = 1; // the least damage, so the injured minion lives through several stacks
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7550, 7150);
            var minion = match.Runtime.Waves!.SpawnFixture(enemy.Team, MinionType.Melee, 0, "C", 0, 0);
            minion.Position = new Position2(7500, 7000);
            var monster = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
                UnitType.Monster, Team.Neutral, new Position2(7450, 6880)) { Name = "Wolf", Health = 1200, MaxHealth = 1200, Radius = 50 });
            match.Runtime.Minions!.HaltAI(minion);
            MorganaSettle(match, enemy);
            minion.Position = new Position2(7500, 7000);
            var units = new[] { minion, monster, enemy };
            TestAssert.True(minion.Created && monster.Created, $"{team}: the minion and the monster are on the clients");
            // Half their health off through the damage pipeline, so each client holds an injured bar.
            foreach (var unit in units)
                TestAssert.True(match.Runtime.Combat.ApplyHit(morgana.NetId, unit.NetId, unit.MaxHealth * 0.5, DamageType.True, DamageSourceKind.Spell),
                    $"{team}: {unit.Name} took the opening hit");
            match.Runtime.Delivery.Flush();
            var heroes = match.Runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Hero).Select(unit => unit.NetId).ToHashSet();
            var bars = match.Captures.Select(_ => new Dictionary<uint, (float Hp, float Max)>()).ToArray();
            var synced = match.Captures.Select(_ => new Dictionary<(uint, int), uint>()).ToArray();
            var read = new int[match.Captures.Count];
            void Read()
            {
                for (var seat = 0; seat < match.Captures.Count; seat++)
                {
                    var packets = match.Captures[seat].Packets;
                    for (; read[seat] < packets.Count; read[seat]++) MorganaReadBar(packets[read[seat]].Plaintext.Span, heroes, bars[seat], synced[seat]);
                }
            }
            Read();
            var healthBefore = units.Select(unit => unit.Health).ToArray();
            TestAssert.True(match.TryCastGround(0, 1, new Position2(7500, 7000)), $"{team}: Tormented Soil is accepted");
            var stacks = new int[units.Length];
            for (var tick = 0; tick < 30 * 7; tick++)
            {
                minion.Position = new Position2(7500, 7000); // the lane minion walks on; it is held in the zone
                match.Runtime.Tick(1.0 / 30.0);
                Read();
                for (var index = 0; index < units.Length; index++)
                {
                    var unit = units[index];
                    stacks[index] = Math.Max(stacks[index], MorganaStacks(unit, "TormentedSoilDebuff"));
                    for (var seat = 0; seat < bars.Length; seat++)
                        if (bars[seat].TryGetValue(unit.NetId, out var bar))
                            TestAssert.True(bar.Hp <= unit.Health + 0.02 * unit.MaxHealth,
                                $"{team}: seat {seat}'s bar of {unit.Name} at {tick} ticks holds {bar.Hp} of {bar.Max} while the unit has {unit.Health:0.0} of {unit.MaxHealth:0.0}");
                }
            }
            for (var index = 0; index < units.Length; index++)
            {
                var unit = units[index];
                TestAssert.True(stacks[index] >= 2, $"{team}: {unit.Name} took the shred more than once ({stacks[index]} stacks)");
                TestAssert.True(unit.Health < healthBefore[index] - 20.0, $"{team}: {unit.Name} took the zone's damage");
                for (var seat = 0; seat < bars.Length; seat++)
                    if (bars[seat].TryGetValue(unit.NetId, out var bar))
                        TestAssert.Near(unit.MaxHealth, bar.Max, 1.0, $"{team}: seat {seat}'s bar of {unit.Name} keeps the maximum");
            }
            TestAssert.True(bars.All(bar => bar.ContainsKey(minion.NetId) && bar.ContainsKey(monster.NetId)), $"{team}: both clients drew the minion and the monster");
        }
    }

    // One packet's writes to the client's health bars (see MorganaTormentedSoilKeepsHealthBars): 0xB5 is opcode, net id, mMaxHP, mHP; a 0xCC block
    // writes a registry only past the sync id the client stored for it.
    private static void MorganaReadBar(ReadOnlySpan<byte> bytes, HashSet<uint> heroes, Dictionary<uint, (float Hp, float Max)> bars,
        Dictionary<(uint, int), uint> synced)
    {
        if (bytes.Length >= 13 && bytes[0] == 0xB5)
        {
            bars[System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])] =
                (System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[5..]));
            return;
        }
        Span<uint> masks = stackalloc uint[5];
        if (!CombatPackets.TryReadReplicationBlock(bytes, out var syncId, out var unit, masks)) return;
        var health = heroes.Contains(unit) ? 3 : 1;
        var offset = 15;
        for (var map = 0; map < 5; map++)
        {
            if ((bytes[10] & (1 << map)) == 0) continue;
            offset += 4;
            var applies = map == health && (!synced.TryGetValue((unit, map), out var stored) || stored < syncId);
            if (applies) synced[(unit, map)] = syncId;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((masks[map] & (1u << bit)) == 0) continue;
                if (applies && bit is 0 or 2)
                {
                    var value = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
                    var bar = bars.TryGetValue(unit, out var held) ? held : (0f, 0f);
                    bars[unit] = bit == 0 ? (value, bar.Item2) : (bar.Item1, value);
                }
                offset += 4;
            }
        }
    }

    [Check("scripting.bb-morgana-black-shield-absorbs-magic-damage-only")]
    public static void MorganaBlackShieldAbsorbsMagic()
    {
        using var match = new Match("Morgana", Team.Order);
        var morgana = match.Hero(0);
        var enemy = match.Hero(1);
        MorganaLearn(morgana);
        match.Runtime.Tick(1.2);
        TestAssert.False(match.TryCast(0, 2, enemy), "Black Shield is for friendly champions: a press on an enemy is refused");
        TestAssert.True(match.TryCast(0, 2, morgana), "Black Shield on herself is accepted");
        match.Runtime.Tick(0.6);
        TestAssert.True(morgana.Buffs.Any(buff => buff.Name == "BlackShield"), "the shield is up");
        double Hit(double raw, DamageType type)
        {
            var health = morgana.Health;
            morgana.Armor = 0.0;
            morgana.SpellBlock = 0.0;
            match.Runtime.Combat.ApplyHit(enemy.NetId, morgana.NetId, raw, type, DamageSourceKind.Spell);
            return health - morgana.Health;
        }
        TestAssert.Near(0.0, Hit(200.0, DamageType.Magic), 1e-6, "200 magic into a 355 shield: nothing");
        TestAssert.Near(100.0, Hit(100.0, DamageType.Physical), 1e-6, "physical damage passes");
        TestAssert.Near(50.0, Hit(50.0, DamageType.True), 1e-6, "true damage passes");
        TestAssert.Near(145.0, Hit(300.0, DamageType.Magic), 1e-6, "300 magic into the 155 that is left: 145 through");
        match.Runtime.Tick(0.4);
        TestAssert.False(morgana.Buffs.Any(buff => buff.Name == "BlackShield"), "a spent shield leaves at its next update");
        TestAssert.Near(100.0, Hit(100.0, DamageType.Magic), 1e-6, "and the next hit is whole");
    }

    [Check("scripting.bb-morgana-black-shield-refuses-the-other-teams-disables-whoever-adds-them")]
    public static void MorganaBlackShieldRefusesEnemyControl()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team, ally: true, enemyChampion: "Ryze", allyChampion: "Taric");
            var morgana = match.Hero(0);
            var ryze = match.Hero(1);
            var taric = match.Hero(2);
            MorganaLearn(morgana);
            ryze.SpellRanks[1] = 5;
            morgana.Position = new Position2(7000, 7000);
            ryze.Position = new Position2(7200, 7000);
            taric.Position = new Position2(7400, 7000);
            match.Runtime.Tick(1.2);
            // Control: no shield, Rune Prison roots and the native adds land.
            ryze.Mana = ryze.MaxMana;
            TestAssert.True(match.TryCast(1, 1, taric), $"{team}: Rune Prison is accepted");
            match.Runtime.Tick(0.6);
            TestAssert.True(taric.Buffs.Any(buff => buff.Name == "RunePrison"), $"{team}: without the shield Rune Prison roots Taric");
            foreach (var type in MorganaRefusedTypes)
                TestAssert.True(match.Runtime.Buffs.AddBuff(taric, "Control_" + type, type, 1.0, ryze.NetId) is not null, $"{team}: without the shield a {type} lands");
            match.Runtime.Tick(3.5);
            // Morgana shields Taric.
            morgana.Mana = morgana.MaxMana;
            TestAssert.True(match.TryCast(0, 2, taric), $"{team}: Black Shield on Taric is accepted");
            match.Runtime.Tick(0.6);
            TestAssert.True(taric.Buffs.Any(buff => buff.Name == "BlackShield"), $"{team}: Taric holds the shield");
            ryze.SpellCooldowns[1] = 0.0;
            ryze.Mana = ryze.MaxMana;
            TestAssert.True(match.TryCast(1, 1, taric), $"{team}: Rune Prison is accepted again");
            match.Runtime.Tick(0.6);
            TestAssert.False(taric.Buffs.Any(buff => buff.Name == "RunePrison"), $"{team}: the shield refuses a script add of the other team's module");
            foreach (var type in MorganaRefusedTypes)
                TestAssert.True(match.Runtime.Buffs.AddBuff(taric, "Refused_" + type, type, 1.0, ryze.NetId) is null, $"{team}: the shield refuses a native {type} of the other team");
            TestAssert.True(match.Runtime.Buffs.AddBuff(taric, "Allowed_Stun", BuffType.Stun, 1.0, morgana.NetId) is not null, $"{team}: and lets an ally's stun through");
            TestAssert.True(match.Runtime.Buffs.AddBuff(taric, "Allowed_Dot", BuffType.DamageOverTime, 1.0, ryze.NetId) is not null, $"{team}: and a damage over time, which it does not list");
        }
    }

    [Check("scripting.bb-morgana-soul-shackles-needs-an-enemy-champion-in-600")]
    public static void MorganaSoulShacklesNeedsAChampionInRange()
    {
        using var match = new Match("Morgana", Team.Order);
        var morgana = match.Hero(0);
        var enemy = match.Hero(1);
        MorganaLearn(morgana);
        morgana.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7640, 7000);
        match.Runtime.Tick(1.2);
        TestAssert.False(match.TryCast(0, 3, morgana), "a champion at 640 is inside the 650 area but outside the 600 of the press: refused");
        enemy.Position = new Position2(7560, 7000);
        TestAssert.True(match.TryCast(0, 3, morgana), "at 560 the press is accepted");
    }

    [Check("scripting.bb-morgana-soul-shackles-damages-twice-and-stuns-after-three-seconds")]
    public static void MorganaSoulShacklesSecondDamageAndStun()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Morgana", team);
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7300, 7000);
            match.Runtime.Tick(1.2);
            MorganaSettle(match, enemy);
            var health = enemy.Health;
            var resistance = enemy.SpellBlock;
            TestAssert.True(match.TryCast(0, 3, morgana), $"{team}: Soul Shackles is accepted");
            match.Runtime.Tick(1.0);
            var first = health - enemy.Health;
            TestAssert.Near(325.0 * 100.0 / (100.0 + resistance), first, 3.0, $"{team}: 325 magic at once at rank 3");
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "SoulShackles") && enemy.Buffs.Any(buff => buff.Name == "Slow"), $"{team}: the chain and the slow");
            TestAssert.True(morgana.Buffs.Any(buff => buff.Name == "SoulShacklesOwner"), $"{team}: the owner's marker");
            var stunned = false;
            for (var tick = 0; tick < 30 * 4 && !stunned; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                stunned = enemy.Buffs.Any(buff => buff.Type == BuffType.Stun);
            }
            TestAssert.True(stunned, $"{team}: the chain ends after its three seconds with a stun (OnBuffDeactivate: TimeElapsed >= 3)");
            var stun = enemy.Buffs.First(buff => buff.Type == BuffType.Stun);
            TestAssert.Near(1.5, stun.ExpiresAt, 0.1, $"{team}: 1.5 s at every rank");
            TestAssert.True(health - enemy.Health > first + 0.5 * first, $"{team}: and the second damage came with it");
        }
    }

    [Check("scripting.bb-morgana-soul-shackles-breaks-past-600-of-gap-and-with-the-owner")]
    public static void MorganaSoulShacklesBreaks()
    {
        foreach (var breaks in new[] { "distance", "owner" })
        {
            using var match = new Match("Morgana", Team.Order);
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7300, 7000);
            match.Runtime.Tick(1.2);
            TestAssert.True(match.TryCast(0, 3, morgana), $"{breaks}: Soul Shackles is accepted");
            match.Runtime.Tick(1.2);
            var health = enemy.Health;
            if (breaks == "distance")
                enemy.Position = new Position2(7900, 7000); // 900 apart, about 770 between the bodies (BBDistanceBetweenObjects is edge to edge)
            else
                match.Runtime.Combat.Kill(morgana, enemy.NetId);
            match.Runtime.Tick(0.7);
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "SoulShackles"), $"{breaks}: the chain is gone");
            match.Runtime.Tick(3.5);
            TestAssert.False(enemy.Buffs.Any(buff => buff.Type == BuffType.Stun), $"{breaks}: no stun");
            TestAssert.Near(health, enemy.Health, 5.0, $"{breaks}: and no second damage");
        }
    }

    [Check("scripting.bb-morgana-soul-shackles-stun-meets-the-black-shield-of-the-enemy-morgana")]
    public static void MorganaSoulShacklesStunIsRefusedByAShield()
    {
        // Two Morganas on opposing teams. The second damage of the chain comes first (the script's order) and spends most of the 355 shield, but the shield
        // leaves at its next update, so the stun meets it and is refused; the unshielded control is stunned.
        foreach (var shielded in new[] { false, true })
        {
            using var match = new Match("Morgana", Team.Order, enemyChampion: "Morgana");
            var morgana = match.Hero(0);
            var enemy = match.Hero(1);
            MorganaLearn(morgana);
            MorganaLearn(enemy);
            morgana.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7300, 7000);
            match.Runtime.Tick(1.2);
            if (shielded)
            {
                TestAssert.True(match.TryCast(1, 2, enemy), "the enemy Morgana shields herself");
                match.Runtime.Tick(0.6);
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlackShield"), "and holds the shield");
            }
            TestAssert.True(match.TryCast(0, 3, morgana), "Soul Shackles is accepted");
            var stunned = false;
            for (var tick = 0; tick < 30 * 5; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                stunned |= enemy.Buffs.Any(buff => buff.Type == BuffType.Stun);
            }
            TestAssert.Equal(!shielded, stunned, shielded ? "the shield refuses the chain's stun" : "the unshielded enemy is stunned at the end of the chain");
        }
    }

    [Check("scripting.bb-game-time-keeps-an-interval-of-whole-ticks")]
    public static void BbGameTimeKeepsWholeTickIntervals()
    {
        // The clock adds 1/30 s per tick in a double: 114.56666666666273 and 117.56666666666256 are 90 ticks apart and differ by 2.99999999999983.
        TestAssert.True(BbNativeHandlers.GameTimeSeconds(117.56666666666256) - BbNativeHandlers.GameTimeSeconds(114.56666666666273) >= 3.0,
            "the Soul Shackles interval reads three seconds");
        var clock = 0.0;
        var readings = new List<double>();
        for (var tick = 0; tick < 30 * 600; tick++)
        {
            clock += 1.0 / 30.0;
            readings.Add(BbNativeHandlers.GameTimeSeconds(clock));
        }
        for (var start = 0; start + 90 < readings.Count; start += 7)
            TestAssert.True(readings[start + 90] - readings[start] >= 3.0, $"90 ticks from tick {start} read at least 3 s");
        TestAssert.Near(clock, BbNativeHandlers.GameTimeSeconds(clock), 0.0005, "the snap is half a millisecond at most");
    }
}
