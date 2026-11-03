using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Amberline.Network.Protocol;

namespace Amberline.Tools.Diagnostics;

/// <summary>Violation kinds of the reconnect rules (also the session.jsonl <c>msg</c> values); see docs/testing.md "Reconnect rules".</summary>
public static class ReconnectRuleKinds
{
    public const string Name = "reconnect-name";
    public const string Rank = "reconnect-rank";
    public const string Cooldown = "reconnect-cooldown";
    public const string Targeting = "reconnect-targeting";
    public const string Layer = "reconnect-layer";
    public const string CastBits = "reconnect-cast-bits";
    public const string Known = "reconnect-known";
    public const string Item = "reconnect-item";
    public const string Death = "reconnect-death";
    public const string LayerPop = "character-pop-unknown";
    public const string LayerPush = "character-push-duplicate";
}

/// <summary>A spellbook slot as the packets name it: the summoner book flag and the stored slot (0-3 champion, 4+ items, 42+ extra).</summary>
public readonly record struct BookSlot(bool Summoner, int Slot)
{
    public override string ToString() => (Summoner ? "summoner " : "") + "slot " + Slot;
}

/// <summary>
/// What the server holds for a reconnecting hero at the moment its burst is judged (DIAGNOSTICS: a diagnostic input). Names are the current
/// spell names with whether each equals the name the client seeds natively from its character data (slots 0-3 and the extras 42-49 of
/// <c>CreateHero</c>; the summoner book from the loadout): a seeded name needs no packet, any other must have been sent.
/// </summary>
public sealed record ReconnectTruth(
    uint HeroNetId,
    bool Alive,
    IReadOnlyDictionary<BookSlot, (string Name, bool Seeded)> Names,
    IReadOnlyDictionary<int, int> Ranks,
    IReadOnlyDictionary<BookSlot, double> Cooldowns,
    IReadOnlyDictionary<int, int> Targeting,
    (uint Id, string Skin, bool Spells)? Layer,
    uint CanCastBits1,
    uint CanCastBits2,
    IReadOnlyCollection<uint> KnownCharacters,
    IReadOnlyList<(int ItemId, int Count, int Charges)?> Inventory,
    int SkillPoints,
    IReadOnlyList<(uint Id, string Skin, bool Spells)>? BuriedLayers = null);

/// <summary>What <see cref="PacketStreamChecker.JudgeReconnect"/> compared and found.</summary>
public sealed record ReconnectJudgement(int Peer, double T, int NamesChecked, int RanksChecked, int CooldownsChecked, int UnitsChecked,
    int Violations, int ItemsChecked = 0);

public sealed partial class PacketStreamChecker
{
    // Tolerance on a cooldown's time left: the burst carries the value of its own step, the judgement is a step or more later and both
    // sides tick it down, so only a real difference exceeds a step or two.
    private const double CooldownToleranceSeconds = 0.25;

    private int _resets;

    /// <summary>How many times <see cref="ResetPeer"/> replaced a client model.</summary>
    public int PeerResets => _resets;

    /// <summary>
    /// A new stock client process connected on this peer: it starts with no units, no stat blocks and no spellbook state, so the model of the
    /// old client is dropped and the violations already reported for the peer may be reported again for the new one.
    /// </summary>
    public void ResetPeer(int peer, double t)
    {
        if ((uint)peer >= (uint)_peers.Length) throw new ArgumentOutOfRangeException(nameof(peer));
        _peers[peer] = new Peer();
        _reported.RemoveWhere(key => key.Peer == peer);
        _resets++;
        _lastT = Math.Max(_lastT, t);
    }

    /// <summary>Lists a finding the driver made about a peer's connection (not a packet rule): every call is listed, up to the per-kind cap.</summary>
    public void AddFinding(string kind, int peer, double t, uint? netId, string detail)
    {
        if (Admit(kind, peer, 0, netId ?? 0, (int)_totals.GetValueOrDefault(kind)))
            List(kind, new PacketObservation(t, peer, 3, true, ReadOnlyMemory<byte>.Empty), 0, netId, detail);
    }

    // ---- the client's spellbook of one hero, from the packets that write it ----

    private sealed class Book
    {
        public readonly Dictionary<BookSlot, uint> Names = [];
        public readonly Dictionary<BookSlot, double> Expiry = [];
        public readonly Dictionary<int, int> Ranks = [];
        public readonly Dictionary<int, (uint ItemId, int Count, int Charges)> Items = [];
        public int Points = -1;
        public readonly Dictionary<int, int> Targeting = [];
        public readonly Dictionary<uint, (string Skin, bool Spells)> Layers = [];
        public readonly Dictionary<BookSlot, double> NamedAt = [];
        public double ResetAt = double.NegativeInfinity;
        public uint CastBits1, CastBits2;
        public bool CastBitsSeen;
        public bool DiedSeen;
    }

