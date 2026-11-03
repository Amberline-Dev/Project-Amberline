using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Stats;

public static class HeroStatsChecks
{


    [Check("stats.hero.validation")]
    public static void TypedInputsAndOrdering()
    {
        TestAssert.Throws<ArgumentOutOfRangeException>(() => _ = new BuffPayload { Armor = double.NaN });
        TestAssert.Throws<ArgumentOutOfRangeException>(() => _ = new BuffPayload { MoveSpeedPercent = double.PositiveInfinity });
        var payloadDefaults = new BuffPayload();
        TestAssert.Near(1.2, payloadDefaults.SpellbladeDelay, 1e-12);
        TestAssert.Equal(1, payloadDefaults.Stacks);
        TestAssert.True(payloadDefaults.PowerFistRank is null);
        var buffState = new BuffState("Stackable", payloadDefaults);
        TestAssert.True(buffState.MaxStack is null);
        var replacement = new BuffPayload { Stacks = 2 };
        buffState.Payload = replacement;
        TestAssert.True(ReferenceEquals(replacement, buffState.Payload));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => buffState.MaxStack = -1);
        var forcedMove = new ForcedMoveState(new Position2(10.0, 20.0), 900.0, 0.5, 7, "TestMove");
        replacement.ForcedMove = forcedMove;
        TestAssert.True(ReferenceEquals(forcedMove, replacement.ForcedMove));
        replacement.SilverCasterNetId = 42;
        TestAssert.Equal((uint)42, replacement.SilverCasterNetId);
        TestAssert.Throws<ArgumentException>(() => _ = new HeroStatsInput(
            "Annie", 1, new InventorySlot?[Inventory.SlotCount - 1]));

        var gameData = new GameData();
        var catalog = new ItemCatalog(gameData.Paths);
        var empty = Inventory.Empty();
        var input = new HeroStatsInput("Nidalee", 1, empty, statCharacter: "Nidalee_Cougar");
        var cougar = gameData.StatsAtLevel(gameData.Champion("Nidalee_Cougar"), 1);
        var stats = HeroStatCalculator.Calculate(gameData, catalog, input);
        TestAssert.Near(cougar.MaxHp, stats.MaxHp, 1e-9);
        TestAssert.Near(cougar.AttackDamage, stats.AttackDamage, 1e-9);

        var mobilitySlots = Inventory.Empty().ToArray();
        mobilitySlots[0] = new InventorySlot(3117);
        var mobility = HeroStatCalculator.Calculate(gameData, catalog,
            new HeroStatsInput("Annie", 1, mobilitySlots,
                new[] { new BuffState("BootsOfMobilityDebuff", new BuffPayload { MoveSpeedFlat = -60.0 }) }));
        TestAssert.Near(310.0 + 70.0, mobility.MoveSpeed, 1e-9);

        // Batch 149 step 1: the snowball's per-stack stats and its twenty-stack caps are the shipped script buffs
        // (Spells/MuramasaStats.luaobj +5 attack damage a stack scaled by the lifecycle, MuramasaCap.luaobj's
        // IncPercentMultiplicativeMovementSpeedMod +0.15). The calculator sees those payloads; the hand-written
        // Snowball3041/3141 counters are gone.
        var growth = HeroStatCalculator.Calculate(gameData, catalog, new HeroStatsInput("Annie", 1, empty,
            [new BuffState("MuramasaStats", new BuffPayload { AttackDamage = 100.0 }),
             new BuffState("MuramasaCap", new BuffPayload { MoveSpeedMult = 0.15 })]));
        var baseAnnie = gameData.StatsAtLevel(gameData.Champion("Annie"), 1);
        TestAssert.Near(baseAnnie.AttackDamage + 100.0, growth.AttackDamage, 1e-9);
        TestAssert.Near((baseAnnie.MoveSpeed * 1.15), growth.MoveSpeed, 1e-9);

