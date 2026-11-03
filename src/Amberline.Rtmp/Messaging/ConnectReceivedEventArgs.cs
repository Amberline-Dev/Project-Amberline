using System;

namespace RtmpSharp.Messaging
{
    /// <summary>
    /// The optional arguments of an RTMP <c>connect</c> command. Flex's RTMPChannel passes
    /// <c>(needsConfig, flexClientId, credentials, handshake)</c>: the client id it learned on its first connection
    /// ("nil" on the very first) and, once the channel login has succeeded, the channel credentials it will present again
    /// on every reconnect.
    /// </summary>
    public class ConnectReceivedEventArgs : EventArgs
    {
        public readonly string FlexClientId;
        public readonly string Credentials;

        internal ConnectReceivedEventArgs(string flexClientId, string credentials)
        {
            FlexClientId = flexClientId;
            Credentials = credentials;
        }
    }
}
