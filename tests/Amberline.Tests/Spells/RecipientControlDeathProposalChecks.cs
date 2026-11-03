using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-recipient-control-death-janna-r-rapid-revive")]
    public static void JannaKnockbackControlDoesNotEnterTheNextLife()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, enemyChampion: "Janna");
            var recipient = match.Hero(0);
            var janna = match.Hero(1);
            match.Cast(1, 3, janna);
            Advance(match, .3);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "MoveAway" && buff.Type == BuffType.Stun),
                "actual Janna R installed the knockback lock");
            TestAssert.True(match.Runtime.Combat.Kill(janna, recipient.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "MoveAway"),
                "caster death alone leaves the living recipient's movement control to finish");
            TestAssert.True(match.Runtime.Combat.Kill(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == "MoveAway"),
                "recipient death retires Janna's nonpersistent movement control");
            match.Runtime.Combat.ScheduleRespawn(recipient);
            recipient.SummonerSpells[0] = "SummonerRevive";
            TestAssert.True(match.TryCast(0, 0, recipient, summoner: true),
                "dead-only Revive uses the production cast path");
            TestAssert.False(match.Runtime.Buffs.IsStunned(recipient),
                "the revived hero is free of the previous life's action lock");
            TestAssert.True(match.TryCast(0, 2, recipient),
                "a newly revived hero can cast E immediately");
        }
    }

    [Check("spells-live-recipient-control-death-exact-aliases")]
    public static void ShippedControlAliasesRetireAtCommittedRecipientDeath()
    {
        var exact = new[]
        {
            "BlindMonkRKick", "BlindMonkRDamage", "OrianaStun",
            "MoveAwayCollision", "RocketGrab2", "PowerFistSlow",
            // Playtest B2 YV5, 26 Sept 2026: Condemn applies the global Stun.
            "VayneCondemnMissile", "Stun",
        };
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            foreach (var name in exact)
                TestAssert.True(match.Runtime.Buffs.AddBuff(recipient, name,
                    BuffType.Stun, 5.0, caster.NetId, canMitigateDuration: false) is not null);
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            TestAssert.False(recipient.Buffs.Any(buff => exact.Contains(buff.Name)),
                "death removes only the audited champion control instances");
            match.Runtime.Combat.ScheduleRespawn(recipient);
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(recipient));
            TestAssert.False(match.Runtime.Buffs.IsStunned(recipient),
                "rapid respawn does not inherit a previous-life control lock");
        }

        // Move and MoveAway are shared script names. Janna's real Move and MoveAway are script buffs, which the script engine retires with
        // any other non-persistent script buff at the holder's death (the first check above); a plain instance outside the engine is not
        // a control of this cleanup.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var name in new[] { "Move", "MoveAway" })
        {
            using var match = new Match("Annie", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            var unrelated = match.Runtime.Buffs.AddBuff(recipient, name,
                BuffType.Internal, 5.0, caster.NetId, canMitigateDuration: false);
            TestAssert.True(unrelated is not null);
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => ReferenceEquals(buff, unrelated)),
                $"{team}/{name}: a generic instance outside the script engine is preserved");
            match.Runtime.Combat.ScheduleRespawn(recipient);
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(recipient));
            TestAssert.True(recipient.Buffs.Any(buff => ReferenceEquals(buff, unrelated)));
            TestAssert.False(match.Runtime.Buffs.IsStunned(recipient),
                "negative control carries no old-life action lock");
        }
    }
}