        // Playtest B2 YV12, 26 Sept 2026: six Infinity Edges showed 152% crit.
        var infinityEdges = Enumerable.Range(0, Inventory.SlotCount)
            .Select(_ => (InventorySlot?)new InventorySlot(3031)).ToArray();
        TestAssert.Near(1.0, HeroStatCalculator.Calculate(gameData, catalog,
            new HeroStatsInput("Annie", 1, infinityEdges)).CritChance, 1e-12,
            "item crit chance is capped at 100%");
        var single = Inventory.Empty().ToArray();
        single[0] = new InventorySlot(3031);
        TestAssert.Near(1.0, HeroStatCalculator.Calculate(gameData, catalog,
            new HeroStatsInput("Annie", 1, single,
                [new BuffState("CritTest", new BuffPayload { CritChance = 1.0 })])).CritChance, 1e-12,
            "item plus buff crit chance is capped at 100%");
    }

    [Check("stats.hero.patch126-cooldown-cap")]
    public static void Map1CooldownCap()
    {
        var data = new GameData();
        var catalog = new ItemCatalog(data.Paths);
        // Batch 149 step 1: the twenty-stack bonus is Spells/MejaisCap.luaobj's own IncPercentCooldownMod of -0.15
        // (the cap Items/3041.luaobj adds at twenty stacks), not a hand-written rule over Snowball3041.
        foreach (var (modifier, stacks, expected) in new[]
                 { (-0.2, 0, -0.2), (-0.3, 20, -0.4), (-0.7, 0, -0.4), (-0.9, 20, -0.4), (0.1, 0, 0.1) })
        {
            var buffs = new List<BuffState> { new("CooldownTest", new BuffPayload { CooldownMod = modifier }) };
            if (stacks == 20) buffs.Add(new BuffState("MejaisCap", new BuffPayload { CooldownMod = -0.15 }));
            var stats = HeroStatCalculator.Calculate(data, catalog,
                new HeroStatsInput("Annie", 18, Inventory.Empty(), buffs));
            TestAssert.Near(expected, stats.CooldownMod, 1e-9, "Map1 cap includes the final Mejai bonus");
        }
    }

    [Check("stats.hero.movement-soft-caps")]
    public static void MovementSoftCapsUseTheMaintainedStages()
    {
        // The lower curve is the explicitly adopted Classic compatibility
        // rule: it is intentionally not a native patch-126 reconstruction.
        TestAssert.Near(110.0, HeroStatCalculator.ApplyMovementSoftCaps(0.0), 1e-12,
            "zero raw movement follows the adopted lower curve");
        TestAssert.Near(219.5, HeroStatCalculator.ApplyMovementSoftCaps(219.0), 1e-12,
            "raw movement below 220 uses the half-speed lower curve");
        TestAssert.Near(220.0, HeroStatCalculator.ApplyMovementSoftCaps(220.0), 1e-12,
            "the lower curve ends at 220");
        TestAssert.Near(415.0, HeroStatCalculator.ApplyMovementSoftCaps(415.0), 1e-12,
            "the first movement stage is unchanged at its boundary");
        TestAssert.Near(475.0, HeroStatCalculator.ApplyMovementSoftCaps(490.0), 1e-12,
            "the 415..490 stage uses the 80 percent scale");
        TestAssert.Near(530.0, HeroStatCalculator.ApplyMovementSoftCaps(600.0), 1e-12,
            "the post-490 stage uses the 50 percent scale");
        TestAssert.Near(110.0, HeroStatCalculator.ApplyMovementSoftCaps(-50.0), 1e-12,
            "negative input clamps to zero before the lower curve");
    }

    [Check("stats.movement.modifier-order")]
    public static void MovementModifiersUseClassicSlowStacking()
    {
        var payloads = new[]
        {
            new BuffPayload { MoveSpeedFlat = 10.0, MoveSpeedPercent = 0.20 },
            new BuffPayload { MoveSpeedMult = 0.15 },
            new BuffPayload { MoveSpeedMult = 0.10 },
            new BuffPayload { MoveSpeedPercent = -0.40 },
            new BuffPayload { MoveSpeedMult = -0.20 },
        };
        var raw = MovementSpeedCalculator.Calculate(100.0, 0.0, 0.0, 0.0,
            payloads, applyHeroSoftCaps: false);
        var expectedRaw = (100.0 + 10.0) * (1.0 + 0.20) *
                          (1.0 + 0.15) * (1.0 + 0.10) *
                          (1.0 - 0.40) * (1.0 - 0.20 * 0.35);
        TestAssert.Near(expectedRaw, raw, 1e-12,
            "flat and additive stages precede independent positive multipliers and 35 percent slow stacking");
        TestAssert.Near(raw, MovementSpeedCalculator.Calculate(100.0, 0.0, 0.0, 0.0,
            payloads.Reverse(), applyHeroSoftCaps: false), 1e-12,
            "slow order does not change the strongest-first result");
        TestAssert.Near(0.558, MovementSpeedCalculator.Calculate(1.0, 0.0, 0.0, 0.0,
            new[] { new BuffPayload { MoveSpeedPercent = -0.40 },
                    new BuffPayload { MoveSpeedMult = -0.20 } }, false), 1e-12,
            "40 percent plus 20 percent slow leaves 0.60 times 0.93 speed");
    }

    [Check("stats.movement.occult-cap-order")]
    public static void FullOccultBonusIsCappedAfterMovementBonuses()
    {
        var data = new GameData();
        var catalog = new ItemCatalog(data.Paths);
        var slots = Inventory.Empty().ToArray();
        slots[0] = new InventorySlot(3141);
        // Batch 149 step 1: the full-stack Occult bonus is Spells/MuramasaCap.luaobj's own
        // IncPercentMultiplicativeMovementSpeedMod of +0.15 (the cap Items/3141.luaobj adds at twenty stacks), so it
        // arrives as the same buff payload the other multiplicative movement sources use.
        var input = new HeroStatsInput("Annie", 1, slots,
            [new BuffState("MovementTest", new BuffPayload { MoveSpeedFlat = 300.0 }),
             new BuffState("MuramasaCap", new BuffPayload { MoveSpeedMult = 0.15 })]);
        var raw = data.StatsAtLevel(data.Champion("Annie"), 1).MoveSpeed + 300.0;
        var afterOccult = raw * 1.15;
        var actual = HeroStatCalculator.Calculate(data, catalog, input);
        TestAssert.True(afterOccult > HeroStatCalculator.MovementSoftCapEnd,
            "the regression input reaches the high movement cap");
        TestAssert.Near(afterOccult * 0.5 + 230.0, actual.MoveSpeed, 1e-9,
            "the full Occult movement bonus is included before the final soft cap");
    }

    [Check("stats.hero.patch126-attack-timing")]
    public static void NativeAttackTiming()
    {
        var data = new GameData();
        var orianna = data.Champion("Orianna");
        TestAssert.Near(3.5, orianna.AttackSpeedPerLevel, 1e-9,
            "Orianna shipped attack-speed growth remains 3.5 percent");
        TestAssert.Near(-0.05, Number(orianna.Raw, "Data*AttackDelayOffsetPercent"), 1e-9,
            "Orianna shipped attack-delay offset");
        TestAssert.Near(-0.124561404, Number(orianna.Raw, "Data*AttackDelayCastOffsetPercent"), 1e-9,
            "Orianna shipped attack-delay cast offset");
        TestAssert.False(orianna.Raw.TryGetValue(InibinReader.KeyHash("Data*AttackSpeed"), out _),
            "Orianna has no separate shipped Data*AttackSpeed override");
        var oriannaHero = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0))
        { ChampionName = "Orianna", AttackSpeedMod = 1.0 };
        var oriannaTiming = HeroAttackTimings.Apply(oriannaHero, data);
        TestAssert.Near(1.52, oriannaTiming.Period, 1e-7,
            "Orianna base attack period uses the shipped delay offset");
        TestAssert.Near(1.52 * 0.175438596, oriannaTiming.Windup, 1e-7,
            "Orianna windup uses the shipped cast offset");
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0))
        { ChampionName = "Annie", AttackSpeedMod = 1.0 };
        var timing = HeroAttackTimings.Apply(hero, data);
        // Independent expected values from Annie's shipped offsets and stock
        // helpers, before level attack-speed growth.
        TestAssert.Near(1.728, timing.Period, 1e-7);
        TestAssert.Near(1.728 * 0.195794753, timing.Windup, 1e-7);
        TestAssert.Near(data.Spell("AnnieBasicAttack").MissileSpeed, timing.MissileSpeed, 0);
        hero.AttackSpeedMod = 2;
        var refreshed = HeroAttackTimings.ApplyRefresh(hero, data);
        TestAssert.Near(timing.Period / 2, refreshed.Period, 1e-9);
        TestAssert.Near(timing.Windup / 2, refreshed.Windup, 1e-9);

        foreach (var champion in new[] { "Annie", "Orianna", "LeeSin", "Nidalee", "Yorick", "Shaco",
                     "Ezreal", "Vayne", "Janna", "Blitzcrank" })
        {
            hero.ChampionName = champion;
            foreach (var (speed, period) in new[] { (100.0, 0.4), (0.001, 5.0) })
            {
                hero.AttackSpeedMod = speed;
                var initial = HeroAttackTimings.Apply(hero, data);
                var refresh = HeroAttackTimings.ApplyRefresh(hero, data);
                TestAssert.Equal(initial, refresh, champion + " spawn and refresh agree");
                TestAssert.Near(period, initial.Period, 0, champion + " native period cap");
                TestAssert.True(initial.Windup >= 0 && initial.Windup <= initial.Period);
            }
        }
        hero.AttackSpeedMod = double.NaN;
        TestAssert.Throws<InvalidOperationException>(() => HeroAttackTimings.ForRefresh(hero, data));
    }

    private static HeroStatsInput ParseInput(JsonElement entry)
    {
        var inventory = Inventory.Empty().ToArray();
        // Batch 149 step 1: the growth this corpus records per slot and per counter is the shipped scripts' own
        // now, so it reaches the calculator as buff payloads instead of inventory/hero fields. The recorded
        // numbers are kept; only what carries them changes. Two placements differ from the hand-written formula
        // and the script wins: 3083's ExtraHP is Items/3083.luaobj's IncFlatHPPoolMod, the client's mFlatHPPoolMod,
        // so it joins the flat pool before the percent step (the hand-written warmog_health added it after), and
        // the snowball caps are their own buffs rather than rules over a counter.
        var scripted = new List<BuffState>();
        var index = 0;
        foreach (var slot in entry.GetProperty("slots").EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Null)
            {
                var stateJson = slot.GetProperty("state");
                var state = new InventoryItemState();
                inventory[index] = new InventorySlot(
                    slot.GetProperty("item_id").GetInt32(),
                    slot.GetProperty("count").GetInt32(),
                    slot.GetProperty("charges").GetInt32(), state);
                // Items/3072.luaobj's UpdateSelfBuffStats reads InstanceVars.PhysicalDamageBonus and
                // PercentLifeSteal; 3083's reads ExtraHP and ExtraRegen. The live path samples those rows into
                // Entity.ItemScriptStats and feeds the calculator one more payload named "ItemScript"
                // (HeroStatsServices.ScriptedPayloads), which is what these rows are called here too. The recorder
                // writes the hero's whole growth state into every slot, so only the copy whose own script reads a
                // field carries it — a Long Sword's slot must not grow Warmog's 250 health.
                var itemId = inventory[index]!.ItemId;
                var itemScript = new BuffPayload();
                if (itemId == 3072)
                {
                    itemScript.AttackDamage = Number(stateJson, "bloodthirster_attack_damage");
                    itemScript.LifeSteal = Number(stateJson, "bloodthirster_life_steal");
                }
                else if (itemId == 3083)
                {
                    itemScript.MaxHp = Number(stateJson, "warmog_health");
                    itemScript.HpRegen = Number(stateJson, "warmog_regen");
                }
                if (itemScript.AttackDamage != 0.0 || itemScript.LifeSteal != 0.0 ||
                    itemScript.MaxHp != 0.0 || itemScript.HpRegen != 0.0)
                    scripted.Add(new BuffState("ItemScript", itemScript));
            }
            index++;
        }

        var counters = entry.GetProperty("itemCounters");
        // Batch 149 step 5: the counters this used to restore are the scripts' own tables now (see SupersededGrowth).
        var itemState = new HeroItemState();
        // Spells/MejaisStats.luaobj grants IncFlatMagicDamageMod 8.0 a stack (the lifecycle scales that per-stack
        // row by the stack count), LeviathanStats.luaobj IncFlatHPPoolMod 32.0, MuramasaStats.luaobj
        // IncFlatPhysicalDamageMod 5.0. Each cap is added by its item's UpdateSelfBuffActions when the *Stats
        // count CO_EQUALs 20: MejaisCap's IncPercentCooldownMod -0.15, MuramasaCap's
        // IncPercentMultiplicativeMovementSpeedMod 0.15. LeviathanCap (IncPercentPhysicalReduction /
        // IncPercentMagicReduction 0.15) is damage reduction, which no HeroStats row reads.
        AddSnowball(scripted, "MejaisStats", Integer(counters, "snowball_3041"), 8.0,
            stacks => new BuffPayload { AbilityPower = stacks });
        AddSnowball(scripted, "LeviathanStats", Integer(counters, "snowball_3138"), 32.0,
            stacks => new BuffPayload { MaxHp = stacks });
        AddSnowball(scripted, "MuramasaStats", Integer(counters, "snowball_3141"), 5.0,
            stacks => new BuffPayload { AttackDamage = stacks });
        if (Integer(counters, "snowball_3041") == 20)
            scripted.Add(new BuffState("MejaisCap", new BuffPayload { CooldownMod = -0.15 }));
        if (Integer(counters, "snowball_3141") == 20)
            scripted.Add(new BuffState("MuramasaCap", new BuffPayload { MoveSpeedMult = 0.15 }));

        // Batch 149 step 2: the active-window group's static rows are the window buffs' own now (each item's script
        // re-adds a named ten-second window every nine seconds), and the calculator is fed them the way the live path
        // feeds it (HeroStatsServices): SpiritVisage's IncPercentCooldownMod is the window buff's own payload row, the
        // other four take theirs from OnBuffActivate's BBIncPermanentStat, which the hero keeps in
        // Entity.PermanentStatMods. A window is up whenever its copy is held — that is what the deleted
        // Inventory.ScriptedStaticModifiers rows stood for — and REPLACE_EXISTING/RENEW_EXISTING keeps one of them
        // whatever the copy count. The window's sustain shares are not in this translation: they are the feedback pair
        // CompareStats divides out of the archived rows, since a static input cannot hold them.
        var held = inventory.Where(slot => slot is not null).Select(slot => slot!.ItemId).ToHashSet();
        var permanent = new Dictionary<string, double>(StringComparer.Ordinal);
        void Window(int itemId, double cooldown, double armorPenFlat = 0.0)
        {
            if (!held.Contains(itemId)) return;
            permanent["PercentCooldownMod"] = permanent.GetValueOrDefault("PercentCooldownMod") + cooldown;
            if (armorPenFlat != 0.0)
                permanent["FlatArmorPenetrationMod"] =
                    permanent.GetValueOrDefault("FlatArmorPenetrationMod") + armorPenFlat;
        }
        if (held.Contains(3065))
            scripted.Add(new BuffState("SpiritVisage", new BuffPayload { CooldownMod = -0.10 }));
        Window(3069, -0.15);
        Window(3128, -0.15);
        Window(3142, -0.15, 20.0);
        Window(3143, -0.05);

        var buffs = new List<BuffState>(scripted);
        foreach (var buff in entry.GetProperty("buffs").EnumerateArray())
        {
            var payload = new BuffPayload();
            foreach (var value in buff.GetProperty("payload").EnumerateObject())
                SetFixturePayload(payload, value.Name, value.Value);
            buffs.Add(new BuffState(buff.GetProperty("name").GetString()!, payload));
        }
        // The corpus records the cougar as the AspectOfTheCougar buff; the engine now keys the form on the pushed character (Entity.StatCharacterName).
        var champion = entry.GetProperty("champion").GetString()!;
        var form = champion == "Nidalee" && buffs.Any(buff => buff.Name == "AspectOfTheCougar") ? "Nidalee_Cougar" : null;
        return new HeroStatsInput(champion, entry.GetProperty("level").GetInt32(), inventory, buffs, itemState,
            permanentMods: permanent, statCharacter: form);
    }

    private static void SetFixturePayload(BuffPayload payload, string name, JsonElement value)
    {
        var number = value.GetDouble();
        switch (name)
        {
            case "armor": payload.Armor = number; break;
            case "spell_block": payload.SpellBlock = number; break;
            case "attack_damage": payload.AttackDamage = number; break;
            case "attack_speed_mod": payload.AttackSpeedMod = number; break;
            case "move_speed_percent": payload.MoveSpeedPercent = number; break;
            case "move_speed_mult": payload.MoveSpeedMult = number; break;
            case "ability_power": payload.AbilityPower = number; break;
            case "max_hp": payload.MaxHp = number; break;
            case "hp_regen_mult": payload.HpRegenMult = number; break;
            case "crit_damage": payload.CritDamage = number; break;
            case "cooldown_mod_per_level": payload.CooldownModPerLevel = number; break;
            default: throw new InvalidOperationException($"unmapped fixture payload field {name}");
        }
    }

    // Batch 64: a byte/10 inibin value is the client's float32 (float)b * 0.1f, which differs from the decimal b/10 this Python-era corpus
    // recorded by at most two float32 roundings (under 2e-7 relative; the same bound as data.gamedata.corpus). Everything else keeps 1e-6.
    private static void NearF32(double expected, double actual, string message) =>
        TestAssert.Near(expected, actual, Math.Max(1e-6, Math.Abs(expected) * 2e-7), message);

    private static void CompareStats(HeroStats actual, JsonElement expected, string champion, int level, bool visage)
    {
        var prefix = $"{champion} level {level}";
        // Preserve the 30 archived Visage rows. Their three sustain numbers carry SpiritVisage's own 15 percent raise
        // (the IncPercentHPRegenMod row and the two share rows of its BuffOnUpdateStats); a static calculator input
        // cannot hold that feedback pair, so the amplification is divided out here instead.
        var sustainDivisor = visage ? 1.15 : 1.0;
        NearF32(expected.GetProperty("max_hp").GetDouble(), actual.MaxHp, prefix + " max_hp");
        NearF32(expected.GetProperty("max_mp").GetDouble(), actual.MaxMp, prefix + " max_mp");
        NearF32(expected.GetProperty("attack_damage").GetDouble(), actual.AttackDamage, prefix + " attack_damage");
        NearF32(expected.GetProperty("armor").GetDouble(), actual.Armor, prefix + " armor");
        NearF32(expected.GetProperty("spell_block").GetDouble(), actual.SpellBlock, prefix + " spell_block");
        NearF32(expected.GetProperty("hp_regen").GetDouble() / sustainDivisor, actual.HpRegen, prefix + " hp_regen");
        NearF32(expected.GetProperty("mp_regen").GetDouble(), actual.MpRegen, prefix + " mp_regen");
        NearF32(expected.GetProperty("attack_speed_mod").GetDouble(), actual.AttackSpeedMod, prefix + " attack_speed_mod");
        var expectedMoveSpeed = expected.GetProperty("move_speed").GetDouble();
        if (expectedMoveSpeed > 490.0)
            expectedMoveSpeed = expectedMoveSpeed * 0.5 + 230.0;
        else if (expectedMoveSpeed > 415.0)
            expectedMoveSpeed = expectedMoveSpeed * 0.8 + 83.0;
        NearF32(expectedMoveSpeed, actual.MoveSpeed, prefix + " move_speed");
        NearF32(expected.GetProperty("attack_range").GetDouble(), actual.AttackRange, prefix + " attack_range");
        NearF32(expected.GetProperty("ability_power").GetDouble(), actual.AbilityPower, prefix + " ability_power");
        NearF32(expected.GetProperty("dodge").GetDouble(), actual.Dodge, prefix + " dodge");
        NearF32(expected.GetProperty("crit_chance").GetDouble(), actual.CritChance, prefix + " crit_chance");
        NearF32(expected.GetProperty("crit_multiplier").GetDouble(), actual.CritMultiplier, prefix + " crit_multiplier");
        NearF32(Math.Max(-0.4, expected.GetProperty("cooldown_mod").GetDouble()), actual.CooldownMod, prefix + " cooldown_mod");
        NearF32(expected.GetProperty("hardness").GetDouble(), actual.Hardness, prefix + " hardness");
        TestAssert.Equal(expected.GetProperty("dodge_piercing").GetBoolean(), actual.DodgePiercing, prefix + " dodge_piercing");
        NearF32(expected.GetProperty("armor_pen_flat").GetDouble(), actual.ArmorPenFlat, prefix + " armor_pen_flat");
        NearF32(expected.GetProperty("armor_pen_percent").GetDouble(), actual.ArmorPenPercent, prefix + " armor_pen_percent");
        NearF32(expected.GetProperty("magic_pen_flat").GetDouble(), actual.MagicPenFlat, prefix + " magic_pen_flat");
        NearF32(expected.GetProperty("magic_pen_percent").GetDouble(), actual.MagicPenPercent, prefix + " magic_pen_percent");
        NearF32(expected.GetProperty("life_steal").GetDouble() / sustainDivisor, actual.LifeSteal, prefix + " life_steal");
        NearF32(expected.GetProperty("spell_vamp").GetDouble() / sustainDivisor, actual.SpellVamp, prefix + " spell_vamp");
    }

    /// <summary>One snowball's <c>*Stats</c> buff: the shipped per-stack delta scaled to the recorded stack count.</summary>
    private static void AddSnowball(List<BuffState> buffs, string name, int stacks, double perStack,
        Func<double, BuffPayload> payload)
    {
        if (stacks > 0) buffs.Add(new BuffState(name, payload(perStack * stacks)));
    }

    private static int Integer(JsonElement objectValue, string name) =>
        objectValue.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

    private static double Number(JsonElement objectValue, string name) =>
        objectValue.TryGetProperty(name, out var value) ? value.GetDouble() : 0.0;

    private static double Number(InibinData data, string name) =>
        data.TryGetValue(InibinReader.KeyHash(name), out var value) && value.IsNumeric
            ? value.AsDouble() : 0.0;
}
