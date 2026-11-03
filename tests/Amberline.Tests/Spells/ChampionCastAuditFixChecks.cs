using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-audit-ground-casts-ignore-incidental-unit")]
    public static void GroundAndSelfCastsIgnoreVanishedRequestTarget()
    {
        foreach (var (champion, slot) in new[]
        {
            ("Annie", 1), ("Annie", 2), ("Annie", 3),
            ("Orianna", 0), ("Orianna", 1), ("LeeSin", 0), ("LeeSin", 2),
            ("Nidalee", 0), ("Nidalee", 1), ("Yorick", 0), ("Yorick", 1),
            ("Shaco", 0), ("Shaco", 1), ("Ezreal", 0), ("Ezreal", 2),
            ("Vayne", 0), ("Vayne", 3), ("Janna", 0), ("Blitzcrank", 1),
        })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Shaco");
            var caster = match.Hero(0); var incidental = match.Hero(1);
            incidental.Position = caster.Position + new Position2(50, 0);
            match.Cast(1, 3, incidental);
            Advance(match, .3);
            TestAssert.True(incidental.Buffs.Any(buff => buff.Payload.Untargetable),
                "the real Hallucinate applicator is active");
            var point = new Position3(incidental.Position.X, 0, incidental.Position.Z);
            var clear = match.Runtime.Spells.CanCast(caster, slot, 0, point, .3);
            var stale = match.Runtime.Spells.CanCast(caster, slot, incidental.NetId, point, .3);
            TestAssert.True(clear.Allowed, $"{champion}/{slot}: targetless control is legal: {clear}");
            TestAssert.Equal(clear, stale, $"{champion}/{slot}: incidental ID cannot change validation");
            TestAssert.True(match.TryCast(0, slot, incidental),
                $"{champion}/{slot}: the same native-format request reaches execution");
            TestAssert.Equal(1, caster.MatchStatistics.SpellCasts[slot]);
        }
    }

    [Check("spells-audit-unit-casts-retain-target-validation")]
    public static void UnitCastsStillRejectVanishedTargetsWithoutCommitment()
    {
        foreach (var (champion, slot) in new[]
        {
            ("Annie", 0), ("LeeSin", 3), ("Yorick", 2),
            ("Shaco", 2), ("Vayne", 2), ("Janna", 1),
        })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Shaco");
            var caster = match.Hero(0); var target = match.Hero(1);
            match.Cast(1, 3, target); Advance(match, .3);
            var mana = caster.Mana;
            var check = match.Runtime.Spells.CanCast(caster, slot, target.NetId,
                new Position3(target.Position.X, 0, target.Position.Z), .3);
            TestAssert.Equal(SpellCastFailure.Target, check.Failure, $"{champion}: vanished unit is unavailable");
            TestAssert.False(match.TryCast(0, slot, target));
            TestAssert.Equal(mana, caster.Mana);
            TestAssert.Equal(0.0, caster.SpellCooldowns[slot]);
            TestAssert.Equal(0, caster.MatchStatistics.SpellCasts[slot]);
        }
    }

    [Check("spells-audit-recast-and-form-target-validation")]
    public static void RecastAndFormUseTheirCurrentTargetingMode()
    {
        using (var match = new Match("LeeSin", Team.Order, enemyChampion: "Shaco", extraEnemy: true))
        {
            var caster = match.Hero(0); var incidental = match.Hero(1); var marked = match.Hero(2);
            incidental.Position = caster.Position + new Position2(200, 350);
            marked.Position = caster.Position + new Position2(300, 0);
            match.CastGround(0, marked.Position); Advance(match, .5);
            // 19 October 2026: the stored mark (LeeSinState.QTarget) is the BlindMonkQOne buff and the dash (QDash) the script move of the buff
            // BlindMonkQTwoDash, which follows its mark.
            TestAssert.True(marked.Buffs.Any(buff => buff.Name == "BlindMonkQOne"), "real Sonic Wave marks its target");
            match.Cast(1, 3, incidental); Advance(match, .3);
            TestAssert.True(match.TryCast(0, 0, incidental), "Q2 ignores incidental ID and follows its stored mark");
            Advance(match, .01);
            TestAssert.Equal(marked.NetId, caster.ForcedMove!.FollowNetId, "the dash follows the marked unit");
        }
        using (var match = new Match("LeeSin", Team.Order, enemyChampion: "Shaco"))
        {
            var caster = match.Hero(0); var incidental = match.Hero(1);
            match.Cast(0, 1, caster); Advance(match, .3);
            TestAssert.True(caster.Buffs.Any(buff => buff.Name == "BlindMonkWManager") && caster.ActiveSpellNames[1] == "BlindMonkWTwo",
                "real Safeguard opened Iron Will");
            match.Cast(1, 3, incidental); Advance(match, .3);
            TestAssert.True(match.TryCast(0, 1, incidental), "Iron Will ignores an incidental target");
        }
        using (var match = new Match("Nidalee", Team.Order, enemyChampion: "Shaco"))
        {
            var caster = match.Hero(0); var incidental = match.Hero(1);
            match.Cast(0, 3, caster); Advance(match, .5);
            TestAssert.True(caster.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"),
                "real transform selected the cougar spell names");
            match.Cast(1, 3, incidental); Advance(match, .3);
            TestAssert.True(match.TryCast(0, 2, incidental), "Swipe ignores the unit ID that Primal Surge requires");
        }
        using (var match = new Match("Janna", Team.Order, enemyChampion: "Shaco"))
        {
            var caster = match.Hero(0); var incidental = match.Hero(1);
            match.CastGround(0, incidental.Position);
            match.Cast(1, 3, incidental); Advance(match, .3);
            TestAssert.True(JannaCharging(caster), "Howling Gale is charging");
            TestAssert.True(match.TryCast(0, 0, incidental), "Howling Gale's self recast ignores incidental ID");
            TestAssert.True(!JannaCharging(caster) && JannaCubes(match).Count == 1,
                "the accepted recast releases the existing charge from its TestCube");
        }
    }
}