    // The client's SetSpellData writes the slot record's expiry and length to now + StartCooldown (0 in
    // every shipped spell): naming a slot clears its cooldown. The spellbook's Init re-seeds the main book's slots from the new
    // CharacterData, so a layer push or pop that carries spells wipes the main book the same way.
    private static void Wipe(Book book, double t)
    {
        foreach (var key in book.Names.Keys.Where(key => !key.Summoner).ToArray()) book.Names.Remove(key);
        foreach (var key in book.Expiry.Keys.Where(key => !key.Summoner).ToArray()) book.Expiry.Remove(key);
        book.Ranks.Clear();
        book.Targeting.Clear();
        book.ResetAt = t;
    }

    private static Book BookOf(Peer peer, uint netId)
    {
        if (!peer.Units.TryGetValue(netId, out var unit)) peer.Units[netId] = unit = new Unit();
        return unit.Book ??= new Book();
    }

    // Called for every non-0xCC packet, before the catalog bookkeeping returns for an unlisted opcode.
    private void ObserveBook(Peer peer, PacketObservation packet, byte opcode, ReadOnlySpan<byte> bytes)
    {
        switch (opcode)
        {
            case 0x73 when bytes.Length >= 14:
            {
                var book = BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
                var key = new BookSlot(false, bytes[13]);
                book.Names[key] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
                book.NamedAt[key] = packet.T;
                book.Expiry.Remove(key);
                break;
            }
            case 0x35 when SpellPackets.TryReadChangeSlotSpellName(bytes, out var update):
            {
                var book = BookOf(peer, update.NetId);
                var key = new BookSlot(update.IsSummoner, update.Slot);
                book.Names[key] = ProtocolEncoding.HashString(update.SpellName);
                book.NamedAt[key] = packet.T;
                book.Expiry.Remove(key);
                break;
            }
            case 0x8A when bytes.Length >= 10:
            {
                var book = BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
                var key = new BookSlot((bytes[5] & 0x80) != 0, bytes[5] & 0x7F);
                var seconds = BinaryPrimitives.ReadSingleLittleEndian(bytes[6..]);
                if (seconds > 0.0) book.Expiry[key] = packet.T + seconds;
                else book.Expiry.Remove(key);
                break;
            }
            case 0x18 when bytes.Length >= 8:
            {
                var book = BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
                book.Ranks[bytes[5]] = bytes[6];
                book.Points = bytes[7];
                break;
            }
            case 0x56 when bytes.Length >= 12:
                BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])).Items[bytes[5]] =
                    (BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]), bytes[10], bytes[11]);
                break;
            case 0x0B when bytes.Length >= 7:
            {
                // RemoveItemAns: count 0 empties the slot (the burst's form); a count removes that many from the stack.
                var items = BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])).Items;
                if (items.TryGetValue(bytes[5], out var held))
                {
                    if (bytes[6] == 0 || bytes[6] >= held.Count) items.Remove(bytes[5]);
                    else items[bytes[5]] = held with { Count = held.Count - bytes[6] };
                }
                break;
            }
            case 0x1A when bytes.Length >= 7 && (bytes[5] & 0x80) == 0:
                BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])).Targeting[bytes[5]] = bytes[6];
                break;
            case 0x9F when bytes.Length >= 74:
            {
                var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
                var change = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
                var book = BookOf(peer, netId);
                var spells = bytes[9] != 0;
                var skin = Encoding.ASCII.GetString(bytes[10..74]).TrimEnd('\0');
                if (book.Layers.ContainsKey(change) && Admit(ReconnectRuleKinds.LayerPush, packet.Peer, 0x9F, netId, (int)change))
                    List(ReconnectRuleKinds.LayerPush, packet, 0x9F, netId,
                        $"change id {change} is pushed again on {NetIds.Describe(netId)}; the client already holds that layer");
                book.Layers[change] = (skin, spells);
                if (spells) Wipe(book, packet.T);
                break;
            }
            case 0x6A when bytes.Length >= 5:
            {
                var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
                var book = BookOf(peer, netId);
                if (bytes.Length >= 9)
                {
                    var change = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
                    if (!book.Layers.ContainsKey(change) && Admit(ReconnectRuleKinds.LayerPop, packet.Peer, 0x6A, netId, (int)change))
                        List(ReconnectRuleKinds.LayerPop, packet, 0x6A, netId,
                            $"change id {change} is popped on {NetIds.Describe(netId)}, a layer this peer was never given");
                }
                // Batch 256: the client runs both 0x6A handlers for the 9- and the 5-byte form (one clears the stack, the other
                // erases the id), so any pop leaves the unit with no layer.
                if (book.Layers.Values.Any(layer => layer.Spells)) Wipe(book, packet.T);
                book.Layers.Clear();
                break;
            }
            case 0x61 or 0xA6 when bytes.Length >= 5:
                BookOf(peer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])).DiedSeen = true;
                break;
        }
    }

    // mCanCastBits1 and mCanCastBits2 are the hero's client-only replication fields 2 and 3 (WirePacketDecoder, HeroStatsServices).
    private static void ObserveCastBits(Unit unit, ReadOnlySpan<byte> bytes, uint mask, int values)
    {
        var book = unit.Book ??= new Book();
        if ((mask & 4u) != 0)
        {
            book.CastBits1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(values + 4 * BitOperations.PopCount(mask & 3u))..]);
            book.CastBitsSeen = true;
        }
        if ((mask & 8u) != 0)
        {
            book.CastBits2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(values + 4 * BitOperations.PopCount(mask & 7u))..]);
            book.CastBitsSeen = true;
        }
    }

    /// <summary>
    /// Compares what the peer's client holds with what the server holds for the returning hero (call once the burst has been sent, with
    /// the packets already observed). Every difference is a violation of the reconnect rules in docs/testing.md; the counts say how much
    /// was compared, so an empty result can be told from an empty comparison.
    /// </summary>
    public ReconnectJudgement JudgeReconnect(int peer, double t, ReconnectTruth truth)
    {
        ArgumentNullException.ThrowIfNull(truth);
        if ((uint)peer >= (uint)_peers.Length) throw new ArgumentOutOfRangeException(nameof(peer));
        var client = _peers[peer];
        var hero = truth.HeroNetId;
        client.Units.TryGetValue(hero, out var heroUnit);
        var book = heroUnit?.Book ?? new Book();
        var before = _totals.Values.Sum();
        var packet = new PacketObservation(t, peer, 3, true, ReadOnlyMemory<byte>.Empty);
        var names = 0;
        var ranks = 0;
        var cooldowns = 0;

        void Fail(string kind, int variant, string detail)
        {
            if (Admit(kind, peer, 0, hero, variant)) List(kind, packet, 0, hero, detail);
        }

        foreach (var (key, (name, seeded)) in truth.Names)
        {
            names++;
            var expected = ProtocolEncoding.HashString(name);
            if (book.Names.TryGetValue(key, out var held))
            {
                if (held != expected)
                    Fail(ReconnectRuleKinds.Name, key.Slot * 2 + (key.Summoner ? 1 : 0),
                        $"{key}: the client holds 0x{held:X8} (named at t={Fmt(book.NamedAt.GetValueOrDefault(key))}), the server holds '{name}' (0x{expected:X8})");
            }
            else if (!seeded)
                Fail(ReconnectRuleKinds.Name, key.Slot * 2 + (key.Summoner ? 1 : 0),
                    $"{key}: the server holds '{name}', which the client does not seed from its character data, and no name packet reached it" +
                    (book.ResetAt > double.NegativeInfinity ? $" (the main book was wiped by a spell-carrying layer at t={Fmt(book.ResetAt)})" : ""));
        }

        var items = 0;
        for (var slot = 0; slot < truth.Inventory.Count; slot++)
        {
            items++;
            var expected = truth.Inventory[slot];
            var has = book.Items.TryGetValue(slot, out var held);
            if (expected is { } want
                    ? !has || held != ((uint)want.ItemId, want.Count, want.Charges)
                    : has)
                Fail(ReconnectRuleKinds.Item, slot,
                    $"item slot {slot}: the client holds {(has ? $"{held.ItemId} x{held.Count} ({held.Charges} charges)" : "nothing")}, " +
                    $"the server {(expected is { } w ? $"{w.ItemId} x{w.Count} ({w.Charges} charges)" : "nothing")}");
        }
        if (book.Points != truth.SkillPoints)
            Fail(ReconnectRuleKinds.Rank, 100, $"skill points: the client holds {(book.Points < 0 ? "none" : book.Points.ToString())}, the server {truth.SkillPoints}");

        for (var slot = 0; slot < 4; slot++)
        {
            ranks++;
            var expected = truth.Ranks.GetValueOrDefault(slot);
            var held = book.Ranks.GetValueOrDefault(slot);
            if (held != expected)
                Fail(ReconnectRuleKinds.Rank, slot, $"slot {slot}: the client holds rank {held}, the server holds {expected}" +
                    (book.ResetAt > double.NegativeInfinity ? $" (ranks were wiped by a spell-carrying layer at t={Fmt(book.ResetAt)})" : ""));
        }

        var keys = truth.Cooldowns.Keys.Concat(book.Expiry.Keys).Distinct().ToArray();
        foreach (var key in keys)
        {
            cooldowns++;
            var expected = truth.Cooldowns.GetValueOrDefault(key);
            var held = book.Expiry.TryGetValue(key, out var expiry) ? Math.Max(0.0, expiry - t) : 0.0;
            if (Math.Abs(held - expected) > CooldownToleranceSeconds)
            {
                var wiped = book.NamedAt.TryGetValue(key, out var namedAt) && held <= 0.0 ? $" (the slot was named at t={Fmt(namedAt)}, which clears a cooldown)" : "";
                Fail(ReconnectRuleKinds.Cooldown, key.Slot * 2 + (key.Summoner ? 1 : 0),
                    $"{key}: the client has {Fmt(held)} s left, the server {Fmt(expected)} s{wiped}");
            }
        }

        foreach (var slot in truth.Targeting.Keys.Concat(book.Targeting.Keys).Distinct())
            if (book.Targeting.GetValueOrDefault(slot, -1) != truth.Targeting.GetValueOrDefault(slot, -1))
                Fail(ReconnectRuleKinds.Targeting, slot,
                    $"slot {slot}: the client holds targeting type {Describe(book.Targeting.GetValueOrDefault(slot, -1))}, the server {Describe(truth.Targeting.GetValueOrDefault(slot, -1))}");

        // The client's character-data stack holds the layers the server pushed and has not popped, oldest first (Udyr's stances stack): the buried
        // ones, then the top one.
        var layers = (truth.BuriedLayers ?? []).Concat(truth.Layer is { } top ? [top] : []).ToArray();
        foreach (var layer in layers)
            if (!book.Layers.TryGetValue(layer.Id, out var held) || held != (layer.Skin, layer.Spells))
                Fail(ReconnectRuleKinds.Layer, 0,
                    $"the server holds layer {layer.Id} '{layer.Skin}' (spells={layer.Spells}); the client holds {DescribeLayers(book)}");
        if (layers.Length > 0 && book.Layers.Count > layers.Length)
            Fail(ReconnectRuleKinds.Layer, 1, $"the client holds extra layers: {DescribeLayers(book)}; the server only {string.Join(", ", layers.Select(layer => layer.Id))}");
        else if (layers.Length == 0 && book.Layers.Count != 0)
            Fail(ReconnectRuleKinds.Layer, 2, $"the server holds no layer; the client holds {DescribeLayers(book)}");

        if (!book.CastBitsSeen)
            Fail(ReconnectRuleKinds.CastBits, 0, "no mCanCastBits1/2 value reached the client: its replicated bits stay zero and no spell can be cast");
        else
        {
            if (book.CastBits1 != truth.CanCastBits1)
                Fail(ReconnectRuleKinds.CastBits, 1, $"mCanCastBits1: the client holds 0x{book.CastBits1:X8}, the server 0x{truth.CanCastBits1:X8}");
            if (book.CastBits2 != truth.CanCastBits2)
                Fail(ReconnectRuleKinds.CastBits, 2, $"mCanCastBits2: the client holds 0x{book.CastBits2:X8}, the server 0x{truth.CanCastBits2:X8}");
        }

        var units = 0;
        foreach (var known in truth.KnownCharacters.Where(NetIds.IsCharacter))
        {
            units++;
            if (!client.Units.TryGetValue(known, out var unit) || !unit.Created)
                Fail(ReconnectRuleKinds.Known, (int)(known & 0xFFFF),
                    $"the server lists {NetIds.Describe(known)} as known to this seat, but no creation packet reached the new client");
        }

        if (!truth.Alive && !book.DiedSeen)
            Fail(ReconnectRuleKinds.Death, 0, "the hero is dead on the server, but no death packet (0x61/0xA6) reached the new client");

        var violations = (int)(_totals.Values.Sum() - before);
        return new ReconnectJudgement(peer, t, names, ranks, cooldowns, units, violations, items);
    }

    private static string Describe(int type) => type < 0 ? "none" : type.ToString();

    private static string DescribeLayers(Book book) => book.Layers.Count == 0
        ? "none"
        : string.Join(", ", book.Layers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} '{pair.Value.Skin}' (spells={pair.Value.Spells})"));
}
