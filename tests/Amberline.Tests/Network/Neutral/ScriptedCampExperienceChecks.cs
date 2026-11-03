using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 219 (coordinator follow-up): a camp's experience is paid by the script's GiveExpToNearHeroesFromNeutral, through the
// awarder the composition wires. Each check runs a production match with three Order heroes (near at 250 units from the
// monster, mid at 500, far at 1500) and one Chaos hero at 1500, then kills every member of the camp type with one hit from
// the near hero. The expected experience is the old kill-path share (two recipients inside 800 units, the team's global
// experience for Worm) times the hero's own experience bonus; the expected gold is the old CharacterData gold.
public static class ScriptedCampExperienceChecks
{
    [Check("neutral.camp-experience.wraith")]
    public static void WraithCampsPayTheirShare() => AssertCampShares("Wraith", 3, 9);

    [Check("neutral.camp-experience.golem")]
    public static void GolemCampsPayTheirShare() => AssertCampShares("Golem", 5, 11);

    [Check("neutral.camp-experience.wolves")]
    public static void WolvesCampsPayTheirShare() => AssertCampShares("Wolves", 2, 8);

    [Check("neutral.camp-experience.lizard")]
    public static void LizardCampsPayTheirShare() => AssertCampShares("Lizard", 4, 10);

    [Check("neutral.camp-experience.ancient-golem")]
    public static void AncientGolemCampsPayTheirShare() => AssertCampShares("Ancient Golem", 1, 7);

    [Check("neutral.camp-experience.dragon")]
    public static void DragonCampPaysItsShare() => AssertCampShares("Dragon", 6);

    [Check("neutral.camp-experience.baron")]
    public static void BaronCampPaysItsShare() => AssertCampShares("Baron", 12);

    [Check("neutral.camp-experience-script-pays-only")]
    public static void KillPathPaysNoCampExperienceWithoutTheScriptNative()
    {
        var runtime = StartCampMatch();
        // The script's native is the only payer of camp experience: with it switched off, a kill must pay none.
        runtime.Waves!.LevelScript.AwardNeutralExperience = null;
        var near = OrderHeroes(runtime)[0];
        var monster = Members(runtime, 5)[0];
        Place(near, monster, 250.0);
        var before = near.Experience;
        TestAssert.True(runtime.Combat.ApplyHit(near.NetId, monster.NetId, monster.Health + 1.0,
            DamageType.True, DamageSourceKind.Spell), "one hit kills the first Golem");
        TestAssert.Near(0.0, near.Experience - before, 1e-9,
            "the kill path pays no camp experience when the script's native does not");
    }

    private static void AssertCampShares(string label, params int[] camps)
    {
        var runtime = StartCampMatch();
        var heroes = OrderHeroes(runtime);
        var chaos = runtime.Session.Participants.Single(participant => participant.Team == Team.Chaos).Hero!;
        var values = GameData.Default.LevelConstants.Values;
        var low = values["gcd_PercentEXPBonusMinimum"];
        var high = values["gcd_PercentEXPBonusMaximum"];
        double Bonus(Entity hero) => Math.Clamp(hero.LoadoutExperiencePercent, low, high);
        foreach (var camp in camps)
        foreach (var monster in Members(runtime, camp))
        {
            var near = heroes[0];
            var mid = heroes[1];
            var far = heroes[2];
            Place(near, monster, 250.0);
            Place(mid, monster, 500.0);
            Place(far, monster, 1500.0);
            Place(chaos, monster, 1500.0);
            var all = new[] { near, mid, far, chaos };
            var experienceBefore = all.Select(hero => hero.Experience).ToArray();
            var goldBefore = all.Select(hero => hero.Gold).ToArray();
            TestAssert.True(runtime.Combat.ApplyHit(near.NetId, monster.NetId, monster.Health + 1.0,
                DamageType.True, DamageSourceKind.Spell), $"{label} member {monster.NeutralMember} dies to one hit");
            var share = ProgressionRules.SharedUnitExperience(monster.ExperienceReward, 2, 1.0);
            var global = monster.GlobalExperienceReward;
            double[] expectedExperience =
            [
                (share + global) * (1.0 + Bonus(near)),
                (share + global) * (1.0 + Bonus(mid)),
                global * (1.0 + Bonus(far)),
                0.0,
            ];
            double[] expectedGold =
            [
                monster.GoldReward + monster.GlobalGoldReward,
                monster.GlobalGoldReward,
                monster.GlobalGoldReward,
                0.0,
            ];
            for (var index = 0; index < all.Length; index++)
            {
                TestAssert.Near(expectedExperience[index], all[index].Experience - experienceBefore[index], 1e-6,
                    $"{label} member {monster.NeutralMember} experience of hero {index}");
                TestAssert.Near(expectedGold[index], all[index].Gold - goldBefore[index], 1e-6,
                    $"{label} member {monster.NeutralMember} gold of hero {index}");
            }
        }
    }

    private static MatchRuntime StartCampMatch()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Order", "Shaco", Team.Order),
            new SessionParticipantDefinition(4, 3, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "production neutral match starts");
        runtime.Tick(120.0);
        return runtime;
    }

    private static Entity[] OrderHeroes(MatchRuntime runtime) =>
        runtime.Session.Participants.Where(participant => participant.Team == Team.Order)
            .Select(participant => participant.Hero!).ToArray();

    private static Entity[] Members(MatchRuntime runtime, int camp) =>
        runtime.World.Entities.Values
            .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == camp)
            .OrderBy(entity => entity.NeutralMember).ToArray();

    private static void Place(Entity hero, Entity monster, double offset) =>
        hero.Position = new Position2(monster.Position.X + offset, monster.Position.Z);
}
