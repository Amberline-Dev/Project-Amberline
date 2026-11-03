using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 340 item 1: when a cast pays. The shared cast code (the cast-and-serve routine)
/// spends the cast's mana and starts its cooldown (<c>ApplyCostsOfSpell</c> -> <c>AdjustVarsAfterSpellCast</c>) at the windup's cast frame;
/// an instacast pays at once (<c>TryCastInstaSpell</c>). A stun does not stop a champion's windup (every non-instant champion
/// slot spell carries <c>CantCancelWhileWindingUp</c>, which the cancel-blocked test honours for a caster that cannot cast, and the
/// stun's channel stop, the channel-stop routine only ends a caster past its cast frame); a death does, so a windup
/// cut by its caster's death costs nothing. Malphite's Seismic Shard (a windup missile, 70 mana, 8 s at rank one) and Nunu's Absolute
/// Zero (a windup channel, 150 mana, 150 s) are the probes; the instacast half is Malphite's Brutal Strikes check.
/// </summary>
public static class CastFrameCostChecks
{
    private static double CastTime(string spell) => GameData.Default.Spell(spell, 5).CastTime;

    [Check("spells.windup-cast-pays-at-its-cast-frame-and-a-stun-in-the-windup-does-not-stop-it")]
    public static void WindupPaysAtTheCastFrameThroughAStun()
    {
        using var match = new Match("Malphite");
        match.Learn(1);
        match.Refill();
        var mana = match.Hero.Mana;
        var castTime = CastTime("SeismicShard");
        TestAssert.True(castTime > 0.2, $"Seismic Shard has a windup ({castTime:0.###} s)");
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard is cast at a foe 300 away");
        TestAssert.Near(mana, match.Hero.Mana, 0.5, "the press spends no mana: the cast frame does");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "the press starts no cooldown: the cast frame does");
        match.Advance(Step * 2);
        match.Runtime.Buffs.AddBuff(match.Hero, "ProbeStun", BuffType.Stun, 2.0, match.Foe.NetId, canMitigateDuration: false);
        TestAssert.Near(mana, match.Hero.Mana, 0.5, "a stun in the windup spends nothing");
        var peak = 0.0;
        for (var elapsed = 0.0; elapsed < castTime + 0.2; elapsed += Step)
        {
            match.Advance(Step);
            peak = Math.Max(peak, match.Hero.SpellCooldowns[0]);
        }
        TestAssert.Near(mana - 70.0, match.Hero.Mana, 1.0, "the cast frame spends the 70 mana, stunned or not");
        TestAssert.Near(8.0, peak, Step + 1e-6, "and starts the 8 s cooldown");
        match.Advance(1.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, $"the shard still flies and hits (log {string.Join(" | ", match.Log.Hits)})");
    }

    [Check("spells.windup-cast-cut-by-its-casters-death-costs-nothing")]
    public static void WindupCutByDeathCostsNothing()
    {
        using var match = new Match("Malphite");
        match.Learn(1);
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard is cast at a foe 300 away");
        match.Advance(Step * 2);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.False(match.Hero.Alive, "Malphite dies in the windup");
        match.Advance(1.0);
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-9, "no cast frame, no cooldown");
        TestAssert.True(match.Hero.Mana >= mana - 0.5, $"no cast frame, no mana spent ({mana:0.#} -> {match.Hero.Mana:0.#})");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "and no shard");
    }

    /// <summary>
    /// Security review of batch 340: the slot of a windup that has not paid yet shows no cooldown, so a second request for it must be
    /// refused, as the client never sends one (the targeted-cast handler: no 0xA2 for the slot of the running
    /// SpellCaster).
    /// </summary>
    [Check("spells.windup-cast-refuses-a-second-request-for-its-slot")]
    public static void WindupRefusesASecondRequestForItsSlot()
    {
        using var match = new Match("Malphite");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard is cast");
        match.Advance(Step);
        var second = match.CanCast(0, match.Foe);
        TestAssert.False(second.Allowed, $"a second Seismic Shard inside the windup is refused ({second.Reason})");
        TestAssert.True(second.Failure == SpellCastFailure.Cooldown, $"as a slot that is not ready ({second.Failure})");
        match.Advance(1.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "one shard");
    }

    /// <summary>
    /// Security review of batch 340: the mana a windup spends at its cast frame is held from the press, so an instacast pressed inside it
    /// cannot spend the same mana (Seismic Shard 70, then Brutal Strikes 50, with 100 mana).
    /// </summary>
    [Check("spells.two-quick-casts-cannot-spend-more-mana-than-the-hero-has")]
    public static void TwoQuickCastsCannotSpendMoreThanTheHeroHas()
    {
        using var match = new Match("Malphite");
        match.Learn(1);
        match.Refill();
        match.Hero.Mana = 100.0;
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard (70) is cast with 100 mana");
        var brutal = match.CanCast(1, match.Hero);
        TestAssert.False(brutal.Allowed, $"Brutal Strikes (50) inside the windup is refused: 70 of the 100 are held ({brutal.Reason})");
        TestAssert.True(brutal.Failure == SpellCastFailure.Mana, $"for mana ({brutal.Failure})");
        match.Cast(1, match.Hero);
        match.Advance(CastTime("SeismicShard") + 0.2);
        TestAssert.True(match.Hero.Mana >= 30.0 - 0.5, $"only the shard's 70 is spent ({match.Hero.Mana:0.#} left)");
        TestAssert.True(Buff(match.Hero, "Obduracy") is null && Buff(match.Hero, "ObduracyBuff") is null, "and Brutal Strikes did not run");
    }

    [Check("spells.windup-channel-pays-at-its-start-frame")]
    public static void WindupChannelPaysAtItsStartFrame()
    {
        using var match = new Match("Nunu");
        match.Learn(1);
        match.Refill();
        var mana = match.Hero.Mana;
        var castTime = CastTime("AbsoluteZero");
        TestAssert.True(castTime > 0.1, $"Absolute Zero has a windup ({castTime:0.###} s)");
        TestAssert.True(match.Cast(3, match.Hero), "Absolute Zero is cast");
        TestAssert.Near(mana, match.Hero.Mana, 0.5, "the press spends nothing");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 1e-9, "and starts no cooldown");
        var peak = 0.0;
        for (var elapsed = 0.0; elapsed < castTime + 0.2; elapsed += Step)
        {
            match.Advance(Step);
            peak = Math.Max(peak, match.Hero.SpellCooldowns[3]);
        }
        TestAssert.Near(mana - 150.0, match.Hero.Mana, 1.0, "the channel's start frame spends the 150 mana");
        TestAssert.Near(150.0, peak, Step + 1e-6, "and starts the 150 s cooldown");
    }
}
