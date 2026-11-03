using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 203: APBonusDamageToTowers runs from its shipped script. Every champion's CharOnActivate adds it (BBSpellBuffAdd, BUFF_RENEW_EXISTING,
/// BUFF_Internal, 25000 s), and the buff's hooks are the tower bonus (BuffOnHitUnit), FortifyCheck per summoner slot holding SummonerFortify
/// (OnBuffActivate) and DisconnectTimer (BuffOnDisconnect). DisconnectTimer's BuffOnTakeDamage then spawns the placemarker that carries
/// DisconnectTarget. A champion without a module gets the same add from HeroCreated. These checks drive the real session and the real combat path.
/// </summary>
public static class BbApBonusTowerChecks
{
    [Check("scripting.bb-ap-bonus-tower-once-with-and-without-fortify")]
    public static void TowerBonusIsTheScriptsFormulaOnceWithAndWithoutFortify()
    {
        var runtime = Start(
        [
            new(1, 0, "Fortifier", "Annie", Team.Order, "SummonerFortify", "SummonerDot"),
            new(2, 1, "Plain", "Annie", Team.Order),
            new(3, 2, "Enemy", "Orianna", Team.Chaos),
            new(4, 3, "FarEnemy", "Ezreal", Team.Chaos),
        ]);
        var fortifier = runtime.Session.Participants[0].Hero!;
        var plain = runtime.Session.Participants[1].Hero!;
        foreach (var (label, hero) in new[] { ("Fortify", fortifier), ("plain", plain) })
        {
            // The shipped CharOnActivate add lands once: one live instance, as its BUFF_RENEW_EXISTING keeps it.
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
                $"{label}: APBonusDamageToTowers from the shipped CharOnActivate, one instance");
            TowerHitOnce(runtime, hero, label);
        }
        // FortifyCheck is the shipped OnBuffActivate's add for each summoner slot that holds SummonerFortify, and nothing else.
        TestAssert.Equal(1, fortifier.Buffs.Count(buff => buff.Name == "FortifyCheck"), "FortifyCheck once on the Fortify hero");
        TestAssert.False(plain.Buffs.Any(buff => buff.Name == "FortifyCheck"), "no FortifyCheck without SummonerFortify");
    }

    [Check("scripting.bb-ap-bonus-stand-in-for-a-champion-without-a-module")]
    public static void AChampionWithoutAModuleGetsTheShippedTowerBuff()
    {
        // Ashe is a bot champion with no BbChampionModule (ChampionInfo.RetailBot), so her CharOnActivate never runs. HeroCreated adds the
        // shipped APBonusDamageToTowers itself (a Reconstruction), which runs the tower bonus and DisconnectTimer like any other hero's.
        var runtime = Start(
        [
            new(1, 0, "Bot", "Ashe", Team.Order),
            new(2, 1, "Enemy", "Orianna", Team.Chaos),
            new(3, 2, "FarEnemy", "Ezreal", Team.Chaos),
        ]);
        var hero = runtime.Session.Participants[0].Hero!;
        TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"), "the stand-in add: one instance");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyCheck"), "no SummonerFortify on this seat, no FortifyCheck");
        TowerHitOnce(runtime, hero, "Ashe");
        TestAssert.True(runtime.Session.Disconnect(runtime.Session.Participants[0].ConnectionId!.Value), "the bot seat drops");
        TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "DisconnectTimer"), "DisconnectTimer once from the shipped BuffOnDisconnect");
    }

    [Check("scripting.bb-ap-bonus-client-name-and-internal-type")]
    public static void TheInternalBuffGoesOutUnderItsShippedNameAndTypeZero()
    {
        var data = GameData.Default;
        // The client resolves a buff's add through the name hash of its DATA/Spells file names (batch 199). ClientBuffName sends the binding name (Batch 357: binding stem; the script's
        // BuffName is never sent); both are APBonusDamageToTowers here.
        TestAssert.True(data.HasClientBuff("APBonusDamageToTowers"), "the client holds Spells/APBonusDamageToTowers.luaobj");
        TestAssert.False(data.HasBuffData("APBonusDamageToTowers"), "no DATA/Buffs icon: the binding name is sent");
        TestAssert.Equal("APBonusDamageToTowers", data.ClientBuffName("APBonusDamageToTowers"), "the name sent");
        TestAssert.Equal(0x0A04A1E3u, SpellPackets.BuffNameHash("APBonusDamageToTowers"), "the hash batch 199 recorded");
        using var match = new Match("Annie");
        var buff = Buff(match.Hero, "APBonusDamageToTowers") ?? throw new InvalidOperationException("the shipped add is on the hero");
        TestAssert.Equal(BuffType.Internal, buff.Type, "BUFF_Internal: wire type byte 0");
        TestAssert.False(buff.Payload.HiddenOnClient, "IsHiddenOnClient = false in the script");
    }

    [Check("scripting.bb-disconnect-timer-once-per-drop")]
    public static void DisconnectTimerIsAddedOncePerDropAndTheHitPlacesOneRunToMe()
    {
        using var match = new Match("Annie");
        var runtime = match.Runtime;
        var seat = runtime.Session.Participants[0];
        for (var cycle = 0; cycle < 2; cycle++)
        {
            TestAssert.True(runtime.Session.Disconnect(seat.ConnectionId!.Value), $"cycle {cycle}: the player disconnects");
            TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "DisconnectTimer"),
                $"cycle {cycle}: BuffOnDisconnect of the shipped add adds DisconnectTimer once");
            // BuffOnTakeDamage of DisconnectTimer spawns the placemarker (its skin, TestCube, is the name it keeps) and gives it
            // DisconnectTarget, once per damage instance. A spell-kind hit is used: a basic hit from Orianna also fires her on-hit proc,
            // which is a second damage instance and a second placemarker, as the shipped scripts make it.
            var known = new HashSet<uint>(runtime.World.Entities.Keys);
            var healthBefore = match.Hero.Health;
            TestAssert.True(runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 10, DamageType.Magic, DamageSourceKind.Spell), "the hit lands");
            TestAssert.True(match.Hero.Health < healthBefore, $"the hit takes health: {healthBefore} -> {match.Hero.Health}");
            var added = runtime.World.Entities.Values.Where(unit => !known.Contains(unit.NetId) &&
                unit.Buffs.Any(buff => buff.Name == "DisconnectTarget")).ToArray();
            TestAssert.Equal(1, added.Length, $"cycle {cycle}: one placemarker per hit on the dropped hero");
            TestAssert.Equal(1, added[0].Buffs.Count(buff => buff.Name == "DisconnectTarget"),
                $"cycle {cycle}: the placemarker holds one DisconnectTarget, not two");
            var participant = runtime.Session.AuthenticateReconnect(77, runtime.Session.BuildKeyCheck(seat.PlayerId));
            TestAssert.True(participant is not null, $"cycle {cycle}: the player reconnects");
            TestAssert.False(match.Hero.Buffs.Any(buff => buff.Name == "DisconnectTimer"),
                $"cycle {cycle}: the reconnect runs DisconnectTimer's BuffOnReconnect, which removes it");
        }
    }

    [Check("scripting.bb-fortify-passive-after-a-death")]
    public static void FortifyPassiveIsGoneAfterADeath()
    {
        // FortifyCheck is not PersistsThroughDeath (no Lua override, no spell-data flag), so the first death removes it, as the client's
        // persistence predicate does. The shipped add that puts it on the hero (APBonusDamageToTowers) persists, but no
        // shipped script runs that add's OnBuffActivate again, so nothing gives the passive back.
        // Batch 306 (owner decision, 26 October 2026: "Fortify follows the scripts"): batch 203's respawn renew of APBonusDamageToTowers is
        // deleted, so the passive stays gone after the respawn; APBonusDamageToTowers itself persists (its script says PersistsThroughDeath),
        // so the tower bonus stays. This check asserted the renew's return of FortifyCheck before.
        var runtime = Start(
        [
            new(1, 0, "Fortifier", "Annie", Team.Order, "SummonerFortify", "SummonerDot"),
            new(2, 1, "Enemy", "Orianna", Team.Chaos),
            new(3, 2, "FarEnemy", "Ezreal", Team.Chaos),
        ]);
        var hero = runtime.Session.Participants[0].Hero!;
        hero.Position = new Position2(7000, 7000);
        runtime.Buffs.TickBuffs(hero, 3.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"), "Fortify is ready: FortifyBuff from the shipped poll");
        runtime.Combat.Kill(hero, runtime.Session.Participants[1].Hero!.NetId);
        TestAssert.False(hero.Alive, "the hero dies");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyCheck"), "the first death removes FortifyCheck");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"), "and FortifyBuff with it");
        runtime.Combat.ScheduleRespawn(hero);
        for (var step = 0; step < 3000 && !hero.Alive; step++)
        {
            runtime.Combat.CombatTick(Step, regenerate: false);
            runtime.Combat.ProcessRespawns();
        }
        TestAssert.True(hero.Alive, "the hero respawns");
        runtime.Buffs.TickBuffs(hero, 3.0);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyCheck"), "no shipped script gives FortifyCheck back after the respawn");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"), "so no FortifyBuff either");
        TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "APBonusDamageToTowers persists through the death (its script's PersistsThroughDeath), once");
        // FortifyBuff's BuffOnHitUnit would add 9 to the swing against a minion (a target that is AI, not a turret and not a hero); without it
        // the swing is the plain attack damage. The respawn puts the hero back at its base, so it walks to the minion's side first.
        hero.Position = new Position2(7000, 7000);
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(7050, 7000)) { Health = 1000, MaxHealth = 1000 });
        hero.AttackCooldown = hero.AttackWindup = hero.MissileSpeed = 0;
        var before = minion.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, minion, new AttackRolls(1, 1, 1)), "the hero attacks the minion");
        runtime.Combat.CombatTick(0.001, regenerate: false);
        TestAssert.Near(hero.AttackDamage, before - minion.Health, 1e-8, "the swing against the minion carries no FortifyBuff 9");
    }

    [Check("scripting.bb-persist-through-death-data-flag")]
    public static void ABuffWithTheSpellDataBitSurvivesADeath()
    {
        // The client's persistence predicate is the script's PersistsThroughDeath OR the spell data's bit 8. RumbleCarpetBomb
        // has no script override and the bit, so it survives its holder's death; a buff with neither (FortifyCheck) does not (checked above).
        var runtime = Start(
        [
            new(1, 0, "Hero", "Annie", Team.Order),
            new(2, 1, "Enemy", "Orianna", Team.Chaos),
            new(3, 2, "FarEnemy", "Ezreal", Team.Chaos),
        ]);
        var hero = runtime.Session.Participants[0].Hero!;
        ScriptBuff(runtime, hero, "RumbleCarpetBomb", 25000.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RumbleCarpetBomb"), "the buff is on the hero");
        runtime.Combat.Kill(hero, runtime.Session.Participants[1].Hero!.NetId);
        TestAssert.False(hero.Alive, "the hero dies");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RumbleCarpetBomb"), "the spell-data bit keeps it through the death");
    }

    [Check("scripting.bb-ap-bonus-clone-names-no-summoner-spell")]
    public static void TheHallucinateCloneHoldsTheShippedTowerBuffWithoutAScriptFailure()
    {
        // HallucinateApplicator's OnBuffActivate adds the tower buff to the clone, so the buff's own OnBuffActivate reads the clone's summoner
        // slots (FortifyCheck's GetSlotSpellName test). A clone has no summoner book: that read must not fail the script.
        using var shaco = new Match("Shaco");
        shaco.Learn(1);
        shaco.Refill();
        shaco.Foe.Position = new Position2(12000, 12000);
        TestAssert.True(shaco.Cast(3), "Hallucinate cast accepted");
        for (var tick = 0; tick < 120 && !shaco.Runtime.World.Entities.Values.Any(unit =>
                 unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == shaco.Hero.NetId); tick++)
            shaco.Advance(Step);
        var clone = shaco.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == shaco.Hero.NetId);
        TestAssert.True(clone.Buffs.Any(buff => buff.Name == "APBonusDamageToTowers"), "the shipped scripts give the clone the tower buff");
        TestAssert.False(clone.Buffs.Any(buff => buff.Name == "FortifyCheck"), "a clone has no summoner spell, so no FortifyCheck");
        shaco.Advance(3.0);
        TestAssert.Equal(0, shaco.Module.ScriptFailures.Count + shaco.Runtime.Spells.UnitScripts!.ScriptFailures.Count,
            "no script failed on the clone's tower buff");
    }

    /// <summary>A live match of the given seats: each is authenticated, spawned, ready and started, with no automatic targeting.</summary>
    private static MatchRuntime Start(List<SessionParticipantDefinition> roster)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0, roster: roster);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        for (var i = 0; i < roster.Count; i++)
        {
            var id = i + 1;
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
            runtime.Session.Participants[i].Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(runtime.Session.Start(), "start");
        return runtime;
    }

    /// <summary>
    /// One basic attack on a tower by <paramref name="hero"/> with 20 bonus attack damage and 100 ability power: the shipped script adds
    /// AP / 2.5 - bonus AD = 20 to DamageAmount once, and armor 100 halves the hit: (AD + max(0, AP / 2.5 - bonus AD)) / 2.
    /// </summary>
    private static void TowerHitOnce(MatchRuntime runtime, Entity hero, string label)
    {
        var data = GameData.Default;
        var baseAttack = data.StatsAtLevel(data.Champion(hero.ChampionName), hero.Level).AttackDamage;
        hero.Position = new Position2(7000, 7000);
        hero.AttackDamage = baseAttack + 20;
        hero.AbilityPower = 100;
        hero.AttackCooldown = hero.AttackWindup = hero.MissileSpeed = 0;
        var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret), EntityKind.Turret,
            UnitType.Turret, Team.Chaos, new(7050, 7000)) { Health = 10000, MaxHealth = 10000, Armor = 100, SpellBlock = 900 });
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, turret, new AttackRolls(1, 1, 1)), $"{label}: attack queued");
        runtime.Combat.CombatTick(0.001, regenerate: false);
        TestAssert.Near((baseAttack + 20 + Math.Max(0.0, 100 / 2.5 - 20)) / 2, 10000 - turret.Health, 1e-8,
            $"{label}: the tower hit is the script's formula once");
        runtime.World.Entities.Remove(turret.NetId);
    }
}
