using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

/// <summary>
/// The local ITC Friz Quadrata pipeline: SWF DefineFont3 to TrueType converter, per-user cache and the loopback-only
/// server. A tiny synthetic SWF is built here, so no client file is read; real-data validation lives outside the repo.
/// </summary>
public static class AirFrizFontChecks
{
    // ------------------------------------------------------------------ converter

    [Check("server.air-friz-font-converter-roundtrip")]
    public static void ConvertsSyntheticDefineFont3ToValidTrueType()
    {
        var swf = BuildSwf();
        TestAssert.True(FrizFont.TryConvert(swf, out var regular, out var bold, out var report), report);
        TestAssert.Equal("converted 2 faces, 10 glyphs", report);

        var again = FrizFont.TryConvert(swf, out var regular2, out var bold2, out _);
        TestAssert.True(again && regular.AsSpan().SequenceEqual(regular2) && bold.AsSpan().SequenceEqual(bold2),
            "conversion must be deterministic");

        // A zlib-compressed (CWS) copy of the same SWF converts to the same bytes.
        TestAssert.True(FrizFont.TryConvert(Compress(swf), out var packed, out _, out report), report);
        TestAssert.True(packed.AsSpan().SequenceEqual(regular), "CWS and FWS must give the same font");

        var font = new Ttf(regular);
        font.AssertContainer();
        TestAssert.Equal(6, font.NumGlyphs, ".notdef plus five SWF glyphs");
        TestAssert.Equal(2048, font.U16(font.Offset("head") + 18), "unitsPerEm");
        TestAssert.Equal(0, font.U16(font.Offset("head") + 50), "short loca");
        TestAssert.Equal((font.NumGlyphs + 1) * 2, font.Length("loca"), "loca length matches indexToLocFormat");

        TestAssert.Equal(1, font.Cmap('A'));
        TestAssert.Equal(2, font.Cmap('B'));
        TestAssert.Equal(3, font.Cmap('C'));
        TestAssert.Equal(4, font.Cmap('D'));
        TestAssert.Equal(5, font.Cmap(' '));
        TestAssert.Equal(0, font.Cmap('E'), "an unmapped code gives .notdef");

        var a = font.Glyph(1)!;
        TestAssert.Equal(2, a.Ends.Length, "A has an outer contour and a hole");
        TestAssert.Equal(8, a.Points.Count, "closing duplicates are dropped");
        TestAssert.Equal(3, a.Ends[0]);
        TestAssert.Equal(7, a.Ends[1]);
        TestAssert.True(a.Points.All(point => point.On), "straight edges are on-curve points");
        TestAssert.Equal((100, 0, 900, 1000), (a.XMin, a.YMin, a.XMax, a.YMax), "SWF units / 10, y flipped up");
        TestAssert.True(a.Area(0) < 0, "outer contour is clockwise");
        TestAssert.True(a.Area(1) > 0, "hole is counter-clockwise");

        foreach (var reversedGlyph in new[] { font.Glyph(2)!, font.Glyph(4)! })
        {
            TestAssert.Equal(8, reversedGlyph.Points.Count);
            TestAssert.True(reversedGlyph.Area(0) < 0, "outer is normalized to clockwise");
            TestAssert.True(reversedGlyph.Area(1) > 0, "hole stays opposite to the outer");
        }

        var c = font.Glyph(3)!;
        TestAssert.Equal(1, c.Ends.Length);
        TestAssert.Equal(8, c.Points.Count);
        TestAssert.Equal(4, c.Points.Count(point => point.On), "quadratic control points stay off-curve");
        TestAssert.True(c.Area(0) < 0, "a counter-clockwise SWF contour is reversed to clockwise");

        TestAssert.True(font.Glyph(5) is null, "an empty glyph has a zero-length loca entry");
        TestAssert.Equal(1000, font.Advance(1));
        TestAssert.Equal(100, font.Lsb(1));
        TestAssert.Equal(1100, font.Advance(3));
        TestAssert.Equal(500, font.Advance(5));
        TestAssert.Equal(1952, font.I16(font.Offset("hhea") + 4), "ascent from the SWF layout");
        TestAssert.Equal(-504, font.I16(font.Offset("hhea") + 6));
        TestAssert.Equal(170, font.I16(font.Offset("hhea") + 8));
        TestAssert.Equal(6, font.U16(font.Offset("hhea") + 34), "numberOfHMetrics");
        TestAssert.Equal(400, font.U16(font.Offset("OS/2") + 4));
        TestAssert.Equal(96, font.Length("OS/2"));
        TestAssert.Equal(0, font.U16(font.Offset("head") + 44), "macStyle regular");
        TestAssert.Equal("ITC Friz Quadrata", font.Name(3, 1, 1));
        TestAssert.Equal("ITC Friz Quadrata", font.Name(1, 0, 1), "Macintosh Roman family record");
        TestAssert.Equal("Regular", font.Name(3, 1, 2));
        TestAssert.Equal("ITCFrizQuadrata-Regular", font.Name(3, 1, 6));

        var boldFont = new Ttf(bold);
        boldFont.AssertContainer();
        TestAssert.Equal(700, boldFont.U16(boldFont.Offset("OS/2") + 4));
        TestAssert.Equal(1, boldFont.U16(boldFont.Offset("head") + 44), "macStyle bold");
        TestAssert.Equal("Bold", boldFont.Name(3, 1, 2));
        TestAssert.Equal("ITC Friz Quadrata Bold", boldFont.Name(3, 1, 4));
        TestAssert.Equal(1020, boldFont.Advance(1), "the bold face keeps its own advances");
    }

