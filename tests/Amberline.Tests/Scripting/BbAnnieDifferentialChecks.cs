using System.Globalization;
using System.Reflection;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// The Annie checks of the script engine. Until 19 October 2026 this was the M1 differential (go/no-go of the BuildingBlocks engine):
/// the shipped Annie scripts, run by <see cref="BbChampionModule"/>, against the hand-written <c>AnnieModule</c> on the same production
/// graph. Annie now runs from her scripts in production, see docs/maintenance/2026-10-19-w21-annie-script.md, and the hand-written module is
/// gone, and every check runs the production Annie once and asserts the retail truth: the tables of the shipped scripts and inibins and
/// the order their blocks run in. The harness is unchanged: <c>MatchRuntime.CreateProduction</c> (session, driver, combat, buffs,
/// stats) with proxies at the service boundary that record the damage, buff, effect, cooldown and assist calls of the module, plus a
/// per-step snapshot of the two heroes. The names keep the word differential because other records cite them.
/// </summary>
public static partial class BbAnnieDifferentialChecks
{
    private const double Step = 1.0 / 30.0;

    // ------------------------------------------------------------------------------------------------------------
    // Findings: the differences between the script and the hand-written module that the differential found.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The only places the two variants may differ, each decided from the shipped script and the inibins (see
    /// docs/scripting/building-blocks-engine.md, "M1 differential findings"). The check removes exactly these before it compares.
    /// </summary>
    private static class Findings
    {
        /// <summary>
        /// CharScriptAnnie.CharOnActivate adds the permanent Pyromania_Marker aura (BuffTextureName Annie_Brilliance.dds,
        /// the passive's HUD icon) when the hero is created.
        /// </summary>
        public const string PassiveMarkerBuff = "Pyromania_Marker";

        /// <summary>
        /// Disintegrate.TargetExecute adds a 1 s BUFF_Internal buff named after the spell to the target (IsHiddenOnClient
        /// false). Its BuffOnDeath refunds the mana, its BuffOnUpdateActions removes it at the first update.
        /// </summary>
        public const string QMarkBuff = "Disintegrate";
    }

