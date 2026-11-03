using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Sessions;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static class ProgressionAssistCompositionChecks
{
    // Janna runs from her shipped scripts: a match builds the module when its first Janna is created through the session, and these
    // checks register their heroes directly, so they bind the module to the match the way the session does.
    private static void BindJanna(MatchRuntime runtime)
    {
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Janna")!;
        ((MatchGameplayHooks)runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(runtime.Spells);
    }

    // Lee Sin runs from his shipped scripts too (19 October 2026): the same binding.
    private static void BindLee(MatchRuntime runtime)
    {
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("LeeSin")!;
        ((MatchGameplayHooks)runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(runtime.Spells);
    }

    [Check("spells-leesin-b5-absorbed-mark-assists")]
    public static void LeeAbsorbedMarksAwardAssist()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in new byte[] { 0, 2 })
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                "LeeSin", "Annie");
            BindLee(runtime);
            var lee = AddHero(runtime, 50, "LeeSin", team, new(7000, 7000));
            var ally = AddHero(runtime, 51, "Annie", team, new(7000, 7300));
            var victim = AddHero(runtime, 52, "Annie", team == Team.Order ? Team.Chaos : Team.Order,
                new(7300, 7000));
            runtime.SpellBuffs.Add(victim, "OrdinaryShield", BuffType.CombatEnhancer, 20,
                victim.NetId, new BuffPayload { DamageShield = 10000 }, canMitigateDuration: false);
            var hp = victim.Health;
            TestAssert.True(runtime.Spells.HandleCastRequest(lee, Cast(slot, victim.Position, victim.NetId), 0));
            runtime.Spells.Tick(.25); runtime.Spells.Tick(.5);
            TestAssert.Near(hp, victim.Health, 0, "ordinary shield absorbs all spell damage");
            TestAssert.Near(0, lee.MatchStatistics.TotalDamageDealt, 0, "assist is independent of damage credit");
            var name = slot == 2 ? "BlindMonkEOne" : team == Team.Order ? "BlindMonkQOne" : "BlindMonkQOneChaos";
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == name));
            runtime.Combat.CombatTick(12, regenerate: false);
            var gold = lee.Gold;
            TestAssert.True(runtime.Combat.Kill(victim, ally.NetId));
            TestAssert.Equal(1, lee.ChampionAssists, "LEE-04: reveal mark supplies the shared fifteen-second credit");
            TestAssert.Near(400 * ProgressionRules.GoldPoolForAssist, lee.Gold - gold, .001);
        }
    }

    [Check("runtime.progression-assist-markers")]
    public static void ChampionSupportMarkersReachCombat()
    {
        JannaShieldMarker();
        NidaleeHealMarker();
        EzrealAllyWMarker();
    }

    [Check("runtime.progression-support-assist-retail-duration")]
    public static void JannaShieldUsesRetailAssistDuration()
    {
        foreach (var (elapsed, eligible) in new[] { (12.0, true), (15.1, false) })
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, "Janna", "Blitzcrank");
            BindJanna(runtime);
            var janna = AddHero(runtime, 1, "Janna", Team.Order, new(5000, 5000));
            var ally = AddHero(runtime, 2, "Blitzcrank", Team.Order, new(5050, 5000));
            var victim = AddHero(runtime, 3, "Blitzcrank", Team.Chaos, new(5100, 5000));
            TestAssert.True(runtime.Spells.HandleCastRequest(janna,
                Cast(2, janna.Position, ally.NetId), 0.0), "Janna E accepted");
            runtime.Combat.CombatTick(elapsed);
            var goldBefore = janna.Gold;
            TestAssert.True(runtime.Combat.Kill(victim, ally.NetId), "shield recipient kills");
            TestAssert.Equal(eligible ? 1 : 0, janna.ChampionAssists,
                "support-only assist survives twelve seconds but expires after fifteen");
            TestAssert.Near(eligible ? 400.0 * ProgressionRules.GoldPoolForAssist : 0.0,
                janna.Gold - goldBefore, 1e-9, "support-only assist reward");
            TestAssert.Equal(0, janna.ChampionKills, "shield supplies no damage kill credit");
        }
    }

    // 19 October 2026: Lee Sin and Ezreal run from their shipped scripts. A match binds a script champion's module when its first hero is
    // created through the session; this check registers its heroes directly, so it binds the module the way the session does.
    private static void BindScripted(MatchRuntime runtime, string champion)
    {
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule(champion)!;
        ((MatchGameplayHooks)runtime.Dependencies.ChampionHooks).ScriptHooks = module.Bind(runtime.Spells);
    }

    [Check("runtime.b4-cripple-and-essence-flux-assist-branches")]
    public static void CrippleAndEssenceFluxAssistBranches()
    {
        foreach (var cripple in new[] { false, true })
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, "LeeSin", "Blitzcrank");
            BindScripted(runtime, "LeeSin");
            var lee = AddHero(runtime, 30, "LeeSin", Team.Order, new(7000, 7000));
            var ally = AddHero(runtime, 31, "Annie", Team.Order, new(7000, 7300));
            var victim = AddHero(runtime, 32, "Blitzcrank", Team.Chaos, new(7300, 7000));
            var hp = victim.Health;
            TestAssert.True(runtime.Spells.HandleCastRequest(lee, Cast(2, lee.Position, lee.NetId), 0), "E1 accepted");
            runtime.Spells.Tick(.4);
            TestAssert.True(victim.Health < hp, "E1 creates ordinary damage credit");
            // playtest B4 / retail sweep, 27 Sept 2026: E2 must be accepted
            // inside its three-second window, before E1's credit later expires.
            runtime.Combat.CombatTick(2, regenerate: false);
            runtime.Spells.Tick(2);
            hp = victim.Health;
            if (cripple)
            {
                lee.Mana = lee.MaxMana;
                TestAssert.True(runtime.Spells.HandleCastRequest(lee, Cast(2, lee.Position, lee.NetId), 2), "E2 accepted");
                runtime.Spells.Tick(.3);
                runtime.Spells.Tick(.5);
                TestAssert.True(victim.Buffs.Any(buff => buff.Name == "BlindMonkETwoMissile"), "E2 lands");
                TestAssert.Near(hp, victim.Health, 0, "Cripple extends credit without damage");
            }
            runtime.Combat.CombatTick(14, regenerate: false);
            TestAssert.True(runtime.Combat.Kill(victim, ally.NetId), "ally kills after E1's fifteen-second expiry");
            TestAssert.Equal(cripple ? 1 : 0, lee.ChampionAssists,
                "E2 marker uses the centrally applied retail fifteen-second override");
        }
        foreach (var shielded in new[] { false, true })
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, "Ezreal", "Blitzcrank");
            BindScripted(runtime, "Ezreal");
            var ezreal = AddHero(runtime, 40, "Ezreal", Team.Order, new(7000, 7000));
            var ally = AddHero(runtime, 41, "Annie", Team.Order, new(7000, 7300));
            var victim = AddHero(runtime, 42, "Blitzcrank", Team.Chaos, new(7300, 7000));
            if (shielded)
                runtime.SpellBuffs.Add(victim, "BansheesVeil", BuffType.Aura, 10, victim.NetId,
                    canMitigateDuration: false);
            var hp = victim.Health;
            TestAssert.True(runtime.Spells.HandleCastRequest(ezreal, Cast(1, victim.Position, 0), 0), "enemy W accepted");
            // The script engine flies the bolt in the shared projectile phase of SpellSystem.Tick (the hand-written module used the late tick).
            runtime.Spells.Tick(.25);
            runtime.Spells.Tick(.5);
            // EzrealEssenceFluxMissile breaks the target's spell shields (BBBreakSpellShields) before its damage block.
            // Batch 56 (811c362): the popped veil's BuffOnPreDamage zeroes that same hit (BansheesVeil.luaobj), so the shield
            // is spent and no damage is dealt. The damage credit follows the damage (CombatService.RecordDamageCredit ignores a
            // zero hit) and the popped veil refuses the attack-speed debuff too, so a shielded W credits nothing; whether retail
            // credits an absorbed hit is not recovered.
            if (shielded)
                TestAssert.False(victim.Buffs.Any(buff => buff.Name == "BansheesVeil"), "W consumed the shield");
            if (shielded)
                TestAssert.Near(hp, victim.Health, 0, "W breaks the spell shield and the popped veil zeroes its damage");
            else
                TestAssert.True(victim.Health < hp, "unshielded W creates damage credit");
            TestAssert.True(runtime.Combat.Kill(victim, ally.NetId), "ally kills W target");
            TestAssert.Equal(shielded ? 0 : 1, ezreal.ChampionAssists,
                "hostile W grants damage credit unless the popped veil zeroed it");
        }
    }

    private static void JannaShieldMarker()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, "Janna", "Blitzcrank");
        BindJanna(runtime);
        var janna = AddHero(runtime, 1, "Janna", Team.Order, new(5000, 5000));
        var ally = AddHero(runtime, 2, "Blitzcrank", Team.Order, new(5050, 5000));
        var victim = AddHero(runtime, 3, "Blitzcrank", Team.Chaos, new(5100, 5000));
        TestAssert.True(runtime.Spells.HandleCastRequest(janna,
            Cast(2, janna.Position, ally.NetId), 0.0),
            "Janna E production cast accepted");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "EyeOfTheStorm"),
            "Janna E production shield installed");
        TestAssert.True(runtime.Combat.Kill(victim, ally.NetId),
            "Janna shield recipient kills victim");
        TestAssert.Equal(1, janna.ChampionAssists,
            "Janna E marker reaches champion assist credit");
    }

    private static void NidaleeHealMarker()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, "Nidalee", "Blitzcrank");
        BindScripted(runtime, "Nidalee");
        var nidalee = AddHero(runtime, 10, "Nidalee", Team.Order, new(5000, 5000));
        var ally = AddHero(runtime, 11, "Ezreal", Team.Order, new(5050, 5000));
        var victim = AddHero(runtime, 12, "Blitzcrank", Team.Chaos, new(5100, 5000));
        ally.Health = ally.MaxHealth * 0.5;
        TestAssert.True(runtime.Spells.HandleCastRequest(nidalee,
            Cast(2, nidalee.Position, ally.NetId), 0.0),
            "Nidalee E production cast accepted");
        runtime.Spells.Tick(1.0);
        runtime.Spells.TickLateChampions(1.0);
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "PrimalSurge"),
            "Nidalee production heal buff installed");
        TestAssert.True(runtime.Combat.Kill(victim, ally.NetId),
            "Nidalee heal recipient kills victim");
        TestAssert.Equal(1, nidalee.ChampionAssists,
            "Nidalee heal marker reaches champion assist credit");
    }

    private static void EzrealAllyWMarker()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, "Ezreal", "Blitzcrank");
        BindScripted(runtime, "Ezreal");
        var ezreal = AddHero(runtime, 20, "Ezreal", Team.Order, new(5000, 5000));
        var ally = AddHero(runtime, 21, "Janna", Team.Order, new(5050, 5000));
        var victim = AddHero(runtime, 22, "Blitzcrank", Team.Chaos, new(5100, 5000));
        TestAssert.True(runtime.Spells.HandleCastRequest(ezreal,
            Cast(1, ezreal.Position, ally.NetId), 0.0),
            "Ezreal W production cast accepted");
        runtime.Spells.Tick(0.3);
        runtime.Spells.Tick(0.5);
        runtime.Spells.TickLateChampions(0.8);
        runtime.Spells.TickLateChampions(0.1);
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "EzrealEssenceFlux"),
            "Ezreal ally W production buff installed");
        TestAssert.True(runtime.Combat.Kill(victim, ally.NetId),
            "Ezreal W recipient kills victim");
        TestAssert.Equal(1, ezreal.ChampionAssists,
            "Ezreal ally W marker reaches champion assist credit");
    }

    private static Entity AddHero(MatchRuntime runtime, uint id, string champion,
        Team team, Position2 position)
    {
        var hero = new Entity(id, EntityKind.Hero, UnitType.Hero, team, position)
        {
            ChampionName = champion,
            Alive = true,
            Created = true,
            Visible = true,
            Level = 18,
            SpellRanks = [1, 1, 1, 1],
        };
        runtime.World.Register(hero);
        runtime.HeroStats.Refresh(hero);
        hero.Health = hero.MaxHealth;
        hero.Mana = hero.MaxMana;
        return hero;
    }

    private static byte[] Cast(byte slot, Position2 position, uint targetNetId)
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
