# Third-party notices

This project vendors the RTMP/AMF source carried by [Draven-AIR](https://github.com/Maufeat/Draven-AIR).
The original `rtmp-sharp` upstream is [hinaria/rtmp-sharp](https://github.com/hinaria/rtmp-sharp).

The vendored source is pinned to Draven-AIR commit `7472b9ce21e04ce536b8b9f067c3d5c583f543a7`.

Local modifications target .NET 10 and retain the prior async server lifecycle,
connection cleanup, explicit acknowledgement/reply and correlated error control,
disconnect signaling, and sanitized operational logging. This checkout also
keeps the compatibility service bounded at the RTMP packet, AMF collection and
local session boundaries. The RTMP/AMF source is covered by the vendored MIT
license in `LICENSE`.
