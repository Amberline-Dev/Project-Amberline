using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// Batch 95 (W20e): the headless match's seat reconnect (<c>--reconnect-seat</c>) and the reconnect rules of <see cref="PacketStreamChecker"/>.
/// The run checks play short real matches on the virtual clock into temp folders (serial, like the other match checks); the rule checks feed
/// the checker packets built by the real encoders. Written 19 October 2026, not run (owner rule).
/// </summary>
public static class HeadlessReconnectChecks
{
    private static readonly uint Hero = NetIdAllocator.RangeFor(UnitType.Hero).Base;

    private static PacketObservation Seen(double t, PlaintextPacket packet) =>
        new(t, 0, packet.Channel, packet.Reliability == LeaguePacketReliability.Reliable, packet.Plaintext);

    private static ReconnectTruth Truth(
        IReadOnlyDictionary<BookSlot, (string, bool)>? names = null, IReadOnlyDictionary<BookSlot, double>? cooldowns = null,
        (uint, string, bool)? layer = null, bool alive = true) =>
        new(Hero, alive, names ?? new Dictionary<BookSlot, (string, bool)>(), new Dictionary<int, int>(),
            cooldowns ?? new Dictionary<BookSlot, double>(), new Dictionary<int, int>(), layer, uint.MaxValue, uint.MaxValue, [], [], 0);

    // The castbits are not sent in these streams: a reconnect-cast-bits violation is expected and ignored by the asserts below.
    private static long Count(PacketStreamChecker checker, string kind) => checker.TotalsByKind.GetValueOrDefault(kind);

    // ------------------------------------------------------------------------------------------------------------
    // The reconnect rules
    // ------------------------------------------------------------------------------------------------------------

    [Check("packet-rules.reconnect-judge-accepts-names-then-cooldowns-then-layer-order")]
    public static void AnOrderedBurstIsAccepted()
    {
        var checker = new PacketStreamChecker(1);
        checker.ResetPeer(0, 0.0);
        // Layer with spells first (it re-initialises the book), then the name, the rank and the cooldown: what BuildReconnectState sends.
        checker.Observe(Seen(1.0, SpellPackets.PushCharacterData(Hero, 7, "Nidalee_Cougar", useSpells: true)));
        checker.Observe(Seen(1.0, SpellPackets.SetSpellData(Hero, ProtocolEncoding.HashString("Starcall"), 0)));
        checker.Observe(Seen(1.0, SpellPackets.SetCooldown(Hero, 0, 6.0f)));
        var truth = Truth(new Dictionary<BookSlot, (string, bool)> { [new BookSlot(false, 0)] = ("Starcall", false) },
            new Dictionary<BookSlot, double> { [new BookSlot(false, 0)] = 5.5 }, (7u, "Nidalee_Cougar", true));
        var judged = checker.JudgeReconnect(0, 1.5, truth);
        TestAssert.Equal(0L, Count(checker, ReconnectRuleKinds.Name), "the name is held");
        TestAssert.Equal(0L, Count(checker, ReconnectRuleKinds.Cooldown), "and so is the cooldown, 0.5 s older");
        TestAssert.Equal(0L, Count(checker, ReconnectRuleKinds.Layer), "and the layer");
        TestAssert.Equal(1, judged.NamesChecked, "one name was compared");
        TestAssert.Equal(1, judged.CooldownsChecked, "one cooldown was compared");
    }

