using System.Buffers.Binary;
using System.Globalization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Batch 711 (owner, 7 November 2026: "Minions should not just die and disappear when approaching a shrine, they should keep attacking the shrine
/// and gradually losing HP"). The shipped 126 minion side of a capture point: the guardian's OdinGuardianBuff taunts every enemy minion within
/// 900 with OdinMinionTaunt ("Capture Fervor ... when assaulting a Capture Point"); MinionOdin's scan answers the taunt with UseTarget; the use
/// is the guardian's MinionUseSpell, OdinMinionSpellAttack (CastRange 400, Cooldown 0.8, a 1600 missile, AnimationName Attack2). Its
/// TargetExecute moves the guardian's pool toward the minion's team (1 % on a neutral point, 2 % on an enemy one; 1.5 % and 3 % for a
/// superminion), returns 25 % of the minion's maximum health as true damage on a neutral point only, and puts OdinMinionSpellAttack on the
/// guardian for 1 s, during which OdinGuardianBuff's regen does not run. Order holds point B; its first B1 wave walks lane 4 to point C.
/// </summary>
public static partial class DominionGuardianChecks
{
    // OdinWaves.Lanes: point B (index 1) sends its B1 wave down __NAV_4 to point C (PointIndex).
    private const int SendingPoint = 1;
    private const string MinionUseSpell = "OdinMinionSpellAttack";
    // The use slot a minion's use is cast from (BbSummonerModule.UseChannelSlot), whose ready time each cast sets.
    private const int UseSlot = 0x33;

    /// <summary>One simulation step of the followed minion and point C.</summary>
    private sealed record AssaultSample(double Time, double Health, double Progress, double Pool, bool RegenPaused, double UseReadyAt);

    private sealed record Assault(Match Match, Entity Minion, List<AssaultSample> Samples, double ArrivedAt, double? DiedAt,
        SeatCapture[] Seats) : IDisposable
    {
        public void Dispose() => Match.Dispose();

        public double ProgressBefore => Samples[0].Progress;

        /// <summary>The killer each seat's death packet of the minion names (0xA6: the dying unit at 1, the killer at 5), 0 when none came.</summary>
        public IEnumerable<uint> KillersOnWire => Seats.Select(seat => seat.Packets.Select(packet => packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length >= 9 && bytes[0] == 0xA6 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == Minion.NetId)
            .Select(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5))).FirstOrDefault());
        public double ProgressPeak => Samples.Max(sample => sample.Progress);
        public string ProgressTrace => string.Join(" ", Samples.Select(sample => sample.Progress.ToString("F1", CultureInfo.InvariantCulture)).Distinct());

        /// <summary>The followed minion's casts: each one sets a new ready time on its use slot.</summary>
        public List<double> CastTimes => Samples.Zip(Samples.Skip(1)).Where(pair => pair.Second.UseReadyAt > pair.First.UseReadyAt + 1e-9)
            .Select(pair => pair.Second.Time).ToList();

        /// <summary>The largest one-step fall of the point's progress toward the defender while the guardian's regen is paused.</summary>
        public double PausedFall(double towardAttacker) => Samples.Zip(Samples.Skip(1)).Where(pair => pair.Second.RegenPaused)
            .Select(pair => (pair.First.Progress - pair.Second.Progress) * towardAttacker).DefaultIfEmpty(0.0).Max();
    }

    /// <summary>
    /// A wave caster that reaches a neutral point is not consumed by the point: it keeps hitting the guardian, loses a quarter of its maximum
    /// health on each hit (OdinMinionSpellAttack's DamageReturn, true damage), at most once per 0.8 s (the spell's cooldown), and dies on the
    /// fourth hit. Each hit moves the point's capture toward Order (the tree's CaptureProgress and the guardian's pool, the bar the clients draw),
    /// and while the guardian holds the 1 s OdinMinionSpellAttack the point's regen toward neutral does not take it back.
    /// </summary>
    [Check("dominion.wave-minion-assaults-a-neutral-point-losing-a-quarter-health-per-hit", TimeoutSeconds = 600)]
    public static void AWaveMinionAssaultsANeutralPointLosingAQuarterHealthPerHit()
    {
        using var assault = RunAssault(enemyPoint: false);
        var minion = assault.Minion;
        var drops = Drops(assault.Samples);
        var detail = $"health {string.Join(" ", assault.Samples.Select(sample => sample.Health.ToString("F0", CultureInfo.InvariantCulture)).Distinct())}";
        TestAssert.True(assault.DiedAt is { } died && died - assault.ArrivedAt >= 2.0,
            $"the caster dies at the point, not on arrival (arrived {assault.ArrivedAt:F2} s, died {assault.DiedAt?.ToString("F2", CultureInfo.InvariantCulture) ?? "never"}; {detail})");
        TestAssert.True(assault.KillersOnWire.All(killer => killer == assault.Match.Guardian.NetId),
            $"each seat sees the caster die to the guardian's returned damage (killers {string.Join(",", assault.KillersOnWire)})");
        TestAssert.True(drops.Count >= 3, $"the caster loses health in at least three hits before its last ({drops.Count}; {detail})");
        foreach (var (_, amount) in drops)
            TestAssert.True(Math.Abs(amount - 0.25 * minion.MaxHealth) <= 1.0,
                $"each hit returns 25 % of the caster's {minion.MaxHealth:F0} maximum health ({amount:F1})");
        for (var index = 1; index < drops.Count; index++)
            TestAssert.True(drops[index].Time - drops[index - 1].Time >= 0.8 - 1e-6,
                $"hits come at most once per the spell's 0.8 s cooldown ({drops[index - 1].Time:F2} s, {drops[index].Time:F2} s)");
        TestAssert.True(assault.ProgressPeak >= assault.ProgressBefore + 4.0 - 1e-6,
            $"the hits move point C's capture toward Order ({assault.ProgressTrace})");
        TestAssert.True(assault.PausedFall(+1.0) <= 1e-9,
            $"no regen toward neutral while the guardian holds OdinMinionSpellAttack (largest fall {assault.PausedFall(+1.0):F3}; {assault.ProgressTrace})");
        TestAssert.True(assault.Samples.Max(sample => sample.Pool) > assault.Samples[0].Pool,
            $"the guardian's pool, the clients' capture bar, follows ({assault.Samples[0].Pool:F0} to a peak of {assault.Samples.Max(sample => sample.Pool):F0})");
    }

    /// <summary>
    /// On an enemy point (Chaos holds C) the hit costs the minion nothing: it keeps casting at the guardian until the guardian's shots kill it,
    /// and each hit takes 2 % of the pool (3 % for the superminion), 2 or 3 points of the tree's progress, from Chaos with no regen back in between.
    /// </summary>
    [Check("dominion.wave-minion-assaults-an-enemy-point-until-its-guardian-kills-it", TimeoutSeconds = 600)]
    public static void AWaveMinionAssaultsAnEnemyPointUntilItsGuardianKillsIt()
    {
        using var assault = RunAssault(enemyPoint: true);
        var minion = assault.Minion;
        var casts = assault.CastTimes;
        var detail = $"casts at {string.Join(" ", casts.Select(time => time.ToString("F2", CultureInfo.InvariantCulture)))}; health " +
                     string.Join(" ", assault.Samples.Select(sample => sample.Health.ToString("F0", CultureInfo.InvariantCulture)).Distinct());
        var guardian = assault.Match.Guardian.NetId;
        TestAssert.True(assault.DiedAt is not null && assault.KillersOnWire.All(killer => killer == guardian),
            $"the guardian kills the caster, and each seat sees it die (killers {string.Join(",", assault.KillersOnWire)}, guardian {guardian}; {detail})");
        TestAssert.True(casts.Count >= 2 && assault.DiedAt!.Value - casts[^1] <= 1.0 + 0.8,
            $"the caster keeps casting at the point until it dies (died {assault.DiedAt:F2}; {detail})");
        foreach (var (time, amount) in Drops(assault.Samples))
            TestAssert.True(!casts.Any(cast => time >= cast && time <= cast + 0.5) || Math.Abs(amount - 0.25 * minion.MaxHealth) > 1.0,
                $"no hit returns damage to the caster on an enemy point ({amount:F1} at {time:F2}; {detail})");
        var rises = assault.Samples.Zip(assault.Samples.Skip(1)).Select(pair => pair.Second.Progress - pair.First.Progress)
            .Where(rise => rise > 1e-9).ToList();
        TestAssert.True(rises.Count >= 2 && rises.All(rise => rise >= 2.0 - 1e-6 && Math.Abs(rise - Math.Round(rise)) <= 1e-6),
            $"each hit takes 2 or 3 points of progress from Chaos ({string.Join(" ", rises.Select(rise => rise.ToString("F3", CultureInfo.InvariantCulture)))})");
        TestAssert.True(assault.PausedFall(+1.0) <= 1e-9,
            $"no regen back to Chaos while the guardian holds OdinMinionSpellAttack (largest fall {assault.PausedFall(+1.0):F3}; {assault.ProgressTrace})");
    }

    /// <summary>
    /// Each hit is the minion's cast of OdinMinionSpellAttack, whose AnimationName is Attack2: every seat that sees the minion gets its cast answer
    /// (0xBD) naming the spell, after the use slot (0x33) was bound to it on that seat (0x73), the binding a hero's use takes (batch 494).
    /// </summary>
    [Check("dominion.wave-minion-assault-is-announced-as-its-attack2-cast", TimeoutSeconds = 600)]
    public static void AWaveMinionAssaultIsAnnouncedAsItsAttackTwoCast()
    {
        using var assault = RunAssault(enemyPoint: false);
        TestAssert.Equal("Attack2", GameData.Default.Spell(MinionUseSpell).RawText("SpellData*AnimationName"), "the use spell's animation");
        var hash = SpellSystem.ProtocolHash(MinionUseSpell);
        var netId = assault.Minion.NetId;
        foreach (var seat in assault.Seats)
        {
            var bytes = seat.Packets.Select(packet => packet.Plaintext.ToArray()).ToList();
            var casts = bytes.FindAll(packet => packet.Length > 9 && packet[0] == SpellPackets.CastSpellAnswerOpcode &&
                                                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == netId);
            var bind = bytes.FindIndex(packet => packet.Length >= 14 && packet[0] == 0x73 &&
                                                 BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == netId &&
                                                 BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(9)) == hash && packet[13] == UseSlot);
            TestAssert.True(casts.Count >= 3, $"the seat sees the caster's hits as casts ({casts.Count})");
            TestAssert.True(casts.All(packet => packet.AsSpan().IndexOf(BitConverter.GetBytes(hash)) >= 0), "each cast names OdinMinionSpellAttack");
            TestAssert.True(bind >= 0 && bind < bytes.IndexOf(casts[0]), "the use slot is bound to the spell before the first cast");
        }
    }

    /// <summary>
    /// Order captures point B (and, for an enemy point, Chaos then captures C); the first Order caster of B's B1 wave that comes within 450 of
    /// point C's guardian is followed until it dies.
    /// </summary>
    private static Assault RunAssault(bool enemyPoint)
    {
        var match = StartMatch();
        try
        {
            var runtime = match.Runtime;
            var seats = new[] { Observe(match, Team.Order), Observe(match, Team.Chaos) };
            Entity? followed = null;
            var sender = match.Quests.Context.PointGuardian(SendingPoint + 2) ?? throw new InvalidOperationException("point B has a guardian");
            match.Order.Position = Offset(sender, 300.0);
            TestAssert.True(runtime.Spells.BeginCaptureChannel(match.Order, sender), "Order starts the capture of point B");
            TestAssert.True(Until(runtime, () => match.Quests.CapturePointOwners.GetValueOrDefault(SendingPoint + 1) == Team.Order, 20.0),
                "point B becomes Order's");
            Until(runtime, () => runtime.Spells.CaptureChannelTarget(match.Order) is null, 1.0);
            match.Order.Position = match.OrderHome;
            if (enemyPoint)
            {
                Capture(match, match.Chaos);
                match.Chaos.Position = match.ChaosHome;
            }
            var guardian = match.Guardian;
            var capture = match.Quests.Captures[PointIndex];
            var elapsed = 0.0;
            var (seen, closest) = (new HashSet<uint>(), double.MaxValue);
            TestAssert.True(Until(runtime, () =>
            {
                elapsed += Step;
                SuppressChaosWaves(runtime);
                foreach (var unit in runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team == Team.Order && unit.Name == "Odin_Blue_Minion_Caster"))
                {
                    seen.Add(unit.NetId);
                    closest = Math.Min(closest, Distance(unit.Position, guardian.Position));
                    if (Distance(unit.Position, guardian.Position) <= 450.0) followed = unit;
                }
                return followed is not null;
            }, 120.0), $"an Order wave caster reaches point C ({seen.Count} casters seen, closest {closest:F0}, " +
                       $"{seen.Count(id => runtime.World.Get(id) is not { Alive: true })} dead)");
            var minion = followed!;
            var arrivedAt = elapsed;
            var samples = new List<AssaultSample> { Sample(elapsed, minion, capture, guardian) };
            double? diedAt = null;
            for (var tick = 0; tick < (int)(20.0 / Step) && diedAt is null; tick++)
            {
                SuppressChaosWaves(runtime);
                runtime.Tick(Step);
                elapsed += Step;
                samples.Add(Sample(elapsed, minion, capture, guardian));
                if (!minion.Alive) diedAt = elapsed;
            }
            return new Assault(match, minion, samples, arrivedAt, diedAt, seats);
        }
        catch
        {
            match.Dispose();
            throw;
        }
    }

    private static AssaultSample Sample(double time, Entity minion, Amberline.Simulation.BehaviorTrees.BehaviorTreeInstance capture, Entity guardian) =>
        new(time, minion.Alive ? minion.Health : 0.0, Convert.ToDouble(capture.Variable("CaptureProgress") ?? 0.0, CultureInfo.InvariantCulture),
            guardian.Mana, guardian.Buffs.Any(buff => string.Equals(buff.Name, MinionUseSpell, StringComparison.OrdinalIgnoreCase)),
            minion.ScriptSlotReadyAt.GetValueOrDefault(UseSlot));

    /// <summary>
    /// DIAGNOSTICS: test seam. Chaos's own waves (sent once it holds C) are removed as they spawn, so only the point's guardian fights the
    /// followed Order wave.
    /// </summary>
    private static void SuppressChaosWaves(MatchRuntime runtime)
    {
        foreach (var unit in runtime.World.Entities.Values.Where(unit => unit.Alive && unit.Team == Team.Chaos && OdinWaves.ScriptOf(unit) is not null).ToList())
            runtime.Combat.Kill(unit, 0u);
    }

    /// <summary>The health losses of the samples, each with its time, the last (the death) excluded.</summary>
    private static List<(double Time, double Amount)> Drops(List<AssaultSample> samples)
    {
        var drops = new List<(double, double)>();
        for (var index = 1; index < samples.Count; index++)
            if (samples[index].Health < samples[index - 1].Health - 1e-6 && samples[index].Health > 0.0)
                drops.Add((samples[index].Time, samples[index - 1].Health - samples[index].Health));
        return drops;
    }
}
