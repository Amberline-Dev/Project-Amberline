using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using static Amberline.Tests.BbLineRuleChecks;
using static Amberline.Tests.BbSlotChecks;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, script cooldowns on the wire (19 October 2026, fourth chunk): the <c>0x8A</c> cooldown packet of a hero's extra slot
/// (the client's record wants it, a minion's slot is never drawn and gets none), and the reconnect burst
/// (<c>MatchRuntimeNetwork.BuildReconnectState</c>, after <c>SpawnReconnect</c> and the observer flush) replaying what scripts keep on the hero: slot names,
/// targeting types, champion and extra-slot cooldowns and a character layer the flush did not push. Packet layouts are the encoders' in
/// <c>SpellPackets</c>: <c>0x8A</c> unit, slot byte (bit 7 = summoner book), float; <c>0x1A</c> unit, slot byte, type; <c>0x9F</c> unit, change id, spells flag, name.
/// </summary>
public static class BbScriptCooldownChecks
{
    private static byte Op(PlaintextPacket packet) => packet.Plaintext.Span[0];

    private static float SetCooldownSeconds(IEnumerable<PlaintextPacket> packets, int slot) =>
        BitConverter.ToSingle(packets.Single(packet => Op(packet) == 0x8A && packet.Plaintext.Span[5] == slot).Plaintext.Span[6..10]);

    // ------------------------------------------------------------------------------------------------------------
    // The 0x8A of a script child
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-hero-extra-slot-child-cooldown-is-sent-and-a-minions-is-not")]
    public static void AHeroesExtraSlotCooldownReachesTheClientAndAMinionsDoesNot()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = probe.Hero;
        int Sent(string unit, int slot) => match.Log.Cooldowns.Count(line => line.Contains($" {unit} slot={slot} ", StringComparison.Ordinal));
        probe.Run(null, SetSpell("Starcall", 0, slotType: 2));
        var cooldown = GameData.Default.Spell("Starcall", 5).Cooldowns[0] * (1.0 + hero.CooldownMod);
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(1, Sent(match.Champion, 42), "the hero's extra slot 42 got its cooldown (stored slot 42, no summoner bit)");
        TestAssert.True(match.Log.Cooldowns[^1].Contains("summoner=False", StringComparison.Ordinal), "from the champion book");
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(1, Sent(match.Champion, 42), "a refused cast sends nothing");
        // Cooldowns disabled after a running one: the client's record is cleared.
        hero.CooldownsDisabled = true;
        hero.ScriptSlotReadyAt[42] = match.Runtime.Spells.CurrentTime;
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(2, Sent(match.Champion, 42), "the cleared entry is sent as 0");
        hero.CooldownsDisabled = false;
        TestAssert.True(cooldown > 1.0, "Starcall has a cooldown to send");

        // A minion's slot: nothing in the client draws it, and a summoner-book flag would reach a book only heroes have.
        var dummy = BbM3.Enemy(match, "TestCube", new Position2(7300, 7100), UnitType.Minion, Team.Order);
        dummy.ActiveSpellNames.Add("Starcall");
        var before = match.Log.Cooldowns.Count;
        probe.Run(dummy, CheckedChild("Target", 0, 0));
        TestAssert.True(dummy.ScriptSlotReadyAt.ContainsKey(0), "the minion's cooldown runs on the server");
        TestAssert.Equal(before, match.Log.Cooldowns.Count, "and no packet went out for it");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The reconnect burst
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-reconnect-burst-replays-the-slot-names-types-cooldowns-and-layer-a-script-set")]
    public static void TheReconnectBurstReplaysTheScriptsSlotState()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = probe.Hero;
        var runtime = match.Runtime;
        // What scripts did to the hero before the drop: an extra slot renamed, W re-typed as a ground cast, E sealed, Q cooling down, an extra-slot
        // child on its cooldown, and a character layer pushed (without spells, so the champion names stay as they are).
        probe.Run(null, SetSpell("Starcall", 0, slotType: 2), SetTargeting(1, 6), Seal(2, true), Push("Nidalee_Cougar", spells: false, "Layer"));
        hero.SpellCooldowns[0] = 7.0;
        probe.Run(null, CheckedChild("Owner", 0, 2));
        var extraLeft = hero.ScriptSlotReadyAt[42] - runtime.Spells.CurrentTime;
        TestAssert.True(extraLeft > 1.0, "the extra-slot child is cooling down");
        TestAssert.Equal(uint.MaxValue & ~4u, runtime.HeroStats.Snapshot(hero).CanCastBits1, "slot 2's cast bit is clear, and rides the stats block");

        var participant = runtime.Session.Participants[0];
        using var network = MatchRuntimeNetwork.CreateEphemeral(runtime);
        // A dead owner is not part of the flush's hydration: its view holds no push yet.
        participant.View.CharacterData.Clear();
        var packets = network.BuildReconnectState(participant).ToList();

        var layer = packets.Single(packet => Op(packet) == 0x9F);
        TestAssert.Equal(hero.ScriptCharacterData!.Id, BitConverter.ToUInt32(layer.Plaintext.Span[5..9]), "the layer comes back under its own change id");
        TestAssert.Equal((byte)0, layer.Plaintext.Span[9], "without spells");
        TestAssert.Equal("Nidalee_Cougar", System.Text.Encoding.ASCII.GetString(layer.Plaintext.Span[10..74]).TrimEnd('\0'), "and its model");
        TestAssert.True(participant.View.CharacterData.Contains((hero.NetId, hero.ScriptCharacterData.Id)), "recorded in the view's ledger");

        TestAssert.True(packets.Any(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
                                              !update.IsSummoner && update.Slot == 42 && update.SpellName == "Starcall"), "the renamed extra slot (0x35)");
        var type = packets.Single(packet => Op(packet) == 0x1A);
        TestAssert.Equal((byte)1, type.Plaintext.Span[5], "the re-typed slot");
        TestAssert.Equal((byte)6, type.Plaintext.Span[6], "and its targeting type");
        TestAssert.Near(7.0, SetCooldownSeconds(packets, 0), 1e-3, "Q's cooldown");
        TestAssert.Near(extraLeft, SetCooldownSeconds(packets, 42), 1e-3, "the extra slot's time left");

        var firstName = packets.FindIndex(packet => Op(packet) == 0x73);
        TestAssert.True(packets.FindIndex(packet => Op(packet) == 0x9F) < firstName, "the layer precedes the names it may re-initialise");
        TestAssert.True(packets.FindIndex(packet => Op(packet) == 0x8A && packet.Plaintext.Span[5] == 42) >
                        packets.FindIndex(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) && update.Slot == 42),
            "and the extra slot's cooldown follows its name, which resets the record");

        // A second burst for the same view repeats no push (the ledger already holds it) and drops an elapsed cooldown.
        hero.ScriptSlotReadyAt[42] = runtime.Spells.CurrentTime - 1.0;
        var again = network.BuildReconnectState(participant).ToList();
        TestAssert.False(again.Any(packet => Op(packet) == 0x9F), "no second push");
        TestAssert.False(again.Any(packet => Op(packet) == 0x8A && packet.Plaintext.Span[5] == 42), "and no cooldown for a slot that is ready");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }
}
