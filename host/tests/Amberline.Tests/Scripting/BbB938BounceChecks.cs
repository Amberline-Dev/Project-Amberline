using Amberline.Core;
using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 938 (community reports, 22 November 2026: Ryze's Spell Flux "hits only 2 enemies in most cases ... bounces to a 3rd unit but
/// doesn't deal damage"; Katarina's Bouncing Blades "bounces after the 1st don't deal damage"). The case table of the chain missile
/// (<c>BbSpellProjectile</c> with <c>BbChainParameters</c>): for each layout the units the server hits in order, and whether the client's
/// missile, which follows the list it was sent, is told the chain ended before that list did (a destroy). Ryze stands at (7000, 7000), 400 from
/// the first unit, outside Spell Flux's 375 unless a row says otherwise.
/// </summary>
public static class BbB938BounceChecks
{
    private const double Big = 2000.0;

    private sealed record Row(string Label, (string Name, double X, double Z, double Hp)[] Units, string Hits, int Destroyed = 0,
        int Rank = 5, Action<Match, Dictionary<string, Entity>>? AfterCast = null);

    private static (string Hits, int Destroyed, List<string> Syncs) Run(string champion, int slot, Row row)
    {
        using var match = new Match(champion);
        match.Learn(row.Rank);
        match.Foe.Position = new Position2(11000, 11000);
        var units = new Dictionary<string, Entity>();
        foreach (var (name, x, z, hp) in row.Units)
        {
            var unit = name == "Foe" ? match.Foe : Enemy(match, name, new Position2(x, z));
            unit.Position = new Position2(x, z);
            unit.Health = unit.MaxHealth = hp;
            units[name] = unit;
        }
        match.Advance(0.2);
        match.Refill();
        TestAssert.True(match.Cast(slot, units[row.Units[0].Name]), $"{row.Label}: cast");
        match.Advance(Step);
        row.AfterCast?.Invoke(match, units);
        match.Advance(3.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{row.Label}: no script failed");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, $"{row.Label}: the chain ended");
        string Name(uint id) => id == 0 ? "-" : id == match.Hero.NetId ? champion
            : units.FirstOrDefault(pair => pair.Value.NetId == id).Key ?? id.ToString("x");
        var hits = match.Log.Hits.Select(line => line.Split(' ').First(part => part.Contains("->", StringComparison.Ordinal)))
            .Select(token => token[(token.IndexOf("->", StringComparison.Ordinal) + 2)..]);
        return (string.Join(",", hits), match.Log.DestroyedMissiles.Count,
            match.Log.ChainSyncs.Select(sync => string.Join(",", sync.Targets.Select(Name))).ToList());
    }

    private static void Table(string champion, int slot, IEnumerable<Row> rows)
    {
        var failures = new List<string>();
        foreach (var row in rows)
        {
            var (hits, destroyed, syncs) = Run(champion, slot, row);
            if (hits != row.Hits || destroyed != row.Destroyed)
                failures.Add($"{row.Label}: hits {hits} (expected {row.Hits}), destroys {destroyed} (expected {row.Destroyed}), lists [{string.Join(" | ", syncs.Distinct())}]");
        }
        TestAssert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static readonly (string, double, double, double)[] Cluster4 =
        [("A", 7400, 7000, Big), ("B", 7500, 7000, Big), ("C", 7400, 7100, Big), ("D", 7500, 7100, Big)];

    private static void Kill(Match match, Entity unit) => TestAssert.True(match.Runtime.Combat.Kill(unit, match.Hero.NetId), "killed");

    /// <summary>
    /// Bouncing Blades (CanHitSameTarget 0, CanHitCaster 0, MaximumHits 2..6 by rank, BounceRadius 400): every unit at most once, the nearest from
    /// the unit just hit. A unit the dagger's own hit kills is a normal hop (it was alive when the dagger arrived): before the batch the first such
    /// kill used up the chain's one corpse bounce and the second ended the chain on the server while the client flew on down its list ("bounces after
    /// the 1st don't deal damage"). A chain the server ends while the client's list still names hops destroys the client's missile.
    /// </summary>
    [Check("scripting.b938-katarina-q-case-table")]
    public static void KatarinaBouncingBladesCaseTable() => Table("Katarina", 0,
    [
        new("2 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big)], "A,B"),
        new("3 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big)], "A,B,C"),
        new("4 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big), ("D", 8300, 7000, Big)], "A,B,C,D"),
        // Reconstruction kept: the radius is measured from the unit just hit, so two units 350 either side of the first (495 apart) give 2 hits.
        new("3 spread 350 around the first", [("A", 7400, 7000, Big), ("B", 7400, 7350, Big), ("C", 7400, 6650, Big)], "A,B"),
        new("5 clustered", [("A", 7400, 7000, Big), ("B", 7500, 7000, Big), ("C", 7400, 7100, Big), ("D", 7500, 7100, Big), ("E", 7450, 6900, Big)],
            "A,B,D,C,E"),
        new("6 clustered", [("A", 7400, 7000, Big), ("B", 7500, 7000, Big), ("C", 7400, 7100, Big), ("D", 7500, 7100, Big), ("E", 7450, 6900, Big),
            ("F", 7350, 6900, Big)], "A,B,D,C,E,F"),
        new("a champion first, two minions", [("Foe", 7400, 7000, Big), ("B", 7650, 7000, Big), ("C", 7400, 7250, Big)], "Foe,B,C"),
        new("3 in a line, each dies to its hit", [("A", 7400, 7000, 1), ("B", 7700, 7000, 1), ("C", 8000, 7000, 1)], "A,B,C"),
        new("4 clustered, each dies to its hit", [("A", 7400, 7000, 1), ("B", 7500, 7000, 1), ("C", 7400, 7100, 1), ("D", 7500, 7100, 1)], "A,B,D,C"),
        new("3 clustered, the second dies to its hit", [("A", 7400, 7000, Big), ("B", 7500, 7000, 1), ("C", 7400, 7300, Big)], "A,B,C"),
        new("2, the second dies before the dagger leaves", [("A", 7400, 7000, Big), ("B", 7650, 7000, Big)], "A", Destroyed: 1,
            AfterCast: (match, units) => Kill(match, units["B"])),
        // The replanned rest keeps the rules: C (390 from A) takes B's place once, not B's place and its own (C twice).
        new("3, the second dies before the dagger leaves", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 7650, 7300, Big)], "A,C",
            Destroyed: 1, AfterCast: (match, units) => Kill(match, units["B"])),
        new("3 in a line, the second walks 500 away", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big)], "A,B,C",
            AfterCast: (_, units) => units["B"].Position = new Position2(7700, 7500)),
        new("2, 395 apart", [("A", 7400, 7000, Big), ("B", 7795, 7000, Big)], "A,B"),
        new("2, 420 apart", [("A", 7400, 7000, Big), ("B", 7820, 7000, Big)], "A"),
        new("rank 1, 4 clustered", Cluster4, "A,B", Rank: 1),
    ]);

    /// <summary>
    /// Spell Flux (CanHitSameTarget 1, not consecutively, CanHitCaster 1, five hits, BounceRadius 375). Reconstruction (batch 938): the next hop
    /// is the nearest unit the orb has not hit yet, then Ryze, then the nearest it has hit. Before the batch it was the nearest allowed unit, which
    /// with CanHitSameTarget is almost always the unit it just came from, so every layout gave A,B,A,B,A ("hits only 2 enemies in most cases").
    /// </summary>
    [Check("scripting.b938-ryze-e-case-table")]
    public static void RyzeSpellFluxCaseTable() => Table("Ryze", 2,
    [
        new("2 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big)], "A,B,A,B,A"),
        new("3 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big)], "A,B,C,B,A"),
        new("4 in a line", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big), ("D", 8300, 7000, Big)], "A,B,C,D,C"),
        new("3 spread 350 around the first", [("A", 7400, 7000, Big), ("B", 7400, 7350, Big), ("C", 7400, 6650, Big)], "A,B,A,C,A"),
        new("5 clustered", [("A", 7400, 7000, Big), ("B", 7500, 7000, Big), ("C", 7400, 7100, Big), ("D", 7500, 7100, Big), ("E", 7450, 6900, Big)],
            "A,B,D,C,E"),
        new("a champion first, two minions", [("Foe", 7400, 7000, Big), ("B", 7650, 7000, Big), ("C", 7400, 7250, Big)], "Foe,B,C,Foe,B"),
        new("3 in a line, each dies to its hit", [("A", 7400, 7000, 1), ("B", 7700, 7000, 1), ("C", 8000, 7000, 1)], "A,B,C", Destroyed: 1),
        new("4 clustered, each dies to its hit", [("A", 7400, 7000, 1), ("B", 7500, 7000, 1), ("C", 7400, 7100, 1), ("D", 7500, 7100, 1)], "A,B,D,C",
            Destroyed: 1),
        new("3 clustered, the second dies to its hit", [("A", 7400, 7000, Big), ("B", 7500, 7000, 1), ("C", 7400, 7300, Big)], "A,B,C,A,C"),
        new("2, the second dies before the orb leaves", [("A", 7400, 7000, Big), ("B", 7650, 7000, Big)], "A", Destroyed: 1,
            AfterCast: (match, units) => Kill(match, units["B"])),
        new("3 in a line, the second walks 500 away", [("A", 7400, 7000, Big), ("B", 7700, 7000, Big), ("C", 8000, 7000, Big)], "A,B,C,B,A",
            AfterCast: (_, units) => units["B"].Position = new Position2(7700, 7500)),
        new("2, 370 apart", [("A", 7400, 7000, Big), ("B", 7770, 7000, Big)], "A,B,A,B,A"),
        new("2, 380 apart", [("A", 7400, 7000, Big), ("B", 7780, 7000, Big)], "A"),
        new("rank 1, 4 clustered", Cluster4, "A,B,D,C,A", Rank: 1),
        // Ryze 300 from the first unit: he takes a hop once no new enemy is in reach, before an enemy is hit again (V1.0.0.139 was the patch
        // that put enemy champions before him), and his own hop deals nothing (TargetExecute skips the Owner).
        new("2, Ryze 300 from the first", [("A", 7300, 7000, Big), ("B", 7500, 7000, Big)], "A,B,A,A"),
    ]);

    /// <summary>
    /// The two timed copies of the hop list (<c>BbChainSync</c>, one windup plus 0.05 and 0.15 s after the cast) send the list the flight follows
    /// now, not the cast's: a first unit 120 from Katarina is reached 0.086 s after the windup, where a dead planned hop is replanned, and the
    /// second copy used to send the cast's list (naming the dead unit) after the replanned one.
    /// </summary>
    [Check("scripting.b938-chain-sync-copies-carry-the-replanned-list")]
    public static void ChainSyncCopiesCarryTheReplannedList()
    {
        var row = new Row("close first unit", [("A", 7120, 7000, Big), ("B", 7400, 7000, Big), ("C", 7300, 7250, Big)], "A,C", Destroyed: 1,
            AfterCast: (match, units) => Kill(match, units["B"]));
        var (hits, destroyed, syncs) = Run("Katarina", 0, row);
        TestAssert.Equal("A,C", hits, "the dagger hits A, then C in B's place");
        TestAssert.Equal(1, destroyed, "and the client's missile ends where the server's chain does");
        TestAssert.Equal("A,B,C", syncs[0], "the first copy carries the cast's list");
        TestAssert.True(syncs.Count >= 3 && syncs.Skip(1).All(list => !list.Split(',').Contains("B")),
            "every list after the replan names C, not the dead B: " + string.Join(" | ", syncs));
    }

    /// <summary>
    /// The early end reaches the client that got the cast. A chain missile is announced by its cast answer, and its usual retirement
    /// (<c>RetireCastMissile</c>) reaches only late copies. The destroy of a cut chain must reach the caster's own client, whose missile would
    /// otherwise fly on to the dead unit.
    /// </summary>
    [Check("scripting.b938-an-early-chain-end-reaches-the-casters-client-as-a-destroy")]
    public static void EarlyChainEndReachesTheCastersClient()
    {
        using var match = new Match("Katarina");
        match.Learn(5);
        match.Foe.Position = new Position2(11000, 11000);
        var a = Enemy(match, "A", new Position2(7400, 7000));
        var b = Enemy(match, "B", new Position2(7650, 7000));
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        match.Refill();
        TestAssert.True(match.Cast(0, a), "Q cast");
        var missile = match.Log.Casts[^1].MissileNetId;
        match.Advance(Step);
        Kill(match, b);
        match.Advance(2.0);
        var destroy = Amberline.Network.Protocol.SpellPackets.DestroyMissile(missile).Plaintext.ToArray();
        TestAssert.True(wire.Packets.Any(item => item.Packet.Plaintext.Span.SequenceEqual(destroy)),
            "the caster's client is told its missile is over");
        TestAssert.Equal(1, HitsOn(match, "A").Count, "A was hit");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "the chain ended");
    }

    /// <summary>
    /// Sivir's Ricochet orb (<c>BbChainAttack</c>, the same replan and cut). The swing plans Foe, M1, M2. M1 dies before the orb starts toward it,
    /// and M2 (390 from the Foe) is in reach. The replan takes M2 once in M1's place; before the batch it also stayed the next hop, so M2 was hit
    /// twice. Then nothing is left, so the orb ends early and the client's missile is destroyed.
    /// </summary>
    [Check("scripting.b938-sivir-ricochet-replan-keeps-the-rules-and-cuts-the-client")]
    public static void RicochetReplanKeepsTheRulesAndCuts()
    {
        using var match = new Match("Sivir");
        match.Learn(3);
        var m1 = Enemy(match, "M1", new Position2(7550, 7000));
        Enemy(match, "M2", new Position2(7550, 7300));
        match.Refill();
        TestAssert.True(match.Cast(1), "W toggles on");
        match.Hero.Mana = match.Hero.MaxMana;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
        var launched = false;
        for (var frame = 0; frame < 60 && !launched; frame++)
        {
            match.Advance(Step);
            launched = match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == match.Hero.NetId && hit.WindupRemaining <= 0.0 && hit.MissileNetId != 0);
        }
        TestAssert.True(launched, "the swing's missile has left");
        Kill(match, m1);
        match.Advance(1.8);
        TestAssert.Equal(0, HitsOn(match, "M1").Count, "the dead M1 takes nothing");
        TestAssert.Equal(1, HitsOn(match, "M2").Count, "M2 once, in M1's place");
        TestAssert.Equal(1, match.Log.DestroyedMissiles.Count, "the orb ended before the client's list did: its missile is destroyed");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "the orb is gone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
