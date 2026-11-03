using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 388 (BUF-FLUSH): a buff's add and remove that share one flush, and the adds the client must never hold. Each check runs the shipped
/// script in a live match and reads what the caster's observers were sent, step by step (one flush per 1/30 s step), so a remove and an add
/// that go out together are visible as one step holding both.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Gangplank's Parley is an instant marker: its duration is 0.01 s and its own update action removes it, so the client holds nothing
    // (no texture, no torch). Before batch 388 its add went out and its removal in the same step.
    [Check("buffs.same-flush.parley-marker-sends-no-add")]
    public static void ParleyMarkerSendsNoAdd()
    {
        using var match = new Match("Gangplank", Team.Order);
        match.Cast(0, 0, match.Hero(1));
        var parley = SpellPackets.BuffNameHash("Parley");
        TestAssert.Equal(0, BuffAddsOf(FlushWindow(match, 1.0), parley), "a Parley marker sends no add to the client");
    }

    // Miss Fortune's E re-applies Slow every second, and the instance it replaces expires in the same step as the re-add. The remove and the
    // add went out in one flush (the sweep's MissFortune E row). The wire grace holds an expiry that a re-add of the name follows, so the
    // client gets one renewal of its copy, not a remove and an add of one buff in a flush.
    [Check("buffs.same-flush.slow-reapply-renews-the-copy")]
    public static void SlowReapplyRenewsTheCopy()
    {
        using var match = new Match("MissFortune", Team.Order);
        match.Cast(0, 2, match.Hero(1));
        var slow = SpellPackets.BuffNameHash("Slow");
        var steps = FlushWindow(match, 3.0);
        var sameFlush = steps.GroupBy(step => step.Step)
            .Count(group => group.Any(step => step.Opcode == 0x7F && HashAt(step.Packet, 6) == slow) &&
                           group.Any(step => step.Opcode == SpellPackets.BuffAddOpcode && HashAt(step.Packet, 9) == slow));
        TestAssert.Equal(0, sameFlush, "a Slow remove and a Slow add never share a flush");
    }

    // Batch 399: Gangplank's R adds Slow (BUFF_STACKS_AND_OVERLAPS, MaxStack 100, 1 s) to every enemy in its area about every quarter second, so four
    // or five instances live at once. Each add took a slot of its own, and the stock client draws one icon per slot. The overlapping instances of one
    // caster now share one client slot, shown with the live count (Reconstruction: retail's wire for overlapping stacks is not in the shipped files).
    [Check("buffs.overlap-wire.gangplank-r-holds-one-slow-slot")]
    public static void GangplankRHoldsOneSlowSlot()
    {
        using var match = new Match("Gangplank", Team.Order);
        match.Cast(0, 3, match.Hero(1));
        var slow = SpellPackets.BuffNameHash("Slow");
        var enemy = match.Hero(1).NetId;
        var steps = FlushWindow(match, 3.0);
        var most = 0;
        var adds = 0;
        for (var observer = 0; observer < match.Captures.Count; observer++)
        {
            var live = new HashSet<byte>();
            foreach (var step in steps.Where(s => s.Observer == observer).OrderBy(s => s.Step))
            {
                var span = step.Packet.Plaintext.Span;
                if (span.Length < 10 || BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1, 4)) != enemy) continue;
                if (step.Opcode == SpellPackets.BuffAddOpcode && HashAt(step.Packet, 9) == slow)
                {
                    adds++;
                    live.Add(span[5]);
                }
                else if (step.Opcode == 0x7F && HashAt(step.Packet, 6) == slow) live.Remove(span[5]);
                most = Math.Max(most, live.Count);
            }
        }
        TestAssert.True(adds >= 2, "the R slows the enemy more than once in three seconds");
        TestAssert.True(most <= 1, $"at most one Slow slot from Gangplank at any time (saw {most})");
    }

    // Batch 399: Aegis Protection adds one Pantheon_Aegis_Counter per attack and spell (BUFF_STACKS_AND_OVERLAPS, MaxStack 5), and the owner's
    // hand test saw four icons for four counters. The counters of one caster share one client slot whose count rises with each (Reconstruction, as
    // the Slow check above): four attacks hold one slot, and its count reaches four on the wire.
    [Check("buffs.overlap-wire.pantheon-aegis-counter-holds-one-slot-counting-to-four")]
    public static void PantheonAegisCounterHoldsOneSlotCountingToFour()
    {
        using var match = new Match("Pantheon", Team.Order);
        var counter = SpellPackets.BuffNameHash("Pantheon_Aegis_Counter");
        match.Attack(0, match.Hero(1));
        var steps = FlushWindow(match, 6.0);
        var most = 0;
        var events = 0;
        for (var observer = 0; observer < match.Captures.Count; observer++)
        {
            var live = new HashSet<byte>();
            var seen = 0;
            foreach (var step in steps.Where(s => s.Observer == observer).OrderBy(s => s.Step))
            {
                var span = step.Packet.Plaintext.Span;
                if (step.Opcode == SpellPackets.BuffAddOpcode && span.Length >= 13 && HashAt(step.Packet, 9) == counter)
                {
                    seen++;
                    live.Add(span[5]);
                    most = Math.Max(most, span[7]);
                }
                else if (step.Opcode == SpellPackets.BuffUpdateCountOpcode && span.Length >= 7 && live.Contains(span[5]))
                {
                    seen++;
                    most = Math.Max(most, span[6]);
                }
                else if (step.Opcode == 0x7F && HashAt(step.Packet, 6) == counter) live.Remove(span[5]);
                TestAssert.True(live.Count <= 1, $"at most one Aegis counter slot at a time (saw {live.Count})");
            }
            events = Math.Max(events, seen);
        }
        TestAssert.True(events >= 4, $"four counters reach one observer's wire, as an add or a count update (saw {events})");
        TestAssert.True(most >= 4, $"the counter's count reaches four on the wire (saw {most})");
    }

    // Batch 407: Gangplank's R re-applies Slow about every quarter second. The shipped add is StacksExclusive (the client's default), so one caster's
    // re-application renews the live instance: the enemy holds one Slow instance with count one, and the wire sends no count update (0x1F) for it.
    [Check("buffs.overlap-wire.gangplank-r-one-slow-instance-no-count-update")]
    public static void GangplankROneSlowInstanceNoCountUpdate()
    {
        using var match = new Match("Gangplank", Team.Order);
        match.Cast(0, 3, match.Hero(1));
        var enemy = match.Hero(1);
        var steps = FlushWindow(match, 2.0);
        // A renewal of a copy the client holds goes out as 0x1F with the same count (ReplicationDelivery.AsHeldBuffUpdate: the Slow template has its
        // own particle); what must not happen is a count change (the owner's 4-5-4-5 flicker), so every 0x1F to the enemy carries the count one.
        var changedCount = steps.Count(step => step.Opcode == SpellPackets.BuffUpdateCountOpcode &&
            step.Packet.Plaintext.Span.Length >= 7 && step.Packet.Plaintext.Span[6] != 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(step.Packet.Plaintext.Span.Slice(1, 4)) == enemy.NetId);
        TestAssert.Equal(0, changedCount, "the enemy's client sees no Slow count change (no 0x1F with a count other than one)");
        TestAssert.Equal(1, enemy.Buffs.Count(buff => buff.Name == "Slow"), "one Slow instance lives on the enemy");
        TestAssert.Equal(1, enemy.Buffs.Where(buff => buff.Name == "Slow").Sum(buff => buff.Stacks), "the Slow count is one");
    }

    // Batch 407: the shipped Slow sets MoveSpeedMod on both IncPercentMovementSpeedMod and IncPercentMultiplicativeMovementSpeedMod, so one 25
    // percent Slow is one slow: 75 of 100 speed, not two entries (the second at 0.35) that leave 68.4.
    [Check("movement.slow-one-buff-counts-once")]
    public static void SlowOneBuffCountsOnce()
    {
        var payload = new BuffPayload { MoveSpeedPercent = -0.25, MoveSpeedMult = -0.25 };
        var speed = MovementSpeedCalculator.Calculate(100.0, 0.0, 0.0, 0.0, new[] { payload }, applyHeroSoftCaps: false);
        TestAssert.True(Math.Abs(speed - 75.0) < 1e-9, $"one 25 percent Slow leaves 75 of 100 speed (saw {speed:F4})");
    }

    // The rules the live checks above rest on, read directly: an overlapping add carries the wire margin (Slow, the carpet bomb missile), and an
    // instant marker is one whose duration is one step at most.
    [Check("buffs.same-flush.rules-for-margins-and-instant-markers")]
    public static void SameFlushRules()
    {
        TestAssert.True(BbBuffRuntime.ReplicatesWithGraceMargin(BbBuffAddType.BUFF_STACKS_AND_OVERLAPS), "an overlapping add carries the margin");
        TestAssert.True(BbBuffRuntime.ReplicatesWithGraceMargin(BbBuffAddType.BUFF_REPLACE_EXISTING), "a replacing add still carries it");
        TestAssert.False(BbBuffRuntime.ReplicatesWithGraceMargin(BbBuffAddType.BUFF_STACKS_AND_CONTINUE), "a continuing add is not changed");

        TestAssert.True(BbBuffRuntime.IsInstantMarker(0.0) && BbBuffRuntime.IsInstantMarker(0.001) && BbBuffRuntime.IsInstantMarker(0.01),
            "a duration within one step is an instant marker");
        TestAssert.True(BbBuffRuntime.IsInstantMarker(1.0 / 30.0), "exactly one step is an instant marker");
        TestAssert.False(BbBuffRuntime.IsInstantMarker(0.25) || BbBuffRuntime.IsInstantMarker(1.0), "a buff of a quarter second or more is not one");
    }

    // Batch 554 (owner finding 93): each buff Riven's Q adds or removes on her pushes her stat block, and each push carried her whole spellbook
    // tooltip table (0x83, selectors 60 and up), so one flush sent the same table six or seven times. A copy identical to the observer's last
    // table of the unit in the flush is no longer sent (ReplicationDelivery.IsRepeatedSpellTooltips); the table itself still goes out.
    [Check("replication.riven-q-sends-the-spell-tooltip-table-once")]
    public static void RivenQSendsTheSpellTooltipTableOnce()
    {
        using var match = new Match("Riven", Team.Order);
        var riven = match.Hero(0).NetId;
        // The first strike's script writes the table's rows (Entity.ScriptSpellTooltips); the second strike's flush is the one measured.
        match.CastGround(0, match.Hero(1).Position, slot: 0);
        FlushWindow(match, 1.0);
        var steps = AfterCast(match, () => match.CastGround(0, match.Hero(1).Position, slot: 0), 1.0);
        bool IsTable(FlushedPacket step)
        {
            var span = step.Packet.Plaintext.Span;
            if (step.Opcode != 0x83 || span.Length < 24 || span.Length != 7 + 17 * BinaryPrimitives.ReadUInt16LittleEndian(span[5..])) return false;
            for (var entry = 7; entry < span.Length; entry += 17)
                if (BinaryPrimitives.ReadUInt32LittleEndian(span[entry..]) != riven || span[entry + 4] < 60) return false;
            return true;
        }
        var tables = steps.Where(IsTable).ToList();
        var repeats = tables.GroupBy(step => (step.Step, step.Observer))
            .Sum(group => group.Zip(group.Skip(1)).Count(pair => pair.First.Packet.Plaintext.Span.SequenceEqual(pair.Second.Packet.Plaintext.Span)));
        TestAssert.True(tables.Any(step => step.Step == -1), "the cast's flush sends Riven's spellbook tooltip table");
        TestAssert.Equal(0, repeats, "no observer gets the same tooltip table twice in a row in one flush");
    }

    // Batch 389 (BUF-FLUSH): Shaco's box adds Stealth and removes it in the same run once a unit is in range (the sweep's Shaco W row). The
    // client never held that Stealth, so it must see neither the add nor the remove. The shipped box script decides the add and the remove;
    // the flush rule (ReplicationDelivery.Flush, the buff pairs) decides what reaches the wire.
    [Check("buffs.same-flush.shaco-box-in-range-sends-no-stealth-pair")]
    public static void ShacoBoxInRangeSendsNoStealthPair()
    {
        using var match = new Match("Shaco", Team.Order);
        var stealth = SpellPackets.BuffNameHash("Stealth");
        // The box lands where the foe stands, so the foe is in its range from the box's first run.
        match.CastGround(0, match.Hero(1).Position, slot: 1);
        var steps = FlushWindow(match, 3.0);
        var box = ShacoBoxOf(match);
        TestAssert.Equal(0, SameFlushPairs(steps, box.NetId, stealth), "the box's Stealth add and remove never share a flush");
        TestAssert.Equal(0, steps.Count(packet => packet.Opcode == SpellPackets.BuffAddOpcode && IsAddOrRemoveOf(packet, box.NetId, stealth)),
            "the box sends no Stealth add when its run removes that Stealth again");
    }

    // The other half of the box rule: with no unit in its range the box keeps its Stealth, so the add still goes out.
    [Check("buffs.same-flush.shaco-box-keeps-its-stealth-add")]
    public static void ShacoBoxKeepsItsStealthAdd()
    {
        using var match = new Match("Shaco", Team.Order);
        var stealth = SpellPackets.BuffNameHash("Stealth");
        match.Hero(1).Position = new Position2(9000, 9000);   // out of the box's range
        match.CastGround(0, new Position2(7000, 7300), slot: 1);
        var steps = FlushWindow(match, 3.0);
        var box = ShacoBoxOf(match);
        TestAssert.True(steps.Count(packet => packet.Opcode == SpellPackets.BuffAddOpcode && IsAddOrRemoveOf(packet, box.NetId, stealth)) > 0,
            "the box keeps its Stealth, so its add goes out");
        TestAssert.Equal(0, SameFlushPairs(steps, box.NetId, stealth), "no Stealth remove shares the flush of its add");
    }

    // Batch 389: Lux's R removes the charge by name and re-adds it in the same tick (the sweep's Lux R row). The client holds the charge, so the
    // flush sends one renewal of its copy (0x32 or 0x1F), not a remove and an add of one buff.
    [Check("buffs.same-flush.lux-r-renews-the-charge")]
    public static void LuxRRenewsTheCharge()
    {
        using var match = new Match("Lux", Team.Order);
        var charge = SpellPackets.BuffNameHash("LuxIlluminatingFraulein");
        var foe = match.Hero(1);
        match.CastGround(0, foe.Position, slot: 0);      // Q charges the foe
        var charged = FlushWindow(match, 1.0);
        var add = charged.FirstOrDefault(packet => packet.Opcode == SpellPackets.BuffAddOpcode && IsAddOrRemoveOf(packet, foe.NetId, charge));
        TestAssert.True(add is not null, "the foe carries the charge before R");
        var slot = add!.Packet.Plaintext.Span[5];
        match.CastGround(0, foe.Position, slot: 3);      // R
        var steps = FlushWindow(match, 1.0);
        TestAssert.Equal(0, SameFlushPairs(steps, foe.NetId, charge), "R sends no charge remove and add in one flush");
        TestAssert.True(RenewalsOf(steps, foe.NetId, slot) > 0, "R renews the copy the client holds");
    }

    // The guard for everything else: an ordinary buff adds in one flush and expires in a later one (the charge lasts six seconds).
    [Check("buffs.same-flush.ordinary-add-and-expiry-stay-apart")]
    public static void OrdinaryAddAndExpiryStayApart()
    {
        using var match = new Match("Lux", Team.Order);
        var charge = SpellPackets.BuffNameHash("LuxIlluminatingFraulein");
        var foe = match.Hero(1);
        match.CastGround(0, foe.Position, slot: 0);      // Q charges the foe
        var steps = FlushWindow(match, 8.0);
        var added = steps.Where(packet => packet.Opcode == SpellPackets.BuffAddOpcode && IsAddOrRemoveOf(packet, foe.NetId, charge))
            .Select(packet => packet.Step).ToList();
        var expired = steps.Where(packet => packet.Opcode == 0x7F && IsAddOrRemoveOf(packet, foe.NetId, charge))
            .Select(packet => packet.Step).ToList();
        TestAssert.True(added.Count > 0 && expired.Count > 0, "the charge is added and then removed");
        TestAssert.True(expired.Min() > added.Min(), "the expiry goes out in a later flush than the add");
        TestAssert.Equal(0, SameFlushPairs(steps, foe.NetId, charge), "no charge add and remove share a flush");
    }

    // Batch 389: a minion the turret kills gets its TurretDamageMarker on the lethal hit and loses it in the death cleanup of that same hit (the
    // sweep's TurretDamageMarker rows). The turret marks itself on each landed hit and the victim once the turret reads three, so the fourth
    // hit both marks the minion and kills it.
    [Check("buffs.same-flush.turret-kill-sends-no-marker-pair")]
    public static void TurretKillSendsNoMarkerPair()
    {
        using var match = new Match("Annie", Team.Order);
        var marker = SpellPackets.BuffNameHash("TurretDamageMarker");
        var runtime = match.Runtime;
        var turret = runtime.World.Entities.Values.First(unit => unit.UnitType == UnitType.Turret && unit.Team == Team.Order);
        turret.Protected = false;
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new Position2(turret.Position.X + 60, turret.Position.Z)) { Health = 80, MaxHealth = 80, Armor = 0 });
        // The minion reaches the observers before the shot, as it does in a live step.
        runtime.Delivery.Flush();
        match.Clear();
        var combat = new CombatService(runtime.World, runtime.Dependencies.CombatHooks,
            new ProtocolCombatTransport(runtime.Delivery, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
            new HalfRandomSource(), runtime.HeroStats, gameData: GameData.Default);
        // The sweep's shape: the victim's marker is added before the lethal damage (0x68) and the death cleanup removes it in the same step. The
        // marker's add and its remove are queued through the live delivery around the turret's real lethal shot.
        runtime.Delivery.Enqueue(SpellPackets.AddBuff(new BuffAdd(minion.NetId, 0, (byte)BuffType.Internal, marker, 3.0f, turret.NetId, 1)));
        TestAssert.True(combat.ApplyHit(turret.NetId, minion.NetId, 100, DamageType.Physical, DamageSourceKind.Basic), "the turret's shot lands");
        runtime.Delivery.Enqueue(SpellPackets.RemoveBuff(minion.NetId, 0, marker));
        var steps = FlushWindow(match, 0.5);
        TestAssert.False(minion.Alive, "the turret's shot kills the minion");
        TestAssert.Equal(0, SameFlushPairs(steps, minion.NetId, marker), "the marker's add and its death cleanup never share a flush");
    }

    // Batch 390 (BUF-FLUSH): Sona's Crescendo removes her Song of Discord check and re-adds it in the same tick (the sweep's Sona R row). Here the
    // check holds slot 1 above a plug in slot 0, and the plug leaves before the cast: the re-add took the lowest free slot (0), so one flush held
    // a remove of slot 1 and an add of slot 0. The re-add takes the slot the check left, so the flush sends one renewal of the copy the client holds.
    [Check("buffs.same-flush.sona-r-re-add-takes-the-freed-slot")]
    public static void SonaRReAddTakesTheFreedSlot()
    {
        using var match = new Match("Sona", Team.Order);
        var check = SpellPackets.BuffNameHash("SonaSongofDiscordCheck");
        var sona = match.Hero(0);
        var buffs = match.Runtime.Buffs;
        var plug = buffs.AddBuff(sona, "FreedSlotPlug", BuffType.Internal, 60.0, sona.NetId)!;
        // Power Chord counts the spells and the third one readies it (BbSonaChecks, the Power Chord checks). Hymn (Q), Aria (W) and Song of Discord
        // (E) are the three, and Discord's check goes on Sona with the third. Crescendo (R) then removes that check and re-adds it while Power
        // Chord is ready: the sweep's Sona R probe (remove slot 5, add slot 3) is this state.
        void Press(int index)
        {
            sona.SpellCooldowns[index] = 0.0;     // a song puts the other songs on a cooldown (BbSonaChecks' Press resets it the same way)
            match.Cast(0, index, sona);
        }
        Press(0);
        Press(1);
        Press(2);
        FlushWindow(match, 1.0);
        var held = sona.Buffs.FirstOrDefault(buff => buff.Name == "SonaSongofDiscordCheck");
        TestAssert.True(held is not null && held.Slot > plug.Slot, "the song's check holds a slot above the plug");
        var slot = (byte)held!.Slot;
        buffs.RemoveBuff(sona, plug.Slot);      // the plug leaves: its slot is free again, below the check
        var steps = AfterCast(match, () => match.Cast(0, 3, match.Hero(1)), 1.0);     // R: Crescendo is cast at a unit (BbSonaChecks: Cast(3, Foe))
        TestAssert.Equal(0, SameFlushPairs(steps, sona.NetId, check),
            $"R sends no check remove and add in one flush (wire: remove slot {WireSlotOf(steps, sona.NetId, check, 0x7F)}, " +
            $"add slot {WireSlotOf(steps, sona.NetId, check, SpellPackets.BuffAddOpcode)})");
        TestAssert.True(RenewalsOf(steps, sona.NetId, slot) > 0, "R renews the copy the client holds in the slot the check left");
    }

    // Batch 390 (BUF-FLUSH): Lux's R removes the charge and re-adds it in the same tick (the sweep's Lux R row). Here the charge holds a slot
    // above a plug that leaves before the cast, so the re-add took the lowest free slot: a cross-slot remove and add in one flush.
    [Check("buffs.same-flush.lux-r-re-add-takes-the-freed-slot")]
    public static void LuxRReAddTakesTheFreedSlot()
    {
        using var match = new Match("Lux", Team.Order);
        var charge = SpellPackets.BuffNameHash("LuxIlluminatingFraulein");
        var foe = match.Hero(1);
        var buffs = match.Runtime.Buffs;
        var plug = buffs.AddBuff(foe, "FreedSlotPlug", BuffType.Internal, 60.0, foe.NetId)!;
        match.CastGround(0, foe.Position, slot: 0);      // Q charges the foe
        var charged = FlushWindow(match, 1.0);
        var add = charged.FirstOrDefault(packet => packet.Opcode == SpellPackets.BuffAddOpcode && IsAddOrRemoveOf(packet, foe.NetId, charge));
        TestAssert.True(add is not null, "the foe carries the charge before R");
        var slot = add!.Packet.Plaintext.Span[5];
        TestAssert.True(slot > plug.Slot, "the charge holds a slot above the plug");
        buffs.RemoveBuff(foe, plug.Slot);      // the plug leaves: its slot is free again, below the charge
        var steps = AfterCast(match, () => match.CastGround(0, foe.Position, slot: 3), 1.0);      // R
        TestAssert.Equal(0, SameFlushPairs(steps, foe.NetId, charge),
            $"R sends no charge remove and add in one flush (wire: remove slot {WireSlotOf(steps, foe.NetId, charge, 0x7F)}, " +
            $"add slot {WireSlotOf(steps, foe.NetId, charge, SpellPackets.BuffAddOpcode)})");
        TestAssert.True(RenewalsOf(steps, foe.NetId, slot) > 0, "R renews the copy the client holds in the slot the charge left");
    }

    // Batch 390 guard: the freed-slot rule is for the same name only. Two buffs of other names leave in one step (the lower one first, then the
    // higher one), and an add of a third name takes the lowest free slot, as before the rule.
    [Check("buffs.same-flush.unrelated-add-keeps-the-lowest-slot")]
    public static void UnrelatedAddKeepsTheLowestSlot()
    {
        using var match = new Match("Sona", Team.Order);
        var sona = match.Hero(0);
        var buffs = match.Runtime.Buffs;
        var low = buffs.AddBuff(sona, "FreedSlotPlug", BuffType.Internal, 60.0, sona.NetId)!;
        buffs.AddBuff(sona, "FreedSlotKeep", BuffType.Internal, 60.0, sona.NetId);
        var high = buffs.AddBuff(sona, "FreedSlotOther", BuffType.Internal, 60.0, sona.NetId)!;
        buffs.RemoveBuff(sona, low.Slot);
        buffs.RemoveBuff(sona, high.Slot);
        var added = buffs.AddBuff(sona, "FreedSlotNew", BuffType.Internal, 60.0, sona.NetId)!;
        TestAssert.Equal(low.Slot, added.Slot, "an add of another name takes the lowest free slot, not the one another name left");
    }

    private static Entity ShacoBoxOf(Match match) =>
        match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Kind == EntityKind.ShacoBox) ??
        throw new InvalidOperationException("no Shaco box in the world");

    // A 0xBF or 0x7F of one name on one unit, read at the offset that carries the name hash (9 for the add, 6 for the remove).
    private static bool IsAddOrRemoveOf(FlushedPacket packet, uint unit, uint hash) => packet.Opcode switch
    {
        SpellPackets.BuffAddOpcode => HashAt(packet.Packet, 1) == unit && HashAt(packet.Packet, 9) == hash,
        0x7F => HashAt(packet.Packet, 1) == unit && HashAt(packet.Packet, 6) == hash,
        _ => false,
    };

    // The steps in which one observer was sent both the add and the remove of one name on one unit: the client creates and destroys it in one frame.
    private static int SameFlushPairs(IEnumerable<FlushedPacket> packets, uint unit, uint hash) =>
        packets.Where(packet => IsAddOrRemoveOf(packet, unit, hash))
            .GroupBy(packet => (packet.Step, packet.Observer))
            .Count(group => group.Any(packet => packet.Opcode == 0x7F) &&
                            group.Any(packet => packet.Opcode == SpellPackets.BuffAddOpcode));

    // Renewals of one unit's slot (0x32 BuffReplace, 0x1F BuffUpdateCount): the client keeps the copy it holds and restarts its timers.
    private static int RenewalsOf(IEnumerable<FlushedPacket> packets, uint unit, byte slot) =>
        packets.Count(packet => (packet.Opcode == SpellPackets.BuffReplaceOpcode || packet.Opcode == SpellPackets.BuffUpdateCountOpcode) &&
            HashAt(packet.Packet, 1) == unit && packet.Packet.Plaintext.Span[5] == slot);

    // The slot byte of the first 0x7F (remove) or 0xBF (add) of one name on one unit; -1 when the flush has none.
    private static int WireSlotOf(IEnumerable<FlushedPacket> packets, uint unit, uint hash, int opcode)
    {
        var packet = packets.FirstOrDefault(candidate => candidate.Opcode == opcode && IsAddOrRemoveOf(candidate, unit, hash));
        return packet is null ? -1 : packet.Packet.Plaintext.Span[5];
    }

    // The turret's combat rolls are fixed (the ramp check's FixedRandomSource(0.5), which is private to another file).
    private sealed class HalfRandomSource : IRandomSource
    {
        public double NextDouble() => 0.5;
    }

    private sealed record FlushedPacket(int Step, int Observer, byte Opcode, PlaintextPacket Packet);

    // A cast and the window after it. The live cast helpers flush inside the cast, and FlushWindow clears the captures first, so the packets of the
    // cast's own flush are kept as step -1 (ahead of the window's steps).
    private static List<FlushedPacket> AfterCast(Match match, Action cast, double seconds)
    {
        match.Clear();
        cast();
        var atCast = new List<FlushedPacket>();
        for (var observer = 0; observer < match.Captures.Count; observer++)
            foreach (var packet in match.Captures[observer].Packets)
                if (packet.Plaintext.Length > 0) atCast.Add(new FlushedPacket(-1, observer, packet.Plaintext.Span[0], packet));
        atCast.AddRange(FlushWindow(match, seconds));
        return atCast;
    }

    // One flush per step: the packets each observer was sent in that step, with the step's number and the observer's index.
    private static List<FlushedPacket> FlushWindow(Match match, double seconds)
    {
        var seen = new List<FlushedPacket>();
        var steps = (int)Math.Round(seconds * 30.0);
        for (var step = 0; step < steps; step++)
        {
            match.Clear();
            match.Runtime.Tick(1.0 / 30.0);
            match.Runtime.Delivery.Flush();
            for (var observer = 0; observer < match.Captures.Count; observer++)
                foreach (var packet in match.Captures[observer].Packets)
                    if (packet.Plaintext.Length > 0) seen.Add(new FlushedPacket(step, observer, packet.Plaintext.Span[0], packet));
        }
        return seen;
    }

    private static uint HashAt(PlaintextPacket packet, int offset) =>
        packet.Plaintext.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[offset..]) : 0;

    private static int BuffAddsOf(IEnumerable<FlushedPacket> packets, uint hash) =>
        packets.Count(packet => packet.Opcode == SpellPackets.BuffAddOpcode && HashAt(packet.Packet, 9) == hash);
}
