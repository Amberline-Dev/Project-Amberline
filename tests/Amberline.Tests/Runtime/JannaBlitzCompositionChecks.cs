using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static class JannaBlitzCompositionChecks
{
    // Janna runs from her shipped scripts (19 October 2026). A match builds the module when its first Janna is created through the session;
    // these checks register their heroes directly, so they bind the module to the match the way the session does.
    private static BbChampionModule BindJanna(MatchRuntime runtime)
    {
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Janna")!;
        ((MatchGameplayHooks)runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(runtime.Spells);
        return module;
    }

    [Check("runtime.janna-e-independent-buffs")]
    public static void EyeOfTheStormDamageBonusSurvivesShieldBreakForItsOwnLifetime()
    {
        // EyeOfTheStorm.TargetExecute adds two buffs of five seconds: the shield (TotalArmorAmount = {80,120,160,200,240} + 0.9 AP, absorbed
        // by its own BuffOnPreDamage, removed by the hit that exceeds it) and JannaEoTSBuff (+{14,23,32,41,50} attack damage). Neither
        // removal touches the other.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var turretTarget in new[] { false, true })
        for (var rank = 1; rank <= 5; rank++)
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, "Janna", "Blitzcrank");
            BindJanna(runtime);
            var janna = AddHero(runtime, 1, "Janna", team, new(5000, 5000));
            janna.SpellRanks[2] = rank;
            janna.AbilityPower = 100;
            var target = turretTarget
                ? runtime.World.Register(new Entity(2, EntityKind.Turret, UnitType.Turret, team, new(5100, 5000))
                    { Name = team == Team.Order ? "Turret_T1_C_05" : "Turret_T2_C_05" })
                : AddHero(runtime, 2, "Blitzcrank", team, new(5100, 5000));
            if (turretTarget) TurretRules.Initialize(target, 5, GameData.Default);
            var enemy = AddHero(runtime, 3, "Blitzcrank", team == Team.Order ? Team.Chaos : Team.Order, new(5200, 5000));
            var baseDamage = target.AttackDamage;
            var bonus = new[] { 14, 23, 32, 41, 50 }[rank - 1];
            var shieldAmount = new[] { 80, 120, 160, 200, 240 }[rank - 1] + 90;
            TestAssert.True(runtime.Spells.HandleCastRequest(janna, Cast(2, target.Position, target.NetId), 0));
            TestAssert.Near(baseDamage + bonus, target.AttackDamage, 1e-8);
            var shield = target.Buffs.Single(b => b.Name == "EyeOfTheStorm");
            TestAssert.Equal(0.0, shield.Payload.AttackDamage, "shield and damage bonus have independent removal");
            TestAssert.Equal((double)bonus, target.Buffs.Single(b => b.Name == "JannaEoTSBuff").Payload.AttackDamage);
            runtime.Buffs.TickBuffs(target, 1);
            var health = target.Health;
            // A hit below the shield is absorbed whole and keeps the buff; the hit that exceeds what is left breaks it and only the excess
            // reaches the health (the script's numbers are float32: the tolerance is far below one point).
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, target.NetId, shieldAmount - 1, DamageType.True, DamageSourceKind.Spell));
            TestAssert.Near(health, target.Health, 1e-4, $"rank {rank}: the shield absorbs {shieldAmount - 1}");
            TestAssert.True(target.Buffs.Any(b => b.Name == "EyeOfTheStorm"), "an unexhausted shield stays");
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 2, DamageType.True, DamageSourceKind.Spell));
            TestAssert.Near(health - 1, target.Health, 1e-4);
            TestAssert.False(target.Buffs.Any(b => b.Name == "EyeOfTheStorm"));
            TestAssert.Near(baseDamage + bonus, target.AttackDamage, 1e-8, "breaking E retains its five-second AD buff");
            if (turretTarget)
            {
                // Spells/TurretBonus.luaobj's BuffOnUpdateStats is three BBIncPermanentStat lines under the add's
                // TickRate 60; the +6 flat physical one is TurretRules.BonusDamage. Batch 159 part 1 deleted the
                // hand-written stack TurretRules.Tick applied, so this writes that one permanent the way the script's
                // lands on a non-hero (BbSpellHost.PermanentOnPet: pet.AttackDamage += delta) instead of running a
                // minute of buff clock, which would expire the five-second E buff the check is about. Growth through
                // the live script path is covered by combat.turret-buff-growth.
                target.AttackDamage += TurretRules.BonusDamage;
                baseDamage += TurretRules.BonusDamage;
                TestAssert.Near(baseDamage + bonus, target.AttackDamage, 1e-8, "growth retains Janna E damage");
            }
            runtime.Buffs.TickBuffs(target, 3.999);
            TestAssert.Near(baseDamage + bonus, target.AttackDamage, 1e-8);
            runtime.Buffs.TickBuffs(target, 0.0011);
            TestAssert.False(target.Buffs.Any(b => b.Name == "JannaEoTSBuff"));
            TestAssert.Near(baseDamage, target.AttackDamage, 1e-8, "AD expiry reverses only its own bonus");
        }
    }

    [Check("runtime.janna-blitz-composition")]
    public static void ChampionHooksUseProductionServices()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, "Janna", "Blitzcrank");
        runtime.World.Clock.SetVirtual(0.0);
        BindJanna(runtime);
        var janna = AddHero(runtime, 1, "Janna", Team.Order, new Position2(5000, 5000));
        var blitz = AddHero(runtime, 2, "Blitzcrank", Team.Chaos, new Position2(5100, 5000));
        TestAssert.True(runtime.Dependencies.ChampionDispatcher.Handles(janna), "Janna is registered");
        // Blitzcrank runs from his shipped scripts since 19 October 2026 (a hero added without the session's HeroCreated binding has no script
        // module bound to the match): his Power Fist, Mana Barrier and Static Field are covered by BlitzcrankLiveChecks and the
        // scripting.bb-blitzcrank-* checks. Here he is only the opposing hero.

        foreach (var buff in janna.Buffs.ToArray()) runtime.Buffs.RemoveBuff(janna, buff.Slot);
        janna.ForcedMove = null;
        TestAssert.True(runtime.Spells.HandleCastRequest(janna, Cast(3, janna.Position), 0.0),
            "Janna R is accepted through shared casting");
        var channels = ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Janna")!).Channels!;
        TestAssert.True(channels.Of(janna) is null, "the 0.25 s windup has not ended: no channel yet");
        runtime.Spells.Tick(0.3);
        runtime.Spells.TickLateChampions(0.3);
        TestAssert.True(channels.Of(janna) is not null, "R starts the script channel when its windup ends");
        TestAssert.True(janna.Buffs.Any(buff => buff.Name == "ReapTheWhirlwind"), "ChannelingStart adds the Monsoon buff");
        runtime.Dependencies.ChampionHooks.CancelChannel(janna, "order");
        TestAssert.True(channels.Of(janna) is null,
            "production cancellation ends the script channel");
        TestAssert.False(janna.Buffs.Any(buff => buff.Name == "ReapTheWhirlwind"),
            "production cancellation removes channel buff");
    }

    private static Entity AddHero(MatchRuntime runtime, uint id, string champion,
        Team team, Position2 position)
    {
        var hero = new Entity(id, EntityKind.Hero, UnitType.Hero, team, position)
        {
            ChampionName = champion, Alive = true, Created = true, Level = 18,
            SpellRanks = [1, 1, 1, 1],
        };
        runtime.World.Register(hero);
        runtime.HeroStats.Refresh(hero);
        hero.Health = hero.MaxHealth;
        hero.Mana = hero.MaxMana;
        return hero;
    }

    private static byte[] Cast(byte slot, Position2 position, uint targetNetId = 0)
    {
        var packet = new PacketBuffer(34);
        packet.WriteBytes(new byte[5]);
        packet.WriteByte((byte)(slot << 1));
        for (var i = 0; i < 2; i++)
        {
            packet.WriteSingle(position.X);
            packet.WriteSingle(0.0);
            packet.WriteSingle(position.Z);
        }
        packet.WriteUInt32(targetNetId);
        return packet.ToArray();
    }
}
