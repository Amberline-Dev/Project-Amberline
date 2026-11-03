using System.Reflection;
using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>Phase 1 loose ends (18 October 2026): the script hit hooks read the swing's real result, not always HIT_Normal.</summary>
public static class BbHitResultChecks
{
    /// <summary>Forwards every call to the real script sink and remembers the result each landed hit carried.</summary>
    public class HitSinkProbe : DispatchProxy
    {
        public IBbHookSink Target { get; set; } = null!;
        public List<BasicAttackOutcome> Hits { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            // CharOnHitUnit and BuffOnHitUnit run as the hit is resolved (ModifyBasicAttackDamage, W28); its fifth argument is the result.
            if (targetMethod!.Name == "ModifyBasicAttackDamage") Hits.Add((BasicAttackOutcome)args![4]!);
            try { return targetMethod.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    [Check("scripting.bb-hit-hooks-carry-the-swing-result")]
    public static void ScriptHitHooksReceiveTheRealResult()
    {
        // The script constants (BbConstants: HIT_Normal 0, HIT_Critical 1, HIT_Dodge 2, HIT_Miss 3).
        TestAssert.Equal(0.0, BbHookRouter.HitResultOf(BasicAttackOutcome.Normal));
        TestAssert.Equal(1.0, BbHookRouter.HitResultOf(BasicAttackOutcome.Critical));
        TestAssert.Equal(2.0, BbHookRouter.HitResultOf(BasicAttackOutcome.Dodge));
        TestAssert.Equal(3.0, BbHookRouter.HitResultOf(BasicAttackOutcome.Miss));

        foreach (var crit in new[] { false, true })
        {
            using var match = new Match("Taric");
            var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
            var proxy = DispatchProxy.Create<IBbHookSink, HitSinkProbe>();
            var probe = (HitSinkProbe)(object)proxy;
            probe.Target = hooks.ScriptHooks ?? throw new InvalidOperationException("Taric runs from her scripts");
            hooks.ScriptHooks = proxy;
            match.Hero.CritChance = 1.0;
            match.Foe.Position = new Position2(7100, 7000);
            // Roll 0 is under the crit chance, 1 is over it; the dodge and miss rolls never trigger.
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe, new AttackRolls(crit ? 0.0 : 1.0, 1.0, 1.0)),
                "the attack starts");
            match.Advance(1.0);
            TestAssert.Equal([crit ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal], probe.Hits.ToArray(),
                $"the landed hit reaches the script hooks with its result (crit={crit})");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
        }
    }
}
