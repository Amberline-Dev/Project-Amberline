#include <string.h>

#define AMBERLINE_ENET_BRIDGE_BUILD 1
#include "amberline_enet_bridge.h"
#include "enet/enet.h"

#if defined(_MSC_VER)
__declspec(thread) static char amberline_enet_error[256];
#else
static _Thread_local char amberline_enet_error[256];
#endif

static void
amberline_enet_set_error (const char * message)
{
    if (message == NULL)
      message = "native ENet error";

    strncpy (amberline_enet_error, message, sizeof (amberline_enet_error) - 1);
    amberline_enet_error [sizeof (amberline_enet_error) - 1] = '\0';
}

const char *
amberline_enet_last_error (void)
{
    return amberline_enet_error;
}

int
amberline_enet_initialize (void)
{
    int result = enet_initialize ();
    if (result < 0)
      amberline_enet_set_error ("enet_initialize failed");
    return result;
}

void
amberline_enet_deinitialize (void)
{
    enet_deinitialize ();
}

uint32_t
amberline_enet_linked_version (void)
{
    return (uint32_t) enet_linked_version ();
}

static LeagueEnetHost *
amberline_enet_host_create_with_limits (const char * bind_host,
                                     uint16_t port,
                                     size_t peer_limit,
                                     size_t channel_limit,
                                     uint32_t incoming_bandwidth,
                                     uint32_t outgoing_bandwidth,
                                     int enable_crc32,
                                     size_t maximum_packet_size,
                                     size_t maximum_waiting_data)
{
    ENetAddress address;
    ENetHost * host;

    memset (& address, 0, sizeof (address));
    address.host = ENET_HOST_ANY;
    address.port = port;

    if (bind_host != NULL && bind_host [0] != '\0' &&
        enet_address_set_host (& address, bind_host) < 0)
    {
        amberline_enet_set_error ("could not resolve bind host");
        return NULL;
    }

    host = enet_host_create (& address,
                             peer_limit,
                             channel_limit,
                             incoming_bandwidth,
                             outgoing_bandwidth);
    if (host == NULL)
    {
        amberline_enet_set_error ("enet_host_create failed");
        return NULL;
    }

    if (maximum_packet_size != 0)
      host -> maximumPacketSize = maximum_packet_size;
    if (maximum_waiting_data != 0)
    {
      host -> maximumWaitingData = maximum_waiting_data;
      host -> enforceWaitingDataLimit = 1;
    }

    /* Keep the legacy optional checksum property behind the bridge instead of
       changing ENet's wire code. */
    host -> checksum = enable_crc32 ? enet_crc32 : NULL;
    return (LeagueEnetHost *) host;
}

/* Preserve this seven-argument export for generic and older consumers. */
LeagueEnetHost *
amberline_enet_host_create (const char * bind_host,
                         uint16_t port,
                         size_t peer_limit,
                         size_t channel_limit,
                         uint32_t incoming_bandwidth,
                         uint32_t outgoing_bandwidth,
                         int enable_crc32)
{
    return amberline_enet_host_create_with_limits(bind_host, port, peer_limit,
        channel_limit, incoming_bandwidth, outgoing_bandwidth, enable_crc32, 0, 0);
}

/* A separate symbol is an explicit capability check for bounded hosts. */
LeagueEnetHost *
amberline_enet_host_create_bounded (const char * bind_host,
                                 uint16_t port,
                                 size_t peer_limit,
                                 size_t channel_limit,
                                 uint32_t incoming_bandwidth,
                                 uint32_t outgoing_bandwidth,
                                 int enable_crc32,
                                 size_t maximum_packet_size,
                                 size_t maximum_waiting_data)
{
    return amberline_enet_host_create_with_limits(bind_host, port, peer_limit,
        channel_limit, incoming_bandwidth, outgoing_bandwidth, enable_crc32,
        maximum_packet_size, maximum_waiting_data);
}

void
amberline_enet_host_destroy (LeagueEnetHost * opaque_host)
{
    if (opaque_host != NULL)
      enet_host_destroy ((ENetHost *) opaque_host);
}

