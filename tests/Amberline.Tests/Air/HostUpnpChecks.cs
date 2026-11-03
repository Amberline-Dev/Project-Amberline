using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amberline.Tests.Air;

/// <summary>
/// The UPnP client against an in-process fake router on loopback only: an SSDP responder on a loopback UDP port and an HTTP
/// description and SOAP endpoint on 127.0.0.1 (and 127.0.0.2, which plays a foreign host). Every search is a unicast M-SEARCH
/// to the fake; nothing goes to the SSDP multicast group or a real router.
/// </summary>
public static class HostUpnpChecks
{
    private static readonly IReadOnlyList<UpnpPort> Ports = UpnpGateway.HostPorts(8391, 5219, 4);

    [Check("server.host-upnp-maps-and-renews")]
    public static void MapsAndRenews() => MapsAndRenewsAsync().GetAwaiter().GetResult();

    private static async Task MapsAndRenewsAsync()
    {
        TestAssert.Equal(new[] { "TCP 8391", "UDP 5219", "UDP 5220", "UDP 5221", "UDP 5222" }, Ports.Select(port => $"{port.Protocol} {port.Port}").ToArray(),
            "the design's ports");
        await using (var router = await FakeRouter.StartAsync())
        {
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("the fake router was not found");
            var searches = router.Snapshot(router.Searches);
            TestAssert.Equal(2, searches.Length, "one unicast search per gateway version");
            foreach (var version in new[] { "2", "1" })
            {
                TestAssert.True(searches.Any(search => search == "M-SEARCH * HTTP/1.1\r\nHOST: " + router.SsdpEndPoint + "\r\nMAN: \"ssdp:discover\"\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:" + version + "\r\n\r\n"), "an exact M-SEARCH for InternetGatewayDevice:" + version);
            }
            TestAssert.Equal(router.Url("127.0.0.1", "/desc.xml"), gateway.Location.AbsoluteUri);
            TestAssert.Equal(router.Url("127.0.0.1", "/ctl/IPConn"), gateway.ControlUrl.AbsoluteUri, "the relative control address resolves");
            TestAssert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", gateway.ServiceType, "the WAN connection, not the Layer3Forwarding service");
            TestAssert.Equal(IPAddress.Loopback, gateway.LocalAddress, "this PC's end of the route to the router");
            TestAssert.Equal("FR-1000", gateway.ModelName);
            TestAssert.Equal(IPAddress.Parse("198.51.100.20"), await gateway.ExternalAddressAsync());

            gateway.RenewEvery = TimeSpan.FromMilliseconds(250);
            TestAssert.Equal<UpnpProblem?>(null, await gateway.MapAsync(Ports));
            var table = router.Table();
            TestAssert.Equal(5, table.Count, "every port mapped");
            foreach (var port in Ports)
            {
                var entry = table[(port.Protocol, port.Port)];
                TestAssert.Equal(("", port.Port.ToString(), "127.0.0.1", "1", "Amberline", "3600"),
                    (entry["NewRemoteHost"], entry["NewInternalPort"], entry["NewInternalClient"], entry["NewEnabled"], entry["NewPortMappingDescription"], entry["NewLeaseDuration"]),
                    $"{port.Protocol} {port.Port}: any remote host, same port, this PC, enabled, named, one-hour lease");
            }
            TestAssert.Equal(5, router.Count("GetSpecificPortMappingEntry"), "each mapping checked");
            TestAssert.False(gateway.PermanentLeases);

            await Until(() => router.Count("AddPortMapping") >= 15, "two renewals");
            TestAssert.Equal<UpnpProblem?>(null, gateway.RenewalProblem);
            await gateway.StopAsync();
            TestAssert.Equal(0, router.Table().Count, "Stop deletes every mapping");
            TestAssert.Equal(5, router.Count("DeletePortMapping"));
            var added = router.Count("AddPortMapping");
            await Task.Delay(800);
            TestAssert.Equal(added, router.Count("AddPortMapping"), "no renewal after Stop");
            foreach (var call in router.Snapshot(router.Calls))
                TestAssert.True(call.StartsWith("127.0.0.1 ", StringComparison.Ordinal), "every request went to the router's own host: " + call);
        }

        // Error 725: the router keeps only permanent mappings; the client falls back to lease 0, never renews, and deletes at Stop.
        await using (var router = await FakeRouter.StartAsync())
        {
            router.Refuse = (action, arguments) => action == "AddPortMapping" && arguments["NewLeaseDuration"] != "0" ? 725 : null;
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("the fake router was not found");
            gateway.RenewEvery = TimeSpan.FromMilliseconds(200);
            TestAssert.Equal<UpnpProblem?>(null, await gateway.MapAsync(Ports));
            TestAssert.True(gateway.PermanentLeases);
            TestAssert.True(router.Table().Values.All(entry => entry["NewLeaseDuration"] == "0"), "permanent mappings");
            TestAssert.Equal(6, router.Count("AddPortMapping"), "one refused try, then five permanent mappings");
            await Task.Delay(700);
            TestAssert.Equal(6, router.Count("AddPortMapping"), "permanent mappings are not renewed");
            await gateway.StopAsync();
            TestAssert.Equal(0, router.Table().Count, "Stop deletes the permanent mappings");
        }
    }

