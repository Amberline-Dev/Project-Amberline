using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Amberline.Platform;

/// <summary>What the router's own external address says about the line to the internet (Amberline Host design, section 5.3).</summary>
public enum WanAddressClass
{
    /// <summary>No usable address: none reported, 0.0.0.0, loopback, link-local or multicast.</summary>
    None,
    /// <summary>A public address: the normal case, friends can reach the router.</summary>
    Public,
    /// <summary>A private address (RFC 1918): the router sits behind a second router, often the provider's box (double NAT).</summary>
    Private,
    /// <summary>The Shared Address Space 100.64.0.0/10 of RFC 6598: the provider's carrier-grade NAT, friends cannot reach this PC.</summary>
    Shared,
}

/// <summary>A problem card for the host page: the short code for an issue report, what happened and what to do.</summary>
public sealed record UpnpProblem(string Code, string Message, string WhatToDo);

/// <summary>One port the router forwards to the same port on this PC; <see cref="Protocol"/> is "TCP" or "UDP".</summary>
public readonly record struct UpnpPort(string Protocol, int Port);

/// <summary>The address classes of the Amberline Host design, section 5.3, from the router's own answer (no third party).</summary>
public static class WanAddress
{
    private static readonly (System.Net.IPNetwork Network, WanAddressClass Class)[] Ranges =
    [
        (System.Net.IPNetwork.Parse("10.0.0.0/8"), WanAddressClass.Private),
        (System.Net.IPNetwork.Parse("172.16.0.0/12"), WanAddressClass.Private),
        (System.Net.IPNetwork.Parse("192.168.0.0/16"), WanAddressClass.Private),
        (System.Net.IPNetwork.Parse("100.64.0.0/10"), WanAddressClass.Shared),
        (System.Net.IPNetwork.Parse("0.0.0.0/8"), WanAddressClass.None),
        (System.Net.IPNetwork.Parse("127.0.0.0/8"), WanAddressClass.None),
        (System.Net.IPNetwork.Parse("169.254.0.0/16"), WanAddressClass.None),
        (System.Net.IPNetwork.Parse("224.0.0.0/3"), WanAddressClass.None),
    ];

    /// <summary>Only IPv4 counts: the WAN connection services report the router's IPv4 address.</summary>
    public static WanAddressClass Classify(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return WanAddressClass.None;
        foreach (var (network, kind) in Ranges)
            if (network.Contains(address)) return kind;
        return WanAddressClass.Public;
    }

    /// <summary>The page's card for an address class; none for a public address.</summary>
    public static UpnpProblem? Problem(WanAddressClass kind) => kind switch
    {
        WanAddressClass.Public => null,
        WanAddressClass.Private => new("A-NET-06", "Your router is behind another router (often your provider's box).",
            "Put the provider's box in bridge mode, or open the same ports on both routers, or set this router as the box's \"exposed host\"."),
        WanAddressClass.Shared => new("A-NET-07", "Your internet provider shares one address among many homes, so players cannot reach this PC.",
            "Ask your provider for a public IPv4 address (some do it on request), or host on a rented server."),
        // Reconstruction: the design names no code for a router without an address; it is the router-did-not-help case.
        _ => new("A-NET-03", "Your router answered but has no internet address right now.",
            "Check that the router is connected to the internet, then try again."),
    };
}

/// <summary>
/// NOT RETAIL: retail ran its servers on public addresses; a home host needs its router to forward the sign-in and game ports.
/// An own UPnP Internet Gateway Device client (Amberline Host design, section 5.1), standard library only. It follows, by name:
/// UPnP Device Architecture 1.1, Discovery ("Search request with M-SEARCH", "Search response"), Description ("Device
/// description") and Control ("Action invocation", "Action response"); and the IGD services WANIPConnection:2, WANIPConnection:1
/// and WANPPPConnection:1 (the actions AddPortMapping, GetSpecificPortMappingEntry, DeletePortMapping and GetExternalIPAddress,
/// with their error tables). Leases last an hour and are renewed every half hour; error 725 (only permanent leases) falls back
/// to a permanent mapping. <see cref="StopAsync"/> deletes every mapping this instance made. Map and Stop are not meant to run
/// at the same time.
/// </summary>
public sealed class UpnpGateway : IAsyncDisposable
{
    public const string MappingDescription = "Amberline";
    public const int LeaseSeconds = 3600;

