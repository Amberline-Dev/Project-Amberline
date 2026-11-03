using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 359 (owner findings 7, 59 and 80): a unit a script spawns replicates the state its script gave it. The stat block a client receives
/// carries Invulnerable, MagicImmune and Targetable and the Attack, Cast and Move bits of ActionState, from the spawn call's flags and from the
/// statuses the script sets later. Before the fix the block carried protocol defaults, so the client saw a trap, an axe or a sapling as
/// targetable, mortal and free to move. A 0xCC block is: opcode, a zero (4 bytes), the replication prefix (sync id, 1, map byte, unit netId at
/// offset 11, property mask at 15), then one 4-byte value per set mask bit in ascending order from offset 19. Map 1 bit 0 is HP, bit 4
/// ActionState, bit 5 MagicImmune, bit 6 Invulnerable and bit 7 Targetable.
/// <para>
/// Batch 546 (owner findings 59, 80 and 109): the bar. The stock client's minion bar draw (its bar-draw slot)
/// returns before drawing when AttackableUnit slot 4 fails, and that slot reads the replicated IsTargetable flag.
/// The axe, the bird and the sapling carry SetTargetable false from their own buffs, so each client, the owner's and the
/// enemy's, must hold Targetable 0 for them.
/// </para>
/// </summary>
public static class BbSpawnedUnitStateChecks
{
    /// <summary>The owner's client and an enemy client of <paramref name="match"/> (the Chaos foe's).</summary>
    private static (BbM3.WireCapture Own, BbM3.WireCapture Enemy) BothTeams(Match match) =>
        (Observe(match, Team.Order, match.Hero), Observe(match, Team.Chaos, match.Foe, id: 9002));

    /// <summary>Both clients hold the unit untargetable, so neither draws its bar.</summary>
    private static void DrawsNoBar((BbM3.WireCapture Own, BbM3.WireCapture Enemy) wire, Entity unit, string why)
    {
        TestAssert.Equal(0u, Latest(Blocks(wire.Own, unit.NetId), 7), $"the owner's client holds the {unit.Name} untargetable ({why}), so it draws no bar");
        TestAssert.Equal(0u, Latest(Blocks(wire.Enemy, unit.NetId), 7), $"the enemy's client holds the {unit.Name} untargetable ({why}), so it draws no bar");
    }

    // Attack (bit 0) and Move (bit 2) of the ActionState word.
    private const uint AttackBit = 1u << 0, MoveBit = 1u << 2;

    /// <summary>Every stat block of one unit that the client received, in order.</summary>
    private static byte[][] Blocks(BbM3.WireCapture wire, uint netId) =>
        wire.Packets.Select(item => item.Packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length >= 19 && bytes[0] == 0xCC && BitConverter.ToUInt32(bytes, 11) == netId).ToArray();

    /// <summary>
    /// The value the client holds for a map 1 property: the last block that carries it (a narrow delta carries only its own properties).
    /// uint.MaxValue when no block carries it, so an assertion on a flag the client was never sent fails.
    /// </summary>
    private static uint Latest(byte[][] blocks, int bit)
    {
        var value = uint.MaxValue;
        foreach (var block in blocks)
        {
            var mask = BitConverter.ToUInt32(block, 15);
            if ((mask & (1u << bit)) != 0)
                value = BitConverter.ToUInt32(block, 19 + 4 * System.Numerics.BitOperations.PopCount(mask & ((1u << bit) - 1)));
        }
        return value;
    }

    [Check("scripting.bb-spawned-caitlyn-trap-replicates-its-script-state")]
    public static void CaitlynTrapReplicatesItsScriptState()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        var wire = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "W places the trap");
        match.Advance(1.0);
        match.Runtime.Delivery.Flush();
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Alive && unit.Name == "CaitlynTrap");
        var block = Blocks(wire, trap.NetId);
        TestAssert.Equal(1u, Latest(block,6), "the trap is invulnerable (the call's Invulnerable flag)");
        TestAssert.Equal(1u, Latest(block,5), "the trap is magic immune (the call's MagicImmune flag)");
        TestAssert.Equal(0u, Latest(block,7), "the trap is untargetable (its script's SetTargetable false)");
        TestAssert.Equal(0u, Latest(block,4) & MoveBit, "the trap cannot move (the call's Rooted flag)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-spawned-olaf-axe-replicates-its-script-state")]
    public static void OlafAxeReplicatesItsScriptState()
    {
        using var match = new Match("Olaf");
        match.Learn(1);
        match.Refill();
        var both = BothTeams(match);
        var wire = both.Own;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q throws the axe");
        match.Advance(0.5);
        match.Runtime.Delivery.Flush();
        var axe = match.Runtime.World.Entities.Values.Single(unit => unit.Alive && unit.Name == "OlafAxe");
        var block = Blocks(wire, axe.NetId);
        TestAssert.Equal(1u, Latest(block,6), "the axe is invulnerable (the call's Invulnerable flag)");
        TestAssert.Equal(1u, Latest(block,5), "the axe is magic immune (the call's MagicImmune flag)");
        TestAssert.Equal(0u, Latest(block,4) & MoveBit, "the axe is rooted (the call's Rooted flag)");
        DrawsNoBar(both, axe, "OlafAxeExpirationTimer's SetTargetable false");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-spawned-maokai-sproutling-replicates-its-script-state")]
    public static void MaokaiSproutlingReplicatesItsScriptState()
    {
        using var match = new Match("Maokai");
        match.Learn(1);
        match.Refill();
        var both = BothTeams(match);
        var wire = both.Own;
        TestAssert.True(match.Cast(2, at: new Position2(match.Hero.Position.X + 200.0, match.Hero.Position.Z)), "E plants the sapling");
        // The placemarker's burst leaves the sproutling about 1.5 seconds after the cast; it springs at a foe later and dies, so its block is read
        // as soon as it appears.
        var deadline = match.Now + 4.0;
        Entity? sproutling = null;
        while (match.Now < deadline && sproutling is null)
        {
            match.Advance(BbM3.Step);
            sproutling = match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "MaokaiSproutling");
        }
        TestAssert.True(sproutling is not null, "the sapling lands as a sproutling");
        match.Runtime.Delivery.Flush();
        var block = Blocks(wire, sproutling!.NetId);
        // The landing's script sets SetCanMove false and SetCanAttack false on the sproutling (Other1).
        TestAssert.Equal(0u, Latest(block,4) & MoveBit, "the sproutling cannot move (its script's SetCanMove false)");
        TestAssert.Equal(0u, Latest(block,4) & AttackBit, "the sproutling cannot attack (its script's SetCanAttack false)");
        DrawsNoBar(both, sproutling, "MaokaiSaplingMine's SetTargetable false");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-spawned-swain-raven-is-untargetable-to-both-clients")]
    public static void SwainRavenIsUntargetableToBothClients()
    {
        using var match = new Match("Swain");
        match.Learn(1);
        match.Refill();
        var both = BothTeams(match);
        // Decrepify's TargetExecute spawns the raven (BBSpawnMinion skin SwainBeam) and gives it SwainBeam (a hero target) or SwainBeamMinion,
        // whose OnBuffActivate sets SetTargetable false on it.
        TestAssert.True(match.Cast(0, match.Foe), "Decrepify is accepted on the foe");
        match.Advance(0.5);
        match.Runtime.Delivery.Flush();
        var raven = match.Runtime.World.Entities.Values.Single(unit => unit.Alive && unit.Name == "SwainBeam");
        DrawsNoBar(both, raven, "SwainBeam's SetTargetable false");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