    [Check("server.host-upnp-errors-plain-words")]
    public static void ErrorsInPlainWords() => ErrorsInPlainWordsAsync().GetAwaiter().GetResult();

    private static async Task ErrorsInPlainWordsAsync()
    {
        TestAssert.Equal(new UpnpProblem("A-NET-03", "Your router did not answer. It may have UPnP switched off.",
            "Switch on UPnP in the router (\"Allow programs to open ports\") and try again, or open the ports by hand."), UpnpGateway.NotFound);
        TestAssert.Equal(UpnpGateway.NotFound, UpnpGateway.Explain(0, 8391), "no usable answer is the router-did-not-answer card");
        TestAssert.Equal(new UpnpProblem("A-NET-04", "Your router does not allow programs to open ports.", "Switch on UPnP for this PC, or open them by hand."),
            UpnpGateway.Explain(606, 8391));
        TestAssert.Equal(("A-NET-05", "Another computer in your home already uses port 8391."),
            (UpnpGateway.Explain(718, 8391).Code, UpnpGateway.Explain(718, 8391).Message));
        TestAssert.Equal(("A-NET-04", "Your router has no room for more open ports."), (UpnpGateway.Explain(728, 5219).Code, UpnpGateway.Explain(728, 5219).Message));
        TestAssert.Equal(("A-NET-04", "Your router keeps port 5220 for something else, such as its own port forwarding."),
            (UpnpGateway.Explain(729, 5220).Code, UpnpGateway.Explain(729, 5220).Message));
        TestAssert.Equal(("A-NET-04", "Your router refused to open port 5221 (its error 501)."), (UpnpGateway.Explain(501, 5221).Code, UpnpGateway.Explain(501, 5221).Message));
        foreach (var code in new[] { 0, 606, 718, 725, 728, 729, 501 })
            TestAssert.True(UpnpGateway.Explain(code, 8391).WhatToDo.Length > 0, $"error {code} says what to do");

        // A silent router: the search times out and the page shows the router-did-not-answer card.
        await using (var silent = await FakeRouter.StartAsync())
        {
            silent.Answer = false;
            TestAssert.True(await UpnpGateway.DiscoverAsync(silent.SsdpEndPoint, TimeSpan.FromMilliseconds(800)) is null, "no answer, no gateway");
            TestAssert.Equal(0, silent.Snapshot(silent.Calls).Length);
        }

        // The router's own SOAP faults come back as their cards, naming the refused port.
        foreach (var (code, port, expected) in new[] { (606, 8391, "A-NET-04"), (718, 5220, "A-NET-05"), (728, 5219, "A-NET-04"), (729, 5222, "A-NET-04") })
        {
            await using var router = await FakeRouter.StartAsync();
            router.Refuse = (action, arguments) => action == "AddPortMapping" && arguments["NewExternalPort"] == port.ToString() ? code : null;
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("the fake router was not found");
            var problem = await gateway.MapAsync(Ports);
            TestAssert.Equal(UpnpGateway.Explain(code, port), problem, $"error {code} on port {port}");
            TestAssert.Equal(expected, problem!.Code);
            await gateway.StopAsync();
            TestAssert.Equal(0, router.Table().Count, $"error {code}: Stop deletes the ports mapped before the refusal");
        }

        // The router says yes but its entry names another PC: that port is taken (A-NET-05), and Stop leaves the entry alone.
        await using (var router = await FakeRouter.StartAsync())
        {
            router.Holder = (protocol, port) => port == 5221 ? "192.168.1.77" : null;
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("the fake router was not found");
            TestAssert.Equal(UpnpGateway.Explain(718, 5221), await gateway.MapAsync(Ports), "another PC holds the mapping");
            await gateway.StopAsync();
            TestAssert.Equal(new[] { "UDP 5221" }, router.Table().Keys.Select(key => $"{key.Protocol} {key.Port}").ToArray(), "the other PC's entry stays");
        }

        // The router stops answering its control address: the card is A-NET-03, and the external address is unknown.
        var gone = await FakeRouter.StartAsync();
        var orphan = await UpnpGateway.DiscoverAsync(gone.SsdpEndPoint, TimeSpan.FromSeconds(3)) ?? throw new InvalidOperationException("the fake router was not found");
        await gone.DisposeAsync();
        TestAssert.Equal(UpnpGateway.NotFound, await orphan.MapAsync(Ports));
        TestAssert.True(await orphan.ExternalAddressAsync() is null);
        await orphan.DisposeAsync();
    }

