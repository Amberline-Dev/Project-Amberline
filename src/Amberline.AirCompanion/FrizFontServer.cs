using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amberline.AirCompanion;

/// <summary>The converted faces and the one log line (<c>friz-font: ...</c>, no path, user or address) that describes them.</summary>
public sealed record FrizFontFaces(byte[]? Regular, byte[]? Bold, string Status);

/// <summary>
/// Per-user cache of the converted Friz fonts. The source is the AIR copy's own <c>css/fonts.swf</c>; the files live in
/// <c>%LOCALAPPDATA%/AmberlineAir/fonts</c> (never in the stage or preview folder, never in a friend package), named
/// by the SHA-256 of the SWF and <see cref="FrizFont.ConverterVersion"/>, so a changed SWF or converter rebuilds them.
/// Writes are atomic (temporary file, then move). The fonts are never sent to another machine.
/// </summary>
public static class FrizFontCache
{
    public static string? DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "AmberlineAir", "fonts");
    }

    /// <summary>Never throws for a missing, unreadable or malformed SWF or an unwritable cache; the status line says what happened.</summary>
    public static FrizFontFaces Load(string airRoot, string? cacheDirectory)
    {
        var swfPath = Path.Combine(airRoot, "css", "fonts.swf");
        try
        {
            if (!File.Exists(swfPath)) return Unavailable("fonts.swf not found");
            if (new FileInfo(swfPath).Length > 32L * 1024 * 1024) return Unavailable("fonts.swf is too large");
            var swf = File.ReadAllBytes(swfPath);
            var hash = Convert.ToHexString(SHA256.HashData(swf))[..8].ToLowerInvariant();
            var suffix = string.Create(CultureInfo.InvariantCulture, $"-{hash}-v{FrizFont.ConverterVersion}.ttf");
            string? regularFile = null, boldFile = null;
            if (!string.IsNullOrEmpty(cacheDirectory))
            {
                regularFile = Path.Combine(cacheDirectory, "friz" + suffix);
                boldFile = Path.Combine(cacheDirectory, "friz-bold" + suffix);
                if (TryRead(regularFile) is { } cachedRegular && TryRead(boldFile) is { } cachedBold)
                    return new FrizFontFaces(cachedRegular, cachedBold, "friz-font: cached");
            }

            if (!FrizFont.TryConvert(swf, out var regular, out var bold, out var report)) return Unavailable(report);
            if (regularFile is not null && boldFile is not null && bold.Length > 0)
                Persist(cacheDirectory!, regularFile, regular, boldFile, bold);
            return new FrizFontFaces(regular, bold.Length == 0 ? null : bold, "friz-font: " + report);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Unavailable("fonts.swf cannot be read (" + exception.GetType().Name + ")"); }
    }

    private static FrizFontFaces Unavailable(string reason) => new(null, null, "friz-font: unavailable (" + reason + ")");

    /// <summary>A cached file is trusted only when it still looks like a TrueType font (0x00010000 header).</summary>
    private static byte[]? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            return bytes.Length > 12 && bytes[0] == 0 && bytes[1] == 1 && bytes[2] == 0 && bytes[3] == 0 ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Persist(string directory, string regularFile, byte[] regular, string boldFile, byte[] bold)
    {
        try
        {
            Directory.CreateDirectory(directory);
            WriteAtomic(regularFile, regular);
            WriteAtomic(boldFile, bold);
            // Older SWF hashes or converter versions are dead weight.
            foreach (var old in Directory.EnumerateFiles(directory, "friz*-v*.ttf"))
                if (!string.Equals(old, regularFile, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(old, boldFile, StringComparison.OrdinalIgnoreCase))
                    File.Delete(old);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { /* The fonts are still served from memory; the cache is only a speed-up. */ }
    }

    private static void WriteAtomic(string file, byte[] bytes)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>
/// Serves the locally converted Friz faces to the AIR pages from <c>http://127.0.0.1:8396</c>, loopback only. The fonts
/// come from the user's own client files and are never served by the Platform or sent to another machine; the pages
/// reference <c>/fonts/friz.ttf</c> and <c>/fonts/friz-bold.ttf</c> in <c>@font-face</c> with an Arial fallback.
/// Exactly those two GET (or HEAD) paths exist; everything else is 404 and other verbs are 405. Starting never throws:
/// a missing font, a taken port or any other failure only means the pages use their fallback font.
/// </summary>
public sealed class FrizFontServer : IAsyncDisposable
{
    public const int Port = 8396;
    public const string RegularPath = "/fonts/friz.ttf";
    public const string BoldPath = "/fonts/friz-bold.ttf";

    private readonly WebApplication? app;
    private readonly FrizFontFaces faces;

    private FrizFontServer(WebApplication? app, FrizFontFaces faces, int port)
    {
        this.app = app;
        this.faces = faces;
        ListeningPort = app is null ? 0 : port;
    }

    /// <summary>The loopback port in use, or 0 when the server is not listening.</summary>
    public int ListeningPort { get; }

    public static async Task<FrizFontServer> StartAsync(string airRoot, Action<string>? log = null, int port = Port,
        string? cacheDirectory = null)
    {
        log ??= _ => { };
        var faces = FrizFontCache.Load(airRoot, cacheDirectory ?? FrizFontCache.DefaultDirectory());
        log(faces.Status);
        WebApplication? built = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 1024;
                options.Limits.MaxConcurrentConnections = 16;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback, port);
            });
            built = builder.Build();
            var server = new FrizFontServer(built, faces, port);
            built.Run(server.HandleAsync);
            await built.StartAsync().ConfigureAwait(false);
            return server;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The launch is never blocked by the font: most often a second companion already holds the port.
            log("friz-font: not serving (" + (exception is IOException ? "port busy" : exception.GetType().Name) + ")");
            if (built is not null) await built.DisposeAsync().ConfigureAwait(false);
            return new FrizFontServer(null, faces, port);
        }
    }

    private Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        // DNS rebinding: a remote site can point its own name at 127.0.0.1, after which this server is same-origin to that site and
        // the page could read the font bytes (a redistribution). The Host header still names the remote site, so anything but this
        // listener's own loopback name is refused first, with no body and no other header.
        if (!IsOwnHost(request.Host))
        {
            response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return Task.CompletedTask;
        }
        response.Headers.XContentTypeOptions = "nosniff";
        var body = request.Path.Value switch
        {
            RegularPath => faces.Regular,
            BoldPath => faces.Bold,
            _ => null,
        };
        if (body is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        var head = HttpMethods.IsHead(request.Method);
        if (!head && !HttpMethods.IsGet(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = "GET, HEAD";
            return Task.CompletedTask;
        }
        response.ContentType = "font/ttf";
        response.ContentLength = body.Length;
        response.Headers.CacheControl = "public, max-age=86400";
        response.Headers.Vary = "Origin";
        if (IsLocalOrigin(request.Headers.Origin.ToString()))
            response.Headers.AccessControlAllowOrigin = request.Headers.Origin.ToString();
        return head ? Task.CompletedTask : response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    /// <summary>Host "127.0.0.1" or "localhost", with this listener's port or no port; anything else (also an IPv6 or a look-alike name) is not ours.</summary>
    private bool IsOwnHost(HostString host) =>
        host.HasValue && (host.Host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) &&
        (host.Port is null || host.Port == ListeningPort);

    /// <summary>An <c>http</c> origin (no path, user or query) whose host is 127.0.0.1 or localhost, on any port.</summary>
    public static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host is "127.0.0.1" or "localhost" && uri.UserInfo.Length == 0 &&
        origin == uri.GetLeftPart(UriPartial.Authority);

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(grace.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }
}
