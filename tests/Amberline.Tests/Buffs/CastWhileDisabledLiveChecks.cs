using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("buffs.live-stun-keeps-cast-while-disabled-bits")]
    public static void StunKeepsOnlyTheCastWhileDisabledSpellCastable()
    {
        using var match = new Match("Tryndamere", Team.Order);
        var hero = match.Hero(0);
        TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "Stun", BuffType.Stun, 2.0, hero.NetId) is not null,
            "stun applied");
        var bits = match.Runtime.HeroStats.Snapshot(hero).CanCastBits1;
        TestAssert.Equal(0u, bits & 0x7u, "slots 0-2 cannot be cast while stunned");
        TestAssert.Equal(0x8u, bits & 0xFu, "Undying Rage (slot 3, CanCastWhileDisabled) stays castable under stun");
    }

}
