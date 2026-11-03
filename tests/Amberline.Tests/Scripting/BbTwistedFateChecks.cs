using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 5 (19 October 2026): Twisted Fate plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the literals of the
/// shipped scripts and spell inibins at rank one; the scenario <c>S-twistedfate</c> shows the same facts, and more, on a headless match.
/// </summary>
public static class BbTwistedFateChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static Match Duel()
    {
        var match = new Match("TwistedFate");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7080, 7000);
        match.Foe.Armor = 40.0;
        match.Foe.SpellBlock = 30.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        return match;
    }

    [Check("scripting.bb-twistedfate-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosure()
    {
        var module = new BbChampionModule("TwistedFate");
        foreach (var name in new[] { "CharScriptTwistedFate", "WildCards", "PickACard", "CardmasterStack", "Destiny", "Gate", "Destiny_marker", "SealFateMissile", "BlueCardLock",
                     "RedCardLock", "GoldCardLock", "BlueCardAttack", "RedCardAttack", "goldcardattack", "CardMasterStackHolder", "CardmasterStackParticle", "SecondSight" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("TwistedFate");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var hero = new Entity { ChampionName = "TwistedFate" };
        TestAssert.Equal(["WildCards", "PickACard", "CardmasterStack", "Destiny"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from TwistedFate.inibin");
        var info = ChampionCatalog.ByName("TwistedFate")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 1350, RetailBot: false } || info is { Playable: true, Role: "mid", DefaultIp: 1350 }, "playable as a mid for 1350 IP");
    }

    [Check("scripting.bb-twistedfate-wild-cards-hit-a-target-once-for-the-script-damage")]
    public static void WildCardsHitOnce()
    {
        using var match = Duel();
        match.Foe.Position = new Position2(7800, 7000);
        TestAssert.True(match.Cast(0, at: new Position2(7800, 7000)), "Wild Cards casts at a point");
        match.Advance(3.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "three cards, one hit on a unit the cards share: " + string.Join(";", hits));
        NearF32(60.0 + 0.65 * match.Hero.AbilityPower, hits[0].Raw, "60 at rank 1 plus 0.65 ability power, magic");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twistedfate-pick-a-card-cycles-locks-and-throws-the-card")]
    public static void PickACardLocksEachColour()
    {
        foreach (var (lockName, bonus) in new[] { ("BlueCardLock", 40.0), ("RedCardLock", 30.0), ("GoldCardLock", 15.0) })
        {
            using var match = Duel();
            TestAssert.True(match.Cast(1, match.Hero), "Pick a Card casts on himself");
            match.Advance(Step);
            var found = false;
            for (var tick = 0; tick < 120 && !found; tick++)
            {
                var slot = match.Hero.ActiveSpellNames[1];
                TestAssert.True(slot is "BlueCardLock" or "RedCardLock" or "GoldCardLock", $"the slot reads a card lock, not '{slot}' (Rnd1 is supplied)");
                if (string.Equals(slot, lockName, StringComparison.OrdinalIgnoreCase)) { found = match.Cast(1, match.Hero); break; }
                match.Advance(Step);
            }
            TestAssert.True(found, $"{lockName} comes up and locks");
            match.Advance(0.3);
            match.Hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
            match.Advance(1.0);
            var card = HitsOn(match, "Foe").First(hit => hit.Kind != "Basic");
            NearF32(match.Hero.AttackDamage + bonus + 0.4 * match.Hero.AbilityPower, card.Raw, $"{lockName}: attack damage + {bonus} + 0.4 ability power (rank 1), magic");
            TestAssert.Equal("PickACard", match.Hero.ActiveSpellNames[1], "the slot is Pick a Card again");
            TestAssert.True(match.Hero.SpellCooldowns[1] > 4.0, "the cooldown started");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        }
    }

    /// <summary>The cast answer of a swing or a card: its spell hash, flags byte and designer total time.</summary>
    private static (uint Hash, byte Flags, float Total) CastAnswer(PlaintextPacket packet)
    {
        var info = packet.Plaintext.Span[9..];
        var tail = 48 + 17 * info[47];
        return (BinaryPrimitives.ReadUInt32LittleEndian(info[2..]), info[tail + 20], BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 8)..]));
    }

    [Check("scripting.bb-twistedfate-gold-card-throw-frees-the-hero-after-its-windup")]
    public static void GoldCardThrowFreesTheHeroAfterItsWindup()
    {
        // Batch 175, the owner's thrown-card report (24 October 2026, "he can't move for a short while after throwing it"): the gold card's swing carries isAutoAttack and the
        // hero's attack period (batch 168), and GoldCardPreAttack carries CantCancelWhileChanneling, so NoteCastWindup held a move sent during the
        // throw until the attack period had passed (1.49 s on the wire). An auto attack is not a channel: its window is the windup, so the held move
        // is applied once the windup is over, while the swing keeps the attack period as its total.
        using var match = Duel();
        TestAssert.True(match.Cast(1, match.Hero), "Pick a Card casts on himself");
        match.Advance(Step);
        var found = false;
        for (var tick = 0; tick < 120 && !found; tick++)
        {
            if (match.Hero.ActiveSpellNames[1] == "GoldCardLock") { found = match.Cast(1, match.Hero); break; }
            match.Advance(Step);
        }
        TestAssert.True(found, "GoldCardLock comes up and locks");
        match.Advance(0.3);
        var hero = match.Hero;
        var wire = Observe(match, hero.Team, hero);
        wire.Packets.Clear();
        hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "the attack that throws the card is queued");
        match.Advance(2.0 / 30.0);
        var order = ClientGameplayPackets.MoveOrder(hero.NetId, 2, new Position2(6600, 7000), 0);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay),
            "the move order sent during the throw is accepted");
        match.Runtime.Delivery.Flush();
        var sent = match.Now;
        match.Advance(0.6);
        var swings = new List<(uint Hash, byte Flags, float Total)>();
        var firstWaypoint = -1.0;
        foreach (var (at, packet) in wire.Packets)
        {
            var span = packet.Plaintext.Span;
            if (span[0] == SpellPackets.CastSpellAnswerOpcode && span.Length > 60 && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == hero.NetId)
                swings.Add(CastAnswer(packet));
            if (firstWaypoint < 0 && at >= sent - 1e-6 && span[0] == VisionPackets.WaypointListOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == hero.NetId)
                firstWaypoint = at - sent;
        }
        var swing = swings.Where(answer => answer.Hash == SpellSystem.ProtocolHash("GoldCardPreAttack")).ToArray();
        TestAssert.Equal(1, swing.Length, "the throw is announced once as the gold card's swing");
        TestAssert.Equal(0x01, swing[0].Flags & 0x01, "the swing is the hero's own attack (isAutoAttack)");
        TestAssert.Near(hero.AttackPeriod, swing[0].Total, 0.01,
            "the swing keeps the attack period as its total (batch 168's clip); its order window is the windup, because an auto attack is not a channel");
        TestAssert.True(firstWaypoint >= 0 && firstWaypoint < 0.5,
            $"the hero's move is applied once the windup is over, not after the attack period (first own waypoint {firstWaypoint:0.00} s after the order)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    [Check("scripting.bb-twistedfate-gate-out-of-range-walks-into-range-then-teleports")]
    public static void GateOutOfRangeWalksIntoRangeBeforeItCasts()
    {
        // Batch 175, owner finding 21 ("casting R out of range creates odd minimap pathing: he heads to the teleport target and then back"): a point
        // cast out of its range walks to the nearest firing point inside the range, not to the point itself, so Gate fires from inside its range
        // of the target and the teleport follows 1.5 s later.
        using var match = Duel();
        match.Foe.Position = new Position2(11000, 11000);
        TestAssert.True(match.Cast(3, match.Hero), "Destiny casts");
        match.Advance(0.6);
        TestAssert.Equal("Gate", match.Hero.ActiveSpellNames[3], "the R slot reads Gate");
        var start = match.Hero.Position;
        var target = new Position2(7000, 13500);
        match.Hero.SpellCooldowns[3] = 0.0;
        TestAssert.True(match.Cast(3, at: target), "Gate is cast at a point 6500 units away");
        var firedAt = -1.0;
        var distanceAtFire = 0.0;
        var farthest = 0.0;
        for (var step = 1; step <= 240; step++)
        {
            match.Advance(Step);
            if (firedAt < 0) farthest = Math.Max(farthest, Dist(match.Hero.Position, start));
            if (firedAt < 0 && match.Hero.ActiveSpellNames[3] == "Destiny")
            {
                firedAt = step * Step;
                distanceAtFire = Dist(match.Hero.Position, target);
            }
            if (firedAt >= 0 && step * Step >= firedAt + 1.6) break;
        }
        TestAssert.True(firedAt >= 0, "the Gate cast fires once the hero is in range");
        // Batch 279: the cast is taken inside its range; its stop then seats the hero on its two-unit lattice anchor, at most
        // a diagonal unit (sqrt 2) from where it stood.
        TestAssert.True(distanceAtFire <= 5500.0 + Math.Sqrt(2.0) + 1e-6,
            $"the cast fires from inside its range ({distanceAtFire:0.00} units from the target)");
        TestAssert.True(farthest < 1100.0, $"the hero walks to the firing point, not to the teleport target ({farthest:0} units from where he cast)");
        TestAssert.True(Dist(match.Hero.Position, target) < 5.0, "Gate teleports him to the target 1.5 s after the cast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twistedfate-destiny-opens-gate-and-it-teleports-after-one-and-a-half-seconds")]
    public static void GateTeleportsAfterOneAndAHalfSeconds()
    {
        // Zhonya's Hourglass removing Gate (batch 107 U2) is driven by S-twistedfate (gate-zhonyas): the unit harness here has no item cast path that Gate leaves open.
        foreach (var zhonyas in new[] { false })
        {
            using var match = Duel();
            match.Foe.Position = new Position2(11000, 11000);
            TestAssert.True(match.Cast(3, match.Hero), "Destiny casts");
            match.Advance(0.6);
            TestAssert.Equal("Gate", match.Hero.ActiveSpellNames[3], "the R slot reads Gate");
            TestAssert.True(Buff(match.Foe, "Destiny") is not null, "every enemy champion carries the debuff");
            match.Hero.SpellCooldowns[3] = 0.0;
            var target = new Position2(7000, 9000);
            TestAssert.True(match.Cast(3, at: target), "Gate casts");
            match.Advance(0.1);
            TestAssert.True(Buff(match.Hero, "Gate") is not null, "the channel is a buff");
            if (zhonyas)
            {
                match.Advance(0.5);
                match.Hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(match.Hero, push: false);
                TestAssert.True(match.Cast(4, match.Hero), "Zhonya's Hourglass is used");
                TestAssert.True(Buff(match.Hero, "Gate") is null, "it removes Gate (batch 107 U2)");
            }
            match.Advance(1.8);
            var moved = Math.Sqrt(Math.Pow(match.Hero.Position.X - 7000, 2) + Math.Pow(match.Hero.Position.Z - 7000, 2));
            if (zhonyas) TestAssert.Near(0.0, moved, 20.0, "he stays where he is");
            else TestAssert.True(moved is > 1700.0 and < 2100.0, $"he arrives at the target (nearest passable point) after 1.5 s: moved {moved:0}");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        }
    }
}
