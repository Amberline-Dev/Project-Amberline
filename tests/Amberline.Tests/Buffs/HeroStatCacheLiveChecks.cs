using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("buffs.live-stat-refresh-for-unlisted-rows")]
    public static void UnlistedStatRowsRefreshTheHeroCacheAtOnce()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var failures = new List<string>();
        void Probe(string name, BuffPayload payload, Func<double> read, double expected)
        {
            var before = read();
            var buff = match.Runtime.Buffs.AddBuff(hero, name, BuffType.Internal, 30.0, hero.NetId, payload);
            if (buff is null) { failures.Add($"{name} not added"); return; }
            var after = read();
            if (Math.Abs(before + expected - after) > 1e-9) failures.Add($"{name} {before} -> {after}, expected +{expected}");
            match.Runtime.Buffs.RemoveBuff(hero, buff.Slot);
        }
        Probe("ProbeArmorPenPercent", new BuffPayload { ArmorPenPercent = 0.2 }, () => hero.ArmorPenetrationPercent, 0.2);
        Probe("ProbeMagicPenFlat", new BuffPayload { MagicPenFlat = 7.0 }, () => hero.MagicPenetrationFlat, 7.0);
        Probe("ProbeMagicPenPercent", new BuffPayload { MagicPenPercent = 0.1 }, () => hero.MagicPenetrationPercent, 0.1);
        Probe("ProbeMaxMana", new BuffPayload { MaxMp = 100.0 }, () => hero.MaxMana, 100.0);
        // The income CombatService grants: the live GoldPer10 rows plus the loadout cache (which subtracts those rows).
        Probe("ProbeGoldPer10", new BuffPayload { GoldPer10 = 3.0 },
            () => hero.LoadoutGoldPer10 + hero.Buffs.Sum(buff => buff.Payload.GoldPer10), 3.0);
        Probe("ProbeRespawnTime", new BuffPayload { RespawnTimePercent = 0.25 }, () => hero.LoadoutRespawnTimePercent, 0.25);
        TestAssert.True(failures.Count == 0, "rows that do not refresh the hero cache at once: " + string.Join("; ", failures));
    }

}