    [Check("server.host-wan-address-classes")]
    public static void WanAddressClasses() => WanAddressClassesAsync().GetAwaiter().GetResult();

    private static async Task WanAddressClassesAsync()
    {
        var cases = new (string? Address, WanAddressClass Class)[]
        {
            ("198.51.100.20", WanAddressClass.Public), ("203.0.113.9", WanAddressClass.Public), ("192.0.2.44", WanAddressClass.Public),
            ("10.0.0.1", WanAddressClass.Private), ("10.255.255.255", WanAddressClass.Private),
            ("172.16.0.1", WanAddressClass.Private), ("172.31.255.254", WanAddressClass.Private), ("172.15.255.255", WanAddressClass.Public), ("172.32.0.1", WanAddressClass.Public),
            ("192.168.0.1", WanAddressClass.Private), ("192.169.0.1", WanAddressClass.Public),
            ("100.64.0.1", WanAddressClass.Shared), ("100.127.255.254", WanAddressClass.Shared), ("100.63.255.255", WanAddressClass.Public), ("100.128.0.1", WanAddressClass.Public),
            ("0.0.0.0", WanAddressClass.None), ("127.0.0.1", WanAddressClass.None), ("169.254.1.1", WanAddressClass.None), ("239.255.255.250", WanAddressClass.None),
            ("2001:db8::1", WanAddressClass.None), (null, WanAddressClass.None),
        };
        foreach (var (address, kind) in cases)
            TestAssert.Equal(kind, WanAddress.Classify(address is null ? null : IPAddress.Parse(address)), address ?? "no address");

        TestAssert.Equal<UpnpProblem?>(null, WanAddress.Problem(WanAddressClass.Public), "a public address needs no card");
        TestAssert.Equal(new UpnpProblem("A-NET-06", "Your router is behind another router (often your provider's box).",
            "Put the provider's box in bridge mode, or open the same ports on both routers, or set this router as the box's \"exposed host\"."),
            WanAddress.Problem(WanAddressClass.Private));
        TestAssert.Equal(new UpnpProblem("A-NET-07", "Your internet provider shares one address among many homes, so friends cannot reach this PC.",
            "Ask your provider for a public IPv4 address (some do it on request), or host on a rented server."),
            WanAddress.Problem(WanAddressClass.Shared));
        TestAssert.Equal("A-NET-03", WanAddress.Problem(WanAddressClass.None)!.Code);

        // From the router's own GetExternalIPAddress answer.
        foreach (var (reported, kind) in new[] { ("100.64.12.34", WanAddressClass.Shared), ("192.168.100.2", WanAddressClass.Private), ("203.0.113.9", WanAddressClass.Public), ("", WanAddressClass.None), ("0.0.0.0", WanAddressClass.None) })
        {
            await using var router = await FakeRouter.StartAsync();
            router.ExternalAddress = reported;
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("the fake router was not found");
            TestAssert.Equal(kind, WanAddress.Classify(await gateway.ExternalAddressAsync()), "router reports '" + reported + "'");
        }
    }

