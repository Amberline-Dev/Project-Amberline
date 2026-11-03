using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// The hand-written turret target-selection path batch 141 deleted from <c>CombatTick</c>, run
/// side by side with the DATA/Scripts/Turret.luaobj host over the same services and the same
/// scripted situations. Both sides see the same world, the same acquisition ladder
/// (<see cref="CombatService.AcquireTurretTarget"/>) and the same swing gate, so every difference
/// is a rule the script changed — each one is asserted here and classified below.
///
/// Differences (all script = retail, the shipped Turret.luaobj wins):
///  1. OnTargetLost takes a replacement only while the state is ATTACKING or TAUNTED; the
///     hand-written machine took one unconditionally. A HARDIDLE turret discards the result of
///     the same search and takes it at the next 0.15 s scan instead.
///  2. Out of attack range is not a target loss: the script drops to HARDIDLE and stops the swing
///     but keeps the target id (it never calls SetTarget); the machine cleared the id and
///     searched on the next tick.
///  3. OnCallForHelp assigns only from HARDIDLE or ATTACKING, so a TAUNTED turret ignores a hero
///     attacking a hero under it; the machine assigned the aggressor unconditionally. The engine's
///     own gate (CombatAwareness skips a turret carrying a Taunt buff) drops the call before either
///     machine sees it, so this difference is only visible when the host raises the callback.
///  4. OnTauntBegin/OnTauntEnd redirect to and from the taunter; the machine had no taunt path at
///     all, so it kept whatever it was shooting.
/// </summary>
public static class TurretAiDifferentialChecks
{
    private const double Step = 0.05;
    private const int Steps = 20;

    [Check("combat.turret-ai-differential")]
    public static void ScriptedTurretAgreesWithTheHandWrittenMachineWhereItShould()
    {
        MinionPriorityAndSwitchOnDeath();
        CallForHelpTakesTheAggressorAtOnce();
        OutOfRangeDropKeepsTheTargetId();
        TauntRedirectsAndGatesCallForHelp();
    }