    // ------------------------------------------------------------------------------------------------------------
    // Recording proxies
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Forwards every call and logs the semantic ones; one proxy type serves the three spell service interfaces.</summary>
    public class SpellServiceProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public Trace Log { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Log.Note(targetMethod!, args ?? []);
            try
            {
                var result = targetMethod!.Invoke(Target, args);
                Log.NoteResult(targetMethod, args ?? [], result);
                return result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>The event log of one run, in call order, with unit ids turned into the names of the scenario's units.</summary>
    public sealed class Trace
    {
        private readonly Dictionary<uint, string> _names = new();
        private readonly WorldState _world;
        /// <summary>Spell-service damage calls: the amounts the module asks for, before mitigation.</summary>
        public List<string> Damage { get; } = [];
        /// <summary>Committed hits (ICombatHooks.OnUnitDamaged): final amount after mitigation, every damage path included.</summary>
        public List<string> Hits { get; } = [];
        public List<string> Buffs { get; } = [];
        public List<string> Effects { get; } = [];
        /// <summary>Every <c>CreateEffect</c> call with the id the sink returned: what the 0x8C packet carries (name, bind, target, bone).</summary>
        public List<EffectCall> EffectCalls { get; } = [];
        /// <summary>The effect ids <c>KillEffect</c> was asked to end (the 0x3D packets).</summary>
        public List<uint> Killed { get; } = [];
        /// <summary>Stop-attack announcements (ISpellEffectSink.SendStopAttack, the 0x39 packets): the unit.</summary>
        public List<string> StopAttacks { get; } = [];
        public List<string> Cooldowns { get; } = [];
        public List<string> Assists { get; } = [];
        /// <summary>Animation plays and unlocks (ISpellEffectSink.PlayAnimation / StopAnimation), for the script-driven champion checks.</summary>
        public List<string> Animations { get; } = [];
        /// <summary>Auto-attack spell slot changes (ISpellEffectSink.SetAutoAttackSpellSlot): the unit and the absolute slot, -1 restores the default.</summary>
        public List<string> AttackSlots { get; } = [];
        /// <summary>Cast announcements (ISpellEffectSink.SendCast), for the script-driven champion checks.</summary>
        public List<SpellCastInfo> Casts { get; } = [];
        /// <summary>Chain missile target lists (ISpellEffectSink.SendChainMissileSync): the missile and the hops in flight order.</summary>
        public List<(uint Missile, uint[] Targets)> ChainSyncs { get; } = [];
        /// <summary>Missile destroys (ISpellEffectSink.SendDestroyMissile): the client missile ids the server took out.</summary>
        public List<uint> DestroyedMissiles { get; } = [];
        public List<string> Steps { get; } = [];
        /// <summary>Every recorded call of every category in call order (used to assert orderings the findings describe).</summary>
        public List<string> Timeline { get; } = [];

        public Trace(WorldState world) => _world = world;

        /// <summary>One <c>CreateEffect</c>: the 0x8C it sends, with the source (PosVar), target point (TargetPosVar) and third position it was given.</summary>
        public sealed record EffectCall(double At, uint Id, string Name, uint Bind, uint Target, string? Bone, string? EnemyName = null,
            Position2? Source = null, Position2? TargetPoint = null, Position2? Anchor = null, Position3? Direction = null, double? Height = null);

        public void NoteResult(MethodInfo method, object?[] args, object? result)
        {
            // Batch 582: CreateHitEffect is the default hit pass's world-placed effect (bind, no target, no bone).
            if (method.Name is not ("CreateEffect" or "CreateHitEffect") || result is not uint id) return;
            object? Arg(string name)
            {
                var index = Array.FindIndex(method.GetParameters(), p => p.Name == name);
                return index >= 0 ? args[index] : null;
            }
            EffectCalls.Add(new EffectCall(_world.Clock.Now(), id, (string)Arg("effectName")!, (uint)(Arg("bindNetId") ?? 0u),
                (uint)(Arg("targetNetId") ?? 0u), Arg("boneName") as string, Arg("enemyEffectName") as string,
                Arg("position") as Position2?, Arg("target") as Position2?, Arg("ownerPosition") as Position2?, Arg("direction") as Position3?, Arg("height") as double?));
        }

        public void Name(Entity unit, string name) => _names[unit.NetId] = name;

        private string N(object? unit) => unit switch
        {
            Entity entity => _names.TryGetValue(entity.NetId, out var name) ? name : $"#{entity.Name}",
            uint id when id == 0 => "-",
            uint id => _names.TryGetValue(id, out var name) ? name : $"#{id}",
            _ => "?",
        };

        private static string R(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private string T() => R(_world.Clock.Now());

        public void Note(MethodInfo method, object?[] args)
        {
            object? Arg(string name)
            {
                var index = Array.FindIndex(method.GetParameters(), p => p.Name == name);
                return index >= 0 ? args[index] : null;
            }
            var before = (Damage.Count, Buffs.Count, Effects.Count, Cooldowns.Count, Assists.Count);
            switch (method.Name)
            {
                case "OnUnitDamaged":
                    Hits.Add($"{T()} {N(Arg("source"))}->{N(Arg("target"))} dealt={R((double)Arg("damage")!)} raw={R((double)Arg("rawDamage")!)} " +
                        $"{(Arg("sourceKind") is { } hitKind ? hitKind : "none")} periodic={Arg("sourceIsPeriodic")}" +
                        (Arg("reflected") is true ? " reflected" : ""));
                    Timeline.Add("hit " + Hits[^1]);
                    break;
                case "ApplyDamage":
                    Damage.Add($"{T()} {N(Arg("source"))}->{N(Arg("target"))} {R((double)Arg("amount")!)} {Arg("type")} " +
                        $"{(Arg("sourceKind") is { } kind ? kind : "none")} vamp={R(Arg("vampScale") as double? ?? 1.0)} " +
                        $"reflect={Arg("isReflect") ?? false} periodic={Arg("sourceIsPeriodic") ?? false}");
                    break;
                // Batch 582: an Instacast missile child's cast (no cast answer; its 0x41 builds the missile) is still the cast the script made.
                case "SendCast" or "SendInstantMissileCast" when Arg("info") is SpellCastInfo cast:
                    Casts.Add(cast);
                    break;
                case "SendDestroyMissile" when Arg("missileNetId") is uint destroyed:
                    DestroyedMissiles.Add(destroyed);
                    break;
                case "SendChainMissileSync" when Arg("targets") is IReadOnlyList<uint> hops:
                    ChainSyncs.Add(((uint)Arg("missileNetId")!, hops.ToArray()));
                    break;
                case "RecordSupportAssist":
                    Assists.Add($"{T()} {N(Arg("source"))}->{N(Arg("recipient"))} {R(Arg("durationSeconds") as double? ?? 10.0)}");
                    break;
                case "Add":
                    Buffs.Add($"{T()} add {N(Arg("unit"))} {Arg("name")} type={Arg("type")} dur={R((double)Arg("duration")!)} " +
                        $"caster={N(Arg("casterNetId"))} mitigate={Arg("canMitigateDuration") ?? true}");
                    break;
                case "AddStacking":
                    Buffs.Add($"{T()} stack {N(Arg("unit"))} {Arg("name")} type={Arg("type")} dur={R((double)Arg("duration")!)} " +
                        $"caster={N(Arg("casterNetId"))} max={Arg("maxStacks")} replace={Arg("replaceExistingPayload") ?? false}");
                    break;
                case "Remove" when Arg("slot") is int slot && Arg("unit") is Entity holder:
                    Buffs.Add($"{T()} remove {N(holder)} {holder.Buffs.FirstOrDefault(b => b.Slot == slot)?.Name ?? "?"}");
                    break;
                case "CreateEffect" or "CreateHitEffect":
                    Effects.Add($"{T()} {Arg("effectName")} bind={N(Arg("bindNetId"))} at={Pos(Arg("position"))}");
                    break;
                case "KillEffect":
                    Killed.Add((uint)Arg("effectNetId")!);
                    break;
                case "SendStopAttack":
                    StopAttacks.Add($"{T()} {N(Arg("unit"))}");
                    break;
                case "PlayAnimation":
                    Animations.Add($"{T()} play {N(Arg("unit"))} {Arg("name")}");
                    break;
                case "StopAnimation":
                    Animations.Add($"{T()} stop {N(Arg("unit"))}");
                    break;
                case "SetAutoAttackSpellSlot":
                    AttackSlots.Add($"{T()} {N(Arg("unit"))} {Arg("slot")}");
                    break;
                case "SendCooldown":
                    Cooldowns.Add($"{T()} {N(Arg("unit"))} slot={Arg("slot")} {R((double)Arg("seconds")!)} summoner={Arg("summoner") ?? false}");
                    break;
            }
            Flush(before);
        }

        /// <summary>Appends whichever line the last call added to the ordered timeline.</summary>
        public void Flush((int, int, int, int, int) before)
        {
            if (Damage.Count > before.Item1) Timeline.Add("damage " + Damage[^1]);
            if (Buffs.Count > before.Item2) Timeline.Add("buff " + Buffs[^1]);
            if (Effects.Count > before.Item3) Timeline.Add("effect " + Effects[^1]);
            if (Cooldowns.Count > before.Item4) Timeline.Add("cooldown " + Cooldowns[^1]);
            if (Assists.Count > before.Item5) Timeline.Add("assist " + Assists[^1]);
        }

        private static string Pos(object? position) => position is Position2 p ? $"{R(p.X)},{R(p.Z)}" : "?";
    }

    // ------------------------------------------------------------------------------------------------------------
    // The match: production graph, Annie (script-driven) and an Orianna dummy
    // ------------------------------------------------------------------------------------------------------------

    internal sealed class Run : IDisposable
    {
        private readonly MatchRuntime _runtime;
        private readonly List<Entity> _watched = [];
        private readonly Dictionary<Entity, string> _watchedNames = new();

        public Run(int annieSkin = 0)
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Caster", "Annie", Team.Order, SkinId: annieSkin), new(2, 1, "Enemy", "Orianna", Team.Chaos),
            };
            _runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                readySettleSeconds: 0, roster: roster);
            _runtime.World.Clock.SetVirtual(0);
            _runtime.Session.CreateWorld();
            for (var i = 0; i < roster.Count; i++)
            {
                var id = i + 1;
                TestAssert.True(_runtime.Session.Authenticate(id, _runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                _runtime.Session.Spawn(id);
                TestAssert.True(_runtime.Session.MarkReady(id), "ready");
                var hero = _runtime.Session.Participants[i].Hero!;
                hero.HeroAutoAcquire = false;
                hero.Position = new Position2(7000 + i * 300, 7000);
                hero.MaxHealth = hero.Health = 5000;
                hero.Mana = hero.MaxMana;
            }
            TestAssert.True(_runtime.Session.Start(), "start");
            Annie = _runtime.Session.Participants[0].Hero!;
            Foe = _runtime.Session.Participants[1].Hero!;
            Log = new Trace(_runtime.World);
            Log.Name(Annie, "Annie");
            Log.Name(Foe, "Foe");
            _watched.AddRange([Annie, Foe]);
            _watchedNames[Annie] = "Annie";
            _watchedNames[Foe] = "Foe";
            // Production binds the module and activates the hero when the session creates the world (SetVarsByLevel, CharOnActivate).
            Module = (BbChampionModule)_runtime.Dependencies.ChampionDispatcher.FindModule("Annie")!;
            TestAssert.True(Module.IsBound, "production binds the Annie module to the match");
            Intercept();
        }

        public MatchRuntime Runtime => _runtime;
        public Entity Annie { get; }
        public Entity Foe { get; }
        public Trace Log { get; }
        public BbChampionModule Module { get; }

        private void Intercept()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Wrap<T>(string field) where T : class
            {
                var info = typeof(SpellSystem).GetField(field, flags)!;
                var proxy = DispatchProxy.Create<T, SpellServiceProxy>();
                var typed = (SpellServiceProxy)(object)proxy;
                typed.Target = info.GetValue(_runtime.Spells)!;
                typed.Log = Log;
                info.SetValue(_runtime.Spells, proxy);
            }
            Wrap<ISpellCombatService>("_combat");
            Wrap<ISpellBuffService>("_buffs");
            Wrap<ISpellEffectSink>("_effects");
            // Paths that bypass SpellSystem: committed hits (CombatService -> its hooks) and the hooks' own effect sink
            // (the hand-written Molten Shield reflection creates its sparks there).
            WrapField<ICombatHooks>(_runtime.Combat, typeof(CombatService), "_hooks");
            WrapField<ISpellEffectSink>(_runtime.Dependencies.ChampionHooks, typeof(MatchGameplayHooks), "_effects");
        }

        private void WrapField<T>(object owner, Type type, string field) where T : class
        {
            var info = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var proxy = DispatchProxy.Create<T, SpellServiceProxy>();
            var typed = (SpellServiceProxy)(object)proxy;
            typed.Target = info.GetValue(owner)!;
            typed.Log = Log;
            info.SetValue(owner, proxy);
        }

        public bool Cast(int slot, Entity? target = null, Position2? aim = null, Position2? origin = null)
        {
            var point = aim ?? target?.Position ?? Annie.Position;
            var end = new Position3(point.X, 0, point.Z);
            var start = origin is { } from ? new Position3(from.X, 0, from.Z) : end;
            return _runtime.Spells.HandleCastRequest(Annie, new SpellCastRequest(slot, false, start, end, target?.NetId ?? 0),
                _runtime.World.Clock.Now());
        }


        /// <summary>Runs the production driver for <paramref name="seconds"/> in 1/30 s steps and snapshots both heroes after each.</summary>
        public void Advance(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step)
            {
                _runtime.Tick(Step);
                Log.Steps.Add(Snapshot());
            }
        }

