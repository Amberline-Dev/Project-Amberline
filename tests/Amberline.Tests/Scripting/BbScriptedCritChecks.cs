using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Combat;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 301: a script's critical <c>HitResult</c>. <c>BBApplyDamage</c> reads the calling script's own
/// <c>HitResult</c> variable (the integer getter looks the name up in the script's variables first) into the
/// damage record next to <c>IgnoreDamageCrit</c> (the damage effect). An attack hit whose result the script left
/// critical, and that does not ignore critical strikes, is multiplied by the attacker's critical multiplier and reaches the client as
/// a critical (0x68 result type 3): Pantheon's Certain Death (<c>PantheonBasicAttack</c>) and Gangplank's Parley (<c>Parley</c>, which rolls
/// its own critical strike).
/// </summary>
public static class BbScriptedCritChecks
{
    /// <summary>The 0x68 result types (DamageResultType, seven bits) the client received for hits on <paramref name="target"/>.</summary>
    private static List<int> DamageResults(WireCapture wire, Entity target) => wire.Packets
        .Select(entry => entry.Packet.Plaintext.Span.ToArray())
        .Where(bytes => bytes.Length >= 18 && bytes[0] == 0x68 && BitConverter.ToUInt32(bytes, 6) == target.NetId)
        .Select(bytes => bytes[5] & 0x7f).ToList();

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Pantheon (every spell at rank one, so Certain Death is on him) with Ezreal 150 in front of him at a fraction of his health.</summary>
    private static Match CertainDeath(double healthFraction)
    {
        var match = new Match("Pantheon", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Ally.Position = new Position2(6500, 6500);
        match.Advance(0.5); // his character script adds Pantheon_CertainDeath on its first update
        match.Foe.Health = healthFraction * match.Foe.MaxHealth;
        return match;
    }

    [Check("scripting.bb-scripted-crit-certain-death-swing-is-critical-at-fifteen-percent")]
    public static void CertainDeathSwingIsCritical()
    {
        using var match = CertainDeath(0.14);
        TestAssert.True(Buff(match.Hero, "Pantheon_CertainDeath") is not null, "Certain Death is on him");
        var wire = Observe(match, Team.Order, match.Hero);
        // The swing itself is rolled normal (Crit 1.0): only the script makes it critical.
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe, new AttackRolls(1.0, 1.0, 1.0)), "he swings at the foe");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit: the script's DAMAGESOURCE_DEFAULT is the attack's own hit (" +
            string.Join(", ", hits.Select(hit => $"{hit.Kind} {hit.Raw:0.##}")) + ")");
        TestAssert.Equal("Basic", hits[0].Kind, "the hit is the basic attack");
        TestAssert.Near(53.6 * match.Hero.CritMultiplier, hits[0].Raw, 0.05,
            "at fifteen percent or less the swing is a critical strike: 53.6 x 2 (PantheonBasicAttack sets HitResult = HIT_Critical)");
        TestAssert.Equal("3", string.Join(",", DamageResults(wire, match.Foe)), "the client shows it as a critical (0x68 result type 3)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-scripted-crit-certain-death-swing-stays-normal-above-fifteen-percent")]
    public static void CertainDeathLeavesHealthyTargetsNormal()
    {
        using var match = CertainDeath(0.16);
        var wire = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe, new AttackRolls(1.0, 1.0, 1.0)), "he swings at the foe");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit, the basic attack");
        TestAssert.Near(53.6, hits[0].Raw, 0.05, "above fifteen percent the swing is the base attack damage");
        TestAssert.Equal("4", string.Join(",", DamageResults(wire, match.Foe)), "an ordinary hit (0x68 result type 4)");
    }

    /// <summary>
    /// Runs the shipped <c>Parley</c> <c>TargetExecute</c> through the production router as a rank-one cast of the hero at the foe, with the
    /// hero's critical chance set so the script's own roll (<c>CO_RANDOM_CHANCE_LESS_THAN</c> of <c>GetFlatCritChanceMod</c>) always or never passes.
    /// Returns the hit on the foe, the 0x68 result types, and the expected uncritical amount (20 plus the base attack damage).
    /// </summary>
    private static ((double Raw, string Kind) Hit, List<int> Results, double Plain, double Multiplier) Parley(double critChance)
    {
        using var match = new Match("Soraka");
        match.Learn(1);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Hero.CritChance = critChance;
        var at = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
        match.Module.Router!.RunTargetExecute(Script("Parley"), match.Hero, 0, 1, 0.0, at, at, match.Foe);
        match.Advance(0.1);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "Parley hits the foe once");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        return ((hits[0].Raw, hits[0].Kind), DamageResults(wire, match.Foe), 20.0 + match.Hero.AttackDamage, match.Hero.CritMultiplier);
    }

    [Check("scripting.bb-scripted-crit-parley-critical-roll-doubles-its-attack-hit")]
    public static void ParleyCriticalRollDoublesItsHit()
    {
        var critical = Parley(1.0);
        TestAssert.Equal("Basic", critical.Hit.Kind, "Parley deals DAMAGESOURCE_ATTACK");
        TestAssert.Near(critical.Plain * critical.Multiplier, critical.Hit.Raw, 0.05,
            "its own roll sets HitResult = HIT_Critical and IgnoreDamageCrit is false: (20 + attack damage) x 2");
        TestAssert.Equal("3", string.Join(",", critical.Results), "a critical on the wire (0x68 result type 3)");

        var normal = Parley(0.0);
        TestAssert.Near(normal.Plain, normal.Hit.Raw, 0.05, "a failed roll (HIT_Normal) deals 20 + attack damage");
        TestAssert.Equal("4", string.Join(",", normal.Results), "an ordinary hit on the wire (0x68 result type 4)");
    }
}
