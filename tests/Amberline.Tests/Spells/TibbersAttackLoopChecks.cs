using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

/// <summary>
/// Batch 716 (player report via the owner, 8 November 2026: "Tibbers got stuck in attack animation even though it was not
/// attacking anyone or dealing damage"). The stock client runs Tibbers as an obj_AI_Minion: the 0x0D enters its attack state
///, and while that state lasts UpdatePimpl starts the last attack again every
/// attack period on the last target object, whatever it is doing, and each start erases the path the unit follows. Only a 0x39
/// or the unit's own death raises event 4 and ends it. These checks run Tibbers through every way
/// his attack ends without a new order, in a production <c>MatchRuntime</c>, and read what each seat was sent.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const double TibbersLoopStep = 1.0 / 30.0;

    /// <summary>What one seat's client holds of Tibbers' attack state, from the bytes it was sent.</summary>
    private sealed class TibbersClientModel
    {
        public bool Armed;
        public bool Dead;
        public int Seen;
        public int Swings;
        public int Stops;
        public int RoutesWhileArmed;
        public double LastSwing = double.NegativeInfinity;
        /// <summary>
        /// The longest the client stayed in its attack state after the server's last swing: from one attack period on, every
        /// period of it is a swing the client starts again on its own and the server never made.
        /// </summary>
        public double MaxArmedGap;

        public void Disarm(double now)
        {
            if (Armed) MaxArmedGap = Math.Max(MaxArmedGap, now - LastSwing);
            Armed = false;
        }

        public double Gap(double now) => Armed ? Math.Max(MaxArmedGap, now - LastSwing) : MaxArmedGap;
    }

    private sealed class TibbersLoopScene : IDisposable
    {
        private readonly TibbersClientModel[] _models;

        public TibbersLoopScene(string enemyChampion)
        {
            Match = new Match("Annie", Team.Order, extraEnemy: true, enemyChampion: enemyChampion);
            Owner = Match.Hero(0);
            Enemy = Match.Hero(1);
            Second = Match.Hero(2);
            _models = Match.Captures.Select(_ => new TibbersClientModel()).ToArray();
            Owner.Position = new Position2(7000, 7000);
            Enemy.Position = new Position2(7250, 7000);
            Second.Position = new Position2(11000, 11000);
            // Annie's R, cast the way a player casts it, on the enemy she stands next to.
            Match.CastGround(0, Enemy.Position, 3);
            Run(0.3);
            Pet = Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == Owner.NetId);
        }

        public Match Match { get; }
        public Amberline.Network.Sessions.MatchRuntime Runtime => Match.Runtime;
        public Entity Owner { get; }
        public Entity Enemy { get; }
        public Entity Second { get; }
        public Entity Pet { get; private set; } = null!;
        public double Now => Runtime.Combat.CurrentTime;
        public TibbersClientModel Seat(int seat) => _models[seat];
        public int Seats => _models.Length;

        public void Run(double seconds)
        {
            for (var step = 0; step < (int)Math.Round(seconds / TibbersLoopStep); step++)
            {
                Runtime.Tick(TibbersLoopStep);
                Absorb();
            }
        }

        /// <summary>Runs until <paramref name="done"/> holds, at most <paramref name="seconds"/>; answers whether it held.</summary>
        public bool RunUntil(Func<bool> done, double seconds)
        {
            for (var step = 0; step < (int)Math.Round(seconds / TibbersLoopStep); step++)
            {
                if (done()) return true;
                Runtime.Tick(TibbersLoopStep);
                Absorb();
            }
            return done();
        }

        /// <summary>Folds the packets each seat was sent since the last call into that seat's model of the client.</summary>
        public void Absorb()
        {
            if (Pet is null) return;
            for (var seat = 0; seat < _models.Length; seat++)
            {
                var model = _models[seat];
                var packets = Match.Captures[seat].Packets;
                for (; model.Seen < packets.Count; model.Seen++)
                {
                    var bytes = packets[model.Seen].Plaintext.Span;
                    if (bytes.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != Pet.NetId) continue;
                    switch (bytes[0])
                    {
                        case 0x0D:
                            if (model.Armed) model.MaxArmedGap = Math.Max(model.MaxArmedGap, Now - model.LastSwing);
                            model.Armed = true;
                            model.Swings++;
                            model.LastSwing = Now;
                            break;
                        case 0x39:
                            model.Disarm(Now);
                            model.Stops++;
                            break;
                        case 0xA6:
                            model.Disarm(Now);
                            model.Dead = true;
                            break;
                        case VisionPackets.WaypointListOpcode when (bytes.Length - 9) / 8 >= 2 && model.Armed:
                            // A walk the client erases at its attack state's next start.
                            model.RoutesWhileArmed++;
                            break;
                    }
                }
            }
        }

        public void Dispose() => Match.Dispose();
    }

    private sealed record TibbersLoopCase(string Name, Action<TibbersLoopScene> End, double Seconds = 4.0,
        string EnemyChampion = "Annie");

    /// <summary>Every way Tibbers' attack on the enemy ends without a new attack of his (the brief's list).</summary>
    private static readonly TibbersLoopCase[] TibbersLoopCases =
    [
        new("target walks out of reach", scene => scene.Match.Move(1, scene.Enemy.Position + new Position2(1000, 1000))),
        new("target stops just out of reach", scene =>
        {
            // Inside the 100-unit cancel band (ar_StopAttackRangeModifier): Pet.lua neither turns the attack on nor off.
            var away = scene.Enemy.Position - scene.Pet.Position;
            var length = Math.Sqrt(away.X * away.X + away.Z * away.Z);
            var reach = Amberline.Simulation.Combat.CombatService.AttackReach(scene.Pet, scene.Enemy) + 50.0;
            scene.Match.Move(1, scene.Pet.Position + new Position2(away.X / length * reach, away.Z / length * reach));
        }),
        new("Tibbers rooted, target walks away", scene =>
        {
            scene.Runtime.Buffs.AddBuff(scene.Pet, "Snare", BuffType.Snare, 6.0, scene.Enemy.NetId);
            scene.Match.Move(1, scene.Enemy.Position + new Position2(1000, 1000));
        }, 5.0),
        new("target killed by Annie", scene => scene.Runtime.Combat.Kill(scene.Enemy, scene.Owner.NetId)),
        new("target killed by Tibbers", scene => scene.Enemy.Health = 1.0),
        new("target becomes untargetable", scene => scene.Enemy.Protected = true),
        new("target teleports out of sight", scene => scene.Enemy.Position = new Position2(12500, 12500)),
        new("owner walks past the leash", scene => scene.Match.Move(0, new Position2(4700, 4700)), 9.0),
        new("owner recalls", scene => scene.Owner.Position = new Position2(1000, 1000)),
        new("owner dies", scene => scene.Runtime.Combat.Kill(scene.Owner, scene.Enemy.NetId)),
        new("owner hard-moves Tibbers", scene => SendPetInput(scene.Match, scene.Pet, 6, scene.Pet.Position + new Position2(-500, 0))),
        new("owner hard-returns Tibbers", scene => SendPetInput(scene.Match, scene.Pet, 9, scene.Owner.Position)),
        new("owner hard-stops Tibbers", scene => SendPetInput(scene.Match, scene.Pet, 11, scene.Pet.Position)),
        new("owner hard-attacks another target", scene =>
        {
            scene.Second.Position = scene.Pet.Position + new Position2(0, 400);
            SendPetInput(scene.Match, scene.Pet, 5, scene.Second.Position, scene.Second.NetId);
        }),
        new("owner moves (mirrored)", scene => SendPetInput(scene.Match, scene.Pet, 2, scene.Pet.Position + new Position2(-1500, 0))),
        new("Tibbers stunned", scene => scene.Runtime.Buffs.AddBuff(scene.Pet, "Stun", BuffType.Stun, 1.0, scene.Enemy.NetId)),
        new("Tibbers knocked back", scene => scene.Match.Cast(1, 3, scene.Enemy), 4.0, "Janna"),
        new("second target after the first", scene =>
        {
            scene.Second.Position = scene.Pet.Position + new Position2(0, 250);
            scene.Runtime.Combat.Kill(scene.Enemy, scene.Owner.NetId);
        }),
        new("Tibbers expires", scene => scene.Pet.ExpiresAt = scene.Now + 0.2),
    ];

    /// <summary>
    /// Reproduce-first sweep: after each way Tibbers' attack ends, no seat may be sent a walk of his while its client is still in
    /// his attack state (it would erase the walk at its next start and swing in place), nor stay in that state two attack periods
    /// past the server's last swing (two swings the server never made).
    /// </summary>
    [Check("pets-tibbers-attack-loop-ends-on-every-client")]
    public static void TibbersAttackLoopEndsOnEveryClient() => RunTibbersLoopCases(TibbersLoopCases);

    /// <summary>
    /// Pet.lua's TimerFindEnemies turns the attack off with STOPREASON_MOVING once the target leaves the cancel range. A rooted Tibbers
    /// cannot follow, so no walk follows either: that call is the attack's whole end, and the clients must be told at once.
    /// </summary>
    [Check("pets-tibbers-turn-off-auto-attack-ends-the-client-attack")]
    public static void TibbersTurnOffAutoAttackEndsTheClientAttack() =>
        RunTibbersLoopCases(TibbersLoopCases.Where(@case => @case.Name == "Tibbers rooted, target walks away"));

    /// <summary>
    /// A target that stops inside the cancel band (beyond reach, within reach + ar_StopAttackRangeModifier) is one Pet.lua neither
    /// turns the attack on for nor off: only the native's walk to it ends the attack, so the walk itself must end the clients' attack
    /// state first.
    /// </summary>
    [Check("pets-tibbers-walk-ends-the-client-attack-first")]
    public static void TibbersWalkEndsTheClientAttackFirst() =>
        RunTibbersLoopCases(TibbersLoopCases.Where(@case => @case.Name == "target stops just out of reach"));

    private static void RunTibbersLoopCases(IEnumerable<TibbersLoopCase> cases)
    {
        var table = new StringBuilder();
        var failures = new List<string>();
        foreach (var @case in cases)
        {
            using var scene = new TibbersLoopScene(@case.EnemyChampion);
            var pet = scene.Pet;
            TestAssert.True(scene.RunUntil(() => scene.Seat(0).Swings > 0 && scene.Seat(1).Swings > 0, 4.0),
                $"{@case.Name}: Tibbers attacks the enemy and both teams see the swing");
            scene.Run(0.1);
            var before = Enumerable.Range(0, scene.Seats).Select(seat => scene.Seat(seat).Swings).ToArray();
            @case.End(scene);
            scene.Absorb();
            scene.Run(@case.Seconds);
            var period = Math.Max(pet.AttackPeriod, 0.5);
            for (var seat = 0; seat < scene.Seats; seat++)
            {
                var model = scene.Seat(seat);
                // Stuck: the client repeated the attack on its own at least twice after the server's last swing.
                var gap = model.Gap(scene.Now);
                var stuck = gap > 2.0 * period;
                table.AppendLine(CultureInfo.InvariantCulture,
                    $"{@case.Name,-34} seat {seat}: swings {before[seat]}->{model.Swings} stops {model.Stops} " +
                    $"armed {model.Armed} dead {model.Dead} routes-while-armed {model.RoutesWhileArmed} " +
                    $"armed-gap {gap:0.00}s/{period:0.00}s stuck {stuck} | " +
                    $"server alive {pet.Alive} armed {pet.ClientAttackArmed} target {pet.AttackTarget?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                    $"state {scene.Runtime.Pets.StateOf(pet)?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                if (model.Swings == 0) continue;
                if (model.RoutesWhileArmed > 0)
                    failures.Add($"{@case.Name} seat {seat}: {model.RoutesWhileArmed} route(s) reached a client still in Tibbers' attack state");
                if (stuck)
                    failures.Add($"{@case.Name} seat {seat}: the client stayed in the attack state " +
                                 $"{gap.ToString("0.00", CultureInfo.InvariantCulture)} s after the server's last swing " +
                                 $"(period {period.ToString("0.00", CultureInfo.InvariantCulture)} s)");
            }
        }
        Console.WriteLine(table.ToString());
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
