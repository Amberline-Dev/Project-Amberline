using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>Production-graph capacity boundaries for the remaining summoners.</summary>
public static class SummonerCapacityNextChecks
{
    [Check("spells-live-summoner-ghost-cleanse-capacity")]
    public static void GhostAndCleanseAdmitRequiredSelfBuff()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = NewRuntime();
            var hero = Hero(runtime, team, ["SummonerHaste", "SummonerBoost"]);
            Fill(runtime, hero, 64);
            TestAssert.False(Cast(runtime, hero, 0), "full Ghost rejects before cooldown");
            TestAssert.Equal(0.0, hero.SummonerCooldowns[0]);
            TestAssert.False(Has(hero, "SummonerHaste"));
            Free(runtime, hero);
            TestAssert.True(Cast(runtime, hero, 0), "one slot admits Ghost");
            TestAssert.True(Has(hero, "SummonerHaste"));
            TestAssert.Equal(64, hero.Buffs.Count);
            hero.SummonerCooldowns[0] = 0.0;
            TestAssert.True(Cast(runtime, hero, 0), "same-name Ghost refresh at full capacity");

            var cleanseRuntime = NewRuntime();
            var cleanser = Hero(cleanseRuntime, team, ["SummonerBoost", "SummonerDot"]);
            Fill(cleanseRuntime, cleanser, 64);
            TestAssert.False(Cast(cleanseRuntime, cleanser, 0),
                "full Cleanse rejects when no control can free a follow-up slot");
            TestAssert.Equal(0.0, cleanser.SummonerCooldowns[0]);
            Free(cleanseRuntime, cleanser);
            TestAssert.True(cleanseRuntime.Buffs.AddBuff(cleanser, "TestStun", BuffType.Stun,
                30.0, cleanser.NetId, canMitigateDuration: false) is not null);
            TestAssert.Equal(64, cleanser.Buffs.Count);
            TestAssert.True(Cast(cleanseRuntime, cleanser, 0),
                "full Cleanse uses its removable control slot for the follow-up");
            TestAssert.False(Has(cleanser, "TestStun"));
            TestAssert.True(Has(cleanser, "SummonerBoost"));
            TestAssert.Equal(64, cleanser.Buffs.Count);
            cleanser.SummonerCooldowns[0] = 0.0;
            TestAssert.True(Cast(cleanseRuntime, cleanser, 0),
                "full Cleanse refreshes its own follow-up by exact name");
        }
    }

    [Check("spells-live-summoner-ignite-embers-capacity")]
    public static void IgniteRequiresThreeTargetBuffsAndEmbersRetries()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = NewRuntime();
            var caster = Hero(runtime, team, ["SummonerDot", "SummonerHaste"]);
            var target = Hero(runtime, Opposite(team), ["SummonerHaste", "SummonerBoost"],
                caster.Position + new Position2(300.0, 0.0));
            Fill(runtime, target, 64);
            for (var used = 64; used >= 62; used--)
            {
                TestAssert.False(Cast(runtime, caster, 0, target),
                    $"{used} occupied target slots reject all-or-none Ignite");
                TestAssert.Equal(0.0, caster.SummonerCooldowns[0]);
                TestAssert.False(Has(target, "SummonerDot") || Has(target, "Internal_50MS") ||
                    Has(target, "GrievousWound"));
                Free(runtime, target);
            }
            TestAssert.True(Cast(runtime, caster, 0, target), "three free target slots admit Ignite");
            TestAssert.True(Has(target, "SummonerDot") && Has(target, "Internal_50MS") &&
                Has(target, "GrievousWound"));
            TestAssert.Equal(64, target.Buffs.Count);
            caster.SummonerCooldowns[0] = 0.0;
            TestAssert.True(Cast(runtime, caster, 0, target),
                "three same-name Ignite buffs refresh at full capacity");

            var bootsRuntime = NewRuntime();
            var bootsCaster = Hero(bootsRuntime, team, ["SummonerDot", "SummonerHaste"]);
            var bootsTarget = Hero(bootsRuntime, Opposite(team),
                ["SummonerHaste", "SummonerBoost"],
                bootsCaster.Position + new Position2(300.0, 0.0));
            bootsTarget.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            // 19 October 2026 (batch 107, Items/3117 OnDeactivate): a refresh that finds the boots newly equipped removes a stale debuff, so the
            // boots are owned before the cast, as a purchase's refresh makes them in play.
            bootsRuntime.HeroStats.Refresh(bootsTarget);
            // Batch 147 step 4: Items/3117.luaobj starts its Mobility penalty from its own BuffOnAllowAdd now, so the
            // copy has to be bound to its slot first (BbItemModule.SyncInventory, the same next-spell-update latency the
            // 3022 cases already account for); a cast in the equip step never runs that gate and the table below would
            // come out one row short of the nested BootsOfMobilityDebuff.
            bootsRuntime.Spells.ItemScripts?.SyncInventory(bootsTarget);
            Fill(bootsRuntime, bootsTarget, 64);
            for (var used = 64; used >= 61; used--)
            {
                TestAssert.False(Cast(bootsRuntime, bootsCaster, 0, bootsTarget),
                    $"{used} occupied slots reject Ignite's nested Mobility Boots buff");
                TestAssert.Equal(0.0, bootsCaster.SummonerCooldowns[0]);
                TestAssert.False(Has(bootsTarget, "BootsOfMobilityDebuff") ||
                    Has(bootsTarget, "SummonerDot") || Has(bootsTarget, "Internal_50MS") ||
                    Has(bootsTarget, "GrievousWound"));
                Free(bootsRuntime, bootsTarget);
            }
            TestAssert.True(Cast(bootsRuntime, bootsCaster, 0, bootsTarget),
                "four free slots admit Boots, DoT, speed support and grievous wounds");
            TestAssert.True(Has(bootsTarget, "BootsOfMobilityDebuff") &&
                Has(bootsTarget, "SummonerDot") && Has(bootsTarget, "Internal_50MS") &&
                Has(bootsTarget, "GrievousWound"), "boots table: " +
                string.Join(", ", bootsTarget.Buffs.Select(buff => buff.Name).Order(StringComparer.Ordinal)));
            TestAssert.Equal(64, bootsTarget.Buffs.Count);
            bootsCaster.SummonerCooldowns[0] = 0.0;
            TestAssert.True(Cast(bootsRuntime, bootsCaster, 0, bootsTarget),
                "all four exact-name buffs renew at full capacity");

            // 18 October 2026 (batch 81): SummonerDot.luaobj sets DoesntTriggerSpellCasts and no DoesntBreakShields, so Ignite does not pop a Banshee's
            // Veil; a veiled hero takes all of it, and the all-or-none capacity rule counts the same buffs as for an unveiled one (here Mobility
            // Boots' debuff, the DoT, its speed support and Grievous Wounds). The Veil is the item's buff, so the target holds the item.
            var shieldRuntime = NewRuntime();
            var shieldCaster = Hero(shieldRuntime, team, ["SummonerDot", "SummonerHaste"]);
            var shieldTarget = Hero(shieldRuntime, Opposite(team),
                ["SummonerHaste", "SummonerBoost"],
                shieldCaster.Position + new Position2(300.0, 0.0));
            shieldTarget.Inventory = [new InventorySlot(3117), new InventorySlot(3102), null, null, null, null];
            shieldRuntime.HeroStats.Refresh(shieldTarget);
            // The same bind as the boots arm above: without it Items/3117's BuffOnAllowAdd never runs and the veiled
            // cast below adds no nested BootsOfMobilityDebuff of its own.
            shieldRuntime.Spells.ItemScripts?.SyncInventory(shieldTarget);
            shieldRuntime.Buffs.AddBuff(shieldTarget, "BansheesVeil", BuffType.Aura,
                30.0, shieldTarget.NetId, canMitigateDuration: false);
            Fill(shieldRuntime, shieldTarget, 64);
            for (var used = 64; used >= 61; used--)
            {
                TestAssert.False(Cast(shieldRuntime, shieldCaster, 0, shieldTarget),
                    $"{used} occupied slots reject Ignite on a veiled hero: the Veil does not block it");
                TestAssert.Equal(0.0, shieldCaster.SummonerCooldowns[0]);
                TestAssert.False(Has(shieldTarget, "SummonerDot") || Has(shieldTarget, "BootsOfMobilityDebuff") ||
                    Has(shieldTarget, "BansheesVeilTimer"));
                Free(shieldRuntime, shieldTarget);
            }
            TestAssert.True(Cast(shieldRuntime, shieldCaster, 0, shieldTarget),
                "four free slots admit Ignite on a veiled hero");
            TestAssert.True(Has(shieldTarget, "SummonerDot") && Has(shieldTarget, "BootsOfMobilityDebuff"));
            TestAssert.True(Has(shieldTarget, "BansheesVeil"), "the Veil stays");
            TestAssert.False(Has(shieldTarget, "BansheesVeilTimer"), "and starts no replacement timer");

            var embersRuntime = NewRuntime();
            var embers = Hero(embersRuntime, team, ["SummonerDot", "SummonerHaste"]);
            var victim = Hero(embersRuntime, Opposite(team), ["SummonerHaste", "SummonerBoost"],
                embers.Position + new Position2(300.0, 0.0));
            embers.Masteries = MasteryPage.Create(new MasteryCatalog(),
                new Dictionary<int, int> { [103] = 3, [136] = 1, [126] = 1,
                    [106] = 3, [146] = 1 });
            embersRuntime.HeroStats.Refresh(embers);
            Fill(embersRuntime, embers, 64);
            TestAssert.True(Cast(embersRuntime, embers, 0, victim),
                "Ignite itself does not need a caster buff slot");
            embersRuntime.Spells.Tick(0.0);
            TestAssert.False(Has(embers, "BurningEmbers"),
                "full caster skips optional mastery buff without aborting tick");
            Free(embersRuntime, embers);
            embersRuntime.Spells.Tick(0.0);
            TestAssert.True(Has(embers, "BurningEmbers"),
                "next tick installs mastery buff after a slot opens");
            TestAssert.Equal(64, embers.Buffs.Count);
            embersRuntime.Buffs.TickBuffs(embers, 24.5);
            embersRuntime.Spells.Tick(0.0);
            TestAssert.True(embers.Buffs.Single(buff => buff.Name == "BurningEmbers").ExpiresAt > 24000.0,
                "the 25000-second mastery buff survives at full capacity");
        }
    }

    [Check("spells-live-summoner-revive-fortify-capacity")]
    public static void ReviveAndFortifyRejectBeforePartialCommit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = NewRuntime();
            var dead = Hero(runtime, team, ["SummonerRevive", "SummonerDot"]);
            dead.Alive = false;
            dead.Health = 0.0;
            dead.RespawnAt = 20.0;
            Fill(runtime, dead, 64);
            TestAssert.False(Cast(runtime, dead, 0), "full Revive rejects before respawn");
            TestAssert.False(dead.Alive);
            TestAssert.Equal(20.0, dead.RespawnAt);
            TestAssert.Equal(0.0, dead.SummonerCooldowns[0]);
            Free(runtime, dead);
            TestAssert.True(Cast(runtime, dead, 0), "one free slot admits ordinary Revive");
            TestAssert.True(dead.Alive && Has(dead, "SummonerReviveSpeedBoost"));

            var masteryRuntime = NewRuntime();
            var masteryDead = Hero(masteryRuntime, team, ["SummonerRevive", "SummonerDot"]);
            masteryDead.Masteries = MasteryPage.Create(new MasteryCatalog(),
                new Dictionary<int, int> { [133] = 1, [110] = 3, [129] = 1,
                    [109] = 3, [112] = 1 });
            masteryRuntime.HeroStats.Refresh(masteryDead);
            masteryDead.Alive = false;
            masteryDead.Health = 0.0;
            masteryDead.RespawnAt = 20.0;
            Fill(masteryRuntime, masteryDead, 63);
            TestAssert.False(Cast(masteryRuntime, masteryDead, 0),
                "one free slot cannot hold mastery Revive's two buffs");
            TestAssert.False(masteryDead.Alive);
            Free(masteryRuntime, masteryDead);
            TestAssert.True(Cast(masteryRuntime, masteryDead, 0),
                "two free slots admit mastery Revive");
            TestAssert.True(Has(masteryDead, "ReviveMarker") &&
                Has(masteryDead, "SummonerReviveSpeedBoost"));

            var fortifyRuntime = NewRuntime();
            var fortifier = Hero(fortifyRuntime, team, ["SummonerFortify", "SummonerDot"]);
            // Batch 203: the tower buff is the shipped APBonusDamageToTowers add (a champion's CharOnActivate; this hero is built without one, so
            // the add is made here). Its OnBuffActivate places FortifyCheck once per add, and only when a slot is free for it: the engine no
            // longer retries FortifyCheck on every tick. A renewal of the add runs OnBuffActivate again, and the shipped FortifyCheck poll then
            // needs one more free slot for FortifyBuff.
            Fill(fortifyRuntime, fortifier, 63);
            fortifyRuntime.Spells.UnitScripts!.AddShippedBuff(fortifier, "APBonusDamageToTowers", BuffType.Internal, 25000.0);
            TestAssert.True(Has(fortifier, "APBonusDamageToTowers"), "one free slot admits the shipped tower buff");
            TestAssert.False(Has(fortifier, "FortifyCheck"), "its OnBuffActivate finds no free slot for FortifyCheck");
            Free(fortifyRuntime, fortifier);
            fortifyRuntime.Spells.UnitScripts!.AddShippedBuff(fortifier, "APBonusDamageToTowers", BuffType.Internal, 25000.0);
            TestAssert.True(Has(fortifier, "FortifyCheck"), "the renewal's OnBuffActivate admits FortifyCheck into the free slot");
            fortifyRuntime.Buffs.TickBuffs(fortifier, 3.0);
            TestAssert.False(Has(fortifier, "FortifyBuff"), "FortifyBuff needs a further free slot");
            Free(fortifyRuntime, fortifier);
            fortifyRuntime.Buffs.TickBuffs(fortifier, 3.0);
            TestAssert.True(Has(fortifier, "FortifyBuff"), "a free slot admits FortifyBuff");
            var first = Turret(fortifyRuntime, team, fortifier.Position + new Position2(200.0, 0.0));
            var second = Turret(fortifyRuntime, team, fortifier.Position + new Position2(400.0, 0.0));
            Fill(fortifyRuntime, second, 64);
            TestAssert.False(Cast(fortifyRuntime, fortifier, 0),
                "one saturated turret rejects global Fortify before any immunity");
            TestAssert.True(Has(fortifier, "FortifyBuff"));
            TestAssert.False(Has(first, "SummonerFortify") || Has(second, "SummonerFortify"));
            TestAssert.Equal(0.0, fortifier.SummonerCooldowns[0]);
            Free(fortifyRuntime, second);
            TestAssert.True(Cast(fortifyRuntime, fortifier, 0), "all eligible turrets accept Fortify");
            TestAssert.True(Has(first, "SummonerFortify") && Has(second, "SummonerFortify"));
        }
    }

    [Check("spells-live-summoner-rally-aura-capacity")]
    public static void RallySkipsOnlySaturatedAuraRecipients()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var improved in new[] { false, true })
        {
            var runtime = NewRuntime();
            var hero = Hero(runtime, team, ["SummonerRally", "SummonerDot"]);
            if (improved)
            {
                hero.Masteries = MasteryPage.Create(new MasteryCatalog(),
                    new Dictionary<int, int> { [103] = 3, [106] = 3, [136] = 1,
                        [126] = 1, [102] = 4, [105] = 4, [146] = 1, [101] = 3,
                        [123] = 1, [143] = 1 });
                runtime.HeroStats.Refresh(hero);
            }
            var aura = improved ? "BeaconAuraNoParticleAP" : "BeaconAuraNoParticle";
            var saturated = Hero(runtime, team, ["SummonerHaste", "SummonerBoost"],
                hero.Position + new Position2(100.0, 0.0));
            var available = Hero(runtime, team, ["SummonerHaste", "SummonerBoost"],
                hero.Position + new Position2(-100.0, 0.0));
            Fill(runtime, saturated, 64);
            Fill(runtime, available, 63);
            TestAssert.True(Cast(runtime, hero, 0), "Rally creates its fresh beacon");
            var beacon = runtime.World.Entities.Values.Single(unit => unit.Name == "Beacon");
            TestAssert.True(Has(beacon, improved ? "BeaconAuraAP" : "BeaconAura"));
            // The aura scan is the beacon's own BeaconAura.BuffOnUpdateActions (the shipped BBExecutePeriodically every
            // second, then BBForEachUnitInTargetAreaAddBuff), so a step updates the beacon's buffs the way production
            // updates every entity's every simulation step. The scan's first update only seeds its LastTimeExecuted
            // (ExecuteImmediately is false), which production's first step after the cast is; one coarse step seeds it at
            // the cast clock before the next carries the clock to the due second.
            runtime.Buffs.TickBuffs(beacon, 0.0);
            runtime.Spells.Tick(1.0);
            runtime.Buffs.TickBuffs(beacon, 1.0);
            TestAssert.False(Has(saturated, aura));
            TestAssert.True(Has(available, aura),
                "later aura recipient still receives buff");
            Free(runtime, saturated);
            runtime.Spells.Tick(1.0);
            runtime.Buffs.TickBuffs(beacon, 1.0);
            TestAssert.True(Has(saturated, aura),
                "aura retries after a recipient slot opens");
            TestAssert.Equal(64, saturated.Buffs.Count);
            runtime.Spells.Tick(1.0);
            runtime.Buffs.TickBuffs(beacon, 1.0);
            TestAssert.True(Has(saturated, aura),
                "same-name aura renewal succeeds at full capacity");
        }
    }

    private static MatchRuntime NewRuntime() =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, solo: true);

    private static Entity Hero(MatchRuntime runtime, Team team, IReadOnlyList<string> spells,
        Position2? position = null)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, position ?? new Position2(7000.0, 7000.0))
        {
            Name = team == Team.Order ? "OrderCapacity" : "ChaosCapacity",
            ChampionName = "Annie", Health = 1000.0, MaxHealth = 1000.0,
            Mana = 1000.0, MaxMana = 1000.0, Speed = 325.0, Radius = 35.0,
            AttackRange = 625.0, AttackDamage = 100.0,
            AttackPeriod = 1.6, AttackWindup = 0.3,
        };
        hero.SummonerSpells = spells.ToList();
        runtime.World.Register(hero);
        return hero;
    }

    private static Entity Turret(MatchRuntime runtime, Team team, Position2 position)
    {
        var turret = new Entity(runtime.World.Allocate(UnitType.Turret), EntityKind.Turret,
            UnitType.Turret, team, position)
        {
            Name = "CapacityTurret", Health = 1000.0, MaxHealth = 1000.0,
            Radius = 88.0, Armor = 100.0, AttackRange = 775.0,
        };
        runtime.World.Register(turret);
        return turret;
    }

    /// <summary>
    /// Fills <paramref name="unit"/>'s table with <c>CapacityN</c> rows until it holds <paramref name="count"/> rows in
    /// total. The capacity rules count the table, not the filler rows, and a mastery page brings one of its own: <c>Talents/112</c>'s
    /// <c>UpdateSelfBuffStats</c> asks for <c>StrengthOfSpirit</c> (the script's <c>BBIfNotHasBuff</c> gate), a buff with its own
    /// <c>BuffOnUpdateActions</c> and therefore installed live by <c>BbLoadoutHooks.Bind</c>. Here that bind lands in the middle of the
    /// first add — the item module's <c>BuffOnAllowAdd</c> question is the first thing that asks the loadout about the hero — while retail
    /// runs <c>SetVarsByLevel</c> at character creation (Reconstruction, see <c>BbLoadoutHooks.PageFor</c>), so the marker is one of the
    /// table's rows from the start. Either way it is a row the cast's all-or-none reservation must fit into, so this fills to a total
    /// rather than adding that many rows of its own.
    /// </summary>
    private static void Fill(MatchRuntime runtime, Entity unit, int count)
    {
        for (var index = 0; unit.Buffs.Count < count; index++)
        {
            var before = unit.Buffs.Count;
            TestAssert.True(runtime.Buffs.AddBuff(unit, $"Capacity{index}", BuffType.Internal,
                1000.0, unit.NetId, canMitigateDuration: false) is not null);
            TestAssert.True(unit.Buffs.Count > before, "a filler row has to open a row of its own");
        }
        TestAssert.Equal(count, unit.Buffs.Count, "buff table: " +
            string.Join(", ", unit.Buffs.Select(buff => buff.Name).Order(StringComparer.Ordinal)));
    }

    private static void Free(MatchRuntime runtime, Entity unit)
    {
        var filler = unit.Buffs.First(buff => buff.Name.StartsWith("Capacity", StringComparison.Ordinal));
        TestAssert.True(runtime.Buffs.RemoveBuff(unit, filler.Slot));
    }

    private static bool Has(Entity unit, string name) => unit.Buffs.Any(buff => buff.Name == name);
    private static Team Opposite(Team team) => team == Team.Order ? Team.Chaos : Team.Order;

    private static bool Cast(MatchRuntime runtime, Entity caster, int slot, Entity? target = null)
    {
        var point = target?.Position ?? caster.Position;
        var bytes = new byte[34];
        bytes[0] = 0xA2;
        bytes[5] = checked((byte)((slot << 1) | 1));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), caster.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(6), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(14), (float)point.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(18), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(26), (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), target?.NetId ?? 0);
        return runtime.Spells.HandleCastRequest(caster, bytes, runtime.World.Clock.Now());
    }
}