    [Check("server.air-friz-font-rejects-malformed-swf")]
    public static void MalformedSwfEndsCleanly()
    {
        TestAssert.Throws<InvalidDataException>(() => FrizFont.ReadFonts([]));
        TestAssert.Throws<InvalidDataException>(() => FrizFont.ReadFonts(Encoding.ASCII.GetBytes("not a swf file at all")));
        TestAssert.Throws<InvalidDataException>(() => FrizFont.ReadFonts(
            [(byte)'Z', (byte)'W', (byte)'S', 10, 20, 0, 0, 0, 1, 2, 3, 4, 5, 6]), "LZMA is rejected, not guessed");
        var broken = new byte[40];
        "CWS"u8.CopyTo(broken);
        TestAssert.Throws<InvalidDataException>(() => FrizFont.ReadFonts(broken), "a corrupt zlib stream");

        TestAssert.False(FrizFont.TryConvert([], out var regular, out var bold, out var report));
        TestAssert.True(regular.Length == 0 && bold.Length == 0 && report.Length > 0);

        // Only a Helvetica face: nothing to convert.
        TestAssert.False(FrizFont.TryConvert(BuildSwf(withFriz: false), out _, out _, out report));
        TestAssert.True(report.Contains("no regular ITC Friz Quadrata face", StringComparison.Ordinal), report);

        // Every truncation and a fixed-seed burst of byte flips must give true or false, never an exception.
        var swf = BuildSwf();
        for (var length = 0; length < swf.Length; length++)
            FrizFont.TryConvert(swf[..length], out _, out _, out _);
        var random = new Random(126);
        for (var round = 0; round < 3000; round++)
        {
            var mutated = (byte[])swf.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
                mutated[random.Next(8, mutated.Length)] = (byte)random.Next(256);
            FrizFont.TryConvert(mutated, out _, out _, out report);
            TestAssert.False(report.Contains(Path.DirectorySeparatorChar), "reports never carry paths");
        }
        TestAssert.False(FrizFont.TryConvert(Path.Combine(Path.GetTempPath(), "league-no-such-" + Guid.NewGuid().ToString("N"), "fonts.swf"),
            out _, out _, out report));
        TestAssert.Equal("fonts.swf not found", report);
    }

    // ------------------------------------------------------------------ cache