    [Check("packet-rules.reconnect-judge-finds-a-cooldown-cleared-by-a-later-name")]
    public static void ANameAfterACooldownClearsIt()
    {
        var checker = new PacketStreamChecker(1);
        checker.ResetPeer(0, 0.0);
        checker.Observe(Seen(1.0, SpellPackets.SetCooldown(Hero, 0, 6.0f)));
        checker.Observe(Seen(1.0, SpellPackets.SetSpellData(Hero, ProtocolEncoding.HashString("Starcall"), 0)));
        // The client's spell data setter sets the expiry to now + StartCooldown (0): the cooldown the server sent first is gone.
        checker.JudgeReconnect(0, 1.5, Truth(cooldowns: new Dictionary<BookSlot, double> { [new BookSlot(false, 0)] = 5.5 }));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.Cooldown), "the cleared cooldown is reported");
        TestAssert.True(checker.Finish().Any(row => row.Kind == ReconnectRuleKinds.Cooldown && row.Detail.Contains("clears a cooldown", StringComparison.Ordinal)),
            "and says why");
    }

    [Check("packet-rules.reconnect-judge-finds-a-name-wiped-by-a-later-layer")]
    public static void ANameBeforeASpellCarryingLayerIsWiped()
    {
        var checker = new PacketStreamChecker(1);
        checker.ResetPeer(0, 0.0);
        checker.Observe(Seen(1.0, SpellPackets.ChangeSlotSpellName(Hero, 44, "SummonerHeal", isSummoner: false)));
        checker.Observe(Seen(1.0, SpellPackets.PushCharacterData(Hero, 7, "Nidalee_Cougar", useSpells: true)));
        checker.JudgeReconnect(0, 1.5, Truth(new Dictionary<BookSlot, (string, bool)> { [new BookSlot(false, 44)] = ("SummonerHeal", false) },
            layer: (7u, "Nidalee_Cougar", true)));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.Name), "the name the layer wiped is reported");
        TestAssert.Equal(0L, Count(checker, ReconnectRuleKinds.Layer), "the layer itself is held");
        // A layer without spells wipes nothing.
        var kept = new PacketStreamChecker(1);
        kept.ResetPeer(0, 0.0);
        kept.Observe(Seen(1.0, SpellPackets.ChangeSlotSpellName(Hero, 44, "SummonerHeal", isSummoner: false)));
        kept.Observe(Seen(1.0, SpellPackets.PushCharacterData(Hero, 7, "Nidalee_Cougar", useSpells: false)));
        kept.JudgeReconnect(0, 1.5, Truth(new Dictionary<BookSlot, (string, bool)> { [new BookSlot(false, 44)] = ("SummonerHeal", false) },
            layer: (7u, "Nidalee_Cougar", false)));
        TestAssert.Equal(0L, Count(kept, ReconnectRuleKinds.Name), "a layer that carries no spells leaves the book alone");
    }

    [Check("packet-rules.reconnect-judge-finds-layers-popped-unknown-pushed-twice-and-a-missing-death")]
    public static void LayerAndDeathRules()
    {
        var checker = new PacketStreamChecker(1);
        checker.ResetPeer(0, 0.0);
        checker.Observe(Seen(1.0, SpellPackets.PopCharacterData(Hero, 99)));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.LayerPop), "a pop of a layer the new client never got");
        checker.Observe(Seen(1.1, SpellPackets.PushCharacterData(Hero, 7, "Nidalee_Cougar")));
        checker.Observe(Seen(1.2, SpellPackets.PushCharacterData(Hero, 7, "Nidalee_Cougar")));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.LayerPush), "the same change id pushed twice");
        // Batch 256: any 0x6A clears the client's whole stack (both handlers run), so a layer pushed again after the pop of another is no repeat.
        var cleared = new PacketStreamChecker(1);
        cleared.ResetPeer(0, 0.0);
        cleared.Observe(Seen(1.0, SpellPackets.PushCharacterData(Hero, 8, "OriannaNoBall")));
        cleared.Observe(Seen(1.0, SpellPackets.PushCharacterData(Hero, 9, "Udyr")));
        cleared.Observe(Seen(1.1, SpellPackets.PopCharacterData(Hero, 9)));
        cleared.Observe(Seen(1.1, SpellPackets.PushCharacterData(Hero, 8, "OriannaNoBall")));
        TestAssert.Equal(0L, Count(cleared, ReconnectRuleKinds.LayerPush), "a pop leaves no layer, so the live one may be pushed again");
        checker.JudgeReconnect(0, 1.5, Truth(alive: false, layer: (7u, "Nidalee_Cougar", false)));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.Death), "a dead hero with no death packet");
        var dead = new PacketStreamChecker(1);
        dead.ResetPeer(0, 0.0);
        dead.Observe(Seen(1.0, CombatPackets.NpcDie(Hero, Hero, 8.0f, opcode: 0x61)));
        dead.JudgeReconnect(0, 1.5, Truth(alive: false));
        TestAssert.Equal(0L, Count(dead, ReconnectRuleKinds.Death), "the burst's 0x61 counts");
    }

    [Check("packet-rules.reconnect-reset-forgets-the-old-client")]
    public static void AResetPeerStartsEmpty()
    {
        var checker = new PacketStreamChecker(1);
        checker.Observe(Seen(1.0, SpellPackets.SetCooldown(Hero, 0, 60.0f)));
        checker.ResetPeer(0, 2.0);
        TestAssert.Equal(1, checker.PeerResets, "one restart was counted");
        // The new client has no cooldown: a judgement that expects none passes, and one that expects the old value fails.
        checker.JudgeReconnect(0, 3.0, Truth());
        TestAssert.Equal(0L, Count(checker, ReconnectRuleKinds.Cooldown), "nothing carried over from the old client");
        checker.JudgeReconnect(0, 3.0, Truth(cooldowns: new Dictionary<BookSlot, double> { [new BookSlot(false, 0)] = 58.0 }));
        TestAssert.Equal(1L, Count(checker, ReconnectRuleKinds.Cooldown), "a cooldown the server holds and the new client was not given");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The headless seat reconnect
    // ------------------------------------------------------------------------------------------------------------

    [Check("tools.match.reconnect-options-parse", Serial = true)]
    public static void TheReconnectOptionsParse()
    {
        var options = HeadlessMatch.Parse(["--seed", "3", "--reconnect-seat", "4", "--reconnect-at", "120", "--reconnect-when", "fight",
            "--drop-seconds", "2", "--load-seconds", "6", "--reconnect-prime", "--reconnect-seat", "5", "--reconnect-at", "90"]);
        TestAssert.Equal(2, options.Reconnects.Count, "one plan per seat");
        TestAssert.Equal(new ReconnectPlan(4, 120.0, 2.0, 6.0, "fight", true), options.Reconnects[0], "the flags after a seat belong to it");
        TestAssert.Equal(new ReconnectPlan(5, 90.0), options.Reconnects[1], "and the next seat starts from the defaults");
        TestAssert.Equal(0, HeadlessMatch.Parse(["--seed", "3"]).Reconnects.Count, "an ordinary match has none");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "3", "--reconnect-seat", "4"]), "a seat needs a time");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "3", "--reconnect-at", "60"]), "a time needs a seat");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "3", "--reconnect-seat", "10", "--reconnect-at", "60"]), "seats are 0 to 9");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "3", "--reconnect-seat", "1", "--reconnect-at", "60", "--reconnect-when", "late"]), "a known trigger");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "3", "--reconnect-seat", "1", "--reconnect-at", "60", "--reconnect-seat", "1", "--reconnect-at", "90"]),
            "a seat reconnects once");
    }

    [Check("tools.match.reconnect-seats-come-back-through-the-production-path-clean", Serial = true)]
    public static void ReconnectingSeatsAreJudgedClean()
    {
        var root = TempRoot();
        try
        {
            var options = new HeadlessMatchOptions(20261019, Path.Combine(root, "m"), 4.0)
            {
                Reconnects =
                [
                    new ReconnectPlan(0, 60.0, 2.0, 3.0, "any", Prime: true),
                    new ReconnectPlan(5, 80.0), // the default 10 s drop and 15 s load of a stock client
                ],
            };
            var report = HeadlessMatch.Run(options);
            TestAssert.Equal(2, report.Reconnects!.Count, "both plans are reported");
            foreach (var reconnect in report.Reconnects)
            {
                TestAssert.Equal("clean", reconnect.Outcome, $"seat {reconnect.Seat}: {string.Join("; ", reconnect.Findings)}");
                TestAssert.True(reconnect.LoadedAt >= reconnect.ConnectedAt + options.Reconnects[reconnect.Seat == 0 ? 0 : 1].LoadSeconds - 0.1,
                    "the 0xC6 comes after the load time");
                TestAssert.True(reconnect.SpawnedAt >= reconnect.LoadedAt, "and the spawn burst after the 0xC6, never before");
                TestAssert.True(reconnect.BurstPackets > 0 && reconnect.Judgement is { ItemsChecked: > 0, UnitsChecked: > 0 }, "a burst was sent and judged");
            }
            TestAssert.Equal(6, report.Reconnects[0].Judgement!.CooldownsChecked, "the primed seat's four slots, summoner and extra-slot cooldowns were compared");
            TestAssert.Equal(0L, report.PacketViolations, "the checker finds nothing in the whole stream");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.reconnect-checker-finds-a-damaged-burst", Serial = true)]
    public static void ADamagedBurstIsReported()
    {
        var root = TempRoot();
        try
        {
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(20261019, Path.Combine(root, "m"), 3.0)
            {
                Reconnects =
                [
                    new ReconnectPlan(0, 60.0, 2.0, 3.0, "any", Prime: true, Fault: "no-cooldowns"),
                    new ReconnectPlan(1, 60.0, 2.0, 3.0, "any", Prime: true, Fault: "layer-last"),
                    new ReconnectPlan(2, 60.0, 2.0, 3.0, "any", Prime: true, Fault: "no-names"),
                ],
            });
            TestAssert.True(report.Reconnects!.All(reconnect => reconnect.Outcome == "violations"), "every damaged burst is reported");
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            foreach (var kind in new[] { ReconnectRuleKinds.Cooldown, ReconnectRuleKinds.Rank, ReconnectRuleKinds.Name })
                TestAssert.True(log.Contains($"\"msg\":\"{kind}\"", StringComparison.Ordinal), $"{kind} is in the log");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "amberline-reconnect-" + Guid.NewGuid().ToString("N"));
}