    /// <summary>The acquisition ladder and the switch moments, through deaths and a ward.</summary>
    private static void MinionPriorityAndSwitchOnDeath()
    {
        var (scripted, oracle) = Drive(
            side =>
            {
                Turret(side, 1);
                side.Units["summoned"] = Spawn(side, 2, EntityKind.Minion, UnitType.Minion, Team.Chaos,
                    new(7100, 7000), owner: 99);
                side.Units["lane"] = Spawn(side, 3, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7150, 7000));
                side.Units["hero"] = Spawn(side, 4, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7250, 7000));
                side.Units["ward"] = Spawn(side, 5, EntityKind.Ward, UnitType.Monster, Team.Chaos,
                    new(7050, 7000), ward: true);
            },
            (side, _, index) =>
            {
                // The summoned unit dies first, then the lane minion: both sides must drop to the
                // next tier at once, and the ward is never taken at any point.
                if (index == 4) side.Units["summoned"].Alive = false;
                if (index == 8) side.Units["lane"].Alive = false;
            });

        TestAssert.Equal(Lines(scripted), Lines(oracle),
            "the summoned -> lane -> hero ladder and every switch moment agree with the hand-written machine");
        TestAssert.Equal((uint?)2, scripted[2].Target, "the first scan takes the summoned unit over the nearer ward");
        TestAssert.Equal((uint?)3, scripted[4].Target, "the death switch to the lane minion is immediate");
        TestAssert.Equal((uint?)4, scripted[8].Target, "the death switch to the champion is immediate");
    }

    /// <summary>A hero attacking a hero under the turret is taken at once, between scans.</summary>
    private static void CallForHelpTakesTheAggressorAtOnce()
    {
        var (scripted, oracle) = Drive(
            side =>
            {
                Turret(side, 1);
                side.Units["ally"] = Spawn(side, 2, EntityKind.Hero, UnitType.Hero, Team.Order, new(7150, 7000));
            },
            (side, _, index) =>
            {
                if (index == 3)
                {
                    side.Units["aggressor"] = Spawn(side, 3, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7250, 7000));
                    side.Combat.ApplyHit(3, 2, 1, DamageType.True, DamageSourceKind.Basic);
                }
                if (index == 8) side.Units["aggressor"].Alive = false;
            });

        TestAssert.Equal(Lines(scripted), Lines(oracle),
            "call-for-help assignment and the loss of the aggressor agree with the hand-written machine");
        TestAssert.Equal((uint?)3, scripted[3].Target, "the aggressor is taken between timer ticks, not at the next scan");
        TestAssert.True(scripted[8].Target is null, "a lost aggressor with nothing left in range clears the target");
    }

    /// <summary>Classified difference 2: the script keeps the target id across the range drop.</summary>
    private static void OutOfRangeDropKeepsTheTargetId()
    {
        var (scripted, oracle) = Drive(
            side =>
            {
                Turret(side, 1);
                side.Units["lane"] = Spawn(side, 2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7100, 7000));
            },
            (side, _, index) =>
            {
                if (index == 3) side.Units["lane"].Position = new(9000, 7000);
                if (index == 10) side.Units["lane"].Position = new(7100, 7000);
            });

        // The two machines only disagree about the retained id, and only while the target is out
        // of attack range: the script holds it through the drop and re-takes it at the same scan
        // the machine searches on, so the swing timeline is identical either way.
        TestAssert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, Diverging(scripted, oracle),
            "the only disagreement is the retained target id while the target is out of attack range");
        for (var index = 5; index <= 10; index++)
        {
            TestAssert.Equal((uint?)2, scripted[index].Target,
                $"t={scripted[index].Time:0.00}: Turret.luaobj keeps the target id across the drop (NOT the hand-written clear)");
            TestAssert.True(oracle[index].Target is null,
                $"t={oracle[index].Time:0.00}: the hand-written machine cleared the id and waits for its next search");
            TestAssert.Equal(scripted[index].Swings, oracle[index].Swings,
                $"t={scripted[index].Time:0.00}: neither side swings at a target outside attack range");
        }
        TestAssert.Equal((uint?)2, scripted[11].Target, "the re-acquisition lands on the same scan in both machines");
        TestAssert.Equal((uint?)2, oracle[11].Target, "the re-acquisition lands on the same scan in both machines");
    }

    /// <summary>Classified differences 3 and 4: the taunt redirect, and the call it gates.</summary>
    private static void TauntRedirectsAndGatesCallForHelp()
    {
        var (scripted, oracle) = Drive(
            side =>
            {
                Turret(side, 1);
                side.Units["ordinary"] = Spawn(side, 2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7100, 7000));
                side.Units["taunter"] = Spawn(side, 3, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7250, 7000));
                side.Units["ally"] = Spawn(side, 4, EntityKind.Hero, UnitType.Hero, Team.Order, new(7150, 7000));
            },
            (side, _, index) =>
            {
                // BuffLifecycle raises the same events when the buff lands and when it is removed;
                // the check raises them directly so the differential is about the host's decision.
                if (index == 3)
                {
                    side.Turret!.Buffs.Add(new BuffState("ScriptTaunt", type: BuffType.Taunt, slot: 0,
                        expiresAt: 10, casterNetId: 3));
                    side.Combat.AiHost.OnTauntBegin(side.Turret);
                }
                if (index == 6)
                {
                    // The game path: CombatAwareness skips a turret carrying a Taunt buff, so
                    // neither machine is called and both keep the target they had.
                    side.Units["aggressor"] = Spawn(side, 5, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7300, 7000));
                    side.Combat.ApplyHit(5, 4, 1, DamageType.True, DamageSourceKind.Basic);
                }
                if (index == 9)
                    // The host path the engine would take without that gate: this is where the
                    // two machines' own rules part company.
                    side.Combat.AiHost.OnCallForHelp(side.Turret!, side.Units["aggressor"]);
            });

        TestAssert.Equal(Enumerable.Range(3, Steps - 3).ToArray(), Diverging(scripted, oracle),
            "the taunt redirect is the first disagreement and nothing before it differs");
        for (var index = 3; index <= 5; index++)
        {
            TestAssert.Equal((uint?)3, scripted[index].Target,
                $"t={scripted[index].Time:0.00}: OnTauntBegin closes on the taunter (NOT the hand-written hold)");
            TestAssert.Equal((uint?)2, oracle[index].Target, $"t={oracle[index].Time:0.00}: the machine keeps its old target");
        }
        for (var index = 6; index <= 8; index++)
        {
            TestAssert.Equal((uint?)3, scripted[index].Target,
                $"t={scripted[index].Time:0.00}: the engine's taunt gate drops the call, so the taunt target is kept");
            TestAssert.Equal((uint?)2, oracle[index].Target,
                $"t={oracle[index].Time:0.00}: the engine's taunt gate drops the call for the machine too");
        }
        for (var index = 9; index < Steps; index++)
        {
            TestAssert.Equal((uint?)3, scripted[index].Target,
                $"t={scripted[index].Time:0.00}: a TAUNTED turret ignores the call for help");
            TestAssert.Equal((uint?)5, oracle[index].Target,
                $"t={oracle[index].Time:0.00}: the machine takes the aggressor unconditionally");
        }
    }

    // ---- the harness -------------------------------------------------------------------------------

    private static (List<Sample> Scripted, List<Sample> Oracle) Drive(
        Action<Side> setup, Action<Side, double, int> at, int steps = Steps)
    {
        var scripted = Build(scripted: true, setup);
        var oracle = Build(scripted: false, setup);
        for (var index = 0; index < steps; index++)
        {
            var time = (index + 1) * Step;
            at(scripted, time, index);
            at(oracle, time, index);
            scripted.Combat.CombatTick(Step, regenerate: false);
            oracle.Combat.CombatTick(Step, regenerate: false);
            scripted.Trace.Add(new Sample(time, scripted.Turret!.AttackTarget, Swings(scripted)));
            oracle.Trace.Add(new Sample(time, oracle.Turret!.AttackTarget, Swings(oracle)));
        }
        return (scripted.Trace, oracle.Trace);
    }

    private static Side Build(bool scripted, Action<Side> setup)
    {
        var side = new Side(scripted);
        setup(side);
        return side;
    }

    private static List<string> Lines(List<Sample> trace) =>
        trace.Select(sample => sample.ToString()).ToList();

    private static int[] Diverging(List<Sample> scripted, List<Sample> oracle)
    {
        var diverging = new List<int>();
        for (var index = 0; index < scripted.Count; index++)
            if (scripted[index].ToString() != oracle[index].ToString()) diverging.Add(index);
        return diverging.ToArray();
    }

    /// <summary>The turret's in-flight swings, as the targets they are flying at.</summary>
    private static string Swings(Side side) => string.Join(",", side.Combat.PendingHits
        .Where(hit => hit.SourceNetId == side.Turret!.NetId)
        .Select(hit => hit.TargetNetId)
        .OrderBy(netId => netId));

    private static void Turret(Side side, uint netId) =>
        side.Turret = side.World.Register(new Entity(netId, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(7000, 7000))
        {
            Health = 1e6, MaxHealth = 1e6, AttackRange = 775, AttackDamage = 1, AttackPeriod = 0.5,
            AttackWindup = 0.05, MissileSpeed = 1000, Radius = 0,
        });

    private static Entity Spawn(Side side, uint netId, EntityKind kind, UnitType type, Team team,
        Position2 position, uint owner = 0, bool ward = false) =>
        side.World.Register(new Entity(netId, kind, type, team, position)
        {
            Health = 1e6, MaxHealth = 1e6, Radius = 0, OwnerNetId = owner, IsWard = ward,
            Name = ward ? "SightWard" : string.Empty,
        });

    private readonly record struct Sample(double Time, uint? Target, string Swings)
    {
        public override string ToString() =>
            $"t={Time:0.00} target={(Target?.ToString() ?? "-")} swings={(Swings.Length == 0 ? "-" : Swings)}";
    }

    private sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    /// <summary>One side of the differential: its own world, combat pass and AI host.</summary>
    private sealed class Side
    {
        public Side(bool scripted)
        {
            World = new WorldState();
            Combat = new CombatService(World, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
                new FixedRandomSource(0.5));
            if (!scripted)
            {
                Oracle = new HandwrittenTurretOracle(Combat, World);
                Combat.AiHost = Oracle;
            }
        }

        public WorldState World { get; }
        public CombatService Combat { get; }
        public HandwrittenTurretOracle? Oracle { get; }
        public Entity? Turret { get; set; }
        public Dictionary<string, Entity> Units { get; } = new();
        public List<Sample> Trace { get; } = new();
    }

    /// <summary>
    /// The hand-written turret machine batch 141 deleted, kept as the other side of this
    /// differential: <c>StepUnit</c> is the deleted <c>CombatTick</c> block and the scan deadline
    /// is the deleted <c>TurretScanReady</c>, both transcribed. What it has no code for (taunts,
    /// control release) it does not do — that gap is the classified difference, not a bug here.
    /// </summary>
    private sealed class HandwrittenTurretOracle : IAiHost
    {
        private readonly CombatService _combat;
        private readonly WorldState _world;
        private readonly Dictionary<uint, double> _nextTurretScan = new();

        public HandwrittenTurretOracle(CombatService combat, WorldState world)
        {
            _combat = combat;
            _world = world;
        }

        public bool Drives(Entity unit) => unit.UnitType == UnitType.Turret;

        // The machine had no events: its whole rule ran inside the per-tick pass below.
        public void OnTargetLost(Entity unit) { }

        // Turret.luaobj exports no OnReachedDestinationForGoingToLastLocation and never walks to a
        // lost target's last known position; the differential raises nothing for it.
        public void OnReachedDestinationForGoingToLastLocation(Entity unit) { }

        // The assignment 947ede18 deleted from CombatAwareness: unconditional, once the engine's
        // reach and stick windows had already accepted the call.
        public void OnCallForHelp(Entity unit, Entity aggressor) => _combat.SetTurretTarget(unit, aggressor);

        public void OnTauntBegin(Entity unit) { }
        public void OnTauntEnd(Entity unit) { }
        // Damage, fear and control release are jungle events; the hand-written turret machine had
        // no code for them, and this differential raises none of them.
        public void OnTakeDamage(Entity unit, Entity attacker) { }
        public void OnFearBegin(Entity unit) { }
        public void OnFearEnd(Entity unit) { }
        public void OnCanMove(Entity unit) { }
        public void OnCanAttack(Entity unit) { }

        // The hand-written machine had no HaltAI; nothing in this differential raises it.
        public void HaltAI(Entity unit) { }

        public void StepUnit(Entity unit, double now, double dt)
        {
            var timerTick = TurretScanReady(unit, now, dt);
            var current = unit.AttackTarget is uint currentId ? _world.Get(currentId) : null;
            if (unit.AttackTarget is not null && (current is null || !_combat.CanAttackIgnoringRange(unit, current)))
            {
                _combat.SetTurretTarget(unit, _combat.AcquireTurretTarget(unit));
                current = unit.AttackTarget is uint lostId ? _world.Get(lostId) : null;
            }
            if (timerTick)
            {
                if (unit.AttackTarget is null)
                {
                    var found = _combat.AcquireTurretTarget(unit);
                    if (found is null) _combat.SetTurretAutoAttack(unit, null, enabled: false);
                    else _combat.SetTurretTarget(unit, found);
                }
                else if (current is not null && CombatService.InRange(unit, current))
                    _combat.SetTurretAutoAttack(unit, current, enabled: true);
                else
                {
                    _combat.SetTurretTarget(unit, null);
                    _combat.SetTurretAutoAttack(unit, null, enabled: false);
                }
            }
        }

        private bool TurretScanReady(Entity turret, double now, double dt)
        {
            var deadline = _nextTurretScan.GetValueOrDefault(turret.NetId, now - dt + 0.15);
            var ready = now + 1e-9 >= deadline;
            if (ready) deadline += (Math.Floor(Math.Max(0.0, now - deadline) / 0.15) + 1.0) * 0.15;
            _nextTurretScan[turret.NetId] = deadline;
            return ready;
        }
    }
}
