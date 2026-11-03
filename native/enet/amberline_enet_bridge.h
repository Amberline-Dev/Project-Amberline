#ifndef AMBERLINE_ENET_BRIDGE_H
#define AMBERLINE_ENET_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(AMBERLINE_ENET_BRIDGE_BUILD)
#define AMBERLINE_ENET_API __declspec(dllexport)
#else
#define AMBERLINE_ENET_API
#endif

typedef struct LeagueEnetHost LeagueEnetHost;
typedef struct LeagueEnetPeer LeagueEnetPeer;

enum
{
    AMBERLINE_ENET_EVENT_NONE = 0,
    AMBERLINE_ENET_EVENT_CONNECT = 1,
    AMBERLINE_ENET_EVENT_DISCONNECT = 2,
    AMBERLINE_ENET_EVENT_RECEIVE = 3
};

enum
{
    AMBERLINE_ENET_PACKET_RELIABLE = 1u << 0,
    AMBERLINE_ENET_PACKET_UNSEQUENCED = 1u << 1,
    AMBERLINE_ENET_PACKET_UNRELIABLE_FRAGMENT = 1u << 3
};

typedef struct LeagueEnetEvent
{
    int32_t type;
    int32_t channel;
    uint32_t data;
    uintptr_t peer;
    uintptr_t packet;
} LeagueEnetEvent;

typedef struct LeagueEnetPeerStats
{
    uint32_t state;
    uint32_t channel_count;
    uint32_t round_trip_time_ms;
    uint32_t round_trip_time_variance_ms;
    uint32_t packet_loss;
    uint32_t packet_loss_variance;
    uint32_t packets_sent;
    uint32_t packets_lost;
    uint32_t incoming_data_total;
    uint32_t outgoing_data_total;
    size_t waiting_data_bytes;
    uint32_t address_host;
    uint16_t address_port;
    uint16_t reserved;
} LeagueEnetPeerStats;

AMBERLINE_ENET_API int amberline_enet_initialize(void);
AMBERLINE_ENET_API void amberline_enet_deinitialize(void);
AMBERLINE_ENET_API uint32_t amberline_enet_linked_version(void);
AMBERLINE_ENET_API const char *amberline_enet_last_error(void);

AMBERLINE_ENET_API LeagueEnetHost *amberline_enet_host_create(
    const char *bind_host,
    uint16_t port,
    size_t peer_limit,
    size_t channel_limit,
    uint32_t incoming_bandwidth,
    uint32_t outgoing_bandwidth,
    int enable_crc32);
/* Bounded creation is a distinct capability so an old bridge cannot silently
   accept a newer call while retaining ENet's unbounded defaults. */
AMBERLINE_ENET_API LeagueEnetHost *amberline_enet_host_create_bounded(
    const char *bind_host,
    uint16_t port,
    size_t peer_limit,
    size_t channel_limit,
    uint32_t incoming_bandwidth,
    uint32_t outgoing_bandwidth,
    int enable_crc32,
    size_t maximum_packet_size,
    size_t maximum_waiting_data);
AMBERLINE_ENET_API void amberline_enet_host_destroy(LeagueEnetHost *host);
AMBERLINE_ENET_API uint16_t amberline_enet_host_port(const LeagueEnetHost *host);
AMBERLINE_ENET_API int amberline_enet_host_connect(
    LeagueEnetHost *host,
    const char *remote_host,
    uint16_t port,
    size_t channel_count,
    uint32_t data,
    LeagueEnetPeer **peer_out);
AMBERLINE_ENET_API int amberline_enet_host_service(
    LeagueEnetHost *host,
    LeagueEnetEvent *event,
    uint32_t timeout_ms);
AMBERLINE_ENET_API int amberline_enet_host_check_events(
    LeagueEnetHost *host,
    LeagueEnetEvent *event);
AMBERLINE_ENET_API void amberline_enet_host_flush(LeagueEnetHost *host);

AMBERLINE_ENET_API int amberline_enet_peer_send(
    LeagueEnetPeer *peer,
    uint8_t channel,
    const uint8_t *data,
    size_t length,
    uint32_t flags);
AMBERLINE_ENET_API void amberline_enet_peer_disconnect(LeagueEnetPeer *peer, uint32_t data);
AMBERLINE_ENET_API void amberline_enet_peer_disconnect_now(LeagueEnetPeer *peer, uint32_t data);
AMBERLINE_ENET_API void amberline_enet_peer_disconnect_later(LeagueEnetPeer *peer, uint32_t data);
AMBERLINE_ENET_API void amberline_enet_peer_reset(LeagueEnetPeer *peer);
AMBERLINE_ENET_API void amberline_enet_peer_ping(LeagueEnetPeer *peer);
AMBERLINE_ENET_API int amberline_enet_peer_stats(
    const LeagueEnetPeer *peer,
    LeagueEnetPeerStats *stats);

AMBERLINE_ENET_API size_t amberline_enet_packet_length(uintptr_t packet);
AMBERLINE_ENET_API uint32_t amberline_enet_packet_flags(uintptr_t packet);
AMBERLINE_ENET_API int amberline_enet_packet_copy(
    uintptr_t packet,
    uint8_t *destination,
    size_t destination_length);
AMBERLINE_ENET_API void amberline_enet_event_dispose(LeagueEnetEvent *event);

#ifdef __cplusplus
}
#endif

#endif
