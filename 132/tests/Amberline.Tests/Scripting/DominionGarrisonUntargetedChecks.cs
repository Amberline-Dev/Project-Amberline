using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>Batch 950 (28 November 2026): 1.0.0.132 Garrison is untargeted (TargettingType 4, SelfAOE; wiki V1.0.0.128), on the two-Chaos match of <see cref="DominionGuardianChecks"/>.</summary>
public static partial class DominionGuardianChecks
{
    /// <summary>
    /// An untargeted Garrison request (no unit netId) is accepted within the script's 1250 gate and the script's own search garrisons the
    /// nearest guardian; past the gate the script's CanCast refuses it with no cooldown and no buff. Before the fix the untargeted request
    /// was refused every time, since the cast path demanded a unit.
    /// </summary>
    [Check("dominion.p132-garrison-untargeted-cast-searches-its-guardian", TimeoutSeconds = 300)]
    public static void GarrisonUntargetedCastSearchesItsGuardian()
    {
        using var match = StartMatch(secondChaos: true);
        var (runtime, order, caster) = (match.Runtime, match.Order, match.Chaos);
        Capture(match, caster);
        var guardian = match.Guardian;
        foreach (var hero in new[] { order, caster }) hero.HeroAutoAcquire = false;
        caster.SummonerSpells[0] = "SummonerOdinGarrison";
        var at = new Position3(guardian.Position.X, 0.0, guardian.Position.Z);
        order.Position = match.OrderHome;

        caster.Position = Offset(guardian, -1400.0);
        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.False(runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(0, true, at, at, 0), runtime.Combat.CurrentTime),
            "an untargeted Garrison past 1250 of every guardian is refused");
        TestAssert.True(caster.SummonerCooldowns[0] == 0.0, "the refused Garrison spends no cooldown");
        TestAssert.False(guardian.Buffs.Any(buff => buff.Name == "SummonerOdinGarrison"), "the refused Garrison garrisons nothing");

        Advance(runtime, 1.0);
        caster.Position = Offset(guardian, -600.0);
        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.True(runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(0, true, at, at, 0), runtime.Combat.CurrentTime),
            "an untargeted Garrison within 1250 of a guardian is accepted");
        TestAssert.True(Until(runtime, () => guardian.Buffs.Any(buff => buff.Name == "SummonerOdinGarrison"), 1.0),
            "the script's own search garrisons the guardian");
        TestAssert.True(caster.SummonerCooldowns[0] > 0.0, "the accepted Garrison spends its cooldown");
    }
}