    [Check("server.host-upnp-ignores-foreign-location")]
    public static void IgnoresForeignLocation() => IgnoresForeignLocationAsync().GetAwaiter().GetResult();

    private static async Task IgnoresForeignLocationAsync()
    {
        // After a multicast search: only plain http to a private address on one of this PC's own subnets.
        var lan = new (IPAddress, int)[] { (IPAddress.Parse("192.168.1.10"), 24) };
        TestAssert.True(UpnpGateway.LocationAllowed(new Uri("http://192.168.1.1:49152/rootDesc.xml"), null, lan), "the router on this subnet");
        foreach (var foreign in new[]
                 {
                     "http://192.168.2.1:49152/rootDesc.xml", // another subnet
                     "http://203.0.113.1/rootDesc.xml",       // the internet
                     "http://10.0.0.1/rootDesc.xml",          // private, but not this PC's subnet
                     "http://127.0.0.1:8390/manage",          // this PC's own loopback pages
                     "http://router.lan/rootDesc.xml",        // a name, resolved by someone else
                     "https://192.168.1.1/rootDesc.xml",      // not the architecture's plain http
                     "file://192.168.1.1/rootDesc.xml",
                 })
            TestAssert.False(UpnpGateway.LocationAllowed(new Uri(foreign), null, lan), foreign);
        TestAssert.False(UpnpGateway.LocationAllowed(new Uri("http://203.0.113.1/rootDesc.xml"), null, [(IPAddress.Parse("203.0.113.5"), 24)]),
            "a public subnet of this PC is not a home network");
        TestAssert.False(UpnpGateway.LocationAllowed(new Uri("http://192.168.1.1/rootDesc.xml"), null, []), "no private interface, nothing allowed");
        // After a unicast search: only the searched address.
        TestAssert.True(UpnpGateway.LocationAllowed(new Uri("http://192.168.1.1:5000/desc.xml"), IPAddress.Parse("192.168.1.1"), []));
        TestAssert.False(UpnpGateway.LocationAllowed(new Uri("http://192.168.1.2:5000/desc.xml"), IPAddress.Parse("192.168.1.1"), lan));

        // Live: the foreign answers come first, and none of them is fetched; the router's own answer still wins.
        await using var router = await FakeRouter.StartAsync();
        router.Locations =
        [
            router.Url("127.0.0.2", "/desc.xml"),               // another host than the searched router
            $"http://localhost:{router.HttpPort}/desc.xml",      // a name
            router.Url("127.0.0.1", "/redirect.xml"),           // a redirect to another host is not followed
            router.Url("127.0.0.1", "/foreign-control.xml"),    // its control address points at another host
            router.Url("127.0.0.1", "/desc.xml"),
        ];
        await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3))
            ?? throw new InvalidOperationException("the router's own answer was not used");
        TestAssert.Equal(router.Url("127.0.0.1", "/desc.xml"), gateway.Location.AbsoluteUri);
        TestAssert.Equal(router.Url("127.0.0.1", "/ctl/IPConn"), gateway.ControlUrl.AbsoluteUri);
        TestAssert.Equal(IPAddress.Parse("198.51.100.20"), await gateway.ExternalAddressAsync());
        TestAssert.Equal(new[] { "127.0.0.1 GET /redirect.xml", "127.0.0.1 GET /foreign-control.xml", "127.0.0.1 GET /desc.xml", "127.0.0.1 POST GetExternalIPAddress" },
            router.Snapshot(router.Calls), "only the router's own host was asked, and nothing on the foreign host");

        // Only foreign answers: no gateway, and no request anywhere.
        await using var spoofed = await FakeRouter.StartAsync();
        spoofed.Locations = [spoofed.Url("127.0.0.2", "/desc.xml"), $"http://localhost:{spoofed.HttpPort}/desc.xml"];
        TestAssert.True(await UpnpGateway.DiscoverAsync(spoofed.SsdpEndPoint, TimeSpan.FromMilliseconds(800)) is null, "foreign answers only");
        TestAssert.Equal(0, spoofed.Snapshot(spoofed.Calls).Length);
    }

    internal static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// An IGD:1 router on loopback: answers only the InternetGatewayDevice:1 search, serves a nested device description with a
    /// Layer3Forwarding and a WANIPConnection:1 service, and keeps a port-mapping table behind a SOAP endpoint that checks the
    /// SOAPAction header against the body.
    /// </summary>
    internal sealed class FakeRouter : IAsyncDisposable
    {
        private const string Service = "urn:schemas-upnp-org:service:WANIPConnection:1";
        private const string Device = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        private readonly UdpClient _ssdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<(string Protocol, int Port), Dictionary<string, string>> _table = [];
        private WebApplication? _app;
        private Task? _answering;

        public List<string> Searches { get; } = [];
        public List<string> Calls { get; } = [];
        public int HttpPort { get; private set; }
        public IPEndPoint SsdpEndPoint => (IPEndPoint)_ssdp.Client.LocalEndPoint!;
        public bool Answer { get; set; } = true;
        public List<string> Locations { get; set; } = [];
        public string ExternalAddress { get; set; } = "198.51.100.20";
        public Func<string, IReadOnlyDictionary<string, string>, int?> Refuse { get; set; } = (_, _) => null;
        public Func<string, int, string?> Holder { get; set; } = (_, _) => null;

        public string Url(string host, string path) => $"http://{host}:{HttpPort}{path}";
        public T[] Snapshot<T>(List<T> list) { lock (_table) return [.. list]; }
        public int Count(string action) { lock (_table) return Calls.Count(call => call.EndsWith(" POST " + action, StringComparison.Ordinal)); }
        public Dictionary<(string Protocol, int Port), Dictionary<string, string>> Table() { lock (_table) return new(_table); }

        public static async Task<FakeRouter> StartAsync()
        {
            var router = new FakeRouter();
            router.HttpPort = AirManagementChecks.FreePort();
            router.Locations = [router.Url("127.0.0.1", "/desc.xml")];
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, router.HttpPort);
                options.Listen(IPAddress.Parse("127.0.0.2"), router.HttpPort);
            });
            var app = builder.Build();
            app.Run(router.HandleAsync);
            await app.StartAsync();
            router._app = app;
            router._answering = router.AnswerAsync(router._stop.Token);
            return router;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is null) return;
            await _stop.CancelAsync();
            _ssdp.Dispose();
            await _answering!;
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        private async Task AnswerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var search = await _ssdp.ReceiveAsync(cancellationToken);
                    var text = Encoding.ASCII.GetString(search.Buffer);
                    lock (_table) Searches.Add(text);
                    if (!Answer || !text.StartsWith("M-SEARCH * HTTP/1.1\r\n", StringComparison.Ordinal) ||
                        !text.Contains("\r\nMAN: \"ssdp:discover\"\r\n", StringComparison.Ordinal) || !text.Contains("\r\nST: " + Device + "\r\n", StringComparison.Ordinal))
                        continue;
                    foreach (var location in Locations)
                    {
                        var reply = $"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nEXT:\r\nLOCATION: {location}\r\nSERVER: FakeOS/1.0 UPnP/1.1 FakeRouter/1.0\r\n" +
                                    $"ST: {Device}\r\nUSN: uuid:00000000-0000-0000-0000-000000000001::{Device}\r\n\r\n";
                        await _ssdp.SendAsync(Encoding.ASCII.GetBytes(reply), search.RemoteEndPoint, cancellationToken);
                    }
                }
                catch (SocketException)
                {
                    // Windows reports an ICMP port-unreachable from a client socket that already closed; keep answering.
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task HandleAsync(HttpContext context)
        {
            var host = context.Connection.LocalIpAddress!.ToString();
            var path = context.Request.Path.Value ?? "";
            if (context.Request.Method == "GET")
            {
                lock (_table) Calls.Add($"{host} GET {path}");
                if (path == "/redirect.xml")
                {
                    context.Response.Redirect(Url("127.0.0.2", "/desc.xml"));
                    return;
                }
                var control = path switch { "/desc.xml" => "/ctl/IPConn", "/foreign-control.xml" => Url("127.0.0.2", "/ctl/IPConn"), _ => null };
                if (control is null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                context.Response.ContentType = "text/xml";
                await context.Response.WriteAsync(Description(control));
                return;
            }

            var envelope = XElement.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
            var call = envelope.Descendants().First(element => element.Name.LocalName == "Body").Elements().First();
            var action = call.Name.LocalName;
            lock (_table) Calls.Add($"{host} POST {action}");
            if (call.Name.NamespaceName != Service || context.Request.Headers["SOAPAction"] != $"\"{Service}#{action}\"" ||
                !context.Request.ContentType!.StartsWith("text/xml", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                return;
            }
            var arguments = call.Elements().ToDictionary(element => element.Name.LocalName, element => element.Value);
            var key = arguments.TryGetValue("NewProtocol", out var protocol) ? (protocol, int.Parse(arguments["NewExternalPort"])) : ("", 0);
            var refused = Refuse(action, arguments);
            Dictionary<string, string>? output = null;
            lock (_table)
            {
                if (refused is null)
                {
                    switch (action)
                    {
                        case "AddPortMapping":
                            _table[key] = arguments;
                            output = new();
                            break;
                        case "GetSpecificPortMappingEntry" when _table.TryGetValue(key, out var entry):
                            output = new()
                            {
                                ["NewInternalPort"] = entry["NewInternalPort"], ["NewInternalClient"] = Holder(key.Item1, key.Item2) ?? entry["NewInternalClient"],
                                ["NewEnabled"] = entry["NewEnabled"], ["NewPortMappingDescription"] = entry["NewPortMappingDescription"],
                                ["NewLeaseDuration"] = entry["NewLeaseDuration"],
                            };
                            break;
                        case "DeletePortMapping" when Holder(key.Item1, key.Item2) is null && _table.Remove(key):
                            output = new();
                            break;
                        case "GetExternalIPAddress":
                            output = new() { ["NewExternalIPAddress"] = ExternalAddress };
                            break;
                    }
                    refused = output is null ? 714 : null; // NoSuchEntryInArray
                }
            }
            context.Response.ContentType = "text/xml; charset=\"utf-8\"";
            var body = refused is { } code
                ? $"<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">" +
                  $"<errorCode>{code}</errorCode><errorDescription>refused</errorDescription></UPnPError></detail></s:Fault>"
                : $"<u:{action}Response xmlns:u=\"{Service}\">" + string.Concat(output!.Select(pair => $"<{pair.Key}>{pair.Value}</{pair.Key}>")) + $"</u:{action}Response>";
            if (refused is not null) context.Response.StatusCode = 500;
            await context.Response.WriteAsync("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" + body + "</s:Body></s:Envelope>");
        }

        private static string Description(string control) =>
            "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>" +
            $"<device><deviceType>{Device}</deviceType><friendlyName>Fake router</friendlyName><modelName>FR-1000</modelName>" +
            "<serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><controlURL>/ctl/L3F</controlURL></service></serviceList>" +
            "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType><deviceList><device>" +
            "<deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType><serviceList><service>" +
            $"<serviceType>{Service}</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId><controlURL>{control}</controlURL>" +
            "</service></serviceList></device></deviceList></device></deviceList></device></root>";
    }
}
