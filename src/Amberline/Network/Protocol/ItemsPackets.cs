using System.Buffers.Binary;

namespace Amberline.Network.Protocol;

public readonly record struct ShopOpenRequest(uint Header);
public readonly record struct BuyItemRequest(uint SenderNetId, uint ItemId);
public readonly record struct RemoveItemRequest(uint SenderNetId, byte Slot, bool Sell);
public readonly record struct SwapItemRequest(uint SenderNetId, byte SourceSlot, byte DestinationSlot);

public static class ItemsPackets
{
    // Shipped TEMPORARY_ITEM_SLOT_START. Retail's item routine places, uses
    // and removes a useOnBought item; using a visible slot would destroy it.
    public const int TemporaryItemSlot = 7;

    public static ShopOpenRequest ParseShopOpen(ReadOnlySpan<byte> payload)
    {
        Require(payload, 0x60, 5);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]));
    }

    public static BuyItemRequest ParseBuyItem(ReadOnlySpan<byte> payload)
    {
        Require(payload, 0x87, 9);
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]));
    }

    public static RemoveItemRequest ParseRemoveItem(ReadOnlySpan<byte> payload)
    {
        Require(payload, 0x09, 6);
        var packed = payload[5];
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]),
            (byte)(packed & 0x7F),
            (packed & 0x80) != 0);
    }

    public static SwapItemRequest ParseSwapItem(ReadOnlySpan<byte> payload)
    {
        Require(payload, 0x23, 7);
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]),
            payload[5],
            payload[6]);
    }

    public static PlaintextPacket BuyItem(
        uint heroNetId,
        int slot,
        uint itemId,
        int itemsInSlot,
        bool useOnBought)
    {
        using var writer = ProtocolEncoding.Writer(12, out var stream);
        writer.Write((byte)0x72);
        writer.Write(heroNetId);
        writer.Write((byte)slot);
        writer.Write(itemId);
        writer.Write((byte)itemsInSlot);
        writer.Write((byte)(useOnBought ? 1 : 0));
        writer.Flush();
        return Answer(stream, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    public static PlaintextPacket RemoveItem(uint heroNetId, int slot, int count)
    {
        using var writer = ProtocolEncoding.Writer(7, out var stream);
        writer.Write((byte)0x0B);
        writer.Write(heroNetId);
        writer.Write((byte)slot);
        writer.Write((byte)count);
        writer.Flush();
        return Answer(stream, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    public static PlaintextPacket SwapItem(uint heroNetId, int sourceSlot, int destinationSlot)
    {
        using var writer = ProtocolEncoding.Writer(7, out var stream);
        writer.Write((byte)0x44);
        writer.Write(heroNetId);
        writer.Write((byte)sourceSlot);
        writer.Write((byte)destinationSlot);
        writer.Flush();
        return Answer(stream, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    public static PlaintextPacket UseItem(
        uint heroNetId,
        int slot,
        int items,
        int charges)
    {
        using var writer = ProtocolEncoding.Writer(8, out var stream);
        writer.Write((byte)0xA7);
        writer.Write(heroNetId);
        writer.Write((byte)slot);
        writer.Write((byte)items);
        writer.Write((byte)charges);
        writer.Flush();
        return Answer(stream, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    public static PlaintextPacket SetItem(
        uint heroNetId,
        int slot,
        uint itemId,
        int count,
        int charges)
    {
        using var writer = ProtocolEncoding.Writer(12, out var stream);
        writer.Write((byte)0x56);
        writer.Write(heroNetId);
        writer.Write((byte)slot);
        writer.Write(itemId);
        writer.Write((byte)count);
        writer.Write((byte)charges);
        writer.Flush();
        return Answer(stream, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }

    private static PlaintextPacket Answer(
        MemoryStream stream,
        LeaguePacketEntityReference reference)
    {
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.CombatChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable,
            reference.NetId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { reference });
    }

    private static void Require(ReadOnlySpan<byte> payload, byte opcode, int length)
    {
        if (payload.Length != length || payload[0] != opcode)
            throw new ArgumentException($"Expected item packet 0x{opcode:X2} with {length} bytes.", nameof(payload));
    }
}
