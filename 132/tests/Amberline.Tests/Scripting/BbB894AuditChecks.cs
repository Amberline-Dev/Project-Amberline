using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 894 (18 November 2026, owner: "deeply double-check all the champions that were added since 126"): pins of the real-play cases the audit of
/// Sejuani, Jax and Sivir (1.0.0.132) found working, so the shipped behaviour stays as measured. Record:
/// 132/docs/maintenance/2026-11-18-batch-894-audit-sejuani-jax-sivir.md.
/// </summary>
public static class BbB894AuditChecks
{
    private static void Attack(Match match, Entity target)
    {
        // The order names a unit the player's client sees (the dispatcher's check).
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(target.NetId);
        view.Visible.Add(target.NetId);
        var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 3, target.Position, target.NetId);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "the attack order");
    }

    /// <summary>
    /// JaxPassive's BuffOnHitUnit counts the hits in JaxRelentlessAssaultDebuff and adds JaxRelentlessAttack (the override attack) on the second;
    /// the count it compares is the one read before the adds, and the third hit's own BuffOnHitUnit must still see JaxRelentlessAttack (its damage
    /// runs inside the override's TargetExecute, before that removes the buff). Read any other way the cycle drifts to every second hit.
    /// </summary>
    [Check("scripting.b894-jax-grandmasters-might-magic-lands-on-every-third-hit-of-a-chain")]
    public static void JaxMagicHitEveryThirdHit()
    {
        using var match = new Match("Jax", foeChampion: "Ezreal");
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(match.Hero.Position.X + 120, match.Hero.Position.Z);
        match.Foe.MaxHealth = match.Foe.Health = 50000;
        Attack(match, match.Foe);
        for (var step = 0; step < 400 && HitsOn(match, "Foe").Count(hit => hit.Kind == "Basic") < 9; step++) match.Advance(Step);
        var hits = HitsOn(match, "Foe");
        var basics = hits.Where(hit => hit.Kind == "Basic").Select(hit => hit.At).ToList();
        TestAssert.True(basics.Count >= 9, $"nine swings land (saw {basics.Count})");
        var magic = hits.Where(hit => hit.Kind == "Spell").ToList();
        TestAssert.Equal(3, magic.Count, "three magic hits in nine swings: " + string.Join(" | ", hits.Select(hit => $"{hit.At:0.00} {hit.Kind}")));
        for (var k = 0; k < 3; k++)
            TestAssert.Near(basics[3 * k + 2], magic[k].At, 1e-6, $"magic hit {k + 1} rides swing {3 * k + 3}");
        TestAssert.Near(100.0, magic[0].Raw, 0.01, "JaxRelentlessAttack DamageByLevel 100 at rank one, no ability power");
        TestAssert.Equal(6, Buff(match.Hero, "JaxRelentlessAssaultAS")?.Stacks ?? 0, "the attack-speed stacks stop at UltStacks 6");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// SejuaniArcticAssault: the dash toward a champion stops at it (BuffOnCollision / BuffOnUpdateActions: BBStopMoveBlock), the end hook
    /// (BuffOnMoveEnd) deals the damage around her and casts SejuaniFrostApply, the slots sealed for the dash (E, R) open again, and she is ordered
    /// to attack the champion.
    /// </summary>
    [Check("scripting.b894-sejuani-arctic-assault-stops-at-the-first-champion-and-hits-on-landing")]
    public static void SejuaniQStopsAtTheFirstChampion()
    {
        using var match = new Match("Sejuani", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(match.Hero.Position.X + 500, match.Hero.Position.Z);
        match.Foe.SpellBlock = 0.0;
        match.Ally.Position = new Position2(5000, 5000);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Arctic Assault is accepted");
        match.Advance(0.2);
        TestAssert.False(match.CanCast(2).Allowed, "E is sealed while the dash runs");
        match.Advance(0.8);
        TestAssert.True(match.Hero.ForcedMove is null, "the dash is over");
        TestAssert.True(match.Hero.Position.X < match.Foe.Position.X - 100.0, $"Sejuani stops at the champion (at {match.Hero.Position.X:0}, foe at {match.Foe.Position.X:0})");
        var spells = HitsOn(match, "Foe").Where(hit => hit.Kind == "SpellAoe").ToList();
        TestAssert.Equal(1, spells.Count, "one Arctic Assault hit");
        TestAssert.Near(60.0, spells[0].Raw, 0.01, "60 magic at rank one (DamageToDeal)");
        TestAssert.True(Buff(match.Foe, "SejuaniFrost") is not null, "the landing frosts the champion");
        TestAssert.Equal((uint?)match.Foe.NetId, match.Hero.AttackTarget, "the script orders her to attack it");
        TestAssert.Near(70.0, mana - match.Hero.Mana, 2.0, "70 mana (ManaCost1)");
        TestAssert.Near(19.0 - 1.0, match.Hero.SpellCooldowns[0], 0.1, "19 s cooldown (Cooldown1)");
        TestAssert.True(match.CanCast(3).Allowed, $"R is not left sealed ({match.CanCast(3).Reason})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// SejuaniFrostApply on a target under Permafrost (SejuaniWintersClaw) adds SejuaniFrostTracker instead of Frost; when Permafrost ends, its
    /// OnBuffDeactivate turns the tracker's time left back into SejuaniFrost. Both teams' copies (the Chaos scripts) behave the same.
    /// </summary>
    [Check("scripting.b894-sejuani-frost-comes-back-after-permafrost-on-both-teams")]
    public static void SejuaniFrostReturnsAfterPermafrost()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var suffix = team == Team.Order ? "" : "Chaos";
            using var match = new Match("Sejuani", foeChampion: "Ezreal", team: team);
            match.Learn(1);
            match.Refill();
            match.Foe.Position = new Position2(match.Hero.Position.X + 120, match.Hero.Position.Z);
            match.Foe.MaxHealth = match.Foe.Health = 50000;
            match.Hero.AttackCooldown = 0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), $"{team}: the first attack");
            match.Advance(0.6);
            TestAssert.True(Buff(match.Foe, "SejuaniFrost" + suffix) is not null, $"{team}: the attack frosts ({Names(match.Foe)})");
            TestAssert.True(match.Cast(2), $"{team}: Permafrost is accepted on a frosted champion");
            match.Advance(0.5);
            TestAssert.True(Buff(match.Foe, "SejuaniWintersClaw" + suffix) is not null && Buff(match.Foe, "SejuaniFrost" + suffix) is null,
                $"{team}: Permafrost replaces Frost ({Names(match.Foe)})");
            match.Hero.AttackCooldown = 0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), $"{team}: an attack during Permafrost");
            match.Advance(0.6);
            TestAssert.True(Buff(match.Foe, "SejuaniFrostTracker") is not null, $"{team}: the tracker holds the new Frost ({Names(match.Foe)})");
            for (var step = 0; step < 150 && Buff(match.Foe, "SejuaniWintersClaw" + suffix) is not null; step++) match.Advance(Step);
            match.Advance(Step);
            TestAssert.True(Buff(match.Foe, "SejuaniFrost" + suffix) is not null, $"{team}: Frost comes back when Permafrost ends ({Names(match.Foe)})");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{team}: no script failed");
        }
    }
}