    [Check("server.air-friz-font-cache")]
    public static void CachesPerSwfHashAndReportsPathFreeStatus()
    {
        var root = TemporaryDirectory();
        try
        {
            var airRoot = Path.Combine(root, "air");
            var cache = Path.Combine(root, "cache");
            WriteFontsSwf(airRoot, BuildSwf());

            var first = FrizFontCache.Load(airRoot, cache);
            TestAssert.Equal("friz-font: converted 2 faces, 10 glyphs", first.Status);
            TestAssert.True(first.Regular is not null && first.Bold is not null);
            var files = Directory.GetFiles(cache).Select(Path.GetFileName).ToArray();
            TestAssert.Equal(2, files.Length);
            TestAssert.Equal(1, files.Count(file => file!.StartsWith("friz-bold-", StringComparison.Ordinal) && file.EndsWith("-v1.ttf", StringComparison.Ordinal)));
            TestAssert.Equal(1, files.Count(file => file!.StartsWith("friz-", StringComparison.Ordinal) && !file.StartsWith("friz-bold-", StringComparison.Ordinal) &&
                file.EndsWith("-v1.ttf", StringComparison.Ordinal)));
            TestAssert.False(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length > 0, "no temporary file is left behind");
            TestAssert.False(Directory.GetFiles(airRoot, "*.ttf", SearchOption.AllDirectories).Length > 0, "nothing is written into the AIR copy");

            var second = FrizFontCache.Load(airRoot, cache);
            TestAssert.Equal("friz-font: cached", second.Status);
            TestAssert.True(second.Regular!.AsSpan().SequenceEqual(first.Regular) && second.Bold!.AsSpan().SequenceEqual(first.Bold));

            // A different SWF hash rebuilds the files and removes the stale ones.
            WriteFontsSwf(airRoot, BuildSwf(extraAdvance: 40));
            var third = FrizFontCache.Load(airRoot, cache);
            TestAssert.Equal("friz-font: converted 2 faces, 10 glyphs", third.Status);
            TestAssert.False(third.Regular!.AsSpan().SequenceEqual(first.Regular));
            var after = Directory.GetFiles(cache).Select(Path.GetFileName).ToHashSet();
            TestAssert.Equal(2, after.Count);
            TestAssert.False(after.Overlaps(files), "files of the old SWF are deleted");

            // A damaged cache file is rebuilt instead of served.
            File.WriteAllBytes(Directory.GetFiles(cache, "friz-bold-*")[0], [1, 2, 3]);
            TestAssert.True(FrizFontCache.Load(airRoot, cache).Status.StartsWith("friz-font: converted", StringComparison.Ordinal));

            // No cache directory at all still converts.
            TestAssert.True(FrizFontCache.Load(airRoot, null).Regular is not null);

            File.Delete(Path.Combine(airRoot, "css", "fonts.swf"));
            var missing = FrizFontCache.Load(airRoot, cache);
            TestAssert.Equal("friz-font: unavailable (fonts.swf not found)", missing.Status);
            TestAssert.True(missing.Regular is null && missing.Bold is null);

            File.WriteAllBytes(Path.Combine(airRoot, "css", "fonts.swf"), "garbage"u8.ToArray());
            var malformed = FrizFontCache.Load(airRoot, cache);
            TestAssert.True(malformed.Status.StartsWith("friz-font: unavailable (", StringComparison.Ordinal), malformed.Status);
            TestAssert.False(malformed.Status.Contains(root, StringComparison.OrdinalIgnoreCase), "status lines carry no path");
            TestAssert.False(malformed.Status.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "status lines carry no user name");

            var home = FrizFontCache.DefaultDirectory();
            TestAssert.True(home is null || (home.EndsWith(Path.Combine("AmberlineAir", "fonts"), StringComparison.Ordinal) &&
                home.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ server

    [Check("server.air-friz-font-server-allow-list-and-cors")]
    public static void ServesExactlyTheTwoFontPathsOnLoopback() => ServeAsync().GetAwaiter().GetResult();

    [Check("server.air-friz-font-server-refuses-a-rebinding-host")]
    public static void FontServerAnswersOnlyItsOwnLoopbackHost() => FontServerAnswersOnlyItsOwnLoopbackHostAsync().GetAwaiter().GetResult();

    private static async Task FontServerAnswersOnlyItsOwnLoopbackHostAsync()
    {
        var root = TemporaryDirectory();
        try
        {
            var airRoot = Path.Combine(root, "air");
            WriteFontsSwf(airRoot, BuildSwf());
            var port = FreePort();
            await using var server = await FrizFontServer.StartAsync(airRoot, _ => { }, port, Path.Combine(root, "cache"));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5), BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            async Task<HttpResponseMessage> Get(string? host)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/fonts/friz.ttf");
                if (host is not null) request.Headers.Host = host;
                return await client.SendAsync(request);
            }
            // The server's own names are served, with or without the port.
            foreach (var host in new[] { $"127.0.0.1:{port}", "127.0.0.1", $"localhost:{port}", "LOCALHOST" })
            {
                using var response = await Get(host);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, host);
            }
            // A site whose name was rebound to 127.0.0.1 sends its own name as Host: no font bytes, no body, no font headers.
            foreach (var host in new[] { $"evil.example:{port}", "evil.example", $"127.0.0.1.evil.example:{port}", $"localhost.evil.example:{port}",
                $"127.0.0.2:{port}", $"[::1]:{port}", $"127.0.0.1:{port + 1}", $"localhost:{port + 1}" })
            {
                using var response = await Get(host);
                TestAssert.True(response.StatusCode is HttpStatusCode.MisdirectedRequest or HttpStatusCode.NotFound or HttpStatusCode.BadRequest, host + " got " + response.StatusCode);
                TestAssert.Equal(0, (await response.Content.ReadAsByteArrayAsync()).Length, host + " must get no body");
                TestAssert.False(response.Content.Headers.ContentType?.MediaType == "font/ttf", host);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-friz-font-server-degrades-quietly")]
    public static void MissingFontOrBusyPortNeverThrows() => DegradeAsync().GetAwaiter().GetResult();

    private static async Task ServeAsync()
    {
        var root = TemporaryDirectory();
        try
        {
            var airRoot = Path.Combine(root, "air");
            WriteFontsSwf(airRoot, BuildSwf());
            TestAssert.True(FrizFont.TryConvert(Path.Combine(airRoot, "css", "fonts.swf"), out var regular, out var bold, out _));
            var port = FreePort();
            var log = new List<string>();
            await using var server = await FrizFontServer.StartAsync(airRoot, log.Add, port, Path.Combine(root, "cache"));
            TestAssert.Equal(port, server.ListeningPort);
            TestAssert.Equal(["friz-font: converted 2 faces, 10 glyphs"], log.ToArray());
            TestAssert.Equal(8396, FrizFontServer.Port);
            TestAssert.Equal("/fonts/friz.ttf", FrizFontServer.RegularPath);
            TestAssert.Equal("/fonts/friz-bold.ttf", FrizFontServer.BoldPath);

            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(endpoint => endpoint.Port == port).ToArray();
            TestAssert.True(listeners.Length > 0 && listeners.All(endpoint => IPAddress.IsLoopback(endpoint.Address) &&
                !endpoint.Address.Equals(IPAddress.IPv6Loopback)), "bound to 127.0.0.1 only: " + string.Join(", ", listeners.Select(endpoint => endpoint.ToString())));

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5), BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            foreach (var (path, expected) in new[] { ("/fonts/friz.ttf", regular), ("/fonts/friz-bold.ttf", bold) })
            {
                using var response = await client.GetAsync(path);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, path);
                TestAssert.Equal("font/ttf", response.Content.Headers.ContentType!.MediaType);
                TestAssert.Equal("public, max-age=86400", response.Headers.CacheControl!.ToString());
                TestAssert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
                TestAssert.False(response.Headers.Contains("Access-Control-Allow-Origin"), "no Origin, no CORS header");
                TestAssert.True((await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(expected), "the served bytes are the converted font");
                TestAssert.True(response.Headers.TryGetValues("Server", out _) == false, "no Server header");
            }

            using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/fonts/friz.ttf")))
            {
                TestAssert.Equal(HttpStatusCode.OK, head.StatusCode);
                TestAssert.Equal(regular.Length, (int)head.Content.Headers.ContentLength!.Value);
                TestAssert.Equal(0, (await head.Content.ReadAsByteArrayAsync()).Length);
            }
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                using var response = await client.SendAsync(new HttpRequestMessage(method, "/fonts/friz.ttf"));
                TestAssert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode, method.Method);
                TestAssert.Equal("GET, HEAD", string.Join(", ", response.Content.Headers.Allow));
            }
            foreach (var path in new[] { "/", "/fonts", "/fonts/", "/fonts/../x", "/fonts/friz.ttf/", "/fonts/FRIZ.ttf", "/friz.ttf",
                "/fonts/friz.ttf;a=1", "/fonts/%2e%2e/friz.ttf", "/fonts/friz-bold.ttf.bak", "/fonts/other.ttf", "/health" })
            {
                using var response = await client.GetAsync(path);
                TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, path);
            }
            using (var post = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/nothing")))
                TestAssert.Equal(HttpStatusCode.NotFound, post.StatusCode, "an unknown path is 404 for every verb");

            foreach (var origin in new[] { "http://127.0.0.1:8390", "http://localhost:3000", "http://127.0.0.1", "http://localhost" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/fonts/friz.ttf");
                request.Headers.Add("Origin", origin);
                using var response = await client.SendAsync(request);
                TestAssert.Equal(origin, string.Join(",", response.Headers.GetValues("Access-Control-Allow-Origin")), origin);
                TestAssert.Equal("Origin", string.Join(",", response.Headers.GetValues("Vary")));
            }
            foreach (var origin in new[] { "http://evil.example", "http://127.0.0.1.evil.example", "https://127.0.0.1", "null", "http://user@127.0.0.1",
                "http://127.0.0.1/path", "http://localhost.evil.example:80", "ftp://127.0.0.1", "*" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/fonts/friz.ttf");
                request.Headers.TryAddWithoutValidation("Origin", origin);
                using var response = await client.SendAsync(request);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, origin);
                TestAssert.False(response.Headers.Contains("Access-Control-Allow-Origin"), "no CORS header for " + origin);
                TestAssert.False(FrizFontServer.IsLocalOrigin(origin), origin);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task DegradeAsync()
    {
        var root = TemporaryDirectory();
        try
        {
            // No fonts.swf: the server still answers, with 404, and says why on one path-free line.
            var port = FreePort();
            var log = new List<string>();
            await using (var server = await FrizFontServer.StartAsync(Path.Combine(root, "air"), log.Add, port, Path.Combine(root, "cache")))
            {
                TestAssert.Equal(port, server.ListeningPort);
                TestAssert.Equal(["friz-font: unavailable (fonts.swf not found)"], log.ToArray());
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5), BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                foreach (var path in new[] { "/fonts/friz.ttf", "/fonts/friz-bold.ttf" })
                {
                    using var response = await client.GetAsync(path);
                    TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, path);
                }
            }

            // A port held by someone else (a second companion): one line, no exception, no listener.
            var airRoot = Path.Combine(root, "air2");
            WriteFontsSwf(airRoot, BuildSwf());
            using var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
            log.Clear();
            await using var second = await FrizFontServer.StartAsync(airRoot, log.Add, busy, Path.Combine(root, "cache2"));
            TestAssert.Equal(0, second.ListeningPort);
            TestAssert.Equal(2, log.Count, "the font status plus one line for the busy port");
            TestAssert.Equal("friz-font: not serving (port busy)", log[1]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ helpers

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "league-friz-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFontsSwf(string airRoot, byte[] swf)
    {
        Directory.CreateDirectory(Path.Combine(airRoot, "css"));
        File.WriteAllBytes(Path.Combine(airRoot, "css", "fonts.swf"), swf);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // ------------------------------------------------------------------ synthetic SWF

    /// <summary>Bit-packed SHAPE of one glyph (SWF units, y down): style changes, general lines and quadratic curves.</summary>
    private sealed class ShapeWriter
    {
        private readonly List<byte> bytes = [];
        private int accumulator;
        private int used;

        public ShapeWriter() { Bits(1, 4); Bits(0, 4); } // one fill style bit, no line style bits

        private void Bits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                accumulator = (accumulator << 1) | ((value >> i) & 1);
                if (++used == 8) { bytes.Add((byte)accumulator); accumulator = 0; used = 0; }
            }
        }

        private static int Width(int value)
        {
            var magnitude = value < 0 ? ~value : value;
            var width = 1;
            while (magnitude != 0) { width++; magnitude >>= 1; }
            return width;
        }

        private void Signed(int value, int count) => Bits(value & ((1 << count) - 1), count);

        public ShapeWriter Move(int x, int y, int? fill0, int? fill1)
        {
            Bits(0, 1);
            Bits(1 | (fill0 is null ? 0 : 2) | (fill1 is null ? 0 : 4), 5);
            var width = Math.Max(Width(x), Width(y));
            Bits(width, 5); Signed(x, width); Signed(y, width);
            if (fill0 is { } zero) Bits(zero, 1);
            if (fill1 is { } one) Bits(one, 1);
            return this;
        }

        public ShapeWriter Line(int dx, int dy)
        {
            var width = Math.Max(2, Math.Max(Width(dx), Width(dy)));
            Bits(1, 1); Bits(1, 1); Bits(width - 2, 4); Bits(1, 1); Signed(dx, width); Signed(dy, width);
            return this;
        }

        public ShapeWriter Curve(int controlDx, int controlDy, int anchorDx, int anchorDy)
        {
            var width = Math.Max(2, new[] { controlDx, controlDy, anchorDx, anchorDy }.Max(Width));
            Bits(1, 1); Bits(0, 1); Bits(width - 2, 4);
            Signed(controlDx, width); Signed(controlDy, width); Signed(anchorDx, width); Signed(anchorDy, width);
            return this;
        }

        public byte[] End()
        {
            Bits(0, 1); Bits(0, 5);
            while (used != 0) Bits(0, 1);
            return bytes.ToArray();
        }
    }

    private sealed record SyntheticGlyph(int Code, int Advance, byte[] Shape);

    /// <summary>The five test glyphs. A: square with a hole. B: the same with both contours reversed. C: a curved blob drawn counter-clockwise.
    /// D: hole drawn in the outer's direction but filled on the left (style 0), so the converter must reverse it. Space: empty.</summary>
    private static List<SyntheticGlyph> Glyphs(int extraAdvance) =>
    [
        new('A', 10000 + extraAdvance, new ShapeWriter()
            .Move(1000, 0, null, 1).Line(0, -10000).Line(8000, 0).Line(0, 10000).Line(-8000, 0)
            .Move(3000, -2000, null, null).Line(4000, 0).Line(0, -6000).Line(-4000, 0).Line(0, 6000).End()),
        new('B', 10000 + extraAdvance, new ShapeWriter()
            .Move(1000, 0, null, 1).Line(8000, 0).Line(0, -10000).Line(-8000, 0).Line(0, 10000)
            .Move(3000, -2000, null, null).Line(0, -6000).Line(4000, 0).Line(0, 6000).Line(-4000, 0).End()),
        new('C', 11000 + extraAdvance, new ShapeWriter()
            .Move(5000, 0, null, 1)
            .Curve(5000, 0, 0, -5000).Curve(0, -5000, -5000, 0).Curve(-5000, 0, 0, 5000).Curve(0, 5000, 5000, 0).End()),
        new('D', 10000 + extraAdvance, new ShapeWriter()
            .Move(1000, 0, null, 1).Line(0, -10000).Line(8000, 0).Line(0, 10000).Line(-8000, 0)
            .Move(3000, -8000, 1, 0).Line(4000, 0).Line(0, 6000).Line(-4000, 0).Line(0, -6000).End()),
        new(' ', 5000 + extraAdvance, new ShapeWriter().End()),
    ];

    private static byte[] BuildDefineFont3(int fontId, bool bold, string internalName, int extraAdvance)
    {
        var glyphs = Glyphs(extraAdvance);
        var output = new MemoryStream();
        void U8(int value) => output.WriteByte((byte)value);
        void U16(int value) { U8(value); U8(value >> 8); }
        void U32(int value) { U16(value); U16(value >> 16); }

        U16(fontId);
        U8(0x80 | 0x08 | 0x04 | (bold ? 1 : 0)); // layout, wide offsets, wide codes
        U8(0);
        var name = Encoding.ASCII.GetBytes(internalName + "\0");
        U8(name.Length);
        output.Write(name);
        U16(glyphs.Count);
        var tableSize = glyphs.Count * 4 + 4;
        var offset = tableSize;
        foreach (var glyph in glyphs) { U32(offset); offset += glyph.Shape.Length; }
        U32(offset); // code table offset
        foreach (var glyph in glyphs) output.Write(glyph.Shape);
        foreach (var glyph in glyphs) U16(glyph.Code);
        U16(19520); U16(5040); U16(1700); // ascent, descent, leading
        foreach (var glyph in glyphs) U16(glyph.Advance);
        foreach (var _ in glyphs) U8(0); // empty bounds RECTs (5 zero bits, padded)
        U16(0); // no kerning
        return output.ToArray();
    }

    private static byte[] Tag(int code, byte[] payload)
    {
        var output = new MemoryStream();
        var header = (code << 6) | 0x3F;
        output.WriteByte((byte)header); output.WriteByte((byte)(header >> 8));
        var length = BitConverter.GetBytes(payload.Length);
        output.Write(length);
        output.Write(payload);
        return output.ToArray();
    }

    private static byte[] FontName(int fontId, string name)
    {
        var bytes = new List<byte> { (byte)fontId, (byte)(fontId >> 8) };
        bytes.AddRange(Encoding.UTF8.GetBytes(name + "\0"));
        bytes.AddRange(Encoding.UTF8.GetBytes("Copyright test\0"));
        return bytes.ToArray();
    }

    private static byte[] BuildSwf(bool withFriz = true, int extraAdvance = 0)
    {
        var body = new MemoryStream();
        body.WriteByte(0); // RECT with zero bits
        body.Write(new byte[] { 0, 12, 1, 0 }); // frame rate, frame count
        if (withFriz)
        {
            body.Write(Tag(75, BuildDefineFont3(1, bold: false, "ApplicationFont2", extraAdvance)));
            body.Write(Tag(88, FontName(1, "ITC Friz Quadrata")));
            body.Write(Tag(75, BuildDefineFont3(2, bold: true, "ApplicationFont2", extraAdvance + 200)));
            body.Write(Tag(88, FontName(2, "ITC Friz Quadrata")));
        }
        body.Write(Tag(75, BuildDefineFont3(3, bold: false, "ApplicationFont1", 0)));
        body.Write(Tag(88, FontName(3, "Helvetica LT Std")));
        body.Write(new byte[] { 0, 0 }); // End tag
        var swf = new MemoryStream();
        swf.Write("FWS"u8);
        swf.WriteByte(10);
        swf.Write(BitConverter.GetBytes((int)body.Length + 8));
        swf.Write(body.ToArray());
        return swf.ToArray();
    }

    private static byte[] Compress(byte[] swf)
    {
        var output = new MemoryStream();
        output.Write(swf.AsSpan(0, 8));
        output.GetBuffer()[0] = (byte)'C';
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(swf.AsSpan(8));
        return output.ToArray();
    }

    // ------------------------------------------------------------------ minimal TrueType reader

    private sealed class TtGlyph(int[] ends, List<(int X, int Y, bool On)> points)
    {
        public int[] Ends { get; } = ends;
        public List<(int X, int Y, bool On)> Points { get; } = points;
        public int XMin => Points.Min(p => p.X);
        public int YMin => Points.Min(p => p.Y);
        public int XMax => Points.Max(p => p.X);
        public int YMax => Points.Max(p => p.Y);

        public long Area(int contour)
        {
            var start = contour == 0 ? 0 : Ends[contour - 1] + 1;
            var contourPoints = Points.Skip(start).Take(Ends[contour] - start + 1).ToList();
            long area = 0;
            for (var i = 0; i < contourPoints.Count; i++)
            {
                var a = contourPoints[i];
                var b = contourPoints[(i + 1) % contourPoints.Count];
                area += (long)a.X * b.Y - (long)b.X * a.Y;
            }
            return area;
        }
    }

    private sealed class Ttf
    {
        private readonly byte[] data;
        private readonly Dictionary<string, (int Offset, int Length, uint Sum)> tables = [];
        private readonly List<string> order = [];

        public Ttf(byte[] data)
        {
            this.data = data;
            TestAssert.Equal(0x00010000u, U32(0), "sfnt version");
            var count = U16(4);
            for (var i = 0; i < count; i++)
            {
                var record = 12 + i * 16;
                var tag = Encoding.ASCII.GetString(data, record, 4);
                order.Add(tag);
                tables[tag] = ((int)U32(record + 8), (int)U32(record + 12), U32(record + 4));
            }
        }

        public int Offset(string tag) => tables[tag].Offset;
        public int Length(string tag) => tables[tag].Length;
        public int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        public int I16(int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
        public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        public int NumGlyphs => U16(Offset("maxp") + 4);

        private static uint Sum(ReadOnlySpan<byte> bytes)
        {
            uint sum = 0;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                uint word = 0;
                for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < bytes.Length ? bytes[i + j] : (uint)0);
                sum += word;
            }
            return sum;
        }

        /// <summary>Directory shape, alignment, sorted tags, per-table checksums and the whole-font checkSumAdjustment.</summary>
        public void AssertContainer()
        {
            var count = order.Count;
            var power = 1;
            var selector = 0;
            while (power * 2 <= count) { power *= 2; selector++; }
            TestAssert.Equal(power * 16, U16(6), "searchRange");
            TestAssert.Equal(selector, U16(8), "entrySelector");
            TestAssert.Equal(count * 16 - power * 16, U16(10), "rangeShift");
            TestAssert.Equal(order.Order(StringComparer.Ordinal).ToList(), order, "tags are sorted");
            foreach (var tag in new[] { "OS/2", "cmap", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post" })
                TestAssert.True(tables.ContainsKey(tag), "missing table " + tag);
            foreach (var (tag, (offset, length, sum)) in tables)
            {
                TestAssert.Equal(0, offset % 4, tag + " is 4-byte aligned");
                var bytes = data.AsSpan(offset, length).ToArray();
                if (tag == "head") Array.Clear(bytes, 8, 4);
                TestAssert.Equal(sum, Sum(bytes), "checksum of " + tag);
            }
            TestAssert.Equal(0xB1B0AFBAu, Sum(data), "whole-font checksum with checkSumAdjustment");
            TestAssert.Equal(0x5F0F3CF5u, U32(Offset("head") + 12), "head magic");
            // cmap: (3,1) format 4 with consistent search parameters and a 0xFFFF terminator.
            var cmap = Offset("cmap");
            var subtable = cmap + (int)U32(cmap + 4 + 8 + 4);
            TestAssert.Equal(3, U16(cmap + 4 + 8));
            TestAssert.Equal(1, U16(cmap + 4 + 8 + 2));
            TestAssert.Equal(4, U16(subtable));
            var segments = U16(subtable + 6) / 2;
            var segPower = 1;
            var segSelector = 0;
            while (segPower * 2 <= segments) { segPower *= 2; segSelector++; }
            TestAssert.Equal(segPower * 2, U16(subtable + 8), "cmap searchRange");
            TestAssert.Equal(segSelector, U16(subtable + 10), "cmap entrySelector");
            TestAssert.Equal(segments * 2 - segPower * 2, U16(subtable + 12), "cmap rangeShift");
            TestAssert.Equal(0xFFFF, U16(subtable + 14 + (segments - 1) * 2), "cmap terminator segment");
            TestAssert.Equal(16 + segments * 8, U16(subtable + 2), "cmap length");
        }

        public int Cmap(int code)
        {
            var cmap = Offset("cmap");
            var subtable = cmap + (int)U32(cmap + 4 + 8 + 4);
            var segments = U16(subtable + 6) / 2;
            for (var i = 0; i < segments; i++)
            {
                var end = U16(subtable + 14 + i * 2);
                var start = U16(subtable + 16 + segments * 2 + i * 2);
                if (code < start || code > end) continue;
                var delta = U16(subtable + 16 + segments * 4 + i * 2);
                TestAssert.Equal(0, U16(subtable + 16 + segments * 6 + i * 2), "idRangeOffset");
                return (code + delta) & 0xFFFF;
            }
            return 0;
        }

        private int GlyphOffset(int glyph, out int length)
        {
            var loca = Offset("loca");
            var format = U16(Offset("head") + 50);
            int At(int index) => format == 0 ? U16(loca + index * 2) * 2 : (int)U32(loca + index * 4);
            length = At(glyph + 1) - At(glyph);
            return Offset("glyf") + At(glyph);
        }

        public TtGlyph? Glyph(int glyph)
        {
            var start = GlyphOffset(glyph, out var length);
            if (length == 0) return null;
            var contours = I16(start);
            TestAssert.True(contours > 0, "simple glyph");
            var ends = Enumerable.Range(0, contours).Select(i => U16(start + 10 + i * 2)).ToArray();
            var count = ends[^1] + 1;
            var cursor = start + 10 + contours * 2;
            cursor += 2 + U16(cursor); // instructions
            var flags = new List<byte>();
            while (flags.Count < count)
            {
                var flag = data[cursor++];
                flags.Add(flag);
                if ((flag & 8) != 0) for (var repeat = data[cursor++]; repeat > 0; repeat--) flags.Add(flag);
            }
            var xs = new int[count];
            var ys = new int[count];
            int value = 0;
            for (var i = 0; i < count; i++)
            {
                var flag = flags[i];
                if ((flag & 2) != 0) { var delta = data[cursor++]; value += (flag & 0x10) != 0 ? delta : -delta; }
                else if ((flag & 0x10) == 0) { value += I16(cursor); cursor += 2; }
                xs[i] = value;
            }
            value = 0;
            for (var i = 0; i < count; i++)
            {
                var flag = flags[i];
                if ((flag & 4) != 0) { var delta = data[cursor++]; value += (flag & 0x20) != 0 ? delta : -delta; }
                else if ((flag & 0x20) == 0) { value += I16(cursor); cursor += 2; }
                ys[i] = value;
            }
            TestAssert.True(cursor - start <= length, "glyph data stays inside its loca slot");
            return new TtGlyph(ends, Enumerable.Range(0, count).Select(i => (xs[i], ys[i], (flags[i] & 1) != 0)).ToList());
        }

        public int Advance(int glyph) => U16(Offset("hmtx") + glyph * 4);
        public int Lsb(int glyph) => I16(Offset("hmtx") + glyph * 4 + 2);

        public string Name(int platform, int encoding, int id)
        {
            var name = Offset("name");
            var count = U16(name + 2);
            var storage = name + U16(name + 4);
            var previous = (-1, -1, -1, -1);
            for (var i = 0; i < count; i++)
            {
                var record = name + 6 + i * 12;
                var key = (U16(record), U16(record + 2), U16(record + 4), U16(record + 6));
                TestAssert.True(string.CompareOrdinal(Key(previous), Key(key)) <= 0, "name records are sorted");
                previous = key;
                if (key.Item1 != platform || key.Item2 != encoding || key.Item4 != id) continue;
                var text = data.AsSpan(storage + U16(record + 10), U16(record + 8));
                return platform == 3 ? Encoding.BigEndianUnicode.GetString(text) : Encoding.Latin1.GetString(text);
            }
            throw new InvalidOperationException($"name record {platform}/{encoding}/{id} is missing");
        }

        private static string Key((int, int, int, int) key) => $"{key.Item1:D5}{key.Item2:D5}{key.Item3:D5}{key.Item4:D5}";
    }
}