        /// <summary>
        /// Runs the driver step by step to <paramref name="seconds"/>; each action fires at the first step boundary at or after
        /// its time, before that step's tick, so both variants see exactly the same input at exactly the same step.
        /// </summary>
        public void Timeline(double seconds, params (double At, Action<Run> Do)[] actions)
        {
            var pending = actions.OrderBy(a => a.At).ToList();
            for (var step = 0; step * Step < seconds - 1e-9; step++)
            {
                while (pending.Count > 0 && pending[0].At <= step * Step + 1e-9)
                {
                    pending[0].Do(this);
                    pending.RemoveAt(0);
                }
                _runtime.Tick(Step);
                Log.Steps.Add(Snapshot());
            }
        }

        public void Refill()
        {
            for (var slot = 0; slot < 4; slot++) Annie.SpellCooldowns[slot] = 0;
            Annie.Mana = Annie.MaxMana;
        }

        public void GiveAbilityPower()
        {
            Annie.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            _runtime.HeroStats.Refresh(Annie, push: false);
            TestAssert.True(Annie.AbilityPower > 50, "Needlessly Large Rod raises ability power");
        }

        public string Snapshot()
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{_runtime.World.Clock.Now():0.######}");
            foreach (var (unit, name) in _watchedNames)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" | {name} hp={unit.Health:0.######} mana={unit.Mana:0.######} armor={unit.Armor:0.######} mr={unit.SpellBlock:0.######} ap={unit.AbilityPower:0.######} " +
                    $"cd=[{string.Join(',', unit.SpellCooldowns.Select(c => c.ToString("0.######", CultureInfo.InvariantCulture)))}] buffs=[");
                // The permanent passive marker is a finding (asserted separately), not a difference of behaviour.
                text.Append(string.Join(';', unit.Buffs.Where(b => b.Name != Findings.PassiveMarkerBuff)
                    .OrderBy(b => b.Name, StringComparer.Ordinal).Select(b =>
                    string.Create(CultureInfo.InvariantCulture,
                        $"{b.Name}:{b.Type}x{b.Stacks}@{b.ExpiresAt:0.####}")))).Append(']');
            }
            return text.ToString();
        }

