using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amberline.Network.Transport;

namespace Amberline.Tests.Network;

public static class ProtocolStockHandshakeChecks
{


    private static int SessionBits(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 6)
            throw new ArgumentException("Captured datagram is shorter than the ENet header.", nameof(datagram));
        var peerId = BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]);
        return (peerId >> 12) & 0x03;
    }


}