uint16_t
amberline_enet_host_port (const LeagueEnetHost * opaque_host)
{
    if (opaque_host == NULL)
      return 0;
    return ((const ENetHost *) opaque_host) -> address.port;
}

int
amberline_enet_host_connect (LeagueEnetHost * opaque_host,
                          const char * remote_host,
                          uint16_t port,
                          size_t channel_count,
                          uint32_t data,
                          LeagueEnetPeer ** peer_out)
{
    ENetAddress address;
    ENetPeer * peer;

    if (peer_out != NULL)
      * peer_out = NULL;

    if (opaque_host == NULL || remote_host == NULL || peer_out == NULL)
    {
        amberline_enet_set_error ("invalid host, remote host, or peer output");
        return -1;
    }

    memset (& address, 0, sizeof (address));
    address.port = port;
    if (enet_address_set_host (& address, remote_host) < 0)
    {
        amberline_enet_set_error ("could not resolve remote host");
        return -1;
    }

    peer = enet_host_connect ((ENetHost *) opaque_host,
                              & address,
                              channel_count,
                              data);
    if (peer == NULL)
    {
        amberline_enet_set_error ("enet_host_connect failed");
        return -1;
    }

    /* ENet owns this object for the lifetime of the host. The ABI handle is
       deliberately just the stable native peer address; no second wrapper is
       allocated and therefore there is nothing for callers to free. */
    * peer_out = (LeagueEnetPeer *) peer;
    return 0;
}

static void
amberline_enet_copy_event (LeagueEnetEvent * destination, const ENetEvent * source)
{
    destination -> type = (int32_t) source -> type;
    destination -> channel = (int32_t) source -> channelID;
    destination -> data = (uint32_t) source -> data;
    destination -> peer = (uintptr_t) source -> peer;
    destination -> packet = (uintptr_t) source -> packet;
}

static void
amberline_enet_clear_event (LeagueEnetEvent * event)
{
    memset (event, 0, sizeof (* event));
    event -> type = AMBERLINE_ENET_EVENT_NONE;
    event -> channel = -1;
}

int
amberline_enet_host_service (LeagueEnetHost * opaque_host,
                          LeagueEnetEvent * event,
                          uint32_t timeout_ms)
{
    ENetEvent native_event;
    int result;

    if (opaque_host == NULL || event == NULL)
    {
        amberline_enet_set_error ("invalid host or event output");
        return -1;
    }

    amberline_enet_clear_event (event);
    memset (& native_event, 0, sizeof (native_event));
    result = enet_host_service ((ENetHost *) opaque_host, & native_event, timeout_ms);
    if (result < 0)
    {
        amberline_enet_set_error ("enet_host_service failed");
        return -1;
    }
    if (result > 0)
      amberline_enet_copy_event (event, & native_event);
    return result;
}

int
amberline_enet_host_check_events (LeagueEnetHost * opaque_host,
                               LeagueEnetEvent * event)
{
    ENetEvent native_event;
    int result;

    if (opaque_host == NULL || event == NULL)
    {
        amberline_enet_set_error ("invalid host or event output");
        return -1;
    }

    amberline_enet_clear_event (event);
    memset (& native_event, 0, sizeof (native_event));
    result = enet_host_check_events ((ENetHost *) opaque_host, & native_event);
    if (result < 0)
    {
        amberline_enet_set_error ("enet_host_check_events failed");
        return -1;
    }
    if (result > 0)
      amberline_enet_copy_event (event, & native_event);
    return result;
}

void
amberline_enet_host_flush (LeagueEnetHost * opaque_host)
{
    if (opaque_host != NULL)
      enet_host_flush ((ENetHost *) opaque_host);
}

int
amberline_enet_peer_send (LeagueEnetPeer * opaque_peer,
                       uint8_t channel,
                       const uint8_t * data,
                       size_t length,
                       uint32_t flags)
{
    ENetPacket * packet;
    int result;

    if (opaque_peer == NULL || (data == NULL && length != 0))
    {
        amberline_enet_set_error ("invalid peer or packet data");
        return -1;
    }

    packet = enet_packet_create (data, length, flags);
    if (packet == NULL)
    {
        amberline_enet_set_error ("enet_packet_create failed");
        return -1;
    }

    result = enet_peer_send ((ENetPeer *) opaque_peer, channel, packet);
    if (result < 0)
    {
        enet_packet_destroy (packet);
        amberline_enet_set_error ("enet_peer_send failed");
    }
    return result;
}