        public void Dispose() => _runtime.World.Clock.UseReal();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Scenarios
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Compares two ordered event lists; a first mismatch (or a length difference) is reported with both neighbourhoods. The negative
    /// control uses it on two runs that must differ.
    /// </summary>
    private static void SameLines(string what, IEnumerable<string> first, IEnumerable<string> second)
    {
        var a = first.ToArray();
        var b = second.ToArray();
        string? message = null;
        for (var i = 0; i < Math.Min(a.Length, b.Length) && message is null; i++)
            if (a[i] != b[i])
                message = $"{what} differs at #{i}:\n  first:  {a[i]}\n  second: {b[i]}";
        if (message is null && a.Length != b.Length)
            message = $"{what}: {a.Length} vs {b.Length} events\n  first: {string.Join(" || ", a)}\n  second: {string.Join(" || ", b)}";
        if (message is not null) throw new InvalidOperationException(message);
    }

    internal static bool Press(Run run, char key)
    {
        run.Refill();
        return key switch
        {
            'Q' => run.Cast(0, run.Foe),
            'W' => run.Cast(1, null, run.Foe.Position, run.Annie.Position),
            'E' => run.Cast(2, run.Annie),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    /// <summary>Runs one scenario on the production (script-driven) Annie; BB_DIFF_TRACE=1 prints the call timeline of the run.</summary>
    private static Run Once(string name, Action<Run> scenario, int skin = 0)
    {
        var run = new Run(skin);
        try
        {
            scenario(run);
            if (Environment.GetEnvironmentVariable("BB_DIFF_TRACE") == "1")
            {
                Console.WriteLine($"== {name}");
                foreach (var line in run.Log.Timeline) Console.WriteLine("  " + line);
            }
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    [Check("scripting.bb-annie-differential-q")]
    public static void QDealsTheShippedTableAtEveryRank()
    {
        for (var rank = 1; rank <= 5; rank++)
        {
            using var run = Once($"Q{rank}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[0] = rank;
                TestAssert.True(r.Cast(0, r.Foe), $"Q rank {rank} accepted");
                r.Advance(1.5);
            });
            // The shipped Disintegrate table and coefficient, read from the script, not from an implementation.
            // Batch 64: the client's number getter returns a float32, so the script's SpellDamageRatio 0.7 reads 0.7f, the same
            // value as the inibin byte/10 coefficient.
            var expected = new[] { 85.0, 125, 165, 205, 245 }[rank - 1] + (double)0.7f * run.Annie.AbilityPower;
            TestAssert.Equal(1, run.Log.Damage.Count, "one hit");
            TestAssert.True(run.Log.Damage[0].Contains($" {expected.ToString("0.######", CultureInfo.InvariantCulture)} Magic Spell ", StringComparison.Ordinal),
                $"Q{rank} damage is DamageByLevel + 0.7 AP: {run.Log.Damage[0]}");
            // The script adds the 1 s Disintegrate mark buff to the target (its BuffOnDeath refunds the mana).
            TestAssert.Equal(1, run.Log.Buffs.Count(line => line.Contains($"add Foe {Findings.QMarkBuff} type=Internal dur=1 caster=Annie", StringComparison.Ordinal)),
                "script Q adds its mark buff");
        }
    }

    [Check("scripting.bb-annie-differential-w")]
    public static void WDealsTheShippedTableAtEveryRank()
    {
        for (var rank = 1; rank <= 5; rank++)
        {
            using var run = Once($"W{rank}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[1] = rank;
                TestAssert.True(r.Cast(1, null, r.Foe.Position, r.Annie.Position), $"W rank {rank} accepted");
                r.Advance(1.5);
            });
            var expected = new[] { 80.0, 130, 180, 230, 280 }[rank - 1] + 0.75 * run.Annie.AbilityPower;
            TestAssert.Equal(1, run.Log.Damage.Count, "one victim");
            TestAssert.True(run.Log.Damage[0].Contains($" {expected.ToString("0.######", CultureInfo.InvariantCulture)} Magic SpellAoe ", StringComparison.Ordinal),
                $"W{rank} damage is DamageByLevel + 0.75 AP: {run.Log.Damage[0]}");
        }
    }

    [Check("scripting.bb-annie-differential-e")]
    public static void EGivesTheShippedShieldAtEveryRank()
    {
        for (var rank = 1; rank <= 5; rank++)
        {
            using var run = Once($"E{rank}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[2] = rank;
                var armor = r.Annie.Armor;
                TestAssert.True(r.Cast(2, r.Annie), $"E rank {rank} accepted");
                r.Advance(0.5);
                TestAssert.Near(armor + 10 * rank, r.Annie.Armor, 1e-9, "Molten Shield armor");
                TestAssert.Near(30 + 10 * rank, r.Annie.SpellBlock, 1e-9, "Molten Shield magic resistance");
                // NextBuffVars hand-off: the script's by-level variables became the buff's InstanceVars.
                var shield = r.Module.Buffs!.Instances.Single(item => item.Name == "MoltenShield");
                TestAssert.Equal(10.0 * rank, (double)shield.InstanceVars["ArmorAmount"]!, "InstanceVars.ArmorAmount");
                TestAssert.Equal(10.0 + 10.0 * rank, (double)shield.InstanceVars["DamageReturn"]!, "InstanceVars.DamageReturn");
                TestAssert.Equal(rank, shield.SpellRank, "the buff keeps the rank of the cast that made it");
                r.Advance(15.0);
            });
            TestAssert.True(run.Log.Buffs.Any(line => line.Contains("add Annie MoltenShield type=CombatEnhancer dur=15 caster=Annie mitigate=False", StringComparison.Ordinal)),
                "the shield is the script's 15 s BUFF_CombatEnchancer");
        }
    }

    [Check("scripting.bb-annie-differential-pyromania")]
    public static void PassiveStacksAndStunFollowTheScripts()
    {
        // Five damaging casts arm the passive (Q/W/R count, E keeps a charge but adds none once armed); the next damaging
        // cast stuns for CharVars.StunDuration and starts the next cycle at one stack.
        foreach (var sequence in new[] { "EEEEEQ", "EEEEEW", "QWQWQQ", "QWQWQEQ", "QQQQWEWQ", "EEEEEEEE" })
        {
            using var run = Once($"passive {sequence}", r =>
            {
                r.GiveAbilityPower();
                for (var slot = 0; slot < 3; slot++) r.Annie.SpellRanks[slot] = 3;
                foreach (var key in sequence)
                {
                    TestAssert.True(Press(r, key), $"{sequence}: {key} accepted");
                    r.Advance(0.6);
                }
                r.Advance(3.0);
            });
            var stuns = run.Log.Buffs.Count(line => line.Contains(" Stun ", StringComparison.Ordinal));
            TestAssert.Equal(sequence == "EEEEEEEE" ? 0 : 1, stuns, $"{sequence}: stun count");
        }
    }

    [Check("scripting.bb-annie-differential-reflect")]
    public static void ShieldReflectsAttacksOnly()
    {
        for (var rank = 1; rank <= 5; rank++)
        {
            using var run = Once($"reflect E{rank}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[2] = rank;
                r.Annie.SpellRanks[0] = 1;
                // E, an auto attack (reflected), a spell hit (not reflected), and after the 15 s shield is gone another attack.
                r.Timeline(18.0,
                    (0.0, x => TestAssert.True(Press(x, 'E'), "E accepted")),
                    (0.5, x => x.Runtime.Combat.ApplyHit(x.Foe.NetId, x.Annie.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic)),
                    (1.0, x => x.Runtime.Combat.ApplyHit(x.Foe.NetId, x.Annie.NetId, 50.0, DamageType.Magic, DamageSourceKind.Spell)),
                    (1.5, x => x.Runtime.Combat.ApplyHit(x.Foe.NetId, x.Annie.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic)),
                    (16.5, x => x.Runtime.Combat.ApplyHit(x.Foe.NetId, x.Annie.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic)));
            });
            var reflectHits = run.Log.Hits.Where(h => h.StartsWith("0.5") || h.StartsWith("1.5")).Where(h => h.Contains("Annie->Foe")).ToArray();
            TestAssert.Equal(2, reflectHits.Length, "two auto attacks are reflected");
            // The script reflects as an ordinary DAMAGESOURCE_SPELLAOE hit (F6): no hit carries the reflected flag of the removed
            // hand-written reflection.
            TestAssert.Equal(0, run.Log.Hits.Count(h => h.Contains(" reflected", StringComparison.Ordinal)), "the script's reflect is an ordinary hit");
            var damage = new[] { 20.0, 30, 40, 50, 60 }[rank - 1] + 0.2 * run.Annie.AbilityPower;
            TestAssert.True(run.Log.Damage.Any(d => d.Contains($"Annie->Foe {damage.ToString("0.######", CultureInfo.InvariantCulture)} Magic SpellAoe ", StringComparison.Ordinal)),
                $"reflect is DamageReturn {damage} (table + 0.2 AP): {string.Join(" | ", run.Log.Damage)}");
            // The spell hit at 1.0 and the attack after the shield expired are not reflected (two reflections in all).
            TestAssert.Equal(2, run.Log.Hits.Count(h => h.Contains("Annie->Foe", StringComparison.Ordinal)), "only the two attacks inside the shield reflect");
        }
    }

    [Check("scripting.bb-annie-differential-q-refund")]
    public static void QRefundAndWindupLifecycle()
    {
        // The kill: Q refunds its mana through Disintegrate's BuffOnDeath. The scenario list also covers the lifecycle: target lost in the
        // windup (refund, owner decision 29 Sept), target lost in flight (the missile flies on to the corpse, its hit runs there and
        // the corpse refuses the damage, batch 348), caster lost in the
        // windup (no cast) and in flight (the missile still lands).
        var scenarios = new (string Name, Action<Run> Do, Action<Run> Check)[]
        {
            ("kill", r => { r.Foe.Health = 20; r.Annie.SpellRanks[0] = 2; r.Annie.Mana = r.Annie.MaxMana; Press(r, 'Q'); r.Advance(1.2); },
                r => TestAssert.Near(r.Annie.MaxMana, r.Annie.Mana, 1e-6, "the kill refunds the cost of the Q")),
            ("target dies in the windup", r => { r.Annie.SpellRanks[0] = 1; Press(r, 'Q');
                r.Timeline(1.0, (0.1, x => x.Runtime.Combat.Kill(x.Foe, x.Annie.NetId))); },
                r => { TestAssert.Equal(0, r.Log.Damage.Count, "no hit"); TestAssert.Near(0, r.Annie.SpellCooldowns[0], 1e-9, "the cast is refunded"); }),
            ("target dies in flight", r => { r.Annie.SpellRanks[0] = 1; Press(r, 'Q'); r.Foe.Position = r.Annie.Position + new Position2(600, 0);
                r.Timeline(1.0, (0.4, x => x.Runtime.Combat.Kill(x.Foe, x.Annie.NetId))); },
                // Batch 348: the hit runs at the corpse (Disintegrate's BBApplyDamage is called), and the corpse refuses the damage.
                r => TestAssert.Equal(0, r.Log.Hits.Count, "the missile ends at the corpse without damaging it")),
            ("caster dies in the windup", r => { r.Annie.SpellRanks[0] = 1; Press(r, 'Q');
                r.Timeline(1.0, (0.1, x => x.Runtime.Combat.Kill(x.Annie, x.Foe.NetId))); },
                r => TestAssert.Equal(0, r.Log.Damage.Count, "a cast that dies in its windup never lands")),
            ("caster dies in flight", r => { r.Annie.SpellRanks[0] = 1; Press(r, 'Q'); r.Foe.Position = r.Annie.Position + new Position2(600, 0);
                r.Timeline(1.5, (0.4, x => x.Runtime.Combat.Kill(x.Annie, x.Foe.NetId))); },
                r => TestAssert.Equal(1, r.Log.Damage.Count, "a launched missile survives its caster")),
            ("W caster dies in the windup", r => { r.Annie.SpellRanks[1] = 1; Press(r, 'W');
                r.Timeline(1.0, (0.1, x => x.Runtime.Combat.Kill(x.Annie, x.Foe.NetId))); },
                r => TestAssert.Equal(0, r.Log.Damage.Count, "a W that dies in its windup never lands")),
        };
        foreach (var (name, scenario, check) in scenarios)
        {
            using var run = Once($"Q lifecycle: {name}", scenario);
            try { check(run); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException($"{name}: {ex.Message}", ex); }
        }
    }

    /// <summary>
    /// Batch 348: a homing missile that reaches a target which died in its flight runs the spell's hit there. The shared hit pass
    /// (the spell manager's cast step, reached from the homing arrival) acts on every target object that exists, with no
    /// life check, and the shipped VeigarBalefulStrike guards its first block with CO_IS_NOT_DEAD on the target for exactly this arrival.
    /// Disintegrate's TargetExecute then adds Annie's Pyromania stack (its last BBSpellBuffAdd on Owner); the corpse takes no damage.
    /// </summary>
    [Check("scripting.bb-annie-q-reaching-a-corpse-runs-its-hit")]
    public static void QReachingACorpseRunsItsHit()
    {
        using var run = Once("Q at a corpse", r =>
        {
            r.Annie.SpellRanks[0] = 1;
            Press(r, 'Q');
            r.Foe.Position = r.Annie.Position + new Position2(600, 0);
            r.Timeline(1.0, (0.4, x => x.Runtime.Combat.Kill(x.Foe, x.Annie.NetId)));
        });
        TestAssert.Equal(1, run.Log.Damage.Count, "the hit ran at the corpse: Disintegrate's BBApplyDamage was called");
        TestAssert.Equal(0, run.Log.Hits.Count, "and the corpse took no damage");
        TestAssert.Equal(1, run.Annie.Buffs.Where(buff => buff.Name == "Pyromania").Sum(buff => buff.Stacks),
            "Disintegrate's hit ran at the corpse: its Pyromania stack on Annie");
    }

    [Check("scripting.bb-annie-differential-level-ups")]
    public static void LevelUpsKeepTheScriptVariablesAndTheStunInStep()
    {
        using var run = Once("level-ups", r =>
        {
            r.GiveAbilityPower();
            for (var slot = 0; slot < 3; slot++) r.Annie.SpellRanks[slot] = 1;
            for (var level = r.Annie.Level + 1; level <= 18; level++)
                r.Runtime.Combat.AwardHeroProgress(r.Annie, experience: 100000.0);
            TestAssert.Equal(18, r.Annie.Level, "Annie reached level 18");
            foreach (var key in "EEEEEQ")
            {
                TestAssert.True(Press(r, key), $"{key} accepted at level 18");
                r.Advance(0.6);
            }
            r.Advance(3.0);
        });
        // CharScriptAnnie.SetVarsByLevel: CharVars.StunDuration = 1.75 at every level (an 18 entry table).
        var vars = run.Module.CharVars(run.Annie);
        TestAssert.Equal(1.75, (double)vars["StunDuration"]!, "StunDuration after the level-ups");
        TestAssert.True(run.Log.Buffs.Any(line => line.Contains(" Stun ", StringComparison.Ordinal) && line.Contains("dur=1.75", StringComparison.Ordinal)),
            "the sixth cast stuns for the 1.75 s of CharVars.StunDuration");
    }

    [Check("scripting.bb-annie-differential-skins-and-death")]
    public static void FrostSkinEffectsAndDeathCleanup()
    {
        // Skin 5 (Frostfire) selects the *_frost effects through GetSkinID in every spell script.
        using (var frost = Once("frost skin", r =>
        {
            r.GiveAbilityPower();
            for (var slot = 0; slot < 3; slot++) r.Annie.SpellRanks[slot] = 2;
            foreach (var key in "QWE")
            {
                TestAssert.True(Press(r, key), $"{key} accepted (frost)");
                r.Advance(0.6);
            }
        }, skin: 5))
            TestAssert.True(frost.Log.Effects.Any(effect => effect.Contains("_frost.troy", StringComparison.Ordinal)),
                "the frost skin reaches the script's GetSkinID branch");

        // Death: the shield and the Pyromania stacks go with the hero; the permanent marker stays (PersistsThroughDeath).
        using var run = Once("death cleanup", r =>
        {
            r.GiveAbilityPower();
            r.Annie.SpellRanks[2] = 2;
            r.Timeline(6.0,
                (0.0, x => Press(x, 'E')),
                (0.7, x => Press(x, 'E')),
                (1.5, x => x.Runtime.Combat.Kill(x.Annie, x.Foe.NetId)));
        });
        TestAssert.False(run.Annie.Alive, "Annie is dead");
        TestAssert.False(run.Annie.Buffs.Any(buff => buff.Name is "MoltenShield" or "Pyromania"), "script buffs left with the hero");
        TestAssert.True(run.Annie.Buffs.Any(buff => buff.Name == Findings.PassiveMarkerBuff),
            "Pyromania_Marker persists through death (PersistsThroughDeath in the script)");
    }

    [Check("scripting.bb-annie-differential-findings")]
    public static void ScriptOrderAndPassiveMarkerAreAsShipped()
    {
        // F1 passive marker: the permanent aura of CharOnActivate.
        // F2 Q mark: the script adds the Disintegrate buff to the target.
        // F3 E recast: the script renews (BUFF_RENEW_EXISTING).
        // F4 stun order: the script stuns before it damages and removes the particle after the stun.
        using var run = Once("findings", r =>
        {
            r.GiveAbilityPower();
            r.Annie.SpellRanks[0] = 1;
            r.Annie.SpellRanks[2] = 1;
            foreach (var key in "EEEEE")
            {
                Press(r, key);
                r.Advance(0.6);
            }
            Press(r, 'Q');
            r.Advance(1.2);
        });
        var marker = run.Annie.Buffs.Single(buff => buff.Name == Findings.PassiveMarkerBuff);
        TestAssert.Equal(BuffType.Aura, marker.Type, "BUFF_Aura");

        // The shield: the first cast adds, the recasts renew the live instance (script BUFF_RENEW_EXISTING).
        var shieldAdds = run.Log.Buffs.Where(line => line.Contains(" MoltenShield ", StringComparison.Ordinal)).ToArray();
        TestAssert.True(shieldAdds[0].Contains(" add ", StringComparison.Ordinal) &&
                        shieldAdds.Skip(1).All(line => line.Contains(" stack ", StringComparison.Ordinal) && line.Contains("replace=True", StringComparison.Ordinal)),
            "first cast adds, recasts renew the live instance");

        var stun = run.Log.Timeline.FindIndex(line => line.StartsWith("buff", StringComparison.Ordinal) && line.Contains(" Stun ", StringComparison.Ordinal));
        var damage = run.Log.Timeline.FindIndex(line => line.StartsWith("damage", StringComparison.Ordinal) && line.Contains("Annie->Foe", StringComparison.Ordinal));
        var particle = run.Log.Timeline.FindIndex(line => line.Contains("remove Annie Pyromania_particle", StringComparison.Ordinal));
        TestAssert.True(stun >= 0 && stun < damage && stun < particle, "script order: stun, then damage; the particle is removed after the stun");

        // E: the shield first, the Pyromania stack second.
        static int First(Run r, string needle) => r.Log.Timeline.FindIndex(line => line.StartsWith("buff", StringComparison.Ordinal) && line.Contains(needle, StringComparison.Ordinal));
        TestAssert.True(First(run, " MoltenShield ") < First(run, " Pyromania "), "script E order: shield, stack");

        // F5 stun assist: BBApplyStun adds the shipped Stun buff, whose OnBuffActivate records a 10 s assist marker for the stunner.
        TestAssert.Equal(["Annie->Foe 10"], run.Log.Assists.Select(line => line[(line.IndexOf(' ') + 1)..]).ToArray(),
            "the stun records a 10 s assist marker");
    }

    [Check("scripting.bb-annie-differential-harness-detects-divergence")]
    public static void TheComparisonFailsWhenTheRunsDiffer()
    {
        // Negative control: two script runs with different ability power must not compare equal.
        using var first = new Run();
        using var second = new Run();
        first.GiveAbilityPower(); // the second run keeps zero ability power
        foreach (var run in new[] { first, second }) run.Annie.SpellRanks[0] = 1;
        Press(first, 'Q');
        Press(second, 'Q');
        first.Advance(1.0);
        second.Advance(1.0);
        // Same cast, but only the first Annie has the rod: the 0.7 AP part of the damage differs.
        TestAssert.Throws<InvalidOperationException>(() => SameLines("control", first.Log.Damage, second.Log.Damage),
            "different ability power gives different Q damage and the comparison reports it");
        TestAssert.Equal(1, first.Log.Damage.Count);
    }
}
