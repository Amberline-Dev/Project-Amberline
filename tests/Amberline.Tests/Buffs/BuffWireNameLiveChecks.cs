using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;

namespace Amberline.Tests;

/// <summary>
/// Batch 181, restated by batch 357: a buff's add and remove carry the binding stem (GameData.ClientBuffName); a script's BuffName is never
/// sent. A count update carries no name, and a reconnecting client's snapshot carries the same hash as the add. Class a (AbsoluteZero),
/// class b (Empower: EmpowerCleave is not the wire name) and class d (LeblancPassive) all send their binding name.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("buffs.wire-name.class-a-b-d-add-update-remove-and-reconnect")]
    public static void BuffWireNameIsTheShippedNameThroughAddUpdateRemoveAndReconnect()
    {
        var data = GameData.Default;
        TestAssert.Equal("AbsoluteZero", data.ClientBuffName("AbsoluteZero"), "class a sends its binding name: Absolute Zero has no script (batch 357; batch 181 sent the BuffName)");
        TestAssert.Equal("Empower", data.ClientBuffName("Empower"), "class b sends the binding stem (Batch 357: binding stem; its BuffName EmpowerCleave is not sent)");
        TestAssert.Equal("LeblancPassive", data.ClientBuffName("LeblancPassive"), "class d sends the binding name");
        TestAssert.Equal("AtmasImpaler", data.ClientBuffName("AtmasImpaler"), "class c sends the binding name");
        TestAssert.Equal("Stun", data.ClientBuffName("Stun"), "a buff with no script name sends its own");

        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var participant = match.Runtime.Session.Participants[0];
        var cases = new (string Binding, string BuffName, string Wire)[]
        {
            ("AbsoluteZero", "Absolute Zero", "AbsoluteZero"),
            ("Empower", "EmpowerCleave", "Empower"),
            ("LeblancPassive", "LeblancNoxianCruelty", "LeblancPassive"),
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var (binding, _, wire) = cases[index];
            var hash = SpellPackets.BuffNameHash(wire);
            var payload = new BuffPayload();

            match.Clear();
            var buff = match.Runtime.Buffs.AddBuff(hero, binding, BuffType.Internal, 30.0, hero.NetId, payload)
                ?? throw new InvalidOperationException($"{binding} was refused");
            match.Runtime.Delivery.Flush();
            var adds = match.Captures[0].Packets.Where(packet => BuffPacketAt(packet, SpellPackets.BuffAddOpcode, hero.NetId, buff.Slot)).ToList();
            TestAssert.Equal(1, adds.Count, $"{binding}: one add for the holder's slot");
            TestAssert.Equal(hash, BinaryPrimitives.ReadUInt32LittleEndian(adds[0].Plaintext.Span[9..]), $"{binding}: the add carries {wire}");

            match.Clear();
            match.Runtime.Buffs.AddStackingBuff(hero, binding, BuffType.Internal, 30.0, hero.NetId, 2, payload);
            match.Runtime.Delivery.Flush();
            var counts = match.Captures[0].Packets.Where(packet => BuffPacketAt(packet, 0x1F, hero.NetId, buff.Slot)).ToList();
            TestAssert.Equal(1, counts.Count, $"{binding}: the count update names the same slot");
            TestAssert.Equal(0, match.Captures[0].Packets.Count(packet => packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode),
                $"{binding}: a count update is not a new add");

            match.Clear();
            TestAssert.True(match.Runtime.Buffs.RemoveBuff(hero, buff.Slot), $"{binding}: removed");
            match.Runtime.Delivery.Flush();
            var removes = match.Captures[0].Packets.Where(packet => BuffPacketAt(packet, 0x7F, hero.NetId, buff.Slot)).ToList();
            TestAssert.Equal(1, removes.Count, $"{binding}: one remove for the holder's slot");
            TestAssert.Equal(hash, BinaryPrimitives.ReadUInt32LittleEndian(removes[0].Plaintext.Span[6..]), $"{binding}: the remove carries {wire}");

            var again = match.Runtime.Buffs.AddBuff(hero, binding, BuffType.Internal, 30.0, hero.NetId, payload)
                ?? throw new InvalidOperationException($"{binding} was refused on the second add");
            var late = new Capture();
            match.Captures.Add(late);
            // A new observer per case with a fresh view (the seat's first observer keeps id 1 and its view): the reconnecting client
            // sees the hero for the first time, so the delivery sends its buff snapshot.
            match.Runtime.Delivery.AddObserver(new ReplicationObserver((ulong)(100 + index), participant.Team, hero.NetId, late,
                ready: true));
            match.Runtime.Delivery.Flush();
            var snapshot = late.Packets.Where(packet => BuffPacketAt(packet, SpellPackets.BuffAddOpcode, hero.NetId, again.Slot)).ToList();
            TestAssert.True(snapshot.Count >= 1, $"{binding}: a reconnecting client is sent the buff");
            TestAssert.True(snapshot.All(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == hash),
                $"{binding}: the reconnect carries {wire} too");
            match.Runtime.Buffs.RemoveBuff(hero, again.Slot);
        }
    }

    /// <summary>
    /// Batch 412 (sound events, findings 119 and 41): the stock client composes a shipped typed event, <c>&lt;buff&gt;/OnBuffActivate</c> or
    /// <c>&lt;buff&gt;/OnBuffDeactivate</c>, from the name a 0xBF add or a 0x7F remove carries, so an event plays only when its packets reach
    /// the client. Twitch's stealth (HideInShadows: shipped <c>hideinshadows/OnBuffDeactivate</c>) must be removed on the wire when the stealth
    /// ends, by its timer and by an attack launch. The caster's and the enemy's captures each get the add and the remove.
    /// </summary>
    [Check("buffs.wire-sound-events.twitch-stealth-add-and-removal-reach-both-clients")]
    public static void TwitchStealthAddAndRemovalReachBothClients()
    {
        using var match = new Match("Twitch", Team.Order, enemyChampion: "Ezreal");
        var hero = match.Hero(0);
        var foe = match.Hero(1);
        const string stealth = "HideInShadows";
        // Ambush (slot 0): a 0.25 s cast, a 1.5 s fade, then the stealth, which the timer ends after ten seconds.
        match.Cast(0, 0, hero);
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, stealth) is not null, 3.0), "the stealth starts");
        var slot = WireBuffNamed(hero, stealth)!.Slot;
        for (var seat = 0; seat < 2; seat++)
        {
            var adds = WireBuffCount(match, seat, SpellPackets.BuffAddOpcode, hero, slot, stealth, 9);
            TestAssert.Equal(1, adds, $"seat {seat}: one add of {stealth} at slot {slot} when the stealth starts (got {adds})");
        }
        match.Clear();
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, stealth) is null, 13.0), "the stealth ends by its timer");
        WireSettle(match, 0.2);
        for (var seat = 0; seat < 2; seat++)
        {
            var removes = WireBuffCount(match, seat, 0x7F, hero, slot, stealth, 6);
            TestAssert.Equal(1, removes, $"seat {seat}: one remove of {stealth} at slot {slot} when the timer ends the stealth (got {removes})");
        }

        // The same stealth ended by an attack launch (BuffOnLaunchAttack).
        hero.SpellCooldowns[0] = 0.0;
        match.Cast(0, 0, hero);
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, stealth) is not null, 3.0), "stealthed again");
        slot = WireBuffNamed(hero, stealth)!.Slot;
        match.Clear();
        match.Attack(0, foe);
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, stealth) is null, 3.0), "an attack ends the stealth");
        WireSettle(match, 0.2);
        for (var seat = 0; seat < 2; seat++)
        {
            var removes = WireBuffCount(match, seat, 0x7F, hero, slot, stealth, 6);
            TestAssert.Equal(1, removes, $"seat {seat}: one remove of {stealth} at slot {slot} when an attack ends the stealth (got {removes})");
        }
    }

    /// <summary>
    /// Batch 412 (sound events, finding 41): Kennen's Slicing Maelstrom adds KennenShurikenStorm for three seconds, and the shipped
    /// <c>kennenshurikenstorm/OnBuffActivate</c> and <c>/OnBuffDeactivate</c> are composed from that add and that remove. Both must reach the
    /// caster's and the enemy's client at the start and at the end of the storm.
    /// </summary>
    [Check("buffs.wire-sound-events.kennen-shurikenstorm-add-and-removal-reach-both-clients")]
    public static void KennenShurikenStormAddAndRemovalReachBothClients()
    {
        using var match = new Match("Kennen", Team.Order);
        var hero = match.Hero(0);
        const string storm = "KennenShurikenStorm";
        // Slicing Maelstrom (slot 3): a 0.25 s cast, then the storm for three seconds.
        match.Cast(0, 3, hero);
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, storm) is not null, 2.0), "the storm is added");
        var slot = WireBuffNamed(hero, storm)!.Slot;
        for (var seat = 0; seat < 2; seat++)
        {
            var adds = WireBuffCount(match, seat, SpellPackets.BuffAddOpcode, hero, slot, storm, 9);
            TestAssert.Equal(1, adds, $"seat {seat}: one add of {storm} at slot {slot} (got {adds})");
        }
        match.Clear();
        TestAssert.True(WireAdvanceUntil(match, () => WireBuffNamed(hero, storm) is null, 5.0), "the storm ends");
        WireSettle(match, 0.2);
        for (var seat = 0; seat < 2; seat++)
        {
            var removes = WireBuffCount(match, seat, 0x7F, hero, slot, storm, 6);
            TestAssert.Equal(1, removes, $"seat {seat}: one remove of {storm} at slot {slot} when the storm ends (got {removes})");
        }
    }

    private static bool WireAdvanceUntil(Match match, Func<bool> done, double maxSeconds)
    {
        for (var elapsed = 0.0; elapsed < maxSeconds && !done(); elapsed += 1.0 / 30.0) match.Runtime.Tick(1.0 / 30.0);
        return done();
    }

    // A removal that expiry holds for a buff with a wire margin goes out one step later (BuffLifecycle.EndTick), so the count waits for it.
    private static void WireSettle(Match match, double seconds) => WireAdvanceUntil(match, () => false, seconds);

    private static BuffState? WireBuffNamed(Entity unit, string name) => unit.Buffs.FirstOrDefault(buff => buff.Name == name);

    // The 0xBF add (name at offset 9) or 0x7F remove (name at offset 6) of one buff name at one slot, on one seat's capture.
    private static int WireBuffCount(Match match, int seat, byte opcode, Entity holder, int slot, string name, int nameOffset)
    {
        var hash = SpellPackets.BuffNameHash(name);
        return match.Captures[seat].Packets.Count(packet => BuffPacketAt(packet, opcode, holder.NetId, slot) &&
            packet.Plaintext.Length >= nameOffset + 4 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[nameOffset..]) == hash);
    }

    private static bool BuffPacketAt(PlaintextPacket packet, byte opcode, uint netId, int slot)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 6 && bytes[0] == opcode && BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == netId && bytes[5] == slot;
    }
}