void
amberline_enet_peer_disconnect (LeagueEnetPeer * opaque_peer, uint32_t data)
{
    if (opaque_peer != NULL)
      enet_peer_disconnect ((ENetPeer *) opaque_peer, data);
}

void
amberline_enet_peer_disconnect_now (LeagueEnetPeer * opaque_peer, uint32_t data)
{
    if (opaque_peer != NULL)
      enet_peer_disconnect_now ((ENetPeer *) opaque_peer, data);
}

void
amberline_enet_peer_disconnect_later (LeagueEnetPeer * opaque_peer, uint32_t data)
{
    if (opaque_peer != NULL)
      enet_peer_disconnect_later ((ENetPeer *) opaque_peer, data);
}

void
amberline_enet_peer_reset (LeagueEnetPeer * opaque_peer)
{
    if (opaque_peer != NULL)
      enet_peer_reset ((ENetPeer *) opaque_peer);
}

void
amberline_enet_peer_ping (LeagueEnetPeer * opaque_peer)
{
    if (opaque_peer != NULL)
      enet_peer_ping ((ENetPeer *) opaque_peer);
}

int
amberline_enet_peer_stats (const LeagueEnetPeer * opaque_peer,
                        LeagueEnetPeerStats * stats)
{
    const ENetPeer * peer;

    if (opaque_peer == NULL || stats == NULL)
    {
        amberline_enet_set_error ("invalid peer or stats output");
        return -1;
    }

    peer = (const ENetPeer *) opaque_peer;
    memset (stats, 0, sizeof (* stats));
    stats -> state = (uint32_t) peer -> state;
    stats -> channel_count = (uint32_t) peer -> channelCount;
    stats -> round_trip_time_ms = peer -> roundTripTime;
    stats -> round_trip_time_variance_ms = peer -> roundTripTimeVariance;
    stats -> packet_loss = peer -> packetLoss;
    stats -> packet_loss_variance = peer -> packetLossVariance;
    stats -> packets_sent = peer -> packetsSent;
    stats -> packets_lost = peer -> packetsLost;
    stats -> incoming_data_total = peer -> incomingDataTotal;
    stats -> outgoing_data_total = peer -> outgoingDataTotal;
    stats -> waiting_data_bytes = peer -> totalWaitingData;
    stats -> address_host = peer -> address.host;
    stats -> address_port = peer -> address.port;
    return 0;
}

size_t
amberline_enet_packet_length (uintptr_t opaque_packet)
{
    if (opaque_packet == 0)
      return 0;
    return ((const ENetPacket *) opaque_packet) -> dataLength;
}

uint32_t
amberline_enet_packet_flags (uintptr_t opaque_packet)
{
    if (opaque_packet == 0)
      return 0;
    return ((const ENetPacket *) opaque_packet) -> flags;
}

int
amberline_enet_packet_copy (uintptr_t opaque_packet,
                         uint8_t * destination,
                         size_t destination_length)
{
    const ENetPacket * packet;

    if (opaque_packet == 0)
    {
        amberline_enet_set_error ("invalid packet");
        return -1;
    }

    packet = (const ENetPacket *) opaque_packet;
    if (packet -> dataLength > destination_length ||
        (packet -> dataLength != 0 && destination == NULL))
    {
        amberline_enet_set_error ("packet destination is too small");
        return -1;
    }

    if (packet -> dataLength != 0)
      memcpy (destination, packet -> data, packet -> dataLength);
    return 0;
}

void
amberline_enet_event_dispose (LeagueEnetEvent * event)
{
    if (event == NULL)
      return;

    if (event -> packet != 0)
      enet_packet_destroy ((ENetPacket *) event -> packet);
    event -> packet = 0;
}
