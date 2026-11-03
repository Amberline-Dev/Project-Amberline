using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("stats.live-damage-accounting")]
    public static void DamageStatisticsFollowResolvedHitsAndOwnership()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var target = match.Hero(1);
            var combat = match.Runtime.Combat;
            target.Armor = target.SpellBlock = 100;
            hero.ArmorPenetrationFlat = hero.ArmorPenetrationPercent = 0;
            hero.MagicPenetrationFlat = hero.MagicPenetrationPercent = 0;
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 200, DamageType.Physical));
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 100, DamageType.Magic));
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 25, DamageType.True));
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 150, DamageType.Physical,
                DamageSourceKind.Basic, typeByte: 0x83));
            var dealt = hero.MatchStatistics;
            var taken = target.MatchStatistics;
            TestAssert.Equal((250.0, 175.0, 50.0, 75.0),
                (dealt.TotalDamageDealt, dealt.PhysicalDamageDealt, dealt.MagicDamageDealt, dealt.LargestCriticalStrike));
            TestAssert.Equal((250.0, 175.0, 50.0),
                (taken.TotalDamageTaken, taken.PhysicalDamageTaken, taken.MagicDamageTaken));

            target.Protected = true;
            TestAssert.False(combat.ApplyHit(hero.NetId, target.NetId, 100, DamageType.True));
            target.Protected = false;
            match.Runtime.Buffs.AddBuff(target, "StatisticsShield", BuffType.CombatEnhancer, 30,
                target.NetId, new BuffPayload { DamageShield = 40 });
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 30, DamageType.True));
            TestAssert.Equal(250.0, dealt.TotalDamageDealt, "fully absorbed damage does not enter the hit totals");
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 20, DamageType.True));
            TestAssert.Equal(260.0, taken.TotalDamageTaken, "only shield overflow is recorded");

            target.Health = 5;
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 40, DamageType.True));
            TestAssert.Equal(0.0, target.Health);
            TestAssert.Equal(300.0, dealt.TotalDamageDealt, "totals use the resolved hit, including overkill");
            TestAssert.False(combat.ApplyHit(hero.NetId, target.NetId, 40, DamageType.True));
            TestAssert.Equal(300.0, taken.TotalDamageTaken, "a dead target cannot receive a duplicate hit");
            TestAssert.True(combat.RespawnImmediately(target));
            combat.Kill(hero);
            var pet = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Monster),
                EntityKind.Pet, UnitType.Monster, team, target.Position)
                { OwnerNetId = hero.NetId, Health = 100, MaxHealth = 100 });
            TestAssert.True(combat.ApplyHit(pet.NetId, target.NetId, 40, DamageType.True));
            TestAssert.Equal(340.0, dealt.TotalDamageDealt, "pet damage belongs to its dead owner");
            TestAssert.True(combat.ApplyHit(target.NetId, pet.NetId, 10, DamageType.True));
            TestAssert.Equal(0.0, dealt.TotalDamageTaken, "damage to a pet is not damage to its owner");
            TestAssert.Equal(10.0, taken.TotalDamageDealt, "dealt totals include non-champion targets");
            TestAssert.Equal(75.0, dealt.LargestCriticalStrike);
        }
    }

    [Check("stats.live-purchase-accounting")]
    public static void PurchaseStatisticsIgnoreRejectedRequestsSalesAndSwaps()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        hero.Inventory = Inventory.Empty();
        hero.Gold = 5000;
        var catalog = new ItemCatalog();
        bool Shop(byte[] packet) => match.Runtime.Session.DispatchShop(
            new SessionInput(1, 1, match.Runtime.Cipher.Encrypt(packet), packet, true));
        byte[] Buy(int itemId)
        {
            var packet = new byte[9]; packet[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), (uint)itemId);
            return packet;
        }
        TestAssert.False(Shop(Buy(1001)), "range rejection");
        match.Runtime.Combat.Kill(hero);
        match.Runtime.Combat.ScheduleRespawn(hero);
        double spent = 0;
        foreach (var id in new[] { 1001, 1042, 3006, 2003, 2003 })
        {
            TestAssert.True(Shop(Buy(id)), $"purchase {id}");
            // Components were bought first: Price is the shipped combine cost.
            spent += catalog.Get(id)!.Price;
        }
        var stats = hero.MatchStatistics;
        TestAssert.Equal((5, 2), (stats.ItemsPurchased, stats.ConsumablesPurchased));
        TestAssert.Equal(spent, stats.GoldSpent);
        TestAssert.Equal(5000 - spent, hero.Gold);
        TestAssert.Equal(3006, hero.Inventory[0]!.ItemId);
        TestAssert.Equal(2, hero.Inventory[1]!.Count);
        var swap = new byte[7]; swap[0] = 0x23; swap[5] = 0; swap[6] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(swap.AsSpan(1), hero.NetId);
        TestAssert.True(Shop(swap));
        var sell = new byte[6]; sell[0] = 0x09; sell[5] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(1), hero.NetId);
        TestAssert.True(Shop(sell));
        TestAssert.False(Shop(Buy(int.MaxValue)), "unknown item rejection");
        hero.Gold = 0;
        TestAssert.False(Shop(Buy(1001)), "insufficient gold rejection");
        TestAssert.Equal((spent, 5, 2), (stats.GoldSpent, stats.ItemsPurchased, stats.ConsumablesPurchased));
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(hero));
        TestAssert.Equal((spent, 5, 2), (stats.GoldSpent, stats.ItemsPurchased, stats.ConsumablesPurchased));
    }

    [Check("stats.live-cast-accounting")]
    public static void CastStatisticsCountExecutionInsteadOfQueuedIntention()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var stats = hero.MatchStatistics;
        enemy.Position = new(7800, 7000); // Outside Q range, still inside the player's vision.
        match.Cast(0, 0, enemy);
        TestAssert.True(hero.PendingCast is not null);
        TestAssert.Equal(0, stats.SpellCasts[0]);
        enemy.Position = new(7200, 7000);
        match.Runtime.Spells.Tick(.01);
        TestAssert.True(hero.PendingCast is null);
        TestAssert.Equal(1, stats.SpellCasts[0]);
        // Batch 346 (batch 340's rule): Q is a windup that starts its cooldown at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell),
        // and a second press of the slot inside the unpaid windup is refused (the client sends none). Run past the cast frame first, so the
        // rejection is the cooldown's and the hand-cleared cooldown below frees the slot for the queued cast.
        SpellStep(match, .5);
        TestAssert.False(match.Runtime.Spells.HandleCastRequest(hero, ActivityCastRequest(hero, enemy, 0), 0),
            "cooldown rejection is not a cast");
        hero.SpellCooldowns[0] = 0;
        enemy.Position = new(7800, 7000);
        match.Cast(0, 0, enemy);
        match.Move(0, new(7100, 7000));
        enemy.Position = new(7200, 7000);
        match.Runtime.Spells.Tick(.01);
        TestAssert.Equal(1, stats.SpellCasts[0], "movement cancels the queued cast without counting it");

        hero.SummonerSpells[0] = "SummonerHeal";
        hero.SummonerSpells[1] = "SummonerHaste";
        match.Cast(0, 0, hero, summoner: true);
        TestAssert.False(match.Runtime.Spells.HandleCastRequest(hero, ActivityCastRequest(hero, hero, 0, true), 0));
        match.Cast(0, 1, hero, summoner: true);
        TestAssert.Equal(new[] { 1, 1 }, stats.SummonerCasts.ToArray());
        match.Cast(0, 10, hero);
        TestAssert.Equal(new[] { 1, 0, 0, 0 }, stats.SpellCasts.ToArray(), "Recall has no Q/W/E/R count");
        match.Runtime.Combat.Kill(hero);
        match.Runtime.Combat.ScheduleRespawn(hero);
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(hero));
        TestAssert.Equal(new[] { 1, 1 }, stats.SummonerCasts.ToArray());

        foreach (var champion in new[] { "Annie", "Orianna", "LeeSin", "Nidalee", "Yorick",
                     "Shaco", "Ezreal", "Vayne", "Janna", "Blitzcrank" })
        {
            using var other = new Match(champion, Team.Chaos);
            other.Cast(0, 0, other.Hero(1));
            TestAssert.Equal(new[] { 1, 0, 0, 0 }, other.Hero(0).MatchStatistics.SpellCasts.ToArray(), champion);
        }
        // Lee Sin's E1/E2 timing (the brief cooldown between the two halves) belongs to his scripts now (BlindMonkEManager), not to a hand-written
        // window; this one only pins that the first activation counts as a cast of slot 2.
        using var lee = new Match("LeeSin", Team.Order);
        lee.Cast(0, 2, lee.Hero(1));
        TestAssert.Equal(1, lee.Hero(0).MatchStatistics.SpellCasts[2]);
    }

    [Check("stats.live-healing-accounting")]
    public static void HealingStatisticsUseActualGainPerTickAndUniqueRecipients()
    {
        using var match = new Match("Nidalee", Team.Order, ally: true);
        var hero = match.Hero(0);
        var ally = match.Hero(2);
        match.Runtime.HeroStats.Refresh(hero);
        match.Runtime.HeroStats.Refresh(ally);
        hero.Health = hero.MaxHealth;
        ally.Health = ally.MaxHealth - 20;
        match.Cast(0, 2, ally);
        match.Runtime.Spells.Tick(.3);
        TestAssert.Near(ally.MaxHealth, ally.Health, 1e-6);
        var stats = hero.MatchStatistics;
        TestAssert.Equal((20, 1), (stats.TotalHeal, stats.TotalUnitsHealed), "overhealing is discarded");
        match.Runtime.Buffs.AddBuff(ally, "StatisticsPeriodicHeal", BuffType.CombatEnhancer, 2,
            hero.NetId, new BuffPayload { Period = 1, HpPerTick = 2.75 });
        ally.Health = ally.MaxHealth - 10;
        match.Runtime.Buffs.TickBuffs(ally, 2);
        TestAssert.Near(ally.MaxHealth - 4.5, ally.Health, 1e-6);
        TestAssert.Equal((24, 1), (stats.TotalHeal, stats.TotalUnitsHealed), "two ticks truncate separately and share a recipient");
        hero.Health = hero.MaxHealth - .5;
        TestAssert.Equal(.5, Healing.Apply(hero, 100));
        TestAssert.Equal((24, 2), (stats.TotalHeal, stats.TotalUnitsHealed), "a fractional heal still adds its unique recipient");
        TestAssert.Equal(0.0, Healing.Apply(hero, 100), "full health is not a heal event");
        ally.Alive = false;
        TestAssert.Equal(0.0, Healing.Apply(ally, 100, hero), "dead units cannot be healed");
        TestAssert.Equal((24, 2), (stats.TotalHeal, stats.TotalUnitsHealed));

        hero.Health = hero.MaxHealth - 10;
        hero.HealthRegen = 2.75;
        match.Runtime.Combat.CombatTick(1, regenerate: true);
        TestAssert.Equal(26, stats.TotalHeal, "ordinary regeneration uses the same stat conversion");
        hero.HealthRegen = 0;
        hero.Position = new(26, 264);
        hero.Health = hero.MaxHealth - 3;
        // Retail sweep B4, 27 Sept 2026: cross a complete fountain tick.
        match.Runtime.Combat.CombatTick(1, regenerate: true);
        TestAssert.Equal(29, stats.TotalHeal, "fountain healing counts actual restoration");

        using var janna = new Match("Janna", Team.Chaos, ally: true);
        var healer = janna.Hero(0);
        var friend = janna.Hero(2);
        janna.Runtime.HeroStats.Refresh(healer);
        janna.Runtime.HeroStats.Refresh(friend);
        healer.Health = healer.MaxHealth - 5;
        friend.Health = friend.MaxHealth - 30;
        janna.Cast(0, 3, healer);
        // The first pulse comes when the 0.25 s windup ends; the spell step does not regenerate anyone.
        janna.Runtime.Spells.Tick(.3);
        janna.Runtime.Spells.TickLateChampions(.3);
        TestAssert.Equal((35, 2), (healer.MatchStatistics.TotalHeal, healer.MatchStatistics.TotalUnitsHealed));
        janna.Runtime.Tick(1);
        TestAssert.Equal(35, healer.MatchStatistics.TotalHeal, "later full-health pulses do not add healing");

        using var sustain = new Match("Annie", Team.Order);
        var attacker = sustain.Hero(0);
        var victim = sustain.Hero(1);
        attacker.Health -= 20;
        attacker.LifeSteal = .5;
        victim.Armor = victim.SpellBlock = 0;
        TestAssert.True(sustain.Runtime.Combat.ApplyHit(attacker.NetId, victim.NetId,
            20, DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Equal(10, attacker.MatchStatistics.TotalHeal);
        attacker.Health = attacker.MaxHealth - 5;
        attacker.SpellVamp = 1;
        TestAssert.True(sustain.Runtime.Combat.ApplyHit(attacker.NetId, victim.NetId,
            10, DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Equal((15, 1), (attacker.MatchStatistics.TotalHeal, attacker.MatchStatistics.TotalUnitsHealed),
            "lifesteal and spell vamp credit their actual restoration to the attacker");

        using var yorick = new Match("Yorick", Team.Chaos);
        var gravedigger = yorick.Hero(0);
        yorick.Runtime.HeroStats.Refresh(gravedigger);
        gravedigger.Health = gravedigger.MaxHealth - 5;
        yorick.Cast(0, 2, yorick.Hero(1));
        // Yorick runs from his scripts (batch 109): the spell step finishes the windup and runs the heal.
        yorick.Runtime.Spells.Tick(.3);
        TestAssert.Equal((5, 1), (gravedigger.MatchStatistics.TotalHeal, gravedigger.MatchStatistics.TotalUnitsHealed));
    }

    [Check("stats.live-final-activity-snapshot")]
    public static void FinalActivitySnapshotIsDetachedAndIncludesTheLethalHit()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var target = match.Hero(1);
        match.Cast(0, 2, hero);
        hero.SummonerSpells[0] = "SummonerHeal";
        hero.Health = hero.MaxHealth - 10;
        match.Cast(0, 0, hero, summoner: true);
        var nexus = match.Runtime.World.Entities.Values.Single(u => u.Kind == EntityKind.Nexus && u.Team != hero.Team);
        nexus.Protected = false;
        TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, nexus.NetId, 10000, DamageType.True));
        var result = match.Runtime.Session.Result!;
        var stats = result.Players[0].Statistics;
        TestAssert.Equal(8, result.SchemaVersion);
        TestAssert.Equal(10000.0, stats.TotalDamageDealt, "the terminal hit is included before result capture");
        TestAssert.Equal((10, 1), (stats.TotalHeal, stats.TotalUnitsHealed));
        TestAssert.Equal(new[] { 0, 0, 1, 0 }, stats.SpellCasts.ToArray());
        TestAssert.Equal(new[] { 1, 0 }, stats.SummonerCasts.ToArray());
        hero.SpellCooldowns[2] = 0;
        TestAssert.True(match.Runtime.Spells.HandleCastRequest(hero, ActivityCastRequest(hero, hero, 2), 0));
        hero.Health -= 5;
        Healing.Apply(hero, 5);
        match.Runtime.Combat.ApplyHit(hero.NetId, target.NetId, 20, DamageType.True);
        TestAssert.Equal(1, stats.SpellCasts[2], "cast arrays are detached");
        TestAssert.Equal(10, stats.TotalHeal);
        TestAssert.Equal(10000.0, stats.TotalDamageDealt);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var saved = json.RootElement.GetProperty("Players")[0].GetProperty("Statistics");
        TestAssert.Equal(10000.0, saved.GetProperty("TotalDamageDealt").GetDouble());
        TestAssert.Equal(1, saved.GetProperty("SummonerCasts")[0].GetInt32());
    }

    private static byte[] ActivityCastRequest(Entity hero, Entity target, int slot, bool summoner = false)
    {
        var request = new byte[34]; request[0] = 0xA2;
        request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
        return request;
    }
}
