using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 438: Paranoia's first press nearsights every enemy champion for its four seconds (BBApplyNearSight adds NearSight, whose
    // OnBuffActivate sets SetNearSight) and cuts its sight by 300 (NocturneParanoiaTarget: IncPermanentFlatBubbleRadiusMod). A nearsighted
    // client keeps only its own sight bubble (the client's near-sight gate), so the enemy in the river loses the mid
    // lane that his Chaos ally in mid showed him, and sees it again when the window closes. The ally's own bubble still shows Nocturne.
    // The ally stands out of R's 2500 reach at the cast, so the cast lists no enemy and reveals no caster (SpellCastTargets, RevealAttack);
    // it walks back into mid during the window.
    [Check("spells-b438-nocturne-paranoia-takes-the-allies-sight-from-the-nearsighted-enemy")]
    public static void ParanoiaTakesTheAlliesSightFromTheNearsightedEnemy()
    {
        using var match = new Match("Nocturne", Team.Order, extraEnemy: true);
        var hero = match.Hero(0);
        var foe = match.Hero(1);
        var ally = match.Hero(2);
        var foeView = match.Runtime.Session.Participants[1].View;
        var allyView = match.Runtime.Session.Participants[2].View;
        foe.Position = new Position2(3000, 9000);
        hero.Position = new Position2(7000, 7000);
        ally.Position = new Position2(7400, 7000);
        match.Runtime.Delivery.Flush();
        TestAssert.True(foeView.Visible.Contains(hero.NetId), "before: the enemy in the river sees Nocturne in mid through his Chaos ally");

        ally.Position = new Position2(11000, 11000);
        match.Runtime.Delivery.Flush();
        match.Cast(0, 3, hero);
        match.Runtime.Delivery.Flush();
        ally.Position = new Position2(7400, 7000);
        RunParanoiaSeconds(match, 0.5);
        TestAssert.True(foe.Buffs.Any(buff => buff.Type == BuffType.NearSight), "during: the enemy is nearsighted");
        TestAssert.Near(-300.0, VisibilityRules.BubbleRadiusMods(foe).Flat, 1.0, "during: the enemy's sight is 300 less");
        TestAssert.False(foeView.Visible.Contains(hero.NetId), "during: the nearsighted enemy loses the mid lane that his ally's sight showed");
        TestAssert.True(allyView.Visible.Contains(hero.NetId), "during: the nearsighted ally still sees Nocturne in its own sight");

        RunParanoiaSeconds(match, 4.0);
        TestAssert.False(foe.Buffs.Any(buff => buff.Type == BuffType.NearSight), "after the window: the nearsight is gone");
        TestAssert.Near(0.0, VisibilityRules.BubbleRadiusMods(foe).Flat, 1.0, "after the window: the enemy's sight is back");
        TestAssert.True(foeView.Visible.Contains(hero.NetId), "after the window: the enemy sees Nocturne in mid again");
    }

    private static void RunParanoiaSeconds(Match match, double seconds)
    {
        // MatchRuntime.Tick also ticks the buffs (BuffLifecycle), which the spell system's Tick alone does not.
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.05) match.Runtime.Tick(0.05);
        match.Runtime.Delivery.Flush();
    }
}
