using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-b4-guardian-excludes-yorick-temporary-life")]
    public static void GuardianAngelCannotReviveYorickZombie()
    {
        using var match = new Match("Yorick", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.Inventory = [new(3026), null, null, null, null, null];
        runtime.Buffs.AddBuff(hero, "HasBeenRevived", BuffType.CombatEnhancer, 2, hero.NetId);
        match.Cast(0, 3, hero);
        runtime.Tick(0.3);
        runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True);
        TestAssert.True(hero.YorickRevival is not null, "Yorick temporary life starts while GA is cooling down");
        runtime.Tick(1.1);
        TestAssert.True(hero.YorickRevival is not null && hero.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
            "the one-second delay is over and the zombie buff runs the life");
        // Yorick clears CombatEnhancer rows during revival. Seed a near-expiry
        // GA timer in the playable temporary life to exercise the expiry edge.
        TestAssert.True(runtime.Buffs.AddBuff(hero, "HasBeenRevived",
            BuffType.CombatEnhancer, 0.1, hero.NetId) is not null);
        runtime.Buffs.TickBuffs(hero, 0.11);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "HasBeenRevived"));
        runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True);
        TestAssert.False(hero.Alive, "lethal damage ends the zombie life");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name is "GuardianAngel" or "HasBeenRevived"),
            "GA neither activates nor consumes its cooldown during Yorick revival");
    }

    [Check("items-live-guardian-summoner-cooldown-floor")]
    public static void GuardianAngelFloorsBothSummonerTimersAndReplicatesChanges()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var timers in new[] { new[] { 0.0, 2.0 }, new[] { 2.0, 0.0 },
                     new[] { 4.0, 4.0 }, new[] { 4.01, 120.0 } })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            // Batch 149: the copy binds on the next spell update (BbItemModule.SyncInventory) and only then does
            // Items/3026.luaobj keep WillRevive alive, whose BuffOnPreDamage opens the revival. The timers are set
            // after that update so the bind's own clock does not walk a 4.01 into the floor range.
            match.Runtime.Tick(0.01);
            hero.SummonerCooldowns = timers.ToList();
            match.Runtime.Combat.ApplyHit(match.Hero(1).NetId, hero.NetId, 10000, DamageType.True);
            TestAssert.True(hero.Alive && hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
            match.Runtime.Delivery.Flush();
            var packets = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x8A &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
            TestAssert.Equal(timers.Count(timer => timer <= 4), packets.Length);
            for (var slot = 0; slot < 2; slot++)
            {
                TestAssert.Near(Math.Max(4, timers[slot]), hero.SummonerCooldowns[slot], 0);
                if (timers[slot] > 4) continue;
                var packet = packets.Single(packet => packet.Plaintext.Span[5] == (0x80 | slot));
                TestAssert.Near(4, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..]), 0);
            }
        }
    }

    [Check("items-live-guardian-summoner-cast-delay")]
    public static void GuardianAngelDelaysReadyCastsThenReleasesThem()
    {
        foreach (var spell in new[] { "SummonerHeal", "SummonerHaste", "SummonerMana", "SummonerTeleport" })
        foreach (var slot in new[] { 0, 1 })
        foreach (var noCooldowns in new[] { false, true })
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            hero.SummonerSpells[slot] = spell;
            hero.CooldownsDisabled = noCooldowns;
            var target = spell == "SummonerTeleport"
                ? match.Runtime.World.Entities.Values.First(unit => unit.UnitType == UnitType.Turret && unit.Team == hero.Team)
                : hero;
            // Batch 149: the copy binds on the next spell update (BbItemModule.SyncInventory), the same latency every
            // converted item check accounts for; the revival (and its cast delay) starts from WillRevive.
            match.Runtime.Tick(0.01);
            match.Runtime.Combat.ApplyHit(match.Hero(1).NetId, hero.NetId, 10000, DamageType.True);
            match.Runtime.Delivery.Flush();
            match.Clear();
            TestAssert.False(match.TryCast(0, slot, target, summoner: true), $"{spell}/{slot}: GA activation delays casting");
            TestAssert.Near(4, hero.SummonerCooldowns[slot], 0);
            TestAssert.Near(1, hero.Health, 0);
            TestAssert.Near(0, hero.Mana, 0);
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD));
            match.Runtime.Tick(3.99);
            TestAssert.False(match.TryCast(0, slot, target, summoner: true), $"{spell}/{slot}: delay lasts four seconds");
            match.Runtime.Tick(.02);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
            TestAssert.True(match.TryCast(0, slot, target, summoner: true), $"{spell}/{slot}: usable after revival");
        }
    }

    // GuardianAngel.luaobj OnBuffDeactivate opens with BBIncHealth(Owner, 750, HealerVar = Owner): a script heal, so
    // the client's heal routine announces UnitApplyHeal with the requested 750 and the heal event credits
    // TOTAL_HEAL / TOTAL_UNITS_HEALED to the healer (the revived hero) with the health actually restored.
    [Check("items-live-guardian-revival-heal-is-announced-and-counted")]
    public static void GuardianAngelRevivalHealIsAScriptHeal()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var enemyInFog in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            if (enemyInFog) enemy.Position = new Position2(1500, 13000);
            // Batch 149: the copy binds on the next spell update (BbItemModule.SyncInventory); Items/3026.luaobj then keeps
            // WillRevive alive and its BuffOnPreDamage is the revive the heal below announces.
            runtime.Tick(0.01);
            runtime.Delivery.Flush();
            TestAssert.Equal(!enemyInFog, runtime.Session.Participants[1].View.Visible.Contains(hero.NetId),
                "enemy sight of the hero");
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10000, DamageType.True));
            var guardian = hero.Buffs.Single(buff => buff.Name == "GuardianAngel");
            TestAssert.Near(1, hero.Health, 1e-9, "the lethal hit leaves one health");
            runtime.Delivery.Flush();
            TestAssert.Equal(0, match.Captures.Sum(capture => HandlerPackets(capture, HealOpcode, hero.NetId).Length),
                "nothing is announced before the revival completes");
            var statistics = hero.MatchStatistics;
            var healed = statistics.TotalHeal;

            match.Clear();
            TestAssert.True(runtime.Buffs.RemoveBuff(hero, guardian.Slot));
            runtime.Delivery.Flush();
            TestAssert.Near(751, hero.Health, 1e-9, "750 health on top of the one left");
            TestAssert.Near(Math.Min(375, hero.MaxMana), hero.Mana, 1e-9, "BBIncPAR restores 375 mana, capped at the pool");
            for (var seat = 0; seat < match.Captures.Count; seat++)
            {
                var sees = seat == 1 ? !enemyInFog : true;
                var heals = HandlerPackets(match.Captures[seat], HealOpcode, hero.NetId);
                TestAssert.Equal(sees ? 1 : 0, heals.Length, $"{team}/seat {seat}: one announcement per observer that sees the hero");
                foreach (var heal in heals)
                {
                    TestAssert.Equal(5000f, BinaryPrimitives.ReadSingleLittleEndian(heal.Plaintext.Span[5..]), "max health");
                    TestAssert.Equal(750f, BinaryPrimitives.ReadSingleLittleEndian(heal.Plaintext.Span[9..]), "healed amount");
                }
            }
            // The script names the owner as healer, so the restored health is the hero's own healing statistic.
            TestAssert.Equal(750, statistics.TotalHeal - healed, "TOTAL_HEAL gains the restored health");
            TestAssert.Equal(1, statistics.TotalUnitsHealed, "the hero is its own healed unit");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "HasBeenRevived"));
        }
    }
}