    private static readonly IPEndPoint SsdpGroup = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] DeviceTypes =
        ["urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:device:InternetGatewayDevice:1"];
    private static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>No router answered the search, or the router stopped answering its control address.</summary>
    public static readonly UpnpProblem NotFound = new("A-NET-03", "Your router did not answer. It may have UPnP switched off.",
        "Switch on UPnP in the router (\"Allow programs to open ports\") and try again, or open the ports by hand.");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<UpnpPort> _added = [];
    private IReadOnlyList<UpnpPort> _wanted = [];
    private int _lease = LeaseSeconds;
    private CancellationTokenSource? _renewal;
    private Task? _renewing;

    private UpnpGateway(HttpClient http, Uri location, Uri controlUrl, string serviceType, IPAddress localAddress, string? modelName)
    {
        _http = http;
        Location = location;
        ControlUrl = controlUrl;
        ServiceType = serviceType;
        LocalAddress = localAddress;
        ModelName = modelName;
    }

    /// <summary>The device description the router announced.</summary>
    public Uri Location { get; }
    /// <summary>The WAN connection service's control address, always on the description's own host.</summary>
    public Uri ControlUrl { get; }
    public string ServiceType { get; }
    /// <summary>This PC's address on the router's interface: the local end of a socket aimed at the router.</summary>
    public IPAddress LocalAddress { get; }
    /// <summary>The router's model name from its description, for the router guide; null when it gives none.</summary>
    public string? ModelName { get; }
    /// <summary>The router keeps only permanent mappings (error 725); nothing is renewed and Stop deletes them.</summary>
    public bool PermanentLeases => _lease == 0;
    /// <summary>Half the lease; the checks shorten it to watch a renewal.</summary>
    public TimeSpan RenewEvery { get; set; } = TimeSpan.FromSeconds(LeaseSeconds / 2);
    /// <summary>The last renewal's problem, or null when the last renewal mapped every port.</summary>
    public UpnpProblem? RenewalProblem { get; private set; }

    /// <summary>The design's ports: the public sign-in port over TCP and one UDP port per match.</summary>
    public static IReadOnlyList<UpnpPort> HostPorts(int remotePort, int firstGamePort, int games) =>
        [new("TCP", remotePort), .. Enumerable.Range(firstGamePort, games).Select(port => new UpnpPort("UDP", port))];

    /// <summary>
    /// Searches for the router and opens the first answer whose description has a WAN connection service. Without
    /// <paramref name="router"/> the search goes to the SSDP multicast group from each private address of this PC, and a
    /// description is fetched only from a private address on one of this PC's own subnets. With it, the search is a unicast
    /// M-SEARCH to that one address, and only a description on that address is fetched. Null when nothing usable answered
    /// within <paramref name="wait"/> (3 seconds by default): the page shows <see cref="NotFound"/>.
    /// </summary>
    public static async Task<UpnpGateway?> DiscoverAsync(IPEndPoint? router = null, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var networks = router is null ? LocalNetworks() : [];
        if (router is null && networks.Count == 0) return null;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        if (router is null)
        {
            // The device architecture's default TTL for a search is 2.
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            foreach (var (address, _) in networks)
            {
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                foreach (var type in DeviceTypes) await udp.SendAsync(Search(SsdpGroup, type, multicast: true), SsdpGroup, cancellationToken);
            }
        }
        else
        {
            foreach (var type in DeviceTypes) await udp.SendAsync(Search(router, type, multicast: false), router, cancellationToken);
        }

        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 1 << 20,
        };
        UpnpGateway? found = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(wait ?? TimeSpan.FromSeconds(3));
        try
        {
            while (found is null)
            {
                UdpReceiveResult reply;
                try
                {
                    reply = await udp.ReceiveAsync(window.Token);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // Windows reports an ICMP port-unreachable for an earlier search as a reset; other answers may follow
                }
                var location = LocationOf(reply.Buffer);
                if (location is null || !seen.Add(location.AbsoluteUri) || !LocationAllowed(location, router?.Address, networks)) continue;
                found = await OpenAsync(http, location, cancellationToken);
            }
            return found;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (found is null) http.Dispose();
        }
    }

    /// <summary>
    /// Whether a search answer's description address may be fetched: plain http to an IPv4 literal that is the searched
    /// <paramref name="router"/>, or, after a multicast search, a private address on one of <paramref name="networks"/>.
    /// A spoofed answer can therefore never make this PC fetch from the internet, a name, or this PC's own loopback pages.
    /// </summary>
    public static bool LocationAllowed(Uri location, IPAddress? router, IReadOnlyList<(IPAddress Address, int PrefixLength)> networks)
    {
        if (location.Scheme != Uri.UriSchemeHttp || location.HostNameType != UriHostNameType.IPv4 ||
            !IPAddress.TryParse(location.Host, out var host)) return false;
        if (router is not null) return host.Equals(router);
        return WanAddress.Classify(host) == WanAddressClass.Private &&
            networks.Any(network => SameSubnet(host, network.Address, network.PrefixLength));
    }

    /// <summary>This PC's private IPv4 addresses on interfaces that are up, with their prefix lengths (loopback excluded).</summary>
    public static IReadOnlyList<(IPAddress Address, int PrefixLength)> LocalNetworks() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => WanAddress.Classify(unicast.Address) == WanAddressClass.Private)
            .Select(unicast => (unicast.Address, unicast.PrefixLength)),
    ];

    /// <summary>The router's external address from GetExternalIPAddress; null when it does not answer or gives none.</summary>
    public async Task<IPAddress?> ExternalAddressAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await SoapAsync("GetExternalIPAddress", [], cancellationToken);
            return reply.TryGetValue("NewExternalIPAddress", out var text) && IPAddress.TryParse(text, out var address) ? address : null;
        }
        catch (UpnpFault)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps each port to the same port on <see cref="LocalAddress"/> and checks it with GetSpecificPortMappingEntry; stops at the
    /// first problem. While mapped, every port is added again each <see cref="RenewEvery"/>, which also restores mappings a
    /// rebooted router lost; a renewal's problem shows in <see cref="RenewalProblem"/>. Null when every port is mapped. A port this
    /// instance mapped before that <paramref name="ports"/> no longer holds is deleted first (batch 684: fewer games at the same time).
    /// </summary>
    public async Task<UpnpProblem?> MapAsync(IReadOnlyList<UpnpPort> ports, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _wanted = ports;
            await DeleteAsync(_added.Except(ports).ToArray(), cancellationToken);
            var problem = await AddAllAsync(cancellationToken);
            if (_renewing is null && _lease > 0)
            {
                _renewal = new CancellationTokenSource();
                _renewing = RenewAsync(_renewal.Token);
            }
            return problem;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the renewal and deletes every mapping this instance made; a mapping the router already dropped is skipped.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_renewal is not null)
        {
            await _renewal.CancelAsync();
            await _renewing!;
            _renewal.Dispose();
            _renewal = null;
            _renewing = null;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DeleteAsync(_added.ToArray(), cancellationToken);
            _wanted = [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes these mappings of this instance; a mapping the router already dropped is skipped.</summary>
    private async Task DeleteAsync(IReadOnlyList<UpnpPort> ports, CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            try
            {
                await SoapAsync("DeletePortMapping", Key(port), cancellationToken);
            }
            catch (UpnpFault)
            {
            }
            _added.Remove(port);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>The plain-words card for a router error code on <paramref name="port"/>; code 0 means no usable answer.</summary>
    public static UpnpProblem Explain(int errorCode, int port) => errorCode switch
    {
        0 => NotFound,
        606 => new("A-NET-04", "Your router does not allow programs to open ports.", "Switch on UPnP for this PC, or open them by hand."),
        718 => new("A-NET-05", $"Another computer in your home already uses port {port}.",
            "Remove that port forwarding on the router's page or stop the program on the other computer, then try again."),
        728 => new("A-NET-04", "Your router has no room for more open ports.",
            "Remove old port forwardings on the router's page, or open the ports by hand."),
        729 => new("A-NET-04", $"Your router keeps port {port} for something else, such as its own port forwarding.",
            "Open the ports by hand on the router's page."),
        _ => new("A-NET-04", $"Your router refused to open port {port} (its error {errorCode}).",
            "Open the ports by hand on the router's page."),
    };

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RenewEvery);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_lease > 0) RenewalProblem = await AddAllAsync(cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<UpnpProblem?> AddAllAsync(CancellationToken cancellationToken)
    {
        foreach (var port in _wanted)
        {
            if (await AddAsync(port, cancellationToken) is { } problem) return problem;
        }
        return null;
    }

    private async Task<UpnpProblem?> AddAsync(UpnpPort port, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await SoapAsync("AddPortMapping", AddArguments(port), cancellationToken);
            }
            catch (UpnpFault fault) when (fault.Code == 725 && _lease != 0)
            {
                _lease = 0;
                await SoapAsync("AddPortMapping", AddArguments(port), cancellationToken);
            }
        }
        catch (UpnpFault fault)
        {
            return Explain(fault.Code, port.Port);
        }
        try
        {
            var entry = await SoapAsync("GetSpecificPortMappingEntry", Key(port), cancellationToken);
            // The router kept another PC's entry: that one is not ours to renew or delete.
            if (entry.TryGetValue("NewInternalClient", out var client) && IPAddress.TryParse(client, out var holder) && !holder.Equals(LocalAddress))
                return Explain(718, port.Port);
        }
        catch (UpnpFault)
        {
            // A router without this action: its AddPortMapping answer stands.
        }
        if (!_added.Contains(port)) _added.Add(port);
        return null;
    }

    private static (string, string)[] Key(UpnpPort port) =>
        [("NewRemoteHost", ""), ("NewExternalPort", port.Port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", port.Protocol)];

    private (string, string)[] AddArguments(UpnpPort port) =>
    [
        .. Key(port),
        ("NewInternalPort", port.Port.ToString(CultureInfo.InvariantCulture)),
        ("NewInternalClient", LocalAddress.ToString()),
        ("NewEnabled", "1"),
        ("NewPortMappingDescription", MappingDescription),
        ("NewLeaseDuration", _lease.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>One SOAP action: the reply's out arguments by name, or a <see cref="UpnpFault"/> with the router's error code.</summary>
    private async Task<Dictionary<string, string>> SoapAsync(string action, (string Name, string Value)[] arguments, CancellationToken cancellationToken)
    {
        var body = new StringBuilder("<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:").Append(action)
            .Append(" xmlns:u=\"").Append(ServiceType).Append("\">");
        foreach (var (name, value) in arguments)
            body.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        using var content = new StringContent(body.ToString(), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        using var request = new HttpRequestMessage(HttpMethod.Post, ControlUrl) { Content = content };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ServiceType}#{action}\"");
        HttpStatusCode status;
        XElement reply;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            reply = ParseXml(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception error) when (error is HttpRequestException or XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpnpFault(0, error.Message);
        }
        // A fault is HTTP 500 with a UPnPError detail (errorCode, errorDescription).
        var code = reply.Descendants().FirstOrDefault(element => element.Name.LocalName == "errorCode")?.Value.Trim();
        if (status != HttpStatusCode.OK || code is not null)
            throw new UpnpFault(int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0, $"{action}: HTTP {(int)status}");
        var result = reply.Descendants().FirstOrDefault(element => element.Name.LocalName == action + "Response")
            ?? throw new UpnpFault(0, $"{action}: no response element");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in result.Elements()) values[element.Name.LocalName] = element.Value.Trim();
        return values;
    }

    private static async Task<UpnpGateway?> OpenAsync(HttpClient http, Uri location, CancellationToken cancellationToken)
    {
        XElement root;
        try
        {
            using var response = await http.GetAsync(location, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            root = ParseXml(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception error) when (error is HttpRequestException or XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var services = root.Descendants().Where(element => element.Name.LocalName == "service")
            .Select(service => (Type: Child(service, "serviceType"), Control: Child(service, "controlURL"))).ToArray();
        var type = ServiceTypes.FirstOrDefault(wanted => services.Any(service => service.Type == wanted && service.Control is not null));
        if (type is null) return null;
        // URLBase is optional (deprecated in architecture 1.1); without it, relative addresses resolve against the description's.
        var baseAddress = Uri.TryCreate(Child(root, "URLBase"), UriKind.Absolute, out var declared) ? declared : location;
        if (!Uri.TryCreate(baseAddress, services.First(service => service.Type == type).Control, out var control) ||
            control.Scheme != Uri.UriSchemeHttp || control.Host != location.Host) return null;
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(IPAddress.Parse(control.Host), control.Port); // a UDP connect sends nothing; it only picks the route
        var model = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "modelName")?.Value.Trim();
        return new UpnpGateway(http, location, control, type, ((IPEndPoint)probe.LocalEndPoint!).Address, string.IsNullOrEmpty(model) ? null : model);
    }

    private static string? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();

    /// <summary>Router XML with document type definitions refused, so a hostile answer cannot expand entities.</summary>
    private static XElement ParseXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XElement.Load(reader);
    }

    private static Uri? LocationOf(byte[] datagram)
    {
        var lines = Encoding.ASCII.GetString(datagram).Split('\n', StringSplitOptions.TrimEntries);
        if (!lines[0].StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return null;
        foreach (var line in lines)
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var location))
                return location;
        }
        return null;
    }

    /// <summary>An M-SEARCH; MX (the answer spread in seconds) belongs to a multicast search only.</summary>
    private static byte[] Search(IPEndPoint target, string type, bool multicast) => Encoding.ASCII.GetBytes(
        $"M-SEARCH * HTTP/1.1\r\nHOST: {target}\r\nMAN: \"ssdp:discover\"\r\n{(multicast ? "MX: 2\r\n" : "")}ST: {type}\r\n\r\n");

    private static bool SameSubnet(IPAddress candidate, IPAddress local, int prefixLength)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32) return false;
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (Bits(candidate) & mask) == (Bits(local) & mask);
        static uint Bits(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private sealed class UpnpFault(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
