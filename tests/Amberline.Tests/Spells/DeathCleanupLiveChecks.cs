using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-death-clears-current-roster-temporary-buffs")]
    public static void DeathClearsCurrentRosterTemporaryBuffs()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Annie", team))
            {
                match.Cast(0, 2, match.Hero(0));
                AssertDeathRemoves(match, match.Hero(0), "MoltenShield");
            }
            using (var match = new Match("Blitzcrank", team))
            {
                var hero = match.Hero(0);
                match.Cast(0, 1, hero);
                match.Cast(0, 2, hero);
                hero.Health = hero.MaxHealth * .201;
                TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero(1).NetId, hero.NetId,
                    30, DamageType.Magic, DamageSourceKind.Spell));
                TestAssert.True(hero.Alive && hero.Buffs.Any(buff => buff.Name == "ManaBarrier"),
                    "threshold crossing leaves a live Mana Barrier to retire on death");
                AssertDeathRemoves(match, hero, "Overdrive", "PowerFist", "ManaBarrier");
                // playtest B4 / retail sweep, 27 Sept 2026: the shipped
                // ManaBarrierCooldown omits PersistsThroughDeath (default false).
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ManaBarrierCooldown"),
                    "nonpersistent passive cooldown marker ends on death");
            }
            using (var match = new Match("Ezreal", team, ally: true))
            {
                var ally = match.Hero(2);
                match.Hero(1).Position = new Position2(11000, 10000);
                ally.Position = match.Hero(0).Position + new Position2(400, 0);
                match.CastGround(0, ally.Position, slot: 1);
                match.Runtime.Tick(.8);
                AssertDeathRemoves(match, ally, "EzrealEssenceFlux");
            }
            using (var match = new Match("Ezreal", team))
            {
                var enemy = match.Hero(1);
                enemy.Position = match.Hero(0).Position + new Position2(400, 0);
                match.CastGround(0, enemy.Position, slot: 1);
                match.Runtime.Tick(.8);
                AssertDeathRemoves(match, enemy, "EzrealEssenceFluxMissile");
            }
            using (var match = new Match("Janna", team, ally: true))
            {
                var ally = match.Hero(2);
                match.Cast(0, 2, ally);
                AssertDeathRemoves(match, ally, "EyeOfTheStorm", "JannaEoTSBuff");
            }
            using (var match = new Match("LeeSin", team, ally: true))
            {
                var hero = match.Hero(0);
                var ally = match.Hero(2);
                ally.Position = hero.Position + new Position2(400, 0);
                match.Cast(0, 1, ally);
                match.Runtime.Tick(.4);
                hero.Mana = hero.MaxMana;
                match.Cast(0, 1, hero);
                AssertDeathRemoves(match, hero, "BlindMonkWOneShield", "BlindMonkWTwo");
                TestAssert.True(ally.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"),
                    "Lee Sin's death does not erase a living ally's shield");
                AssertDeathRemoves(match, ally, "BlindMonkWOneShield");
            }
            using (var match = new Match("Nidalee", team, ally: true))
            {
                var hero = match.Hero(0);
                var ally = match.Hero(2);
                match.Cast(0, 2, ally);
                match.Runtime.Tick(.3);
                match.Cast(0, 3, hero);
                match.Runtime.Tick(.6);
                match.Cast(0, 0, match.Hero(1));
                AssertDeathRemoves(match, hero, "Takedown");
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"),
                    "Nidalee's persistent form survives death");
                TestAssert.True(ally.Buffs.Any(buff => buff.Name == "PrimalSurge"),
                    "living Surge recipient keeps its buff after caster death");
                AssertDeathRemoves(match, ally, "PrimalSurge");
            }
            using (var match = new Match("Orianna", team, ally: true))
            {
                var ally = match.Hero(2);
                ally.Position = match.Hero(0).Position + new Position2(400, 0);
                match.Cast(0, 2, ally);
                match.Runtime.Tick(.6);
                AssertDeathRemoves(match, ally, "OrianaRedactShield");
            }
            using (var match = new Match("Vayne", team))
            {
                var hero = match.Hero(0);
                // 19 October 2026 (batch 98): the persistent Silver Bolts aura is added by CharOnLevelUpSpell when W reaches level 1; this Match sets
                // the ranks directly, so the module's rank-up hook runs here the way SpellSystem.RankUp runs it.
                match.Runtime.Dependencies.ChampionDispatcher.OnRankUp(match.Runtime.Spells, hero, 1, 1);
                match.Runtime.Tick(.01);
                match.Cast(0, 3, hero);
                match.Cast(0, 0, hero);
                AssertDeathRemoves(match, hero,
                    "VayneTumbleBonus", "VayneTumbleFade", "VayneInquisition");
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "VayneSilveredBolts"),
                    "Vayne's persistent W survives death");
            }
            using (var match = new Match("Yorick", team))
            {
                var hero = match.Hero(0);
                match.Cast(0, 0, hero);
                TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, match.Hero(1).NetId,
                    hero.AttackDamage, DamageType.Physical, DamageSourceKind.Basic));
                AssertDeathRemoves(match, hero, "YorickActiveSpectral");
            }
        }
    }

    [Check("spells-live-janna-e-death-recipient-not-caster")]
    public static void JannaShieldAndDamageBuffBelongToRecipientDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team, ally: true);
            var janna = match.Hero(0);
            var ally = match.Hero(2);
            var baseAd = ally.AttackDamage;
            match.Cast(0, 2, ally);
            var ad = ally.AttackDamage;
            // EyeOfTheStorm.OnBuffActivate creates EyeoftheStorm_buf on its holder; the effect id is the one the create packet carries.
            var shieldFx = BinaryPrimitives.ReadUInt32LittleEndian(match.Captures[2].Packets.Single(packet =>
                Opcode(packet) == FxPackets.CreateGroupOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("EyeoftheStorm_buf")).Plaintext.Span[25..]);
            TestAssert.True(shieldFx != 0, "Janna's shield owns a persistent effect");
            TestAssert.True(ally.Buffs.Any(buff => buff.Name == "EyeOfTheStorm"));
            TestAssert.True(ally.Buffs.Any(buff => buff.Name == "JannaEoTSBuff"));
            TestAssert.True(match.Runtime.Combat.Kill(janna, match.Hero(1).NetId));
            TestAssert.True(ally.Buffs.Any(buff => buff.Name == "EyeOfTheStorm"));
            TestAssert.True(ally.Buffs.Any(buff => buff.Name == "JannaEoTSBuff"));
            TestAssert.Near(ad, ally.AttackDamage, 1e-9,
                "caster death leaves the living recipient's separate AD buff active");
            match.Clear();
            AssertDeathRemoves(match, ally, "EyeOfTheStorm", "JannaEoTSBuff");
            TestAssert.Near(baseAd, ally.AttackDamage, 1e-9,
                "recipient death reverses Janna's separate AD buff");
            TestAssert.Equal(1, match.Captures[2].Packets.Count(packet =>
                Opcode(packet) == 0x3D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == shieldFx),
                "recipient death retires the shield-owned effect exactly once");

            using var self = new Match("Janna", team);
            self.Cast(0, 2, self.Hero(0));
            AssertDeathRemoves(self, self.Hero(0), "EyeOfTheStorm", "JannaEoTSBuff");
        }
    }

    [Check("spells-live-blitzcrank-power-fist-death-cooldown")]
    public static void PowerFistDeathStartsCooldownAndRetiresOnlyUnlaunchedCast()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var rank in new[] { 1, 5 })
        foreach (var disabled in new[] { false, true })
        {
            using var match = new Match("Blitzcrank", team);
            var hero = match.Hero(0);
            hero.SpellRanks[2] = rank;
            TestAssert.True(match.Runtime.SpellBuffs.Add(hero, "PowerFistDeathCDR",
                BuffType.Internal, 60.0, hero.NetId,
                new BuffPayload { CooldownMod = -.2 }) is not null);
            TestAssert.Near(-.2, hero.CooldownMod, 1e-9,
                "a real stat buff supplies cooldown reduction through the cast");
            hero.CooldownsDisabled = disabled;
            match.Cast(0, 2, hero);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "PowerFist"));
            match.Clear();
            TestAssert.True(match.Runtime.Combat.Kill(hero, match.Hero(1).NetId));
            match.Runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "PowerFist"));
            var expected = disabled ? 0.0 :
                Amberline.Data.GameData.Default.Spell("PowerFist", 5).Cooldowns[rank - 1] * .8;
            TestAssert.Near(expected, hero.SpellCooldowns[2], 1e-9,
                "buff deactivation computes the learned-rank cooldown even on death");
            var ownerPackets = match.Captures[0].Packets;
            var deathIndex = ownerPackets.FindIndex(packet => Opcode(packet) == 0x61 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
            var cooldowns = ownerPackets.Where(packet => Opcode(packet) == 0x8A &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
            TestAssert.True(deathIndex >= 0, "death precedes cleanup in the maintained wire queue");
            TestAssert.Equal(1, cooldowns.Length, "one cooldown update on Power Fist death removal");
            TestAssert.True(ownerPackets.IndexOf(cooldowns[0]) > deathIndex,
                "cooldown follows death in the maintained wire queue");
            TestAssert.Equal((byte)2, cooldowns[0].Plaintext.Span[5],
                "Power Fist deactivation updates E, not another slot");
            TestAssert.Near(expected,
                BinaryPrimitives.ReadSingleLittleEndian(cooldowns[0].Plaintext.Span[6..]),
                1e-5, "wire cooldown agrees with the authoritative remaining time");
            match.Runtime.Combat.ScheduleRespawn(hero);
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(hero));
            if (!disabled)
            {
                TestAssert.False(match.TryCast(0, 2, hero),
                    "revived Blitzcrank cannot bypass the retained E cooldown");
                match.Runtime.Tick(expected + .05);
            }
            hero.Mana = hero.MaxMana;
            TestAssert.True(match.TryCast(0, 2, hero),
                "E becomes castable only after cooldown or with cooldowns disabled");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match("Blitzcrank", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(1000, 0);
            match.CastGround(0, enemy.Position);
            if (launched)
                match.Runtime.Tick(Amberline.Data.GameData.Default.Spell("RocketGrabMissile").CastDelay + .02);
            // Rocket Grab is a script line missile (BbLineMissile) in the shared projectile queue since 19 October 2026.
            IReadOnlyList<SharedProjectileSnapshot> Lines() => match.Runtime.Spells.SharedProjectiles.Select(p => p.Snapshot)
                .Where(p => p.CasterNetId == hero.NetId && p.MissileNetId != 0).ToArray();
            TestAssert.Equal(1, Lines().Count, "Q has exactly one line, waiting or launched");
            TestAssert.Equal(launched, Lines()[0].Launched, "the line leaves after the cast delay");
            TestAssert.True(match.Runtime.Combat.Kill(hero, enemy.NetId));
            match.Runtime.Tick(.05);
            TestAssert.Equal(launched, Lines().Count == 1,
                "death retires an unfinished Q cast and preserves only an already-launched line");
        }
    }

    [Check("spells-live-vayne-rapid-revive-death-buff-snapshot")]
    public static void VayneRapidReviveHasNoDeadTumbleOrFinalHourSnapshot()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var useSummonerRevive in new[] { false, true })
        {
            using var match = new Match("Vayne", team, enemyChampion: "Vayne");
            var hero = match.Hero(0);
            var mirror = match.Hero(1);
            LearnSilverBolts(match, hero, mirror);
            match.Runtime.Tick(.01);
            match.Cast(0, 3, hero);
            match.Cast(0, 0, hero);
            match.Cast(1, 3, mirror);
            match.Cast(1, 0, mirror);
            TestAssert.True(match.Runtime.Combat.Kill(hero, mirror.NetId));
            TestAssert.True(mirror.Buffs.Any(buff => buff.Name == "VayneInquisition") &&
                mirror.Buffs.Any(buff => buff.Name == "VayneTumbleBonus"),
                "dying Vayne's cleanup does not remove the opposite-team mirror's R/Q");
            match.Runtime.Combat.ScheduleRespawn(hero);
            if (useSummonerRevive)
            {
                hero.SummonerSpells[0] = "SummonerRevive";
                TestAssert.True(match.TryCast(0, 0, hero, summoner: true),
                    "dead-only Summoner Revive uses the production cast path");
            }
            else TestAssert.True(match.Runtime.Combat.RespawnImmediately(hero));
            TestAssert.False(hero.Buffs.Any(buff => buff.Name is
                "VayneTumbleBonus" or "VayneTumbleFade" or "VayneInquisition"));
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "VayneSilveredBolts"));

            // Reconnect follows the same production visibility/buff snapshot path.
            TestAssert.True(match.Runtime.Session.Disconnect(1));
            TestAssert.True(match.Runtime.Delivery.RemoveObserver(1));
            var reconnected = match.Runtime.Session.AuthenticateReconnect(101,
                match.Runtime.Session.BuildKeyCheck(1));
            TestAssert.True(reconnected is not null);
            TestAssert.True(match.Runtime.Session.Receive(101, 1, [0xC5]).Any(output =>
                Opcode(output.Packet) == 0x57),
                "reconnect begins with the stock staged handshake");
            match.Runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
            var spawnAt = reconnected!.SpawnAt ?? throw new InvalidOperationException(
                "reconnect handshake did not arm spawn");
            match.Runtime.World.Clock.SetVirtual(spawnAt);
            match.Runtime.Session.RunSchedulers();
            match.Runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
            match.Runtime.Session.RunSchedulers();
            TestAssert.True(reconnected.Spawned && reconnected.Loaded && reconnected.Ready,
                "reconnect completes its staged loading boundary");
            match.Runtime.Delivery.AddObserver(new ReplicationObserver(
                reconnected.PlayerId, reconnected.Team, hero.NetId, match.Captures[0],
                ready: reconnected.Ready, view: reconnected.View,
                clientId: reconnected.ClientId));
            match.Clear();
            match.Runtime.Delivery.Flush();
            // Batch 181: these are the wire names (the shipped BuffNames of VayneTumbleBonus and VayneTumbleFade; VayneInquisition has none).
            var forbidden = new[] { "VayneTumble", "VayneTumbleFade", "VayneInquisition" }
                .Select(SpellPackets.BuffNameHash).ToHashSet();
            var ownerBuffAdds = match.Captures[0].Packets.Where(packet =>
                Opcode(packet) == SpellPackets.BuffAddOpcode &&
                packet.Plaintext.Length >= 13 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId)
                .ToArray();
            TestAssert.True(ownerBuffAdds.Any(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                SpellPackets.BuffNameHash("VayneSilveredBolts")), // Batch 357 (restates batch 181): the binding
                "reconnect positively restores Vayne's persistent W buff");
            TestAssert.False(ownerBuffAdds.Any(packet =>
                forbidden.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]))),
                "reconnect does not replay dead-life Q/R buffs");
        }
    }

    [Check("spells-live-yorick-first-death-cleans-temporary-not-revival")]
    public static void YorickFirstDeathKeepsRevivalStateWithoutRepeatingCleanup()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team, ally: true);
            var yorick = match.Hero(0);
            var victim = match.Hero(2);
            var killer = match.Hero(1);
            match.Cast(0, 3, victim);
            match.Runtime.Tick(.3);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"));
            // The marker belongs to Yorick; the temporary protection belongs
            // to the recipient and must not be inherited by temporary life.
            match.Runtime.Buffs.AddBuff(victim, "MoltenShield", Amberline.Core.Buffs.BuffType.CombatEnhancer,
                10.0, victim.NetId, new Amberline.Core.Buffs.BuffPayload { Armor = 25.0 });
            match.Clear();
            TestAssert.True(match.Runtime.Combat.Kill(victim, killer.NetId));
            TestAssert.True(victim.YorickRevival is not null && victim.Alive);
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "MoltenShield"));
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "BeginZombie consumes the CombatEnhancer marker after the committed first death");
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickRADelay"),
                "independent revival state supplies the temporary-life delay");
            match.Runtime.Tick(1.1);
            TestAssert.True(victim.YorickRevival is not null && victim.Buffs.Any(buff => buff.Name == "YorickRAZombie"));
            match.Clear();
            match.Runtime.Combat.Kill(victim, killer.NetId);
            match.Runtime.Delivery.Flush();
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "MoltenShield"));
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                Opcode(packet) == 0x7F && packet.Plaintext.Length >= 10 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                SpellPackets.BuffNameHash("MoltenShield")), // Batch 357 (restates batch 181): the binding
                "zombie expiry does not repeat ordinary hero buff cleanup");
            TestAssert.True(yorick.Alive, "the caster remains independent of the marked ally's death");
        }
    }

    private static void AssertDeathRemoves(Match match, Entity recipient, params string[] aliases)
    {
        foreach (var alias in aliases)
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{recipient.ChampionName}: production path armed {alias}");
        var killer = recipient.NetId == match.Hero(1).NetId ? match.Hero(0) : match.Hero(1);
        TestAssert.True(match.Runtime.Combat.Kill(recipient, killer.NetId));
        match.Runtime.Delivery.Flush();
        foreach (var alias in aliases)
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == alias),
                $"{recipient.ChampionName}: death removed {alias}");
    }
}
