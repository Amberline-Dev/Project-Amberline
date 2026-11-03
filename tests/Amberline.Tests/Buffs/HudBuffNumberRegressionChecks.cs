using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Production lifecycle checks for numbered client buff values and item-owned
/// stack state. These checks stop at the server packet boundary; native HUD
/// acceptance remains a separate stock-client gate.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-champion-b6-native-auto-buff-effects")]
    public static void NativeAutoBuffEffectsAreNotExplicitlyDuplicated()
    {
        // Batch 181: these are raw SpellBuffs.Add calls with no script payload, so the wire keeps the binding name
        // (Batch 357: binding stem; the wire name is the binding for every add). Not restated: the rule is applied as built.
        // VFX sweep F1. Literal hashes are from the shipped script filenames
        // and their AutoBuffActivateEffect[1..4] strings (suffix-free for FX).
        var rows = new (string Name, uint Hash, uint[] Effects, bool Explicit)[]
        {
            ("OrianaRedactShield", 0x047DA5C4u, [0x0A87D574u], false),
            ("BlindMonkEOne", 0x0D820F25u, [0x0ED21634u], false),
            ("BlindMonkWOneShield", 0x011C5504u, [0x0AA2F686u], false),
            ("BlindMonkRKick", 0x082DA94Bu, [0x043A9443u], false),
            ("BlindMonkETwoMissile", 0x0C1294B5u, [0x0725AFD5u, 0x0AFDE062u], false),
            ("Pounce", 0x0776C495u, [0x0BF5BFD6u], false),
            ("Prowl", 0x007796DCu, [0x042FC4A6u], false),
            ("PrimalSurge", 0x084D58E5u, [0x0624CD42u], false),
            ("Takedown", 0x081BB17Eu, [0x06EE8F72u], false),
            ("YorickDecayedSlow", 0x05215417u, [0x0888B8E7u], false),
            ("Fear", 0x0006CB82u, [0x088BFE02u], false),
            // Lee Sin's BlindMonkQOne (ground indicator) and BlindMonkWTwo (Iron Will armor) rows and Blitzcrank's ManaBarrier row are gone: they were
            // server-owned persistent effects (BuffEffectService); the scripts create them now (BBSpellEffectCreate in OnBuffActivate, batches 100 and
            // 105), which a bare SpellBuffs.Add does not run.
        };
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var row in rows)
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var caster = match.Hero(0); var target = match.Hero(1);
            TestAssert.True(runtime.GameData.HasClientBuff(row.Name), row.Name + " is a shipped script filename");
            var buff = runtime.SpellBuffs.Add(target, row.Name, BuffType.Internal, 5,
                caster.NetId, canMitigateDuration: false) ?? throw new InvalidOperationException(row.Name);
            runtime.Delivery.Flush();
            AssertPhase("activation", expectCreates: row.Explicit);
            var explicitIds = new[] { buff.Payload.FxNetId, buff.Payload.ExtraFxNetId }.Where(id => id != 0).ToArray();
            TestAssert.Equal(row.Explicit ? 1 : 0, explicitIds.Length, row.Name + " explicit ownership");

            match.Clear();
            runtime.SpellBuffs.AddStacking(target, row.Name, BuffType.Internal, 5, caster.NetId, 1, new BuffPayload());
            runtime.Delivery.Flush();
            AssertPhase("renewal", expectCreates: false);

            match.Clear();
            foreach (var participant in runtime.Session.Participants) participant.View.Reset();
            runtime.Delivery.Flush();
            AssertPhase("reconnect", expectCreates: row.Explicit);

            match.Clear();
            runtime.SpellBuffs.Remove(target, buff.Slot);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var removed = capture.Packets.Single(packet => Opcode(packet) == 0x7F &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == row.Hash);
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(removed.Plaintext.Span[1..]));
                TestAssert.Equal((byte)buff.Slot, removed.Plaintext.Span[5]);
                foreach (var id in explicitIds)
                    TestAssert.Equal(1, capture.Packets.Count(packet => IsVisualKill(packet, id)), row.Name + " explicit retirement");
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x8C &&
                    row.Effects.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]))),
                    row.Name + " removal does not recreate the particle");
            }

            void AssertPhase(string phase, bool expectCreates)
            {
                foreach (var capture in match.Captures)
                {
                    if (phase == "renewal")
                    {
                        // A same-count renewal of a held buff is a silent 0x32 (slot @5, caster @14), never a second 0xBF.
                        var renewed = capture.Packets.Single(packet => Opcode(packet) == 0x32 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
                            packet.Plaintext.Span[5] == (byte)buff.Slot);
                        TestAssert.Equal(caster.NetId, BinaryPrimitives.ReadUInt32LittleEndian(renewed.Plaintext.Span[14..]));
                        TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0xBF &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == row.Hash),
                            row.Name + " renewal is not a re-add");
                    }
                    else
                    {
                    var added = capture.Packets.Single(packet => Opcode(packet) == 0xBF &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == row.Hash);
                    TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(added.Plaintext.Span[1..]));
                    TestAssert.Equal(caster.NetId, BinaryPrimitives.ReadUInt32LittleEndian(added.Plaintext.Span[21..]));
                    TestAssert.Equal((byte)buff.Slot, added.Plaintext.Span[5]);
                    }
                    foreach (var hash in row.Effects)
                    {
                        var effects = capture.Packets.Where(packet => Opcode(packet) == 0x8C &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == hash).ToArray();
                        TestAssert.Equal(expectCreates ? 1 : 0, effects.Length, row.Name + "/" + phase);
                        foreach (var effect in effects)
                        {
                            TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[21..]));
                            TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[29..]));
                            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[12..]));
                            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[16..]));
                        }
                    }
                }
            }
        }
    }

    [Check("spells-champion-tooltip-publication-and-reconnect", Serial = true)]
    public static void ChampionTooltipPublicationAndReconnect()
    {
        // TT-01, 27 Sept 2026: independent CharScript table, A=150, D=90,
        // B=60, P=100. No learned-rank gate; Nidalee/Vayne start unlearned.
        var cases = new (string Champion, ItemTooltipEntry[] Initial, ItemTooltipEntry[] Changed)[]
        {
            ("Annie", [], []),
            ("Blitzcrank", [new(3, 100, 0, 0)], [new(3, 125, 0, 0)]),
            ("Ezreal", [new(0, 150, 20, 0), new(3, 60, 0, 0)], [new(0, 210, 25, 0), new(3, 120, 0, 0)]),
            ("Janna", [], []),
            // Lee Sin is not in this table any more: his tooltip numbers are BBSetSpellToolTipVar of his character script (published when
            // a value changes, HeroStats.ScriptSpellTooltips), not HeroStatsService.ChampionTooltips' one-second callback.
            ("Nidalee", [new(0, 40, 0, 0), new(1, 125, 0, 0), new(2, 150, 0, 0)],
                [new(0, 40, 0, 0), new(1, 125, 0, 0), new(2, 150, 0, 0)]),
            ("Orianna", [], []),
            ("Shaco", [], []),
            // The scripts' GetFlatPhysicalDamageMod is the unit's bonus attack damage, total minus the base at its level (BbSpellHost.BonusAttackDamage,
            // the value the stat block replicates as mFlatPhysicalDamageMod), so after the change below (A=210, D=90) the changed rows read 120, not the
            // probe's flat 90: Vayne's E 0.5 x 120, Yorick's Q, W and E 1.0, 0.8 and 1.0 x 120.
            ("Vayne", [new(0, 60, 0, 0), new(2, 30, 0, 0)], [new(0, 84, 0, 0), new(2, 60, 0, 0)]),
            ("Yorick", [new(0, 60, 0, 0), new(1, 48, 0, 0), new(2, 60, 0, 0)],
                [new(0, 120, 0, 0), new(1, 96, 0, 0), new(2, 120, 0, 0)]),
        };
        foreach (var row in cases)
        {
            var data = new GameData();
            var champion = data.Champion(row.Champion);
            // As in the injected-base-AD combat check, isolate the fixed probe
            // in this match's data cache; never mutate GameData.Default.
            var cache = (Dictionary<string, ChampionData>)(typeof(GameData)
                .GetField("_champions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(data) ?? throw new InvalidOperationException("champion cache missing"));
            cache[row.Champion] = champion with { BaseDamage = 90, DamagePerLevel = 0 };
            using var match = new Match(row.Champion, Team.Order, initializeSpells: false, data: data);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var probe = new BuffState("TooltipStatProbe", new BuffPayload { AttackDamage = 60, AbilityPower = 100 },
                expiresAt: 10000);
            hero.Buffs.Add(probe);
            hero.Inventory = [new InventorySlot(3083), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero);
            // Batch 149 step 1: Warmog's growth is Items/3083.luaobj's own InstanceVars.ExtraHP now — its
            // UpdateSelfBuffStats writes IncFlatHPPoolMod from it and the two BBSetSpellToolTipVar rows the item
            // slot publishes (InventorySlots + 4, the 64 selector below) — not an inventory field. The copy binds
            // on the next item poll, and only then is there an InstanceVars table to write.
            runtime.Spells.Tick(0.01);
            runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(0)!.InstanceVars["ExtraHP"] = 77.0;
            TestAssert.Near(150, hero.AttackDamage, 1e-8, row.Champion + " fixed A");
            TestAssert.Near(60, runtime.HeroStats.Calculate(hero).FlatPhysicalDamageMod, 1e-8, row.Champion + " fixed B");
            TestAssert.Near(100, hero.AbilityPower, 1e-8, row.Champion + " fixed P");

            // Removed with the hand-written table (batch 98): "Vayne activation writes only E" built E (0.8 B) for an entity no script had run on;
            // the numbers are the character script's own writes now, asserted below from the production clock.

            match.Clear();
            runtime.Tick(0.01); // Production late phase runs the immediate script update.
            // The script writes its numbers at activation or in its first update (Nidalee's at activation, before this clear), so the state is the
            // contract and a packet, when the first update sends one, carries the same values.
            foreach (var value in row.Initial)
            {
                var written = hero.ScriptSpellTooltips.GetValueOrDefault(value.Slot) ?? throw new InvalidOperationException(row.Champion + ": no script tooltip for slot " + value.Slot);
                TestAssert.Equal(new[] { value.First, value.Second, value.Third }, written, row.Champion + ": production immediate values of slot " + value.Slot);
            }
            if (match.Captures[0].Packets.LastOrDefault(IsHeroTooltip) is { } immediate)
                TestAssert.True(Carries(immediate, row.Initial), row.Champion + ": production immediate publication carries the script's values");
            match.Clear();
            // B#2: an unchanged calculation sends nothing new. The script's own second now runs on the production clock, and the item slot's
            // once-a-second push carries every champion value, so what is asserted is that no packet carries anything but the current values.
            runtime.Tick(1.2);
            runtime.Delivery.Flush();
            foreach (var unchanged in match.Captures[0].Packets.Where(IsHeroTooltip))
                TestAssert.True(Carries(unchanged, row.Initial), row.Champion + ": an unchanged calculation publishes no other values (B#2)");
            Publish(row.Initial, "first update");
            using var network = new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 3, 8, bindHost: "127.0.0.1"), ownsHost: true);
            Restore(row.Initial, "initial reconnect");

            // Separate A-D=120 from the probe's flat 90 after changing stats: the scripts read the bonus attack damage, A-D (see the table above).
            probe.Payload.AttackDamage = 90;
            probe.Payload.AbilityPower = 125;
            runtime.HeroStats.Refresh(hero);
            hero.AttackDamage = 210;
            runtime.Delivery.Flush();
            // The scripts publish on their own cadence (batches 86 to 112): the character script's periodic update writes BBSetSpellToolTipVar and
            // a changed value is pushed at once (BbNativeHandlers, HeroStatsService.PushItemTooltips); TickSpellTooltips is a no-op. Vayne's update
            // runs twice a second, the others' once a second, so the new values arrive within 0.6 s and 1.2 s of the change.
            match.Clear();
            var published = -1.0;
            for (var elapsed = 0.1; elapsed <= 1.2 + 1e-9; elapsed += 0.1)
            {
                runtime.Tick(0.1);
                runtime.Delivery.Flush();
                if (published < 0 && match.Captures[0].Packets.Where(IsHeroTooltip).Any(packet => Carries(packet, row.Changed))) published = elapsed;
            }
            // The item slot's own once-a-second push (Warmogs' value) carries every champion value too, so the packets of the window are judged by
            // the values they carry: the new ones arrive on the script's cadence and the latest packet never carries anything else.
            if (!row.Changed.SequenceEqual(row.Initial))
            {
                TestAssert.True(published > 0, row.Champion + ": the script update publishes the changed values");
                TestAssert.True(published <= (row.Champion == "Vayne" ? 0.6 : 1.2) + 1e-9,
                    row.Champion + ": the changed values arrive on the script's cadence (" + published.ToString("0.0") + " s)");
            }
            if (match.Captures[0].Packets.LastOrDefault(IsHeroTooltip) is { } latestPacket)
                TestAssert.True(Carries(latestPacket, row.Changed), row.Champion + ": the latest tooltip packet carries the current values");
            Publish(row.Changed, "after the script update");
            Restore(row.Changed, "changed reconnect");

            if (row.Champion == "Blitzcrank")
            {
                // The character script's periodic update still runs for a corpse and writes the tooltip number; the hand-written calculation's
                // one-second callback is replaced by the script's own second.
                match.Clear();
                hero.Alive = false;
                hero.AbilityPower = 150;
                runtime.Tick(1.2);
                runtime.Delivery.Flush();
                AssertValues(match.Captures[0].Packets, [new(3, 150, 0, 0)], "dead with unlearned R");
            }

            if (row.Champion == "Vayne")
            {
                hero.Level = 18;
                hero.SkillPoints = 5;
                hero.AttackDamage = 150;
                foreach (var damage in new[] { 60.0, 67.5, 75.0, 82.5, 90.0 })
                {
                    TestAssert.True(runtime.Spells.RankUp(hero, 0), "Vayne Q rank learns");
                    // Batch 252 (restated from 0.01 s): her character script's bare self hook writes the new Q number on its next 0.25 s poll
                    // line (batch 246, BbSelfUpdate), not on the next tick.
                    runtime.Tick(0.25);
                    // E is 0.5 x the bonus attack damage, A - D = 150 - 90.
                    Publish([new(0, damage, 0, 0), new(2, 30, 0, 0)], "Vayne Q rank");
                }
            }
            if (row.Champion == "Nidalee")
            {
                hero.Level = 18;
                hero.SkillPoints = 3;
                var shown = row.Initial;
                foreach (var values in new ItemTooltipEntry[][]
                {
                    [new(0, 40, 0, 0), new(1, 125, 0, 0), new(2, 150, 0, 0)],
                    [new(0, 70, 0, 0), new(1, 175, 0, 0), new(2, 225, 0, 0)],
                    [new(0, 100, 0, 0), new(1, 225, 0, 0), new(2, 300, 0, 0)],
                })
                {
                    match.Clear();
                    TestAssert.True(runtime.Spells.RankUp(hero, 3), "Nidalee R rank learns");
                    runtime.Delivery.Flush();
                    // The unlearned slot already shows rank 1's numbers, so learning rank 1 changes nothing and sends nothing; the later ranks
                    // publish at once (the script writes them in the rank-up hook).
                    if (!values.SequenceEqual(shown))
                        AssertValues(match.Captures[0].Packets, values, "R rank publishes immediately");
                    shown = values;
                    Restore(values, "rank reconnect");
                }
                var finalValues = new ItemTooltipEntry[] { new(0, 100, 0, 0), new(1, 225, 0, 0), new(2, 300, 0, 0) };
                foreach (var cougar in new[] { true, false, true })
                {
                    hero.SpellCooldowns[3] = 0;
                    match.Clear();
                    match.CastGround(0, hero.Position, 3);
                    runtime.Tick(0.5);
                    TestAssert.Equal(cougar, hero.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"), "Nidalee form changed");
                    var packets = match.Captures[0].Packets;
                    AssertValues(packets, finalValues, "form retains R-derived values");
                    // Batch 108: the form is the script's BBPushCharacterData(OverrideSpells), one 0x9F with the use-spells flag (the client re-initialises its
                    // book and keeps the ranks), and the way back its BBPopCharacterData, a 0x6A; no longer a 0x35 per slot.
                    var swap = cougar ? 0x9F : 0x6A;
                    TestAssert.True(packets.Any(packet => Opcode(packet) == swap), "form swap is a spell-override push or pop");
                    TestAssert.True(packets.FindLastIndex(IsHeroTooltip) > packets.FindLastIndex(packet => Opcode(packet) == swap),
                        "form tooltip follows replacement spell data");
                    Restore(finalValues, "form reconnect");
                }
                match.Clear();
                runtime.SpellBuffs.Remove(hero, hero.Buffs.Single(buff => buff.Name == "AspectOfTheCougar").Slot);
                runtime.Tick(0.01);
                AssertValues(match.Captures[0].Packets, finalValues, "forced human restoration");
                TestAssert.True(match.Captures[0].Packets.FindLastIndex(IsHeroTooltip) >
                    match.Captures[0].Packets.FindLastIndex(packet => Opcode(packet) == 0x6A),
                    "forced human tooltip follows replacement spell data");
                Restore(finalValues, "forced human reconnect");
            }

            bool Carries(PlaintextPacket packet, IReadOnlyList<ItemTooltipEntry> expected)
            {
                var entries = TooltipEntries([packet]).EnumerateArray().ToArray();
                return expected.All(value => entries.Any(entry => entry.GetProperty("selector").GetInt32() == 60 + value.Slot &&
                    Math.Abs(entry.GetProperty("f1").GetDouble() - value.First) < 1e-5 &&
                    Math.Abs(entry.GetProperty("f2").GetDouble() - value.Second) < 1e-5 &&
                    Math.Abs(entry.GetProperty("f3").GetDouble() - value.Third) < 1e-5));
            }

            bool IsHeroTooltip(PlaintextPacket packet) => Opcode(packet) == 0x83 &&
                packet.Plaintext.Length >= 24 && ReadUInt32(packet, 7) == hero.NetId && packet.Plaintext.Span[11] >= 60;

            void AssertValues(IEnumerable<PlaintextPacket> packets, IReadOnlyList<ItemTooltipEntry> expected, string label)
            {
                var latest = packets.LastOrDefault(IsHeroTooltip) ?? throw new InvalidOperationException(row.Champion + ": " + label + ": no tooltip packet was sent");
                var entries = TooltipEntries([latest]);
                TestAssert.Equal(expected.Count + 1, entries.GetArrayLength(), row.Champion + ": " + label + " slot count");
                foreach (var value in expected)
                    AssertTooltip(entries, 60 + value.Slot, value.First, value.Second, value.Third, row.Champion + ": " + label);
                AssertTooltip(entries, 64, 77, 0, 0, "existing item slot coexists");
            }

            void Publish(IReadOnlyList<ItemTooltipEntry> expected, string label)
            {
                match.Clear();
                runtime.HeroStats.Push(hero);
                runtime.Delivery.Flush();
                AssertValues(match.Captures[0].Packets, expected, label);
                AssertValues(match.Captures[1].Packets, expected, label + " visible observer");
                // Exercise the other live adapter as well as HeroStats.Push.
                match.Clear();
                runtime.CombatTransport.SendHeroStats(hero);
                runtime.Delivery.Flush();
                AssertValues(match.Captures[0].Packets, expected, label + " combat transport");
            }

            void Restore(IReadOnlyList<ItemTooltipEntry> expected, string label)
            {
                var packets = network.BuildReconnectState(runtime.Session.Participants[0]).ToList();
                AssertValues(packets, expected, label);
                var tooltipIndex = packets.FindLastIndex(IsHeroTooltip);
                TestAssert.True(tooltipIndex > packets.FindLastIndex(packet => Opcode(packet) is 0x56 or 0x73 or 0x18 or 0x9F),
                    row.Champion + ": reconnect tooltip follows inventory, spellbook, ranks and form");
            }
        }
    }

    [Check("buffs.hud-number.production-lifecycle")]
    public static void ProductionBuffNumbersAndItemStacksFollowLifecycle()
    {
        using var match = new Match("Annie", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var secondEnemy = match.Hero(2);
        hero.Inventory =
        [
            new InventorySlot(3138), // Leviathan
            new InventorySlot(3124), // Rageblade
            new InventorySlot(3091), // Wit's End
            new InventorySlot(3114), // Malady
            new InventorySlot(3131), // Sword of the Divine
            new InventorySlot(3178), // Ionic Spark
        ];
        hero.Gold = 100_000.0;
        runtime.HeroStats.Refresh(hero);
        // Batch 147: Rageblade's stack is Items/3124.luaobj's own ItemOnHitUnit now, so its copy has to reach the
        // inventory slot first (BbItemModule.SyncInventory, one spell update) before the hit below.
        // Batch 149 step 1: the Leviathan stacks are Items/3138.luaobj's own check buff now (LeviathanCheck's
        // BuffOnKill adds the LeviathanStats stacks), and 3138's UpdateSelfBuffActions only re-adds that check buff
        // on its own one-second BBExecutePeriodically — so the window has to open a second after the copy binds.
        runtime.Tick(1.1);
        var maxHealthBeforeStacks = hero.MaxHealth;

        // A real basic hit drives the item callback path and creates the
        // source-side stack buffs plus the recipient-side Malady debuff.
        match.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, 10.0,
            DamageType.Physical, DamageSourceKind.Basic), "production basic hit lands");
        runtime.Delivery.Flush();
        var rageblade = hero.Buffs.Single(buff => buff.Name == "Rageblade");
        var witsEnd = hero.Buffs.Single(buff => buff.Name == "WitsEndCounter");
        var malady = enemy.Buffs.Single(buff => buff.Name == "MaladyCounter");
        TestAssert.Equal(1, rageblade.Stacks, "Rageblade starts at one stack");
        TestAssert.Equal(1, witsEnd.Stacks, "Wit's End starts at one stack");
        TestAssert.Equal(1, malady.Stacks, "Malady starts at one recipient stack");
        AssertBuffTooltip(match.Captures[1].Packets, enemy.NetId, malady.Slot,
            6.0, 0.0, 0.0, "Malady uses native @f1@ value");

        // A real hero kill drives the snowball producer. Two stacks are worth
        // 64 health in patch 126, and the production reconnect projection must
        // carry the same first float.
        TestAssert.True(runtime.Combat.Kill(secondEnemy, hero.NetId),
            "production kill drives Leviathan stack gain");
        runtime.Delivery.Flush();
        var leviathan = hero.Buffs.Single(buff => buff.Name == "LeviathanStats");
        TestAssert.Equal(2, leviathan.Stacks, "Leviathan kill grants two stacks");
        TestAssert.Near(64.0, hero.MaxHealth - maxHealthBeforeStacks, 1e-9,
            "Leviathan applies two 32-point health stacks");
        AssertBuffTooltip(match.Captures[0].Packets, hero.NetId, leviathan.Slot,
            64.0, 0.0, 0.0, "Leviathan publishes 64 health");
        var observer = runtime.Delivery.Observers.Single(item => item.ObserverId == 1);
        var reconnect = runtime.Delivery.Services.BuildBuffSnapshot(hero, observer);
        AssertBuffTooltip(reconnect, hero.NetId, leviathan.Slot,
            64.0, 0.0, 0.0, "reconnect retains Leviathan health");

        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        var open = new byte[] { 0x60, 0, 0, 0, 0 };
        TestAssert.True(Shop(open), "production shop opens at the fountain");

        // Selling the source item is not itself a buff clear. The shipped scripts that want one write their own
        // OnDeactivate (Items/3117.luaobj's removes BootsOfMobilityDebuff); Items/3124.luaobj's OnDeactivate only takes
        // back the copy's own +0.04 attack speed and +7 ability power, and Items/3091, 3131, 3178 and 3181 write none:
        // their rows are the scripts' own timers and lapse once the copy stops renewing them. An already applied
        // recipient debuff is on its own timer either way.
        TestAssert.True(Sell(3), "Malady source item sells");
        TestAssert.True(enemy.Buffs.Contains(malady),
            "recipient Malady remains after source item sale");
        runtime.Delivery.Flush();
        match.Clear();
        TestAssert.True(Sell(1), "Rageblade sells");
        runtime.Delivery.Flush();
        // Batch 303: script = retail. Spells/Rageblade.luaobj (STACKS_AND_RENEWS, Duration 5) has no item test, so the
        // stacks outlive the sale on their own five seconds; the hand-written sale cleanup that stripped them is gone.
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Rageblade"),
            "Rageblade self stacks outlive the sale on their own timer");
        // Batch 357 (restates batch 181): the removal carries the binding Rageblade; GuinsoosRageblade has no script, so the client cannot build it.
        AssertBuffRemovalForObservers(match, hero, "Rageblade", 0,
            "Rageblade sale alone removes no owner HUD buff");
        match.Clear();
        TestAssert.True(Sell(2), "Wit's End sells");
        runtime.Delivery.Flush();
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "WitsEndCounter"),
            "Wit's End sale leaves the script's row on its own five seconds");
        // Batch 357: binding stem. The WitsEnd row's instance is named WitsEndBuff by its BBSpellBuffAdd; the counter is WitsEndCounter.
        AssertBuffRemovalForObservers(match, hero, "WitsEndBuff", 0,
            "Wit's End sale alone removes no owner HUD buff");
        AssertBuffRemovalForObservers(match, hero, "WitsEndCounter", 0,
            "Wit's End sale alone removes no owner HUD buff (the counter)");

        var lightstriker = runtime.SpellBuffs.AddStacking(hero, "LightstrikerBuff",
            BuffType.CombatEnhancer, 8.0, hero.NetId, 1, new BuffPayload());
        var lightningRod = runtime.SpellBuffs.AddStacking(hero, "LightningRodApplicator",
            BuffType.Internal, 1.0, hero.NetId, 1, new BuffPayload());
        TestAssert.True(lightstriker is not null && lightningRod is not null,
            "production creates the 3131 and 3178 self applicators");
        TestAssert.True(Sell(4), "Sword of the Divine sells");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "LightstrikerBuff"),
            "3131 sale leaves its applicator on its own timer");
        TestAssert.True(Sell(5), "Ionic Spark sells");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "LightningRodApplicator"),
            "3178 sale leaves its applicator on its own timer");

        // Dominion-only items cannot be bought on Map1. Seed the existing
        // inventory path to verify shared cleanup without relaxing store rules.
        var seededInventory = hero.Inventory.ToArray();
        seededInventory[1] = new InventorySlot(3181);
        hero.Inventory = seededInventory;
        runtime.HeroStats.Refresh(hero);
        var bloodburster = runtime.SpellBuffs.AddStacking(hero, "OdinBloodbursterBuff",
            BuffType.CombatEnhancer, 4.0, hero.NetId, 7,
            new BuffPayload { AttackDamage = 5.0, LifeSteal = 0.01 });
        TestAssert.True(bloodburster is not null, "production creates the 3181 self stack");
        TestAssert.True(Sell(1), "Sanguine Blade sells");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OdinBloodbursterBuff"),
            "3181 sale leaves its stack on its own timer");

        // Nothing renews those rows any more, so their own timers run out and that lapse is what carries the HUD
        // removal: Spells/WitsEnd.luaobj's BuffOnHitUnit row is a five-second BUFF_STACKS_AND_RENEWS renewed only by
        // the next hit, and the seeded stand-ins above carry the one, four and eight seconds they were given.
        match.Clear();
        runtime.Tick(5.1);
        runtime.Delivery.Flush();
        TestAssert.False(hero.Buffs.Any(buff => buff.Name is "WitsEndCounter" or "LightningRodApplicator"
            or "OdinBloodbursterBuff" or "Rageblade"),
            "rows whose script writes no OnDeactivate lapse on their own timers");
        AssertBuffRemovalForObservers(match, hero, "Rageblade", 1,
            "the lapse removes the Rageblade owner HUD buff");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "LightstrikerBuff"),
            "a row with a longer timer of its own is still inside it");
        // Batch 357: binding stem. WitsEnd and WitsEndCounter both lapse in this tick and each sends its own removal (one per row).
        AssertBuffRemovalForObservers(match, hero, "WitsEndBuff", 1,
            "the lapse removes the Wit's End owner HUD buff");
        AssertBuffRemovalForObservers(match, hero, "WitsEndCounter", 1,
            "the lapse removes the Wit's End counter HUD buff");

        TestAssert.True(Sell(0), "final Leviathan copy sells");
        // Batch 149 step 1: the counter is the LeviathanStats stack count, and a sale clears it the shipped way:
        // the copy stops renewing LeviathanCheck, that check buff
        // lapses 1.2 s later, and the stats buff removes itself from its own BuffOnUpdateStats. The hand-written
        // "clear the counter and the buffs at once" was faster than retail.
        runtime.Tick(1.6);
        TestAssert.Equal(0, LeviathanStacks(),
            "final Leviathan sale resets the counter");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name is "LeviathanStats" or "LeviathanCap"),
            "final Leviathan sale clears icon and stacks");
        var soldReconnect = runtime.Delivery.Services.BuildBuffSnapshot(hero, observer);
        TestAssert.False(soldReconnect.Any(packet => packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                SpellPackets.BuffNameHash("LeviathanCap")), // Batch 181: the add carries the script's BuffName (LeviathanCap)
            "reconnect omits sold Leviathan buff");

        // Re-buying starts clean, while a duplicate copy retains shared stacks
        // until the final copy is sold.
        TestAssert.True(Buy(3138), "Leviathan re-buy succeeds");
        TestAssert.Equal(0, LeviathanStacks(),
            "Leviathan re-buy starts with zero stacks");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "LeviathanStats"),
            "Leviathan re-buy has no stale icon");
        // The fresh copy re-seeds its own BBExecutePeriodically clock, so the check buff that pays the stacks is
        // back one second after the purchase.
        runtime.Tick(1.1);
        runtime.Combat.ScheduleRespawn(secondEnemy);
        TestAssert.True(runtime.Combat.RespawnImmediately(secondEnemy),
            "second enemy respawns for duplicate-copy check");
        TestAssert.True(runtime.Combat.Kill(secondEnemy, hero.NetId),
            "second production kill rebuilds Leviathan stacks");
        runtime.Delivery.Flush();
        TestAssert.True(Buy(3138), "duplicate Leviathan copy buys");
        TestAssert.Equal(2, LeviathanStacks(),
            "duplicate copy retains shared stacks");
        var copies = hero.Inventory.Select((slot, index) => (slot, index))
            .Where(pair => pair.slot?.ItemId == 3138).Select(pair => pair.index).ToArray();
        TestAssert.Equal(2, copies.Length, "two Leviathan copies occupy two slots");
        TestAssert.True(Sell(copies[0]), "one duplicate Leviathan copy sells");
        TestAssert.Equal(2, LeviathanStacks(),
            "duplicate copy keeps shared stacks after one sale");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "LeviathanStats"),
            "duplicate copy keeps the Leviathan icon after one sale");
        TestAssert.True(Sell(copies[1]), "final duplicate Leviathan copy sells");
        runtime.Tick(1.6);
        TestAssert.Equal(0, LeviathanStacks(),
            "final duplicate sale resets shared stacks");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "LeviathanStats"),
            "final duplicate sale removes the Leviathan icon");
        // The ticks above ran past the 3131 stand-in's own eight seconds as well.
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "LightstrikerBuff"),
            "the 3131 applicator lapses on its own timer too");

        int LeviathanStacks() => hero.Buffs.SingleOrDefault(buff => buff.Name == "LeviathanStats")?.Stacks ?? 0;

        bool Shop(byte[] plaintext) => runtime.Session.DispatchShop(new SessionInput(
            1, 1, runtime.Cipher.Encrypt(plaintext), plaintext, true));

        bool Sell(int slot)
        {
            var packet = new byte[6];
            packet[0] = 0x09;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            packet[5] = (byte)(0x80 | slot);
            return Shop(packet);
        }

        bool Buy(int itemId)
        {
            var packet = new byte[9];
            packet[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), checked((uint)itemId));
            return Shop(packet);
        }
    }

    [Check("items-live-snowball-sale-removes-hud")]
    public static void ProductionSnowballSaleRemovesStatsAndCapBuffsForBothObservers()
    {
        foreach (var item in new[]
        {
            (Id: 3041, Stats: "MejaisStats", Cap: "MejaisCap", PerStack: 8.0,
                Stat: "ability power"),
            (Id: 3138, Stats: "LeviathanStats", Cap: "LeviathanCap", PerStack: 32.0,
                Stat: "maximum health"),
            (Id: 3141, Stats: "MuramasaStats", Cap: "MuramasaCap", PerStack: 5.0,
                Stat: "attack damage"),
        })
            AssertSnowballSale(item.Id, item.Stats, item.Cap, item.PerStack, item.Stat);
    }

    private static void AssertSnowballSale(int itemId, string statsName, string capName,
        double perStack, string statName)
    {
        using var match = new Match("Annie", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        // Keep the kill-driven stack exercise at a fixed level. The production
        // kill path awards XP, so a level transition would contaminate the
        // before/after stat assertions for the item-owned values.
        hero.Level = 18;
        hero.Experience = ProgressionRules.ExperienceThreshold(18);
        hero.Gold = 100_000.0;
        hero.Inventory = [null, null, null, null, null, null];
        runtime.HeroStats.Refresh(hero);
        var unowned = runtime.HeroStats.Calculate(hero);
        hero.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
        runtime.HeroStats.Refresh(hero);
        // Batch 149 step 1: the stacks are the shipped scripts' own now (Spells/MejaisCheck.luaobj and its two
        // siblings' BuffOnKill adds one *Stats stack per add, two adds a hero kill). The check buff that carries
        // those hooks is re-added by the copy's UpdateSelfBuffActions on its own one-second BBExecutePeriodically,
        // so the window has to open a second after the copy binds.
        runtime.Tick(1.1);
        var owned = runtime.HeroStats.Calculate(hero);

        // Each champion kill awards two snowball stacks. Ten live kills reach
        // the shipped 20-stack cap while the first kill gives the partial case.
        ReviveEnemyForObserver();
        match.Clear();
        TestAssert.True(runtime.Combat.Kill(enemy, hero.NetId),
            $"{itemId} production kill 1 grants item stacks");
        runtime.Delivery.Flush();
        TestAssert.Equal(2, StatsStacks(),
            $"{itemId} first kill gives partial stacks");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == capName),
            $"{itemId} partial stacks have no cap buff");
        AssertSnowballStat(hero, owned, itemId, 2, perStack, statName,
            $"{itemId} partial stat");

        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        ReviveEnemyForObserver();
        var open = new byte[] { 0x60, 0, 0, 0, 0 };
        TestAssert.True(Shop(open), $"{itemId} shop opens for partial-stack sale");
        match.Clear();
        TestAssert.True(Buy(itemId), $"{itemId} partial duplicate purchase succeeds");
        runtime.Delivery.Flush();
        match.Clear();
        var partialCopies = ItemCopies();
        TestAssert.Equal(2, partialCopies.Length, $"{itemId} partial duplicate occupies two slots");
        ReviveEnemyForObserver();
        TestAssert.True(Sell(partialCopies[0]), $"{itemId} partial first sale succeeds");
        runtime.Delivery.Flush();
        TestAssert.Equal(2, StatsStacks(),
            $"{itemId} partial sale retains stacks");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == statsName),
            $"{itemId} partial sale retains stats buff");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == capName),
            $"{itemId} partial sale retains no cap buff");
        AssertBuffRemovalForObservers(match, hero, statsName, 0,
            $"{itemId} partial sale keeps stats HUD");
        AssertBuffRemovalForObservers(match, hero, capName, 0,
            $"{itemId} partial sale keeps cap HUD");

        var partialRemaining = ItemCopies();
        TestAssert.Equal(1, partialRemaining.Length, $"{itemId} partial final copy remains");
        ReviveEnemyForObserver();
        match.Clear();
        TestAssert.True(Sell(partialRemaining[0]), $"{itemId} partial final sale succeeds");
        runtime.Delivery.Flush();
        // Batch 149 step 1: the last copy's sale is a lapse, not a clear:
        // LeviathanCheck / MejaisCheck / MuramasaCheck stops being renewed, lapses 1.2 s later and the *Stats buff then
        // removes itself from its own BuffOnUpdateStats — that removal is what carries the HUD packet counted below.
        runtime.Tick(1.6);
        TestAssert.Equal(0, StatsStacks(),
            $"{itemId} partial final sale resets stacks");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName ||
            buff.Name == capName), $"{itemId} partial final sale clears buffs");
        AssertUnownedStat(unowned, hero, itemId, statName,
            $"{itemId} partial final sale removes stat contribution");
        AssertBuffRemovalForObservers(match, hero, statsName, 1,
            $"{itemId} partial final sale removes stats HUD");
        // Batch 357: binding stem. The stats row sends its own name (asserted above); the cap row's own-name removal is not in the
        // partial sale (it is in the final sale below). Batch 181 counted the stats removal under the shared cap name.
        AssertBuffRemovalForObservers(match, hero, capName, 0,
            $"{itemId} partial final sale sends no removal of the cap's own name");

        match.Clear();
        TestAssert.True(Buy(itemId), $"{itemId} clean rebuy starts full-stack case");
        runtime.Delivery.Flush();
        TestAssert.Equal(0, StatsStacks(),
            $"{itemId} clean rebuy starts at zero");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName ||
            buff.Name == capName), $"{itemId} clean rebuy has no stale buffs");
        // The fresh copy re-seeds its own one-second clock, so its check buff is back a second after the purchase.
        runtime.Tick(1.1);

        for (var kill = 0; kill < 10; kill++)
        {
            ReviveEnemyForObserver();
            match.Clear();
            TestAssert.True(runtime.Combat.Kill(enemy, hero.NetId),
                $"{itemId} production kill {kill + 1} grants item stacks");
            runtime.Delivery.Flush();
        }

        TestAssert.Equal(20, StatsStacks(),
            $"{itemId} reaches full stacks");
        var fullStatsBuff = hero.Buffs.Single(buff => buff.Name == statsName);
        TestAssert.Equal(20, fullStatsBuff.Stacks, $"{itemId} full stats buff count");
        TestAssert.Near(20.0 * perStack, fullStatsBuff.Payload.TooltipValue1, 1e-9,
            $"{itemId} full HUD value");
        // The cap is Items/3041, 3138 and 3141's own UpdateSelfBuffActions reacting to a *Stats count of exactly
        // twenty (CO_EQUAL 20), and that row runs on the copy's one-second period.
        runtime.Tick(1.1);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == capName),
            $"{itemId} full stacks add cap buff");
        AssertSnowballStat(hero, owned, itemId, 20, perStack, statName,
            $"{itemId} full stat");

        // The cap effects must change the corresponding gameplay fields, not
        // merely create an internal marker for the HUD.
        if (itemId == 3041)
            TestAssert.Near(owned.CooldownMod - 0.15, hero.CooldownMod, 1e-9,
                "Mejai's full-stack cooldown cap applies");
        if (itemId == 3141)
            TestAssert.True(hero.Speed > owned.MoveSpeed,
                "Sword of the Occult full-stack speed cap applies");

        double fullLeviathanDamage = 0.0;
        if (itemId == 3138)
        {
            ReviveEnemyForObserver();
            match.Clear();
            fullLeviathanDamage = TakePhysicalDamage();
            TestAssert.True(fullLeviathanDamage > 0.0,
                "Leviathan full-stack damage probe lands");
        }

        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        ReviveEnemyForObserver();
        TestAssert.True(Shop([0x60, 0, 0, 0, 0]), $"{itemId} shop opens");

        // A duplicate keeps the shared counter and both owner buffs. The
        // first sale must therefore carry no snowball removal packet.
        match.Clear();
        TestAssert.True(Buy(itemId), $"{itemId} duplicate purchase succeeds");
        runtime.Delivery.Flush();
        match.Clear();
        var copies = ItemCopies();
        TestAssert.Equal(2, copies.Length, $"{itemId} duplicate occupies two slots");
        ReviveEnemyForObserver();
        TestAssert.True(Sell(copies[0]), $"{itemId} first duplicate sale succeeds");
        runtime.Delivery.Flush();
        TestAssert.Equal(20, StatsStacks(),
            $"{itemId} first duplicate sale retains stacks");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == statsName),
            $"{itemId} first duplicate sale retains stats buff");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == capName),
            $"{itemId} first duplicate sale retains cap buff");
        AssertBuffRemovalForObservers(match, hero, statsName, 0,
            $"{itemId} first duplicate sale keeps stats HUD");
        AssertBuffRemovalForObservers(match, hero, capName, 0,
            $"{itemId} first duplicate sale keeps cap HUD");

        // The final copy clears the authoritative counter, both buff objects,
        // their stat contributions, and the immediately visible HUD entries.
        ReviveEnemyForObserver();
        match.Clear();
        TestAssert.True(Sell(copies[1]), $"{itemId} final duplicate sale succeeds");
        runtime.Delivery.Flush();
        // The shipped lapse again: the check buff dies 1.2 s after the last copy leaves, the *Stats buff removes
        // itself at its next BuffOnUpdateStats and the cap from its own periodic row. Those rows are the scripts' own
        // (MejaisCap and MuramasaCap every 0.3 s, LeviathanCap every 0.9 s), so the wait covers the slowest one after
        // the check buff has gone.
        runtime.Tick(2.3);
        TestAssert.Equal(0, StatsStacks(),
            $"{itemId} final sale resets stacks");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName ||
            buff.Name == capName),
            $"{itemId} final sale removes stats and cap buffs");
        AssertUnownedStat(unowned, hero, itemId, statName,
            $"{itemId} final stat removal");
        if (itemId == 3041)
            TestAssert.Near(unowned.CooldownMod, hero.CooldownMod, 1e-9,
                "Mejai's final sale removes cooldown cap");
        if (itemId == 3141)
            TestAssert.Near(unowned.MoveSpeed, hero.Speed, 1e-9,
                "Sword of the Occult final sale removes speed cap");
        if (itemId == 3138)
        {
            var unownedLeviathanDamage = TakePhysicalDamage();
            TestAssert.Near(unownedLeviathanDamage * 0.85, fullLeviathanDamage,
                1e-9, "Leviathan full-stack damage reduction is 15 percent");
        }
        // Batch 357: binding stem. The stats and cap rows each send their own removal in the final sale (batch 181 counted both
        // under the shared cap name).
        AssertBuffRemovalForObservers(match, hero, statsName, 1,
            $"{itemId} final sale removes stats HUD");
        AssertBuffRemovalForObservers(match, hero, capName, 1,
            $"{itemId} final sale removes cap HUD");

        foreach (var observer in runtime.Delivery.Observers)
        {
            var reconnect = runtime.Delivery.Services.BuildBuffSnapshot(hero, observer);
            TestAssert.False(reconnect.Any(packet => HasBuffAdd(packet, hero.NetId, statsName) ||
                    HasBuffAdd(packet, hero.NetId, capName)),
                $"{itemId} reconnect omits sold item buffs for observer {observer.ObserverId}");
        }

        // A fresh purchase is a new equipment transition and starts with no
        // stale stacks or client buff state.
        match.Clear();
        TestAssert.True(Buy(itemId), $"{itemId} rebuy succeeds");
        runtime.Delivery.Flush();
        TestAssert.Equal(0, StatsStacks(),
            $"{itemId} rebuy starts at zero");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == statsName ||
            buff.Name == capName),
            $"{itemId} rebuy has no stale buffs");

        int StatsStacks() => hero.Buffs.SingleOrDefault(buff => buff.Name == statsName)?.Stacks ?? 0;

        int[] ItemCopies() => hero.Inventory.Select((slot, index) => (slot, index))
            .Where(pair => pair.slot?.ItemId == itemId)
            .Select(pair => pair.index).ToArray();

        void ReviveEnemyForObserver()
        {
            if (!enemy.Alive)
            {
                runtime.Combat.ScheduleRespawn(enemy);
                TestAssert.True(runtime.Combat.RespawnImmediately(enemy),
                    $"{itemId} enemy respawns for a live observer");
            }
            enemy.Position = hero.Position + new Position2(300.0, 0.0);
            runtime.Delivery.Flush();
            match.Clear();
        }

        double TakePhysicalDamage()
        {
            hero.Health = hero.MaxHealth;
            var before = hero.Health;
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 100.0,
                DamageType.Physical, DamageSourceKind.Basic),
                $"{itemId} physical damage probe lands");
            return before - hero.Health;
        }

        bool Shop(byte[] packet) => runtime.Session.DispatchShop(new SessionInput(
            1, 1, runtime.Cipher.Encrypt(packet), packet, true));

        bool Sell(int slot)
        {
            var packet = new byte[6];
            packet[0] = 0x09;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            packet[5] = (byte)(0x80 | slot);
            return Shop(packet);
        }

        bool Buy(int id)
        {
            var packet = new byte[9];
            packet[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), checked((uint)id));
            return Shop(packet);
        }
    }

    private static void AssertSnowballStat(Entity hero, Amberline.Simulation.Stats.HeroStats owned, int itemId,
        int stacks, double perStack, string statName, string label)
    {
        // Batch 149 step 1: the growth is the *Stats buff's own payload (Spells/MejaisStats.luaobj's
        // IncFlatMagicDamageMod 8.0 a stack, LeviathanStats' IncFlatHPPoolMod 32.0, MuramasaStats'
        // IncFlatPhysicalDamageMod 5.0, each scaled by the stack count), so the hero's live stat is the item's own
        // value plus that growth. The hand-written counter this used to toggle is gone.
        var (expected, actual) = itemId switch
        {
            3041 => (owned.AbilityPower + stacks * perStack, hero.AbilityPower),
            3138 => (owned.MaxHp + stacks * perStack, hero.MaxHealth),
            3141 => (owned.AttackDamage + stacks * perStack, hero.AttackDamage),
            _ => throw new ArgumentOutOfRangeException(nameof(itemId)),
        };
        TestAssert.Near(expected, actual, 1e-9, label + " " + statName);
    }

    private static void AssertUnownedStat(Amberline.Simulation.Stats.HeroStats expected,
        Entity hero, int itemId, string statName, string label)
    {
        var (expectedValue, actualValue) = itemId switch
        {
            3041 => (expected.AbilityPower, hero.AbilityPower),
            3138 => (expected.MaxHp, hero.MaxHealth),
            3141 => (expected.AttackDamage, hero.AttackDamage),
            _ => throw new ArgumentOutOfRangeException(nameof(itemId)),
        };
        TestAssert.Near(expectedValue, actualValue, 1e-9, label + " " + statName);
    }

    /// <summary>
    /// The name an add or remove carries for a binding name (batch 181): the script's BuffName when the client holds its Data\Buffs
    /// entry. Since batch 357 every add carries its binding stem, so this is the identity (Batch 357: binding stem).
    /// </summary>
    private static string WireBuffName(string name) => name;

    private static void AssertBuffRemovalForObservers(Match match, Entity hero,
        string name, int expected, string label)
    {
        var hash = SpellPackets.BuffNameHash(WireBuffName(name));
        foreach (var (capture, index) in match.Captures.Take(2).Select((capture, index) =>
                     (capture, index)))
        {
            var count = capture.Packets.Count(packet =>
            {
                var bytes = packet.Plaintext.Span;
                return bytes.Length >= 10 && bytes[0] == 0x7F &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == hero.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]) == hash;
            });
            TestAssert.Equal(expected, count, label + $" observer {index}");
        }
    }

    private static bool HasBuffAdd(PlaintextPacket packet, uint netId, string name)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 13 && bytes[0] == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == netId &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) == SpellPackets.BuffNameHash(WireBuffName(name));
    }

    [Check("buffs.hud-number.ezreal-production-fx")]
    public static void EzrealPassiveDrawsAGlowPerStackAndReconnectsThem()
    {
        using var match = new Match("Ezreal", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var target = match.Hero(1);
        target.MaxHealth = target.Health = 5000.0;

        match.Clear();
        match.Cast(0, 0, target);
        runtime.Tick(1.0);
        runtime.Delivery.Flush();
        var first = hero.Buffs.Single(buff => buff.Name == "EzrealRisingSpellForce");
        TestAssert.Equal(1, first.Stacks, "Ezreal Q adds the first passive stack");
        TestAssert.Near(10.0, first.Payload.TooltipValue1, 1e-9,
            "Ezreal first passive stack publishes ten percent attack speed");
        var firstGlows = match.Captures[0].Packets.Where(packet =>
            packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode &&
            ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("Ezreal_glow1")).ToArray();
        TestAssert.Equal(1, firstGlows.Length,
            "first Ezreal passive stack creates exactly one glow");
        TestAssert.Equal(hero.NetId, FxTarget(firstGlows[0]),
            "first Ezreal glow targets its owner");
        TestAssert.Equal(hero.NetId, FxBinding(firstGlows[0]),
            "first Ezreal glow binds its owner");
        TestAssert.Equal(SpellPackets.BuffNameHash("l_hand"),
            ReadUInt32(firstGlows[0], 16), "first Ezreal glow uses l_hand");

        hero.SpellCooldowns[0] = 0.0;
        hero.Mana = hero.MaxMana;
        match.Clear();
        match.Cast(0, 0, target);
        runtime.Tick(1.0);
        runtime.Delivery.Flush();
        var refreshed = hero.Buffs.Single(buff => buff.Name == "EzrealRisingSpellForce");
        TestAssert.Equal(2, refreshed.Stacks, "Ezreal Q refreshes the passive stack");
        var secondGlows = match.Captures[0].Packets.Where(packet =>
            packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode &&
            ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("Ezreal_glow2")).ToArray();
        TestAssert.Equal(1, secondGlows.Length,
            "the second passive stack creates exactly one glow for its count");
        TestAssert.Equal(hero.NetId, FxTarget(secondGlows[0]),
            "the second Ezreal glow targets its owner");
        TestAssert.Equal(hero.NetId, FxBinding(secondGlows[0]),
            "the second Ezreal glow binds its owner");
        TestAssert.Equal(SpellPackets.BuffNameHash("l_hand"),
            ReadUInt32(secondGlows[0], 16), "the second Ezreal glow uses l_hand");
        // 19 October 2026 (batch 103): the glow is the shipped script's. EzrealRisingSpellForce.OnBuffActivate creates Ezreal_glow<count> on every
        // stack and OnBuffDeactivate removes the id it kept; retail keeps an instance per stack, each removing its own glow when it ends, so the
        // glows add up. The earlier stack's glow is not killed by the next stack (the hand-written module replaced it).
        TestAssert.False(match.Captures[0].Packets.Any(packet => packet.Plaintext.Span[0] == FxPackets.KillOpcode),
            "the second stack leaves glow1 in place");

        // A reconnecting client is given every glow that is still alive, once (the persistent ledger of the replication layer: the script removes
        // the id it keeps, so the effect lives as long as the buff).
        var firstId = ReadUInt32(firstGlows[0], 25);
        var secondId = ReadUInt32(secondGlows[0], 25);
        TestAssert.True(firstId != 0 && secondId != 0 && firstId != secondId, "each stack's glow has its own effect id");
        var observer = runtime.Delivery.Observers.Single(item => item.ObserverId == 1);
        observer.View.Effects.Remove(firstId);
        observer.View.Effects.Remove(secondId);
        match.Clear();
        runtime.Delivery.Flush();
        var restored = match.Captures[0].Packets.Where(packet =>
            packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode).ToArray();
        foreach (var (name, id) in new[] { ("Ezreal_glow1", firstId), ("Ezreal_glow2", secondId) })
        {
            var again = restored.Where(packet => ReadUInt32(packet, 6) == SpellPackets.BuffNameHash(name)).ToArray();
            TestAssert.Equal(1, again.Length, $"reconnect restores {name} exactly once");
            TestAssert.Equal(id, ReadUInt32(again[0], 25), $"reconnect keeps the effect id of {name}");
            TestAssert.Equal(hero.NetId, FxTarget(again[0]), $"reconnect {name} targets its owner");
            TestAssert.Equal(hero.NetId, FxBinding(again[0]), $"reconnect {name} binds its owner");
        }
        TestAssert.Equal(2, restored.Length, "reconnect restores only the glows of the two stacks");
    }

    [Check("spells-ezreal-passive-five-stack-renewal")]
    public static void EzrealFifthGlowSurvivesSixthHit()
    {
        using var match = new Match("Ezreal", Team.Order);
        var hero = match.Hero(0); var target = match.Hero(1);
        for (var hit = 0; hit < 5; hit++)
        {
            hero.SpellCooldowns[0] = 0; hero.Mana = hero.MaxMana;
            match.Cast(0, 0, target); SpellStep(match, 1);
        }
        match.Runtime.Delivery.Flush();
        var buff = hero.Buffs.Single(item => item.Name == "EzrealRisingSpellForce");
        TestAssert.Equal(5, buff.Stacks);
        // 19 October 2026 (batch 103): the glow is the script's (EzrealRisingSpellForce.OnBuffActivate creates Ezreal_glow5 at the fifth stack
        // into InstanceVars.Particle), not a BuffState payload field; its effect id is the one the client was given.
        var fifth = match.Captures[0].Packets.Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
            ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("Ezreal_glow5")).ToArray();
        TestAssert.Equal(1, fifth.Length, "the fifth stack creates glow5 once");
        var glow = ReadUInt32(fifth[0], 25);
        TestAssert.True(glow != 0, "fifth stack has a persistent glow");
        match.Runtime.Buffs.TickBuffs(hero, 2);
        hero.SpellCooldowns[0] = 0; hero.Mana = hero.MaxMana;
        match.Clear();
        match.Cast(0, 0, target); SpellStep(match, 1);
        match.Runtime.Delivery.Flush();
        // EZ-04: the sixth hit keeps glow5's identity: the script creates glow5 only when LastCount is not already 5, and nothing kills it.
        TestAssert.Near(6, buff.ExpiresAt, 1e-9, "sixth hit renews the full duration");
        TestAssert.Near(50, buff.Payload.TooltipValue1, 1e-9);
        foreach (var capture in match.Captures)
        {
            TestAssert.False(capture.Packets.Any(packet =>
                Opcode(packet) == FxPackets.KillOpcode && ReadUInt32(packet, 5) == glow),
                "renewal does not destroy glow5");
            TestAssert.False(HasEffect(capture, "Ezreal_glow5"), "renewal does not recreate glow5");
            // Batch 55 (d3ea329): the sixth hit leaves the count at 5, so the HUD renewal is the silent 0x32 (BuffReplace:
            // slot @5, duration @10, caster @14) and no second 0xBF re-adds the buff.
            TestAssert.False(capture.Packets.Any(packet => HasBuffAdd(packet, hero.NetId, "EzrealRisingSpellForce")),
                "renewal is not a re-add");
            var renewal = capture.Packets.Single(packet => Opcode(packet) == 0x32 &&
                ReadUInt32(packet, 1) == hero.NetId && packet.Plaintext.Span[5] == (byte)buff.Slot);
            // Reconstruction (wire grace): the renewal's wire duration carries the copy's margin (one step for a script renewal).
            TestAssert.Near(buff.ExpiresAt + buff.Payload.WireDurationMargin,
                BinaryPrimitives.ReadSingleLittleEndian(renewal.Plaintext.Span[10..]), 1e-5, "HUD duration renews");
            TestAssert.Equal(hero.NetId, ReadUInt32(renewal, 14), "renewal caster");
        }
    }

    // 19 October 2026: "spells-orianna-passive-tooltip-publication-and-reconnect" pinned OriannaPassive.UpdateTooltip; the passive is OrianaSpellSword's
    // BuffOnUpdateActions now (scripting.bb-orianna-passive-tooltip-and-stacks).

    private static void AssertBuffTooltip(
        IEnumerable<PlaintextPacket> packets, uint netId, int slot,
        double first, double second, double third, string label)
    {
        // The row follows every script publication, so one flush can describe the same slot twice: a hero kill runs
        // LeviathanCheck's two LeviathanStats adds (the shipped BuffOnKill pair), each of which resamples and rewrites
        // the row (BBSetBuffToolTipVar). The client keeps the last one it was sent, which is the value asserted here.
        var packet = packets.Last(candidate =>
        {
            var bytes = candidate.Plaintext.Span;
            return bytes.Length >= 24 && bytes[0] == 0x83 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[7..]) == netId &&
                bytes[11] == slot;
        });
        var raw = packet.Plaintext.Span;
        TestAssert.Near(first, BinaryPrimitives.ReadSingleLittleEndian(raw[12..]),
            1e-5, label + " first float");
        TestAssert.Near(second, BinaryPrimitives.ReadSingleLittleEndian(raw[16..]),
            1e-5, label + " second float");
        TestAssert.Near(third, BinaryPrimitives.ReadSingleLittleEndian(raw[20..]),
            1e-5, label + " third float");
    }
}
