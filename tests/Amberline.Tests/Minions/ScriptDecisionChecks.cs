using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class ScriptDecisionChecks
{
    [Check("minions.b15-find-enemies-controls-autoattack")]
    public static void EnemyScanControlsAutoAttack()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var target = Enemy(runtime, new(7100, 7000));
        var ai = runtime.Minions!;
        minion.MinionState = MinionAiState.March;
        ai.TimerFindEnemies(minion);
        TestAssert.Equal(target.NetId, minion.AttackTarget ?? 0);
        TestAssert.False(minion.MinionAutoAttackEnabled,
            "the ATTACKMOVE entry branch closes without executing the attacking entry branch");
        runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId),
            "retaining an in-range target alone cannot start a basic attack");
        ai.TimerFindEnemies(minion);
        TestAssert.True(minion.MinionAutoAttackEnabled);
        runtime.Combat.CombatTick(.01, regenerate: false);
        TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId));
        runtime.Combat.CancelPendingHits(minion.NetId);
        minion.AttackCooldown = 2;
        target.Position = new(7250, 7000);
        ai.TimerFindEnemies(minion);
        TestAssert.True(minion.MinionAutoAttackEnabled,
            "the cancel band preserves permission between start reach and reach + 100");
        target.Position = new(7450, 7000);
        ai.TimerFindEnemies(minion);
        TestAssert.False(minion.MinionAutoAttackEnabled);
        TestAssert.Equal(target.NetId, minion.AttackTarget ?? 0, "outside cancel range still retains pursuit");
        // Minion.lua 0.2's cancel branch writes LastAttackScan = 0 (SETGLOBAL pc0079), a stamp the
        // script keeps in its own globals and never reads back — Minion.luaobj and minionLib.luaobj
        // have no reader for it. The hand-written scan mirrored it onto the unit and this assertion
        // read that mirror; the observable of the branch is the TurnOffAutoAttack just above, so the
        // mirror goes with the hand-written scan.
        runtime.Combat.CombatTick(.1, regenerate: false);
        TestAssert.Near(1.9, minion.AttackCooldown, 1e-9, "disabled AA does not pause cooldown");
        target.Position = new(7100, 7000);
        minion.MissileSpeed = 650;
        minion.AttackCooldown = 0;
        TestAssert.True(runtime.Combat.QueueBasicAttack(minion, target));
        runtime.Combat.CombatTick(minion.AttackWindup + .01, regenerate: false);
        TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId &&
            hit.WindupRemaining <= 0), "a launched missile exists before disabling AA");
        minion.MinionState = MinionAiState.March;
        minion.MinionAutoAttackEnabled = true;
        target.Position = new(8100, 7000);
        ai.TimerFindEnemies(minion);
        TestAssert.False(minion.MinionAutoAttackEnabled, "an empty scan explicitly turns AA off");
        var health = target.Health;
        for (var tick = 0; tick < 30; tick++) runtime.Combat.CombatTick(.1, regenerate: false);
        TestAssert.True(target.Health < health, "launched missiles still resolve after AA is disabled");
    }

    [Check("minions.b15-find-target-defers-unfinished-attack")]
    public static void FindTargetDefersUnfinishedAttack()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var old = Enemy(runtime, new(7100, 7000));
        minion.AttackTarget = old.NetId;
        minion.MinionState = MinionAiState.Engage;
        TestAssert.True(runtime.Combat.QueueBasicAttack(minion, old));
        var replacement = Enemy(runtime, new(7050, 7000));
        var ai = runtime.Minions!;
        ai.FindTargetOrMove(minion);
        TestAssert.Equal(old.NetId, minion.AttackTarget ?? 0, "unfinished attack cannot retarget");
        TestAssert.False(runtime.Combat.LastAutoAttackFinished(minion));
        TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId),
            "deferral must preserve the unlaunched attack");
        old.Alive = false;
        runtime.Combat.CancelPendingHits(minion.NetId);
        ai.Tick(.24);
        // minionLib 0.1 OnTargetLost re-decides inside the loss callback: a lost target runs
        // FindTargetOrMove at once, rather than waiting out the TimerFindEnemies that Minion.lua
        // 0.3 re-armed for DELAY_FIND_ENEMIES when the unfinished attack made it defer.
        TestAssert.Equal(replacement.NetId, minion.AttackTarget ?? 0,
            "the loss callback selects as soon as the unfinished attack releases");
        ai.Tick(.01);
        TestAssert.Equal(replacement.NetId, minion.AttackTarget ?? 0,
            "the restarted callback keeps the selection the loss already made");
    }

    [Check("minions.b15-collision-preserves-anti-kite-deadline")]
    public static void CollisionPreservesAntiKiteDeadline()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var old = Enemy(runtime, new(7400, 7000));
        var ai = runtime.Minions!;
        ai.FindTargetOrMove(minion);
        TestAssert.Equal(old.NetId, minion.AttackTarget ?? 0);
        ai.Tick(3.9);
        var deadline = ai.AntiKiteDeadline(minion);
        TestAssert.Near(4, deadline ?? -1, 1e-9);
        var blocker = Enemy(runtime, new(7100, 7000));
        blocker.Speed = 325;
        minion.Waypoints = [new(7300, 7000)];
        minion.AnchorPath = [minion.Position, .. minion.Waypoints];
        runtime.Movement.Tick(.1);
        ai.Tick(0);
        TestAssert.Equal(blocker.NetId, minion.AttackTarget ?? 0, "real enemy contact changed the selection");
        TestAssert.Equal(deadline, ai.AntiKiteDeadline(minion), "collision does not reset anti-kite");
        runtime.Movement.Tick(.1);
        ai.Tick(0);
        TestAssert.Equal(deadline, ai.AntiKiteDeadline(minion), "unchanged collision selection also preserves it");
    }

    [Check("minions.b15-taunt-begin-end-target-and-timers")]
    public static void TauntUsesItsTargetAndSuspendsAntiKite()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var ordinary = Enemy(runtime, new(7090, 7000));
        var taunter = Enemy(runtime, new(7120, 7000));
        var ai = runtime.Minions!;
        ai.FindTargetOrMove(minion);
        var taunt = runtime.Buffs.AddBuff(minion, "ScriptTaunt", BuffType.Taunt, 6,
            taunter.NetId, canMitigateDuration: false)!;
        TestAssert.Equal(MinionAiState.Taunted, minion.MinionState);
        TestAssert.Equal(taunter.NetId, minion.AttackTarget ?? 0);
        TestAssert.True(ai.AntiKiteDeadline(minion) is null);
        ai.Tick(5);
        TestAssert.Equal(taunter.NetId, minion.AttackTarget ?? 0);
        TestAssert.True(minion.MinionAutoAttackEnabled);
        TestAssert.True(ai.AntiKiteDeadline(minion) is null, "taunted in-range scans never rearm anti-kite");
        ai.OnTauntEnd(minion); // Native GetTauntTarget may still resolve at the end callback.
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState);
        TestAssert.Equal(taunter.NetId, minion.AttackTarget ?? 0);
        TestAssert.Near(9, ai.AntiKiteDeadline(minion) ?? -1, 1e-9);
        runtime.Buffs.RemoveBuff(minion, taunt.Slot);
        TestAssert.Equal(ordinary.NetId, minion.AttackTarget ?? 0,
            "without a remaining taunt target the end callback immediately runs selection");
    }

    /// <summary>Batch 478: counts the taunt hooks the buff engine raises on a unit, and drives it so the end hook runs.</summary>
    private sealed class TauntHookCounter : IAiHost
    {
        public int Begins, Ends;
        public bool Drives(Entity unit) => true;
        public void StepUnit(Entity unit, double now, double dt) { }
        public void OnTargetLost(Entity unit) { }
        public void OnReachedDestinationForGoingToLastLocation(Entity unit) { }
        public void OnCallForHelp(Entity unit, Entity aggressor) { }
        public void OnTakeDamage(Entity unit, Entity attacker) { }
        public void OnTauntBegin(Entity unit) => Begins++;
        public void OnTauntEnd(Entity unit) => Ends++;
        public void OnFearBegin(Entity unit) { }
        public void OnFearEnd(Entity unit) { }
        public void OnCanMove(Entity unit) { }
        public void OnCanAttack(Entity unit) { }
        public void HaltAI(Entity unit) { }
    }

    // Batch 478 (owner's native Dominion log, 29 October 2026): the neutral guardian renews its taunt on a minion every ~0.24 s by a
    // same-caster replacing add (BuffReplace 0x32). Each renewal ran the end hook and then the begin hook, so the minion flipped
    // taunted -> march -> taunted and re-routed twice a renewal. A renewal of a taunt the same caster holds raises neither hook.
    [Check("minions.b478-taunt-renewal-raises-no-taunt-hooks")]
    public static void TauntRenewalRaisesNoTauntHooks()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var taunter = Enemy(runtime, new(7120, 7000));
        var counter = new TauntHookCounter();
        var previous = runtime.Buffs.AiScripts;
        runtime.Buffs.AiScripts = counter;
        try
        {
            runtime.Buffs.AddBuff(minion, "RenewedTaunt", BuffType.Taunt, 1, taunter.NetId, canMitigateDuration: false);
            TestAssert.Equal(1, counter.Begins, "the first taunt begins once");
            counter.Begins = 0;
            runtime.Buffs.AddBuff(minion, "RenewedTaunt", BuffType.Taunt, 1, taunter.NetId, canMitigateDuration: false);
            TestAssert.Equal(1, minion.Buffs.Count(buff => buff.Type == BuffType.Taunt), "the renewal replaces the one taunt");
            TestAssert.Equal(0, counter.Ends, "a renewal does not end the taunt");
            TestAssert.Equal(0, counter.Begins, "a renewal does not begin the taunt again");
        }
        finally
        {
            runtime.Buffs.AiScripts = previous;
        }
    }

    [Check("minions.b15-halt-suppresses-callbacks")]
    public static void HaltSuppressesAllCallbacks()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var target = Enemy(runtime, new(7100, 7000));
        var ai = runtime.Minions!;
        ai.FindTargetOrMove(minion);
        ai.TimerFindEnemies(minion);
        runtime.Combat.CombatTick(.01, regenerate: false);
        ai.HaltAI(minion);
        TestAssert.True(runtime.Combat.LastAutoAttackFinished(minion), "halt cancels an unlaunched attack");
        var fear = runtime.Buffs.AddBuff(minion, "ScriptFear", BuffType.Fear, 3, target.NetId)!;
        var taunt = runtime.Buffs.AddBuff(minion, "ScriptTaunt", BuffType.Taunt, 3, target.NetId)!;
        ai.NotifyTargetLost(target.NetId);
        ai.OnCanMove(minion);
        ai.OnCanAttack(minion);
        ai.OnPathToTargetBlocked(minion);
        ai.TimerFindEnemies(minion);
        minion.HelpTarget = target.NetId;
        minion.HelpRemaining = 1;
        ai.Tick(10);
        TestAssert.True(minion.HelpTarget is null, "halted help events are consumed, never replayed on re-init");
        runtime.Buffs.RemoveBuff(minion, fear.Slot);
        runtime.Buffs.RemoveBuff(minion, taunt.Slot);
        TestAssert.Equal(MinionAiState.Halted, minion.MinionState);
        TestAssert.False(minion.MinionAutoAttackEnabled);
        TestAssert.True(ai.AntiKiteDeadline(minion) is null && minion.ForcedMove is null);
        minion.AttackCooldown = 0;
        runtime.Combat.CombatTick(.1, regenerate: false);
        TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId));
        ai.OnInit(minion);
        TestAssert.Equal(MinionAiState.Idle, minion.MinionState, "OnInit explicitly starts in IDLE");
        ai.Tick(0);
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState, "zero-delay forward timer leaves IDLE immediately");
    }

    [Check("minions.b15-control-release-runs-find-target")]
    public static void ControlReleaseRunsImmediateSelection()
    {
        foreach (var type in new[] { BuffType.Stun, BuffType.Snare, BuffType.Internal })
        {
            using var runtime = CreateRuntime();
            var minion = Spawn(runtime);
            var old = Enemy(runtime, new(7420, 7000));
            var ai = runtime.Minions!;
            ai.FindTargetOrMove(minion);
            ai.Tick(0);
            var candidate = Enemy(runtime, new(7100, 7000));
            var control = runtime.Buffs.AddBuff(minion,
                type == BuffType.Internal ? "CantAttack" : "ScriptControl", type, 2, candidate.NetId)!;
            TestAssert.Equal(old.NetId, minion.AttackTarget ?? 0);
            runtime.Buffs.RemoveBuff(minion, control.Slot);
            TestAssert.Equal(candidate.NetId, minion.AttackTarget ?? 0,
                $"{type}: release invokes IDLE -> FindTargetOrMove without advancing the AI clock");
            TestAssert.Equal(MinionAiState.Engage, minion.MinionState);
        }
    }

    [Check("minions.b15-generic-fear-wanders-and-recovers")]
    public static void GenericFearWandersAndRecovers()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        var target = Enemy(runtime, new(7400, 7000));
        var ai = runtime.Minions!;
        ai.FindTargetOrMove(minion);
        var leash = minion.Position;
        var fear = runtime.Buffs.AddBuff(minion, "GenericFear", BuffType.Fear, 3,
            target.NetId, canMitigateDuration: false)!;
        TestAssert.Equal(MinionAiState.Feared, minion.MinionState);
        TestAssert.False(minion.MinionAutoAttackEnabled);
        var first = minion.ForcedMove;
        TestAssert.True(first is not null, "unnamed generic fear starts a wander immediately");
        TestAssert.Equal(leash, fear.Payload.FearOrigin!.Value);
        TestAssert.True(minion.AnchorPath.Count > 1 &&
            Distance(leash, minion.AnchorPath[^1]) <= 502, "wander remains within the 500-unit leash plus wire rounding");
        ai.Tick(.99);
        TestAssert.True(ReferenceEquals(first, minion.ForcedMove), "fear timer does not fire early");
        ai.Tick(.01);
        TestAssert.False(ReferenceEquals(first, minion.ForcedMove), "fear repeats after one second");
        var root = runtime.Buffs.AddBuff(minion, "ScriptRoot", BuffType.Snare, 2, target.NetId)!;
        TestAssert.True(minion.ForcedMove is null, "root retires the fear walking segment");
        ai.Tick(1);
        TestAssert.True(minion.ForcedMove is null, "fear cannot walk through a movement lock");
        runtime.Buffs.RemoveBuff(minion, root.Slot);
        // minionLib 0.6 TimerFeared is the only source of a fear wander, and it runs on its own one
        // second clock. A movement lock that releases while the fear still holds raises no callback
        // at all — BuffLifecycle raises OnCanMove only once no fear is left on the unit, and
        // minionLib 0.7's OnCanMove is NetSetState(AI_IDLE) plus FindTargetOrMove, not a wander — so
        // the walk comes back on the fear timer's next tick, not on the root's release.
        ai.Tick(1);
        TestAssert.True(minion.ForcedMove is not null, "the fear timer resumes the wander once the lock is gone");
        runtime.Buffs.RemoveBuff(minion, fear.Slot);
        TestAssert.True(minion.ForcedMove is null);
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState);
        TestAssert.Equal(target.NetId, minion.AttackTarget ?? 0,
            "fear end immediately selects without a Shaco box service tick");
    }

    [Check("minions.b15-failed-target-path-ignores-and-reacquires")]
    public static void FailedTargetPathIgnoresAndReacquires()
    {
        using var runtime = CreateRuntime();
        var minion = Spawn(runtime);
        minion.Position = new(6999, 6757);
        TestAssert.True(runtime.Navigation.IsMovementWalkable(minion.Position), "failure starts on valid terrain");
        var unreachable = Enemy(runtime, new(6913, 6435));
        // A narrow approach ring cannot stop safely on this side of the wall.
        minion.AttackRange = 6;
        minion.CombatRadius = 0;
        unreachable.CombatRadius = 0;
        var alternative = Enemy(runtime, minion.Position + new Position2(10, 0));
        TestAssert.False(runtime.Navigation.HasMovementLineOfSight(minion.Position, unreachable.Position));
        minion.AttackTarget = unreachable.NetId;
        minion.MinionState = MinionAiState.Engage;
        var searches = 0;
        runtime.Navigation.FindPathOverride = (_, _) => { searches++; return []; };
        using var ai = new MinionAiSystem(runtime.World, runtime.Navigation, runtime.Movement,
            new ProductionMinionCombatService(runtime.Combat), new SilentPackets(), new VisibleVision(),
            canSee: (_, _) => true);
        ai.Tick(0);
        TestAssert.True(searches > 1, "normal planning and alternate approach searches were exhausted");
        TestAssert.Equal(alternative.NetId, minion.AttackTarget ?? 0,
            "genuine completed target-path failure immediately acquires the reachable alternative");
        alternative.Protected = true;
        TestAssert.True(ai.SelectTarget(minion) is null, "the failed target is ignored for 0.1 seconds");
        ai.HaltAI(minion);
        ai.Tick(.1);
        TestAssert.Equal(unreachable.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
            "ignore expires after 0.1 seconds independently of attack permission");
    }

    internal static MatchRuntime CreateRuntime() => MatchRuntime.CreateProduction(
        NavigationGrid.LoadDefault(), GameData.Default, orderChampion: "Annie", chaosChampion: "Annie",
        solo: true, originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0);

    internal static Entity Spawn(MatchRuntime runtime)
    {
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var minion = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0, 0);
        minion.Position = new(7000, 7000);
        minion.Waypoints.Clear();
        minion.Target = null;
        minion.LaneWaypoints.Clear();
        minion.AnchorPath = [minion.Position];
        return minion;
    }

    internal static Entity Enemy(MatchRuntime runtime, Position2 position) =>
        runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Chaos, position)
        {
            ChampionName = "Annie", Health = 10000, MaxHealth = 10000,
            Radius = 35, PathfindingRadius = 35, Created = true, Visible = true,
        });

    private sealed class VisibleVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class SilentPackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => false;
        public bool TryGetClientPosition(Entity minion, out Position2 position) { position = default; return false; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private static double Distance(Position2 a, Position2 b) => double.Hypot(a.X - b.X, a.Z - b.Z);
}
