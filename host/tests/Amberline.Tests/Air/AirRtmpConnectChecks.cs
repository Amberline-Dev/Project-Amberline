using System.Net;
using System.Net.Sockets;
using System.Text;
using Amberline.Platform;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 942: the stock AIR client sends its RTMP <c>connect</c> right behind the handshake's C2, in the same write, so the platform's
/// RTMP server finds the connect already buffered when it takes the connection over. The Linux spike (batch 941) saw AIR's login hang
/// at "Retrieving data from server": the server sent the three control messages before the connect's <c>_result</c> (49 bytes after
/// S0+S1+S2) and never the <c>_result</c>; Flex's RTMPChannel then tried rtmpt on the same port, which the RTMP server cannot read.
/// </summary>
public static class AirRtmpConnectChecks
{
    private const int Attempts = 300;
    private const int StopAfterMisses = 5;

    /// <summary>
    /// A raw client that writes C2 and the connect together, as AIR does, gets the connect's <c>_result</c> every time on a warm server,
    /// and a connect that carries channel credentials (a reconnect) always reaches the platform's resume.
    /// </summary>
    [Check("server.air-rtmp-connect-sent-with-c2")]
    public static void AConnectSentWithTheLastHandshakePacketIsAlwaysAnswered() => ConnectSentWithC2Async().GetAwaiter().GetResult();

    private static async Task ConnectSentWithC2Async()
    {
        var resumes = 0;
        await using var host = new AirRtmpHost(new IPEndPoint(IPAddress.Loopback, 0), (_, _, _, _) => null, (_, _, _) => false, _ => null,
            _ => { }, TextWriter.Null, resumeSession: (_, _, _) => { Interlocked.Increment(ref resumes); return false; });
        await host.StartAsync();
        var port = host.BoundEndPoint.Port;
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("friend_login:" + new string('a', 64)));
        var (attempts, withCredentials, misses) = (0, 0, new List<string>());
        while (attempts < Attempts && misses.Count < StopAfterMisses)
        {
            var resume = attempts % 2 == 1;
            attempts++;
            if (resume) withCredentials++;
            var (answered, bytes) = await ConnectAsync(port, resume ? credentials : "");
            if (!answered) misses.Add($"attempt {attempts}: {bytes} bytes after S0+S1+S2");
        }
        TestAssert.True(misses.Count == 0, $"{misses.Count} of {attempts} connects got no _result (49 bytes = the three control messages " +
                                           $"queued before it): {string.Join("; ", misses)}");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref resumes) != withCredentials && DateTime.UtcNow < deadline) await Task.Delay(10);
        TestAssert.Equal(withCredentials, Volatile.Read(ref resumes), "every connect with channel credentials reaches the platform's resume");
    }

    /// <summary>One connection: C0+C1, S0+S1+S2, then C2 and the connect in one write; true when the <c>_result</c> arrives within a second.</summary>
    internal static async Task<(bool Answered, int Bytes)> ConnectAsync(int port, string credentials)
    {
        using var client = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        var c01 = new byte[1 + 1536];
        c01[0] = 3;
        Random.Shared.NextBytes(c01.AsSpan(9));
        await stream.WriteAsync(c01, timeout.Token);
        var s012 = new byte[1 + 2 * 1536];
        await stream.ReadExactlyAsync(s012, timeout.Token);
        await stream.WriteAsync(s012.AsMemory(1, 1536).ToArray().Concat(Connect(credentials)).ToArray(), timeout.Token);

        using var answer = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var received = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            while (received.Count < 64 * 1024 && !Answered(received))
            {
                var read = await stream.ReadAsync(buffer, answer.Token);
                if (read == 0) break;
                received.AddRange(buffer[..read]);
            }
        }
        catch (OperationCanceledException) { }
        return (Answered(received), received.Count);
    }

    private static bool Answered(List<byte> received) => received.ToArray().AsSpan().IndexOf("_result"u8) >= 0;

    /// <summary>
    /// The connect as Flex's RTMPChannel.internalConnect calls it (needsConfig, the Flex client id, the channel credentials, the handshake
    /// message; the last one null here), AMF0 on chunk stream 3 at the default chunk size of 128.
    /// </summary>
    private static byte[] Connect(string credentials)
    {
        using var body = new MemoryStream();
        var amf = new AmfWriter(body, new SerializationContext(), ObjectEncoding.Amf0);
        amf.WriteAmf0Item("connect");
        amf.WriteAmf0Item(1.0);
        amf.WriteAmf0Item(new AsObject { { "app", "" }, { "tcUrl", "rtmp://127.0.0.1:2099" }, { "objectEncoding", 3.0 } });
        amf.WriteAmf0Item(false);
        amf.WriteAmf0Item("nil");
        amf.WriteAmf0Item(credentials);
        amf.WriteAmf0Item(null);
        amf.Flush();
        var payload = body.ToArray();
        var message = new List<byte> { 0x03, 0, 0, 0, (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, 0x14, 0, 0, 0, 0 };
        for (var offset = 0; offset < payload.Length; offset += 128)
        {
            if (offset > 0) message.Add(0xC3);
            message.AddRange(new ArraySegment<byte>(payload, offset, Math.Min(128, payload.Length - offset)));
        }
        return message.ToArray();
    }
}
