using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Batch 897 (18 November 2026): every self poll (a character script's, an item copy's, a talent's, a bound unit's) runs on its holder's
/// 0.25 s self-update line (<c>BbSelfUpdate</c>), a hook that owns a <c>BBExecutePeriodically</c> included: its period is tested on the
/// runs that line gives it, as the shipped buff hooks' periods are tested on the buff update. Before, such a hook ran on every 33 ms
/// step (batch 246's exemption).
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Items/3001 (Abyssal Scepter) re-adds <c>AbyssalScepterAuraSelf</c> (1.0 s, <c>BUFF_RENEW_EXISTING</c>) from a 0.9 s
    /// <c>BBExecutePeriodically</c> of its <c>UpdateSelfBuffStats</c>. Each renewal is a run of the copy's hook, so every renewal falls on a
    /// step of the holder's self-update line, and the one aura instance lives through the whole hold.
    /// </summary>
    [Check("items-live-item-self-poll-with-a-periodic-runs-on-the-quarter-second-line")]
    public static void ItemSelfPollWithAPeriodicRunsOnTheLine()
    {
        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Orianna");
        var hero = match.Hero(0);
        hero.Inventory = [new InventorySlot(3001), null, null, null, null, null];
        const double step = 1.0 / 30.0;
        for (var wait = 0; wait < 60 && !hero.Buffs.Any(buff => buff.Name == "AbyssalScepterAuraSelf"); wait++) match.Runtime.Tick(step);
        var aura = hero.Buffs.Single(buff => buff.Name == "AbyssalScepterAuraSelf");
        var renewals = new List<(double At, bool OnLine)>();
        var elapsed = 0.0;
        for (var index = 0; index < 180; index++)
        {
            var left = aura.ExpiresAt;
            match.Runtime.Tick(step);
            elapsed += step;
            if (aura.ExpiresAt > left - step + 1e-6) renewals.Add((elapsed, match.Runtime.Spells.SelfUpdateDue(hero)));
        }
        var text = string.Join(" ", renewals.Select(row => $"{row.At:0.000}{(row.OnLine ? "" : "!")}"));
        Console.WriteLine($"3001 self aura renewals in 6.0 s: {text}");
        TestAssert.True(ReferenceEquals(aura, hero.Buffs.Single(buff => buff.Name == "AbyssalScepterAuraSelf")),
            "one AbyssalScepterAuraSelf instance for the whole hold");
        TestAssert.True(renewals.Count >= 5, $"the 0.9 s periodic renews the aura ({text})");
        TestAssert.True(renewals.All(row => row.OnLine),
            $"every renewal is a run on the holder's 0.25 s self-update line, not a 33 ms step between its lines (! marks one): {text}");
    }

}
