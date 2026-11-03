using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("neutral.live-pending-basic-and-worm-hits-stay-on-target-life")]
    public static void NeutralQueuedHitsDoNotStrikeTheRevivedTargetLife()
    {
        // Batch 174b: the Baron's WormAttack is a scripted missile (CharScriptWorm's rotation casts it, and the missile lives in the script's
        // missile registry, not in Combat.PendingHits), so the window follows the missile in flight. The Baron has no basic attack
        // (CharScriptWorm sets SetCanAttack false), so the scripted branch is the only one here; the ordinary basic is the second block.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var victim = match.Hero(0);
            var killer = match.Hero(1);
            var worm = RegisterNearbyWorm(match, victim);
            // The Baron's WormAttack is its own script's cast on a hit, so the Worm binds its character script first.
            match.Runtime.Spells.UnitScripts!.BindUnit(worm, "Worm");
            TestAssert.True(match.Runtime.Combat.ApplyHit(victim.NetId, worm.NetId,
                1.0, DamageType.Physical, DamageSourceKind.SpellAoe),
                $"{team}: a real damage wake casts WormAttack");
            // The cast's windup comes first and the missile is in flight for about a tenth of a second, so the window steps in short steps
            // until the missile exists (the clock, the spell system, the buffs and the combat tick run together, as the production tick does).
            for (var step = 0; step < 300 && !WormAttackInFlight(match.Runtime, worm); step++)
                StepScripted(match.Runtime, .02);
            TestAssert.True(WormAttackInFlight(match.Runtime, worm), $"{team}: the live neutral attack owns a missile");
            var priorDeaths = victim.ChampionDeaths;
            TestAssert.True(match.Runtime.Combat.Kill(victim, killer.NetId));
            TestAssert.Equal(priorDeaths + 1, victim.ChampionDeaths,
                $"{team}: first death changes the target life identity");
            TestAssert.True(WormAttackInFlight(match.Runtime, worm),
                $"{team}: no combat tick retired the queued old-life missile yet");
            Revive(match, seat: 0, victim);
            var damageAfterRevive = victim.MatchStatistics.TotalDamageTaken;
            for (var frame = 0; frame < 24; frame++) StepScripted(match.Runtime, .1);
            TestAssert.Equal(damageAfterRevive, victim.MatchStatistics.TotalDamageTaken,
                $"{team}: an old-life projectile cannot damage Revive");
            // Batch 174b (open): the scripted missile is not retired by the death or the revive (BbChampionModule.cs:1282 keeps a launched missile
            // flying to its target object). What the check pins is the old one: the new life takes no damage from it inside the window.
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "WormAttack"),
                $"{team}: old WormAttack leaves no new-life debuff");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var basic = new Match("Annie", team))
            {
                var currentLife = basic.Hero(0);
                var attacker = RegisterNearbyWorm(basic, currentLife);
                TestAssert.True(basic.Runtime.Combat.QueueBasicAttack(attacker, currentLife));
                var basicBefore = currentLife.MatchStatistics.TotalDamageTaken;
                DrainPending(basic.Runtime.Combat);
                TestAssert.True(currentLife.MatchStatistics.TotalDamageTaken > basicBefore,
                    $"{team}: an ordinary current-life neutral basic still lands");
            }

            using var match = new Match("Annie", team);
            var victim = match.Hero(0);
            var worm = RegisterNearbyWorm(match, victim);
            // Batch 174b: the shot is the bound Worm's scripted WormAttack, so the caster dies only once that missile is in flight.
            match.Runtime.Spells.UnitScripts!.BindUnit(worm, "Worm");
            TestAssert.True(match.Runtime.Combat.ApplyHit(victim.NetId, worm.NetId,
                1.0, DamageType.Physical, DamageSourceKind.SpellAoe));
            for (var step = 0; step < 300 && !WormAttackInFlight(match.Runtime, worm); step++)
                StepScripted(match.Runtime, .02);
            TestAssert.True(WormAttackInFlight(match.Runtime, worm), $"{team}: the Worm launches its WormAttack before its death");
            var before = victim.MatchStatistics.TotalDamageTaken;
            TestAssert.True(match.Runtime.Combat.Kill(worm, match.Hero(1).NetId),
                $"{team}: Worm caster dies after its scripted shot is launched");
            for (var frame = 0; frame < 24; frame++) StepScripted(match.Runtime, .1);
            TestAssert.True(victim.MatchStatistics.TotalDamageTaken > before &&
                            victim.Buffs.Any(buff => buff.Name == "WormAttack"),
                $"{team}: a valid current-life WormAttack survives caster death");
        }
    }

    [Check("neutral.live-wrath-timer-retired-on-recipient-new-life")]
    public static void WormWrathDoesNotApplyAnOldAttachedTimerAfterRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var markerFull in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var victim = match.Hero(0);
            match.Hero(1).Position = new Position2(10000, 10000);
            var (ai, worm) = CreateWrathAi(match, victim);
            // Fill only the free slots: every hero already holds the permanent
            // ChampionChampionDelta catch-up controller from spawn.
            if (markerFull)
                for (var filler = 0; victim.Buffs.Count < 64; filler++)
                    TestAssert.True(match.Runtime.Buffs.AddBuff(victim,
                        $"WrathFullSlot{filler}", BuffType.Internal, 20.0,
                        victim.NetId) is not null);
            TestAssert.True(NeutralWormScriptView.Trigger(match.Runtime.Spells.UnitScripts!, match.Runtime.Buffs, worm, NeutralWormAction.Wrath));
            // The Wrath selection (BBForEachUnitInTargetAreaRandom, every 0.4 s) attaches its WrathDamage marker, which lasts 0.4 s. A full
            // table refuses the shipped add, so the marker and the pending hit are absent (batch 174 part 2: the script's capacity rule
            // replaces the hand-written "pending hit resolves without its marker").
            var stepped = 0.0;
            while (stepped < 1.5 && NeutralWormScriptView.Snapshot(match.Runtime.World, worm).PendingWrath.Count == 0)
            { NeutralWormScriptView.StepRuntime(match.Runtime, ai, .1); stepped += .1; }
            TestAssert.Equal(markerFull ? 0 : 1, NeutralWormScriptView.Snapshot(match.Runtime.World, worm).PendingWrath.Count,
                $"{team}/{markerFull}: the Wrath selection attaches its marker only when the table has room (stepped {stepped})");
            TestAssert.Equal(!markerFull,
                victim.Buffs.Any(buff => buff.Name == "WrathDamage"),
                $"{team}/{markerFull}: optional marker admission is visible");
            TestAssert.True(match.Runtime.Combat.Kill(victim, match.Hero(1).NetId));
            TestAssert.False(victim.Buffs.Any(buff => buff.Name == "WrathDamage"),
                $"{team}/{markerFull}: death removes the attached old-life marker");
            // A saturated table has no ReviveSpeedBoost slot until one inert
            // fixture filler is removed. Wrath's timer remains independent.
            if (markerFull)
            {
                var filler = victim.Buffs.First(buff => buff.Name == "WrathFullSlot0");
                match.Runtime.Buffs.RemoveBuff(victim, filler.Slot);
            }
            Revive(match, seat: 0, victim);
            var damageAfterRevive = victim.MatchStatistics.TotalDamageTaken;
            NeutralWormScriptView.StepRuntime(match.Runtime, ai, .5);
            TestAssert.Equal(damageAfterRevive, victim.MatchStatistics.TotalDamageTaken,
                $"{team}/{markerFull}: Wrath cannot hit the new target life");
            TestAssert.False(victim.Buffs.Any(buff => buff.Name is "WrathDamage" or "WrathDebuff"),
                $"{team}/{markerFull}: no attached marker or new-life debuff returns");
            TestAssert.Equal(0, NeutralWormScriptView.Snapshot(match.Runtime.World, worm).PendingWrath.Count,
                $"{team}/{markerFull}: old timer is consumed without applying");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var victim = match.Hero(0);
            match.Hero(1).Position = new Position2(10000, 10000);
            var (ai, worm) = CreateWrathAi(match, victim);
            TestAssert.True(NeutralWormScriptView.Trigger(match.Runtime.Spells.UnitScripts!, match.Runtime.Buffs, worm, NeutralWormAction.Wrath));
            // Batch 174b: the shipped selection attaches WrathDamage to the recipient when its WrathoftheAncients pick lands (0.4 s to 0.8 s
            // after the timer). The Baron's WrathTimer has no PersistsThroughDeath and leaves with its unit, so the marker has to be attached
            // before the death. The marker has no caster check: its own deactivation deals the damage after the death (no dead-attacker rule
            // exists in the damage path; the batch 174 part 2 reading that one did was a setup that killed the Baron first).
            var attachedFor = 0.0;
            while (attachedFor < 2.0 && !victim.Buffs.Any(buff => buff.Name == "WrathDamage"))
            { NeutralWormScriptView.StepRuntime(match.Runtime, ai, .1); attachedFor += .1; }
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "WrathDamage"), "the Wrath marker is attached before its caster dies");
            var before = victim.MatchStatistics.TotalDamageTaken;
            TestAssert.True(match.Runtime.Combat.Kill(worm, match.Hero(1).NetId));
            NeutralWormScriptView.StepRuntime(match.Runtime, ai, .5);
            TestAssert.True(victim.MatchStatistics.TotalDamageTaken > before &&
                            victim.Buffs.Any(buff => buff.Name == "WrathDebuff"),
                $"{team}: attached Wrath hits a living recipient after caster death");
        }
    }

    [Check("neutral.live-pending-life-guard-preserves-ga-and-yorick-transition")]
    public static void PendingTargetLifeIncludesYorickAndLeavesGuardianAngelAlone()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var ga = new Match("Annie", team))
            {
                var victim = ga.Hero(0);
                victim.Inventory = [new InventorySlot(3026), null, null, null, null, null];
                // Batch 149: a copy binds to its inventory slot on the next spell update
                // (BbItemModule.SyncInventory). The revival is the shipped scripts' own chain from there:
                // Items/3026.luaobj's UpdateSelfBuffActions (the 9 s poll) adds Spells/WillRevive.luaobj while
                // neither HasBeenRevived nor GuardianAngel is up, and only that window's BuffOnPreDamage
                // (CurHealth <= DamageAmount) turns a lethal hit into CurHealth - 1 and adds GuardianAngel.
                // The old hand-written arm read the inventory at damage time, so this fixture used to hit a
                // revived hero without ever binding the copy.
                ga.Runtime.Tick(0.01);
                ga.Runtime.HeroStats.Refresh(victim);
                var worm = RegisterNearbyWorm(ga, victim);
                TestAssert.True(ga.Runtime.Combat.QueueBasicAttack(worm, victim));
                var deaths = victim.ChampionDeaths;
                TestAssert.True(ga.Runtime.Combat.ApplyHit(ga.Hero(1).NetId,
                    victim.NetId, 100000.0, DamageType.Magic, DamageSourceKind.SpellAoe));
                TestAssert.True(victim.Alive && victim.ChampionDeaths == deaths &&
                                victim.Buffs.Any(buff => buff.Name == "GuardianAngel"),
                    $"{team}: real Guardian Angel retains the same living target identity");
                ga.Runtime.Combat.CombatTick(.1, regenerate: false);
                TestAssert.True(ga.Runtime.Combat.PendingHits.Any(hit =>
                        hit.SourceNetId == worm.NetId && hit.TargetNetId == victim.NetId),
                    $"{team}: life guard does not cancel a same-life GA windup");
            }

            using var yorick = new Match("Yorick", team, ally: true);
            var ally = yorick.Hero(2);
            TestAssert.True(yorick.TryCast(0, 3, ally),
                $"{team}: real Yorick R marks an allied hero");
            yorick.Runtime.Tick(1.0);
            TestAssert.True(ally.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"));
            TestAssert.True(yorick.Runtime.Combat.Kill(ally, yorick.Hero(1).NetId));
            yorick.Runtime.Tick(1.1);
            var temporaryLife = ally.YorickRevival ??
                throw new InvalidOperationException("Yorick R did not create its temporary life");
            TestAssert.True(ReferenceEquals(temporaryLife, ally.YorickRevival) && ally.Alive &&
                            ally.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
                $"{team}: temporary Yorick life reached its attackable phase");
            var wormAtAlly = RegisterNearbyWorm(yorick, ally);
            TestAssert.True(yorick.Runtime.Combat.QueueBasicAttack(wormAtAlly, ally));
            var countedDeaths = ally.ChampionDeaths;
            // A kill of the unit in its zombie life ends the life (MatchGameplayHooks.EndZombieLife), as the script's own ForceDead does.
            yorick.Runtime.Combat.Kill(ally, yorick.Hero(1).NetId);
            TestAssert.True(!ally.Alive && ally.YorickRevival is null &&
                            ally.ChampionDeaths == countedDeaths,
                $"{team}: Yorick's second death ends a life without another death count");
            TestAssert.True(yorick.Runtime.Combat.PendingHits.Any(hit =>
                    hit.SourceNetId == wormAtAlly.NetId && hit.TargetNetId == ally.NetId),
                $"{team}: the queued old temporary-life hit survives until real Revive");
            ally.SummonerCooldowns[0] = 0.0;
            Revive(yorick, seat: 2, ally);
            var damageAfterRevive = ally.MatchStatistics.TotalDamageTaken;
            DrainPending(yorick.Runtime.Combat);
            TestAssert.Equal(damageAfterRevive, ally.MatchStatistics.TotalDamageTaken,
                $"{team}: an old temporary-life shot cannot strike ordinary Revive");
            TestAssert.False(yorick.Runtime.Combat.PendingHits.Any(hit =>
                    hit.SourceNetId == wormAtAlly.NetId && hit.TargetNetId == ally.NetId),
                $"{team}: Yorick life mismatch retires the pending shot");
        }
    }

    private static Entity RegisterNearbyWorm(Match match, Entity victim) =>
        match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Monster),
            EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            victim.Position + new Position2(200, 0))
        {
            Name = "Worm", Alive = true, Created = true, Visible = true,
            Health = 2000, MaxHealth = 2000, AttackDamage = 180,
            AttackRange = 600, AttackPeriod = 1.0, AttackWindup = .3,
            MissileSpeed = 300, Radius = 65,
        });

    private static (NeutralAiService Ai, Entity Worm) CreateWrathAi(Match match,
        Entity victim)
    {
        var runtime = match.Runtime;
        var grid = runtime.Navigation;
        var sink = new ProtocolNeutralPacketSink(packet => runtime.Delivery.Enqueue(packet),
            runtime.SyncIds, runtime.World.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(runtime.World, runtime.GameData, sink,
            [12], grid);
        // Batch 174 part 2: production binds every camp member to its character script; the Baron's wrath is its scripted rotation.
        scheduler.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        scheduler.Start(0.0);
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds +
            NeutralDefinitions.Camps[12].InitialDelay);
        var worm = scheduler.MembersForCamp(12).Single(member => member.Name == "Worm").Entity;
        worm.Position = victim.Position + new Position2(200, 0);
        worm.Created = true;
        return (new NeutralAiService(runtime.World, scheduler, runtime.Movement,
            runtime.Combat, sink, runtime.Effects, runtime.SpellBuffs,
            runtime.GameData), worm);
    }

    private static void Revive(Match match, int seat, Entity hero)
    {
        if (hero.RespawnAt <= 0.0) match.Runtime.Combat.ScheduleRespawn(hero);
        hero.SummonerSpells[0] = "SummonerRevive";
        TestAssert.True(match.TryCast(seat, 0, hero, summoner: true),
            "the dead hero casts real Summoner Revive");
        TestAssert.True(hero.Alive && hero.YorickRevival is null,
            "Revive returned the same NetId to ordinary life");
    }

    /// <summary>One step of the scripted world in the order the production tick runs it: the clock, the spell system, the buffs, the combat tick.</summary>
    private static void StepScripted(Amberline.Network.Sessions.MatchRuntime runtime, double dt)
    {
        runtime.World.Clock.Advance(dt);
        runtime.Spells.Tick(dt);
        foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, dt);
        runtime.Combat.CombatTick(dt, regenerate: false);
    }

    /// <summary>Whether a WormAttack missile of this Worm is in flight: a live script missile of its caster (the registry BBDestroyMissile reads).</summary>
    private static bool WormAttackInFlight(Amberline.Network.Sessions.MatchRuntime runtime, Entity worm) =>
        runtime.Spells.UnitScripts!.LiveMissiles.Any(missile => missile.SpellName == "WormAttack" && missile.Caster.NetId == worm.NetId);

    private static void DrainPending(CombatService combat)
    {
        for (var frame = 0; frame < 24; frame++)
            combat.CombatTick(.1, regenerate: false);
    }
}
