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

    /// <summary>
    /// Batch 732 (owner report, 9 November 2026: "Wukong clone has a healthbar - it shouldnt"). The decoy clone is an obj_AI_Minion on the
    /// client (a pet's 0x3C creates one). Its shipped OnBuffActivate calls <c>BBShowHealthBar(Owner, Show=true)</c> and
    /// <c>BBSetNotTargetableToTeam(Owner, ToAlly=true, ToEnemy=false)</c>. The minion bar gate draws the bar unless the unit is
    /// untargetable (IsTargetable 0) or untargetable to both teams (IsTargetableToTeam 0x1800000). So retail draws the decoy's bar for both
    /// teams, and that is what each client is told: IsTargetable 1 and IsTargetableToTeam 0x800000 (the owner's team cannot target it, the
    /// enemy can), and the health bar shown. Nothing here hides it. Batch 734 corrects the rest of batch 732's reading: past this gate the
    /// minion draw takes the champion bar for a clone of a champion (<see cref="CreatedAsACopyOf"/>), and that bar does read 0xD7's hide
    /// flag; "0xD7 is never read for a minion" (batch 495) holds only for a minion that copies no champion.
    /// </summary>
    [Check("scripting.bb-spawned-monkeyking-decoy-bar-is-drawn-by-both-teams")]
    public static void MonkeyKingDecoyBarIsDrawnByBothTeams()
    {
        using var match = new Match("MonkeyKing", foeChampion: "Orianna");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Refill();
        var both = BothTeams(match);
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.2);
        match.Runtime.Delivery.Flush();
        var clone = match.Runtime.World.Entities.Values.Single(unit => unit.Alive && unit.Kind == EntityKind.ClonePet);
        foreach (var (who, wire) in new[] { ("owner's", both.Own), ("enemy's", both.Enemy) })
        {
            var block = Blocks(wire, clone.NetId);
            TestAssert.Equal(1u, Latest(block, 7), $"the {who} client holds the decoy targetable (IsTargetable), so the bar gate is open");
            TestAssert.Equal(0x00800000u, Latest(block, 8), $"the {who} client holds it untargetable to its own team only (IsTargetableToTeam 0x800000)");
            var shown = wire.Packets.Select(item => item.Packet.Plaintext.ToArray())
                .Where(bytes => bytes.Length == 6 && bytes[0] == 0xD7 && BitConverter.ToUInt32(bytes, 1) == clone.NetId).ToArray();
            TestAssert.True(shown.Length > 0 && shown[^1][5] == 1, $"the {who} client is told the bar is shown (0xD7 show 1, BBShowHealthBar Show=true)");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 734: a clone of a champion draws the champion bar
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The champion's pools when its clone appeared: the client copies these into the clone.</summary>
    private readonly record struct Pools(double Health, double MaxHealth, double Mana, double MaxMana);

    /// <summary>
    /// Ticks <paramref name="match"/> for up to <paramref name="seconds"/>, flushing each step, until a clone of <paramref name="kind"/> exists.
    /// Returns it with the pools of <paramref name="champion"/> in the step it appeared (later regeneration would blur the copy).
    /// </summary>
    private static (Entity Clone, Pools Copied) AdvanceUntilClone(Match match, Entity champion, EntityKind kind, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step)
        {
            match.Runtime.Tick(Step);
            match.Runtime.Delivery.Flush();
            if (match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.Kind == kind) is { } clone)
                return (clone, new Pools(champion.Health, champion.MaxHealth, champion.Mana, champion.MaxMana));
        }
        throw new InvalidOperationException($"no {kind} appeared within {seconds} s");
    }

    /// <summary>
    /// What each client is told when a clone of <paramref name="champion"/> appears. The stock client's minion bar draw,
    /// The client, draws the client
    /// (the champion bar: the hero GameBar art, health ticks, the mana bar and a level box) when
    /// <c>GetCloneOf</c> (vtable +0x1BC, or the unit itself) is an
    /// <c>obj_AI_Hero</c> (vtable +0xA0 returns 1); that bar's level box is then the champion's own level. The link is
    /// set only by the client and <c>CloneInventory</c> (+0x238, +0x23C), which <c>DoSpawnPet</c> (the client, bytes
    /// -) calls with the unit the 0x3C names at offset 0xAA (CloneInventory only with the CopyItems bit). The client
    /// also copies the champion's mHP, mMaxHP, mMP and mMaxMP into the clone, so the first stat block must agree.
    /// </summary>
    private static void CreatedAsACopyOf((BbM3.WireCapture Own, BbM3.WireCapture Enemy) wire, Entity clone, Entity champion, Pools copied, string what)
    {
        foreach (var (who, capture) in new[] { ("owner's", wire.Own), ("enemy's", wire.Enemy) })
        {
            var creations = capture.Packets.Select(item => item.Packet.Plaintext.ToArray())
                .Where(bytes => bytes.Length > 9 && (bytes[0] is 0x3C or 0x80 or 0x66) && BitConverter.ToUInt32(bytes, 5) == clone.NetId).ToArray();
            TestAssert.Equal(1, creations.Length, $"the {who} client creates {what} once");
            var spawn = creations[0];
            TestAssert.Equal((byte)0x3C, spawn[0], $"the {who} client creates {what} with the pet creation (DoSpawnPet), the one packet that names a copied unit");
            TestAssert.Equal(champion.NetId, BitConverter.ToUInt32(spawn, 1), $"the {who} client runs {what}'s creation on {champion.ChampionName}");
            TestAssert.Equal(champion.NetId, BitConverter.ToUInt32(spawn, 0xAA),
                $"the {who} client is told {what} copies {champion.ChampionName} (offset 0xAA): GetCloneOf is a hero, so it draws the champion bar with his level");
            TestAssert.True((spawn[0xAE] & 1) != 0, $"CopyItems: the {who} client's CloneInventory points the same link at {champion.ChampionName}");
            var block = Blocks(capture, clone.NetId).First(bytes => (bytes[10] & 2) != 0 && (BitConverter.ToUInt32(bytes, 15) & 0xFu) == 0xFu);
            float Value(int bit) => BitConverter.ToSingle(block, 19 + 4 * bit);
            TestAssert.Equal((float)copied.MaxHealth, Value(2), $"the {who} client's first block gives {what} {champion.ChampionName}'s maximum health (mMaxHP)");
            TestAssert.Equal((float)copied.MaxMana, Value(3), $"the {who} client's first block gives {what} {champion.ChampionName}'s maximum mana (mMaxMP)");
            TestAssert.Near(copied.Health, Value(0), 0.5, $"the {who} client's first block gives {what} {champion.ChampionName}'s health at the copy (mHP)");
            TestAssert.Near(copied.Mana, Value(1), 0.5, $"the {who} client's first block gives {what} {champion.ChampionName}'s mana at the copy (mMP)");
            TestAssert.Equal(1u, Latest(Blocks(capture, clone.NetId), 7), $"the {who} client holds {what} targetable, so the bar gate in front of the clone test is open");
        }
    }

    /// <summary>
    /// Batch 734 (owner, 9 November 2026: "Looking at old gameplay it should look like a champion healthbar, not minion healthbar"). Wukong's W:
    /// <c>MonkeyKingDecoy</c> calls <c>BBCloneUnitPet(UnitToClone = Owner)</c>, so each client is told the decoy copies Wukong and draws the
    /// champion bar with Wukong's level and the health and mana he had (see <see cref="CreatedAsACopyOf"/>). The server already sent this;
    /// batch 732 read the minion bar draw without its clone test.
    /// </summary>
    [Check("scripting.bb-spawned-monkeyking-decoy-is-created-as-a-copy-of-wukong")]
    public static void MonkeyKingDecoyIsCreatedAsACopyOfWukong()
    {
        using var match = new Match("MonkeyKing", foeChampion: "Orianna");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Refill();
        var both = BothTeams(match);
        TestAssert.True(match.Cast(1), "W casts");
        var (decoy, copied) = AdvanceUntilClone(match, match.Hero, EntityKind.ClonePet, 1.0);
        match.Advance(0.1);
        match.Runtime.Delivery.Flush();
        CreatedAsACopyOf(both, decoy, match.Hero, copied, "the decoy");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Batch 734: the same rule for the other clones of a champion. Shaco's R (<c>Hallucinate</c>) and LeBlanc's passive (<c>LeblancMIApplicator</c>,
    /// below 40 percent health) call the same <c>BBCloneUnitPet(UnitToClone = Owner)</c>, so their clones draw the champion bar too.
    /// </summary>
    [Check("scripting.bb-spawned-shaco-and-leblanc-clones-are-created-as-copies-of-their-champion")]
    public static void ShacoAndLeblancClonesAreCreatedAsCopiesOfTheirChampion()
    {
        using (var shaco = new Match("Shaco", foeChampion: "Orianna"))
        {
            shaco.Learn(1);
            shaco.Refill();
            var both = BothTeams(shaco);
            TestAssert.True(shaco.Cast(3), "Hallucinate casts");
            var (clone, copied) = AdvanceUntilClone(shaco, shaco.Hero, EntityKind.ShacoClone, 1.5);
            shaco.Advance(0.1);
            shaco.Runtime.Delivery.Flush();
            CreatedAsACopyOf(both, clone, shaco.Hero, copied, "Shaco's clone");
            TestAssert.Equal(0, shaco.Module.ScriptFailures.Count, "no Shaco script failed");
        }
        using (var leblanc = new Match("Leblanc", foeChampion: "Orianna"))
        {
            var hero = leblanc.Hero;
            var both = BothTeams(leblanc);
            TestAssert.True(leblanc.Runtime.Combat.ApplyHit(leblanc.Foe.NetId, hero.NetId, hero.Health * 0.7, DamageType.True, DamageSourceKind.Raw),
                "a hit takes LeBlanc under forty percent health");
            var (image, copied) = AdvanceUntilClone(leblanc, hero, EntityKind.ClonePet, 5.0);
            leblanc.Advance(0.1);
            leblanc.Runtime.Delivery.Flush();
            CreatedAsACopyOf(both, image, hero, copied, "LeBlanc's image");
            TestAssert.Equal(0, leblanc.Module.ScriptFailures.Count, "no LeBlanc script failed");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 734 follow-up: every way a client can come to create a clone
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The three clones of a champion the owner named, each with how its match makes one.</summary>
    private static readonly (string Champion, EntityKind Kind, string What)[] CloneCases =
    [
        ("MonkeyKing", EntityKind.ClonePet, "Wukong's decoy"),
        ("Shaco", EntityKind.ShacoClone, "Shaco's clone"),
        ("Leblanc", EntityKind.ClonePet, "LeBlanc's image"),
    ];

    /// <summary>
    /// The production observer of every seat, with the seat's own view (<see cref="MatchRuntimeNetwork.NewObserver"/>, as
    /// <c>AttachObserver</c> builds it): the spawn burst already made every champion known to it. Seats: 0 the clone's champion, 1 his ally,
    /// 2 the enemy, 3 the second enemy.
    /// </summary>
    private static BbM3.WireCapture[] SeatObservers(Match match)
    {
        var captures = new BbM3.WireCapture[match.Runtime.Session.Participants.Count];
        for (var seat = 0; seat < captures.Length; seat++)
        {
            captures[seat] = new BbM3.WireCapture(() => match.Now);
            match.Runtime.Delivery.AddObserver(Amberline.Network.Sessions.MatchRuntimeNetwork.NewObserver(
                match.Runtime, match.Runtime.Session.Participants[seat], captures[seat]));
        }
        return captures;
    }

    /// <summary>Makes the clone of <paramref name="what"/>: Wukong's W, Shaco's R, or the hit that takes LeBlanc under 40 percent health.</summary>
    private static Entity MakeClone(Match match, EntityKind kind, string what)
    {
        var hero = match.Hero;
        TestAssert.True(match.Champion switch
        {
            "MonkeyKing" => match.Cast(1),
            "Shaco" => match.Cast(3),
            _ => match.Runtime.Combat.ApplyHit(match.FarFoe.NetId, hero.NetId, hero.Health * 0.7, DamageType.True, DamageSourceKind.Raw),
        }, $"{what} is made");
        return AdvanceUntilClone(match, hero, kind, 5.0).Clone;
    }

    private static void AdvanceFlushed(Match match, double seconds)
    {
        match.Advance(seconds);
        match.Runtime.Delivery.Flush();
    }

    /// <summary>
    /// The one packet that creates <paramref name="clone"/> on a client, and that it names <paramref name="champion"/> as the unit it copies
    /// (the stock client then draws the champion bar, see <see cref="CreatedAsACopyOf"/>). A second creation of one net id would make a second
    /// native object, so exactly one is required.
    /// </summary>
    private static void CreatesTheCopy(BbM3.WireCapture capture, Entity clone, Entity champion, string path)
    {
        var creations = capture.Packets.Select(item => item.Packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length > 9 && (bytes[0] is 0x3C or 0x80 or 0x66) && BitConverter.ToUInt32(bytes, 5) == clone.NetId).ToArray();
        TestAssert.Equal(1, creations.Length, $"{path}: the client creates the clone exactly once");
        TestAssert.Equal((byte)0x3C, creations[0][0], $"{path}: with the pet creation");
        TestAssert.Equal(champion.NetId, BitConverter.ToUInt32(creations[0], 0xAA),
            $"{path}: naming {champion.ChampionName} as the unit it copies, so GetCloneOf is a hero and the client draws the champion bar");
    }

    /// <summary>
    /// Batch 734 follow-up (the owner saw a minion-style bar on the decoy in real games). Each clone appears in the enemy team's fog (its
    /// champions are far), so the enemy client does not see it at its spawn. Paths: (a) and (b) the enemy's champion arrives from far away
    /// and sees it, the second enemy client with it (team sight), (d) the enemy loses it and sees it again, (e) the champion's own client
    /// and his ally's. In every
    /// one the clone is created once, by the 0x3C naming the champion: the enemy clients at the spawn (the pet creation is a broadcast to
    /// every client that holds the owner, followed by a leave of its sight), the others at their first sight. Fog, brush, a ward or a reveal
    /// all reach the same first-sight code (the team's sight set), so one of them stands for all.
    /// </summary>
    [Check("scripting.bb-spawned-hero-clones-unseen-at-spawn-are-created-as-copies")]
    public static void HeroClonesUnseenAtSpawnAreCreatedAsCopies()
    {
        foreach (var (champion, kind, what) in CloneCases)
        {
            using var match = new Match(champion, foeChampion: "Orianna");
            match.Learn(1);
            match.Refill();
            match.Foe.Position = new Position2(11000, 11000);
            var seats = SeatObservers(match);
            AdvanceFlushed(match, 0.1);
            var clone = MakeClone(match, kind, what);
            var enemy = match.Runtime.Session.Participants[2].View;
            TestAssert.True(enemy.Known.Contains(clone.NetId) && !enemy.Visible.Contains(clone.NetId),
                $"{what}: the enemy client holds the clone but does not see it at its spawn");
            match.Foe.Position = new Position2(clone.Position.X + 150, clone.Position.Z);
            AdvanceFlushed(match, 0.1);
            TestAssert.True(enemy.Visible.Contains(clone.NetId), $"{what}: (a) the enemy's champion walks in and sees it");
            match.Foe.Position = new Position2(11000, 11000);
            AdvanceFlushed(match, 0.1);
            TestAssert.False(enemy.Visible.Contains(clone.NetId), $"{what}: (d) the enemy loses sight of it");
            match.Foe.Position = new Position2(clone.Position.X + 150, clone.Position.Z);
            AdvanceFlushed(match, 0.1);
            TestAssert.True(clone.Alive && enemy.Visible.Contains(clone.NetId), $"{what}: (d) and sees it again");
            CreatesTheCopy(seats[2], clone, match.Hero, $"{what}, (a)+(b)+(d) the enemy, unseen at its spawn, its champion arriving later");
            CreatesTheCopy(seats[3], clone, match.Hero, $"{what}, the other enemy client (its team's sight)");
            CreatesTheCopy(seats[0], clone, match.Hero, $"{what}, (e) the champion's own client");
            CreatesTheCopy(seats[1], clone, match.Hero, $"{what}, (e) his ally's client");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{what}: no script failed");
        }
    }

    /// <summary>
    /// Batch 734 follow-up, path (c): the enemy's client drops and reconnects while the clone lives (the stock staged handshake, as
    /// <c>DeathCleanupLiveChecks</c> drives it). The returning client gets every champion again in its spawn burst (its view holds them before
    /// any unit is created), then creates the clone at its first sight, by the 0x3C naming the champion. Run for the clone in sight of the
    /// returning client and for one it only meets later.
    /// </summary>
    [Check("scripting.bb-spawned-hero-clones-are-created-as-copies-after-a-reconnect")]
    public static void HeroClonesAreCreatedAsCopiesAfterAReconnect()
    {
        foreach (var (champion, kind, what) in CloneCases)
            foreach (var inSight in new[] { true, false })
            {
                using var match = new Match(champion, foeChampion: "Orianna");
                match.Learn(1);
                match.Refill();
                SeatObservers(match);
                AdvanceFlushed(match, 0.1);
                var clone = MakeClone(match, kind, what);
                match.Foe.Position = inSight ? new Position2(clone.Position.X + 150, clone.Position.Z) : new Position2(11000, 11000);
                AdvanceFlushed(match, 0.1);
                var session = match.Runtime.Session;
                var seat = session.Participants[2];
                TestAssert.True(session.Disconnect(seat.ConnectionId!.Value), $"{what}: the enemy's client drops");
                TestAssert.True(match.Runtime.Delivery.RemoveObserver(seat.PlayerId), $"{what}: its observer is detached");
                const long connection = 103;
                var back = session.AuthenticateReconnect(connection, session.BuildKeyCheck(seat.PlayerId))
                    ?? throw new InvalidOperationException($"{what}: the enemy cannot reconnect");
                session.Receive(connection, 1, [0xC5]);
                session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                if (back.SpawnAt is { } spawnAt && spawnAt > match.Now) match.Runtime.World.Clock.SetVirtual(spawnAt);
                session.RunSchedulers();
                session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                session.RunSchedulers();
                TestAssert.True(back.Spawned && back.Loaded && back.Ready, $"{what}: the reconnect completes its loading");
                TestAssert.True(back.View.Known.Contains(match.Hero.NetId) && !back.View.Known.Contains(clone.NetId),
                    $"{what}: the returning client holds {match.Hero.ChampionName} again, and not yet the clone");
                var capture = new BbM3.WireCapture(() => match.Now);
                match.Runtime.Delivery.AddObserver(Amberline.Network.Sessions.MatchRuntimeNetwork.NewObserver(match.Runtime, back, capture));
                AdvanceFlushed(match, 0.1);
                if (!inSight)
                {
                    TestAssert.False(back.View.Known.Contains(clone.NetId), $"{what}: a clone out of the returning client's sight is not created yet");
                    match.Foe.Position = new Position2(clone.Position.X + 150, clone.Position.Z);
                    AdvanceFlushed(match, 0.1);
                }
                TestAssert.True(clone.Alive && back.View.Visible.Contains(clone.NetId), $"{what}: the returning client sees the clone");
                CreatesTheCopy(capture, clone, match.Hero, $"{what}, (c) after a reconnect, {(inSight ? "in sight" : "met later")}");
                TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{what}: no script failed");
            }
    }
}
