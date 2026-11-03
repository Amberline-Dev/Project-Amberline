using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// G14: production triggers of the two stock-client packet handlers the server now feeds. A script heal sends
/// UnitApplyHeal (the unit's heal routine); a stack change of a buff the client
/// already holds sends NpcBuffUpdateCount instead of a second activation-sound-replaying add.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const string StackBuff = "EzrealRisingSpellForce";
    private const byte HealOpcode = CombatPackets.UnitApplyHealOpcode;

    [Check("items-live-script-heals-send-unit-apply-heal")]
    public static void ScriptHealsSendUnitApplyHealToObserversThatSeeTheUnit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);

            // A drunk Health Potion heals from Spells/Potion_Internal.luaobj's own one-second
            // BBExecutePeriodically (ExecuteImmediately false): the first buff update after the drink seeds its
            // InstanceVars clock, and each later second runs its BBIncHealth(Owner, +10). Batch 149 step 3: that
            // periodic is the restore now, so the check drives the spell clock and the buff update the way a
            // match's steps do; a coarse TickBuffs alone leaves the script clock standing and heals nothing (the
            // hand-written Period/HpPerTick row it used to catch up in one call is gone with the cast script).
            hero.Inventory = [new(2003, count: 2), null, null, null, null, null];
            hero.MaxHealth = 5000; hero.Health = 4000;
            DrinkPotion(match, 0);
            match.Clear();
            PotionUpdate(runtime, hero, 1.0 / 30);
            PotionUpdate(runtime, hero, 1.0);
            PotionUpdate(runtime, hero, 1.0);
            runtime.Delivery.Flush();
            TestAssert.Near(4020, hero.Health, 1e-9, "two potion ticks restored 10 each");
            foreach (var capture in match.Captures)
            {
                var heals = HandlerPackets(capture, HealOpcode, hero.NetId);
                TestAssert.Equal(2, heals.Length, $"{team}: one announcement per heal tick");
                foreach (var heal in heals)
                {
                    TestAssert.Equal(13, heal.Plaintext.Length);
                    TestAssert.Equal(5000f, BinaryPrimitives.ReadSingleLittleEndian(heal.Plaintext.Span[5..]), "max health");
                    TestAssert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(heal.Plaintext.Span[9..]), "healed amount");
                    TestAssert.Equal(LeaguePacketReliability.Reliable, heal.Reliability);
                }
            }

            // IncHealth sends the heal it was asked for, not the part that fitted under the maximum.
            hero.Health = 4995;
            match.Clear();
            PotionUpdate(runtime, hero, 1.0);
            runtime.Delivery.Flush();
            TestAssert.Near(5000, hero.Health, 1e-9, "clamped at the maximum");
            foreach (var capture in match.Captures)
            {
                var heal = HandlerPackets(capture, HealOpcode, hero.NetId).Single();
                TestAssert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(heal.Plaintext.Span[9..]), "requested amount");
            }

            // A heal that raises nothing (already full) sends nothing: IncHealth returns before the send.
            match.Clear();
            PotionUpdate(runtime, hero, 1.0);
            runtime.Delivery.Flush();
            TestAssert.Equal(0, match.Captures.Sum(capture => HandlerPackets(capture, HealOpcode, hero.NetId).Length),
                "full health");

            // Summoner Heal: a script heal on every recipient, each with its own amount.
            hero.SummonerSpells[0] = "SummonerHeal";
            hero.Health = 1000; ally.MaxHealth = 5000; ally.Health = 1000;
            match.Clear();
            match.Cast(0, 0, hero, summoner: true);
            var selfAmount = (float)(140.0 + 20.0 * Math.Max(1, hero.Level));
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(selfAmount, BinaryPrimitives.ReadSingleLittleEndian(
                    HandlerPackets(capture, HealOpcode, hero.NetId).Single().Plaintext.Span[9..]), "caster heal");
                TestAssert.Equal(selfAmount * 0.5f, BinaryPrimitives.ReadSingleLittleEndian(
                    HandlerPackets(capture, HealOpcode, ally.NetId).Single().Plaintext.Span[9..]), "ally heal");
            }

            // Engine sustain is not a script call: regeneration and the fountain send no announcement.
            hero.MaxHealth = 5000; hero.Health = 100;
            hero.Position = team == Team.Order ? new Position2(26, 264) : new Position2(13933.79, 14170.09);
            match.Runtime.Delivery.Flush();
            match.Clear();
            runtime.Combat.CombatTick(1, regenerate: true);
            TestAssert.True(hero.Health > 100, "regeneration and fountain healed");
            TestAssert.Equal(0, hero.PendingHealAnnouncements.Count, "regeneration is not IncHealth");
            runtime.Delivery.Flush();
            TestAssert.Equal(0, match.Captures.Sum(capture => HandlerPackets(capture, HealOpcode, hero.NetId).Length),
                "nothing was sent");
        }
    }

    [Check("items-live-unit-apply-heal-follows-visibility")]
    public static void UnitApplyHealReachesOnlyObserversThatSeeTheHealedUnit()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0); var enemy = match.Hero(1);
        hero.Inventory = [new(2003, count: 1), null, null, null, null, null];
        hero.MaxHealth = 5000; hero.Health = 4000;
        enemy.Position = new Position2(1500, 13000);
        runtime.Delivery.Flush();
        TestAssert.False(runtime.Session.Participants[1].View.Visible.Contains(hero.NetId), "the enemy lost sight");
        DrinkPotion(match, 0);
        match.Clear();
        // Batch 149 step 3: the heal is Spells/Potion_Internal.luaobj's own one-second periodic (see the
        // other check above), so the clock it tracks is driven here too — one update to seed it and one second
        // on for the single tick this visibility window is about.
        PotionUpdate(runtime, hero, 1.0 / 30);
        PotionUpdate(runtime, hero, 1.0);
        runtime.Delivery.Flush();
        TestAssert.Equal(1, HandlerPackets(match.Captures[0], HealOpcode, hero.NetId).Length, "owner sees its heal");
        TestAssert.Equal(0, HandlerPackets(match.Captures[1], HealOpcode, hero.NetId).Length, "fogged enemy gets none");
    }

    [Check("buffs.live-stack-change-sends-buff-update-count")]
    public static void StackChangeOfAHeldBuffSendsBuffUpdateCount()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Ezreal", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0); var enemy = match.Hero(1);
            var hash = SpellPackets.BuffNameHash(StackBuff);

            match.Clear();
            var buff = Stack(runtime, hero)!;
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var add = capture.Packets.Single(packet => HasBuffAdd(packet, hero.NetId, StackBuff));
                TestAssert.Equal((byte)1, add.Plaintext.Span[7], "the first stack is a normal add");
                TestAssert.Equal(0, UpdateCounts(capture, hero.NetId).Length, "no update for a new instance");
            }

            for (var stacks = 2; stacks <= 5; stacks++)
            {
                match.Clear();
                Stack(runtime, hero);
                runtime.Delivery.Flush();
                TestAssert.Equal(stacks, buff.Stacks);
                for (var seat = 0; seat < match.Captures.Count; seat++)
                {
                    var capture = match.Captures[seat];
                    TestAssert.False(capture.Packets.Any(packet => HasBuffAdd(packet, hero.NetId, StackBuff)),
                        $"{team}/seat {seat}/stack {stacks}: no second add (it would replay the activation sound)");
                    var update = UpdateCounts(capture, hero.NetId).Single();
                    var bytes = update.Plaintext.Span;
                    TestAssert.Equal((byte)buff.Slot, bytes[5], "slot");
                    TestAssert.Equal((byte)stacks, bytes[6], "stack count");
                    TestAssert.Near(buff.ExpiresAt, BinaryPrimitives.ReadSingleLittleEndian(bytes[7..]), 1e-5, "duration");
                    TestAssert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[11..]), "running time");
                    TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]), "caster");
                    var held = runtime.Session.Participants[seat].View.Buffs[(hero.NetId, (byte)buff.Slot)];
                    TestAssert.Equal(hash, held.Hash, "the ledger keeps the held name");
                    TestAssert.Equal((byte)stacks, held.Count, "the ledger follows the update");
                }
            }

            // At the cap the count does not change. Batch 55 (d3ea329): the renewal is the silent 0x32 (BuffReplace: slot @5,
            // duration @10, caster @14), not a second add and not a count update.
            match.Clear();
            Stack(runtime, hero);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(0, UpdateCounts(capture, hero.NetId).Length, "same count is not a count update");
                TestAssert.False(capture.Packets.Any(packet => HasBuffAdd(packet, hero.NetId, StackBuff)),
                    "capped renewal is not a re-add");
                var renewal = capture.Packets.Single(packet => Opcode(packet) == 0x32 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
                    packet.Plaintext.Span[5] == (byte)buff.Slot);
                TestAssert.Near(buff.ExpiresAt, BinaryPrimitives.ReadSingleLittleEndian(renewal.Plaintext.Span[10..]), 1e-5,
                    "capped renewal duration");
                TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(renewal.Plaintext.Span[14..]),
                    "capped renewal caster");
            }
            foreach (var participant in runtime.Session.Participants)
                TestAssert.Equal((byte)5, participant.View.Buffs[(hero.NetId, (byte)buff.Slot)].Count,
                    "the ledger keeps the capped count");

            // Fog re-entry right after delivered updates re-adds nothing: the ledger already matches.
            match.Clear();
            var enemyView = runtime.Session.Participants[1].View;
            enemyView.Visible.Remove(hero.NetId);
            runtime.Delivery.Flush();
            TestAssert.True(enemyView.Visible.Contains(hero.NetId), "the enemy saw the hero again");
            TestAssert.False(match.Captures[1].Packets.Any(packet => HasBuffAdd(packet, hero.NetId, StackBuff)),
                "reveal does not re-add a buff the client holds");
            TestAssert.Equal(0, UpdateCounts(match.Captures[1], hero.NetId).Length, "reveal sends no update either");

            // An observer in fog gets nothing live (a removal still reaches it); its reveal repairs the missed
            // change with the existing snapshot add.
            enemy.Position = new Position2(1500, 13000);
            runtime.Delivery.Flush();
            TestAssert.False(enemyView.Visible.Contains(hero.NetId), "the enemy lost sight");
            runtime.SpellBuffs.Remove(hero, buff.Slot);
            match.Clear();
            Stack(runtime, hero);
            Stack(runtime, hero);
            runtime.Delivery.Flush();
            TestAssert.False(match.Captures[1].Packets.Any(packet => HasBuffAdd(packet, hero.NetId, StackBuff)),
                "no add leaks to the fogged observer");
            TestAssert.Equal(0, UpdateCounts(match.Captures[1], hero.NetId).Length, "no update leaks either");
            // Batch 389: the remove and the new instance's add share the owner's flush while the owner holds the old copy, so the pair reaches it
            // as a renewal of that copy (ReplicationDelivery.CoalescesBuffPair): no add, and count updates that end at the new instance's count.
            TestAssert.Equal(0, match.Captures[0].Packets.Count(packet => HasBuffAdd(packet, hero.NetId, StackBuff)),
                "the owner's held copy is renewed, not re-added");
            var ownerCounts = UpdateCounts(match.Captures[0], hero.NetId);
            TestAssert.True(ownerCounts.Length >= 1 && ownerCounts[^1].Plaintext.Span[6] == 2,
                "and its count updates end at the new instance's 2");
            match.Clear();
            enemy.Position = new Position2(7200, 7000);
            runtime.Delivery.Flush();
            var repair = match.Captures[1].Packets.Single(packet => HasBuffAdd(packet, hero.NetId, StackBuff));
            TestAssert.Equal((byte)2, repair.Plaintext.Span[7], "reveal adds the instance at its current count");
        }
    }

    [Check("items-live-potion-stack-sends-buff-update-count")]
    public static void SecondPotionSendsBuffUpdateCountNotASecondAdd()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        hero.Inventory = [new(2003, count: 2), null, null, null, null, null];
        hero.MaxHealth = 5000; hero.Health = 3000;
        match.Clear();
        DrinkPotion(match, 0);
        foreach (var capture in match.Captures)
        {
            // Batch 357 (restates batch 181): the potion's add and its stack carry its binding RegenerationPotion; Health Potion has no script.
            var add = capture.Packets.Single(packet => HasBuffAdd(packet, hero.NetId, "RegenerationPotion"));
            TestAssert.Equal((byte)1, add.Plaintext.Span[7], "first potion starts the buff at one stack");
        }
        var buff = hero.Buffs.Single(item => item.Name == "RegenerationPotion");
        hero.ItemCooldowns[0] = 0;
        match.Clear();
        DrinkPotion(match, 0);
        TestAssert.Equal(2, buff.Stacks, "second potion stacks");
        foreach (var capture in match.Captures)
        {
            TestAssert.False(capture.Packets.Any(packet => HasBuffAdd(packet, hero.NetId, "RegenerationPotion")),
                "no second add");
            var update = UpdateCounts(capture, hero.NetId).Single();
            TestAssert.Equal((byte)2, update.Plaintext.Span[6], "stack count");
            TestAssert.Equal((byte)buff.Slot, update.Plaintext.Span[5], "slot");
            TestAssert.Near(buff.ExpiresAt, BinaryPrimitives.ReadSingleLittleEndian(update.Plaintext.Span[7..]), 1e-5,
                "duration");
        }
    }

    private static PlaintextPacket[] HandlerPackets(Capture capture, byte opcode, uint netId) =>
        capture.Packets.Where(packet => packet.Plaintext.Length >= 5 && Opcode(packet) == opcode &&
            ReadUInt32(packet, 1) == netId).ToArray();

    private static PlaintextPacket[] UpdateCounts(Capture capture, uint netId) =>
        HandlerPackets(capture, SpellPackets.BuffUpdateCountOpcode, netId);

    /// <summary>
    /// The stack counts one client was told for a buff, in wire order: a 0xBF add carries it at +7, a 0x1F
    /// update at +6 (and names its buff only by slot, so the slot is followed through adds and removals).
    /// </summary>
    private static int[] WireBuffCounts(Capture capture, uint unitNetId, string buffName)
    {
        var hash = SpellPackets.BuffNameHash(buffName);
        var slotHash = new Dictionary<byte, uint>();
        var counts = new List<int>();
        foreach (var packet in capture.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != unitNetId) continue;
            switch (bytes[0])
            {
                case SpellPackets.BuffAddOpcode when bytes.Length >= 25:
                    slotHash[bytes[5]] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
                    if (slotHash[bytes[5]] == hash) counts.Add(bytes[7]);
                    break;
                case SpellPackets.BuffUpdateCountOpcode when bytes.Length >= 19:
                    if (slotHash.TryGetValue(bytes[5], out var held) && held == hash) counts.Add(bytes[6]);
                    break;
                case 0x7F:
                    slotHash.Remove(bytes[5]);
                    break;
            }
        }
        return counts.ToArray();
    }

    private static BuffState? Stack(MatchRuntime runtime, Entity hero) =>
        runtime.SpellBuffs.AddStacking(hero, StackBuff, BuffType.CombatEnhancer, 6.0, hero.NetId, 5, new BuffPayload());

    /// <summary>
    /// One production update of the drinking hero: the spell phase advances the clock the shipped
    /// <c>Spells/Potion_Internal.luaobj</c> periodic tracks (<c>BBExecutePeriodically</c> keeps
    /// <c>LastTimeExecuted</c> in <c>InstanceVars</c> against <c>GetTime</c>), the buff phase runs its
    /// <c>BuffOnUpdateActions</c>. Batch 149 step 3: the potion restore is that periodic's own
    /// <c>BBIncHealth</c> / <c>BBIncPAR</c>, so a buff tick without the spell clock leaves it never due.
    /// </summary>
    private static void PotionUpdate(MatchRuntime runtime, Entity hero, double seconds)
    {
        runtime.Spells.Tick(seconds);
        runtime.Buffs.TickBuffs(hero, seconds);
    }

    /// <summary>Uses inventory slot 0 through the real gameplay dispatch (spell slot 4 is the first item).</summary>
    private static void DrinkPotion(Match match, int seat)
    {
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = 4 << 1;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), match.Hero(seat).NetId);
        var accepted = match.Runtime.Dependencies.Gameplay(new SessionGameplayContext(
            match.Runtime.Session.Participants[seat], match.Hero(seat), 1, request, null, null));
        TestAssert.True(accepted, "potion accepted");
        match.Runtime.Delivery.Flush();
    }
}
